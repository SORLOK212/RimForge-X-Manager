using System.Text.Json;
using RimForge.Core.Models;

namespace RimForge.Core.Sharing;

/// <summary>
/// What importing a shared modpack takes on this PC: which mods are here already, which come from the
/// Workshop, which are unpacked from the file itself, and which cannot be had at all.
/// </summary>
public sealed class ModpackPlan
{
    /// <summary>Entry → the installed mod it stands for.</summary>
    public required IReadOnlyDictionary<ModpackEntry, ModInfo> Have { get; init; }
    public required IReadOnlyList<ModpackEntry> FromFile { get; init; }
    public required IReadOnlyList<ModpackEntry> Download { get; init; }
    /// <summary>Local mods that were not put into the file: only their author can give them.</summary>
    public required IReadOnlyList<ModpackEntry> Unavailable { get; init; }
    /// <summary>Official expansions the modpack uses that this PC lacks.</summary>
    public required IReadOnlyList<ModpackEntry> MissingDlcs { get; init; }

    /// <summary>The modpack's list with each mod under the id it has (or will have) here.</summary>
    public required IReadOnlyList<string> Order { get; init; }

    public static ModpackPlan For(ModpackManifest manifest, IReadOnlyCollection<ModInfo> installed)
    {
        var byId = new Dictionary<string, ModInfo>();
        foreach (var mod in installed)
        {
            byId.TryAdd(mod.PackageId, mod);
            byId.TryAdd(mod.DeclaredPackageId, mod);
        }
        var byWorkshop = installed.Where(m => m.WorkshopId is not null).GroupBy(m => m.WorkshopId!.Value).ToDictionary(g => g.Key, g => g.First());

        var have = new Dictionary<ModpackEntry, ModInfo>();
        var fromFile = new List<ModpackEntry>();
        var download = new List<ModpackEntry>();
        var unavailable = new List<ModpackEntry>();
        var dlcs = new List<ModpackEntry>();
        var order = new List<string>();
        foreach (var entry in manifest.Mods)
        {
            var id = entry.Id.ToLowerInvariant();
            if (byId.TryGetValue(id, out var mod) || entry.WorkshopId is { } w && byWorkshop.TryGetValue(w, out mod))
            {
                have[entry] = mod;
                order.Add(mod.PackageId);
                continue;
            }
            order.Add(id);
            if (entry.IsOfficial) dlcs.Add(entry);
            else if (entry.Folder is not null) fromFile.Add(entry);
            else if (entry.WorkshopId is not null) download.Add(entry);
            else unavailable.Add(entry);
        }
        return new ModpackPlan
        {
            Have = have, FromFile = fromFile, Download = download, Unavailable = unavailable, MissingDlcs = dlcs,
            Order = order.Distinct().ToList(),
        };
    }

    /// <summary>
    /// Workshop mods whose author published a newer version after the modpack was made, judged by
    /// <paramref name="latest"/> (Workshop id → last update).
    /// </summary>
    public static IReadOnlyList<ModpackEntry> UpdatedSince(ModpackManifest manifest, IReadOnlyDictionary<ulong, DateTime> latest) =>
        manifest.Mods.Where(m => m.WorkshopId is { } w && m.UpdatedUtc is { } then && latest.TryGetValue(w, out var now) && now > then.AddMinutes(1))
            .ToList();
}

/// <summary>
/// Puts mod settings from a shared modpack into the game's Config folder. Files that are replaced are
/// copied to a backup folder first. Settings of mods that are still downloading wait in
/// <c>pending-settings.json</c> and are written once the mod is on disk.
/// </summary>
public sealed class ModSettingsInstaller(string configDir, string backupDir, string pendingFile)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Writes the settings for a mod living in <paramref name="modFolder"/>. Returns how many files were written.</summary>
    public int Apply(string modFolder, IReadOnlyDictionary<string, string> settings)
    {
        var count = 0;
        foreach (var (settingsClass, xml) in settings)
        {
            var target = ModpackFile.SettingsPath(configDir, modFolder, settingsClass);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                if (File.ReadAllText(target) == xml) continue;
                Directory.CreateDirectory(backupDir);
                File.Copy(target, Path.Combine(backupDir, Path.GetFileName(target)), overwrite: true);
            }
            File.WriteAllText(target, xml);
            count++;
        }
        return count;
    }

    /// <summary>Settings waiting for their mod: package id → class → XML.</summary>
    public Dictionary<string, Dictionary<string, string>> LoadPending()
    {
        try
        {
            return File.Exists(pendingFile)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(pendingFile)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void AddPending(string packageId, IReadOnlyDictionary<string, string> settings)
    {
        var pending = LoadPending();
        pending[packageId.ToLowerInvariant()] = settings.ToDictionary();
        SavePending(pending);
    }

    /// <summary>Writes the waiting settings of every mod that is on disk now. Returns the names of those mods.</summary>
    public List<string> ApplyPending(IEnumerable<ModInfo> installed)
    {
        var pending = LoadPending();
        if (pending.Count == 0) return [];
        var done = new List<string>();
        foreach (var mod in installed)
        {
            var key = pending.ContainsKey(mod.PackageId) ? mod.PackageId : pending.ContainsKey(mod.DeclaredPackageId) ? mod.DeclaredPackageId : null;
            if (key is null) continue;
            Apply(mod.FolderPath, pending[key]);
            pending.Remove(key);
            done.Add(mod.Name);
        }
        if (done.Count > 0) SavePending(pending);
        return done;
    }

    private void SavePending(Dictionary<string, Dictionary<string, string>> pending)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pendingFile)!);
        if (pending.Count == 0) File.Delete(pendingFile);
        else File.WriteAllText(pendingFile, JsonSerializer.Serialize(pending, Json));
    }
}
