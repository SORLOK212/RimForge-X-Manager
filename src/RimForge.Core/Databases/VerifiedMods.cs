using System.Xml;
using System.Xml.Linq;

namespace RimForge.Core.Databases;

/// <summary>
/// The "No Version Warning" list by Mlie (github.com/emipa606/NoVersionWarning, MIT licence): mods that were checked
/// and work on a game version even though their About.xml does not list it. One list per game version.
/// </summary>
public sealed class VerifiedMods
{
    public const string HomeUrl = "https://github.com/emipa606/NoVersionWarning";

    public static VerifiedMods Empty { get; } = new(new HashSet<string>(), null);

    public VerifiedMods(IReadOnlySet<string> packageIds, string? gameVersion)
    {
        PackageIds = packageIds;
        GameVersion = gameVersion;
    }

    /// <summary>Package ids in lower case.</summary>
    public IReadOnlySet<string> PackageIds { get; }
    public string? GameVersion { get; }
    public int Count => PackageIds.Count;

    public bool Contains(string declaredPackageId) => PackageIds.Contains(declaredPackageId);

    public static string UrlFor(string gameVersion) =>
        $"https://raw.githubusercontent.com/emipa606/NoVersionWarning/main/{gameVersion}/ModIdsToFix.xml";

    /// <summary>Reads ModIdsToFix.xml: <c>&lt;ModIdsToFix&gt;&lt;li&gt;author.mod&lt;/li&gt;…</c>.</summary>
    public static VerifiedMods Parse(byte[] xml, string? gameVersion)
    {
        if (xml.Length == 0) return new VerifiedMods(new HashSet<string>(), gameVersion);
        using var stream = new MemoryStream(xml);
        var doc = XDocument.Load(stream);
        if (doc.Root is null) throw new XmlException("Empty list.");
        var ids = doc.Root.Elements("li")
            .Select(li => li.Value.Trim().ToLowerInvariant())
            .Where(id => id.Length > 0)
            .ToHashSet();
        return new VerifiedMods(ids, gameVersion);
    }

    public static async Task<VerifiedMods> LoadAsync(string cacheDir, string gameVersion, TimeSpan maxAge, bool force = false, HttpClient? http = null)
    {
        var file = Path.Combine(cacheDir, $"verified-{gameVersion}.xml");
        var bytes = await CachedDownload.GetAsync(UrlFor(gameVersion), file, maxAge, force, b => Parse(b, gameVersion), http);
        if (bytes is null) return Empty;
        try
        {
            return Parse(bytes, gameVersion);
        }
        catch (XmlException)
        {
            return Empty;
        }
    }
}
