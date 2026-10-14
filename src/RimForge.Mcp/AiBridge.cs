using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimForge.Core.Storage;

namespace RimForge.Mcp;

/// <summary>A change an AI assistant asks for. The RimForge window shows it to the player and applies it.</summary>
public sealed record WriteRequest
{
    /// <summary>One of <see cref="WriteActions"/>.</summary>
    public required string Action { get; init; }
    public List<string>? Ids { get; init; }
    public List<ulong>? WorkshopIds { get; init; }
    public string? Name { get; init; }
    public string? Mode { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public bool Flag { get; init; }
    /// <summary>The AI app that asks (from the MCP handshake), shown to the player.</summary>
    public string? Client { get; init; }
}

public static class WriteActions
{
    public const string SetLoadOrder = "set_load_order";
    public const string Enable = "enable_mods";
    public const string Disable = "disable_mods";
    public const string Sort = "sort_mods";
    public const string AddRule = "add_order_rule";
    public const string Pin = "pin_mod";
    public const string CreateModpack = "create_modpack";
    public const string SwitchModpack = "switch_modpack";
    public const string Download = "download_mods";
    public const string Launch = "launch_game";
}

public sealed record WriteResult(bool Ok, string Message);

/// <summary>What the tools work against: the open window, or the files when it is closed.</summary>
public interface IRimForgeHost
{
    Task<RimForgeState> GetStateAsync(CancellationToken ct);
    Task<WriteResult> WriteAsync(WriteRequest request, CancellationToken ct);
}

/// <summary>A tool call or a change, sent from the AI server process to the window over a named pipe.</summary>
public sealed record BridgeMessage
{
    /// <summary>"call" (run a tool in the window) or "write" (apply a change).</summary>
    public required string Kind { get; init; }
    public string? Tool { get; init; }
    public JsonElement? Arguments { get; init; }
    public WriteRequest? Write { get; init; }
    public string? Client { get; init; }
}

public sealed record BridgeReply(bool Ok, string Text, bool IsError = false);

/// <summary>
/// The named pipe between the AI server (<c>RimForge.exe --mcp</c>, started by the AI app) and the RimForge window.
/// The pipe is open to the current Windows user only. One JSON line in, one JSON line out per connection.
/// </summary>
public static class AiBridge
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One pipe per RimForge data folder, so a test copy never talks to the real one.</summary>
    public static string PipeName
    {
        get
        {
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppDataPaths.Root).ToLowerInvariant()));
            return "RimForge.Ai." + Convert.ToHexString(hash)[..12];
        }
    }

    /// <summary>Sends a message to the window. Null when the window is not running.</summary>
    public static async Task<BridgeReply?> SendAsync(BridgeMessage message, CancellationToken ct, int connectTimeoutMs = 400)
    {
        // Not PipeOptions.CurrentUserOnly: when one side runs as administrator the pipe's owner is the Administrators
        // group and that check fails. The window's pipe admits only this user anyway (see ServeAsync).
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeoutMs, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(message, Json).AsMemory(), ct);
        var line = await new StreamReader(pipe, Encoding.UTF8).ReadLineAsync(ct);
        return line is null ? new BridgeReply(false, "The RimForge window closed the connection.", true)
            : JsonSerializer.Deserialize<BridgeReply>(line, Json);
    }

    /// <summary>Starts the RimForge window (if needed) and waits until it accepts messages.</summary>
    public static async Task<bool> EnsureWindowAsync(CancellationToken ct)
    {
        if (await PingAsync(ct)) return true;
        if (Environment.ProcessPath is not { } exe) return false;
        // ShellExecute: the window must not inherit this process's stdin/stdout (they belong to the AI app).
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(700, ct);
            if (await PingAsync(ct)) return true;
        }
        return false;
    }

    private static async Task<bool> PingAsync(CancellationToken ct) =>
        await SendAsync(new BridgeMessage { Kind = "ping" }, ct) is { Ok: true };

    /// <summary>Runs in the window: accepts connections and hands each message to <paramref name="handle"/>.</summary>
    public static async Task ServeAsync(Func<BridgeMessage, CancellationToken, Task<BridgeReply>> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = OperatingSystem.IsWindows()
                    ? NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, OnlyThisUser())
                    : new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another RimForge window already serves this pipe.
                await Task.Delay(5000, ct).ContinueWith(_ => { });
                continue;
            }
            _ = Task.Run(() => ServeOneAsync(pipe, handle, ct), ct);
        }
    }

    /// <summary>Only the Windows user running RimForge may connect (elevated or not).</summary>
    [SupportedOSPlatform("windows")]
    private static PipeSecurity OnlyThisUser()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    private static async Task ServeOneAsync(NamedPipeServerStream pipe, Func<BridgeMessage, CancellationToken, Task<BridgeReply>> handle, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                var line = await new StreamReader(pipe, Encoding.UTF8).ReadLineAsync(ct);
                if (line is null) return;
                BridgeReply reply;
                try
                {
                    var message = JsonSerializer.Deserialize<BridgeMessage>(line, Json)!;
                    reply = message.Kind == "ping" ? new BridgeReply(true, "pong") : await handle(message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    reply = new BridgeReply(false, "RimForge error: " + ex.Message, true);
                }
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json).AsMemory(), ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The AI server went away; nothing to answer.
            }
        }
    }
}
