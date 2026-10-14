using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimForge.App.Services;
using RimForge.Core.Profiles;
using RimForge.Core.Storage;
using RimForge.Steam.Assistant;
using RimForge.Steam.Client;
using RimForge.Steam.Translations;
using RimForge.Steam.Workshop;

namespace RimForge.App.ViewModels;

/// <summary>
/// The assistant: the player describes a modpack in plain words, RimForge finds fitting Workshop mods,
/// explains the pick and, once the player agrees, creates the modpack and downloads it.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private PackRequest? _asstRequest;
    private string _asstRequestText = "";
    private CancellationTokenSource? _asstCts;
    private bool _asstSyncing;
    private string _asstSuggestedName = "";
    private string? _asstBrainName;

    /// <summary>Ticks the player set by hand: they survive rebuilding the pack.</summary>
    private readonly Dictionary<ulong, bool> _asstTicks = [];

    public ObservableCollection<ChatMessageViewModel> AsstMessages { get; } = [];
    public ObservableCollection<PackGroupViewModel> AsstGroups { get; } = [];
    public ObservableCollection<PackPickViewModel> AsstAlternatives { get; } = [];
    public IReadOnlyList<PackTopicViewModel> AsstTopics => PackTopics.Quick.Select(t => new PackTopicViewModel(t)).ToList();
    public IReadOnlyList<string> AsstExamples => [Loc.Get("Asst.Example1"), Loc.Get("Asst.Example2"), Loc.Get("Asst.Example3")];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendAsstCommand))]
    public partial string AsstInput { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAsstPlan))]
    [NotifyCanExecuteChangedFor(nameof(SendAsstCommand), nameof(CreateAsstPackCommand))]
    public partial bool IsAsstBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAsstPlan), nameof(CanEditAsstPlan))]
    [NotifyCanExecuteChangedFor(nameof(CreateAsstPackCommand))]
    public partial PackPlan? AsstPlan { get; set; }

    [ObservableProperty] public partial string AsstPackName { get; set; } = "";
    [ObservableProperty] public partial string AsstSelectionText { get; set; } = "";
    [ObservableProperty] public partial bool AsstFps { get; set; }
    [ObservableProperty] public partial bool AsstStable { get; set; }
    [ObservableProperty] public partial PackSize AsstSize { get; set; } = PackSize.Normal;

    public bool HasAsstPlan => AsstPlan is not null;
    public bool CanEditAsstPlan => HasAsstPlan && !IsAsstBusy;
    public bool HasAsstMessages => AsstMessages.Count > 0;
    public bool HasAsstAlternatives => AsstAlternatives.Count > 0;
    public string AsstAlternativesText => Loc.Format("Asst.Alternatives", AsstAlternatives.Count);
    public bool AsstSizeSmall { get => AsstSize == PackSize.Small; set { if (value) AsstSize = PackSize.Small; } }
    public bool AsstSizeNormal { get => AsstSize == PackSize.Normal; set { if (value) AsstSize = PackSize.Normal; } }
    public bool AsstSizeLarge { get => AsstSize == PackSize.Large; set { if (value) AsstSize = PackSize.Large; } }

    private IEnumerable<PackPickViewModel> AsstAllPicks => AsstGroups.SelectMany(g => g.Items).Concat(AsstAlternatives);

    // ───────────────────────── Talking ─────────────────────────

    private bool CanSendAsst() => !IsAsstBusy && !string.IsNullOrWhiteSpace(AsstInput);

    [RelayCommand(CanExecute = nameof(CanSendAsst))]
    private Task SendAsst()
    {
        var text = AsstInput.Trim();
        AsstInput = "";
        return AskAssistantAsync(text);
    }

    /// <summary>A quick theme button: a pack about that theme (with the switches below the chat).</summary>
    [RelayCommand]
    private Task AskAsstTopic(PackTopicViewModel topic) =>
        IsAsstBusy ? Task.CompletedTask : AskAssistantAsync(Loc.Format("Asst.TopicRequest", topic.Name), new PackRequest { Topics = [topic.Topic.Id] });

    [RelayCommand]
    private Task AskAsstExample(string example) => IsAsstBusy ? Task.CompletedTask : AskAssistantAsync(example);

    /// <summary>Starts a new conversation.</summary>
    [RelayCommand]
    private void ClearAsst()
    {
        _asstCts?.Cancel();
        AsstMessages.Clear();
        ClearAsstPlan();
        _asstTicks.Clear();
        AsstPackName = "";
        _asstRequest = null;
        OnPropertyChanged(nameof(HasAsstMessages));
    }

    private async Task AskAssistantAsync(string text, PackRequest? ready = null)
    {
        AddAsstMessage(new ChatMessageViewModel { IsUser = true, Text = text });
        if (ready is not null)
        {
            _asstRequestText = text;
            await BuildAsstPlanAsync(ready with { Fps = AsstFps, Stable = AsstStable, Size = AsstSize }, followUp: false);
            return;
        }

        _asstCts?.Cancel();
        var cts = _asstCts = new CancellationTokenSource();
        var answer = new ChatMessageViewModel { IsUser = false, IsBusy = true, Text = Loc.Get("Brain.Thinking") };
        var parsed = RequestParser.Parse(text, _asstRequest);

        // A neural network reads the request when one is set up; the word list is the fallback.
        BrainAnswer? brain = null;
        string? brainError = null;
        if (_settings.AsstBrain != AsstBrains.Words)
        {
            AddAsstMessage(answer);
            IsAsstBusy = true;
            try
            {
                if (await BrainEndpointAsync(answer, cts.Token) is { } endpoint)
                {
                    answer.Text = Loc.Get("Brain.Thinking");
                    // The current pack goes along only when the message looks like a change to it ("убери…", "добавь…").
                    brain = await AssistantBrain.UnderstandAsync(_llm, endpoint, text, parsed.IsFollowUp ? _asstRequest : null, cts.Token);
                    if (brain is null) brainError = Loc.Get("Brain.BadAnswer");
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                answer.IsBusy = false;
                answer.Text = Loc.Get("Asst.Cancelled");
                return;
            }
            catch (Exception ex) when (ex is LlmException or HttpRequestException or OperationCanceledException or IOException)
            {
                brainError = ex is OperationCanceledException ? Loc.Get("Brain.Server.Timeout") : ex.Message;
            }
            finally
            {
                if (_asstCts == cts) IsAsstBusy = false;
            }
        }

        PackRequest request;
        bool followUp;
        string? intro = null;
        // A small network may just repeat the current pack for a message that changes nothing ("привет").
        var echo = brain is { IsFollowUp: true } && !parsed.Understood;
        if (brain is { Request.IsEmpty: false } && !echo)
        {
            request = AssistantBrain.Combine(brain.Request, parsed);
            followUp = brain.IsFollowUp;
            intro = brain.Reply;
            // Small networks sometimes name a Russian request in English: then RimForge's own name is better.
            _asstBrainName = brain.Name is { } n && (!HasCyrillic(text) || HasCyrillic(n)) ? n : null;
        }
        else if (parsed.Understood && !parsed.Request.IsEmpty && !echo)
        {
            request = parsed.Request;
            followUp = parsed.IsFollowUp;
            if (brainError is not null) intro = Loc.Format("Brain.Fallback", brainError);
            _asstBrainName = null;
        }
        else
        {
            var reply = brain?.Reply is { Length: > 0 } said ? said + "\n\n" + Loc.Get("Asst.NotUnderstood") : Loc.Get("Asst.NotUnderstood");
            if (brainError is not null) reply = Loc.Format("Brain.Fallback", brainError) + "\n" + reply;
            answer.IsBusy = false;
            answer.Text = reply;
            if (!AsstMessages.Contains(answer)) AddAsstMessage(answer);
            return;
        }

        // The switches under the chat keep their state unless the request says otherwise.
        if (!followUp) request = request with { Fps = request.Fps || AsstFps, Stable = request.Stable || AsstStable };
        if (!followUp) _asstRequestText = text;
        else _asstRequestText += " · " + text;
        await BuildAsstPlanAsync(request, followUp, answer, intro);
    }

    /// <summary>The switches (fast, stable, size) rebuild the current pack at once.</summary>
    partial void OnAsstFpsChanged(bool value) => RebuildForSwitches();
    partial void OnAsstStableChanged(bool value) => RebuildForSwitches();

    partial void OnAsstSizeChanged(PackSize value)
    {
        OnPropertyChanged(nameof(AsstSizeSmall));
        OnPropertyChanged(nameof(AsstSizeNormal));
        OnPropertyChanged(nameof(AsstSizeLarge));
        RebuildForSwitches();
    }

    private void RebuildForSwitches()
    {
        if (_asstSyncing || _asstRequest is null || IsAsstBusy) return;
        var request = _asstRequest with { Fps = AsstFps, Stable = AsstStable, Size = AsstSize, Count = AsstSize == _asstRequest.Size ? _asstRequest.Count : null };
        if (request == _asstRequest) return;
        AddAsstMessage(new ChatMessageViewModel { IsUser = true, Text = Loc.Format("Asst.SwitchesChanged", DescribeSwitches(request)) });
        _ = BuildAsstPlanAsync(request, followUp: true);
    }

    [RelayCommand]
    private Task RebuildAsst() => _asstRequest is null || IsAsstBusy ? Task.CompletedTask : BuildAsstPlanAsync(_asstRequest, followUp: true);

    private void AddAsstMessage(ChatMessageViewModel message)
    {
        AsstMessages.Add(message);
        OnPropertyChanged(nameof(HasAsstMessages));
    }

    // ───────────────────────── Building the pack ─────────────────────────

    private async Task BuildAsstPlanAsync(PackRequest request, bool followUp, ChatMessageViewModel? answer = null, string? intro = null)
    {
        _asstCts?.Cancel();
        var cts = _asstCts = new CancellationTokenSource();
        _asstRequest = request;
        if (!followUp) _asstTicks.Clear(); // a new request starts from scratch
        SyncAsstSwitches(request);

        answer ??= new ChatMessageViewModel { IsUser = false };
        answer.IsBusy = true;
        answer.Text = Loc.Get("Asst.Searching");
        if (!AsstMessages.Contains(answer)) AddAsstMessage(answer);
        IsAsstBusy = true;
        try
        {
            var searched = new List<string>();
            var progress = new Progress<string>(query =>
            {
                if (!answer.IsBusy) return; // a late report must not overwrite the answer
                searched.Add(query);
                answer.Text = Loc.Format("Asst.SearchingFor", string.Join(", ", searched.TakeLast(4)));
            });
            var plan = await PackPlanner.BuildAsync(request, AsstContext(), new AssistantSource(this), progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            ShowAsstPlan(plan);
            answer.Text = (intro is { Length: > 0 } ? intro + "\n\n" : "") + DescribeAsstPlan(plan);
            if (plan.Picks.Count == 0 && plan.Notes.FailedSearches > 0) answer.Text = Loc.Get("Asst.Offline");
            else if (plan.Picks.Count > 0) _ = AddPlanTranslationsAsync(plan, answer, cts.Token);
        }
        catch (OperationCanceledException)
        {
            answer.Text = Loc.Get("Asst.Cancelled");
        }
        catch (Exception ex)
        {
            answer.Text = Loc.Format("Asst.Failed", ex.Message);
        }
        finally
        {
            answer.IsBusy = false;
            if (_asstCts == cts) IsAsstBusy = false;
        }
    }

    private void SyncAsstSwitches(PackRequest request)
    {
        _asstSyncing = true;
        AsstFps = request.Fps;
        AsstStable = request.Stable;
        AsstSize = request.Size;
        _asstSyncing = false;
    }

    private PackContext AsstContext()
    {
        var owned = WorkshopItem.Dlcs.Where(d => _installed.ContainsKey("ludeon.rimworld." + d.ToLowerInvariant())).ToList();
        return new PackContext(_gameVersion ?? "1.6", owned, _byWorkshopId.Keys.ToHashSet())
        {
            Replacement = id => _replacements.For(id)?.NewWorkshopId,
        };
    }

    /// <summary>Searches through the Steam client when it runs (several at once), otherwise the website (two at a time).</summary>
    private sealed class AssistantSource(MainWindowViewModel vm) : IWorkshopSource
    {
        private readonly SemaphoreSlim _gate = new(SteamBridge.IsSteamRunning() && !Core.Launch.GameLauncher.IsRunning() ? 6 : 2);

        public async Task<IReadOnlyList<WorkshopItem>> SearchAsync(WorkshopQuery query, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                return (await vm.QueryAsync(query, ct)).Items;
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<IReadOnlyList<WorkshopItem>> GetDetailsAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct) =>
            vm._web.GetDetailsAsync(ids, ct);
    }

    private void ClearAsstPlan()
    {
        foreach (var pick in AsstAllPicks) pick.Preview?.Dispose();
        AsstGroups.Clear();
        AsstAlternatives.Clear();
        AsstPlan = null;
        OnPropertyChanged(nameof(HasAsstAlternatives));
    }

    private void ShowAsstPlan(PackPlan plan)
    {
        ClearAsstPlan();
        AsstPlan = plan;

        var order = plan.Request.Topics.Concat(plan.Request.Words.Select(w => PackPlanner.WordPrefix + w)).ToList();
        foreach (var group in plan.Picks.GroupBy(p => p.Group).OrderBy(g => g.Key == PackTopics.Performance ? int.MaxValue : order.IndexOf(g.Key)))
            AsstGroups.Add(new PackGroupViewModel(group.Key, group.Select(p => MakePick(p, _asstTicks.GetValueOrDefault(p.Item.Id, true)))));
        foreach (var pick in plan.Alternatives)
            AsstAlternatives.Add(MakePick(pick, _asstTicks.GetValueOrDefault(pick.Item.Id, false)));
        OnPropertyChanged(nameof(HasAsstAlternatives));
        OnPropertyChanged(nameof(AsstAlternativesText));

        // A name the player typed stays; RimForge's own suggestion follows the request.
        var suggested = _asstBrainName is { Length: > 0 } brainName ? brainName : SuggestPackName(plan.Request);
        if (AsstPackName.Length == 0 || AsstPackName == _asstSuggestedName) AsstPackName = suggested;
        _asstSuggestedName = suggested;
        UpdateAsstSelection();
        foreach (var pick in AsstAllPicks) _ = LoadPickPreviewAsync(pick);
    }

    /// <summary>The group of translations in the assistant's pack.</summary>
    public const string TranslationsGroup = "translations";

    /// <summary>
    /// Once the pack is shown: translations into the player's language for its new mods, as their own group
    /// (ticked when they are for this game version and not older than the mod).
    /// </summary>
    private async Task AddPlanTranslationsAsync(PackPlan plan, ChatMessageViewModel answer, CancellationToken ct)
    {
        if (TrLanguageId is not { } language) return;
        var needs = plan.Picks.Where(p => !p.IsInstalled)
            .Select(p => new TranslationNeed(p.Item.Id.ToString(), p.Item.Title, p.Item.Id, p.Item.UpdatedUtc, 0))
            .ToList();
        if (needs.Count == 0) return;
        try
        {
            var allowed = plan.Picks.Select(p => p.Item.Id).Concat(_byWorkshopId.Keys).ToHashSet();
            var search = await TranslationFinder.FindAsync(needs, new TranslationContext(language, _gameVersion ?? "1.6", allowed),
                new AssistantSource(this), RequiredItemsAsync, null, ct);
            if (ct.IsCancellationRequested || AsstPlan != plan || search.Offers.Count == 0) return;
            var picks = search.Offers
                .Select(o => MakePick(new PackPick(o.Item, TranslationsGroup, false, false, _byWorkshopId.ContainsKey(o.Item.Id)),
                    _asstTicks.GetValueOrDefault(o.Item.Id, o.SupportsVersion && o.Fresh)))
                .ToList();
            AsstGroups.Add(new PackGroupViewModel(TranslationsGroup, picks));
            foreach (var pick in picks) _ = LoadPickPreviewAsync(pick);
            UpdateAsstSelection();
            answer.Text += "\n\n" + Loc.Format("Asst.Translations", search.Offers.Sum(o => o.Covers.Count), TrLanguageName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the pack works without them; the translations button finds them later
        }
        catch (OperationCanceledException)
        {
        }
    }

    private PackPickViewModel MakePick(PackPick pick, bool selected)
    {
        PackPickViewModel? vm = null;
        vm = new PackPickViewModel(pick, selected, () =>
        {
            _asstTicks[vm!.Item.Id] = vm.IsSelected;
            UpdateAsstSelection();
        });
        return vm;
    }

    private async Task LoadPickPreviewAsync(PackPickViewModel pick) =>
        pick.Preview = await _previews.GetAsync(pick.Item.PreviewUrl, 160);

    private void UpdateAsstSelection()
    {
        var chosen = AsstAllPicks.Where(p => p.IsSelected).ToList();
        var download = chosen.Where(p => !p.IsInstalled).ToList();
        var have = chosen.Count - download.Count;
        AsstSelectionText = Loc.Format("Asst.Selection", chosen.Count, Humanize.Size(download.Sum(p => p.Item.SizeBytes)) is { Length: > 0 } size ? size : "0")
                            + (have > 0 ? " · " + Loc.Format("Asst.Have", have) : "");
        CreateAsstPackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AsstSelectGroup(PackGroupViewModel group)
    {
        var select = !group.Items.All(p => p.IsSelected);
        foreach (var pick in group.Items) pick.IsSelected = select;
    }

    [RelayCommand]
    private void OpenAsstPick(PackPickViewModel pick) => OpenUrl(pick.Item.Url);

    // ───────────────────────── Creating the modpack ─────────────────────────

    private bool CanCreateAsstPack() => CanEditAsstPlan && AsstAllPicks.Any(p => p.IsSelected);

    [RelayCommand(CanExecute = nameof(CanCreateAsstPack))]
    private async Task CreateAsstPack()
    {
        var chosen = AsstAllPicks.Where(p => p.IsSelected).Select(p => p.Item).DistinctBy(i => i.Id).ToList();
        if (chosen.Count == 0) return;
        var download = chosen.Where(i => !_byWorkshopId.ContainsKey(i.Id)).ToList();
        var name = UniqueProfileName(string.IsNullOrWhiteSpace(AsstPackName) ? SuggestPackName(_asstRequest ?? new PackRequest()) : AsstPackName.Trim());

        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Format("Asst.ConfirmTitle", name),
            Text = Loc.Format("Asst.ConfirmText", chosen.Count, download.Count, Humanize.Size(download.Sum(i => i.SizeBytes)) is { Length: > 0 } size ? size : "0")
                   + (download.Count > 0 ? "\n\n" + Loc.Get("Asst.ConfirmSteam") : ""),
            PrimaryText = Loc.Get(download.Count > 0 ? "Coll.CreateAndDownload" : "Dialog.Create"),
        });
        if (answer != "primary" || !await ConfirmLeaveAsync()) return;

        AddAsstMessage(new ChatMessageViewModel
        {
            IsUser = false,
            Text = Loc.Format(download.Count > 0 ? "Asst.Created" : "Asst.CreatedNoDownload", name, download.Count),
        });
        await CreateWorkshopModpackAsync(name, Loc.Format("Asst.Notes", _asstRequestText), chosen);
    }

    // ───────────────────────── Words ─────────────────────────

    private static bool HasCyrillic(string s) => s.Any(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё');

    private static string SuggestPackName(PackRequest request)
    {
        var names = request.Topics.Select(AssistantTexts.TopicName)
            .Concat(request.Words.Select(AssistantTexts.Capitalize))
            .Take(3)
            .ToList();
        var name = names.Count > 0 ? string.Join(" + ", names) : Loc.Get("Asst.DefaultName");
        return request.Fps ? name + " · FPS" : name;
    }

    private static string DescribeSwitches(PackRequest request)
    {
        var parts = new List<string> { Loc.Get("Asst.Size." + request.Size) };
        if (request.Fps) parts.Add(Loc.Get("Asst.Fps"));
        if (request.Stable) parts.Add(Loc.Get("Asst.Stable"));
        return string.Join(", ", parts);
    }

    /// <summary>The assistant's short answer: what is in the pack and why.</summary>
    private string DescribeAsstPlan(PackPlan plan)
    {
        var request = plan.Request;
        if (plan.Picks.Count == 0) return Loc.Get("Asst.NothingFound");

        var lines = new List<string>();
        var have = plan.Picks.Count(p => p.IsInstalled);
        lines.Add(Loc.Format("Asst.Answer.Head", SuggestPackName(request), plan.Picks.Count,
            Humanize.Size(plan.DownloadBytes) is { Length: > 0 } size ? size : "0")
            + (have > 0 ? " " + Loc.Format("Asst.Answer.Have", have) : ""));
        if (request.GuessedTopics) lines.Add(Loc.Get("Asst.Answer.Guessed"));

        foreach (var group in plan.Picks.GroupBy(p => p.Group).Where(g => g.Key != PackTopics.Performance))
        {
            var titles = group.Take(3).Select(p => p.Item.Title).ToList();
            var more = group.Count() - titles.Count;
            lines.Add("• " + AssistantTexts.GroupName(group.Key) + ": " + string.Join(", ", titles)
                      + (more > 0 ? " " + Loc.Format("Asst.Answer.More", more) : ""));
        }

        if (request.Fps)
        {
            var perf = plan.Picks.Where(p => p.IsPerformance).Select(p => p.Item.Title).ToList();
            lines.Add(Loc.Format("Asst.Answer.Fps", perf.Count, string.Join(", ", perf.Take(3))));
            if (plan.Notes.HeavySkipped.Count > 0)
                lines.Add(Loc.Format("Asst.Answer.Heavy", string.Join(", ", plan.Notes.HeavySkipped.Take(3))));
        }
        if (request.Stable) lines.Add(Loc.Get("Asst.Answer.Stable"));
        if (request.ExcludeTopics.Count + request.ExcludeWords.Count > 0)
            lines.Add(Loc.Format("Asst.Answer.Without", string.Join(", ",
                request.ExcludeTopics.Select(AssistantTexts.TopicName).Concat(request.ExcludeWords))));
        foreach (var empty in plan.Notes.EmptyGroups)
            lines.Add(Loc.Format("Asst.Answer.Empty", AssistantTexts.GroupName(empty)));
        if (plan.Picks.Any(p => p.ReplacesTitle is not null))
            lines.Add(Loc.Format("Asst.Answer.Replaced", plan.Picks.Count(p => p.ReplacesTitle is not null)));
        if (plan.Notes.DlcSkipped > 0)
            lines.Add(Loc.Format(request.NoDlc ? "Asst.Answer.NoDlc" : "Asst.Answer.Dlc", plan.Notes.DlcSkipped));
        if (plan.Notes.FailedSearches > 0) lines.Add(Loc.Get("Asst.Answer.Failed"));

        lines.Add(Loc.Format("Asst.Answer.Safety", _gameVersion ?? "?"));
        lines.Add(Loc.Get("Asst.Answer.Next"));
        return string.Join("\n", lines);
    }

    /// <summary>The language changed: names in the plan follow.</summary>
    private void RefreshAsstTexts()
    {
        OnPropertyChanged(nameof(AsstExamples));
        OnPropertyChanged(nameof(AsstTopics));
        OnPropertyChanged(nameof(AsstAlternativesText));
        OnPropertyChanged(nameof(AsstBrainText));
        OnPropertyChanged(nameof(BrainModelSizeText));
        RefreshBrainModelState();
        if (AsstPlan is not null) UpdateAsstSelection();
    }
}
