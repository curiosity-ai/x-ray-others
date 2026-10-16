namespace XRay.OneNote.Tests.Support;

internal static class Fixture
{
    public static Document Load(string name, bool loadHistory = false) =>
        new(Corpus.Require(name), new LoadOptions { LoadHistory = loadHistory });
}
