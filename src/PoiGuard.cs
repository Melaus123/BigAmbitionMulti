using System;
using HarmonyLib;
using Streets;   // Address.ToFormattedString / IsUndefined
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Permanent field guard (round-84, user-approved): dead map-pin trap + self-heal.
    ///
    /// Mechanism (read from native): UI.PermanentPointsOfInterest.HandlePermanentPOIs iterates a
    /// CACHED array of Permanent pins, rebuilt only when UpdatePermanentPointsOfInterest() sets the
    /// flag. A pin destroyed without that refresh NREs the loop EVERY FRAME forever — and a rebuild
    /// alone can't purge it, because the corpse also still sits in cityMap.pois (its managed
    /// .Permanent stays readable). KILOKEN 20260724-170745: the report's entire 4MB Player.log
    /// window was this one NRE — the field evidence for the actual bug was erased. Never reproduced
    /// on the dev rig (all local + archived logs clean), so this trap is the ONLY realistic way to
    /// name the destroyer: the corpse's managed identity (targetAddress/flags) survives destruction.
    ///
    /// Guards against self-inflicted spam (user-reviewed):
    ///   * heal (purge + rebuild) runs ONLY when dead pins were actually found — an unrelated
    ///     failure in the same method never triggers rebuild loops;
    ///   * logging: first 5 events in full detail, then at most one line per 60s with a running
    ///     event counter — the counter itself discriminates one-off poisoning from a hot loop.
    /// The exception is always suppressed after logging: the native status quo is an exception
    /// per frame that Unity swallows into the log — strictly worse than one counted WARN.
    /// </summary>
    [HarmonyPatch(typeof(UI.PermanentPointsOfInterest), nameof(UI.PermanentPointsOfInterest.HandlePermanentPOIs))]
    public static class PoiGuard
    {
        private static System.Reflection.FieldInfo? _cacheField;
        private static int _eventCount;
        private static float _nextVerboseLog;

        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return __exception;   // SP: vanilla behavior
            try
            {
                _eventCount++;
                bool verbose = _eventCount <= 5 || Time.unscaledTime >= _nextVerboseLog;
                if (verbose) _nextVerboseLog = Time.unscaledTime + 60f;

                // 1. IDENTIFY — walk the cached permanent-pin array. A destroyed pin is Unity
                //    fake-null: the managed shell is alive, so its plain managed fields
                //    (targetAddress, isGuider, hidden) are still readable — the corpse names itself.
                int dead = 0;
                _cacheField ??= AccessTools.Field(typeof(UI.PermanentPointsOfInterest), "_permanentPointsOfInterest");
                if (_cacheField?.GetValue(null) is PointOfInterest[] cache)
                {
                    foreach (var pin in cache)
                    {
                        if (pin is null || pin) continue;   // managed-null or still alive → not a corpse
                        dead++;
                        if (verbose)
                        {
                            string addr = "<none>";
                            try
                            {
                                if (pin.targetAddress is not null && !pin.targetAddress.IsUndefined())
                                    addr = pin.targetAddress.ToFormattedString();
                            }
                            catch { }
                            Plugin.Logger.LogWarning(
                                $"[PoiGuard] DEAD PIN in permanent map cache: targetAddress='{addr}' "
                                + $"isGuider={pin.isGuider} hidden={pin.hidden} (event #{_eventCount}).");
                        }
                    }
                }

                // 2. HEAL — only when corpses were actually found (rebuilds can't fix anything
                //    else, and rebuilding on unrelated failures would loop). Purge the corpses
                //    from the master list, then queue the game's own cache rebuild.
                if (dead > 0)
                {
                    int purged = PurgeMasterList();
                    UI.PermanentPointsOfInterest.UpdatePermanentPointsOfInterest();
                    if (verbose)
                        Plugin.Logger.LogWarning(
                            $"[PoiGuard] healed: {dead} dead cached pin(s), {purged} purged from the master list, "
                            + $"rebuild queued (event #{_eventCount}). Per-frame NRE spam suppressed.");
                }
                else if (verbose)
                {
                    Plugin.Logger.LogWarning(
                        $"[PoiGuard] HandlePermanentPOIs threw but NO dead pins found — different failure "
                        + $"(event #{_eventCount}): {__exception.GetType().Name}: {__exception.Message}");
                }
            }
            catch { }
            return null;
        }

        /// <summary>Round-198: purge TRUE nulls AND Unity-dead corpses from cityMap.pois.
        /// (Round-84 purged corpses only; field 20260730-221621 proved true nulls exist and
        /// NRE the map's own iteration paths.) Logs each corpse's surviving identity so a
        /// field log can finally name the null-producer.</summary>
        internal static int PurgeMasterList()
        {
            int purged = 0;
            try
            {
                var pois = InstanceBehavior<CityManager>.Instance?.cityMap?.pois;
                if (pois == null) return 0;
                for (int i = pois.Count - 1; i >= 0; i--)
                {
                    var p = pois[i];
                    if (p is null) { pois.RemoveAt(i); purged++; continue; }
                    if (!p)
                    {
                        string addr = "<none>";
                        try { if (p.targetAddress is not null && !p.targetAddress.IsUndefined()) addr = p.targetAddress.ToFormattedString(); } catch { }
                        Plugin.Logger.LogWarning($"[PoiGuard] purging dead pin from master list: targetAddress='{addr}' isGuider={p.isGuider} hidden={p.hidden}.");
                        pois.RemoveAt(i); purged++;
                    }
                }
            }
            catch { }
            return purged;
        }
    }

    /// <summary>
    /// Round-198 (field 20260730-221621, 'frozen after subway with cart'): CityMap's OWN two
    /// iteration paths had no guard. A null/dead entry in cityMap.pois:
    ///   * NREd CityMap.LateUpdate every frame (1,556 in one field log — the round-84 log-erasure
    ///     class on a different cache), and
    ///   * NREd CityMap.TogglePois inside the map-CLOSE coroutine, killing it one step BEFORE
    ///     UnsetNavigationBlocker(Map) — the subway ride then waits forever on the dead close:
    ///     Map + Subway blockers stranded, game stays paused, total movement lock.
    /// Heal = purge the bad entries, queue both cache rebuilds, SUPPRESS the throw — for
    /// TogglePois suppression lets the close coroutine CONTINUE to the blocker release, which is
    /// the actual un-freeze. MP-gated; SP keeps vanilla behavior.
    /// </summary>
    [HarmonyPatch(typeof(CityMap), "LateUpdate")]
    public static class PoiGuard_CityMapLateUpdate
    {
        private static System.Reflection.FieldInfo? _rebuildFlag;
        private static int _eventCount;
        private static float _nextVerboseLog;

        static Exception? Finalizer(CityMap __instance, Exception __exception)
        {
            if (__exception == null) return null;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return __exception;
            try
            {
                _eventCount++;
                bool verbose = _eventCount <= 5 || Time.unscaledTime >= _nextVerboseLog;
                if (verbose) _nextVerboseLog = Time.unscaledTime + 60f;
                int purged = PoiGuard.PurgeMasterList();
                try
                {
                    _rebuildFlag ??= AccessTools.Field(typeof(CityMap), "_requirePOIRebuild");
                    _rebuildFlag?.SetValue(__instance, true);   // rebuild _pointOfInterests from the now-clean master list
                }
                catch { }
                UI.PermanentPointsOfInterest.UpdatePermanentPointsOfInterest();
                if (verbose)
                    Plugin.Logger.LogWarning($"[PoiGuard] CityMap.LateUpdate threw ({__exception.GetType().Name}) — purged {purged} bad pin(s), caches queued for rebuild (event #{_eventCount}).");
            }
            catch { }
            return null;
        }
    }

    [HarmonyPatch(typeof(CityMap), "TogglePois")]
    public static class PoiGuard_CityMapTogglePois
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return __exception;
            try
            {
                int purged = PoiGuard.PurgeMasterList();
                Plugin.Logger.LogWarning($"[PoiGuard] CityMap.TogglePois threw ({__exception.GetType().Name}) — purged {purged} bad pin(s); throw suppressed so the map close reaches its blocker release (round-198).");
            }
            catch { }
            return null;
        }
    }
}
