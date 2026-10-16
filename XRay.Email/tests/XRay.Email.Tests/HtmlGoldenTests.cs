using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using XRay.Email.Msg;
using XRay.Email.Msg.Html;

namespace XRay.Email.Tests;

/// <summary>
/// The HTML preview of a hand-picked set of fixtures, compared with files under Goldens/html. The
/// goldens are our own output reviewed by eye (this is not a comparison with any other library), so
/// a change to the rendering shows up as a diff to review. Regenerate them after an intended change:
/// <c>XRAY_EMAIL_UPDATE_GOLDENS=1 dotnet test --project XRay.Email/tests/XRay.Email.Tests -c Release -- --filter-class "*HtmlGoldenTests"</c>.
/// </summary>
public sealed class HtmlGoldenTests
{
    private static readonly string[] Fixtures =
    [
        "email/simple_msg.msg",
        "email/sample_email.eml",
        "email/unicode.msg",
        "email/xray-email/related-inline-image.eml",
        "email/xray-email/alternative-text-html.eml",
        "email/xray-email/gb2312-html.eml",
        "email/xray-email/msg/nested-embedded-messages.msg",
        "email/xray-email/html-preview/script-and-handlers.eml",
        "email/xray-email/html-preview/inline-images-cid.eml",
        "email/xray-email/html-preview/plain-text-urls.eml",
        "email/xray-email/html-preview/rtf-encapsulated-html.msg",
        "email/xray-email/html-preview/rtf-formatted.msg",
        "email/msgreader/RtfSampleEmailWithAttachment.msg",
        "email/msgreader/TxtSampleEmailWithAttachment.msg",
        "email/msgreader/EmailWithInnerMailAndAttachments.msg",
    ];

    public static TheoryData<string> Keys() => new(Fixtures);

    private static string GoldenName(string key) => key.Replace('/', '_') + ".html";

    private static string SourceDirectory([CallerFilePath] string callerFile = "") => Path.Combine(Path.GetDirectoryName(callerFile)!, "Goldens", "html");

    [Theory]
    [MemberData(nameof(Keys))]
    public void Preview_matches_the_golden(string key)
    {
        var message = MapiMessage.Load(TestDocuments.RequireFile(key), TestContext.Current.CancellationToken);
        var actual = message.ToHtml(null, TestContext.Current.CancellationToken);

        if (Environment.GetEnvironmentVariable("XRAY_EMAIL_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(SourceDirectory());
            File.WriteAllText(Path.Combine(SourceDirectory(), GoldenName(key)), actual, new UTF8Encoding(false));
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Goldens", "html", GoldenName(key));
        Assert.True(File.Exists(path), $"No golden for '{key}'. Run with XRAY_EMAIL_UPDATE_GOLDENS=1 and review the new file.");
        var expected = File.ReadAllText(path, Encoding.UTF8);
        if (!string.Equals(TestMessages.Lf(expected), TestMessages.Lf(actual), StringComparison.Ordinal))
        {
            var expectedLines = TestMessages.Lf(expected).Split('\n');
            var actualLines = TestMessages.Lf(actual).Split('\n');
            var line = Enumerable.Range(0, Math.Min(expectedLines.Length, actualLines.Length)).FirstOrDefault(index => expectedLines[index] != actualLines[index], Math.Min(expectedLines.Length, actualLines.Length));
            Assert.Fail($"HTML for '{key}' differs from {GoldenName(key)} at line {line + 1}:\n  golden: {Clip(expectedLines.ElementAtOrDefault(line))}\n  actual: {Clip(actualLines.ElementAtOrDefault(line))}");
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Golden_files_hold_the_properties_the_preview_promises(string key)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Goldens", "html", GoldenName(key));
        if (!File.Exists(path))
        {
            Assert.Skip("golden not generated yet");
        }

        var html = File.ReadAllText(path, Encoding.UTF8);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("class=\"xray-mail-header\"", html);
        Assert.Contains("<th>Subject:</th>", html);
        AssertNoActiveContent(html);
    }

    [Fact]
    public void Selected_goldens_show_the_behaviours_they_were_picked_for()
    {
        string Golden(string key) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Goldens", "html", GoldenName(key)), Encoding.UTF8);
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Goldens", "html", GoldenName(Fixtures[0]))))
        {
            Assert.Skip("goldens not generated yet");
        }

        var inline = Golden("email/xray-email/html-preview/inline-images-cid.eml");
        Assert.Contains("data:image/png;base64,", inline);
        Assert.Contains("data:image/gif;base64,", inline);
        Assert.DoesNotContain("src=\"cid:logo", inline);
        Assert.Contains("src=\"cid:missing@example.com\"", inline);

        var script = Golden("email/xray-email/html-preview/script-and-handlers.eml");
        Assert.DoesNotContain("<script", script);
        Assert.DoesNotContain("alert(", script);
        Assert.Contains("a normal link", script);
        Assert.Contains("href=\"https://example.com/report\"", script);

        Assert.Contains("xray-mail-text", Golden("email/xray-email/html-preview/plain-text-urls.eml"));
        Assert.Contains("<ul><li>first item</li>", Golden("email/xray-email/html-preview/rtf-encapsulated-html.msg"));
        Assert.Contains("<table", Golden("email/xray-email/html-preview/rtf-formatted.msg"));
        Assert.Contains("Attachments:", Golden("email/msgreader/TxtSampleEmailWithAttachment.msg"));
        Assert.Contains(".msg (", Golden("email/xray-email/msg/nested-embedded-messages.msg"));
    }

    /// <summary>The same promises over the whole corpus, not only the golden set: nothing in the output may run.</summary>
    public static TheoryData<string> AllFixtures() => new(CorpusManifest.Entries.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_renders_without_active_content(string key)
    {
        var path = TestDocuments.RequireFile(key);
        if (new FileInfo(path).Length > 600 * 1024)
        {
            return;
        }

        var message = MapiMessage.Load(path, TestContext.Current.CancellationToken);
        var html = message.ToHtml(null, TestContext.Current.CancellationToken);

        Assert.Contains("<meta charset=\"utf-8\">", html);
        AssertNoActiveContent(html);
        Assert.Equal(html, message.ToHtml(null, TestContext.Current.CancellationToken));
    }

    private static void AssertNoActiveContent(string html)
    {
        foreach (var token in HtmlTokenizer.Tokenize(html))
        {
            if (token.Kind != HtmlTokenKind.StartTag)
            {
                continue;
            }

            Assert.NotEqual("script", token.Name);
            Assert.DoesNotContain(token.Name, new[] { "iframe", "object", "embed", "applet", "base" });
            foreach (var attribute in token.Attributes)
            {
                Assert.False(attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"event handler {attribute.Name} on <{token.Name}>");
                if (attribute.Value is { } value && attribute.Name is "href" or "src" or "action" or "background")
                {
                    Assert.False(HtmlBodyProcessor.IsDangerousUrl(attribute.Name, value), $"dangerous {attribute.Name}={value[..Math.Min(40, value.Length)]}");
                }
            }
        }

        Assert.DoesNotMatch(new Regex(@"expression\s*\(", RegexOptions.IgnoreCase), html);
    }

    private static string Clip(string? line) => line is null ? "<end>" : line.Length <= 200 ? line : line[..200] + "...";
}
