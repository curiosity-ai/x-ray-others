// Recoverable problems reported by a conversion that was asked to suppress errors.

namespace XRay.PS;

/// <summary>A problem a conversion recovered from (see <see cref="PdfSaveOptions.SuppressErrors"/>).</summary>
/// <param name="Source">"ps" for PostScript and EPS, "xps" for XPS.</param>
/// <param name="Code">A short kind: the PostScript error name, "limitcheck", or "element".</param>
/// <param name="Message">What went wrong.</param>
/// <param name="PageNumber">The one-based page the problem occurred on, when known.</param>
public sealed record ConversionWarning(string Source, string Code, string Message, int? PageNumber = null);
