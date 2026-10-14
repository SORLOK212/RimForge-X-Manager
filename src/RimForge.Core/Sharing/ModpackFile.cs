using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimForge.Core.Models;
using RimForge.Core.Sorting;

namespace RimForge.Core.Sharing;

/// <summary>One mod of a shared modpack.</summary>
public sealed class ModpackEntry
{
    /// <summary>Package id, as in ModsConfig.xml.</summary>
    public required string Id { get; set; }
    public string? Name { get; set; }
    public ulong? WorkshopId { get; set; }

    /// <summary>"official", "workshop" or "local".</summary>
    public string Source { get; set; } = "local";

    /// <summary>When the author of the mod published the version the modpack was made with (Workshop mods).</summary>
    public DateTime? UpdatedUtc { get; set; }
    public long? SizeBytes { get; set; }

    /// <summary>Folder inside the file holding the mod itself (full archives only).</summary>
    public string? Folder { get; set; }

    /// <summary>Settings files inside the file (<c>settings/…/ClassName.xml</c>).</summary>
    public List<string>? Settings { get; set; }

    [JsonIgnore] public bool IsOfficial => Source == "official";
}

/// <summary>Describes a shared modpack: <c>manifest.json</c> inside a <c>.rimforge</c> file.</summary>
public sealed class ModpackManifest
{
    public const string FormatName = "rimforge-modpack";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedWith { get; set; }

    /// <summary>Full game version the modpack was made on, e.g. "1.6.4633 rev1261".</summary>
    public string? GameVersion { get; set; }

    /// <summary>Mods in load order, the game and its expansions included.</summary>
    public List<ModpackEntry> Mods { get; set; } = [];

    /// <summary>Order rules of the modpack ("A always after B").</summary>
    public List<UserRule> Rules { get; set; } = [];

    /// <summary>Mods pinned to the top / bottom of the list.</summary>
    public List<string>? PinTop { get; set; }
    public List<string>? PinBottom { get; set; }

    [JsonIgnore] public IEnumerable<ModpackEntry> Expansions => Mods.Where(m => m.IsOfficial && m.Id != KnownMods.Core);
    [JsonIgnore] public bool HasModFiles => Mods.Any(m => m.Folder is not null);
    [JsonIgnore] public int SettingsCount => Mods.Count(m => m.Settings is { Count: > 0 });
}

/// <summary>
/// The <c>.rimforge</c> file: a zip with <c>manifest.json</c>, optionally the mods' settings
/// (<c>settings/&lt;mod&gt;/&lt;Class&gt;.xml</c>) and, in a full archive, the mods themselves (<c>mods/&lt;mod&gt;/…</c>).
/// </summary>
public static class ModpackFile
{
    public const string Extension = ".rimforge";
    private const string ManifestEntry = "manifest.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Names and descriptions stay readable in the file (Cyrillic, Chinese… as they are).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── Mod settings (Config\Mod_<folder>_<Class>.xml) ──

