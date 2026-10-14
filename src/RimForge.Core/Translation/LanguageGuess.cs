using System.Globalization;

namespace RimForge.Core.Translation;

/// <summary>Quick, offline guesses about a text's writing system — enough to skip needless translations.</summary>
public static class LanguageGuess
{
    public enum Writing { Latin, Cyrillic, Han, Kana, Hangul, Other }

    /// <summary>The writing system most of the letters belong to.</summary>
    public static Writing Script(string text)
    {
        int latin = 0, cyrillic = 0, han = 0, kana = 0, hangul = 0, other = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            switch (c)
            {
                case >= 'A' and <= 'z' or >= 'À' and <= 'ɏ': latin++; break;
                case >= 'Ѐ' and <= 'ӿ': cyrillic++; break;
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ': hangul++; break;
                case >= '一' and <= '鿿' or >= '㐀' and <= '䶿': han++; break;
                default: other++; break;
            }
        }
        // Japanese mixes kana with Han characters; any kana means Japanese.
        if (kana > 0 && kana + han >= latin) return Writing.Kana;
        var best = new[] { (Writing.Latin, latin), (Writing.Cyrillic, cyrillic), (Writing.Han, han), (Writing.Hangul, hangul), (Writing.Other, other) }
            .MaxBy(x => x.Item2);
        return best.Item2 == 0 ? Writing.Latin : best.Item1;
    }

    /// <summary>
    /// True when the text is surely in <paramref name="target"/> already: Cyrillic for Russian; for English,
    /// plain Latin letters with no accents (German, French... have them).
    /// </summary>
    public static bool IsAlreadyIn(string text, string target)
    {
        var script = Script(text);
        return target switch
        {
            "ru" => script == Writing.Cyrillic,
            "en" => script == Writing.Latin && !text.Any(c => c is >= 'À' and <= 'ɏ'),
            _ => false,
        };
    }

    /// <summary>Language name in the interface language, e.g. ("zh-CN", "ru") → "китайский".</summary>
    public static string DisplayName(string code, string uiLanguage)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).Parent is { Name.Length: > 0 } parent && code.Contains('-')
                ? parent : CultureInfo.GetCultureInfo(code);
            var names = uiLanguage == "ru" ? RussianNames : null;
            if (names is not null && names.TryGetValue(name.TwoLetterISOLanguageName, out var ru)) return ru;
            return name.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static readonly Dictionary<string, string> RussianNames = new()
    {
        ["en"] = "английский", ["ru"] = "русский", ["zh"] = "китайский", ["ja"] = "японский", ["ko"] = "корейский",
        ["de"] = "немецкий", ["fr"] = "французский", ["es"] = "испанский", ["pt"] = "португальский", ["pl"] = "польский",
        ["it"] = "итальянский", ["tr"] = "турецкий", ["uk"] = "украинский", ["cs"] = "чешский", ["hu"] = "венгерский",
        ["nl"] = "нидерландский", ["sv"] = "шведский", ["fi"] = "финский", ["th"] = "тайский", ["vi"] = "вьетнамский",
        ["id"] = "индонезийский", ["be"] = "белорусский", ["kk"] = "казахский", ["ar"] = "арабский", ["no"] = "норвежский",
        ["da"] = "датский", ["ro"] = "румынский", ["el"] = "греческий", ["bg"] = "болгарский",
    };

    /// <summary>
    /// Best offline guess of the language ("en", "zh-CN", "ja", "ru", "de"...): the writing system first,
    /// then, for Latin and Cyrillic text, the most frequent common words.
    /// </summary>
    public static string Guess(string text)
    {
        switch (Script(text))
        {
            case Writing.Han: return "zh-CN";
            case Writing.Kana: return "ja";
            case Writing.Hangul: return "ko";
            case Writing.Cyrillic:
                return text.Any(c => c is 'і' or 'ї' or 'є' or 'ґ' or 'І' or 'Ї' or 'Є') ? "uk" : "ru";
        }
        var words = text.ToLowerInvariant().Split([' ', '\n', '\t', ',', '.', '!', '?', ';', ':', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries);
        var best = "en";
        var bestScore = 0;
        foreach (var (lang, common) in CommonWords)
        {
            // Letters only some languages use count double: "ß" is German, "ñ" Spanish...
            var score = words.Count(common.Contains) + 2 * text.Count(c => SpecialLetters.TryGetValue(c, out var l) && l == lang);
            if (score > bestScore)
            {
                bestScore = score;
                best = lang;
            }
        }
        return best;
    }

    private static readonly Dictionary<char, string> SpecialLetters = new()
    {
        ['ß'] = "de", ['ä'] = "de", ['ö'] = "de", ['ü'] = "de",
        ['ñ'] = "es", ['¿'] = "es", ['¡'] = "es",
        ['ã'] = "pt", ['õ'] = "pt", ['ç'] = "pt",
        ['ą'] = "pl", ['ę'] = "pl", ['ł'] = "pl", ['ś'] = "pl", ['ż'] = "pl", ['ź'] = "pl", ['ń'] = "pl", ['ć'] = "pl",
        ['ı'] = "tr", ['ş'] = "tr", ['ğ'] = "tr",
        ['è'] = "it", ['ù'] = "it", ['ò'] = "it",
        ['œ'] = "fr", ['ê'] = "fr", ['â'] = "fr", ['î'] = "fr",
    };

    private static readonly (string Lang, HashSet<string> Words)[] CommonWords =
    [
        ("en", ["the", "and", "of", "to", "is", "for", "with", "this", "that", "you", "it", "are", "on", "your", "will", "can", "be", "a"]),
        ("de", ["der", "die", "und", "das", "ist", "nicht", "mit", "ein", "eine", "für", "auf", "den", "dem", "sie", "ich", "es", "zu",
            "dieser", "diese", "neue", "hinzu", "wird", "werden", "auch", "oder"]),
        ("fr", ["le", "la", "les", "et", "des", "est", "une", "pour", "dans", "que", "qui", "pas", "sur", "avec", "du", "vous"]),
        ("es", ["el", "los", "las", "y", "es", "una", "para", "con", "que", "del", "por", "se", "su", "como", "pero"]),
        ("pt", ["o", "os", "as", "e", "é", "um", "uma", "para", "com", "não", "que", "do", "da", "dos", "em", "você"]),
        ("pl", ["i", "w", "nie", "na", "się", "jest", "z", "do", "to", "że", "jak", "dla", "oraz", "ten", "od"]),
        ("it", ["il", "di", "che", "è", "per", "una", "con", "non", "sono", "gli", "della", "del", "questo", "anche"]),
        ("tr", ["ve", "bir", "bu", "için", "ile", "da", "de", "olarak", "çok", "daha", "gibi", "değil"]),
        ("nl", ["de", "het", "een", "en", "van", "is", "niet", "met", "voor", "op", "dat", "zijn", "je"]),
    ];

    /// <summary>"zh-CN" → "ZH".</summary>
    public static string ShortCode(string code) => code.Split('-')[0].ToUpperInvariant();
}
