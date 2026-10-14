using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimForge.Core.Models;

namespace RimForge.Core.Diagnostics;

/// <summary>The folders and files of a mod that the game actually loads for the current version.</summary>
public sealed record ModContent(ModInfo Mod, IReadOnlyList<string> Folders)
{
    public IEnumerable<string> Assemblies => Files("Assemblies", "*.dll");
    public IEnumerable<string> DefFiles => Files("Defs", "*.xml");
    public IEnumerable<string> PatchFiles => Files("Patches", "*.xml");

    private IEnumerable<string> Files(string sub, string pattern) =>
        Folders.Select(f => Path.Combine(f, sub))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, pattern, SearchOption.AllDirectories));

    /// <summary>
    /// Works out the load folders the way the game does: the matching entry in loadFolders.xml if
    /// there is one (honouring IfModActive / IfModNotActive), otherwise the mod root plus "Common"
    /// and the best version folder (the current version, or the newest older one).
    /// </summary>
    public static ModContent Resolve(ModInfo mod, string gameVersion, IReadOnlySet<string> activeIds)
    {
        var root = mod.FolderPath;
        var folders = FromLoadFolders(root, gameVersion, activeIds) ?? DefaultFolders(root, gameVersion);
        return new ModContent(mod, folders.Select(f => Path.GetFullPath(Path.Combine(root, f))).Where(Directory.Exists).Distinct().ToList());
    }

    private static List<string>? FromLoadFolders(string root, string gameVersion, IReadOnlySet<string> activeIds)
    {
        var file = Path.Combine(root, "loadFolders.xml");
        if (!File.Exists(file)) return null;
        XElement? entry;
        try
        {
            entry = XDocument.Load(file).Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals("v" + gameVersion, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
        if (entry is null) return null;

        var result = new List<string>();
        foreach (var li in entry.Elements("li"))
        {
            if (!Condition(li, "IfModActive", activeIds, mustBeActive: true)) continue;
            if (!Condition(li, "IfModNotActive", activeIds, mustBeActive: false)) continue;
            var path = li.Value.Trim().Replace('\\', '/').TrimStart('/');
            result.Add(path);
        }
        return result;
    }

    /// <summary>Attributes hold one or more package ids separated by commas; any active one counts.</summary>
    private static bool Condition(XElement li, string attribute, IReadOnlySet<string> activeIds, bool mustBeActive)
    {
        var value = li.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(attribute, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var anyActive = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(id => activeIds.Contains(id.ToLowerInvariant()) || activeIds.Contains(id.ToLowerInvariant() + "_steam"));
        return anyActive == mustBeActive;
    }

    private static List<string> DefaultFolders(string root, string gameVersion)
    {
        var result = new List<string> { "", "Common" };
        if (!Directory.Exists(root)) return result;

        var current = Version.TryParse(gameVersion, out var v) ? v : new Version(1, 6);
        var best = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => n is not null && Regex.IsMatch(n, @"^\d+\.\d+$"))
            .Select(n => (Name: n!, Version: Version.Parse(n!)))
            .Where(x => x.Version <= current)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();
        if (best.Name is not null) result.Add(best.Name);
        return result;
    }
}
