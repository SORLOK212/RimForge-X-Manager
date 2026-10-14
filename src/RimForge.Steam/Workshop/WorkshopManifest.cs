using RimForge.Core.Models;

namespace RimForge.Steam.Workshop;

/// <summary>What Steam knows about one downloaded Workshop mod.</summary>
/// <param name="UpdatedUtc">When the version on disk was published by the author.</param>
/// <param name="LatestUpdatedUtc">The newest version Steam has heard of (newer than <paramref name="UpdatedUtc"/> = waiting for an update).</param>
public sealed record InstalledWorkshopItem(ulong Id, DateTime UpdatedUtc, DateTime LatestUpdatedUtc, long SizeBytes)
{
    public bool NeedsUpdate => LatestUpdatedUtc > UpdatedUtc;
}

/// <summary>
/// Reads Steam's own record of downloaded Workshop items:
/// <c>&lt;library&gt;\steamapps\workshop\appworkshop_294100.acf</c>.
/// No network and no Steam login needed.
/// </summary>
public static class WorkshopManifest
{
    /// <summary>Finds the manifest next to a Workshop content folder (…\workshop\content\294100).</summary>
    public static string? FileFor(string? workshopContentDir)
    {
        if (workshopContentDir is null) return null;
        var workshop = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(workshopContentDir).TrimEnd('\\', '/')));
        var file = workshop is null ? null : Path.Combine(workshop, $"appworkshop_{KnownMods.RimWorldAppId}.acf");
        return file is not null && File.Exists(file) ? file : null;
    }

    public static IReadOnlyDictionary<ulong, InstalledWorkshopItem> Load(string? file)
    {
        if (file is null || !File.Exists(file)) return new Dictionary<ulong, InstalledWorkshopItem>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (IOException)
        {
            return new Dictionary<ulong, InstalledWorkshopItem>();
        }
    }

    public static IReadOnlyDictionary<ulong, InstalledWorkshopItem> Parse(string acf)
    {
        var root = VdfNode.Parse(acf)["AppWorkshop"] ?? new VdfNode();
        var details = root["WorkshopItemDetails"];
        var result = new Dictionary<ulong, InstalledWorkshopItem>();
        if (root["WorkshopItemsInstalled"] is not { } installed) return result;

        foreach (var (key, node) in installed.Children)
        {
            if (!ulong.TryParse(key, out var id)) continue;
            var updated = node.Long("timeupdated");
            var latest = details?[key]?.Long("latest_timeupdated") ?? 0;
            result[id] = new InstalledWorkshopItem(id, FromUnix(updated), FromUnix(Math.Max(updated, latest)), node.Long("size"));
        }
        return result;
    }

    public static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, seconds)).UtcDateTime;
}
