using System.Xml;
using System.Xml.Linq;
using RimForge.Core.Profiles;

namespace RimForge.Core.Saves;

public enum ModListSourceKind { Save, GameModList }

/// <summary>The mod list stored in a save (.rws) or in a list saved from the game's mod menu (.rml).</summary>
public sealed record ModListMeta(string? GameVersion, IReadOnlyList<ProfileMod> Mods);

/// <summary>A save or .rml file the user can turn into a modpack.</summary>
public sealed record ModListSource(string Path, string Name, ModListSourceKind Kind, DateTime Modified);

public static class ModListMetaReader
{
    /// <summary>
    /// Reads only the &lt;meta&gt; block at the top of the file, so even large saves are fast.
    /// RimWorld writes three parallel lists there: modIds, modSteamIds and modNames.
    /// </summary>
    public static ModListMeta Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore });

        string? version = null;
        List<string> ids = [], names = [];
        List<ulong> steamIds = [];

        if (!reader.ReadToFollowing("meta")) return new ModListMeta(null, []);
        using (var meta = reader.ReadSubtree())
        {
            meta.Read(); // <meta> itself
            meta.Read(); // its first child
            // Every branch consumes the whole element, so the loop never skips a sibling.
            while (!meta.EOF)
            {
                if (meta.NodeType != XmlNodeType.Element)
                {
                    meta.Read();
                    continue;
                }
                switch (meta.Name)
                {
                    case "gameVersion": version = meta.ReadElementContentAsString().Trim(); break;
                    case "modIds": ids = Items(meta); break;
                    case "modNames": names = Items(meta); break;
                    case "modSteamIds": steamIds = Items(meta).Select(s => ulong.TryParse(s, out var v) ? v : 0).ToList(); break;
                    default: meta.Skip(); break;
                }
            }
        }

        var mods = ids.Select((id, i) => new ProfileMod(
            id.ToLowerInvariant(),
            i < names.Count ? names[i] : null,
            i < steamIds.Count && steamIds[i] != 0 ? steamIds[i] : null)).ToList();
        return new ModListMeta(version, mods);
    }

    /// <summary>Reads a &lt;li&gt; list and leaves the reader right after its closing tag.</summary>
    private static List<string> Items(XmlReader reader) =>
        ((XElement)XNode.ReadFrom(reader)).Elements("li").Select(e => e.Value.Trim()).ToList();

    /// <summary>Saves and the game's saved mod lists, newest first.</summary>
    public static IReadOnlyList<ModListSource> FindSources(string? configDir)
    {
        if (configDir is null) return [];
        var result = new List<ModListSource>();
        Add(result, System.IO.Path.Combine(configDir, "Saves"), "*.rws", ModListSourceKind.Save);
        Add(result, System.IO.Path.Combine(configDir, "ModLists"), "*.rml", ModListSourceKind.GameModList);
        return result.OrderByDescending(s => s.Modified).ToList();
    }

    private static void Add(List<ModListSource> target, string dir, string pattern, ModListSourceKind kind)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern))
            target.Add(new ModListSource(f.FullName, System.IO.Path.GetFileNameWithoutExtension(f.Name), kind, f.LastWriteTime));
    }
}
