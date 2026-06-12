using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Steamworks;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Holds runtime connection settings.
    /// PlayerId is auto-detected from Steam on first run; settings persist in
    /// a JSON file inside the mod's own folder (official loader's
    /// ModRootPath) — the BepInEx config system is gone with the Mono port.
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

        // ── Tiny persisted key-value store (JSON in the mod folder) ───────────
        private static string _cfgPath = "";
        private static Dictionary<string, string> _cfg = new();

        private static string Get(string key, string def = "")
            => _cfg.TryGetValue(key, out var v) ? v : def;

        private static void Set(string key, string value)
        {
            _cfg[key] = value;
            try { File.WriteAllText(_cfgPath, JsonConvert.SerializeObject(_cfg, Formatting.Indented)); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Config] save: {ex.Message}"); }
        }

        /// <summary>Host-controlled minutes between coordinated MP autosaves.
        /// Read LIVE (so the host can retune mid-session without a restart).
        /// 0 = mirror the single-player autosave setting.</summary>
        public static int AutosaveMinutesLive()
        {
            try { return int.TryParse(Get("AutosaveMinutes", "0"), out var m) ? m : 0; }
            catch { return 0; }
        }

        /// <summary>The mod's install folder (ModsLocal\BigAmbitionsMP) — asset
        /// lookups (icons) read from here; replaces BepInEx.Paths.PluginPath.</summary>
        public static string ModRootPath { get; private set; } = ".";

        public static void Init(string modRootPath)
        {
            try
            {
                ModRootPath = modRootPath ?? ".";
                _cfgPath = Path.Combine(modRootPath ?? ".", "BigAmbitionsMP.cfg.json");
                if (File.Exists(_cfgPath))
                    _cfg = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_cfgPath))
                           ?? new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Config] load: {ex.Message}");
                _cfg = new Dictionary<string, string>();
            }

            HostIP = Get("HostIP", "127.0.0.1");
            Port   = int.TryParse(Get("Port", "7777"), out var p) ? p : 7777;

            StableId = ResolveStableId();
            Plugin.Logger.LogInfo($"[Config] Stable id: {StableId}");

            // Resolve the player ID: config override → Steam name → generated fallback
            var stored = Get("PlayerId").Trim();
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
        /// Persists the player name so subsequent launches pre-fill the panel.</summary>
        public static void SetRuntime(string playerId, string? hostIp, int port)
        {
            PlayerId = playerId;
            Port     = port;
            if (hostIp != null)
                HostIP = hostIp;

            // Re-resolve the stable id now that we're connecting — Steam is
            // reliably valid by this point, so an early GUID fallback upgrades
            // to the permanent SteamID64 (only if nothing was persisted yet).
            StableId = ResolveStableId();

            try
            {
                if (!string.IsNullOrWhiteSpace(playerId))
                {
                    Set("PlayerId", playerId);
                    if (hostIp != null) Set("HostIP", hostIp);
                    Set("Port", port.ToString());
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

        /// <summary>Resolve the immutable identity key — persisted id ALWAYS
        /// wins (see main-branch history: re-deriving flipped guid↔steam and
        /// orphaned progress).  IMPORTANT for the 0.10→0.11 migration: the old
        /// BepInEx cfg held the id; this fresh store mints a NEW one unless the
        /// migration shim below finds the old value.</summary>
        private static string ResolveStableId()
        {
            // 1. Already established here → reuse verbatim.
            var stored = Get("StableId").Trim();
            if (!string.IsNullOrEmpty(stored))
                return stored;

            // 1b. Migration shim: lift the id out of the old BepInEx config so
            //     0.10 progress (saves/ownership keyed by stable id) carries
            //     over.  Checked once; the value is then persisted HERE forever.
            var migrated = TryMigrateStableIdFromBepInEx();
            if (!string.IsNullOrEmpty(migrated))
            {
                Set("StableId", migrated!);
                Plugin.Logger.LogInfo($"[Config] Stable id migrated from BepInEx config: {migrated}");
                return migrated!;
            }

            // 2. First time only — mint one and persist for good.
            var steam = TryGetSteamId64();
            string gen = steam != null ? "steam-" + steam : "guid-" + Guid.NewGuid().ToString("N");
            Set("StableId", gen);
            Plugin.Logger.LogInfo($"[Config] Established stable id (persisted, permanent): {gen}");
            return gen;
        }

        private static string? TryMigrateStableIdFromBepInEx()
        {
            try
            {
                // Old location: <game>\BepInEx\config\BigAmbitionsMP.cfg — check
                // both known install roots (StableId = key in [Network]).
                string[] candidates =
                {
                    @"C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions\BepInEx\config\BigAmbitionsMP.cfg",
                    @"C:\BigAmbitions2\BepInEx\config\BigAmbitionsMP.cfg",
                    @"C:\BigAmbitions2-0.10-archive\BepInEx\config\BigAmbitionsMP.cfg",
                };
                foreach (var path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var t = line.Trim();
                        if (!t.StartsWith("StableId", StringComparison.OrdinalIgnoreCase)) continue;
                        int eq = t.IndexOf('=');
                        if (eq < 0) continue;
                        var v = t.Substring(eq + 1).Trim();
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Config] BepInEx migration: {ex.Message}"); }
            return null;
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
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
            return $"Player-{suffix}";
        }
    }
}
