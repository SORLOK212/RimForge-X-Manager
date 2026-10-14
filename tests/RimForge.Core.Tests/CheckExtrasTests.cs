using System.IO.Compression;
using System.Text;
using RimForge.Core.Checks;
using RimForge.Core.Databases;
using RimForge.Core.Models;
using RimForge.Core.Sorting;

namespace RimForge.Core.Tests;

public class CheckExtrasTests
{
    private static ModInfo Mod(string id, string[]? versions = null, string[]? deps = null, ulong? workshopId = null,
        ModSource source = ModSource.Workshop) => new()
    {
        PackageId = id,
        DeclaredPackageId = id.Replace("_steam", ""),
        Name = id,
        FolderPath = id,
        Source = source,
        WorkshopId = workshopId,
        SupportedVersions = versions ?? ["1.6"],
        Dependencies = (deps ?? []).Select(d => new ModDependency(d, d, null, null)).ToList(),
    };

    private static Dictionary<string, ModInfo> Installed(params ModInfo[] mods) => mods.ToDictionary(m => m.PackageId);

    private static byte[] Gzip(string json)
    {
        var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest)) gz.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    private const string ReplacementsJson = """
        {"version":"2026-10-13T12:18:57Z","rules":[
          {"oldWorkshopId":"100","oldName":"Old Thing","oldAuthor":"Gone","oldPackageId":"Gone.Thing",
           "newWorkshopId":"200","newName":"Old Thing (Continued)","newAuthor":"Mlie","newPackageId":"Mlie.Thing",
           "oldVersions":["1.4"],"newVersions":["1.5","1.6"]},
          {"oldWorkshopId":"300","oldName":"Future","newWorkshopId":"400","newName":"Future 2","newVersions":["1.7"]}
        ]}
        """;

    [Fact]
    public void Replacements_AreReadFromTheGzippedDatabase()
    {
        var db = ModReplacements.Parse(Gzip(ReplacementsJson));

        Assert.Equal(2, db.Count);
        Assert.Equal(new DateTime(2026, 10, 13, 12, 18, 57, DateTimeKind.Utc), db.UpdatedUtc);
        var r = db.For(100)!;
        Assert.Equal("mlie.thing", r.NewPackageId);
        Assert.Equal(200UL, r.NewWorkshopId);
        Assert.Null(db.For(999));
        Assert.Throws<InvalidDataException>(() => ModReplacements.Parse(Gzip("{\"version\":\"x\"}")));
    }

    [Fact]
    public void AbandonedMod_GetsItsReplacement_OnlyIfTheReplacementFitsThisGame()
    {
        var extras = new CheckExtras { Replacements = ModReplacements.Parse(Gzip(ReplacementsJson)) };
        var installed = Installed(Mod("gone.thing", versions: ["1.4"], workshopId: 100), Mod("future", workshopId: 300));

        var issues = ModListChecker.Check(["gone.thing", "future"], installed, "1.6", extras: extras);

        var use = Assert.Single(issues, i => i.Kind == IssueKind.UseInstead);
        Assert.Equal("gone.thing", use.ModId);
        Assert.Equal(IssueSeverity.Warning, use.Severity); // the old one does not support 1.6
        Assert.Equal("Old Thing (Continued)", use.Replacement!.NewName);
        // "Future 2" is only for 1.7: no advice on 1.6.
        Assert.DoesNotContain(issues, i => i.ModId == "future");
        // The replacement says it all; no separate "unsupported version" line.
        Assert.DoesNotContain(issues, i => i.Kind == IssueKind.UnsupportedVersion && i.ModId == "gone.thing");
    }

    [Fact]
    public void VerifiedMods_SilenceTheVersionWarning()
    {
        var xml = "﻿<?xml version=\"1.0\" encoding=\"utf-8\"?><ModIdsToFix><!-- c --><li>Old.Works</li><li> </li></ModIdsToFix>"u8.ToArray();
        var verified = VerifiedMods.Parse(xml, "1.6");
        Assert.Equal(1, verified.Count);

        var installed = Installed(Mod("old.works", versions: ["1.5"]), Mod("old.unknown", versions: ["1.5"]));
        var issues = ModListChecker.Check(["old.works", "old.unknown"], installed, "1.6", extras: new CheckExtras { Verified = verified });

        Assert.DoesNotContain(issues, i => i.ModId == "old.works");
        Assert.Contains(issues, i => i.ModId == "old.unknown" && i.Kind == IssueKind.UnsupportedVersion);
        Assert.Equal(0, VerifiedMods.Parse([], "1.7").Count); // nothing published for this version yet
    }

    [Fact]
    public void ModForThePreviousVersion_WithoutCode_IsMostLikelyFine()
    {
        var installed = Installed(Mod("xml.only", versions: ["1.6"]), Mod("with.code", versions: ["1.6"]), Mod("ancient", versions: ["1.4"]));
        var extras = new CheckExtras { HasCode = m => m.PackageId == "with.code" };

        var issues = ModListChecker.Check(["xml.only", "with.code", "ancient"], installed, "1.7", extras: extras);

        Assert.Equal(IssueKind.OlderVersionLikelyOk, Assert.Single(issues, i => i.ModId == "xml.only").Kind);
        Assert.Equal(IssueSeverity.Info, Assert.Single(issues, i => i.ModId == "xml.only").Severity);
        Assert.Equal(IssueKind.UnsupportedVersion, Assert.Single(issues, i => i.ModId == "with.code").Kind);
        Assert.Equal(IssueKind.UnsupportedVersion, Assert.Single(issues, i => i.ModId == "ancient").Kind);
        Assert.Equal("1.6", ModListChecker.PreviousVersion("1.7"));
        Assert.Null(ModListChecker.PreviousVersion("1.0"));
    }

    [Fact]
    public void Duplicates_AndMissingExpansions_AreReported()
    {
        var installed = Installed(Mod("a", source: ModSource.Local), Mod("a_steam", workshopId: 5), Mod("b"),
            Mod("needs.biotech", deps: ["ludeon.rimworld.biotech"]));
        var extras = new CheckExtras { DuplicateFolders = new Dictionary<string, IReadOnlyList<string>> { ["b"] = ["B (1)", "B (2)"] } };

        var issues = ModListChecker.Check(["a", "a_steam", "b", "needs.biotech"], installed, "1.6", extras: extras);

        Assert.Contains(issues, i => i.Kind == IssueKind.DuplicateActive && i.ModId == "a_steam" && i.OtherId == "a");
        Assert.Contains(issues, i => i.Kind == IssueKind.DuplicateInstalled && i.ModId == "b" && i.Detail == "B (1), B (2)");
        var dlc = Assert.Single(issues, i => i.ModId == "needs.biotech");
        Assert.Equal(IssueKind.DlcMissing, dlc.Kind);
        Assert.Equal("ludeon.rimworld.biotech", dlc.OtherId);
    }

    [Fact]
    public void Pins_PutModsAtTheTopOrBottom_AndBeatTheCommunity()
    {
        var installed = Installed(Mod(KnownMods.Core, versions: [], source: ModSource.Official), Mod("patch"), Mod("x"), Mod("y"), Mod("ui"));
        var order = new[] { KnownMods.Core, "patch", "x", "ui", "y" };
        var community = CommunityRules.Parse("""{"rules":{"ui":{"loadBottom":{"value":true}}}}""");
        var pins = new UserPins(Top: ["ui"], Bottom: ["patch"]);

        var rules = RuleSet.Build(order, installed, community, pins: pins);
        var sorted = ModSorter.Sort(order, rules);

        Assert.Equal([KnownMods.Core, "ui", "x", "y", "patch"], sorted.Order);
        Assert.Contains("ui", rules.PinnedByUser);
        Assert.False(rules.Bottom.ContainsKey("ui"));
    }
}
