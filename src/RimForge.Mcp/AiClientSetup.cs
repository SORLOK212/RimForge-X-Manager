using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimForge.Mcp;

/// <summary>How an AI app keeps its list of MCP servers.</summary>
public enum AiConfigFormat
{
    /// <summary><c>{"mcpServers": {"rimforge": {"command", "args"}}}</c> — Claude Desktop, Cursor, Gemini CLI, Windsurf, LM Studio.</summary>
    McpServers,
    /// <summary>Claude Code's <c>~/.claude.json</c>: the same, with <c>"type": "stdio"</c>.</summary>
    ClaudeCode,
    /// <summary>VS Code's <c>mcp.json</c>: <c>{"servers": {"rimforge": {"type": "stdio", …}}}</c>.</summary>
    VsCode,
    /// <summary>Codex's <c>config.toml</c>: a <c>[mcp_servers.rimforge]</c> table.</summary>
    CodexToml,
}

public enum AiClientStatus
{
    /// <summary>The app does not seem to be installed on this PC.</summary>
    NotFound,
    NotConnected,
    Connected,
    /// <summary>Connected, but to another RimForge.exe (moved or a second copy).</summary>
    OtherCopy,
}

/// <param name="DetectPath">A folder or file that exists when the app is installed.</param>
public sealed record AiClient(string Id, string Name, string ConfigPath, string DetectPath, AiConfigFormat Format);

/// <summary>
/// Connects AI apps to RimForge with one button: writes RimForge into the app's own MCP settings file (a backup of
/// the file is kept next to it) or takes it out again. Other servers and settings in the file stay as they are.
/// </summary>
public static class AiClientSetup
{
    public const string ServerName = "rimforge";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// The AI apps RimForge knows. <paramref name="home"/> and <paramref name="appData"/> replace the user's folders
    /// (tests; the RIMFORGE_HOME variable does the same for a test run of the window).
    /// </summary>
    public static IReadOnlyList<AiClient> Known(string? home = null, string? appData = null, string? localAppData = null)
    {
        var testHome = Environment.GetEnvironmentVariable("RIMFORGE_HOME") is { Length: > 0 } t ? t : null;
        home ??= testHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        appData ??= testHome is not null ? Path.Combine(testHome, "AppData", "Roaming") : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        localAppData ??= testHome is not null ? Path.Combine(testHome, "AppData", "Local") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Claude Desktop from the Microsoft Store keeps its settings inside its package folder.
        var claudeDir = Path.Combine(appData, "Claude");
        if (!Directory.Exists(claudeDir) && Directory.Exists(Path.Combine(localAppData, "Packages"))
            && Directory.EnumerateDirectories(Path.Combine(localAppData, "Packages"), "Claude_*").FirstOrDefault() is { } package)
            claudeDir = Path.Combine(package, "LocalCache", "Roaming", "Claude");

        return
        [
            new("claude-desktop", "Claude Desktop", Path.Combine(claudeDir, "claude_desktop_config.json"), claudeDir, AiConfigFormat.McpServers),
            new("claude-code", "Claude Code", Path.Combine(home, ".claude.json"), Path.Combine(home, ".claude"), AiConfigFormat.ClaudeCode),
            new("codex", "OpenAI Codex", Path.Combine(home, ".codex", "config.toml"), Path.Combine(home, ".codex"), AiConfigFormat.CodexToml),
            new("gemini", "Gemini CLI", Path.Combine(home, ".gemini", "settings.json"), Path.Combine(home, ".gemini"), AiConfigFormat.McpServers),
            new("cursor", "Cursor", Path.Combine(home, ".cursor", "mcp.json"), Path.Combine(home, ".cursor"), AiConfigFormat.McpServers),
            new("vscode", "VS Code (Copilot)", Path.Combine(appData, "Code", "User", "mcp.json"), Path.Combine(appData, "Code", "User"), AiConfigFormat.VsCode),
            new("windsurf", "Windsurf", Path.Combine(home, ".codeium", "windsurf", "mcp_config.json"), Path.Combine(home, ".codeium", "windsurf"), AiConfigFormat.McpServers),
            new("lmstudio", "LM Studio", Path.Combine(home, ".lmstudio", "mcp.json"), Path.Combine(home, ".lmstudio"), AiConfigFormat.McpServers),
        ];
    }

    public static bool IsInstalled(AiClient client) => Directory.Exists(client.DetectPath) || File.Exists(client.ConfigPath);

    public static AiClientStatus Status(AiClient client, string exe)
    {
        if (!IsInstalled(client)) return AiClientStatus.NotFound;
        if (!File.Exists(client.ConfigPath)) return AiClientStatus.NotConnected;
        try
        {
            var command = client.Format == AiConfigFormat.CodexToml ? TomlCommand(File.ReadAllText(client.ConfigPath)) : JsonCommand(client);
            return command is null ? AiClientStatus.NotConnected
                : SamePath(command, exe) ? AiClientStatus.Connected
                : AiClientStatus.OtherCopy;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return AiClientStatus.NotConnected;
        }
    }

