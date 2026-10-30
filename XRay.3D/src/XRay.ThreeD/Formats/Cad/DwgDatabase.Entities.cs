using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Cad;

internal sealed partial class DwgDatabase
{
    private const double Deg = 180.0 / Math.PI;

    private EntityChunk? DecodeEntity(Record record, string kind, DwgBits b)
    {
        if (kind == "SEQEND")
        {
            return null;
        }

        var h = ReadEntityHeader(b);
        var chunk = new EntityChunk { Handle = record.Handle, Mode = h.Mode, Kind = kind };
        Dxf? d;
        long layerHandle = 0;
        var layerIndex = -1;
        List<long>? handles = null;

        // The handle stream is read after the entity's own data: the layer, the block an insert names, the block a dimension draws.
        void LoadHandles()
        {
            if (handles is not null)
            {
                return;
            }

            var saved = b.Position;
            handles = ReadHandles(b, h, 14);
            layerHandle = LayerOf(handles, out layerIndex);
            if (h.Mode == 0 && handles.Count > 0)
            {
                chunk.Owner = handles[0];
            }

            b.Position = saved;
        }

        LoadHandles();
        d = Begin(Dxf.TypeName(kind), h, layerHandle);
        switch (kind)
        {
            case "LINE":
            {
                var zZero = b.Bit();
                var x1 = b.RD();
                var x2 = b.DD(x1);
                var y1 = b.RD();
                var y2 = b.DD(y1);
                double z1 = 0, z2 = 0;
                if (!zZero)
                {
                    z1 = b.RD();
                    z2 = b.DD(z1);
                }

                var thickness = b.BT();
                var n = b.BE();
                d.P(10, (x1, y1, z1)).P(11, (x2, y2, z2));
                Common(d, thickness, n);
                break;
            }

            case "POINT":
            {
                var p = b.BD3();
                var thickness = b.BT();
                var n = b.BE();
                d.P(10, p);
                Common(d, thickness, n);
                break;
            }

            case "CIRCLE":
            case "ARC":
            {
                var c = b.BD3();
                var radius = b.BD();
                var thickness = b.BT();
                var n = b.BE();
                d.P(10, c).G(40, radius);
                if (kind == "ARC")
                {
                    d.G(50, b.BD() * Deg).G(51, b.BD() * Deg);
                }

                Common(d, thickness, n);
                break;
            }

            case "TEXT":
            case "ATTRIB":
            case "ATTDEF":
                ReadText(b, d, kind);
                break;
            case "MTEXT":
            {
                var at = b.BD3();
                var n = b.BD3();
                var axis = b.BD3();
                var width = b.BD();
                if (_generation >= 21)
                {
                    b.BD();
                }

                var height = b.BD();
                var attachment = b.BS();
                b.BS();
                b.BD();
                b.BD();
                var text = b.TV();
                d.P(10, at).G(40, height).G(41, width).G(71, attachment).G(1, text).P(11, axis).P(210, n);
                break;
            }

            case "INSERT":
            case "MINSERT":
            {
                var at = b.BD3();
                double sx, sy, sz;
                switch (b.Read(2))
                {
                    case 0:
                        sx = b.RD();
                        sy = b.DD(sx);
                        sz = b.DD(sx);
                        break;
                    case 1:
                        sx = 1;
                        sy = b.DD(1);
                        sz = b.DD(1);
                        break;
                    case 2:
                        sx = b.RD();
                        sy = sx;
                        sz = sx;
                        break;
                    default:
                        sx = sy = sz = 1;
                        break;
                }

                var rotation = b.BD();
                var n = b.BD3();
                var hasAttributes = b.Bit();
                if (_generation >= 18 && hasAttributes)
                {
                    b.BL();
                }

                int cols = 1, rows = 1;
                double dc = 0, dr = 0;
                if (kind == "MINSERT")
                {
                    cols = b.BS();
                    rows = b.BS();
                    dc = b.BD();
                    dr = b.BD();
                }

                if (!double.IsFinite(sx + sy + sz + rotation))
                {
                    throw new ModelFormatException("An insert has impossible values.");
                }

                var block = BlockHandleAfter(handles!, layerIndex + 1);
                if (block == 0 || !_blocks.TryGetValue(block, out var info))
                {
                    return null;
                }

                d = Begin("INSERT", h, layerHandle);
                d.G(2, info.Name);
                if (hasAttributes)
                {
                    d.G(66, 1);
                }

                d.P(10, at).G(41, sx).G(42, sy).G(43, sz).G(50, rotation * Deg).P(210, n);
                if (kind == "MINSERT")
                {
                    d.G(70, cols).G(71, rows).G(44, dc).G(45, dr);
                }

                if (hasAttributes)
                {
                    chunk.Trailer = new Dxf().G(0, "SEQEND").ToString();
                }

                break;
            }

            case "LWPOLYLINE":
                ReadLwPolyline(b, d);
                break;
            case "POLYLINE":
            {
                var flags = b.BS();
                var curve = b.BS();
                b.BD();
                b.BD();
                var thickness = b.BT();
                var elevation = b.BD();
                var n = b.BE();
                if (_generation >= 18)
                {
                    b.BL();
                }

                d.G(66, 1).P(10, (0, 0, elevation)).G(70, flags);
                if (curve != 0)
                {
                    d.G(75, curve);
                }

                Common(d, thickness, n);
                chunk.Trailer = new Dxf().G(0, "SEQEND").ToString();
                break;
            }

            case "POLYLINE3D":
            {
                var curve = b.RC();
                var closed = b.RC();
                if (_generation >= 18)
                {
                    b.BL();
                }

                d.G(66, 1).P(10, (0, 0, 0)).G(70, 8 | (closed & 1) | (curve != 0 ? 4 : 0));
                if (curve != 0)
                {
                    d.G(75, curve == 1 ? 6 : curve);
                }

                chunk.Trailer = new Dxf().G(0, "SEQEND").ToString();
                break;
            }

            case "POLYLINEPFACE":
            {
                var vertices = b.BS();
                var faces = b.BS();
                if (_generation >= 18)
                {
                    b.BL();
                }

                d.G(66, 1).P(10, (0, 0, 0)).G(70, 64).G(71, vertices).G(72, faces);
                chunk.Trailer = new Dxf().G(0, "SEQEND").ToString();
                break;
            }

            case "POLYLINEMESH":
            {
                var flags = b.BS();
                b.BS();
                var m = b.BS();
                var n = b.BS();
                b.BS();
                b.BS();
                if (_generation >= 18)
                {
                    b.BL();
                }

                d.G(66, 1).P(10, (0, 0, 0)).G(70, 16 | (flags & 1) | ((flags & 8) != 0 ? 32 : 0)).G(71, m).G(72, n);
                chunk.Trailer = new Dxf().G(0, "SEQEND").ToString();
                break;
            }

            case "VERTEX":
            {
                var flags = b.RC();
                var p = b.BD3();
                var start = b.BD();
                double end = start;
                if (start >= 0)
                {
                    end = b.BD();
                }
                else
                {
                    start = -start;
                    end = start;
                }

                var bulge = b.BD();
                if (_generation >= 24)
                {
                    b.BL();
                }

                b.BD();
                d.P(10, p).G(70, flags);
                if (start != 0 || end != 0)
                {
                    d.G(40, start).G(41, end);
                }

                if (bulge != 0)
                {
                    d.G(42, bulge);
                }

                break;
            }

            case "VERTEX3D":
            case "VERTEXMESH":
            case "VERTEXPFACE":
            {
                var flags = b.RC();
                var p = b.BD3();
                var vflags = kind == "VERTEX3D" ? 32 : kind == "VERTEXMESH" ? 64 : 128 | 64;
                d.P(10, p).G(70, vflags | (kind == "VERTEX3D" ? flags & 8 : 0));
                break;
            }

            case "VERTEXPFACEFACE":
            {
                var a = b.BS();
                var bb = b.BS();
                var c = b.BS();
                var dd = b.BS();
                d.P(10, (0, 0, 0)).G(70, 128).G(71, a).G(72, bb).G(73, c).G(74, dd);
                break;
            }

            case "3DFACE":
            {
                var noFlag = _generation == 15 ? b.Bit() : b.Bit();
                var zZero = b.Bit();
                var corners = new (double X, double Y, double Z)[4];
                double x = b.RD(), y = b.RD(), z = zZero ? 0 : b.RD();
                corners[0] = (x, y, z);
                for (var i = 1; i < 4; i++)
                {
                    x = b.DD(x);
                    y = b.DD(y);
                    z = b.DD(z);
                    corners[i] = (x, y, z);
                }

                var invisible = noFlag ? 0 : b.BS();
                for (var i = 0; i < 4; i++)
                {
                    d.P(10 + i, corners[i]);
                }

                d.G(70, invisible);
                break;
            }

            case "SOLID":
            case "TRACE":
            {
                var thickness = b.BT();
                var elevation = b.BD();
                var c = new (double X, double Y)[4];
                for (var i = 0; i < 4; i++)
                {
                    c[i] = (b.RD(), b.RD());
                }

                var n = b.BE();
                for (var i = 0; i < 4; i++)
                {
                    d.P(10 + i, (c[i].X, c[i].Y, elevation));
                }

                Common(d, thickness, n);
                break;
            }

            case "ELLIPSE":
            {
                var c = b.BD3();
                var axis = b.BD3();
                var n = b.BD3();
                var ratio = b.BD();
                var a0 = b.BD();
                var a1 = b.BD();
                d.P(10, c).P(11, axis).P(210, n).G(40, ratio).G(41, a0).G(42, a1);
                break;
            }

            case "RAY":
            case "XLINE":
            {
                var p = b.BD3();
                var v = b.BD3();
                d.P(10, p).P(11, v);
                break;
            }

            case "SPLINE":
                ReadSpline(b, d);
                break;
            case "HATCH":
                ReadHatch(b, d);
                break;
            case "DIMENSION":
            {
                var block = BlockHandleAfter(handles!, layerIndex + 1, true);
                if (block == 0 || !_blocks.TryGetValue(block, out var info))
                {
                    return null;
                }

                d.G(2, info.Name).G(70, record.Type - 20);
                break;
            }

            default:
                return null;
        }

        if (b.Overrun)
        {
            throw new ModelFormatException("An entity overruns its data.");
        }

        chunk.Text = d.ToString();
        return chunk;
    }

