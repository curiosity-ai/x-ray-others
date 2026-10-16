// Ported from ps/graphics_ops.py: path construction, painting, clipping, transforms and the
// graphics state operators.

using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>Registers the path, painting, clipping and transformation operators.</summary>
public static class GraphicsOperators
{
    public static void Register(OperatorRegistry registry, RenderModelBuilder builder)
    {
        registry.Register("newpath", OpNewPath);
        registry.Register("moveto", OpMoveTo, 2);
        registry.Register("lineto", OpLineTo, 2);
        registry.Register("curveto", OpCurveTo, 6);
        registry.Register("rmoveto", OpRMoveTo, 2);
        registry.Register("rlineto", OpRLineTo, 2);
        registry.Register("rcurveto", OpRCurveTo, 6);
        registry.Register("closepath", OpClosePath);
        registry.Register("rectpath", OpRectPath, 4);
        registry.Register("arc", ctx => OpArc(ctx, false), 5);
        registry.Register("arcn", ctx => OpArc(ctx, true), 5);
        registry.Register("arcto", OpArcTo, 5);
        registry.Register("arct", OpArct, 5);

        var interpreter = new PsInterpreter(registry);

        registry.Register("stroke", ctx => OpStroke(ctx, builder));
        registry.Register("fill", ctx => OpFill(ctx, builder, false));
        registry.Register("eofill", ctx => OpFill(ctx, builder, true));
        registry.Register("clip", ctx => OpClip(ctx, builder, false));
        registry.Register("eoclip", ctx => OpClip(ctx, builder, true));
        registry.Register("initclip", ctx => OpInitClip(ctx, builder));
        registry.Register("clippath", OpClipPath);
        registry.Register("rectclip", ctx => OpRectClip(ctx, builder, interpreter), 1);
        registry.Register("strokepath", OpStrokePath);
        registry.Register("flattenpath", OpFlattenPath);
        registry.Register("reversepath", OpReversePath);
        registry.Register("pathbbox", OpPathBBox);
        registry.Register("pathforall", ctx => OpPathForall(ctx, interpreter), 4);
        registry.Register("showpage", ctx => OpShowPage(ctx, builder));
        registry.Register("setpagedevice", ctx => OpSetPageDevice(ctx, builder), 1);

        registry.Register("setlinewidth", ctx => State(ctx).LineWidth = PopNumber(ctx), 1);
        registry.Register("currentpoint", OpCurrentPoint);
        registry.Register("setlinecap", ctx => State(ctx).LineCap = (int)Py.Int(PopNumber(ctx)), 1);
        registry.Register("setlinejoin", ctx => State(ctx).LineJoin = (int)Py.Int(PopNumber(ctx)), 1);
        registry.Register("setmiterlimit", ctx => State(ctx).MiterLimit = PopNumber(ctx), 1);
        registry.Register("setdash", ctx => OpSetDash(ctx, interpreter), 2);
        registry.Register("setflat", OpSetFlat, 1);
        registry.Register("currentflat", ctx => ctx.OperandStack.Push(State(ctx).Flatness));
        registry.Register("setstrokeadjust", OpSetStrokeAdjust, 1);

        registry.Register("translate", ctx => { double ty = PopNumber(ctx); double tx = PopNumber(ctx); ConcatCtm(ctx, new Matrix(1.0, 0.0, 0.0, 1.0, tx, ty)); }, 2);
        registry.Register("scale", ctx => { double sy = PopNumber(ctx); double sx = PopNumber(ctx); ConcatCtm(ctx, new Matrix(sx, 0.0, 0.0, sy, 0.0, 0.0)); }, 2);
        registry.Register("rotate", OpRotate, 1);
        registry.Register("concat", ctx => ConcatCtm(ctx, PopMatrix(ctx, interpreter)), 1);
        registry.Register("transform", OpTransform, 2);
        registry.Register("dtransform", OpDTransform, 2);
        registry.Register("itransform", OpITransform, 2);
        registry.Register("idtransform", OpIDTransform, 2);
        registry.Register("concatmatrix", ctx => OpConcatMatrix(ctx, interpreter), 3);
        registry.Register("currentmatrix", OpCurrentMatrix, 1);
        registry.Register("setmatrix", ctx => OpSetMatrix(ctx, interpreter), 1);
        registry.Register("rectfill", ctx => OpRectFill(ctx, builder, interpreter), 1);
        registry.Register("rectstroke", ctx => OpRectStroke(ctx, builder, interpreter), 1);

        registry.Register("gsave", ctx => { ctx.GraphicsStateStack.Push(State(ctx).Clone()); builder.SaveState(); });
        registry.Register("grestore", ctx => OpGRestore(ctx, builder));
        registry.Register("save", ctx => ctx.OperandStack.Push(PsSaveState.Save(ctx)));
        registry.Register("restore", OpRestore);
    }

    internal static GraphicsState State(PsExecutionContext ctx) => ctx.GraphicsStateStack.Peek();

    private static void OpNewPath(PsExecutionContext ctx)
    {
        var state = State(ctx);
        state.CurrentPath = new RenderPath();
        state.CurrentPoint = null;
        state.SubpathStart = null;
    }

    private static void OpMoveTo(PsExecutionContext ctx)
    {
        double y = PopNumber(ctx);
        double x = PopNumber(ctx);
        UpdateTextPosition(ctx, x, y);
        State(ctx).CurrentPoint = (x, y);
        State(ctx).SubpathStart = (x, y);
        AppendSegment(ctx, PathSegmentKind.Move, [ApplyCtm(State(ctx).Ctm, x, y)]);
    }

    private static void OpLineTo(PsExecutionContext ctx)
    {
        double y = PopNumber(ctx);
        double x = PopNumber(ctx);
        UpdateTextPosition(ctx, x, y);
        State(ctx).CurrentPoint = (x, y);
        AppendSegment(ctx, PathSegmentKind.Line, [ApplyCtm(State(ctx).Ctm, x, y)]);
    }

    private static void OpCurveTo(PsExecutionContext ctx)
    {
        double y3 = PopNumber(ctx), x3 = PopNumber(ctx);
        double y2 = PopNumber(ctx), x2 = PopNumber(ctx);
        double y1 = PopNumber(ctx), x1 = PopNumber(ctx);
        UpdateTextPosition(ctx, x3, y3);
        State(ctx).CurrentPoint = (x3, y3);
        var ctm = State(ctx).Ctm;
        AppendSegment(ctx, PathSegmentKind.Curve, [ApplyCtm(ctm, x1, y1), ApplyCtm(ctm, x2, y2), ApplyCtm(ctm, x3, y3)]);
    }

    private static void OpRMoveTo(PsExecutionContext ctx)
    {
        double dy = PopNumber(ctx);
        double dx = PopNumber(ctx);
        var current = State(ctx).CurrentPoint ?? throw new PsRangeError("no current point");
        double x = current.X + dx;
        double y = current.Y + dy;
        UpdateTextPosition(ctx, x, y);
        State(ctx).CurrentPoint = (x, y);
        State(ctx).SubpathStart = (x, y);
        AppendSegment(ctx, PathSegmentKind.Move, [ApplyCtm(State(ctx).Ctm, x, y)]);
    }

