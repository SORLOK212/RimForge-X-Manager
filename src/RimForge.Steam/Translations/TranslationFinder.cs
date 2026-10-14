using System.Text.RegularExpressions;
using RimForge.Steam.Assistant;
using RimForge.Steam.Workshop;

namespace RimForge.Steam.Translations;

/// <summary>A mod of the player's list that lacks a translation (or has an outdated one).</summary>
/// <param name="UpdatedUtc">When the mod was last updated (a translation older than that may miss new texts).</param>
/// <param name="Share">How much is translated now, 0…1.</param>
/// <param name="Replace">The mod is translated, but by an outdated translation mod (its package id): a fresher one replaces it.</param>
public sealed record TranslationNeed(string PackageId, string Name, ulong WorkshopId, DateTime UpdatedUtc, double Share, string? Replace = null);

/// <summary>A Workshop translation and the player's mods it translates.</summary>
/// <param name="Covers">Package ids of the needs it covers.</param>
/// <param name="Verified">Its "Required items" name the mod(s), so it surely belongs to them.</param>
public sealed record TranslationOffer(WorkshopItem Item, IReadOnlyList<string> Covers, bool Verified, bool SupportsVersion, bool Fresh)
{
    public bool IsPack => Covers.Count >= 3;
}

public sealed record TranslationSearch(IReadOnlyList<TranslationOffer> Offers, IReadOnlyList<TranslationNeed> NotFound, int FailedSearches);

/// <summary>What the finder needs to know about the player.</summary>
/// <param name="ActiveWorkshopIds">Mods on in the list: a translation may require only those (else it would pull other mods in).</param>
public sealed record TranslationContext(string Language, string GameVersion, IReadOnlySet<ulong> ActiveWorkshopIds);

