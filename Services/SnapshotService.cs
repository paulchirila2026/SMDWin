using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SMDWin.Models;

namespace SMDWin.Services
{
    public static class SnapshotService
    {
        private static readonly string FolderPath = AppPaths.Data("Snapshots");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private static void EnsureFolder()
        {
            try { Directory.CreateDirectory(FolderPath); }
            catch (Exception ex) { AppLogger.Warning(ex, "SnapshotService.EnsureFolder"); }
        }

        /// <summary>Saves a new snapshot. The file name combines the date + the (sanitized) user-given name, so the list is
        /// already in chronological order on disk.</summary>
        public static string Save(SystemSnapshot snap)
        {
            EnsureFolder();
            string safeName = string.IsNullOrWhiteSpace(snap.Name) ? "snapshot" : snap.Name;
            foreach (char c in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');

            string fileName = $"{snap.TakenAt:yyyy-MM-dd_HH-mm-ss}_{safeName}.json";
            string fullPath = Path.Combine(FolderPath, fileName);

            string json = JsonSerializer.Serialize(snap, JsonOpts);
            File.WriteAllText(fullPath, json);
            return fullPath;
        }

        /// <summary>List of all saved snapshots, most recent first.
        /// Corrupt/old/incompatible files are skipped silently (they do not block the rest of the list).</summary>
        public static List<(string Path, SystemSnapshot Snapshot)> ListAll()
        {
            var result = new List<(string, SystemSnapshot)>();
            EnsureFolder();
            try
            {
                foreach (var file in Directory.GetFiles(FolderPath, "*.json").OrderByDescending(f => f))
                {
                    try
                    {
                        var snap = JsonSerializer.Deserialize<SystemSnapshot>(File.ReadAllText(file));
                        if (snap != null) result.Add((file, snap));
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, $"SnapshotService.Load({file})"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SnapshotService.ListAll"); }
            return result;
        }

        public static void Delete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { AppLogger.Warning(ex, "SnapshotService.Delete"); }
        }
    }
}
