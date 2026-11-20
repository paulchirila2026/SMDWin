using System;
using System.IO;
using System.Text.Json;
using SMDWin.Models;

namespace SMDWin.Services
{
    public static class SettingsService
    {
        // Settings live next to SMDWin.exe (see AppPaths) — NOT AppContext.BaseDirectory, which in the single-file build points
        // to the temporary .NET extraction folder.
        private static readonly string _portablePath = AppPaths.Data("settings.json");

        private static readonly string _appDataPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "SMDWin", "settings.json");

        public static bool IsPortable { get; } = true;

        private static readonly string SettingsPath = IsPortable ? _portablePath : _appDataPath;

        /// <summary>Full path to the settings.json file currently in use.</summary>
        public static string SettingsFilePath => SettingsPath;

        public static AppSettings Current { get; private set; } = new();

        public static string GetEffectiveReportFolder()
        {
            if (Current.UseNetworkReportPath && !string.IsNullOrWhiteSpace(Current.NetworkReportPath))
            {
                try
                {
                    if (Directory.Exists(Current.NetworkReportPath))
                        return Current.NetworkReportPath;
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* invalid path, unreachable server etc. — fall back */ }
            }
            return !string.IsNullOrWhiteSpace(Current.ReportPath) && Directory.Exists(Current.ReportPath)
                ? Current.ReportPath
                : AppPaths.ReportsDirectory;
        }

        /// <summary>Tests whether a network folder (UNC path) is writable. Used by the "Test Connection" button in Settings —
        /// with a short implicit timeout through Directory.Exists (does not block the UI for long if the server does not answer).</summary>
        public static bool TestNetworkPathWritable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (!Directory.Exists(path)) return false;
                string testFile = Path.Combine(path, $".smdwin_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch { return false; }
        }

        private static readonly string[] ValidThemes =
            { "Dark", "Light", "Auto",
              // legacy — accepted but migrated automatically
              "Dark Navy", "Dark Slate", "Dark Midnight", "Light Clean", "Light Warm", "Light Refined" };

        private const int CurrentSettingsVersion = 28;

        /// <summary>All settings back to their defaults (Settings › Advanced › Reset); saved immediately.</summary>
        public static void ResetToDefaults()
        {
            Current = new AppSettings();
            Save();
        }

