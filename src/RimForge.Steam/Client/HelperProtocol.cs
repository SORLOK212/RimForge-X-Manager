using System.Text.Json;
using System.Text.Json.Serialization;
using RimForge.Steam.Workshop;

namespace RimForge.Steam.Client;

/// <summary>
/// The Steam helper is RimForge.exe started with <see cref="Argument"/>. It talks to the Steam client
/// as RimWorld (that is the only way to search and subscribe), one JSON message per line over stdin/stdout.
/// Keeping it in its own process means Steam shows "playing RimWorld" only while it runs, and the game
/// can be started normally once it exits.
/// </summary>
public static class HelperProtocol
{
    public const string Argument = "--steam-helper";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

public enum HelperOp { Search, Details, States, Subscribe, Unsubscribe, Download }

public sealed record HelperRequest
{
    public int Id { get; init; }
    public HelperOp Op { get; init; }
    public WorkshopQuery? Query { get; init; }
    public ulong[]? Ids { get; init; }
}

public sealed record HelperResponse
{
    /// <summary>0 is the greeting sent once Steam is connected (or failed to connect).</summary>
    public int Id { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? UserName { get; init; }
    public WorkshopPage? Page { get; init; }
    public List<WorkshopItem>? Items { get; init; }
    public List<ItemState>? States { get; init; }
}

/// <summary>Local state of a Workshop item for the logged-in Steam user.</summary>
public sealed record ItemState(ulong Id, bool Subscribed, bool Installed, bool Downloading, bool Pending, bool NeedsUpdate,
    long BytesDownloaded, long BytesTotal, string? Folder)
{
    public double Progress => BytesTotal > 0 ? Math.Clamp((double)BytesDownloaded / BytesTotal, 0, 1) : 0;
    public bool IsReady => Subscribed && Installed && !Downloading && !Pending && !NeedsUpdate;
}
