using System;
using System.Collections.Generic;
using HarmonyLib;
using BigAmbitions.Items;                 // ItemInstance
using Buildings.Indoors.InteriorDesign;   // InteriorDesignerController, InteriorDesignerHelper

namespace BigAmbitionsMP
{
    /// <summary>H-SELL-3 r4 (user decision 2026-09-05, option B settled at CLOSE; restructured after reviews F-2026-09-05-AR/-AU).
    /// The interior designer in a FRIEND's building: nothing about sales reaches the owner while the designer is open; at close the
    /// still-sold things are sold through the owner-confirmed storage routes ("itemsell" for whole items, "stacksell"/"bundlesell"
    /// for box or vehicle contents), and their ids are kept out of the close-time edit delta. A sale's proceeds are neutralised in the
    /// designer's running balance the moment it is booked (and restored on undo), so purchases are gated on real money and the
    /// native close settlement pays purchases only. A box with pending contents sales is locked against package actions until close. Source of truth = LEDGERS of live sale actions (Do adds, Undo removes) —
    /// never InteriorDesignerController.SoldItemIndexes, the designer's destroy-at-close list (purchase-undo, duplicate-undo,
    /// packing and emptied boxes write it too).</summary>
    internal static class DesignerSaleRoute
    {
        /// <summary>Address key of the foreign granted building the designer is open in ("" = not routing).</summary>
        internal static string RoutingAddr = "";
        /// <summary>Item ids present in the registration when the designer opened here — what the OWNER knows. Anything else was
        /// created this session and cannot be sold through the owner (refused at the sale click).</summary>
        private static readonly HashSet<string> _idsAtOpen = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Live WHOLE-ITEM sales: the SellRevertibleAction instance → (ids of every instance it sold, the price it booked).</summary>
        private static readonly Dictionary<object, (List<string> ids, float price)> _ledger = new Dictionary<object, (List<string>, float)>();
        /// <summary>One package-overlay contents sale (a whole grouped cargo row from one holder), captured BEFORE native discards it.</summary>
        internal sealed class CargoSale
        {
            public bool IsVehicle; public string HolderId = "";   // building: the box's ItemInstance id; vehicle: the REAL id (no BAMP_ prefix)
            public string ItemName = ""; public int Amount; public bool Paid; public float PricePerUnit; public int Count; public bool Bundle;
            public float Price;   // what the designer booked (changeBalance) — neutralised at click, restored on undo
            public bool EmptiedBox;   // r6: the sale itself emptied (hid + removed) the box — only such a box's removal is shielded from the delta
        }
        /// <summary>Live CONTENTS sales: the PackageSellRevertibleAction instance → the captured row.</summary>
        private static readonly Dictionary<object, CargoSale> _cargoLedger = new Dictionary<object, CargoSale>();
        /// <summary>Whole-item ids captured at HandleOnClose; consulted by the delta builder until InteriorSync.ForwardGuestDesignerClose
        /// clears it.</summary>
        internal static readonly HashSet<string> SoldAtClose = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Holder (box) ids of contents sales settled at close — the OWNER decides what becomes of those boxes (it keeps an empty
        /// box after paying, exactly like the shelf-screen route); no client-side removal of them may reach it (review r4 MAJOR-1).</summary>
        internal static readonly HashSet<string> BoxesAtClose = new HashSet<string>(StringComparer.Ordinal);
        private static int _refusedLogged;

        internal static bool RoutingHere()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return false;
                if (InteriorDesignerHelper.BlueprintCreatorMode) return false;
                if (!HousingFurniture.LocalGuestHere() && !HousingFurniture.LocalHelperHere()) return false;
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                return reg != null && !MergerFlip.TrulyMine(reg);
            }
            catch { return false; }
        }

        internal static void ResetSession()
        {
            _idsAtOpen.Clear(); _ledger.Clear(); _cargoLedger.Clear(); SoldAtClose.Clear(); BoxesAtClose.Clear(); RoutingAddr = "";
        }

