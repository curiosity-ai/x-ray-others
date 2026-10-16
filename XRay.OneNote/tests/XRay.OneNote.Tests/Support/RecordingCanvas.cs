using XRay.OneNote.Internal.Pdf;

namespace XRay.OneNote.Tests.Support;

/// <summary>An <see cref="IPdfCanvas"/> that records every call, like the fake canvas of the Python tests.</summary>
internal sealed class RecordingCanvas : IPdfCanvas
{
    public double PageWidth => 595;

    public double PageHeight => 842;

    public PdfFontRegistry Fonts { get; } = new();

    public List<(double X, double Y, string Text)> DrawnStrings { get; } = [];

    public List<(string Name, double Size)> FontCalls { get; } = [];

    public List<(double R, double G, double B)> FillColors { get; } = [];

    public List<(double R, double G, double B)> StrokeColors { get; } = [];

    public List<(double X, double Y, double Width, double Height, bool Stroke, bool Fill)> RectCalls { get; } = [];

    public List<(double X1, double Y1, double X2, double Y2)> LineCalls { get; } = [];

    public List<(double X, double Y, double Width, double Height, bool PreserveAspectRatio)> ImageCalls { get; } = [];

    public List<(string Url, double X1, double Y1, double X2, double Y2)> LinkCalls { get; } = [];

    public int PolygonCalls { get; private set; }

    public int Pages { get; private set; }

    public void SetFont(string name, double size) => FontCalls.Add((name, size));

    public void DrawString(double x, double y, string text) => DrawnStrings.Add((x, y, text));

    public void SetFillColor(double r, double g, double b) => FillColors.Add((r, g, b));

    public void SetStrokeColor(double r, double g, double b) => StrokeColors.Add((r, g, b));

    public void SetLineWidth(double width)
    {
    }

    public void SaveState()
    {
    }

    public void RestoreState()
    {
    }

    public void Rect(double x, double y, double width, double height, bool stroke, bool fill) => RectCalls.Add((x, y, width, height, stroke, fill));

    public void Line(double x1, double y1, double x2, double y2) => LineCalls.Add((x1, y1, x2, y2));

    public void Circle(double centerX, double centerY, double radius, bool stroke, bool fill)
    {
    }

    public void Polygon(IReadOnlyList<(double X, double Y)> points, bool stroke, bool fill) => PolygonCalls++;

    public PdfImageSource? LoadImage(byte[] data) => new() { PixelWidth = 1024, PixelHeight = 768 };

    public void DrawImage(PdfImageSource image, double x, double y, double width, double height, bool preserveAspectRatio) =>
        ImageCalls.Add((x, y, width, height, preserveAspectRatio));

    public void LinkUrl(string url, double x1, double y1, double x2, double y2) => LinkCalls.Add((url, x1, y1, x2, y2));

    public void ShowPage() => Pages++;

    public double StringWidth(string text, string fontName, double size) => text.Length * size * 0.55;

    public byte[] Save() => "%PDF-fake"u8.ToArray();

    public static RecordingCanvas Render(Document document, XRay.OneNote.Saving.PdfSaveOptions? options = null)
    {
        var canvas = new RecordingCanvas();
        PdfExporter.Render(document, options ?? TestFonts.Options(), canvas);
        return canvas;
    }
}
