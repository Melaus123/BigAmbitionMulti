using System.Reflection;
using HarmonyLib;
using Helpers;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// AUTOPARK-1 diagnostic — log-only, no behaviour change.
    ///
    /// Field report: on a client, auto-park teleports the car into a parked car /
    /// into the middle of the lane.  The suspected cause is that a client's parking
    /// lanes look EMPTY to the native spot generator (our pooled parked-car ghosts
    /// were never parented under the lane, and only CHILD MeshColliders count as
    /// obstacles), so a lane emits one lane-long spot centred mid-lane.  The fix
    /// lives in ParkedVehicleSync; this class only records what the game actually
    /// picked, on host and client alike, so the two can be compared.
    ///
    /// Hook point: VehicleParkingHelper.ParkInClosestSpot is an ITERATOR method, so a
    /// Harmony Postfix on it runs when the coroutine object is CREATED — i.e. exactly
    /// once per park attempt, before any of the body has executed.  That is the moment
    /// we want: `availableAutoParkSpot` still holds the chosen spot (the body nulls it),
    /// and the occupancy tests have not run yet.
    /// </summary>
    [HarmonyPatch(typeof(VehicleParkingHelper), nameof(VehicleParkingHelper.ParkInClosestSpot))]
    public static class Patch_AutoParkDiag_ParkInClosestSpot
    {
        private const int Budget = 60;          // lines per session, total
        private static int _left = Budget;

        private static readonly Collider[] _boxBuf   = new Collider[16];
        private static readonly Collider[] _nearBuf  = new Collider[64];
        private static MethodInfo? _miIsSpotOccupied;

        static void Postfix(VehicleParkingHelper __instance)
        {
            try
            {
                if (_left <= 0) return;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;   // MP sessions only

                // Our pooled parked-car ghosts carry this component but no CarController —
                // the same ghost test the Start/Update shields use (Patch_VehicleParkingHelper_SkipOnGhost).
                var car = __instance.GetComponentInParent<CarController>();
                if (car == null) return;

                var spot = __instance.availableAutoParkSpot;
                if (spot == null) return;

                _left--;

                Vector3 sp  = spot.transform.position;
                Vector3 cp  = car.transform.position;
                float   len = spot.maxVehicleLength;
                float   dist = Vector3.Distance(cp, sp);

                Quaternion rot = spot.transform.parent != null ? spot.transform.parent.rotation
                                                              : spot.transform.rotation;
                float width = spot.visuals != null ? spot.visuals.size.y : 2.5f;

                // Native occupancy verdict, if the private helper is still where we expect it.
                string occupiedNative = "n/a";
                if (_miIsSpotOccupied == null)
                    _miIsSpotOccupied = AccessTools.Method(typeof(VehicleParkingHelper), "IsSpotOccupied",
                                                           new[] { typeof(AutoParkSpot) });
                if (_miIsSpotOccupied != null)
                    occupiedNative = (_miIsSpotOccupied.Invoke(__instance, new object[] { spot }) as bool?)
                                     ?.ToString() ?? "n/a";

                // What actually stands in the spot on THIS machine.  vehicleConflictMask is
                // ParkedVehicles + AiVehicles — the ParkedVehicles layer is the one the native
                // free-spot mask deliberately leaves out, which is the whole point of the probe.
                int hits = Physics.OverlapBoxNonAlloc(sp, new Vector3(width, 2f, len) * 0.5f, _boxBuf, rot,
                                                      LayerHelper.vehicleConflictMask,
                                                      QueryTriggerInteraction.Ignore);
                string first = "-";
                if (hits > 0 && _boxBuf[0] != null)
                    first = $"{_boxBuf[0].name}/layer{_boxBuf[0].gameObject.layer}";

                int near = Physics.OverlapSphereNonAlloc(sp, 10f, _nearBuf, LayerHelper.vehicleConflictMask,
                                                         QueryTriggerInteraction.Ignore);

                Plugin.Logger.LogInfo(
                    $"[AutoPark] pick spot=({sp.x:F1},{sp.y:F1},{sp.z:F1}) len={len:F1} dist={dist:F1} " +
                    $"occupiedNative={occupiedNative} wouldHit={hits} first={first} parkedNear10m={near} " +
                    $"role={(MPServer.IsRunning ? "host" : "client")} (AUTOPARK-1)");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning($"[AutoPark] diagnostic postfix (AUTOPARK-1): {ex.Message}");
            }
        }
    }
}
