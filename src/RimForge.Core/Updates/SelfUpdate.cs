using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimForge.Core.Updates;

/// <summary>
/// A published RimForge build, as the update feed (latest.json next to the release) describes it:
/// <code>{ "version": "0.2.0", "url": "https://…/RimForge.exe", "sha256": "…", "size": 55927903, "notes": "…" }</code>
/// </summary>
public sealed record UpdateInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size = 0,
    [property: JsonPropertyName("notes")] string? Notes = null)
{
    public Version? Parsed => System.Version.TryParse(Version.TrimStart('v', 'V'), out var v) ? v : null;
}

/// <summary>
/// RimForge updating itself: read the feed, download the new exe, check its SHA-256, and swap it in. A running exe
/// can be renamed on Windows, so the old one steps aside as "RimForge.exe.old" (no .exe — it can't be started by
/// mistake) and is deleted on the next start.
/// </summary>
public static class SelfUpdate
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The newer build the feed offers, or null when this one is the newest (or the feed can't be read).</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, string feed, Version current, CancellationToken ct = default)
    {
        var text = IsLocal(feed) ? await File.ReadAllTextAsync(LocalPath(feed), ct) : await http.GetStringAsync(feed, ct);
        var info = JsonSerializer.Deserialize<UpdateInfo>(text, Json);
        return info?.Parsed is { } v && v > current && !string.IsNullOrWhiteSpace(info.Url) && info.Sha256.Length == 64 ? info : null;
    }

    /// <summary>Downloads the build into <paramref name="folder"/> and returns its path; a file whose SHA-256 differs is deleted.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string folder, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "RimForge-" + info.Version + ".exe");
        var temp = target + ".part";
        await using (var output = File.Create(temp))
        {
            Stream input;
            long total;
            if (IsLocal(info.Url))
            {
                input = File.OpenRead(LocalPath(info.Url));
                total = input.Length;
            }
            else
            {
                var response = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                total = response.Content.Headers.ContentLength ?? info.Size;
                input = await response.Content.ReadAsStreamAsync(ct);
            }
            await using (input)
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
        }

        if (!string.Equals(await HashAsync(temp, ct), info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException("The downloaded update does not match its checksum.");
        }
        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>Puts the downloaded build in place of <paramref name="currentExe"/>; on any failure the old one is put back.</summary>
    public static void Install(string newExe, string currentExe)
    {
        var old = OldPath(currentExe);
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try
        {
            File.Move(newExe, currentExe);
        }
        catch
        {
            File.Move(old, currentExe);
            throw;
        }
    }

    /// <summary>Removes the build an update replaced (it may still be running for a moment, so failing is fine).</summary>
    public static void CleanUp(string currentExe)
    {
        try
        {
            if (File.Exists(OldPath(currentExe))) File.Delete(OldPath(currentExe));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes the feed for a build: run at publish time, next to the exe.</summary>
    public static async Task<UpdateInfo> DescribeAsync(string exe, string version, string url, string? notes)
    {
        var info = new UpdateInfo(version, url, await HashAsync(exe, default), new FileInfo(exe).Length, notes);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(exe)!, "latest.json"),
            JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        return info;
    }

    public static string OldPath(string currentExe) => currentExe + ".old";

    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private static bool IsLocal(string url) => url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(url) && !url.Contains("://");

    private static string LocalPath(string url) => url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;
}