        internal static void OpenSession(string addr)
        {
            ResetSession();
            RoutingAddr = addr;
            try
            {
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg?.itemInstances != null)
                    foreach (var k in reg.itemInstances.Keys) if (!string.IsNullOrEmpty(k)) _idsAtOpen.Add(k);
            }
            catch { }
        }

        internal static bool KnownToOwner(string id) => !string.IsNullOrEmpty(id) && _idsAtOpen.Contains(id);

        /// <summary>Instance ids behind a sale action's controller indices (null entries skipped).</summary>
        internal static List<string> IdsOf(int[] indices)
        {
            var ids = new List<string>();
            if (indices == null) return ids;
            foreach (int idx in indices)
            {
                if (idx < 0 || idx >= InteriorDesignerController.ItemControllersCache.Count) continue;
                var ctrl = InteriorDesignerController.ItemControllersCache[idx];
                var inst = ctrl != null ? ctrl.ItemInstance : null;
                if (inst != null && !string.IsNullOrEmpty(inst.id)) ids.Add(inst.id);
            }
            return ids;
        }

        internal static bool AllKnownToOwner(List<string> ids)
        {
            foreach (var id in ids) if (!_idsAtOpen.Contains(id)) return false;
            return ids.Count > 0;
        }

        internal static void LedgerAdd(object action, List<string> ids, float price) { _ledger[action] = (ids, price); }
        internal static bool LedgerRemove(object action) => _ledger.Remove(action);
        internal static bool LedgerContains(string id)
        {
            foreach (var kv in _ledger) if (kv.Value.ids.Contains(id)) return true;
            return false;
        }
        internal static void CargoLedgerAdd(object action, CargoSale sale) { _cargoLedger[action] = sale; }
        internal static bool CargoLedgerRemove(object action) => _cargoLedger.Remove(action);
        /// <summary>Does this building item have ANY pending contents sale (emptied or not)? Such a box is locked against package actions
        /// that would move cargo out of it or pack it until the designer closes (review r6: an early settlement is clawed back by the
        /// designer's close settlement; a removal without settlement would destroy the unsold rows on the owner).</summary>
        internal static bool HasPendingSales(string id)
        {
            foreach (var kv in _cargoLedger) if (!kv.Value.IsVehicle && kv.Value.HolderId == id) return true;
            return false;
        }

        private static int _lockedLogged;
        /// <summary>The one place the pending-box refusal is shown and logged (toast wording user-approved 2026-09-05).</summary>
        internal static void RefusePendingBox(string id, string what)
        {
            try { PassengerHud.Toast("Close the designer first: this box has a pending sale."); } catch { }   // user-approved wording 2026-09-05
            try { if (_lockedLogged++ < 20) Plugin.Logger.LogInfo($"[DesignerSale] {what} refused — box '{id}' has pending contents sales; close the designer to settle them (H-SELL-3 r7/r8)."); } catch { }
        }

        /// <summary>r8 (review r7 MINOR-1): refuse a package action at its ENTRY point, before the game hides the original item or creates the
        /// new box/item controller (PackageToolSetup.PackItem, PackageOverlay.OnPlaceClick/OnPackClick). True = refused (caller skips the
        /// original). Same check as GuardPendingBox; the Do() prefixes remain the money backstop behind this.</summary>
        internal static bool RefuseIfPendingHolder(int holderIndex, bool isVehicle, string what)
        {
            try
            {
                if (RoutingAddr.Length == 0 || isVehicle) return false;
                string id = IdAtIndex(holderIndex);
                if (id.Length == 0 || !HasPendingSales(id)) return false;
                RefusePendingBox(id, what);
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] pending-box entry lock: {ex.Message}"); return false; }
        }
        /// <summary>The pending-box lock: refuse a package action whose SOURCE holder is a building box with pending contents sales.</summary>
        internal static bool GuardPendingBox(BigAmbitions.InteriorDesigner.Tools.PackageRevertibleAction action, ref bool result, string what)
        {
            try
            {
                if (RoutingAddr.Length == 0 || action == null || action.isVehicle) return true;
                string id = IdAtIndex(action.cargoHolderIndex);
                if (id.Length == 0 || !HasPendingSales(id)) return true;
                result = false;   // review r7 MINOR-2: the verdict is set before anything that could throw
                RefusePendingBox(id, what);
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] pending-box lock: {ex.Message}"); return true; }
        }
        /// <summary>Is this building item a box the SALE ITSELF emptied and still pending? Only such a box's removal is kept out of the
        /// delta (review r5 MAJOR-1: shielding every pending box swallowed genuine pack/empty removals → duplication on the owner).</summary>
        internal static bool CargoHolderPending(string id)
        {
            foreach (var kv in _cargoLedger) if (!kv.Value.IsVehicle && kv.Value.EmptiedBox && kv.Value.HolderId == id) return true;
            return false;
        }

        /// <summary>Item id at a designer controller index, or "" (used by the pending-box lock).</summary>
        internal static string IdAtIndex(int itemIndex)
        {
            try
            {
                if (itemIndex < 0 || itemIndex >= InteriorDesignerController.ItemControllersCache.Count) return "";
                var ctrl = InteriorDesignerController.ItemControllersCache[itemIndex];
                var inst = ctrl != null ? ctrl.ItemInstance : null;
                return inst != null && !string.IsNullOrEmpty(inst.id) ? inst.id : "";
            }
            catch { return ""; }
        }

        /// <summary>The designer booked changeBalance(+price) for a sale whose proceeds are not the helper's until the owner confirms —
        /// take them straight back out so purchases are gated on real money (review r3 MAJOR-1). Restore reverses it after an undo.</summary>
        internal static void Neutralise(float price)
        {
            try { BigAmbitions.InteriorDesigner.IRevertibleAction.changeBalance?.Invoke(0f - price); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] proceeds neutralise: {ex.Message}"); }
        }
        internal static void Restore(float price)
        {
            try { BigAmbitions.InteriorDesigner.IRevertibleAction.changeBalance?.Invoke(price); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] proceeds restore: {ex.Message}"); }
        }

        /// <summary>Should the edit-delta builder skip a REMOVE op for this id at this address? TRUE whole-item sales and the boxes a routed contents sale itself EMPTIED (live ledgers
        /// while the designer is open here, or the close-time capture) — the owner removes those itself on the itemsell verdict. Every
        /// other designer removal (packing, purchase-undo, an emptied box) is conveyed by the delta as natively intended.</summary>
        internal static bool SkipDeltaRemove(string addressKey, string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || addressKey != RoutingAddr) return false;
                if (SoldAtClose.Contains(id) || BoxesAtClose.Contains(id)) return true;
                return UI.InteriorDesigner.InteriorDesignerUI.IsOpen && (LedgerContains(id) || CargoHolderPending(id));
            }
            catch { return false; }
        }

        internal static void Refused(string why)
        {
            try
            {
                PassengerHud.Toast("That can't be sold right now.");   // user-approved wording 2026-09-05
                if (_refusedLogged++ < 20) Plugin.Logger.LogInfo($"[DesignerSale] sale refused in '{RoutingAddr}': {why} (H-SELL-3).");
            }
            catch { }
        }

        /// <summary>Close-time settlement from the ledgers: whole-item ids to sell (top-level only — a sold instance whose parent is also
        /// sold rides its parent's tree on the owner) and the captured contents sales. Clears both ledgers so a second HandleOnClose in
        /// the same session sends nothing (review r3 MINOR-4). Returns the total the designer had booked, for the log only.</summary>
        internal static float Settle(out List<string> topLevel, out int instanceCount, out List<CargoSale> cargo)
        {
            topLevel = new List<string>(); instanceCount = 0; cargo = new List<CargoSale>(_cargoLedger.Values);
            float booked = 0f;
            var all = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in _ledger) { booked += kv.Value.price; foreach (var id in kv.Value.ids) all.Add(id); }
            foreach (var c in cargo) booked += c.Price;
            instanceCount = all.Count;
            SoldAtClose.Clear();
            foreach (var id in all) SoldAtClose.Add(id);
            foreach (var id in all)
            {
                string parent = "";
                try
                {
                    // the instance is already out of the registration (sold at click) — read the parent link from the controller cache
                    foreach (var ctrl in InteriorDesignerController.ItemControllersCache)
                        if (ctrl != null && ctrl.ItemInstance != null && ctrl.ItemInstance.id == id) { parent = ctrl.ItemInstance.parentId ?? ""; break; }
                }
                catch { }
                if (parent.Length > 0 && all.Contains(parent)) continue;
                topLevel.Add(id);
            }
            BoxesAtClose.Clear();
            foreach (var c in cargo) if (!c.IsVehicle && c.EmptiedBox && c.HolderId.Length > 0) BoxesAtClose.Add(c.HolderId);   // review r4 MAJOR-1 + r5 MAJOR-1: only a box the sale itself emptied
            _ledger.Clear(); _cargoLedger.Clear();
            return booked;
        }
    }

    /// <summary>Designer OPEN/CLOSE bookkeeping. Toggle(true) with the designer actually open here (CanOpen can skip the body — check
    /// IsOpen) starts a routing session in a foreign granted building; OnDestroy (leaving the building mid-design) drops it.</summary>
    [HarmonyPatch(typeof(UI.InteriorDesigner.InteriorDesignerUI), "Toggle")]
    public static class Patch_InteriorDesignerUI_Toggle_DesignerSaleRoute
    {
        static void Postfix(bool show)
        {
            try
            {
                if (!show) return;
                if (!UI.InteriorDesigner.InteriorDesignerUI.IsOpen) { DesignerSaleRoute.ResetSession(); return; }   // review r1 MINOR-6: CanOpen refused
                if (!DesignerSaleRoute.RoutingHere()) { DesignerSaleRoute.ResetSession(); return; }
                DesignerSaleRoute.OpenSession(HousingDesign.CurrentBuildingAddr());
                Plugin.Logger.LogInfo($"[DesignerSale] designer opened in a friend's building '{DesignerSaleRoute.RoutingAddr}' — sales settle at close through the owner (H-SELL-3).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] toggle: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(UI.InteriorDesigner.InteriorDesignerUI), "OnDestroy")]
    public static class Patch_InteriorDesignerUI_OnDestroy_DesignerSaleRoute
    {
        static void Postfix()
        {
            try
            {
                if (DesignerSaleRoute.RoutingAddr.Length > 0)
                    Plugin.Logger.LogInfo($"[DesignerSale] designer torn down mid-session in '{DesignerSaleRoute.RoutingAddr}' — route state dropped (no sale settles; the owner's next interior sync heals the replica).");
                DesignerSaleRoute.ResetSession();
            }
            catch { }
        }
    }

    /// <summary>WHOLE-ITEM SALE. SellRevertibleAction.Do books changeBalance(_price), hides the items and adds their indices to the destroy
    /// list. Prefix: in a routing session, refuse (return false — the native empty-indices shape) when the indices do not all resolve
    /// or any instance was not in the registration at designer open (the owner never saw it). Postfix: ledger + neutralise the
    /// proceeds. Undo removes and restores.</summary>
    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.SellRevertibleAction), "Do")]
    public static class Patch_SellRevertibleAction_Do_Ledger
    {
        static bool Prefix(BigAmbitions.InteriorDesigner.Tools.SellRevertibleAction __instance, ref bool __result)
        {
            try
            {
                if (DesignerSaleRoute.RoutingAddr.Length == 0) return true;
                var ids = DesignerSaleRoute.IdsOf(__instance._itemIndices);
                if ((__instance._itemIndices?.Length ?? 0) == 0) return true;   // native handles the truly empty case; indices that resolve to nothing are refused below (review r4 MINOR-2)
                if (ids.Count != (__instance._itemIndices?.Length ?? 0))
                {
                    DesignerSaleRoute.Refused("a sold index resolved to no item (review r3 MINOR-5) — refusing rather than booking money the route could not recover");
                    __result = false;
                    return false;
                }
                if (!DesignerSaleRoute.AllKnownToOwner(ids))
                {
                    DesignerSaleRoute.Refused("an item created this session (the owner never saw it) — undo the purchase/unpacking instead");
                    __result = false;
                    return false;
                }
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] sale prefix: {ex.Message}"); return true; }
        }
        static void Postfix(BigAmbitions.InteriorDesigner.Tools.SellRevertibleAction __instance, bool __result)
        {
            try
            {
                if (!__result || DesignerSaleRoute.RoutingAddr.Length == 0) return;
                var ids = DesignerSaleRoute.IdsOf(__instance._itemIndices);
                if (ids.Count == 0) return;
                DesignerSaleRoute.LedgerAdd(__instance, ids, __instance._price);
                DesignerSaleRoute.Neutralise(__instance._price);   // review r3 MAJOR-1
                Plugin.Logger.LogInfo($"[DesignerSale] item sale booked: {ids.Count} instance(s), ${__instance._price:F2} neutralised in the running balance — settles through the owner at close.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] sale postfix: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.SellRevertibleAction), "Undo")]
    public static class Patch_SellRevertibleAction_Undo_Ledger
    {
        static void Postfix(BigAmbitions.InteriorDesigner.Tools.SellRevertibleAction __instance, bool __result)
        {
            try
            {
                if (!__result || DesignerSaleRoute.RoutingAddr.Length == 0) return;
                if (!DesignerSaleRoute.LedgerRemove(__instance)) return;   // not one of ours — nothing was neutralised
                DesignerSaleRoute.Restore(__instance._price);
                Plugin.Logger.LogInfo("[DesignerSale] item sale un-done — removed from the ledger, running balance restored.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] undo postfix: {ex.Message}"); }
        }
    }

    /// <summary>CONTENTS SALE (review r3 MAJOR-2; user decision 2026-09-05: route it). PackageSellRevertibleAction.Do discards the whole
    /// grouped cargo row from the holder (PackageToolSetup.DiscardCargo), hides the box if empty, and books changeBalance(+price). In a
    /// routing session the row is captured BEFORE the discard (holder + name/amount/paid/price/count/bundle), ledgered and neutralised;
    /// at close it becomes the shelf screen's owner-confirmed cargo sale (stacksell / bundlesell). No owner route exists for a
    /// display-stock carrier (native zeroes the stock amount), a box created this session, or an unresolvable row → refused. The
    /// helper's OWN vehicle stays native.</summary>
    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.PackageSellRevertibleAction), "Do")]
    public static class Patch_PackageSellRevertibleAction_Do_Ledger
    {
        static bool Prefix(BigAmbitions.InteriorDesigner.Tools.PackageSellRevertibleAction __instance, ref bool __result, out DesignerSaleRoute.CargoSale? __state)
        {
            __state = null;
            try
            {
                if (DesignerSaleRoute.RoutingAddr.Length == 0) return true;
                int holderIdx = __instance.cargoHolderIndex; int cargoIdx = __instance.cargoIndex; bool isVehicle = __instance.isVehicle;
                ICargoHolder? holder = null; string holderId = "";
                if (!isVehicle)
                {
                    if (holderIdx < 0 || holderIdx >= InteriorDesignerController.ItemControllersCache.Count) return Refuse(ref __result, "holder index out of range");
                    var ctrl = InteriorDesignerController.ItemControllersCache[holderIdx];
                    var inst = ctrl != null ? ctrl.ItemInstance : null;
                    if (inst == null || string.IsNullOrEmpty(inst.id)) return Refuse(ref __result, "holder resolved to no item");
                    bool stockCarrier = false; try { stockCarrier = ctrl.Item != null && ctrl.Item.IsStockCarrier(); } catch { }
                    if (stockCarrier) return Refuse(ref __result, "display stock — no owner route for a stock sale");
                    if (!DesignerSaleRoute.KnownToOwner(inst.id)) return Refuse(ref __result, "a box created this session (the owner never saw it)");
                    holder = inst; holderId = inst.id;
                }
                else
                {
                    if (holderIdx < 0 || holderIdx >= InteriorDesignerController.VehicleControllersCache.Count) return Refuse(ref __result, "vehicle index out of range");
                    var vc = InteriorDesignerController.VehicleControllersCache[holderIdx];
                    var vinst = vc != null ? vc.vehicleInstance : null;
                    if (vinst == null || string.IsNullOrEmpty(vinst.id)) return Refuse(ref __result, "vehicle resolved to no instance");
                    if (!vinst.id.StartsWith("BAMP_", StringComparison.Ordinal)) return true;   // the helper's OWN vehicle — its cargo is theirs, native
                    holder = vinst; holderId = vinst.id.Substring(5);   // REAL id for the vehicle route
                }
                var groups = CargoItem.ConvertCargoInstancesToCargoItems(holder.GetCargoInstances());
                if (groups == null || cargoIdx < 0 || cargoIdx >= groups.Count) return Refuse(ref __result, "cargo row out of range");
                var g = groups[cargoIdx];
                var first = g?.cargoInstances != null && g.cargoInstances.Count > 0 ? g.cargoInstances[0] : null;
                if (first == null || string.IsNullOrEmpty(first.itemName)) return Refuse(ref __result, "cargo row resolved to nothing");
                __state = new DesignerSaleRoute.CargoSale
                {
                    IsVehicle = isVehicle, HolderId = holderId, ItemName = first.itemName, Amount = first.amount, Paid = first.paid,
                    PricePerUnit = first.pricePerUnit, Count = g.cargoInstances.Count,
                    Bundle = first.nestedCargoInstances != null && first.nestedCargoInstances.Count > 0, Price = __instance._price,
                };
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] contents sale prefix: {ex.Message}"); __state = null; return true; }
        }
        private static bool Refuse(ref bool result, string why) { DesignerSaleRoute.Refused(why); result = false; return false; }
        static void Postfix(BigAmbitions.InteriorDesigner.Tools.PackageSellRevertibleAction __instance, bool __result, DesignerSaleRoute.CargoSale? __state)
        {
            try
            {
                if (!__result || __state == null || DesignerSaleRoute.RoutingAddr.Length == 0) return;
                // r6/r7: did the sale empty (hide + remove) the box? Then only its removal is shielded from the delta; a box still present
                // stays a normal item that the pending-box lock keeps from being packed, moved out of, or unpacked until close.
                try
                {
                    var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    __state.EmptiedBox = !__state.IsVehicle && reg?.itemInstances != null && !reg.itemInstances.ContainsKey(__state.HolderId);
                }
                catch { __state.EmptiedBox = false; }
                DesignerSaleRoute.CargoLedgerAdd(__instance, __state);
                DesignerSaleRoute.Neutralise(__instance._price);
                Plugin.Logger.LogInfo($"[DesignerSale] contents sale booked: {__state.Count}×({__state.ItemName}×{__state.Amount}) from {(__state.IsVehicle ? "vehicle" : "box")} '{__state.HolderId}', ${__instance._price:F2} neutralised — settles through the owner at close.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] contents sale postfix: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.PackageSellRevertibleAction), "Undo")]
    public static class Patch_PackageSellRevertibleAction_Undo_Ledger
    {
        static void Postfix(BigAmbitions.InteriorDesigner.Tools.PackageSellRevertibleAction __instance, bool __result)
        {
            try
            {
                if (!__result || DesignerSaleRoute.RoutingAddr.Length == 0) return;
                if (!DesignerSaleRoute.CargoLedgerRemove(__instance)) return;   // not one of ours (own vehicle / not routing) — nothing was neutralised
                DesignerSaleRoute.Restore(__instance._price);
                Plugin.Logger.LogInfo("[DesignerSale] contents sale un-done — removed from the ledger, running balance restored.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] contents undo postfix: {ex.Message}"); }
        }
    }

    /// <summary>r7 PENDING-BOX LOCK (review r6). While a box has pending contents sales, the package actions that would take cargo out of
    /// it or pack it are refused until the designer closes: PackageMoveRevertibleAction (move a row to another holder),
    /// PackagePlaceRevertibleAction (unpack a row as an item), PackagePackCargoRevertibleAction (pack a row into a new box),
    /// PackagePackItemRevertibleAction (pack the box itself). All carry the SOURCE holder as PackageRevertibleAction.cargoHolderIndex /
    /// isVehicle. Selling further rows stays allowed. Refusal = the native false return; the r8 entry locks below fire first on the three paths with pre-work.</summary>
    /// <summary>r8 ENTRY LOCKS (review r7 MINOR-1): the same refusal one step earlier, before the game's pre-work, so a refused click leaves
    /// nothing behind. PackItem(itemIndex): the item being packed IS the holder. OnPlaceClick/OnPackClick: the overlay's current holder
    /// (_currentClickedIndex/_currentIsVehicle). The overlay's drag-move (ExecuteRevertibleAction) and the hand-tool move construct their
    /// action with no pre-work / inside a composite that rolls back, so the Do() lock alone is clean for them.</summary>
    [HarmonyPatch(typeof(Buildings.Indoors.InteriorDesign.PackageToolSetup), "PackItem")]
    public static class Patch_PackageToolSetup_PackItem_EntryLock
    {
        static bool Prefix(int itemIndex) => !DesignerSaleRoute.RefuseIfPendingHolder(itemIndex, false, "packing a box (entry)");
    }

    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.PackageOverlay), "OnPlaceClick")]
    public static class Patch_PackageOverlay_OnPlaceClick_EntryLock
    {
        static bool Prefix(BigAmbitions.InteriorDesigner.PackageOverlay __instance)
            => !DesignerSaleRoute.RefuseIfPendingHolder(__instance._currentClickedIndex, __instance._currentIsVehicle, "unpacking from a box (entry)");
    }

    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.PackageOverlay), "OnPackClick")]
    public static class Patch_PackageOverlay_OnPackClick_EntryLock
    {
        static bool Prefix(BigAmbitions.InteriorDesigner.PackageOverlay __instance)
            => !DesignerSaleRoute.RefuseIfPendingHolder(__instance._currentClickedIndex, __instance._currentIsVehicle, "packing cargo out of a box (entry)");
    }

    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.PackageMoveRevertibleAction), "Do")]
    public static class Patch_PackageMove_Do_PendingBoxLock
    {
        static bool Prefix(BigAmbitions.InteriorDesigner.Tools.PackageRevertibleAction __instance, ref bool __result)
            => DesignerSaleRoute.GuardPendingBox(__instance, ref __result, "moving cargo out of a box");
    }

    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.PackagePlaceRevertibleAction), "Do")]
    public static class Patch_PackagePlace_Do_PendingBoxLock
    {
        static bool Prefix(BigAmbitions.InteriorDesigner.Tools.PackageRevertibleAction __instance, ref bool __result)
            => DesignerSaleRoute.GuardPendingBox(__instance, ref __result, "unpacking from a box");
    }

    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.PackagePackCargoRevertibleAction), "Do")]
    public static class Patch_PackagePackCargo_Do_PendingBoxLock
    {
        static bool Prefix(BigAmbitions.InteriorDesigner.Tools.PackageRevertibleAction __instance, ref bool __result)
            => DesignerSaleRoute.GuardPendingBox(__instance, ref __result, "packing cargo out of a box");
    }

    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.PackagePackItemRevertibleAction), "Do")]
    public static class Patch_PackagePackItem_Do_PendingBoxLock
    {
        static bool Prefix(BigAmbitions.InteriorDesigner.Tools.PackageRevertibleAction __instance, ref bool __result)
            => DesignerSaleRoute.GuardPendingBox(__instance, ref __result, "packing a box");
    }

    /// <summary>Review r4 MAJOR-1: a contents sale that empties its box runs PackageToolSetup.HideBoxIfEmpty INSIDE the click, whose
    /// RemoveItemInstanceFromBuilding (PackageToolSetup.cs:420) the chokepoint postfix would forward to the owner at once — destroying the
    /// box and its goods before the close-time sale arrives. Hold the forward flag for the whole native Do (first-setter clears); the
    /// delta builder keeps the box's removal out afterwards (CargoHolderPending / BoxesAtClose).</summary>
    [HarmonyPatch(typeof(BigAmbitions.InteriorDesigner.Tools.PackageSellRevertibleAction), "Do")]
    public static class Patch_PackageSellRevertibleAction_Do_SuppressForward
    {
        static void Prefix(out bool __state)
        {
            __state = false;
            try
            {
                if (DesignerSaleRoute.RoutingAddr.Length == 0 || StorageSync.SuppressGuestForward) return;
                StorageSync.SuppressGuestForward = true; __state = true;
            }
            catch { }
        }
        static void Finalizer(bool __state) { if (__state) StorageSync.SuppressGuestForward = false; }
    }

    /// <summary>The whole-item sale's registration removal (SellToolSetup.AddToSoldList, SellToolSetup.cs:74-82) must not be forwarded
    /// to the owner at click — the owner removes on the close-time verdict. First-setter clears.</summary>
    [HarmonyPatch(typeof(Buildings.Indoors.InteriorDesign.SellToolSetup), "AddToSoldList")]
    public static class Patch_SellToolSetup_AddToSoldList_SuppressForward
    {
        static void Prefix(out bool __state)
        {
            __state = false;
            try
            {
                if (DesignerSaleRoute.RoutingAddr.Length == 0 || StorageSync.SuppressGuestForward) return;
                StorageSync.SuppressGuestForward = true; __state = true;
            }
            catch { }
        }
        static void Finalizer(bool __state) { if (__state) StorageSync.SuppressGuestForward = false; }
    }

    /// <summary>CLOSE-TIME SETTLEMENT (Prefix — runs BEFORE the native HandleOnClose, which pays PlayerBalanceAfterChanges − Money in one
    /// ChangeMoneySafe and destroys the destroy-list controllers). From the LEDGERS only: the sale proceeds were already neutralised at
    /// click, so the native settlement pays purchases only; the whole-item ids are captured for the close delta's remove skip; one
    /// owner-confirmed request per top-level whole item (itemsell) and per contents sale (stacksell / bundlesell, building or vehicle).
    /// Nothing else in the destroy list is touched or priced. The requests leave before the close delta (Postfix) on the same link.</summary>
    [HarmonyPatch(typeof(InteriorDesignerController), nameof(InteriorDesignerController.HandleOnClose))]
    public static class Patch_InteriorDesignerController_HandleOnClose_SettleThroughOwner
    {
        static void Prefix()
        {
            try
            {
                string addr = DesignerSaleRoute.RoutingAddr;
                if (addr.Length == 0) return;
                if (InteriorDesignerHelper.BlueprintCreatorMode) return;
                float booked = DesignerSaleRoute.Settle(out var topLevel, out int instances, out var cargo);
                if (instances == 0 && cargo.Count == 0) return;

                int sent = 0;
                foreach (var id in topLevel)
                {
                    StorageSync.SendOp(new StorageOpPayload
                    {
                        Container = StorageSync.ContainerBuilding, AddressKey = addr, ItemId = id,
                        PlayerId = MPConfig.PlayerId, Op = StorageSync.OpTake, Ctx = "itemsell",
                        ItemName = "", Amount = 1, Paid = true, PricePerUnit = 0f, Count = 1,
                    });
                    sent++;
                }
                int cargoSent = 0;
                foreach (var c in cargo)
                {
                    if (c.IsVehicle)
                    {
                        if (c.Bundle) VehicleStorageSync.RequestBundleOp(c.HolderId, c.ItemName, c.Amount, c.Paid, c.PricePerUnit, sell: true);
                        else          VehicleStorageSync.RequestStackOp(c.HolderId, c.ItemName, c.Amount, c.Paid, c.PricePerUnit, c.Count, sell: true);
                    }
                    else
                    {
                        if (c.Bundle) BuildingStorageSync.RequestBundleOp(addr, c.HolderId, c.ItemName, c.Amount, c.Paid, c.PricePerUnit, sell: true);
                        else          BuildingStorageSync.RequestStackOp(addr, c.HolderId, c.ItemName, c.Amount, c.Paid, c.PricePerUnit, c.Count, sell: true);
                    }
                    cargoSent++;
                }
                Plugin.Logger.LogInfo($"[DesignerSale] designer close in '{addr}': {instances} sold instance(s) → {sent} item request(s); {cargoSent} contents sale request(s); ${booked:F2} had been neutralised at click (H-SELL-3 r4).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[DesignerSale] close settlement: {ex.Message}"); }
        }

        /// <summary>Review r4 MINOR-3: the route state must not outlive the session when the close forward is grant-gated off. Priority.Last
        /// so it runs AFTER the guest-forward Postfix, which needs SoldAtClose/BoxesAtClose while it builds the close delta.</summary>
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.Last)]
        static void Postfix()
        {
            try { DesignerSaleRoute.ResetSession(); } catch { }
        }
    }
}
