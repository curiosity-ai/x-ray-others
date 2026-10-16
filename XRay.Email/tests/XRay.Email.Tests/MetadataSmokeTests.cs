using XRay.Email.Cfb;
using XRay.Email.Msg;
using Xunit;

namespace XRay.Email.Tests;

public sealed class MetadataSmokeTests
{
    [Fact]
    public void CfbConstantsAreAvailable()
    {
        Assert.Equal(0xFFFEu, CfbConstants.ByteOrderLittleEndian);
        Assert.Equal(0xFFFFFFFFu, CfbConstants.NOSTREAM);
        Assert.Equal("Root Entry", CfbConstants.RootEntryName);
    }

    [Fact]
    public void MsgConstantsAreAvailable()
    {
        Assert.Equal("__properties_version1.0", MsgConstants.PropertyStreamName);
        Assert.Equal("__attach_version1.0_#", MsgConstants.AttachmentStoragePrefix);
    }

    [Fact]
    public void MsgEnumsAreAvailable()
    {
        Assert.Equal((ushort)2, (ushort)PropertyTypeCode.PtypInteger16);
        Assert.Equal((ushort)0x0037, (ushort)CommonMessagePropertyId.Subject);
    }
}
