using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Diagnostics;
using RimForge.Core.Sorting;

namespace RimForge.App.ViewModels;

/// <summary>
/// One-click fixes for diagnostics findings. Every fix shows what it will do first, changes only the
/// mod list and order (undo with Ctrl+Z) and never touches mod files.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private FindingViewModel WithFixes(FindingViewModel finding, IReadOnlyList<FixOption> options)
    {
        if (options.FirstOrDefault(o => o.Kind == FixKind.LoadAfter) is { } order)
            finding.Explanation = Loc.Format("Diag.Log.MissingParent.Order", NameOf(order.ModIds[0]), NameOf(order.ModIds[1]));
        finding.Fixes = options
            .Where(o => o.Kind != FixKind.Ignore || !finding.IsIgnored)
            .Select(o => new FixOptionViewModel(finding, o, FixTitle(o), Loc.Get("Fix.Tip." + o.Kind)))
            .Append(finding.IsIgnored ? new FixOptionViewModel(finding, new FixOption(FixKind.Ignore, []), Loc.Get("Fix.Unignore"), Loc.Get("Fix.Tip.Unignore")) : null)
            .OfType<FixOptionViewModel>()
            .ToList();
        return finding;
    }

    private string FixTitle(FixOption o) => o.Kind switch
    {
        FixKind.EnableMods => o.ModIds.Count > 1
            ? Loc.Format("Fix.EnableWithDeps", NameOf(o.Provider), o.ModIds.Count - 1)
            : Loc.Format("Fix.EnableOne", NameOf(o.Provider)),
        FixKind.LoadAfter => Loc.Format("Fix.LoadAfter", NameOf(o.ModIds[0]), NameOf(o.ModIds[1])),
        FixKind.DisableMods => Loc.Format("Fix.Disable", NameOf(o.ModIds[0])),
        FixKind.CheckSuspects => Loc.Format("Fix.CheckSuspects", o.ModIds.Count),
        FixKind.ChooseWinner => Loc.Get("Fix.ChooseWinner"),
        _ => Loc.Get("Fix.Ignore"),
    };

    [RelayCommand]
    private async Task ApplyFix(FixOptionViewModel fix)
    {
        var o = fix.Option;
        switch (o.Kind)
        {
            case FixKind.Ignore:
                SetIgnored(fix.Finding, !fix.Finding.IsIgnored);
                return;
            case FixKind.CheckSuspects:
                OpenBisectWith(o.ModIds, fix.Finding.Key.StartsWith("log:") ? fix.Finding.Key[4..] : null);
                return;
            case FixKind.ChooseWinner:
                if (await ChooseWinnerAsync(o.ModIds)) MarkFixedEverywhere(fix.Finding, o);
                return;
        }

        if (!await ConfirmFixAsync([o])) return;
        ApplyListFixes([o]);
        MarkFixedEverywhere(fix.Finding, o);
    }

    /// <summary>Applies every safe fix (turning mods on, moving mods after templates they need) in one go.</summary>
    [RelayCommand]
    private async Task FixAllSafe()
    {
        // One safe fix per finding is enough (several providers would do the same job).
        var picked = AllFindings
            .Where(f => !f.IsIgnored && !f.IsFixed)
            .Select(f => (Finding: f, Fix: f.Fixes.FirstOrDefault(x => x.IsSafe)))
            .Where(x => x.Fix is not null)
            .ToList();

        // Dependencies the traffic light already knows are installed but off.
        var dependencyFixes = _issues.Where(i => i.Kind == Core.Checks.IssueKind.DependencyInactive && i.OtherId is not null)
            .Select(i => _installed.ContainsKey(i.OtherId!) ? i.OtherId! : i.OtherId! + "_steam")
            .Where(_installed.ContainsKey).Distinct()
            .Select(id => new FixOption(FixKind.EnableMods, FixPlanner.WithDependencies(id, _installed, Snapshot().ToHashSet()), id))
            .ToList();

        var options = picked.Select(x => x.Fix!.Option).Concat(dependencyFixes)
            .DistinctBy(o => (o.Kind, string.Join(",", o.ModIds))).ToList();
        if (options.Count == 0)
        {
            ShowToast(Loc.Get("Fix.NothingSafe"));
            return;
        }
        if (!await ConfirmFixAsync(options)) return;
        ApplyListFixes(options);
        foreach (var (finding, fix) in picked) MarkFixedEverywhere(finding, fix!.Option);
    }

    /// <summary>Describes what the fixes will do and asks for a go-ahead.</summary>
    private async Task<bool> ConfirmFixAsync(IReadOnlyList<FixOption> options)
    {
        var lines = new List<string>();
        var danger = false;
        foreach (var o in options)
        {
            switch (o.Kind)
            {
                case FixKind.EnableMods:
                    lines.Add(Loc.Format("Fix.Preview.Enable", NameOf(o.Provider)));
                    lines.AddRange(o.ModIds.Where(id => id != o.Provider).Select(id => Loc.Format("Fix.Preview.EnableDep", NameOf(id))));
                    break;
                case FixKind.LoadAfter:
                    lines.Add(Loc.Format("Fix.Preview.LoadAfter", NameOf(o.ModIds[0]), NameOf(o.ModIds[1])));
                    break;
                case FixKind.DisableMods:
                    foreach (var id in o.ModIds)
                    {
                        lines.Add(Loc.Format("Fix.Preview.Disable", NameOf(id)));
                        if (_saveUsage.TryGetValue(id, out var saves) && saves.Count > 0)
                        {
                            danger = true;
                            lines.Add(Loc.Format("Fix.Preview.UsedInSaves", saves.Count, string.Join(", ", saves.Take(5))));
                        }
                    }
                    break;
            }
        }
        if (options.Any(o => o.Kind is FixKind.EnableMods or FixKind.LoadAfter)) lines.Add(Loc.Get("Fix.Preview.Sorted"));
        lines.Add(Loc.Get("Fix.Preview.Undo"));

        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Fix.Preview.Title"),
            Text = string.Join("\n", lines),
            PrimaryText = Loc.Get("Fix.Apply"),
            PrimaryIsDanger = danger,
        });
        return answer == "primary";
    }

    /// <summary>
    /// Turns mods on/off and adds order rules, then lets the careful sort put everything where the rules
    /// say (moving a mod by hand would strand the mods that depend on it).
    /// </summary>
    private void ApplyListFixes(IReadOnlyList<FixOption> options)
    {
        Change(() =>
        {
            foreach (var o in options)
            {
                switch (o.Kind)
                {
                    case FixKind.EnableMods:
                        foreach (var id in o.ModIds.Where(id => _items.ContainsKey(id) && !_active.Contains(_items[id])))
                            _active.Add(_items[id]);
                        break;
                    case FixKind.DisableMods:
                        foreach (var id in o.ModIds) if (_items.TryGetValue(id, out var item)) _active.Remove(item);
                        break;
                    case FixKind.LoadAfter:
                        AddUserRule(new UserRule(o.ModIds[1], o.ModIds[0]));
                        break;
                }
            }
        });

        SortCarefully();
        ShowToast(Loc.Get("Fix.Done"));
    }

    /// <summary>Applies the careful (fewest moves) sort with the current rules, including the player's own.</summary>
    private void SortCarefully()
    {
        var sorted = ModSorter.Sort(Snapshot(), RuleSet.Build(Snapshot(), _installed, _community, ActiveProfile?.Model.Rules, ActiveProfile?.Model.Pins));
        if (sorted.Changed) Change(() => _active = sorted.Order.Select(ItemFor).ToList());
        else RefreshAll(); // a new rule alone changes no positions but does change the checks
    }

    /// <summary>Marks the finding, and every other finding that offered the very same fix, as fixed.</summary>
    private void MarkFixedEverywhere(FindingViewModel finding, FixOption applied)
    {
        MarkFixed(finding);
        foreach (var other in AllFindings.Where(f => f.Fixes.Any(x => x.Option.Kind == applied.Kind && x.Option.ModIds.SequenceEqual(applied.ModIds))))
            MarkFixed(other);
    }

    private void MarkFixed(FindingViewModel finding)
    {
        finding.IsFixed = true;
        IsReportStale = true;
    }

    private void SetIgnored(FindingViewModel finding, bool ignore)
    {
        if (ActiveProfile is not { } profile) return;
        profile.Model.Ignored.Remove(finding.Key);
        if (ignore) profile.Model.Ignored.Add(finding.Key);
        _store.Save(profile.Model);

        // Rebuild the card so its buttons match the new state.
        var replacement = new FindingViewModel
        {
            Key = finding.Key, Severity = finding.Severity, Title = finding.Title, Explanation = finding.Explanation,
            Mods = finding.Mods, Details = finding.Details, Count = finding.Count, ModsLabel = finding.ModsLabel, IsIgnored = ignore,
        };
        WithFixes(replacement, finding.Fixes.Select(f => f.Option).Where(o => o.Kind != FixKind.Ignore).Append(new FixOption(FixKind.Ignore, [])).ToList());
        foreach (var list in new[] { _logFindings, _codeFindings, _dataFindings })
        {
            var i = list.IndexOf(finding);
            if (i >= 0) list[i] = replacement;
        }
        RefreshVisibleFindings();
        ShowToast(Loc.Get(ignore ? "Fix.Ignored" : "Fix.Unignored"));
    }

    // ── "Whose version should win?" ──

    private async Task<bool> ChooseWinnerAsync(IReadOnlyList<string> modIds)
    {
        var contenders = modIds.Where(id => _items.TryGetValue(id, out var item) && item.IsActive).ToList();
        if (contenders.Count < 2) return false;
        var dialog = new DialogViewModel
        {
            Title = Loc.Get("Fix.Winner.Title"),
            Text = Loc.Get("Fix.Winner.Text"),
            Choices = contenders.Select(id => NameOf(id)).ToList(),
            PrimaryText = Loc.Get("Fix.Apply"),
        };
        // The current winner (lowest in the list) is preselected.
        dialog.ChoiceIndex = contenders.Count - 1;
        if (await AskAsync(dialog) != "primary" || dialog.ChoiceIndex < 0) return false;

        var winner = _items[contenders[dialog.ChoiceIndex]];
        foreach (var other in contenders.Where(id => id != winner.Id)) AddUserRule(new UserRule(other, winner.Id));
        SortCarefully();
        IsReportStale = true;
        ShowToast(Loc.Format("Fix.Winner.Done", winner.Name));
        return true;
    }

    // ── The player's own order rules ──

    private void AddUserRule(UserRule rule)
    {
        if (ActiveProfile is not { } profile) return;
        // A newer rule about the same pair replaces the old one, whichever way it pointed.
        profile.Model.Rules.RemoveAll(r => (r.Before == rule.Before && r.After == rule.After) || (r.Before == rule.After && r.After == rule.Before));
        profile.Model.Rules.Add(rule);
        _store.Save(profile.Model);
    }

    [RelayCommand]
    private void RemoveUserRule(UserRule rule)
    {
        if (ActiveProfile is not { } profile) return;
        profile.Model.Rules.Remove(rule);
        _store.Save(profile.Model);
        RefreshAll();
        ShowToast(Loc.Get("Fix.RuleRemoved"));
    }
}
