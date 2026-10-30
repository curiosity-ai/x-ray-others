namespace XRay.ThreeD.Formats.Jt;

using XRay.ThreeD.Internal;

/// <summary>
/// The topology of a topologically compressed JT mesh: the stream of vertex valences, face degrees and attribute masks describes a dual mesh (each
/// "vertex" of the stream is a polygon of the model, each "face" a model vertex) that is rebuilt by walking it the way the writer produced it. The result is,
/// for every polygon, the index of each of its corners into the unique vertices and into the unique normals.
/// </summary>
internal sealed class JtTopology
{
    public int[] VertexIndices = [];
    public int[] NormalIndices = [];

    /// <summary>The first corner and the corner count of every polygon.</summary>
    public List<(int Start, int Count)> Polygons = new();
}

internal sealed class JtMeshInput
{
    private readonly JtPackage[] _degrees = new JtPackage[8];
    private JtPackage _valences = null!;
    private JtPackage _groups = null!;
    private JtPackage _faceFlags = null!;
    private readonly JtPackage[] _attrMasks = new JtPackage[8];
    private JtPackage _mask830 = null!;
    private JtPackage _mask84 = null!;
    private JtPackage _attrMasksLarge = null!;
    private JtPackage _splitFaces = null!;
    private JtPackage _splitPositions = null!;

    public static JtMeshInput Load(JtCursor c, CancellationToken token)
    {
        var m = new JtMeshInput();
        for (var i = 0; i < 8; i++)
        {
            m._degrees[i] = JtPackage.Load2(c, token);
        }

        m._valences = JtPackage.Load2(c, token);
        m._groups = JtPackage.Load2(c, token);
        m._faceFlags = JtPackage.Load2(c, token);
        for (var i = 0; i < 8; i++)
        {
            m._attrMasks[i] = JtPackage.Load2(c, token);
        }

        m._mask830 = JtPackage.Load2(c, token);
        m._mask84 = c.Major >= 10 ? JtPackage.Empty() : JtPackage.Load2(c, token);
        m._attrMasksLarge = JtPackage.Load2(c, token);
        m._splitFaces = JtPackage.Load2(c, token);
        m._splitPositions = JtPackage.Load2(c, token);
        c.U32();
        return m;
    }

    public JtTopology Decode(bool bigEndian, CancellationToken token)
    {
        var s = new Symbols();
        for (var i = 0; i < 8; i++)
        {
            s.Degrees[i] = _degrees[i].Decode(bigEndian, UnpackKind.Null, token);
            s.AttrMasks[i] = _attrMasks[i].Decode(bigEndian, UnpackKind.Null, token);
        }

        s.Valences = _valences.Decode(bigEndian, UnpackKind.Null, token);
        s.Groups = _groups.Decode(bigEndian, UnpackKind.Null, token);
        s.FaceFlags = _faceFlags.Decode(bigEndian, UnpackKind.Lag1, token);
        s.Mask830 = _mask830.Decode(bigEndian, UnpackKind.Null, token);
        s.Mask84 = _mask84.Decode(bigEndian, UnpackKind.Null, token);
        s.AttrMasksLarge = _attrMasksLarge.Decode(bigEndian, UnpackKind.Null, token).Select(v => (uint)v).ToArray();
        s.SplitFaces = _splitFaces.Decode(bigEndian, UnpackKind.Lag1, token);
        s.SplitPositions = _splitPositions.Decode(bigEndian, UnpackKind.Null, token);
        var decoder = new MeshDecoder(s, token);
        decoder.Run();
        if (!s.IsExhausted)
        {
            // Symbols are left over: the reference reader drops the mesh, and so does this one.
            return new JtTopology();
        }

        return decoder.Mesh.ToTopology();
    }

    private sealed class Symbols
    {
        public readonly int[][] Degrees = new int[8][];
        public readonly int[][] AttrMasks = new int[8][];
        public int[] Valences = [];
        public int[] Groups = [];
        public int[] FaceFlags = [];
        public int[] Mask830 = [];
        public int[] Mask84 = [];
        public uint[] AttrMasksLarge = [];
        public int[] SplitFaces = [];
        public int[] SplitPositions = [];
        private readonly int[] _degreePos = new int[8];
        private readonly int[] _attrMaskPos = new int[8];
        private int _valencePos, _groupPos, _faceFlagPos, _attrMaskLargePos, _splitFacePos, _splitPositionPos;

