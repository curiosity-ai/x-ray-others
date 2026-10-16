namespace XRay.Email.Msg;

internal sealed record PropertyEntryFixedLength(uint PropertyTag, uint Flags, byte[] Value)
{
    public bool IsMandatory() => (Flags & MsgConstants.PROPATTR_MANDATORY) != 0;

    public bool IsReadable() => (Flags & MsgConstants.PROPATTR_READABLE) != 0;

    public bool IsWritable() => (Flags & MsgConstants.PROPATTR_WRITABLE) != 0;
}
