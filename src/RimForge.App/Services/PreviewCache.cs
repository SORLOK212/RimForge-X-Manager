using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using RimForge.Core.Storage;

namespace RimForge.App.Services;

/// <summary>
/// Workshop preview pictures: asked from Steam already shrunk to the needed size, kept on disk so
/// the same picture is downloaded once, and decoded off the UI thread.
/// </summary>
public sealed class PreviewCache(HttpClient http)
{
    private static string Dir => Path.Combine(AppDataPaths.Root, "cache", "previews");
    private readonly SemaphoreSlim _slots = new(8, 8);

    public async Task<Bitmap?> GetAsync(string? url, int width, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var sized = Sized(url, width);
        var file = Path.Combine(Dir, Hash(sized) + ".img");
        try
        {
            if (!File.Exists(file))
            {
                await _slots.WaitAsync(ct);
                try
                {
                    if (!File.Exists(file))
                    {
                        var bytes = await DownloadAsync(sized, ct);
                        Directory.CreateDirectory(Dir);
                        var temp = file + ".tmp";
                        await File.WriteAllBytesAsync(temp, bytes, ct);
                        File.Move(temp, file, overwrite: true);
                    }
                }
                finally
                {
                    _slots.Release();
                }
            }
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(file);
                return Bitmap.DecodeToWidth(stream, width);
            }, ct);
        }
        catch (Exception)
        {
            return null; // a missing picture is not worth an error message
        }
    }

    /// <summary>Steam's image server resizes on request: a 320 px card picture is a few KB instead of a few MB.</summary>
    private static string Sized(string url, int width)
    {
        if (!url.Contains("steamusercontent.com") && !url.Contains("steamuserimages")) return url;
        var height = width * 9 / 16;
        var baseUrl = url.Split('?')[0];
        return $"{baseUrl}?imw={width}&imh={height}&ima=fit&impolicy=Letterbox&imcolor=%23000000&letterbox=true";
    }

    /// <summary>One more try after a short pause: Steam's image server sometimes drops a request when many go at once.</summary>
    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            return await http.GetByteArrayAsync(url, ct);
        }
        catch (HttpRequestException)
        {
            await Task.Delay(1500, ct);
            return await http.GetByteArrayAsync(url, ct);
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..20];

    /// <summary>Removes pictures not used for a month so the cache can't grow forever.</summary>
    public static void Trim()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in new DirectoryInfo(Dir).EnumerateFiles())
                if (f.LastAccessTimeUtc < DateTime.UtcNow.AddDays(-30) && f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))
                    f.Delete();
        }
        catch (Exception)
        {
        }
    }
}
