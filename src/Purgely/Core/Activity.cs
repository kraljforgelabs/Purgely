namespace Purgely.Core;

/// <summary>
/// Tracks whether Purgely is in the middle of changing the PC (cleaning, restoring). While it is, windows must not
/// close and the app must not exit – a half-written Undo package could not be restored.
/// </summary>
public static class Activity
{
    private static int _running;

    public static bool IsBusy => Volatile.Read(ref _running) > 0;

    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _running);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _running);
        }
    }
}
