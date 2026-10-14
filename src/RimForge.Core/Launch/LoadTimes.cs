using System.Globalization;
using System.Xml.Linq;
using RimForge.Core.Content;

namespace RimForge.Core.Launch;

/// <summary>What the Loading Progress mod knows: its settings file and the load times it recorded.</summary>
/// <param name="SettingsFile">Config\Mod_&lt;folder&gt;_LoadingProgressMod.xml (it may not exist yet).</param>
/// <param name="LastLoadSeconds">The last startup it timed (to the main menu), if any.</param>
/// <param name="TracksMods">"Track startup loading impact" is on: the next startup is timed mod by mod.</param>
/// <param name="SavesReport">The per-mod report is written to StartupImpactData.xml after every startup.</param>
public sealed record LoadingProgressState(string SettingsFile, double? LastLoadSeconds, DateTime? MeasuredUtc, bool TracksMods, bool SavesReport)
{
    public bool Ready => TracksMods && SavesReport;
}

/// <summary>Loading Progress' per-mod report of one startup.</summary>
/// <param name="TotalSeconds">The whole startup, when the report says.</param>
/// <param name="Mods">Seconds each mod added, by package id (lower case).</param>
public sealed record StartupReport(double? TotalSeconds, IReadOnlyDictionary<string, double> Mods, DateTime WrittenUtc);

/// <summary>One line of the startup breakdown.</summary>
public sealed record ModLoadTime(string PackageId, double Seconds, bool Measured);

/// <summary>How long the game will take to start with this list, and where each part of the figure comes from.</summary>
public sealed record LoadTimeEstimate(double TotalSeconds, double BaseSeconds, IReadOnlyList<ModLoadTime> Mods, LoadTimeSource Source, int Estimated);

public enum LoadTimeSource
{
    /// <summary>Nothing measured yet: worked out from what the mods contain.</summary>
    Guess,
    /// <summary>The last startup's total was measured; the parts are shared out by content.</summary>
    LastStartup,
    /// <summary>Each mod was measured (Loading Progress report); new mods are estimated.</summary>
    PerMod,
}

public static class LoadTimes
{
    public const string LoadingProgressId = "ilyvion.loadingprogress";
    public const ulong LoadingProgressWorkshopId = 3535481557;
    public const string ReportFile = "StartupImpactData.xml";
    private const string SettingsClass = "ilyvion.LoadingProgress.Settings";

    /// <summary>The game itself (Core and the DLC) when nothing better is known.</summary>
    public const double DefaultBaseSeconds = 14;

    // ── Loading Progress ──

