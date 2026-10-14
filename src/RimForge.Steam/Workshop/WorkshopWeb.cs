using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimForge.Core.Models;

namespace RimForge.Steam.Workshop;

/// <summary>One entry of a mod's change notes.</summary>
public sealed record ChangelogEntry(DateTime DateUtc, string Text);

/// <summary>
/// The public Steam web: item details and collections through the official Web API (no key, no login),
/// change notes and a search fallback through the Workshop pages. Works without the Steam client.
/// </summary>
public sealed class WorkshopWeb(HttpClient http)
{
    private const string Api = "https://api.steampowered.com/ISteamRemoteStorage/";

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RimForge/0.1");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return http;
    }

    /// <summary>Details for any number of items (asked 100 at a time). Unknown ids come back with <see cref="WorkshopItem.Removed"/>.</summary>
    public async Task<IReadOnlyList<WorkshopItem>> GetDetailsAsync(IEnumerable<ulong> ids, CancellationToken ct = default)
    {
        var result = new List<WorkshopItem>();
        foreach (var chunk in ids.Distinct().Chunk(100))
        {
            var form = new List<KeyValuePair<string, string>> { new("itemcount", chunk.Length.ToString()) };
            form.AddRange(chunk.Select((id, i) => new KeyValuePair<string, string>($"publishedfileids[{i}]", id.ToString())));
            using var response = await http.PostAsync(Api + "GetPublishedFileDetails/v1/", new FormUrlEncodedContent(form), ct);
            response.EnsureSuccessStatusCode();
            result.AddRange(ParseDetails(await response.Content.ReadAsStringAsync(ct)));
        }
        return result;
    }

    internal static IEnumerable<WorkshopItem> ParseDetails(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("publishedfiledetails", out var list)) yield break;

        foreach (var e in list.EnumerateArray())
        {
            if (!ulong.TryParse(Str(e, "publishedfileid"), out var id)) continue;
            var ok = Int(e, "result") == 1;
            yield return new WorkshopItem
            {
                Id = id,
                Title = Str(e, "title") ?? id.ToString(),
                Description = Str(e, "description") ?? "",
                PreviewUrl = Str(e, "preview_url") is { Length: > 0 } url ? url : null,
                AuthorId = ulong.TryParse(Str(e, "creator"), out var author) ? author : 0,
                Subscribers = Int(e, "subscriptions"),
                CreatedUtc = WorkshopManifest.FromUnix(Int(e, "time_created")),
                UpdatedUtc = WorkshopManifest.FromUnix(Int(e, "time_updated")),
                SizeBytes = Int(e, "file_size"),
                Tags = e.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Select(t => Str(t, "tag")).OfType<string>().ToList()
                    : [],
                Removed = !ok || Int(e, "banned") != 0 || Int(e, "visibility") == 2,
                Favorites = Int(e, "lifetime_favorited") is > 0 and var fav ? fav : Int(e, "favorited"),
                Views = Int(e, "views"),
            };
        }
    }

    /// <summary>Items of a Workshop collection (nested collections are opened too), in the collection's order.</summary>
    public async Task<(string Title, IReadOnlyList<ulong> Items)> GetCollectionAsync(ulong collectionId, CancellationToken ct = default)
    {
        var title = (await GetDetailsAsync([collectionId], ct)).FirstOrDefault()?.Title ?? collectionId.ToString();
        var items = new List<ulong>();
        var seen = new HashSet<ulong> { collectionId };
        var queue = new Queue<ulong>([collectionId]);
        while (queue.Count > 0 && seen.Count < 50)
        {
            var form = new Dictionary<string, string> { ["collectioncount"] = "1", ["publishedfileids[0]"] = queue.Dequeue().ToString() };
            using var response = await http.PostAsync(Api + "GetCollectionDetails/v1/", new FormUrlEncodedContent(form), ct);
            response.EnsureSuccessStatusCode();
            foreach (var (child, isCollection) in ParseCollection(await response.Content.ReadAsStringAsync(ct)))
            {
                if (!seen.Add(child)) continue;
                if (isCollection) queue.Enqueue(child);
                else items.Add(child);
            }
        }
        return (title, items);
    }

    internal static IEnumerable<(ulong Id, bool IsCollection)> ParseCollection(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("collectiondetails", out var list)) yield break;
        foreach (var collection in list.EnumerateArray())
        {
            if (!collection.TryGetProperty("children", out var children)) continue;
            foreach (var c in children.EnumerateArray().OrderBy(c => Int(c, "sortorder")))
                if (ulong.TryParse(Str(c, "publishedfileid"), out var id))
                    yield return (id, Int(c, "filetype") == 2);
        }
    }

    /// <summary>Search through the Workshop web page (used when the Steam client is not available).</summary>
    public async Task<WorkshopPage> SearchAsync(WorkshopQuery query, CancellationToken ct = default)
    {
        var sort = query.Sort switch
        {
            WorkshopSort.MostSubscribed => "totaluniquesubscribers",
            WorkshopSort.Newest => "mostrecent",
            WorkshopSort.Updated => "lastupdated",
            WorkshopSort.Relevance when !string.IsNullOrWhiteSpace(query.Text) => "textsearch",
            _ => "trend",
        };
        var url = $"https://steamcommunity.com/workshop/browse/?appid={KnownMods.RimWorldAppId}&section=readytouseitems" +
                  $"&browsesort={sort}&actualsort={sort}&days=7&numperpage={WorkshopIds.PageSize}&p={query.Page}" +
                  (string.IsNullOrWhiteSpace(query.Text) ? "" : "&searchtext=" + Uri.EscapeDataString(query.Text.Trim())) +
                  (query.VersionTag is null ? "" : "&requiredtags%5B%5D=" + Uri.EscapeDataString(query.VersionTag));
        var html = await http.GetStringAsync(url, ct);
        var ids = ParseBrowseIds(html);
        var details = (await GetDetailsAsync(ids, ct)).Where(i => !i.Removed).ToDictionary(i => i.Id);
        var items = ids.Where(details.ContainsKey).Select(id => details[id]).ToList();
        return new WorkshopPage(items, -1, ids.Count >= WorkshopIds.PageSize);
    }

    /// <summary>
    /// Collections through the Workshop website (the Steam client can't sort them), with how many items each holds.
    /// The page is asked in English so its "N entries matching" can be read.
    /// </summary>
    public async Task<WorkshopPage> SearchCollectionsAsync(WorkshopQuery query, CancellationToken ct = default)
    {
        var sort = query.Sort switch
        {
            WorkshopSort.Newest => "mostrecent",
            WorkshopSort.Updated => "lastupdated",
            WorkshopSort.Relevance when !string.IsNullOrWhiteSpace(query.Text) => "textsearch",
            _ => "trend",
        };
        var url = $"https://steamcommunity.com/workshop/browse/?appid={KnownMods.RimWorldAppId}&section=collections&l=english" +
                  $"&browsesort={sort}&actualsort={sort}&days={(query.AllTime ? -1 : 7)}&numperpage={WorkshopIds.PageSize}&p={query.Page}" +
                  (string.IsNullOrWhiteSpace(query.Text) ? "" : "&searchtext=" + Uri.EscapeDataString(query.Text.Trim())) +
                  (query.VersionTag is null ? "" : "&requiredtags%5B%5D=" + Uri.EscapeDataString(query.VersionTag));
        var html = await http.GetStringAsync(url, ct);
        var ids = ParseBrowseIds(html);
        var counts = await GetChildCountsAsync(ids, ct);
        var details = (await GetDetailsAsync(ids, ct)).Where(i => !i.Removed).ToDictionary(i => i.Id);
        // Only real collections: the page also links to other items.
        var items = ids.Where(id => details.ContainsKey(id) && counts.ContainsKey(id))
            .Select(id => details[id] with { IsCollection = true, ChildCount = counts.GetValueOrDefault(id, -1) })
            .ToList();
        var total = ParseTotal(html);
        return new WorkshopPage(items, total, total > 0 ? query.Page * WorkshopIds.PageSize < total : ids.Count >= WorkshopIds.PageSize);
    }

    /// <summary>How many items each collection holds (nested collections count as one).</summary>
    public async Task<IReadOnlyDictionary<ulong, int>> GetChildCountsAsync(IReadOnlyList<ulong> ids, CancellationToken ct = default)
    {
        var counts = new Dictionary<ulong, int>();
        foreach (var chunk in ids.Chunk(50))
        {
            var form = new List<KeyValuePair<string, string>> { new("collectioncount", chunk.Length.ToString()) };
            form.AddRange(chunk.Select((id, i) => new KeyValuePair<string, string>($"publishedfileids[{i}]", id.ToString())));
            using var response = await http.PostAsync(Api + "GetCollectionDetails/v1/", new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode) continue;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("collectiondetails", out var list)) continue;
            foreach (var c in list.EnumerateArray())
                if (ulong.TryParse(Str(c, "publishedfileid"), out var id) && Int(c, "result") == 1)
                    counts[id] = c.TryGetProperty("children", out var children) ? children.GetArrayLength() : 0;
        }
        return counts;
    }

    /// <summary>"3,310 entries matching filters" → 3310 (any digit grouping); -1 if the page doesn't say.</summary>
    internal static int ParseTotal(string html)
    {
        var m = Regex.Match(html, @"([\d][\d.,\s\u00a0]*)\s+entries matching");
        return m.Success && int.TryParse(Regex.Replace(m.Groups[1].Value, @"\D", ""), out var n) ? n : -1;
    }

    /// <summary>
    /// The screenshots and videos of an item's page, in the page's order. Only the page has them (the Web API gives the
    /// main picture alone); a refusal for too many requests is retried after a pause.
    /// </summary>
    public async Task<WorkshopMedia> GetMediaAsync(ulong id, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await http.GetAsync(WorkshopIds.PageUrl(id), ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), ct);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return ParseMedia(await response.Content.ReadAsStringAsync(ct));
        }
    }

    internal static WorkshopMedia ParseMedia(string html)
    {
        // rgScreenshotURLs = { '29328545': 'https://images…/?imw=637…', … } — the strip below gives the order.
        var urls = Regex.Matches(html, @"'(\d+)'\s*:\s*'(https://[^']+)'")
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value.Split('?')[0]);
        var order = Regex.Matches(html, @"thumb_screenshot_(\d+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        if (order.Count == 0) order = urls.Keys.ToList();
        var images = order.Where(urls.ContainsKey).Select(k => urls[k]).Distinct().ToList();
        var videos = Regex.Matches(html, @"youtubevideoid\\?""\s*:\s*\\?""([A-Za-z0-9_-]{11})")
            .Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(html, @"thumb_movie_([A-Za-z0-9_-]{11})\b").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        return new WorkshopMedia(images, videos);
    }

    internal static IReadOnlyList<ulong> ParseBrowseIds(string html) =>
        Regex.Matches(html, @"sharedfiles/filedetails/\?id=(\d+)")
            .Select(m => ulong.Parse(m.Groups[1].Value))
            .Distinct()
            .ToList();

    /// <summary>
    /// The items a mod requires ("Required items" on its page; the Web API only gives them with a key). Steam answers
    /// "429 Too Many Requests" to bursts, so a refusal is retried a couple of times after a pause.
    /// </summary>
    public async Task<IReadOnlyList<ulong>> GetRequiredItemsAsync(ulong id, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await http.GetAsync(WorkshopIds.PageUrl(id), ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(4 * (attempt + 1)), ct);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return ParseRequiredItems(await response.Content.ReadAsStringAsync(ct));
        }
    }

    internal static IReadOnlyList<ulong> ParseRequiredItems(string html)
    {
        var m = Regex.Match(html, @"id=""RequiredItems"">(.*?)</div>\s*</div>", RegexOptions.Singleline);
        return m.Success
            ? Regex.Matches(m.Groups[1].Value, @"filedetails/\?id=(\d+)").Select(x => ulong.Parse(x.Groups[1].Value)).Distinct().ToList()
            : [];
    }

    /// <summary>The author's change notes, newest first.</summary>
    public async Task<IReadOnlyList<ChangelogEntry>> GetChangelogAsync(ulong id, CancellationToken ct = default) =>
        ParseChangelog(await http.GetStringAsync(WorkshopIds.ChangelogUrl(id), ct));

    internal static IReadOnlyList<ChangelogEntry> ParseChangelog(string html) =>
        Regex.Matches(html, @"<p id=""(\d{9,})""[^>]*>(.*?)</p>", RegexOptions.Singleline)
            .Select(m => new ChangelogEntry(WorkshopManifest.FromUnix(long.Parse(m.Groups[1].Value)), HtmlToText(m.Groups[2].Value)))
            .ToList();

    private static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static long Int(JsonElement e, string name) => long.TryParse(Str(e, name), out var v) ? v : 0;
}
