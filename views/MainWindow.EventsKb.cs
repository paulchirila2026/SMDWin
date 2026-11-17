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
        // ══ Event Viewer A: explanations from the event database, chart over time, the card of the chosen event ══

        private EventLogEntry? _evSelected;

        // The explanation: first the event database (Data/EventDB), then the older rules from EventExplainer.
        private static void ApplyEventKnowledge(EventLogEntry ev)
        {
            var kb = EventKnowledge.Find(ev.Source, ev.EventId);
            if (kb != null)
            {
                ev.KbTitle = kb.Title;
                ev.Explanation = kb.Meaning;
                ev.KbSteps = kb.Steps ?? new List<string>();
                ev.KbSeverity = kb.Severity;
                ev.KbPage = kb.Page ?? "";
                ev.IsHarmless = kb.Severity == "routine";
                return;
            }
            var ex = EventExplainer.Explain(ev.Source, ev.EventId);
            ev.Explanation = SMDWin.Services.Tx.T(ex?.text ?? "");
            ev.IsHarmless  = ex?.weight == EventExplainer.Weight.Harmless;
        }

        // Chart: one bar per day / hour / 10 minutes, aligned to the calendar (Services/EventTimeline.cs);
        // red = critical + errors, orange = warnings. Each bar has its label below ("Mo 28", "14:00");
        // when they do not all fit, they are thinned out starting from the current bar.
        private List<EventBucket> _evBuckets = new();
        private EventBucketKind _evBucketKind = EventBucketKind.Day;
        private bool _evTimelineHooked;

        private void BuildEventsTimeline(IReadOnlyList<EventStamp> stamps)
        {
            try
            {
                if (EvTimelineBars == null) return;
                var from = _evtFrom; var to = DateTime.Now < _evtTo ? DateTime.Now : _evtTo;
                _evBucketKind = EventTimeline.KindFor(from, to);
                _evBuckets = EventTimeline.Build(from, to, stamps, LanguageService.CurrentCode);

                string E(string key, string english) => LanguageService.S("Events", key, english);
                int ec = _evBuckets.Sum(b => b.Errors), wc = _evBuckets.Sum(b => b.Warnings);
                // the card title in "Sentence case" (the texts in the language files are capitalised)
                TxtEvTimelineCaption.Text = SMDWin.Services.DisplayFormat.SentenceCase(_evBucketKind switch
                {
                    EventBucketKind.Day  => E("ChartPerDay", "PROBLEMS PER DAY"),
                    EventBucketKind.Hour => E("ChartPerHour", "PROBLEMS PER HOUR"),
                    _                    => E("ChartPerTenMin", "PROBLEMS PER 10 MINUTES"),
                });
                TxtEvTimelineSub.Text = stamps.Count == 0 ? E("ChartNothing", "Nothing recorded in this range")
                    : string.Format(E("ChartCounts", "{0:N0} errors · {1:N0} warnings"), ec, wc);

                if (!_evTimelineHooked)
                {
                    _evTimelineHooked = true;
                    EvTimelineBars.SizeChanged += (_, _) => DrawEventsTimelineLabels();
                }
                DrawEventsTimeline();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BuildEventsTimeline"); }
        }

        private void DrawEventsTimeline()
        {
            EvTimelineBars.Children.Clear();
            int n = _evBuckets.Count;
            EvTimelineBars.Columns = Math.Max(1, n);
            if (n == 0) { DrawEventsTimelineLabels(); return; }

            string E(string key, string english) => LanguageService.S("Events", key, english);
            int max = Math.Max(1, _evBuckets.Max(b => b.Errors + b.Warnings));
            const double H = 64;
            foreach (var b in _evBuckets)
            {
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1.5, 0, 1.5, 0) };
                string when = _evBucketKind == EventBucketKind.Day
                    ? $"{b.Label} · {b.Start:dd.MM.yyyy}"
                    : $"{b.Start:dd.MM HH:mm} – {b.End:HH:mm}";
                col.ToolTip = when + "\n" + string.Format(E("ChartCounts", "{0:N0} errors · {1:N0} warnings"), b.Errors, b.Warnings);
                col.Background = System.Windows.Media.Brushes.Transparent;   // tooltip on the whole column, not only on the bar
                if (b.Warnings > 0) col.Children.Add(Bar(b.Warnings * H / max, "StatusWarningBrush", 0.7));
                if (b.Errors > 0)   col.Children.Add(Bar(b.Errors * H / max, "StatusCritBrush", 0.9));
                if (b.Errors + b.Warnings == 0) col.Children.Add(Bar(2, "BorderBrush2", 1));
                EvTimelineBars.Children.Add(col);
            }
            DrawEventsTimelineLabels();

            static Border Bar(double h, string key, double op)
            {
                var b = new Border { Height = Math.Max(1, h), CornerRadius = new CornerRadius(1.5), Opacity = op, Margin = new Thickness(0, 1, 0, 0) };
                b.SetResourceReference(Border.BackgroundProperty, key);
                return b;
            }
        }

        /// <summary>The labels under the bars, centred on their bar; redone when the chart changes width.</summary>
        private void DrawEventsTimelineLabels()
        {
            if (FindName("EvTimelineLabels") is not Canvas labels) return;
            labels.Children.Clear();
            int n = _evBuckets.Count;
            double width = EvTimelineBars.ActualWidth;
            if (n == 0 || width < 20) return;

            double slot = width / n;
            // room for a label: "Mo 28" / "14:00" plus a little air
            double room = (_evBucketKind == EventBucketKind.Day ? 38 : 40) * (Ui.FontXS / 11.0);
            int every = EventTimeline.LabelEvery(n, slot, room);
            for (int i = 0; i < n; i++)
            {
                if (!EventTimeline.HasLabel(i, n, every)) continue;
                bool last = i == n - 1;
                var tb = new TextBlock { Text = _evBuckets[i].Label, FontSize = Ui.FontXS, FontWeight = last ? FontWeights.SemiBold : FontWeights.Normal };
                // the current bar and the start of a day (on the hourly chart) stand out
                bool strong = last || (_evBucketKind == EventBucketKind.Hour && _evBuckets[i].Start.Hour == 0);
                tb.SetResourceReference(TextBlock.ForegroundProperty, strong ? "TextPrimaryBrush" : "TextSecondaryBrush");
                tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                double w = tb.DesiredSize.Width;
                Canvas.SetLeft(tb, Math.Clamp(slot * (i + 0.5) - w / 2, 0, Math.Max(0, width - w)));
                labels.Children.Add(tb);
            }
        }

        private void ShowEventDetail(EventLogEntry entry)
        {
            _evSelected = entry;
            EventDetailPanel.Visibility = Visibility.Visible;
            EvDetailEmpty.Visibility = Visibility.Collapsed;

            TxtEvDetTitle.Text = entry.Headline;
            TxtEvDetSource.Text = PT("{0} · event {1} · {2}", entry.Source, entry.EventId, entry.LogName);
            string when = SMDWin.Services.DateFormatHelper.FormatDateTimeSec(entry.TimeCreated);
            TxtEvDetCount.Text = entry.Count > 1
                ? PT("{0} times · first {1} · last {2}", entry.Count, SMDWin.Services.DateFormatHelper.FormatDateTimeSec(entry.FirstSeen), when)
                : when;
            EvDetDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                entry.KbSeverity switch { "serious" => "StatusCritBrush", "watch" => "StatusWarningBrush", "routine" => "StatusOkBrush", _ => entry.LevelBrushKey });

            bool known = !string.IsNullOrEmpty(entry.Explanation);
            TxtEvDetMeaning.Text = known ? entry.Explanation + (entry.IsHarmless ? " " + PT("This is a routine Windows message — usually nothing to do.") : "")
                                         : PT("This event is not in the SMD Win database yet. The full message below comes from Windows; searching the source and event ID online usually explains it.");
            bool hasSteps = entry.KbSteps.Count > 0;
            TxtEvDetStepsCaption.Visibility = hasSteps ? Visibility.Visible : Visibility.Collapsed;
            TxtEvDetSteps.Visibility = hasSteps ? Visibility.Visible : Visibility.Collapsed;
            TxtEvDetSteps.Text = string.Join("\n", entry.KbSteps.Select((st, i) => $"{i + 1}. {st}"));

            string? tag = PageTagFor(entry.KbPage);
            BtnEvDetPage.Visibility = tag != null ? Visibility.Visible : Visibility.Collapsed;
            BtnEvDetPage.Content = tag != null ? $"Open {PageLabelFor(tag)}" : "";

            TxtEventDetail.Text = entry.Message;
        }

        private static string? PageTagFor(string page) => page switch
        {
            "Crash" => "Crash", "Disk" => "Disk", "Ram" => "Ram", "Stress" => "Stress", "Power" => "Battery",
            "Drivers" => "Drivers", "Network" => "Network", "Services" => "Services", "Startup" => "Startup", "Apps" => "Apps",
            _ => null
        };
        private static string PageLabelFor(string tag) => tag switch
        {
            "Crash" => "BSOD / Crash", "Disk" => "Storage", "Ram" => "RAM Memory", "Stress" => "Temp & Stress", "Battery" => "Power",
            "Drivers" => "Drivers", "Network" => "Network", "Services" => "Windows Services", "Startup" => "Startup Manager", "Apps" => "Applications",
            _ => tag
        };

        private async void EvDetPage_Click(object sender, RoutedEventArgs e)
        {
            string? tag = PageTagFor(_evSelected?.KbPage ?? "");
            if (tag != null) await NavigateTo(tag);
        }

        private void EvDetSearch_Click(object sender, RoutedEventArgs e)
        {
            if (_evSelected == null) return;
            try { SearchGoogle($"{_evSelected.Source} event {_evSelected.EventId}"); }
            catch (Exception ex) { AppLogger.Warning(ex, "EvDetSearch_Click"); }
        }

        private void EvDetCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_evSelected == null) return;
            try
            {
                var ev = _evSelected;
                System.Windows.Clipboard.SetText($"{ev.TimeCreated:dd.MM.yyyy HH:mm:ss}  {ev.Level}  {ev.Source}  ID {ev.EventId}\n{(string.IsNullOrEmpty(ev.KbTitle) ? "" : ev.KbTitle + " — " + ev.Explanation + "\n")}\n{ev.Message}");
                ShowToastSuccess(LanguageService.S("Msg", "Copied"));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "EvDetCopy_Click"); }
        }
    }
}
