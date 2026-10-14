using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimForge.Steam.Workshop;

namespace RimForge.Steam.Client;

/// <summary>Why the Steam client can't be used right now.</summary>
public enum SteamProblem { None, SteamNotRunning, NotLoggedIn, GameRunning, Failed }

public sealed class SteamUnavailableException(SteamProblem problem, string? detail = null)
    : Exception(detail ?? problem.ToString())
{
    public SteamProblem Problem { get; } = problem;
}

/// <summary>
/// RimForge's side of the Steam helper: starts it on first use, sends requests, stops it when asked
/// (before the game starts, after a quiet minute, when RimForge closes).
/// </summary>
public sealed class SteamBridge : IDisposable
{
    private readonly string _exePath;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HelperResponse>> _pending = new();
    private Process? _process;
    private StreamWriter? _input;
    private TaskCompletionSource<HelperResponse>? _hello;
    private int _nextId;

    public SteamBridge(string? exePath = null) => _exePath = exePath ?? Environment.ProcessPath!;

    public bool IsConnected => _process is { HasExited: false } && _hello?.Task is { IsCompletedSuccessfully: true, Result.Ok: true };
    public string? UserName { get; private set; }
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Raised (on a background thread) whenever the helper starts or stops.</summary>
    public event Action? ConnectionChanged;

    public static bool IsSteamRunning() =>
        Process.GetProcessesByName("steam").Length > 0;

    public async Task<HelperResponse> SendAsync(HelperRequest request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        LastUsedUtc = DateTime.UtcNow;
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<HelperResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var line = JsonSerializer.Serialize(request with { Id = id }, HelperProtocol.Json);
            lock (_pending) _input!.WriteLine(line);
            var response = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), ct);
            LastUsedUtc = DateTime.UtcNow;
            return response;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<WorkshopPage> SearchAsync(WorkshopQuery query, CancellationToken ct = default) =>
        Check(await SendAsync(new HelperRequest { Op = HelperOp.Search, Query = query }, ct: ct)).Page ?? new WorkshopPage([], 0, false);

    public async Task<IReadOnlyList<ItemState>> GetStatesAsync(IEnumerable<ulong> ids, CancellationToken ct = default) =>
        Check(await SendAsync(new HelperRequest { Op = HelperOp.States, Ids = ids.ToArray() }, ct: ct)).States ?? [];

    public async Task SubscribeAsync(IEnumerable<ulong> ids, CancellationToken ct = default) =>
        Check(await SendAsync(new HelperRequest { Op = HelperOp.Subscribe, Ids = ids.ToArray() }, TimeSpan.FromSeconds(60), ct));

    public async Task UnsubscribeAsync(IEnumerable<ulong> ids, CancellationToken ct = default) =>
        Check(await SendAsync(new HelperRequest { Op = HelperOp.Unsubscribe, Ids = ids.ToArray() }, TimeSpan.FromSeconds(60), ct));

    public async Task DownloadAsync(IEnumerable<ulong> ids, CancellationToken ct = default) =>
        Check(await SendAsync(new HelperRequest { Op = HelperOp.Download, Ids = ids.ToArray() }, ct: ct));

    private static HelperResponse Check(HelperResponse response) =>
        response.Ok ? response : throw new SteamUnavailableException(SteamProblem.Failed, response.Error);

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        await _startLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return;
            Stop();
            if (!IsSteamRunning()) throw new SteamUnavailableException(SteamProblem.SteamNotRunning);
            if (Core.Launch.GameLauncher.IsRunning()) throw new SteamUnavailableException(SteamProblem.GameRunning);

            var start = new ProcessStartInfo(_exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(_exePath)!,
            };
            start.ArgumentList.Add(HelperProtocol.Argument);

            _hello = new TaskCompletionSource<HelperResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = Process.Start(start) ?? throw new SteamUnavailableException(SteamProblem.Failed, "helper did not start");
            _process = process;
            _input = process.StandardInput;
            _input.AutoFlush = true;
            _ = Task.Run(() => ReadLoop(process));
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

            HelperResponse hello;
            try
            {
                hello = await _hello.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
            catch (TimeoutException)
            {
                Stop();
                throw new SteamUnavailableException(SteamProblem.Failed, "Steam did not answer");
            }
            if (!hello.Ok)
            {
                Stop();
                throw new SteamUnavailableException(hello.Error == "offline" ? SteamProblem.NotLoggedIn : SteamProblem.Failed, hello.Error);
            }
            UserName = hello.UserName;
            ConnectionChanged?.Invoke();
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void ReadLoop(Process process)
    {
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                HelperResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<HelperResponse>(line, HelperProtocol.Json);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (response is null) continue;
                if (response.Id == 0) _hello?.TrySetResult(response);
                else if (_pending.TryGetValue(response.Id, out var tcs)) tcs.TrySetResult(response);
            }
        }
        catch (IOException)
        {
        }
        // The helper is gone: fail whatever was still waiting for it.
        _hello?.TrySetResult(new HelperResponse { Ok = false, Error = "helper exited" });
        foreach (var tcs in _pending.Values)
            tcs.TrySetResult(new HelperResponse { Ok = false, Error = "helper exited" });
        if (ReferenceEquals(process, _process)) ConnectionChanged?.Invoke();
    }

    /// <summary>Closes the helper; Steam stops showing "playing RimWorld" a moment later.</summary>
    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            _input?.Close(); // the helper exits when its input ends
            if (!process.WaitForExit(3000)) process.Kill();
        }
        catch (Exception)
        {
            // already gone
        }
        process.Dispose();
        _input = null;
        UserName = null;
        ConnectionChanged?.Invoke();
    }

    /// <summary>Stops the helper and waits until Steam has noticed, so "Play" can start the real game.</summary>
    public async Task StopForGameAsync()
    {
        if (_process is null) return;
        Stop();
        await Task.Delay(1500);
    }

    public void Dispose() => Stop();
}
