using System.Net;
using System.Text.RegularExpressions;

namespace RimForge.Steam.Workshop;

/// <summary>
/// Turns Steam's BBCode descriptions ([h1], [b], [url=…], [img]…) into readable plain text. Also used for About.xml
/// descriptions, where authors paste the same BBCode or the game's rich-text tags (&lt;b&gt;, &lt;color=…&gt;).
/// </summary>
public static partial class BbCode
{
    public static string ToPlainText(string? bb)
    {
        if (string.IsNullOrWhiteSpace(bb)) return "";
        var s = bb.Replace("\r\n", "\n");
        s = Images().Replace(s, "");
        s = Links().Replace(s, m => m.Groups[2].Value.Trim() is { Length: > 0 } text ? text : m.Groups[1].Value);
        s = Headings().Replace(s, m => "\n" + m.Groups[1].Value.Trim() + "\n");
        s = ListItems().Replace(s, "\n• ");
        s = Rules().Replace(s, "\n――――――――\n");
        s = Cells().Replace(s, "  ");
        s = AnyTag().Replace(s, "");
        s = RichText().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, "\n");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    // [img]url[/img], [img=url], [img src="url"][/img], videos and link previews.
    [GeneratedRegex(@"\[img\].*?\[/img\]|\[(img|video|dynamiclink)[ =][^\]]*\](\s*\[/\1\])?|\[previewyoutube=[^\]]*\].*?\[/previewyoutube\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Images();

    [GeneratedRegex(@"\[url=([^\]]*)\](.*?)\[/url\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Links();

    [GeneratedRegex(@"\[h[1-6]\](.*?)\[/h[1-6]\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Headings();

    [GeneratedRegex(@"\s*\[\*\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"\[hr\]\s*\[/hr\]|\[hr\]", RegexOptions.IgnoreCase)]
    private static partial Regex Rules();

    [GeneratedRegex(@"\[/t[dh]\]", RegexOptions.IgnoreCase)]
    private static partial Regex Cells();

    [GeneratedRegex(@"\[/?(b|i|u|s|strike|spoiler|noparse|code|quote|list|olist|table|tr|td|th|url|h[1-6]|p|center|color|size)(=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex AnyTag();

    // Unity rich text from About.xml: <b>, <i>, <color=#fff>, <size=20>.
    [GeneratedRegex(@"</?(b|i|u|s|color|size|material)(=[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex RichText();

    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
