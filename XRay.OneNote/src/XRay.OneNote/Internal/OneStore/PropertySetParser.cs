namespace XRay.OneNote.Internal.OneStore;

/// <summary>
/// Decodes ObjectSpaceObjectPropSet structures (MS-ONESTORE 2.6.1) and the property sets they hold
/// (2.6.7). Property sets are read best-effort: a property whose value runs past the end of the data
/// ends the set, and the properties read before it are kept.
/// </summary>
internal static class PropertySetParser
{
    // Property ids whose declared type is replaced: VersionHistoryGraphSpaceContextNodes is declared
    // as a ContextID array but carries a property set.
    // Property sets nest (a property may hold a set); real files nest a few levels. Deeper nesting
    // only comes from damaged data and would otherwise recurse without bound.
    private const int MaxNesting = 32;

    private static readonly Dictionary<uint, int> TypeOverrides = new()
    {
        [0x3400347B] = 0x11,
    };

    /// <summary>The object ids referenced by a property set, consumed in order as properties ask for them.</summary>
    internal sealed class State
    {
        public State(List<ExtendedGuid> objectIds, List<ExtendedGuid>? objectSpaceIds = null, List<ExtendedGuid>? contextIds = null)
        {
            ObjectIds = objectIds;
            ObjectSpaceIds = objectSpaceIds ?? [];
            ContextIds = contextIds ?? [];
        }

        public List<ExtendedGuid> ObjectIds { get; }

        /// <summary>The object space references (the OSID stream), consumed by ObjectSpaceID properties.</summary>
        public List<ExtendedGuid> ObjectSpaceIds { get; }

        /// <summary>The context references (the context stream), consumed by ContextID properties.</summary>
        public List<ExtendedGuid> ContextIds { get; }

        public long OidCursor { get; set; }

        public long OsidCursor { get; set; }

        public long ContextCursor { get; set; }
    }

    /// <summary>Parses an ObjectSpaceObjectPropSet blob: the OID streams, then the property set.</summary>
    public static PropertySet ParseObjectBlob(ReadOnlySpan<byte> blob, Dictionary<uint, Guid> gidTable)
    {
        long offset = 0;
        var header = Bytes.U32(blob, offset);
        offset += 4;
        var oidCount = header & 0x00FFFFFF;
        var noOsidStream = ((header >> 31) & 1) != 0;

        var objectIds = new List<ExtendedGuid>();
        for (long index = 0; index < oidCount; index++)
        {
            var resolved = ResolveObjectId(Bytes.U32(blob, offset + (index * 4)), gidTable);
            if (resolved is { } oid)
            {
                objectIds.Add(oid);
            }
        }

        offset += oidCount * 4L;

        var objectSpaceIds = new List<ExtendedGuid>();
        var contextIds = new List<ExtendedGuid>();
        if (!noOsidStream)
        {
            var osidHeader = Bytes.U32(blob, offset);
            offset += 4;
            var osidCount = osidHeader & 0x00FFFFFF;
            var osidHasExtendedStreams = ((osidHeader >> 30) & 1) != 0;
            for (long index = 0; index < osidCount; index++)
            {
                if (ResolveObjectId(Bytes.U32(blob, offset + (index * 4)), gidTable) is { } osid)
                {
                    objectSpaceIds.Add(osid);
                }
            }

            offset += osidCount * 4L;
            if (osidHasExtendedStreams)
            {
                var contextHeader = Bytes.U32(blob, offset);
                offset += 4;
                var contextCount = contextHeader & 0x00FFFFFF;
                for (long index = 0; index < contextCount; index++)
                {
                    if (ResolveObjectId(Bytes.U32(blob, offset + (index * 4)), gidTable) is { } context)
                    {
                        contextIds.Add(context);
                    }
                }

                offset += contextCount * 4L;
            }
        }

        var state = new State(objectIds, objectSpaceIds, contextIds);
        return ParsePropertySet(blob, offset, state, out _);
    }

    /// <summary>
    /// Parses the ObjectSpaceObjectPropSet of an object carried in an FSSHTTPB package. The stream headers
    /// are skipped: the references come from the object data's own arrays, matched to the properties that
    /// ask for them by position (MS-ONESTORE 2.7.6, 2.7.8).
    /// </summary>
    public static PropertySet ParseObjectBlobWithReferences(
        ReadOnlySpan<byte> blob,
        List<ExtendedGuid> objectReferences,
        List<ExtendedGuid> objectSpaceReferences,
        List<ExtendedGuid> contextReferences)
    {
        long offset = 0;
        var header = Bytes.U32(blob, offset);
        offset += 4;
        offset += (header & 0x00FFFFFF) * 4L;
        if (((header >> 31) & 1) == 0)
        {
            var osidHeader = Bytes.U32(blob, offset);
            offset += 4;
            offset += (osidHeader & 0x00FFFFFF) * 4L;
            if (((osidHeader >> 30) & 1) != 0)
            {
                var contextHeader = Bytes.U32(blob, offset);
                offset += 4;
                offset += (contextHeader & 0x00FFFFFF) * 4L;
            }
        }

        return ParsePropertySet(blob, offset, new State(objectReferences, objectSpaceReferences, contextReferences), out _);
    }

