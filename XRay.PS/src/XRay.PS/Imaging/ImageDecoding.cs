// Ported from _materialize_image_resource and its decoders in image/raster_renderer.py: turns
// DCT and CCITT encoded image resources into raw samples for the rasterizer.

using XRay.PS.Rendering;

namespace XRay.PS.Imaging;

internal static class ImageDecoding
{
    /// <summary>Returns raw samples for an image resource: decoded for DCTDecode (always RGB) and
    /// CCITTFaxDecode (1-bit gray), the stored data otherwise or when decoding fails.</summary>
    public static (byte[] Data, int Width, int Height, string ColorSpace, int BitsPerComponent) Materialize(RenderImageResource resource)
    {
        var fallback = (resource.Data, resource.Width, resource.Height, resource.ColorSpace, resource.BitsPerComponent);
        if (resource.Filter == "CCITTFaxDecode")
            return DecodeCcitt(resource) ?? fallback;
        if (resource.Filter != "DCTDecode")
            return fallback;
        var decoded = JpegDecoder.DecodeToRgb(resource.Data);
        if (decoded is not var (rgb, width, height))
            return fallback;
        return (rgb, width, height, "DeviceRGB", 8);
    }

    private static (byte[], int, int, string, int)? DecodeCcitt(RenderImageResource resource)
    {
        var parameters = resource.FilterParams;
        int width = resource.Width;
        int height = resource.Height;
        int columns = IntParam(parameters, "Columns", width != 0 ? width : 1728);
        if (columns == 0)
            columns = width != 0 ? width : 1728;
        int rows = IntParam(parameters, "Rows", height);
        if (rows == 0)
            rows = height;
        int k = IntParam(parameters, "K", 0);
        bool blackIs1 = parameters is not null && parameters.TryGetValue("BlackIs1", out var b) && Truthy(b);
        var mode = k < 0 ? CcittDecoder.Mode.Group4 : k == 0 ? CcittDecoder.Mode.Group3OneD : CcittDecoder.Mode.ModifiedHuffman;
        int imageWidth = Math.Max(1, columns);
        int imageHeight = Math.Max(1, rows > 0 ? rows : height);
        var bits = CcittDecoder.Decode(resource.Data, imageWidth, imageHeight, mode, blackIs1);
        if (bits is null)
            return null;
        return (bits, imageWidth, imageHeight, "DeviceGray", 1);
    }

    private static int IntParam(IReadOnlyDictionary<string, object?>? parameters, string key, int fallback)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null)
            return fallback;
        return value switch
        {
            long l => (int)l,
            int i => i,
            double d => (int)Internal.Py.Int(d),
            bool flag => flag ? 1 : 0,
            _ => fallback,
        };
    }

    private static bool Truthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        string s => s.Length > 0,
        _ => true,
    };
}
