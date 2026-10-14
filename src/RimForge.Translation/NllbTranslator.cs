using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using RimForge.Core.Translation;

namespace RimForge.Translation;

/// <summary>
/// Offline translation with Meta's NLLB-200 (600M, 8-bit), run by ONNX Runtime on the CPU.
/// 200 languages, any to any (Chinese straight to Russian). Texts are cut into sentences (the model was
/// trained on single sentences) and the sentences are translated in batches. The model is freed after a
/// couple of idle minutes — it takes a lot of memory.
/// </summary>
public sealed partial class NllbTranslator : ITranslator, IDisposable
{
    private const int Pad = 1, Eos = 2, Unk = 3, FairseqOffset = 1, FirstLanguageId = 256001;
    private const int MaxSourceTokens = 160;
    private const int MaxBatch = 12;
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(2);

    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private InferenceSession? _encoder, _decoder;
    private SentencePieceTokenizer? _tokenizer;
    private int _layers, _heads, _headDim;
    private DateTime _lastUse;

    public NllbTranslator(string modelDir)
    {
        _dir = modelDir;
        _idleTimer = new Timer(_ => UnloadIfIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>NLLB language codes for the languages RimForge recognises.</summary>
    private static readonly Dictionary<string, (string Code, int Id)> Languages = new()
    {
        ["en"] = ("eng_Latn", 256047), ["ru"] = ("rus_Cyrl", 256147), ["zh-CN"] = ("zho_Hans", 256200), ["zh-TW"] = ("zho_Hant", 256201),
        ["ja"] = ("jpn_Jpan", 256079), ["ko"] = ("kor_Hang", 256098), ["de"] = ("deu_Latn", 256042), ["fr"] = ("fra_Latn", 256057),
        ["es"] = ("spa_Latn", 256161), ["pt"] = ("por_Latn", 256141), ["pl"] = ("pol_Latn", 256140), ["it"] = ("ita_Latn", 256077),
        ["tr"] = ("tur_Latn", 256184), ["uk"] = ("ukr_Cyrl", 256188), ["cs"] = ("ces_Latn", 256036), ["hu"] = ("hun_Latn", 256071),
        ["nl"] = ("nld_Latn", 256127), ["sv"] = ("swe_Latn", 256167), ["fi"] = ("fin_Latn", 256055), ["th"] = ("tha_Thai", 256175),
        ["vi"] = ("vie_Latn", 256193), ["id"] = ("ind_Latn", 256075),
    };

    public static bool Supports(string language) => Languages.ContainsKey(language);

    private void EnsureLoaded()
    {
        if (_decoder is not null) return;
        // Installs from before the decoder rework get it now (a second or two, once).
        NllbModel.Prepare(_dir);
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        using (var spm = File.OpenRead(Path.Combine(_dir, NllbModel.Tokenizer)))
            _tokenizer = SentencePieceTokenizer.Create(spm, false, false);
        _encoder = new InferenceSession(Path.Combine(_dir, NllbModel.Encoder), options);
        _decoder = new InferenceSession(NllbModel.DecoderPath(_dir), options);

        var key = _decoder.InputMetadata.First(kv => kv.Key.StartsWith("past_key_values.") && kv.Key.EndsWith(".decoder.key"));
        _layers = _decoder.InputMetadata.Keys.Count(k => k.EndsWith(".decoder.key"));
        _heads = key.Value.Dimensions[1] > 0 ? key.Value.Dimensions[1] : 16;
        _headDim = key.Value.Dimensions[3] > 0 ? key.Value.Dimensions[3] : 64;
    }

    private void Unload()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
        _encoder = _decoder = null;
        _tokenizer = null;
    }

    private void UnloadIfIdle()
    {
        if (_decoder is null || DateTime.UtcNow - _lastUse < IdleUnload || !_gate.Wait(0)) return;
        try
        {
            if (_decoder is not null && DateTime.UtcNow - _lastUse >= IdleUnload)
            {
                Unload();
                GC.Collect();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Translated>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                EnsureLoaded();
                var target = Languages.GetValueOrDefault(targetLanguage, Languages["en"]).Id;
                var sources = texts.Select(LanguageGuess.Guess).ToList();

                // Every text → lines → sentences; the sentences of all texts are translated together.
                var layout = new List<List<List<int>?>>(); // text → line → sentence indexes (null: line kept as is)
                var lines = new List<string[]>();
                var sentences = new List<(string Text, int Source)>();
                for (var t = 0; t < texts.Count; t++)
                {
                    var src = Languages.GetValueOrDefault(sources[t], Languages["en"]).Id;
                    var textLines = texts[t].Replace("\r\n", "\n").Split('\n');
                    lines.Add(textLines);
                    var perLine = new List<List<int>?>();
                    foreach (var line in textLines)
                    {
                        if (!line.Any(char.IsLetter))
                        {
                            perLine.Add(null);
                            continue;
                        }
                        var ids = new List<int>();
                        foreach (var sentence in Sentences(line.Trim()))
                        {
                            ids.Add(sentences.Count);
                            sentences.Add((sentence, src));
                        }
                        perLine.Add(ids);
                    }
                    layout.Add(perLine);
                }

                var translated = TranslateSentences(sentences, target, ct);

                var result = new List<Translated>(texts.Count);
                for (var t = 0; t < texts.Count; t++)
                {
                    var output = new List<string>();
                    for (var l = 0; l < lines[t].Length; l++)
                    {
                        var line = lines[t][l];
                        if (layout[t][l] is not { } ids)
                        {
                            output.Add(line);
                            continue;
                        }
                        var lead = line[..(line.Length - line.TrimStart().Length)];
                        output.Add(lead + string.Join(" ", ids.Select(i => translated[i])));
                    }
                    result.Add(new Translated(string.Join("\n", output), sources[t]));
                }
                return (IReadOnlyList<Translated>)result;
            }, ct);
        }
        finally
        {
            _lastUse = DateTime.UtcNow;
            _gate.Release();
        }
    }

