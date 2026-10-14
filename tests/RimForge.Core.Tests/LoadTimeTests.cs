using RimForge.Core.Content;
using RimForge.Core.Launch;

namespace RimForge.Core.Tests;

public sealed class LoadTimeTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "rf-lt-" + Guid.NewGuid().ToString("N"));

    public LoadTimeTests() => Directory.CreateDirectory(Path.Combine(_config, "Config"));

    public void Dispose() => Directory.Delete(_config, recursive: true);

    [Fact]
    public void TurningTimingOn_KeepsTheModsOtherSettings()
    {
        var file = Path.Combine(_config, "Config", "Mod_3535481557_LoadingProgressMod.xml");
        File.WriteAllText(file, """
            <?xml version="1.0" encoding="utf-8"?>
            <SettingsBlock>
              <ModSettings Class="ilyvion.LoadingProgress.Settings">
                <lastLoadingModHash>-1280865705</lastLoadingModHash>
                <loadingTimes><li>120.5</li><li>148.109634</li></loadingTimes>
              </ModSettings>
            </SettingsBlock>
            """);
        var state = LoadTimes.ReadLoadingProgress(_config, "3535481557");
        Assert.Equal(148.109634, state.LastLoadSeconds);
        Assert.False(state.Ready);

        LoadTimes.EnableTracking(state, Path.Combine(_config, "backup"));

        var after = LoadTimes.ReadLoadingProgress(_config, "3535481557");
        Assert.True(after.Ready);
        Assert.Equal(148.109634, after.LastLoadSeconds);
        Assert.Contains("-1280865705", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(Path.Combine(_config, "backup")));
    }

    [Fact]
    public void Report_IsReadInSeconds_WhateverTheUnit()
    {
        File.WriteAllText(Path.Combine(_config, LoadTimes.ReportFile), """
            <StartupImpactSession>
              <loadingTime>100</loadingTime>
              <mods>
                <li><modName>Big</modName><modPackageId>Big.Mod</modPackageId><totalImpact>30000</totalImpact></li>
                <li><modName>Small</modName><modPackageId>small.mod</modPackageId><totalImpact>500</totalImpact></li>
              </mods>
            </StartupImpactSession>
            """);
        var report = LoadTimes.ReadReport(_config)!;
        Assert.Equal(100, report.TotalSeconds);
        Assert.Equal(30, report.Mods["big.mod"]); // 30 500 "seconds" in a 100 s startup can only be milliseconds
        Assert.Equal(0.5, report.Mods["small.mod"]);
    }

    [Fact]
    public void Estimate_UsesMeasuredTimes_AndScalesGuessesForNewMods()
    {
        var report = new StartupReport(100, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["a"] = 30, ["b"] = 10 }, DateTime.UtcNow);
        var profile = new ModProfile { Defs = 100, Textures = 100 };
        var estimate = LoadTimes.Estimate(["core", "a", "b", "new"], id => id == "core", _ => profile, report, null, null);

        Assert.Equal(LoadTimeSource.PerMod, estimate.Source);
        Assert.Equal(1, estimate.Estimated);
        Assert.Equal(60, estimate.BaseSeconds, 3); // 100 total − 40 of mods
        // Guesses for a and b were equal and came out at 20 s on average: the new mod gets 20 s too.
        Assert.Equal(20, estimate.Mods.Single(m => m.PackageId == "new").Seconds, 3);
        Assert.Equal(120, estimate.TotalSeconds, 3);
        Assert.Equal("2:00", LoadTimes.Clock(estimate.TotalSeconds));
    }
}
