using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

using XRay.ThreeD.Internal;

/// <summary>
/// The USD "crate" binary layer (.usdc, "PXR-USDC", versions 0.4 and later), written from the public description of the format: a
/// bootstrap header, a table of sections (TOKENS, STRINGS, FIELDS, FIELDSETS, PATHS, SPECS), LZ4 and delta/variable-width integer
/// coding, and 64-bit value representations that are either inlined or point at the data. The result is the same stage model the text
/// reader fills, so both feed one scene builder. Values the scene builder does not read (dictionaries, time samples, payloads,
/// variants) are skipped, never guessed.
/// </summary>
internal static class UsdcReader
{
    private const int MaxCount = 1 << 26;

    public static bool IsCrate(ReadOnlySpan<byte> data) => data.StartsWith("PXR-USDC"u8);

    public static UsdaReader.Stage ReadStage(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var crate = new Crate(data, options);
        return crate.Parse();
    }

    private enum T
    {
        Invalid = 0, Bool, UChar, Int, UInt, Int64, UInt64, Half, Float, Double, String, Token, AssetPath, Matrix2d, Matrix3d, Matrix4d, Quatd, Quatf, Quath,
        Vec2d, Vec2f, Vec2h, Vec2i, Vec3d, Vec3f, Vec3h, Vec3i, Vec4d, Vec4f, Vec4h, Vec4i,
        Dictionary, TokenListOp, StringListOp, PathListOp, ReferenceListOp, IntListOp, Int64ListOp, UIntListOp, UInt64ListOp,
        PathVector, TokenVector, Specifier, Permission, Variability, VariantSelectionMap, TimeSamples, Payload, DoubleVector,
        LayerOffsetVector, StringVector, ValueBlock, Value, UnregisteredValue, UnregisteredValueListOp, PayloadListOp, TimeCode,
    }

    private enum Kind { Bool, Byte, Int, UInt, Long, ULong, Half, Float, Double }

    private sealed class Crate
    {
        private readonly ReadOnlyMemory<byte> _m;
        private readonly LoadOptions _options;
        private readonly int _minor;
        private string[] _tokens = [];
        private uint[] _strings = [];
        private uint[] _fieldTokens = [];
        private ulong[] _fieldReps = [];
        private uint[] _fieldSets = [];
        private string[] _paths = [];
        private uint[] _specPaths = [];
        private uint[] _specFieldSets = [];
        private uint[] _specTypes = [];

        public Crate(ReadOnlyMemory<byte> data, LoadOptions options)
        {
            _m = data;
            _options = options;
            var d = data.Span;
            if (d.Length < 88 || !IsCrate(d))
            {
                throw new ModelFormatException("Not a USD crate file.");
            }

            if (d[8] != 0)
            {
                throw new UnsupportedModelFormatException(ModelFormats.Usd, $"USD crate version {d[8]}.{d[9]}.{d[10]} is newer than this reader understands.");
            }

            _minor = d[9];
            if (_minor < 4)
            {
                throw new UnsupportedModelFormatException(ModelFormats.Usd, $"USD crate version 0.{_minor} (before 0.4, uncompressed sections) is not supported; re-save the stage with a current USD.");
            }
        }

        // ---- raw access ----------------------------------------------------------------------------------------------

        private ReadOnlySpan<byte> Slice(long pos, long length)
        {
            if (pos < 0 || length < 0 || pos > _m.Length || length > _m.Length - pos)
            {
                throw new ModelFormatException("USDC: a read goes beyond the end of the file.");
            }

            return _m.Span.Slice((int)pos, (int)length);
        }

        private uint U32(long pos) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(pos, 4));

