using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using RimForge.Core.Models;

namespace RimForge.Core.Library;

/// <summary>A branch of RimWorld on Steam ("public", "unstable", "version-1.5", "alpha17"...).</summary>
public sealed record GameBranch(string Name, string? Description, DateTime? UpdatedUtc, long Bytes, bool Locked)
{
    /// <summary>Download size of each official expansion for this branch, by the expansion's Steam app id.</summary>
    public IReadOnlyDictionary<string, long> Dlc { get; init; } = new Dictionary<string, long>();

    public bool IsCurrent => Name == "public";
    public bool IsTesting => Name == "unstable";

    /// <summary>"1.5" for "version-1.5", "1.4.3901" for "version-1.4.3901"; null for branches without a number.</summary>
    public string? Version => Regex.Match(Name, @"^version-(\d+\.\d+(?:\.\d+)?)") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>"Alpha 17", "Beta 19" for the earliest versions.</summary>
    public string? Early => Regex.Match(Name, @"^(alpha|beta)(\d+)$") is { Success: true } m
        ? char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..] + " " + m.Groups[2].Value
        : null;
}

/// <summary>
/// SteamCMD, Valve's console Steam client, kept in the RimForge library: it downloads any branch of RimWorld (old
/// versions, the test branch) into a folder of its own, so the game installed by Steam is never touched. The player
/// signs in once in SteamCMD's own window (RimForge never sees the password); later runs use the saved login.
/// SteamCMD buffers its output when it is not a console, so download progress is measured by the folder's size.
/// </summary>
public sealed class SteamCmd(string dir)
{
    public static readonly Uri InstallerUrl = new("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");

    public string Dir { get; } = dir;
    public string Exe => Path.Combine(Dir, "steamcmd.exe");

    /// <summary>Unpacked and updated once (the first run downloads the rest of the client).</summary>
    public bool IsReady => File.Exists(Exe) && File.Exists(Path.Combine(Dir, "steamclient64.dll"));

