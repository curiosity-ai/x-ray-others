using XRay.OneNote.Tests.Support;
using XRay.OneNote.Tools;

namespace XRay.OneNote.Tests;

/// <summary>
/// The C# document tree of every corpus fixture, dumped field for field, equals the stored golden (Goldens/dom: the dump
/// the Python reference produced, with the fields this package corrects re-derived from it), page history included.
/// </summary>
public sealed class DomParityTests
{
    public static TheoryData<string> Fixtures => Corpus.AllFixtures();

    /// <summary>
    /// The golden fields this package corrects: the Python reference left them wrong or empty, so the
    /// goldens take this package's value there (see "Deliberate differences" in CLAUDE.md). The rest of
    /// each golden is exactly what the Python reader produced. The paths only matter when re-deriving
    /// with XRAY_ONENOTE_UPDATE_GOLDENS=1, after tools/regenerate_goldens.py has rewritten the goldens.
    /// </summary>
    private static bool DeliberateDifference(string path) =>
        // The reference reads the one-byte count of TableColumnWidths as part of the first float.
        path.EndsWith(".columns", StringComparison.Ordinal) || path.Contains(".columns[", StringComparison.Ordinal)
        // The reference reports a tag's state from the wrong property set (every tag completed on
        // 1980-01-01); the state is the ActionItemStatus bits and times of the tag's own NoteTagState.
        || (path.Contains(".tags[", StringComparison.Ordinal)
            && (path.EndsWith(".status", StringComparison.Ordinal)
                || path.EndsWith(".creation_time", StringComparison.Ordinal)
                || path.EndsWith(".completed_time", StringComparison.Ordinal)))
        // The reference leaves a page's creation time empty when only the page metadata records it
        // (the topology time stamp); this package fills it.
        || path.EndsWith(".creation_time", StringComparison.Ordinal)
        // The reference leaves a hyperlink's address empty when the link is written as a field code in the text;
        // this package gives the address to the hyperlink run the field code governs.
        || path.EndsWith(".style.hyperlink_address", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Dom_matches_python_reference(string fixture)
    {
        var path = Corpus.Require(fixture);
        var document = new Document(path);
        var historyDocument = new Document(path, new LoadOptions { LoadHistory = true });

        var actual = DomJson.DumpDocument(fixture, document, historyDocument);
        var expected = Goldens.Load("dom", fixture);

        // Re-derive: the golden keeps what the Python reference produced everywhere except the fields
        // this package corrects (DeliberateDifference), which take this package's value. Review the diff.
        if (Environment.GetEnvironmentVariable("XRAY_ONENOTE_UPDATE_GOLDENS") == "1" && Goldens.SourceFolder("dom") is { } folder)
        {
            var merged = Goldens.TakeActualWhere(expected, actual, DeliberateDifference)!;
            File.WriteAllText(Path.Combine(folder, Path.GetFileNameWithoutExtension(fixture) + ".json"), merged.ToJsonString(GoldenJson.Options) + "\n");
            expected = merged;
        }

        var differences = Goldens.Diff(expected, actual);
        Assert.True(differences.Count == 0, $"{fixture}: {differences.Count} difference(s)\n" + string.Join("\n", differences));
    }
}
