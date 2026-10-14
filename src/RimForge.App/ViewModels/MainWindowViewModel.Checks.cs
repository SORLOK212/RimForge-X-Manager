using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Checks;
using RimForge.Core.Databases;
using RimForge.Core.Models;
using RimForge.Core.Sorting;
using RimForge.Core.Storage;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

/// <summary>One action of "Fix everything": the player can untick it before applying.</summary>
public sealed partial class FixAllRow : ObservableObject
{
    public required string Text { get; init; }
    public string? Note { get; init; }
    public bool HasNote => Note is not null;

    /// <summary>Changes the mod list (runs inside one undo step).</summary>
    public Action? ListChange { get; init; }
    public DownloadViewModel? Download { get; init; }

    [ObservableProperty] public partial bool IsChecked { get; set; } = true;
}

/// <summary>
/// The traffic light's helpers: community databases (Use This Instead, No Version Warning), "Fix everything",
/// replacing abandoned mods and the player's own pins and order rules.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private ModReplacements _replacements = ModReplacements.Empty;
    private VerifiedMods _verified = VerifiedMods.Empty;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _duplicateFolders = new Dictionary<string, IReadOnlyList<string>>();
    private readonly Dictionary<string, bool> _hasCode = new();
    private bool _verifiedLoading;

    public ObservableCollection<FixAllRow> FixAllRows { get; } = [];
    public ObservableCollection<string> FixAllManual { get; } = [];

    [ObservableProperty] public partial bool IsFixAllOpen { get; set; }
    [ObservableProperty] public partial string FixAllSummary { get; set; } = "";
    [ObservableProperty] public partial string DatabasesStatus { get; set; } = "";

    public bool HasFixAllRows => FixAllRows.Count > 0;
    public bool HasFixAllManual => FixAllManual.Count > 0;
    public bool CanFixAll => _issues.Any(i => i.Severity != IssueSeverity.Info || i.Kind == IssueKind.UseInstead);

    private string CacheDir => Path.Combine(AppDataPaths.Root, "cache");

    private bool IsUnsubscribed(ModInfo mod) => mod.WorkshopId is { } w && _unsubscribed.Contains(w);

    private static string DuplicateLabel(ModInfo mod) => mod.Source switch
    {
        ModSource.Workshop => $"«{mod.Name}» ({Loc.Get("Badge.Workshop")} {mod.WorkshopId})",
        ModSource.Local => $"«{mod.Name}» ({Loc.Get("Badge.Local")}: {Path.GetFileName(mod.FolderPath)})",
        _ => $"«{mod.Name}»",
    };

    private CheckExtras CheckExtras()
    {
        if (_gameVersion is { } version && _verified.GameVersion != version && !_verifiedLoading)
            _ = LoadVerifiedAsync(version, force: false);
        return new CheckExtras
        {
            Verified = _verified.GameVersion == _gameVersion ? _verified : null,
            Replacements = _replacements,
            DuplicateFolders = _duplicateFolders,
            HasCode = HasCode,
        };
    }

    /// <summary>Whether the mod ships DLLs of its own (Harmony itself does not count). Cached per folder.</summary>
    private bool HasCode(ModInfo mod)
    {
        if (_hasCode.TryGetValue(mod.FolderPath, out var known)) return known;
        bool result;
        try
        {
            result = Directory.EnumerateFiles(mod.FolderPath, "*.dll", SearchOption.AllDirectories)
                .Any(f => !Path.GetFileName(f).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = true; // unknown: assume the risky case
        }
        return _hasCode[mod.FolderPath] = result;
    }

    // ── Community databases ──

    private async Task LoadDatabasesAsync(bool force)
    {
        try
        {
            _replacements = await ModReplacements.LoadAsync(Path.Combine(CacheDir, "useThisInstead.json.gz"), CommunityMaxAge, force);
        }
        catch (Exception)
        {
            _replacements = ModReplacements.Empty;
        }
        if (_gameVersion is { } version) await LoadVerifiedAsync(version, force, recheck: false);
        UpdateDatabasesStatus();
        Recheck();
        UpdateSelectionDetails();
    }

    private async Task LoadVerifiedAsync(string version, bool force, bool recheck = true)
    {
        _verifiedLoading = true;
        try
        {
            _verified = await VerifiedMods.LoadAsync(CacheDir, version, CommunityMaxAge, force);
            if (_verified.GameVersion is null) _verified = new VerifiedMods(_verified.PackageIds, version);
        }
        catch (Exception)
        {
            _verified = new VerifiedMods(new HashSet<string>(), version);
        }
        finally
        {
            _verifiedLoading = false;
        }
        UpdateDatabasesStatus();
        if (recheck)
        {
            Recheck();
            UpdateSelectionDetails();
        }
    }

    private void UpdateDatabasesStatus() =>
        DatabasesStatus = Loc.Format("Db.Status", _replacements.Count, _verified.Count, _gameVersion ?? "?");

    // ── Messages and one-click fixes for the newer checks ──

    /// <summary>Message and fix for the kinds this file added; null for the others.</summary>
    private (string Message, string? FixText, Action? Fix)? MakeExtraIssue(ModIssue issue, string name, string other)
    {
        switch (issue.Kind)
        {
            case IssueKind.DuplicateActive:
                return (Loc.Format("Issue.DuplicateActive", name), Loc.Get("Fix.RemoveCopy"),
                    () => { if (_items.TryGetValue(issue.ModId, out var copy)) Deactivate([copy]); });
            case IssueKind.DuplicateInstalled:
                return (Loc.Format("Issue.DuplicateInstalled", name, issue.Detail), null, null);
            case IssueKind.DlcMissing:
                return (Loc.Format("Issue.DlcMissing", name, DlcName(issue.OtherId, issue.OtherName)), Loc.Get("Btn.Disable"),
                    () => { if (_items.TryGetValue(issue.ModId, out var mod)) Deactivate([mod]); });
            case IssueKind.OlderVersionLikelyOk:
                return (Loc.Format("Issue.OlderVersionLikelyOk", name, issue.Detail, _gameVersion), null, null);
            case IssueKind.UseInstead when issue.Replacement is { } r:
                var by = r.NewAuthor is { } author ? Loc.Format("Issue.By", author) : "";
                var message = Loc.Format("Issue.UseInstead", name, r.NewName, by);
                if (_byWorkshopId.TryGetValue(r.NewWorkshopId, out var installedNew) && _items.ContainsKey(installedNew.PackageId))
                    return (message, Loc.Get("Fix.Replace"), () => ReplaceMod(issue.ModId, installedNew.PackageId));
                return (message, Loc.Get("Fix.DownloadReplacement"), () => _ = DownloadReplacementAsync(issue.ModId, r));
            default:
                return null;
        }
    }

    private static string DlcName(string? id, string? fallback) => id switch
    {
        "ludeon.rimworld.royalty" => "Royalty",
        "ludeon.rimworld.ideology" => "Ideology",
        "ludeon.rimworld.biotech" => "Biotech",
        "ludeon.rimworld.anomaly" => "Anomaly",
        "ludeon.rimworld.odyssey" => "Odyssey",
        _ => fallback ?? id ?? "?",
    };

    // ── Replacing abandoned mods ──

    /// <summary>Puts the new mod where the old one was in the list and turns the old one off.</summary>
    private void ReplaceMod(string oldId, string newId)
    {
        if (!_items.TryGetValue(oldId, out var oldItem) || !_items.TryGetValue(newId, out var newItem)) return;
        Change(() => SwapInList(oldItem, newItem));
        SortCarefully();
        ShowToast(Loc.Format("Fix.Replaced", oldItem.Name, newItem.Name));
    }

    private void SwapInList(ModItemViewModel oldItem, ModItemViewModel newItem)
    {
        var at = _active.IndexOf(oldItem);
        if (at < 0) return;
        _active.Remove(newItem);
        at = _active.IndexOf(oldItem);
        _active[at] = newItem;
    }

    private async Task DownloadReplacementAsync(string oldId, ModReplacement r)
    {
        var oldName = NameOf(oldId);
        var sameId = r.NewPackageId is { } pid && _items.TryGetValue(oldId, out var oldItem) && oldItem.Mod.DeclaredPackageId == pid;
        var oldWorkshop = _items.TryGetValue(oldId, out var o) && o.Mod.Source == ModSource.Workshop ? o.Mod.WorkshopId : null;
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Fix.Replace.Title"),
            Text = Loc.Format("Fix.Replace.Text", oldName, r.NewName)
                + (sameId && oldWorkshop is not null ? "\n\n" + Loc.Get("Fix.Replace.SameId") : ""),
            PrimaryText = Loc.Get("Fix.DownloadReplacement"),
        });
        if (answer != "primary") return;
        await StartDownloadsAsync([ReplacementDownload(oldId, r, sameId ? oldWorkshop : null)]);
    }

    private DownloadViewModel ReplacementDownload(string oldId, ModReplacement r, ulong? unsubscribeOld) =>
        new(r.NewWorkshopId, r.NewName) { NeededBy = null, ReplacesId = oldId, UnsubscribeAfter = unsubscribeOld };

    /// <summary>
    /// Called before the rescan after downloads: a replacement with the same package id needs the old subscription
    /// gone, or the game would keep loading the old copy.
    /// </summary>
    private async Task UnsubscribeReplacedAsync(IReadOnlyList<DownloadViewModel> done)
    {
        var old = done.Where(d => d.UnsubscribeAfter is not null).Select(d => d.UnsubscribeAfter!.Value).ToList();
        if (old.Count == 0) return;
        try
        {
            await _steam.UnsubscribeAsync(old);
            foreach (var id in old) _unsubscribed.Add(id);
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ProblemText(ex));
        }
    }

    /// <summary>After the rescan: downloaded replacements take the place of the mods they replace.</summary>
    private bool ApplyReplacements(IReadOnlyList<DownloadViewModel> done)
    {
        var swaps = done.Where(d => d.ReplacesId is not null)
            .Select(d => (Old: _items.GetValueOrDefault(d.ReplacesId!), New: _byWorkshopId.GetValueOrDefault(d.Id) is { } m ? _items.GetValueOrDefault(m.PackageId) : null))
            .Where(s => s.Old is not null && s.New is not null && s.Old != s.New)
            .ToList();
        if (swaps.Count == 0) return false;
        Change(() =>
        {
            foreach (var (oldItem, newItem) in swaps) SwapInList(oldItem!, newItem!);
        });
        return true;
    }

    // ── Fix everything ──

    [RelayCommand]
    private void OpenFixAll()
    {
        FixAllRows.Clear();
        FixAllManual.Clear();
        var downloads = new HashSet<ulong>();
        var enabled = new HashSet<string>();
        var needsSort = false;

        foreach (var issue in _issues.OrderByDescending(i => i.Severity))
        {
            var name = NameOf(issue.ModId);
            var other = NameOf(issue.OtherId, issue.OtherName);
            switch (issue.Kind)
            {
                case IssueKind.DependencyInactive when issue.OtherId is { } dep:
                    var depId = _items.ContainsKey(dep) ? dep : dep + "_steam";
                    if (!_items.ContainsKey(depId) || !enabled.Add(depId)) break;
                    FixAllRows.Add(new FixAllRow
                    {
                        Text = Loc.Format("FixAll.Enable", other), Note = Loc.Format("FixAll.NeededBy", name),
                        ListChange = () => { if (!_active.Contains(_items[depId])) _active.Add(_items[depId]); },
                    });
                    needsSort = true;
                    break;
                case IssueKind.DependencyMissing when WorkshopIds.Parse(issue.Detail) is { } wid:
                    if (!downloads.Add(wid)) break;
                    FixAllRows.Add(new FixAllRow
                    {
                        Text = Loc.Format("FixAll.Download", other), Note = Loc.Format("FixAll.NeededBy", name),
                        Download = new DownloadViewModel(wid, other) { NeededBy = name, EnableWhenDone = true },
                    });
                    break;
                case IssueKind.NotInstalled when WorkshopIdOfMissing(issue.ModId) is { } wid:
                    if (!downloads.Add(wid)) break;
                    FixAllRows.Add(new FixAllRow { Text = Loc.Format("FixAll.Download", name), Download = new DownloadViewModel(wid, name) });
                    break;
                case IssueKind.NotInstalled:
                    var missing = ItemFor(issue.ModId);
                    FixAllRows.Add(new FixAllRow
                    {
                        Text = Loc.Format("FixAll.RemoveMissing", name), Note = Loc.Get("FixAll.RemoveMissingNote"),
                        ListChange = () => _active.Remove(missing), IsChecked = false,
                    });
                    break;
                case IssueKind.DuplicateActive when _items.TryGetValue(issue.ModId, out var copy):
                    FixAllRows.Add(new FixAllRow { Text = Loc.Format("FixAll.RemoveCopy", name), ListChange = () => _active.Remove(copy) });
                    break;
                case IssueKind.UseInstead when issue.Replacement is { } r:
                    var outdated = issue.Severity == IssueSeverity.Warning;
                    if (_byWorkshopId.TryGetValue(r.NewWorkshopId, out var installedNew) && _items.TryGetValue(installedNew.PackageId, out var newItem)
                        && _items.TryGetValue(issue.ModId, out var oldItem))
                    {
                        FixAllRows.Add(new FixAllRow
                        {
                            Text = Loc.Format("FixAll.Replace", name, r.NewName), IsChecked = outdated,
                            ListChange = () => SwapInList(oldItem, newItem),
                        });
                        needsSort = true;
                    }
                    else if (downloads.Add(r.NewWorkshopId))
                    {
                        var sameId = r.NewPackageId is { } pid && _items.TryGetValue(issue.ModId, out var o) && o.Mod.DeclaredPackageId == pid
                            && o.Mod.Source == ModSource.Workshop;
                        FixAllRows.Add(new FixAllRow
                        {
                            Text = Loc.Format("FixAll.DownloadReplacement", r.NewName, name), IsChecked = outdated,
                            Note = string.Join(" ", new[]
                            {
                                r.NewAuthor is { } author ? Loc.Format("FixAll.ReplacementBy", author, r.OldName) : null,
                                sameId ? Loc.Get("Fix.Replace.SameId") : null,
                            }.OfType<string>()) is { Length: > 0 } note ? note : null,
                            Download = ReplacementDownload(issue.ModId, r, sameId ? _items[issue.ModId].Mod.WorkshopId : null),
                        });
                    }
                    break;
                case IssueKind.ShouldLoadAfter or IssueKind.ShouldLoadBefore:
                    needsSort = true;
                    break;
                case IssueKind.Incompatible:
                case IssueKind.DlcMissing:
                case IssueKind.UnsupportedVersion:
                case IssueKind.DuplicateInstalled:
                case IssueKind.BrokenAbout:
                case IssueKind.DependencyMissing:
                    FixAllManual.Add(MakeIssue(issue).Message);
                    break;
            }
        }

        if (needsSort || ModSorter.Sort(Snapshot(), _rules).Changed)
            FixAllRows.Add(new FixAllRow { Text = Loc.Get("FixAll.Sort"), Note = Loc.Get("FixAll.SortNote") });

        FixAllSummary = FixAllRows.Count == 0 && FixAllManual.Count == 0 ? Loc.Get("FixAll.Nothing")
            : FixAllRows.Count == 0 ? Loc.Get("FixAll.OnlyManual")
            : Loc.Format("FixAll.Summary", FixAllRows.Count);
        OnPropertyChanged(nameof(HasFixAllRows));
        OnPropertyChanged(nameof(HasFixAllManual));
        IsFixAllOpen = true;
    }

    [RelayCommand]
    private void CloseFixAll() => IsFixAllOpen = false;

    [RelayCommand]
    private async Task ApplyFixAll()
    {
        IsFixAllOpen = false;
        var picked = FixAllRows.Where(r => r.IsChecked).ToList();
        if (picked.Count == 0) return;

        var changes = picked.Where(r => r.ListChange is not null).ToList();
        if (changes.Count > 0) Change(() => { foreach (var r in changes) r.ListChange!(); });
        // Sorting runs last and always when it was picked: new mods also need their place.
        if (picked.Any(r => r.ListChange is null && r.Download is null) || changes.Count > 0) SortCarefully();

        var downloads = picked.Where(r => r.Download is not null).Select(r => r.Download!).ToList();
        ShowToast(downloads.Count > 0 ? Loc.Format("FixAll.DoneDownloading", picked.Count - downloads.Count, downloads.Count) : Loc.Get("Fix.Done"));
        if (downloads.Count > 0) await StartDownloadsAsync(downloads);
    }

    // ── Pins and the player's own order rules (right-click menu of the load order) ──

    [ObservableProperty] public partial bool IsRulePickerOpen { get; set; }
    [ObservableProperty] public partial string RulePickerTitle { get; set; } = "";
    [ObservableProperty] public partial string RulePickerSearch { get; set; } = "";
    [ObservableProperty] public partial ModItemViewModel? RulePickerSelected { get; set; }
    public ObservableCollection<ModItemViewModel> RulePickerItems { get; } = [];

    private ModItemViewModel? _ruleSubject;
    private bool _ruleAfter;

    /// <summary>The mod the right-click menu acts on.</summary>
    private ModItemViewModel? MenuTarget => SelectedActive ?? SelectedMod;

    public bool MenuTargetPinnedTop => MenuTarget is { } m && ActiveProfile?.Model.PinTop.Contains(m.Id) == true;
    public bool MenuTargetPinnedBottom => MenuTarget is { } m && ActiveProfile?.Model.PinBottom.Contains(m.Id) == true;
    public bool MenuTargetPinned => MenuTargetPinnedTop || MenuTargetPinnedBottom;

    /// <summary>Refreshes the menu's checkmarks right before it opens.</summary>
    public void PrepareModMenu()
    {
        OnPropertyChanged(nameof(MenuTargetPinnedTop));
        OnPropertyChanged(nameof(MenuTargetPinnedBottom));
        OnPropertyChanged(nameof(MenuTargetPinned));
    }

    [RelayCommand]
    private void Pin(string where)
    {
        if (MenuTarget is not { IsActive: true } mod || mod.Mod.IsOfficial) return;
        if (ActiveProfile is null)
        {
            ShowToast(Loc.Get("Rule.NeedProfile"));
            return;
        }
        PinMod(mod, where);
        ShowToast(Loc.Format(where switch { "top" => "Rule.PinnedTop", "bottom" => "Rule.PinnedBottom", _ => "Rule.Unpinned" }, mod.Name));
    }

    /// <summary>Pins the mod to the "top" or "bottom" of the list ("none" unpins) and re-sorts.</summary>
    private void PinMod(ModItemViewModel mod, string where)
    {
        if (ActiveProfile is not { } profile || mod.Mod.IsOfficial) return;
        profile.Model.PinTop.Remove(mod.Id);
        profile.Model.PinBottom.Remove(mod.Id);
        if (where == "top") profile.Model.PinTop.Add(mod.Id);
        else if (where == "bottom") profile.Model.PinBottom.Add(mod.Id);
        _store.Save(profile.Model);
        Recheck();
        if (where != "none") SortCarefully();
        else RefreshAll();
    }

    /// <param name="direction">"after": the selected mod must load after the one picked; "before": before it.</param>
    [RelayCommand]
    private void StartOrderRule(string direction)
    {
        if (MenuTarget is not { IsActive: true } mod) return;
        if (ActiveProfile is null)
        {
            ShowToast(Loc.Get("Rule.NeedProfile"));
            return;
        }
        _ruleSubject = mod;
        _ruleAfter = direction == "after";
        RulePickerTitle = Loc.Format(_ruleAfter ? "Rule.PickAfter" : "Rule.PickBefore", mod.Name);
        RulePickerSearch = "";
        FillRulePicker();
        IsRulePickerOpen = true;
    }

    partial void OnRulePickerSearchChanged(string value) => FillRulePicker();

    private void FillRulePicker()
    {
        RulePickerItems.Clear();
        var search = RulePickerSearch.Trim().ToLowerInvariant();
        foreach (var item in _active.Where(i => i != _ruleSubject && !i.IsMissing && (search.Length == 0 || i.SearchText.Contains(search))))
            RulePickerItems.Add(item);
        RulePickerSelected = null;
    }

    [RelayCommand]
    private void ConfirmOrderRule()
    {
        if (_ruleSubject is not { } subject || RulePickerSelected is not { } other) return;
        IsRulePickerOpen = false;
        AddUserRule(_ruleAfter ? new UserRule(other.Id, subject.Id) : new UserRule(subject.Id, other.Id));
        SortCarefully();
        ShowToast(Loc.Format(_ruleAfter ? "Rule.AddedAfter" : "Rule.AddedBefore", subject.Name, other.Name));
    }

    [RelayCommand]
    private void CloseRulePicker() => IsRulePickerOpen = false;

    /// <summary>Marks pinned mods in the lists.</summary>
    private void RefreshPins()
    {
        var top = ActiveProfile?.Model.PinTop ?? [];
        var bottom = ActiveProfile?.Model.PinBottom ?? [];
        foreach (var item in _items.Values)
        {
            item.PinTip = top.Contains(item.Id) ? Loc.Get("Rule.PinnedTopTip")
                : bottom.Contains(item.Id) ? Loc.Get("Rule.PinnedBottomTip")
                : "";
        }
    }
}
