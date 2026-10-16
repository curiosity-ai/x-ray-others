// Writes the generated MSG fixtures of test_documents/email/xray-email/msg/.
//
//   dotnet run --project XRay.Email/tools/XRay.Email.CorpusGen -c Release -- <test_documents>/email/xray-email
//
// Part of them are EML fixtures from the same folder converted to MSG by this library; the rest are
// built through the API to cover what EML cannot express (ANSI code pages, CFB version 4, MAPI-only
// properties). Every output is deterministic, and every one is cross-checked against an
// independent MSG reader when the expectation manifest is rebuilt (see tools/corpus/).
using System.Text;
using XRay.Email.Msg;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: XRay.Email.CorpusGen <xray-email folder>");
    return 1;
}

var root = Path.GetFullPath(args[0]);
var output = Path.Combine(root, "msg");
Directory.CreateDirectory(output);

string[] converted =
[
    "plain-ascii", "alternative-text-html", "mixed-with-attachments", "related-inline-image",
    "nested-rfc822", "headers-utf8-split-words", "rfc2231-filenames", "address-groups-and-comments",
    "calendar-invite", "koi8r-body-and-subject",
];

foreach (var name in converted)
{
    var message = MapiMessage.LoadFromEml(Path.Combine(root, $"{name}.eml"));
    Write(message, $"{name}.msg");
}

// ANSI (non-Unicode) MSG whose PtypString8 values are in code page 1251.
var ansi = MapiMessage.Create(unicodeStrings: false);
ansi.SetProperty(0x3FFD, (ushort)PropertyTypeCode.PtypInteger32, 1251);
ansi.Subject = "Отчёт за квартал";
ansi.Body = "Добрый день! Отчёт во вложении.";
ansi.SenderName = "Иван Петров";
ansi.SenderEmailAddress = "ivan@example.ru";
ansi.SenderAddressType = "SMTP";
ansi.InternetMessageId = "<ansi-1251@example.ru>";
ansi.MessageDeliveryTime = new DateTime(2024, 2, 1, 9, 30, 0, DateTimeKind.Utc);
ansi.AddRecipient("olga@example.ru", "Ольга Смирнова");
ansi.AddAttachment("отчёт.txt", Encoding.UTF8.GetBytes("квартал 1: 100\n"), "text/plain");
Write(ansi, "ansi-codepage-1251.msg");

// CFB major version 4 (4096-byte sectors) with an attachment that needs regular sectors.
var v4 = MapiMessage.Create("CFB version 4 container", "This message uses 4096-byte sectors.");
v4.MajorVersion = 4;
v4.SenderName = "Alice";
v4.SenderEmailAddress = "alice@example.com";
v4.InternetMessageId = "<cfb-v4@example.com>";
v4.MessageDeliveryTime = new DateTime(2024, 2, 2, 10, 0, 0, DateTimeKind.Utc);
v4.AddRecipient("bob@example.com", "Bob");
v4.AddAttachment("pattern.bin", Pattern(70_000), "application/octet-stream");
Write(v4, "cfb-version-4.msg");

// Many recipients of each type, multi-valued properties, transport headers and an HTML body.
var rich = MapiMessage.Create("Recipients, headers and properties", "Plain body of the rich message.");
rich.HtmlBody = "<html><body><p>HTML body of the <i>rich</i> message.</p></body></html>";
rich.SenderName = "Build Agent";
rich.SenderEmailAddress = "build.agent@example.com";
rich.InternetMessageId = "<rich@example.com>";
rich.MessageDeliveryTime = new DateTime(2024, 2, 3, 11, 0, 0, DateTimeKind.Utc);
rich.TransportMessageHeaders = "Received: from mx.example.com\r\nX-Workflow: corpus-generator\r\n";
for (var index = 1; index <= 5; index++)
{
    rich.AddRecipient($"to{index}@example.com", $"To Recipient {index}");
}