    private static void Common(Dxf d, double thickness, (double X, double Y, double Z) normal)
    {
        if (thickness != 0)
        {
            d.G(39, thickness);
        }

        if (normal != (0, 0, 1))
        {
            d.P(210, normal);
        }
    }

    private void ReadText(DwgBits b, Dxf d, string kind)
    {
        var flags = b.RC();
        double elevation = 0;
        if ((flags & 1) == 0)
        {
            elevation = b.RD();
        }

        var x = b.RD();
        var y = b.RD();
        double ax = x, ay = y;
        if ((flags & 2) == 0)
        {
            ax = b.DD(x);
            ay = b.DD(y);
        }

        var n = b.BE();
        var thickness = b.BT();
        double oblique = 0, rotation = 0, width = 1;
        if ((flags & 4) == 0)
        {
            oblique = b.RD();
        }

        if ((flags & 8) == 0)
        {
            rotation = b.RD();
        }

        var height = b.RD();
        if ((flags & 0x10) == 0)
        {
            width = b.RD();
        }

        var text = b.TV();
        int generation = 0, halign = 0, valign = 0;
        if ((flags & 0x20) == 0)
        {
            generation = b.BS();
        }

        if ((flags & 0x40) == 0)
        {
            halign = b.BS();
        }

        if ((flags & 0x80) == 0)
        {
            valign = b.BS();
        }

        d.P(10, (x, y, elevation)).G(40, height).G(1, text).G(50, rotation * Deg);
        if (width != 1)
        {
            d.G(41, width);
        }

        if (oblique != 0)
        {
            d.G(51, oblique * Deg);
        }

        if (generation != 0)
        {
            d.G(71, generation);
        }

        if (halign != 0)
        {
            d.G(72, halign);
        }

        if (valign != 0)
        {
            d.G(73, valign);
        }

        if (halign != 0 || valign != 0)
        {
            d.P(11, (ax, ay, elevation));
        }

        Common(d, thickness, n);
        if (kind is "ATTRIB" or "ATTDEF")
        {
            if (_generation >= 24)
            {
                b.RC();
            }

            if (_generation >= 32 && kind == "ATTRIB")
            {
                b.RC();
            }

            var tag = b.TV();
            b.BS();
            var attFlags = b.RC();
            if (_generation >= 21)
            {
                b.Bit();
            }

            var prompt = kind == "ATTDEF" ? b.TV() : "";
            d.G(2, tag).G(70, attFlags);
            if (kind == "ATTDEF")
            {
                d.G(3, prompt);
            }
        }
    }

