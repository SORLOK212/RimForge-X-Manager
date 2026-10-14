using System.Text.Json;

namespace RimForge.Core.Sorting;

/// <summary>Rules the community collected for one mod (all ids lower case).</summary>
public sealed class CommunityModRules
{
    /// <summary>Other mod id → optional comment explaining the rule.</summary>
    public Dictionary<string, string?> LoadAfter { get; } = new();
    public Dictionary<string, string?> LoadBefore { get; } = new();
    public Dictionary<string, string?> IncompatibleWith { get; } = new();
    public bool LoadTop { get; set; }
    public bool LoadBottom { get; set; }
    public string? BottomComment { get; set; }
}

/// <summary>
/// The RimSort Community Rules Database (github.com/RimSort/Community-Rules-Database).
/// It is downloaded to the player's PC at run time and never shipped with RimForge.
/// </summary>
public sealed class CommunityRules
{
    public const string SourceUrl = "https://raw.githubusercontent.com/RimSort/Community-Rules-Database/main/communityRules.json";
    public const string HomeUrl = "https://github.com/RimSort/Community-Rules-Database";

    public static CommunityRules Empty { get; } = new(new Dictionary<string, CommunityModRules>(), null);

    public CommunityRules(IReadOnlyDictionary<string, CommunityModRules> rules, DateTime? updatedUtc)
    {
        Rules = rules;
        UpdatedUtc = updatedUtc;
    }

    public IReadOnlyDictionary<string, CommunityModRules> Rules { get; }
    public DateTime? UpdatedUtc { get; }
    public int Count => Rules.Count;

    public static CommunityRules Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        DateTime? updated = root.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

        var result = new Dictionary<string, CommunityModRules>();
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Object)
            return new CommunityRules(result, updated);

        foreach (var mod in rules.EnumerateObject())
        {
            var r = new CommunityModRules();
            foreach (var kind in mod.Value.EnumerateObject())
            {
                switch (kind.Name)
                {
                    case "loadAfter": ReadTargets(kind.Value, r.LoadAfter); break;
                    case "loadBefore": ReadTargets(kind.Value, r.LoadBefore); break;
                    case "incompatibleWith": ReadTargets(kind.Value, r.IncompatibleWith); break;
                    case "loadTop": r.LoadTop = Flag(kind.Value); break;
                    case "loadBottom":
                        r.LoadBottom = Flag(kind.Value);
                        r.BottomComment = Comment(kind.Value);
                        break;
                }
            }
            result[mod.Name.ToLowerInvariant()] = r;
        }
        return new CommunityRules(result, updated);
    }

    private static void ReadTargets(JsonElement element, Dictionary<string, string?> target)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var other in element.EnumerateObject())
            target[other.Name.ToLowerInvariant()] = Comment(other.Value);
    }

    private static bool Flag(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Comments are stored either as a string or as a list of strings.</summary>
    private static string? Comment(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("comment", out var c)) return null;
        var text = c.ValueKind switch
        {
            JsonValueKind.String => c.GetString(),
            JsonValueKind.Array => string.Join(" ", c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Loads the cached copy; downloads a fresh one when it is missing, older than <paramref name="maxAge"/> or <paramref name="force"/>d.</summary>
    public static async Task<CommunityRules> LoadAsync(string cacheFile, TimeSpan maxAge, bool force = false, HttpClient? http = null)
    {
        var cached = File.Exists(cacheFile) ? new FileInfo(cacheFile) : null;
        var fresh = cached is not null && DateTime.UtcNow - cached.LastWriteTimeUtc < maxAge;
        if (!force && fresh) return ParseFile(cacheFile);

        try
        {
            using var client = http is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : null;
            var json = await (http ?? client!).GetStringAsync(SourceUrl);
            var rules = Parse(json); // validate before replacing the cache
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllTextAsync(cacheFile, json);
            return rules;
        }
        catch (Exception) when (cached is not null)
        {
            // Offline: an old copy is much better than none.
            return ParseFile(cacheFile);
        }
    }

    private static CommunityRules ParseFile(string file)
    {
        try
        {
            return Parse(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}
