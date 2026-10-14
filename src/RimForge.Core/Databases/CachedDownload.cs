namespace RimForge.Core.Databases;

/// <summary>
/// Community databases are downloaded to the player's PC and kept in a cache file. A fresh copy is fetched when
/// the cache is older than the given age; when the PC is offline, the old copy is used.
/// </summary>
public static class CachedDownload
{
    /// <summary>
    /// Returns the cached bytes, downloading a fresh copy when needed. <paramref name="validate"/> must throw for
    /// broken data, so a bad download never replaces a good cache. A missing file on the server (404) counts as empty.
    /// </summary>
    public static async Task<byte[]?> GetAsync(string url, string cacheFile, TimeSpan maxAge, bool force,
        Action<byte[]> validate, HttpClient? http = null)
    {
        var cached = File.Exists(cacheFile) ? new FileInfo(cacheFile) : null;
        var fresh = cached is not null && DateTime.UtcNow - cached.LastWriteTimeUtc < maxAge;
        if (!force && fresh) return await File.ReadAllBytesAsync(cacheFile);

        try
        {
            using var client = http is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : null;
            using var response = await (http ?? client!).GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Nothing published (yet) for this case, e.g. a game version nobody checked mods for.
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                await File.WriteAllBytesAsync(cacheFile, []);
                return [];
            }
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            validate(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllBytesAsync(cacheFile + ".tmp", bytes);
            File.Move(cacheFile + ".tmp", cacheFile, overwrite: true);
            return bytes;
        }
        catch (Exception) when (cached is not null)
        {
            return await File.ReadAllBytesAsync(cacheFile);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
