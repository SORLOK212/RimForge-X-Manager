using System.Text.RegularExpressions;
using RimForge.Core.Content;
using RimForge.Core.Models;

namespace RimForge.Core.Sorting;

/// <summary>Groups of the full sort, top to bottom.</summary>
public enum SortGroup
{
    /// <summary>Mods that must load before the game itself (Harmony, Prepatcher…).</summary>
    BeforeGame,
    /// <summary>The game and its official DLC.</summary>
    Game,
    /// <summary>Libraries and frameworks: other active mods depend on them.</summary>
    Library,
    /// <summary>Mods that add things: defs, with their pictures and code.</summary>
    Content,
    /// <summary>Retextures: pictures only, so they come after the mods whose pictures they replace.</summary>
    Graphics,
    /// <summary>Patches and compatibility mods: they change defs of the mods above.</summary>
    Patch,
    /// <summary>Code only (interface, performance, quality of life): nothing else depends on where they are.</summary>
    Tweak,
    Translation,
}

/// <summary>A mod the sort moved, with the rule that made it move (if one applies to it directly).</summary>
public sealed record SortMove(string Id, int OldIndex, int NewIndex, OrderRule? Reason);

/// <summary>
/// Rules that contradict each other (A before B and B before A, possibly through a chain).
/// The sorter keeps these mods in their current relative order and ignores <see cref="IgnoredRules"/>.
/// </summary>
public sealed record SortConflict(IReadOnlyList<string> Mods, IReadOnlyList<OrderRule> IgnoredRules);

public sealed record SortResult(IReadOnlyList<string> Order, IReadOnlyList<SortMove> Moves, IReadOnlyList<SortConflict> Conflicts)
{
    /// <summary>Set by the full sort: the group each mod was put in.</summary>
    public IReadOnlyDictionary<string, SortGroup>? Groups { get; init; }

    public bool Changed => Moves.Count > 0;
}

/// <summary>A line of "why is this mod here?".</summary>
public sealed record PlacementReason(OrderRule Rule, bool OtherIsAbove);

public static class ModSorter
{
    /// <summary>
    /// Orders mods so that every rule holds while moving as little as possible. An order that
    /// already obeys every rule stays exactly as it is.
    /// <para>
    /// Two candidates are built: one fills the list from the top, always taking the free mod that
    /// was highest before (misplaced mods sink down); the other fills it from the bottom, taking the
    /// free mod that was lowest (misplaced mods rise up). The one that moves fewer mods wins.
    /// </para>
    /// </summary>
    public static SortResult Sort(IReadOnlyList<string> current, RuleSet rules)
    {
        var ids = current.Distinct().ToList();
        var index = new Dictionary<string, int>();
        for (var i = 0; i < ids.Count; i++) index[ids[i]] = i;

        var relevant = rules.Orders.Where(r => index.ContainsKey(r.Before) && index.ContainsKey(r.After)).ToList();
        var (edges, conflicts) = BreakCycles(ids, index, relevant);

        int Tier(string id) => TierOf(id, rules);

        var down = Fill(ids, edges, Comparer<string>.Create((a, b) => (Tier(a), index[a]).CompareTo((Tier(b), index[b]))), fromTop: true);
        var up = Fill(ids, edges, Comparer<string>.Create((a, b) => (2 - Tier(a), -index[a]).CompareTo((2 - Tier(b), -index[b]))), fromTop: false);
        var downMoves = FindMoves(ids, down, relevant);
        var upMoves = FindMoves(ids, up, relevant);

        return upMoves.Count < downMoves.Count
            ? new SortResult(up, upMoves, conflicts)
            : new SortResult(down, downMoves, conflicts);
    }

    /// <summary>Groups of rules that contradict each other in this list (see <see cref="SortConflict"/>).</summary>
    public static IReadOnlyList<SortConflict> FindConflicts(IReadOnlyList<string> order, RuleSet rules)
    {
        var ids = order.Distinct().ToList();
        var index = new Dictionary<string, int>();
        for (var i = 0; i < ids.Count; i++) index[ids[i]] = i;
        var relevant = rules.Orders.Where(r => index.ContainsKey(r.Before) && index.ContainsKey(r.After)).ToList();
        return BreakCycles(ids, index, relevant).Conflicts;
    }

    private static int TierOf(string id, RuleSet rules) => rules.Top.Contains(id) ? 0 : rules.Bottom.ContainsKey(id) ? 2 : 1;

