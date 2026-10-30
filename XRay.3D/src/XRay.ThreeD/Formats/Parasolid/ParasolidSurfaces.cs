using XRay.ThreeD.Formats.Step;

namespace XRay.ThreeD.Formats.Parasolid;

/// <summary>A surface moved along its own normal by a distance (a Parasolid OFFSET_SURF).</summary>
internal sealed class OffsetSurfaceOf : Surface
{
    public required Surface Base { get; init; }

    public double Distance { get; init; }

    public override bool PeriodicU => Base.PeriodicU;

    public override bool PeriodicV => Base.PeriodicV;

    public override double[] PoleV => Base.PoleV;

    public override V3 At(double u, double v) => Base.At(u, v) + Base.Normal(u, v) * Distance;

    public override V3 Normal(double u, double v) => Base.Normal(u, v);

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        // The foot point of the base surface is found for p moved back along the normal found at the previous guess.
        var uv = Base.Project(p, hint);
        for (var i = 0; i < 4; i++)
        {
            uv = Base.Project(p - Base.Normal(uv.U, uv.V) * Distance, uv);
        }

        return uv;
    }

    public override (double U, double V) InteriorStep(double angular) => Base.InteriorStep(angular);
}
