using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Brush     = System.Windows.Media.Brush;
using Brushes   = System.Windows.Media.Brushes;
using Color     = System.Windows.Media.Color;
using TextBlock = System.Windows.Controls.TextBlock;
using Border    = System.Windows.Controls.Border;
using Control   = System.Windows.Controls.Control;
using Panel     = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    /// <summary>
    /// The automatic checks run on every self-test screenshot, and the report built from them:
    /// texts cut off, text too faint for its background, elements sticking out of their card or overlapping, texts left in
    /// English, values that stayed empty; plus page times, window blocks, memory / Windows resources across the two tours and
    /// the errors the app ignored. Everything ends up in report.html (with the pictures) and in text files in the zip.
    /// </summary>
    public partial class MainWindow
    {
        internal sealed record UiIssue(string Kind, string Shot, string Text, string Detail);

        internal sealed record PageTime(string Theme, string Page, long OpenMs, int Stalls, long StallMs);

        internal sealed record ResourceSample(string When, long WorkingMB, long PrivateMB, long ManagedMB, int Handles, int Gdi, int User);

        internal sealed class SelfTestUiData
        {
            public readonly List<UiIssue> Issues = new();
            public readonly List<PageTime> Times = new();
            public readonly List<ResourceSample> Resources = new();
            /// <summary>Private memory after each running tool (after a clean-up) — shows which tool keeps memory.</summary>
            public readonly List<(string What, long PrivateMB, long ManagedMB)> ToolMemory = new();
            public readonly List<(string Theme, long Ms)> ThemeSwitch = new();
            public readonly List<string> DataChecks = new();
            public Dictionary<string, int> SwallowedAtStart = new();
            public int StallsAtStart;
        }

        // ── Window resources (memory and Windows objects) ─────────────────────────────────────────────────────────────

        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

        internal static ResourceSample SampleResources(string when, bool collect)
        {
            if (collect)
            {
                // the same compacting collection the app does after a large test (MemoryRelease), so the row shows what stays
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            int gdi = 0, user = 0;
            try { gdi = GetGuiResources(p.Handle, 0); user = GetGuiResources(p.Handle, 1); }
            catch (Exception ex) { AppLogger.Debug($"GetGuiResources: {ex.Message}"); }
            return new ResourceSample(when, p.WorkingSet64 >> 20, p.PrivateMemorySize64 >> 20, GC.GetTotalMemory(false) >> 20,
                                      p.HandleCount, gdi, user);
        }

        // ── The checks on one picture ─────────────────────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> EmptyMarks = new(StringComparer.Ordinal) { "—", "--", "-", "N/A", "n/a", "…", "...", "?" };

        // Pages whose values appear only after the user starts something (a scan, a speed test, a lookup, a battery run, a
        // clean-up analysis): before that "—" is the correct state, not a missing reading. The self-test does not start them,
        // so the empty-value check is skipped there (the 7 x 2 placeholders of the 07.11 report were all of this kind).
        private static readonly string[] RunFirstShots =
            { "Disk-Surface", "Network-SpeedTest", "Network-Dns", "Window-BatteryTest", "Window-AutoClean" };
        private static bool IsRunFirstShot(string shot) => RunFirstShots.Any(k => shot.Contains(k, StringComparison.OrdinalIgnoreCase));

        // common English words: a visible text containing them while the app is in another language is most likely untranslated
        // only words that do not also exist in Romanian, German or French ("min", "max", "Scan", "Driver", "Start" are left out)
        private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "of", "and", "to", "for", "is", "not", "all", "with", "your", "this", "left", "used", "free", "now", "last",
            "running", "stopped", "click", "open", "show", "found", "none", "available", "installed", "usable", "speed",
            "healthy", "temperature", "battery", "memory", "storage", "drive", "drives", "network", "settings", "update", "updates",
            "errors", "warning", "warnings", "today", "yesterday", "days", "hours", "minutes", "ago", "since", "until", "read",
            "write", "written", "spare", "media", "critical", "cycles", "values", "within", "limits", "while", "runs", "session",
            "every", "round", "trip", "time", "track", "background", "window", "closed", "continuous", "monitoring", "average",
            "lifetime", "commands", "completed", "processing", "wake", "keeps", "almost", "rows", "button", "above", "drive's",
            "use", "keep", "sleep", "shutdown", "lost", "never", "reached", "exceeded", "remaining", "estimate", "rated",
        };

        /// <summary>
        /// All checks on the visible part of <paramref name="root"/>: cut-off texts, low contrast, sticking out / overlapping,
        /// untranslated texts and values that stayed empty.
        /// </summary>
        internal static void CheckPicture(FrameworkElement root, string shot, Brush pageBackground, List<UiIssue> issues)
        {
            if (issues.Count > 4000) return;
            double ppd = VisualTreeHelper.GetDpi(root).PixelsPerDip;
            bool otherLanguage = LanguageService.CurrentCode != "en";
            var seen = new HashSet<string>();
            var textsInCards = new Dictionary<Border, List<(TextBlock Tb, Rect R)>>();
            Color baseColor = (pageBackground as SolidColorBrush)?.Color ?? Colors.White;

            bool runFirst = IsRunFirstShot(shot);

            void Add(string kind, string text, string detail)
            {
                string t = text.Replace('\n', ' ').Replace('\r', ' ');
                if (t.Length > 80) t = t[..80] + "…";
                if (seen.Add(kind + "|" + t + "|" + detail)) issues.Add(new UiIssue(kind, shot, t, detail));
            }

            void Walk(DependencyObject d, bool enabled, bool inData)
            {
                if (d is UIElement ue && !ue.IsVisible) return;
                if (d is UIElement ue2 && !ue2.IsEnabled) enabled = false;

                if (d is DataGridCell) { inData = true; }
                if (d is TextBlock tb && tb.ActualWidth > 2 && tb.ActualHeight > 2)
                {
                    string text = tb.Text;
                    if (string.IsNullOrEmpty(text)) text = string.Concat(tb.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        CheckCutOff(tb, text, ppd, Add);
                        if (enabled) CheckContrast(tb, text, baseColor, Add);
                        if (otherLanguage && !inData) CheckEnglish(text, Add);   // table cells hold data (rule names, processes)
                        if (!inData && !runFirst && EmptyMarks.Contains(text.Trim()))
                            Add("Empty value", NearbyLabel(tb) ?? "(no label)", $"shows \"{text.Trim()}\"");
                        RememberInCard(tb, root, textsInCards);
                        CheckInsideCard(tb, text, root, Add);
                    }
                }
                else if (d is MiniLineChart chart && chart.ActualWidth > 20 && !chart.HasData && !runFirst)
                {
                    Add("Empty value", NearbyLabel(chart) ?? chart.Name, "chart without data after the page settled");
                }

                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), enabled, inData);
            }

            try { Walk(root, true, false); }
            catch (Exception ex) { AppLogger.Debug($"CheckPicture walk: {ex.Message}"); }

            // texts in the same card that are drawn over each other
            foreach (var list in textsInCards.Values)
            {
                if (list.Count > 160) continue;
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var a = list[i].R; var b = list[j].R;
                        a.Inflate(-1.5, -1.5); b.Inflate(-1.5, -1.5);
                        if (a.Width <= 0 || b.Width <= 0 || !a.IntersectsWith(b)) continue;
                        if (IsAncestorOrSame(list[i].Tb, list[j].Tb) || IsAncestorOrSame(list[j].Tb, list[i].Tb)) continue;
                        var x = Rect.Intersect(a, b);
                        if (x.Width < 3 || x.Height < 3) continue;
                        Add("Overlap", $"{Short(list[i].Tb.Text)} ⟷ {Short(list[j].Tb.Text)}", $"{x.Width:0}×{x.Height:0} px drawn over each other");
                    }
            }
        }

        private static string Short(string s) => s.Length > 30 ? s[..30] + "…" : s;

        private static bool IsAncestorOrSame(DependencyObject a, DependencyObject b)
        {
            for (DependencyObject? p = b; p != null; p = VisualTreeHelper.GetParent(p)) if (ReferenceEquals(p, a)) return true;
            return false;
        }

        /// <summary>A single-line text that needs more room than it has (shown with "…" or clipped).</summary>
        private static void CheckCutOff(TextBlock tb, string text, double ppd, Action<string, string, string> add)
        {
            if (tb.TextWrapping != TextWrapping.NoWrap) return;
            // a row made of differently formatted pieces ("SMD " light + "Win" bold) cannot be measured as one text
            if (string.IsNullOrEmpty(tb.Text)) return;
            // a text hidden on purpose until it fits (Health Check's "5 checks" at opacity 0) is not a cut-off text
            double op = 1;
            for (DependencyObject? p = tb; p != null && op >= 0.25; p = VisualTreeHelper.GetParent(p)) if (p is UIElement u) op *= u.Opacity;
            if (op < 0.25) return;
            try
            {
                var ft = new FormattedText(text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, ppd);
                double need = ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right;
                // a few pixels are measuring noise (the window draws text slightly narrower than the ideal measure)
                if (need > tb.ActualWidth + 4) add("Cut off", text, $"needs {need:0} px, has {tb.ActualWidth:0} px");
            }
            catch (Exception ex) { AppLogger.Debug($"CheckCutOff: {ex.Message}"); }
        }

        /// <summary>Text against the colour actually behind it (all the backgrounds under it blended, down to the page).</summary>
        private static void CheckContrast(TextBlock tb, string text, Color pageBase, Action<string, string, string> add)
        {
            if (tb.Foreground is not SolidColorBrush fgBrush) return;
            double opacity = 1;
            var layers = new List<(Color C, double A)>();
            for (DependencyObject? p = tb; p != null; p = VisualTreeHelper.GetParent(p))
            {
                if (p is UIElement u) opacity *= u.Opacity;
                Brush? bg = p switch
                {
                    Border b => b.Background,
                    Panel pn => pn.Background,
                    TextBlock t when !ReferenceEquals(t, tb) => t.Background,
                    _ => null,
                };
                var avg = bg == null ? null : Average(bg);
                if (avg is { } v && v.Item2 > 0.01) layers.Add((v.Item1, v.Item2));
                if (p is Window) break;
            }
            if (opacity < 0.25) return;   // a faded-out element (e.g. in the middle of an animation) — not a text to read
            // blend from the page up to the text
            double r = pageBase.R, g = pageBase.G, bl = pageBase.B;
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                var (c, a) = layers[i];
                r = r * (1 - a) + c.R * a; g = g * (1 - a) + c.G * a; bl = bl * (1 - a) + c.B * a;
            }
            var fc = fgBrush.Color;
            double fa = fc.A / 255.0 * fgBrush.Opacity * opacity;
            double fr = r * (1 - fa) + fc.R * fa, fg = g * (1 - fa) + fc.G * fa, fb = bl * (1 - fa) + fc.B * fa;
            double ratio = ContrastRatio(fr, fg, fb, r, g, bl);
            if (ratio < 3.0)
                add(ratio < 2.0 ? "Hard to read" : "Low contrast", text,
                    $"contrast {ratio:0.0}:1 (text #{(int)fr:X2}{(int)fg:X2}{(int)fb:X2} on #{(int)r:X2}{(int)g:X2}{(int)bl:X2})");
        }

        private static (Color, double)? Average(Brush b)
        {
            switch (b)
            {
                case SolidColorBrush s:
                    return (s.Color, s.Color.A / 255.0 * s.Opacity);
                case GradientBrush gb when gb.GradientStops.Count > 0:
                    double r = 0, g = 0, bl = 0, a = 0;
                    foreach (var st in gb.GradientStops) { r += st.Color.R; g += st.Color.G; bl += st.Color.B; a += st.Color.A; }
                    int n = gb.GradientStops.Count;
                    return (Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(bl / n)), a / n / 255.0 * gb.Opacity);
                default:
                    return null;
            }
        }

        private static double ContrastRatio(double r1, double g1, double b1, double r2, double g2, double b2)
        {
            static double Lin(double c) { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            double l1 = 0.2126 * Lin(r1) + 0.7152 * Lin(g1) + 0.0722 * Lin(b1);
            double l2 = 0.2126 * Lin(r2) + 0.7152 * Lin(g2) + 0.0722 * Lin(b2);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }

        /// <summary>A text that still reads English while the app runs in another language.</summary>
        private static void CheckEnglish(string text, Action<string, string, string> add)
        {
            string t = text.Trim();
            if (!LiteralTexts.IsTranslatable(t)) return;
            var words = System.Text.RegularExpressions.Regex.Matches(t, "[A-Za-z']{2,}").Select(m => m.Value).ToList();
            if (words.Count == 0) return;
            bool hasKey = LiteralTexts.HasTranslation(t) && LiteralTexts.Translate(t) != t;
            int english = words.Count(w => EnglishWords.Contains(w));
            bool looksEnglish = english >= 2 && english * 2 >= words.Count;
            if (hasKey) add("Untranslated", t, "a translation exists but the English text is shown");
            else if (looksEnglish) add("Untranslated", t, "no translation key");
        }

        /// <summary>The label next to a value: the first other text in the same small group (e.g. "Clock now" above "—").</summary>
        private static string? NearbyLabel(DependencyObject el)
        {
            DependencyObject? p = VisualTreeHelper.GetParent(el);
            for (int up = 0; p != null && up < 3; up++, p = VisualTreeHelper.GetParent(p))
            {
                if (p is not Panel pn) continue;
                foreach (var child in pn.Children)
                {
                    if (ReferenceEquals(child, el)) continue;
                    var tb = child as TextBlock ?? (child is DependencyObject dobj ? FirstText(dobj) : null);
                    if (tb != null && !string.IsNullOrWhiteSpace(tb.Text) && !EmptyMarks.Contains(tb.Text.Trim())) return tb.Text.Trim();
                }
            }
            return null;
        }

        private static TextBlock? FirstText(DependencyObject d)
        {
            if (d is TextBlock t) return t;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n && i < 6; i++) if (FirstText(VisualTreeHelper.GetChild(d, i)) is { } f) return f;
            return null;
        }

        /// <summary>The card a text sits in: the nearest rounded border with a background.</summary>
        private static Border? CardOf(DependencyObject el)
        {
            for (DependencyObject? p = VisualTreeHelper.GetParent(el); p != null; p = VisualTreeHelper.GetParent(p))
                if (p is Border b && b.Background != null && b.CornerRadius.TopLeft >= 6 && b.ActualWidth > 60) return b;
            return null;
        }

        private static void RememberInCard(TextBlock tb, FrameworkElement root, Dictionary<Border, List<(TextBlock, Rect)>> map)
        {
            var card = CardOf(tb);
            if (card == null) return;
            try
            {
                // the area really covered by the glyphs (not the whole stretched box of the text)
                var ink = VisualTreeHelper.GetDescendantBounds(tb);
                if (ink.IsEmpty) return;
                var r = tb.TransformToAncestor(root).TransformBounds(ink);
                if (!map.TryGetValue(card, out var list)) map[card] = list = new List<(TextBlock, Rect)>();
                list.Add((tb, r));
            }
            catch (Exception ex) { AppLogger.Debug($"RememberInCard: {ex.Message}"); }
        }

        /// <summary>A text whose drawing goes past the edge of its card (or of the window).</summary>
        private static void CheckInsideCard(TextBlock tb, string text, FrameworkElement root, Action<string, string, string> add)
        {
            try
            {
                var ink = VisualTreeHelper.GetDescendantBounds(tb);
                if (ink.IsEmpty) return;
                var r = tb.TransformToAncestor(root).TransformBounds(ink);
                var win = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                if (r.Right > win.Right + 2 || r.Bottom > win.Bottom + 2 || r.Left < -2)
                {
                    if (!InsideScroller(tb)) add("Sticks out", text, "drawn outside the window");
                    return;
                }
                var card = CardOf(tb);
                if (card == null || card.ClipToBounds || InsideScroller(tb)) return;
                var c = card.TransformToAncestor(root).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                double over = Math.Max(Math.Max(r.Right - c.Right, c.Left - r.Left), Math.Max(r.Bottom - c.Bottom, c.Top - r.Top));
                if (over > 2) add("Sticks out", text, $"{over:0} px outside its card");
            }
            catch (Exception ex) { AppLogger.Debug($"CheckInsideCard: {ex.Message}"); }
        }

        private static bool InsideScroller(DependencyObject el)
        {
            for (DependencyObject? p = VisualTreeHelper.GetParent(el); p != null; p = VisualTreeHelper.GetParent(p))
                if (p is ScrollViewer or ScrollContentPresenter) return true;
            return false;
        }

        // ── The report ────────────────────────────────────────────────────────────────────────────────────────────────

        internal static string IssuesText(SelfTestUiData d)
        {
            var sb = new StringBuilder();
            foreach (var g in d.Issues.GroupBy(i => i.Kind).OrderBy(g => KindOrder(g.Key)))
            {
                sb.AppendLine($"══ {g.Key} — {g.Count()} ══");
                foreach (var i in g) sb.AppendLine($"  {i.Shot} | {i.Text} | {i.Detail}");
                sb.AppendLine();
            }
            if (d.Issues.Count == 0) sb.AppendLine("No interface problems found.");
            return sb.ToString();
        }

        internal static string PerformanceText(SelfTestUiData d)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Page opening times (from the click to the page drawn), window blocks over 0.5 s while the page settled:");
            foreach (var t in d.Times.OrderByDescending(t => t.OpenMs))
                sb.AppendLine($"  {t.Theme,-5} {t.Page,-22} {t.OpenMs,6} ms{(t.Stalls > 0 ? $"   ⚠ {t.Stalls} block(s), {t.StallMs} ms" : "")}");
            sb.AppendLine();
            foreach (var (theme, ms) in d.ThemeSwitch) sb.AppendLine($"Theme switch to {theme}: {ms} ms");
            sb.AppendLine($"Window blocks since start: {UiStallLogger.StallCount} (longest {UiStallLogger.LongestStallMs} ms), {UiStallLogger.StallCount - d.StallsAtStart} during the screenshots");
            sb.AppendLine();
            sb.AppendLine("Memory and Windows resources (after a clean-up), across the two tours of all pages:");
            sb.AppendLine("  when                      working  private  managed  handles  GDI  USER");
            foreach (var r in d.Resources)
                sb.AppendLine($"  {r.When,-24} {r.WorkingMB,6} MB {r.PrivateMB,6} MB {r.ManagedMB,6} MB {r.Handles,7} {r.Gdi,5} {r.User,5}");
            if (d.Resources.Count >= 3)
            {
                // "tour 2" is row 2; the last row (after the running tools) was compared before, so +1 190 MB of test
                // buffers was written "(stable)". The tools get their own line.
                var a = d.Resources[1]; var b = d.Resources[2];
                sb.AppendLine($"  growth between tour 1 and tour 2: private {b.PrivateMB - a.PrivateMB:+0;-0} MB, managed {b.ManagedMB - a.ManagedMB:+0;-0} MB, " +
                              $"GDI {b.Gdi - a.Gdi:+0;-0}, USER {b.User - a.User:+0;-0}, handles {b.Handles - a.Handles:+0;-0}" +
                              ((b.Gdi - a.Gdi) > 150 || (b.User - a.User) > 150 || (b.ManagedMB - a.ManagedMB) > 120 || (b.PrivateMB - a.PrivateMB) > 200
                                  ? "   ⚠ keeps growing — possible leak" : "   (stable)"));
                if (d.Resources.Count >= 4)
                {
                    var c = d.Resources[^1];
                    sb.AppendLine($"  after the running tools: private {c.PrivateMB - b.PrivateMB:+0;-0} MB compared with tour 2" +
                                  (c.PrivateMB - b.PrivateMB > 300 ? "   ⚠ the test buffers were not given back" : "   (given back)"));
                }
                if (d.ToolMemory.Count > 0)
                {
                    sb.AppendLine("  private memory after each running tool (after a clean-up):");
                    long prev = b.PrivateMB;
                    foreach (var (what, priv, managed) in d.ToolMemory)
                    {
                        sb.AppendLine($"    {what,-22} {priv,6} MB  ({priv - prev:+0;-0} MB, live objects {managed} MB){(priv - prev > 200 ? "   ⚠ kept" : "")}");
                        prev = priv;
                    }
                }
            }
            sb.AppendLine();
            sb.AppendLine("Errors the app caught and ignored (place: count since start, + during this self-test):");
            var now = AppLogger.SwallowedCounts();
            foreach (var kv in now.OrderByDescending(k => k.Value).Take(40))
            {
                d.SwallowedAtStart.TryGetValue(kv.Key, out int before);
                sb.AppendLine($"  {kv.Key,-44} {kv.Value,5}{(kv.Value > before ? $"  (+{kv.Value - before})" : "")}");
            }
            if (now.Count == 0) sb.AppendLine("  none");
            return sb.ToString();
        }

        private static int KindOrder(string kind) => kind switch
        {
            "Hard to read" => 0, "Overlap" => 1, "Sticks out" => 2, "Cut off" => 3, "Low contrast" => 4, "Untranslated" => 5, "Empty value" => 6, _ => 9,
        };

        /// <summary>report.html: the summary, the problems (with a link to the picture), the times, the data checks and all the
        /// pictures, Dark next to Light. Paths are relative to the zip root.</summary>
        internal static string ReportHtml(SelfTestUiData d, string environment, IReadOnlyList<string> shots, string shotsRoot)
        {
            static string H(string s) => WebUtility.HtmlEncode(s);
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>SMD Win self-test</title><style>")
              .Append("body{font:14px Segoe UI,Arial,sans-serif;margin:24px;background:#f4f6fa;color:#0f172a}h1{margin:0 0 4px}h2{margin:28px 0 8px}")
              .Append("pre{background:#fff;border:1px solid #dde3ec;border-radius:8px;padding:12px;white-space:pre-wrap}")
              .Append("table{border-collapse:collapse;background:#fff;width:100%}td,th{border-bottom:1px solid #e6eaf0;padding:5px 8px;text-align:left;vertical-align:top}")
              .Append(".k{font-weight:600}.chip{display:inline-block;border-radius:10px;padding:1px 9px;margin:2px;background:#e8eefc;color:#1d4ed8}")
              .Append(".bad{background:#fde8e8;color:#b91c1c}.warn{background:#fdf1e2;color:#b45309}")
              .Append(".pair{display:flex;gap:8px;margin:14px 0}.pair figure{margin:0;flex:1}.pair img{width:100%;border:1px solid #cfd6e2;border-radius:6px}")
              .Append("figcaption{font-size:12px;color:#475569;margin-top:2px}a{color:#1d4ed8}</style></head><body>");
            sb.Append("<h1>SMD Win — self-test</h1><pre>").Append(H(environment)).Append("</pre>");

            sb.Append("<h2>Interface problems</h2><div>");
            foreach (var g in d.Issues.GroupBy(i => i.Kind).OrderBy(g => KindOrder(g.Key)))
                sb.Append($"<span class='chip {(KindOrder(g.Key) <= 2 ? "bad" : KindOrder(g.Key) <= 4 ? "warn" : "")}'>{H(g.Key)}: {g.Count()}</span>");
            if (d.Issues.Count == 0) sb.Append("<span class='chip'>none found</span>");
            sb.Append("</div><table><tr><th>Kind</th><th>Picture</th><th>Text</th><th>Detail</th></tr>");
            foreach (var i in d.Issues.OrderBy(i => KindOrder(i.Kind)).Take(1500))
            {
                string img = shots.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s) == i.Shot.Split(" · ").Last()
                                                       && s.Contains(Path.DirectorySeparatorChar + i.Shot.Split(" · ")[0].ToLowerInvariant() + Path.DirectorySeparatorChar)) ?? "";
                string link = img.Length > 0 ? $"<a href='screenshots/{H(Path.GetRelativePath(shotsRoot, img).Replace('\\', '/'))}'>{H(i.Shot)}</a>" : H(i.Shot);
                sb.Append($"<tr><td class='k'>{H(i.Kind)}</td><td>{link}</td><td>{H(i.Text)}</td><td>{H(i.Detail)}</td></tr>");
            }
            sb.Append("</table>");

            if (d.DataChecks.Count > 0)
                sb.Append("<h2>Data checks (two sources compared, sensors watched)</h2><pre>").Append(H(string.Join("\n", d.DataChecks))).Append("</pre>");
            sb.Append("<h2>Speed, memory and ignored errors</h2><pre>").Append(H(PerformanceText(d))).Append("</pre>");

            sb.Append("<h2>Pictures (Dark | Light)</h2>");
            var dark = shots.Where(s => s.Contains(Path.DirectorySeparatorChar + "dark" + Path.DirectorySeparatorChar) && s.EndsWith(".png")).ToList();
            foreach (var dk in dark)
            {
                string name = Path.GetFileName(dk);
                string lt = shots.FirstOrDefault(s => s.EndsWith(Path.DirectorySeparatorChar + "light" + Path.DirectorySeparatorChar + name)) ?? "";
                string Rel(string f) => "screenshots/" + Path.GetRelativePath(shotsRoot, f).Replace('\\', '/');
                sb.Append($"<div class='pair'><figure><a href='{H(Rel(dk))}'><img loading='lazy' src='{H(Rel(dk))}'></a><figcaption>Dark · {H(name)}</figcaption></figure>");
                if (lt.Length > 0) sb.Append($"<figure><a href='{H(Rel(lt))}'><img loading='lazy' src='{H(Rel(lt))}'></a><figcaption>Light · {H(name)}</figcaption></figure>");
                sb.Append("</div>");
            }
            sb.Append("</body></html>");
            return sb.ToString();
        }
    }
}
