using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Models;
using RimForge.Core.Profiles;
using RimForge.Core.Sorting;
using RimForge.Core.Storage;
using RimForge.Mcp;

namespace RimForge.App.ViewModels;

/// <summary>One AI app on the settings page: is it installed, is it connected to RimForge, and the button for it.</summary>
public sealed partial class AiClientViewModel(AiClient client, string where, string snippet) : ObservableObject
{
    public AiClient Client { get; } = client;
    public string Name => Client.Name;
    /// <summary>Where the settings go when they are pasted by hand.</summary>
    public string Where { get; } = where;
    public string Snippet { get; } = snippet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsFound), nameof(CanConnect), nameof(NeedsUpdate))]
    public partial AiClientStatus Status { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool ShowManual { get; set; }

    public bool IsFound => Status != AiClientStatus.NotFound;
    public bool IsConnected => Status == AiClientStatus.Connected;
    public bool CanConnect => Status is AiClientStatus.NotConnected or AiClientStatus.OtherCopy;
    public bool NeedsUpdate => Status == AiClientStatus.OtherCopy;

    [RelayCommand]
    private void ToggleManual() => ShowManual = !ShowManual;
}

/// <summary>One thing an AI assistant did (or was refused), for the log on the settings page.</summary>
public sealed record AiLogRow(string Time, string Text, bool Ok);