    private static readonly Regex TranslationName = new(
        @"\b(rus|russian|русск\w*|перевод\w*|translations?|traduction|übersetzung|chinese|中文|简体|繁體|japanese|日本語|korean|한국어|deutsch|german|french|français|español|spanish|portugu\w*|polski|polish|türkçe|turkish|ukrainian|українськ\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The phase of a regular mod. Names count first (a "patch" or "rus" in the name is a strong hint), then what is
    /// inside: needed by others and little of its own — a library; only pictures — a retexture; only patch
    /// operations — a patch; only code — a tweak; defs — content.
    /// </summary>
    public static SortGroup PhaseOf(string name, bool neededByOthers, ModProfile? content)
    {
        if (TranslationName.IsMatch(name)) return SortGroup.Translation;
        if (FrameworkName.IsMatch(name)) return SortGroup.Library;
        if (PatchName.IsMatch(name)) return SortGroup.Patch;
        if (content is not { } p) return neededByOthers ? SortGroup.Library : SortGroup.Content;
        // Others need it, but a mod full of things is content its add-ons follow anyway (the dependency rules see to that).
        if (neededByOthers && p.Defs <= 40) return SortGroup.Library;
        if (p.HasLanguages && p.Defs == 0 && p.Assemblies == 0 && p.Patches == 0 && p.Textures == 0) return SortGroup.Translation;
        if (p.Defs > 0) return p.Patches > p.Defs * 3 && p.Defs < 10 ? SortGroup.Patch : SortGroup.Content;
        if (p.Patches > 0) return SortGroup.Patch;
        if (p.Assemblies > 0) return p.Textures > 20 ? SortGroup.Content : SortGroup.Tweak;
        return p.Textures > 0 ? SortGroup.Graphics : SortGroup.Content;
    }

    private static readonly Regex FrameworkName = new(
        @"\b(framework|library|lib|api|фреймворк|библиотек\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PatchName = new(
        @"\b(patch|patches|патч\w*|compat\w*|совместим\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Rebuilds the whole list into a tidy order: mods that must precede the game, the game and DLC,
    /// libraries, regular mods, patches, translations — alphabetical inside each group. Every rule
    /// still holds (a rule always wins over grouping), community "load last" mods end up last.
    /// </summary>
    /// <param name="contentOf">What a mod contains (from the content scan), when known: puts it in its phase by what it does,
    /// not only by its name. Without it, names and dependencies decide.</param>
    public static SortResult SortFull(IReadOnlyList<string> current, RuleSet rules, IReadOnlyDictionary<string, ModInfo> installed,
        Func<string, ModProfile?>? contentOf = null)
    {
        var ids = current.Distinct().ToList();
        var index = new Dictionary<string, int>();
        for (var i = 0; i < ids.Count; i++) index[ids[i]] = i;

        var relevant = rules.Orders.Where(r => index.ContainsKey(r.Before) && index.ContainsKey(r.After)).ToList();
        var (edges, conflicts) = BreakCycles(ids, index, relevant);

        string NameOf(string id) => installed.TryGetValue(id, out var m) ? m.Name : id;
        bool IsOfficial(string id) => installed.TryGetValue(id, out var m) && m.IsOfficial;

        // Everything that has to precede some official mod, directly or through a chain.
        var predecessors = ids.ToDictionary(id => id, _ => new List<string>());
        foreach (var (before, after) in edges) predecessors[after].Add(before);
        var beforeGame = new HashSet<string>();
        var stack = new Stack<string>(ids.Where(IsOfficial));
        while (stack.Count > 0)
            foreach (var p in predecessors[stack.Pop()])
                if (!IsOfficial(p) && beforeGame.Add(p)) stack.Push(p);

        var neededBy = relevant.Where(r => r.Source == RuleSource.Dependency).Select(r => r.Before).ToHashSet();
        var groups = ids.ToDictionary(id => id, id =>
            IsOfficial(id) ? SortGroup.Game
            : beforeGame.Contains(id) ? SortGroup.BeforeGame
            : PhaseOf(NameOf(id), neededBy.Contains(id), contentOf?.Invoke(id)));

        var names = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, ignoreCase: true);
        var priority = Comparer<string>.Create((a, b) =>
        {
            var c = TierOf(a, rules).CompareTo(TierOf(b, rules));
            if (c == 0) c = groups[a].CompareTo(groups[b]);
            // The game keeps its own DLC order; everything else is alphabetical.
            if (c == 0 && groups[a] != SortGroup.Game) c = names.Compare(NameOf(a), NameOf(b));
            return c != 0 ? c : index[a].CompareTo(index[b]);
        });

        var order = Fill(ids, edges, priority, fromTop: true);
        return new SortResult(order, FindMoves(ids, order, relevant), conflicts) { Groups = groups };
    }

    private static List<string> Fill(List<string> ids, List<(string Before, string After)> edges,
        IComparer<string> priority, bool fromTop)
    {
        // Filling from the bottom is the same walk on reversed rules.
        var next = ids.ToDictionary(id => id, _ => new List<string>());
        var waiting = ids.ToDictionary(id => id, _ => 0);
        foreach (var (before, after) in edges)
        {
            var (from, to) = fromTop ? (before, after) : (after, before);
            next[from].Add(to);
            waiting[to]++;
        }

        var ready = new PriorityQueue<string, string>(priority);
        foreach (var id in ids)
            if (waiting[id] == 0) ready.Enqueue(id, id);

        var order = new List<string>(ids.Count);
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            order.Add(id);
            foreach (var n in next[id])
                if (--waiting[n] == 0) ready.Enqueue(n, n);
        }
        if (!fromTop) order.Reverse();
        return order;
    }

