using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Models;
using SMDWin.Services;
using Brush  = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color  = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// The right-hand card of Applications, Services and Startup while nothing is selected (mockup "soft"): instead of one
    /// sentence in an empty card, a short summary — the largest / recent / possibly unwanted programs; the services that
    /// crashed or did not start, the changes made from SMD Win (with Undo) and the services of other makers; what Windows
    /// measured as slowing the start, the broken entries (with Clean) and the entries added recently. Clicking a row selects
    /// it in the list. The original sentence stays at the bottom as a hint.
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<StackPanel, string> _summaryHints = new();

        /// <summary>Clears the summary card and keeps its "Selected" caption and its sentence (moved to the bottom).</summary>
        private StackPanel? BeginSummary(StackPanel? host)
        {
            if (host == null) return null;
            if (!_summaryHints.ContainsKey(host))
            {
                string hint = host.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text ?? "";
                _summaryHints[host] = hint;
            }
            host.Children.Clear();
            return host;
        }

        private void EndSummary(StackPanel host)
        {
            string hint = _summaryHints.TryGetValue(host, out var h) ? h : "";
            if (hint.Length == 0) return;
            var sep = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 10), Opacity = 0.7 };
            sep.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            host.Children.Add(sep);
            var t = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = Ui.FontS };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            host.Children.Add(t);
        }

        private static void SummaryCaption(StackPanel host, string text, bool first = false)
        {
            var t = new TextBlock { Text = text.ToUpperInvariant(), FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 0 : 14, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            host.Children.Add(t);
        }

        private Brush SummaryTint(string key, byte alpha)
        {
            var c = (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); b.Freeze(); return b;
        }

        /// <summary>One row: an optional tile, name + sub line, and on the right a value, a pill or a link button.</summary>
        private Border SummaryRow(StackPanel host, string name, string sub, Action? onClick = null,
                                  string? value = null, (string text, string brush)? pill = null, (string text, Func<Task> act)? link = null,
                                  Brush? tile = null, string? initial = null)
        {
            var row = new Border { Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (tile != null)
            {
                var tb = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = tile, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                tb.Child = new TextBlock { Text = initial ?? "", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontS, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(tb);
            }
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var n = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            n.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(n);
            if (sub.Length > 0)
            {
                var s = new TextBlock { Text = sub, FontSize = Ui.FontXS, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = sub };
                s.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(s);
            }
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            FrameworkElement? right = null;
            if (value != null)
            {
                var v = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                v.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
                v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                right = v;
            }
            else if (pill is { } p)
            {
                var pb = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Background = SummaryTint(p.brush, 34), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                var pt = new TextBlock { Text = p.text, FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold };
                pt.SetResourceReference(TextBlock.ForegroundProperty, p.brush);
                pb.Child = pt;
                right = pb;
            }
            else if (link is { } l)
            {
                var b = new Button { Content = l.text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
                b.SetResourceReference(StyleProperty, "GhostButtonStyle");
                b.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
                b.Click += async (_, e) =>
                {
                    e.Handled = true;
                    b.IsEnabled = false;
                    try { await l.act(); } catch (Exception ex) { AppLogger.Warning(ex, "Summary action"); }
                    finally { b.IsEnabled = true; }
                };
                right = b;
            }
            if (right != null) { Grid.SetColumn(right, 2); g.Children.Add(right); }
            row.Child = g;
            if (onClick != null)
            {
                row.Cursor = System.Windows.Input.Cursors.Hand;
                row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) => onClick();
            }
            host.Children.Add(row);
            return row;
        }

        private static string Ago(DateTime when)
        {
            var d = DateTime.Now - when;
            if (d.TotalHours < 1) return PTs("just now");
            if (d.TotalDays < 1) return PTs("{0} h ago", (int)d.TotalHours);
            if (d.TotalDays < 2) return PTs("yesterday");
            return PTs("{0} days ago", (int)d.TotalDays);
        }

        private static string PTs(string en, params object[] args)
        {
            string s = LiteralTexts.Translate(en);
            try { return args.Length > 0 ? string.Format(s, args) : s; } catch { return s; }
        }

        // ══ Applications ════════════════════════════════════════════════════════════════════════════════════════════

        private void RefreshAppsSummary()
        {
            try
            {
                if (BeginSummary(AppDetailEmpty) is not { } host) return;
                var apps = _allApps;
                if (apps.Count == 0) { host.Children.Add(new TextBlock()); EndSummary(host); return; }
                void Select(InstalledApp a) { AppsGrid.SelectedItem = a; AppsGrid.ScrollIntoView(a); }

                SummaryCaption(host, PT("Largest"), first: true);
                foreach (var a in apps.Where(a => a.SizeBytes > 0).OrderByDescending(a => a.SizeBytes).Take(3))
                    SummaryRow(host, a.Name, a.Publisher, () => Select(a), value: a.Size, tile: a.TileBrush, initial: a.Initial);

                var recent = apps.Where(a => a.IsRecent && a.InstallDateRaw != null).OrderByDescending(a => a.InstallDateRaw).Take(3).ToList();
                if (recent.Count > 0)
                {
                    SummaryCaption(host, PT("Installed recently"));
                    foreach (var a in recent)
                        SummaryRow(host, a.Name, Ago(a.InstallDateRaw!.Value), () => Select(a), pill: (PT("New"), "AccentBrush"), tile: a.TileBrush, initial: a.Initial);
                }

                var bloat = apps.Where(a => a.IsBloat).OrderByDescending(a => a.SizeBytes).ToList();
                if (bloat.Count > 0)
                {
                    SummaryCaption(host, $"{PT("Possibly unwanted")} · {bloat.Count}");
                    foreach (var a in bloat.Take(3))
                        SummaryRow(host, a.Name, a.Publisher, () => Select(a), pill: (PT("Check it"), "StatusWarningBrush"), tile: a.TileBrush, initial: a.Initial);
                }
                EndSummary(host);
            }
            catch (Exception ex) { AppLogger.Debug($"RefreshAppsSummary: {ex.Message}"); }
        }

        // ══ Services ════════════════════════════════════════════════════════════════════════════════════════════════

        private List<ServiceProblemsService.Problem>? _svcProblems;
        private int _svcThirdCount = -1, _svcThirdRunning;
        private bool _svcSummaryHooked, _svcSummaryLoading;

        private async void RefreshServicesSummary()
        {
            try
            {
                if (!_svcSummaryHooked)
                {
                    _svcSummaryHooked = true;
                    ServiceChangeLog.Changed += () => Dispatcher.InvokeAsync(RefreshServicesSummary);
                }
                DrawServicesSummary();
                if (_svcProblems == null && !_svcSummaryLoading)
                {
                    _svcSummaryLoading = true;
                    // the event log and the full service list are read once, in the background
                    _svcProblems = await Task.Run(() => ServiceProblemsService.Recent());
                    try
                    {
                        var all = await _svcService.GetServicesAsync(onlyKnown: false);
                        var third = all.Where(s => s.IsThirdParty).ToList();
                        _svcThirdCount = third.Count;
                        _svcThirdRunning = third.Count(s => s.Status == "Running");
                    }
                    catch (Exception ex) { AppLogger.Debug($"Services summary count: {ex.Message}"); }
                    _svcSummaryLoading = false;
                    DrawServicesSummary();
                }
            }
            catch (Exception ex) { AppLogger.Debug($"RefreshServicesSummary: {ex.Message}"); }
        }

        private void DrawServicesSummary()
        {
            if (BeginSummary(ServiceDetailEmpty) is not { } host) return;
            bool first = true;
            var problems = _svcProblems ?? new();
            if (problems.Count > 0)
            {
                SummaryCaption(host, PT("Recent problems"), first); first = false;
                foreach (var p in problems.Take(4))
                {
                    string sub = (p.Crashed ? PT("stopped unexpectedly") : PT("did not start")) + " · "
                               + (p.Count > 1 ? PT("{0} times", p.Count) + " · " : "") + Ago(p.Last);
                    SummaryRow(host, p.Service, sub, () => SelectServiceByDisplayName(p.Service),
                               pill: p.Crashed ? (PT("Crashed"), "StatusErrorBrush") : (PT("Did not start"), "StatusWarningBrush"));
                }
            }

            var changes = ServiceChangeLog.List();
            if (changes.Count > 0)
            {
                SummaryCaption(host, PT("Changed from SMD Win"), first); first = false;
                foreach (var c in changes.Take(4))
                {
                    var entry = c;
                    SummaryRow(host, c.DisplayName.Length > 0 ? c.DisplayName : c.Name,
                               $"{PT(c.From)} → {PT(c.To)} · {SMDWin.Services.DateFormatHelper.FormatDate(c.When)}",
                               () => SelectServiceByName(entry.Name),
                               link: (PT("Undo"), async () =>
                               {
                                   bool ok = await ServiceChangeLog.UndoAsync(entry, _svcService);
                                   if (ok) { ShowToast(PT("Put back: {0}", entry.DisplayName)); await ReloadServicesAsync(); RefreshServiceToggleLabels(); }
                                   else ShowToastWarning(PT("Could not change the service — SMD Win needs to run as administrator"));
                               }));
                }
            }

            if (_svcThirdCount > 0 && !_servicesThirdView)
            {
                SummaryCaption(host, PT("Services of other makers"), first); first = false;
                SummaryRow(host, PT("{0} services", _svcThirdCount), PT("not part of Windows · {0} running", _svcThirdRunning),
                           link: (PT("Show"), async () => await LoadThirdPartyServicesAsync()));
            }
            if (first)
            {
                var ok = new TextBlock { Text = _svcSummaryLoading ? PT("Reading the event log…") : PT("No service problems in the last 7 days."), FontSize = Ui.FontS, Margin = new Thickness(0, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
                ok.SetResourceReference(TextBlock.ForegroundProperty, _svcSummaryLoading ? "TextSecondaryBrush" : "StatusGoodBrush");
                host.Children.Add(ok);
            }
            EndSummary(host);
        }

        private void SelectServiceByName(string name)
        {
            var row = (ServicesGrid.ItemsSource as IEnumerable<WinServiceEntry>)?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row != null) { ServicesGrid.SelectedItem = row; ServicesGrid.ScrollIntoView(row); }
            else { TxtSvcSearch.Text = name; }
        }

        private void SelectServiceByDisplayName(string display)
        {
            var row = (ServicesGrid.ItemsSource as IEnumerable<WinServiceEntry>)?.FirstOrDefault(x => string.Equals(x.DisplayName, display, StringComparison.OrdinalIgnoreCase));
            if (row != null) { ServicesGrid.SelectedItem = row; ServicesGrid.ScrollIntoView(row); }
            else { TxtSvcSearch.Text = display; }
        }

        // ══ Startup ═════════════════════════════════════════════════════════════════════════════════════════════════

        private void RefreshStartupSummary()
        {
            try
            {
                if (BeginSummary(StartupDetailEmpty) is not { } host) return;
                var all = _allStartupEntries ?? new List<StartupEntry>();
                void Select(StartupEntry e) { StartupGrid.SelectedItem = e; StartupGrid.ScrollIntoView(e); }
                bool first = true;

                if (_startupDelays.Count > 0)
                {
                    SummaryCaption(host, PT("What slowed the start"), first); first = false;
                    foreach (var d in _startupDelays.Take(3))
                    {
                        var match = all.FirstOrDefault(e => string.Equals(System.IO.Path.GetFileName(e.ExeName ?? ""), d.File, StringComparison.OrdinalIgnoreCase));
                        string kind = d.Kind == 103 ? PT("service") : d.Kind == 102 ? PT("driver") : PT("program");
                        SummaryRow(host, d.Friendly, $"{kind} · {PT("measured by Windows")} · {Ago(d.Last)}",
                                   match != null ? () => Select(match) : null, value: $"+{d.DelayMs / 1000.0:0.#} s");
                    }
                }

                var slow = all.Where(e => e.IsEnabled && !e.IsBroken && (e.MeasuredDelayMs >= 2000 || e.ImpactRating == "High")).ToList();
                var broken = all.Where(e => e.IsBroken).ToList();
                if (slow.Count > 0 || broken.Count > 0)
                {
                    SummaryCaption(host, PT("Suggestions"), first); first = false;
                    if (slow.Count > 0)
                        SummaryRow(host, PT("{0} programs slow the start noticeably", slow.Count),
                                   string.Join(", ", slow.Take(3).Select(e => e.Name)) + " · " + PT("turning them off is reversible"),
                                   () => Select(slow[0]));
                    if (broken.Count > 0)
                        SummaryRow(host, PT("{0} broken entries", broken.Count), PT("the file no longer exists"), () => Select(broken[0]),
                                   link: (PT("Clean"), async () =>
                                   {
                                       if (!AppDialog.Confirm(PT("Remove the {0} entries whose file no longer exists? They start nothing.", broken.Count), _LS("Common", "Confirm"))) return;
                                       foreach (var b in broken)
                                       {
                                           try { _startupSvc.Remove(b); ToolActivityLog.Add("Startup", $"Remove broken entry {b.Name}", "info"); }
                                           catch (Exception ex) { AppLogger.Debug($"Remove broken {b.Name}: {ex.Message}"); }
                                       }
                                       await LoadStartupAsync();
                                   }));
                }

                var added = all.Where(e => e.AddedRecently && !e.IsBroken).Take(3).ToList();
                if (added.Count > 0)
                {
                    SummaryCaption(host, PT("Added recently"), first); first = false;
                    foreach (var e in added)
                        SummaryRow(host, e.Name, e.WhereLabel, () => Select(e), pill: (PT("New"), "AccentBrush"));
                }
                EndSummary(host);
            }
            catch (Exception ex) { AppLogger.Debug($"RefreshStartupSummary: {ex.Message}"); }
        }
    }
}
