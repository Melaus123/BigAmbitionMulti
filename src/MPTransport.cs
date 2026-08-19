using System;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

namespace BigAmbitionsMP
{
    // ── Transport seam (Steam-connect campaign, slice 1) ─────────────────────
    // MPServer/MPClient talk to peers through these types instead of LiteNetLib
    // directly, so slice 2 can add a Steam relay transport (Facepunch
    // SteamNetworkingSockets) beside the UDP one without touching the message
    // layer.  The LiteNetLib implementations below MOVE the existing semantics
    // verbatim — same NetManager flags, same "BAMP" accept key, same background
    // poll threads (events fire on the poll thread, exactly as before; all
    // existing handlers already marshal to the main thread where needed).
    //
    // Link ids: LiteNetLib peer ids are small non-negatives.  A later Steam
    // transport must allocate from a DISJOINT range (e.g. 1_000_000+) so the
    // int-keyed registries (_peerNames, _pendingJoins) never collide.

    /// <summary>One connected remote peer, transport-agnostic.</summary>
    public abstract class MPLink
    {
        public abstract int Id { get; }
        /// <summary>Is the underlying connection still live (join-queue expiry checks).</summary>
        public abstract bool IsAlive { get; }
        /// <summary>Log-safe endpoint description (no raw IPs).</summary>
        public abstract string Describe { get; }
        /// <summary>Round-276: bytes this peer is owed but has not been handed yet —
        /// the congestion signal for the join-baseline verifier and the phase-report
        /// probe (a verify window or a peer-log deadline is unmeetable while megabytes
        /// sit in front of the reply).
        /// Round-282 UPDATE: this is now the WHOLE outbound truth — the transport's own
        /// refused-send backlog PLUS the paced lane's still-unreleased bytes.  A probe
        /// reading queue depth must see the paced mirror too: those bytes are real data
        /// owed to that peer, merely metered out.  Deliberately NON-virtual (round-282):
        /// one definition of "the whole truth" instead of per-transport copies that
        /// drift apart as one of them is edited.</summary>
        public long PendingSendBytes => TransportPendingBytes + PacedSendBytes;

        /// <summary>The transport's OWN send backlog, paced lane excluded.  Scope
        /// (round-276b): Steam links report exact queued bytes; LiteNetLib links report
        /// a count×MTU upper-bound estimate; the base returns 0 only for transports with
        /// no visible backlog at all.
        /// This — never PendingSendBytes — is the pacing gate's input (round-282): gating
        /// on a figure that INCLUDES the paced queue would make the queue gate itself and
        /// never drain.</summary>
        protected virtual long TransportPendingBytes => 0;

        /// <summary>Round-282: bytes still sitting unreleased in this link's paced lane.</summary>
        public virtual long PacedSendBytes => 0;

        /// <summary>Round-282b: the STRICTEST outbound figure this transport can report —
        /// everything above PLUS whatever the transport library itself is still holding.
        /// Used ONLY by the host-quit drain, which needs "has it actually left?", not the
        /// cheap congestion signal PendingSendBytes is tuned for.  Deliberately NOT the
        /// pacing gate's input: the gate must not stall on bytes that are already in
        /// flight and therefore no longer blocking anything queued behind them.
        /// The base can only report what it can see.</summary>
        public virtual long UnflushedSendBytes => PendingSendBytes;

        /// <summary>Round-282b: close this link GRACEFULLY, flushing what the transport
        /// still holds, and carry a reason tag to the peer.  Returns false when the
        /// transport offers no flushing close — in which case the caller must NOT
        /// disconnect at all: on such a transport a disconnect DISCARDS pending data,
        /// making a "polite" close strictly worse than letting teardown happen (see
        /// LnlLink for the decompiled evidence).</summary>
        public virtual bool CloseFlushing(byte[] reason) => false;

        public abstract void Send(byte[] data, bool reliable);

        /// <summary>Round-282 (mirror pacing, field 20260818-215459: ~6.6MB of store
        /// mirrors queued per join-save cycle against links draining at 250-330KB/s,
        /// leaving urgent gameplay messages 30s+ behind the convoy): send a payload that
        /// tolerates lateness by design down a METERED lane instead of dumping it into
        /// the strictly-ordered per-link FIFO all at once.  The user's shape for it:
        /// "go now, but don't use the entire road — multiple smaller trucks."
        /// supersedeKey (may be "") lets a fresher payload drop an older, still-entirely-
        /// unsent one for the same subject; see PacedSendQueue.
        /// The base falls back to an immediate reliable Send — a transport with no pump
        /// to meter from is honest about being unpaced rather than silently dropping.</summary>
        public virtual void SendPaced(byte[] data, string supersedeKey = "") => Send(data, reliable: true);

