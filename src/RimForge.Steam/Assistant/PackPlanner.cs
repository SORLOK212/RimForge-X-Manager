using System.Text.RegularExpressions;
using RimForge.Steam.Workshop;

namespace RimForge.Steam.Assistant;

/// <summary>Where the planner finds mods: the Steam client or the Workshop website.</summary>
public interface IWorkshopSource
{
    Task<IReadOnlyList<WorkshopItem>> SearchAsync(WorkshopQuery query, CancellationToken ct);
    Task<IReadOnlyList<WorkshopItem>> GetDetailsAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct);
}

/// <summary>What the planner needs to know about the player.</summary>
/// <param name="OwnedDlcs">Expansions the player has ("Biotech"…, as in <see cref="WorkshopItem.Dlcs"/>).</param>
/// <param name="Installed">Workshop ids already on disk.</param>
public sealed record PackContext(string GameVersion, IReadOnlyCollection<string> OwnedDlcs, IReadOnlySet<ulong> Installed)
{
    /// <summary>The maintained mod to use instead of an abandoned one (from "Use This Instead").</summary>
    public Func<ulong, ulong?>? Replacement { get; init; }
}

/// <summary>One mod of the suggested pack.</summary>
/// <param name="Group">The theme id it was chosen for, or "word:&lt;search&gt;" for a searched name.</param>
/// <param name="IsCore">From the hand-checked list of the theme.</param>
/// <param name="IsPerformance">Added for a fast game.</param>
/// <param name="ReplacesTitle">An abandoned mod this one stands in for.</param>
public sealed record PackPick(WorkshopItem Item, string Group, bool IsCore, bool IsPerformance, bool IsInstalled, string? ReplacesTitle = null);

/// <summary>Things worth telling the player about how the pack was made.</summary>
public sealed record PackNotes
{
    /// <summary>Heavy mods left out because a fast game was asked for.</summary>
    public IReadOnlyList<string> HeavySkipped { get; init; } = [];
    /// <summary>Mods left out because they need an expansion the player doesn't have.</summary>
    public int DlcSkipped { get; init; }
    /// <summary>Groups ("fantasy", "word:warhammer") that found nothing fitting.</summary>
    public IReadOnlyList<string> EmptyGroups { get; init; } = [];
    /// <summary>Searches that failed (no connection).</summary>
    public int FailedSearches { get; init; }
    public int TotalSearches { get; init; }
}

public sealed record PackPlan(PackRequest Request, IReadOnlyList<PackPick> Picks, IReadOnlyList<PackPick> Alternatives, PackNotes Notes)
{
    /// <summary>Download size of the mods that are not installed yet.</summary>
    public long DownloadBytes => Picks.Where(p => !p.IsInstalled).Sum(p => p.Item.SizeBytes);
}

