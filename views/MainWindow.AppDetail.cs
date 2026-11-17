using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Models;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Applications page, the "Selected" card — the part that is found and done beyond the registry entry:
    ///   • information read in the background (Services/AppInsight.cs): the real size on disk, last run, whether it runs
    ///     now, the main program's signature;
    ///   • actions: Change / Repair, copy the details, open the key in Registry Editor, search for a newer version,
    ///     jump to Process Monitor / Startup Manager;
    ///   • the technician's note about the program (Services/AppNotesStore.cs), saved automatically.
    /// Rule: what cannot be found stays "—", with the reason in the tooltip.
    /// </summary>
    public partial class MainWindow
    {
        private int _appInsightVersion;
        private CancellationTokenSource? _appInsightCts;
        private AppInsightResult? _appInsight;
        private string _appStartupEntryName = "";
        private AppNotesStore? _appNotes;
        private InstalledApp? _appNoteFor;

        private AppNotesStore AppNotes => _appNotes ??= new AppNotesStore(Path.Combine(AppPaths.DataDirectory, "app-notes.json"));

        private static string AppText(string key, string english) => LanguageService.S("Apps", key, english);

        /// <summary>Marks in the table the programs that have a note.</summary>
        private void MarkAppsWithNotes()
        {
            try
            {
                if (AppNotes.Count == 0) return;
                foreach (var a in _allApps) a.HasNote = AppNotes.Has(a.Name, a.Publisher);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "MarkAppsWithNotes"); }
        }

        /// <summary>The new part of the card: fallback icon, the Change / Repair button, the note, then the background reads.</summary>
        private void ShowAppExtras(InstalledApp app)
        {
            // no icon of its own: a square in the program's colour, with its initial
            if (FindName("AppDetTile") is Border tile && FindName("TxtAppDetInitial") is TextBlock initial)
            {
                tile.Visibility = ImgAppDetIcon.Source == null ? Visibility.Visible : Visibility.Collapsed;
                tile.Background = app.TileBrush;
                initial.Text = app.Initial;
            }

            if (FindName("BtnAppModify") is System.Windows.Controls.Button modify)
            {
                bool can = !string.IsNullOrWhiteSpace(app.ModifyCommand);
                modify.IsEnabled = can;
                modify.ToolTip = can ? AppText("ModifyTip", "Open the program's own change / repair window")
                                     : AppText("ModifyNone", "This program does not offer a change or repair option");
            }

            _appNoteFor = app;
            if (FindName("TxtAppNotes") is System.Windows.Controls.TextBox notes) notes.Text = AppNotes.Get(app.Name, app.Publisher);

            foreach (var name in new[] { "TxtAppDetOnDisk", "TxtAppDetLastRun", "TxtAppDetRunning", "TxtAppDetSigned" })
                if (FindName(name) is TextBlock tb)
                {
                    tb.Text = "…"; tb.ToolTip = null; tb.Cursor = null; tb.TextDecorations = null;
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                }
            _appInsight = null;
            _ = LoadAppInsightAsync(app);
        }

        private async Task LoadAppInsightAsync(InstalledApp app)
        {
            int version = ++_appInsightVersion;
            _appInsightCts?.Cancel();
            _appInsightCts = new CancellationTokenSource();
            var ct = _appInsightCts.Token;
            AppInsightResult? result = null;
            try { result = await Task.Run(() => AppInsight.Read(app, ct)); }
            catch (Exception ex) { AppLogger.Warning(ex, "LoadAppInsightAsync"); }
            // another program was chosen meanwhile: the result no longer belongs to the card shown
            if (version != _appInsightVersion || result == null) return;
            _appInsight = result;
            ShowAppInsight(result);
        }

        private void ShowAppInsight(AppInsightResult r)
        {
            void Set(string name, string text, string brushKey, string? tip, bool link = false)
            {
                if (FindName(name) is not TextBlock tb) return;
                tb.Text = text;
                tb.ToolTip = tip;
                tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
                tb.Cursor = link ? System.Windows.Input.Cursors.Hand : null;
                tb.TextDecorations = link ? TextDecorations.Underline : null;
            }

            if (r.DiskBytes is long bytes)
                Set("TxtAppDetOnDisk", (r.DiskPartial ? "≥ " : "") + FormatBytes(bytes), "TextPrimaryBrush",
                    r.DiskPartial ? AppText("OnDiskPartialTip", "A large folder — counting stopped after a few seconds, the real size is at least this.")
                                  : AppText("OnDiskTip", "What the program's folder takes on the disk now."));
            else
                Set("TxtAppDetOnDisk", "—", "TextSecondaryBrush",
                    AppText("OnDiskUnknownTip", "The installer did not report a folder that belongs only to this program."));

            if (r.LastRun is DateTime last)
                Set("TxtAppDetLastRun", AppCommands.Ago(last, DateTime.Now), "TextPrimaryBrush",
                    $"{DateFormatHelper.FormatDateTimeSec(last)}\n" + AppText("LastRunTip", "From the Windows start-up traces (prefetch), matched by the program's file name."));
            else
                Set("TxtAppDetLastRun", "—", "TextSecondaryBrush",
                    AppText("LastRunUnknownTip", "Windows has no trace of this program being started (or the traces were cleaned)."));

            if (r.RunningCount > 0)
                Set("TxtAppDetRunning", string.Format(r.RunningCount == 1 ? AppText("RunningOne", "Yes · 1 process") : AppText("RunningMany", "Yes · {0} processes"), r.RunningCount),
                    "AccentBrush", string.Join(", ", r.RunningNames) + "\n" + AppText("RunningTip", "Click to see it in Process Monitor."), link: true);
            else
                Set("TxtAppDetRunning", AppText("No", "No"), "TextPrimaryBrush", null);

            switch (r.SignStatus)
            {
                case "signed":
                    Set("TxtAppDetSigned", string.IsNullOrWhiteSpace(r.SignPublisher) ? AppText("Signed", "Signed") : r.SignPublisher, "TextPrimaryBrush",
                        AppText("SignedTip", "The main program file has a valid digital signature.") + "\n" + r.MainExe);
                    break;
                case "untrusted":
                    Set("TxtAppDetSigned", AppText("SignedUntrusted", "Signed, not trusted"), "StatusWarningBrush",
                        AppText("SignedUntrustedTip", "The file is signed, but Windows does not trust the signature.") + "\n" + r.MainExe);
                    break;
                case "unsigned":
                    Set("TxtAppDetSigned", AppText("NotSigned", "Not signed"), "StatusWarningBrush",
                        AppText("NotSignedTip", "The main program file has no digital signature. Common for small free programs; worth a second look if the program is unknown.") + "\n" + r.MainExe);
                    break;
                default:
                    Set("TxtAppDetSigned", "—", "TextSecondaryBrush",
                        AppText("SignedUnknownTip", "The main program file could not be found, so there is nothing to check."));
                    break;
            }
        }

        // ── jump to the pages that show more ──
        private async void AppRunning_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_appInsight is not { RunningCount: > 0 } r) return;
            try
            {
                string search = System.IO.Path.GetFileNameWithoutExtension(r.RunningNames.FirstOrDefault() ?? "");
                await NavigateTo("Processes");
                if (search.Length > 0 && TxtProcSearch != null) TxtProcSearch.Text = search;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "AppRunning_Click"); }
        }

        private async void AppStartup_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_appStartupEntryName.Length == 0) return;
            try
            {
                string search = _appStartupEntryName;
                await NavigateTo("Startup");
                if (TxtStartupSearch != null) TxtStartupSearch.Text = search;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "AppStartup_Click"); }
        }

        // ── actiuni ──
        private void AppModify_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp app) { AppDialog.Show(_LS("Apps", "SelectApp")); return; }
            if (string.IsNullOrWhiteSpace(app.ModifyCommand))
            {
                ShowToastWarning(AppText("ModifyNone", "This program does not offer a change or repair option"));
                return;
            }
            try { _appsService.Modify(app); }
            catch (Exception ex) { AppDialog.Show(string.Format(_LS("Common", "ErrorFmt"), ex.Message)); }
        }

        private void AppMore_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement anchor || AppsGrid.SelectedItem is not InstalledApp app) return;
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = anchor,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            };
            void Item(string text, RoutedEventHandler click, bool enabled = true)
            {
                var item = new System.Windows.Controls.MenuItem { Header = text, IsEnabled = enabled };
                item.Click += click;
                menu.Items.Add(item);
            }
            Item(AppText("CopyDetails", "Copy details"), AppCopyDetails_Click);
            Item(AppText("OpenRegistry", "Open in Registry Editor"), AppOpenRegistry_Click, !string.IsNullOrWhiteSpace(app.RegistryPath));
            Item(AppText("CheckUpdate", "Check for a newer version"), AppCheckUpdate_Click);
            menu.Items.Add(new System.Windows.Controls.Separator());
            Item(AppText("ProgramsAndFeatures", "Programs & Features"), OpenAddRemovePrograms_Click);
            menu.IsOpen = true;
        }

        private void AppCopyDetails_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp app) return;
            try
            {
                System.Windows.Clipboard.SetText(AppCommands.ClipboardText(app.Name, app.Version, app.Publisher, app.InstallDate,
                    app.InstallLocation, app.RegistryPath, app.UninstallKey));
                ShowToastSuccess(AppText("Copied", "Copied to clipboard"));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "AppCopyDetails_Click"); }
        }

        /// <summary>Opens Registry Editor directly on the program's key: the editor starts at the last visited key, which it
        /// keeps in its own settings — the desired key is written there and a new window is started.</summary>
        private void AppOpenRegistry_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp app || string.IsNullOrWhiteSpace(app.RegistryPath)) return;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    key?.SetValue("LastKey", @"Computer\" + app.RegistryPath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("regedit.exe", "/m") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "AppOpenRegistry_Click");
                ShowToastWarning(LanguageService.S("Common", "ErrorPrefix") + ex.Message);
            }
        }

        private void AppCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp app) return;
            try { SearchGoogle($"\"{app.Name}\" {app.Publisher} latest version download"); }
            catch (Exception ex) { AppLogger.Warning(ex, "AppCheckUpdate_Click"); }
        }

        // ── note ──
        private void AppNotes_LostFocus(object sender, RoutedEventArgs e) => SaveAppNote();

        /// <summary>Saves the note of the program shown (on leaving the box and on choosing another program).</summary>
        private void SaveAppNote()
        {
            try
            {
                if (_appNoteFor is not InstalledApp app || FindName("TxtAppNotes") is not System.Windows.Controls.TextBox notes) return;
                if (!AppNotes.Set(app.Name, app.Publisher, notes.Text)) return;
                app.HasNote = AppNotes.Has(app.Name, app.Publisher);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SaveAppNote"); }
        }
    }
}
