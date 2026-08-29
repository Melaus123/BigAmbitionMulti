// ── Hamptons guest access (user decision 2026-08-29: RESIDENCE-GRANT requirement) ────────────────
//
// 1.0's Hamptons private zones have two locks, both keyed to the LOCAL machine's tenancy:
//   * HamptonsPrivateFenceDoor.IsLocked() — scans registrations for RentedByPlayer/
//     BuildingOwnedByPlayer with unlocksHamptonsPrivateFence + a matching privateFenceIndex;
//   * CityHamptonsHouseController.RefreshBlockerCollider() — a PHYSICAL barrier over the plot,
//     enabled whenever the registration is not RentedByPlayer (or is on sale / service-blocked).
// Under the mod's ownership model only the RENTER's machine reads RentedByPlayer=true, so a
// partner invited into a friend's Hamptons home met a locked gate and an invisible wall
// (sweep-3 mis#3/#4). Ruling: a RESIDENCE GRANT for a building behind the fence opens both —
// exactly the population CanEnterGranted already answers for every other shared-home surface.
// Any session player without the grant stays locked out, matching native "private" semantics.
//
// The fence lock is re-evaluated LIVE on every trigger enter, so it needs no refresh hook. The
// plot blocker is CACHED collider state — GrantSync.SetEnterableBuildings calls
// RefreshAllBlockers() on any change so an arriving/removed grant repaints within the same
// second (ruling 32: live parity of shared surfaces; the sweep is event-driven and rare).
using HarmonyLib;
using Helpers;      // RealEstateHelper.IsOnSale extension
using UnityEngine;

namespace BigAmbitionsMP
{
    internal static class HamptonsAccess
    {
        /// <summary>True when the local player holds a residence grant for a building that unlocks
        /// this fence index. Mirrors the native scan in HamptonsPrivateFenceDoor.IsLocked with the
        /// tenancy term replaced by the grant set.</summary>
        internal static bool GrantOpensFence(int fenceIndex)
        {
            try
            {
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return false;
                foreach (var reg in regs)
                {
                    if (reg == null) continue;
                    var b = reg.BuildingCached;
                    if (b == null || !b.unlocksHamptonsPrivateFence || b.privateFenceIndex != fenceIndex) continue;
                    if (GrantSync.CanEnterGranted(GameStateReader.AddressKey(reg))) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Grant set changed → re-evaluate every Hamptons plot barrier. Event-driven and
        /// rare (a grant push), so the scene walk is acceptable here.</summary>
        internal static void RefreshAllBlockers()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var all = UnityEngine.Object.FindObjectsOfType<CityHamptonsHouseController>(true);
                if (all == null || all.Length == 0) return;
                foreach (var c in all)
                    try { c?.RefreshBlockerCollider(); } catch { }
                Plugin.Logger.LogInfo($"[Hamptons] grant change → {all.Length} plot blocker(s) re-evaluated.");
            }
            catch { }
        }
    }

    /// <summary>Fence gate: a residence grant behind this fence unlocks it for the local player.</summary>
    [HarmonyPatch(typeof(HamptonsPrivateFenceDoor), "IsLocked")]
    public static class Patch_HamptonsFence_GrantUnlocks
    {
        static void Postfix(HamptonsPrivateFenceDoor __instance, ref bool __result)
        {
            try
            {
                if (!__result) return;                                        // natively unlocked — done
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return; // single player — native
                if (HamptonsAccess.GrantOpensFence(__instance.privateFenceIndex)) __result = false;
            }
            catch { }
        }
    }

    /// <summary>Plot barrier: a granted guest gets the same open plot the renter has — the native
    /// formula with the tenancy term satisfied (on-sale and service-blocked still close it).</summary>
    [HarmonyPatch(typeof(CityHamptonsHouseController), nameof(CityHamptonsHouseController.RefreshBlockerCollider))]
    public static class Patch_HamptonsBlocker_GrantOpens
    {
        static void Postfix(CityHamptonsHouseController __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var reg = __instance.buildingRegistration;
                var col = __instance.blockerCollider;
                if (reg == null || col == null || !col.enabled) return;       // open already / nothing to do
                if (reg.RentedByPlayer) return;                               // native path owns this case
                if (!GrantSync.CanEnterGranted(GameStateReader.AddressKey(reg))) return;
                bool inside = false;
                try { inside = BuildingManager.IsInsideBuilding && __instance.building == InstanceBehavior<BuildingManager>.Instance.building; } catch { }
                bool stillClosed = reg.IsOnSale()
                    || (BuildingManager.IsBuildingBlockedByAnyService(__instance.building.Address) && !inside);
                if (!stillClosed)
                {
                    col.enabled = false;
                    Plugin.Logger.LogInfo($"[Hamptons] plot barrier opened for granted guest at '{GameStateReader.AddressKey(reg)}'.");
                }
            }
            catch { }
        }
    }
}
