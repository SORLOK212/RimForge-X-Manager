using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using RimForge.App.Services;
using RimForge.Core.Storage;
using RimForge.Core.Updates;

namespace RimForge.App.ViewModels;

/// <summary>
/// RimForge keeps itself up to date: a while after start it reads the release feed; a newer build is offered,
/// downloaded on a yes, checked against its SHA-256 and swapped in, then RimForge starts again (or at exit).
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Where releases are announced: latest.json of the newest GitHub release (RIMFORGE_UPDATE_FEED or the settings can point elsewhere).</summary>
    private const string DefaultUpdateFeed = "https://github.com/SORLOK212/RimForge-X-Manager/releases/latest/download/latest.json";

    /// <summary>The uninstall entry the installer (Inno Setup) writes; its version is kept current after a self-update.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3C2A1E-8B4D-4E7A-9C5F-2D1B0A9E7F41}_is1";

    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";

    private string? _updateReady;

    public static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    private string? UpdateFeed =>
        Environment.GetEnvironmentVariable("RIMFORGE_UPDATE_FEED") is { Length: > 0 } env ? env : _settings.UpdateFeed ?? DefaultUpdateFeed;

    /// <summary>The running exe, when it is the single-file build (a development build is a folder of files and isn't swapped).</summary>
    private static string? InstalledExe =>
        Environment.ProcessPath is { } exe && Path.GetFileName(exe).Equals("RimForge.exe", StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "RimForge.dll")) ? exe : null;

    public async Task CheckForUpdateAsync()
    {
        if (InstalledExe is { } exe)
        {
            SelfUpdate.CleanUp(exe);
            SyncInstalledVersion(exe);
        }
        if (UpdateFeed is not { } feed) return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        UpdateInfo? info;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            info = await SelfUpdate.CheckAsync(http, feed, CurrentVersion);
        }
        catch (Exception)
        {
            return; // no connection, no feed yet: try again next start
        }
        if (info is null || info.Version == _settings.UpdateSkipVersion) return;

        var answer = await AskAsync(new DialogViewModel
        {
            Title = Loc.Format("Upd.Title", info.Version),
            Text = Loc.Format("Upd.Text", CurrentVersion.ToString(3), info.Version)
                   + (string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n\n" + Loc.Get("Upd.Notes") + "\n" + info.Notes.Trim()),
            PrimaryText = Loc.Get("Upd.Install"),
            SecondaryText = Loc.Get("Upd.Skip"),
        });
        if (answer == "secondary")
        {
            _settings.UpdateSkipVersion = info.Version;
            _settings.Save();
        }
        if (answer == "primary") await DownloadUpdateAsync(info);
    }

    private async Task DownloadUpdateAsync(UpdateInfo info)
    {
        UpdateStatus = Loc.Format("Upd.Downloading", 0);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var progress = new Progress<double>(p => UpdateStatus = Loc.Format("Upd.Downloading", (int)(p * 100)));
            var file = await SelfUpdate.DownloadAsync(http, info, Path.Combine(AppDataPaths.Root, "update"), progress);
            UpdateStatus = "";

            if (InstalledExe is null)
            {
                ShowToast(Loc.Format("Upd.NotInstalled", file), 15);
                return;
            }
            _updateReady = file;
            var answer = await AskAsync(new DialogViewModel
            {
                Title = Loc.Format("Upd.ReadyTitle", info.Version),
                Text = Loc.Get("Upd.ReadyText"),
                PrimaryText = Loc.Get("Upd.Restart"),
                SecondaryText = Loc.Get("Upd.Later"),
            });
            if (answer == "primary") RestartWithUpdate();
            else ShowToast(Loc.Get("Upd.AtExit"), 8);
        }
        catch (Exception ex)
        {
            UpdateStatus = "";
            Message = Loc.Format("Upd.Failed", ex.Message);
        }
    }

    /// <summary>
    /// RimForge updates itself by swapping its exe, so "Apps and features" would keep showing the version the installer
    /// put in; the entry is brought up to date (only when this exe is the installed one).
    /// </summary>
    private static void SyncInstalledVersion(string exe)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location
                || !string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetDirectoryName(Path.GetFullPath(exe))!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return;
            var version = CurrentVersion.ToString(3);
            if (key.GetValue("DisplayVersion") as string != version) key.SetValue("DisplayVersion", version);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>Saves the list, swaps the exe and starts the new one; this one closes.</summary>
    private void RestartWithUpdate()
    {
        if (_updateReady is not { } file || InstalledExe is not { } exe) return;
        if (IsDirty && !SaveNow()) return;
        try
        {
            SelfUpdate.Install(file, exe);
            _updateReady = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Loc.Format("Upd.Failed", ex.Message);
            return;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>An update downloaded but not installed yet goes in when RimForge closes.</summary>
    private void InstallUpdateAtExit()
    {
        if (_updateReady is not { } file || InstalledExe is not { } exe) return;
        try
        {
            SelfUpdate.Install(file, exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
