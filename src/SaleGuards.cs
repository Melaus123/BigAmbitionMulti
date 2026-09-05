using System;
using HarmonyLib;
using Helpers;          // VehicleHelper
using UI.ItemPanel;     // ItemPanelUI

namespace BigAmbitionsMP
{
    /// <summary>H-SELL-1 (verbal report 2026-09-05; user-approved: hide + silent no-op). The game's vehicle popup offers SELL to anyone
    /// seated in a drivable priced vehicle; a friend's car you hold a key to IS such a vehicle here (drivable granted proxy). Selling it
    /// paid the seller and destroyed only the local copy, which the owner's next fleet packet respawned — infinite money. Both halves of
    /// the native path are guarded: the button (UpdateButtonsVisibility) and the action (ClickSell — also reached by the Sell key).</summary>
    [HarmonyPatch(typeof(UI.ItemPanel.ItemPanelUI), "UpdateButtonsVisibility")]
    public static class Patch_ItemPanelUI_UpdateButtonsVisibility_ForeignVehicle
    {
        static void Postfix(UI.ItemPanel.ItemPanelUI __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;
                if (MPClient.OfflineFork && !VehicleHelper.IsInsideVehicle())
                {
                    // Review r1 MAJOR-2 (class MP SHIELD LAPSES IN THE OFFLINE FORK): no host to route an item sale to — the popup's
                    // Sell is hidden in any building that is not truly ours (the item guard below refuses the action as well).
                    var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    if (reg != null && !MergerFlip.TrulyMine(reg) && __instance?.sellButton != null && __instance.sellButton.gameObject.activeSelf)
                        __instance.sellButton.gameObject.SetActive(false);
                    return;
                }
                if (!VehicleHelper.IsInsideVehicle()) return;
                if (!VehicleManager.IsForeignProxy(VehicleHelper.GetCurrentVehicle())) return;
                if (__instance?.sellButton != null && __instance.sellButton.gameObject.activeSelf) __instance.sellButton.gameObject.SetActive(false);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(UI.ItemPanel.ItemPanelUI), nameof(UI.ItemPanel.ItemPanelUI.ClickSell))]
    public static class Patch_ItemPanelUI_ClickSell_ForeignVehicle
    {
        private static int _logged;
        static bool Prefix()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;
                var sv = InstanceBehavior<GameManager>.Instance?.selectedVehicle;
                if (sv == null || !VehicleManager.IsForeignProxy(sv.vehicleInstance)) return true;
                if (_logged++ < 20) Plugin.Logger.LogInfo($"[SaleGuard] vehicle sale refused — '{sv.vehicleInstance?.id}' is another player's vehicle (H-SELL-1).");
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>H-SELL-2 (verbal report 2026-09-05; user ruling: selling other players' stock stays ALLOWED, it must never mint money).
    /// The item popup's SELL on a building item natively credits the local wallet and discards the local copy in one breath; in a
    /// friend's building that copy is a replica — money was created here while the owner's stock was only asynchronously informed.
    /// Now, in a building where the local player is a granted guest/helper, the sale is REQUESTED from the owner ("itemsell"): the
    /// owner prices and removes the item tree on its authoritative copy and the seller is credited only on that verdict
    /// (StorageSync.OnTakeResult). The native confirm dialog already ran (its price is the replica's estimate; the credit uses the
    /// owner's). Placement-mode sales (an item in the seller's own hands) and every own-building sale stay native.</summary>
    [HarmonyPatch(typeof(UI.ItemPanel.ItemPanelUI), "OnConfirmSellItem")]
    public static class Patch_ItemPanelUI_OnConfirmSellItem_OwnerConfirmedRoute
    {
        static bool Prefix(UI.ItemPanel.ItemPanelUI __instance, float price)
        {
            try
            {
                if (MPClient.OfflineFork)
                {
                    // Review r1 MAJOR-2: in the offline fork there is no owner to ask — refuse the sale in any building that is not
                    // truly ours (the native path would credit this wallet against a replica). Own buildings stay native.
                    var freg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    if (freg != null && !MergerFlip.TrulyMine(freg))
                    {
                        Plugin.Logger.LogInfo($"[SaleGuard] item sale refused — offline fork, '{freg.BusinessName}' is not ours (H-SELL-2; class MP SHIELD LAPSES IN THE OFFLINE FORK).");
                        return false;
                    }
                    return true;
                }
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                if (BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode) return true;   // an item in the seller's own hands — native
                var ii = __instance?.selectedItemInstance;
                var held = HelperStorageGuard.HelperBuildingHolder(ii, out var addr);
                if (held == null) return true;   // own building / not a guest or helper here / hand item — native
                string itemId = held.id?.ToString() ?? "";
                if (string.IsNullOrEmpty(itemId)) return true;
                StorageSync.SendOp(new StorageOpPayload
                {
                    Container = StorageSync.ContainerBuilding, AddressKey = addr, ItemId = itemId,
                    PlayerId = MPConfig.PlayerId, Op = StorageSync.OpTake, Ctx = "itemsell",
                    ItemName = held.itemName ?? "", Amount = 1, Paid = true, PricePerUnit = price, Count = 1,
                });
                Plugin.Logger.LogInfo($"[SaleGuard] item sale routed to the owner: '{held.itemName}' ({itemId}) @'{addr}', replica estimate ${price:F2} (H-SELL-2).");
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SaleGuard] item sale route: {ex.Message}"); return true; }
        }
    }
}
