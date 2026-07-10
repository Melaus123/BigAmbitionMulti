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

        public override void Send(byte[] data, bool reliable)
        {
            try { _conn.SendMessage(data, reliable ? SendType.Reliable : SendType.Unreliable); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamHost] send to {Describe}: {ex.Message}"); }
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

        public void Send(byte[] data, bool reliable)
        {
            try { _mgr?.Connection.SendMessage(data, reliable ? SendType.Reliable : SendType.Unreliable); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamClient] send: {ex.Message}"); }
        }

        private void PumpLoop()
        {
            while (_running)
            {
                try { _mgr?.Receive(); }
                catch (Exception ex) { Plugin.Logger.LogError($"[SteamClient] Receive: {ex}"); }
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
            Received?.Invoke(bytes);
        }
    }
}
