using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimForge.Core.Content;

/// <summary>What the player said about one mod: a star, a note, categories added or taken away by hand.</summary>
public sealed class ModNote
{
    public bool Favorite { get; set; }
    public string? Note { get; set; }
    public List<ModCategory> Added { get; set; } = [];
    public List<ModCategory> Removed { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => !Favorite && string.IsNullOrWhiteSpace(Note) && Added.Count == 0 && Removed.Count == 0;
}

/// <summary>The player's own notes about mods, keyed by package id, in %APPDATA%\RimForge\mod-notes.json.</summary>
public sealed class ModNotesStore(string file)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private Dictionary<string, ModNote> _notes = new();

    public void Load()
    {
        try
        {
            if (File.Exists(file)) _notes = JsonSerializer.Deserialize<Dictionary<string, ModNote>>(File.ReadAllText(file), Json) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _notes = new();
        }
    }

    public ModNote Get(string packageId) => _notes.TryGetValue(packageId, out var n) ? n : new ModNote();

    public void Set(string packageId, ModNote note)
    {
        if (note.IsEmpty) _notes.Remove(packageId);
        else _notes[packageId] = note;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_notes, Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Automatic categories with the player's corrections applied.</summary>
    public static IReadOnlyList<ModCategory> Apply(IEnumerable<ModCategory> automatic, ModNote note) =>
        automatic.Where(c => !note.Removed.Contains(c)).Concat(note.Added).Distinct().ToList();
}
