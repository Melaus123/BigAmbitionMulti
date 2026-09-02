using System;
using Buildings.BuildingTypes.Shared.Dirtiness;   // GetCleanliness extension (BuildingCleanlinessHelper)
using HarmonyLib;
using Helpers;
using UI.Notification;
using UnityEngine;
using BigAmbitions.Tags;

namespace BigAmbitionsMP
{
    /// <summary>Round-112 — HELPER PARITY: CLEANING. A granted helper can now take a mop from a cleaning
    /// station in someone else's business, mop the floor, and have that cleaning actually count for the owner.
    ///
    /// FIELD REPORT (bamp-bug-20260726-230943, 0.1.14): client 'Shalowe', a granted helper in
    /// 'Broth3rhood04's shop, clicked a cleaning station 6 → 14 → 20 times in five seconds with nothing
    /// happening (our own [MoveFreeze] probe caught it: selection=CleaningStationController, overUI=0,
    /// position steady). They had just forwarded an interior edit for the same building, so permissions were
    /// not the issue. Register work already worked for helpers (Patch_StationCanWork_Helper) — cleaning was
    /// simply never covered: the mod had ZERO references to cleaning stations.
    ///
    /// THREE separate owner-gates had to be answered, which is why "just hand them a mop" would not have
    /// worked:
    ///   1. CleaningStationController.OnCleaningStationClick — walks the player over, then gates the entire
    ///      mop handover on IsPlayerOwnedBusiness INSIDE the arrival callback, with no else and no
    ///      notification. Silence.
    ///   2. MopController.AssignToPlayer — subscribes the floor-cell click listener ONLY when
    ///      IsPlayerOwnedBusiness. With the gate above bypassed but this one left, a helper would hold a mop
    ///      that does nothing when they click the floor.
    ///   3. Dirt is owner-authoritative interior state. MopController.FloorCellClick writes straight into
    ///      buildingRegistration.dirtSpots[...].dirtiness, but the ONLY pre-existing guest→owner interior
    ///      channel was the designer-close forward (retired in interior-edit Stage 3; edits now ride
    ///      BuildingInteriorDelta), whose sole trigger was the interior designer
    ///      closing. Mopping never triggers it, so a helper's cleaning would look right on their screen and
    ///      then be overwritten by the owner's next authoritative push.
    ///
    /// Gate 1 cannot use the usual Prefix/Finalizer ownership flip (as Bed/TV/CanWork do) because the check
    /// runs in the SetGoal ARRIVAL callback — after the walk, long after any wrapper around the click would
    /// have restored ownership. So the click is taken over for helpers and the native arrival body is
    /// mirrored inside the flip scope. Gate 2 IS inline, so it gets the ordinary flip.
    ///
    /// ECONOMICALLY INERT: the mop is a brand-new local instance (ItemHelper.InitializeNewInstance), never
    /// drawn from the owner's stock, and StopCleaning ends with PlayerHelper.RemoveItemsFromHands, destroying
    /// it. No inventory moves, so there is nothing to reconcile beyond the dirt values themselves.</summary>
    internal static class HelperCleaning
    {
        // ── B2: forward the cells this helper cleaned ────────────────────────────────────────────────────
        private const int MaxSpotsPerMessage = 400;   // a mop action touches a handful of cells; sanity ceiling only
        private static bool _affectedCellsMissLogged;   // warn once, not once per mop stroke

