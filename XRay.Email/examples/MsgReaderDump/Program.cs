using System.Text;
using XRay.Email.Cfb;
using XRay.Email.Msg;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: msg_reader.cs <path-to-msg> [--out <path>]");
    return;
}

var msgPath = args[0];
var outPath = GetOption(args, "--out");

using var reader = MsgReader.FromFile(msgPath);
using var cfb = CfbReader.FromFile(msgPath);
var document = MsgDocument.FromFile(msgPath);

var dump = BuildDump(reader, cfb, document, msgPath);
if (outPath is not null)
{
    File.WriteAllText(outPath, dump, new UTF8Encoding(false));
}
else
{
    Console.WriteLine(dump);
}

static string BuildDump(MsgReader reader, CfbReader cfb, MsgDocument document, string msgPath)
{
    var recipients = document.Root.Storages.Where(item => item.Role == MsgStorageRole.Recipient).ToArray();
    var attachments = document.Root.Storages.Where(item => item.Role == MsgStorageRole.Attachment).ToArray();
    var namedPropertyMapping = document.Root.Storages.FirstOrDefault(item => item.Role == MsgStorageRole.NamedPropertyMapping);
    var propertyStream = document.Root.FindStream(MsgConstants.PropertyStreamName);

    var lines = new List<string>
    {
        $"MSG file: {msgPath}",
        "Compound File Binary (CFB) Summary:",
        $"- major_version={cfb.MajorVersion}",
        $"- sector_size={cfb.SectorSize}",
        $"- mini_sector_size={cfb.MiniSectorSize}",
        $"- fat_sectors={cfb.Fat.Count}",
        $"- directory_entries={cfb.DirectoryEntryCount}",
        $"- streams_materialized={cfb.MaterializedStreamCount}",
        $"- file_size={cfb.FileSize}",
        "MSG Summary:",
        $"- validation_issues={reader.ValidationIssues.Count}",
        $"- named_property_mapping={namedPropertyMapping?.Name ?? "<missing>"}",
        $"- top_level_property_stream={propertyStream?.Name ?? "<missing>"}",
        $"- top_level_property_stream_size={propertyStream?.Data.Length ?? 0}",
        $"- recipients={recipients.Length}",
        $"- attachments={attachments.Length}",
    };

    if (reader.ValidationIssues.Count > 0)
    {
        lines.Add("Validation Issues:");
        foreach (var issue in reader.ValidationIssues)
        {
            lines.Add($"- {issue}");
        }
    }

    lines.Add("Recipient Storages:");
    if (recipients.Length == 0)
    {
        lines.Add("- <none>");
    }
    else
    {
        foreach (var recipient in recipients)
        {
            var recipientPropertyStream = recipient.FindStream(MsgConstants.PropertyStreamName);
            lines.Add(
                $"- {recipient.Name} role={recipient.Role} property_stream_size={recipientPropertyStream?.Data.Length ?? 0} nested_storages={recipient.Storages.Count}");
        }
    }

    lines.Add("Attachment Storages:");
    if (attachments.Length == 0)
    {
        lines.Add("- <none>");
    }
    else
    {
        foreach (var attachment in attachments)
        {
            var attachmentPropertyStream = attachment.FindStream(MsgConstants.PropertyStreamName);
            var hasEmbeddedMessage = attachment.FindStorage(MsgConstants.EmbeddedMessageStorageName) is not null;
            lines.Add(
                $"- {attachment.Name} role={attachment.Role} property_stream_size={attachmentPropertyStream?.Data.Length ?? 0} embedded_message={hasEmbeddedMessage}");
        }
    }

    lines.Add("Compound File Binary (CFB) Tree:");
    foreach (var (depth, entry) in cfb.IterTree())
    {
        var indent = new string(' ', depth * 2);
        if (entry.IsStream() || entry.IsRoot())
        {
            lines.Add($"{indent}- {entry.Name} [sid={entry.StreamId}, type={entry.ObjectType}, size={entry.StreamSize}]");
        }
        else
        {
            lines.Add($"{indent}- {entry.Name} [sid={entry.StreamId}, type={entry.ObjectType}]");
        }
    }

    return string.Join(Environment.NewLine, lines);
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
