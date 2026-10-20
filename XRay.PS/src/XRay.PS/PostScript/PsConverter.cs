// Ported from ps/output.py: PS/EPS to PDF and raster conversion.

using XRay.PS.Fonts;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>The render model of an interpreted PostScript job and the stores it refers to.</summary>
public sealed record PsRenderResult(RenderDocument Document, PsImageStore Images, FontResolver Fonts);

/// <summary>Converts PS/EPS documents to PDF and raster images.</summary>
public static partial class PsConverter
{
    /// <summary>Interprets a document into its render model (with the viewing-orientation fix
    /// and a blank page for empty jobs applied).</summary>
    public static PsRenderResult BuildRenderModel(PsDocument document, string? additionalFontsFolder = null) =>
        BuildRenderModel(document, additionalFontsFolder, null, false, null, default);

    /// <summary>Interprets a document into its render model under resource limits, optionally
    /// recovering from errors (see <see cref="PsPipeline.BuildRenderModel(byte[], PsInterpreterLimits?, bool, List{ConversionWarning}?, CancellationToken)"/>).</summary>
    public static PsRenderResult BuildRenderModel(PsDocument document, string? additionalFontsFolder, PsInterpreterLimits? limits,
        bool suppressErrors, List<ConversionWarning>? warnings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new RenderModelBuilder();
        var registry = new OperatorRegistry();
        var imageStore = new PsImageStore();
        var fontResolver = new FontResolver(additionalFontsFolder);
        BaseOperators.Register(registry);
        GraphicsOperators.Register(registry, builder);
        ColorOperators.Register(registry, builder);
        TextOperators.Register(registry, builder, fontResolver);
        ImageOperators.Register(registry, builder, imageStore);
        ExtraOperators.Register(registry, builder);
        var interpreter = new PsInterpreter(registry);
        var pipeline = new PsPipeline(interpreter, registry, builder, fontResolver, imageStore);
        var bytes = document.AsBytes();
        var effective = limits ?? PsInterpreterLimits.Default;
        var renderDoc = PsInterpreter.RunWithLargeStack(
            () => pipeline.BuildRenderModel(bytes, effective, suppressErrors, warnings, cancellationToken), cancellationToken, effective.MaxMemoryBytes);
        ApplyViewingOrientation(renderDoc, document.Dsc);
        EnsureNonEmptyDocument(renderDoc, document);
        return new PsRenderResult(renderDoc, imageStore, fontResolver);
    }

    private static void EnsureNonEmptyDocument(RenderDocument renderDoc, PsDocument document)
    {
        if (renderDoc.Pages.Count > 0)
            return;
        var (width, height) = PageGeometry.PageSizeFromDsc(document.Dsc);
        renderDoc.Pages.Add(new RenderPage(width, height));
    }

    private static void ApplyViewingOrientation(RenderDocument renderDoc, DscMetadata? dsc)
    {
        string? raw = dsc?.ViewingOrientation;
        if (string.IsNullOrEmpty(raw))
            return;
        var parts = Internal.Py.Split(raw);
        if (parts.Count != 4)
            return;
        var values = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!Internal.Py.TryFloat(parts[i], out values[i]))
                return;
        }
        // Common DSC orientation matrix used by PScript: %%ViewingOrientation: 0 1 -1 0
        if (!(Math.Abs(values[0]) < 1e-9 && Math.Abs(values[1] - 1.0) < 1e-9 && Math.Abs(values[2] + 1.0) < 1e-9 && Math.Abs(values[3]) < 1e-9))
            return;
        var transform = new Matrix(0.0, 1.0, 1.0, 0.0, 0.0, 0.0);
        foreach (var page in renderDoc.Pages)
        {
            if (page.Width >= page.Height)
                continue;
            page.Commands = page.Commands.Select(c => TransformCommand(c, transform)).ToList();
            (page.Width, page.Height) = (page.Height, page.Width);
        }
    }

    private static RenderCommand TransformCommand(RenderCommand command, Matrix matrix) => command switch
    {
        PathCommand pc => pc with { Path = GraphicsOperators.TransformPath(pc.Path, matrix) },
        ClipCommand cc => cc with { Path = GraphicsOperators.TransformPath(cc.Path, matrix) },
        // The original rebuilt the text command from its first six fields only.
        TextCommand tc => new TextCommand(tc.Text, tc.FontRef, tc.FontSize, TextOperators.Multiply(matrix, tc.Matrix), tc.Fill, tc.FillOpacity),
        ImageCommand ic => ic with { Matrix = TextOperators.Multiply(matrix, ic.Matrix) },
        _ => command,
    };
}
