using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SMDWin.Services;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using HAlign = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBlock = System.Windows.Controls.TextBlock;
using VAlign = System.Windows.VerticalAlignment;
using Window = System.Windows.Window;

namespace SMDWin.Views
{
    /// <summary>
    /// "Windows on another drive": choose which Windows the Event Viewer and BSOD pages read — this PC, or a Windows found on
    /// another drive (the internal disk of a PC started from Hiren's BootCD PE / a rescue stick, or the disk of another PC on
    /// USB). For the chosen one: what its System log says about the last starts, stops and crashes, and offline checks
    /// (system files, Windows image, the disk), each in its own console window.
    /// </summary>
    public sealed class OfflineWindowsDialog : Window
    {
        private readonly List<WindowsInstall> _found;
        private WindowsInstall? _choice;
        private readonly StackPanel _diag = new();
        private readonly StackPanel _repair = new();
        private CancellationTokenSource? _cts;

        private static string P(string en, params object[] a) => MainWindow.PT(en, a);

        public OfflineWindowsDialog(List<WindowsInstall> found)
        {
            _found = found;
            _choice = AnalyzedWindows.Offline;
            Title = P("Windows on another drive");
            Width = 640; SizeToContent = SizeToContent.Height; MaxHeight = 760;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Content = Build();
            Loaded += (_, _) => _ = RefreshDiagnosisAsync();
            Closed += (_, _) => _cts?.Cancel();
        }

