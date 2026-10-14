using System.Text.RegularExpressions;

namespace RimForge.Core.Diagnostics;

public enum LogErrorKind
{
    /// <summary>Code crashed (an exception). The most serious kind.</summary>
    Exception,
    /// <summary>A mod's XML refers to a parent template that does not exist.</summary>
    MissingParent,
    /// <summary>A mod's XML uses a type (C# class) that is not loaded.</summary>
    MissingType,
    /// <summary>A def refers to another def that does not exist.</summary>
    MissingReference,
    /// <summary>Broken XML or a def that failed its own config check.</summary>
    XmlError,
    /// <summary>A texture or sound could not be loaded.</summary>
    Asset,
    /// <summary>Translation errors.</summary>
    Translation,
    /// <summary>Two key bindings share a key.</summary>
    KeyBinding,
    Other,
}

public enum SuspectReason
{
    /// <summary>The error mentions a file inside the mod's folder.</summary>
    Path,
    /// <summary>The error is about an XML file that only this mod has.</summary>
    File,
    /// <summary>The broken object (defName) is declared by this mod.</summary>
    Def,
    /// <summary>The mod's code is in the error's stack trace.</summary>
    Code,
    /// <summary>Harmony reports this mod patched a method in the stack trace.</summary>
    HarmonyPatch,
    /// <summary>The broken object belongs to the game, and this mod's patches change it.</summary>
    PatchesDef,
    /// <summary>This mod refers to the object that is missing.</summary>
    Mentions,
}

public sealed record LogSuspect(string ModId, SuspectReason Reason);

/// <summary>One kind of error from the game log (repeats are counted, not listed).</summary>
/// <param name="Key">Identifies the same problem across different logs (used to recheck it after a game start).</param>
public sealed record LogFinding(LogErrorKind Kind, FindingSeverity Severity, string Message, int Count, string Details,
    IReadOnlyList<LogSuspect> Suspects, string? MissingName, string Key);

/// <summary>Reads Player.log, groups the errors and works out which mods are behind them.</summary>
public static partial class LogAnalyzer
{
    public static IReadOnlyList<LogFinding> Analyze(string logText, ModIndex? index)
    {
        var lines = logText.Replace("\r\n", "\n").Split('\n');
        var raw = new List<(LogErrorKind Kind, string Message, string Details)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var kind = Classify(line);
            if (kind is null) continue;

            // An error's block runs until the next empty line: stack traces, file names, Harmony notes.
            var block = new List<string> { line };
            var j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length > 0 && Classify(lines[j].TrimEnd()) is null && j - i < 80)
                block.Add(lines[j++].TrimEnd());
            i = j - 1;

