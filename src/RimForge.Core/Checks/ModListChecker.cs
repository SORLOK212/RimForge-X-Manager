using RimForge.Core.Databases;
using RimForge.Core.Models;
using RimForge.Core.Sorting;

namespace RimForge.Core.Checks;

public enum IssueSeverity { Info, Warning, Error }

public enum IssueKind
{
    /// <summary>An active mod needs <see cref="ModIssue.OtherId"/>, which is installed but turned off.</summary>
    DependencyInactive,
    /// <summary>An active mod needs <see cref="ModIssue.OtherId"/>, which is not installed at all.</summary>
    DependencyMissing,
    /// <summary>Two active mods declare each other incompatible.</summary>
    Incompatible,
    /// <summary>The mod does not list the current game version as supported.</summary>
    UnsupportedVersion,
    /// <summary>The mod asks to load after <see cref="ModIssue.OtherId"/> but sits above it.</summary>
    ShouldLoadAfter,
    /// <summary>The mod asks to load before <see cref="ModIssue.OtherId"/> but sits below it.</summary>
    ShouldLoadBefore,
    /// <summary>The active list mentions a mod that is not installed.</summary>
    NotInstalled,
    /// <summary>About.xml is missing data or could not be read.</summary>
    BrokenAbout,
    /// <summary>Order rules for these mods contradict each other (a mistake in a mod's description); moving mods can't fix it.</summary>
    Contradiction,
    /// <summary>The same mod is on twice (a local copy and a Workshop copy, <see cref="ModIssue.OtherId"/>).</summary>
    DuplicateActive,
    /// <summary>Several folders hold a mod with this package id; the game will pick one of them (names in <see cref="ModIssue.Detail"/>).</summary>
    DuplicateInstalled,
    /// <summary>The mod needs an official expansion (<see cref="ModIssue.OtherId"/>) that is not installed.</summary>
    DlcMissing,
    /// <summary>The mod was made for the previous game version, has no code of its own and will most likely work.</summary>
    OlderVersionLikelyOk,
    /// <summary>The mod is abandoned; <see cref="ModIssue.Replacement"/> is the maintained version.</summary>
    UseInstead,
}

/// <summary>Extra knowledge for the check: community databases and what the disk scan saw.</summary>
public sealed record CheckExtras
{
    /// <summary>Mods checked by the community to work on the current game version (No Version Warning).</summary>
    public VerifiedMods? Verified { get; init; }
    /// <summary>Abandoned mods and their replacements (Use This Instead).</summary>
    public ModReplacements? Replacements { get; init; }
    /// <summary>Package id → names of all the folders that hold it, when there is more than one.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? DuplicateFolders { get; init; }
    /// <summary>Whether a mod ships its own code (DLLs). Unknown when not given.</summary>
    public Func<ModInfo, bool>? HasCode { get; init; }
}

/// <summary>One problem found in a mod list. Messages are built by the UI from Kind + ids, so they can be translated.</summary>
public sealed record ModIssue(IssueSeverity Severity, IssueKind Kind, string ModId, string? OtherId = null, string? OtherName = null, string? Detail = null)
{
    /// <summary>For order problems: the rule that is broken.</summary>
    public OrderRule? Rule { get; init; }

    /// <summary>For <see cref="IssueKind.UseInstead"/>: the maintained mod.</summary>
    public ModReplacement? Replacement { get; init; }
}

