using System.Text.RegularExpressions;

namespace RimForge.Steam.Assistant;

/// <summary>The result of reading a request: what to build, and whether it changes the previous pack.</summary>
/// <param name="Understood">Something in the text meant anything to the parser.</param>
public sealed record ParsedRequest(PackRequest Request, bool IsFollowUp, bool Understood);

/// <summary>
/// Reads a request written in plain words (Russian or English) without any AI: themes, "fast", "stable",
/// "small/big", "without X", names of games and films. Follow-ups ("убери магию", "добавь оружие", "поменьше")
/// change the previous request.
/// </summary>
public static class RequestParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex FollowUp = new(
        @"^\s*(добав|ещ[её]\b|убер|убра|без\b|исключ|замен|поменьше|побольше|меньше|больше|плюс|а также|и ещ[её]|также|теперь|сделай|не надо|кроме|" +
        @"add\b|also\b|remove\b|without\b|no\b|more\b|less\b|fewer\b|plus\b|now\b|except\b)", Options);

    private static readonly Regex Negation = new(
        @"\b(без|не надо|не нужн\w*|не хочу|не хочется|кроме|убер\w*|убра\w*|исключ\w*|никаких|никакой|no|without|except|remove|exclude|not)\b", Options);

    /// <summary>Asking for something again after a "no" ("убери драконов и добавь оружие").</summary>
    private static readonly Regex Positive = new(
        @"\b(добав\w*|хочу|хотел\w*|нужн[аоы]?|плюс|включ\w*|верни\w*|оставь|давай|add|want|include|need|plus|keep)\b", Options);

    /// <summary>
    /// Single things inside a theme ("драконы" in fantasy): "без драконов" leaves out mods named so, not the whole theme.
    /// </summary>
    private static readonly (Regex Pattern, string[] Words)[] Things =
    [
        (new(@"дракон", Options), ["dragon"]),
        (new(@"эльф", Options), ["elf", "elves", "elven"]),
        (new(@"гном|дворф", Options), ["dwarf", "dwarves", "dwarven"]),
        (new(@"\bорк(и|ов|ами)?\b", Options), ["orc"]),
        (new(@"гоблин", Options), ["goblin"]),
        (new(@"зомби", Options), ["zombie"]),
        (new(@"вампир", Options), ["vampire", "sanguophage"]),
        (new(@"оборот", Options), ["werewolf", "lycanthrope"]),
        (new(@"андроид", Options), ["android"]),
        (new(@"робот", Options), ["robot"]),
        (new(@"кошкодев|\bнеко\b|кошач", Options), ["neko", "cat"]),
        (new(@"аниме", Options), ["anime"]),
        (new(@"насеком|инсектоид|жук", Options), ["insect"]),
        (new(@"\bтанк", Options), ["tank"]),
        (new(@"лазер", Options), ["laser"]),
        (new(@"псикаст", Options), ["psycast"]),
    ];

    private static readonly Regex Stable = new(
        @"стабильн|без (ошибок|багов|вылетов|крашей|проблем)|не вылета|не крашил|надежн|безглючн|\bstable\b|reliab|no (bugs|crash)", Options);

    private static readonly Regex Small = new(
        @"(маленьк|небольш|компактн|легк|минимальн|скромн)\w* сборк|мало модов|поменьше(?!\s*\p{L})|меньше модов|немного модов|" +
        @"\b(small|minimal|light(weight)?)\b.{0,12}\b(pack|modpack|list)\b|fewer mods|less mods", Options);

    private static readonly Regex Large = new(
        @"(больш|огромн|крупн|мега|гигантск|здоров)\w* сборк|много модов|побольше(?!\s*\p{L})|больше модов|" +
        @"\b(large|big|huge|massive)\b.{0,12}\b(pack|modpack|list)\b|more mods|lots of mods", Options);

    private static readonly Regex Count = new(@"\b(\d{1,3})\s*(мод|mod)", Options);

    private static readonly Regex NoDlc = new(@"без (dlc|длц|дополнени|аддон|длс)|\bno dlcs?\b|without (any )?dlcs?|без платных", Options);

    /// <summary>Names of games, films and settings, written in Russian, and what to search for.</summary>
    private static readonly (Regex Pattern, string Search)[] Aliases =
    [
        (new(@"вархаммер|warhammer|\bwh40k|40к", Options), "warhammer"),
        (new(@"звездн\w* войн", Options), "star wars"),
        (new(@"ведьмак", Options), "witcher"),
        (new(@"властелин\w* колец|толкин|средиземь", Options), "lord of the rings"),
        (new(@"аниме|анимешн", Options), "anime"),
        (new(@"фоллаут|постапок|пост-апок|постъапок", Options), "fallout"),
        (new(@"сталкер", Options), "stalker"),
        (new(@"гарри поттер", Options), "harry potter"),
        (new(@"марвел|мстител", Options), "marvel"),
        (new(@"покемон", Options), "pokemon"),
        (new(@"наруто", Options), "naruto"),
        (new(@"\bдюн[аыу]\b", Options), "dune"),
        (new(@"стар трек|звездн\w* путь", Options), "star trek"),
        (new(@"викинг|скандинав", Options), "viking"),
        (new(@"самура|япони|японск", Options), "japanese"),
        (new(@"\bрим\b|римск|античн|древн\w* (рим|греци)|греческ", Options), "classical"),
        (new(@"ковбо|дик\w* запад|вестерн", Options), "western"),
        (new(@"стимпанк", Options), "steampunk"),
        (new(@"ниндзя", Options), "ninja"),
        (new(@"лавкрафт|ктулху", Options), "lovecraft"),
        (new(@"египет|египетск", Options), "egypt"),
        (new(@"викториан", Options), "victorian"),
        (new(@"славян", Options), "slavic"),
        (new(@"старкрафт", Options), "starcraft"),
        (new(@"масс эффект", Options), "mass effect"),
        (new(@"китайск|китай\b", Options), "chinese"),
    ];

    /// <summary>English words that never name what to search for.</summary>
    internal static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "with", "without", "for", "of", "in", "on", "to", "me", "my", "i", "want", "need", "like",
        "please", "some", "any", "lots", "lot", "many", "much", "more", "less", "few", "fewer", "good", "great", "best", "nice", "high",
        "low", "fast", "stable", "small", "big", "large", "huge", "mod", "mods", "modpack", "modlist", "pack", "list", "build", "make",
        "create", "game", "rimworld", "rim", "fps", "tps", "dlc", "dlcs", "ce", "qol", "ui", "pc", "sci", "fi", "no", "not", "add",
        "remove", "also", "plus", "now", "that", "this", "it", "is", "be", "should", "run", "runs", "work", "works", "lag", "lags",
        "performance", "playthrough", "colony", "theme", "themed", "style", "set", "setting", "vanilla", "expanded", "friendly",
    };

    private static readonly Regex Clauses = new(@"[.,;:!?\n()]+|\s(но|а|однако|but|however)\s", Options);
    private static readonly Regex Joins = new(@"\s(и|или|and|or)\s", Options);
    private static readonly Regex LatinPhrase = new(@"[A-Za-z][A-Za-z0-9'\-]*(\s+[A-Za-z][A-Za-z0-9'\-]*)*");

    /// <summary>The themes when the player named none.</summary>
    public static IReadOnlyList<string> DefaultTopics { get; } = ["vanilla", "qol"];

    public static ParsedRequest Parse(string text, PackRequest? previous = null)
    {
        var norm = (text ?? "").Replace('ё', 'е').Replace('Ё', 'Е').Trim();
        var lower = norm.ToLowerInvariant();

        var include = new List<string>();
        var exclude = new List<string>();
        var words = new List<string>();
        var notWords = new List<string>();
        var hasCyrillic = Regex.IsMatch(norm, @"\p{IsCyrillic}");

        // "без dlc" is a setting, not a theme to leave out.
        foreach (var clause in Clauses.Split(NoDlc.Replace(norm, " ")).Where(c => !string.IsNullOrWhiteSpace(c) && !IsConjunction(c)))
        {
            var negative = false;
            foreach (var part in Joins.Split(clause).Where(p => !string.IsNullOrWhiteSpace(p) && !IsConjunction(p)))
            {
                // "космическая сборка без магии": what comes before "без" is wanted, what follows is not.
                // "без магии и драконов": the second part is negative too, "…и добавь оружие" is not.
                var no = Negation.Match(part);
                if (no.Success)
                {
                    if (no.Index > 0) Read(part[..no.Index], negative);
                    negative = true;
                    Read(part[no.Index..], negative);
                }
                else
                {
                    if (Positive.IsMatch(part)) negative = false;
                    Read(part, negative);
                }
            }
        }

        void Read(string part, bool negative)
        {
            var partLower = part.ToLowerInvariant();
            if (negative)
            {
                // "без драконов" leaves out dragons, not all of fantasy.
                foreach (var (pattern, things) in Things.Where(t => t.Pattern.IsMatch(partLower)))
                {
                    notWords.AddRange(things);
                    partLower = pattern.Replace(partLower, " ");
                }
            }
            foreach (var topic in PackTopics.All.Where(t => t.Id != PackTopics.Performance && t.Triggers.IsMatch(partLower)))
                (negative ? exclude : include).Add(topic.Id);
            foreach (var (pattern, search) in Aliases.Where(a => a.Pattern.IsMatch(partLower)))
                (negative ? notWords : words).Add(search);
            foreach (var phrase in NamePhrases(part, hasCyrillic))
                (negative ? notWords : words).Add(phrase);
        }

        var fps = PackTopics.Get(PackTopics.Performance)!.Triggers.IsMatch(lower);
        var stable = Stable.IsMatch(lower);
        var noDlc = NoDlc.IsMatch(lower);
        int? count = Count.Match(lower) is { Success: true } m ? Math.Clamp(int.Parse(m.Groups[1].Value), 5, 150) : null;
        PackSize? size = Small.IsMatch(lower) ? PackSize.Small : Large.IsMatch(lower) ? PackSize.Large : null;

        var understood = include.Count + exclude.Count + words.Count + notWords.Count > 0 || fps || stable || noDlc || count is not null || size is not null;
        var followUp = previous is { IsEmpty: false } && (FollowUp.IsMatch(lower) || include.Count == 0 && words.Count == 0);
        var basis = followUp ? previous! : new PackRequest();

        var request = new PackRequest
        {
            Topics = Merge(basis.Topics, include, exclude),
            ExcludeTopics = Merge(basis.ExcludeTopics, exclude, include),
            Words = Merge(basis.Words, words, notWords),
            ExcludeWords = Merge(basis.ExcludeWords, notWords, words),
            Fps = basis.Fps || fps,
            Stable = basis.Stable || stable,
            NoDlc = basis.NoDlc || noDlc,
            // "добавь оружие" adds to the pack rather than squeezing the themes already in it.
            Count = count ?? (size is not null ? null
                : followUp && include.Concat(words).Except(basis.Topics).Except(basis.Words).Distinct().Count() is var added && added > 0
                    ? Math.Min(150, basis.Target + 8 * added)
                    : basis.Count),
            Size = size ?? basis.Size,
        };
        // Only "fast", "stable" or a size: a vanilla-like pack with conveniences is the safe guess.
        if (request.IsEmpty && understood && request.ExcludeTopics.Count == 0)
            request = request with { Topics = DefaultTopics, GuessedTopics = true };
        return new ParsedRequest(request, followUp, understood);
    }

    private static bool IsConjunction(string s) => s.Trim().ToLowerInvariant() is "и" or "или" or "но" or "а" or "and" or "or" or "but";

    private static List<string> Merge(IReadOnlyList<string> basis, IEnumerable<string> add, IEnumerable<string> remove)
    {
        var removed = remove.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return basis.Where(x => !removed.Contains(x)).Concat(add).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Names written in Latin letters ("Warhammer", "Star Wars"): searched as they are. In an English request only
    /// capitalised words count, so that "with good fps" doesn't turn into searches.
    /// </summary>
    private static IEnumerable<string> NamePhrases(string part, bool hasCyrillic)
    {
        foreach (Match m in LatinPhrase.Matches(part))
        {
            // Words that name a theme ("Combat Extended", "fantasy") are not searched again.
            var rest = PackTopics.All.Aggregate(m.Value, (s, topic) => topic.Triggers.Replace(s, " "));
            var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 3 && !StopWords.Contains(t))
                .Where(t => hasCyrillic || char.IsUpper(t[0]))
                .Where(t => !PackTopics.All.Any(topic => topic.Triggers.IsMatch(t.ToLowerInvariant())))
                .ToList();
            if (tokens.Count > 0) yield return string.Join(' ', tokens).ToLowerInvariant();
        }
    }
}