rich.AddRecipient("cc1@example.com", "Cc Recipient", MapiMessage.RecipientTypeCc);
rich.AddRecipient("bcc1@example.com", "Bcc Recipient", MapiMessage.RecipientTypeBcc);
rich.SetProperty(0x9000, (ushort)PropertyTypeCode.PtypMultipleString, new[] { "alpha", "beta", "γάμμα" });
rich.SetProperty(0x9001, (ushort)PropertyTypeCode.PtypMultipleBinary, new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5 } });
rich.SetProperty(0x9002, (ushort)PropertyTypeCode.PtypInteger64, 1234567890123L);
rich.SetProperty(0x9003, (ushort)PropertyTypeCode.PtypBoolean, true);
rich.SetProperty(0x9004, (ushort)PropertyTypeCode.PtypFloating64, 3.25);
Write(rich, "recipients-and-properties.msg");

// Two levels of embedded messages, each with its own attachment.
var level2 = MapiMessage.Create("Level 2", "Innermost message.");
level2.SenderEmailAddress = "level2@example.com";
level2.AddAttachment("inner.txt", Encoding.UTF8.GetBytes("innermost attachment"), "text/plain");
var level1 = MapiMessage.Create("Level 1", "Middle message.");
level1.SenderEmailAddress = "level1@example.com";
level1.AddEmbeddedMessageAttachment(level2, "level2.msg");
var top = MapiMessage.Create("Nested embedded messages", "Outer message with a message inside a message.");
top.SenderEmailAddress = "top@example.com";
top.AddRecipient("bob@example.com", "Bob");
top.AddEmbeddedMessageAttachment(level1, "level1.msg");
top.AddAttachment("outer.txt", Encoding.UTF8.GetBytes("outer attachment"), "text/plain");
Write(top, "nested-embedded-messages.msg");

// Messages whose only body is RTF, for the HTML preview tests (written to html-preview/).
var preview = Path.Combine(root, "html-preview");
Directory.CreateDirectory(preview);

var encapsulated = MapiMessage.Create("Release notes (encapsulated HTML)", unicodeStrings: true);
encapsulated.SenderName = "Release Bot";
encapsulated.SenderEmailAddress = "release.bot@example.com";
encapsulated.SenderAddressType = "SMTP";
encapsulated.InternetMessageId = "<rtf-encapsulated@example.com>";
encapsulated.MessageDeliveryTime = new DateTime(2024, 3, 4, 9, 15, 0, DateTimeKind.Utc);
encapsulated.AddRecipient("team@example.com", "Team");
encapsulated.SetProperty(0x1009, (ushort)PropertyTypeCode.PtypBinary, UncompressedRtf(RtfSamples.EncapsulatedHtmlRtf));
Write(encapsulated, "rtf-encapsulated-html.msg", preview);

var formatted = MapiMessage.Create("Formatted RTF body", unicodeStrings: true);
formatted.SenderName = "Word Writer";
formatted.SenderEmailAddress = "word.writer@example.com";
formatted.SenderAddressType = "SMTP";
formatted.InternetMessageId = "<rtf-formatted@example.com>";
formatted.MessageDeliveryTime = new DateTime(2024, 3, 5, 14, 40, 0, DateTimeKind.Utc);
formatted.AddRecipient("reader@example.com", "Reader");
formatted.SetProperty(0x1009, (ushort)PropertyTypeCode.PtypBinary, UncompressedRtf(RtfSamples.FormattedRtf));
Write(formatted, "rtf-formatted.msg", preview);

return 0;

// PR_RTF_COMPRESSED in the uncompressed form of MS-OXRTFCP ("MELA", CRC zero).
static byte[] UncompressedRtf(string rtf)
{
    var raw = Encoding.Latin1.GetBytes(rtf);
    var data = new byte[16 + raw.Length];
    BitConverter.TryWriteBytes(data.AsSpan(0, 4), raw.Length + 12);
    BitConverter.TryWriteBytes(data.AsSpan(4, 4), raw.Length);
    BitConverter.TryWriteBytes(data.AsSpan(8, 4), 0x414C454Du);
    raw.CopyTo(data, 16);
    return data;
}

