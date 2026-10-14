using RimForge.Core.Launch;
using RimForge.Core.Paths;

namespace RimForge.Core.Tests;

public sealed class GameLauncherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rimforge-launch-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void SteamInstall_LaunchesThroughSteam()
    {
        var steamExe = Touch("Steam", "steam.exe");
        var game = Path.GetDirectoryName(Touch("Steam", "steamapps", "common", "RimWorld", "RimWorldWin64.exe"))!;

        var start = GameLauncher.BuildStartInfo(new GamePaths { GameDir = game, Store = GameStore.Steam },
            Path.GetDirectoryName(steamExe), ["-quicktest"]);

        Assert.Equal(steamExe, start.FileName);
        Assert.Equal(["-applaunch", "294100", "-quicktest"], start.ArgumentList);
    }

    [Fact]
    public void GogInstall_LaunchesExeDirectly()
    {
        var exe = Touch("GOG", "RimWorld", "RimWorldWin64.exe");
        var game = Path.GetDirectoryName(exe)!;

        var start = GameLauncher.BuildStartInfo(new GamePaths { GameDir = game, Store = GameStore.Gog }, null, []);

        Assert.Equal(exe, start.FileName);
        Assert.Equal(game, start.WorkingDirectory);
    }

    [Fact]
    public void ManualFolderInsideSteamLibrary_CountsAsSteam() =>
        Assert.True(GameLauncher.IsSteamInstall(new GamePaths { GameDir = @"E:\SteamLibrary\steamapps\common\RimWorld" }));
}
