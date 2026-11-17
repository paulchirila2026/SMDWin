using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

using WpfColor         = System.Windows.Media.Color;
using WpfBrush         = System.Windows.Media.Brush;
using WpfSolidBrush    = System.Windows.Media.SolidColorBrush;
using WpfFontFamily    = System.Windows.Media.FontFamily;
using Button           = System.Windows.Controls.Button;
using CheckBox         = System.Windows.Controls.CheckBox;
using TextBox          = System.Windows.Controls.TextBox;
using TextBlock        = System.Windows.Controls.TextBlock;
using ScrollViewer     = System.Windows.Controls.ScrollViewer;
using StackPanel       = System.Windows.Controls.StackPanel;
using Border           = System.Windows.Controls.Border;
using Orientation      = System.Windows.Controls.Orientation;
using ComboBox         = System.Windows.Controls.ComboBox;
using ComboBoxItem     = System.Windows.Controls.ComboBoxItem;
using Brushes          = System.Windows.Media.Brushes;
using ColorConverter   = System.Windows.Media.ColorConverter;
using WpfPoint         = System.Windows.Point;
using WpfClipboard     = System.Windows.Clipboard;

namespace SMDWin.Views
{
    public partial class DebugInfoWindow
    {
        // ══════════════════════════════════════════════════════════════════════
        // PAGE 3 — SELF-TEST
        // ══════════════════════════════════════════════════════════════════════
        UIElement BuildSelfTestPage()
        {
            var root = new Grid { Margin = new Thickness(24, 16, 24, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: topBar
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: portable checkbox
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: warnBanner
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3: log

            // ── Top bar: run button + status ────────────────────────────────
            var topBar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _selfTestRunBtn = new Button
            {
                Content = "Run Self-Test  (~60–70 s)",
                Padding = new Thickness(16, 8, 16, 8),
                FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _selfTestRunBtn.SetResourceReference(Button.StyleProperty, "PrimaryButtonStyle");
            _selfTestRunBtn.Click += SelfTestRunBtn_Click;
            Grid.SetColumn(_selfTestRunBtn, 0);

            _selfTestStatus = new TextBlock
            {
                Text = "Walks through every sidebar page, runs CPU and GPU stress (10 s each, checking the real load %) and a benchmark. No interaction needed after the click.",
                FontSize = Ui.FontS, Opacity = 0.55, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            };
            _selfTestStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(_selfTestStatus, 1);

            topBar.Children.Add(_selfTestRunBtn);
            topBar.Children.Add(_selfTestStatus);
            Grid.SetRow(topBar, 0);
            root.Children.Add(topBar);

            var warnBanner = new TextBlock
            {
                Text = "During the test the CPU and GPU run at 100% for a few seconds — this is expected.",
                FontSize = Ui.FontXS, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 10),
            };
            warnBanner.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetRow(warnBanner, 2);
            root.Children.Add(warnBanner);

            // ── Live log ─────────────────────────────────────────────────────
            _selfTestBox = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = Ui.FontXS, BorderThickness = new Thickness(0),
                AcceptsReturn = true,
                Text = "Press \"Run Self-Test\" to start. When it finishes, the report is saved in Data › SelfTest-Results automatically.",
            };
            _selfTestBox.SetResourceReference(TextBox.ForegroundProperty, "TextSecondaryBrush");
            _selfTestBox.SetResourceReference(TextBox.BackgroundProperty, "BgDarkBrush");
            _selfTestScroll = new ScrollViewer
            {
                Content = _selfTestBox,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetRow(_selfTestScroll, 3);
            root.Children.Add(_selfTestScroll);

            return root;
        }

        private async void SelfTestRunBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selfTestRunning)
            {
                _selfTestCts?.Cancel();
                return;
            }

            _selfTestRunning = true;
            SMDWin.Services.ThreadTags.SelfTestRunning = true;
            _selfTestCts?.Dispose();
            _selfTestCts = new System.Threading.CancellationTokenSource();
            _selfTestRunBtn.Content = "Stop";
            _selfTestRunBtn.SetResourceReference(Button.StyleProperty, "RedButtonStyle");
            _selfTestStatus.Text = LanguageService.S("Diag", "SelfTestRunning");
            _selfTestBox.Text = "";
            var lines = new StringBuilder();

            void OnProgress(string line)
            {
                // The callback may come from a background thread — marshal to the UI thread.
                Dispatcher.Invoke(() =>
                {
                    lines.AppendLine(line);
                    _selfTestBox.Text = lines.ToString();
                    _selfTestScroll.ScrollToBottom();
                });
            }

            var mainWin = this.Owner as SMDWin.Views.MainWindow;

            try
            {
                var run = await SelfTestRunner.RunAllAsync(mainWin, OnProgress, _selfTestCts.Token);
                string savedPath = run.SavedPath;
                _selfTestStatus.Text = string.IsNullOrEmpty(savedPath)
                    ? "Finished — the report could not be saved (see the log above)."
                    : $"Finished — report saved to: {savedPath}";
            }
            catch (OperationCanceledException)
            {
                _selfTestStatus.Text = LanguageService.S("Diag", "SelfTestStoppedByUser");
                OnProgress("");
                OnProgress("⚠ TEST STOPPED MANUALLY — the results above are partial.");
                await SelfTestService.SaveReportAsync(lines.ToString(), OnProgress);
            }
            catch (Exception ex)
            {
                _selfTestStatus.Text = "Failed unexpectedly — see the log for details.";
                OnProgress("");
                OnProgress($"✘ SELF-TEST FAILED: {ex}");
                AppLogger.Warning(ex, "DebugInfoWindow.SelfTest");
            }
            finally
            {
                _selfTestRunning = false;
                SMDWin.Services.ThreadTags.SelfTestRunning = false;
                _selfTestRunBtn.Content = "Run Self-Test  (~60–70 s)";
                _selfTestRunBtn.SetResourceReference(Button.StyleProperty, "PrimaryButtonStyle");
            }
        }
    }
}
