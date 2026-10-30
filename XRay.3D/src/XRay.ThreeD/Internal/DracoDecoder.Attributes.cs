namespace XRay.ThreeD.Internal;

internal sealed partial class DracoDecoder
{
    private const int PredictionNone = -2;
    private const int PredictionDifference = 0;
    private const int PredictionParallelogram = 1;
    private const int PredictionConstrainedMulti = 4;
    private const int PredictionTexCoords = 5;
    private const int PredictionGeometricNormal = 6;
    private const int TransformWrap = 1;
    private const int TransformNormalOctahedron = 3;
    private const int MaxParallelograms = 4;

    private sealed class Att
    {
        public int Type, DataType, NumComponents, UniqueId, SeqType;
        public bool Normalized;
        public int Prediction = PredictionNone;
        public int Transform;
        public int WrapMin, WrapMax, MaxQ, QuantBits;
        public float[] Min = [];
        public float Range;
        public bool[] Flip = [];
        public bool[] Orientations = [];
        public bool[][] Crease = [];
        public int[] Signed = [];
        public int[] Original = [];
        public float[] Floats = [];
    }

    private sealed class Dec
    {
        public int DataId, Type, Traversal;
        public List<Att> Atts = new();
        public List<int> EntryToCorner = new();
        public int[] VertexToEntry = [];
        public int[] CornerToVertex = [];
        public List<int> LeftMost = new();
        public int[][] AttFtv = [];
        public int[] IndicesMap = [];
        public bool[] FaceVisited = [];
        public bool[] VertexVisited = [];
    }

    private Dec[] _decs = [];
    private int _posDec;
    private Dec _cur = null!;
    private int _curIndex;
    private HashSet<int>[] _seamVertices = [];
    private bool[][] _edgeOnSeam = [];

    private bool IsEb => _method == EdgeBreaker && _encoderType == 1;

