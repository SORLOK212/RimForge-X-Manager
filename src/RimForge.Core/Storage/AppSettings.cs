using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimForge.Core.Storage;

/// <summary>
/// Where RimForge keeps its own files: %APPDATA%\RimForge, or the "Data" folder next to RimForge.exe in the portable
/// build (a "portable.txt" file lies next to the exe).
/// </summary>
public static class AppDataPaths
{
    /// <summary>The portable build keeps everything (settings, modpacks, the game library) in its own folder.</summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    /// <summary>The RIMFORGE_DATA environment variable points it elsewhere (used for testing on a copy).</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("RIMFORGE_DATA") is { Length: > 0 } custom ? custom
        : IsPortable ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RimForge");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string BackupsDir => Path.Combine(Root, "backups");
    public static string ProfilesDir => Path.Combine(Root, "profiles");
}

/// <summary>User settings. Paths left null are auto-detected on every start.</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "ru";
    public string? GameDir { get; set; }
    public string? WorkshopDir { get; set; }
    public string? ConfigDir { get; set; }
    public bool ExpertMode { get; set; }
    public string? ActiveProfileId { get; set; }

    /// <summary>The RimForge library (copies of the game, frozen mods). Null: next to RimForge itself.</summary>
    public string? LibraryDir { get; set; }

    /// <summary>The saved copy of the game RimForge manages and launches; null = the installed game (Steam/GOG).</summary>
    public string? ActiveGameId { get; set; }

    /// <summary>When the player last looked through the Workshop update feed.</summary>
    public DateTime? WorkshopSeenUtc { get; set; }

    /// <summary>Translate mod descriptions into the interface language automatically.</summary>
    public bool AutoTranslate { get; set; } = true;

    /// <summary>Translate with the built-in model (no internet) instead of the online translator.</summary>
    public bool TranslateOffline { get; set; }

    /// <summary>Every modpack keeps its own mod settings (Config\Mod_*.xml), swapped in when it is switched to.</summary>
    public bool ProfileModSettings { get; set; } = true;

    /// <summary>When the online translator fails, use the built-in model (if it is downloaded).</summary>
    public bool TranslateFallback { get; set; } = true;

    /// <summary>AI assistants (any MCP client) may connect to RimForge.</summary>
    public bool AiEnabled { get; set; } = true;

    /// <summary>What an AI assistant may do: "read" (look only), "ask" (changes need the player's OK), "full".</summary>
    public string AiAccess { get; set; } = AiAccessModes.Ask;

    /// <summary>The language to find mod translations for ("Russian"); null: the game's language (or RimForge's, if the game is in English).</summary>
    public string? TranslationLanguage { get; set; }

    /// <summary>How the built-in assistant reads requests: "words" (word list), "model" (RimForge's own network), "server" (another one).</summary>
    public string AsstBrain { get; set; } = AsstBrains.Words;

    /// <summary>The other network: one of the presets ("ollama", "openrouter"…) or "custom".</summary>
    public string? AsstServerPreset { get; set; }
    public string? AsstServerUrl { get; set; }
    public string? AsstServerModel { get; set; }

    /// <summary>The service key, encrypted for this Windows user (DPAPI).</summary>
    public string? AsstServerKey { get; set; }

    /// <summary>Where new RimForge builds are announced (latest.json); null: the built-in address.</summary>
    public string? UpdateFeed { get; set; }

    /// <summary>A version the player chose to skip: not offered again.</summary>
    public string? UpdateSkipVersion { get; set; }
    /// <summary>The Steam account SteamCMD signs in with to download other versions of the game (no password is kept).</summary>
    public string? SteamCmdAccount { get; set; }

    /// <summary>Widths of the panels the player dragged (by panel name); a missing one has its usual width.</summary>
    public Dictionary<string, double> Layout { get; set; } = [];

    /// <summary>Where the window was and how big, to open it there again.</summary>
    public WindowPlace? Window { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load(string? file = null)
    {
        file ??= AppDataPaths.SettingsFile;
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string? file = null)
    {
        file ??= AppDataPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>The window's place on screen: position in screen pixels, size in the window's own units.</summary>
public sealed record WindowPlace(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>Values of <see cref="AppSettings.AiAccess"/>.</summary>
public static class AiAccessModes
{
    public const string Read = "read";
    public const string Ask = "ask";
    public const string Full = "full";
}

/// <summary>Values of <see cref="AppSettings.AsstBrain"/>.</summary>
public static class AsstBrains
{
    public const string Words = "words";
    public const string Model = "model";
    public const string Server = "server";
}
