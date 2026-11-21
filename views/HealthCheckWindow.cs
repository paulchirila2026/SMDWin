using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;
using Button      = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HAlign      = System.Windows.HorizontalAlignment;
using VAlign      = System.Windows.VerticalAlignment;
// the state dots are Windows-style marks (tick / ! / i in a filled circle)
using Ellipse     = SMDWin.Views.StatusMark;
using WPath       = System.Windows.Shapes.Path;
using UGrid       = System.Windows.Controls.Primitives.UniformGrid;
using Phase       = SMDWin.Services.DiagnosticPhase;
using Sev         = SMDWin.Services.DiagnosticSeverity;

namespace SMDWin.Views
{
    /// <summary>
    /// System Health Check: the verdict on top (an "x of y" dial) and "Needs attention" with what to do, the 4 groups as
    /// cards below. All checks finish within a few seconds: the processor, graphics, throttling and memory are not tested
    /// here — the latest tests from Temp &amp; Stress / RAM are read (TestResultStore). A test that has not run has a
    /// "Run test" button leading to its page.
    /// </summary>
    public class HealthCheckWindow : Window
    {
        private sealed record Group(string Caption, string Icon, Phase[] Phases);

        private static readonly Group[] Groups =
        {
            new("SECURITY", "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z",
                new[] { Phase.WindowsActivation, Phase.Security, Phase.Firewall, Phase.Antivirus, Phase.WindowsUpdate }),
            new("STORAGE", "M4 5a8 3 0 1 0 16 0a8 3 0 1 0 -16 0 M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5 M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3",
                new[] { Phase.StorageHealth, Phase.FreeSpace, Phase.DiskTemperature, Phase.RestorePoints }),
            new("HARDWARE", "M9 9h6v6H9z M5 5h14v14H5z M9 2v3 M15 2v3 M9 19v3 M15 19v3 M2 9h3 M2 15h3 M19 9h3 M19 15h3",
                new[] { Phase.MissingDrivers, Phase.Cpu, Phase.Throttling, Phase.Gpu, Phase.Ram, Phase.RamDiagnosticHistory, Phase.BatteryHealth }),
            new("SYSTEM", "M6 3h9l4 4v14H6z M9 12h7 M9 16h5",
                new[] { Phase.CriticalServices, Phase.Uptime, Phase.EventLogErrors, Phase.BsodHistory, Phase.NetworkDriverErrors, Phase.ClockSync }),
        };

        private static readonly Dictionary<Phase, string> Names = new()
        {
            [Phase.WindowsActivation] = "Windows activation", [Phase.Security] = "Secure Boot & TPM", [Phase.Firewall] = "Firewall",
            [Phase.Antivirus] = "Antivirus", [Phase.WindowsUpdate] = "Windows Update",
            [Phase.StorageHealth] = "Drive health", [Phase.FreeSpace] = "Free space", [Phase.DiskTemperature] = "Drive temperature",
            [Phase.RestorePoints] = "Restore points",
            [Phase.MissingDrivers] = "Drivers", [Phase.Cpu] = "Processor under load", [Phase.Throttling] = "Processor slowdown",
            [Phase.Gpu] = "Graphics under load", [Phase.Ram] = "Memory test", [Phase.RamDiagnosticHistory] = "Windows Memory Diagnostic",
            [Phase.BatteryHealth] = "Battery",
            [Phase.CriticalServices] = "Core services", [Phase.Uptime] = "Uptime", [Phase.EventLogErrors] = "Event log",
            [Phase.BsodHistory] = "Blue screens", [Phase.NetworkDriverErrors] = "Network adapter", [Phase.ClockSync] = "Clock",
        };

        /// <summary>What the problem means and what to do — the row in "Needs attention".</summary>
        private static readonly Dictionary<Phase, string> Advice = new()
        {
            [Phase.WindowsActivation] = "Windows is not activated, so some settings stay locked.",
            [Phase.Security] = "Secure Boot or TPM is off. The PC works normally — only Windows 11 upgrades and some games need them (turned on in the BIOS).",
            [Phase.Firewall] = "The firewall is off, so the PC accepts connections it should block.",
            [Phase.Antivirus] = "No active antivirus. Turning Microsoft Defender back on is enough.",
            [Phase.WindowsUpdate] = "Updates are waiting. Install them and restart when convenient.",
            [Phase.StorageHealth] = "A drive reports problems. Back up important files soon.",
            [Phase.FreeSpace] = "The system drive is almost full — Windows slows down and updates can fail.",
            [Phase.DiskTemperature] = "A drive runs hot. Check the airflow around it.",
            [Phase.RestorePoints] = "System Restore is off, so there is no way back after a bad update or driver.",
            [Phase.MissingDrivers] = "A device has no working driver.",
            [Phase.Cpu] = "The processor got too hot or made errors under load. Clean the cooler, renew the thermal paste.",
            [Phase.Throttling] = "The processor slows itself down — usually heat or a power limit.",
            [Phase.Gpu] = "The graphics card got too hot or gave wrong results under load.",
            [Phase.Ram] = "The memory test found errors. Test each module on its own to find the bad one.",
            [Phase.RamDiagnosticHistory] = "Windows Memory Diagnostic found errors.",
            [Phase.BatteryHealth] = "The battery has lost a large part of its capacity.",
            [Phase.CriticalServices] = "An important Windows service is stopped.",
            [Phase.Uptime] = "The PC hasn't restarted in a long time. A restart finishes updates and frees memory.",
            [Phase.EventLogErrors] = "Windows logged serious errors recently.",
            [Phase.BsodHistory] = "The PC crashed with a blue screen recently.",
            [Phase.NetworkDriverErrors] = "The network adapter's driver reported errors.",
            [Phase.ClockSync] = "The clock is not synced — websites and updates can fail.",
        };

        private sealed class Row
        {
            public Phase Phase;
            public string Name = "";
            public Border Container = null!;
            public Ellipse Dot = null!;
            public TextBlock Result = null!;
            public Grid Pill = null!;
            public StatusMark PillBg = null!;
            public TextBlock PillText = null!;
            public Sev? Severity;
            public bool Skip, NotTested, Info;
            public string Summary = "";
        }

