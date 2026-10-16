using System.Collections.Concurrent;
using XRay.OneNote.Saving;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>
/// Damaged input never escapes as anything but a <see cref="OneNoteException"/>: every prefix of a
/// small fixture, and seeded random mutations of the small fixtures, either load or fail with the
/// library's own exception, and whatever loads also exports to PDF. Set XRAY_ONENOTE_MUTATIONS for a
/// deeper mutation run (default 150 per fixture).
/// </summary>
public sealed class RobustnessTests
{
    private static readonly string[] SmallFixtures =
    [
        "SimpleHistory.one", "Issue1Subpages.one", "SimpleTable.one", "FormattedRichText.one", "TagSizes.one", "NumberedListWithTags.one",
    ];

    private static readonly PdfSaveOptions Pdf = TestFonts.Options();

    /// <summary>Loads and exports; returns the unexpected exception, if any.</summary>
    private static Exception? Exercise(byte[] data)
    {
        Document document;
        try
        {
            document = new Document(new MemoryStream(data, writable: false), new LoadOptions { LoadHistory = true });
        }
        catch (OneNoteException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }

        try
        {
            _ = document.GetChildNodes<RichText>().Select(rt => rt.Text).ToList();
            foreach (var page in document.Pages)
            {
                _ = document.GetPageHistory(page).Count;
            }

            using var output = new MemoryStream();
            document.Save(output, Pdf);
            using var html = new MemoryStream();
            document.Save(html, new HtmlSaveOptions { PerPage = false });
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void AssertNoFailures(ConcurrentBag<string> failures) =>
        Assert.True(failures.IsEmpty, $"{failures.Count} input(s) raised unexpected exceptions:\n" + string.Join("\n", failures.Take(10)));

    [Fact]
    public void Every_prefix_of_a_small_fixture_loads_or_fails_cleanly()
    {
        var data = File.ReadAllBytes(Corpus.Require("SimpleHistory.one"));
        var failures = new ConcurrentBag<string>();
        Parallel.For(0, data.Length + 1, length =>
        {
            if (Exercise(data[..length]) is { } exception)
            {
                failures.Add($"prefix {length}: {exception.GetType().Name}: {exception.Message}");
            }
        });
        AssertNoFailures(failures);
    }

    [Fact]
    public void Prefixes_of_every_fixture_load_or_fail_cleanly()
    {
        // Resolve paths first: a skip thrown inside the parallel loop would surface as a failure.
        var paths = Corpus.Fixtures.ToDictionary(fixture => fixture, Corpus.Require);
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(Corpus.Fixtures, fixture =>
        {
            var data = File.ReadAllBytes(paths[fixture]);
            var step = Math.Max(1, data.Length / 400);
            for (var length = 0; length <= data.Length; length += step)
            {
                if (Exercise(data[..length]) is { } exception)
                {
                    failures.Add($"{fixture} prefix {length}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        });
        AssertNoFailures(failures);
    }

    [Fact]
    public void Seeded_mutations_load_or_fail_cleanly()
    {
        var mutations = int.TryParse(Environment.GetEnvironmentVariable("XRAY_ONENOTE_MUTATIONS"), out var configured) ? configured : 150;
        var paths = SmallFixtures.ToDictionary(fixture => fixture, Corpus.Require);
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(SmallFixtures, fixture =>
        {
            var original = File.ReadAllBytes(paths[fixture]);
            for (var seed = 0; seed < mutations; seed++)
            {
                var random = new Random(HashCode.Combine(fixture.Length, seed));
                var data = (byte[])original.Clone();
                var edits = random.Next(1, 24);
                for (var edit = 0; edit < edits; edit++)
                {
                    var at = random.Next(data.Length);
                    switch (random.Next(4))
                    {
                        case 0:
                            data[at] = (byte)random.Next(256);
                            break;
                        case 1:
                            data[at] ^= (byte)(1 << random.Next(8));
                            break;
                        case 2:
                            // Plausible-looking large values where counts, sizes and offsets live.
                            var value = random.Next(4) switch { 0 => 0xFFFFFFFFu, 1 => 0x7FFFFFFFu, 2 => (uint)data.Length, _ => (uint)random.Next() };
                            for (var i = 0; i < 4 && at + i < data.Length; i++)
                            {
                                data[at + i] = (byte)(value >> (8 * i));
                            }

                            break;
                        default:
                            var length = Math.Min(random.Next(1, 64), data.Length - at);
                            Array.Copy(original, random.Next(original.Length - length), data, at, length);
                            break;
                    }
                }

                if (Exercise(data) is { } exception)
                {
                    failures.Add($"{fixture} seed {seed}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        });
        AssertNoFailures(failures);
    }
}
