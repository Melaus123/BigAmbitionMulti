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
        public sealed class BugReportDiscordTag
        {
            public string Label = "";
            public string Id = "";
        }

        public static string PlayerId { get; private set; } = "Player1";
        public static string HostIP   { get; private set; } = "127.0.0.1";
        public static int    Port     { get; private set; } = 7777;

        private static string? _cachedLanIp;
        /// <summary>This machine's best-guess LAN IPv4 — the address other players on
        /// the SAME network use to join (e.g. 192.168.x.x).  Detected locally from the
        /// network adapters; makes NO external calls.  Returns "" if none is found
        /// (callers fall back to the configured HostIP).  For internet play the host
        /// still needs their PUBLIC ip + a forwarded port; this is the LAN address.</summary>
        public static string LocalLanIp()
        {
            if (_cachedLanIp != null) return _cachedLanIp;
            _cachedLanIp = "";
            try
            {
                // SCORE candidates rather than taking the first match: a machine with
                // a VPN / VirtualBox / Hamachi / WSL adapter would otherwise hand out
                // that adapter's address instead of the real Wi-Fi/Ethernet LAN IP.
                string best = ""; int bestScore = int.MinValue;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue;   // no route → not a usable LAN adapter
                    string nm = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    if (nm.Contains("virtual") || nm.Contains("vmware") || nm.Contains("virtualbox")
                        || nm.Contains("hyper-v") || nm.Contains("hamachi") || nm.Contains("zerotier")
                        || nm.Contains("tailscale") || nm.Contains("vpn") || nm.Contains("wsl")
                        || nm.Contains("docker") || nm.Contains("pseudo") || nm.Contains("tap-")
                        || nm.Contains("tunnel")) continue;   // skip virtual/VPN adapters by name
                    foreach (var ua in props.UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(a)) continue;
                        int score = 0;
                        if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) score += 3;
                        else if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) score += 2;
                        var b = a.GetAddressBytes();
                        if (b[0] == 192 && b[1] == 168) score += 2;                       // typical home LAN
                        else if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) score += 1;
                        else if (b[0] == 10) score += 1;
                        if (score > bestScore) { bestScore = score; best = a.ToString(); }
                    }
                }
                _cachedLanIp = best;
            }
            catch { }
            return _cachedLanIp;
        }

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

        /// <summary>The mod's install folder (ModsLocal\BigAmbitionsMP or the Steam
        /// Workshop item dir) — STATIC CONTENT ONLY (icons, preview).  Never write
        /// here: Workshop installs are Steam-managed and re-validated on update.</summary>
        public static string ModRootPath { get; private set; } = ".";

        /// <summary>Per-user runtime-data root (config, ring logs, bug reports):
        /// LocalLow\Hovgaard Games\Big Ambitions\BigAmbitionsMP.  Survives Steam
        /// Workshop updates and never rides a Workshop upload.  Set in Init;
        /// falls back to ModRootPath only if persistentDataPath is unavailable.</summary>
        public static string DataRootPath { get; private set; } = ".";
        public static string ConfigPath => _cfgPath;

        public static string BugReportDiscordWebhookUrlLive()
        {
            try
            {
                return GetLiveString("BugReportDiscordWebhookUrl").Trim();
            }
            catch { return ""; }
        }

        /// <summary>The bug-report RELAY endpoint — a Cloudflare Worker that holds the Discord
        /// webhook server-side, so the webhook is NEVER shipped in the mod.  Public URL, safe to
        /// bake in; override with "BugReportRelayUrl" in config.  Reports POST here by default;
        /// a direct "BugReportDiscordWebhookUrl" (maintainer testing) takes precedence when set.</summary>
        public const string DefaultBugReportRelayUrl = "https://bamp-bug-relay.allscott16.workers.dev";
        public static string BugReportRelayUrlLive()
        {
            try { var v = GetLiveString("BugReportRelayUrl").Trim(); return v.Length > 0 ? v : DefaultBugReportRelayUrl; }
            catch { return DefaultBugReportRelayUrl; }
        }

        /// <summary>Optional shared key sent as the X-BAMP-Key header to the relay (matches the
        /// Worker's RELAY_KEY secret).  Obfuscation only — it ships in the mod.</summary>
        public static string BugReportRelayKeyLive()
        {
            try { return GetLiveString("BugReportRelayKey").Trim(); }
            catch { return ""; }
        }

        public static string BugReportDiscordCrashTagIdLive()
        {
            try { return CleanDiscordTagId(GetLiveString("BugReportDiscordCrashTagId")); }
            catch { return ""; }
        }

        public static IReadOnlyList<BugReportDiscordTag> BugReportDiscordBugTagsLive()
        {
            var tags = new List<BugReportDiscordTag>();
            try
            {
                string raw = GetLiveString("BugReportDiscordBugTags");
                if (string.IsNullOrWhiteSpace(raw)) return tags;

                foreach (var part in raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string item = part.Trim();
                    if (item.Length == 0) continue;

                    string label;
                    string id;
                    int eq = item.IndexOf('=');
                    if (eq >= 0)
                    {
                        label = item.Substring(0, eq).Trim();
                        id = item.Substring(eq + 1).Trim();
                    }
                    else
                    {
                        label = "Bug";
                        id = item;
                    }

                    id = CleanDiscordTagId(id);
                    if (id.Length == 0) continue;
                    if (label.Length == 0) label = "Bug";
                    tags.Add(new BugReportDiscordTag { Label = label, Id = id });
                }
            }
            catch { }
            return tags;
        }

        public static bool AllowBugReportCrashTestLive()
        {
            try
            {
                string v = GetLiveString("AllowBugReportCrashTest");
                return v.Equals("true", StringComparison.OrdinalIgnoreCase)
                       || v.Equals("1", StringComparison.OrdinalIgnoreCase)
                       || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string GetLiveString(string key)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_cfgPath) && File.Exists(_cfgPath))
                {
                    var live = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_cfgPath));
                    if (live != null && live.TryGetValue(key, out var v))
                        return v ?? "";
                }
            }
            catch { }
            return Get(key);
        }

        private static string CleanDiscordTagId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            value = value.Trim();
            var chars = new List<char>(value.Length);
            foreach (char c in value)
                if (char.IsDigit(c)) chars.Add(c);
            return new string(chars.ToArray());
        }

        /// <summary>The GAME install root this process runs from (e.g.
        /// "C:\BigAmbitions2").  ModsLocal is SHARED by every install
        /// (persistentDataPath), so identity must key off this instead.</summary>
        public static string GameRootPath { get; private set; } = "";

        /// <summary>Filename-safe key for this game install ("Big_Ambitions",
        /// "BigAmbitions2", …) — suffixes the cfg file so each install keeps its
        /// OWN identity inside the shared ModsLocal folder.  Without this, host
        /// and client instances read one cfg = same PlayerId + same StableId
        /// (name and save-slot collisions; 0.10 had per-install BepInEx cfgs).</summary>
        private static string InstallKey()
        {
            try
            {
                var name = Path.GetFileName(GameRootPath.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) return "default";
                foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                return name.Replace(' ', '_');
            }
            catch { return "default"; }
        }

        public static void Init(string modRootPath)
        {
            try
            {
                ModRootPath  = modRootPath ?? ".";
                // dataPath = "<gameRoot>/Big Ambitions_Data"
                GameRootPath = Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? "";
                // Runtime data (config, ring logs, bug reports) lives OUTSIDE the mod
                // folder (Workshop readiness): on a Workshop install the mod folder is
                // Steam-managed content — updates re-validate/clobber it, and a config
                // wipe would destroy StableId (the player's save identity).  The
                // in-game Workshop upload also ships the WHOLE selected folder, so
                // runtime logs/configs must never live in it.  persistentDataPath is
                // per-user and update-safe; per-instance config filenames already
                // disambiguate the two local game installs via InstallKey.
                try
                {
                    DataRootPath = Path.Combine(UnityEngine.Application.persistentDataPath, "BigAmbitionsMP");
                    Directory.CreateDirectory(DataRootPath);
                }
                catch (Exception dx)
                {
                    DataRootPath = ModRootPath;
                    Plugin.Logger.LogWarning($"[Config] data root unavailable ({dx.Message}) — falling back to the mod folder.");
                }
                _cfgPath = Path.Combine(DataRootPath, $"BigAmbitionsMP.cfg.{InstallKey()}.json");
                // One-time migration: pre-0.1.11 the config lived in the mod folder.
                // MOVE it — StableId must survive or the player returns as a stranger.
                try
                {
                    string legacy = Path.Combine(ModRootPath, $"BigAmbitionsMP.cfg.{InstallKey()}.json");
                    if (!File.Exists(_cfgPath) && File.Exists(legacy))
                    {
                        File.Move(legacy, _cfgPath);
                        Plugin.Logger.LogInfo("[Config] migrated config out of the mod folder (Workshop-safe data root).");
                    }
                }
                catch (Exception mx) { Plugin.Logger.LogWarning($"[Config] config migration: {mx.Message}"); }
                Plugin.Logger.LogInfo($"[Config] install '{InstallKey()}' (root '{GameRootPath}') → cfg '{Path.GetFileName(_cfgPath)}' in '{DataRootPath}'.");
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

            // Resolve the player ID: config override → old BepInEx cfg ("Host"/
            // "Client1" — keeps lobby identity continuity) → Steam name → fallback
            var stored = Get("PlayerId").Trim();
            if (string.IsNullOrEmpty(stored) || stored == "Player1")
            {
                TryMigrateFromBepInEx(out _, out var oldName);
                if (!string.IsNullOrEmpty(oldName))
                {
                    PlayerId = oldName!;
                    Set("PlayerId", PlayerId);
                    Plugin.Logger.LogInfo($"[Config] Player name migrated from BepInEx cfg: {PlayerId}");
                }
                else
                {
                    PlayerId = DetectPlayerName();
                    Plugin.Logger.LogInfo($"[Config] Auto-detected player name: {PlayerId}");
                }
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

            // 1b. Migration shim: lift the id out of THIS INSTALL's old BepInEx
            //     config so 0.10 progress (saves/ownership keyed by stable id)
            //     carries over.  Checked once; then persisted HERE forever.
            TryMigrateFromBepInEx(out var migrated, out _);
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

        /// <summary>Read StableId + PlayerId out of THIS install's old BepInEx
        /// config (com.bigambitions.multiplayer.cfg — the plugin GUID name, NOT
        /// "BigAmbitionsMP.cfg"; the first migration shim looked for the wrong
        /// filename and silently minted a fresh id).  For the second install the
        /// 0.11 robocopy mirror overwrote its BepInEx folder with the HOST's
        /// copy, so the 0.10 archive holds the true client identity and is
        /// checked FIRST.</summary>
        private static void TryMigrateFromBepInEx(out string? stableId, out string? playerId)
        {
            stableId = null; playerId = null;
            try
            {
                var candidates = new List<string>();
                if (GameRootPath.TrimEnd('\\', '/').EndsWith("BigAmbitions2", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(@"C:\BigAmbitions2-0.10-archive\BepInEx\config\com.bigambitions.multiplayer.cfg");
                if (!string.IsNullOrEmpty(GameRootPath))
                    candidates.Add(Path.Combine(GameRootPath, @"BepInEx\config\com.bigambitions.multiplayer.cfg"));

                foreach (var path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("#") || t.StartsWith("[")) continue;
                        int eq = t.IndexOf('=');
                        if (eq < 0) continue;
                        var k = t.Substring(0, eq).Trim();
                        var v = t.Substring(eq + 1).Trim();
                        if (string.IsNullOrEmpty(v)) continue;
                        if (k.Equals("StableId", StringComparison.OrdinalIgnoreCase)) stableId ??= v;
                        if (k.Equals("PlayerId", StringComparison.OrdinalIgnoreCase)) playerId ??= v;
                    }
                    if (stableId != null || playerId != null)
                    {
                        Plugin.Logger.LogInfo($"[Config] BepInEx migration source: {path}");
                        return;   // one source only — never mix installs
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Config] BepInEx migration: {ex.Message}"); }
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