        public int NextDegree(int ctx)
        {
            var pos = _degreePos[ctx];
            if (pos >= Degrees[ctx].Length)
            {
                return -1;
            }

            _degreePos[ctx] = pos + 1;
            return Degrees[ctx][pos];
        }

        public int NextValence()
        {
            if (_valencePos >= Valences.Length)
            {
                return -1;
            }

            return Valences[_valencePos++];
        }

        public int NextGroup()
        {
            if (_groupPos >= Groups.Length)
            {
                return -1;
            }

            return Groups[_groupPos++];
        }

        public int NextFaceFlag()
        {
            if (_faceFlagPos >= FaceFlags.Length)
            {
                return 0;
            }

            return FaceFlags[_faceFlagPos++];
        }

        public long NextAttrMask(int ctx)
        {
            var pos = _attrMaskPos[ctx];
            long value = pos < AttrMasks[ctx].Length ? AttrMasks[ctx][pos] : 0;
            if (ctx == 7)
            {
                long high = 0;
                if (pos < Mask84.Length)
                {
                    high += (long)Mask84[pos] << 30;
                }

                if (pos < Mask830.Length)
                {
                    high += (long)Mask830[pos] << 30;
                }

                value |= high;
            }

            _attrMaskPos[ctx]++;
            return value;
        }

        public uint[] NextAttrMaskBits(int degree)
        {
            var words = new uint[MaskWordCount(degree)];
            if (_attrMaskLargePos >= AttrMasksLarge.Length)
            {
                return words;
            }

            var needed = (degree + 31) / 32;
            var available = AttrMasksLarge.Length - _attrMaskLargePos;
            var count = Math.Min(needed, available);
            Array.Copy(AttrMasksLarge, _attrMaskLargePos, words, 0, Math.Min(count, words.Length));
            _attrMaskLargePos += needed;
            return words;
        }

        public int NextSplitFace()
        {
            var v = _splitFacePos < SplitFaces.Length ? SplitFaces[_splitFacePos] : -1;
            _splitFacePos++;
            return v;
        }

        public int NextSplitPosition()
        {
            var v = _splitPositionPos < SplitPositions.Length ? SplitPositions[_splitPositionPos] : -1;
            _splitPositionPos++;
            return v;
        }

        public bool IsExhausted
        {
            get
            {
                for (var i = 0; i < 8; i++)
                {
                    if (_degreePos[i] != Degrees[i].Length || _attrMaskPos[i] != AttrMasks[i].Length)
                    {
                        return false;
                    }
                }

                return _valencePos == Valences.Length && _groupPos == Groups.Length && _faceFlagPos == FaceFlags.Length
                    && _attrMaskLargePos == AttrMasksLarge.Length && _splitFacePos == SplitFaces.Length && _splitPositionPos == SplitPositions.Length;
            }
        }
    }

    private const int ActiveScanWidth = 16;
    private const int InlineMaskBits = 64;
    private const int MaxRing = 1 << 20;
    private const int MaxSlots = 1 << 26;

    private static int MaskWordCount(int bits) => Math.Max(0, bits) / 32 + 1;

    private static bool BitTest(List<uint> words, int bit)
    {
        if (bit < 0)
        {
            return false;
        }

        var w = bit >> 5;
        return w < words.Count && (words[w] & (1u << (bit & 31))) != 0;
    }

    private static bool BitTest(uint[] words, int bit)
    {
        if (bit < 0)
        {
            return false;
        }

        var w = bit >> 5;
        return w < words.Length && (words[w] & (1u << (bit & 31))) != 0;
    }

    private static void BitSet(List<uint> words, int bit)
    {
        if (bit < 0)
        {
            return;
        }

        var w = bit >> 5;
        while (words.Count <= w)
        {
            words.Add(0);
        }

        words[w] |= 1u << (bit & 31);
    }

    private static int IncModN(int v, int len) => v == len - 1 ? 0 : v + 1;

    private static int DecModN(int v, int len) => v == 0 ? len - 1 : v - 1;

    private struct VertexEntry
    {
        public int Valence;
        public int Group;
        public int FaceBase;
    }

    private sealed class FaceEntry
    {
        public int Degree;
        public int EmptyDegree;
        public int AttrCount;
        public ulong AttrMask;
        public uint[] AttrMaskBits = [];
        public int VertexBase;
        public int AttrBase;
    }

