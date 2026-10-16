using System.Security.Cryptography;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Images, tables and attachments in the corpus fixtures (ported from the Python DOM content tests).</summary>
public sealed class DomContentTests
{
    [Fact]
    public void Images_are_exposed_with_bytes()
    {
        var images = Fixture.Load("3ImagesWithDifferentAlignment.one").GetChildNodes<Image>();
        Assert.Equal(3, images.Count);
        Assert.All(images, image => Assert.True(image.Bytes.Length > 1024));
    }

    [Fact]
    public void Images_are_identical_by_hash()
    {
        var images = Fixture.Load("3ImagesWithDifferentAlignment.one").GetChildNodes<Image>();
        Assert.Single(images.Select(image => Convert.ToHexString(SHA256.HashData(image.Bytes))).Distinct());
    }

    [Fact]
    public void Images_preserve_outline_alignment()
    {
        var images = Fixture.Load("3ImagesWithDifferentAlignment.one").GetChildNodes<Image>();
        Assert.Equal([HorizontalAlignment.Right, HorizontalAlignment.Center, HorizontalAlignment.Left], images.Select(image => image.Alignment));
    }

    [Fact]
    public void Images_report_original_size_equal_to_size()
    {
        var image = Fixture.Load("3ImagesWithDifferentAlignment.one").GetChildNodes<Image>()[0];
        Assert.Equal(image.Width, image.OriginalWidth);
        Assert.Equal(image.Height, image.OriginalHeight);
        Assert.Equal("Безымянный рисунок.png", image.FileName);
    }

    [Fact]
    public void Image_metadata_is_init_only_but_tags_are_mutable()
    {
        var image = new Image { FileName = "image.png", FilePath = "C:/tmp/image.png", Format = "png", Bytes = "img"u8.ToArray(), Width = 10, Height = 20, OriginalWidth = 10, OriginalHeight = 20, Alignment = HorizontalAlignment.Center };
        image.Tags.Add(NoteTag.CreateYellowStar("Важно"));
        image.Tags.Add(NoteTag.CreateQuestionMark("Вопрос"));

        Assert.False(typeof(Image).GetProperty(nameof(Image.FileName))!.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Length == 0);
        Assert.Null(typeof(Image).GetProperty(nameof(Image.Tags))!.GetSetMethod());
        Assert.Equal(HorizontalAlignment.Center, image.Alignment);
        Assert.Equal(["Важно", "Вопрос"], image.Tags.Select(tag => tag.Label));
    }

    [Fact]
    public void Image_replace_copies_data_and_properties()
    {
        var target = new Image { FileName = "a.png", Bytes = [1] };
        var source = new Image { FileName = "b.png", Bytes = [2, 3], Width = 4, Alignment = HorizontalAlignment.Right };
        source.Tags.Add(NoteTag.CreateMusicalNote());

        target.Replace(source);

        Assert.Equal("b.png", target.FileName);
        Assert.Equal([2, 3], target.Bytes);
        Assert.Equal(4, target.Width);
        Assert.Equal(HorizontalAlignment.Right, target.Alignment);
        Assert.Single(target.Tags);
    }

    [Fact]
    public void Tables_are_exposed_with_rows_and_cells()
    {
        var document = Fixture.Load("SimpleTable.one");
        Assert.NotEmpty(document.GetChildNodes<Table>());
        var rows = document.GetChildNodes<TableRow>();
        Assert.NotEmpty(rows);
        Assert.NotEmpty(document.GetChildNodes<TableCell>());
        Assert.All(rows, row => Assert.NotEmpty(row));
    }

    [Fact]
    public void Simple_table_has_the_expected_grid()
    {
        var table = Fixture.Load("SimpleTable.one").GetChildNodes<Table>()[0];
        var grid = table.GetChildNodes<TableRow>()
            .Select(row => row.GetChildNodes<TableCell>().Select(cell => string.Concat(cell.GetChildNodes<RichText>().Select(rt => rt.Text))).ToArray())
            .ToArray();
        Assert.Equal([["1", "2", "3"], ["6", "5", "4"], ["7", "8", "9"], ["b", "a", "0"]], grid);
        Assert.Equal(3, table.Columns.Count);
        Assert.True(table.IsBordersVisible);
    }

    [Fact]
    public void Table_collections_are_read_only_properties_with_mutable_contents()
    {
        var table = new Table();
        table.Tags.Add(NoteTag.CreateYellowStar("Важно"));
        table.Columns.Add(new TableColumn { Width = 70 });
        table.Tags.Add(NoteTag.CreateQuestionMark("Вопрос"));
        table.Columns.Add(new TableColumn { Width = 120 });

        Assert.Null(typeof(Table).GetProperty(nameof(Table.Tags))!.GetSetMethod());
        Assert.Null(typeof(Table).GetProperty(nameof(Table.Columns))!.GetSetMethod());
        Assert.Equal(["Важно", "Вопрос"], table.Tags.Select(tag => tag.Label));
        Assert.Equal([70.0, 120.0], table.Columns.Select(column => column.Width!.Value));
    }

    [Theory]
    [InlineData("OnePageWithFile.one")]
    [InlineData("AttachedFileWithTag.one")]
    public void Attached_files_are_exposed_with_bytes(string fixture)
    {
        var files = Fixture.Load(fixture).GetChildNodes<AttachedFile>();
        var file = Assert.Single(files);
        Assert.Equal("TestOneNoteSaveAsTiffByFormat.tiff", file.FileName);
        Assert.Equal(474222, file.Bytes.Length);
        Assert.Equal("II*\0"u8.ToArray(), file.Bytes[..4]);
    }

    [Fact]
    public void Attached_file_metadata_is_read_only_but_tags_are_mutable()
    {
        var file = new AttachedFile { FileName = "doc.bin", Bytes = "abc"u8.ToArray() };
        file.Tags.Add(NoteTag.CreateYellowStar("Важно"));
        file.Tags.Add(NoteTag.CreateQuestionMark("Вопрос"));

        Assert.Null(typeof(AttachedFile).GetProperty(nameof(AttachedFile.Tags))!.GetSetMethod());
        Assert.Equal(["Важно", "Вопрос"], file.Tags.Select(tag => tag.Label));
    }
}
