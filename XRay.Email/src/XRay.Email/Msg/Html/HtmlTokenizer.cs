using System.Net;

namespace XRay.Email.Msg.Html;

internal enum HtmlTokenKind
{
    Text,
    RawText,
    StartTag,
    EndTag,
    Comment,
    Declaration,
}

internal sealed class HtmlAttribute(string name, string? rawValue)
{
    public string Name { get; } = name;

    /// <summary>The attribute value as written, or null for a valueless attribute.</summary>
    public string? RawValue { get; } = rawValue;

    /// <summary>The value with character references decoded.</summary>
    public string? Value { get; } = rawValue is null ? null : WebUtility.HtmlDecode(rawValue);
}

internal sealed class HtmlToken
{
    public HtmlTokenKind Kind { get; init; }

    /// <summary>Text, raw text, comment or declaration source; for a tag, the source of the whole tag.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Lower-case tag name.</summary>
    public string Name { get; init; } = string.Empty;

    public List<HtmlAttribute> Attributes { get; init; } = [];

    public bool SelfClosing { get; init; }

    public string? GetAttribute(string name)
    {
        foreach (var attribute in Attributes)
        {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value ?? string.Empty;
            }
        }

        return null;
    }
}

/// <summary>
/// A forgiving HTML tokenizer: it never throws, treats stray '&lt;' as text, keeps script and style
/// content as one raw token, and makes no attempt to build a tree. Enough to filter and flatten mail HTML.
/// </summary>
internal static class HtmlTokenizer
{
    public static IEnumerable<HtmlToken> Tokenize(string html, CancellationToken cancellationToken = default)
    {
        var position = 0;
        var count = 0;
        while (position < html.Length)
        {
            if ((++count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var lt = html.IndexOf('<', position);
            if (lt < 0)
            {
                yield return Text(html[position..]);
                yield break;
            }

            if (lt > position)
            {
                yield return Text(html[position..lt]);
                position = lt;
            }

            // position is at '<'
            if (position + 1 >= html.Length)
            {
                yield return Text("<");
                yield break;
            }

            var next = html[position + 1];
            if (next == '!')
            {
                if (string.CompareOrdinal(html, position, "<!--", 0, 4) == 0)
                {
                    var end = html.IndexOf("-->", position + 4, StringComparison.Ordinal);
                    var stop = end < 0 ? html.Length : end + 3;
                    yield return new HtmlToken { Kind = HtmlTokenKind.Comment, Text = html[position..stop] };
                    position = stop;
                    continue;
                }

                var close = html.IndexOf('>', position + 2);
                var declarationEnd = close < 0 ? html.Length : close + 1;
                yield return new HtmlToken { Kind = HtmlTokenKind.Declaration, Text = html[position..declarationEnd] };
                position = declarationEnd;
                continue;
            }

            if (next == '?')
            {
                var close = html.IndexOf('>', position + 2);
                var piEnd = close < 0 ? html.Length : close + 1;
                yield return new HtmlToken { Kind = HtmlTokenKind.Declaration, Text = html[position..piEnd] };
                position = piEnd;
                continue;
            }

            var isEnd = next == '/';
            var nameStart = position + (isEnd ? 2 : 1);
            if (nameStart >= html.Length || !char.IsAsciiLetter(html[nameStart]))
            {
                yield return Text("<");
                position++;
                continue;
            }

            var token = ParseTag(html, position, nameStart, isEnd, out var after);
            position = after;
            yield return token;

            if (!isEnd && !token.SelfClosing && (token.Name is "script" or "style"))
            {
                var closing = IndexOfClosingTag(html, token.Name, position);
                var rawEnd = closing < 0 ? html.Length : closing;
                if (rawEnd > position)
                {
                    yield return new HtmlToken { Kind = HtmlTokenKind.RawText, Text = html[position..rawEnd] };
                }

                position = rawEnd;
            }
        }
    }

    private static HtmlToken Text(string text) => new() { Kind = HtmlTokenKind.Text, Text = text };

    private static int IndexOfClosingTag(string html, string name, int from)
    {
        var search = "</" + name;
        var index = from;
        while (true)
        {
            index = html.IndexOf(search, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            var after = index + search.Length;
            if (after >= html.Length || html[after] is '>' or '/' || char.IsWhiteSpace(html[after]))
            {
                return index;
            }

            index = after;
        }
    }

    private static HtmlToken ParseTag(string html, int start, int nameStart, bool isEnd, out int after)
    {
        var index = nameStart;
        while (index < html.Length && !char.IsWhiteSpace(html[index]) && html[index] is not ('/' or '>'))
        {
            index++;
        }

        var name = html[nameStart..index].ToLowerInvariant();
        var attributes = new List<HtmlAttribute>();
        var selfClosing = false;
        while (index < html.Length)
        {
            while (index < html.Length && (char.IsWhiteSpace(html[index]) || html[index] == '/'))
            {
                if (html[index] == '/' && index + 1 < html.Length && html[index + 1] == '>')
                {
                    selfClosing = true;
                }

                index++;
            }

            if (index >= html.Length)
            {
                break;
            }

            if (html[index] == '>')
            {
                index++;
                break;
            }

            var attributeStart = index;
            while (index < html.Length && !char.IsWhiteSpace(html[index]) && html[index] is not ('=' or '>' or '/'))
            {
                index++;
            }

            if (index == attributeStart)
            {
                index++; // a stray character such as a lone '=' or quote
                continue;
            }

            var attributeName = html[attributeStart..index];
            var probe = index;
            while (probe < html.Length && char.IsWhiteSpace(html[probe]))
            {
                probe++;
            }

            string? value = null;
            if (probe < html.Length && html[probe] == '=')
            {
                probe++;
                while (probe < html.Length && char.IsWhiteSpace(html[probe]))
                {
                    probe++;
                }

                if (probe < html.Length && html[probe] is '"' or '\'')
                {
                    var quote = html[probe];
                    var close = html.IndexOf(quote, probe + 1);
                    if (close < 0)
                    {
                        // an unterminated quote must not swallow the rest of the document
                        var gt = html.IndexOf('>', probe + 1);
                        close = gt < 0 ? html.Length : gt;
                        value = html[(probe + 1)..close];
                        index = close;
                    }
                    else
                    {
                        value = html[(probe + 1)..close];
                        index = close + 1;
                    }
                }
                else
                {
                    var valueStart = probe;
                    while (probe < html.Length && !char.IsWhiteSpace(html[probe]) && html[probe] != '>')
                    {
                        probe++;
                    }

                    value = html[valueStart..probe];
                    index = probe;
                }
            }

            attributes.Add(new HtmlAttribute(attributeName, value));
        }

        after = Math.Min(index, html.Length);
        return new HtmlToken
        {
            Kind = isEnd ? HtmlTokenKind.EndTag : HtmlTokenKind.StartTag,
            Text = html[start..after],
            Name = name,
            Attributes = attributes,
            SelfClosing = selfClosing,
        };
    }
}