    private sealed class DualMesh
    {
        public readonly List<VertexEntry> Vertices = new();
        public readonly List<FaceEntry> Faces = new();
        public readonly List<int> VertexFaceSlots = new();
        public readonly List<int> FaceVertexSlots = new();
        public readonly List<int> FaceAttrSlots = new();

        private static int At(List<int> list, long index) => index >= 0 && index < list.Count ? list[(int)index] : -1;

        public int Valence(int v) => (uint)v < (uint)Vertices.Count ? Vertices[v].Valence : 0;

        public int Degree(int f) => (uint)f < (uint)Faces.Count ? Faces[f].Degree : 0;

        public bool IsValidFace(int f) => (uint)f < (uint)Faces.Count && Faces[f].Degree != 0;

        public int Face(int v, int slot) => (uint)v < (uint)Vertices.Count ? At(VertexFaceSlots, (long)Vertices[v].FaceBase + slot) : -1;

        public int Vertex(int f, int slot) => (uint)f < (uint)Faces.Count ? At(FaceVertexSlots, (long)Faces[f].VertexBase + slot) : -1;

        public int EmptyFaceSlots(int f) => (uint)f < (uint)Faces.Count ? Faces[f].EmptyDegree : 0;

        public void NewVertex(int valence)
        {
            var faceBase = -1;
            if (VertexFaceSlots.Count + (long)valence > MaxSlots)
            {
                throw new ModelFormatException("A JT mesh is too large.");
            }

            if (valence != 0)
            {
                faceBase = VertexFaceSlots.Count;
                for (var i = 0; i < valence; i++)
                {
                    VertexFaceSlots.Add(-1);
                }
            }

            Vertices.Add(new VertexEntry { Valence = valence, Group = -1, FaceBase = faceBase });
        }

        public void SetVertexGroup(int v, int group)
        {
            if ((uint)v < (uint)Vertices.Count)
            {
                var e = Vertices[v];
                e.Group = group;
                Vertices[v] = e;
            }
        }

        public int VertexGroup(int v) => (uint)v < (uint)Vertices.Count ? Vertices[v].Group : -1;

        public void NewFace(int degree, int attrCount, ulong mask, uint[] maskBits)
        {
            if (FaceVertexSlots.Count + (long)Math.Max(0, degree) > MaxSlots || FaceAttrSlots.Count + (long)attrCount > MaxSlots)
            {
                throw new ModelFormatException("A JT mesh is too large.");
            }

            var vertexBase = FaceVertexSlots.Count;
            var attrBase = -1;
            if (attrCount > 0)
            {
                attrBase = FaceAttrSlots.Count;
                for (var i = 0; i < attrCount; i++)
                {
                    FaceAttrSlots.Add(-1);
                }
            }

            for (var i = 0; i < Math.Max(0, degree); i++)
            {
                FaceVertexSlots.Add(-1);
            }

            Faces.Add(new FaceEntry
            {
                Degree = (ushort)degree,
                EmptyDegree = (ushort)degree,
                AttrCount = (ushort)attrCount,
                AttrMask = mask,
                AttrMaskBits = maskBits,
                VertexBase = vertexBase,
                AttrBase = attrBase,
            });
        }

        public void SetFaceAttr(int face, int slot, int attr)
        {
            var b = (uint)face < (uint)Faces.Count ? Faces[face].AttrBase : -1;
            var index = (long)b + slot;
            if (index < 0 || index >= FaceAttrSlots.Count)
            {
                throw new ModelFormatException("A JT face attribute slot is out of range.");
            }

            FaceAttrSlots[(int)index] = attr;
        }

        public void SetVertexFace(int vertex, int slot, int face)
        {
            var b = (uint)vertex < (uint)Vertices.Count ? Vertices[vertex].FaceBase : -1;
            var index = (long)b + slot;
            if (index < 0 || index >= VertexFaceSlots.Count)
            {
                throw new ModelFormatException("A JT face slot of a vertex is out of range.");
            }

            VertexFaceSlots[(int)index] = face;
        }

