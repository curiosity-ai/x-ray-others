using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Formatting runs and styles (ported from the Python rich text formatting tests).</summary>
public sealed class RichTextFormattingTests
{
    [Fact]
    public void Rich_text_runs_are_preserved() =>
        Assert.Contains(Fixture.Load("FormattedRichText.one").GetChildNodes<RichText>(), rt => rt.TextRuns.Count > 0);

    [Fact]
    public void Some_run_carries_non_default_formatting()
    {
        static bool IsStyled(TextStyle s) => s.IsBold || s.IsItalic || s.IsUnderline || s.IsStrikethrough || s.IsSuperscript
            || s.IsSubscript || s.FontName is not null || s.FontSize is not null || s.FontColor is not null || s.Highlight is not null
            || s.HyperlinkAddress is not null;
        Assert.Contains(Fixture.Load("FormattedRichText.one").GetChildNodes<RichText>(), rt => rt.TextRuns.Any(run => IsStyled(run.Style)));
    }

    [Fact]
    public void Text_style_properties_round_trip()
    {
        var style = TextStyle.Default;
        Assert.Equal(1033, style.Language);
        style.IsBold = style.IsItalic = style.IsUnderline = style.IsStrikethrough = true;
        style.IsSuperscript = style.IsSubscript = true;
        style.Highlight = 123;
        style.Language = 1031;
        Assert.Equal(123u, style.Highlight);
        Assert.Equal(1031, style.Language);
        Assert.Equal(15, style.FontStyle);
        Assert.NotSame(TextStyle.Default, TextStyle.Default);
        Assert.Equal(20.0, TextStyle.DefaultMsOneNoteTitleTextStyle.FontSize);
        Assert.True(TextStyle.DefaultMsOneNoteTitleTextStyle.IsBold);
        Assert.Equal(11.0, TextStyle.DefaultMsOneNoteTitleDateStyle.FontSize);
    }

    [Fact]
    public void Alignment_lives_on_rich_text()
    {
        var richText = new RichText("Aligned", paragraphStyle: new ParagraphStyle(), alignment: HorizontalAlignment.Center);
        Assert.Equal(HorizontalAlignment.Center, richText.Alignment);
        Assert.Null(typeof(ParagraphStyle).GetProperty("Alignment"));
    }

    [Fact]
    public void Paragraph_style_exposes_default_text_properties()
    {
        var style = ParagraphStyle.Default;
        style.FontName = "Courier New";
        style.FontSize = 20.0;
        style.FontColor = 0x112233;
        style.Highlight = 0xAABBCC;
        style.IsBold = style.IsItalic = style.IsUnderline = style.IsStrikethrough = style.IsSuperscript = style.IsSubscript = true;

        Assert.Equal("Courier New", style.FontName);
        Assert.Equal(20.0, style.FontSize);
        Assert.Equal(0x112233u, style.FontColor);
        Assert.Equal(0xAABBCCu, style.Highlight);
        Assert.Equal(15, style.FontStyle);
        Assert.Null(typeof(ParagraphStyle).GetProperty("Language"));
    }

    [Fact]
    public void Paragraph_style_is_the_default_run_style()
    {
        var richText = new RichText("Paragraph defaults", paragraphStyle: new ParagraphStyle { FontName = "Courier New", FontSize = 20.0, IsBold = true, Highlight = 0xFFFF00 });
        var run = Assert.Single(richText.TextRuns);
        Assert.Equal("Courier New", run.Style.FontName);
        Assert.Equal(20.0, run.Style.FontSize);
        Assert.True(run.Style.IsBold);
        Assert.Equal(0xFFFF00u, run.Style.Highlight);
    }

    [Fact]
    public void Append_without_a_style_uses_the_paragraph_style()
    {
        var richText = new RichText(paragraphStyle: new ParagraphStyle { FontName = "Courier New", FontSize = 18.0, IsItalic = true });
        richText.Append("First");
        richText.Append("Second");
        Assert.Equal(["First", "Second"], richText.TextRuns.Select(run => run.Text));
        Assert.All(richText.TextRuns, run =>
        {
            Assert.Equal("Courier New", run.Style.FontName);
            Assert.Equal(18.0, run.Style.FontSize);
            Assert.True(run.Style.IsItalic);
        });
    }

    [Fact]
    public void Collections_are_read_only_properties_with_live_contents()
    {
        var richText = new RichText(textRuns: [new TextRun("segment")], tags: [NoteTag.CreateYellowStar("Важно")]);
        Assert.Null(typeof(RichText).GetProperty(nameof(RichText.TextRuns))!.GetSetMethod());
        Assert.Null(typeof(RichText).GetProperty(nameof(RichText.Tags))!.GetSetMethod());

        richText.TextRuns.Add(new TextRun(" tail"));
        richText.Tags.Add(NoteTag.CreateQuestionMark("Вопрос"));
        Assert.Equal("segment tail", richText.Text);
        Assert.Equal(["Важно", "Вопрос"], richText.Tags.Select(tag => tag.Label));
    }

    [Fact]
    public void Text_run_has_no_boundaries()
    {
        Assert.Null(typeof(TextRun).GetProperty("Start"));
        Assert.Null(typeof(TextRun).GetProperty("End"));
    }

    [Fact]
    public void Formatted_run_boundaries_align_with_visible_text()
    {
        var target = Fixture.Load("FormattedRichText.one").GetChildNodes<RichText>()
            .First(rt => rt.Text.Contains("hyperlink. This text is not a hyperlink.", StringComparison.Ordinal));
        var visible = target.TextRuns.Where(run => run.Text.Length > 0 && !run.Text.Contains("HYPERLINK", StringComparison.Ordinal)).ToList();
        var texts = visible.Select(run => run.Text).ToList();
        foreach (var expected in (string[])["This", "text", "is ", "not", "hyperlink"])
        {
            Assert.Contains(expected, texts);
        }

        var byText = new Dictionary<string, TextStyle>();
        foreach (var run in visible)
        {
            byText[run.Text] = run.Style;
        }

        Assert.Equal(65535u, byText["This"].Highlight);
        Assert.True(byText["text"].IsBold);
        Assert.Equal(16776960u, byText["is "].Highlight);
        Assert.True(byText["not"].IsItalic);
        Assert.True(byText["hyperlink"].IsUnderline);
    }

    [Fact]
    public void Hyperlink_runs_carry_their_field_code()
    {
        var runs = Fixture.Load("FormattedRichText.one").GetChildNodes<RichText>().SelectMany(rt => rt.TextRuns).ToList();
        Assert.Contains(runs, run => run.Style.IsHyperlink && run.Text.Contains("﷟HYPERLINK \"www.google.com\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Editing_operations_follow_the_reference_semantics()
    {
        var richText = new RichText("  Hello world  ");
        Assert.Equal(2, richText.IndexOf("Hello"));
        Assert.Equal(-1, richText.IndexOf("Hello", 3));
        Assert.Equal(8, richText.IndexOf("WORLD", comparisonType: StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Hello world", richText.Trim().Text);
        richText.Insert(5, ",");
        Assert.Equal("Hello, world", richText.Text);
        richText.Remove(5, 1);
        Assert.Equal("Hello world", richText.Text);
        richText.AppendFront(">");
        Assert.Equal(">Hello world", richText.Text);
        Assert.Throws<ArgumentException>(() => richText.Replace(string.Empty, "x"));
        Assert.Empty(richText.Clear().TextRuns);
        Assert.Equal(string.Empty, richText.Text);
        Assert.Equal("\u001Cx", new RichText("\u001Cx\u001F").TrimEnd().Text);
    }
}
