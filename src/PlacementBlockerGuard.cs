// PROBE-START: P-PLACEMENT-STRAND — why does PlacementMode never end, and what is being held?
//
// THE INCIDENT (client, 2026-08-29). A disconnect save failed three times with "an item is being
// placed" and wrote a 0-byte .hsg. Measured in the log afterwards:
//   * "[Rest] PlacementMode blocker SET" at line 7358; the save failed at 7853;
//   * Patch_NavBlockerUnset_PlacementOrigin (MPRestSync.cs:147-155) logs EVERY unset of that
//     blocker and NEVER FIRED anywhere in the 8283-line log.
// So placement mode was entered once and never left. The player did not know they were in it.
//
// HOW IT STARTS (from the game's own stack in that line): EntityController.OnIoRightClick()
// (EntityController.cs:249-254) -> ItemController.SecondaryInteract() (ItemController.cs:668-682,
// which requires: inside a building, not entering/exiting, and buildingRegistration.RentedByPlayer)
// -> PlacementHelper.StartPlacementMode(). A RIGHT-CLICK on an item in a shop you own.
// StartPlacementMode sets the nav blocker LAST, after every early return (PlacementHelper.cs:63-121),
// so a refused pick-up cannot strand it. This was a successful pick-up.
//
// THE SECOND FLAG NOBODY MENTIONS: StartPlacementMode also sets GameManager.preventAutoSave = true
// (:78). The ONLY thing that clears it is CancelPlacementMode -> SetPreventAutoSave(false) (:195).
// A stranded placement therefore ALSO silently disables autosave for the rest of the session.
//
// THE UNGUARDED HALF. CancelPlacementMode (PlacementHelper.cs:171-196) does a lot before the line
// that actually clears state, and several steps have no null guard:
//     :173  PlacementSystem.CurrentPlaceableItemBeingPlaced.GetItemInstance()   <- unguarded
//     :185  .Where((ItemController x) => x.Item.HasTag(...))                    <- no null check on x
//           (the loop at :175 DOES guard with x != null, so the omission at :185 is asymmetric)
//     :188  item2.transform.Find("SecondaryCoverageRadiusIndicator").gameObject <- unguarded Find
// A throw at any of those skips StopPlacingItem() (:194) and SetPreventAutoSave(false) (:195),
// stranding both flags. The mod ALREADY guards the mirror image on the way IN
// (Patch_PlacementHelper_StartPlacementMode_Guard, HousingPatches.cs:721-753, added after a
// 2026-07-20 field bug where a guest was locked up by exactly this). The way OUT had no guard.
// That asymmetry is the finding: a paired set/clear needs a guard on BOTH sides, not on whichever
// side happened to fail in the field first.
//
// WHAT THIS FILE DOES, AND DELIBERATELY DOES NOT DO. The user asked to UNDERSTAND the behaviour
// before anything auto-cancels on their behalf, so:
//   * it NEVER cancels a placement, and never clears a flag the game still considers live;
//   * it guards the native cancel so a THROW there cannot strand the pair. That is repairing a
//     failure the game already committed to, not a policy decision about the player's item;
//   * everything else is diagnosis: name the item, timestamp it, say how long it has been held.
using System;
using HarmonyLib;
using Buildings.Indoors.InteriorDesign;   // PlacementHelper

namespace BigAmbitionsMP
{
    internal static class PlacementWatch
    {
        private static float  _startedAt;
        private static string _what = "";
        private static float  _nextBeat;
        private static int    _beats;

        private static bool Held
        {
            get { try { return BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode; } catch { return false; } }
        }

        /// <summary>Identity of whatever is being placed, read LIVE at the moment of the question.
        /// The save gate uses this so a refused save names the culprit instead of saying "an item".</summary>
        internal static string Describe()
        {
            try
            {
                if (!Held) return "nothing";
                var inst = BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced?.GetItemInstance();
                string id = "";
                try { id = inst?.id.ToString() ?? ""; } catch { }
                string nm = "";
                try { nm = inst?.itemName ?? ""; } catch { }
                string held = _startedAt > 0f ? $", held {UnityEngine.Time.unscaledTime - _startedAt:0}s" : "";
                if (nm.Length == 0 && id.Length == 0) return $"an unidentified item{held}";
                return $"'{nm}' (id {id}){held}";
            }
            catch { return "an item (identity unreadable)"; }
        }

        private static string SafePreventAutoSave()
        {
            try { return GameManager.preventAutoSave ? "STILL TRUE (stranded)" : "false (cleared)"; }
            catch { return "unreadable"; }
        }

