using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Storage;
using RimForge.Translation;

namespace RimForge.App.ViewModels;

/// <summary>
/// Which translator to use (online or the built-in model) and the built-in model itself: download on the
/// player's request (≈ 900 MB, resumable), progress, delete.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static string ModelDir => Path.Combine(AppDataPaths.Root, "models", "nllb-200-600m");

    private SmartTranslator _translator = null!;
    private CancellationTokenSource? _modelDownload;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText), nameof(CanDownloadModel), nameof(CanDeleteModel))]
    public partial bool IsModelInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText), nameof(CanDownloadModel), nameof(CanDeleteModel), nameof(TranslationButtonText))]
    public partial bool IsModelDownloading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText), nameof(TranslationButtonText))]
    public partial double ModelProgress { get; set; }

    private long _modelBytes, _partialBytes;

    public bool CanDownloadModel => !IsModelInstalled && !IsModelDownloading;
    public bool CanDeleteModel => IsModelInstalled && !IsModelDownloading;

    public string ModelStatusText =>
        IsModelDownloading ? Loc.Format("Tr.Model.Downloading", (int)(ModelProgress * 100))
        : IsModelInstalled ? Loc.Format("Tr.Model.Ready", Humanize.Size(_modelBytes))
        : _partialBytes > 0 ? Loc.Format("Tr.Model.Partial", Humanize.Size(_partialBytes))
        : Loc.Get("Tr.Model.NotInstalled");

    /// <summary>"Download model (~900 MB)", or "Continue download" after an interrupted one.</summary>
    public string DownloadModelText => Loc.Get(_partialBytes > 0 && !IsModelInstalled ? "Tr.Model.Continue" : "Tr.Model.Download");

    /// <summary>Shown on the small button next to "Auto-translate": the download progress while it runs.</summary>
    public string TranslationButtonText => IsModelDownloading ? $"{(int)(ModelProgress * 100)}%" : "";

    /// <summary>Online translator (better quality, needs the internet).</summary>
    public bool EngineOnline
    {
        get => !_settings.TranslateOffline;
        set { if (value) SetOffline(false); }
    }

    /// <summary>The built-in model (no internet, lower quality); only once it is downloaded.</summary>
    public bool EngineOffline
    {
        get => _settings.TranslateOffline;
        set { if (value) SetOffline(true); }
    }

    public bool TranslateFallback
    {
        get => _settings.TranslateFallback;
        set
        {
            if (_settings.TranslateFallback == value) return;
            _settings.TranslateFallback = value;
            _translator.FallBackToOffline = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    private void SetOffline(bool offline)
    {
        if (_settings.TranslateOffline == offline) return;
        _settings.TranslateOffline = offline;
        _translator.PreferOffline = offline;
        _settings.Save();
        OnPropertyChanged(nameof(EngineOnline));
        OnPropertyChanged(nameof(EngineOffline));
    }

    private void RefreshModelState()
    {
        IsModelInstalled = NllbModel.IsInstalled(ModelDir);
        _modelBytes = IsModelInstalled ? NllbModel.InstalledBytes(ModelDir) : 0;
        _partialBytes = IsModelInstalled ? 0 : NllbModel.PartialBytes(ModelDir);
        // The model was deleted outside RimForge: back to the online translator.
        if (!IsModelInstalled && _settings.TranslateOffline) SetOffline(false);
        OnPropertyChanged(nameof(ModelStatusText));
        OnPropertyChanged(nameof(DownloadModelText));
    }

    [RelayCommand]
    private async Task DownloadModel()
    {
        if (IsModelDownloading) return;
        Directory.CreateDirectory(ModelDir);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ModelDir))!).AvailableFreeSpace;
        if (free < NllbModel.ApproxBytes + 200L * 1024 * 1024)
        {
            ShowToast(Loc.Format("Tr.Model.NoSpace", Humanize.Size(NllbModel.ApproxBytes), Humanize.Size(free)));
            return;
        }

        _modelDownload = new CancellationTokenSource();
        IsModelDownloading = true;
        ModelProgress = 0;
        try
        {
            // A big file: no overall time limit (the shared client gives up after 20 seconds).
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RimForge");
            var progress = new Progress<double>(p => ModelProgress = p);
            await NllbModel.DownloadAsync(http, ModelDir, progress, _modelDownload.Token);
            RefreshModelState();
            ShowToast(Loc.Get("Tr.Model.Done"));
        }
        catch (OperationCanceledException)
        {
            // Stopped by the player; the ".part" files stay, so the next download continues from there.
        }
        catch (Exception ex)
        {
            ShowToast(Loc.Format("Tr.Model.Failed", ex.Message));
        }
        finally
        {
            IsModelDownloading = false;
            _modelDownload.Dispose();
            _modelDownload = null;
            RefreshModelState();
        }
    }

    [RelayCommand]
    private void CancelModelDownload() => _modelDownload?.Cancel();

    [RelayCommand]
    private void DeleteModel()
    {
        if (IsModelDownloading) return;
        SetOffline(false);
        _translator.Unload();
        try
        {
            NllbModel.Delete(ModelDir);
            ShowToast(Loc.Get("Tr.Model.Deleted"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(Loc.Format("Tr.Model.DeleteFailed", ex.Message));
        }
        RefreshModelState();
    }

    private void StopModelDownload() => _modelDownload?.Cancel();
}
