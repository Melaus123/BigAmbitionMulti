using System;
using HarmonyLib;
using BigAmbitions.Items;   // CargoInstance, ItemInstance, ItemType, ICargoHolder
using Buildings;
using Extensions;           // ToShortCurrencyFormat (round-47b sell confirms)
using Helpers;
using Localizor;            // .Localize (native confirm-dialog bodies)

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

    /// <summary>Round-38f — Phase 3 slice 1: let a helper MAN the register (the panel's WORK button was a
    /// silent no-op: ButtonOverlay renders under the visibility flip so the button SHOWED, but the
    /// click-time CanWork re-check ran unflipped — ShouldOpenWorkUI needs IsPlayerOwnedBusiness or a hired
    /// job — and Work() returned false doing nothing). Flipping CanWork itself covers both callers
    /// uniformly. What the helper gets is the OWNER's work mode (WorkActivity with a null job): no salary
    /// (IsWorkingInPlayerBuilding skips the payout — "helping is donating" for free), native energy drain,
    /// session bounded by the store's opening hours. Serving is currently COSMETIC on the helper's machine —
    /// every economic step in FullServiceEmployee (paperbag entry :111, Pay-with-revenue :135, order
    /// recording :154) gates on the unflipped IsPlayerOwnedBusiness, so no money moves and nothing records.
    /// Order forwarding to the owner (the real Phase 3) comes after this slice answers whether customers
    /// even queue on the helper's machine.</summary>
    [HarmonyPatch(typeof(EmployeeStationController), nameof(EmployeeStationController.CanWork))]
    public static class Patch_StationCanWork_Helper
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    /// <summary>Round-39f — Phase 3 slice-2 step-2: ORDER FORWARDING. Order.Pay is the single point
    /// every NPC checkout passes through (full-service serve :136, self-service, kiosk). On a helper's
    /// machine the pay moves no money and records nothing (all owner-gated) — so when an NPC pays HERE
    /// in a granted business, forward the paid order to the owner, who claims the schedule entry
    /// (single-writer dedup), deducts real stock + a bag, and records it. Only orders born from the
    /// owner's synced schedule forward (EntryIdOf null = a local stray — skip); each forwards once.</summary>
    [HarmonyPatch(typeof(Order), nameof(Order.Pay))]
    public static class Patch_Order_Pay_HelperForward
    {
        private static readonly System.Collections.Generic.HashSet<string> _sent = new();

        static void Postfix(Order __instance, bool isPlayer, bool __result)
        {
            try
            {
                if (!__result || isPlayer || __instance == null) return;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (!BusinessHelperRoute.HelperHere(out var addr)) return;
                string? entryId = CustomerEntrySync.EntryIdOf(__instance);
                if (string.IsNullOrEmpty(entryId) || _sent.Contains(entryId)) return;

                var payload = new HelperOrderPayload { AddressKey = addr, PlayerId = MPConfig.PlayerId, EntryId = entryId! };
                foreach (var e in __instance.entries)
                {
                    if (e == null || !e.paid || string.IsNullOrEmpty(e.itemName)) continue;
                    payload.Items.Add(new OrderEntryInfo { ItemName = e.itemName, Price = e.price, WholesalePrice = e.wholesalePrice });
                }
                if (payload.Items.Count == 0) return;   // empty basket — nothing real happened
                _sent.Add(entryId!);

                if (MPServer.IsRunning) MPServer.HandleHelperOrder(payload, MPConfig.PlayerId);
                else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.HelperOrderForward, MPConfig.PlayerId, payload));
                Plugin.Logger.LogInfo($"[Business] forwarded helper-served order {entryId} ({payload.Items.Count} item(s)) @'{addr}' → owner.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] order forward: {ex.Message}"); }
        }
    }

    /// <summary>Round-39e — complaint parity: a just-arrived customer's complaint bubbles
    /// (Customer.Init :204 → ComplainAboutUnfulfilledDemands) gate on IsPlayerOwnedBusiness, so the
    /// owner saw customers complain about shop shortcomings and a helper never did. The scoped flip
    /// makes Init's two ownership reads (:196 demand score, :204 complain) pass for helpers; the
    /// complaint data is real on this machine now that entries carry customerDemandTypes and the
    /// snapshot carries the fulfilled-demand set (round-39e sync). Cosmetic-only: the expression
    /// coroutine plays audio + an emoji bubble, no economy writes.</summary>
    [HarmonyPatch(typeof(Customer), nameof(Customer.Init))]
    public static class Patch_Customer_Init_HelperComplaints
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    // (register-npc investigation CLOSED 2026-07-07: the [StaffProbe] run showed exactly ONE
    // AssignEmployee — tpc='Player' isPlayer=True via WorkActivity.StartWorking — no rogue spawner.
    // The "NPC on top" was the native business-uniform overlay applied to the working player, same as
    // an owner working their own register; the look restores on stop. Probe retired.)

    /// <summary>Round-38f — when the helper STOPS working, WorkActivity.Finish's deferred branch (:185-191,
    /// runs one frame later so no scoped flip can cover it) sees a non-owner building and calls
    /// AssignAiToEmployeeStation — minting a phantom AI cashier on the helper's replica register. Skip it in
    /// helper businesses; its two native call sites are AI-shop setup (HelperHere false there) and this
    /// Finish branch, so nothing legitimate is lost.</summary>
    [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.AssignAiToEmployeeStation))]
    public static class Patch_AssignAi_HelperGuard
    {
        static bool Prefix()
        {
            try { return !BusinessHelperRoute.HelperHere(out _); }
            catch { return true; }
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

    /// <summary>Round-49 slice 5 — SIGN parity. SignController's IO methods gate on IsPlayerOwnedBusiness
    /// (decompile :66/:78): non-owners get redirected to the linked item's customer overlay instead of the
    /// sign's own owner overlay (whose dropdown the already-wrapped base UpdateSelectedStockOverlay shows).
    /// Read-only routing decisions — same scoped-flip family as the CTA/overlay wraps.</summary>
    [HarmonyPatch(typeof(Controllers.SignController), nameof(Controllers.SignController.OnIoEnter))]
    public static class Patch_SignController_IoEnter_Helper
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    [HarmonyPatch(typeof(Controllers.SignController), nameof(Controllers.SignController.OnIoLeftClick))]
    public static class Patch_SignController_IoLeftClick_Helper
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    /// <summary>Round-49 slice 5 — the sign's dropdown pick. SignController overrides the INT overload and
    /// sets ItemInstance.linkedItemName directly (self-gated on IsPlayerOwnedBusiness, :97) — it never
    /// reaches the base string overload our generic stock-select route patches. linkedItemName is a pure
    /// display link (no cargo moves), so the route is: set the replica for instant feedback, send
    /// "signset" to the owner (authoritative), whose interior push carries linkedItemName back to every
    /// client (it's in the snapshot hash + apply, GameStatePatcher :1936/:1968).</summary>
    [HarmonyPatch(typeof(Controllers.SignController), nameof(Controllers.SignController.OnStockOptionSelected),
                  typeof(int), typeof(UI.Elements.Dropdown))]
    public static class Patch_SignController_StockSelect_HelperRoute
    {
        static bool Prefix(Controllers.SignController __instance, int stockIndex)
        {
            try
            {
                if (!BusinessHelperRoute.HelperHere(out var addr)) return true;
                var ii = __instance?.ItemInstance;
                var st = __instance?.StockTypes;
                if (ii == null || st == null || stockIndex < 0 || stockIndex >= st.Count) return false;   // helper + bad pick: mirror the native no-op
                string newName = st[stockIndex] == "ba:itemname_undefined" ? string.Empty : st[stockIndex];
                ii.linkedItemName = newName;   // optimistic local — the owner's next interior push confirms it
                try { HarmonyLib.AccessTools.Method(typeof(Controllers.SignController), "SetInfo")?.Invoke(__instance, null); } catch { }
                BuildingStorageSync.RequestSetStock(addr, ii.id?.ToString() ?? "", newName, "signset");
                Plugin.Logger.LogInfo($"[Business] helper sign-select '{newName}' on {ii.itemName} @'{addr}' → routed to owner.");
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] sign-select route: {ex.Message}"); return true; }
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

    /// <summary>Round-47 (slice 2b) — helpers get the storage manage panel, GUARDED. History: round-37b
    /// blocked the whole panel because its take/sell/discard mutate the LOCAL replica (a take duplicated
    /// 1000 bags) and sell credits the LOCAL wallet. Now: the panel OPENS for helpers; per-row rewiring
    /// lives in Patch_CargoItemUi_HelperRoute (take → routed "boxtake"; sell/discard hidden); the
    /// sell-all button hides + its handler hard-blocks below.</summary>
    internal static class HelperStorageGuard
    {
        /// <summary>The building-owned ItemInstance behind a manage-panel holder when the local player is
        /// a business HELPER **or a housing GUEST** here (round-47c: the storage panel serves both grant
        /// kinds — the BStore relay/apply gates accept Housing OR Business) — null in every
        /// owner/non-building case (native behavior untouched).</summary>
        internal static ItemInstance? HelperBuildingHolder(ICargoHolder? holder, out string addr)
        {
            addr = "";
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return null;
                var ii = holder as ItemInstance;
                if (ii == null) return null;   // own vehicles/boxes etc. — not building-owned
                if (!HousingFurniture.LocalGuestHere() && !HousingFurniture.LocalHelperHere()) return null;
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null) return null;
                addr = GameStateReader.AddressKey(reg);
                bool inBuilding = false;
                try { inBuilding = reg.itemInstances != null && ii.id != null && reg.itemInstances.ContainsKey(ii.id.ToString()); } catch { }
                return inBuilding ? ii : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Round-47b (full parity, user: the grant is trust-scoped) — helper SELL ALL: same
    /// confirm-then-act shape as native, but every removal routes to the owner; the sale money credits
    /// the helper's wallet per group verdict (Transfers exist for gifting it back).</summary>
    [HarmonyPatch(typeof(UI.MergeCargo.ManageCargoUi), "OnSellAllClick")]
    public static class Patch_ManageCargo_HelperSellAll
    {
        static bool Prefix(UI.MergeCargo.ManageCargoUi __instance)
        {
            try
            {
                var ii = HelperStorageGuard.HelperBuildingHolder(__instance.currentCargoHolder, out var addr);
                if (ii == null) return true;
                // Group the holder's PAID non-sealed instances by identity (name+amount+paid) — the same
                // grouping the panel rows use.
                var groups = new System.Collections.Generic.Dictionary<string, (CargoInstance first, int count)>();
                float total = 0f;
                var src = __instance.currentCargoHolder.GetCargoInstances();
                if (src != null)
                    foreach (var ci in src)
                    {
                        if (ci == null || !ci.paid || ci.IsSealed) continue;
                        string k = $"{ci.itemName}|{ci.amount}";
                        groups[k] = groups.TryGetValue(k, out var g) ? (g.first, g.count + 1) : (ci, 1);
                        try { total += ci.GetSellingPrice(); } catch { }
                    }
                if (groups.Count == 0) return false;
                string itemId = ii.id?.ToString() ?? "";
                HudConfirm.Show(default, "manage_cargo_sell_all_confirm".Localize(new
                {
                    cargoName = __instance.currentCargoHolder.GetCargoName(),
                    totalPrice = total.ToShortCurrencyFormat(),
                }), delegate
                {
                    foreach (var kv in groups)
                        BuildingStorageSync.RequestStackOp(addr, itemId, kv.Value.first.itemName, kv.Value.first.amount,
                                                           paid: true, kv.Value.first.pricePerUnit, kv.Value.count, sell: true);
                    Plugin.Logger.LogInfo($"[Business] helper SELL ALL routed: {groups.Count} group(s) @'{addr}'.");
                });
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] sell-all route: {ex.Message}"); return true; }
        }
    }

    /// <summary>Round-47 — per-row rewiring for helpers on building storage: SELL/DISCARD route the
    /// removal to the owner (money credits the helper), the item click becomes a ROUTED box take — the
    /// owner removes the real sealed box (contents echoed over the wire) and it lands in the helper's
    /// hands, exactly like the owner's own ClickItem take. Empty hands required (deferred delivery makes
    /// holder-merge racy). Round-49 (helper parity): the action button's one building-holder action —
    /// PLACE furniture from a delivery spot — is re-enabled with the reduce routed ("placereduce"; the
    /// placement itself rides the native flow + the existing guest interior forward), and vehicle-type
    /// cargo (stored hand trucks / flatbeds) becomes a routed take that spawns the vehicle LOCALLY as the
    /// HELPER'S OWN ("vehicletake"; user ruling 2026-07-21: valueless + freely spawnable, so the taker
    /// keeps it — re-storing it into owner storage gifts it back).</summary>
    [HarmonyPatch(typeof(UI.PlayerHUD.CargoItemUi), nameof(UI.PlayerHUD.CargoItemUi.SetUp))]
    public static class Patch_CargoItemUi_HelperRoute
    {
        static void Postfix(UI.PlayerHUD.CargoItemUi __instance, BigAmbitions.Items.CargoItem cargoItem, ICargoHolder cargoHolder)
        {
            try
            {
                ICargoHolder? holder = cargoHolder;
                if (holder == null)
                    try { holder = InstanceBehavior<UI.UIs>.Instance?.playerHUD?.manageCargoUI?.currentCargoHolder; } catch { }
                var ii = HelperStorageGuard.HelperBuildingHolder(holder, out var addr);
                if (ii == null) return;
                if (cargoItem?.cargoInstances == null || cargoItem.cargoInstances.Count == 0) return;
                var first = cargoItem.cargoInstances[0];
                if (first == null) return;

                string name = first.itemName ?? "";
                int amount = first.amount; bool paid = first.paid; float price = first.pricePerUnit;
                int stackCount = cargoItem.cargoInstances.Count;
                bool isSealed = first.IsSealed;
                string itemId = ii.id?.ToString() ?? "";

                // Round-47b (full parity): SELL/DISCARD stay visible per the native rules (paid→sell,
                // unpaid→discard, sealed→neither) but the click routes the REMOVAL to the owner; on a
                // sell the money credits the helper's own wallet at verdict time.
                var sellBtn = Btn("sellButton");
                if (sellBtn != null && sellBtn.gameObject.activeSelf)
                {
                    sellBtn.onClick.RemoveAllListeners();
                    sellBtn.onClick.AddListener(delegate
                    {
                        try
                        {
                            float sellTotal = 0f;
                            try { sellTotal = first.GetSellingPrice() * stackCount; } catch { }
                            HudConfirm.Show(default, "itempanelui_hud_confirm_sellitem".Localize(new
                            {
                                type = name,
                                price = sellTotal.ToShortCurrencyFormat(),
                            }), delegate
                            {
                                BuildingStorageSync.RequestStackOp(addr, itemId, name, amount, paid, price, stackCount, sell: true);
                                Plugin.Logger.LogInfo($"[Business] helper stack sell {stackCount}×({name}×{amount}) @'{addr}' → routed.");
                            });
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] sell route: {ex.Message}"); }
                    });
                }
                var discardBtn = Btn("discardButton");
                if (discardBtn != null && discardBtn.gameObject.activeSelf)
                {
                    discardBtn.onClick.RemoveAllListeners();
                    discardBtn.onClick.AddListener(delegate
                    {
                        try
                        {
                            BuildingStorageSync.RequestStackOp(addr, itemId, name, amount, paid, price, stackCount, sell: false);
                            Plugin.Logger.LogInfo($"[Business] helper stack discard {stackCount}×({name}×{amount}) @'{addr}' → routed.");
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] discard route: {ex.Message}"); }
                    });
                }
                // Round-49 slice 2 — the ONLY native action for a building holder is PLACE (furniture on a
                // delivery spot; the equip/read/consume family belongs to the vehicle-picker mode, decompile
                // CargoItemUi.SetupActionButton :226-307). Native gates paid && isFurniture && owned-business
                // && holder isdeliveryspot; for a helper the ownership term is the grant. Click = route the
                // 1-unit reduce to the owner FIRST ("placereduce"); placement starts only on the owner's Ok
                // (BuildingStorageSync.OnResult) so a lost race costs nothing. The placed item then forwards
                // through the existing guest interior-edit flow on completion.
                var actionBtn = Btn("actionButton");
                bool canPlace = false;
                try
                {
                    canPlace = paid && first.ItemCached != null && first.ItemCached.isFurniture
                               && ii.ItemCached != null && ii.ItemCached.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isdeliveryspot);
                }
                catch { }
                if (actionBtn != null)
                {
                    if (canPlace)
                    {
                        try
                        {
                            var lbl = HarmonyLib.AccessTools.Field(typeof(UI.PlayerHUD.CargoItemUi), "actionButtonLabel")?.GetValue(__instance);
                            if (lbl != null)
                            {
                                var t = HarmonyLib.Traverse.Create(lbl);
                                if (t.Property("Key").PropertyExists()) t.Property("Key").SetValue("itempanelui_buttons_place");
                                else t.Field("Key").SetValue("itempanelui_buttons_place");
                            }
                        }
                        catch { }
                        actionBtn.onClick.RemoveAllListeners();
                        var placeSrc = first;
                        actionBtn.onClick.AddListener(delegate
                        {
                            try
                            {
                                if (BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode) return;   // native early-out parity
                                bool fits = true;
                                try { fits = placeSrc.ItemCached.FitsInBuilding(); } catch { }
                                if (!fits) { PassengerHud.Toast("That item is too tall for this building."); return; }
                                BuildingStorageSync.SetPendingPlace(addr, itemId, placeSrc);
                                BuildingStorageSync.RequestTake(addr, itemId, name, 1, paid, price, "placereduce");
                                Plugin.Logger.LogInfo($"[Business] helper PLACE {name} from delivery spot @'{addr}' → reduce routed, placement starts on Ok.");
                            }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] place route: {ex.Message}"); }
                        });
                        actionBtn.gameObject.SetActive(true);
                    }
                    else actionBtn.gameObject.SetActive(false);   // no other holder-context action exists natively
                }

                var itemBtn = Btn("itemButton");
                if (itemBtn == null) return;
                itemBtn.onClick.RemoveAllListeners();
                bool isVehicleCargo = false;
                try { isVehicleCargo = !string.IsNullOrEmpty(BigAmbitions.Items.ItemsGetter.GetByName(name)?.vehicleType); } catch { }
                itemBtn.onClick.AddListener(delegate
                {
                    try
                    {
                        if (isVehicleCargo)
                        {
                            // Round-49 slice 4 — unpack a stored hand truck/flatbed. The owner's copy loses the
                            // cargo (routed, "vehicletake"); the vehicle spawns LOCALLY on the owner's Ok and is
                            // the HELPER'S OWN from birth (user ruling 2026-07-21) — same native spawn call the
                            // owner's own unpack runs, so the normal local-vehicle sync picks it up.
                            if (PlayerHelper.IsHoldingItem || PlayerHelper.IsUsingVehicle)
                            { PassengerHud.Toast("Empty your hands to unpack that."); return; }
                            BuildingStorageSync.RequestTake(addr, itemId, name, amount, paid, price, "vehicletake");
                            Plugin.Logger.LogInfo($"[Business] helper stored-vehicle take {name} @'{addr}' → routed; spawns locally on Ok.");
                            try { InstanceBehavior<UI.UIs>.Instance.playerHUD.manageCargoUI.Close(); } catch { }
                            return;
                        }
                        if (PlayerHelper.IsHoldingItem || PlayerHelper.IsUsingVehicle)
                        { PassengerHud.Toast("Empty your hands to take that."); return; }
                        // Sealed boxes ride "boxtake" (whole instance + nested contents); loose stacks
                        // ride the generic take (the fridge path).
                        BuildingStorageSync.RequestTake(addr, itemId, name, amount, paid, price, isSealed ? "boxtake" : "");
                        Plugin.Logger.LogInfo($"[Business] helper {(isSealed ? "box" : "stack")} take {name}×{amount} @'{addr}' → routed to owner.");
                        try { InstanceBehavior<UI.UIs>.Instance.playerHUD.manageCargoUI.Close(); } catch { }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] take route: {ex.Message}"); }
                });

                UnityEngine.UI.Button? Btn(string field)
                {
                    try { return HarmonyLib.AccessTools.Field(typeof(UI.PlayerHUD.CargoItemUi), field)?.GetValue(__instance) as UnityEngine.UI.Button; }
                    catch { return null; }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] cargo row rewire: {ex.Message}"); }
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
