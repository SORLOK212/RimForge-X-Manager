using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Storage;
using RimForge.Core.Translation;
using RimForge.Translation;

namespace RimForge.App.ViewModels;

/// <summary>
/// Mod descriptions in the player's language: the Workshop mod that is opened and the selected mod on the Mods page.
/// Names stay as their authors wrote them — that is how mods are recognised and searched. Translations are cached on disk.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly TranslationService _translations = new(Path.Combine(AppDataPaths.Root, "cache", "translations.json"));
    private bool _translationWarned;

    /// <summary>Translate automatically everything that is shown.</summary>
    [ObservableProperty] public partial bool AutoTranslate { get; set; }

    [ObservableProperty] public partial string? SelectedTranslatedDescription { get; set; }
    [ObservableProperty] public partial string? SelectedSourceLanguage { get; set; }
    [ObservableProperty] public partial bool SelectedShowTranslated { get; set; } = true;
    [ObservableProperty] public partial bool IsTranslatingSelected { get; set; }

    private string TargetLanguage => Loc.Current;

    public string SelectedDisplayDescription => SelectedShowTranslated && SelectedTranslatedDescription is { } d ? d : SelectedMod?.PlainDescription ?? "";
    public bool SelectedIsTranslated => SelectedSourceLanguage is { } s && !s.StartsWith(TargetLanguage) && SelectedTranslatedDescription is not null;
    public bool SelectedCanTranslate => !SelectedIsTranslated && !IsTranslatingSelected && SelectedHasDescription
                                        && !LanguageGuess.IsAlreadyIn(SelectedMod?.PlainDescription ?? "", TargetLanguage);
    public string SelectedTranslationText => SelectedSourceLanguage is { } s
        ? Loc.Format(SelectedShowTranslated ? "Tr.TranslatedFrom" : "Tr.ShowingOriginal", LanguageGuess.DisplayName(s, Loc.Current))
        : "";
    public string SelectedTranslationToggleText => Loc.Get(SelectedShowTranslated ? "Tr.ShowOriginal" : "Tr.ShowTranslation");

    private void InitTranslation()
    {
        // Online by default; the built-in model when the player chose it, or when the online translator fails.
        _translator = new SmartTranslator(new OnlineTranslator(Http), ModelDir)
        {
            PreferOffline = _settings.TranslateOffline,
            FallBackToOffline = _settings.TranslateFallback,
        };
        _translations.Translator = _translator;
        AutoTranslate = _settings.AutoTranslate;
        RefreshModelState();
    }

    partial void OnAutoTranslateChanged(bool value)
    {
        if (_settings.AutoTranslate != value)
        {
            _settings.AutoTranslate = value;
            _settings.Save();
        }
        if (!value) return;
        if (SelectedCard is { } card) _ = TranslateCardDescriptionAsync(card);
        _ = TranslateSelectedAsync();
    }

    private void NotifySelectedTranslation()
    {
        foreach (var name in new[]
                 {
                     nameof(SelectedDisplayDescription), nameof(SelectedIsTranslated), nameof(SelectedCanTranslate),
                     nameof(SelectedTranslationText), nameof(SelectedTranslationToggleText),
                 })
            OnPropertyChanged(name);
    }

    partial void OnSelectedTranslatedDescriptionChanged(string? value) => NotifySelectedTranslation();
    partial void OnSelectedShowTranslatedChanged(bool value) => NotifySelectedTranslation();
    partial void OnIsTranslatingSelectedChanged(bool value) => NotifySelectedTranslation();

    /// <summary>A new mod was selected on the Mods page: show what is cached, translate if automatic.</summary>
    private void OnSelectedModForTranslation()
    {
        SelectedTranslatedDescription = null;
        SelectedSourceLanguage = null;
        SelectedShowTranslated = true;
        if (SelectedMod is not { IsMissing: false } mod || mod.IsOfficial) return;
        var description = mod.PlainDescription;
        if (_translations.Cached(description, TargetLanguage) is { } known)
        {
            SelectedSourceLanguage = known.SourceLanguage;
            SelectedTranslatedDescription = known.Text;
        }
        else if (AutoTranslate) _ = TranslateSelectedAsync();
        NotifySelectedTranslation();
    }

    private async Task TranslateSelectedAsync()
    {
        if (SelectedMod is not { IsMissing: false } mod || mod.IsOfficial || IsTranslatingSelected) return;
        var description = mod.PlainDescription;
        if (description.Length == 0 || LanguageGuess.IsAlreadyIn(description, TargetLanguage)) return;
        IsTranslatingSelected = true;
        try
        {
            var done = await _translations.TranslateAsync(description, TargetLanguage);
            if (SelectedMod != mod) return;
            SelectedSourceLanguage = done.SourceLanguage;
            SelectedTranslatedDescription = done.Text;
            SelectedShowTranslated = true;
            SaveTranslationsLater();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnTranslation(ex);
        }
        finally
        {
            IsTranslatingSelected = false;
        }
    }

    /// <summary>"Translate" / "Show original" on the Mods page.</summary>
    [RelayCommand]
    private Task ToggleSelectedTranslation()
    {
        if (SelectedTranslatedDescription is null) return TranslateSelectedAsync();
        SelectedShowTranslated = !SelectedShowTranslated;
        return Task.CompletedTask;
    }

    // ───────────────────────── Workshop cards ─────────────────────────

    /// <summary>The description of a card (when it is opened, or its 文A badge is clicked).</summary>
    private async Task TranslateCardDescriptionAsync(WorkshopCardViewModel card)
    {
        if (card.TranslatedDescription is not null || card.IsTranslating || !card.IsForeign) return;
        card.IsTranslating = true;
        try
        {
            card.SetDescription(await _translations.TranslateAsync(card.Description, TargetLanguage), TargetLanguage);
            card.ShowTranslated = true;
            SaveTranslationsLater();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnTranslation(ex);
        }
        finally
        {
            card.IsTranslating = false;
        }
    }

    /// <summary>The 文A button on a card: translate its description, or switch between translation and original.</summary>
    [RelayCommand]
    private Task ToggleCardTranslation(WorkshopCardViewModel card)
    {
        if (card.TranslatedDescription is null) return TranslateCardDescriptionAsync(card);
        card.ShowTranslated = !card.ShowTranslated;
        return Task.CompletedTask;
    }

    /// <summary>Cached translations right away and the description's language for the badge — no requests.</summary>
    private void OnCardsAdded(IReadOnlyList<WorkshopCardViewModel> cards)
    {
        foreach (var card in cards)
        {
            if (_translations.Cached(card.Description, TargetLanguage) is { } d) card.SetDescription(d, TargetLanguage);
            else card.GuessLanguage(TargetLanguage);
        }
    }

    private void OnCardSelectedForTranslation(WorkshopCardViewModel card)
    {
        if (AutoTranslate) _ = TranslateCardDescriptionAsync(card);
    }

    private void ResetTranslationsForLanguage()
    {
        foreach (var card in WorkshopResults) card.ClearTranslation();
        OnCardsAdded(WorkshopResults.ToList());
        OnSelectedModForTranslation();
    }

    private void WarnTranslation(Exception ex)
    {
        if (_translationWarned) return;
        _translationWarned = true;
        ShowToast(Loc.Format("Tr.Failed", ex.Message));
    }

    private int _saveScheduled;

    private void SaveTranslationsLater()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Interlocked.Exchange(ref _saveScheduled, 0);
            _translations.Save();
        });
    }
}
