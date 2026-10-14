using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Checks;
using RimForge.Core.Config;
using RimForge.Core.Launch;
using RimForge.Core.Models;
using RimForge.Core.Mods;
using RimForge.Core.Paths;
using RimForge.Core.Profiles;
using RimForge.Core.Sorting;
using RimForge.Core.Storage;

namespace RimForge.App.ViewModels;

public sealed record DependencyRow(string Name, string StatusText, string Level)
{
    public bool IsOk => Level == "ok";
    public bool IsWarn => Level == "warn";
    public bool IsError => Level == "error";
}

public sealed partial class IssueViewModel(ModIssue issue, string message, string? fixText, Action? fix) : ObservableObject
{
    public ModIssue Issue { get; } = issue;
    public string Message { get; } = message;
    public string? FixText { get; } = fixText;
    public bool CanFix => fix is not null;
    public bool IsError => Issue.Severity == IssueSeverity.Error;
    public bool IsWarning => Issue.Severity == IssueSeverity.Warning;

    [RelayCommand]
    private void Fix() => fix?.Invoke();
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const int SourceAll = 0, SourceWorkshop = 1, SourceLocal = 2, SourceOfficial = 3;

    private readonly AppSettings _settings;
    private GamePaths _paths = new();
    private string? _gameVersion;
    private ModsConfigFile? _config;
    private Dictionary<string, ModInfo> _installed = new();
    /// <summary>Every mod folder found, duplicates included.</summary>
    private IReadOnlyList<ModInfo> _scanned = [];
    private Dictionary<string, ModItemViewModel> _items = new();
    private List<ModItemViewModel> _active = [];
    /// <summary>Order stored in the active modpack.</summary>
    private List<string> _savedOrder = [];
    /// <summary>Order currently written to the game's ModsConfig.xml.</summary>
    private List<string> _gameOrder = [];
    /// <summary>Names and Workshop ids for mods that are in a modpack but not installed.</summary>
    private Dictionary<string, ProfileMod> _hints = new();
    private readonly ProfileStore _store = new(AppDataPaths.ProfilesDir);
    private readonly Stack<List<string>> _undo = new();
    private readonly Stack<List<string>> _redo = new();
    private IReadOnlyList<ModIssue> _issues = [];
    private bool _communityStarted;

    public MainWindowViewModel(AppSettings settings)
    {
        _settings = settings;
        AvailableSearch = "";
        ActiveSearch = "";
        StatusText = "";
        StatusLevel = "ok";
        FooterCounts = "";
        FooterGame = "";
        CurrentPage = "mods";
        ActiveProfileName = "";
        Loc.Changed += RefreshTexts;
        InitModInfo();
        InitWorkshop();
        InitBrain();
        InitTranslation();
    }

    public ObservableCollection<ModItemViewModel> AvailableView { get; } = [];
    public ObservableCollection<ModItemViewModel> ActiveView { get; } = [];
    public ObservableCollection<IssueViewModel> Issues { get; } = [];
    public ObservableCollection<IssueViewModel> SelectedIssues { get; } = [];
    public ObservableCollection<DependencyRow> SelectedDependencies { get; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool NeedsGameDir { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial string AvailableSearch { get; set; }
    [ObservableProperty] public partial string ActiveSearch { get; set; }
    [ObservableProperty] public partial int SourceFilter { get; set; }

    [ObservableProperty] public partial ModItemViewModel? SelectedAvailable { get; set; }
    [ObservableProperty] public partial ModItemViewModel? SelectedActive { get; set; }
    [ObservableProperty] public partial ModItemViewModel? SelectedMod { get; set; }
    [ObservableProperty] public partial Bitmap? SelectedPreview { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOk), nameof(IsStatusWarn), nameof(IsStatusError))]
    public partial string StatusLevel { get; set; }

