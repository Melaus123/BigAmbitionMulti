using System;
using HarmonyLib;
using BigAmbitions.Items;   // CargoInstance, ItemInstance, ItemType, ICargoHolder
using Buildings;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Business-helper interaction routing (round-32, Phase 2 of business permissions). A granted HELPER's
    /// stocking actions in another player's business must change the OWNER's authoritative building data,
    /// never the local replica. Where an action moves cargo between two owner-authoritative containers
    /// (stock displays, producers, storage), we forward the OPERATION and the owner's machine runs the
    /// game's own logic on it. See docs/PERMISSIONS-SYSTEM.md (Business Permissions).
    /// </summary>
    internal static class BusinessHelperRoute
    {
        /// <summary>The local player stands in a business they hold a HELPER grant for (never true for
        /// the owner, never true in residences).</summary>
        internal static bool HelperHere(out string addr)
        {
            addr = "";
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return false;
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null) return false;
                addr = GameStateReader.AddressKey(reg);
                return GrantSync.IsHelperBusiness(addr);
            }
            catch { return false; }
        }
    }

    /// <summary>The stock DROPDOWN's visibility refresh gates on IsPlayerOwnedBusiness inline — the same
    /// scoped flip the overlay wraps use lets a helper see it. Patches the BASE implementation only:
    /// SignController overrides this method separately (its dropdown stays owner-only).</summary>
    [HarmonyPatch(typeof(ItemController), nameof(ItemController.UpdateSelectedStockOverlay))]
    public static class Patch_ItemController_StockOverlay_Helper
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    /// <summary>A helper picking a stock type routes the change to the owner instead of mutating the
    /// replica (native OnStockOptionSelected shuffles cargo between the display, storage shelves, and
    /// parked vehicles — ALL owner-authoritative). Targets the private string overload so both the
    /// dropdown's int path and any direct calls are covered. Type-guarded to stock DISPLAYS: signs are
    /// not PointOfSale/ShowcaseShelf (their own override + owner re-check handles them), and producers
    /// route through the Producer patch below.</summary>
    [HarmonyPatch(typeof(ItemController), "OnStockOptionSelected", typeof(string), typeof(UI.Elements.Dropdown))]
    public static class Patch_ItemController_StockSelect_HelperRoute
    {
        static bool Prefix(ItemController __instance, string stockItemName)
        {
            try
            {
                if (!BusinessHelperRoute.HelperHere(out var addr)) return true;
                var ii = __instance?.ItemInstance;
                if (ii == null || ii.ItemCached == null) return true;
                if ((ii.ItemCached.type & (ItemType.PointOfSale | ItemType.ShowcaseShelf)) == 0) return true;
                BuildingStorageSync.RequestSetStock(addr, ii.id?.ToString() ?? "", stockItemName ?? "");
                Plugin.Logger.LogInfo($"[Business] helper stock-select '{stockItemName}' on {ii.itemName} @'{addr}' → routed to owner.");
                return false;   // the owner's interior push re-renders the shelf; nothing to change locally
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] stock-select route: {ex.Message}"); return true; }
        }
    }

    /// <summary>Round-34: owner-management WARNING ICONS ("out of paper bags", low stock…) leaked to
    /// helpers — the visibility flip lets UpdateSelectedStockOverlay run its UpdateWarningIcon call in
    /// owner mode (user 2026-07-04: guest told the register is out of paper bags). Warning icons are the
    /// owner's management UI; suppress them entirely for a helper in someone else's business.</summary>
    [HarmonyPatch(typeof(Player.HUD.ItemWarningIcons.ItemWarningIconManager),
                  nameof(Player.HUD.ItemWarningIcons.ItemWarningIconManager.UpdateWarningIcon), typeof(ItemController))]
    public static class Patch_WarningIcons_HelperSuppress
    {
        static bool Prefix()
        {
            try { return !BusinessHelperRoute.HelperHere(out _); }
            catch { return true; }
        }
    }

    /// <summary>A helper REFILLING a producer (pouring held/vehicle ingredients into it). The native
    /// owner-branch never runs for a helper (RentedByPlayer is unflipped at Interact time), so this
    /// prefix IMPLEMENTS the refill as routed ops: optionally set the ingredient name (empty machine),
    /// then put amounts CLAMPED to the machine's remaining capacity — clamping keeps the owner-side
    /// merge all-or-nothing, and the "producer" Ctx consume reduces exactly what was accepted. Falls
    /// through to the native (customer) flow whenever the refill shape doesn't apply.</summary>
    [HarmonyPatch(typeof(Producer), nameof(Producer.Interact))]
    public static class Patch_Producer_Interact_HelperRefill
    {
        static bool Prefix(Producer __instance, ref bool __result)
        {
            try
            {
                if (!BusinessHelperRoute.HelperHere(out var addr)) return true;
                // Visitor-shopping producers (purchaser-enabled) keep their native customer flow.
                try { var ps = __instance.playerItemPurchaserSettings; if (ps != null && ps.enabled) return true; } catch { }
                var ii = __instance?.ItemInstance;
                if (ii?.cargoInstances == null || ii.cargoInstances.Count != 1 || ii.ItemCached == null) return true;

                ICargoHolder? holder = !PlayerHelper.IsHoldingItem
                    ? VehicleHelper.GetCurrentVehicle()
                    : (ICargoHolder?)PlayerHelper.ItemInstanceInHands;
                var src = holder?.GetCargoInstances();
                if (src == null || src.Count == 0) return true;   // nothing to pour in → native flow

                var stock = ii.cargoInstances[0];
                string stockName = stock.itemName ?? "";
                string id = ii.id?.ToString() ?? "";

                if (string.IsNullOrEmpty(stockName))
                {
                    // Empty machine: pick the first held ingredient this producer can showcase — the
                    // native flow's selection rule — and ask the owner to load that name first.
                    CargoInstance? fit = null;
                    var can = __instance.Item?.itemsThatCanShowcase;
                    if (can != null)
                        foreach (var c in src)
                            if (c != null && !c.IsSealed && c.amount > 0)
                                foreach (var y in can)
                                    if (y == c.itemName) { fit = c; break; }
                    if (fit == null) { PassengerHud.Toast("Nothing fitting to load this machine with."); __result = true; return false; }
                    stockName = fit.itemName;
                    BuildingStorageSync.RequestSetStock(addr, id, stockName, "producerset");
                }

                int capacity = 0;
                try
                {
                    foreach (var c in src)
                        if (c != null && c.itemName == stockName) { capacity = c.GetMaxStockCapacity(ii); break; }
                }
                catch { }
                int space = capacity - stock.amount;
                if (capacity > 0 && space <= 0) { PassengerHud.Toast("It's already fully loaded."); __result = true; return false; }

                int routed = 0;
                foreach (var c in new System.Collections.Generic.List<CargoInstance>(src))
                {
                    if (c == null || c.IsSealed || c.amount <= 0 || c.itemName != stockName) continue;
                    int amt = capacity > 0 ? Math.Min(c.amount, space - routed) : c.amount;
                    if (amt <= 0) break;
                    BuildingStorageSync.RequestPut(addr, id, c.itemName, amt, c.paid, c.pricePerUnit, "producer");
                    routed += amt;
                }
                if (routed == 0 && string.IsNullOrEmpty(stock.itemName))
                { /* name-set only — the owner loads the name; nothing matching to pour */ }
                else if (routed == 0) return true;   // held nothing matching → native (customer) flow

                Plugin.Logger.LogInfo($"[Business] helper producer refill {routed}×{stockName} @'{addr}' → routed to owner.");
                __result = true;
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] producer refill route: {ex.Message}"); return true; }
        }
    }
}
