using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimForge.Core.Models;
using RimForge.Core.Paths;

namespace RimForge.Core.Library;

/// <summary>A saved copy of the game: its own folder, its own saves and settings, its own frozen mods.</summary>
public sealed class GameInstance
{
    public required string Id { get; set; }
    /// <summary>Full version, e.g. "1.6.4871 rev590".</summary>
    public required string Version { get; set; }
    public required string GameDir { get; set; }
    /// <summary>The game's save data folder for this copy (passed as -savedatafolder).</summary>
    public required string DataDir { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int ModCount { get; set; }
    /// <summary>The Steam branch the copy was downloaded from ("version-1.5", "unstable"); null for a saved copy of the installed game.</summary>
    public string? Branch { get; set; }

    /// <summary>"1.6.4871" — the version without the build revision.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ShortVersion => Version.Split(' ')[0];

    [System.Text.Json.Serialization.JsonIgnore]
    public GamePaths Paths => new() { GameDir = GameDir, ConfigDir = DataDir, WorkshopDir = null, Store = GameStore.Unknown };
}

/// <summary>How much a new copy of the game takes.</summary>
public sealed record SnapshotPlan(string Version, long GameBytes, long DataBytes, long NewModBytes, int NewMods, int ReusedMods)
{
    public long TotalBytes => GameBytes + DataBytes + NewModBytes;
}

/// <summary>
/// The RimForge library: copies of the game (<c>Games\&lt;version&gt;</c>), one store of mods shared by all copies
/// (<c>Mods\&lt;mod&gt;@&lt;fingerprint&gt;</c>, a version of a mod is stored once) and the save data of each copy
/// (<c>Data\&lt;version&gt;</c>). A copy's Mods folder holds only junctions into the store, so the game copy itself
/// stays clean and switching or removing mods never copies anything.
/// </summary>
public sealed class GameLibrary(string root)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;
    public string GamesDir => Path.Combine(Root, "Games");
    public string ModsDir => Path.Combine(Root, "Mods");
    public string DataRoot => Path.Combine(Root, "Data");
    public string ToolsDir => Path.Combine(Root, "Tools");

    /// <summary>Where a branch is downloaded until it is complete (an unfinished download there is continued).</summary>
    public string DownloadDir(string branch) => Path.Combine(GamesDir, "_download-" + Sharing.ModpackFile.SafeName(branch));
    private string IndexFile => Path.Combine(Root, "library.json");

