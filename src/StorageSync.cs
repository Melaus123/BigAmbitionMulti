using System;
using BigAmbitions.Items;   // ItemInstance, CargoInstance, NestedCargoInstance, ItemsGetter
using Buildings;            // BuildingRegistration
using Helpers;
using Vehicles.VehicleTypes;   // VehicleInstance resolution (matches VehicleStorageSync's usings)

namespace BigAmbitionsMP
{
    /// <summary>
    /// THE storage engine (unification Stages A/A2/B, 2026-08 — design:
    /// .modding/03-systems/storage-unification-2026-08.md). One owner-apply, one result handler
    /// and ONE wire family (StorageOp/StorageRes, 195/196, v16) serve BOTH containers (building
    /// interior items and vehicles). VehicleStorageSync / BuildingStorageSync remain only as
    /// per-container SENDER facades (request-side conveniences building StorageOpPayload). Ruling
    /// 37 made this structural: the twin implementations drifted four separate times (sealed
    /// takes, manifest parser, put rollback, cargo callbacks) — a storage fix now has exactly one
    /// home, and the wire has exactly one parser on each side.
    ///
    /// THREADING: OwnerApply() and OnResult() mutate game state and MUST run on the Unity main
    /// thread — the network dispatch marshals them (see MPServer/MPClient), unchanged.
    /// </summary>
    internal static class StorageSync
    {
        // ── Stage A2 COMPLETE (2026-08-25): the extraction-era profile flags are deleted — the
        // three behavior asymmetries they pinned are resolved permanently (each was its own
        // reviewed commit; the field evidence lives in the design doc and F-2026-08-25-E):
        //   A2-1  both containers roll back partial merges — "full" is all-or-nothing.
        //   A2-2  partial vehicle takes fire the cargo callback (the one native gap; every other
        //         path fires natively and the callback plays the box SOUND — never add fires).
        //   A2-3  both containers take with the paid-preference two-pass (ruling 36 makes paid
        //         and unpaid stacks of one item genuinely coexist).

        // ── The wire types ARE the engine types since Stage B (StorageOpPayload/StorageResPayload
        // in Protocol.cs, v16) — the Stage-A internal records and the adapters' mapping layers are
        // deleted; the facades build wire payloads and call SendOp below. ──
        internal const string ContainerBuilding = "building";
        internal const string ContainerVehicle  = "vehicle";
        internal const string OpTake = "take", OpPut = "put", OpMarkPaid = "markpaid", OpSetStock = "setstock";

        /// <summary>The one send seam: host hands the op straight to its broker; a client sends to
        /// the host, which resolves the owner (ruling 38) and routes.</summary>
        internal static void SendOp(StorageOpPayload req)
        {
            if (req == null) return;
#if DEBUG || BAMP_DEV
            DebugWireRoundTripOnce();
#endif
            if (MPServer.IsRunning) MPServer.HandleStorageOp(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.StorageOp, MPConfig.PlayerId, req));
        }

        // ── THE nested-contents codec (risk R7/R11): the ONLY place the Nested band is built or
        // consumed. Depth-1 by construction (NestedCargoInstance has no nested field). The 24
        // bound is a TRIPWIRE ONLY — never a truncation (Stage B review MAJOR-1: the take removes
        // the WHOLE box from the owner, so a truncated echo would DESTROY the remainder; the bound
        // is the packed item's own cargoCapacity, item-database data the mod cannot assume).
        // customColors deferred (D4 — SerializableColor's shape is not in the decompile; extend
        // BOTH halves here when it is read). ──
        internal static System.Collections.Generic.List<CargoNestedInfo> EncodeNested(System.Collections.Generic.List<NestedCargoInstance>? nested)
        {
            var outList = new System.Collections.Generic.List<CargoNestedInfo>();
            if (nested == null) return outList;
            foreach (var n in nested)
            {
                if (n == null) continue;
                outList.Add(new CargoNestedInfo { ItemName = n.itemName ?? "", Amount = n.amount, PricePerUnit = n.pricePerUnit });
            }
            if (outList.Count > 24)
                Plugin.Logger.LogWarning($"[Store] nested encode carries {outList.Count} entries (>24 tripwire) — conveyed IN FULL; if this recurs, revisit the payload size assumptions.");
            return outList;
        }

