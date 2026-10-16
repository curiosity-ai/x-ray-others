using System.Buffers.Binary;

namespace XRay.OneNote.Internal.OneStore;

/// <summary>An MS-ONESTORE ExtendedGUID: a GUID plus a 32-bit sequence number (section 2.2.1).</summary>
internal readonly record struct ExtendedGuid(Guid Guid, uint N)
{
    public bool IsZero => Guid == Guid.Empty && N == 0;

    public override string ToString() => $"{Guid:D}/{N}";
}

/// <summary>A JCID (section 2.6.14): the object type index plus its flag bits.</summary>
internal readonly record struct Jcid(ushort Index, bool IsBinary, bool IsPropertySet, bool IsGraphNode, bool IsFileData, bool IsReadOnly)
{
    public static Jcid FromRaw(uint raw) => new(
        (ushort)(raw & 0xFFFF),
        ((raw >> 16) & 1) != 0,
        ((raw >> 17) & 1) != 0,
        ((raw >> 18) & 1) != 0,
        ((raw >> 19) & 1) != 0,
        ((raw >> 20) & 1) != 0);
}

/// <summary>One FileNode (section 2.4.3): its id, the reference formats from the header and the raw payload.</summary>
internal sealed record FileNode(uint FileNodeId, ReadOnlyMemory<byte> Payload, int StpFormat, int CbFormat, int BaseType);

/// <summary>The kinds a decoded property value can take (MS-ONESTORE 2.6.6 property types, after decoding).</summary>
internal enum PropertyKind
{
    /// <summary>No value: an unresolved object reference or a property type this reader does not decode.</summary>
    None,
    Bool,
    Integer,
    Bytes,
    ObjectId,
    List,
    PropertySet,
}

/// <summary>
/// A decoded property value. Values stay loosely typed, as in the format: an object's properties are
/// interpreted by the MS-ONE layer, which checks the kind before using a value.
/// </summary>
internal sealed class PropertyValue
{
    public static readonly PropertyValue None = new(PropertyKind.None);

    private PropertyValue(PropertyKind kind)
    {
        Kind = kind;
    }

    public PropertyKind Kind { get; }

    public bool BoolValue { get; private init; }

    public ulong IntegerValue { get; private init; }

    public byte[] BytesValue { get; private init; } = [];

    public ExtendedGuid ObjectIdValue { get; private init; }

    public List<PropertyValue> Items { get; private init; } = [];

    public PropertySet? SetValue { get; private init; }

    public static PropertyValue FromBool(bool value) => new(PropertyKind.Bool) { BoolValue = value };

    public static PropertyValue FromInteger(ulong value) => new(PropertyKind.Integer) { IntegerValue = value };

    public static PropertyValue FromBytes(byte[] value) => new(PropertyKind.Bytes) { BytesValue = value };

    public static PropertyValue FromObjectId(ExtendedGuid value) => new(PropertyKind.ObjectId) { ObjectIdValue = value };

    public static PropertyValue FromList(List<PropertyValue> items) => new(PropertyKind.List) { Items = items };

    public static PropertyValue FromSet(PropertySet set) => new(PropertyKind.PropertySet) { SetValue = set };

    /// <summary>
    /// Truthiness as the reference implementation evaluates it: false for no value, false, zero and
    /// empty containers; true otherwise (an object reference is always true).
    /// </summary>
    public bool IsTruthy => Kind switch
    {
        PropertyKind.None => false,
        PropertyKind.Bool => BoolValue,
        PropertyKind.Integer => IntegerValue != 0,
        PropertyKind.Bytes => BytesValue.Length > 0,
        PropertyKind.ObjectId => true,
        PropertyKind.List => Items.Count > 0,
        PropertyKind.PropertySet => SetValue!.Count > 0,
        _ => false,
    };

    /// <summary>The value as an integer when it is one (a boolean counts as 0 or 1), otherwise null.</summary>
    public ulong? AsInteger() => Kind switch
    {
        PropertyKind.Integer => IntegerValue,
        PropertyKind.Bool => BoolValue ? 1UL : 0UL,
        _ => null,
    };

    /// <summary>The integer value when it fits in <see cref="int"/>, otherwise null.</summary>
    public int? AsInt32()
    {
        var value = AsInteger();
        return value is { } v && v <= int.MaxValue ? (int)v : null;
    }

