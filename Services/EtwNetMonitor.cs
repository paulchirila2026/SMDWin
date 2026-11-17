using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

using Timer = System.Threading.Timer;   // the project also imports System.Windows.Forms (which has its own Timer)

namespace SMDWin.Services
{
    /// <summary>One remote address an app talked to, with what went each way.</summary>
    public sealed class NetRemoteStat
    {
        public IPAddress Address = IPAddress.None;
        public ushort RemotePort, LocalPort;
        public bool Udp;
        public ulong Sent, Recv;
        public DateTime LastSeenUtc;
        public string Host = "";        // from the DNS answers seen while monitoring ("" = unknown)
        public string Service => NetPortServices.Name(RemotePort, LocalPort, Udp);
        public string Endpoint => Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{Address}]:{RemotePort}" : $"{Address}:{RemotePort}";
    }

    /// <summary>One process's traffic: totals since monitoring started, the current rate and the last 5 minutes.</summary>
    public sealed class NetAppTraffic
    {
        public int Pid;
        public ulong Sent, Recv;                 // bytes, TCP + UDP, IPv4 + IPv6
        public double SendKBs, RecvKBs;          // last second
        public double[] HistSend = Array.Empty<double>(), HistRecv = Array.Empty<double>();   // KB/s, oldest first
        public List<NetRemoteStat> Remotes = new();
        public DateTime LastSeenUtc;
        public bool HasUdp, HasTcp;
    }

    /// <summary>
    /// Network traffic per application, the way Task Manager / Resource Monitor count it: an ETW real-time session on the
    /// Windows providers Microsoft-Windows-Kernel-Network (every TCP and UDP send/receive, IPv4 and IPv6, with the process
    /// id and the byte count) and Microsoft-Windows-DNS-Client (the answers to DNS queries, so an address can be shown with the
    /// site name that was asked for). No driver, no extra package — the same mechanism Windows' own tools use.
    ///
    /// Started on first use (Network › Apps, Process Monitor/Analyzer, or the network alerts) and stopped after 3 minutes
    /// without anyone asking, unless the alerts are on. Needs administrator rights (SMD Win always has them) and a 64-bit
    /// process. If the session cannot start, <see cref="Running"/> stays false and callers keep the older per-connection TCP
    /// counters (TcpEStatsService), which miss UDP (QUIC, games, calls) and IPv6.
    /// </summary>
    public sealed unsafe class EtwNetMonitor
    {
        public static readonly EtwNetMonitor Instance = new();
        private EtwNetMonitor() { }

        private const string SessionName = "SMDWin-NetTraffic";
        private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
        private static readonly Guid DnsClient     = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
        private const int HistorySeconds = 300;
        private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(3);

        public bool Running { get; private set; }
        public string LastError { get; private set; } = "";
        public DateTime StartedUtc { get; private set; }
        /// <summary>Keeps the session alive without anyone asking (set by the network alerts).</summary>
        public bool KeepAlive { get; set; }

        /// <summary>Raised once a second on a worker thread, after the rates are computed (used by the alerts).</summary>
        public event Action? Tick;

        // ── data ─────────────────────────────────────────────────────────────
        private sealed class RemoteAcc
        {
            public ulong Hi, Lo; public bool V6; public ushort RPort, LPort; public bool Udp;
            public ulong Sent, Recv; public long LastTicks;
        }
        private sealed class AppAcc
        {
            public ulong Sent, Recv, PrevSent, PrevRecv;
            public double SendKBs, RecvKBs;
            public readonly double[] HS = new double[HistorySeconds], HR = new double[HistorySeconds];
            public int HPos, HCount;
            public long LastTicks;
            public bool Udp, Tcp;
            public readonly Dictionary<(ulong, ulong, ushort, bool), RemoteAcc> Remotes = new();
        }
        private readonly object _lock = new();
        private readonly Dictionary<int, AppAcc> _apps = new();
        private readonly Dictionary<IPAddress, string> _dns = new();
        // this computer's addresses as raw keys (no allocation per event): IPv4 = (0, address as stored in the event)
        private volatile HashSet<(ulong, ulong)> _localKeys = new();
        private ulong _totalSent, _totalRecv;
        private long _events;

        private Thread? _thread;
        private ulong _session, _trace;
        private Timer? _secondTimer;
        private DateTime _lastAskedUtc = DateTime.UtcNow;
        private EventRecordCallback? _callback;   // kept alive while the trace runs
        private readonly object _startLock = new();

        // ── public API ───────────────────────────────────────────────────────

        /// <summary>Starts the session if it is not running; cheap to call often. Returns whether it is running.</summary>
        public bool EnsureStarted()
        {
            _lastAskedUtc = DateTime.UtcNow;
            if (Running) return true;
            lock (_startLock)
            {
                if (Running) return true;
                if (_failedUtc != DateTime.MinValue && (DateTime.UtcNow - _failedUtc).TotalMinutes < 5) return false;
                try { Start(); }
                catch (Exception ex) { Fail("start: " + ex.Message); }
                return Running;
            }
        }
        private DateTime _failedUtc = DateTime.MinValue;

        public bool TryGetPid(int pid, out double sendKBs, out double recvKBs, out ulong sent, out ulong recv)
        {
            _lastAskedUtc = DateTime.UtcNow;
            lock (_lock)
            {
                if (Running && _apps.TryGetValue(pid, out var a))
                {
                    sendKBs = a.SendKBs; recvKBs = a.RecvKBs; sent = a.Sent; recv = a.Recv;
                    return true;
                }
            }
            sendKBs = recvKBs = 0; sent = recv = 0;
            return false;
        }

        /// <summary>A copy of every process seen (active in the last 10 minutes), busiest first.</summary>
        public List<NetAppTraffic> Snapshot(bool withRemotes = true, bool withHistory = false)
        {
            _lastAskedUtc = DateTime.UtcNow;
            var list = new List<NetAppTraffic>();
            long cutoff = DateTime.UtcNow.AddMinutes(-10).Ticks;
            lock (_lock)
            {
                foreach (var (pid, a) in _apps)
                {
                    if (a.LastTicks < cutoff) continue;
                    list.Add(Copy(pid, a, withRemotes, withHistory));
                }
            }
            return list.OrderByDescending(x => x.SendKBs + x.RecvKBs).ThenByDescending(x => x.Sent + x.Recv).ToList();
        }

        public NetAppTraffic? Get(int pid)
        {
            _lastAskedUtc = DateTime.UtcNow;
            lock (_lock) return _apps.TryGetValue(pid, out var a) ? Copy(pid, a, true, true) : null;
        }

        /// <summary>The name a DNS query returned this address for, while monitoring ("" if not seen).</summary>
        public string HostFor(IPAddress ip)
        {
            lock (_lock) return _dns.TryGetValue(ip, out var h) ? h : "";
        }

        public (ulong sent, ulong recv, long events) Totals { get { lock (_lock) return (_totalSent, _totalRecv, _events); } }

        public void Stop()
        {
            lock (_startLock)
            {
                if (!Running && _session == 0) return;
                Running = false;
                try { _secondTimer?.Dispose(); } catch (Exception ex) { AppLogger.Debug($"EtwNet timer: {ex.Message}"); }
                _secondTimer = null;
                StopSession(SessionName);   // ProcessTrace returns once the session is gone
                if (_trace != 0 && _trace != ulong.MaxValue) { try { CloseTrace(_trace); } catch (Exception ex) { AppLogger.Debug($"EtwNet CloseTrace: {ex.Message}"); } }
                _trace = 0; _session = 0;
                try { _thread?.Join(2000); } catch (Exception ex) { AppLogger.Debug($"EtwNet join: {ex.Message}"); }
                _thread = null;
                lock (_lock) { _apps.Clear(); _totalSent = _totalRecv = 0; _events = 0; }
                AppLogger.Info("EtwNetMonitor: stopped");
            }
        }

        // ── session ──────────────────────────────────────────────────────────

        private void Start()
        {
            if (IntPtr.Size != 8) { Fail("32-bit process — structure layout not supported"); return; }
            try { _secondTimer?.Dispose(); } catch (Exception ex) { AppLogger.Debug($"EtwNet old timer: {ex.Message}"); }
            _secondTimer = null;
            StopSession(SessionName);   // a session left over by a crashed run would make StartTrace fail

            int nameBytes = (SessionName.Length + 1) * 2;
            int size = 120 + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            try
            {
                FillProperties(props, size);
                uint rc = StartTraceW(out _session, SessionName, props);
                if (rc != 0) { Fail($"StartTrace error {rc}"); _session = 0; return; }
            }
            finally { Marshal.FreeHGlobal(props); }

            Guid kn = KernelNetwork, dns = DnsClient;
            // all keywords (the data events are under IPv4 0x10 / IPv6 0x20; the few connect/disconnect events are skipped in
            // OnEvent); level 4 = information
            uint e1 = EnableTraceEx2(_session, ref kn, 1, 4, ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (e1 != 0) { Fail($"EnableTrace Kernel-Network error {e1}"); StopSession(SessionName); _session = 0; return; }
            uint e2 = EnableTraceEx2(_session, ref dns, 1, 4, ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (e2 != 0) AppLogger.Info($"EtwNetMonitor: DNS-Client provider not enabled ({e2}) — no site names");

            _callback = OnEvent;
            IntPtr logfile = Marshal.AllocHGlobal(448);
            IntPtr namePtr = Marshal.StringToHGlobalUni(SessionName);
            try
            {
                new Span<byte>((void*)logfile, 448).Clear();
                Marshal.WriteIntPtr(logfile, 8, namePtr);                                   // LoggerName
                Marshal.WriteInt32(logfile, 28, 0x00000100 | 0x10000000);                   // REAL_TIME | EVENT_RECORD
                Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback));   // EventRecordCallback
                _trace = OpenTraceW(logfile);
            }
            finally { Marshal.FreeHGlobal(logfile); Marshal.FreeHGlobal(namePtr); }
            if (_trace == ulong.MaxValue || _trace == 0)
            {
                Fail($"OpenTrace error {Marshal.GetLastWin32Error()}");
                StopSession(SessionName); _session = 0; _trace = 0;
                return;
            }

            RefreshLocalAddresses();
            Running = true;
            _checkLogged = false;
            StartedUtc = DateTime.UtcNow;
            _failedUtc = DateTime.MinValue;
            ulong handle = _trace;
            _thread = new Thread(() =>
            {
                ThreadTags.Set("ETW network");
                try
                {
                    var handles = new[] { handle };
                    uint pr = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
                    if (pr != 0 && Running) AppLogger.Warning($"EtwNetMonitor: ProcessTrace ended with {pr}");
                }
                catch (Exception ex) { AppLogger.Warning(ex, "EtwNetMonitor.ProcessTrace"); }
                finally { Running = false; }
            }) { IsBackground = true, Name = "SMDWin ETW network", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            _secondTimer = new Timer(_ => OnSecond(), null, 1000, 1000);
            AppLogger.Info("EtwNetMonitor: started (Kernel-Network + DNS-Client)");
        }

        private void Fail(string why)
        {
            LastError = why;
            _failedUtc = DateTime.UtcNow;
            Running = false;
            AppLogger.Warning($"EtwNetMonitor: {why} — falling back to the TCP counters");
        }

        private static void FillProperties(IntPtr p, int size)
        {
            new Span<byte>((void*)p, size).Clear();
            Marshal.WriteInt32(p, 0, size);              // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);                // Wnode.ClientContext: QPC timestamps
            Marshal.WriteInt32(p, 44, 0x00020000);       // Wnode.Flags: WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);               // BufferSize (KB)
            Marshal.WriteInt32(p, 52, 8);                // MinimumBuffers
            Marshal.WriteInt32(p, 56, 64);               // MaximumBuffers
            Marshal.WriteInt32(p, 64, 0x00000100);       // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);                // FlushTimer (s)
            Marshal.WriteInt32(p, 116, 120);             // LoggerNameOffset
        }

        private static void StopSession(string name)
        {
            int size = 120 + (name.Length + 1) * 2 + 16 + 1024;
            IntPtr props = Marshal.AllocHGlobal(size);
            try
            {
                FillProperties(props, size);
                ControlTraceW(0, name, props, 1);          // EVENT_TRACE_CONTROL_STOP; "not found" is fine
            }
            catch (Exception ex) { AppLogger.Debug($"EtwNet StopSession: {ex.Message}"); }
            finally { Marshal.FreeHGlobal(props); }
        }

        // ── events (ProcessTrace thread) ─────────────────────────────────────

        private static readonly (long a, long b) KnKey = GuidKey(KernelNetwork), DnsKey = GuidKey(DnsClient);
        private static (long, long) GuidKey(Guid g) { var b = g.ToByteArray(); return (BitConverter.ToInt64(b, 0), BitConverter.ToInt64(b, 8)); }

        private long _rawEvents;   // every callback, recorded or not (diagnostics: how busy the session is)
        public long RawEvents => Interlocked.Read(ref _rawEvents);

        private void OnEvent(IntPtr rec)
        {
            Interlocked.Increment(ref _rawEvents);
            try
            {
                byte* r = (byte*)rec;
                long g1 = *(long*)(r + 24), g2 = *(long*)(r + 32);
                ushort id = *(ushort*)(r + 40);
                ushort len = *(ushort*)(r + 86);
                byte* ud = *(byte**)(r + 96);
                if (ud == null) return;

                if (g1 == KnKey.a && g2 == KnKey.b)
                {
                    // 10/11 TCP send/recv IPv4 · 26/27 TCP IPv6 · 42/43 UDP IPv4 · 58/59 UDP IPv6
                    bool v6, udp, send;
                    switch (id)
                    {
                        case 10: v6 = false; udp = false; send = true; break;
                        case 11: v6 = false; udp = false; send = false; break;
                        case 26: v6 = true;  udp = false; send = true; break;
                        case 27: v6 = true;  udp = false; send = false; break;
                        case 42: v6 = false; udp = true;  send = true; break;
                        case 43: v6 = false; udp = true;  send = false; break;
                        case 58: v6 = true;  udp = true;  send = true; break;
                        case 59: v6 = true;  udp = true;  send = false; break;
                        default: return;
                    }
                    int need = v6 ? 44 : 20;
                    if (len < need) return;
                    int pid = *(int*)ud;
                    uint size = *(uint*)(ud + 4);
                    ulong dHi, dLo, sHi, sLo; int pOff;
                    if (v6)
                    {
                        dHi = *(ulong*)(ud + 8);  dLo = *(ulong*)(ud + 16);
                        sHi = *(ulong*)(ud + 24); sLo = *(ulong*)(ud + 32);
                        pOff = 40;
                    }
                    else
                    {
                        dHi = 0; dLo = *(uint*)(ud + 8);
                        sHi = 0; sLo = *(uint*)(ud + 12);
                        pOff = 16;
                    }
                    ushort dport = Swap(*(ushort*)(ud + pOff)), sport = Swap(*(ushort*)(ud + pOff + 2));
                    Record(pid, size, send, udp, v6, dHi, dLo, dport, sHi, sLo, sport);
                }
                else if (g1 == DnsKey.a && g2 == DnsKey.b && id == 3008)
                {
                    ParseDns(ud, len);
                }
            }
            catch { /* never throw into ETW; a malformed event is skipped */ }
        }

        private static ushort Swap(ushort v) => (ushort)((v >> 8) | (v << 8));

        private void Record(int pid, uint size, bool send, bool udp, bool v6,
                            ulong dHi, ulong dLo, ushort dport, ulong sHi, ulong sLo, ushort sport)
        {
            // which end is the other computer: normally daddr; if daddr is one of ours (and saddr is not), it is saddr
            bool dLocal = IsLocal(v6, dHi, dLo), sLocal = IsLocal(v6, sHi, sLo);
            ulong rHi = dHi, rLo = dLo; ushort rPort = dport, lPort = sport;
            if (dLocal && !sLocal) { rHi = sHi; rLo = sLo; rPort = sport; lPort = dport; }
            if (IsLoopback(v6, rHi, rLo)) return;   // traffic inside this computer is not network traffic

            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                _events++;
                if (!_apps.TryGetValue(pid, out var a)) _apps[pid] = a = new AppAcc();
                a.LastTicks = now;
                if (udp) a.Udp = true; else a.Tcp = true;
                if (send) { a.Sent += size; _totalSent += size; } else { a.Recv += size; _totalRecv += size; }
                var key = (rHi, rLo, rPort, udp);
                if (!a.Remotes.TryGetValue(key, out var rs))
                {
                    if (a.Remotes.Count >= 400) return;   // a scanner / torrent client: totals still count, the list stays bounded
                    a.Remotes[key] = rs = new RemoteAcc { Hi = rHi, Lo = rLo, V6 = v6, RPort = rPort, LPort = lPort, Udp = udp };
                }
                rs.LastTicks = now;
                if (send) rs.Sent += size; else rs.Recv += size;
            }
        }

        private bool IsLocal(bool v6, ulong hi, ulong lo)
        {
            var set = _localKeys;
            return set.Count > 0 && set.Contains((v6 ? hi : 0UL, lo));
        }

        private static bool IsLoopback(bool v6, ulong hi, ulong lo)
        {
            if (!v6) return (lo & 0xFF) == 127;
            return hi == 0 && lo == 0x0100000000000000UL;   // ::1 (network byte order in memory)
        }

        private static IPAddress ToIp(bool v6, ulong hi, ulong lo)
        {
            if (!v6) return new IPAddress((long)(uint)lo);
            Span<byte> b = stackalloc byte[16];
            BitConverter.TryWriteBytes(b, hi);
            BitConverter.TryWriteBytes(b[8..], lo);
            var ip = new IPAddress(b);
            return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        }

        private void ParseDns(byte* ud, int len)
        {
            // template: QueryName (wstring), QueryType (u32), QueryOptions (u64), QueryStatus (u32), QueryResults (wstring)
            int maxChars = len / 2;
            string name = ReadWString(ud, maxChars, out int used);
            int off = (used + 1) * 2 + 4 + 8;
            if (off + 4 > len) return;
            uint status = *(uint*)(ud + off);
            off += 4;
            if (status != 0 || name.Length == 0) return;
            string results = ReadWString(ud + off, (len - off) / 2, out _);
            foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string t = part.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? part[7..] : part;
                if (!IPAddress.TryParse(t, out var ip)) continue;   // "type: 5 cname.example" entries are skipped
                lock (_lock)
                {
                    if (_dns.Count > 30000) _dns.Clear();
                    _dns[ip] = name.TrimEnd('.');
                }
            }
        }

        private static string ReadWString(byte* p, int maxChars, out int chars)
        {
            char* c = (char*)p;
            int n = 0;
            while (n < maxChars && c[n] != '\0') n++;
            chars = n;
            return new string(c, 0, n);
        }

        // ── once a second (timer thread) ─────────────────────────────────────

        private int _localRefreshCountdown;
        private bool _checkLogged;

        private void OnSecond()
        {
            try
            {
                if (!Running) return;
                if (!KeepAlive && DateTime.UtcNow - _lastAskedUtc > IdleStop)
                {
                    AppLogger.Info("EtwNetMonitor: not used for 3 minutes — stopping");
                    ThreadPool.QueueUserWorkItem(_ => Stop());
                    return;
                }
                if (--_localRefreshCountdown <= 0) { _localRefreshCountdown = 30; RefreshLocalAddresses(); }
                if (!_checkLogged && DateTime.UtcNow - StartedUtc > TimeSpan.FromSeconds(30))
                {
                    // one line for the log / self-test: proves the events arrive and are understood on this PC
                    _checkLogged = true;
                    int apps, names; ulong up, down; long ev;
                    lock (_lock) { apps = _apps.Count; names = _dns.Count; up = _totalSent; down = _totalRecv; ev = _events; }
                    AppLogger.Info($"EtwNetMonitor check after 30 s: {ev} events ({RawEvents} callbacks in all), {apps} programs, ↑{up / 1024} KB ↓{down / 1024} KB, {names} DNS names");
                }

                long cutoff = DateTime.UtcNow.AddMinutes(-15).Ticks;
                lock (_lock)
                {
                    List<int>? gone = null;
                    foreach (var (pid, a) in _apps)
                    {
                        a.SendKBs = (a.Sent - a.PrevSent) / 1024.0;
                        a.RecvKBs = (a.Recv - a.PrevRecv) / 1024.0;
                        a.PrevSent = a.Sent; a.PrevRecv = a.Recv;
                        a.HS[a.HPos] = a.SendKBs; a.HR[a.HPos] = a.RecvKBs;
                        a.HPos = (a.HPos + 1) % HistorySeconds;
                        if (a.HCount < HistorySeconds) a.HCount++;
                        if (a.LastTicks < cutoff) (gone ??= new()).Add(pid);
                        else if (a.Remotes.Count > 300)
                        {
                            // keep the list bounded: forget remotes quiet for 10 minutes
                            long rc = DateTime.UtcNow.AddMinutes(-10).Ticks;
                            foreach (var k in a.Remotes.Where(x => x.Value.LastTicks < rc).Select(x => x.Key).ToList()) a.Remotes.Remove(k);
                        }
                    }
                    if (gone != null) foreach (var pid in gone) _apps.Remove(pid);
                }
                Tick?.Invoke();
            }
            catch (Exception ex) { AppLogger.Debug($"EtwNet second: {ex.Message}"); }
        }

        private void RefreshLocalAddresses()
        {
            try
            {
                var set = new HashSet<(ulong, ulong)>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var ip = ua.Address.IsIPv4MappedToIPv6 ? ua.Address.MapToIPv4() : ua.Address;
                        var b = ip.GetAddressBytes();
                        if (b.Length == 4) set.Add((0UL, BitConverter.ToUInt32(b, 0)));
                        else if (b.Length == 16) set.Add((BitConverter.ToUInt64(b, 0), BitConverter.ToUInt64(b, 8)));
                    }
                _localKeys = set;
            }
            catch (Exception ex) { AppLogger.Debug($"EtwNet local addresses: {ex.Message}"); }
        }

        private NetAppTraffic Copy(int pid, AppAcc a, bool withRemotes, bool withHistory)
        {
            var t = new NetAppTraffic
            {
                Pid = pid, Sent = a.Sent, Recv = a.Recv, SendKBs = a.SendKBs, RecvKBs = a.RecvKBs,
                LastSeenUtc = new DateTime(a.LastTicks, DateTimeKind.Utc), HasUdp = a.Udp, HasTcp = a.Tcp,
            };
            if (withHistory)
            {
                t.HistSend = new double[a.HCount]; t.HistRecv = new double[a.HCount];
                int start = (a.HPos - a.HCount + HistorySeconds) % HistorySeconds;
                for (int i = 0; i < a.HCount; i++)
                {
                    t.HistSend[i] = a.HS[(start + i) % HistorySeconds];
                    t.HistRecv[i] = a.HR[(start + i) % HistorySeconds];
                }
            }
            if (withRemotes)
            {
                foreach (var r in a.Remotes.Values)
                {
                    var ip = ToIp(r.V6, r.Hi, r.Lo);
                    t.Remotes.Add(new NetRemoteStat
                    {
                        Address = ip, RemotePort = r.RPort, LocalPort = r.LPort, Udp = r.Udp,
                        Sent = r.Sent, Recv = r.Recv, LastSeenUtc = new DateTime(r.LastTicks, DateTimeKind.Utc),
                        Host = _dns.TryGetValue(ip, out var h) ? h : "",
                    });
                }
                t.Remotes.Sort((x, y) => (y.Sent + y.Recv).CompareTo(x.Sent + x.Recv));
            }
            return t;
        }

        // ── P/Invoke (advapi32) ──────────────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void EventRecordCallback(IntPtr eventRecord);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll")]
        private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
            ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern ulong OpenTraceW(IntPtr logfile);

        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(ulong traceHandle);
    }
}