/// <summary>
/// Finds Workshop translations for the player's mods: searches each mod's name (and name + "rus"), keeps pages in the
/// right language that belong to the mod (its "Required items", a link to it, or its name in the title), prefers packs that
/// translate several of the player's mods at once, then the best single translation: for the game version, fresh, popular.
/// </summary>
public static class TranslationFinder
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Most pages read per search ("Required items" are only on the page).</summary>
    public const int MaxPages = 15;

    public static async Task<TranslationSearch> FindAsync(IReadOnlyList<TranslationNeed> needs, TranslationContext context,
        IWorkshopSource source, Func<ulong, CancellationToken, Task<IReadOnlyList<ulong>>> requiredItems,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var markers = LanguageMarkers.For(context.Language);
        var needIds = needs.Select(n => n.WorkshopId).ToHashSet();

        // 1. Two searches per mod.
        // A few mods at a time, so each one finishes quickly and the progress moves.
        var done = 0;
        var failed = 0;
        using var gate = new SemaphoreSlim(6);
        var found = await Task.WhenAll(needs.Select(async need =>
        {
            await gate.WaitAsync(ct);
            try
            {
            var name = CleanName(need.Name);
            var results = new List<WorkshopItem>();
            foreach (var text in new[] { name, name + " " + markers.SearchWord })
            {
                try
                {
                    results.AddRange(await source.SearchAsync(new WorkshopQuery(text, WorkshopSort.Relevance, null, WithAuthors: false), ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                }
            }
            progress?.Report((Interlocked.Increment(ref done), needs.Count));
            return (Need: need, Results: results.DistinctBy(i => i.Id).ToList());
            }
            finally
            {
                gate.Release();
            }
        }));

        // 2. Pages in the right language that may belong to each mod.
        var candidates = new Dictionary<ulong, (WorkshopItem Item, HashSet<TranslationNeed> Maybe)>();
        foreach (var (need, results) in found)
        {
            foreach (var item in results)
            {
                if (item.Removed || item.Id == need.WorkshopId || needIds.Contains(item.Id) || context.ActiveWorkshopIds.Contains(item.Id)) continue;
                if (!markers.Matches(item)) continue;
                if (!candidates.TryGetValue(item.Id, out var c)) candidates[item.Id] = c = (item, []);
                c.Maybe.Add(need);
            }
        }

        // Reading a page is slow and Steam refuses bursts: only packs and unclear matches are checked, and after a refusal
        // no more pages are asked for in this search.
        var required = new Dictionary<ulong, IReadOnlyList<ulong>?>();
        var pagesLeft = MaxPages;
        async Task<IReadOnlyList<ulong>?> RequiredAsync(ulong id)
        {
            if (required.TryGetValue(id, out var known)) return known;
            IReadOnlyList<ulong>? list = null;
            if (pagesLeft > 0)
            {
                pagesLeft--;
                try
                {
                    list = await requiredItems(id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    pagesLeft = 0; // unknown: the page could not be read
                }
            }
            required[id] = list;
            return list;
        }

        // The translation may only need mods the player has on (beyond what it translates).
        bool Allowed(IReadOnlyList<ulong> req) => req.All(r => context.ActiveWorkshopIds.Contains(r) || needIds.Contains(r));

        var offers = new List<TranslationOffer>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 3. Packs first: a page that turned up for three or more of the player's mods.
        foreach (var (item, maybe) in candidates.Values.Where(c => c.Maybe.Count >= 3).OrderByDescending(c => c.Maybe.Count))
        {
            var req = await RequiredAsync(item.Id);
            var covers = needs.Where(n => !covered.Contains(n.PackageId)
                    && (req?.Contains(n.WorkshopId) == true || LinksTo(item, n.WorkshopId) || NamedAfter(item, n.Name, markers)))
                .ToList();
            if (covers.Count < 3 || req is not null && !Allowed(req)) continue;
            offers.Add(Offer(item, covers, verified: req?.Count > 0, context));
            foreach (var n in covers) covered.Add(n.PackageId);
        }

        // 4. The best single translation for each mod still without one.
        foreach (var need in needs.Where(n => !covered.Contains(n.PackageId)))
        {
            var ranked = candidates.Values
                .Where(c => c.Maybe.Contains(need))
                .Select(c => (c.Item, Strong: NamedAfter(c.Item, need.Name, markers) || LinksTo(c.Item, need.WorkshopId)))
                .OrderByDescending(c => Score(c.Item, need, c.Strong, context))
                .Take(4)
                .ToList();
            foreach (var (item, strong) in ranked)
            {
                // A translation named after the mod is clear enough; only the others need their page read.
                var req = strong ? null : await RequiredAsync(item.Id);
                bool ok;
                if (req is null) ok = strong; // the page didn't answer: trust only a clear name match
                else if (!Allowed(req)) ok = false;
                else ok = req.Contains(need.WorkshopId) || req.Count == 0 && strong;
                if (!ok) continue;

                // Maybe it is a pack after all: it may translate other mods of the list too.
                var covers = needs.Where(n => n == need || !covered.Contains(n.PackageId) && req?.Contains(n.WorkshopId) == true).ToList();
                var existing = offers.FindIndex(o => o.Item.Id == item.Id);
                if (existing >= 0) offers[existing] = offers[existing] with { Covers = offers[existing].Covers.Union(covers.Select(c => c.PackageId)).ToList() };
                else offers.Add(Offer(item, covers, verified: req?.Contains(need.WorkshopId) == true, context));
                foreach (var n in covers) covered.Add(n.PackageId);
                break;
            }
        }

        var notFound = needs.Where(n => !covered.Contains(n.PackageId)).ToList();
        return new TranslationSearch(offers, notFound, failed);
    }

    private static TranslationOffer Offer(WorkshopItem item, IReadOnlyList<TranslationNeed> covers, bool verified, TranslationContext context) =>
        new(item, covers.Select(c => c.PackageId).ToList(), verified, item.Versions.Contains(context.GameVersion),
            covers.All(c => item.UpdatedUtc >= c.UpdatedUtc.AddDays(-30)));

    private static double Score(WorkshopItem item, TranslationNeed need, bool strong, TranslationContext context)
    {
        var score = Math.Log10(item.Subscribers + 10);
        score += item.Versions.Count == 0 ? 0 : item.Versions.Contains(context.GameVersion) ? 3 : -3;
        if (item.UpdatedUtc >= need.UpdatedUtc.AddDays(-30)) score += 1;
        if (strong) score += 1.5;
        var votes = item.VotesUp + item.VotesDown;
        if (votes >= 20) score += (item.VotesUp / (double)votes - 0.8) * 2;
        return score;
    }

    /// <summary>"[1.6] Pick Up And Haul (Continued)" → "Pick Up And Haul": what translators keep in their titles.</summary>
    public static string CleanName(string name)
    {
        var s = Regex.Replace(name, @"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}", " ");
        s = Regex.Replace(s, @"\b(continued|updated|unofficial|fork(ed)?|redux|reupload|re-upload|v?\d+(\.\d+)+)\b", " ", Options);
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '–', '—', ':', '|');
        return s.Length >= 3 ? s : name.Trim();
    }

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    /// <summary>Words translators put around the mod's name ("Russian Language Pack for …").</summary>
    private static readonly Regex Filler = new(
        @"\b(translations?|translated|languages?|lang|pack|localization|localisation|for|of|the|mod|version|" +
        @"для|на|мода?|язык\w*|версия|перевод\w*|локализац\w*|русификат\w*|руссификат\w*)\b", Options);

    /// <summary>
    /// The title is the mod's name plus language words only ("RimHUD rus", "Русификатор Pick Up And Haul"): then it
    /// is clearly that mod's translation. "Rus Gunplay addon" is not "Gunplay"'s — it may be another mod's.
    /// </summary>
    internal static bool NamedAfter(WorkshopItem item, string modName, LanguageMarkers markers)
    {
        var name = Normalize(CleanName(modName));
        if (name.Length < 3) return false;
        var title = Normalize(Filler.Replace(markers.Strip(CleanName(item.Title)), " "));
        return title == name;
    }

    /// <summary>The description links to the mod's page.</summary>
    internal static bool LinksTo(WorkshopItem item, ulong workshopId) =>
        Regex.IsMatch(item.Description, $@"(\?id=|filedetails/|CommunityFilePage/){workshopId}\b");
}
