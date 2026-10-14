using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Config;
using RimForge.Core.Library;
using RimForge.Core.Models;
using RimForge.Core.Mods;
using RimForge.Core.Paths;

namespace RimForge.App.ViewModels;

/// <summary>One branch of RimWorld on Steam the player can download.</summary>
public sealed partial class GameBranchViewModel(GameBranch branch, long dlcBytes) : ObservableObject
{
    public GameBranch Branch { get; } = branch;
    /// <summary>The game plus the expansions the player has.</summary>
    public long TotalBytes { get; } = branch.Bytes + dlcBytes;
    public bool IsTesting => Branch.IsTesting;

    public string Title => Branch switch
    {
        { IsCurrent: true } => Loc.Get("GameDl.Current"),
        { IsTesting: true } => Loc.Get("GameDl.Testing"),
        { Version: { } v } => "RimWorld " + v,
        { Early: { } e } => "RimWorld " + e,
        _ => Branch.Name,
    };

    public string Details => string.Join(" · ", new[]
    {
        Branch.Name,
        Branch.UpdatedUtc is { } d ? Loc.Format("GameDl.Updated", Dates.Format(d.ToLocalTime())) : null,
        TotalBytes > 0 ? "≈ " + Humanize.Size(TotalBytes) : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>
/// Downloading other versions of RimWorld (old ones, the test branch) straight into the library with SteamCMD, so the
/// game Steam installed stays as it is. The result is a copy of the game like a saved one: own saves, frozen mods.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<GameBranchViewModel> GameBranches { get; } = [];

    [ObservableProperty] public partial bool IsGameDlOpen { get; set; }

    /// <summary>"setup" (getting SteamCMD and the list), "pick", "login", "download", "error".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameDlSetup), nameof(IsGameDlPick), nameof(IsGameDlLogin), nameof(IsGameDlDownloading), nameof(IsGameDlError))]
    public partial string GameDlStep { get; set; } = "setup";

    public bool IsGameDlSetup => GameDlStep == "setup";
    public bool IsGameDlPick => GameDlStep == "pick";
    public bool IsGameDlLogin => GameDlStep == "login";
    public bool IsGameDlDownloading => GameDlStep == "download";
    public bool IsGameDlError => GameDlStep == "error";

    [ObservableProperty] public partial string GameDlStatus { get; set; } = "";
    [ObservableProperty] public partial double GameDlProgress { get; set; }
    [ObservableProperty] public partial bool GameDlIndeterminate { get; set; } = true;
    /// <summary>"↓ 45%" on the game button while a version downloads in the background.</summary>
    [ObservableProperty] public partial string GameDlBadge { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartGameDl))]
    public partial GameBranchViewModel? SelectedGameBranch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartGameDl))]
    public partial string SteamAccount { get; set; } = "";

    public bool CanStartGameDl => SelectedGameBranch is not null && !string.IsNullOrWhiteSpace(SteamAccount);

    private SteamCmd SteamCmdTool => new(Path.Combine(Library.ToolsDir, "steamcmd"));
    private CancellationTokenSource? _gameDlCts;
    private Task? _gameDlWork;

    [RelayCommand]
    private async Task OpenGameDownload()
    {
        IsGameDlOpen = true;
        if (_gameDlWork is { IsCompleted: false } || GameDlStep is "pick" or "login") return;
        await PrepareGameDownloadAsync();
    }

    /// <summary>Hides the window; a running download goes on (its progress is on the game button).</summary>
    [RelayCommand]
    private void CloseGameDownload() => IsGameDlOpen = false;

    [RelayCommand]
    private Task RetryGameDownload() => PrepareGameDownloadAsync();

    /// <summary>Gets SteamCMD (once) and the list of branches.</summary>
    private async Task PrepareGameDownloadAsync()
    {
        GameDlStep = "setup";
        GameDlIndeterminate = true;
        if (string.IsNullOrWhiteSpace(SteamAccount))
            SteamAccount = _settings.SteamCmdAccount ?? SteamCmd.LastSteamAccount(GamePathDetector.SteamInstallDir()) ?? "";
        var tool = SteamCmdTool;
        try
        {
            if (!tool.IsReady)
            {
                GameDlStatus = Loc.Get("GameDl.Installing");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                await tool.InstallAsync(http, CancellationToken.None);
            }
            var branches = ReadBranchCache(tool);
            if (branches is null)
            {
                GameDlStatus = Loc.Get("GameDl.Reading");
                branches = await tool.GetBranchesAsync(CancellationToken.None);
                if (branches.Count == 0) throw new IOException(Loc.Get("GameDl.NoBranches"));
                WriteBranchCache(tool, branches);
            }

            var owned = SteamCmd.InstalledDlc(_steamPaths.GameDir);
            GameBranches.Clear();
            foreach (var b in branches.Where(b => !b.Locked)
                         .OrderBy(b => b.IsCurrent ? 0 : b.IsTesting ? 1 : b.Version is not null ? 2 : b.Early is not null ? 4 : 3)
                         .ThenByDescending(b => System.Version.TryParse(b.Version, out var v) ? v : null)
                         .ThenByDescending(b => b.UpdatedUtc))
                GameBranches.Add(new GameBranchViewModel(b, b.Dlc.Where(d => owned.Contains(d.Key)).Sum(d => d.Value)));
            if (SelectedGameBranch is not null && GameBranches.FirstOrDefault(b => b.Branch.Name == SelectedGameBranch.Branch.Name) is { } again)
                PickGameBranch(again);
            else SelectedGameBranch = null;
            GameDlStep = "pick";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException
                                       or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            GameDlStatus = Loc.Format("GameDl.SetupFailed", ex.Message);
            GameDlStep = "error";
        }
    }

    // Asking Steam takes SteamCMD a minute, so the list is kept for a while.
    private static readonly TimeSpan BranchCacheAge = TimeSpan.FromHours(12);
    private static string BranchCacheFile(SteamCmd tool) => Path.Combine(tool.Dir, "rimforge-branches.json");

    private static List<GameBranch>? ReadBranchCache(SteamCmd tool)
    {
        var file = BranchCacheFile(tool);
        try
        {
            if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > BranchCacheAge) return null;
            return System.Text.Json.JsonSerializer.Deserialize<List<GameBranch>>(File.ReadAllText(file)) is { Count: > 0 } list ? list : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteBranchCache(SteamCmd tool, List<GameBranch> branches)
    {
        try
        {
            File.WriteAllText(BranchCacheFile(tool), System.Text.Json.JsonSerializer.Serialize(branches));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [RelayCommand]
    private void PickGameBranch(GameBranchViewModel branch)
    {
        foreach (var b in GameBranches) b.IsSelected = b == branch;
        SelectedGameBranch = branch;
    }

    [RelayCommand]
    private async Task StartGameDownload()
    {
        if (SelectedGameBranch is not { } branch || string.IsNullOrWhiteSpace(SteamAccount)) return;
        var account = SteamAccount.Trim();
        _settings.SteamCmdAccount = account;
        _settings.Save();

        Directory.CreateDirectory(LibraryRoot);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(LibraryRoot))!).AvailableFreeSpace;
        var needed = branch.TotalBytes * 2 + 500L * 1024 * 1024; // SteamCMD stages files before moving them in place
        if (branch.TotalBytes > 0 && free < needed)
        {
            ShowToast(Loc.Format("Game.NoSpace", Humanize.Size(needed), Humanize.Size(free)));
            return;
        }
#if DEBUG
        // Testing without a Steam account: a small fake game from this folder is "downloaded" instead.
        if (Environment.GetEnvironmentVariable("RIMFORGE_DEMO_GAMEDL") is { Length: > 0 } demo)
        {
            _gameDlWork = DownloadBranchAsync(branch, (target, bytes, ct) => DemoFetchAsync(demo, target, bytes, ct));
            await _gameDlWork;
            return;
        }
#endif
        if (!SteamCmdTool.HasSavedLogin(account))
        {
            GameDlStatus = Loc.Format("GameDl.LoginText", account);
            GameDlStep = "login";
            return;
        }
        _gameDlWork = DownloadBranchAsync(branch, (target, bytes, ct) => SteamCmdTool.DownloadAsync(account, branch.Branch.Name, target, bytes, ct));
        await _gameDlWork;
    }

#if DEBUG
    private static async Task<string?> DemoFetchAsync(string from, string target, IProgress<long> bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        long done = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
            done += new FileInfo(file).Length;
            bytes.Report(done);
            await Task.Delay(300, ct);
        }
        return null;
    }
