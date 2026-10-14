using RimForge.Core.Paths;

namespace RimForge.Core.Tests;

public class GamePathsTests
{
    [Fact]
    public void ParsesSteamLibraryFolders()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"    "g:\steam"  "label" "" }
                "1" { "path"    "E:\SteamLibrary" }
            }
            """;
        Assert.Equal([@"g:\steam", @"E:\SteamLibrary"], GamePathDetector.ParseLibraryFolders(vdf));
    }

    [Theory]
    [InlineData("1.6.4871 rev590", "1.6")]
    [InlineData("1.5.4409 rev1118\n", "1.5")]
    [InlineData("garbage", null)]
    public void ParsesGameVersion(string text, string? expected) =>
        Assert.Equal(expected, GamePaths.ParseMajorMinor(text));

    [Fact]
    public void FoldersChosenByHand_ForgiveOneLevelTooHigh()
    {
        var root = Path.Combine(Path.GetTempPath(), "rf-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = Path.Combine(root, "steamapps", "common", "RimWorld");
            Directory.CreateDirectory(Path.Combine(game, "Data", "Core"));
            File.WriteAllText(Path.Combine(game, "Version.txt"), "1.6.4871 rev590");
            var workshop = Path.Combine(root, "steamapps", "workshop", "content", "294100");
            Directory.CreateDirectory(Path.Combine(workshop, "123", "About"));
            File.WriteAllText(Path.Combine(workshop, "123", "About", "About.xml"), "<ModMetaData/>");
            var mods = Path.Combine(root, "MyMods");
            Directory.CreateDirectory(Path.Combine(mods, "a", "About"));
            File.WriteAllText(Path.Combine(mods, "a", "About", "About.xml"), "<ModMetaData/>");
            var data = Path.Combine(root, "Ludeon Studios", "RimWorld by Ludeon Studios");
            Directory.CreateDirectory(Path.Combine(data, "Config"));

            Assert.Equal(game, GamePathDetector.ResolveGameDir(game));
            Assert.Equal(game, GamePathDetector.ResolveGameDir(Path.Combine(root, "steamapps", "common")));
            Assert.Equal(game, GamePathDetector.ResolveGameDir(root));
            Assert.Null(GamePathDetector.ResolveGameDir(mods));

            Assert.Equal(workshop, GamePathDetector.ResolveWorkshopDir(workshop));
            Assert.Equal(workshop, GamePathDetector.ResolveWorkshopDir(root));
            Assert.Equal(workshop, GamePathDetector.ResolveWorkshopDir(Path.Combine(root, "steamapps", "workshop")));
            Assert.Equal(mods, GamePathDetector.ResolveWorkshopDir(mods));
            Assert.Null(GamePathDetector.ResolveWorkshopDir(Path.Combine(game, "Data")));

            Assert.Equal(data, GamePathDetector.ResolveConfigDir(data));
            Assert.Equal(data, GamePathDetector.ResolveConfigDir(Path.Combine(root, "Ludeon Studios")));
            Assert.Null(GamePathDetector.ResolveConfigDir(game));

            Assert.True(GamePathDetector.SamePath(game + Path.DirectorySeparatorChar, game.ToUpperInvariant()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
