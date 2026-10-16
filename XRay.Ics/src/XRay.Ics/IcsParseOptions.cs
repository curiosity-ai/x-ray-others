using System.Text;

namespace XRay.Ics;

/// <summary>Limits and encoding fallbacks of the reader. Every value has a safe default.</summary>
public sealed class IcsParseOptions
{
    /// <summary>The largest input accepted, in bytes. Larger input raises <see cref="IcsFormatException"/>. Default 256 MiB.</summary>
    public long MaxInputBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// The longest logical (unfolded) line kept, in characters. A longer line is dropped and
    /// reported in <see cref="Calendar.Warnings"/>. Inline attachments are one line, so the default
    /// is generous (64 Mi characters).
    /// </summary>
    public int MaxLineLength { get; set; } = 64 * 1024 * 1024;

    /// <summary>The deepest component nesting kept; deeper components are skipped. Default 16.</summary>
    public int MaxDepth { get; set; } = 16;

    /// <summary>The most components read in one input; further ones are skipped. Default 1,000,000.</summary>
    public int MaxComponents { get; set; } = 1_000_000;

    /// <summary>The most properties kept in one component; further ones are skipped. Default 100,000.</summary>
    public int MaxPropertiesPerComponent { get; set; } = 100_000;

    /// <summary>
    /// The encoding for bytes that are not valid UTF-8 and carry no BOM and no per-line
    /// <c>CHARSET</c>. Default Windows-1252.
    /// </summary>
    public Encoding? FallbackEncoding { get; set; }

    internal static IcsParseOptions Default { get; } = new();
}
