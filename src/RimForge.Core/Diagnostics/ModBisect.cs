using RimForge.Core.Models;

namespace RimForge.Core.Diagnostics;

/// <summary>
/// Finds the mod behind a problem by halving: each round turns on half of the suspects (with their
/// dependencies) and asks whether the problem is still there. Assumes one culprit; when a problem
/// needs two mods together, the result points at one of them.
/// </summary>
public sealed class ModBisect
{
    private readonly IReadOnlyList<string> _order;
    private readonly IReadOnlyDictionary<string, ModInfo> _installed;
    private readonly HashSet<string> _alwaysOn;
    private readonly bool _onlySuspects;
    private readonly Stack<List<string>> _history = new();
    private List<string> _candidates;

    /// <param name="order">The full active list in load order.</param>
    /// <param name="alwaysOn">Mods kept on in every round and never suspected (the game and its DLC).</param>
    /// <param name="suspects">
    /// When given, only these mods are checked and every other mod stays on (except mods that need a
    /// suspect which is off) — a quick search among the suspects of one diagnostics finding.
    /// </param>
    public ModBisect(IReadOnlyList<string> order, IReadOnlyDictionary<string, ModInfo> installed, IEnumerable<string> alwaysOn,
        IReadOnlyCollection<string>? suspects = null)
    {
        _order = order.Distinct().ToList();
        _installed = installed;
        _alwaysOn = alwaysOn.ToHashSet();
        _onlySuspects = suspects is not null;
        _candidates = _order.Where(id => !_alwaysOn.Contains(id) && (suspects is null || suspects.Contains(id))).ToList();
        PlanRound();
    }

    public IReadOnlyList<string> Candidates => _candidates;

    /// <summary>The mods to turn on for this round, in load order.</summary>
    public IReadOnlyList<string> TestOrder { get; private set; } = [];

    /// <summary>The suspects that are on in this round.</summary>
    public IReadOnlyList<string> TestedSuspects { get; private set; } = [];

    public int Round => _history.Count + 1;
    public bool IsDone => _candidates.Count <= 1 || TestedSuspects.Count == 0;
    public string? Culprit => _candidates.Count == 1 ? _candidates[0] : null;

    /// <summary>Rough number of game starts still needed.</summary>
    public int RoundsLeft => _candidates.Count <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(_candidates.Count));

    public void Answer(bool problemPresent)
    {
        if (IsDone) return;
        _history.Push(_candidates);
        var on = TestOrder.ToHashSet();
        // Problem with this half on: the culprit is among the suspects that were on. Otherwise it is among the rest.
        _candidates = problemPresent
            ? _candidates.Where(on.Contains).ToList()
            : _candidates.Where(id => !on.Contains(id)).ToList();
        PlanRound();
    }

    public bool CanUndo => _history.Count > 0;

    public void Undo()
    {
        if (!CanUndo) return;
        _candidates = _history.Pop();
        PlanRound();
    }

    private void PlanRound()
    {
        TestOrder = [];
        TestedSuspects = [];
        if (_candidates.Count <= 1) return;

        // Try the first half; if its dependencies drag in every suspect, try smaller groups. Failing
        // that, start from mods with the fewest dependencies: a library can be tested without the mods that need it.
        var byDependencies = _candidates.OrderBy(id => WithDependencies([id]).Count).ToList();
        foreach (var pool in new[] { _candidates, byDependencies })
        {
            for (var take = (pool.Count + 1) / 2; take >= 1; take = take == 1 ? 0 : take / 2)
            {
                var on = _onlySuspects ? AllExceptOtherSuspects(pool.Take(take)) : WithDependencies(pool.Take(take));
                var tested = _candidates.Where(on.Contains).ToList();
                if (tested.Count == _candidates.Count) continue;
                TestedSuspects = tested;
                TestOrder = _order.Where(on.Contains).ToList();
                return;
            }
        }
    }

    /// <summary>Everything stays on except the suspects outside <paramref name="tested"/> and the mods that need them.</summary>
    private HashSet<string> AllExceptOtherSuspects(IEnumerable<string> tested)
    {
        var keep = tested.ToHashSet();
        var off = _candidates.Where(c => !keep.Contains(c)).ToHashSet();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var id in _order)
            {
                if (off.Contains(id) || _alwaysOn.Contains(id) || !_installed.TryGetValue(id, out var mod)) continue;
                if (mod.Dependencies.Any(d => off.Contains(d.PackageId) || off.Contains(d.PackageId + "_steam")))
                {
                    off.Add(id);
                    changed = true;
                }
            }
        }
        return _order.Where(id => !off.Contains(id)).ToHashSet();
    }

    private HashSet<string> WithDependencies(IEnumerable<string> mods)
    {
        var on = new HashSet<string>(_alwaysOn);
        var stack = new Stack<string>(mods);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!on.Add(id) || !_installed.TryGetValue(id, out var mod)) continue;
            foreach (var dep in mod.Dependencies)
            {
                var depId = _order.Contains(dep.PackageId) ? dep.PackageId : dep.PackageId + "_steam";
                if (_order.Contains(depId)) stack.Push(depId);
            }
        }
        return on;
    }
}