    public bool IsStatusOk => StatusLevel == "ok";
    public bool IsStatusWarn => StatusLevel == "warn";
    public bool IsStatusError => StatusLevel == "error";
    [ObservableProperty] public partial string FooterCounts { get; set; }
    [ObservableProperty] public partial string FooterGame { get; set; }

    public bool HasSelection => SelectedMod is not null;
    public bool HasNoIssues => Issues.Count == 0;
    public string ToggleText => SelectedMod?.IsActive == true ? Loc.Get("Btn.Disable") : Loc.Get("Btn.Enable");
    public bool CanToggleSelected => SelectedMod is { Mod.IsCore: false };
    public bool SelectedHasWorkshop => SelectedMod?.Mod.WorkshopId is not null;
    public string SelectedVersions => SelectedMod is null ? ""
        : SelectedMod.Mod.SupportedVersions.Count == 0 ? Loc.Get("Details.AnyVersion")
        : string.Join(" · ", SelectedMod.Mod.SupportedVersions);
    public bool SelectedHasDependencies => SelectedDependencies.Count > 0;
    public bool SelectedHasIssues => SelectedIssues.Count > 0;
    public bool SelectedHasDescription => !string.IsNullOrWhiteSpace(SelectedMod?.PlainDescription);

    // ───────────────────────── Loading ─────────────────────────

