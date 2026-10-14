using System.Text.RegularExpressions;
using RimForge.Steam.Workshop;

namespace RimForge.Steam.Translations;

/// <summary>
/// How a translation into one language names itself on the Workshop: words in the title or description
/// ("Rus", "русификатор", "汉化"), languages it must not be mistaken for (Ukrainian for Russian), and the word to search with.
/// </summary>
public sealed class LanguageMarkers
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private readonly Regex _title;
    private readonly Regex? _text;
    private readonly Regex? _not;

    /// <param name="title">Markers that count in the title (short codes like "RU" only count there).</param>
    /// <param name="text">Longer words that also count at the start of the description.</param>
    /// <param name="not">Signs of a neighbouring language.</param>
    private LanguageMarkers(string language, string searchWord, string title, string? text, string? not)
    {
        Language = language;
        SearchWord = searchWord;
        _title = new Regex(title, Options);
        _text = text is null ? null : new Regex(text, Options);
        _not = not is null ? null : new Regex(not, Options);
    }

    public string Language { get; }

    /// <summary>Added to the mod's name in the second search ("Pick Up And Haul rus").</summary>
    public string SearchWord { get; }

    /// <summary>The page says it is in this language (and not in a look-alike one).</summary>
    public bool Matches(WorkshopItem item)
    {
        var start = item.Description.Length > 400 ? item.Description[..400] : item.Description;
        if (_not is not null && (_not.IsMatch(item.Title) || _not.IsMatch(start))) return false;
        return _title.IsMatch(item.Title) || _text is not null && _text.IsMatch(start) && item.IsTranslation;
    }

    /// <summary>The title without its language words ("RimHUD rus" → "RimHUD ").</summary>
    public string Strip(string title) => _title.Replace(title, " ");

    private const string Ukrainian = @"[іїєґІЇЄҐ]|\b(ua|ukr|ukrainian|україн\w*|переклад\w*)\b";

    private static readonly Dictionary<string, LanguageMarkers> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Russian"] = new("Russian", "rus",
            @"\b(rus|ru|russian|русск\w*|русификат\w*|руссификат\w*|перевод\w*|русифиц\w*)\b|[а-яё]{3,}",
            @"\b(russian|русск\w*|русификат\w*|перевод\w*)\b", Ukrainian),
        ["Ukrainian"] = new("Ukrainian", "ukr", Ukrainian, null, null),
        ["ChineseSimplified"] = new("ChineseSimplified", "汉化", @"简体|简中|汉化|中文|\b(chs|zh|zh-cn|cn)\b", @"简体|汉化", @"繁體|繁体|正體"),
        ["ChineseTraditional"] = new("ChineseTraditional", "繁體", @"繁體|繁体|正體|\b(cht|zht|zh-tw|tw)\b", @"繁體|繁体", null),
        ["Japanese"] = new("Japanese", "日本語", @"日本語|\b(jp|japanese)\b", @"日本語|japanese", null),
        ["Korean"] = new("Korean", "한국어", @"한국어|한글|\b(kr|ko|korean)\b", @"한국어|한글|korean", null),
        ["German"] = new("German", "german", @"\b(german|deutsch|ger|übersetzung)\b|[\[\(]de[\]\)]|\sde$", @"\b(german|deutsch|übersetzung)\b", null),
        ["French"] = new("French", "french", @"\b(french|français|francais|traduction)\b|[\[\(]fr[\]\)]|\sfr$", @"\b(french|français|traduction)\b", null),
        ["Spanish"] = new("Spanish", "spanish", @"\b(spanish|español|espanol|traducci[oó]n)\b|[\[\(]es[\]\)]|\ses$", @"\b(spanish|español|traducci[oó]n)\b", null),
        ["SpanishLatin"] = new("SpanishLatin", "spanish", @"\b(spanish|español|espanol|latino|traducci[oó]n)\b|[\[\(]es[\]\)]", @"\b(spanish|español|traducci[oó]n)\b", null),
        ["PortugueseBrazilian"] = new("PortugueseBrazilian", "portuguese", @"\b(portuguese|português|portugues|pt-?br|tradu[cç][aã]o)\b|[\[\(](br|pt)[\]\)]", @"\b(portuguese|português|tradu[cç][aã]o)\b", null),
        ["Portuguese"] = new("Portuguese", "portuguese", @"\b(portuguese|português|portugues|tradu[cç][aã]o)\b|[\[\(]pt[\]\)]", @"\b(portuguese|português)\b", null),
        ["Polish"] = new("Polish", "polish", @"\b(polish|polski|spolszczenie|tłumaczenie)\b|[\[\(]pl[\]\)]|\spl$", @"\b(polish|polski|spolszczenie|tłumaczenie)\b", null),
        ["Italian"] = new("Italian", "italian", @"\b(italian|italiano|traduzione)\b|[\[\(]it[\]\)]|\sita$", @"\b(italian|italiano|traduzione)\b", null),
        ["Turkish"] = new("Turkish", "turkish", @"\b(turkish|türkçe|turkce|çeviri)\b|[\[\(]tr[\]\)]", @"\b(turkish|türkçe|çeviri)\b", null),
        ["Czech"] = new("Czech", "czech", @"\b(czech|čeština|cestina|překlad)\b|[\[\(](cz|cs)[\]\)]", @"\b(czech|čeština|překlad)\b", null),
    };

    /// <summary>The markers for a game language id ("Russian"); other languages are recognised by their English name.</summary>
    public static LanguageMarkers For(string language) =>
        Known.TryGetValue(language, out var m) ? m
            : new LanguageMarkers(language, language.ToLowerInvariant(), $@"\b{Regex.Escape(language)}\b", $@"\b{Regex.Escape(language)}\b", null);
}
