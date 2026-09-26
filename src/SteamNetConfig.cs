using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;
using Steamworks.Data;

namespace BigAmbitionsMP
{
    /// <summary>H-STEAMNET-1 (user-approved 2026-09-26, "best of both worlds"): Steam joins keep their convenience
    /// (no IP, no port forwarding) without being held to the relay's throughput and the library's default send caps.
    ///
    /// 1. GLOBAL networking config, set once before any Steam socket is created (a startup watcher applies it the
    ///    first time Steam is valid; the host listener start and the client connect call EnsureApplied again as a
    ///    guard, a no-op once applied):
    ///    - P2P_Transport_ICE_Enable = -1 (Default): the library follows each PLAYER's own Steam IP-sharing setting
    ///      (user decision 2026-09-26); set through Facepunch's INTERNAL ISteamNetworkingUtils.SetConfigValue
    ///      (reflection) - Facepunch has no public ICE setter.
    ///    - SendRateMin = SendRateMax = 256 KB/s and SendBufferSize at the default 512 KB - the starting point of
    ///      every connection; public SteamNetworkingUtils properties.
    ///    Every value is read back after it is set and the effective values are logged beside the defaults.
    /// 2. The same rate/buffer values on each connection (connection-scope SetConfigValue, reflection), read back.
    ///    H-STEAMNET-2: from there each connection's own SteamRateController moves that connection's rate
    ///    (min = max, 256 KB/s - 4 MB/s) and buffer from 1 s samples (settings key SteamRateControl, default on).
    /// 3. A per-link status line - direct or relayed, ping, the library's current send rate, queued bytes - at
    ///    connect, on a 30 s check when the path or the rate bucket changed (and at least every 5 min), and at close.
    ///
    /// Everything is best-effort: a failure logs once and never blocks hosting or joining. No wire change.</summary>
    internal static class SteamNetConfig
    {
        // ── targets (brief H-STEAMNET-1) ─────────────────────────────────────
        // User decision 2026-09-26: RESPECT each player's own Steam 'direct connections (IP sharing)' setting.
        // -1 = k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Default: the library follows the player's choice;
        // players who allow it get the direct NAT-punched path, anyone who chose otherwise stays on the relay.
        internal const int IceWanted         = -1;
        // Manager decision 2026-09-26: 512 KB/s, not 1 MB/s. If the library holds the rate AT the minimum (no
        // bandwidth estimation - unverified), a 1 MB/s floor would overrun slow home uplinks (~8 Mbit/s) and force
        // resends; 512 KB/s (~4 Mbit/s) still doubles the 256 KB/s default. The per-link 'rate=' line in a real
        // two-account session settles whether the rate adapts up toward SendRateMax.
        // User decision 2026-09-26: back to Valve's 256 KB/s until the mod sets each connection's rate itself
        // (Valve: the library does no bandwidth estimation; min = max = a fixed rate) - never worse than the release.
        internal const int SendRateMinWanted = 256 * 1024;
        // Design read 2026-09-26 (Valve snp.cpp SNP_ClampSendRate): with min != max the library FREEZES a rate from
        // the connect-time ping (never updated), so max = min until the per-connection controller (H-STEAMNET-2) exists.
        internal const int SendRateMaxWanted = 256 * 1024;
        // Review MEDIUM: 8 MB at a fixed 256 KB/s is ~32 s of queue ahead of every message (and invisible to the mod's
        // own backlog warnings until Steam refuses). Steam's default until the controller scales it with the rate.
        internal const int SendBufferWanted  = 512 * 1024;

        // Facepunch's internal enums (verified in the shipped Facepunch.Steamworks.Win64.dll):
        // Steamworks.NetConfig, Steamworks.NetConfigScope, Steamworks.NetConfigType, Steamworks.NetConfigResult.
        private const int CfgSendBufferSize = 9, CfgSendRateMin = 10, CfgSendRateMax = 11, CfgIceEnable = 104;
        private const int ScopeGlobal = 1, ScopeConnection = 4;
        private const int TypeInt32 = 1;

        private static readonly object _gate = new object();
        private static volatile bool _applied;
        private static int _watchStarted, _applyFailLogged;
        private static volatile string _summary = "not applied (Steam not valid yet)";
        private static volatile string _appliedAt = "";   // which caller applied it: "startup" = the watcher, before any socket

        /// <summary>The effective global values as last read back ("ICE=.. sendRate=..-.. sendBuffer=..").</summary>
        internal static string Summary => _summary;
        internal static bool Applied => _applied;