        internal static void NoteStart()
        {
            _startedAt = UnityEngine.Time.unscaledTime;
            _what      = Describe();
            _nextBeat  = _startedAt + 60f;
            _beats     = 0;
            Plugin.Logger.LogInfo($"[Placement] ENTERED placement mode with {_what}. "
                + "Autosave is now OFF (GameManager.preventAutoSave) and the game will refuse saves until this ends. "
                + "Nothing says so on screen.");
        }

        internal static void NoteEnd(string how)
        {
            if (_startedAt <= 0f)
            {
                Plugin.Logger.LogInfo($"[Placement] LEFT placement mode ({how}) — no recorded start.");
                return;
            }
            Plugin.Logger.LogInfo($"[Placement] LEFT placement mode ({how}) after "
                + $"{UnityEngine.Time.unscaledTime - _startedAt:0}s; held {_what}. "
                + $"preventAutoSave now {SafePreventAutoSave()}.");
            _startedAt = 0f; _what = ""; _beats = 0;
        }

        /// <summary>Heartbeat. Placement mode is meant to last seconds; a session-long one is the bug.
        /// Logging it on a cadence makes the NEXT occurrence visible in the log on its own, instead of
        /// waiting for a save to fail to reveal it — which is how this one was found, too late.</summary>
        internal static void Tick()
        {
            try
            {
                if (!Held)
                {
                    if (_startedAt > 0f) NoteEnd("noticed by the heartbeat, not by the unset hook");
                    return;
                }
                if (_startedAt <= 0f) { NoteStart(); return; }   // already active before we started watching
                float now = UnityEngine.Time.unscaledTime;
                if (now < _nextBeat) return;
                _nextBeat = now + 60f;
                if (_beats++ >= 30) return;                      // half an hour of evidence is plenty
                Plugin.Logger.LogWarning(
                    $"[Placement] STILL in placement mode after {now - _startedAt:0}s, holding {Describe()}. "
                  + $"preventAutoSave={SafePreventAutoSave()}. Saves are being refused; a disconnect right now "
                  + "would fail to write this player's own save.");
            }
            catch { }
        }
    }

    /// <summary>Records WHAT is being placed the moment a placement actually starts. The existing
    /// Patch_NavBlockerSet_PlacementOrigin (MPRestSync) already logs the call STACK; this adds the
    /// item. Postfix and only on a true result, so a refused pick-up is never counted as a start.</summary>
    [HarmonyPatch(typeof(PlacementHelper), nameof(PlacementHelper.StartPlacementMode),
                  typeof(ItemController), typeof(bool), typeof(bool))]
    internal static class Patch_PlacementHelper_StartPlacementMode_Note
    {
        static void Postfix(bool __result)
        {
            try { if (__result) PlacementWatch.NoteStart(); } catch { }
        }
    }

    /// <summary>The missing half of the pair. If the native cancel throws part-way, StopPlacingItem
    /// and SetPreventAutoSave(false) never run and BOTH flags strand for the session. This repairs
    /// that specific failure, loudly and with the full stack. It does not cancel anything on its own;
    /// it only finishes a teardown the game had already committed to.</summary>
    [HarmonyPatch(typeof(PlacementHelper), nameof(PlacementHelper.CancelPlacementMode))]
    internal static class Patch_PlacementHelper_CancelPlacementMode_Guard
    {
        static void Prefix()
        {
            try { Plugin.Logger.LogInfo($"[Placement] cancel requested for {PlacementWatch.Describe()}."); } catch { }
        }