    /// <summary>Resolves a CompactID (2.2.2) through the global id table; null when the index is unknown.</summary>
    public static ExtendedGuid? ResolveObjectId(uint rawCompactId, Dictionary<uint, Guid> gidTable)
    {
        var guidIndex = rawCompactId >> 8;
        var n = rawCompactId & 0xFF;
        return gidTable.TryGetValue(guidIndex, out var guid) ? new ExtendedGuid(guid, n) : null;
    }

    /// <summary>Parses one PropertySet at <paramref name="start"/>. Never throws on short data.</summary>
    public static PropertySet ParsePropertySet(ReadOnlySpan<byte> data, long start, State state, out long consumed, int depth = 0)
    {
        var result = new PropertySet();
        consumed = 0;
        if (start < 0 || start + 2 > data.Length)
        {
            return result;
        }

        var offset = start;
        long propertyCount = Bytes.U16(data, offset);
        offset += 2;

        // Clamp to the property ids actually present so a damaged count cannot read past the end.
        var availableIds = Math.Max((data.Length - offset) / 4, 0);
        propertyCount = Math.Min(propertyCount, availableIds);
        var propertyIds = new uint[propertyCount];
        for (var index = 0; index < propertyCount; index++)
        {
            propertyIds[index] = Bytes.U32(data, offset + (index * 4L));
        }

        offset += propertyCount * 4;
        var dataOffset = offset;

        foreach (var rawPropertyId in propertyIds)
        {
            var propertyId = rawPropertyId & 0x03FFFFFF;
            var defaultType = (int)((rawPropertyId >> 26) & 0x1F);
            var propertyType = TypeOverrides.GetValueOrDefault(rawPropertyId, defaultType);
            var name = PropertyNames.For(propertyId);

            var snapshotOffset = dataOffset;
            var snapshotCursor = state.OidCursor;
            var snapshotOsid = state.OsidCursor;
            var snapshotContext = state.ContextCursor;
            if (!TryReadValue(data, ref dataOffset, rawPropertyId, propertyType, state, depth, out var value))
            {
                dataOffset = snapshotOffset;
                state.OidCursor = snapshotCursor;
                state.OsidCursor = snapshotOsid;
                state.ContextCursor = snapshotContext;
                if (propertyType == defaultType)
                {
                    break;
                }

                if (!TryReadValue(data, ref dataOffset, rawPropertyId, defaultType, state, depth, out value))
                {
                    dataOffset = snapshotOffset;
                    state.OidCursor = snapshotCursor;
                    state.OsidCursor = snapshotOsid;
                    state.ContextCursor = snapshotContext;
                    break;
                }
            }

            result.Add(name, value);
        }

        consumed = dataOffset - start;
        return result;
    }