        public static void Load()
        {
            AppPaths.MigrateLegacyData();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { Current = new AppSettings(); }

            if (Current.ChartColorMode == "Colored") Current.ChartColorMode = "LightBlue";

            // ── Settings migration: add new fields without wiping existing user prefs ──
            // Each migration block runs only once (version gate), then bumps the version.
            // JSON deserialization gives default values for missing fields automatically,
            // so we only need to handle cases where we CHANGE defaults or rename fields.
            if (Current.SettingsVersion < 1)
            {
                // v0 → v1: Initial versioning. No breaking changes, just stamp the version.
                Current.SettingsVersion = 1;
                Save(); // persist the bumped version immediately
            }
            if (Current.SettingsVersion < 2)
            {
                // v1 → v2: Mica enabled by default on Windows 11 for new installs and for users who had not changed the setting
                // (the default value was false)
                if (!Current.UseMica && ThemeManager.IsWindows11())
                    Current.UseMica = true;
                Current.SettingsVersion = 2;
                Save();
            }
            if (Current.SettingsVersion < 3)
            {
                // v2 → v3: redenumire teme (Dark Navy→Dark, Light Clean→Light)
                Current.ThemeName      = MigrateThemeName(Current.ThemeName);
                Current.AutoDarkTheme  = MigrateThemeName(Current.AutoDarkTheme);
                Current.AutoLightTheme = MigrateThemeName(Current.AutoLightTheme);
                Current.SettingsVersion = 3;
                Save();
            }
            if (Current.SettingsVersion < 4)
            {
                // v3 → v4: forces UseMica=true on Win11, sets the default MicaOpacity
                if (ThemeManager.IsWindows11())
                    Current.UseMica = true;
                if (Current.MicaOpacity <= 0.01)
                    Current.MicaOpacity = 0.80;
                Current.SettingsVersion = 4;
                Save();
            }
            if (Current.SettingsVersion < 5)
            {
                // v4 → v5: reset MicaExplicitlyDisabled (could be true from earlier versions that set it wrongly). Mica is on by default on Win11.
                Current.MicaExplicitlyDisabled = false;
                if (ThemeManager.IsWindows11())
                    Current.UseMica = true;
                if (Current.MicaOpacity <= 0.01)
                    Current.MicaOpacity = 0.80;
                Current.SettingsVersion = 5;
                Save();
            }
            // Template for future migrations:
            // if (Current.SettingsVersion < 2)
            // {
            //     Current.SomeNewField = MigratedValue(Current.OldField);
            //     Current.SettingsVersion = 2;
            //     Save();
            // }
            if (Current.SettingsVersion < 6)
            {
                // v5 → v6: Widget refactor — always dark, 2 modes only (Graphs/Gauges)
                if (Current.WidgetMode == "Compact" || Current.WidgetMode == "Detailed" || Current.WidgetMode == "Full")
                    Current.WidgetMode = "Graphs";
                if (string.IsNullOrWhiteSpace(Current.WidgetMetrics))
                    Current.WidgetMetrics = "CPU,RAM,Disk,Network";
                Current.SettingsVersion = 6;
                Save();
            }

            if (Current.SettingsVersion < 7)
            {
                // v6 → v7: Enable animations by default for all users.
                // The old default was false (conservative choice during early dev).
                // Now that rendering is stable, animations are on unless user explicitly disabled them.
                Current.EnableAnimations = true;
                Current.SettingsVersion = 7;
                Save();
            }

            if (Current.SettingsVersion < 8)
            {
                // v7 → v8: version bump only.
                Current.SettingsVersion = 8;
                Save();
            }

            if (Current.SettingsVersion < 9)
            {
                if (string.IsNullOrWhiteSpace(Current.WidgetCompactMetrics))
                {
                    Current.WidgetCompactMetrics = "CPUTemp,GPUTemp";
                }
                Current.SettingsVersion = 9;
                Save();
            }

            if (Current.SettingsVersion < 10)
            {
                // v9 → v10: version bump only — do NOT force CloseToTray=true.
                Current.SettingsVersion = 10;
                Save();
            }

            if (Current.SettingsVersion < 11)
            {
                // v10 → v11: reset CloseToTray to false — previous migration had forced it true
                // for all existing users. Now we respect the user's choice (default: off).
                Current.CloseToTray = false;
                Current.SettingsVersion = 11;
                Save();
            }

            if (Current.SettingsVersion < 12)
            {
                // v11 → v12: LHM is always on through PawnIO (no WinRing0). UseLhm is no longer a toggle — TempReader ignores it.
                Current.SettingsVersion = 12;
                Save();
            }
            if (Current.SettingsVersion < 13)
            {
                // v12 → v13: default chart window raised from 30 s/60 s to 300 s (5 min). Existing users have the old value saved in
                // the JSON — if it is below the old default threshold, it is raised to the new default (an explicit larger choice,
                // e.g. 600 s, is not overwritten).
                if (Current.ChartWindowSeconds <= 60)
                    Current.ChartWindowSeconds = 300;
                Current.SettingsVersion = 13;
                Save();
            }

            if (Current.SettingsVersion < 14)
            {
                // v13 → v14: WidgetMode has a single valid value today ("Compact" — CompactWidgetWindow is the only widget type).
                // An older migration (v5→v6, above) rewrote "Compact" -> "Graphs", and existing settings.json files stayed on "Graphs";
                // the Widget card in Settings then hid the metric checkboxes. This resets the value to "Compact".
                Current.WidgetMode = "Compact";
                Current.SettingsVersion = 14;
                Save();
            }

            if (Current.SettingsVersion < 15)
            {
                Current.PingBackgroundTrackingEnabled = true;
                Current.SettingsVersion = 15;
                Save();
            }

            if (Current.SettingsVersion < 15)
            {
                if (Current.ChartWindowSeconds == 300)
                    Current.ChartWindowSeconds = 60;
                Current.SettingsVersion = 15;
                Save();
            }

            if (Current.SettingsVersion < 16)
            {
                Current.StressProcTableExpanded = false;
                Current.SettingsVersion = 16;
                Save();
            }

            if (Current.SettingsVersion < 17)
            {
                // v16 → v17: the default widget style becomes "Gauges". Whoever had the old default ("Pill") moves to Gauges once;
                // a choice made after this version (in Settings or the widget menu) stays.
                if (string.IsNullOrEmpty(Current.WidgetStyle) || Current.WidgetStyle == "Pill")
                    Current.WidgetStyle = "Gauges";
                Current.SettingsVersion = 17;
                Save();
            }

            if (Current.SettingsVersion < 18)
            {
                // v17 → v18: the sidebar "test running" indicator defaults to squares. Whoever had the old default (the ring) moves to
                // the new style once; a style chosen later in Developer › Design experiments stays.
                if (string.IsNullOrEmpty(Current.TaskBadgeStyle) || Current.TaskBadgeStyle == "SpinRing")
                    Current.TaskBadgeStyle = SquareBadgeAnim.Default;
                Current.SettingsVersion = 18;
                Save();
            }

            if (Current.SettingsVersion < 19)
            {
                // v18 → v19: the battery on the Power page is drawn from blocks by default. Whoever had the old default (the simple
                // outline) moves to the new drawing once; a variant chosen later in Developer › Design experiments stays.
                if (string.IsNullOrEmpty(Current.BatteryGlyphStyle) || Current.BatteryGlyphStyle == "Classic")
                    Current.BatteryGlyphStyle = BatteryBlocks.Default;
                Current.SettingsVersion = 19;
                Save();
            }

            if (Current.SettingsVersion < 20)
            {
                // v19 → v20: the System Info PDF report defaults to a single page; whoever had the old default template moves to it once.
                if (string.IsNullOrEmpty(Current.HwInfoPdfTemplate) || Current.HwInfoPdfTemplate == "Technical")
                    Current.HwInfoPdfTemplate = "OnePage";
                Current.SettingsVersion = 20;
                Save();
            }

            if (Current.SettingsVersion < 21)
            {
                // v20 → v21: the "Technical" and "AppStyle" templates were replaced by "Like the app (one page)".
                if (Current.HwInfoPdfTemplate is "Technical" or "AppStyle") Current.HwInfoPdfTemplate = "OnePage";
                Current.SettingsVersion = 21;
                Save();
            }

            if (Current.SettingsVersion < 22)
            {
                // v21 → v22: reports are no longer saved to the Desktop by default, but to Data\Reports next to the exe.
                // Whoever had the old default (Desktop) moves to the new folder; an explicitly chosen folder stays.
                try
                {
                    string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    if (string.IsNullOrWhiteSpace(Current.ReportPath) ||
                        string.Equals(Path.GetFullPath(Current.ReportPath).TrimEnd('\\'), Path.GetFullPath(desk).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        Current.ReportPath = "";
                }
                catch (Exception ex) { AppLogger.Swallowed(ex); }
                Current.SettingsVersion = 22;
                Save();
            }

            if (Current.SettingsVersion < 23)
            {
                // v22 → v23: the three read rates (sensors / network / power) become a single level.
                Current.UpdateSpeed = Current.RefreshInterval <= 0.5 ? "Fast" : Current.RefreshInterval >= 3 ? "Saver" : "Normal";
                Current.CpuTempLimit = 0;
                Current.GpuTempLimit = 0;
                Current.SettingsVersion = 23;
                Save();
            }
            if (Current.SettingsVersion < 24)
            {
                // v23 → v24: the workshop details get a switch ("Use in a computer service"); turned on for whoever already had a name.
                Current.WorkshopMode = !string.IsNullOrWhiteSpace(Current.WorkshopName);
                Current.SettingsVersion = 24;
                Save();
            }
            if (Current.SettingsVersion < 25)
            {
                // v24 → v25: selected buttons are no longer solid blue, but the raised pill (style 6)
                if (Current.SelectedButtonStyleId == 0) Current.SelectedButtonStyleId = 6;
                Current.SettingsVersion = 25;
                Save();
            }
            if (Current.SettingsVersion < 26)
            {
                // v25 → v26: the new look relies on smooth motion (dials, figures, charts) — animations on again for everyone,
                // once; the user can still turn them off in Settings
                Current.EnableAnimations = true;
                Current.SettingsVersion = 26;
                Save();
            }
            if (Current.SettingsVersion < 27)
            {
                // v26 → v27: the Dashboard charts start at 1 minute again (they fill faster); other windows are in the right-click menu
                Current.ChartWindowSeconds = 60;
                Current.SettingsVersion = 27;
                Save();
            }
            if (Current.SettingsVersion < 28)
            {
                // v27 → v28: the charts show only the horizontal grid lines; the vertical ones (no labels, only noise on large,
                // mostly empty charts) stay available through "Gridlines" in the chart menu
                Current.ShowChartGridlines = false;
                Current.SettingsVersion = 28;
                Save();
            }
            if (Current.UpdateSpeed is not ("Saver" or "Normal" or "Fast")) Current.UpdateSpeed = "Normal";

            // Migrate old theme names to new ones
            Current.ThemeName = MigrateThemeName(Current.ThemeName);

            // Validate — reset to default if unknown theme
            if (!Array.Exists(ValidThemes, t => t == Current.ThemeName))
                Current.ThemeName = "Auto";

            // Validate AccentName
            // No accent colours: a red / green / orange accent made charts and selections look like warnings.
            // Blue is fixed; the setting stays in the file only for compatibility.
            Current.AccentName = "Blue";
            Current.CustomAccentHex = "";
            // the design variants removed from the Developer window: the decided ones only
            Current.DesignExperimentSysInfoLayout = "";
            Current.DesignExperimentPowerMap = false;
            Current.DesignExperimentMonoNumbers = false;
            Current.HwInfoPdfTemplate = "OnePage";

            // Validate AutoDark / AutoLight sub-themes
            var validDark = new[] { "Dark", "Dark Navy", "Dark Slate", "Dark Midnight" };
            if (!Array.Exists(validDark, t => t == Current.AutoDarkTheme))
                Current.AutoDarkTheme = "Dark";
            var validLight = new[] { "Light", "Light Clean", "Light Warm", "Light Refined" };
            if (!Array.Exists(validLight, t => t == Current.AutoLightTheme))
                Current.AutoLightTheme = "Light";

            // Back-compat: if old JSON had AutoTheme=true, map it to "Auto"
            if (Current.AutoTheme && Current.ThemeName != "Auto")
                Current.ThemeName = "Auto";

            // On Win11 Mica is ON by default, unless the user explicitly turned it off in Settings (MicaExplicitlyDisabled=true).
            if (ThemeManager.IsWindows11() && !Current.MicaExplicitlyDisabled)
                Current.UseMica = true;

            // MicaOpacity must have a valid value
            if (Current.MicaOpacity <= 0.01)
                Current.MicaOpacity = 0.80;
        }

        /// <summary>Maps legacy theme names to new equivalents.</summary>
        private static string MigrateThemeName(string? name) => name switch
        {
            "Dark Blue"     => "Dark",
            "Dark Graphite" => "Dark",
            "Dark Green"    => "Dark",
            "Dark Glass"    => "Dark",
            "Dark Navy"     => "Dark",
            "Dark Slate"    => "Dark",
            "Dark Midnight" => "Dark",
            "Light Blue"    => "Light",
            "Light Gray"    => "Light",
            "Light Fluent"  => "Light",
            "Light macOS"   => "Light",
            "Light Clean"   => "Light",
            "Light Warm"    => "Light",
            "Light Refined" => "Light",
            _               => name ?? "Dark"
        };

        private static readonly object _saveLock = new();

        public static void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                    var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                    // Write to temp file first, then rename — prevents corruption on crash/power loss
                    var tmp = SettingsPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, SettingsPath, overwrite: true);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "SettingsService.unknown"); }
            }
        }
    }
}
