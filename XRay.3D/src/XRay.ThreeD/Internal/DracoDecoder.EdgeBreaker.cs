namespace XRay.ThreeD.Internal;

internal sealed partial class DracoDecoder
{
    private int _numPoints;
    private int _numFaces;
    private int _numVerts;
    private int _faceCount;
    private int _traversalType;
    private int[][] _ftv = [[], [], []];
    private int[] _opp = [];
    private int[] _vertexCorners = [];
    private bool[] _isHole = [];
    private int[] _vertexParent = [];
    private int[] _cornerToPoint = [];
    private readonly List<int> _deadVertices = new();

    // ---- corner table

    private static int Next(int c) => c < 0 ? c : (c % 3 == 2 ? c - 2 : c + 1);

    private static int Prev(int c) => c < 0 ? c : (c % 3 == 0 ? c + 2 : c - 1);

    private int PosOpposite(int c) => c < 0 || c >= _opp.Length ? -1 : _opp[c];

    private void SetOpposite(int c, int o)
    {
        _opp[c] = o;
        _opp[o] = c;
    }

    private int Find(int v)
    {
        var root = v;
        while (_vertexParent[root] != root)
        {
            root = _vertexParent[root];
        }

        while (_vertexParent[v] != root)
        {
            (v, _vertexParent[v]) = (_vertexParent[v], root);
        }

        return root;
    }

    private int ConnVert(int corner) => Find(_ftv[corner % 3][corner / 3]);

    private void PushFace(int a, int b, int c)
    {
        if (_faceCount >= _numFaces)
        {
            throw Bad("more faces than declared.");
        }

        _ftv[0][_faceCount] = a;
        _ftv[1][_faceCount] = b;
        _ftv[2][_faceCount] = c;
        _faceCount++;
    }

    /// <summary>Corner-to-vertex mapping is implicit in the face arrays; this is kept as a no-op marker for the spec's MapCornerToVertex.</summary>
    private static void MapCorner(int corner, int vert)
    {
    }

    /// <summary>Records the corner that represents a vertex when its fan is walked; which assignments count is part of the format (it fixes the point numbering).</summary>
    private void SetLeft(int vert, int corner)
    {
        if (vert >= 0)
        {
            _vertexCorners[vert] = corner;
        }
    }

    private int SwingLeftPos(int c) => Next(PosOpposite(Next(c)));

    private int SwingRightPos(int c) => Prev(PosOpposite(Prev(c)));

