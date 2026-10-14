using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Models;
using RimForge.Core.Profiles;
using RimForge.Core.Sharing;
using RimForge.Core.Storage;

namespace RimForge.App.ViewModels;

/// <summary>
/// Sharing modpacks as <c>.rimforge</c> files: saving one (list and order, optionally the mods' settings and
/// the mods themselves) and opening one (summary → new modpack → downloads, unpacking, settings).
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Set by the window: asks where to save (suggested file name) / which file to open.</summary>
    public Func<string, Task<string?>>? PickSaveFile { get; set; }
    public Func<Task<string?>>? PickOpenFile { get; set; }

    private ModSettingsInstaller SettingsInstaller(string? stamp = null) => new(
        _paths.ConfigDir ?? "",
        Path.Combine(AppDataPaths.BackupsDir, "mod-settings", stamp ?? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")),
        Path.Combine(AppDataPaths.Root, "pending-settings.json"));

    // ───────────────────────── Saving a modpack to a file ─────────────────────────

    [ObservableProperty] public partial bool IsShareOpen { get; set; }
    [ObservableProperty] public partial string ShareTitle { get; set; } = "";
    [ObservableProperty] public partial string ShareDescription { get; set; } = "";
    [ObservableProperty] public partial bool ShareIncludeSettings { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareSizeText))]
    public partial bool ShareIncludeMods { get; set; }

    [ObservableProperty] public partial string ShareSettingsText { get; set; } = "";
    [ObservableProperty] public partial bool ShareHasSettings { get; set; }
    [ObservableProperty] public partial string ShareModsText { get; set; } = "";
    [ObservableProperty] public partial string ShareLocalText { get; set; } = "";
    [ObservableProperty] public partial bool ShareHasLocal { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShareSave))]
    public partial bool IsSharing { get; set; }

    [ObservableProperty] public partial double ShareProgress { get; set; }

    private ProfileViewModel? _shareProfile;
    private List<string> _shareOrder = [];
    private long _shareModsBytes;
    private CancellationTokenSource? _shareCancel;

    public bool CanShareSave => !IsSharing;
    public string ShareSizeText => ShareIncludeMods ? Loc.Format("Share.FullSize", Humanize.Size(_shareModsBytes)) : Loc.Get("Share.LightSize");

    [RelayCommand]
    private void ShareProfile(ProfileViewModel profile)
    {
        _shareProfile = profile;
        // The active modpack is shared as it is on screen, unsaved changes included.
        _shareOrder = profile == ActiveProfile ? Snapshot() : profile.Model.ModIds.ToList();
        var mods = _shareOrder.Select(id => _installed.GetValueOrDefault(id)).OfType<ModInfo>().Where(m => !m.IsOfficial).ToList();

        var withSettings = mods.Count(m => ModpackFile.SettingsFilesOf(m, _paths.ConfigDir).Count > 0);
        ShareHasSettings = withSettings > 0;
        ShareSettingsText = Loc.Format("Share.Settings", withSettings);
        ShareIncludeSettings = ShareHasSettings;

        _shareModsBytes = mods.Sum(m => _profiles.Get(m)?.SizeBytes ?? 0);
        ShareModsText = Loc.Format("Share.Mods", mods.Count, Humanize.Size(_shareModsBytes));
        ShareIncludeMods = false;

        var local = mods.Where(m => m.Source == ModSource.Local).ToList();
        ShareHasLocal = local.Count > 0;
        ShareLocalText = Loc.Format("Share.Local", local.Count, Names(local.Select(m => new ModpackEntry { Id = m.PackageId, Name = m.Name }), 4));

        ShareTitle = Loc.Format("Share.Title", profile.Name);
        ShareDescription = profile.Model.Notes is { } notes && !notes.StartsWith("http") && !File.Exists(notes) ? notes : "";
        ShareProgress = 0;
        IsShareOpen = true;
        OnPropertyChanged(nameof(ShareSizeText));
    }

    [RelayCommand]
    private void CloseShare()
    {
        if (IsSharing) _shareCancel?.Cancel();
        else IsShareOpen = false;
    }

    [RelayCommand]
    private async Task ShareSave()
    {
        if (_shareProfile is not { } profile || PickSaveFile is null || IsSharing) return;
        var path = await PickSaveFile(ModpackFile.SafeName(profile.Name) + ModpackFile.Extension);
        if (path is null) return;
        if (!path.EndsWith(ModpackFile.Extension, StringComparison.OrdinalIgnoreCase)) path += ModpackFile.Extension;

        var manifest = ModpackFile.Build(profile.Name, ShareDescription, _shareOrder, _installed, profile.Model.Mods.ToDictionary(m => m.Id),
            _paths.ReadFullGameVersion(), profile.Model.Rules,
            m => m.WorkshopId is { } w && _manifest.TryGetValue(w, out var item) ? item.UpdatedUtc : null,
            m => _profiles.Get(m)?.SizeBytes);
        manifest.PinTop = profile.Model.PinTop.Count > 0 ? profile.Model.PinTop.ToList() : null;
        manifest.PinBottom = profile.Model.PinBottom.Count > 0 ? profile.Model.PinBottom.ToList() : null;

        IsSharing = true;
        ShareProgress = 0;
        _shareCancel = new CancellationTokenSource();
        var includeSettings = ShareIncludeSettings && ShareHasSettings;
        var includeMods = ShareIncludeMods;
        try
        {
            var progress = new Progress<double>(p => ShareProgress = p);
            var token = _shareCancel.Token;
            await Task.Run(() =>
            {
                var parts = ModpackFile.Prepare(manifest, _installed, _paths.ConfigDir, includeSettings, includeMods);
                ModpackFile.Write(path, manifest, parts, progress, token);
            }, token);
            IsShareOpen = false;
            ShowToast(Loc.Format("Share.Saved", Path.GetFileName(path), Humanize.Size(new FileInfo(path).Length)));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            ShowToast(Loc.Get("Share.Cancelled"));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.Format("Share.Failed", ex.Message));
        }
        finally
        {
            IsSharing = false;
            _shareCancel.Dispose();
            _shareCancel = null;
        }
    }

    // ───────────────────────── Opening a modpack file ─────────────────────────

    [ObservableProperty] public partial bool IsPackOpen { get; set; }
    [ObservableProperty] public partial string PackTitle { get; set; } = "";
    [ObservableProperty] public partial string PackInfo { get; set; } = "";
    [ObservableProperty] public partial string? PackDescription { get; set; }
    [ObservableProperty] public partial string PackHaveText { get; set; } = "";
    [ObservableProperty] public partial string PackDownloadText { get; set; } = "";
    [ObservableProperty] public partial bool PackHasDownload { get; set; }
    [ObservableProperty] public partial string PackFromFileText { get; set; } = "";
    [ObservableProperty] public partial bool PackHasFromFile { get; set; }
    [ObservableProperty] public partial string PackUnavailableText { get; set; } = "";
    [ObservableProperty] public partial bool PackHasUnavailable { get; set; }
    [ObservableProperty] public partial string PackDlcText { get; set; } = "";
    [ObservableProperty] public partial bool PackHasMissingDlc { get; set; }
    [ObservableProperty] public partial string PackVersionText { get; set; } = "";
    [ObservableProperty] public partial bool PackHasVersionWarning { get; set; }
    [ObservableProperty] public partial string PackUpdatedText { get; set; } = "";
    [ObservableProperty] public partial bool PackHasUpdated { get; set; }
    [ObservableProperty] public partial string PackRemovedText { get; set; } = "";
    [ObservableProperty] public partial bool PackHasRemoved { get; set; }
    [ObservableProperty] public partial string PackSettingsText { get; set; } = "";
    [ObservableProperty] public partial bool PackHasSettings { get; set; }
    [ObservableProperty] public partial bool PackApplySettings { get; set; } = true;
    [ObservableProperty] public partial bool IsPackChecking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateFromPack))]
    public partial bool IsPackBusy { get; set; }

    [ObservableProperty] public partial double PackProgress { get; set; }
    [ObservableProperty] public partial string PackCreateText { get; set; } = "";

    public bool CanCreateFromPack => !IsPackBusy;

    private string? _packPath;
    private ModpackManifest? _pack;
    private ModpackPlan? _packPlan;
    private HashSet<ulong> _packRemoved = [];

    [RelayCommand]
    private async Task OpenModpackFile()
    {
        if (PickOpenFile is null || await PickOpenFile() is not { } path) return;
        await OpenModpackAsync(path);
    }

    /// <summary>Shows what a modpack file brings (from the button, a dropped file or the command line).</summary>
    public async Task OpenModpackAsync(string path)
    {
        if (IsPackBusy) return;
        ModpackManifest manifest;
        try
        {
            manifest = await Task.Run(() => ModpackFile.Read(path));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ShowToast(Loc.Format("Pack.Bad", Path.GetFileName(path), ex.Message));
            return;
        }

        _packPath = path;
        _pack = manifest;
        _packRemoved = [];
        CurrentPage = "profiles";
        PackTitle = manifest.Name;
        PackDescription = manifest.Description;
        PackInfo = Loc.Format("Pack.Info", Dates.Format(manifest.CreatedUtc.ToLocalTime()), manifest.GameVersion ?? "?", manifest.Mods.Count);
        PackApplySettings = true;
        PackProgress = 0;
        RefreshPackSummary();
        IsPackOpen = true;

        // Sizes, mods taken off the Workshop and mods updated since: from the Workshop, without holding the window.
        var workshopIds = manifest.Mods.Where(m => m.WorkshopId is not null).Select(m => m.WorkshopId!.Value).Distinct().ToList();
        if (workshopIds.Count == 0) return;
        IsPackChecking = true;
        try
        {
            var details = await _web.GetDetailsAsync(workshopIds);
            if (_pack != manifest) return;
            _packRemoved = details.Where(d => d.Removed).Select(d => d.Id).ToHashSet();
            var latest = details.Where(d => !d.Removed).ToDictionary(d => d.Id, d => d.UpdatedUtc);
            var sizes = details.ToDictionary(d => d.Id, d => d.SizeBytes);
            RefreshPackSummary(sizes);

            var updated = ModpackPlan.UpdatedSince(manifest, latest);
            PackHasUpdated = updated.Count > 0;
            PackUpdatedText = Loc.Format("Pack.Updated", updated.Count, Names(updated));
        }
        catch (Exception)
        {
            // Offline: the summary simply has no sizes.
        }
        finally
        {
            IsPackChecking = false;
        }
    }

    private static string Names(IEnumerable<ModpackEntry> mods, int max = 5)
    {
        var list = mods.Select(m => m.Name ?? m.Id).ToList();
        return string.Join(", ", list.Take(max)) + (list.Count > max ? $" … (+{list.Count - max})" : "");
    }

    private void RefreshPackSummary(IReadOnlyDictionary<ulong, long>? sizes = null)
    {
        if (_pack is not { } pack) return;
        var plan = _packPlan = ModpackPlan.For(pack, _installed.Values.ToList());
        var download = plan.Download.Where(m => !_packRemoved.Contains(m.WorkshopId!.Value)).ToList();
        var removed = plan.Download.Where(m => _packRemoved.Contains(m.WorkshopId!.Value)).ToList();

        PackHaveText = Loc.Format("Pack.Have", plan.Have.Count);
        PackHasDownload = download.Count > 0;
        var downloadBytes = download.Sum(m => sizes?.GetValueOrDefault(m.WorkshopId!.Value) ?? m.SizeBytes ?? 0);
        PackDownloadText = Loc.Format("Pack.Download", download.Count, downloadBytes > 0 ? Humanize.Size(downloadBytes) : "?");
        PackHasFromFile = plan.FromFile.Count > 0;
        PackFromFileText = Loc.Format("Pack.FromFile", plan.FromFile.Count, Humanize.Size(plan.FromFile.Sum(m => m.SizeBytes ?? 0)));
        PackHasUnavailable = plan.Unavailable.Count > 0;
        PackUnavailableText = Loc.Format("Pack.Unavailable", plan.Unavailable.Count, Names(plan.Unavailable));
        PackHasRemoved = removed.Count > 0;
        PackRemovedText = Loc.Format("Pack.Removed", removed.Count, Names(removed));
        PackHasMissingDlc = plan.MissingDlcs.Count > 0;
        PackDlcText = Loc.Format("Pack.Dlc", Names(plan.MissingDlcs.Select(d => new ModpackEntry { Id = d.Id, Name = DlcName(d) })));
        var gameVersion = pack.GameVersion?.Split(' ')[0];
        PackHasVersionWarning = gameVersion is not null && _gameVersion is not null && !gameVersion.StartsWith(_gameVersion + ".") && gameVersion != _gameVersion;
        PackVersionText = Loc.Format("Pack.Version", gameVersion ?? "?", _gameVersion ?? "?");
        PackHasSettings = pack.SettingsCount > 0;
        PackSettingsText = Loc.Format("Pack.Settings", pack.SettingsCount);
        PackCreateText = Loc.Get(download.Count > 0 ? "Pack.CreateAndDownload" : "Pack.Create");
    }

    private static string DlcName(ModpackEntry dlc)
    {
        if (dlc.Name is { } known) return known;
        var name = dlc.Id.Replace("ludeon.rimworld.", "");
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : dlc.Id;
    }

    [RelayCommand]
    private void ClosePack()
    {
        if (IsPackBusy) return;
        IsPackOpen = false;
        _pack = null;
    }

    [RelayCommand]
    private async Task CreateFromPack()
    {
        if (_pack is not { } pack || _packPath is not { } path || IsPackBusy) return;
        if (!await ConfirmLeaveAsync()) return;
        IsPackBusy = true;
        try
        {
            // 1. Mods packed into the file go to the game's Mods folder.
            var plan = _packPlan ?? ModpackPlan.For(pack, _installed.Values.ToList());
            if (plan.FromFile.Count > 0)
            {
                if (_paths.LocalModsDir is not { } modsDir)
                {
                    ShowToast(Loc.Get("Pack.NoGameDir"));
                    return;
                }
                Directory.CreateDirectory(modsDir);
                var total = Math.Max(1, plan.FromFile.Sum(m => m.SizeBytes ?? 0));
                long done = 0;
                var progress = new Progress<long>(b => PackProgress = Math.Min(1, (double)(done += b) / total));
                await Task.Run(() =>
                {
                    foreach (var entry in plan.FromFile) ModpackFile.ExtractMod(path, entry, modsDir, progress);
                });
                await LoadAsync();
                plan = ModpackPlan.For(pack, _installed.Values.ToList());
            }

            // 2. The modpack itself, in the order it was made with.
            var hints = pack.Mods.GroupBy(m => m.Id.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => new ProfileMod(g.Key, g.First().Name, g.First().WorkshopId));
            var profile = new Profile { Name = UniqueProfileName(pack.Name), Notes = pack.Description, Rules = pack.Rules.ToList(),
                PinTop = pack.PinTop?.ToList() ?? [], PinBottom = pack.PinBottom?.ToList() ?? [] };
            profile.SetMods(plan.Order, _installed, hints);
            Apply(AddProfile(profile));

            // 3. The mods' settings: now for mods that are here, after the download for the others.
            var settingsApplied = 0;
            if (PackApplySettings && pack.SettingsCount > 0 && _paths.ConfigDir is not null)
            {
                var installer = SettingsInstaller();
                var downloading = plan.Download.ToHashSet();
                foreach (var entry in pack.Mods.Where(m => m.Settings is { Count: > 0 }))
                {
                    var settings = ModpackFile.ReadSettings(path, entry);
                    if (plan.Have.TryGetValue(entry, out var mod)) settingsApplied += installer.Apply(mod.FolderPath, settings) > 0 ? 1 : 0;
                    else if (downloading.Contains(entry)) installer.AddPending(entry.Id, settings);
                }
            }

            IsPackOpen = false;
            CurrentPage = "mods";
            var download = plan.Download.Where(m => !_packRemoved.Contains(m.WorkshopId!.Value)).ToList();
            ShowToast(Loc.Format("Pack.Created", profile.Name, plan.Have.Count, download.Count, settingsApplied));
            if (download.Count > 0)
                await StartDownloadsAsync(download.Select(m => new DownloadViewModel(m.WorkshopId!.Value, m.Name ?? m.Id)).ToList());
        }
        catch (Exception ex)
        {
            ShowToast(Loc.Format("Pack.Failed", ex.Message));
        }
        finally
        {
            IsPackBusy = false;
            _pack = null;
        }
    }

    private string UniqueProfileName(string name)
    {
        var taken = Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{name} ({n})")) return $"{name} ({n})";
    }

    /// <summary>After downloads: settings from a shared modpack that were waiting for their mods.</summary>
    private void ApplyPendingSettings()
    {
        if (_paths.ConfigDir is null) return;
        try
        {
            var applied = SettingsInstaller().ApplyPending(_installed.Values);
            if (applied.Count > 0) ShowToast(Loc.Format("Pack.SettingsApplied", applied.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(Loc.Format("Pack.Failed", ex.Message));
        }
    }
}
