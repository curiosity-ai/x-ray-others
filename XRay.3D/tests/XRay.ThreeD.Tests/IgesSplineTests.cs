using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// IGES parametric spline surfaces (114), ruled surfaces (118), free-standing bounded planes (108) and parametric spline curves (112) against
/// OpenCASCADE's IGES reader: area and bounds of what each file draws (see tools/fixtures/make_iges_splines.py).
/// </summary>
public class IgesSplineTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("iges-splines-golden.json"))).RootElement;

    [Theory]
    [InlineData("synthetic/iges-spline-surface.igs", 0.01)]
    [InlineData("synthetic/iges-ruled.igs", 0.01)]
    [InlineData("synthetic/iges-bounded-plane.igs", 0.001)]
    public void Surfaces_match_opencascade(string file, double areaTolerance)
    {
        var expected = Golden.GetProperty(file);
        var model = ModelLoader.Load(DatasetPaths.File(file));
        var (area, _) = StepTests.Measure(model);
        var expectedArea = expected.GetProperty("area").GetDouble();
        Assert.True(Math.Abs(area / expectedArea - 1) < areaTolerance, $"{file}: area {area:F3} vs {expectedArea:F3}");
        AssertBounds(model, expected, 0.02f);
        Assert.Empty(model.Warnings.Where(w => w.Contains("not drawn", StringComparison.Ordinal) || w.Contains("skipped", StringComparison.Ordinal)));
    }

    [Fact]
    public void Spline_curve_matches_its_polynomial_and_opencascade_bounds()
    {
        var file = "synthetic/iges-spline-curve.igs";
        var model = ModelLoader.Load(DatasetPaths.File(file));
        AssertBounds(model, Golden.GetProperty(file), 0.05f);

        // x(s) = 0.5 + 1.0 s - 0.4 s^2 + 0.2 s^3 for s in [0, 2]: the samples lie on it at s = 2 k / 8.
        var mesh = model.Meshes[0];
        var origin = model.Metadata.Origin ?? [0, 0, 0];
        double X(double s) => 0.5 + s * (1.0 + s * (-0.4 + s * 0.2));
        double Y(double s) => -1.0 + s * (0.3 + s * (0.8 + s * -0.1));
        double Z(double s) => 2.0 + s * (-0.5 + s * (0.25 + s * 0.05));
        var found = 0;
        for (var k = 0; k <= 8; k++)
        {
            var s = 2.0 * k / 8;
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                if (Math.Abs(mesh.Positions[3 * i] - X(s)) < 1e-4 && Math.Abs(mesh.Positions[3 * i + 1] - Y(s)) < 1e-4 && Math.Abs(mesh.Positions[3 * i + 2] - Z(s)) < 1e-4)
                {
                    found++;
                    break;
                }
            }
        }

        Assert.Equal(9, found);
    }

    private static void AssertBounds(Model3D model, JsonElement expected, float tolerance)
    {
        var bounds = model.ComputeBounds()!.Value;
        var min = expected.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var max = expected.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(min[0], min[1], min[2])) < tolerance, $"min {bounds.Min}");
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(max[0], max[1], max[2])) < tolerance, $"max {bounds.Max}");
    }
}
