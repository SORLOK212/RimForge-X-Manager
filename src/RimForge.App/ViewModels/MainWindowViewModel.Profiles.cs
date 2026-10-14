using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Launch;
using RimForge.Core.Models;
using RimForge.Core.Profiles;
using RimForge.Core.Saves;
using RimForge.Core.Storage;

namespace RimForge.App.ViewModels;

/// <summary>Modpacks ("сборки"): switching, creating, renaming, deleting and importing from saves.</summary>
public sealed partial class MainWindowViewModel
{
    // Official expansions in the order the game itself loads them.
    private static readonly string[] OfficialOrder =
    [
        KnownMods.Core, "ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "ludeon.rimworld.biotech",
        "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey",
    ];

    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];
    public ObservableCollection<ImportSourceViewModel> ImportSources { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModsPage), nameof(IsProfilesPage), nameof(IsDiagnosticsPage), nameof(IsWorkshopPage), nameof(IsSettingsPage), nameof(IsAssistantPage))]
    public partial string CurrentPage { get; set; }

    public bool IsModsPage => CurrentPage == "mods";
    public bool IsProfilesPage => CurrentPage == "profiles";
    public bool IsDiagnosticsPage => CurrentPage == "diagnostics";
    public bool IsWorkshopPage => CurrentPage == "workshop";
    public bool IsSettingsPage => CurrentPage == "settings";
    public bool IsAssistantPage => CurrentPage == "assistant";

    [ObservableProperty] public partial ProfileViewModel? ActiveProfile { get; set; }
    [ObservableProperty] public partial string ActiveProfileName { get; set; }
    [ObservableProperty] public partial DialogViewModel? Dialog { get; set; }
    [ObservableProperty] public partial bool IsImportOpen { get; set; }
    [ObservableProperty] public partial bool ImportIsEmpty { get; set; }

    [RelayCommand]
    private void ShowPage(string page)
    {
        CurrentPage = page;
        if (page == "settings")
        {
            RefreshAiClients();
            RefreshBrainModelState();
            _ = DetectLocalServersAsync();
        }
    }

    /// <summary>Esc closes whatever floats on top.</summary>
    [RelayCommand]
    private void Escape()
    {
        if (Dialog is not null) Dialog.Cancel();
        else if (FullGallery is not null) CloseGalleryFull();
        else if (IsLoadTimeOpen) IsLoadTimeOpen = false;
        else if (IsPackOpen) ClosePack();
        else if (IsShareOpen) CloseShare();
        else if (IsImportOpen) IsImportOpen = false;
        else if (IsSortOpen) CloseSort();
        else if (IsFixAllOpen) CloseFixAll();
        else if (IsRulePickerOpen) CloseRulePicker();
        else if (IsBisectOpen) HideBisect();
        else if (IsWorkshopPage && SelectedCard is not null) SelectedCard = null;
    }

    private void LoadProfiles(List<string> gameOrder)
    {
        var profiles = _store.LoadAll();
        if (profiles.Count == 0)
        {
            // First start: the list that is in the game right now becomes the first modpack.
            var first = new Profile { Name = Loc.Get("Profile.DefaultName") };
            first.SetMods(gameOrder, _installed);
            _store.Save(first);
            profiles.Add(first);
        }

        Profiles.Clear();
        foreach (var p in profiles) Profiles.Add(new ProfileViewModel(p));
        var active = Profiles.FirstOrDefault(p => p.Model.Id == _settings.ActiveProfileId) ?? Profiles[0];
        SetActive(active);
    }

    private void SetActive(ProfileViewModel profile)
    {
        // Placeholders of mods that have arrived since the modpack was saved become the real mods.
        var resolved = ResolvePlaceholders(profile.Model.ModIds);
        if (!resolved.SequenceEqual(profile.Model.ModIds))
        {
            profile.Model.SetMods(resolved, _installed, profile.Model.Mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First()));
            _store.Save(profile.Model);
        }
        foreach (var p in Profiles)
        {
            p.IsActive = p == profile;
            p.Refresh(IsInstalled);
        }
        ActiveProfile = profile;
        ActiveProfileName = profile.Name;
        _savedOrder = profile.Model.ModIds.ToList();
        _hints = profile.Model.Mods.ToDictionary(m => m.Id);

        // Placeholders for missing mods are rebuilt so they pick up this modpack's names.
        foreach (var missing in _items.Where(kv => kv.Value.IsMissing).Select(kv => kv.Key).ToList())
            _items.Remove(missing);

        if (_settings.ActiveProfileId != profile.Model.Id)
        {
            _settings.ActiveProfileId = profile.Model.Id;
            _settings.Save();
        }
    }

    private async Task<string> AskAsync(DialogViewModel dialog)
    {
        Dialog = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            Dialog = null;
        }
    }

    /// <summary>Offers to save unsaved changes. Returns false if the user cancelled.</summary>
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!IsDirty || ActiveProfile is null) return true;
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Dialog.UnsavedTitle"),
            Text = Loc.Format("Dialog.UnsavedMessage", ActiveProfile.Name),
            PrimaryText = Loc.Get("Dialog.Save"),
            SecondaryText = Loc.Get("Dialog.DontSave"),
        });
        return answer switch
        {
            "primary" => SaveNow(),
            "secondary" => true,
            _ => false,
        };
    }

    private ProfileSettings? _profileSettings;
    private ProfileSettings ProfileSettingsStore => _profileSettings ??= new ProfileSettings(_paths.ConfigDir ?? "", ProfileSettingsDir, AppDataPaths.BackupsDir);

    /// <summary>Every modpack keeps its own mod settings.</summary>
    public bool ProfileModSettings
    {
        get => _settings.ProfileModSettings;
        set
        {
            if (_settings.ProfileModSettings == value) return;
            _settings.ProfileModSettings = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Remembers the mod settings of the modpack being left and puts in those of the new one.
    /// Returns a note for the toast ("settings restored" / "game is running, settings left as they are").
    /// </summary>
    private string SwitchModSettings(ProfileViewModel? from, ProfileViewModel to)
    {
        if (!_settings.ProfileModSettings || _paths.ConfigDir is null || from == to) return "";
        // The game writes its settings when it closes and would undo the swap.
        if (GameLauncher.IsRunning()) return " " + Loc.Get("Msg.SettingsGameRunning");
        try
        {
            return ProfileSettingsStore.Switch(from?.Model.Id, to.Model.Id) ? " " + Loc.Get("Msg.SettingsRestored") : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return " " + Loc.Format("Msg.SettingsFailed", ex.Message);
        }
    }

    /// <summary>Makes <paramref name="profile"/> active and writes its list to the game.</summary>
    private void Apply(ProfileViewModel profile)
    {
        var settingsNote = SwitchModSettings(ActiveProfile, profile);
        SetActive(profile);
        _undo.Clear();
        _redo.Clear();
        if (_config is not null && _paths.ModsConfigFile is not null)
        {
            try
            {
                WriteGameList(profile.Model.ModIds.ToList());
            }
            catch (Exception ex)
            {
                Message = Loc.Format("Msg.Error", ex.Message);
            }
        }
        Restore(profile.Model.ModIds);
        ShowToast(Loc.Format("Msg.ProfileApplied", profile.Name) + settingsNote);
    }

    [RelayCommand]
    private async Task SwitchProfile(ProfileViewModel profile)
    {
        if (profile == ActiveProfile) return;
        if (!await ConfirmLeaveAsync()) return;
        Apply(profile);
    }

    [RelayCommand]
    private async Task NewProfile()
    {
        var dialog = new DialogViewModel
        {
            Title = Loc.Get("Dialog.NewTitle"),
            Text = Loc.Get("Dialog.NewMessage"),
            HasInput = true,
            InputHint = Loc.Get("Dialog.NameHint"),
            PrimaryText = Loc.Get("Dialog.Create"),
        };
        dialog.Input = Loc.Get("Profile.NewDefaultName");
        if (await AskAsync(dialog) != "primary") return;
        if (!await ConfirmLeaveAsync()) return;

        // A fresh modpack starts with the game and every installed official expansion.
        var profile = new Profile { Name = dialog.Input.Trim() };
        profile.SetMods(OfficialOrder.Where(_installed.ContainsKey), _installed);
        Apply(AddProfile(profile));
        CurrentPage = "mods";
    }

    [RelayCommand]
    private void DuplicateProfile(ProfileViewModel source)
    {
        var copy = new Profile
        {
            Name = Loc.Format("Profile.CopySuffix", source.Name),
            Notes = source.Model.Notes,
            Rules = source.Model.Rules.ToList(),
            PinTop = source.Model.PinTop.ToList(),
            PinBottom = source.Model.PinBottom.ToList(),
            Ignored = source.Model.Ignored.ToList(),
        };
        // Copying the active modpack takes what is on screen, including unsaved changes.
        var ids = source == ActiveProfile ? Snapshot() : source.Model.ModIds.ToList();
        copy.SetMods(ids, _installed, source.Model.Mods.ToDictionary(m => m.Id));
        AddProfile(copy);
        ShowToast(Loc.Format("Msg.ProfileCreated", copy.Name));
    }

    [RelayCommand]
    private async Task RenameProfile(ProfileViewModel profile)
    {
        var dialog = new DialogViewModel
        {
            Title = Loc.Get("Dialog.RenameTitle"),
            HasInput = true,
            InputHint = Loc.Get("Dialog.NameHint"),
            PrimaryText = Loc.Get("Dialog.Rename"),
        };
        dialog.Input = profile.Name;
        if (await AskAsync(dialog) != "primary") return;

        profile.Model.Name = dialog.Input.Trim();
        _store.Save(profile.Model);
        profile.Refresh(IsInstalled);
        if (profile == ActiveProfile) ActiveProfileName = profile.Name;
    }

    [RelayCommand]
    private async Task DeleteProfile(ProfileViewModel profile)
    {
        if (Profiles.Count == 1)
        {
            ShowToast(Loc.Get("Msg.CantDeleteLast"));
            return;
        }
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Dialog.DeleteTitle"),
            Text = Loc.Format("Dialog.DeleteMessage", profile.Name),
            PrimaryText = Loc.Get("Dialog.Delete"),
            PrimaryIsDanger = true,
        });
        if (answer != "primary") return;

        if (profile == ActiveProfile)
        {
            IsDirty = false; // its unsaved changes go away with it
            Apply(Profiles.First(p => p != profile));
        }
        _store.Delete(profile.Model);
        try
        {
            ProfileSettingsStore.Forget(profile.Model.Id);
        }
        catch (IOException)
        {
            // A leftover copy of settings does no harm.
        }
        Profiles.Remove(profile);
    }

    private ProfileViewModel AddProfile(Profile profile)
    {
        _store.Save(profile);
        var vm = new ProfileViewModel(profile);
        vm.Refresh(IsInstalled);
        Profiles.Add(vm);
        return vm;
    }

    // ── Modpack from a save or a game mod list ──

    [RelayCommand]
    private async Task OpenImport()
    {
        ImportSources.Clear();
        foreach (var source in ModListMetaReader.FindSources(_paths.ConfigDir))
            ImportSources.Add(new ImportSourceViewModel(source));
        ImportIsEmpty = ImportSources.Count == 0;
        IsImportOpen = true;

        // Saves can be big; only their header is read, in the background.
        foreach (var item in ImportSources.ToList())
        {
            try
            {
                var meta = await Task.Run(() => ModListMetaReader.Read(item.Source.Path));
                item.SetMeta(meta, IsInstalled);
            }
            catch (Exception)
            {
                ImportSources.Remove(item);
            }
        }
    }

    [RelayCommand]
    private void CloseImport() => IsImportOpen = false;

    [RelayCommand]
    private async Task ImportFrom(ImportSourceViewModel source)
    {
        if (source.Meta is not { } meta) return;
        IsImportOpen = false;
        if (!await ConfirmLeaveAsync()) return;

        var profile = new Profile { Name = source.Name, Notes = source.Source.Path };
        profile.SetMods(meta.Mods.Select(m => m.Id), _installed, meta.Mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First()));
        var vm = AddProfile(profile);
        Apply(vm);
        var missing = profile.ModIds.Count(id => !IsInstalled(id));
        ShowToast(Loc.Format("Msg.ProfileImported", profile.Name, profile.Mods.Count, missing));
        CurrentPage = "mods";
    }
}