    /// <summary>
    /// Finds groups of mods whose rules form a loop (strongly connected components) and drops the
    /// rules inside each group that disagree with the current order, which makes the rest solvable.
    /// </summary>
    private static (List<(string, string)> Edges, List<SortConflict> Conflicts) BreakCycles(
        List<string> ids, Dictionary<string, int> index, List<OrderRule> rules)
    {
        var next = ids.ToDictionary(id => id, _ => new List<string>());
        foreach (var r in rules) next[r.Before].Add(r.After);

        // Tarjan's algorithm.
        var component = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var order = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var counter = 0;
        var components = 0;

        void Visit(string v)
        {
            order[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in next[v])
            {
                if (!order.ContainsKey(w))
                {
                    Visit(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], order[w]);
                }
            }
            if (low[v] != order[v]) return;
            string x;
            do
            {
                x = stack.Pop();
                onStack.Remove(x);
                component[x] = components;
            } while (x != v);
            components++;
        }

        foreach (var id in ids)
            if (!order.ContainsKey(id)) Visit(id);

        // Inside a loop, the player's own rules win: order each loop by those rules first and the current
        // order second, then drop the other rules that disagree with that.
        var rank = new Dictionary<string, int>(index);
        foreach (var group in ids.GroupBy(id => component[id]).Where(g => g.Count() > 1))
        {
            var members = group.ToHashSet();
            var userEdges = rules.Where(r => r.Source == RuleSource.User && members.Contains(r.Before) && members.Contains(r.After)).ToList();
            if (userEdges.Count == 0) continue;
            var preferred = Fill(group.ToList(), userEdges.Select(r => (r.Before, r.After)).ToList(),
                Comparer<string>.Create((a, b) => index[a].CompareTo(index[b])), fromTop: true);
            if (preferred.Count != members.Count) continue; // the player's rules contradict each other: keep the current order
            var slots = group.Select(id => index[id]).OrderBy(i => i).ToList();
            for (var i = 0; i < preferred.Count; i++) rank[preferred[i]] = slots[i];
        }

        var edges = new List<(string, string)>();
        var ignored = new Dictionary<int, List<OrderRule>>();
        foreach (var r in rules)
        {
            var c = component[r.Before];
            if (c == component[r.After] && rank[r.Before] > rank[r.After])
            {
                if (!ignored.TryGetValue(c, out var list)) ignored[c] = list = [];
                list.Add(r);
            }
            else
            {
                edges.Add((r.Before, r.After));
            }
        }

        var conflicts = ignored.Select(kv => new SortConflict(
            ids.Where(id => component[id] == kv.Key).ToList(), kv.Value)).ToList();
        return (edges, conflicts);
    }

    /// <summary>
    /// Mods that kept their relative order form the longest increasing run of old positions;
    /// everything outside it is what really moved (the rest only shifted).
    /// </summary>
    private static List<SortMove> FindMoves(List<string> before, List<string> after, List<OrderRule> rules)
    {
        var oldIndex = new Dictionary<string, int>();
        for (var i = 0; i < before.Count; i++) oldIndex[before[i]] = i;
        var seq = after.Select(id => oldIndex[id]).ToArray();

        // Longest increasing subsequence with back-pointers (patience sorting).
        var tails = new List<int>();
        var tailAt = new List<int>();
        var prev = new int[seq.Length];
        for (var i = 0; i < seq.Length; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (tails[mid] < seq[i]) lo = mid + 1; else hi = mid;
            }
            prev[i] = lo > 0 ? tailAt[lo - 1] : -1;
            if (lo == tails.Count) { tails.Add(seq[i]); tailAt.Add(i); }
            else { tails[lo] = seq[i]; tailAt[lo] = i; }
        }
        var kept = new HashSet<int>();
        for (var i = tailAt.Count > 0 ? tailAt[^1] : -1; i >= 0; i = prev[i]) kept.Add(i);

        var moves = new List<SortMove>();
        for (var i = 0; i < after.Count; i++)
        {
            if (kept.Contains(i)) continue;
            var id = after[i];
            // The rule involving this mod that the old order broke and the new one keeps.
            var reason = rules.FirstOrDefault(r =>
                (r.Before == id || r.After == id) && oldIndex[r.Before] > oldIndex[r.After]);
            moves.Add(new SortMove(id, oldIndex[id], i, reason));
        }
        return moves;
    }

    /// <summary>Rules that tie <paramref name="id"/> to other active mods: who must be above it and who below.</summary>
    public static IReadOnlyList<PlacementReason> Explain(string id, RuleSet rules) =>
        rules.Orders
            .Where(r => r.Before == id || r.After == id)
            .Select(r => new PlacementReason(r, OtherIsAbove: r.After == id))
            .ToList();
}
