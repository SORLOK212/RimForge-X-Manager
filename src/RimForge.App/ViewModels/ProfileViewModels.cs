using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Profiles;
using RimForge.Core.Saves;

namespace RimForge.App.ViewModels;

internal static class Dates
{
    public static string Format(DateTime local) =>
        local.ToString("g", CultureInfo.GetCultureInfo(Loc.Current == "ru" ? "ru-RU" : "en-US"));
}

/// <summary>A modpack card on the Modpacks page and a row in the top-bar switcher.</summary>
public sealed partial class ProfileViewModel(Profile model) : ObservableObject
{
    public Profile Model { get; } = model;

    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial string CountText { get; set; } = "";
    [ObservableProperty] public partial string MissingText { get; set; } = "";
    [ObservableProperty] public partial string ModifiedText { get; set; } = "";
    [ObservableProperty] public partial bool HasMissing { get; set; }

    public string Name => Model.Name;

    public void Refresh(Func<string, bool> isInstalled)
    {
        var missing = Model.ModIds.Count(id => !isInstalled(id));
        CountText = Loc.Format("Profiles.ModCount", Model.Mods.Count);
        HasMissing = missing > 0;
        MissingText = HasMissing ? Loc.Format("Profiles.Missing", missing) : "";
        ModifiedText = Loc.Format("Profiles.Modified", Dates.Format(Model.ModifiedUtc.ToLocalTime()));
        OnPropertyChanged(nameof(Name));
    }
}

/// <summary>A save or game mod list offered in the "modpack from a save" window.</summary>
public sealed partial class ImportSourceViewModel(ModListSource source) : ObservableObject
{
    public ModListSource Source { get; } = source;
    public string Name => Source.Name;
    public bool IsSave => Source.Kind == ModListSourceKind.Save;
    public ModListMeta? Meta { get; private set; }

    [ObservableProperty] public partial string InfoText { get; set; } = "";
    [ObservableProperty] public partial string MissingText { get; set; } = "";
    [ObservableProperty] public partial bool HasMissing { get; set; }
    [ObservableProperty] public partial bool IsReady { get; set; }

    public void SetMeta(ModListMeta meta, Func<string, bool> isInstalled)
    {
        Meta = meta;
        var kind = Loc.Get(IsSave ? "Import.Save" : "Import.List");
        InfoText = Loc.Format("Import.Info", kind, Dates.Format(Source.Modified), meta.Mods.Count);
        var missing = meta.Mods.Count(m => !isInstalled(m.Id));
        HasMissing = missing > 0;
        MissingText = HasMissing ? Loc.Format("Import.Missing", missing) : Loc.Get("Import.AllPresent");
        IsReady = true;
    }
}

/// <summary>A simple in-window question: title, text, optional input and up to three buttons.</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string> _result = new();

    public required string Title { get; init; }
    public string? Text { get; init; }
    public bool HasInput { get; init; }
    public string? InputHint { get; init; }
    public required string PrimaryText { get; init; }
    public bool PrimaryIsDanger { get; init; }
    public string? SecondaryText { get; init; }
    public bool HasSecondary => SecondaryText is not null;
    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>Optional list to pick one item from (e.g. "whose version should win").</summary>
    public IReadOnlyList<string>? Choices { get; init; }
    public bool HasChoices => Choices is { Count: > 0 };

    [ObservableProperty] public partial int ChoiceIndex { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string Input { get; set; } = "";

    public Task<string> Result => _result.Task;

    private bool CanPrimary() => !HasInput || !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private void Primary() => _result.TrySetResult("primary");

    [RelayCommand]
    private void Secondary() => _result.TrySetResult("secondary");

    [RelayCommand]
    public void Cancel() => _result.TrySetResult("cancel");
}
