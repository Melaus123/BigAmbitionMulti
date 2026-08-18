using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Sweep batch 12 (2026-08-18, user-approved): guards around native code that
    /// throws in MP-only states.  DESIGN (user ruling): every guard REPORTS when it fires,
    /// classified two ways —
    ///
    ///  * PARITY guards protect native code from states that are NORMAL in MP but impossible
    ///    solo (a remote player's collider in a trigger volume, a remote player in a chair).
    ///    Firing is expected life: logged once per launch per site (with a running count on
    ///    later summaries), and does NOT indicate a deeper bug.
    ///
    ///  * TRIPWIRE guards protect against states that should not exist at all.  EVERY firing
    ///    is evidence of an unrooted upstream defect: logged WARN with context, throttled per
    ///    site.  The guard keeps the session alive; the line is the case file for rooting the
    ///    real cause.  A guard that absorbed silently would be a bandaid hiding its own
    ///    evidence.
    ///
    /// All guards pass exceptions through untouched outside MP (single-player behavior is
    /// native, unguarded — these states cannot occur there and masking a real SP bug would
    /// be wrong).</summary>
    internal static class NativeGuardReport
    {
        internal static bool InMp => MPServer.IsRunning || MPClient.InMpGame;

        private static readonly System.Collections.Generic.Dictionary<string, int>   _count  = new();
        private static readonly System.Collections.Generic.Dictionary<string, float> _nextAt = new();

        /// <summary>Throttled, counted report. Parity sites use a long interval (fire-once
        /// feel); tripwires a short one so recurrence stays visible.</summary>
        internal static void Fire(string site, bool tripwire, string context)
        {
            try
            {
                _count.TryGetValue(site, out var n); _count[site] = ++n;
                float now = UnityEngine.Time.unscaledTime;
                if (_nextAt.TryGetValue(site, out var at) && now < at) return;
                _nextAt[site] = now + (tripwire ? 30f : 600f);
                if (tripwire)
                    Plugin.Logger.LogWarning($"[Guard] TRIPWIRE {site} fired (#{n}) — an upstream state that should be impossible; needs rooting. {context}");
                else
                    Plugin.Logger.LogInfo($"[Guard] parity {site} absorbed an MP-normal case (#{n}). {context}");
            }
            catch { }
        }
    }

    /// <summary>PARITY: happiness trigger volumes throw when a collider without the expected
    /// local-player plumbing enters/exits — remote-player puppets are the standing suspect
    /// (4+ field bundles, enter/exit pairs tracking remote movement).  Swallowed in MP: the
    /// volume simply doesn't register that crossing.</summary>
    [HarmonyPatch(typeof(LocationHappinessTrigger), "OnTriggerEnter")]
    public static class Guard_HappinessTrigger_Enter
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null || !NativeGuardReport.InMp) return __exception;
            NativeGuardReport.Fire("LocationHappinessTrigger.OnTriggerEnter", tripwire: false, $"{__exception.GetType().Name}");
            return null;
        }
    }
    [HarmonyPatch(typeof(LocationHappinessTrigger), "OnTriggerExit")]
    public static class Guard_HappinessTrigger_Exit
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null || !NativeGuardReport.InMp) return __exception;
            NativeGuardReport.Fire("LocationHappinessTrigger.OnTriggerExit", tripwire: false, $"{__exception.GetType().Name}");
            return null;
        }
    }

    /// <summary>PARITY: clicking a slot machine whose chair state involves a remote occupant
    /// NREs in SetChairOccupied (field 20260816-213224 ×7, both click and walk-over paths).
    /// Swallowed in MP: the interaction aborts cleanly instead of throwing out of the click.</summary>
    [HarmonyPatch(typeof(SlotMachineController), nameof(SlotMachineController.SetChairOccupied))]
    public static class Guard_SlotMachine_Chair
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null || !NativeGuardReport.InMp) return __exception;
            NativeGuardReport.Fire("SlotMachineController.SetChairOccupied", tripwire: false, $"{__exception.GetType().Name} (remote occupant suspected)");
            return null;
        }
    }

    /// <summary>TRIPWIRE: valuation over an EMPTY AI-owned comparable set throws
    /// InvalidOperationException out of LINQ Average and aborts whatever BizMan panel was
    /// building (6+ field bundles, host+client+offline).  An empty set means a rival-poor
    /// world — round-256-class residue — which is the bug to root.  Guarded result: 0
    /// (the panel renders with a zero valuation instead of half-built).</summary>
    [HarmonyPatch(typeof(Helpers.CompetitionHelper), nameof(Helpers.CompetitionHelper.CalculateAiOwnedValuation))]
    public static class Guard_AiOwnedValuation
    {
        static Exception? Finalizer(Exception __exception, ref float __result)
        {
            if (__exception == null || !NativeGuardReport.InMp) return __exception;
            __result = 0f;
            NativeGuardReport.Fire("CompetitionHelper.CalculateAiOwnedValuation", tripwire: true,
                $"{__exception.GetType().Name} — empty AI-owned comparable set = rival-poor world (round-256 class).");
            return null;
        }
    }

    /// <summary>TRIPWIRE: the shop-exit unpaid-items check NRE'd up to 98×/session (0.1.14
    /// era), killing the exit-zone path = "stuck in the building".  The MP-shared unpaid list
    /// holding a null is the state to root.  Guarded result: TRUE (allow the exit) — a
    /// deliberate trade: a possible unpaid walk-out beats a player soft-locked indoors, and
    /// the tripwire line records every occurrence for the root hunt.</summary>
    [HarmonyPatch(typeof(Helpers.PlayerHelper), nameof(Helpers.PlayerHelper.HasPaidForAllItems))]
    public static class Guard_HasPaidForAllItems
    {
        static Exception? Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception == null || !NativeGuardReport.InMp) return __exception;
            __result = true;
            NativeGuardReport.Fire("PlayerHelper.HasPaidForAllItems", tripwire: true,
                $"{__exception.GetType().Name} — unpaid-item list holds a null; exit allowed by policy (soft-lock beats the edge case).");
            return null;
        }
    }

    /// <summary>TRIPWIRE: the pedestrian camera's input handler died permanently after
    /// parking (field 20260816-004749: 12,809 per-frame NREs to end of log — the reported
    /// "stuck after parking").  Swallowing keeps the camera loop alive frame to frame
    /// instead of storming; the vehicle-exit state that nulls its target is the root to
    /// hunt, and the throttled tripwire counts it without becoming the next log storm.</summary>
    [HarmonyPatch(typeof(CameraControllers.PedestrianCam), "Update")]
    public static class Guard_PedestrianCam_Update
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null || !NativeGuardReport.InMp) return __exception;
            NativeGuardReport.Fire("PedestrianCam.Update", tripwire: true, $"{__exception.GetType().Name} — vehicle-exit camera state suspected.");
            return null;
        }
    }
    [HarmonyPatch(typeof(CameraControllers.PedestrianCam), "UpdateInput")]
    public static class Guard_PedestrianCam_UpdateInput
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null || !NativeGuardReport.InMp) return __exception;
            NativeGuardReport.Fire("PedestrianCam.UpdateInput", tripwire: true, $"{__exception.GetType().Name} — vehicle-exit camera state suspected.");
            return null;
        }
    }

    // (Batch 15's StationaryAiBehavior attribution guard REMOVED same-day: the round-199
    //  shield (MPPatches.Patch_StationaryAi_HealStuckCombinedFlag) already roots AND heals
    //  this storm — a native one-way _isPlayingCombinedAnimation flag, MP-amplified by
    //  weather-sync re-rolls — and a second finalizer on the same method registered first
    //  would have displaced that heal (verifier catch, 2026-08-18).  Lesson: grep OUR OWN
    //  patch classes for the target method before adding a patch to it.)
}
