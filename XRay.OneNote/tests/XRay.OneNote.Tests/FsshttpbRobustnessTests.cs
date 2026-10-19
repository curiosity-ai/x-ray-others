using System.Collections.Concurrent;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>
/// Damaged FSSHTTPB packages load or fail with the library's own exceptions, like damaged section files.
/// Uses the optional third-party samples (XRAY_ONENOTE_RS_SAMPLES), which are never copied into a repository;
/// XRAY_ONENOTE_MUTATIONS sets the number of mutations per file.
/// </summary>
public sealed class FsshttpbRobustnessTests
{
    private static readonly string[] Samples =
    [
        "non-legacy/New Section 3.one",
        "joplin/Notebook created on OneNote App/Section D/Section D1.one",
        "joplin/Simple notebook/Quick Notes.one",
    ];

    [Fact]
    public void Prefixes_of_packages_load_or_fail_cleanly()
    {
        var paths = Samples.Select(RustSamples.Require).ToList();
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(paths, path =>
        {
            var data = File.ReadAllBytes(path);
            var step = Math.Max(1, data.Length / 500);
            for (var length = 0; length <= data.Length; length += step)
            {
                if (RobustnessTests.Exercise(data[..length]) is { } exception)
                {
                    failures.Add($"{Path.GetFileName(path)} prefix {length}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public void Seeded_mutations_of_packages_load_or_fail_cleanly()
    {
        var mutations = int.TryParse(Environment.GetEnvironmentVariable("XRAY_ONENOTE_MUTATIONS"), out var configured) ? configured : 150;
        var paths = Samples.Select(RustSamples.Require).ToList();
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(paths, path =>
        {
            var original = File.ReadAllBytes(path);
            for (var seed = 0; seed < mutations; seed++)
            {
                var random = new Random(HashCode.Combine(path.Length, seed));
                var data = (byte[])original.Clone();
                for (var edit = random.Next(1, 24); edit > 0; edit--)
                {
                    var at = random.Next(data.Length);
                    switch (random.Next(3))
                    {
                        case 0:
                            data[at] = (byte)random.Next(256);
                            break;
                        case 1:
                            data[at] ^= (byte)(1 << random.Next(8));
                            break;
                        default:
                            var value = random.Next(3) switch { 0 => 0xFFFFFFFFu, 1 => 0x7FFFFFFFu, _ => (uint)random.Next() };
                            for (var i = 0; i < 4 && at + i < data.Length; i++)
                            {
                                data[at + i] = (byte)(value >> (8 * i));
                            }

                            break;
                    }
                }

                if (RobustnessTests.Exercise(data) is { } exception)
                {
                    failures.Add($"{Path.GetFileName(path)} seed {seed}: {exception.GetType().Name}: {exception.Message}");
                }
            }
        });
        Assert.True(failures.IsEmpty, $"{failures.Count} failures:\n" + string.Join("\n", failures.Take(10)));
    }
}
