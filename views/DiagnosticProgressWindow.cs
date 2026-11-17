
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Brush        = System.Windows.Media.Brush;
using Brushes      = System.Windows.Media.Brushes;
using Button       = System.Windows.Controls.Button;
using Orientation  = System.Windows.Controls.Orientation;
using Panel        = System.Windows.Controls.Panel;
using ProgressBar  = System.Windows.Controls.ProgressBar;

namespace SMDWin.Views
{
    public class DiagnosticProgressWindow : Window
    {
        private readonly Dictionary<SMDWin.Services.DiagnosticPhase, PhaseRow> _rows = new();
        private readonly SMDWin.Services.DiagnosticOrchestrator _orchestrator = new();
        private CancellationTokenSource? _cts;
        private Button _btnAction = null!;
        private Button _btnReport = null!;
        private List<SMDWin.Services.DiagnosticPhaseResult>? _finalResults;
        private TextBlock _txtOverallStatus = null!;
        private bool _finished;

        private static readonly (SMDWin.Services.DiagnosticPhase Phase, string Icon, string Name)[] Phases =
        {
            (SMDWin.Services.DiagnosticPhase.Cpu,     "🔥", "CPU"),
            (SMDWin.Services.DiagnosticPhase.Gpu,     "🎮", "GPU"),
            (SMDWin.Services.DiagnosticPhase.Ram,     "🧠", "RAM"),
            (SMDWin.Services.DiagnosticPhase.Storage, "💾", "Storage"),
            (SMDWin.Services.DiagnosticPhase.Network, "🌐", "Network"),
        };

        private class PhaseRow
        {
            public Border Container = null!;
            public TextBlock StatusIcon = null!;
            public TextBlock SummaryText = null!;
            public ProgressBar Bar = null!;
        }

        public DiagnosticProgressWindow(string driveLetter)
        {
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            Net.NetWindowHelper.SetupWindow(this, "System Diagnostic — SMDWin", 560, 620);
            SizeToContent = SizeToContent.Height;
            MinHeight = 0; // otherwise MinHeight (set by SetupWindow to 0.6*620=372) would prevent shrinking below the content
            Content = Net.NetWindowHelper.WindowShell(BuildUI());

            _orchestrator.PhaseStarted   += p => Dispatcher.Invoke(() => OnPhaseStarted(p));
            _orchestrator.PhaseProgress  += (p, pct) => Dispatcher.Invoke(() => OnPhaseProgress(p, pct));
            _orchestrator.PhaseCompleted += r => Dispatcher.Invoke(() => OnPhaseCompleted(r));

            Loaded += async (_, _) => await RunAsync(driveLetter);
            Closing += (_, _) => { if (!_finished) _cts?.Cancel(); };
        }

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // phases
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // action button

