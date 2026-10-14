using System.Text.RegularExpressions;
using Microsoft.Win32;
using RimForge.Core.Models;

namespace RimForge.Core.Paths;

public enum GameStore { Unknown, Steam, Gog }

/// <summary>All folders RimForge needs. Any of them may be null if not found yet.</summary>
public sealed record GamePaths
{
    public string? GameDir { get; init; }
    public string? WorkshopDir { get; init; }
    public string? ConfigDir { get; init; }
    public GameStore Store { get; init; }

    public string? DataDir => GameDir is null ? null : Path.Combine(GameDir, "Data");
    public string? LocalModsDir => GameDir is null ? null : Path.Combine(GameDir, "Mods");
    public string? ModsConfigFile => ConfigDir is null ? null : Path.Combine(ConfigDir, "Config", "ModsConfig.xml");
    public string? SavesDir => ConfigDir is null ? null : Path.Combine(ConfigDir, "Saves");
    public string? PlayerLogFile => ConfigDir is null ? null : Path.Combine(ConfigDir, "Player.log");
    /// <summary>The game's executable: RimWorldWin64.exe, or RimWorld.exe in builds on the new engine (1.7).</summary>
    public string? GameExe => GameDir is null ? null
        : GamePathDetector.ExeNames.Select(n => Path.Combine(GameDir, n)).FirstOrDefault(File.Exists) ?? Path.Combine(GameDir, GamePathDetector.ExeNames[0]);

    public bool IsComplete => GameDir is not null && ConfigDir is not null;

    /// <summary>Reads Version.txt ("1.6.4871 rev590") and returns "1.6", or null.</summary>
    public string? ReadGameVersion()
    {
        var file = GameDir is null ? null : Path.Combine(GameDir, "Version.txt");
        if (file is null || !File.Exists(file)) return null;
        return ParseMajorMinor(File.ReadAllText(file));
    }

    public string? ReadFullGameVersion()
    {
        var file = GameDir is null ? null : Path.Combine(GameDir, "Version.txt");
        return file is not null && File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    public static string? ParseMajorMinor(string versionText)
    {
        var m = Regex.Match(versionText, @"^\s*(\d+)\.(\d+)");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : null;
    }
}

/// <summary>Finds the RimWorld install (Steam or GOG), the Workshop folder and the game's config folder.</summary>
public static class GamePathDetector
{
    public static GamePaths Detect()
    {
        var config = DetectConfigDir();

        foreach (var library in SteamLibraries())
        {
            var game = Path.Combine(library, "steamapps", "common", "RimWorld");
            if (!IsGameDir(game)) continue;
            var workshop = Path.Combine(library, "steamapps", "workshop", "content", KnownMods.RimWorldAppId.ToString());
            return new GamePaths
            {
                GameDir = game,
                WorkshopDir = Directory.Exists(workshop) ? workshop : null,
                ConfigDir = config,
                Store = GameStore.Steam,
            };
        }

        if (DetectGogGameDir() is { } gog)
            return new GamePaths { GameDir = gog, ConfigDir = config, Store = GameStore.Gog };

        return new GamePaths { ConfigDir = config };
    }

    /// <summary>Names of the game's executable, newest engine last.</summary>
    public static readonly string[] ExeNames = ["RimWorldWin64.exe", "RimWorld.exe"];

    /// <summary>A folder is a RimWorld install if it has Data\Core and the game executable or Version.txt.</summary>
    public static bool IsGameDir(string dir) =>
        Directory.Exists(Path.Combine(dir, "Data", "Core")) &&
        (ExeNames.Any(n => File.Exists(Path.Combine(dir, n))) || File.Exists(Path.Combine(dir, "Version.txt")));

    // ── Folders the player points at by hand: forgive picking one level too high ──

    /// <summary>The game folder the player meant: the folder itself, or a "RimWorld" inside it (e.g. steamapps\common).</summary>
    public static string? ResolveGameDir(string picked) =>
        Candidates(picked, "RimWorld", Path.Combine("common", "RimWorld"), Path.Combine("steamapps", "common", "RimWorld"))
            .FirstOrDefault(IsGameDir);

    /// <summary>
    /// The Workshop mods folder the player meant (steamapps\workshop\content\294100): the folder itself if it holds
    /// mods or is that folder, or the 294100 folder below a Steam library / steamapps / workshop folder they picked.
    /// </summary>
    public static string? ResolveWorkshopDir(string picked)
    {
        var app = KnownMods.RimWorldAppId.ToString();
        var below = Candidates(picked, app, Path.Combine("content", app), Path.Combine("workshop", "content", app),
            Path.Combine("steamapps", "workshop", "content", app)).Skip(1);
        return below.FirstOrDefault(Directory.Exists)
               ?? (Directory.Exists(picked) && (Path.GetFileName(Path.TrimEndingDirectorySeparator(picked)) == app || HasMods(picked)) ? picked : null);
    }

    /// <summary>The game's data folder (settings, saves) the player meant: it holds Config or Saves.</summary>
    public static string? ResolveConfigDir(string picked) =>
        Candidates(picked, "RimWorld by Ludeon Studios", Path.Combine("Ludeon Studios", "RimWorld by Ludeon Studios"))
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Config")) || Directory.Exists(Path.Combine(d, "Saves")));

    /// <summary>Some mod folders (with About\About.xml) right inside.</summary>
    public static bool HasMods(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Take(200)
                .Any(d => File.Exists(Path.Combine(d, "About", "About.xml")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Candidates(string picked, params string[] below)
    {
        yield return picked;
        foreach (var b in below) yield return Path.Combine(picked, b);
    }

    public static string? DetectConfigDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios");
        return Directory.Exists(dir) ? dir : null;
    }

    public static IEnumerable<string> SteamLibraries()
    {
        var steam = SteamInstallDir();
        if (steam is null) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(Path.GetFullPath(steam))) yield return steam;

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (var lib in ParseLibraryFolders(File.ReadAllText(vdf)))
            if (Directory.Exists(lib) && seen.Add(Path.GetFullPath(lib)))
                yield return lib;
    }

    /// <summary>Extracts the "path" values from Steam's libraryfolders.vdf.</summary>
    public static IEnumerable<string> ParseLibraryFolders(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    public static string? SteamInstallDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                   ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return path is not null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static string? DetectGogGameDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) return null;
        foreach (var name in games.GetSubKeyNames())
        {
            using var key = games.OpenSubKey(name);
            if (key?.GetValue("gameName") is string title && title.Contains("RimWorld", StringComparison.OrdinalIgnoreCase)
                && key.GetValue("path") is string path && IsGameDir(path))
                return path;
        }
        return null;
    }
}