        private sealed class GroupCard
        {
            public Group Group = null!;
            public Border Card = null!;
            public Ellipse StatusDot = null!;
            public TextBlock Status = null!, Count = null!;
            public UGrid Strip = null!;   // one block per check, coloured by state
            public List<Row> Rows = new();
        }

        private readonly Dictionary<Phase, Row> _rows = new();
        private readonly List<ScrollViewer> _groupScrolls = new();
        private UIElement? _fitRoot;
        private bool _fitQueued, _fitting;

        // ── No scrolling: when a group's rows do not fit, the whole window moves to a more compact step ──
        // 0 = normal, 1 = lower rows, 2 = even lower, result on a single line (the full text stays in the tooltip).
        private static readonly (double Pad, double MinH, int Lines)[] FitLevels = { (8, 56, 2), (5, 46, 2), (4, 40, 1), (2, 34, 1) };

        private bool AnyGroupScrolls() => _groupScrolls.Any(sv => sv.ExtentHeight > sv.ViewportHeight + 0.5 && sv.ViewportHeight > 0);

        /// <summary>On tall windows each group's rows stretch over the whole card height (at most 16 px more per row),
        /// instead of staying stuck to the top with a gap below.</summary>
        private void SpreadGroupRows()
        {
            for (int i = 0; i < _cards.Count && i < _groupScrolls.Count; i++)
            {
                var sv = _groupScrolls[i];
                var rows = _cards[i].Rows;
                if (rows.Count == 0 || sv.ViewportHeight <= 0) continue;
                double extra = sv.ViewportHeight - sv.ExtentHeight;
                if (extra < 6) continue;
                double add = Math.Min(16, extra / rows.Count);
                foreach (var r in rows) r.Container.MinHeight = Math.Max(r.Container.MinHeight, r.Container.ActualHeight + add);
            }
        }

