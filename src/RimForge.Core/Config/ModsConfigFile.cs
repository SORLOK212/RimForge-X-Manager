using System.Xml.Linq;

namespace RimForge.Core.Config;

/// <summary>The game's Config\ModsConfig.xml: which mods are active and in what order.</summary>
public sealed class ModsConfigFile
{
    public string? Version { get; set; }
    public List<string> ActiveMods { get; set; } = [];
    public List<string> KnownExpansions { get; set; } = [];

    public static ModsConfigFile Load(string path)
    {
        var root = XDocument.Load(path).Root;
        return new ModsConfigFile
        {
            Version = root?.Element("version")?.Value.Trim(),
            ActiveMods = Items(root?.Element("activeMods")),
            KnownExpansions = Items(root?.Element("knownExpansions")),
        };
    }

    /// <summary>
    /// Puts the version of the game that will read the file into it. The game throws the whole mod list away when
    /// the file's major.minor is not its own ("Mods config data is from version 1.6 while we are running 1.7.
    /// Resetting."), e.g. right after Steam switched it to another branch. Returns true if the version changed.
    /// </summary>
    public bool StampVersion(string? fullGameVersion)
    {
        if (string.IsNullOrWhiteSpace(fullGameVersion) || Version == fullGameVersion) return false;
        Version = fullGameVersion;
        return true;
    }

    /// <summary>True if the file was written by (or for) the same major.minor version of the game.</summary>
    public bool IsForGame(string? fullGameVersion) =>
        fullGameVersion is null || Version is null ||
        Paths.GamePaths.ParseMajorMinor(Version) == Paths.GamePaths.ParseMajorMinor(fullGameVersion);

    private static List<string> Items(XElement? list) =>
        list?.Elements("li").Select(e => e.Value.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList() ?? [];

    /// <summary>
    /// Writes the file safely: the current file is copied to <paramref name="backupDir"/> first,
    /// then the new content goes to a temp file which replaces the original in one step.
    /// Returns the backup path, if one was made.
    /// </summary>
    public string? Save(string path, string? backupDir, int keepBackups = 30)
    {
        string? backup = null;
        if (backupDir is not null && File.Exists(path))
        {
            Directory.CreateDirectory(backupDir);
            backup = Path.Combine(backupDir, $"ModsConfig_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.xml");
            File.Copy(path, backup, overwrite: true);
            PruneBackups(backupDir, keepBackups);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("ModsConfigData",
                new XElement("version", Version ?? ""),
                new XElement("activeMods", ActiveMods.Select(id => new XElement("li", id))),
                new XElement("knownExpansions", KnownExpansions.Select(id => new XElement("li", id)))));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".rimforge-tmp";
        doc.Save(temp);
        File.Move(temp, path, overwrite: true);
        return backup;
    }

    private static void PruneBackups(string dir, int keep)
    {
        foreach (var old in new DirectoryInfo(dir).GetFiles("ModsConfig_*.xml")
                     .OrderByDescending(f => f.Name).Skip(keep))
            old.Delete();
    }
}
