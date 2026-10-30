using System.Globalization;
using System.Text.Json;
using XRay.ThreeD.Formats.Cad;

namespace XRay.ThreeD.Tests;

/// <summary>
/// DWG releases AC1015 to AC1032 against the DXF twin of one drawing (Autodesk-exported samples of a public DWG library, not redistributable: they are read from
/// XRAY_3D_LOCAL_SAMPLES, default /tmp/claude-0/samples, and the tests skip when absent). The expectations in dwg-releases-golden.json come from ezdxf reading the twins.
/// </summary>
public class DwgReleaseTests
{
    public static IEnumerable<object[]> Releases() => [["2000"], ["2004"], ["2007"], ["2010"], ["2013"], ["2018"]];

    private static string? Sample(string release)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "dwg", $"example_{release}.dwg");
        return File.Exists(path) ? path : null;
    }

    private static JsonElement Golden(string release)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.CorpusFile("dwg-releases-golden.json")));
        return doc.RootElement.GetProperty(release).Clone();
    }

    /// <summary>Entity types counted over the DXF text, and the coordinate sums of LINE, POINT and CIRCLE entities.</summary>
    private static (Dictionary<string, int> Counts, double Line, double Point, double Circle) Scan(string dxf)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        double line = 0, point = 0, circle = 0;
        var lines = dxf.Split('\n');
        var current = "";
        var section = "";
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            var code = int.Parse(lines[i].Trim(), CultureInfo.InvariantCulture);
            var value = lines[i + 1];
            if (code == 0)
            {
                current = value;
                if (section is "BLOCKS" or "ENTITIES" && value is not ("BLOCK" or "ENDBLK" or "SEQEND" or "ENDSEC"))
                {
                    counts[value] = counts.GetValueOrDefault(value) + 1;
                }

                continue;
            }

            if (code == 2 && current == "SECTION")
            {
                section = value;
            }

            if (section is not ("BLOCKS" or "ENTITIES") || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            switch (current)
            {
                case "LINE" when code is 10 or 20 or 30 or 11 or 21 or 31:
                    line += number;
                    break;
                case "POINT" when code is 10 or 20 or 30:
                    point += number;
                    break;
                case "CIRCLE" when code is 10 or 20 or 30 or 40:
                    circle += number;
                    break;
            }
        }

        return (counts, line, point, circle);
    }

    private static readonly string[] DecodedTypes =
    [
        "3DFACE", "ARC", "ATTDEF", "ATTRIB", "CIRCLE", "DIMENSION", "ELLIPSE", "HATCH", "INSERT", "LINE", "LWPOLYLINE", "MTEXT", "POINT", "POLYLINE", "RAY", "SOLID", "SPLINE", "TEXT", "VERTEX", "XLINE",
    ];

    [Theory]
    [MemberData(nameof(Releases))]
    public void Entities_of_every_release_match_the_twin_type_by_type_and_coordinate_for_coordinate(string release)
    {
        if (Sample(release) is not { } path)
        {
            return;
        }

        var golden = Golden(release);
        var dxf = DwgReader.ToDxf(File.ReadAllBytes(path), new LoadOptions(), out _);
        var (counts, line, point, circle) = Scan(dxf);
        var expected = golden.GetProperty("counts");
        foreach (var type in DecodedTypes)
        {
            var want = expected.TryGetProperty(type, out var n) ? n.GetInt32() : 0;
            Assert.True(counts.GetValueOrDefault(type) == want, $"{release} {type}: expected {want}, decoded {counts.GetValueOrDefault(type)}");
        }

        Assert.Equal(golden.GetProperty("lineSum").GetDouble(), line, 2);
        Assert.Equal(golden.GetProperty("pointSum").GetDouble(), point, 2);
        Assert.Equal(golden.GetProperty("circleSum").GetDouble(), circle, 2);
    }

    [Fact]
    public void Every_AC1021_sample_with_a_DXF_twin_decodes_the_same_entity_counts()
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var folder = Path.Combine(root, "dwg", "2007");
        if (!Directory.Exists(folder))
        {
            return;
        }

        var checkedFiles = 0;
        foreach (var dwg in Directory.EnumerateFiles(folder, "*.dwg"))
        {
            var twin = Path.ChangeExtension(dwg, ".dxf");
            if (!File.Exists(twin))
            {
                continue;
            }

            var (want, _, _, _) = Scan(File.ReadAllText(twin).Replace("\r", "", StringComparison.Ordinal));
            var (got, _, _, _) = Scan(DwgReader.ToDxf(File.ReadAllBytes(dwg), new LoadOptions(), out _));
            foreach (var type in DecodedTypes)
            {
                Assert.True(got.GetValueOrDefault(type) == want.GetValueOrDefault(type), $"{Path.GetFileName(dwg)} {type}: twin {want.GetValueOrDefault(type)}, decoded {got.GetValueOrDefault(type)}");
            }

            checkedFiles++;
        }

        Assert.True(checkedFiles > 0);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Layers_units_and_the_drawing_itself_are_read_from_every_release(string release)
    {
        if (Sample(release) is not { } path)
        {
            return;
        }

        var golden = Golden(release);
        var bytes = File.ReadAllBytes(path);
        var meta = ModelLoader.ReadMetadata(bytes, "x.dwg");
        Assert.Equal(golden.GetProperty("layers").EnumerateArray().Select(l => l.GetString()!).Order(StringComparer.Ordinal), meta.LayerNames.Order(StringComparer.Ordinal));
        Assert.True(meta.HasGeometry);

        var model = ModelLoader.Load(bytes, "x.dwg");
        Assert.Equal("dwg", model.Metadata.Format.Id);
        Assert.NotEmpty(model.Meshes);
        Assert.NotEmpty(model.Texts);
        Assert.True(model.Fills.Count >= 0);
        // $INSUNITS is 4 (millimetres) in the twin of every release.
        Assert.Equal(LengthUnit.Millimeter, model.Metadata.Units);

        _ = GlbWriter.ToArray(model);
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Header_variables_are_decoded_one_by_one_and_match_the_twin(string release)
    {
        if (Sample(release) is not { } path)
        {
            return;
        }

        var header = Golden(release).GetProperty("header");
        var sections = DwgContainer.Open(File.ReadAllBytes(path), new LoadOptions());
        var vars = DwgHeaderVariables.Read(sections.Header, sections.Generation);
        Assert.NotNull(vars);
        double N(string name) => vars!.Numbers[name];
        double[] P(string name) => header.GetProperty(name).EnumerateArray().Select(e => e.GetDouble()).ToArray();
        Assert.Equal(header.GetProperty("$INSUNITS").GetInt32(), (int)N("InsUnits"));
        Assert.Equal(header.GetProperty("$LUNITS").GetInt32(), (int)N("LinearUnitFormat"));
        Assert.Equal(header.GetProperty("$LUPREC").GetInt32(), (int)N("LinearUnitPrecision"));
        Assert.Equal(header.GetProperty("$AUNITS").GetInt32(), (int)N("AngularUnit"));
        Assert.Equal(header.GetProperty("$AUPREC").GetInt32(), (int)N("AngularUnitPrecision"));
        Assert.Equal(header.GetProperty("$LTSCALE").GetDouble(), N("LineTypeScale"), 9);
        Assert.Equal(header.GetProperty("$TEXTSIZE").GetDouble(), N("TextHeightDefault"), 9);
        Assert.InRange(N("CreateDateTime"), 2_400_000, 2_500_000); // the twin was saved again, so its $TDCREATE differs; the DWG holds the original Julian date
        var min = P("$EXTMIN");
        var max = P("$EXTMAX");
        Assert.Equal(min[0], N("ModelSpaceExtMinX"), 6);
        Assert.Equal(min[1], N("ModelSpaceExtMinY"), 6);
        Assert.Equal(max[0], N("ModelSpaceExtMaxX"), 6);
        Assert.Equal(max[1], N("ModelSpaceExtMaxY"), 6);
        var limits = P("$LIMMAX");
        Assert.Equal(limits[0], N("ModelSpaceLimitsMaxX"), 9);
        Assert.Equal(limits[1], N("ModelSpaceLimitsMaxY"), 9);
    }

    [Theory]
    [InlineData("2004")]
    [InlineData("2007")]
    [InlineData("2010")]
    public void The_preview_of_the_page_container_is_a_png(string release)
    {
        if (Sample(release) is not { } path)
        {
            return;
        }

        var preview = ModelLoader.ReadPreview(File.ReadAllBytes(path), "x.dwg");
        Assert.NotNull(preview);
        Assert.Equal("image/png", preview!.MediaType);
    }

    [Theory]
    [InlineData("2007")]
    [InlineData("2018")]
    public void A_truncated_page_container_fails_with_the_formats_own_exception(string release)
    {
        if (Sample(release) is not { } path)
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        for (var cut = 0x200; cut < bytes.Length; cut += bytes.Length / 40)
        {
            Assert.ThrowsAny<ModelFormatException>(() => ModelLoader.Load(bytes.AsSpan(0, cut).ToArray(), "x.dwg"));
        }
    }

    [Theory]
    [MemberData(nameof(Releases))]
    public void Mutated_files_of_every_release_fail_cleanly(string release)
    {
        if (Sample(release) is not { } path)
        {
            return;
        }

        var original = File.ReadAllBytes(path);
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("XRAY_3D_MUTATIONS"), out var n) ? Math.Min(n, 150) : 20;
        var random = new Random(release.Aggregate(4242, (h, c) => unchecked(h * 31 + c)));
        for (var round = 0; round < rounds; round++)
        {
            var data = (byte[])original.Clone();
            for (var k = 0; k < 1 + random.Next(8); k++)
            {
                // Half of the flips land in the page directory area, the rest anywhere.
                var at = k % 2 == 0 ? random.Next(Math.Min(data.Length, 0x400)) : random.Next(data.Length);
                data[at] = (byte)random.Next(256);
            }

            try
            {
                var model = ModelLoader.Load(data, "x.dwg");
                _ = GlbWriter.ToArray(model);
                _ = ModelLoader.ReadMetadata(data, "x.dwg");
            }
            catch (ModelFormatException)
            {
            }
        }
    }

    [Fact]
    public void The_code_page_id_of_the_file_header_selects_the_text_encoding_of_releases_before_2007()
    {
        Assert.Equal('\u20AC', DwgCodePages.For(30).GetString([0x80])[0]);
        Assert.Equal("\u041F\u0440", DwgCodePages.For(29).GetString([0xCF, 0xF0]));
        Assert.Equal("\u05D0", DwgCodePages.For(34).GetString([0xE0]));
        Assert.Equal("\u00E9", DwgCodePages.For(0).GetString([0xE9]));
        Assert.Equal("\u00E9", DwgCodePages.For(9999).GetString([0xE9]));
        Assert.Equal("\u3042", DwgCodePages.For(22).GetString([0x82, 0xA0]));
    }

    [Theory]
    [InlineData("2004")]
    [InlineData("2007")]
    public void A_spline_stored_by_its_fit_points_gets_the_control_points_and_knots_AutoCAD_reports(string release)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "dwg", release, "Spline.dwg");
        if (!File.Exists(path))
        {
            return;
        }

        // The property dump next to the sample (Spline.txt, from AutoCAD): 3 fit points, 5 control points starting (28.059, 8.80677), (33.9643, 7.72128), knots 0 0 0 0 12.2776 26.0835 ...
        var lines = DwgReader.ToDxf(File.ReadAllBytes(path), new LoadOptions(), out _).Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var at = Array.FindIndex(lines, l => l == "SPLINE");
        Assert.True(at >= 0);
        var xs = new List<double>();
        var ys = new List<double>();
        var knots = new List<double>();
        for (var i = at + 1; i + 1 < lines.Length && lines[i].Trim() != "0"; i += 2)
        {
            var code = lines[i].Trim();
            var value = double.TryParse(lines[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            if (code == "10")
            {
                xs.Add(value);
            }
            else if (code == "20" && xs.Count > ys.Count)
            {
                ys.Add(value);
            }
            else if (code == "40")
            {
                knots.Add(value);
            }
        }

        Assert.Equal(5, xs.Count);
        Assert.Equal(9, knots.Count);
        Assert.Equal(28.059, xs[0], 3);
        Assert.Equal(33.9643, xs[1], 2);
        Assert.Equal(7.72128, ys[1], 2);
        Assert.Equal(12.2776, knots[4], 3);
        Assert.InRange(knots[5], 26.0834, 26.0836);
    }
}
