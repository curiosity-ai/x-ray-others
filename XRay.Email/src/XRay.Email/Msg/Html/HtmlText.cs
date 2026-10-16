using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace XRay.Email.Msg.Html;

/// <summary>Conversions between HTML and plain text used for body fallbacks.</summary>
internal static partial class HtmlText
{
    private static readonly Regex Whitespace = WhitespacePattern();
    private static readonly Regex Url = UrlPattern();

    /// <summary>
    /// Plain text from HTML: tags removed, character references decoded, paragraphs and line breaks
    /// kept, list items bulleted, table cells tab-separated. Line breaks are CRLF, as in a MAPI body.
    /// </summary>
    public static string ToText(string html, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder(html.Length / 2);
        var lists = new Stack<ListState>();
        var skipName = (string?)null;
        var preDepth = 0;

        foreach (var token in HtmlTokenizer.Tokenize(html, cancellationToken))
        {
            if (skipName is not null)
            {
                if (token.Kind == HtmlTokenKind.EndTag && token.Name == skipName)
                {
                    skipName = null;
                }

                continue;
            }

            switch (token.Kind)
            {
                case HtmlTokenKind.RawText:
                case HtmlTokenKind.Comment:
                case HtmlTokenKind.Declaration:
                    continue;

                case HtmlTokenKind.Text:
                    AppendText(builder, token.Text, preDepth > 0);
                    continue;

                case HtmlTokenKind.StartTag:
                    switch (token.Name)
                    {
                        case "head" or "title" or "script" or "style" or "template" when !token.SelfClosing:
                            skipName = token.Name;
                            break;
                        case "br":
                            builder.Append('\n');
                            break;
                        case "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "address" or "fieldset":
                            EnsureBreak(builder, 2);
                            break;
                        case "div" or "section" or "article" or "header" or "footer" or "nav" or "aside" or "main" or "form" or "dl" or "dt" or "dd" or "caption" or "tr":
                            EnsureBreak(builder, 1);
                            break;
                        case "table":
                            EnsureBreak(builder, 1);
                            break;
                        case "pre":
                            EnsureBreak(builder, 1);
                            preDepth++;
                            break;
                        case "hr":
                            EnsureBreak(builder, 1);
                            builder.Append("----------");
                            EnsureBreak(builder, 1);
                            break;
                        case "ul" or "ol":
                            EnsureBreak(builder, lists.Count == 0 ? 2 : 1);
                            lists.Push(new ListState(token.Name == "ol"));
                            break;
                        case "li":
                            EnsureBreak(builder, 1);
                            builder.Append(' ', Math.Max(0, lists.Count - 1) * 2);
                            if (lists.Count > 0 && lists.Peek() is { } list)
                            {
                                builder.Append(list.Ordered ? (++list.Counter).ToString(CultureInfo.InvariantCulture) + ". " : "• ");
                            }
                            else
                            {
                                builder.Append("• ");
                            }

                            break;
                    }

                    continue;

                case HtmlTokenKind.EndTag:
                    switch (token.Name)
                    {
                        case "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "address" or "fieldset" or "pre":
                            EnsureBreak(builder, 2);
                            if (token.Name == "pre" && preDepth > 0)
                            {
                                preDepth--;
                            }

                            break;
                        case "div" or "section" or "article" or "header" or "footer" or "nav" or "aside" or "main" or "form" or "dl" or "dt" or "dd" or "caption" or "table" or "li":
                            EnsureBreak(builder, 1);
                            break;
                        case "tr":
                            TrimTrailingTabs(builder);
                            EnsureBreak(builder, 1);
                            break;
                        case "td" or "th":
                            if (builder.Length > 0 && builder[^1] != '\n' && builder[^1] != '\t')
                            {
                                builder.Append('\t');
                            }

                            break;
                        case "ul" or "ol":
                            if (lists.Count > 0)
                            {
                                lists.Pop();
                            }

                            EnsureBreak(builder, lists.Count == 0 ? 2 : 1);
                            break;
                    }

                    continue;
            }
        }

        return Normalize(builder.ToString());
    }

    private sealed class ListState(bool ordered)
    {
        public bool Ordered { get; } = ordered;

        public int Counter { get; set; }
    }

    private static void AppendText(StringBuilder builder, string raw, bool preformatted)
    {
        var text = WebUtility.HtmlDecode(raw);
        if (preformatted)
        {
            builder.Append(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
            return;
        }

        text = Whitespace.Replace(text, " ");
        if (text.Length == 0)
        {
            return;
        }

        if (builder.Length == 0 || builder[^1] is '\n' or ' ' or '\t')
        {
            text = text.TrimStart(' ');
        }

        builder.Append(text.Replace(' ', ' '));
    }

    private static void TrimTrailingTabs(StringBuilder builder)
    {
        while (builder.Length > 0 && builder[^1] is '\t' or ' ')
        {
            builder.Length--;
        }
    }

    private static void EnsureBreak(StringBuilder builder, int newlines)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var trailing = 0;
        for (var i = builder.Length - 1; i >= 0 && builder[i] == '\n'; i--)
        {
            trailing++;
        }

        for (; trailing < newlines; trailing++)
        {
            builder.Append('\n');
        }
    }

    private static string Normalize(string text)
    {
        var lines = text.Split('\n');
        var result = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd(' ', '\t');
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (result.Length > 0)
            {
                result.Append("\r\n");
                if (blank > 0)
                {
                    result.Append("\r\n");
                }
            }

            blank = 0;
            result.Append(line);
        }

        return result.ToString();
    }

    /// <summary>Escapes text for HTML content: &amp;, &lt;, &gt; and the quote characters.</summary>
    public static string Escape(string text)
    {
        if (text.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&#39;"); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Escapes plain text for HTML, optionally turning http, https and www addresses into links.</summary>
    public static string PlainToHtml(string text, bool linkify)
    {
        if (!linkify)
        {
            return Escape(text);
        }

        var builder = new StringBuilder(text.Length + 64);
        var position = 0;
        foreach (Match match in Url.Matches(text))
        {
            var value = match.Value;
            var trimmed = value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"');
            if (trimmed.Length < 8)
            {
                continue;
            }

            builder.Append(Escape(text[position..match.Index]));
            var href = trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + trimmed : trimmed;
            builder.Append("<a href=\"").Append(HtmlBodyProcessor.EscapeAttribute(href)).Append("\" rel=\"noopener noreferrer\">").Append(Escape(trimmed)).Append("</a>");
            position = match.Index + trimmed.Length;
        }

        builder.Append(Escape(text[position..]));
        return builder.ToString();
    }

    [GeneratedRegex(@"[ \t\r\n\f]+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}
