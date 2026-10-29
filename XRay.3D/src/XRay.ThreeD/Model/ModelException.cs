namespace XRay.ThreeD;

/// <summary>The only exception type that escapes a load: the file is damaged or in a form this library does not read.</summary>
public class ModelFormatException : Exception
{
    public ModelFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>The format is recognised but the library has no reader for it (or for this variant of it).</summary>
public sealed class UnsupportedModelFormatException : ModelFormatException
{
    public UnsupportedModelFormatException(FormatInfo format, string message)
        : base(message)
    {
        Format = format;
    }

    public FormatInfo Format { get; }
}
