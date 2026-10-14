using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RimForge.App.Services;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

internal static class Humanize
{
    /// <summary>"сегодня", "3 дня назад", "2 мес. назад".</summary>
    public static string Ago(DateTime utc)
    {
        if (utc.Year < 2000) return "?";
        var days = (DateTime.UtcNow - utc).TotalDays;
        return days switch
        {
            < 1 => Loc.Get("Ago.Today"),
            < 2 => Loc.Get("Ago.Yesterday"),
            < 31 => Loc.Format("Ago.Days", (int)days),
            < 365 => Loc.Format("Ago.Months", (int)(days / 30.4)),
            _ => Loc.Format("Ago.Years", (int)(days / 365.25)),
        };
    }

    /// <summary>1 925 374 → "1,9 млн" / "1.9M"; 12 345 → "12 тыс." / "12K".</summary>
    public static string Count(long n)
    {
        var culture = CultureInfo.GetCultureInfo(Loc.Current == "ru" ? "ru-RU" : "en-US");
        return n switch
        {
            >= 1_000_000 => Loc.Format("Num.Millions", (n / 1_000_000.0).ToString(n >= 10_000_000 ? "0" : "0.#", culture)),
            >= 10_000 => Loc.Format("Num.Thousands", (n / 1000).ToString(culture)),
            _ => n.ToString("N0", culture),
        };
    }

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "",
        >= 1L << 30 => Loc.Format("Size.GB", (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture)),
        >= 1L << 20 => Loc.Format("Size.MB", (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture)),
        _ => Loc.Format("Size.KB", Math.Max(1, bytes / 1024)),
    };
}

/// <summary>Where a Workshop mod stands for this player.</summary>
public enum CardState { NotInstalled, Queued, Downloading, Installed, Active, Unsubscribed }

/// <summary>One mod in Workshop search results (card) and in the details panel.</summary>
public sealed partial class WorkshopCardViewModel(WorkshopItem item, string? gameVersion, IReadOnlyList<string> missingDlcs) : ObservableObject
{
    public WorkshopItem Item { get; } = item;
    public ulong Id => Item.Id;
    public string Title => Item.Title;
    public string AuthorText => Item.AuthorName is { Length: > 0 } a ? Loc.Format("Ws.By", a) : "";
    public bool HasAuthor => AuthorText.Length > 0;
    public string SubscribersText => Loc.Format("Ws.Subscribers", Humanize.Count(Item.Subscribers));

    // ── A collection instead of a mod ──
    public bool IsCollection => Item.IsCollection;
    public bool IsMod => !Item.IsCollection;
    public string ChildCountText => Item.ChildCount >= 0 ? Loc.Format("CollC.Mods", Item.ChildCount) : "";
    public string FavoritesShort => Humanize.Count(Item.Favorites);
    public string FavoritesText => Loc.Format("CollC.Favorites", Humanize.Count(Item.Favorites), Humanize.Count(Item.Views));
    public bool CanSubscribeMod => CanSubscribe && IsMod;
    public string UpdatedText => Loc.Format("Ws.Updated", Humanize.Ago(Item.UpdatedUtc));
    public string SubscribersShort => Humanize.Count(Item.Subscribers);
    public string UpdatedShort => Humanize.Ago(Item.UpdatedUtc);
    public string VersionsText => Item.Versions.Count switch
    {
        0 => Loc.Get("Ws.NoVersions"),
        > 3 => "… " + string.Join(" · ", Item.Versions.TakeLast(3)),
        _ => string.Join(" · ", Item.Versions),
    };
    public bool SupportsMyVersion => gameVersion is null || Item.Versions.Count == 0 || Item.Versions.Contains(gameVersion);
    public string VersionWarning => Loc.Format("Ws.WrongVersion", gameVersion);
    public string SizeText => Humanize.Size(Item.SizeBytes);
    public string RatingText => Item.VotesUp + Item.VotesDown > 0
        ? Loc.Format("Ws.Rating", (int)Math.Round(100.0 * Item.VotesUp / (Item.VotesUp + Item.VotesDown)))
        : "";
    public bool HasRating => RatingText.Length > 0;

    private int Rating => Item.VotesUp + Item.VotesDown > 0 ? (int)Math.Round(100.0 * Item.VotesUp / (Item.VotesUp + Item.VotesDown)) : -1;
    public string RatingShort => Rating >= 0 ? Rating + "%" : "";
    public bool IsRatingGood => Rating >= 85;
    public bool IsRatingMid => Rating is >= 65 and < 85;
    public bool IsRatingBad => Rating is >= 0 and < 65;

    /// <summary>Updated within two months.</summary>
    public bool IsFresh => Item.UpdatedUtc > DateTime.UtcNow.AddDays(-60);
    /// <summary>No update for over a year: may be abandoned.</summary>
    public bool IsStale => Item.UpdatedUtc < DateTime.UtcNow.AddDays(-365);
    public bool IsUpdatedNormal => !IsFresh && !IsStale;

