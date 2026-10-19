using System.Text.Encodings.Web;
using System.Text.Json;

namespace XRay.OneNote.Tests.Support;

/// <summary>Serializer options that write a golden the way tools/regenerate_goldens.py does (one-space indent, readable text).</summary>
internal static class GoldenJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
