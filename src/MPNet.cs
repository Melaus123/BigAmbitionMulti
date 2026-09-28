using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Connectivity helpers for the lobby's "Show IP": the host's PUBLIC ip (for
    /// internet play) and a BEST-EFFORT UPnP port-forward so a friend over the
    /// internet can reach the host without manually configuring their router.
    ///
    /// FAILS SAFE by construction.  Every network call runs on a background Task
    /// with a short timeout and a catch-all, so a router with no UPnP (or no
    /// internet at all) just leaves the flags unset and the UI falls back to the
    /// manual port-forward instructions — nothing blocks the game and no exception
    /// ever reaches it.
    /// </summary>
    public static class MPNet
    {
        // ── Public IP (for internet play) ─────────────────────────────────────
        private static volatile string _publicIp = "";
        private static volatile bool   _publicIpTried;
        public static string PublicIp     => _publicIp;
        public static bool   PublicIpTried => _publicIpTried;

        /// <summary>Look up our public IPv4 from a couple of "what's my ip" services
        /// (background, short timeout, cached).  No-op once we have it.</summary>
        public static void FetchPublicIpAsync()
        {
            if (!string.IsNullOrEmpty(_publicIp)) return;
            Task.Run(() =>
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                foreach (var url in new[] { "https://api.ipify.org", "http://checkip.amazonaws.com", "http://icanhazip.com" })
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.Timeout = 4000; req.ReadWriteTimeout = 4000; req.UserAgent = "BigAmbitionsMP";
                        using var resp = req.GetResponse();
                        using var sr = new StreamReader(resp.GetResponseStream());
                        string ip = sr.ReadToEnd().Trim();
                        if (IsIpv4(ip)) { _publicIp = ip; break; }
                    }
                    catch { }
                }
                _publicIpTried = true;
            });
        }

        private static bool IsIpv4(string s)
            => IPAddress.TryParse(s, out var a) && a.AddressFamily == AddressFamily.InterNetwork;

        // ── LAN IP (for same-network play; user request 2026-07-04) ───────────
        private static string _lanIp = "";
        /// <summary>The host's LAN IPv4 — the address same-network players join with, shown alongside the
        /// public IP. Resolved once via the UDP-connect trick (connect() on a UDP socket sends NOTHING; it
        /// only makes the OS pick the outbound interface, whose local address is the LAN ip), with a
        /// NIC-enumeration fallback for machines without a default route. Local-only — instant and safe on
        /// the main thread. Empty when the machine has no usable IPv4.</summary>
        public static string LanIp
        {
            get
            {
                if (!string.IsNullOrEmpty(_lanIp)) return _lanIp;
                try
                {
                    using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        s.Connect("8.8.8.8", 65530);   // no packet — routing decision only
                        if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                            _lanIp = ep.Address.ToString();
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(_lanIp))
                {
                    try
                    {
                        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                                { _lanIp = ua.Address.ToString(); break; }
                            if (!string.IsNullOrEmpty(_lanIp)) break;
                        }
                    }
                    catch { }
                }
                return _lanIp;
            }
        }

        // ── UPnP port-forward (best-effort) ───────────────────────────────────
        public enum UpnpState { Idle, Trying, Mapped, Unsupported, Failed }
        private static volatile UpnpState _upnp = UpnpState.Idle;
        public static UpnpState Upnp => _upnp;

        private static string _ctrlUrl = "", _svcType = "";
        private static int    _mappedPort;
        // Fold 2 (recurrence-covered Change port): the state below changes under _upnpLock. _gen is bumped when hosting
        // stops, so a forward still in flight then removes its own mapping instead of keeping it. _wantMove/_wantPort = a
        // Change-port move asked for while a forward was in progress (only the latest counts), run when that forward
        // completes. _tryPort = the port the in-flight forward asks for.
        private static readonly object _upnpLock = new object();
        private static int    _gen, _wantPort, _tryPort;
        private static bool   _wantMove;
        private static string _wantIp = "";

        /// <summary>Ask the router (UPnP IGD) to forward UDP <paramref name="port"/>
        /// to <paramref name="localIp"/>.  Background + timeouts + caught; sets
        /// Upnp=Unsupported when no IGD answers, Failed on a router error.</summary>
        public static void TryForwardAsync(int port, string localIp)
        {
            try
            {
                int gen;
                lock (_upnpLock)
                {
                    if (_upnp == UpnpState.Trying || _upnp == UpnpState.Mapped) return;
                    if (port <= 0 || string.IsNullOrEmpty(localIp)) { _upnp = UpnpState.Failed; return; }
                    _upnp = UpnpState.Trying; _tryPort = port; gen = _gen;
                }
                Task.Run(() => ForwardWork(0, port, localIp, gen));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UPnP] forward start failed: {ex.Message}"); }
        }

        /// <summary>Hosting stopped: drop a deferred move and remove whatever mapping exists - now if one is in place, or
        /// (for a forward still in flight) when that forward completes. Best-effort; a leftover mapping is harmless.</summary>
        public static void RemoveMappingAsync()
        {
            try
            {
                int port = 0, dropped = 0, inFlight = 0; string ctrl = "", svc = "";
                lock (_upnpLock)
                {
                    _gen++;
                    if (_wantMove) dropped = _wantPort;
                    _wantMove = false; _wantPort = 0; _wantIp = "";
                    if (_upnp == UpnpState.Trying) inFlight = _tryPort;
                    if (_upnp == UpnpState.Mapped) { port = _mappedPort; ctrl = _ctrlUrl; svc = _svcType; }
                    _mappedPort = 0; _upnp = UpnpState.Idle;
                }
                if (dropped != 0) Plugin.Logger.LogInfo($"[UPnP] hosting stopped - the deferred move to UDP {dropped} is dropped.");
                if (inFlight != 0) Plugin.Logger.LogInfo($"[UPnP] hosting stopped while the UDP {inFlight} forward is in progress - that forward removes its mapping when it completes.");
                if (port <= 0 || string.IsNullOrEmpty(ctrl)) return;
                Task.Run(() =>
                {
                    try { DeletePortMapping(ctrl, svc, port); }
                    catch { }
                });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UPnP] remove mapping failed: {ex.Message}"); }
        }

        /// <summary>Lobby review item 8 (Change port): remove the router mapping of the old port and ask for the new one, in
        /// that order on one background task. Fold 2: asked while a forward is in progress, the move is REMEMBERED (only the
        /// latest port counts) and runs when that forward completes. Best-effort like the rest.</summary>
        public static void MoveMappingAsync(int newPort, string localIp) => StartMove(newPort, localIp, false);

        private static void StartMove(int newPort, string localIp, bool deferred)
        {
            try
            {
                int oldPort = 0, gen = 0; bool defer = false, same = false, remove = false;
                lock (_upnpLock)
                {
                    if (_upnp == UpnpState.Trying) { _wantMove = true; _wantPort = newPort; _wantIp = localIp ?? ""; defer = true; }
                    else if (_upnp == UpnpState.Mapped && _mappedPort == newPort) same = true;
                    else if (newPort <= 0 || string.IsNullOrEmpty(localIp)) remove = true;
                    else
                    {
                        oldPort = _upnp == UpnpState.Mapped ? _mappedPort : 0;
                        _upnp = UpnpState.Trying; _tryPort = newPort; gen = _gen;
                    }
                }
                if (defer) { Plugin.Logger.LogInfo($"[UPnP] a forward is still in progress - the move to UDP {newPort} runs when it completes."); return; }
                if (same) { if (deferred) Plugin.Logger.LogInfo($"[UPnP] deferred move: UDP {newPort} is already the mapped port - nothing to move."); return; }
                if (remove) { RemoveMappingAsync(); return; }
                Task.Run(() => ForwardWork(oldPort, newPort, localIp ?? "", gen));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UPnP] move failed: {ex.Message}"); }
        }

        /// <summary>Background: remove <paramref name="oldPort"/>'s mapping (a move; 0 = none), then forward
        /// <paramref name="port"/>, then settle the state (ForwardDone).</summary>
        private static void ForwardWork(int oldPort, int port, string localIp, int gen)
        {
            bool mapped = false; UpnpState end = UpnpState.Failed;
            try
            {
                if (oldPort > 0)
                {
                    try
                    {
                        string ctrl = _ctrlUrl, svc = _svcType;
                        if (!string.IsNullOrEmpty(ctrl))
                        {
                            DeletePortMapping(ctrl, svc, oldPort);
                            Plugin.Logger.LogInfo($"[UPnP] Removed the old UDP {oldPort} mapping.");
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[UPnP] removing the old mapping failed: {ex.Message}"); }
                }
                if (!Discover()) { end = UpnpState.Unsupported; Plugin.Logger.LogInfo("[UPnP] No UPnP router found — manual port-forward needed."); }
                else if (AddPortMapping(port, localIp)) mapped = true;
                else Plugin.Logger.LogWarning("[UPnP] Router refused the port mapping.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UPnP] forward failed: {ex.Message}"); }
            ForwardDone(mapped, end, port, localIp, gen);
        }

        /// <summary>A forward completed: settle the state; if hosting stopped meanwhile, remove the mapping just made (unless a
        /// newer forward owns the same port); else run a move that was deferred while this forward ran.</summary>
        private static void ForwardDone(bool mapped, UpnpState end, int port, string localIp, int gen)
        {
            try
            {
                bool stale, undo = false, move = false; int movePort = 0; string moveIp = "", ctrl = "", svc = "";
                lock (_upnpLock)
                {
                    stale = gen != _gen;
                    if (stale)
                        undo = mapped && !((_upnp == UpnpState.Trying && _tryPort == port) || (_upnp == UpnpState.Mapped && _mappedPort == port));
                    else
                    {
                        _mappedPort = mapped ? port : 0;
                        _upnp = mapped ? UpnpState.Mapped : end;
                        if (_wantMove) { move = true; movePort = _wantPort; moveIp = _wantIp; _wantMove = false; _wantPort = 0; _wantIp = ""; }
                    }
                    ctrl = _ctrlUrl; svc = _svcType;
                }
                if (mapped && !stale) Plugin.Logger.LogInfo($"[UPnP] Forwarded UDP {port} -> {localIp} on the router.");
                if (undo)
                {
                    Plugin.Logger.LogInfo($"[UPnP] hosting stopped while the UDP {port} forward ran - removing that mapping.");
                    try { if (!string.IsNullOrEmpty(ctrl)) DeletePortMapping(ctrl, svc, port); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[UPnP] removing the stale UDP {port} mapping failed: {ex.Message}"); }
                }
                if (move)
                {
                    Plugin.Logger.LogInfo($"[UPnP] the forward in progress completed ({(mapped ? "mapped UDP " + port : end.ToString())}) - running the deferred move to UDP {movePort}.");
                    StartMove(movePort, moveIp, true);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UPnP] forward completion failed: {ex.Message}"); }
        }

        // ── UPnP internals: SSDP discovery → device description → SOAP ─────────
        private static bool Discover()
        {
            foreach (var st in new[]
            {
                "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                "urn:schemas-upnp-org:service:WANIPConnection:1",
            })
            {
                string loc = SsdpSearch(st);
                if (!string.IsNullOrEmpty(loc) && LoadControlUrl(loc)) return true;
            }
            return false;
        }

        private static string SsdpSearch(string st)
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.ReceiveTimeout = 2500;
                string req = "M-SEARCH * HTTP/1.1\r\n" +
                             "HOST: 239.255.255.250:1900\r\n" +
                             "MAN: \"ssdp:discover\"\r\n" +
                             "MX: 2\r\n" +
                             $"ST: {st}\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(req);
                udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var resp = udp.Receive(ref from);   // throws SocketException on timeout
                    string loc = Header(Encoding.ASCII.GetString(resp), "LOCATION");
                    if (!string.IsNullOrEmpty(loc)) return loc;
                }
            }
            catch { }
            return "";
        }

        private static string Header(string resp, string name)
        {
            foreach (var line in resp.Split('\n'))
            {
                int c = line.IndexOf(':');
                if (c > 0 && line.Substring(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(c + 1).Trim();
            }
            return "";
        }

        private static bool LoadControlUrl(string descUrl)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(descUrl);
                req.Timeout = 4000;
                string xml;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                    xml = sr.ReadToEnd();

                foreach (var svc in new[] { "WANIPConnection:1", "WANPPPConnection:1", "WANIPConnection:2" })
                {
                    string stype = "urn:schemas-upnp-org:service:" + svc;
                    int si = xml.IndexOf(stype, StringComparison.OrdinalIgnoreCase);
                    if (si < 0) continue;
                    int cu = xml.IndexOf("<controlURL>", si, StringComparison.OrdinalIgnoreCase);
                    if (cu < 0) continue;
                    int end = xml.IndexOf("</controlURL>", cu, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) continue;
                    string ctrl = xml.Substring(cu + 12, end - (cu + 12)).Trim();
                    _ctrlUrl = new Uri(new Uri(descUrl), ctrl).ToString();
                    _svcType = stype;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool AddPortMapping(int port, string localIp)
        {
            string body =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                $"<u:AddPortMapping xmlns:u=\"{_svcType}\">" +
                "<NewRemoteHost></NewRemoteHost>" +
                $"<NewExternalPort>{port}</NewExternalPort>" +
                "<NewProtocol>UDP</NewProtocol>" +
                $"<NewInternalPort>{port}</NewInternalPort>" +
                $"<NewInternalClient>{localIp}</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>BigAmbitionsMP</NewPortMappingDescription>" +
                "<NewLeaseDuration>0</NewLeaseDuration>" +
                "</u:AddPortMapping></s:Body></s:Envelope>";
            return Soap(_ctrlUrl, _svcType, "AddPortMapping", body);
        }

        private static void DeletePortMapping(string ctrl, string svc, int port)
        {
            string body =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                $"<u:DeletePortMapping xmlns:u=\"{svc}\">" +
                "<NewRemoteHost></NewRemoteHost>" +
                $"<NewExternalPort>{port}</NewExternalPort>" +
                "<NewProtocol>UDP</NewProtocol>" +
                "</u:DeletePortMapping></s:Body></s:Envelope>";
            Soap(ctrl, svc, "DeletePortMapping", body);
        }

        private static bool Soap(string ctrl, string svc, string action, string body)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(ctrl);
                req.Method = "POST";
                req.ContentType = "text/xml; charset=\"utf-8\"";
                req.Headers.Add("SOAPAction", $"\"{svc}#{action}\"");
                req.Timeout = 5000; req.ReadWriteTimeout = 5000;
                var data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (var rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
                using var resp = (HttpWebResponse)req.GetResponse();
                return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }
    }
}
