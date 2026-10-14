using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Launch;
using RimForge.Core.Storage;

namespace RimForge.App.ViewModels;

/// <summary>One mod in the startup breakdown.</summary>
public sealed record LoadTimeRow(string Name, string Time, double Share, bool Measured, string Tip);

/// <summary>
/// How long the game will take to start: next to "Play", and a breakdown by mod on a click. Measured by the
/// Loading Progress mod when it is there (RimForge can turn its per-mod timing on), estimated from content otherwise.
/// </summary>
public sealed partial class MainWindowViewModel
{
    [ObservableProperty] public partial string LoadTimeText { get; set; } = "";
    [ObservableProperty] public partial string LoadTimeTip { get; set; } = "";
    [ObservableProperty] public partial bool HasLoadTime { get; set; }
    [ObservableProperty] public partial bool IsLoadTimeOpen { get; set; }
    [ObservableProperty] public partial string LoadTimeSourceText { get; set; } = "";
    [ObservableProperty] public partial string LoadTimeBaseText { get; set; } = "";

    /// <summary>What to do for exact figures: get Loading Progress, turn it on, or turn its timing on.</summary>
    [ObservableProperty] public partial string LoadTimeAction { get; set; } = "";
    [ObservableProperty] public partial string LoadTimeActionHint { get; set; } = "";

    public ObservableCollection<LoadTimeRow> LoadTimeRows { get; } = [];
    public bool HasLoadTimeAction => LoadTimeAction.Length > 0;

    partial void OnLoadTimeActionChanged(string value) => OnPropertyChanged(nameof(HasLoadTimeAction));

    private DispatcherTimer? _loadTimeTimer;
    private LoadTimeEstimate? _loadTime;

    /// <summary>Works it out again a moment after the list stops changing.</summary>
    private void ScheduleLoadTimeRefresh()
    {
        _loadTimeTimer?.Stop();
        _loadTimeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _loadTimeTimer?.Stop();
            RefreshLoadTime();
        });
        _loadTimeTimer.Start();
    }

    private Core.Models.ModInfo? LoadingProgressMod => _installed.GetValueOrDefault(LoadTimes.LoadingProgressId);

    private LoadingProgressState? LoadingProgressNow() =>
        _paths.ConfigDir is { } config && LoadingProgressMod is { } lp ? LoadTimes.ReadLoadingProgress(config, Path.GetFileName(lp.FolderPath)) : null;

    private void RefreshLoadTime()
    {
        if (NeedsGameDir || _paths.ConfigDir is not { } config)
        {
            HasLoadTime = false;
            return;
        }
        var lp = LoadingProgressNow();
        var report = lp is not null ? LoadTimes.ReadReport(config) : null;
        var active = Snapshot();
        _loadTime = LoadTimes.Estimate(active, id => _installed.TryGetValue(id, out var m) && m.IsOfficial, id => ContentOf(id),
            report, lp?.LastLoadSeconds, _gameOrder);

        var approx = _loadTime.Source == LoadTimeSource.PerMod && _loadTime.Estimated == 0 ? "" : "~";
        LoadTimeText = approx + LoadTimes.Clock(_loadTime.TotalSeconds);
        LoadTimeSourceText = _loadTime.Source switch
        {
            LoadTimeSource.PerMod when _loadTime.Estimated > 0 => Loc.Format("Lt.Source.PerModNew", _loadTime.Estimated),
            LoadTimeSource.PerMod => Loc.Get("Lt.Source.PerMod"),
            LoadTimeSource.LastStartup => Loc.Format("Lt.Source.Last", LoadTimes.Clock(lp!.LastLoadSeconds!.Value)),
            _ => Loc.Get("Lt.Source.Guess"),
        };
        LoadTimeTip = Loc.Format("Lt.Tip", LoadTimes.Clock(_loadTime.TotalSeconds)) + "\n" + LoadTimeSourceText;
        LoadTimeBaseText = Loc.Format("Lt.Base", LoadTimes.Clock(_loadTime.BaseSeconds));
        HasLoadTime = true;

        var lpActive = active.Contains(LoadTimes.LoadingProgressId);
        (LoadTimeAction, LoadTimeActionHint) =
            LoadingProgressMod is null ? (Loc.Get("Lt.Act.Get"), Loc.Get("Lt.Act.Get.Hint"))
            : !lpActive ? (Loc.Get("Lt.Act.Enable"), Loc.Get("Lt.Act.Enable.Hint"))
            : lp is { Ready: false } ? (Loc.Get("Lt.Act.Track"), Loc.Get("Lt.Act.Track.Hint"))
            : report is null ? ("", Loc.Get("Lt.Act.WaitLaunch"))
            : ("", "");

        if (IsLoadTimeOpen) FillLoadTimeRows();
    }

    private void FillLoadTimeRows()
    {
        LoadTimeRows.Clear();
        if (_loadTime is null) return;
        var top = _loadTime.Mods.FirstOrDefault()?.Seconds ?? 0;
        foreach (var m in _loadTime.Mods)
            LoadTimeRows.Add(new LoadTimeRow(NameOf(m.PackageId), Seconds(m.Seconds), top > 0 ? m.Seconds / top : 0, m.Measured,
                Loc.Get(m.Measured ? "Lt.Row.Measured" : "Lt.Row.Guessed")));
    }

    private static string Seconds(double s) => s >= 60 ? LoadTimes.Clock(s) : s >= 10 ? $"{s:0} {Loc.Get("Lt.Sec")}" : $"{s:0.0} {Loc.Get("Lt.Sec")}";

    [RelayCommand]
    private void OpenLoadTime()
    {
        RefreshLoadTime();
        FillLoadTimeRows();
        IsLoadTimeOpen = true;
    }

    [RelayCommand]
    private void CloseLoadTime() => IsLoadTimeOpen = false;

    /// <summary>The button of the breakdown: whatever step towards exact figures comes next.</summary>
    [RelayCommand]
    private async Task LoadTimeStep()
    {
        if (LoadingProgressMod is null)
        {
            IsLoadTimeOpen = false;
            await StartDownloadsAsync([new DownloadViewModel(LoadTimes.LoadingProgressWorkshopId, "Loading Progress") { EnableWhenDone = true }]);
            return;
        }
        if (!Snapshot().Contains(LoadTimes.LoadingProgressId) && _items.TryGetValue(LoadTimes.LoadingProgressId, out var item))
        {
            Activate([item]);
            ShowToast(Loc.Get("Lt.Enabled"));
        }
        if (LoadingProgressNow() is { Ready: false } lp)
        {
            if (GameLauncher.IsRunning())
            {
                ShowToast(Loc.Get("Lt.GameRunning"), 8);
                return;
            }
            try
            {
                LoadTimes.EnableTracking(lp, AppDataPaths.BackupsDir);
                ShowToast(Loc.Get("Lt.TrackingOn"), 8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                Message = Loc.Format("Msg.Error", ex.Message);
            }
        }
        RefreshLoadTime();
    }
}
