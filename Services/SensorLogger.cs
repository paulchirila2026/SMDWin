// SMDWin — Services/SensorLogger.cs
// CSV auto-logger for all active sensors.
// File: Data\Logs\sensors-YYYY-MM-DD.csv (next to SMDWin.exe, see AppPaths)
// Columns: Timestamp, CPU_C, GPU_C, CPU_Load_%, GPU_Load_%, RAM_%,
//          Net_Down_KBs, Net_Up_KBs, Ping_ms, CPU_W, GPU_W, VCore_V, Fan_RPM

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SMDWin.Services
{
    public sealed class SensorLogger : IDisposable
    {
        private StreamWriter? _writer;
        private string        _currentFile = "";
        private DateTime      _fileDate    = DateTime.MinValue;
        private bool          _headerWritten = false;
        private bool          _enabled;
        private bool          _disposed;

        private readonly string _logDir;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!value) CloseFile();
            }
        }

        public string CurrentFile => _currentFile;

        public SensorLogger()
        {
            _logDir = AppLogger.LogDirectory;
        }

        public void Log(
            float? cpuTempC,   float? gpuTempC,
            float? cpuLoadPct, float? gpuLoadPct,
            float? ramPct,
            float  netDownKBs, float  netUpKBs,
            float  pingMs,
            float? cpuPowerW,  float? gpuPowerW,
            float? vcoreV,
            float? fanRpm1)
        {
            if (!_enabled || _disposed) return;

            try
            {
                EnsureFile();
                if (_writer == null) return;

                var sb = new StringBuilder(128);
                sb.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                sb.Append(','); AppF(sb, cpuTempC);
                sb.Append(','); AppF(sb, gpuTempC);
                sb.Append(','); AppF(sb, cpuLoadPct, 1);
                sb.Append(','); AppF(sb, gpuLoadPct, 1);
                sb.Append(','); AppF(sb, ramPct, 1);
                sb.Append(','); sb.Append(netDownKBs.ToString("F1", CultureInfo.InvariantCulture));
                sb.Append(','); sb.Append(netUpKBs.ToString("F1", CultureInfo.InvariantCulture));
                sb.Append(','); sb.Append(pingMs.ToString("F0", CultureInfo.InvariantCulture));
                sb.Append(','); AppF(sb, cpuPowerW, 1);
                sb.Append(','); AppF(sb, gpuPowerW, 1);
                sb.Append(','); AppF(sb, vcoreV, 3);
                sb.Append(','); AppF(sb, fanRpm1, 0);

                _writer.WriteLine(sb.ToString());
                _writer.Flush();
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"SensorLogger.Log: {ex.Message}");
                CloseFile();
            }
        }

        private static void AppF(StringBuilder sb, float? v, int dec = 1)
        {
            if (v.HasValue) sb.Append(v.Value.ToString($"F{dec}", CultureInfo.InvariantCulture));
        }

        private void EnsureFile()
        {
            var today = DateTime.Today;
            if (_writer != null && _fileDate == today) return;

            CloseFile();
            try
            {
                Directory.CreateDirectory(_logDir);
                _currentFile = Path.Combine(_logDir,
                    $"sensors-{today:yyyy-MM-dd}.csv");
                bool isNew = !File.Exists(_currentFile);
                _writer   = new StreamWriter(_currentFile, append: true, Encoding.UTF8);
                _fileDate = today;

                if (isNew || !_headerWritten)
                {
                    _writer.WriteLine(
                        "Timestamp,CPU_C,GPU_C,CPU_Load_%,GPU_Load_%,RAM_%," +
                        "Net_Down_KBs,Net_Up_KBs,Ping_ms," +
                        "CPU_W,GPU_W,VCore_V,Fan1_RPM");
                    _headerWritten = true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"SensorLogger.EnsureFile: {ex.Message}");
                _writer = null;
            }
        }

        private void CloseFile()
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            _writer = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseFile();
        }

        // ── Reading the warnings of a day ─────────────────────────────────────
        public record SensorWarning(DateTime Time, string Category, string Message, string Color);

        public List<SensorWarning> GetWarningsForDay(DateTime day, float cpuTempLimit = 85f, float gpuTempLimit = 85f)
        {
            var warnings = new List<SensorWarning>();
            string file = Path.Combine(_logDir, $"sensors-{day:yyyy-MM-dd}.csv");
            if (!File.Exists(file)) return warnings;

            try
            {
                bool headerSkipped = false;
                float? prevCpuTemp = null, prevGpuTemp = null;
                bool cpuThrottled = false;

                foreach (var line in File.ReadLines(file, Encoding.UTF8))
                {
                    if (!headerSkipped) { headerSkipped = true; continue; }
                    var cols = line.Split(',');
                    if (cols.Length < 4) continue;

                    if (!DateTime.TryParseExact(cols[0].Trim(), "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts)) continue;

                    float.TryParse(cols[1].Trim(), CultureInfo.InvariantCulture, out float cpuC);
                    float.TryParse(cols[2].Trim(), CultureInfo.InvariantCulture, out float gpuC);
                    float.TryParse(cols[3].Trim(), CultureInfo.InvariantCulture, out float cpuLoad);

                    // CPU temp depasire
                    if (cpuC > 0 && cpuC >= cpuTempLimit && (prevCpuTemp == null || prevCpuTemp < cpuTempLimit))
                        warnings.Add(new SensorWarning(ts, "CPU",
                            $"CPU temperature exceeded {cpuTempLimit:F0}°C  →  {cpuC:F0}°C", "#EF4444"));

                    // GPU temp depasire
                    if (gpuC > 0 && gpuC >= gpuTempLimit && (prevGpuTemp == null || prevGpuTemp < gpuTempLimit))
                        warnings.Add(new SensorWarning(ts, "GPU",
                            $"GPU temperature exceeded {gpuTempLimit:F0}°C  →  {gpuC:F0}°C", "#F97316"));

                    // CPU throttle (load < 5% but the CPU is close to the thermal limit)
                    if (cpuLoad > 0 && cpuLoad < 5f && cpuC > cpuTempLimit - 5f && !cpuThrottled)
                    {
                        cpuThrottled = true;
                        warnings.Add(new SensorWarning(ts, "Throttle",
                            $"Possible thermal throttle: CPU {cpuC:F0}°C, load {cpuLoad:F0}%", "#F59E0B"));
                    }
                    else if (cpuLoad > 20f) cpuThrottled = false;


                    prevCpuTemp = cpuC > 0 ? cpuC : prevCpuTemp;
                    prevGpuTemp = gpuC > 0 ? gpuC : prevGpuTemp;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"SensorLogger.GetWarningsForDay: {ex.Message}"); }

            return warnings;
        }

        // Log directory path (for external reading)
        public string LogDir => _logDir;
    }
}