        public void SetFaceVertex(int face, int slot, int vertex)
        {
            if ((uint)face >= (uint)Faces.Count)
            {
                throw new ModelFormatException("A JT face does not exist.");
            }

            var index = (long)Faces[face].VertexBase + slot;
            if (index < 0 || index >= FaceVertexSlots.Count)
            {
                throw new ModelFormatException("A JT vertex slot of a face is out of range.");
            }

            if (FaceVertexSlots[(int)index] != vertex)
            {
                Faces[face].EmptyDegree--;
                FaceVertexSlots[(int)index] = vertex;
            }
        }

        public int FindVertexSlot(int face, int target)
        {
            if ((uint)face >= (uint)Faces.Count)
            {
                return -1;
            }

            var degree = Faces[face].Degree;
            for (var slot = 0; slot < degree; slot++)
            {
                if (Vertex(face, slot) == target)
                {
                    return slot;
                }
            }

            return -1;
        }

        public int FindFaceSlot(int vertex, int target)
        {
            if ((uint)vertex >= (uint)Vertices.Count)
            {
                return -1;
            }

            var valence = Vertices[vertex].Valence;
            for (var slot = 0; slot < valence; slot++)
            {
                if (Face(vertex, slot) == target)
                {
                    return slot;
                }
            }

            return -1;
        }

        public int VertexFaceAttr(int vertex, int face)
        {
            if ((uint)face >= (uint)Faces.Count)
            {
                return -1;
            }

            var e = Faces[face];
            if (e.AttrCount <= 0)
            {
                return -1;
            }

            var attrSlot = e.AttrCount - 1;
            for (var vertexSlot = 0; vertexSlot < e.Degree; vertexSlot++)
            {
                var set = e.Degree <= InlineMaskBits ? ((e.AttrMask >> (vertexSlot & 63)) & 1) != 0 : BitTest(e.AttrMaskBits, vertexSlot);
                if (set)
                {
                    attrSlot++;
                }

                if (attrSlot >= e.AttrCount)
                {
                    attrSlot -= e.AttrCount;
                }

                if (Vertex(face, vertexSlot) == vertex)
                {
                    return At(FaceAttrSlots, (long)e.AttrBase + attrSlot);
                }
            }

            return -1;
        }

        public JtTopology ToTopology()
        {
            var vertexCount = Vertices.Count;
            var starts = new int[vertexCount];
            Array.Fill(starts, -1);
            long total = 0;
            for (var v = 0; v < vertexCount; v++)
            {
                if (VertexGroup(v) >= 0)
                {
                    starts[v] = (int)total;
                    total += Valence(v);
                    if (total > 1 << 28)
                    {
                        throw new ModelFormatException("A JT mesh is too large.");
                    }
                }
            }

            var t = new JtTopology { VertexIndices = new int[total], NormalIndices = new int[total] };
            for (var v = 0; v < vertexCount; v++)
            {
                var start = starts[v];
                if (start < 0)
                {
                    continue;
                }

                var valence = Valence(v);
                t.Polygons.Add((start, valence));
                for (var slot = 0; slot < valence; slot++)
                {
                    var index = start + slot;
                    var face = Face(v, slot);
                    t.VertexIndices[index] = face;
                    t.NormalIndices[index] = VertexFaceAttr(v, face);
                }
            }

            return t;
        }
    }

    private sealed class MeshDecoder
    {
        private readonly Symbols _s;
        private readonly CancellationToken _token;
        private readonly List<int> _active = new();
        private readonly List<uint> _removed = new();
        private int _attrCounter;
        private long _tick;

        public DualMesh Mesh { get; } = new();

        public MeshDecoder(Symbols s, CancellationToken token)
        {
            _s = s;
            _token = token;
        }

        public void Run()
        {
            while (RunComponent())
            {
            }
        }

        private bool RunComponent()
        {
            var vertex = ReadVertex();
            if (vertex is null)
            {
                return false;
            }

            var valence = Mesh.Valence(vertex.Value);
            for (var slot = 0; slot < valence; slot++)
            {
                ActivateFace(vertex.Value, slot);
            }

            while (NextActiveFace() is { } face)
            {
                CompleteVertex(face);
                BitSet(_removed, face);
            }

            return true;
        }

        private int? ReadVertex()
        {
            Cancel.Every(_token, _tick++);
            var valence = _s.NextValence();
            if (valence <= -1)
            {
                return null;
            }

            if (valence > MaxRing)
            {
                throw new ModelFormatException("A JT vertex valence is out of range.");
            }

            var vertex = Mesh.Vertices.Count;
            var group = _s.NextGroup();
            _s.NextFaceFlag();
            Mesh.NewVertex(valence);
            Mesh.SetVertexGroup(vertex, group);
            return vertex;
        }

