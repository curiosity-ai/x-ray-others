// Ambient cancellation and allocation guard. The conversion entry points publish their token
// for the duration of a call so that the decoders, parsers and rasterizer deep inside can poll
// it without every internal signature growing a parameter. State is per thread: the code that
// moves work to another thread (the interpreter's large-stack thread) republishes it there.

using XRay.PS.PostScript;

namespace XRay.PS.Internal;

internal static class Guard
{
    private sealed class State
    {
        public CancellationToken Token;
        public long MaxAllocation = long.MaxValue;
    }

    [ThreadStatic]
    private static State? t_state;

    /// <summary>The token published on this thread (default when none).</summary>
    public static CancellationToken Token => t_state?.Token ?? default;

    /// <summary>The allocation ceiling in force on this thread.</summary>
    public static long MaxAllocation => t_state?.MaxAllocation ?? long.MaxValue;

    /// <summary>Publishes a token (and optionally an allocation ceiling) until disposed. Nested
    /// scopes restore the outer one; an inner scope without a cancellable token keeps the
    /// outer token.</summary>
    public static Scope Begin(CancellationToken token, long? maxAllocation = null)
    {
        var previous = t_state;
        var next = new State
        {
            Token = token.CanBeCanceled || previous is null ? token : previous.Token,
            MaxAllocation = maxAllocation ?? previous?.MaxAllocation ?? long.MaxValue,
        };
        t_state = next;
        return new Scope(previous);
    }

    /// <summary>Throws <see cref="OperationCanceledException"/> when the published token is
    /// cancelled. Cheap enough for a per-row or per-record call.</summary>
    public static void Check() => t_state?.Token.ThrowIfCancellationRequested();

    /// <summary>Checks once every <c>mask + 1</c> calls (mask is a power of two minus one).</summary>
    public static void Every(ref int counter, int mask = 0xFF)
    {
        if ((++counter & mask) == 0)
            Check();
    }

    /// <summary>Throws <see cref="PsResourceLimitError"/> when a single allocation of
    /// <paramref name="bytes"/> exceeds the ceiling.</summary>
    public static void Charge(long bytes, string what = "allocation")
    {
        var state = t_state;
        if (state is not null && bytes > state.MaxAllocation)
            throw new PsResourceLimitError($"{what} of {bytes} bytes exceeds the memory limit of {state.MaxAllocation}");
    }

    internal readonly struct Scope(object? previous) : IDisposable
    {
        public void Dispose() => t_state = (State?)previous;
    }
}
