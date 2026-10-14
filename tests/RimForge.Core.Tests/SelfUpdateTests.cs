using System.Security.Cryptography;
using RimForge.Core.Updates;

namespace RimForge.Core.Tests;

public sealed class SelfUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-upd-" + Guid.NewGuid().ToString("N"));

    public SelfUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Feed(string version, string exe, string? hash = null)
    {
        var feed = Path.Combine(_root, "latest.json");
        hash ??= Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe)));
        File.WriteAllText(feed, $$"""{ "version": "{{version}}", "url": "{{exe.Replace("\\", "\\\\")}}", "sha256": "{{hash}}", "notes": "Faster." }""");
        return feed;
    }

    [Fact]
    public async Task NewerBuild_IsOffered_Downloaded_AndSwappedIn()
    {
        var release = Path.Combine(_root, "release.exe");
        File.WriteAllText(release, "new build");
        var current = Path.Combine(_root, "app", "RimForge.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "old build");
        using var http = new HttpClient();

        Assert.Null(await SelfUpdate.CheckAsync(http, Feed("0.1.0", release), new Version(0, 1, 0)));
        var info = await SelfUpdate.CheckAsync(http, Feed("0.2.0", release), new Version(0, 1, 0));
        Assert.NotNull(info);
        Assert.Equal("Faster.", info.Notes);

        var file = await SelfUpdate.DownloadAsync(http, info, Path.Combine(_root, "dl"));
        SelfUpdate.Install(file, current);

        Assert.Equal("new build", File.ReadAllText(current));
        Assert.Equal("old build", File.ReadAllText(SelfUpdate.OldPath(current)));
        SelfUpdate.CleanUp(current);
        Assert.False(File.Exists(SelfUpdate.OldPath(current)));
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchItsChecksum_IsRefused()
    {
        var release = Path.Combine(_root, "release.exe");
        File.WriteAllText(release, "tampered");
        using var http = new HttpClient();
        var info = (await SelfUpdate.CheckAsync(http, Feed("9.0.0", release, new string('A', 64)), new Version(0, 1)))!;

        await Assert.ThrowsAsync<InvalidDataException>(() => SelfUpdate.DownloadAsync(http, info, Path.Combine(_root, "dl")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "dl")));
    }
}