            var hdr = new Grid { Margin = new Thickness(0, 0, 0, 12), Background = Brushes.Transparent };
            hdr.ColumnDefinitions.Add(new ColumnDefinition());
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = "System Diagnostic", FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = Net.NetWindowHelper.Res("TextPrimaryBrush")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "CPU, GPU, RAM, Storage, Network — real tests, no synthetic score",
                FontSize = Ui.FontS, Foreground = Net.NetWindowHelper.Res("TextSecondaryBrush"),
                Margin = new Thickness(0, 2, 0, 0), Opacity = 0.7
            });
            Grid.SetColumn(titleStack, 0); hdr.Children.Add(titleStack);
            Grid.SetRow(hdr, 0); root.Children.Add(hdr);
            Net.NetWindowHelper.MakeDraggable(hdr, this);

            _txtOverallStatus = new TextBlock
            {
                FontSize = Ui.FontM, Margin = new Thickness(0, 0, 0, 12),
                Foreground = Net.NetWindowHelper.Res("TextSecondaryBrush"),
                Text = "Starting…"
            };
            Grid.SetRow(_txtOverallStatus, 1); root.Children.Add(_txtOverallStatus);

            var phasesPanel = new StackPanel();
            foreach (var (phase, icon, name) in Phases)
                phasesPanel.Children.Add(BuildPhaseRow(phase, icon, name));
            Grid.SetRow(phasesPanel, 2); root.Children.Add(phasesPanel);

            var btnRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            btnRow.ColumnDefinitions.Add(new ColumnDefinition());
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition());

            _btnReport = Net.NetWindowHelper.SecondaryButton("📄 View Report", "Generate and open the diagnostic report");
            _btnReport.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            _btnReport.Visibility = Visibility.Collapsed;
            _btnReport.Click += BtnReport_Click;
            Grid.SetColumn(_btnReport, 0); btnRow.Children.Add(_btnReport);

            _btnAction = new Button
            {
                Width = 40, Height = 40, Padding = new Thickness(0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Stop the diagnostic",
                Content = new System.Windows.Shapes.Path
                {
                    Data = SMDWin.Services.FluentIcons.Get("dismiss"), Fill = Brushes.White, Width = 24, Height = 24,
                    RenderTransform = new ScaleTransform(0.75, 0.75, 12, 12),
                }
            };
            var stopTemplate = new ControlTemplate(typeof(Button));
            var ellipseFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            ellipseFactory.SetBinding(System.Windows.Shapes.Ellipse.FillProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            gridFactory.AppendChild(ellipseFactory);
            gridFactory.AppendChild(contentFactory);
            stopTemplate.VisualTree = gridFactory;
            _btnAction.Template = stopTemplate;
            _btnAction.Click += BtnAction_Click;
            Grid.SetColumn(_btnAction, 1); btnRow.Children.Add(_btnAction);

            Grid.SetRow(btnRow, 3); root.Children.Add(btnRow);

            return root;
        }

        private Border BuildPhaseRow(SMDWin.Services.DiagnosticPhase phase, string icon, string name)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(Net.NetWindowHelper.CurrentRadiusCard()), Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Background = Net.NetWindowHelper.Res("BgCardBrush"),
                BorderBrush = Net.NetWindowHelper.Res("BorderBrush2"), BorderThickness = new Thickness(1),
            };
            var stack = new StackPanel();

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var statusIcon = new TextBlock { Text = "○", FontSize = Ui.FontL, Margin = new Thickness(0, 0, 10, 0),
                Foreground = Net.NetWindowHelper.Res("TextSecondaryBrush"), VerticalAlignment = System.Windows.VerticalAlignment.Center };
            Grid.SetColumn(statusIcon, 0); row.Children.Add(statusIcon);

            var nameBlock = new TextBlock { Text = $"{icon} {name}", FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                Foreground = Net.NetWindowHelper.Res("TextPrimaryBrush"), VerticalAlignment = System.Windows.VerticalAlignment.Center };
            Grid.SetColumn(nameBlock, 1); row.Children.Add(nameBlock);

            stack.Children.Add(row);

            var bar = new ProgressBar { Height = 4, Margin = new Thickness(0, 8, 0, 0), Minimum = 0, Maximum = 1,
                Value = 0, Visibility = Visibility.Collapsed };
            stack.Children.Add(bar);

            var summary = new TextBlock { FontSize = Ui.FontS, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
                Foreground = Net.NetWindowHelper.Res("TextSecondaryBrush"), Visibility = Visibility.Collapsed };
            stack.Children.Add(summary);

            card.Child = stack;
            _rows[phase] = new PhaseRow { Container = card, StatusIcon = statusIcon, SummaryText = summary, Bar = bar };
            return card;
        }

        private async Task RunAsync(string driveLetter)
        {
            _cts = new CancellationTokenSource();
            try
            {
                var results = await _orchestrator.RunAsync(driveLetter, _cts.Token);
                _finished = true;
                _finalResults = results;
                bool allOk = results.TrueForAll(r => r.Success);
                _txtOverallStatus.Text = allOk
                    ? "✓ All tests completed — no issues found"
                    : "⚠ Diagnostic complete — see details below";
                _btnAction.Content = "Close";
                _btnReport.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException)
            {
                _finished = true;
                _txtOverallStatus.Text = "Cancelled.";
                _btnAction.Content = "Close";
            }
            catch (Exception ex)
            {
                _finished = true;
                SMDWin.Services.AppLogger.Warning(ex, "DiagnosticProgressWindow.RunAsync");
                _txtOverallStatus.Text = $"⚠ Unexpected error: {ex.Message}";
                _btnAction.Content = "Close";
            }
        }

        private void OnPhaseStarted(SMDWin.Services.DiagnosticPhase phase)
        {
            if (!_rows.TryGetValue(phase, out var row)) return;
            row.StatusIcon.Text = "◐";
            row.StatusIcon.Foreground = Net.NetWindowHelper.Res("AccentBrush");
            row.Bar.Visibility = Visibility.Visible;
            _txtOverallStatus.Text = $"Running {phase}…";
        }

        private void OnPhaseProgress(SMDWin.Services.DiagnosticPhase phase, double pct)
        {
            if (!_rows.TryGetValue(phase, out var row)) return;
            row.Bar.Value = Math.Clamp(pct, 0, 1);
        }

        private void OnPhaseCompleted(SMDWin.Services.DiagnosticPhaseResult result)
        {
            if (!_rows.TryGetValue(result.Phase, out var row)) return;
            row.Bar.Visibility = Visibility.Collapsed;
            row.StatusIcon.Text = result.Success ? "✔" : "⚠";
            row.StatusIcon.Foreground = result.Success
                ? Net.NetWindowHelper.Res("StatusOkBrush")
                : Net.NetWindowHelper.Res("WarnAmberBrush");
            row.SummaryText.Text = result.Summary;
            row.SummaryText.Visibility = Visibility.Visible;
        }

        private void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            if (_finished) { Close(); return; }
            _cts?.Cancel();
        }

        private async void BtnReport_Click(object sender, RoutedEventArgs e)
        {
            if (_finalResults == null) return;
            try
            {
                _btnReport.IsEnabled = false;
                var reportSvc = new SMDWin.Services.ReportService();
                string html = await reportSvc.GenerateDiagnosticReportAsync(
                    _finalResults, Environment.MachineName,
                    SMDWin.Services.SettingsService.Current.Language);

                string savePath = SMDWin.Services.SettingsService.GetEffectiveReportFolder();
                string fileName = System.IO.Path.Combine(savePath,
                    $"SMDWin_SystemDiagnostic_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.html");
                await System.IO.File.WriteAllTextAsync(fileName, html, System.Text.Encoding.UTF8);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SMDWin.Services.AppLogger.Warning(ex, "DiagnosticProgressWindow.BtnReport_Click");
            }
            finally { _btnReport.IsEnabled = true; }
        }
    }
}