    public async Task LoadAsync()
    {
        IsLoading = true;
        Message = null;
        try
        {
            var keepOrder = IsDirty ? _active.Select(m => m.Id).ToList() : null;

            var detected = await Task.Run(GamePathDetector.Detect);
            _paths = detected with
            {
                GameDir = _settings.GameDir ?? detected.GameDir,
                WorkshopDir = _settings.WorkshopDir ?? detected.WorkshopDir,
                ConfigDir = _settings.ConfigDir ?? detected.ConfigDir,
            };
            if (_settings.GameDir is not null && _settings.GameDir != detected.GameDir)
                _paths = _paths with { Store = GameStore.Unknown };
            _paths = ChooseGame(_paths);
            RefreshPathRows();

            NeedsGameDir = _paths.GameDir is null || !GamePathDetector.IsGameDir(_paths.GameDir);
            if (NeedsGameDir) return;

            _gameVersion = _paths.ReadGameVersion();
            var mods = await Task.Run(() => ModScanner.Scan(_paths, _gameVersion ?? "1.6"));
            _scanned = mods;
            // Two folders with one package id: the game takes one; a mod the player just unsubscribed from loses.
            _installed = mods.GroupBy(m => m.PackageId).ToDictionary(g => g.Key, g => g.OrderBy(IsUnsubscribed).First());
            _duplicateFolders = mods.Where(m => !IsUnsubscribed(m)).GroupBy(m => m.PackageId).Where(g => g.Count() > 1)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(DuplicateLabel).ToList());
            _items = _installed.Values.ToDictionary(m => m.PackageId, m => new ModItemViewModel(m));

            var configFile = _paths.ModsConfigFile;
            _config = configFile is not null && File.Exists(configFile) ? ModsConfigFile.Load(configFile) : null;
            if (_config is null) Message = Loc.Get("Msg.ConfigNotFound");
            // Steam switched the game to another version (e.g. the 1.7 test branch) since the list was written:
            // the game would throw the whole list away, so the file gets the game's version right away.
            else if (_paths.ReadFullGameVersion() is { } fullVersion && !_config.IsForGame(fullVersion))
            {
                try
                {
                    _config.StampVersion(fullVersion);
                    _config.Save(configFile!, AppDataPaths.BackupsDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Message = Loc.Format("Msg.Error", ex.Message);
                }
            }

            _gameOrder = _config?.ActiveMods.ToList() ?? [KnownMods.Core];
            // Mods of a collection that finished downloading while RimForge was closed or another modpack was chosen.
            if (ResolvePlaceholders(_gameOrder) is var resolved && !resolved.SequenceEqual(_gameOrder) && _config is not null && _paths.ModsConfigFile is not null)
            {
                try
                {
                    WriteGameList(resolved);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _gameOrder = resolved;
                }
            }
            LoadProfiles(_gameOrder);
            _undo.Clear();
            _redo.Clear();
            // The game's own list is what will actually load, so it is shown even if it drifted from the modpack.
            Restore(keepOrder ?? _gameOrder);
            if (keepOrder is null && !_gameOrder.SequenceEqual(_savedOrder) && ActiveProfile is not null)
                Message = Loc.Format("Msg.GameListDiffers", ActiveProfile.Name);

            RefreshWorkshopIndex();
            RefreshModInfo();

            if (!_communityStarted)
            {
                _communityStarted = true;
                _ = LoadCommunityRulesAsync(force: false);
                _ = LoadDatabasesAsync(force: false);
            }
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SetGameDirAsync(string dir)
    {
        if (GamePathDetector.ResolveGameDir(dir) is not { } game)
        {
            Message = Loc.Get("Msg.NotAGameDir");
            return;
        }
        _settings.GameDir = game;
        _settings.Save();
        await LoadAsync();
    }

    private ModItemViewModel ItemFor(string id)
    {
        if (_items.TryGetValue(id, out var item)) return item;
        // Keep unknown ids so saving never silently drops what the game had.
        _hints.TryGetValue(id, out var hint);
        item = new ModItemViewModel(new ModInfo
        {
            PackageId = id, DeclaredPackageId = id, Name = hint?.Name ?? id, FolderPath = "", Source = ModSource.Local,
            WorkshopId = hint?.WorkshopId,
        }, isMissing: true);
        _items[id] = item;
        return item;
    }

    // ───────────────────────── Changing the list ─────────────────────────

    private List<string> Snapshot() => _active.Select(m => m.Id).ToList();

    private void Restore(IEnumerable<string> ids)
    {
        _active = ids.Distinct().Select(ItemFor).ToList();
        RefreshAll();
    }

    /// <summary>Runs a change to the active list with undo support.</summary>
    private void Change(Action action)
    {
        var before = Snapshot();
        action();
        if (before.SequenceEqual(Snapshot())) return;
        _undo.Push(before);
        _redo.Clear();
        RefreshAll();
    }

    private void RefreshAll()
    {
        var activeSet = new HashSet<ModItemViewModel>(_active);
        foreach (var item in _items.Values)
        {
            item.IsActive = activeSet.Contains(item);
            item.Index = 0;
        }
        for (var i = 0; i < _active.Count; i++) _active[i].Index = i + 1;

        var now = Snapshot();
        IsDirty = !now.SequenceEqual(_savedOrder) || !now.SequenceEqual(_gameOrder);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RefreshAvailableView();
        RefreshActiveView();
        RefreshPins();
        Recheck();
        UpdateSelectionDetails();
        RefreshCardStates();
    }

    public void Activate(IEnumerable<ModItemViewModel> items, int? insertAt = null) => Change(() =>
    {
        var toAdd = items.Where(i => !i.IsMissing).ToList();
        foreach (var i in toAdd) _active.Remove(i);
        var at = Math.Clamp(insertAt ?? _active.Count, 0, _active.Count);
        _active.InsertRange(at, toAdd);
    });

    public void Deactivate(IEnumerable<ModItemViewModel> items) => Change(() =>
    {
        foreach (var i in items.Where(i => !i.Mod.IsCore).ToList()) _active.Remove(i);
    });

    /// <summary>Moves (or inserts) items so they land before <paramref name="target"/>, or after it if <paramref name="after"/>.</summary>
    public void MoveTo(IReadOnlyList<ModItemViewModel> items, ModItemViewModel? target, bool after) => Change(() =>
    {
        var moving = items.Where(i => !i.IsMissing || _active.Contains(i)).ToList();
        if (target is not null && moving.Contains(target)) return;
        foreach (var i in moving) _active.Remove(i);
        var at = target is null ? _active.Count : _active.IndexOf(target) + (after ? 1 : 0);
        _active.InsertRange(Math.Clamp(at, 0, _active.Count), moving);
    });

    public void Toggle(ModItemViewModel item)
    {
        if (item.IsActive) Deactivate([item]);
        else Activate([item]);
    }

    [RelayCommand]
    private void ToggleSelected()
    {
        if (SelectedMod is { } m) Toggle(m);
    }

    [RelayCommand]
    private void MoveSelected(string where)
    {
        if (SelectedActive is not { } item) return;
        Change(() =>
        {
            var i = _active.IndexOf(item);
            _active.RemoveAt(i);
            var to = where switch
            {
                "top" => 0,
                "up" => Math.Max(0, i - 1),
                "down" => Math.Min(_active.Count, i + 1),
                _ => _active.Count,
            };
            _active.Insert(to, item);
        });
    }

    [RelayCommand]
    private void EnableAllShown() => Activate(AvailableView.ToList());

    [RelayCommand]
    private void DisableAllShown() => Deactivate(ActiveView.ToList());

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }

    private bool CanUndo() => _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }

