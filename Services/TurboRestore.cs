using System;
using System.IO;
using System.Text.Json;

namespace SMDWin.Services
{
    /// <summary>
    /// The processor power settings changed "only while the app is open", kept in a file in Data with their values from
    /// before the change. Two groups of settings use it:
    ///   • Turbo — the processor boost mode and the maximum processor state (Turbo button, Processor boost mode);
    ///   • ProcState — the minimum and maximum processor state (the Power page slider).
    /// Each value is remembered once, before its first temporary change, and forgotten when a permanent change is made
    /// to it. On close the remembered values are applied back; if the app stopped without applying them (crash, forced
    /// close), they are applied at the next start.
    /// </summary>
    public static class TurboRestore
    {
        public enum Part { Turbo, ProcState }

        public sealed class Snapshot
        {
            public DateTime SavedUtc { get; set; }
            public int? BoostAc { get; set; }
            public int? BoostDc { get; set; }
            public int? MaxAc { get; set; }
            public int? MaxDc { get; set; }
            public int? MinAc { get; set; }
            public int? MinDc { get; set; }

            internal bool IsEmpty => BoostAc == null && BoostDc == null && MaxAc == null && MaxDc == null
                                     && MinAc == null && MinDc == null;
        }

        private static readonly object _lock = new();
        private static string FilePath => AppPaths.Data("turbo-restore.json");

        /// <summary>Any temporary change is active.</summary>
        public static bool HasPending
        {
            get { lock (_lock) { try { return Load() is { IsEmpty: false }; } catch { return false; } } }
        }

        /// <summary>A temporary change of this group is active.</summary>
        public static bool HasPendingFor(Part part)
        {
            lock (_lock)
            {
                try
                {
                    var s = Load();
                    if (s == null) return false;
                    return part == Part.Turbo
                        ? s.BoostAc != null || s.BoostDc != null || s.MaxAc != null || s.MaxDc != null
                        : s.MinAc != null || s.MinDc != null || s.MaxAc != null || s.MaxDc != null;
                }
                catch { return false; }
            }
        }

        /// <summary>Reads the current settings from Windows.</summary>
        public static Snapshot ReadCurrent() => new()
        {
            SavedUtc = DateTime.UtcNow,
            BoostAc = (int?)PowerAdvancedService.GetBoostMode(true),
            BoostDc = (int?)PowerAdvancedService.GetBoostMode(false),
            MaxAc   = PowerAdvancedService.GetMaxProcessorState(true),
            MaxDc   = PowerAdvancedService.GetMaxProcessorState(false),
            MinAc   = PowerAdvancedService.GetMinProcessorState(true),
            MinDc   = PowerAdvancedService.GetMinProcessorState(false),
        };

