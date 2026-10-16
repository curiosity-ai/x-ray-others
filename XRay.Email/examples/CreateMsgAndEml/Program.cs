using System.Text;
using XRay.Email.Msg;

var msgPath = GetOption(args, "--msg-path") ?? "example-message.msg";
var emlPath = GetOption(args, "--eml-path") ?? "example-message.eml";

var message = MapiMessage.Create(
    "Quarterly status update and rollout plan",
    "Hello team,\n\nPlease find the latest rollout summary attached.\n\nRegards,\nEngineering");

message.SetProperty(
    (ushort)CommonMessagePropertyId.SenderName,
    (ushort)PropertyTypeCode.PtypString,
    "Build Agent");
message.SetProperty(
    (ushort)CommonMessagePropertyId.SenderEmailAddress,
    (ushort)PropertyTypeCode.PtypString,
    "build.agent@example.com");
message.SetProperty(
    (ushort)CommonMessagePropertyId.InternetMessageId,
    (ushort)PropertyTypeCode.PtypString,
    "<example-message-001@example.com>");
message.SetProperty(
    (ushort)CommonMessagePropertyId.MessageDeliveryTime,
    (ushort)PropertyTypeCode.PtypTime,
    new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc));
message.SetProperty(
    (ushort)CommonMessagePropertyId.DisplayTo,
    (ushort)PropertyTypeCode.PtypString,
    "Alice Example; Bob Example");
message.SetProperty(
    (ushort)CommonMessagePropertyId.DisplayCc,
    (ushort)PropertyTypeCode.PtypString,
    "Carol Example");
message.SetProperty(
    (ushort)CommonMessagePropertyId.DisplayBcc,
    (ushort)PropertyTypeCode.PtypString,
    "Ops Archive");
message.SetProperty(
    (ushort)CommonMessagePropertyId.TransportMessageHeaders,
    (ushort)PropertyTypeCode.PtypString,
    "X-Environment: example\r\nX-Workflow: create-msg-and-eml\r\n");

message.AddRecipient("alice@example.com", "Alice Example");
message.AddRecipient("bob@example.com", "Bob Example");
message.AddRecipient("carol@example.com", "Carol Example", MapiMessage.RecipientTypeCc);
message.AddRecipient("archive@example.com", "Ops Archive", MapiMessage.RecipientTypeBcc);

message.AddAttachment("hello.txt", Encoding.UTF8.GetBytes("sample attachment\n"), "text/plain");
message.AddAttachment("report.bin", new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }, "application/octet-stream");
message.Save(msgPath);

using (var loadedMessage = MapiMessage.FromFile(msgPath))
{
    loadedMessage.SaveToEml(emlPath);
}

Console.WriteLine($"MSG saved to: {msgPath}");
Console.WriteLine($"EML saved to: {emlPath}");

static string? GetOption(IReadOnlyList<string> arguments, string name)
{
    for (var index = 0; index < arguments.Count - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.Ordinal))
        {
            return arguments[index + 1];
        }
    }

    return null;
}
