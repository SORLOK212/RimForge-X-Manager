using System.IO.Compression;
using RimForge.Core.Models;
using RimForge.Core.Profiles;
using RimForge.Core.Sharing;
using RimForge.Core.Sorting;

namespace RimForge.Core.Tests;

public sealed class ModpackFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-pack-" + Guid.NewGuid().ToString("N"));
    private string Config => Path.Combine(_root, "config");

    public ModpackFileTests() => Directory.CreateDirectory(Path.Combine(Config, "Config"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private ModInfo Mod(string id, string folder, ModSource source = ModSource.Workshop, ulong? workshopId = null)
    {
        var path = Path.Combine(_root, source == ModSource.Local ? "Mods" : "workshop", folder);
        Directory.CreateDirectory(Path.Combine(path, "About"));
        File.WriteAllText(Path.Combine(path, "About", "About.xml"), $"<ModMetaData><packageId>{id}</packageId></ModMetaData>");
        Directory.CreateDirectory(Path.Combine(path, "Textures"));
        File.WriteAllBytes(Path.Combine(path, "Textures", "a.png"), [1, 2, 3]);
        return new ModInfo { PackageId = id, DeclaredPackageId = id, Name = "Mod " + id, FolderPath = path, Source = source, WorkshopId = workshopId };
    }

    private static ModInfo Official(string id) =>
        new() { PackageId = id, DeclaredPackageId = id, Name = id, FolderPath = "C:/game/Data/" + id, Source = ModSource.Official };

    [Fact]
    public void SettingsFiles_AreFoundByFolderName()
    {
        var mod = Mod("a.b", "2009");
        File.WriteAllText(Path.Combine(Config, "Config", "Mod_2009_ASettings.xml"), "<x/>");
        File.WriteAllText(Path.Combine(Config, "Config", "Mod_20091_Other.xml"), "<y/>");
        File.WriteAllText(Path.Combine(Config, "Config", "ModsConfig.xml"), "<z/>");

        var files = ModpackFile.SettingsFilesOf(mod, Config);

        Assert.Equal(["Mod_2009_ASettings.xml"], files.Select(Path.GetFileName));
        Assert.Equal("ASettings", ModpackFile.SettingsClass(files[0], "2009"));
    }

    [Fact]
    public void Modpack_IsWrittenAndReadBack_WithSettingsAndMods()
    {
        var core = Official(KnownMods.Core);
        var biotech = Official("ludeon.rimworld.biotech");
        var harmony = Mod("brrainz.harmony", "2009463077", workshopId: 2009463077);
        var local = Mod("me.mymod", "My Mod", ModSource.Local);
        File.WriteAllText(Path.Combine(Config, "Config", "Mod_2009463077_HarmonySettings.xml"), "<harmony/>");
        var installed = new[] { core, biotech, harmony, local }.ToDictionary(m => m.PackageId);
        var hints = new Dictionary<string, ProfileMod> { ["gone.mod"] = new("gone.mod", "Gone", 42) };
        var updated = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var manifest = ModpackFile.Build("Pack", "  for friends ", [core.PackageId, biotech.PackageId, harmony.PackageId, local.PackageId, "gone.mod"],
            installed, hints, "1.6.4633", [new UserRule("a", "b")], m => updated, m => 100);
        var parts = ModpackFile.Prepare(manifest, installed, Config, includeSettings: true, includeMods: true);
        var file = Path.Combine(_root, "pack" + ModpackFile.Extension);
        ModpackFile.Write(file, manifest, parts);

        var read = ModpackFile.Read(file);
        Assert.Equal("Pack", read.Name);
        Assert.Equal("for friends", read.Description);
        Assert.Equal(["ludeon.rimworld", "ludeon.rimworld.biotech", "brrainz.harmony", "me.mymod", "gone.mod"], read.Mods.Select(m => m.Id));
        Assert.Equal(["ludeon.rimworld.biotech"], read.Expansions.Select(m => m.Id));
        Assert.Equal(updated, read.Mods[2].UpdatedUtc);
        Assert.Equal(42UL, read.Mods[4].WorkshopId);
        Assert.Equal("workshop", read.Mods[4].Source);
        Assert.Null(read.Mods[0].Folder); // the game itself is never packed
        Assert.True(read.HasModFiles);
        Assert.Equal(1, read.SettingsCount);
        Assert.Single(read.Rules);

        var settings = ModpackFile.ReadSettings(file, read.Mods[2]);
        Assert.Equal("<harmony/>", settings["HarmonySettings"]);

        var modsDir = Path.Combine(_root, "OtherPC", "Mods");
        Directory.CreateDirectory(modsDir);
        var unpacked = ModpackFile.ExtractMod(file, read.Mods[3], modsDir);
        Assert.Equal("Mod me.mymod", Path.GetFileName(unpacked));
        Assert.True(File.Exists(Path.Combine(unpacked, "About", "About.xml")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(unpacked, "Textures", "a.png")));
        // A second copy does not overwrite the first.
        Assert.Equal("Mod me.mymod (2)", Path.GetFileName(ModpackFile.ExtractMod(file, read.Mods[3], modsDir)));
    }

    [Fact]
    public void PathsLeavingTheModsFolder_AreRefused()
    {
        var file = Path.Combine(_root, "evil" + ModpackFile.Extension);
        var manifest = new ModpackManifest { Name = "Evil", Mods = [new ModpackEntry { Id = "x", Name = "X", Folder = "x" }] };
        ModpackFile.Write(file, manifest, []);
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry("mods/x/../../../escape.txt").Open()))
            writer.Write("bad");
        var modsDir = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(modsDir);

        Assert.Throws<InvalidDataException>(() => ModpackFile.ExtractMod(file, manifest.Mods[0], modsDir));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.Empty(Directory.GetDirectories(modsDir));
    }

    [Fact]
    public void OtherFiles_AreNotModpacks()
    {
        var file = Path.Combine(_root, "photo.rimforge");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create)) zip.CreateEntry("hello.txt");
        Assert.Throws<InvalidDataException>(() => ModpackFile.Read(file));
    }

    [Fact]
    public void Plan_SplitsWhatIsHere_WhatToDownload_AndWhatIsInTheFile()
    {
        var manifest = new ModpackManifest
        {
            Name = "P",
            Mods =
            [
                new() { Id = "ludeon.rimworld", Source = "official" },
                new() { Id = "ludeon.rimworld.anomaly", Source = "official" },
                new() { Id = "brrainz.harmony", WorkshopId = 1, Source = "workshop" },
                new() { Id = "renamed.here", WorkshopId = 2, Source = "workshop" },
                new() { Id = "to.download", WorkshopId = 3, Source = "workshop" },
                new() { Id = "packed.local", Folder = "packed.local" },
                new() { Id = "only.author" },
            ],
        };
        var installed = new[]
        {
            Official("ludeon.rimworld"),
            Mod("brrainz.harmony", "1", workshopId: 1),
            Mod("other.name", "2", workshopId: 2),
        };

        var plan = ModpackPlan.For(manifest, installed);

        Assert.Equal(3, plan.Have.Count);
        Assert.Equal(["ludeon.rimworld.anomaly"], plan.MissingDlcs.Select(m => m.Id));
        Assert.Equal(["to.download"], plan.Download.Select(m => m.Id));
        Assert.Equal(["packed.local"], plan.FromFile.Select(m => m.Id));
        Assert.Equal(["only.author"], plan.Unavailable.Select(m => m.Id));
        // A mod found by its Workshop id keeps the id it has here.
        Assert.Contains("other.name", plan.Order);
        Assert.DoesNotContain("renamed.here", plan.Order);
    }

    [Fact]
    public void ModsUpdatedAfterTheModpack_AreNoticed()
    {
        var then = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var manifest = new ModpackManifest
        {
            Name = "P",
            Mods = [new() { Id = "a", WorkshopId = 1, UpdatedUtc = then }, new() { Id = "b", WorkshopId = 2, UpdatedUtc = then }],
        };

        var updated = ModpackPlan.UpdatedSince(manifest, new Dictionary<ulong, DateTime> { [1] = then, [2] = then.AddDays(3) });

        Assert.Equal(["b"], updated.Select(m => m.Id));
    }

    [Fact]
    public void Settings_AreBackedUp_AndWaitForModsStillDownloading()
    {
        var backup = Path.Combine(_root, "backup");
        var installer = new ModSettingsInstaller(Config, backup, Path.Combine(_root, "pending.json"));
        var mod = Mod("a.b", "777");
        var existing = ModpackFile.SettingsPath(Config, mod.FolderPath, "S");
        File.WriteAllText(existing, "<mine/>");

        Assert.Equal(1, installer.Apply(mod.FolderPath, new Dictionary<string, string> { ["S"] = "<theirs/>" }));
        Assert.Equal("<theirs/>", File.ReadAllText(existing));
        Assert.Equal("<mine/>", File.ReadAllText(Path.Combine(backup, "Mod_777_S.xml")));

        installer.AddPending("Later.Mod", new Dictionary<string, string> { ["T"] = "<later/>" });
        Assert.Empty(installer.ApplyPending([mod]));
        var later = Mod("later.mod", "888");
        Assert.Equal([later.Name], installer.ApplyPending([mod, later]));
        Assert.Equal("<later/>", File.ReadAllText(ModpackFile.SettingsPath(Config, later.FolderPath, "T")));
        Assert.Empty(installer.LoadPending());
    }
}
