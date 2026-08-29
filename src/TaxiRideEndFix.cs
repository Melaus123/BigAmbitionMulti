// ── Game 1.0: the end-of-taxi-ride re-spawn, and why a CLIENT died there ──────────────────────
// FIELD EVIDENCE (2026-08-29, Player-instance2.log — one unremarked NRE):
//     NullReferenceException
//       at TaxiController.GetVehiclePrefab ()
//       at TaxiSystem+<TravelCoroutine>d__9.MoveNext ()
//
// WHAT 1.0 ADDED. TaxiSystem.TravelCoroutine grew a whole new tail (TaxiSystem.cs:85-101 — none of
// it exists in 0.11): after the player is warped and made visible, the game re-spawns the cab so it
// can drive away —
//     Waypoint w = TrafficManager.Instance.GetClosestWaypoint(pos, 50f, IsValidWaypointForLeaving,
//                                                             taxi.GetVehiclePrefab());
//     if (w != null) taxi.InstantiateVehicle(w); else ...DismissPrivateDriver(...);
// and GetVehiclePrefab (TaxiController.cs:58-61, also NEW in 1.0) is
//     return _vehicleComponent.prefab.GetComponent<VehicleComponent>();
//
// WHY IT THROWS ON A CLIENT. A client's taxis are mod GHOSTS: cloned INACTIVE from Gley's pool and
// stripped (TrafficSync ~:880-930). Taxis deliberately keep TaxiController and keep VehicleComponent
// (disabled), but a clone created inactive never runs TaxiController.Awake — and Awake is the only
// place _vehicleComponent is assigned (TaxiController.cs:32). So the ghost reaches 1.0's new call
// with either _vehicleComponent or its .prefab null.
//
// WHY IT MATTERS MUCH MORE THAN ONE LOGGED NRE. Look at what comes AFTER the throw:
//     yield return UiFader.UnFade();     // TaxiSystem.cs:103  -> the client stays on a faded screen
//     _travelCoroutine = null;           // TaxiSystem.cs:104
// and TravelTo opens with `if (_travelCoroutine != null) return;` (TaxiSystem.cs:37-41). The field
// is never cleared, so EVERY LATER TAXI RIDE THAT SESSION IS SILENTLY REFUSED — no message, no log.
// The warp, visibility and UnsetNavigationBlocker all happen BEFORE the throw (:81-84), so the
// player can still walk: it reads as "taxis just stopped working", not as a crash. That is exactly
// how it survived a field session as a single unremarked line.
//
// THE FIX (user-chosen 2026-08-29: "give it enough of a blueprint to satisfy the game").
//   1. GetVehiclePrefab — when, and ONLY when, the native path would throw, hand back a REAL pooled
//      traffic prefab's VehicleComponent. The game uses it purely as a filter argument to
//      GetClosestWaypoint, so a genuine taxi prefab is exactly the right answer. On the host, and on
//      any client vehicle that does carry a live _vehicleComponent, this patch stands aside entirely.
//   2. InstantiateVehicle — allowed to run natively (that is what "satisfy the game" means), but
//      wrapped in a Finalizer that swallows and LOGS any throw. Rationale: it does
//      Manager.RemoveVehicle(this.gameObject) on what is, here, a mod ghost Gley never registered,
//      then LoadVehicle. Whether that is safe is a RUNTIME question no amount of reading settles —
//      and the cost of being wrong is the identical black-screen-plus-dead-taxis outcome this file
//      exists to remove. The Finalizer makes the coroutine reach UnFade and clear _travelCoroutine
//      no matter what happens inside.
//
// PROBE, not silent: both paths log. If a phantom local car ever appears on a client after a ride,
// [TaxiEnd] respawn lines are where to look first.
using System;
using GleyTrafficSystem;   // VehicleComponent
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    internal static class TaxiRideEndFix
    {
        private static int _prefabLogs;
        private static int _respawnLogs;

        /// <summary>Reads TaxiController._vehicleComponent without assuming it is set.</summary>
        private static VehicleComponent? LiveVehicleComponent(object taxi)
        {
            try
            {
                var f = taxi.GetType().GetField("_vehicleComponent",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Public);
                return f?.GetValue(taxi) as VehicleComponent;
            }
            catch { return null; }
        }

        [HarmonyPatch]
        internal static class Patch_TaxiController_GetVehiclePrefab_GhostSafe
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("TaxiController")?.GetMethod("GetVehiclePrefab",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            static bool Prefix(object __instance, ref VehicleComponent __result)
            {
                try
                {
                    var vc = LiveVehicleComponent(__instance);
                    if (vc != null && vc.prefab != null) return true;      // healthy — native runs

                    var prefab = TrafficSync.PooledPrefab("Taxi");
                    var sub = prefab != null ? prefab.GetComponent<VehicleComponent>() : null;
                    __result = sub!;

                    if (_prefabLogs++ < 6)
                        Plugin.Logger.LogInfo(
                            $"[TaxiEnd] GetVehiclePrefab on a ghost (vehicleComponent={(vc == null ? "NULL" : "present")}, "
                          + $"prefab={(vc != null && vc.prefab != null ? "present" : "NULL")}) \u2014 substituted the pooled Taxi "
                          + $"prefab ({(sub != null ? "OK" : "POOL NOT READY \u2014 returning null; the game will take the no-waypoint branch")}). "
                          + "Without this the ride coroutine dies here and taxis stop working for the session.");
                    return false;                                          // skip native (it would NRE)
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[TaxiEnd] GetVehiclePrefab guard: {ex.Message}");
                    __result = null!;
                    return false;
                }
            }
        }

        [HarmonyPatch]
        internal static class Patch_TaxiController_InstantiateVehicle_Contain
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("TaxiController")?.GetMethod("InstantiateVehicle",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            static Exception? Finalizer(Exception? __exception)
            {
                if (__exception != null && _respawnLogs++ < 6)
                    Plugin.Logger.LogWarning(
                        $"[TaxiEnd] InstantiateVehicle threw and was CONTAINED: {__exception.GetType().Name}: {__exception.Message}. "
                      + "The cab does not respawn locally; the ride completes normally (screen un-fades, taxis stay usable). "
                      + "On a client the host's traffic snapshot is what shows the cab leaving anyway.");
                return null;   // swallow: a throw here would strand the coroutine before UnFade
            }
        }
    }
}