    private static void OpRLineTo(PsExecutionContext ctx)
    {
        double dy = PopNumber(ctx);
        double dx = PopNumber(ctx);
        var current = State(ctx).CurrentPoint ?? throw new PsRangeError("no current point");
        double x = current.X + dx;
        double y = current.Y + dy;
        UpdateTextPosition(ctx, x, y);
        State(ctx).CurrentPoint = (x, y);
        AppendSegment(ctx, PathSegmentKind.Line, [ApplyCtm(State(ctx).Ctm, x, y)]);
    }

    private static void OpRCurveTo(PsExecutionContext ctx)
    {
        double dy3 = PopNumber(ctx), dx3 = PopNumber(ctx);
        double dy2 = PopNumber(ctx), dx2 = PopNumber(ctx);
        double dy1 = PopNumber(ctx), dx1 = PopNumber(ctx);
        var current = State(ctx).CurrentPoint ?? throw new PsRangeError("no current point");
        var (x0, y0) = current;
        double x1 = x0 + dx1, y1 = y0 + dy1;
        double x2 = x0 + dx2, y2 = y0 + dy2;
        double x3 = x0 + dx3, y3 = y0 + dy3;
        UpdateTextPosition(ctx, x3, y3);
        State(ctx).CurrentPoint = (x3, y3);
        var ctm = State(ctx).Ctm;
        AppendSegment(ctx, PathSegmentKind.Curve, [ApplyCtm(ctm, x1, y1), ApplyCtm(ctm, x2, y2), ApplyCtm(ctm, x3, y3)]);
    }

    private static void OpClosePath(PsExecutionContext ctx)
    {
        AppendSegment(ctx, PathSegmentKind.Close, []);
        var state = State(ctx);
        if (state.SubpathStart is { } start)
            state.CurrentPoint = start;
    }

    internal static void OpRectPath(PsExecutionContext ctx)
    {
        double height = PopNumber(ctx);
        double width = PopNumber(ctx);
        double y = PopNumber(ctx);
        double x = PopNumber(ctx);
        var ctm = State(ctx).Ctm;
        State(ctx).CurrentPath = RectanglePath(ctm, x, y, width, height);
    }

    private static RenderPath RectanglePath(Matrix ctm, double x, double y, double width, double height) => new(new List<PathSegment>
    {
        new(PathSegmentKind.Move, [ApplyCtm(ctm, x, y)]),
        new(PathSegmentKind.Line, [ApplyCtm(ctm, x + width, y)]),
        new(PathSegmentKind.Line, [ApplyCtm(ctm, x + width, y + height)]),
        new(PathSegmentKind.Line, [ApplyCtm(ctm, x, y + height)]),
        new(PathSegmentKind.Close, []),
    });

    private static void OpArc(PsExecutionContext ctx, bool clockwise)
    {
        double angle2 = PopNumber(ctx);
        double angle1 = PopNumber(ctx);
        double radius = PopNumber(ctx);
        double cy = PopNumber(ctx);
        double cx = PopNumber(ctx);
        if (clockwise)
        {
            if (angle2 > angle1)
                angle2 -= 360.0;
        }
        else if (angle2 < angle1)
        {
            angle2 += 360.0;
        }
        double ex = cx + radius * Math.Cos(Py.Radians(angle2));
        double ey = cy + radius * Math.Sin(Py.Radians(angle2));
        UpdateTextPosition(ctx, ex, ey);
        State(ctx).CurrentPoint = (ex, ey);
        AppendArc(ctx, cx, cy, radius, angle1, angle2);
    }

    private static void OpArcTo(PsExecutionContext ctx)
    {
        double radius = PopNumber(ctx);
        double y2 = PopNumber(ctx);
        double x2 = PopNumber(ctx);
        double y1 = PopNumber(ctx);
        double x1 = PopNumber(ctx);
        var current = State(ctx).CurrentPoint ?? throw new PsRangeError("no current point");
        var (t1, t2, center, startAngle, endAngle, clockwise) = ArcToGeometry(current, (x1, y1), (x2, y2), radius);
        UpdateTextPosition(ctx, t2.X, t2.Y);
        State(ctx).CurrentPoint = t2;
        if (clockwise)
        {
            if (endAngle > startAngle)
                endAngle -= 360.0;
        }
        else if (endAngle < startAngle)
        {
            endAngle += 360.0;
        }
        AppendArc(ctx, center.X, center.Y, radius, startAngle, endAngle);
        ctx.OperandStack.Push(t1.X);
        ctx.OperandStack.Push(t1.Y);
        ctx.OperandStack.Push(t2.X);
        ctx.OperandStack.Push(t2.Y);
    }

    private static void OpArct(PsExecutionContext ctx)
    {
        OpArcTo(ctx);
        for (int i = 0; i < 4; i++)
            ctx.OperandStack.Pop();
    }

    internal static void OpStroke(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        if (ctx.CharpathMode)
            return;
        var state = State(ctx);
        if (!ctx.InType3Glyph && state.Dash.Length == 0 && HasNonUniformAxisAlignedStrokeScale(state.Ctm))
        {
            if (EmitNonUniformStrokeOutline(state, builder))
            {
                state.CurrentPath = new RenderPath();
                return;
            }
        }
        double strokeScale = StrokeScaleFromCtm(state.Ctm);
        double lineWidth = state.LineWidth * strokeScale;
        var dash = state.Dash.Select(v => v * strokeScale).ToArray();
        double dashPhase = state.DashPhase * strokeScale;
        var style = new StrokeStyle(lineWidth, state.LineCap, state.LineJoin, state.MiterLimit, dash, dashPhase);
        builder.AddPath(state.CurrentPath, style, null, strokePaint: state.StrokePaint, overprint: state.Overprint);
        state.CurrentPath = new RenderPath();
    }

    private static bool EmitNonUniformStrokeOutline(GraphicsState state, RenderModelBuilder builder)
    {
        var inverse = InvertCtm(state.Ctm);
        if (inverse is null)
            return false;
        var userPath = TransformPath(state.CurrentPath, inverse.Value);
        if (userPath.Segments.Count == 0)
            return false;
        int curveSteps = CurveStepsFromFlatness(state.Flatness);
        var subpaths = FlattenToPoints(userPath, curveSteps);
        if (subpaths.Count == 0)
            return false;
        var tempState = state.Clone();
        var strokePaint = state.StrokePaint;
        bool emitted = false;
        foreach (var (points, closed) in subpaths)
        {
            var outlines = StrokeOutline(points, closed, tempState);
            if (outlines is null || outlines.Count == 0)
                continue;
            var combined = new List<PathSegment>();
            foreach (var outline in outlines)
            {
                var transformed = outline.Select(pt => ApplyCtm(state.Ctm, pt.X, pt.Y)).ToList();
                combined.AddRange(PointsToPath(transformed));
            }
            if (combined.Count > 0)
            {
                builder.AddPath(new RenderPath(combined), null, strokePaint, "nonzero", overprint: state.Overprint);
                emitted = true;
            }
        }
        return emitted;
    }

    internal static double StrokeScaleFromCtm(Matrix ctm)
    {
        double sx = Py.Hypot(ctm.A, ctm.B);
        double sy = Py.Hypot(ctm.C, ctm.D);
        if (sx <= 1e-12 && sy <= 1e-12)
            return 1.0;
        if (sx <= 1e-12)
            return sy;
        if (sy <= 1e-12)
            return sx;
        return (sx + sy) * 0.5;
    }

