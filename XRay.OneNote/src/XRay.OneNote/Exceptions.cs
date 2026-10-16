namespace XRay.OneNote;

/// <summary>Base type of every exception raised by this library.</summary>
public class OneNoteException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public OneNoteException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public OneNoteException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The input is not a well-formed OneNote file: it is truncated or its structures are damaged.</summary>
public class FileCorruptedException : OneNoteException
{
    /// <summary>Creates the exception with a message.</summary>
    public FileCorruptedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public FileCorruptedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The document tree does not have the shape an operation requires.</summary>
public class IncorrectDocumentStructureException : OneNoteException
{
    /// <summary>Creates the exception with a message.</summary>
    public IncorrectDocumentStructureException(string message)
        : base(message)
    {
    }
}

/// <summary>The document is password protected. Encrypted sections are not supported.</summary>
public class IncorrectPasswordException : OneNoteException
{
    /// <summary>Creates the exception with a message.</summary>
    public IncorrectPasswordException(string message)
        : base(message)
    {
    }
}

/// <summary>The input is not a OneNote section file this library can read.</summary>
public class UnsupportedFileFormatException : OneNoteException
{
    private static readonly Dictionary<string, FileFormat> FileFormatGuids = new(StringComparer.Ordinal)
    {
        ["7b5c52e4-d88c-4da7-aeb1-5378d02996d3"] = FileFormat.OneNote2010,
    };

    /// <summary>Creates the exception with a message and the detected format.</summary>
    public UnsupportedFileFormatException(string message = "Unsupported file format", FileFormat fileFormat = FileFormat.Unknown)
        : base(message)
    {
        FileFormat = fileFormat;
    }

    /// <summary>
    /// Creates the exception with a message and a format token: a <see cref="OneNote.FileFormat"/> name or
    /// a file type GUID. Unrecognized tokens map to <see cref="FileFormat.Unknown"/>.
    /// </summary>
    public UnsupportedFileFormatException(string message, string? fileFormat)
        : base(message)
    {
        FileFormat = Coerce(fileFormat);
    }

    /// <summary>The format the input was detected as, or <see cref="FileFormat.Unknown"/>.</summary>
    public FileFormat FileFormat { get; }

    private static FileFormat Coerce(string? value)
    {
        if (value is null)
        {
            return FileFormat.Unknown;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return FileFormat.Unknown;
        }

        foreach (var format in Enum.GetValues<FileFormat>())
        {
            if (string.Equals(format.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return FileFormatGuids.GetValueOrDefault(normalized.Trim('{', '}'), FileFormat.Unknown);
    }
}

/// <summary>The requested save format or save target is not supported.</summary>
public class UnsupportedSaveFormatException : OneNoteException
{
    /// <summary>Creates the exception with a message.</summary>
    public UnsupportedSaveFormatException(string message)
        : base(message)
    {
    }
}
