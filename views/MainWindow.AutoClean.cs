using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // ══════════════════════════════════════════════════════════════════════
        // AUTO-CLEAN TOOL
        // ══════════════════════════════════════════════════════════════════════


        private void AutoClean_Click(object s, RoutedEventArgs e)
        {
            bool isLight = SMDWin.Services.ThemeManager.IsLight(SMDWin.Services.SettingsService.Current.ThemeName) ||
                           (SMDWin.Services.SettingsService.Current.ThemeName == "Auto" &&
                            !SMDWin.Services.ThemeManager.IsDark(SMDWin.Services.ThemeManager.ResolveAuto()));

            var dlg = new AutoCleanDialog(isLight)
            {
                Owner       = this,
                FormatBytes = FormatBytes,
                ScanAction  = () => Task.Run(() =>
                {
                    var sizes = new Dictionary<string, long>();
                    sizes["Temp"]       = GetFolderSize(System.IO.Path.GetTempPath())
                                        + GetFolderSize(@"C:\Windows\Temp");
                    sizes["Prefetch"]   = GetFolderSize(@"C:\Windows\Prefetch");
                    sizes["Thumb"]      = GetFolderSize(System.IO.Path.Combine(
                                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                            @"Microsoft\Windows\Explorer"), "thumbcache*.db");
                    sizes["WinUpdate"]  = GetFolderSize(@"C:\Windows\SoftwareDistribution\Download");
                    sizes["EventLog"]   = GetFolderSize(@"C:\Windows\System32\winevt\Logs", "*.evtx");
                    sizes["RecycleBin"] = GetRecycleBinSize();
                    return sizes;
                }),
                CleanAction = async (selected) =>
                {
                    long freed = 0;
                    await Task.Run(() =>
                    {
                        if (selected.GetValueOrDefault("Temp"))
                        { freed += DeleteFolder(System.IO.Path.GetTempPath()); freed += DeleteFolder(@"C:\Windows\Temp"); }
                        if (selected.GetValueOrDefault("Prefetch"))
                            freed += DeleteFolder(@"C:\Windows\Prefetch");
                        if (selected.GetValueOrDefault("Thumb"))
                            freed += DeleteFiles(System.IO.Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                @"Microsoft\Windows\Explorer"), "thumbcache*.db");
                        if (selected.GetValueOrDefault("WinUpdate"))
                            freed += DeleteFolder(@"C:\Windows\SoftwareDistribution\Download");
                        if (selected.GetValueOrDefault("EventLog"))
                            freed += DeleteFiles(@"C:\Windows\System32\winevt\Logs", "*.evtx");
                        if (selected.GetValueOrDefault("RecycleBin"))
                            EmptyRecycleBin();
                    });
                    SMDWin.Services.ToolActivityLog.Add("Auto-Clean", $"{FormatBytes(freed)} freed", "ok");
                    return freed;
                },
            };
            SMDWin.Views.Net.NetWindowHelper.ShowModalWithOverlay(dlg, this);
        }

        private static long GetFolderSize(string path, string pattern = "*")
        {
            try
            {
                if (!System.IO.Directory.Exists(path)) return 0;
                return System.IO.Directory.EnumerateFiles(path, pattern, System.IO.SearchOption.AllDirectories)
                    .Sum(f => { try { return new System.IO.FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        /// <summary>
        /// Empties the content of a temporary folder. Does not touch the app's own folder (%TEMP%\SMDWin: the "bundle" tools)
        /// and does NOT follow links (junction / symlink) to other folders. Errors are written to the log.
        /// </summary>
        private static long DeleteFolder(string path)
        {
            long freed = 0;
            int failed = 0;
            string own = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SMDWin").TrimEnd('\\');
            void Walk(string dir)
            {
                if (dir.TrimEnd('\\').Equals(own, StringComparison.OrdinalIgnoreCase)) return;   // the app folder
                try
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(dir))
                        try { var fi = new System.IO.FileInfo(f); long len = fi.Length; fi.Delete(); freed += len; } catch { failed++; }
                    foreach (var d in System.IO.Directory.EnumerateDirectories(dir))
                    {
                        try
                        {
                            var di = new System.IO.DirectoryInfo(d);
                            if ((di.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
                            {   // link: only the link is deleted, not what is behind it
                                di.Delete(false); continue;
                            }
                            Walk(d);
                            if (!d.TrimEnd('\\').Equals(own, StringComparison.OrdinalIgnoreCase)) di.Delete(false);   // only if it was left empty
                        }
                        catch { failed++; }
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "AutoClean.DeleteFolder " + dir); }
            }
            if (System.IO.Directory.Exists(path)) Walk(path);
            AppLogger.Info($"Auto-Clean: {path} -> {FormatBytes(freed)} freed, {failed} items skipped (in use or protected)");
            return freed;
        }

        private static long DeleteFiles(string path, string pattern)
        {
            long freed = 0;
            try
            {
                if (!System.IO.Directory.Exists(path)) return 0;
                foreach (var f in System.IO.Directory.EnumerateFiles(path, pattern))
                    try { var fi = new System.IO.FileInfo(f); freed += fi.Length; fi.Delete(); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
            return freed;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int  cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [System.Runtime.InteropServices.DllImport("Shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        private static long GetRecycleBinSize()
        {
            try
            {
                var info = new SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                int hr = SHQueryRecycleBinW(null, ref info);
                return hr == 0 ? info.i64Size : 0;
            }
            catch { return 0; }
        }

        [System.Runtime.InteropServices.DllImport("Shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private static void EmptyRecycleBin()
        {
            try { SHEmptyRecycleBinW(IntPtr.Zero, null, 0x00000007); } catch (Exception logEx) { AppLogger.Warning(logEx, "SHEmptyRecycleBinW(IntPtr.Zero, null, 0x00000007);"); }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // ══════════════════════════════════════════════════════════════════════
        // HIBERNATE
        // ══════════════════════════════════════════════════════════════════════

        private async void ToggleHibernate_Click(object s, RoutedEventArgs e)
        {
            // Hibernation is active when hiberfil.sys exists
            bool isEnabled = await Task.Run(() => System.IO.File.Exists(@"C:\hiberfil.sys"));

            if (isEnabled)
            {
                if (!AppDialog.Confirm(
                    _LS("Tools", "DisableHibernateConfirm"),
_LS("Tools", "DisableHibernateTitle"))) return;
                RunPowercfg("/h off");
                ShowToastSuccess(_LS("Tools", "HibernateDisabledMsg"));
                SMDWin.Services.ToolActivityLog.Add("Hibernate", "turned off · hiberfil.sys removed", "ok");
            }
            else
            {
                RunPowercfg("/h on");
                ShowToastSuccess(_LS("Tools", "HibernateEnabledMsg"));
                SMDWin.Services.ToolActivityLog.Add("Hibernate", "turned on", "info");
            }
            await Task.Delay(500);
            RefreshHibernateStatus();
        }

        private static void RunPowercfg(string args)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
"powercfg", args)
                { UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Events.ToggleHibernate_Click"); }
        }

        internal async void RefreshHibernateStatus()
        {
            try
            {
                bool exists = await Task.Run(() => System.IO.File.Exists(@"C:\hiberfil.sys"));
                long sizeBytes = 0;
                if (exists)
                    try { sizeBytes = new System.IO.FileInfo(@"C:\hiberfil.sys").Length; } catch (Exception logEx) { AppLogger.Warning(logEx, "Unhandled exception"); }

                if (TxtHibernateStatus != null)
                {
                    TxtHibernateStatus.Text = exists ? _LS("Common", "Enabled") : _LS("Common", "Disabled");
                    TxtHibernateStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                        exists ? "StatusSuccessBrush" : "TextSecondaryBrush");
                }
                if (TxtHibernateInfo != null)
                    TxtHibernateInfo.Text = exists
                        ? ($"hiberfil.sys: {FormatBytes(sizeBytes)} " + _LS("Tools", "UsedOnDisk"))
                        : _LS("Tools", "HibernateDisabledNotFound");
                if (BtnToggleHibernate != null)
                {
                    // Button content is a StackPanel - update the TextBlock label inside
                    var lbl = BtnToggleHibernate.FindName("_HibBtnLbl") as System.Windows.Controls.TextBlock;
                    if (lbl == null)
                    {
                        // fallback: search visual tree
                        foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(BtnToggleHibernate))
                        {
                            lbl = tb; break;
                        }
                    }
                    if (lbl != null)
                        lbl.Text = exists ? _LS("Tools", "DisableHibernateBtn") : _LS("Tools", "EnableHibernateBtn");
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "RefreshHibernateStatus"); }
        }
    }
}