        /// <summary>EVENT-DRIVEN, not sampled (user challenge 2026-07-27, and they were right).  My first cut
        /// polled every 0.5s and diffed the whole dirt lattice against a remembered baseline.  Reporting on the
        /// action itself is better on every axis:
        ///   • EXACT — MopController.AffectedCells is precisely the cells that mop action touched (the game
        ///     collects them with one OverlapBox at click time), and DirtSpotObject.DirtSpot is their index into
        ///     buildingRegistration.dirtSpots.  A handful of cells, named, instead of a lattice-wide diff.
        ///   • ONE MESSAGE PER ACTION instead of a timer that fires whether or not anything happened.
        ///   • MORE CORRECT — a sampler forwards ANY local reduction, including ones that are not ours to push
        ///     (a client-side employee-cleaning tick, say).  This only ever reports what the local player's own
        ///     mop just did.
        ///   • NO BOOKKEEPING — no baseline dictionary, no per-session reset, no drift between them.
        /// Sending at STOP rather than per swing is deliberate: only the resting value matters, and the mop loop
        /// re-reads the registration each swing, so anything restored mid-run is simply re-cleaned.
        ///
        /// RACE FIX (field 20260830-181203, approved 2026-08-31; trade paragraph corrected per
        /// review-dirt-recheck-2026-09-01 #3): the mop loop only exits once every affected cell is
        /// ≤0.1 — i.e. the mop CLEANS TO ZERO by design — but StopCleaning (and this report) fires
        /// after a 0.1–1.0s cosmetic WaitForSeconds (1f - time%1f; the full 1.0s is reachable —
        /// clicking an already-clean tile skips the loop entirely and still reports). The owner's 2s
        /// absolute dirt band can land in that gap and RESTORE the helper's local copies to the
        /// owner's still-dirty values, so a live read here reported dirt the helper had just mopped
        /// away — the "mop stroke eaten" symptom. So this reports Dirtiness = 0 for every affected
        /// cell: that is what the mop achieved the moment the loop finished.
        /// THE REAL TRADE (the review traced every StopCleaning caller — walk-off and mode-change
        /// interrupts CANNOT fire this report): OVERLAPPING STROKES. AffectedCells is one static
        /// list, StopCleaning's StopCoroutine("FloorCellClick") is a no-op against the
        /// IEnumerator-started coroutine, and nothing gates a second floor click — so an earlier
        /// stroke's StopCleaning could report a click-spammed SECOND stroke's still-dirty cells as
        /// zero. GUARDED (approved 2026-09-01, review #1): the SAME-STROKE GATE below — a report
        /// fires only for a stroke whose cells were OBSERVED to reach ≤0.1 at a loop boundary
        /// (the MoveNext latch, sampled BEFORE the pause where the band restore lives), each
        /// stroke reports at most once, and a stroke abandoned finished-but-unreported by a new
        /// click is flushed at that click (its cells are still in the list, pre-clear).
        /// Plus the SAME-REGISTRATION GUARD (review #2): the mop writes through the CONTROLLER's
        /// cached BuildingContext.Registration, not necessarily the building the player stands
        /// in — the report now requires reference equality and reads the mop's own registration.</summary>
        // ── Same-stroke gate state ───────────────────────────────────────────────────────────
        private static int _strokeSeq;          // bumped by every floor click (new stroke)
        private static int _cleanLatchSeq = -1; // stroke whose cells were seen all ≤0.1 at a loop boundary
        private static int _reportedSeq = -1;   // stroke that has already been reported
        private static float _dropLogAt;
        // Both hooks are private-member patches a game update can silently unbind. The gate is
        // ALL-OR-NOTHING: with either hook dead it stands down entirely and StopCleaning reports
        // unconditionally — the pre-guard behavior with its documented overlap trade — instead
        // of a half-alive gate silently dropping every report.
        // Recheck C-1 (2026-09-01): armed by EXECUTION, not resolution — TargetMethods can find a
        // method that Harmony then fails to apply (Plugin's per-class catch continues), and a
        // found-but-unapplied click hook froze _strokeSeq → every report after the first died in
        // the dedup return below, silently. Each flag now flips the first time its hook body
        // actually runs; both fire during the very first stroke (click prefix, then swing
        // boundaries), so a healthy install arms before the first StopCleaning.
        internal static bool ClickHookOk, LatchHookOk;
        private static int _dedupSkips;   // review C-1: the early return is counted, never silent
        private static readonly System.Reflection.FieldInfo? AffectedCellsField =
            AccessTools.Field(typeof(MopController), "AffectedCells");

        internal static System.Collections.Generic.List<DirtSpotObject>? AffectedCellsList()
            => AffectedCellsField?.GetValue(null) as System.Collections.Generic.List<DirtSpotObject>;

