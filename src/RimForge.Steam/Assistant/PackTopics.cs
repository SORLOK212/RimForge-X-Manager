using System.Text.RegularExpressions;
using RimForge.Steam.Workshop;

namespace RimForge.Steam.Assistant;

/// <summary>
/// A theme the assistant can build a modpack around ("fantasy", "sci-fi", "quality of life"…).
/// </summary>
/// <param name="Glyph">Segoe Fluent Icons glyph for the quick button.</param>
/// <param name="Triggers">Words in the player's request (Russian and English stems) that ask for this theme.</param>
/// <param name="Queries">Workshop searches (mod pages are mostly in English).</param>
/// <param name="Keywords">A found mod must mention one of these (title, tags or description) to count as on-topic.</param>
/// <param name="Core">Well-known, maintained mods of the theme, checked by hand: offered first.</param>
public sealed record PackTopic(string Id, string Glyph, Regex Triggers, IReadOnlyList<string> Queries, Regex Keywords, IReadOnlyList<ulong> Core)
{
    /// <summary>Shown as a quick button on the assistant page.</summary>
    public bool IsQuick { get; init; } = true;
}

public static class PackTopics
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>The themes with a quick button, in the order shown.</summary>
    private static readonly string[] QuickIds =
        ["fantasy", "medieval", "scifi", "vanilla", "magic", "races", "weapons", "animals", "factions", "building", "horror", "hardcore", "qol"];

    private static PackTopic T(string id, char glyph, string triggers, string[] queries, string keywords, ulong[] core) =>
        new(id, glyph.ToString(), new Regex(triggers, Options), queries, new Regex(keywords, Options), core) { IsQuick = QuickIds.Contains(id) };

    public const string Performance = "performance";
    public const string CombatExtended = "ce";

    public static IReadOnlyList<PackTopic> All { get; } =
    [
        T("fantasy", '\uE734',
            @"фэнтез|фентез|фэнтэз|fantasy|сказочн|эльф|гном|дворф|\bорк(и|а|ов|ами)?\b|гоблин|дракон|мифическ|dragon|\belf|elves|dwar(f|ves)|goblin",
            ["fantasy", "medieval fantasy", "magic", "dragon", "elves", "dwarves", "goblin"],
            @"fantasy|magic|dragon|\belf|elves|elven|dwar(f|v)|goblin|\borcs?\b|medieval|myth|wizard|sorcer|spell|fairy|undead|necroman|psycast|rune",
            [3632363090, 2955864975, 2942661554, 3035624471, 2842502659, 2026992161, 3444347874]),
        T("medieval", '\uE80F',
            @"средневек|рыцар|замок|замки|замков|феодал|medieval|knight|castle",
            ["medieval", "knights", "castle", "medieval weapons"],
            @"medieval|knight|castle|sword|crossbow|feudal|kingdom|\blords?\b|middle ages|plate armou?r",
            [3444347874, 3219596926, 2501486827, 3028566550, 2101545242]),
        T("magic", '\uE706',
            @"маги(я|и|ю|ей)\b|\bмаг(и|ов|ами)?\b|магическ|волшеб|заклин|колдун|колдов|ведьм|чародей|псикаст|\bmagic|spells?\b|wizard|psycast",
            ["magic", "psycasts", "spells", "witch"],
            @"magic|spell|psycast|wizard|sorcer|witch|\bmages?\b|arcane|\bmana\b|enchant|rune|necroman",
            [2842502659, 1201382956, 3339063087]),
        T("scifi", '\uE909',
            @"космос|космич|sci-?fi|научн\w* фантаст|будущ|футурист|лазер|андроид|робот|звездол|бластер|киберпанк|\bspace\b|futur|laser|android|cyber",
            ["sci-fi", "space", "spacer", "laser", "android", "plasma"],
            @"sci-?fi|space|spacer|laser|plasma|android|robot|starship|spaceship|futur|cyber|glitterworld|gravship|orbital|archotech",
            [2028381079, 1989352844, 2375692535, 2975771801]),
        T("weapons", '\uE945',
            @"оруж|пушк|стволы|винтов|пистолет|автоматы|автоматов|\bмеч(и|ей|ами)?\b|weapon|\bguns?\b",
            ["weapons", "guns", "melee weapons"],
            @"weapon|\bguns?\b|rifle|pistol|sword|blade|melee|ranged|launcher|grenade|turret|armory|arsenal",
            [1814383360, 2329126791, 2194472657, 1906780517, 927155256]),
        T("apparel", '\uEA18',
            @"одежд|брон[яиюей]\b|доспех|шлем|костюм|причес|apparel|armou?r|clothes|clothing|outfit",
            ["apparel", "armor", "clothing"],
            @"apparel|cloth|outfit|armou?r|helmet|\bhats?\b|dress|uniform|hair|accessor|cape",
            [1814987817, 1814988282, 1888705256]),
        T("animals", '\uED54',
            @"животн|звер[ьеия]|зверуш|питом|фаун|\bскот|animal|creature|\bpets?\b",
            ["animals", "creatures", "pets"],
            @"animal|creature|beast|\bpets?\b|wildlife|fauna|insect|livestock|\bbirds?\b|hybrid",
            [2871933948, 1541721856, 1098354593, 2801160906]),
        T("races", '\uE716',
            @"\bрас[аыуеой]?\b|ксенотип|\bген(ы|ов)\b|генетик|инопланет|кошкодев|\bнеко\b|\brace|xenotype|\bgenes?\b|alien|neko",
            ["race", "xenotype", "genes", "humanoid alien races"],
            @"\brace\b|xenotype|\bgenes?\b|species|humanoid|alien|kin\b",
            [2975771801, 2880990495, 2963116383, 2927323805, 3114453100, 2891845502]),
        T("mechs", '\uE7FC',
            @"\bмех(и|ов|ами)?\b|механоид|mechanoid|\bmechs?\b|mechanitor",
            ["mechanoid", "mechs", "mechanitor"],
            @"\bmechs?\b|mechanoid|mechanitor|robot|drone",
            [3365118555]),
        T("building", '\uE80A',
            @"\bстроит|\bпострои|\bпостройк|стройк|мебел|декор|интерьер|архитект|furniture|building|decor",
            ["furniture", "architecture", "decorations"],
            @"furniture|build|architect|decor|\bwalls?\b|floor|\bdoors?\b|structure|storage|lamp|light|table|chair|\bbeds?\b",
            [1718190143, 2608762624, 2062943477]),
        T("food", '\uE719',
            @"\bед[аыу]\b|кулинар|готовк|\bблюд|ферм|сельск|урожа|огород|растени|рыбалк|пиво|алкогол|\bfood|cooking|\bfarm|crops?\b",
            ["food", "cooking", "crops", "farming"],
            @"food|cook|meal|crop|plant|farm|garden|brew|drink|fish|kitchen|harvest|recipe",
            [2134308519, 2134308522, 2186560858, 1914064942]),
        T("medical", '\uE95E',
            @"медиц|лечени|имплант|бионик|протез|хирург|операци|medic|implant|bionic|prosthet|surgery",
            ["implants", "prosthetics", "bionics", "medicine"],
            @"medic|surger|implant|bionic|prosthe|organ|hospital|disease|health|drug",
            [1949064302]),
        T("factions", '\uE7C1',
            @"фракци|поселени|пират|\bимпери|faction|pirate|settlement",
            ["faction", "factions", "settlements"],
            @"faction|settlement|tribe|pirate|empire|outlander|\braids?\b",
            [2052918119, 2723801948, 3079786283, 2938820380, 3309003431]),
        T("vehicles", '\uE804',
            @"транспорт|машин[аыу]\b|машин\b|\bтанк|вертол|самол[её]т|vehicle|\btanks?\b|\bcars?\b",
            ["vehicles", "vehicle", "tank"],
            @"vehicle|\bcars?\b|\btanks?\b|truck|aircraft|\bboats?\b|helicopter|transport",
            [3014906877, 3014915404]),
        T("events", '\uE736',
            @"событи|ивент|квест|приключен|рассказчик|сюжет|\bevents?\b|quests?\b|storyteller|adventure",
            ["events", "quests", "storyteller", "incidents"],
            @"\bevents?\b|incident|quest|storyteller|story|encounter|adventure",
            [1938420742, 3411401573, 3461526070, 3215569151]),
        T("horror", '\uE7E8',
            @"\bужас(ы|ов|ам|ами|ах)?\b|хоррор|зомби|нежит|жуть|жутк|мистик|аномал|монстр|вампир|horror|zombie|undead|anomaly|vampire",
            ["horror", "zombie", "undead", "anomaly"],
            @"horror|zombie|undead|anomaly|eldritch|monster|ghoul|vampire|creep|\bcults?\b|nightmare",
            [928376710]),
        T("hardcore", '\uE7BA',
            @"хардкор|высок\w* сложн|сложн\w* (игр|сборк|выживан|режим)|выживани|реалист|реализм|жестк|жёстк|hardcore|survival|realis|difficult",
            ["hardcore", "realistic", "survival", "difficulty"],
            @"hardcore|realis|survival|difficult|challeng|harsh|brutal|immersi",
            []),
        T(CombatExtended, '\uE7BA',
            @"combat extended|\bce\b|комбат экстенд|реалистичн\w* бо[йяи]",
            ["combat extended"],
            @"combat extended|\bCE\b",
            [2890901044]),
        T("qol", '\uE713',
            @"удобств|удобн|качеств\w* жизни|интерфейс|\bqol\b|quality of life|\bui\b",
            ["quality of life", "QoL", "interface"],
            @"quality of life|\bQoL\b|interface|\bUI\b|convenien|manage|automat|\btools?\b|hotkey|button|overlay|priorit|haul",
            [1279012058, 1508850027, 1446523594, 730936602, 761219125, 1541460369, 867467808, 1414302321, 2683622537, 3232242247, 3513846773]),
        T(Performance, '\uEC4A',
            @"\bfps\b|фпс|\bлаг(и|ов|ал\w*|а(ет|ть|ют)|ам)?\b|подлаг|тормоз|производительн|оптимиз|слаб\w* (пк|комп|желез|ноут)|performance|\btps\b|тпс|быстр\w* работ",
            ["performance", "optimization"],
            @"performance|optimi|\bTPS\b|\bFPS\b|\blag|faster|speed ?up|\btick|cache",
            [2664723367, 3524116050, 3676343434, 3621644930, 3652938473]),
        T("vanilla", '\uE73E',
            @"ванил|vanilla|\bлор\b|лорн|близк\w* к оригинал|в духе (оригинал|игры)",
            ["vanilla expanded", "vanilla friendly"],
            @"vanilla",
            [1718190143, 2134308519, 1814383360, 1814987817, 1814988282, 2134308522, 1938420742, 2296404655, 2193152410, 1914064942, 3400246558, 2871933948]),
        T("tribal", '\uE80F',
            @"племен|племя|первобыт|дикар|каменн\w* век|tribal|tribe|primitive",
            ["tribal", "primitive", "neolithic"],
            @"tribal|tribe|primitive|neolithic|\bbows?\b|spear|stone age|caveman",
            [3079786283, 2454918552]),
        T("social", '\uE716',
            @"характер|\bчерт[ыа]?\b|настроени|отношени|психолог|навык|предыстор|романт|социальн|идеолог|\btraits?\b|\bmood|social|relationship|psycholog",
            ["traits", "mood", "social", "relationships"],
            @"\btraits?\b|\bmood|social|relation|backstor|psycholog|\bskills?\b|thought|aspiration|personalit|romance|ideolog",
            [2296404655, 3268401022, 3400246558, 2861806869]),
        T("graphics", '\uE790',
            @"графи|текстур|красив|визуал|анимаци|ретекстур|texture|graphic|visual|retexture",
            ["retexture", "textures", "graphics"],
            @"retexture|texture|graphic|visual|sprite|animation|\bhair|effect|shader|\bart\b",
            [2016436324, 2917732219, 1888705256]),
        T("industry", '\uE7FC',
            @"промышл|индустр|электрич|энерги|завод|фабрик|производств|нефт|industr|\bpower\b|electric|factory",
            ["industry", "power", "factory", "production"],
            @"industr|\bpower|electric|generator|factory|automat|production|\boil\b|chemfuel|machin|conveyor",
            [1321849735, 2062943477]),
    ];

    public static PackTopic? Get(string id) => All.FirstOrDefault(t => t.Id == id);

    public static IReadOnlyList<PackTopic> Quick { get; } = QuickIds.Select(id => All.First(t => t.Id == id)).ToList();

    /// <summary>
    /// Mods that are known to cost a lot of frames or memory: left out when the player asks for a fast game.
    /// Very big downloads (lots of textures) count too, see <see cref="IsHeavy"/>.
    /// </summary>
    public static IReadOnlySet<ulong> Heavy { get; } = new HashSet<ulong>
    {
        1201382956, // A RimWorld of Magic
        928376710,  // Zombieland
        1909914131, // Save Our Ship 2
        2890901044, // Combat Extended
        3219596926, // Medieval Overhaul
    };

    /// <summary>
    /// Expansions the hand-checked mods need (their pages don't always say "requires" in a way
    /// <see cref="WorkshopItem.GuessRequiredDlcs"/> understands).
    /// </summary>
    public static IReadOnlyDictionary<ulong, string[]> CoreDlcs { get; } = new Dictionary<ulong, string[]>
    {
        [3632363090] = ["Biotech"],             // Fantasy Biotech
        [3035624471] = ["Ideology"],            // Medieval Fantasy Themed Relic Quests
        [2842502659] = ["Royalty"],             // Vanilla Psycasts Expanded
        [3339063087] = ["Royalty"],             // VPE - Runesmith
        [2975771801] = ["Biotech"],             // VRE - Android
        [2880990495] = ["Biotech"],             // VRE - Saurid
        [2963116383] = ["Biotech"],             // VRE - Sanguophage
        [2927323805] = ["Biotech"],             // VRE - Phytokin
        [3114453100] = ["Biotech"],             // VRE - Lycanthrope
        [2891845502] = ["Biotech"],             // Alpha Genes
        [3365118555] = ["Biotech"],             // Mechanoid Upgrades
        [2938820380] = ["Royalty"],             // VFE - Empire
    };

    public static bool IsHeavy(WorkshopItem item) => Heavy.Contains(item.Id) || item.SizeBytes > 150L << 20;
}