        /// <summary>Plugin startup: poll (background thread, 0.5 s) until Steam is valid, then apply once. Steam
        /// turns valid some time after the mod loads (the game initialises it), so a direct call at load would
        /// find no Steam interface. Ends after 30 min or once applied.</summary>
        internal static void StartWatch()
        {
            if (Interlocked.Exchange(ref _watchStarted, 1) == 1) return;
            try
            {
                var th = new Thread(() =>
                {
                    for (int i = 0; i < 3600 && !_applied; i++)
                    {
                        try { if (SteamClient.IsValid) EnsureApplied("startup"); } catch { }
                        if (_applied) break;
                        Thread.Sleep(500);
                    }
                }) { IsBackground = true, Name = "BAMP-SteamNetCfg" };
                th.Start();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamNet] config watcher: {ex.Message} - config is applied at the first host/join instead."); }
        }

        /// <summary>Apply the global config once. Safe from any thread (the networking utils are thread-safe);
        /// returns false (and retries on the next call) while Steam or its networking interface is not up yet.</summary>
        internal static bool EnsureApplied(string why)
        {
            if (_applied) return true;
            lock (_gate)
            {
                if (_applied) return true;
                try { if (!SteamClient.IsValid) return false; } catch { return false; }
                try
                {
                    if (!EnsureReflection()) return false;
                    string dIce = ReadText(CfgIceEnable, ScopeGlobal, IntPtr.Zero), dMin = ReadText(CfgSendRateMin, ScopeGlobal, IntPtr.Zero),
                           dMax = ReadText(CfgSendRateMax, ScopeGlobal, IntPtr.Zero), dBuf = ReadText(CfgSendBufferSize, ScopeGlobal, IntPtr.Zero);
                    // Max before min: a min above the CURRENT max could be clamped by the library.
                    SteamNetworkingUtils.SendRateMax    = SendRateMaxWanted;
                    SteamNetworkingUtils.SendRateMin    = SendRateMinWanted;
                    SteamNetworkingUtils.SendBufferSize = SendBufferWanted;
                    bool iceOk = SetInt(CfgIceEnable, ScopeGlobal, IntPtr.Zero, IceWanted, out string iceErr);
                    string ice = ReadText(CfgIceEnable, ScopeGlobal, IntPtr.Zero), mn = ReadText(CfgSendRateMin, ScopeGlobal, IntPtr.Zero),
                           mx = ReadText(CfgSendRateMax, ScopeGlobal, IntPtr.Zero), buf = ReadText(CfgSendBufferSize, ScopeGlobal, IntPtr.Zero);
                    _summary = $"ICE={ice} sendRate={mn}-{mx} sendBuffer={buf}";
                    bool match = ice == IceWanted.ToString() && mn == SendRateMinWanted.ToString() && mx == SendRateMaxWanted.ToString() && buf == SendBufferWanted.ToString();
                    Plugin.Logger.LogInfo($"[SteamNet] config: {_summary} (defaults were ICE={dIce} sendRate={dMin}-{dMax} sendBuffer={dBuf}) - applied at {why}"
                        + (match ? "." : $"; WANTED ICE={IceWanted} sendRate={SendRateMinWanted}-{SendRateMaxWanted} sendBuffer={SendBufferWanted}")
                        + (iceOk ? "" : $"; ICE set refused: {iceErr}"));
                    _appliedAt = why;
                    _applied = true;
                    return true;
                }
                catch (Exception ex)
                {
                    // Facepunch flips SteamClient.IsValid BEFORE it adds the interfaces, so the first try can land
                    // in that window - retried on the next call; logged once.
                    if (Interlocked.Exchange(ref _applyFailLogged, 1) == 0)
                        Plugin.Logger.LogWarning($"[SteamNet] config not applied yet ({why}): {ex.GetType().Name}: {ex.Message} - will retry.");
                    return false;
                }
            }
        }

