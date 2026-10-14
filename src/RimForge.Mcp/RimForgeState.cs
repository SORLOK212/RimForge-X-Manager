using RimForge.Core.Checks;
using RimForge.Core.Config;
using RimForge.Core.Content;
using RimForge.Core.Databases;
using RimForge.Core.Library;
using RimForge.Core.Models;
using RimForge.Core.Mods;
using RimForge.Core.Paths;
using RimForge.Core.Profiles;
using RimForge.Core.Sorting;
using RimForge.Core.Storage;

namespace RimForge.Mcp;

/// <summary>
/// Everything the AI tools read: the game, its mods, the load order and the modpacks. Built either by the RimForge
/// window (what the player sees, unsaved changes included) or straight from the files when the window is closed.
/// </summary>
public sealed class RimForgeState
{
    public required GamePaths Paths { get; init; }
    public string? GameVersion { get; init; }
    public string? FullGameVersion { get; init; }
    /// <summary>Set when a saved copy of the game from the RimForge library is chosen.</summary>
    public string? GameCopy { get; init; }
    public required IReadOnlyDictionary<string, ModInfo> Installed { get; init; }
    /// <summary>Active mods in load order.</summary>
    public required IReadOnlyList<string> Active { get; init; }
    /// <summary>Names and Workshop ids of mods that are in the list but not installed.</summary>
    public IReadOnlyDictionary<string, ProfileMod> Hints { get; init; } = new Dictionary<string, ProfileMod>();
    public required IReadOnlyList<Profile> Profiles { get; init; }
    public string? ActiveProfileId { get; init; }
    public CommunityRules Community { get; init; } = CommunityRules.Empty;
    public CheckExtras Extras { get; init; } = new();
    public Func<ModInfo, ModProfile?> Content { get; init; } = _ => null;
    public string Access { get; init; } = AiAccessModes.Ask;
    /// <summary>The window has changes the player has not saved yet.</summary>
    public bool HasUnsavedChanges { get; init; }
    /// <summary>The state came from the open RimForge window.</summary>
    public bool FromWindow { get; init; }

    public Profile? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);

    private RuleSet? _rules;
    public RuleSet Rules => _rules ??= RuleSet.Build(Active, Installed, Community, ActiveProfile?.Rules, ActiveProfile?.Pins);

    private IReadOnlyList<ModIssue>? _issues;

    /// <summary>The traffic light: the same checks the window shows.</summary>
    public IReadOnlyList<ModIssue> Issues => _issues ??= BuildIssues();

    private IReadOnlyList<ModIssue> BuildIssues()
    {
        var issues = ModListChecker.Check(Active, Installed, GameVersion, Rules, Extras);
        // Broken rules inside a contradiction can't be fixed by moving mods (a mistake in a mod's description).
        var contradictions = ModSorter.FindConflicts(Active, Rules).Select(c => c.Mods.ToHashSet()).ToList();
        return issues.Select(i => i.Rule is { } r && contradictions.Any(c => c.Contains(r.Before) && c.Contains(r.After))
            ? i with { Severity = IssueSeverity.Info, Kind = IssueKind.Contradiction }
            : i).ToList();
    }

    public string NameOf(string id) =>
        Installed.TryGetValue(id, out var m) ? (m.IsCore ? "RimWorld" : m.Name)
        : Hints.TryGetValue(id, out var h) && h.Name is { } n ? n
        : id;

    /// <summary>Finds an installed mod by package id (any case), "_steam" duplicate or Workshop id.</summary>
    public ModInfo? Find(string idOrWorkshopId)
    {
        var id = idOrWorkshopId.Trim().ToLowerInvariant();
        if (Installed.TryGetValue(id, out var mod)) return mod;
        if (id.StartsWith("workshop:")) id = id["workshop:".Length..];
        return ulong.TryParse(id, out var wid) ? Installed.Values.FirstOrDefault(m => m.WorkshopId == wid) : null;
    }
}

