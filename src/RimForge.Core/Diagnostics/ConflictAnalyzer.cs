namespace RimForge.Core.Diagnostics;

public enum FindingSeverity { Info, Low, Medium, High }

public enum HarmonyRisk
{
    /// <summary>Several mods add code around the same method; usually fine.</summary>
    Shared,
    /// <summary>A prefix of one mod can cancel the method, so other mods' patches may silently not run.</summary>
    CanSkip,
    /// <summary>Several mods rewrite the same method's code (transpilers); they can break each other.</summary>
    Transpilers,
}

/// <summary>Several mods patch the same method.</summary>
public sealed record HarmonyOverlap(string Target, IReadOnlyList<HarmonyPatchInfo> Patches, HarmonyRisk Risk, FindingSeverity Severity)
{
    public IReadOnlyList<string> ModIds => Patches.Select(p => p.ModId).Distinct().ToList();
}

/// <summary>Several mods declare the same def; the one loaded last wins completely.</summary>
public sealed record DefOverride(string DefType, string DefName, IReadOnlyList<string> ModIds, FindingSeverity Severity)
{
    public string Winner => ModIds[^1];
}

/// <summary>Several mods replace or remove the same field of the same def; only the last change survives.</summary>
public sealed record PatchClash(string DefName, string Field, IReadOnlyList<DestructivePatch> Patches, FindingSeverity Severity)
{
    public IReadOnlyList<string> ModIds => Patches.Select(p => p.ModId).Distinct().ToList();
}

public static class ConflictAnalyzer
{
    public static IReadOnlyList<HarmonyOverlap> HarmonyOverlaps(ModIndex index)
    {
        var result = new List<HarmonyOverlap>();
        foreach (var group in index.HarmonyPatches.GroupBy(p => p.Target))
        {
            var mods = group.Select(p => p.ModId).Distinct().ToList();
            if (mods.Count < 2) continue;

            var patches = group.OrderBy(p => index.PositionOf(p.ModId)).ToList();
            var skippers = patches.Where(p => p.CanSkipOriginal).Select(p => p.ModId).Distinct().ToList();
            var transpilerMods = patches.Where(p => p.Kind == PatchKind.Transpiler).Select(p => p.ModId).Distinct().Count();

            // A skipping prefix only matters if some *other* mod also patches the method.
            var (risk, severity) =
                skippers.Any(s => mods.Any(m => m != s)) ? (HarmonyRisk.CanSkip, FindingSeverity.Medium)
                : transpilerMods >= 2 ? (HarmonyRisk.Transpilers, FindingSeverity.Medium)
                : (HarmonyRisk.Shared, FindingSeverity.Low);
            result.Add(new HarmonyOverlap(group.Key, patches, risk, severity));
        }
        return result.OrderByDescending(o => o.Severity).ThenByDescending(o => o.ModIds.Count).ToList();
    }

    public static IReadOnlyList<DefOverride> DefOverrides(ModIndex index)
    {
        var result = new List<DefOverride>();
        foreach (var group in index.Defs.GroupBy(d => (d.DefType, d.DefName)))
        {
            var mods = group.Select(d => d.ModId).Distinct().OrderBy(index.PositionOf).ToList();
            if (mods.Count < 2) continue;
            // Replacing a def of the game itself is usually on purpose; two mods fighting over one is not.
            var regular = mods.Count(m => !index.Mods[m].IsOfficial);
            var severity = regular >= 2 ? FindingSeverity.Medium : FindingSeverity.Info;
            result.Add(new DefOverride(group.Key.DefType, group.Key.DefName, mods, severity));
        }
        return result.OrderByDescending(o => o.Severity).ThenBy(o => o.DefName).ToList();
    }

    public static IReadOnlyList<PatchClash> PatchClashes(ModIndex index)
    {
        var result = new List<PatchClash>();
        foreach (var group in index.XmlPatches.GroupBy(p => (p.DefName, p.Field)))
        {
            var patches = group.OrderBy(p => index.PositionOf(p.ModId)).ToList();
            if (patches.Select(p => p.ModId).Distinct().Count() < 2) continue;
            result.Add(new PatchClash(group.Key.DefName, group.Key.Field, patches, FindingSeverity.Medium));
        }
        return result.OrderBy(c => c.DefName).ToList();
    }
}
