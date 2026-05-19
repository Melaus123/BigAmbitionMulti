using BepInEx.Configuration;
using Steamworks;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Holds runtime connection settings.
    /// PlayerId is auto-detected from Steam on first run; all other settings
    /// are read from the BepInEx config file and surfaced in the in-game UI.
    /// </summary>
    public static class MPConfig
    {
        public static string PlayerId { get; private set; } = "Player1";
        public static string HostIP   { get; private set; } = "127.0.0.1";
        public static int    Port     { get; private set; } = 7777;

        private static ConfigEntry<string>? _playerIdEntry;

        public static void Init(ConfigFile config)
        {
            // Empty default signals "auto-detect from Steam".
            // Old installs that still have "Player1" are also treated as unset.
            _playerIdEntry = config.Bind(
                "Network", "PlayerId", "",
                "Your display name in multiplayer.\n" +
                "Leave blank to use your Steam username (recommended).\n" +
                "Set a custom value here only if you want to override it.");

            HostIP = config.Bind("Network", "HostIP", "127.0.0.1",
                "Default host IP shown in the join panel.").Value;

            Port = config.Bind("Network", "Port", 7777,
                "Default port shown in the host/join panels.").Value;

            // Resolve the player ID: config override → Steam name → generated fallback
            var stored = _playerIdEntry.Value?.Trim();
            if (string.IsNullOrEmpty(stored) || stored == "Player1")
            {
                PlayerId = DetectPlayerName();
                Plugin.Logger.LogInfo($"[Config] Auto-detected player name: {PlayerId}");
            }
            else
            {
                PlayerId = stored;
                Plugin.Logger.LogInfo($"[Config] Using configured player name: {PlayerId}");
            }
        }

        /// <summary>Called by the UI when the user clicks Host or Join.
        /// Persists the player name to the BepInEx config so subsequent launches
        /// pre-fill the F8 panel with the last-used name.</summary>
        public static void SetRuntime(string playerId, string? hostIp, int port)
        {
            PlayerId = playerId;
            Port     = port;
            if (hostIp != null)
                HostIP = hostIp;

            // Persist the name across sessions (#1).  Explicit ConfigFile.Save()
            // because BepInEx 6 BE doesn't always flush the auto-save before a
            // hard process exit (e.g. user clicks the window X).
            try
            {
                if (_playerIdEntry != null && !string.IsNullOrWhiteSpace(playerId))
                {
                    _playerIdEntry.Value = playerId;
                    _playerIdEntry.ConfigFile?.Save();
                    Plugin.Logger.LogInfo($"[Config] Persisted PlayerId='{playerId}' to disk.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Config] Could not persist PlayerId: {ex.Message}");
            }
        }

        // ── Name resolution ───────────────────────────────────────────────────

        private static string DetectPlayerName()
        {
            var steamName = TryGetSteamName();
            if (steamName != null)
                return steamName;

            // Steam not ready yet — generate a stable random name.
            // We intentionally do NOT write it back to config so the next launch
            // will try Steam again; if Steam still fails, a new random name is used.
            return GenerateFallbackName();
        }

        private static string? TryGetSteamName()
        {
            try
            {
                if (!SteamClient.IsValid)
                {
                    Plugin.Logger.LogWarning("[Config] Steam client not valid yet — cannot read username.");
                    return null;
                }

                var name = SteamClient.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Config] Could not read Steam username: {ex.Message}");
            }
            return null;
        }

        private static string GenerateFallbackName()
        {
            // Short random suffix so two people who both fail Steam lookup
            // don't collide with each other.
            var suffix = Guid.NewGuid().ToString("N")[..6].ToUpper();
            return $"Player-{suffix}";
        }
    }
}
