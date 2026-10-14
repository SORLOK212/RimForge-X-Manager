using System.Text.RegularExpressions;

namespace RimForge.Steam.Workshop;

/// <summary>One mod page in the Steam Workshop.</summary>
public sealed record WorkshopItem
{
    public required ulong Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public string? PreviewUrl { get; init; }
    public ulong AuthorId { get; init; }
    public string? AuthorName { get; init; }
    public long Subscribers { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public long SizeBytes { get; init; }
    public uint VotesUp { get; init; }
    public uint VotesDown { get; init; }

    /// <summary>The page is gone: deleted by the author, hidden or banned.</summary>
    public bool Removed { get; init; }

    /// <summary>A collection (a list of other items) rather than a mod.</summary>
    public bool IsCollection { get; init; }

    /// <summary>A collection's items, when counted (-1: not known).</summary>
    public int ChildCount { get; init; } = -1;

    /// <summary>How many people added it to their favourites (collections have no subscriber count).</summary>
    public long Favorites { get; init; }

    public long Views { get; init; }

    /// <summary>Game versions from the tags ("1.5", "1.6"), oldest first.</summary>
    public IReadOnlyList<string> Versions => Tags.Where(t => Regex.IsMatch(t, @"^\d+\.\d+$")).OrderBy(Version.Parse).ToList();

    public bool IsTranslation => Tags.Any(t => t.Equals("Translation", StringComparison.OrdinalIgnoreCase)) || LooksLikeTranslation(Title);
    public bool IsScenario => Tags.Any(t => t.Equals("Scenario", StringComparison.OrdinalIgnoreCase)) && !Tags.Any(t => t.Equals("Mod", StringComparison.OrdinalIgnoreCase));

    public string Url => WorkshopIds.PageUrl(Id);

    private static readonly Regex TranslationWords = new(
        @"\b(translation|перевод\w*|русификац\w*|русск\w*|traducci\w*|tradu\w*|übersetzung|tłumaczenie)\b|翻译|漢化|汉化|中文|日本語|한국어" +
        @"|[\(\[](ru|rus|de|fr|es|pl|cn|zh|jp|ko|kr|pt|br|ua|tr|it)[\)\]]|\s(ru|rus|ua|cn|chs|cht|kr|jp|de|pl|br)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeTranslation(string title) => TranslationWords.IsMatch(title);

    /// <summary>Official expansions, in release order.</summary>
    public static IReadOnlyList<string> Dlcs { get; } = ["Royalty", "Ideology", "Biotech", "Anomaly", "Odyssey"];

    /// <summary>
    /// Expansions the page says it needs ("Requires Biotech", "Biotech DLC required", "[Biotech]" in the title).
    /// A guess from the text: the Workshop has no field for it.
    /// </summary>
    public IReadOnlyList<string> GuessRequiredDlcs()
    {
        var text = Title + "\n" + Description;
        return Dlcs.Where(d =>
                Regex.IsMatch(Title, $@"[\[\(]{d}[\]\)]", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, $@"\b(requires?|required|needs?)\b[^.\n]{{0,40}}\b{d}\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, $@"\b{d}\b( DLC| expansion)?( is)? (required|needed)", RegexOptions.IgnoreCase))
            .ToList();
    }
}

/// <summary>How a Workshop search orders its results.</summary>
public enum WorkshopSort
{
    /// <summary>Popular this week.</summary>
    Trend,
    /// <summary>Most subscribers of all time.</summary>
    MostSubscribed,
    /// <summary>Newest first.</summary>
    Newest,
    /// <summary>Best match for the search words.</summary>
    Relevance,
    /// <summary>Most recently updated first (website only).</summary>
    Updated,
}

/// <summary>A search request. <paramref name="VersionTag"/> ("1.6") keeps only mods for that game version.</summary>
/// <param name="WithAuthors">Look up the authors' names (the Steam client takes a moment per page for that).</param>
/// <param name="Collections">Collections instead of mods (always through the website).</param>
/// <param name="AllTime">"Popular" over all time rather than this week.</param>
public sealed record WorkshopQuery(string? Text, WorkshopSort Sort, string? VersionTag, int Page = 1, bool WithAuthors = true,
    bool Collections = false, bool AllTime = false);

/// <summary>One page of results. <paramref name="Total"/> is the number of matches if known, otherwise -1.</summary>
/// <param name="PageSize">How many items a full page has (the website gives 30, the Steam client 50).</param>
public sealed record WorkshopPage(IReadOnlyList<WorkshopItem> Items, int Total, bool HasMore, int PageSize = WorkshopIds.PageSize);

/// <summary>What a Workshop page shows above its description: screenshots (full-size addresses) and YouTube videos.</summary>
public sealed record WorkshopMedia(IReadOnlyList<string> Images, IReadOnlyList<string> Videos)
{
    public static WorkshopMedia None { get; } = new([], []);
}

public static class WorkshopIds
{
    public const int PageSize = 30;

    public static string PageUrl(ulong id) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
    public static string ChangelogUrl(ulong id) => $"https://steamcommunity.com/sharedfiles/filedetails/changelog/{id}";

    /// <summary>Reads an id from "123", "...filedetails/?id=123", "steam://url/CommunityFilePage/123" and similar.</summary>
    public static ulong? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (ulong.TryParse(text, out var plain)) return plain;
        var m = Regex.Match(text, @"(?:[?&]id=|CommunityFilePage/|/filedetails/|/changelog/)(\d{3,})");
        return m.Success && ulong.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }
}
