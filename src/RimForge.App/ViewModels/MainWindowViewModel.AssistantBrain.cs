using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Storage;
using RimForge.Steam.Assistant;

namespace RimForge.App.ViewModels;

/// <summary>A ready setting for another neural network: a program on this PC or a service with a key.</summary>
/// <param name="KeyPage">Where the player gets a key (null: no key needed).</param>
public sealed record AsstServerPreset(string Id, string Name, string Url, string? KeyPage, string[] PreferredModels)
{
    public bool NeedsKey => KeyPage is not null;
    public override string ToString() => Id == "custom" ? Loc.Get("Brain.Preset.Custom") : Name;
}

/// <summary>
/// How the assistant understands requests: by its word list (always there), with RimForge's own neural network
/// (downloaded on request, runs on this PC) or with another one (Ollama, LM Studio, or a service with a key).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly HttpClient LlmHttp = new() { Timeout = TimeSpan.FromMinutes(4) };
    private readonly LlmClient _llm = new(LlmHttp);
    private LocalModel? _localModel;
    private CancellationTokenSource? _brainDownloadCts;

    private LocalModel LocalModel => _localModel ??= new LocalModel(Path.Combine(AppDataPaths.Root, "models", "assistant"));

    public IReadOnlyList<AsstServerPreset> AsstPresets { get; } =
    [
        new("ollama", "Ollama", "http://127.0.0.1:11434/v1", null, ["qwen3", "qwen2.5", "gemma3", "llama3"]),
        new("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", null, ["qwen", "gemma", "llama"]),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "https://openrouter.ai/keys",
            ["google/gemini-2.5-flash", "deepseek/deepseek-chat", "meta-llama/llama-3.3-70b-instruct", "qwen/qwen3"]),
        new("groq", "Groq", "https://api.groq.com/openai/v1", "https://console.groq.com/keys", ["llama-3.3-70b", "qwen", "llama"]),
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "https://aistudio.google.com/apikey",
            ["gemini-2.5-flash", "gemini-2.0-flash", "gemini"]),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "https://platform.deepseek.com/api_keys", ["deepseek-chat"]),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "https://console.mistral.ai/api-keys", ["mistral-small", "mistral-medium", "mistral"]),
        new("openai", "OpenAI", "https://api.openai.com/v1", "https://platform.openai.com/api-keys", ["gpt-4.1-mini", "gpt-4o-mini", "gpt"]),
        new("custom", "custom", "", "", []),
    ];

    public ObservableCollection<string> AsstServerModels { get; } = [];

    [ObservableProperty] public partial bool IsBrainModelDownloading { get; set; }
    [ObservableProperty] public partial double BrainModelProgress { get; set; }
    [ObservableProperty] public partial string BrainModelStatus { get; set; } = "";
    [ObservableProperty] public partial string BrainServerStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsBrainServerChecking { get; set; }
    [ObservableProperty] public partial AsstServerPreset? AsstPreset { get; set; }
    [ObservableProperty] public partial string AsstServerUrl { get; set; } = "";
    [ObservableProperty] public partial string AsstServerKey { get; set; } = "";
    [ObservableProperty] public partial string AsstServerModel { get; set; } = "";

    public bool BrainWords { get => _settings.AsstBrain == AsstBrains.Words; set { if (value) SetBrain(AsstBrains.Words); } }
    public bool BrainModel { get => _settings.AsstBrain == AsstBrains.Model; set { if (value) SetBrain(AsstBrains.Model); } }
    public bool BrainServer { get => _settings.AsstBrain == AsstBrains.Server; set { if (value) SetBrain(AsstBrains.Server); } }
    public bool IsBrainModelInstalled => LocalModel.IsInstalled;
    public bool CanDownloadBrainModel => !IsBrainModelInstalled && !IsBrainModelDownloading;
    public bool AsstPresetNeedsKey => AsstPreset?.NeedsKey ?? true;
    public bool AsstPresetHasKeyPage => AsstPreset?.KeyPage is { Length: > 0 };
    public string BrainModelSizeText => Loc.Format("Brain.Model.Download", Humanize.Size(LocalModel.ApproxBytes));

    /// <summary>On the assistant page: what reads the requests now.</summary>
    public string AsstBrainText => _settings.AsstBrain switch
    {
        AsstBrains.Model when LocalModel.IsInstalled => Loc.Format("Brain.Using", Loc.Get("Brain.Model.Name")),
        AsstBrains.Server when !string.IsNullOrWhiteSpace(_settings.AsstServerModel) =>
            Loc.Format("Brain.Using", (AsstPresets.FirstOrDefault(p => p.Id == _settings.AsstServerPreset)?.ToString() ?? "") + " · " + _settings.AsstServerModel),
        _ => Loc.Get("Brain.UsingWords"),
    };

    private void InitBrain()
    {
        AsstPreset = AsstPresets.FirstOrDefault(p => p.Id == _settings.AsstServerPreset) ?? AsstPresets[0];
        AsstServerUrl = _settings.AsstServerUrl ?? AsstPreset.Url;
        AsstServerModel = _settings.AsstServerModel ?? "";
        AsstServerKey = Unprotect(_settings.AsstServerKey) ?? "";
        if (AsstServerModel.Length > 0) AsstServerModels.Add(AsstServerModel);
        RefreshBrainModelState();
    }

    private void SetBrain(string brain)
    {
        if (_settings.AsstBrain == brain) return;
        _settings.AsstBrain = brain;
        _settings.Save();
        OnPropertyChanged(nameof(BrainWords));
        OnPropertyChanged(nameof(BrainModel));
        OnPropertyChanged(nameof(BrainServer));
        OnPropertyChanged(nameof(AsstBrainText));
        if (brain != AsstBrains.Model) _localModel?.Stop(); // frees its memory at once
    }

    [RelayCommand]
    private void OpenBrainSettings() => ShowPage("settings");

    // ───────────────────────── RimForge's own model ─────────────────────────

    private void RefreshBrainModelState()
    {
        BrainModelStatus = IsBrainModelDownloading ? Loc.Format("Brain.Model.Downloading", (int)(BrainModelProgress * 100))
            : LocalModel.IsInstalled ? Loc.Format("Brain.Model.Installed", Humanize.Size(LocalModel.InstalledBytes))
            : Loc.Get("Brain.Model.NotInstalled");
        OnPropertyChanged(nameof(IsBrainModelInstalled));
        OnPropertyChanged(nameof(CanDownloadBrainModel));
        OnPropertyChanged(nameof(AsstBrainText));
    }

    [RelayCommand]
    private async Task DownloadBrainModel()
    {
        if (IsBrainModelDownloading || LocalModel.IsInstalled) return;
        var cts = _brainDownloadCts = new CancellationTokenSource();
        IsBrainModelDownloading = true;
        BrainModelProgress = 0;
        RefreshBrainModelState();
        try
        {
            var progress = new Progress<double>(p =>
            {
                BrainModelProgress = p;
                if (IsBrainModelDownloading) BrainModelStatus = Loc.Format("Brain.Model.Downloading", (int)(p * 100));
            });
            await LocalModel.DownloadAsync(Http, progress, cts.Token);
            SetBrain(AsstBrains.Model);
            ShowToast(Loc.Get("Brain.Model.Ready"));
        }
        catch (OperationCanceledException)
        {
            ShowToast(Loc.Get("Brain.Model.Paused"));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.Format("Brain.Model.Failed", ex.Message));
        }
        finally
        {
            IsBrainModelDownloading = false;
            _brainDownloadCts = null;
            RefreshBrainModelState();
        }
    }

    [RelayCommand]
    private void CancelBrainDownload() => _brainDownloadCts?.Cancel();

    [RelayCommand]
    private async Task DeleteBrainModel()
    {
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Brain.Model.DeleteTitle"),
            Text = Loc.Format("Brain.Model.DeleteText", Humanize.Size(LocalModel.InstalledBytes)),
            PrimaryText = Loc.Get("Brain.Model.Delete"),
            PrimaryIsDanger = true,
        });
        if (answer != "primary") return;
        try
        {
            LocalModel.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(Loc.Format("Brain.Model.Failed", ex.Message));
        }
        if (_settings.AsstBrain == AsstBrains.Model) SetBrain(AsstBrains.Words);
        RefreshBrainModelState();
    }

    // ───────────────────────── Another network ─────────────────────────

    partial void OnAsstPresetChanged(AsstServerPreset? value)
    {
        OnPropertyChanged(nameof(AsstPresetNeedsKey));
        OnPropertyChanged(nameof(AsstPresetHasKeyPage));
        if (value is null || _settings.AsstServerPreset == value.Id && AsstServerUrl.Length > 0) return;
        if (value.Id != "custom") AsstServerUrl = value.Url;
        AsstServerModels.Clear();
        AsstServerModel = "";
        BrainServerStatus = "";
    }

    [RelayCommand]
    private void OpenAsstKeyPage()
    {
        if (AsstPreset?.KeyPage is { Length: > 0 } page) OpenUrl(page);
    }

    /// <summary>Asks the server for its models, picks a good one and saves the setting if it answers.</summary>
    [RelayCommand]
    private async Task CheckBrainServer()
    {
        if (string.IsNullOrWhiteSpace(AsstServerUrl)) return;
        IsBrainServerChecking = true;
        BrainServerStatus = Loc.Get("Brain.Server.Checking");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var models = await _llm.ListModelsAsync(AsstServerUrl.Trim(), AsstServerKey, cts.Token);
            var current = AsstServerModel;
            AsstServerModels.Clear();
            foreach (var m in models.Order(StringComparer.OrdinalIgnoreCase)) AsstServerModels.Add(m);
            if (string.IsNullOrWhiteSpace(current) || !models.Contains(current))
                AsstServerModel = PickModel(models, AsstPreset?.PreferredModels ?? []) ?? current;
            else AsstServerModel = current;

            if (string.IsNullOrWhiteSpace(AsstServerModel))
            {
                BrainServerStatus = Loc.Get(models.Count == 0 ? "Brain.Server.NoModels" : "Brain.Server.PickModel");
                return;
            }
            // One tiny question to be sure the model answers.
            _ = await _llm.CompleteAsync(new LlmEndpoint(AsstServerUrl.Trim(), AsstServerModel, AsstServerKey), "Answer with one word.", "Say OK.", false, 5, cts.Token);
            SaveBrainServer();
            SetBrain(AsstBrains.Server);
            BrainServerStatus = Loc.Format("Brain.Server.Ok", AsstServerModel);
        }
        catch (Exception ex) when (ex is LlmException or OperationCanceledException or HttpRequestException or UriFormatException)
        {
            BrainServerStatus = Loc.Format("Brain.Server.Failed", ex is OperationCanceledException ? Loc.Get("Brain.Server.Timeout") : ex.Message);
        }
        finally
        {
            IsBrainServerChecking = false;
        }
    }

    private static string? PickModel(IReadOnlyList<string> models, string[] preferred)
    {
        foreach (var want in preferred)
            if (models.FirstOrDefault(m => m.Contains(want, StringComparison.OrdinalIgnoreCase) && !m.Contains("embed", StringComparison.OrdinalIgnoreCase)) is { } found)
                return found;
        return models.FirstOrDefault(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase));
    }

    private void SaveBrainServer()
    {
        _settings.AsstServerPreset = AsstPreset?.Id;
        _settings.AsstServerUrl = AsstServerUrl.Trim();
        _settings.AsstServerModel = AsstServerModel.Trim();
        _settings.AsstServerKey = Protect(AsstServerKey);
        _settings.Save();
        OnPropertyChanged(nameof(AsstBrainText));
    }

    /// <summary>Programs found on this PC (Ollama, LM Studio), shown when the settings open.</summary>
    private async Task DetectLocalServersAsync()
    {
        if (BrainServerStatus.Length > 0) return;
        var found = new List<string>();
        foreach (var (name, url) in LlmClient.LocalServers)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                var models = await _llm.ListModelsAsync(url, null, cts.Token);
                found.Add(models.Count > 0 ? $"{name} ({models.Count})" : name);
            }
            catch (Exception ex) when (ex is LlmException or OperationCanceledException)
            {
                // not running here
            }
        }
        if (found.Count > 0 && BrainServerStatus.Length == 0) BrainServerStatus = Loc.Format("Brain.Server.Found", string.Join(", ", found));
    }

    // ───────────────────────── Asking the network ─────────────────────────

    /// <summary>The network to ask now, or null for the word list. May start RimForge's own model (slow the first time).</summary>
    private async Task<LlmEndpoint?> BrainEndpointAsync(ChatMessageViewModel status, CancellationToken ct)
    {
        switch (_settings.AsstBrain)
        {
            case AsstBrains.Model when LocalModel.IsInstalled:
                if (!LocalModel.IsRunning) status.Text = Loc.Get("Brain.Starting");
                var endpoint = await LocalModel.EnsureRunningAsync(LlmHttp, ct);
                LocalModel.Touch();
                return endpoint;
            case AsstBrains.Server when !string.IsNullOrWhiteSpace(_settings.AsstServerUrl) && !string.IsNullOrWhiteSpace(_settings.AsstServerModel):
                return new LlmEndpoint(_settings.AsstServerUrl, _settings.AsstServerModel, Unprotect(_settings.AsstServerKey));
            default:
                return null;
        }
    }

    private void StopBrain()
    {
        _brainDownloadCts?.Cancel();
        _localModel?.Dispose();
    }

    private static string? Protect(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return null;
        if (!OperatingSystem.IsWindows()) return Convert.ToBase64String(Encoding.UTF8.GetBytes(secret.Trim()));
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret.Trim()), null, DataProtectionScope.CurrentUser));
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var bytes = Convert.FromBase64String(stored);
            return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