        internal static void DecodeNestedInto(CargoInstance ci, System.Collections.Generic.List<CargoNestedInfo>? nested)
        {
            if (ci == null || nested == null || nested.Count == 0) return;
            foreach (var n in nested)
                if (n != null) ci.nestedCargoInstances.Add(new NestedCargoInstance(n.ItemName, n.Amount, n.PricePerUnit, null));
        }

#if DEBUG || BAMP_DEV
        // Risk R7 — the guard against the GhostCargoFor disease (a field written by one side and
        // not read by the other): round-trip an all-fields-non-default payload through the REAL
        // envelope serializer once per session and compare every field. Runs lazily on the first
        // storage op (main thread) so it needs no entry-point hook.
        private static bool _wireChecked;
        private static void DebugWireRoundTripOnce()
        {
            if (_wireChecked) return;
            _wireChecked = true;
            try
            {
                var op = new StorageOpPayload
                {
                    Container = "vehicle", AddressKey = "a", ItemId = "i", VehicleId = "v",
                    PlayerId = "p", Op = "take", Ctx = "boxtake", ItemName = "n", Amount = 2,
                    Paid = false, PricePerUnit = 1.5f, Count = 3, Silent = true,
                    Nested = { new CargoNestedInfo { ItemName = "x", Amount = 4, PricePerUnit = 2.5f } },
                };
                var env = MessageEnvelope.Create(MessageType.StorageOp, "p", op);
                var back = env.GetPayload<StorageOpPayload>();
                bool ok = back != null && back.Container == op.Container && back.AddressKey == op.AddressKey
                    && back.ItemId == op.ItemId && back.VehicleId == op.VehicleId && back.PlayerId == op.PlayerId
                    && back.Op == op.Op && back.Ctx == op.Ctx && back.ItemName == op.ItemName
                    && back.Amount == op.Amount && back.Paid == op.Paid && back.PricePerUnit == op.PricePerUnit
                    && back.Count == op.Count && back.Silent == op.Silent
                    && back.Nested.Count == 1 && back.Nested[0].ItemName == "x"
                    && back.Nested[0].Amount == 4 && back.Nested[0].PricePerUnit == 2.5f;
                if (!ok) Plugin.Logger.LogError("[Store] WIRE ROUND-TRIP CHECK FAILED — a StorageOp field does not survive serialization (the GhostCargoFor class). Fix before trusting any storage op.");
                else Plugin.Logger.LogInfo("[Store] wire round-trip check OK (all StorageOp fields survive).");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[Store] wire round-trip check THREW: {ex.Message}"); }
        }
#endif

        private static StorageResPayload ResFrom(StorageOpPayload req) => new StorageResPayload
        {
            Container = req.Container, AddressKey = req.AddressKey, ItemId = req.ItemId,
            VehicleId = req.VehicleId, PlayerId = req.PlayerId, Op = req.Op, Ctx = req.Ctx,
            ItemName = req.ItemName, Amount = req.Amount, Paid = req.Paid,
            PricePerUnit = req.PricePerUnit, Count = req.Count, Silent = req.Silent,
            Ok = false, Reason = "gone",
        };

        // ── Round-49 slice 2: helper PLACE pending slot (single-slot by design; clicks and
        // verdicts are both main-thread, one place in flight at a time). Moved verbatim. ──
        private static CargoInstance? _pendingPlace;

        internal static void SetPendingPlace(CargoInstance source)
        {
            try
            {
                var pc = source.Copy();   // name/price/paid/colors (colors deep-copied by the game's Copy)
                pc.amount = 1;            // native place consumes exactly one unit
                if (source.nestedCargoInstances != null)
                    foreach (var n in source.nestedCargoInstances)
                        if (n != null)
                            pc.nestedCargoInstances.Add(new NestedCargoInstance(n.itemName, n.amount, n.pricePerUnit,
                                n.customColors == null ? null : new System.Collections.Generic.List<CustomColor>(n.customColors)));
                _pendingPlace = pc;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] SetPendingPlace: {ex.Message}"); _pendingPlace = null; }
        }

        // ══════════════════════════════ OWNER SIDE ══════════════════════════════
        // MAIN THREAD ONLY. One body; container-specific pieces are resolution,
        // authorization and convergence — everything else is shared.

        internal static StorageResPayload OwnerApply(StorageOpPayload req)
        {
            var res = ResFrom(req);
            try
            {
                if (req.Container == ContainerVehicle) OwnerApplyVehicle(req, res);
                else                                   OwnerApplyBuilding(req, res);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[{Tag(req.Container)}] OwnerApply: {ex.Message}");
                res.Ok = false; res.Reason = "error";
            }
            return res;
        }

        private static string Tag(string container) => container == ContainerVehicle ? "VStore" : "BStore";

        private static void OwnerApplyVehicle(StorageOpPayload req, StorageResPayload res)
        {
            // Locked storage opens only to a granted key-holder (authoritative backstop).
            if (PassengerSync.IsLocked(req.VehicleId) && !GrantSync.IsGranted(MPConfig.PlayerId, req.PlayerId)) { res.Reason = "locked"; return; }
            VehicleInstance? found = null;
            var list = VehicleHelper.AllPlayerVehicles;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var vi = list[i]?.vehicleInstance;
                    if (vi != null && vi.id == req.VehicleId) { found = vi; break; }
                }
            // Field 20260821-180203: AllPlayerVehicles is the LIVE controller list — a cart
            // left inside an interior the owner doesn't have loaded has NO live object on the
            // owner's machine ("data-follow"), so every borrower TAKE/PUT on it failed "gone"
            // and reverted. The fallback is GameInstance.VehicleInstances — the SAME
            // VehicleInstance objects live controllers hold (CreateAndSpawnVehicle adds the
            // one instance to both), so live-first vs data-first find one identical record and
            // every mutation below works without a spawned controller. LIVE-FIRST ORDER IS
            // KEPT DELIBERATELY (unification risk R8). ReadLocalFleet's dormant pass emits
            // every save-data vehicle with its manifest, so a dormant mutation here
            // re-broadcasts on the next resting-sig change; MarkFleetDirty makes it immediate.
            if (found == null)
            {
                var dataList = SaveGameManager.Current?.VehicleInstances;
                if (dataList != null)
                    for (int i = 0; i < dataList.Count; i++)
                    {
                        var vi = dataList[i];
                        if (vi != null && vi.id == req.VehicleId)
                        {
                            found = vi;
                            Plugin.Logger.LogInfo($"[VStore] owner apply on '{req.VehicleId}': no live object — using the data record (dormant vehicle).");
                            break;
                        }
                    }
            }
            if (found != null)
            {
                var inst = found;
                if (req.Op == OpTake)
                {
                    // Stage C (M5, user-approved): sealed boxes take WHOLE from trunks through the
                    // same codec fridges use — parity with the owner's native ClickItem, which has
                    // no seal check. Ruling 39: any OTHER ctx is building-only machinery and
                    // refuses "unsupported" rather than falling through to the loose loop.
                    if (req.Ctx == "boxtake")
                        TakeWholeInstance(inst.cargoInstances, req, res,
                                          removeWhole: (ci) => inst.RemoveFromCargo(ci));
                    else if (!string.IsNullOrEmpty(req.Ctx))
                    {
                        res.Reason = "unsupported";
                        Plugin.Logger.LogWarning($"[VStore] take ctx '{req.Ctx}' unsupported for a vehicle container.");
                    }
                    else
                        // A2-2: fire the callback ONLY when the reduce leaves the stack alive — the
                        // single case native VehicleInstance omits (see the A2 header above).
                        TakeLoose(inst.cargoInstances, req, res,
                                  reduce: (ci, amt) =>
                                  {
                                      inst.ReduceFromCargo(ci, amt);
                                      if (ci.amount > 0)
                                          try { inst.OnItemsInCargoUpdated()?.Invoke(); } catch { }
                                  });
                }
                else if (req.Op == OpMarkPaid)
                    MarkPaidWithSplit(inst, req, res);
                else if (req.Op == OpPut)
                {
                    // Ruling 39: the vehicle put knows exactly two ctxs — plain and the sealed-box
                    // give-back (Stage C). Building-only put machinery (producer/stationreturn/
                    // worn) refuses rather than falling through to the generic path.
                    if (!string.IsNullOrEmpty(req.Ctx) && req.Ctx != "boxreturn")
                    {
                        res.Reason = "unsupported";
                        Plugin.Logger.LogWarning($"[VStore] put ctx '{req.Ctx}' unsupported for a vehicle container.");
                        return;
                    }
                    // A2-1: the rollback below is LIVE (F-2026-08-25-E) — "full" is all-or-nothing
                    // on both containers now. Review-verified against BOTH native absorption shapes
                    // (merge-into-existing and copy+append); reversal is amount-based per
                    // (name, paid) — distribution across fungible stacks may differ, converging on
                    // the next manifest (inherited round-32 semantics, both containers).
                    var ci = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                    DecodeNestedInto(ci, req.Nested);   // ONE codec — a sealed give-back keeps its contents (Stage C)
                    if (inst.TryToAddToCargo(ci)) { res.Ok = true; res.Reason = ""; }
                    else
                    {
                        RollbackPartialMerge(req, req.Amount - ci.amount,
                            inst.cargoInstances,
                            (s) => inst.RemoveFromCargo(s), (s, amt) => inst.ReduceFromCargo(s, amt));
                        res.Reason = "full";
                        // Risk R12: DeliveryVehicleInstance overrides TryToAddToCargo with a whitelist —
                        // name the holder's runtime type so a whitelist refusal doesn't read as capacity.
                        Plugin.Logger.LogInfo($"[VStore] put refused 'full' by holder type {inst.GetType().Name}.");
                    }
                }
                else { res.Reason = "unsupported"; Plugin.Logger.LogWarning($"[VStore] op '{req.Op}' unsupported for a vehicle container."); }
            }
            // The cargo change re-syncs to every ghost through VehicleManager's normal fleet broadcast.
            if (res.Ok)
            {
                // Car-package belt (2026-08-25): a change on a vehicle the owner is DRIVING
                // contributes only its id to the resting sig and would wait for the 5 s
                // heartbeat; the dirty flag forces the next tick's packet out full.
                try { VehicleManager.MarkFleetDirty(); } catch { }
                Plugin.Logger.LogInfo($"[VStore] owner applied {(req.Op == OpTake ? "TAKE" : req.Op == OpMarkPaid ? "MARK-PAID" : "PUT")}{(req.Silent ? " (mirror)" : "")} {req.Amount}×{req.ItemName} on '{req.VehicleId}' for '{req.PlayerId}'.");
            }
        }

