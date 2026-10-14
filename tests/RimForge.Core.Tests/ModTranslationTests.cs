using RimForge.Core.Models;
using RimForge.Core.Translation;
using RimForge.Steam.Assistant;
using RimForge.Steam.Translations;
using RimForge.Steam.Workshop;

namespace RimForge.Core.Tests;

public sealed class ModTranslationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-trn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ModInfo Mod(string id, params (string Path, string Xml)[] files)
    {
        var folder = Path.Combine(_root, id);
        foreach (var (path, xml) in files)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, xml);
        }
        Directory.CreateDirectory(folder);
        return new ModInfo { PackageId = id, DeclaredPackageId = id, Name = id, FolderPath = folder, Source = ModSource.Local };
    }

    private const string Defs = """
        <Defs>
          <ThingDef Abstract="True" Name="Base"><label>base</label></ThingDef>
          <ThingDef><defName>Gun_Blaster</defName><label>blaster</label><description>Pew.</description><statBases><Mass>2</Mass></statBases></ThingDef>
          <MyMod.FancyDef><defName>Fancy</defName><label>fancy</label></MyMod.FancyDef>
        </Defs>
        """;

    private const string Keyed = "<LanguageData><Mod_Hello>Hello</Mod_Hello><Mod_Bye>Bye</Mod_Bye></LanguageData>";

    [Fact]
    public void SourceTexts_AreKeyedStringsAndDefFields()
    {
        var mod = Mod("a.mod", ("Defs/Things.xml", Defs), ("Languages/English/Keyed/Main.xml", Keyed));
        var content = Diagnostics.ModContent.Resolve(mod, "1.6", new HashSet<string> { "a.mod" });

        var texts = ModLanguages.SourceTexts(content);

        Assert.Equal(new HashSet<string>
        {
            "K:Mod_Hello", "K:Mod_Bye", "D:ThingDef/Gun_Blaster.label", "D:ThingDef/Gun_Blaster.description", "D:FancyDef/Fancy.label",
        }, texts.Keys.ToHashSet());
    }

    [Fact]
    public void Coverage_CountsOwnFilesAndTranslationMods()
    {
        var mod = Mod("a.mod", ("Defs/Things.xml", Defs), ("Languages/English/Keyed/Main.xml", Keyed),
            ("Languages/Russian (Русский)/Keyed/Main.xml", "<LanguageData><Mod_Hello>Привет</Mod_Hello></LanguageData>"));
        var pack = Mod("ru.pack",
            ("Languages/Russian/DefInjected/ThingDef/Guns.xml", "<LanguageData><Gun_Blaster.label>бластер</Gun_Blaster.label><Gun_Blaster.description>Пиу.</Gun_Blaster.description></LanguageData>"));
        var empty = Mod("lib.mod", ("About/About.xml", "<ModMetaData/>"));
        var ukr = Mod("ua.pack", ("Languages/Ukrainian/Keyed/Main.xml", "<LanguageData><Mod_Bye>Бувай</Mod_Bye></LanguageData>"));
        var mods = new[] { mod, pack, empty, ukr };
        var active = mods.Select(m => m.PackageId).ToHashSet();

        var coverage = ModLanguages.Coverage(mods, mods, "Russian", "1.6", active, new ModTextCache()).ToDictionary(c => c.PackageId);

        var a = coverage["a.mod"];
        Assert.Equal(5, a.Texts);
        Assert.Equal(3, a.Translated); // Mod_Hello (own) + two blaster fields (pack); the Ukrainian one doesn't count
        Assert.Equal(["ru.pack"], a.Providers);
        Assert.False(coverage["lib.mod"].HasText);
    }

    [Fact]
    public void LanguageIds_AndGameLanguage()
    {
        Assert.Equal("Russian", ModLanguages.LanguageId("Russian (Русский)"));
        Assert.Equal("ChineseSimplified", ModLanguages.LanguageId("ChineseSimplified (简体中文).tar"));
        var config = Path.Combine(_root, "cfg");
        Directory.CreateDirectory(Path.Combine(config, "Config"));
        File.WriteAllText(Path.Combine(config, "Config", "Prefs.xml"), "<PrefsData><langFolderName>Russian (Русский)</langFolderName></PrefsData>");
        Assert.Equal("Russian", ModLanguages.GameLanguageId(config));
        Assert.Null(ModLanguages.GameLanguageId(Path.Combine(_root, "nothing")));
    }

    // ───────────────────────── Finding translations ─────────────────────────

    private static WorkshopItem Item(ulong id, string title, long subs = 1000, string description = "", string version = "1.6", params string[] tags) => new()
    {
        Id = id, Title = title, Description = description, Subscribers = subs, UpdatedUtc = DateTime.UtcNow.AddDays(-5),
        Tags = [.. tags, version],
    };

    private sealed class Source(Func<string, IReadOnlyList<WorkshopItem>> search) : IWorkshopSource
    {
        public Task<IReadOnlyList<WorkshopItem>> SearchAsync(WorkshopQuery query, CancellationToken ct) => Task.FromResult(search(query.Text!));
        public Task<IReadOnlyList<WorkshopItem>> GetDetailsAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkshopItem>>([]);
    }

    [Fact]
    public void Markers_TellRussianFromUkrainian()
    {
        var ru = LanguageMarkers.For("Russian");
        Assert.True(ru.Matches(Item(1, "RimHUD rus")));
        Assert.True(ru.Matches(Item(2, "Русификатор Pick Up And Haul")));
        Assert.True(ru.Matches(Item(3, "Door Mat [Ru]")));
        Assert.False(ru.Matches(Item(4, "RimHUD - Український переклад")));
        Assert.False(ru.Matches(Item(5, "Pick Up And Haul UA")));
        Assert.False(ru.Matches(Item(6, "Rusty Weapons"))); // "Rus" must be a word of its own
        Assert.True(LanguageMarkers.For("ChineseSimplified").Matches(Item(7, "RimHUD 简体中文汉化")));
        Assert.Equal("Pick Up And Haul", TranslationFinder.CleanName("[1.6] Pick Up And Haul (Continued)"));
    }

    [Fact]
    public async Task Finder_PicksTheBestFittingTranslation()
    {
        var results = new Dictionary<string, List<WorkshopItem>>
        {
            ["RimHUD"] =
            [
                Item(100, "RimHUD", 1_500_000),
                Item(101, "RimHUD rus", 30_000, version: "1.4"),                // old game version
                Item(102, "Русификатор RimHUD", 20_000),                        // the one to pick
                Item(103, "RimHUD - Український переклад", 500),               // another language
                Item(104, "Rus Something Else", 90_000, tags: "Translation"),   // not for this mod (page says so)
            ],
            ["Door Mat"] = [Item(200, "Door Mat"), Item(201, "Door Mat [Ru]", 1700)],
            ["Gunplay"] = [Item(300, "Gunplay"), Item(301, "Rus Gunplay addon", 50, description: "needs other things")],
        };
        var source = new Source(text => results.GetValueOrDefault(text.Replace(" rus", "")) ?? []);
        var required = new Dictionary<ulong, IReadOnlyList<ulong>> { [104] = [999], [301] = [300, 555] };
        var needs = new[]
        {
            new TranslationNeed("rimhud", "RimHUD", 100, DateTime.UtcNow.AddMonths(-1), 0),
            new TranslationNeed("doormat", "Door Mat", 200, DateTime.UtcNow.AddMonths(-1), 0),
            new TranslationNeed("gunplay", "Gunplay", 300, DateTime.UtcNow.AddMonths(-1), 0),
        };

        var search = await TranslationFinder.FindAsync(needs, new TranslationContext("Russian", "1.6", new HashSet<ulong> { 100, 200, 300 }), source,
            (id, _) => Task.FromResult(required.GetValueOrDefault(id) ?? []));

        Assert.Equal([102UL, 201], search.Offers.Select(o => o.Item.Id).Order().ToList());
        // "Rus Gunplay addon" needs a mod (555) the player doesn't have: it would pull it in.
        Assert.Equal(["gunplay"], search.NotFound.Select(n => n.PackageId));
    }

    [Fact]
    public async Task Finder_PrefersAPackThatCoversSeveralMods()
    {
        var pack = Item(900, "Big Russian Pack", 50_000, tags: "Translation");
        var source = new Source(text => text.StartsWith("Mod A") ? [pack, Item(901, "Mod A rus")]
            : text.StartsWith("Mod B") || text.StartsWith("Mod C") ? [pack] : []);
        var needs = new[] { "A", "B", "C" }.Select((n, i) => new TranslationNeed("mod." + n, "Mod " + n, (ulong)(10 + i), DateTime.UtcNow, 0)).ToList();

        var search = await TranslationFinder.FindAsync(needs, new TranslationContext("Russian", "1.6", new HashSet<ulong> { 10, 11, 12 }), source,
            (id, _) => Task.FromResult<IReadOnlyList<ulong>>(id == 900 ? [10, 11, 12] : [10]));

        var offer = Assert.Single(search.Offers);
        Assert.Equal(900UL, offer.Item.Id);
        Assert.True(offer.IsPack);
        Assert.Equal(3, offer.Covers.Count);
    }
}