        internal static void OnStrokeStart(MopController mop)
        {
            ClickHookOk = true;   // proof of application (review C-1)
            // Flush a FINISHED but unreported stroke before the native click clears the list —
            // without this, a click landing in the previous stroke's cosmetic pause would eat
            // that stroke's completed cleaning (the exact symptom the report-zero fix targets).
            if (_cleanLatchSeq == _strokeSeq && _strokeSeq != _reportedSeq)
                ReportCleanedCells(mop);
            _strokeSeq++;
            _lastMopReg = mop?.BuildingContext?.Registration;   // the lattice the new stroke writes to
        }

        internal static void OnLoopBoundary()
        {
            LatchHookOk = true;   // proof of application (review C-1)
            var cells = AffectedCellsList();
            if (cells == null || cells.Count == 0) return;
            var reg = _lastMopReg;
            var spots = reg?.dirtSpots;
            if (spots == null) return;
            foreach (var c in cells)
            {
                if (c == null) continue;
                int idx = c.DirtSpot;
                if (idx < 0 || idx >= spots.Count || spots[idx] == null) continue;
                if (spots[idx].dirtiness > 0.1f) return;   // stroke not finished yet
            }
            _cleanLatchSeq = _strokeSeq;
        }

        private static BuildingRegistration? _lastMopReg;   // the registration the active mop writes to

        internal static void OnMopStopCleaning(MopController mop)
        {
            if (ClickHookOk && LatchHookOk)
            {
                if (_strokeSeq == _reportedSeq)
                {
                    // This stroke already reported (a click-time flush, or an earlier StopCleaning of
                    // the same stroke). Counted: a runaway count here would mean the counter froze.
                    _dedupSkips++;
                    if (_dedupSkips == 1 || _dedupSkips % 50 == 0)
                        Plugin.Logger.LogInfo($"[Cleaning] stop-report already sent for stroke #{_strokeSeq} — dedup skip #{_dedupSkips}.");
                    return;
                }
                if (_cleanLatchSeq != _strokeSeq)
                {
                    // A StopCleaning fired while the CURRENT stroke had not been observed finished —
                    // an overlapped earlier stroke's stop. The finishing stroke reports later.
                    if (UnityEngine.Time.unscaledTime >= _dropLogAt)
                    {
                        _dropLogAt = UnityEngine.Time.unscaledTime + 5f;
                        Plugin.Logger.LogInfo("[Cleaning] stop-report dropped — current stroke not observed finished (overlapping strokes); the finishing stroke reports.");
                    }
                    return;
                }
            }
            ReportCleanedCells(mop);
        }

        public static void ReportCleanedCells(MopController mop)
        {
            try
            {
                if (!MPClient.IsClientInWorld && !MPServer.IsRunning) return;
                if (!HousingFurniture.LocalHelperHere()) return;   // owners need no forward; only a granted helper

                // Review #2: the mop writes through ITS controller's cached registration — read
                // the same one, and refuse to report when it is not the building we stand in.
                var reg = mop?.BuildingContext?.Registration;
                var here = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null || !ReferenceEquals(reg, here))
                {
                    Plugin.Logger.LogWarning("[Cleaning] mop's registration is not the building we stand in — report skipped (stale BuildingContext).");
                    return;
                }
                var spots = reg.dirtSpots;
                if (spots == null || spots.Count == 0) return;
                string addr = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(addr)) return;

                // AffectedCells is private static on MopController — the authoritative list of what the last
                // mop action touched.  Cleared at the start of every action, so it is never stale in a way that
                // matters: re-reporting the same already-clean cells is a no-op at the receiver.
                var cells = AffectedCellsList();
                if (cells == null)
                {
                    // Reflection on a private field is the one part of this that a game update can break
                    // WITHOUT a compile error, so say so loudly once rather than returning in silence — a
                    // quiet return here would look exactly like "the feature just doesn't work".
                    if (!_affectedCellsMissLogged)
                    {
                        _affectedCellsMissLogged = true;
                        Plugin.Logger.LogWarning("[Cleaning] MopController.AffectedCells did not resolve — a helper's cleaning "
                            + "cannot be reported to the owner (field renamed by a game update?). Cleaning still works locally.");
                    }
                    return;
                }

