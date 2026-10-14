using RimForge.Core.Models;

namespace RimForge.Core.Sorting;

public enum RuleSource
{
    /// <summary>The player set this order themselves; it wins over every other rule.</summary>
    User,
    /// <summary>The later mod requires the earlier one (modDependencies).</summary>
    Dependency,
    /// <summary>The later mod asks to load after the earlier one (loadAfter / forceLoadAfter).</summary>
    LoadAfter,
    /// <summary>The earlier mod asks to load before the later one (loadBefore / forceLoadBefore).</summary>
    LoadBefore,
    /// <summary>A rule from the community database.</summary>
    Community,
    /// <summary>Regular mods load after the game and its official expansions.</summary>
    Official,
}

/// <summary><see cref="Before"/> must load above <see cref="After"/>. <see cref="DeclaredBy"/> is the mod the rule belongs to.</summary>
public sealed record OrderRule(string Before, string After, RuleSource Source, string DeclaredBy, string? Comment = null)
{
    /// <summary>The other mod, seen from <see cref="DeclaredBy"/>.</summary>
    public string Other => DeclaredBy == After ? Before : After;
}

public sealed record IncompatibilityRule(string A, string B, string? Comment);

/// <summary>A rule the player made: <see cref="Before"/> loads above <see cref="After"/>. Stored in the modpack.</summary>
public sealed record UserRule(string Before, string After);

/// <summary>Mods the player pinned to the top (right after the game and DLC) or to the bottom of the list.</summary>
public sealed record UserPins(IReadOnlyCollection<string> Top, IReadOnlyCollection<string> Bottom)
{
    public static UserPins None { get; } = new([], []);
}

/// <summary>All ordering rules that apply to one list of active mods.</summary>
public sealed class RuleSet
{
    public required IReadOnlyList<OrderRule> Orders { get; init; }
    public required IReadOnlySet<string> Top { get; init; }
    public required IReadOnlyDictionary<string, string?> Bottom { get; init; }
    public required IReadOnlyList<IncompatibilityRule> CommunityIncompatible { get; init; }

    /// <summary>Top/bottom places the player chose (a subset of <see cref="Top"/> and <see cref="Bottom"/>).</summary>
    public IReadOnlySet<string> PinnedByUser { get; init; } = new HashSet<string>();

    public static RuleSet Build(IReadOnlyList<string> activeIds, IReadOnlyDictionary<string, ModInfo> installed, CommunityRules? community,
        IEnumerable<UserRule>? userRules = null, UserPins? pins = null)
    {
        var active = new HashSet<string>(activeIds);
        // Rules name declared ids; a duplicate the game renamed to "<id>_steam" must still match.
        string? Resolve(string id) => active.Contains(id) ? id : active.Contains(id + "_steam") ? id + "_steam" : null;

        var orders = new List<OrderRule>();
        var seen = new HashSet<(string, string)>();
        void Add(string? before, string? after, RuleSource source, string declaredBy, string? comment = null)
        {
            if (before is null || after is null || before == after) return;
            if (seen.Add((before, after))) orders.Add(new OrderRule(before, after, source, declaredBy, comment));
        }

        // The player's own rules go first so they win when another rule names the same pair.
        foreach (var rule in userRules ?? [])
            Add(Resolve(rule.Before), Resolve(rule.After), RuleSource.User, rule.After);

        foreach (var id in activeIds)
        {
            if (!installed.TryGetValue(id, out var mod)) continue;
            foreach (var dep in mod.Dependencies) Add(Resolve(dep.PackageId), id, RuleSource.Dependency, id);
            foreach (var other in mod.LoadAfter.Concat(mod.ForceLoadAfter)) Add(Resolve(other), id, RuleSource.LoadAfter, id);
            foreach (var other in mod.LoadBefore.Concat(mod.ForceLoadBefore)) Add(id, Resolve(other), RuleSource.LoadBefore, id);
        }

        var top = new HashSet<string>();
        var bottom = new Dictionary<string, string?>();
        var incompatible = new List<IncompatibilityRule>();
        if (community is not null)
        {
            foreach (var id in activeIds)
            {
                var declared = installed.TryGetValue(id, out var m) ? m.DeclaredPackageId : id;
                if (!community.Rules.TryGetValue(declared, out var rules)) continue;
                foreach (var (other, comment) in rules.LoadAfter) Add(Resolve(other), id, RuleSource.Community, id, comment);
                foreach (var (other, comment) in rules.LoadBefore) Add(id, Resolve(other), RuleSource.Community, id, comment);
                foreach (var (other, comment) in rules.IncompatibleWith)
                    if (Resolve(other) is { } o && o != id) incompatible.Add(new IncompatibilityRule(id, o, comment));
                if (rules.LoadTop) top.Add(id);
                if (rules.LoadBottom) bottom[id] = rules.BottomComment;
            }
        }

        // The player's pins win over the community's.
        var pinned = new HashSet<string>();
        foreach (var id in (pins ?? UserPins.None).Top.Select(Resolve).OfType<string>())
        {
            bottom.Remove(id);
            top.Add(id);
            pinned.Add(id);
        }
        foreach (var id in (pins ?? UserPins.None).Bottom.Select(Resolve).OfType<string>())
        {
            top.Remove(id);
            bottom[id] = null;
            pinned.Add(id);
        }

        AddOfficialRules(activeIds, installed, orders, Add);

        return new RuleSet { Orders = orders, Top = top, Bottom = bottom, CommunityIncompatible = incompatible, PinnedByUser = pinned };
    }

    /// <summary>
    /// Regular mods go below the game and its DLCs, unless their own rules say they must be above one
    /// of them (like Harmony, which loads before the game).
    /// </summary>
    private static void AddOfficialRules(IReadOnlyList<string> activeIds, IReadOnlyDictionary<string, ModInfo> installed,
        List<OrderRule> orders, Action<string?, string?, RuleSource, string, string?> add)
    {
        var official = activeIds.Where(id => installed.TryGetValue(id, out var m) && m.IsOfficial).ToList();
        if (official.Count == 0) return;

        // The game declares this only for some DLCs; every DLC loads after the base game.
        if (official.Contains(KnownMods.Core))
            foreach (var dlc in official.Where(o => o != KnownMods.Core))
                add(KnownMods.Core, dlc, RuleSource.Official, dlc, null);

        var predecessors = new Dictionary<string, List<string>>();
        foreach (var r in orders)
        {
            if (!predecessors.TryGetValue(r.After, out var list)) predecessors[r.After] = list = [];
            list.Add(r.Before);
        }

        foreach (var o in official)
        {
            // Everything that (directly or through a chain) has to load before this official mod.
            var mustPrecede = new HashSet<string>();
            var stack = new Stack<string>([o]);
            while (stack.Count > 0)
                foreach (var p in predecessors.GetValueOrDefault(stack.Pop()) ?? [])
                    if (mustPrecede.Add(p)) stack.Push(p);

            foreach (var id in activeIds)
            {
                if (official.Contains(id) || mustPrecede.Contains(id)) continue;
                add(o, id, RuleSource.Official, id, null);
            }
        }
    }
}
