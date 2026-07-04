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

        /// <summary>Ask the router (UPnP IGD) to forward UDP <paramref name="port"/>
        /// to <paramref name="localIp"/>.  Background + timeouts + caught; sets
        /// Upnp=Unsupported when no IGD answers, Failed on a router error.</summary>
        public static void TryForwardAsync(int port, string localIp)
        {
            if (_upnp == UpnpState.Trying || _upnp == UpnpState.Mapped) return;
            if (port <= 0 || string.IsNullOrEmpty(localIp)) { _upnp = UpnpState.Failed; return; }
            _upnp = UpnpState.Trying;
            Task.Run(() =>
            {
                try
                {
                    if (!Discover()) { _upnp = UpnpState.Unsupported; Plugin.Logger.LogInfo("[UPnP] No UPnP router found — manual port-forward needed."); return; }
                    if (AddPortMapping(port, localIp))
                    {
                        _mappedPort = port; _upnp = UpnpState.Mapped;
                        Plugin.Logger.LogInfo($"[UPnP] Forwarded UDP {port} -> {localIp} on the router.");
                    }
                    else { _upnp = UpnpState.Failed; Plugin.Logger.LogWarning("[UPnP] Router refused the port mapping."); }
                }
                catch (Exception ex) { _upnp = UpnpState.Failed; Plugin.Logger.LogWarning($"[UPnP] forward failed: {ex.Message}"); }
            });
        }

        /// <summary>Remove our mapping (host stop).  Best-effort; a leftover mapping
        /// is harmless if this can't run.</summary>
        public static void RemoveMappingAsync()
        {
            if (_upnp != UpnpState.Mapped) { _upnp = UpnpState.Idle; return; }
            int port = _mappedPort; string ctrl = _ctrlUrl, svc = _svcType;
            _upnp = UpnpState.Idle;
            Task.Run(() =>
            {
                try { if (port > 0 && !string.IsNullOrEmpty(ctrl)) DeletePortMapping(ctrl, svc, port); }
                catch { }
            });
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
