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
        public const byte OpTake     = 0;   // remove Amount of ItemName from the interior item (guest receives it)
        public const byte OpPut      = 1;   // add Amount of ItemName to the interior item (from the guest)
        public const byte OpSetStock = 2;   // round-32: set the item's STOCK type (display shelf / producer) — ItemName = new stock name ("" = clear)

        // ── Guest side: start a take / put ───────────────────────────────────────
        public static void RequestTake(string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
            => Send(OpTake, addressKey, itemId, itemName, amount, paid, price, ctx);

        public static void RequestPut(string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
            => Send(OpPut, addressKey, itemId, itemName, amount, paid, price, ctx);

        /// <summary>Round-32 (business helpers): ask the owner to change what a display/showcase item — or a
        /// producer (ctx="producerset") — stocks. The owner runs the same moves the native dropdown does.</summary>
        public static void RequestSetStock(string addressKey, string itemId, string newStockName, string ctx = "setstock")
            => Send(OpSetStock, addressKey, itemId, newStockName ?? "", 1, paid: false, price: 0f, ctx);

        private static void Send(byte op, string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
        {
            // SetStock legitimately carries an EMPTY ItemName ("clear the stock type" = the native
            // "undefined" dropdown choice); take/put never do.
            if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemId) || amount <= 0
                || (string.IsNullOrEmpty(itemName) && op != OpSetStock))
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
                // Housing OR Business (round-32): the gates only ever OFFER these ops in buildings the
                // requester holds the matching grant for, so kind-precision here buys nothing — either
                // key from this owner authorizes cargo ops on this owner's buildings.
                if (req.PlayerId != MPConfig.PlayerId
                    && !GrantSync.IsGranted(GrantKind.Housing, MPConfig.PlayerId, req.PlayerId)
                    && !GrantSync.IsGranted(GrantKind.Business, MPConfig.PlayerId, req.PlayerId))
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
                else if (req.Op == OpPut)
                {
                    // Producer refills may ONLY merge into the machine's single existing stock slot — a
                    // producer with two cargo instances breaks the game's GetStockInstance invariant.
                    if (req.Ctx == "producer")
                    {
                        var slot = (item.cargoInstances != null && item.cargoInstances.Count == 1) ? item.cargoInstances[0] : null;
                        if (slot == null) { res.Reason = "full"; return res; }
                        // Round-37f (user: EMPTY register refused as "full"): an UNSET slot (name cleared,
                        // amount 0) is a valid deposit target — the owner's own deposit onto an unset
                        // station names the slot exactly like this (Producer.Interact empty-name branch).
                        // Only a DIFFERENT ingredient occupying the slot is a real refusal.
                        if (string.IsNullOrEmpty(slot.itemName) && slot.amount == 0)
                        {
                            slot.itemName = req.ItemName;
                            slot.ResetItemCached();
                            Plugin.Logger.LogInfo($"[BStore] producer put named the unset slot '{req.ItemName}' on '{req.AddressKey}'/{req.ItemId} (owner-parity name-set).");
                        }
                        else if (slot.itemName != req.ItemName)
                        { res.Reason = "full"; return res; }   // a different ingredient is loaded — genuine refusal
                    }
                    var ci = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                    if (item.TryToAddToCargo(ci)) { res.Ok = true; res.Reason = ""; }
                    else
                    {
                        // Round-32 (decompile ItemInstance.cs:198-231): TryToAddToCargo PARTIALLY merges
                        // before returning false when the holder can't take the whole stack — without a
                        // rollback the absorbed part stays here while the guest keeps the full stack = DUP.
                        // Roll it back so "full" is all-or-nothing.
                        int absorbed = req.Amount - ci.amount;
                        if (absorbed > 0)
                        {
                            var src = item.cargoInstances;
                            if (src != null)
                                for (int c = src.Count - 1; c >= 0 && absorbed > 0; c--)
                                {
                                    var s = src[c];
                                    if (s == null || s.IsSealed || s.itemName != req.ItemName || s.paid != req.Paid) continue;
                                    int take = Math.Min(absorbed, s.amount);
                                    if (take >= s.amount) item.RemoveFromCargo(s); else item.ReduceFromCargo(s, take);
                                    absorbed -= take;
                                }
                            Plugin.Logger.LogInfo($"[BStore] put of {req.Amount}×{req.ItemName} didn't fully fit '{req.AddressKey}'/{req.ItemId} — partial merge rolled back.");
                        }
                        res.Reason = "full";
                    }
                }
                else if (req.Op == OpSetStock)
                {
                    res.Ok = ApplySetStock(reg, item, req, out var reason);
                    res.Reason = reason;
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

        /// <summary>Round-32, OWNER side: change what an item stocks — the native dropdown's moves minus its
        /// UI (ItemController.OnStockOptionSelected, decompile :918-976): return the old stock to a shelf,
        /// set the new name, refill from storage, refresh the business numbers. ctx="producerset" is the
        /// bare variant for PRODUCERS (ingredient name only — no shelf return, no auto-fill: the native
        /// producer flow never does those, and a producer's single cargo slot must stay single).</summary>
        private static bool ApplySetStock(BuildingRegistration reg, ItemInstance item, BuildingCargoReqPayload req, out string reason)
        {
            reason = "";
            var cargo = item.cargoInstances;
            if (cargo == null || cargo.Count != 1) { reason = "gone"; return false; }   // stock carriers hold exactly one stock instance
            var stock = cargo[0];
            string newName = req.ItemName ?? "";
            if (stock.itemName == newName) return true;   // idempotent (duplicate click / re-send)

            // Owner-side audit line (round 35): SetStock quietly runs the native stocking moves (return old
            // stock to a shelf, auto-FILL the new stock from boxes/containers — which DISCARDS containers it
            // empties, native behavior). Without this line those moves are unattributable ("items vanished").
            // Helpers keep FULL owner parity including clear (user 2026-07-04) — attribution, not restriction.
            Plugin.Logger.LogInfo($"[BStore] setstock by '{req.PlayerId}' on '{req.AddressKey}'/{req.ItemId}: '{stock.itemName}'x{stock.amount} → '{newName}' (fill may drain stock containers; empties are discarded natively).");

            if (req.Ctx == "producerset")
            {
                if (stock.amount > 0 && !string.IsNullOrEmpty(stock.itemName)) { reason = "occupied"; return false; }
                stock.itemName = newName;
                stock.ResetItemCached();
                return true;
            }

            if (stock.amount > 0 && !string.IsNullOrEmpty(stock.itemName))
            {
                var old = new CargoInstance(stock.itemName, stock.amount, stock.pricePerUnit);
                if (!old.ReturnToAShelf(item.AddressCached, item))
                { stock.amount = old.amount; reason = "full"; return false; }   // native: "no storage available"
                stock.amount = 0;
            }
            stock.itemName = newName;
            stock.ResetItemCached();
            if (!string.IsNullOrEmpty(newName))
                try { item.FillUpShowcaseShelfOrPointOfSale(); } catch { }
            // The native tail's business refreshers — each independently non-critical.
            try { BusinessHelper.UpdateCustomerCapacity(reg); } catch { }
            try { if (reg.HasValidAddress) { BusinessHelper.UpdatePromotion(reg); reg.UpdateSecurityLevel(); } } catch { }
            try { GlobalEvents.onBuildingRegistrationChange?.Invoke(reg.Address); } catch { }
            return true;
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
                else if (res.Op == OpSetStock)
                {
                    // Success needs no local action — the owner's interior push re-renders the shelf.
                    if (!res.Ok)
                        PassengerHud.Toast(res.Reason == "full" ? "No storage room for the current stock."
                                         : res.Reason == "occupied" ? "That machine is already loaded."
                                         : res.Reason == "denied" ? "No access." : "Couldn't change the stock.");
                }
                else // OpPut
                {
                    if (!res.Ok)
                    {
                        PassengerHud.Toast(res.Reason == "full" ? "Storage full." : "Couldn't store.");
                        // Round-37b: our replica said the cargo FITS (the gates only offer puts that fit) yet
                        // the owner says FULL — proof the replica diverged (e.g. an unrouted local mutation
                        // stuck via the keep-live apply optimization). Force a full re-pull: drop the
                        // byte-diff baseline so the next apply replaces every live item, then re-request.
                        if (res.Reason == "full" && MPClient.IsConnected && !MPServer.IsRunning)
                        {
                            GameStatePatcher.ForgetInteriorBaseline(res.AddressKey);
                            MPClient.SendInteriorRequest(res.AddressKey);
                            Plugin.Logger.LogInfo($"[BStore] put-full vs replica-fits mismatch on '{res.AddressKey}' — forced interior re-pull (divergence heal).");
                        }
                        return;
                    }
                    // Round-32: producer refills are AMOUNT-CLAMPED (partial stacks) — reduce exactly
                    // res.Amount from the source stack instead of the whole-stack consume below.
                    if (res.Ctx == "producer") { ReducePutSourceByAmount(res); return; }
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

        // Round-32: amount-aware put consume for producer refills — reduce exactly res.Amount of
        // res.ItemName from the helper's source (held box contents, held single, or hand-vehicle).
        private static void ReducePutSourceByAmount(BuildingCargoResPayload res)
        {
            try
            {
                int remaining = res.Amount;
                var held = PlayerHelper.ItemInstanceInHands;
                var contents = held?.cargoInstances;
                if (contents != null && contents.Count > 0)
                {
                    for (int i = contents.Count - 1; i >= 0 && remaining > 0; i--)
                    {
                        var c = contents[i];
                        if (c == null || c.IsSealed || c.itemName != res.ItemName) continue;
                        int take = Math.Min(remaining, c.amount);
                        if (take >= c.amount) contents.RemoveAt(i); else c.amount -= take;
                        remaining -= take;
                    }
                    if (contents.Count == 0) PlayerHelper.ItemInstanceInHands = null;
                }
                else if (held != null && held.itemName == res.ItemName)
                {
                    PlayerHelper.ItemInstanceInHands = null;   // held single unit
                    remaining = 0;
                }
                if (remaining > 0)
                {
                    var cur = VehicleHelper.GetCurrentVehicle();
                    var src = (cur != null && cur.VehicleType != null && cur.VehicleType.spawnInPlayerObject) ? cur.cargoInstances : null;
                    if (src != null)
                        for (int i = src.Count - 1; i >= 0 && remaining > 0; i--)
                        {
                            var c = src[i];
                            if (c == null || c.IsSealed || c.itemName != res.ItemName) continue;
                            int take = Math.Min(remaining, c.amount);
                            if (take >= c.amount) cur.RemoveFromCargo(c); else cur.ReduceFromCargo(c, take);
                            remaining -= take;
                        }
                }
                if (remaining > 0) Plugin.Logger.LogWarning($"[BStore] producer consume: {remaining}×{res.ItemName} not found locally (source changed mid-request).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] producer consume: {ex.Message}"); }
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
