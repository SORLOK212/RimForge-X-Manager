using RimForge.Core.Models;
using RimForge.Core.Profiles;
using RimForge.Core.Saves;

namespace RimForge.Core.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rimforge-profiles-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Store_SavesLoadsAndDeletes()
    {
        var store = new ProfileStore(Path.Combine(_dir, "profiles"));
        var a = new Profile { Name = "Фэнтези", Mods = [new("ludeon.rimworld", "Core"), new("some.mod", "Some", 123)] };
        var b = new Profile { Name = "Space", CreatedUtc = a.CreatedUtc.AddMinutes(1) };
        store.Save(a);
        store.Save(b);

        var loaded = store.LoadAll();
        Assert.Equal(["Фэнтези", "Space"], loaded.Select(p => p.Name));
        Assert.Equal(["ludeon.rimworld", "some.mod"], loaded[0].ModIds);
        Assert.Equal(123UL, loaded[0].Mods[1].WorkshopId);

        store.Delete(a);
        Assert.Equal("Space", Assert.Single(store.LoadAll()).Name);
    }

    [Fact]
    public void Store_SkipsDamagedFiles()
    {
        var store = new ProfileStore(_dir);
        store.Save(new Profile { Name = "ok" });
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        Assert.Equal("ok", Assert.Single(store.LoadAll()).Name);
    }

    [Fact]
    public void SetMods_FillsNamesFromInstalledThenHints()
    {
        var installed = new Dictionary<string, ModInfo>
        {
            ["a"] = new() { PackageId = "a", DeclaredPackageId = "a", Name = "Mod A", FolderPath = "", Source = ModSource.Workshop, WorkshopId = 1 },
        };
        var hints = new Dictionary<string, ProfileMod> { ["b"] = new("b", "Mod B", 2) };
        var p = new Profile();
        p.SetMods(["a", "b", "c", "a"], installed, hints);

        Assert.Equal([new ProfileMod("a", "Mod A", 1), new ProfileMod("b", "Mod B", 2), new ProfileMod("c")], p.Mods);
    }

    [Fact]
    public void ReadsSaveMeta_WithoutReadingTheWholeFile()
    {
        var file = Path.Combine(_dir, "colony.rws");
        File.WriteAllText(file, """
            <?xml version="1.0" encoding="utf-8"?>
            <savegame>
              <meta>
                <gameVersion>1.6.4871 rev591</gameVersion>
                <modIds><li>brrainz.harmony</li><li>Ludeon.RimWorld</li><li>some.mod</li></modIds>
                <modSteamIds><li>2009463077</li><li>0</li><li>42</li></modSteamIds>
                <modNames><li>Harmony</li><li>Core</li><li>Some Mod</li></modNames>
                <PrisonLaborVersion>v1</PrisonLaborVersion>
              </meta>
              <game><broken
            """);

        var meta = ModListMetaReader.Read(file);

        Assert.Equal("1.6.4871 rev591", meta.GameVersion);
        Assert.Equal(
            [new ProfileMod("brrainz.harmony", "Harmony", 2009463077), new ProfileMod("ludeon.rimworld", "Core"), new ProfileMod("some.mod", "Some Mod", 42)],
            meta.Mods);
    }

    [Fact]
    public void ReadsMeta_WithoutWhitespaceBetweenTags()
    {
        var file = Path.Combine(_dir, "list.rml");
        File.WriteAllText(file,
            "<savedModList><meta><gameVersion>1.5</gameVersion><modIds><li>a</li><li>b</li></modIds>" +
            "<modSteamIds><li>0</li><li>7</li></modSteamIds><modNames><li>A</li><li>B</li></modNames></meta></savedModList>");

        var meta = ModListMetaReader.Read(file);

        Assert.Equal("1.5", meta.GameVersion);
        Assert.Equal([new ProfileMod("a", "A"), new ProfileMod("b", "B", 7)], meta.Mods);
    }
}