    [GeneratedRegex(@"(?<=[.!?])\s+(?=\S)|(?<=[。！？])")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"(?<=[,;:，；：])\s*")]
    private static partial Regex ClauseEnd();

    /// <summary>Sentences of a line; a sentence too long for the model is cut at commas, then by words.</summary>
    private IEnumerable<string> Sentences(string line)
    {
        foreach (var sentence in SentenceEnd().Split(line).Where(s => s.Trim().Length > 0))
        {
            if (Count(sentence) <= MaxSourceTokens)
            {
                yield return sentence.Trim();
                continue;
            }
            var current = "";
            foreach (var part in ClauseEnd().Split(sentence).SelectMany(p => Count(p) <= MaxSourceTokens ? [p] : ByWords(p)))
            {
                if (current.Length > 0 && Count(current + " " + part) > MaxSourceTokens)
                {
                    yield return current.Trim();
                    current = part;
                }
                else current = current.Length == 0 ? part : current + " " + part;
            }
            if (current.Trim().Length > 0) yield return current.Trim();
        }
    }

    private static IEnumerable<string> ByWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i += 60) yield return string.Join(" ", words.Skip(i).Take(60));
    }

    private int Count(string text) => _tokenizer!.EncodeToIds(text, false, false, true, true).Count;

    private string[] TranslateSentences(List<(string Text, int Source)> sentences, int target, CancellationToken ct)
    {
        var result = new string[sentences.Count];
        var encoded = sentences.Select((s, i) => (Index: i, Ids: Encode(s.Text, s.Source))).OrderBy(e => e.Ids.Length).ToList();
        // Similar lengths together, so little padding is computed.
        for (var start = 0; start < encoded.Count; start += MaxBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = encoded.Skip(start).Take(MaxBatch).ToList();
            var outputs = TranslateBatch(batch.Select(b => b.Ids).ToList(), target, ct);
            for (var i = 0; i < batch.Count; i++) result[batch[i].Index] = outputs[i];
        }
        return result;
    }

    private long[] Encode(string text, int source)
    {
        var ids = new List<long>(MaxSourceTokens + 2) { source };
        ids.AddRange(_tokenizer!.EncodeToIds(text, false, false, true, true).Take(MaxSourceTokens)
            .Select(i => (long)(i == 0 ? Unk : i + FairseqOffset)));
        ids.Add(Eos);
        return ids.ToArray();
    }

    /// <summary>Greedy decoding of a batch of sentences, reusing the attention cache between steps.</summary>
    private string[] TranslateBatch(List<long[]> sources, int target, CancellationToken ct)
    {
        var batch = sources.Count;
        var length = sources.Max(s => s.Length);
        var inputIds = new long[batch * length];
        var mask = new long[batch * length];
        for (var b = 0; b < batch; b++)
        for (var i = 0; i < length; i++)
        {
            var real = i < sources[b].Length;
            inputIds[b * length + i] = real ? sources[b][i] : Pad;
            mask[b * length + i] = real ? 1 : 0;
        }

        using var runOptions = new RunOptions();
        using var idsValue = OrtValue.CreateTensorValueFromMemory(inputIds, [batch, length]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, [batch, length]);
        using var encoderOut = _encoder!.Run(runOptions, ["input_ids", "attention_mask"], [idsValue, maskValue], ["last_hidden_state"]);
        var hidden = encoderOut[0];

        var pastNames = new List<string>();
        for (var l = 0; l < _layers; l++)
            pastNames.AddRange([$"past_key_values.{l}.decoder.key", $"past_key_values.{l}.decoder.value",
                $"past_key_values.{l}.encoder.key", $"past_key_values.{l}.encoder.value"]);
        var inputNames = new List<string> { "input_ids", "encoder_attention_mask", "encoder_hidden_states", "use_cache_branch" };
        inputNames.AddRange(pastNames);
        var outputNames = _decoder!.OutputNames.ToList();
        var logitsAt = outputNames.IndexOf("logits");
        var presentAt = pastNames.Select(n => outputNames.IndexOf(n.Replace("past_key_values.", "present."))).ToArray();

        using var empty = OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, [batch, _heads, 0, _headDim]);
        IDisposableReadOnlyCollection<OrtValue>? first = null, previous = null;
        var output = Enumerable.Range(0, batch).Select(_ => new List<int>()).ToArray();
        var done = new bool[batch];
        var limits = sources.Select(s => Math.Min(400, s.Length * 2 + 20)).ToArray();
        try
        {
            var step = 0;
            var next = new long[batch * 2];
            for (var b = 0; b < batch; b++)
            {
                next[b * 2] = Eos;
                next[b * 2 + 1] = target;
            }
            var width = 2;
            while (!done.All(d => d) && step < 400)
            {
                ct.ThrowIfCancellationRequested();
                using var nextValue = OrtValue.CreateTensorValueFromMemory(next, [batch, width]);
                using var useCache = OrtValue.CreateTensorValueFromMemory(new[] { step > 0 }, [1]);
                var inputs = new List<OrtValue> { nextValue, maskValue, hidden, useCache };
                for (var p = 0; p < pastNames.Count; p++)
                {
                    var isEncoder = p % 4 >= 2;
                    inputs.Add(step == 0 ? empty : isEncoder ? first![presentAt[p]] : previous![presentAt[p]]);
                }

                var results = _decoder.Run(runOptions, inputNames, inputs, outputNames);
                if (step == 0) first = results;
                else if (previous != first) previous?.Dispose();
                previous = results;

                // The best word for every sentence (never a language code or padding).
                var logits = results[logitsAt].GetTensorDataAsSpan<float>();
                var shape = results[logitsAt].GetTensorTypeAndShape().Shape; // [batch, positions, vocab]
                var positions = (int)shape[1];
                var vocab = (int)shape[2];
                next = new long[batch];
                width = 1;
                for (var b = 0; b < batch; b++)
                {
                    if (done[b])
                    {
                        next[b] = Pad;
                        continue;
                    }
                    var scores = logits.Slice((b * positions + positions - 1) * vocab, vocab);
                    var best = Eos;
                    var bestScore = float.NegativeInfinity;
                    for (var v = 0; v < FirstLanguageId && v < vocab; v++)
                    {
                        if (v == Pad) continue;
                        if (scores[v] > bestScore)
                        {
                            bestScore = scores[v];
                            best = v;
                        }
                    }
                    if (best == Eos || output[b].Count >= limits[b])
                    {
                        done[b] = true;
                        next[b] = Pad;
                        continue;
                    }
                    output[b].Add(best);
                    if (Repeats(output[b])) done[b] = true;
                    next[b] = best;
                }
                step++;
            }
        }
        finally
        {
            if (previous != first) previous?.Dispose();
            first?.Dispose();
        }

        return output.Select(tokens =>
            _tokenizer!.Decode(tokens.Where(id => id > Unk && id < FirstLanguageId).Select(id => id - FairseqOffset)).Trim()).ToArray();
    }

    /// <summary>A model stuck repeating the same few words is stopped.</summary>
    private static bool Repeats(List<int> tokens)
    {
        const int Window = 4;
        if (tokens.Count < Window * 3) return false;
        for (var k = 1; k <= Window; k++)
        {
            var a = tokens.Skip(tokens.Count - k).ToList();
            var b = tokens.Skip(tokens.Count - 2 * k).Take(k).ToList();
            var c = tokens.Skip(tokens.Count - 3 * k).Take(k).ToList();
            if (a.SequenceEqual(b) && b.SequenceEqual(c)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        Unload();
    }
}
