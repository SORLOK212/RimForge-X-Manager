using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RimForge.App.ViewModels;

namespace RimForge.App.Views;

/// <summary>
/// View-only behaviour that is awkward in XAML: drag &amp; drop between the lists,
/// double-click / keyboard toggling and the folder picker.
/// </summary>
public partial class MainWindow : Window
{
    private const string DragFormat = "rimforge/mods";

    private Point? _pressPoint;
    private ListBox? _pressList;
    private ListBoxItem? _markedItem;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var list in new[] { AvailableList, ActiveList })
        {
            list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            list.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, (_, _) => _pressPoint = null, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            list.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropMarkers());
            list.AddHandler(DragDrop.DropEvent, OnDrop);
            list.DoubleTapped += OnListDoubleTapped;
            list.KeyDown += OnListKeyDown;
            // A mod opened from elsewhere (e.g. Diagnostics) must be brought into view; the list may still be hidden, hence the post.
            var current = list;
            current.SelectionChanged += (_, _) =>
            {
                if (current.SelectedItem is { } item)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => current.ScrollIntoView(item), Avalonia.Threading.DispatcherPriority.Loaded);
            };
        }

        // Any choice in the modpack switcher or sort menu closes it. Closing is posted so the button's command
        // still runs first: once the popup closes, the button leaves the window and loses its command.
        foreach (var button in new[] { ProfileButton, SortButton, GameButton })
            if (button.Flyout is Flyout { Content: Control content } flyout)
                content.AddHandler(Button.ClickEvent, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(flyout.Hide));

        // The assistant's chat follows the newest message as it grows.
        AsstScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty) AsstScroll.ScrollToEnd();
        };

        // The right-click menu of the load order shows the current pins.
        ActiveMenu.Opening += (_, _) => Vm.PrepareModMenu();

        Closing += (_, _) => SavePlace();

        // A picture full screen: ← → and the mouse wheel turn it.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not MainWindowViewModel { FullGallery: { } gallery }) return;
            if (e.Key == Key.Left) gallery.PrevCommand.Execute(null);
            else if (e.Key == Key.Right) gallery.NextCommand.Execute(null);
            else return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        GalleryOverlay.PointerWheelChanged += (_, e) =>
        {
            if (DataContext is not MainWindowViewModel { FullGallery: { } gallery }) return;
            (e.Delta.Y > 0 ? gallery.PrevCommand : gallery.NextCommand).Execute(null);
            e.Handled = true;
        };

        // A modpack file dropped anywhere on the window opens its summary.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DropEvent, OnFileDrop);

        // Put the cursor into the name box as soon as a question with a text field appears.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.PickSaveFile = PickSaveFileAsync;
            vm.PickOpenFile = PickModpackFileAsync;
            vm.PickFolder = PickFolderAsync;
            RestorePlace(vm.WindowPlace);
            InitLayout();
            vm.CopyToClipboard = async text =>
            {
                if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            };
            vm.BringToFront = () =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                // Activate alone does not raise a window that is behind another app; a short Topmost does.
                Topmost = true;
                Activate();
                Topmost = false;
            };
            vm.PropertyChanged += (_, e) =>
            {
                // The problems list must not hang over a question or another page it led to.
                if (e.PropertyName is nameof(MainWindowViewModel.Dialog) or nameof(MainWindowViewModel.CurrentPage) or nameof(MainWindowViewModel.IsFixAllOpen))
                    StatusButton.Flyout?.Hide();
                if (e.PropertyName == nameof(MainWindowViewModel.HasCard)) ApplyWsLayout();
                // A new page of Workshop results starts at its top.
                if (e.PropertyName == nameof(MainWindowViewModel.WsPage))
                    WsResultsList.FindDescendantOfType<ScrollViewer>()?.ScrollToHome();
                if (e.PropertyName == nameof(MainWindowViewModel.IsRulePickerOpen) && vm.IsRulePickerOpen)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => RulePickerSearchBox.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
                if (e.PropertyName == nameof(MainWindowViewModel.Dialog) && vm.Dialog is { HasInput: true })
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        DialogInput.Focus();
                        DialogInput.SelectAll();
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
            };
        };
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    // ── Resizable panels and the window's place ──

    private const string ModsLeft = "mods.left", ModsDetails = "mods.details", WsCard = "ws.card", AsstPlan = "asst.plan";
    private const double ModsDetailsDefault = 380, WsCardDefault = 400, AsstPlanDefault = 500;

    private void InitLayout()
    {
        ApplyModsLayout();
        ApplyWsLayout();
        ApplyAsstLayout();
        // Dragging a splitter remembers the widths; a double click puts the panels back as they were.
        foreach (var (splitter, save, reset) in new (GridSplitter, Action, Action)[]
                 {
                     (ModsSplitLeft, SaveModsLayout, () => ResetLayout(ApplyModsLayout, ModsLeft, ModsDetails)),
                     (ModsSplitRight, SaveModsLayout, () => ResetLayout(ApplyModsLayout, ModsLeft, ModsDetails)),
                     (WsSplit, () => SaveWidth(WsResultsGrid, 2, WsCard), () => ResetLayout(ApplyWsLayout, WsCard)),
                     (AsstSplit, () => SaveWidth(AsstGrid, 2, AsstPlan), () => ResetLayout(ApplyAsstLayout, AsstPlan)),
                 })
        {
            splitter.DragCompleted += (_, _) => save();
            splitter.DoubleTapped += (_, e) =>
            {
                reset();
                e.Handled = true;
            };
            ToolTip.SetTip(splitter, Application.Current?.FindResource("Tip.Splitter"));
        }
    }

    private double Stored(string key, double usual, double min, double max) => Math.Clamp(Vm.LayoutValue(key) ?? usual, min, max);

    /// <summary>The two lists share the room by a ratio (so both grow with the window); the details keep their width.</summary>
    private void ApplyModsLayout()
    {
        var columns = ModsGrid.ColumnDefinitions;
        columns[0].Width = new GridLength(Stored(ModsLeft, 1, 0.2, 5), GridUnitType.Star);
        columns[2].Width = new GridLength(1, GridUnitType.Star);
        columns[4].Width = new GridLength(Stored(ModsDetails, ModsDetailsDefault, 280, 900));
    }

    private void SaveModsLayout()
    {
        var columns = ModsGrid.ColumnDefinitions;
        if (columns[2].ActualWidth > 0) Vm.SetLayout(ModsLeft, columns[0].ActualWidth / columns[2].ActualWidth);
        Vm.SetLayout(ModsDetails, columns[4].ActualWidth);
        ApplyModsLayout();
    }

    /// <summary>The Workshop card takes room only while a mod is open.</summary>
    private void ApplyWsLayout()
    {
        var open = DataContext is MainWindowViewModel { HasCard: true };
        var columns = WsResultsGrid.ColumnDefinitions;
        columns[1].Width = new GridLength(open ? 12 : 0);
        columns[2].MinWidth = open ? 320 : 0;
        columns[2].Width = new GridLength(open ? Stored(WsCard, WsCardDefault, 320, 900) : 0);
    }

    private void ApplyAsstLayout() =>
        AsstGrid.ColumnDefinitions[2].Width = new GridLength(Stored(AsstPlan, AsstPlanDefault, 340, 1000));

    private void SaveWidth(Grid grid, int column, string key)
    {
        Vm.SetLayout(key, grid.ColumnDefinitions[column].ActualWidth);
        // The splitter may have turned the other column into pixels: give it back the rest of the room.
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
    }

    private void ResetLayout(Action apply, params string[] keys)
    {
        foreach (var key in keys) Vm.SetLayout(key, null);
        apply();
    }

    /// <summary>Opens where it was closed, as long as that place is still on one of the screens.</summary>
    private void RestorePlace(Core.Storage.WindowPlace? place)
    {
        if (place is null || place.Width < MinWidth || place.Height < MinHeight) return;
        var rect = new PixelRect(place.X, place.Y, (int)place.Width, (int)place.Height);
        var onScreen = Screens?.All.Any(s => s.WorkingArea.Intersects(rect) &&
                                             s.WorkingArea.Intersect(rect) is { Width: > 200, Height: > 100 }) ?? false;
        if (!onScreen) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(place.X, place.Y);
        Width = place.Width;
        Height = place.Height;
        if (place.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlace()
    {
        if (DataContext is not MainWindowViewModel vm || WindowState == WindowState.Minimized) return;
        // A maximized window keeps the size it had before, so un-maximizing it later still gives that.
        vm.WindowPlace = WindowState == WindowState.Maximized
            ? (vm.WindowPlace ?? new Core.Storage.WindowPlace(Position.X, Position.Y, Width, Height, true)) with { Maximized = true }
            : new Core.Storage.WindowPlace(Position.X, Position.Y, Width, Height, false);
    }

    private static ModItemViewModel? ItemAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ModItemViewModel;

    // ── Toggling ──

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemAt(e.Source) is { } item) Vm.Toggle(item);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        var list = (ListBox)sender!;
        var items = list.SelectedItems?.OfType<ModItemViewModel>().ToList() ?? [];
        if (items.Count == 0) return;

        if (e.Key == Key.Enter)
        {
            if (list == ActiveList) Vm.Deactivate(items);
            else Vm.Activate(items);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && list == ActiveList)
        {
            Vm.Deactivate(items);
            e.Handled = true;
        }
    }

    // ── Drag & drop ──

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var onItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null;
        if (onItem && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressPoint = e.GetPosition(this);
            _pressList = (ListBox)sender!;
        }
        else
        {
            _pressPoint = null;
        }
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressPoint is not { } start || _pressList is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressPoint = null;
            return;
        }
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

        _pressPoint = null;
        var items = _pressList.SelectedItems?.OfType<ModItemViewModel>().ToList();
        if (items is null || items.Count == 0) return;