    private static bool TryReadValue(ReadOnlySpan<byte> data, ref long offset, uint rawPropertyId, int type, State state, int depth, out PropertyValue value)
    {
        value = PropertyValue.None;
        var local = offset;
        switch (type)
        {
            case 0x1:
                // NoData: the property carries no value.
                value = PropertyValue.None;
                return true;
            case 0x2:
                value = PropertyValue.FromBool(((rawPropertyId >> 31) & 1) != 0);
                return true;
            case 0x3:
            case 0x4:
            case 0x5:
            case 0x6:
            case 0x7:
                if (!TryReadScalar(data, ref local, type, out value))
                {
                    return false;
                }

                break;
            case 0x8:
                value = state.OidCursor < state.ObjectIds.Count && !state.ObjectIds[(int)state.OidCursor].IsZero
                    ? PropertyValue.FromObjectId(state.ObjectIds[(int)state.OidCursor])
                    : PropertyValue.None;
                state.OidCursor++;
                break;
            case 0x9:
            {
                if (!TryU32(data, local, out var count))
                {
                    return false;
                }

                local += 4;
                var items = new List<PropertyValue>();
                var first = state.OidCursor;
                var last = Math.Min(first + count, state.ObjectIds.Count);
                for (var index = first; index < last; index++)
                {
                    if (!state.ObjectIds[(int)index].IsZero)
                    {
                        items.Add(PropertyValue.FromObjectId(state.ObjectIds[(int)index]));
                    }
                }

                state.OidCursor += count;
                value = PropertyValue.FromList(items);
                break;
            }

            case 0x10:
            {
                if (!TryU32(data, local, out var count))
                {
                    return false;
                }

                local += 4;
                var items = new List<PropertyValue>();
                if (count > 0)
                {
                    if (!TryU32(data, local, out var arrayPropertyId))
                    {
                        return false;
                    }

                    local += 4;
                    var arrayType = TypeOverrides.GetValueOrDefault(arrayPropertyId, (int)((arrayPropertyId >> 26) & 0x1F));

                    // A well-formed array consumes at least one byte per element; bounding the element
                    // count by the remaining data keeps a damaged count from producing billions of items.
                    var limit = Math.Min(count, Math.Max(data.Length - local, 0) + 1);
                    if (arrayType == 0x11)
                    {
                        if (depth >= MaxNesting)
                        {
                            return false;
                        }

                        for (long index = 0; index < limit; index++)
                        {
                            var child = ParsePropertySet(data, local, state, out var childConsumed, depth + 1);
                            items.Add(PropertyValue.FromSet(child));
                            local += childConsumed;
                        }
                    }
                    else
                    {
                        for (long index = 0; index < limit; index++)
                        {
                            if (!TryReadScalar(data, ref local, arrayType, out var item))
                            {
                                return false;
                            }

                            items.Add(item);
                        }
                    }
                }

                value = PropertyValue.FromList(items);
                break;
            }

            case 0x11:
            {
                if (depth >= MaxNesting)
                {
                    return false;
                }

                var set = ParsePropertySet(data, local, state, out var setConsumed, depth + 1);
                local += setConsumed;
                value = PropertyValue.FromSet(set);
                break;
            }

            case 0xA:
                value = state.OsidCursor < state.ObjectSpaceIds.Count
                    ? PropertyValue.FromObjectSpaceId(state.ObjectSpaceIds[(int)state.OsidCursor])
                    : PropertyValue.None;
                state.OsidCursor++;
                break;
            case 0xB:
            {
                if (!TryU32(data, local, out var count))
                {
                    return false;
                }

                local += 4;
                var items = new List<PropertyValue>();
                var first = state.OsidCursor;
                var last = Math.Min(first + count, state.ObjectSpaceIds.Count);
                for (var index = first; index < last; index++)
                {
                    items.Add(PropertyValue.FromObjectSpaceId(state.ObjectSpaceIds[(int)index]));
                }

                state.OsidCursor += count;
                value = PropertyValue.FromList(items);
                break;
            }

            case 0xC:
                value = state.ContextCursor < state.ContextIds.Count
                    ? PropertyValue.FromContextId(state.ContextIds[(int)state.ContextCursor])
                    : PropertyValue.None;
                state.ContextCursor++;
                break;
            case 0xD:
            {
                if (!TryU32(data, local, out var count))
                {
                    return false;
                }

                local += 4;
                var items = new List<PropertyValue>();
                var first = state.ContextCursor;
                var last = Math.Min(first + count, state.ContextIds.Count);
                for (var index = first; index < last; index++)
                {
                    items.Add(PropertyValue.FromContextId(state.ContextIds[(int)index]));
                }

                state.ContextCursor += count;
                value = PropertyValue.FromList(items);
                break;
            }

            default:
                // Unknown types are not decoded, matching the reference reader: the value is absent and
                // nothing is consumed.
                value = PropertyValue.None;
                return true;
        }

        offset = local;
        return true;
    }

    private static bool TryReadScalar(ReadOnlySpan<byte> data, ref long offset, int type, out PropertyValue value)
    {
        value = PropertyValue.None;
        switch (type)
        {
            case 0x3:
                if (offset < 0 || offset >= data.Length)
                {
                    return false;
                }

                value = PropertyValue.FromInteger(data[(int)offset]);
                offset += 1;
                return true;
            case 0x4:
                if (offset < 0 || offset + 2 > data.Length)
                {
                    return false;
                }

                value = PropertyValue.FromInteger(Bytes.U16(data, offset));
                offset += 2;
                return true;
            case 0x5:
                if (!TryU32(data, offset, out var u32))
                {
                    return false;
                }

                value = PropertyValue.FromInteger(u32);
                offset += 4;
                return true;
            case 0x6:
                if (offset < 0 || offset + 8 > data.Length)
                {
                    return false;
                }

                value = PropertyValue.FromInteger(Bytes.U64(data, offset));
                offset += 8;
                return true;
            case 0x7:
            {
                if (!TryU32(data, offset, out var cb))
                {
                    return false;
                }

                offset += 4;
                var start = Math.Min(offset, data.Length);
                var end = Math.Min(offset + cb, data.Length);
                value = PropertyValue.FromBytes(data[(int)start..(int)end].ToArray());
                offset += cb;
                return true;
            }

            default:
                // Not a scalar type: no value and nothing consumed.
                return true;
        }
    }

    private static bool TryU32(ReadOnlySpan<byte> data, long offset, out uint value)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = Bytes.U32(data, offset);
        return true;
    }
}
