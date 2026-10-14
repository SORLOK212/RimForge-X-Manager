using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RimForge.Core.Diagnostics;
using RimForge.Core.Models;

namespace RimForge.Core.Content;

/// <summary>What kind of mod this is. A mod can be several at once ("weapons + apparel + faction").</summary>
public enum ModCategory
{
    Weapons, Apparel, Races, Animals, Mechanoids, Buildings, FoodPlants, Medical, Factions, Events, Vehicles,
    Interface, Performance, Graphics, Library, Patch, Translation,
    Fantasy, Medieval, SciFi, Horror,
}

/// <summary>What a mod adds, counted from its Defs, plus the categories that follows from it.</summary>
public sealed record ModProfile
{
    public int Weapons { get; init; }
    public int Apparel { get; init; }
    public int Races { get; init; }
    public int Xenotypes { get; init; }
    public int Genes { get; init; }
    public int Animals { get; init; }
    public int Mechanoids { get; init; }
    public int Buildings { get; init; }
    public int Plants { get; init; }
    public int Food { get; init; }
    public int Medical { get; init; }
    public int Research { get; init; }
    public int Factions { get; init; }
    public int Events { get; init; }
    public int Vehicles { get; init; }
    public int Defs { get; init; }
    public int Patches { get; init; }
    public int Assemblies { get; init; }
    public int Textures { get; init; }
    public bool HasLanguages { get; init; }
    public long SizeBytes { get; init; }
    public List<ModCategory> Categories { get; init; } = [];
}

/// <summary>
/// Reads a mod's files and works out what it contains: real counts from the Defs (a ThingDef with
/// &lt;apparel&gt; is clothing, one with &lt;race&gt; is a creature...), then the name and description for
/// themes (fantasy, sci-fi...) and for code-only mods (interface, performance).
/// </summary>
public static partial class ModProfiler
{
    public static ModProfile Analyze(ModInfo mod, string gameVersion, IReadOnlySet<string> installedIds)
    {
        var content = ModContent.Resolve(mod, gameVersion, installedIds);
        var c = new Counter();
        var all = content.DefFiles.Select(Load).OfType<XElement>().SelectMany(r => r.Elements()).ToList();
        // Templates (Name="…") let a def inherit fields such as <building> from its parents.
        var templates = new Dictionary<string, XElement>();
        foreach (var def in all)
            if ((string?)def.Attribute("Name") is { Length: > 0 } name) templates.TryAdd(name, def);
        foreach (var def in all)
        {
            if (string.Equals((string?)def.Attribute("Abstract"), "true", StringComparison.OrdinalIgnoreCase)) continue;
            c.Defs++;
            Count(def, Chain(def, templates), c);
        }

        var patches = content.PatchFiles.Count();
        var assemblies = content.Assemblies.Count(a => !Path.GetFileName(a).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase));
        var textures = CountFiles(content.Folders, "Textures", "*.png") + CountFiles(content.Folders, "Textures", "*.dds");
        var languages = content.Folders.Any(f => Directory.Exists(Path.Combine(f, "Languages")));
        var size = FolderSize(mod.FolderPath);