    /// <summary>Writes RimForge into the app's settings. Returns the backup file, if the settings file existed.</summary>
    public static string? Connect(AiClient client, string exe)
    {
        var backup = BackUp(client);
        if (client.Format == AiConfigFormat.CodexToml)
        {
            var text = File.Exists(client.ConfigPath) ? File.ReadAllText(client.ConfigPath) : "";
            WriteText(client.ConfigPath, RemoveTomlTable(text).TrimEnd() + (text.Trim().Length > 0 ? "\n\n" : "") + TomlTable(exe) + "\n");
            return backup;
        }

        var root = ReadJson(client);
        var key = client.Format == AiConfigFormat.VsCode ? "servers" : "mcpServers";
        if (root[key] is not JsonObject servers)
            root[key] = servers = new JsonObject();
        servers[ServerName] = Entry(client.Format, exe);
        WriteText(client.ConfigPath, root.ToJsonString(Indented));
        return backup;
    }

    /// <summary>Takes RimForge out of the app's settings.</summary>
    public static void Disconnect(AiClient client)
    {
        if (!File.Exists(client.ConfigPath)) return;
        BackUp(client);
        if (client.Format == AiConfigFormat.CodexToml)
        {
            WriteText(client.ConfigPath, RemoveTomlTable(File.ReadAllText(client.ConfigPath)).TrimEnd() + "\n");
            return;
        }
        var root = ReadJson(client);
        var key = client.Format == AiConfigFormat.VsCode ? "servers" : "mcpServers";
        if (root[key] is JsonObject servers && servers.Remove(ServerName))
            WriteText(client.ConfigPath, root.ToJsonString(Indented));
    }

    /// <summary>The settings to paste by hand, for apps RimForge can't set up itself.</summary>
    public static string Snippet(AiConfigFormat format, string exe) => format switch
    {
        AiConfigFormat.CodexToml => TomlTable(exe),
        _ => new JsonObject
        {
            [format == AiConfigFormat.VsCode ? "servers" : "mcpServers"] = new JsonObject { [ServerName] = Entry(format, exe) },
        }.ToJsonString(Indented),
    };

    private static JsonObject Entry(AiConfigFormat format, string exe)
    {
        var entry = new JsonObject();
        if (format is AiConfigFormat.VsCode or AiConfigFormat.ClaudeCode) entry["type"] = "stdio";
        entry["command"] = exe;
        entry["args"] = new JsonArray("--mcp");
        return entry;
    }

    private static string TomlTable(string exe) =>
        // A literal string ('…') needs no escaping of the backslashes in a Windows path.
        $"[mcp_servers.{ServerName}]\ncommand = '{exe}'\nargs = [\"--mcp\"]";

    private static readonly Regex TomlHeader = new(@"^\s*\[\[?\s*([^\]]+?)\s*\]\]?\s*(#.*)?$");

    /// <summary>Removes <c>[mcp_servers.rimforge]</c> and its sub-tables, keeping everything else.</summary>
    internal static string RemoveTomlTable(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in lines)
        {
            if (TomlHeader.Match(line) is { Success: true } header)
            {
                var name = header.Groups[1].Value.Replace("\"", "").Replace(" ", "");
                skipping = name == $"mcp_servers.{ServerName}" || name.StartsWith($"mcp_servers.{ServerName}.");
            }
            if (!skipping) kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    internal static string? TomlCommand(string text)
    {
        var inTable = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (TomlHeader.Match(line) is { Success: true } header)
            {
                inTable = header.Groups[1].Value.Replace("\"", "").Replace(" ", "") == $"mcp_servers.{ServerName}";
                continue;
            }
            if (inTable && Regex.Match(line, @"^\s*command\s*=\s*(?:'([^']*)'|""((?:[^""\\]|\\.)*)"")") is { Success: true } m)
                return m.Groups[1].Success ? m.Groups[1].Value : Regex.Unescape(m.Groups[2].Value);
        }
        return null;
    }

    private static string? JsonCommand(AiClient client)
    {
        var root = ReadJson(client);
        var key = client.Format == AiConfigFormat.VsCode ? "servers" : "mcpServers";
        return root[key]?[ServerName]?["command"]?.GetValue<string>();
    }

    private static JsonObject ReadJson(AiClient client)
    {
        if (!File.Exists(client.ConfigPath)) return new JsonObject();
        var text = File.ReadAllText(client.ConfigPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: Lenient) as JsonObject
               ?? throw new JsonException($"{client.ConfigPath} is not a JSON object.");
    }

    private static string? BackUp(AiClient client)
    {
        if (!File.Exists(client.ConfigPath)) return null;
        var backup = client.ConfigPath + ".rimforge-backup";
        File.Copy(client.ConfigPath, backup, overwrite: true);
        return backup;
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".rimforge-tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