        private static TextBlock T(string text, string brush = "TextPrimaryBrush", double size = 0, bool bold = false, bool wrap = true)
        {
            var t = new TextBlock { Text = text, FontSize = size > 0 ? size : Ui.FontM, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                                    TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        private UIElement Build()
        {
            var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(22, 18, 22, 18) };
            frame.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            frame.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is Border) DragMove(); };
            var root = new StackPanel();
            frame.Child = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var close = new Button { Content = "✕", Width = 30, Height = 28, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
            close.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            close.Click += (_, _) => Close();
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            head.Children.Add(T(P("Windows on another drive"), size: Ui.FontXL, bold: true));
            root.Children.Add(head);
            root.Children.Add(T(P("SMD Win can read the event logs and blue screens of a Windows that is not running: the internal disk of a PC started from a rescue stick (for example Hiren's BootCD PE), or the disk of another PC connected by USB. Nothing is changed on that Windows."), "TextSecondaryBrush", Ui.FontS));

            // the choice
            var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            RadioButton Opt(string title, string sub, WindowsInstall? w)
            {
                var sp = new StackPanel();
                sp.Children.Add(T(title, bold: true, wrap: false));
                sp.Children.Add(T(sub, "TextSecondaryBrush", Ui.FontS));
                var rb = new RadioButton { Content = sp, GroupName = "win", Margin = new Thickness(0, 4, 0, 4), IsChecked = Equals(_choice?.Root, w?.Root), Cursor = Cursors.Hand };
                rb.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                rb.Checked += (_, _) => { _choice = w; _ = RefreshDiagnosisAsync(); };
                return rb;
            }
            bool pe = AnalyzedWindows.RunningInWinPE();
            list.Children.Add(Opt(P("This PC"), pe ? P("The Windows SMD Win runs on now (the rescue stick)") : P("The Windows SMD Win runs on now"), null));
            var culture = CultureInfo.CurrentUICulture;
            foreach (var w in _found)
            {
                string label = w.VolumeLabel.Length > 0 ? $" ({w.VolumeLabel})" : "";
                string sub = $"{w.Letter}{label} · {DisplayFormat.SizeGb(w.SizeBytes / 1e9)}" + (w.Version.Length > 0 ? $" · {w.Version}" : "")
                           + (w.LastUsed is DateTime lu ? " · " + P("last used {0}", lu.ToString("d MMM yyyy", culture)) : "");
                list.Children.Add(Opt($"{w.Name} — {w.Letter}", sub, w));
            }
            if (_found.Count == 0)
                list.Children.Add(T(P("No other Windows found on the connected drives. Connect the disk (USB adapter or dock) and open this window again."), "TextSecondaryBrush", Ui.FontS));
            root.Children.Add(list);

            // what its log says
            root.Children.Add(Section(P("What the System log says (last 90 days)")));
            root.Children.Add(_diag);
            // offline checks
            _repair.Margin = new Thickness(0, 4, 0, 0);
            root.Children.Add(_repair);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Wpf.Ui.Controls.Button { Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Content = P("Close"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
            cancel.Click += (_, _) => Close();
            var apply = new Wpf.Ui.Controls.Button { Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Content = P("Read this Windows"), Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand, IsDefault = true };
            apply.Click += (_, _) => { AnalyzedWindows.Set(_choice); DialogResult = true; };
            buttons.Children.Add(cancel); buttons.Children.Add(apply);
            root.Children.Add(buttons);
            return frame;
        }

        private static TextBlock Section(string text)
        {
            var t = T(text.ToUpper(CultureInfo.CurrentUICulture), "TextSecondaryBrush", Ui.FontXS, bold: true);
            t.Margin = new Thickness(0, 16, 0, 6);
            return t;
        }

        private async Task RefreshDiagnosisAsync()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = _cts.Token;
            _diag.Children.Clear();
            _diag.Children.Add(T(P("Reading…"), "TextSecondaryBrush", Ui.FontS));
            BuildRepair();
            // the log of the chosen Windows, read without changing the pages yet
            var d = await AnalyzedWindows.DiagnoseAsync(_choice?.Root, ct);
            if (ct.IsCancellationRequested) return;
            _diag.Children.Clear();
            if (d.LogMissing) { _diag.Children.Add(T(P("The System log of this Windows could not be read."), "StatusWarnBrush", Ui.FontS)); return; }
            var culture = CultureInfo.CurrentUICulture;
            string D(DateTime? t) => t is DateTime x ? x.ToString("d MMM yyyy, HH:mm", culture) : "—";
            void Row(string k, string v, string brush = "TextPrimaryBrush")
            {
                var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var vt = T(v, brush, Ui.FontS, bold: true, wrap: false); DockPanel.SetDock(vt, Dock.Right); dp.Children.Add(vt);
                dp.Children.Add(T(k, "TextSecondaryBrush", Ui.FontS, wrap: false));
                _diag.Children.Add(dp);
            }
            Row(P("Last start"), D(d.LastBoot));
            Row(P("Last normal shutdown"), D(d.LastCleanShutdown));
            Row(P("Unexpected shutdowns"), d.Unexpected == 0 ? "0" : $"{d.Unexpected} · " + P("last: {0}", D(d.LastUnexpected)), d.Unexpected > 0 ? "StatusWarnBrush" : "TextPrimaryBrush");
            Row(P("Blue screens"), d.BlueScreens == 0 ? "0" : $"{d.BlueScreens} · " + P("last: {0}", D(d.LastBlueScreen)), d.BlueScreens > 0 ? "StatusCritBrush" : "TextPrimaryBrush");
            Row(P("Disk errors"), d.DiskErrors.ToString(culture), d.DiskErrors > 0 ? "StatusCritBrush" : "TextPrimaryBrush");
            Row(P("Hardware errors (WHEA)"), d.HardwareErrors.ToString(culture), d.HardwareErrors > 0 ? "StatusWarnBrush" : "TextPrimaryBrush");

            // the likely story, in one sentence
            string verdict = d.BlueScreens > 0 ? P("It crashed with blue screens — the BSOD page shows each one with its cause and what to do.")
                : d.DiskErrors > 0 ? P("The disk reported read / write errors — check its health (Storage › SMART) and copy the important files first.")
                : d.Unexpected > 0 ? P("It turned off suddenly without a blue screen — most often power (power supply, battery, cable), overheating or a hard reset with the button.")
                : d.HardwareErrors > 0 ? P("The hardware reported corrected errors — check temperatures and remove any overclock.")
                : P("Nothing serious in the last 90 days.");
            var vtx = T(verdict, d.BlueScreens + d.DiskErrors > 0 ? "StatusCritBrush" : d.Unexpected + d.HardwareErrors > 0 ? "StatusWarnBrush" : "TextPrimaryBrush", Ui.FontS);
            vtx.Margin = new Thickness(0, 8, 0, 0);
            _diag.Children.Add(vtx);
        }

        private void BuildRepair()
        {
            _repair.Children.Clear();
            if (_choice is not WindowsInstall w) return;
            _repair.Children.Add(Section(P("Checks on that Windows (offline)")));
            _repair.Children.Add(T(P("Each check opens in its own window and can take 10–30 minutes. SMD Win must run as administrator."), "TextSecondaryBrush", Ui.FontS));
            string root = w.Root.TrimEnd('\\');
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            void Btn(string text, string tip, string command)
            {
                var b = new Wpf.Ui.Controls.Button { Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Content = text, ToolTip = tip + "\n" + command, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6), Cursor = Cursors.Hand };
                b.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo("cmd.exe", "/k " + command) { UseShellExecute = true, Verb = "runas" }); }
                    catch (Exception ex) { AppLogger.Warning(ex, "OfflineWindows check"); }
                };
                wrap.Children.Add(b);
            }
            Btn(P("System files (SFC)"), P("Checks and repairs the Windows system files of that drive."), $"sfc /scannow /offbootdir={root}\\ /offwindir={root}\\Windows");
            Btn(P("Windows image (DISM)"), P("Checks whether the Windows component store of that drive is damaged (read-only)."), $"DISM /Image:{root}\\ /Cleanup-Image /ScanHealth");
            Btn(P("Disk check (CHKDSK)"), P("Reads the file system of that drive and reports errors without changing anything."), $"chkdsk {root}");
            _repair.Children.Add(wrap);
        }
    }
}
