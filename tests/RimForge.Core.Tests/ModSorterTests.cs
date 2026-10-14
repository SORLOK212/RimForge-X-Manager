using RimForge.Core.Models;
using RimForge.Core.Sorting;

namespace RimForge.Core.Tests;

public class ModSorterTests
{
    private static ModInfo Mod(string id, ModSource source = ModSource.Workshop, string[]? deps = null,
        string[]? after = null, string[]? before = null) => new()
    {
        PackageId = id,
        DeclaredPackageId = id,
        Name = id,
        FolderPath = id,
        Source = source,
        Dependencies = (deps ?? []).Select(d => new ModDependency(d, d, null, null)).ToList(),
        LoadAfter = after ?? [],
        LoadBefore = before ?? [],
    };

    private static Dictionary<string, ModInfo> Installed(params ModInfo[] mods) => mods.ToDictionary(m => m.PackageId);

    private static SortResult Sort(string[] order, Dictionary<string, ModInfo> installed, CommunityRules? community = null) =>
        ModSorter.Sort(order, RuleSet.Build(order, installed, community));

    [Fact]
    public void CorrectOrder_StaysExactlyTheSame()
    {
        var installed = Installed(Mod("harmony", before: [KnownMods.Core]), Mod(KnownMods.Core, ModSource.Official),
            Mod("lib"), Mod("x", deps: ["lib"]), Mod("y"));
        string[] order = ["harmony", KnownMods.Core, "y", "lib", "x"];

        var result = Sort(order, installed);

        Assert.Equal(order, result.Order);
        Assert.False(result.Changed);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void OnlyTheMisplacedModMoves()
    {
        var installed = Installed(Mod("a", after: ["c"]), Mod("b"), Mod("c"), Mod("d"));

        var result = Sort(["a", "b", "c", "d"], installed);

        Assert.Equal(["b", "c", "a", "d"], result.Order);
        var move = Assert.Single(result.Moves);
        Assert.Equal("a", move.Id);
        Assert.Equal(RuleSource.LoadAfter, move.Reason?.Source);
    }

    [Fact]
    public void DependencyGoesAboveTheModThatNeedsIt()
    {
        var installed = Installed(Mod("x", deps: ["lib"]), Mod("lib"));
        Assert.Equal(["lib", "x"], Sort(["x", "lib"], installed).Order);
    }

    [Fact]
    public void RegularModsGoBelowTheGame_ButHarmonyStaysOnTop()
    {
        var installed = Installed(Mod("harmony", before: [KnownMods.Core]), Mod("m"), Mod(KnownMods.Core, ModSource.Official),
            Mod("ludeon.rimworld.royalty", ModSource.Official));

        var result = Sort(["m", "harmony", KnownMods.Core, "ludeon.rimworld.royalty"], installed);

        Assert.Equal(["harmony", KnownMods.Core, "ludeon.rimworld.royalty", "m"], result.Order);
        Assert.Equal(RuleSource.Official, Assert.Single(result.Moves).Reason?.Source);
    }

    [Fact]
    public void CommunityRules_LoadAfterAndBottom()
    {
        var community = CommunityRules.Parse("""
            {
              "timestamp": 1789239946,
              "rules": {
                "Some.Patch": { "loadAfter": { "big.mod": { "name": ["Big Mod"], "comment": ["Patches Big Mod"] } } },
                "krkr.rocketman": { "loadBottom": { "comment": "RocketMan should always be loaded last.", "value": true } }
              }
            }
            """);
        var installed = Installed(Mod("krkr.rocketman"), Mod("some.patch"), Mod("big.mod"), Mod("other"));

        var result = Sort(["krkr.rocketman", "some.patch", "big.mod", "other"], installed, community);

        Assert.Equal(["big.mod", "some.patch", "other", "krkr.rocketman"], result.Order);
        Assert.Equal(2, community.Count);
        Assert.Equal("Patches Big Mod", community.Rules["some.patch"].LoadAfter["big.mod"]);
    }

    [Fact]
    public void ContradictingRules_AreReported_AndThoseModsStayPut()
    {
        // Like a real mod that requires another one and also asks to load before it.
        var installed = Installed(Mod("x", deps: ["lib"], before: ["lib"]), Mod("y"), Mod("lib"), Mod("z"));

        var result = Sort(["x", "y", "lib", "z"], installed);

        Assert.Equal(["x", "y", "lib", "z"], result.Order);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(["x", "lib"], conflict.Mods);
        Assert.Equal(RuleSource.Dependency, Assert.Single(conflict.IgnoredRules).Source);
    }

    [Fact]
    public void ModWithAddons_IsNotDraggedAroundForOnePatch()
    {
        // "patch" must load after "big", which sits below it together with three addons.
        // Fixing that takes one move, not four.
        var installed = Installed(Mod("patch", after: ["big"]), Mod("big"),
            Mod("addon1", deps: ["big"]), Mod("addon2", deps: ["big"]), Mod("addon3", deps: ["big"]));

        var result = Sort(["patch", "big", "addon1", "addon2", "addon3"], installed);

        Assert.Single(result.Moves);
        Assert.True(result.Order.ToList().IndexOf("patch") > result.Order.ToList().IndexOf("big"));
    }

    [Fact]
    public void LiftsAModUpWhenThatIsCheaper()
    {
        // "lib" is needed by "a", and "b" needs "a", but "lib" sits at the bottom:
        // lifting "lib" is one move, sinking "a" and "b" below it would be two.
        var installed = Installed(Mod("a", deps: ["lib"]), Mod("b", deps: ["a"]), Mod("c"), Mod("lib"));

        var result = Sort(["a", "b", "c", "lib"], installed);

        Assert.Equal(["lib", "a", "b", "c"], result.Order);
        Assert.Equal("lib", Assert.Single(result.Moves).Id);
    }

    private static ModInfo Named(string id, string name, string[]? deps = null, string[]? after = null, string[]? before = null) =>
        Mod(id, deps: deps, after: after, before: before) with { Name = name };

    [Fact]
    public void FullSort_BuildsGroupsAndKeepsEveryRule()
    {
        var installed = Installed(
            Named("zeta", "Zeta Weapons", deps: ["vef"]),
            Named("alpha", "Alpha Animals"),
            Named("ru", "Alpha Animals rus", after: ["alpha"]),
            Named("patch", "Zeta - Alpha Patch", after: ["zeta", "alpha"]),
            Named("vef", "Vanilla Expanded Framework"),
            Named("harmony", "Harmony", before: [KnownMods.Core]),
            Mod(KnownMods.Core, ModSource.Official) with { Name = "Core" },
            Mod("ludeon.rimworld.biotech", ModSource.Official) with { Name = "Biotech" });
        string[] messy = ["ru", "zeta", "patch", KnownMods.Core, "alpha", "ludeon.rimworld.biotech", "vef", "harmony"];

        var rules = RuleSet.Build(messy, installed, null);
        var result = ModSorter.SortFull(messy, rules, installed);

        Assert.Equal(["harmony", KnownMods.Core, "ludeon.rimworld.biotech", "vef", "alpha", "zeta", "patch", "ru"], result.Order);
        Assert.Equal(SortGroup.Library, result.Groups!["vef"]);
        Assert.Equal(SortGroup.Translation, result.Groups["ru"]);
        Assert.Empty(ModSorter.Sort(result.Order, rules).Moves); // nothing left to fix
    }

    [Fact]
    public void FullSort_RuleBeatsGroup()
    {
        // A translation that some mod must load after still goes above that mod.
        var installed = Installed(Named("tr", "Big Mod translation"), Named("x", "Aardvark", after: ["tr"]));

        var result = ModSorter.SortFull(["x", "tr"], RuleSet.Build(["x", "tr"], installed, null), installed);

        Assert.Equal(["tr", "x"], result.Order);
    }

    [Fact]
    public void PlayersRule_WinsOverAContradictingAuthorRule()
    {
        // The author of "a" says it loads before "b"; the player says "b" first.
        var installed = Installed(Mod("a", before: ["b"]), Mod("b"), Mod("c"));
        string[] order = ["a", "b", "c"];
        var rules = RuleSet.Build(order, installed, null, [new UserRule("b", "a")]);

        var result = ModSorter.Sort(order, rules);

        Assert.Equal(["b", "a", "c"], result.Order);
        Assert.Equal(RuleSource.LoadBefore, Assert.Single(Assert.Single(result.Conflicts).IgnoredRules).Source);
    }

    [Fact]
    public void Explain_ListsModsAboveAndBelow()
    {
        var installed = Installed(Mod("lib"), Mod("x", deps: ["lib"]), Mod("patch", after: ["x"]));
        var rules = RuleSet.Build(["lib", "x", "patch"], installed, null);

        var reasons = ModSorter.Explain("x", rules);

        Assert.Contains(reasons, r => r.OtherIsAbove && r.Rule.Before == "lib" && r.Rule.Source == RuleSource.Dependency);
        Assert.Contains(reasons, r => !r.OtherIsAbove && r.Rule.After == "patch");
    }

    [Fact]
    public void Phases_FollowWhatIsInsideTheMod()
    {
        Content.ModProfile P(int defs = 0, int patches = 0, int asm = 0, int tex = 0, bool lang = false) =>
            new() { Defs = defs, Patches = patches, Assemblies = asm, Textures = tex, HasLanguages = lang };

        Assert.Equal(SortGroup.Graphics, ModSorter.PhaseOf("Nice Walls", false, P(tex: 300)));
        Assert.Equal(SortGroup.Patch, ModSorter.PhaseOf("VE + CE", false, P(patches: 40)));
        Assert.Equal(SortGroup.Tweak, ModSorter.PhaseOf("Better Tooltips", false, P(asm: 1, tex: 2)));
        Assert.Equal(SortGroup.Content, ModSorter.PhaseOf("Big Guns", false, P(defs: 80, patches: 5, asm: 1, tex: 200)));
        Assert.Equal(SortGroup.Translation, ModSorter.PhaseOf("Big Guns RU", false, P(lang: true)));
        Assert.Equal(SortGroup.Library, ModSorter.PhaseOf("Vehicle Framework", false, P(defs: 30, asm: 2)));
        Assert.Equal(SortGroup.Library, ModSorter.PhaseOf("Anything", true, P(tex: 5)));
        Assert.Equal(SortGroup.Content, ModSorter.PhaseOf("Insectoids", true, P(defs: 300, asm: 1, tex: 400))); // add-ons need it, but it is content
        Assert.Equal(SortGroup.Patch, ModSorter.PhaseOf("Guns Patch", false, null)); // no scan yet: the name decides
        Assert.Equal(SortGroup.Content, ModSorter.PhaseOf("Guns", false, null));
    }
}
