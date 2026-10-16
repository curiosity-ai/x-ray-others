using XRay.Email.Msg;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: msg_summary.cs <path-to-msg> [--body-preview-chars <count>] [--property-id <id>] [--property-type <type>] [--property-raw]");
    return;
}

var msgPath = args[0];
var bodyPreviewChars = ParseIntOption(args, "--body-preview-chars") ?? 400;
var propertyIdText = GetOption(args, "--property-id");
var propertyTypeText = GetOption(args, "--property-type");
var propertyRaw = args.Any(item => string.Equals(item, "--property-raw", StringComparison.Ordinal));

using var message = MapiMessage.FromFile(msgPath);

Console.WriteLine("Message Summary:");
Console.WriteLine($"file: {msgPath}");
Console.WriteLine($"subject: {message.Subject ?? string.Empty}");
Console.WriteLine($"message_class: {message.MessageClass ?? string.Empty}");
Console.WriteLine($"sender_name: {message.SenderName ?? string.Empty}");
Console.WriteLine($"sender_email: {message.SenderEmailAddress ?? string.Empty}");
Console.WriteLine($"internet_message_id: {message.InternetMessageId ?? string.Empty}");
Console.WriteLine($"recipients_count: {message.Recipients.Count}");
Console.WriteLine($"attachments_count: {message.Attachments.Count}");
Console.WriteLine($"validation_issues: {message.ValidationIssues.Count}");

Console.WriteLine();
Console.WriteLine("Display Recipients:");
Console.WriteLine($"- To: {AsString(message.GetPropertyValue((ushort)CommonMessagePropertyId.DisplayTo, (ushort)PropertyTypeCode.PtypString))}");
Console.WriteLine($"- Cc: {AsString(message.GetPropertyValue((ushort)CommonMessagePropertyId.DisplayCc, (ushort)PropertyTypeCode.PtypString))}");
Console.WriteLine($"- Bcc: {AsString(message.GetPropertyValue((ushort)CommonMessagePropertyId.DisplayBcc, (ushort)PropertyTypeCode.PtypString))}");

Console.WriteLine();
Console.WriteLine("Transport Headers:");
var transportHeaders = AsString(message.GetPropertyValue((ushort)CommonMessagePropertyId.TransportMessageHeaders, (ushort)PropertyTypeCode.PtypString));
if (string.IsNullOrWhiteSpace(transportHeaders))
{
    Console.WriteLine("- <none>");
}
else
{
    foreach (var line in transportHeaders.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
    {
        Console.WriteLine($"- {line}");
    }
}

Console.WriteLine();
Console.WriteLine("Recipients:");
if (message.Recipients.Count == 0)
{
    Console.WriteLine("- <none>");
}
else
{
    for (var index = 0; index < message.Recipients.Count; index++)
    {
        var recipient = message.Recipients[index];
        Console.WriteLine(
            $"- [{index + 1}] name={recipient.DisplayName ?? string.Empty} email={recipient.EmailAddress ?? string.Empty} type={recipient.RecipientType}");
    }
}

Console.WriteLine();
Console.WriteLine("Body Preview:");
var body = string.IsNullOrEmpty(message.HtmlBody) ? message.Body ?? string.Empty : message.HtmlBody!;
var preview = body.Length <= bodyPreviewChars ? body : body[..bodyPreviewChars];
Console.WriteLine(preview);
if (body.Length > preview.Length)
{
    Console.WriteLine($"... ({body.Length - preview.Length} more chars omitted)");
}

Console.WriteLine();
Console.WriteLine("Attachments:");
if (message.Attachments.Count == 0)
{
    Console.WriteLine("- <none>");
}
else
{
    for (var index = 0; index < message.Attachments.Count; index++)
    {
        var attachment = message.Attachments[index];
        Console.WriteLine(
            $"- [{index + 1}] name={attachment.Filename ?? string.Empty} mime={attachment.MimeType ?? string.Empty} size={attachment.Data.Length} content_id={attachment.ContentId ?? string.Empty} embedded={attachment.IsEmbeddedMessage}");
    }
}

Console.WriteLine();
Console.WriteLine("Attachment Memory Read Check:");
if (message.Attachments.Count == 0)
{
    Console.WriteLine("- <none>");
}
else
{
    for (var index = 0; index < message.Attachments.Count; index++)
    {
        var attachment = message.Attachments[index];
        using var stream = attachment.OpenRead();
        Console.WriteLine(
            $"- [{index + 1}] name={attachment.Filename ?? string.Empty} read_ok=True bytes_in_memory={stream.Length} content_type={attachment.MimeType ?? string.Empty}");
    }
}

if (propertyIdText is not null)
{
    var propertyId = ParseNumeric(propertyIdText);
    var propertyType = propertyTypeText is null ? (ushort?)null : ParseNumeric(propertyTypeText);
    var value = message.GetPropertyValue(propertyId, propertyType, decode: !propertyRaw);

    Console.WriteLine();
    Console.WriteLine("Arbitrary Property Lookup:");
    Console.WriteLine($"- property_id=0x{propertyId:X4}");
    Console.WriteLine($"- property_type={(propertyType is null ? "<auto>" : $"0x{propertyType.Value:X4}")}");
    if (value is null)
    {
        Console.WriteLine("- value=<not found>");
    }
    else if (value is byte[] bytes)
    {
        var hex = Convert.ToHexString(bytes);
        Console.WriteLine("- value_type=byte[]");
        Console.WriteLine($"- value_len={bytes.Length}");
        Console.WriteLine($"- value_hex={(hex.Length > 256 ? $"{hex[..256]}..." : hex)}");
    }
    else
    {
        Console.WriteLine($"- value_type={value.GetType().Name}");
        Console.WriteLine($"- value={value}");
    }
}

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

static int? ParseIntOption(IReadOnlyList<string> arguments, string name)
{
    var value = GetOption(arguments, name);
    return value is null ? null : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

static ushort ParseNumeric(string value)
{
    return Convert.ToUInt16(value, value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
}

static string AsString(object? value)
{
    return value as string ?? string.Empty;
}
