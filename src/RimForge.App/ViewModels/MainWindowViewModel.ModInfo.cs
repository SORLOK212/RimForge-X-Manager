using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Content;
using RimForge.Core.Models;
using RimForge.Core.Saves;
using RimForge.Core.Storage;

namespace RimForge.App.ViewModels;

/// <summary>A toggle in the "Filters" drop-down (a category or a state such as "new").</summary>
public sealed partial class FilterChipViewModel(string key, Action changed) : ObservableObject
{
    public string Key { get; } = key;
    public ModCategory? Category => Enum.TryParse<ModCategory>(Key, out var c) ? c : null;
    public string Name => Loc.Get((Category is null ? "State." : "Cat.") + Key);
    public string Tip => Loc.Get((Category is null ? "Tip.State." : "Tip.Cat.") + Key);

    [ObservableProperty] public partial bool IsChecked { get; set; }
    [ObservableProperty] public partial int Count { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Tip));
    }
}

/// <summary>A category shown on a mod's card, with a way to take it off.</summary>
public sealed record CategoryTag(ModCategory Category, string Name, bool IsManual);

/// <summary>
/// Extra knowledge about installed mods: what is inside (categories, counts, size), what is new or
/// freshly updated, the player's stars and notes — and the "Filters" drop-down built on all of it.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly string[] StateKeys = ["New", "Updated", "Favorite", "Problems", "WrongVersion", "Removed", "InSaves", "Unused"];

    private readonly ModProfileStore _profiles = new(Path.Combine(AppDataPaths.Root, "cache", "mod-profiles-v2.json"));
    private readonly ModNotesStore _notes = new(Path.Combine(AppDataPaths.Root, "mod-notes.json"));
    private readonly NewModTracker _newMods = new(Path.Combine(AppDataPaths.Root, "new-mods.json"));
    private bool _infoStoresLoaded;
    private bool _profilesRunning;
    private bool _noteLoading;

    public ObservableCollection<FilterChipViewModel> CategoryChips { get; private set; } = [];
    public ObservableCollection<FilterChipViewModel> StateChips { get; private set; } = [];
    public ObservableCollection<CategoryTag> SelectedCategories { get; } = [];
    public ObservableCollection<FilterChipViewModel> CategoryEditChips { get; private set; } = [];

    [ObservableProperty] public partial string FilterButtonText { get; set; } = "";
    [ObservableProperty] public partial bool HasActiveFilters { get; set; }
    [ObservableProperty] public partial string? AnalysisText { get; set; }
    [ObservableProperty] public partial string SelectedContentText { get; set; } = "";
    [ObservableProperty] public partial string SelectedSizeText { get; set; } = "";
    [ObservableProperty] public partial string SelectedNote { get; set; } = "";
    [ObservableProperty] public partial bool SelectedIsFavorite { get; set; }

    public bool HasSelectedContent => SelectedContentText.Length > 0;
    public bool HasSelectedCategories => SelectedCategories.Count > 0;
    public bool SelectedIsNew => SelectedMod?.IsNew == true;
    public bool SelectedIsUpdated => SelectedMod?.IsUpdated == true;
    public bool CanEditSelected => SelectedMod is { IsMissing: false, IsOfficial: false };

    partial void OnSelectedContentTextChanged(string value) => OnPropertyChanged(nameof(HasSelectedContent));

    private void InitModInfo()
    {
        CategoryChips = new(Enum.GetValues<ModCategory>().Select(c => new FilterChipViewModel(c.ToString(), OnFiltersChanged)));
        StateChips = new(StateKeys.Select(k => new FilterChipViewModel(k, OnFiltersChanged)));
        CategoryEditChips = new(Enum.GetValues<ModCategory>().Select(c => new FilterChipViewModel(c.ToString(), () => { })));
        foreach (var chip in CategoryEditChips)
            chip.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilterChipViewModel.IsChecked) && !_noteLoading) SetCategory(chip.Category!.Value, chip.IsChecked);
            };
        UpdateFilterButton();
    }

    private void RefreshModInfoTexts()
    {
        foreach (var chip in CategoryChips.Concat(StateChips).Concat(CategoryEditChips)) chip.Refresh();
        UpdateFilterButton();
        UpdateSelectedInfo();
    }

    // ───────────────────────── Filters ─────────────────────────

    private void OnFiltersChanged()
    {
        UpdateFilterButton();
        RefreshAvailableView();
        RefreshActiveView();
    }

    private void UpdateFilterButton()
    {
        var count = CategoryChips.Count(c => c.IsChecked) + StateChips.Count(c => c.IsChecked);
        HasActiveFilters = count > 0;
        FilterButtonText = count > 0 ? Loc.Format("Filters.ButtonCount", count) : Loc.Get("Filters.Button");
    }

    [RelayCommand]
    private void ResetFilters()
    {
        foreach (var chip in CategoryChips.Concat(StateChips)) chip.IsChecked = false;
    }

    /// <summary>Several ticks in one group mean "any of them"; ticks in both groups must both hold.</summary>
    private bool PassesFilters(ModItemViewModel item)
    {
        if (!HasActiveFilters) return true;
        var categories = CategoryChips.Where(c => c.IsChecked).Select(c => c.Category!.Value).ToList();
        if (categories.Count > 0 && !categories.Any(item.Categories.Contains)) return false;
        var states = StateChips.Where(c => c.IsChecked).ToList();
        return states.Count == 0 || states.Any(s => HasState(item, s.Key));
    }

    private bool HasState(ModItemViewModel item, string key) => key switch
    {
        "New" => item.IsNew,
        "Updated" => item.IsUpdated,
        "Favorite" => item.IsFavorite,
        "Problems" => item.HasIssue,
        "WrongVersion" => _gameVersion is not null && !item.IsOfficial && !item.IsMissing && !item.Mod.SupportsVersion(_gameVersion),
        "Removed" => item.Mod.WorkshopId is { } wid && _webDetails.TryGetValue(wid, out var web) && web.Removed,
        "InSaves" => _saveUsage.TryGetValue(item.Id, out var saves) && saves.Count > 0,
        "Unused" => IsUnused(item),
        _ => false,
    };

    /// <summary>Not on in the game, not in any modpack, not in any save: safe to clean up.</summary>
    private bool IsUnused(ModItemViewModel item) =>
        !item.IsOfficial && !item.IsMissing && !item.IsActive
        && !Profiles.Any(p => p.Model.ModIds.Contains(item.Id))
        && !(_saveUsage.TryGetValue(item.Id, out var saves) && saves.Count > 0);

    private void UpdateChipCounts()
    {
        var items = _items.Values.Where(i => !i.IsMissing && !i.IsOfficial).ToList();
        foreach (var chip in CategoryChips) chip.Count = items.Count(i => i.Categories.Contains(chip.Category!.Value));
        foreach (var chip in StateChips) chip.Count = items.Count(i => HasState(i, chip.Key));
    }

    // ───────────────────────── Background knowledge ─────────────────────────

    /// <summary>After every scan: marks, notes, then (in the background) contents, sizes and save usage.</summary>
    private void RefreshModInfo()
    {
        if (!_infoStoresLoaded)
        {
            _infoStoresLoaded = true;
            _profiles.Load();
            _notes.Load();
            _newMods.Load();
        }

        var lastGame = LastGameStartUtc();
        _newMods.Update(
            _installed.Values.Where(m => !m.IsOfficial).Select(m => (m.PackageId, FolderCreated(m.FolderPath))),
            _gameOrder, lastGame, DateTime.UtcNow);

        foreach (var item in _items.Values.Where(i => !i.IsMissing))
        {
            var note = _notes.Get(item.Id);
            item.IsFavorite = note.Favorite;
            item.IsNew = !item.IsOfficial && _newMods.IsNew(item.Id);
            // A saved copy of the game keeps its mods frozen: Steam updating a mod does not reach it.
            item.IsUpdated = !item.IsNew && _activeCopy is null && item.Mod.WorkshopId is { } wid && lastGame is { } played
                             && _manifest.TryGetValue(wid, out var local) && local.UpdatedUtc > played;
            ApplyProfile(item, _profiles.Get(item.Mod), note);
        }
        UpdateChipCounts();
        RefreshAvailableView();
        RefreshActiveView();
        UpdateSelectedInfo();
        _ = AnalyzeModsAsync();
    }

    private DateTime? LastGameStartUtc()
    {
        var log = _paths.PlayerLogFile;
        return log is not null && File.Exists(log) ? File.GetLastWriteTimeUtc(log) : null;
    }

    private static DateTime FolderCreated(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetCreationTimeUtc(folder) : DateTime.MinValue;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private void ApplyProfile(ModItemViewModel item, ModProfile? profile, ModNote note)
    {
        item.Profile = profile;
        var automatic = profile?.Categories ?? [];
        // A mod many others need is a library, whatever its name says.
        var library = _installed.Values.Count(m => m.Dependencies.Any(d => d.PackageId == item.Mod.DeclaredPackageId)) >= 3
            ? [ModCategory.Library] : Array.Empty<ModCategory>();
        item.Categories = ModNotesStore.Apply(automatic.Concat(library), note);
        ScheduleLoadTimeRefresh();
    }

    private async Task AnalyzeModsAsync()
    {
        if (_profilesRunning) return;
        _profilesRunning = true;
        try
        {
            var mods = _installed.Values.ToList();
            var version = _gameVersion ?? "1.6";
            var scanned = await Task.Run(() => _profiles.Refresh(mods, version, share =>
                Dispatcher.UIThread.Post(() => AnalysisText = Loc.Format("Analysis.Progress", (int)(share * 100)))));
            if (scanned > 0) await Task.Run(_profiles.Save);

            // Saves are read once per start; they say which mods colonies depend on.
            if (_saveUsage.Count == 0) _saveUsage = await Task.Run(() => SaveUsage.Find(_paths.ConfigDir));

            foreach (var item in _items.Values.Where(i => !i.IsMissing))
                ApplyProfile(item, _profiles.Get(item.Mod), _notes.Get(item.Id));
            UpdateChipCounts();
            RefreshAvailableView();
            RefreshActiveView();
            UpdateSelectedInfo();
            UpdateFooterSize();
            if (!_webCheckDone) _ = CheckUpdatesOnlineAsync(); // finds mods deleted from the Workshop
        }
        catch (Exception)
        {
            // knowledge is a bonus; the mod list works without it
        }
        finally
        {
            AnalysisText = null;
            _profilesRunning = false;
        }
    }

    private void UpdateFooterSize()
    {
        var size = _active.Sum(i => i.Profile?.SizeBytes ?? 0);
        FooterCounts = Loc.Format("Footer.Counts", _installed.Count, _active.Count)
                       + (size > 0 ? " · " + Loc.Format("Footer.Size", Humanize.Size(size)) : "");
    }

    // ───────────────────────── Selected mod: contents, categories, star, note ─────────────────────────

    private void UpdateSelectedInfo()
    {
        SelectedCategories.Clear();
        SelectedContentText = "";
        SelectedSizeText = "";
        _noteLoading = true;
        try
        {
            if (SelectedMod is { } item && !item.IsMissing)
            {
                var note = _notes.Get(item.Id);
                SelectedNote = note.Note ?? "";
                SelectedIsFavorite = note.Favorite;
                foreach (var c in item.Categories)
                    SelectedCategories.Add(new CategoryTag(c, Loc.Get("Cat." + c), note.Added.Contains(c)));
                foreach (var chip in CategoryEditChips) chip.IsChecked = item.Categories.Contains(chip.Category!.Value);
                if (item.Profile is { } p)
                {
                    SelectedContentText = ContentSummary(p);
                    SelectedSizeText = Humanize.Size(p.SizeBytes);
                }
            }
            else
            {
                SelectedNote = "";
                SelectedIsFavorite = false;
            }
        }
        finally
        {
            _noteLoading = false;
        }
        OnPropertyChanged(nameof(HasSelectedCategories));
        OnPropertyChanged(nameof(SelectedIsNew));
        OnPropertyChanged(nameof(SelectedIsUpdated));
        OnPropertyChanged(nameof(CanEditSelected));
    }

    /// <summary>"Weapons 24 · Apparel 8 · Races 1" — only what is there.</summary>
    private static string ContentSummary(ModProfile p)
    {
        var parts = new (string Key, int Count)[]
        {
            ("Weapons", p.Weapons), ("Apparel", p.Apparel), ("Races", p.Races), ("Xenotypes", p.Xenotypes), ("Genes", p.Genes),
            ("Animals", p.Animals), ("Mechanoids", p.Mechanoids), ("Buildings", p.Buildings), ("Plants", p.Plants),
            ("Food", p.Food), ("Medical", p.Medical), ("Research", p.Research), ("Factions", p.Factions),
            ("Events", p.Events), ("Vehicles", p.Vehicles),
        }.Where(x => x.Count > 0).Select(x => Loc.Format("Content." + x.Key, x.Count)).ToList();
        if (parts.Count == 0)
        {
            if (p.Assemblies > 0) parts.Add(Loc.Get("Content.CodeOnly"));
            else if (p.Patches > 0) parts.Add(Loc.Format("Content.Patches", p.Patches));
            else if (p.Textures > 0) parts.Add(Loc.Format("Content.Textures", p.Textures));
            else if (p.HasLanguages) parts.Add(Loc.Get("Content.Languages"));
        }
        return string.Join(" · ", parts);
    }

    partial void OnSelectedIsFavoriteChanged(bool value)
    {
        if (_noteLoading || SelectedMod is not { IsMissing: false } item) return;
        var note = _notes.Get(item.Id);
        note.Favorite = value;
        _notes.Set(item.Id, note);
        item.IsFavorite = value;
        UpdateChipCounts();
        if (HasActiveFilters) OnFiltersChanged();
    }

    private DispatcherTimer? _noteTimer;

    partial void OnSelectedNoteChanged(string value)
    {
        if (_noteLoading || SelectedMod is not { IsMissing: false } item) return;
        // Saved a moment after typing stops.
        _noteTimer?.Stop();
        _noteTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _noteTimer?.Stop();
            var note = _notes.Get(item.Id);
            note.Note = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _notes.Set(item.Id, note);
        });
        _noteTimer.Start();
    }

    [RelayCommand]
    private void ToggleFavorite() => SelectedIsFavorite = !SelectedIsFavorite;

    [RelayCommand]
    private void RemoveCategory(CategoryTag tag) => SetCategory(tag.Category, false);

    /// <summary>The player says this mod is (or is not) of this category; remembered for good.</summary>
    private void SetCategory(ModCategory category, bool on)
    {
        if (SelectedMod is not { IsMissing: false } item) return;
        if (item.Categories.Contains(category) == on) return;
        var note = _notes.Get(item.Id);
        var automatic = item.Profile?.Categories ?? [];
        note.Added.Remove(category);
        note.Removed.Remove(category);
        if (on && !automatic.Contains(category)) note.Added.Add(category);
        if (!on && automatic.Contains(category)) note.Removed.Add(category);
        _notes.Set(item.Id, note);
        ApplyProfile(item, item.Profile, note);
        UpdateChipCounts();
        UpdateSelectedInfo();
        if (HasActiveFilters) OnFiltersChanged();
    }
}
