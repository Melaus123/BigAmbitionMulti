using System;
using Steamworks;

namespace BigAmbitionsMP
{
    /// <summary>Steam-connect slice 2 stage C: friend invites + join-via-Steam.
    /// While hosting, rich presence advertises "connect" = "steam:&lt;hostId&gt;";
    /// friends see "Join Game" in their Steam friends list / can be invited from
    /// the overlay.  On the friend's side Steam delivers the connect string via
    /// OnGameRichPresenceJoinRequested (game running — warm) or the launch
    /// command line (Steam started the game — cold); both land in PendingJoinId,
    /// consumed by MPCanvasUI's per-frame tick on the main thread.</summary>
    public static class MPSteamPresence
    {
        private static bool _hooked;

        /// <summary>Join target awaiting the menu (0 = none).  Set here (Steam
        /// callback / cold start), consumed by MPCanvasUI.TickSteamJoin.</summary>
        public static ulong  PendingJoinId;
        public static string PendingJoinVia = "";

        /// <summary>Idempotent; called once SteamClient reports valid (probe tick).</summary>
        public static void EnsureHooked()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                SteamFriends.OnGameRichPresenceJoinRequested += (friend, connect) =>
                    QueueJoin(connect, $"invite/join from '{friend.Name}'");
                // Cold start: Steam launched the game because the player clicked
                // Join/an invite while the game was closed — the connect string
                // rides the launch command line.
                string cl = "";
                try { cl = SteamApps.CommandLine ?? ""; } catch { }
                if (!string.IsNullOrWhiteSpace(cl) && cl.Contains("steam:"))
                    QueueJoin(cl.Substring(cl.IndexOf("steam:", StringComparison.OrdinalIgnoreCase)), "launch command line");
                Plugin.Logger.LogInfo("[SteamJoin] rich-presence join hook installed.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] hook: {ex.Message}"); }
        }

        private static void QueueJoin(string connect, string via)
        {
            try
            {
                string s = (connect ?? "").Trim();
                int i = s.IndexOf("steam:", StringComparison.OrdinalIgnoreCase);
                if (i < 0) { Plugin.Logger.LogWarning($"[SteamJoin] unrecognized connect string '{connect}' ({via})."); return; }
                s = s.Substring(i + 6);
                // Take the leading digit run (command lines may append more args).
                int end = 0; while (end < s.Length && char.IsDigit(s[end])) end++;
                if (end == 0 || !ulong.TryParse(s.Substring(0, end), out ulong id) || id == 0)
                { Plugin.Logger.LogWarning($"[SteamJoin] bad SteamId in connect string '{connect}' ({via})."); return; }
                PendingJoinVia = via;
                PendingJoinId  = id;
                Plugin.Logger.LogInfo($"[SteamJoin] queued join → {id} ({via}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] queue: {ex.Message}"); }
        }

        /// <summary>Host is up — advertise so friends can Join/be invited.</summary>
        public static void AdvertiseHosting()
        {
            try
            {
                if (!SteamClient.IsValid) return;
                SteamFriends.SetRichPresence("connect", $"steam:{(ulong)SteamClient.SteamId}");
                SteamFriends.SetRichPresence("status", $"Hosting {MyPluginInfo.SHORT_NAME} multiplayer");
                Plugin.Logger.LogInfo("[SteamJoin] rich presence set — friends can Join Game.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] advertise: {ex.Message}"); }
        }

        public static void ClearAdvertise()
        { try { if (SteamClient.IsValid) SteamFriends.ClearRichPresence(); } catch { } }

        /// <summary>Lobby "Invite Steam Friends" — the overlay friends list;
        /// with rich presence set, friends get Join Game / invite options.</summary>
        public static void OpenFriendsOverlay()
        { try { SteamFriends.OpenOverlay("friends"); } catch (Exception ex) { Plugin.Logger.LogWarning($"[SteamJoin] overlay: {ex.Message}"); } }
    }
}
