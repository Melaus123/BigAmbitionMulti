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

    /// <summary>T3 (2026-08 throughput audit #2): SteamNetworkingSockets supports multiple
    /// independently-ordered outbound streams per connection ("lanes") with priorities — exactly what
    /// the hand-built paced/express queues approximate, done in the transport where it works. Three
    /// lanes: EXPRESS (clock/pause — must overtake everything), GAMEPLAY (default), BULK (the
    /// snapshot/save megabyte class — may be overtaken by everything). Messages within a lane stay
    /// ordered; a 700 KB market table on BULK no longer stalls positions and register events behind
    /// it. Lane routing keys on the envelope's six-byte type peek (works for T1 frames too). If
    /// ConfigureConnectionLanes is unavailable, everything stays on lane 0 — exactly today's wire.</summary>
    internal static class SteamLanes
    {
        public const ushort Express = 0, Gameplay = 1, Bulk = 2;
        public static readonly int[]    Priorities = { 0, 1, 2 };   // lower = more urgent
        public static readonly ushort[] Weights    = { 1, 1, 1 };

        public static ushort For(byte[] envelope)
        {
            switch (MPNetStats.PeekType(envelope))
            {
                case (int)MessageType.Welcome:                 // full world snapshot incl. the market table
                case (int)MessageType.MarketSnapshot:
                case (int)MessageType.BusinessSnapshot:
                case (int)MessageType.InteriorSnapshot:
                case (int)MessageType.InteriorOwnerSnapshot:
                case (int)MessageType.SaveData:
                case (int)MessageType.LoadData:
                case (int)MessageType.StoreMirror:
                // Review M5 — the ORDERING invariant: any type that writes state a Bulk snapshot also
                // writes must SHARE the snapshot's lane, or an older snapshot can overtake-revert a
                // newer delta (BusinessChange had no version guard and no re-assert path).
                case (int)MessageType.BusinessChange:
                case (int)MessageType.InteriorCargoSync:
                case (int)MessageType.RadioState:
                // Review M6 — the megabyte class that was missed: a rejoiner's disconnect-save upload
                // and gzipped log replies must not head-of-line-block their own gameplay lane.
                case (int)MessageType.ClientDisconnectUpload:
                case (int)MessageType.PeerLogReply:
                case (int)MessageType.BuildingInteriorEdit:
                case (int)MessageType.AuditDrillReply:
                    return Bulk;
                default:
                    return Gameplay;
            }
        }
    }

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

        // ── Round-282 (mirror pacing) ────────────────────────────────────────
        // The paced lane uses a SMALLER fragment than the immediate lane, because
        // the paced chunk is the unit of head-of-line blocking: whatever size we
        // pick is what an urgent gameplay message can end up waiting behind.
        // 192KB ≈ 0.6-0.8s on the 250-330KB/s links measured in field
        // 20260818-215459, against 400KB ≈ 1.3-1.6s.
        //
        // Verified before choosing it (SteamReassembly, this file): reassembly is
        // INDEX/COUNT based — it sizes the output from the SUM of the received
        // chunk lengths and concatenates the parts in index order.  There is no
        // offset = index × ChunkSize assumption anywhere, so a message fragmented
        // at 192KB reassembles on an unmodified receiver.  Its guards still hold:
        // count ≤ 4096 (4096 × 192KB = 786MB of headroom) and the 16-bit index.
        public const int PacedChunkSize = 192 * 1024;

        public static int FragmentCount(int totalLen) => (totalLen + ChunkSize - 1) / ChunkSize;

        /// <summary>Round-282: pre-split a paced payload into the frames the pump will
        /// release one at a time.  A payload at or under PacedChunkSize rides as ONE
        /// unwrapped message (still headroom-gated) — exactly what the immediate lane
        /// does with a small message, so the receiver sees nothing new.</summary>
        public static byte[][] BuildPacedFrames(int msgId, byte[] data)
        {
            if (data.Length <= PacedChunkSize) return new[] { data };
            int count = (data.Length + PacedChunkSize - 1) / PacedChunkSize;
            var frames = new byte[count][];
            for (int i = 0, off = 0; i < count; i++)
            {
                int len = Math.Min(PacedChunkSize, data.Length - off);
                frames[i] = WrapFragment(msgId, i, count, data, off, len);
                off += len;
            }
            return frames;
        }

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

    /// <summary>Round-270 (join progress, field 20260816-112127 "friend cannot join" =
    /// players cancelling silent multi-MB relay downloads): live snapshot of the newest
    /// in-flight fragment assembly, read by the join UI and the progress reporter.
    /// Written from the receive pump, read from the main thread — plain volatile fields,
    /// display-grade freshness only.</summary>
    internal static class SteamXferProgress
    {
        public static volatile int Got, Cnt;
        public static volatile int KBytes;
        public static long AtMs;
        public static string Tag = "";
        public static void Report(string tag, int got, int cnt, int bytes)
        { Tag = tag; Got = got; Cnt = cnt; KBytes = bytes / 1024; AtMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }
        public static void Done() { Got = 0; Cnt = 0; }
        public static bool ActiveWithin(int ms)
            => Cnt > 0 && (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) - AtMs < ms;
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
                    SteamXferProgress.Report(_tag, e.Got, cnt, e.Bytes);   // round-270: live download progress
                    if (e.Got < cnt) return true;

                    var full = new byte[e.Bytes];
                    int off = 0;
                    foreach (var p in e.Parts) { Array.Copy(p!, 0, full, off, p!.Length); off += p!.Length; }
                    _pending.Remove(id);
                    complete = full;
                    SteamXferProgress.Done();   // round-270: assembly finished — UI falls back to "loading"
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

        private bool _lanesOk;   // T3: false → every send stays on lane 0 (today's wire, unchanged)

        public SteamLink(Connection conn, int id, string who)
        {
            _conn = conn; _id = id; _who = who;
            try { _lanesOk = _conn.ConfigureConnectionLanes(SteamLanes.Priorities, SteamLanes.Weights) == Result.OK; }
            catch { _lanesOk = false; }
            if (!_lanesOk) Plugin.Logger.LogWarning($"[SteamLink] connection lanes unavailable for {who} — single-lane sends (pre-T3 behaviour).");
        }
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
        private readonly System.Collections.Generic.Queue<(byte[] d, ushort lane)> _pending = new();   // T3: retries keep their lane
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
        // Round-88 hygiene (user-approved): per-message retry lines flooded the bug-report ring to a
        // 19-second window during congestion (field 20260725-000353: 78-deep backlog to one peer).
        // Summarize to at most one line per 5s. Ticks-based — the pump runs OFF the main thread,
        // where UnityEngine.Time is unavailable.
        private long _retryLogNextTicks;
        private int  _retrySummed;
        private long _retrySummedBytes;

        private static int _nextFragId;
        internal readonly SteamReassembly Reassembly = new("SteamHost");

        // Round-276 congestion signal; round-282: transport backlog ONLY — MPLink adds
        // the paced lane on top for PendingSendBytes, and this figure is the pacing gate.
        // T0 (2026-08 audit #7): _pendingBytes alone is bytes STEAM REFUSED, which stays ~0 until
        // Steam's own 512 KB send buffer is full — so the gate engaged half a megabyte too late and
        // the join verifier under-read congestion the same way. PendingReliable (queued inside Steam,
        // not yet sent) is the missing half. SentUnackedReliable stays excluded on purpose — on a
        // high-RTT relay it is just the bandwidth-delay product and blocks nothing (round-282b note).
        protected override long TransportPendingBytes
        {
            get
            {
                long ours; lock (_pending) ours = _pendingBytes;
                try { return ours + _conn.QuickStatus().PendingReliable; }
                catch { return ours; }
            }
        }

        // ── Round-282: the paced lane (host → client store mirrors) ──────────
        private readonly PacedSendQueue _paced = new("SteamLink");
        public override long PacedSendBytes => _paced.Bytes;

        // ── Round-283: the express lane (host → client game-clock sync) ──────
        // Deliberately NOT counted in PendingSendBytes/TransportPendingBytes.  That figure
        // is the CONGESTION signal the round-276 phase probe prints as outQ and the
        // join-baseline verifier extends its window on — it means "bulk this peer is still
        // waiting on".  Express bytes are by construction ahead of all of it, are two small
        // messages at most, and folding them in would move a number other code makes
        // decisions from for no diagnostic gain.
        private readonly ExpressSendQueue _express = new("SteamLink");
        private int _expressRefusalsLogged;   // its own budget — must not eat the bulk path's 8 lines

        public override void SendExpress(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            // NEVER fragment express.  A fragmented express message would interleave with
            // itself against the bulk lane on the wire and could only reassemble by luck;
            // worse, it would put ~400KB of "urgent" in front of the very traffic the lane
            // exists to protect.  A payload this size is not urgent by definition — it is
            // bulk that reached the wrong call site, which is a bug worth an ERROR line.
            if (data.Length > SteamFrames.ChunkSize)
            {
                Plugin.Logger.LogError($"[SteamLink] express payload for {Describe} is {data.Length}B — over the "
                    + $"{SteamFrames.ChunkSize}B express cap; sent on the ORDERED lane instead.  Express is for small "
                    + $"urgent signals only (round-283 whitelist); a payload this large does not belong on it.");
                Send(data, reliable: true);
                return;
            }
            MPNetStats.NoteOut(data);   // review M7: express counted at hand-off (retries not recounted)
            long bulk = 0;
            try { bulk = PendingSendBytes; } catch { }   // read OUTSIDE the express lock (it takes _pending)
            _express.Send(data, TrySendExpressRaw, bulk, Describe);
        }

        /// <summary>The express wire call: straight to Steam, deliberately NOT through
        /// SendReliableRaw — that method's whole job is to keep order behind _pending, which is
        /// exactly the queue an express message must overtake.  Returns true only on Result.OK;
        /// anything else leaves the payload in the express queue for the pump.</summary>
        private bool TrySendExpressRaw(byte[] d)
        {
            Result r;
            try { r = _conn.SendMessage(d, SendType.Reliable, _lanesOk ? SteamLanes.Express : (ushort)0); }
            catch { return false; }
            if (r == Result.OK) return true;
            if (_expressRefusalsLogged++ < 8)
                Plugin.Logger.LogWarning($"[SteamLink] express send to {Describe} refused: {r} ({d.Length}B) — "
                    + $"held in the express queue, which the pump drains before the retry and paced lanes.");
            return false;
        }

        /// <summary>Round-283: drain the express lane.  Pump thread, ~15ms, BEFORE FlushPending and
        /// BEFORE FlushPaced — a queued express message must still be ahead of every bulk byte this
        /// link owes, or the lane would only help on the first (unrefused) send.</summary>
        internal void FlushExpress()
        {
            if (!Alive) { _express.Clear(Describe); return; }
            _express.Flush(TrySendExpressRaw, Describe);
        }

        /// <summary>Round-282b: the true outbound figure — our own queues PLUS what
        /// Steam itself still holds for this connection.  Connection.QuickStatus()
        /// (verified against the shipped Facepunch.Steamworks.Win64.dll) exposes
        /// PendingReliable (queued in Steam, not yet sent) and SentUnackedReliable
        /// (sent, not yet acknowledged by the peer).  Counting BOTH is what makes the
        /// quit drain a real answer to "did the farewell mirror leave?": zero here
        /// means the peer has acknowledged every reliable byte, not merely that our
        /// process handed them over.  A closed/invalid connection reads as zero, which
        /// is the correct answer — nothing more can be sent down it.
        /// NOT wired into TransportPendingBytes on purpose: the pacing gate must not
        /// stall on SentUnackedReliable, which on a high-RTT relay is simply the
        /// bandwidth-delay product and blocks nothing that is queued behind it.</summary>
        public override long UnflushedSendBytes
        {
            get
            {
                // Review MIN-1: TransportPendingBytes now folds PendingReliable in (T0) — build from the
                // raw refused-bytes figure so the drain number is not double-counted.
                long ours; lock (_pending) ours = _pendingBytes;
                try { var s = _conn.QuickStatus(); return ours + PacedSendBytes + s.PendingReliable + s.SentUnackedReliable; }
                catch { return ours + PacedSendBytes; }
            }
        }

        /// <summary>Round-282b: the flushing close.  Disconnect(reason) already takes
        /// the round-91 linger path — Close(linger: true, 1000, tag) — which asks Steam
        /// to flush queued reliable data before the connection goes away; a bare Close()
        /// discards it.  Marking the link dead afterwards stops the pump from re-offering
        /// into a closing connection (and lets it release both queues).</summary>
        public override bool CloseFlushing(byte[] reason)
        {
            try { Disconnect(reason); Alive = false; return true; }
            catch (Exception ex)
            { Plugin.Logger.LogWarning($"[SteamLink] flushing close for {Describe}: {ex.Message}"); return false; }
        }

        public override void SendPaced(byte[] data, string supersedeKey = "")
        {
            if (data == null || data.Length == 0) return;
            try
            {
                // Fragment ids come from the SAME counter the immediate lane uses, so a
                // paced assembly can never collide with an immediate one on the receiver.
                int id = Interlocked.Increment(ref _nextFragId);
                _paced.Enqueue(SteamFrames.BuildPacedFrames(id, data), supersedeKey, Describe);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SteamLink] paced queue for {Describe}: {ex.Message} — sending immediately instead.");
                Send(data, reliable: true);
            }
        }

        /// <summary>Round-282: release at most ONE paced chunk per pump tick, and only
        /// while this link's own backlog is under the headroom — so at most about one
        /// paced chunk ever sits in front of an urgent gameplay message.  Pump thread,
        /// ~15ms, immediately after FlushPending (the gate must read a post-flush
        /// backlog or it under-releases for one tick after every drain).</summary>
        internal void FlushPaced()
        {
            if (!Alive) { _paced.Clear(); return; }
            var chunk = _paced.TryRelease(TransportPendingBytes, Describe);
            if (chunk == null) return;
            MPNetStats.NoteOutAs((int)MessageType.StoreMirror, chunk.Length);   // review M7: fragment heads are unpeekable; the lane carries only StoreMirror
            try { SendReliableRaw(chunk, SteamLanes.Bulk); }   // T3: paced mirrors are bulk by definition
            catch (Exception ex)
            {
                // A lost chunk means the receiver's assembly never completes and expires
                // after 120s.  Background replication: the next save re-mirrors the file.
                Plugin.Logger.LogWarning($"[SteamLink] paced chunk to {Describe} lost: {ex.Message} — that mirror will not reassemble; the next save re-mirrors.");
            }
        }

        public override void Send(byte[] data, bool reliable)
        {
            MPNetStats.NoteOut(data);   // T0: every wire send counts once, per recipient

            try
            {
                if (reliable)
                {
                    ushort lane = _lanesOk ? SteamLanes.For(data) : (ushort)0;   // T3: from the ORIGINAL envelope
                    if (data.Length > SteamFrames.ChunkSize)
                    {
                        int id = Interlocked.Increment(ref _nextFragId);
                        int count = SteamFrames.FragmentCount(data.Length);
                        Plugin.Logger.LogInfo($"[SteamLink] fragmenting {data.Length}B → {count} chunk(s) for {Describe} (Steam 512KB cap).");
                        for (int i = 0, off = 0; i < count; i++)
                        {
                            int len = Math.Min(SteamFrames.ChunkSize, data.Length - off);
                            SendReliableRaw(SteamFrames.WrapFragment(id, i, count, data, off, len), lane);
                            off += len;
                        }
                        return;
                    }
                    SendReliableRaw(data, lane);
                }
                else
                {
                    var r = _conn.SendMessage(data, SendType.Unreliable, _lanesOk ? SteamLanes.Gameplay : (ushort)0);   // review MIN-2: never the express lane by default
                    if (r != Result.OK && _refusalsLogged++ < 8)
                        Plugin.Logger.LogWarning($"[SteamLink] unreliable send to {Describe} refused: {r} ({data.Length}B) — dropped (unreliable by contract).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamHost] send to {Describe}: {ex.Message}"); }
        }

        private void SendReliableRaw(byte[] data, ushort lane = SteamLanes.Gameplay)
        {
            lock (_pending)
                if (_pending.Count > 0) { EnqueueLocked(data, lane); return; }   // keep order behind pending
            var r = _conn.SendMessage(data, SendType.Reliable, _lanesOk ? lane : (ushort)0);
            if (r != Result.OK)
            {
                if (_refusalsLogged++ < 8)
                    Plugin.Logger.LogWarning($"[SteamLink] reliable send to {Describe} refused: {r} ({data.Length}B) — queued for retry.");
                lock (_pending) EnqueueLocked(data, lane);
            }
        }

        private void EnqueueLocked(byte[] data, ushort lane)
        {
            if (_pendingBytes + data.Length > MaxPendingBytes)
            {
                if (_refusalsLogged++ < 8)
                    Plugin.Logger.LogError($"[SteamLink] pending-send cap for {Describe} ({_pendingBytes}B queued) — dropping {data.Length}B; connection is stalled beyond recovery.");
                return;
            }
            _pending.Enqueue((data, lane)); _pendingBytes += data.Length;
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
                    var (d, lane) = _pending.Peek();
                    Result r;
                    try { r = _conn.SendMessage(d, SendType.Reliable, _lanesOk ? lane : (ushort)0); }
                    catch { return; }
                    if (r != Result.OK) return;   // still refused — next pump retries
                    _pending.Dequeue(); _pendingBytes -= d.Length;
                    _retrySummed++; _retrySummedBytes += d.Length;
                    if (System.DateTime.UtcNow.Ticks >= _retryLogNextTicks)
                    {
                        _retryLogNextTicks = System.DateTime.UtcNow.Ticks + System.TimeSpan.TicksPerSecond * 5;
                        Plugin.Logger.LogInfo($"[SteamLink] retries to {Describe}: {_retrySummed} msg(s)/{_retrySummedBytes}B accepted, {_pending.Count} still pending.");
                        _retrySummed = 0; _retrySummedBytes = 0;
                    }
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
                {
                    // Round-282c (verifier): the tag frame's Result was ignored — on a congested
                    // link (exactly where the quit drain ends in STOPPED/TIMEOUT) Steam can refuse
                    // it and the tag vanished silently.  The linger close still carries the tag in
                    // its debug string, so the player message usually survives; the log now says
                    // when the frame itself was lost.
                    var tagResult = _conn.SendMessage(SteamFrames.WrapClose(reason), SendType.Reliable, _lanesOk ? SteamLanes.Gameplay : (ushort)0);   // review MIN-2
                    if (tagResult != Result.OK)
                        Plugin.Logger.LogWarning($"[SteamLink] close-tag frame to {Describe} refused ({tagResult}) — relying on the close debug-string fallback (round-282c).");
                    // Round-91 (field 20260725-165954: a relay version-refusal reached the player as a
                    // bare 'App_Min' and a wordless bounce to the menu): Close() WITHOUT linger discards
                    // queued reliable data, so the close-tag frame above lost the race on the relay and
                    // every refusal (version/kick/ban) arrived mute. linger=true flushes the frame first;
                    // the tag also rides the close debug-string as belt-and-suspenders.
                    string tag = ""; try { tag = System.Text.Encoding.UTF8.GetString(reason); } catch { }
                    _conn.Close(true, 1000, tag);
                    return;
                }
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
                // Round-283: express FIRST — the lane is worthless if a refused express
                // message has to wait for the retry queue it was meant to overtake.
                // Round-282: FlushPending next (retries own the backlog figure the
                // paced gate reads), then release at most one paced chunk per link.
                // Per-link isolation: one sick peer must not stop the others draining.
                foreach (var l in _links.Values)
                {
                    try { l.FlushExpress(); l.FlushPending(); l.FlushPaced(); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamHost] flush {l.Describe}: {ex.Message}"); }
                }
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
        private readonly System.Collections.Generic.Queue<(byte[] d, ushort lane)> _pending = new();   // T3: retries keep their lane
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
        // Round-88 hygiene (user-approved): per-message retry lines flooded the bug-report ring to a
        // 19-second window during congestion (field 20260725-000353: 78-deep backlog to one peer).
        // Summarize to at most one line per 5s. Ticks-based — the pump runs OFF the main thread,
        // where UnityEngine.Time is unavailable.
        private long _retryLogNextTicks;
        private int  _retrySummed;
        private long _retrySummedBytes;

        private static int _nextFragId;
        private readonly SteamReassembly _reassembly = new("SteamClient");

        // ── Round-282: the paced lane, client side ───────────────────────────
        // Same queue, same headroom gate, same pump as the host link.  No v1 caller:
        // the client's save upload stays IMMEDIATE by the round-282 scope decision
        // (the host is waiting on it to complete a coordinated save — that is not
        // background replication).  The lane exists so the handoff work can meter
        // client→host bulk without reopening the transport layer.
        private readonly PacedSendQueue _paced = new("SteamClient");
        public long PacedSendBytes => _paced.Bytes;

        // ── Round-283: the express lane, client side (phase reports → host) ──
        // Same queue, same discipline, same pump position as SteamLink.  Its one v1 caller
        // is MPClient.SendPhaseReport, and only when the host advertised the capability
        // (LobbyUpdatePayload.HostExpress) — a host without the Seq guard must keep
        // receiving these strictly in order, which is today's behaviour.
        private readonly ExpressSendQueue _express = new("SteamClient");
        private int _expressRefusalsLogged;   // its own budget — must not eat the bulk path's 8 lines

        public void SendExpress(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            // Never fragment express — see SteamLink.SendExpress for the reasoning.
            if (data.Length > SteamFrames.ChunkSize)
            {
                Plugin.Logger.LogError($"[SteamClient] express payload is {data.Length}B — over the "
                    + $"{SteamFrames.ChunkSize}B express cap; sent on the ORDERED lane instead.  Express is for small "
                    + $"urgent signals only (round-283 whitelist); a payload this large does not belong on it.");
                Send(data, reliable: true);
                return;
            }
            long bulk; lock (_pending) bulk = _pendingBytes;
            bulk += _paced.Bytes;                       // the whole bulk debt this send goes ahead of
            _express.Send(data, TrySendExpressRaw, bulk, "host");
        }

        /// <summary>Straight to Steam, deliberately NOT through SendReliableRaw — that method keeps
        /// order behind _pending, which is the queue express exists to overtake.</summary>
        private bool TrySendExpressRaw(byte[] d)
        {
            var mgr = _mgr; if (mgr == null) return false;
            Result r;
            try { r = mgr.Connection.SendMessage(d, SendType.Reliable); }
            catch { return false; }
            if (r == Result.OK) return true;
            if (_expressRefusalsLogged++ < 8)
                Plugin.Logger.LogWarning($"[SteamClient] express send refused: {r} ({d.Length}B) — held in the "
                    + $"express queue, which the pump drains before the retry and paced lanes.");
            return false;
        }

        /// <summary>Round-283: drain the express lane.  Pump thread, ~15ms, before FlushPending and
        /// FlushPaced (see SteamLink.FlushExpress).</summary>
        private void FlushExpress()
        {
            if (_mgr == null) { _express.Clear("host"); return; }
            _express.Flush(TrySendExpressRaw, "host");
        }
        // (round-282c: an unused PendingSendBytes aggregate lived here — not on
        // IClientTransport, no reader; removed rather than left as drift surface.)

        public void SendPaced(byte[] data, string supersedeKey = "")
        {
            if (data == null || data.Length == 0) return;
            try
            {
                int id = Interlocked.Increment(ref _nextFragId);
                _paced.Enqueue(SteamFrames.BuildPacedFrames(id, data), supersedeKey, "host");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SteamClient] paced queue: {ex.Message} — sending immediately instead.");
                Send(data, reliable: true);
            }
        }

        /// <summary>Round-282: release at most one paced chunk per pump tick, gated on
        /// our own send backlog (NOT PendingSendBytes — that includes the paced queue
        /// and would gate the queue against itself).  Pump thread, ~15ms.</summary>
        private void FlushPaced()
        {
            var mgr = _mgr;
            if (mgr == null) { _paced.Clear(); return; }
            long backlog; lock (_pending) backlog = _pendingBytes;
            try { backlog += mgr.Connection.QuickStatus().PendingReliable; } catch { }   // review MIN-10: T0 sensor parity with the host gate
            var chunk = _paced.TryRelease(backlog, "host");
            if (chunk == null) return;
            MPNetStats.NoteOutAs((int)MessageType.StoreMirror, chunk.Length);   // review M7
            try { SendReliableRaw(mgr, chunk, SteamLanes.Bulk); }   // T3: paced mirrors are bulk by definition
            catch (Exception ex)
            { Plugin.Logger.LogWarning($"[SteamClient] paced chunk lost: {ex.Message} — that payload will not reassemble on the host."); }
        }

        public void Send(byte[] data, bool reliable)
        {
            MPNetStats.NoteOut(data);   // T0: every wire send counts once, per recipient

            try
            {
                var mgr = _mgr; if (mgr == null) return;
                if (reliable)
                {
                    ushort lane = _lanesOk ? SteamLanes.For(data) : (ushort)0;   // T3: from the ORIGINAL envelope
                    if (data.Length > SteamFrames.ChunkSize)
                    {
                        int id = Interlocked.Increment(ref _nextFragId);
                        int count = SteamFrames.FragmentCount(data.Length);
                        Plugin.Logger.LogInfo($"[SteamClient] fragmenting {data.Length}B → {count} chunk(s) (Steam 512KB cap).");
                        for (int i = 0, off = 0; i < count; i++)
                        {
                            int len = Math.Min(SteamFrames.ChunkSize, data.Length - off);
                            SendReliableRaw(mgr, SteamFrames.WrapFragment(id, i, count, data, off, len), lane);
                            off += len;
                        }
                        return;
                    }
                    SendReliableRaw(mgr, data, lane);
                }
                else
                {
                    var r = mgr.Connection.SendMessage(data, SendType.Unreliable, _lanesOk ? SteamLanes.Gameplay : (ushort)0);   // review MIN-2: never the express lane by default
                    if (r != Result.OK && _refusalsLogged++ < 8)
                        Plugin.Logger.LogWarning($"[SteamClient] unreliable send refused: {r} ({data.Length}B) — dropped (unreliable by contract).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamClient] send: {ex.Message}"); }
        }

        private bool _lanesOk;   // T3: false → every send stays on lane 0 (today's wire, unchanged)

        private void SendReliableRaw(ConnectionManager mgr, byte[] data, ushort lane = SteamLanes.Gameplay)
        {
            lock (_pending)
                if (_pending.Count > 0) { EnqueueLocked(data, lane); return; }   // keep order behind pending
            var r = mgr.Connection.SendMessage(data, SendType.Reliable, _lanesOk ? lane : (ushort)0);
            if (r != Result.OK)
            {
                if (_refusalsLogged++ < 8)
                    Plugin.Logger.LogWarning($"[SteamClient] reliable send refused: {r} ({data.Length}B) — queued for retry.");
                lock (_pending) EnqueueLocked(data, lane);
            }
        }

        private void EnqueueLocked(byte[] data, ushort lane)
        {
            if (_pendingBytes + data.Length > MaxPendingBytes)
            {
                if (_refusalsLogged++ < 8)
                    Plugin.Logger.LogError($"[SteamClient] pending-send cap ({_pendingBytes}B queued) — dropping {data.Length}B; connection is stalled beyond recovery.");
                return;
            }
            _pending.Enqueue((data, lane)); _pendingBytes += data.Length;
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
                    var (d, lane) = _pending.Peek();
                    Result r;
                    try { r = mgr.Connection.SendMessage(d, SendType.Reliable, _lanesOk ? lane : (ushort)0); }
                    catch { return; }
                    if (r != Result.OK) return;   // still refused — next pump retries
                    _pending.Dequeue(); _pendingBytes -= d.Length;
                    _retrySummed++; _retrySummedBytes += d.Length;
                    if (System.DateTime.UtcNow.Ticks >= _retryLogNextTicks)
                    {
                        _retryLogNextTicks = System.DateTime.UtcNow.Ticks + System.TimeSpan.TicksPerSecond * 5;
                        Plugin.Logger.LogInfo($"[SteamClient] retries: {_retrySummed} msg(s)/{_retrySummedBytes}B accepted, {_pending.Count} still pending.");
                        _retrySummed = 0; _retrySummedBytes = 0;
                    }
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
                try { FlushExpress(); } catch { }  // round-283: express lane FIRST — it must overtake the retry queue
                try { FlushPending(); } catch { }
                try { FlushPaced(); } catch { }   // round-282: paced lane, after the retry flush
                Thread.Sleep(15);
            }
        }

        // ── IConnectionManager ────────────────────────────────────────────────
        public void OnConnecting(ConnectionInfo info) { }

        public void OnConnected(ConnectionInfo info)
        {
            Plugin.Logger.LogInfo("[SteamClient] relay connected.");
            try { var m = _mgr; _lanesOk = m != null && m.Connection.ConfigureConnectionLanes(SteamLanes.Priorities, SteamLanes.Weights) == Result.OK; }
            catch { _lanesOk = false; }
            if (!_lanesOk) Plugin.Logger.LogWarning("[SteamClient] connection lanes unavailable — single-lane sends (pre-T3 behaviour).");
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
