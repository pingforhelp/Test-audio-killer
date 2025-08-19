using System.Collections.Concurrent;
using System.Threading;

namespace Jellyfin.Plugin.AudioPruner.Services;

public static class FileLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public static SemaphoreSlim GetLock(string path) =>
        _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    public static void ReleaseAndCleanup(string path, SemaphoreSlim sem)
    {
        try { sem.Release(); } catch { }
        // keep entry; removing raises race complexity — memory cost is trivial.
    }
}
