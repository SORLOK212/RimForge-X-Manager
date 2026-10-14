namespace RimForge.Core.Profiles;

/// <summary>
/// Gives every modpack its own mod settings (the game's <c>Config\Mod_*.xml</c> files). When the player leaves
/// a modpack its settings are copied next to it (<c>profiles\&lt;id&gt;.settings</c>); when they come back, exactly
/// those files are put back. A modpack that has no copy yet simply keeps whatever is in the game.
/// </summary>
public sealed class ProfileSettings(string configDir, string profilesDir, string backupsDir)
{
    private const int BackupsKept = 10;

    private string ConfigFolder => Path.Combine(configDir, "Config");
    private string CopyOf(string profileId) => Path.Combine(profilesDir, profileId + ".settings");

    private IEnumerable<string> Current() =>
        Directory.Exists(ConfigFolder) ? Directory.EnumerateFiles(ConfigFolder, "Mod_*.xml") : [];

    public bool HasCopy(string profileId) => Directory.Exists(CopyOf(profileId));

    /// <summary>Remembers the game's current mod settings as the settings of <paramref name="profileId"/>.</summary>
    public void Save(string profileId)
    {
        var target = CopyOf(profileId);
        var temp = target + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        foreach (var file in Current()) File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(temp, target);
    }

    /// <summary>
    /// Puts the remembered settings of <paramref name="profileId"/> into the game: its files are copied in and
    /// settings files it did not have are removed (those mods start from their defaults, as they did in it).
    /// Returns false when the modpack has no remembered settings (nothing is changed then).
    /// </summary>
    public bool Restore(string profileId, bool backUpCurrent)
    {
        var source = CopyOf(profileId);
        if (!Directory.Exists(source)) return false;
        if (backUpCurrent) BackUp();
        Directory.CreateDirectory(ConfigFolder);
        var wanted = Directory.EnumerateFiles(source, "Mod_*.xml").ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Current().Where(f => !wanted.ContainsKey(Path.GetFileName(f))).ToList()) File.Delete(file);
        foreach (var (name, file) in wanted) File.Copy(file, Path.Combine(ConfigFolder, name), overwrite: true);
        return true;
    }

    /// <summary>
    /// Switching modpacks: the settings of the one being left are remembered (if it is known), then the new one's
    /// are put in. When the old settings are not remembered by any modpack, they go to a backup first.
    /// </summary>
    public bool Switch(string? fromProfileId, string toProfileId)
    {
        if (fromProfileId == toProfileId) return false;
        if (fromProfileId is not null) Save(fromProfileId);
        return Restore(toProfileId, backUpCurrent: fromProfileId is null);
    }

    public void Forget(string profileId)
    {
        if (HasCopy(profileId)) Directory.Delete(CopyOf(profileId), true);
    }

    /// <summary>Copies the current settings to <c>backups\mod-settings\&lt;time&gt;</c>, keeping the last few backups.</summary>
    public string? BackUp()
    {
        var files = Current().ToList();
        if (files.Count == 0) return null;
        var root = Path.Combine(backupsDir, "mod-settings");
        var dir = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
        Directory.CreateDirectory(dir);
        foreach (var file in files) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        foreach (var old in new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.Name).Skip(BackupsKept))
            old.Delete(true);
        return dir;
    }
}
