using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Diagnostics;
using RimForge.Core.Saves;

namespace RimForge.App.ViewModels;

/// <summary>A mod named by a finding; clicking it opens the mod in the list.</summary>
public sealed record ModChip(string Id, string Name, string Tip);

/// <summary>One button under a finding that fixes it (or hides it).</summary>
public sealed record FixOptionViewModel(FindingViewModel Finding, FixOption Option, string Title, string Tip)
{
    public bool IsSafe => Option.IsSafe;
}

/// <summary>One diagnostics finding, already in plain words.</summary>
public sealed partial class FindingViewModel : ObservableObject
{
    /// <summary>Identifies the finding across checks, for "don't show again".</summary>
    public required string Key { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; set; }
    public required IReadOnlyList<ModChip> Mods { get; init; }
    public required string Details { get; init; }
    public int Count { get; init; } = 1;
    public string ModsLabel { get; init; } = "";
    public bool IsIgnored { get; init; }
    public IReadOnlyList<FixOptionViewModel> Fixes { get; set; } = [];

    public bool IsHigh => Severity == FindingSeverity.High;
    public bool IsMedium => Severity == FindingSeverity.Medium;
    public bool IsMinor => Severity <= FindingSeverity.Low;
    public bool HasMods => Mods.Count > 0;
    public bool HasCount => Count > 1;
    public string CountText => "×" + Count;

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixes))]
    public partial bool IsFixed { get; set; }

    public bool ShowFixes => !IsFixed && Fixes.Count > 0;

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;
}

/// <summary>The Diagnostics page: code and XML conflicts between mods plus errors from the game log.</summary>
public sealed partial class MainWindowViewModel
{
    private readonly List<FindingViewModel> _logFindings = [];
    private readonly List<FindingViewModel> _codeFindings = [];
    private readonly List<FindingViewModel> _dataFindings = [];
    private CancellationTokenSource? _scanCancel;
    private ModIndex? _activeIndex;
    private ModIndex? _inactiveIndex;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _saveUsage = new Dictionary<string, IReadOnlyList<string>>();

    public ObservableCollection<FindingViewModel> VisibleFindings { get; } = [];

    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial double ScanProgress { get; set; }
    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial string ReportInfo { get; set; } = "";
    [ObservableProperty] public partial string HighText { get; set; } = "";
    [ObservableProperty] public partial string MediumText { get; set; } = "";
    [ObservableProperty] public partial string MinorText { get; set; } = "";
    [ObservableProperty] public partial string HiddenText { get; set; } = "";
    [ObservableProperty] public partial string LogTabText { get; set; } = "";
    [ObservableProperty] public partial string CodeTabText { get; set; } = "";
    [ObservableProperty] public partial string DataTabText { get; set; } = "";
    [ObservableProperty] public partial bool SectionIsEmpty { get; set; }

    /// <summary>Set after a fix: the report describes the list as it was before.</summary>
    [ObservableProperty] public partial bool IsReportStale { get; set; }

