using RimForge.Core.Config;

namespace RimForge.Core.Tests;

public sealed class ModsConfigFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rimforge-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SaveThenLoad_RoundTrips_AndMakesBackup()
    {
        var file = Path.Combine(_dir, "ModsConfig.xml");
        File.WriteAllText(file, """
            <?xml version="1.0" encoding="utf-8"?>
            <ModsConfigData>
              <version>1.6.4871 rev591</version>
              <activeMods><li>brrainz.harmony</li><li>Ludeon.RimWorld</li></activeMods>
              <knownExpansions><li>ludeon.rimworld.royalty</li></knownExpansions>
            </ModsConfigData>
            """);

        var config = ModsConfigFile.Load(file);
        Assert.Equal(["brrainz.harmony", "ludeon.rimworld"], config.ActiveMods);

        config.ActiveMods.Add("some.mod");
        var backup = config.Save(file, Path.Combine(_dir, "backups"));

        Assert.NotNull(backup);
        Assert.Equal(2, ModsConfigFile.Load(backup!).ActiveMods.Count);

        var reloaded = ModsConfigFile.Load(file);
        Assert.Equal("1.6.4871 rev591", reloaded.Version);
        Assert.Equal(["brrainz.harmony", "ludeon.rimworld", "some.mod"], reloaded.ActiveMods);
        Assert.Equal(["ludeon.rimworld.royalty"], reloaded.KnownExpansions);
    }

    [Fact]
    public void Version_FollowsTheGame_SoTheGameDoesNotResetTheList()
    {
        // Written by 1.6, then Steam switched the game to 1.7: RimWorld would throw the list away.
        var config = new ModsConfigFile { Version = "1.6.4871 rev591", ActiveMods = ["ludeon.rimworld", "some.mod"] };
        Assert.True(config.IsForGame("1.6.4900 rev600"));
        Assert.False(config.IsForGame("1.7.4976 rev1043"));

        Assert.True(config.StampVersion("1.7.4976 rev1043"));
        Assert.True(config.IsForGame("1.7.4976 rev1043"));
        Assert.False(config.StampVersion("1.7.4976 rev1043"));
        Assert.False(config.StampVersion(null));
        Assert.Equal("1.7.4976 rev1043", config.Version);
    }
}
