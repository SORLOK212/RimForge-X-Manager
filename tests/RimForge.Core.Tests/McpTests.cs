using System.Text.Json;
using System.Text.Json.Nodes;
using RimForge.Core.Models;
using RimForge.Core.Paths;
using RimForge.Core.Profiles;
using RimForge.Core.Storage;
using RimForge.Mcp;

namespace RimForge.Core.Tests;

public class McpTests
{
    private static ModInfo Mod(string id, string[]? deps = null, ModSource source = ModSource.Workshop, ulong? workshopId = null) => new()
    {
        PackageId = id, DeclaredPackageId = id, Name = id == KnownMods.Core ? "Core" : "Mod " + id, FolderPath = id, Source = source,
        WorkshopId = workshopId, SupportedVersions = ["1.6"],
        Dependencies = (deps ?? []).Select(d => new ModDependency(d, d, null, null)).ToList(),
    };

    private sealed class FakeHost(RimForgeState state) : IRimForgeHost
    {
        public List<WriteRequest> Writes { get; } = [];
        public Task<RimForgeState> GetStateAsync(CancellationToken ct) => Task.FromResult(state);

        public Task<WriteResult> WriteAsync(WriteRequest request, CancellationToken ct)
        {
            Writes.Add(request);
            return Task.FromResult(new WriteResult(true, "done"));
        }
    }

    private static RimForgeState State(string access = AiAccessModes.Ask)
    {
        var installed = new[]
        {
            Mod(KnownMods.Core, source: ModSource.Official), Mod("lib"), Mod("x", deps: ["lib"], workshopId: 42), Mod("off"),
        }.ToDictionary(m => m.PackageId);
        var profile = new Profile { Name = "Main" };
        profile.SetMods([KnownMods.Core, "x"], installed);
        return new RimForgeState
        {
            Paths = new GamePaths(), GameVersion = "1.6", FullGameVersion = "1.6.4871 rev590",
            Installed = installed, Active = [KnownMods.Core, "x"], Profiles = [profile], ActiveProfileId = profile.Id, Access = access,
        };
    }

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    [Fact]
    public async Task ReadingTools_DescribeTheModList()
    {
        var tools = new RimForgeTools(new FakeHost(State()));

        var list = await tools.CallAsync("list_mods", Args(new { }), null, default);
        Assert.False(list.IsError);
        Assert.Contains("2. Mod x [x] workshop 42", list.Text);
        Assert.Contains("⚠", list.Text); // x needs lib, which is off

        var check = await tools.CallAsync("check_modlist", Args(new { }), null, default);
        Assert.Contains("Mod x [x] needs Mod lib [lib], which is installed but turned off.", check.Text);

        var mod = await tools.CallAsync("get_mod", Args(new { id = "42" }), null, default);
        Assert.Contains("Mod x [x]", mod.Text);
        Assert.Contains("installed but OFF", mod.Text);
    }

    [Fact]
    public async Task Changes_AreCheckedFirst_ThenHandedToTheWindow()
    {
        var host = new FakeHost(State());
        var tools = new RimForgeTools(host);

        var bad = await tools.CallAsync("enable_mods", Args(new { ids = new[] { "lib", "nope" } }), "Claude", default);
        Assert.True(bad.IsError);
        Assert.Contains("Not installed: nope", bad.Text);
        Assert.Empty(host.Writes);

        var core = await tools.CallAsync("disable_mods", Args(new { ids = new[] { KnownMods.Core } }), "Claude", default);
        Assert.True(core.IsError);

        var ok = await tools.CallAsync("enable_mods", Args(new { ids = new[] { "LIB" } }), "Claude", default);
        Assert.False(ok.IsError);
        var write = Assert.Single(host.Writes);
        Assert.Equal(WriteActions.Enable, write.Action);
        Assert.Equal(["lib"], write.Ids);
        Assert.Equal("Claude", write.Client);

        await tools.CallAsync("create_modpack", Args(new { name = "Fantasy", ids = new[] { "x", "workshop:777" } }), "Codex", default);
        var pack = host.Writes[^1];
        Assert.Equal([KnownMods.Core, "x", "workshop:777"], pack.Ids);
        Assert.True(pack.Flag);
    }

    [Fact]
    public async Task ReadOnlyAccess_RefusesChanges()
    {
        var host = new FakeHost(State(AiAccessModes.Read));
        var result = await new RimForgeTools(host).CallAsync("sort_mods", Args(new { }), null, default);

        Assert.True(result.IsError);
        Assert.Empty(host.Writes);
    }

    [Fact]
    public async Task Server_SpeaksMcpOverJsonRpc()
    {
        var server = new McpServer(TextReader.Null, TextWriter.Null);

        var init = await server.ReplyAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"Claude Desktop","version":"1"}}}
            """), default);
        Assert.Equal("2025-06-18", (string?)init!["result"]!["protocolVersion"]);
        Assert.Equal("rimforge", (string?)init["result"]!["serverInfo"]!["name"]);

        var future = await server.ReplyAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}"""), default);
        Assert.Equal("2025-11-25", (string?)future!["result"]!["protocolVersion"]);

        var list = await server.ReplyAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":"a","method":"tools/list"}"""), default);
        var tools = list!["result"]!["tools"]!.AsArray();
        Assert.Contains(tools, t => (string?)t!["name"] == "get_overview" && (bool)t["annotations"]!["readOnlyHint"]!);
        Assert.Contains(tools, t => (string?)t!["name"] == "disable_mods" && !(bool)t["annotations"]!["readOnlyHint"]!);
        Assert.Equal("a", (string?)list["id"]);

        Assert.Null(await server.ReplyAsync(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), default));
        var unknown = await server.ReplyAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"nope"}"""), default);
        Assert.Equal(-32601, (int)unknown!["error"]!["code"]!);
    }
}
