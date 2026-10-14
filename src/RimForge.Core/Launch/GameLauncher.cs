using System.Diagnostics;
using RimForge.Core.Models;
using RimForge.Core.Paths;

namespace RimForge.Core.Launch;

public static class GameLauncher
{
    /// <summary>
    /// Starts RimWorld. Steam installs go through Steam itself ("steam.exe -applaunch 294100"):
    /// started directly, the game cannot initialise the Steam API and Workshop features break.
    /// Other installs (GOG, custom folders) start the executable directly.
    /// </summary>
    public static void Launch(GamePaths paths, params string[] args)
    {
        var start = BuildStartInfo(paths, GamePathDetector.SteamInstallDir(), args);
        Process.Start(start)?.Dispose();
    }

    public static ProcessStartInfo BuildStartInfo(GamePaths paths, string? steamDir, IReadOnlyList<string> args)
    {
        var steamExe = steamDir is null ? null : Path.Combine(steamDir, "steam.exe");
        if (IsSteamInstall(paths) && steamExe is not null && File.Exists(steamExe))
        {
            var viaSteam = new ProcessStartInfo(steamExe) { UseShellExecute = false };
            viaSteam.ArgumentList.Add("-applaunch");
            viaSteam.ArgumentList.Add(KnownMods.RimWorldAppId.ToString());
            foreach (var a in args) viaSteam.ArgumentList.Add(a);
            return viaSteam;
        }

        var exe = paths.GameExe;
        if (exe is null || !File.Exists(exe))
            throw new FileNotFoundException("RimWorld executable not found", exe);

        var direct = new ProcessStartInfo(exe) { WorkingDirectory = paths.GameDir!, UseShellExecute = false };
        foreach (var a in args) direct.ArgumentList.Add(a);
        return direct;
    }

    /// <summary>Detected as Steam, or a manually chosen folder that lives inside a Steam library.</summary>
    public static bool IsSteamInstall(GamePaths paths) =>
        paths.Store == GameStore.Steam ||
        (paths.GameDir?.Contains(Path.Combine("steamapps", "common"), StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Process names of the game (the new engine of 1.7 runs as "RimWorld").</summary>
    public static readonly string[] ProcessNames = ["RimWorldWin64", "RimWorld"];

    public static bool IsRunning() =>
        ProcessNames.Any(n => Process.GetProcessesByName(n).Length > 0);
}
