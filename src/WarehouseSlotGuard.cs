using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>SLOT-GUARD-1 (user-approved "guard build", 2026-09-18).  A partner's truck is a mod
    /// GHOST on this machine — the mod removes ghosts from SaveGameManager.Current.VehicleInstances
    /// at spawn (VehicleManager, the ghost-spawn path).  Driving one into your OWN warehouse made
    /// native CarController.CheckIfWarehouseSlotShouldBeSet write that non-local id into a vehicle
    /// slot; native Drivers.HasDriverEnoughSkill then does VehicleInstances.First(id), which THROWS
    /// on every driver pick, and the driver station shows no vehicle at all.  The slot write is a
    /// cause this mod introduced, so it is stopped at the source: a vehicle that is not in the LOCAL
    /// save list is never assigned to a slot (no slot write, no notification).  Local vehicles are
    /// untouched, and single player is untouched — every vehicle there is local.</summary>
    [HarmonyPatch(typeof(CarController), "CheckIfWarehouseSlotShouldBeSet")]
    public static class WarehouseSlotGuard
    {
        private const int LogCap = 20;
        private static int _logged;

        static bool Prefix(CarController __instance)
        {
            try
            {
                var inst = __instance?.vehicleInstance;
                string id = inst?.id ?? "";
                bool local = false;
                if (!string.IsNullOrEmpty(id))
                {
                    try { local = SaveGameManager.Current != null && SaveGameManager.Current.VehicleInstances.Exists(x => x != null && x.id == id); }
                    catch { local = true; }   // can't tell → behave exactly like the plain game
                }
                if (local) return true;
                string addr = "";
                int cleared = 0;
                try
                {
                    var bm = InstanceBehavior<BuildingManager>.Instance;
                    addr = bm?.buildingRegistration?.Address?.ToString() ?? "";
                    // FOLD F5 (L2, 2026-09-18): native does TWO halves — it CLEARS every slot naming this
                    // vehicle on this warehouse, then assigns it to the bay.  Refusing the whole method would
                    // leave a stale slot still naming this id behind (an older record names it, the truck is
                    // driven out).  Run the clearing half only, under exactly the conditions native itself
                    // checks (CarController.CheckIfWarehouseSlotShouldBeSet: player-owned business, building
                    // type warehouse) plus TrulyMine — a flipped partner warehouse's slots are the partner's
                    // business, never ours to edit.
                    if (!string.IsNullOrEmpty(id) && bm != null && bm.IsPlayerOwnedBusiness
                        && bm.building != null && bm.building.BuildingType == "ba:buildingtype_warehouse"
                        && bm.buildingRegistration is Entities.Warehouse gw && MergerFlip.TrulyMine(gw))
                    {
                        gw.ClearSlotAssignments(id);   // native clearing call, the same field native nulls
                        cleared = 1;
                    }
                }
                catch { }
                if (_logged++ < LogCap)
                    Plugin.Logger.LogInfo($"[SlotGuard] '{inst?.vehicleTypeName}' ({id}) is not a local vehicle — warehouse slot not assigned at '{addr}'{(cleared > 0 ? " (any slot naming it here cleared — native's own clearing half)" : "")} (SLOT-GUARD-1).");
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[SlotGuard] WarehouseSlotGuard prefix FAILED (SLOT-GUARD-1): {ex.Message}");
                return true;
            }
        }
    }
}