    private void ReadLwPolyline(DwgBits b, Dxf d)
    {
        var flag = b.BS();
        double width = 0, elevation = 0, thickness = 0;
        if ((flag & 4) != 0)
        {
            width = b.BD();
        }

        if ((flag & 8) != 0)
        {
            elevation = b.BD();
        }

        if ((flag & 2) != 0)
        {
            thickness = b.BD();
        }

        var normal = (X: 0.0, Y: 0.0, Z: 1.0);
        if ((flag & 1) != 0)
        {
            normal = b.BD3();
        }

        var count = b.BL();
        var bulges = (flag & 16) != 0 ? b.BL() : 0;
        var ids = (_generation >= 24 && (flag & 1024) != 0) ? b.BL() : 0;
        var widths = (flag & 32) != 0 ? b.BL() : 0;
        if (count < 0 || count > 10_000_000 || bulges < 0 || bulges > count || widths < 0 || widths > count || ids < 0 || ids > count || b.Position + (long)count * 2 > b.End)
        {
            throw new ModelFormatException("A polyline declares an impossible number of vertices.");
        }

        var x = new double[count];
        var y = new double[count];
        for (var i = 0; i < count; i++)
        {
            Cancel.Every(_cancel, i);
            x[i] = i == 0 ? b.RD() : b.DD(x[i - 1]);
            y[i] = i == 0 ? b.RD() : b.DD(y[i - 1]);
        }

        var bulge = new double[count];
        for (var i = 0; i < bulges; i++)
        {
            bulge[i] = b.BD();
        }

        for (var i = 0; i < ids; i++)
        {
            b.BL();
        }

        var sw = new double[count];
        var ew = new double[count];
        for (var i = 0; i < widths; i++)
        {
            sw[i] = b.BD();
            ew[i] = b.BD();
        }

        d.G(90, count).G(70, (flag & 512) != 0 ? 1 : 0);
        if (width != 0)
        {
            d.G(43, width);
        }

        if (elevation != 0)
        {
            d.G(38, elevation);
        }

        if (thickness != 0)
        {
            d.G(39, thickness);
        }

        for (var i = 0; i < count; i++)
        {
            d.P2(10, x[i], y[i]);
            if (sw[i] != 0)
            {
                d.G(40, sw[i]);
            }

            if (ew[i] != 0)
            {
                d.G(41, ew[i]);
            }

            if (bulge[i] != 0)
            {
                d.G(42, bulge[i]);
            }
        }

        if (normal != (0, 0, 1))
        {
            d.P(210, normal);
        }
    }