    private bool CanRedo() => _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert() => Change(() => _active = _savedOrder.Distinct().Select(ItemFor).ToList());

    private bool IsInstalled(string id) => _installed.ContainsKey(id) || _installed.ContainsKey(id + "_steam");

    // ───────────────────────── Views and filters ─────────────────────────

    partial void OnAvailableSearchChanged(string value) => RefreshAvailableView();
    partial void OnActiveSearchChanged(string value) => RefreshActiveView();
    partial void OnSourceFilterChanged(int value) => RefreshAvailableView();

    private bool Matches(ModItemViewModel item, string search) =>
        (search.Length == 0 || item.SearchText.Contains(search.Trim().ToLowerInvariant())) && PassesFilters(item);

    private void RefreshAvailableView()
    {
        var search = AvailableSearch;
        var list = _items.Values
            .Where(i => !i.IsActive && !i.IsMissing && Matches(i, search))
            .Where(i => SourceFilter switch
            {
                SourceWorkshop => i.IsWorkshop,
                SourceLocal => i.IsLocal,
                SourceOfficial => i.IsOfficial,
                _ => true,
            })
            .OrderBy(i => i.IsOfficial ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Sync(AvailableView, list);
    }

    private void RefreshActiveView() => Sync(ActiveView, _active.Where(i => Matches(i, ActiveSearch)).ToList());

    /// <summary>Updates a collection in place so the selection and scroll position survive.</summary>
    private static void Sync(ObservableCollection<ModItemViewModel> target, List<ModItemViewModel> desired)
    {
        var wanted = new HashSet<ModItemViewModel>(desired);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var j = target.IndexOf(desired[i]);
            if (j >= 0) target.Move(j, i);
            else target.Insert(i, desired[i]);
        }
    }

    // ───────────────────────── Checks (traffic light) ─────────────────────────