        /// <summary>Round-283 (the express lane, field 20260818-215459: clock heartbeats arriving
        /// 30s+ late because they sat in the ONE strictly-ordered FIFO behind megabytes of save
        /// mirror — which flapped client lifecycle and fed the round-276 join-save storm): send a
        /// payload that must not wait behind bulk, EVER.  Round-282 metered the bulk; this gives the
        /// two most critical signals a lane bulk cannot occupy.
        ///
        /// Contract: within the express lane order is preserved; BETWEEN lanes ordering is
        /// deliberately broken — that is the entire point, and it is why every express message
        /// carries a monotonic Seq stamp its receiver uses to drop stale copies.  ONLY the round-283
        /// whitelist may use it (host→client GameTimeSync, client→host PhaseReport), and only toward
        /// a peer that advertised the capability; widening it needs the same order-safety audit those
        /// two got.
        ///
        /// The base is an immediate reliable Send — a transport with no lane to jump is honest about
        /// being ordinary rather than pretending to a priority it cannot deliver.</summary>
        public virtual void SendExpress(byte[] data) => Send(data, reliable: true);

        public abstract void Disconnect(byte[] reason);

        public void Send(MessageEnvelope env) => Send(env.Serialize(), reliable: true);
    }

    // ── Round-282: the paced lane ────────────────────────────────────────────
    // One implementation shared by every link type (SteamLink, SteamClientTransport,
    // LnlLink).  The three transports already carry three near-identical copies of
    // the refused-send retry queue, and each fix to it has had to be made three
    // times; the paced lane gets exactly one copy from the start.
    //
    // Shape: a FIFO of PAYLOADS, each pre-split into the chunks the transport will
    // actually put on the wire.  The link's pump asks TryRelease once per tick and
    // hands at most ONE chunk to the normal send path, and only while the link's own
    // backlog is under the headroom.  Consequences that matter:
    //   • at most ~one paced chunk is ever queued in front of an urgent message;
    //   • chunks of ONE payload keep their relative order (single releaser, single
    //     FIFO underneath) — Steam reassembly is index-keyed, but the transport's own
    //     ordering is what keeps a later payload from interleaving;
    //   • concurrent paced payloads to the same link queue behind each other.

    /// <summary>Round-282: per-link metered send queue.  Thread-safe; enqueued from
    /// sender threads (the mirror sweep runs off the main thread), released only from
    /// the owning transport's pump thread.</summary>
    internal sealed class PacedSendQueue
    {
        /// <summary>Release the next paced chunk only while the link's own backlog is
        /// UNDER this.  256KB ≈ one second of a 250-330KB/s relay link (the rates
        /// measured in field 20260818-215459), so an urgent message enqueued at the
        /// worst moment waits about a second instead of the whole convoy.
        /// TUNABLE: the `outQ=` figures on the round-276 phase lines are the field
        /// fingerprint to tune against — if outQ is routinely pinned at the headroom
        /// while mirrors crawl, the link is slower than this assumes and the number
        /// should come DOWN (latency), not up (throughput).</summary>
        public const long HeadroomBytes = 256L * 1024;

        /// <summary>A payload waiting longer than this without finishing gets one WARN
        /// naming the backlog — silence during a 60s stall is undiagnosable.</summary>
        private const long SlowWarnSeconds = 60;

        private sealed class Payload
        {
            public byte[][] Chunks = Array.Empty<byte[]>();
            public int Next;                 // index of the next chunk to release
            public long TotalBytes;
            public long UnsentBytes;
            public string Key = "";
            public long QueuedTicks;
            public bool Warned;
            /// <summary>Partially sent — a supersede must NEVER drop one of these: its
            /// first chunks are already on the wire and the receiver would wait out the
            /// 120s reassembly timeout on a message that can never complete.</summary>
            public bool Started => Next > 0;
        }

        private readonly LinkedList<Payload> _q = new();
        private long _bytes;
        private readonly string _tag;

        public PacedSendQueue(string tag) { _tag = tag; }

        /// <summary>Bytes accepted for this peer that have not been released yet.</summary>
        public long Bytes { get { lock (_q) return _bytes; } }