    private void ReadSpline(DwgBits b, Dxf d)
    {
        var scenario = b.BL();
        var flags1 = 0;
        if (_generation >= 27)
        {
            flags1 = b.BL();
            b.BL();
            scenario = (flags1 & 1) != 0 ? 2 : 1;
        }

        var degree = b.BL();
        var fit = new List<(double, double, double)>();
        var knots = new List<double>();
        var control = new List<(double, double, double)>();
        var weights = new List<double>();
        bool rational = false, closed = false, periodic = false;
        if (scenario == 2)
        {
            b.BD();
            b.BD3();
            b.BD3();
            var n = b.BL();
            if (n < 0 || n > 10_000_000 || b.Position + (long)n * 3 > b.End)
            {
                throw new ModelFormatException("A spline declares an impossible number of fit points.");
            }

            for (var i = 0; i < n; i++)
            {
                fit.Add(b.BD3());
            }
        }
        else
        {
            rational = b.Bit();
            closed = b.Bit();
            periodic = b.Bit();
            b.BD();
            b.BD();
            var nk = b.BL();
            var nc = b.BL();
            var weight = b.Bit();
            if (nk < 0 || nc < 0 || nk > 10_000_000 || nc > 10_000_000 || b.Position + ((long)nk + nc) * 2 > b.End)
            {
                throw new ModelFormatException("A spline declares impossible knot or control point counts.");
            }

            for (var i = 0; i < nk; i++)
            {
                knots.Add(b.BD());
            }

            for (var i = 0; i < nc; i++)
            {
                control.Add(b.BD3());
                if (weight)
                {
                    weights.Add(b.BD());
                }
            }
        }

        d.G(70, (closed ? 1 : 0) | (periodic ? 2 : 0) | (rational ? 4 : 8)).G(71, degree).G(72, knots.Count).G(73, control.Count).G(74, fit.Count);
        foreach (var k in knots)
        {
            d.G(40, k);
        }

        foreach (var w in weights)
        {
            d.G(41, w);
        }

        foreach (var c in control)
        {
            d.P(10, c);
        }

        foreach (var f in fit)
        {
            d.P(11, f);
        }
    }

