using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>The FSSHTTPB reader against packages written by <see cref="SyntheticPackage"/>, with no corpus.</summary>
public sealed class SyntheticPackageTests
{
    private static readonly Guid FileGuid = new("0a1b2c3d-1111-2222-3333-444455556666");

    private static byte[] Sample() => SyntheticPackage.Build(
        "Notes.one",
        FileGuid,
        [
            new SyntheticPackage.PageSpec("First page", "alpha", "beta"),
            new SyntheticPackage.PageSpec("Second page", "gamma"),
            new SyntheticPackage.PageSpec("Third page"),
        ]);

    [Fact]
    public void A_package_loads_with_its_pages_in_series_order()
    {
        var document = new Document(new MemoryStream(Sample()));
        Assert.Equal(FileGuid, document.Guid);
        Assert.Equal("Notes", document.DisplayName);
        var pages = document.Pages.ToList();
        Assert.Equal(["First page", "Second page", "Third page"], pages.Select(page => page.Title!.TitleText!.Text).ToArray());
        Assert.Equal(["alpha", "beta"], pages[0].GetChildNodes<RichText>().Select(rt => rt.Text).Where(t => t != "First page").ToArray());
        Assert.Equal(["gamma"], pages[1].GetChildNodes<RichText>().Select(rt => rt.Text).Where(t => t != "Second page").ToArray());
    }

    [Fact]
    public void A_package_exports_like_any_document()
    {
        var document = new Document(new MemoryStream(Sample()));
        using var html = new MemoryStream();
        document.Save(html, new Saving.HtmlSaveOptions { PerPage = false }, TestContext.Current.CancellationToken);
        var text = System.Text.Encoding.UTF8.GetString(html.ToArray());
        Assert.Contains("First page", text, StringComparison.Ordinal);
        Assert.Contains("gamma", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_is_not_a_table_of_contents()
    {
        Assert.Throws<UnsupportedFileFormatException>(() => NotebookToc.Load(new MemoryStream(Sample()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_prefix_loads_or_fails_with_the_librarys_exceptions()
    {
        var data = Sample();
        var failures = new List<string>();
        for (var length = 0; length <= data.Length; length++)
        {
            if (RobustnessTests.Exercise(data[..length]) is { } exception)
            {
                failures.Add($"prefix {length}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
    }

    [Fact]
    public void Seeded_mutations_load_or_fail_with_the_librarys_exceptions()
    {
        var original = Sample();
        var failures = new List<string>();
        for (var seed = 0; seed < 600; seed++)
        {
            var random = new Random(seed);
            var data = (byte[])original.Clone();
            for (var edit = random.Next(1, 10); edit > 0; edit--)
            {
                var at = random.Next(68, data.Length);
                if (random.Next(2) == 0)
                {
                    data[at] = (byte)random.Next(256);
                }
                else
                {
                    data[at] ^= (byte)(1 << random.Next(8));
                }
            }

            if (RobustnessTests.Exercise(data) is { } exception)
            {
                failures.Add($"seed {seed}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
    }

    [Fact]
    public void A_pre_cancelled_token_stops_the_package_reader()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => Document.Load(new MemoryStream(Sample()), null, source.Token));
    }
}
