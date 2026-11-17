using System;
using System.Runtime;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Gives the large test buffers back to Windows after a test: 550 MB before the live tools,
    /// 1 617 MB after — the memory test works in 128 MB pieces, up to 1 GB, and .NET keeps the space of large freed objects
    /// for later instead of returning it). One compacting collection of the large-object area, in the background.
    /// </summary>
    public static class MemoryRelease
    {
        private static int _busy;

        public static void AfterLargeTest(string what)
        {
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500);   // let the test's last references go first
                    long before = Environment.WorkingSet;
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    long after = Environment.WorkingSet;
                    AppLogger.Info($"Memory released after {what}: {before / 1048576} MB → {after / 1048576} MB");
                }
                catch (Exception ex) { AppLogger.Debug($"MemoryRelease ({what}): {ex.Message}"); }
                finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }
            });
        }
    }
}
