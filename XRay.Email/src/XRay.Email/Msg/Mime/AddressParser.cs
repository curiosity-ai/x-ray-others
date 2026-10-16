using System.Text;

namespace XRay.Email.Msg.Mime;

internal readonly record struct MimeMailbox(string? DisplayName, string Address);

/// <summary>
/// RFC 5322 address lists: mailboxes, groups (<c>Team: a@x, b@x;</c>), quoted display names with
/// escapes, comments, and RFC 2047 encoded words in display names. Parse from the raw header value so
/// a decoded display name that contains a comma cannot split a mailbox.
/// </summary>
internal static class AddressParser
{
    public static IReadOnlyList<MimeMailbox> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var items = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var angleDepth = 0;
        var commentDepth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '\\' && (inQuotes || commentDepth > 0) && index + 1 < value.Length)
            {
                current.Append(ch).Append(value[++index]);
                continue;
            }

            if (commentDepth > 0)
            {
                commentDepth += ch switch { '(' => 1, ')' => -1, _ => 0 };
                current.Append(ch);
                continue;
            }

            if (inQuotes)
            {
                inQuotes = ch != '"';
                current.Append(ch);
                continue;
            }

            if (ch == '=' && index + 1 < value.Length && value[index + 1] == '?' &&
                value.IndexOf("?=", index + 2, StringComparison.Ordinal) is var end and > 0 &&
                value.AsSpan(index, end - index).IndexOfAny(' ', '\t') < 0)
            {
                // An encoded word is one token: ':' or ',' inside it is text, not syntax.
                current.Append(value, index, end + 2 - index);
                index = end + 1;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    current.Append(ch);
                    break;
                case '(':
                    commentDepth = 1;
                    current.Append(ch);
                    break;
                case '<':
                    angleDepth++;
                    current.Append(ch);
                    break;
                case '>':
                    angleDepth = Math.Max(0, angleDepth - 1);
                    current.Append(ch);
                    break;
                case ':' when angleDepth == 0:
                    // A group display name ("undisclosed-recipients:") names no mailbox.
                    current.Clear();
                    break;
                case ',' or ';' when angleDepth == 0:
                    items.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
        {
            items.Add(current.ToString());
        }

        var mailboxes = new List<MimeMailbox>(items.Count);
        foreach (var item in items)
        {
            var mailbox = ParseSingle(item);

            // An encoded word is only ever a display name; as a bare "address" it is garbage.
            if (!string.IsNullOrWhiteSpace(mailbox.Address) && !mailbox.Address.Contains("=?", StringComparison.Ordinal))
            {
                mailboxes.Add(mailbox);
            }
        }

        return mailboxes;
    }

    /// <summary>Formats mailboxes for a header, encoding non-ASCII display names as RFC 2047 words.</summary>
    public static string FormatList(IEnumerable<MimeMailbox> mailboxes)
    {
        return string.Join(", ", mailboxes.Select(FormatSingle));
    }

    public static string FormatSingle(MimeMailbox mailbox)
    {
        if (string.IsNullOrWhiteSpace(mailbox.DisplayName) || string.Equals(mailbox.DisplayName, mailbox.Address, StringComparison.OrdinalIgnoreCase))
        {
            return mailbox.Address;
        }

        return $"{FormatDisplayName(mailbox.DisplayName)} <{mailbox.Address}>";
    }

    private static MimeMailbox ParseSingle(string value)
    {
        var comments = new StringBuilder();
        var phrase = new StringBuilder();
        string? address = null;
        var inQuotes = false;
        var commentDepth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '\\' && (inQuotes || commentDepth > 0) && index + 1 < value.Length)
            {
                (commentDepth > 0 ? comments : phrase).Append(value[++index]);
                continue;
            }

            if (commentDepth > 0)
            {
                if (ch == '(')
                {
                    commentDepth++;
                }
                else if (ch == ')')
                {
                    commentDepth--;
                    if (commentDepth == 0)
                    {
                        comments.Append(' ');
                        continue;
                    }
                }

                comments.Append(ch);
                continue;
            }

            if (inQuotes)
            {
                if (ch == '"')
                {
                    inQuotes = false;
                    continue;
                }

                phrase.Append(ch);
                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == '(')
            {
                commentDepth = 1;
            }
            else if (ch == '<' && address is null)
            {
                var close = value.IndexOf('>', index + 1);
                address = close < 0 ? value[(index + 1)..] : value[(index + 1)..close];
                index = close < 0 ? value.Length : close;
            }
            else
            {
                phrase.Append(ch);
            }
        }

        string? displayName;
        if (address is null)
        {
            // addr-spec form: "alice@example.com (Alice)" - the comment is the conventional name.
            address = phrase.ToString();
            displayName = comments.ToString();
        }
        else
        {
            displayName = phrase.ToString();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = comments.ToString();
            }
        }

        address = NormalizeAddress(address);
        displayName = CollapseWhitespace(HeaderParser.DecodeEncodedWords(displayName.Trim()));
        return new MimeMailbox(string.IsNullOrEmpty(displayName) ? null : displayName, address);
    }

    private static string NormalizeAddress(string address)
    {
        var trimmed = address.Trim();

        // Obsolete source route: <@relay1,@relay2:user@example.com>
        if (trimmed.StartsWith('@') && trimmed.IndexOf(':') is var colon and > 0)
        {
            trimmed = trimmed[(colon + 1)..];
        }

        if (!trimmed.Contains('@'))
        {
            return trimmed; // not an addr-spec (an X.500 name, a bare local name): keep it as written
        }

        // Obsolete syntax allows whitespace around the parts of an addr-spec ("john . doe @ example.com").
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (!char.IsWhiteSpace(ch) && ch != '"')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousSpace = true;
                continue;
            }

            builder.Append(ch);
            previousSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDisplayName(string displayName)
    {
        if (displayName.Any(ch => ch > 0x7E || ch < 0x20))
        {
            return TransferEncodingEncoder.EncodeWords(displayName);
        }

        if (displayName.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '/' or '=' or '?' or '^' or '_' or '`' or '{' or '|' or '}' or '~') &&
            !displayName.Contains("=?", StringComparison.Ordinal))
        {
            return displayName;
        }

        return $"\"{displayName.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