    /// <summary>0 = the last game run (Player.log), 1 = the run before it (Player-prev.log).</summary>
    [ObservableProperty] public partial int LogChoice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogSection), nameof(IsCodeSection), nameof(IsDataSection))]
    public partial string DiagSection { get; set; } = "log";

    [ObservableProperty] public partial bool ShowMinor { get; set; }
    [ObservableProperty] public partial bool ShowIgnored { get; set; }

    public bool IsLogSection => DiagSection == "log";
    public bool IsCodeSection => DiagSection == "code";
    public bool IsDataSection => DiagSection == "data";

    /// <summary>Labels for the log picker; rebuilt whenever the page opens because the dates change after every game run.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> LogOptions { get; set; } = [];

    private string LogOption(string file, string key)
    {
        var path = _paths.ConfigDir is null ? null : Path.Combine(_paths.ConfigDir, file);
        return path is not null && File.Exists(path)
            ? Loc.Format(key, Dates.Format(File.GetLastWriteTime(path)))
            : Loc.Format(key, "—");
    }

    partial void OnCurrentPageChanged(string value)
    {
        if (value == "workshop") OnWorkshopPageShown();
        if (value != "diagnostics") return;
        var choice = Math.Max(0, LogChoice);
        LogOptions = [LogOption("Player.log", "Diag.LogLast"), LogOption("Player-prev.log", "Diag.LogPrevious")];
        // The picker drops its selection when the list is replaced; put it back once it has settled.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogChoice = -1; // the same value would not be re-sent to the picker
            LogChoice = choice;
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    partial void OnDiagSectionChanged(string value) => RefreshVisibleFindings();
    partial void OnShowMinorChanged(bool value) => RefreshVisibleFindings();
    partial void OnShowIgnoredChanged(bool value) => RefreshVisibleFindings();

    [RelayCommand]
    private void ShowDiagSection(string section) => DiagSection = section;

    [RelayCommand]
    private async Task RunDiagnostics()
    {
        if (IsScanning || _gameVersion is null) return;
        _scanCancel = new CancellationTokenSource();
        IsScanning = true;
        ScanProgress = 0;
        var order = Snapshot();
        var inactiveIds = _installed.Keys.Where(id => !order.Contains(id)).ToList();
        var configDir = _paths.ConfigDir;
        var logFile = configDir is null ? null : Path.Combine(configDir, LogChoice == 1 ? "Player-prev.log" : "Player.log");
        try
        {
            var progress = new Progress<double>(p => ScanProgress = p * 90);
            var result = await Task.Run(() =>
            {
                var active = ModIndex.Build(order, _installed, _gameVersion, progress, _scanCancel.Token);
                // Installed mods that are turned off: where missing templates, types and objects are looked for.
                var inactive = ModIndex.Build(inactiveIds, _installed, _gameVersion, cancel: _scanCancel.Token);
                // The log may be locked while the game runs; a shared read still works.
                string? text = null;
                if (logFile is not null && File.Exists(logFile))
                {
                    using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    text = new StreamReader(stream).ReadToEnd();
                }
                var logs = text is null ? [] : LogAnalyzer.Analyze(text, active);
                return (active, inactive, logs, ConflictAnalyzer.HarmonyOverlaps(active), ConflictAnalyzer.DefOverrides(active),
                    ConflictAnalyzer.PatchClashes(active), SaveUsage.Find(configDir), TextureConflicts.Find(order, _installed, _gameVersion));
            });

            (_activeIndex, _inactiveIndex, _lastLogFindings, var overlaps, var overrides, var clashes, _saveUsage, var textures) = result;
            var activeSet = order.ToHashSet();
            var ignored = ActiveProfile?.Model.Ignored.ToHashSet() ?? [];

            _logFindings.Clear();
            _logFindings.AddRange(_lastLogFindings.Select(f =>
                WithFixes(ToFinding(f, ignored), FixPlanner.ForLog(f, _activeIndex, _inactiveIndex, _installed, activeSet))));
            _codeFindings.Clear();
            _codeFindings.AddRange(overlaps.Select(o => WithFixes(ToFinding(o, ignored), FixPlanner.ForOverlap(o))));
            _dataFindings.Clear();
            _dataFindings.AddRange(clashes.Select(c => WithFixes(ToFinding(c, ignored), FixPlanner.ForClash(c)))
                .Concat(overrides.Select(o => WithFixes(ToFinding(o, ignored), FixPlanner.ForOverride(o))))
                .Concat(textures.Select(c => ToFinding(c, ignored)))
                .OrderByDescending(f => f.Severity));

            var logDate = logFile is not null && File.Exists(logFile) ? Dates.Format(File.GetLastWriteTime(logFile)) : "—";
            ReportInfo = Loc.Format("Diag.Info", _activeIndex.LoadOrder.Count, _activeIndex.AssemblyCount, _activeIndex.FileCount, logDate);
            HasReport = true;
            IsReportStale = false;
            RefreshVisibleFindings();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ex.Message);
        }
        finally
        {
            IsScanning = false;
            ScanProgress = 100;
        }
    }

    private IEnumerable<FindingViewModel> AllFindings => _logFindings.Concat(_codeFindings).Concat(_dataFindings);

    private void RefreshVisibleFindings()
    {
        bool Shown(FindingViewModel f) => (ShowMinor || !f.IsMinor) && (ShowIgnored || !f.IsIgnored);

        var counted = AllFindings.Where(f => !f.IsIgnored).ToList();
        HighText = Loc.Format("Diag.High", counted.Count(f => f.IsHigh));
        MediumText = Loc.Format("Diag.Medium", counted.Count(f => f.IsMedium));
        MinorText = Loc.Format("Diag.Minor", counted.Count(f => f.IsMinor));
        HiddenText = Loc.Format("Diag.Hidden", AllFindings.Count(f => f.IsIgnored));

        LogTabText = Loc.Format("Diag.TabLog", _logFindings.Count(Shown));
        CodeTabText = Loc.Format("Diag.TabCode", _codeFindings.Count(Shown));
        DataTabText = Loc.Format("Diag.TabData", _dataFindings.Count(Shown));

        var source = DiagSection switch { "code" => _codeFindings, "data" => _dataFindings, _ => _logFindings };
        VisibleFindings.Clear();
        foreach (var f in source.Where(Shown).Take(300)) VisibleFindings.Add(f);
        SectionIsEmpty = HasReport && VisibleFindings.Count == 0;
    }

    /// <summary>Opens a mod from a finding on the Mods page.</summary>
    [RelayCommand]
    private void OpenMod(string id)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        CurrentPage = "mods";
        AvailableSearch = "";
        ActiveSearch = "";
        if (item.IsActive) SelectedActive = item;
        else SelectedAvailable = item;
        SelectedMod = item;
    }

    // ── Plain-language texts ──

    private ModChip Chip(string id, string reasonKey) => new(id, NameOf(id), Loc.Get(reasonKey));

    private static string ShortTarget(string target)
    {
        // "Verse.PawnGenerator.GeneratePawn" → "PawnGenerator.GeneratePawn"
        var parts = target.Split('.');
        return parts.Length > 2 ? string.Join('.', parts[^2..]) : target;
    }

    private string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => $"«{NameOf(id)}»"));

    private FindingViewModel ToFinding(LogFinding f, HashSet<string> ignored)
    {
        var name = f.MissingName ?? "";
        var key = "log:" + f.Key;
        return new FindingViewModel
        {
            Key = key,
            IsIgnored = ignored.Contains(key),
            Severity = f.Severity,
            Title = f.Kind == LogErrorKind.XmlError && name.Length > 0
                ? Loc.Format("Diag.Log.XmlErrorNamed", name)
                : Loc.Format("Diag.Log." + f.Kind, name),
            Explanation = Loc.Get("Diag.Log." + f.Kind + ".Hint"),
            Mods = f.Suspects.Take(8).Select(s => Chip(s.ModId, "Diag.Why." + s.Reason)).ToList(),
            Details = f.Details,
            Count = f.Count,
            ModsLabel = Loc.Get("Diag.Suspects"),
        };
    }

    private FindingViewModel ToFinding(HarmonyOverlap o, HashSet<string> ignored)
    {
        var target = ShortTarget(o.Target);
        var skipper = o.Patches.FirstOrDefault(p => p.CanSkipOriginal)?.ModId;
        var explanation = o.Risk switch
        {
            HarmonyRisk.CanSkip => Loc.Format("Diag.Code.CanSkip.Hint", NameOf(skipper), Names(o.ModIds.Where(m => m != skipper))),
            HarmonyRisk.Transpilers => Loc.Format("Diag.Code.Transpilers.Hint", Names(o.Patches.Where(p => p.Kind == PatchKind.Transpiler).Select(p => p.ModId).Distinct())),
            _ => Loc.Format("Diag.Code.Shared.Hint", Names(o.ModIds)),
        };
        var key = "code:" + o.Target;
        return new FindingViewModel
        {
            Key = key,
            IsIgnored = ignored.Contains(key),
            Severity = o.Severity,
            Title = Loc.Format("Diag.Code." + o.Risk, target),
            Explanation = explanation,
            Mods = o.ModIds.Select(id => Chip(id, "Diag.Why.Patches")).ToList(),
            ModsLabel = Loc.Get("Diag.Mods"),
            Details = o.Target + "\n" + string.Join('\n', o.Patches.Select(p =>
                $"  {NameOf(p.ModId)}: {p.Kind}{(p.CanSkipOriginal ? " (" + Loc.Get("Diag.CanSkipMark") + ")" : "")} — {p.PatchMethod}")),
        };
    }

    private FindingViewModel ToFinding(DefOverride o, HashSet<string> ignored)
    {
        var key = $"def:{o.DefType}/{o.DefName}";
        return new FindingViewModel
        {
            Key = key,
            IsIgnored = ignored.Contains(key),
            Severity = o.Severity,
            Title = Loc.Format(o.Severity == FindingSeverity.Info ? "Diag.Data.OverrideGame" : "Diag.Data.Override", o.DefName, o.DefType),
            Explanation = Loc.Format("Diag.Data.Override.Hint", Names(o.ModIds), NameOf(o.Winner)),
            Mods = o.ModIds.Select(id => Chip(id, id == o.Winner ? "Diag.Why.Winner" : "Diag.Why.Declares")).ToList(),
            ModsLabel = Loc.Get("Diag.Mods"),
            Details = $"{o.DefType} {o.DefName}\n" + string.Join(" → ", o.ModIds.Select(id => NameOf(id))),
        };
    }

    /// <summary>Mods with pictures under the same paths: the game shows the last one's.</summary>
    private FindingViewModel ToFinding(TextureClash c, HashSet<string> ignored)
    {
        var key = "tex:" + string.Join('|', c.ModIds);
        var shown = c.Paths.Take(12).ToList();
        return new FindingViewModel
        {
            Key = key,
            IsIgnored = ignored.Contains(key),
            Severity = c.Severity,
            Title = Loc.Format(c.Intended ? "Diag.Data.TextureIntended" : "Diag.Data.Texture", c.Paths.Count, NameOf(c.Winner)),
            Explanation = Loc.Format(c.Intended ? "Diag.Data.TextureIntended.Hint" : "Diag.Data.Texture.Hint", Names(c.ModIds), NameOf(c.Winner)),
            Mods = c.ModIds.Select(id => Chip(id, id == c.Winner ? "Diag.Why.Winner" : "Diag.Why.Textures")).ToList(),
            ModsLabel = Loc.Get("Diag.Mods"),
            Details = string.Join(" → ", c.ModIds.Select(id => NameOf(id))) + "\n" + string.Join('\n', shown.Select(s => "Textures/" + s))
                      + (c.Paths.Count > shown.Count ? "\n" + Loc.Format("Diag.Data.TextureMore", c.Paths.Count - shown.Count) : ""),
        };
    }

    private FindingViewModel ToFinding(PatchClash c, HashSet<string> ignored)
    {
        var winner = c.Patches[^1].ModId;
        var key = $"clash:{c.DefName}/{c.Field}";
        return new FindingViewModel
        {
            Key = key,
            IsIgnored = ignored.Contains(key),
            Severity = c.Severity,
            Title = Loc.Format("Diag.Data.Clash", c.DefName, c.Field.Length == 0 ? "—" : c.Field),
            Explanation = Loc.Format("Diag.Data.Clash.Hint", Names(c.ModIds), NameOf(winner)),
            Mods = c.ModIds.Select(id => Chip(id, id == winner ? "Diag.Why.Winner" : "Diag.Why.Patches")).ToList(),
            ModsLabel = Loc.Get("Diag.Mods"),
            Details = string.Join('\n', c.Patches.Select(p => $"{NameOf(p.ModId)}: {p.Operation}\n  {p.XPath}\n  {p.File}")),
        };
    }
}
