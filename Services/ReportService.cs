using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class ReportService
    {
        private static string AppVersionString
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return AppVersionInfo.Short;
            }
        }


        public async Task<string> GenerateDiagnosticReportAsync(
            List<SMDWin.Services.DiagnosticPhaseResult> results, string computerName, string language = "en")
        {
            return await Task.Run(() =>
            {
                string L(string en, string ro, string de, string fr) => language switch
                    { "ro" => ro, "de" => de, "fr" => fr, _ => en };

                var sb = new StringBuilder();
                var now = DateTime.Now;
                bool allOk = results.TrueForAll(r => r.Success);
                string health = allOk
                    ? L("✔ All tests passed", "✔ Toate testele au trecut", "✔ Alle Tests bestanden", "✔ Tous les tests réussis")
                    : L("⚠ Issues found", "⚠ Probleme găsite", "⚠ Probleme gefunden", "⚠ Problèmes détectés");
                string healthCls = allOk ? "health-good" : "health-warn";

                string PhaseIcon(SMDWin.Services.DiagnosticPhase p) => p switch
                {
                    SMDWin.Services.DiagnosticPhase.Cpu => "🔥",
                    SMDWin.Services.DiagnosticPhase.Gpu => "🎮",
                    SMDWin.Services.DiagnosticPhase.Ram => "🧠",
                    SMDWin.Services.DiagnosticPhase.Storage => "💾",
                    SMDWin.Services.DiagnosticPhase.Network => "🌐",
                    _ => "•",
                };

                sb.Append($@"<!DOCTYPE html>
<html lang='{language}'>
<head>
<meta charset='UTF-8'>
<title>SMDWin — {L("System Diagnostic", "Diagnostic Sistem", "Systemdiagnose", "Diagnostic système")} — {computerName} — {now:dd-MM-yyyy}</title>
<style>
  *{{box-sizing:border-box;margin:0;padding:0}}
  body{{font-family:'Segoe UI',Arial,sans-serif;background:#f0f4f8;color:#1a202c;padding:14px}}
  .container{{max-width:960px;margin:0 auto}}
  .header{{background:linear-gradient(135deg,#1e3a8a,#3b82f6);color:white;padding:14px 18px;border-radius:10px;margin-bottom:12px}}
  .header h1{{font-size:18px;font-weight:700;margin-bottom:3px}}
  .header p{{font-size:11px;opacity:.85;margin-top:2px}}
  .health-badge{{display:inline-block;padding:3px 12px;border-radius:16px;font-weight:700;font-size:11px;margin-top:6px}}
  .health-good{{background:#dcfce7;color:#166534}}.health-warn{{background:#fef9c3;color:#854d0e}}
  .card{{background:white;border-radius:10px;padding:12px 15px;box-shadow:0 1px 3px rgba(0,0,0,.06);margin-bottom:10px}}
  .card h2{{font-size:13px;font-weight:700;color:#1e3a8a;border-bottom:1px solid #e5edff;padding-bottom:6px;margin-bottom:8px;display:flex;align-items:center;gap:6px}}
  .row{{display:flex;justify-content:space-between;padding:3px 0;border-bottom:1px solid #f1f5f9;font-size:11px}}
  .row:last-child{{border-bottom:none}}
  .lbl{{color:#64748b;font-weight:500}}.val{{color:#0f172a;font-weight:600;text-align:right;max-width:65%}}
  .tag{{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600}}
  .tag-ok{{background:#dcfce7;color:#166534}}.tag-warn{{background:#fef9c3;color:#854d0e}}
  .summary{{font-size:12px;color:#334155;margin-top:6px;padding-top:6px;border-top:1px solid #f1f5f9}}
  .footer{{text-align:center;color:#94a3b8;font-size:11px;margin-top:28px;padding-top:14px;border-top:1px solid #e2e8f0}}
  @media print{{body{{background:white;padding:0}}.card{{box-shadow:none;border:1px solid #e2e8f0}}}}
</style>
</head>
<body>
<div class='container'>

<div class='header'>
  <h1>🖥 {L("System Diagnostic Report", "Raport Diagnostic Sistem", "Systemdiagnosebericht", "Rapport de diagnostic système")}</h1>
  {ReportBranding.HtmlLine}
  <p>{L("Computer", "Calculator", "Computer", "Ordinateur")}: <strong>{computerName}</strong></p>
  <p>{L("Generated", "Generat", "Erstellt", "Généré")}: {now:dd MMMM yyyy, HH:mm:ss} &nbsp;·&nbsp; SMDWin {AppVersionString}</p>
  <span class='health-badge {healthCls}'>{health}</span>
</div>
");

                foreach (var r in results)
                {
                    sb.Append($@"<div class='card'>
  <h2>{PhaseIcon(r.Phase)} {r.Phase}
    <span class='tag {(r.Success ? "tag-ok" : "tag-warn")}' style='margin-left:auto'>{(r.Success ? L("PASS", "OK", "OK", "OK") : L("WARNING", "AVERTISMENT", "WARNUNG", "AVERTISSEMENT"))}</span>
  </h2>
  <div class='summary'>{System.Web.HttpUtility.HtmlEncode(r.Summary)}</div>");
                    foreach (var kv in r.Details)
                        sb.Append($"  <div class='row'><span class='lbl'>{System.Web.HttpUtility.HtmlEncode(kv.Key)}</span><span class='val'>{System.Web.HttpUtility.HtmlEncode(kv.Value)}</span></div>\n");
                    sb.Append($"  <div class='row'><span class='lbl'>{L("Duration", "Durată", "Dauer", "Durée")}</span><span class='val'>{r.Elapsed.TotalSeconds:F0}s</span></div>\n");
                    sb.Append("</div>\n");
                }

                sb.Append($@"<div class='footer'>
  SMDWin {AppVersionString} &nbsp;·&nbsp; {now:dd-MM-yyyy HH:mm:ss} &nbsp;·&nbsp; {computerName}
  <br>{L("Auto-generated report.", "Raport generat automat.", "Automatisch generierter Bericht.", "Rapport généré automatiquement.")}
</div>
</div></body></html>");

                return sb.ToString();
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // PERFORMANCE REFERENCE SCALES  (based on real-world benchmarks)
        // Left = weakest real system, Right = top-tier current system
        // Scale chosen so mid-range hardware lands in 40-65% zone
        // ════════════════════════════════════════════════════════════════════

        // CPU: SHA-256 hash/s (SMD Win single-threaded benchmark)
        // Celeron N4020 ~12K | Pentium ~30K | i5-8th ~120K | i7-8th ~160K
        // i5-12th ~220K | i9-13th ~350K | i9-14th ~420K
        private const double CpuScaleMin = 10_000;
        private const double CpuScaleMax = 420_000;

        // RAM Read bandwidth (GB/s, managed Buffer.BlockCopy)
        // DDR3-1333 single ~6 | DDR3-1600 ~9 | DDR4-2666 ~18 | DDR4-3200 ~25
        // DDR5-4800 ~38 | DDR5-6000 ~50
        private const double RamReadScaleMin = 5.0;
        private const double RamReadScaleMax = 52.0;

        // RAM Write bandwidth
        private const double RamWriteScaleMin = 4.0;
        private const double RamWriteScaleMax = 48.0;

        // Disk Sequential Read (MB/s) — realistic consumer range
        // HDD 5400rpm ~80 | HDD 7200rpm ~150 | SATA SSD ~550
        // NVMe Gen3 ~3500 | NVMe Gen4 ~7000 | NVMe Gen5 ~12000
        // Scale: left=HDD, right=NVMe Gen4 (Gen5 is niche, would push SATA too far left)
        private const double DiskReadScaleMin = 70.0;
        private const double DiskReadScaleMax = 7_000.0;

        // Disk Sequential Write (MB/s)
        private const double DiskWriteScaleMin = 60.0;
        private const double DiskWriteScaleMax = 6_500.0;


        // ── Helpers ──────────────────────────────────────────────────────────

        private static string RowH(string label, string value) =>
            $"<div class='row'><span class='lbl'>{label}</span><span class='val'>{System.Web.HttpUtility.HtmlEncode(value)}</span></div>";

        public async Task SaveReportAsync(string htmlContent, string path) =>
            await Task.Run(() => File.WriteAllText(path, htmlContent,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)));
    }
}
