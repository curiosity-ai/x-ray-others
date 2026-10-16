namespace XRay.Psd;

/// <summary>
/// The cancellation token of the public call in progress, published for the code underneath
/// it. Load, render, encode and extract entry points take a <see cref="CancellationToken"/>;
/// instead of threading it through every internal signature they publish it here for the
/// duration of the call, and the parsers, decoders, compositor passes and
/// <see cref="Parallelism.For"/> poll it. Like <see cref="Parallelism"/>'s degree it lives in
/// an <see cref="AsyncLocal{T}"/>, so it flows into the worker threads of a parallel loop.
/// </summary>
internal static class Cancellation
{
    private static readonly AsyncLocal<CancellationToken> Current = new();

    /// <summary>The token in effect (default, which never cancels, outside a scope).</summary>
    public static CancellationToken Token => Current.Value;

    /// <summary>Whether a token that can be cancelled is in effect.</summary>
    public static bool IsActive => Current.Value.CanBeCanceled;

    /// <summary>
    /// Publishes <paramref name="token"/> until the scope is disposed. A token that cannot be
    /// cancelled keeps the outer one, so a nested call without a token does not hide the
    /// caller's.
    /// </summary>
    public static Scope Use(CancellationToken token)
    {
        var previous = Current.Value;
        if (token.CanBeCanceled)
        {
            Current.Value = token;
        }

        return new Scope(previous);
    }

    /// <summary>Throws <see cref="OperationCanceledException"/> when the token in effect is cancelled.</summary>
    public static void Check() => Current.Value.ThrowIfCancellationRequested();

    /// <summary>Checks once every <c>mask + 1</c> calls (<paramref name="mask"/> is a power of two minus one).</summary>
    public static void Every(ref int counter, int mask = 0xFF)
    {
        if ((++counter & mask) == 0)
        {
            Check();
        }
    }

    /// <summary>Restores the previous token on dispose.</summary>
    public readonly struct Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
