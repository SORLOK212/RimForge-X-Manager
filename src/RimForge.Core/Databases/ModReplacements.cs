using System.IO.Compression;
using System.Text.Json;

namespace RimForge.Core.Databases;

/// <summary>An abandoned mod and the maintained mod that replaces it.</summary>
public sealed record ModReplacement(
    ulong OldWorkshopId, string OldName, string? OldPackageId,
    ulong NewWorkshopId, string NewName, string? NewAuthor, string? NewPackageId,
    IReadOnlyList<string> OldVersions, IReadOnlyList<string> NewVersions)
{
    public bool NewSupports(string? gameVersion) => gameVersion is null || NewVersions.Count == 0 || NewVersions.Contains(gameVersion);
}

/// <summary>
/// The "Use This Instead" database by Mlie (github.com/emipa606/UseThisInstead, MIT licence): abandoned mods and
/// their maintained replacements ("… (Continued)" and the like). Downloaded to the player's PC at run time.
/// </summary>
public sealed class ModReplacements
{
    public const string SourceUrl = "https://raw.githubusercontent.com/emipa606/UseThisInstead/main/replacements.json.gz";
    public const string HomeUrl = "https://github.com/emipa606/UseThisInstead";

    public static ModReplacements Empty { get; } = new(new Dictionary<ulong, ModReplacement>(), null);

    private readonly IReadOnlyDictionary<ulong, ModReplacement> _byOldId;

    public ModReplacements(IReadOnlyDictionary<ulong, ModReplacement> byOldId, DateTime? updatedUtc)
    {
        _byOldId = byOldId;
        UpdatedUtc = updatedUtc;
    }

    public DateTime? UpdatedUtc { get; }
    public int Count => _byOldId.Count;

    /// <summary>The replacement for a mod, by its Workshop id.</summary>
    public ModReplacement? For(ulong? workshopId) =>
        workshopId is { } id && _byOldId.TryGetValue(id, out var r) ? r : null;

    /// <summary>Reads the gzip-compressed JSON of the database.</summary>
    public static ModReplacements Parse(byte[] gzip)
    {
        using var stream = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        DateTime? updated = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
            ? when : null;

        var result = new Dictionary<ulong, ModReplacement>();
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("No rules in the Use This Instead database.");

        foreach (var r in rules.EnumerateArray())
        {
            if (!ulong.TryParse(Text(r, "oldWorkshopId"), out var oldId) || !ulong.TryParse(Text(r, "newWorkshopId"), out var newId)) continue;
            if (oldId == newId) continue;
            result[oldId] = new ModReplacement(
                oldId, Text(r, "oldName") ?? oldId.ToString(), Id(Text(r, "oldPackageId")),
                newId, Text(r, "newName") ?? newId.ToString(), Text(r, "newAuthor"), Id(Text(r, "newPackageId")),
                Versions(r, "oldVersions"), Versions(r, "newVersions"));
        }
        return new ModReplacements(result, updated);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private static string? Id(string? packageId) => packageId?.ToLowerInvariant();

    private static IReadOnlyList<string> Versions(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).ToList()
            : [];

    public static async Task<ModReplacements> LoadAsync(string cacheFile, TimeSpan maxAge, bool force = false, HttpClient? http = null)
    {
        var bytes = await CachedDownload.GetAsync(SourceUrl, cacheFile, maxAge, force, b => Parse(b), http);
        if (bytes is not { Length: > 0 }) return Empty;
        try
        {
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Empty;
        }
    }
}