    private void Recheck()
    {
        ScheduleCoverageRefresh();
        ScheduleLoadTimeRefresh();
        _rules = RuleSet.Build(Snapshot(), _installed, _community, ActiveProfile?.Model.Rules, ActiveProfile?.Model.Pins);
        _issues = ModListChecker.Check(Snapshot(), _installed, _gameVersion, _rules, CheckExtras());

        // A broken order rule that belongs to a contradiction can't be fixed by moving mods:
        // it is a mistake in a mod's description, so it is shown for information only.
        var contradictions = ModSorter.FindConflicts(Snapshot(), _rules).Select(c => c.Mods.ToHashSet()).ToList();
        _issues = _issues.Select(i => i.Rule is { } r && contradictions.Any(c => c.Contains(r.Before) && c.Contains(r.After))
            ? i with { Severity = IssueSeverity.Info, Kind = IssueKind.Contradiction }
            : i).ToList();

        foreach (var item in _items.Values)
        {
            item.IssueLevel = "";
            item.IssueTip = "";
        }

        Issues.Clear();
        foreach (var issue in _issues.OrderByDescending(i => i.Severity))
        {
            var vm = MakeIssue(issue);
            Issues.Add(vm);
            if (issue.Severity != IssueSeverity.Info && _items.TryGetValue(issue.ModId, out var item))
            {
                var level = issue.Severity == IssueSeverity.Error ? "error" : "warn";
                if (item.IssueLevel != "error") item.IssueLevel = level;
                item.IssueTip = item.IssueTip.Length == 0 ? vm.Message : item.IssueTip + "\n" + vm.Message;
            }
        }
        OnPropertyChanged(nameof(HasNoIssues));
        OnPropertyChanged(nameof(CanFixAll));

        var errors = _issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = _issues.Count(i => i.Severity == IssueSeverity.Warning);
        (StatusLevel, StatusText) = errors > 0 ? ("error", Loc.Format("Status.Errors", errors))
            : warnings > 0 ? ("warn", Loc.Format("Status.Warnings", warnings))
            : ("ok", Loc.Get("Status.Ok"));

        UpdateFooterSize();
        var store = _paths.Store switch
        {
            GameStore.Steam => Loc.Get("Source.Steam"),
            GameStore.Gog => Loc.Get("Source.Gog"),
            _ => Loc.Get("Source.Unknown"),
        };
        FooterGame = Loc.Format("Footer.Game", _gameVersion ?? "?", store);
        CountDownloadable();
    }

    private string NameOf(string? id, string? fallback = null) =>
        id is not null && _items.TryGetValue(id, out var item) ? item.Name : fallback ?? id ?? "?";

    private IssueViewModel MakeIssue(ModIssue issue)
    {
        var name = NameOf(issue.ModId);
        var other = NameOf(issue.OtherId, issue.OtherName);
        if (MakeExtraIssue(issue, name, other) is { } extra)
            return new IssueViewModel(issue, extra.Message, extra.FixText, extra.Fix);
        var message = issue.Kind switch
        {
            IssueKind.UnsupportedVersion => Loc.Format("Issue.UnsupportedVersion", name, issue.Detail, _gameVersion),
            IssueKind.BrokenAbout => Loc.Format("Issue.BrokenAbout", name, issue.Detail),
            IssueKind.Contradiction => Loc.Format("Issue.Contradiction", name, other),
            _ => Loc.Format("Issue." + issue.Kind, name, other),
        };
        if (issue.Rule is { } rule)
            message += " " + Loc.Format("Issue.Reason", RuleText(rule));
        else if (issue.Kind == IssueKind.Incompatible && issue.Detail is { } note)
            message += " " + Loc.Format("Issue.Reason", note);

        (string?, Action?) fix = issue.Kind switch
        {
            IssueKind.DependencyInactive when issue.OtherId is { } dep => (Loc.Get("Fix.Enable"), () =>
            {
                var target = _items[issue.ModId];
                MoveTo([ItemFor(_items.ContainsKey(dep) ? dep : dep + "_steam")], target, after: false);
            }),
            IssueKind.DependencyMissing when Steam.Workshop.WorkshopIds.Parse(issue.Detail) is { } depId =>
                (Loc.Get("Fix.Download"), () => DownloadOne(depId, other, name, enable: true)),
            IssueKind.DependencyMissing => (Loc.Get("Fix.Find"), () => SearchWorkshopFor(other)),
            IssueKind.NotInstalled when WorkshopIdOfMissing(issue.ModId) is { } wid =>
                (Loc.Get("Fix.Download"), () => DownloadOne(wid, name, null, enable: false)),
            IssueKind.NotInstalled => (Loc.Get("Fix.Find"), () => SearchWorkshopFor(name)),
            IssueKind.ShouldLoadAfter when issue.OtherId is { } o && FindActive(o) is { } t =>
                (Loc.Get("Fix.Move"), () => MoveTo([_items[issue.ModId]], t, after: true)),
            IssueKind.ShouldLoadBefore when issue.OtherId is { } o && FindActive(o) is { } t =>
                (Loc.Get("Fix.Move"), () => MoveTo([_items[issue.ModId]], t, after: false)),
            _ => (null, null),
        };
        return new IssueViewModel(issue, message, fix.Item1, fix.Item2);
    }