/// <summary>
/// AI assistant mode: RimForge is an MCP server (<c>RimForge.exe --mcp</c>) any AI app can connect to. Tool calls
/// from it arrive here over a named pipe; changes are shown to the player first (unless they allowed everything).
/// </summary>
public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _aiCancel;
    private readonly SemaphoreSlim _aiLock = new(1, 1);

    public ObservableCollection<AiClientViewModel> AiClients { get; } = [];
    public bool CanConnectAnyAi => AiClients.Any(c => c.CanConnect);
    public string OtherAiCommand => $"\"{McpExePath}\" --mcp";
    public ObservableCollection<AiLogRow> AiLog { get; } = [];
    public bool HasAiLog => AiLog.Count > 0;

    /// <summary>Set by the window: brings it to the front when an AI asks the player something.</summary>
    public Action? BringToFront { get; set; }

    /// <summary>The command an AI app runs to start RimForge's MCP server.</summary>
    public static string McpExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RimForge.exe");

    public bool AiEnabled
    {
        get => _settings.AiEnabled;
        set
        {
            if (_settings.AiEnabled == value) return;
            _settings.AiEnabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool AiAccessRead
    {
        get => _settings.AiAccess == AiAccessModes.Read;
        set { if (value) SetAiAccess(AiAccessModes.Read); }
    }

    public bool AiAccessAsk
    {
        get => _settings.AiAccess is not (AiAccessModes.Read or AiAccessModes.Full);
        set { if (value) SetAiAccess(AiAccessModes.Ask); }
    }

    public bool AiAccessFull
    {
        get => _settings.AiAccess == AiAccessModes.Full;
        set { if (value) SetAiAccess(AiAccessModes.Full); }
    }

    private void SetAiAccess(string mode)
    {
        _settings.AiAccess = mode;
        _settings.Save();
        OnPropertyChanged(nameof(AiAccessRead));
        OnPropertyChanged(nameof(AiAccessAsk));
        OnPropertyChanged(nameof(AiAccessFull));
    }

    /// <summary>Starts listening for AI assistants (called once the mods are loaded).</summary>
    public void StartAiBridge()
    {
        FillAiClients();
        if (_aiCancel is not null) return;
        _aiCancel = new CancellationTokenSource();
        _ = Task.Run(() => AiBridge.ServeAsync(HandleBridgeAsync, _aiCancel.Token));
    }

    public void StopAiBridge() => _aiCancel?.Cancel();

    private async Task<BridgeReply> HandleBridgeAsync(BridgeMessage message, CancellationToken ct)
    {
        if (!_settings.AiEnabled)
            return new BridgeReply(false, "The player turned AI assistants off in RimForge (Settings → AI assistant).", true);
        var host = new WindowHost(this);
        switch (message.Kind)
        {
            case "call":
                var args = message.Arguments ?? JsonSerializer.SerializeToElement(new { });
                var result = await new RimForgeTools(host).CallAsync(message.Tool ?? "", args, message.Client, ct);
                return new BridgeReply(true, result.Text, result.IsError);
            case "write" when message.Write is { } write:
                var done = await host.WriteAsync(write with { Client = write.Client ?? message.Client }, ct);
                return new BridgeReply(done.Ok, done.Message, !done.Ok);
            default:
                return new BridgeReply(false, "Unknown request.", true);
        }
    }

    /// <summary>The tools see the window's state; changes run on the UI thread.</summary>
    private sealed class WindowHost(MainWindowViewModel vm) : IRimForgeHost
    {
        public Task<RimForgeState> GetStateAsync(CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(vm.BuildAiState).GetTask();

        public Task<WriteResult> WriteAsync(WriteRequest request, CancellationToken ct) =>
            Dispatcher.UIThread.InvokeAsync(() => vm.ApplyAiWriteAsync(request));
    }

    private RimForgeState BuildAiState()
    {
        if (_paths.GameDir is null || NeedsGameDir)
            throw new InvalidOperationException("RimForge has not found the RimWorld folder yet: the player needs to choose it in the window.");
        return new RimForgeState
        {
            Paths = _paths,
            GameVersion = _gameVersion,
            FullGameVersion = _paths.ReadFullGameVersion(),
            GameCopy = _activeCopy?.ShortVersion,
            Installed = new Dictionary<string, ModInfo>(_installed),
            Active = Snapshot(),
            Hints = new Dictionary<string, ProfileMod>(_hints),
            Profiles = Profiles.Select(p => p.Model).ToList(),
            ActiveProfileId = ActiveProfile?.Model.Id,
            Community = _community,
            Extras = CheckExtras(),
            Content = _profiles.Get,
            Access = _settings.AiAccess,
            HasUnsavedChanges = IsDirty,
            FromWindow = true,
        };
    }

    // ───────────────────────── Changes asked for by an AI ─────────────────────────

    private async Task<WriteResult> ApplyAiWriteAsync(WriteRequest r)
    {
        await _aiLock.WaitAsync();
        try
        {
            var who = string.IsNullOrWhiteSpace(r.Client) ? Loc.Get("Ai.SomeAssistant") : r.Client!;
            if (_settings.AiAccess == AiAccessModes.Read)
                return new WriteResult(false, "RimForge lets AI assistants only look, not change anything.");
            while (IsLoading) await Task.Delay(200);
            // Which saves use which mods: the question warns before turning off a mod a save needs.
            if (r.Action == WriteActions.Disable && _saveUsage.Count == 0)
                _saveUsage = await Task.Run(() => Core.Saves.SaveUsage.Find(_paths.ConfigDir));

            var description = DescribeAiWrite(r);
            if (_settings.AiAccess != AiAccessModes.Full)
            {
                BringToFront?.Invoke();
                // Another question is on screen: wait for the player to answer it first.
                while (Dialog is not null) await Task.Delay(250);
                var answer = await AskAsync(new DialogViewModel
                {
                    Title = Loc.Format("Ai.Ask.Title", who),
                    Text = description + "\n\n" + Loc.Get(r.Action is WriteActions.Download or WriteActions.Launch ? "Ai.Ask.NoUndo" : "Ai.Ask.Undo"),
                    PrimaryText = Loc.Get("Ai.Allow"),
                });
                if (answer != "primary")
                {
                    LogAi(who, description, ok: false);
                    return new WriteResult(false, "The player declined this change in RimForge.");
                }
            }

            var result = ApplyAiWrite(r);
            LogAi(who, description, result.Ok);
            if (result.Ok) ShowToast(Loc.Format("Ai.Done", who, description.Split('\n')[0]));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new WriteResult(false, "RimForge could not apply the change: " + ex.Message);
        }
        finally
        {
            _aiLock.Release();
        }
    }

    private List<ModItemViewModel> AiItems(IEnumerable<string>? ids) =>
        (ids ?? []).Select(id => _items.GetValueOrDefault(id)).OfType<ModItemViewModel>().ToList();

    private static string NamesText(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(8).Select(n => $"«{n}»")) + (names.Count > 8 ? " " + Loc.Format("Ai.AndMore", names.Count - 8) : "");

    /// <summary>What the change does, in the player's language.</summary>
    private string DescribeAiWrite(WriteRequest r)
    {
        var names = (r.Ids ?? []).Select(id => NameOf(id)).ToList();
        return r.Action switch
        {
            WriteActions.Enable => Loc.Format("Ai.Enable", names.Count, NamesText(names)),
            WriteActions.Disable => Loc.Format("Ai.Disable", names.Count, NamesText(names))
                + (names.Count > 0 && (r.Ids ?? []).Any(id => _saveUsage.ContainsKey(id)) ? "\n" + Loc.Get("Ai.UsedInSaves") : ""),
            WriteActions.SetLoadOrder => Loc.Format("Ai.SetOrder", names.Count, Snapshot().Count),
            WriteActions.Sort => Loc.Get(r.Mode == "full" ? "Ai.SortFull" : "Ai.Sort"),
            WriteActions.AddRule => Loc.Format("Ai.Rule", NameOf(r.Before), NameOf(r.After)),
            WriteActions.Pin => Loc.Format(r.Mode switch { "top" => "Ai.PinTop", "bottom" => "Ai.PinBottom", _ => "Ai.Unpin" }, names.FirstOrDefault() ?? "?"),
            WriteActions.CreateModpack => Loc.Format(r.Flag ? "Ai.CreateAndSwitch" : "Ai.Create", r.Name, r.Ids?.Count ?? Snapshot().Count)
                + ((r.Ids ?? []).Count(id => id.StartsWith(PlaceholderPrefix)) is > 0 and var toDownload ? "\n" + Loc.Format("Ai.WillDownload", toDownload) : ""),
            WriteActions.SwitchModpack => Loc.Format("Ai.Switch", Profiles.FirstOrDefault(p => p.Model.Id == r.Name)?.Name ?? r.Name),
            WriteActions.Download => Loc.Format(r.Flag ? "Ai.DownloadEnable" : "Ai.Download", r.WorkshopIds?.Count ?? 0),
            WriteActions.Launch => Loc.Get("Ai.Launch"),
            _ => r.Action,
        };
    }

    /// <summary>Applies an approved change. The message goes back to the AI (in English).</summary>
    private WriteResult ApplyAiWrite(WriteRequest r)
    {
        switch (r.Action)
        {
            case WriteActions.Enable:
            {
                var items = AiItems(r.Ids).Where(i => !i.IsActive).ToList();
                if (items.Count == 0) return new WriteResult(true, "Those mods were already on.");
                Activate(items);
                SortCarefully();
                return Saved($"Turned on {items.Count} mods and sorted the list carefully.");
            }
            case WriteActions.Disable:
            {
                var items = AiItems(r.Ids).Where(i => i.IsActive && !i.Mod.IsCore).ToList();
                Deactivate(items);
                return Saved($"Turned off {items.Count} mods.");
            }
            case WriteActions.SetLoadOrder:
                Change(() => _active = (r.Ids ?? []).Distinct().Select(ItemFor).ToList());
                return Saved($"The load order now has {_active.Count} mods.");
            case WriteActions.Sort:
            {
                var before = Snapshot();
                if (r.Mode == "full")
                {
                    var sorted = ModSorter.SortFull(Snapshot(), _rules, _installed, ContentOf);
                    if (sorted.Changed) Change(() => _active = sorted.Order.Select(ItemFor).ToList());
                }
                else SortCarefully();
                var moved = Snapshot().Where((id, i) => i >= before.Count || before[i] != id).Count();
                return Saved(moved == 0 ? "The order already followed every rule; nothing moved." : $"Sorted: {moved} positions changed.");
            }
            case WriteActions.AddRule when r.Before is { } b && r.After is { } a:
                if (ActiveProfile is null) return new WriteResult(false, "There is no active modpack to keep the rule in.");
                AddUserRule(new UserRule(b, a));
                SortCarefully();
                return Saved($"Rule saved in the modpack: {b} always loads before {a}. The list was re-sorted.");
            case WriteActions.Pin when AiItems(r.Ids).FirstOrDefault() is { } item:
                if (ActiveProfile is null) return new WriteResult(false, "There is no active modpack to keep the pin in.");
                PinMod(item, r.Mode ?? "none");
                return Saved(r.Mode == "none" ? $"{item.Id} is no longer pinned." : $"{item.Id} is pinned to the {r.Mode}.");
            case WriteActions.CreateModpack:
                return CreateModpackForAi(r);
            case WriteActions.SwitchModpack when Profiles.FirstOrDefault(p => p.Model.Id == r.Name) is { } profile:
                if (IsDirty) SaveNow();
                Apply(profile);
                return new WriteResult(true, $"Modpack \"{profile.Name}\" is active now and its list is written to the game.");
            case WriteActions.Download:
                if (r.WorkshopIds is not { Count: > 0 } ids) return new WriteResult(false, "No Workshop ids.");
                _ = StartDownloadsAsync(ids.Select(id => new DownloadViewModel(id, $"Workshop {id}") { EnableWhenDone = r.Flag }).ToList());
                return new WriteResult(true, $"Asked Steam to download {ids.Count} mods (dependencies follow). " +
                    (r.Flag ? "They are turned on and sorted when the download finishes. " : "") +
                    "Check progress later with get_overview or list_mods. If Steam isn't running, RimForge asks the player to start it.");
            case WriteActions.Launch:
                if (Downloads.Any(d => d.IsRunning && !d.IsWaiting)) return new WriteResult(false, "Mods are still downloading; launch the game when they are done.");
                _ = Play();
                return new WriteResult(true, "Saved the mod list and started RimWorld.");
        }
        return new WriteResult(false, "This change can't be applied.");
    }

    private WriteResult Saved(string message) =>
        SaveNow()
            ? new WriteResult(true, message + " Saved to the game; the player can undo it with Ctrl+Z in RimForge.")
            : new WriteResult(false, message + " But the list could not be saved to the game: " + (Message ?? "unknown error"));

    private WriteResult CreateModpackForAi(WriteRequest r)
    {
        if (IsDirty) SaveNow();
        var ids = r.Ids ?? Snapshot();
        var hints = ids.Where(id => id.StartsWith(PlaceholderPrefix))
            .ToDictionary(id => id, id => new ProfileMod(id, "Workshop " + id[PlaceholderPrefix.Length..], ulong.Parse(id[PlaceholderPrefix.Length..])));
        var profile = new Profile { Name = UniqueProfileName(r.Name ?? "AI"), Notes = Loc.Format("Ai.ModpackNote", r.Client ?? "AI") };
        profile.SetMods(ids, _installed, hints);
        var vm = AddProfile(profile);
        if (!r.Flag) return new WriteResult(true, $"Created modpack \"{profile.Name}\" with {ids.Count} mods (not active).");

        Apply(vm);
        SortCarefully();
        SaveNow();
        var missing = hints.Keys.Select(id => ulong.Parse(id[PlaceholderPrefix.Length..])).ToList();
        if (missing.Count > 0)
            _ = StartDownloadsAsync(missing.Select(id => new DownloadViewModel(id, "Workshop " + id)).ToList());
        return new WriteResult(true, $"Created modpack \"{profile.Name}\" with {ids.Count} mods, made it active and sorted it." +
            (missing.Count > 0 ? $" Downloading {missing.Count} mods from the Workshop; they take their places when done." : "") +
            " Run check_modlist to see what is still missing.");
    }

    private void LogAi(string who, string description, bool ok)
    {
        AiLog.Insert(0, new AiLogRow(DateTime.Now.ToString("HH:mm"), $"{who}: {description.Split('\n')[0]}", ok));
        while (AiLog.Count > 30) AiLog.RemoveAt(AiLog.Count - 1);
        OnPropertyChanged(nameof(HasAiLog));
    }

    // ───────────────────────── Settings page: connecting AI apps ─────────────────────────

    private void FillAiClients()
    {
        AiClients.Clear();
        foreach (var client in AiClientSetup.Known())
            AiClients.Add(new AiClientViewModel(client, Loc.Get("Ai.Where." + client.Id), AiClientSetup.Snippet(client.Format, McpExePath)));
        RefreshAiClients();
    }

    /// <summary>Re-reads every app's settings (the player may have changed them meanwhile).</summary>
    private void RefreshAiClients()
    {
        foreach (var c in AiClients)
        {
            c.Status = AiClientSetup.Status(c.Client, McpExePath);
            c.StatusText = Loc.Get("Ai.Status." + c.Status);
        }
        OnPropertyChanged(nameof(CanConnectAnyAi));
    }

    [RelayCommand]
    private void ConnectAiClient(AiClientViewModel client)
    {
        try
        {
            AiClientSetup.Connect(client.Client, McpExePath);
            if (!AiEnabled) AiEnabled = true;
            RefreshAiClients();
            ShowToast(Loc.Format("Ai.Connected", client.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowToast(Loc.Format("Ai.ConnectFailed", client.Name, ex.Message));
        }
    }

    [RelayCommand]
    private void ConnectAllAiClients()
    {
        var done = new List<string>();
        foreach (var client in AiClients.Where(c => c.CanConnect))
        {
            try
            {
                AiClientSetup.Connect(client.Client, McpExePath);
                done.Add(client.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ShowToast(Loc.Format("Ai.ConnectFailed", client.Name, ex.Message));
            }
        }
        if (done.Count > 0 && !AiEnabled) AiEnabled = true;
        RefreshAiClients();
        if (done.Count > 0) ShowToast(Loc.Format("Ai.ConnectedMany", string.Join(", ", done)));
    }

    [RelayCommand]
    private async Task DisconnectAiClient(AiClientViewModel client)
    {
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Format("Ai.Disconnect.Title", client.Name),
            Text = Loc.Format("Ai.Disconnect.Text", client.Name),
            PrimaryText = Loc.Get("Ai.Disconnect"),
        });
        if (answer != "primary") return;
        try
        {
            AiClientSetup.Disconnect(client.Client);
            RefreshAiClients();
            ShowToast(Loc.Format("Ai.Disconnected", client.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowToast(Loc.Format("Ai.ConnectFailed", client.Name, ex.Message));
        }
    }

    /// <summary>Set by the window: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    [RelayCommand]
    private async Task CopyAiSnippet(AiClientViewModel client)
    {
        if (CopyToClipboard is null) return;
        await CopyToClipboard(client.Snippet);
        ShowToast(Loc.Format("Ai.Copied", client.Name));
    }

    [RelayCommand]
    private async Task CopyOtherAiCommand()
    {
        if (CopyToClipboard is null) return;
        await CopyToClipboard(OtherAiCommand);
        ShowToast(Loc.Format("Ai.Copied", Loc.Get("Ai.OtherClient")));
    }

    [RelayCommand]
    private void OpenSettings() => CurrentPage = "settings";
}
