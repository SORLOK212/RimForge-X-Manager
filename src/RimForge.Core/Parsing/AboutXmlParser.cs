using System.Xml;
using System.Xml.Linq;
using RimForge.Core.Models;

namespace RimForge.Core.Parsing;

/// <summary>Raw contents of an About/About.xml file, before version resolution.</summary>
public sealed class AboutXml
{
    public string? Name { get; set; }
    public string? PackageId { get; set; }
    public List<string> Authors { get; } = [];
    public string? Description { get; set; }
    public string? Url { get; set; }
    public string? ModVersion { get; set; }
    public List<string> SupportedVersions { get; } = [];

    public List<ModDependency> Dependencies { get; } = [];
    public Dictionary<string, List<ModDependency>> DependenciesByVersion { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> LoadBefore { get; } = [];
    public List<string> LoadAfter { get; } = [];
    public List<string> ForceLoadBefore { get; } = [];
    public List<string> ForceLoadAfter { get; } = [];
    public List<string> IncompatibleWith { get; } = [];
    public Dictionary<string, List<string>> LoadBeforeByVersion { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> LoadAfterByVersion { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> IncompatibleWithByVersion { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game uses a version-specific list instead of the plain one when it exists.</summary>
    public static IReadOnlyList<T> Resolve<T>(List<T> plain, Dictionary<string, List<T>> byVersion, string gameVersion) =>
        byVersion.TryGetValue("v" + gameVersion, out var versioned) ? versioned : plain;
}

/// <summary>
/// Tolerant About.xml reader. Mods in the wild have odd casing, stray whitespace before the
/// XML declaration and other small defects, so element names are matched case-insensitively.
/// </summary>
public static class AboutXmlParser
{
    public static AboutXml ParseFile(string path) => Parse(File.ReadAllText(path));

    public static AboutXml Parse(string xml)
    {
        var doc = XDocument.Parse(xml.TrimStart('﻿', ' ', '\t', '\r', '\n'), LoadOptions.None);
        var root = doc.Root ?? throw new XmlException("About.xml has no root element");
        var about = new AboutXml();

        foreach (var el in root.Elements())
        {
            switch (el.Name.LocalName.ToLowerInvariant())
            {
                case "name": about.Name = Text(el); break;
                case "packageid": about.PackageId = Text(el); break;
                case "author": about.Authors.AddRange(SplitAuthors(Text(el))); break;
                case "authors": about.Authors.AddRange(Items(el)); break;
                case "description": about.Description = el.Value.Trim(); break;
                case "url": about.Url = Text(el); break;
                case "modversion": about.ModVersion = Text(el); break;
                case "supportedversions": about.SupportedVersions.AddRange(Items(el)); break;
                case "moddependencies": about.Dependencies.AddRange(Dependencies(el)); break;
                case "moddependenciesbyversion":
                    foreach (var v in el.Elements()) about.DependenciesByVersion[v.Name.LocalName] = Dependencies(v).ToList();
                    break;
                case "loadbefore": about.LoadBefore.AddRange(Ids(el)); break;
                case "loadafter": about.LoadAfter.AddRange(Ids(el)); break;
                case "forceloadbefore": about.ForceLoadBefore.AddRange(Ids(el)); break;
                case "forceloadafter": about.ForceLoadAfter.AddRange(Ids(el)); break;
                case "incompatiblewith": about.IncompatibleWith.AddRange(Ids(el)); break;
                case "loadbeforebyversion": ByVersion(el, about.LoadBeforeByVersion); break;
                case "loadafterbyversion": ByVersion(el, about.LoadAfterByVersion); break;
                case "incompatiblewithbyversion": ByVersion(el, about.IncompatibleWithByVersion); break;
            }
        }
        return about;
    }

    private static string? Text(XElement el)
    {
        var s = el.Value.Trim();
        return s.Length == 0 ? null : s;
    }

    private static IEnumerable<string> Items(XElement el) =>
        el.Elements().Select(Text).OfType<string>();

    private static IEnumerable<string> Ids(XElement el) =>
        Items(el).Select(s => s.ToLowerInvariant());

    private static void ByVersion(XElement el, Dictionary<string, List<string>> target)
    {
        foreach (var v in el.Elements()) target[v.Name.LocalName] = Ids(v).ToList();
    }

    private static IEnumerable<string> SplitAuthors(string? s) =>
        s is null ? [] : s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<ModDependency> Dependencies(XElement list)
    {
        foreach (var li in list.Elements())
        {
            string? Field(string name) =>
                li.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } f ? Text(f) : null;

            var id = Field("packageId");
            if (id is null) continue;
            yield return new ModDependency(id.ToLowerInvariant(), Field("displayName"), Field("steamWorkshopUrl"), Field("downloadUrl"));
        }
    }
}