        /// <summary>
        /// Before a temporary change: keeps the current values of the group, only those not kept yet
        /// (a second temporary change must not replace the original values).
        /// </summary>
        public static void RememberOriginalIfNeeded(Part part = Part.Turbo)
        {
            lock (_lock)
            {
                try
                {
                    var saved = Load() ?? new Snapshot { SavedUtc = DateTime.UtcNow };
                    var now = ReadCurrent();
                    if (part == Part.Turbo)
                    {
                        saved.BoostAc ??= now.BoostAc;
                        saved.BoostDc ??= now.BoostDc;
                    }
                    else
                    {
                        saved.MinAc ??= now.MinAc;
                        saved.MinDc ??= now.MinDc;
                    }
                    saved.MaxAc ??= now.MaxAc;
                    saved.MaxDc ??= now.MaxDc;
                    Save(saved);
                    AppLogger.Info("TurboRestore: original {0} settings kept (boost {1}/{2}, min {3}/{4}, max {5}/{6})",
                        part, saved.BoostAc, saved.BoostDc, saved.MinAc, saved.MinDc, saved.MaxAc, saved.MaxDc);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TurboRestore.RememberOriginalIfNeeded"); }
            }
        }

        /// <summary>Permanent change: the new values of the group stay, their original copy is no longer used.</summary>
        public static void Clear(Part part = Part.Turbo)
        {
            lock (_lock)
            {
                try
                {
                    var saved = Load();
                    if (saved == null) return;
                    if (part == Part.Turbo) { saved.BoostAc = null; saved.BoostDc = null; }
                    else { saved.MinAc = null; saved.MinDc = null; }
                    saved.MaxAc = null; saved.MaxDc = null;
                    Save(saved);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TurboRestore.Clear"); }
            }
        }

        /// <summary>
        /// The values that are back to the original ones (the user returned to them) no longer need their copy.
        /// </summary>
        public static void ClearIfBackToOriginal(Part part = Part.Turbo)
        {
            lock (_lock)
            {
                try
                {
                    var saved = Load();
                    if (saved == null) return;
                    var now = ReadCurrent();
                    if (saved.BoostAc == now.BoostAc && saved.BoostDc == now.BoostDc) { saved.BoostAc = null; saved.BoostDc = null; }
                    if (saved.MinAc == now.MinAc && saved.MinDc == now.MinDc) { saved.MinAc = null; saved.MinDc = null; }
                    if (saved.MaxAc == now.MaxAc && saved.MaxDc == now.MaxDc) { saved.MaxAc = null; saved.MaxDc = null; }
                    Save(saved);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "TurboRestore.ClearIfBackToOriginal"); }
            }
        }

        /// <summary>Applies the original values and deletes the copy. Returns true if there was anything to apply.</summary>
        public static bool RestoreAndClear()
        {
            lock (_lock)
            {
                Snapshot? saved;
                try { saved = Load(); }
                catch (Exception ex) { AppLogger.Warning(ex, "TurboRestore: unreadable file"); saved = null; }
                if (saved == null || saved.IsEmpty) { Delete(); return false; }

                bool ok = true;
                try
                {
                    if (saved.BoostAc is int bac && Enum.IsDefined(typeof(PowerAdvancedService.BoostMode), bac))
                        PowerAdvancedService.SetBoostMode((PowerAdvancedService.BoostMode)bac, applyToAc: true, applyToDc: false);
                    if (saved.BoostDc is int bdc && Enum.IsDefined(typeof(PowerAdvancedService.BoostMode), bdc))
                        PowerAdvancedService.SetBoostMode((PowerAdvancedService.BoostMode)bdc, applyToAc: false, applyToDc: true);
                }
                catch (Exception ex) { ok = false; AppLogger.Warning(ex, "TurboRestore: boost mode"); }
                try
                {
                    if (saved.MinAc is int nac && nac is >= 0 and <= 100)
                        PowerAdvancedService.SetMinProcessorState(nac, applyToAc: true, applyToDc: false);
                    if (saved.MinDc is int ndc && ndc is >= 0 and <= 100)
                        PowerAdvancedService.SetMinProcessorState(ndc, applyToAc: false, applyToDc: true);
                }
                catch (Exception ex) { ok = false; AppLogger.Warning(ex, "TurboRestore: minimum processor state"); }
                try
                {
                    if (saved.MaxAc is int mac && mac is >= 1 and <= 100)
                        PowerAdvancedService.SetMaxProcessorState(mac, applyToAc: true, applyToDc: false);
                    if (saved.MaxDc is int mdc && mdc is >= 1 and <= 100)
                        PowerAdvancedService.SetMaxProcessorState(mdc, applyToAc: false, applyToDc: true);
                }
                catch (Exception ex) { ok = false; AppLogger.Warning(ex, "TurboRestore: maximum processor state"); }

                // The copy stays if applying failed — it is retried at the next start.
                if (ok) Delete();
                AppLogger.Info("TurboRestore: original settings applied {0}", ok ? "completely" : "partially (the copy stays)");
                return true;
            }
        }

        private static Snapshot? Load()
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(FilePath));
        }

        private static void Save(Snapshot s)
        {
            if (s.IsEmpty) { Delete(); return; }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void Delete()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex) { AppLogger.Warning(ex, "TurboRestore.Delete"); }
        }
    }
}
