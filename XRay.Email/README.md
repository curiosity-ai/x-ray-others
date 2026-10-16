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
SMTP), does not read PST/OST mailboxes or TNEF (`winmail.dat`), and has no dedicated calendar
or contact API (those MAPI properties are reachable through `GetPropertyValue`). HTML is not
derived from RTF bodies; `RtfBody` returns the RTF itself.

## License

MIT. X-Ray.Email is derived from an MIT-licensed open-source C# MSG/EML toolkit; its licence
and copyright notice are in `LICENSE`, which ships in the package.
