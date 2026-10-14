namespace RimForge.Core.Saves;

/// <summary>Which saves use which mods — so RimForge can warn before turning off a mod a colony depends on.</summary>
public static class SaveUsage
{
    /// <summary>Mod id → names of the saves that were made with it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Find(string? configDir)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var save in ModListMetaReader.FindSources(configDir).Where(s => s.Kind == ModListSourceKind.Save))
        {
            ModListMeta meta;
            try
            {
                meta = ModListMetaReader.Read(save.Path);
            }
            catch (Exception)
            {
                continue; // a damaged save must not stop the others
            }
            foreach (var mod in meta.Mods)
            {
                if (!result.TryGetValue(mod.Id, out var saves)) result[mod.Id] = saves = [];
                saves.Add(save.Name);
            }
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }
}
