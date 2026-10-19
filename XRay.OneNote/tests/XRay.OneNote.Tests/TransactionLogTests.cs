using System.Buffers.Binary;
using XRay.OneNote.Internal.OneStore;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Only the nodes the committed transactions put in a file node list are part of the file (MS-ONESTORE 2.3.3).</summary>
public sealed class TransactionLogTests
{
    [Theory]
    [InlineData("SimpleTable.one")]
    [InlineData("SimpleHistory.one")]
    [InlineData("Issue1Subpages.one")]
    public void The_log_of_a_real_file_names_the_node_count_of_its_lists(string fixture)
    {
        var data = File.ReadAllBytes(Corpus.Require(fixture));
        var counts = OneStoreParser.ReadCommittedCounts(data, TestContext.Current.CancellationToken);
        Assert.NotNull(counts);
        Assert.True(counts.ListCount > 3);
        Assert.True(counts.Total > counts.ListCount);
    }

    [Fact]
    public void Dropping_the_last_transaction_from_the_header_uncommits_its_nodes()
    {
        var data = File.ReadAllBytes(Corpus.Require("SimpleHistory.one"));
        var all = OneStoreParser.ReadCommittedCounts(data, TestContext.Current.CancellationToken)!;
        var transactions = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(96));
        Assert.True(transactions > 1);

        var earlier = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(earlier.AsSpan(96), transactions - 1);
        var fewer = OneStoreParser.ReadCommittedCounts(earlier, TestContext.Current.CancellationToken)!;
        Assert.True(fewer.Total < all.Total);

        // The earlier state still loads, with no more content than the full file.
        var full = new Document(new MemoryStream(data));
        var partial = new Document(new MemoryStream(earlier));
        Assert.True(partial.GetChildNodes<RichText>().Count <= full.GetChildNodes<RichText>().Count);
    }

    [Fact]
    public void A_log_that_cannot_be_read_does_not_cut_any_list()
    {
        var data = File.ReadAllBytes(Corpus.Require("SimpleHistory.one"));
        var broken = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(broken.AsSpan(160), (ulong)data.Length + 100);
        Assert.Null(OneStoreParser.ReadCommittedCounts(broken, TestContext.Current.CancellationToken));
        Assert.Equal(
            new Document(new MemoryStream(data)).GetChildNodes<RichText>().Count,
            new Document(new MemoryStream(broken)).GetChildNodes<RichText>().Count);
    }
}