    private DracoGeometry DecodeEdgeBreakerMesh(Reader r)
    {
        _traversalType = r.U8();
        if (_traversalType is not (0 or 2))
        {
            throw new ModelFormatException("This Draco EdgeBreaker traversal is not supported.");
        }

        var numEncodedVertices = (long)r.Var32();
        var numFaces = (long)r.Var32();
        int numAttributeData = r.U8();
        var numEncodedSymbols = (long)r.Var32();
        var numSplitSymbols = (long)r.Var32();
        if (numFaces > _maxFaces || numEncodedSymbols > numFaces || numSplitSymbols > numEncodedSymbols || numEncodedVertices + numSplitSymbols > 3 * numFaces + 3)
        {
            throw Bad("inconsistent EdgeBreaker counts.");
        }

        _numFaces = (int)numFaces;
        _numVerts = (int)(numEncodedVertices + numSplitSymbols);
        _ftv = [new int[_numFaces], new int[_numFaces], new int[_numFaces]];
        _opp = new int[3 * _numFaces];
        Array.Fill(_opp, -1);
        _vertexCorners = new int[_numVerts];
        Array.Fill(_vertexCorners, -1);
        _isHole = new bool[_numVerts];
        Array.Fill(_isHole, true);
        _vertexParent = new int[_numVerts];
        for (var i = 0; i < _numVerts; i++)
        {
            _vertexParent[i] = i;
        }

        // Topology split events.
        var splitCount = (int)r.Var32();
        if (splitCount > numEncodedSymbols)
        {
            throw Bad("too many topology splits.");
        }

        var sourceIds = new List<int>(splitCount);
        var splitIds = new List<int>(splitCount);
        long lastId = 0;
        var sourceDeltas = new uint[splitCount];
        var splitDeltas = new uint[splitCount];
        for (var i = 0; i < splitCount; i++)
        {
            sourceDeltas[i] = r.Var32();
            splitDeltas[i] = r.Var32();
        }

        var edgeBits = new bool[splitCount];
        var splitBits = new BitReader(r.Data, r.Pos, r.End);
        for (var i = 0; i < splitCount; i++)
        {
            edgeBits[i] = splitBits.Read(1) != 0;
        }

        r.Skip((splitBits.BitsRead + 7) / 8);
        for (var i = 0; i < splitCount; i++)
        {
            var source = lastId + sourceDeltas[i];
            var split = source - splitDeltas[i];
            if (source > numEncodedSymbols || split < 0)
            {
                throw Bad("a topology split refers to a symbol that does not exist.");
            }

            sourceIds.Add((int)source);
            splitIds.Add((int)split);
            lastId = source;
        }

        // Traversal data.
        BitReader? symbolBits = null;
        if (_traversalType == 0)
        {
            var size = r.Var64();
            if (size > (ulong)(r.End - r.Pos))
            {
                throw Bad("the symbol buffer runs past the end of the data.");
            }

            symbolBits = new BitReader(r.Data, r.Pos, r.Pos + (int)size);
            r.Skip((long)size);
        }

        var faceRans = ReadRansBits(r, out var faceProb);
        _seamRans = new Rans[numAttributeData];
        _seamProb = new int[numAttributeData];
        for (var i = 0; i < numAttributeData; i++)
        {
            _seamRans[i] = ReadRansBits(r, out _seamProb[i]);
        }

        var contextSymbols = new uint[6][];
        var contextCounters = new int[6];
        if (_traversalType == 2)
        {
            for (var i = 0; i < 6; i++)
            {
                var count = r.Var32();
                if (count > numEncodedSymbols)
                {
                    throw Bad("too many valence symbols.");
                }

                contextCounters[i] = (int)count;
                contextSymbols[i] = count > 0 ? DecodeSymbols(r, count, 1) : [];
            }
        }

        var valences = _traversalType == 2 ? new int[_numVerts] : [];
        var stack = new List<int>();
        var splitActive = new Dictionary<int, List<int>>();
        var lastVertAdded = -1;
        var lastSymbol = -1;
        var activeContext = -1;
        ReadOnlySpan<int> symbolToTopology = [TopologyC, TopologyS, TopologyL, TopologyR, TopologyE];
        for (var symbolId = 0; symbolId < numEncodedSymbols; symbolId++)
        {
            if ((symbolId & 4095) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            if (_traversalType == 2)
            {
                if (activeContext != -1)
                {
                    if (contextCounters[activeContext] <= 0)
                    {
                        throw Bad("the valence symbols ran out.");
                    }

                    var id = contextSymbols[activeContext][--contextCounters[activeContext]];
                    if (id > 4)
                    {
                        throw Bad("an unknown EdgeBreaker symbol.");
                    }

                    lastSymbol = symbolToTopology[(int)id];
                }
                else
                {
                    lastSymbol = TopologyE;
                }
            }
            else
            {
                var symbol = (int)symbolBits!.Read(1);
                if (symbol != TopologyC)
                {
                    symbol |= (int)symbolBits.Read(2) << 1;
                }

                lastSymbol = symbol;
            }

            var newCorner = 3 * symbolId;
            var checkSplit = false;
            int vert, next, prev, cornerA, cornerB;
            switch (lastSymbol)
            {
                case TopologyC:
                    {
                        cornerA = Last(stack);
                        cornerB = Prev(cornerA);
                        for (var guard = 0; PosOpposite(cornerB) >= 0; guard++)
                        {
                            Guard(guard);
                            cornerB = Prev(PosOpposite(cornerB));
                        }

                        SetOpposite(cornerA, newCorner + 1);
                        SetOpposite(cornerB, newCorner + 2);
                        stack[^1] = newCorner;
                        vert = ConnVert(Next(cornerA));
                        next = ConnVert(Next(cornerB));
                        prev = ConnVert(Prev(cornerA));
                        if (_traversalType == 2)
                        {
                            valences[next]++;
                            valences[prev]++;
                        }

                        PushFace(vert, next, prev);
                        _isHole[vert] = false;
                        SetLeft(prev, newCorner + 2);
                        break;
                    }

                case TopologyS:
                    {
                        cornerB = Pop(stack);
                        if (splitActive.TryGetValue(symbolId, out var corners))
                        {
                            stack.AddRange(corners);
                        }

                        cornerA = Last(stack);
                        SetOpposite(cornerA, newCorner + 2);
                        SetOpposite(cornerB, newCorner + 1);
                        stack[^1] = newCorner;
                        vert = ConnVert(Prev(cornerA));
                        next = ConnVert(Next(cornerA));
                        prev = ConnVert(Prev(cornerB));
                        SetLeft(prev, newCorner + 2);
                        var vertexN = ConnVert(Next(cornerB));
                        if (_traversalType == 2)
                        {
                            valences[vert] += valences[vertexN];
                        }

                        if (vertexN != vert)
                        {
                            _vertexParent[vertexN] = vert;
                        }

                        if (_traversalType == 2)
                        {
                            valences[next]++;
                            valences[prev]++;
                        }

                        PushFace(vert, next, prev);
                        SetLeft(vert, _vertexCorners[vertexN]);
                        _vertexCorners[vertexN] = -1;
                        if (vertexN != vert)
                        {
                            _deadVertices.Add(vertexN);
                        }

                        break;
                    }

                case TopologyR:
                    {
                        cornerA = Last(stack);
                        SetOpposite(newCorner + 2, cornerA);
                        stack[^1] = newCorner;
                        checkSplit = true;
                        vert = ConnVert(Prev(cornerA));
                        next = ConnVert(Next(cornerA));
                        prev = ++lastVertAdded;
                        if (_traversalType == 2)
                        {
                            valences[vert]++;
                            valences[next]++;
                            valences[prev] += 2;
                        }

                        PushFace(vert, next, prev);
                        SetLeft(prev, newCorner + 2);
                        SetLeft(vert, newCorner);
                        break;
                    }

                case TopologyL:
                    {
                        cornerA = Last(stack);
                        SetOpposite(newCorner + 1, cornerA);
                        stack[^1] = newCorner;
                        checkSplit = true;
                        vert = ConnVert(Next(cornerA));
                        next = ++lastVertAdded;
                        prev = ConnVert(Prev(cornerA));
                        if (_traversalType == 2)
                        {
                            valences[vert]++;
                            valences[next] += 2;
                            valences[prev]++;
                        }

                        PushFace(vert, next, prev);
                        SetLeft(next, newCorner + 1);
                        SetLeft(prev, newCorner + 2);
                        break;
                    }

                case TopologyE:
                    {
                        stack.Add(newCorner);
                        checkSplit = true;
                        vert = lastVertAdded + 1;
                        next = vert + 1;
                        prev = next + 1;
                        if (_traversalType == 2)
                        {
                            valences[vert] += 2;
                            valences[next] += 2;
                            valences[prev] += 2;
                        }

                        PushFace(vert, next, prev);
                        lastVertAdded = prev;
                        SetLeft(vert, newCorner);
                        SetLeft(next, newCorner + 1);
                        SetLeft(prev, newCorner + 2);
                        break;
                    }

                default:
                    throw Bad("an unknown EdgeBreaker symbol.");
            }

            if (_traversalType == 2)
            {
                var clamped = Math.Clamp(valences[next], 2, 7);
                activeContext = clamped - 2;
            }

            if (checkSplit)
            {
                var encoderSymbolId = (int)numEncodedSymbols - symbolId - 1;
                while (sourceIds.Count > 0 && sourceIds[^1] == encoderSymbolId)
                {
                    var edge = edgeBits[sourceIds.Count - 1];
                    var split = splitIds[^1];
                    sourceIds.RemoveAt(sourceIds.Count - 1);
                    splitIds.RemoveAt(splitIds.Count - 1);
                    var top = Last(stack);
                    var corner = edge ? Next(top) : Prev(top);
                    var decoderSplitId = (int)numEncodedSymbols - split - 1;
                    if (!splitActive.TryGetValue(decoderSplitId, out var list))
                    {
                        splitActive[decoderSplitId] = list = new List<int>();
                    }

                    list.Add(corner);
                }
            }
        }

        while (stack.Count > 0)
        {
            var cornerA = Pop(stack);
            if (!faceRans.ReadBit(faceProb))
            {
                continue;
            }

            var cornerB = Prev(cornerA);
            for (var guard = 0; PosOpposite(cornerB) >= 0; guard++)
            {
                Guard(guard);
                cornerB = Prev(PosOpposite(cornerB));
            }

            var cornerC = Next(cornerA);
            for (var guard = 0; PosOpposite(cornerC) >= 0; guard++)
            {
                Guard(guard);
                cornerC = Next(PosOpposite(cornerC));
            }

            var newCorner = _faceCount * 3;
            if (_faceCount >= _numFaces)
            {
                throw Bad("more faces than declared.");
            }

            SetOpposite(newCorner, cornerA);
            SetOpposite(newCorner + 1, cornerB);
            SetOpposite(newCorner + 2, cornerC);
            var nextA = ConnVert(Next(cornerA));
            var nextB = ConnVert(Next(cornerB));
            var nextC = ConnVert(Next(cornerC));
            PushFace(nextB, nextC, nextA);
            _isHole[nextB] = false;
            _isHole[nextC] = false;
            _isHole[nextA] = false;
        }

        if (_faceCount != _numFaces)
        {
            throw Bad("fewer faces than declared.");
        }

        for (var k = 0; k < 3; k++)
        {
            for (var f = 0; f < _numFaces; f++)
            {
                _ftv[k][f] = Find(_ftv[k][f]);
            }
        }

        return DecodeAttributes(r, null);
    }

    private static int Last(List<int> stack)
    {
        if (stack.Count == 0)
        {
            throw Bad("the corner stack is empty.");
        }

        return stack[^1];
    }

    private static int Pop(List<int> stack)
    {
        var v = Last(stack);
        stack.RemoveAt(stack.Count - 1);
        return v;
    }

    /// <summary>Fans around a vertex are at most as long as the corner table; more means the topology loops.</summary>
    private void Guard(int steps)
    {
        if (steps > 3 * _numFaces + 8)
        {
            throw Bad("the connectivity loops.");
        }

        if ((steps & 8191) == 0)
        {
            _cancel.ThrowIfCancellationRequested();
        }
    }

    private Rans[] _seamRans = [];
    private int[] _seamProb = [];
}
