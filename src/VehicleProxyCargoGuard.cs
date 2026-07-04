using System;
using HarmonyLib;
using Controllers;    // PlayerItemPurchaser (borrowed-car shopping scope)
using Helpers;        // PlayerHelper, VehicleHelper

namespace BigAmbitionsMP
{
    /// <summary>Borrowed-vehicle cargo is OWNER-AUTHORITATIVE. A granted car spawns on the borrower's machine as a
    /// REGISTERED proxy (vehicleInstance.id = "BAMP_"+realId) whose cargo is a READ-ONLY replica, overwritten on
    /// every fleet broadcast. So ANY local gameplay mutation of a proxy's cargo is a bug: a local ADD gets
    /// overwritten on the next sync (item DELETED — the 2026-06-30 "put deletes" bug), a local REMOVE re-appears on
    /// the next sync (item DUPLICATED — the earlier "take dupes" bug).
    ///
    /// The earlier fix patched ONE UI entry point (VehicleController.ManageStorage → our panel). That was a
    /// half-measure: the cargo has MANY entry points (held-item deposit, enter-with-item, hand-truck transfer, …),
    /// and each unpatched one re-introduces the same class of bug. The robust fix is at the DATA layer: the game
    /// funnels every add through VehicleInstance.TryToAddToCargo and every remove through RemoveFromCargo /
    /// ReduceFromCargo. Guarding those three covers 100% of gameplay paths in one place. We then ROUTE the one path
    /// players actually use to put items in (the on-foot held-item deposit) to the owner so it still works.
    ///
    /// The mod's own display sync (RefreshGhostCargo) mutates cargoInstances RAW (Clear + Add), NOT through these
    /// methods, so it is unaffected. The owner's real vehicle (id without "BAMP_") is never a proxy, so owner-side
    /// apply (VehicleStorageSync.OwnerApply) passes through untouched.</summary>
    internal static class ProxyCargo
    {
        internal static bool IsProxy(VehicleInstance inst) => inst?.id != null && inst.id.StartsWith("BAMP_");
    }

