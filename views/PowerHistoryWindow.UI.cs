using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;

using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using Canvas              = System.Windows.Controls.Canvas;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox          = System.Windows.MessageBox;
using Orientation         = System.Windows.Controls.Orientation;
using Rectangle           = System.Windows.Shapes.Rectangle;
using ToolTip             = System.Windows.Controls.ToolTip;
using MouseEventArgs      = System.Windows.Input.MouseEventArgs;
using Cursors             = System.Windows.Input.Cursors;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;
using WpfPoint            = System.Windows.Point;

namespace SMDWin.Views
{
    public partial class PowerHistoryWindow
    {
        // ── UI Construction ───────────────────────────────────────────────────
        private UIElement BuildUI()
        {
            var outer = new Grid { Margin = new Thickness(20) };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // stats bar
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // splitter area

            var header = new Grid { Margin = new Thickness(0, 0, 0, 12), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleSp.Children.Add(new TextBlock
            {
                Text = MainWindow.PT("Power & Session History"), FontSize = Ui.FontXXL, FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimaryBrush")
            });
            titleSp.Children.Add(new TextBlock
            {
                Text = "Boot, shutdown and crash events from Windows Event Log",
                FontSize = Ui.FontS, Foreground = Res("TextSecondaryBrush"),
                Margin = new Thickness(0, 2, 0, 0), Opacity = 0.7
            });
            Grid.SetColumn(titleSp, 0); header.Children.Add(titleSp);

            var hdrRight = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            _btnMonth = MakeRangeButton("Month", _viewMode == "month");
            _btnWeek  = MakeRangeButton("Week",  _viewMode == "week");
            _btnDay   = MakeRangeButton("Day",   false);
            // Day always active — a click without a selection => today

            _btnMonth.Click += (_, _) =>
            {
                _viewMode = "month"; _filterDay = null; _days = 30;
                SetRangeActive(_btnMonth, _btnWeek, _btnDay);
                _ = LoadAsync();
            };
            _btnWeek.Click += (_, _) =>
            {
                _viewMode = "week"; _filterDay = null; _days = 7;
                _weekStart = StartOfWeek(DateTime.Today);
                SetRangeActive(_btnWeek, _btnMonth, _btnDay);
                DrawTimeline();
            };
            _btnDay.Click += (_, _) =>
            {
                _filterDay ??= DateTime.Today;
                _viewMode = "day";
                SetRangeActive(_btnDay, _btnMonth, _btnWeek);
                DrawTimeline();
            };
            hdrRight.Children.Add(_btnMonth);
            hdrRight.Children.Add(_btnWeek);
            hdrRight.Children.Add(_btnDay);

            _btnRefresh = MakeActionButton("⟳ " + SMDWin.Views.MainWindow.PT("Refresh"));
            _btnRefresh.Margin = new Thickness(16, 0, 0, 0);
            _btnRefresh.Click += async (_, _) => { _filterDay = null; await LoadAsync(); };
            hdrRight.Children.Add(_btnRefresh);

            _btnExport = MakeActionButton("↓ " + SMDWin.Views.MainWindow.PT("Export CSV"));
            _btnExport.Margin = new Thickness(8, 0, 0, 0);
            _btnExport.Click += ExportCsv_Click;
            hdrRight.Children.Add(_btnExport);

            var btnClose = MakeActionButton("✕");
            btnClose.Margin = new Thickness(8, 0, 0, 0);
            btnClose.Width = 36;
            btnClose.Height = 32;
            btnClose.Padding = new Thickness(0);
            btnClose.FontSize = Ui.FontL;
            btnClose.ToolTip = LanguageService.S("Common", "Close");
            btnClose.Click += (_, _) => Close();
            hdrRight.Children.Add(btnClose);

            Grid.SetColumn(hdrRight, 1); header.Children.Add(hdrRight);
            Grid.SetRow(header, 0); outer.Children.Add(header);
            SMDWin.Views.Net.NetWindowHelper.MakeDraggable(header, this);

            // ── Stats bar ─────────────────────────────────────────────────────
            var statsRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            statsRow.ColumnDefinitions.Add(new ColumnDefinition());
            statsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtStats = new TextBlock
            {
                FontSize = Ui.FontS, Foreground = Res("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(_txtStats, 0); statsRow.Children.Add(_txtStats);

            // Crash badge (hidden when no crashes)
            _crashBadge = new Border
            {
                Background   = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)),
                CornerRadius = new CornerRadius(10),
                Padding      = new Thickness(10, 4, 10, 4),
                Margin       = new Thickness(12, 0, 0, 0),
                Visibility   = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center
            };
            _txtCrash = new TextBlock { Foreground = Brushes.White, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold };
            _crashBadge.Child = _txtCrash;
            Grid.SetColumn(_crashBadge, 1); statsRow.Children.Add(_crashBadge);

            Grid.SetRow(statsRow, 1); outer.Children.Add(statsRow);

            // ── Splitter area: timeline (auto-height) + legend + grid ───────────
            var splitGrid = new Grid();
            splitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // timeline — auto by content
            splitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // splitter
            splitGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // legend + filter
            splitGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
            Grid.SetRow(splitGrid, 2); outer.Children.Add(splitGrid);

            // Timeline card
            var timelineBorder = new Border
            {
                Background   = Res("BgCardBrush"),
                CornerRadius = new CornerRadius((System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8),
                Padding      = new Thickness(16, 10, 16, 10),
                Margin       = new Thickness(0, 0, 0, 0),
                ClipToBounds = true
            };
            _timelinePanel = new StackPanel();
            timelineBorder.Child = _timelinePanel;
            Grid.SetRow(timelineBorder, 0); splitGrid.Children.Add(timelineBorder);

            // GridSplitter
            var splitter = new GridSplitter
            {
                Height              = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Center,
                Background          = Brushes.Transparent,
                Cursor              = Cursors.SizeNS,
                Margin              = new Thickness(0, 2, 0, 2),
                ShowsPreview        = true
            };
            Grid.SetRow(splitter, 1); splitGrid.Children.Add(splitter);

            // Legend + filter chips row
            var legendRow = new Grid { Margin = new Thickness(0, 4, 0, 6) };
            legendRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // chips
            legendRow.ColumnDefinitions.Add(new ColumnDefinition());                            // legend
            legendRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // filter label

            // ── Chips filtru tip ──────────────────────────────────────────────
            _filterChips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            string[] chipLabels = { "All", "Power", "Warnings", "Crashes", "Logon" };
            foreach (var chip in chipLabels)
            {
                var captured = chip;
                var chipBtn = new Button
                {
                    Content = SMDWin.Views.MainWindow.PT(captured), Tag = captured,
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin  = new Thickness(0, 0, 4, 0),
                    FontSize = Ui.FontXS, Cursor = Cursors.Hand
                };
                // Initial style: All is active, the rest outline
                if (captured == "All")
                {
                    if (Application.Current.Resources["OutlineButtonStyle"] is Style sa) chipBtn.Style = sa;
                }
                else
                {
                    if (Application.Current.Resources["OutlineButtonStyle"] is Style so) chipBtn.Style = so;
                }
                chipBtn.Click += (_, _) =>
                {
                    _kindFilter = captured;
                    UpdateChipStyles();
                    ApplyDayFilter();
                };
                _filterChips.Children.Add(chipBtn);
            }
            Grid.SetColumn(_filterChips, 0); legendRow.Children.Add(_filterChips);

            // ── Legend culori ─────────────────────────────────────────────────
            // wraps onto a second line in a narrow window instead of running past the edge
            var legend = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            legend.Children.Add(LegendItem(WpfColor.FromRgb(34, 197, 94),          "Running"));
            legend.Children.Add(LegendItem(WpfColor.FromRgb(251, 191, 36),         "Sleep / Standby"));
            legend.Children.Add(LegendItem(WpfColor.FromRgb(239, 68, 68),          "Crash / Unexpected"));
            legend.Children.Add(LegendItem(WpfColor.FromRgb(125, 131, 143),        "Unknown (no data)"));
            legend.Children.Add(LegendItem(WpfColor.FromArgb(60, 180, 180, 180),   "Offline"));
            Grid.SetColumn(legend, 1); legendRow.Children.Add(legend);

            // Filter label (shows when a day is selected)
            _filterLabel = new TextBlock
            {
                FontSize = Ui.FontS, Foreground = Res("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
            };
            var clearFilter = new Button
            {
                Content = "✕ " + SMDWin.Views.MainWindow.PT("Clear filter"), Padding = new Thickness(8, 3, 8, 3),
                Margin  = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed
            };
            if (Application.Current.Resources["OutlineButtonStyle"] is Style cs) clearFilter.Style = cs;
            clearFilter.Click += (_, _) =>
            {
                _filterDay = null;
                _filterLabel.Visibility = Visibility.Collapsed;
                clearFilter.Visibility  = Visibility.Collapsed;
                ApplyDayFilter();
            };
            // Store ref to clear button in Tag for access in DrawTimeline
            _filterLabel.Tag = clearFilter;

            var filterSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            filterSp.Children.Add(_filterLabel);
            filterSp.Children.Add(clearFilter);
            Grid.SetColumn(filterSp, 2); legendRow.Children.Add(filterSp);

            Grid.SetRow(legendRow, 2); splitGrid.Children.Add(legendRow);

            // Events DataGrid
            var gridOuter = new Grid();
            gridOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            gridOuter.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _txtStatus = new TextBlock
            {
                FontSize = Ui.FontS, Foreground = Res("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(_txtStatus, 0); gridOuter.Children.Add(_txtStatus);

            var gridBorder = new Border
            {
                Background = Res("BgCardBrush"), CornerRadius = new CornerRadius((System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8), Padding = new Thickness(0)
            };
            _grid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                RowHeaderWidth      = 0, BorderThickness = new Thickness(0),
                FontSize = Ui.FontS, RowHeight = 30, ColumnHeaderHeight = 33,
                Background = Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanUserResizeColumns = true,
            };

            // Icon column — wider so emoji are not clipped
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header  = "",
                Binding = new System.Windows.Data.Binding("KindIcon"),
                Width   = new DataGridLength(40),
                ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center),
                                new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center) }
                }
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header  = MainWindow.PT("Date / Time"),
                Binding = new System.Windows.Data.Binding("Time") { StringFormat = "dd MMM yyyy  HH:mm:ss" },
                Width   = new DataGridLength(175)
            });