    private static bool HasNonUniformAxisAlignedStrokeScale(Matrix ctm)
    {
        double sx = Py.Hypot(ctm.A, ctm.B);
        double sy = Py.Hypot(ctm.C, ctm.D);
        if (sx <= 1e-12 || sy <= 1e-12)
            return false;
        if (Math.Abs(sx - sy) <= 1e-9)
            return false;
        return Math.Abs(ctm.B) <= 1e-9 && Math.Abs(ctm.C) <= 1e-9;
    }

    internal static void OpFill(PsExecutionContext ctx, RenderModelBuilder builder, bool evenOdd)
    {
        if (ctx.CharpathMode)
            return;
        var state = State(ctx);
        builder.AddPath(state.CurrentPath, null, state.FillPaint, evenOdd ? "evenodd" : "nonzero", overprint: state.Overprint);
        state.CurrentPath = new RenderPath();
    }

    private static void OpStrokePath(PsExecutionContext ctx)
    {
        var state = State(ctx);
        if (state.CurrentPath.Segments.Count == 0)
            return;
        int curveSteps = CurveStepsFromFlatness(state.Flatness);
        var subpaths = FlattenToPoints(state.CurrentPath, curveSteps);
        var outlineSegments = new List<PathSegment>();
        foreach (var (points, closed) in subpaths)
        {
            var outlines = StrokeOutline(points, closed, state);
            if (outlines is null || outlines.Count == 0)
                continue;
            foreach (var outline in outlines)
                outlineSegments.AddRange(PointsToPath(outline));
        }
        state.CurrentPath = new RenderPath(outlineSegments);
        ResetCurrentPoint(state);
    }

    private static void OpFlattenPath(PsExecutionContext ctx)
    {
        var state = State(ctx);
        if (state.CurrentPath.Segments.Count == 0)
            return;
        state.CurrentPath = FlattenPathSegments(state.CurrentPath, CurveStepsFromFlatness(state.Flatness));
        ResetCurrentPoint(state);
    }

    private static void OpReversePath(PsExecutionContext ctx)
    {
        var state = State(ctx);
        if (state.CurrentPath.Segments.Count == 0)
            return;
        state.CurrentPath = ReversePath(state.CurrentPath);
        ResetCurrentPoint(state);
    }

    private static void OpPathBBox(PsExecutionContext ctx)
    {
        var state = State(ctx);
        var path = state.CurrentPath;
        var inverse = InvertCtm(state.Ctm);
        if (inverse is not null)
            path = TransformPath(path, inverse.Value);
        var bounds = PathBounds(path) ?? throw new PsRangeError("no current point");
        ctx.OperandStack.Push(bounds.Llx);
        ctx.OperandStack.Push(bounds.Lly);
        ctx.OperandStack.Push(bounds.Urx);
        ctx.OperandStack.Push(bounds.Ury);
    }

