namespace XRay.ThreeD.Internal;

/// <summary>Cheap cancellation checks for loops whose bound comes from the file.</summary>
internal static class Cancel
{
    /// <summary>Throws when <paramref name="token"/> is cancelled, but only on every <paramref name="mask"/> + 1 iterations (iteration 0 always checks).</summary>
    public static void Every(CancellationToken token, long iteration, int mask = 1023)
    {
        if ((iteration & mask) == 0)
        {
            token.ThrowIfCancellationRequested();
        }
    }
}
