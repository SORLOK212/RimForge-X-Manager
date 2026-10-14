using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RimForge.App.Services;
using RimForge.Core.Models;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

/// <summary>What a row of the translations list does.</summary>
public enum TranslationAction
{
    /// <summary>Download a translation from the Workshop and turn it on.</summary>
    Download,
    /// <summary>A translation already on disk: just turn it on.</summary>
    Enable,
    /// <summary>A fresher translation in place of an outdated one.</summary>
    Replace,
}

/// <summary>One translation offered for the player's mods.</summary>
public sealed partial class TranslationRowViewModel(TranslationAction action, string title, IReadOnlyList<string> forMods, bool selected, Action changed)
    : ObservableObject
{
    public TranslationAction Action { get; } = action;
    public string Title { get; } = title;
    /// <summary>Names of the player's mods it translates (with how much is translated now).</summary>
    public IReadOnlyList<string> ForMods { get; } = forMods;
    public string ForText => Loc.Format(ForMods.Count >= 3 ? "Trn.ForPack" : "Trn.For", string.Join(", ", ForMods.Take(6)) + (ForMods.Count > 6 ? " …" : ""), ForMods.Count);

    /// <summary>The Workshop page (downloads and replacements).</summary>
    public WorkshopItem? Item { get; init; }
    /// <summary>The installed translation mod (turning on).</summary>
    public ModInfo? Installed { get; init; }
    /// <summary>The outdated translation it replaces (package id).</summary>
    public string? ReplacesId { get; init; }
    public string? ReplacesName { get; init; }
    /// <summary>Package ids of the mods it translates.</summary>
    public IReadOnlyList<string> Covers { get; init; } = [];

    public bool IsPack => ForMods.Count >= 3;
    public bool SupportsVersion { get; init; }
    public bool IsOld => Item is not null && !SupportsVersion;
    public bool Verified { get; init; }
    public bool HasReplaces => ReplacesName is not null;
    public string ReplacesText => Loc.Format("Trn.Replaces", ReplacesName);
    public string PackText => Loc.Format("Trn.Pack", ForMods.Count);

    public string Details => Item is null ? ""
        : string.Join(" · ", new[]
        {
            Loc.Format("Ws.Subscribers", Humanize.Count(Item.Subscribers)),
            Humanize.Size(Item.SizeBytes),
            Loc.Format("Ws.Updated", Humanize.Ago(Item.UpdatedUtc)),
        }.Where(s => s.Length > 0));

    [ObservableProperty] public partial bool IsSelected { get; set; } = selected;
    [ObservableProperty] public partial Bitmap? Preview { get; set; }

    partial void OnIsSelectedChanged(bool value) => changed();
}