        /// <summary>The per-connection override: the same rate/buffer values in connection scope, read back.</summary>
        internal static void ApplyToConnection(Connection conn, string who)
        {
            try
            {
                if (!EnsureReflection()) return;
                var h = new IntPtr(conn.Id);
                bool ok = SetInt(CfgSendRateMax, ScopeConnection, h, SendRateMaxWanted, out string e1)
                        & SetInt(CfgSendRateMin, ScopeConnection, h, SendRateMinWanted, out string e2)
                        & SetInt(CfgSendBufferSize, ScopeConnection, h, SendBufferWanted, out string e3);
                Plugin.Logger.LogInfo($"[SteamNet] conn {who} config: sendRate={ReadText(CfgSendRateMin, ScopeConnection, h)}-{ReadText(CfgSendRateMax, ScopeConnection, h)} "
                    + $"sendBuffer={ReadText(CfgSendBufferSize, ScopeConnection, h)}" + (ok ? "." : $" (a set was refused: {e1}{e2}{e3})"));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamNet] conn {who} config: {ex.Message}"); }
        }

        /// <summary>H-STEAMNET-2: set ONE connection's send rate (min = max = rate - raising sets max first, lowering
        /// min first, so min never passes max) and/or its send buffer, then read every value back. 0 = leave it.
        /// False when a set was refused or a read-back differs (err says which).</summary>
        internal static bool SetConnectionRate(Connection conn, int rate, int buffer, bool raising, out string err)
        {
            err = "";
            try
            {
                if (!EnsureReflection()) { err = "config surface unavailable"; return false; }
                var h = new IntPtr(conn.Id);
                // fold F5: stop at the FIRST refused set - no later set runs (&& short-circuits)
                bool ok = true; string e1 = "", e2 = "", e3 = "";
                if (rate > 0)
                {
                    if (raising) ok = SetInt(CfgSendRateMax, ScopeConnection, h, rate, out e1) && SetInt(CfgSendRateMin, ScopeConnection, h, rate, out e2);
                    else         ok = SetInt(CfgSendRateMin, ScopeConnection, h, rate, out e2) && SetInt(CfgSendRateMax, ScopeConnection, h, rate, out e1);
                }
                if (ok && buffer > 0) ok = SetInt(CfgSendBufferSize, ScopeConnection, h, buffer, out e3);
                if (!ok) { err = ("set refused: " + e1 + " " + e2 + " " + e3).Trim(); return false; }
                if (rate > 0)
                {
                    string mn = ReadText(CfgSendRateMin, ScopeConnection, h), mx = ReadText(CfgSendRateMax, ScopeConnection, h);
                    if (mn != rate.ToString() || mx != rate.ToString()) { err = $"read-back sendRate={mn}-{mx}, wanted {rate}"; return false; }
                }
                if (buffer > 0)
                {
                    string b = ReadText(CfgSendBufferSize, ScopeConnection, h);
                    if (b != buffer.ToString()) { err = $"read-back sendBuffer={b}, wanted {buffer}"; return false; }
                }
                return true;
            }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        // ── reflection onto Facepunch's internal ISteamNetworkingUtils ───────
        private static bool _reflReady; private static int _reflFailLogged;
        private static PropertyInfo? _utilsInternalProp;
        private static MethodInfo? _setCfg, _getCfg;
        private static Type? _tNetConfig, _tScope, _tType;

        private static bool EnsureReflection()
        {
            if (_reflReady) return true;
            try
            {
                var asm = typeof(SteamNetworkingUtils).Assembly;
                _tNetConfig = asm.GetType("Steamworks.NetConfig", true);
                _tScope     = asm.GetType("Steamworks.NetConfigScope", true);
                _tType      = asm.GetType("Steamworks.NetConfigType", true);
                _utilsInternalProp = typeof(SteamNetworkingUtils).GetProperty("Internal", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new MissingMemberException("SteamNetworkingUtils.Internal");
                var it = _utilsInternalProp.PropertyType;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _setCfg = it.GetMethod("SetConfigValue", inst) ?? throw new MissingMethodException(it.Name, "SetConfigValue");
                _getCfg = it.GetMethod("GetConfigValue", inst) ?? throw new MissingMethodException(it.Name, "GetConfigValue");
                _reflReady = true;
                return true;
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _reflFailLogged, 1) == 0)
                    Plugin.Logger.LogWarning($"[SteamNet] Facepunch config surface not found ({ex.GetType().Name}: {ex.Message}) - Steam networking keeps the library defaults.");
                return false;
            }
        }

        private static object UtilsInstance() =>
            _utilsInternalProp!.GetValue(null) ?? throw new InvalidOperationException("SteamNetworkingUtils interface not up yet");