        /// <summary>Queue a pre-chunked payload.  When supersedeKey is non-empty, any
        /// queued payload with the same key that has NOT started sending is dropped:
        /// a store mirror for the same (session, character) is a full replacement.
        /// Round-282c honesty note (rig-measured): because the autosave ROTATION renames
        /// the session every save, consecutive sweeps rarely share member keys — in
        /// practice supersede mostly catches the small manifest piece (~0.07% of bytes
        /// in the soak).  It is kept for semantic correctness (same-key = same file on
        /// the receiver), not as a convoy collapser.  Started payloads are never touched
        /// (see Payload.Started).</summary>
        public void Enqueue(byte[][] chunks, string supersedeKey, string describe)
        {
            if (chunks == null || chunks.Length == 0) return;
            long total = 0;
            foreach (var c in chunks) total += c?.Length ?? 0;
            var p = new Payload
            {
                Chunks = chunks, TotalBytes = total, UnsentBytes = total,
                Key = supersedeKey ?? "", QueuedTicks = DateTime.UtcNow.Ticks,
            };
            long droppedBytes = 0; int droppedCount = 0;
            lock (_q)
            {
                if (p.Key.Length > 0)
                {
                    var node = _q.First;
                    while (node != null)
                    {
                        var next = node.Next;
                        if (!node.Value.Started && node.Value.Key == p.Key)
                        {
                            droppedBytes += node.Value.UnsentBytes; droppedCount++;
                            _bytes -= node.Value.UnsentBytes;
                            _q.Remove(node);
                        }
                        node = next;
                    }
                }
                _q.AddLast(p);
                _bytes += total;
            }
            if (droppedCount > 0)
                Plugin.Logger.LogInfo($"[{_tag}] paced mirror '{p.Key}' to {describe}: {droppedCount} queued copy/copies "
                                    + $"({droppedBytes / 1024}KB, none sent yet) superseded by a fresher mirror.");
            Plugin.Logger.LogInfo($"[{_tag}] paced mirror: {total / 1024}KB in {chunks.Length} chunk(s) to {describe}.");
        }

        /// <summary>Pump tick: the next chunk to hand to the normal send path, or null
        /// when the queue is empty or the link is still too backed up.  linkBacklog is
        /// the transport's OWN backlog — see MPLink.TransportPendingBytes for why it
        /// must not include this queue.</summary>
        public byte[]? TryRelease(long linkBacklog, string describe)
        {
            // Round-282c (verifier nit): Bytes is read from the main thread (phase probe,
            // quit drain) — logging inside the lock let a slow logger stall those reads.
            // Messages are composed under the lock and emitted after it releases.
            List<string>? warns = null;
            string? drained = null;
            byte[]? released = null;
            lock (_q)
            {
                if (_q.Count == 0) return null;
                long now = DateTime.UtcNow.Ticks;

                // Stall warning fires whether or not the gate opens — a payload stuck
                // behind a congested link is exactly the case worth naming.
                for (var n = _q.First; n != null; n = n.Next)
                {
                    var w = n.Value;
                    if (w.Warned || now - w.QueuedTicks < TimeSpan.TicksPerSecond * SlowWarnSeconds) continue;
                    w.Warned = true;
                    (warns ??= new List<string>()).Add($"[{_tag}] paced mirror to {describe} still unsent after "
                        + $"{(now - w.QueuedTicks) / TimeSpan.TicksPerSecond}s: {w.UnsentBytes / 1024}KB of it left, "
                        + $"{_bytes / 1024}KB paced behind it, link backlog {linkBacklog / 1024}KB.");
                }

                if (linkBacklog < HeadroomBytes)
                {
                    var head = _q.First!.Value;
                    released = head.Chunks[head.Next++];
                    int len = released?.Length ?? 0;
                    head.UnsentBytes -= len; _bytes -= len;
                    if (head.Next >= head.Chunks.Length)
                    {
                        _q.RemoveFirst();
                        double secs = (now - head.QueuedTicks) / (double)TimeSpan.TicksPerSecond;
                        // "drained" = every chunk has been handed to the link, NOT acked by
                        // the peer (no transport here reports acks).  Worded so a reader of
                        // the log cannot mistake it for delivery confirmation.
                        drained = $"[{_tag}] paced mirror drained to {describe} in {secs:F1}s "
                                + $"({head.TotalBytes / 1024}KB released to the link).";
                    }
                }
            }
            try
            {
                if (warns != null) foreach (var w in warns) Plugin.Logger.LogWarning(w);
                if (drained != null) Plugin.Logger.LogInfo(drained);
            }
            catch { }
            return released;
        }

        /// <summary>Link is gone — nothing queued here can ever be delivered.</summary>
        public void Clear()
        {
            lock (_q) { if (_q.Count == 0) return; _q.Clear(); _bytes = 0; }
        }
    }

    // ── Round-283: the express lane ──────────────────────────────────────────
    // One implementation shared by SteamLink and SteamClientTransport, for the same
    // reason PacedSendQueue is shared: the retry queue already exists in three
    // near-identical copies and every fix to it has had to be made three times.
    //
    // Shape: a FIFO the pump drains BEFORE the retry (_pending) queue and BEFORE the
    // paced lane.  An express send goes out IMMEDIATELY when the express lane itself
    // is empty — it does not queue behind _pending the way SendReliableRaw does, which
    // is the whole mechanism: the urgent message overtakes the bulk backlog instead of
    // inheriting its latency.  It queues only when Steam refuses it (send buffer full)
    // or when an earlier express message is still waiting, so order WITHIN the lane is
    // preserved even though order between lanes is not.
    //
    // Deliberately unbounded-by-count but tiny by construction: the whitelist is two
    // small messages, one every ~3s and one per lifecycle transition, and each is
    // rejected above SteamFrames.ChunkSize before it ever reaches here.