    private IReadOnlyList<string>? _needs;
    private IReadOnlyList<string> Needs => _needs ??= Item.GuessRequiredDlcs().Where(missingDlcs.Contains).ToList();
    /// <summary>"Needs Biotech?" — only for expansions this player does not have.</summary>
    public bool NeedsDlc => Needs.Count > 0;
    public string NeedsDlcText => Loc.Format("Ws.NeedsDlc", string.Join(", ", Needs));

    private string? _description;
    public string Description => _description ??= BbCode.ToPlainText(Item.Description);

    // ── Translation (the description only: the mod's name stays as the author wrote it) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayDescription), nameof(IsTranslated), nameof(CanTranslate), nameof(TranslationText))]
    public partial string? TranslatedDescription { get; set; }

    /// <summary>The description's language: guessed offline at first, then the one the translator detected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslated), nameof(IsForeign), nameof(TranslationBadge), nameof(TranslationText))]
    public partial string? SourceLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayDescription), nameof(TranslationText), nameof(TranslationToggleText))]
    public partial bool ShowTranslated { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTranslate))]
    public partial bool IsTranslating { get; set; }

    private string _target = "ru";

    public string DisplayDescription => ShowTranslated && TranslatedDescription is { } d ? d : Description;
    /// <summary>The description is in another language than the player's — the card shows "EN → RU".</summary>
    public bool IsForeign => SourceLanguage is { } s && !s.StartsWith(_target);
    public bool IsTranslated => IsForeign && TranslatedDescription is not null;
    public bool CanTranslate => !IsTranslated && !IsTranslating;
    public string TranslationBadge => SourceLanguage is { } s
        ? $"{RimForge.Core.Translation.LanguageGuess.ShortCode(s)} → {_target.ToUpperInvariant()}" : "";
    public string TranslationText => SourceLanguage is not { } s ? ""
        : Loc.Format(TranslatedDescription is null ? "Tr.WillTranslate" : ShowTranslated ? "Tr.TranslatedFrom" : "Tr.ShowingOriginal",
            RimForge.Core.Translation.LanguageGuess.DisplayName(s, Loc.Current));
    public string TranslationToggleText => Loc.Get(ShowTranslated ? "Tr.ShowOriginal" : "Tr.ShowTranslation");

    /// <summary>Offline guess of the description's language, for the badge before anything is translated.</summary>
    public void GuessLanguage(string target)
    {
        _target = target;
        if (SourceLanguage is null && Description.Any(char.IsLetter))
            SourceLanguage = RimForge.Core.Translation.LanguageGuess.Guess(Description);
    }

    public void SetDescription(RimForge.Core.Translation.Translated t, string target)
    {
        _target = target;
        SourceLanguage = t.SourceLanguage;
        if (t.SourceLanguage.StartsWith(target)) return; // already in the player's language
        TranslatedDescription = t.Text;
    }

    public void ClearTranslation()
    {
        TranslatedDescription = null;
        SourceLanguage = null;
        ShowTranslated = true;
    }

    [ObservableProperty] public partial Bitmap? Preview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubscribe), nameof(IsBusy), nameof(CanEnable), nameof(IsActive), nameof(IsInstalled),
        nameof(StateText), nameof(CanUnsubscribe), nameof(CanSubscribeMod))]
    public partial CardState State { get; set; }

    [ObservableProperty] public partial double Progress { get; set; }

    /// <summary>Package id once the mod is on disk.</summary>
    public string? PackageId { get; set; }

    public bool CanSubscribe => State is CardState.NotInstalled or CardState.Unsubscribed;
    public bool IsBusy => State is CardState.Queued or CardState.Downloading;
    public bool CanEnable => State == CardState.Installed;
    public bool IsActive => State == CardState.Active;
    public bool IsInstalled => State is CardState.Installed or CardState.Active;
    public bool CanUnsubscribe => IsInstalled;

    public string StateText => State switch
    {
        CardState.Queued => Loc.Get("Ws.State.Queued"),
        CardState.Downloading => Loc.Format("Ws.State.Downloading", (int)(Progress * 100)),
        CardState.Installed => Loc.Get("Ws.State.Installed"),
        CardState.Active => Loc.Get("Ws.State.Active"),
        CardState.Unsubscribed => Loc.Get("Ws.State.Unsubscribed"),
        _ => "",
    };

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StateText));
}

/// <summary>A tile on the "Selections" tab.</summary>
public sealed partial class ThemeViewModel(WorkshopTheme theme) : ObservableObject
{
    public WorkshopTheme Theme { get; } = theme;
    public string Glyph => Theme.Glyph;
    public string Name => Loc.Get("Theme." + Theme.Id);
    public string Tip => Loc.Get("Theme." + Theme.Id + ".Tip");

    [ObservableProperty] public partial bool IsCurrent { get; set; }

    /// <summary>Picked as the category in the search filters.</summary>
    [ObservableProperty] public partial bool IsSearchCategory { get; set; }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Tip));
    }
}