        private static bool SetInt(int cfg, int scope, IntPtr scopeObj, int value, out string err)
        {
            err = "";
            IntPtr buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt32(buf, value);
                object? r = _setCfg!.Invoke(UtilsInstance(), new object[] {
                    Enum.ToObject(_tNetConfig!, cfg), Enum.ToObject(_tScope!, scope), scopeObj, Enum.ToObject(_tType!, TypeInt32), buf });
                if (r is bool b && b) return true;
                err = $"cfg {cfg} scope {scope} returned false";
                return false;
            }
            catch (Exception ex) { err = $"cfg {cfg}: {ex.GetType().Name} {(ex.InnerException ?? ex).Message}"; return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>The effective value as text; "?rc" when the library refused the read (rc = NetConfigResult).</summary>
        private static string ReadText(int cfg, int scope, IntPtr scopeObj)
        {
            IntPtr buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt32(buf, 0);
                var args = new object[] { Enum.ToObject(_tNetConfig!, cfg), Enum.ToObject(_tScope!, scope), scopeObj,
                                          Enum.ToObject(_tType!, TypeInt32), buf, new UIntPtr(4u) };
                int rc = Convert.ToInt32(_getCfg!.Invoke(UtilsInstance(), args));
                return rc == 1 || rc == 2 ? Marshal.ReadInt32(buf).ToString() : "?" + rc;   // OK / OKInherited
            }
            catch (Exception ex) { return "?" + ex.GetType().Name; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ── link status (path / ping / rate / queued) ────────────────────────
        // Facepunch's ConnectionInfo struct declares the fields only up to the description string, so the
        // m_nFlags word (offset 440 of SteamNetConnectionInfo_t, 696 bytes) is lost in its marshalling. The flat
        // API export is called directly into a raw buffer instead; the interface pointer is Facepunch's own
        // (SteamNetworkingSockets.Internal.Self, reflection).
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamNetworkingSockets_GetConnectionInfo")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool NativeGetConnectionInfo(IntPtr self, uint hConn, IntPtr pInfo);

        private const int InfoSize = 696, OffPopRelay = 172, OffFlags = 440;
        private const int FlagRelayed = 16;   // k_nSteamNetworkConnectionInfoFlags_Relayed (SDR or TURN)
        private static int _pathFailLogged;
        private static FieldInfo? _sendRateField;
        private static PropertyInfo? _socketsInternalProp;

        private static string ReadPath(Connection conn, out int flags)
        {
            flags = -1;
            IntPtr buf = IntPtr.Zero;
            try
            {
                _socketsInternalProp ??= typeof(SteamNetworkingSockets).GetProperty("Internal", BindingFlags.NonPublic | BindingFlags.Static);
                object? iface = _socketsInternalProp?.GetValue(null);
                if (iface == null) return "unknown";
                var selfField = iface.GetType().GetField("Self", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                IntPtr self = selfField?.GetValue(iface) is IntPtr sp ? sp : IntPtr.Zero;
                if (self == IntPtr.Zero) return "unknown";
                buf = Marshal.AllocHGlobal(InfoSize);
                for (int i = 0; i < InfoSize; i += 4) Marshal.WriteInt32(buf, i, 0);
                if (!NativeGetConnectionInfo(self, conn.Id, buf)) return "unknown";
                flags = Marshal.ReadInt32(buf, OffFlags);
                uint popRelay = unchecked((uint)Marshal.ReadInt32(buf, OffPopRelay));
                return (flags & FlagRelayed) != 0 || popRelay != 0 ? "relayed" : "direct";
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _pathFailLogged, 1) == 0)
                    Plugin.Logger.LogWarning($"[SteamNet] connection-info read unavailable ({ex.GetType().Name}: {ex.Message}) - path reads 'unknown'.");
                return "unknown";
            }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        private static int ReadSendRate(ConnectionStatus st)
        {
            try
            {
                _sendRateField ??= typeof(ConnectionStatus).GetField("sendRateBytesPerSecond", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return _sendRateField?.GetValue(st) is int r ? r : -1;
            }
            catch { return -1; }
        }

        private static readonly ConcurrentDictionary<LinkWatch, byte> _live = new();
        // A1 (2026-09-26): the last 8 CLOSED links' close lines (without the '[SteamNet] link ' prefix), for report.md
        private static readonly ConcurrentQueue<string> _closedLines = new();
        private const int ClosedKept = 8;

        /// <summary>A1: the bug report's 'Steam connections' lines - one per live link, then the last 8 closed
        /// links' close lines. Empty when there are none. Never throws.</summary>
        internal static System.Collections.Generic.List<string> DescribeForReport()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var w in _live.Keys)
                {
                    try { list.Add("live: " + w.ReportLine()); } catch (Exception ex) { list.Add("live: (link read failed: " + ex.Message + ")"); }
                }
                foreach (var c in _closedLines.ToArray()) list.Add("closed: " + c);
            }
            catch (Exception ex) { list.Add("(read failed: " + ex.GetType().Name + ": " + ex.Message + ")"); }
            return list;
        }
        private static FieldInfo? _stateField;

