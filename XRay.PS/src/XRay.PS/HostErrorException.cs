namespace XRay.PS;

/// <summary>
/// An error of the host language rather than a PostScript error. The library this package was
/// ported from let Python built-in errors (division by zero, a failed conversion, an index out
/// of range) escape from operators; those are not caught by <c>stopped</c> and abort the job.
/// The port raises this exception at the same places so that the same inputs fail the same way.
/// </summary>
public sealed class HostErrorException(string kind, string message) : Exception($"{kind}: {message}")
{
    /// <summary>The kind of failure, named after the original error class (for example
    /// <c>ZeroDivisionError</c>).</summary>
    public string Kind { get; } = kind;
}
