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

return 0;

void Write(MapiMessage message, string fileName)
{
    var path = Path.Combine(output, fileName);
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
