using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Checks;
using RimForge.Core.Launch;
using RimForge.Core.Models;
using RimForge.Core.Mods;
using RimForge.Core.Paths;
using RimForge.Core.Profiles;
using RimForge.Core.Saves;
using RimForge.Steam.Client;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

/// <summary>
/// The Steam Workshop inside RimForge: search, ready-made selections, subscribing with a download queue
/// (dependencies come along), update feed and modpacks from Steam collections.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Placeholder id for a mod known only by its Workshop id (from a collection) until it is downloaded.</summary>
    private const string PlaceholderPrefix = "workshop:";

    private static readonly HttpClient Http = WorkshopWeb.CreateHttpClient();
    private readonly SteamBridge _steam = new();
    private readonly WorkshopWeb _web = new(Http);
    private readonly PreviewCache _previews = new(Http);
    private CancellationTokenSource? _searchCts;

    /// <summary>Picture downloads of the cards shown now; a new page cancels them so its own pictures don't wait in line.</summary>
    private CancellationTokenSource _previewCts = new();
    private WorkshopQuery? _lastQuery;
    private string? _lastHeader;
    private bool _workshopOpened;
    private bool _downloadsRunning;
    private bool _webCheckDone;
    private DispatcherTimer? _steamIdleTimer;
    private Dictionary<ulong, ModInfo> _byWorkshopId = new();
    private readonly HashSet<ulong> _unsubscribed = [];
    private IReadOnlyDictionary<ulong, InstalledWorkshopItem> _manifest = new Dictionary<ulong, InstalledWorkshopItem>();
    private Dictionary<ulong, WorkshopItem> _webDetails = new();

    public ObservableCollection<WorkshopCardViewModel> WorkshopResults { get; } = [];
    public ObservableCollection<ThemeViewModel> Themes { get; } = new(WorkshopThemes.All.Select(t => new ThemeViewModel(t)));
    public ObservableCollection<DownloadViewModel> Downloads { get; } = [];
    public ObservableCollection<UpdateRowViewModel> RecentUpdates { get; } = [];
    public ObservableCollection<UpdateRowViewModel> PendingUpdates { get; } = [];
    public ObservableCollection<UpdateRowViewModel> RemovedMods { get; } = [];

    public IReadOnlyList<string> WorkshopSortOptions =>
        [Loc.Get("Ws.Sort.Auto"), Loc.Get("Ws.Sort.Trend"), Loc.Get("Ws.Sort.Subscribers"), Loc.Get("Ws.Sort.Newest"), Loc.Get("Ws.Sort.Updated")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWsSearch), nameof(IsWsThemes), nameof(IsWsUpdates), nameof(IsWsDownloads), nameof(IsWsCleanup), nameof(IsWsTranslations), nameof(IsWsCollections), nameof(ShowResults))]
    public partial string WorkshopSection { get; set; } = "search";

    public bool IsWsSearch => WorkshopSection == "search";
    public bool IsWsThemes => WorkshopSection == "themes";
    public bool IsWsUpdates => WorkshopSection == "updates";
    public bool IsWsDownloads => WorkshopSection == "downloads";
    public bool IsWsCleanup => WorkshopSection == "cleanup";
    public bool IsWsTranslations => WorkshopSection == "translations";
    public bool ShowResults => IsWsSearch || IsWsThemes || IsWsCollections;

    [ObservableProperty] public partial string WorkshopText { get; set; } = "";
    [ObservableProperty] public partial int WorkshopSortIndex { get; set; }
    [ObservableProperty] public partial bool OnlyMyVersion { get; set; } = true;
    [ObservableProperty] public partial bool HideTranslations { get; set; } = true;
    [ObservableProperty] public partial bool IsWorkshopBusy { get; set; }
    [ObservableProperty] public partial string ResultsHeader { get; set; } = "";
    [ObservableProperty] public partial string? ResultsNote { get; set; }
    // ── Pages of the results, like a book: one page at a time, so old pictures leave memory ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWsPrev), nameof(CanWsNext), nameof(WsPageOf))]
    public partial int WsPage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWsNext), nameof(HasWsPager), nameof(WsPageOf))]
    public partial int WsPageCount { get; set; }

    [ObservableProperty] public partial string WsPageInput { get; set; } = "";

    public ObservableCollection<WsPageButton> WsPageButtons { get; } = [];
    public bool HasWsPager => WsPageCount > 1;
    public bool CanWsPrev => WsPage > 1;
    public bool CanWsNext => WsPage < WsPageCount;
    public string WsPageOf => Loc.Format("Ws.PageOf", WsPageCount);

    private void SetWsPages(int page, int count)
    {
        WsPage = page;
        WsPageCount = count;
        WsPageButtons.Clear();
        if (count <= 1) return;
        // 1 … 4 5 [6] 7 8 … 40
        var shown = new SortedSet<int> { 1, count };
        for (var p = Math.Max(1, page - 2); p <= Math.Min(count, page + 2); p++) shown.Add(p);
        var last = 0;
        foreach (var p in shown)
        {
            if (p - last > 1) WsPageButtons.Add(new WsPageButton(null, false));
            WsPageButtons.Add(new WsPageButton(p, p == page));
            last = p;
        }
    }

    [RelayCommand]
    private Task WsGoPage(int? page)
    {
        if (_lastQuery is not { } q || IsWorkshopBusy || page is not { } n || n < 1 || n > Math.Max(WsPageCount, 1) || n == WsPage)
            return Task.CompletedTask;
        WsPageInput = "";
        return RunSearchAsync(q with { Page = n }, append: false, header: _lastHeader);
    }

    [RelayCommand]
    private Task WsPrevPage() => WsGoPage(WsPage - 1);

    [RelayCommand]
    private Task WsNextPage() => WsGoPage(WsPage + 1);

    /// <summary>The number typed into "Page … of N".</summary>
    [RelayCommand]
    private Task WsGoTypedPage() =>
        int.TryParse(WsPageInput.Trim(), out var page) ? WsGoPage(Math.Clamp(page, 1, Math.Max(WsPageCount, 1))) : Task.CompletedTask;
    [ObservableProperty] public partial bool EnableAfterDownload { get; set; } = true;
    [ObservableProperty] public partial bool IsCheckingUpdates { get; set; }
    [ObservableProperty] public partial int DownloadableMissing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCard))]
    public partial WorkshopCardViewModel? SelectedCard { get; set; }

    [ObservableProperty] public partial Bitmap? SelectedCardPreview { get; set; }

    [ObservableProperty] public partial string SteamStatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteamOk), nameof(IsSteamWarn))]
    public partial string SteamStatusLevel { get; set; } = "off";

    [ObservableProperty] public partial bool CanStartSteam { get; set; }

    public bool HasCard => SelectedCard is not null;
    public bool IsSteamOk => SteamStatusLevel == "ok";
    public bool IsSteamWarn => SteamStatusLevel == "warn";
    public bool HasResultsNote => !string.IsNullOrEmpty(ResultsNote);
    public bool HasDownloads => Downloads.Count > 0;
    public bool HasDownloadableMissing => DownloadableMissing > 0;
    public bool HasRecentUpdates => RecentUpdates.Count > 0;
    public bool HasPendingUpdates => PendingUpdates.Count > 0;
    public bool HasRemovedMods => RemovedMods.Count > 0;
    public bool UpdatesEmpty => !HasRecentUpdates && !HasPendingUpdates && !HasRemovedMods && !IsCheckingUpdates;
    public int RunningDownloads => Downloads.Count(d => d.IsRunning);
    public bool HasRunningDownloads => RunningDownloads > 0;
    public string UpdatesTabText => Loc.Format("Ws.Tab.Updates", RecentUpdates.Count + PendingUpdates.Count + RemovedMods.Count);
    public string DownloadsTabText => HasRunningDownloads ? Loc.Format("Ws.Tab.DownloadsRunning", RunningDownloads) : Loc.Get("Ws.Tab.Downloads");
    public string DownloadMissingText => Loc.Format("Dl.DownloadMissing", DownloadableMissing);
    public string WorkshopVersionText => Loc.Format("Ws.OnlyMyVersion", _gameVersion ?? "?");
    public string UpdatesSeenText => Loc.Format("Upd.SeenSince", Dates.Format((_settings.WorkshopSeenUtc ?? DateTime.UtcNow.AddDays(-7)).ToLocalTime()));

    partial void OnResultsNoteChanged(string? value) => OnPropertyChanged(nameof(HasResultsNote));
    partial void OnDownloadableMissingChanged(int value)
    {
        OnPropertyChanged(nameof(HasDownloadableMissing));
        OnPropertyChanged(nameof(DownloadMissingText));
    }
    partial void OnIsCheckingUpdatesChanged(bool value) => OnPropertyChanged(nameof(UpdatesEmpty));

    private void InitWorkshop()
    {
        _steam.ConnectionChanged += () => Dispatcher.UIThread.Post(UpdateSteamStatus);
        Downloads.CollectionChanged += (_, _) => UpdateDownloadCounters();
        // Steam shows "playing RimWorld" while the helper runs, so it is closed after a quiet minute.
        _steamIdleTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) =>
        {
            if (_steam.IsConnected && !_downloadsRunning && DateTime.UtcNow - _steam.LastUsedUtc > TimeSpan.FromSeconds(60))
                _steam.Stop();
        });
        _steamIdleTimer.Start();
        PreviewCache.Trim();
        UpdateSteamStatus();
    }

    private void RefreshWorkshopTexts()
    {
        foreach (var t in Themes) t.Refresh();
        var sort = WorkshopSortIndex;
        OnPropertyChanged(nameof(WorkshopSortOptions));
        // The picker drops its selection when its list is replaced.
        Dispatcher.UIThread.Post(() =>
        {
            _sortRestoring = true;
            WorkshopSortIndex = -1;
            WorkshopSortIndex = sort;
            _sortRestoring = false;
        }, DispatcherPriority.Background);
        foreach (var name in new[] { nameof(UpdatesTabText), nameof(DownloadsTabText), nameof(WorkshopVersionText), nameof(UpdatesSeenText), nameof(DownloadMissingText) })
            OnPropertyChanged(name);
        UpdateSteamStatus();
    }

    private bool _sortRestoring;

    /// <summary>Called when RimForge closes.</summary>
    public void Shutdown()
    {
        StopAiBridge();
        StopBrain();
        _steam.Dispose();
        StopModelDownload();
        _translations.Save();
        InstallUpdateAtExit();
    }

    private void OnWorkshopPageShown()
    {
        UpdateSteamStatus();
        if (_workshopOpened) return;
        _workshopOpened = true;
        UpdateWorkshopFilterText();
        _ = RefreshResultsAsync();
        _ = CheckUpdatesOnlineAsync();
    }

    [RelayCommand]
    private void ShowWorkshopSection(string section)
    {
        if (section == "collections")
        {
            _ = ShowCollections();
            return;
        }
        var fromCollections = _lastQuery is { Collections: true } || IsWsCollections;
        WorkshopSection = section;
        if (section == "themes" && (!Themes.Any(t => t.IsCurrent) || fromCollections)) ClearResults();
        if (section == "search" && (Themes.Any(t => t.IsCurrent) || fromCollections))
        {
            foreach (var t in Themes) t.IsCurrent = false;
            _ = RefreshResultsAsync();
        }
        if (section == "cleanup") RefreshCleanup();
        UpdateWorkshopFilterText();
    }

    // ───────────────────────── Steam status ─────────────────────────

    private void UpdateSteamStatus()
    {
        CanStartSteam = false;
        if (_steam.IsConnected)
            (SteamStatusLevel, SteamStatusText) = ("ok", Loc.Format("Ws.Steam.Connected", _steam.UserName ?? "Steam"));
        else if (!SteamBridge.IsSteamRunning())
        {
            (SteamStatusLevel, SteamStatusText) = ("warn", Loc.Get("Ws.Steam.NotRunning"));
            CanStartSteam = GamePathDetector.SteamInstallDir() is not null;
        }
        else if (GameLauncher.IsRunning())
            (SteamStatusLevel, SteamStatusText) = ("warn", Loc.Get("Ws.Steam.GameRunning"));
        else
            (SteamStatusLevel, SteamStatusText) = ("off", Loc.Get("Ws.Steam.Idle"));
    }

    [RelayCommand]
    private void StartSteam()
    {
        if (GamePathDetector.SteamInstallDir() is not { } dir) return;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(dir, "steam.exe")) { UseShellExecute = true })?.Dispose();
            ShowToast(Loc.Get("Ws.Steam.Starting"));
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ex.Message);
        }
    }

    private string ProblemText(Exception ex) => ex is SteamUnavailableException s
        ? s.Problem switch
        {
            SteamProblem.SteamNotRunning => Loc.Get("Ws.Steam.NotRunning"),
            SteamProblem.NotLoggedIn => Loc.Get("Ws.Steam.Offline"),
            SteamProblem.GameRunning => Loc.Get("Ws.Steam.GameRunning"),
            _ => Loc.Format("Ws.Steam.Failed", s.Message),
        }
        : ex.Message;

    /// <summary>Makes sure Steam can take subscriptions; explains what to do if it can't.</summary>
    private async Task<bool> EnsureSteamForDownloadsAsync()
    {
        if (GameLauncher.IsRunning())
        {
            await AskAsync(new DialogViewModel { Title = Loc.Get("Ws.NeedSteam.Title"), Text = Loc.Get("Ws.Steam.GameRunning"), PrimaryText = Loc.Get("Dialog.Ok") });
            return false;
        }
        if (SteamBridge.IsSteamRunning()) return true;

        var canStart = GamePathDetector.SteamInstallDir() is not null;
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Ws.NeedSteam.Title"),
            Text = Loc.Get(canStart ? "Ws.NeedSteam.Text" : "Ws.NeedSteam.NoSteam"),
            PrimaryText = Loc.Get(canStart ? "Ws.Steam.Start" : "Dialog.Ok"),
        });
        if (answer == "primary" && canStart) StartSteam();
        return false;
    }

    // ───────────────────────── Search and selections ─────────────────────────

    /// <summary>Category picked in the search filters (one of the selection themes), or null for any.</summary>
    [ObservableProperty] public partial ThemeViewModel? SearchCategory { get; set; }
    [ObservableProperty] public partial bool HideInstalled { get; set; }
    [ObservableProperty] public partial bool HideAbandoned { get; set; }
    [ObservableProperty] public partial bool OnlyMods { get; set; } = true;
    [ObservableProperty] public partial string WorkshopFilterText { get; set; } = "";

    public bool HasSearchCategory => SearchCategory is not null;
    public string SearchCategoryText => SearchCategory?.Name ?? Loc.Get("Ws.Category.Any");

    private WorkshopSort SelectedSort(bool hasText) => WorkshopSortIndex switch
    {
        1 => WorkshopSort.Trend,
        2 => WorkshopSort.MostSubscribed,
        3 => WorkshopSort.Newest,
        4 => WorkshopSort.Updated,
        _ => hasText ? WorkshopSort.Relevance : WorkshopSort.Trend,
    };

    private string? SearchText => WorkshopText.Trim() is { Length: > 0 } t ? t : null;

    private WorkshopQuery BuildQuery(int page) =>
        new(SearchText, SelectedSort(SearchText is not null), OnlyMyVersion ? _gameVersion : null, page);

    [RelayCommand]
    private Task WorkshopSearch()
    {
        foreach (var t in Themes) t.IsCurrent = false;
        WorkshopSection = "search";
        return RefreshResultsAsync();
    }

    /// <summary>Shows the current tab's results again (after the sort or a filter changed).</summary>
    private Task RefreshResultsAsync()
    {
        if (IsWsCollections) return CollSearch();
        if (IsWsThemes)
            return Themes.FirstOrDefault(t => t.IsCurrent) is { } theme ? ShowTheme(theme) : Task.CompletedTask;
        if (SearchCategory is { } category)
        {
            if (SearchText is { } text)
            {
                // Words plus a category: search the words, keep what fits the category.
                var words = category.Theme.Queries;
                return RunMergedAsync([text, text], Loc.Format("Ws.Header.SearchIn", text, category.Name),
                    i => words.Any(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(w, StringComparison.OrdinalIgnoreCase)),
                    pages: 2);
            }
            return RunMergedAsync(category.Theme.Queries, Loc.Format("Ws.Header.Category", category.Name));
        }
        return RunSearchAsync(BuildQuery(1), append: false, header: null);
    }

    private void OnWorkshopFilterChanged()
    {
        UpdateWorkshopFilterText();
        if (_workshopOpened && ShowResults) _ = RefreshResultsAsync();
    }

    private void UpdateWorkshopFilterText()
    {
        var count = new[] { OnlyMyVersion, HideTranslations, HideInstalled, HideAbandoned, OnlyMods, SearchCategory is not null && IsWsSearch }.Count(x => x);
        WorkshopFilterText = Loc.Format("Filters.ButtonCount", count);
        OnPropertyChanged(nameof(HasSearchCategory));
        OnPropertyChanged(nameof(SearchCategoryText));
    }

    partial void OnWorkshopSortIndexChanged(int value)
    {
        if (!_sortRestoring && value >= 0) OnWorkshopFilterChanged();
    }

    partial void OnHideTranslationsChanged(bool value) => OnWorkshopFilterChanged();
    partial void OnOnlyMyVersionChanged(bool value) => OnWorkshopFilterChanged();
    partial void OnHideInstalledChanged(bool value) => OnWorkshopFilterChanged();
    partial void OnHideAbandonedChanged(bool value) => OnWorkshopFilterChanged();
    partial void OnOnlyModsChanged(bool value) => OnWorkshopFilterChanged();

    partial void OnSearchCategoryChanged(ThemeViewModel? value)
    {
        foreach (var t in Themes) t.IsSearchCategory = t == value;
        OnWorkshopFilterChanged();
    }

    [RelayCommand]
    private void PickSearchCategory(ThemeViewModel? theme) => SearchCategory = theme == SearchCategory ? null : theme;

    /// <summary>Opens the Workshop page with a search for <paramref name="text"/> (used by "Find" buttons).</summary>
    private void SearchWorkshopFor(string text)
    {
        WorkshopText = text;
        WorkshopSortIndex = 0;
        SearchCategory = null;
        _workshopOpened = true;
        CurrentPage = "workshop";
        _ = WorkshopSearch();
    }

    private async Task RunSearchAsync(WorkshopQuery query, bool append, string? header)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsWorkshopBusy = true;
        ResultsNote = null;
        if (!append) ClearResults();
        try
        {
            var page = await QueryAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            var wasBeyond = false;
            if (page.Items.Count == 0 && query.Page > 1)
            {
                ResultsNote = Loc.Format("Ws.FindingLast", query.Page);
                (query, page) = await FindLastPageAsync(query, cts.Token);
                ResultsNote = null;
                if (cts.IsCancellationRequested) return;
                wasBeyond = true;
            }
            AddCards(page.Items);
            _lastQuery = query;
            _lastHeader = header;
            var known = page.HasMore ? query.Page + 1 : query.Page;
            var count = Math.Max(known, (page.Total + page.PageSize - 1) / page.PageSize);
            if (_wsLastPages.TryGetValue(query with { Page = 1 }, out var last)) count = Math.Min(count, last);
            SetWsPages(query.Page, Math.Max(count, query.Page));
            if (wasBeyond) ShowToast(Loc.Format("Ws.LastPage", query.Page), 6);
            ResultsHeader = header ?? (query.Text is null
                ? Loc.Get(query.Sort switch
                {
                    WorkshopSort.MostSubscribed => "Ws.Header.Subscribers",
                    WorkshopSort.Newest => "Ws.Header.Newest",
                    WorkshopSort.Updated => "Ws.Header.Updated",
                    _ => "Ws.Header.Trend",
                })
                : Loc.Format("Ws.Header.Search", query.Text)) + (page.Total > 0 ? " · " + Loc.Format("Ws.Found", Humanize.Count(page.Total)) : "");
            // A far page whose mods the filters all hid is not "nothing found".
            if (WorkshopResults.Count == 0)
                ResultsNote = Loc.Get(page.Items.Count > 0 ? "Ws.PageFiltered" : query.Page > 1 ? "Ws.PageEmpty" : "Ws.NothingFound");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ResultsNote = Loc.Format("Ws.SearchFailed", ProblemText(ex));
        }
        finally
        {
            if (_searchCts == cts) IsWorkshopBusy = false;
        }
    }

    /// <summary>The real last page of a search (Steam counts more matches than it hands out), found once and remembered.</summary>
    private readonly Dictionary<WorkshopQuery, int> _wsLastPages = [];

    /// <summary>
    /// The page asked for came back empty: halve the distance to the last page known to have mods until the last
    /// one with mods is found (about ten quick questions even for a thousand pages), and show that one.
    /// </summary>
    private async Task<(WorkshopQuery Query, WorkshopPage Page)> FindLastPageAsync(WorkshopQuery empty, CancellationToken ct)
    {
        var good = 1;
        var bad = empty.Page;
        WorkshopPage? goodPage = null;
        if (_lastQuery is { } shown && shown with { Page = 1 } == empty with { Page = 1 } && shown.Page < bad)
            good = shown.Page;
        while (bad - good > 1)
        {
            var mid = good + (bad - good) / 2;
            var probe = await QueryAsync(empty with { Page = mid }, ct);
            if (probe.Items.Count > 0)
            {
                good = mid;
                goodPage = probe;
            }
            else bad = mid;
        }
        _wsLastPages[empty with { Page = 1 }] = good;
        var query = empty with { Page = good };
        return (query, goodPage ?? await QueryAsync(query, ct));
    }

    /// <summary>Asks the Steam client when it is available (richer, official), otherwise the Workshop website.</summary>
    private async Task<WorkshopPage> QueryAsync(WorkshopQuery query, CancellationToken ct)
    {
        if (query.Collections) return await _web.SearchCollectionsAsync(query, ct);
        // Only the website can order by "last updated".
        if (query.Sort != WorkshopSort.Updated && SteamBridge.IsSteamRunning() && !GameLauncher.IsRunning())
        {
            try
            {
                return await _steam.SearchAsync(query, ct);
            }
            catch (SteamUnavailableException)
            {
                // fall back to the website below
            }
            finally
            {
                UpdateSteamStatus();
            }
        }
        return await _web.SearchAsync(query, ct);
    }

    [RelayCommand]
    private Task ShowTheme(ThemeViewModel theme)
    {
        foreach (var t in Themes) t.IsCurrent = t == theme;
        WorkshopSection = "themes";
        return RunMergedAsync(theme.Theme.Queries, Loc.Format("Ws.Header.Theme", theme.Name));
    }

    /// <summary>Several searches merged into one list (selections, categories), then sorted as the player chose.</summary>
    private async Task RunMergedAsync(IReadOnlyList<string> words, string header, Func<WorkshopItem, bool>? keep = null, int pages = 1)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsWorkshopBusy = true;
        ResultsNote = null;
        ClearResults();
        _lastQuery = null;
        ResultsHeader = header;
        try
        {
            var version = OnlyMyVersion ? _gameVersion : null;
            var queries = words.Select((w, i) => new WorkshopQuery(w, WorkshopSort.Relevance, version, pages > 1 ? i + 1 : 1)).ToList();
            var results = new List<IReadOnlyList<WorkshopItem>>();
            if (SteamBridge.IsSteamRunning() && !GameLauncher.IsRunning())
                results.AddRange(await Task.WhenAll(queries.Select(q => SafeQueryAsync(q, cts.Token))));
            else
                foreach (var q in queries) results.Add(await SafeQueryAsync(q, cts.Token)); // the website dislikes bursts
            if (cts.IsCancellationRequested) return;

            IEnumerable<WorkshopItem> merged = WorkshopThemes.Merge(results, take: 80);
            if (keep is not null) merged = merged.Where(keep);
            merged = WorkshopSortIndex switch
            {
                2 => merged.OrderByDescending(i => i.Subscribers),
                3 => merged.OrderByDescending(i => i.CreatedUtc),
                4 => merged.OrderByDescending(i => i.UpdatedUtc),
                _ => merged,
            };
            var list = merged.ToList();
            var have = list.Count(i => _byWorkshopId.ContainsKey(i.Id));
            AddCards(list);
            ResultsHeader = header + " · " + Loc.Format("Ws.Found", WorkshopResults.Count)
                            + (have > 0 ? " · " + Loc.Format(HideInstalled ? "Ws.Theme.HaveHidden" : "Ws.Theme.Have", have) : "");
            if (WorkshopResults.Count == 0) ResultsNote = Loc.Get("Ws.NothingFound");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ResultsNote = Loc.Format("Ws.SearchFailed", ProblemText(ex));
        }
        finally
        {
            if (_searchCts == cts) IsWorkshopBusy = false;
        }
    }

    private async Task<IReadOnlyList<WorkshopItem>> SafeQueryAsync(WorkshopQuery query, CancellationToken ct)
    {
        try
        {
            return (await QueryAsync(query, ct)).Items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private void ClearResults()
    {
        _previewCts.Cancel();
        _previewCts = new CancellationTokenSource();
        SelectedCard = null;
        foreach (var card in WorkshopResults) card.Preview?.Dispose();
        WorkshopResults.Clear();
        ResultsHeader = "";
        SetWsPages(0, 0);
    }

    /// <summary>The filters that need no new search: translations, scenarios, abandoned and installed mods.</summary>
    private bool PassesWorkshopFilters(WorkshopItem item) => item.IsCollection ? PassesCollectionSize(item) :
        (!HideTranslations || !item.IsTranslation)
        && (!OnlyMods || !item.IsScenario)
        && (!HideAbandoned || item.UpdatedUtc > DateTime.UtcNow.AddDays(-365))
        && (!HideInstalled || !_byWorkshopId.ContainsKey(item.Id));

    /// <summary>Official expansions this player does not have (for "needs Biotech" marks).</summary>
    private IReadOnlyList<string> MissingDlcs() =>
        WorkshopItem.Dlcs.Where(d => !_installed.ContainsKey("ludeon.rimworld." + d.ToLowerInvariant())).ToList();

    private void AddCards(IEnumerable<WorkshopItem> items)
    {
        var known = WorkshopResults.Select(c => c.Id).ToHashSet();
        var missingDlcs = MissingDlcs();
        var added = new List<WorkshopCardViewModel>();
        foreach (var item in items)
        {
            if (!PassesWorkshopFilters(item) || !known.Add(item.Id)) continue;
            var card = new WorkshopCardViewModel(item, _gameVersion, missingDlcs);
            UpdateCardState(card);
            WorkshopResults.Add(card);
            added.Add(card);
            _ = LoadCardPreviewAsync(card, _previewCts.Token);
        }
        OnCardsAdded(added);
    }

    // ───────────────────────── Disk cleanup ─────────────────────────

    public ObservableCollection<CleanupRowViewModel> CleanupRows { get; } = [];
    [ObservableProperty] public partial string CleanupSummary { get; set; } = "";
    [ObservableProperty] public partial bool CanCleanup { get; set; }
    public bool HasCleanupRows => CleanupRows.Count > 0;

    /// <summary>Workshop mods that no modpack, no save and the game's list use.</summary>
    private void RefreshCleanup()
    {
        CleanupRows.Clear();
        var rows = _items.Values
            .Where(i => i.IsWorkshop && i.Mod.WorkshopId is { } wid && !_unsubscribed.Contains(wid) && IsUnused(i))
            .Select(i => new CleanupRowViewModel(i.Mod.WorkshopId!.Value, i.Id, i.Name, i.Profile?.SizeBytes ?? 0,
                _manifest.TryGetValue(i.Mod.WorkshopId!.Value, out var m) ? m.UpdatedUtc : null, UpdateCleanupSummary))
            .OrderByDescending(r => r.SizeBytes);
        foreach (var row in rows) CleanupRows.Add(row);
        OnPropertyChanged(nameof(HasCleanupRows));
        UpdateCleanupSummary();
    }

    private void UpdateCleanupSummary()
    {
        var picked = CleanupRows.Where(r => r.IsChecked).ToList();
        CanCleanup = picked.Count > 0;
        CleanupSummary = Loc.Format("Clean.Summary", CleanupRows.Count, Humanize.Size(CleanupRows.Sum(r => r.SizeBytes)) is { Length: > 0 } all ? all : "0",
            picked.Count, Humanize.Size(picked.Sum(r => r.SizeBytes)) is { Length: > 0 } some ? some : "0");
    }

    [RelayCommand]
    private void CleanupSelect(string all)
    {
        foreach (var row in CleanupRows) row.IsChecked = all == "all";
    }

    [RelayCommand]
    private async Task CleanupUnsubscribe()
    {
        var picked = CleanupRows.Where(r => r.IsChecked).ToList();
        if (picked.Count == 0) return;
        var size = Humanize.Size(picked.Sum(r => r.SizeBytes));
        var names = string.Join("\n", picked.Take(12).Select(r => "• " + r.Name)) + (picked.Count > 12 ? "\n…" : "");
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Clean.ConfirmTitle"),
            Text = Loc.Format("Clean.ConfirmText", picked.Count, size) + "\n\n" + names,
            PrimaryText = Loc.Format("Clean.Button", picked.Count),
            PrimaryIsDanger = true,
        });
        if (answer != "primary" || !await EnsureSteamForDownloadsAsync()) return;
        try
        {
            await _steam.UnsubscribeAsync(picked.Select(r => r.Id));
            foreach (var row in picked)
            {
                _unsubscribed.Add(row.Id);
                CleanupRows.Remove(row);
            }
            RefreshCardStates();
            OnPropertyChanged(nameof(HasCleanupRows));
            UpdateCleanupSummary();
            ShowToast(Loc.Format("Clean.Done", picked.Count, size));
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ProblemText(ex));
        }
        finally
        {
            UpdateSteamStatus();
        }
    }

    [RelayCommand]
    private void CleanupShow(CleanupRowViewModel row) => OpenMod(row.PackageId);

    private async Task LoadCardPreviewAsync(WorkshopCardViewModel card, CancellationToken ct)
    {
        var picture = await _previews.GetAsync(card.Item.PreviewUrl, 320, ct);
        // The page was turned meanwhile: the card is gone, so is its picture.
        if (picture is not null && (ct.IsCancellationRequested || !WorkshopResults.Contains(card))) picture.Dispose();
        else card.Preview = picture;
    }

    partial void OnSelectedCardChanged(WorkshopCardViewModel? value)
    {
        var old = SelectedCardPreview;
        SelectedCardPreview = value?.Preview;
        if (old is not null && !WorkshopResults.Any(c => ReferenceEquals(c.Preview, old))) old.Dispose();
        if (value is not null)
        {
            _ = LoadSelectedPreviewAsync(value);
            OnCardSelectedForTranslation(value);
        }
        OnCardOpened(value);
    }

    private async Task LoadSelectedPreviewAsync(WorkshopCardViewModel card)
    {
        var big = await _previews.GetAsync(card.Item.PreviewUrl, 640);
        if (big is null) return;
        if (SelectedCard != card)
        {
            big.Dispose();
            return;
        }
        var old = SelectedCardPreview;
        SelectedCardPreview = big;
        if (old is not null && !WorkshopResults.Any(c => ReferenceEquals(c.Preview, old))) old.Dispose();
    }

    [RelayCommand]
    private void CloseCard() => SelectedCard = null;

    // ───────────────────────── Card states and actions ─────────────────────────

    /// <summary>Rebuilt after every scan: which Workshop ids are on disk.</summary>
    private void RefreshWorkshopIndex()
    {
        _byWorkshopId = new Dictionary<ulong, ModInfo>();
        foreach (var mod in _scanned.Where(m => m.WorkshopId is not null).OrderBy(m => m.Source == ModSource.Workshop ? 0 : 1))
            _byWorkshopId.TryAdd(mod.WorkshopId!.Value, mod);
        _unsubscribed.RemoveWhere(id => !_byWorkshopId.ContainsKey(id));
        ComputeUpdates();
        RefreshCardStates();
        OnPropertyChanged(nameof(WorkshopVersionText));
    }

    private void RefreshCardStates()
    {
        foreach (var card in WorkshopResults) UpdateCardState(card);
    }

    private void UpdateCardState(WorkshopCardViewModel card)
    {
        if (Downloads.FirstOrDefault(d => d.Id == card.Id && d.IsRunning) is { } download)
        {
            card.State = download.State == DownloadState.Downloading ? CardState.Downloading : CardState.Queued;
            card.Progress = download.Progress;
        }
        else if (_unsubscribed.Contains(card.Id)) card.State = CardState.Unsubscribed;
        else if (_byWorkshopId.TryGetValue(card.Id, out var mod))
        {
            card.PackageId = mod.PackageId;
            card.State = _items.TryGetValue(mod.PackageId, out var item) && item.IsActive ? CardState.Active : CardState.Installed;
        }
        else card.State = CardState.NotInstalled;
    }

    [RelayCommand]
    private Task WorkshopSubscribe(WorkshopCardViewModel card) =>
        StartDownloadsAsync([new DownloadViewModel(card.Id, card.Title) { EnableWhenDone = EnableAfterDownload }]);

    [RelayCommand]
    private void WorkshopEnable(WorkshopCardViewModel card)
    {
        if (card.PackageId is not { } id || !_items.TryGetValue(id, out var item)) return;
        Activate([item]);
        SortCarefully();
        ShowToast(Loc.Format("Ws.Enabled", item.Name));
    }

    [RelayCommand]
    private void WorkshopShowInList(WorkshopCardViewModel card)
    {
        if (card.PackageId is { } id) OpenMod(id);
    }

    [RelayCommand]
    private void WorkshopOpenPage(WorkshopCardViewModel card) => OpenUrl(card.Item.Url);

    [RelayCommand]
    private async Task WorkshopUnsubscribe(WorkshopCardViewModel card)
    {
        var name = card.PackageId is { } pid ? NameOf(pid, card.Title) : card.Title;
        var text = Loc.Format("Ws.Unsub.Text", name);
        if (_saveUsage.Count == 0) _saveUsage = await Task.Run(() => SaveUsage.Find(_paths.ConfigDir));
        if (card.PackageId is { } id && _saveUsage.TryGetValue(id, out var saves) && saves.Count > 0)
            text += "\n\n" + Loc.Format("Fix.Preview.UsedInSaves", saves.Count, string.Join(", ", saves.Take(5)));
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Ws.Unsub.Title"),
            Text = text,
            PrimaryText = Loc.Get("Ws.Unsubscribe"),
            PrimaryIsDanger = true,
        });
        if (answer != "primary") return;
        if (GameLauncher.IsRunning() || !SteamBridge.IsSteamRunning())
        {
            await EnsureSteamForDownloadsAsync();
            return;
        }
        try
        {
            await _steam.UnsubscribeAsync([card.Id]);
            if (card.PackageId is { } pkg && _items.TryGetValue(pkg, out var item) && item.IsActive) Deactivate([item]);
            _unsubscribed.Add(card.Id);
            UpdateCardState(card);
            ShowToast(Loc.Format("Ws.Unsub.Done", name));
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Msg.Error", ProblemText(ex));
        }
        finally
        {
            UpdateSteamStatus();
        }
    }

    // ───────────────────────── Download queue ─────────────────────────

    private void UpdateDownloadCounters()
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(RunningDownloads));
        OnPropertyChanged(nameof(HasRunningDownloads));
        OnPropertyChanged(nameof(DownloadsTabText));
        UpdateDownloadIndicator();
    }

    // ── What the whole window shows while mods download: the strip under the top bar, the footer, the rows ──

    /// <summary>All running downloads together, 0..1.</summary>
    [ObservableProperty] public partial double DownloadsProgress { get; set; }

    /// <summary>Nothing has moved yet (subscribing, waiting in Steam's queue): the strip just runs.</summary>
    [ObservableProperty] public partial bool DownloadsIndeterminate { get; set; }

    /// <summary>Every running download stands still (no Steam, no connection): shown in the warning colour.</summary>
    [ObservableProperty] public partial bool DownloadsStalled { get; set; }

    [ObservableProperty] public partial string DownloadsFooterText { get; set; } = "";

    private readonly List<ModItemViewModel> _downloadingRows = [];

    private void UpdateDownloadIndicator()
    {
        var running = Downloads.Where(d => d.IsRunning).ToList();
        DownloadsProgress = running.Count == 0 ? 0 : running.Average(d => d.Progress);
        DownloadsIndeterminate = running.Count > 0 && running.All(d => d.Progress <= 0.001);
        DownloadsStalled = running.Count > 0 && running.All(d => d.IsWaiting || d.State == DownloadState.Queued && d.NextTryUtc > DateTime.UtcNow);
        DownloadsFooterText = running.Count == 0 ? ""
            : DownloadsStalled ? Loc.Format("Dl.Footer.Waiting", running.Count)
            : DownloadsIndeterminate ? Loc.Format("Dl.Footer.Starting", running.Count)
            : Loc.Format("Dl.Footer.Running", running.Count, $"{DownloadsProgress:P0}");

        // Rows of the lists: a mod being downloaded (a modpack's placeholder) or updated shows its progress.
        var byId = running.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        var now = new List<ModItemViewModel>();
        if (byId.Count > 0)
            foreach (var item in _items.Values)
                if (RowWorkshopId(item) is { } wid && byId.TryGetValue(wid, out var d))
                {
                    item.DownloadProgress = d.Progress;
                    item.IsDownloadWaiting = d.IsWaiting;
                    now.Add(item);
                }
        foreach (var gone in _downloadingRows.Except(now)) gone.DownloadProgress = null;
        _downloadingRows.Clear();
        _downloadingRows.AddRange(now);
    }

