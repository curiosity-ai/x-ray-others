using XRay.OneNote.Internal.MsOne;
using XRay.OneNote.Internal.OneStore;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Table column widths: TableColumnWidths and TableColumnsLocked carry a one-byte count prefix.</summary>
public sealed class TableColumnTests
{
    [Fact]
    public void Widths_skip_the_count_byte()
    {
        var bytes = new byte[1 + 8];
        bytes[0] = 2;
        BitConverter.GetBytes(1.5f).CopyTo(bytes, 1);
        BitConverter.GetBytes(4.25f).CopyTo(bytes, 5);
        Assert.Equal([1.5, 4.25], OneNoteLoader.DecodeColumnWidths(PropertyValue.FromBytes(bytes)));
    }

    [Fact]
    public void A_count_beyond_the_data_is_clamped()
    {
        var bytes = new byte[1 + 4];
        bytes[0] = 9;
        BitConverter.GetBytes(2f).CopyTo(bytes, 1);
        Assert.Equal([2.0], OneNoteLoader.DecodeColumnWidths(PropertyValue.FromBytes(bytes)));
        Assert.Empty(OneNoteLoader.DecodeColumnWidths(PropertyValue.None));
        Assert.Empty(OneNoteLoader.DecodeColumnWidths(PropertyValue.FromBytes([])));
    }

    [Fact]
    public void Locks_are_one_bit_per_column()
    {
        var locks = OneNoteLoader.DecodeColumnLocks(PropertyValue.FromBytes([10, 0b0000_0101, 0b0000_0010]), 10);
        Assert.Equal([true, false, true, false, false, false, false, false, false, true], locks);
        Assert.Equal([false, false], OneNoteLoader.DecodeColumnLocks(PropertyValue.None, 2));
    }

    [Fact]
    public void Corpus_tables_have_real_widths()
    {
        var simple = Fixture.Load("SimpleTable.one").GetChildNodes<Table>().Single();
        Assert.Equal(3, simple.Columns.Count);
        Assert.All(simple.Columns, column => Assert.Equal(1.0308334, column.Width!.Value, 5));

        var tagged = Fixture.Load("TableWithTag.one").GetChildNodes<Table>().Single();
        Assert.Equal([1.0308334, 3.9499803, 1.0308334], tagged.Columns.Select(column => column.Width!.Value).ToArray(), new ApproximateComparer());
        Assert.All(tagged.Columns, column => Assert.False(column.LockedWidth));
    }

    private sealed class ApproximateComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-5;

        public int GetHashCode(double obj) => 0;
    }
}