#pragma warning disable CS0618 // DataObject is the simplest in-process drag payload in Avalonia 11.
        var data = new DataObject();
        data.Set(DragFormat, items);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
        ClearDropMarkers();
    }

    private (ListBoxItem? Container, bool After) HitTest(ListBox list, DragEventArgs e)
    {
        var hit = list.InputHitTest(e.GetPosition(list)) as Visual;
        var container = hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (container is null) return (null, false);
        return (container, e.GetPosition(container).Y > container.Bounds.Height / 2);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var list = (ListBox)sender!;
#pragma warning disable CS0618
        if (!e.Data.Contains(DragFormat))
#pragma warning restore CS0618
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        ClearDropMarkers();
        list.Classes.Add("dropTarget");

        if (list == ActiveList && HitTest(list, e) is ({ } container, var after))
        {
            container.Classes.Add(after ? "dropBelow" : "dropAbove");
            _markedItem = container;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var list = (ListBox)sender!;
#pragma warning disable CS0618
        var items = e.Data.Get(DragFormat) as List<ModItemViewModel>;
#pragma warning restore CS0618
        var (container, after) = HitTest(list, e);
        ClearDropMarkers();
        if (items is null || items.Count == 0) return;

        if (list == ActiveList)
            Vm.MoveTo(items, container?.DataContext as ModItemViewModel, after);
        else
            Vm.Deactivate(items.Where(i => i.IsActive));
    }

    private void ClearDropMarkers()
    {
        AvailableList.Classes.Remove("dropTarget");
        ActiveList.Classes.Remove("dropTarget");
        if (_markedItem is null) return;
        _markedItem.Classes.Remove("dropAbove");
        _markedItem.Classes.Remove("dropBelow");
        _markedItem = null;
    }

    // ── Modpack files: drop, open, save ──

    private static string? DroppedModpack(DragEventArgs e)
    {
#pragma warning disable CS0618
        return e.Data.GetFiles()?.Select(f => f.TryGetLocalPath())
#pragma warning restore CS0618
            .FirstOrDefault(p => p is not null && p.EndsWith(RimForge.Core.Sharing.ModpackFile.Extension, StringComparison.OrdinalIgnoreCase));
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (DroppedModpack(e) is null) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DroppedModpack(e) is not { } path) return;
        e.Handled = true;
        await Vm.OpenModpackAsync(path);
    }

    private static readonly FilePickerFileType ModpackType = new("RimForge") { Patterns = ["*" + RimForge.Core.Sharing.ModpackFile.Extension] };

    private async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = RimForge.Core.Sharing.ModpackFile.Extension,
            FileTypeChoices = [ModpackType],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickModpackFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = [ModpackType] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    // ── Game folder picker ──

    private async Task<string?> PickFolderAsync(string title, string? start)
    {
        var options = new FolderPickerOpenOptions { AllowMultiple = false, Title = title };
        if (start is not null && Directory.Exists(start))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnPickGameDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await Vm.SetGameDirAsync(path);
    }
}
