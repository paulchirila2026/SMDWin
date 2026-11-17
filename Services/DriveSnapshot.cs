using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Drives (C:, D:, sticks…) read in the background. <c>DriveInfo.IsReady</c> / free space can wait for seconds while
    /// a just-connected (or failing) external disk spins up; on the window thread that freezes it.
    /// Each drive is read separately, and those that do not answer in time are left out.
    /// </summary>
    public sealed class DriveSnapshot
    {
        public string Letter { get; init; } = "";        // „D:”
        public string Root { get; init; } = "";          // „D:\”
        public DriveType Type { get; init; }
        public string Format { get; init; } = "";
        public string Label { get; init; } = "";
        public long TotalBytes { get; init; }
        public long FreeBytes { get; init; }

        /// <summary>Ready drives (with a mounted volume). Those that do not answer within <paramref name="timeoutMs"/> are missing from the list.</summary>
        public static async Task<List<DriveSnapshot>> ReadyDrivesAsync(int timeoutMs = 3000)
        {
            DriveInfo[] all;
            try { all = await Task.Run(DriveInfo.GetDrives).ConfigureAwait(false); }
            catch (Exception ex) { AppLogger.Warning(ex, "DriveSnapshot.GetDrives"); return new(); }

            var tasks = all.Select(d => Task.Run(() => Read(d))).ToArray();
            var all3 = Task.WhenAll(tasks);
            await Task.WhenAny(all3, Task.Delay(timeoutMs)).ConfigureAwait(false);

            var result = new List<DriveSnapshot>();
            for (int i = 0; i < tasks.Length; i++)
            {
                if (tasks[i].IsCompletedSuccessfully && tasks[i].Result is DriveSnapshot s) result.Add(s);
                else if (!tasks[i].IsCompleted) AppLogger.Info("DriveSnapshot: {0} did not answer in {1} ms — left out", all[i].Name, timeoutMs);
            }
            return result;
        }

        private static DriveSnapshot? Read(DriveInfo d)
        {
            try
            {
                if (!d.IsReady) return null;
                string root = d.RootDirectory.FullName;
                return new DriveSnapshot
                {
                    Root = root,
                    Letter = root.TrimEnd('\\', '/').TrimEnd(':').ToUpperInvariant() + ":",
                    Type = d.DriveType,
                    Format = d.DriveFormat ?? "",
                    Label = d.VolumeLabel ?? "",
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.AvailableFreeSpace,
                };
            }
            catch (Exception ex) { AppLogger.Debug($"DriveSnapshot {d.Name}: {ex.Message}"); return null; }
        }
    }
}
