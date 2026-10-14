using System.Text.RegularExpressions;
using RimForge.Core.Models;

namespace RimForge.Core.Diagnostics;

/// <summary>
/// Several mods ship a picture under the same path (Textures/Things/Item/Steel.png); the game shows the one of the
/// mod loaded last. Grouped by the set of mods, since one retexture can cover hundreds of pictures.
/// </summary>
/// <param name="ModIds">The mods in load order; the last one wins.</param>
/// <param name="Paths">The shared paths (relative to Textures, without extension).</param>
/// <param name="Intended">
/// The winner meant it: it is a retexture, needs or loads after the mods it overrides, or only replaces the game's own art.
/// </param>
public sealed record TextureClash(IReadOnlyList<string> ModIds, IReadOnlyList<string> Paths, bool Intended, FindingSeverity Severity)
{
    public string Winner => ModIds[^1];
}

public static class TextureConflicts
{
    /// <summary>The picture formats the game reads; the same path in two formats is the same picture.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".dds", ".psd" };

    private static readonly Regex RetextureName = new(@"(?i)re-?texture|retex|\bhd\b|hi-?res|high.?res|texture|graphics|sprites?|ретекстур|текстур",
        RegexOptions.Compiled);

    public static IReadOnlyList<TextureClash> Find(IReadOnlyList<string> loadOrder, IReadOnlyDictionary<string, ModInfo> installed, string gameVersion)
    {
        var active = loadOrder.Where(installed.ContainsKey).Distinct().ToList();
        var activeSet = active.ToHashSet();
        var position = active.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);

        // path → mods that ship it (each mod once, however many of its folders have it)
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var perMod = new (string Id, HashSet<string> Paths)[active.Count];
        Parallel.For(0, active.Count, i =>
        {
            var id = active[i];
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in ModContent.Resolve(installed[id], gameVersion, activeSet).Folders)
            {
                var textures = Path.Combine(folder, "Textures");
                if (!Directory.Exists(textures)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(textures, "*", SearchOption.AllDirectories))
                        if (Extensions.Contains(Path.GetExtension(file)))
                            paths.Add(Path.ChangeExtension(Path.GetRelativePath(textures, file), null).Replace('\\', '/'));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            perMod[i] = (id, paths);
        });
        foreach (var (id, paths) in perMod)
            foreach (var path in paths)
            {
                if (!owners.TryGetValue(path, out var list)) owners[path] = list = [];
                list.Add(id);
            }

        return owners.Where(o => o.Value.Count > 1)
            .GroupBy(o => string.Join('|', o.Value.OrderBy(id => position[id])))
            .Select(g =>
            {
                var mods = g.Key.Split('|');
                var intended = IsIntended(mods, installed);
                var paths = g.Select(o => o.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
                var severity = intended ? FindingSeverity.Info : paths.Count >= 10 ? FindingSeverity.Medium : FindingSeverity.Low;
                return new TextureClash(mods, paths, intended, severity);
            })
            .OrderByDescending(c => c.Severity).ThenByDescending(c => c.Paths.Count)
            .ToList();
    }

    /// <summary>
    /// The mod that wins says it wants the others' pictures: it requires or loads after each of them, it is named
    /// as a retexture, it comes from the same authors, or the pictures it replaces are only the game's own.
    /// </summary>
    private static bool IsIntended(IReadOnlyList<string> mods, IReadOnlyDictionary<string, ModInfo> installed)
    {
        var winner = installed[mods[^1]];
        if (mods.Take(mods.Count - 1).All(id => installed[id].IsOfficial)) return true;
        if (RetextureName.IsMatch(winner.Name)) return true;
        // The same people made them: they share their art on purpose (Vanilla Expanded, a series of xenotypes…).
        var authors = winner.Authors.Select(a => a.Trim()).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (authors.Count > 0 && mods.Take(mods.Count - 1).All(id => installed[id].IsOfficial || installed[id].Authors.Any(a => authors.Contains(a.Trim()))))
            return true;
        var after = winner.Dependencies.Select(d => d.PackageId).Concat(winner.LoadAfter).Concat(winner.ForceLoadAfter)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return mods.Take(mods.Count - 1).All(id => installed[id].IsOfficial || after.Contains(id));
    }
}
