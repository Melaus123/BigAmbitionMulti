using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Round-231b — drive-in entrances vs borrowed proxies (field 20260802-125303:
    /// borrowed Vord at a wholesaler drive-in → "there is no parking there", player glitched
    /// into the building volume, car left dormant-tagged inside).
    ///
    /// The AFTERMATH machinery (building-stamped drive stream, dormant owner-side release,
    /// wake-on-interior-load, interior masks) already exists and behaved per design — what's
    /// unlocalized is WHY the native entry transition refuses/half-runs for a proxy. These
    /// probes name the failing branch: TryToEnterWithCar's refusal is silent (any false →
    /// the blacklist + the player-facing error), so we recompute its condition vector when
    /// it refuses a BAMP_ proxy, and log EnterBuildingWithVehicle's verdict for proxies.
    /// Logging only; zero behavior change.</summary>
    [HarmonyPatch(typeof(DriveInEntrance), nameof(DriveInEntrance.TryToEnterWithCar))]
    public static class Probe_DriveIn_TryToEnterWithCar_Proxy
    {
        static void Postfix(DriveInEntrance __instance, CarController carController, bool __result)
        {
            try
            {
                var inst = carController?.vehicleInstance;
                if (inst?.id == null || !inst.id.StartsWith("BAMP_")) return;   // proxies only — owners' entries are native-normal
                var cbc = Traverse.Create(__instance).Field("cityBuildingController").GetValue<CityBuildingController>();
                if (__result)
                {
                    Plugin.Logger.LogInfo($"[DriveIn] proxy '{inst.id}' ACCEPTED at '{(cbc != null ? GameStateReader.AddressKey(cbc.buildingRegistration) : "?")}' — native transition ran.");
                    return;
                }
                // Refused — recompute the same checks the native chain consults so the log names the branch.
                string addr = "?", why = "";
                try
                {
                    if (cbc == null) { why = "cityBuildingController NULL"; }
                    else
                    {
                        addr = GameStateReader.AddressKey(cbc.buildingRegistration);
                        int slot = Traverse.Create(__instance).Field("doorID").GetValue<int>() + 1;
                        bool canEnter = false, blocked = false, whFull = false, slotUsed = false, entranceBlocked = false;
                        try { canEnter = Helpers.BuildingHelper.CanEnterBuilding(cbc.building.Address); } catch { }
                        try { blocked = BuildingManager.IsBuildingBlockedByAnyService(cbc); } catch { }
                        try { whFull = Traverse.Create(typeof(BuildingManager)).Method("IsWarehouseFull", cbc).GetValue<bool>(); } catch { }
                        try { slotUsed = Helpers.BuildingHelper.VehicleSlotIsUsed(cbc, slot); } catch { }
                        try { entranceBlocked = Helpers.BuildingHelper.IsAnyCarBlockingTheEntrance(carController, slot, cbc.building); } catch { }
                        why = $"slot={slot} canEnter={canEnter} blockedByService={blocked} warehouseFull={whFull} slotUsed={slotUsed} entranceBlocked={entranceBlocked}";
                    }
                }
                catch (Exception ix) { why = $"probe recompute failed: {ix.Message}"; }
                Plugin.Logger.LogWarning($"[DriveIn] proxy '{inst.id}' REFUSED at '{addr}' → blacklisted from parking ('no parking there'). {why}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DriveIn] probe: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.EnterBuildingWithVehicle))]
    public static class Probe_DriveIn_EnterWithVehicle_Proxy
    {
        static void Postfix(CityBuildingController cbc, int vehicleSlot, bool __result)
        {
            try
            {
                var sel = InstanceBehavior<GameManager>.Instance?.selectedVehicle;
                var inst = sel?.vehicleInstance;
                if (inst?.id == null || !inst.id.StartsWith("BAMP_")) return;
                string addr = cbc != null ? GameStateReader.AddressKey(cbc.buildingRegistration) : "?";
                Plugin.Logger.LogWarning($"[DriveIn] EnterBuildingWithVehicle proxy '{inst.id}' at '{addr}' slot={vehicleSlot} → {(__result ? "SUCCEEDED (transition committed)" : "FALSE (aborted after its own checks)")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DriveIn] enter probe: {ex.Message}"); }
        }
    }
}
