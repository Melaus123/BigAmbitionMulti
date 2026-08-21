using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>Round-290 (field 20260820-092547, icejustice: "players can't see each other's
    /// ads on billboards"): billboard CAMPAIGNS live only in the owner's save —
    /// AdManager.GenerateAds builds each day's ad pools from the LOCAL RentedByPlayer
    /// registrations — so a partner's paid campaign never entered anyone else's pools;
    /// partners appeared, at most, as low-weight AI filler. This syncs each player's active
    /// billboard campaigns (absolute set: change-triggered send + slow heartbeat so joiners
    /// and lost packets converge without join plumbing) and injects them into the pools as
    /// PLAYER-weight ads. Rendering needs nothing new: the per-size billboard logo images
    /// already sync (BusinessSync) and partner logos already resolve through the patched
    /// logo path. Viewer-side this is cosmetic by construction — the campaign's customer
    /// effect runs on the owner's machine exactly as before.
    ///
    /// Cadence (user ruling 2026-08-21): ONCE A DAY is plenty for a cosmetic feature — and
    /// it matches the native rhythm, since AdManager only rebuilds its pools daily anyway.
    /// Sends fire on session entry, on a peer joining (the host re-ships every known set so
    /// the joiner never waits a day), and at most once per in-game day when the set actually
    /// changed. The 60s check below is a cheap gate, not a send cadence.</summary>
    internal static class BillboardAdSync
    {
        private const float CheckSeconds = 60f;   // cheap event gate — actual sends are entry/join/daily

        private static readonly object _lock = new();
        private static readonly Dictionary<string, List<(int type, string name)>> _partner = new();   // pid → entries (never our own)
        private static string _lastSent = "\0unsent";  // own set last shipped ("\0unsent" forces the first send)
        private static float  _nextCheckAt;
        private static int    _lastDaySeen = -1;
        private static bool   _kick = true;            // session entry / a peer joined — force one send pass

        /// <summary>HOST: a peer connected — re-ship the known sets on the next check.</summary>
        public static void NoteJoin() => _kick = true;

        /// <summary>MAIN THREAD, every frame (MPCanvasUI); does real work at most once a
        /// minute, and sends only on entry/join/day-change (see the class doc).</summary>
        public static void Tick()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected)
            {
                if (_lastSent != "\0unsent")
                { lock (_lock) _partner.Clear(); _lastSent = "\0unsent"; _lastDaySeen = -1; _kick = true; }
                return;
            }
            if (Time.unscaledTime < _nextCheckAt) return;
            _nextCheckAt = Time.unscaledTime + CheckSeconds;
            try
            {
                int day = -1; try { day = SaveGameManager.Current?.Day ?? -1; } catch { }
                bool dayRolled = day >= 0 && day != _lastDaySeen;
                if (!_kick && !dayRolled) return;
                _lastDaySeen = day;
                bool kicked = _kick; _kick = false;
                var mine = SnapshotOwnCampaigns();
                string key = string.Join(";", mine);
                if (!kicked && key == _lastSent) return;   // daily check, nothing changed — nothing to ship
                _lastSent = key;
                var p = new BillboardAdsPayload { PlayerId = MPConfig.PlayerId };
                p.Entries.AddRange(mine);
                if (MPServer.IsRunning)
                {
                    HostHandle(p, MPConfig.PlayerId, null);
                    if (kicked) ReshipStored();   // a joiner needs the OTHER members' sets too
                }
                else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BillboardAds, MPConfig.PlayerId, p));
                Plugin.Logger.LogInfo($"[AdSync] own billboard campaigns sent: {mine.Count} entr{(mine.Count == 1 ? "y" : "ies")} ({(kicked ? "entry/join" : "daily change")}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AdSync] tick: {ex.Message}"); }
        }

        /// <summary>HOST: rebroadcast every stored partner set — how a joiner converges.
        /// Absolute state, idempotent, tiny.</summary>
        private static void ReshipStored()
        {
            var all = new List<(string pid, List<(int type, string name)> list)>();
            lock (_lock) foreach (var kv in _partner) all.Add((kv.Key, new List<(int, string)>(kv.Value)));
            foreach (var (pid, list) in all)
            {
                var p = new BillboardAdsPayload { PlayerId = pid };
                foreach (var (t, n) in list) p.Entries.Add(t + "|" + n);
                MPServer.BroadcastAny(MessageEnvelope.Create(MessageType.BillboardAds, pid, p));
            }
        }

        /// <summary>The local player's active billboard campaigns as sorted "type|name"
        /// strings (a stable diff key). Live read of the authoritative save state.</summary>
        private static List<string> SnapshotOwnCampaigns()
        {
            var outp = new List<string>();
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return outp;
            foreach (var reg in gi.BuildingRegistrations)
            {
                if (reg == null || !reg.RentedByPlayer || string.IsNullOrEmpty(reg.BusinessName)) continue;
                var camps = reg.marketingCampaigns;
                if (camps == null) continue;
                foreach (var c in camps)
                {
                    if (c == null || !c.enabled) continue;
                    int t = (int)c.marketingTypeName;
                    if (t < (int)Entities.MarketingTypeName.SmallBillboard || t > (int)Entities.MarketingTypeName.LargeBillboard) continue;
                    string e = t + "|" + reg.BusinessName;
                    if (!outp.Contains(e)) outp.Add(e);
                }
            }
            outp.Sort(StringComparer.Ordinal);
            return outp;
        }

        /// <summary>HOST: store the sender's set (unless it is the host's own — those are
        /// native locally) and rebroadcast; the echo back to the sender is a no-op (Apply
        /// skips its own pid), the radio-sync convention.</summary>
        public static void HostHandle(BillboardAdsPayload p, string senderPid, MessageEnvelope env)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(senderPid)) return;
                if (p.Entries != null && p.Entries.Count > 64)
                { Plugin.Logger.LogWarning($"[AdSync] implausible set from '{senderPid}' ({p.Entries.Count} entries) — dropped."); return; }
                if (senderPid != MPConfig.PlayerId) Store(senderPid, p.Entries);
                MPServer.BroadcastAny(env ?? MessageEnvelope.Create(MessageType.BillboardAds, senderPid, p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AdSync] host handle: {ex.Message}"); }
        }

        /// <summary>CLIENT: a partner's set arrived (or our own echo — skipped).</summary>
        public static void Apply(BillboardAdsPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.PlayerId) || p.PlayerId == MPConfig.PlayerId) return;
                Store(p.PlayerId, p.Entries);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AdSync] apply: {ex.Message}"); }
        }

        private static void Store(string pid, List<string> entries)
        {
            var list = new List<(int, string)>();
            if (entries != null)
                foreach (var e in entries)
                {
                    if (string.IsNullOrEmpty(e)) continue;
                    int i = e.IndexOf('|');
                    if (i <= 0 || i >= e.Length - 1) continue;
                    if (!int.TryParse(e.Substring(0, i), out int t)) continue;
                    if (t < (int)Entities.MarketingTypeName.SmallBillboard || t > (int)Entities.MarketingTypeName.LargeBillboard) continue;
                    list.Add((t, e.Substring(i + 1)));
                }
            bool changed;
            lock (_lock)
            {
                _partner.TryGetValue(pid, out var prev);
                changed = prev == null || prev.Count != list.Count || !SameSet(prev, list);
                _partner[pid] = list;
            }
            if (changed)
            {
                Plugin.Logger.LogInfo($"[AdSync] partner '{pid}' billboard campaigns: {list.Count} entr{(list.Count == 1 ? "y" : "ies")} — regenerating the ad pools.");
                GameStatePatcher.EnqueueOnMainThread(RegenerateAdPools);
            }
        }

        private static bool SameSet(List<(int, string)> a, List<(int, string)> b)
        {
            for (int i = 0; i < a.Count; i++) if (!b.Contains(a[i])) return false;
            return true;
        }

        /// <summary>Every stored partner entry, flattened, for the pool-injection patch.</summary>
        internal static List<(int type, string name)> SnapshotPartners()
        {
            var outp = new List<(int, string)>();
            lock (_lock) foreach (var kv in _partner) outp.AddRange(kv.Value);
            return outp;
        }

        /// <summary>MAIN THREAD: rebuild the pools now so a newly arrived campaign shows
        /// without waiting for the next in-game day (GenerateAds is private → reflection;
        /// our injection postfix runs as part of the call).</summary>
        private static void RegenerateAdPools()
        {
            try
            {
                var am = InstanceBehavior<AdManager>.Instance;
                if (am == null) return;   // not in a world — the daily generation covers it later
                AccessTools.Method(typeof(AdManager), "GenerateAds")?.Invoke(am, null);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AdSync] regenerate: {ex.Message}"); }
        }
    }

    /// <summary>After the native daily pool build, append every partner campaign as a
    /// PLAYER-weight ad. Patching GenerateAds covers every entry point — RunDaily and the
    /// lazy rebuilds inside RequestAd.</summary>
    [HarmonyPatch(typeof(AdManager), "GenerateAds")]
    public static class Patch_AdManager_GenerateAds_PartnerAds
    {
        static void Postfix(AdManager __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var entries = BillboardAdSync.SnapshotPartners();
                if (entries.Count == 0) return;
                var cycles  = AccessTools.Field(typeof(AdManager), "_adCycles").GetValue(__instance)
                                  as Dictionary<Entities.MarketingTypeName, List<AdManager.AdSettings>>;
                var weights = AccessTools.Field(typeof(AdManager), "_adWeights").GetValue(__instance)
                                  as Dictionary<Entities.MarketingTypeName, List<float>>;
                if (cycles == null || weights == null) return;
                int added = 0;
                foreach (var (type, name) in entries)
                {
                    var key = (Entities.MarketingTypeName)type;
                    if (!cycles.TryGetValue(key, out var pool) || !weights.TryGetValue(key, out var w)) continue;
                    pool.Add(new AdManager.AdSettings { businessName = name, isPlayerAd = true });
                    w.Add(__instance.playerWeight);
                    added++;
                }
                if (added > 0) Plugin.Logger.LogInfo($"[AdSync] injected {added} partner billboard ad(s) into the daily pools.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AdSync] inject: {ex.Message}"); }
        }
    }
}
