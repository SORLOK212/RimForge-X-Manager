using Microsoft.ML.OnnxRuntime;

namespace RimForge.Translation;

/// <summary>
/// The offline model's files: Meta's NLLB-200 distilled 600M, converted to ONNX and 8-bit by Xenova,
/// downloaded from Hugging Face on the player's request (≈ 900 MB, licence CC-BY-NC 4.0: free, non-commercial use).
/// After the download the decoder is reworked once (<see cref="NllbModelOptimizer"/>) and the original removed.
/// </summary>
public static class NllbModel
{
    public const string Encoder = "encoder_model_quantized.onnx";
    public const string Decoder = "decoder_model_merged_quantized.onnx";
    public const string Tokenizer = "sentencepiece.bpe.model";

    /// <summary>The decoder reworked by <see cref="NllbModelOptimizer"/> (replaces <see cref="Decoder"/>).</summary>
    public const string FastDecoder = "decoder_rimforge.onnx";

    private const string Base = "https://huggingface.co/Xenova/nllb-200-distilled-600M/resolve/main/";

    public static IReadOnlyList<(string File, string Url)> Files { get; } =
    [
        (Tokenizer, Base + Tokenizer),
        (Encoder, Base + "onnx/" + Encoder),
        (Decoder, Base + "onnx/" + Decoder),
    ];

    /// <summary>Approximate total download, for the "≈ 900 MB" note.</summary>
    public const long ApproxBytes = 900L * 1024 * 1024;

    private static bool Present(string dir, string file) =>
        File.Exists(Path.Combine(dir, file)) && new FileInfo(Path.Combine(dir, file)).Length > 1000;

    public static bool IsInstalled(string dir) =>
        Present(dir, Tokenizer) && Present(dir, Encoder) && (Present(dir, FastDecoder) || Present(dir, Decoder));

    public static long InstalledBytes(string dir) =>
        new[] { Tokenizer, Encoder, Decoder, FastDecoder }.Select(f => new FileInfo(Path.Combine(dir, f))).Where(f => f.Exists).Sum(f => f.Length);

    /// <summary>What an interrupted download has already fetched (complete files and ".part" pieces).</summary>
    public static long PartialBytes(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles().Where(f => f.Name.EndsWith(".part") || f.Name.EndsWith(".onnx") || f.Name == Tokenizer).Sum(f => f.Length) : 0;

    /// <summary>The decoder to run: the reworked one when it is there.</summary>
    public static string DecoderPath(string dir) =>
        Present(dir, FastDecoder) ? Path.Combine(dir, FastDecoder) : Path.Combine(dir, Decoder);

    /// <summary>
    /// Downloads the missing files (continuing an interrupted download), reporting the share done, then
    /// reworks the decoder. Files are written as ".part" and renamed when complete.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, string dir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        // The original decoder is not needed once it has been reworked.
        var files = Files.Where(f => f.File != Decoder || !Present(dir, FastDecoder)).ToList();
        var sizes = new long[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, files[i].Url);
            using var response = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            sizes[i] = response.Content.Headers.ContentLength ?? 0;
        }
        var total = Math.Max(1, sizes.Sum());
        long doneBefore = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var (file, url) = files[i];
            var target = Path.Combine(dir, file);
            if (File.Exists(target) && (sizes[i] == 0 || new FileInfo(target).Length == sizes[i]))
            {
                doneBefore += sizes[i];
                continue;
            }
            var part = target + ".part";
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored the range

            await using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[1 << 16];
                var written = have;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    progress?.Report(0.99 * (doneBefore + written) / total);
                }
            }
            File.Move(part, target, overwrite: true);
            doneBefore += sizes[i];
        }
        await Task.Run(() => Prepare(dir), ct);
        progress?.Report(1);
    }

    /// <summary>
    /// Reworks the downloaded decoder (once) and removes the original. If the rework fails for any reason,
    /// the original decoder stays and is used as is — slower, but it works.
    /// </summary>
    public static void Prepare(string dir)
    {
        if (Present(dir, FastDecoder) || !Present(dir, Decoder)) return;
        var fast = Path.Combine(dir, FastDecoder);
        try
        {
            NllbModelOptimizer.Optimize(Path.Combine(dir, Decoder), fast);
            using (new InferenceSession(fast)) { } // make sure ONNX Runtime accepts it
            File.Delete(Path.Combine(dir, Decoder));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { File.Delete(fast); } catch (IOException) { }
        }
    }

    public static void Delete(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