    // ADD chokepoint — block every local add to a proxy. Returning __result=false makes the caller keep the item in
    // its source (hands / cart / shelf), so nothing is deleted; the deposit path below routes it to the owner.
    [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.TryToAddToCargo))]
    public static class Patch_VehicleInstance_TryAdd_ProxyGuard
    {
        static bool Prefix(VehicleInstance __instance, ref bool __result)
        {
            if (!ProxyCargo.IsProxy(__instance)) return true;
            __result = false;
            return false;
        }
    }

    // REMOVE chokepoints — block every local remove/reduce on a proxy (the owner's real cargo is the single source
    // of truth; the take path routes through the owner and the display re-syncs from the broadcast).
    [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.RemoveFromCargo))]
    public static class Patch_VehicleInstance_Remove_ProxyGuard
    {
        static bool Prefix(VehicleInstance __instance) => !ProxyCargo.IsProxy(__instance);
    }

    [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.ReduceFromCargo))]
    public static class Patch_VehicleInstance_Reduce_ProxyGuard
    {
        static bool Prefix(VehicleInstance __instance) => !ProxyCargo.IsProxy(__instance);
    }

    // MERGE chokepoint — the 4th mutation path, missed by the original three (round-12 #1c). The game's
    // hand-truck→car transfer (ICargoHolder.TryToMergeAndMoveCargoBetweenHolders) calls MergeIntoCargo
    // FIRST: on a proxy trunk holding a MATCHING stack it would merge into the replica, the source stack
    // hits 0 and is removed from the borrower's own hand-truck — then the next fleet re-sync overwrites
    // the replica and the items are GONE. Only an empty/no-match trunk made this survivable before
    // (merge no-ops, the guarded TryToAddToCargo then blocks). Block the merge outright; the routed
    // deposit (RequestDeposit) is the supported path.
    [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.MergeIntoCargo))]
    public static class Patch_VehicleInstance_Merge_ProxyGuard
    {
        static bool Prefix(VehicleInstance __instance) => !ProxyCargo.IsProxy(__instance);
    }

    // ── BORROWED-CAR SHOPPING (round-13 D) ────────────────────────────────────────────────────────────────
    // Drive-in shopping targets the CURRENT vehicle: PlayerItemPurchaser.GrabItem puts picked (unpaid) items
    // into GetCurrentVehicle() (:161), and PurchaseUI.SetCargoInstancesToPaid consolidates just-paid stacks
    // into the trunk — including a DIRECT `cargoInstances.Remove` (:398) that bypasses every chokepoint. On a
    // borrowed car both are wrong: the trunk is a read-only replica, so picks dead-end on the guard with a
    // misleading "vehicle full", and the checkout consolidation would drain a HELD box into the replica (pay
    // → goods evaporate on the next re-sync). FIX: within these two methods ONLY, a borrower whose current
    // vehicle is a proxy is treated as ON FOOT — picks land in a held box natively (correct charging + shelf
    // stock), the trunk consolidation is skipped entirely (side door closed), and the box is then deposited
    // into the trunk through the owner-confirmed deposit channel like any other deposit. (user-approved D)
    internal static class BorrowedShopScope
    {
        internal static int Depth;
    }

    [HarmonyPatch(typeof(PlayerHelper), nameof(PlayerHelper.IsUsingVehicle), MethodType.Getter)]
    public static class Patch_PlayerHelper_IsUsingVehicle_BorrowedShop
    {
        static void Postfix(ref bool __result)
        {
            try
            {
                if (!__result || BorrowedShopScope.Depth == 0) return;   // scoped: only inside the two wrapped methods
                if (ProxyCargo.IsProxy(VehicleHelper.GetCurrentVehicle())) __result = false;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerItemPurchaser), nameof(PlayerItemPurchaser.GrabItem))]
    public static class Patch_PlayerItemPurchaser_GrabItem_BorrowedShop
    {
        static void Prefix()    { BorrowedShopScope.Depth++; }
        static void Finalizer() { if (BorrowedShopScope.Depth > 0) BorrowedShopScope.Depth--; }
    }

    [HarmonyPatch(typeof(UI.Purchase.PurchaseUI), nameof(UI.Purchase.PurchaseUI.SetCargoInstancesToPaid))]
    public static class Patch_PurchaseUI_SetPaid_BorrowedShop
    {
        static void Prefix()    { BorrowedShopScope.Depth++; }
        static void Finalizer() { if (BorrowedShopScope.Depth > 0) BorrowedShopScope.Depth--; }
    }

    // HAND-TRUCK deposit — the vehicle menu's "add items to storage" button (round-14 #1: the deposit fires
    // from THIS button, exactly like the owner — never from the initial click on the car). The native flow
    // walks via SetGoal (arrival callback unreliable for ghosts) then merges truck→car locally (blocked by
    // the guards on a proxy) — redirect to our proximity-polled walk-then-deposit instead.
    [HarmonyPatch(typeof(VehicleController), "MoveAndAddHandTruckItemsToStorage")]
    public static class Patch_VehicleController_MoveAndAddHandTruck_Borrowed
    {
        static bool Prefix(VehicleController __instance)
        {
            try
            {
                var inst = __instance?.vehicleInstance;
                if (!ProxyCargo.IsProxy(inst)) return true;            // my own car → native
                string realId = inst.id.Substring(5);
                string owner = VehicleManager.OwnerIdFor(realId);
                if (string.IsNullOrEmpty(owner)) return true;
                PassengerRide.WalkAndDeposit(realId, owner);           // walk to the loading spot, deposit on arrival
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] handtruck-deposit redirect: {ex.Message}"); return true; }
        }
    }

    // Backstop: any native path that reaches the final truck→car transfer directly.
    [HarmonyPatch(typeof(VehicleController), "AddHandTruckItemsToStorage")]
    public static class Patch_VehicleController_AddHandTruckItems_Borrowed
    {
        static bool Prefix(VehicleController __instance)
        {
            try
            {
                var inst = __instance?.vehicleInstance;
                if (!ProxyCargo.IsProxy(inst)) return true;
                string realId = inst.id.Substring(5);
                string owner = VehicleManager.OwnerIdFor(realId);
                if (string.IsNullOrEmpty(owner)) return true;
                VehicleStorageSync.RequestDeposit(realId, owner);      // hand-truck-aware source resolution
                VehicleManager.ClearGhostHighlight(realId);
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] handtruck-transfer redirect: {ex.Message}"); return true; }
        }
    }

    // PUT route — the on-foot "deposit held item" path (VehicleController.AddHeldItemToStorage) adds straight to the
    // proxy's local cargo; with the chokepoint guard above that add is now blocked, so without this the deposit
    // would silently do nothing. Route it to the owner (RequestDeposit is box-aware: it unwraps a carried item).
    [HarmonyPatch(typeof(VehicleController), "AddHeldItemToStorage")]
    public static class Patch_VehicleController_AddHeldItem_Borrowed
    {
        static bool Prefix(VehicleController __instance)
        {
            try
            {
                var inst = __instance?.vehicleInstance;
                if (!ProxyCargo.IsProxy(inst)) return true;            // my own car → native deposit
                string realId = inst.id.Substring(5);
                string owner = VehicleManager.OwnerIdFor(realId);
                if (string.IsNullOrEmpty(owner)) return true;
                VehicleStorageSync.RequestDeposit(realId, owner);      // owner-authoritative, box-aware
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] held-deposit redirect: {ex.Message}"); return true; }
        }
    }
}
