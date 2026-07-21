using System;
using HarmonyLib;
using BigAmbitions.Items;   // CargoInstance, ICargoHolder, ItemInstance
using Buildings;
using Buildings.Indoors.InteriorDesign;   // InteriorDesignerController
using Entities;
using Helpers;
using UI.CurrentBuilding;   // CurrentBuildingUI
using UI.InteriorDesigner;  // InteriorDesignerUI
using Player.HUD.ItemInfoOverlays;   // CtaManager, OverlayManager (interaction-gate wraps)
using UI.Overlays;                   // BuildingEntranceOverlay (re-entry diagnostic)

namespace BigAmbitionsMP
{
    /// <summary>Shared residency — ENTRY gate. The game's BuildingHelper.CanEnterBuilding passes only the
    /// owner (RentedByPlayer) or an open business; a residence stays closed to everyone else. The host pushes
    /// each guest the set of buildings they may enter (GrantSync.CanEnterGranted, kept current on grant +
    /// ownership change), and this Postfix flips the result true for those — so a granted guest can walk in.
    /// See docs/PERMISSIONS-SYSTEM.md (Housing).</summary>
    [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.CanEnterBuilding))]
    public static class Patch_BuildingHelper_CanEnterBuilding_Grant
    {
        private static readonly System.Collections.Generic.Dictionary<string, float> _nextDiagAt = new();
        static void Postfix(Address address, ref bool __result)
        {
            if (__result) return;   // already enterable (the owner, or an open business) — leave it
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;   // no grants in single-player
                string addr = GameStateReader.AddressKey(address);
                // Round-32: a business HELPER may enter the owner's business even while it's closed to the
                // public — that's exactly when you restock together. Same postfix, second key set.
                bool guest = GrantSync.CanEnterGranted(addr);
                if (!guest && !GrantSync.IsHelperBusiness(addr)) return;
                __result = true;
                // DIAGNOSTIC (remove once re-entry is settled): confirm the gate grants on every attempt, throttled per addr.
                float now = UnityEngine.Time.unscaledTime;
                if (!_nextDiagAt.TryGetValue(addr, out var due) || now >= due)
                { _nextDiagAt[addr] = now + 5f; Plugin.Logger.LogInfo($"[Housing] CanEnter GRANTED '{addr}' ({(guest ? "guest" : "helper")})."); }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] CanEnterBuilding guard: {ex.Message}"); }
        }
    }

    /// <summary>DIAGNOSTIC (remove once re-entry settled): when a building door is clicked, log the vehicle state —
    /// the gate passes but the game skips the actual enter when `selectedVehicle` is a car. This shows whether a
    /// stale borrowed-car SELECTION (selVeh='BAMP_…') or the player being counted as IN/driving it (curVeh='BAMP_…')
    /// is what blocks re-entry after accessing the trunk.</summary>
    [HarmonyPatch(typeof(BuildingEntranceOverlay), "EnterBuilding")]
    public static class Patch_EnterBuilding_VehStateDiag
    {
        static void Prefix()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                var sel = InstanceBehavior<GameManager>.Instance?.selectedVehicle;
                var cur = VehicleHelper.GetCurrentVehicle();
                Plugin.Logger.LogInfo($"[Housing] EnterBuilding: selVeh='{(sel?.vehicleInstance?.id ?? "null")}' curVeh='{(cur?.id ?? "null")}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] EnterBuilding diag: {ex.Message}"); }
        }
    }

    /// <summary>Shared residency — FURNITURE functionality. The owner-gated furniture (bed, TV, computer,
    /// workout machine) checks the current building's <c>RentedByPlayer</c> (directly, or via
    /// BuildingManager.IsPlayerOwnedBusiness). While a GRANTED GUEST performs one of those interactions in the
    /// owner's home, we briefly mark the current building rented-by-them so the native gate passes, then restore
    /// it the instant the call returns — so nothing else ever reads the guest as the owner (no ownership leakage,
    /// no persistent registration: the field is back to its real value before any save/ownership code runs).
    /// Depth-counted so it's safe even if calls nest. Everything else in a home (toilet/sink/shower/fridge/
    /// wardrobe/shelves/seats) is already open to anyone inside and needs no patch.</summary>
    internal static class HousingFurniture
    {
        private static BuildingRegistration? _forced;
        private static bool _savedValue;
        private static int  _depth;

        internal static bool LocalGuestHere()
        {
            try
            {
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                return reg != null && GrantSync.CanEnterGranted(GameStateReader.AddressKey(reg));
            }
            catch { return false; }
        }

        /// <summary>Round-32: the local player is inside a business they hold a HELPER grant for. Kept apart
        /// from LocalGuestHere — a business helper must NOT inherit the blanket residence flips (the CTA/
        /// overlay wraps would pass the native owner-gates on shelf stock, register work, and the management
        /// computer with NO routing behind them, mutating the replica). Helper gates opt in one by one.</summary>
        internal static bool LocalHelperHere()
        {
            try
            {
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                return reg != null && GrantSync.IsHelperBusiness(GameStateReader.AddressKey(reg));
            }
            catch { return false; }
        }

        internal static void Enter(bool includeHelper = false)
        {
            try
            {
                if (_depth == 0)
                {
                    _forced = null;
                    if (LocalGuestHere() || (includeHelper && LocalHelperHere()))
                    {
                        var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                        if (reg != null) { _forced = reg; _savedValue = reg.RentedByPlayer; reg.RentedByPlayer = true; }
                    }
                }
                _depth++;
            }
            catch { }
        }

        internal static void Exit()
        {
            try
            {
                if (_depth > 0) _depth--;
                if (_depth == 0 && _forced != null) { _forced.RentedByPlayer = _savedValue; _forced = null; }
            }
            catch { _depth = 0; _forced = null; }
        }
    }

    [HarmonyPatch(typeof(BedController), "PerformActivity")]
    public static class Patch_BedController_PerformActivity_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    [HarmonyPatch(typeof(TVController), "PerformActivity")]
    public static class Patch_TVController_PerformActivity_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    [HarmonyPatch(typeof(ComputerController), "Interact")]
    public static class Patch_ComputerController_Interact_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    [HarmonyPatch(typeof(ComputerController), "PerformActivity")]
    public static class Patch_ComputerController_PerformActivity_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    [HarmonyPatch(typeof(WorkoutMachineController), "PerformActivity")]
    public static class Patch_WorkoutMachineController_PerformActivity_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    // Shared residency — INTERACTION GATES (the "offer it" half). The activity patches above force ownership only
    // DURING the activity, but the game decides whether to even OFFER the interaction earlier — the CTA, the
    // detailed overlay, and the hover-react all read IsPlayerOwnedBusiness (= RentedByPlayer) FIRST. For a guest
    // that's false, so the bed shows no "Sleep" prompt and the fridge overlay never opens (click does nothing —
    // user 2026-06-30). Wrap those three read-only decision methods with the SAME scoped override so a guest is
    // offered the interaction and can complete it. Bounded to these UI decisions (NOT LoadBuilding / save / the
    // many business-context IsPlayerOwnedBusiness readers, which never run in a residence). See PERMISSIONS-SYSTEM.md.
    // includeHelper on the five visibility wraps (round-32): a business HELPER is offered interactions too.
    // SAFE ONLY BECAUSE every mutation path this exposes in a business is accounted for: storage-shelf put +
    // fridge/wardrobe family routed (GuestRoute now helper-aware), stock dropdown + producer refill routed
    // (BusinessPatches), ManageStorage routed per-row (round-47c; delivery-spot place + stored-vehicle take
    // routed round-49), sign stock-select routed (round-49, BusinessPatches), and the rest self-gate at
    // INTERACT time on the UNFLIPPED flag (register work: purchaser-enabled short-circuits; DJ booth :28).
    [HarmonyPatch(typeof(CtaManager), nameof(CtaManager.UpdateCta))]
    public static class Patch_CtaManager_UpdateCta_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    [HarmonyPatch(typeof(OverlayManager), "ShouldShowDetailedOverlay")]
    public static class Patch_OverlayManager_DetailedOverlay_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    /// <summary>Round-16, probe-confirmed: the detailed overlay's COMPONENTS have their own ownership checks
    /// (DecorativeItemHolderOverlay.ShouldShow → IsPlayerOwnedBusiness, :38) evaluated inside
    /// OverlayBase.UpdateOverlay at ShowDetailedOverlay:180 — AFTER the wrapped inner gate has exited and
    /// restored the flag. Probe data: inner GATE(FridgeController)=True (wrap engaged) yet the outer
    /// ShowDetailedOverlay=False (0 components) → guest fridge menu never opened. Wrap the WHOLE show call
    /// (also covers GetRelevantEntity's non-owner branch at :170) …</summary>
    [HarmonyPatch(typeof(OverlayManager), nameof(OverlayManager.ShowDetailedOverlay))]
    public static class Patch_OverlayManager_ShowDetailed_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    /// <summary>… and the dynamic refresh, which re-evaluates each component's ShouldShow (OverlayBase:109)
    /// and would SetActive(false) the fridge rows one tick after opening if it ran unwrapped.</summary>
    [HarmonyPatch(typeof(OverlayManager), nameof(OverlayManager.UpdateDynamicComponents),
                  typeof(EntityController), typeof(DynamicOverlayUpdateType))]
    public static class Patch_OverlayManager_UpdateDynamic_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    [HarmonyPatch(typeof(ItemController), nameof(ItemController.ShouldReactToIoEnter))]
    public static class Patch_ItemController_ReactToIoEnter_Guest
    {
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    /// <summary>Shared residency — right-click MOVE/PICK-UP menu. ItemController.SecondaryInteract gates on
    /// reg.RentedByPlayer (decompile :650, EA 0.11) — the same unwrapped-gate family as the CTA/overlay/IO
    /// wraps above, missed in the round-3 sweep: a guest's right-click on placed furniture silently no-ops
    /// (round-10 #3a). Same scoped override. The move it starts completes through PlacementSystem.
    /// SaveCurrentItemBeingMovedPosition → placement forward (below); a pick-up goes through TryToGrabItem →
    /// RemoveItemInstanceFromBuilding → removal forward (below).</summary>
    [HarmonyPatch(typeof(ItemController), nameof(ItemController.SecondaryInteract))]
    public static class Patch_ItemController_SecondaryInteract_Guest
    {
        // includeHelper (round-32): a business HELPER gets the right-click move/pick-up menu too — the move
        // completes through the placement forward and a pick-up through the removal forward, both helper-
        // aware. Right-click offers placement actions only, so the brief flip can't reach the economy paths.
        static void Prefix()    { HousingFurniture.Enter(includeHelper: true); }
        static void Finalizer() { HousingFurniture.Exit(); }
    }

    /// <summary>Shared residency — FRIDGE interception. A granted guest's fridge actions must change the
    /// OWNER's authoritative cargo (reg.itemInstances), not the guest's local copy (which the owner's next
    /// snapshot would overwrite). So for a guest we route take/put through BuildingStorageSync (host → owner
    /// applies → re-syncs) and skip the native local apply. The OWNER, single-player, and businesses keep the
    /// native behavior (CanEnterGranted is false for them). NOTE: a guest "eats" by taking the item to hand
    /// (then eats from hand) — reusing the take path avoids a dup. The native walk-to-fridge is skipped for a
    /// guest (the action routes immediately); minor, polish later if it matters.</summary>
    internal static class HousingFridge
    {
        internal static bool GuestRoute(ItemController fridge, out string addr, out string fid)
        {
            addr = ""; fid = "";
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return false;   // no grants in single-player
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null) return false;
                addr = GameStateReader.AddressKey(reg);
                // Residence guest OR business helper (round-32) — every consumer of this predicate is a
                // cargo-mutation route/block, which is exactly what a helper needs owner-routed too.
                if (!GrantSync.CanEnterGranted(addr) && !GrantSync.IsHelperBusiness(addr)) return false;   // owner / non-guest → native
                fid = fridge?.ItemInstance?.id?.ToString() ?? "";
                return !string.IsNullOrEmpty(fid);
            }
            catch { return false; }
        }
    }

    [HarmonyPatch(typeof(FridgeController), nameof(FridgeController.ConsumeItem))]
    public static class Patch_Fridge_ConsumeItem_Guest
    {
        static bool Prefix(FridgeController __instance, CargoInstance cargoInstance)
        {
            try
            {
                if (cargoInstance == null || !HousingFridge.GuestRoute(__instance, out var addr, out var fid)) return true;
                // PARITY (round-17): the owner's eat = TryToConsume (which itself handles the "too stuffed"
                // refusal + the hunger gain) then remove 1 from the fridge. The old route delivered a take-to-
                // hands BOX instead of eating (user 2026-07-03). Eat locally EXACTLY like the owner; only on
                // success ask the owner to remove ONE — nothing is delivered back (Ctx=consume), so no box and
                // no dup. The rare lost race (someone else drained the stack between click and confirm) costs
                // one phantom bite — no item duplicated or lost.
                try { InstanceBehavior<OverlayManager>.Instance?.HideDetailedOverlay(); } catch { }   // native hides first
                if (cargoInstance.ItemCached != null && cargoInstance.ItemCached.TryToConsume())
                    BuildingStorageSync.RequestTake(addr, fid, cargoInstance.itemName, 1, cargoInstance.paid, cargoInstance.pricePerUnit, "consume");
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] fridge consume route: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(FridgeController), "AddToStorage")]
    public static class Patch_Fridge_AddToStorage_Guest
    {
        static bool Prefix(FridgeController __instance, ICargoHolder cargoHolder)
        {
            try
            {
                if (cargoHolder == null || !HousingFridge.GuestRoute(__instance, out var addr, out var fid)) return true;
                var cargo = cargoHolder.GetCargoInstances();
                if (cargo != null)
                {
                    var snap = new System.Collections.Generic.List<CargoInstance>(cargo);
                    foreach (var c in snap)
                        if (c != null && !c.IsSealed && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                            BuildingStorageSync.RequestPut(addr, fid, c.itemName, c.amount, c.paid, c.pricePerUnit);
                }
                return false;   // routed each item to the owner; skip the native local add
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] fridge add route: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(FridgeController), nameof(FridgeController.EmptyFridge))]
    public static class Patch_Fridge_EmptyFridge_Guest
    {
        static bool Prefix(FridgeController __instance)
        {
            try
            {
                if (!HousingFridge.GuestRoute(__instance, out var addr, out var fid)) return true;
                var cargo = __instance.ItemInstance?.cargoInstances;
                if (cargo != null)
                {
                    var snap = new System.Collections.Generic.List<CargoInstance>(cargo);
                    foreach (var c in snap)
                        if (c != null && !c.IsSealed && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                            BuildingStorageSync.RequestTake(addr, fid, c.itemName, c.amount, c.paid, c.pricePerUnit);
                }
                return false;   // routed every item to the owner; skip the native local empty
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] fridge empty route: {ex.Message}"); return true; }
        }
    }

    // ── Shared residency — WARDROBE / DECORATIVE HOLDER interception (round-12 audit A). ──────────────────
    // The fridge got its guest actions rerouted in round 3; its SIBLINGS — DecorativeItemHolderController
    // (wardrobes, coat racks, hat stands) — kept mutating the guest's LOCAL replica (UseItem/Empty/AddToStorage/
    // worn-store all funnel into ItemInstance cargo ops), which the owner's next snapshot overwrites → dup/loss.
    // Same routing as the fridge: guest actions become BStore take/put requests the OWNER applies, then re-syncs.
    // A guest "uses" an item by taking it to hand (consume/wear from hand) — same UX compromise as fridge eat.

    [HarmonyPatch(typeof(DecorativeItemHolderController), nameof(DecorativeItemHolderController.UseItem))]
    public static class Patch_Holder_UseItem_Guest
    {
        static bool Prefix(DecorativeItemHolderController __instance, CargoInstance cargoInstance)
        {
            try
            {
                if (cargoInstance == null || !HousingFridge.GuestRoute(__instance, out var addr, out var hid)) return true;
                // PARITY (round-17, mirrors the fridge consume above): a CONSUMABLE is eaten in place exactly
                // like the owner (TryToConsume handles refusal + hunger; only a successful bite asks the owner
                // to remove one, delivering nothing). Non-consumables keep the take-to-hands route (native puts
                // them in a bag in hands too).
                if (cargoInstance.ItemCached != null && cargoInstance.ItemCached.saturation > 0)
                {
                    try { InstanceBehavior<OverlayManager>.Instance?.HideDetailedOverlay(); } catch { }
                    if (cargoInstance.ItemCached.TryToConsume())
                        BuildingStorageSync.RequestTake(addr, hid, cargoInstance.itemName, 1, cargoInstance.paid, cargoInstance.pricePerUnit, "consume");
                    return false;
                }
                BuildingStorageSync.RequestTake(addr, hid, cargoInstance.itemName, 1, cargoInstance.paid, cargoInstance.pricePerUnit);
                return false;   // routed to the owner; item arrives in the guest's hands on confirm
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] holder use route: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(DecorativeItemHolderController), nameof(DecorativeItemHolderController.Empty))]
    public static class Patch_Holder_Empty_Guest
    {
        static bool Prefix(DecorativeItemHolderController __instance)
        {
            try
            {
                if (!HousingFridge.GuestRoute(__instance, out var addr, out var hid)) return true;
                var cargo = __instance.ItemInstance?.cargoInstances;
                if (cargo != null)
                {
                    var snap = new System.Collections.Generic.List<CargoInstance>(cargo);
                    foreach (var c in snap)
                        if (c != null && !c.IsSealed && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                            BuildingStorageSync.RequestTake(addr, hid, c.itemName, c.amount, c.paid, c.pricePerUnit);
                }
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] holder empty route: {ex.Message}"); return true; }
        }
    }

    [HarmonyPatch(typeof(DecorativeItemHolderController), "AddToStorage")]
    public static class Patch_Holder_AddToStorage_Guest
    {
        static bool Prefix(DecorativeItemHolderController __instance, ICargoHolder cargoHolder)
        {
            try
            {
                if (cargoHolder == null || !HousingFridge.GuestRoute(__instance, out var addr, out var hid)) return true;
                var cargo = cargoHolder.GetCargoInstances();
                if (cargo != null)
                {
                    var snap = new System.Collections.Generic.List<CargoInstance>(cargo);
                    foreach (var c in snap)
                        if (c != null && !c.IsSealed && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                            BuildingStorageSync.RequestPut(addr, hid, c.itemName, c.amount, c.paid, c.pricePerUnit);
                }
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] holder add route: {ex.Message}"); return true; }
        }
    }

    // Worn-accessory store (right-click a hat stand while wearing a storable hat → native adds the WORN cargo
    // to the holder replica + unequips). Route the put with a Ctx tag; the UNEQUIP happens only on the owner's
    // confirm (BuildingStorageSync.UnequipWornAfterStore) — never before, so a full holder can't vanish the hat.
    [HarmonyPatch(typeof(DecorativeItemHolderController), "StorePlayerWornItemIntoItemHolder")]
    public static class Patch_Holder_StoreWorn_Guest
    {
        static bool Prefix(DecorativeItemHolderController __instance)
        {
            try
            {
                if (!HousingFridge.GuestRoute(__instance, out var addr, out var hid)) return true;
                var acc = SaveGameManager.Current?.accessoriesData;
                if (acc == null) return true;
                bool head = __instance.CanStore(acc.headAccessoryCargoInstance);   // native picks head first (:281)
                var ci = head ? acc.headAccessoryCargoInstance : acc.handAccessoryCargoInstance;
                if (ci == null || string.IsNullOrEmpty(ci.itemName)) return true;
                BuildingStorageSync.RequestPut(addr, hid, ci.itemName, ci.amount > 0 ? ci.amount : 1, ci.paid,
                                               ci.pricePerUnit, head ? "wornHead" : "wornHand");
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] holder worn-store route: {ex.Message}"); return true; }
        }
    }

    // ── Shared residency — STORAGE SHELF interception (round-12 audit B). Same family as the fridge/wardrobe. ──
    // PUT: AddItemsToStorage merges hands/box/hand-truck cargo into the shelf replica → route to the owner.
    // TAKE: ManageStorage opens the native manage-cargo UI, whose item clicks mutate the replica through paths
    // we don't intercept yet — BLOCK it for guests with a clear message rather than ship a silent dup; routing
    // that UI properly is a follow-up (disclosed to the user, 2026-07-03).
    [HarmonyPatch(typeof(StorageShelfController), nameof(StorageShelfController.AddItemsToStorage))]
    public static class Patch_Shelf_AddItems_Guest
    {
        static bool Prefix(StorageShelfController __instance)
        {
            try
            {
                if (!HousingFridge.GuestRoute(__instance, out var addr, out var sid)) return true;
                if (PlayerHelper.IsHoldingShoppingBasket) return true;   // native path just warns — parity
                string shelfId = sid;
                System.Collections.Generic.IEnumerable<CargoInstance>? src = null;
                if (PlayerHelper.IsUsingVehicle)
                {
                    var cur = VehicleHelper.GetCurrentVehicle();
                    if (cur != null && cur.VehicleType != null && cur.VehicleType.spawnInPlayerObject)
                        src = cur.cargoInstances != null ? new System.Collections.Generic.List<CargoInstance>(cur.cargoInstances) : null;
                }
                else if (PlayerHelper.IsHoldingItem)
                {
                    var held = PlayerHelper.ItemInstanceInHands;
                    var contents = held?.cargoInstances;
                    if (contents != null && contents.Count > 0)
                        src = new System.Collections.Generic.List<CargoInstance>(contents);
                    else if (held != null)
                    {
                        var c = held.ConvertToCargoInstance();
                        if (c != null) src = new[] { c };
                    }
                }
                if (src != null)
                    foreach (var c in src)
                        if (c != null && !c.IsSealed && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                            BuildingStorageSync.RequestPut(addr, shelfId, c.itemName, c.amount, c.paid, c.pricePerUnit);
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] shelf add route: {ex.Message}"); return true; }
        }
    }

    // (round-47c: the round-13 "Only the owner can manage this shelf (for now)" ManageStorage block is
    // RETIRED — the manage panel now opens for guests/helpers with per-row routed take/sell/discard;
    // see BusinessPatches HelperStorageGuard + Patch_CargoItemUi_HelperRoute.)

    /// <summary>Shared residency — FURNITURE + FLOORING editing. The interior designer is a batched editor
    /// (debit-on-close), so a guest edits their LOCAL copy of the owner's interior, and on close we forward
    /// the result to the owner who ADOPTS it (ApplyInteriorSnapshot) + re-syncs. Flooring/wallpaper cost
    /// debits the GUEST automatically (they're the local player in the designer = "billed to the actor");
    /// furniture is pre-bought, so placement costs nothing and auto-owns to the owner (it lands in the owner's
    /// reg). See docs/PERMISSIONS-SYSTEM.md.</summary>
    internal static class HousingDesign
    {
        internal static string CurrentBuildingAddr()
        {
            try { var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration; return reg != null ? GameStateReader.AddressKey(reg) : ""; }
            catch { return ""; }
        }

        /// <summary>True when the LOCAL player is a granted guest currently in design mode in this building —
        /// used to suppress applying received interior snapshots that would clobber their in-progress edits.</summary>
        internal static bool GuestIsDesigning(string addressKey)
        {
            try { return InteriorDesignerUI.IsOpen && HousingFurniture.LocalGuestHere() && CurrentBuildingAddr() == addressKey; }
            catch { return false; }
        }

        /// <summary>Bug #5: flush the guest's live floor/wall edits into reg.interiorDesigns before we snapshot them.
        /// The game's BuildingManager.SerializeInteriorDesign() early-outs unless RentedByPlayer (false for a guest),
        /// so the guest's design edits otherwise never leave the scene InteriorElements and the forward would carry
        /// the OLD floor → the owner repaints the old floor. Run it under the same scoped RentedByPlayer override the
        /// furniture patches use. No-op unless the local player is the granted guest currently in THIS building.</summary>
        internal static void CommitLocalDesigns(string addressKey)
        {
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm?.buildingRegistration == null) return;
                if (GameStateReader.AddressKey(bm.buildingRegistration) != addressKey) return;
                if (!HousingFurniture.LocalGuestHere()) return;
                HousingFurniture.Enter();
                try { bm.SerializeInteriorDesign(); }
                finally { HousingFurniture.Exit(); }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] CommitLocalDesigns '{addressKey}': {ex.Message}"); }
        }
    }

    /// <summary>Shared residency — PLACE button + build-mode gate. The Place button (furniture from inventory) and
    /// every build/move/pack/edit-text action gate on BuildingManager.CanBuildOnCurrentBuilding, which is true only
    /// for the renter. Flip it true for a granted guest so they can place + alter furniture in the owner's home. All
    /// of its call sites are UI visibility or AI-employee init skips (decompile-checked, EA 0.11) — none touch
    /// ownership, billing, or saving — so forcing it true for a guest is safe. (Flooring/wallpaper billing is
    /// separate: the interior designer debits the local actor on close.) See docs/PERMISSIONS-SYSTEM.md.</summary>
    [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.CanBuildOnCurrentBuilding), MethodType.Getter)]
    public static class Patch_BuildingManager_CanBuild_Guest
    {
        static void Postfix(ref bool __result)
        {
            try { if (!__result && (HousingFurniture.LocalGuestHere() || HousingFurniture.LocalHelperHere())) __result = true; }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] CanBuild guest gate: {ex.Message}"); }
        }
    }

    /// <summary>Shared residency — FURNITURE PLACEMENT forward. When a granted GUEST finishes free-placing a
    /// furniture item from inventory in the owner's home, forward the interior to the owner (who adopts it). The
    /// item is already in reg.itemInstances by here (ItemPanelUI.TryToStartPlacingItem added it on placement start),
    /// so BuildSnapshot captures it. Furniture is pre-bought → costs nothing and auto-owns to the owner. Gated to
    /// designer-closed so the interior designer's own edits (which forward on HandleOnClose) don't double-send.</summary>
    [HarmonyPatch(typeof(BigAmbitions.PlacementSystem.PlacementSystem), "SaveCurrentItemBeingMovedPosition")]
    public static class Patch_PlacementSystem_GuestForward
    {
        static void Postfix()
        {
            try
            {
                if ((HousingFurniture.LocalGuestHere() || HousingFurniture.LocalHelperHere()) && !InteriorDesignerUI.IsOpen)
                    InteriorSync.ForwardGuestInteriorEdit(HousingDesign.CurrentBuildingAddr());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] placement guest forward: {ex.Message}"); }
        }
    }

    /// <summary>Crash-guard (round-14 #3), universal. Clicking a destroyed/not-yet-initialized ItemController
    /// (e.g. the zombie window while an interior refresh destroy+respawns a changed item) NRE'd deep inside
    /// the game's StartPlacementMode — AFTER it set the PlacementMode navigation blocker (PlacementHelper:119)
    /// but before the matching Unset (:261) — stranding the blocker: the player can't move ("guest locked up",
    /// client log :1232+ / "Tried to set navigation blocker that is already set"). Refuse bad targets cleanly,
    /// and if the native body still throws, swallow it, return false, and RELEASE the blocker.</summary>
    [HarmonyPatch(typeof(PlacementHelper), nameof(PlacementHelper.StartPlacementMode),
                  typeof(ItemController), typeof(bool), typeof(bool))]
    public static class Patch_PlacementHelper_StartPlacementMode_Guard
    {
        static bool Prefix(ItemController itemControllerToPlace, ref bool __result)
        {
            try
            {
                if (itemControllerToPlace == null || itemControllerToPlace.ItemInstance == null)
                { __result = false; return false; }   // destroyed / uninitialized target → refuse, no side effects
                // Field 2026-07-20 (helper's stuck computer, ×5): the native body
                // derefs ItemInstance.ItemCached (warp-cursor wallMounted read)
                // one field deeper than the old check — a refresh-respawned
                // controller can sit in exactly that half-initialized state.
                // Refuse it SPECIFICALLY so the log names the item, not just "NRE".
                if (itemControllerToPlace.ItemInstance.ItemCached == null)
                {
                    Plugin.Logger.LogWarning($"[Housing] StartPlacementMode refused: '{itemControllerToPlace.itemName}' (id {itemControllerToPlace.ItemInstance.id}) has no cached item data — mid-refresh zombie window.");
                    __result = false; return false;
                }
            }
            catch { __result = false; return false; }
            return true;
        }

        static Exception? Finalizer(Exception? __exception, ref bool __result)
        {
            if (__exception == null) return null;
            __result = false;
            try { InstanceBehavior<GameManager>.Instance.playerController.UnsetNavigationBlocker(NavigationBlocker.PlacementMode); } catch { }
            // Full STACK (field 2026-07-20): message-only logging hid the exact
            // native null for a session's worth of failures — never again.
            Plugin.Logger.LogWarning($"[Housing] StartPlacementMode threw ({__exception.GetType().Name}: {__exception.Message}) — refused cleanly, PlacementMode blocker released.\n{__exception.StackTrace}");
            return null;   // swallow — the caller sees false and the player keeps moving
        }
    }

    /// <summary>Shared residency — ITEM REMOVAL forward: the missing half of the symmetric pair with the
    /// placement forward above (ANTIPATTERNS Class 10). A guest grabbing a placed item into hands/vehicle
    /// removes it only from the guest's LOCAL reg (BuildingRegistration.RemoveItemInstanceFromBuilding — the
    /// single removal chokepoint; ItemController.Interact :519 + TryToGrabItem :637, EA 0.11) — the owner's
    /// authoritative copy kept it, so the owner still saw the item on the ground while the guest held it =
    /// duplication (round-10 #3b). Forward the post-removal interior (ItemInstancesAuthoritative) so the
    /// owner adopts the removal. No forward-loop: the mod's own snapshot APPLY rewrites reg.itemInstances
    /// directly (GameStatePatcher) and never calls this method.</summary>
    [HarmonyPatch(typeof(BuildingRegistration), nameof(BuildingRegistration.RemoveItemInstanceFromBuilding))]
    public static class Patch_BuildingRegistration_RemoveItem_GuestForward
    {
        static void Postfix(BuildingRegistration __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;   // single-player → native only
                string addr = GameStateReader.AddressKey(__instance);
                if (!GrantSync.CanEnterGranted(addr) && !GrantSync.IsHelperBusiness(addr)) return;   // owner / non-guest → native only
                InteriorSync.ForwardGuestInteriorEdit(addr);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] guest removal forward: {ex.Message}"); }
        }
    }

    /// <summary>Ungate the building overlay's owner-actions (which hold the Decorate button) for a granted guest,
    /// so they can open the interior designer in the owner's home. (Other owner actions there remain
    /// host-authoritative — a guest clicking them is rejected; verify the exposed set in-test.)</summary>
    [HarmonyPatch(typeof(CurrentBuildingUI), "UpdateBuildingData")]
    public static class Patch_CurrentBuildingUI_UngateGuestDesign
    {
        static void Postfix(CurrentBuildingUI __instance)
        {
            try { if (__instance?.ownerActions != null && (HousingFurniture.LocalGuestHere() || HousingFurniture.LocalHelperHere())) __instance.ownerActions.gameObject.SetActive(true); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] ungate design: {ex.Message}"); }
        }
    }

    /// <summary>When a granted GUEST closes the interior designer, forward their edited interior to the owner,
    /// who adopts it (the owner is the authority; the guest's local edits would otherwise be overwritten).</summary>
    [HarmonyPatch(typeof(InteriorDesignerController), "HandleOnClose")]
    public static class Patch_InteriorDesignerController_HandleOnClose_GuestForward
    {
        static void Postfix()
        {
            try { if (HousingFurniture.LocalGuestHere() || HousingFurniture.LocalHelperHere()) InteriorSync.ForwardGuestInteriorEdit(HousingDesign.CurrentBuildingAddr()); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Housing] design close forward: {ex.Message}"); }
        }
    }
}
