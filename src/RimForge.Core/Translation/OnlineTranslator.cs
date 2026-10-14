using System.Text.Json;

namespace RimForge.Core.Translation;

/// <summary>
/// The free Google Translate web endpoint (the one browser extensions use). No key needed, good quality —
/// but unofficial, so it may hit limits; the offline model is the fallback.
/// </summary>
public sealed class OnlineTranslator(HttpClient http) : ITranslator
{
    private const string Url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t&tl=";

    public async Task<IReadOnlyList<Translated>> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct)
    {
        // Several texts go as lines of one request; line breaks inside a text are protected first.
        const string Break = " ¶ ";
        var joined = string.Join("\n", texts.Select(t => texts.Count > 1 ? t.Replace("\r\n", "\n").Replace("\n", Break) : t));
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", joined)]);
        using var response = await http.PostAsync(Url + Uri.EscapeDataString(targetLanguage), content, ct);
        response.EnsureSuccessStatusCode();
        var (text, source) = Parse(await response.Content.ReadAsStringAsync(ct));

        if (texts.Count == 1) return [new Translated(text, source)];
        var lines = text.Split('\n');
        if (lines.Length != texts.Count)
        {
            // The service merged or split lines: fall back to one request per text.
            var one = new List<Translated>();
            foreach (var t in texts) one.Add((await TranslateAsync([t], targetLanguage, ct))[0]);
            return one;
        }
        return lines.Select(l => new Translated(RestoreBreaks(l.TrimEnd(), Break), source)).ToList();
    }

    private static string RestoreBreaks(string line, string mark) =>
        line.Replace(mark, "\n").Replace(mark.Trim(), "\n").Replace(" \n ", "\n").Replace("\n ", "\n").Replace(" \n", "\n");

    /// <summary>Reads [[["перевод","original",...],...],null,"en",...].</summary>
    internal static (string Text, string Source) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var text = string.Concat(root[0].EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0 && s[0].ValueKind == JsonValueKind.String)
            .Select(s => s[0].GetString()));
        var source = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString()! : "auto";
        return (text, source);
    }
}