        /// <summary>H-STEAMNET-2: ConnectionStatus.state is internal in Facepunch (no property) - read by reflection;
        /// -99 when it cannot be read (the controller ignores such a sample).</summary>
        private static int ReadState(ConnectionStatus st)
        {
            try
            {
                _stateField ??= typeof(ConnectionStatus).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object? v = _stateField?.GetValue(st);
                return v == null ? -99 : Convert.ToInt32(v);
            }
            catch { return -99; }
        }

        /// <summary>One Steam connection's status reporter and (H-STEAMNET-2) its send-rate controller. Tick from the
        /// owning pump thread (~15 ms): the controller samples every 1 s, the status line is checked every 30 s.
        /// OnClose from whichever thread closes. Status line: at connect, when the path or the send rate's doubling
        /// bucket changed since the last line (checked every 30 s), at least every 5 min, at close.</summary>
        internal sealed class LinkWatch
        {
            private static readonly long Freq = System.Diagnostics.Stopwatch.Frequency;
            private readonly Connection _conn;
            private readonly string _who;
            private readonly Func<long> _ours;
            private readonly Func<bool> _lanes;
            private string _lastPath = "";
            private int _lastBucket = int.MinValue;
            private long _nextCheck, _nextForce;
            private long _nextSample = long.MaxValue;   // H-STEAMNET-2: next 1 s controller sample (MaxValue = controller not running)
            private int _closed;

            // ── H-STEAMNET-2: the per-link rate controller ──
            private SteamRateController? _rc;
            private bool _rcDisabled;
            private string _rcState = "not started";
            private long _accepted;                        // reliable bytes Steam ACCEPTED from the mod (Interlocked)
            private int _upFrom = -1, _upMerged;           // rises merged into one line per 10 s
            private SteamRateDecision? _upLast;
            private long _nextUpLog;
            private int _downLogged, _downHidden;          // back-offs: 30 lines per link, then one summary per 60 s
            private long _nextDownSummary;
            private int _sampleFailLogged;
            private int _resetHidden;                      // fold F6: reset lines at most one per 60 s per link
            private long _nextResetLog;

            internal LinkWatch(Connection conn, string who, Func<long> ourQueuedBytes, Func<bool> lanesOk)
            {
                _conn = conn; _who = who; _ours = ourQueuedBytes; _lanes = lanesOk;
                _live[this] = 0;
            }

            /// <summary>H-STEAMNET-2: a reliable send Steam accepted (Result.OK) - any thread. Feeds the delivery figure.</summary>
            internal void NoteAccepted(int bytes) { if (bytes > 0) Interlocked.Add(ref _accepted, bytes); }

            internal void OnConnected()
            {
                ApplyToConnection(_conn, _who);
                bool on = true;
                try { on = MPConfig.SteamRateControlLive(); } catch { }
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (this)
                {
                    if (on) { _rc = new SteamRateController(); _rcState = "on"; _nextSample = now + Freq; }
                    else _rcState = "off (SteamRateControl=false)";
                    Emit("connected"); _nextCheck = now + 30 * Freq; _nextForce = now + 300 * Freq;
                }
                try
                {
                    Plugin.Logger.LogInfo(on
                        ? $"[SteamNet] rate {_who} control on: start {SteamRateController.Start / 1024} KB/s, floor {SteamRateController.Floor / 1024} KB/s, ceiling {SteamRateController.Ceiling / 1024} KB/s (min = max per connection, 1 s samples)."
                        : $"[SteamNet] rate {_who} control OFF (settings SteamRateControl=false) - pinned {SendRateMinWanted / 1024} KB/s.");
                }
                catch { }
            }

