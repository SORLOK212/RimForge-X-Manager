using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RimForge.Steam.Assistant;

/// <summary>
/// RimForge's own neural network for the assistant, run on the player's computer: the llama.cpp server
/// (MIT licence, Vulkan build: uses the graphics card when it can, otherwise the processor) and the Qwen3 4B
/// Instruct model (Apache 2.0, ≈ 2.4 GB). Downloaded only when the player asks, checked by SHA-256,
/// started when the assistant needs it and stopped when idle or when RimForge closes.
/// </summary>
public sealed class LocalModel(string dir) : IDisposable
{
    public const string RuntimeUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b11181/llama-b11181-bin-win-vulkan-x64.zip";
    private const string RuntimeSha256 = "e2f84f99359be043722cd11ba4388ddddf2eac072032924a1ee84164890d57b9";
    private const long RuntimeBytes = 32_457_115;

    public const string ModelUrl =
        "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/a06e946bb6b655725eafa393f4a9745d460374c9/Qwen3-4B-Instruct-2507-Q4_K_M.gguf";
    private const string ModelSha256 = "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597";
    public const long ModelBytes = 2_497_281_120;

    public const string ModelName = "Qwen3 4B Instruct";
    public static long ApproxBytes => RuntimeBytes + ModelBytes;

    private const string ModelFile = "model.gguf";
    private const string RuntimeDir = "runtime";
    private const string ServerExe = "llama-server.exe";
    private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _server;
    private int _port;
    private Timer? _idleTimer;
    private DateTime _lastUseUtc;
    private IntPtr _job;

    public string Dir { get; } = dir;
    private string ModelPath => Path.Combine(Dir, ModelFile);
    private string ServerPath => Path.Combine(Dir, RuntimeDir, ServerExe);

    public bool IsInstalled => File.Exists(ServerPath) && File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelBytes;
    public bool IsRunning => _server is { HasExited: false };

    public long InstalledBytes => Directory.Exists(Dir)
        ? new DirectoryInfo(Dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        : 0;

    // ───────────────────────── Download ─────────────────────────

    /// <summary>
    /// Fetches the server and the model (an interrupted download continues), checks both by SHA-256 and unpacks the server.
    /// Progress goes from 0 to 1 over the whole download.
    /// </summary>
    public async Task DownloadAsync(HttpClient http, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var total = (double)(RuntimeBytes + ModelBytes);

        if (!File.Exists(ServerPath))
        {
            var zip = Path.Combine(Dir, "runtime.zip");
            await FetchAsync(http, RuntimeUrl, zip, RuntimeBytes, RuntimeSha256, b => progress?.Report(b / total), ct);
            var target = Path.Combine(Dir, RuntimeDir);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, target), ct);
            File.Delete(zip);
        }
        if (!File.Exists(ModelPath) || new FileInfo(ModelPath).Length != ModelBytes)
            await FetchAsync(http, ModelUrl, ModelPath, ModelBytes, ModelSha256, b => progress?.Report((RuntimeBytes + b) / total), ct);
        progress?.Report(1);
    }

    /// <summary>Downloads into "<paramref name="target"/>.part" (continuing it), checks the hash, then renames.</summary>
    private static async Task FetchAsync(HttpClient http, string url, string target, long size, string sha256, Action<long> progress, CancellationToken ct)
    {
        var part = target + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > size) { File.Delete(part); have = 0; }
        if (have < size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0; // the server ignored the range

            await using var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[1 << 16];
            var written = have;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                progress(written);
            }
        }

        string hash;
        await using (var check = File.OpenRead(part))
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
        if (hash != sha256)
        {
            File.Delete(part);
            throw new InvalidDataException("the downloaded file is damaged (checksum mismatch) — try again");
        }
        File.Move(part, target, overwrite: true);
    }

    public void Delete()
    {
        Stop();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }

    // ───────────────────────── Running ─────────────────────────

    /// <summary>Starts the server if needed and waits until the model is loaded (the first start takes a while).</summary>
    public async Task<LlmEndpoint> EnsureRunningAsync(HttpClient http, CancellationToken ct)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            _lastUseUtc = DateTime.UtcNow;
            if (IsRunning && await IsHealthyAsync(http, ct)) return Endpoint;
            if (!IsInstalled) throw new LlmException("the model is not downloaded");
            Stop();
            KillLeftovers();

            _port = FreePort();
            var start = new ProcessStartInfo(ServerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ServerPath)!,
            };
            foreach (var arg in new[]
                     {
                         "-m", ModelPath, "--host", "127.0.0.1", "--port", _port.ToString(), "-c", "8192", "-np", "1",
                         "--no-webui", "--reasoning", "off",
                     })
                start.ArgumentList.Add(arg);
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new LlmException("the model did not start");
            }
            catch (Win32Exception ex)
            {
                throw new LlmException("the model did not start: " + ex.Message, ex);
            }
            _server = process;
            TieToThisProcess(process);
            // The server talks a lot; its output is read and dropped so it never blocks.
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new LlmException($"the model stopped (code {process.ExitCode}) — maybe not enough memory");
                if (await IsHealthyAsync(http, ct)) break;
                await Task.Delay(500, ct);
            }
            if (!await IsHealthyAsync(http, ct))
            {
                Stop();
                throw new LlmException("the model took too long to start");
            }
            _idleTimer ??= new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            return Endpoint;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private LlmEndpoint Endpoint => new($"http://127.0.0.1:{_port}/v1", "rimforge-assistant");

    private async Task<bool> IsHealthyAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await http.GetAsync($"http://127.0.0.1:{_port}/health", cts.Token);
            return response.IsSuccessStatusCode; // 503 while the model is loading
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Marks the model as in use, so the idle timer doesn't stop it in the middle of an answer.</summary>
    public void Touch() => _lastUseUtc = DateTime.UtcNow;

    private void StopIfIdle()
    {
        if (IsRunning && DateTime.UtcNow - _lastUseUtc > IdleStop && _startLock.Wait(0))
        {
            try { Stop(); }
            finally { _startLock.Release(); }
        }
    }

    /// <summary>Stops the server and frees its memory.</summary>
    public void Stop()
    {
        var process = _server;
        _server = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // already gone
        }
        process.Dispose();
    }

    /// <summary>A server left over from a RimForge that crashed.</summary>
    private void KillLeftovers()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ServerExe)))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, ServerPath, StringComparison.OrdinalIgnoreCase)) p.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // not ours or already gone
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Windows closes the server together with RimForge, even if RimForge crashes (a job object).</summary>
    private void TieToThisProcess(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
                var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                var ptr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(_job, 9, ptr, (uint)length); // JobObjectExtendedLimitInformation
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            AssignProcessToJobObject(_job, process.Handle);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or EntryPointNotFoundException)
        {
            // Then it is stopped by Stop() only.
        }
    }

    public void Dispose()
    {
        _idleTimer?.Dispose();
        Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
