using System;
using HarmonyLib;
using Controllers;         // PlayerItemPurchaser (borrowed-car shopping scope)
using Helpers;             // PlayerHelper, VehicleHelper
using BigAmbitions.Items;  // CargoInstance (chokepoint mirror signatures)

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

        /// <summary>Option A (user-approved 2026-07-07): the proxy hand cart the LOCAL player is actively
        /// PUSHING. ActiveVehicleId == the proxy id IS the native possession contract (EnterVehicle sets it,
        /// ExitVehicle clears it), so this is true exactly while the borrower pushes the cart. For THIS cart
        /// the chokepoints below switch from BLOCK to ALLOW + MIRROR: the native mutation runs on the replica
        /// (instant, native toasts/sounds/capacity) and is replayed on the owner's real cart, so the next
        /// re-sync confirms instead of reverting. Parked proxies and car trunks keep the hard block.</summary>
        internal static bool IsPossessedPushed(VehicleInstance inst)
        {
            try
            {
                if (!IsProxy(inst)) return false;
                var vt = inst.VehicleType;
                if (vt == null || !vt.spawnInPlayerObject || vt.maxCargoCapacity <= 0) return false;
                if (SaveGameManager.Current != null && SaveGameManager.Current.ActiveVehicleId == inst.id) return true;
                // Grab-in-progress window: EnterVehicle stuffs a HELD item into the cart at :263-282 BEFORE
                // committing ActiveVehicleId at :291 — and early-returns (half-possession, the "looks pushed,
                // isn't" state) if that stuffing is refused. While the native grab of a hand-cart proxy is on
                // the stack, treat it as possessed so the stuff runs natively and mirrors.
                return EnterVehicleScope.Depth > 0;
            }
            catch { return false; }
        }
    }

    internal static class EnterVehicleScope
    {
        internal static int Depth;
    }

    [HarmonyPatch(typeof(VehicleController), nameof(VehicleController.EnterVehicle))]
    public static class Patch_VehicleController_EnterVehicle_BorrowedStuffScope
    {
        static void Prefix()    { EnterVehicleScope.Depth++; }
        static void Finalizer() { if (EnterVehicleScope.Depth > 0) EnterVehicleScope.Depth--; }
    }

    // Per-call capture for the mirror postfixes (Harmony __state).
    public struct CargoMirrorState
    {
        public bool   Active;
        public int    Amount;   // source amount BEFORE the native call (the call mutates it)
        public bool   Paid;
        public float  Price;
        public string Name;
    }

    // ADD chokepoint. Proxy: block (caller keeps the item in its source — nothing is deleted; the routed
    // deposit still works) — EXCEPT the pushed cart, where the native add runs and is mirrored to the owner.
    // Mirror accounting: on true the ENTIRE before-amount went in (merge zeroes the source, add-whole keeps
    // the object); on false any partial merges already absorbed (before − after) — mirror exactly that.
    [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.TryToAddToCargo))]
    public static class Patch_VehicleInstance_TryAdd_ProxyGuard
    {
        static bool Prefix(VehicleInstance __instance, CargoInstance cargoInstance, ref bool __result, out CargoMirrorState __state)
        {
            __state = default;
            if (!ProxyCargo.IsProxy(__instance)) return true;
            if (ProxyCargo.IsPossessedPushed(__instance) && cargoInstance != null)
            {
                __state = new CargoMirrorState { Active = true, Amount = cargoInstance.amount, Paid = cargoInstance.paid, Price = cargoInstance.pricePerUnit, Name = cargoInstance.itemName };
                return true;   // run the native add on the replica
            }
            __result = false;
            return false;
        }

        static void Postfix(VehicleInstance __instance, CargoInstance cargoInstance, bool __result, CargoMirrorState __state)
        {
            try
            {
                if (!__state.Active) return;
                int added = __result ? __state.Amount : __state.Amount - (cargoInstance?.amount ?? __state.Amount);
                if (added > 0) VehicleStorageSync.MirrorToOwner(VehicleStorageSync.OpPut, __instance, __state.Name, added, __state.Paid, __state.Price);
            }
            catch { }
        }
    }

    // REMOVE chokepoints. Proxy: block (owner's real cargo is the single source of truth) — EXCEPT the
    // pushed cart: native remove runs (unload into shelves/registers works natively) and mirrors as a TAKE.
    [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.RemoveFromCargo))]
    public static class Patch_VehicleInstance_Remove_ProxyGuard
    {
        static bool Prefix(VehicleInstance __instance, CargoInstance cargoInstance, out CargoMirrorState __state)
        {
            __state = default;
            if (!ProxyCargo.IsProxy(__instance)) return true;
            if (ProxyCargo.IsPossessedPushed(__instance) && cargoInstance != null)
            {
                // ReduceFromCargo calls this internally AFTER zeroing the stack — amount ≤ 0 there, so the
                // nested mirror self-suppresses (MirrorToOwner rejects amount ≤ 0). No double-count.
                __state = new CargoMirrorState { Active = true, Amount = cargoInstance.amount, Paid = cargoInstance.paid, Price = cargoInstance.pricePerUnit, Name = cargoInstance.itemName };
                return true;
            }
            return false;
        }

        static void Postfix(VehicleInstance __instance, CargoMirrorState __state)
        {
            try
            {
                if (!__state.Active || __state.Amount <= 0) return;
                VehicleStorageSync.MirrorToOwner(VehicleStorageSync.OpTake, __instance, __state.Name, __state.Amount, __state.Paid, __state.Price);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.ReduceFromCargo))]
    public static class Patch_VehicleInstance_Reduce_ProxyGuard
    {
        static bool Prefix(VehicleInstance __instance, CargoInstance cargoInstance, int amountToReduce, out CargoMirrorState __state)
        {
            __state = default;
            if (!ProxyCargo.IsProxy(__instance)) return true;
            if (ProxyCargo.IsPossessedPushed(__instance) && cargoInstance != null)
            {
                __state = new CargoMirrorState { Active = true, Amount = amountToReduce, Paid = cargoInstance.paid, Price = cargoInstance.pricePerUnit, Name = cargoInstance.itemName };
                return true;
            }
            return false;
        }

        static void Postfix(VehicleInstance __instance, CargoMirrorState __state)
        {
            try
            {
                if (!__state.Active || __state.Amount <= 0) return;
                VehicleStorageSync.MirrorToOwner(VehicleStorageSync.OpTake, __instance, __state.Name, __state.Amount, __state.Paid, __state.Price);
            }
            catch { }
        }
    }

    // MERGE chokepoint — the 4th mutation path, missed by the original three (round-12 #1c). The game's
    // hand-truck→car transfer (ICargoHolder.TryToMergeAndMoveCargoBetweenHolders) calls MergeIntoCargo
    // FIRST: on a proxy trunk holding a MATCHING stack it would merge into the replica, the source stack
    // hits 0 and is removed from the borrower's own hand-truck — then the next fleet re-sync overwrites
    // the replica and the items are GONE. Block stays for parked proxies / car trunks; the PUSHED cart
    // runs the native merge and mirrors the absorbed delta (before − after of the source) as a PUT.
    [HarmonyPatch(typeof(VehicleInstance), nameof(VehicleInstance.MergeIntoCargo))]
    public static class Patch_VehicleInstance_Merge_ProxyGuard
    {
        static bool Prefix(VehicleInstance __instance, CargoInstance cargoInstanceToMerge, out CargoMirrorState __state)
        {
            __state = default;
            if (!ProxyCargo.IsProxy(__instance)) return true;
            if (ProxyCargo.IsPossessedPushed(__instance) && cargoInstanceToMerge != null)
            {
                __state = new CargoMirrorState { Active = true, Amount = cargoInstanceToMerge.amount, Paid = cargoInstanceToMerge.paid, Price = cargoInstanceToMerge.pricePerUnit, Name = cargoInstanceToMerge.itemName };
                return true;
            }
            return false;
        }

        static void Postfix(VehicleInstance __instance, CargoInstance cargoInstanceToMerge, CargoMirrorState __state)
        {
            try
            {
                if (!__state.Active) return;
                int absorbed = __state.Amount - (cargoInstanceToMerge?.amount ?? __state.Amount);
                if (absorbed > 0) VehicleStorageSync.MirrorToOwner(VehicleStorageSync.OpPut, __instance, __state.Name, absorbed, __state.Paid, __state.Price);
            }
            catch { }
        }
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
                var cur = VehicleHelper.GetCurrentVehicle();
                if (!ProxyCargo.IsProxy(cur)) return;
                // Option A (2026-07-07): a PUSHED borrowed hand cart shops natively — picks go straight into
                // the cart (allow+mirror chokepoints) and checkout consolidates on the replica. The on-foot
                // veil stays only for borrowed CARS (trunk parked elsewhere; hands-first is the natural flow).
                if (ProxyCargo.IsPossessedPushed(cur)) return;
                __result = false;
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
        // Option A checkout mirror: when the borrower pays while PUSHING a borrowed cart, the native method
        // flips paid=true on the replica's stacks (the UI list holds the same object references) and
        // consolidates with DIRECT list ops that bypass every chokepoint. Snapshot the flip set in the
        // prefix and replay it on the owner's real cart as MARK-PAID mirrors — the flip is the one native
        // cargo mutation with no chokepoint to hook.
        static void Prefix(UI.Purchase.PurchaseUI __instance, out System.Collections.Generic.List<CargoMirrorState> __state)
        {
            BorrowedShopScope.Depth++;
            __state = null;
            try
            {
                var cur = VehicleHelper.GetCurrentVehicle();
                if (!ProxyCargo.IsPossessedPushed(cur)) return;
                var list = AccessTools.Field(typeof(UI.Purchase.PurchaseUI), "_cargoInstances")?.GetValue(__instance)
                           as System.Collections.Generic.IEnumerable<BigAmbitions.Items.CargoInstance>;
                if (list == null) return;
                foreach (var ci in list)
                    if (ci != null && !ci.paid && ci.amount > 0 && !string.IsNullOrEmpty(ci.itemName))
                        (__state ??= new System.Collections.Generic.List<CargoMirrorState>())
                            .Add(new CargoMirrorState { Active = true, Amount = ci.amount, Price = ci.pricePerUnit, Name = ci.itemName });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] mark-paid snapshot: {ex.Message}"); }
        }

        static void Postfix(System.Collections.Generic.List<CargoMirrorState> __state)
        {
            try
            {
                if (__state == null) return;
                var cur = VehicleHelper.GetCurrentVehicle();
                if (cur == null) return;
                foreach (var s in __state)
                    VehicleStorageSync.MirrorToOwner(VehicleStorageSync.OpMarkPaid, cur, s.Name, s.Amount, paid: true, price: s.Price);
            }
            catch { }
        }

        static void Finalizer() { if (BorrowedShopScope.Depth > 0) BorrowedShopScope.Depth--; }
    }

    // NATIVE ABANDONED-CART CLEANUP vs BORROWING (2026-07-07, user field knowledge verified): the game
    // auto-destroys a vehicle — INCLUDING its persistent save instance — once it has been out of the
    // LOCAL player's sight for vehicleType.autoDestroyAfterMinutes with no PAID cargo and not active
    // (AutoDestroyVehicle.ShouldDestroyVehicle; lastSeen starts when visibility ends). "In use" is a
    // purely local concept, so a cart actively pushed by a REMOTE borrower reads as abandoned on the
    // owner's machine and gets DELETED FROM THE SAVE mid-use (the CartTrace UNTRACKED event → fleet
    // drop → the borrower's cart annihilated). While remote-driven: not abandoned — refuse the destroy
    // and keep lastSeen fresh so the full native timer restarts only after the borrow ends. Native
    // cleanup semantics are otherwise untouched (SP and un-borrowed carts behave exactly as vanilla).
    [HarmonyPatch(typeof(AutoDestroyVehicle), nameof(AutoDestroyVehicle.ShouldDestroyVehicle))]
    public static class Patch_AutoDestroy_RemoteBorrowGuard
    {
        private const float AnyPlayerNearMeters = 30f;

        static void Postfix(VehicleInstance vehicleInstance, ref bool __result)
        {
            try
            {
                if (!__result || vehicleInstance == null) return;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;      // SP — vanilla behavior
                bool keep = VehicleManager.IsDrivenRemotely(vehicleInstance.id)   // in a borrower's hands
                            || AnySessionPlayerNear(vehicleInstance);             // user-approved 2026-07-07: "out of
                                                                                  // ALL players' sight" — proximity is
                                                                                  // the cross-machine stand-in for
                                                                                  // sight (broader = more protective)
                if (!keep) return;
                __result = false;
                try { vehicleInstance.lastSeen = TimeHelper.Now(); } catch { }   // full native timer restarts after everyone leaves
            }
            catch { }
        }

        private static bool AnySessionPlayerNear(VehicleInstance inst)
        {
            try
            {
                var pos = inst.position;
                var me = PlayerHelper.PlayerController?.Character?.transform;
                if (me != null && UnityEngine.Vector3.Distance(me.position, pos) < AnyPlayerNearMeters) return true;
                foreach (var t in RemotePlayerManager.GetRemotePlayerTransforms())
                    if (t != null && UnityEngine.Vector3.Distance(t.position, pos) < AnyPlayerNearMeters) return true;
            }
            catch { }
            return false;
        }
    }

    // OWNER-SIDE in-use gate (2026-07-07, run-9 dual possession): while a BORROWER is driving/pushing
    // this vehicle (the follow is active), the owner grabbing their own real one would create two
    // simultaneous possessions of one vehicle — the follow then drags the owner's in-hands copy toward
    // the borrower's pose. Mirror of the CanDriveGhost OwnerUsing gate, native voice kept minimal.
    [HarmonyPatch(typeof(VehicleController), nameof(VehicleController.DriveVehicle))]
    public static class Patch_VehicleController_Drive_InUseGate
    {
        static bool Prefix(VehicleController __instance)
        {
            try
            {
                var inst = __instance?.vehicleInstance;
                if (inst == null || string.IsNullOrEmpty(inst.id) || inst.id.StartsWith("BAMP_")) return true;   // proxies handled by CanDriveGhost
                if (!VehicleManager.IsDrivenRemotely(inst.id)) return true;
                PassengerHud.Toast("Someone is using this right now.");
                return false;
            }
            catch { return true; }
        }
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
