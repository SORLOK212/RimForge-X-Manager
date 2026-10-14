using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimForge.Steam.Assistant;

/// <summary>What a neural network understood from the player's message.</summary>
/// <param name="IsFollowUp">It changed the current request (which is given only when the message looks like a change).</param>
/// <param name="Reply">A sentence in the player's language: what it understood.</param>
public sealed record BrainAnswer(PackRequest Request, bool IsFollowUp, string? Reply, string? Name);

/// <summary>
/// Lets a neural network read the request instead of the word list: it turns any wording into the same
/// <see cref="PackRequest"/> the planner uses. It never chooses mods itself — those always come from the
/// Workshop through the planner, so nothing it says can invent a mod.
/// </summary>
public static class AssistantBrain
{
    private static readonly Dictionary<string, string> TopicHints = new()
    {
        ["fantasy"] = "fantasy: elves, dwarves, goblins, dragons, fantasy races",
        ["medieval"] = "medieval: knights, castles, swords, medieval tech",
        ["magic"] = "magic: spells, psycasts, wizards, witches",
        ["scifi"] = "space, sci-fi, lasers, androids, spaceships, future tech",
        ["weapons"] = "weapons and guns",
        ["apparel"] = "clothes, armor, hair",
        ["animals"] = "animals, creatures, pets",
        ["races"] = "playable races, xenotypes, genes",
        ["mechs"] = "mechanoids, mechanitor",
        ["building"] = "building, furniture, decorations",
        ["food"] = "food, cooking, farming, fishing, drinks",
        ["medical"] = "medicine, surgery, implants, bionics",
        ["factions"] = "factions, settlements, pirates, empire",
        ["vehicles"] = "vehicles, cars, tanks",
        ["events"] = "events, quests, storytellers",
        ["horror"] = "horror, zombies, undead, Anomaly",
        ["hardcore"] = "hardcore, realism, survival, difficulty",
        ["ce"] = "Combat Extended (only if the player names it or asks for realistic combat)",
        ["qol"] = "quality of life, interface, convenience",
        ["vanilla"] = "vanilla-friendly content (Vanilla Expanded)",
        ["tribal"] = "tribal, primitive, stone age",
        ["social"] = "traits, moods, relationships, skills, ideology",
        ["graphics"] = "graphics, textures, visuals",
        ["industry"] = "industry, power, factories, production",
    };

    public static string SystemPrompt { get; } = BuildSystemPrompt();

