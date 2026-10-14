using RimForge.Core.Content;
using RimForge.Core.Models;

namespace RimForge.Core.Tests;

public sealed class ModContentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rimforge-content-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ModInfo Mod(string name, Dictionary<string, string> files, string? description = null)
    {
        var folder = Path.Combine(_dir, name.Replace(' ', '_'));
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Directory.CreateDirectory(folder);
        return new ModInfo
        {
            PackageId = name.ToLowerInvariant(), DeclaredPackageId = name.ToLowerInvariant(), Name = name,
            FolderPath = folder, Source = ModSource.Workshop, Description = description,
        };
    }

    private static ModProfile Analyze(ModInfo mod) => ModProfiler.Analyze(mod, "1.6", new HashSet<string>());

    [Fact]
    public void CountsWhatTheDefsAre_IncludingInheritedFields()
    {
        var mod = Mod("Armory", new()
        {
            ["Defs/Things.xml"] = """
                <Defs>
                  <ThingDef Name="RackBase" ParentName="MyWeaponStorageBase" Abstract="True"><building /></ThingDef>
                  <ThingDef ParentName="RackBase"><defName>GunRack</defName></ThingDef>
                  <ThingDef ParentName="BaseHumanMakeableGun"><defName>Rifle</defName><weaponTags><li>Gun</li></weaponTags></ThingDef>
                  <ThingDef ParentName="BaseMeleeWeapon_Sharp"><defName>Sword</defName></ThingDef>
                  <ThingDef ParentName="ArmorMachineableBase"><defName>Vest</defName><apparel /></ThingDef>
                  <ThingDef ParentName="AnimalThingBase"><defName>Wolf</defName><race><intelligence>Animal</intelligence></race></ThingDef>
                  <ThingDef ParentName="BasePawn"><defName>Elf</defName><race><intelligence>Humanlike</intelligence></race></ThingDef>
                  <AlienRace.ThingDef_AlienRace ParentName="BasePawn"><defName>Dwarf</defName></AlienRace.ThingDef_AlienRace>
                </Defs>
                """,
            ["Defs/Other.xml"] = "<Defs><XenotypeDef><defName>X</defName></XenotypeDef><FactionDef><defName>F</defName></FactionDef></Defs>",
        });

        var p = Analyze(mod);

        Assert.Equal(1, p.Buildings);
        Assert.Equal(2, p.Weapons);
        Assert.Equal(1, p.Apparel);
        Assert.Equal(1, p.Animals);
        Assert.Equal(2, p.Races);
        Assert.Equal(1, p.Xenotypes);
        Assert.Equal(1, p.Factions);
        Assert.Contains(ModCategory.Weapons, p.Categories);
        Assert.Contains(ModCategory.Races, p.Categories);
        Assert.Contains(ModCategory.Factions, p.Categories);
        Assert.True(p.SizeBytes > 0);
    }

    [Fact]
    public void TranslationOnlyMod_IsATranslation()
    {
        var mod = Mod("Some Mod rus", new() { ["Languages/Russian/Keyed/A.xml"] = "<LanguageData />" });

        Assert.Equal([ModCategory.Translation], Analyze(mod).Categories);
    }

    [Fact]
    public void CodeOnlyMod_IsInterface_OrPerformanceWhenItSaysSo()
    {
        var qol = Mod("Better Tabs", new() { ["Assemblies/BetterTabs.dll"] = "x" });
        var fast = Mod("Fast Fish", new() { ["Assemblies/Fish.dll"] = "x" }, "Improves performance and TPS.");

        Assert.Contains(ModCategory.Interface, Analyze(qol).Categories);
        var fastCats = Analyze(fast).Categories;
        Assert.Contains(ModCategory.Performance, fastCats);
        Assert.DoesNotContain(ModCategory.Interface, fastCats);
    }

    [Fact]
    public void Themes_ComeFromNameAndDescription()
    {
        var mod = Mod("Dragons of the Rim", new() { ["Defs/A.xml"] = "<Defs />" }, "Medieval fantasy with magic.");

        var cats = Analyze(mod).Categories;

        Assert.Contains(ModCategory.Fantasy, cats);
        Assert.Contains(ModCategory.Medieval, cats);
        Assert.DoesNotContain(ModCategory.SciFi, cats);
    }

    [Fact]
    public void PlayersCorrections_WinOverAutomaticCategories()
    {
        var note = new ModNote { Added = [ModCategory.Horror], Removed = [ModCategory.Weapons] };

        Assert.Equal([ModCategory.Apparel, ModCategory.Horror], ModNotesStore.Apply([ModCategory.Weapons, ModCategory.Apparel], note));
    }

    [Fact]
    public void Notes_AreKeptOnDisk_AndEmptyOnesForgotten()
    {
        var file = Path.Combine(_dir, "notes.json");
        var store = new ModNotesStore(file);
        store.Set("a", new ModNote { Favorite = true, Note = "do not use with CE" });
        store.Set("b", new ModNote());

        var again = new ModNotesStore(file);
        again.Load();

        Assert.True(again.Get("a").Favorite);
        Assert.Equal("do not use with CE", again.Get("a").Note);
        Assert.True(again.Get("b").IsEmpty);
    }

    [Fact]
    public void NewMark_StaysUntilTheModWasOnDuringAGameStart()
    {
        var tracker = new NewModTracker(Path.Combine(_dir, "new.json"));
        var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var lastPlayed = t0.AddHours(-1);

        // First start: "old" was there before the last game, "fresh" arrived after it.
        tracker.Update([("old", t0.AddDays(-30)), ("fresh", t0.AddMinutes(-10))], [], lastPlayed, t0);
        Assert.False(tracker.IsNew("old"));
        Assert.True(tracker.IsNew("fresh"));

        // A mod installed later is new too.
        tracker.Update([("old", t0.AddDays(-30)), ("fresh", t0.AddMinutes(-10)), ("later", t0.AddHours(1))], [], lastPlayed, t0.AddHours(1));
        Assert.True(tracker.IsNew("later"));

        // Turned on, but the game has not run since: still new.
        tracker.Update([("old", t0), ("fresh", t0), ("later", t0)], ["fresh"], lastPlayed, t0.AddHours(2));
        Assert.True(tracker.IsNew("fresh"));

        // The game ran with it: no longer new. "later" was never on, so it stays new.
        tracker.Update([("old", t0), ("fresh", t0), ("later", t0)], ["fresh"], t0.AddHours(3), t0.AddHours(3));
        Assert.False(tracker.IsNew("fresh"));
        Assert.True(tracker.IsNew("later"));
    }
}
