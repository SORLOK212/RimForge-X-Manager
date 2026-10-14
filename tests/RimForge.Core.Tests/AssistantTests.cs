using RimForge.Steam.Assistant;
using RimForge.Steam.Workshop;

namespace RimForge.Core.Tests;

public class AssistantTests
{
    [Fact]
    public void Parser_ReadsThemeFpsAndStability()
    {
        var r = RequestParser.Parse("Я хочу себе сборку, связанную с фэнтези. Но при этом, чтобы она выдавала большое количество FPS и стабильно работала.");
        Assert.True(r.Understood);
        Assert.Equal(["fantasy"], r.Request.Topics);
        Assert.True(r.Request.Fps);
        Assert.True(r.Request.Stable);
        Assert.Equal(PackSize.Normal, r.Request.Size); // "большое количество FPS" is not a big pack
    }

    [Fact]
    public void Parser_WordsInsideOtherWordsDontCount()
    {
        // "сборка" hides "орка", "настроить" hides "строит", "лагерь" hides "лаг".
        var r = RequestParser.Parse("космическая сборка, хочу настроить лагерь");
        Assert.Equal(["scifi"], r.Request.Topics);
        Assert.False(r.Request.Fps);
    }

    [Fact]
    public void Parser_NegationCoversOnlyWhatFollows()
    {
        var r = RequestParser.Parse("средневековье без магии и драконов, 20 модов");
        Assert.Equal(["medieval"], r.Request.Topics);
        Assert.Equal(["magic"], r.Request.ExcludeTopics);
        Assert.Contains("dragon", r.Request.ExcludeWords);
        Assert.Equal(20, r.Request.Count);
    }

    [Fact]
    public void Parser_NoDlcIsASettingNotATheme()
    {
        var r = RequestParser.Parse("космос и андроиды без DLC");
        Assert.True(r.Request.NoDlc);
        Assert.Contains("scifi", r.Request.Topics);
        Assert.Empty(r.Request.ExcludeTopics);
    }

    [Fact]
    public void Parser_FollowUpChangesThePreviousRequest()
    {
        var first = RequestParser.Parse("фэнтези для слабого пк").Request;
        var second = RequestParser.Parse("убери драконов и добавь оружие", first);
        Assert.True(second.IsFollowUp);
        Assert.Equal(["fantasy", "weapons"], second.Request.Topics);
        Assert.Contains("dragon", second.Request.ExcludeWords);
        Assert.True(second.Request.Fps);
        Assert.True(second.Request.Target > first.Target); // adding a theme grows the pack

        var third = RequestParser.Parse("поменьше", second.Request);
        Assert.Equal(PackSize.Small, third.Request.Size);
        Assert.Equal(["fantasy", "weapons"], third.Request.Topics);
    }

    [Fact]
    public void Parser_KnowsNamesAndGuessesWhenNoTheme()
    {
        var names = RequestParser.Parse("сборка по вархаммеру и Star Wars");
        Assert.Contains("warhammer", names.Request.Words);
        Assert.Contains("star wars", names.Request.Words);

        var english = RequestParser.Parse("a fantasy modpack with good fps, no magic");
        Assert.Equal(["fantasy"], english.Request.Topics);
        Assert.Equal(["magic"], english.Request.ExcludeTopics);
        Assert.Empty(english.Request.Words); // "good" is not a search

        var guess = RequestParser.Parse("сборка для слабого ПК");
        Assert.True(guess.Request.GuessedTopics);
        Assert.Equal(RequestParser.DefaultTopics, guess.Request.Topics);

        Assert.False(RequestParser.Parse("привет").Understood);
    }