/// <summary>
/// Builds a modpack from a request: the hand-checked mods of each theme, then the best Workshop matches.
/// Only mods for the player's game version, without translations, patches for other mods, expansions the
/// player lacks or (for a fast game) known heavy mods. Themes get an equal share, so no theme crowds out the rest.
/// </summary>
public static class PackPlanner
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    public const string WordPrefix = "word:";

    /// <summary>Add-ons, patches and fixes for other mods: they make sense only next to the mod they are for.</summary>
    private static readonly Regex Addon = new(
        @"\b(patch(es)?|compat\w*|add-?ons?|translat\w*|fix(es|ed)?|hotfix|sub-?mods?|integration|bridge|retextures?|retex|reskin|" +
        @"for (ce|combat extended|vanilla expanded)|ce patch|\[ce\]|unofficial|obsolete|deprecated|discontinued|abandoned|outdated|upscal\w*|soundtrack|music|songs?|ost)\b", Options);

    private static readonly Regex CeMention = new(@"combat extended|\bCE\b", Options);
    /// <summary>Frameworks come by themselves as dependencies of the mods that need them.</summary>
    private static readonly Regex Library = new(@"\b(framework|library|libs?|harmony|hugslib|humanoid alien races|xml extensions|prepatcher)\b", Options);

    /// <summary>Mods built on a heavy mod (they bring it along as a dependency).</summary>
    private static readonly Regex HeavyMention = new(@"medieval overhaul|rim ?world of magic|combat extended|save our ship|zombieland|facial animation", Options);

    private static readonly Regex Unstable =new(@"\b(beta|wip|work in progress|experimental|unstable|testing|test build|alpha version|dev build)\b", Options);
    private static readonly Regex Cjk = new(@"[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uac00-\ud7af]");

    private sealed class Group(string id, IReadOnlyList<string> queries, Regex keywords, IReadOnlyList<ulong> core)
    {
        public string Id { get; } = id;
        public IReadOnlyList<string> Queries { get; } = queries;
        public Regex Keywords { get; } = keywords;
        public IReadOnlyList<ulong> Core { get; } = core;
    }

    private sealed class Candidate(WorkshopItem item)
    {
        public WorkshopItem Item { get; set; } = item;
        public HashSet<string> Groups { get; } = [];
        public int Hits { get; set; }
        public int BestRank { get; set; } = int.MaxValue;
        public string? CoreOf { get; set; }
        public int CoreIndex { get; set; }
        public string? ReplacesTitle { get; set; }
        public double Score { get; set; }
    }

    public static async Task<PackPlan> BuildAsync(PackRequest request, PackContext context, IWorkshopSource source,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var groups = request.Topics
            .Where(t => t != PackTopics.Performance)
            .Select(PackTopics.Get).OfType<PackTopic>()
            .Select(t => new Group(t.Id, t.Queries, t.Keywords, t.Core))
            .Concat(request.Words.Select(w => new Group(WordPrefix + w, [w], WordKeywords(w), [])))
            .ToList();
        var performance = PackTopics.Get(PackTopics.Performance)!;

        // 1. The hand-checked mods.
        var coreIds = groups.SelectMany(g => g.Core).Concat(request.Fps ? performance.Core : []).Distinct().ToList();
        var details = coreIds.Count > 0
            ? (await source.GetDetailsAsync(coreIds, ct)).ToDictionary(i => i.Id)
            : new Dictionary<ulong, WorkshopItem>();

        // 2. The Workshop searches, all at once (the source decides how many really run together).
        var searches = groups.SelectMany(g => g.Queries.Select(q => (Group: g, Query: q))).ToList();
        var failed = 0;
        var done = 0;
        var found = await Task.WhenAll(searches.Select(async s =>
        {
            try
            {
                var items = await source.SearchAsync(new WorkshopQuery(s.Query, WorkshopSort.Relevance, context.GameVersion), ct);
                progress?.Report(s.Query);
                return (s.Group, Items: items);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                return (s.Group, Items: (IReadOnlyList<WorkshopItem>)[]);
            }
            finally
            {
                Interlocked.Increment(ref done);
            }
        }));

        // 3. One pool of candidates: a mod found by several searches counts more.
        var pool = new Dictionary<ulong, Candidate>();
        foreach (var group in groups)
        {
            for (var i = 0; i < group.Core.Count; i++)
            {
                if (!details.TryGetValue(group.Core[i], out var item)) continue;
                var c = Get(pool, item);
                c.Groups.Add(group.Id);
                if (c.CoreOf is null)
                {
                    c.CoreOf = group.Id;
                    c.CoreIndex = i;
                }
            }
        }
        foreach (var (group, items) in found)
        {
            for (var rank = 0; rank < items.Count; rank++)
            {
                var c = Get(pool, items[rank]);
                c.Groups.Add(group.Id);
                c.Hits++;
                c.BestRank = Math.Min(c.BestRank, rank);
            }
        }

        // 4. Abandoned mods give way to their maintained replacements.
        if (context.Replacement is { } replacementOf)
        {
            var swaps = pool.Values.Select(c => (Candidate: c, New: replacementOf(c.Item.Id))).Where(s => s.New is not null).ToList();
            if (swaps.Count > 0)
            {
                var fresh = (await source.GetDetailsAsync(swaps.Select(s => s.New!.Value).Distinct().ToList(), ct)).ToDictionary(i => i.Id);
                foreach (var (c, newId) in swaps)
                {
                    pool.Remove(c.Item.Id);
                    if (!fresh.TryGetValue(newId!.Value, out var item) || pool.ContainsKey(item.Id)) continue;
                    c.ReplacesTitle = c.Item.Title;
                    c.Item = item;
                    pool[item.Id] = c;
                }
            }
        }

        // The Steam client's search leaves out sizes: the web API knows them (needed for "heavy" and the download size).
        var unsized = pool.Values.Where(c => c.Item.SizeBytes == 0).Select(c => c.Item.Id).ToList();
        if (unsized.Count > 0)
        {
            try
            {
                foreach (var item in await source.GetDetailsAsync(unsized, ct))
                    if (pool.TryGetValue(item.Id, out var c) && item.SizeBytes > 0)
                        c.Item = c.Item with { SizeBytes = item.SizeBytes };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // sizes stay unknown
            }
        }

        // 5. What fits.
        var heavySkipped = new List<string>();
        var dlcSkipped = 0;
        var fitting = new List<Candidate>();
        foreach (var c in pool.Values)
        {
            var verdict = Check(c, request, context, groups);
            if (verdict == Verdict.Ok) fitting.Add(c);
            else if (verdict == Verdict.Heavy && (c.CoreOf is not null || c.Item.Subscribers > 50_000)) heavySkipped.Add(c.Item.Title);
            else if (verdict == Verdict.Dlc) dlcSkipped++;
        }
        foreach (var c in fitting) c.Score = Score(c, request, context, groups);

        // 6. Each theme in turn takes its best mod until the pack is full, so every theme is there.
        var target = request.Target;
        var picks = new List<(Candidate C, string Group)>();
        var taken = new HashSet<ulong>();
        var titles = pool.Values
            .GroupBy(c => c.Item.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.MaxBy(c => c.Item.Subscribers)!.Item.Id, StringComparer.OrdinalIgnoreCase);
        var queues = groups.ToDictionary(g => g.Id, g => new Queue<Candidate>(fitting.Where(c => c.Groups.Contains(g.Id)).OrderByDescending(c => c.Score)));
        var progressMade = true;
        while (picks.Count < target && progressMade)
        {
            progressMade = false;
            foreach (var group in groups)
            {
                if (picks.Count >= target) break;
                var queue = queues[group.Id];
                while (queue.Count > 0)
                {
                    var c = queue.Dequeue();
                    if (taken.Contains(c.Item.Id) || !ParentTaken(c.Item.Title, titles, taken)) continue;
                    picks.Add((c, group.Id));
                    taken.Add(c.Item.Id);
                    progressMade = true;
                    break;
                }
            }
        }

        var result = picks.Select(p => Pick(p.C, p.Group, context, performance: false)).ToList();

        // 7. A fast game: the performance mods come on top.
        if (request.Fps)
        {
            foreach (var id in performance.Core)
            {
                if (taken.Contains(id) || !details.TryGetValue(id, out var item)) continue;
                var c = new Candidate(item) { CoreOf = PackTopics.Performance };
                if (Check(c, request, context, groups) != Verdict.Ok) continue;
                result.Add(Pick(c, PackTopics.Performance, context, performance: true));
                taken.Add(id);
            }
        }

        var alternatives = fitting
            .Where(c => !taken.Contains(c.Item.Id) && ParentTaken(c.Item.Title, titles, taken))
            .OrderByDescending(c => c.Score)
            .Take(24)
            .Select(c => Pick(c, groups.FirstOrDefault(g => c.Groups.Contains(g.Id))?.Id ?? "", context, performance: false))
            .ToList();

        var notes = new PackNotes
        {
            HeavySkipped = heavySkipped,
            DlcSkipped = dlcSkipped,
            EmptyGroups = groups.Where(g => result.All(p => p.Group != g.Id)).Select(g => g.Id).ToList(),
            FailedSearches = failed,
            TotalSearches = searches.Count,
        };
        return new PackPlan(request, result, alternatives, notes);
    }

    private static Candidate Get(Dictionary<ulong, Candidate> pool, WorkshopItem item)
    {
        if (!pool.TryGetValue(item.Id, out var c)) pool[item.Id] = c = new Candidate(item);
        return c;
    }

    private static PackPick Pick(Candidate c, string group, PackContext context, bool performance) =>
        new(c.Item, group, c.CoreOf is not null, performance, context.Installed.Contains(c.Item.Id), c.ReplacesTitle);

    private enum Verdict { Ok, No, Heavy, Dlc }

    private static Verdict Check(Candidate c, PackRequest request, PackContext context, List<Group> groups)
    {
        var item = c.Item;
        var core = c.CoreOf is not null;
        if (item.Removed || item.IsTranslation || item.IsScenario) return Verdict.No;
        if (item.Versions.Count > 0 ? !item.Versions.Contains(context.GameVersion) : !core) return Verdict.No;
        if (Cjk.IsMatch(item.Title)) return Verdict.No;

        // What the player said no to.
        var label = item.Title + " " + string.Join(' ', item.Tags);
        if (request.ExcludeWords.Any(w => Regex.IsMatch(label, @"\b" + Regex.Escape(w), Options))) return Verdict.No;
        if (request.ExcludeTopics.Select(PackTopics.Get).OfType<PackTopic>().Any(t => t.Keywords.IsMatch(label) || t.Core.Contains(item.Id)))
            return Verdict.No;
        if (!request.Topics.Contains(PackTopics.CombatExtended) && CeMention.IsMatch(item.Title)) return Verdict.No;

        var needs = item.GuessRequiredDlcs().Concat(PackTopics.CoreDlcs.GetValueOrDefault(item.Id) ?? []).ToList();
        if (needs.Count > 0 && (request.NoDlc || needs.Any(d => !context.OwnedDlcs.Contains(d)))) return Verdict.Dlc;
        if (request.Fps && PackTopics.IsHeavy(item)) return Verdict.Heavy;
        if (core) return Verdict.Ok;

        // Found by a search: it must really be about the theme, standalone and alive.
        var graphics = c.Groups.Contains("graphics");
        if (Addon.IsMatch(graphics ? Regex.Replace(item.Title, @"re(texture|tex|skin)", "", Options) : item.Title)) return Verdict.No;
        if (Unstable.IsMatch(item.Title) || Library.IsMatch(item.Title)) return Verdict.No;
        if (request.Fps && HeavyMention.IsMatch(item.Title + " " + (item.Description.Length > 400 ? item.Description[..400] : item.Description)))
            return Verdict.Heavy;
        var minSubscribers = request.Stable ? 5000 : 1000;
        if (item.Subscribers < minSubscribers) return Verdict.No;
        if (request.Stable && item.UpdatedUtc < DateTime.UtcNow.AddYears(-2)) return Verdict.No;
        var text = item.Title + " " + string.Join(' ', item.Tags) + " " + (item.Description.Length > 1500 ? item.Description[..1500] : item.Description);
        return groups.Any(g => c.Groups.Contains(g.Id) && g.Keywords.IsMatch(text)) ? Verdict.Ok : Verdict.No;
    }

    private static double Score(Candidate c, PackRequest request, PackContext context, List<Group> groups)
    {
        if (c.CoreOf is not null) return 1000 - c.CoreIndex;
        var item = c.Item;
        var score = Math.Log10(item.Subscribers + 10) * (1 + 0.3 * (c.Hits - 1)) - Math.Min(c.BestRank, 30) * 0.02;
        if (item.UpdatedUtc > DateTime.UtcNow.AddYears(-1)) score += 0.3;
        if (context.Installed.Contains(item.Id)) score += 0.2;
        var votes = item.VotesUp + item.VotesDown;
        if (votes >= 50) score += (item.VotesUp / (double)votes - 0.8) * 2;
        if (request.Fps) score -= 0.4 * Math.Log10(item.SizeBytes / (1024.0 * 1024) + 1);
        // Fantasy + weapons: medieval weapons beat lasers.
        if (groups.Count > 1)
        {
            var text = item.Title + " " + string.Join(' ', item.Tags) + " " + (item.Description.Length > 600 ? item.Description[..600] : item.Description);
            score += 0.4 * groups.Count(g => !c.Groups.Contains(g.Id) && g.Keywords.IsMatch(text));
        }
        return score;
    }

    /// <summary>"Vanilla Weapons Expanded - Laser" is taken only next to "Vanilla Weapons Expanded" when that one was found too.</summary>
    private static bool ParentTaken(string title, Dictionary<string, ulong> titles, HashSet<ulong> taken)
    {
        foreach (var separator in new[] { " - ", ": ", " — ", " – " })
        {
            var at = title.IndexOf(separator, StringComparison.Ordinal);
            if (at <= 0) continue;
            if (titles.TryGetValue(title[..at].Trim(), out var parent) && !taken.Contains(parent)) return false;
        }
        return true;
    }

    /// <summary>
    /// A searched name must be in the mod's text: "star wars" → "star" or "wars", as whole words ("desert" finds "deserts",
    /// not "Deserters").
    /// </summary>
    private static Regex WordKeywords(string words) =>
        new(string.Join("|", words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3)
            .DefaultIfEmpty(words)
            .Select(w => @"\b" + Regex.Escape(w.TrimEnd('s')) + @"(s|es)?\b")), Options);
}
