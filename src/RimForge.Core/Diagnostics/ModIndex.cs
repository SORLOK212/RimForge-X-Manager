using System.Collections.Concurrent;
using RimForge.Core.Models;

namespace RimForge.Core.Diagnostics;

/// <summary>
/// What every active mod brings: its types, defs, patches and files, plus lookups from those back
/// to the mod. Built once per check and used both for conflict detection and for blaming log errors.
/// </summary>
public sealed class ModIndex
{
    private ModIndex() { }

    public required IReadOnlyList<string> LoadOrder { get; init; }
    public required IReadOnlyDictionary<string, ModInfo> Mods { get; init; }
    public required IReadOnlyList<HarmonyPatchInfo> HarmonyPatches { get; init; }
    public required IReadOnlyList<DefEntry> Defs { get; init; }
    public required IReadOnlyList<DestructivePatch> XmlPatches { get; init; }

    private Dictionary<string, HashSet<string>> _typeToMods = new();
    private Dictionary<string, HashSet<string>> _namespaceToMods = new();
    private Dictionary<string, HashSet<string>> _defNameToMods = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HashSet<string>> _fileNameToMods = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HashSet<string>> _patchedDefToMods = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HashSet<string>> _templateToMods = new(StringComparer.Ordinal);
    private List<(string ModId, string File)> _xmlFiles = [];
    private Dictionary<string, string> _folderToMod = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _position = new();

    public int AssemblyCount { get; private init; }
    public int FileCount { get; private init; }

    public int PositionOf(string modId) => _position.GetValueOrDefault(modId, -1);

    public static ModIndex Build(IReadOnlyList<string> activeIds, IReadOnlyDictionary<string, ModInfo> installed, string gameVersion,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var active = activeIds.Where(installed.ContainsKey).Distinct().ToList();
        var activeSet = active.ToHashSet();
        var scans = new ConcurrentBag<(string ModId, List<AssemblyScan> Assemblies, XmlScan Xml)>();
        var done = 0;

        Parallel.ForEach(active, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount }, id =>
        {
            var content = ModContent.Resolve(installed[id], gameVersion, activeSet);
            var assemblies = content.Assemblies.Select(dll => AssemblyScanner.Scan(dll, id)).OfType<AssemblyScan>().ToList();
            scans.Add((id, assemblies, XmlScanner.Scan(content)));
            progress?.Report((double)Interlocked.Increment(ref done) / active.Count);
        });

        var index = new ModIndex
        {
            LoadOrder = active,
            Mods = active.ToDictionary(id => id, id => installed[id]),
            HarmonyPatches = scans.SelectMany(s => s.Assemblies.SelectMany(a => a.Patches)).ToList(),
            Defs = scans.SelectMany(s => s.Xml.Defs).ToList(),
            XmlPatches = scans.SelectMany(s => s.Xml.Patches).ToList(),
            AssemblyCount = scans.Sum(s => s.Assemblies.Count),
            FileCount = scans.Sum(s => s.Xml.Files.Count),
        };

        for (var i = 0; i < active.Count; i++) index._position[active[i]] = i;
        foreach (var (id, assemblies, xml) in scans)
        {
            foreach (var type in assemblies.SelectMany(a => a.Types))
            {
                Add(index._typeToMods, type, id);
                var dot = type.LastIndexOf('.');
                if (dot > 0) Add(index._namespaceToMods, type[..dot], id);
            }
            foreach (var def in xml.Defs) Add(index._defNameToMods, def.DefName, id);
            foreach (var file in xml.Files)
            {
                Add(index._fileNameToMods, Path.GetFileName(file), id);
                index._xmlFiles.Add((id, file));
            }
            foreach (var def in xml.PatchedDefs) Add(index._patchedDefToMods, def, id);
            foreach (var template in xml.Templates) Add(index._templateToMods, template, id);

            var mod = installed[id];
            index._folderToMod.TryAdd(Path.GetFileName(mod.FolderPath), id);
            if (mod.WorkshopId is { } wid) index._folderToMod.TryAdd(wid.ToString(), id);
        }
        return index;
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string modId)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = [];
        set.Add(modId);
    }

    // ── Lookups used to blame log lines on mods ──

    /// <summary>Mods defining a type. Types in a library shipped by many mods (more than 3) are ignored.</summary>
    public IReadOnlyCollection<string> ModsOfType(string typeName)
    {
        var top = typeName.Split('+', '/')[0];
        if (_typeToMods.TryGetValue(top, out var mods) && mods.Count <= 3) return mods;
        var dot = top.LastIndexOf('.');
        if (dot > 0 && _namespaceToMods.TryGetValue(top[..dot], out var ns) && ns.Count <= 3) return ns;
        return [];
    }

    public bool TypeIsKnown(string typeName)
    {
        var top = typeName.Split('+', '/')[0];
        return _typeToMods.ContainsKey(top);
    }

    public IReadOnlyCollection<string> ModsDefining(string defName) =>
        _defNameToMods.TryGetValue(defName, out var mods) ? mods : [];

    public IReadOnlyCollection<string> ModsWithFile(string fileName) =>
        _fileNameToMods.TryGetValue(fileName, out var mods) ? mods : [];

    /// <summary>Mods that declare a template (Name="…") other defs can inherit from.</summary>
    public IReadOnlyCollection<string> ModsWithTemplate(string name) =>
        _templateToMods.TryGetValue(name, out var mods) ? mods : [];

    /// <summary>Mods whose code contains exactly this type (not just the same namespace).</summary>
    public IReadOnlyCollection<string> ModsWithExactType(string typeName) =>
        _typeToMods.TryGetValue(typeName.Split('+', '/')[0], out var mods) ? mods : [];

    /// <summary>Mods whose patches change the def with this name.</summary>
    public IReadOnlyCollection<string> ModsPatching(string defName) =>
        _patchedDefToMods.TryGetValue(defName, out var mods) ? mods : [];

    /// <summary>
    /// Which mods mention each of <paramref name="names"/> in their XML (one pass over all files).
    /// Used to find who refers to an object that does not exist.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ModsMentioning(IReadOnlyCollection<string> names)
    {
        var found = names.ToDictionary(n => n, _ => new ConcurrentDictionary<string, byte>());
        if (names.Count > 0)
        {
            Parallel.ForEach(_xmlFiles, entry =>
            {
                string text;
                try { text = File.ReadAllText(entry.File); }
                catch (IOException) { return; }
                foreach (var name in names)
                    if (text.Contains('>' + name + '<', StringComparison.Ordinal) || text.Contains('"' + name + '"', StringComparison.Ordinal))
                        found[name].TryAdd(entry.ModId, 0);
            });
        }
        return found.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Keys.OrderBy(PositionOf).ToList());
    }

    /// <summary>A mod folder name (local mods, Core/DLC) or Workshop id.</summary>
    public string? ModOfFolder(string folderOrWorkshopId) =>
        _folderToMod.TryGetValue(folderOrWorkshopId, out var id) ? id : null;
}