    private static void OpPathForall(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var closeProc = ctx.OperandStack.Pop();
        var curveProc = ctx.OperandStack.Pop();
        var lineProc = ctx.OperandStack.Pop();
        var moveProc = ctx.OperandStack.Pop();
        if (moveProc is not PsProcedure mp || lineProc is not PsProcedure lp || curveProc is not PsProcedure cp || closeProc is not PsProcedure clp)
            throw new PsTypeError("pathforall expects procedures");
        var state = State(ctx);
        var segments = state.CurrentPath.Segments.ToList();
        Point? current = null;
        Point? start = null;
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    current = segment.Points[0];
                    start = current;
                    ctx.OperandStack.Push(current.Value.X);
                    ctx.OperandStack.Push(current.Value.Y);
                    interpreter.ExecuteProcedure(mp, ctx);
                    break;
                case PathSegmentKind.Line:
                    current = segment.Points[0];
                    ctx.OperandStack.Push(current.Value.X);
                    ctx.OperandStack.Push(current.Value.Y);
                    interpreter.ExecuteProcedure(lp, ctx);
                    break;
                case PathSegmentKind.Curve:
                    var (p1, p2, p3) = (segment.Points[0], segment.Points[1], segment.Points[2]);
                    current = p3;
                    ctx.OperandStack.Push(p1.X);
                    ctx.OperandStack.Push(p1.Y);
                    ctx.OperandStack.Push(p2.X);
                    ctx.OperandStack.Push(p2.Y);
                    ctx.OperandStack.Push(p3.X);
                    ctx.OperandStack.Push(p3.Y);
                    interpreter.ExecuteProcedure(cp, ctx);
                    break;
                case PathSegmentKind.Close:
                    if (start is not null)
                        current = start;
                    interpreter.ExecuteProcedure(clp, ctx);
                    break;
            }
        }
        state.CurrentPoint = current is { } c ? (c.X, c.Y) : null;
        state.SubpathStart = start is { } s ? (s.X, s.Y) : null;
    }

    internal static void OpClip(PsExecutionContext ctx, RenderModelBuilder builder, bool evenOdd)
    {
        if (ctx.CharpathMode)
            return;
        var state = State(ctx);
        var clipPath = state.CurrentPath.Clone();
        builder.Clip(clipPath, evenOdd ? "evenodd" : "nonzero");
        // PostScript clip does not clear the current path; snapshot the clip path.
        state.ClipPath = clipPath;
    }

    private static RenderPath PageBox(double width, double height) => new(new List<PathSegment>
    {
        new(PathSegmentKind.Move, [new Point(0.0, 0.0)]),
        new(PathSegmentKind.Line, [new Point(width, 0.0)]),
        new(PathSegmentKind.Line, [new Point(width, height)]),
        new(PathSegmentKind.Line, [new Point(0.0, height)]),
        new(PathSegmentKind.Close, []),
    });

    private static void OpClipPath(PsExecutionContext ctx)
    {
        if (ctx.CharpathMode)
            return;
        var state = State(ctx);
        if (state.ClipPath is not null)
            state.CurrentPath = state.ClipPath.Clone();
        else if (ctx.DefaultPageSize is null)
            state.CurrentPath = new RenderPath();
        else
            state.CurrentPath = PageBox(ctx.DefaultPageSize.Value.Width, ctx.DefaultPageSize.Value.Height);
        state.CurrentPoint = null;
        state.SubpathStart = null;
    }

    private static void OpInitClip(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        if (ctx.CharpathMode || ctx.DefaultPageSize is null)
            return;
        // initclip resets clipping to the full page in device space.
        var path = PageBox(ctx.DefaultPageSize.Value.Width, ctx.DefaultPageSize.Value.Height);
        builder.Clip(path);
        State(ctx).ClipPath = path;
        State(ctx).CurrentPath = new RenderPath();
    }

    private static void OpShowPage(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        if (ctx.CharpathMode)
            return;
        builder.Document();
    }

    private static void OpSetPageDevice(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        var value = ctx.OperandStack.Pop();
        if (value is not PsDict d)
            return;
        var pageSize = d.Get("PageSize");
        List<object?> items;
        if (pageSize is PsArray a)
            items = a.Items;
        else if (pageSize is List<object?> l)
            items = l;
        else
            return;
        if (items.Count < 2)
            return;
        if (!PsOps.IsNumeric(items[0]) || !PsOps.IsNumeric(items[1]))
            return;
        double width = PsOps.ToDouble(items[0]);
        double height = PsOps.ToDouble(items[1]);
        if (width <= 0 || height <= 0)
            return;
        string orientation = ctx.Dsc?.Orientation is { } o ? Py.Strip(o).ToLowerInvariant() : "";
        bool hasViewingOrientation = !string.IsNullOrEmpty(ctx.Dsc?.ViewingOrientation);
        if (orientation == "landscape" && width < height && !hasViewingOrientation)
            (width, height) = (height, width);
        ctx.DefaultPageSize = (width, height);
        builder.SetDefaultPageSize(width, height);
        var activePage = builder.ActivePage;
        if (activePage is null)
            return;
        // Some printer prologs emit save/restore around page-device setup before any marks;
        // keep the open page in sync with the new size.
        if (activePage.Commands.All(c => c is StateSaveCommand or StateRestoreCommand))
        {
            activePage.Width = width;
            activePage.Height = height;
        }
    }

    private static void OpCurrentPoint(PsExecutionContext ctx)
    {
        var point = State(ctx).CurrentPoint ?? throw new PsRangeError("no current point");
        ctx.OperandStack.Push(point.X);
        ctx.OperandStack.Push(point.Y);
    }

    private static void OpSetDash(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        double phase = PopNumber(ctx);
        var arrayObj = ctx.OperandStack.Pop();
        var values = ResolveArrayNumbers(ctx, interpreter, arrayObj);
        State(ctx).Dash = [.. values];
        State(ctx).DashPhase = phase;
    }

    private static void OpSetFlat(PsExecutionContext ctx)
    {
        double value = PopNumber(ctx);
        if (value < 0)
            throw new PsRangeError("flatness must be non-negative");
        State(ctx).Flatness = value;
    }

    private static void OpSetStrokeAdjust(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (value is bool b)
        {
            State(ctx).StrokeAdjust = b;
            return;
        }
        if (PsOps.IsNumeric(value))
        {
            State(ctx).StrokeAdjust = PsOps.ToDouble(value) != 0;
            return;
        }
        throw new PsTypeError("setstrokeadjust expects boolean");
    }

    private static void OpRectClip(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter)
    {
        var numbers = RectNumbersFromOperand(ctx, interpreter, "rectclip expects 4n numbers");
        var ctm = State(ctx).Ctm;
        var segments = new List<PathSegment>();
        for (int idx = 0; idx < numbers.Count; idx += 4)
            segments.AddRange(RectanglePath(ctm, numbers[idx], numbers[idx + 1], numbers[idx + 2], numbers[idx + 3]).Segments);
        var path = new RenderPath(segments);
        builder.Clip(path);
        var state = State(ctx);
        state.ClipPath = path;
        state.CurrentPath = new RenderPath();
        state.CurrentPoint = null;
        state.SubpathStart = null;
    }

    private static void OpRotate(PsExecutionContext ctx)
    {
        double angle = PopNumber(ctx);
        double r = Py.Radians(angle);
        double c = Math.Cos(r);
        double s = Math.Sin(r);
        ConcatCtm(ctx, new Matrix(c, s, -s, c, 0.0, 0.0));
    }

    private static void OpTransform(PsExecutionContext ctx)
    {
        double y = PopNumber(ctx);
        double x = PopNumber(ctx);
        var p = ApplyCtm(State(ctx).Ctm, x, y);
        ctx.OperandStack.Push(p.X);
        ctx.OperandStack.Push(p.Y);
    }

    private static void OpDTransform(PsExecutionContext ctx)
    {
        var matrix = State(ctx).Ctm;
        if (ctx.OperandStack.Peek() is PsArray or List<object?>)
            matrix = PopMatrix(ctx, null);
        double dy = PopNumber(ctx);
        double dx = PopNumber(ctx);
        ctx.OperandStack.Push(matrix.A * dx + matrix.C * dy);
        ctx.OperandStack.Push(matrix.B * dx + matrix.D * dy);
    }

    private static void OpITransform(PsExecutionContext ctx)
    {
        double y = PopNumber(ctx);
        double x = PopNumber(ctx);
        var inverse = InvertCtm(State(ctx).Ctm) ?? throw new PsRangeError("non-invertible matrix");
        var p = ApplyCtm(inverse, x, y);
        ctx.OperandStack.Push(p.X);
        ctx.OperandStack.Push(p.Y);
    }

    private static void OpIDTransform(PsExecutionContext ctx)
    {
        var matrix = State(ctx).Ctm;
        if (ctx.OperandStack.Peek() is PsArray or List<object?>)
            matrix = PopMatrix(ctx, null);
        double dy = PopNumber(ctx);
        double dx = PopNumber(ctx);
        var inv = InvertCtm(matrix) ?? throw new PsRangeError("non-invertible matrix");
        ctx.OperandStack.Push(inv.A * dx + inv.C * dy);
        ctx.OperandStack.Push(inv.B * dx + inv.D * dy);
    }

    private static void OpConcatMatrix(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var target = ctx.OperandStack.Pop();
        var matrix2 = PopMatrix(ctx, interpreter);
        var matrix1 = PopMatrix(ctx, interpreter);
        var items = target switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("matrix array expected"),
        };
        if (items.Count != 6)
            throw new PsRangeError("matrix must have 6 elements");
        var r = MulTuple(matrix1, matrix2);
        items[0] = r.A;
        items[1] = r.B;
        items[2] = r.C;
        items[3] = r.D;
        items[4] = r.E;
        items[5] = r.F;
        ctx.OperandStack.Push(target);
    }

    /// <summary>The <c>concatmatrix</c> product (matrix1 applied first).</summary>
    private static Matrix MulTuple(Matrix m1, Matrix m2) => new(
        m1.A * m2.A + m1.C * m2.B,
        m1.B * m2.A + m1.D * m2.B,
        m1.A * m2.C + m1.C * m2.D,
        m1.B * m2.C + m1.D * m2.D,
        m1.A * m2.E + m1.C * m2.F + m1.E,
        m1.B * m2.E + m1.D * m2.F + m1.F);

    private static void OpCurrentMatrix(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        var items = value switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("matrix array expected"),
        };
        if (items.Count != 6)
            throw new PsRangeError("matrix must have 6 elements");
        var m = State(ctx).Ctm;
        items[0] = m.A;
        items[1] = m.B;
        items[2] = m.C;
        items[3] = m.D;
        items[4] = m.E;
        items[5] = m.F;
        ctx.OperandStack.Push(value);
    }

    private static void OpSetMatrix(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var matrix = PopMatrix(ctx, interpreter);
        var state = State(ctx);
        var oldCtm = state.Ctm;
        var inverse = InvertCtm(matrix);
        if (inverse is not null)
            RebaseUserPoints(state, oldCtm, inverse.Value);
        state.Ctm = matrix;
    }

    private static void RebaseUserPoints(GraphicsState state, Matrix oldCtm, Matrix inverse)
    {
        if (state.CurrentPoint is { } cp)
        {
            var device = ApplyCtm(oldCtm, cp.X, cp.Y);
            var user = ApplyCtm(inverse, device.X, device.Y);
            state.CurrentPoint = (user.X, user.Y);
        }
        if (state.SubpathStart is { } sp)
        {
            var device = ApplyCtm(oldCtm, sp.X, sp.Y);
            var user = ApplyCtm(inverse, device.X, device.Y);
            state.SubpathStart = (user.X, user.Y);
        }
        state.TextMatrix = RebaseTextTranslation(state.TextMatrix, oldCtm, inverse);
        state.TextLineMatrix = RebaseTextTranslation(state.TextLineMatrix, oldCtm, inverse);
    }

    private static void OpRectFill(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter)
    {
        var numbers = RectNumbersFromOperand(ctx, interpreter, "rect operator expects 4n numbers");
        var state = State(ctx);
        var originalPath = state.CurrentPath.Clone();
        var originalPoint = state.CurrentPoint;
        var originalSubpath = state.SubpathStart;
        try
        {
            for (int idx = 0; idx < numbers.Count; idx += 4)
            {
                for (int k = 0; k < 4; k++)
                    ctx.OperandStack.Push(numbers[idx + k]);
                OpRectPath(ctx);
                OpFill(ctx, builder, false);
            }
        }
        finally
        {
            state.CurrentPath = originalPath;
            state.CurrentPoint = originalPoint;
            state.SubpathStart = originalSubpath;
        }
    }

    private static void OpRectStroke(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter)
    {
        var numbers = RectNumbersFromOperand(ctx, interpreter, "rect operator expects 4n numbers");
        var state = State(ctx);
        var originalPath = state.CurrentPath.Clone();
        var originalPoint = state.CurrentPoint;
        var originalSubpath = state.SubpathStart;
        try
        {
            for (int idx = 0; idx < numbers.Count; idx += 4)
            {
                for (int k = 0; k < 4; k++)
                    ctx.OperandStack.Push(numbers[idx + k]);
                OpRectPath(ctx);
                OpStroke(ctx, builder);
            }
        }
        finally
        {
            state.CurrentPath = originalPath;
            state.CurrentPoint = originalPoint;
            state.SubpathStart = originalSubpath;
        }
    }

    private static void OpGRestore(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        if (ctx.GraphicsStateStack.Count <= 1)
            throw new PsRangeError("graphics state stack underflow");
        ctx.GraphicsStateStack.Pop();
        builder.RestoreState();
    }

    private static void OpRestore(PsExecutionContext ctx)
    {
        if (ctx.OperandStack.Pop() is not PsSaveState save)
            throw new PsTypeError("restore expects save state");
        save.Restore(ctx);
    }

    private static void AppendArc(PsExecutionContext ctx, double cx, double cy, double radius, double angle1, double angle2)
    {
        var segments = ArcSegments(cx, cy, radius, angle1, angle2);
        if (segments.Count == 0)
            return;
        var ctm = State(ctx).Ctm;
        var startPoint = ApplyCtm(ctm, segments[0].P0.X, segments[0].P0.Y);
        AppendSegment(ctx, State(ctx).CurrentPath.Segments.Count > 0 ? PathSegmentKind.Line : PathSegmentKind.Move, [startPoint]);
        foreach (var (_, c1, c2, end) in segments)
            AppendSegment(ctx, PathSegmentKind.Curve, [ApplyCtm(ctm, c1.X, c1.Y), ApplyCtm(ctm, c2.X, c2.Y), ApplyCtm(ctm, end.X, end.Y)]);
    }

    internal static List<(Point P0, Point P1, Point P2, Point P3)> ArcSegments(double cx, double cy, double radius, double angle1, double angle2)
    {
        var segments = new List<(Point, Point, Point, Point)>();
        double delta = angle2 - angle1;
        if (delta == 0)
            return segments;
        long steps = Math.Max(1, Py.Int(Math.Abs(delta) / 90.0) + (Py.Mod(Math.Abs(delta), 90.0) > 1e-6 ? 1 : 0));
        double step = delta / steps;
        for (long index = 0; index < steps; index++)
        {
            double a0 = angle1 + step * index;
            double a1 = a0 + step;
            segments.Add(ArcToBezier(cx, cy, radius, a0, a1));
        }
        return segments;
    }

    private static (Point, Point, Point, Point) ArcToBezier(double cx, double cy, double radius, double angle1, double angle2)
    {
        double theta1 = Py.Radians(angle1);
        double theta2 = Py.Radians(angle2);
        double delta = theta2 - theta1;
        double k = 4.0 / 3.0 * Math.Tan(delta / 4.0);
        double x0 = cx + radius * Math.Cos(theta1);
        double y0 = cy + radius * Math.Sin(theta1);
        double x3 = cx + radius * Math.Cos(theta2);
        double y3 = cy + radius * Math.Sin(theta2);
        double x1 = x0 - radius * Math.Sin(theta1) * k;
        double y1 = y0 + radius * Math.Cos(theta1) * k;
        double x2 = x3 + radius * Math.Sin(theta2) * k;
        double y2 = y3 - radius * Math.Cos(theta2) * k;
        return (new Point(x0, y0), new Point(x1, y1), new Point(x2, y2), new Point(x3, y3));
    }

    private static ((double X, double Y) T1, (double X, double Y) T2, (double X, double Y) Center, double Start, double End, bool Clockwise) ArcToGeometry(
        (double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, double radius)
    {
        var (x0, y0) = p0;
        var (x1, y1) = p1;
        var (x2, y2) = p2;
        double v1x = x0 - x1, v1y = y0 - y1;
        double v2x = x2 - x1, v2y = y2 - y1;
        double len1 = Math.Pow(v1x * v1x + v1y * v1y, 0.5);
        double len2 = Math.Pow(v2x * v2x + v2y * v2y, 0.5);
        if (len1 == 0 || len2 == 0)
            throw new PsRangeError("invalid arcto geometry");
        double u1x = v1x / len1, u1y = v1y / len1;
        double u2x = v2x / len2, u2y = v2y / len2;
        double dot = Math.Max(-1.0, Math.Min(1.0, u1x * u2x + u1y * u2y));
        double angle = Math.Acos(dot);
        if (Math.Abs(angle) < 1e-9)
            throw new PsRangeError("invalid arcto angle");
        double offset = Py.Div(radius, Math.Tan(angle / 2.0));
        var t1 = (x1 + u1x * offset, y1 + u1y * offset);
        var t2 = (x1 + u2x * offset, y1 + u2y * offset);
        double bisX = u1x + u2x, bisY = u1y + u2y;
        double bisLen = Math.Pow(bisX * bisX + bisY * bisY, 0.5);
        if (bisLen == 0)
            throw new PsRangeError("invalid arcto geometry");
        bisX /= bisLen;
        bisY /= bisLen;
        double dist = Py.Div(radius, Math.Sin(angle / 2.0));
        var center = (x1 + bisX * dist, y1 + bisY * dist);
        double cross = u1x * u2y - u1y * u2x;
        bool clockwise = cross > 0;
        double startAngle = Py.Degrees(Math.Atan2(t1.Item2 - center.Item2, t1.Item1 - center.Item1));
        double endAngle = Py.Degrees(Math.Atan2(t2.Item2 - center.Item2, t2.Item1 - center.Item1));
        return (t1, t2, center, startAngle, endAngle, clockwise);
    }

    internal static void AppendSegment(PsExecutionContext ctx, PathSegmentKind kind, Point[] points) =>
        State(ctx).CurrentPath.Segments.Add(new PathSegment(kind, points));

    internal static void UpdateTextPosition(PsExecutionContext ctx, double x, double y)
    {
        var state = State(ctx);
        state.TextMatrix = new Matrix(1.0, 0.0, 0.0, 1.0, x, y);
        state.TextLineMatrix = state.TextMatrix;
    }

    internal static Point ApplyCtm(Matrix m, double x, double y) => new(m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);

    internal static void ConcatCtm(PsExecutionContext ctx, Matrix matrix)
    {
        var state = State(ctx);
        var oldCtm = state.Ctm;
        // _concat_ctm: the new transform applies `matrix` first, then the old CTM.
        var newCtm = MulTuple(oldCtm, matrix);
        var inverse = InvertCtm(newCtm);
        if (inverse is not null)
            RebaseUserPoints(state, oldCtm, inverse.Value);
        state.Ctm = newCtm;
    }

    internal static Matrix? InvertCtm(Matrix m)
    {
        double det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) < 1e-12)
            return null;
        double inv = 1.0 / det;
        return new Matrix(m.D * inv, -m.B * inv, -m.C * inv, m.A * inv, (m.C * m.F - m.D * m.E) * inv, (m.B * m.E - m.A * m.F) * inv);
    }

    private static Matrix RebaseTextTranslation(Matrix textMatrix, Matrix oldCtm, Matrix inverseNewCtm)
    {
        var device = ApplyCtm(oldCtm, textMatrix.E, textMatrix.F);
        var user = ApplyCtm(inverseNewCtm, device.X, device.Y);
        return textMatrix with { E = user.X, F = user.Y };
    }

    internal static double PopNumber(PsExecutionContext ctx) => CoerceNumber(ctx.OperandStack.Pop());

    internal static double CoerceNumber(object? value) =>
        PsOps.IsNumeric(value) ? PsOps.ToDouble(value) : throw new PsTypeError("number expected");

    internal static Matrix PopMatrix(PsExecutionContext ctx, PsInterpreter? interpreter)
    {
        var value = ctx.OperandStack.Pop();
        var items = value switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("matrix array expected"),
        };
        if (items.Count == 6 && items.All(PsOps.IsNumeric))
            return new Matrix(PsOps.ToDouble(items[0]), PsOps.ToDouble(items[1]), PsOps.ToDouble(items[2]), PsOps.ToDouble(items[3]), PsOps.ToDouble(items[4]), PsOps.ToDouble(items[5]));
        // Some real-world inputs use executable names/procedures in matrix arrays; resolve
        // them with interpreter semantics.
        var numbers = interpreter is not null ? ResolveArrayNumbers(ctx, interpreter, value) : EvaluateArrayNumbers(items);
        if (numbers.Count != 6)
            throw new PsRangeError("matrix must have 6 elements");
        return new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
    }

    private static List<double> EvaluateArrayNumbers(List<object?> items)
    {
        var stack = new List<double>();
        double Pop()
        {
            if (stack.Count == 0)
                throw Py.IndexError("pop from empty list");
            double v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }
        foreach (var item in items)
        {
            if (PsOps.IsNumeric(item))
            {
                stack.Add(PsOps.ToDouble(item));
                continue;
            }
            if (item is PsName name)
            {
                if (name.Literal)
                    throw new PsTypeError("number expected");
                switch (name.Value)
                {
                    case "sin":
                        stack.Add(Math.Sin(Py.Radians(Pop())));
                        continue;
                    case "cos":
                        stack.Add(Math.Cos(Py.Radians(Pop())));
                        continue;
                    case "atan":
                    {
                        double x = Pop();
                        double y = Pop();
                        stack.Add(Py.Degrees(Math.Atan2(y, x)));
                        continue;
                    }
                    case "add":
                    {
                        double b = Pop();
                        double a = Pop();
                        stack.Add(a + b);
                        continue;
                    }
                    case "sub":
                    {
                        double b = Pop();
                        double a = Pop();
                        stack.Add(a - b);
                        continue;
                    }
                    case "mul":
                    {
                        double b = Pop();
                        double a = Pop();
                        stack.Add(a * b);
                        continue;
                    }
                    case "div":
                    {
                        double b = Pop();
                        double a = Pop();
                        stack.Add(Py.Div(a, b));
                        continue;
                    }
                    case "neg":
                        stack.Add(-Pop());
                        continue;
                    case "abs":
                        stack.Add(Math.Abs(Pop()));
                        continue;
                    case "dup":
                        if (stack.Count == 0)
                            throw Py.IndexError();
                        stack.Add(stack[^1]);
                        continue;
                    case "exch":
                        if (stack.Count < 2)
                            throw Py.IndexError();
                        (stack[^1], stack[^2]) = (stack[^2], stack[^1]);
                        continue;
                }
                throw new PsTypeError($"unsupported array operator: {name.Value}");
            }
            throw new PsTypeError("number expected");
        }
        return stack;
    }

    internal static List<double> ResolveArrayNumbers(PsExecutionContext ctx, PsInterpreter interpreter, object? arrayObj)
    {
        var items = arrayObj switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("array expected"),
        };
        if (items.All(PsOps.IsNumeric))
            return items.Select(PsOps.ToDouble).ToList();
        var temp = new PsExecutionContext(new PsStack<object?>(), new PsStack<object?>(), ctx.DictionaryStack.Clone(),
            new PsStack<GraphicsState>([State(ctx).Clone()]), ctx.Userdict, ctx.Systemdict)
        {
            Dsc = ctx.Dsc,
            DefaultPageSize = ctx.DefaultPageSize,
            ImageStore = ctx.ImageStore,
            FontResolver = ctx.FontResolver,
            CharpathMode = ctx.CharpathMode,
        };
        foreach (var item in items.ToList())
            interpreter.ExecuteObject(item, temp);
        return temp.OperandStack.Items.Select(CoerceNumber).ToList();
    }

    private static List<double> RectNumbersFromOperand(PsExecutionContext ctx, PsInterpreter interpreter, string message)
    {
        var value = ctx.OperandStack.Pop();
        List<double> numbers;
        if (value is PsArray or List<object?>)
        {
            numbers = ResolveArrayNumbers(ctx, interpreter, value);
        }
        else
        {
            double height = CoerceNumber(value);
            double width = PopNumber(ctx);
            double y = PopNumber(ctx);
            double x = PopNumber(ctx);
            numbers = [x, y, width, height];
        }
        if (numbers.Count % 4 != 0)
            throw new PsRangeError(message);
        return numbers;
    }

    internal static int CurveStepsFromFlatness(double flatness)
    {
        if (flatness <= 0)
            return 36;
        long steps = Py.Int(Py.Div(12, flatness));
        return (int)Math.Max(4, Math.Min(200, steps));
    }

    internal static List<(List<Point> Points, bool Closed)> FlattenToPoints(RenderPath path, int curveSteps)
    {
        var subpaths = new List<(List<Point>, bool)>();
        var current = new List<Point>();
        Point? start = null;
        Point? currentPoint = null;
        bool closed = false;
        foreach (var segment in path.Segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    if (current.Count > 0)
                        subpaths.Add((current, closed));
                    current = [segment.Points[0]];
                    start = segment.Points[0];
                    currentPoint = segment.Points[0];
                    closed = false;
                    break;
                case PathSegmentKind.Line:
                {
                    var point = segment.Points[0];
                    if (currentPoint is null)
                    {
                        current = [point];
                        start = point;
                    }
                    else
                    {
                        current.Add(point);
                    }
                    currentPoint = point;
                    break;
                }
                case PathSegmentKind.Curve:
                {
                    if (currentPoint is null)
                        continue;
                    var p0 = currentPoint.Value;
                    var (p1, p2, p3) = (segment.Points[0], segment.Points[1], segment.Points[2]);
                    int steps = CurveSteps(p0, p1, p2, p3, curveSteps);
                    for (int step = 1; step <= steps; step++)
                    {
                        double t = (double)step / steps;
                        current.Add(new Point(Bezier(p0.X, p1.X, p2.X, p3.X, t), Bezier(p0.Y, p1.Y, p2.Y, p3.Y, t)));
                    }
                    currentPoint = p3;
                    break;
                }
                case PathSegmentKind.Close:
                    if (start is not null && current.Count > 0 && current[^1] != start.Value)
                        current.Add(start.Value);
                    if (current.Count > 0)
                    {
                        closed = true;
                        subpaths.Add((current, closed));
                    }
                    current = [];
                    start = null;
                    currentPoint = null;
                    closed = false;
                    break;
            }
        }
        if (current.Count > 0)
            subpaths.Add((current, closed));
        return subpaths;
    }

    /// <summary>The cubic Bezier polynomial evaluated exactly as the original wrote it.</summary>
    internal static double Bezier(double p0, double p1, double p2, double p3, double t) =>
        Math.Pow(1 - t, 3) * p0
        + 3 * Math.Pow(1 - t, 2) * t * p1
        + 3 * (1 - t) * Math.Pow(t, 2) * p2
        + Math.Pow(t, 3) * p3;

    internal static int CurveSteps(Point p0, Point p1, Point p2, Point p3, int baseSteps)
    {
        double length = Py.Hypot(p1.X - p0.X, p1.Y - p0.Y) + Py.Hypot(p2.X - p1.X, p2.Y - p1.Y) + Py.Hypot(p3.X - p2.X, p3.Y - p2.Y);
        long adaptive = Py.Int(length / 30);
        if (adaptive < baseSteps)
            return baseSteps;
        return (int)Math.Min(200, adaptive);
    }

    internal static List<PathSegment> PointsToPath(List<Point> points)
    {
        if (points.Count == 0)
            return [];
        var segments = new List<PathSegment>(points.Count + 1) { new(PathSegmentKind.Move, [points[0]]) };
        for (int i = 1; i < points.Count; i++)
            segments.Add(new PathSegment(PathSegmentKind.Line, [points[i]]));
        segments.Add(new PathSegment(PathSegmentKind.Close, []));
        return segments;
    }

    internal static List<List<Point>>? StrokeOutline(List<Point> points, bool closed, GraphicsState state)
    {
        if (points.Count < 2)
            return null;
        double half = Math.Max(0.0, state.LineWidth / 2.0);
        if (half == 0)
            return null;
        var pts = closed && points[0] == points[^1] ? points.GetRange(0, points.Count - 1) : points;
        if (pts.Count < 2)
            return null;
        var join = state.LineJoin switch
        {
            1 => ClipperJoinType.Round,
            2 => ClipperJoinType.Square,
            _ => ClipperJoinType.Miter,
        };
        var endType = ClipperEndType.Closed;
        if (!closed)
        {
            endType = state.LineCap switch
            {
                1 => ClipperEndType.Round,
                2 => ClipperEndType.Square,
                _ => ClipperEndType.Butt,
            };
        }
        const double scale = 10000.0;
        double width = half * scale;
        if (width == 0)
            return null;
        var path = pts.Select(p => new IntPoint(Py.Round(p.X * scale), Py.Round(p.Y * scale))).ToList();
        var outlines = new List<List<Point>>();
        double miterLimit = state.MiterLimit * scale;
        if (closed)
        {
            var outer = OffsetPaths([path], width, join, ClipperEndType.Closed, miterLimit);
            var inner = OffsetPaths([path], -width, join, ClipperEndType.Closed, miterLimit);
            foreach (var result in outer)
                outlines.Add(result.Select(pt => new Point(pt.X / scale, pt.Y / scale)).ToList());
            foreach (var result in inner)
                outlines.Add(Enumerable.Reverse(result).Select(pt => new Point(pt.X / scale, pt.Y / scale)).ToList());
        }
        else
        {
            var outer = OffsetPaths([path], width, join, endType, miterLimit);
            foreach (var result in outer)
                outlines.Add(result.Select(pt => new Point(pt.X / scale, pt.Y / scale)).ToList());
        }
        return outlines;
    }

    private static List<List<IntPoint>> OffsetPaths(List<List<IntPoint>> polys, double delta, ClipperJoinType join, ClipperEndType end, double miterLimit)
    {
        var input = polys.Select(p => p.Select(pt => new IntPoint(pt.X, pt.Y)).ToList()).ToList();
        var offset = ClipperOffset.OffsetPaths(input, delta, join, end, miterLimit);
        return offset.Where(p => p.Count > 0).Select(p => p.Select(pt => new IntPoint(pt.X, pt.Y)).ToList()).ToList();
    }

    internal static RenderPath FlattenPathSegments(RenderPath path, int curveSteps)
    {
        var segments = new List<PathSegment>();
        Point? currentPoint = null;
        Point? start = null;
        foreach (var segment in path.Segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    currentPoint = segment.Points[0];
                    start = currentPoint;
                    segments.Add(new PathSegment(PathSegmentKind.Move, [currentPoint.Value]));
                    break;
                case PathSegmentKind.Line:
                {
                    var point = segment.Points[0];
                    if (currentPoint is null)
                    {
                        start = point;
                        segments.Add(new PathSegment(PathSegmentKind.Move, [point]));
                    }
                    else
                    {
                        segments.Add(new PathSegment(PathSegmentKind.Line, [point]));
                    }
                    currentPoint = point;
                    break;
                }
                case PathSegmentKind.Curve:
                {
                    if (currentPoint is null)
                        continue;
                    var p0 = currentPoint.Value;
                    var (p1, p2, p3) = (segment.Points[0], segment.Points[1], segment.Points[2]);
                    int steps = CurveSteps(p0, p1, p2, p3, curveSteps);
                    for (int step = 1; step <= steps; step++)
                    {
                        double t = (double)step / steps;
                        segments.Add(new PathSegment(PathSegmentKind.Line, [new Point(Bezier(p0.X, p1.X, p2.X, p3.X, t), Bezier(p0.Y, p1.Y, p2.Y, p3.Y, t))]));
                    }
                    currentPoint = p3;
                    break;
                }
                case PathSegmentKind.Close:
                    segments.Add(new PathSegment(PathSegmentKind.Close, []));
                    if (start is not null)
                        currentPoint = start;
                    break;
            }
        }
        return new RenderPath(segments);
    }

    private static RenderPath ReversePath(RenderPath path)
    {
        var reversed = new List<PathSegment>();
        foreach (var (segments, closed) in CollectSubpaths(path))
        {
            if (segments.Count == 0)
                continue;
            reversed.Add(new PathSegment(PathSegmentKind.Move, [segments[^1].End]));
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                var (kind, segStart, _, points) = segments[i];
                if (kind == PathSegmentKind.Line)
                    reversed.Add(new PathSegment(PathSegmentKind.Line, [segStart]));
                else if (kind == PathSegmentKind.Curve)
                    reversed.Add(new PathSegment(PathSegmentKind.Curve, [points[1], points[0], segStart]));
            }
            if (closed)
                reversed.Add(new PathSegment(PathSegmentKind.Close, []));
        }
        return new RenderPath(reversed);
    }

    private static List<(List<(PathSegmentKind Kind, Point Start, Point End, Point[] Points)> Segments, bool Closed)> CollectSubpaths(RenderPath path)
    {
        var subpaths = new List<(List<(PathSegmentKind, Point, Point, Point[])>, bool)>();
        Point? current = null;
        Point? start = null;
        var segments = new List<(PathSegmentKind, Point, Point, Point[])>();
        bool closed = false;
        foreach (var segment in path.Segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    if (segments.Count > 0)
                        subpaths.Add((segments, closed));
                    segments = [];
                    closed = false;
                    current = segment.Points[0];
                    start = current;
                    break;
                case PathSegmentKind.Line:
                    if (current is null)
                    {
                        current = segment.Points[0];
                        start = current;
                    }
                    segments.Add((PathSegmentKind.Line, current.Value, segment.Points[0], segment.Points));
                    current = segment.Points[0];
                    break;
                case PathSegmentKind.Curve:
                    if (current is null)
                    {
                        current = segment.Points[^1];
                        start = current;
                    }
                    segments.Add((PathSegmentKind.Curve, current.Value, segment.Points[^1], segment.Points));
                    current = segment.Points[^1];
                    break;
                case PathSegmentKind.Close:
                    if (current is not null && start is not null)
                    {
                        segments.Add((PathSegmentKind.Close, current.Value, start.Value, []));
                        current = start;
                        closed = true;
                    }
                    break;
            }
        }
        if (segments.Count > 0)
            subpaths.Add((segments, closed));
        return subpaths;
    }

    internal static (double Llx, double Lly, double Urx, double Ury)? PathBounds(RenderPath path)
    {
        // Expanded a bit to align with PostScript pathbbox behavior for Bezier curves; this
        // matches reference outputs that include control-point extremes.
        const double tolerance = 1e-6;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        Point? current = null;
        Point? start = null;
        void Expand(Point p)
        {
            minX = PyMin(minX, p.X);
            minY = PyMin(minY, p.Y);
            maxX = PyMax(maxX, p.X);
            maxY = PyMax(maxY, p.Y);
        }
        foreach (var segment in path.Segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    current = segment.Points[0];
                    start = current;
                    Expand(current.Value);
                    break;
                case PathSegmentKind.Line:
                {
                    var point = segment.Points[0];
                    if (current is null)
                    {
                        current = point;
                        start = point;
                    }
                    Expand(current.Value);
                    Expand(point);
                    current = point;
                    break;
                }
                case PathSegmentKind.Curve:
                {
                    if (current is null)
                        continue;
                    var (p1, p2, p3) = (segment.Points[0], segment.Points[1], segment.Points[2]);
                    var b = BezierBounds(current.Value, p1, p2, p3);
                    minX = PyMin(PyMin(PyMin(minX, b.MinX), p1.X), p2.X);
                    minY = PyMin(PyMin(PyMin(minY, b.MinY), p1.Y), p2.Y);
                    maxX = PyMax(PyMax(PyMax(maxX, b.MaxX), p1.X), p2.X);
                    maxY = PyMax(PyMax(PyMax(maxY, b.MaxY), p1.Y), p2.Y);
                    current = p3;
                    break;
                }
                case PathSegmentKind.Close:
                    if (current is not null && start is not null)
                    {
                        Expand(current.Value);
                        Expand(start.Value);
                        current = start;
                    }
                    break;
            }
        }
        if (double.IsPositiveInfinity(minX))
            return null;
        double llx = minX - tolerance, lly = minY - tolerance, urx = maxX + tolerance, ury = maxY + tolerance;
        if (Math.Abs(llx) < tolerance)
            llx = 0.0;
        if (Math.Abs(lly) < tolerance)
            lly = 0.0;
        if (Math.Abs(urx) < tolerance)
            urx = 0.0;
        if (Math.Abs(ury) < tolerance)
            ury = 0.0;
        return (llx, lly, urx, ury);
    }

    /// <summary>Python <c>min(a, b)</c>: the first argument wins ties and NaN comparisons.</summary>
    internal static double PyMin(double a, double b) => b < a ? b : a;

    /// <summary>Python <c>max(a, b)</c>.</summary>
    internal static double PyMax(double a, double b) => b > a ? b : a;

    private static (double MinX, double MinY, double MaxX, double MaxY) BezierBounds(Point p0, Point p1, Point p2, Point p3)
    {
        var xs = new List<double> { p0.X, p3.X };
        var ys = new List<double> { p0.Y, p3.Y };
        foreach (double t in BezierExtrema(p0.X, p1.X, p2.X, p3.X))
            xs.Add(Bezier(p0.X, p1.X, p2.X, p3.X, t));
        foreach (double t in BezierExtrema(p0.Y, p1.Y, p2.Y, p3.Y))
            ys.Add(Bezier(p0.Y, p1.Y, p2.Y, p3.Y, t));
        return (xs.Aggregate(PyMin), ys.Aggregate(PyMin), xs.Aggregate(PyMax), ys.Aggregate(PyMax));
    }

    private static List<double> BezierExtrema(double p0, double p1, double p2, double p3)
    {
        double a = -p0 + 3 * p1 - 3 * p2 + p3;
        double b = 3 * p0 - 6 * p1 + 3 * p2;
        double c = -3 * p0 + 3 * p1;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) < 1e-12)
                return [];
            double t = Py.Div(-c, 2 * b);
            return t > 0 && t < 1 ? [t] : [];
        }
        double aa = 3 * a, bb = 2 * b, cc = c;
        double disc = bb * bb - 4 * aa * cc;
        if (disc < 0)
            return [];
        double root = Math.Sqrt(disc);
        double t1 = (-bb + root) / (2 * aa);
        double t2 = (-bb - root) / (2 * aa);
        var result = new List<double>();
        if (t1 > 0 && t1 < 1)
            result.Add(t1);
        if (t2 > 0 && t2 < 1)
            result.Add(t2);
        return result;
    }

    internal static void ResetCurrentPoint(GraphicsState state)
    {
        Point? current = null;
        Point? start = null;
        foreach (var segment in state.CurrentPath.Segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    current = segment.Points[0];
                    start = current;
                    break;
                case PathSegmentKind.Line:
                    current = segment.Points[0];
                    break;
                case PathSegmentKind.Curve:
                    current = segment.Points[^1];
                    break;
                case PathSegmentKind.Close:
                    if (start is not null)
                        current = start;
                    break;
            }
        }
        state.CurrentPoint = current is { } c ? (c.X, c.Y) : null;
        state.SubpathStart = start is { } s ? (s.X, s.Y) : null;
    }

    internal static RenderPath TransformPath(RenderPath path, Matrix matrix)
    {
        var segments = new List<PathSegment>(path.Segments.Count);
        foreach (var segment in path.Segments)
        {
            if (segment.Kind is PathSegmentKind.Move or PathSegmentKind.Line or PathSegmentKind.Curve)
                segments.Add(new PathSegment(segment.Kind, segment.Points.Select(p => ApplyCtm(matrix, p.X, p.Y)).ToArray()));
            else
                segments.Add(new PathSegment(segment.Kind, []));
        }
        return new RenderPath(segments);
    }
}