#if DEBUG
    /// <summary>Test builds only (RIMFORGE_DEMO_DOWNLOADS=1): two pretend updates, to see the indicator without touching Steam.</summary>
    public void DemoDownloads()
    {
        var mods = _active.Where(i => i.Mod.WorkshopId is not null).Skip(3).Take(2).ToList();
        if (mods.Count < 2) return;
        Downloads.Add(new DownloadViewModel(mods[0].Mod.WorkshopId!.Value, mods[0].Name) { IsUpdate = true, State = DownloadState.Downloading, Progress = 0.45 });
        Downloads.Add(new DownloadViewModel(mods[1].Mod.WorkshopId!.Value, mods[1].Name) { IsUpdate = true, State = DownloadState.Downloading, Progress = 0.2 });
        UpdateDownloadCounters();
    }
#endif

    private static ulong? RowWorkshopId(ModItemViewModel item) =>
        item.Mod.WorkshopId ?? (item.Id.StartsWith(PlaceholderPrefix) && ulong.TryParse(item.Id[PlaceholderPrefix.Length..], out var wid) ? wid : null);

    [RelayCommand]
    private void ShowDownloads()
    {
        WorkshopSection = "downloads";
        ShowPage("workshop");
    }

    private async Task StartDownloadsAsync(IReadOnlyList<DownloadViewModel> requested)
    {
        var fresh = requested
            .Where(d => !Downloads.Any(x => x.Id == d.Id && x.IsRunning))
            .Where(d => d.IsUpdate || !_byWorkshopId.ContainsKey(d.Id) || _unsubscribed.Contains(d.Id))
            .DistinctBy(d => d.Id)
            .ToList();
        if (fresh.Count == 0)
        {
            ShowToast(Loc.Get("Dl.NothingToDo"));
            return;
        }
        if (!await EnsureSteamForDownloadsAsync()) return;

        foreach (var d in fresh)
        {
            foreach (var old in Downloads.Where(x => x.Id == d.Id).ToList()) Downloads.Remove(old);
            Downloads.Add(d);
        }
        ShowToast(fresh.Count == 1 ? Loc.Format("Dl.Started", fresh[0].Title) : Loc.Format("Dl.StartedMany", fresh.Count));
        RefreshCardStates();
        if (!_downloadsRunning) await RunDownloadsAsync();
    }

    /// <summary>How long a new subscription keeps being retried while Steam can't be reached.</summary>
    private static readonly TimeSpan SubscribeRetryFor = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Watches the queue until every download is done. A lost connection, a closed Steam or a stalled download is
    /// never a failure by itself: Steam carries on (or resumes) by itself, so RimForge keeps watching, says why it is
    /// waiting and picks the mod up the moment it lands on disk.
    /// </summary>
    private async Task RunDownloadsAsync()
    {
        _downloadsRunning = true;
        try
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                var queued = Downloads.Where(d => d.State == DownloadState.Queued && d.NextTryUtc <= now).ToList();
                if (queued.Count > 0) await SubscribeQueuedAsync(queued);

                var running = Downloads.Where(d => d.State == DownloadState.Downloading).ToList();
                if (running.Count == 0)
                {
                    if (!Downloads.Any(d => d.State == DownloadState.Queued)) break;
                    await Task.Delay(1000);
                    continue;
                }

                IReadOnlyList<ItemState>? states = null;
                try
                {
                    states = await _steam.GetStatesAsync(running.Select(d => d.Id));
                }
                catch (Exception ex)
                {
                    // No answer from Steam: keep waiting; a mod that shows up on disk meanwhile is finished.
                    var reason = WaitText(ex);
                    foreach (var d in running)
                    {
                        if (FinishedOnDisk(d) is { } folder) Finish(d, folder);
                        else Wait(d, reason);
                    }
                }

                foreach (var state in states ?? [])
                {
                    if (running.FirstOrDefault(d => d.Id == state.Id) is not { } d) continue;
                    if (state.IsReady)
                    {
                        Finish(d, state.Folder);
                        continue;
                    }
                    if (d.IsResumed && !state.Subscribed && !state.Installed)
                    {
                        Downloads.Remove(d); // Steam isn't getting it: nothing to watch
                        continue;
                    }
                    if (state.BytesTotal > 0 && state.Progress > d.Progress + 0.0005)
                    {
                        d.Progress = state.Progress;
                        d.LastProgressUtc = DateTime.UtcNow;
                        d.WaitingSinceUtc = null;
                        d.WaitReason = null;
                    }
                    else if (DateTime.UtcNow - d.LastProgressUtc > TimeSpan.FromMinutes(2))
                    {
                        if (!state.Subscribed && DateTime.UtcNow - d.LastProgressUtc > TimeSpan.FromMinutes(5))
                            Fail(d, Loc.Get("Dl.NotSubscribed"));
                        else
                            Wait(d, Loc.Get(state.Pending || state.Downloading ? "Dl.Wait.Stalled" : "Dl.Wait.SteamQueue"));
                    }
                    else if (d.WaitReason is not null)
                    {
                        d.WaitReason = null; // Steam answers again
                    }
                }
                RefreshCardStates();
                UpdateDownloadCounters();
                // Slower checks while nothing moves, so a long wait doesn't keep Steam busy.
                await Task.Delay(running.All(d => d.IsWaiting) ? 5000 : 800);
            }
        }
        finally
        {
            _downloadsRunning = false;
            UpdateDownloadCounters();
        }
        await AfterDownloadsAsync();
    }

    private async Task SubscribeQueuedAsync(List<DownloadViewModel> queued)
    {
        foreach (var d in queued) d.State = DownloadState.Subscribing;
        try
        {
            if (queued.Where(d => !d.IsUpdate && !d.IsResumed).Select(d => d.Id).ToList() is { Count: > 0 } subscribe)
                await _steam.SubscribeAsync(subscribe);
            if (queued.Where(d => d.IsUpdate).Select(d => d.Id).ToList() is { Count: > 0 } update)
                await _steam.DownloadAsync(update);
            foreach (var d in queued)
            {
                _unsubscribed.Remove(d.Id);
                d.State = DownloadState.Downloading;
                d.LastProgressUtc = DateTime.UtcNow;
                d.WaitReason = null;
                d.WaitingSinceUtc = null;
            }
        }
        catch (Exception ex)
        {
            // Probably the connection: try again in a while instead of giving up at once.
            foreach (var d in queued)
            {
                d.WaitingSinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - d.WaitingSinceUtc > SubscribeRetryFor)
                {
                    Fail(d, ProblemText(ex));
                    continue;
                }
                d.State = DownloadState.Queued;
                d.NextTryUtc = DateTime.UtcNow.AddSeconds(15);
                d.WaitReason = WaitText(ex);
            }
        }
        UpdateSteamStatus();
    }

    private void Finish(DownloadViewModel d, string? folder)
    {
        d.Progress = 1;
        d.Folder = folder;
        d.WaitReason = null;
        d.State = DownloadState.Done;
        QueueDependencies(d);
    }

    private static void Wait(DownloadViewModel d, string reason)
    {
        d.WaitingSinceUtc ??= DateTime.UtcNow;
        d.WaitReason = reason;
    }

    /// <summary>Why RimForge waits, in words the player can act on.</summary>
    private static string WaitText(Exception ex) => ex is SteamUnavailableException { Problem: var problem }
        ? problem switch
        {
            SteamProblem.SteamNotRunning => Loc.Get("Dl.Wait.SteamClosed"),
            SteamProblem.GameRunning => Loc.Get("Dl.Wait.GameRunning"),
            SteamProblem.NotLoggedIn => Loc.Get("Dl.Wait.NotLoggedIn"),
            _ => Loc.Get("Dl.Wait.NoAnswer"),
        }
        : Loc.Get("Dl.Wait.NoAnswer");

    /// <summary>A new mod whose folder Steam has finished (About.xml is there), seen without asking Steam.</summary>
    private string? FinishedOnDisk(DownloadViewModel d)
    {
        if (d.IsUpdate || _steamPaths.WorkshopDir is not { } dir) return null;
        var folder = Path.Combine(dir, d.Id.ToString());
        return File.Exists(Path.Combine(folder, "About", "About.xml")) ? folder : null;
    }

    /// <summary>The player stops watching a download that waits (Steam keeps its subscription).</summary>
    [RelayCommand]
    private void StopWaiting(DownloadViewModel d)
    {
        if (!d.IsRunning) return;
        Fail(d, Loc.Get("Dl.Stopped"));
        UpdateDownloadCounters();
    }

    /// <summary>
    /// After a restart: mods of the modpack that are still placeholders ("workshop:123", e.g. from a collection) may be
    /// on their way from Steam — watch them again, so they are put in place when they arrive.
    /// </summary>
    public void ResumeUnfinishedDownloads()
    {
        if (!SteamBridge.IsSteamRunning() || GameLauncher.IsRunning()) return;
        var waiting = Snapshot()
            .Where(id => id.StartsWith(PlaceholderPrefix) && ulong.TryParse(id[PlaceholderPrefix.Length..], out _))
            .Select(id => ulong.Parse(id[PlaceholderPrefix.Length..]))
            .Where(wid => !_byWorkshopId.ContainsKey(wid) && !Downloads.Any(d => d.Id == wid))
            .ToList();
        if (waiting.Count == 0) return;
        foreach (var wid in waiting)
            Downloads.Add(new DownloadViewModel(wid, _hints.GetValueOrDefault(PlaceholderPrefix + wid)?.Name ?? "Workshop " + wid)
            {
                IsResumed = true,
                State = DownloadState.Downloading,
            });
        UpdateDownloadCounters();
        if (!_downloadsRunning) _ = RunDownloadsAsync();
    }

    private void Fail(DownloadViewModel d, string error)
    {
        d.Error = error;
        d.State = DownloadState.Failed;
    }

    /// <summary>Reads the downloaded mod's About.xml and queues the mods it requires that are not on disk yet.</summary>
    private void QueueDependencies(DownloadViewModel d)
    {
        if (d.IsUpdate || d.Folder is null) return;
        var mod = ModScanner.ReadMod(d.Folder, ModSource.Workshop, _gameVersion ?? "1.6");
        if (mod is null) return;
        d.PackageId = mod.PackageId;

        // A requirement of something that will be on in the modpack must be on too.
        var parentOn = d.EnableWhenDone || Snapshot().Contains(PlaceholderPrefix + d.Id) || Snapshot().Contains(mod.PackageId);
        var manual = new List<string>();
        foreach (var dep in mod.Dependencies)
        {
            if (IsInstalled(dep.PackageId) || Downloads.Any(x => x.PackageId == dep.PackageId && !x.IsFailed)) continue;
            if (WorkshopIds.Parse(dep.SteamWorkshopUrl) is { } wid)
            {
                if (!Downloads.Any(x => x.Id == wid && !x.IsFailed) && !_byWorkshopId.ContainsKey(wid))
                    Downloads.Add(new DownloadViewModel(wid, dep.DisplayName ?? dep.PackageId) { NeededBy = d.Title, EnableWhenDone = parentOn });
            }
            else manual.Add(dep.DisplayName ?? dep.PackageId);
        }
        if (manual.Count > 0) d.Note = Loc.Format("Dl.NeedsManual", string.Join(", ", manual));
    }

    /// <summary>Once the queue is empty: rescan the mods, put downloaded ones into the modpack and sort them in.</summary>
    private async Task AfterDownloadsAsync()
    {
        var done = Downloads.Where(d => d.IsDone && !d.Handled).ToList();
        if (done.Count == 0) return;
        foreach (var d in done) d.Handled = true;

        // A saved copy of the game does not see the Workshop folder: new mods are frozen into it.
        AddDownloadsToCopy(done.Where(d => !d.IsUpdate && d.Folder is not null).Select(d => d.Folder!));
        await UnsubscribeReplacedAsync(done);
        await LoadAsync();
        ApplyPendingSettings();

        var changed = ReplacePlaceholders();
        changed |= ApplyReplacements(done);
        var toEnable = done.Where(d => d.EnableWhenDone && !d.IsUpdate)
            .Select(d => _byWorkshopId.GetValueOrDefault(d.Id))
            .OfType<ModInfo>()
            .Select(m => _items.GetValueOrDefault(m.PackageId))
            .OfType<ModItemViewModel>()
            .Where(i => !i.IsActive)
            .ToList();
        if (toEnable.Count > 0) Activate(toEnable);
        if (changed || toEnable.Count > 0) SortCarefully();
        RefreshCardStates();

        var updates = done.Count(d => d.IsUpdate);
        ShowToast(toEnable.Count > 0
            ? Loc.Format("Dl.FinishedEnabled", done.Count - updates, toEnable.Count)
            : updates == done.Count ? Loc.Format("Dl.FinishedUpdates", updates) : Loc.Format("Dl.Finished", done.Count - updates));
        _ = AfterDownloadsTranslationsAsync(done);
    }

    /// <summary>
    /// "workshop:123" placeholders (mods of a collection that were still downloading) whose mod is on disk now
    /// become the mod's package id. Returns the same list when there is nothing to swap.
    /// </summary>
    private List<string> ResolvePlaceholders(IReadOnlyList<string> ids)
    {
        List<string>? result = null;
        for (var i = 0; i < ids.Count; i++)
        {
            if (!ids[i].StartsWith(PlaceholderPrefix) || !ulong.TryParse(ids[i][PlaceholderPrefix.Length..], out var wid)) continue;
            if (_scanned.FirstOrDefault(m => m.WorkshopId == wid && _installed.ContainsKey(m.PackageId)) is not { } mod) continue;
            result ??= ids.ToList();
            result[i] = mod.PackageId;
        }
        return result?.Distinct().ToList() ?? ids.ToList();
    }

    /// <summary>Swaps "workshop:123" placeholders (from collections) for the real mods once they are on disk.</summary>
    private bool ReplacePlaceholders()
    {
        var ids = Snapshot();
        var changed = false;
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i].StartsWith(PlaceholderPrefix) && ulong.TryParse(ids[i][PlaceholderPrefix.Length..], out var wid)
                && _byWorkshopId.TryGetValue(wid, out var mod))
            {
                ids[i] = mod.PackageId;
                changed = true;
            }
        }
        if (changed) Change(() => _active = ids.Distinct().Select(ItemFor).ToList());
        return changed;
    }

    [RelayCommand]
    private void ClearFinishedDownloads()
    {
        foreach (var d in Downloads.Where(d => !d.IsRunning).ToList()) Downloads.Remove(d);
    }

    [RelayCommand]
    private Task RetryDownload(DownloadViewModel d) =>
        StartDownloadsAsync([new DownloadViewModel(d.Id, d.Title)
        {
            NeededBy = d.NeededBy, EnableWhenDone = d.EnableWhenDone, IsUpdate = d.IsUpdate, ReplacesId = d.ReplacesId, UnsubscribeAfter = d.UnsubscribeAfter,
            TranslationFor = d.TranslationFor,
        }]);

    [RelayCommand]
    private void OpenDownloadPage(DownloadViewModel d) => OpenUrl(WorkshopIds.PageUrl(d.Id));

    // ───────────────────────── Missing mods of the modpack ─────────────────────────

    /// <summary>Workshop id of a mod in the list that is not on disk, if known.</summary>
    private ulong? WorkshopIdOfMissing(string id) =>
        id.StartsWith(PlaceholderPrefix) && ulong.TryParse(id[PlaceholderPrefix.Length..], out var wid) ? wid
        : _items.TryGetValue(id, out var item) ? item.Mod.WorkshopId
        : _hints.TryGetValue(id, out var hint) ? hint.WorkshopId : null;

    private List<DownloadViewModel> DownloadableForIssues() =>
        _issues.Select(i => i.Kind switch
            {
                IssueKind.NotInstalled when WorkshopIdOfMissing(i.ModId) is { } wid =>
                    new DownloadViewModel(wid, NameOf(i.ModId)),
                IssueKind.DependencyMissing when WorkshopIds.Parse(i.Detail) is { } wid =>
                    new DownloadViewModel(wid, i.OtherName ?? i.OtherId ?? wid.ToString()) { NeededBy = NameOf(i.ModId), EnableWhenDone = true },
                _ => null,
            })
            .OfType<DownloadViewModel>()
            .DistinctBy(d => d.Id)
            .ToList();

    private void CountDownloadable() => DownloadableMissing = DownloadableForIssues().Count;

    [RelayCommand]
    private async Task DownloadMissing()
    {
        var list = DownloadableForIssues();
        if (list.Count == 0) return;
        var lines = list.Take(15).Select(d => "• " + d.Title + (d.NeededBy is { } by ? " — " + Loc.Format("Dl.NeededBy", by) : ""));
        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Get("Dl.ConfirmTitle"),
            Text = Loc.Format("Dl.ConfirmText", list.Count) + "\n\n" + string.Join("\n", lines) + (list.Count > 15 ? "\n…" : ""),
            PrimaryText = Loc.Get("Fix.Download"),
        });
        if (answer == "primary") await StartDownloadsAsync(list);
    }

    /// <summary>Download (and turn on) one mod that the list needs.</summary>
    private void DownloadOne(ulong workshopId, string title, string? neededBy, bool enable) =>
        _ = StartDownloadsAsync([new DownloadViewModel(workshopId, title) { NeededBy = neededBy, EnableWhenDone = enable }]);

    // ───────────────────────── Updates ─────────────────────────

    /// <summary>What changed since the player last looked, from Steam's own records (no network).</summary>
    private void ComputeUpdates()
    {
        _manifest = WorkshopManifest.Load(WorkshopManifest.FileFor(_paths.WorkshopDir));
        var seen = _settings.WorkshopSeenUtc ?? DateTime.UtcNow.AddDays(-7);
        var inPack = Snapshot().ToHashSet();
        var recent = new List<UpdateRowViewModel>();
        var pending = new List<UpdateRowViewModel>();
        var removed = new List<UpdateRowViewModel>();

        foreach (var (wid, mod) in _byWorkshopId.Where(kv => kv.Value.Source == ModSource.Workshop))
        {
            UpdateRowViewModel Row(UpdateKind kind, DateTime when) => new()
            {
                WorkshopId = wid, PackageId = mod.PackageId, Name = mod.Name, Kind = kind, UpdatedUtc = when,
                InModpack = inPack.Contains(mod.PackageId), Folder = mod.FolderPath,
            };
            _webDetails.TryGetValue(wid, out var web);
            if (_manifest.TryGetValue(wid, out var local))
            {
                if (local.UpdatedUtc > seen) recent.Add(Row(UpdateKind.Recent, local.UpdatedUtc));
                var newest = web is { Removed: false } && web.UpdatedUtc > local.LatestUpdatedUtc ? web.UpdatedUtc : local.LatestUpdatedUtc;
                if (newest > local.UpdatedUtc.AddMinutes(1)) pending.Add(Row(UpdateKind.Pending, newest));
            }
            if (web is { Removed: true }) removed.Add(Row(UpdateKind.Removed, default));
        }

        Fill(RecentUpdates, recent);
        Fill(PendingUpdates, pending);
        Fill(RemovedMods, removed);
        OnPropertyChanged(nameof(HasRecentUpdates));
        OnPropertyChanged(nameof(HasPendingUpdates));
        OnPropertyChanged(nameof(HasRemovedMods));
        OnPropertyChanged(nameof(UpdatesEmpty));
        OnPropertyChanged(nameof(UpdatesTabText));
        OnPropertyChanged(nameof(UpdatesSeenText));

        static void Fill(ObservableCollection<UpdateRowViewModel> target, List<UpdateRowViewModel> rows)
        {
            target.Clear();
            foreach (var r in rows.OrderByDescending(r => r.InModpack).ThenByDescending(r => r.UpdatedUtc).ThenBy(r => r.Name))
                target.Add(r);
        }
    }

    /// <summary>Asks the Workshop about every installed mod: newer versions and pages that were deleted.</summary>
    private async Task CheckUpdatesOnlineAsync()
    {
        if (_webCheckDone || IsCheckingUpdates) return;
        var ids = _byWorkshopId.Where(kv => kv.Value.Source == ModSource.Workshop).Select(kv => kv.Key).ToList();
        if (ids.Count == 0) return;
        IsCheckingUpdates = true;
        try
        {
            var details = await _web.GetDetailsAsync(ids);
            _webDetails = details.ToDictionary(d => d.Id);
            _webCheckDone = true;
            ComputeUpdates();
            UpdateChipCounts();
        }
        catch (Exception)
        {
            // offline: the list from Steam's own records is still shown
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private Task RecheckUpdates()
    {
        _webCheckDone = false;
        ComputeUpdates();
        return CheckUpdatesOnlineAsync();
    }

    [RelayCommand]
    private void MarkUpdatesSeen()
    {
        _settings.WorkshopSeenUtc = DateTime.UtcNow;
        _settings.Save();
        ComputeUpdates();
        ShowToast(Loc.Get("Upd.Seen"));
    }

    [RelayCommand]
    private Task UpdateAllPending() =>
        StartDownloadsAsync(PendingUpdates.Select(r => new DownloadViewModel(r.WorkshopId, r.Name) { IsUpdate = true }).ToList());

    [RelayCommand]
    private async Task ToggleChangelog(UpdateRowViewModel row)
    {
        row.IsExpanded = !row.IsExpanded;
        if (!row.IsExpanded || row.Changelog is not null || row.IsLoadingChangelog) return;
        row.IsLoadingChangelog = true;
        try
        {
            var entries = await _web.GetChangelogAsync(row.WorkshopId);
            var seen = _settings.WorkshopSeenUtc ?? DateTime.UtcNow.AddDays(-7);
            var fresh = entries.Where(e => e.DateUtc > seen.AddDays(-1)).Take(5).ToList();
            if (fresh.Count == 0) fresh = entries.Take(1).ToList();
            row.Changelog = fresh.Count == 0
                ? Loc.Get("Upd.NoChangelog")
                : string.Join("\n\n", fresh.Select(e => $"{Dates.Format(e.DateUtc.ToLocalTime())}\n{(e.Text.Length > 0 ? e.Text : Loc.Get("Upd.EmptyNote"))}"));
        }
        catch (Exception ex)
        {
            row.Changelog = Loc.Format("Upd.ChangelogFailed", ex.Message);
        }
        finally
        {
            row.IsLoadingChangelog = false;
        }
    }

    [RelayCommand]
    private void OpenUpdatePage(UpdateRowViewModel row) => OpenUrl(row.Kind == UpdateKind.Removed
        ? WorkshopIds.PageUrl(row.WorkshopId)
        : WorkshopIds.ChangelogUrl(row.WorkshopId));

    [RelayCommand]
    private void OpenUpdateFolder(UpdateRowViewModel row)
    {
        if (row.Folder is { Length: > 0 } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void ShowUpdateInList(UpdateRowViewModel row) => OpenMod(row.PackageId);

    // ───────────────────────── Modpack from a Steam collection ─────────────────────────

    [RelayCommand]
    private async Task ImportCollection()
    {
        var dialog = new DialogViewModel
        {
            Title = Loc.Get("Coll.Title"),
            Text = Loc.Get("Coll.Text"),
            HasInput = true,
            InputHint = Loc.Get("Coll.Hint"),
            PrimaryText = Loc.Get("Coll.Next"),
        };
        if (await AskAsync(dialog) != "primary") return;
        if (WorkshopIds.Parse(dialog.Input) is not { } collectionId)
        {
            ShowToast(Loc.Get("Coll.BadLink"));
            return;
        }

        Message = Loc.Get("Coll.Loading"); // stays until the collection is read: big ones take a few seconds
        string title;
        List<WorkshopItem> items;
        try
        {
            var (name, ids) = await _web.GetCollectionAsync(collectionId);
            if (ids.Count == 0)
            {
                Message = null;
                ShowToast(Loc.Get("Coll.Empty"));
                return;
            }
            var details = (await _web.GetDetailsAsync(ids)).ToDictionary(d => d.Id);
            title = name;
            items = ids.Select(id => details.GetValueOrDefault(id) ?? new WorkshopItem { Id = id, Title = id.ToString(), Removed = true }).ToList();
        }
        catch (Exception ex)
        {
            Message = Loc.Format("Coll.Failed", ex.Message);
            return;
        }
        Message = null;

        var have = items.Where(i => _byWorkshopId.ContainsKey(i.Id)).ToList();
        var removed = items.Where(i => !_byWorkshopId.ContainsKey(i.Id) && i.Removed).ToList();
        var missing = items.Where(i => !_byWorkshopId.ContainsKey(i.Id) && !i.Removed).ToList();
        var text = Loc.Format("Coll.Summary", items.Count, have.Count, missing.Count, Humanize.Size(missing.Sum(i => i.SizeBytes)));
        if (removed.Count > 0) text += "\n" + Loc.Format("Coll.Removed", removed.Count, string.Join(", ", removed.Take(5).Select(r => r.Title)));
        text += "\n\n" + Loc.Get(missing.Count > 0 ? "Coll.WillDownload" : "Coll.WillCreate");
        var confirm = await AskAsync(new DialogViewModel
        {
            Title = Loc.Format("Coll.ConfirmTitle", title),
            Text = text,
            PrimaryText = Loc.Get(missing.Count > 0 ? "Coll.CreateAndDownload" : "Dialog.Create"),
        });
        if (confirm != "primary" || !await ConfirmLeaveAsync()) return;
        await CreateWorkshopModpackAsync(title, WorkshopIds.PageUrl(collectionId), items.Where(i => !i.Removed || _byWorkshopId.ContainsKey(i.Id)));
    }

    /// <summary>
    /// A new active modpack from Workshop mods (a collection, the assistant's pack): the game and its expansions first,
    /// then the mods in the given order; mods not on disk yet are downloaded and take their place when they arrive.
    /// </summary>
    private async Task CreateWorkshopModpackAsync(string name, string? notes, IEnumerable<WorkshopItem> items)
    {
        var ids = OfficialOrder.Where(_installed.ContainsKey).ToList();
        var hints = new Dictionary<string, ProfileMod>();
        var missing = new List<WorkshopItem>();
        foreach (var item in items)
        {
            if (_byWorkshopId.TryGetValue(item.Id, out var mod)) ids.Add(mod.PackageId);
            else
            {
                var placeholder = PlaceholderPrefix + item.Id;
                ids.Add(placeholder);
                hints[placeholder] = new ProfileMod(placeholder, item.Title, item.Id);
                missing.Add(item);
            }
        }
        var profile = new Profile { Name = name, Notes = notes };
        profile.SetMods(ids, _installed, hints);
        Apply(AddProfile(profile));
        SortCarefully();
        CurrentPage = "mods";

        if (missing.Count > 0)
            await StartDownloadsAsync(missing.Select(i => new DownloadViewModel(i.Id, i.Title)).ToList());
    }
}
