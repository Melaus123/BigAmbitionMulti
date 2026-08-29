using System;
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
        /// re-reads the registration each swing, so anything restored mid-run is simply re-cleaned.</summary>
        public static void ReportCleanedCells()
        {
            try
            {
                if (!MPClient.IsClientInWorld && !MPServer.IsRunning) return;
                if (!HousingFurniture.LocalHelperHere()) return;   // owners need no forward; only a granted helper

                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                var spots = reg?.dirtSpots;
                if (spots == null || spots.Count == 0) return;
                string addr = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(addr)) return;

                // AffectedCells is private static on MopController — the authoritative list of what the last
                // mop action touched.  Cleared at the start of every action, so it is never stale in a way that
                // matters: re-reporting the same already-clean cells is a no-op at the receiver.
                var cells = AccessTools.Field(typeof(MopController), "AffectedCells")?.GetValue(null)
                            as System.Collections.Generic.IEnumerable<DirtSpotObject>;
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
                foreach (var c in cells)
                {
                    if (c == null || payload.Spots.Count >= MaxSpotsPerMessage) continue;
                    int idx = c.DirtSpot;
                    if (idx < 0 || idx >= spots.Count || spots[idx] == null) continue;
                    var s = spots[idx];
                    payload.Spots.Add(new DirtSpotDeltaInfo { Index = idx, X = s.x, Z = s.z, Dirtiness = s.dirtiness });
                }
                if (payload.Spots.Count == 0) return;

                if (MPServer.IsRunning) MPServer.HandleBuildingDirtEdit(payload, MPConfig.PlayerId);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingDirtEdit, MPConfig.PlayerId, payload));
                Plugin.Logger.LogInfo($"[Cleaning] helper cleaned {payload.Spots.Count} cell(s) in '{addr}' — reported to the owner.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cleaning] ReportCleanedCells: {ex.Message}"); }
        }

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

                int applied = 0, skipped = 0;
                foreach (var d in p.Spots)
                {
                    if (d == null) continue;
                    int idx = d.Index;
                    // Trust the index only if it names the same cell; otherwise fall back to an X/Z lookup so
                    // a lattice built in a different order still lands on the right tile.
                    if (idx < 0 || idx >= spots.Count || spots[idx] == null || spots[idx].x != d.X || spots[idx].z != d.Z)
                    {
                        idx = -1;
                        for (int i = 0; i < spots.Count; i++)
                            if (spots[i] != null && spots[i].x == d.X && spots[i].z == d.Z) { idx = i; break; }
                        if (idx < 0) { skipped++; continue; }
                    }
                    float want = Mathf.Clamp(d.Dirtiness, 0f, 100f);
                    if (want >= spots[idx].dirtiness) { skipped++; continue; }   // never dirtier via this channel
                    spots[idx].dirtiness = want;
                    applied++;
                }

                if (applied > 0)
                    Plugin.Logger.LogInfo($"[Cleaning] applied {applied} cleaned cell(s) from '{p.SenderId}' for '{p.AddressKey}'"
                        + (skipped > 0 ? $" ({skipped} skipped — unmatched or not a reduction)." : "."));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cleaning] Apply: {ex.Message}"); }
        }
    }

    /// <summary>Round-112 B2 trigger: a mop action just finished, so report the cells it cleaned.  StopCleaning
    /// is public and is the last thing MopController.FloorCellClick calls, which makes it both a compile-time
    /// safe patch target and the exact moment the resting values are final.  It also runs if the action ends
    /// early (mop put away mid-clean), so a partial clean is still reported.</summary>
    [HarmonyPatch(typeof(MopController), nameof(MopController.StopCleaning))]
    public static class Patch_MopController_StopCleaning_Report
    {
        static void Postfix() { HelperCleaning.ReportCleanedCells(); }
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
