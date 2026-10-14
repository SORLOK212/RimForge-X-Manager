using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimForge.Core.Translation;

/// <summary>A translated text and the language it was written in ("en", "zh-CN"...).</summary>
public sealed record Translated(string Text, string SourceLanguage);

/// <summary>Something that can translate: the online translator or the built-in offline model.</summary>
public interface ITranslator
{
    /// <summary>Translates texts that are all in one script; returns them in the same order.</summary>
    Task<IReadOnlyList<Translated>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct);
}

/// <summary>
/// Translation with a disk cache (each text is translated once), batching (many titles in one request,
/// grouped by script so the language is detected right) and long texts split into pieces.
/// </summary>
public sealed class TranslationService(string cacheFile)
{
    private const int MaxChunk = 4000;

    private ConcurrentDictionary<string, Translated> _cache = new();
    private readonly SemaphoreSlim _gate = new(2, 2);
    private bool _loaded;
    private int _dirty;

    public ITranslator? Translator { get; set; }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(cacheFile))
                _cache = new ConcurrentDictionary<string, Translated>(
                    JsonSerializer.Deserialize<Dictionary<string, Translated>>(File.ReadAllText(cacheFile)) ?? new());
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            _cache = new();
        }
    }

    public void Save()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var temp = cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache));
            File.Move(temp, cacheFile, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Key(string text, string target) =>
        target + ":" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..24];

    /// <summary>Already-known translation, without any network or model work.</summary>
    public Translated? Cached(string text, string target)
    {
        EnsureLoaded();
        return _cache.TryGetValue(Key(text, target), out var t) ? t : null;
    }

    /// <summary>
    /// Translates each text into <paramref name="target"/> ("ru", "en"). Texts already in that language come
    /// back unchanged with their language set to the target.
    /// </summary>
    public async Task<IReadOnlyList<Translated>> TranslateAsync(IReadOnlyList<string> texts, string target, CancellationToken ct = default)
    {
        EnsureLoaded();
        var result = new Translated?[texts.Count];
        var todo = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (string.IsNullOrWhiteSpace(text) || LanguageGuess.IsAlreadyIn(text, target)) result[i] = new Translated(text, target);
            else if (_cache.TryGetValue(Key(text, target), out var known)) result[i] = known;
            else todo.Add(i);
        }

        if (todo.Count > 0)
        {
            if (Translator is null) throw new InvalidOperationException("No translator");
            // One request per script group (and per long text): the language is detected once per request.
            foreach (var group in todo.GroupBy(i => LanguageGuess.Script(texts[i])))
            {
                var shortOnes = group.Where(i => texts[i].Length <= MaxChunk).ToList();
                foreach (var batch in Batches(shortOnes, texts))
                {
                    var done = await Run(batch.Select(i => texts[i]).ToList(), target, ct);
                    for (var k = 0; k < batch.Count; k++) Store(texts[batch[k]], target, result[batch[k]] = done[k]);
                }
                foreach (var i in group.Where(i => texts[i].Length > MaxChunk))
                {
                    var pieces = Split(texts[i]);
                    var done = await Run(pieces, target, ct);
                    Store(texts[i], target, result[i] = new Translated(string.Concat(done.Select(d => d.Text)), done[0].SourceLanguage));
                }
            }
        }
        return result.Select(r => r!).ToList();
    }

    public async Task<Translated> TranslateAsync(string text, string target, CancellationToken ct = default) =>
        (await TranslateAsync([text], target, ct))[0];

    private async Task<IReadOnlyList<Translated>> Run(IReadOnlyList<string> texts, string target, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Translator!.TranslateAsync(texts, target, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Store(string text, string target, Translated? translated)
    {
        if (translated is null) return;
        _cache[Key(text, target)] = translated;
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Groups short texts so each request stays under the size limit.</summary>
    private static IEnumerable<List<int>> Batches(List<int> indexes, IReadOnlyList<string> texts)
    {
        var batch = new List<int>();
        var size = 0;
        foreach (var i in indexes)
        {
            if (batch.Count > 0 && (size + texts[i].Length > MaxChunk || batch.Count >= 40))
            {
                yield return batch;
                batch = [];
                size = 0;
            }
            batch.Add(i);
            size += texts[i].Length + 1;
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Cuts a long text at paragraph (or sentence) ends, keeping the separators.</summary>
    internal static List<string> Split(string text)
    {
        var pieces = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + MaxChunk);
            if (end < text.Length)
            {
                var cut = text.LastIndexOf('\n', end - 1, end - start);
                if (cut <= start) cut = text.LastIndexOf(". ", end - 1, end - start, StringComparison.Ordinal) + 1;
                if (cut > start) end = cut + 1;
            }
            pieces.Add(text[start..end]);
            start = end;
        }
        return pieces;
    }
}
