namespace RimForge.Steam.Workshop;

/// <summary>A ready-made selection ("fantasy", "space", ...): several searches merged into one list.</summary>
/// <param name="Glyph">Segoe Fluent Icons glyph for the tile.</param>
/// <param name="Queries">Words searched in the Workshop (mod pages are mostly in English).</param>
public sealed record WorkshopTheme(string Id, string Glyph, IReadOnlyList<string> Queries);

public static class WorkshopThemes
{
    public static IReadOnlyList<WorkshopTheme> All { get; } =
    [
        new("fantasy", "", ["fantasy", "magic", "dragon", "elves", "dwarves", "medieval fantasy"]),
        new("medieval", "", ["medieval", "knights", "castle", "swords"]),
        new("scifi", "", ["space", "spaceship", "sci-fi", "laser", "android", "mech"]),
        new("weapons", "", ["weapons", "guns", "melee", "turret"]),
        new("apparel", "", ["apparel", "armor", "clothing", "helmet"]),
        new("races", "", ["xenotype", "race", "faction", "genes"]),
        new("animals", "", ["animals", "creatures", "wildlife"]),
        new("mechanoids", "", ["mechanoid", "mechs", "mechanitor"]),
        new("building", "", ["furniture", "architecture", "building", "decorations"]),
        new("food", "", ["food", "cooking", "crops", "plants"]),
        new("medical", "", ["implants", "prosthetics", "bionics", "medicine"]),
        new("factions", "", ["faction", "factions", "settlements"]),
        new("vehicles", "", ["vehicles", "vehicle", "tank", "car"]),
        new("story", "", ["storyteller", "events", "quests", "incidents"]),
        new("horror", "", ["horror", "zombie", "anomaly", "undead"]),
        new("hardcore", "", ["hardcore", "realistic", "survival", "combat extended"]),
        new("qol", "", ["quality of life", "QoL", "interface", "UI"]),
        new("performance", "", ["performance", "optimization", "TPS"]),
        new("graphics", "", ["retexture", "textures", "graphics"]),
        new("library", "", ["framework", "library"]),
    ];

    /// <summary>
    /// Merges the results of a theme's searches: a mod found by several searches and liked by many players
    /// comes first. Translations and scenarios are left out — they are not what a theme is about.
    /// </summary>
    public static IReadOnlyList<WorkshopItem> Merge(IEnumerable<IReadOnlyList<WorkshopItem>> resultsPerQuery, int take = 60)
    {
        var hits = new Dictionary<ulong, (WorkshopItem Item, int Hits, int BestRank)>();
        foreach (var results in resultsPerQuery)
        {
            for (var rank = 0; rank < results.Count; rank++)
            {
                var item = results[rank];
                if (item.Removed || item.IsTranslation || item.IsScenario) continue;
                hits[item.Id] = hits.TryGetValue(item.Id, out var h)
                    ? (h.Item, h.Hits + 1, Math.Min(h.BestRank, rank))
                    : (item, 1, rank);
            }
        }
        return hits.Values
            .OrderByDescending(h => Score(h.Item, h.Hits, h.BestRank))
            .Take(take)
            .Select(h => h.Item)
            .ToList();
    }

    private static double Score(WorkshopItem item, int hits, int bestRank) =>
        Math.Log10(item.Subscribers + 10) * (1 + 0.35 * (hits - 1)) - bestRank * 0.01;
}
