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
        public const byte OpSetStock = 2;   // round-32: set the item's STOCK type (display shelf / producer) — ItemName = new stock name ("" = clear); round-49 ctx "signset" = a SIGN's linkedItemName instead

        // ── Guest side: start a take / put ───────────────────────────────────────
        public static void RequestTake(string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
            => Send(OpTake, addressKey, itemId, itemName, amount, paid, price, ctx);

        public static void RequestPut(string addressKey, string itemId, string itemName, int amount, bool paid, float price, string ctx = "")
            => Send(OpPut, addressKey, itemId, itemName, amount, paid, price, ctx);

        /// <summary>Round-47b (full sell/discard parity, user 2026-07-07): a helper sells or discards a
        /// whole stack row. The REMOVAL routes to the owner (stock truth); on a sell, the MONEY credits
        /// the HELPER's own wallet locally on confirm — native "whoever sells pockets it" semantics; the
        /// grant is trust-scoped and Transfers exist for gifting it back (user's design).</summary>
        public static void RequestStackOp(string addressKey, string itemId, string itemName, int amount, bool paid, float price, int count, bool sell)
        {
            if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(itemName) || count <= 0) return;
            var req = new BuildingCargoReqPayload
            {
                AddressKey = addressKey, ItemId = itemId, PlayerId = MPConfig.PlayerId,
                Op = OpTake, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price,
                Count = count, Ctx = sell ? "stacksell" : "stackdiscard",
            };
            if (MPServer.IsRunning) MPServer.HandleBuildingCargoReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingCargoReq, MPConfig.PlayerId, req));
        }

        /// <summary>Round-47: put a SEALED BOX back (hands-full race after a boxtake) — nested contents
        /// travel so the give-back doesn't strip the box.</summary>
        public static void RequestPutBox(string addressKey, string itemId, string itemName, int amount, bool paid, float price, List<CargoNestedInfo> nested)
        {
            if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemId) || amount <= 0 || string.IsNullOrEmpty(itemName)) return;
            var req = new BuildingCargoReqPayload
            {
                AddressKey = addressKey, ItemId = itemId, PlayerId = MPConfig.PlayerId,
                Op = OpPut, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price, Ctx = "boxreturn",
            };
            if (nested != null) req.Nested.AddRange(nested);
            if (MPServer.IsRunning) MPServer.HandleBuildingCargoReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingCargoReq, MPConfig.PlayerId, req));
        }

        /// <summary>Round-32 (business helpers): ask the owner to change what a display/showcase item — or a
        /// producer (ctx="producerset") — stocks. The owner runs the same moves the native dropdown does.</summary>
        public static void RequestSetStock(string addressKey, string itemId, string newStockName, string ctx = "setstock")
            => Send(OpSetStock, addressKey, itemId, newStockName ?? "", 1, paid: false, price: 0f, ctx);

        // ── Round-49 slice 2: helper PLACE from a delivery spot. The reduce routes to the owner first;
        // placement starts only on the Ok verdict, so the cargo details the wire doesn't echo (nested
        // contents, custom colors) are captured here at click time. Single-slot by design: clicks and
        // verdicts are both main-thread, and one place can be in flight at a time. ──
        private static CargoInstance? _pendingPlace;

        public static void SetPendingPlace(string addressKey, string itemId, CargoInstance source)
        {
            try
            {
                var pc = source.Copy();   // name/price/paid/colors (colors deep-copied by the game's Copy)
                pc.amount = 1;            // native place consumes exactly one unit
                if (source.nestedCargoInstances != null)
                    foreach (var n in source.nestedCargoInstances)
                        if (n != null)
                            pc.nestedCargoInstances.Add(new NestedCargoInstance(n.itemName, n.amount, n.pricePerUnit,
                                n.customColors == null ? null : new List<CustomColor>(n.customColors)));
                _pendingPlace = pc;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] SetPendingPlace: {ex.Message}"); _pendingPlace = null; }
        }

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
                    // Round-47 (slice 2b) — a SEALED BOX taken from a storage shelf through the manage
                    // panel. The regular take loop SKIPS sealed instances; this branch takes the whole
                    // box (first name+amount+paid match — identical boxes are fungible; contents are
                    // owner truth) and echoes its NESTED contents so the guest's in-hands box is exact.
                    // Round-47b — helper SELL/DISCARD: remove up to Count identical non-sealed stack
                    // instances (name+amount+paid identity — grouped rows are fungible). Money is the
                    // REQUESTER's side (credited there on this Ok); the owner only loses the stock,
                    // exactly as if the helper were standing at the shelf natively.
                    if (req.Ctx == "stacksell" || req.Ctx == "stackdiscard")
                    {
                        var ssrc = item.cargoInstances;
                        int removed = 0;
                        if (ssrc != null)
                            for (int c = ssrc.Count - 1; c >= 0 && removed < req.Count; c--)
                            {
                                var ci = ssrc[c];
                                if (ci == null || ci.IsSealed) continue;
                                if (ci.itemName != req.ItemName || ci.amount != req.Amount || ci.paid != req.Paid) continue;
                                item.RemoveFromCargo(ci);
                                removed++;
                            }
                        if (removed > 0) { res.Ok = true; res.Reason = ""; res.Count = removed; }
                        else res.Reason = "gone";
                    }
                    else if (req.Ctx == "boxtake")
                    {
                        var bsrc = item.cargoInstances;
                        if (bsrc != null)
                            for (int c = 0; c < bsrc.Count; c++)
                            {
                                var ci = bsrc[c];
                                if (ci == null || ci.itemName != req.ItemName) continue;
                                if (ci.amount != req.Amount || ci.paid != req.Paid) continue;
                                res.Paid = ci.paid; res.PricePerUnit = ci.pricePerUnit; res.Amount = ci.amount;
                                if (ci.nestedCargoInstances != null)
                                    foreach (var n in ci.nestedCargoInstances)
                                        if (n != null) res.Nested.Add(new CargoNestedInfo { ItemName = n.itemName ?? "", Amount = n.amount, PricePerUnit = n.pricePerUnit });
                                item.RemoveFromCargo(ci);
                                res.Ok = true; res.Reason = "";
                                break;
                            }
                    }
                    else
                    // Round-38e — "REMOVE CONTENT" routed for helpers: mirror of the native
                    // ItemController.RemoveStockInContent (:1091-1152) the owner's button runs — take the
                    // ENTIRE stock (owner truth, not the requester's replica amount), clear the emptied
                    // slot's NAME like native does (:1123/:1138), fire the cargo callback, run the native
                    // tail refreshers. Echoes the real amount/paid/price so the delivered box is faithful.
                    if (req.Ctx == "stationtake")
                    {
                        var slot = (item.cargoInstances != null && item.cargoInstances.Count == 1) ? item.cargoInstances[0] : null;
                        if (slot != null && !slot.IsSealed && !string.IsNullOrEmpty(slot.itemName)
                            && slot.itemName == req.ItemName && slot.amount > 0)
                        {
                            res.Amount = slot.amount; res.Paid = slot.paid; res.PricePerUnit = slot.pricePerUnit;
                            slot.amount = 0;
                            slot.itemName = null;
                            slot.ResetItemCached();
                            try { item.OnItemsInCargoUpdated()?.Invoke(); } catch { }
                            try { BusinessHelper.UpdateCustomerCapacity(reg); } catch { }
                            try { GlobalEvents.onBuildingRegistrationChange?.Invoke(reg.Address); } catch { }
                            res.Ok = true; res.Reason = "";
                        }
                        // else: slot gone/renamed/empty — res stays !Ok ("gone"); requester's replica was stale.
                    }
                    else
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
                }
                else if (req.Op == OpPut)
                {
                    // Producer refills may ONLY merge into the machine's single existing stock slot — a
                    // producer with two cargo instances breaks the game's GetStockInstance invariant.
                    // "stationreturn" = a routed station-take's give-back (requester's hands turned out
                    // full) — same single-slot merge, but the requester consumes NOTHING locally on success.
                    if (req.Ctx == "producer" || req.Ctx == "stationreturn")
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
                        // Round-38 (field-proven: five 1000×bag puts refused "full" on a 0/1000 register):
                        // BOTH native merge primitives gate on paid EQUALITY (MergeCargo :270,
                        // TryToAddToCargo :202), and a stock station's cargoCapacity is 1 — a mismatch is an
                        // instant all-or-nothing refusal. An EMPTY slot's paid/price are dead leftovers from
                        // whenever it was last stocked; the owner's own first deposit never merges against
                        // them (TryToAddToCargo APPENDS the incoming stack wholesale onto an empty list,
                        // stamping the slot with the stack's own flags). Adopt them the same way.
                        if (slot.amount == 0 && (slot.paid != req.Paid || slot.pricePerUnit != req.PricePerUnit))
                        {
                            slot.paid = req.Paid; slot.pricePerUnit = req.PricePerUnit;
                            Plugin.Logger.LogInfo($"[BStore] producer put adopted paid={req.Paid}/price={req.PricePerUnit:F2} onto the empty slot on '{req.AddressKey}'/{req.ItemId} (owner-parity stamp).");
                        }

                        // Round-38c: merge via the game's OWN station primitive. The owner's working deposit
                        // goes TryToMergeAndMoveCargoBetweenHolders → MergeIntoCargo → MergeCargo, which gates
                        // only on name/paid/sealed/nested + clamps to GetMaxStockCapacity. TryToAddToCargo
                        // (round-32's pick) additionally hard-gates on the holder's cargoCapacity and
                        // itemsWhitelist — general-cargo-holder rules a stock station's data doesn't satisfy
                        // (probe-confirmed 2026-07-07: the cash register's cargoCapacity is 0, whitelist empty),
                        // which refused every helper deposit the owner's own path would have landed. Partial
                        // fills are native semantics (excess stays with the guest), so no rollback needed:
                        // res.Amount echoes what LANDED and the guest consumes exactly that.
                        var inc = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                        int before = slot.amount;
                        item.MergeCargo(inc, slot, req.Amount);
                        int landed = slot.amount - before;
                        if (landed > 0) { res.Ok = true; res.Reason = ""; res.Amount = landed; }
                        else res.Reason = (slot.amount > 0 && slot.paid != req.Paid) ? "mixed" : "full";
                    }
                    else
                    {
                        var ci = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                        // Round-47: a returned sealed box keeps its contents ("boxreturn" give-backs).
                        if (req.Nested != null && req.Nested.Count > 0)
                            foreach (var n in req.Nested)
                                if (n != null) ci.nestedCargoInstances.Add(new NestedCargoInstance(n.ItemName, n.Amount, n.PricePerUnit, null));
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
                }
                else if (req.Op == OpSetStock)
                {
                    res.Ok = ApplySetStock(reg, item, req, out var reason);
                    res.Reason = reason;
                }

                if (res.Ok)
                {
                    InteriorSync.PushOwnedBuildingNow(req.AddressKey);   // re-sync the interior to everyone inside, now
                    OwnerBusinessTail(reg);   // round-39c: the business must RECOGNIZE the change (see below)
                    // Round-38: setstock used to log as "PUT 1×<name>" (its wire Amount is a hardcoded 1) —
                    // which read as a landed 1-unit deposit and derailed a log read. Name the op truthfully.
                    string opName = req.Op == OpTake ? "TAKE" : req.Op == OpPut ? "PUT" : "SETSTOCK";
                    string what   = req.Op == OpSetStock ? $"'{req.ItemName}'" : $"{req.Amount}×{req.ItemName}";
                    Plugin.Logger.LogInfo($"[BStore] owner applied {opName} {what} on '{req.AddressKey}'/{req.ItemId} for '{req.PlayerId}'.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] OwnerApply: {ex.Message}"); res.Ok = false; res.Reason = "error"; }
            return res;
        }

        /// <summary>Round-39c — make the business RECOGNIZE a routed cargo change (user: guest-stocked
        /// shelf items "never recognized as product being sold"). The native recognition pipeline is
        /// BuildingManager.OnItemChanged → ScheduleUpdateAvailableProducers (re-derives cachedAvailable-
        /// Products, which the customer sim, BizMan inventory, and retail-price retention all read) —
        /// but it only fires for the OWNER'S OWN local actions; a routed put mutated reg data and
        /// nothing re-evaluated, even with the owner standing in the shop. When the owner is INSIDE the
        /// building: run the full native tail (needs the loaded shelves). When elsewhere: run the
        /// data-level refreshers; the products re-derive natively when the owner next enters (building
        /// load rebuilds the item-controller scan).</summary>
        internal static void OwnerBusinessTail(BuildingRegistration reg)
        {
            try
            {
                if (reg == null) return;
                // Round-39d: the shopper schedule keys off products/schedule — refresh it with every
                // routed stock change (the native owner-stock flow calls this too, Producer.Interact
                // :154). Data-level, works owner-anywhere. Feeds the CustomerEntries snapshot ship-out.
                try { AI.Customers.CustomerEntries.CustomerEntriesHelper.UpdateCustomerEntriesForPlayerBusiness(reg, TimeHelper.GetDayOfWeek()); } catch { }
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm != null && bm.buildingRegistration == reg)
                {
                    bm.OnItemChanged(forced: true);   // avail products + capacity + promotion + change event + workstations
                    return;
                }
                try { BusinessHelper.UpdateCustomerCapacity(reg); } catch { }
                try { if (reg.HasValidAddress) BusinessHelper.UpdatePromotion(reg); } catch { }
                try { GlobalEvents.onBuildingRegistrationChange?.Invoke(reg.Address); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] OwnerBusinessTail: {ex.Message}"); }
        }

        /// <summary>Round-32, OWNER side: change what an item stocks — the native dropdown's moves minus its
        /// UI (ItemController.OnStockOptionSelected, decompile :918-976): return the old stock to a shelf,
        /// set the new name, refill from storage, refresh the business numbers. ctx="producerset" is the
        /// bare variant for PRODUCERS (ingredient name only — no shelf return, no auto-fill: the native
        /// producer flow never does those, and a producer's single cargo slot must stay single).</summary>
        /// <remarks>See also OwnerBusinessTail below — the shared recognition tail runs on every Ok.</remarks>
        private static bool ApplySetStock(BuildingRegistration reg, ItemInstance item, BuildingCargoReqPayload req, out string reason)
        {
            reason = "";
            // Round-49 slice 5 — a SIGN's dropdown pick: linkedItemName lives on the ItemInstance itself
            // (signs have no cargo slots — the checks below would refuse one as "gone"). The shared Ok tail
            // fires onBuildingRegistrationChange — exactly the event SignController re-reads — and the
            // interior push carries linkedItemName to every client (it's in the snapshot hash + apply).
            if (req.Ctx == "signset")
            {
                string linkName = req.ItemName ?? "";
                if ((item.linkedItemName ?? "") == linkName) return true;   // idempotent (duplicate click / re-send)
                Plugin.Logger.LogInfo($"[BStore] signset by '{req.PlayerId}' on '{req.AddressKey}'/{req.ItemId}: '{item.linkedItemName}' → '{linkName}'.");
                item.linkedItemName = linkName;
                return true;
            }
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
                    // Round-47b — sell/discard verdicts: nothing arrives in hands. A SELL credits the
                    // helper's own wallet for exactly what the owner removed (native seller-pockets-it
                    // semantics; user's trust-scoped design — Transfers exist for gifting it back).
                    if (res.Ctx == "stacksell" || res.Ctx == "stackdiscard")
                    {
                        if (!res.Ok) { PassengerHud.Toast("Already gone."); return; }
                        if (res.Ctx == "stacksell")
                        {
                            try
                            {
                                // No custom toast — ChangeMoneySafe fires the game's own transaction
                                // feedback; doubling it broke parity (user 2026-07-07).
                                var priced = new CargoInstance(res.ItemName, res.Amount, res.PricePerUnit, res.Paid);
                                float total = priced.GetSellingPrice() * res.Count;
                                var data = new System.Collections.Generic.Dictionary<string, string> { { "itemName", res.ItemName } };
                                GameManager.ChangeMoneySafe(total, new TransactionInfo("ba:transaction_itemsold", data));
                                Plugin.Logger.LogInfo($"[Business] helper stack sell confirmed: {res.Count}×({res.ItemName}×{res.Amount}) → ${total:F2} credited locally.");
                            }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] sell credit: {ex.Message}"); }
                        }
                        return;
                    }
                    if (res.Ctx == "consume")
                    {
                        // Eaten in place at click time (round-17 parity) — nothing to deliver. A failed
                        // confirm is the phantom-bite race: fridge unchanged, nothing lost; log only.
                        if (!res.Ok) Plugin.Logger.LogInfo($"[BStore] consume confirm failed ({res.Reason}) — nothing removed, nothing delivered.");
                        return;
                    }
                    if (res.Ctx == "placereduce")
                    {
                        // Round-49 slice 2: the owner reduced one furniture unit off the delivery spot —
                        // start the native placement from the click-time captured cargo. Any local failure
                        // gives the unit back so the owner's holder is made whole.
                        var pc = _pendingPlace; _pendingPlace = null;
                        if (!res.Ok) { PassengerHud.Toast("Already gone."); return; }
                        if (pc == null || pc.itemName != res.ItemName)
                        {
                            RequestPut(res.AddressKey, res.ItemId, res.ItemName, 1, res.Paid, res.PricePerUnit);
                            Plugin.Logger.LogWarning("[BStore] placereduce Ok without a matching pending place — unit returned to the owner.");
                            return;
                        }
                        bool started = false;
                        try
                        {
                            var inst = pc.InitializeNewInstance();
                            pc.ParseIntoItemInstance(inst);
                            // The native place flow's own entry (private static): creates the controller,
                            // enters placement mode, adds the instance to the (replica) registration —
                            // completion then forwards through the guest interior-edit flow.
                            var m = HarmonyLib.AccessTools.Method(typeof(UI.ItemPanel.ItemPanelUI), "TryToStartPlacingItem");
                            started = m != null && (bool)m.Invoke(null, new object[] { pc.ItemCached, pc.itemName, inst });
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] place start: {ex.Message}"); }
                        if (!started)
                        {
                            RequestPut(res.AddressKey, res.ItemId, res.ItemName, 1, res.Paid, res.PricePerUnit);
                            PassengerHud.Toast("Couldn't start placing that — it was put back.");
                        }
                        else Plugin.Logger.LogInfo($"[BStore] helper placement started: {pc.itemName} (unit reduced owner-side; completion forwards the interior).");
                        return;
                    }
                    if (res.Ctx == "vehicletake")
                    {
                        // Round-49 slice 4: the owner's storage lost the packed hand truck/flatbed — spawn
                        // it HERE as the HELPER'S OWN vehicle (user ruling 2026-07-21: valueless + freely
                        // spawnable, taker keeps it) with the same native call the owner's unpack runs
                        // (CargoItemUi.ClickItem :389); the regular local-vehicle sync picks it up.
                        if (!res.Ok) { PassengerHud.Toast("Already gone."); return; }
                        string vt = "";
                        try { vt = ItemsGetter.GetByName(res.ItemName)?.vehicleType ?? ""; } catch { }
                        if (string.IsNullOrEmpty(vt))
                        {
                            RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit);
                            Plugin.Logger.LogWarning($"[BStore] vehicletake Ok but '{res.ItemName}' has no vehicleType — returned to the owner.");
                            return;
                        }
                        if (PlayerHelper.IsHoldingItem || PlayerHelper.IsUsingVehicle)
                        {
                            // Hands filled during the round-trip — give it back rather than strand it.
                            RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit);
                            PassengerHud.Toast("No room to unpack that now.");
                            return;
                        }
                        try
                        {
                            var vc = VehicleSpawnerController.CreateVehicle(InstanceBehavior<GameManager>.Instance.playerController.transform, vt);
                            if (vc != null) vc.EnterVehicle();
                            try { GameEvent.Invoke("ba:gameevent_itemcargochanged"); } catch { }
                            Plugin.Logger.LogInfo($"[BStore] helper unpacked stored vehicle '{res.ItemName}' → local spawn, helper-owned.");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger.LogWarning($"[BStore] vehicle spawn: {ex.Message}");
                            RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit);
                        }
                        return;
                    }
                    if (!res.Ok)
                    {
                        PassengerHud.Toast(res.Reason == "full" ? "No room." : res.Reason == "denied" ? "No access." : "Already taken.");
                        return;
                    }
                    var ci = new CargoInstance(res.ItemName, res.Amount, res.PricePerUnit, res.Paid);
                    // Round-47: a taken sealed box arrives with its contents.
                    if (res.Nested != null && res.Nested.Count > 0)
                        foreach (var n in res.Nested)
                            if (n != null) ci.nestedCargoInstances.Add(new BigAmbitions.Items.NestedCargoInstance(n.ItemName, n.Amount, n.PricePerUnit, null));
                    if (PlayerHelper.ItemInstanceInHands == null)
                        PlayerHelper.ItemInstanceInHands = ItemHelper.InitializeItemInHandsWithCargo(ci);
                    else
                    {
                        // No empty hands (race after the request) — give it back so the owner's holder is made
                        // whole. A STATION take must return via the station merge path ("stationreturn"): the
                        // generic put runs TryToAddToCargo, which a register's cargoCapacity=0 always refuses —
                        // the give-back would bounce and the removed stock would be lost. A sealed BOX returns
                        // with its nested contents (round-47) or the give-back would strip it.
                        if (res.Ctx == "boxtake")
                            RequestPutBox(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit, res.Nested);
                        else
                            RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit,
                                       res.Ctx == "stationtake" ? "stationreturn" : "");
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
                    if (res.Ctx == "stationreturn")
                    {
                        // A station-take give-back: on success there is nothing to consume locally (the
                        // contents never reached our hands). A failure here is the rare double-race (owner
                        // station changed between the take and the return) — the stock is stranded; log loud.
                        if (!res.Ok)
                        {
                            PassengerHud.Toast("Couldn't return the contents to the station.");
                            Plugin.Logger.LogWarning($"[BStore] stationreturn REFUSED ({res.Reason}) for {res.Amount}×{res.ItemName} on '{res.AddressKey}'/{res.ItemId} — removed stock could not be returned.");
                        }
                        return;
                    }
                    if (!res.Ok)
                    {
                        PassengerHud.Toast(res.Reason == "full"  ? "Storage full."
                                         : res.Reason == "mixed" ? "Can't mix with the stock already loaded."
                                         : "Couldn't store.");
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
