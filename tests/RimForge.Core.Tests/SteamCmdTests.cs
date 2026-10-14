using RimForge.Core.Library;

namespace RimForge.Core.Tests;

public sealed class SteamCmdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rimforge-steamcmd-" + Guid.NewGuid().ToString("N"));

    public SteamCmdTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // What "app_info_print 294100" prints (shortened).
    private const string AppInfo = """
        Connecting anonymously to Steam Public...OK
        Waiting for user info...OK
        AppID : 294100, change number : 39790308/39790308, last change : Wed Oct 14 18:47:07 2026
        "294100"
        {
        	"common" { "name" "RimWorld" }
        	"depots"
        	{
        		"workshopdepot"		"294100"
        		"294101"
        		{
        			"config" { "oslist" "windows" "osarch" "32" }
        			"manifests"
        			{
        				"public" { "gid" "1" "size" "567092438" "download" "1" }
        				"version-1.5" { "gid" "2" "size" "490581334" "download" "1" }
        			}
        		}
        		"294104"
        		{
        			"config" { "oslist" "windows" "osarch" "64" }
        			"manifests"
        			{
        				"public" { "gid" "3" "size" "577318615" }
        				"unstable" { "gid" "4" "size" "600000000" }
        			}
        		}
        		"294105"
        		{
        			"config" { "oslist" "macos" }
        			"manifests" { "public" { "gid" "5" "size" "999999999" } }
        		}
        		"294106"
        		{
        			"dlcappid"		"1149640"
        			"manifests" { "public" { "gid" "6" "size" "57121561" } "unstable" { "gid" "7" "size" "60000000" } }
        		}
        		"branches"
        		{
        			"public" { "buildid" "23969874" "timeupdated" "1782839555" }
        			"unstable" { "buildid" "25915543" "description" "Public testing" "timeupdated" "1791918096" }
        			"alpha17" { "buildid" "3080643" "description" "Old version" "timeupdated" "1535701362" }
        			"version-1.5" { "buildid" "1" "description" "Old version" "timeupdated" "1751920000" }
        			"version-1.4.3901" { "buildid" "2" "description" "Previous 1.4 version" "timeupdated" "1712659355" }
        			"secret" { "buildid" "3" "pwdrequired" "1" }
        		}
        	}
        }
        """;

    [Fact]
    public void Branches_AreReadWithSizes()
    {
        var branches = SteamCmd.ParseBranches(AppInfo);
        Assert.Equal(["public", "unstable", "alpha17", "version-1.5", "version-1.4.3901", "secret"], branches.Select(b => b.Name));

        var current = branches[0];
        Assert.True(current.IsCurrent);
        Assert.Equal(577318615, current.Bytes);              // the 64-bit Windows depot only
        Assert.Equal(57121561, current.Dlc["1149640"]);

        var testing = branches[1];
        Assert.True(testing.IsTesting);
        Assert.Equal("Public testing", testing.Description);
        Assert.Equal(600000000, testing.Bytes);

        Assert.Equal("Alpha 17", branches[2].Early);
        Assert.Equal("1.5", branches[3].Version);
        Assert.Equal(490581334, branches[3].Bytes);           // no 64-bit build: the 32-bit one
        Assert.Equal("1.4.3901", branches[4].Version);
        Assert.True(branches[5].Locked);
        Assert.Equal(new DateTime(2025, 7, 7, 20, 26, 40, DateTimeKind.Utc), branches[3].UpdatedUtc);
    }

    [Fact]
    public void Branches_SurviveJson()
    {
        var branches = SteamCmd.ParseBranches(AppInfo);
        var back = System.Text.Json.JsonSerializer.Deserialize<List<GameBranch>>(System.Text.Json.JsonSerializer.Serialize(branches))!;
        Assert.Equal(branches.Select(b => (b.Name, b.Bytes, b.UpdatedUtc, b.Locked, b.Version)), back.Select(b => (b.Name, b.Bytes, b.UpdatedUtc, b.Locked, b.Version)));
        Assert.Equal(57121561, back[0].Dlc["1149640"]);
    }

    [Fact]
    public void DownloadResult_IsRead()
    {
        Assert.Null(SteamCmd.ReadDownload(" Update state (0x61) downloading, progress: 54.58 (37039920 / 67863136)\nSuccess! App '294100' fully installed.\n"));
        Assert.Equal(SteamCmd.LoginNeeded, SteamCmd.ReadDownload("Cached credentials not found.\n\npassword: \nLogging in user 'x' to Steam Public...ERROR (Invalid Password)\n"));
        Assert.Equal("ERROR! Failed to install app '294100' (No subscription)",
            SteamCmd.ReadDownload("Logging in user 'x' to Steam Public...OK\nERROR! Failed to install app '294100' (No subscription)\n"));
        Assert.NotNull(SteamCmd.ReadDownload("Loading Steam API...OK\n"));
    }

    [Fact]
    public void SavedLogin_IsFoundInSteamCmdConfig()
    {
        var tool = new SteamCmd(_dir);
        Assert.False(tool.HasSavedLogin("player"));

        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        File.WriteAllText(Path.Combine(_dir, "config", "config.vdf"), """
            "InstallConfigStore"
            {
            	"Software" { "Valve" { "Steam"
            	{
            		"Accounts" { "player" { "SteamID" "76561198000000000" } }
            		"ConnectCache" { "2f1e0ab01" "0100000a..." }
            	} } }
            }
            """);
        Assert.True(tool.HasSavedLogin("player"));
        Assert.True(tool.HasSavedLogin("PLAYER"));
        Assert.False(tool.HasSavedLogin("someone"));
    }

    [Fact]
    public void LastSteamAccount_IsTheMostRecentOne()
    {
        var steam = Path.Combine(_dir, "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        File.WriteAllText(Path.Combine(steam, "config", "loginusers.vdf"), """
            "users"
            {
            	"76561198000000001" { "AccountName" "old_one" "MostRecent" "0" "Timestamp" "100" }
            	"76561198000000002" { "AccountName" "main_one" "MostRecent" "1" "Timestamp" "50" }
            }
            """);
        Assert.Equal("main_one", SteamCmd.LastSteamAccount(steam));
        Assert.Null(SteamCmd.LastSteamAccount(Path.Combine(_dir, "nowhere")));
    }

    [Fact]
    public void InstalledDlc_ComesFromTheAppManifest()
    {
        var game = Path.Combine(_dir, "steamapps", "common", "RimWorld");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(_dir, "steamapps", "appmanifest_294100.acf"), """
            "AppState"
            {
            	"appid"		"294100"
            	"InstalledDepots"
            	{
            		"294104" { "manifest" "1" "size" "2" }
            		"1149644" { "manifest" "3" "size" "4" "dlcappid" "1149640" }
            		"294106" { "manifest" "5" "size" "6" "dlcappid" "1392840" }
            	}
            }
            """);
        Assert.Equal(["1149640", "1392840"], SteamCmd.InstalledDlc(game).Order());
    }

    [Fact]
    public void Adopt_TurnsADownloadIntoACopy()
    {
        var library = new GameLibrary(Path.Combine(_dir, "Library"));
        var download = library.DownloadDir("version-1.5");
        Directory.CreateDirectory(Path.Combine(download, "Data", "Core"));
        Directory.CreateDirectory(Path.Combine(download, "steamapps", "downloading"));
        File.WriteAllText(Path.Combine(download, "Version.txt"), "1.5.4409 rev1118");
        var oldData = Path.Combine(_dir, "OldData");
        Directory.CreateDirectory(Path.Combine(oldData, "Config"));
        File.WriteAllText(Path.Combine(oldData, "Config", "Prefs.xml"), "<PrefsData><langFolderName>Russian</langFolderName></PrefsData>");

        var copy = library.Adopt(download, "version-1.5", oldData);

        Assert.Equal("1.5.4409", copy.ShortVersion);
        Assert.Equal("version-1.5", copy.Branch);
        Assert.False(Directory.Exists(download));
        Assert.True(Directory.Exists(Path.Combine(copy.GameDir, "Mods")));
        Assert.False(Directory.Exists(Path.Combine(copy.GameDir, "steamapps")));
        Assert.True(File.Exists(Path.Combine(copy.DataDir, "Config", "Prefs.xml")));
        Assert.Equal("version-1.5", Assert.Single(library.Load()).Branch);

        // The same version twice is refused.
        Directory.CreateDirectory(download);
        File.WriteAllText(Path.Combine(download, "Version.txt"), "1.5.4409 rev1118");
        Assert.Throws<InvalidOperationException>(() => library.Adopt(download, "version-1.5", null));
    }
}
