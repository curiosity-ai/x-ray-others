// Resource ceilings for the PostScript interpreter. Not part of the original: PostScript is a
// Turing-complete language, so a hostile job can loop forever, recurse without end or ask for
// gigabytes. The limits make those jobs fail with a PsResourceLimitError even when the caller
// passes no CancellationToken.

namespace XRay.PS.PostScript;

/// <summary>Ceilings that bound the work and memory a PostScript job may use.</summary>
public sealed class PsInterpreterLimits
{
    /// <summary>The most objects and procedure calls the interpreter may execute. A loop such
    /// as <c>{} loop</c> reaches it after a few seconds. Default 400 million; a long real job
    /// executes a few million.</summary>
    public long MaxOperators { get; set; } = 400_000_000;

    /// <summary>The deepest operand stack. Default 500,000 entries (the PostScript language
    /// level 3 limit is 65,535 but real-world generators are not always careful).</summary>
    public int MaxOperandStackDepth { get; set; } = 500_000;

    /// <summary>The deepest nesting of procedure calls. The default, 985, is the recursion limit
    /// of the original library, so the same programs fail the same way; going beyond a few
    /// thousand risks the CLR stack even on the interpreter's large-stack thread.</summary>
    public int MaxExecutionDepth { get; set; } = PsInterpreter.MaxDepth;

    /// <summary>The deepest nesting of <c>{ }</c> in the source. Default 10,000.</summary>
    public int MaxNestingDepth { get; set; } = 10_000;

    /// <summary>The ceiling on managed memory a job may use, in bytes. It bounds each single
    /// allocation the job asks for (a string, an array, a decoded image or filter output), and
    /// the growth of the managed heap since the job started, which is checked periodically.
    /// Default 1 GiB; null disables both. The heap check also sees allocations a host makes on
    /// other threads, so a host running many conversions at once may want a larger value.</summary>
    public long? MaxMemoryBytes { get; set; } = 1L << 30;

    /// <summary>How many recoverable errors <see cref="PdfSaveOptions.SuppressErrors"/> may
    /// absorb before interpretation stops. Default 1,000.</summary>
    public int MaxSuppressedErrors { get; set; } = 1_000;

    /// <summary>Limits with every ceiling at its default.</summary>
    public static PsInterpreterLimits Default => new();

    /// <summary>Limits that never trip (except the execution depth, which stays at the
    /// default so the CLR stack is safe).</summary>
    public static PsInterpreterLimits Unlimited => new()
    {
        MaxOperators = long.MaxValue,
        MaxOperandStackDepth = int.MaxValue,
        MaxNestingDepth = int.MaxValue,
        MaxMemoryBytes = null,
        MaxSuppressedErrors = int.MaxValue,
    };

    internal PsInterpreterLimits Clone() => (PsInterpreterLimits)MemberwiseClone();
}

/// <summary>Raised when a job exceeds a <see cref="PsInterpreterLimits"/> ceiling. It is a
/// <see cref="PsError"/> but <c>stopped</c> does not catch it, so a job cannot hide a runaway
/// loop behind it.</summary>
public sealed class PsResourceLimitError(string message) : PsError("limitcheck", message);
