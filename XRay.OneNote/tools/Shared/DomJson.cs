using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace XRay.OneNote.Tools;

/// <summary>
/// A deterministic JSON dump of a document tree, field for field the same as the one
/// <c>tools/regenerate_goldens.py</c> writes from the Python reference, so the two can be compared.
/// Uses only the public API.
/// </summary>
internal static class DomJson
{
    public static JsonObject DumpDocument(string fixture, Document document, Document? historyDocument)
    {
        var result = new JsonObject
        {
            ["fixture"] = fixture,
            ["display_name"] = document.DisplayName,
            ["file_format"] = document.FileFormat.ToString(),
            ["pages"] = new JsonArray([.. document.Pages.Select(DumpNode)]),
        };

        if (historyDocument is not null)
        {
            var histories = new JsonArray();
            foreach (var page in historyDocument.Pages)
            {
                var history = historyDocument.GetPageHistory(page);
                histories.Add(new JsonObject
                {
                    ["current_is_page"] = ReferenceEquals(history.Current, page),
                    ["count"] = history.Count,
                    ["versions"] = new JsonArray([.. history.Select(DumpNode)]),
                });
            }

            result["history"] = histories;
        }

        return result;
    }

    public static JsonNode? DumpNode(Node? node) => node switch
    {
        null => null,
        Page page => new JsonObject
        {
            ["type"] = "Page",
            ["author"] = page.Author,
            ["creation_time"] = Time(page.CreationTime),
            ["last_modified_time"] = Time(page.LastModifiedTime),
            ["level"] = page.Level,
            ["has_title"] = page.Title is not null,
            ["children"] = Children(page),
        },
        Title title => new JsonObject
        {
            ["type"] = "Title",
            ["text"] = DumpNode(title.TitleText),
            ["date"] = DumpNode(title.TitleDate),
            ["time"] = DumpNode(title.TitleTime),
        },
        Outline outline => new JsonObject
        {
            ["type"] = "Outline",
            ["horizontal_offset"] = outline.HorizontalOffset,
            ["vertical_offset"] = outline.VerticalOffset,
            ["max_width"] = outline.MaxWidth,
            ["children"] = Children(outline),
        },
        OutlineElement element => new JsonObject
        {
            ["type"] = "OutlineElement",
            ["number_list"] = element.NumberList is { } list
                ? new JsonObject
                {
                    ["format"] = list.Format,
                    ["number_format"] = list.NumberFormat,
                    ["restart"] = list.Restart,
                }
                : null,
            ["children"] = Children(element),
        },
        RichText richText => new JsonObject
        {
            ["type"] = "RichText",
            ["text"] = richText.Text,
            ["alignment"] = richText.Alignment?.ToString(),
            ["paragraph_style"] = Paragraph(richText.ParagraphStyle),
            ["runs"] = new JsonArray([.. richText.TextRuns.Select(run => (JsonNode)new JsonObject
            {
                ["text"] = run.Text,
                ["style"] = Style(run.Style),
            })]),
            ["tags"] = Tags(richText.Tags),
        },
        Image image => new JsonObject
        {
            ["type"] = "Image",
            ["file_name"] = image.FileName,
            ["bytes_len"] = image.Bytes.Length,
            ["sha256"] = Sha256(image.Bytes),
            ["width"] = image.Width,
            ["height"] = image.Height,
            ["original_width"] = image.OriginalWidth,
            ["original_height"] = image.OriginalHeight,
            ["alignment"] = image.Alignment?.ToString(),
            ["alt_text"] = image.AlternativeTextDescription,
            ["hyperlink_url"] = image.HyperlinkUrl,
            ["tags"] = Tags(image.Tags),
            ["children"] = Children(image),
        },
        AttachedFile file => new JsonObject
        {
            ["type"] = "AttachedFile",
            ["file_name"] = file.FileName,
            ["bytes_len"] = file.Bytes.Length,
            ["sha256"] = Sha256(file.Bytes),
            ["tags"] = Tags(file.Tags),
        },
        Table table => new JsonObject
        {
            ["type"] = "Table",
            ["columns"] = new JsonArray([.. table.Columns.Select(column => (JsonNode?)JsonValue.Create(column.Width))]),
            ["borders_visible"] = table.IsBordersVisible,
            ["tags"] = Tags(table.Tags),
            ["children"] = Children(table),
        },
        TableRow row => new JsonObject { ["type"] = "TableRow", ["children"] = Children(row) },
        TableCell cell => new JsonObject { ["type"] = "TableCell", ["children"] = Children(cell) },
        _ => new JsonObject { ["type"] = node.GetType().Name },
    };

    /// <summary>Plain text of a document, one paragraph per line, in document order.</summary>
    public static string PlainText(Document document) =>
        string.Join("\n", document.GetChildNodes<RichText>().Select(rt => rt.Text).Where(text => text.Length > 0));

    private static JsonArray Children(CompositeNode node) => new([.. node.Select(DumpNode)]);

    private static JsonObject Paragraph(ParagraphStyle style) => new()
    {
        ["font_name"] = style.FontName,
        ["font_size"] = style.FontSize,
        ["font_color"] = style.FontColor,
        ["highlight"] = style.Highlight,
        ["bold"] = style.IsBold,
        ["italic"] = style.IsItalic,
        ["underline"] = style.IsUnderline,
        ["strikethrough"] = style.IsStrikethrough,
        ["superscript"] = style.IsSuperscript,
        ["subscript"] = style.IsSubscript,
    };

    private static JsonObject Style(TextStyle style) => new()
    {
        ["font_name"] = style.FontName,
        ["font_size"] = style.FontSize,
        ["font_color"] = style.FontColor,
        ["highlight"] = style.Highlight,
        ["bold"] = style.IsBold,
        ["italic"] = style.IsItalic,
        ["underline"] = style.IsUnderline,
        ["strikethrough"] = style.IsStrikethrough,
        ["superscript"] = style.IsSuperscript,
        ["subscript"] = style.IsSubscript,
        ["is_hyperlink"] = style.IsHyperlink,
        ["hyperlink_address"] = style.HyperlinkAddress,
        ["language"] = style.Language,
    };

    private static JsonArray Tags(IEnumerable<NoteTag> tags) => new([.. tags.Select(tag => (JsonNode)new JsonObject
    {
        ["label"] = tag.Label,
        ["icon"] = tag.Icon,
        ["status"] = tag.Status.ToString(),
        ["font_color"] = tag.FontColor,
        ["highlight"] = tag.Highlight,
        ["creation_time"] = Time(tag.CreationTime),
        ["completed_time"] = Time(tag.CompletedTime),
    })]);

    private static string? Time(DateTime? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
