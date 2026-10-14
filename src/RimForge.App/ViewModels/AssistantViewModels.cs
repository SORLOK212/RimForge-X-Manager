using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RimForge.App.Services;
using RimForge.Steam.Assistant;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

/// <summary>One message in the assistant's chat.</summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public required bool IsUser { get; init; }
    public bool IsAssistant => !IsUser;

    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>The assistant is still working on this answer.</summary>
    [ObservableProperty] public partial bool IsBusy { get; set; }
}

/// <summary>A quick theme button under the chat.</summary>
public sealed class PackTopicViewModel(PackTopic topic)
{
    public PackTopic Topic { get; } = topic;
    public string Glyph => Topic.Glyph;
    public string Name => AssistantTexts.TopicName(Topic.Id);
}

/// <summary>One mod of the suggested pack, with a tick to keep it or not.</summary>
public sealed partial class PackPickViewModel(PackPick pick, bool selected, Action changed) : ObservableObject
{
    public PackPick Pick { get; } = pick;
    public WorkshopItem Item => Pick.Item;
    public string Title => Item.Title;
    public string Details => string.Join(" · ", new[]
    {
        Loc.Format("Ws.Subscribers", Humanize.Count(Item.Subscribers)),
        Humanize.Size(Item.SizeBytes),
        Loc.Format("Ws.Updated", Humanize.Ago(Item.UpdatedUtc)),
    }.Where(s => s.Length > 0));

    public bool IsInstalled => Pick.IsInstalled;
    public bool IsCore => Pick.IsCore && !Pick.IsPerformance;
    public bool IsPerformance => Pick.IsPerformance;
    public bool HasReplaces => Pick.ReplacesTitle is not null;
    public string ReplacesText => Loc.Format("Asst.Replaces", Pick.ReplacesTitle);

    private string? _summary;
    /// <summary>The start of the description, for the tooltip.</summary>
    public string Summary => _summary ??= Shorten(BbCode.ToPlainText(Item.Description));

    [ObservableProperty] public partial bool IsSelected { get; set; } = selected;
    [ObservableProperty] public partial Bitmap? Preview { get; set; }

    partial void OnIsSelectedChanged(bool value) => changed();

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 420 ? text[..420].TrimEnd() + "…" : text;
    }
}

/// <summary>The mods chosen for one theme.</summary>
public sealed class PackGroupViewModel(string id, IEnumerable<PackPickViewModel> items)
{
    public string Id { get; } = id;
    public string Name => AssistantTexts.GroupName(Id);
    public string Glyph => Id == MainWindowViewModel.TranslationsGroup ? "\uE909" : PackTopics.Get(Id)?.Glyph ?? "";
    public ObservableCollection<PackPickViewModel> Items { get; } = new(items);
    public string CountText => Items.Count.ToString();
}

internal static class AssistantTexts
{
    public static string TopicName(string id) => Loc.Get("Asst.Topic." + id);

    /// <summary>A theme's name, or the searched words for a name the player typed ("Warhammer").</summary>
    public static string GroupName(string id) => id.StartsWith(PackPlanner.WordPrefix)
        ? Capitalize(id[PackPlanner.WordPrefix.Length..])
        : TopicName(id);

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