        static Exception? Finalizer(Exception? __exception)
        {
            if (__exception == null)
            {
                try { PlacementWatch.NoteEnd("native cancel completed"); } catch { }
                return null;
            }

            Plugin.Logger.LogError(
                $"[Placement] CancelPlacementMode THREW ({__exception.GetType().Name}: {__exception.Message}) — "
              + "on a throw the native body abandons StopPlacingItem() and SetPreventAutoSave(false), which strands "
              + "BOTH the PlacementMode navigation blocker and the autosave suppression for the rest of the session. "
              + $"Finishing the teardown here.\n{__exception.StackTrace}");

            try { BigAmbitions.PlacementSystem.PlacementSystem.StopPlacingItem(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Placement] StopPlacingItem during repair: {e.Message}"); }
            try { GameManager.SetPreventAutoSave(false); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Placement] SetPreventAutoSave during repair: {e.Message}"); }
            try { InstanceBehavior<GameManager>.Instance.playerController.UnsetNavigationBlocker(NavigationBlocker.PlacementMode); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Placement] UnsetNavigationBlocker during repair: {e.Message}"); }

            try { PlacementWatch.NoteEnd("native cancel THREW — teardown completed by the mod"); } catch { }
            // Bug 235855 review MAJOR-2 (user-approved 2026-09-01): a Harmony postfix is skipped when the
            // original throws, so leg A (MPRadioSync.Patch_PlacementEnd_SpeakerUnpause) never runs on this
            // path and the speaker pause flag stays armed. StopPlacingItem has run above (IsInPlacementMode
            // false), so the same reconcile clears it here. Part of the GUARD, not the probe — survives
            // P-PLACEMENT-STRAND cleanup with the rest of this finalizer.
            try { MPRadioSync.ReconcileSpeakerPause("placement end (native cancel threw)"); } catch { }
            return null;   // swallow: re-throwing would abandon the teardown just performed
        }
    }
}
// PROBE-END: P-PLACEMENT-STRAND

// ORPHANED-MOVE REPAIR - BOX-PLACEMENT-STUCK-1, F2 (owner-approved 2026-10-02, decision 72). Part of the GUARD:
// it is outside the P-PLACEMENT-STRAND probe bracket on purpose and survives that probe's cleanup.
//
// THE FAILURE. The object being moved can be deleted mid-move (the restock in T-BOXGRAB-20261002-061901; any
// other removal does the same - BuildingRegistration.RemoveItemInstanceFromBuilding destroys the object,
// ItemController.cs:435-438). PlacementSystem.CurrentPlaceableItemBeingPlaced still points at the destroyed
// controller, so IsInPlacementMode stays true (PlacementSystem.cs:89) and every exit route throws on the
// missing transform before it clears anything:
//   * every frame:  PlacementHelper.Run -> PlacementSystem.UpdateItemPosition (the TickIso NRE), so a click
//                   never reaches the confirm path (PlacementHelper.cs:164-168);
//   * Escape:       CancelPlacementModeIfIsActive -> PlacementSystem.RevertPlacement (:653-655) throws BEFORE
//                   CancelPlacementMode is called - so the cancel guard above never ran, and nothing ever
//                   called SetPreventAutoSave(false): that is why preventAutoSave stayed TRUE in the run;
//   * any cancel:   CancelPlacementMode -> PlacementSystem.StopPlacingItem throws on its first line
//                   (TransformCached.tag, PlacementSystem.cs:689), and the guard's own StopPlacingItem retry
//                   threw the same way, so IsInPlacementMode never became false.
// THE REPAIR. StopPlacingItem gets the safe body for a gone object (the field clears of :700-711, the outline
// and grid steps only where their objects still exist), so the game's OWN CancelPlacementMode completes:
// SetPreventAutoSave(false), onPlacementModeEnd, unpause, time control, camera reset and - at the end of that
// coroutine - UnsetNavigationBlocker(PlacementMode). RevertPlacement has nothing to put back for a gone object
// and is skipped. PlacementHelper.Run ends such a move through that same native cancel on the next frame, so
// no exception repeats per frame and the player's next click is an ordinary click.
namespace BigAmbitionsMP
{
    internal static class OrphanedPlacement
    {
        private static object? _repairedFor;
        internal static int Repairs;   // DEV lever readout

        /// <summary>True when the game still says an item is being moved but that item's object no longer exists.</summary>
        internal static bool IsOrphaned()
        {
            var cur = BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced;
            if (cur == null) return false;
            if (cur is UnityEngine.Object uo && uo == null) return true;
            var t = BigAmbitions.PlacementSystem.PlacementSystem.TransformCached;
            return (object?)t != null && t == null;
        }

        /// <summary>What StopPlacingItem (PlacementSystem.cs:687-712) does, minus the steps on the gone object.</summary>
        internal static void FinishStopPlacing(string why)
        {
            var cur = BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced;
            try
            {
                if (cur is global::ItemController ic && ic.childItemControllers != null)
                    for (int i = 0; i < ic.childItemControllers.Count; i++)
                    {
                        var k = ic.childItemControllers[i];
                        if (k != null) { try { k.RemoveOutline(); } catch { } }
                    }
            }
            catch { }
            try
            {
                var lp = BigAmbitions.PlacementSystem.PlacementSystem.lastParentItem;
                if (lp != null && !(lp is UnityEngine.Object lpo && lpo == null)) lp.RemoveOutline();
            }
            catch { }
            try { BigAmbitions.PlacementSystem.PlacementSystem.currentBuildingGrid?.HideGrid(BigAmbitions.PlacementSystem.GridType.Both); } catch { }
            ClearFields();
            Repairs++;
            try { Plugin.Logger.LogWarning($"[Placement] moved object was GONE - {why}: placement state cleared the way StopPlacingItem would (IsInPlacementMode now {BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode})."); } catch { }
        }

