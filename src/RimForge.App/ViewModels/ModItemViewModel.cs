using CommunityToolkit.Mvvm.ComponentModel;
using RimForge.Core.Models;

namespace RimForge.App.ViewModels;

/// <summary>One row in the Available or Active list.</summary>
public partial class ModItemViewModel : ObservableObject
{
    public ModItemViewModel(ModInfo mod, bool isMissing = false)
    {
        Mod = mod;
        IsMissing = isMissing;
        IssueTip = "";
        IssueLevel = "";
        // Matches name, authors and id; built once so typing in search stays fast.
        SearchText = $"{mod.Name}\n{string.Join(' ', mod.Authors)}\n{mod.PackageId}".ToLowerInvariant();
    }

    public ModInfo Mod { get; }

    /// <summary>The mod is in the game's active list but not installed.</summary>
    public bool IsMissing { get; }

    public string Id => Mod.PackageId;
    public string Name => Mod.IsCore ? "RimWorld" : Mod.Name;
    public string AuthorText => string.Join(", ", Mod.Authors);
    public bool HasAuthor => Mod.Authors.Count > 0;
    public string SearchText { get; }

    /// <summary>The About.xml description without BBCode and rich-text tags.</summary>
    public string PlainDescription => _plainDescription ??= RimForge.Steam.Workshop.BbCode.ToPlainText(Mod.Description);
    private string? _plainDescription;

    public bool IsWorkshop => !IsMissing && Mod.Source == ModSource.Workshop;
    public bool IsOfficialOrMissing => IsMissing || Mod.Source == ModSource.Official;
    public bool IsLocal => !IsMissing && Mod.Source == ModSource.Local;
    public bool IsOfficial => !IsMissing && Mod.Source == ModSource.Official;

    [ObservableProperty] public partial bool IsActive { get; set; }

    /// <summary>1-based position in the load order (0 when inactive).</summary>
    [ObservableProperty] public partial int Index { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(HasWarning), nameof(HasIssue), nameof(ShowIssue))]
    public partial string IssueLevel { get; set; }

    [ObservableProperty] public partial string IssueTip { get; set; }

    /// <summary>Installed recently and not played with yet.</summary>
    [ObservableProperty] public partial bool IsNew { get; set; }

    /// <summary>The Workshop updated it since the game last ran.</summary>
    [ObservableProperty] public partial bool IsUpdated { get; set; }

    [ObservableProperty] public partial bool IsFavorite { get; set; }

    /// <summary>Set when the player pinned the mod to the top or bottom of the list (the tooltip says which).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    public partial string PinTip { get; set; } = "";

    public bool IsPinned => PinTip.Length > 0;

    /// <summary>What the mod contains (filled in by a background scan).</summary>
    public RimForge.Core.Content.ModProfile? Profile { get; set; }

    /// <summary>Categories after the player's own corrections.</summary>
    public IReadOnlyList<RimForge.Core.Content.ModCategory> Categories { get; set; } = [];

    /// <summary>Being downloaded or updated right now (0..1); null when not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading), nameof(DownloadText), nameof(ShowMissing), nameof(ShowIssue))]
    public partial double? DownloadProgress { get; set; }

    /// <summary>The download stands still for now (Steam closed, no connection).</summary>
    [ObservableProperty] public partial bool IsDownloadWaiting { get; set; }

    public bool IsDownloading => DownloadProgress is not null;
    public string DownloadText => DownloadProgress is > 0.001 and var p ? $"{p:P0}" : "";

    /// <summary>Not on disk and not on its way either.</summary>
    public bool ShowMissing => IsMissing && !IsDownloading;

    /// <summary>The problem mark; a mod that is still downloading has none yet.</summary>
    public bool ShowIssue => HasIssue && !IsDownloading;

    public bool HasError => IssueLevel == "error";
    public bool HasWarning => IssueLevel == "warn";
    public bool HasIssue => IssueLevel.Length > 0;
}
