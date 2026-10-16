![X-Ray](https://raw.githubusercontent.com/curiosity-ai/x-ray-others/main/assets/logo.png)

# X-Ray.Email

A .NET 10 library for Outlook `.msg` files, Compound File Binary (CFB) containers and EML/MIME
messages: read them, create and edit them, and convert between MSG and EML. It has no
dependencies outside the .NET base class library and needs no Outlook installation.

Part of the [X-Ray](https://github.com/curiosity-ai/x-ray) family of document libraries.

## Install

```sh
dotnet add package X-Ray.Email
```

## Features

- **MSG**: read and write Outlook messages at two levels. `MapiMessage` exposes subject, bodies
  (plain text, HTML, and RTF decompressed from PR_RTF_COMPRESSED), sender, recipients,
  attachments, embedded messages and arbitrary MAPI properties. `MsgReader`, `MsgWriter` and
  `MsgDocument` expose the raw property streams and storages.
- **EML**: a MIME reader and writer built in. It handles multipart structures, nested
  `message/rfc822` parts, quoted-printable and base64, RFC 2047 encoded words in any charset the
  platform provides, raw UTF-8 headers (RFC 6532), RFC 2231 parameters, address groups and
  comments, and obsolete date syntax. Damaged input degrades instead of failing: missing closing
  boundaries, truncated base64, broken header folding.
- **Conversion**: `MapiMessage.LoadFromEml` turns an EML into a message that saves as MSG;
  `SaveToEml` writes any message as MIME, with non-ASCII headers and file names encoded.
- **Exchange addresses**: `SenderSmtpAddress` and `MapiRecipient.SmtpAddress` resolve the SMTP
  address of senders and recipients that Outlook stores as X.500 names.
- **HTML preview**: `message.ToHtml()` renders one self-contained, deterministic HTML document in
  the layout Outlook displays: a header block (From, Sent, To, Cc, Bcc, Subject, Attachments)
  above the body. Scripts, event handlers and scripting URLs are removed by default, `cid:`
  images are inlined as `data:` URIs, and the output is UTF-8 with the charset declared.
- **Body fallbacks**: `GetBestTextBody()` and `GetBestHtmlBody()` give a body whichever one the
  message stores: HTML, else RTF (the original HTML is recovered from encapsulated-HTML RTF,
  [MS-OXRTFEX]; other RTF is rendered), else plain text.
- **Attachment adapters**: `GetAttachmentsFlattened()` lists attachments as files: safe unique
  names, content type, size, content id, inline flag, `OpenRead()` and `CopyTo`/`CopyToAsync`.
  Embedded messages are named from their subject and serialised on demand as MSG or EML, and
  can be flattened to any depth.
- **Streams and cancellation**: loading reads an MSG from a file or seekable stream on demand
  instead of copying it, and saving writes to a `Stream` sector by sector (MSG) or part by part
  (EML) instead of building the whole file. Every load, save, convert, render and copy method
  takes a `CancellationToken` and stops promptly.
- **CFB**: `CfbReader` and `CfbWriter` read and write compound files (versions 3 and 4)
  independently of MSG.
- **Lenient by default**: anomalies in a damaged MSG (trailing bytes in a property stream,
  counts that do not match) are reported in `ValidationIssues`. Pass `strict: true` to reject
  them instead. Malformed input raises `CfbException` or `MsgException`.

## Quick start

Read a message, whichever format it is in:

```csharp
using XRay.Email.Msg;

var message = path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)
    ? MapiMessage.LoadFromEml(path)
    : MapiMessage.FromFile(path);

Console.WriteLine(message.Subject);
Console.WriteLine($"{message.SenderName} <{message.SenderSmtpAddress}>");
foreach (var recipient in message.Recipients)
{
    Console.WriteLine($"  to: {recipient.DisplayName} <{recipient.SmtpAddress ?? recipient.EmailAddress}>");
}

Console.WriteLine(message.Body ?? message.HtmlBody ?? message.RtfBody);
foreach (var attachment in message.Attachments)
{
    Console.WriteLine(attachment.IsEmbeddedMessage
        ? $"  message: {attachment.EmbeddedMessage?.Subject}"
        : $"  file: {attachment.Filename} ({attachment.Data.Length} bytes)");
}
```

Create a message and save it in both formats:

```csharp
using XRay.Email.Msg;

var message = MapiMessage.Create("Quarterly report", "Please find the report attached.");
message.SenderName = "Alice";
message.SenderEmailAddress = "alice@example.com";
message.AddRecipient("bob@example.com", "Bob");
message.AddRecipient("carol@example.com", "Carol", MapiMessage.RecipientTypeCc);
message.AddAttachment("report.pdf", File.ReadAllBytes("report.pdf"), "application/pdf");

message.Save("report.msg");
message.SaveToEml("report.eml");
```

Preview a message as HTML, read the best body, and store the attachments as files:

```csharp
using XRay.Email.Msg;

using var input = File.OpenRead(path);
var message = MapiMessage.Load(input, cancellationToken);   // MSG or EML, detected from the bytes

await using (var html = File.Create("preview.html"))
{
    message.WriteHtml(html, new MailHtmlOptions { LinkifyPlainText = true }, cancellationToken);
}

string? text = message.GetBestTextBody(cancellationToken);   // plain text, derived if need be
bool unread = !message.IsRead;                               // PR_MESSAGE_FLAGS
DateTime? sent = message.SentTime;                           // UTC

foreach (var attachment in message.GetAttachmentsFlattened(new AttachmentFlattenOptions { MaxDepth = 2 }, cancellationToken))
{
    await using var file = File.Create(Path.Combine(folder, attachment.Name));
    await attachment.CopyToAsync(file, cancellationToken);   // an embedded message is written as .msg or .eml
}
```

### The HTML preview

`ToHtml(MailHtmlOptions?, CancellationToken)` and `WriteHtml(Stream, ...)` pick the body in this
order: the HTML body; the RTF body (encapsulated HTML comes back as the original HTML, plain RTF
is rendered with its character formatting, colours, fields, tables and PNG/JPEG pictures); the
plain text body, escaped and with its whitespace preserved. An RTF that only wraps plain text
defers to the plain text body.

| Option | Default | Effect |
|---|---|---|
| `SanitizeHtml` | on | removes `script`, `iframe`, `object`, `embed`, `applet`, `base`, meta refresh, event handler attributes, `javascript:`/`vbscript:`/`data:` (other than images) URLs, CSS `expression()`, `behavior`, `@import` |
| `EmbedInlineImages` | on | `cid:` references (and an attachment's Content-Location) become `data:` URIs |
| `MaxInlineImageBytes` | 10 MiB | larger images keep their `cid:` reference |
| `IncludeHeader` | on | the From/Sent/To/Cc/Bcc/Subject/Attachments block |
| `ListInlineAttachments` | off | list attachments the body already shows |
| `LinkifyPlainText` | off | link http, https and www addresses in a plain text body |
| `FullDocument` | on | doctype, head and body; off gives a fragment |
| `DateFormat`, `TimeZone`, `ShowTimeZone`, `Culture` | Outlook format, UTC, on, invariant | the Sent line |

The output has no timestamps and does not depend on the machine, so it can be compared in tests.
Sanitising is a filter for display, not a security boundary for untrusted mail: serve the page
from an origin that holds nothing, or in a sandboxed frame.

### Streams

Existing stream-capable members: `MapiMessage.FromStream`, `LoadFromEml(Stream)`, `Save(Stream)`,
`SaveToEml(Stream)`, `AddAttachment(string, Stream)`, `MsgDocument.FromStream`, `CfbReader.FromStream`.
Now:

- `FromFile` and a seekable `FromStream` read sectors on demand; a non-seekable stream is spooled
  to a temporary file (deleted on dispose), never held whole in memory. The parsed message still
  holds its properties and attachment bytes.
- `Save(Stream)`, `SaveToEml(Stream)`, `MsgWriter.Write` and `CfbWriter.Write` write straight to the
  stream: an MSG a sector at a time, an EML with attachments base64-encoded block by block.
  The `byte[]` forms are thin wrappers.
- EML is parsed from one block (decoded parts stay in memory anyway), so `LoadFromEml` reads the
  stream into one exactly sized array.
- `MapiMessage.Load(Stream|path)` detects MSG or EML.

Convert an EML file to MSG:

```csharp
MapiMessage.LoadFromEml("message.eml").Save("message.msg");
```

Walk the CFB structure of any compound file:

```csharp
using XRay.Email.Cfb;

using var cfb = CfbReader.FromFile("message.msg");
foreach (var (depth, entry) in cfb.IterTree())
{
    Console.WriteLine($"{new string(' ', depth * 2)}{entry.Name} ({entry.StreamSize} bytes)");
}
```

## Scope

X-Ray.Email works on files and streams. It does not talk to mail servers (no IMAP, POP3 or
SMTP), does not read PST/OST mailboxes, and has no dedicated calendar or contact API (those MAPI
properties are reachable through `GetPropertyValue`). **S/MIME** (signed and encrypted messages,
`smime.p7m`) and **TNEF** (`winmail.dat`) are not supported: such a message shows whatever body
it carries and the opaque part as an attachment. `RtfBody` returns the RTF itself;
`GetBestHtmlBody` and `ToHtml` are the converted forms.

## License

MIT. X-Ray.Email is derived from an MIT-licensed open-source C# MSG/EML toolkit; its licence
and copyright notice are in `LICENSE`, which ships in the package.
