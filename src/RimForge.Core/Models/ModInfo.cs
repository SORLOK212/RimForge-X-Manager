namespace RimForge.Core.Models;

/// <summary>Where a mod was found on disk.</summary>
public enum ModSource
{
    /// <summary>Core game or an official DLC from &lt;game&gt;\Data.</summary>
    Official,
    /// <summary>A mod in &lt;game&gt;\Mods.</summary>
    Local,
    /// <summary>A Steam Workshop subscription.</summary>
    Workshop,
}

/// <summary>A dependency declared in About.xml (modDependencies).</summary>
public sealed record ModDependency(string PackageId, string? DisplayName, string? SteamWorkshopUrl, string? DownloadUrl);

/// <summary>
/// Everything RimForge knows about one installed mod. Rule lists are already resolved
/// for the current game version (a "...ByVersion" entry replaces the plain one, like the game does).
/// </summary>
public sealed record ModInfo
{
    /// <summary>Package id in lower case, exactly as the game writes it to ModsConfig.xml (may carry a "_steam" suffix for duplicates).</summary>
    public required string PackageId { get; init; }

    /// <summary>Package id as declared in About.xml, lower case, without any duplicate suffix.</summary>
    public required string DeclaredPackageId { get; init; }

    public required string Name { get; init; }
    public required string FolderPath { get; init; }
    public required ModSource Source { get; init; }

    public IReadOnlyList<string> Authors { get; init; } = [];
    public string? Description { get; init; }
    public string? Url { get; init; }
    public string? ModVersion { get; init; }
    public ulong? WorkshopId { get; init; }
    public string? PreviewImagePath { get; init; }

    /// <summary>Game versions like "1.5", "1.6".</summary>
    public IReadOnlyList<string> SupportedVersions { get; init; } = [];

    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<string> LoadBefore { get; init; } = [];
    public IReadOnlyList<string> LoadAfter { get; init; } = [];
    public IReadOnlyList<string> ForceLoadBefore { get; init; } = [];
    public IReadOnlyList<string> ForceLoadAfter { get; init; } = [];
    public IReadOnlyList<string> IncompatibleWith { get; init; } = [];

    /// <summary>Set when About.xml was missing or broken; the mod is still listed so the user can see it.</summary>
    public string? LoadError { get; init; }

    public bool IsOfficial => Source == ModSource.Official;
    public bool IsCore => PackageId == KnownMods.Core;

    public bool SupportsVersion(string gameVersion) =>
        SupportedVersions.Count == 0 || SupportedVersions.Contains(gameVersion);

    public override string ToString() => $"{Name} ({PackageId})";
}

public static class KnownMods
{
    public const string Core = "ludeon.rimworld";
    public const string Harmony = "brrainz.harmony";
    public const uint RimWorldAppId = 294100;
}