            // "[Def Error]: Name" only says which def; the next line is its file — keep both in the message.
            var message = kind == LogErrorKind.XmlError && line.StartsWith("[Def Error]") && block.Count > 1 ? line + " — " + block[1].Trim() : line;
            raw.Add((kind.Value, message, string.Join('\n', block)));
        }

        var findings = raw
            // Errors about the same missing thing are one problem, even if the failing XML nodes differ.
            .GroupBy(r => (r.Kind, Key: MissingName(r.Message) is { } name && r.Kind != LogErrorKind.Exception ? name : Normalize(r.Message)))
            .Select(g =>
            {
                var first = g.First();
                return new LogFinding(g.Key.Kind, SeverityOf(g.Key.Kind), first.Message, g.Count(), first.Details,
                    index is null ? [] : Blame(first.Details, index, g.Key.Kind), MissingName(first.Message), g.Key.Kind + ":" + g.Key.Key);
            })
            .ToList();

        // "No X named Y": nobody owns Y, so look for the mods that refer to it.
        if (index is not null)
        {
            var missing = findings.Where(f => f.Kind == LogErrorKind.MissingReference && f.MissingName is not null)
                .Select(f => f.MissingName!).Distinct().Take(50).ToList();
            var mentions = index.ModsMentioning(missing);
            findings = findings.Select(f =>
                f.Kind == LogErrorKind.MissingReference && f.MissingName is { } name
                && mentions.TryGetValue(name, out var mods) && mods.Count is > 0 and <= 5
                    ? f with { Suspects = [.. f.Suspects, .. mods.Where(m => f.Suspects.All(s => s.ModId != m)).Select(m => new LogSuspect(m, SuspectReason.Mentions))] }
                    : f).ToList();
        }

        return findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Count).ToList();
    }

    /// <summary>Whether <paramref name="logText"/> still contains the problem identified by <paramref name="key"/>.</summary>
    public static bool Contains(string logText, string key) => Analyze(logText, null).Any(f => f.Key == key);

    private static FindingSeverity SeverityOf(LogErrorKind kind) => kind switch
    {
        LogErrorKind.Exception => FindingSeverity.High,
        LogErrorKind.MissingParent or LogErrorKind.MissingType or LogErrorKind.XmlError => FindingSeverity.Medium,
        LogErrorKind.MissingReference or LogErrorKind.Asset => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    // ── What counts as an error ──

    [GeneratedRegex(@"^(\S+\.)*\w*Exception\b|\bException (in|ticking|drawing|filling|during|while|thrown|caught|occurred)|^Error (in|while|loading|trying|calling)|^Exception\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionLine();

    private static LogErrorKind? Classify(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || char.IsWhiteSpace(line.FirstOrDefault())) return null; // continuation lines are indented
        if (t.StartsWith("XML error: Could not find parent node")) return LogErrorKind.MissingParent;
        if (t.StartsWith("Type ") && t.Contains("is not a Def type or could not be found")) return LogErrorKind.MissingType;
        if (t.StartsWith("Could not find type named")) return LogErrorKind.MissingType;
        if (t.StartsWith("Could not resolve cross-reference")) return LogErrorKind.MissingReference;
        if (t.StartsWith("XML error") || t.StartsWith("[Def Error]") || t.StartsWith("Config error") || t.StartsWith("XML ") && t.Contains("error"))
            return LogErrorKind.XmlError;
        if (t.StartsWith("DDS loading failed") || t.StartsWith("Could not load") && (t.Contains("texture") || t.Contains("Texture")))
            return LogErrorKind.Asset;
        if (Regex.IsMatch(t, @"^Translation data for language .* has \d+ errors")) return LogErrorKind.Translation;
        if (t.StartsWith("Key binding conflict")) return LogErrorKind.KeyBinding;
        if (ExceptionLine().IsMatch(t)) return LogErrorKind.Exception;
        return null;
    }

    /// <summary>Numbers, memory addresses and Harmony refs differ between repeats of the same error.</summary>
    private static string Normalize(string message)
    {
        var s = Regex.Replace(message, @"0x[0-9A-Fa-f]+|\[Ref [0-9A-F]+\]|\d+", "#");
        return s.Length > 300 ? s[..300] : s;
    }

    [GeneratedRegex("""No \S+ named (\S+) found|Type (\S+) is not a Def type|Could not find parent node named "([^"]+)"|Could not find type named (\S+)|^\[Def Error\]:\s*(\S+)|^Config error in (\S+)""")]
    private static partial Regex MissingNamePattern();

    private static string? MissingName(string message)
    {
        var m = MissingNamePattern().Match(message);
        return m.Success ? m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.TrimEnd('.', ',') : null;
    }

    // ── Who is behind it ──

    [GeneratedRegex(@"workshop[\\/]content[\\/]294100[\\/](\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkshopPath();

    [GeneratedRegex(@"[\\/]Mods[\\/]([^\\/]+)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex LocalModPath();

    /// <summary>Relative paths the game prints, like "Carniculus\1.6\Defs\..." or "Core\Defs\...".</summary>
    [GeneratedRegex(@"(?:^|\s)([^\\/\s:]+)[\\/](?:[\w.]+[\\/])*?(?:Defs|Patches|Textures|Sounds|Languages)[\\/]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RelativeModPath();

    [GeneratedRegex(@"in file ([^\s,]+\.xml)", RegexOptions.IgnoreCase)]
    private static partial Regex XmlFileName();

    [GeneratedRegex(@"<defName>\s*([^<\s]+)\s*</defName>")]
    private static partial Regex DefNameTag();

    [GeneratedRegex(@"(?:Type |to give to |type named )([A-Za-z_][\w.+]*)")]
    private static partial Regex TypeMention();

    /// <summary>Stack frames: "at Ns.Type.Method (" (.NET style) or "Ns.Type:Method (" (Unity style).</summary>
    [GeneratedRegex(@"^\s*(?:at\s+)?(?:\(wrapper [^)]*\)\s+)?([A-Za-z_][\w.+`<>]*)[.:]([\w<>`.|]+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex StackFrame();

    /// <summary>Harmony's notes under a patched frame: "    - PREFIX some.harmony.id: Ns.Type.Method(...)".</summary>
    [GeneratedRegex(@"^\s*-\s*(?:PREFIX|POSTFIX|TRANSPILER|FINALIZER)\s+[^:]+:\s*(?:[\w.<>`\[\], ]+\s)?([A-Za-z_][\w.+`<>]*)\.[\w<>`]+\(", RegexOptions.Multiline)]
    private static partial Regex HarmonyNote();

    private static readonly string[] GameNamespaces =
        ["Verse", "RimWorld", "UnityEngine", "Unity", "System", "Mono", "HarmonyLib", "MonoMod", "LudeonTK", "Steamworks", "TMPro", "Ionic", "NAudio", "RuntimeAudioClipLoader", "DynamicClass"];

    private static bool IsGameType(string type) =>
        GameNamespaces.Any(ns => type == ns || type.StartsWith(ns + ".", StringComparison.Ordinal));

    /// <summary>
    /// Mods behind an error, most likely first. For crashes the stack trace is the best evidence;
    /// for XML problems it is the file or object the error is about.
    /// </summary>
    public static IReadOnlyList<LogSuspect> Blame(string text, ModIndex index, LogErrorKind kind = LogErrorKind.Other)
    {
        var suspects = new List<LogSuspect>();
        void Add(string? modId, SuspectReason reason)
        {
            if (modId is not null && index.Mods.ContainsKey(modId) && suspects.All(s => s.ModId != modId))
                suspects.Add(new LogSuspect(modId, reason));
        }

        if (kind == LogErrorKind.Exception)
        {
            FromCode(text, index, Add);
            FromFiles(text, index, Add);
        }
        else
        {
            FromFiles(text, index, Add);
            FromCode(text, index, Add);
        }
        return suspects;
    }

    private static void FromCode(string text, ModIndex index, Action<string?, SuspectReason> add)
    {
        // Harmony notes are the strongest evidence for crashes: the mod wrapped the crashing method.
        foreach (Match m in HarmonyNote().Matches(text))
            foreach (var id in index.ModsOfType(m.Groups[1].Value)) add(id, SuspectReason.HarmonyPatch);

        foreach (Match m in StackFrame().Matches(text))
        {
            var type = m.Groups[1].Value;
            if (IsGameType(type)) continue;
            foreach (var id in index.ModsOfType(type)) add(id, SuspectReason.Code);
        }

        foreach (Match m in TypeMention().Matches(text))
        {
            var type = m.Groups[1].Value.TrimEnd('.');
            if (IsGameType(type)) continue;
            foreach (var id in index.ModsOfType(type)) add(id, SuspectReason.Code);
        }
    }

    [GeneratedRegex(@"^\[Def Error\]:\s*(\S+)|^Config error in (\S+)", RegexOptions.Multiline)]
    private static partial Regex BrokenDefName();

    private static void FromFiles(string text, ModIndex index, Action<string?, SuspectReason> add)
    {
        // A broken def of the game itself is almost always broken by a mod patching it.
        foreach (Match m in BrokenDefName().Matches(text))
        {
            var name = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).TrimEnd(':', '.');
            foreach (var id in index.ModsPatching(name).OrderBy(index.PositionOf)) add(id, SuspectReason.PatchesDef);
        }

        foreach (Match m in WorkshopPath().Matches(text)) add(index.ModOfFolder(m.Groups[1].Value), SuspectReason.Path);
        foreach (Match m in LocalModPath().Matches(text)) add(index.ModOfFolder(m.Groups[1].Value), SuspectReason.Path);
        foreach (Match m in RelativeModPath().Matches(text)) add(index.ModOfFolder(m.Groups[1].Value), SuspectReason.Path);

        foreach (Match m in XmlFileName().Matches(text))
        {
            var mods = index.ModsWithFile(Path.GetFileName(m.Groups[1].Value));
            if (mods.Count is > 0 and <= 2) foreach (var id in mods) add(id, SuspectReason.File);
        }

        // The defName inside a broken XML node belongs to the mod that wrote the node.
        foreach (Match m in DefNameTag().Matches(text))
        {
            var mods = index.ModsDefining(m.Groups[1].Value);
            if (mods.Count is > 0 and <= 2) foreach (var id in mods) add(id, SuspectReason.Def);
        }
    }
}