/// <summary>Builds <see cref="RimForgeState"/> from the files on disk, the way the window loads them. Cached for a short while.</summary>
public sealed class FileStateSource
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DatabaseAge = TimeSpan.FromDays(3);

    private RimForgeState? _cached;
    private DateTime _builtUtc;
    private DateTime _configWriteUtc;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<RimForgeState> GetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var settings = AppSettings.Load();
            var context = GameContext.Resolve(settings);
            var configFile = context.Paths.ModsConfigFile;
            var configWrite = configFile is not null && File.Exists(configFile) ? File.GetLastWriteTimeUtc(configFile) : default;
            if (_cached is not null && DateTime.UtcNow - _builtUtc < MaxAge && configWrite == _configWriteUtc
                && _cached.Paths == context.Paths && _cached.Access == settings.AiAccess && _cached.ActiveProfileId == settings.ActiveProfileId)
                return _cached;

            _cached = await Task.Run(() => BuildAsync(settings, context, ct), ct);
            _builtUtc = DateTime.UtcNow;
            _configWriteUtc = configWrite;
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<RimForgeState> BuildAsync(AppSettings settings, GameContext context, CancellationToken ct)
    {
        var paths = context.Paths;
        if (paths.GameDir is null || !GamePathDetector.IsGameDir(paths.GameDir))
            throw new InvalidOperationException("RimForge can't find the RimWorld folder. Open RimForge once and choose the game folder.");

        var version = paths.ReadGameVersion() ?? "1.6";
        var mods = ModScanner.Scan(paths, version);
        var installed = mods.GroupBy(m => m.PackageId).ToDictionary(g => g.Key, g => g.First());
        var duplicates = mods.GroupBy(m => m.PackageId).Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(m => $"\"{m.Name}\" ({Path.GetFileName(m.FolderPath)})").ToList());

        var config = paths.ModsConfigFile is { } file && File.Exists(file) ? ModsConfigFile.Load(file) : null;
        var active = config?.ActiveMods.Distinct().ToList() ?? [KnownMods.Core];
        var profiles = new ProfileStore(AppDataPaths.ProfilesDir).LoadAll();
        var activeProfile = profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId);
        var hints = activeProfile?.Mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First()) ?? new Dictionary<string, ProfileMod>();

        var cache = Path.Combine(AppDataPaths.Root, "cache");
        var community = await Safe(() => CommunityRules.LoadAsync(Path.Combine(cache, "communityRules.json"), DatabaseAge), CommunityRules.Empty);
        var replacements = await Safe(() => ModReplacements.LoadAsync(Path.Combine(cache, "useThisInstead.json.gz"), DatabaseAge), ModReplacements.Empty);
        var verified = await Safe(() => VerifiedMods.LoadAsync(cache, version, DatabaseAge), VerifiedMods.Empty);
        var content = new ModProfileStore(Path.Combine(cache, "mod-profiles-v2.json"));
        content.Load();
        ct.ThrowIfCancellationRequested();

        return new RimForgeState
        {
            Paths = paths,
            GameVersion = version,
            FullGameVersion = paths.ReadFullGameVersion(),
            GameCopy = context.Copy?.ShortVersion,
            Installed = installed,
            Active = active,
            Hints = hints,
            Profiles = profiles,
            ActiveProfileId = activeProfile?.Id,
            Community = community,
            Extras = new CheckExtras
            {
                Verified = verified,
                Replacements = replacements,
                DuplicateFolders = duplicates,
                HasCode = m => HasCode(m.FolderPath),
            },
            Content = content.Get,
            Access = settings.AiAccess,
        };
    }

    private static async Task<T> Safe<T>(Func<Task<T>> load, T fallback)
    {
        try
        {
            return await load();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    public static bool HasCode(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories)
                .Any(f => !Path.GetFileName(f).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
