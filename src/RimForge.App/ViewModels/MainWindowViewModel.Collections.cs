using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

/// <summary>A theme button of the "Collections" tab: what is typed into the Workshop search for it.</summary>
public sealed partial class CollThemeViewModel(string id, string? query) : ObservableObject
{
    public string Id { get; } = id;
    public string? Query { get; } = query;
    public string Name => Loc.Get("CollTheme." + Id);
    [ObservableProperty] public partial bool IsCurrent { get; set; }
    public void Refresh() => OnPropertyChanged(nameof(Name));
}

/// <summary>One mod of an opened collection: ticked ones go into the new modpack.</summary>
public sealed partial class CollectionModRow(WorkshopItem item, bool installed, Action changed) : ObservableObject
{
    public WorkshopItem Item { get; } = item;
    public string Title => Item.Title;
    public bool IsInstalled { get; } = installed;
    public bool IsRemoved => Item.Removed && !IsInstalled;
    public bool CanTick => !IsRemoved;
    public string StatusText => IsInstalled ? Loc.Get("CollC.Have") : IsRemoved ? Loc.Get("CollC.Removed") : Humanize.Size(Item.SizeBytes);
    public string Description => _description ??= Shorten(BbCode.ToPlainText(Item.Description));
    private string? _description;

    [ObservableProperty] public partial bool IsChecked { get; set; } = !item.Removed || installed;
    [ObservableProperty] public partial Bitmap? Thumb { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();

    private static string Shorten(string text) => text.Length > 600 ? text[..600].TrimEnd() + "…" : text;
}

/// <summary>
/// Steam collections inside the Workshop page: browse them by theme, words, popularity, size and game version; open
/// one to read it, see its pictures and mods; tick the mods and get them as a new modpack. Also the picture galleries
/// of Workshop cards and of installed mods.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public bool IsWsCollections => WorkshopSection == "collections";

    public ObservableCollection<CollThemeViewModel> CollThemes { get; } =
    [
        new("all", null), new("vanilla", "vanilla"), new("medieval", "medieval"), new("modern", "modern"),
        new("realism", "realistic"), new("fantasy", "fantasy"), new("scifi", "sci-fi"), new("horror", "horror"),
        new("tribal", "tribal"), new("hardcore", "hardcore"), new("performance", "performance"),
    ];

    [ObservableProperty] public partial string CollText { get; set; } = "";
    [ObservableProperty] public partial int CollSortIndex { get; set; }
    [ObservableProperty] public partial int CollSizeIndex { get; set; }

    public IReadOnlyList<string> CollSortOptions =>
        [Loc.Get("CollSort.Week"), Loc.Get("CollSort.AllTime"), Loc.Get("CollSort.Newest"), Loc.Get("CollSort.Updated")];
    public IReadOnlyList<string> CollSizeOptions =>
        [Loc.Get("CollSize.Any"), Loc.Get("CollSize.Small"), Loc.Get("CollSize.Medium"), Loc.Get("CollSize.Large")];

    private bool _collOpened;

    partial void OnCollSortIndexChanged(int value)
    {
        if (_collOpened && value >= 0) _ = CollSearch();
    }

    partial void OnCollSizeIndexChanged(int value)
    {
        if (_collOpened && value >= 0) _ = CollSearch();
    }

    /// <summary>Opens the tab; the first time it shows this week's popular collections.</summary>
    [RelayCommand]
    private Task ShowCollections()
    {
        WorkshopSection = "collections";
        SelectedCard = null;
        if (_collOpened && _lastQuery is { Collections: true }) return Task.CompletedTask;
        _collOpened = true;
        return CollSearch();
    }

    [RelayCommand]
    private Task PickCollTheme(CollThemeViewModel theme)
    {
        foreach (var t in CollThemes) t.IsCurrent = t == theme && theme.Query is not null;
        return CollSearch();
    }