    /// <summary>Round-283: per-link express send queue plus the lane's instrumentation.
    /// Thread-safe; enqueued from sender threads, drained from the owning transport's pump.</summary>
    internal sealed class ExpressSendQueue
    {
        /// <summary>Log "express jumped a backlog" only when the backlog it jumped is at least
        /// this big — below it the lane is not buying anything worth a log line.</summary>
        private const long JumpLogBacklogBytes = 64L * 1024;
        /// <summary>...and at most once per this many seconds per link: the clock heartbeat is
        /// ~3s, so an unthrottled line would itself become the flood during exactly the congestion
        /// it reports (round-88 hygiene: retry lines once burned the bug-report ring to 19s).</summary>
        private const long JumpLogEverySeconds = 60;

        private readonly LinkedList<byte[]> _q = new();
        private readonly string _tag;
        private long _bytes;
        // Round-283b (verifier finding b): an immediate send runs trySend OUTSIDE the lock.
        // Without this flag, two concurrent senders could both see an empty queue and race
        // their trySends — and a refused-then-queued older message behind a succeeded newer
        // one is an IN-LANE REORDER, which the lane's contract forbids.  Inert with today's
        // single caller (BroadcastGameTime, main thread); load-bearing the day the held
        // phase-report switch ships, whose callers span the poll AND main threads — fixed
        // now precisely because that is the round it would be forgotten in.
        private bool _inFlight;

        // Instrumentation.  Ticks-based, not UnityEngine.Time — the pump runs off the main thread.
        private long _sends;             // express payloads handed to the wire (immediate + released)
        private long _queued;            // express payloads that had to wait (refusal or lane busy)
        private int  _depthHighWater;    // deepest the lane has ever been, in messages
        private long _bytesHighWater;    // ...and in bytes
        private long _jumpLogNextTicks;
        private long _jumpsSinceLog;
        private long _jumpBacklogMax;

        public ExpressSendQueue(string tag) { _tag = tag; }

        public long Bytes { get { lock (_q) return _bytes; } }
        public long Sends { get { lock (_q) return _sends; } }
        public int  DepthHighWater { get { lock (_q) return _depthHighWater; } }

        /// <summary>The express send.  `trySend` returns true when the transport ACCEPTED the bytes
        /// (a Steam Result.OK); false means refused-and-retryable, so the payload waits here and the
        /// pump re-offers it.  `bulkBacklog` is the link's bulk debt (retry queue + paced lane) read
        /// by the CALLER before entering this lock — reading it in here would mean holding two
        /// transport locks at once for a log line.</summary>
        public void Send(byte[] data, Func<byte[], bool> trySend, long bulkBacklog, string describe)
        {
            if (data == null || data.Length == 0) return;
            bool sendNow;
            lock (_q)
            {
                sendNow = _q.Count == 0 && !_inFlight;
                if (sendNow) _inFlight = true;
                else { EnqueueLocked(data); _queued++; }
            }
            // Queued behind an earlier express message: the pump owns it now, and it has NOT
            // jumped anything yet — NoteJump is only for a send that actually reached the wire
            // ahead of the backlog, so the log line stays literally true.
            if (!sendNow) return;
            bool ok;
            try { ok = trySend(data); }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[{_tag}] express send to {describe} threw: {ex.Message} — queued for the pump to retry.");
                // Front of the queue: anything enqueued while we were in flight is NEWER.
                lock (_q) { EnqueueFrontLocked(data); _queued++; _inFlight = false; }
                return;
            }
            lock (_q)
            {
                _inFlight = false;
                if (ok) _sends++;
                else { EnqueueFrontLocked(data); _queued++; }   // refused: older than anything queued meanwhile
            }
            if (ok) NoteJump(bulkBacklog, describe);
        }

        /// <summary>Pump tick: re-offer the head until the transport refuses.  Runs BEFORE the retry
        /// queue and BEFORE the paced lane, so a queued express message is still ahead of every bulk
        /// byte the link owes.</summary>
        public void Flush(Func<byte[], bool> trySend, string describe)
        {
            while (true)
            {
                byte[] head;
                // Round-283b: never flush around an in-flight immediate send — its payload is
                // OLDER than anything queued here, and sending the queue head past it would be
                // the same in-lane reorder the _inFlight flag exists to prevent.
                lock (_q) { if (_inFlight || _q.Count == 0) return; head = _q.First!.Value; }
                bool ok;
                try { ok = trySend(head); }
                catch { return; }                      // still stuck — next pump tick retries
                if (!ok) return;
                lock (_q) { if (_q.Count > 0) { _q.RemoveFirst(); _bytes -= head.Length; _sends++; } }
            }
        }