    /// <summary>
    /// The settings files of a mod. The game names them after the mod's folder and its settings class:
    /// <c>Mod_&lt;folder&gt;_&lt;Class&gt;.xml</c> in the Config folder.
    /// </summary>
    public static IReadOnlyList<string> SettingsFilesOf(ModInfo mod, string? configDir)
    {
        var dir = configDir is null ? null : Path.Combine(configDir, "Config");
        if (dir is null || !Directory.Exists(dir) || mod.IsOfficial) return [];
        var prefix = "Mod_" + Path.GetFileName(mod.FolderPath) + "_";
        return Directory.EnumerateFiles(dir, "Mod_*.xml")
            .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileNameWithoutExtension(f).Length > prefix.Length)
            .ToList();
    }

    /// <summary>"Mod_2009463077_HugsLibController.xml" for the folder "2009463077" → "HugsLibController".</summary>
    public static string SettingsClass(string file, string folder) =>
        Path.GetFileNameWithoutExtension(file)[("Mod_" + folder + "_").Length..];

    /// <summary>Where the game reads a settings class of a mod living in <paramref name="modFolder"/>.</summary>
    public static string SettingsPath(string configDir, string modFolder, string settingsClass) =>
        Path.Combine(configDir, "Config", $"Mod_{Path.GetFileName(modFolder)}_{settingsClass}.xml");

    // ── Building ──

    /// <summary>
    /// Describes a modpack: its mods in order, with names and Workshop ids (taken from the installed mods or,
    /// for mods missing here, from <paramref name="hints"/>), Workshop update dates and sizes.
    /// </summary>
    public static ModpackManifest Build(string name, string? description, IReadOnlyList<string> order,
        IReadOnlyDictionary<string, ModInfo> installed, IReadOnlyDictionary<string, Profiles.ProfileMod> hints,
        string? gameVersion, IEnumerable<UserRule> rules, Func<ModInfo, DateTime?> updated, Func<ModInfo, long?> size)
    {
        var manifest = new ModpackManifest
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            GameVersion = gameVersion,
            CreatedWith = "RimForge " + typeof(ModpackFile).Assembly.GetName().Version?.ToString(3),
            Rules = rules.ToList(),
        };
        foreach (var id in order)
        {
            if (installed.TryGetValue(id, out var mod))
            {
                manifest.Mods.Add(new ModpackEntry
                {
                    Id = id, Name = mod.Name, WorkshopId = mod.WorkshopId,
                    Source = mod.Source switch { ModSource.Official => "official", ModSource.Workshop => "workshop", _ => "local" },
                    UpdatedUtc = mod.Source == ModSource.Workshop ? updated(mod) : null,
                    SizeBytes = mod.IsOfficial ? null : size(mod),
                });
            }
            else
            {
                var hint = hints.GetValueOrDefault(id);
                manifest.Mods.Add(new ModpackEntry
                {
                    Id = id, Name = hint?.Name, WorkshopId = hint?.WorkshopId,
                    Source = id.StartsWith("ludeon.rimworld") ? "official" : hint?.WorkshopId is not null ? "workshop" : "local",
                });
            }
        }
        return manifest;
    }

    /// <summary>
    /// Picks the files to put next to the manifest — the mods' settings and/or the mods themselves (never the
    /// game and its expansions) — and notes them in the manifest's entries.
    /// </summary>
    public static List<Part> Prepare(ModpackManifest manifest, IReadOnlyDictionary<string, ModInfo> installed, string? configDir,
        bool includeSettings, bool includeMods)
    {
        var parts = new List<Part>();
        foreach (var entry in manifest.Mods)
        {
            entry.Settings = null;
            entry.Folder = null;
            if (!installed.TryGetValue(entry.Id, out var mod) || mod.IsOfficial) continue;
            if (includeSettings && SettingsParts(mod, configDir) is { Count: > 0 } settings)
            {
                entry.Settings = settings.Select(s => s.Entry).ToList();
                parts.AddRange(settings);
            }
            if (includeMods && Directory.Exists(mod.FolderPath))
            {
                entry.Folder = SafeName(mod.PackageId);
                parts.AddRange(ModParts(mod, entry.Folder));
            }
        }
        return parts;
    }

    // ── Writing ──

    /// <summary>A file to put into the archive: its name inside, and where it is on disk.</summary>
    public sealed record Part(string Entry, string Source);

    /// <summary>
    /// Writes the modpack. <paramref name="parts"/> are settings and mod files already named for the archive.
    /// Reports the share of bytes written; a cancelled or failed write leaves no half-written file behind.
    /// </summary>
    public static void Write(string path, ModpackManifest manifest, IReadOnlyList<Part> parts,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var temp = path + ".tmp";
        try
        {
            var total = Math.Max(1, parts.Sum(p => new FileInfo(p.Source).Length));
            long done = 0;
            using (var file = File.Create(temp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using (var stream = entry.Open())
                    JsonSerializer.Serialize(stream, manifest, Json);

                var buffer = new byte[1 << 16];
                foreach (var part in parts)
                {
                    ct.ThrowIfCancellationRequested();
                    // Pictures and sounds are compressed already; packing them again only costs time.
                    var level = Path.GetExtension(part.Source).ToLowerInvariant() is ".png" or ".jpg" or ".dds" or ".ogg" or ".mp3" or ".wav" or ".dll"
                        ? CompressionLevel.Fastest : CompressionLevel.Optimal;
                    var zipEntry = zip.CreateEntry(part.Entry, level);
                    zipEntry.LastWriteTime = File.GetLastWriteTime(part.Source);
                    using var input = File.OpenRead(part.Source);
                    using var output = zipEntry.Open();
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        done += read;
                        progress?.Report((double)done / total);
                    }
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Entry names for the settings of a mod: <c>settings/&lt;safe id&gt;/&lt;Class&gt;.xml</c>.</summary>
    public static List<Part> SettingsParts(ModInfo mod, string? configDir)
    {
        var folder = Path.GetFileName(mod.FolderPath);
        return SettingsFilesOf(mod, configDir)
            .Select(f => new Part($"settings/{SafeName(mod.PackageId)}/{SettingsClass(f, folder)}.xml", f))
            .ToList();
    }

    /// <summary>Every file of a mod folder as <c>mods/&lt;folder&gt;/…</c> (version control folders left out).</summary>
    public static List<Part> ModParts(ModInfo mod, string folderInArchive)
    {
        var root = mod.FolderPath;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(p => p is ".git" or ".vs" or ".svn"))
            .Select(f => new Part($"mods/{folderInArchive}/{Path.GetRelativePath(root, f).Replace('\\', '/')}", f))
            .ToList();
    }

    /// <summary>A name that is safe as a folder: the mod's package id without odd characters.</summary>
    public static string SafeName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(text.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim(' ', '.');
        return safe.Length == 0 ? "mod" : safe;
    }

    // ── Reading ──

    /// <summary>Reads the description of a modpack file. Throws <see cref="InvalidDataException"/> for anything else.</summary>
    public static ModpackManifest Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("Not a RimForge modpack.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<ModpackManifest>(stream, Json) ?? throw new InvalidDataException("Empty modpack.");
        if (manifest.Format != ModpackManifest.FormatName) throw new InvalidDataException("Not a RimForge modpack.");
        if (manifest.Version > ModpackManifest.CurrentVersion) throw new InvalidDataException("Made by a newer RimForge.");
        return manifest;
    }

    /// <summary>The settings of one mod from the file: class name → XML text.</summary>
    public static Dictionary<string, string> ReadSettings(string path, ModpackEntry mod)
    {
        var result = new Dictionary<string, string>();
        if (mod.Settings is not { Count: > 0 }) return result;
        using var zip = ZipFile.OpenRead(path);
        foreach (var name in mod.Settings)
        {
            if (zip.GetEntry(name) is not { } entry) continue;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            result[Path.GetFileNameWithoutExtension(name)] = reader.ReadToEnd();
        }
        return result;
    }

    /// <summary>
    /// Unpacks a mod from a full archive into <paramref name="modsDir"/> (the game's Mods folder) under a new
    /// folder named after it. Returns that folder. Paths that would leave the folder are refused.
    /// </summary>
    public static string ExtractMod(string path, ModpackEntry mod, string modsDir, IProgress<long>? bytes = null, CancellationToken ct = default)
    {
        if (mod.Folder is null) throw new InvalidOperationException("The mod is not in this file.");
        var name = SafeName(mod.Name ?? mod.Id);
        var target = Path.Combine(modsDir, name);
        for (var n = 2; Directory.Exists(target); n++) target = Path.Combine(modsDir, $"{name} ({n})");
        var prefix = $"mods/{mod.Folder}/";

        var temp = target + ".unpacking";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        var tempRoot = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
            {
                ct.ThrowIfCancellationRequested();
                var file = Path.GetFullPath(Path.Combine(temp, entry.FullName[prefix.Length..]));
                if (!file.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bad path in the modpack file.");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                entry.ExtractToFile(file, overwrite: true);
                bytes?.Report(entry.Length);
            }
            Directory.Move(temp, target);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
        return target;
    }

    /// <summary>The total unpacked size of the mods included in a full archive.</summary>
    public static long IncludedBytes(string path, IEnumerable<ModpackEntry> mods)
    {
        var prefixes = mods.Where(m => m.Folder is not null).Select(m => $"mods/{m.Folder}/").ToList();
        if (prefixes.Count == 0) return 0;
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => prefixes.Any(p => e.FullName.StartsWith(p, StringComparison.Ordinal))).Sum(e => e.Length);
    }
}
