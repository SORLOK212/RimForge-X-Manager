using RimForge.Steam.Workshop;

namespace RimForge.Core.Tests;

public class WorkshopTests
{
    [Fact]
    public void Manifest_ReadsInstalledItemsAndPendingUpdates()
    {
        const string acf = """
            "AppWorkshop"
            {
            	"appid"		"294100"
            	"WorkshopItemsInstalled"
            	{
            		"725747149"
            		{
            			"size"		"2299977"
            			"timeupdated"		"1752965580"
            			"manifest"		"500416045061872535"
            		}
            		"818773962"
            		{
            			"size"		"2463770"
            			"timeupdated"		"1752434318"
            		}
            	}
            	"WorkshopItemDetails"
            	{
            		"725747149"
            		{
            			"timeupdated"		"1752965580"
            			"latest_timeupdated"		"1752965580"
            		}
            		"818773962"
            		{
            			"timeupdated"		"1752434318"
            			"latest_timeupdated"		"1790000000"
            		}
            	}
            }
            """;

        var items = WorkshopManifest.Parse(acf);

        Assert.Equal(2, items.Count);
        Assert.False(items[725747149].NeedsUpdate);
        Assert.Equal(2299977, items[725747149].SizeBytes);
        Assert.True(items[818773962].NeedsUpdate);
        Assert.Equal(new DateTime(2026, 9, 21, 14, 13, 20, DateTimeKind.Utc), items[818773962].LatestUpdatedUtc);
    }