    public List<GameInstance> Load()
    {
        try
        {
            return File.Exists(IndexFile) ? JsonSerializer.Deserialize<List<GameInstance>>(File.ReadAllText(IndexFile), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save(List<GameInstance> instances)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(IndexFile, JsonSerializer.Serialize(instances, Json));
    }

    // ── Mod store ──

    /// <summary>
    /// The name a version of a mod has in the store: its folder name plus a fingerprint of its files (names,
    /// sizes, dates). The same version is stored once however many game copies use it.
    /// </summary>
    public static string StoreKey(ModInfo mod)
    {
        var root = mod.FolderPath;
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            text.Append(Path.GetRelativePath(root, file)).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..10].ToLowerInvariant();
        return Sharing.ModpackFile.SafeName(Path.GetFileName(root.TrimEnd('\\', '/'))) + "@" + hash;
    }

    public string StorePath(string key) => Path.Combine(ModsDir, key);

    // ── Copies of the game ──

    /// <summary>What saving the current game would take (the mods already in the store are not copied again).</summary>
    public SnapshotPlan Plan(GamePaths game, IReadOnlyList<(ModInfo Mod, string Key)> mods)
    {
        var version = game.ReadFullGameVersion() ?? "unknown";
        var gameBytes = GameFiles(game.GameDir!).Sum(f => new FileInfo(f).Length);
        var dataBytes = game.ConfigDir is { } data && Directory.Exists(data) ? DataFiles(data).Sum(f => new FileInfo(f).Length) : 0;
        var fresh = mods.Where(m => !Directory.Exists(StorePath(m.Key))).ToList();
        var freshBytes = fresh.Sum(m => FolderBytes(m.Mod.FolderPath));
        return new SnapshotPlan(version, gameBytes, dataBytes, freshBytes, fresh.Count, mods.Count - fresh.Count);
    }

    public GameInstance? Find(string shortVersion) => Load().FirstOrDefault(i => i.ShortVersion == shortVersion);

    /// <summary>
    /// Saves the game in <paramref name="game"/> as a new copy: the game files (without its Mods folder), the
    /// given mods into the store (each linked into the copy's Mods folder under its usual folder name, so mod
    /// settings keep their names) and the save data (saves, settings, mod settings; not the logs).
    /// </summary>
    public GameInstance Snapshot(GamePaths game, IReadOnlyList<(ModInfo Mod, string Key)> mods,
        IProgress<(string Stage, double Share)>? progress = null, CancellationToken ct = default)
    {
        var plan = Plan(game, mods);
        var id = Sharing.ModpackFile.SafeName(plan.Version.Split(' ')[0]);
        if (Load().Any(i => i.Id == id)) throw new InvalidOperationException($"RimWorld {plan.Version} is saved already.");

        var gameDir = Path.Combine(GamesDir, id);
        var dataDir = Path.Combine(DataRoot, id);
        var total = Math.Max(1, plan.TotalBytes);
        long done = 0;
        void Count(long bytes, string stage)
        {
            done += bytes;
            progress?.Report((stage, Math.Min(1, (double)done / total)));
        }

        try
        {
            // 1. The game itself.
            CopyFiles(game.GameDir!, GameFiles(game.GameDir!), gameDir + ".tmp", b => Count(b, "game"), ct);
            Directory.Move(gameDir + ".tmp", gameDir);
            Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));

            // 2. Its save data.
            if (game.ConfigDir is { } data && Directory.Exists(data))
                CopyFiles(data, DataFiles(data), dataDir + ".tmp", b => Count(b, "data"), ct);
            else Directory.CreateDirectory(dataDir + ".tmp");
            Directory.Move(dataDir + ".tmp", dataDir);

            // 3. Mods: into the store (once per version), then linked into the copy.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mod, key) in mods)
            {
                ct.ThrowIfCancellationRequested();
                var stored = StorePath(key);
                if (!Directory.Exists(stored))
                {
                    var files = Directory.EnumerateFiles(mod.FolderPath, "*", SearchOption.AllDirectories);
                    CopyFiles(mod.FolderPath, files, stored + ".tmp", b => Count(b, "mods"), ct);
                    Directory.Move(stored + ".tmp", stored);
                }
                var name = Path.GetFileName(mod.FolderPath.TrimEnd('\\', '/'));
                var linkName = name;
                for (var n = 2; !used.Add(linkName); n++) linkName = $"{name} ({n})";
                Junction.Create(Path.Combine(gameDir, "Mods", linkName), stored);
            }
        }
        catch
        {
            foreach (var dir in new[] { gameDir, gameDir + ".tmp", dataDir, dataDir + ".tmp" }) Junction.DeleteTree(dir);
            foreach (var (_, key) in mods) Junction.DeleteTree(StorePath(key) + ".tmp");
            throw;
        }

