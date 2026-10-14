using RimForge.Core.Diagnostics;
using RimForge.Core.Models;

namespace RimForge.Core.Tests;

public class ModBisectTests
{
    private static ModInfo Mod(string id, params string[] deps) => new()
    {
        PackageId = id,
        DeclaredPackageId = id,
        Name = id,
        FolderPath = id,
        Source = id == KnownMods.Core ? ModSource.Official : ModSource.Workshop,
        Dependencies = deps.Select(d => new ModDependency(d, d, null, null)).ToList(),
    };

    /// <summary>Plays the game for the bisect: the problem shows whenever the culprit is on.</summary>
    private static (string? Found, int Rounds) Run(ModBisect bisect, string culprit)
    {
        var rounds = 0;
        while (!bisect.IsDone)
        {
            Assert.Contains(KnownMods.Core, bisect.TestOrder); // the game itself is always on
            bisect.Answer(bisect.TestOrder.Contains(culprit));
            rounds++;
            Assert.True(rounds < 50);
        }
        return (bisect.Culprit, rounds);
    }

    [Theory]
    [InlineData("m0")]
    [InlineData("m137")]
    [InlineData("m294")]
    public void FindsTheCulpritAmong295Mods_InAboutNineRounds(string culprit)
    {
        var mods = new[] { Mod(KnownMods.Core) }.Concat(Enumerable.Range(0, 295).Select(i => Mod("m" + i))).ToList();
        var bisect = new ModBisect(mods.Select(m => m.PackageId).ToList(), mods.ToDictionary(m => m.PackageId), [KnownMods.Core]);

        Assert.Equal(9, bisect.RoundsLeft);
        var (found, rounds) = Run(bisect, culprit);

        Assert.Equal(culprit, found);
        Assert.True(rounds <= 10, $"took {rounds} rounds");
    }

    [Fact]
    public void DependenciesComeAlong_AndCanBeTheCulprit()
    {
        // "a" and "b" need "lib"; the problem is in "lib" itself.
        var mods = new[] { Mod(KnownMods.Core), Mod("a", "lib"), Mod("b", "lib"), Mod("c"), Mod("d"), Mod("lib") };
        var bisect = new ModBisect(mods.Select(m => m.PackageId).ToList(), mods.ToDictionary(m => m.PackageId), [KnownMods.Core]);

        Assert.Contains("lib", bisect.TestOrder); // turning on "a" brings its dependency
        Assert.Equal("lib", Run(bisect, "lib").Found);
    }

    [Fact]
    public void UndoGoesBackOneRound()
    {
        var mods = new[] { Mod(KnownMods.Core) }.Concat(Enumerable.Range(0, 8).Select(i => Mod("m" + i))).ToList();
        var bisect = new ModBisect(mods.Select(m => m.PackageId).ToList(), mods.ToDictionary(m => m.PackageId), [KnownMods.Core]);
        var first = bisect.TestOrder;

        bisect.Answer(true);
        Assert.Equal(4, bisect.Candidates.Count);
        bisect.Undo();

        Assert.Equal(8, bisect.Candidates.Count);
        Assert.Equal(first, bisect.TestOrder);
    }
}

public class ModBisectSuspectTests
{
    private static ModInfo Mod(string id, params string[] deps) => new()
    {
        PackageId = id,
        DeclaredPackageId = id,
        Name = id,
        FolderPath = id,
        Source = id == KnownMods.Core ? ModSource.Official : ModSource.Workshop,
        Dependencies = deps.Select(d => new ModDependency(d, d, null, null)).ToList(),
    };

    [Fact]
    public void ChecksOnlySuspects_KeepingEveryoneElseOn()
    {
        var mods = new[] { Mod(KnownMods.Core) }.Concat(Enumerable.Range(0, 295).Select(i => Mod("m" + i)))
            .Append(Mod("needs_s3", "s3")).Concat(Enumerable.Range(0, 8).Select(i => Mod("s" + i))).ToList();
        var order = mods.Select(m => m.PackageId).ToList();
        var bisect = new ModBisect(order, mods.ToDictionary(m => m.PackageId), [KnownMods.Core],
            suspects: Enumerable.Range(0, 8).Select(i => "s" + i).ToList());

        var rounds = 0;
        while (!bisect.IsDone)
        {
            Assert.Contains("m200", bisect.TestOrder); // innocent mods stay on
            // A mod that needs a suspect is off whenever that suspect is off.
            Assert.Equal(bisect.TestOrder.Contains("s3"), bisect.TestOrder.Contains("needs_s3"));
            bisect.Answer(bisect.TestOrder.Contains("s5"));
            rounds++;
        }

        Assert.Equal("s5", bisect.Culprit);
        Assert.Equal(3, rounds);
    }
}
