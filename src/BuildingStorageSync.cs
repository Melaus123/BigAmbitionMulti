using System;
using BigAmbitions.Items;   // ItemInstance, CargoInstance
using Buildings;            // BuildingRegistration
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared HOME storage — a granted guest takes from / puts into an owner's home INTERIOR item cargo
    /// (the fridge; same ICargoHolder / cargoInstances model as a vehicle). Host-authoritative, mirroring
    /// VehicleStorageSync: the owner's machine is the sole authority on its own interior data
    /// (BuildingRegistration.itemInstances — ALWAYS present in the owner's save, whether or not the room is
    /// loaded), so the take/put commits only on the owner's grant (no optimistic local edit to roll back).
    ///
    /// Relay path: guest → host (resolves the building owner from the addressKey — clients don't keep a
    /// building→owner map) → owner (applies to reg + pushes that room's snapshot) → host → guest (places it).
    ///
    /// THREADING: OwnerApply() and OnResult() mutate game state and MUST run on the Unity main thread; the
    /// network dispatch marshals them (see MPServer/MPClient). See docs/PERMISSIONS-SYSTEM.md (Housing).
    /// </summary>
    public static class BuildingStorageSync
    {
        public const byte OpTake = 0;   // remove Amount of ItemName from the interior item (guest receives it)
        public const byte OpPut  = 1;   // add Amount of ItemName to the interior item (from the guest)

        // ── Guest side: start a take / put ───────────────────────────────────────
        public static void RequestTake(string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
            => Send(OpTake, addressKey, itemId, itemName, amount, paid, price, ctx);

        public static void RequestPut(string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
            => Send(OpPut, addressKey, itemId, itemName, amount, paid, price, ctx);

        private static void Send(byte op, string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
        {
            if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(itemName) || amount <= 0)
                return;
            var req = new BuildingCargoReqPayload
            {
                AddressKey = addressKey, ItemId = itemId, PlayerId = MPConfig.PlayerId,
                Op = op, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price, Ctx = ctx,
            };
            if (MPServer.IsRunning) MPServer.HandleBuildingCargoReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingCargoReq, MPConfig.PlayerId, req));
        }

        // ── Owner side: apply to the REAL interior cargo (runs on whoever owns the building) ── MAIN THREAD.
        public static BuildingCargoResPayload OwnerApply(BuildingCargoReqPayload req)
        {
            var res = new BuildingCargoResPayload
            {
                AddressKey = req.AddressKey, ItemId = req.ItemId, PlayerId = req.PlayerId, Op = req.Op,
                ItemName = req.ItemName, Amount = req.Amount, Paid = req.Paid, PricePerUnit = req.PricePerUnit,
                Ctx = req.Ctx, Ok = false, Reason = "gone",
            };
            try
            {
                // Grant backstop (the host already gated; re-verify on the authoritative machine).
                if (req.PlayerId != MPConfig.PlayerId && !GrantSync.IsGranted(GrantKind.Housing, MPConfig.PlayerId, req.PlayerId))
                { res.Reason = "denied"; return res; }

                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return res;
                BuildingRegistration? reg = null;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == req.AddressKey) { reg = r; break; }
                if (reg == null || reg.itemInstances == null) return res;

                ItemInstance? item = null;
                foreach (var kv in reg.itemInstances)
                    if (kv.Value != null && (kv.Value.id?.ToString() ?? "") == req.ItemId) { item = kv.Value; break; }
                if (item == null) return res;

                if (req.Op == OpTake)
                {
                    var src = item.cargoInstances;
                    if (src != null)
                        for (int c = 0; c < src.Count; c++)
                        {
                            var ci = src[c];
                            if (ci == null || ci.IsSealed) continue;
                            if (ci.itemName != req.ItemName) continue;
                            if (ci.amount < req.Amount) continue;
                            res.Paid = ci.paid; res.PricePerUnit = ci.pricePerUnit;
                            item.ReduceFromCargo(ci, req.Amount);
                            res.Ok = true; res.Reason = "";
                            break;
                        }
                }
                else // OpPut
                {
                    var ci = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                    if (item.TryToAddToCargo(ci)) { res.Ok = true; res.Reason = ""; }
                    else res.Reason = "full";
                }

                if (res.Ok)
                {
                    InteriorSync.PushOwnedBuildingNow(req.AddressKey);   // re-sync the interior to everyone inside, now
                    Plugin.Logger.LogInfo($"[BStore] owner applied {(req.Op == OpTake ? "TAKE" : "PUT")} {req.Amount}×{req.ItemName} on '{req.AddressKey}'/{req.ItemId} for '{req.PlayerId}'.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] OwnerApply: {ex.Message}"); res.Ok = false; res.Reason = "error"; }
            return res;
        }

        // ── Guest side: the owner's verdict came back ── MAIN THREAD.
        public static void OnResult(BuildingCargoResPayload res)
        {
            try
            {
                if (res.Op == OpTake)
                {
                    if (res.Ctx == "consume")
                    {
                        // Eaten in place at click time (round-17 parity) — nothing to deliver. A failed
                        // confirm is the phantom-bite race: fridge unchanged, nothing lost; log only.
                        if (!res.Ok) Plugin.Logger.LogInfo($"[BStore] consume confirm failed ({res.Reason}) — nothing removed, nothing delivered.");
                        return;
                    }
                    if (!res.Ok)
                    {
                        PassengerHud.Toast(res.Reason == "full" ? "No room." : res.Reason == "denied" ? "No access." : "Already taken.");
                        return;
                    }
                    var ci = new CargoInstance(res.ItemName, res.Amount, res.PricePerUnit, res.Paid);
                    if (PlayerHelper.ItemInstanceInHands == null)
                        PlayerHelper.ItemInstanceInHands = ItemHelper.InitializeItemInHandsWithCargo(ci);
                    else
                    {
                        // No empty hands (race after the request) — give it back so the owner's fridge is made whole.
                        RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit);
                        PassengerHud.Toast("No room to carry that.");
                    }
                }
                else // OpPut
                {
                    if (!res.Ok) { PassengerHud.Toast(res.Reason == "full" ? "Storage full." : "Couldn't store."); return; }
                    // Stored OK → consume the deposited item from wherever it came from, and ONLY now:
                    // hands (held directly or as box content) → pushed hand-vehicle → worn accessory (Ctx).
                    // The worn case is Ctx-tagged rather than name-inferred so it can never be confused with
                    // a truck stack of the same item (round-12 A).
                    if (res.Ctx == "wornHead" || res.Ctx == "wornHand") { UnequipWornAfterStore(res); return; }
                    var held = PlayerHelper.ItemInstanceInHands;
                    if (held == null) { RemoveFromHandVehicleLocal(res); return; }   // truck-sourced put (round-12 B)
                    if (held.itemName == res.ItemName) { PlayerHelper.ItemInstanceInHands = null; return; }
                    var contents = held.cargoInstances;
                    if (contents != null)
                    {
                        for (int i = 0; i < contents.Count; i++)
                            if (contents[i] != null && contents[i].itemName == res.ItemName) { contents.RemoveAt(i); break; }
                        if (contents.Count == 0) PlayerHelper.ItemInstanceInHands = null;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] OnResult: {ex.Message}"); }
        }

        // The owner confirmed a PUT sourced from the guest's pushed hand-truck/flatbed — remove that stack
        // from the truck now (native chokepoints → box visuals update; it's the guest's OWN vehicle, so the
        // proxy guard passes it). Mirrors VehicleStorageSync.RemoveFromHandVehicle. (round-12 B)
        private static void RemoveFromHandVehicleLocal(BuildingCargoResPayload res)
        {
            try
            {
                var cur = VehicleHelper.GetCurrentVehicle();
                if (cur == null || cur.VehicleType == null || !cur.VehicleType.spawnInPlayerObject) return;
                var src = cur.cargoInstances;
                if (src == null) return;
                for (int i = 0; i < src.Count; i++)
                {
                    var ci = src[i];
                    if (ci == null || ci.IsSealed || ci.itemName != res.ItemName) continue;
                    if (ci.amount <= res.Amount) cur.RemoveFromCargo(ci);
                    else                         cur.ReduceFromCargo(ci, res.Amount);
                    return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] truck-consume: {ex.Message}"); }
        }

        // The owner confirmed a PUT of the guest's WORN accessory (hat/hand item → wardrobe/coat rack) —
        // unequip it now, and only now (unequipping on send would vanish the item if the holder was full).
        // Mirrors the native StorePlayerWornItemIntoItemHolder tail (UnEquipAccessory). (round-12 A)
        private static void UnequipWornAfterStore(BuildingCargoResPayload res)
        {
            try
            {
                var acc = SaveGameManager.Current?.accessoriesData;
                var ci = res.Ctx == "wornHead" ? acc?.headAccessoryCargoInstance : acc?.handAccessoryCargoInstance;
                if (ci == null || ci.itemName != res.ItemName) return;   // changed in the request→confirm window — leave it
                PlayerHelper.PlayerController.UnEquipAccessory(ci);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] worn-consume: {ex.Message}"); }
        }
    }
}