        /// <summary>The field clears of StopPlacingItem (:701-711); the property uses the game's own setter.</summary>
        internal static void ClearFields()
        {
            BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced = null;
            BigAmbitions.PlacementSystem.PlacementSystem.TransformCached = null;
            BigAmbitions.PlacementSystem.PlacementSystem.ItemInstanceCached = null;
            BigAmbitions.PlacementSystem.PlacementSystem.ItemCached = null;
            BigAmbitions.PlacementSystem.PlacementSystem.IsItemRotating = false;
            BigAmbitions.PlacementSystem.PlacementSystem.IsNewItemPlacementValid = false;
            BigAmbitions.PlacementSystem.PlacementSystem.WasLastItemPlacementValid = true;
            BigAmbitions.PlacementSystem.PlacementSystem.lastAttachmentPoint = null;
            BigAmbitions.PlacementSystem.PlacementSystem.lastParentItem = null;
            BigAmbitions.PlacementSystem.PlacementSystem.InitialParentPlaceableItem = null;
            BigAmbitions.PlacementSystem.PlacementSystem.InitialParentAttachmentPoint = null;
        }

        /// <summary>Ends a move whose object is gone through the game's own cancel (main thread, from Run).</summary>
        internal static void EndOrphanedMove()
        {
            var cur = BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced;
            bool again = cur != null && ReferenceEquals(cur, _repairedFor);
            _repairedFor = cur;
            if (!again)
            {
                Plugin.Logger.LogWarning($"[Placement] the object being moved is GONE ({PlacementWatch.Describe()}) - "
                    + "ending the move through the game's own cancel (nothing is left to place).");
                bool designer = false;
                try { designer = global::UI.InteriorDesigner.InteriorDesignerUI.IsOpen; } catch { }
                PlacementHelper.CancelPlacementMode(setCamera: !designer);   // the guard above finalizes a throw
            }
            if (!BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode) return;
            // The native cancel could not finish twice for the same object: clear it directly, release the pair.
            Plugin.Logger.LogWarning("[Placement] native cancel left the gone object in place - clearing the placement state directly.");
            FinishStopPlacing("direct clear after the native cancel");
            try { GameManager.SetPreventAutoSave(false); } catch { }
            try { InstanceBehavior<GameManager>.Instance.playerController.UnsetNavigationBlocker(NavigationBlocker.PlacementMode); } catch { }
            try { PlacementWatch.NoteEnd("object gone - placement state cleared by the mod"); } catch { }
        }
    }

    /// <summary>A gone object: the safe body instead of a throw on line 1. Any OTHER throw is finished the same
    /// way and swallowed, so the caller (CancelPlacementMode) completes its own teardown.</summary>
    [HarmonyPatch(typeof(BigAmbitions.PlacementSystem.PlacementSystem), nameof(BigAmbitions.PlacementSystem.PlacementSystem.StopPlacingItem))]
    internal static class Patch_PlacementSystem_StopPlacingItem_OrphanRepair
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix()
        {
            try
            {
                if (!OrphanedPlacement.IsOrphaned()) return true;
                OrphanedPlacement.FinishStopPlacing("StopPlacingItem");
                return false;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Placement] orphan check in StopPlacingItem: {e.Message}"); return true; }
        }

        static Exception? Finalizer(Exception? __exception)
        {
            if (__exception == null) return null;
            try
            {
                Plugin.Logger.LogError($"[Placement] StopPlacingItem THREW ({__exception.GetType().Name}: {__exception.Message}) - "
                    + $"finishing its field clears so placement mode can end.\n{__exception.StackTrace}");
                OrphanedPlacement.FinishStopPlacing("StopPlacingItem threw");
            }
            catch { }
            return null;
        }
    }

    /// <summary>Escape on a gone object: nothing to put back; let CancelPlacementModeIfIsActive reach the cancel.</summary>
    [HarmonyPatch(typeof(BigAmbitions.PlacementSystem.PlacementSystem), nameof(BigAmbitions.PlacementSystem.PlacementSystem.RevertPlacement))]
    internal static class Patch_PlacementSystem_RevertPlacement_OrphanRepair
    {
        static bool Prefix()
        {
            try
            {
                if (!OrphanedPlacement.IsOrphaned()) return true;
                Plugin.Logger.LogInfo("[Placement] RevertPlacement skipped - the moved object is gone, nothing to put back.");
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>Per frame (GameManager.cs:562): a move whose object is gone is ended once through the native cancel.
    /// Not placing: one static read and a null test, no allocation.</summary>
    [HarmonyPatch(typeof(PlacementHelper), nameof(PlacementHelper.Run))]
    internal static class Patch_PlacementHelper_Run_OrphanRepair
    {
        static bool Prefix()
        {
            if (BigAmbitions.PlacementSystem.PlacementSystem.CurrentPlaceableItemBeingPlaced == null) return true;
            try
            {
                if (!OrphanedPlacement.IsOrphaned()) return true;
                OrphanedPlacement.EndOrphanedMove();
                return false;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Placement] orphaned-move repair: {e.Message}"); return true; }
        }
    }
}