        /// <summary>Link is gone — nothing queued here can ever be delivered.  Reports the lane's
        /// lifetime counters, because a link that dies mid-congestion is exactly the case where the
        /// high-water figures explain what the player saw.</summary>
        public void Clear(string describe)
        {
            long sends, queued, bytesHw; int depthHw;
            lock (_q)
            {
                // Idempotent BY COUNTER RESET, not just by emptiness: the pump calls this on every
                // ~15ms tick once the link is dead, so a summary keyed on "have we ever sent" would
                // print eighty lines a second forever.  Zeroing here makes the second call a no-op.
                if (_q.Count == 0 && _sends == 0 && _queued == 0) return;
                _q.Clear(); _bytes = 0;
                sends = _sends; queued = _queued; depthHw = _depthHighWater; bytesHw = _bytesHighWater;
                _sends = 0; _queued = 0; _depthHighWater = 0; _bytesHighWater = 0;
            }
            try
            {
                Plugin.Logger.LogInfo($"[{_tag}] express lane to {describe} closed: {sends} sent, {queued} had to wait, "
                                    + $"depth high-water {depthHw} msg/{bytesHw}B.");
            }
            catch { }
        }

        private void EnqueueFrontLocked(byte[] data)
        {
            _q.AddFirst(data); _bytes += data.Length;
            if (_q.Count > _depthHighWater) _depthHighWater = _q.Count;
            if (_bytes > _bytesHighWater) _bytesHighWater = _bytes;
        }

        private void EnqueueLocked(byte[] data)
        {
            _q.AddLast(data); _bytes += data.Length;
            if (_q.Count > _depthHighWater) _depthHighWater = _q.Count;
            if (_bytes > _bytesHighWater)   _bytesHighWater = _bytes;
        }

