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
    /// (The round-35→37 shelf/register probe fleet was retired 2026-07-05 after the ActiveVehicleId
    /// null-contract root fix — see the context log rounds 37a-37m for the investigation record.)
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

        internal enum RefillResult { NotApplicable, Handled }

        /// <summary>Shared helper-refill core (round-36d): pour the helper's held/vehicle cargo into a
        /// single-slot stock station (producer OR cash register — same data shape: ItemInstance with one
        /// stock CargoInstance) as routed owner-side ops. Optionally sets the stock name first (empty
        /// station, "producerset" — bare name-set, refused if occupied), then puts amounts CLAMPED to the
        /// station's remaining capacity ("producer" Ctx: owner-side single-slot merge guard + exact-amount
        /// consume on our side). NotApplicable = shape didn't match; caller decides the fallback.</summary>
        internal static RefillResult RouteStationRefill(ItemController? station, string addr)
        {
            try
            {
                var ii = station?.ItemInstance;
                if (ii?.cargoInstances == null || ii.cargoInstances.Count != 1 || ii.ItemCached == null)
                    return RefillResult.NotApplicable;

                ICargoHolder? holder = !PlayerHelper.IsHoldingItem
                    ? VehicleHelper.GetCurrentVehicle()
                    : (ICargoHolder?)PlayerHelper.ItemInstanceInHands;
                var src = holder?.GetCargoInstances();
                if (src == null || src.Count == 0) return RefillResult.NotApplicable;

                var stock = ii.cargoInstances[0];
                string stockName = stock.itemName ?? "";
                string id = ii.id?.ToString() ?? "";

                if (string.IsNullOrEmpty(stockName))
                {
                    // Empty station: pick the first held item this station can showcase — the native
                    // flow's selection rule — and ask the owner to load that name first.
                    CargoInstance? fit = null;
                    var can = station!.Item?.itemsThatCanShowcase;
                    if (can != null)
                        foreach (var c in src)
                            if (c != null && !c.IsSealed && c.amount > 0)
                                foreach (var y in can)
                                    if (y == c.itemName) { fit = c; break; }
                    if (fit == null) { PassengerHud.Toast("Nothing fitting to load this with."); return RefillResult.Handled; }
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
                if (capacity > 0 && space <= 0) { PassengerHud.Toast("It's already fully loaded."); return RefillResult.Handled; }

                int routed = 0;
                foreach (var c in new System.Collections.Generic.List<CargoInstance>(src))
                {
                    if (c == null || c.IsSealed || c.amount <= 0 || c.itemName != stockName) continue;
                    int amt = capacity > 0 ? Math.Min(c.amount, space - routed) : c.amount;
                    if (amt <= 0) break;
                    BuildingStorageSync.RequestPut(addr, id, c.itemName, amt, c.paid, c.pricePerUnit, "producer");
                    routed += amt;
                }
                if (routed == 0 && !string.IsNullOrEmpty(stock.itemName))
                    return RefillResult.NotApplicable;   // held nothing matching an already-set station

                Plugin.Logger.LogInfo($"[Business] helper station refill {routed}×{stockName} @'{addr}' ({station!.GetType().Name}) → routed to owner.");
                return RefillResult.Handled;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] station refill route: {ex.Message}"); return RefillResult.NotApplicable; }
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

    /// <summary>Round-38e — the register panel's REMOVE CONTENT button (DropdownOverlay.OnRemoveContentClick
    /// → ItemController.RemoveStockInContent) ran NATIVELY for helpers: it emptied the REPLICA slot and
    /// minted the contents box locally, while the owner's real station kept its stock — the field-proven
    /// duplication (2026-07-07; the cargo authority shield stops the laundering, this routes the take
    /// itself). Helper click → same native walk → RequestTake ctx "stationtake"; the owner empties the REAL
    /// slot (native RemoveStockInContent semantics) and the box arrives via the take delivery. Empty hands
    /// required up front — the deferred delivery makes the native merge-into-holder branch racy.</summary>
    [HarmonyPatch(typeof(ItemController), nameof(ItemController.RemoveStockInContent))]
    public static class Patch_ItemController_RemoveContent_HelperRoute
    {
        static bool Prefix(ItemController __instance)
        {
            try
            {
                if (!BusinessHelperRoute.HelperHere(out var addr)) return true;
                var ii = __instance?.ItemInstance;
                if (ii?.cargoInstances == null || ii.cargoInstances.Count != 1 || ii.ItemCached == null) return true;
                if ((ii.ItemCached.type & (ItemType.PointOfSale | ItemType.ShowcaseShelf)) == 0) return true;
                var stock = ii.cargoInstances[0];
                string name = stock.itemName ?? "";
                int shown = stock.amount;
                if (string.IsNullOrEmpty(name) || shown <= 0) return false;   // replica shows nothing — never run native (replica mutation)
                if (PlayerHelper.IsHoldingItem || PlayerHelper.IsUsingVehicle)
                { PassengerHud.Toast("Empty your hands to take the contents."); return false; }
                string a = addr, id = ii.id?.ToString() ?? "";
                InstanceBehavior<GameManager>.Instance.playerController.SetGoal(__instance, delegate
                {
                    try
                    {
                        BuildingStorageSync.RequestTake(a, id, name, shown, paid: false, price: 0f, "stationtake");
                        Plugin.Logger.LogInfo($"[Business] helper station take {shown}×{name} @'{a}' → routed to owner (owner truth decides the amount).");
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] station take: {ex.Message}"); }
                });
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] remove-content route: {ex.Message}"); return true; }
        }
    }

    /// <summary>Round-38 — hover-tooltip parity: the stock overlay ("Contents: …" + "0/1000") gates on
    /// IsPlayerOwnedBusiness (StockOverlay.ShouldShow), so a helper hovering the owner's register saw only
    /// the bare-name tooltip while the owner saw contents+amount — the two players were reading DIFFERENT
    /// facts off the same machine. Read-only overlay (GetStockInstance), so the same scoped visibility flip
    /// the dropdown wrap uses is safe here.</summary>
    [HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.StockOverlay),
                  nameof(Player.HUD.ItemInfoOverlays.StockOverlay.ShouldShow))]
    public static class Patch_StockOverlay_HelperParity
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
    /// owner mode. Warning icons are the owner's management UI; suppress them entirely for a helper in
    /// someone else's business.</summary>
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
    /// prefix IMPLEMENTS the refill as routed ops. Falls through to the native (customer) flow whenever
    /// the refill shape doesn't apply.</summary>
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
                if (BusinessHelperRoute.RouteStationRefill(__instance, addr) == BusinessHelperRoute.RefillResult.Handled)
                { __result = true; return false; }
                return true;   // shape didn't match → native (customer) flow
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] producer refill route: {ex.Message}"); return true; }
        }
    }

    /// <summary>Round-37b — the register deposit's REAL click path: no CTA behavior reliably claims the
    /// register, so the owner's deposit rides the null-CTA fallback (overlay refuses → WalkOverAndInteract
    /// → Producer.Interact owner-merge); the helper never gets there because our visibility flip makes the
    /// register's overlay SHOW, eating the click. Fix at the decision point: after UpdateCta, if nothing
    /// claimed the click and the helper's cargo fits the register, BIND the click action directly
    /// (CtaManager.ctaKey/ctaAction are public statics) — the click then runs the routed refill and the
    /// overlay never opens, mirroring the owner's experience.</summary>
    [HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.CtaManager),
                  nameof(Player.HUD.ItemInfoOverlays.CtaManager.UpdateCta))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]   // run after the visibility wrap's own postfix ordering
    public static class Patch_RegisterCta_HelperBind
    {
        static void Postfix(EntityController entityController)
        {
            try
            {
                if (!string.IsNullOrEmpty(Player.HUD.ItemInfoOverlays.CtaManager.ctaKey)) return;   // something claimed it — keep
                if (Player.HUD.ItemInfoOverlays.CtaManager.ctaAction != null) return;
                var reg = entityController as Controllers.CashRegisterController;
                if (reg == null) return;
                if (!BusinessHelperRoute.HelperHere(out var addr)) return;
                if (!PlayerHelper.HasPaidForAllItems()) return;   // shopper flow keeps priority
                bool can = false;
                try
                {
                    if (PlayerHelper.IsUsingVehicle && VehicleHelper.GetCurrentVehicle() != null)
                        can = reg.CanAddAnyToInventory(VehicleHelper.GetCurrentVehicle().cargoInstances);
                    else if (PlayerHelper.IsHoldingItem && PlayerHelper.ItemInstanceInHands != null)
                        can = reg.CanAddAnyToInventory(PlayerHelper.ItemInstanceInHands.cargoInstances);
                }
                catch { }
                if (!can) return;
                var ec = entityController; string a = addr;
                Player.HUD.ItemInfoOverlays.CtaManager.ctaKey = "click_to_add_inventory";
                Player.HUD.ItemInfoOverlays.CtaManager.ctaAction = delegate
                {
                    try
                    {
                        InstanceBehavior<GameManager>.Instance.playerController.SetGoal(ec, delegate
                        {
                            BusinessHelperRoute.RouteStationRefill(ec as ItemController, a);
                        });
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] helper register refill: {ex.Message}"); }
                };
            }
            catch { }
        }
    }

    /// <summary>Round-37b — the register-bag DUPLICATION class: the visibility flip exposes the
    /// manage-cargo UI on owner-authoritative building items, and its take/sell/discard paths mutate the
    /// LOCAL replica only (a take duplicated 1000 bags). Same class round-13 blocked for storage shelves —
    /// block the whole manage UI for helpers on building-owned holders until a routed panel exists.</summary>
    [HarmonyPatch(typeof(UI.MergeCargo.ManageCargoUi), nameof(UI.MergeCargo.ManageCargoUi.Show))]
    public static class Patch_ManageCargo_HelperBlock
    {
        static bool Prefix(ICargoHolder cargoHolder)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                var ii = cargoHolder as ItemInstance;
                if (ii == null) return true;   // own vehicles/boxes etc. — not building-owned
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                bool inBuilding = false;
                try { inBuilding = reg?.itemInstances != null && ii.id != null && reg.itemInstances.ContainsKey(ii.id.ToString()); } catch { }
                if (!inBuilding) return true;
                if (BusinessHelperRoute.HelperHere(out _))
                {
                    PassengerHud.Toast("Only the owner can manage this storage (for now).");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>Round-36d/e — when CashRegisterCtaBehavior.GetCta DOES run for a helper: an empty native
    /// pick gets our routed deposit action when the cargo fits; a native "click_to_add_inventory" pick
    /// (possible under the visibility flip's owner branch) keeps its label but has its action SWAPPED for
    /// the routed one — the native action's walk+Interact dies on the unflipped Interact (falls into the
    /// customer purchase flow). Other native picks (pay/work/order) keep priority.</summary>
    [HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.CashRegisterCtaBehavior),
                  nameof(Player.HUD.ItemInfoOverlays.CashRegisterCtaBehavior.GetCta))]
    public static class Patch_RegisterCta_HelperRefill
    {
        static void Postfix(EntityController entityController, ref (string, Action) __result)
        {
            try
            {
                if (!BusinessHelperRoute.HelperHere(out var addr)) return;
                var reg = entityController as Controllers.CashRegisterController;
                if (reg == null) return;
                bool nativeAddInventory = __result.Item1 == "click_to_add_inventory";
                if (!string.IsNullOrEmpty(__result.Item1) && !nativeAddInventory) return;
                if (!PlayerHelper.HasPaidForAllItems()) return;             // shopper flow first (native)
                bool can = false;
                try
                {
                    if (PlayerHelper.IsUsingVehicle && VehicleHelper.GetCurrentVehicle() != null)
                        can = reg.CanAddAnyToInventory(VehicleHelper.GetCurrentVehicle().cargoInstances);
                    else if (PlayerHelper.IsHoldingItem && PlayerHelper.ItemInstanceInHands != null)
                        can = reg.CanAddAnyToInventory(PlayerHelper.ItemInstanceInHands.cargoInstances);
                }
                catch { }
                if (!can) return;
                var ec = entityController;
                string a = addr;
                __result = ("click_to_add_inventory", delegate
                {
                    try
                    {
                        // Same walk-to-the-register the owner's CTA does, then the routed refill.
                        InstanceBehavior<GameManager>.Instance.playerController.SetGoal(ec, delegate
                        {
                            BusinessHelperRoute.RouteStationRefill(ec as ItemController, a);
                        });
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] helper register refill: {ex.Message}"); }
                });
            }
            catch { }
        }
    }
}
