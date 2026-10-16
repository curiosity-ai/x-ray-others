using System.Reflection;
using XRay.OneNote.Saving;

namespace XRay.OneNote.Tests;

/// <summary>The shape of the public API: abstract bases, read-only members, exceptions (ported from the Python stub tests).</summary>
public sealed class ApiSurfaceTests
{
    private sealed class EmptyVisitor : DocumentVisitor
    {
    }

    [Fact]
    public void Abstract_bases_cannot_be_instantiated_directly()
    {
        Assert.True(typeof(Node).IsAbstract);
        Assert.True(typeof(CompositeNode).IsAbstract);
        Assert.True(typeof(DocumentVisitor).IsAbstract);
        Assert.True(typeof(SaveOptions).IsAbstract);
        Assert.IsType<EmptyVisitor>(new EmptyVisitor());
        Assert.IsType<Document>(new Document());
        Assert.IsType<ParagraphStyle>(new ParagraphStyle());
    }

    [Fact]
    public void Note_tag_has_no_public_constructor() =>
        Assert.Empty(typeof(NoteTag).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

    [Fact]
    public void Password_protected_load_is_rejected()
    {
        var path = Support.Corpus.Require("FormattedRichText.one");
        Assert.Throws<IncorrectPasswordException>(() => new Document(path, new LoadOptions { DocumentPassword = "pass" }));
    }

    [Fact]
    public void Document_children_are_pages()
    {
        var document = new Document();
        var page = document.AppendChildLast(new Page());
        Assert.Single(document);
        Assert.Same(document, page.ParentNode);
        Assert.Null(typeof(Node).GetProperty(nameof(Node.ParentNode))!.GetSetMethod());
    }

    [Fact]
    public void Note_tag_status_and_completion_are_read_only()
    {
        var tag = NoteTag.CreateQuestionMark("Question");
        Assert.Equal(TagStatus.Open, tag.Status);
        Assert.Null(tag.CompletedTime);
        Assert.Equal(15, tag.Icon);
        Assert.Null(typeof(NoteTag).GetProperty(nameof(NoteTag.Status))!.GetSetMethod());
        Assert.Null(typeof(NoteTag).GetProperty(nameof(NoteTag.CompletedTime))!.GetSetMethod());
    }

    [Fact]
    public void Note_tag_factories_have_default_labels()
    {
        Assert.Equal(("Yellow Star", 13), (NoteTag.CreateYellowStar().Label, NoteTag.CreateYellowStar().Icon!.Value));
        Assert.Equal(("Question Mark", 15), (NoteTag.CreateQuestionMark("").Label, NoteTag.CreateQuestionMark().Icon!.Value));
        Assert.Equal(("Musical Note", 121), (NoteTag.CreateMusicalNote(null).Label, NoteTag.CreateMusicalNote().Icon!.Value));
    }

    [Fact]
    public void Note_tag_creation_time_is_settable()
    {
        var created = new DateTime(2024, 5, 6, 7, 8, 0, DateTimeKind.Utc);
        var tag = NoteTag.CreateYellowStar("Created");
        tag.CreationTime = created;
        Assert.Equal(created, tag.CreationTime);
        Assert.Equal(TagStatus.Open, tag.Status);

        var updated = new DateTime(2024, 5, 7, 8, 9, 0, DateTimeKind.Utc);
        tag.CreationTime = updated;
        Assert.Equal(updated, tag.CreationTime);
    }

    [Fact]
    public void Title_is_not_a_composite_node()
    {
        var title = new Title(new RichText("Title"));
        Assert.Same(title.TitleText, title.First());
        Assert.False(typeof(CompositeNode).IsAssignableFrom(typeof(Title)));
        foreach (var member in (string[])["FirstChild", "LastChild", "AppendChildFirst", "AppendChildLast", "InsertChild", "RemoveChild"])
        {
            Assert.Empty(typeof(Title).GetMember(member));
        }
    }

    [Fact]
    public void Title_parts_are_reparented_when_replaced()
    {
        var first = new RichText("one");
        var title = new Title(first);
        Assert.Same(title, first.ParentNode);
        var second = new RichText("two");
        title.TitleText = second;
        Assert.Null(first.ParentNode);
        Assert.Same(title, second.ParentNode);
    }

    [Fact]
    public void Unsupported_file_format_exception_maps_unknown_tokens_to_unknown()
    {
        Assert.Equal(FileFormat.Unknown, new UnsupportedFileFormatException("x", "bogus-guid").FileFormat);
        Assert.Equal(FileFormat.OneNote2010, new UnsupportedFileFormatException("x", "{7B5C52E4-D88C-4DA7-AEB1-5378D02996D3}").FileFormat);
        Assert.Equal(FileFormat.OneNote2007, new UnsupportedFileFormatException("x", "onenote2007").FileFormat);
        Assert.Null(typeof(UnsupportedFileFormatException).GetProperty(nameof(UnsupportedFileFormatException.FileFormat))!.GetSetMethod());
        Assert.IsAssignableFrom<OneNoteException>(new UnsupportedFileFormatException());
    }

    [Fact]
    public void Page_history_takes_only_the_current_page()
    {
        var current = new Page();
        var history = new PageHistory(current);
        Assert.Same(current, history.Current);
        Assert.Empty(history);
        Assert.Single(typeof(PageHistory).GetConstructors());
        Assert.Single(typeof(PageHistory).GetConstructors()[0].GetParameters());
    }

    [Fact]
    public void Page_history_behaves_as_a_list()
    {
        var history = new PageHistory(new Page());
        Page a = new(), b = new(), c = new();
        history.AddRange([a, b]);
        history.Insert(0, c);
        Assert.Equal([c, a, b], history);
        Assert.Equal(1, history.IndexOf(a));
        Assert.True(history.Remove(a));
        Assert.False(history.Remove(a));
        history.RemoveRange(0, 1);
        Assert.Equal([b], history);
        Assert.False(history.IsReadOnly);
    }

    [Fact]
    public void Save_format_has_pdf_and_html() => Assert.Equal([SaveFormat.Pdf, SaveFormat.Html], Enum.GetValues<SaveFormat>());

    [Fact]
    public void Outline_exposes_offsets_and_max_width()
    {
        var outline = Support.Fixture.Load("FormattedRichText.one").GetChildNodes<Outline>()[0];
        Assert.NotNull(outline.HorizontalOffset);
        Assert.NotNull(outline.VerticalOffset);
        Assert.NotNull(outline.MaxWidth);
        Assert.Null(typeof(Outline).GetProperty("X"));
        Assert.Null(typeof(Outline).GetProperty("Width"));
    }
}