            internal void Tick()
            {
                if (Volatile.Read(ref _closed) != 0) return;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now < Volatile.Read(ref _nextCheck) && now < Volatile.Read(ref _nextSample)) return;
                lock (this)
                {
                    if (Volatile.Read(ref _closed) != 0) return;
                    if (now >= _nextSample) { _nextSample = now + Freq; RateStep(now); }
                    if (now < _nextCheck) return;
                    _nextCheck = now + 30 * Freq;
                    var s = Sample();
                    if (now >= _nextForce || s.path != _lastPath || Bucket(s.rate) != _lastBucket)
                    { Emit(now >= _nextForce ? "5 min" : "changed", s); _nextForce = now + 300 * Freq; }
                }
            }

            internal void OnClose(string why)
            {
                if (Interlocked.Exchange(ref _closed, 1) == 1) return;
                _live.TryRemove(this, out _);
                try
                {
                    lock (this)
                    {
                        try { FlushUp(System.Diagnostics.Stopwatch.GetTimestamp()); FlushDownSummary(); } catch { }
                        Emit("close: " + why);
                    }
                }
                catch { }
            }

            /// <summary>The status line, logged, for the DEV lever.</summary>
            internal string OnDemand() { lock (this) return Emit("on demand"); }

            /// <summary>A1: this link's line for report.md's 'Steam connections' section (not logged).</summary>
            internal string ReportLine()
            {
                try
                {
                    lock (this)
                    {
                        var s = Sample();
                        var rc = _rc;
                        bool on = rc != null && !_rcDisabled;
                        string r = $"{_who} path={s.path} ping={s.ping} rate={(s.rate < 0 ? "?" : (s.rate / 1024).ToString())}KB/s";
                        if (on) r += $" target={rc!.Rate / 1024}KB/s ceil={rc.LearnedCeiling / 1024}KB/s dlv={(rc.LastDelivery < 0 ? "?" : (rc.LastDelivery / 1024).ToString())}KB/s"
                                   + $" backoffs={rc.Backoffs} peak={rc.PeakRate / 1024}KB/s floorSec={rc.FloorBusyMs / 1000}";
                        else r += $" target={SendRateMinWanted / 1024}KB/s";
                        return r + $" ctl={(on ? "on" : _rcDisabled ? "disabled" : "off")}";
                    }
                }
                catch (Exception ex) { return $"{_who} (read failed: {ex.GetType().Name}: {ex.Message})"; }
            }

            /// <summary>H-STEAMNET-2 DEV lever 'steamrate': this link's controller state.</summary>
            internal string RateState()
            {
                lock (this)
                {
                    var rc = _rc;
                    return $"{_who} ctl={_rcState}" + (rc != null ? " " + rc.Describe() : "") + $" accepted={Interlocked.Read(ref _accepted)}";
                }
            }