        var profile = new ModProfile
        {
            Weapons = c.Weapons, Apparel = c.Apparel, Races = c.Races, Xenotypes = c.Xenotypes, Genes = c.Genes,
            Animals = c.Animals, Mechanoids = c.Mechanoids, Buildings = c.Buildings, Plants = c.Plants, Food = c.Food,
            Medical = c.Medical, Research = c.Research, Factions = c.Factions, Events = c.Events, Vehicles = c.Vehicles,
            Defs = c.Defs, Patches = patches, Assemblies = assemblies, Textures = textures, HasLanguages = languages, SizeBytes = size,
        };
        return profile with { Categories = Categorize(mod, profile) };
    }

    private sealed class Counter
    {
        public int Weapons, Apparel, Races, Xenotypes, Genes, Animals, Mechanoids, Buildings, Plants, Food, Medical,
            Research, Factions, Events, Vehicles, Defs;
    }

    /// <summary>The def and its parents inside the same mod, nearest first.</summary>
    private static List<XElement> Chain(XElement def, Dictionary<string, XElement> templates)
    {
        var chain = new List<XElement> { def };
        var parent = (string?)def.Attribute("ParentName");
        while (parent is not null && templates.TryGetValue(parent, out var next) && !chain.Contains(next) && chain.Count < 10)
        {
            chain.Add(next);
            parent = (string?)next.Attribute("ParentName");
        }
        return chain;
    }

    private static void Count(XElement def, List<XElement> chain, Counter c)
    {
        var type = def.Name.LocalName;
        // All parent names along the chain, e.g. "sbzWeaponRackBase sbzWeaponStorageBase BuildingBase".
        var parent = string.Join(' ', chain.Select(e => (string?)e.Attribute("ParentName")).OfType<string>());
        XElement? Get(string child) => chain.Select(e => e.Element(child)).FirstOrDefault(x => x is not null);
        bool Has(string child) => Get(child) is not null;

        switch (type)
        {
            case "XenotypeDef": c.Xenotypes++; return;
            case "GeneDef": c.Genes++; return;
            case "ResearchProjectDef": c.Research++; return;
            case "FactionDef": c.Factions++; return;
            case "IncidentDef" or "QuestScriptDef" or "GameConditionDef" or "StorytellerDef": c.Events++; return;
            case "TerrainDef": c.Buildings++; return;
            case "HediffDef" when Has("addedPartProps") || parent.Contains("AddedBodyPart") || parent.Contains("Implant"): c.Medical++; return;
        }
        if (type.Contains("VehicleDef") || parent.Contains("Vehicle")) { c.Vehicles++; return; }
        if (type.Contains("AlienRace", StringComparison.Ordinal) && type.EndsWith("ThingDef_AlienRace")) { c.Races++; return; }
        if (!type.EndsWith("ThingDef", StringComparison.Ordinal)) return;

        if (Has("race"))
        {
            var race = Get("race")!;
            var humanlike = string.Equals(race.Element("intelligence")?.Value.Trim(), "Humanlike", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(race.Element("humanlike")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            var mech = parent.Contains("Mech", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(race.Element("fleshType")?.Value.Trim(), "Mechanoid", StringComparison.OrdinalIgnoreCase);
            if (humanlike) c.Races++;
            else if (mech) c.Mechanoids++;
            else c.Animals++;
            return;
        }
        // Fields first (they say what the thing is), names of parents only as a fallback.
        if (Has("apparel")) { c.Apparel++; return; }
        if (Has("building") || Has("placeWorkers") || Get("category")?.Value.Trim() == "Building") { c.Buildings++; return; }
        if (Has("plant")) { c.Plants++; return; }
        if (Has("weaponTags") || Has("weaponClasses")) { c.Weapons++; return; }
        if (BuildingParent().IsMatch(parent)) { c.Buildings++; return; }
        if (parent.Contains("Apparel", StringComparison.OrdinalIgnoreCase) || parent.Contains("Armor", StringComparison.OrdinalIgnoreCase)) { c.Apparel++; return; }
        if (parent.Contains("Plant", StringComparison.OrdinalIgnoreCase)) { c.Plants++; return; }
        if (WeaponParent().IsMatch(parent)) { c.Weapons++; return; }
        if (Get("ingestible") is { } ing)
        {
            if (ing.Element("drugCategory") is not null || parent.Contains("Drug", StringComparison.OrdinalIgnoreCase)) c.Medical++;
            else c.Food++;
            return;
        }
        if (parent.Contains("Medicine", StringComparison.OrdinalIgnoreCase) || parent.Contains("BodyPart", StringComparison.OrdinalIgnoreCase)) c.Medical++;
    }

    [GeneratedRegex("Gun|Weapon|Melee|Sword|Grenade|Bow|Spear|Turret", RegexOptions.IgnoreCase)]
    private static partial Regex WeaponParent();

    [GeneratedRegex("Building|Furniture|Bench|Storage|Shelf|Rack|Table|Chair|Bed|Wall|Door|Lamp|Light", RegexOptions.IgnoreCase)]
    private static partial Regex BuildingParent();

    internal static List<ModCategory> Categorize(ModInfo mod, ModProfile p)
    {
        var result = new List<ModCategory>();
        void Add(ModCategory cat, bool when)
        {
            if (when && !result.Contains(cat)) result.Add(cat);
        }

        // Counted content first; a couple of incidental items does not make a weapons mod.
        Add(ModCategory.Weapons, p.Weapons >= 2 || (p.Weapons >= 1 && p.Defs <= 6));
        Add(ModCategory.Apparel, p.Apparel >= 2 || (p.Apparel >= 1 && p.Defs <= 6));
        Add(ModCategory.Races, p.Races + p.Xenotypes >= 1 || p.Genes >= 3);
        Add(ModCategory.Animals, p.Animals >= 1);
        Add(ModCategory.Mechanoids, p.Mechanoids >= 1);
        Add(ModCategory.Buildings, p.Buildings >= 2 || (p.Buildings >= 1 && p.Defs <= 6));
        Add(ModCategory.FoodPlants, p.Plants + p.Food >= 2 || (p.Plants + p.Food >= 1 && p.Defs <= 6));
        Add(ModCategory.Medical, p.Medical >= 2 || (p.Medical >= 1 && p.Defs <= 6));
        Add(ModCategory.Factions, p.Factions >= 1);
        Add(ModCategory.Events, p.Events >= 2 || (p.Events >= 1 && p.Defs <= 6));
        Add(ModCategory.Vehicles, p.Vehicles >= 1);

        var text = $"{mod.Name}\n{mod.Description}";
        var name = mod.Name;
        Add(ModCategory.Translation, (p.HasLanguages && p.Defs == 0 && p.Assemblies == 0 && p.Patches == 0) || TranslationName().IsMatch(name));
        Add(ModCategory.Library, LibraryName().IsMatch(name));
        Add(ModCategory.Performance, PerformanceWords().IsMatch(text) && p.Assemblies > 0);
        Add(ModCategory.Graphics, (p.Textures > 0 && p.Defs == 0 && p.Assemblies == 0) || RetextureWords().IsMatch(name));
        Add(ModCategory.Patch, p.Patches > 0 && p.Defs == 0 && p.Assemblies == 0 && p.Textures == 0 && !result.Contains(ModCategory.Translation));
        // Code with next to nothing new in the game: it changes how things work — usually convenience.
        Add(ModCategory.Interface, (p.Assemblies > 0 && p.Defs <= 3 && !result.Contains(ModCategory.Library) && !result.Contains(ModCategory.Performance))
                                   || InterfaceWords().IsMatch(name));

        Add(ModCategory.Fantasy, FantasyWords().IsMatch(text));
        Add(ModCategory.Medieval, MedievalWords().IsMatch(text));
        Add(ModCategory.SciFi, SciFiWords().IsMatch(text));
        Add(ModCategory.Horror, HorrorWords().IsMatch(text));
        return result;
    }

    [GeneratedRegex(@"translation|перевод|русиф|\b(rus|ru|cn|chs|cht|zh|jp|kr|de|pl|ua|br)\s*$|\((ru|rus|cn|zh|jp|kr|de|pl|ua|br)\)", RegexOptions.IgnoreCase)]
    private static partial Regex TranslationName();

    [GeneratedRegex(@"framework|\blib\b|library|\bapi\b|harmony|hugslib|\bcore\b|xml extensions|prepatcher", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryName();

    [GeneratedRegex(@"performance|optimi[sz]|\bTPS\b|\bFPS\b|lag\b|faster|speed ?up|rocketman|производительн", RegexOptions.IgnoreCase)]
    private static partial Regex PerformanceWords();

    [GeneratedRegex(@"retexture|re-texture|textures?\b|graphics|ретекстур", RegexOptions.IgnoreCase)]
    private static partial Regex RetextureWords();

    [GeneratedRegex(@"\bUI\b|interface|menu|\bQoL\b|quality of life|tab\b|button|overlay|notification", RegexOptions.IgnoreCase)]
    private static partial Regex InterfaceWords();

    [GeneratedRegex(@"fantasy|magic|\bmage|wizard|dragon|\belf|elves|dwar(f|ves)|sorcer|spell|фэнтези|магия", RegexOptions.IgnoreCase)]
    private static partial Regex FantasyWords();

    [GeneratedRegex(@"medieval|knight|castle|feudal|средневек", RegexOptions.IgnoreCase)]
    private static partial Regex MedievalWords();

    [GeneratedRegex(@"sci-?fi|spaceship|space ship|\bspace\b|laser|plasma|android|cyber|starship|космос", RegexOptions.IgnoreCase)]
    private static partial Regex SciFiWords();

    [GeneratedRegex(@"horror|zombie|undead|vampire|eldritch|necro|ужас|зомби", RegexOptions.IgnoreCase)]
    private static partial Regex HorrorWords();

    private static XElement? Load(string file)
    {
        try
        {
            return XDocument.Load(file, LoadOptions.None).Root;
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int CountFiles(IEnumerable<string> folders, string sub, string pattern) =>
        folders.Select(f => Path.Combine(f, sub)).Where(Directory.Exists)
            .Sum(d => Directory.EnumerateFiles(d, pattern, SearchOption.AllDirectories).Count());

    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Cheap fingerprint of a mod folder that changes when Steam updates it: the newest write time
    /// among the folder and its direct children.
    /// </summary>
    public static long Fingerprint(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var dir = new DirectoryInfo(folder);
        var newest = dir.LastWriteTimeUtc;
        foreach (var entry in dir.EnumerateFileSystemInfos())
            if (entry.LastWriteTimeUtc > newest) newest = entry.LastWriteTimeUtc;
        var about = new FileInfo(Path.Combine(folder, "About", "About.xml"));
        if (about.Exists && about.LastWriteTimeUtc > newest) newest = about.LastWriteTimeUtc;
        return newest.Ticks;
    }
}
