using System;
using Steamworks;

namespace BigAmbitionsMP
{
    /// <summary>Steam-connect slice 2 stage C (lobby-based, standard practice —
    /// user ruling 2026-07-10): hosting creates a friends-only Steam LOBBY used
    /// purely as an invite rendezvous — session traffic stays on the relay/UDP
    /// transports.  "Invite Friends" opens Steam's NATIVE invite dialog
    /// (OpenGameInviteOverlay needs a lobby — the friends-list overlay fallback
    /// was the pre-lobby stopgap).  Accepting an invite / Join Game delivers the
    /// lobby via OnGameLobbyJoinRequested (game running) or "+connect_lobby
    /// &lt;id&gt;" on the launch command line (Steam started the game); we join the
    /// lobby just long enough to read the host's SteamId from its data, leave,
    /// and relay-connect.  Rich presence "connect" stays set as well, so
    /// right-click → Join Game works even without an explicit invite.</summary>
    public static class MPSteamPresence
    {
        private const string LobbyHostKey = "bamp_host";

        private static bool _hooked;
        private static Steamworks.Data.Lobby? _lobby;   // live while hosting

        /// <summary>Join target awaiting the menu (0 = none).  Set here, consumed
        /// on the main thread by MPCanvasUI.TickSteamJoin.</summary>
        public static ulong  PendingJoinId;
        public static string PendingJoinVia = "";

        /// <summary>Idempotent; called once SteamClient reports valid (probe tick).</summary>
        public static void EnsureHooked()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                SteamFriends.OnGameLobbyJoinRequested += (lobby, friendId) =>
                    ResolveLobby(lobby, $"lobby invite/join via '{friendId}'");
                SteamFriends.OnGameRichPresenceJoinRequested += (friend, connect) =>
                    QueueJoin(connect, $"rich-presence join from '{friend.Name}'");
                // Cold start: Steam launched the game from an invite/Join click.
                string cl = "";
                try { cl = SteamApps.CommandLine ?? ""; } catch { }
                int li = cl.IndexOf("+connect_lobby", StringComparison.OrdinalIgnoreCase);
                if (li >= 0 && ulong.TryParse(LeadingDigits(cl.Substring(li + 14)), out ulong lobbyId) && lobbyId != 0)
                    JoinLobbyById(lobbyId, "launch command line (+connect_lobby)");
                else if (cl.IndexOf("steam:", StringComparison.OrdinalIgnoreCase) >= 0)
                    QueueJoin(cl.Substring(cl.IndexOf("steam:", StringComparison.OrdinalIgnoreCase)), "launch command line");
                Plugin.Logger.LogInfo("[SteamJoin] lobby + rich-presence join hooks installed.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] hook: {ex.Message}"); }
        }

        /// <summary>Host is up — create the invite lobby + advertise presence.</summary>
        public static async void AdvertiseHosting()
        {
            try
            {
                if (!SteamClient.IsValid) return;
                SteamFriends.SetRichPresence("connect", $"steam:{(ulong)SteamClient.SteamId}");
                SteamFriends.SetRichPresence("status", $"Hosting {MyPluginInfo.SHORT_NAME} multiplayer");
                var created = await SteamMatchmaking.CreateLobbyAsync(16);
                if (created is { } lobby)
                {
                    lobby.SetFriendsOnly();
                    lobby.SetJoinable(true);
                    lobby.SetData(LobbyHostKey, ((ulong)SteamClient.SteamId).ToString());
                    _lobby = lobby;
                    Plugin.Logger.LogInfo($"[SteamJoin] invite lobby up ({lobby.Id}) — friends can Join Game / be invited.");
                }
                else Plugin.Logger.LogWarning("[SteamJoin] lobby create failed — invites limited to right-click Join Game.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] advertise: {ex.Message}"); }
        }

        public static void ClearAdvertise()
        {
            try { _lobby?.Leave(); } catch { }
            _lobby = null;
            try { if (SteamClient.IsValid) SteamFriends.ClearRichPresence(); } catch { }
        }

        /// <summary>Lobby "Invite Friends" — Steam's native invite dialog when the
        /// lobby is up; overlay friends list as the degraded fallback.</summary>
        public static void OpenInviteDialog()
        {
            try
            {
                if (_lobby is { } lobby) { SteamFriends.OpenGameInviteOverlay(lobby.Id); return; }
                Plugin.Logger.LogWarning("[SteamJoin] no invite lobby (create failed / not hosting) — opening friends list instead.");
                SteamFriends.OpenOverlay("friends");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] invite dialog: {ex.Message}"); }
        }

        // ── friend side ──────────────────────────────────────────────────────

        private static async void ResolveLobby(Steamworks.Data.Lobby lobby, string via)
        {
            try
            {
                var enter = await lobby.Join();
                if (enter != RoomEnter.Success)
                { Plugin.Logger.LogWarning($"[SteamJoin] lobby join failed ({enter}) — {via}."); return; }
                string hostStr = lobby.GetData(LobbyHostKey) ?? "";
                try { lobby.Leave(); } catch { }   // rendezvous only — the session runs on the relay
                if (!ulong.TryParse(hostStr, out ulong hostId) || hostId == 0)
                { Plugin.Logger.LogWarning($"[SteamJoin] lobby {lobby.Id} carries no {LobbyHostKey} — not a {MyPluginInfo.SHORT_NAME} lobby? ({via})."); return; }
                PendingJoinVia = via;
                PendingJoinId  = hostId;
                Plugin.Logger.LogInfo($"[SteamJoin] queued join → {hostId} ({via}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] resolve lobby: {ex.Message}"); }
        }

        private static async void JoinLobbyById(ulong lobbyId, string via)
        {
            try
            {
                var joined = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
                if (joined is { } lobby) ResolveJoined(lobby, via);
                else Plugin.Logger.LogWarning($"[SteamJoin] could not join lobby {lobbyId} ({via}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] join lobby {lobbyId}: {ex.Message}"); }
        }

        private static void ResolveJoined(Steamworks.Data.Lobby lobby, string via)
        {
            string hostStr = lobby.GetData(LobbyHostKey) ?? "";
            try { lobby.Leave(); } catch { }
            if (!ulong.TryParse(hostStr, out ulong hostId) || hostId == 0)
            { Plugin.Logger.LogWarning($"[SteamJoin] lobby {lobby.Id} carries no {LobbyHostKey} ({via})."); return; }
            PendingJoinVia = via;
            PendingJoinId  = hostId;
            Plugin.Logger.LogInfo($"[SteamJoin] queued join → {hostId} ({via}).");
        }

        private static void QueueJoin(string connect, string via)
        {
            try
            {
                string s = (connect ?? "").Trim();
                int i = s.IndexOf("steam:", StringComparison.OrdinalIgnoreCase);
                if (i < 0) { Plugin.Logger.LogWarning($"[SteamJoin] unrecognized connect string '{connect}' ({via})."); return; }
                if (!ulong.TryParse(LeadingDigits(s.Substring(i + 6)), out ulong id) || id == 0)
                { Plugin.Logger.LogWarning($"[SteamJoin] bad SteamId in connect string '{connect}' ({via})."); return; }
                PendingJoinVia = via;
                PendingJoinId  = id;
                Plugin.Logger.LogInfo($"[SteamJoin] queued join → {id} ({via}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] queue: {ex.Message}"); }
        }

        private static string LeadingDigits(string s)
        {
            s = s.TrimStart();
            int end = 0; while (end < s.Length && char.IsDigit(s[end])) end++;
            return s.Substring(0, end);
        }
    }
}
