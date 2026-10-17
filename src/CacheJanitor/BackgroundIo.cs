using System.Runtime.InteropServices;

namespace CacheJanitor;

/// <summary>
/// Puts the current thread into Windows background processing mode (low CPU, I/O and memory priority) so a
/// scan of a large cache never competes with the builds that use it. Dispose on the same thread to leave it.
/// </summary>
internal static partial class BackgroundIo
{
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    public static Scope Enter() => new(SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN));

    public readonly struct Scope(bool entered) : IDisposable
    {
        public void Dispose()
        {
            if (entered) SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(nint thread, int priority);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();
}
