using System;
using System.Collections.Generic;
using System.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The stress test PDF report: a light header, the verdict at the top, four figures, processor / graphics card, then the
    /// temperature and load charts over the whole session, with the test period coloured. No dark backgrounds and no program
    /// version.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Called on every read: opens / closes the period in which a stress test runs.</summary>
        private void TrackStressSpan(DateTime now)
        {
            bool cpu = _cpuStress.Running, gpu = _gpuStress.IsRunning, rd = _psuStress.IsRunning;
            lock (_stressSpans)
            {
                var open = _stressSpans.Count > 0 && _stressSpans[^1].End == null ? _stressSpans[^1] : null;
                if (cpu || gpu || rd)
                {
                    if (open == null) { open = new StressSpan { Start = now }; _stressSpans.Add(open); }
                    open.Cpu |= cpu; open.Gpu |= gpu; open.RamDisk |= rd;
                    if (_stressSpans.Count > 200) _stressSpans.RemoveAt(0);
                }
                else if (open != null) open.End = now;
            }
        }

        private void GenerateStressPdf(string path)
        {
            PdfKit.EnsureFontResolver();
            var doc = new PdfDocument();
            doc.Info.Title = "Stress Test Report";
            doc.Info.Author = string.IsNullOrEmpty(ReportBranding.Workshop) ? "SMD Win" : ReportBranding.Workshop;
            doc.Info.Creator = "SMD Win";

            const double W = 595, H = 842, M = 36;
            var page = doc.AddPage();
            page.Width = XUnitPt.FromPoint(W);
            page.Height = XUnitPt.FromPoint(H);
            var g = XGraphics.FromPdfPage(page);
            g.DrawRectangle(XBrushes.White, 0, 0, W, H);

            // ── the data ──
            List<(DateTime t, float cpuT, float gpuT, float cpuL, float gpuL)> hist;
            lock (_sessHist) hist = new(_sessHist);
            List<StressSpan> spans;
            lock (_stressSpans) spans = _stressSpans.Select(s => new StressSpan { Start = s.Start, End = s.End ?? DateTime.Now, Cpu = s.Cpu, Gpu = s.Gpu, RamDisk = s.RamDisk }).ToList();

            double stressSec = spans.Sum(s => ((s.End ?? DateTime.Now) - s.Start).TotalSeconds);
            if (stressSec < 1)
            {
                var d = (_stressSessionLastSeen > _stressSessionStart ? _stressSessionLastSeen : DateTime.Now) - _stressSessionStart;
                if (_stressSessionLastSeen > DateTime.MinValue) stressSec = Math.Max(0, d.TotalSeconds);
            }
            bool anyStress = stressSec >= 1;
            string Dur(double sec) => sec < 60 ? $"{sec:F0} s" : sec < 3600 ? $"{(int)(sec / 60)} min {(int)sec % 60:D2} s" : $"{(int)(sec / 3600)} h {(int)(sec / 60) % 60:D2} min";
            var parts = new List<string>();
            if (spans.Any(s => s.Cpu)) parts.Add("CPU");
            if (spans.Any(s => s.Gpu)) parts.Add("GPU");
            if (spans.Any(s => s.RamDisk)) parts.Add("RAM + disk");
            string testKind = parts.Count > 0 ? string.Join(" + ", parts) : "—";

            float cpuWarn = SettingsService.Current.CpuTempAlertThreshold > 0 ? SettingsService.Current.CpuTempAlertThreshold : 90f;
            float gpuWarn = SettingsService.Current.GpuTempAlertThreshold > 0 ? SettingsService.Current.GpuTempAlertThreshold : 90f;
            int errors = CpuStressor.IntegrityErrorCount + GpuStressService.IntegrityErrorCount + PsuStressService.IntegrityErrorCount;
            bool hot = (_cpuMax.HasValue && _cpuMax.Value >= cpuWarn) || (_gpuMax.HasValue && _gpuMax.Value >= gpuWarn);
            int thrSec = _stThrottledSec;

            string Temp(float? v) => v.HasValue && v.Value > 0 ? TempUnit.Fmt(v.Value, 0, true) : "—";
            string Ghz(float mhz) => mhz > 0 && mhz < float.MaxValue ? $"{mhz / 1000f:F2}" : "—";

            // ── antet ──
            string model = PdfKit.CleanName($"{_summary?.Manufacturer} {_summary?.Model}".Trim());
            if (string.IsNullOrWhiteSpace(model)) model = Environment.MachineName;
            string hw = string.Join(" · ", new[] { PdfKit.CleanName(_summary?.Cpu ?? ""), PdfKit.CleanName(_summary?.GpuName ?? "") }.Where(s => s.Length > 0));
            var right = new List<string> { $"{DateTime.Now:d MMM yyyy · HH:mm}" };
            if (anyStress) right.Add($"Test: {testKind} · {Dur(stressSec)}");
            if (ReportBranding.NameAndPhone.Length > 0) right.Add(ReportBranding.NameAndPhone);
            double y = PdfKit.Header(g, W, M, M - 4, "Stress test report", model, hw, right);

            // ── the verdict ──
            string vTitle, vText, vWord, vKind; XColor vCol, vSoft;
            string limitTxt = TempUnit.Fmt(Math.Min(cpuWarn, gpuWarn), 0, true);
            if (!anyStress)
            {
                (vTitle, vWord, vKind, vCol, vSoft) = ("No stress test in this session", "Not tested", "!", PdfKit.Unknown, PdfKit.UnknownSoft);
                vText = "The charts show the temperatures and load since SMD Win started.";
            }
            else if (errors > 0)
            {
                (vTitle, vWord, vKind, vCol, vSoft) = ("Errors during the test — the system may be unstable", "Problem", "x", PdfKit.Problem, PdfKit.ProblemSoft);
                vText = $"{errors} calculation error(s) found. Repeat the test; if they come back, check cooling, memory and overclock settings.";
            }
            else if (hot)
            {
                (vTitle, vWord, vKind, vCol, vSoft) = ("Temperatures reached the warning limit", "Check", "!", PdfKit.Check, PdfKit.CheckSoft);
                vText = $"No calculation errors, but the warning limit ({limitTxt}) was reached. Cleaning the cooler or new thermal paste can help.";
            }
            else if (thrSec > 0)
            {
                (vTitle, vWord, vKind, vCol, vSoft) = ("Stable — the processor slowed down for a while", "Check", "!", PdfKit.Check, PdfKit.CheckSoft);
                vText = $"No calculation errors. Throttling for {Dur(thrSec)} — usually the power limit of the computer.";
            }
            else
            {
                (vTitle, vWord, vKind, vCol, vSoft) = ("Stable — no errors during the test", "OK", "ok", PdfKit.Good, PdfKit.GoodSoft);
                vText = $"Temperatures stayed below the warning limit ({limitTxt}). No throttling. Calculation checks: 0 errors.";
            }
            const double vH = 48;
            PdfKit.Card(g, M, y, W - 2 * M, vH);
            PdfKit.CheckBadge(g, M + 25, y + vH / 2, 13, vCol, vSoft, vKind);
            var fVw = PdfKit.F(8.5, true);
            double vwW = g.MeasureString(vWord, fVw).Width;
            double textW = W - 2 * M - 48 - vwW - 34;
            g.DrawString(PdfKit.Fit(g, vTitle, PdfKit.F(11.5, true), textW), PdfKit.F(11.5, true), PdfKit.B(PdfKit.Ink), M + 46, y + 20);
            g.DrawString(PdfKit.Fit(g, vText, PdfKit.F(8.5), textW), PdfKit.F(8.5), PdfKit.B(PdfKit.Ink2), M + 46, y + 34);
            PdfKit.Dot(g, W - M - 14 - vwW - 8, y + vH / 2 - 0.5, 3.2, vCol);
            g.DrawString(vWord, fVw, PdfKit.B(vCol), W - M - 14 - vwW, y + vH / 2 + 3);
            y += vH + 10;

            // ── four figures ──
            double kGap = 8, kW = (W - 2 * M - 3 * kGap) / 4, kH = 58;
            void Kpi(int i, string label, string value, string unit, string? sub, double? bar, XColor barCol)
            {
                double x = M + i * (kW + kGap);
                PdfKit.Card(g, x, y, kW, kH);
                g.DrawString(label, PdfKit.F(7.5), PdfKit.B(PdfKit.Ink3), x + 11, y + 15);
                var fN = PdfKit.Num(20);
                g.DrawString(value, fN, PdfKit.B(PdfKit.Ink), x + 11, y + 38);
                if (value != "—" && unit.Length > 0)
                    g.DrawString(unit, PdfKit.F(10), PdfKit.B(PdfKit.Ink3), x + 13 + g.MeasureString(value, fN).Width, y + 38);
                if (bar.HasValue)
                {
                    g.DrawRoundedRectangle(PdfKit.B(PdfKit.Grid), x + 11, y + 46, kW - 22, 3.5, 2, 2);
                    double bw = Math.Clamp(bar.Value, 0, 1) * (kW - 22);
                    if (bw > 1) g.DrawRoundedRectangle(PdfKit.B(barCol), x + 11, y + 46, bw, 3.5, 2, 2);
                }
                else if (!string.IsNullOrEmpty(sub)) g.DrawString(sub, PdfKit.F(7.5), PdfKit.B(PdfKit.Ink3), x + 11, y + 50);
            }
            string tU = TempUnit.Symbol;
            string TNum(float? v) => v.HasValue && v.Value > 0 ? $"{TempUnit.Convert(v.Value):F0}" : "—";
            Kpi(0, "CPU max temperature", TNum(_cpuMax), tU, null, _cpuMax.HasValue ? _cpuMax.Value / 100.0 : null, _cpuMax >= cpuWarn ? PdfKit.Problem : PdfKit.Accent);
            Kpi(1, "GPU max temperature", TNum(_gpuMax), tU, null, _gpuMax.HasValue ? _gpuMax.Value / 100.0 : null, _gpuMax >= gpuWarn ? PdfKit.Problem : PdfKit.Orange);
            Kpi(2, "CPU power (peak)", _cpuPowerMax > 0 ? $"{_cpuPowerMax:F1}" : "—", "W", "package power", null, PdfKit.Accent);
            Kpi(3, "System power (est.)", _sysPowerPeak.HasValue ? $"{_sysPowerPeak.Value:F0}" : "—", "W", "CPU + GPU + ~45 W", null, PdfKit.Accent);
            y += kH + 10;

            // ── processor / graphics card ──
            double cGap = 10, cW = (W - 2 * M - cGap) / 2;
            float cpuFreqAvg = _cpuFreqCount > 0 ? _cpuFreqSum / _cpuFreqCount : 0f;
            string cpuBase = "—";
            if (!string.IsNullOrEmpty(_summary?.CpuMaxMHz) && float.TryParse(_summary!.CpuMaxMHz.Replace("MHz", "").Trim(),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float nom) && nom > 0)
                cpuBase = $"{nom / 1000f:F2} GHz";
            var cpuRows = new List<(string, string)>
            {
                ("Temperature", _cpuMin.HasValue && _cpuMax.HasValue ? $"{TNum(_cpuMin)} – {Temp(_cpuMax)}" : Temp(_cpuMax)),
                ("Load (peak)", _cpuLoadMax > 0 ? $"{_cpuLoadMax:F0} %" : "—"),
                ("Clock min · avg · max", _cpuFreqMax > 0 ? $"{Ghz(_cpuFreqMin)} · {Ghz(cpuFreqAvg)} · {Ghz(_cpuFreqMax)} GHz" : "—"),
                ("Base clock", cpuBase),
                ("Power (peak)", _cpuPowerMax > 0 ? $"{_cpuPowerMax:F1} W" : "not reported"),
            };
            var gpuRows = new List<(string, string)>
            {
                ("Temperature", _gpuMin.HasValue && _gpuMax.HasValue ? $"{TNum(_gpuMin)} – {Temp(_gpuMax)}" : Temp(_gpuMax)),
                ("Load (peak)", _gpuLoadMax > 0 ? $"{_gpuLoadMax:F0} %" : "—"),
                ("Clock (max)", _gpuFreqMax > 0 ? (_gpuFreqMax >= 1000 ? $"{_gpuFreqMax / 1000f:F2} GHz" : $"{_gpuFreqMax:F0} MHz") : "—"),
                ("Video memory (peak)", _gpuVramMax.HasValue ? $"{_gpuVramMax:F0} MB" : "—"),
                ("Power (peak)", _gpuPowerMax > 0 ? $"{_gpuPowerMax:F1} W" : "not reported"),
            };
            double tH = 28 + cpuRows.Count * 17 + 4;
            void Table(double x, string title, XColor mark, List<(string, string)> rows)
            {
                PdfKit.Card(g, x, y, cW, tH);
                g.DrawRoundedRectangle(PdfKit.B(mark), x + 12, y + 11, 7, 7, 2, 2);
                g.DrawString(title, PdfKit.F(10, true), PdfKit.B(PdfKit.Ink), x + 25, y + 19);
                double ry = y + 28;
                var fl = PdfKit.F(8.5); var fv = PdfKit.F(8.5, true);
                for (int i = 0; i < rows.Count; i++)
                {
                    var (l, v) = rows[i];
                    if (i > 0) g.DrawLine(new XPen(PdfKit.Grid, 0.6), x + 12, ry, x + cW - 12, ry);
                    g.DrawString(l, fl, PdfKit.B(PdfKit.Ink2), x + 12, ry + 11.5);
                    bool muted = v is "—" or "not reported";
                    var vf = muted ? fl : fv;
                    double vw = g.MeasureString(v, vf).Width;
                    g.DrawString(v, vf, PdfKit.B(muted ? PdfKit.Ink3 : PdfKit.Ink), x + cW - 12 - vw, ry + 11.5);
                    ry += 17;
                }
            }
            Table(M, "Processor", PdfKit.Accent, cpuRows);
            Table(M + cW + cGap, "Graphics", PdfKit.Orange, gpuRows);
            y += tH + 10;

            // ── benchmark (only if it ran) ──
            bool benchRan = TxtBenchTotalScore != null &&
                Dispatcher.Invoke(() => !string.IsNullOrEmpty(TxtBenchTotalScore?.Text) && TxtBenchTotalScore.Text != "—");
            if (benchRan)
            {
                string total = Dispatcher.Invoke(() => TxtBenchTotalScore?.Text ?? "—");
                string grade = Dispatcher.Invoke(() => TxtBenchGrade?.Text ?? "");
                string cpuS = Dispatcher.Invoke(() => TxtBenchCpuScore?.Text ?? "—");
                string ramS = Dispatcher.Invoke(() => TxtBenchRamScore?.Text ?? "—");
                string ssdS = Dispatcher.Invoke(() => TxtBenchSsdScore?.Text ?? "—");
                const double bH = 30;
                PdfKit.Card(g, M, y, W - 2 * M, bH);
                g.DrawString("Benchmark", PdfKit.F(10, true), PdfKit.B(PdfKit.Ink), M + 12, y + 19);
                string line = $"Total {total} / 80 {grade}   ·   CPU {cpuS}   ·   RAM {ramS}   ·   SSD {ssdS}";
                g.DrawString(PdfKit.Fit(g, line, PdfKit.F(8.5), W - 2 * M - 100), PdfKit.F(8.5), PdfKit.B(PdfKit.Ink2), M + 86, y + 19);
                y += bH + 10;
            }

            // ── charts ──
            double bottom = H - 40;
            if (hist.Count >= 4)
            {
                double chH = (bottom - y - 10) / 2;
                DateTime t0 = hist[0].t, t1 = hist[^1].t;
                double span = Math.Max(1, (t1 - t0).TotalSeconds);
                float tMax = Math.Max(100f, Math.Max(cpuWarn, gpuWarn) + 10f);
                StressChart(g, M, y, W - 2 * M, chH, "Temperature", hist, spans, t0, span,
                    h => h.cpuT, h => h.gpuT, 20f, tMax, Math.Min(cpuWarn, gpuWarn), TempUnit.Symbol, true);
                StressChart(g, M, y + chH + 10, W - 2 * M, chH, "Load", hist, spans, t0, span,
                    h => h.cpuL, h => h.gpuL, 0f, 100f, null, "%", false);
            }
            else
            {
                PdfKit.Card(g, M, y, W - 2 * M, 40);
                g.DrawString("Not enough readings yet for the charts (one every 5 s).", PdfKit.F(9), PdfKit.B(PdfKit.Ink3), M + 12, y + 24);
            }

            string footL = ReportBranding.Workshop.Length > 0 ? $"{ReportBranding.Workshop} · made with SMD Win" : "Generated by SMD Win";
            PdfKit.Footer(g, W, H, M, $"{footL}  ·  one reading every 5 s  ·  dashed line = warning limit", "Page 1 / 1");
            doc.Save(path);
        }

        private static void StressChart(XGraphics g, double x, double y, double w, double h, string title,
            List<(DateTime t, float cpuT, float gpuT, float cpuL, float gpuL)> hist, List<StressSpan> spans, DateTime t0, double spanSec,
            Func<(DateTime t, float cpuT, float gpuT, float cpuL, float gpuL), float> cpuSel,
            Func<(DateTime t, float cpuT, float gpuT, float cpuL, float gpuL), float> gpuSel,
            float yMin, float yMax, float? warnAt, string unit, bool isTemp)
        {
            PdfKit.Card(g, x, y, w, h);
            g.DrawString(title, PdfKit.F(10, true), PdfKit.B(PdfKit.Ink), x + 12, y + 19);

            // legenda
            var fL = PdfKit.F(7.5);
            double lx = x + w - 12;
            void Leg(string text, XColor c, bool band)
            {
                double tw = g.MeasureString(text, fL).Width;
                lx -= tw;
                g.DrawString(text, fL, PdfKit.B(PdfKit.Ink2), lx, y + 18);
                lx -= 15;
                if (band) g.DrawRectangle(PdfKit.B(PdfKit.StressBand), lx, y + 11.5, 11, 8);
                else g.DrawRoundedRectangle(PdfKit.B(c), lx, y + 14.5, 11, 2.2, 1, 1);
                lx -= 10;
            }
            if (spans.Count > 0) Leg("stress test", PdfKit.StressBand, true);
            Leg("GPU", PdfKit.Orange, false);
            Leg("CPU", PdfKit.Accent, false);

            double pL = 34, pR = 12, pT = 30, pB = 20;
            double ax = x + pL, ay = y + pT, aw = w - pL - pR, ah = h - pT - pB;
            double X(DateTime t) => ax + aw * Math.Clamp((t - t0).TotalSeconds / spanSec, 0, 1);
            double Y(float v) => ay + ah * (1 - Math.Clamp((v - yMin) / (yMax - yMin), 0, 1));

            // the test period
            foreach (var s in spans)
            {
                double sx0 = X(s.Start), sx1 = Math.Max(X(s.End ?? DateTime.Now), sx0 + 2.5);
                if (sx1 <= ax || sx0 >= ax + aw) continue;
                g.DrawRectangle(PdfKit.B(PdfKit.StressBand), sx0, ay, sx1 - sx0, ah);
            }

            var fA = PdfKit.F(7);
            float[] ticks = isTemp ? new[] { 20f, 40f, 60f, 80f, 100f } : new[] { 0f, 25f, 50f, 75f, 100f };
            foreach (float tv in ticks)
            {
                if (tv < yMin || tv > yMax) continue;
                double gy = Y(tv);
                g.DrawLine(new XPen(PdfKit.Grid, 0.6), ax, gy, ax + aw, gy);
                string lbl = isTemp ? $"{TempUnit.Convert(tv):F0}°" : $"{tv:F0}%";
                g.DrawString(lbl, fA, PdfKit.B(PdfKit.Ink3), x + 10, gy + 2.5);
            }
            if (warnAt.HasValue && warnAt > yMin && warnAt < yMax)
            {
                double wy = Y(warnAt.Value);
                g.DrawLine(new XPen(PdfKit.Problem, 0.8) { DashStyle = XDashStyle.Dash }, ax, wy, ax + aw, wy);
                string wl = $"limit {TempUnit.Fmt(warnAt.Value, 0, true)}";
                g.DrawString(wl, fA, PdfKit.B(PdfKit.Problem), ax + 4, wy - 3);
            }

            // the lines: smooth, broken where reads are missing
            void Series(Func<(DateTime t, float cpuT, float gpuT, float cpuL, float gpuL), float> sel, XColor c)
            {
                var pen = new XPen(c, 1.5) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
                var run = new List<XPoint>();
                DateTime prev = DateTime.MinValue;
                float peak = float.MinValue; XPoint peakPt = default;
                foreach (var hp in hist)
                {
                    float v = sel(hp);
                    bool gap = prev != DateTime.MinValue && (hp.t - prev).TotalSeconds > 30;
                    if (float.IsNaN(v) || (v <= 0 && isTemp) || gap) { PdfKit.SmoothLine(g, pen, run); run.Clear(); }
                    prev = hp.t;
                    if (float.IsNaN(v) || (v <= 0 && isTemp)) continue;
                    var pt = new XPoint(X(hp.t), Y(v));
                    run.Add(pt);
                    if (v > peak) { peak = v; peakPt = pt; }
                }
                PdfKit.SmoothLine(g, pen, run);
                if (peak > float.MinValue)
                {
                    g.DrawEllipse(new XPen(XColors.White, 1.2), PdfKit.B(c), peakPt.X - 2.6, peakPt.Y - 2.6, 5.2, 5.2);
                    string pl = isTemp ? $"{TempUnit.Convert(peak):F0}°" : $"{peak:F0}%";
                    var fp = PdfKit.F(7, true);
                    double pw = g.MeasureString(pl, fp).Width;
                    double px = Math.Clamp(peakPt.X - pw / 2, ax, ax + aw - pw);
                    double py = peakPt.Y - 5 < ay + 6 ? peakPt.Y + 11 : peakPt.Y - 5;
                    g.DrawString(pl, fp, PdfKit.B(PdfKit.Ink), px, py);
                }
            }
            Series(gpuSel, PdfKit.Orange);
            Series(cpuSel, PdfKit.Accent);

            // ora: inceput, mijloc, sfarsit
            var fT = PdfKit.F(7);
            string fmt = spanSec < 600 ? "HH:mm:ss" : "HH:mm";
            var times = new[] { t0, t0.AddSeconds(spanSec / 2), t0.AddSeconds(spanSec) };
            for (int i = 0; i < 3; i++)
            {
                string s = times[i].ToString(fmt);
                double sw = g.MeasureString(s, fT).Width;
                double sx = i == 0 ? ax : i == 1 ? ax + aw / 2 - sw / 2 : ax + aw - sw;
                g.DrawString(s, fT, PdfKit.B(PdfKit.Ink3), sx, ay + ah + 12);
            }
        }
    }
}