    [RelayCommand]
    private Task CollSearch()
    {
        WorkshopSection = "collections";
        _collOpened = true;
        var theme = CollThemes.FirstOrDefault(t => t.IsCurrent)?.Query;
        var text = string.Join(' ', new[] { theme, CollText.Trim() }.Where(s => !string.IsNullOrEmpty(s)));
        var sort = CollSortIndex switch
        {
            2 => WorkshopSort.Newest,
            3 => WorkshopSort.Updated,
            _ => WorkshopSort.Trend,
        };
        var query = new WorkshopQuery(text.Length == 0 ? null : text, sort, OnlyMyVersion ? _gameVersion : null,
            Collections: true, AllTime: CollSortIndex == 1);
        var header = Loc.Get(CollSortIndex switch { 1 => "CollHdr.AllTime", 2 => "CollHdr.Newest", 3 => "CollHdr.Updated", _ => "CollHdr.Week" })
                     + (text.Length > 0 ? " · «" + text + "»" : "");
        return RunSearchAsync(query, append: false, header: header);
    }

    /// <summary>The size filter of the tab (how many mods a collection holds).</summary>
    private bool PassesCollectionSize(WorkshopItem item) => CollSizeIndex switch
    {
        1 => item.ChildCount is >= 0 and <= 50,
        2 => item.ChildCount is > 50 and <= 200,
        3 => item.ChildCount > 200,
        _ => true,
    };

    private void RefreshCollectionTexts()
    {
        foreach (var t in CollThemes) t.Refresh();
        OnPropertyChanged(nameof(CollSortOptions));
        OnPropertyChanged(nameof(CollSizeOptions));
    }

    // ───────────────────────── An opened collection ─────────────────────────

    public ObservableCollection<CollectionModRow> CollectionMods { get; } = [];
    [ObservableProperty] public partial bool IsCollectionLoading { get; set; }
    [ObservableProperty] public partial string CollectionSummary { get; set; } = "";
    [ObservableProperty] public partial string CollectionCreateText { get; set; } = "";
    [ObservableProperty] public partial bool CanCreateFromCollection { get; set; }

    public bool SelectedIsCollection => SelectedCard?.Item.IsCollection == true;
    public bool SelectedIsMod => SelectedCard is { Item.IsCollection: false };

    private CancellationTokenSource _collCts = new();
    private string _collectionTitle = "";

