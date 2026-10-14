using System.Text.Json.Nodes;
using RimForge.Mcp;

namespace RimForge.Core.Tests;

public sealed class AiClientSetupTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "rf-ai-" + Guid.NewGuid().ToString("N"));
    private const string Exe = @"C:\Games\RimForge\RimForge.exe";

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private AiClient Client(string id) =>
        AiClientSetup.Known(_home, Path.Combine(_home, "Roaming"), Path.Combine(_home, "Local")).Single(c => c.Id == id);

    [Fact]
    public void AppsThatAreNotInstalled_AreNotFound()
    {
        Assert.All(AiClientSetup.Known(_home, Path.Combine(_home, "Roaming"), Path.Combine(_home, "Local")),
            c => Assert.Equal(AiClientStatus.NotFound, AiClientSetup.Status(c, Exe)));
    }

    [Fact]
    public void ClaudeDesktop_KeepsItsOtherSettings()
    {
        var claude = Client("claude-desktop");
        Directory.CreateDirectory(claude.DetectPath);
        File.WriteAllText(claude.ConfigPath, """
            {
              // comments are allowed by some apps
              "globalShortcut": "Ctrl+Space",
              "mcpServers": { "files": { "command": "npx", "args": ["fs"] } },
            }
            """);
        Assert.Equal(AiClientStatus.NotConnected, AiClientSetup.Status(claude, Exe));

        var backup = AiClientSetup.Connect(claude, Exe);

        Assert.Equal(AiClientStatus.Connected, AiClientSetup.Status(claude, Exe));
        Assert.True(File.Exists(backup));
        var root = JsonNode.Parse(File.ReadAllText(claude.ConfigPath))!;
        Assert.Equal("Ctrl+Space", (string?)root["globalShortcut"]);
        Assert.Equal("npx", (string?)root["mcpServers"]!["files"]!["command"]);
        Assert.Equal(Exe, (string?)root["mcpServers"]!["rimforge"]!["command"]);
        Assert.Equal("--mcp", (string?)root["mcpServers"]!["rimforge"]!["args"]![0]);

        // RimForge moved: the old entry points elsewhere.
        Assert.Equal(AiClientStatus.OtherCopy, AiClientSetup.Status(claude, @"D:\New\RimForge.exe"));

        AiClientSetup.Disconnect(claude);
        Assert.Equal(AiClientStatus.NotConnected, AiClientSetup.Status(claude, Exe));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(claude.ConfigPath))!["mcpServers"]!["files"]);
    }

    [Fact]
    public void ClaudeCode_AndVsCode_UseTheirOwnShape()
    {
        var code = Client("claude-code");
        Directory.CreateDirectory(code.DetectPath);
        AiClientSetup.Connect(code, Exe); // no file yet: it is created
        Assert.Equal("stdio", (string?)JsonNode.Parse(File.ReadAllText(code.ConfigPath))!["mcpServers"]!["rimforge"]!["type"]);

        var vscode = Client("vscode");
        Directory.CreateDirectory(vscode.DetectPath);
        AiClientSetup.Connect(vscode, Exe);
        var root = JsonNode.Parse(File.ReadAllText(vscode.ConfigPath))!;
        Assert.Equal(Exe, (string?)root["servers"]!["rimforge"]!["command"]);
        Assert.Null(root["mcpServers"]);
    }

    [Fact]
    public void Codex_TomlTable_IsAddedReplacedAndRemoved()
    {
        var codex = Client("codex");
        Directory.CreateDirectory(codex.DetectPath);
        File.WriteAllText(codex.ConfigPath, "model = \"gpt-5\"\n\n[mcp_servers.rimforge]\ncommand = 'C:\\Old\\RimForge.exe'\nargs = [\"--mcp\"]\n\n[mcp_servers.rimforge.env]\nX = \"1\"\n\n[mcp_servers.other]\ncommand = \"other\"\n");
        Assert.Equal(AiClientStatus.OtherCopy, AiClientSetup.Status(codex, Exe));

        AiClientSetup.Connect(codex, Exe);

        var text = File.ReadAllText(codex.ConfigPath);
        Assert.Equal(AiClientStatus.Connected, AiClientSetup.Status(codex, Exe));
        Assert.Contains("model = \"gpt-5\"", text);
        Assert.Contains("[mcp_servers.other]", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"\[mcp_servers\.rimforge\]"));
        Assert.DoesNotContain("C:\\Old", text);
        Assert.DoesNotContain("X = \"1\"", text);

        AiClientSetup.Disconnect(codex);
        Assert.Equal(AiClientStatus.NotConnected, AiClientSetup.Status(codex, Exe));
        Assert.Contains("[mcp_servers.other]", File.ReadAllText(codex.ConfigPath));
    }
}
