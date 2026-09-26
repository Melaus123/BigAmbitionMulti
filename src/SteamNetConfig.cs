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
    ///    - P2P_Transport_ICE_Enable = Private | Public: the library tries a DIRECT UDP path (NAT punch-through,
    ///      signalled through Steam) and keeps the relay as its own fallback. Set through Facepunch's INTERNAL
    ///      ISteamNetworkingUtils.SetConfigValue (reflection) - Facepunch has no public ICE setter.
    ///    - SendRateMin / SendRateMax / SendBufferSize raised - public SteamNetworkingUtils properties.
    ///    Every value is read back after it is set and the effective values are logged beside the defaults.
    /// 2. The same rate/buffer values on each connection (connection-scope SetConfigValue, reflection), read back.
    /// 3. A per-link status line - direct or relayed, ping, the library's current send rate, queued bytes - at
    ///    connect, on a 30 s check when the path or the rate bucket changed (and at least every 5 min), and at close.
    ///
    /// Everything is best-effort: a failure logs once and never blocks hosting or joining. No wire change.</summary>
    internal static class SteamNetConfig
    {
        // ── targets (brief H-STEAMNET-1) ─────────────────────────────────────
        internal const int IceWanted         = 2 | 4;              // k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Private | _Public
        // Manager decision 2026-09-26: 512 KB/s, not 1 MB/s. If the library holds the rate AT the minimum (no
        // bandwidth estimation - unverified), a 1 MB/s floor would overrun slow home uplinks (~8 Mbit/s) and force
        // resends; 512 KB/s (~4 Mbit/s) still doubles the 256 KB/s default. The per-link 'rate=' line in a real
        // two-account session settles whether the rate adapts up toward SendRateMax.
        internal const int SendRateMinWanted = 512 * 1024;
        internal const int SendRateMaxWanted = 16 * 1024 * 1024;   // 16 MB/s
        internal const int SendBufferWanted  = 8 * 1024 * 1024;    // 8 MB

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

        /// <summary>One Steam connection's status reporter. Tick from the owning pump thread (~15 ms; it only
        /// samples every 30 s), OnClose from whichever thread closes. Logs: at connect, when the path or the send
        /// rate's doubling bucket changed since the last line (checked every 30 s), at least every 5 min, at close.</summary>
        internal sealed class LinkWatch
        {
            private static readonly long Freq = System.Diagnostics.Stopwatch.Frequency;
            private readonly Connection _conn;
            private readonly string _who;
            private readonly Func<long> _ours;
            private string _lastPath = "";
            private int _lastBucket = int.MinValue;
            private long _nextCheck, _nextForce;
            private int _closed;

            internal LinkWatch(Connection conn, string who, Func<long> ourQueuedBytes)
            {
                _conn = conn; _who = who; _ours = ourQueuedBytes;
                _live[this] = 0;
            }

            internal void OnConnected()
            {
                ApplyToConnection(_conn, _who);
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (this) { Emit("connected"); _nextCheck = now + 30 * Freq; _nextForce = now + 300 * Freq; }
            }

            internal void Tick()
            {
                if (Volatile.Read(ref _closed) != 0) return;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now < Volatile.Read(ref _nextCheck)) return;
                lock (this)
                {
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
                try { lock (this) Emit("close: " + why); } catch { }
            }

            /// <summary>The status line, logged, for the DEV lever.</summary>
            internal string OnDemand() { lock (this) return Emit("on demand"); }

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
                string line = $"[SteamNet] link {_who} path={s.path} ping={s.ping} rate={(s.rate < 0 ? "?" : (s.rate / 1024).ToString())} queued={s.queued} flags=0x{s.flags:X} ({evt})";
                Plugin.Logger.LogInfo(line);
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
    }
}
