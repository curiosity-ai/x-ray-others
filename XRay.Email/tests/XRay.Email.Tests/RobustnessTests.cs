using XRay.Email.Cfb;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>
/// Damaged input must fail cleanly: an MSG that cannot be read raises <see cref="CfbException"/> or
/// <see cref="MsgException"/>, never another exception type (and never a stack overflow), and EML
/// parsing never throws at all. Seeded truncations and byte flips of every small corpus fixture.
/// </summary>
public sealed class RobustnessTests
{
    private const int MaxFixtureSize = 300 * 1024;

    public static TheoryData<string> Fixtures() => new(CorpusManifest.Entries.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Damaged_fixture_fails_cleanly(string key)
    {
        var path = TestDocuments.RequireFile(key);
        var original = File.ReadAllBytes(path);
        if (original.Length > MaxFixtureSize)
        {
            return;
        }

        var isEml = key.EndsWith(".eml", StringComparison.OrdinalIgnoreCase);
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("XRAY_EMAIL_MUTATIONS"), out var configured) ? configured : 12;
        var random = new Random(Seed(key));
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var data = original.ToArray();
            if (iteration % 3 == 0)
            {
                data = data[..random.Next(data.Length)];
            }
            else
            {
                var flips = 1 + random.Next(16);
                for (var flip = 0; flip < flips && data.Length > 0; flip++)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }
            }

            try
            {
                var message = isEml ? MapiMessage.LoadFromEml(data, TestContext.Current.CancellationToken) : MapiMessage.FromStream(new MemoryStream(data), TestContext.Current.CancellationToken);
                _ = message.HtmlBody;
                _ = message.RtfBody;
                _ = message.SenderSmtpAddress;
                _ = MapiMessage.LoadFromEml(message.SaveToEml(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
                _ = MapiMessage.FromStream(new MemoryStream(message.Save(TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);

                // the preview, body fallbacks and attachment adapters take the same hostile input
                _ = message.ToHtml(null, TestContext.Current.CancellationToken);
                _ = message.GetBestTextBody(TestContext.Current.CancellationToken);
                _ = message.GetBestHtmlBody(TestContext.Current.CancellationToken);
                foreach (var attachment in message.GetAttachmentsFlattened(new AttachmentFlattenOptions { MaxDepth = 3 }, TestContext.Current.CancellationToken))
                {
                    _ = attachment.GetSize(TestContext.Current.CancellationToken);
                    attachment.CopyTo(Stream.Null, TestContext.Current.CancellationToken);
                }

                if (!isEml)
                {
                    _ = MapiMessage.FromStream(new ForwardOnlyStream(new MemoryStream(data)), TestContext.Current.CancellationToken);
                }
            }
            catch (Exception exception) when (!isEml && exception is CfbException or MsgException)
            {
            }
            catch (Exception exception)
            {
                Assert.Fail($"{key}, iteration {iteration}: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
            }
        }
    }

    private static int Seed(string key)
    {
        // string.GetHashCode is randomised per process; a stable seed keeps failures reproducible.
        var hash = 17;
        foreach (var ch in key)
        {
            hash = unchecked((hash * 31) + ch);
        }

        return hash & 0x7FFFFFFF;
    }
}
