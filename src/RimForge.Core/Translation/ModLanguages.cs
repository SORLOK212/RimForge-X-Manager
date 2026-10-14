using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;
using RimForge.Core.Diagnostics;
using RimForge.Core.Models;

namespace RimForge.Core.Translation;

/// <summary>A game language: its folder id ("Russian") and how it is shown ("Русский").</summary>
public sealed record GameLanguage(string Id, string NativeName)
{
    public override string ToString() => NativeName;
}

/// <summary>What a mod has to translate: Keyed strings and the text fields of its defs, in the form translations use.</summary>
/// <param name="Keys">"K:Key" for Keyed strings, "D:ThingDef/Gun_Revolver.label" for def fields.</param>
public sealed record ModTexts(IReadOnlySet<string> Keys)
{
    public static ModTexts Empty { get; } = new(new HashSet<string>());
    public bool HasText => Keys.Count > 0;
}

/// <summary>How much of a mod's text exists in a language, counting every mod in the list (packs translate many mods at once).</summary>
/// <param name="Share">0…1 of the mod's texts that are translated.</param>
/// <param name="Providers">Package ids of the other mods that bring the translation, most first.</param>
public sealed record TranslationCoverage(string PackageId, int Texts, int Translated, IReadOnlyList<string> Providers)
{
    public double Share => Texts == 0 ? 1 : (double)Translated / Texts;
    public bool HasText => Texts > 0;
}

/// <summary>
/// Reads which texts a mod has and which of them a language covers. A mod counts as translated when its own
/// Languages folder or another mod's (a translation mod, a pack) holds the same Keyed keys and DefInjected fields.
/// </summary>
public static class ModLanguages
{
    /// <summary>Def fields players read; the ones translations cover first.</summary>
    private static readonly HashSet<string> TextFields =
    [
        "label", "labelShort", "labelPlural", "labelNoun", "labelMale", "labelFemale", "labelMalePlural", "labelFemalePlural",
        "description", "jobString", "reportString", "verb", "gerund", "deathMessage", "letterLabel", "letterText",
        "beginLetter", "beginLetterLabel", "recoveryMessage", "pawnLabel", "pawnsPlural", "leaderTitle", "ingestCommandString",
        "ingestReportString", "useLabel", "gerundLabel", "skillLabel", "customLabel", "baseInspectLine", "inspectLine",
    ];

    public const string English = "English";

    /// <summary>"Russian (Русский)" → "Russian".</summary>
    public static string LanguageId(string folderName)
    {
        var name = folderName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? folderName[..^4] : folderName;
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        return (paren > 0 ? name[..paren] : name).Trim();
    }

