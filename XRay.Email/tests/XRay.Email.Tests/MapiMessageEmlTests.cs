using System.Text;
using XRay.Email.Msg;
using Xunit;

namespace XRay.Email.Tests;

public sealed class MapiMessageEmlTests
{
    [Fact]
    public void LoadFromEmlParsesPlainTextHeadersAndBody()
    {
        var eml = """
From: Alice <alice@example.com>
To: Bob <bob@example.com>
Subject: Plain
Message-ID: <plain@example.com>
Date: Tue, 02 Jan 2024 03:04:05 +0000
Content-Type: text/plain; charset=utf-8

Hello from EML.
"""u8.ToArray();

        var message = MapiMessage.LoadFromEml(eml, TestContext.Current.CancellationToken);

        Assert.Equal("Plain", message.Subject);
        Assert.Equal("alice@example.com", message.SenderEmailAddress);
        Assert.Equal("Alice", message.SenderName);
        Assert.Equal("<plain@example.com>", message.InternetMessageId);
        Assert.Equal("Hello from EML.", message.Body?.Trim());
        Assert.Single(message.Recipients);
        Assert.Equal("bob@example.com", message.Recipients[0].EmailAddress);
    }

    [Fact]
    public void LoadFromEmlHandlesFoldedHeadersAndBase64Text()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("Base64 body"));
        var eml = $"""
From: Alice <alice@example.com>
To: Bob <bob@example.com>,
 Carol <carol@example.com>
Subject: Folded
Content-Type: text/plain; charset=utf-8
Content-Transfer-Encoding: base64

{body}
""";

        var message = MapiMessage.LoadFromEml(Encoding.ASCII.GetBytes(eml.Replace("\n", "\r\n", StringComparison.Ordinal)), TestContext.Current.CancellationToken);

        Assert.Equal("Folded", message.Subject);
        Assert.Equal("Base64 body", message.Body);
        Assert.Equal(2, message.Recipients.Count);
        Assert.Equal("carol@example.com", message.Recipients[1].EmailAddress);
    }

    [Fact]
    public void LoadFromEmlHandlesMultipartRelatedInlineAttachment()
    {
        var imagePayload = Convert.ToBase64String("png"u8.ToArray());
        var eml = $"""
From: Alice <alice@example.com>
To: Bob <bob@example.com>
Subject: Inline
MIME-Version: 1.0
Content-Type: multipart/related; boundary="outer"

--outer
Content-Type: text/html; charset=utf-8
Content-Transfer-Encoding: quoted-printable

<html><body><img src=3D"cid:inline-1"></body></html>
--outer
Content-Type: image/png; name="inline.png"
Content-Transfer-Encoding: base64
Content-Disposition: inline; filename="inline.png"
Content-ID: <inline-1>

{imagePayload}
--outer--
""";

        var message = MapiMessage.LoadFromEml(Encoding.ASCII.GetBytes(eml.Replace("\n", "\r\n", StringComparison.Ordinal)), TestContext.Current.CancellationToken);

        Assert.Contains("cid:inline-1", message.HtmlBody);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("inline.png", attachment.Filename);
        Assert.Equal("inline-1", attachment.ContentId);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal("png"u8.ToArray(), attachment.Data);
    }

    [Fact]
    public void SaveToEmlRoundTripsBodiesAndAttachments()
    {
        var message = MapiMessage.Create("RoundTrip", "Plain body");
        message.SenderName = "Alice";
        message.SenderEmailAddress = "alice@example.com";
        message.AddRecipient("bob@example.com", "Bob");
        message.HtmlBody = "<p>Plain body</p>";
        message.AddAttachment("note.txt", "abc"u8.ToArray(), "text/plain");
        message.Attachments.Add(new MapiAttachment
        {
            Filename = "inline.png",
            MimeType = "image/png",
            ContentId = "cid-1",
            Data = "img"u8.ToArray(),
        });

        var reloaded = MapiMessage.LoadFromEml(message.SaveToEml(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal("RoundTrip", reloaded.Subject);
        Assert.Equal("alice@example.com", reloaded.SenderEmailAddress);
        Assert.Equal("Plain body", reloaded.Body);
        Assert.Equal("<p>Plain body</p>", reloaded.HtmlBody);
        Assert.Equal(2, reloaded.Attachments.Count);
        Assert.Contains(reloaded.Attachments, item => item.Filename == "note.txt" && item.Data.SequenceEqual("abc"u8.ToArray()));
        Assert.Contains(reloaded.Attachments, item => item.ContentId == "cid-1" && item.Data.SequenceEqual("img"u8.ToArray()));
    }

    [Fact]
    public void CanLoadAndSaveEmlUsingStreams()
    {
        var eml = """
From: Alice <alice@example.com>
To: Bob <bob@example.com>
Subject: Stream EML
Content-Type: text/plain; charset=utf-8

Hello stream.
"""u8.ToArray();

        using var input = new MemoryStream(eml);
        var message = MapiMessage.LoadFromEml(input, TestContext.Current.CancellationToken);

        Assert.Equal("Stream EML", message.Subject);

        using var output = new MemoryStream();
        message.SaveToEml(output, TestContext.Current.CancellationToken);
        output.Position = 0;

        var roundTripped = MapiMessage.LoadFromEml(output, TestContext.Current.CancellationToken);
        Assert.Equal("Stream EML", roundTripped.Subject);
        Assert.Equal("Hello stream.", roundTripped.Body?.Trim());
    }

    [Fact]
    public void LoadFromEmlHandlesQuotedPrintableHtml()
    {
        var eml = """
From: Alice <alice@example.com>
To: Bob <bob@example.com>
Subject: Html
MIME-Version: 1.0
Content-Type: text/html; charset=utf-8
Content-Transfer-Encoding: quoted-printable

<p>line=20one</p>
"""u8.ToArray();

        var message = MapiMessage.LoadFromEml(eml, TestContext.Current.CancellationToken);

        Assert.Equal("<p>line one</p>", message.HtmlBody);
        Assert.True(string.IsNullOrEmpty(message.Body));
    }
}
