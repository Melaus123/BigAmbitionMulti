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
        /// clicking an already-clean tile skips the loop entirely and still reports).
        /// CORRECTED 2026-09-28 (review R4): in build 3682 there is no such pause. The loop exits once every
        /// affected cell is 0, then waits only until Time.time reaches the stroke's START + minimumMopDuration
        /// (default 0.2 s; MopController.FloorCellClick), so after any stroke longer than 0.2 s StopCleaning
        /// follows the loop at once. The race below was real only in that sub-0.2 s window. The owner's 2s
        /// absolute dirt band can land in that gap and RESTORE the helper's local copies to the
        /// owner's still-dirty values, so a live read here reported dirt the helper had just mopped
        /// away — the "mop stroke eaten" symptom. So this reports Dirtiness = 0 for every affected
        /// cell: that is what the mop achieved the moment the loop finished.
        /// SUPERSEDED 2026-09-28 (H-CLEANLOOP-1 fix, F1+F3, user-approved decision 14): the forced zero
        /// was the other half of the bug, not its cure. The band did not only land in the pause - it
        /// re-dirtied the stroke's cells every ~2 s so the stroke never ended at all (rig
        /// T-MOP-MERGED-HC-20260928-170639), and a later click's flush then reported the owner's FRESH
        /// dirt as zero ("P-MOP-EATEN 7 of 9 non-zero"). Now the StrokeGuard (F1, below) keeps every
        /// network dirt write from RAISING a cell of the live local stroke until its StopCleaning, so the
        /// live value at report time IS what the mop achieved: the report sends each cell's CURRENT
        /// value (clamped >= 0). The owner's Apply only ever lowers, so a non-zero value erases nothing.
        /// THE REAL TRADE (the review traced every StopCleaning caller — walk-off and mode-change
        /// interrupts CANNOT fire this report): OVERLAPPING STROKES. AffectedCells is one static
        /// list, StopCleaning's StopCoroutine("FloorCellClick") is a no-op against the
        /// IEnumerator-started coroutine, and nothing gates a second floor click — so an earlier
        /// stroke's StopCleaning could report a click-spammed SECOND stroke's still-dirty cells as
        /// zero. CORRECTED 2026-09-28 (review R4): in build 3682 overlapping strokes cannot happen - the
        /// native OnFloorCellClick starts a stroke only when none is live (`if (!currentCleaningMop)`, see F3)
        /// and StopCleaning's only caller is the stroke's own coroutine, after its cells reached 0. The gate
        /// below stays as a belt; F2's drop branch in OnMopStopCleaning is now almost unreachable. GUARDED (approved 2026-09-01, review #1): the SAME-STROKE GATE below — a report
        /// fires only for a stroke whose cells were OBSERVED to reach ≤0.1 at a loop boundary
        /// (the MoveNext latch, sampled BEFORE the pause where the band restore lives), each
        /// stroke reports at most once, and a stroke abandoned finished-but-unreported by a new
        /// click is flushed at that click (its cells are still in the list, pre-clear).
        /// Plus the SAME-REGISTRATION GUARD (review #2): the mop writes through the CONTROLLER's
        /// cached BuildingContext.Registration, not necessarily the building the player stands
        /// in — the report now requires reference equality and reads the mop's own registration.</summary>
        // ── Same-stroke gate state ───────────────────────────────────────────────────────────
        private static int _strokeSeq;          // bumped by every floor click that really starts a stroke (F3)
        private static int _lateLatches;        // F2: strokes observed finished only at their StopCleaning
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
        internal static int StrokeSeq => _strokeSeq;   // R2 probe: the stroke a coroutine exception belongs to
        private static int _dedupSkips;   // review C-1: the early return is counted, never silent
        private static readonly System.Reflection.FieldInfo? AffectedCellsField =
            AccessTools.Field(typeof(MopController), "AffectedCells");

        internal static System.Collections.Generic.List<DirtSpotObject>? AffectedCellsList()
            => AffectedCellsField?.GetValue(null) as System.Collections.Generic.List<DirtSpotObject>;

        internal static void OnStrokeStart(MopController mop)
        {
            ClickHookOk = true;   // proof of application (review C-1)
            // H-CLEANLOOP-1 fix F3 (2026-09-28): the native OnFloorCellClick starts a stroke ONLY when no
            // stroke is live (`if (!currentCleaningMop) StartCoroutine(FloorCellClick(..))`, decompile
            // MopController.OnFloorCellClick). A click during a live stroke (its cosmetic pause included)
            // is ignored by the game - AffectedCells is not cleared, nothing starts - so it is not a
            // stroke here either: no flush, no _strokeSeq bump (the bump let the running stroke latch
            // and report under a number it was never started as).
            try { if (MopController.currentCleaningMop != null) return; } catch { }
            // Flush a FINISHED but unreported stroke before the native click clears the list. Since F2
            // the stroke-end report leaves at StopCleaning whenever the stroke's cells are clean, so
            // this is a backstop (e.g. a report gate that returned early); since F3 it reports CURRENT
            // values, so cells the band re-dirtied after the stroke ended are never sent as zero.
            if (_cleanLatchSeq == _strokeSeq && _strokeSeq != _reportedSeq)
                ReportCleanedCells(mop);
            _strokeSeq++;
            _lastMopReg = mop?.BuildingContext?.Registration;   // the lattice the new stroke writes to
        }

        internal static void OnLoopBoundary()
        {
            LatchHookOk = true;   // proof of application (review C-1)
            MopProbe.OnFrame();   // [PROBE:P-CLEANLOOP] log-only: stroke start / watchdog / presence (the per-frame part is two compares)
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
#if BAMP_DEV
        /// <summary>P-CLEANLOOP rig (DEV): the same-stroke gate's counters, for the `handstate` / `mopcell` levers.</summary>
        internal static string DevGateState() => $"gate: stroke#={_strokeSeq} latch={_cleanLatchSeq} reported={_reportedSeq} clickHook={ClickHookOk} latchHook={LatchHookOk} dedup={_dedupSkips}";
#endif

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
                    // H-CLEANLOOP-1 fix F2 (2026-09-28): when an INCOMING update is what finishes the
                    // stroke (it lowers the last dirty cells between two swings), the loop's final
                    // MoveNext breaks and calls StopCleaning INSIDE the same MoveNext - before the
                    // MoveNext postfix (OnLoopBoundary) can latch. That report used to be dropped here,
                    // and the next click then flushed the cells as zero although they were dirty again
                    // (rig T-MOP-MERGED-HC-20260928-170639). So: if the CURRENT stroke's cells are all
                    // <= 0.1 right now, the stroke is finished - latch it and report.
                    if (CurrentStrokeClean())
                    {
                        _cleanLatchSeq = _strokeSeq;
                        _lateLatches++;
                        if (_lateLatches == 1 || _lateLatches % 50 == 0)
                            Plugin.Logger.LogInfo($"[Cleaning] stroke #{_strokeSeq} observed finished at its StopCleaning (an incoming update finished it after the last swing) — reported (late latch #{_lateLatches}).");
                    }
                    else
                    {
                        // A StopCleaning fired while the CURRENT stroke's cells are still dirty — an
                        // overlapped earlier stroke's stop. The finishing stroke reports later (as before).
                        // Review R4 (2026-09-28): almost unreachable in build 3682 - no native overlap (F3) and
                        // StopCleaning comes only from the stroke's own coroutine after its cells hit 0; only a
                        // StrokeGuard miss (a raise between the loop's exit and StopCleaning) could land here.
                        if (UnityEngine.Time.unscaledTime >= _dropLogAt)
                        {
                            _dropLogAt = UnityEngine.Time.unscaledTime + 5f;
                            Plugin.Logger.LogInfo("[Cleaning] stop-report dropped — current stroke not observed finished (overlapping strokes); the finishing stroke reports.");
                        }
                        return;
                    }
                }
            }
            ReportCleanedCells(mop);
        }

        /// <summary>F2: are all of the CURRENT stroke's cells (AffectedCells on the stroke's registration) at most 0.1 now?
        /// False when there is no cell to judge.</summary>
        private static bool CurrentStrokeClean()
        {
            try
            {
                var cells = AffectedCellsList();
                var spots = _lastMopReg?.dirtSpots;
                if (cells == null || cells.Count == 0 || spots == null) return false;
                int seen = 0;
                foreach (var c in cells)
                {
                    if (c == null) continue;
                    int idx = c.DirtSpot;
                    if (idx < 0 || idx >= spots.Count || spots[idx] == null) continue;
                    if (spots[idx].dirtiness > 0.1f) return false;
                    seen++;
                }
                return seen > 0;
            }
            catch { return false; }
        }

        public static void ReportCleanedCells(MopController mop)
        {
            try
            {
                if (!MPClient.IsClientInWorld && !MPServer.IsRunning) return;
                if (!HousingFurniture.LocalHelperHere()) { MopProbe.Gate("not-a-helper-here"); return; }   // owners need no forward; only a granted helper ([PROBE:P-CLEANLOOP] gate line is log-only)

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
                if (spots == null || spots.Count == 0) { MopProbe.Gate("no-dirt-lattice"); return; }
                string addr = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(addr)) { MopProbe.Gate("no-address"); return; }

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
                // restore after the stroke ended (a click's flush) or an overlapping stroke's untouched
                // cells (review #4: never assert which). Since F3 such a cell is reported as it is.
                int nonZero = 0; float nonZeroMax = 0f, nonZeroSum = 0f;
                foreach (var c in cells)
                {
                    if (c == null || payload.Spots.Count >= MaxSpotsPerMessage) continue;
                    int idx = c.DirtSpot;
                    if (idx < 0 || idx >= spots.Count || spots[idx] == null) continue;
                    var s = spots[idx];
                    if (s.dirtiness > 0.1f) { nonZero++; nonZeroSum += s.dirtiness; if (s.dirtiness > nonZeroMax) nonZeroMax = s.dirtiness; }
                    // H-CLEANLOOP-1 fix F3 (2026-09-28): the cell's CURRENT local value (clamped >= 0), never
                    // a forced zero. With the StrokeGuard (F1) no network write can raise a live stroke's
                    // cell, so at the stroke's StopCleaning this IS what the mop achieved (0 for a finished
                    // stroke). At a later click's flush it may be the owner's own re-sent value, which the
                    // owner's Apply (lower-only) ignores - where the old forced zero erased fresh dirt.
                    payload.Spots.Add(new DirtSpotDeltaInfo { Index = idx, X = s.x, Z = s.z, Dirtiness = Mathf.Max(0f, s.dirtiness) });
                }
                if (payload.Spots.Count == 0) { MopProbe.Gate("no-cells"); return; }

                if (MPServer.IsRunning) MPServer.HandleBuildingDirtEdit(payload, MPConfig.PlayerId);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingDirtEdit, MPConfig.PlayerId, payload));
                _reportedSeq = _strokeSeq;   // same-stroke gate: one report per stroke
                Plugin.Logger.LogInfo($"[Cleaning] helper cleaned {payload.Spots.Count} cell(s) in '{addr}' — reported to the owner (current values)."
                    + (nonZero > 0 ? $" [PROBE:P-MOP-EATEN] {nonZero} of {payload.Spots.Count} reported cell(s) were non-zero at report time (max {nonZeroMax:F0}, sum {nonZeroSum:F0}) — reported at those values, not zeroed (F3)." : ""));
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
                    // H-CLEANLOOP-1 F1: this network writer only ever LOWERS, so it can never raise a live
                    // local stroke's cell - it needs no StrokeGuard.
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

    /// <summary>H-CLEANLOOP-1 fix F1 - the STROKE GUARD (user-approved 2026-09-28, decision 14; cleaning fix attempt 2).
    /// Reproduced on the rig (T-MOP-MERGED-HC-20260928-170639): the native stroke (MopController.FloorCellClick) loops
    /// while ANY affected cell's dirtiness is above 0 and holds NavigationBlocker.CleaningFloor until StopCleaning; our
    /// incoming dirt applies SET the owner's absolute values, so a non-owner mopper's stroke cells were re-dirtied by
    /// every ~2 s band, the stroke never ended, and the put-away (held back behind it by the game) never ran.
    /// RULE: while THIS machine has a live stroke - MopController.currentCleaningMop, set by the stroke's first line and
    /// cleared at the end of StopCleaning, so the cosmetic pause is covered - on the SAME registration object, a network
    /// dirt write takes min(incoming, local) for that stroke's cells (MopController.AffectedCells); every other cell
    /// applies as before. Lowering stays allowed. The owner stays the authority: the stroke's report (F2/F3) lowers the
    /// owner's copy, the owner's next band carries the cleaned values, and after StopCleaning the guard is off.
    /// Callers = every network write of dirtSpots[i].dirtiness on this machine: GameStatePatcher's full-snapshot dirt
    /// apply (ApplySnapshotDirt - values in place, the length never changes: fold cleanfix2 G1) and ApplyInteriorDirtSync (the band, and the host world's copy of a client owner's upload).
    /// HelperCleaning.Apply only ever lowers (no guard needed). Main thread; runs per dirt APPLY, not per frame; no
    /// allocation on the write path (fixed arrays + one reused HashSet); a [MopProbe] line only when a cell is held.</summary>
    internal static class StrokeGuard
    {
        private const int Max = 16;   // MopController.OverlapResults holds 16, so AffectedCells <= 16
        private static readonly System.Collections.Generic.HashSet<int> _cells = new System.Collections.Generic.HashSet<int>();
        private static readonly int[] _ix = new int[Max], _x = new int[Max], _z = new int[Max];
        private static readonly float[] _v = new float[Max];
        private static int _n;
        private static readonly int[] _hIx = new int[Max];                               // this apply's held-back cells
        private static readonly float[] _hIn = new float[Max], _hKept = new float[Max];
        private static int _hN;

        /// <summary>Call BEFORE a network dirt write on <paramref name="reg"/>. True when reg is the live local
        /// stroke's registration; captures the stroke's cells (index, tile X/Z, local value).</summary>
        internal static bool Begin(BuildingRegistration? reg)
        {
            _n = 0; _hN = 0; _cells.Clear();
            try
            {
                var cur = MopController.currentCleaningMop;
                if (cur == null || reg?.dirtSpots == null) return false;
                if (!ReferenceEquals(cur.BuildingContext?.Registration, reg)) return false;   // the stroke reads ITS registration object
                var cells = HelperCleaning.AffectedCellsList();
                if (cells == null) return false;
                var spots = reg.dirtSpots;
                foreach (var c in cells)
                {
                    if (c == null || _n >= Max) continue;
                    int i = c.DirtSpot;
                    if (i < 0 || i >= spots.Count || spots[i] == null || !_cells.Add(i)) continue;
                    _ix[_n] = i; _x[_n] = spots[i].x; _z[_n] = spots[i].z; _v[_n] = spots[i].dirtiness; _n++;
                }
                return _n > 0;
            }
            catch { _n = 0; _cells.Clear(); return false; }
        }

        /// <summary>Both network paths (values in place): a live-stroke cell takes min(incoming, local); others unchanged.</summary>
        internal static float Clamp(int i, float incoming, float local)
        {
            try
            {
                if (_n == 0 || incoming <= local || !_cells.Contains(i)) return incoming;
                Note(i, incoming, local);
                return local;
            }
            catch { return incoming; }
        }

        /// <summary>True while this machine's mop stroke is live on <paramref name="reg"/> (the stroke's own registration object).</summary>
        internal static bool LiveOn(BuildingRegistration? reg)
        {
            try
            {
                var cur = MopController.currentCleaningMop;
                return cur != null && reg != null && ReferenceEquals(cur.BuildingContext?.Registration, reg);
            }
            catch { return false; }
        }

        private static void Note(int i, float incoming, float kept)
        {
            if (_hN < Max) { _hIx[_hN] = i; _hIn[_hN] = incoming; _hKept[_hN] = kept; _hN++; }
        }

        /// <summary>Call AFTER the write: the rate-limited [MopProbe] line when cells were held back; resets the state.</summary>
        internal static void End(BuildingRegistration? reg, string source)
        {
            try
            {
                if (_hN > 0 && MopProbe.GuardDue(_hN))
                {
                    var sb = new System.Text.StringBuilder();
                    for (int k = 0; k < _hN; k++)
                    {
                        if (k > 0) sb.Append(' ');
                        sb.Append(_hIx[k]).Append(':').Append(_hIn[k].ToString("F0")).Append("->").Append(_hKept[k].ToString("F0"));
                    }
                    string addr = ""; try { if (reg != null) addr = GameStateReader.AddressKey(reg) ?? ""; } catch { }
                    MopProbe.GuardLine(addr, source, _hN, _n, sb.ToString());
                }
            }
            catch { }
            _n = 0; _hN = 0; _cells.Clear();
        }
    }

    /// <summary>Round-112 B2 trigger: a mop action finished, so report the cells it cleaned. StopCleaning is
    /// public and compile-time safe. Since 2026-09-01 (review #1) the report is same-stroke-gated: it fires
    /// only for a stroke observed finished at a loop boundary — a put-away/overlap StopCleaning with the
    /// current stroke unfinished is dropped (that state was never final), and each stroke reports once.</summary>
    [HarmonyPatch(typeof(MopController), nameof(MopController.StopCleaning))]
    public static class Patch_MopController_StopCleaning_Report
    {
        static void Postfix(MopController __instance) { HelperCleaning.OnMopStopCleaning(__instance); MopProbe.OnStop("StopCleaning"); }
    }

    /// <summary>Same-stroke gate, half 1 (review #1): every floor click that really starts a stroke (no stroke live -
    /// the native condition; F3 2026-09-28) is a new stroke. The PREFIX flushes a
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
    /// FRAME (yield return null; 0.3 s is only the mop-sound interval) and, after the loop, until the stroke's
    /// START + minimumMopDuration (default 0.2 s) - corrected 2026-09-28 (review R4): build 3682 has no ≤1.0 s
    /// cosmetic pause, so after a stroke longer than 0.2 s there is no pause at all; at each boundary, if every affected cell
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

    /// <summary>[PROBE:P-CLEANLOOP] (2026-09-28; user pre-approved; LOG-ONLY; ships in every build; MP sessions
    /// only). Field reports 2026-09-28 (likely v0.3.2): "stuck in a loop on cleaning mode", "mop fused to your
    /// character, can't interact with anything", "another person entering/leaving/checking out cuts into it".
    /// Hypothesis H-CLEANLOOP-1: the native stroke (MopController.FloorCellClick) loops while ANY affected cell
    /// is above 0 and holds NavigationBlocker.CleaningFloor; the owner's ABSOLUTE dirt pushes (ApplyInteriorDirtSync,
    /// the full snapshot's dirt rebuild) raise the stroke's cells again on a non-owner mopper, and the cleaned-cells
    /// report leaves only at the stroke's end - so the stroke never ends and the put-away
    /// (CleaningStationController.ReturnMopToStation) waits behind it. Lines: stroke start, a dirt update RAISING
    /// cells under the live stroke, the stuck-stroke watchdog ("LIVELOCK?"), stroke end, put-away / Escape /
    /// hands-cleared, the report gate's silent returns, the owner's dirt re-send reason, and other players
    /// entering / leaving during a live stroke. Every line is budgeted (400 per session; restarts with each MP session, R3). Nothing here writes game
    /// state, changes a flow or a return value. Registered in .modding/04-probes.md (P-CLEANLOOP-*).</summary>
    internal static class MopProbe
    {
        private const float WatchAfterS = 15f, WatchEveryS = 10f;
        private const int SessionBudget = 400, StartBudget = 30, ResendPerAddr = 20;
        private static int _lines, _starts;
        private static bool InMp => MPServer.IsRunning || MPClient.IsClientInWorld;

        // R3 (review 2026-09-28): the budgets restart with each multiplayer session (a new MPLog.SessionId: the host's
        // Start / the client's world snapshot), not only per game launch. Checked lazily at the next line / stroke start.
        private static string _budgetSession = "";
        private static void SessionCheck()
        {
            string sid = MPLog.SessionId ?? "";
            if (sid.Length == 0 || string.Equals(sid, _budgetSession, StringComparison.Ordinal)) return;
            _budgetSession = sid; _lines = 0; _starts = 0; _resendN.Clear();
        }

        private static void Log(string s)
        {
            try
            {
                SessionCheck();
                if (_lines > SessionBudget) return;
                if (++_lines > SessionBudget)
                {
                    Plugin.Logger.LogInfo($"[MopProbe] budget spent ({SessionBudget} lines) - further [MopProbe] lines are suppressed this session.");
                    return;
                }
                Plugin.Logger.LogInfo("[MopProbe] " + s);
            }
            catch { }
        }

        private static readonly System.Reflection.FieldInfo? OnStopField = AccessTools.Field(typeof(MopController), "OnStopCleaning");
        private static readonly System.Reflection.FieldInfo? BlockersField = AccessTools.Field(typeof(PlayerController), "_activeNavigationBlockers");

        /// <summary>True while the game holds a put-away (or any stop action) until the live stroke ends.</summary>
        internal static bool PutAwayPending() { try { return OnStopField?.GetValue(null) != null; } catch { return false; } }

        internal static string Blockers()
        {
            try
            {
                if (BlockersField?.GetValue(PlayerHelper.PlayerController) is System.Collections.IEnumerable e)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var x in e) { if (sb.Length > 0) sb.Append(','); sb.Append(x); }
                    return sb.ToString();
                }
            }
            catch { }
            return "?";
        }

        private static string Held() { try { return PlayerHelper.ItemInstanceInHands?.itemName ?? "empty"; } catch { return "?"; } }

        // ── the live stroke ────────────────────────────────────────────────────────────────
        private static MopController? _mop;
        private static BuildingRegistration? _reg;
        private static string _addr = "", _shop = "";
        private static float _since = -1f, _nextWatch, _nextPresence, _nextHitLog;
        private static int _seq, _n, _hits, _raisedCells;
        private static int _guardHits, _guardCells;   // H-CLEANLOOP-1 F1: StrokeGuard holds in this stroke
        private static float _nextGuardLog;
        private static float _maxRaise;
        private static readonly int[] _idx = new int[16];      // MopController.OverlapResults holds 16, so AffectedCells <= 16
        private static readonly float[] _snap = new float[16];
        private static readonly System.Collections.Generic.HashSet<string> _inside = new System.Collections.Generic.HashSet<string>();
        private static readonly System.Collections.Generic.HashSet<string> _insideNow = new System.Collections.Generic.HashSet<string>();

        internal static float StrokeAge() => _since >= 0f ? Time.unscaledTime - _since : -1f;
        internal static int StrokeNo => _seq;

        /// <summary>FloorCellClick MoveNext postfix - every frame of a live stroke. Two compares unless a line is due.</summary>
        internal static void OnFrame()
        {
            try
            {
                if (_since < 0f && !InMp) return;
                var cur = MopController.currentCleaningMop;
                if (cur == null) { if (_since >= 0f) End("stroke loop finished"); return; }
                if (_since < 0f || !ReferenceEquals(cur, _mop)) Begin(cur);
                float now = Time.unscaledTime;
                if (now >= _nextPresence) { _nextPresence = now + 1f; Presence(now); }
                if (now - _since >= WatchAfterS && now >= _nextWatch) { _nextWatch = now + WatchEveryS; Watch(now); }
            }
            catch { }
        }

        internal static void OnStop(string how) { try { if (_since >= 0f) End(how); } catch { } }

        // ── R2 (review 2026-09-28): the stroke coroutine threw (log-only finalizer, the exception is rethrown) ──
        private static int _threwFor = int.MinValue;
        internal static void StrokeThrew(Exception ex)
        {
            try
            {
                int seq = HelperCleaning.StrokeSeq;
                if (seq == _threwFor) return;   // once per stroke
                _threwFor = seq;
                Log($"stroke coroutine threw (stroke #{seq}, probe stroke #{_seq}, age {StrokeAge():F1}s): {ex.GetType().Name} {ex.Message}; mop still assigned={MopController.currentCleaningMop != null}, blockers=[{Blockers()}], putAwayPending={PutAwayPending()}, held={Held()}");
            }
            catch { }
        }

        internal static void OnUnassign() { try { if (_since >= 0f && MopController.currentCleaningMop == null) End("mop unassigned (Escape / hands cleared / building exit)"); } catch { } }

        private static string Cells(BuildingRegistration? reg)
        {
            var sb = new System.Text.StringBuilder();
            var s = reg?.dirtSpots;
            for (int k = 0; k < _n; k++)
            {
                int i = _idx[k];
                float v = (s != null && i >= 0 && i < s.Count && s[i] != null) ? s[i].dirtiness : -1f;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(i).Append(':').Append(v.ToString("F0"));
            }
            return sb.ToString();
        }

        private static int Left(BuildingRegistration? reg)
        {
            int left = 0; var s = reg?.dirtSpots;
            if (s == null) return -1;
            for (int k = 0; k < _n; k++) { int i = _idx[k]; if (i >= 0 && i < s.Count && s[i] != null && s[i].dirtiness > 0f) left++; }
            return left;
        }

        private static void Begin(MopController cur)
        {
            if (_since >= 0f) End("superseded by a new stroke");
            try { SessionCheck(); } catch { }   // R3: a new session's first stroke sees a fresh start budget
            _mop = cur; _since = Time.unscaledTime; _seq++;
            _hits = 0; _raisedCells = 0; _maxRaise = 0f; _nextWatch = 0f; _nextHitLog = 0f; _nextPresence = _since + 1f;
            _guardHits = 0; _guardCells = 0; _nextGuardLog = 0f;
            _reg = null; _addr = ""; _n = 0;
            try { _reg = cur.BuildingContext?.Registration; if (_reg != null) _addr = GameStateReader.AddressKey(_reg) ?? ""; } catch { }
            _shop = MPRegisterSync.CurrentShopAddress ?? "";
            var cells = HelperCleaning.AffectedCellsList();
            if (cells != null) foreach (var c in cells) { if (c == null || _n >= _idx.Length) continue; _idx[_n++] = c.DirtSpot; }
            FillInside(_inside);
            if (_starts++ >= StartBudget) return;
            bool helperAt = false, pushed = false, flipped = false, books = false, sim = false, mine = false;
            try
            {
                helperAt = BusinessHelperRoute.HelperAt(_reg, out _);
                pushed = GrantSync.IsHelperBusiness(_addr); flipped = MergerFlip.IsFlipped(_addr);
                if (_reg != null) { books = MergerFlip.BooksHere(_reg); mine = MergerFlip.TrulyMine(_reg); }
                sim = MergerAbsence.SimulatesHere(_addr);
            }
            catch { }
            int mopCtl = -1;
            try { var hand = PlayerHelper.PlayerController?.Character?.rightHand; if (hand != null) mopCtl = hand.GetComponentsInChildren<MopController>(true).Length; } catch { }
            Log($"stroke #{_seq} started in '{_addr}' ({(MPServer.IsRunning ? "host" : "client")}): {_n} cell(s) [{Cells(_reg)}] helperAt={helperAt} pushed={pushed} flipped={flipped} books={books} simulates={sim} trulyMine={mine} mopCtl={mopCtl} others=[{string.Join(",", _inside)}]");
        }

        private static void End(string how)
        {
            try
            {
                float age = StrokeAge();
                // F1 (2026-09-28): also every stroke whose start line was logged, and every stroke the guard held.
                if (age >= WatchAfterS || _hits > 0 || _guardHits > 0 || _starts <= StartBudget)
                    Log($"stroke #{_seq} in '{_addr}' ended ({how}) after {age:F1}s; raised by dirt updates {_hits}x ({_raisedCells} cell raises, max +{_maxRaise:F0}); stroke guard held {_guardCells} cell value(s) in {_guardHits} update(s); putAwayPending={PutAwayPending()}; held={Held()}");
            }
            catch { }
            _since = -1f; _mop = null; _reg = null; _n = 0; _inside.Clear();
        }

        private static void Watch(float now)
        {
            Log($"LIVELOCK? stroke #{_seq} in '{_addr}' alive {now - _since:F0}s: {Left(_reg)}/{_n} cell(s) still dirty [{Cells(_reg)}]; raised by dirt updates {_hits}x ({_raisedCells} cell raises, max +{_maxRaise:F0}); putAwayPending={PutAwayPending()}; blockers=[{Blockers()}]; held={Held()}; others=[{string.Join(",", _inside)}]");
        }

        private static void FillInside(System.Collections.Generic.HashSet<string> set)
        {
            set.Clear();
            try
            {
                if (string.IsNullOrEmpty(_shop)) return;
                foreach (var pid in RemotePlayerManager.GetRemotePlayerIds())
                    if (!string.IsNullOrEmpty(pid) && RemotePlayerManager.BuildingOf(pid) == _shop) set.Add(pid);
            }
            catch { }
        }

        private static void Presence(float now)
        {
            try
            {
                FillInside(_insideNow);
                foreach (var p in _insideNow)
                    if (!_inside.Contains(p))
                        Log($"other '{p}' ENTERED '{_shop}' during live stroke #{_seq} (age {now - _since:F1}s, cells still dirty {Left(_reg)}/{_n}, raises so far {_hits})");
                foreach (var p in _inside)
                    if (!_insideNow.Contains(p))
                        Log($"other '{p}' LEFT '{_shop}' during live stroke #{_seq} (age {now - _since:F1}s, cells still dirty {Left(_reg)}/{_n}, raises so far {_hits})");
                _inside.Clear(); _inside.UnionWith(_insideNow);
            }
            catch { }
        }

        // ── dirt writes against the live stroke ─────────────────────────────────────────────
        internal static bool LiveOn(BuildingRegistration? reg)
        {
            try
            {
                if (_since < 0f || reg == null || _n == 0 || MopController.currentCleaningMop == null) return false;
                if (ReferenceEquals(reg, _reg)) return true;
                return _addr.Length > 0 && GameStateReader.AddressKey(reg) == _addr;
            }
            catch { return false; }
        }

        internal static void Snap(BuildingRegistration reg)
        {
            try
            {
                var s = reg.dirtSpots;
                for (int k = 0; k < _n; k++)
                {
                    int i = _idx[k];
                    _snap[k] = (s != null && i >= 0 && i < s.Count && s[i] != null) ? s[i].dirtiness : -1f;
                }
            }
            catch { }
        }

        internal static void AfterWrite(BuildingRegistration reg, string source)
        {
            try
            {
                var s = reg.dirtSpots;
                if (s == null) return;
                int raised = 0; float max = 0f;
                for (int k = 0; k < _n; k++)
                {
                    int i = _idx[k];
                    if (_snap[k] < 0f || i < 0 || i >= s.Count || s[i] == null) continue;
                    float d = s[i].dirtiness - _snap[k];
                    if (d > 0.05f) { raised++; if (d > max) max = d; }
                }
                if (raised == 0) return;
                _hits++; _raisedCells += raised; if (max > _maxRaise) _maxRaise = max;
                float now = Time.unscaledTime;
                if (_hits > 1 && now < _nextHitLog) return;   // first raise of a stroke always, then one line per 10 s
                _nextHitLog = now + 10f;
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < _n; k++)
                {
                    int i = _idx[k];
                    if (_snap[k] < 0f || i < 0 || i >= s.Count || s[i] == null) continue;
                    float a = s[i].dirtiness;
                    if (a - _snap[k] <= 0.05f) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(i).Append(':').Append(_snap[k].ToString("F0")).Append("->").Append(a.ToString("F0"));
                }
                Log($"dirt update RAISED {raised} of {_n} cell(s) under live stroke #{_seq} in '{_addr}' (source={source}, stroke age {now - _since:F1}s, raise #{_hits}, max +{max:F0}): [{sb}]");
            }
            catch { }
        }

        /// <summary>H-CLEANLOOP-1 F1 (StrokeGuard): count a held-back apply; true when a line is due (first hold of a
        /// stroke always, then one line per 10 s).</summary>
        internal static bool GuardDue(int held)
        {
            try
            {
                if (!InMp) return false;
                _guardHits++; _guardCells += held;
                float now = Time.unscaledTime;
                if (_guardHits > 1 && now < _nextGuardLog) return false;
                _nextGuardLog = now + 10f;
                return true;
            }
            catch { return false; }
        }

        internal static void GuardLine(string addr, string source, int held, int of, string detail)
        {
            try
            {
                Log($"stroke guard held back {held} of {of} cell(s) under live stroke #{_seq} in '{addr}' (source={source}, stroke age {StrokeAge():F1}s, hold #{_guardHits}): [{detail}] - incoming->kept; the local mopped value stays, so the stroke can finish (H-CLEANLOOP-1 F1)");
            }
            catch { }
        }

        // ── put-away / Escape / hands cleared ──────────────────────────────────────────────
        private static float _nextPut, _nextEsc, _nextRem;

        internal static void PutAway(bool strokeWasLive)
        {
            try
            {
                if (!InMp) return;
                float now = Time.unscaledTime;
                if (now < _nextPut) return;
                _nextPut = now + 2f;
                bool pending = PutAwayPending();
                Log($"put-away (ReturnMopToStation): strokeLive={strokeWasLive} age={StrokeAge():F1}s putAwayPending={pending} -> "
                    + (strokeWasLive && pending ? "HELD BACK until the stroke ends" : "walking to the station / cleared now") + $"; held={Held()}");
            }
            catch { }
        }

        internal static void Escape(bool handled, bool strokeWasLive, float age)
        {
            try
            {
                if (!InMp) return;
                float now = Time.unscaledTime;
                if (now < _nextEsc) return;
                _nextEsc = now + 2f;
                Log($"Escape (MopController.HandleEscape): handled={handled} strokeWasLive={strokeWasLive} age={age:F1}s -> held={Held()} cleaning={MopController.currentCleaningMop != null} blockers=[{Blockers()}]");
            }
            catch { }
        }

        /// <summary>RemoveItemsFromHands prefix: -1 when no mop is held (nothing to say), else mopCtl*1000+attached.</summary>
        internal static int HandsBefore(out bool live)
        {
            live = false;
            try
            {
                if (!InMp || !PlayerHelper.IsHoldingAMop) return -1;
                live = MopController.currentCleaningMop != null;
                return HandCensus();
            }
            catch { return -1; }
        }

        private static int HandCensus()
        {
            var ch = PlayerHelper.PlayerController?.Character;
            var hand = ch?.rightHand;
            if (hand == null) return 0;
            int mopCtl = hand.GetComponentsInChildren<MopController>(true).Length;
            int attached = 0;
            try
            {
                var f = AccessTools.Field(ch!.GetType(), "_attachedRenderers");
                if (f?.GetValue(ch) is System.Collections.IDictionary d && d.Contains(hand) && d[hand] is System.Collections.ICollection c) attached = c.Count;
            }
            catch { attached = -1; }
            return mopCtl * 1000 + Math.Max(0, Math.Min(999, attached));
        }

        internal static void HandsAfter(int before, bool live)
        {
            try
            {
                if (before < 0) return;
                float now = Time.unscaledTime;
                if (now < _nextRem) return;
                _nextRem = now + 2f;
                int after = HandCensus();
                Log($"mop left the hands (RemoveItemsFromHands): mopCtl {before / 1000}->{after / 1000} (a destroyed one counts until frame end) rightAttached {before % 1000}->{after % 1000} strokeWasLive={live} cleaning={MopController.currentCleaningMop != null} held={Held()}");
            }
            catch { }
        }

        // ── the report gate's silent returns ────────────────────────────────────────────────
        private static readonly System.Collections.Generic.Dictionary<string, float> _gateAt = new System.Collections.Generic.Dictionary<string, float>();

        internal static void Gate(string reason)
        {
            try
            {
                if (!InMp) return;
                var bm = InstanceBehavior<BuildingManager>.Instance;
                var reg = bm?.buildingRegistration;
                if (reg != null && MergerFlip.TrulyMine(reg)) return;   // the owner's own stroke: no report is expected
                string addr = ""; try { if (reg != null) addr = GameStateReader.AddressKey(reg) ?? ""; } catch { }
                string key = reason + "|" + addr;
                float now = Time.unscaledTime;
                if (_gateAt.TryGetValue(key, out var t) && now < t) return;
                _gateAt[key] = now + 60f;
                bool nativeOwner = false; try { nativeOwner = bm != null && bm.IsPlayerOwnedBusiness; } catch { }
                Log($"report gate: '{addr}' {reason} - the cleaned cells are NOT reported to the owner (helperAt={BusinessHelperRoute.HelperAt(reg, out _)} pushed={GrantSync.IsHelperBusiness(addr)} flipped={MergerFlip.IsFlipped(addr)} books={(reg != null && MergerFlip.BooksHere(reg))} simulates={MergerAbsence.SimulatesHere(addr)} type='{reg?.businessTypeName}' nativeOwner={nativeOwner}; stroke #{_seq})");
            }
            catch { }
        }

        // ── why the owner / the host band re-sent dirt ─────────────────────────────────────
        private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<int, float>> _lastSent = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<int, float>>();
        private static readonly System.Collections.Generic.Dictionary<string, int> _resendN = new System.Collections.Generic.Dictionary<string, int>();
        private static readonly System.Collections.Generic.Dictionary<string, float> _resendAt = new System.Collections.Generic.Dictionary<string, float>();

        private static bool AnyRemoteIn(string addr)
        {
            try
            {
                foreach (var pid in RemotePlayerManager.GetRemotePlayerIds())
                    if (!string.IsNullOrEmpty(pid) && RemotePlayerManager.BuildingOf(pid) == addr) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Called right after a dirt-band message leaves (the owner client's upload, or the host's band to
        /// the players inside). Logs how many spots changed since the previous send and the top 5 deltas, only
        /// while another player is inside (owner upload) / someone subscribes (host band). 20 lines per address,
        /// then one per minute.</summary>
        internal static void OwnerDirtSent(string addr, InteriorDirtSyncPayload? p, string channel, bool needRemoteInside)
        {
            try
            {
                if (p?.Spots == null || string.IsNullOrEmpty(addr)) return;
                if (!_lastSent.TryGetValue(addr, out var last)) { last = new System.Collections.Generic.Dictionary<int, float>(); _lastSent[addr] = last; }
                bool first = last.Count == 0;
                if (!needRemoteInside || AnyRemoteIn(addr))
                {
                    _resendN.TryGetValue(addr, out var n);
                    float now = Time.unscaledTime;
                    bool due = n < ResendPerAddr || !_resendAt.TryGetValue(addr, out var at) || now >= at;
                    if (due)
                    {
                        _resendN[addr] = n + 1;
                        if (n + 1 >= ResendPerAddr) _resendAt[addr] = now + 60f;
                        var deltas = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, float>>();
                        var sent = new System.Collections.Generic.HashSet<int>();
                        foreach (var s in p.Spots)
                        {
                            if (s == null) continue;
                            sent.Add(s.Index);
                            last.TryGetValue(s.Index, out var was);
                            float d = s.Dirtiness - was;
                            if (Math.Abs(d) >= 0.1f) deltas.Add(new System.Collections.Generic.KeyValuePair<int, float>(s.Index, d));
                        }
                        foreach (var kv in last)
                            if (kv.Value >= 0.1f && !sent.Contains(kv.Key)) deltas.Add(new System.Collections.Generic.KeyValuePair<int, float>(kv.Key, -kv.Value));
                        deltas.Sort((x, y) => Math.Abs(y.Value).CompareTo(Math.Abs(x.Value)));
                        var sb = new System.Text.StringBuilder();
                        for (int k = 0; k < deltas.Count && k < 5; k++)
                        {
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(deltas[k].Key).Append(':').Append(deltas[k].Value >= 0 ? "+" : "").Append(deltas[k].Value.ToString("F1"));
                        }
                        Log($"owner dirt re-send ({channel}) '{addr}': {(first ? "first send, " : "")}{deltas.Count} spot(s) changed since the last send of {p.Spots.Count} dirty; top [{sb}] (line {n + 1}; 20 per address, then 1/min)");
                    }
                }
                last.Clear();
                foreach (var s in p.Spots) if (s != null) last[s.Index] = s.Dirtiness;
            }
            catch { }
        }
    }

    /// <summary>[PROBE:P-CLEANLOOP] log-only: the put-away (the item panel's discard button) - is it held back
    /// behind a live stroke? Prefix reads, Postfix logs; never changes the return.</summary>
    [HarmonyPatch(typeof(CleaningStationController), nameof(CleaningStationController.ReturnMopToStation))]
    public static class Patch_MopProbe_ReturnMop
    {
        static void Prefix(out bool __state) { __state = false; try { __state = MopController.currentCleaningMop != null; } catch { } }
        static void Postfix(bool __state) { MopProbe.PutAway(__state); }
    }

    /// <summary>[PROBE:P-CLEANLOOP] log-only: the Escape key's mop branch (CancelButtonHandler -> HandleEscape).</summary>
    [HarmonyPatch(typeof(MopController), nameof(MopController.HandleEscape))]
    public static class Patch_MopProbe_Escape
    {
        static void Prefix(out float __state) { __state = -1f; try { if (MopController.currentCleaningMop != null) __state = Math.Max(0f, MopProbe.StrokeAge()); } catch { } }
        static void Postfix(bool __result, float __state) { MopProbe.Escape(__result, __state >= 0f, __state); }
    }

    /// <summary>[PROBE:P-CLEANLOOP] log-only: the mop leaving the hands (put-away arrival, Escape, building exit).</summary>
    [HarmonyPatch(typeof(PlayerHelper), nameof(PlayerHelper.RemoveItemsFromHands))]
    public static class Patch_MopProbe_RemoveHands
    {
        static void Prefix(out int __state)
        {
            __state = -1;
            try { int b = MopProbe.HandsBefore(out bool live); if (b >= 0) __state = b * 2 + (live ? 1 : 0); } catch { __state = -1; }
        }
        static void Postfix(int __state) { if (__state >= 0) MopProbe.HandsAfter(__state / 2, (__state & 1) == 1); }
    }

    /// <summary>[PROBE:P-CLEANLOOP] log-only: a stroke that ends WITHOUT StopCleaning (UnAssignFromPlayer clears
    /// currentCleaningMop and the coroutine dies with the destroyed mop).</summary>
    [HarmonyPatch(typeof(MopController), nameof(MopController.UnAssignFromPlayer))]
    public static class Patch_MopProbe_Unassign
    {
        static void Postfix() { MopProbe.OnUnassign(); }
    }

    /// <summary>[PROBE:P-CLEANLOOP] log-only (review R2, 2026-09-28): a Harmony FINALIZER on the stroke coroutine's
    /// MoveNext (the compiler-generated MopController.&lt;FloorCellClick&gt;d__N iterator). When the coroutine throws
    /// (e.g. a dirt list shorter than the stroke's cells) it logs once per stroke whether the mop is still assigned and
    /// which navigation blockers remain, then RETURNS the exception so Harmony rethrows it - behaviour unchanged.
    /// Resolution miss: yields nothing (the latch patch on the same method already warns).</summary>
    [HarmonyPatch]
    public static class Patch_MopProbe_StrokeThrew
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
            if (mv != null) yield return mv;
        }
        static Exception? Finalizer(Exception? __exception)
        {
            if (__exception != null) MopProbe.StrokeThrew(__exception);
            return __exception;   // rethrow: never swallowed
        }
    }
}
