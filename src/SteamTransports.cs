using System;
using System.Threading;
using Steamworks;
using Steamworks.Data;

namespace BigAmbitionsMP
{
    // ── Steam relay transports (Steam-connect campaign, slice 2) ─────────────
    // Ride the game-initialized Facepunch SteamClient (2.5.2): connect by
    // SteamId over Valve's relay network — no port forwarding, no IP exposure.
    // Verified API surface recorded in .modding/03-systems/steam-connect-map.md.
    //
    // Threading: connection-state callbacks dispatch from the game's main
    // thread (it pumps SteamClient.RunCallbacks); OnMessage fires from our
    // Receive() pump thread.  Same handler contracts as the UDP transports —
    // MPServer/MPClient handlers already marshal internally where needed.
    //
    // Close-reason tags: LiteNetLib carries the host's "BAMP:..." refusal tag
    // as disconnect DATA; Steam's ConnectionInfo exposes no debug string, so
    // SteamLink.Disconnect sends the tag as a CONTROL FRAME (0x02 'B' 'C' 'L'
    // prefix — an envelope serializes as JSON and can never start with 0x02)
    // immediately before closing; the client transport stashes it and hands it
    // to Disconnected(reason, extra) exactly like the UDP path.

    internal static class SteamFrames
    {
        public static readonly byte[] ClosePrefix = { 0x02, (byte)'B', (byte)'C', (byte)'L' };

        public static byte[] WrapClose(byte[] tag)
        {
            var f = new byte[ClosePrefix.Length + (tag?.Length ?? 0)];
            ClosePrefix.CopyTo(f, 0);
            if (tag != null && tag.Length > 0) Array.Copy(tag, 0, f, ClosePrefix.Length, tag.Length);
            return f;
        }

        public static bool IsClose(byte[] data, out byte[] tag)
        {
            tag = Array.Empty<byte>();
            if (data == null || data.Length < ClosePrefix.Length) return false;
            for (int i = 0; i < ClosePrefix.Length; i++)
                if (data[i] != ClosePrefix[i]) return false;
            tag = new byte[data.Length - ClosePrefix.Length];
            Array.Copy(data, ClosePrefix.Length, tag, 0, tag.Length);
            return true;
        }

        // ── Fragment frames (field 2026-07-19: stuck "waiting for" overlay) ──
        // Steam's SendMessage hard-caps a message at 512KB and REFUSES larger
        // ones by Result — the uncompressed-JSON business snapshot exceeded it,
        // so clients never became world-ready over the relay.  LiteNetLib
        // fragments big reliable messages natively; Steam does not — so we do:
        // payloads over ChunkSize split into [0x02 'B' 'F' 'R'][msgId:4][idx:2]
        // [count:2][chunk] frames and reassemble on receive.  Both sides need
        // this build for large messages over Steam (old peers see the frames as
        // unparseable envelopes and drop them — no worse than today's silent
        // loss).  IP/LAN sessions are untouched.
        private static readonly byte[] FragPrefix = { 0x02, (byte)'B', (byte)'F', (byte)'R' };
        public const int ChunkSize = 400_000;   // header + chunk stays well under the 512KB cap

        public static int FragmentCount(int totalLen) => (totalLen + ChunkSize - 1) / ChunkSize;

        public static byte[] WrapFragment(int msgId, int index, int count, byte[] data, int offset, int len)
        {
            var f = new byte[12 + len];
            FragPrefix.CopyTo(f, 0);
            f[4] = (byte)msgId; f[5] = (byte)(msgId >> 8); f[6] = (byte)(msgId >> 16); f[7] = (byte)(msgId >> 24);
            f[8] = (byte)index; f[9] = (byte)(index >> 8);
            f[10] = (byte)count; f[11] = (byte)(count >> 8);
            Array.Copy(data, offset, f, 12, len);
            return f;
        }

