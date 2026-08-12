using System;
using System.Collections.Generic;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Round-232 (rig batch test, user-observed): the OWNER got a parking ticket while a
    /// borrower was driving the car. Native ParkingSimulator.RunHourly skips a car only when the
    /// LOCAL player drives it (ActiveVehicleId check) — a remotely-driven car still reads as
    /// "parked" with the legality of its PICKUP spot, so Illegal keeps rolling -$125 tickets and
    /// Legal keeps accruing meter fees while the car is in motion across town. Vanilla can never
    /// fine a car that is being driven; restore that rule for cars in the owned-follow set.
    ///
    /// Scoped shim (the takeover-RentedByPlayer try/finally pattern): for the duration of the
    /// native pass only, a remotely-driven instance reads ParkingState.Undefined (the "in motion /
    /// no parking data" state — the pass has no case for it), restored in the Finalizer so nothing
    /// persists. Fines for a car AT REST stay the owner's burden per the standing fairness ruling;
    /// the release packet's ParkState (round-232, VehicleManager) keeps that verdict honest.</summary>
    [HarmonyPatch(typeof(Helpers.ParkingSimulator), nameof(Helpers.ParkingSimulator.RunHourly))]
    public static class Patch_ParkingSimulator_SkipRemotelyDriven
    {
        private static bool _loggedOnce;

        static void Prefix(out List<KeyValuePair<VehicleInstance, Helpers.ParkingState>> __state)
        {
            __state = null;
            try
            {
                var list = SaveGameManager.Current?.VehicleInstances;
                if (list == null) return;
                foreach (var inst in list)
                {
                    if (inst?.id == null) continue;
                    if (!VehicleManager.IsDrivenRemotely(inst.id)) continue;
                    (__state ??= new List<KeyValuePair<VehicleInstance, Helpers.ParkingState>>())
                        .Add(new KeyValuePair<VehicleInstance, Helpers.ParkingState>(inst, inst.parkingState));
                    inst.parkingState = Helpers.ParkingState.Undefined;
                }
                if (__state != null && !_loggedOnce)
                {
                    _loggedOnce = true;
                    Plugin.Logger.LogInfo($"[Drive] parking pass: {__state.Count} remotely-driven car(s) exempt while in motion (first occurrence this session; further exemptions silent).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Drive] parking shim: {ex.Message}"); }
        }

        static void Finalizer(List<KeyValuePair<VehicleInstance, Helpers.ParkingState>> __state)
        {
            if (__state == null) return;
            foreach (var kv in __state)
                try { kv.Key.parkingState = kv.Value; } catch { }
        }
    }
}
