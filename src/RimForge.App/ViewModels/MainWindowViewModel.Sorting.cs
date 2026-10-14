using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Storage;
using RimForge.Core.Sorting;

namespace RimForge.App.ViewModels;

public sealed record SortMoveRow(string Name, string Positions, string Reason, bool MovesUp);

/// <param name="Rule">Set for the player's own rules, which can be removed from here.</param>
public sealed record PlacementRow(string Text, UserRule? Rule = null)
{
    public bool CanRemove => Rule is not null;
}

/// <summary>Sorting, its preview, "why is this mod here?" and the community rules database.</summary>
public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan CommunityMaxAge = TimeSpan.FromDays(3);
    private const int MaxPlacementRows = 25;

    private CommunityRules _community = CommunityRules.Empty;
    private RuleSet _rules = RuleSet.Build([], new Dictionary<string, Core.Models.ModInfo>(), null);
    private SortResult? _pendingSort;

    public ObservableCollection<SortMoveRow> SortMoves { get; } = [];
    public ObservableCollection<string> SortConflicts { get; } = [];
    public ObservableCollection<PlacementRow> PlacementAbove { get; } = [];
    public ObservableCollection<PlacementRow> PlacementBelow { get; } = [];

    [ObservableProperty] public partial bool IsSortOpen { get; set; }
    [ObservableProperty] public partial string SortSummary { get; set; } = "";
    [ObservableProperty] public partial string SortTitle { get; set; } = "";
    [ObservableProperty] public partial string SortGroupsText { get; set; } = "";
    [ObservableProperty] public partial bool SortHasGroups { get; set; }

    /// <summary>What the content scan found in a mod (null until it has run).</summary>
    private Core.Content.ModProfile? ContentOf(string id) => _items.TryGetValue(id, out var item) ? item.Profile : null;
    [ObservableProperty] public partial bool SortHasConflicts { get; set; }
    [ObservableProperty] public partial bool SortHasMoves { get; set; }
    [ObservableProperty] public partial string CommunityStatus { get; set; } = "";
    [ObservableProperty] public partial string PlacementNote { get; set; } = "";
    [ObservableProperty] public partial bool HasPlacement { get; set; }

    public bool HasPlacementAbove => PlacementAbove.Count > 0;
    public bool HasPlacementBelow => PlacementBelow.Count > 0;

    // ── Community rules ──

    private async Task LoadCommunityRulesAsync(bool force)
    {
        CommunityStatus = Loc.Get("Community.Loading");
        try
        {
            var file = Path.Combine(AppDataPaths.Root, "cache", "communityRules.json");
            _community = await CommunityRules.LoadAsync(file, CommunityMaxAge, force);
        }
        catch (Exception)
        {
            _community = CommunityRules.Empty;
        }
        UpdateCommunityStatus();
        Recheck();
        UpdateSelectionDetails();
    }

    private void UpdateCommunityStatus() =>
        CommunityStatus = _community.Count == 0
            ? Loc.Get("Community.Failed")
            : Loc.Format("Community.Status", _community.Count,
                _community.UpdatedUtc is { } u ? Dates.Format(u.ToLocalTime()) : "?");

    [RelayCommand]
    private Task RefreshCommunityRules() => Task.WhenAll(LoadCommunityRulesAsync(force: true), LoadDatabasesAsync(force: true));

    // ── Sorting ──

    /// <param name="mode">"quick" moves as little as possible, "full" rebuilds the list into groups.</param>
    [RelayCommand]
    private void Sort(string mode)
    {
        var full = mode == "full";
        var result = full ? ModSorter.SortFull(Snapshot(), _rules, _installed, ContentOf) : ModSorter.Sort(Snapshot(), _rules);
        if (!result.Changed && result.Conflicts.Count == 0)
        {
            ShowToast(Loc.Get("Sort.Nothing"));
            return;
        }

        _pendingSort = result;
        SortMoves.Clear();
        foreach (var move in result.Moves.OrderBy(m => m.NewIndex))
        {
            var reason = move.Reason is { } rule
                ? Loc.Format("Sort.ReasonLine", rule.After == move.Id
                    ? Loc.Format("Sort.MustBeAfter", NameOf(rule.Before))
                    : Loc.Format("Sort.MustBeBefore", NameOf(rule.After)), RuleText(rule))
                : result.Groups?.GetValueOrDefault(move.Id) is { } group
                    ? Loc.Format("Sort.Reason.Group", Loc.Get("Group." + group))
                    : Loc.Get("Sort.Reason.Chain");
            SortMoves.Add(new SortMoveRow(NameOf(move.Id), Loc.Format("Sort.Positions", move.OldIndex + 1, move.NewIndex + 1),
                reason, move.NewIndex < move.OldIndex));
        }

        SortConflicts.Clear();
        foreach (var conflict in result.Conflicts)
        {
            var names = string.Join(", ", conflict.Mods.Select(id => $"«{NameOf(id)}»"));
            var why = string.Join("; ", conflict.IgnoredRules.Select(RuleText));
            SortConflicts.Add(Loc.Format("Sort.ConflictLine", names, why));
        }

        SortHasMoves = result.Moves.Count > 0;
        SortHasConflicts = result.Conflicts.Count > 0;
        SortSummary = !SortHasMoves ? Loc.Get("Sort.OnlyConflicts")
            : full ? Loc.Format("Sort.FullSummary", result.Moves.Count)
            : Loc.Format("Sort.Summary", result.Moves.Count);
        SortTitle = Loc.Get(full ? "Sort.Full" : "Sort.Quick");
        SortHasGroups = result.Groups is not null;
        SortGroupsText = result.Groups is null ? "" : string.Join("  ·  ", result.Groups.Values
            .GroupBy(g => g).OrderBy(g => g.Key)
            .Select(g => Loc.Format("Sort.GroupCount", Loc.Get("Group." + g.Key), g.Count())));
        IsSortOpen = true;
    }

    [RelayCommand]
    private void ApplySort()
    {
        if (_pendingSort is not { } result) return;
        IsSortOpen = false;
        _pendingSort = null;
        if (!result.Changed) return;
        Change(() => _active = result.Order.Select(ItemFor).ToList());
        ShowToast(Loc.Format("Msg.Sorted", result.Moves.Count));
    }

    [RelayCommand]
    private void CloseSort()
    {
        IsSortOpen = false;
        _pendingSort = null;
    }

    /// <summary>Why a rule exists, in plain words.</summary>
    private string RuleText(OrderRule rule) => rule.Source switch
    {
        RuleSource.Dependency => Loc.Format("Reason.Dependency", NameOf(rule.After), NameOf(rule.Before)),
        RuleSource.LoadAfter or RuleSource.LoadBefore => Loc.Format("Reason.Author", NameOf(rule.DeclaredBy)),
        RuleSource.User => Loc.Get("Reason.User"),
        RuleSource.Community when rule.Comment is { } c => Loc.Format("Reason.CommunityComment", c),
        RuleSource.Community => Loc.Get("Reason.Community"),
        _ => Loc.Get("Reason.Official"),
    };

    // ── Why is this mod here? ──

    private void UpdatePlacement()
    {
        PlacementAbove.Clear();
        PlacementBelow.Clear();
        PlacementNote = "";
        HasPlacement = SelectedMod is { IsActive: true };
        if (SelectedMod is not { IsActive: true } mod)
        {
            NotifyPlacement();
            return;
        }

        var reasons = ModSorter.Explain(mod.Id, _rules);
        bool IsOfficial(string id) => _installed.TryGetValue(id, out var m) && m.IsOfficial;
        // Ties to the game and its DLC are summed up in one note instead of a line per DLC.
        var officialTies = reasons.Where(r => r.Rule.Source == RuleSource.Official
            || (!mod.IsOfficial && IsOfficial(r.OtherIsAbove ? r.Rule.Before : r.Rule.After))).ToList();
        foreach (var r in reasons.Except(officialTies))
        {
            var other = r.OtherIsAbove ? r.Rule.Before : r.Rule.After;
            var text = $"«{NameOf(other)}» — {WhyText(r, mod.Id)}";
            var userRule = r.Rule.Source == RuleSource.User
                ? ActiveProfile?.Model.Rules.FirstOrDefault(u => u.Before == r.Rule.Before && u.After == r.Rule.After)
                : null;
            (r.OtherIsAbove ? PlacementAbove : PlacementBelow).Add(new PlacementRow(text, userRule));
        }
        Trim(PlacementAbove);
        Trim(PlacementBelow);

        PlacementNote = officialTies.Any(r => r.OtherIsAbove) ? Loc.Get("Why.AfterOfficial")
            : mod.IsOfficial ? Loc.Get("Why.OfficialTop")
            : reasons.Count == 0 && !_rules.PinnedByUser.Contains(mod.Id) ? Loc.Get("Why.Free")
            : "";
        if (_rules.PinnedByUser.Contains(mod.Id))
        {
            var pin = Loc.Get(_rules.Top.Contains(mod.Id) ? "Why.PinnedTop" : "Why.PinnedBottom");
            PlacementNote = PlacementNote.Length == 0 ? pin : pin + "\n" + PlacementNote;
        }
        NotifyPlacement();
    }

    private string WhyText(PlacementReason r, string self) => r.Rule.Source switch
    {
        RuleSource.Dependency => Loc.Get(r.OtherIsAbove ? "Why.NeededByThis" : "Why.NeedsThis"),
        RuleSource.LoadAfter or RuleSource.LoadBefore => r.Rule.DeclaredBy == self
            ? Loc.Get("Why.AuthorThis")
            : Loc.Format("Why.AuthorOther", NameOf(r.Rule.DeclaredBy)),
        _ => RuleText(r.Rule),
    };

    private static void Trim(ObservableCollection<PlacementRow> rows)
    {
        if (rows.Count <= MaxPlacementRows) return;
        var extra = rows.Count - MaxPlacementRows;
        while (rows.Count > MaxPlacementRows) rows.RemoveAt(rows.Count - 1);
        rows.Add(new PlacementRow(Loc.Format("Why.More", extra)));
    }

    private void NotifyPlacement()
    {
        OnPropertyChanged(nameof(HasPlacementAbove));
        OnPropertyChanged(nameof(HasPlacementBelow));
    }
}