        public static bool IsFragment(byte[] b, out int msgId, out int index, out int count, out byte[] chunk)
        {
            msgId = 0; index = 0; count = 0; chunk = Array.Empty<byte>();
            if (b == null || b.Length < 12) return false;
            for (int i = 0; i < 4; i++) if (b[i] != FragPrefix[i]) return false;
            msgId = b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24);
            index = b[8] | (b[9] << 8);
            count = b[10] | (b[11] << 8);
            chunk = new byte[b.Length - 12];
            Array.Copy(b, 12, chunk, 0, chunk.Length);
            return true;
        }
    }

    /// <summary>Per-connection reassembly of fragmented Steam messages.  Fed
    /// from the receive path (pump thread); lock-protected; stale assemblies
    /// (lost fragment / dead peer) pruned after 120s.</summary>
    internal sealed class SteamReassembly
    {
        private sealed class Entry { public byte[]?[] Parts = null!; public int Got; public int Bytes; public long AtMs; }
        private readonly System.Collections.Generic.Dictionary<int, Entry> _pending = new();
        private readonly string _tag;
        public SteamReassembly(string tag) { _tag = tag; }

        /// <summary>True when the frame WAS a fragment (consumed either way);
        /// complete is non-null once the full message is reassembled.</summary>
        public bool TryAccept(byte[] frame, out byte[]? complete)
        {
            complete = null;
            if (!SteamFrames.IsFragment(frame, out var id, out var idx, out var cnt, out var chunk)) return false;
            try
            {
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;   // net48: no Environment.TickCount64
                lock (_pending)
                {
                    System.Collections.Generic.List<int>? stale = null;
                    foreach (var kv in _pending) if (now - kv.Value.AtMs > 120_000) (stale ??= new()).Add(kv.Key);
                    if (stale != null)
                        foreach (var k in stale)
                        { _pending.Remove(k); Plugin.Logger.LogWarning($"[{_tag}] fragment assembly {k} timed out — dropped."); }

                    if (cnt <= 0 || cnt > 4096 || idx < 0 || idx >= cnt) return true;   // malformed — swallow
                    if (!_pending.TryGetValue(id, out var e))
                        _pending[id] = e = new Entry { Parts = new byte[cnt][], AtMs = now };
                    if (e.Parts.Length != cnt) { _pending.Remove(id); return true; }    // inconsistent — drop
                    if (e.Parts[idx] == null) { e.Parts[idx] = chunk; e.Got++; e.Bytes += chunk.Length; e.AtMs = now; }
                    if (e.Got < cnt) return true;

                    var full = new byte[e.Bytes];
                    int off = 0;
                    foreach (var p in e.Parts) { Array.Copy(p!, 0, full, off, p!.Length); off += p!.Length; }
                    _pending.Remove(id);
                    complete = full;
                    Plugin.Logger.LogInfo($"[{_tag}] reassembled large message: {cnt} fragment(s), {full.Length}B.");
                    return true;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[{_tag}] reassembly: {ex.Message}"); return true; }
        }
    }

    /// <summary>A relay-connected peer (host side).</summary>
    public sealed class SteamLink : MPLink
    {
        private readonly Connection _conn;
        private readonly int _id;
        private readonly string _who;
        internal volatile bool Alive = true;

        public SteamLink(Connection conn, int id, string who) { _conn = conn; _id = id; _who = who; }
        public override int Id => _id;
        public override bool IsAlive => Alive;
        public override string Describe => $"steam:{_who}";

        // Silent-loss fix (field 2026-07-19, new-game start burst): Facepunch's
        // SendMessage REFUSES messages by Result return — oversized or send-
        // buffer-full — WITHOUT throwing.  The original code ignored the return,
        // so the 826-building business snapshot vanished silently and the client
        // could never become world-ready.  Reliable sends now queue on refusal
        // and re-offer from the pump loop; while anything is pending, later
        // reliable sends queue behind it so delivery order is preserved.
        private readonly System.Collections.Generic.Queue<byte[]> _pending = new();
        private long _pendingBytes;
        // 64MB (2026-07-20 scaling review): the old 8MB cap was a rebirth of the
        // silent-loss cliff — a fragmented message bigger than the cap (or a join
        // burst sharing it) would drop MID-MESSAGE and never reassemble.  Mature
        // worlds project to 2-6MB snapshots; 64MB = ~15× headroom.  Transient
        // worst-case memory only while a connection is congested; cleared when
        // the link dies.  The 4MB high-water warn signals pressure far earlier
        // than the cap.
        private const long MaxPendingBytes  = 64L * 1024 * 1024;
        private const long HighWaterBytes   = 4L * 1024 * 1024;
        private const long HighWaterRearm   = 1L * 1024 * 1024;
        private bool _highWaterLatched;
        private int _refusalsLogged;

        private static int _nextFragId;
        internal readonly SteamReassembly Reassembly = new("SteamHost");

        public override void Send(byte[] data, bool reliable)
        {
            try
            {
                if (reliable)
                {
                    if (data.Length > SteamFrames.ChunkSize)
                    {
                        int id = Interlocked.Increment(ref _nextFragId);
                        int count = SteamFrames.FragmentCount(data.Length);
                        Plugin.Logger.LogInfo($"[SteamLink] fragmenting {data.Length}B → {count} chunk(s) for {Describe} (Steam 512KB cap).");
                        for (int i = 0, off = 0; i < count; i++)
                        {
                            int len = Math.Min(SteamFrames.ChunkSize, data.Length - off);
                            SendReliableRaw(SteamFrames.WrapFragment(id, i, count, data, off, len));
                            off += len;
                        }
                        return;
                    }
                    SendReliableRaw(data);
                }
                else
                {
                    var r = _conn.SendMessage(data, SendType.Unreliable);
                    if (r != Result.OK && _refusalsLogged++ < 8)
                        Plugin.Logger.LogWarning($"[SteamLink] unreliable send to {Describe} refused: {r} ({data.Length}B) — dropped (unreliable by contract).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamHost] send to {Describe}: {ex.Message}"); }
        }

        private void SendReliableRaw(byte[] data)
        {
            lock (_pending)
                if (_pending.Count > 0) { EnqueueLocked(data); return; }   // keep order behind pending
            var r = _conn.SendMessage(data, SendType.Reliable);
            if (r != Result.OK)
            {
                if (_refusalsLogged++ < 8)
                    Plugin.Logger.LogWarning($"[SteamLink] reliable send to {Describe} refused: {r} ({data.Length}B) — queued for retry.");
                lock (_pending) EnqueueLocked(data);
            }
        }

        private void EnqueueLocked(byte[] data)
        {
            if (_pendingBytes + data.Length > MaxPendingBytes)
            {
                if (_refusalsLogged++ < 8)
                    Plugin.Logger.LogError($"[SteamLink] pending-send cap for {Describe} ({_pendingBytes}B queued) — dropping {data.Length}B; connection is stalled beyond recovery.");
                return;
            }
            _pending.Enqueue(data); _pendingBytes += data.Length;
            if (!_highWaterLatched && _pendingBytes > HighWaterBytes)
            {
                _highWaterLatched = true;
                Plugin.Logger.LogWarning($"[SteamLink] send backlog high-water for {Describe}: {_pendingBytes / 1024}KB queued — connection is congested (delivery delayed, nothing lost).");
            }
        }

        /// <summary>Re-offer queued reliable sends.  Pump thread, ~15ms.</summary>
        internal void FlushPending()
        {
            if (_pending.Count == 0) return;
            lock (_pending)
            {
                if (!Alive) { _pending.Clear(); _pendingBytes = 0; return; }
                while (_pending.Count > 0)
                {
                    var d = _pending.Peek();
                    Result r;
                    try { r = _conn.SendMessage(d, SendType.Reliable); }
                    catch { return; }
                    if (r != Result.OK) return;   // still refused — next pump retries
                    _pending.Dequeue(); _pendingBytes -= d.Length;
                    Plugin.Logger.LogInfo($"[SteamLink] retry to {Describe} accepted ({d.Length}B, {_pending.Count} still pending).");
                    if (_highWaterLatched && _pendingBytes < HighWaterRearm)
                    {
                        _highWaterLatched = false;
                        Plugin.Logger.LogInfo($"[SteamLink] send backlog for {Describe} drained below {HighWaterRearm / 1024}KB — congestion cleared.");
                    }
                }
            }
        }

        public override void Disconnect(byte[] reason)
        {
            try
            {
                if (reason != null && reason.Length > 0)
                    _conn.SendMessage(SteamFrames.WrapClose(reason), SendType.Reliable);
                _conn.Close();
            }
            catch { }
        }
    }

    /// <summary>Host-side relay listener.  Runs BESIDE the UDP transport — link
    /// ids allocate from 1_000_000 so the int-keyed registries never collide
    /// with LiteNetLib peer ids (small non-negatives).</summary>
    public sealed class SteamHostTransport : IHostTransport, ISocketManager
    {
        private SocketManager? _socket;
        private Thread? _pumpThread;
        private volatile bool _running;
        private static int _nextId = 1_000_000;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SteamLink> _links = new();

        public event Action<MPLink>? PeerConnected;
        public event Action<MPLink, string>? PeerDisconnected;
        public event Action<MPLink, byte[]>? Received;

        /// <summary>port is ignored — relay sockets use virtual port 0.</summary>
        public bool Start(int port)
        {
            try
            {
                if (!SteamClient.IsValid)
                { Plugin.Logger.LogWarning("[SteamHost] Steam client not valid — relay listener OFF (UDP-only session)."); return false; }
                // Valve recommends kicking off SDR access early — it warms the relay
                // route asynchronously and is a no-op when already available.
                try { SteamNetworkingUtils.InitRelayNetworkAccess(); Plugin.Logger.LogInfo($"[SteamHost] relay network status: {SteamNetworkingUtils.Status}."); } catch { }
                _links.Clear();
                _socket = SteamNetworkingSockets.CreateRelaySocket(0, this);
                _running = true;
                _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "BAMP-SteamHost" };
                _pumpThread.Start();
                Plugin.Logger.LogInfo($"[SteamHost] relay listener up (id {SteamClient.SteamId}).");
                return true;
            }
            catch (Exception ex)
            { Plugin.Logger.LogWarning($"[SteamHost] start: {ex.Message} — relay listener OFF."); _socket = null; return false; }
        }

        public void Stop()
        {
            _running = false;
            try { _socket?.Close(); } catch { }
            if (_pumpThread != null && _pumpThread != Thread.CurrentThread) _pumpThread.Join(1000);
            _socket = null;
            _links.Clear();
        }

        private void PumpLoop()
        {
            while (_running)
            {
                try { _socket?.Receive(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SteamHost] Receive: {ex}"); }
                try { foreach (var l in _links.Values) l.FlushPending(); } catch { }
                Thread.Sleep(15);
            }
        }

        // ── ISocketManager (state callbacks: game main thread; messages: pump) ──
        public void OnConnecting(Connection connection, ConnectionInfo info)
        {
            Plugin.Logger.LogInfo($"[SteamHost] connecting: {info.Identity} (peers={_links.Count}).");
            connection.Accept();
        }

        public void OnConnected(Connection connection, ConnectionInfo info)
        {
            var link = new SteamLink(connection, Interlocked.Increment(ref _nextId), info.Identity.ToString());
            _links[connection.Id] = link;
            Plugin.Logger.LogInfo($"[SteamHost] connected: {link.Describe} → link {link.Id}.");
            PeerConnected?.Invoke(link);
        }

        public void OnDisconnected(Connection connection, ConnectionInfo info)
        {
            if (_links.TryRemove(connection.Id, out var link))
            {
                link.Alive = false;
                PeerDisconnected?.Invoke(link, info.EndReason.ToString());
            }
        }

        public void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            if (!_links.TryGetValue(connection.Id, out var link)) return;
            var bytes = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, size);
            if (link.Reassembly.TryAccept(bytes, out var full))
            {
                if (full != null) Received?.Invoke(link, full);
                return;
            }
            Received?.Invoke(link, bytes);
        }
    }

    /// <summary>Client-side relay connection to a host SteamId.</summary>
    public sealed class SteamClientTransport : IClientTransport, IConnectionManager
    {
        private ConnectionManager? _mgr;
        private Thread? _pumpThread;
        private volatile bool _running;
        private byte[] _closeTag = Array.Empty<byte>();   // stashed BAMP:... control frame

        public event Action? Connected;
        public event Action<string, byte[]>? Disconnected;
        public event Action<byte[]>? Received;

        public bool IsRunning => _running;

        public bool Connect(SteamId hostId)
        {
            try
            {
                if (!SteamClient.IsValid)
                { Plugin.Logger.LogWarning("[SteamClient] Steam client not valid — cannot relay-connect."); return false; }
                try { SteamNetworkingUtils.InitRelayNetworkAccess(); Plugin.Logger.LogInfo($"[SteamClient] relay network status: {SteamNetworkingUtils.Status}."); } catch { }
                _closeTag = Array.Empty<byte>();
                _mgr = SteamNetworkingSockets.ConnectRelay(hostId, 0, this);
                _running = true;
                _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "BAMP-SteamClient" };
                _pumpThread.Start();
                Plugin.Logger.LogInfo($"[SteamClient] relay connect → {hostId}...");
                return true;
            }
            catch (Exception ex)
            { Plugin.Logger.LogWarning($"[SteamClient] connect: {ex.Message}"); _mgr = null; return false; }
        }

        public void StopPolling() => _running = false;

        public void Disconnect()
        {
            _running = false;
            try { _mgr?.Close(); } catch { }
            if (_pumpThread != null && _pumpThread != Thread.CurrentThread) _pumpThread.Join(1000);
            _mgr = null;
        }

        // Same silent-loss fix as SteamLink (field 2026-07-19): check the send
        // Result; queue refused reliable sends and re-offer from the pump loop.
        private readonly System.Collections.Generic.Queue<byte[]> _pending = new();
        private long _pendingBytes;
        // 64MB (2026-07-20 scaling review): the old 8MB cap was a rebirth of the
        // silent-loss cliff — a fragmented message bigger than the cap (or a join
        // burst sharing it) would drop MID-MESSAGE and never reassemble.  Mature
        // worlds project to 2-6MB snapshots; 64MB = ~15× headroom.  Transient
        // worst-case memory only while a connection is congested; cleared when
        // the link dies.  The 4MB high-water warn signals pressure far earlier
        // than the cap.
        private const long MaxPendingBytes  = 64L * 1024 * 1024;
        private const long HighWaterBytes   = 4L * 1024 * 1024;
        private const long HighWaterRearm   = 1L * 1024 * 1024;
        private bool _highWaterLatched;
        private int _refusalsLogged;

        private static int _nextFragId;
        private readonly SteamReassembly _reassembly = new("SteamClient");

        public void Send(byte[] data, bool reliable)
        {
            try
            {
                var mgr = _mgr; if (mgr == null) return;
                if (reliable)
                {
                    if (data.Length > SteamFrames.ChunkSize)
                    {
                        int id = Interlocked.Increment(ref _nextFragId);
                        int count = SteamFrames.FragmentCount(data.Length);
                        Plugin.Logger.LogInfo($"[SteamClient] fragmenting {data.Length}B → {count} chunk(s) (Steam 512KB cap).");
                        for (int i = 0, off = 0; i < count; i++)
                        {
                            int len = Math.Min(SteamFrames.ChunkSize, data.Length - off);
                            SendReliableRaw(mgr, SteamFrames.WrapFragment(id, i, count, data, off, len));
                            off += len;
                        }
                        return;
                    }
                    SendReliableRaw(mgr, data);
                }
                else
                {
                    var r = mgr.Connection.SendMessage(data, SendType.Unreliable);
                    if (r != Result.OK && _refusalsLogged++ < 8)
                        Plugin.Logger.LogWarning($"[SteamClient] unreliable send refused: {r} ({data.Length}B) — dropped (unreliable by contract).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamClient] send: {ex.Message}"); }
        }

        private void SendReliableRaw(ConnectionManager mgr, byte[] data)
        {
            lock (_pending)
                if (_pending.Count > 0) { EnqueueLocked(data); return; }   // keep order behind pending
            var r = mgr.Connection.SendMessage(data, SendType.Reliable);
            if (r != Result.OK)
            {
                if (_refusalsLogged++ < 8)
                    Plugin.Logger.LogWarning($"[SteamClient] reliable send refused: {r} ({data.Length}B) — queued for retry.");
                lock (_pending) EnqueueLocked(data);
            }
        }

        private void EnqueueLocked(byte[] data)
        {
            if (_pendingBytes + data.Length > MaxPendingBytes)
            {
                if (_refusalsLogged++ < 8)
                    Plugin.Logger.LogError($"[SteamClient] pending-send cap ({_pendingBytes}B queued) — dropping {data.Length}B; connection is stalled beyond recovery.");
                return;
            }
            _pending.Enqueue(data); _pendingBytes += data.Length;
            if (!_highWaterLatched && _pendingBytes > HighWaterBytes)
            {
                _highWaterLatched = true;
                Plugin.Logger.LogWarning($"[SteamClient] send backlog high-water: {_pendingBytes / 1024}KB queued — connection is congested (delivery delayed, nothing lost).");
            }
        }

        private void FlushPending()
        {
            if (_pending.Count == 0) return;
            var mgr = _mgr; if (mgr == null) return;
            lock (_pending)
            {
                while (_pending.Count > 0)
                {
                    var d = _pending.Peek();
                    Result r;
                    try { r = mgr.Connection.SendMessage(d, SendType.Reliable); }
                    catch { return; }
                    if (r != Result.OK) return;   // still refused — next pump retries
                    _pending.Dequeue(); _pendingBytes -= d.Length;
                    Plugin.Logger.LogInfo($"[SteamClient] retry accepted ({d.Length}B, {_pending.Count} still pending).");
                    if (_highWaterLatched && _pendingBytes < HighWaterRearm)
                    {
                        _highWaterLatched = false;
                        Plugin.Logger.LogInfo($"[SteamClient] send backlog drained below {HighWaterRearm / 1024}KB — congestion cleared.");
                    }
                }
            }
        }

        private void PumpLoop()
        {
            while (_running)
            {
                try { _mgr?.Receive(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SteamClient] Receive: {ex}"); }
                try { FlushPending(); } catch { }
                Thread.Sleep(15);
            }
        }

        // ── IConnectionManager ────────────────────────────────────────────────
        public void OnConnecting(ConnectionInfo info) { }

        public void OnConnected(ConnectionInfo info)
        {
            Plugin.Logger.LogInfo("[SteamClient] relay connected.");
            Connected?.Invoke();
        }

        public void OnDisconnected(ConnectionInfo info)
        {
            Disconnected?.Invoke(info.EndReason.ToString(), _closeTag);
        }

        public void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            var bytes = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, size);
            if (SteamFrames.IsClose(bytes, out var tag)) { _closeTag = tag; return; }   // refusal tag — arrives just before the close
            if (_reassembly.TryAccept(bytes, out var full))
            {
                if (full != null) Received?.Invoke(full);
                return;
            }
            Received?.Invoke(bytes);
        }
    }
}
