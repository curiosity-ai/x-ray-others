using XRay.Email.Cfb;
using XRay.Email.Msg;
using static XRay.Email.Tests.CorpusManifest;

namespace XRay.Email.Tests;

/// <summary>
/// Every EML and MSG fixture in test_documents/email, checked against the manifest, then through
/// each conversion the library offers.
/// </summary>
public sealed class CorpusTests
{
    private static readonly string[] Folders = ["email", "email/msgreader", "email/mimekit", "email/xray-email", "email/xray-email/msg"];

    public static TheoryData<string> EmlFixtures() => new(Keys("eml"));

    public static TheoryData<string> MsgFixtures() => new(Keys("msg"));

    [Fact]
    public void Manifest_covers_every_fixture_in_the_corpus()
    {
        var root = TestDocuments.RequireRoot();
        var onDisk = Folders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar)))
                .Where(path => path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
                .Select(path => $"{folder}/{Path.GetFileName(path)}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Entries.Keys.Order(StringComparer.Ordinal), onDisk);
    }

    [Theory]
    [MemberData(nameof(EmlFixtures))]
    public void Eml_fixture_matches_manifest(string key) => AssertMatches(key, Fields.All);

    [Theory]
    [MemberData(nameof(MsgFixtures))]
    public void Msg_fixture_matches_manifest(string key) => AssertMatches(key, Fields.All);

    [Theory]
    [MemberData(nameof(EmlFixtures))]
    public void Eml_fixture_survives_eml_round_trip(string key)
    {
        if (Load(key, out var expected) is not { } message)
        {
            return;
        }

        var reloaded = MapiMessage.LoadFromEml(message.SaveToEml(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        AssertNoMismatch(key, "EML -> EML", Compare(reloaded, expected, Fields.All));
    }

    [Theory]
    [MemberData(nameof(EmlFixtures))]
    public void Eml_fixture_survives_conversion_to_msg(string key)
    {
        if (Load(key, out var expected) is not { } message)
        {
            return;
        }

        using var stream = new MemoryStream(message.Save(TestContext.Current.CancellationToken));
        var reloaded = MapiMessage.FromStream(stream, strict: true, TestContext.Current.CancellationToken);
        Assert.Empty(reloaded.ValidationIssues);
        AssertNoMismatch(key, "EML -> MSG", Compare(reloaded, expected, Fields.All));
    }

    [Theory]
    [MemberData(nameof(MsgFixtures))]
    public void Msg_fixture_survives_msg_round_trip(string key)
    {
        if (Load(key, out var expected) is not { } message)
        {
            return;
        }

        using var stream = new MemoryStream(message.Save(TestContext.Current.CancellationToken));
        var reloaded = MapiMessage.FromStream(stream, TestContext.Current.CancellationToken);
        AssertNoMismatch(key, "MSG -> MSG", Compare(reloaded, expected, Fields.All));
    }

    [Theory]
    [MemberData(nameof(MsgFixtures))]
    public void Msg_fixture_survives_conversion_to_eml(string key)
    {
        if (Load(key, out var expected) is not { } message)
        {
            return;
        }

        // An EML carries no MAPI-only state, so only what MIME can express is compared: the HTML
        // body (Outlook often keeps it only as RTF) and attachment types are left out.
        var reloaded = MapiMessage.LoadFromEml(message.SaveToEml(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        AssertNoMismatch(key, "MSG -> EML", Compare(reloaded, expected, Fields.Subject | Fields.From | Fields.Recipients | Fields.MessageId | Fields.Date | Fields.Body | Fields.Attachments));
    }

    private static void AssertMatches(string key, Fields fields)
    {
        var path = TestDocuments.RequireFile(key);
        var expected = Entries[key];
        if (expected.TryGetProperty("error", out var error))
        {
            var exception = Record.Exception(() => CorpusManifest.Load(path));
            Assert.NotNull(exception);
            Assert.Equal(error.GetString(), exception.GetType().Name);
            return;
        }

        var message = CorpusManifest.Load(path);
        AssertNoMismatch(key, "read", Compare(message, expected, fields));
        var expectIssues = expected.TryGetProperty("validationIssues", out var issues) && issues.GetBoolean();
        Assert.Equal(expectIssues, message.ValidationIssues.Count > 0);
    }

    private static MapiMessage? Load(string key, out System.Text.Json.JsonElement expected)
    {
        var path = TestDocuments.RequireFile(key);
        expected = Entries[key];
        return expected.TryGetProperty("error", out _) ? null : CorpusManifest.Load(path);
    }

    private static void AssertNoMismatch(string key, string stage, string mismatches)
    {
        Assert.True(mismatches.Length == 0, $"{key} ({stage}):\n{mismatches}");
    }
}