    [Fact]
    public void Manifest_IsFoundNextToTheContentFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf-ws-" + Guid.NewGuid().ToString("N")[..8]);
        var content = Path.Combine(root, "steamapps", "workshop", "content", "294100");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(root, "steamapps", "workshop", "appworkshop_294100.acf"), "\"AppWorkshop\" { }");
        try
        {
            Assert.Equal(Path.Combine(root, "steamapps", "workshop", "appworkshop_294100.acf"), WorkshopManifest.FileFor(content));
            Assert.Equal(Path.Combine(root, "steamapps", "workshop", "appworkshop_294100.acf"), WorkshopManifest.FileFor(content + "\\"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WebDetails_ParsesItemsAndMarksMissingOnes()
    {
        const string json = """
            {"response":{"result":1,"resultcount":2,"publishedfiledetails":[
              {"publishedfileid":"2009463077","result":1,"creator":"76561197973010050","file_size":"8296511",
               "preview_url":"https://images.steamusercontent.com/ugc/1/ABC/","title":"Harmony","description":"Patch library",
               "time_created":1582913607,"time_updated":1791825433,"visibility":0,"banned":0,"subscriptions":3327631,
               "tags":[{"tag":"Mod"},{"tag":"1.6"},{"tag":"1.5"}]},
              {"publishedfileid":"1","result":9}]}}
            """;

        var items = WorkshopWeb.ParseDetails(json).ToList();

        var harmony = items[0];
        Assert.Equal(2009463077UL, harmony.Id);
        Assert.Equal("Harmony", harmony.Title);
        Assert.Equal(3327631, harmony.Subscribers);
        Assert.Equal(8296511, harmony.SizeBytes);
        Assert.Equal(["1.5", "1.6"], harmony.Versions);
        Assert.False(harmony.Removed);
        Assert.True(items[1].Removed);
    }

    [Fact]
    public void Collection_KeepsOrderAndSpotsNestedCollections()
    {
        const string json = """
            {"response":{"result":1,"resultcount":1,"collectiondetails":[{"publishedfileid":"500","result":1,"children":[
              {"publishedfileid":"30","sortorder":2,"filetype":0},
              {"publishedfileid":"10","sortorder":0,"filetype":0},
              {"publishedfileid":"77","sortorder":1,"filetype":2}]}]}}
            """;

        Assert.Equal([(10UL, false), (77UL, true), (30UL, false)], WorkshopWeb.ParseCollection(json).ToList());
    }

    [Fact]
    public void BrowsePage_GivesIdsInOrderWithoutRepeats()
    {
        const string html = """
            <a href="https://steamcommunity.com/sharedfiles/filedetails/?id=3816083162" class="x"><img></a>
            <a href="https://steamcommunity.com/sharedfiles/filedetails/?id=3816083162&searchtext=">Title</a>
            <a href="https://steamcommunity.com/sharedfiles/filedetails/?id=42">Other</a>
            """;

        Assert.Equal([3816083162UL, 42UL], WorkshopWeb.ParseBrowseIds(html));
    }

    [Fact]
    public void Changelog_ReadsDatesAndText()
    {
        const string html = """
            <div class="changelog headline">Update: 12 Oct @ 10:17am</div>
            <p id="1791825433">v3.0.0<br><br>Adds RimWorld 1.7 support &amp; more.</p>
            <div class="changelog headline">Update: 3 Sep, 2025</div>
            <p id="1756914561">v2.4.2 small fix</p>
            """;

        var entries = WorkshopWeb.ParseChangelog(html);

        Assert.Equal(2, entries.Count);
        Assert.Equal("v3.0.0\n\nAdds RimWorld 1.7 support & more.", entries[0].Text);
        Assert.Equal(new DateTime(2025, 9, 3, 15, 49, 21, DateTimeKind.Utc), entries[1].DateUtc);
    }

    [Fact]
    public void BbCode_BecomesReadableText()
    {
        const string bb = "[h1]What is this?[/h1]\r\n[b]Safe[/b] to add. See [url=https://x.y/z]here[/url].\r\n[img]https://i/a.png[/img]\r\n[list]\r\n[*]One\r\n[*]Two\r\n[/list]\r\n\r\n\r\n\r\nEnd &amp; bye";

        Assert.Equal("What is this?\n\nSafe to add. See here.\n\n• One\n• Two\n\nEnd & bye", BbCode.ToPlainText(bb));
    }

    [Theory]
    [InlineData("Bench\n[img=https://x.com/a.webp]\nWorks.")]
    [InlineData("Bench\n[img src=\"https://x.com/a.webp\"][/img]\nWorks.")]
    [InlineData("Bench\n[video mp4=\"https://x.com/a.mp4\" autoplay=\"true\"][/video]\nWorks.")]
    public void BbCode_DropsImagesInAnyForm(string bb) => Assert.Equal("Bench\n\nWorks.", BbCode.ToPlainText(bb));

    [Fact]
    public void AboutXmlRichText_IsRemovedToo() =>
        Assert.Equal("Big news: works.", BbCode.ToPlainText("<size=20><b>Big news</b></size>: <color=#ff0000>works</color>."));

    [Theory]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077", 2009463077UL)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?l=russian&id=123456&searchtext=", 123456UL)]
    [InlineData("steam://url/CommunityFilePage/2009463077", 2009463077UL)]
    [InlineData(" 2009463077 ", 2009463077UL)]
    [InlineData("https://steamcommunity.com/workshop/filedetails/?id=818773962", 818773962UL)]
    public void WorkshopIds_AreFoundInLinks(string text, ulong expected) =>
        Assert.Equal(expected, WorkshopIds.Parse(text));

    [Theory]
    [InlineData("https://example.com/mod")]
    [InlineData("")]
    [InlineData(null)]
    public void WorkshopIds_RejectNonsense(string? text) => Assert.Null(WorkshopIds.Parse(text));

    [Theory]
    [InlineData("Fantasy Biotech Ru", true)]
    [InlineData("Vanilla Expanded - Russian translation", true)]
    [InlineData("Medieval Overhaul (RU)", true)]
    [InlineData("[ZAV] Fantasy Metals", false)]
    [InlineData("Rimworld Run Rules", false)]
    [InlineData("Мед. фэнтези — русский перевод", true)]
    public void Translations_AreRecognised(string title, bool expected) =>
        Assert.Equal(expected, WorkshopItem.LooksLikeTranslation(title));

    private static WorkshopItem Item(ulong id, long subs, string title = "Mod", params string[] tags) =>
        new() { Id = id, Title = title, Subscribers = subs, Tags = tags };

    [Fact]
    public void ThemeMerge_PrefersModsFoundOftenAndLiked_SkipsTranslations()
    {
        var magic = new List<WorkshopItem> { Item(1, 1000), Item(2, 500_000), Item(3, 900, "Magic Ru") };
        var dragons = new List<WorkshopItem> { Item(1, 1000), Item(4, 2_000_000) };
        var elves = new List<WorkshopItem> { Item(1, 1000), Item(5, 10, "Elves", "Scenario") };

        var merged = WorkshopThemes.Merge([magic, dragons, elves]);

        Assert.Equal([4UL, 2UL, 1UL], merged.Select(i => i.Id));
    }

    [Fact]
    public void ThemeMerge_ModFoundByEverySearchBeatsSlightlyMorePopularOne()
    {
        var a = new List<WorkshopItem> { Item(1, 50_000), Item(2, 80_000) };
        var b = new List<WorkshopItem> { Item(1, 50_000) };
        var c = new List<WorkshopItem> { Item(1, 50_000) };

        Assert.Equal(1UL, WorkshopThemes.Merge([a, b, c])[0].Id);
    }
}
