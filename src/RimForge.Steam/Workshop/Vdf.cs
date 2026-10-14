using System.Text;

namespace RimForge.Steam.Workshop;

/// <summary>
/// Reader for Valve's text KeyValues format (.acf, .vdf): nested <c>"key" "value"</c> pairs and
/// <c>"key" { ... }</c> blocks. Keys are case-insensitive, like Steam treats them.
/// </summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.GetValueOrDefault(key);
    public string? Value(string key) => Values.GetValueOrDefault(key);
    public long Long(string key) => long.TryParse(Value(key), out var v) ? v : 0;

    public static VdfNode Parse(string text)
    {
        var pos = 0;
        var root = new VdfNode();
        ReadBlock(text, ref pos, root);
        return root;
    }

    private static void ReadBlock(string text, ref int pos, VdfNode node)
    {
        while (true)
        {
            var key = ReadToken(text, ref pos);
            if (key is null || key == "}") return;
            var next = ReadToken(text, ref pos);
            if (next is null) return;
            if (next == "{")
            {
                var child = new VdfNode();
                ReadBlock(text, ref pos, child);
                node.Children[key] = child;
            }
            else
            {
                node.Values[key] = next;
            }
        }
    }

    /// <summary>Returns a quoted or bare token, "{" or "}", or null at the end of the text.</summary>
    private static string? ReadToken(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            break;
        }
        if (pos >= text.Length) return null;

        var ch = text[pos];
        if (ch is '{' or '}')
        {
            pos++;
            return ch.ToString();
        }

        var sb = new StringBuilder();
        if (ch == '"')
        {
            pos++;
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length)
                {
                    pos++;
                    sb.Append(text[pos] switch { 'n' => '\n', 't' => '\t', _ => text[pos] });
                }
                else sb.Append(text[pos]);
                pos++;
            }
            pos++; // closing quote
            return sb.ToString();
        }

        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"'))
            sb.Append(text[pos++]);
        return sb.ToString();
    }
}
