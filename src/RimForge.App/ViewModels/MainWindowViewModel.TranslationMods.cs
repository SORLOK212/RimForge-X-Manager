using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Models;
using RimForge.Core.Translation;
using RimForge.Steam.Translations;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

/// <summary>
/// Translations for the player's mods: which mods of the modpack lack the game's language, and which Workshop
/// translations (single ones or packs) fill the gap — downloaded and turned on with one button.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Below this share a mod counts as not translated.</summary>
    private const double TranslatedEnough = 0.7;
    /// <summary>Mods with fewer texts than this are not worth a translation.</summary>
    private const int MinTexts = 3;

    private readonly ModTextCache _textCache = new();
    private readonly Dictionary<ulong, IReadOnlyList<ulong>> _requiredItems = [];
    private readonly SemaphoreSlim _pageGate = new(1, 1);
    private DispatcherTimer? _coverageTimer;
    private CancellationTokenSource? _trCts;
    private IReadOnlyDictionary<string, TranslationCoverage> _coverage = new Dictionary<string, TranslationCoverage>();
    private string? _coverageLanguage;

    public ObservableCollection<TranslationRowViewModel> TrRows { get; } = [];
    public ObservableCollection<string> TrNotFound { get; } = [];

    [ObservableProperty] public partial int UntranslatedCount { get; set; }
    [ObservableProperty] public partial bool IsTrSearching { get; set; }
    [ObservableProperty] public partial string TrStatus { get; set; } = "";
    [ObservableProperty] public partial string TrSummary { get; set; } = "";
    [ObservableProperty] public partial bool TrSearched { get; set; }
    [ObservableProperty] public partial string TrApplyText { get; set; } = "";

    public bool HasUntranslated => UntranslatedCount > 0 && TrLanguageId is not null;
    public bool HasTrRows => TrRows.Count > 0;
    public bool HasTrNotFound => TrNotFound.Count > 0;
    public bool TrNothingFound => TrSearched && !IsTrSearching && TrRows.Count == 0;
    public bool CanApplyTranslations => !IsTrSearching && TrRows.Any(r => r.IsSelected);
    public string TrNotFoundTitle => Loc.Format("Trn.NotFound", TrNotFound.Count);
    public string UntranslatedText => Loc.Format("Trn.Untranslated", UntranslatedCount, TrLanguageName);
    public string TranslationsTabText => UntranslatedCount > 0 && TrLanguageId is not null
        ? Loc.Format("Ws.Tab.TranslationsCount", UntranslatedCount) : Loc.Get("Ws.Tab.Translations");

    partial void OnUntranslatedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUntranslated));
        OnPropertyChanged(nameof(UntranslatedText));
        OnPropertyChanged(nameof(TranslationsTabText));
    }

    partial void OnIsTrSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(TrNothingFound));
        OnPropertyChanged(nameof(CanApplyTranslations));
        FindTranslationsCommand.NotifyCanExecuteChanged();
    }

    // ───────────────────────── Language ─────────────────────────

    /// <summary>The interface language changed: names and counts follow.</summary>
    private void RefreshTranslationTexts()
    {
        OnPropertyChanged(nameof(TrLanguages));
        OnPropertyChanged(nameof(TrLanguage));
        OnPropertyChanged(nameof(TrGameNote));
        OnPropertyChanged(nameof(HasTrGameNote));
        OnPropertyChanged(nameof(UntranslatedText));
        OnPropertyChanged(nameof(TranslationsTabText));
        OnPropertyChanged(nameof(TrNotFoundTitle));
        UpdateTrApply();
        ScheduleCoverageRefresh(now: true);
    }

    /// <summary>The languages translations can be found for (English is the mods' own language).</summary>
    public IReadOnlyList<GameLanguage> TrLanguages => ModLanguages.GameLanguages(_paths.DataDir).Where(l => l.Id != ModLanguages.English).ToList();

    private string? GameLanguageId => ModLanguages.GameLanguageId(_paths.ConfigDir);

    /// <summary>
    /// The chosen language; by default the game's, or RimForge's own when the game is in English
    /// (a Russian player often keeps the game in English until the mods are translated).
    /// </summary>
    private string? TrLanguageId =>
        _settings.TranslationLanguage
        ?? (GameLanguageId is { } game && game != ModLanguages.English ? game
            : Loc.Current == "ru" ? "Russian" : null);

    public GameLanguage? TrLanguage
    {
        get => TrLanguages.FirstOrDefault(l => l.Id == TrLanguageId);
        set
        {
            if (value is null || value.Id == TrLanguageId) return;
            _settings.TranslationLanguage = value.Id;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrGameNote));
            OnPropertyChanged(nameof(HasTrGameNote));
            ClearTranslationResults();
            ScheduleCoverageRefresh(now: true);
        }
    }

    private string TrLanguageName => TrLanguage?.NativeName ?? TrLanguageId ?? "";

    /// <summary>The game shows another language than the one searched for: translations work once it is switched.</summary>
    public string TrGameNote => Loc.Format("Trn.GameNote", GameLanguageId ?? ModLanguages.English, TrLanguageName);
    public bool HasTrGameNote => TrLanguageId is not null && GameLanguageId is { } g && g != TrLanguageId;

    // ───────────────────────── How much is translated ─────────────────────────

    /// <summary>Counts again a moment after the list stops changing (scanning languages takes a fraction of a second).</summary>
    private void ScheduleCoverageRefresh(bool now = false)
    {
        _coverageTimer?.Stop();
        _coverageTimer = new DispatcherTimer(TimeSpan.FromSeconds(now ? 0.1 : 1.5), DispatcherPriority.Background, (_, _) =>
        {
            _coverageTimer?.Stop();
            _ = RefreshCoverageAsync();
        });
        _coverageTimer.Start();
    }

    private async Task RefreshCoverageAsync()
    {
        if (TrLanguageId is not { } language || _gameVersion is null)
        {
            UntranslatedCount = 0;
            return;
        }
        var active = ActiveMods();
        try
        {
            var coverage = await CoverageAsync(active, active, language);
            _coverage = coverage.ToDictionary(c => c.PackageId, StringComparer.OrdinalIgnoreCase);
            _coverageLanguage = language;
            UntranslatedCount = coverage.Count(NeedsTranslation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // counted again on the next change
        }
    }

    private static bool NeedsTranslation(TranslationCoverage c) => c.HasText && c.Texts >= MinTexts && c.Share < TranslatedEnough;

    private List<ModInfo> ActiveMods() =>
        Snapshot().Select(id => _items.GetValueOrDefault(id)).OfType<ModItemViewModel>()
            .Where(i => !i.IsMissing && i.Mod.Source != ModSource.Official)
            .Select(i => i.Mod)
            .ToList();

    private Task<IReadOnlyList<TranslationCoverage>> CoverageAsync(IReadOnlyList<ModInfo> check, IReadOnlyList<ModInfo> providers, string language)
    {
        var activeIds = Snapshot().ToHashSet();
        var version = _gameVersion ?? "1.6";
        return Task.Run(() => ModLanguages.Coverage(check, providers, language, version, activeIds, _textCache));
    }

    private DateTime ModUpdatedUtc(ModInfo mod) =>
        mod.WorkshopId is { } wid && _manifest.TryGetValue(wid, out var m) ? m.UpdatedUtc : Directory.GetLastWriteTimeUtc(mod.FolderPath);

    // ───────────────────────── Finding translations ─────────────────────────

    /// <summary>From the mods page and the traffic light: the Workshop's "Translations" section, searching at once.</summary>
    [RelayCommand]
    private void OpenTranslations()
    {
        ShowPage("workshop");
        WorkshopSection = "translations";
        if (!TrSearched && !IsTrSearching) _ = FindTranslations();
    }

    private bool CanFindTranslations() => !IsTrSearching;

    [RelayCommand(CanExecute = nameof(CanFindTranslations))]
    private async Task FindTranslations()
    {
        if (TrLanguageId is not { } language)
        {
            TrStatus = Loc.Get("Trn.PickLanguage");
            return;
        }
        _trCts?.Cancel();
        var cts = _trCts = new CancellationTokenSource();
        ClearTranslationResults();
        IsTrSearching = true;
        TrStatus = Loc.Get("Trn.Checking");
        try
        {
            var active = ActiveMods();
            var activeIds = active.Select(m => m.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var coverage = await CoverageAsync(active, active, language);
            _coverage = coverage.ToDictionary(c => c.PackageId, StringComparer.OrdinalIgnoreCase);
            _coverageLanguage = language;
            UntranslatedCount = coverage.Count(NeedsTranslation);
            var byId = active.ToDictionary(m => m.PackageId, StringComparer.OrdinalIgnoreCase);

            // 1. Translations already on disk but switched off.
            var untranslated = coverage.Where(NeedsTranslation).Select(c => byId[c.PackageId]).ToList();
            var withInstalled = await CoverageAsync(untranslated, _installed.Values.Where(m => m.Source != ModSource.Official).ToList(), language);
            var enableRows = withInstalled
                .Where(c => c.Share >= TranslatedEnough)
                .SelectMany(c => c.Providers.Where(p => !activeIds.Contains(p)).Take(1).Select(p => (Provider: p, Mod: c.PackageId)))
                .GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Installed: _installed.GetValueOrDefault(g.Key), Mods: g.Select(x => x.Mod).ToList()))
                .Where(x => x.Installed is not null)
                .ToList();
            var enabledFor = enableRows.SelectMany(r => r.Mods).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 2. What to look for: untranslated mods, and translated ones whose translation is long out of date.
            var needs = new List<TranslationNeed>();
            var local = new List<string>();
            foreach (var c in coverage)
            {
                var mod = byId[c.PackageId];
                if (enabledFor.Contains(c.PackageId)) continue;
                string? replace = null;
                if (!NeedsTranslation(c))
                {
                    if (!c.HasText || c.Share >= 0.95 || c.Providers.Count == 0 || !byId.TryGetValue(c.Providers[0], out var provider)) continue;
                    if (ModUpdatedUtc(provider) > ModUpdatedUtc(mod).AddDays(-90)) continue;
                    replace = provider.PackageId;
                }
                if (mod.WorkshopId is not { } wid)
                {
                    if (replace is null) local.Add(mod.Name);
                    continue;
                }
                needs.Add(new TranslationNeed(mod.PackageId, mod.Name, wid, ModUpdatedUtc(mod), c.Share, replace));
            }

            // 3. The Workshop.
            TranslationSearch search = new([], [], 0);
            if (needs.Count > 0)
            {
                var progress = new Progress<(int Done, int Total)>(p =>
                {
                    if (IsTrSearching) TrStatus = Loc.Format("Trn.Searching", p.Done, p.Total);
                });
                var context = new TranslationContext(language, _gameVersion ?? "1.6",
                    active.Where(m => m.WorkshopId is not null).Select(m => m.WorkshopId!.Value).ToHashSet());
                search = await TranslationFinder.FindAsync(needs, context, new AssistantSource(this), RequiredItemsAsync, progress, cts.Token);
            }
            if (cts.IsCancellationRequested) return;

            // 4. The list.
            foreach (var (installed, mods) in enableRows)
                AddTrRow(new TranslationRowViewModel(TranslationAction.Enable, installed!.Name, mods.Select(m => ForName(m, byId)).ToList(), true, UpdateTrApply)
                {
                    Installed = installed, Covers = mods,
                });
            var needById = needs.ToDictionary(n => n.PackageId, StringComparer.OrdinalIgnoreCase);
            foreach (var offer in search.Offers.OrderByDescending(o => o.Covers.Count).ThenBy(o => o.Item.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var replaces = offer.Covers.All(c => needById[c].Replace is not null) ? needById[offer.Covers[0]].Replace : null;
                var oldOne = replaces is null ? null : _installed.GetValueOrDefault(replaces);
                var row = new TranslationRowViewModel(replaces is null ? TranslationAction.Download : TranslationAction.Replace, offer.Item.Title,
                    offer.Covers.Select(c => ForName(c, byId)).ToList(),
                    selected: replaces is null && (offer.SupportsVersion || offer.Item.Versions.Count == 0), UpdateTrApply)
                {
                    Item = offer.Item, Covers = offer.Covers, SupportsVersion = offer.SupportsVersion, Verified = offer.Verified,
                    ReplacesId = replaces, ReplacesName = oldOne?.Name,
                };
                AddTrRow(row);
                _ = LoadTrPreviewAsync(row);
            }
            foreach (var name in search.NotFound.Where(n => n.Replace is null).Select(n => n.Name).Concat(local).Order(StringComparer.CurrentCultureIgnoreCase))
                TrNotFound.Add(name);

            var translated = coverage.Count(c => c.HasText && c.Texts >= MinTexts && c.Share >= TranslatedEnough);
            var noText = coverage.Count(c => !c.HasText || c.Texts < MinTexts);
            TrSummary = Loc.Format("Trn.Summary", active.Count, translated, UntranslatedCount, noText, TrLanguageName);
            TrStatus = search.FailedSearches > 0 ? Loc.Get("Trn.SomeFailed") : "";
            TrSearched = true;
        }
        catch (OperationCanceledException)
        {
            TrStatus = "";
        }
        catch (Exception ex)
        {
            TrStatus = Loc.Format("Trn.Failed", ProblemText(ex));
        }
        finally
        {
            if (_trCts == cts) IsTrSearching = false;
            OnPropertyChanged(nameof(HasTrRows));
            OnPropertyChanged(nameof(HasTrNotFound));
            OnPropertyChanged(nameof(TrNotFoundTitle));
            OnPropertyChanged(nameof(TrNothingFound));
            UpdateTrApply();
        }
    }

    /// <summary>"RimHUD" or "RimHUD (40%)" when a part is translated already.</summary>
    private string ForName(string packageId, IReadOnlyDictionary<string, ModInfo> byId)
    {
        var name = byId.TryGetValue(packageId, out var mod) ? mod.Name : NameOf(packageId);
        return _coverage.TryGetValue(packageId, out var c) && c.Share >= 0.01 ? $"{name} ({Percent(c.Share)})" : name;
    }

    private static string Percent(double share) => (int)Math.Round(share * 100) + "%";

    /// <summary>"Required items" from a translation's page, one page at a time and remembered for the session.</summary>
    private async Task<IReadOnlyList<ulong>> RequiredItemsAsync(ulong id, CancellationToken ct)
    {
        if (_requiredItems.TryGetValue(id, out var known)) return known;
        await _pageGate.WaitAsync(ct);
        try
        {
            if (_requiredItems.TryGetValue(id, out known)) return known;
            var list = await _web.GetRequiredItemsAsync(id, ct);
            _requiredItems[id] = list;
            await Task.Delay(700, ct); // Steam refuses quick bursts of page requests
            return list;
        }
        finally
        {
            _pageGate.Release();
        }
    }

    private void AddTrRow(TranslationRowViewModel row) => TrRows.Add(row);

    private async Task LoadTrPreviewAsync(TranslationRowViewModel row) =>
        row.Preview = await _previews.GetAsync(row.Item?.PreviewUrl, 160);

    private void ClearTranslationResults()
    {
        foreach (var row in TrRows) row.Preview?.Dispose();
        TrRows.Clear();
        TrNotFound.Clear();
        TrSummary = "";
        TrStatus = "";
        TrSearched = false;
        OnPropertyChanged(nameof(HasTrRows));
        OnPropertyChanged(nameof(HasTrNotFound));
        OnPropertyChanged(nameof(TrNothingFound));
        UpdateTrApply();
    }

    private void UpdateTrApply()
    {
        var count = TrRows.Count(r => r.IsSelected);
        TrApplyText = Loc.Format("Trn.Apply", count);
        OnPropertyChanged(nameof(CanApplyTranslations));
    }

    [RelayCommand]
    private void TrSelect(string which)
    {
        foreach (var row in TrRows) row.IsSelected = which == "all";
    }

    [RelayCommand]
    private void OpenTrPage(TranslationRowViewModel row)
    {
        if (row.Item is not null) OpenUrl(row.Item.Url);
        else if (row.Installed?.WorkshopId is { } wid) OpenUrl(WorkshopIds.PageUrl(wid));
    }

    // ───────────────────────── Putting them in ─────────────────────────

    [RelayCommand]
    private async Task ApplyTranslations()
    {
        var chosen = TrRows.Where(r => r.IsSelected).ToList();
        if (chosen.Count == 0) return;

        // Already on disk: on at once, each right after its mods (the careful sort puts it there).
        var enable = chosen.Where(r => r.Action == TranslationAction.Enable && r.Installed is not null)
            .Select(r => _items.GetValueOrDefault(r.Installed!.PackageId)).OfType<ModItemViewModel>().Where(i => !i.IsActive).ToList();
        if (enable.Count > 0)
        {
            Activate(enable);
            SortCarefully();
        }

        var downloads = chosen.Where(r => r.Item is not null)
            .Select(r => new DownloadViewModel(r.Item!.Id, r.Item.Title)
            {
                EnableWhenDone = r.Action == TranslationAction.Download,
                ReplacesId = r.Action == TranslationAction.Replace ? r.ReplacesId : null,
                TranslationFor = r.Covers,
            })
            .ToList();
        foreach (var row in chosen) TrRows.Remove(row);
        OnPropertyChanged(nameof(HasTrRows));
        UpdateTrApply();

        if (downloads.Count > 0) await StartDownloadsAsync(downloads);
        else if (enable.Count > 0) await ReportTranslationsAsync(chosen.SelectMany(r => r.Covers).ToList());
        ScheduleCoverageRefresh();
    }

    /// <summary>After translations arrived: how much of each mod is translated now.</summary>
    private async Task ReportTranslationsAsync(IReadOnlyList<string> mods)
    {
        if (TrLanguageId is not { } language || mods.Count == 0) return;
        var active = ActiveMods();
        var check = active.Where(m => mods.Contains(m.PackageId, StringComparer.OrdinalIgnoreCase)).ToList();
        if (check.Count == 0) return;
        var coverage = await CoverageAsync(check, active, language);
        var share = coverage.Where(c => c.HasText).Select(c => c.Share).DefaultIfEmpty(1).Average();
        var weak = coverage.Where(c => c.HasText && c.Share < TranslatedEnough).Select(c => NameOf(c.PackageId) + $" ({Percent(c.Share)})").ToList();
        ShowToast(Loc.Format("Trn.Done", check.Count, Percent(share))
                  + (weak.Count > 0 ? " " + Loc.Format("Trn.DoneWeak", string.Join(", ", weak.Take(4))) : ""));
    }

    /// <summary>
    /// Once downloads are done: translations get their report; new mods without a translation get a suggestion to find one.
    /// </summary>
    private async Task AfterDownloadsTranslationsAsync(IReadOnlyList<DownloadViewModel> done)
    {
        ScheduleCoverageRefresh();
        var translated = done.Where(d => d.TranslationFor is { Count: > 0 }).SelectMany(d => d.TranslationFor!).Distinct().ToList();
        if (translated.Count > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(4)); // after the "downloaded" message
            await ReportTranslationsAsync(translated);
            return;
        }
        if (TrLanguageId is not { } language) return;
        var fresh = done.Where(d => !d.IsUpdate && d.PackageId is not null).Select(d => d.PackageId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = ActiveMods();
        var check = active.Where(m => fresh.Contains(m.PackageId)).ToList();
        if (check.Count == 0) return;
        var coverage = await CoverageAsync(check, active, language);
        var missing = coverage.Count(NeedsTranslation);
        if (missing == 0) return;
        await Task.Delay(TimeSpan.FromSeconds(4));
        ShowToast(Loc.Format("Trn.NewMods", missing, TrLanguageName), Loc.Get("Trn.FindShort"), OpenTranslations, seconds: 12);
    }
}
