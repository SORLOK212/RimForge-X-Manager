using RimForge.Core.Models;

namespace RimForge.Core.Diagnostics;

public enum FixKind
{
    /// <summary>Turn on installed mods (with their dependencies) that provide what is missing.</summary>
    EnableMods,
    /// <summary>
    /// Move the first mod below the second and remember it as an order rule. Used when a mod inherits
    /// a template from a mod that loads later: the game only looks for templates in mods loaded before.
    /// </summary>
    LoadAfter,
    /// <summary>Turn off the mod behind the problem.</summary>
    DisableMods,
    /// <summary>Run the culprit search among these suspects only.</summary>
    CheckSuspects,
    /// <summary>Let the player pick which of these mods should win, and remember it as an order rule.</summary>
    ChooseWinner,
    /// <summary>The player says this is fine: don't show it again for this modpack.</summary>
    Ignore,
}

/// <param name="ModIds">Mods to turn on or off, suspects to check, or contenders to pick from.</param>
/// <param name="Provider">For <see cref="FixKind.EnableMods"/>: the mod that has the missing thing (the rest are its dependencies).</param>
public sealed record FixOption(FixKind Kind, IReadOnlyList<string> ModIds, string? Provider = null)
{
    /// <summary>Safe fixes only turn mods on; "fix everything safe" applies them without asking about each one.</summary>
    public bool IsSafe => Kind is FixKind.EnableMods or FixKind.LoadAfter;
}

/// <summary>Works out what can be done about each diagnostics finding.</summary>
public static class FixPlanner
{
    /// <param name="activeIndex">Index of the active mods (the list the log was made with).</param>
    /// <param name="inactive">Index of installed mods that are turned off — where missing things are looked for.</param>
    public static IReadOnlyList<FixOption> ForLog(LogFinding finding, ModIndex activeIndex, ModIndex? inactive,
        IReadOnlyDictionary<string, ModInfo> installed, IReadOnlyCollection<string> active)
    {
        var fixes = new List<FixOption>();

        // The template exists in an active mod, but one that loads later than the mod using it.
        if (finding.Kind == LogErrorKind.MissingParent && finding.MissingName is { } template)
        {
            var owner = finding.Suspects.Select(s => s.ModId).FirstOrDefault(id => installed.TryGetValue(id, out var m) && !m.IsOfficial);
            var provider = activeIndex.ModsWithTemplate(template).Where(p => p != owner).OrderBy(activeIndex.PositionOf).FirstOrDefault();
            if (owner is not null && provider is not null && activeIndex.PositionOf(owner) < activeIndex.PositionOf(provider))
                fixes.Add(new FixOption(FixKind.LoadAfter, [owner, provider]));
        }

        if (inactive is not null && finding.MissingName is { } name)
        {
            var providers = finding.Kind switch
            {
                LogErrorKind.MissingParent => inactive.ModsWithTemplate(name),
                LogErrorKind.MissingType => inactive.ModsWithExactType(name),
                LogErrorKind.MissingReference => inactive.ModsDefining(name),
                _ => [],
            };
            foreach (var provider in providers.Where(installed.ContainsKey).OrderBy(id => id).Take(3))
                fixes.Add(new FixOption(FixKind.EnableMods, WithDependencies(provider, installed, active), provider));
        }

        var suspects = finding.Suspects.Select(s => s.ModId)
            .Where(id => installed.TryGetValue(id, out var m) && !m.IsOfficial).ToList();
        var serious = finding.Kind is LogErrorKind.Exception or LogErrorKind.XmlError or LogErrorKind.MissingType;
        if (serious && suspects.Count >= 2)
            fixes.Add(new FixOption(FixKind.CheckSuspects, suspects));
        // A type that exists nowhere usually means an addon made for another version of its main mod.
        if (suspects.Count >= 1 && (finding.Kind == LogErrorKind.MissingType && fixes.All(f => f.Kind != FixKind.EnableMods)
                                    || finding.Kind == LogErrorKind.Exception && suspects.Count == 1))
            fixes.Add(new FixOption(FixKind.DisableMods, [suspects[0]]));

        fixes.Add(new FixOption(FixKind.Ignore, []));
        return fixes;
    }

    public static IReadOnlyList<FixOption> ForOverlap(HarmonyOverlap overlap) =>
        [new FixOption(FixKind.CheckSuspects, overlap.ModIds), new FixOption(FixKind.Ignore, [])];

    public static IReadOnlyList<FixOption> ForOverride(DefOverride over) =>
        over.Severity == FindingSeverity.Info
            ? [new FixOption(FixKind.Ignore, [])]
            : [new FixOption(FixKind.ChooseWinner, over.ModIds), new FixOption(FixKind.Ignore, [])];

    public static IReadOnlyList<FixOption> ForClash(PatchClash clash) =>
        [new FixOption(FixKind.ChooseWinner, clash.ModIds), new FixOption(FixKind.Ignore, [])];

    /// <summary>The mod plus every installed dependency (and theirs) that is not active yet.</summary>
    public static IReadOnlyList<string> WithDependencies(string modId, IReadOnlyDictionary<string, ModInfo> installed,
        IReadOnlyCollection<string> active)
    {
        var result = new List<string>();
        var stack = new Stack<string>([modId]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (result.Contains(id) || active.Contains(id) || !installed.TryGetValue(id, out var mod)) continue;
            result.Add(id);
            foreach (var dep in mod.Dependencies)
            {
                var depId = installed.ContainsKey(dep.PackageId) ? dep.PackageId : dep.PackageId + "_steam";
                if (!active.Contains(dep.PackageId) && !active.Contains(dep.PackageId + "_steam")) stack.Push(depId);
            }
        }
        return result;
    }
}
