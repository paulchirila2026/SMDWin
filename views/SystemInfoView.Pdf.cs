using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SMDWin.Models;
using SMDWin.Services;

// Explicit aliases — avoid conflicts with System.Windows.Forms.* and System.IO.*
using WpfColor       = System.Windows.Media.Color;
using WpfButton      = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox     = System.Windows.Controls.TextBox;
using WpfClipboard   = System.Windows.Clipboard;
using WpfFontFamily  = System.Windows.Media.FontFamily;
using WpfHAlign      = System.Windows.HorizontalAlignment;
using WpfVAlign      = System.Windows.VerticalAlignment;
using WpfTabControl  = System.Windows.Controls.TabControl;
using WpfTabItem     = System.Windows.Controls.TabItem;
using WpfBrushes     = System.Windows.Media.Brushes;
using PenLineCap     = System.Windows.Media.PenLineCap;
using PenLineJoin    = System.Windows.Media.PenLineJoin;
using WpfPath        = System.Windows.Shapes.Path;
using WpfCursors     = System.Windows.Input.Cursors;
using WpfViewbox     = System.Windows.Controls.Viewbox;
using WpfCanvas      = System.Windows.Controls.Canvas;
using WpfPanel       = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    public partial class SystemInfoView
    {
        /// <summary>A pagination engine shared by the 3 templates — a single implementation of "when it no longer fits, move to
        /// a new page", instead of 3 manually synced copies of the same logic (a classic source of bugs).</summary>
        private sealed class PdfPager
        {
            public readonly PdfDocument Doc;
            public readonly double PageW = 595, PageH = 842, Margin = 40;
            public double Y;
            public int PageNumber;
            public XGraphics G = null!;
            private readonly Action<PdfPager> _drawHeader;

            public PdfPager(PdfDocument doc, Action<PdfPager> drawHeader)
            {
                Doc = doc; _drawHeader = drawHeader;
                NewPage();
            }

            public void NewPage()
            {
                PageNumber++;
                var page = Doc.AddPage();
                page.Width  = XUnitPt.FromPoint(PageW);
                page.Height = XUnitPt.FromPoint(PageH);
                G = XGraphics.FromPdfPage(page);
                G.DrawRectangle(XBrushes.White, 0, 0, PageW, PageH);
                _drawHeader(this); // sets Y at the end of the header area
            }

            /// <summary>If the next block (height `needed`) no longer fits on the current page, moves to a new one BEFORE drawing it.</summary>
            public void EnsureSpace(double needed)
            {
                if (Y + needed > PageH - Margin) NewPage();
            }
        }

        private static (byte r, byte g, byte b) CategoryColor(string title)
        {
            string t = title.ToLowerInvariant();
            if (t.Contains("process") || t.Contains("cpu"))              return (0x37, 0x8A, 0xDD); // blue
            if (t.Contains("memory") || t.Contains("memorie") || t.Contains("ram")) return (0x7F, 0x77, 0xDD); // purple
            if (t.Contains("storage") || t.Contains("stocare") || t.Contains("disk")) return (0x1D, 0x9E, 0x75); // teal
            if (t.Contains("graphic") || t.Contains("grafic") || t.Contains("gpu")) return (0xD8, 0x5A, 0x30); // coral
            if (t.Contains("batter") || t.Contains("baterie") || t.Contains("chassis") || t.Contains("șasiu")) return (0xBA, 0x75, 0x17); // amber
            if (t.Contains("network") || t.Contains("rețea") || t.Contains("bluetooth")) return (0x63, 0x99, 0x22); // green
            if (t.Contains("display") || t.Contains("afiș")) return (0xD4, 0x53, 0x7E); // pink
            if (t.Contains("periph") || t.Contains("periferic") || t.Contains("usb") || t.Contains("audio")) return (0x1D, 0x9E, 0x75); // teal
            return (0x5F, 0x5E, 0x5A); // gray fallback (motherboard, chassis etc.)
        }

        private void ExportToPdf()
        {
            var cards = _cardData
                .Select(c => (Title: c.Title, Subtitle: c.Subtitle, c.Rows))
                .ToList();
            string identity = _txtHeaderSubtitle?.Text ?? "";

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = L("Save PDF Report", "Salvează raport PDF", "PDF-Bericht speichern", "Enregistrer le rapport PDF"),
                Filter     = "PDF Files|*.pdf",
                FileName   = $"SMDWin_SystemInfo_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
                DefaultExt = ".pdf",
                InitialDirectory = SettingsService.GetEffectiveReportFolder(),
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                PdfKit.EnsureFontResolver();

                var doc = new PdfDocument();
                doc.Info.Title   = "Hardware report";
                doc.Info.Author  = "SMD Win";
                doc.Info.Creator = "SMD Win";

                string template = SettingsService.Current.HwInfoPdfTemplate ?? "OnePage";
                switch (template)
                {
                    case "OnePage":
                    case "AppStyle": BuildPdfOnePage(doc, cards, identity);  break;
                    case "Summary":  BuildPdfSummary(doc, cards, identity);  break;
                    default:         BuildPdfTechnical(doc, cards, identity); break;
                }

                doc.Save(dlg.FileName);
                doc.Close();
                try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    L("PDF export error:\n", "Eroare la export PDF:\n", "PDF-Exportfehler:\n", "Erreur d’exportation PDF :\n") + ex.Message,
                    "Export PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── Shared drawing helpers (shared between the 3 templates) ──────────────
        private static List<string> WrapText(XGraphics g, string text, XFont font, double maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return new List<string> { "" };
            if (g.MeasureString(text, font).Width <= maxWidth) return new List<string> { text };
            var words = text.Split(' ');
            var lines = new List<string>();
            var cur = "";
            foreach (var w in words)
            {
                var trial = cur.Length == 0 ? w : cur + " " + w;
                if (g.MeasureString(trial, font).Width <= maxWidth) cur = trial;
                else { if (cur.Length > 0) lines.Add(cur); cur = w; }
            }
            if (cur.Length > 0) lines.Add(cur);
            return lines.Count > 0 ? lines : new List<string> { text };
        }

        // ═══════════════════════════════════════════════════════════════════════
        // TEMPLATE 1 — "Technical": a coloured bar per category, a single row of cards (not 2 columns — with real pagination
        // there is no need to cram), multi-page, a formal style, suitable for handing to a client.
        // ═══════════════════════════════════════════════════════════════════════
        private void BuildPdfTechnical(PdfDocument doc, List<(string Title, string Subtitle, List<(string,string)> Rows)> cards, string identity)
        {
            var fTitle  = new XFont("Arial", 17, XFontStyleEx.Bold);
            var fSub    = new XFont("Arial", 9,  XFontStyleEx.Regular);
            var fCard   = new XFont("Arial", 11, XFontStyleEx.Bold);
            var fCardSub= new XFont("Arial", 8.5, XFontStyleEx.Regular);
            var fLabel  = new XFont("Arial", 9,  XFontStyleEx.Regular);
            var fValue  = new XFont("Arial", 9,  XFontStyleEx.Bold);
            var clrText   = XColor.FromArgb(0xFF, 0x0C, 0x1A, 0x2E);
            var clrGray   = XColor.FromArgb(0xFF, 0x5A, 0x6E, 0x84);
            var clrLine   = XColor.FromArgb(0xFF, 0xE0, 0xE4, 0xEA);
            var clrRowAlt = XColor.FromArgb(0xFF, 0xF6, 0xF8, 0xFB);

            void DrawHeader(PdfPager p)
            {
                double margin = p.Margin;
                if (p.PageNumber == 1)
                {
                    p.G.DrawString("SMD Win — Hardware Report", fTitle, new XSolidBrush(clrText), margin, margin + 20);
                    string subLine = ReportBranding.Prefix(string.IsNullOrWhiteSpace(identity)
                        ? $"Generated {DateTime.Now:yyyy-MM-dd HH:mm}"
                        : $"{identity}  ·  Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
                    p.G.DrawString(subLine, fSub, new XSolidBrush(clrGray), margin, margin + 35);
                    p.G.DrawLine(new XPen(clrLine, 1), margin, margin + 44, p.PageW - margin, margin + 44);
                    p.Y = margin + 56;
                }
                else
                {
                    p.G.DrawString("SMD Win — Hardware Report", fSub, new XSolidBrush(clrGray), margin, margin + 12);
                    p.G.DrawString($"{p.PageNumber}", fSub, new XSolidBrush(clrGray), p.PageW - margin - 14, margin + 12);
                    p.G.DrawLine(new XPen(clrLine, 0.75), margin, margin + 18, p.PageW - margin, margin + 18);
                    p.Y = margin + 30;
                }
            }
            var pager = new PdfPager(doc, DrawHeader);

            double contentW = pager.PageW - pager.Margin * 2;
            double barW = 4, padL = barW + 10, padR = 8;

            foreach (var (title, subtitle, rows) in cards)
            {
                var (r, gC, b) = CategoryColor(title);
                var accent = XColor.FromArgb(0xFF, r, gC, b);

                // measure the card height BEFORE knowing whether it fits (without drawing)
                double rowH = 15;
                var wrapped = rows.Select(rw =>
                {
                    var lines = WrapText(pager.G, rw.Item2, fValue, contentW - padL - padR - 140);
                    double h = Math.Max(rowH, lines.Count * 11 + 4);
                    return (Label: rw.Item1, Lines: lines, H: h);
                }).ToList();
                double titleH = 22 + (string.IsNullOrWhiteSpace(subtitle) ? 0 : 12);
                double cardH = titleH + wrapped.Sum(w => w.H) + 10;

                pager.EnsureSpace(cardH + 14);
                double x = pager.Margin, y = pager.Y;

                pager.G.DrawRectangle(new XSolidBrush(accent), x, y, barW, cardH);
                pager.G.DrawString(title, fCard, new XSolidBrush(XColor.FromArgb(0xFF, r, gC, b)), x + padL, y + 13);
                double rowsY = y + 20;
                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    pager.G.DrawString(subtitle, fCardSub, new XSolidBrush(clrGray), x + padL, y + 24);
                    rowsY = y + 34;
                }

                double runningY = rowsY;
                for (int i = 0; i < wrapped.Count; i++)
                {
                    var (label, lines, h) = wrapped[i];
                    if (i % 2 == 0) pager.G.DrawRectangle(new XSolidBrush(clrRowAlt), x + padL - 4, runningY - 9, contentW - padL + 4 - padR, h);
                    pager.G.DrawString(label, fLabel, new XSolidBrush(clrGray), x + padL, runningY);
                    for (int li = 0; li < lines.Count; li++)
                    {
                        var sz = pager.G.MeasureString(lines[li], fValue);
                        pager.G.DrawString(lines[li], fValue, new XSolidBrush(clrText),
                            x + contentW - padR - sz.Width, runningY + li * 11);
                    }
                    runningY += h;
                }
                pager.Y = runningY + 14;
            }
        }


        // ═══════════════════════════════════════════════════════════════════════
        // TEMPLATE 0 — "OnePage" / "AppStyle" (default): the same cards as in System Info, with the component icon, on two
        // balanced columns, on A SINGLE page. The text grows or shrinks (0.58–1.25) until everything fits exactly; only below the
        // minimum readable size does it move to a second page.
        // ═══════════════════════════════════════════════════════════════════════
        private sealed class OnePlaced
        {
            public int Card, Col, Page;
            public double Y, H;
            public List<PdfFactRow> Rows = new();
            public List<string> NameLines = new();
            public double Head;
        }

        private sealed class PdfFact { public string Label = ""; public List<string> Lines = new(); public bool Full, List; }
        private sealed class PdfFactRow { public List<PdfFact> Cells = new(); public double H; }

        /// <summary>
        /// Draws a card icon as a filled Fluent icon (the same as in the app): the 24×24 geometry is flattened into polygons
        /// and filled with the nonzero rule, so the holes of the shape stay open.
        /// </summary>
        private static void DrawPdfIcon(XGraphics g, string iconKey, double x, double y, double size, XColor color)
        {
            try
            {
                string name = FluentIcons.ForInfoKey.TryGetValue(iconKey, out var n) ? n : "desktop";
                var flat = FluentIcons.Get(name).GetFlattenedPathGeometry(0.02, ToleranceType.Absolute);
                double k = size / 24.0;
                var path = new XGraphicsPath { FillMode = XFillMode.Winding };
                foreach (var fig in flat.Figures)
                {
                    var pts = new List<XPoint> { new XPoint(x + fig.StartPoint.X * k, y + fig.StartPoint.Y * k) };
                    foreach (var seg in fig.Segments)
                    {
                        if (seg is PolyLineSegment pl) foreach (var q in pl.Points) pts.Add(new XPoint(x + q.X * k, y + q.Y * k));
                        else if (seg is LineSegment ls) pts.Add(new XPoint(x + ls.Point.X * k, y + ls.Point.Y * k));
                    }
                    if (pts.Count > 2) { path.StartFigure(); path.AddPolygon(pts.ToArray()); path.CloseFigure(); }
                }
                g.DrawPath(new XSolidBrush(color), path);
            }
            catch (Exception ex) { AppLogger.Debug("DrawPdfIcon: " + ex.Message); }
        }

        private void BuildPdfOnePage(PdfDocument doc, List<(string Title, string Subtitle, List<(string,string)> Rows)> cardsIn, string identity)
        {
            // A light header (no dark band — it gets printed), a summary row with the main components, then the cards on two
            // columns: a grey icon, the small category, the name on up to two lines (no longer cut), the facts two per row.
            // Everything on one page when possible.
            const double M = 30, Gap = 12, MinScale = 0.62, MaxScale = 1.15, Bottom = 40;

            // rows without a value (e.g. empty "Serial", "BIOS Date") add nothing to the report
            static bool Empty(string v) => string.IsNullOrWhiteSpace(v) || v.Trim() is "—" or "-" or "–";
            var cards = cardsIn.Select(c => (c.Title, Subtitle: PdfKit.CleanName(c.Subtitle), Rows: c.Rows.Where(r => !Empty(r.Item2)).ToList())).ToList();
            string IconOf(int i) => i < _cardIconKeys.Count ? _cardIconKeys[i] : "System";
            static bool IsListLabel(string l) => System.Text.RegularExpressions.Regex.IsMatch(l ?? "", @"^\s*\d+\.\s*$");

            var idParts = (identity ?? "").Split(new[] { "·" }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            string title = idParts.Count > 0 ? PdfKit.CleanName(idParts[0]) : Environment.MachineName;
            string subtitle = string.Join(" · ", idParts.Skip(1));

            // the summary at the top: processor, memory, graphics card, storage, monitors (those that exist)
            var strip = new List<(string Label, string Value)>();
            foreach (var key in new[] { "Cpu", "Ram", "Gpu", "Disk", "Monitor" })
            {
                int i = Enumerable.Range(0, cards.Count).FirstOrDefault(k => IconOf(k) == key, -1);
                if (i >= 0 && !string.IsNullOrWhiteSpace(cards[i].Subtitle))
                    strip.Add((cards[i].Title.Replace(" (RAM)", ""), cards[i].Subtitle));
            }

            double stripTop = 0, stripH = strip.Count > 0 ? 40 : 0;
            void DrawHeader(PdfPager p)
            {
                if (p.PageNumber == 1)
                {
                    var right = new List<string> { $"{DateTime.Now:d MMM yyyy · HH:mm}" };
                    if (ReportBranding.NameAndPhone.Length > 0) right.Add(ReportBranding.NameAndPhone);
                    if (ReportBranding.Contact.Length > 0) right.Add(ReportBranding.Contact);
                    p.Y = PdfKit.Header(p.G, p.PageW, M, M - 6, "Hardware report", title, subtitle, right) - 4;
                    if (strip.Count > 0)
                    {
                        stripTop = p.Y;
                        double sw = (p.PageW - 2 * M) / strip.Count;
                        var fL = PdfKit.F(7.5); var fV = PdfKit.F(8.5, true);
                        for (int i = 0; i < strip.Count; i++)
                        {
                            double sx = M + i * sw + (i == 0 ? 0 : 10);
                            if (i > 0) p.G.DrawLine(new XPen(PdfKit.Line, 0.6), M + i * sw, p.Y + 2, M + i * sw, p.Y + stripH - 8);
                            p.G.DrawString(strip[i].Label, fL, PdfKit.B(PdfKit.Ink3), sx, p.Y + 9);
                            var lines = PdfKit.Wrap(p.G, strip[i].Value, fV, sw - 14);
                            for (int li = 0; li < Math.Min(2, lines.Count); li++)
                            {
                                string t = li == 1 && lines.Count > 2 ? PdfKit.Fit(p.G, lines[1] + " " + lines[2], fV, sw - 14) : lines[li];
                                p.G.DrawString(t, fV, PdfKit.B(PdfKit.Ink), sx, p.Y + 21 + li * 10.5);
                            }
                        }
                        p.Y += stripH;
                        p.G.DrawLine(new XPen(PdfKit.Line, 0.8), M, p.Y, p.PageW - M, p.Y);
                        p.Y += 12;
                    }
                }
                else
                {
                    var f = PdfKit.F(8);
                    PdfKit.Logo(p.G, M, M - 2, 9);
                    p.G.DrawString($"Hardware report · {title}", f, PdfKit.B(PdfKit.Ink3), M + 16, M + 6);
                    p.G.DrawLine(new XPen(PdfKit.Line, 0.75), M, M + 13, p.PageW - M, M + 13);
                    p.Y = M + 24;
                }
                string footL = ReportBranding.Workshop.Length > 0 ? $"{ReportBranding.Workshop} · made with SMD Win" : "Generated by SMD Win";
                PdfKit.Footer(p.G, p.PageW, p.PageH, M, footL, $"Page {p.PageNumber}");
            }
            var pager = new PdfPager(doc, DrawHeader);
            double colW = (pager.PageW - 2 * M - Gap) / 2;
            double firstTop = pager.Y;
            double laterTop = M + 24;
            double bottom = pager.PageH - Bottom;

            // measures a card at a given scale: the small label above, the bold value below, two per row; a long value takes the
            // whole row; lists ("1.", "2.") have the number on the same line as the text
            OnePlaced Measure(int i, double sc)
            {
                var fValue = PdfKit.F(8.5 * sc, true);
                var fName  = PdfKit.F(10.5 * sc, true);
                var (ttl, sub, rows) = cards[i];
                var it = new OnePlaced { Card = i };
                double inner = colW - 24, half = (inner - 10) / 2;
                var facts = new List<PdfFact>();
                foreach (var rw in rows)
                {
                    if (IsListLabel(rw.Item1))
                    {
                        facts.Add(new PdfFact { Label = rw.Item1.Trim().TrimEnd('.'), Full = true, List = true, Lines = PdfKit.Wrap(pager.G, rw.Item2, fValue, inner - 16) });
                        continue;
                    }
                    bool full = rw.Item2.Contains('\n') || pager.G.MeasureString(rw.Item2, fValue).Width > half - 2;
                    facts.Add(new PdfFact { Label = rw.Item1.Trim(), Full = full, Lines = PdfKit.Wrap(pager.G, rw.Item2, fValue, (full ? inner : half) - 2) });
                }
                void AddRow(List<PdfFact> cells)
                {
                    double h = cells[0].List
                        ? cells[0].Lines.Count * 10 * sc + 4 * sc
                        : cells.Max(c => 8 * sc + c.Lines.Count * 10 * sc) + 6 * sc;
                    it.Rows.Add(new PdfFactRow { Cells = cells, H = h });
                }
                List<PdfFact>? pending = null;
                foreach (var f in facts)
                {
                    if (f.Full) { if (pending != null) { AddRow(pending); pending = null; } AddRow(new List<PdfFact> { f }); }
                    else if (pending == null) pending = new List<PdfFact> { f };
                    else { pending.Add(f); AddRow(pending); pending = null; }
                }
                if (pending != null) AddRow(pending);
                double nameW = colW - 24;
                it.NameLines = string.IsNullOrWhiteSpace(sub) ? new List<string>() : PdfKit.Wrap(pager.G, sub, fName, nameW).Take(2).ToList();
                it.Head = (18 + Math.Max(1, it.NameLines.Count) * 12.5 + 7) * sc;
                it.H = it.Head + it.Rows.Sum(r => r.H) + 7 * sc;
                return it;
            }

            // a single page: the cards in order, cut into two columns at the point that balances the heights
            List<OnePlaced>? Balanced(double sc)
            {
                var items = Enumerable.Range(0, cards.Count).Select(i => Measure(i, sc)).ToList();
                double gap = 9 * sc, avail = bottom - firstTop;
                int best = -1; double bestMax = double.MaxValue;
                for (int k = 0; k <= items.Count; k++)
                {
                    double h0 = items.Take(k).Sum(x => x.H + gap), h1 = items.Skip(k).Sum(x => x.H + gap);
                    double mx = Math.Max(h0, h1);
                    if (mx < bestMax) { bestMax = mx; best = k; }
                }
                if (bestMax - gap > avail) return null;
                double y0 = firstTop, y1 = firstTop;
                for (int i = 0; i < items.Count; i++)
                {
                    items[i].Page = 1;
                    if (i < best) { items[i].Col = 0; items[i].Y = y0; y0 += items[i].H + gap; }
                    else          { items[i].Col = 1; items[i].Y = y1; y1 += items[i].H + gap; }
                }
                return items;
            }

            // fallback (when it does not fit even at the minimum size): masonry over several pages
            List<OnePlaced> Multi(double sc)
            {
                var items = new List<OnePlaced>();
                int page = 1; double top = firstTop;
                var colY = new[] { top, top };
                double gap = 9 * sc;
                for (int i = 0; i < cards.Count; i++)
                {
                    var it = Measure(i, sc);
                    int col = colY[0] <= colY[1] ? 0 : 1;
                    if (colY[col] + it.H > bottom)
                    {
                        int other = 1 - col;
                        if (colY[other] + it.H <= bottom) col = other;
                        else { page++; top = laterTop; colY[0] = colY[1] = top; col = 0; }
                    }
                    it.Col = col; it.Page = page; it.Y = colY[col];
                    colY[col] += it.H + gap;
                    items.Add(it);
                }
                return items;
            }

            double scale = MaxScale;
            List<OnePlaced>? layout = null;
            while (scale >= MinScale - 0.001)
            {
                layout = Balanced(scale);
                if (layout != null) break;
                scale -= 0.03;
            }
            if (layout == null) { scale = MinScale; layout = Multi(scale); }

            var fCap  = PdfKit.F(7.5 * scale);
            var fName2 = PdfKit.F(10.5 * scale, true);
            var fLab  = PdfKit.F(7 * scale);
            var fVal  = PdfKit.F(8.5 * scale, true);
            var fValR = PdfKit.F(8.5 * scale);
            int curPage = 1;
            foreach (var it in layout)
            {
                while (curPage < it.Page) { pager.NewPage(); curPage++; }
                var (ttl, sub, _) = cards[it.Card];
                double x = M + it.Col * (colW + Gap), y = it.Y;
                PdfKit.Card(pager.G, x, y, colW, it.H);

                // header: the grey icon, the small category, then the component name (up to two lines)
                double ix = x + 12, iy = y + 8 * scale, isz = 11 * scale;
                DrawPdfIcon(pager.G, IconOf(it.Card), ix, iy, isz, PdfKit.Ink2);
                pager.G.DrawString(ttl, fCap, PdfKit.B(PdfKit.Ink3), ix + isz + 6, iy + 8.5 * scale);
                double ny = y + 18 * scale + 10 * scale;
                var nameLines = it.NameLines.Count > 0 ? it.NameLines : new List<string> { ttl };
                foreach (var nl in nameLines) { pager.G.DrawString(nl, fName2, PdfKit.B(PdfKit.Ink), x + 12, ny); ny += 12.5 * scale; }
                pager.G.DrawLine(new XPen(PdfKit.Grid, 0.75), x + 12, y + it.Head - 3 * scale, x + colW - 12, y + it.Head - 3 * scale);

                double inner = colW - 24, half = (inner - 10) / 2;
                double ry = y + it.Head;
                foreach (var row in it.Rows)
                {
                    if (row.Cells[0].List)
                    {
                        var f = row.Cells[0];
                        double by = ry + 9 * scale;
                        pager.G.DrawString(f.Label, fLab, PdfKit.B(PdfKit.Ink3), x + 12, by);
                        for (int li = 0; li < f.Lines.Count; li++)
                            pager.G.DrawString(f.Lines[li], li == 0 ? fVal : fValR, PdfKit.B(li == 0 ? PdfKit.Ink : PdfKit.Ink3), x + 28, by + li * 10 * scale);
                    }
                    else
                    {
                        for (int c = 0; c < row.Cells.Count; c++)
                        {
                            var f = row.Cells[c];
                            double cx = x + 12 + (c == 0 ? 0 : half + 10);
                            pager.G.DrawString(f.Label, fLab, PdfKit.B(PdfKit.Ink3), cx, ry + 6 * scale);
                            for (int li = 0; li < f.Lines.Count; li++)
                                pager.G.DrawString(f.Lines[li], fVal, PdfKit.B(PdfKit.Ink), cx, ry + 6 * scale + 9.5 * scale + li * 10 * scale);
                        }
                    }
                    ry += row.H;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // TEMPLATE 2 — "Summary": a cover with identity + a few "at a glance" cards (the title+subtitle of the first cards, the
        // same data already shown on screen — no new metrics are invented), then the rest in detail on the following pages, in
        // the same style as Technical (reused, not duplicated).
        // ═══════════════════════════════════════════════════════════════════════
        private void BuildPdfSummary(PdfDocument doc, List<(string Title, string Subtitle, List<(string,string)> Rows)> cards, string identity)
        {
            var fCover    = new XFont("Arial", 22, XFontStyleEx.Bold);
            var fCoverSub = new XFont("Arial", 11, XFontStyleEx.Regular);
            var fChipTtl  = new XFont("Arial", 9,  XFontStyleEx.Bold);
            var fChipSub  = new XFont("Arial", 8.5, XFontStyleEx.Regular);
            var clrText = XColor.FromArgb(0xFF, 0x0C, 0x1A, 0x2E);
            var clrGray = XColor.FromArgb(0xFF, 0x5A, 0x6E, 0x84);
            var clrLine = XColor.FromArgb(0xFF, 0xE0, 0xE4, 0xEA);

            // Page 1 — the cover, drawn manually ONCE (not through the generic DrawHeader, it looks different from the detail pages).
            var doc0 = doc.AddPage();
            doc0.Width  = XUnitPt.FromPoint(595); doc0.Height = XUnitPt.FromPoint(842);
            var g0 = XGraphics.FromPdfPage(doc0);
            g0.DrawRectangle(XBrushes.White, 0, 0, 595, 842);
            const double margin = 40;
            g0.DrawString(string.IsNullOrWhiteSpace(identity) ? "SMD Win — Hardware Report" : identity,
                fCover, new XSolidBrush(clrText), margin, margin + 30);
            g0.DrawString(ReportBranding.Prefix($"Hardware report · Generated {DateTime.Now:yyyy-MM-dd HH:mm}"), fCoverSub, new XSolidBrush(clrGray), margin, margin + 52);
            g0.DrawLine(new XPen(clrLine, 1), margin, margin + 64, 595 - margin, margin + 64);

            // "At a glance" — up to 6 chips, 2 per row, with the title + subtitle EXACTLY as on screen (the same _cardData) — no new
            // metrics are extracted/guessed from the rows, only what already exists is shown again.
            double chipY = margin + 84, chipW = (595 - margin * 2 - 12) / 2, chipH = 46;
            int chipCount = 0;
            foreach (var (title, subtitle, _) in cards)
            {
                if (chipCount >= 6 || string.IsNullOrWhiteSpace(subtitle)) { if (chipCount >= 6) break; continue; }
                var (r, gC, b) = CategoryColor(title);
                double cx = margin + (chipCount % 2) * (chipW + 12);
                double cy = chipY + (chipCount / 2) * (chipH + 10);
                var bgTint = XColor.FromArgb(0xFF, (byte)Math.Min(255, r + (255 - r) * 9 / 10), (byte)Math.Min(255, gC + (255 - gC) * 9 / 10), (byte)Math.Min(255, b + (255 - b) * 9 / 10));
                g0.DrawRoundedRectangle(new XSolidBrush(bgTint), cx, cy, chipW, chipH, 6, 6);
                g0.DrawString(title.ToUpperInvariant(), fChipTtl, new XSolidBrush(XColor.FromArgb(0xFF, r, gC, b)), cx + 10, cy + 16);
                var subLines = WrapText(g0, subtitle, fChipSub, chipW - 20);
                g0.DrawString(subLines.FirstOrDefault() ?? "", fChipSub, new XSolidBrush(clrText), cx + 10, cy + 32);
                chipCount++;
            }
            g0.DrawString(L("Full details on the following pages →", "Detalii complete pe paginile următoare →", "Vollständige Details auf den folgenden Seiten →", "Détails complets sur les pages suivantes →"),
                fChipSub, new XSolidBrush(clrGray), margin, chipY + ((chipCount + 1) / 2) * (chipH + 10) + 14);

            // Pages 2+ — exactly the engine + drawing of Technical, reused through the same PdfPager, only starting from a NEW page
            // (the cover is not overwritten).
            BuildPdfTechnical(doc, cards, identity);
        }

    }
}
