using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RimForge.Core.Diagnostics;

/// <summary>A def declared in a mod's Defs folder.</summary>
public sealed record DefEntry(string ModId, string DefType, string DefName, string File);

/// <summary>A patch operation that replaces or removes something in an existing def.</summary>
public sealed record DestructivePatch(string ModId, string Operation, string DefName, string Field, string XPath, string File);

/// <param name="PatchedDefs">Every defName any of the mod's patch operations targets.</param>
/// <param name="Templates">Names other defs can inherit from (the Name="…" attribute, usually on abstract defs).</param>
public sealed record XmlScan(IReadOnlyList<DefEntry> Defs, IReadOnlyList<DestructivePatch> Patches, IReadOnlyList<string> Files,
    IReadOnlySet<string> PatchedDefs, IReadOnlySet<string> Templates);

public static partial class XmlScanner
{
    public static XmlScan Scan(ModContent content)
    {
        var modId = content.Mod.PackageId;
        var defs = new List<DefEntry>();
        var patches = new List<DestructivePatch>();
        var files = new List<string>();
        var patchedDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in content.DefFiles)
        {
            files.Add(file);
            var root = Load(file);
            if (root is null) continue;
            foreach (var el in root.Elements())
            {
                if ((string?)el.Attribute("Name") is { Length: > 0 } template) templates.Add(template);
                // Abstract parents only exist to be inherited from; they are not real objects.
                if (string.Equals((string?)el.Attribute("Abstract"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                var defName = el.Element("defName")?.Value.Trim();
                if (string.IsNullOrEmpty(defName)) continue;
                defs.Add(new DefEntry(modId, el.Name.LocalName, defName, file));
            }
        }

        foreach (var file in content.PatchFiles)
        {
            files.Add(file);
            var root = Load(file);
            if (root is null) continue;
            foreach (var op in root.DescendantsAndSelf().Where(e => e.Attribute("Class") is not null))
            {
                var operation = ((string)op.Attribute("Class")!).Split('.').Last();
                var xpath = op.Element("xpath")?.Value.Trim();
                if (string.IsNullOrEmpty(xpath)) continue;
                var targets = Targets(xpath).ToList();
                foreach (var (defName, _) in targets) patchedDefs.Add(defName);
                if (!IsDestructive(operation)) continue;
                foreach (var (defName, field) in targets)
                    patches.Add(new DestructivePatch(modId, operation, defName, field, xpath, file));
            }
        }

        return new XmlScan(defs, patches, files, patchedDefs, templates);
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

    /// <summary>Replace / Remove style operations overwrite each other; Add style ones stack up fine.</summary>
    private static bool IsDestructive(string operation) =>
        (operation.Contains("Replace", StringComparison.OrdinalIgnoreCase) || operation.Contains("Remove", StringComparison.OrdinalIgnoreCase))
        && !operation.Contains("Conditional", StringComparison.OrdinalIgnoreCase)
        && !operation.Contains("Test", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("""defName\s*=\s*["']([^"']+)["']""")]
    private static partial Regex DefNamePredicate();

    /// <summary>
    /// Pulls (defName, field) pairs out of an xpath such as
    /// <c>Defs/ThingDef[defName="Gun_Revolver" or defName="Gun_Pistol"]/statBases/MarketValue</c>.
    /// Xpaths without a defName (too broad to compare) give nothing.
    /// </summary>
    public static IEnumerable<(string DefName, string Field)> Targets(string xpath)
    {
        var names = DefNamePredicate().Matches(xpath);
        if (names.Count == 0) yield break;

        // The field is what follows the bracket that closes the defName predicate.
        var close = xpath.IndexOf(']', names[^1].Index);
        var field = close < 0 ? "" : xpath[(close + 1)..].Trim().Trim('/');
        foreach (Match m in names)
            yield return (m.Groups[1].Value, field);
    }
}
