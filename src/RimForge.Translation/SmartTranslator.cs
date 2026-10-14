using RimForge.Core.Translation;

namespace RimForge.Translation;

/// <summary>
/// Picks the engine for each request: the built-in model when the player chose "offline", otherwise the
/// online translator — falling back to the built-in model when the online one fails and the model is there.
/// </summary>
public sealed class SmartTranslator(ITranslator online, string modelDir) : ITranslator, IDisposable
{
    private NllbTranslator? _offline;

    public bool PreferOffline { get; set; }
    public bool FallBackToOffline { get; set; } = true;
    public bool OfflineReady => NllbModel.IsInstalled(modelDir);

    private NllbTranslator Offline => _offline ??= new NllbTranslator(modelDir);

    public async Task<IReadOnlyList<Translated>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct)
    {
        if (PreferOffline && OfflineReady) return await Offline.TranslateAsync(texts, targetLanguage, ct);
        try
        {
            return await online.TranslateAsync(texts, targetLanguage, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested && FallBackToOffline && OfflineReady)
        {
            return await Offline.TranslateAsync(texts, targetLanguage, ct);
        }
    }

    /// <summary>Frees the model (before deleting its files).</summary>
    public void Unload()
    {
        _offline?.Dispose();
        _offline = null;
    }

    public void Dispose() => Unload();
}
