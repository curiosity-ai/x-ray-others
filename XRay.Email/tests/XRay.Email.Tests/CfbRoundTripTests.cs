using System.Text;
using XRay.Email.Cfb;
using Xunit;

namespace XRay.Email.Tests;

public sealed class CfbRoundTripTests
{
    [Fact]
    public void DirectoryEntryNameOrderingMatchesSpecRules()
    {
        Assert.True(DirectoryEntryNameComparer.Compare("B", "AA") < 0);
        Assert.Equal(0, DirectoryEntryNameComparer.Compare("ab", "AB"));
        Assert.True(DirectoryEntryNameComparer.Compare("ab", "AC") < 0);
    }

    [Fact]
    public void WriterRoundTripsManualDocument()
    {
        var document = new CfbDocument();
        document.Root.AddStream(new CfbStream("A", Encoding.ASCII.GetBytes("hello")));
        var storage = document.Root.AddStorage(new CfbStorage("Storage"));
        storage.AddStream(new CfbStream("BigStream", Encoding.ASCII.GetBytes(new string('x', 6000))));
        storage.AddStream(new CfbStream("Zed", Encoding.ASCII.GetBytes("payload")));

        var payload = CfbWriter.ToBytes(document);

        using var reader = new CfbReader(payload);
        Assert.Equal(3, reader.MajorVersion);
        Assert.Equal(new[] { "A", "Storage" }, reader.IterChildren(CfbConstants.RootStreamId).Select(item => item.Name).ToArray());

        var aStream = reader.ResolvePath(["A"]);
        Assert.NotNull(aStream);
        Assert.Equal("hello", Encoding.ASCII.GetString(reader.GetStreamData(aStream!.StreamId)));

        var bigStream = reader.ResolvePath(["Storage", "BigStream"]);
        Assert.NotNull(bigStream);
        Assert.Equal(6000ul, bigStream!.StreamSize);
    }

    [Fact]
    public void WriterRejectsDuplicateSiblingNamesUnderCfbOrdering()
    {
        var document = new CfbDocument();
        document.Root.AddStream(new CfbStream("ab", [1]));
        document.Root.AddStream(new CfbStream("AB", [2]));

        var error = Assert.Throws<CfbException>(() => CfbWriter.ToBytes(document));
        Assert.Contains("Duplicate sibling name", error.Message);
    }
}