    /// <summary>The integer value when it fits in <see cref="uint"/>, otherwise null.</summary>
    public uint? AsUInt32()
    {
        var value = AsInteger();
        return value is { } v && v <= uint.MaxValue ? (uint)v : null;
    }

    /// <summary>Non-empty raw bytes, otherwise null.</summary>
    public byte[]? AsNonEmptyBytes() => Kind == PropertyKind.Bytes && BytesValue.Length > 0 ? BytesValue : null;

    public ExtendedGuid? AsObjectId() => Kind == PropertyKind.ObjectId ? ObjectIdValue : null;
}

/// <summary>
/// A property set (section 2.6.7): the decoded values by property name, plus the raw sequence in file
/// order. A name that occurs more than once maps to a list of its values, matching the reference reader.
/// </summary>
internal sealed class PropertySet
{
    public static readonly PropertySet Empty = new();

    private readonly OrderedDictionary<string, PropertyValue> _values = new(StringComparer.Ordinal);

    public List<(string Name, PropertyValue Value)> Raw { get; } = [];

    public int Count => _values.Count;

    public IEnumerable<PropertyValue> Values => _values.Values;

    public PropertyValue Get(string name) => _values.TryGetValue(name, out var value) ? value : PropertyValue.None;

    public bool Contains(string name) => _values.ContainsKey(name);

    public void Add(string name, PropertyValue value)
    {
        Raw.Add((name, value));
        if (!_values.TryGetValue(name, out var existing))
        {
            _values[name] = value;
            return;
        }

        if (existing.Kind == PropertyKind.List)
        {
            existing.Items.Add(value);
        }
        else
        {
            _values[name] = PropertyValue.FromList([existing, value]);
        }
    }
}

/// <summary>A declared object: its id, JCID and properties, or a file data reference for file data objects.</summary>
internal sealed class OneStoreObject
{
    public required ExtendedGuid Oid { get; init; }

    public required Jcid Jcid { get; init; }

    public required string JcidName { get; init; }

    public required PropertySet Properties { get; init; }

    public string? FileDataReference { get; init; }

    public string? FileExtension { get; init; }
}

/// <summary>One revision manifest (section 2.1.8): its ids, root objects by role and the objects it declares.</summary>
internal sealed class RevisionManifestRecord
{
    public required ExtendedGuid Rid { get; init; }

    public ExtendedGuid? RidDependent { get; init; }

    public ExtendedGuid? ContextId { get; init; }

    public OrderedDictionary<uint, ExtendedGuid> RootRoles { get; } = new();

    public OrderedDictionary<ExtendedGuid, OneStoreObject> Objects { get; set; } = new();

    public string? SectionDisplayName { get; set; }

    public int OrderIndex { get; init; }
}

/// <summary>A RevisionRoleDeclaration or RevisionRoleAndContextDeclaration node (sections 2.5.17, 2.5.18).</summary>
internal sealed record RevisionRoleBinding(ExtendedGuid Rid, ExtendedGuid? ContextId, uint Role, int OrderIndex);

/// <summary>A (context, role) pair; a null context is the default context.</summary>
internal readonly record struct ContextRole(ExtendedGuid? ContextId, uint Role);

/// <summary>The state of one object space: its revisions and the current objects.</summary>
internal sealed class ObjectSpaceState
{
    public ObjectSpaceState(ExtendedGuid gosid)
    {
        Gosid = gosid;
    }

    public ExtendedGuid Gosid { get; }

    public OrderedDictionary<uint, ExtendedGuid> RootRoles { get; set; } = new();

    public OrderedDictionary<ExtendedGuid, OneStoreObject> Objects { get; set; } = new();

    public string? SectionDisplayName { get; set; }

    public List<RevisionManifestRecord> Revisions { get; } = [];

    public List<RevisionRoleBinding> RoleBindings { get; } = [];

    public OrderedDictionary<ContextRole, ExtendedGuid> LatestRootsByContextRole { get; set; } = new();

    public OrderedDictionary<ContextRole, int> LatestRevisionIndicesByContextRole { get; set; } = new();

