using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Diagnostics;
using RimForge.Core.Launch;

namespace RimForge.App.ViewModels;

/// <summary>What the culprit search looks for: a problem the player checks by eye, or an error from the log.</summary>
public sealed record BisectTarget(string Title, string? LogKey);

/// <summary>"Find the culprit mod": halving the mod list round by round with the player's help.</summary>
public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<LogFinding> _lastLogFindings = [];
    private ModBisect? _bisect;
    private List<string> _bisectOriginal = [];
    private DateTime _bisectLaunchedAt;
    private IReadOnlyList<string>? _bisectSuspects;

    public ObservableCollection<BisectTarget> BisectTargets { get; } = [];

    [ObservableProperty] public partial bool IsBisectOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBisectSetup), nameof(IsBisectTesting), nameof(IsBisectDone))]
    public partial string BisectStage { get; set; } = "setup";

    public bool IsBisectSetup => BisectStage == "setup";
    public bool IsBisectTesting => BisectStage == "testing";
    public bool IsBisectDone => BisectStage == "done";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BisectUsesLog))]
    public partial int BisectTargetIndex { get; set; }

    public bool BisectUsesLog => BisectTargetIndex > 0 && BisectTargetIndex < BisectTargets.Count && BisectTargets[BisectTargetIndex].LogKey is not null;

    [ObservableProperty] public partial string BisectRoundText { get; set; } = "";
    [ObservableProperty] public partial string BisectStatusText { get; set; } = "";
    [ObservableProperty] public partial string BisectVerdict { get; set; } = "";
    [ObservableProperty] public partial string BisectResultText { get; set; } = "";
    [ObservableProperty] public partial bool BisectFoundCulprit { get; set; }
    [ObservableProperty] public partial bool BisectCanUndo { get; set; }
    [ObservableProperty] public partial string BisectScopeText { get; set; } = "";
    [ObservableProperty] public partial bool BisectHasScope { get; set; }

    [RelayCommand]
    private void OpenBisect() => OpenBisectWith(null, null);

    /// <summary>Opens the culprit search, optionally limited to <paramref name="suspects"/> and aimed at one log error.</summary>
    private void OpenBisectWith(IReadOnlyList<string>? suspects, string? logKey)
    {
        if (_bisect is not null && !IsBisectDone)
        {
            IsBisectOpen = true; // a search is already running — just show it again
            return;
        }
        _bisectSuspects = suspects;
        BisectHasScope = suspects is not null;
        BisectScopeText = suspects is null ? "" : Loc.Format("Bisect.Scope", suspects.Count, (int)Math.Ceiling(Math.Log2(Math.Max(2, suspects.Count))), Names(suspects));

        BisectTargets.Clear();
        BisectTargets.Add(new BisectTarget(Loc.Get("Bisect.Manual"), null));
        foreach (var f in _lastLogFindings.Where(f => f.Severity >= FindingSeverity.Medium || f.Key == logKey).Take(20))
            BisectTargets.Add(new BisectTarget(Loc.Format("Bisect.LogTarget", Shorten(f.Message)), f.Key));
        var target = Math.Max(0, BisectTargets.ToList().FindIndex(t => t.LogKey is not null && t.LogKey == logKey));
        BisectTargetIndex = -1; // the same value would not be re-sent to the picker after its items changed
        BisectTargetIndex = target;
        BisectStage = "setup";
        IsBisectOpen = true;
    }

    private static string Shorten(string text) => text.Length > 90 ? text[..90] + "…" : text;

    [RelayCommand]
    private async Task StartBisect()
    {
        if (!await ConfirmLeaveAsync()) return;
        _bisectOriginal = Snapshot();
        var official = _bisectOriginal.Where(id => _installed.TryGetValue(id, out var m) && m.IsOfficial);
        _bisect = new ModBisect(_bisectOriginal, _installed, official, _bisectSuspects);
        BisectStage = "testing";
        ApplyBisectRound();
    }

    private void ApplyBisectRound()
    {
        if (_bisect is null) return;
        BisectVerdict = "";
        BisectCanUndo = _bisect.CanUndo;
        if (_bisect.IsDone)
        {
            ShowBisectResult();
            return;
        }
        try
        {
            WriteGameList(_bisect.TestOrder.ToList());
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ex.Message);
        }
        BisectRoundText = Loc.Format("Bisect.Round", _bisect.Round, _bisect.RoundsLeft, _bisect.Candidates.Count);
        BisectStatusText = Loc.Format("Bisect.Status", _bisect.TestOrder.Count, _bisect.TestedSuspects.Count);
    }

    private void ShowBisectResult()
    {
        BisectStage = "done";
        BisectFoundCulprit = _bisect?.Culprit is not null;
        BisectResultText = _bisect?.Culprit is { } culprit
            ? Loc.Format("Bisect.Found", NameOf(culprit))
            : Loc.Format("Bisect.NotFound", Names(_bisect?.Candidates ?? []));
    }

    [RelayCommand]
    private async Task BisectLaunchGame()
    {
        if (GameLauncher.IsRunning())
        {
            ShowToast(Loc.Get("Msg.GameRunning"));
            return;
        }
        if (!await ReadyForGameVersionAsync()) return;
        try
        {
            await _steam.StopForGameAsync();
            _bisectLaunchedAt = DateTime.Now;
            GameLauncher.Launch(_paths, LaunchArguments());
            ShowToast(Loc.Get("Msg.Launching"));
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ex.Message);
        }
    }

    [RelayCommand]
    private void BisectCheckLog()
    {
        if (!BisectUsesLog || _paths.PlayerLogFile is not { } file || !File.Exists(file)) return;
        if (File.GetLastWriteTime(file) < _bisectLaunchedAt)
        {
            BisectVerdict = Loc.Get("Bisect.LogOld");
            return;
        }
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var text = new StreamReader(stream).ReadToEnd();
        var key = BisectTargets[BisectTargetIndex].LogKey!;
        BisectVerdict = Loc.Get(LogAnalyzer.Contains(text, key) ? "Bisect.LogHas" : "Bisect.LogHasNot");
    }

    [RelayCommand]
    private void BisectAnswer(string answer)
    {
        if (_bisect is null) return;
        _bisect.Answer(answer == "yes");
        ApplyBisectRound();
    }

    [RelayCommand]
    private void BisectUndo()
    {
        if (_bisect is null) return;
        _bisect.Undo();
        BisectStage = "testing";
        ApplyBisectRound();
    }

    /// <summary>Ends the search and puts the player's own list back into the game.</summary>
    [RelayCommand]
    private void BisectRestore()
    {
        EndBisect();
        ShowToast(Loc.Get("Msg.BisectRestored"));
    }

    [RelayCommand]
    private void BisectDisableCulprit()
    {
        var culprit = _bisect?.Culprit;
        EndBisect();
        if (culprit is null || !_items.TryGetValue(culprit, out var item)) return;
        Deactivate([item]);
        OpenMod(culprit);
        ShowToast(Loc.Format("Msg.BisectDisabled", item.Name));
    }

    [RelayCommand]
    private void BisectOpenCulprit()
    {
        var culprit = _bisect?.Culprit;
        EndBisect();
        if (culprit is not null) OpenMod(culprit);
    }

    private void EndBisect()
    {
        if (_bisect is not null)
        {
            try
            {
                WriteGameList(_bisectOriginal);
            }
            catch (Exception ex)
            {
                Message = Loc.Format("Msg.Error", ex.Message);
            }
            Restore(_bisectOriginal);
        }
        _bisect = null;
        IsBisectOpen = false;
        BisectStage = "setup";
    }

    /// <summary>Hides the window; a running search keeps going and "Find the culprit" brings it back.</summary>
    [RelayCommand]
    private void HideBisect() => IsBisectOpen = false;
}