        private ulong U64(long pos) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(pos, 8));

        private int Count(ulong value, string what)
        {
            if (value > MaxCount)
            {
                throw new ModelFormatException($"USDC: the {what} count is implausible.");
            }

            return (int)value;
        }

        // ---- compression ---------------------------------------------------------------------------------------------

        /// <summary>One LZ4 block (the public block format) into <paramref name="dst"/>; returns the bytes written.</summary>
        private int Lz4(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int sp = 0, dp = 0, iteration = 0;
            while (sp < src.Length)
            {
                Cancel.Every(_options.Cancellation, iteration++, 255);
                var token = src[sp++];
                long literals = token >> 4;
                if (literals == 15)
                {
                    int b;
                    do
                    {
                        if (sp >= src.Length)
                        {
                            throw new ModelFormatException("USDC: LZ4 data is truncated.");
                        }

                        b = src[sp++];
                        literals += b;
                    }
                    while (b == 255);
                }

                if (literals > src.Length - sp || literals > dst.Length - dp)
                {
                    throw new ModelFormatException("USDC: LZ4 literal run is out of range.");
                }

                src.Slice(sp, (int)literals).CopyTo(dst[dp..]);
                sp += (int)literals;
                dp += (int)literals;
                if (sp >= src.Length)
                {
                    break;
                }

                if (sp + 2 > src.Length)
                {
                    throw new ModelFormatException("USDC: LZ4 data is truncated.");
                }

                var offset = src[sp] | (src[sp + 1] << 8);
                sp += 2;
                if (offset == 0 || offset > dp)
                {
                    throw new ModelFormatException("USDC: LZ4 match offset is out of range.");
                }

                long match = token & 15;
                if (match == 15)
                {
                    int b;
                    do
                    {
                        if (sp >= src.Length)
                        {
                            throw new ModelFormatException("USDC: LZ4 data is truncated.");
                        }

                        b = src[sp++];
                        match += b;
                    }
                    while (b == 255);
                }

                match += 4;
                if (match > dst.Length - dp)
                {
                    throw new ModelFormatException("USDC: LZ4 match runs past the output.");
                }

                var from = dp - offset;
                for (var k = 0; k < match; k++)
                {
                    dst[dp++] = dst[from + k];
                }
            }

            return dp;
        }

        /// <summary>The chunk framing around LZ4 blocks: a count byte (0: one block), then per chunk a 32-bit size and the block.</summary>
        private int FastDecompress(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            if (src.Length < 1)
            {
                throw new ModelFormatException("USDC: compressed block is empty.");
            }

            int chunks = src[0];
            if (chunks == 0)
            {
                return Lz4(src[1..], dst);
            }

            var sp = 1;
            var written = 0;
            for (var c = 0; c < chunks; c++)
            {
                if (sp + 4 > src.Length)
                {
                    throw new ModelFormatException("USDC: chunked block is truncated.");
                }

                var size = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(sp, 4));
                sp += 4;
                if (size < 0 || size > src.Length - sp)
                {
                    throw new ModelFormatException("USDC: chunk size is out of range.");
                }

                written += Lz4(src.Slice(sp, size), dst[written..]);
                sp += size;
            }

            return written;
        }

        /// <summary>A compressed run of 32-bit integers: size-prefixed, LZ4 over a most-common-delta, 2-bit-code, variable-width coding.</summary>
        private uint[] ReadCompressedInts(ref long pos, long count)
        {
            var compressed = Count(U64(pos), "compressed size");
            pos += 8;
            var src = Slice(pos, compressed);
            pos += compressed;
            return DecodeInts(src, Count((ulong)count, "integer"));
        }

        private uint[] DecodeInts(ReadOnlySpan<byte> src, int n)
        {
            var result = new uint[n];
            if (n == 0)
            {
                return result;
            }

            var codeBytes = ((long)n * 2 + 7) / 8;
            var capacity = 4 + codeBytes + (long)n * 4;
            if (capacity > 255L * src.Length + 64)
            {
                throw new ModelFormatException("USDC: a compressed integer run claims more data than its bytes can hold.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent((int)capacity);
            try
            {
                var length = FastDecompress(src, buffer.AsSpan(0, (int)capacity));
                var work = buffer.AsSpan(0, length);
                if (length < 4 + codeBytes)
                {
                    throw new ModelFormatException("USDC: compressed integers are truncated.");
                }

                var common = BinaryPrimitives.ReadInt32LittleEndian(work);
                var codes = work.Slice(4, (int)codeBytes);
                var vints = work[(int)(4 + codeBytes)..];
                var vp = 0;
                var prev = 0;
                for (var i = 0; i < n; i++)
                {
                    Cancel.Every(_options.Cancellation, i);
                    var code = (codes[i >> 2] >> ((i & 3) * 2)) & 3;
                    int delta;
                    switch (code)
                    {
                        case 0:
                            delta = common;
                            break;
                        case 1:
                            Need(vints, vp, 1);
                            delta = (sbyte)vints[vp];
                            vp += 1;
                            break;
                        case 2:
                            Need(vints, vp, 2);
                            delta = BinaryPrimitives.ReadInt16LittleEndian(vints[vp..]);
                            vp += 2;
                            break;
                        default:
                            Need(vints, vp, 4);
                            delta = BinaryPrimitives.ReadInt32LittleEndian(vints[vp..]);
                            vp += 4;
                            break;
                    }

                    prev = unchecked(prev + delta);
                    result[i] = unchecked((uint)prev);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return result;
        }

        private static void Need(ReadOnlySpan<byte> span, int pos, int length)
        {
            if (pos + length > span.Length)
            {
                throw new ModelFormatException("USDC: compressed integers are truncated.");
            }
        }

        // ---- sections ------------------------------------------------------------------------------------------------

        public UsdaReader.Stage Parse()
        {
            var token = _options.Cancellation;
            token.ThrowIfCancellationRequested();
            var toc = (long)U64(16);
            if (toc < 88 || toc > _m.Length - 8)
            {
                throw new ModelFormatException("USDC: the table of contents is out of range.");
            }

            var sections = Count(U64(toc), "section");
            if (sections > 64)
            {
                throw new ModelFormatException("USDC: too many sections.");
            }

            var found = new Dictionary<string, (long Start, long Size)>(StringComparer.Ordinal);
            for (var i = 0; i < sections; i++)
            {
                var entry = Slice(toc + 8 + i * 32L, 32);
                var nameLength = entry[..16].IndexOf((byte)0);
                var name = Encoding.ASCII.GetString(entry[..(nameLength < 0 ? 16 : nameLength)]);
                var start = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
                var size = BinaryPrimitives.ReadInt64LittleEndian(entry[24..]);
                Slice(start, size);
                found[name] = (start, size);
            }

            foreach (var required in new[] { "TOKENS", "STRINGS", "FIELDS", "FIELDSETS", "PATHS", "SPECS" })
            {
                if (!found.ContainsKey(required))
                {
                    throw new ModelFormatException($"USDC: the {required} section is missing.");
                }
            }

            ReadTokens(found["TOKENS"].Start);
            token.ThrowIfCancellationRequested();
            ReadStrings(found["STRINGS"].Start);
            ReadFields(found["FIELDS"].Start);
            ReadFieldSets(found["FIELDSETS"].Start);
            token.ThrowIfCancellationRequested();
            ReadPaths(found["PATHS"].Start);
            token.ThrowIfCancellationRequested();
            ReadSpecs(found["SPECS"].Start);
            return BuildStage();
        }

        private void ReadTokens(long pos)
        {
            var count = Count(U64(pos), "token");
            var uncompressed = U64(pos + 8);
            var compressed = U64(pos + 16);
            pos += 24;
            if (uncompressed > int.MaxValue || compressed > int.MaxValue || (long)uncompressed > 255L * (long)compressed + 64)
            {
                throw new ModelFormatException("USDC: the token block sizes are implausible.");
            }

            var src = Slice(pos, (long)compressed);
            var buffer = ArrayPool<byte>.Shared.Rent((int)uncompressed);
            try
            {
                var length = FastDecompress(src, buffer.AsSpan(0, (int)uncompressed));
                var text = buffer.AsSpan(0, length);
                _tokens = new string[count];
                var at = 0;
                for (var i = 0; i < count; i++)
                {
                    Cancel.Every(_options.Cancellation, i);
                    if (at > text.Length)
                    {
                        throw new ModelFormatException("USDC: the token block ends early.");
                    }

                    var end = text[at..].IndexOf((byte)0);
                    if (end < 0)
                    {
                        throw new ModelFormatException("USDC: a token is not terminated.");
                    }

                    _tokens[i] = Encoding.UTF8.GetString(text.Slice(at, end));
                    at += end + 1;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void ReadStrings(long pos)
        {
            var count = Count(U64(pos), "string");
            var raw = Slice(pos + 8, count * 4L);
            _strings = new uint[count];
            for (var i = 0; i < count; i++)
            {
                _strings[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw[(i * 4)..]);
            }
        }

        private void ReadFields(long pos)
        {
            var count = Count(U64(pos), "field");
            pos += 8;
            _fieldTokens = ReadCompressedInts(ref pos, count);
            var compressed = Count(U64(pos), "compressed size");
            pos += 8;
            var src = Slice(pos, compressed);
            _fieldReps = new ulong[count];
            var need = count * 8L;
            if (need > 255L * compressed + 64)
            {
                throw new ModelFormatException("USDC: the field block claims more data than its bytes can hold.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(need, 1));
            try
            {
                var length = FastDecompress(src, buffer.AsSpan(0, (int)need));
                if (length < need)
                {
                    throw new ModelFormatException("USDC: the field values are truncated.");
                }

                for (var i = 0; i < count; i++)
                {
                    _fieldReps[i] = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i * 8));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void ReadFieldSets(long pos)
        {
            var count = Count(U64(pos), "field set");
            pos += 8;
            _fieldSets = ReadCompressedInts(ref pos, count);
        }

        private string Token(uint index) => index < _tokens.Length ? _tokens[index] : throw new ModelFormatException("USDC: a token index is out of range.");

        private string Str(uint index) => index < _strings.Length ? Token(_strings[index]) : throw new ModelFormatException("USDC: a string index is out of range.");

        private string Path(uint index) => index < _paths.Length ? _paths[index] ?? "" : throw new ModelFormatException("USDC: a path index is out of range.");

        private void ReadPaths(long pos)
        {
            var count = Count(U64(pos), "path");
            var encoded = Count(U64(pos + 8), "encoded path");
            pos += 16;
            var indexes = ReadCompressedInts(ref pos, encoded);
            var elements = ReadCompressedInts(ref pos, encoded);
            var jumps = ReadCompressedInts(ref pos, encoded);
            _paths = new string[count];

            // The tree is stored depth first: a node is followed by its first child; the jump says where its next sibling is
            // (-1: it only has a child, -2: it has neither, 0 or more: the sibling is that many entries on). Children come right after.
            var work = new Stack<(int Index, string Parent)>();
            work.Push((0, ""));
            var visited = 0;
            while (work.Count > 0)
            {
                var (start, parentStart) = work.Pop();
                var cursor = start;
                var parent = parentStart;
                var hasChild = true;
                var hasSibling = true;
                while (hasChild || hasSibling)
                {
                    Cancel.Every(_options.Cancellation, visited);
                    if (cursor >= encoded || ++visited > encoded + 1)
                    {
                        throw new ModelFormatException("USDC: the path tree is inconsistent.");
                    }

                    var thisIndex = cursor++;
                    string path;
                    if (parent.Length == 0)
                    {
                        path = "/";
                        hasChild = true;
                        hasSibling = false;
                        var jump0 = unchecked((int)jumps[thisIndex]);
                        hasChild = jump0 > 0 || jump0 == -1;
                        hasSibling = jump0 >= 0;
                    }
                    else
                    {
                        var jump = unchecked((int)jumps[thisIndex]);
                        hasChild = jump > 0 || jump == -1;
                        hasSibling = jump >= 0;
                        var elementToken = unchecked((int)elements[thisIndex]);
                        var isProperty = elementToken < 0;
                        var name = Token((uint)Math.Abs((long)elementToken));
                        path = isProperty ? parent + "." + name : name.StartsWith('{') ? parent + name : parent == "/" ? "/" + name : parent + "/" + name;
                    }

                    if (indexes[thisIndex] >= _paths.Length)
                    {
                        throw new ModelFormatException("USDC: a path index is out of range.");
                    }

                    _paths[indexes[thisIndex]] = path;
                    if (hasChild)
                    {
                        if (hasSibling)
                        {
                            var sibling = (long)thisIndex + unchecked((int)jumps[thisIndex]);
                            if (sibling <= thisIndex || sibling >= encoded)
                            {
                                throw new ModelFormatException("USDC: a path jump is out of range.");
                            }

                            work.Push(((int)sibling, parent));
                        }

                        parent = path;
                    }
                }
            }
        }

        private void ReadSpecs(long pos)
        {
            var count = Count(U64(pos), "spec");
            pos += 8;
            _specPaths = ReadCompressedInts(ref pos, count);
            _specFieldSets = ReadCompressedInts(ref pos, count);
            _specTypes = ReadCompressedInts(ref pos, count);
        }

        // ---- the stage -----------------------------------------------------------------------------------------------

        private const uint SpecAttribute = 1, SpecPrim = 6, SpecPseudoRoot = 7, SpecRelationship = 8;

        private UsdaReader.Stage BuildStage()
        {
            var stage = new UsdaReader.Stage();
            var prims = new Dictionary<string, UsdaReader.Prim>(StringComparer.Ordinal);
            var order = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var primSpecs = new List<(string Path, int Spec)>();
            for (var i = 0; i < _specPaths.Length; i++)
            {
                Cancel.Every(_options.Cancellation, i);
                var path = Path(_specPaths[i]);
                if (path.Contains('{', StringComparison.Ordinal))
                {
                    continue; // inside a variant: not composed
                }

                switch (_specTypes[i])
                {
                    case SpecPseudoRoot:
                        foreach (var (name, rep) in Fields(i))
                        {
                            if (name is "upAxis" or "metersPerUnit" or "defaultPrim" or "doc" or "subLayers" && ValueOf(rep) is { } v)
                            {
                                stage.Metadata[name] = v is double[] { Length: 1 } one ? one[0] : v;
                            }
                            else if (name == "primChildren" && ValueOf(rep) is List<object> kids)
                            {
                                order["/"] = kids.OfType<string>().ToList();
                            }
                        }

                        break;
                    case SpecPrim:
                        primSpecs.Add((path, i));
                        break;
                }
            }

            foreach (var (path, spec) in primSpecs)
            {
                var slash = path.LastIndexOf('/');
                var prim = new UsdaReader.Prim { Name = path[(slash + 1)..], Kind = "def" };
                foreach (var (name, rep) in Fields(spec))
                {
                    switch (name)
                    {
                        case "specifier":
                            prim.Kind = ValueOf(rep) as string ?? "def";
                            break;
                        case "typeName":
                            prim.Type = ValueOf(rep) as string ?? "";
                            break;
                        case "primChildren":
                            if (ValueOf(rep) is List<object> kids)
                            {
                                order[path] = kids.OfType<string>().ToList();
                            }

                            break;
                        case "references":
                            if (ValueOf(rep) is List<object> refs && refs.Count > 0)
                            {
                                prim.Metadata["references"] = refs;
                            }

                            break;
                    }
                }

                prims[path] = prim;
            }

            // Properties.
            for (var i = 0; i < _specPaths.Length; i++)
            {
                Cancel.Every(_options.Cancellation, i);
                var kind = _specTypes[i];
                if (kind is not (SpecAttribute or SpecRelationship))
                {
                    continue;
                }

                var path = Path(_specPaths[i]);
                var dot = path.IndexOf('.', StringComparison.Ordinal);
                if (dot < 0 || path.Contains('{', StringComparison.Ordinal) || !prims.TryGetValue(path[..dot], out var owner))
                {
                    continue;
                }

                var property = path[(dot + 1)..];
                object? value = null;
                object? timeSample = null;
                string? interpolation = null;
                foreach (var (name, rep) in Fields(i))
                {
                    switch (name)
                    {
                        case "default" when kind == SpecAttribute:
                            value = ValueOf(rep);
                            break;
                        case "timeSamples" when kind == SpecAttribute:
                            timeSample = ValueOf(rep);
                            break;
                        case "interpolation":
                            interpolation = ValueOf(rep) as string;
                            break;
                        case "targetPaths" when kind == SpecRelationship:
                            if (ValueOf(rep) is List<object> { Count: > 0 } targets)
                            {
                                value = targets[0];
                            }

                            break;
                    }
                }

                value ??= timeSample;
                if (value is double[] numbers)
                {
                    owner.Arrays[property] = numbers;
                    owner.Attributes[property] = Array.Empty<object>().ToList();
                }
                else if (value is not null)
                {
                    owner.Attributes[property] = value;
                }

                if (interpolation is not null)
                {
                    owner.Interpolation[property] = interpolation;
                }
            }

            // The hierarchy, in authored order.
            void Attach(string parentPath, UsdaReader.Prim? parent)
            {
                var names = order.TryGetValue(parentPath, out var authored) ? authored : [];
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var childName in names)
                {
                    var childPath = parentPath == "/" ? "/" + childName : parentPath + "/" + childName;
                    if (prims.TryGetValue(childPath, out var child) && seen.Add(childPath))
                    {
                        Link(child, parent, childPath);
                    }
                }
            }

            void Link(UsdaReader.Prim child, UsdaReader.Prim? parent, string childPath)
            {
                child.Parent = parent;
                if (parent is null)
                {
                    stage.Roots.Add(child);
                }
                else
                {
                    parent.Children.Add(child);
                }

                _options.Cancellation.ThrowIfCancellationRequested();
                Attach(childPath, child);
            }

            Attach("/", null);
            return stage;
        }

        private IEnumerable<(string Name, ulong Rep)> Fields(int spec)
        {
            var at = (long)_specFieldSets[spec];
            for (var guard = 0; at >= 0 && at < _fieldSets.Length && guard < 4096; at++, guard++)
            {
                var field = _fieldSets[at];
                if (field == uint.MaxValue)
                {
                    yield break;
                }

                if (field >= _fieldTokens.Length)
                {
                    throw new ModelFormatException("USDC: a field index is out of range.");
                }

                yield return (Token(_fieldTokens[field]), _fieldReps[field]);
            }
        }

        // ---- values --------------------------------------------------------------------------------------------------

        private static (Kind Kind, int Components) Layout(T type) => type switch
        {
            T.Bool => (Kind.Bool, 1),
            T.UChar => (Kind.Byte, 1),
            T.Int => (Kind.Int, 1),
            T.UInt => (Kind.UInt, 1),
            T.Int64 => (Kind.Long, 1),
            T.UInt64 => (Kind.ULong, 1),
            T.Half => (Kind.Half, 1),
            T.Float => (Kind.Float, 1),
            T.Double or T.TimeCode => (Kind.Double, 1),
            T.Quatd => (Kind.Double, 4),
            T.Quatf => (Kind.Float, 4),
            T.Quath => (Kind.Half, 4),
            T.Vec2d => (Kind.Double, 2),
            T.Vec2f => (Kind.Float, 2),
            T.Vec2h => (Kind.Half, 2),
            T.Vec2i => (Kind.Int, 2),
            T.Vec3d => (Kind.Double, 3),
            T.Vec3f => (Kind.Float, 3),
            T.Vec3h => (Kind.Half, 3),
            T.Vec3i => (Kind.Int, 3),
            T.Vec4d => (Kind.Double, 4),
            T.Vec4f => (Kind.Float, 4),
            T.Vec4h => (Kind.Half, 4),
            T.Vec4i => (Kind.Int, 4),
            T.Matrix2d => (Kind.Double, 4),
            T.Matrix3d => (Kind.Double, 9),
            T.Matrix4d => (Kind.Double, 16),
            _ => (Kind.Double, 0),
        };

        private static int Size(Kind kind) => kind switch
        {
            Kind.Bool or Kind.Byte => 1,
            Kind.Half => 2,
            Kind.Int or Kind.UInt or Kind.Float => 4,
            _ => 8,
        };

        private static double Read(ReadOnlySpan<byte> s, Kind kind) => kind switch
        {
            Kind.Bool or Kind.Byte => s[0],
            Kind.Int => BinaryPrimitives.ReadInt32LittleEndian(s),
            Kind.UInt => BinaryPrimitives.ReadUInt32LittleEndian(s),
            Kind.Long => BinaryPrimitives.ReadInt64LittleEndian(s),
            Kind.ULong => BinaryPrimitives.ReadUInt64LittleEndian(s),
            Kind.Half => (double)BinaryPrimitives.ReadHalfLittleEndian(s),
            Kind.Float => BinaryPrimitives.ReadSingleLittleEndian(s),
            _ => BinaryPrimitives.ReadDoubleLittleEndian(s),
        };

        /// <summary>Numbers become <c>double[]</c> (flattened), tokens and strings <c>string</c>, vectors and list-ops <c>List&lt;object&gt;</c> of strings; anything else is null.</summary>
        private object? ValueOf(ulong rep)
        {
            try
            {
                return Decode(rep);
            }
            catch (ModelFormatException)
            {
                return null;
            }
        }

        private object? Decode(ulong rep)
        {
            var isArray = (rep >> 63 & 1) != 0;
            var inlined = (rep >> 62 & 1) != 0;
            var compressed = (rep >> 61 & 1) != 0;
            var type = (T)(int)(rep >> 48 & 0xFF);
            var payload = rep & 0xFFFFFFFFFFFFUL;

            switch (type)
            {
                case T.Token or T.String or T.AssetPath when isArray:
                {
                    if (inlined)
                    {
                        return new List<object>();
                    }

                    var at = (long)payload;
                    var count = Count(_minor >= 7 ? U64(at) : U32(at), "array");
                    at += _minor >= 7 ? 8 : 4;
                    var raw = Slice(at, count * 4L);
                    var list = new List<object>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var id = BinaryPrimitives.ReadUInt32LittleEndian(raw[(i * 4)..]);
                        list.Add(type == T.String ? Str(id) : Token(id));
                    }

                    return list;
                }

                case T.Token or T.AssetPath when inlined && !isArray:
                    return Token((uint)payload);
                case T.String when inlined && !isArray:
                    return Str((uint)payload);
                case T.Specifier when inlined:
                    return payload switch { 0 => "def", 1 => "over", _ => "class" };
                case T.Variability or T.Permission when inlined:
                    return payload.ToString();
                case T.TokenVector or T.StringVector or T.PathVector when !inlined:
                {
                    var count = Count(U64((long)payload), "vector");
                    var raw = Slice((long)payload + 8, count * 4L);
                    var list = new List<object>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var id = BinaryPrimitives.ReadUInt32LittleEndian(raw[(i * 4)..]);
                        list.Add(type == T.TokenVector ? Token(id) : type == T.StringVector ? Str(id) : Path(id));
                    }

                    return list;
                }

                case T.TokenVector or T.StringVector or T.PathVector:
                    return new List<object>();
                case T.TokenListOp or T.PathListOp or T.StringListOp or T.ReferenceListOp when !inlined:
                    return ReadListOp(type, (long)payload);
                case T.TimeSamples when !inlined:
                    return null;
            }

            var (kind, comps) = Layout(type);
            if (comps == 0)
            {
                return null;
            }

            if (isArray)
            {
                return ReadArray(type, kind, comps, inlined, compressed, (long)payload);
            }

            var values = new double[comps];
            if (inlined)
            {
                switch (kind)
                {
                    case Kind.Bool or Kind.Byte or Kind.UInt or Kind.ULong:
                        values[0] = (uint)payload;
                        break;
                    case Kind.Int or Kind.Long:
                        values[0] = (int)(uint)payload;
                        break;
                    case Kind.Half:
                        values[0] = (double)BitConverter.UInt16BitsToHalf((ushort)payload);
                        break;
                    case Kind.Float or Kind.Double when comps == 1:
                        values[0] = BitConverter.UInt32BitsToSingle((uint)payload);
                        break;
                    default:
                    {
                        // Vectors and matrices inline one signed byte per component (matrices: the diagonal).
                        var isMatrix = type is T.Matrix2d or T.Matrix3d or T.Matrix4d;
                        var diagonal = isMatrix ? type == T.Matrix2d ? 2 : type == T.Matrix3d ? 3 : 4 : comps;
                        for (var k = 0; k < diagonal && k < 8; k++)
                        {
                            var b = (sbyte)(payload >> (8 * k));
                            values[isMatrix ? k * (diagonal + 1) : k] = b;
                        }

                        break;
                    }
                }

                return Normalise(type, values);
            }

            var bytes = Slice((long)payload, comps * Size(kind));
            for (var k = 0; k < comps; k++)
            {
                values[k] = Read(bytes[(k * Size(kind))..], kind);
            }

            return Normalise(type, values);
        }

        /// <summary>Quaternions are stored imaginary part first; the stage model carries them as (real, i, j, k), as the text form writes them.</summary>
        private static double[] Normalise(T type, double[] v)
        {
            if (type is T.Quatd or T.Quatf or T.Quath && v.Length == 4)
            {
                (v[0], v[1], v[2], v[3]) = (v[3], v[0], v[1], v[2]);
            }

            return v;
        }

        private double[]? ReadArray(T type, Kind kind, int comps, bool inlined, bool compressed, long pos)
        {
            if (inlined)
            {
                return [];
            }

            ulong size;
            if (_minor >= 7)
            {
                size = U64(pos);
                pos += 8;
            }
            else
            {
                size = U32(pos);
                pos += 4;
            }

            var elements = Count(size, "array") ;
            var total = (long)elements * comps;
            if (total > MaxCount)
            {
                throw new ModelFormatException("USDC: an array is implausibly large.");
            }

            var result = new double[total];
            if (!compressed || kind is Kind.Bool or Kind.Byte)
            {
                var bytes = Slice(pos, total * Size(kind));
                var stride = Size(kind);
                for (var i = 0; i < total; i++)
                {
                    Cancel.Every(_options.Cancellation, i);
                    result[i] = Read(bytes[(int)(i * stride)..], kind);
                }

                return Reordered(type, result);
            }

            if (kind is Kind.Int or Kind.UInt)
            {
                var ints = ReadCompressedInts(ref pos, total);
                for (var i = 0; i < total; i++)
                {
                    result[i] = kind == Kind.Int ? unchecked((int)ints[i]) : ints[i];
                }

                return Reordered(type, result);
            }

            if (kind is Kind.Half or Kind.Float or Kind.Double)
            {
                var code = Slice(pos, 1)[0];
                pos++;
                if (code == (byte)'i')
                {
                    var ints = ReadCompressedInts(ref pos, total);
                    for (var i = 0; i < total; i++)
                    {
                        result[i] = unchecked((int)ints[i]);
                    }

                    return Reordered(type, result);
                }

                if (code == (byte)'t')
                {
                    var lutSize = Count(U32(pos), "lookup");
                    pos += 4;
                    var stride = Size(kind);
                    var lut = Slice(pos, lutSize * (long)stride);
                    pos += lutSize * (long)stride;
                    var indexes = ReadCompressedInts(ref pos, total);
                    for (var i = 0; i < total; i++)
                    {
                        Cancel.Every(_options.Cancellation, i);
                        if (indexes[i] >= lutSize)
                        {
                            throw new ModelFormatException("USDC: a lookup index is out of range.");
                        }

                        result[i] = Read(lut[(int)(indexes[i] * stride)..], kind);
                    }

                    return Reordered(type, result);
                }
            }

            throw new ModelFormatException("USDC: this compressed array form is not supported.");
        }

        private static double[] Reordered(T type, double[] v)
        {
            if (type is T.Quatd or T.Quatf or T.Quath)
            {
                for (var i = 0; i + 3 < v.Length; i += 4)
                {
                    (v[i], v[i + 1], v[i + 2], v[i + 3]) = (v[i + 3], v[i], v[i + 1], v[i + 2]);
                }
            }

            return v;
        }

        /// <summary>A list-op: a flag byte (explicit, explicit items, added, deleted, ordered, prepended, appended), then each present list.</summary>
        private List<object>? ReadListOp(T type, long pos)
        {
            var flags = Slice(pos, 1)[0];
            pos++;
            var result = new List<object>();
            // The lists come in the order explicit, added, prepended, appended, deleted, ordered; deleted and ordered are read to be skipped.
            foreach (var bit in new[] { 1 << 1, 1 << 2, 1 << 5, 1 << 6, 1 << 3, 1 << 4 })
            {
                if ((flags & bit) == 0)
                {
                    continue;
                }

                var count = Count(U64(pos), "list");
                pos += 8;
                var keep = bit is not (1 << 3 or 1 << 4);
                for (var i = 0; i < count; i++)
                {
                    Cancel.Every(_options.Cancellation, i);
                    if (type == T.ReferenceListOp)
                    {
                        var asset = Str(U32(pos));
                        var prim = Path(U32(pos + 4));
                        pos += 8 + 16;
                        var customCount = U64(pos);
                        pos += 8;
                        if (keep)
                        {
                            result.Add(asset.Length > 0 ? (prim.Length > 0 ? asset + "\u0001" + prim : asset) : prim);
                        }

                        if (customCount != 0)
                        {
                            return result; // a dictionary follows whose extent is not decoded: keep what was read
                        }
                    }
                    else
                    {
                        var id = U32(pos);
                        pos += 4;
                        if (keep)
                        {
                            result.Add(type switch { T.PathListOp => Path(id), T.StringListOp => Str(id), _ => Token(id) });
                        }
                    }
                }
            }

            return result;
        }
    }
}