    public ExtendedGuid? GetLatestRoot(uint role, ExtendedGuid? contextId = null)
    {
        if (LatestRootsByContextRole.Count > 0)
        {
            return LatestRootsByContextRole.TryGetValue(new ContextRole(contextId, role), out var root) ? root : null;
        }

        if (contextId is null)
        {
            return RootRoles.TryGetValue(role, out var root) ? root : null;
        }

        return null;
    }

    public List<(ExtendedGuid? ContextId, ExtendedGuid Root)> IterLatestRoots(uint role)
    {
        if (LatestRootsByContextRole.Count == 0)
        {
            return RootRoles.TryGetValue(role, out var root) ? [(null, root)] : [];
        }

        var result = new List<(ExtendedGuid?, ExtendedGuid)>();
        foreach (var (key, root) in LatestRootsByContextRole)
        {
            if (key.Role == role)
            {
                result.Add((key.ContextId, root));
            }
        }

        return result;
    }

    public int? GetLatestRevisionIndex(uint role, ExtendedGuid? contextId = null)
    {
        if (LatestRevisionIndicesByContextRole.Count > 0)
        {
            return LatestRevisionIndicesByContextRole.TryGetValue(new ContextRole(contextId, role), out var index) ? index : null;
        }

        for (var index = Revisions.Count - 1; index >= 0; index--)
        {
            var revision = Revisions[index];
            if (revision.ContextId != contextId)
            {
                continue;
            }

            if (RevisionChains.ResolveRoot(this, revision, role) is not null)
            {
                return index;
            }
        }

        return null;
    }
}

/// <summary>The parsed revision store: every object space plus the file data store.</summary>
internal sealed class OneStoreFile
{
    public required List<ObjectSpaceState> ObjectSpaces { get; init; }

    public required Dictionary<string, byte[]> FileDataStore { get; init; }

    public string? DisplayName { get; init; }
}

/// <summary>Bounds-checked little-endian reads. Out-of-range reads raise <see cref="FileCorruptedException"/>.</summary>
internal static class Bytes
{
    public static uint U32(ReadOnlySpan<byte> data, long offset)
    {
        if (offset < 0 || offset > data.Length - 4)
        {
            throw Truncated(offset, 4);
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data[(int)offset..]);
    }

    public static ushort U16(ReadOnlySpan<byte> data, long offset)
    {
        if (offset < 0 || offset > data.Length - 2)
        {
            throw Truncated(offset, 2);
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(data[(int)offset..]);
    }

    public static ulong U64(ReadOnlySpan<byte> data, long offset)
    {
        if (offset < 0 || offset > data.Length - 8)
        {
            throw Truncated(offset, 8);
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(data[(int)offset..]);
    }

    /// <summary>
    /// Reads an unsigned little-endian integer of <paramref name="size"/> bytes, using whatever bytes are
    /// present: a short read yields the value of the available prefix, as the reference reader does.
    /// </summary>
    public static ulong LenientUInt(ReadOnlySpan<byte> data, long offset, int size)
    {
        ulong value = 0;
        for (var i = 0; i < size; i++)
        {
            var at = offset + i;
            if (at < 0 || at >= data.Length)
            {
                break;
            }

            value |= (ulong)data[(int)at] << (8 * i);
        }

        return value;
    }

    /// <summary>Reads a GUID; a truncated GUID is zero-padded rather than rejected.</summary>
    public static Guid Guid(ReadOnlySpan<byte> data, long offset)
    {
        Span<byte> buffer = stackalloc byte[16];
        buffer.Clear();
        if (offset >= 0 && offset < data.Length)
        {
            var available = (int)Math.Min(16, data.Length - offset);
            data.Slice((int)offset, available).CopyTo(buffer);
        }

        return new Guid(buffer);
    }

    public static ExtendedGuid ExtendedGuid(ReadOnlySpan<byte> data, long offset) => new(Guid(data, offset), U32(data, offset + 16));

    /// <summary>A slice clamped to the buffer, like a Python slice: never throws, may be shorter or empty.</summary>
    public static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> data, ulong offset, ulong size)
    {
        if (offset >= (ulong)data.Length)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var start = (int)offset;
        var length = (int)Math.Min(size, (ulong)(data.Length - start));
        return data.Slice(start, length);
    }

    public static FileCorruptedException Truncated(long offset, int size) =>
        new($"Unexpected end of data reading {size} bytes at offset {offset}");
}