    /// <param name="modFolderName">The folder of the installed Loading Progress (the game names its settings file after it).</param>
    public static LoadingProgressState ReadLoadingProgress(string configDir, string modFolderName)
    {
        var file = Path.Combine(configDir, "Config", $"Mod_{modFolderName}_LoadingProgressMod.xml");
        double? last = null;
        bool tracks = false, saves = false;
        DateTime? when = null;
        try
        {
            if (File.Exists(file))
            {
                var settings = XDocument.Load(file).Descendants("ModSettings").FirstOrDefault();
                if (settings is not null)
                {
                    last = settings.Element("loadingTimes")?.Elements("li").Select(e => Number(e.Value)).OfType<double>().LastOrDefault() is > 0 and var s ? s : null;
                    tracks = Flag(settings.Element("trackStartupLoadingImpact"));
                    saves = Flag(settings.Element("autoSaveStartupImpactReport"));
                    when = File.GetLastWriteTimeUtc(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
        }
        return new LoadingProgressState(file, last, when, tracks, saves);
    }

    /// <summary>
    /// Turns on Loading Progress' per-mod timing and its report file, keeping every other setting (a copy of the old
    /// file goes to <paramref name="backupDir"/>). Only while the game is closed: the game writes this file on exit.
    /// </summary>
    public static void EnableTracking(LoadingProgressState state, string backupDir)
    {
        XDocument doc;
        if (File.Exists(state.SettingsFile))
        {
            Directory.CreateDirectory(backupDir);
            File.Copy(state.SettingsFile, Path.Combine(backupDir, Path.GetFileName(state.SettingsFile) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss")), overwrite: true);
            doc = XDocument.Load(state.SettingsFile);
        }
        else
        {
            doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("SettingsBlock"));
        }
        var root = doc.Root!;
        var settings = root.Element("ModSettings");
        if (settings is null)
        {
            settings = new XElement("ModSettings", new XAttribute("Class", SettingsClass));
            root.Add(settings);
        }
        settings.SetElementValue("trackStartupLoadingImpact", "True");
        settings.SetElementValue("autoSaveStartupImpactReport", "True");
        Directory.CreateDirectory(Path.GetDirectoryName(state.SettingsFile)!);
        doc.Save(state.SettingsFile);
    }

    /// <summary>
    /// Reads StartupImpactData.xml (next to the saves). The format is the mod's own, so this only relies on what any
    /// version has: entries that carry a package id and a total, and the session's loading time.
    /// </summary>
    public static StartupReport? ReadReport(string configDir)
    {
        var file = Path.Combine(configDir, ReportFile);
        if (!File.Exists(file)) return null;
        try
        {
            var doc = XDocument.Load(file);
            var mods = new Dictionary<string, (double Value, bool Ms)>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.Descendants())
            {
                var id = (e.Element("modPackageId") ?? e.Element("packageId"))?.Value.Trim();
                var ms = e.Element("totalImpactMs");
                var value = ms ?? e.Element("totalImpact");
                if (string.IsNullOrEmpty(id) || value is null || Number(value.Value) is not { } v) continue;
                mods[id.ToLowerInvariant()] = (mods.GetValueOrDefault(id.ToLowerInvariant()).Value + v, ms is not null);
            }
            if (mods.Count == 0) return null;

            double? total = null;
            foreach (var name in new[] { "timeToMenuMs", "loadingTimeMs", "timeToMenu", "loadingTime" })
                if (doc.Descendants(name).FirstOrDefault() is { } t && Number(t.Value) is > 0 and var n)
                {
                    total = name.EndsWith("Ms") ? n / 1000 : n;
                    break;
                }

            // Times without a unit in their name: milliseconds if seconds would make the mods far longer than the startup.
            var sum = mods.Values.Sum(m => m.Value);
            var unitless = mods.Values.Any(m => !m.Ms) && (total is { } tt ? sum > tt * 3 : sum > 2000);
            var seconds = mods.ToDictionary(m => m.Key, m => m.Value.Ms || unitless ? m.Value.Value / 1000 : m.Value.Value, StringComparer.OrdinalIgnoreCase);
            return new StartupReport(total, seconds, File.GetLastWriteTimeUtc(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    // ── The estimate ──

    /// <summary>
    /// What a mod costs at startup, roughly, from what it contains: every def is parsed and resolved, every patch
    /// operation runs over the whole XML, pictures are read from disk and uploaded, code is loaded and its static
    /// constructors run. Only the proportions matter: <see cref="Estimate"/> scales them to measured startups.
    /// </summary>
    public static double Guess(ModProfile? p) => p is null ? 0.15
        : 0.03 + p.Defs * 0.0008 + p.Patches * 0.003 + p.Textures * 0.004 + p.Assemblies * 0.12;

    /// <summary>
    /// The startup of <paramref name="active"/>. Measured mods (a report) count as measured; the others are guessed and
    /// the guesses scaled by how the measured ones compared with theirs. Without a report, the last measured total
    /// scales all guesses (the game ran with <paramref name="lastList"/> then).
    /// </summary>
    /// <param name="official">Core and the DLC: the "base game" part, never guessed per mod.</param>
    public static LoadTimeEstimate Estimate(IReadOnlyList<string> active, Func<string, bool> official, Func<string, ModProfile?> content,
        StartupReport? report, double? lastTotal, IReadOnlyList<string>? lastList)
    {
        var mods = active.Where(id => !official(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var guesses = mods.ToDictionary(id => id, id => Guess(content(id)), StringComparer.OrdinalIgnoreCase);

        if (report is not null)
        {
            var measured = mods.Where(id => report.Mods.ContainsKey(id)).ToList();
            var guessSum = measured.Sum(id => guesses[id]);
            var factor = guessSum > 0 ? Math.Clamp(measured.Sum(id => report.Mods[id]) / guessSum, 0.05, 100) : 1;
            var reportModsSum = report.Mods.Values.Sum();
            var baseSeconds = report.TotalSeconds is { } t && t > reportModsSum ? t - reportModsSum : DefaultBaseSeconds;
            var rows = mods.Select(id => report.Mods.TryGetValue(id, out var s)
                ? new ModLoadTime(id, s, true)
                : new ModLoadTime(id, guesses[id] * factor, false)).ToList();
            return Build(baseSeconds, rows, LoadTimeSource.PerMod);
        }

        if (lastTotal is { } total && lastList is { Count: > 0 })
        {
            var lastGuess = lastList.Where(id => !official(id)).Distinct(StringComparer.OrdinalIgnoreCase).Sum(id => Guess(content(id)));
            var baseSeconds = Math.Min(DefaultBaseSeconds, total * 0.5);
            var factor = lastGuess > 0 ? Math.Clamp((total - baseSeconds) / lastGuess, 0.05, 100) : 1;
            return Build(baseSeconds, mods.Select(id => new ModLoadTime(id, guesses[id] * factor, false)).ToList(), LoadTimeSource.LastStartup);
        }

        return Build(DefaultBaseSeconds, mods.Select(id => new ModLoadTime(id, guesses[id], false)).ToList(), LoadTimeSource.Guess);
    }

    private static LoadTimeEstimate Build(double baseSeconds, List<ModLoadTime> rows, LoadTimeSource source) =>
        new(baseSeconds + rows.Sum(r => r.Seconds), baseSeconds, rows.OrderByDescending(r => r.Seconds).ToList(), source,
            rows.Count(r => !r.Measured));

    /// <summary>"2:28", "45 s" style without the unit: minutes and seconds.</summary>
    public static string Clock(double seconds)
    {
        var s = (int)Math.Round(Math.Max(0, seconds));
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private static double? Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool Flag(XElement? e) => e is not null && bool.TryParse(e.Value.Trim(), out var b) && b;
}