        private static void OwnerApplyBuilding(StorageOpPayload req, StorageResPayload res)
        {
            // Grant backstop (the host already gated; re-verify on the authoritative machine).
            // Housing OR Business (round-32): the gates only ever OFFER these ops in buildings the
            // requester holds the matching grant for, so kind-precision here buys nothing — either
            // key from this owner authorizes cargo ops on this owner's buildings.
            if (req.PlayerId != MPConfig.PlayerId
                && !GrantSync.IsGranted(GrantKind.Housing, MPConfig.PlayerId, req.PlayerId)
                && !GrantSync.IsGranted(GrantKind.Business, MPConfig.PlayerId, req.PlayerId))
            { res.Reason = "denied"; return; }

            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return;
            BuildingRegistration? reg = null;
            foreach (var r in gi.BuildingRegistrations)
                if (r != null && GameStateReader.AddressKey(r) == req.AddressKey) { reg = r; break; }
            if (reg == null || reg.itemInstances == null) return;

            ItemInstance? item = null;
            foreach (var kv in reg.itemInstances)
                if (kv.Value != null && (kv.Value.id?.ToString() ?? "") == req.ItemId) { item = kv.Value; break; }
            if (item == null) return;

            if (req.Op == OpTake)
            {
                // Round-47 (slice 2b) — a SEALED BOX taken from a storage shelf through the manage
                // panel. The regular take loop SKIPS sealed instances; boxtake takes the whole
                // box (first name+amount+paid match — identical boxes are fungible; contents are
                // owner truth) and echoes its NESTED contents so the guest's in-hands box is exact.
                // Round-47b — helper SELL/DISCARD: remove up to Count identical non-sealed stack
                // instances (name+amount+paid identity — grouped rows are fungible). Money is the
                // REQUESTER's side (credited there on this Ok); the owner only loses the stock,
                // exactly as if the helper were standing at the shelf natively.
                if (req.Ctx == "stacksell" || req.Ctx == "stackdiscard")
                    RemoveStackInstances(item, req, res);
                else if (req.Ctx == "boxtake")
                    TakeWholeInstance(item.cargoInstances, req, res,
                                      removeWhole: (ci) => item.RemoveFromCargo(ci));
                else if (req.Ctx == "stationtake")
                    TakeStock(reg, item, req, res);
                else
                    // A2-3: the paid-preference two-pass now applies here too (ruling 36 —
                    // paid and unpaid stacks of one item genuinely coexist; a request naming
                    // one must not consume the other while both fit). ItemInstance.ReduceFromCargo
                    // fires the cargo callback natively on both branches — no engine fire here.
                    TakeLoose(item.cargoInstances, req, res,
                              reduce: (ci, amt) => item.ReduceFromCargo(ci, amt));
            }
            else if (req.Op == OpPut)
            {
                if (req.Ctx == "producer" || req.Ctx == "stationreturn")
                    PutIntoSingleSlot(item, req, res);
                else
                {
                    var ci = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                    // Round-47: a returned sealed box keeps its contents ("boxreturn" give-backs).
                    DecodeNestedInto(ci, req.Nested);   // ONE codec (R7)
                    if (item.TryToAddToCargo(ci)) { res.Ok = true; res.Reason = ""; }
                    else
                    {
                        // Round-32 (decompile ItemInstance.cs:198-231): TryToAddToCargo PARTIALLY merges
                        // before returning false when the holder can't take the whole stack — without a
                        // rollback the absorbed part stays here while the guest keeps the full stack = DUP.
                        // Roll it back so "full" is all-or-nothing.
                        RollbackPartialMerge(req, req.Amount - ci.amount,
                            item.cargoInstances,
                            (s) => item.RemoveFromCargo(s), (s, amt) => item.ReduceFromCargo(s, amt));
                        res.Reason = "full";
                        // Risk R12: DeliveryVehicleInstance-class overrides refuse for reasons capacity
                        // can't explain — name the holder's runtime type so the log can.
                        Plugin.Logger.LogInfo($"[BStore] put refused 'full' by holder type {item.GetType().Name}.");
                    }
                }
            }
            else if (req.Op == OpSetStock)
            {
                res.Ok = ApplySetStock(reg, item, req, out var reason);
                res.Reason = reason;
            }
            else { res.Reason = "unsupported"; Plugin.Logger.LogWarning($"[BStore] op '{req.Op}' unsupported for a building container."); }

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

        // ── Shared apply internals (moved verbatim; delegates carry the container's mutators) ──

        /// <summary>First matching unsealed stack with enough on hand (first request wins), in TWO
        /// passes (A2-3, both containers): pass 0 prefers a stack whose paid flag matches the
        /// request (mirrored takes name the exact stack the borrower consumed natively; ruling 36
        /// makes mixed paid states real), pass 1 falls back to any match so UI takes keep working.
        /// IsSealed hoisted per iteration (risk R13 — the getter re-resolves through ItemsGetter
        /// on every access).</summary>
        private static void TakeLoose(System.Collections.Generic.List<CargoInstance>? src, StorageOpPayload req, StorageResPayload res,
                                      Action<CargoInstance, int> reduce)
        {
            if (src == null) return;
            for (int pass = 0; pass < 2 && !res.Ok; pass++)
                for (int c = 0; c < src.Count; c++)
                {
                    var ci = src[c];
                    if (ci == null) continue;
                    bool sealedCi = ci.IsSealed;
                    if (sealedCi) continue;
                    if (ci.itemName != req.ItemName) continue;   // match by item; carry the owner's REAL paid/price back (manifest is lossy)
                    if (pass == 0 && ci.paid != req.Paid) continue;
                    if (ci.amount < req.Amount) continue;
                    res.Paid = ci.paid;
                    res.PricePerUnit = ci.pricePerUnit;
                    reduce(ci, req.Amount);
                    res.Ok = true; res.Reason = "";
                    break;
                }
        }

        /// <summary>THE sealed-box codec's take half (from building boxtake, round-47 slice 2b;
        /// generalized to BOTH containers in Stage C): whole-instance removal by name+amount+paid
        /// identity, nested contents echoed. Native contract: sealed instances move ONLY whole
        /// (ReduceFromCargo no-ops on sealed; MergeIntoCargo early-returns) — F-2026-08-25-F.
        /// The removeWhole delegate carries the container's own RemoveFromCargo, which fires the
        /// native cargo callback on both container kinds (box-take sound parity for free).</summary>
        private static void TakeWholeInstance(System.Collections.Generic.List<CargoInstance>? src, StorageOpPayload req, StorageResPayload res,
                                              Action<CargoInstance> removeWhole)
        {
            if (src == null) return;
            for (int c = 0; c < src.Count; c++)
            {
                var ci = src[c];
                if (ci == null || ci.itemName != req.ItemName) continue;
                if (ci.amount != req.Amount || ci.paid != req.Paid) continue;
                res.Paid = ci.paid; res.PricePerUnit = ci.pricePerUnit; res.Amount = ci.amount;
                res.Nested = EncodeNested(ci.nestedCargoInstances);   // ONE codec (R7/R11)
                removeWhole(ci);
                res.Ok = true; res.Reason = "";
                break;
            }
        }

        /// <summary>Round-38e — "REMOVE CONTENT" routed for helpers: mirror of the native
        /// ItemController.RemoveStockInContent (:1091-1152) the owner's button runs — take the
        /// ENTIRE stock (owner truth, not the requester's replica amount), clear the emptied
        /// slot's NAME like native does (:1123/:1138), fire the cargo callback, run the native
        /// tail refreshers. Echoes the real amount/paid/price so the delivered box is faithful.</summary>
        private static void TakeStock(BuildingRegistration reg, ItemInstance item, StorageOpPayload req, StorageResPayload res)
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

        /// <summary>Round-47b — helper SELL/DISCARD stacks: remove up to Count identical non-sealed
        /// instances (name+amount+paid identity). ECHO POLICY (risk R2): res.Amount/Paid/PricePerUnit
        /// deliberately stay the REQUESTER's values — the sell credit is computed from them; only
        /// res.Count is owner truth. Never share an echo helper with the owner-truth branches.</summary>
        private static void RemoveStackInstances(ItemInstance item, StorageOpPayload req, StorageResPayload res)
        {
            var ssrc = item.cargoInstances;
            int removed = 0;
            if (ssrc != null)
                for (int c = ssrc.Count - 1; c >= 0 && removed < req.Count; c--)
                {
                    var ci = ssrc[c];
                    if (ci == null) continue;
                    bool sealedCi = ci.IsSealed;
                    if (sealedCi) continue;
                    if (ci.itemName != req.ItemName || ci.amount != req.Amount || ci.paid != req.Paid) continue;
                    item.RemoveFromCargo(ci);
                    removed++;
                }
            if (removed > 0) { res.Ok = true; res.Reason = ""; res.Count = removed; }
            else res.Reason = "gone";
        }

        /// <summary>Round-32's rollback: TryToAddToCargo PARTIALLY merges before returning false —
        /// remove the absorbed part back out so "full" is all-or-nothing.</summary>
        private static void RollbackPartialMerge(StorageOpPayload req, int absorbed,
            System.Collections.Generic.List<CargoInstance>? src,
            Action<CargoInstance> removeWhole, Action<CargoInstance, int> reduceBy)
        {
            if (absorbed <= 0 || src == null) return;
            for (int c = src.Count - 1; c >= 0 && absorbed > 0; c--)
            {
                var s = src[c];
                if (s == null) continue;
                bool sealedS = s.IsSealed;
                if (sealedS || s.itemName != req.ItemName || s.paid != req.Paid) continue;
                int take = Math.Min(absorbed, s.amount);
                if (take >= s.amount) removeWhole(s); else reduceBy(s, take);
                absorbed -= take;
            }
            Plugin.Logger.LogInfo($"[{Tag(req.Container)}] put of {req.Amount}×{req.ItemName} didn't fully fit — partial merge rolled back.");
        }

        /// <summary>Single-slot station put (producer refill / stationreturn give-back) — moved
        /// verbatim from BuildingStorageSync (rounds 37f/38/38c; see those comments in git history
        /// for the field evidence). Partial fills are native semantics; res.Amount echoes what
        /// LANDED and the requester consumes exactly that.</summary>
        private static void PutIntoSingleSlot(ItemInstance item, StorageOpPayload req, StorageResPayload res)
        {
            var slot = (item.cargoInstances != null && item.cargoInstances.Count == 1) ? item.cargoInstances[0] : null;
            if (slot == null) { res.Reason = "full"; return; }
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
            { res.Reason = "full"; return; }   // a different ingredient is loaded — genuine refusal
            // Round-38: BOTH native merge primitives gate on paid EQUALITY, and a stock station's
            // cargoCapacity is 1 — a mismatch is an instant all-or-nothing refusal. An EMPTY slot's
            // paid/price are dead leftovers; adopt the incoming stack's flags exactly as the
            // owner's own first deposit would (TryToAddToCargo appends wholesale onto an empty list).
            if (slot.amount == 0 && (slot.paid != req.Paid || slot.pricePerUnit != req.PricePerUnit))
            {
                slot.paid = req.Paid; slot.pricePerUnit = req.PricePerUnit;
                Plugin.Logger.LogInfo($"[BStore] producer put adopted paid={req.Paid}/price={req.PricePerUnit:F2} onto the empty slot on '{req.AddressKey}'/{req.ItemId} (owner-parity stamp).");
            }
            // Round-38c: merge via the game's OWN station primitive (MergeCargo — TryToAddToCargo
            // hard-gates on cargoCapacity/whitelist, which a stock station's data doesn't satisfy).
            var inc = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
            int before = slot.amount;
            item.MergeCargo(inc, slot, req.Amount);
            int landed = slot.amount - before;
            if (landed > 0) { res.Ok = true; res.Reason = ""; res.Amount = landed; }
            else res.Reason = (slot.amount > 0 && slot.paid != req.Paid) ? "mixed" : "full";
        }

        /// <summary>Borrowed-vehicle checkout mirror (ruling 35): flip Amount of unpaid ItemName
        /// stacks to paid on the REAL vehicle, splitting when partial. THE SPLIT USES RAW
        /// AddToCargo, NEVER TryToAddToCargo (risk R10): the net slot count is unchanged, so
        /// capacity must not be consulted — a full trunk refusing the split would silently void
        /// the borrower's payment (the F-2026-08-25-C fries class, reintroduced).</summary>
        private static void MarkPaidWithSplit(VehicleInstance inst, StorageOpPayload req, StorageResPayload res)
        {
            int remaining = req.Amount;
            var src = inst.cargoInstances;
            if (src != null)
            {
                for (int c = src.Count - 1; c >= 0 && remaining > 0; c--)
                {
                    var ci = src[c];
                    if (ci == null) continue;
                    bool sealedCi = ci.IsSealed;
                    // PROBE-START: P-SEALED-MARKPAID — Stage C review MINOR-1: the checkout
                    // collector bills unpaid sealed boxes but this mirror skips them, so the
                    // borrower would pay while the owner's record stays unpaid (the fries class,
                    // sealed edition — pre-existing; C makes sealed trunk traffic routine). This
                    // line is the field-evidence gate for the proposed whole-instance branch.
                    if (sealedCi && !ci.paid && ci.itemName == req.ItemName)
                        Plugin.Logger.LogWarning($"[PROBE] sealed UNPAID '{ci.itemName}' skipped by mark-paid on '{req.VehicleId}' — the sealed-checkout gap is REAL here (C MINOR-1).");
                    // PROBE-END: P-SEALED-MARKPAID
                    if (sealedCi || ci.paid) continue;
                    if (ci.itemName != req.ItemName) continue;
                    if (ci.amount <= remaining) { remaining -= ci.amount; ci.paid = true; }
                    else
                    {
                        ci.amount -= remaining;
                        inst.AddToCargo(new CargoInstance(req.ItemName, remaining, ci.pricePerUnit, true));
                        remaining = 0;
                    }
                }
            }
            try { inst.OnItemsInCargoUpdated()?.Invoke(); } catch { }
            res.Ok = remaining == 0;
            if (!res.Ok) { res.Reason = "gone"; Plugin.Logger.LogWarning($"[VStore] mark-paid on '{req.VehicleId}': {remaining}×{req.ItemName} had no unpaid stack (state drift; re-sync will converge)."); }
        }

        /// <summary>Round-39c — make the business RECOGNIZE a routed cargo change. Moved verbatim;
        /// stays internal with its second consumer (CustomerEntrySync) served via the
        /// BuildingStorageSync delegate.</summary>
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

        /// <summary>Round-32, OWNER side: change what an item stocks — moved verbatim (dropdown
        /// moves minus UI; producerset = bare name-set for producers; signset = linkedItemName).</summary>
        private static bool ApplySetStock(BuildingRegistration reg, ItemInstance item, StorageOpPayload req, out string reason)
        {
            reason = "";
            // Round-49 slice 5 — a SIGN's dropdown pick: linkedItemName lives on the ItemInstance
            // itself (signs have no cargo slots — the checks below would refuse one as "gone").
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

            // Owner-side audit line (round 35): attribution, not restriction (user 2026-07-04).
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

        // ══════════════════════════════ ACCESSOR SIDE ══════════════════════════════
        // MAIN THREAD ONLY.

        internal static void OnResult(StorageResPayload res)
        {
            try
            {
                // RISK R1 — FIRST STATEMENT, before any ctx routing: mirrors of native actions
                // (borrowed-vehicle shopping) already consumed/placed the item natively; a grant
                // needs NOTHING here, and a failure is state drift the next re-sync repairs.
                // Reaching Deliver() with a Silent result would hand the borrower a second copy.
                if (res.Silent)
                {
                    if (!res.Ok)
                        Plugin.Logger.LogWarning($"[VStore] mirror {res.Op.ToUpperInvariant()} {res.Amount}×{res.ItemName} on '{res.VehicleId}' FAILED owner-side ({res.Reason}) — replica reverts on next re-sync.");
                    return;
                }
                if (res.Op == OpTake)       OnTakeResult(res);
                else if (res.Op == OpPut)   OnPutResult(res);
                else if (res.Op == OpSetStock)
                {
                    // Success needs no local action — the owner's interior push re-renders the shelf.
                    if (!res.Ok)
                        PassengerHud.Toast(res.Reason == "full" ? "No storage room for the current stock."
                                         : res.Reason == "occupied" ? "That machine is already loaded."
                                         : res.Reason == "denied" ? "No access." : "Couldn't change the stock.");
                }
                // OpMarkPaid: mirrors are Silent by construction — nothing ever reaches here.
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[{Tag(res.Container)}] OnResult: {ex.Message}"); }
        }

        private static void OnTakeResult(StorageResPayload res)
        {
            // ── building-only ctx routes (moved verbatim) ──
            if (res.Ctx == "stacksell" || res.Ctx == "stackdiscard")
            {
                if (!res.Ok) { PassengerHud.Toast("Already gone."); return; }
                if (res.Ctx == "stacksell")
                {
                    try
                    {
                        // RISK R2 — credit basis is the REQUESTER's echoed Amount/Price × owner Count.
                        // No custom toast — ChangeMoneySafe fires the game's own transaction
                        // feedback; doubling it broke parity (user 2026-07-07).
                        var priced = new CargoInstance(res.ItemName, res.Amount, res.PricePerUnit, res.Paid);
                        float total = priced.GetSellingPrice() * res.Count;
                        var data = new System.Collections.Generic.Dictionary<string, string> { { "itemName", res.ItemName } };
                        GameManager.ChangeMoneySafe(total, new TransactionInfo("ba:transaction_itemsold", data));
                        Plugin.Logger.LogInfo($"[Business] helper stack sell confirmed: {res.Count}×({res.ItemName}×{res.Amount}) → ${total:F2} credited locally (credit basis: {res.Amount}×{res.PricePerUnit:F2}×{res.Count}).");
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
            if (res.Ctx == "placereduce") { OnPlaceReduceResult(res); return; }
            if (res.Ctx == "vehicletake") { OnVehicleTakeResult(res); return; }

            // ── shared failure toasts (union of both channels' reasons — each container only
            // ever produces its own subset, so the mapping is verbatim for both) ──
            if (!res.Ok)
            {
                PassengerHud.Toast(res.Reason == "locked" ? "Vehicle locked."
                                 : res.Reason == "full"   ? "No room."
                                 : res.Reason == "denied" ? "No access."
                                 :                          "Already taken.");
                return;
            }
            var ci = new CargoInstance(res.ItemName, res.Amount, res.PricePerUnit, res.Paid);
            // Round-47: a taken sealed box arrives with its contents (building boxtake today;
            // Stage C extends the same band to vehicles).
            DecodeNestedInto(ci, res.Nested);   // ONE codec (R7)
            Deliver(res, ci);
        }

        /// <summary>Unified delivery. The two containers' flows are preserved verbatim behind the
        /// container branch: vehicle = pushed-hand-vehicle attempt (excluding the cart being
        /// pushed) → stale-ActiveVehicleId repair → empty hands + session-guarded panel close →
        /// give-back to the vehicle; building = empty hands → ctx-routed give-back (risk R3:
        /// stationtake returns via stationreturn; boxtake returns WITH nested).</summary>
        private static void Deliver(StorageResPayload res, CargoInstance ci)
        {
            if (res.Container == ContainerVehicle)
            {
                string vid = res.VehicleId;
                try
                {
                    if (PlayerHelper.IsUsingVehicle)
                    {
                        var cur = VehicleHelper.GetCurrentVehicle();
                        if (cur != null && cur.VehicleType != null
                            && cur.VehicleType.spawnInPlayerObject       // a pushed hand-vehicle (hand-truck / flatbed), not a car
                            && cur.VehicleType.maxCargoCapacity > 0
                            && cur.id != "BAMP_" + vid                   // taking from the cart I'm PUSHING = "I want it in hands" —
                                                                         // placing it back would round-trip the take into a no-op
                            && cur.TryToAddToCargo(ci))
                            return;
                    }
                }
                catch { }
                try
                {
                    // Round-34 (probe-confirmed stack): a STALE ActiveVehicleId that resolves to NO local
                    // vehicle makes the native hands pipeline NRE — repair it, then deliver normally.
                    if (PlayerHelper.IsUsingVehicle && VehicleHelper.GetCurrentVehicle() == null)
                    {
                        Plugin.Logger.LogWarning($"[VStore] stale ActiveVehicleId='{SaveGameManager.Current?.ActiveVehicleId}' resolves to no vehicle — cleared (take-to-hands repair).");
                        // NULL, never "" (round-37m, THE dead-shelf root): the native on-foot contract is
                        // ActiveVehicleId == null; an empty string passes ShelfCtaBehavior's != null check
                        // and NREs inside the hover chain, killing OnIoEnter for every storage shelf.
                        try { SaveGameManager.Current.ActiveVehicleId = null; } catch { }
                    }
                    if (PlayerHelper.ItemInstanceInHands == null)   // EMPTY hands only — never clobber what's held
                    {
                        PlayerHelper.ItemInstanceInHands = ItemHelper.InitializeItemInHandsWithCargo(ci);
                        // Into HANDS → close the panel — but ONLY the panel session this take belongs
                        // to (risk R5, round-35): a LATE result from an earlier click must not close a
                        // freshly-reopened panel.
                        if (VehicleStoragePanel.IsOpenFor(vid)) VehicleStoragePanel.Close();
                        return;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] Deliver hands: {ex.Message}"); }
                // No room (race): return the item to the owner so it isn't lost (risk R9). A
                // sealed BOX returns with its nested contents (Stage C — the give-back would
                // otherwise strip it, the exact hazard the building boxreturn closed in round-47).
                try
                {
                    SendOp(new StorageOpPayload
                    {
                        Container = ContainerVehicle, VehicleId = vid, PlayerId = MPConfig.PlayerId,
                        Op = OpPut, Ctx = res.Ctx == "boxtake" ? "boxreturn" : "",
                        ItemName = ci.itemName, Amount = ci.amount, Paid = ci.paid, PricePerUnit = ci.pricePerUnit,
                        Nested = res.Nested ?? new System.Collections.Generic.List<CargoNestedInfo>(),
                    });
                    Plugin.Logger.LogInfo($"[VStore] gave back {ci.amount}×{ci.itemName} to '{vid}' (no room to carry{(res.Ctx == "boxtake" ? "; contents preserved" : "")}).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] give-back: {ex.Message}"); }
                PassengerHud.Toast("No room to carry that.");
                return;
            }
            // ── building container ──
            if (PlayerHelper.ItemInstanceInHands == null)
            {
                PlayerHelper.ItemInstanceInHands = ItemHelper.InitializeItemInHandsWithCargo(ci);
                return;
            }
            // No empty hands (race after the request) — give it back so the owner's holder is made
            // whole (risk R9). A STATION take must return via the station merge path ("stationreturn"):
            // the generic put runs TryToAddToCargo, which a register's cargoCapacity=0 always refuses —
            // the give-back would bounce and the removed stock would be lost (risk R3). A sealed BOX
            // returns with its nested contents (round-47) or the give-back would strip it.
            if (res.Ctx == "boxtake")
                BuildingStorageSync.RequestPutBox(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit, res.Nested);
            else
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit,
                                               res.Ctx == "stationtake" ? "stationreturn" : "");
            Plugin.Logger.LogInfo($"[BStore] gave back {res.Amount}×{res.ItemName} to '{res.AddressKey}'/{res.ItemId} (no room to carry).");
            PassengerHud.Toast("No room to carry that.");
        }

        private static void OnPlaceReduceResult(StorageResPayload res)
        {
            // Round-49 slice 2: the owner reduced one furniture unit off the delivery spot —
            // start the native placement from the click-time captured cargo. Any local failure
            // gives the unit back so the owner's holder is made whole (risk R9).
            var pc = _pendingPlace; _pendingPlace = null;
            if (!res.Ok) { PassengerHud.Toast("Already gone."); return; }
            if (pc == null || pc.itemName != res.ItemName)
            {
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, 1, res.Paid, res.PricePerUnit);
                Plugin.Logger.LogWarning("[BStore] placereduce Ok without a matching pending place — unit returned to the owner (gave back 1).");
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
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, 1, res.Paid, res.PricePerUnit);
                PassengerHud.Toast("Couldn't start placing that — it was put back.");
                Plugin.Logger.LogInfo($"[BStore] gave back 1×{res.ItemName} to '{res.AddressKey}'/{res.ItemId} (placement failed to start).");
            }
            else Plugin.Logger.LogInfo($"[BStore] helper placement started: {pc.itemName} (unit reduced owner-side; completion forwards the interior).");
        }

        private static void OnVehicleTakeResult(StorageResPayload res)
        {
            // Round-49 slice 4: the owner's storage lost the packed hand truck/flatbed — spawn
            // it HERE as the HELPER'S OWN vehicle (user ruling 2026-07-21) with the same native
            // call the owner's unpack runs; the regular local-vehicle sync picks it up.
            if (!res.Ok) { PassengerHud.Toast("Already gone."); return; }
            string vt = "";
            try { vt = ItemsGetter.GetByName(res.ItemName)?.vehicleType ?? ""; } catch { }
            if (string.IsNullOrEmpty(vt))
            {
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit);
                Plugin.Logger.LogWarning($"[BStore] vehicletake Ok but '{res.ItemName}' has no vehicleType — returned to the owner (gave back {res.Amount}).");
                return;
            }
            if (PlayerHelper.IsHoldingItem || PlayerHelper.IsUsingVehicle)
            {
                // Hands filled during the round-trip — give it back rather than strand it (risk R9).
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit);
                PassengerHud.Toast("No room to unpack that now.");
                Plugin.Logger.LogInfo($"[BStore] gave back {res.Amount}×{res.ItemName} to '{res.AddressKey}'/{res.ItemId} (hands filled mid-round-trip).");
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
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit);
            }
        }

        private static void OnPutResult(StorageResPayload res)
        {
            if (res.Ctx == "stationreturn")
            {
                // A station-take give-back: on success there is nothing to consume locally (the
                // contents never reached our hands). A failure here is the rare double-race — the
                // stock is stranded; log loud (risk R3's tripwire).
                if (!res.Ok)
                {
                    PassengerHud.Toast("Couldn't return the contents to the station.");
                    Plugin.Logger.LogWarning($"[BStore] stationreturn REFUSED ({res.Reason}) for {res.Amount}×{res.ItemName} on '{res.AddressKey}'/{res.ItemId} — removed stock could not be returned.");
                }
                return;
            }
            if (!res.Ok)
            {
                // Union of both channels' put-failure toasts ("mixed" is building-only; the vehicle
                // owner never produces it — mapping is verbatim for both).
                PassengerHud.Toast(res.Reason == "full"  ? "Storage full."
                                 : res.Reason == "mixed" ? "Can't mix with the stock already loaded."
                                 : "Couldn't store.");
                // RISK R4 — the four round-37b conditions VERBATIM: our replica said the cargo FITS
                // yet the owner says FULL — proof the replica diverged. Building container only (a
                // vehicle ghost's cargo is Clear+rebuilt every fleet packet — no baseline to drift).
                if (res.Container == ContainerBuilding
                    && res.Reason == "full" && MPClient.IsConnected && !MPServer.IsRunning)
                {
                    GameStatePatcher.ForgetInteriorBaseline(res.AddressKey);
                    MPClient.SendInteriorRequest(res.AddressKey);
                    Plugin.Logger.LogInfo($"[BStore] put-full vs replica-fits mismatch on '{res.AddressKey}' — forced interior re-pull (divergence heal).");
                }
                return;
            }
            // Round-32: producer refills are AMOUNT-CLAMPED (partial stacks) — reduce exactly
            // res.Amount from the source instead of the whole-stack consume below.
            if (res.Ctx == "producer") { ReducePutSourceByAmount(res); return; }
            // The worn case is Ctx-tagged rather than name-inferred so it can never be confused
            // with a truck stack of the same item (round-12 A).
            if (res.Ctx == "wornHead" || res.Ctx == "wornHand") { UnequipWornAfterStore(res); return; }
            ConsumeSource(res);
        }

        /// <summary>Stored OK → drop the deposited item from wherever it came from, and ONLY now:
        /// hands (held directly or as box content) → pushed hand-vehicle. One body for both
        /// containers — the two originals were duplicate implementations (round-12 #1b / B).</summary>
        private static void ConsumeSource(StorageResPayload res)
        {
            try
            {
                var held = PlayerHelper.ItemInstanceInHands;
                if (held == null) { RemoveFromAccessorHandVehicle(res.ItemName, res.Amount); return; }   // truck-sourced deposit
                if (held.itemName == res.ItemName) { PlayerHelper.ItemInstanceInHands = null; return; }   // held the item directly
                // Remove ONLY the content that actually went in; keep anything a near-full holder
                // refused (its put comes back !Ok and never reaches here), so partial deposits
                // never drop items.
                var contents = held.cargoInstances;
                if (contents != null)
                {
                    for (int i = 0; i < contents.Count; i++)
                        if (contents[i] != null && contents[i].itemName == res.ItemName) { contents.RemoveAt(i); break; }
                    if (contents.Count == 0) PlayerHelper.ItemInstanceInHands = null;   // box emptied → drop it
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[{Tag(res.Container)}] put-consume: {ex.Message}"); }
        }

        /// <summary>The owner confirmed a PUT sourced from the accessor's pushed hand-truck/flatbed
        /// (hands were empty at send AND at confirm) — now, and only now, remove that stack from
        /// the truck. Native chokepoints (RemoveFromCargo fires onItemsInCargoUpdated → box visuals
        /// update); the truck is the accessor's OWN vehicle (no BAMP_ prefix), so the proxy guard
        /// passes it. One body — the two originals were identical.</summary>
        private static void RemoveFromAccessorHandVehicle(string itemName, int amount)
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
                    if (ci == null) continue;
                    bool sealedCi = ci.IsSealed;
                    if (sealedCi || ci.itemName != itemName) continue;
                    if (ci.amount <= amount) cur.RemoveFromCargo(ci);
                    else                     cur.ReduceFromCargo(ci, amount);
                    return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Store] truck-consume: {ex.Message}"); }
        }

        // Round-32: amount-aware put consume for producer refills — reduce exactly res.Amount of
        // res.ItemName from the helper's source (held box contents, held single, or hand-vehicle).
        private static void ReducePutSourceByAmount(StorageResPayload res)
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
                        if (c == null) continue;
                        bool sealedC = c.IsSealed;
                        if (sealedC || c.itemName != res.ItemName) continue;
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
                            if (c == null) continue;
                            bool sealedC = c.IsSealed;
                            if (sealedC || c.itemName != res.ItemName) continue;
                            int take = Math.Min(remaining, c.amount);
                            if (take >= c.amount) cur.RemoveFromCargo(c); else cur.ReduceFromCargo(c, take);
                            remaining -= take;
                        }
                }
                if (remaining > 0) Plugin.Logger.LogWarning($"[BStore] producer consume: {remaining}×{res.ItemName} not found locally (source changed mid-request).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] producer consume: {ex.Message}"); }
        }

        // The owner confirmed a PUT of the guest's WORN accessory (hat/hand item → wardrobe/coat
        // rack) — unequip it now, and only now (unequipping on send would vanish the item if the
        // holder was full). Mirrors the native StorePlayerWornItemIntoItemHolder tail. (round-12 A)
        private static void UnequipWornAfterStore(StorageResPayload res)
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
