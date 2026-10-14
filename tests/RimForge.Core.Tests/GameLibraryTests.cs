using RimForge.Core.Library;
using RimForge.Core.Models;
using RimForge.Core.Paths;

namespace RimForge.Core.Tests;

public sealed class GameLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-lib-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Junction.DeleteTree(_root);
    }

    private string Make(string relative, string text = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Junction_ShowsTheTarget_AndRemovingItKeepsTheTarget()
    {
        Make(@"store\mod\About\About.xml", "about");
        var link = Path.Combine(_root, @"game\Mods\mod");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        Junction.Create(link, Path.Combine(_root, @"store\mod"));

        Assert.True(Junction.IsLink(link));
        Assert.Equal("about", File.ReadAllText(Path.Combine(link, "About", "About.xml")));
        Junction.DeleteTree(Path.Combine(_root, "game"));
        Assert.False(Directory.Exists(link));
        Assert.Equal("about", File.ReadAllText(Path.Combine(_root, @"store\mod\About\About.xml")));
    }

    private (GamePaths Game, List<ModInfo> Mods) FakeGame(string version)
    {
        Make(@"steam\RimWorld\Version.txt", version);
        Make(@"steam\RimWorld\RimWorldWin64.exe", "exe");
        Make(@"steam\RimWorld\Data\Core\About\About.xml", "core");
        Make(@"steam\RimWorld\Mods\LocalMod\About\About.xml", "local");
        Make(@"steam\workshop\2009463077\About\About.xml", "harmony");
        Make(@"steam\workshop\2009463077\1.6\Assemblies\0Harmony.dll", "dll");
        Make(@"data\Config\ModsConfig.xml", "list");
        Make(@"data\Config\Mod_2009463077_HarmonyMod.xml", "settings");
        Make(@"data\Saves\colony.rws", "save");
        Make(@"data\Player.log", "log");
        var game = new GamePaths { GameDir = Path.Combine(_root, @"steam\RimWorld"), ConfigDir = Path.Combine(_root, "data"), Store = GameStore.Steam };
        var mods = new List<ModInfo>
        {
            new() { PackageId = "me.local", DeclaredPackageId = "me.local", Name = "Local", Source = ModSource.Local,
                FolderPath = Path.Combine(_root, @"steam\RimWorld\Mods\LocalMod") },
            new() { PackageId = "brrainz.harmony", DeclaredPackageId = "brrainz.harmony", Name = "Harmony", Source = ModSource.Workshop,
                FolderPath = Path.Combine(_root, @"steam\workshop\2009463077"), WorkshopId = 2009463077 },
        };
        return (game, mods);
    }

    [Fact]
    public void Snapshot_CopiesTheGame_LinksTheMods_AndKeepsTheSaveData()
    {
        var (game, mods) = FakeGame("1.6.4871 rev590");
        var library = new GameLibrary(Path.Combine(_root, "library"));
        var keyed = mods.Select(m => (m, GameLibrary.StoreKey(m))).ToList();

        var plan = library.Plan(game, keyed);
        Assert.Equal(2, plan.NewMods);
        var copy = library.Snapshot(game, keyed);

        Assert.Equal("1.6.4871", copy.Id);
        Assert.Equal("1.6.4871 rev590", File.ReadAllText(Path.Combine(copy.GameDir, "Version.txt")));
        Assert.True(File.Exists(Path.Combine(copy.GameDir, @"Data\Core\About\About.xml")));
        // Mods are links into the store, under their usual folder names (mod settings files keep matching).
        var harmony = Path.Combine(copy.GameDir, @"Mods\2009463077");
        Assert.True(Junction.IsLink(harmony));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(harmony, @"1.6\Assemblies\0Harmony.dll")));
        Assert.True(Junction.IsLink(Path.Combine(copy.GameDir, @"Mods\LocalMod")));
        // Its own save data, without the logs.
        Assert.Equal("save", File.ReadAllText(Path.Combine(copy.DataDir, @"Saves\colony.rws")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(copy.DataDir, @"Config\Mod_2009463077_HarmonyMod.xml")));
        Assert.False(File.Exists(Path.Combine(copy.DataDir, "Player.log")));
        Assert.Single(library.Load());

        // Steam updating the mod later does not touch the frozen version.
        File.WriteAllText(Path.Combine(_root, @"steam\workshop\2009463077\1.6\Assemblies\0Harmony.dll"), "new");
        Assert.Equal("dll", File.ReadAllText(Path.Combine(harmony, @"1.6\Assemblies\0Harmony.dll")));

        Assert.Throws<InvalidOperationException>(() => library.Snapshot(game, keyed));
    }

    [Fact]
    public void ACopy_StartsDirectly_WithItsOwnSaveData()
    {
        var (game, mods) = FakeGame("1.6.4871 rev590");
        var copy = new GameLibrary(Path.Combine(_root, "library")).Snapshot(game, mods.Select(m => (m, GameLibrary.StoreKey(m))).ToList());

        var start = RimForge.Core.Launch.GameLauncher.BuildStartInfo(copy.Paths, Path.Combine(_root, "steam"), ["-savedatafolder=" + copy.DataDir]);

        // Not through Steam: that would start the installed game instead of the copy.
        Assert.Equal(Path.Combine(copy.GameDir, "RimWorldWin64.exe"), start.FileName);
        Assert.Equal(copy.GameDir, start.WorkingDirectory);
        Assert.Equal(["-savedatafolder=" + copy.DataDir], start.ArgumentList);
    }

    [Fact]
    public void ModsSharedByTwoCopies_AreStoredOnce_AndRemovedWithTheLastOne()
    {
        var (game, mods) = FakeGame("1.6.4871 rev590");
        var library = new GameLibrary(Path.Combine(_root, "library"));
        var keyed = mods.Select(m => (m, GameLibrary.StoreKey(m))).ToList();
        var first = library.Snapshot(game, keyed);

        // A new game version, Harmony updated, the local mod unchanged.
        File.WriteAllText(Path.Combine(_root, @"steam\RimWorld\Version.txt"), "1.7.5000 rev1");
        File.WriteAllText(Path.Combine(_root, @"steam\workshop\2009463077\1.6\Assemblies\0Harmony.dll"), "new");
        var keyed2 = mods.Select(m => (m, GameLibrary.StoreKey(m))).ToList();
        Assert.Equal(1, library.Plan(game, keyed2).NewMods);
        Assert.Equal(1, library.Plan(game, keyed2).ReusedMods);
        var second = library.Snapshot(game, keyed2);

        Assert.Equal(3, Directory.GetDirectories(library.ModsDir).Length);
        library.Delete(first);
        // The old Harmony goes, the local mod stays: the 1.7 copy still uses it.
        Assert.Equal(2, Directory.GetDirectories(library.ModsDir).Length);
        Assert.True(File.Exists(Path.Combine(second.GameDir, @"Mods\LocalMod\About\About.xml")));
        Assert.False(Directory.Exists(first.GameDir));
        Assert.True(File.Exists(Path.Combine(_root, @"steam\RimWorld\Mods\LocalMod\About\About.xml"))); // the original is never touched
        library.Delete(second);
        Assert.Empty(Directory.GetDirectories(library.ModsDir));
    }
}
