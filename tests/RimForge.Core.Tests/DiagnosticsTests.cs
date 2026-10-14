using RimForge.Core.Diagnostics;
using RimForge.Core.Models;

namespace HarmonyLib
{
    // Stand-ins with Harmony's names: the scanner only looks at attribute names, so this test DLL
    // can play the part of a mod.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type) { }
        public HarmonyPatch(string method) { }
        public HarmonyPatch(Type type, string method) { }
    }
}

namespace FakeModCode
{
    public class GameThing
    {
        public void Tick() { }
    }

    [HarmonyLib.HarmonyPatch(typeof(GameThing), nameof(GameThing.Tick))]
    public static class Patch_GameThing_Tick
    {
        public static bool Prefix() => true;
        public static void Postfix() { }
    }
}

namespace RimForge.Core.Tests
{
    public sealed class DiagnosticsTests : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("rimforge-diag-").FullName;

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private ModInfo MakeMod(string id, string folder, Dictionary<string, string> files, bool withThisDll = false)
        {
            var root = Path.Combine(_dir, folder);
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            if (withThisDll)
            {
                Directory.CreateDirectory(Path.Combine(root, "Assemblies"));
                File.Copy(typeof(DiagnosticsTests).Assembly.Location, Path.Combine(root, "Assemblies", "FakeMod.dll"));
            }
            return new ModInfo { PackageId = id, DeclaredPackageId = id, Name = id, FolderPath = root, Source = ModSource.Local };
        }

        private const string GunDefs = """
            <Defs>
              <ThingDef Abstract="True" Name="BaseGun"><defName>Ignored</defName></ThingDef>
              <ThingDef ParentName="BaseGun"><defName>Gun_X</defName></ThingDef>
            </Defs>
            """;

        private const string ReplacePatch = """
            <Patch>
              <Operation Class="PatchOperationSequence">
                <operations>
                  <li Class="PatchOperationReplace">
                    <xpath>Defs/ThingDef[defName="Gun_X"]/statBases/MarketValue</xpath>
                    <value><MarketValue>5</MarketValue></value>
                  </li>
                </operations>
              </Operation>
            </Patch>
            """;

        private ModIndex BuildTwoMods()
        {
            var a = MakeMod("a.mod", "ModA", new() { ["1.6/Defs/Guns.xml"] = GunDefs, ["1.6/Patches/Price.xml"] = ReplacePatch });
            var b = MakeMod("b.mod", "ModB", new() { ["Defs/MoreGuns.xml"] = GunDefs, ["Patches/Cheaper.xml"] = ReplacePatch }, withThisDll: true);
            var installed = new Dictionary<string, ModInfo> { [a.PackageId] = a, [b.PackageId] = b };
            return ModIndex.Build(["a.mod", "b.mod"], installed, "1.6");
        }

        private const string TemplateDefs = """
            <Defs><ThingDef Name="Base_Android" Abstract="True"><label>x</label></ThingDef></Defs>
            """;

        private const string ChildDefs = """
            <Defs><ThingDef ParentName="Base_Android"><defName>Child_Gene</defName></ThingDef></Defs>
            """;

        private const string ParentLog = """
            XML error: Could not find parent node named "Base_Android" for node "ThingDef". Full node: <ThingDef ParentName="Base_Android"><defName>Child_Gene</defName></ThingDef>
            """;

        [Fact]
        public void MissingTemplate_InLaterActiveMod_IsFixedByLoadingAfterIt()
        {
            var child = MakeMod("child", "Child", new() { ["Defs/C.xml"] = ChildDefs });
            var parent = MakeMod("parent", "Parent", new() { ["Defs/P.xml"] = TemplateDefs });
            var installed = new Dictionary<string, ModInfo> { ["child"] = child, ["parent"] = parent };
            var index = ModIndex.Build(["child", "parent"], installed, "1.6");

            var finding = Assert.Single(LogAnalyzer.Analyze(ParentLog, index));
            var fixes = FixPlanner.ForLog(finding, index, null, installed, ["child", "parent"]);

            var move = Assert.Single(fixes, f => f.Kind == FixKind.LoadAfter);
            Assert.Equal(["child", "parent"], move.ModIds);
            Assert.True(move.IsSafe);
        }