    private void ReadHatch(DwgBits b, Dxf d)
    {
        var gradient = false;
        if (_generation >= 18)
        {
            gradient = b.BL() != 0;
            b.BL();
            b.BD();
            b.BD();
            b.BL();
            b.BD();
            var colors = b.BL();
            if (colors < 0 || colors > 256)
            {
                throw new ModelFormatException("A hatch declares an impossible number of gradient colours.");
            }

            for (var i = 0; i < colors; i++)
            {
                b.BD();
                b.BS();
                b.BL();
                var type = b.RC();
                if ((type & 1) != 0)
                {
                    b.TV();
                }

                if ((type & 2) != 0)
                {
                    b.TV();
                }
            }

            b.TV();
        }

        var elevation = b.BD();
        var n = b.BD3();
        var name = b.TV();
        var solid = b.Bit();
        var associative = b.Bit();
        var paths = b.BL();
        if (paths < 0 || paths > 100_000)
        {
            throw new ModelFormatException("A hatch declares an impossible number of paths.");
        }

        d.P(10, (0, 0, elevation)).P(210, n).G(2, name).G(70, solid ? 1 : 0).G(71, associative ? 1 : 0).G(91, paths);
        _ = gradient;
        for (var p = 0; p < paths; p++)
        {
            Cancel.Every(_cancel, p);
            var flag = b.BL();
            d.G(92, flag);
            if ((flag & 2) == 0)
            {
                var segments = b.BL();
                if (segments < 0 || segments > 1_000_000)
                {
                    throw new ModelFormatException("A hatch path declares an impossible number of edges.");
                }

                d.G(93, segments);
                for (var s = 0; s < segments; s++)
                {
                    var type = b.RC();
                    d.G(72, type);
                    switch (type)
                    {
                        case 1:
                            d.P2(10, b.RD(), b.RD()).P2(11, b.RD(), b.RD());
                            break;
                        case 2:
                        {
                            var cx = b.RD();
                            var cy = b.RD();
                            var r = b.BD();
                            var a0 = b.BD();
                            var a1 = b.BD();
                            var ccw = b.Bit();
                            d.P2(10, cx, cy).G(40, r).G(50, a0 * Deg).G(51, a1 * Deg).G(73, ccw ? 1 : 0);
                            break;
                        }

                        case 3:
                        {
                            var cx = b.RD();
                            var cy = b.RD();
                            var mx = b.RD();
                            var my = b.RD();
                            var ratio = b.BD();
                            var a0 = b.BD();
                            var a1 = b.BD();
                            var ccw = b.Bit();
                            d.P2(10, cx, cy).P2(11, mx, my).G(40, ratio).G(50, a0 * Deg).G(51, a1 * Deg).G(73, ccw ? 1 : 0);
                            break;
                        }

                        case 4:
                        {
                            var degree = b.BL();
                            var rational = b.Bit();
                            var periodic = b.Bit();
                            var nk = b.BL();
                            var nc = b.BL();
                            if (nk < 0 || nc < 0 || nk > 1_000_000 || nc > 1_000_000)
                            {
                                throw new ModelFormatException("A hatch spline declares impossible counts.");
                            }

                            d.G(94, degree).G(73, rational ? 1 : 0).G(74, periodic ? 1 : 0).G(95, nk).G(96, nc);
                            for (var k = 0; k < nk; k++)
                            {
                                d.G(40, b.BD());
                            }

                            for (var k = 0; k < nc; k++)
                            {
                                d.P2(10, b.RD(), b.RD());
                                if (rational)
                                {
                                    d.G(42, b.BD());
                                }
                            }

                            if (_generation >= 27)
                            {
                                var nf = b.BL();
                                if (nf < 0 || nf > 1_000_000)
                                {
                                    throw new ModelFormatException("A hatch spline declares impossible fit points.");
                                }

                                if (nf > 0)
                                {
                                    for (var k = 0; k < nf; k++)
                                    {
                                        b.RD();
                                        b.RD();
                                    }

                                    b.RD();
                                    b.RD();
                                    b.RD();
                                    b.RD();
                                }
                            }

                            break;
                        }

                        default:
                            throw new ModelFormatException("A hatch edge has an unknown type.");
                    }
                }
            }
            else
            {
                var bulges = b.Bit();
                var closed = b.Bit();
                var count = b.BL();
                if (count < 0 || count > 10_000_000 || b.Position + (long)count * 16 > b.End)
                {
                    throw new ModelFormatException("A hatch polyline declares an impossible number of vertices.");
                }

                d.G(72, bulges ? 1 : 0).G(73, closed ? 1 : 0).G(93, count);
                for (var v = 0; v < count; v++)
                {
                    d.P2(10, b.RD(), b.RD());
                    if (bulges)
                    {
                        d.G(42, b.BD());
                    }
                }
            }

            var boundaryHandles = b.BL();
            if (boundaryHandles < 0 || boundaryHandles > 100_000)
            {
                throw new ModelFormatException("A hatch path declares impossible boundary objects.");
            }

            d.G(97, boundaryHandles);
        }

        var style = b.BS();
        var pattern = b.BS();
        d.G(75, style).G(76, pattern);
        if (!solid)
        {
            var angle = b.BD();
            var scale = b.BD();
            var dbl = b.Bit();
            var lines = b.BS();
            if (lines < 0 || lines > 10_000)
            {
                throw new ModelFormatException("A hatch declares an impossible number of pattern lines.");
            }

            d.G(52, angle * Deg).G(41, scale).G(77, dbl ? 1 : 0).G(78, lines);
            for (var l = 0; l < lines; l++)
            {
                var la = b.BD();
                var ox = b.BD();
                var oy = b.BD();
                var dx = b.BD();
                var dy = b.BD();
                var dashes = b.BS();
                if (dashes < 0 || dashes > 1000)
                {
                    throw new ModelFormatException("A hatch pattern line declares impossible dashes.");
                }

                d.G(53, la * Deg).G(43, ox).G(44, oy).G(45, dx).G(46, dy).G(79, dashes);
                for (var k = 0; k < dashes; k++)
                {
                    d.G(49, b.BD());
                }
            }
        }
    }
}
