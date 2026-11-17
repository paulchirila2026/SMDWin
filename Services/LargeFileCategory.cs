using System;
using System.IO;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>
    /// Category of a large file in Storage › Large files. Decided by the real system folders (not by pieces of text in the
    /// path); returns a stable key; the displayed text comes from the language files.
    /// Only "Temporary" is offered for deletion.
    /// </summary>
    public static class LargeFileCategory
    {
        public const string Temporary = "Temporary";
        public const string Logs      = "Logs";
        public const string Backup    = "Backup";
        public const string Media     = "Media";
        public const string SystemFiles = "System";
        public const string Apps      = "Apps";
        public const string Other     = "Other";

        private static readonly string[] MediaExt =
            { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mp3", ".flac", ".wav", ".m4a", ".jpg", ".jpeg", ".png", ".heic", ".raw" };

        /// <summary>The system folders, read once (can be overridden in tests).</summary>
        internal static string WindowsDir = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        internal static string[] ProgramDirs =
        {
            Norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            Norm(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
        };
        internal static string[] TempDirs =
        {
            Norm(Path.GetTempPath()),
            Norm(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")),
        };

        public static string Classify(string fullPath)
        {
            string path = Norm(fullPath);
            string ext = Path.GetExtension(path);
            var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);

            if (ext == ".tmp" || TempDirs.Any(d => Under(path, d)) || segments.Any(s => s is "temp" or "tmp"))
                return Temporary;
            if (ext is ".log" or ".etl" || ext.StartsWith(".log", StringComparison.Ordinal) || path.EndsWith(".log.old", StringComparison.Ordinal))
                return Logs;
            if (ext is ".bak" or ".old" || segments.Any(s => s is "backup" or "backups"))
                return Backup;
            if (MediaExt.Contains(ext))
                return Media;
            if (Under(path, WindowsDir))
                return SystemFiles;
            if (ProgramDirs.Any(d => Under(path, d)))
                return Apps;
            return Other;
        }

        public static bool IsSafeToDelete(string category) => category == Temporary;

        /// <summary>Displayed (translated) text for a category key.</summary>
        public static string Display(string category) => LanguageService.S("Storage", "FileCat" + category, category);

        private static bool Under(string path, string dir) =>
            dir.Length > 0 && path.StartsWith(dir + "\\", StringComparison.Ordinal);

        private static string Norm(string p) =>
            string.IsNullOrWhiteSpace(p) ? "" : p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }
}
