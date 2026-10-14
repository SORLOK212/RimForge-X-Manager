using System.Text;
using System.Text.Json;
using RimForge.Core.Models;
using RimForge.Steam.Workshop;
using Steamworks;
using Steamworks.Ugc;

namespace RimForge.Steam.Client;

/// <summary>
/// Runs inside the helper process (see <see cref="HelperProtocol"/>). Exits when RimForge closes its input.
/// </summary>
public static class SteamHelperHost
{
    private static readonly object WriteLock = new();
    private static StreamWriter _output = null!;

    public static int Run()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        try
        {
            SteamClient.Init(KnownMods.RimWorldAppId, asyncCallbacks: true);
        }
        catch (Exception ex)
        {
            Write(new HelperResponse { Id = 0, Ok = false, Error = ex.Message });
            return 1;
        }

        try
        {
            Write(new HelperResponse { Id = 0, Ok = SteamClient.IsLoggedOn, UserName = SteamClient.Name, Error = SteamClient.IsLoggedOn ? null : "offline" });
            var running = new List<Task>();
            while (input.ReadLine() is { } line)
            {
                HelperRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<HelperRequest>(line, HelperProtocol.Json);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (request is null) continue;
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(() => HandleAsync(request)));
            }
            Task.WaitAll(running.ToArray(), TimeSpan.FromSeconds(5));
        }
        finally
        {
            SteamClient.Shutdown();
        }
        return 0;
    }

    private static async Task HandleAsync(HelperRequest request)
    {
        HelperResponse response;
        try
        {
            response = request.Op switch
            {
                HelperOp.Search => new HelperResponse { Page = await SearchAsync(request.Query!) },
                HelperOp.Details => new HelperResponse { Items = await DetailsAsync(request.Ids ?? []) },
                HelperOp.States => new HelperResponse { States = (request.Ids ?? []).Select(StateOf).ToList() },
                HelperOp.Subscribe => await SubscribeAsync(request.Ids ?? []),
                HelperOp.Unsubscribe => await UnsubscribeAsync(request.Ids ?? []),
                HelperOp.Download => Download(request.Ids ?? []),
                _ => new HelperResponse { Error = "unknown op" },
            };
            response = response with { Ok = response.Error is null };
        }
        catch (Exception ex)
        {
            response = new HelperResponse { Ok = false, Error = ex.Message };
        }
        Write(response with { Id = request.Id });
    }

    private static async Task<WorkshopPage> SearchAsync(WorkshopQuery q)
    {
        var query = Query.Items;
        var hasText = !string.IsNullOrWhiteSpace(q.Text);
        query = q.Sort switch
        {
            WorkshopSort.MostSubscribed => query.RankedByTotalUniqueSubscriptions(),
            WorkshopSort.Newest => query.RankedByPublicationDate(),
            WorkshopSort.Relevance when hasText => query.RankedByTextSearch(),
            _ => query.RankedByTrend().WithTrendDays(7),
        };
        if (hasText) query = query.WhereSearchText(q.Text!.Trim());
        if (q.VersionTag is { } tag) query = query.WithTag(tag);
        query = query.WithLongDescription(true);

        using var page = await query.GetPageAsync(Math.Max(1, q.Page));
        if (page is not { } p) return new WorkshopPage([], 0, false);
        var items = p.Entries.Select(ToItem).ToList();
        if (q.WithAuthors) await FillAuthorsAsync(items);
        // Steam returns 50 items a page.
        return new WorkshopPage(items, p.TotalCount, q.Page * 50 < p.TotalCount, 50);
    }

    private static async Task<List<WorkshopItem>> DetailsAsync(ulong[] ids)
    {
        if (ids.Length == 0) return [];
        var result = new List<WorkshopItem>();
        foreach (var chunk in ids.Chunk(50))
        {
            var query = Query.All.WithFileId(chunk.Select(id => (Steamworks.Data.PublishedFileId)id).ToArray()).WithLongDescription(true);
            using var page = await query.GetPageAsync(1);
            if (page is { } p) result.AddRange(p.Entries.Select(ToItem));
        }
        await FillAuthorsAsync(result);
        return result;
    }

    private static WorkshopItem ToItem(Item i) => new()
    {
        Id = i.Id.Value,
        Title = i.Title ?? i.Id.Value.ToString(),
        Description = i.Description ?? "",
        PreviewUrl = string.IsNullOrEmpty(i.PreviewImageUrl) ? null : i.PreviewImageUrl,
        AuthorId = i.Owner.Id.Value,
        Subscribers = (long)i.NumUniqueSubscriptions,
        CreatedUtc = i.Created,
        UpdatedUtc = i.Updated,
        Tags = i.Tags ?? [],
        VotesUp = i.VotesUp,
        VotesDown = i.VotesDown,
        Removed = i.IsBanned || i.Result != Result.OK,
    };

    /// <summary>Author names come from the friends system and may need a moment to arrive.</summary>
    private static async Task FillAuthorsAsync(List<WorkshopItem> items)
    {
        var owners = items.Select(i => i.AuthorId).Where(id => id != 0).Distinct().Select(id => new Friend(id)).ToList();
        var lookups = owners.Select(f => f.RequestInfoAsync());
        await Task.WhenAny(Task.WhenAll(lookups), Task.Delay(3000));
        var names = owners.ToDictionary(f => f.Id.Value, f => f.Name);
        for (var k = 0; k < items.Count; k++)
            if (names.TryGetValue(items[k].AuthorId, out var name) && !string.IsNullOrEmpty(name) && name != "[unknown]")
                items[k] = items[k] with { AuthorName = name };
    }

    private static ItemState StateOf(ulong id)
    {
        var item = new Item(id);
        return new ItemState(id, item.IsSubscribed, item.IsInstalled, item.IsDownloading, item.IsDownloadPending, item.NeedsUpdate,
            item.DownloadBytesDownloaded, item.DownloadBytesTotal, item.IsInstalled ? item.Directory : null);
    }

    private static async Task<HelperResponse> SubscribeAsync(ulong[] ids)
    {
        var failed = new List<ulong>();
        foreach (var id in ids)
        {
            var item = new Item(id);
            if (!item.IsSubscribed && !await item.Subscribe()) failed.Add(id);
            else SteamUGC.Download(id, highPriority: true);
        }
        return new HelperResponse { Error = failed.Count > 0 ? "subscribe failed: " + string.Join(",", failed) : null };
    }

    private static async Task<HelperResponse> UnsubscribeAsync(ulong[] ids)
    {
        var failed = new List<ulong>();
        foreach (var id in ids)
            if (!await new Item(id).Unsubscribe()) failed.Add(id);
        return new HelperResponse { Error = failed.Count > 0 ? "unsubscribe failed: " + string.Join(",", failed) : null };
    }

    private static HelperResponse Download(ulong[] ids)
    {
        foreach (var id in ids) SteamUGC.Download(id, highPriority: true);
        return new HelperResponse();
    }

    private static void Write(HelperResponse response)
    {
        var line = JsonSerializer.Serialize(response, HelperProtocol.Json);
        lock (WriteLock) _output.WriteLine(line);
    }
}