        var instance = new GameInstance { Id = id, Version = plan.Version, GameDir = gameDir, DataDir = dataDir, ModCount = mods.Count };
        var all = Load();
        all.Add(instance);
        Save(all);
        return instance;
    }

    /// <summary>
    /// Takes a game downloaded into <paramref name="downloaded"/> (see <see cref="DownloadDir"/>) as a new copy: it
    /// moves under its version, loses SteamCMD's bookkeeping and gets its own empty save data, with the player's game
    /// settings (language, screen, sound) taken from <paramref name="settingsFrom"/>, the save data of another game.
    /// </summary>
    public GameInstance Adopt(string downloaded, string? branch, string? settingsFrom)
    {
        var version = new GamePaths { GameDir = downloaded }.ReadFullGameVersion()
                      ?? throw new IOException("The downloaded game has no Version.txt.");
        var id = Sharing.ModpackFile.SafeName(version.Split(' ')[0]);
        if (Load().Any(i => i.Id == id)) throw new InvalidOperationException($"RimWorld {version} is saved already.");

        var gameDir = Path.Combine(GamesDir, id);
        var dataDir = Path.Combine(DataRoot, id);
        Junction.DeleteTree(Path.Combine(downloaded, "steamapps"));
        if (Directory.Exists(gameDir)) Junction.DeleteTree(gameDir); // left over from a copy removed by hand
        Directory.Move(downloaded, gameDir);
        Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));

        var config = Path.Combine(dataDir, "Config");
        Directory.CreateDirectory(config);
        if (settingsFrom is not null && Path.Combine(settingsFrom, "Config", "Prefs.xml") is var prefs && File.Exists(prefs))
            File.Copy(prefs, Path.Combine(config, "Prefs.xml"), overwrite: true);

        var instance = new GameInstance { Id = id, Version = version, GameDir = gameDir, DataDir = dataDir, Branch = branch };
        var all = Load();
        all.Add(instance);
        Save(all);
        return instance;
    }

    /// <summary>
    /// Freezes one more mod into a copy of the game (e.g. one just downloaded from the Workshop): stores its
    /// current version and links it into the copy's Mods folder, replacing an older version linked there.
    /// </summary>
    public void AddMod(GameInstance instance, ModInfo mod)
    {
        var key = StoreKey(mod);
        var stored = StorePath(key);
        if (!Directory.Exists(stored))
        {
            CopyFiles(mod.FolderPath, Directory.EnumerateFiles(mod.FolderPath, "*", SearchOption.AllDirectories), stored + ".tmp", _ => { }, default);
            Directory.Move(stored + ".tmp", stored);
        }
        var link = Path.Combine(instance.GameDir, "Mods", Path.GetFileName(mod.FolderPath.TrimEnd('\\', '/')));
        if (Junction.IsLink(link))
        {
            if (string.Equals(Path.GetFullPath(Junction.TargetOf(link)!), Path.GetFullPath(stored), StringComparison.OrdinalIgnoreCase)) return;
            Junction.Remove(link);
        }
        else if (Directory.Exists(link)) throw new IOException("A real folder is in the way: " + link);
        Junction.Create(link, stored);

        var all = Load();
        if (all.FirstOrDefault(i => i.Id == instance.Id) is { } saved)
        {
            saved.ModCount = Directory.GetDirectories(Path.Combine(instance.GameDir, "Mods")).Length;
            instance.ModCount = saved.ModCount;
            Save(all);
        }
    }

    /// <summary>
    /// Removes a copy of the game with its save data. Mods in the store that no other copy uses are removed
    /// too. Returns the bytes freed.
    /// </summary>
    public long Delete(GameInstance instance)
    {
        var all = Load();
        all.RemoveAll(i => i.Id == instance.Id);
        Save(all);

        long freed = 0;
        var linked = LinkedStore(instance);
        freed += FolderBytes(instance.GameDir) + FolderBytes(instance.DataDir);
        Junction.DeleteTree(instance.GameDir);
        Junction.DeleteTree(instance.DataDir);

        var stillUsed = all.SelectMany(LinkedStore).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stored in linked.Where(s => !stillUsed.Contains(s)))
        {
            freed += FolderBytes(stored);
            Junction.DeleteTree(stored);
        }
        return freed;
    }

    /// <summary>The store folders a copy's Mods folder links to.</summary>
    private static List<string> LinkedStore(GameInstance instance)
    {
        var mods = Path.Combine(instance.GameDir, "Mods");
        return Directory.Exists(mods)
            ? Directory.GetDirectories(mods).Select(Junction.TargetOf).OfType<string>().Select(Path.GetFullPath).ToList()
            : [];
    }

    // ── Files ──

    /// <summary>Everything of the game except its Mods folder (mods come from the store).</summary>
    private static IEnumerable<string> GameFiles(string gameDir)
    {
        var mods = Path.Combine(gameDir, "Mods") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(mods, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The save data without the logs (they belong to the copy that wrote them).</summary>
    private static IEnumerable<string> DataFiles(string dataDir) =>
        Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("Player", StringComparison.OrdinalIgnoreCase) || !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase));

    private static long FolderBytes(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    private static void CopyFiles(string root, IEnumerable<string> files, string target, Action<long> copied, CancellationToken ct)
    {
        if (Directory.Exists(target)) Junction.DeleteTree(target);
        Directory.CreateDirectory(target);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var to = Path.Combine(target, Path.GetRelativePath(root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
            copied(new FileInfo(file).Length);
        }
    }
}
