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
    public partial class MainWindow : Window
    {
        // ── EVENTS ────────────────────────────────────────────────────────────

        private DateTime _evtFrom = DateTime.Today.AddDays(-1);
        private DateTime _evtTo   = DateTime.Now.AddDays(1);

        private async void EventTimeRange_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var tag = btn.Tag?.ToString() ?? "24h";

            foreach (var b in new[] { BtnEvt1h, BtnEvt6h, BtnEvt24h, BtnEvt7d, BtnEvt30d })
                SetTabButtonActive(b, b == btn);

            _evtTo   = DateTime.Now;
            _evtFrom = tag switch
            {
"1h"=> DateTime.Now.AddHours(-1),
"6h"=> DateTime.Now.AddHours(-6),
"24h" => DateTime.Now.AddDays(-1),
"7d"=> DateTime.Now.AddDays(-7),
"30d" => DateTime.Now.AddDays(-30),
                _     => DateTime.Now.AddDays(-1)
            };

            await RunEventScan();
        }

        private async void ScanEvents_Click(object sender, RoutedEventArgs e)
        {
            InitPerfmonEventsOverlay();
            await RunEventScan();
        }

        // Default: errors + warnings (previously "All" — the screen filled up with Information).
        private string _selectedEventLevel = "Errors & Warnings";
        private bool _groupRepeatedEvents = true;

        private async void ChkGroupEvents_Changed(object sender, RoutedEventArgs e)
        {
            _groupRepeatedEvents = (sender as System.Windows.Controls.CheckBox)?.IsChecked == true;
            if (_eventScan != null) ShowEvents(_eventScan);
            await Task.CompletedTask;
        }

        // Windows routine messages (known to be harmless) are hidden by default: the table starts with what matters.
        private bool _hideHarmlessEvents = true;

        private void ChkHideHarmless_Changed(object sender, RoutedEventArgs e)
        {
            _hideHarmlessEvents = (sender as System.Windows.Controls.CheckBox)?.IsChecked == true;
            if (_eventScan != null) ShowEvents(_eventScan);
        }

        private List<EventLogEntry> _rawEvents = new();
        private EventScan? _eventScan;

        /// <summary>
        /// Explanations + grouping + display. Grouping (same log / source / ID / level → one row with "×N") and the chart are
        /// computed from all the events in the interval; a group row shows the most recent occurrence.
        /// </summary>
        private void ShowEvents(EventScan scan)
        {
            string E(string key, string english) => LanguageService.S("Events", key, english);
            var described = scan.Entries.Concat(scan.GroupHeads).ToList();
            foreach (var ev in described)
            {
                ApplyEventKnowledge(ev);
                ev.Count = 1; ev.FirstSeen = ev.TimeCreated;
            }

            List<EventLogEntry> shown;
            if (_groupRepeatedEvents)
            {
                // a group's representative = its first occurrence in the list (the lists are sorted newest first)
                var heads = new Dictionary<EventGroupKey, EventLogEntry>();
                foreach (var ev in described)
                    heads.TryAdd(new EventGroupKey(ev.LogName, ev.Source, ev.EventId, ev.Level), ev);
                shown = new List<EventLogEntry>();
                foreach (var g in EventTimeline.Group(scan.Stamps))
                    if (heads.TryGetValue(g.Key, out var head))
                    {
                        head.Count = g.Count;
                        head.FirstSeen = g.First;
                        shown.Add(head);
                    }
            }
            else shown = scan.Entries.OrderByDescending(e => e.TimeCreated).ToList();

            int harmless = shown.Count(e => e.IsHarmless);
            if (_hideHarmlessEvents) shown = shown.Where(e => !e.IsHarmless).ToList();

            _allEvents = shown;
            BuildEventsTimeline(scan.Stamps);

            int total = scan.Stamps.Count;
            string totalText = scan.CountCapped || scan.Partial ? string.Format(E("MoreThan", "more than {0:N0}"), total) : total.ToString("N0");
            if (TxtEvTableSub != null)
            {
                string sub = total == 0 ? E("NoneInRange", "No events in this range")
                    : $"{shown.Count:N0} {(_groupRepeatedEvents ? E("WordGroups", "groups") : E("WordEvents", "events"))} · {E("NewestFirst", "newest first")}";
                // without grouping only the newest events are shown; the rest appear in the chart and in the count
                if (!_groupRepeatedEvents && total > scan.Entries.Count)
                    sub += " · " + string.Format(E("ShowingNewest", "showing the newest {0:N0} of {1}"), scan.Entries.Count, totalText);
                TxtEvTableSub.Text = sub;
            }
            if (EventsGrid != null)
            {
                EventsGrid.ItemsSource = _allEvents;
                if (FindName("TxtEventsPlaceholderPanel") is System.Windows.Controls.StackPanel epp)
                    epp.Visibility = _allEvents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (FindName("TxtEventsPlaceholder") is TextBlock ph)
                    ph.Text = total == 0 ? E("NoneInRange", "No events in this range")
                            : E("OnlyHarmless", "Only routine Windows messages in this range. Untick \"Hide harmless\" to see them.");
                // The first event is selected from the start, so the panel on the right immediately shows what it means
                if (_allEvents.Count > 0)
                {
                    EventsGrid.SelectedIndex = 0;
                    ShowEventDetail(_allEvents[0]);
                }
                else
                {
                    if (EventDetailPanel != null) EventDetailPanel.Visibility = Visibility.Collapsed;
                    if (EvDetailEmpty != null) EvDetailEmpty.Visibility = Visibility.Visible;
                }
            }
            if (FindName("TxtEventsSummary") is TextBlock sum)
            {
                int crit = scan.Stamps.Count(s => s.Level == "Critical"), err = scan.Stamps.Count(s => s.Level == "Error"),
                    warn = scan.Stamps.Count(s => s.Level == "Warning");
                sum.Text = total == 0 ? "" :
                    $"{totalText} {E("WordEvents", "events")} · {crit:N0} {E("WordCritical", "critical")} · {err:N0} {E("WordErrors", "errors")} · {warn:N0} {E("WordWarnings", "warnings")}" +
                    (harmless > 0 ? " · " + string.Format(_hideHarmlessEvents ? E("HarmlessHidden", "{0} known harmless hidden") : E("HarmlessShown", "{0} known harmless"), harmless) : "");
            }
        }

        private async void EventLevel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            _selectedEventLevel = btn.Tag?.ToString() ?? "All";

            var levelBtns = new[] { BtnLevelAll, BtnLevelCritical, BtnLevelError, BtnLevelWarning, BtnLevelErrWarn };
            foreach (var b in levelBtns)
                SetTabButtonActive(b, b == btn);

            await RunEventScan();
        }

        private CancellationTokenSource? _eventScanCts;

        private async Task RunEventScan()
        {
            // Cancel the previous scan if it is running
            _eventScanCts?.Cancel();
            _eventScanCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ct = _eventScanCts.Token;

            var level  = _selectedEventLevel;
            var search = TxtSearch?.Text.Trim() ?? "";

            SyncEventRangeButtons();
            ShowLoading(_LS("Events", "Scanning"));
            SMDWin.Services.PerfmonService.Instance.PollPaused = true;
            _monitoringPausedForScan = true;
            try
            {
                _eventScan = await _eventService.ScanAsync(_evtFrom, _evtTo, level, search, ct);
                _rawEvents = _eventScan.Entries;
                if (FindName("EventsPerfmonCard") is System.Windows.Controls.Border epc)
                    epc.Visibility = Visibility.Visible;
                ShowEvents(_eventScan);
            }
            catch (OperationCanceledException)
            {
                ShowToastWarning(_LS("Events", "ScanTimeout"));
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "RunEventScan");
                ShowToastWarning(LanguageService.S("Common", "ErrorPrefix") + ex.Message);
            }
            finally
            {
                HideLoading();
                SMDWin.Services.PerfmonService.Instance.PollPaused = false;
                _monitoringPausedForScan = false;
            }
        }

        /// <summary>
        /// The highlighted interval button follows the interval actually scanned. The interval also comes from Settings
        /// ("Event days back", 7 days by default), not only from the buttons — without a match "24h" stayed highlighted
        /// while the chart showed a week.
        /// </summary>
        private void SyncEventRangeButtons()
        {
            double hours = ((DateTime.Now < _evtTo ? DateTime.Now : _evtTo) - _evtFrom).TotalHours;
            // with tolerance: "7 days" in Settings means from midnight, so up to 8 calendar days
            Wpf.Ui.Controls.Button? match =
                  hours <= 1.5                       ? BtnEvt1h
                : hours is >= 4 and <= 8             ? BtnEvt6h
                : hours is >= 20 and <= 49           ? BtnEvt24h
                : hours is >= 6 * 24 and <= 9 * 24   ? BtnEvt7d
                : hours is >= 28 * 24 and <= 32 * 24 ? BtnEvt30d
                : null;   // another interval (from Settings): no button highlighted
            foreach (var b in new[] { BtnEvt1h, BtnEvt6h, BtnEvt24h, BtnEvt7d, BtnEvt30d })
                SetTabButtonActive(b, b == match);
        }

        private void EventsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EventsGrid.SelectedItem is EventLogEntry entry) ShowEventDetail(entry);
        }

        private async void ExportEvents_Click(object sender, RoutedEventArgs e)
        {
            if (_allEvents.Count == 0) { ShowToastWarning(_LS("Events", "NoDataExport")); return; }
            var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"SMDWin_Events_{DateTime.Now:yyyyMMdd}.csv" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        var sb = new StringBuilder("Timp,Nivel,Log,Sursa,EventID,Mesaj\n");
                        // The export contains all occurrences, not only the grouped rows
                        foreach (var ev in (_rawEvents.Count > 0 ? _rawEvents : _allEvents))
                            sb.AppendLine($"\"{ev.TimeCreated:dd.MM.yyyy HH:mm:ss}\",\"{ev.Level}\",\"{ev.LogName}\",\"{ev.Source}\",{ev.EventId},\"{ev.Message.Replace("\"", "'")}\"");
                        File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    });
                    ShowToastSuccess(_LS("Events", "ExportSavedPrefix") + dlg.FileName);
                }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, "ExportEvents_Click");
                    ShowToastWarning(LanguageService.S("Common", "ErrorPrefix") + ex.Message);
                }
            }
        }

    } // end partial class MainWindow

} // end namespace SMDWin.Views