    private DracoGeometry DecodeAttributes(Reader r, uint[]? sequentialIndices)
    {
        int count = r.U8();
        _decs = new Dec[count];
        for (var i = 0; i < count; i++)
        {
            _decs[i] = new Dec();
        }

        if (IsEb)
        {
            for (var i = 0; i < count; i++)
            {
                _decs[i].DataId = (sbyte)r.U8();
                _decs[i].Type = r.U8();
                _decs[i].Traversal = r.U8();
                if (_decs[i].Type > 1 || _decs[i].Traversal > 1)
                {
                    throw Bad("an unknown attribute decoder.");
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            var n = r.Var32();
            if (n > 64)
            {
                throw Bad("too many attributes.");
            }

            for (var j = 0; j < n; j++)
            {
                var a = new Att { Type = r.U8(), DataType = r.U8(), NumComponents = r.U8(), Normalized = r.U8() != 0, UniqueId = (int)r.Var32() };
                if (a.DataType is < 1 or > 11 || a.NumComponents is < 1 or > 32)
                {
                    throw Bad("an unsupported attribute layout.");
                }

                _decs[i].Atts.Add(a);
            }

            foreach (var a in _decs[i].Atts)
            {
                a.SeqType = r.U8();
                if (a.SeqType > 3)
                {
                    throw Bad("an unknown attribute coder.");
                }
            }
        }

        _posDec = Math.Max(Array.FindIndex(_decs, d => d.DataId < 0), 0);
        if (IsEb && (count == 0 || _decs[_posDec].DataId >= 0))
        {
            throw Bad("no attribute decoder works on the position connectivity.");
        }

        if (IsEb)
        {
            PrepareEdgeBreakerAttributes();
        }

        for (var i = 0; i < count; i++)
        {
            _cancel.ThrowIfCancellationRequested();
            _curIndex = i;
            _cur = _decs[i];
                GenerateSequence();
        }

        for (var i = 0; i < count; i++)
        {
            _curIndex = i;
            _cur = _decs[i];
            var entries = _cur.EntryToCorner.Count;
                foreach (var a in _cur.Atts)
            {
                ReadPortable(r, a, entries);
            }

            foreach (var a in _cur.Atts)
            {
                if (a.SeqType == 3)
                {
                    a.QuantBits = r.U8();
                    CheckBits(a.QuantBits);
                }
                else if (a.SeqType == 2)
                {
                    a.Min = new float[a.NumComponents];
                    for (var c = 0; c < a.NumComponents; c++)
                    {
                        a.Min[c] = r.F32();
                    }

                    a.Range = r.F32();
                    a.QuantBits = r.U8();
                    CheckBits(a.QuantBits);
                }
            }

            foreach (var a in _cur.Atts)
            {
                ToOriginalFormat(a, entries);
            }
        }

        return Assemble(sequentialIndices);
    }

    /// <summary>The decoder that works on the position connectivity itself (data id -1); the others work on attribute connectivity.</summary>
    private bool IsPos(int dec) => _decs[dec].DataId < 0;

    private static void CheckBits(int bits)
    {
        if (bits is < 1 or > 30)
        {
            throw Bad("an unreasonable quantization width.");
        }
    }

    private int Components(Att a) => a.SeqType == 3 ? 2 : a.NumComponents;

    // ---- corner helpers for attribute decoders

    private int Opposite(int dec, int c)
    {
        if (IsPos(dec) || _decs[dec].Type == 0)
        {
            return PosOpposite(c);
        }

        return c < 0 || _edgeOnSeam[_decs[dec].DataId][c] ? -1 : PosOpposite(c);
    }

    private int SwingRight(int dec, int c) => Prev(Opposite(dec, Prev(c)));

    private int SwingLeft(int dec, int c) => Next(Opposite(dec, Next(c)));

    private void CornerToVerts(int dec, int corner, out int v, out int n, out int p)
    {
        var ftv = IsPos(dec) || _decs[dec].Type == 0 ? _ftv : _decs[dec].AttFtv;
        var face = corner / 3;
        switch (corner % 3)
        {
            case 0:
                v = ftv[0][face];
                n = ftv[1][face];
                p = ftv[2][face];
                break;
            case 1:
                v = ftv[1][face];
                n = ftv[2][face];
                p = ftv[0][face];
                break;
            default:
                v = ftv[2][face];
                n = ftv[0][face];
                p = ftv[1][face];
                break;
        }
    }

    private int CornerToVert(int dec, int corner)
    {
        CornerToVerts(dec, corner, out var v, out _, out _);
        return v;
    }

    private bool IsVertexOnSeam(int attr, int vert) => _seamVertices[attr].Contains(vert);

    private bool IsCornerOnAttributeSeam(int dec, int attr, int corner) => IsVertexOnSeam(attr, CornerToVert(dec, corner));

    private bool IsOnPositionBoundary(int vert)
    {
        var c = _vertexCorners[vert];
        if (c < 0)
        {
            return true;
        }

        if (_cur.DataId < 0)
        {
            return SwingLeft(_curIndex, c) < 0;
        }

        return _cur.Type == 0 && IsCornerOnAttributeSeam(_curIndex, _cur.DataId, c);
    }

    private bool IsOnBoundary(int dec, int vert)
    {
        if (IsPos(dec) || _decs[dec].Type == 0)
        {
            return IsOnPositionBoundary(vert);
        }

        var corner = _decs[dec].LeftMost[vert];
        return corner < 0 || IsVertexOnSeam(_decs[dec].DataId, CornerToVert(_posDec, corner));
    }

    // ---- connectivity bookkeeping that the attributes need

    private void PrepareEdgeBreakerAttributes()
    {
        var seams = _seamRans.Length;
        foreach (var d in _decs)
        {
            if (d.DataId >= seams)
            {
                throw Bad("an attribute decoder refers to connectivity data that does not exist.");
            }
        }

        _seamVertices = new HashSet<int>[seams];
        _edgeOnSeam = new bool[seams][];
        for (var a = 0; a < seams; a++)
        {
            _seamVertices[a] = new HashSet<int>();
            _edgeOnSeam[a] = new bool[3 * _numFaces];
        }

        // Attribute seams: one bit per interior edge (visited from its lower corner) and per attribute; boundary edges are always seams.
        for (var j = 0; j < _numFaces; j++)
        {
            if ((j & 4095) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            for (var k = 0; k < 3; k++)
            {
                var corner = j * 3 + k;
                CornerToVerts(_posDec, corner, out _, out var n, out var p);
                var opp = PosOpposite(corner);
                if (opp >= 0)
                {
                    if (opp >= corner)
                    {
                        for (var a = 0; a < seams; a++)
                        {
                            if (_seamRans[a].ReadBit(_seamProb[a]))
                            {
                                _seamVertices[a].Add(n);
                                _seamVertices[a].Add(p);
                                _edgeOnSeam[a][corner] = true;
                                CornerToVerts(_posDec, opp, out _, out var on, out var op);
                                _seamVertices[a].Add(on);
                                _seamVertices[a].Add(op);
                                _edgeOnSeam[a][opp] = true;
                            }
                        }
                    }
                }
                else
                {
                    for (var a = 0; a < seams; a++)
                    {
                        _seamVertices[a].Add(n);
                        _seamVertices[a].Add(p);
                        _edgeOnSeam[a][corner] = true;
                    }
                }
            }
        }

        for (var v = 0; v < _numVerts; v++)
        {
            if (_isHole[v])
            {
                UpdateVertexToCornerMap(v);
            }
        }

        for (var i = 0; i < _decs.Length; i++)
        {
            if (_decs[i].DataId >= 0)
            {
                _curIndex = i;
                _cur = _decs[i];
                RecomputeVertices(i);
            }
        }

        AssignPointsToCorners();
        _curIndex = 0;
    }

    private void UpdateVertexToCornerMap(int vert)
    {
        var first = _vertexCorners[vert];
        if (first < 0)
        {
            return;
        }

        var act = SwingLeftPos(first);
        var c = first;
        for (var guard = 0; act >= 0 && act != first; guard++)
        {
            Guard(guard);
            c = act;
            act = SwingLeftPos(act);
        }

        if (act != first)
        {
            _vertexCorners[vert] = c;
        }
    }

    private void RecomputeVertices(int dec)
    {
        var d = _decs[dec];
        var attr = d.DataId;
        d.AttFtv = [(int[])_ftv[0].Clone(), (int[])_ftv[1].Clone(), (int[])_ftv[2].Clone()];
        d.CornerToVertex = new int[3 * _numFaces];
        Array.Fill(d.CornerToVertex, -1);
        var numNew = 0;
        for (var v = 0; v < _numVerts; v++)
        {
            if ((v & 4095) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            var c = _vertexCorners[v];
            if (c < 0)
            {
                continue;
            }

            var firstVertId = numNew++;
            var firstC = c;
            if (IsVertexOnSeam(attr, v))
            {
                var actLeft = SwingLeft(dec, firstC);
                for (var guard = 0; actLeft >= 0; guard++)
                {
                    Guard(guard);
                    firstC = actLeft;
                    actLeft = SwingLeft(dec, actLeft);
                }
            }

            d.CornerToVertex[firstC] = firstVertId;
            d.LeftMost.Add(firstC);
            var act = SwingRightPos(firstC);
            for (var guard = 0; act >= 0 && act != firstC; guard++)
            {
                Guard(guard);
                if (_edgeOnSeam[attr][Next(act)])
                {
                    firstVertId = numNew++;
                    d.LeftMost.Add(act);
                }

                d.CornerToVertex[act] = firstVertId;
                act = SwingRightPos(act);
            }
        }

        for (var i = 0; i < d.CornerToVertex.Length; i += 3)
        {
            var face = i / 3;
            d.AttFtv[0][face] = d.CornerToVertex[i];
            d.AttFtv[1][face] = d.CornerToVertex[i + 1];
            d.AttFtv[2][face] = d.CornerToVertex[i + 2];
        }
    }

    private void AssignPointsToCorners()
    {
        _cornerToPoint = new int[3 * _numFaces];
        Array.Fill(_cornerToPoint, -1);
        if (_seamRans.Length == 0)
        {
            // No attribute has its own connectivity: a point is a vertex. Vertices merged away by topology splits leave holes in the id
            // space, filled (in the order the merges happened) with the highest ids still in use.
            var alive = _numVerts - _deadVertices.Count;
            var remap = new int[_numVerts];
            for (var i = 0; i < remap.Length; i++)
            {
                remap[i] = i;
            }

            var high = _numVerts - 1;
            var dead = new HashSet<int>(_deadVertices);
            foreach (var hole in _deadVertices)
            {
                if (hole >= alive)
                {
                    continue;
                }

                while (high >= alive && dead.Contains(high))
                {
                    high--;
                }

                if (high < alive)
                {
                    break;
                }

                remap[high--] = hole;
            }

            for (var corner = 0; corner < _cornerToPoint.Length; corner++)
            {
                _cornerToPoint[corner] = remap[_ftv[corner % 3][corner / 3]];
            }

            _numPoints = alive;
            return;
        }

        var count = 0;
        for (var v = 0; v < _numVerts; v++)
        {
            if ((v & 4095) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            var c = _vertexCorners[v];
            if (c < 0)
            {
                continue;
            }

            var first = c;
            if (!_isHole[v])
            {
                for (var i = 0; i < _decs.Length; i++)
                {
                    if (_decs[i].DataId < 0 || !IsCornerOnAttributeSeam(_posDec, _decs[i].DataId, c))
                    {
                        continue;
                    }

                    var vertId = _decs[i].CornerToVertex[c];
                    var act = SwingRightPos(c);
                    var found = false;
                    for (var guard = 0; act >= 0 && act != c; guard++)
                    {
                        Guard(guard);
                        if (_decs[i].CornerToVertex[act] != vertId)
                        {
                            first = act;
                            found = true;
                            break;
                        }

                        act = SwingRightPos(act);
                    }

                    if (found)
                    {
                        break;
                    }
                }
            }

            c = first;
            _cornerToPoint[c] = count++;
            var prevC = c;
            c = SwingRightPos(c);
            for (var guard = 0; c >= 0 && c != first; guard++)
            {
                Guard(guard);
                var seam = false;
                for (var i = 0; i < _decs.Length; i++)
                {
                    if (_decs[i].DataId >= 0 && _decs[i].CornerToVertex[c] != _decs[i].CornerToVertex[prevC])
                    {
                        seam = true;
                        break;
                    }
                }

                _cornerToPoint[c] = seam ? count++ : _cornerToPoint[prevC];
                prevC = c;
                c = SwingRightPos(c);
            }
        }

        _numPoints = count;
    }

    // ---- attribute value order

    private void GenerateSequence()
    {
        var d = _cur;
        if (!IsEb)
        {
            for (var i = 0; i < _numPoints; i++)
            {
                d.EntryToCorner.Add(i);
            }

            return;
        }

        d.FaceVisited = new bool[_numFaces];
        d.VertexVisited = new bool[3 * _numFaces];
        d.VertexToEntry = new int[3 * _numFaces];
        var degree = d.Traversal == 1 ? new int[3 * _numFaces] : [];
        for (var i = 0; i < _numFaces; i++)
        {
            if ((i & 4095) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            if (d.Traversal == 0)
            {
                TraverseDepthFirst(3 * i);
            }
            else
            {
                TraversePredictionDegree(3 * i, degree);
            }
        }

        d.IndicesMap = new int[3 * _numFaces];
        Array.Fill(d.IndicesMap, -1);
        for (var corner = 0; corner < 3 * _numFaces; corner++)
        {
            var point = _cornerToPoint[corner];
            var vert = CornerToVert(_curIndex, corner);
            d.IndicesMap[point] = d.VertexToEntry[vert];
        }
    }

    private void NewVertexVisited(int vertex, int corner)
    {
        _cur.VertexToEntry[vertex] = _cur.EntryToCorner.Count;
        _cur.EntryToCorner.Add(corner);
    }

    private bool FaceVisited(int face) => face < 0 || _cur.FaceVisited[face];

    private void TraverseDepthFirst(int start)
    {
        var d = _cur;
        var dec = _curIndex;
        if (FaceVisited(start / 3))
        {
            return;
        }

        var stack = new List<int> { start };
        CornerToVerts(dec, start, out _, out var nextVert, out var prevVert);
        if (!d.VertexVisited[nextVert])
        {
            d.VertexVisited[nextVert] = true;
            NewVertexVisited(nextVert, Next(start));
        }

        if (!d.VertexVisited[prevVert])
        {
            d.VertexVisited[prevVert] = true;
            NewVertexVisited(prevVert, Prev(start));
        }

        var steps = 0;
        while (stack.Count > 0)
        {
            var corner = stack[^1];
            if (corner < 0 || FaceVisited(corner / 3))
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            while (true)
            {
                Guard(steps++);
                d.FaceVisited[corner / 3] = true;
                var vert = CornerToVert(dec, corner);
                if (!d.VertexVisited[vert])
                {
                    bool onBoundary;
                    if (IsPos(dec))
                    {
                        onBoundary = IsOnPositionBoundary(vert);
                    }
                    else
                    {
                        var onSeam = IsOnBoundary(dec, vert);
                        onBoundary = onSeam || IsOnPositionBoundary(CornerToVert(_posDec, corner));
                    }

                    d.VertexVisited[vert] = true;
                    NewVertexVisited(vert, corner);
                    if (!onBoundary)
                    {
                        corner = RightCorner(dec, corner);
                        continue;
                    }
                }

                var rightCorner = RightCorner(dec, corner);
                var leftCorner = LeftCorner(dec, corner);
                var rightFace = rightCorner < 0 ? -1 : rightCorner / 3;
                var leftFace = leftCorner < 0 ? -1 : leftCorner / 3;
                if (FaceVisited(rightFace))
                {
                    if (FaceVisited(leftFace))
                    {
                        stack.RemoveAt(stack.Count - 1);
                        break;
                    }

                    corner = leftCorner;
                }
                else if (FaceVisited(leftFace))
                {
                    corner = rightCorner;
                }
                else
                {
                    stack[^1] = leftCorner;
                    stack.Add(rightCorner);
                    break;
                }
            }
        }
    }

    /// <summary>The corner to the right: through the position connectivity for decoder 0, and not across attribute seams for the others.</summary>
    private int RightCorner(int dec, int corner)
    {
        if (corner < 0)
        {
            return -1;
        }

        if (IsPos(dec))
        {
            return PosOpposite(Next(corner));
        }

        return _edgeOnSeam[_decs[dec].DataId][Next(corner)] ? -1 : PosOpposite(Next(corner));
    }

    private int LeftCorner(int dec, int corner)
    {
        if (corner < 0)
        {
            return -1;
        }

        if (IsPos(dec))
        {
            return PosOpposite(Prev(corner));
        }

        return _edgeOnSeam[_decs[dec].DataId][Prev(corner)] ? -1 : PosOpposite(Prev(corner));
    }

    private void TraversePredictionDegree(int start, int[] degree)
    {
        var d = _cur;
        var dec = _curIndex;
        if (FaceVisited(start / 3))
        {
            return;
        }

        var stacks = new List<int>[3] { new(), new(), new() };
        var best = 0;
        stacks[0].Add(start);
        CornerToVerts(dec, start, out var tip, out var nextVert, out var prevVert);
        if (!d.VertexVisited[nextVert])
        {
            d.VertexVisited[nextVert] = true;
            NewVertexVisited(nextVert, Next(start));
        }

        if (!d.VertexVisited[prevVert])
        {
            d.VertexVisited[prevVert] = true;
            NewVertexVisited(prevVert, Prev(start));
        }

        if (!d.VertexVisited[tip])
        {
            d.VertexVisited[tip] = true;
            NewVertexVisited(tip, start);
        }

        int Priority(int corner)
        {
            CornerToVerts(dec, corner, out var vTip, out _, out _);
            var priority = 0;
            if (!d.VertexVisited[vTip])
            {
                var deg = ++degree[vTip];
                priority = deg > 1 ? 1 : 2;
            }

            return Math.Min(priority, 2);
        }

        int Pop()
        {
            for (var i = best; i < 3; i++)
            {
                if (stacks[i].Count > 0)
                {
                    var v = stacks[i][^1];
                    stacks[i].RemoveAt(stacks[i].Count - 1);
                    best = i;
                    return v;
                }
            }

            return -1;
        }

        var steps = 0;
        int corner;
        while ((corner = Pop()) >= 0)
        {
            if (FaceVisited(corner / 3))
            {
                continue;
            }

            while (true)
            {
                Guard(steps++);
                d.FaceVisited[corner / 3] = true;
                var vert = CornerToVert(dec, corner);
                if (!d.VertexVisited[vert])
                {
                    d.VertexVisited[vert] = true;
                    NewVertexVisited(vert, corner);
                }

                var rightCorner = RightCorner(dec, corner);
                var leftCorner = LeftCorner(dec, corner);
                var rightVisited = FaceVisited(rightCorner < 0 ? -1 : rightCorner / 3);
                var leftVisited = FaceVisited(leftCorner < 0 ? -1 : leftCorner / 3);
                if (!leftVisited)
                {
                    var priority = Priority(leftCorner);
                    if (rightVisited && priority <= best)
                    {
                        corner = leftCorner;
                        continue;
                    }

                    stacks[priority].Add(leftCorner);
                    best = Math.Min(best, priority);
                }

                if (!rightVisited)
                {
                    var priority = Priority(rightCorner);
                    if (priority <= best)
                    {
                        corner = rightCorner;
                        continue;
                    }

                    stacks[priority].Add(rightCorner);
                    best = Math.Min(best, priority);
                }

                break;
            }
        }
    }

    // ---- portable attribute values

    private void ReadPortable(Reader r, Att a, int entries)
    {
        var comps = Components(a);
        long numValues = (long)entries * comps;
        if (numValues > (long)_maxFaces * 9 + 4_000_000)
        {
            throw Bad("an unreasonable number of attribute values.");
        }

        if (a.SeqType == 0)
        {
            ReadGeneric(r, a, entries);
            return;
        }

        a.Prediction = (sbyte)r.U8();
        if (a.Prediction != PredictionNone)
        {
            a.Transform = (sbyte)r.U8();
            if (a.Transform is not (TransformWrap or TransformNormalOctahedron))
            {
                throw Bad("an unknown prediction transform.");
            }
        }

        var compressed = r.U8();
        uint[] symbols;
        if (compressed > 0)
        {
            symbols = DecodeSymbols(r, numValues, comps);
        }
        else
        {
            var bytes = r.U8();
            if (bytes is < 1 or > 4)
            {
                throw Bad("an unsupported raw integer width.");
            }

            symbols = new uint[numValues];
            for (var i = 0; i < symbols.Length; i++)
            {
                uint v = 0;
                for (var b = 0; b < bytes; b++)
                {
                    v |= (uint)r.U8() << (8 * b);
                }

                symbols[i] = v;
            }
        }

        a.Signed = new int[numValues];
        for (var i = 0; i < symbols.Length; i++)
        {
            var val = symbols[i];
            if (a.Prediction != PredictionNone && a.Transform == TransformNormalOctahedron)
            {
                a.Signed[i] = unchecked((int)val);
            }
            else
            {
                var positive = (val & 1) == 0;
                var half = (int)(val >> 1);
                a.Signed[i] = positive ? half : -half - 1;
            }
        }

        if (a.Prediction == PredictionNone)
        {
            a.Original = a.Signed;
            return;
        }

        DecodePredictionData(r, a, entries);
        ComputeOriginalValues(a, entries);
    }

    private void ReadGeneric(Reader r, Att a, int entries)
    {
        var size = a.DataType switch { 1 or 2 or 11 => 1, 3 or 4 => 2, 5 or 6 or 9 => 4, _ => 8 };
        var values = new float[(long)entries * a.NumComponents];
        for (var i = 0; i < values.Length; i++)
        {
            r.Need(size);
            var span = r.Data.AsSpan(r.Pos, size);
            r.Pos += size;
            values[i] = a.DataType switch
            {
                1 => (sbyte)span[0],
                2 or 11 => span[0],
                3 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(span),
                4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span),
                5 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span),
                6 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span),
                7 => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(span),
                8 => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span),
                9 => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(span),
                _ => (float)System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(span),
            };
        }

        a.Floats = values;
    }

    private void DecodePredictionData(Reader r, Att a, int entries)
    {
        switch (a.Prediction)
        {
            case PredictionConstrainedMulti:
                a.Crease = new bool[MaxParallelograms][];
                for (var i = 0; i < MaxParallelograms; i++)
                {
                    var flags = r.Var32();
                    if (flags > (uint)entries * 4 + 16)
                    {
                        throw Bad("too many prediction flags.");
                    }

                    a.Crease[i] = new bool[flags];
                    if (flags > 0)
                    {
                        var ans = ReadRansBits(r, out var p);
                        for (var j = 0; j < flags; j++)
                        {
                            a.Crease[i][j] = ans.ReadBit(p);
                        }
                    }
                }

                DecodeTransformData(r, a);
                break;
            case PredictionTexCoords:
                {
                    var count = r.U32();
                    if (count > (uint)entries + 16)
                    {
                        throw Bad("too many texture coordinate orientations.");
                    }

                    var ans = ReadRansBits(r, out var p);
                    a.Orientations = new bool[count];
                    var last = true;
                    for (var i = 0; i < count; i++)
                    {
                        if (!ans.ReadBit(p))
                        {
                            last = !last;
                        }

                        a.Orientations[i] = last;
                    }

                    DecodeTransformData(r, a);
                    break;
                }

            case PredictionGeometricNormal:
                {
                    DecodeTransformData(r, a);
                    var ans = ReadRansBits(r, out var p);
                    a.Flip = new bool[entries];
                    for (var i = 0; i < entries; i++)
                    {
                        a.Flip[i] = ans.ReadBit(p);
                    }

                    break;
                }

            default:
                DecodeTransformData(r, a);
                break;
        }
    }

    private static void DecodeTransformData(Reader r, Att a)
    {
        if (a.Transform == TransformWrap)
        {
            a.WrapMin = r.I32();
            a.WrapMax = r.I32();
        }
        else
        {
            a.MaxQ = r.I32();
            r.I32();
            if (a.MaxQ <= 1 || a.MaxQ > (1 << 30))
            {
                throw Bad("an unreasonable normal quantization range.");
            }
        }
    }

    // ---- prediction schemes

    private void ComputeOriginalValues(Att a, int entries)
    {
        var comps = Components(a);
        var signed = a.Signed;
        var output = (int[])signed.Clone();
        a.Original = output;
        switch (a.Prediction)
        {
            case PredictionDifference:
                {
                    var zero = new int[comps];
                    TransformValue(a, zero, 0, signed, 0, output, 0);
                    for (var i = comps; i < output.Length; i += comps)
                    {
                        TransformValue(a, output, i - comps, signed, i, output, i);
                    }

                    break;
                }

            case PredictionParallelogram:
                ParallelogramValues(a, entries, signed, output, comps);
                break;
            case PredictionConstrainedMulti:
                ConstrainedMultiValues(a, entries, signed, output, comps);
                break;
            case PredictionTexCoords:
                TexCoordValues(a, entries, output);
                break;
            case PredictionGeometricNormal:
                GeometricNormalValues(a, entries, output);
                break;
            default:
                throw Bad("an unknown prediction scheme.");
        }
    }

    private void TransformValue(Att a, int[] pred, int predOffset, int[] corr, int corrOffset, int[] output, int outOffset)
    {
        if (a.Transform == TransformWrap)
        {
            var comps = Components(a);
            var min = a.WrapMin;
            var max = a.WrapMax;
            var maxDif = 1 + max - min;
            for (var i = 0; i < comps; i++)
            {
                var p = Math.Clamp(pred[predOffset + i], Math.Min(min, max), max);
                if (min > max)
                {
                    p = pred[predOffset + i] > max ? max : pred[predOffset + i] < min ? min : pred[predOffset + i];
                }

                var value = unchecked(p + corr[corrOffset + i]);
                if (value > max)
                {
                    value = unchecked(value - maxDif);
                }
                else if (value < min)
                {
                    value = unchecked(value + maxDif);
                }

                output[outOffset + i] = value;
            }
        }
        else
        {
            OctahedronTransform(a, pred[predOffset], pred[predOffset + 1], corr[corrOffset], corr[corrOffset + 1], out var s, out var t);
            output[outOffset] = s;
            output[outOffset + 1] = t;
        }
    }

    private bool ParallelogramPrediction(int dec, int entry, int corner, int[] data, int comps, int[] prediction)
    {
        var oci = Opposite(dec, corner);
        if (oci < 0)
        {
            return false;
        }

        CornerToVerts(dec, oci, out var v, out var n, out var p);
        var d = _decs[dec];
        int opp = d.VertexToEntry[v], next = d.VertexToEntry[n], prev = d.VertexToEntry[p];
        if (opp < entry && next < entry && prev < entry)
        {
            for (var c = 0; c < comps; c++)
            {
                prediction[c] = unchecked(data[next * comps + c] + data[prev * comps + c] - data[opp * comps + c]);
            }

            return true;
        }

        return false;
    }

    private void ParallelogramValues(Att a, int entries, int[] signed, int[] output, int comps)
    {
        var pred = new int[comps];
        TransformValue(a, pred, 0, signed, 0, output, 0);
        for (var p = 1; p < entries; p++)
        {
            if ((p & 4095) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            var corner = _cur.EntryToCorner[p];
            if (!ParallelogramPrediction(_curIndex, p, corner, output, comps, pred))
            {
                TransformValue(a, output, (p - 1) * comps, signed, p * comps, output, p * comps);
            }
            else
            {
                TransformValue(a, pred, 0, signed, p * comps, output, p * comps);
            }
        }
    }

    private void ConstrainedMultiValues(Att a, int entries, int[] signed, int[] output, int comps)
    {
        var preds = new int[MaxParallelograms][];
        for (var i = 0; i < MaxParallelograms; i++)
        {
            preds[i] = new int[comps];
        }

        TransformValue(a, preds[0], 0, signed, 0, output, 0);
        var creasePos = new int[MaxParallelograms];
        var multi = new int[comps];
        for (var p = 1; p < entries; p++)
        {
            if ((p & 4095) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            var start = _cur.EntryToCorner[p];
            var corner = start;
            var count = 0;
            var firstPass = true;
            for (var guard = 0; corner >= 0; guard++)
            {
                Guard(guard);
                if (ParallelogramPrediction(_curIndex, p, corner, output, comps, preds[count]))
                {
                    if (++count == MaxParallelograms)
                    {
                        break;
                    }
                }

                corner = firstPass ? SwingLeft(_curIndex, corner) : SwingRight(_curIndex, corner);
                if (corner == start)
                {
                    break;
                }

                if (corner < 0 && firstPass)
                {
                    firstPass = false;
                    corner = SwingRight(_curIndex, start);
                }
            }

            var used = 0;
            if (count > 0)
            {
                Array.Clear(multi);
                for (var i = 0; i < count; i++)
                {
                    var context = count - 1;
                    if (creasePos[context] >= a.Crease[context].Length)
                    {
                        throw Bad("the crease flags ran out.");
                    }

                    if (!a.Crease[context][creasePos[context]++])
                    {
                        used++;
                        for (var j = 0; j < comps; j++)
                        {
                            multi[j] = unchecked(multi[j] + preds[i][j]);
                        }
                    }
                }
            }

            if (used == 0)
            {
                TransformValue(a, output, (p - 1) * comps, signed, p * comps, output, p * comps);
            }
            else
            {
                for (var c = 0; c < comps; c++)
                {
                    multi[c] /= used;
                }

                TransformValue(a, multi, 0, signed, p * comps, output, p * comps);
            }
        }
    }

    // ---- texture coordinate prediction

    private static ulong IntSqrt(ulong number)
    {
        if (number == 0)
        {
            return 0;
        }

        var act = number;
        ulong root = 1;
        while (act >= 2)
        {
            root *= 2;
            act /= 4;
        }

        // Newton's iteration; for a hostile number whose square would overflow, the iteration count and the root's size are bounded.
        root = Math.Min(root, uint.MaxValue);
        for (var i = 0; i < 128; i++)
        {
            root = (root + number / root) / 2;
            if (root > uint.MaxValue)
            {
                root = uint.MaxValue;
            }

            if (root * root <= number)
            {
                break;
            }
        }

        return root;
    }

    private void PositionForEntry(int entry, Span<long> pos)
    {
        var corner = _cur.EntryToCorner[entry];
        var point = _cornerToPoint[corner];
        var mapped = _decs[_posDec].IndicesMap[point];
        var original = _decs[_posDec].Atts.Find(x => x.Type == 0)!.Original;
        for (var i = 0; i < 3; i++)
        {
            pos[i] = original[mapped * 3 + i];
        }
    }

    private void TexCoordValues(Att a, int entries, int[] output)
    {
        var orientations = a.Orientations.Length;
        Span<long> tip = stackalloc long[3];
        Span<long> next = stackalloc long[3];
        Span<long> prev = stackalloc long[3];
        var predicted = new int[2];
        for (var p = 0; p < entries; p++)
        {
            if ((p & 4095) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            var corner = _cur.EntryToCorner[p];
            CornerToVerts(_curIndex, corner, out _, out var nv, out var pv);
            var nextId = _cur.VertexToEntry[nv];
            var prevId = _cur.VertexToEntry[pv];
            var done = false;
            if (prevId < p && nextId < p)
            {
                long nu = output[nextId * 2], nvv = output[nextId * 2 + 1], pu = output[prevId * 2], pvv = output[prevId * 2 + 1];
                if (pu == nu && pvv == nvv)
                {
                    predicted[0] = (int)pu;
                    predicted[1] = (int)pvv;
                    done = true;
                }
                else
                {
                    PositionForEntry(p, tip);
                    PositionForEntry(nextId, next);
                    PositionForEntry(prevId, prev);
                    long pn0 = prev[0] - next[0], pn1 = prev[1] - next[1], pn2 = prev[2] - next[2];
                    var pnNorm2 = unchecked((ulong)(pn0 * pn0 + pn1 * pn1 + pn2 * pn2));
                    if (pnNorm2 != 0)
                    {
                        long cn0 = tip[0] - next[0], cn1 = tip[1] - next[1], cn2 = tip[2] - next[2];
                        var cnDotPn = unchecked(cn0 * pn0 + cn1 * pn1 + cn2 * pn2);
                        var absMax = Math.Max(Math.Max(Math.Abs(pn0), Math.Abs(pn1)), Math.Abs(pn2));
                        if (absMax == 0 || cnDotPn <= long.MaxValue / absMax)
                        {
                            long pnuv0 = pu - nu, pnuv1 = pvv - nvv;
                            unchecked
                            {
                                var xuv0 = nu * (long)pnNorm2 + cnDotPn * pnuv0;
                                var xuv1 = nvv * (long)pnNorm2 + cnDotPn * pnuv1;
                                var xpos0 = next[0] + pn0 * cnDotPn / (long)pnNorm2;
                                var xpos1 = next[1] + pn1 * cnDotPn / (long)pnNorm2;
                                var xpos2 = next[2] + pn2 * cnDotPn / (long)pnNorm2;
                                long d0 = tip[0] - xpos0, d1 = tip[1] - xpos1, d2 = tip[2] - xpos2;
                                var cxNorm2 = (ulong)(d0 * d0 + d1 * d1 + d2 * d2);
                                var scale = (long)IntSqrt(cxNorm2 * pnNorm2);
                                var cx0 = pnuv1 * scale;
                                var cx1 = -pnuv0 * scale;
                                if (orientations == 0)
                                {
                                    throw Bad("the texture coordinate orientations ran out.");
                                }

                                var orientation = a.Orientations[--orientations];
                                long t0 = orientation ? xuv0 + cx0 : xuv0 - cx0;
                                long t1 = orientation ? xuv1 + cx1 : xuv1 - cx1;
                                predicted[0] = (int)(t0 / (long)pnNorm2);
                                predicted[1] = (int)(t1 / (long)pnNorm2);
                            }

                            done = true;
                        }
                    }
                }
            }

            if (!done)
            {
                var offset = 0;
                if (prevId < p)
                {
                    offset = prevId * 2;
                }

                if (nextId < p)
                {
                    offset = nextId * 2;
                }
                else if (p > 0)
                {
                    offset = (p - 1) * 2;
                }
                else
                {
                    predicted[0] = predicted[1] = 0;
                    offset = -1;
                }

                if (offset >= 0)
                {
                    predicted[0] = output[offset];
                    predicted[1] = output[offset + 1];
                }
            }

            TransformValue(a, predicted, 0, a.Signed, p * 2, output, p * 2);
        }
    }

    // ---- normal prediction and the octahedron transform

    private static int MostSignificantBit(int n)
    {
        var msb = -1;
        var u = (uint)n;
        while (u != 0)
        {
            msb++;
            u >>= 1;
        }

        return msb;
    }

    private void PositionForCorner(int corner, Span<long> pos)
    {
        var vert = CornerToVert(_curIndex, corner);
        PositionForEntry(_cur.VertexToEntry[vert], pos);
    }

    private void GeometricNormalValues(Att a, int entries, int[] output)
    {
        var bits = MostSignificantBit(a.MaxQ) + 1;
        var maxQuantized = (1 << bits) - 1;
        var maxValue = maxQuantized - 1;
        var center = maxValue / 2;
        Span<long> cent = stackalloc long[3];
        Span<long> pn = stackalloc long[3];
        Span<long> pp = stackalloc long[3];
        Span<long> normal = stackalloc long[3];
        var predOct = new int[2];
        for (var entry = 0; entry < entries; entry++)
        {
            if ((entry & 1023) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            var corner = _cur.EntryToCorner[entry];
            PositionForCorner(corner, cent);
            normal.Clear();
            var start = corner;
            var left = true;
            for (var guard = 0; corner >= 0; guard++)
            {
                Guard(guard);
                PositionForCorner(Next(corner), pn);
                PositionForCorner(Prev(corner), pp);
                long dn0 = pn[0] - cent[0], dn1 = pn[1] - cent[1], dn2 = pn[2] - cent[2];
                long dp0 = pp[0] - cent[0], dp1 = pp[1] - cent[1], dp2 = pp[2] - cent[2];
                unchecked
                {
                    normal[0] += dn1 * dp2 - dn2 * dp1;
                    normal[1] += dn2 * dp0 - dn0 * dp2;
                    normal[2] += dn0 * dp1 - dn1 * dp0;
                }

                if (left)
                {
                    corner = SwingLeft(_curIndex, corner);
                    if (corner < 0)
                    {
                        corner = SwingRight(_curIndex, start);
                        left = false;
                    }
                    else if (corner == start)
                    {
                        corner = -1;
                    }
                }
                else
                {
                    corner = SwingRight(_curIndex, corner);
                }
            }

            var absSum = Math.Abs(normal[0]) + Math.Abs(normal[1]) + Math.Abs(normal[2]);
            const long upper = 1L << 29;
            if (absSum > upper)
            {
                var quotient = absSum / upper;
                normal[0] /= quotient;
                normal[1] /= quotient;
                normal[2] /= quotient;
            }

            // Canonicalise to the octahedron's diamond.
            long x = normal[0], y = normal[1], z = normal[2];
            var sum = Math.Abs(x) + Math.Abs(y) + Math.Abs(z);
            if (sum == 0)
            {
                x = center;
            }
            else
            {
                x = x * center / sum;
                y = y * center / sum;
                z = z >= 0 ? center - Math.Abs(x) - Math.Abs(y) : -(center - Math.Abs(x) - Math.Abs(y));
            }

            if (a.Flip[entry])
            {
                x = -x;
                y = -y;
                z = -z;
            }

            int s, t;
            if (x >= 0)
            {
                s = (int)(y + center);
                t = (int)(z + center);
            }
            else
            {
                s = y < 0 ? (int)Math.Abs(z) : (int)(maxValue - Math.Abs(z));
                t = z < 0 ? (int)Math.Abs(y) : (int)(maxValue - Math.Abs(y));
            }

            if ((s == 0 && t == 0) || (s == 0 && t == maxValue) || (s == maxValue && t == 0))
            {
                s = maxValue;
                t = maxValue;
            }
            else if (s == 0 && t > center)
            {
                t = center - (t - center);
            }
            else if (s == maxValue && t < center)
            {
                t = center + (center - t);
            }
            else if (t == maxValue && s < center)
            {
                s = center + (center - s);
            }
            else if (t == 0 && s > center)
            {
                s = center - (s - center);
            }

            predOct[0] = s;
            predOct[1] = t;
            OctahedronTransform(a, s, t, a.Signed[entry * 2], a.Signed[entry * 2 + 1], out output[entry * 2], out output[entry * 2 + 1]);
        }
    }

    private static int ModMax(int x, int center, int maxQuantized)
    {
        if (x > center)
        {
            return x - maxQuantized;
        }

        if (x < -center)
        {
            return x + maxQuantized;
        }

        return x;
    }

    private static void InvertDiamond(ref int s, ref int t, int center)
    {
        int signS, signT;
        if (s >= 0 && t >= 0)
        {
            signS = 1;
            signT = 1;
        }
        else if (s <= 0 && t <= 0)
        {
            signS = -1;
            signT = -1;
        }
        else
        {
            signS = s > 0 ? 1 : -1;
            signT = t > 0 ? 1 : -1;
        }

        var cornerS = signS * center;
        var cornerT = signT * center;
        s = unchecked(2 * s - cornerS);
        t = unchecked(2 * t - cornerT);
        if (signS * signT >= 0)
        {
            (s, t) = (-t, -s);
        }
        else
        {
            (s, t) = (t, s);
        }

        s = (s + cornerS) / 2;
        t = (t + cornerT) / 2;
    }

    private static void Rotate(int count, ref int x, ref int y)
    {
        var (px, py) = (x, y);
        switch (count)
        {
            case 1:
                (x, y) = (py, -px);
                break;
            case 2:
                (x, y) = (-px, -py);
                break;
            case 3:
                (x, y) = (-py, px);
                break;
        }
    }

    private void OctahedronTransform(Att a, int predS, int predT, int corrS, int corrT, out int outS, out int outT)
    {
        var bits = MostSignificantBit(a.MaxQ != 0 ? a.MaxQ : (1 << a.QuantBits) - 1) + 1;
        var maxQuantized = (1 << bits) - 1;
        var maxValue = maxQuantized - 1;
        var center = maxValue / 2;
        int ps = predS - center, pt = predT - center;
        var inDiamond = Math.Abs(ps) + Math.Abs(pt) <= center;
        if (!inDiamond)
        {
            InvertDiamond(ref ps, ref pt, center);
        }

        var bottomLeft = (ps == 0 && pt == 0) || (ps < 0 && pt <= 0);
        int rotation;
        if (ps == 0)
        {
            rotation = pt == 0 ? 0 : pt > 0 ? 3 : 1;
        }
        else if (ps > 0)
        {
            rotation = pt >= 0 ? 2 : 1;
        }
        else
        {
            rotation = pt <= 0 ? 0 : 3;
        }

        if (!bottomLeft)
        {
            Rotate(rotation, ref ps, ref pt);
        }

        var os = ModMax(unchecked(ps + corrS), center, maxQuantized);
        var ot = ModMax(unchecked(pt + corrT), center, maxQuantized);
        if (!bottomLeft)
        {
            Rotate((4 - rotation) % 4, ref os, ref ot);
        }

        if (!inDiamond)
        {
            InvertDiamond(ref os, ref ot, center);
        }

        outS = os + center;
        outT = ot + center;
    }

    // ---- back to the original format

    private void ToOriginalFormat(Att a, int entries)
    {
        if (a.SeqType == 0)
        {
            return;
        }

        var comps = Components(a);
        if (a.SeqType == 3)
        {
            var bits = a.MaxQ != 0 ? MostSignificantBit(a.MaxQ) + 1 : a.QuantBits;
            var maxQuantized = (1 << bits) - 1;
            var maxValue = maxQuantized - 1;
            var scale = 1.0f / maxValue;
            var normals = new float[entries * 3];
            for (var i = 0; i < entries; i++)
            {
                OctahedronToUnit(a.Original[i * 2] * scale, a.Original[i * 2 + 1] * scale, normals.AsSpan(i * 3, 3));
            }

            a.Floats = normals;
        }
        else if (a.SeqType == 2)
        {
            var max = (1 << a.QuantBits) - 1;
            var factor = 1.0f / max;
            var values = new float[entries * comps];
            var k = 0;
            for (var i = 0; i < entries; i++)
            {
                for (var c = 0; c < comps; c++)
                {
                    var val = a.Original[k];
                    var neg = val < 0;
                    if (neg)
                    {
                        val = -val;
                    }

                    var norm = val * factor;
                    if (neg)
                    {
                        norm = -norm;
                    }

                    values[k++] = norm * a.Range + a.Min[c];
                }
            }

            a.Floats = values;
        }
        else
        {
            var values = new float[a.Original.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = a.Original[i];
            }

            a.Floats = values;
        }
    }

    private static void OctahedronToUnit(float inS, float inT, Span<float> result)
    {
        float s = inS, t = inT;
        var spt = s + t;
        var smt = s - t;
        var xSign = 1.0f;
        if (spt >= 0.5f && spt <= 1.5f && smt >= -0.5f && smt <= 0.5f)
        {
        }
        else
        {
            xSign = -1.0f;
            if (spt <= 0.5f)
            {
                s = 0.5f - inT;
                t = 0.5f - inS;
            }
            else if (spt >= 1.5f)
            {
                s = 1.5f - inT;
                t = 1.5f - inS;
            }
            else if (smt <= -0.5f)
            {
                s = inT - 0.5f;
                t = inS + 0.5f;
            }
            else
            {
                s = inT + 0.5f;
                t = inS - 0.5f;
            }

            spt = s + t;
            smt = s - t;
        }

        var y = 2.0f * s - 1.0f;
        var z = 2.0f * t - 1.0f;
        var x = Math.Min(Math.Min(2.0f * spt - 1.0f, 3.0f - 2.0f * spt), Math.Min(2.0f * smt + 1.0f, 1.0f - 2.0f * smt)) * xSign;
        var normSquared = x * x + y * y + z * z;
        if (normSquared < 1e-6f)
        {
            result[0] = result[1] = result[2] = 0;
        }
        else
        {
            var d = 1.0f / MathF.Sqrt(normSquared);
            result[0] = x * d;
            result[1] = y * d;
            result[2] = z * d;
        }
    }

    // ---- the result

    private DracoGeometry Assemble(uint[]? sequentialIndices)
    {
        var geometry = new DracoGeometry { NumPoints = _numPoints };
        if (IsEb)
        {
            geometry.Indices = new uint[_cornerToPoint.Length];
            for (var i = 0; i < _cornerToPoint.Length; i++)
            {
                geometry.Indices[i] = (uint)_cornerToPoint[i];
            }
        }
        else if (sequentialIndices is not null)
        {
            geometry.Indices = sequentialIndices;
        }

        foreach (var d in _decs)
        {
            foreach (var a in d.Atts)
            {
                var comps = a.SeqType == 3 ? 3 : a.NumComponents;
                var values = new float[(long)_numPoints * comps];
                var entries = d.EntryToCorner.Count;
                if (a.Floats.Length < (long)entries * comps)
                {
                    throw Bad("an attribute has fewer values than points.");
                }

                for (var p = 0; p < _numPoints; p++)
                {
                    var e = IsEb ? d.IndicesMap[p] : p;
                    if (e < 0 || e >= entries)
                    {
                        throw Bad("a point refers to an attribute value that does not exist.");
                    }

                    Array.Copy(a.Floats, (long)e * comps, values, (long)p * comps, comps);
                }

                geometry.Attributes.Add(new DracoAttribute
                {
                    UniqueId = a.UniqueId,
                    Type = a.Type,
                    DataType = a.DataType,
                    NumComponents = comps,
                    Normalized = a.Normalized,
                    Values = values,
                });
            }
        }

        return geometry;
    }
}