    private async Task LoadCollectionAsync(WorkshopCardViewModel card)
    {
        _collCts.Cancel();
        _collCts = new CancellationTokenSource();
        var ct = _collCts.Token;
        foreach (var row in CollectionMods) row.Thumb?.Dispose();
        CollectionMods.Clear();
        CanCreateFromCollection = false;
        CollectionSummary = Loc.Get("CollC.Loading");
        IsCollectionLoading = true;
        try
        {
            var (title, ids) = await _web.GetCollectionAsync(card.Id, ct);
            var details = (await _web.GetDetailsAsync(ids, ct)).ToDictionary(d => d.Id);
            if (ct.IsCancellationRequested) return;
            _collectionTitle = title;
            foreach (var id in ids)
            {
                var item = details.GetValueOrDefault(id) ?? new WorkshopItem { Id = id, Title = id.ToString(), Removed = true };
                CollectionMods.Add(new CollectionModRow(item, _byWorkshopId.ContainsKey(id), UpdateCollectionSummary));
            }
            UpdateCollectionSummary();
            foreach (var row in CollectionMods.ToList())
            {
                if (row.Item.PreviewUrl is null) continue;
                var thumb = await _previews.GetAsync(row.Item.PreviewUrl, 128, ct);
                if (ct.IsCancellationRequested) { thumb?.Dispose(); return; }
                row.Thumb = thumb;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CollectionSummary = Loc.Format("Coll.Failed", ProblemText(ex));
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsCollectionLoading = false;
        }
    }

    private void UpdateCollectionSummary()
    {
        var picked = CollectionMods.Where(r => r.IsChecked && r.CanTick).ToList();
        var toDownload = picked.Where(r => !r.IsInstalled).ToList();
        var removed = CollectionMods.Count(r => r.IsRemoved);
        CollectionSummary = Loc.Format("CollC.Summary", CollectionMods.Count, CollectionMods.Count(r => r.IsInstalled), toDownload.Count,
                                Humanize.Size(toDownload.Sum(r => r.Item.SizeBytes)) is { Length: > 0 } s ? s : "0")
                            + (removed > 0 ? "\n" + Loc.Format("CollC.RemovedCount", removed) : "");
        CanCreateFromCollection = picked.Count > 0;
        CollectionCreateText = Loc.Format(toDownload.Count > 0 ? "CollC.CreateDownload" : "CollC.Create", picked.Count);
    }

    [RelayCommand]
    private void CollectionTickAll(string all)
    {
        foreach (var row in CollectionMods.Where(r => r.CanTick)) row.IsChecked = all == "all";
    }

    [RelayCommand]
    private void ToggleCollectionMod(CollectionModRow row) => row.IsExpanded = !row.IsExpanded;

    [RelayCommand]
    private void OpenCollectionModPage(CollectionModRow row) => OpenUrl(WorkshopIds.PageUrl(row.Item.Id));

    /// <summary>A new, separate modpack from the ticked mods; the missing ones are downloaded into it.</summary>
    [RelayCommand]
    private async Task CreateFromCollection()
    {
        if (SelectedCard is not { Item.IsCollection: true } card) return;
        var items = CollectionMods.Where(r => r.IsChecked && r.CanTick).Select(r => r.Item).ToList();
        if (items.Count == 0 || !await ConfirmLeaveAsync()) return;
        var name = _collectionTitle.Length > 0 ? _collectionTitle : card.Title;
        SelectedCard = null;
        await CreateWorkshopModpackAsync(name, WorkshopIds.PageUrl(card.Id), items);
    }

    // ───────────────────────── Picture galleries ─────────────────────────

    private GalleryViewModel? _cardGallery, _modGallery;
    public GalleryViewModel CardGallery => _cardGallery ??= new GalleryViewModel(_previews, MediaOfAsync);
    public GalleryViewModel ModGallery => _modGallery ??= new GalleryViewModel(_previews, MediaOfAsync);

    /// <summary>The gallery shown full screen, if any.</summary>
    [ObservableProperty] public partial GalleryViewModel? FullGallery { get; set; }

    private readonly Dictionary<ulong, Task<WorkshopMedia>> _media = [];

    /// <summary>A page's screenshots, read once per item (a failure is forgotten, so it is tried again later).</summary>
    private Task<WorkshopMedia> MediaOfAsync(ulong id, CancellationToken ct)
    {
        if (_media.TryGetValue(id, out var known) && !known.IsFaulted && !known.IsCanceled) return known;
        return _media[id] = _web.GetMediaAsync(id, CancellationToken.None);
    }

    /// <summary>Called when a Workshop card opens: its pictures, and for a collection its mods.</summary>
    private void OnCardOpened(WorkshopCardViewModel? card)
    {
        OnPropertyChanged(nameof(SelectedIsCollection));
        OnPropertyChanged(nameof(SelectedIsMod));
        if (card is null)
        {
            CardGallery.Clear();
            _collCts.Cancel();
            return;
        }
        _ = CardGallery.LoadAsync(card.Id, card.Item.PreviewUrl, card.Preview);
        if (card.Item.IsCollection) _ = LoadCollectionAsync(card);
    }

    private DispatcherTimer? _modGalleryTimer;

    /// <summary>An installed mod from the Workshop: its page's pictures, a moment after it is picked (clicking through the list stays quick).</summary>
    private void OnModPickedForGallery(ModItemViewModel? mod)
    {
        _modGalleryTimer?.Stop();
        ModGallery.Clear();
        if (mod?.Mod.WorkshopId is not { } id) return;
        _modGalleryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _modGalleryTimer?.Stop();
            if (SelectedMod == mod) _ = ModGallery.LoadAsync(id, null, SelectedPreview);
        });
        _modGalleryTimer.Start();
    }

    [RelayCommand]
    private async Task OpenGalleryFull(GalleryViewModel gallery)
    {
        if (!gallery.HasItems) return;
        if (gallery.CurrentIsVideo)
        {
            OpenUrl(gallery.Items[gallery.Index].VideoUrl);
            return;
        }
        gallery.IsFullScreen = true;
        FullGallery = gallery;
        await gallery.LoadFullAsync();
    }

    [RelayCommand]
    private void CloseGalleryFull()
    {
        if (FullGallery is { } g) g.IsFullScreen = false;
        FullGallery = null;
    }

    [RelayCommand]
    private void OpenGalleryVideo(GalleryViewModel gallery)
    {
        if (gallery.CurrentIsVideo) OpenUrl(gallery.Items[gallery.Index].VideoUrl);
    }
}
