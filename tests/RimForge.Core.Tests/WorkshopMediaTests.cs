using RimForge.Steam.Workshop;

namespace RimForge.Core.Tests;

public class WorkshopMediaTests
{
    [Fact]
    public void Media_ScreenshotsInThePagesOrder_AndVideos()
    {
        const string html = """
            <div class="highlight_strip_item highlight_movie" id="thumb_movie_dQw4w9WgXcQ"></div>
            <div class="highlight_strip_item highlight_strip_screenshot" id="thumb_screenshot_222" >
            <div class="highlight_strip_item highlight_strip_screenshot" id="thumb_screenshot_111" >
            <script>
            rgScreenshotURLs = {
                '111': 'https://images.steamusercontent.com/ugc/1/AAA/?imw=637&imh=358&ima=fit',
                '222': 'https://images.steamusercontent.com/ugc/2/BBB/?imw=637&imh=358&ima=fit',
            };
            var data = "{\"youtubevideoid\":null,\"title\":\"x\"}";
            </script>
            """;
        var media = WorkshopWeb.ParseMedia(html);
        Assert.Equal(["https://images.steamusercontent.com/ugc/2/BBB/", "https://images.steamusercontent.com/ugc/1/AAA/"], media.Images);
        Assert.Equal(["dQw4w9WgXcQ"], media.Videos);
        Assert.Empty(WorkshopWeb.ParseMedia("<html>nothing</html>").Images);
    }

    [Theory]
    [InlineData("<div>3,310 entries matching filters</div>", 3310)]
    [InlineData("<div class=\"x\">11.816 entries matching filters</div>", 11816)]
    [InlineData("<div>no count</div>", -1)]
    public void CollectionsTotal_IsReadWhateverTheGrouping(string html, int expected) =>
        Assert.Equal(expected, WorkshopWeb.ParseTotal(html));
}
