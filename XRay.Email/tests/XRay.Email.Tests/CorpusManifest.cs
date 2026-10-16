using System.Globalization;
using System.Text;
using System.Text.Json;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>
/// The expected fields of every EML and MSG fixture, produced by independent readers (Python's
/// email package and extract_msg) through tools/corpus/build_manifest.py.
/// </summary>
internal static class CorpusManifest
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> CachedEntries = new(Load);

    public static IReadOnlyDictionary<string, JsonElement> Entries => CachedEntries.Value;

    public static IEnumerable<string> Keys(string format) => Entries.Where(item => item.Value.GetProperty("format").GetString() == format).Select(item => item.Key).Order(StringComparer.Ordinal);

    public static MapiMessage Load(string path) =>
        path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? MapiMessage.LoadFromEml(path) : MapiMessage.FromFile(path);

    /// <summary>Which expected fields to compare; conversions keep some fields and not others.</summary>
    [Flags]
    public enum Fields
    {
        Subject = 1,
        From = 2,
        Recipients = 4,
        MessageId = 8,
        Date = 16,
        Body = 32,
        Html = 64,
        Attachments = 128,
        AttachmentTypes = 256,
        All = Subject | From | Recipients | MessageId | Date | Body | Html | Attachments | AttachmentTypes,
    }

    /// <summary>Compares a message with its manifest entry and returns every mismatch, one per line.</summary>
    public static string Compare(MapiMessage message, JsonElement expected, Fields fields)
    {
        var errors = new StringBuilder();
        void Check(string name, object? want, object? got)
        {
            if (!Equals(want, got))
            {
                errors.AppendLine($"{name}: expected <{want}> but got <{got}>");
            }
        }

        if (fields.HasFlag(Fields.Subject))
        {
            Check("subject", Blank(String(expected, "subject")), Blank(message.Subject));
        }

        if (fields.HasFlag(Fields.From))
        {
            Check("fromAddress", Lower(String(expected, "fromAddress")), Lower(message.SenderSmtpAddress));
            if (expected.TryGetProperty("fromName", out var fromName) && fromName.ValueKind == JsonValueKind.String)
            {
                Check("fromName", fromName.GetString(), message.SenderName);
            }
        }

        if (fields.HasFlag(Fields.Recipients))
        {
            foreach (var (name, type) in new[] { ("to", MapiMessage.RecipientTypeTo), ("cc", MapiMessage.RecipientTypeCc), ("bcc", MapiMessage.RecipientTypeBcc) })
            {
                var want = string.Join(", ", expected.GetProperty(name).EnumerateArray().Select(item => item.GetString()!.ToLowerInvariant()));
                var got = string.Join(", ", message.Recipients.Where(item => item.RecipientType == type).Select(item => (item.SmtpAddress ?? item.EmailAddress ?? string.Empty).ToLowerInvariant()));
                Check(name, want, got);
            }
        }

        if (fields.HasFlag(Fields.MessageId))
        {
            Check("messageId", Blank(String(expected, "messageId")), Blank(message.InternetMessageId?.Trim()));
        }

        if (fields.HasFlag(Fields.Date))
        {
            var got = message.MessageDeliveryTime?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            Check("date", String(expected, "date"), got);
        }

        if (fields.HasFlag(Fields.Body))
        {
            CheckText("body", message.Body, true);
        }

        if (fields.HasFlag(Fields.Html))
        {
            CheckText("html", message.HtmlBody, expected.TryGetProperty("htmlPrefix", out _));
        }

        if (fields.HasFlag(Fields.Attachments))
        {
            var want = expected.GetProperty("attachments").EnumerateArray().ToArray();
            Check("attachments.count", want.Length, message.Attachments.Count);
            for (var index = 0; index < Math.Min(want.Length, message.Attachments.Count); index++)
            {
                var attachment = message.Attachments[index];
                Check($"attachments[{index}].filename", Blank(String(want[index], "filename")), Blank(attachment.Filename));
                if (want[index].TryGetProperty("size", out var size))
                {
                    Check($"attachments[{index}].size", size.GetInt32(), attachment.Data.Length);
                }

                if (want[index].TryGetProperty("embeddedSubject", out var subject))
                {
                    Check($"attachments[{index}].embedded", true, attachment.EmbeddedMessage is not null);
                    Check($"attachments[{index}].embeddedSubject", Blank(subject.GetString()), Blank(attachment.EmbeddedMessage?.Subject));
                }

                if (fields.HasFlag(Fields.AttachmentTypes) && want[index].TryGetProperty("contentType", out var contentType))
                {
                    Check($"attachments[{index}].contentType", contentType.GetString(), attachment.MimeType?.ToLowerInvariant());
                }
            }
        }

        return errors.ToString();

        void CheckText(string name, string? actual, bool comparePrefix)
        {
            var text = Normalize(actual);
            // Lengths are in Unicode scalar values, as the Python oracle counts them.
            Check($"{name}Length", expected.GetProperty($"{name}Length").GetInt32(), text.EnumerateRunes().Count());
            if (comparePrefix && expected.TryGetProperty($"{name}Prefix", out var prefix))
            {
                var want = prefix.GetString()!;
                Check($"{name}Prefix", want, text.StartsWith(want, StringComparison.Ordinal) ? want : text[..Math.Min(text.Length, want.Length)]);
            }
        }
    }

    public static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? Lower(string? value) => Blank(value)?.ToLowerInvariant();

    private static IReadOnlyDictionary<string, JsonElement> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Corpus", "email-manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
    }
}
