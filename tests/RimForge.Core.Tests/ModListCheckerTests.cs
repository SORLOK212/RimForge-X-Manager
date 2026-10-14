using RimForge.Core.Checks;
using RimForge.Core.Models;

namespace RimForge.Core.Tests;

public class ModListCheckerTests
{
    private static ModInfo Mod(string id, ModSource source = ModSource.Workshop, string[]? versions = null,
        string[]? deps = null, string[]? after = null, string[]? incompatible = null, string[]? before = null) => new()
    {
        PackageId = id,
        DeclaredPackageId = id,
        Name = id,
        FolderPath = id,
        Source = source,
        SupportedVersions = versions ?? ["1.6"],
        Dependencies = (deps ?? []).Select(d => new ModDependency(d, d, null, null)).ToList(),
        LoadAfter = after ?? [],
        LoadBefore = before ?? [],
        IncompatibleWith = incompatible ?? [],
    };

    private static Dictionary<string, ModInfo> Installed(params ModInfo[] mods) => mods.ToDictionary(m => m.PackageId);

    [Fact]
    public void CleanList_HasNoIssues()
    {
        var installed = Installed(Mod("brrainz.harmony", before: [KnownMods.Core]), Mod(KnownMods.Core, ModSource.Official, versions: []),
            Mod("a", deps: ["brrainz.harmony"], after: [KnownMods.Core]));
        Assert.Empty(ModListChecker.Check(["brrainz.harmony", KnownMods.Core, "a"], installed, "1.6"));
    }

    [Fact]
    public void FindsInactiveAndMissingDependencies()
    {
        var installed = Installed(Mod("a", deps: ["b", "c"]), Mod("b"));
        var issues = ModListChecker.Check(["a"], installed, "1.6");

        Assert.Contains(issues, i => i.Kind == IssueKind.DependencyInactive && i.OtherId == "b");
        Assert.Contains(issues, i => i.Kind == IssueKind.DependencyMissing && i.OtherId == "c");
        Assert.Equal(IssueSeverity.Error, ModListChecker.Worst(issues));
    }

    [Fact]
    public void FindsWrongOrder_Version_AndIncompatibility_Once()
    {
        var installed = Installed(Mod("a", after: ["b"], incompatible: ["c"]), Mod("b"), Mod("c", versions: ["1.4"], incompatible: ["a"]));
        var issues = ModListChecker.Check(["a", "b", "c"], installed, "1.6");

        Assert.Contains(issues, i => i.Kind == IssueKind.ShouldLoadAfter && i.ModId == "a" && i.OtherId == "b");
        Assert.Contains(issues, i => i.Kind == IssueKind.UnsupportedVersion && i.ModId == "c");
        Assert.Single(issues, i => i.Kind == IssueKind.Incompatible);
    }

    [Fact]
    public void RegularModAboveTheGame_IsReported()
    {
        var installed = Installed(Mod("a"), Mod(KnownMods.Core, ModSource.Official, versions: []));
        var issue = Assert.Single(ModListChecker.Check(["a", KnownMods.Core], installed, "1.6"));
        Assert.Equal(IssueKind.ShouldLoadAfter, issue.Kind);
        Assert.Equal(KnownMods.Core, issue.OtherId);
    }

    [Fact]
    public void ReportsActiveButNotInstalled()
    {
        var issues = ModListChecker.Check(["ghost"], Installed(), "1.6");
        Assert.Equal(IssueKind.NotInstalled, Assert.Single(issues).Kind);
    }
}
