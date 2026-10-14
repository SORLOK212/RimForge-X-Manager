using System.Text.Json;
using System.Text.Json.Serialization;
using RimForge.Core.Models;
using RimForge.Core.Sorting;

namespace RimForge.Core.Profiles;

/// <summary>
/// One mod in a modpack. Name and Workshop id are stored next to the package id so a modpack
/// still makes sense (and can be completed from the Workshop) on a PC that lacks some mods.
/// </summary>
public sealed record ProfileMod(string Id, string? Name = null, ulong? WorkshopId = null);

/// <summary>A modpack ("сборка"): a named, ordered list of active mods.</summary>
public sealed class Profile
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public List<ProfileMod> Mods { get; set; } = [];

    /// <summary>Order rules the player set for this modpack ("A always after B").</summary>
    public List<UserRule> Rules { get; set; } = [];

    /// <summary>Mods the player pinned to the top (after the game and DLC) or to the bottom of the list.</summary>
    public List<string> PinTop { get; set; } = [];
    public List<string> PinBottom { get; set; } = [];

    [JsonIgnore]
    public UserPins Pins => new(PinTop, PinBottom);

    /// <summary>Diagnostics findings the player marked as fine ("don't show again").</summary>
    public List<string> Ignored { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<string> ModIds => Mods.Select(m => m.Id).ToList();

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Replaces the mod list, filling names/Workshop ids from installed mods or from <paramref name="hints"/>.</summary>
    public void SetMods(IEnumerable<string> ids, IReadOnlyDictionary<string, ModInfo> installed,
        IReadOnlyDictionary<string, ProfileMod>? hints = null)
    {
        Mods = ids.Distinct().Select(id => installed.TryGetValue(id, out var m)
            ? new ProfileMod(id, m.Name, m.WorkshopId)
            : hints is not null && hints.TryGetValue(id, out var h) ? h with { Id = id } : new ProfileMod(id)).ToList();
        ModifiedUtc = DateTime.UtcNow;
    }
}

/// <summary>Stores modpacks as one JSON file each in %APPDATA%\RimForge\profiles.</summary>
public sealed class ProfileStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Directory { get; } = directory;

    private string FileOf(Profile p) => Path.Combine(Directory, p.Id + ".json");

    public List<Profile> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var result = new List<Profile>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Profile>(File.ReadAllText(file), Json) is { } p)
                {
                    p.Id = Path.GetFileNameWithoutExtension(file);
                    result.Add(p);
                }
            }
            catch (JsonException)
            {
                // A damaged file must not take the other modpacks down with it.
            }
        }
        return result.OrderBy(p => p.CreatedUtc).ToList();
    }

    public void Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = FileOf(profile) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, Json));
        File.Move(temp, FileOf(profile), overwrite: true);
    }

    public void Delete(Profile profile)
    {
        if (File.Exists(FileOf(profile))) File.Delete(FileOf(profile));
    }
}
