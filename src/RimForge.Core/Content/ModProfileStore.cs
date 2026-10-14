using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimForge.Core.Models;

namespace RimForge.Core.Content;

/// <summary>
/// Remembers each mod's <see cref="ModProfile"/> keyed by folder, so the (slow) content scan only
/// runs for mods that are new or changed since last time.
/// </summary>
public sealed class ModProfileStore(string file)
{
    private sealed record Entry(long Fingerprint, ModProfile Profile);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Load()
    {
        try
        {
            if (File.Exists(file))
                _entries = new ConcurrentDictionary<string, Entry>(
                    JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file), Json) ?? new(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _entries = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
            File.Move(temp, file, overwrite: true);
        }
        catch (IOException)
        {
            // only a cache
        }
    }

    public ModProfile? Get(ModInfo mod) =>
        _entries.TryGetValue(mod.FolderPath, out var e) ? e.Profile : null;

    /// <summary>
    /// Brings every mod's profile up to date (in parallel), calling <paramref name="progress"/> with the
    /// share done. Returns how many mods had to be scanned.
    /// </summary>
    public int Refresh(IReadOnlyCollection<ModInfo> mods, string gameVersion, Action<double>? progress = null, CancellationToken ct = default)
    {
        var installed = mods.Select(m => m.PackageId).ToHashSet();
        var todo = mods.Where(m => m.Source != ModSource.Official)
            .Select(m => (Mod: m, Print: ModProfiler.Fingerprint(m.FolderPath)))
            .Where(x => !_entries.TryGetValue(x.Mod.FolderPath, out var e) || e.Fingerprint != x.Print)
            .ToList();
        var done = 0;
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, x =>
        {
            try
            {
                _entries[x.Mod.FolderPath] = new Entry(x.Print, ModProfiler.Analyze(x.Mod, gameVersion, installed));
            }
            catch (Exception)
            {
                // a broken mod folder must not stop the others
            }
            progress?.Invoke((double)Interlocked.Increment(ref done) / todo.Count);
        });

        // Forget folders that are gone.
        var folders = mods.Select(m => m.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _entries.Keys.Where(k => !folders.Contains(k)).ToList()) _entries.TryRemove(key, out _);
        return todo.Count;
    }
}
