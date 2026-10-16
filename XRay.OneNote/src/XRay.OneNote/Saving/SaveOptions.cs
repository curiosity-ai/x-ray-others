namespace XRay.OneNote.Saving;

/// <summary>Base type of the options passed to <see cref="Document.Save(string, SaveOptions)"/>.</summary>
public abstract class SaveOptions
{
    private protected SaveOptions(SaveFormat saveFormat)
    {
        SaveFormat = saveFormat;
    }

    /// <summary>The output format these options produce.</summary>
    public SaveFormat SaveFormat { get; }

    /// <summary>The zero-based index of the first page to save.</summary>
    public int PageIndex { get; set; }

    /// <summary>The number of pages to save, or null for every page from <see cref="PageIndex"/> on.</summary>
    public int? PageCount { get; set; }

    /// <summary>Where fonts for the output come from. Null uses <see cref="Saving.FontsSubsystem.Default"/>.</summary>
    public FontsSubsystem? FontsSubsystem { get; set; }
}

/// <summary>Options for saving to PDF.</summary>
public sealed class PdfSaveOptions : SaveOptions
{
    /// <summary>Creates PDF save options with the defaults: every page, standard fonts.</summary>
    public PdfSaveOptions()
        : base(SaveFormat.Pdf)
    {
    }
}

/// <summary>
/// Font sources for PDF output. Text that the PDF standard fonts cannot show (anything outside
/// Latin-1, such as Cyrillic) is drawn with a TrueType font found in these directories, which is
/// then embedded (subset) in the PDF.
/// </summary>
public sealed class FontsSubsystem
{
    /// <summary>
    /// The environment variable that, when set to 1/true/yes/on, makes <see cref="Default"/> embed
    /// TrueType fonts for all text instead of only for text outside Latin-1.
    /// </summary>
    public const string UseTrueTypeEnvironmentVariable = "XRAY_ONENOTE_PDF_USE_SYSTEM_FONTS";

    /// <summary>Directories searched before the system font directories, in order.</summary>
    public List<string> FontDirectories { get; } = [];

    /// <summary>Whether the usual system font directories (Windows, Linux, macOS) are searched after <see cref="FontDirectories"/>.</summary>
    public bool SearchSystemFontDirectories { get; set; } = true;

    /// <summary>
    /// Whether every run is drawn with an embedded TrueType font when one is found. When false (the
    /// default) the PDF standard fonts are used for Latin-1 text and TrueType fonts only where needed.
    /// </summary>
    public bool UseTrueTypeForAllText { get; set; }

    /// <summary>The default sources: system font directories, TrueType only where needed unless the environment variable says otherwise.</summary>
    public static FontsSubsystem Default => new() { UseTrueTypeForAllText = EnvironmentRequestsTrueType() };

    private static bool EnvironmentRequestsTrueType()
    {
        var value = Environment.GetEnvironmentVariable(UseTrueTypeEnvironmentVariable)?.Trim().ToLowerInvariant();
        return value is "1" or "true" or "yes" or "on";
    }
}
