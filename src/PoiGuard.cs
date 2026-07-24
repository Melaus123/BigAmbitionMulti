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
                    int purged = 0;
                    try
                    {
                        var pois = InstanceBehavior<CityManager>.Instance?.cityMap?.pois;
                        if (pois != null) purged = pois.RemoveAll(p => p is not null && !p);
                    }
                    catch { }
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
    }
}
