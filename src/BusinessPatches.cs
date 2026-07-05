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

    // ── DIAG:INVESTIGATION(register-helper), round 35 ────────────────────────────────────────────
    // Two register reports to localize before fixing: (1) a HELPER holding paper bags gets "the
    // interface" instead of the owner's auto-load (the load lives on the register's CHILD Producer,
    // reached only through ItemController.Interact :529 — something earlier in the chain eats the
    // click); (2) the helper still saw "out of paper" notices, whose code is IsPlayerOwnedBusiness-
    // gated — a helper seeing them implies a leaked ownership flip or an unexpected caller.
    // One probe names the branch, the other names the notifier + ownership state. REMOVE when settled.

    [HarmonyPatch(typeof(Controllers.CashRegisterController), nameof(Controllers.CashRegisterController.Interact))]
    public static class Probe_Register_HelperInteract
    {
        static void Prefix(Controllers.CashRegisterController __instance)
        {
            try
            {
                if (!BusinessHelperRoute.HelperHere(out var addr)) return;
                var ii = __instance.ItemInstance;
                var stock = (ii?.cargoInstances != null && ii.cargoInstances.Count > 0) ? ii.cargoInstances[0] : null;
                bool purch = false; try { purch = __instance.playerItemPurchaserSettings?.enabled ?? false; } catch { }
                bool canOrder = false; try { canOrder = __instance.CanOrder(); } catch { }
                bool paidAll = true; try { paidAll = PlayerHelper.HasPaidForAllItems(); } catch { }
                string held = "none";
                try { var h = PlayerHelper.ItemInstanceInHands; if (h != null) held = $"{h.itemName}(cargo={(h.cargoInstances?.Count ?? 0)})"; } catch { }
                string et = "?";
                try { et = (AccessTools.Field(typeof(EmployeeStationController), "employeeType")?.GetValue(__instance) as Type)?.Name ?? "null"; } catch { }
                string childIds = "";
                try { foreach (var k in __instance.GetComponentsInChildren<Producer>(true)) childIds += (k?.ItemInstance?.id?.ToString() ?? "?") + ","; } catch { }
                bool rented = false;
                try { rented = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer ?? false; } catch { }
                Plugin.Logger.LogInfo($"[RegHelper] Interact @'{addr}' purch={purch} employeeType={et} canOrder={canOrder} paidAll={paidAll} held='{held}' stock={(stock == null ? "none" : $"{stock.itemName}x{stock.amount}")} childProducers='{childIds}' rentedNOW={rented}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[RegHelper] probe: {ex.Message}"); }
        }
    }

    /// <summary>DIAG:INVESTIGATION(register-helper), round 36 — which CTA the click layer picks at a
    /// REGISTER, for owner AND helper. Click flow (per FridgeProbe round-16 mapping): OnIoLeftClick →
    /// UpdateCta → ctaAction==null ? open overlay : run action. Three runs show the helper's click opens
    /// the overlay ⇒ their ctaAction is null; the owner's bag auto-load rides the chosen CTA — its ctaKey
    /// names the exact action helper parity must route. REMOVE when settled.</summary>
    [HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.CtaManager), nameof(Player.HUD.ItemInfoOverlays.CtaManager.UpdateCta))]
    public static class Probe_Register_CtaSelection
    {
        private static float _nextAt;
        static void Postfix(EntityController entityController)
        {
            try
            {
                if (UnityEngine.Time.unscaledTime < _nextAt) return;
                bool isReg = entityController is Controllers.CashRegisterController
                          || (entityController is Producer p && p.parentItemController is Controllers.CashRegisterController);
                // Round-36: also log BOXES — "guest dropped a box, owner can't pick it up": the owner's
                // click on the box picks WHICH cta (grab expected)?
                bool isBox = false;
                try { isBox = entityController is ItemController ic && ic.Item != null && ic.Item.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isbox); } catch { }
                if (!isReg && !isBox) return;
                bool helper = BusinessHelperRoute.HelperHere(out _);
                bool mine = false;
                try { mine = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer ?? false; } catch { }
                if (!helper && !mine) return;
                _nextAt = UnityEngine.Time.unscaledTime + 2f;
                // Round-36f (user caught the gap): held NAME alone can't distinguish "register full" from
                // "box holds the wrong item" — CanAddToInventory refuses both. Log contents + the station's
                // stock so the CTA verdict is self-explanatory.
                string held = "none";
                try { var h = PlayerHelper.ItemInstanceInHands; if (h != null) held = $"{h.itemName}[{Probe_OwnerShelfDeposit.CargoDump(h.cargoInstances)}]"; } catch { }
                string stock = "?";
                try { var ii = (entityController as ItemController)?.ItemInstance; stock = Probe_OwnerShelfDeposit.CargoDump(ii?.cargoInstances); } catch { }
                Plugin.Logger.LogInfo($"[RegHelper] CTA @{entityController.GetType().Name} role={(mine ? "owner" : "helper")} key='{Player.HUD.ItemInfoOverlays.CtaManager.ctaKey}' actionNull={Player.HUD.ItemInfoOverlays.CtaManager.ctaAction == null} held='{held}' stock=[{stock}]");
            }
            catch { }
        }
    }

    /// <summary>DIAG:INVESTIGATION(box-pickup) — round 36e: the owner's click on a (guest-dropped) box
    /// sometimes does nothing. Click path: null CTA → ShowDetailedOverlay(box); ONLY if no overlay shows
    /// does the click walk+Interact (= grab). In SP the box has no overlay → grab works; on the owner's MP
    /// machine something makes the overlay SHOW (or Interact refuse). Log the branch verdict per box click.
    /// REMOVE when settled.</summary>
    [HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.OverlayManager),
                  nameof(Player.HUD.ItemInfoOverlays.OverlayManager.ShowDetailedOverlay))]
    public static class Probe_Box_ShowDetailed
    {
        static void Postfix(EntityController entityController, bool __result)
        {
            try
            {
                if (!(entityController is ItemController ic) || ic.Item == null) return;
                bool isBox = ic.Item.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isbox);
                // Round-37b: also log STORAGE SHELVES — the owner's "can't select from the shelf" never
                // produced a manage-cargo open, so the failure is upstream: overlay verdict or click-eaten.
                bool isShelf = ic is StorageShelfController;
                if (!isBox && !isShelf) return;
                bool mine = false;
                try { mine = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer ?? false; } catch { }
                if (!mine && !BusinessHelperRoute.HelperHere(out _)) return;
                Plugin.Logger.LogInfo($"[RegHelper] {(isShelf ? "SHELF" : "BOX")} overlay → {__result} item={ic.itemName} purch={(ic.PlayerItemPurchaser != null)} stacked={ic.ItemInstance?.stackedItems?.Count ?? -1}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(UI.Notification.Notifications), nameof(UI.Notification.Notifications.Show))]
    public static class Probe_PaperbagNotice_Source
    {
        internal static void LogIfPaperbag(string headerKey)
        {
            try
            {
                if (string.IsNullOrEmpty(headerKey)) return;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                // Round-35g: the guest's "not enough bags" notice never matched the paperbag filter —
                // log EVERY notification while standing in a helper business (low volume, names the key
                // + caller of whatever notice reaches a helper that shouldn't).
                if (!headerKey.Contains("paperbag") && !BusinessHelperRoute.HelperHere(out _)) return;
                bool rented = false, owned = false;
                try { rented = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer ?? false; } catch { }
                try { owned = InstanceBehavior<BuildingManager>.Instance?.IsPlayerOwnedBusiness ?? false; } catch { }
                string caller = "";
                try
                {
                    var st = new System.Diagnostics.StackTrace(1, false);
                    for (int f = 0; f < Math.Min(4, st.FrameCount); f++)
                        caller += (f > 0 ? "<" : "") + (st.GetFrame(f)?.GetMethod()?.DeclaringType?.Name ?? "?") + "." + (st.GetFrame(f)?.GetMethod()?.Name ?? "?");
                }
                catch { }
                bool helper = BusinessHelperRoute.HelperHere(out var addr);
                Plugin.Logger.LogInfo($"[RegHelper] NOTICE '{headerKey}' @'{addr}' helper={helper} rentedNOW={rented} ownedNOW={owned} caller={caller}");
            }
            catch { }
        }

        static void Prefix(string headerKey) => LogIfPaperbag(headerKey);
    }

    // FullServiceEmployee's serving loop uses the ShowError overload — cover it too.
    [HarmonyPatch(typeof(UI.Notification.Notifications), nameof(UI.Notification.Notifications.ShowError))]
    public static class Probe_PaperbagNotice_SourceErr
    {
        static void Prefix(string headerKey) => Probe_PaperbagNotice_Source.LogIfPaperbag(headerKey);
    }

    // ── DIAG:INVESTIGATION(shelf-item-loss), round 35b ───────────────────────────────────────────
    // Owner deposited a box on their storage shelf, lost hover/click on it, moved it, and the cargo was
    // gone. NO adoption ran on the owner that session (zero "Interior applied" lines) → the loss is
    // LOCAL. Suspect: stale IsUsingVehicle derailing the native deposit source-selection, so the deposit
    // may never have landed. Two probes: (A) ANY item removal from an OWNED building logs item+cargo+
    // caller chain — catches every deletion path red-handed; (B) the owner's shelf deposit logs the
    // vehicle-state + held item + shelf cargo before/after — shows whether the deposit landed at all.
    // REMOVE when settled.

    /// <summary>DIAG:INVESTIGATION(shelf-item-loss) — owner-side shelf STATE scanner (round 35d): every 10s
    /// inside an OWNED business, dump each storage shelf's interactability gates. The owner's shelf stopped
    /// hovering/clicking after a deposit attempt; ShouldReactToIoEnter can only refuse an owner via
    /// !enabled / !visible / !primaryInteractionEnabled (:459 — the owner branch at :463 passes everything
    /// else), and a dead CLICK additionally suggests colliders. This names the flipped gate without
    /// depending on the user's hover timing. Ticked from MPCanvasUI. REMOVE when settled.</summary>
    internal static class BusinessDiag
    {
        private static float _nextAt, _nextRayAt;

        /// <summary>DIAG(shelf-access) round-37h — cursor-ray X-ray: the hover system is a FIRST-HIT
        /// Physics.Raycast (InputHelper.GetClickedComponent :62-77); an invisible collider in front of the
        /// shelf silently wins the ray and kills hover. Log the first hits under the cursor (name, layer,
        /// distance, owning ItemController) every 3s while in an owned business — the occluder, if any,
        /// prints its own name. REMOVE when settled.</summary>
        private static void TickCursorRay()
        {
            if (UnityEngine.Time.unscaledTime < _nextRayAt) return;
            _nextRayAt = UnityEngine.Time.unscaledTime + 3f;
            try
            {
                bool mine = false;
                try { mine = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer ?? false; } catch { }
                if (!mine) return;   // owned business only — no city-wide spam
                var cam = GameManager.GetMainCamera();
                if (cam == null) return;
                var ray  = cam.ScreenPointToRay(UnityEngine.Input.mousePosition);
                var hits = UnityEngine.Physics.RaycastAll(ray, 600f);
                if (hits == null || hits.Length == 0) return;
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < hits.Length && i < 4; i++)
                {
                    var h = hits[i];
                    ItemController? icp = null;
                    try { icp = h.collider.GetComponentInParent<ItemController>(); } catch { }
                    sb.Append($"[{i}]{h.collider.name}(L:{UnityEngine.LayerMask.LayerToName(h.collider.gameObject.layer)} d:{h.distance:F1} ic:{icp?.GetType().Name ?? "none"}) ");
                }
                // Round-37k — MOUSE DISPATCHER X-RAY (the last black box): MouseController only fires
                // OnIoEnter when its target slot is EMPTY and its OWN masked raycast hits (:156-168);
                // right-click reuses the CURRENT target (:172). A sticky target or a narrowed
                // RaycastLayerMark (SetRestrictedObjectTypes — placement/map/mop set it, restores can be
                // skipped) reproduces every observed symptom. Dump its complete private state.
                string disp = "";
                try
                {
                    var t = typeof(MouseController);
                    var tgt = HarmonyLib.AccessTools.Field(t, "currentTargetEntity")?.GetValue(null) as EntityController;
                    var ctr = HarmonyLib.AccessTools.Field(t, "CurrentTarget")?.GetValue(null) as UnityEngine.Transform;
                    var mv  = HarmonyLib.AccessTools.Field(t, "RaycastLayerMark")?.GetValue(null);
                    int mask = mv is UnityEngine.LayerMask lm ? lm.value : (mv is int mi ? mi : -1);
                    var names = new System.Text.StringBuilder();
                    for (int b = 0; b < 32 && mask > 0; b++)
                        if ((mask & (1 << b)) != 0) { var n = UnityEngine.LayerMask.LayerToName(b); names.Append(string.IsNullOrEmpty(n) ? b.ToString() : n).Append('+'); }
                    // round-37l: the dispatcher's LAST unlogged gate (:161) — an invisible UI element under
                    // the cursor (IsPointerOverGameObject) blocks ALL acquisition; our canvas is a suspect.
                    bool uiOver = false;
                    string uiName = "";
                    try
                    {
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        uiOver = es != null && es.IsPointerOverGameObject();
                        if (uiOver)
                        {
                            // Name the exact UI element eating the pointer (round-37l).
                            var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = UnityEngine.Input.mousePosition };
                            var results = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                            es.RaycastAll(ped, results);
                            for (int r = 0; r < results.Count && r < 2; r++)
                            {
                                var go = results[r].gameObject;
                                string root = "";
                                try { root = go.transform.root?.name ?? ""; } catch { }
                                uiName += $"{go.name}(root:{root})/";
                            }
                        }
                    }
                    catch { }
                    string hands = "empty";
                    try { hands = PlayerHelper.ItemInstanceInHands?.itemName ?? "empty"; } catch { }
                    disp = $"target={(tgt != null ? tgt.GetType().Name + ":" + tgt.name : "null")} curTr={(ctr != null ? ctr.name : "null")} uiOver={uiOver}{(uiName.Length > 0 ? $" ui[{uiName}]" : "")} hands='{hands}' mask={names}";
                }
                catch (Exception ex) { disp = "err:" + ex.Message; }
                Plugin.Logger.LogInfo($"[ShelfLoss] cursor ray: {sb}| dispatcher {disp}");
            }
            catch { }
        }

        internal static void Tick()
        {
            try
            {
                TickCursorRay();
                if (UnityEngine.Time.unscaledTime < _nextAt) return;
                _nextAt = UnityEngine.Time.unscaledTime + 10f;
                // Runs in SINGLE-PLAYER too (round 35e) — the SP baseline needs the same scanner.
                var bm  = InstanceBehavior<BuildingManager>.Instance;
                var reg = bm?.buildingRegistration;
                if (reg == null) return;
                // Round-37d (owner: no hover with empty hands in OWN shop): IsPlayerOwnedBusiness is just
                // manager.buildingRegistration.RentedByPlayer — log the flag AND whether the manager's reg
                // reference IS the save's reg for this address (stale-pointer vs flipped-flag discriminator).
                try
                {
                    string addr = GameStateReader.AddressKey(reg);
                    BuildingRegistration? saveReg = null;
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations != null)
                        foreach (var r in gi.BuildingRegistrations)
                            if (r != null && GameStateReader.AddressKey(r) == addr) { saveReg = r; break; }
                    // Round-37e OBSERVATION ONLY (user: identify the culprit, no blanket repairs). Three-way
                    // discriminator for the dead empty-hand hover:
                    //  duplicates>1 → a COUNTERFEIT blank reg was minted for this address
                    //                 (BuildingHelper.GetBuildingRegistration fabricates one on a lookup
                    //                 miss — see Probe_RegMint, which names the minter's caller);
                    //  sameObj=False → the manager holds a STALE reg object;
                    //  flags false   → the flag itself was flipped on the real reg.
                    int duplicates = 0;
                    BuildingRegistration? firstMatch = null;
                    var gi2 = SaveGameManager.Current;
                    if (gi2?.BuildingRegistrations != null)
                        foreach (var r in gi2.BuildingRegistrations)
                            if (r != null && GameStateReader.AddressKey(r) == addr)
                            { duplicates++; if (firstMatch == null) firstMatch = r; }
                    saveReg = firstMatch;
                    bool same = object.ReferenceEquals(reg, saveReg);
                    Plugin.Logger.LogInfo($"[ShelfLoss] ownership: addr='{addr}' mgrRented={reg.RentedByPlayer} ownedBiz={bm!.IsPlayerOwnedBusiness} sameObj={same} saveRented={(saveReg != null ? saveReg.RentedByPlayer.ToString() : "noSaveReg")} duplicates={duplicates}");
                }
                catch { }
                if (!reg.RentedByPlayer) return;
                foreach (var s in UnityEngine.Object.FindObjectsOfType<StorageShelfController>())
                {
                    if (s == null) continue;
                    bool colOn = false; int cols = 0;
                    try
                    {
                        var cc = s.GetComponentsInChildren<UnityEngine.Collider>(true);
                        cols = cc.Length;
                        foreach (var c in cc) if (c != null && c.enabled) { colOn = true; break; }
                    }
                    catch { }
                    bool purch = false; try { purch = s.PlayerItemPurchaser != null; } catch { }
                    // round-37i: THE suspected stuck flag — protected field, read via AccessTools.
                    string dhi = "?";
                    try { dhi = (HarmonyLib.AccessTools.Field(typeof(EntityController), "disableHighlightInteraction")?.GetValue(s) as bool?)?.ToString() ?? "?"; } catch { }
                    // layer (round-37g): the hover RAYCAST only sees layers in the game's interactive mask —
                    // a shelf respawned onto the wrong layer is invisible to the mouse regardless of flags.
                    string layers = "";
                    try
                    {
                        layers = UnityEngine.LayerMask.LayerToName(s.gameObject.layer);
                        var cc2 = s.GetComponentsInChildren<UnityEngine.Collider>(true);
                        foreach (var c in cc2)
                            if (c != null && c.gameObject.layer != s.gameObject.layer)
                            { layers += "/col:" + UnityEngine.LayerMask.LayerToName(c.gameObject.layer); break; }
                    }
                    catch { }
                    Plugin.Logger.LogInfo($"[ShelfLoss] shelf state id={s.ItemInstance?.id} enabled={s.enabled} goActive={s.gameObject.activeInHierarchy} visible={s.visible} primary={s.primaryInteractionEnabled} disableHi={dhi} purchaser={purch} colliders={cols}(anyOn={colOn}) layer={layers} cargo={s.ItemInstance?.cargoInstances?.Count ?? -1}");
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(BuildingRegistration), nameof(BuildingRegistration.RemoveItemInstanceFromBuilding))]
    public static class Probe_OwnShop_ItemRemoval
    {
        static void Prefix(BuildingRegistration __instance, ItemInstance __0)
        {
            try
            {
                // Runs in SINGLE-PLAYER too (round 35e) — the SP baseline run needs identical attribution.
                if (__instance == null || __0 == null) return;
                bool mine = false; try { mine = __instance.RentedByPlayer; } catch { }
                if (!mine) return;
                int stacks = 0, units = 0;
                try { var cs = __0.cargoInstances; if (cs != null) { stacks = cs.Count; foreach (var c in cs) units += c?.amount ?? 0; } } catch { }
                string caller = "";
                try
                {
                    var st = new System.Diagnostics.StackTrace(1, false);
                    for (int f = 0; f < Math.Min(4, st.FrameCount); f++)
                        caller += (f > 0 ? "<" : "") + (st.GetFrame(f)?.GetMethod()?.DeclaringType?.Name ?? "?") + "." + (st.GetFrame(f)?.GetMethod()?.Name ?? "?");
                }
                catch { }
                Plugin.Logger.LogInfo($"[ShelfLoss] REMOVED from OWN building: {__0.itemName} id={__0.id} cargo={stacks}st/{units}u caller={caller}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(StorageShelfController), nameof(StorageShelfController.AddItemsToStorage))]
    public static class Probe_OwnerShelfDeposit
    {
        // Per-stack detail (round 35e): a merge into an existing stack doesn't change the COUNT — the
        // first version's count-only before/after couldn't distinguish "deposit landed via merge" from
        // "deposit refused". Names + amounts + sealed flags settle it. Runs in SINGLE-PLAYER too so a
        // native baseline run is directly comparable.
        internal static string CargoDump(System.Collections.Generic.List<CargoInstance>? cs)
        {
            if (cs == null) return "null";
            var sb = new System.Text.StringBuilder();
            foreach (var c in cs)
            {
                if (c == null) continue;
                sb.Append(c.itemName).Append('x').Append(c.amount);
                if (c.IsSealed) sb.Append("(SEALED)");
                if (c.nestedCargoInstances != null && c.nestedCargoInstances.Count > 0) sb.Append($"(nested={c.nestedCargoInstances.Count})");
                sb.Append(';');
            }
            return sb.Length > 0 ? sb.ToString() : "empty";
        }

        static void Prefix(StorageShelfController __instance, out string? __state)
        {
            __state = null;
            try
            {
                bool mine = false; try { mine = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer ?? false; } catch { }
                if (!mine) return;
                var ii = __instance?.ItemInstance;
                __state = CargoDump(ii?.cargoInstances);
                bool usingVeh = false, curNull = true;
                try { usingVeh = PlayerHelper.IsUsingVehicle; curNull = VehicleHelper.GetCurrentVehicle() == null; } catch { }
                string held = "none";
                try { var h = PlayerHelper.ItemInstanceInHands; if (h != null) held = $"{h.itemName}[{CargoDump(h.cargoInstances)}]"; } catch { }
                Plugin.Logger.LogInfo($"[ShelfLoss] owner AddItemsToStorage: shelf={ii?.id} before=[{__state}] usingVeh={usingVeh} curVehNull={curNull} held='{held}'");
            }
            catch { }
        }
        static void Postfix(StorageShelfController __instance, string? __state)
        {
            try
            {
                if (__state == null) return;
                Plugin.Logger.LogInfo($"[ShelfLoss] owner AddItemsToStorage: after=[{CargoDump(__instance?.ItemInstance?.cargoInstances)}] (was [{__state}]).");
            }
            catch { }
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
                if (BusinessHelperRoute.RouteStationRefill(__instance, addr) == BusinessHelperRoute.RefillResult.Handled)
                { __result = true; return false; }
                return true;   // shape didn't match → native (customer) flow
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] producer refill route: {ex.Message}"); return true; }
        }
    }

    /// <summary>Round-37b — THE REAL CLICK PATH (probe-proven: ctaKey is '' for the OWNER too, every
    /// line): no CTA behavior claims the register at all. The owner's deposit works via the FINAL
    /// fallback — null CTA → ShowDetailedOverlay returns false for the owner → WalkOverAndInteract →
    /// Producer.Interact owner-merge. The helper never gets there: our visibility flip makes the
    /// register's overlay SHOW, eating the click ("the interface"). Fix at the decision point: after
    /// UpdateCta, if nothing claimed the click and the helper's cargo fits the register, BIND the
    /// click action directly (CtaManager.ctaKey/ctaAction are public statics) — the click then runs
    /// the routed refill and the overlay never opens, mirroring the owner's experience.</summary>
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

    /// <summary>Round-37b — the register-bag DUPLICATION (probe-proven: guest register replica 0 bags +
    /// guest holding 1000 while the owner's register still has 1000; NO routed op logged): the visibility
    /// flip exposes the manage-cargo UI on owner-authoritative building items, and its take paths mutate
    /// the LOCAL replica only. Same class round-13 blocked for storage shelves — block the whole manage UI
    /// for helpers on building-owned holders until a routed panel exists. Also logs OWNER opens (holder +
    /// stack count) to localize the "owner can't access shelf deposits" report.</summary>
    /// <summary>DIAG:INVESTIGATION(shelf-access) — round 37e: `BuildingHelper.GetBuildingRegistration`
    /// FABRICATES a blank registration (RentedByPlayer=false) on a lookup miss and permanently adds it to
    /// the world list + cache (decompile :107-141). If that fires for an address that already has a real
    /// reg — e.g. any caller during a load window while the list is mid-populate — the counterfeit shadows
    /// the real one from then on (address lookups become a coin toss). Log EVERY fabrication with the
    /// caller chain: one line names the culprit. Pure observation. REMOVE when settled.</summary>
    [HarmonyPatch(typeof(Helpers.BuildingHelper), nameof(Helpers.BuildingHelper.GetBuildingRegistration), typeof(Address))]
    public static class Probe_RegMint
    {
        static void Prefix(out int __state)
        {
            __state = -1;
            try { __state = SaveGameManager.Current?.BuildingRegistrations?.Count ?? -1; } catch { }
        }
        static void Postfix(Address address, int __state)
        {
            try
            {
                int now = SaveGameManager.Current?.BuildingRegistrations?.Count ?? -1;
                if (__state < 0 || now <= __state) return;   // nothing was minted
                string caller = "";
                var st = new System.Diagnostics.StackTrace(2, false);
                for (int f = 0; f < Math.Min(8, st.FrameCount) && caller.Split('<').Length < 5; f++)
                {
                    var m = st.GetFrame(f)?.GetMethod();
                    var tn = m?.DeclaringType?.Name ?? "?";
                    if (tn.Contains("Harmony") || tn.Contains("MonoMethod") || tn.Contains("DynamicMethod")) continue;
                    caller += (caller.Length > 0 ? "<" : "") + tn + "." + (m?.Name ?? "?");
                }
                Plugin.Logger.LogWarning($"[ShelfLoss] REG MINTED for '{address?.streetNumber} {address?.streetName}' (list {__state}→{now}) caller={caller}");
            }
            catch { }
        }
    }

    /// <summary>DIAG:INVESTIGATION(shelf-access) — round 37g, THE HOVER PROBE (the gap the prior 10 runs
    /// never covered): logs the hover check's own verdict every time the mouse actually evaluates a
    /// storage shelf in the player's own business. Reading the output:
    ///   lines appear, result=True, no highlight → the outline system is broken downstream;
    ///   lines appear, result=False → the logged flags name the failing term;
    ///   NO lines while hovering empty-handed (but lines WITH a box) → the raycast itself misses —
    ///     collider/layer problem (see the scanner's new layer field).</summary>
    [HarmonyPatch(typeof(ItemController), nameof(ItemController.ShouldReactToIoEnter))]
    public static class Probe_Shelf_HoverVerdict
    {
        private static float _nextAt;
        static void Postfix(ItemController __instance, bool __result)
        {
            try
            {
                if (!(__instance is StorageShelfController s)) return;
                bool mine = false;
                try { mine = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer ?? false; } catch { }
                if (!mine) return;
                if (UnityEngine.Time.unscaledTime < _nextAt) return;
                _nextAt = UnityEngine.Time.unscaledTime + 1f;
                string hands = "empty";
                try { hands = PlayerHelper.ItemInstanceInHands?.itemName ?? (PlayerHelper.IsUsingVehicle ? "vehicle" : "empty"); } catch { }
                bool owned = false; try { owned = InstanceBehavior<BuildingManager>.Instance.IsPlayerOwnedBusiness; } catch { }
                Plugin.Logger.LogInfo($"[ShelfLoss] HOVER verdict={__result} hands='{hands}' ownedBiz={owned} enabled={s.enabled} visible={s.visible} primary={s.primaryInteractionEnabled}");
            }
            catch { }
        }
    }

    /// <summary>DIAG:INVESTIGATION(shelf-access) — round 37b: does the OWNER's shelf click even reach
    /// ManageStorage? Zero manage-cargo opens logged while the user reports "can't select from shelves" —
    /// this places the drop-off. REMOVE when settled.</summary>
    [HarmonyPatch(typeof(StorageShelfController), nameof(StorageShelfController.ManageStorage))]
    public static class Probe_Shelf_ManageEntry
    {
        static void Prefix(StorageShelfController __instance)
        {
            try
            {
                bool mine = false;
                try { mine = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.RentedByPlayer ?? false; } catch { }
                if (!mine) return;
                Plugin.Logger.LogInfo($"[RegHelper] owner ManageStorage ENTERED: shelf={__instance?.ItemInstance?.id} (walk starts; manage-cargo Show follows on arrival).");
            }
            catch { }
        }
    }

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
                    Plugin.Logger.LogInfo($"[Business] manage-cargo UI blocked for helper on {ii.itemName} (unrouted take paths = dup).");
                    return false;
                }
                // Owner-side open — DIAG for "can't access deposited items": what the UI is about to show.
                bool mine = false; try { mine = reg!.RentedByPlayer; } catch { }
                if (mine)
                    Plugin.Logger.LogInfo($"[Business] owner manage-cargo open: {ii.itemName} id={ii.id} stacks=[{Probe_OwnerShelfDeposit.CargoDump(ii.cargoInstances)}]");
            }
            catch { }
            return true;
        }
    }

    /// <summary>Round-36d — the OWNER's register bag-deposit rides the CTA layer (probe-decoded:
    /// CashRegisterCtaBehavior.GetCta offers "click_to_add_inventory" ONLY when IsPlayerOwnedBusiness,
    /// :29/:47-63, then walks to the register and Interacts). A HELPER got no CTA → the overlay menu.
    /// When the native pick is EMPTY and the helper's held/vehicle cargo fits the register, offer the
    /// same CTA with the action ROUTED through the owner (shared refill core — clamped, merge-guarded).
    /// Never overrides a native choice (shopping/pay flows keep priority).</summary>
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
                // Round-36e: GetCta can run UNDER our visibility flip (UpdateCta wraps includeHelper) — the
                // native OWNER branch then binds "click_to_add_inventory" whose action is walk+Interact(),
                // which dies on the UNFLIPPED Interact (falls into the customer purchase flow). If native
                // picked exactly that for a helper, keep the label but SWAP the action for the routed one.
                bool nativeAddInventory = __result.Item1 == "click_to_add_inventory";
                if (!string.IsNullOrEmpty(__result.Item1) && !nativeAddInventory) return;   // other native picks (pay/work/order) keep priority
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
