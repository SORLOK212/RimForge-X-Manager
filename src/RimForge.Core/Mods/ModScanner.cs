using RimForge.Core.Models;
using RimForge.Core.Parsing;
using RimForge.Core.Paths;

namespace RimForge.Core.Mods;

/// <summary>Finds every installed mod (Core/DLC, local, Workshop) and reads its About.xml.</summary>
public static class ModScanner
{
    public static IReadOnlyList<ModInfo> Scan(GamePaths paths, string gameVersion)
    {
        var folders = new List<(string Path, ModSource Source)>();
        AddChildren(folders, paths.DataDir, ModSource.Official);
        AddChildren(folders, paths.LocalModsDir, ModSource.Local);
        AddChildren(folders, paths.WorkshopDir, ModSource.Workshop);

        var mods = new ModInfo?[folders.Count];
        Parallel.For(0, folders.Count, i => mods[i] = ReadMod(folders[i].Path, folders[i].Source, gameVersion));

        return ApplyDuplicateSuffixes(mods.OfType<ModInfo>().ToList());
    }

    private static void AddChildren(List<(string, ModSource)> target, string? dir, ModSource source)
    {
        if (dir is null || !Directory.Exists(dir)) return;
        foreach (var child in Directory.EnumerateDirectories(dir))
            target.Add((child, source));
    }

    /// <summary>Reads one mod folder. Returns null for folders that are not mods at all.</summary>
    public static ModInfo? ReadMod(string folder, ModSource source, string gameVersion)
    {
        var aboutDir = Path.Combine(folder, "About");
        var aboutFile = Path.Combine(aboutDir, "About.xml");
        if (!File.Exists(aboutFile)) return null;

        var folderName = Path.GetFileName(folder);
        ulong? workshopId = ReadWorkshopId(aboutDir) ?? (source == ModSource.Workshop && ulong.TryParse(folderName, out var id) ? id : null);
        var preview = Path.Combine(aboutDir, "Preview.png");

        AboutXml about;
        try
        {
            about = AboutXmlParser.ParseFile(aboutFile);
        }
        catch (Exception ex)
        {
            return new ModInfo
            {
                PackageId = folderName.ToLowerInvariant(),
                DeclaredPackageId = folderName.ToLowerInvariant(),
                Name = folderName,
                FolderPath = folder,
                Source = source,
                WorkshopId = workshopId,
                LoadError = ex.Message,
            };
        }

        var packageId = (about.PackageId ?? folderName).ToLowerInvariant();
        return new ModInfo
        {
            PackageId = packageId,
            DeclaredPackageId = packageId,
            // DLC About.xml files have no <name>; their folder names (Royalty, Biotech...) read well.
            Name = about.Name ?? folderName,
            FolderPath = folder,
            Source = source,
            Authors = about.Authors,
            Description = about.Description,
            Url = about.Url,
            ModVersion = about.ModVersion,
            WorkshopId = workshopId,
            PreviewImagePath = File.Exists(preview) ? preview : null,
            SupportedVersions = about.SupportedVersions,
            Dependencies = AboutXml.Resolve(about.Dependencies, about.DependenciesByVersion, gameVersion),
            LoadBefore = AboutXml.Resolve(about.LoadBefore, about.LoadBeforeByVersion, gameVersion),
            LoadAfter = AboutXml.Resolve(about.LoadAfter, about.LoadAfterByVersion, gameVersion),
            ForceLoadBefore = about.ForceLoadBefore,
            ForceLoadAfter = about.ForceLoadAfter,
            IncompatibleWith = AboutXml.Resolve(about.IncompatibleWith, about.IncompatibleWithByVersion, gameVersion),
            LoadError = about.PackageId is null ? "About.xml has no <packageId>" : null,
        };
    }

    private static ulong? ReadWorkshopId(string aboutDir)
    {
        var file = Path.Combine(aboutDir, "PublishedFileId.txt");
        if (!File.Exists(file)) return null;
        return ulong.TryParse(File.ReadAllText(file).Trim(), out var id) ? id : null;
    }

    /// <summary>
    /// When the same package id is installed twice (e.g. a local copy and a Workshop subscription),
    /// the game renames the Workshop copy to "&lt;id&gt;_steam". We mirror that so ModsConfig.xml entries match.
    /// </summary>
    private static IReadOnlyList<ModInfo> ApplyDuplicateSuffixes(List<ModInfo> mods)
    {
        var result = new List<ModInfo>(mods.Count);
        foreach (var group in mods.GroupBy(m => m.PackageId))
        {
            if (group.Count() == 1 || !group.Any(m => m.Source != ModSource.Workshop))
            {
                result.AddRange(group);
                continue;
            }
            foreach (var mod in group)
            {
                result.Add(mod.Source == ModSource.Workshop
                    ? mod with { PackageId = mod.PackageId + "_steam" }
                    : mod);
            }
        }
        return result;
    }
}