/// <summary>Pre-flight check of an ordered list of active mods (the "traffic light").</summary>
public static class ModListChecker
{
    /// <param name="rules">Ordering rules; built from About.xml files alone when not given.</param>
    public static IReadOnlyList<ModIssue> Check(IReadOnlyList<string> activeIds, IReadOnlyDictionary<string, ModInfo> installed,
        string? gameVersion, RuleSet? rules = null, CheckExtras? extras = null)
    {
        extras ??= new CheckExtras();
        rules ??= RuleSet.Build(activeIds, installed, community: null);
        var issues = new List<ModIssue>();
        var position = new Dictionary<string, int>();
        for (var i = 0; i < activeIds.Count; i++) position.TryAdd(activeIds[i], i);

        foreach (var id in activeIds)
        {
            if (!installed.TryGetValue(id, out var mod))
            {
                issues.Add(new(IssueSeverity.Error, IssueKind.NotInstalled, id));
                continue;
            }

            if (mod.LoadError is not null)
                issues.Add(new(IssueSeverity.Warning, IssueKind.BrokenAbout, id, Detail: mod.LoadError));

            if (!mod.IsOfficial && extras.Replacements?.For(mod.WorkshopId) is { } replacement && replacement.NewWorkshopId != mod.WorkshopId
                && replacement.NewSupports(gameVersion) && !IsActiveWorkshop(replacement.NewWorkshopId, activeIds, installed))
            {
                // Only worth a warning when the old mod does not support this game version; otherwise a hint.
                var outdated = gameVersion is not null && !mod.SupportsVersion(gameVersion);
                issues.Add(new(outdated ? IssueSeverity.Warning : IssueSeverity.Info, IssueKind.UseInstead, id, replacement.NewPackageId, replacement.NewName)
                    { Replacement = replacement });
            }
            else if (gameVersion is not null && !mod.IsOfficial && !mod.SupportsVersion(gameVersion)
                && extras.Verified?.Contains(mod.DeclaredPackageId) != true)
            {
                var likelyOk = PreviousVersion(gameVersion) is { } previous && mod.SupportedVersions.Contains(previous)
                    && extras.HasCode is { } hasCode && !hasCode(mod);
                issues.Add(likelyOk
                    ? new(IssueSeverity.Info, IssueKind.OlderVersionLikelyOk, id, Detail: string.Join(", ", mod.SupportedVersions))
                    : new(IssueSeverity.Warning, IssueKind.UnsupportedVersion, id, Detail: string.Join(", ", mod.SupportedVersions)));
            }

            if (extras.DuplicateFolders?.GetValueOrDefault(id) is { Count: > 1 } copies)
                issues.Add(new(IssueSeverity.Warning, IssueKind.DuplicateInstalled, id, Detail: string.Join(", ", copies)));
            if (id.EndsWith("_steam") && position.ContainsKey(id[..^"_steam".Length]))
                issues.Add(new(IssueSeverity.Error, IssueKind.DuplicateActive, id, id[..^"_steam".Length]));

            foreach (var dep in mod.Dependencies)
            {
                if (IsActive(dep.PackageId, position)) continue;
                var installedDep = IsInstalled(dep.PackageId, installed);
                var kind = installedDep ? IssueKind.DependencyInactive
                    : IsOfficialId(dep.PackageId) ? IssueKind.DlcMissing
                    : IssueKind.DependencyMissing;
                issues.Add(new(IssueSeverity.Error, kind, id, dep.PackageId, dep.DisplayName, dep.SteamWorkshopUrl));
            }

            foreach (var other in mod.IncompatibleWith)
            {
                // Report each pair once.
                if (IsActive(other, position) && (string.CompareOrdinal(id, other) < 0 || !Declares(installed, other, id)))
                    issues.Add(new(IssueSeverity.Error, IssueKind.Incompatible, id, other, NameOf(other, installed)));
            }
        }

        // Order: the same rules the sorter uses, reported on the mod the rule belongs to.
        foreach (var rule in rules.Orders)
        {
            if (!position.TryGetValue(rule.Before, out var before) || !position.TryGetValue(rule.After, out var after) || before < after)
                continue;
            var kind = rule.DeclaredBy == rule.After ? IssueKind.ShouldLoadAfter : IssueKind.ShouldLoadBefore;
            issues.Add(new(IssueSeverity.Warning, kind, rule.DeclaredBy, rule.Other, NameOf(rule.Other, installed), rule.Comment) { Rule = rule });
        }

        foreach (var pair in rules.CommunityIncompatible)
            if (!issues.Any(i => i.Kind == IssueKind.Incompatible && (i.ModId, i.OtherId) is var (m, o) && ((m == pair.A && o == pair.B) || (m == pair.B && o == pair.A))))
                issues.Add(new(IssueSeverity.Error, IssueKind.Incompatible, pair.A, pair.B, NameOf(pair.B, installed), pair.Comment));

        return issues;
    }

    /// <summary>Package ids of the game and its expansions.</summary>
    public static bool IsOfficialId(string id) => id == KnownMods.Core || id.StartsWith(KnownMods.Core + ".");

    /// <summary>"1.7" → "1.6". Null when there is no earlier 1.x version.</summary>
    public static string? PreviousVersion(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && minor > 0
            ? $"{major}.{minor - 1}" : null;
    }

    private static bool IsActiveWorkshop(ulong workshopId, IReadOnlyList<string> activeIds, IReadOnlyDictionary<string, ModInfo> installed) =>
        activeIds.Any(a => installed.TryGetValue(a, out var m) && m.WorkshopId == workshopId);

    public static IssueSeverity? Worst(IEnumerable<ModIssue> issues) =>
        issues.Select(i => (IssueSeverity?)i.Severity).DefaultIfEmpty(null).Max();

    // Ids in rules can refer to a duplicate that the game renamed with "_steam".
    private static bool IsActive(string id, Dictionary<string, int> position) =>
        position.ContainsKey(id) || position.ContainsKey(id + "_steam");

    private static bool IsInstalled(string id, IReadOnlyDictionary<string, ModInfo> installed) =>
        installed.ContainsKey(id) || installed.ContainsKey(id + "_steam");

    private static bool Declares(IReadOnlyDictionary<string, ModInfo> installed, string modId, string otherId) =>
        installed.TryGetValue(modId, out var m) && m.IncompatibleWith.Contains(otherId);

    private static string? NameOf(string id, IReadOnlyDictionary<string, ModInfo> installed) =>
        installed.TryGetValue(id, out var m) ? m.Name : null;
}
