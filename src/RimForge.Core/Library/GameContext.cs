using RimForge.Core.Paths;
using RimForge.Core.Storage;

namespace RimForge.Core.Library;

/// <summary>
/// The game RimForge works with: the installed one (Steam/GOG, with the player's path overrides) or the saved copy
/// chosen in the library. The window and the AI server resolve it the same way.
/// </summary>
public sealed record GameContext(GamePaths Paths, GamePaths Installed, GameInstance? Copy)
{
    public static GameContext Resolve(AppSettings settings)
    {
        var detected = GamePathDetector.Detect();
        var installed = detected with
        {
            GameDir = settings.GameDir ?? detected.GameDir,
            WorkshopDir = settings.WorkshopDir ?? detected.WorkshopDir,
            ConfigDir = settings.ConfigDir ?? detected.ConfigDir,
        };
        if (settings.GameDir is not null && settings.GameDir != detected.GameDir)
            installed = installed with { Store = GameStore.Unknown };

        var copy = settings.ActiveGameId is { } id
            ? new GameLibrary(settings.LibraryDir ?? DefaultLibraryDir()).Load().FirstOrDefault(c => c.Id == id && Directory.Exists(c.GameDir))
            : null;
        return new GameContext(copy?.Paths ?? installed, installed, copy);
    }

    /// <summary>
    /// The installed RimForge keeps its library in %LOCALAPPDATA%\RimForge\Library. The portable build (and an older
    /// RimForge that already has a library next to itself) keeps it next to RimForge.exe; if that folder is read-only,
    /// in "RimForge Library" at the root of the same drive.
    /// </summary>
    public static string DefaultLibraryDir()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Library");
        if (!Storage.AppDataPaths.IsPortable && !File.Exists(Path.Combine(beside, "library.json")))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimForge", "Library");
        try
        {
            Directory.CreateDirectory(beside);
            var probe = Path.Combine(beside, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return beside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\", "RimForge Library");
        }
    }
}