    private static WorkshopItem Item(ulong id, string title, long subs, string description = "", long sizeMb = 5, string version = "1.6") => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Subscribers = subs,
        SizeBytes = sizeMb << 20,
        UpdatedUtc = DateTime.UtcNow.AddMonths(-2),
        Tags = ["Mod", version],
    };

    private sealed class FakeSource(Dictionary<string, List<WorkshopItem>> searches, IEnumerable<WorkshopItem> known) : IWorkshopSource
    {
        private readonly Dictionary<ulong, WorkshopItem> _known = known.ToDictionary(i => i.Id);
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<WorkshopItem>> SearchAsync(WorkshopQuery query, CancellationToken ct)
        {
            lock (Queries) Queries.Add(query.Text!);
            return Task.FromResult<IReadOnlyList<WorkshopItem>>(searches.GetValueOrDefault(query.Text!) ?? []);
        }

        public Task<IReadOnlyList<WorkshopItem>> GetDetailsAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkshopItem>>(ids.Where(_known.ContainsKey).Select(id => _known[id]).ToList());
    }

    private static readonly PackContext Context = new("1.6", ["Royalty", "Ideology", "Biotech"], new HashSet<ulong> { 2 });

    [Fact]
    public async Task Planner_KeepsOnlyFittingMods()
    {
        var found = new List<WorkshopItem>
        {
            Item(1, "Dragon Lands", 90_000, "Adds dragons to a fantasy world."),
            Item(2, "Elven Kingdom", 50_000, "Fantasy elves faction."),
            Item(3, "Dragon Lands - Russian Translation", 9_000, "Перевод"),
            Item(4, "Old Fantasy Mod", 70_000, "fantasy", version: "1.4"),
            Item(5, "Fantasy Patch for Dragon Lands", 20_000, "fantasy patch"),
            Item(6, "Totally Unrelated Hats", 80_000, "Hats for everyone."),
            Item(7, "Tiny Fantasy Thing", 120, "fantasy"),
            Item(8, "Fantasy Framework", 400_000, "fantasy library"),
            Item(9, "Goblin Raiders", 30_000, "Fantasy goblins. Requires Anomaly."),
        };
        var source = new FakeSource(new() { ["fantasy"] = found }, found);
        var request = new PackRequest { Topics = ["fantasy"], Count = 10 };

        var plan = await PackPlanner.BuildAsync(request, Context, source);

        Assert.Equal([1UL, 2], plan.Picks.Select(p => p.Item.Id).Order().ToList());
        Assert.True(plan.Picks.Single(p => p.Item.Id == 2).IsInstalled);
        Assert.Equal(1, plan.Notes.DlcSkipped); // Goblin Raiders needs Anomaly, which the player lacks
    }

    [Fact]
    public async Task Planner_FastGameDropsHeavyModsAndAddsPerformanceMods()
    {
        var found = new List<WorkshopItem>
        {
            Item(1, "Dragon Lands", 90_000, "fantasy dragons"),
            Item(10, "Huge Fantasy Textures", 200_000, "fantasy", sizeMb: 400),
            Item(11, "RimFantasy - Medieval Overhaul Edition", 80_000, "Built for Medieval Overhaul. fantasy"),
        };
        var perf = PackTopics.Get(PackTopics.Performance)!.Core.Select(id => Item(id, "Perf " + id, 100_000, "performance")).ToList();
        var source = new FakeSource(new() { ["fantasy"] = found }, found.Concat(perf));
        var request = new PackRequest { Topics = ["fantasy"], Fps = true };

        var plan = await PackPlanner.BuildAsync(request, Context, source);

        Assert.Contains(plan.Picks, p => p.Item.Id == 1);
        Assert.DoesNotContain(plan.Picks, p => p.Item.Id is 10 or 11);
        Assert.Contains("Huge Fantasy Textures", plan.Notes.HeavySkipped);
        Assert.Equal(perf.Count, plan.Picks.Count(p => p.IsPerformance));
    }

    [Fact]
    public async Task Planner_ShareIsFairAndAddOnsFollowTheirMod()
    {
        var fantasy = Enumerable.Range(100, 20).Select(i => Item((ulong)i, "Fantasy Mod " + i, 100_000 + i, "fantasy")).ToList();
        var weapons = new List<WorkshopItem>
        {
            Item(200, "Big Guns", 500_000, "weapons"),
            Item(201, "Big Guns - Lasers", 300_000, "laser weapons"),
            Item(202, "Small Guns - Pistols", 300_000, "pistol weapons"), // its "Small Guns" is not in the results
        };
        var source = new FakeSource(new() { ["fantasy"] = fantasy, ["weapons"] = weapons }, fantasy.Concat(weapons));
        var request = new PackRequest { Topics = ["fantasy", "weapons"], Count = 10 };

        var plan = await PackPlanner.BuildAsync(request, Context, source);

        Assert.Equal(10, plan.Picks.Count);
        Assert.Contains(plan.Picks, p => p.Item.Id == 200);
        Assert.Contains(plan.Picks, p => p.Item.Id == 201); // after Big Guns
        Assert.Contains(plan.Picks, p => p.Item.Id == 202);
        Assert.Equal(7, plan.Picks.Count(p => p.Group == "fantasy"));
    }

    [Fact]
    public async Task Planner_ExcludedWordsAndReplacements()
    {
        var found = new List<WorkshopItem>
        {
            Item(1, "Dragon Lands", 90_000, "fantasy"),
            Item(2, "Elven Kingdom", 50_000, "fantasy"),
            Item(3, "Old Elves", 40_000, "fantasy"),
        };
        var maintained = Item(30, "Old Elves (Continued)", 20_000, "fantasy");
        var source = new FakeSource(new() { ["fantasy"] = found }, found.Append(maintained));
        var context = Context with { Replacement = id => id == 3 ? 30UL : null };
        var request = new PackRequest { Topics = ["fantasy"], ExcludeWords = ["dragon"] };

        var plan = await PackPlanner.BuildAsync(request, context, source);

        Assert.DoesNotContain(plan.Picks, p => p.Item.Id is 1 or 3);
        Assert.Equal("Old Elves", plan.Picks.Single(p => p.Item.Id == 30).ReplacesTitle);
    }

    [Fact]
    public void Brain_ReadsTheNetworksJsonAndDropsWhatIsNotAllowed()
    {
        const string answer = """
            <think>let me see</think>
            Sure! ```json
            {"follow_up": false, "topics": ["tribal", "hardcore", "space-magic", "performance"], "search": ["Cannibal", "desert", ""],
             "exclude_topics": ["magic"], "exclude_words": ["zombie"], "fps": true, "stable": "true", "no_dlc": false,
             "size": "small", "count": 400, "name": "Племя каннибалов", "reply": "Соберу племенную сборку."}
            ```
            """;
        var brain = AssistantBrain.Parse(answer, null);
        Assert.NotNull(brain);
        Assert.Equal(["tribal", "hardcore"], brain.Request.Topics); // unknown ids and "performance" are dropped
        Assert.Equal(["cannibal", "desert"], brain.Request.Words);
        Assert.Equal(["magic"], brain.Request.ExcludeTopics);
        Assert.Equal(["zombie"], brain.Request.ExcludeWords);
        Assert.False(brain.Request.Fps); // "fast" and "stable" are the word list's job
        Assert.Equal(PackSize.Small, brain.Request.Size);
        Assert.Equal(150, brain.Request.Count);
        Assert.False(brain.IsFollowUp);
        Assert.Equal("Племя каннибалов", brain.Name);

        Assert.Null(AssistantBrain.Parse("I can't help with that.", null));
        // Given the current pack, the answer is a change to it.
        Assert.False(AssistantBrain.Parse("""{"topics": ["fantasy"]}""", null)!.IsFollowUp);
        Assert.True(AssistantBrain.Parse("""{"topics": ["fantasy"]}""", new PackRequest { Topics = ["scifi"] })!.IsFollowUp);
    }

    [Fact]
    public void Brain_CombinesWithTheWordList()
    {
        var text = "сборка по мотивам Ведьмака для слабого ноутбука, без зомби";
        var words = RequestParser.Parse(text);
        var brain = new PackRequest { Topics = ["fantasy", "magic"], Words = ["witchcraft", "fps", "shooting", "fantasy setting"] };

        var r = AssistantBrain.Combine(brain, words);

        Assert.Equal(["fantasy", "magic"], r.Topics);
        Assert.Contains("witcher", r.Words);           // the word list knows the name
        Assert.Contains("witchcraft", r.Words);
        Assert.DoesNotContain("fps", r.Words);         // not a search
        Assert.DoesNotContain("shooting", r.Words);
        Assert.DoesNotContain("fantasy setting", r.Words); // the theme covers it
        Assert.Contains("zombie", r.ExcludeWords);
        Assert.True(r.Fps);

        // "добавь побольше животных" is about animals, not a bigger pack.
        Assert.Equal(PackSize.Normal, RequestParser.Parse("добавь побольше животных", new PackRequest { Topics = ["fantasy"] }).Request.Size);
    }

    [Fact]
    public void Brain_PromptCarriesTheCurrentRequest()
    {
        var current = new PackRequest { Topics = ["fantasy"], ExcludeWords = ["dragon"], Fps = true };
        var prompt = AssistantBrain.UserPrompt("добавь оружие", current);
        Assert.Contains("\"topics\":[\"fantasy\"]", prompt);
        Assert.Contains("\"exclude_words\":[\"dragon\"]", prompt);
        Assert.Contains("добавь оружие", prompt);
        Assert.Contains("- weapons:", AssistantBrain.SystemPrompt);
        Assert.DoesNotContain("- performance:", AssistantBrain.SystemPrompt);
    }
}
