using System.Net;
using System.Text.RegularExpressions;

namespace XRay.OneNote.Tests.Support;

/// <summary>Just enough HTML reading for structural assertions on the writer's own output.</summary>
internal static partial class HtmlText
{
    public static int Count(string html, string needle)
    {
        var count = 0;
        for (var at = html.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = html.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The visible-ish text of the body: tags dropped, entities decoded, &lt;br&gt; as a newline.</summary>
    public static string BodyText(string html)
    {
        var body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
        body = Brs().Replace(body, "\n");
        body = Tags().Replace(body, string.Empty);
        return WebUtility.HtmlDecode(body);
    }

    /// <summary>Asserts that each text appears in <paramref name="html"/>'s body text in order.</summary>
    public static void AssertInOrder(string html, IEnumerable<string> texts)
    {
        var body = BodyText(html);
        var position = 0;
        foreach (var text in texts)
        {
            var at = body.IndexOf(text, position, StringComparison.Ordinal);
            Assert.True(at >= 0, $"Text not found after offset {position}: '{text}'");
            position = at + text.Length;
        }
    }

    [GeneratedRegex("<br>")]
    private static partial Regex Brs();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
