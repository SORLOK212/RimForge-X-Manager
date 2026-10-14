using RimForge.Core.Diagnostics;
using RimForge.Core.Models;

namespace RimForge.Core.Tests;

public sealed class TextureConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-tex-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ModInfo Mod(string id, string name, ModSource source = ModSource.Local, string[]? loadAfter = null, params string[] textures)
    {
        var folder = Path.Combine(_root, id);
        foreach (var t in textures)
        {
            var file = Path.Combine(folder, "Textures", t);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, [1]);
        }
        Directory.CreateDirectory(folder);
        return new ModInfo { PackageId = id, DeclaredPackageId = id, Name = name, FolderPath = folder, Source = source, LoadAfter = loadAfter ?? [] };
    }

    [Fact]
    public void FindsSharedPictures_AndTellsIntendedOnesApart()
    {
        var mods = new[]
        {
            Mod("ludeon.rimworld", "Core", ModSource.Official, null, "Things/Item/Steel.png"),
            Mod("a.guns", "Guns", ModSource.Local, null, "Things/Gun/Rifle.png", "UI/Icon.png"),
            Mod("b.more", "More Guns", ModSource.Local, null, "Things/Gun/Rifle.dds", "UI/Icon.png"),
            Mod("c.steel", "Shiny Steel", ModSource.Local, null, "Things/Item/Steel.png"),
            Mod("d.hd", "Guns Extra", ModSource.Local, ["a.guns"], "Things/Gun/Rifle.png"),
        }.ToDictionary(m => m.PackageId);

        var clashes = TextureConflicts.Find(["ludeon.rimworld", "a.guns", "b.more", "c.steel", "d.hd"], mods, "1.6");

        // Rifle is in three mods (one as .dds: same picture), Icon in two.
        var rifle = Assert.Single(clashes, c => c.Paths.Contains("Things/Gun/Rifle"));
        Assert.Equal(["a.guns", "b.more", "d.hd"], rifle.ModIds);
        Assert.Equal("d.hd", rifle.Winner);
        Assert.False(rifle.Intended); // it loads after Guns, but not after More Guns

        var icon = Assert.Single(clashes, c => c.Paths.Contains("UI/Icon"));
        Assert.Equal(["a.guns", "b.more"], icon.ModIds);
        Assert.False(icon.Intended);

        // Replacing only the game's own art is what retextures do.
        Assert.True(Assert.Single(clashes, c => c.Paths.Contains("Things/Item/Steel")).Intended);
    }
}
