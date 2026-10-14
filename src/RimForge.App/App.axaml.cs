using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RimForge.App.Services;
using RimForge.App.ViewModels;
using RimForge.App.Views;
using RimForge.Core.Storage;

namespace RimForge.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load();
        Loc.Apply(settings.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(settings);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) => vm.Shutdown();
            _ = StartAsync(vm, desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Loads the mods; a modpack file given on the command line (double-click on it) opens right after.</summary>
    private static async Task StartAsync(MainWindowViewModel vm, string[] args)
    {
        await vm.LoadAsync();
        vm.StartAiBridge();
        vm.ResumeUnfinishedDownloads();
        _ = vm.CheckForUpdateAsync();
#if DEBUG
        if (Environment.GetEnvironmentVariable("RIMFORGE_DEMO_DOWNLOADS") == "1") vm.DemoDownloads();
#endif
        if (args.FirstOrDefault(a => a.EndsWith(RimForge.Core.Sharing.ModpackFile.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a)) is { } pack)
            await vm.OpenModpackAsync(pack);
    }
}
