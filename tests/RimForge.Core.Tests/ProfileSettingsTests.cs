using RimForge.Core.Profiles;

namespace RimForge.Core.Tests;

public sealed class ProfileSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-pset-" + Guid.NewGuid().ToString("N"));
    private string Config => Path.Combine(_root, "game", "Config");

    public ProfileSettingsTests() => Directory.CreateDirectory(Config);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private ProfileSettings Store() => new(Path.Combine(_root, "game"), Path.Combine(_root, "profiles"), Path.Combine(_root, "backups"));

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(Config, name), text);
    private string? Read(string name) => File.Exists(Path.Combine(Config, name)) ? File.ReadAllText(Path.Combine(Config, name)) : null;

    [Fact]
    public void EachModpack_GetsBackItsOwnSettings()
    {
        var store = Store();
        Write("Mod_1_A.xml", "fantasy");
        Write("ModsConfig.xml", "list");

        // Fantasy → Space: Space has no settings of its own yet, so the game keeps what it has.
        Assert.False(store.Switch("fantasy", "space"));
        Assert.Equal("fantasy", Read("Mod_1_A.xml"));

        // In Space the player changes a setting and configures a new mod.
        Write("Mod_1_A.xml", "space");
        Write("Mod_2_B.xml", "space only");

        // Back to Fantasy: its settings return, the mod it did not have goes back to defaults.
        Assert.True(store.Switch("space", "fantasy"));
        Assert.Equal("fantasy", Read("Mod_1_A.xml"));
        Assert.Null(Read("Mod_2_B.xml"));
        Assert.Equal("list", Read("ModsConfig.xml")); // only mod settings are touched

        // And to Space again.
        Assert.True(store.Switch("fantasy", "space"));
        Assert.Equal("space", Read("Mod_1_A.xml"));
        Assert.Equal("space only", Read("Mod_2_B.xml"));
    }

    [Fact]
    public void SettingsNobodyRemembers_GoToABackup()
    {
        var store = Store();
        Write("Mod_1_A.xml", "mine");
        store.Save("pack");
        Write("Mod_1_A.xml", "changed outside any modpack");

        Assert.True(store.Restore("pack", backUpCurrent: true));

        Assert.Equal("mine", Read("Mod_1_A.xml"));
        var backup = Directory.GetDirectories(Path.Combine(_root, "backups", "mod-settings")).Single();
        Assert.Equal("changed outside any modpack", File.ReadAllText(Path.Combine(backup, "Mod_1_A.xml")));
    }

    [Fact]
    public void ForgottenModpack_HasNoSettings()
    {
        var store = Store();
        Write("Mod_1_A.xml", "x");
        store.Save("pack");
        Assert.True(store.HasCopy("pack"));
        store.Forget("pack");
        Assert.False(store.HasCopy("pack"));
        Assert.False(store.Restore("pack", false));
    }
}