void Write(MapiMessage message, string fileName, string? folder = null)
{
    var path = Path.Combine(folder ?? output, fileName);
    message.Save(path);
    Console.WriteLine($"{fileName}: {new FileInfo(path).Length} bytes");
}

static byte[] Pattern(int length)
{
    var data = new byte[length];
    for (var index = 0; index < length; index++)
    {
        data[index] = (byte)((index * 31) ^ (index >> 8));
    }

    return data;
}

internal static class RtfSamples
{
    public const string EncapsulatedHtmlRtf = """
{\rtf1\ansi\ansicpg1252\fromhtml1 \fbidis \deff0{\fonttbl{\f0\fswiss\fcharset0 Arial;}{\f1\fmodern Courier New;}}
{\colortbl\red0\green0\blue0;\red51\green102\blue153;}
\uc1\pard\plain\deftab360 \f0\fs24
{\*\htmltag19 <html>}{\*\htmltag34 <head>}{\*\htmltag64 <meta charset="windows-1252">}{\*\htmltag241 </head>}
{\*\htmltag64 <body>}
{\*\htmltag64 <h1 style="color:#336699">}\htmlrtf {\b\fs36 \htmlrtf0 Release notes{\*\htmltag72 </h1>}\htmlrtf \b0\fs24\par}\htmlrtf0
{\*\htmltag64 <p>}\htmlrtf {\htmlrtf0 Caf\'e9 cr\'e8me \u8212? prices from \u8364?5 and up {\*\htmltag64 &amp;}\htmlrtf \'26\htmlrtf0  more.\htmlrtf  \htmlrtf0
{\*\htmltag84 <a href="https://example.com/notes">}\htmlrtf {\field{\*\fldinst{HYPERLINK "https://example.com/notes"}}{\fldrslt\htmlrtf0 full notes}}\htmlrtf0 {\*\htmltag92 </a>}{\*\htmltag72 </p>}\htmlrtf \par}\htmlrtf0
{\*\htmltag64 <ul>}{\*\htmltag64 <li>}\htmlrtf {\htmlrtf0 first item{\*\htmltag72 </li>}\htmlrtf \par}\htmlrtf0
{\*\htmltag64 <li>}\htmlrtf {\htmlrtf0 second item with {\*\htmltag64 <b>}\htmlrtf {\b \htmlrtf0 bold text{\*\htmltag72 </b>}\htmlrtf \b0}\htmlrtf0 {\*\htmltag72 </li>}\htmlrtf \par}\htmlrtf0
{\*\htmltag72 </ul>}{\*\htmltag241 </body>}{\*\htmltag241 </html>}}
""";

    public const string FormattedRtf = """
{\rtf1\ansi\ansicpg1252\deff0\nouicompat{\fonttbl{\f0\fnil\fcharset0 Calibri;}{\f1\fnil\fcharset0 Courier New;}}
{\colortbl ;\red255\green0\blue0;\red0\green0\blue255;}
{\*\generator Riched20 10.0.19041}\viewkind4\uc1
\pard\sa200\sl276\slmult1\f0\fs22\lang9 Plain start, \b bold\b0 , \i italic\i0 , \ul underlined\ulnone , \cf1 red text\cf0 , \strike struck\strike0  and x\super 2\nosupersub .\par
\pard\qc\fs28 Centered heading\par
\pard\f1\fs20 Monospaced line with \u8364? euro and caf\'e9 and \u1055?\u1088?\u1080?\u1074?\u1077?\u1090?.\par
\pard\f0\fs22 Visit {\field{\*\fldinst{HYPERLINK "https://example.com/rtf"}}{\fldrslt{\cf2\ul the RTF page}}} for details.\par
\trowd\trgaph108\cellx3000\cellx6000
\pard\intbl Name\cell Value\cell\row
\trowd\trgaph108\cellx3000\cellx6000
\pard\intbl Alpha\cell 1\cell\row
\pard\par
Last paragraph.\par
}
""";
}