            // Event column with colour
            var eventCol = new DataGridTemplateColumn { Header = "Event", Width = new DataGridLength(165) };
            var eventTpl = new DataTemplate();
            var eventFef = new FrameworkElementFactory(typeof(TextBlock));
            eventFef.SetBinding(TextBlock.TextProperty,       new System.Windows.Data.Binding("KindLabel"));
            eventFef.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("KindColor") { Converter = new StringToBrushConverter() });
            eventFef.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            eventFef.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            eventTpl.VisualTree = eventFef; eventCol.CellTemplate = eventTpl;
            _grid.Columns.Add(eventCol);

            _grid.Columns.Add(new DataGridTextColumn
            {
                Header  = "Duration",
                Binding = new System.Windows.Data.Binding("Duration"),
                Width   = new DataGridLength(100)
            });
            // Notes column — tooltip with the full message on hover
            var notesCol = new DataGridTemplateColumn { Header = SMDWin.Views.MainWindow.PT("Notes"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) };
            var notesTpl = new DataTemplate();
            var notesFef = new FrameworkElementFactory(typeof(TextBlock));
            notesFef.SetBinding(TextBlock.TextProperty,   new System.Windows.Data.Binding("Notes"));
            notesFef.SetBinding(ToolTipService.ToolTipProperty, new System.Windows.Data.Binding("Notes"));
            notesFef.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            notesFef.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            notesTpl.VisualTree = notesFef; notesCol.CellTemplate = notesTpl;
            _grid.Columns.Add(notesCol);

