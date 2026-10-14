using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimForge.Core.Storage;

namespace RimForge.Mcp;

/// <summary>
/// The MCP server: <c>RimForge.exe --mcp</c>, started by an AI app (Claude, Codex, Cursor, Gemini CLI, VS Code…).
/// Speaks JSON-RPC 2.0 over stdin/stdout, one message per line (the MCP stdio transport). When the RimForge window
/// is open, tool calls run there (the AI sees what the player sees); otherwise reading works from the files and
/// changes open the window, which asks the player.
/// </summary>
public sealed class McpServer(TextReader input, TextWriter output)
{
    private static readonly string[] KnownProtocols = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private const string Instructions =
        "RimForge manages RimWorld mods for the player. Start with get_overview. Mods are identified by package id " +
        "(list_mods shows them). Before changing anything, check what the player wants; changes may need their OK " +
        "in the RimForge window. To build a modpack: search_workshop → create_modpack with installed ids and " +
        "'workshop:<id>' for new mods → check_modlist → sort_mods.";

    private readonly FileStateSource _files = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string? _client;

    public static async Task<int> RunAsync()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await new McpServer(stdin, stdout).ServeAsync(CancellationToken.None);
        return 0;
    }

    public async Task ServeAsync(CancellationToken ct)
    {
        var running = new List<Task>();
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                await SendAsync(Error(null, -32700, "Parse error"));
                continue;
            }
            // Requests run side by side: one may wait for the player while others (ping, reading) go on.
            running.Add(Task.Run(() => HandleAsync(message, ct), ct));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
    }

    private async Task HandleAsync(JsonNode? message, CancellationToken ct)
    {
        if (message is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
                if (await ReplyAsync(item, ct) is { } r) replies.Add(r);
            if (replies.Count > 0) await SendAsync(replies);
            return;
        }
        if (await ReplyAsync(message, ct) is { } reply) await SendAsync(reply);
    }

    /// <summary>The answer to one request; null for notifications.</summary>
    internal async Task<JsonNode?> ReplyAsync(JsonNode? message, CancellationToken ct)
    {
        if (message is not JsonObject obj) return null;
        if (obj["method"]?.GetValue<string>() is not { } method)
            return obj["id"] is { } badId ? Error(badId, -32600, "Invalid request") : null;
        var id = obj["id"]?.DeepClone();
        if (id is null) return null; // notifications (initialized, cancelled…) need no answer
        var parameters = obj["params"] as JsonObject;

        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(parameters)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray(RimForgeTools.All.Select(t => (JsonNode)t.ToJson()).ToArray()) }),
                "tools/call" => Result(id, await CallToolAsync(parameters, ct)),
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        _client = parameters?["clientInfo"]?["name"]?.GetValue<string>();
        var asked = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && KnownProtocols.Contains(asked) ? asked : KnownProtocols[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "rimforge",
                ["title"] = "RimForge X Manager",
                ["title"] = "RimForge — RimWorld mod manager",
                ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            },
            ["instructions"] = Instructions,
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        var args = parameters?["arguments"] is JsonObject a ? JsonSerializer.SerializeToElement(a) : JsonSerializer.SerializeToElement(new JsonObject());
        var result = await RunToolAsync(name, args, ct);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
            ["isError"] = result.IsError,
        };
    }

    private async Task<ToolResult> RunToolAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!AppSettings.Load().AiEnabled)
            return new ToolResult("The player turned AI assistants off in RimForge (Settings → AI assistant).", true);

        // The window is open: run the tool there, on what the player sees.
        var reply = await AiBridge.SendAsync(new BridgeMessage { Kind = "call", Tool = name, Arguments = args, Client = _client }, ct);
        if (reply is not null) return new ToolResult(reply.Text, reply.IsError);

        return await new RimForgeTools(new FileHost(this)).CallAsync(name, args, _client, ct);
    }

    /// <summary>Reads the files; changes start the window and hand the change to it.</summary>
    private sealed class FileHost(McpServer server) : IRimForgeHost
    {
        public Task<RimForgeState> GetStateAsync(CancellationToken ct) => server._files.GetAsync(ct);

        public async Task<WriteResult> WriteAsync(WriteRequest request, CancellationToken ct)
        {
            await server._writeLock.WaitAsync(ct);
            try
            {
                if (!await AiBridge.EnsureWindowAsync(ct))
                    return new WriteResult(false, "Could not open the RimForge window to apply the change.");
                var reply = await AiBridge.SendAsync(new BridgeMessage { Kind = "write", Write = request, Client = request.Client }, ct, 5000);
                return reply is null ? new WriteResult(false, "The RimForge window did not answer.") : new WriteResult(reply.Ok && !reply.IsError, reply.Text);
            }
            finally
            {
                server._writeLock.Release();
            }
        }
    }

    private readonly SemaphoreSlim _outputLock = new(1, 1);

    private async Task SendAsync(JsonNode message)
    {
        await _outputLock.WaitAsync();
        try
        {
            await output.WriteLineAsync(message.ToJsonString());
            await output.FlushAsync();
        }
        finally
        {
            _outputLock.Release();
        }
    }

    private static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