    /// <summary>Downloads steamcmd.zip from Valve and lets SteamCMD update itself.</summary>
    public async Task InstallAsync(HttpClient http, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(Exe))
        {
            var zip = Path.Combine(Dir, "steamcmd.zip");
            await using (var file = File.Create(zip))
            await using (var body = await http.GetStreamAsync(InstallerUrl, ct))
                await body.CopyToAsync(file, ct);
            ZipFile.ExtractToDirectory(zip, Dir, overwriteFiles: true);
            File.Delete(zip);
        }
        // The first start fetches the client itself (~40 MB) and quits.
        await RunAsync(["+quit"], TimeSpan.FromMinutes(10), ct);
        if (!IsReady) throw new IOException("SteamCMD did not finish setting itself up.");
    }

    // ── Branches ──

    /// <summary>The branches of RimWorld, read anonymously from Steam (no login needed).</summary>
    public async Task<List<GameBranch>> GetBranchesAsync(CancellationToken ct)
    {
        var output = await RunAsync(["+login", "anonymous", "+app_info_update", "1", "+app_info_print", KnownMods.RimWorldAppId.ToString(), "+quit"],
            TimeSpan.FromMinutes(3), ct);
        return ParseBranches(output);
    }

    /// <summary>
    /// Reads the branches from <c>app_info_print</c> output: names, descriptions, dates and the download size of the
    /// 64-bit Windows game for each (official expansions the player owns come on top of it).
    /// </summary>
    public static List<GameBranch> ParseBranches(string output)
    {
        var start = output.IndexOf("\"" + KnownMods.RimWorldAppId + "\"", StringComparison.Ordinal);
        if (start < 0) return [];
        var app = Vdf.Parse(output[start..]).Child(KnownMods.RimWorldAppId.ToString());
        var depots = app?.Child("depots");
        var branches = depots?.Child("branches");
        if (branches is null) return [];

        var list = new List<GameBranch>();
        foreach (var (name, node) in branches.Children)
        {
            var updated = long.TryParse(node.Value("timeupdated"), out var t) && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : (DateTime?)null;
            var dlc = new Dictionary<string, long>();
            foreach (var (_, depot) in depots!.Children)
                if (depot.Value("dlcappid") is { } dlcId && long.TryParse(depot.Child("manifests")?.Child(name)?.Value("size"), out var size))
                    dlc[dlcId] = dlc.GetValueOrDefault(dlcId) + size;
            list.Add(new GameBranch(name, node.Value("description"), updated, GameBytes(depots, name), node.Value("pwdrequired") == "1") { Dlc = dlc });
        }
        return list;
    }

    /// <summary>The size of the game's own depots for a branch: Windows, 64-bit if the branch has one, else 32-bit.</summary>
    private static long GameBytes(Vdf depots, string branch)
    {
        long Sum(string arch) => depots.Children
            .Where(d => d.Node.Child("manifests") is not null && d.Node.Value("dlcappid") is null)
            .Where(d => d.Node.Child("config") is not { } c ||
                        ((c.Value("oslist") ?? "windows").Contains("windows") && (c.Value("osarch") is null or "" || c.Value("osarch") == arch)))
            .Sum(d => long.TryParse(d.Node.Child("manifests")!.Child(branch)?.Value("size"), out var s) ? s : 0);
        var x64 = Sum("64");
        return x64 > 0 ? x64 : Sum("32");
    }

    // ── Login ──

    /// <summary>
    /// True if SteamCMD keeps a saved login for <paramref name="account"/> (config\config.vdf). Without one, a hidden
    /// run would try an empty password, so the player signs in in SteamCMD's window first (<see cref="OpenLoginWindow"/>).
    /// </summary>
    public bool HasSavedLogin(string account)
    {
        var file = Path.Combine(Dir, "config", "config.vdf");
        if (!File.Exists(file)) return false;
        try
        {
            var steam = Vdf.Parse(File.ReadAllText(file)).Child("InstallConfigStore")?.Child("Software")?.Child("Valve")?.Child("Steam");
            return steam?.Child("Accounts")?.Child(account) is not null && steam.Child("ConnectCache") is { } cache && cache.HasValues;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What <see cref="DownloadAsync"/> returns when the saved login is gone and the player has to sign in again.</summary>
    public const string LoginNeeded = "login";

    public static bool NeedsLogin(string output) =>
        Prompt.IsMatch(output) || output.Contains("Cached credentials not found", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Invalid Password", StringComparison.OrdinalIgnoreCase);

    /// <summary>What SteamCMD prints when it waits for the player to type something.</summary>
    private static readonly Regex Prompt = new(@"password:|Steam Guard code|Two-factor code|confirm the login|Enter the current code", RegexOptions.IgnoreCase);

    /// <summary>Opens SteamCMD's own console window so the player signs in (password, Steam Guard). The window closes when done.</summary>
    public Process OpenLoginWindow(string account)
    {
        var start = new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = Dir };
        start.ArgumentList.Add("+login");
        start.ArgumentList.Add(account);
        start.ArgumentList.Add("+quit");
        return Process.Start(start) ?? throw new IOException("SteamCMD did not start.");
    }

    // ── Download ──

    /// <summary>
    /// Downloads a branch of RimWorld into <paramref name="target"/> (a folder of its own; an unfinished download in it
    /// is continued). <paramref name="bytes"/> gets the folder's size every second. Returns null on success, or the
    /// reason SteamCMD gave.
    /// </summary>
    public async Task<string?> DownloadAsync(string account, string branch, string target, IProgress<long>? bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        List<string> args = ["+force_install_dir", Path.GetFullPath(target), "+login", account, "+app_update", KnownMods.RimWorldAppId.ToString()];
        if (branch != "public") args.AddRange(["-beta", branch]);
        args.AddRange(["validate", "+quit"]);

        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var measuring = Task.Run(async () =>
        {
            while (!poll.IsCancellationRequested)
            {
                bytes?.Report(FolderBytes(target));
                try { await Task.Delay(1000, poll.Token); } catch (OperationCanceledException) { }
            }
        }, CancellationToken.None);
        try
        {
            var output = await RunAsync(args, TimeSpan.FromHours(6), ct, stopOnPrompt: true);
            return ReadDownload(output);
        }
        finally
        {
            poll.Cancel();
            await measuring;
        }
    }

    /// <summary>Null when SteamCMD reports the game installed; otherwise its error line.</summary>
    public static string? ReadDownload(string output)
    {
        if (Regex.IsMatch(output, @"Success! App '\d+' fully installed")) return null;
        if (NeedsLogin(output)) return LoginNeeded;
        var error = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("Error!", StringComparison.Ordinal) || l.Contains("FAILED", StringComparison.Ordinal));
        return error ?? "SteamCMD stopped without finishing the download.";
    }

    public static long FolderBytes(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch (IOException) { return 0; } }) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ── Running ──

    /// <summary>
    /// Runs SteamCMD hidden and returns everything it printed. With <paramref name="stopOnPrompt"/> it is stopped as
    /// soon as it asks for input (nobody can answer a hidden window).
    /// </summary>
    private async Task<string> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, bool stopOnPrompt = false)
    {
        var start = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = new Process { StartInfo = start };
        var text = new StringBuilder();
        var prompted = new TaskCompletionSource();
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (text) text.AppendLine(e.Data);
            if (stopOnPrompt && Prompt.IsMatch(e.Data)) prompted.TrySetResult();
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Start();
        process.StandardInput.Close(); // a prompt reads nothing instead of waiting forever
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await Task.WhenAny(process.WaitForExitAsync(limit.Token), prompted.Task.WaitAsync(limit.Token));
        }
        catch (OperationCanceledException) { }
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        ct.ThrowIfCancellationRequested();
        lock (text) return text.ToString();
    }

    /// <summary>The official expansions installed with the game by Steam (their app ids), from its appmanifest.</summary>
    public static HashSet<string> InstalledDlc(string? gameDir)
    {
        var manifest = gameDir is null ? null : Path.GetFullPath(Path.Combine(gameDir, "..", "..", $"appmanifest_{KnownMods.RimWorldAppId}.acf"));
        if (manifest is null || !File.Exists(manifest)) return [];
        try
        {
            return Vdf.Parse(File.ReadAllText(manifest)).Child("AppState")?.Child("InstalledDepots")?.Children
                .Select(d => d.Node.Value("dlcappid")).OfType<string>().ToHashSet() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The account Steam signed in last on this computer (config\loginusers.vdf), to fill in for the player.</summary>
    public static string? LastSteamAccount(string? steamDir)
    {
        var file = steamDir is null ? null : Path.Combine(steamDir, "config", "loginusers.vdf");
        if (file is null || !File.Exists(file)) return null;
        try
        {
            var users = Vdf.Parse(File.ReadAllText(file)).Child("users");
            if (users is null) return null;
            var all = users.Children.Select(u => u.Node).ToList();
            var recent = all.FirstOrDefault(u => u.Value("MostRecent") == "1")
                         ?? all.OrderByDescending(u => long.TryParse(u.Value("Timestamp"), out var t) ? t : 0).FirstOrDefault();
            return recent?.Value("AccountName");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Valve's text KeyValues ("key" "value" / "key" { ... }), enough for SteamCMD output and Steam's config files.</summary>
public sealed class Vdf
{
    private readonly List<(string Key, string? Value, Vdf? Node)> _items = [];

    public IEnumerable<(string Key, Vdf Node)> Children => _items.Where(i => i.Node is not null).Select(i => (i.Key, i.Node!));
    public Vdf? Child(string key) => _items.FirstOrDefault(i => i.Node is not null && string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)).Node;
    public bool HasValues => _items.Any(i => i.Value is not null);
    public string? Value(string key) => _items.FirstOrDefault(i => i.Value is not null && string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public static Vdf Parse(string text)
    {
        var tokens = Tokens(text).GetEnumerator();
        return Read(tokens, nested: false);
    }

    private static Vdf Read(IEnumerator<string?> tokens, bool nested)
    {
        var node = new Vdf();
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key is null) return node;            // "}" closes this level
            if (!tokens.MoveNext()) break;
            if (tokens.Current == "{") node._items.Add((key, null, Read(tokens, nested: true)));
            else if (tokens.Current is null) return node;
            else node._items.Add((key, tokens.Current, null));
        }
        return node;
    }

    /// <summary>Quoted strings and bare words; "{" for an opening brace, null for a closing one.</summary>
    private static IEnumerable<string?> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '{') { i++; yield return "{"; continue; }
            if (c == '}') { i++; yield return null; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            var sb = new StringBuilder();
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
                    }
                    else sb.Append(text[i]);
                    i++;
                }
                i++;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) sb.Append(text[i++]);
            }
            yield return sb.ToString();
        }
    }

    public override string ToString() => string.Join(", ", _items.Select(i => i.Key));
}
