using XRay.Email.Msg;
using Xunit;

namespace XRay.Email.Tests;

public sealed class MsgReaderTests
{
    [Fact]
    public void ParseTopLevelPropertyStreamRejectsShortHeader()
    {
        Assert.Throws<MsgException>(() => MsgReader.ParseTopLevelPropertyStream(new byte[23]));
    }

    [Fact]
    public void ParseSubobjectPropertyStreamRejectsShortHeader()
    {
        Assert.Throws<MsgException>(() => MsgReader.ParseSubobjectPropertyStreamData(new byte[7]));
    }

    [Fact]
    public void ParseTopLevelPropertyStreamRejectsMisalignedPayload()
    {
        var payload = new byte[29];
        Assert.Throws<MsgException>(() => MsgReader.ParseTopLevelPropertyStream(payload));
    }

    [Fact]
    public void ParseSubobjectPropertyStreamRejectsMisalignedPayload()
    {
        var payload = new byte[11];
        Assert.Throws<MsgException>(() => MsgReader.ParseSubobjectPropertyStreamData(payload));
    }

    [Fact]
    public void ParseTopLevelPropertyStreamDecodesHeaderAndEntry()
    {
        var payload = new byte[48];
        BitConverter.GetBytes((uint)1).CopyTo(payload, 8);
        BitConverter.GetBytes((uint)2).CopyTo(payload, 12);
        BitConverter.GetBytes((uint)3).CopyTo(payload, 16);
        BitConverter.GetBytes((uint)4).CopyTo(payload, 20);
        BitConverter.GetBytes(0x0037001Fu).CopyTo(payload, 32);
        BitConverter.GetBytes(6u).CopyTo(payload, 36);
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.CopyTo(payload, 40);

        var (header, entries) = MsgReader.ParseTopLevelPropertyStream(payload);

        Assert.Equal(1u, header.NextRecipientId);
        Assert.Equal(2u, header.NextAttachmentId);
        Assert.Equal(3u, header.RecipientCount);
        Assert.Equal(4u, header.AttachmentCount);
        Assert.Single(entries);
        Assert.Equal(0x0037001Fu, entries[0].PropertyTag);
        Assert.Equal(6u, entries[0].Flags);
        Assert.Equal(8, entries[0].Value.Length);
    }
}
