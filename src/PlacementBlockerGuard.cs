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
