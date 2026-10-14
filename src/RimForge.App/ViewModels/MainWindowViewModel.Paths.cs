using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Library;
using RimForge.Core.Paths;

namespace RimForge.App.ViewModels;

/// <summary>One folder on the settings page: where it is, whether RimForge found it or the player chose it.</summary>
public sealed partial class PathRowViewModel(string key) : ObservableObject
{
    public const string Game = "game", Workshop = "workshop", LocalMods = "local", Config = "config", Library = "library";

    public string Key { get; } = key;
    public string Title => Loc.Get("Paths." + Key);
    public string Hint => Loc.Get("Paths." + Key + ".Hint");
    public string Icon => Key switch
    {
        Game => "\uE7FC",
        Workshop => "\uE774",
        LocalMods => "\uE8B7",
        Config => "\uE713",
        _ => "\uE8F1",
    };

    /// <summary>The player may choose this folder (the local mods folder is always the game's own Mods).</summary>
    public bool CanChange => Key is Game or Workshop or Config;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPath), nameof(PathText), nameof(CanOpen))]
    public partial string? Path { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanReset))]
    public partial bool IsCustom { get; set; }

    public bool HasPath => Path is not null;
    public bool CanOpen => Path is not null && Directory.Exists(Path);
    public bool CanReset => CanChange && IsCustom;
    public string PathText => Path ?? Loc.Get("Paths.NotFound");

    public string StatusText => !HasPath ? Loc.Get("Paths.Status.Missing")
        : IsCustom ? Loc.Get("Paths.Status.Custom")
        : CanChange ? Loc.Get("Paths.Status.Auto") : "";

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(StatusText));
    }
}

/// <summary>
/// The game's folders on the settings page. RimForge finds them by itself (Steam, GOG); the player can point at
/// others — a game on another drive, a Workshop folder Steam keeps elsewhere, save data moved with -savedatafolder.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<PathRowViewModel> PathRows { get; } =
    [
        new(PathRowViewModel.Game), new(PathRowViewModel.Workshop), new(PathRowViewModel.LocalMods),
        new(PathRowViewModel.Config), new(PathRowViewModel.Library),
    ];

    /// <summary>Set by the window: shows the folder picker (title, folder to start in) and returns the chosen folder.</summary>
    public Func<string, string?, Task<string?>>? PickFolder { get; set; }

    /// <summary>A saved copy of the game is chosen: these folders belong to the installed game, not to it.</summary>
    public bool PathsForInstalledOnly => IsCopyActive;

    private PathRowViewModel Row(string key) => PathRows.First(r => r.Key == key);

    private void RefreshPathRows()
    {
        var game = Row(PathRowViewModel.Game);
        game.Path = _steamPaths.GameDir;
        game.IsCustom = _settings.GameDir is not null;

        var workshop = Row(PathRowViewModel.Workshop);
        workshop.Path = _steamPaths.WorkshopDir;
        workshop.IsCustom = _settings.WorkshopDir is not null;

        Row(PathRowViewModel.LocalMods).Path = _steamPaths.LocalModsDir;

        var config = Row(PathRowViewModel.Config);
        config.Path = _steamPaths.ConfigDir;
        config.IsCustom = _settings.ConfigDir is not null;

        Row(PathRowViewModel.Library).Path = LibraryRoot;
        OnPropertyChanged(nameof(PathsForInstalledOnly));
    }

    private void RefreshPathTexts()
    {
        foreach (var row in PathRows) row.RefreshTexts();
    }

    [RelayCommand]
    private async Task ChoosePath(PathRowViewModel row)
    {
        if (!row.CanChange || PickFolder is null || IsLoading) return;
        var picked = await PickFolder(row.Title, row.CanOpen ? row.Path : null);
        if (picked is null) return;

        var resolved = row.Key switch
        {
            PathRowViewModel.Game => GamePathDetector.ResolveGameDir(picked),
            PathRowViewModel.Workshop => GamePathDetector.ResolveWorkshopDir(picked),
            _ => GamePathDetector.ResolveConfigDir(picked),
        };
        if (resolved is null)
        {
            ShowToast(Loc.Format("Paths.Wrong." + row.Key, picked), 10);
            return;
        }
        await SetPathAsync(row.Key, resolved);
        ShowToast(Loc.Format("Paths.Changed", row.Title, resolved));
    }

    [RelayCommand]
    private async Task ResetPath(PathRowViewModel row)
    {
        if (!row.CanReset || IsLoading) return;
        await SetPathAsync(row.Key, null);
        ShowToast(Loc.Format("Paths.Reset", row.Title, row.PathText));
    }

    [RelayCommand]
    private void OpenPath(PathRowViewModel row)
    {
        if (row.CanOpen)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Path}\"") { UseShellExecute = true });
    }

    /// <summary>Remembers the folder (null: find it again by itself) and reads the mods from the new place.</summary>
    private async Task SetPathAsync(string key, string? dir)
    {
        // A folder that is just what RimForge finds by itself is not kept: it follows the game if Steam moves it.
        var detected = await Task.Run(GamePathDetector.Detect);
        switch (key)
        {
            case PathRowViewModel.Game:
                _settings.GameDir = GamePathDetector.SamePath(dir, detected.GameDir) ? null : dir;
                break;
            case PathRowViewModel.Workshop:
                _settings.WorkshopDir = GamePathDetector.SamePath(dir, detected.WorkshopDir) ? null : dir;
                break;
            case PathRowViewModel.Config:
                _settings.ConfigDir = GamePathDetector.SamePath(dir, detected.ConfigDir) ? null : dir;
                break;
        }
        _settings.Save();
        _saveUsage = new Dictionary<string, IReadOnlyList<string>>();
        await LoadAsync();
    }

    /// <summary>
    /// Save data kept somewhere else than the game's usual folder: the game must be told where (-savedatafolder),
    /// or it would play with other settings and another mod list.
    /// </summary>
    private string? CustomSaveDataFolder() =>
        _settings.ConfigDir is { } dir && !GamePathDetector.SamePath(dir, GamePathDetector.DetectConfigDir()) ? dir : null;
}