/// <summary>A row on the "Disk cleanup" tab.</summary>
public sealed partial class CleanupRowViewModel(ulong id, string packageId, string name, long sizeBytes, DateTime? updatedUtc, Action changed)
    : ObservableObject
{
    public ulong Id { get; } = id;
    public string PackageId { get; } = packageId;
    public string Name { get; } = name;
    public long SizeBytes { get; } = sizeBytes;
    public string SizeText => Humanize.Size(SizeBytes);
    public string UpdatedText => updatedUtc is { } u ? Loc.Format("Ws.Updated", Humanize.Ago(u)) : "";

    [ObservableProperty] public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

public enum DownloadState { Queued, Subscribing, Downloading, Done, Failed }

/// <summary>A row in the download queue.</summary>
public sealed partial class DownloadViewModel(ulong id, string title) : ObservableObject
{
    public ulong Id { get; } = id;
    public string Title { get; } = title;

    /// <summary>The mod this one was downloaded for (it is listed as a requirement there).</summary>
    public string? NeededBy { get; init; }
    public bool HasNeededBy => NeededBy is not null;
    public string NeededByText => NeededBy is null ? "" : Loc.Format("Dl.NeededBy", NeededBy);

    /// <summary>Turn the mod on in the current modpack once it is downloaded.</summary>
    public bool EnableWhenDone { get; init; }

    /// <summary>An update of a mod already on disk rather than a new subscription.</summary>
    public bool IsUpdate { get; init; }

    /// <summary>Package id of an abandoned mod this download replaces in the list.</summary>
    public string? ReplacesId { get; init; }

    /// <summary>A translation: the package ids of the mods it translates (for the report once it is on).</summary>
    public IReadOnlyList<string>? TranslationFor { get; init; }

    /// <summary>Workshop id of the old subscription to drop once this one is on disk (same package id as the new mod).</summary>
    public ulong? UnsubscribeAfter { get; init; }

    /// <summary>
    /// Picked up again after RimForge restarted (a collection's mods Steam may still be downloading): it is only
    /// watched, not subscribed again, and quietly dropped if Steam doesn't have it subscribed.
    /// </summary>
    public bool IsResumed { get; init; }

    public DateTime LastProgressUtc { get; set; } = DateTime.UtcNow;
    /// <summary>When the current problem began (no Steam, no progress); null while all is well.</summary>
    public DateTime? WaitingSinceUtc { get; set; }
    /// <summary>A subscription that failed for now is tried again at this time.</summary>
    public DateTime NextTryUtc { get; set; }
    public string? Folder { get; set; }
    public string? PackageId { get; set; }
    public bool Handled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsDone), nameof(IsFailed), nameof(IsRunning), nameof(IsWaiting))]
    public partial DownloadState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; set; }

    /// <summary>Why the download stands still (no connection, Steam closed…); it goes on by itself once that passes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsWaiting))]
    public partial string? WaitReason { get; set; }

    public bool IsWaiting => WaitReason is not null && IsRunning;

    /// <summary>Extra note, e.g. "also needs X, which is not in the Workshop".</summary>
    [ObservableProperty] public partial string? Note { get; set; }

    public bool IsDone => State == DownloadState.Done;
    public bool IsFailed => State == DownloadState.Failed;
    public bool IsRunning => State is DownloadState.Queued or DownloadState.Subscribing or DownloadState.Downloading;

    public string StatusText => IsWaiting ? WaitReason! : State switch
    {
        DownloadState.Queued => Loc.Get("Dl.Queued"),
        DownloadState.Subscribing => Loc.Get("Dl.Subscribing"),
        DownloadState.Downloading => Progress > 0 ? Loc.Format("Dl.Downloading", (int)(Progress * 100)) : Loc.Get("Dl.Waiting"),
        DownloadState.Done => Loc.Get(IsUpdate ? "Dl.Updated" : "Dl.Done"),
        _ => Loc.Format("Dl.Failed", Error ?? "?"),
    };
}

public enum UpdateKind { Recent, Pending, Removed }

/// <summary>A row on the "Updates" tab.</summary>
public sealed partial class UpdateRowViewModel : ObservableObject
{
    public required ulong WorkshopId { get; init; }
    public required string PackageId { get; init; }
    public required string Name { get; init; }
    public required UpdateKind Kind { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public bool InModpack { get; init; }
    public string? Folder { get; init; }
    public bool IsRemoved => Kind == UpdateKind.Removed;

    public string WhenText => Kind switch
    {
        UpdateKind.Recent => Loc.Format("Upd.UpdatedAgo", Humanize.Ago(UpdatedUtc)),
        UpdateKind.Pending => Loc.Format("Upd.NewVersionAgo", Humanize.Ago(UpdatedUtc)),
        _ => Loc.Get("Upd.RemovedText"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangelog))]
    public partial string? Changelog { get; set; }

    [ObservableProperty] public partial bool IsLoadingChangelog { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    public bool HasChangelog => !string.IsNullOrEmpty(Changelog);
}

/// <summary>A page number under the Workshop results; no number is the "…" between far pages.</summary>
public sealed record WsPageButton(int? Number, bool IsCurrent)
{
    public bool IsGap => Number is null;
    public bool IsPage => Number is not null;
    public string Text => Number?.ToString() ?? "…";
}
