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
        /// <summary>Log-safe endpoint description (no raw IPs).</summary>
        public abstract string Describe { get; }
        public abstract void Send(byte[] data, bool reliable);
        public abstract void Disconnect(byte[] reason);

        public void Send(MessageEnvelope env) => Send(env.Serialize(), reliable: true);
    }

    /// <summary>LiteNetLib-backed link (direct UDP / LAN).</summary>
    public sealed class LnlLink : MPLink
    {
        public readonly NetPeer Peer;
        public LnlLink(NetPeer peer) { Peer = peer; }
        public override int Id => Peer.Id;
        public override string Describe => $"udp:{Peer.Id}";
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
