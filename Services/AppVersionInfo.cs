using System;
using System.Linq;
using System.Reflection;

namespace SMDWin.Services
{
    /// <summary>
    /// Version text, the same everywhere (splash, About, Settings, Features, reports, log, self-test, Debug).
    /// until the public release only the build number is shown — "Build 57". It comes from build-number.txt
    /// (SMDWin.csproj, target SmdBuildNumber): every publish adds 1; an ordinary build from Visual Studio keeps the number and
    /// shows "Build 57 dev". At the public release the stage comes back ("0.1 Beta · build 432").
    /// </summary>
    public static class AppVersionInfo
    {
        /// <summary>Not a public release yet: the log stays detailed (AppLogger).</summary>
        public const bool Prerelease = true;

        private static string? Meta(string key)
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == key)?.Value;
            }
            catch (Exception ex) { AppLogger.Debug($"AppVersionInfo {key}: {ex.Message}"); return null; }
        }

        /// <summary>The build number (0 when the exe was built without it).</summary>
        public static int Build { get; } = int.TryParse(Meta("SmdBuild"), out int b) ? b : 0;

        /// <summary>True for a build from Visual Studio (F5 / Build), false for a published exe.</summary>
        public static bool DevBuild { get; } = Meta("SmdBuildKind") != "release";

        /// <summary>When the exe was built (local time), from the BuildDateUtc metadata, else the exe's file time.</summary>
        public static DateTime? BuiltAt { get; } = ReadBuiltAt();

        private static DateTime? ReadBuiltAt()
        {
            try
            {
                if (DateTime.TryParse(Meta("BuildDateUtc"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    return dt.ToLocalTime();
                string exe = Environment.ProcessPath ?? "";
                return System.IO.File.Exists(exe) ? System.IO.File.GetLastWriteTime(exe) : null;
            }
            catch (Exception ex) { AppLogger.Debug($"AppVersionInfo built-at: {ex.Message}"); return null; }
        }

        /// <summary>The technical version (0.4, 0.4.1) — kept for the file properties and Debug, not shown to users.</summary>
        public static string Number
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    if (v == null) return "0.4";
                    return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
                }
                catch (Exception ex) { AppLogger.Debug($"AppVersionInfo: {ex.Message}"); return "0.4"; }
            }
        }

        /// <summary>„Build 57” (or „Build 57 dev”).</summary>
        public static string Short => Build > 0 ? $"Build {Build}{(DevBuild ? " dev" : "")}" : $"v{Number} dev";

        /// <summary>„Build 57 · 11.11.2026 13:42” — for the log, the self-test and reports.</summary>
        public static string Long => BuiltAt is DateTime d ? $"{Short} · {d:dd.MM.yyyy HH:mm}" : Short;
    }
}