        [Fact]
        public void MissingTemplate_InInstalledButOffMod_IsFixedByTurningItOn()
        {
            var child = MakeMod("child", "Child", new() { ["Defs/C.xml"] = ChildDefs });
            var parent = MakeMod("parent", "Parent", new() { ["Defs/P.xml"] = TemplateDefs }) with
            {
                Dependencies = [new ModDependency("lib", "Lib", null, null)],
            };
            var lib = MakeMod("lib", "Lib", new() { ["Defs/L.xml"] = "<Defs />" });
            var installed = new Dictionary<string, ModInfo> { ["child"] = child, ["parent"] = parent, ["lib"] = lib };
            var index = ModIndex.Build(["child"], installed, "1.6");
            var inactive = ModIndex.Build(["parent", "lib"], installed, "1.6");

            var finding = Assert.Single(LogAnalyzer.Analyze(ParentLog, index));
            var enable = Assert.Single(FixPlanner.ForLog(finding, index, inactive, installed, ["child"]), f => f.Kind == FixKind.EnableMods);

            Assert.Equal("parent", enable.Provider);
            Assert.Equal(["parent", "lib"], enable.ModIds); // its dependency comes along
        }

        [Fact]
        public void XpathTargets_AreExtracted()
        {
            Assert.Equal([("Gun_A", "statBases/MarketValue"), ("Gun_B", "statBases/MarketValue")],
                XmlScanner.Targets("""Defs/ThingDef[defName="Gun_A" or defName = 'Gun_B']/statBases/MarketValue""").ToList());
            Assert.Empty(XmlScanner.Targets("Defs/ThingDef/statBases"));
        }

        [Fact]
        public void FindsDefOverridesAndPatchClashes_BetweenMods()
        {
            var index = BuildTwoMods();

            var over = Assert.Single(ConflictAnalyzer.DefOverrides(index));
            Assert.Equal("Gun_X", over.DefName);
            Assert.Equal("b.mod", over.Winner);
            Assert.Equal(FindingSeverity.Medium, over.Severity);

            var clash = Assert.Single(ConflictAnalyzer.PatchClashes(index));
            Assert.Equal(("Gun_X", "statBases/MarketValue"), (clash.DefName, clash.Field));
            Assert.Equal(["a.mod", "b.mod"], clash.ModIds);
        }

        [Fact]
        public void ReadsHarmonyPatchesFromModCode()
        {
            var index = BuildTwoMods();

            var prefix = Assert.Single(index.HarmonyPatches, p => p.Kind == PatchKind.Prefix);
            Assert.Equal("FakeModCode.GameThing.Tick", prefix.Target);
            Assert.True(prefix.CanSkipOriginal);
            Assert.Contains(index.HarmonyPatches, p => p.Kind == PatchKind.Postfix && p.ModId == "b.mod");
        }

        [Fact]
        public void LogErrors_AreBlamedOnTheRightMods()
        {
            var index = BuildTwoMods();
            const string log = """
                Some harmless line
                XML error: Could not find parent node named "Missing_Base" for node "ThingDef". Full node: <ThingDef ParentName="Missing_Base"><defName>Gun_X</defName></ThingDef>

                Exception ticking Thing_1: System.NullReferenceException: Object reference not set to an instance of an object
                [Ref 1A2B3C4D]
                  at FakeModCode.Patch_GameThing_Tick.Postfix () [0x00000] in <abc>:0
                  at Verse.TickManager.DoSingleTick () [0x00000] in <abc>:0

                Exception ticking Thing_2: System.NullReferenceException: Object reference not set to an instance of an object
                  at FakeModCode.Patch_GameThing_Tick.Postfix () [0x00000] in <abc>:0

                Could not resolve cross-reference: No Verse.SoundDef named Gun_X found to give to Verse.Tool fist
                Fallback handler could not load library something.dll
                """;

            var findings = LogAnalyzer.Analyze(log, index);

            var crash = Assert.Single(findings, f => f.Kind == LogErrorKind.Exception);
            Assert.Equal(2, crash.Count); // repeats are grouped
            Assert.Equal(FindingSeverity.High, crash.Severity);
            Assert.Equal(new LogSuspect("b.mod", SuspectReason.Code), crash.Suspects[0]);

            var parent = Assert.Single(findings, f => f.Kind == LogErrorKind.MissingParent);
            Assert.Equal("Missing_Base", parent.MissingName);
            Assert.Contains(parent.Suspects, s => s.Reason == SuspectReason.Def);

            Assert.Single(findings, f => f.Kind == LogErrorKind.MissingReference);
            Assert.Equal(3, findings.Count); // the noise line is ignored
        }
    }
}
