using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace XRay.Email.Msg.Html;

/// <summary>
/// One pass over mail HTML that can (a) remove what executes, (b) lift the content of a full
/// document so it can sit inside another page, and (c) rewrite references such as <c>cid:</c> URLs.
/// </summary>
internal sealed partial class HtmlBodyProcessor
{
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "action", "formaction", "background", "poster", "cite", "data", "codebase", "longdesc",
        "usemap", "xlink:href", "lowsrc", "dynsrc", "ping", "manifest", "profile", "classid", "archive",
    };

    private static readonly HashSet<string> DroppedWithContent = new(StringComparer.Ordinal)
    {
        "script", "object", "embed", "applet", "iframe", "frame", "frameset", "noscript", "noembed", "noframes",
    };

    private static readonly Regex CssHazard = CssHazardPattern();
    private static readonly Regex CssCidUrl = CssCidUrlPattern();
    private static readonly Regex CssUrl = CssUrlPattern();
    private static readonly Regex AttributeName = new("^[A-Za-z_:][-A-Za-z0-9_:.]*$", RegexOptions.Compiled);

    /// <summary>Drop scripts, event handlers and scripting URLs.</summary>
    public bool Sanitize { get; init; } = true;

    /// <summary>Return only what belongs inside a body; style sheets are hoisted into <see cref="Result.Styles"/>.</summary>
    public bool ExtractBody { get; init; }

    /// <summary>Maps a reference (such as "cid:logo") to a replacement URL, or null to leave it.</summary>
    public Func<string, string?>? ResolveReference { get; init; }

    public sealed record Result(string Body, IReadOnlyList<string> Styles, string? BodyStyle, string? Title);

    public Result Process(string html, CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder(html.Length);
        var styles = new List<string>();
        var title = (string?)null;
        string? bodyStyle = null;
        var inHead = false;
        var inTitle = false;
        var inStyle = false;
        var styleBuffer = new StringBuilder();
        string? skipName = null;
        var skipDepth = 0;

        foreach (var token in HtmlTokenizer.Tokenize(html, cancellationToken))
        {
            if (skipName is not null)
            {
                if (token.Kind == HtmlTokenKind.StartTag && token.Name == skipName && !token.SelfClosing)
                {
                    skipDepth++;
                }
                else if (token.Kind == HtmlTokenKind.EndTag && token.Name == skipName && --skipDepth == 0)
                {
                    skipName = null;
                }

                continue;
            }

            switch (token.Kind)
            {
                case HtmlTokenKind.Comment:
                case HtmlTokenKind.Declaration:
                    // Conditional comments and doctype are layout hints for Outlook, not content.
                    continue;

                case HtmlTokenKind.RawText:
                    if (inStyle)
                    {
                        styleBuffer.Append(token.Text);
                    }

                    continue;

                case HtmlTokenKind.Text:
                    if (inTitle)
                    {
                        title = (title ?? string.Empty) + WebUtility.HtmlDecode(token.Text);
                    }
                    else if (!inHead || !ExtractBody)
                    {
                        output.Append(token.Text);
                    }

                    continue;
            }

            var name = token.Name;
            if (token.Kind == HtmlTokenKind.EndTag)
            {
                if (name == "style" && inStyle)
                {
                    inStyle = false;
                    var css = FinishCss(styleBuffer.ToString());
                    styleBuffer.Clear();
                    if (css.Length > 0)
                    {
                        if (ExtractBody)
                        {
                            styles.Add(css);
                        }
                        else
                        {
                            output.Append("<style>").Append(css).Append("</style>");
                        }
                    }

                    continue;
                }

                if (name == "title")
                {
                    inTitle = false;
                    continue;
                }

                if (name == "head")
                {
                    inHead = false;
                    if (ExtractBody)
                    {
                        continue;
                    }
                }

                if (ExtractBody && name is "html" or "body")
                {
                    continue;
                }

                if (ExtractBody && name is "meta" or "link" or "base")
                {
                    continue;
                }

                output.Append("</").Append(name).Append('>');
                continue;
            }

            // start tag
            if (name == "style")
            {
                inStyle = !token.SelfClosing;
                continue;
            }

            if (name == "title")
            {
                inTitle = !token.SelfClosing;
                continue;
            }

            if (name == "head")
            {
                inHead = true;
                if (ExtractBody)
                {
                    continue;
                }
            }

            if (inHead && name is not ("head" or "meta" or "link" or "style" or "title" or "base" or "script" or "noscript" or "template" or "html"))
            {
                inHead = false; // a head that is never closed ends where the content starts
            }

            if (Sanitize && DroppedWithContent.Contains(name))
            {
                if (!token.SelfClosing && name is not "embed")
                {
                    skipName = name;
                    skipDepth = 1;
                }

                continue;
            }

            if (inHead && ExtractBody)
            {
                continue; // everything in the head other than style sheets stays there
            }

            if (ExtractBody && name is "html" or "meta" or "link" or "base")
            {
                continue;
            }

            if (Sanitize && name == "base")
            {
                continue;
            }

            if (Sanitize && name == "meta" && string.Equals(token.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ExtractBody && name == "body")
            {
                bodyStyle = BuildBodyStyle(token);
                output.Clear(); // anything before <body> was misplaced head content
                continue;
            }

            AppendStartTag(output, token);
        }

        if (inStyle && styleBuffer.Length > 0)
        {
            var css = FinishCss(styleBuffer.ToString());
            if (css.Length > 0)
            {
                if (ExtractBody)
                {
                    styles.Add(css);
                }
                else
                {
                    output.Append("<style>").Append(css).Append("</style>");
                }
            }
        }

        return new Result(output.ToString(), styles, bodyStyle, title?.Trim());
    }

    private string FinishCss(string css)
    {
        css = css.Trim();
        if (css.Length == 0)
        {
            return css;
        }

        // <!-- and --> wrapped around a style sheet are a legacy of browsers that did not know style elements
        css = css.Replace("<!--", string.Empty, StringComparison.Ordinal).Replace("-->", string.Empty, StringComparison.Ordinal).Trim();
        css = ResolveCssUrls(css);
        if (Sanitize)
        {
            css = NeutraliseScriptUrls(css);
            css = CssHazard.Replace(css, "/*removed*/");
            // "</style" cannot occur (the tokenizer ended the block there) but "<" has no business in CSS output either
            css = css.Replace("<", "\\3C ", StringComparison.Ordinal);
        }

        return css;
    }

    private string ResolveCssUrls(string css)
    {
        if (ResolveReference is null)
        {
            return css;
        }

        return CssCidUrl.Replace(css, match =>
        {
            var replacement = ResolveReference(match.Groups[2].Value);
            return replacement is null ? match.Value : $"url({replacement})";
        });
    }

    private string? BuildBodyStyle(HtmlToken body)
    {
        var builder = new StringBuilder();
        if (body.GetAttribute("bgcolor") is { Length: > 0 } background && IsSimpleColor(background))
        {
            builder.Append("background-color:").Append(background.Trim()).Append(';');
        }

        if (body.GetAttribute("text") is { Length: > 0 } text && IsSimpleColor(text))
        {
            builder.Append("color:").Append(text.Trim()).Append(';');
        }

        if (body.GetAttribute("style") is { Length: > 0 } style)
        {
            builder.Append(SanitizeStyleAttribute(style));
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool IsSimpleColor(string value) => ColorPattern().IsMatch(value.Trim());

    /// <summary>Replaces a CSS url() that points at script (or at a text document) with an empty one.</summary>
    private static string NeutraliseScriptUrls(string css) => CssUrl.Replace(css, match =>
    {
        var inner = match.Value[4..^1].Trim().Trim('"', '\'');
        return IsDangerousUrl("background", inner) ? "url()" : match.Value;
    });

    private string SanitizeStyleAttribute(string style)
    {
        style = ResolveCssUrls(style);
        return Sanitize ? CssHazard.Replace(NeutraliseScriptUrls(style), "/*removed*/") : style;
    }

    private void AppendStartTag(StringBuilder output, HtmlToken token)
    {
        output.Append('<').Append(token.Name);
        foreach (var attribute in token.Attributes)
        {
            if (!AttributeName.IsMatch(attribute.Name))
            {
                continue;
            }

            var attributeName = attribute.Name.ToLowerInvariant();
            if (Sanitize && (attributeName.StartsWith("on", StringComparison.Ordinal) || attributeName == "srcdoc"))
            {
                continue;
            }

            var value = attribute.Value;
            if (value is not null)
            {
                if (attributeName == "style")
                {
                    value = SanitizeStyleAttribute(value);
                }
                else if (UrlAttributes.Contains(attributeName))
                {
                    var resolved = ResolveReference?.Invoke(value.Trim());
                    if (resolved is not null)
                    {
                        value = resolved;
                    }
                    else if (Sanitize && IsDangerousUrl(attributeName, value))
                    {
                        continue;
                    }
                }
            }

            output.Append(' ').Append(attributeName);
            if (value is not null)
            {
                output.Append("=\"").Append(EscapeAttribute(value)).Append('"');
            }
        }

        output.Append(token.SelfClosing ? " />" : ">");
    }

    /// <summary>True for a URL a browser would run or navigate to as script or foreign markup.</summary>
    internal static bool IsDangerousUrl(string attributeName, string value)
    {
        var compact = new StringBuilder(value.Length);
        foreach (var ch in WebUtility.HtmlDecode(value))
        {
            if (ch > ' ' && !char.IsWhiteSpace(ch) && ch != '­' && ch != '​')
            {
                compact.Append(char.ToLowerInvariant(ch));
            }
        }

        var url = compact.ToString();
        if (url.StartsWith("javascript:", StringComparison.Ordinal) || url.StartsWith("vbscript:", StringComparison.Ordinal) ||
            url.StartsWith("livescript:", StringComparison.Ordinal) || url.StartsWith("mocha:", StringComparison.Ordinal))
        {
            return true;
        }

        if (url.StartsWith("data:", StringComparison.Ordinal))
        {
            var imageContext = attributeName is "src" or "background" or "lowsrc" or "poster";
            return !(imageContext && url.StartsWith("data:image/", StringComparison.Ordinal));
        }

        return false;
    }

    internal static string EscapeAttribute(string value)
    {
        if (value.AsSpan().IndexOfAny("&\"<>") < 0)
        {
            return value;
        }

        return value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"expression\s*\((?:[^()]|\([^()]*\))*\)|expression\s*\(|javascript\s*:|vbscript\s*:|-moz-binding\s*:|behaviou?r\s*:|@import[^;]*;?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssHazardPattern();

    [GeneratedRegex(@"url\(\s*(?:""[^""]*""|'[^']*'|(?:[^()]|\([^()]*\))*)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrlPattern();

    [GeneratedRegex(@"url\(\s*(['""]?)(cid:[^'"")\s]+)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssCidUrlPattern();

    [GeneratedRegex(@"^(#[0-9a-fA-F]{3,8}|[a-zA-Z]{3,20}|rgb\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\))$")]
    private static partial Regex ColorPattern();
}
