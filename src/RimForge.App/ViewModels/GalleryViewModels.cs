using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

/// <summary>One picture (or a YouTube video) of a Workshop page.</summary>
public sealed partial class GalleryItemViewModel(string url, string? videoId) : ObservableObject
{
    public string Url { get; } = url;
    public string? VideoId { get; } = videoId;
    public bool IsVideo => VideoId is not null;
    public string VideoUrl => $"https://www.youtube.com/watch?v={VideoId}";

    [ObservableProperty] public partial Bitmap? Thumb { get; set; }
    [ObservableProperty] public partial bool IsCurrent { get; set; }
}

/// <summary>
/// The pictures of a Workshop page, Steam-style: a big one with ‹ › and a strip of thumbnails; a click opens it full
/// screen. The main picture comes first, then the page's screenshots and videos (read from the page once per item).
/// </summary>
public sealed partial class GalleryViewModel(PreviewCache previews, Func<ulong, CancellationToken, Task<WorkshopMedia>> media) : ObservableObject
{
    public ObservableCollection<GalleryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Counter), nameof(CurrentIsVideo))]
    public partial int Index { get; set; } = -1;

    [ObservableProperty] public partial Bitmap? Current { get; set; }

    /// <summary>The big version for the full-screen view (loaded only there).</summary>
    [ObservableProperty] public partial Bitmap? Full { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; set; }

    public bool HasMany => Items.Count > 1;
    public bool HasItems => Items.Count > 0;
    public string Counter => Items.Count > 1 ? $"{Index + 1} / {Items.Count}" : "";
    public bool CurrentIsVideo => Index >= 0 && Index < Items.Count && Items[Index].IsVideo;

    /// <summary>Set while the full-screen view is open: the big picture follows the current one.</summary>
    public bool IsFullScreen { get; set; }

    private CancellationTokenSource _cts = new();
    private ulong _id;

    /// <summary>Shows <paramref name="id"/>'s pictures; <paramref name="mainPicture"/> (already loaded) appears at once.</summary>
    public async Task LoadAsync(ulong id, string? previewUrl, Bitmap? mainPicture)
    {
        if (id == _id && Items.Count > 0) return;
        Clear();
        _id = id;
        var ct = _cts.Token;
        // An installed mod's own picture has no address: it is shown from the file RimForge already read.
        if (previewUrl is not null || mainPicture is not null) Items.Add(new GalleryItemViewModel(previewUrl ?? "", null) { Thumb = mainPicture });
        Current = mainPicture;
        Index = Items.Count > 0 ? 0 : -1;
        Mark();
        RaiseCounts();

        IsLoading = true;
        try
        {
            var found = await media(id, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var url in found.Images.Where(u => !SamePicture(u, previewUrl)))
                Items.Add(new GalleryItemViewModel(url, null));
            foreach (var video in found.Videos)
                Items.Add(new GalleryItemViewModel($"https://img.youtube.com/vi/{video}/hqdefault.jpg", video));
            RaiseCounts();
            if (Items.Count > 0 && (Index < 0 || Current is null)) await Show(Math.Max(Index, 0));
            foreach (var item in Items.Where(i => i.Thumb is null).ToList())
            {
                var thumb = await previews.GetAsync(item.Url, 160, ct);
                if (ct.IsCancellationRequested) { thumb?.Dispose(); return; }
                item.Thumb = thumb;
            }
        }
        catch (Exception)
        {
            // no page, no connection: the main picture is all there is
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }
    }

    public void Clear()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _id = 0;
        foreach (var item in Items)
            if (!ReferenceEquals(item.Thumb, Current)) item.Thumb?.Dispose();
        Items.Clear();
        Index = -1;
        Current = null;
        Full = null;
        IsLoading = false;
        RaiseCounts();
    }

    [RelayCommand]
    private Task Prev() => Items.Count > 1 ? Show((Index - 1 + Items.Count) % Items.Count) : Task.CompletedTask;

    [RelayCommand]
    private Task Next() => Items.Count > 1 ? Show((Index + 1) % Items.Count) : Task.CompletedTask;

    [RelayCommand]
    private Task Pick(GalleryItemViewModel item) => Show(Items.IndexOf(item));

    public async Task Show(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        Index = index;
        Mark();
        var item = Items[index];
        var ct = _cts.Token;
        var picture = await previews.GetAsync(item.Url, 640, ct);
        if (ct.IsCancellationRequested || Index != index) return;
        Current = picture ?? item.Thumb;
        if (IsFullScreen) await LoadFullAsync();
    }

    /// <summary>The current picture at screen size.</summary>
    public async Task LoadFullAsync()
    {
        if (Index < 0 || Index >= Items.Count) return;
        var index = Index;
        Full = Current;
        var big = await previews.GetAsync(Items[index].Url, 1600, _cts.Token);
        if (Index == index && big is not null) Full = big;
    }

    private void Mark()
    {
        for (var i = 0; i < Items.Count; i++) Items[i].IsCurrent = i == Index;
        OnPropertyChanged(nameof(CurrentIsVideo));
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasMany));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Counter));
    }

    /// <summary>The main picture is often also the first screenshot.</summary>
    private static bool SamePicture(string a, string? b) =>
        b is not null && string.Equals(a.Split('?')[0].TrimEnd('/'), b.Split('?')[0].TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