            // ── H-STEAMNET-2: sample -> controller -> apply -> log (pump thread, under lock(this)) ──
            private void RateStep(long now)
            {
                var rc = _rc;
                if (rc == null || _rcDisabled) { _nextSample = long.MaxValue; return; }
                try
                {
                    var smp = new SteamRateSample { TimeMs = (long)(now * 1000.0 / Freq) };
                    try { smp.LanesOk = _lanes(); } catch { }
                    long acc0 = Interlocked.Read(ref _accepted);   // fold F4: the accepted total BEFORE the status read
                    try
                    {
                        var st = _conn.QuickStatus();   // discards its result code: a failed read is all zeros (state 0, ping 0) - the controller ignores it
                        smp.State = ReadState(st); smp.PingMs = st.Ping;
                        smp.QualityLocal = st.ConnectionQualityLocal; smp.QualityRemote = st.ConnectionQualityRemote;
                        smp.OutBytesPerSec = st.OutBytesPerSec;
                        smp.SteamPending = (long)st.PendingReliable + st.PendingUnreliable;
                        smp.SentUnacked = st.SentUnackedReliable;
                    }
                    catch { smp.State = -99; }
                    long acc1 = Interlocked.Read(ref _accepted);   // ... and AFTER it
                    try { smp.ModBacklog = _ours(); } catch { }
                    smp.AcceptedTotal = acc1;
                    // F4: a send was accepted while Steam's queues were read - the pair does not match; skip this
                    // sample (no Step). The next one covers the interval (dt < 5 s keeps it a normal interval).
                    if (acc0 == acc1)
                    {
                        if (smp.State == SteamRateController.ConnectedState) { ReadPath(_conn, out int flags); smp.PathFlags = flags; }
                        var d = rc.Step(smp);
                        if (d != null) Apply(d, now);
                    }
                    FlushUp(now, onlyIfDue: true);
                    if (_downHidden > 0 && now >= _nextDownSummary) FlushDownSummary();
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref _sampleFailLogged, 1) == 0)
                        Plugin.Logger.LogWarning($"[SteamNet] rate {_who} sample: {ex.GetType().Name}: {ex.Message} (logged once).");
                }
            }

            private void Apply(SteamRateDecision d, long now)
            {
                if (d.NewRate > 0 || d.NewBuffer > 0)
                {
                    if (!SetConnectionRate(_conn, d.NewRate, d.NewBuffer, d.NewRate > d.OldRate, out string err))
                    {
                        Disable($"{d.Kind} {d.OldRate / 1024}->{(d.NewRate > 0 ? d.NewRate : d.OldRate) / 1024}KB/s"
                            + (d.NewBuffer > 0 ? $" buffer {d.NewBuffer / 1024}KB" : "") + $": {err}");
                        return;
                    }
                }
                switch (d.Kind)
                {
                    case "up":
                        if (_upFrom < 0) _upFrom = d.OldRate;
                        _upLast = d; _upMerged++;
                        FlushUp(now, onlyIfDue: true);
                        break;
                    case "reset":
                        FlushUp(now, onlyIfDue: false);
                        // fold F6: at most one reset line per 60 s per link; the hidden ones are counted in the next line
                        if (now >= _nextResetLog)
                        {
                            Plugin.Logger.LogInfo(RateLine(d.OldRate, d.NewRate > 0 ? d.NewRate : d.OldRate, d.Kind, d)
                                + (_resetHidden > 0 ? $" (+{_resetHidden} reset(s) not logged since the last reset line)" : ""));
                            _resetHidden = 0; _nextResetLog = now + 60 * Freq;
                        }
                        else _resetHidden++;
                        break;
                    case "down":
                        FlushUp(now, onlyIfDue: false);
                        if (_downLogged < 30)
                        {
                            _downLogged++;
                            Plugin.Logger.LogInfo(RateLine(d.OldRate, d.NewRate > 0 ? d.NewRate : d.OldRate, d.Kind, d));
                        }
                        else
                        {
                            if (_downHidden++ == 0) _nextDownSummary = now + 60 * Freq;
                        }
                        break;
                    case "floor":
                        FlushUp(now, onlyIfDue: false);
                        Plugin.Logger.LogInfo($"[SteamNet] rate {_who} at floor {d.OldRate / 1024}KB/s, still congested {Metrics(d)} (logged once per link).");
                        break;
                    case "buffer":
                        Plugin.Logger.LogInfo($"[SteamNet] rate {_who} sendBuffer {d.OldBuffer / 1024}->{d.NewBuffer / 1024}KB ({d.Reason}).");
                        break;
                }
            }

            /// <summary>Log the merged rises - when the 10 s window is open (onlyIfDue) or at once (before a
            /// back-off/reset line, at close).</summary>
            private void FlushUp(long now, bool onlyIfDue = false)
            {
                var d = _upLast;
                if (_upFrom < 0 || d == null) return;
                if (onlyIfDue && now < _nextUpLog) return;
                string line = RateLine(_upFrom, d.OldRate < d.NewRate ? d.NewRate : d.OldRate, "up", d);
                if (_upMerged > 1) line += $" ({_upMerged} steps)";
                Plugin.Logger.LogInfo(line);
                _upFrom = -1; _upLast = null; _upMerged = 0; _nextUpLog = now + 10 * Freq;
            }

            private void FlushDownSummary()
            {
                if (_downHidden <= 0) return;
                var rc = _rc;
                Plugin.Logger.LogInfo($"[SteamNet] rate {_who} {_downHidden} more back-off(s) not logged in the last 60 s; now {(rc?.Rate ?? 0) / 1024}KB/s ceil={(rc?.LearnedCeiling ?? 0) / 1024}.");
                _downHidden = 0; _nextDownSummary = System.Diagnostics.Stopwatch.GetTimestamp() + 60 * Freq;
            }

            private string RateLine(int oldRate, int newRate, string kind, SteamRateDecision d)
                => $"[SteamNet] rate {_who} {oldRate / 1024}->{newRate / 1024}KB/s {kind} {Metrics(d)} ceil={d.Ceiling / 1024}"
                 + $" why={d.Reason}" + (d.NewBuffer > 0 ? $" sendBuffer={d.NewBuffer / 1024}KB" : "");

            private static string Metrics(SteamRateDecision d)
                => $"(dlv={(d.Delivery < 0 ? "?" : (d.Delivery / 1024).ToString())} util={d.Util:F2} growth={d.Growth} qL={d.QL:F2} qR={d.QR:F2} backlog={d.Backlog / 1024}K)";

            /// <summary>A set was refused or read back wrong: stop the controller for this link and pin today's
            /// 256 KB/s (buffer 512 KB). Logged once; silent when the link is already closing.</summary>
            private void Disable(string why)
            {
                _rcDisabled = true; _nextSample = long.MaxValue; _rcState = "disabled";
                bool pinned = SetConnectionRate(_conn, SendRateMinWanted, SendBufferWanted, raising: false, out string perr);
                if (Volatile.Read(ref _closed) != 0) return;
                Plugin.Logger.LogWarning($"[SteamNet] rate {_who} controller DISABLED ({why}) - pinned {SendRateMinWanted / 1024} KB/s"
                    + (pinned ? "." : $" (the pin was refused too: {perr})."));
            }

            private string RateTag(bool close)
            {
                var rc = _rc;
                if (rc == null || _rcDisabled) return $" target={SendRateMinWanted / 1024} ctl={(_rcDisabled ? "disabled" : "off")}";
                string s = $" target={rc.Rate / 1024} ceil={rc.LearnedCeiling / 1024} dlv={(rc.LastDelivery < 0 ? "?" : (rc.LastDelivery / 1024).ToString())}";
                if (close) s += $" peak={rc.PeakRate / 1024} backoffs={rc.Backoffs} floorSec={rc.FloorBusyMs / 1000}";
                return s;
            }

            private (string path, int flags, int ping, int rate, long queued) Sample()
            {
                string path = ReadPath(_conn, out int flags);
                int ping = -1, rate = -1; long queued = 0;
                try { var st = _conn.QuickStatus(); ping = st.Ping; rate = ReadSendRate(st); queued = (long)st.PendingReliable + st.PendingUnreliable; } catch { }
                try { queued += _ours(); } catch { }
                return (path, flags, ping, rate, queued);
            }

            private string Emit(string evt) => Emit(evt, Sample());

            private string Emit(string evt, (string path, int flags, int ping, int rate, long queued) s)
            {
                _lastPath = s.path; _lastBucket = Bucket(s.rate);
                string rt = "";
                try { rt = RateTag(evt.StartsWith("close", StringComparison.Ordinal)); } catch { }
                string line = $"[SteamNet] link {_who} path={s.path} ping={s.ping} rate={(s.rate < 0 ? "?" : (s.rate / 1024).ToString())} queued={s.queued} flags=0x{s.flags:X}{rt} ({evt})";
                Plugin.Logger.LogInfo(line);
                if (evt.StartsWith("close", StringComparison.Ordinal))
                {
                    try   // A1: kept in memory for the bug report (last 8)
                    {
                        _closedLines.Enqueue(line.Substring("[SteamNet] link ".Length));
                        while (_closedLines.Count > ClosedKept) _closedLines.TryDequeue(out _);
                    }
                    catch { }
                }
                return line;
            }

            private static int Bucket(int rate) => rate <= 0 ? -1 : (int)Math.Floor(Math.Log(Math.Max(1, rate / 1024), 2));
        }

        /// <summary>DEV lever 'steamnet': the config summary plus every live link's status line (each also logged).</summary>
        internal static string DescribeForLever()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"config: {Summary} applied={_applied} at='{_appliedAt}' links={_live.Count}");
            foreach (var w in _live.Keys)
            {
                try { sb.Append(" | ").Append(w.OnDemand()); } catch (Exception ex) { sb.Append(" | (link read failed: " + ex.Message + ")"); }
            }
            return sb.ToString();
        }

        /// <summary>H-STEAMNET-2 DEV lever 'steamrate': the settings key and every live link's controller state.</summary>
        internal static string DescribeRateForLever()
        {
            var sb = new System.Text.StringBuilder();
            bool on = true;
            try { on = MPConfig.SteamRateControlLive(); } catch { }
            sb.Append($"rateControl={(on ? "on" : "off")} links={_live.Count}");
            foreach (var w in _live.Keys)
            {
                try { sb.Append(" | ").Append(w.RateState()); } catch (Exception ex) { sb.Append(" | (link read failed: " + ex.Message + ")"); }
            }
            return sb.ToString();
        }
    }
}
