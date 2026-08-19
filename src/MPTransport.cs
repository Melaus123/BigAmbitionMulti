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