            // Row styles: red tint for crash, blue tint for logon, dim for logoff
            var rowStyle = new Style(typeof(DataGridRow));
            var badTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsBad"), Value = true
            };
            badTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(22, 239, 68, 68))));
            rowStyle.Triggers.Add(badTrigger);

            var logonTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsLogon"), Value = true
            };
            logonTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(14, 96, 165, 250))));
            rowStyle.Triggers.Add(logonTrigger);

            var warnTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsWarning"), Value = true
            };
            warnTrigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty,
                new SolidColorBrush(WpfColor.FromArgb(16, 245, 158, 11))));
            rowStyle.Triggers.Add(warnTrigger);
            _grid.RowStyle = rowStyle;

            gridBorder.Child = _grid;
            _gridBorder = gridBorder;
            Grid.SetRow(gridBorder, 1); gridOuter.Children.Add(gridBorder);

            // Double click on a row → the event details window
            _grid.MouseDoubleClick += (_, e) =>
            {
                if (_grid.SelectedItem is PowerEvent ev)
                    OpenEventDetail(ev);
            };

            // Context menu: Copy + Search Google + Details
            var ctxMenu = new ContextMenu();
            var miDetails = new MenuItem { Header = MainWindow.PT("View details...") };
            miDetails.Click += (_, _) => { if (_grid.SelectedItem is PowerEvent ev) OpenEventDetail(ev); };
            var miCopy = new MenuItem { Header = MainWindow.PT("Copy row") };
            miCopy.Click += (_, _) =>
            {
                if (_grid.SelectedItem is PowerEvent ev)
                    System.Windows.Clipboard.SetText(
                        $"{ev.Time:dd MMM yyyy HH:mm:ss}\t{ev.KindLabel}\t{ev.Duration}\t{ev.Notes}");
            };
            var miSearch = new MenuItem { Header = MainWindow.PT("Search on Google") };
            miSearch.Click += (_, _) =>
            {
                if (_grid.SelectedItem is PowerEvent ev && !string.IsNullOrWhiteSpace(ev.Notes))
                {
                    string q = Uri.EscapeDataString(ev.Notes.Length > 120 ? ev.Notes[..120] : ev.Notes);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = $"https://www.google.com/search?q={q}", UseShellExecute = true });
                }
            };
            ctxMenu.Items.Add(miDetails);
            ctxMenu.Items.Add(new Separator());
            ctxMenu.Items.Add(miCopy);
            ctxMenu.Items.Add(miSearch);
            _grid.ContextMenu = ctxMenu;
            Grid.SetRow(gridOuter, 3); splitGrid.Children.Add(gridOuter);

            // Hover tooltip (reused across all bar canvases)
            _hoverTip = new ToolTip { Placement = PlacementMode.Mouse };

            return outer;
        }

        // ── Data Loading ──────────────────────────────────────────────────────
        private async Task LoadAsync()
        {
            _btnRefresh.IsEnabled = false;
            _txtStatus.Text       = LanguageService.S("Events", "ReadingEventLog");
            _timelinePanel.Children.Clear();
            _grid.ItemsSource     = null;
            _txtStats.Text        = "";
            _crashBadge.Visibility = Visibility.Collapsed;

            try
            {
                var (events, hasSecLog) = await Task.Run(() => ReadEvents(_days));
                _events  = events;
                _daysSum = BuildDaySummaries(_events, _days);

                DrawTimeline();
                BuildStats(hasSecLog);
                ApplyDayFilter();

                _txtStatus.Text = SMDWin.Views.MainWindow.PT("{0} events loaded from the last {1} days.", _events.Count, _days);
            }
            catch (Exception ex)
            {
                _txtStatus.Text = MainWindow.PT("Error reading Event Log: {0}", ex.Message);
            }
            finally { _btnRefresh.IsEnabled = true; }
        }
    }
}
