using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Library;
using RimForge.Core.Models;
using RimForge.Core.Mods;
using RimForge.Core.Paths;

namespace RimForge.App.ViewModels;

/// <summary>One game the player can play: the Steam install or a saved copy from the library.</summary>
public sealed partial class GameChoiceViewModel(GameInstance? copy, string name, string details) : ObservableObject
{
    /// <summary>Null for the Steam (or GOG) install itself.</summary>
    public GameInstance? Copy { get; } = copy;
    public bool IsCopy => Copy is not null;
    public string Name { get; } = name;
    public string Details { get; } = details;
    [ObservableProperty] public partial bool IsActive { get; set; }
}

/// <summary>
/// Versions of the game: the RimForge library keeps copies of the game (each with its own saves, settings and
/// frozen mods) so a game update cannot break a modpack; the player picks which one RimForge manages and launches.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<GameChoiceViewModel> GameChoices { get; } = [];

    [ObservableProperty] public partial string ActiveGameText { get; set; } = "";
    [ObservableProperty] public partial bool IsCopyActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveGameCopy))]
    public partial bool IsLibraryBusy { get; set; }

    [ObservableProperty] public partial string LibraryStage { get; set; } = "";
    [ObservableProperty] public partial double LibraryProgress { get; set; }

    private GamePaths _steamPaths = new();
    private GameInstance? _activeCopy;
    private GameLibrary? _library;
    private CancellationTokenSource? _libraryCancel;

    public bool CanSaveGameCopy => !IsLibraryBusy;
    public string LibraryPath => LibraryRoot;

    /// <summary>The library lives next to RimForge itself (see <see cref="GameContext.DefaultLibraryDir"/>); the settings may point elsewhere.</summary>
    // Not written into the settings: a portable RimForge moved to another drive finds its library next to itself again.
    private string LibraryRoot => _settings.LibraryDir ?? (_defaultLibrary ??= GameContext.DefaultLibraryDir());
    private string? _defaultLibrary;

    private GameLibrary Library => _library ??= new GameLibrary(LibraryRoot);

    /// <summary>
    /// Called by <see cref="LoadAsync"/> with the detected install: when a saved copy is chosen, RimForge works with
    /// the copy's folders instead (its game files, its save data, no Steam Workshop folder — its mods are frozen).
    /// </summary>
    private GamePaths ChooseGame(GamePaths detected)
    {
        _steamPaths = detected;
        var copies = Library.Load();
        _activeCopy = copies.FirstOrDefault(c => c.Id == _settings.ActiveGameId && Directory.Exists(c.GameDir));
        if (_settings.ActiveGameId is not null && _activeCopy is null)
        {
            _settings.ActiveGameId = null; // the copy was removed outside RimForge
            _settings.Save();
        }

        GameChoices.Clear();
        var steamVersion = detected.ReadFullGameVersion()?.Split(' ')[0] ?? "?";
        var store = detected.Store == GameStore.Gog ? "GOG" : "Steam";
        GameChoices.Add(new GameChoiceViewModel(null, $"RimWorld {steamVersion} · {store}", Loc.Get("Game.SteamDetails")) { IsActive = _activeCopy is null });
        foreach (var copy in copies.OrderByDescending(c => c.CreatedUtc))
            GameChoices.Add(new GameChoiceViewModel(copy, $"RimWorld {copy.ShortVersion} · {Loc.Get("Game.Copy")}",
                copy.Branch is { } branch
                    ? Loc.Format("Game.DownloadedDetails", branch, Dates.Format(copy.CreatedUtc.ToLocalTime()), copy.ModCount)
                    : Loc.Format("Game.CopyDetails", Dates.Format(copy.CreatedUtc.ToLocalTime()), copy.ModCount)) { IsActive = copy == _activeCopy });

        IsCopyActive = _activeCopy is not null;
        ActiveGameText = _activeCopy is { } c ? $"RimWorld {c.ShortVersion} · {Loc.Get("Game.Copy")}" : $"RimWorld {steamVersion} · {store}";
        _profileSettings = null; // mod settings of modpacks are kept per game
        return _activeCopy?.Paths ?? detected;
    }

    /// <summary>Extra launch arguments: a copy plays with its own save data, and so does a game whose save data the player moved.</summary>
    private string[] LaunchArguments() =>
        _activeCopy is { } copy ? [$"-savedatafolder={copy.DataDir}"]
        : CustomSaveDataFolder() is { } data ? [$"-savedatafolder={data}"]
        : [];

    /// <summary>Where the modpacks' own mod settings are kept for the current game.</summary>
    private string ProfileSettingsDir => _activeCopy is { } copy
        ? Path.Combine(Core.Storage.AppDataPaths.ProfilesDir, "game-" + copy.Id)
        : Core.Storage.AppDataPaths.ProfilesDir;

    [RelayCommand]
    private async Task SwitchGame(GameChoiceViewModel choice)
    {
        if (choice.IsActive || IsLibraryBusy) return;
        if (!await ConfirmLeaveAsync()) return;
        IsDirty = false; // unsaved edits belong to the game being left; the other game shows its own list
        _settings.ActiveGameId = choice.Copy?.Id;
        _settings.Save();
        await LoadAsync();
        ShowToast(Loc.Format("Game.Switched", ActiveGameText));
    }

    [RelayCommand]
    private void OpenLibraryFolder()
    {
        Directory.CreateDirectory(LibraryRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LibraryRoot}\"") { UseShellExecute = true });
    }

    /// <summary>Saves the installed game (Steam/GOG) with all its mods and save data as a copy in the library.</summary>
    [RelayCommand]
    private async Task SaveGameCopy()
    {
        if (IsLibraryBusy) return;
        if (_steamPaths.GameDir is null || !GamePathDetector.IsGameDir(_steamPaths.GameDir))
        {
            ShowToast(Loc.Get("Game.NoGame"));
            return;
        }
        var version = _steamPaths.ReadFullGameVersion()?.Split(' ')[0] ?? "?";
        if (Library.Find(version) is not null)
        {
            ShowToast(Loc.Format("Game.AlreadySaved", version));
            return;
        }

        IsLibraryBusy = true;
        LibraryStage = Loc.Get("Game.Counting");
        LibraryProgress = 0;
        try
        {
            // The mods of the installed game, wherever RimForge is looking now.
            var (mods, plan) = await Task.Run(() =>
            {
                var all = ModScanner.Scan(_steamPaths, _steamPaths.ReadGameVersion() ?? "1.6").Where(m => !m.IsOfficial).ToList();
                var keyed = all.Select(m => (m, GameLibrary.StoreKey(m))).ToList();
                return (keyed, Library.Plan(_steamPaths, keyed));
            });
            Directory.CreateDirectory(LibraryRoot);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(LibraryRoot))!).AvailableFreeSpace;
            IsLibraryBusy = false;

            var answer = await AskAsync(new DialogViewModel
            {
                Title = Loc.Format("Game.SaveTitle", version),
                Text = Loc.Format("Game.SaveText", Humanize.Size(plan.GameBytes), mods.Count, Humanize.Size(plan.NewModBytes), plan.ReusedMods,
                    Humanize.Size(plan.DataBytes), Humanize.Size(plan.TotalBytes), LibraryRoot, Humanize.Size(free)),
                PrimaryText = Loc.Get("Game.SaveButton"),
            });
            if (answer != "primary") return;
            if (free < plan.TotalBytes + 500L * 1024 * 1024)
            {
                ShowToast(Loc.Format("Game.NoSpace", Humanize.Size(plan.TotalBytes), Humanize.Size(free)));
                return;
            }

            IsLibraryBusy = true;
            _libraryCancel = new CancellationTokenSource();
            var token = _libraryCancel.Token;
            var progress = new Progress<(string Stage, double Share)>(p =>
            {
                LibraryStage = Loc.Format("Game.Stage." + p.Stage, (int)(p.Share * 100));
                LibraryProgress = p.Share;
            });
            var copy = await Task.Run(() => Library.Snapshot(_steamPaths, mods, progress, token), token);
            IsLibraryBusy = false;

            ChooseGame(_steamPaths);
            if (_activeCopy is null) await LoadAsync(); // refresh the list of games
            var switchNow = await AskAsync(new DialogViewModel
            {
                Title = Loc.Format("Game.SavedTitle", copy.ShortVersion),
                Text = Loc.Format("Game.SavedText", copy.ShortVersion),
                PrimaryText = Loc.Get("Game.SwitchNow"),
                SecondaryText = Loc.Get("Game.StayOnSteam"),
            });
            if (switchNow == "primary" && GameChoices.FirstOrDefault(g => g.Copy?.Id == copy.Id) is { } choice)
                await SwitchGame(choice);
        }
        catch (OperationCanceledException)
        {
            ShowToast(Loc.Get("Game.Cancelled"));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.Format("Game.Failed", ex.Message));
        }
        finally
        {
            IsLibraryBusy = false;
            _libraryCancel?.Dispose();
            _libraryCancel = null;
        }
    }

    [RelayCommand]
    private void CancelLibraryWork() => _libraryCancel?.Cancel();

    [RelayCommand]
    private async Task DeleteGameCopy(GameChoiceViewModel choice)
    {
        if (choice.Copy is not { } copy || IsLibraryBusy) return;
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Format("Game.DeleteTitle", copy.ShortVersion),
            Text = Loc.Format("Game.DeleteText", copy.ShortVersion, copy.DataDir),
            PrimaryText = Loc.Get("Dialog.Delete"),
            PrimaryIsDanger = true,
        });
        if (answer != "primary") return;
        if (choice.IsActive)
        {
            if (!await ConfirmLeaveAsync()) return;
            IsDirty = false;
            _settings.ActiveGameId = null;
            _settings.Save();
        }

        IsLibraryBusy = true;
        LibraryStage = Loc.Get("Game.Deleting");
        LibraryProgress = 0;
        try
        {
            var freed = await Task.Run(() => Library.Delete(copy));
            await LoadAsync();
            ShowToast(Loc.Format("Game.Deleted", copy.ShortVersion, Humanize.Size(freed)));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.Format("Game.Failed", ex.Message));
        }
        finally
        {
            IsLibraryBusy = false;
        }
    }

    /// <summary>
    /// A copy of the game does not read the Steam Workshop folder, so mods downloaded while it is chosen are
    /// frozen into the library and linked into the copy. Returns how many were added.
    /// </summary>
    private int AddDownloadsToCopy(IEnumerable<string> folders)
    {
        if (_activeCopy is not { } copy) return 0;
        var added = 0;
        foreach (var folder in folders.Where(Directory.Exists))
        {
            if (ModScanner.ReadMod(folder, ModSource.Workshop, _gameVersion ?? "1.6") is not { } mod) continue;
            try
            {
                Library.AddMod(copy, mod);
                added++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                ShowToast(Loc.Format("Game.Failed", ex.Message));
            }
        }
        return added;
    }
}
