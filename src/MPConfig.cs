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

        /// <summary>
        /// STABLE, immutable identity used as the key for persistent progress
        /// (saves + building ownership) — NOT the display name (PlayerId), which
        /// players can change.  Prefers the Steam account's SteamID64 (permanent);
        /// falls back to a per-machine GUID persisted in the config so it survives
        /// renames + restarts.  Namespaced ("steam-…" / "guid-…").
        /// </summary>
        public static string StableId { get; private set; } = "";

        private static ConfigEntry<string>? _playerIdEntry;
        private static ConfigEntry<string>? _stableIdEntry;
        private static ConfigEntry<int>?    _autosaveMinutesEntry;

        /// <summary>Host-controlled minutes between coordinated MP autosaves.
        /// Read LIVE (so the host can retune mid-session without a restart).
        /// 0 = mirror the single-player autosave setting.  Each save causes a
        /// brief stutter, so the host raises this if saves feel too frequent.</summary>
        public static int AutosaveMinutesLive()
        {
            try { return _autosaveMinutesEntry?.Value ?? 0; }
            catch { return 0; }
        }

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

            _autosaveMinutesEntry = config.Bind(
                "Multiplayer", "AutosaveMinutes", 0,
                "Minutes between host-driven multiplayer autosaves (host only).\n" +
                "0 = mirror your single-player autosave setting.\n" +
                "Each coordinated save causes a brief stutter, so raise this if\n" +
                "saves feel too frequent.  Takes effect immediately (no restart).");

            _stableIdEntry = config.Bind(
                "Network", "StableId", "",
                "Internal stable identity for save/ownership matching (auto-managed — do not edit).");
            StableId = ResolveStableId();
            Plugin.Logger.LogInfo($"[Config] Stable id: {StableId}");

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

            // Re-resolve the stable id now that we're connecting — Steam is
            // reliably valid by this point (the game launched through it), so we
            // upgrade from any early GUID fallback to the permanent SteamID64.
            StableId = ResolveStableId();

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

        // ── Stable identity resolution ────────────────────────────────────────

        /// <summary>Resolve the immutable identity key — the key for ALL persistent
        /// progress (saves + ownership).  PERMANENCE IS THE WHOLE POINT: once an id
        /// is established + persisted, it is reused forever and NEVER re-derived.
        ///
        /// The earlier logic always preferred "steam-"+SteamID64 over any persisted
        /// value, so the id flipped guid↔steam depending on whether Steam happened
        /// to be ready that run (Steam is flaky in the 2nd same-machine instance).
        /// That orphaned saved progress: saved as guid-…, looked up next run as
        /// steam-…, no match.  So: persisted id ALWAYS wins.</summary>
        private static string ResolveStableId()
        {
            // 1. Already established → reuse it verbatim, regardless of Steam state.
            var stored = _stableIdEntry?.Value?.Trim();
            if (!string.IsNullOrEmpty(stored))
                return stored;

            // 2. First time only — mint one (prefer the permanent, machine-portable
            //    SteamID64; else a per-machine GUID) and persist it for good.
            var steam = TryGetSteamId64();
            string gen = steam != null ? "steam-" + steam : "guid-" + Guid.NewGuid().ToString("N");
            try
            {
                if (_stableIdEntry != null)
                {
                    _stableIdEntry.Value = gen;
                    _stableIdEntry.ConfigFile?.Save();
                    Plugin.Logger.LogInfo($"[Config] Established stable id (persisted, permanent): {gen}");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Config] Could not persist stable id: {ex.Message}"); }
            return gen;
        }

        private static string? TryGetSteamId64()
        {
            try
            {
                if (!SteamClient.IsValid) return null;
                ulong v = SteamClient.SteamId.Value;
                if (v != 0UL) return v.ToString();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Config] Could not read SteamID64: {ex.Message}"); }
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