        private int FaceContext(int vertex)
        {
            var valence = Mesh.Valence(vertex);
            var knownFaces = 0;
            var knownDegree = 0;
            for (var slot = 0; slot < valence; slot++)
            {
                var face = Mesh.Face(vertex, slot);
                if (!Mesh.IsValidFace(face))
                {
                    continue;
                }

                knownFaces++;
                knownDegree += Mesh.Degree(face);
            }

            switch (valence)
            {
                case 3:
                {
                    var target = knownFaces * 6;
                    return knownDegree < target ? 0 : knownDegree == target ? 1 : 2;
                }

                case 4:
                {
                    var target = knownFaces * 4;
                    return knownDegree < target ? 3 : knownDegree == target ? 4 : 5;
                }

                case 5:
                    return 6;
                default:
                    return 7;
            }
        }

        private int ReadFace(int vertex)
        {
            var ctx = FaceContext(vertex);
            var symbol = _s.NextDegree(ctx);
            if (symbol == 0)
            {
                return -1;
            }

            if (symbol < 0)
            {
                return -2;
            }

            var degree = symbol;
            if (degree > MaxRing)
            {
                throw new ModelFormatException("A JT face degree is out of range.");
            }

            var face = Mesh.Faces.Count;
            var attrContext = Math.Clamp(degree - 2, 0, 7);
            if (degree <= InlineMaskBits)
            {
                var mask = (ulong)_s.NextAttrMask(attrContext);
                var attrCount = System.Numerics.BitOperations.PopCount(mask);
                Mesh.NewFace(degree, attrCount, mask, []);
                if (attrCount > degree)
                {
                    return -1;
                }
            }
            else
            {
                var bits = _s.NextAttrMaskBits(degree);
                var attrCount = 0;
                for (var slot = 0; slot < degree; slot++)
                {
                    if (BitTest(bits, slot))
                    {
                        attrCount++;
                    }
                }

                Mesh.NewFace(degree, attrCount, 0, bits);
                if (attrCount > degree)
                {
                    return -1;
                }
            }

            for (var slot = 0; slot < Mesh.Faces[face].AttrCount; slot++)
            {
                Mesh.SetFaceAttr(face, slot, _attrCounter++);
            }

            return face;
        }

        private int ReadSplitFace()
        {
            var symbol = _s.NextSplitFace();
            if (symbol <= -1)
            {
                return -1;
            }

            if (symbol == 0 || symbol > _active.Count)
            {
                return -1;
            }

            return _active[_active.Count - symbol];
        }

        private int ReadSplitPosition()
        {
            var symbol = _s.NextSplitPosition();
            return symbol <= -1 ? -1 : symbol;
        }

        private int ActivateFace(int vertex, int slot)
        {
            var face = ReadFace(vertex);
            if (face >= 0)
            {
                Mesh.SetVertexFace(vertex, slot, face);
                Mesh.SetFaceVertex(face, 0, vertex);
                _active.Add(face);
            }
            else if (face == -1)
            {
                var split = ReadSplitFace();
                var position = ReadSplitPosition();
                if (split < 0 || position < 0)
                {
                    throw new ModelFormatException("A JT topological split points nowhere.");
                }

                Mesh.SetVertexFace(vertex, slot, split);
                AddVertexToFace(vertex, slot, split, position);
            }

            return face;
        }

        private int ActivateVertex(int face, int slot)
        {
            var vertex = ReadVertex() ?? throw new ModelFormatException("A JT vertex symbol stream ended inside a face.");
            Mesh.SetVertexFace(vertex, 0, face);
            AddVertexToFace(vertex, 0, face, slot);
            return vertex;
        }