        private void QueueFit()
        {
            if (_fitQueued) return;
            _fitQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { _fitQueued = false; FitGroups(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void FitGroups()
        {
            if (_fitting || _fitRoot == null || !IsLoaded) return;
            _fitting = true;
            try
            {
                for (int lvl = 0; lvl < FitLevels.Length; lvl++)
                {
                    var (pad, minH, lines) = FitLevels[lvl];
                    foreach (var r in _rows.Values)
                    {
                        r.Container.Padding = new Thickness(0, pad, 0, pad);
                        r.Container.MinHeight = minH;
                        r.Result.MaxHeight = Ui.FontS * (lines == 2 ? 2.9 : 1.5);
                    }
                    _fitRoot.UpdateLayout();
                    if (!AnyGroupScrolls()) break;
                }
                SpreadGroupRows();
            }
            finally { _fitting = false; }
        }
        private readonly List<GroupCard> _cards = new();
        private readonly DiagnosticOrchestrator _orchestrator = new();
        private readonly MainWindow? _owner;
        private readonly string _driveLetter;
        private CancellationTokenSource? _cts;
        private bool _running;
        private readonly Stopwatch _sw = new();

        private TextBlock _txtStatus = null!;
        private Button _btnRerun = null!;
        private Button _btnCopy = null!;
        private PercentGauge _gauge = null!;
        private TextBlock _txtPassed = null!, _txtPassedUnit = null!, _txtPassedGhost = null!;
        private TextBlock _txtVerdictName = null!, _txtVerdictStatus = null!, _txtVerdictText = null!;
        private Ellipse _verdictDot = null!;
        private StackPanel _legend = null!;
        private DockPanel _verdictFoot = null!;
        private TextBlock _txtVerdictFoot = null!;
        private Button _btnMissing = null!;
        private StackPanel _attentionList = null!;
        private Grid _attentionHost = null!;
        private TextBlock? _attentionMore;
        private TextBlock _attentionCount = null!;

        public HealthCheckWindow(string driveLetter, MainWindow? owner = null)
        {
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            _owner = owner;
            _driveLetter = driveLetter;
            double h = Math.Clamp(SystemParameters.WorkArea.Height - 40, 640, 900);
            double w = Math.Clamp(SystemParameters.WorkArea.Width - 40, 980, 1220);
            Net.NetWindowHelper.SetupWindow(this, P("System Health Check") + " — SMD Win", w, h);
            Content = Net.NetWindowHelper.WindowShell(BuildUI());
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this); Ui.UseAppScrollBars(this);
            Ui.FixOwnedWindowMinimize(this, _owner);

            _orchestrator.PhaseCompleted += r => Dispatcher.Invoke(() => OnPhaseCompleted(r));
            Loaded += (_, _) => SMDWin.Services.Tx.Tree(this);
            Loaded += async (_, _) => await RunAsync(force: false);
            Closing += (_, _) => { if (_running) _cts?.Cancel(); };
        }

        // ══ Interface pieces ════════════════════════════════════════════════════════
        private static TextBlock Txt(string text, string brush, double size, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        private static FrameworkElement CardIcon(string data, double size = 20)
        {
            var p = new WPath { Width = 24, Height = 24 };
            SMDWin.Services.FluentIcons.SetIcon(p, data);
            return new Viewbox { Width = size, Height = size, Child = p, VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 11, 0) };
        }

        private static Ellipse Dot(string brush)
        {
            var e = new Ellipse { Width = 14, Height = 14, VerticalAlignment = VAlign.Center };
            e.SetResourceReference(StatusMark.FillProperty, brush);
            return e;
        }

        private static Border Card(UIElement child, Thickness? pad = null)
        {
            var b = new Border { Child = child, Cursor = System.Windows.Input.Cursors.Arrow };
            b.SetResourceReference(StyleProperty, "CompCardStyle");
            if (pad.HasValue) b.Padding = pad.Value;
            return b;
        }

        /// <summary>The card header used throughout the app: icon · CAPTION + name · (right) the state.</summary>
        private static DockPanel CardHead(string icon, string caption, TextBlock name, UIElement? right = null)
        {
            var d = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
            if (right != null) { DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); }
            var ic = CardIcon(icon); DockPanel.SetDock(ic, Dock.Left); d.Children.Add(ic);
            // single-row header: the card title + the summary next to it ("Security  5 checks")
            // the summary shortens with "…" when the state on the right needs the room (a narrow card: "6 checks" was cut
            // to "6" with the state dot right after it)
            var names = new DockPanel { VerticalAlignment = VAlign.Center, LastChildFill = true };
            var cap = new TextBlock { Text = SMDWin.Services.DisplayFormat.SentenceCase(caption) };
            cap.SetResourceReference(StyleProperty, "DashRowName");
            DockPanel.SetDock(cap, Dock.Left);
            names.Children.Add(cap);
            name.SetResourceReference(StyleProperty, "DashRowInlineSub");
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(name.Margin.Left, name.Margin.Top, 10, name.Margin.Bottom);
            names.Children.Add(name);
            // a count cut to "6 ver…" or squeezed to a few pixels says nothing: it is hidden (kept in the layout) until it fits
            void FitSummary()
            {
                if (string.IsNullOrEmpty(name.Text)) return;
                try
                {
                    var ft = new FormattedText(name.Text, System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                        new Typeface(name.FontFamily, name.FontStyle, name.FontWeight, name.FontStretch), name.FontSize, System.Windows.Media.Brushes.Black,
                        VisualTreeHelper.GetDpi(name).PixelsPerDip);
                    name.Opacity = name.ActualWidth + 1 >= ft.WidthIncludingTrailingWhitespace ? 1 : 0;
                }
                catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"Health FitSummary: {ex.Message}"); }
            }
            name.SizeChanged += (_, _) => FitSummary();
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                ?.AddValueChanged(name, (_, _) => name.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(FitSummary)));
            d.Children.Add(names);
            return d;
        }

        private static StackPanel StatusLabel(out Ellipse dot, out TextBlock text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VAlign.Center };
            dot = Dot("TextSecondaryBrush"); dot.Margin = new Thickness(0, 0, 6, 0);
            text = new TextBlock { Text = "" };
            text.SetResourceReference(StyleProperty, "CompStatusText");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(dot); sp.Children.Add(text);
            return sp;
        }

        // the window's texts in the user's language (PageText keys, like the rest of the app)
        private static string P(string en) => SMDWin.Views.MainWindow.PT(en);
        private static string P(string en, params object?[] args) => SMDWin.Views.MainWindow.PT(en, args);

        private static void SetStatus(Ellipse dot, TextBlock text, string label, string brush)
        {
            text.Text = label;
            text.SetResourceReference(TextBlock.ForegroundProperty, brush);
            dot.SetResourceReference(StatusMark.FillProperty, brush);
        }

        private static string BrushOf(Sev s) => s switch { Sev.Critical => "StatusCritBrush", Sev.Warning => "StatusWarnBrush", _ => "StatusOkBrush" };

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(22, 16, 22, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ── antet: titlu + stare · Copy report · Re-run · inchidere ──
            var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent, LastChildFill = true };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VAlign.Center };
            _btnCopy = Net.NetWindowHelper.SecondaryButton(P("Copy report"), P("Copies every check and its result as text"));
            _btnCopy.Click += (_, _) => CopyReport();
            _btnRerun = Net.NetWindowHelper.PrimaryButton(P("Re-run"), P("Run all the checks again"));
            _btnRerun.Margin = new Thickness(8, 0, 0, 0);
            _btnRerun.Click += (_, _) => { if (!_running) _ = RunAsync(force: true); };
            var close = Net.NetWindowHelper.CloseIconButton(32);
            close.Margin = new Thickness(8, 0, 0, 0);
            close.Click += (_, _) => Close();
            btns.Children.Add(_btnCopy); btns.Children.Add(_btnRerun); btns.Children.Add(close);
            DockPanel.SetDock(btns, Dock.Right); hdr.Children.Add(btns);
            var shield = CardIcon("M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z M9 12l2 2 4-4", 24);
            DockPanel.SetDock(shield, Dock.Left); hdr.Children.Add(shield);
            var titles = new StackPanel { VerticalAlignment = VAlign.Center };
            var title = Txt("System Health Check", "TextPrimaryBrush", 24, FontWeights.SemiBold);
            title.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
            titles.Children.Add(title);
            _txtStatus = Txt("Starting…", "TextSecondaryBrush", Ui.FontS);
            titles.Children.Add(_txtStatus);
            hdr.Children.Add(titles);
            Net.NetWindowHelper.MakeDraggable(hdr, this);
            root.Children.Add(hdr);

            // ── row 1: the verdict | what to do ──
            var top = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });   // two equal cards
            var verdict = BuildVerdictCard();
            top.Children.Add(verdict);
            var att = BuildAttentionCard();
            att.VerticalAlignment = VAlign.Stretch; verdict.VerticalAlignment = VAlign.Stretch;   // same row: same height
            Grid.SetColumn(att, 2); top.Children.Add(att);
            Grid.SetRow(top, 1); root.Children.Add(top);

            // ── row 2: the 4 groups ──
            var groups = new UGrid { Rows = 1, Columns = Groups.Length };
            for (int i = 0; i < Groups.Length; i++)
            {
                var gc = BuildGroupCard(Groups[i]);
                gc.Card.Margin = new Thickness(i == 0 ? 0 : 6, 0, i == Groups.Length - 1 ? 0 : 6, 0);
                groups.Children.Add(gc.Card);
                _cards.Add(gc);
            }
            Grid.SetRow(groups, 2); root.Children.Add(groups);
            _fitRoot = root;
            groups.SizeChanged += (_, _) => { ArrangeGroups(groups); QueueFit(); };
            Loaded += (_, _) => QueueFit();
            return root;
        }

        // Below 1150 px the 4 groups go to 2 × 2 (otherwise the text in the columns would be cut).
        private void ArrangeGroups(UGrid groups)
        {
            bool two = groups.ActualWidth > 0 && groups.ActualWidth < 1150 && Groups.Length == 4;
            int cols = two ? 2 : Groups.Length, rows = two ? 2 : 1;
            if (groups.Columns == cols && groups.Rows == rows) return;
            groups.Columns = cols; groups.Rows = rows;
            for (int i = 0; i < _cards.Count; i++)
            {
                int c = i % cols, r = i / cols;
                _cards[i].Card.Margin = new Thickness(c == 0 ? 0 : 6, r == 0 ? 0 : 6, c == cols - 1 ? 0 : 6, r == rows - 1 ? 0 : 6);
            }
        }

        private Border BuildVerdictCard()
        {
            _txtVerdictName = new TextBlock { Text = P("Checking…") };
            var status = StatusLabel(out _verdictDot, out _txtVerdictStatus);
            var sp = new StackPanel();
            sp.Children.Add(CardHead("M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z", P("Verdict").ToUpper(System.Globalization.CultureInfo.CurrentUICulture), _txtVerdictName, status));

            var body = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            var dial = new Grid();
            dial.SetResourceReference(StyleProperty, "CompDialGrid");
            _gauge = new PercentGauge { ShowValue = false, Segments = 20, WarnAt = 101, CritAt = 101, HasValue = false };
            dial.Children.Add(_gauge);
            var vt = new StackPanel();
            vt.SetResourceReference(StyleProperty, "CompDialText");
            _txtPassedGhost = new TextBlock(); _txtPassedGhost.SetResourceReference(StyleProperty, "CompDialUnitGhost");
            _txtPassed = new TextBlock { Text = "—" }; _txtPassed.SetResourceReference(StyleProperty, "CompDialValue");
            _txtPassedUnit = new TextBlock(); _txtPassedUnit.SetResourceReference(StyleProperty, "CompDialUnit");
            vt.Children.Add(_txtPassedGhost); vt.Children.Add(_txtPassed); vt.Children.Add(_txtPassedUnit);
            dial.Children.Add(vt);
            var sub = new TextBlock { Text = P("passed") }; sub.SetResourceReference(StyleProperty, "CompDialSub");
            dial.Children.Add(sub);
            body.Children.Add(new Viewbox { Width = 108, Height = 98, Child = dial, Margin = new Thickness(0, 0, 18, 0) });

            var right = new StackPanel { VerticalAlignment = VAlign.Center };
            _txtVerdictText = Txt("", "TextPrimaryBrush", Ui.FontM);
            _txtVerdictText.TextWrapping = TextWrapping.Wrap;
            right.Children.Add(_txtVerdictText);
            _legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            right.Children.Add(_legend);
            Grid.SetColumn(right, 1); body.Children.Add(right);
            sp.Children.Add(body);

            // footer: the last check + "Run missing tests" (goes to the page of the first test that has not run)
            _txtVerdictFoot = Txt("", "TextSecondaryBrush", Ui.FontS);
            _txtVerdictFoot.VerticalAlignment = VAlign.Center;
            _btnMissing = Net.NetWindowHelper.SecondaryButton(P("Run missing tests"), P("Opens the page of the first test that has not been run yet"));
            _btnMissing.Visibility = Visibility.Collapsed;
            // a compact button on the line of the footer text: the standard size stuck out under the card edge
            _btnMissing.Padding = new Thickness(12, 3, 12, 3);
            _btnMissing.FontSize = Ui.FontS;
            _btnMissing.MinHeight = 0;
            _btnMissing.VerticalAlignment = VAlign.Center;
            _btnMissing.Click += (_, _) => { if (_btnMissing.Tag is Phase ph) Open(ph); };
            DockPanel.SetDock(_btnMissing, Dock.Right);
            _verdictFoot = new DockPanel { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            _verdictFoot.Children.Add(_btnMissing);
            _verdictFoot.Children.Add(_txtVerdictFoot);
            sp.Children.Add(_verdictFoot);

            var dock = new DockPanel { LastChildFill = true };
            dock.Children.Add(sp);
            return Card(dock);
        }

        private Border BuildAttentionCard()
        {
            var name = new TextBlock { Text = P("What to do, most important first") };
            _attentionCount = Txt("", "TextSecondaryBrush", Ui.FontS, FontWeights.SemiBold);
            _attentionCount.VerticalAlignment = VAlign.Center;
            var sp = new DockPanel { LastChildFill = true };
            var head = CardHead("M13 2L4 14h7l-2 8 11-13h-7z", P("Needs attention").ToUpper(System.Globalization.CultureInfo.CurrentUICulture), name, _attentionCount);
            DockPanel.SetDock(head, Dock.Top); sp.Children.Add(head);
            // no scroll: rows that do not fit are hidden and the last row says how many more there are (FitAttention)
            _attentionList = new StackPanel { VerticalAlignment = VAlign.Top };
            _attentionHost = new Grid { ClipToBounds = true };
            _attentionHost.Children.Add(_attentionList);
            _attentionHost.SizeChanged += (_, _) => FitAttention();
            sp.Children.Add(_attentionHost);
            return Card(sp, new Thickness(18, 14, 18, 8));
        }

        private GroupCard BuildGroupCard(Group g)
        {
            var gc = new GroupCard { Group = g };
            gc.Count = new TextBlock { Text = $"{g.Phases.Length} checks" };
            var status = StatusLabel(out gc.StatusDot, out gc.Status);
            var dock = new DockPanel { LastChildFill = true };
            var head = CardHead(g.Icon, P(g.Caption).ToUpper(System.Globalization.CultureInfo.CurrentUICulture), gc.Count, status);
            DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
            gc.Strip = new UGrid { Rows = 1, Height = 6, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(gc.Strip, Dock.Top); dock.Children.Add(gc.Strip);
            var list = new StackPanel();
            foreach (var ph in g.Phases)
            {
                var row = BuildRow(ph);
                _rows[ph] = row; gc.Rows.Add(row);
                list.Children.Add(row.Container);
            }
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
            _groupScrolls.Add(sv);
            dock.Children.Add(sv);
            gc.Card = Card(dock, new Thickness(18, 14, 18, 6));
            return gc;
        }

        private Row BuildRow(Phase ph)
        {
            var row = new Row { Phase = ph, Name = Names.TryGetValue(ph, out var n) ? P(n) : ph.ToString() };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.Dot = Dot("BorderBrush2");
            g.Children.Add(row.Dot);

            var texts = new StackPanel { VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 8, 0) };
            texts.Children.Add(Txt(row.Name, "TextPrimaryBrush", Ui.FontM, FontWeights.SemiBold));
            row.Result = Txt("Waiting…", "TextSecondaryBrush", Ui.FontS);
            // the result may take two lines (no longer cut after one line); the rest stays in the tooltip
            row.Result.TextWrapping = TextWrapping.Wrap;
            row.Result.MaxHeight = Ui.FontS * 2.9;
            texts.Children.Add(row.Result);
            Grid.SetColumn(texts, 1); g.Children.Add(texts);

            // the CHECK / WARNING pill (only when not OK) or the "Run test" button (test not run)
            row.Pill = new Grid { VerticalAlignment = VAlign.Center, Visibility = Visibility.Collapsed };
            // the Windows way: a small mark (! / i in a filled circle) and the coloured word, no tinted box
            row.PillBg = new StatusMark { Width = 13, Height = 13, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VAlign.Center };
            row.PillText = new TextBlock { FontSize = Ui.FontXS, FontWeight = FontWeights.Bold, Margin = new Thickness(19, 3, 0, 3) };
            row.Pill.Children.Add(row.PillBg); row.Pill.Children.Add(row.PillText);
            Grid.SetColumn(row.Pill, 2); g.Children.Add(row.Pill);
            // (no "Run test" buttons: a test that has not run just says so; clicking the row opens its page)

            var action = NavAction(ph);
            if (action != null)
            {
                var chev = new WPath { Data = Geometry.Parse("M9 6l6 6-6 6"), Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.8,
                                       HorizontalAlignment = HAlign.Right, VerticalAlignment = VAlign.Center, Opacity = 0.6 };
                chev.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush");
                Grid.SetColumn(chev, 3); g.Children.Add(chev);
            }

            row.Container = new Border { Child = g, Padding = new Thickness(0, 8, 0, 8), MinHeight = 56, BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent };
            row.Container.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            if (action != null)
            {
                row.Container.Cursor = System.Windows.Input.Cursors.Hand;
                row.Container.MouseEnter += (_, _) => row.Container.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                row.Container.MouseLeave += (_, _) => row.Container.Background = Brushes.Transparent;
                row.Container.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) Open(ph); };
            }
            row.Container.MouseRightButtonUp += (_, _) => CopyRow(row);
            return row;
        }

        // ══ Where each check leads ════════════════════════════════════════════════
        private Action? NavAction(Phase ph) => ph switch
        {
            Phase.WindowsActivation => () => _ = _owner?.ShowWindowsInfoDialogPublic(),
            Phase.Firewall => () => _owner?.OpenFirewallPublic(),
            Phase.Antivirus => () => Shell("windowsdefender:"),
            Phase.WindowsUpdate => () => Shell("ms-settings:windowsupdate"),
            Phase.RestorePoints => () => Shell("SystemPropertiesProtection.exe"),
            Phase.StorageHealth or Phase.FreeSpace or Phase.DiskTemperature => () => _ = _owner?.NavigateToPublic("Disk"),
            Phase.BatteryHealth => () => _ = _owner?.NavigateToPublic("Battery"),
            Phase.MissingDrivers => () => _ = _owner?.NavigateToPublic("Drivers"),
            Phase.CriticalServices => () => _ = _owner?.NavigateToPublic("Services"),
            Phase.EventLogErrors or Phase.NetworkDriverErrors => () => _ = _owner?.NavigateToPublic("Events"),
            Phase.BsodHistory => () => _ = _owner?.NavigateToPublic("Crash"),
            Phase.Cpu or Phase.Gpu or Phase.Throttling => () => _ = _owner?.NavigateToPublic("Stress"),
            Phase.Ram or Phase.RamDiagnosticHistory => () => _ = _owner?.NavigateToPublic("Ram"),
            _ => null,
        };

        private static string ActionLabel(Phase ph) => ph switch
        {
            Phase.WindowsActivation => P("Open"), Phase.Firewall => P("Open firewall"), Phase.Antivirus => P("Open security"),
            Phase.WindowsUpdate => P("Open updates"), Phase.RestorePoints => P("Turn on"),
            Phase.StorageHealth or Phase.FreeSpace or Phase.DiskTemperature => P("Open storage"),
            Phase.BatteryHealth => P("Open power"), Phase.MissingDrivers => P("Open drivers"), Phase.CriticalServices => P("Open services"),
            Phase.EventLogErrors or Phase.NetworkDriverErrors => P("Open events"), Phase.BsodHistory => P("Open crash"),
            Phase.Cpu or Phase.Gpu or Phase.Throttling => P("Open Temp & Stress"),
            Phase.Ram or Phase.RamDiagnosticHistory => P("Open RAM"),
            _ => P("Open"),
        };

        private void Open(Phase ph)
        {
            var a = NavAction(ph);
            if (a == null) return;
            bool page = ph is not (Phase.Antivirus or Phase.WindowsUpdate or Phase.RestorePoints);
            if (page) Close();
            try { a(); } catch (Exception ex) { AppLogger.Warning(ex, $"HealthCheckWindow.Open({ph})"); }
        }

        private static void Shell(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, $"HealthCheckWindow.Shell({target})"); }
        }

        // ══ Running ════════════════════════════════════════════════════════════════════
        // The last check is kept for a few minutes: reopening the window shows those results without running again (the WMI /
        // log / update reads take time). "Re-run" bypasses the memory. It is invalidated when the drive changes or when another
        // check (e.g. the background one) wrote a newer result.
        private static List<DiagnosticPhaseResult>? _cachedResults;
        private static DateTime _cachedAt;
        private static int _cachedSeconds;
        private static string _cachedDrive = "";
        private static string _cachedTestStamp = "";
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
        private DateTime _shownRunAt = DateTime.Now;
        private int _shownRunSeconds;

        private static string TestStamp() => SettingsService.Current.HealthCheckLastRunUtc?.ToString("o") ?? "";

        private async Task RunAsync(bool force)
        {
            if (!force && _cachedResults != null && _cachedDrive == _driveLetter
                && DateTime.Now - _cachedAt < CacheLifetime && _cachedTestStamp == TestStamp())
            {
                foreach (var r in _rows.Values) { r.Severity = null; r.Skip = false; r.NotTested = false; r.Info = false; r.Summary = ""; r.Container.Visibility = Visibility.Visible; }
                _shownRunAt = _cachedAt; _shownRunSeconds = _cachedSeconds;
                foreach (var r in _cachedResults) OnPhaseCompleted(r);
                _txtStatus.Text = P("{0} checks · result from {1} · run the check again for fresh values", _cachedResults.Count(r => !r.Skip), _cachedAt.ToString("HH:mm"));
                RefreshSummary();
                return;
            }
            _running = true;
            _btnRerun.IsEnabled = false;
            _btnCopy.IsEnabled = false;
            _cts = new CancellationTokenSource();
            foreach (var r in _rows.Values)
            {
                r.Severity = null; r.Skip = false; r.NotTested = false; r.Info = false; r.Summary = "";
                r.Container.Visibility = Visibility.Visible;
                r.Result.Text = P("Checking…");
                r.Dot.SetResourceReference(StatusMark.FillProperty, "BorderBrush2");
                r.Pill.Visibility = Visibility.Collapsed;
                r.Container.ToolTip = null;
            }
            _txtStatus.Text = P("Checking… this takes a few seconds");
            _sw.Restart();
            RefreshSummary();
            try
            {
                var results = await _orchestrator.RunHealthCheckAsync(_driveLetter, _cts.Token,
                    getThrottleSnapshot: () => _owner?.LastThrottleSnapshotPublic);
                _sw.Stop();
                var counted = results.Where(r => !r.Skip).ToList();
                int critical = counted.Count(r => r.Severity == Sev.Critical);
                int warning = counted.Count(r => r.Severity == Sev.Warning);
                var s = SettingsService.Current;
                s.HealthCheckLastSeverity = critical > 0 ? "Critical" : warning > 0 ? "Warning" : "Ok";
                s.HealthCheckLastCritical = critical;
                s.HealthCheckLastDanger = counted.Any(DiagnosticOrchestrator.IsPhysicalDanger);
                s.HealthCheckLastWarning = warning;
                s.HealthCheckLastRunUtc = DateTime.UtcNow;
                SettingsService.Save();
                _owner?.RefreshHealthCheckBadgePublic();
                _cachedResults = results; _cachedAt = DateTime.Now; _cachedDrive = _driveLetter;
                _cachedSeconds = Math.Max(1, (int)Math.Round(_sw.Elapsed.TotalSeconds));
                _cachedTestStamp = TestStamp();
                _shownRunAt = _cachedAt; _shownRunSeconds = _cachedSeconds;
                _txtStatus.Text = $"{counted.Count} checks · read-only";
                _txtStatus.ToolTip = P("Nothing is changed on the PC. Right-click a row to copy it.");
            }
            catch (OperationCanceledException) { _txtStatus.Text = P("Cancelled."); }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "HealthCheckWindow.RunAsync");
                _txtStatus.Text = P("Something went wrong: {0}", ex.Message);
            }
            finally
            {
                _running = false;
                _btnRerun.IsEnabled = true;
                _btnCopy.IsEnabled = true;
                _sw.Stop();
                RefreshSummary();
            }
        }

        private void OnPhaseCompleted(DiagnosticPhaseResult r)
        {
            if (!_rows.TryGetValue(r.Phase, out var row)) return;
            if (r.Skip)
            {
                row.Skip = true;
                row.Container.Visibility = Visibility.Collapsed;
                RefreshSummary();
                return;
            }
            QueueFit();
            row.Severity = r.Severity;
            row.NotTested = r.Details.ContainsKey("NotTested") && r.Phase != Phase.Throttling;
            row.Summary = r.Summary;
            row.Result.Text = r.Summary;
            row.Result.ToolTip = r.Summary;
            row.Info = !row.NotTested && r.Severity == Sev.Ok && r.Details.ContainsKey("Info");
            string brush = row.NotTested ? "TextSecondaryBrush" : row.Info ? "AccentBrush" : BrushOf(r.Severity);
            row.Dot.SetResourceReference(StatusMark.FillProperty, brush);
            // the state shows through the dot and the result colour; the pill stays only for "OLD TEST" (extra information)
            bool problem = !row.NotTested && r.Severity != Sev.Ok;
            row.Result.SetResourceReference(TextBlock.ForegroundProperty, problem ? brush : "TextSecondaryBrush");
            row.Pill.Visibility = Visibility.Collapsed;
            if (!row.NotTested && r.Details.ContainsKey("Stale"))
            {
                row.PillText.Text = P("Old test").ToUpper(System.Globalization.CultureInfo.CurrentUICulture);
                row.PillText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                row.PillBg.SetResourceReference(StatusMark.FillProperty, "TextSecondaryBrush");
                row.Pill.Visibility = Visibility.Visible;
            }
            row.Container.ToolTip = Details(r);
            RefreshSummary();
        }

        private static object? Details(DiagnosticPhaseResult r)
        {
            var rows = r.Details.Where(kv => kv.Key is not ("NotTested" or "Stale" or "Info")).ToList();
            if (rows.Count == 0) return null;
            var sp = new StackPanel();
            foreach (var kv in rows) sp.Children.Add(new TextBlock { Text = $"{SpaceOut(kv.Key)}: {kv.Value}", FontSize = Ui.FontXS });
            return sp;
        }

        private static string SpaceOut(string key)
        {
            var sb = new StringBuilder();
            foreach (char c in key) { if (char.IsUpper(c) && sb.Length > 0) sb.Append(' '); sb.Append(c); }
            return sb.ToString();
        }

        // ══ Verdict, "Needs attention", the group states ══════════════════════════════════
        private void RefreshSummary()
        {
            try { RefreshSummaryCore(); } finally { SMDWin.Services.Tx.Tree(this); }
        }

        /// <summary>The group's block strip: green = OK, yellow = check, red = warning, grey = not tested / being checked.</summary>
        private static void FillStrip(GroupCard gc, List<Row> rows)
        {
            gc.Strip.Children.Clear();
            gc.Strip.Columns = Math.Max(1, rows.Count);
            foreach (var r in rows)
            {
                string key = r.Severity == null ? "BorderBrush2"
                    : r.NotTested ? "TextSecondaryBrush"
                    : r.Info ? "AccentBrush"
                    : r.Severity == Sev.Critical ? "StatusCritBrush"
                    : r.Severity == Sev.Warning ? "StatusWarnBrush" : "StatusOkBrush";
                var block = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(1, 0, 1, 0), Opacity = r.NotTested ? 0.35 : 1 };
                block.SetResourceReference(Border.BackgroundProperty, key);
                block.ToolTip = r.Name;
                gc.Strip.Children.Add(block);
            }
        }

        private void RefreshSummaryCore()
        {
            var done = _rows.Values.Where(r => r.Severity != null && !r.Skip).ToList();
            var judged = done.Where(r => !r.NotTested && !r.Info).ToList();
            int ok = judged.Count(r => r.Severity == Sev.Ok);
            int warn = judged.Count(r => r.Severity == Sev.Warning);
            int crit = judged.Count(r => r.Severity == Sev.Critical);
            int notTested = done.Count(r => r.NotTested);
            int info = done.Count(r => r.Info);

            // the groups
            foreach (var gc in _cards)
            {
                var vis = gc.Rows.Where(r => !r.Skip).ToList();
                gc.Count.Text = $"{vis.Count} checks";
                FillStrip(gc, vis);
                if (vis.Any(r => r.Severity == null)) { SetStatus(gc.StatusDot, gc.Status, P("Checking"), "TextSecondaryBrush"); continue; }
                int c = vis.Count(r => !r.NotTested && r.Severity == Sev.Critical);
                int w = vis.Count(r => !r.NotTested && r.Severity == Sev.Warning);
                int nt = vis.Count(r => r.NotTested);
                int okCount = vis.Count(r => !r.NotTested && r.Severity == Sev.Ok);
                if (c > 0) SetStatus(gc.StatusDot, gc.Status, c == 1 ? P("1 warning") : P("{0} warnings", c), "StatusCritBrush");
                else if (w > 0) SetStatus(gc.StatusDot, gc.Status, P("{0} to check", w), "StatusWarnBrush");
                else if (nt > 0) SetStatus(gc.StatusDot, gc.Status, P("{0} OK · {1} not tested", okCount, nt), "TextSecondaryBrush");   /// "All clear" only when all have run
                else SetStatus(gc.StatusDot, gc.Status, P("All clear"), "StatusOkBrush");
            }

            // the verdict
            int total = judged.Count;
            _gauge.Busy = _running;   // while the check runs the dial spins: it shows that the app is working
            _gauge.HasValue = total > 0 && !_running;
            _gauge.Value = total > 0 ? ok * 100.0 / total : 0;
            // dial colour = the overall state: red (warnings), yellow (to check), green (all good)
            string gaugeKey = _running || total == 0 ? "AccentBrush" : crit > 0 ? "StatusCritBrush" : warn > 0 ? "StatusWarnBrush" : "StatusOkBrush";
            _gauge.NormalBrush = TryFindResource(gaugeKey) as System.Windows.Media.Brush;
            _gauge.InvalidateVisual();
            _txtPassed.Text = total > 0 && !_running ? ok.ToString() : "—";
            _txtPassedUnit.Text = total > 0 && !_running ? $"/{total}" : "";
            _txtPassedGhost.Text = _txtPassedUnit.Text;
            if (_running)
            {
                _txtVerdictName.Text = P("Checking…");
                SetStatus(_verdictDot, _txtVerdictStatus, P("Checking"), "TextSecondaryBrush");
                _txtVerdictText.Text = P("Reading Windows, the drives and the last test results.");
            }
            else if (crit > 0)
            {
                int attn = crit + warn;   // the same number as the "Needs attention" list
                // a single number, the same as the "Needs attention" list (the severity shows in each row's colour)
                _txtVerdictName.Text = attn == 1 ? P("1 thing to fix") : P("{0} things to fix", attn);
                SetStatus(_verdictDot, _txtVerdictStatus, P("Warning"), "StatusCritBrush");
                _txtVerdictText.Text = P("Start with the first item on the right — it is the most important.");
            }
            else if (warn > 0)
            {
                _txtVerdictName.Text = warn == 1 ? P("Mostly healthy · 1 thing to check") : P("Mostly healthy · {0} things to check", warn);
                SetStatus(_verdictDot, _txtVerdictStatus, P("Needs attention"), "StatusWarnBrush");
                _txtVerdictText.Text = P("Nothing is broken; a few things are worth a look when you have time.");
            }
            else
            {
                _txtVerdictName.Text = P("Healthy · nothing to fix");
                SetStatus(_verdictDot, _txtVerdictStatus, P("Healthy"), "StatusOkBrush");
                _txtVerdictText.Text = notTested > 0 ? P("Everything checked looks good. Run the missing tests for a complete picture.")
                                                     : P("Everything checked looks good.");
            }
            _legend.Children.Clear();
            void Leg(int n, string label, string brush)
            {
                if (n == 0) return;
                var d = Dot(brush); d.Margin = new Thickness(0, 0, 6, 0);
                var t = Txt($"{n} {label}", "TextSecondaryBrush", Ui.FontS);
                t.Margin = new Thickness(0, 0, 16, 0);
                _legend.Children.Add(d); _legend.Children.Add(t);
            }
            if (!_running) { Leg(ok, "OK", "StatusOkBrush"); Leg(warn, P("to check"), "StatusWarnBrush"); Leg(crit, crit == 1 ? P("warning") : P("warnings"), "StatusCritBrush"); Leg(info, P("info"), "AccentBrush"); Leg(notTested, P("not tested"), "TextSecondaryBrush"); }

            // verdict footer: when the check ran, how long it took, and the way to the missing tests
            var firstMissing = done.FirstOrDefault(r => r.NotTested);
            bool showFoot = !_running && done.Count > 0;
            _verdictFoot.Visibility = showFoot ? Visibility.Visible : Visibility.Collapsed;
            if (showFoot)
                _txtVerdictFoot.Text = P("Last run {0:HH:mm} · Took {1} s", _shownRunAt, Math.Max(1, _shownRunSeconds));
            _btnMissing.Visibility = showFoot && firstMissing != null ? Visibility.Visible : Visibility.Collapsed;
            _btnMissing.Tag = firstMissing?.Phase;

            BuildAttention(done);
        }

        private void BuildAttention(List<Row> done)
        {
            _attentionList.Children.Clear();
            _attentionMore = null;
            var bad = done.Where(r => !r.NotTested && r.Severity != Sev.Ok)
                          .OrderByDescending(r => r.Severity == Sev.Critical).ToList();
            int missing = done.Count(r => r.NotTested);
            _attentionCount.Text = bad.Count > 0 ? (bad.Count == 1 ? P("1 item") : P("{0} items", bad.Count)) : "";

            if (_running && bad.Count == 0)
            {
                _attentionList.Children.Add(Txt(P("Checking…"), "TextSecondaryBrush", Ui.FontM));
                return;
            }
            // serious problems keep the explanation under the title; the others have it in the tooltip, so each takes one row
            foreach (var r in bad)
            {
                string why = Advice.TryGetValue(r.Phase, out var a) ? P(a) : "";
                _attentionList.Children.Add(AttentionRow(BrushOf(r.Severity!.Value), $"{r.Name} · {r.Summary}",
                    r.Severity == Sev.Critical ? why : "", why, NavAction(r.Phase) != null ? ActionLabel(r.Phase) : null, r.Phase));
            }
            if (bad.Count == 0)
                _attentionList.Children.Add(AttentionRow("StatusOkBrush", P("Nothing needs your attention"),
                    missing > 0 ? (missing == 1 ? P("1 test not run yet — run it from Temp & Stress or the RAM page.") : P("{0} tests not run yet — run them from Temp & Stress or the RAM page.", missing)) : P("Every check passed."), "", null, null));
            _attentionMore = Txt("", "TextSecondaryBrush", Ui.FontS);
            _attentionMore.Margin = new Thickness(18, 8, 0, 0);
            _attentionMore.Visibility = Visibility.Collapsed;
            _attentionList.Children.Add(_attentionMore);
            Dispatcher.BeginInvoke(new Action(FitAttention), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Hides the "Needs attention" rows that do not fit and writes "+N more" — the card has no scroll.</summary>
        private void FitAttention()
        {
            try
            {
                if (_attentionHost == null || _attentionHost.ActualHeight <= 0) return;
                double avail = _attentionHost.ActualHeight, w = Math.Max(100, _attentionHost.ActualWidth), used = 0;
                var rows = _attentionList.Children.OfType<FrameworkElement>().Where(c => !ReferenceEquals(c, _attentionMore)).ToList();
                foreach (var r in rows) r.Visibility = Visibility.Visible;
                if (_attentionMore != null) _attentionMore.Visibility = Visibility.Collapsed;
                const double moreH = 30;
                int hidden = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    rows[i].Measure(new System.Windows.Size(w, double.PositiveInfinity));
                    double h = rows[i].DesiredSize.Height;
                    bool last = i == rows.Count - 1;
                    if (hidden > 0 || used + h > avail - (last ? 0 : moreH)) { rows[i].Visibility = Visibility.Collapsed; hidden++; }
                    else used += h;
                }
                if (hidden > 0 && _attentionMore != null)
                {
                    _attentionMore.Text = P("+{0} more — see the cards below", hidden);
                    _attentionMore.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"FitAttention: {ex.Message}"); }
        }

        private Border AttentionRow(string brush, string title, string why, string tip, string? button, Phase? ph)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Dot(brush));
            var sp = new StackPanel { VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 10, 0) };
            var t = Txt(title, "TextPrimaryBrush", Ui.FontM, FontWeights.SemiBold);
            // a long finding ("Processor under load · Hot under load: 91 °C · 11 min test on …") wraps instead of being cut
            t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
            sp.Children.Add(t);
            if (why.Length > 0)
            {
                var w = Txt(why, "TextSecondaryBrush", Ui.FontS);
                w.TextWrapping = TextWrapping.Wrap; w.TextTrimming = TextTrimming.None;
                sp.Children.Add(w);
            }
            Grid.SetColumn(sp, 1); g.Children.Add(sp);
            if (button != null && ph.HasValue)
            {
                var b = Net.NetWindowHelper.SecondaryButton(button);
                b.Padding = new Thickness(12, 4, 12, 4); b.VerticalAlignment = VAlign.Center;
                var p = ph.Value;
                b.Click += (_, _) => Open(p);
                Grid.SetColumn(b, 2); g.Children.Add(b);
            }
            var wrap = new Border { Child = g, Padding = new Thickness(0, 7, 0, 7), BorderThickness = new Thickness(0, 1, 0, 0),
                                    Background = Brushes.Transparent, ToolTip = tip.Length > 0 ? $"{title}\n{tip}" : title };
            wrap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return wrap;
        }

        // ══ Copiere ════════════════════════════════════════════════════════════════════
        private static string Label(Row r) => (r.NotTested ? P("Not tested") : r.Info ? P("Info") : r.Severity switch
        {
            Sev.Critical => P("Warning"), Sev.Warning => P("Check"), Sev.Ok => "OK", _ => "",
        }).ToUpper(System.Globalization.CultureInfo.CurrentUICulture);

        private void CopyRow(Row r)
        {
            if (r.Severity == null) return;
            try
            {
                System.Windows.Clipboard.SetText($"{r.Name}: {r.Summary} [{Label(r)}]");
                var old = r.Container.Background;
                r.Container.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                t.Tick += (_, _) => { r.Container.Background = old; t.Stop(); };
                t.Start();
            }
            catch (Exception ex) { AppLogger.Debug($"HealthCheckWindow.CopyRow: {ex.Message}"); }
        }

        private void CopyReport()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"SMD Win — {P("System Health Check")} — {DateTime.Now:dd.MM.yyyy HH:mm}");
                if (SMDWin.Services.ReportBranding.Workshop.Length > 0) sb.AppendLine(SMDWin.Services.ReportBranding.TextBlock);
                sb.AppendLine($"{_txtVerdictName.Text}");
                foreach (var gc in _cards)
                {
                    sb.AppendLine();
                    sb.AppendLine(P(gc.Group.Caption).ToUpper(System.Globalization.CultureInfo.CurrentUICulture));
                    foreach (var r in gc.Rows.Where(r => !r.Skip && r.Severity != null))
                        sb.AppendLine($"  [{Label(r)}] {r.Name}: {r.Summary}");
                }
                System.Windows.Clipboard.SetText(sb.ToString());
                _txtStatus.Text = P("Report copied — paste it into an e-mail or a document.");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "HealthCheckWindow.CopyReport"); }
        }
    }
}