    private static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You help a RimWorld player build a modpack. Turn the player's message into a JSON object for a search of the Steam Workshop.");
        sb.AppendLine("Themes you may use (ids):");
        foreach (var topic in PackTopics.All.Where(t => t.Id != PackTopics.Performance))
            sb.AppendLine($"- {topic.Id}: {TopicHints.GetValueOrDefault(topic.Id, topic.Id)}");
        sb.AppendLine("""
            Answer with JSON only, exactly these fields:
            {"topics": [ids], "search": [strings], "exclude_topics": [ids], "exclude_words": [strings],
             "size": "small"|"normal"|"large", "count": number or null, "name": string, "reply": string}
            Rules:
            - topics: theme ids from the list that the player asks for, most important first. Only ids from the list.
            - search: up to 4 short ENGLISH search phrases (1-2 words, as mod pages on the Workshop are written) for wishes the
              themes don't cover: games, films, books, settings, creatures, styles. Never invent mod names. Often empty.
            - exclude_topics / exclude_words: ONLY what the player explicitly says they do NOT want ("без …", "no …", "убери …").
              exclude_words are short English words found in mod names. If the player excludes nothing, both are empty.
            - size: "small" (~15 mods), "normal" (~30, the default), "large" (~60). count: only if the player gave an exact number.
            - If a current modpack request is given, the message changes it: return the FULL updated request (the current one
              with the changes applied, everything else kept).
            - If the message is not about mods (a greeting, a question), return empty topics and search and answer in "reply".
            - name: a short modpack name (2-4 words) in the player's language. reply: one short friendly sentence in the
              player's language saying what you will look for.
            Examples:
            Player: "хочу средневековье с рыцарями, но без магии" ->
            {"topics":["medieval"],"search":["knights"],"exclude_topics":["magic"],"exclude_words":[],"size":"normal","count":null,
             "name":"Рыцари и замки","reply":"Соберу средневековую сборку с рыцарями и без магии."}
            Player: "space pirates, about 20 mods" ->
            {"topics":["scifi","factions"],"search":["pirates"],"exclude_topics":[],"exclude_words":[],"size":"normal","count":20,
             "name":"Space Pirates","reply":"I'll look for space and pirate mods, about 20 of them."}
            Player: "привет!" ->
            {"topics":[],"search":[],"exclude_topics":[],"exclude_words":[],"size":"normal","count":null,"name":"",
             "reply":"Привет! Напиши, какую сборку собрать — например, «фэнтези с драконами»."}
            """);
        return sb.ToString();
    }

    public static string UserPrompt(string message, PackRequest? current)
    {
        var sb = new StringBuilder();
        if (current is { IsEmpty: false })
            sb.AppendLine("Current modpack request: " + ToJson(current).ToJsonString());
        else
            sb.AppendLine("There is no current modpack yet.");
        sb.AppendLine("Player's message: " + message.Trim());
        return sb.ToString();
    }

    public static async Task<BrainAnswer?> UnderstandAsync(LlmClient client, LlmEndpoint endpoint, string message, PackRequest? current, CancellationToken ct)
    {
        var answer = await client.CompleteAsync(endpoint, SystemPrompt, UserPrompt(message, current), json: true, maxTokens: 500, ct);
        return Parse(answer, current);
    }

    /// <summary>Searches that say nothing about content ("fps", "stability").</summary>
    private static readonly HashSet<string> NoSearch = new(StringComparer.OrdinalIgnoreCase)
    {
        "fps", "tps", "performance", "optimization", "stability", "stable", "lag", "mods", "modpack", "combat", "gameplay", "game",
        "shooting", "shooter", "first person", "fps shooter",
    };

    /// <summary>
    /// The network's reading joined with the word list's: the word list is sure about "fast", "stable", "no DLC", sizes, numbers
    /// and what it found named outright ("без зомби", "Ведьмак"), small networks sometimes miss those.
    /// </summary>
    public static PackRequest Combine(PackRequest brain, ParsedRequest words)
    {
        var w = words.Request;
        var excludeTopics = brain.ExcludeTopics.Union(w.ExcludeTopics).ToList();
        var topics = brain.Topics.Union(w.GuessedTopics ? [] : w.Topics).Except(excludeTopics).ToList();
        var excludeWords = brain.ExcludeWords.Union(w.ExcludeWords, StringComparer.OrdinalIgnoreCase).ToList();
        var search = brain.Words
            .Where(s => !NoSearch.Contains(s) && !s.Split(' ').All(RequestParser.StopWords.Contains))
            .Where(s => !PackTopics.All.Any(t => t.Triggers.IsMatch(s))) // a theme already covers it
            .Union(w.Words, StringComparer.OrdinalIgnoreCase)
            .Where(s => !excludeWords.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Take(6)
            .ToList();
        if (topics.Count == 0 && search.Count == 0 && !w.IsEmpty) return w;
        return brain with
        {
            Topics = topics,
            Words = search,
            ExcludeTopics = excludeTopics,
            ExcludeWords = excludeWords,
            Fps = brain.Fps || w.Fps,
            Stable = brain.Stable || w.Stable,
            NoDlc = brain.NoDlc || w.NoDlc,
            Size = w.Size != PackSize.Normal ? w.Size : brain.Size,
            Count = w.Count ?? brain.Count,
        };
    }

    /// <summary>Reads the network's JSON; anything outside the allowed values is dropped. Null if nothing usable came back.</summary>
    public static BrainAnswer? Parse(string answer, PackRequest? current)
    {
        if (LlmClient.ExtractJson(answer) is not { } json) return null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (node is not JsonObject o) return null;

        var followUp = current is { IsEmpty: false };
        var topics = Topics(o, "topics");
        var search = Words(o, "search", 4);
        var request = new PackRequest
        {
            Topics = topics,
            Words = search,
            ExcludeTopics = Topics(o, "exclude_topics").Except(topics).ToList(),
            ExcludeWords = Words(o, "exclude_words", 8),
            // "Fast", "stable" and "no DLC" come from the word list, which is sure about them; small models guess.
            Fps = current?.Fps ?? false,
            Stable = current?.Stable ?? false,
            NoDlc = current?.NoDlc ?? false,
            Size = Str(o, "size") switch { "small" => PackSize.Small, "large" => PackSize.Large, _ => PackSize.Normal },
            Count = o["count"] is JsonValue v && v.TryGetValue<double>(out var n) && n >= 1 ? Math.Clamp((int)n, 5, 150) : null,
        };
        return new BrainAnswer(request, followUp, Clean(Str(o, "reply"), 400), Clean(Str(o, "name"), 60));
    }

    /// <summary>The request as the network sees it (for follow-ups).</summary>
    public static JsonObject ToJson(PackRequest r) => new()
    {
        ["topics"] = new JsonArray(r.Topics.Select(t => (JsonNode)t).ToArray()),
        ["search"] = new JsonArray(r.Words.Select(t => (JsonNode)t).ToArray()),
        ["exclude_topics"] = new JsonArray(r.ExcludeTopics.Select(t => (JsonNode)t).ToArray()),
        ["exclude_words"] = new JsonArray(r.ExcludeWords.Select(t => (JsonNode)t).ToArray()),
        ["size"] = r.Size.ToString().ToLowerInvariant(),
        ["count"] = r.Count,
    };

    private static List<string> Topics(JsonObject o, string key) =>
        Strings(o, key).Select(s => s.ToLowerInvariant()).Where(id => id != PackTopics.Performance && PackTopics.Get(id) is not null).Distinct().ToList();

    private static List<string> Words(JsonObject o, string key, int max) =>
        Strings(o, key)
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length is >= 2 and <= 40 && s.Any(char.IsLetter))
            .Distinct()
            .Take(max)
            .ToList();

    private static IEnumerable<string> Strings(JsonObject o, string key) =>
        o[key] switch
        {
            JsonArray a => a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>(),
            JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => [s],
            _ => [],
        };

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Clean(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }
}