        private void AddVertexToFace(int vertex, int faceSlot, int face, int vertexSlot)
        {
            var degree = Mesh.Degree(face);
            var slotCcw = IncModN(vertexSlot, degree);
            var slotCw = DecModN(vertexSlot, degree);
            Mesh.SetFaceVertex(face, vertexSlot, vertex);

            var previous = Mesh.Vertex(face, slotCw);
            if (previous != -1)
            {
                var baseSlot = Mesh.FindFaceSlot(previous, face);
                var target = IncModN(faceSlot, Mesh.Valence(vertex));
                if (Mesh.Face(vertex, target) == -1)
                {
                    var source = DecModN(baseSlot, Mesh.Valence(previous));
                    Mesh.SetVertexFace(vertex, target, Mesh.Face(previous, source));
                }
            }

            var next = Mesh.Vertex(face, slotCcw);
            if (next != -1)
            {
                var baseSlot = Mesh.FindFaceSlot(next, face);
                var target = DecModN(faceSlot, Mesh.Valence(vertex));
                if (Mesh.Face(vertex, target) == -1)
                {
                    var source = IncModN(baseSlot, Mesh.Valence(next));
                    Mesh.SetVertexFace(vertex, target, Mesh.Face(next, source));
                }
            }
        }

        private void CompleteVertex(int face)
        {
            while (true)
            {
                Cancel.Every(_token, _tick++);
                var slot = Mesh.FindVertexSlot(face, -1);
                if (slot == -1)
                {
                    return;
                }

                var vertex = ActivateVertex(face, slot);
                if (Mesh.Vertex(face, slot) != vertex || Mesh.Face(vertex, 0) != face)
                {
                    return;
                }

                CompleteFace(vertex, slot);
            }
        }

        private void CompleteFace(int vertex, int vertexSlot)
        {
            var valence = Mesh.Valence(vertex);
            var facePrevious = Mesh.Face(vertex, 0);
            var slotPrevious = vertexSlot;
            var index = 1;
            while (true)
            {
                Cancel.Every(_token, _tick++);
                var faceNext = Mesh.Face(vertex, index);
                if (faceNext == -1)
                {
                    break;
                }

                slotPrevious = DecModN(slotPrevious, Mesh.Degree(facePrevious));
                var neighbour = Mesh.Vertex(facePrevious, slotPrevious);
                if (neighbour == -1)
                {
                    break;
                }

                var slotNext = Mesh.FindVertexSlot(faceNext, neighbour);
                if (slotNext <= -1)
                {
                    return;
                }

                slotNext = DecModN(slotNext, Mesh.Degree(faceNext));
                AddVertexToFace(vertex, index, faceNext, slotNext);
                facePrevious = faceNext;
                slotPrevious = slotNext;
                index++;
                if (index >= valence)
                {
                    return;
                }
            }

            var last = index;
            facePrevious = Mesh.Face(vertex, 0);
            slotPrevious = vertexSlot;
            index = Mesh.Valence(vertex) - 1;
            while (true)
            {
                Cancel.Every(_token, _tick++);
                var faceNext = Mesh.Face(vertex, index);
                if (faceNext == -1)
                {
                    break;
                }

                slotPrevious = IncModN(slotPrevious, Mesh.Degree(facePrevious));
                var neighbour = Mesh.Vertex(facePrevious, slotPrevious);
                if (neighbour == -1)
                {
                    break;
                }

                var slotNext = Mesh.FindVertexSlot(faceNext, neighbour);
                if (slotNext <= -1)
                {
                    return;
                }

                slotNext = IncModN(slotNext, Mesh.Degree(faceNext));
                AddVertexToFace(vertex, index, faceNext, slotNext);
                facePrevious = faceNext;
                slotPrevious = slotNext;
                index--;
                if (index < last)
                {
                    return;
                }
            }

            while (last <= index)
            {
                Cancel.Every(_token, _tick++);
                var face = ActivateFace(vertex, last);
                if (face < -1)
                {
                    return;
                }

                last++;
            }
        }

        private int? NextActiveFace()
        {
            while (_active.Count > 0 && BitTest(_removed, _active[^1]))
            {
                _active.RemoveAt(_active.Count - 1);
            }

            var best = -1;
            var lowest = int.MaxValue;
            var index = _active.Count - 1;
            while (index >= Math.Max(_active.Count - ActiveScanWidth, 0))
            {
                var face = _active[index];
                if (BitTest(_removed, face))
                {
                    _active.RemoveAt(index);
                    index--;
                    continue;
                }

                var empty = Mesh.EmptyFaceSlots(face);
                if (empty < lowest)
                {
                    lowest = empty;
                    best = face;
                }

                index--;
            }

            return best != -1 ? best : null;
        }
    }
}
