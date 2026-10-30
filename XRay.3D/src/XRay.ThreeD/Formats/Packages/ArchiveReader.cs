using System.IO.Compression;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Packages;

/// <summary>
/// A ZIP package that is not itself a 3D format (an OBJ with its MTL and textures, a download holding several formats): the primary model is
/// the entry whose extension comes first in <see cref="Priority"/> (the largest among equals), loaded with the other entries as companions
/// (an OBJ finds its .mtl next to it, or anywhere in the package by file name). The result describes the model; the metadata names the entry
/// and lists the other models in the package.
/// </summary>
internal static class ArchiveReader
{
    private const int MaxDepth = 3;

    /// <summary>Model extensions, best first: scene formats before mesh formats before CAD, point clouds and toolpaths last.</summary>
    internal static readonly string[] Priority =
    [
        ".glb", ".gltf", ".3mf", ".usdz", ".usda", ".usd", ".fbx", ".dae", ".obj", ".ply", ".stl", ".step", ".stp", ".ifc", ".igs", ".iges",
        ".3ds", ".x3d", ".wrl", ".vrml", ".off", ".amf", ".sldasm", ".sldprt", ".dxf", ".fcstd", ".las", ".pcd", ".pts", ".xyz", ".gcode",
    ];

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        if (options.ArchiveDepth >= MaxDepth)
        {
            throw new ModelFormatException("ZIP archives are nested too deeply.");
        }

        using var zip = ZipEntries.Open(data);
        var files = zip.Entries
            .Where(e => e.Name.Length > 0 && !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal) && !e.Name.StartsWith("._", StringComparison.Ordinal))
            .ToList();
        var candidates = files
            .Select(e => (Entry: e, Rank: Array.IndexOf(Priority, Path.GetExtension(e.Name).ToLowerInvariant())))
            .Where(c => c.Rank >= 0)
            .OrderBy(c => c.Rank)
            .ThenByDescending(c => c.Entry.Length)
            .ThenBy(c => c.Entry.FullName, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new UnsupportedModelFormatException(
                ModelFormats.Archive,
                $"The ZIP archive ({files.Count} files) holds no 3D model this library can read.");
        }

        // A package whose best candidate fails to load falls through to the next one of a different format.
        var failures = new List<string>();
        foreach (var (entry, _) in candidates.Take(8))
        {
            options.Cancellation.ThrowIfCancellationRequested();
            try
            {
                var model = Load(entry, files, candidates.Select(c => c.Entry).ToList(), options);
                foreach (var failure in failures)
                {
                    model.Warnings.Add(failure);
                }

                return model;
            }
            catch (ModelFormatException ex) when (candidates.Count > 1)
            {
                failures.Add($"{entry.FullName}: {ex.Message}");
            }
        }

        throw new ModelFormatException("None of the models in the ZIP archive could be read. " + string.Join(" ", failures.Take(3)));
    }

    private static Model3D Load(ZipArchiveEntry entry, List<ZipArchiveEntry> files, List<ZipArchiveEntry> models, LoadOptions options)
    {
        var bytes = ZipEntries.Read(entry, options.MaxInputBytes);
        var inner = options.Clone();
        inner.ArchiveDepth = options.ArchiveDepth + 1;
        var directory = entry.FullName.Contains('/') ? entry.FullName[..entry.FullName.LastIndexOf('/')] : "";
        long budget = options.MaxInputBytes;
        inner.ResolveCompanion = name =>
        {
            var found = Find(files, directory, name);
            if (found is null || found.Length > budget)
            {
                return null;
            }

            budget -= found.Length;
            return ZipEntries.Read(found, options.MaxInputBytes);
        };

        var model = ModelLoader.Load(bytes, entry.Name, inner);
        var meta = model.Metadata;
        meta.Properties.Add(new ModelProperty("container", "zip", "archive"));
        meta.Properties.Add(new ModelProperty("entry", entry.FullName, "archive"));
        meta.Properties.Add(new ModelProperty("entries", files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "archive"));
        meta.Title ??= Path.GetFileNameWithoutExtension(entry.Name);
        foreach (var other in models.Where(m => m != entry).Take(1000))
        {
            meta.ComponentFiles.Add(other.FullName);
        }

        if (meta.ComponentFiles.Count > 0)
        {
            meta.Notes.Add($"The package holds {models.Count} models; {entry.FullName} was loaded.");
        }

        return model;
    }

    /// <summary>An entry by the name a model gives it: relative to the model's folder first, then by file name anywhere in the package.</summary>
    private static ZipArchiveEntry? Find(List<ZipArchiveEntry> files, string directory, string name)
    {
        var normalised = name.Replace('\\', '/').Trim();
        var parts = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
        foreach (var part in normalised.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part.Length > 0 && part != ".")
            {
                parts.Add(part);
            }
        }

        var full = string.Join('/', parts);
        var match = files.FirstOrDefault(f => string.Equals(f.FullName, full, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        var file = normalised[(normalised.LastIndexOf('/') + 1)..];
        return file.Length == 0 ? null : files.FirstOrDefault(f => string.Equals(f.Name, file, StringComparison.OrdinalIgnoreCase));
    }
}
