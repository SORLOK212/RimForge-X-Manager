using System.Text.Json;

namespace RimForge.Core.Content;

/// <summary>
/// Decides which mods count as "NEW": installed since RimForge first looked, and not yet played.
/// A mod stops being new once it has been in the game's active list during a game start.
/// </summary>
public sealed class NewModTracker(string file)
{
    public sealed class Entry
    {
        public DateTime FirstSeenUtc { get; set; }
        /// <summary>When RimForge first saw the mod in the game's active list.</summary>
        public DateTime? EnabledUtc { get; set; }
        public bool Played { get; set; }
    }

    private sealed class State
    {
        public bool Baseline { get; set; }
        public Dictionary<string, Entry> Mods { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private State _state = new();

    public void Load()
    {
        try
        {
            if (File.Exists(file)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(file), Json) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _state = new();
        }
    }

    /// <param name="installed">Package ids on disk now, with the time each folder appeared.</param>
    /// <param name="gameList">Package ids in the game's ModsConfig.xml now.</param>
    /// <param name="lastGameStartUtc">When the game last ran (Player.log write time), if known.</param>
    public void Update(IEnumerable<(string Id, DateTime CreatedUtc)> installed, IReadOnlyCollection<string> gameList, DateTime? lastGameStartUtc, DateTime nowUtc)
    {
        var list = installed.ToList();
        var ids = list.Select(x => x.Id).ToHashSet();
        var active = gameList.ToHashSet();
        foreach (var (id, created) in list)
        {
            if (_state.Mods.ContainsKey(id)) continue;
            // On the very first start only mods that arrived after the last game session are news.
            var played = !_state.Baseline ? lastGameStartUtc is null || created <= lastGameStartUtc : false;
            _state.Mods[id] = new Entry { FirstSeenUtc = nowUtc, Played = played };
        }
        _state.Baseline = true;

        foreach (var (id, entry) in _state.Mods)
        {
            if (entry.Played) continue;
            if (active.Contains(id)) entry.EnabledUtc ??= nowUtc;
            if (entry.EnabledUtc is { } enabled && lastGameStartUtc is { } started && started > enabled) entry.Played = true;
        }

        foreach (var gone in _state.Mods.Keys.Where(k => !ids.Contains(k)).ToList()) _state.Mods.Remove(gone);
        Save();
    }

    public bool IsNew(string packageId) => _state.Mods.TryGetValue(packageId, out var e) && !e.Played;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(_state, Json));
        }
        catch (IOException)
        {
        }
    }
}
