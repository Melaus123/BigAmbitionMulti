using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    // PROBE-START: P-ENTRY-BAIL (this whole file is the probe — delete the file to remove it)
    /// <summary>Field 20260830-150521 follow-up (user-approved 2026-08-31): in MP,
    /// BuildingManager.DelayedEnterBuildingActions — the one-frame-after-entry coroutine whose
    /// LAST action staffs AI shops (SetupAiEmployeeStations) — silently fails to reach that
    /// call for MOST building entries (measured: 3/10 addresses on the bundle's client — that
    /// half is denominator-solid; 3 of at most 14 on its host — see review F16). Unmanned rival
    /// registers ⇒ the self-checkout queue is never served ⇒ the PurchaseUI navigation blocker
    /// holds ⇒ "stuck in the supermarket". No exception appears near any entry, so the bail
    /// point is invisible in field logs.
    ///
    /// WHAT THIS LOGS (MP only; entries happen at player-walking pace, not a hot path):
    ///   [PROBE] EntryBail armed #N '&lt;addr&gt;' entering=Y/N        — one per enumerator creation
    ///   [PROBE] EntryBail '&lt;addr&gt;' #N: &lt;verdict&gt; | pumps= ...    — one per entry outcome
    /// The armed line exists so "the routine never even STARTED for this entry" is measurable
    /// (review F16): a [ShopCtx] entry with no armed line = the coroutine was never created —
    /// a distinct outcome the outcome-line alone cannot express. entering= reads the public
    /// BuildingManager.enteringBuilding flag: TRUE on the real entry path; FALSE on the
    /// second native creation site (UI.BuildingPreview.CancelLayoutPreview:287, review F7),
    /// which then disables the owning MonoBehaviour and legitimately abandons the coroutine —
    /// an entering=N "NEVER PUMPED/ABANDONED" verdict is that benign path, not the bug.
    ///
    /// HOW THE VERDICT IS DERIVED — the coroutine body has exactly ONE yield
    /// (BuildingManager.cs:1050-1085): MoveNext #1 runs to `yield return null`; MoveNext #2
    /// runs the ENTIRE remainder (first IsInsideBuilding re-check → SpawnPlayerVehicles →
    /// second re-check → purchaser price loop → SetupAiEmployeeStations → onEnterBuildingDelayed).
    /// So:
    ///   threw                  → the body raised; the finalizer's EXCEPTION line above names it
    ///                            (review F8 — Harmony skips the pump postfix on a throw, so
    ///                            without the flag this would misreport as ABANDONED).
    ///   pumps=0                → enumerator created but NEVER PUMPED (owner stopped/disabled
    ///                            before frame+1).
    ///   pumps=1, never ended   → pumped to the yield, then ABANDONED between the two frames.
    ///                            Reported late, when the NEXT entry arms.
    ///   ended, vehicles=N      → took the FIRST `if (!IsInsideBuilding) yield break` (or the
    ///                            address-undefined error path — that one prints its own native
    ///                            "Building.Address is Undefined!!!" line right there).
    ///   ended, vehicles=Y, staffed=N → took the SECOND IsInsideBuilding break.
    ///   ended, staffed=Y       → COMPLETED; native AI staffing ran (its decisions are
    ///                            covered by [StaffSpawn], which stays once-per-address).
    ///
    /// Log-only by standing ruling: nothing here changes control flow; the finalizer returns
    /// the exception it was given, so native error propagation is untouched. Entries are
    /// serialized by the game (enteringBuilding gate), so one current-record slot suffices;
    /// a record abandoned at session end goes unreported (at most one lost line per launch).</summary>
    public static class EntryBailProbe
    {
        private static bool _armed;
        private static string _addr = "";
        private static object? _enum;         // the tracked enumerator instance (reference identity)
        private static int _seq;              // per-launch entry counter — pairs armed/outcome lines
        private static int _pumps;
        private static bool _entering;        // BuildingManager.enteringBuilding at arm time (review F7)
        private static bool _vehicles;        // SpawnPlayerVehicles waypoint reached
        private static bool _vehiclesInside;  // IsInsideBuilding as seen at that waypoint
        private static bool _staffed;         // SetupAiEmployeeStations reached
        private static bool _threw;           // body exception seen by the finalizer (review F8)
        private static bool _reported;
        private static float _t0;

        private static bool InMp => MPServer.IsRunning || MPClient.IsClientInWorld;

        private static void Report(string ending)
        {
            if (_reported || !_armed) return;
            _reported = true;
            string verdict =
                _threw               ? "body THREW (see the EXCEPTION line above)"
                : _staffed           ? "COMPLETED (staffing reached)"
                : _pumps == 0        ? "NEVER PUMPED (owner stopped/disabled before frame+1)"
                : _pumps == 1 && ending == "late" ? "ABANDONED between the yield and the body (coroutine stopped)"
                : !_vehicles         ? "BAILED at the FIRST IsInsideBuilding check (before vehicle spawn)"
                :                      "BAILED after vehicle spawn, before staffing (second IsInsideBuilding check)";
            bool insideNow = false; try { insideNow = BuildingManager.IsInsideBuilding; } catch { }
            var line = $"[PROBE] EntryBail '{_addr}' #{_seq}: {verdict} | pumps={_pumps} entering={(_entering ? "Y" : "N")} vehicles={(_vehicles ? "Y" : "N")}"
                     + (_vehicles ? $"(inside={_vehiclesInside})" : "")
                     + $" staffed={(_staffed ? "Y" : "N")} ended={ending} IsInsideBuilding(now)={insideNow} dt={UnityEngine.Time.unscaledTime - _t0:F2}s";
            if (_staffed || (!_entering && !_threw)) Plugin.Logger.LogInfo(line);   // preview-path abandons are benign (review F7)
            else Plugin.Logger.LogWarning(line);
            _enum = null;   // review F15: the finished state machine need not stay reachable
        }

        /// <summary>Arm a fresh record when the enumerator is CREATED (the caller reached
        /// `DelayedEnterBuildingActions()`; buildingRegistration is already set by then on the
        /// real entry path). An unreported previous record is reported late here — that is how
        /// never-pumped/abandoned entries become visible at all.</summary>
        [HarmonyPatch(typeof(BuildingManager), "DelayedEnterBuildingActions")]
        public static class Patch_EntryBail_Arm
        {
            static void Postfix(BuildingManager __instance, System.Collections.IEnumerator __result)
            {
                try
                {
                    if (!InMp) { _armed = false; _enum = null; return; }
                    Report("late");   // flush an unfinished predecessor first
                    _armed = true; _reported = false;
                    _enum = __result; _pumps = 0; _vehicles = false; _vehiclesInside = false;
                    _staffed = false; _threw = false;
                    _t0 = UnityEngine.Time.unscaledTime;
                    _seq++;
                    _addr = ""; _entering = false;
                    try
                    {
                        var reg = __instance?.buildingRegistration;
                        if (reg != null) _addr = GameStateReader.AddressKey(reg);
                        if (__instance != null) _entering = __instance.enteringBuilding;   // review F7: public field
                    }
                    catch { }
                    Plugin.Logger.LogInfo($"[PROBE] EntryBail armed #{_seq} '{_addr}' entering={(_entering ? "Y" : "N")}");   // review F16
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PROBE] EntryBail arm: {ex.Message}"); }
            }
        }

        /// <summary>Count pumps of the tracked enumerator and report the moment it ends
        /// (MoveNext returns false — completion and yield-break are told apart by the
        /// waypoint flags). A body exception is logged by the finalizer and RETHROWN,
        /// so native behavior is unchanged. Review F1: targets via TargetMethods that
        /// yields nothing on a resolution failure — a diagnostic must degrade to DEAD
        /// (loader counts it deadClasses), never to a patch FAILURE, because a failure
        /// flips PatchesDegraded and shows every player the on-screen warning.</summary>
        [HarmonyPatch]
        public static class Patch_EntryBail_Pump
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                System.Reflection.MethodBase? mn = null;
                try
                {
                    var it = AccessTools.Method(typeof(BuildingManager), "DelayedEnterBuildingActions");
                    mn = it == null ? null : AccessTools.EnumeratorMoveNext(it);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PROBE] EntryBail MoveNext target: {ex.Message}"); }
                if (mn == null)
                {
                    Plugin.Logger.LogWarning("[PROBE] EntryBail: MoveNext unresolved — pump patch goes dead; probe degrades to arm/waypoint lines only.");
                    yield break;
                }
                yield return mn;
            }

            static void Postfix(object __instance, bool __result)
            {
                try
                {
                    if (!_armed || !ReferenceEquals(__instance, _enum)) return;
                    _pumps++;
                    if (!__result) Report("ended");
                }
                catch { }
            }

            static Exception? Finalizer(Exception? __exception, object __instance)
            {
                try
                {
                    if (__exception != null && ReferenceEquals(__instance, _enum))
                    {
                        _threw = true;   // review F8: the pump postfix is skipped on a throw
                        Plugin.Logger.LogWarning($"[PROBE] EntryBail '{_addr}' #{_seq}: body EXCEPTION at pump {_pumps + 1}: {__exception}");
                        Report("threw");
                    }
                }
                catch { }
                return __exception;   // rethrow — log-only, native propagation untouched
            }
        }

        /// <summary>Waypoint 1: SpawnPlayerVehicles has street/parking call sites too, so it
        /// only counts while an entry record is armed and fresh. Review F9: 10s window — the
        /// coroutine reaches it one frame after arming, but a single load hitch can exceed
        /// 2s; 10s still excludes later street/parking calls masquerading as the waypoint.</summary>
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.SpawnPlayerVehicles))]
        public static class Patch_EntryBail_VehiclesWaypoint
        {
            static void Postfix()
            {
                try
                {
                    if (!_armed || _reported || _vehicles) return;
                    if (UnityEngine.Time.unscaledTime - _t0 > 10f) return;
                    _vehicles = true;
                    try { _vehiclesInside = BuildingManager.IsInsideBuilding; } catch { }
                }
                catch { }
            }
        }

        /// <summary>Waypoint 2: staffing reached. A postfix runs even when the
        /// Patch_NoAiStaffInPlayerShops prefix skips the body — "reached" is exactly what
        /// this probe measures (what native then decides is [StaffSpawn]'s job). Third
        /// patch on this method; the loader's shared-target annotation is expected.</summary>
        [HarmonyPatch(typeof(BuildingManager), "SetupAiEmployeeStations")]
        public static class Patch_EntryBail_StaffingWaypoint
        {
            static void Postfix()
            {
                try { if (_armed && !_reported) _staffed = true; }
                catch { }
            }
        }
    }
    // PROBE-END: P-ENTRY-BAIL
}