    /// <summary>The language chosen in the game's options (Config/Prefs.xml), e.g. "Russian".</summary>
    public static string? GameLanguageId(string? configDir)
    {
        if (configDir is null) return null;
        var prefs = Path.Combine(configDir, "Config", "Prefs.xml");
        try
        {
            if (!File.Exists(prefs)) return null;
            var value = XDocument.Load(prefs).Root?.Element("langFolderName")?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : LanguageId(value);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The languages the game ships (Data/Core/Languages), English included.</summary>
    public static IReadOnlyList<GameLanguage> GameLanguages(string? dataDir)
    {
        var dir = dataDir is null ? null : Path.Combine(dataDir, "Core", "Languages");
        var names = dir is not null && Directory.Exists(dir)
            ? Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>()
            : KnownFolders;
        return names
            .Select(n => n.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Select(n => new GameLanguage(LanguageId(n), Native(n)))
            .DistinctBy(l => l.Id)
            .OrderBy(l => l.Id == English ? 0 : 1).ThenBy(l => l.NativeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string Native(string folderName)
    {
        var open = folderName.IndexOf(" (", StringComparison.Ordinal);
        return open > 0 && folderName.EndsWith(')') ? folderName[(open + 2)..^1] : folderName;
    }

    private static readonly string[] KnownFolders =
    [
        "English", "Russian (Русский)", "Ukrainian (Українська)", "ChineseSimplified (简体中文)", "ChineseTraditional (繁體中文)",
        "German (Deutsch)", "French (Français)", "Spanish (Español(Castellano))", "SpanishLatin (Español(Latinoamérica))",
        "PortugueseBrazilian (Português Brasileiro)", "Polish (Polski)", "Japanese (日本語)", "Korean (한국어)", "Italian (Italiano)",
        "Turkish (Türkçe)", "Czech (Čeština)",
    ];

    // ───────────────────────── What a mod has to translate ─────────────────────────

    /// <summary>The English Keyed strings and the def text fields of the folders the game loads.</summary>
    public static ModTexts SourceTexts(ModContent content)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in LanguageFolders(content, English))
            foreach (var key in KeyedKeys(folder)) keys.Add(key);

        foreach (var file in content.DefFiles)
        {
            var root = Load(file);
            if (root is null) continue;
            foreach (var def in root.Elements())
            {
                if (string.Equals((string?)def.Attribute("Abstract"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                var defName = def.Element("defName")?.Value.Trim();
                if (string.IsNullOrEmpty(defName)) continue;
                var type = TypeName(def.Name.LocalName);
                foreach (var field in def.Elements())
                    if (TextFields.Contains(field.Name.LocalName) && !field.HasElements && field.Value.Trim().Length > 0)
                        keys.Add($"D:{type}/{defName}.{field.Name.LocalName}");
            }
        }
        return keys.Count == 0 ? ModTexts.Empty : new ModTexts(keys);
    }

    /// <summary>The keys a mod provides in a language (its own texts or, for a translation mod, other mods' texts).</summary>
    public static IReadOnlySet<string> LanguageKeys(ModContent content, string language)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in LanguageFolders(content, language))
        {
            foreach (var key in KeyedKeys(folder)) keys.Add(key);
            var injected = Path.Combine(folder, "DefInjected");
            if (!Directory.Exists(injected)) continue;
            foreach (var typeDir in Directory.EnumerateDirectories(injected))
            {
                var type = TypeName(Path.GetFileName(typeDir));
                foreach (var file in Directory.EnumerateFiles(typeDir, "*.xml", SearchOption.AllDirectories))
                {
                    var root = Load(file);
                    if (root is null) continue;
                    foreach (var el in root.Elements())
                        keys.Add($"D:{type}/{el.Name.LocalName}");
                }
            }
        }
        return keys;
    }

    /// <summary>A whole language packed as "Languages/Russian (Русский).tar": treated as complete.</summary>
    public static bool HasPackedLanguage(ModContent content, string language) =>
        content.Folders.Select(f => Path.Combine(f, "Languages")).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.tar"))
            .Any(f => LanguageId(Path.GetFileName(f)).Equals(language, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> LanguageFolders(ModContent content, string language) =>
        content.Folders.Select(f => Path.Combine(f, "Languages")).Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(d => LanguageId(Path.GetFileName(d)).Equals(language, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> KeyedKeys(string languageFolder)
    {
        var keyed = Path.Combine(languageFolder, "Keyed");
        if (!Directory.Exists(keyed)) yield break;
        foreach (var file in Directory.EnumerateFiles(keyed, "*.xml", SearchOption.AllDirectories))
        {
            var root = Load(file);
            if (root is null) continue;
            foreach (var el in root.Elements()) yield return "K:" + el.Name.LocalName;
        }
    }

    /// <summary>"VFECore.ExpandableProjectileDef" → "ExpandableProjectileDef": translations use the bare type name.</summary>
    private static string TypeName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

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

    // ───────────────────────── Coverage of a list ─────────────────────────

    /// <summary>
    /// How well each mod of <paramref name="check"/> is translated into <paramref name="language"/>, by its own files and by
    /// <paramref name="providers"/> (usually the active list). Source texts are cached by folder and fingerprint.
    /// </summary>
    public static IReadOnlyList<TranslationCoverage> Coverage(IReadOnlyList<ModInfo> check, IReadOnlyList<ModInfo> providers, string language,
        string gameVersion, IReadOnlySet<string> activeIds, ModTextCache cache, CancellationToken ct = default)
    {
        // What each provider brings in the language (translation mods, packs and the mods themselves).
        var provided = new ConcurrentDictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        var packed = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var all = providers.Concat(check).DistinctBy(m => m.PackageId).Where(m => m.Source != ModSource.Official).ToList();
        Parallel.ForEach(all, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, mod =>
        {
            try
            {
                var content = ModContent.Resolve(mod, gameVersion, activeIds);
                provided[mod.PackageId] = LanguageKeys(content, language);
                packed[mod.PackageId] = HasPackedLanguage(content, language);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                provided[mod.PackageId] = new HashSet<string>();
            }
        });

        var texts = new ConcurrentDictionary<string, ModTexts>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(check.Where(m => m.Source != ModSource.Official), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, mod =>
        {
            texts[mod.PackageId] = cache.Get(mod, () =>
            {
                try
                {
                    return SourceTexts(ModContent.Resolve(mod, gameVersion, activeIds));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return ModTexts.Empty;
                }
            });
        });

        var others = providers.Where(p => provided.TryGetValue(p.PackageId, out var k) && k.Count > 0).ToList();
        var result = new List<TranslationCoverage>();
        foreach (var mod in check)
        {
            if (!texts.TryGetValue(mod.PackageId, out var source) || !source.HasText)
            {
                result.Add(new TranslationCoverage(mod.PackageId, 0, 0, []));
                continue;
            }
            if (packed.GetValueOrDefault(mod.PackageId))
            {
                result.Add(new TranslationCoverage(mod.PackageId, source.Keys.Count, source.Keys.Count, []));
                continue;
            }
            var covered = new HashSet<string>(StringComparer.Ordinal);
            if (provided.TryGetValue(mod.PackageId, out var own)) covered.UnionWith(source.Keys.Where(own.Contains));
            var helpers = new List<(string Id, int Count)>();
            foreach (var other in others)
            {
                if (other.PackageId.Equals(mod.PackageId, StringComparison.OrdinalIgnoreCase)) continue;
                var keys = provided[other.PackageId];
                var count = 0;
                foreach (var key in source.Keys)
                    if (keys.Contains(key) && covered.Add(key)) count++;
                if (count > 0) helpers.Add((other.PackageId, count));
            }
            result.Add(new TranslationCoverage(mod.PackageId, source.Keys.Count, covered.Count,
                helpers.OrderByDescending(h => h.Count).Select(h => h.Id).ToList()));
        }
        return result;
    }
}

/// <summary>Remembers each mod's source texts while its folder doesn't change (scanning defs is the slow part).</summary>
public sealed class ModTextCache
{
    private readonly ConcurrentDictionary<string, (long Print, ModTexts Texts)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ModTexts Get(ModInfo mod, Func<ModTexts> read)
    {
        var print = Content.ModProfiler.Fingerprint(mod.FolderPath);
        if (_entries.TryGetValue(mod.FolderPath, out var e) && e.Print == print) return e.Texts;
        var texts = read();
        _entries[mod.FolderPath] = (print, texts);
        return texts;
    }
}
