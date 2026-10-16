namespace XRay.Ics;

/// <summary>Options of <see cref="IcsExtractor"/>.</summary>
public sealed class IcsExtractOptions
{
    /// <summary>Reader limits and encoding fallback.</summary>
    public IcsParseOptions Parse { get; set; } = new();

    /// <summary>Defaults for the HTML, plain text and Markdown renderings of the result.</summary>
    public IcsRenderOptions Render { get; set; } = new();
}