                var payload = new DirtEditPayload { AddressKey = addr, SenderId = MPConfig.PlayerId };
                // [PROBE:P-MOP-EATEN] observation only — non-zero at report time can be a band
                // restore OR an overlapping stroke's untouched cells (review #4: never assert which).
                int nonZero = 0; float nonZeroMax = 0f, nonZeroSum = 0f;
                foreach (var c in cells)
                {
                    if (c == null || payload.Spots.Count >= MaxSpotsPerMessage) continue;
                    int idx = c.DirtSpot;
                    if (idx < 0 || idx >= spots.Count || spots[idx] == null) continue;
                    var s = spots[idx];
                    if (s.dirtiness > 0.1f) { nonZero++; nonZeroSum += s.dirtiness; if (s.dirtiness > nonZeroMax) nonZeroMax = s.dirtiness; }
                    // Report ZERO, not the live value — the mop loop cleans to zero before the cosmetic
                    // pause, and the live value may already be a mid-pause band restore (comment above).
                    payload.Spots.Add(new DirtSpotDeltaInfo { Index = idx, X = s.x, Z = s.z, Dirtiness = 0f });
                }
                if (payload.Spots.Count == 0) return;

                if (MPServer.IsRunning) MPServer.HandleBuildingDirtEdit(payload, MPConfig.PlayerId);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingDirtEdit, MPConfig.PlayerId, payload));
                _reportedSeq = _strokeSeq;   // same-stroke gate: one report per stroke
                Plugin.Logger.LogInfo($"[Cleaning] helper cleaned {payload.Spots.Count} cell(s) in '{addr}' — reported to the owner (as zero)."
                    + (nonZero > 0 ? $" [PROBE:P-MOP-EATEN] {nonZero} of {payload.Spots.Count} reported cell(s) were non-zero at report time (max {nonZeroMax:F0}, sum {nonZeroSum:F0}) — zeroed anyway." : ""));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cleaning] ReportCleanedCells: {ex.Message}"); }
        }

        // (SameLatticeCell REMOVED 2026-08-31, review-mopping #3/#4/#5: the tolerant match was
        //  approved on a truncation-divergence theory the review REFUTED by decoding both
        //  bundle saves — the two machines' lattices were IDENTICAL, 225/225 labels matching
        //  at the same index; the "8 skipped" were already-clean cells. Interiors are one
        //  cached prefab instance per building size, so (int)position is deterministic across
        //  machines. The tolerance also weakened the load-bearing stacked-storeys X/Z guard
        //  at the band site and could pre-empt the exact fallback scan onto a real neighbour.
        //  Exact equality restored at both call sites; the noCell counter re-opens the
        //  question if a future bundle ever shows a nonzero count.)

        /// <summary>MAIN THREAD.  Apply a helper's cleaning to the local (owner's) registration copy.  Only
        /// ever lowers a value: a cleaning report that tried to raise dirtiness would be either a desync or a
        /// tampered client, and either way the owner's own simulation is the authority on getting dirtier.
        /// v10: dirt lives in its OWN hash band now (InteriorSync's dirt gate, not the full-snapshot
        /// hash) — the owner's next dirt tick detects the corrected values and re-broadcasts them via
        /// InteriorDirtSync to the players inside; still nothing extra to send from here.</summary>
        public static void Apply(DirtEditPayload? p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || p.Spots.Count == 0) return;

                var reg = GameStatePatcher.FindRegistration(p.AddressKey);
                var spots = reg?.dirtSpots;
                if (spots == null || spots.Count == 0)
                {
                    Plugin.Logger.LogWarning($"[Cleaning] dirt edit for '{p.AddressKey}' from '{p.SenderId}' — no local dirt lattice; ignored.");
                    return;
                }

                // Field 181203: the two skip causes were one counter, and they mean OPPOSITE
                // things — "not a reduction" is benign overlap re-reporting, while "no such
                // cell" is LATTICE MEMBERSHIP DIVERGENCE (the bundle's first batch: 8 of 9
                // cells nonexistent here; those cells stay dirty on this side and the dirt
                // band then re-dirties them on the mopper's side — their cleaning visibly
                // un-does itself). Counted apart so the next bundle quantifies the divergence.
                int applied = 0, noCell = 0, noReduction = 0;
                var repaint = new System.Collections.Generic.List<int>();
                foreach (var d in p.Spots)
                {
                    if (d == null) continue;
                    int idx = d.Index;
                    // Trust the index only if it names the same cell EXACTLY; otherwise fall back to an
                    // X/Z lookup so a lattice built in a different order still lands on the right tile.
                    // (Exact on purpose — review-mopping #3: measured lattices are identical across
                    // machines; a tolerant match could only ever hit a real neighbouring tile.)
                    if (idx < 0 || idx >= spots.Count || spots[idx] == null || spots[idx].x != d.X || spots[idx].z != d.Z)
                    {
                        idx = -1;
                        for (int i = 0; i < spots.Count; i++)
                            if (spots[i] != null && spots[i].x == d.X && spots[i].z == d.Z) { idx = i; break; }
                        if (idx < 0) { noCell++; continue; }
                    }
                    float want = Mathf.Clamp(d.Dirtiness, 0f, 100f);
                    if (want >= spots[idx].dirtiness) { noReduction++; continue; }   // never dirtier via this channel
                    spots[idx].dirtiness = want;
                    repaint.Add(idx);
                    applied++;
                }

                // Field 181203 fix (user-approved): the write above is DATA only — repaint the
                // decals when the local player is standing in this very building, or the host
                // watches the helper mop and sees nothing change until re-entry (round-122
                // "value arrived and nothing reacted" class). Native per-spot repaint; a
                // handful of cells per mop stop, so per-cell calls are cheap.
                if (applied > 0)
                {
                    try
                    {
                        var bm = InstanceBehavior<BuildingManager>.Instance;
                        if (bm?.buildingRegistration != null && BuildingManager.IsInsideBuilding
                            && GameStateReader.AddressKey(bm.buildingRegistration) == p.AddressKey)
                            foreach (var idx in repaint)
                                try { bm.UpdateDirtinessInSpecificSpot(idx); } catch { }   // review-mopping #10: one bad index must not abandon the rest
                    }
                    catch (Exception rex) { Plugin.Logger.LogWarning($"[Cleaning] repaint: {rex.Message}"); }
                }

                // Review-mopping #7: log ALL batches (an all-already-clean batch was silent — the
                // very reading that settles the counter question could never appear). #8: state
                // the observation, not a cause.
                if (applied > 0 || noCell > 0 || noReduction > 0)
                    Plugin.Logger.LogInfo($"[Cleaning] applied {applied} cleaned cell(s) from '{p.SenderId}' for '{p.AddressKey}'"
                        + (noCell > 0 ? $" ({noCell} cell(s) with no matching cell here)" : "")
                        + (noReduction > 0 ? $" ({noReduction} already clean)." : "."));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cleaning] Apply: {ex.Message}"); }
        }
    }

    /// <summary>Round-112 B2 trigger: a mop action finished, so report the cells it cleaned. StopCleaning is
    /// public and compile-time safe. Since 2026-09-01 (review #1) the report is same-stroke-gated: it fires
    /// only for a stroke observed finished at a loop boundary — a put-away/overlap StopCleaning with the
    /// current stroke unfinished is dropped (that state was never final), and each stroke reports once.</summary>
    [HarmonyPatch(typeof(MopController), nameof(MopController.StopCleaning))]
    public static class Patch_MopController_StopCleaning_Report
    {
        static void Postfix(MopController __instance) { HelperCleaning.OnMopStopCleaning(__instance); }
    }

    /// <summary>Same-stroke gate, half 1 (review #1): every floor click is a new stroke. The PREFIX flushes a
    /// finished-but-unreported previous stroke BEFORE the native body clears the shared static AffectedCells
    /// (a click landing in the previous stroke's cosmetic pause would otherwise eat its completed cleaning),
    /// then advances the stroke counter. OnFloorCellClick is private — TargetMethods yields nothing on a
    /// resolution miss (never the player-visible patch-degraded notice); ClickHookOk arms only when the
    /// prefix actually executes, so a found-but-unapplied hook also stands the gate down (review C-1).</summary>
    [HarmonyPatch]
    public static class Patch_MopController_FloorClick_Stroke
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(MopController), "OnFloorCellClick");
            if (m == null) Plugin.Logger.LogWarning("[Cleaning] MopController.OnFloorCellClick did not resolve — same-stroke gate stands down (unconditional reports, pre-guard trade).");
            else yield return m;   // the flag arms when the prefix first RUNS (review C-1), not here
        }
        static void Prefix(MopController __instance)  { HelperCleaning.OnStrokeStart(__instance); }
    }

    /// <summary>Same-stroke gate, half 2 (review #1): the latch. The FloorCellClick coroutine yields every
    /// 0.3s swing and once more entering the ≤1.0s cosmetic pause; at each boundary, if every affected cell
    /// is ≤0.1 the CURRENT stroke is marked finished. Sampled at the loop boundary — BEFORE the pause where
    /// the owner's 2s band restore lands — so the race cannot un-finish a stroke, while an overlapped stale
    /// StopCleaning finds the newest stroke unlatched and is dropped. Iterator MoveNext resolved defensively.</summary>
    [HarmonyPatch]
    public static class Patch_MopController_FloorClick_Latch
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            System.Reflection.MethodBase? mv = null;
            try
            {
                var iter = AccessTools.Method(typeof(MopController), "FloorCellClick");
                if (iter != null) mv = AccessTools.EnumeratorMoveNext(iter);
            }
            catch { }
            if (mv == null) Plugin.Logger.LogWarning("[Cleaning] FloorCellClick MoveNext did not resolve — same-stroke gate stands down (unconditional reports, pre-guard trade).");
            else yield return mv;   // the flag arms when the postfix first RUNS (review C-1), not here
        }
        static void Postfix() { HelperCleaning.OnLoopBoundary(); }
    }

    /// <summary>Field 20260830-181203 #2 (approved 2026-08-31): the HUD cleanliness meter is PINNED at
    /// 100% for anyone the game doesn't consider the owner — ItemPanelUI.RefreshMetaCleanliness
    /// (:952-961) defaults maintenanceValue to 100 and reads the registration only when
    /// IsPlayerOwnedBusiness. A granted helper mopping a partner's shop stares at a full meter no
    /// matter how dirty the floor is; the dirt DATA under it is fine (the dirt band syncs to players
    /// inside) — only the meter lies. Prefix: in MP, inside a session player's business that is not
    /// our own, feed the meter the local replica's real cleanliness through the private
    /// SetMaintenanceValue and skip the native body. AI venues and single player stay native.</summary>
    [HarmonyPatch(typeof(UI.ItemPanel.ItemPanelUI), nameof(UI.ItemPanel.ItemPanelUI.RefreshMetaCleanliness))]
    public static class Patch_CleanlinessMeter_Unpin
    {
        private static readonly System.Reflection.MethodInfo? SetMaintenance =
            AccessTools.Method(typeof(UI.ItemPanel.ItemPanelUI), "SetMaintenanceValue");
        private static float _lastLogged = -1f;   // P-CLEAN-METER is changed-only: refresh fires per mop swing

        static bool Prefix(UI.ItemPanel.ItemPanelUI __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                var reg = bm?.buildingRegistration;
                if (reg == null || bm!.IsPlayerOwnedBusiness) return true;      // owner: native path is already live
                if (!GameStatePatcher.IsAnyPlayerBusiness(reg)) return true;    // AI venue: native 100% pin stands
                if (SetMaintenance == null)
                {
                    Plugin.Logger.LogWarning("[Cleaning] ItemPanelUI.SetMaintenanceValue did not resolve — cleanliness meter stays native.");
                    return true;
                }
                float value = reg.GetCleanliness();
                SetMaintenance.Invoke(__instance, new object[] { value });
                if (Math.Abs(value - _lastLogged) >= 1f)
                {
                    _lastLogged = value;
                    Plugin.Logger.LogInfo($"[PROBE:P-CLEAN-METER] meter fed live cleanliness {value:F0}% in '{GameStateReader.AddressKey(reg)}' (native would pin 100%).");
                }
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Cleaning] meter unpin: {ex.Message}");
                return true;
            }
        }
    }

    /// <summary>Gate 2 (see HelperCleaning): the mop only listens for floor clicks when the building reads as
    /// player-owned.  This gate is INLINE, so the ordinary depth-counted flip is enough — ownership is back to
    /// its real value before the method returns, exactly as the housing flips require.</summary>
    [HarmonyPatch(typeof(MopController), nameof(MopController.AssignToPlayer))]
    public static class Patch_MopController_AssignToPlayer_Helper
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    /// <summary>Gate 1 (see HelperCleaning): take over the cleaning-station click for a granted helper and
    /// mirror the native arrival body inside the flip scope.  A Prefix/Finalizer pair cannot work here — the
    /// ownership check lives in the SetGoal arrival callback, which runs after the walk.</summary>
    [HarmonyPatch(typeof(CleaningStationController), nameof(CleaningStationController.OnCleaningStationClick))]
    public static class Patch_CleaningStation_HelperMop
    {
        static bool Prefix(CleaningStationController __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;   // single player — vanilla
                if (__instance == null) return true;
                // 1.0 PORT (sweep-2 S5): native now HEAD-GUARDS the click — holding a mop means the
                // click does nothing at all (no walk, no put-away; putting the mop back moved to the
                // item panel → static ReturnMopToStation, which is not ownership-gated and therefore
                // already works for a helper untouched). Returning true here hands a mop-holding
                // helper to that same native guard — exact owner parity (ruling 28).
                if (Helpers.PlayerHelper.IsHoldingAMop) return true;
                // Owners keep the native path untouched; only a granted helper is redirected.
                if (!HousingFurniture.LocalHelperHere()) return true;

                var gm = InstanceBehavior<GameManager>.Instance;
                if (gm?.playerController == null) return true;

                gm.playerController.SetGoal(__instance, () =>
                {
                    HousingFurniture.Enter(includeHelper: true);
                    try
                    {
                        // Mirror of CleaningStationController.OnCleaningStationClick's arrival body.
                        if (SaveGameManager.Current?.ActiveVehicleId != null)
                        {
                            Notifications.ShowError("notification_need_empty_hands_to_interact");
                            return;
                        }
                        if (PlayerHelper.IsHoldingItem)
                        {
                            // 1.0 PORT (sweep-2 S5): a held MOP can no longer reach this arrival body —
                            // the head guard in the Prefix (and in native) swallows the click first — so
                            // ANY item in hand is an error, exactly the 1.0 native body. The 0.11-era
                            // put-away-via-StopCleaning branch is gone with the flow that owned it.
                            Notifications.ShowError("notification_need_empty_hands_to_interact");
                            return;
                        }

                        // The mop's item id is a private serialized field — read it rather than hardcoding, so a
                        // content change to the prefab can't leave us handing out a stale item name.
                        string mopName = "";
                        try { mopName = AccessTools.Field(typeof(CleaningStationController), "mopItemName")?.GetValue(__instance) as string ?? ""; }
                        catch { }
                        if (string.IsNullOrEmpty(mopName)) mopName = "ba:itemname_mop";

                        PlayerHelper.ItemInstanceInHands = ItemHelper.InitializeNewInstance(mopName);
                        Plugin.Logger.LogInfo($"[Cleaning] helper took a mop ('{mopName}') in '{MPRegisterSync.CurrentShopAddress}'.");
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Cleaning] helper mop handover: {ex.Message}"); }
                    finally { HousingFurniture.Exit(); }
                });
                return false;   // native click replaced
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Cleaning] station click takeover: {ex.Message}");
                return true;    // fall back to native on any surprise
            }
        }
    }
}