    private ModItemViewModel? FindActive(string id) =>
        _active.FirstOrDefault(m => m.Id == id) ?? _active.FirstOrDefault(m => m.Id == id + "_steam");

    // ───────────────────────── Selection / details ─────────────────────────

    partial void OnSelectedAvailableChanged(ModItemViewModel? value)
    {
        if (value is not null) SelectedMod = value;
    }

    partial void OnSelectedActiveChanged(ModItemViewModel? value)
    {
        if (value is not null) SelectedMod = value;
    }

    partial void OnSelectedModChanged(ModItemViewModel? value)
    {
        var old = SelectedPreview;
        SelectedPreview = LoadPreview(value?.Mod.PreviewImagePath);
        old?.Dispose();
        OnSelectedModForTranslation();
        OnModPickedForGallery(value);
        UpdateSelectionDetails();
    }

    private static Bitmap? LoadPreview(string? path)
    {
        if (path is null) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 640);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateSelectionDetails()
    {
        SelectedDependencies.Clear();
        SelectedIssues.Clear();
        if (SelectedMod is { } m)
        {
            foreach (var dep in m.Mod.Dependencies)
            {
                var active = FindActive(dep.PackageId) is not null;
                var installed = _installed.ContainsKey(dep.PackageId) || _installed.ContainsKey(dep.PackageId + "_steam");
                var (text, level) = active ? (Loc.Get("Dep.Ok"), "ok")
                    : installed ? (Loc.Get("Dep.Inactive"), "warn")
                    : (Loc.Get("Dep.Missing"), "error");
                SelectedDependencies.Add(new DependencyRow(NameOf(dep.PackageId, dep.DisplayName), text, level));
            }
            foreach (var issue in Issues.Where(i => i.Issue.ModId == m.Id)) SelectedIssues.Add(issue);
        }
        UpdatePlacement();
        UpdateSelectedInfo();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(CanToggleSelected));
        OnPropertyChanged(nameof(SelectedHasWorkshop));
        OnPropertyChanged(nameof(SelectedVersions));
        OnPropertyChanged(nameof(SelectedHasDependencies));
        OnPropertyChanged(nameof(SelectedHasIssues));
        OnPropertyChanged(nameof(SelectedHasDescription));
    }

    [RelayCommand]
    private void OpenSelectedFolder()
    {
        if (SelectedMod?.Mod.FolderPath is { Length: > 0 } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenSelectedWorkshop()
    {
        if (SelectedMod?.Mod.WorkshopId is { } id)
            OpenUrl($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ───────────────────────── Save / play / language ─────────────────────────

    private bool SaveNow()
    {
        if (_config is null || _paths.ModsConfigFile is null)
        {
            Message = Loc.Get("Msg.ConfigNotFound");
            return false;
        }
        try
        {
            WriteGameList(Snapshot());
            if (ActiveProfile is { } profile)
            {
                profile.Model.SetMods(Snapshot(), _installed, _hints);
                _store.Save(profile.Model);
                profile.Refresh(IsInstalled);
            }
            _savedOrder = Snapshot();
            IsDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ex.Message);
            return false;
        }
    }

    /// <summary>Writes a list to ModsConfig.xml (a backup of the old file is made first).</summary>
    private void WriteGameList(List<string> ids)
    {
        _config!.ActiveMods = ids;
        _config.StampVersion(_paths.ReadFullGameVersion());
        _config.Save(_paths.ModsConfigFile!, AppDataPaths.BackupsDir);
        _gameOrder = ids.ToList();
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save()
    {
        if (SaveNow()) ShowToast(Loc.Get("Msg.Saved"));
    }

    [RelayCommand]
    private async Task Play()
    {
        // Downloads that only wait (no connection, Steam closed) don't hold the game back.
        if (Downloads.Any(d => d.IsRunning && !d.IsWaiting))
        {
            ShowToast(Loc.Format("Dl.WaitBeforePlay", RunningDownloads));
            CurrentPage = "workshop";
            WorkshopSection = "downloads";
            return;
        }
        if (IsDirty && !SaveNow()) return;
        if (GameLauncher.IsRunning())
        {
            ShowToast(Loc.Get("Msg.GameRunning"));
            return;
        }
        if (!await ReadyForGameVersionAsync()) return;
        try
        {
            await _steam.StopForGameAsync();
            GameLauncher.Launch(_paths, LaunchArguments());
            ShowToast(Loc.Get("Msg.Launching"));
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ex.Message);
        }
    }

    /// <summary>
    /// Right before the game starts: if Steam switched the game to another version (a beta branch such as 1.7)
    /// while RimForge was open, the list is read and checked again for that version first; and ModsConfig.xml gets
    /// the game's version, or the game would throw the whole list away on start. False means "don't start yet".
    /// </summary>
    private async Task<bool> ReadyForGameVersionAsync()
    {
        var full = _paths.ReadFullGameVersion();
        if (full is null) return true;
        if (GamePaths.ParseMajorMinor(full) != _gameVersion)
        {
            var was = _gameVersion;
            await LoadAsync();
            ShowToast(Loc.Format("Msg.GameVersionChanged", was ?? "?", _gameVersion ?? "?"));
            return false;
        }
        var file = _paths.ModsConfigFile;
        if (file is null || !File.Exists(file)) return true;
        try
        {
            var onDisk = ModsConfigFile.Load(file);
            if (!onDisk.IsForGame(full))
            {
                // Keep the list exactly as it is on disk (the game may have changed it); only the version is fixed.
                onDisk.StampVersion(full);
                onDisk.Save(file, AppDataPaths.BackupsDir);
                _config?.StampVersion(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Message = Loc.Format("Msg.Error", ex.Message);
        }
        return true;
    }

    [RelayCommand]
    private Task Reload() => LoadAsync();

    [RelayCommand]
    private void SwitchLanguage()
    {
        _settings.Language = Loc.Current == "ru" ? "en" : "ru";
        _settings.Save();
        Loc.Apply(_settings.Language);
    }

    private void RefreshTexts()
    {
        foreach (var p in Profiles) p.Refresh(IsInstalled);
        UpdateCommunityStatus();
        UpdateDatabasesStatus();
        RefreshPins();
        Recheck();
        UpdateSelectionDetails();
        RefreshWorkshopTexts();
        RefreshAsstTexts();
        RefreshTranslationTexts();
        RefreshPathTexts();
        RefreshCollectionTexts();
        ScheduleLoadTimeRefresh();
        RefreshModInfoTexts();
        ResetTranslationsForLanguage();
        RefreshModelState();
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    /// <summary>A button on the message ("Find translations"), or null.</summary>
    [ObservableProperty] public partial string? MessageActionText { get; set; }
    private Action? _messageAction;

    [RelayCommand]
    private void RunMessageAction()
    {
        var action = _messageAction;
        Message = null;
        action?.Invoke();
    }

    partial void OnMessageChanged(string? value)
    {
        if (value is null) MessageActionText = null;
    }

    /// <summary>A message with a button; it stays a bit longer so there is time to press it.</summary>
    public void ShowToast(string text, string actionText, Action action, int seconds = 10)
    {
        ShowToast(text, seconds);
        MessageActionText = actionText;
        _messageAction = action;
    }

    private DispatcherTimer? _toastTimer;

    public void ShowToast(string text) => ShowToast(text, 4);

    private void ShowToast(string text, int seconds)
    {
        Message = text;
        MessageActionText = null;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Normal, (_, _) =>
        {
            if (Message == text) Message = null;
            _toastTimer?.Stop();
        });
        _toastTimer.Start();
    }
}