#endif

    /// <summary>Opens SteamCMD's window to sign in; when it closes with a saved login the download starts.</summary>
    [RelayCommand]
    private async Task SignInSteamCmd()
    {
        var account = SteamAccount.Trim();
        if (account.Length == 0) return;
        var tool = SteamCmdTool;
        try
        {
            GameDlStatus = Loc.Format("GameDl.LoginWaiting", account);
            using var window = tool.OpenLoginWindow(account);
            await window.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            GameDlStatus = Loc.Format("GameDl.SetupFailed", ex.Message);
            return;
        }
        if (!tool.HasSavedLogin(account))
        {
            GameDlStatus = Loc.Format("GameDl.LoginFailed", account);
            return;
        }
        await StartGameDownload();
    }

    [RelayCommand]
    private void BackToGameBranches() => GameDlStep = "pick";

    [RelayCommand]
    private void CancelGameDownload() => _gameDlCts?.Cancel();

    /// <summary>Runs <paramref name="fetch"/> (SteamCMD) into the branch's download folder, then takes the game into the library.</summary>
    private async Task DownloadBranchAsync(GameBranchViewModel branch, Func<string, IProgress<long>, CancellationToken, Task<string?>> fetch)
    {
        var target = Library.DownloadDir(branch.Branch.Name);
        _gameDlCts = new CancellationTokenSource();
        var token = _gameDlCts.Token;
        GameDlStep = "download";
        GameDlIndeterminate = branch.TotalBytes <= 0;
        GameDlProgress = 0;
        GameDlStatus = Loc.Format("GameDl.Starting", branch.Title);
        var progress = new Progress<long>(bytes =>
        {
            if (token.IsCancellationRequested) return;
            if (branch.TotalBytes > 0)
            {
                var share = Math.Min(0.99, (double)bytes / branch.TotalBytes);
                GameDlProgress = share;
                GameDlBadge = $"↓ {(int)(share * 100)}%";
                GameDlStatus = Loc.Format("GameDl.Progress", branch.Title, Humanize.Size(bytes), Humanize.Size(branch.TotalBytes));
            }
            else
            {
                GameDlBadge = "↓";
                GameDlStatus = Loc.Format("GameDl.ProgressNoTotal", branch.Title, Humanize.Size(bytes));
            }
        });
        try
        {
            var error = await fetch(target, progress, token);
            GameDlBadge = "";
            if (error == SteamCmd.LoginNeeded)
            {
                GameDlStatus = Loc.Format("GameDl.LoginText", SteamAccount.Trim());
                GameDlStep = "login";
                return;
            }
            if (error is not null)
            {
                GameDlStatus = Loc.Format("GameDl.Failed", error.Contains("No subscription", StringComparison.OrdinalIgnoreCase)
                    ? Loc.Get("GameDl.NotOwned") : error);
                GameDlStep = "error";
                return;
            }

            GameDlIndeterminate = true;
            GameDlStatus = Loc.Get("GameDl.Finishing");
            var copy = await Task.Run(() => Library.Adopt(target, branch.Branch.Name, _steamPaths.ConfigDir));
            IsGameDlOpen = false;
            GameDlStep = "pick";
            await FinishDownloadedGameAsync(copy);
        }
        catch (OperationCanceledException)
        {
            GameDlBadge = "";
            GameDlStep = "pick";
            await Task.Run(() => Junction.DeleteTree(target));
            ShowToast(Loc.Get("GameDl.Cancelled"));
        }
        catch (InvalidOperationException)
        {
            // This exact version is in the library already.
            GameDlBadge = "";
            var version = new GamePaths { GameDir = target }.ReadFullGameVersion()?.Split(' ')[0] ?? "?";
            await Task.Run(() => Junction.DeleteTree(target));
            GameDlStatus = Loc.Format("Game.AlreadySaved", version);
            GameDlStep = "error";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            GameDlBadge = "";
            GameDlStatus = Loc.Format("GameDl.Failed", ex.Message);
            GameDlStep = "error";
        }
        finally
        {
            _gameDlCts?.Dispose();
            _gameDlCts = null;
        }
    }

    /// <summary>
    /// A version just downloaded: offers the mods of the current modpack that support it (frozen into the library like
    /// in a saved copy), gives the copy a mod list, then offers to switch to it.
    /// </summary>
    private async Task FinishDownloadedGameAsync(GameInstance copy)
    {
        var version = copy.Paths.ReadGameVersion() ?? copy.ShortVersion;
        var order = ActiveProfile?.Model.ModIds.ToList() ?? Snapshot();
        var mods = order.Select(id => _installed.GetValueOrDefault(id)).OfType<ModInfo>().Where(m => !m.IsOfficial).ToList();
        var fit = mods.Where(m => m.SupportsVersion(version)).ToList();
        var misfit = mods.Except(fit).ToList();

        var take = false;
        if (fit.Count > 0)
        {
            var names = string.Join(", ", misfit.Take(8).Select(m => m.Name)) + (misfit.Count > 8 ? ", …" : "");
            var answer = await AskAsync(new DialogViewModel
            {
                Title = Loc.Format("GameDl.ModsTitle", copy.ShortVersion),
                Text = Loc.Format("GameDl.ModsText", ActiveProfile?.Name ?? "", mods.Count, version, fit.Count, misfit.Count)
                       + (misfit.Count > 0 ? "\n\n" + Loc.Format("GameDl.ModsMisfit", names) : ""),
                PrimaryText = Loc.Format("GameDl.ModsTake", fit.Count),
                SecondaryText = Loc.Get("GameDl.ModsNone"),
            });
            take = answer == "primary";
        }

        IsLibraryBusy = true;
        LibraryProgress = 0;
        LibraryStage = Loc.Get("GameDl.Linking");
        try
        {
            var chosen = take ? fit : [];
            await Task.Run(() =>
            {
                for (var i = 0; i < chosen.Count; i++)
                {
                    Library.AddMod(copy, chosen[i]);
                    var share = (double)(i + 1) / chosen.Count;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => LibraryProgress = share);
                }
                WriteCopyModList(copy, order, chosen);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowToast(Loc.Format("Game.Failed", ex.Message));
        }
        finally
        {
            IsLibraryBusy = false;
        }

        ChooseGame(_steamPaths);
        if (_activeCopy is null) await LoadAsync();
        var switchNow = await AskAsync(new DialogViewModel
        {
            Title = Loc.Format("GameDl.DoneTitle", copy.ShortVersion),
            Text = Loc.Format("GameDl.DoneText", copy.ShortVersion),
            PrimaryText = Loc.Get("Game.SwitchNow"),
            SecondaryText = Loc.Get("GameDl.Later"),
        });
        if (switchNow == "primary" && GameChoices.FirstOrDefault(g => g.Copy?.Id == copy.Id) is { } choice)
            await SwitchGame(choice);
    }

    /// <summary>The copy's own ModsConfig.xml: the game, its expansions and the chosen mods, in the modpack's order.</summary>
    private static void WriteCopyModList(GameInstance copy, List<string> order, List<ModInfo> chosen)
    {
        var paths = copy.Paths;
        var version = paths.ReadGameVersion() ?? "1.6";
        var official = ModScanner.Scan(paths, version).Where(m => m.IsOfficial).Select(m => m.PackageId).ToHashSet();
        var wanted = chosen.Select(m => m.PackageId).Concat(official).ToHashSet();
        var list = order.Where(wanted.Contains).ToList();
        list.InsertRange(0, official.Where(id => !list.Contains(id)).OrderBy(id => id == KnownMods.Core ? 0 : 1));
        if (!list.Contains(KnownMods.Core)) list.Insert(0, KnownMods.Core);

        var config = new ModsConfigFile { Version = paths.ReadFullGameVersion(), ActiveMods = list };
        config.Save(paths.ModsConfigFile!, backupDir: null);
    }
}