        /// <summary>The line that proves the lane earned its keep: an express message went out while
        /// this link still owed the peer a real backlog.  Throttled to one line per minute per link,
        /// carrying the WORST backlog jumped in that minute and how many jumps there were.</summary>
        private void NoteJump(long bulkBacklog, string describe)
        {
            if (bulkBacklog < JumpLogBacklogBytes) return;
            long jumps; long worst; bool emit = false;
            lock (_q)
            {
                _jumpsSinceLog++;
                if (bulkBacklog > _jumpBacklogMax) _jumpBacklogMax = bulkBacklog;
                long now = DateTime.UtcNow.Ticks;
                jumps = _jumpsSinceLog; worst = _jumpBacklogMax;
                if (now >= _jumpLogNextTicks)
                {
                    _jumpLogNextTicks = now + TimeSpan.TicksPerSecond * JumpLogEverySeconds;
                    _jumpsSinceLog = 0; _jumpBacklogMax = 0;
                    emit = true;
                }
            }
            if (!emit) return;
            try
            {
                Plugin.Logger.LogInfo($"[{_tag}] express jumped a backlog to {describe}: {jumps} urgent message(s) "
                                    + $"went ahead of up to {worst / 1024}KB of bulk in the last minute "
                                    + $"(that backlog is what they used to wait behind).");
            }
            catch { }
        }
    }

    /// <summary>LiteNetLib-backed link (direct UDP / LAN).</summary>
    public sealed class LnlLink : MPLink
    {
        public readonly NetPeer Peer;
        public LnlLink(NetPeer peer) { Peer = peer; }
        public override int Id => Peer.Id;
        public override bool IsAlive => Peer.ConnectionState == ConnectionState.Connected;
        public override string Describe => $"udp:{Peer.Id}";
        /// <summary>Round-276b (verifier finding 6): LiteNetLib exposes a reliable-queue
        /// PACKET count, not bytes — estimate as count × MTU (an upper bound; the sends
        /// above are ReliableOrdered on channel 0).  Over-estimating biases the verifier
        /// toward window-extension, the safe direction.  Steam links report exact bytes.
        /// Round-282: this is the transport's own backlog only — the paced lane is added
        /// on top by MPLink.PendingSendBytes, and this figure is the pacing gate.</summary>
        protected override long TransportPendingBytes
        {
            get { try { return (long)Peer.GetPacketsCountInReliableQueue(0, true) * Peer.Mtu; } catch { return 0; } }
        }

        // Round-282: LNL pacing.  Unlike Steam, LiteNetLib fragments a large reliable
        // message natively, so there is nothing to split here — splitting at the app
        // layer would hand the receiver pieces that are not parseable envelopes.  One
        // payload = one paced item; the win is that the sweep's several mirror files
        // are released ONE AT A TIME behind the headroom gate instead of all at once.
        // (Honest limit: once released, a single multi-MB payload does occupy the link
        // for its whole length — LAN links rarely make that visible, and a smaller unit
        // would require an app-level fragment format LNL does not need.)
        private readonly PacedSendQueue _paced = new("LnlLink");
        public override long PacedSendBytes => _paced.Bytes;

        public override void SendPaced(byte[] data, string supersedeKey = "")
        {
            if (data == null || data.Length == 0) return;
            _paced.Enqueue(new[] { data }, supersedeKey, Describe);
        }

        // ── Round-283: why this link has NO express lane ─────────────────────
        // Decompiled from the referenced LiteNetLib 1.3.1 (net471 lib in the NuGet
        // package this project builds against) with ilspycmd — evidence, not assumption,
        // per the round-282 precedent.
        //
        // The API surface IS there: NetPeer exposes Send(NetDataWriter, byte
        // channelNumber, DeliveryMethod) and DeliveryMethod.ReliableSequenced = 3.  Using
        // it would be wrong three times over:
        //
        //  1. IT THROWS.  NetManager._channelsCount defaults to 1, and NetPeer's ctor
        //     allocates _channels = new BaseChannel[netManager.ChannelsCount * 4] — four
        //     slots, all belonging to channel 0.  SendInternal guards
        //     `channelNumber >= _channels.Length`, which for channel 1 is `1 >= 4` =
        //     FALSE, so it passes the guard and then calls
        //     CreateChannel(channelNumber * 4 + deliveryMethod) = CreateChannel(7), which
        //     indexes _channels[7] → IndexOutOfRangeException.  The library's own guard
        //     compares channelNumber against the ARRAY length instead of ChannelsCount,
        //     so raising ChannelsCount is the only way to make channel 1 exist at all.
        //  2. RAISING IT SILENTLY LOSES DATA ON OLD PEERS — the July class exactly.
        //     NetPeer.ProcessPacket's Channeled/Ack case does
        //     `if (packet.ChannelId >= _channels.Length) { PoolRecycle(packet); break; }`.
        //     A peer that did not ALSO raise ChannelsCount discards every channel-1 packet
        //     without a word.  Our capability flag could gate that, but see 3.
        //  3. ReliableSequenced IS THE WRONG DELIVERY METHOD FOR A PHASE REPORT.  It
        //     cannot fragment — SendInternal throws TooBigPacketException once
        //     length + headerSize > mtu for anything but ReliableOrdered/ReliableUnordered,
        //     and a phase report's Detail string is unbounded — and it drops intermediate
        //     packets by design: SequencedChannel.ProcessPacket accepts a packet only when
        //     RelativeSequenceNumber > 0, and only _lastPacket is ever retransmitted.
        //     Newest-wins is right for the clock (absolute state) and wrong for phase
        //     reports, where each transition is its own state report the host acts on.
        //
        // And there is nothing here for an express QUEUE to overtake either: NetPeer.Send
        // never refuses, it enqueues inside the library's ReliableChannel, so this
        // transport owns no app-level backlog an urgent message could be lifted out of.
        // SendExpress therefore stays the base immediate ordered Send — honestly identical
        // to today — and only counts, so a UDP field log still says whether urgency was
        // being asked for while this link was backed up.
        private long _expressSends;
        private long _expressBehindLogNextTicks;
        private long _expressBehindSince;

        public override void SendExpress(byte[] data)
        {
            long behind = 0;
            try { behind = PendingSendBytes; } catch { }
            _expressSends++;
            if (behind >= 64L * 1024)
            {
                _expressBehindSince++;
                long now = DateTime.UtcNow.Ticks;
                if (now >= _expressBehindLogNextTicks)
                {
                    _expressBehindLogNextTicks = now + TimeSpan.TicksPerSecond * 60;
                    long n = _expressBehindSince; _expressBehindSince = 0;
                    Plugin.Logger.LogInfo($"[LnlLink] express to {Describe}: {n} urgent message(s) in the last minute "
                        + $"went out behind {behind / 1024}KB of backlog — this transport has no lane to jump "
                        + $"(LiteNetLib queues inside NetPeer; see the round-283 note above).  Total express {_expressSends}.");
                }
            }
            Send(data, reliable: true);
        }

        // ── Round-282b: why this link has NO flushing close ──────────────────
        // Read from the decompiled LiteNetLib 1.3.1 (the exact DLL this project
        // references), not assumed:
        //   NetPeer.Shutdown(data,...) builds the disconnect packet, flips the peer to
        //   ConnectionState.ShutdownRequested and SendRaw's it immediately;
        //   NetPeer.Update's ShutdownRequested case then RETURNS after re-sending that
        //   packet every 300ms — the reliable channels are never serviced again.
        // So a LiteNetLib disconnect DISCARDS whatever reliable data is still queued.
        // CloseFlushing therefore stays false (base) and the quit path leaves UDP links
        // alone: the drain-wait is the whole guarantee here, and disconnecting early
        // would destroy exactly the farewell mirror it is waiting on.
        //
        // Matching honesty about the drain figure: GetPacketsCountInReliableQueue
        // returns BaseChannel.PacketsInQueue = OutgoingQueue.Count — packets not yet
        // moved into the send window.  Packets already in the window awaiting ACK are
        // NOT counted, so this link's drain-to-zero means "handed to the send window",
        // and up to a window's worth (64 packets by default) may still be in flight.
        // No API here exposes that, so UnflushedSendBytes is left at the base figure
        // rather than dressed up as something stricter than it is.

        /// <summary>Round-282: release at most ONE paced payload per pump tick, and only
        /// while this peer's reliable queue is under the headroom.  Called from
        /// LnlHostTransport's existing poll loop (~15ms) — LNL needed no new pump.</summary>
        internal void FlushPaced()
        {
            if (!IsAlive) { _paced.Clear(); return; }
            var chunk = _paced.TryRelease(TransportPendingBytes, Describe);
            if (chunk == null) return;
            try { Send(chunk, reliable: true); }
            catch (Exception ex)
            { Plugin.Logger.LogWarning($"[LnlLink] paced send to {Describe} failed: {ex.Message} — that mirror piece is lost; the next save re-mirrors."); }
        }

        public override void Send(byte[] data, bool reliable)
        {
            var writer = new NetDataWriter();
            writer.Put(data);
            Peer.Send(writer, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }
        public override void Disconnect(byte[] reason)
        { try { Peer.Disconnect(reason); } catch { } }
    }

    /// <summary>Host-side listener: accepts peers, surfaces them as MPLinks.
    /// Events fire on the transport's own poll thread.</summary>
    public interface IHostTransport
    {
        bool Start(int port);
        void Stop();
        event Action<MPLink>? PeerConnected;
        event Action<MPLink, string>? PeerDisconnected;   // reason text
        event Action<MPLink, byte[]>? Received;
    }

    /// <summary>Client-side connection.  Events fire on the poll thread.</summary>
    public interface IClientTransport
    {
        void Disconnect();
        /// <summary>Stop the poll loop WITHOUT tearing down or joining — safe to
        /// call from the Disconnected handler (which runs ON the poll thread;
        /// a full Disconnect there would join the thread against itself).  The
        /// manager itself is torn down by the next Connect's guard, exactly as
        /// the pre-seam code did.</summary>
        void StopPolling();
        bool IsRunning { get; }
        void Send(byte[] data, bool reliable);
        /// <summary>Round-282: the client-side half of the paced lane (see
        /// MPLink.SendPaced).  No v1 caller — the client's save upload stays IMMEDIATE
        /// by the round-282 scope decision (a host waiting on an upload to complete a
        /// coordinated save is not background replication).  The seam exists so the
        /// host-handoff work has a metered lane in both directions without another
        /// transport-layer round.  LiteNetLib falls back to an immediate send.</summary>
        void SendPaced(byte[] data, string supersedeKey = "");
        /// <summary>Round-283: the client-side half of the express lane (see MPLink.SendExpress).
        /// Its ONE v1 caller is MPClient.SendPhaseReport, and only toward a host that advertised
        /// the capability.  LiteNetLib falls back to an immediate ordered send — see LnlLink for
        /// the decompiled reasons that transport gets no separate lane.</summary>
        void SendExpress(byte[] data);
        event Action? Connected;
        event Action<string, byte[]>? Disconnected;       // reason text + host's reason bytes ("BAMP:...")
        event Action<byte[]>? Received;
    }

    /// <summary>The existing LiteNetLib UDP host, moved behind the seam.</summary>
    public sealed class LnlHostTransport : IHostTransport
    {
        private EventBasedNetListener? _listener;
        private NetManager? _server;
        private Thread? _pollThread;
        private volatile bool _running;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, LnlLink> _links = new();

        public event Action<MPLink>? PeerConnected;
        public event Action<MPLink, string>? PeerDisconnected;
        public event Action<MPLink, byte[]>? Received;

        public bool Start(int port)
        {
            _links.Clear();
            _listener = new EventBasedNetListener();
            _server   = new NetManager(_listener)
            {
                AutoRecycle = true,
                UnconnectedMessagesEnabled = false,
            };
            _listener.ConnectionRequestEvent += request =>
            {
                // Accept all connections (key-gated); LOGGED — a silent handler
                // made transport-level join failures undiagnosable (2026-06-11).
                Plugin.Logger.LogInfo($"[Server] connection request from {request.RemoteEndPoint} (peers={_links.Count}).");
                request.AcceptIfKey("BAMP");
            };
            _listener.PeerConnectedEvent += peer =>
            {
                var link = new LnlLink(peer);
                _links[peer.Id] = link;
                PeerConnected?.Invoke(link);
            };
            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                if (!_links.TryRemove(peer.Id, out var link)) link = new LnlLink(peer);
                PeerDisconnected?.Invoke(link, info.Reason.ToString());
            };
            _listener.NetworkReceiveEvent += (peer, reader, channel, delivery) =>
            {
                if (!_links.TryGetValue(peer.Id, out var link)) return;
                Received?.Invoke(link, reader.GetRemainingBytes());
            };
            if (!_server.Start(port)) return false;
            _running = true;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "BAMP-Server" };
            _pollThread.Start();
            return true;
        }

        public void Stop()
        {
            _running = false;
            _server?.Stop();
            _pollThread?.Join(1000);
            _links.Clear();
        }

        private void PollLoop()
        {
            while (_running)
            {
                // A message handler throwing must NOT kill the network thread —
                // that would freeze the whole session with no recovery short of
                // re-hosting.  Catch, log, keep polling.
                try { _server?.PollEvents(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[Server] PollEvents: {ex}"); }
                // Round-282: this loop is LNL's pacing pump.  It already runs at the
                // Steam pump's cadence and already holds the link registry, so the
                // paced lane needed no thread of its own.  Per-link isolation: one
                // sick peer must not stop the others from draining.
                foreach (var l in _links.Values)
                {
                    try { l.FlushPaced(); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] paced flush {l.Describe}: {ex.Message}"); }
                }
                Thread.Sleep(15);
            }
        }
    }

    /// <summary>The existing LiteNetLib UDP client, moved behind the seam.</summary>
    public sealed class LnlClientTransport : IClientTransport
    {
        private EventBasedNetListener? _listener;
        private NetManager? _client;
        private Thread? _pollThread;
        private volatile bool _running;

        public event Action? Connected;
        public event Action<string, byte[]>? Disconnected;
        public event Action<byte[]>? Received;

        public bool IsRunning => _running;

        public bool Connect(string hostIp, int port)
        {
            _listener = new EventBasedNetListener();
            _client   = new NetManager(_listener) { AutoRecycle = true };
            _listener.PeerConnectedEvent += _ => Connected?.Invoke();
            _listener.PeerDisconnectedEvent += (_, info) =>
            {
                byte[] extra = Array.Empty<byte>();
                try
                {
                    if (!info.AdditionalData.IsNull && info.AdditionalData.AvailableBytes > 0)
                        extra = info.AdditionalData.GetRemainingBytes();
                }
                catch { }
                Disconnected?.Invoke(info.Reason.ToString(), extra);
            };
            _listener.NetworkReceiveEvent += (_, reader, channel, delivery) =>
                Received?.Invoke(reader.GetRemainingBytes());
            _client.Start();
            var peer = _client.Connect(hostIp, port, "BAMP");
            if (peer == null) { _client.Stop(); return false; }
            _running = true;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "BAMP-Client" };
            _pollThread.Start();
            return true;
        }

        public void StopPolling() => _running = false;

        public void Disconnect()
        {
            _running = false;
            _client?.Stop();
            if (_pollThread != null && _pollThread != Thread.CurrentThread) _pollThread.Join(1000);
            _client = null;
        }

        public void Send(byte[] data, bool reliable)
        {
            var writer = new NetDataWriter();
            writer.Put(data);
            _client?.FirstPeer?.Send(writer, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        /// <summary>Round-282: the UDP client has ONE peer and no per-link pump of its
        /// own (its poll loop services the NetManager, not a link registry), and LNL
        /// already fragments large reliable messages natively — so the honest answer
        /// here is an immediate send, not a bolted-on metering thread for a LAN link
        /// that rarely congests.  Documented rather than silently unpaced.</summary>
        public void SendPaced(byte[] data, string supersedeKey = "") => Send(data, reliable: true);

        /// <summary>Round-283: an immediate ordered send, for the same decompiled reasons LnlLink
        /// has no express lane (channel 1 throws with the default ChannelsCount, raising it silently
        /// loses data on peers that did not, and ReliableSequenced neither fragments nor keeps
        /// intermediate packets).  There is also no app-level backlog on this transport to overtake:
        /// NetPeer.Send never refuses, it queues inside LiteNetLib.  Documented rather than dressed
        /// up as a priority it does not have.</summary>
        public void SendExpress(byte[] data) => Send(data, reliable: true);

        private void PollLoop()
        {
            while (_running)
            {
                try { _client?.PollEvents(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[Client] PollEvents: {ex}"); }
                Thread.Sleep(15);
            }
        }
    }
}
