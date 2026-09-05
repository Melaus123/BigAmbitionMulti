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
                // Review NEW-2 (2026-08-25): the RES payload is load-bearing too — Nested rides
                // every put verdict since MINOR-2 (the boxreturn echo depends on it surviving) —
                // so it gets the same all-fields-non-default round-trip.
                var rp = new StorageResPayload
                {
                    Container = "building", AddressKey = "a", ItemId = "i", VehicleId = "v",
                    PlayerId = "p", Op = "put", Ctx = "boxreturn", ItemName = "n", Amount = 2,
                    Paid = false, PricePerUnit = 1.5f, Count = 3, Silent = true, Ok = true, Reason = "r", Total = 7.25f,
                    Nested = { new CargoNestedInfo { ItemName = "x", Amount = 4, PricePerUnit = 2.5f } },
                };
                var renv = MessageEnvelope.Create(MessageType.StorageRes, "p", rp);
                var rback = renv.GetPayload<StorageResPayload>();
                bool rok = rback != null && rback.Container == rp.Container && rback.AddressKey == rp.AddressKey
                    && rback.ItemId == rp.ItemId && rback.VehicleId == rp.VehicleId && rback.PlayerId == rp.PlayerId
                    && rback.Op == rp.Op && rback.Ctx == rp.Ctx && rback.ItemName == rp.ItemName
                    && rback.Amount == rp.Amount && rback.Paid == rp.Paid && rback.PricePerUnit == rp.PricePerUnit
                    && rback.Count == rp.Count && rback.Silent == rp.Silent && rback.Ok == rp.Ok && rback.Reason == rp.Reason && rback.Total == rp.Total
                    && rback.Nested.Count == 1 && rback.Nested[0].ItemName == "x"
                    && rback.Nested[0].Amount == 4 && rback.Nested[0].PricePerUnit == 2.5f;
                // v17: the trunk-detail payloads are load-bearing display data — same guard.
                var dp = new TrunkDetailResPayload
                {
                    VehicleId = "v", PlayerId = "p", Sig = "s", Ok = true,
                    Rows = { new CargoDetailInfo { ItemName = "n", Amount = 2, Paid = false, PricePerUnit = 1.5f,
                                                   Nested = { new CargoNestedInfo { ItemName = "x", Amount = 4, PricePerUnit = 2.5f } } } },
                };
                var dback = MessageEnvelope.Create(MessageType.TrunkDetailRes, "p", dp).GetPayload<TrunkDetailResPayload>();
                bool dok = dback != null && dback.VehicleId == "v" && dback.PlayerId == "p" && dback.Sig == "s" && dback.Ok
                    && dback.Rows.Count == 1 && dback.Rows[0].ItemName == "n" && dback.Rows[0].Amount == 2
                    && !dback.Rows[0].Paid && dback.Rows[0].PricePerUnit == 1.5f
                    && dback.Rows[0].Nested.Count == 1 && dback.Rows[0].Nested[0].ItemName == "x"
                    && dback.Rows[0].Nested[0].Amount == 4 && dback.Rows[0].Nested[0].PricePerUnit == 2.5f;
                var qback = MessageEnvelope.Create(MessageType.TrunkDetailReq, "p",
                    new TrunkDetailReqPayload { VehicleId = "v", PlayerId = "p", Sig = "s" }).GetPayload<TrunkDetailReqPayload>();
                bool qok = qback != null && qback.VehicleId == "v" && qback.PlayerId == "p" && qback.Sig == "s";
                if (!ok || !rok || !dok || !qok)
                    Plugin.Logger.LogError($"[Store] WIRE ROUND-TRIP CHECK FAILED — a {(!ok ? "StorageOp" : !rok ? "StorageRes" : !dok ? "TrunkDetailRes" : "TrunkDetailReq")} field does not survive serialization (the GhostCargoFor class). Fix before trusting any storage op.");
                else Plugin.Logger.LogInfo("[Store] wire round-trip check OK (all StorageOp + StorageRes + TrunkDetail fields survive).");
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
            // Review 2026-08-25 MINOR-2: the verdict ECHOES the request's Nested band (defensive
            // copy, never an alias) — a PUT verdict previously carried an empty list, so the
            // replica echo of a boxreturn give-back re-added a HOLLOW sealed box (the exact
            // stripped-box shape round-47/Stage-B guard against). Takes still overwrite this
            // with owner truth in TakeWholeInstance.
            Nested = req.Nested == null ? new System.Collections.Generic.List<CargoNestedInfo>()
                                        : new System.Collections.Generic.List<CargoNestedInfo>(req.Nested),
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

        // Replica-echo guard: when this machine is ALSO the accessor (host requested an op on a
        // container the host itself turned out to own — MPServer routes that result object straight
        // back into OnResult), the apply above already mutated the REAL container; the accessor-side
        // replica echo must not run a second application. Object identity is the discriminator: a
        // result that crossed the wire is a different (deserialized) object. Main-thread only.
        private static StorageResPayload? _lastLocalApplyRes;

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
            _lastLocalApplyRes = res;
            return res;
        }

        private static string Tag(string container) => container == ContainerVehicle ? "VStore" : "BStore";

        /// <summary>The one vehicle resolution (owner side), shared by OwnerApplyVehicle and
        /// BuildTrunkDetail. Field 20260821-180203: AllPlayerVehicles is the LIVE controller
        /// list — a cart left inside an interior the owner doesn't have loaded has NO live object
        /// on the owner's machine ("data-follow"), so every borrower TAKE/PUT on it failed "gone"
        /// and reverted. The fallback is GameInstance.VehicleInstances — the SAME VehicleInstance
        /// objects live controllers hold (CreateAndSpawnVehicle adds the one instance to both),
        /// so live-first vs data-first find one identical record and every mutation works without
        /// a spawned controller. LIVE-FIRST ORDER IS KEPT DELIBERATELY (unification risk R8).
        /// ReadLocalFleet's dormant pass emits every save-data vehicle with its manifest, so a
        /// dormant mutation re-broadcasts on the next resting-sig change; MarkFleetDirty makes it
        /// immediate.</summary>
        private static VehicleInstance? FindVehicleById(string vid, string why)
        {
            var list = VehicleHelper.AllPlayerVehicles;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var vi = list[i]?.vehicleInstance;
                    if (vi != null && vi.id == vid) return vi;
                }
            var dataList = SaveGameManager.Current?.VehicleInstances;
            if (dataList != null)
                for (int i = 0; i < dataList.Count; i++)
                {
                    var vi = dataList[i];
                    if (vi != null && vi.id == vid)
                    {
                        // R8 tripwire — attributed (review MINOR-A): a mutation on a dormant record
                        // and a mere display query must stay distinguishable in a log read.
                        Plugin.Logger.LogInfo($"[VStore] {why} on '{vid}': no live object — using the data record (dormant vehicle).");
                        return vi;
                    }
                }
            return null;
        }

        // ══════════ Trunk detail (v17, F-2026-08-25-I proposal 2 — display parity) ══════════

        /// <summary>OWNER side, main thread: the full cargo detail of one trunk — real paid/price
        /// + nested contents through THE codec, uncapped (the broadcast manifest's 24-instance
        /// cap does not apply). Display-only data: the receiver renders it, never applies it.
        /// The lock backstop mirrors OwnerApplyVehicle's — a locked trunk reveals nothing to a
        /// non-granted requester (Ok=false; the panel keeps its manifest fallback).</summary>
        internal static TrunkDetailResPayload BuildTrunkDetail(TrunkDetailReqPayload req)
        {
            var res = new TrunkDetailResPayload { VehicleId = req.VehicleId, PlayerId = req.PlayerId, Sig = req.Sig };
            try
            {
                if (PassengerSync.IsLocked(req.VehicleId) && !GrantSync.IsGranted(MPConfig.PlayerId, req.PlayerId))
                { Plugin.Logger.LogInfo($"[VStore] trunk detail on '{req.VehicleId}' refused — locked to '{req.PlayerId}'."); return res; }
                var inst = FindVehicleById(req.VehicleId, "trunk detail");
                if (inst == null) { Plugin.Logger.LogInfo($"[VStore] trunk detail on '{req.VehicleId}': vehicle unknown here."); return res; }
                var src = inst.cargoInstances;
                if (src != null)
                    for (int i = 0; i < src.Count; i++)
                    {
                        var ci = src[i];
                        if (ci == null || string.IsNullOrEmpty(ci.itemName)) continue;
                        res.Rows.Add(new CargoDetailInfo
                        {
                            ItemName = ci.itemName, Amount = ci.amount, Paid = ci.paid,
                            PricePerUnit = ci.pricePerUnit,
                            Nested = EncodeNested(ci.nestedCargoInstances),   // ONE codec (R7/R11)
                        });
                    }
                res.Ok = true;
                if (res.Rows.Count > 48)   // R11 discipline: warn-only tripwire, never truncation (review MINOR-I)
                    Plugin.Logger.LogWarning($"[VStore] trunk detail for '{req.VehicleId}' carries {res.Rows.Count} instances (>48 tripwire) — conveyed IN FULL on the Gameplay lane; if this recurs, revisit the payload assumptions.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] BuildTrunkDetail: {ex.Message}"); res.Ok = false; }
            return res;
        }

        /// <summary>Review MINOR-G: RequestTrunkDetail bypasses SendOp, so a detail-only session
        /// would never run the wire self-test that validates its own payloads — this hook lets the
        /// facade trigger it. No-op outside Dev/Debug.</summary>
        internal static void DebugWireCheck()
        {
#if DEBUG || BAMP_DEV
            DebugWireRoundTripOnce();
#endif
        }

        /// <summary>ACCESSOR side, main thread: hand the arrived detail to the panel (which
        /// applies its own open-session and freshness guards — R5 shape).</summary>
        internal static void OnTrunkDetail(TrunkDetailResPayload res)
        {
            try { if (res != null) VehicleStoragePanel.ApplyDetail(res); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] OnTrunkDetail: {ex.Message}"); }
        }

        private static void OwnerApplyVehicle(StorageOpPayload req, StorageResPayload res)
        {
            // Locked storage opens only to a granted key-holder (authoritative backstop).
            if (PassengerSync.IsLocked(req.VehicleId) && !GrantSync.IsGranted(MPConfig.PlayerId, req.PlayerId)) { res.Reason = "locked"; return; }
            var found = FindVehicleById(req.VehicleId, "owner apply");
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
                    // Sell parity (user 2026-08-25): the borrower's trunk panel sells/discards a
                    // grouped row exactly like the building manage panel has since round-47b —
                    // same ctx pair, same body, same requester-side credit (R2). Removal-only;
                    // nothing is delivered. Native per-instance RemoveFromCargo fires the cargo
                    // callback each removal, matching the owner's own native sell loop.
                    else if (req.Ctx == "stacksell" || req.Ctx == "stackdiscard")
                        RemoveStackInstances(inst.cargoInstances, req, res,
                                             removeWhole: (ci) => inst.RemoveFromCargo(ci));
                    // Bundle sell/discard (user-approved 2026-08-25): remove ONE filled bag whole,
                    // nested echoed so the seller's credit can price the contents in (native basis).
                    // Removal-only — ctx untouched, so the result routes to the credit branch.
                    else if (req.Ctx == "bundlesell" || req.Ctx == "bundlediscard")
                        TakeBundleInstance(inst.cargoInstances, req, res,
                                           removeWhole: (ci) => inst.RemoveFromCargo(ci));
                    else if (!string.IsNullOrEmpty(req.Ctx))
                    {
                        res.Reason = "unsupported";
                        Plugin.Logger.LogWarning($"[VStore] take ctx '{req.Ctx}' unsupported for a vehicle container.");
                    }
                    else
                        // A2-2: fire the callback ONLY when the reduce leaves the stack alive — the
                        // single case native VehicleInstance omits (see the A2 header above).
                        // removeWhole (the bundle upgrade) fires it natively — no extra fire.
                        TakeLoose(inst.cargoInstances, req, res,
                                  reduce: (ci, amt) =>
                                  {
                                      inst.ReduceFromCargo(ci, amt);
                                      if (ci.amount > 0)
                                          try { inst.OnItemsInCargoUpdated()?.Invoke(); } catch { }
                                  },
                                  removeWhole: (ci) => inst.RemoveFromCargo(ci));
                }
                else if (req.Op == OpMarkPaid)
                    MarkPaidWithSplit(inst, req, res);
                else if (req.Op == OpPut)
                {
                    // Ruling 39: the vehicle put knows exactly four ctxs — plain, the two
                    // give-back shapes (boxreturn = whole box, "return" = loose, R9 hardening),
                    // and "wholeput" (a bag/sealed instance DEPOSITED whole with contents —
                    // parity 2026-08-26; NOT a give-back: a full trunk refuses it normally and
                    // the source stays with the accessor). Building-only put machinery
                    // (producer/stationreturn/worn) refuses rather than falling through.
                    if (!string.IsNullOrEmpty(req.Ctx) && req.Ctx != "boxreturn" && req.Ctx != "return" && req.Ctx != "wholeput")
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
                    else if (req.Ctx == "boxreturn" || req.Ctx == "return")
                    {
                        // R9 hardening (user-approved 2026-08-25): a give-back is the second half
                        // of a removal this container just granted — refusing it DESTROYS the item
                        // (hands-full + holder-refilled double race; native gives SEALED boxes a
                        // capacity-free pass, open bags got none). Force the remainder in — ci
                        // already holds only what the partial merge didn't absorb, and a bundle
                        // never partial-merges (native MergeIntoCargo refuses nested). Overfill-
                        // by-one mirrors the native sealed pass; the next take drains it.
                        inst.AddToCargo(ci);
                        res.Ok = true; res.Reason = "";
                        Plugin.Logger.LogWarning($"[VStore] give-back force-landed {ci.amount}×{ci.itemName} on '{req.VehicleId}' — holder refused 'full' (overfill-by-one, R9 hardening).");
                    }
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
                // A stack op names the owner-confirmed instance count (res.Count) so this line and the
                // requester's credit line reconcile from either log alone (review 2026-08-25 MINOR-7).
                string vwhat = (req.Ctx == "stacksell" || req.Ctx == "stackdiscard")
                    ? $"{req.Ctx.ToUpperInvariant()} {res.Count}×({req.Amount}×{req.ItemName})"
                    : $"{(req.Op == OpTake ? "TAKE" : req.Op == OpMarkPaid ? "MARK-PAID" : "PUT")} {req.Amount}×{req.ItemName}";
                Plugin.Logger.LogInfo($"[VStore] owner applied {vwhat}{(req.Silent ? " (mirror)" : "")} on '{req.VehicleId}' for '{req.PlayerId}'.");
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
                    RemoveStackInstances(item.cargoInstances, req, res,
                                         removeWhole: (ci) => item.RemoveFromCargo(ci));
                else if (req.Ctx == "bundlesell" || req.Ctx == "bundlediscard")   // one filled bag, whole (see the vehicle twin)
                    TakeBundleInstance(item.cargoInstances, req, res,
                                       removeWhole: (ci) => item.RemoveFromCargo(ci));
                else if (req.Ctx == "boxtake")
                    TakeWholeInstance(item.cargoInstances, req, res,
                                      removeWhole: (ci) => item.RemoveFromCargo(ci));
                else if (req.Ctx == "stationtake")
                    TakeStock(reg, item, req, res);
                else if (req.Ctx == "itemsell")
                    SellWholeItem(reg, item, req, res);   // H-SELL-2: whole item + attached children, owner-priced, owner-removed
                else if (!string.IsNullOrEmpty(req.Ctx) && req.Ctx != "consume" && req.Ctx != "placereduce" && req.Ctx != "vehicletake")
                {
                    // Ruling 39 symmetry (2026-08-25 review): the vehicle take refuses unknown
                    // ctx; the building take silently fell through to the loose loop — so a
                    // NEWER build's ctx literal reaching an older peer would remove a PLAIN
                    // stack while the sender's result handling credits something else entirely
                    // (wrong item AND wrong money). Refuse loudly instead.
                    res.Reason = "unsupported";
                    Plugin.Logger.LogWarning($"[BStore] take ctx '{req.Ctx}' unsupported for a building container.");
                }
                else
                    // A2-3: the paid-preference two-pass now applies here too (ruling 36 —
                    // paid and unpaid stacks of one item genuinely coexist; a request naming
                    // one must not consume the other while both fit). ItemInstance.ReduceFromCargo
                    // fires the cargo callback natively on both branches — no engine fire here.
                    TakeLoose(item.cargoInstances, req, res,
                              reduce: (ci, amt) => item.ReduceFromCargo(ci, amt),
                              removeWhole: (ci) => item.RemoveFromCargo(ci));
            }
            else if (req.Op == OpPut)
            {
                if (req.Ctx == "producer" || req.Ctx == "stationreturn")
                {
                    PutIntoSingleSlot(item, req, res);
                    // R9 hardening, the 7th give-back shape (review MINOR-2): a refused STATION
                    // give-back strands the removed stock — R3's named loss path (the slot was
                    // renamed/reloaded mid-round-trip). Rescue via the native ReturnToAShelf —
                    // the same primitive ApplySetStock trusts — before accepting the loss; only
                    // a shop with no shelf room anywhere still refuses (loud on both sides).
                    if (!res.Ok && req.Ctx == "stationreturn")
                    {
                        string was = res.Reason;
                        try
                        {
                            var rescue = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                            if (rescue.ReturnToAShelf(item.AddressCached, item))
                            {
                                res.Ok = true; res.Reason = ""; res.Amount = req.Amount;
                                Plugin.Logger.LogWarning($"[BStore] station give-back landed on a SHELF instead ({req.Amount}×{req.ItemName} @'{req.AddressKey}' — the station refused '{was}'; R9 rescue).");
                            }
                            else if (rescue.amount < req.Amount)
                                // Review MINOR-6: ReturnToAShelf merges as it goes before a false
                                // return — a PARTIAL landing must not read as a total failure.
                                Plugin.Logger.LogWarning($"[BStore] station give-back PARTIALLY shelved: {req.Amount - rescue.amount} of {req.Amount}×{req.ItemName} landed @'{req.AddressKey}', {rescue.amount} stranded (station refused '{was}'; no shelf slot for the rest).");
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] station give-back shelf rescue: {ex.Message}"); }
                    }
                }
                else
                {
                    var ci = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                    // Round-47: a returned sealed box keeps its contents ("boxreturn" give-backs).
                    DecodeNestedInto(ci, req.Nested);   // ONE codec (R7)
                    if (item.TryToAddToCargo(ci)) { res.Ok = true; res.Reason = ""; }
                    else if (req.Ctx == "boxreturn" || req.Ctx == "return")
                    {
                        // R9 hardening — the vehicle twin's reasoning verbatim (see OwnerApplyVehicle):
                        // a refused give-back destroys the item; force the remainder in, overfill-by-one.
                        item.AddToCargo(ci);
                        res.Ok = true; res.Reason = "";
                        Plugin.Logger.LogWarning($"[BStore] give-back force-landed {ci.amount}×{ci.itemName} on '{req.AddressKey}'/{req.ItemId} — holder refused 'full' (overfill-by-one, R9 hardening).");
                    }
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
                string opName = (req.Ctx == "stacksell" || req.Ctx == "stackdiscard") ? req.Ctx.ToUpperInvariant()
                              : req.Op == OpTake ? "TAKE" : req.Op == OpPut ? "PUT" : "SETSTOCK";
                string what   = (req.Ctx == "stacksell" || req.Ctx == "stackdiscard") ? $"{res.Count}×({req.Amount}×{req.ItemName})"   // owner-confirmed count (MINOR-7)
                              : req.Op == OpSetStock ? $"'{req.ItemName}'" : $"{req.Amount}×{req.ItemName}";
                Plugin.Logger.LogInfo($"[BStore] owner applied {opName} {what} on '{req.AddressKey}'/{req.ItemId} for '{req.PlayerId}'.");
            }
        }

        // ── Shared apply internals (moved verbatim; delegates carry the container's mutators) ──

        /// <summary>First matching unsealed stack with enough on hand (first request wins), in TWO
        /// passes (A2-3, both containers): pass 0 prefers a stack whose paid flag matches the
        /// request (mirrored takes name the exact stack the borrower consumed natively; ruling 36
        /// makes mixed paid states real), pass 1 falls back to any match so UI takes keep working.
        /// IsSealed hoisted per iteration (risk R13 — the getter re-resolves through ItemsGetter
        /// on every access).
        ///
        /// F-2026-08-25-I (user-approved proposal 1, field-confirmed): BUNDLES — unsealed
        /// instances carrying nested contents (filled bags) — are never reduced here. Reducing one
        /// 1→0 ran the native RemoveFromCargo and ANNIHILATED its contents (nothing echoes them on
        /// this path); five senders could reach that, and a stale replica made it unclosable from
        /// the requester side. Phase A below takes plain instances only (bundles skipped). Phase B
        /// — reached only when nothing plain matched AND the ctx is the PLAIN take "" — upgrades
        /// the take at the moment of commitment on the machine that holds the truth: the bundle
        /// moves WHOLE, contents echoed through THE codec, and res.Ctx is stamped "boxtake" so
        /// delivery reconstructs the filled bag (ItemHelper's own isbag branch) and a hands-full
        /// give-back returns it WITH contents (both give-back switches key on res.Ctx). The other
        /// TakeLoose ctxs (consume/placereduce/vehicletake) must never receive a bundle — their
        /// no-deliver/spawn semantics would misfire — so for them bundles simply never match and
        /// the op refuses "gone".</summary>
        private static void TakeLoose(System.Collections.Generic.List<CargoInstance>? src, StorageOpPayload req, StorageResPayload res,
                                      Action<CargoInstance, int> reduce, Action<CargoInstance> removeWhole)
        {
            if (src == null) return;
            for (int pass = 0; pass < 2 && !res.Ok; pass++)
                for (int c = 0; c < src.Count; c++)
                {
                    var ci = src[c];
                    if (ci == null) continue;
                    bool sealedCi = ci.IsSealed;
                    if (sealedCi) continue;
                    if (ci.nestedCargoInstances != null && ci.nestedCargoInstances.Count > 0) continue;   // bundles: phase B only
                    if (ci.itemName != req.ItemName) continue;   // match by item; carry the owner's REAL paid/price back (manifest is lossy)
                    if (pass == 0 && ci.paid != req.Paid) continue;
                    if (ci.amount < req.Amount) continue;
                    res.Paid = ci.paid;
                    res.PricePerUnit = ci.pricePerUnit;
                    reduce(ci, req.Amount);
                    res.Ok = true; res.Reason = "";
                    break;
                }
            // Phase B — the bundle upgrade (plain takes only; plain instances always win first).
            // MATCHING policy lives here (bundle-required, paid-preference, instance-exact
            // amount); the MUTATION is TakeWholeOf — the one whole-instance body (review NEW-7 /
            // ruling 37: one home per mutation shape; TakeWholeInstance shares it).
            if (res.Ok || !string.IsNullOrEmpty(req.Ctx)) return;
            TakeBundleInstance(src, req, res, removeWhole);
            if (res.Ok)
            {
                res.Ctx = "boxtake";   // designed-in requirement #1: delivery + give-back route by ctx
                // Review NEW-8: the preservation guarantee is conditional on DELIVERY. A
                // Silent mirror (ctx "" by construction) never delivers — its res is
                // discarded at the Silent early-out, so the contents are NOT echoed anywhere
                // (unchanged from pre-fix: the borrower's native copy was nested-free; a
                // refusal here would DUPLICATE the bag instead). Name that honestly.
                if (req.Silent)
                    Plugin.Logger.LogInfo($"[{Tag(req.Container)}] mirror take of '{req.ItemName}' matched a BUNDLE — removed whole to mirror the native take; contents are not delivered on a mirror (pre-existing shape, F-2026-08-25-I).");
                else
                    Plugin.Logger.LogInfo($"[{Tag(req.Container)}] plain take of '{req.ItemName}' matched a BUNDLE — upgraded to whole-instance take, contents echoed (F-2026-08-25-I).");
            }
        }

        /// <summary>THE bundle matcher (one home): the first unsealed NESTED-BEARING instance
        /// matching name (paid-preference two-pass) with an instance-EXACT amount, taken whole
        /// via TakeWholeOf. Callers: TakeLoose phase B (the plain-take upgrade, which then stamps
        /// res.Ctx) and the bundlesell/bundlediscard ctx routes (removal-only; ctx untouched so
        /// the result routes to the credit branch, never to delivery).</summary>
        private static void TakeBundleInstance(System.Collections.Generic.List<CargoInstance>? src, StorageOpPayload req, StorageResPayload res,
                                               Action<CargoInstance> removeWhole)
        {
            if (src == null) return;
            for (int pass = 0; pass < 2 && !res.Ok; pass++)
                for (int c = 0; c < src.Count; c++)
                {
                    var ci = src[c];
                    if (ci == null) continue;
                    bool sealedCi = ci.IsSealed;
                    if (sealedCi) continue;
                    if (ci.nestedCargoInstances == null || ci.nestedCargoInstances.Count == 0) continue;
                    if (ci.itemName != req.ItemName) continue;
                    if (pass == 0 && ci.paid != req.Paid) continue;
                    if (ci.amount != req.Amount) continue;   // whole move — instance-exact (bundles are amount 1 by ConvertToCargoInstance)
                    TakeWholeOf(ci, res, removeWhole);
                    break;
                }
        }

        /// <summary>THE whole-instance mutation body (review NEW-7 / ruling 37 — one home):
        /// owner-truth echoes, nested contents through THE codec, whole removal via the
        /// container's own RemoveFromCargo. Callers own their MATCHING policy: TakeWholeInstance
        /// (exact-identity boxtake) and TakeLoose phase B (the bundle upgrade).</summary>
        private static void TakeWholeOf(CargoInstance ci, StorageResPayload res, Action<CargoInstance> removeWhole)
        {
            res.Paid = ci.paid; res.PricePerUnit = ci.pricePerUnit; res.Amount = ci.amount;
            res.Nested = EncodeNested(ci.nestedCargoInstances);   // ONE codec (R7/R11)
            removeWhole(ci);
            res.Ok = true; res.Reason = "";
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
                TakeWholeOf(ci, res, removeWhole);   // THE one whole-instance body (NEW-7)
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
                try { item.OnItemsInCargoUpdated()?.Invoke(); } catch { }   // the repaint driver — fires on echo replays too
                if (!_echoReplay)   // owner business tails — never against a replica (review 2026-08-25 MINOR-3)
                {
                    try { BusinessHelper.UpdateCustomerCapacity(reg); } catch { }
                    try { GlobalEvents.onBuildingRegistrationChange?.Invoke(reg.Address); } catch { }
                }
                res.Ok = true; res.Reason = "";
            }
            // else: slot gone/renamed/empty — res stays !Ok ("gone"); requester's replica was stale.
        }

        /// <summary>H-SELL-2 (verbal report 2026-09-05; user ruling: selling other players' stock stays ALLOWED, it must never mint
        /// money). The requester's item popup pressed SELL on a whole building item (a box/pallet with contents and attached
        /// children). The OWNER prices the tree from its authoritative copy — the same formula as ItemPanelUI.GetTotalSellingPrice
        /// (GetSellingPrice per item, children recursive) — and removes it through the game's own chokepoint
        /// (RemoveItemInstanceFromBuilding fires onInstanceRemoved → a loaded ItemController destroys itself). The seller is
        /// credited ONLY from res.Total on this verdict. Owner outside the building: registration-only removal.</summary>
        private static void SellWholeItem(BuildingRegistration reg, ItemInstance item, StorageOpPayload req, StorageResPayload res)
        {
            try
            {
                if (!TreeSellable(reg, item, out string why))
                {
                    res.Reason = "unsellable";
                    Plugin.Logger.LogInfo($"[BStore] itemsell refused for '{item.itemName}' on '{req.AddressKey}': {why} (owner-side eligibility — the seller's replica said otherwise; review r1 MAJOR-1).");
                    return;
                }
                float total = TotalSellingPrice(reg, item);
                int removed = RemoveItemTree(reg, item);
                if (removed <= 0) { res.Reason = "gone"; return; }
                res.Ok = true; res.Reason = ""; res.Count = 1; res.Total = total;
                try { if (!string.IsNullOrEmpty(item.itemName)) res.ItemName = item.itemName; } catch { }
                // Review r1 MINOR-4: the native Discard tail (BuildingManager.OnItemChanged via onItemDiscarded) is not reachable when
                // the owner is elsewhere — mirror the mod's set-stock refreshers + camera coverage instead.
                try { BusinessHelper.UpdateCustomerCapacity(reg); } catch { }
                try { if (reg.HasValidAddress) { BusinessHelper.UpdatePromotion(reg); reg.UpdateSecurityLevel(); } } catch { }
                try { BusinessSecurityHelper.UpdateCamerasCoverage(reg.Address); } catch { }
                try { GlobalEvents.onBuildingRegistrationChange?.Invoke(reg.Address); } catch { }
                Plugin.Logger.LogInfo($"[BStore] owner applied ITEMSELL '{res.ItemName}' ({removed} instance(s) incl. attached) on '{req.AddressKey}' for '{req.PlayerId}' → total ${total:F2} (owner-priced).");
            }
            catch (Exception ex) { res.Reason = "error"; Plugin.Logger.LogWarning($"[BStore] itemsell apply: {ex.Message}"); }
        }

        /// <summary>Owner-side mirror of the popup's own Sell rule (ItemPanelUI.cs:249: not cannotsell, grabbable, every cargo unit
        /// paid and unsealed), applied to the WHOLE tree — the seller judged it on a replica that can lag the owner (review r1
        /// MAJOR-1: GetWorth prices unpaid and sealed cargo too).</summary>
        private static bool TreeSellable(BuildingRegistration reg, ItemInstance item, out string why)
        {
            why = "";
            try
            {
                var def = item.ItemCached;
                if (def != null && def.HasTag(BigAmbitions.Tags.TagRef.Itemtag.cannotsell)) { why = "cannotsell tag"; return false; }
                if (def != null && !def.canBeGrabbed) { why = "not grabbable"; return false; }
                if (item.cargoInstances != null)
                    foreach (var ci in item.cargoInstances)
                        if (ci != null && (!ci.paid || ci.IsSealed)) { why = ci.IsSealed ? "sealed cargo" : "unpaid cargo"; return false; }
                if (item.stackedItems != null && reg.itemInstances != null)
                    foreach (var child in item.stackedItems)
                        if (child != null && reg.itemInstances.TryGetValue(child.childId, out var ci2) && ci2 != null && !TreeSellable(reg, ci2, out why))
                            return false;
                return true;
            }
            catch (Exception ex) { why = "check threw: " + ex.Message; return false; }
        }

        private static float TotalSellingPrice(BuildingRegistration reg, ItemInstance item)
        {
            float num = 0f;
            try { num = item.GetSellingPrice(); } catch { }
            try
            {
                if (item.stackedItems != null && reg.itemInstances != null)
                    foreach (var child in item.stackedItems)
                        if (child != null && reg.itemInstances.TryGetValue(child.childId, out var ci) && ci != null)
                            num += TotalSellingPrice(reg, ci);
            }
            catch { }
            return num;
        }

        /// <summary>Children first (native DiscardItemWithAttachedItems order), then the item. Mirrors the native Discard path
        /// WITHOUT touching BuildingManager's CURRENT building (the owner may be elsewhere): a loaded controller is detached from
        /// its parent and dropped from allItemControllers only when this machine is inside THIS building; the chokepoint's
        /// onInstanceRemoved destroys the object. Returns the number of instances removed from the registration.</summary>
        private static int RemoveItemTree(BuildingRegistration reg, ItemInstance item)
        {
            int n = 0;
            try
            {
                if (item.stackedItems != null && reg.itemInstances != null)
                    for (int i = item.stackedItems.Count - 1; i >= 0; i--)
                    {
                        var child = item.stackedItems[i];
                        if (child != null && reg.itemInstances.TryGetValue(child.childId, out var ci) && ci != null)
                            n += RemoveItemTree(reg, ci);
                    }
                bool inside = false;
                try { inside = BuildingManager.IsInsideBuilding && ReferenceEquals(InstanceBehavior<BuildingManager>.Instance?.buildingRegistration, reg); } catch { }
                if (inside)
                {
                    try
                    {
                        var ctrl = ItemHelper.GetItemControllerByID(item.id);
                        if (ctrl != null)
                        {
                            ctrl.RemoveFromParentPlaceableItem(updateWarningIcon: false);
                            InstanceBehavior<BuildingManager>.Instance.allItemControllers.Remove(ctrl);
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] itemsell controller detach: {ex.Message}"); }
                }
                try { item.RemoveFromWorkShifts(item.AddressCached); } catch { }
                string key = item.id?.ToString() ?? "";
                if (reg.itemInstances != null && key.Length > 0 && reg.itemInstances.ContainsKey(key))
                {
                    reg.RemoveItemInstanceFromBuilding(item);
                    n++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] itemsell remove tree: {ex.Message}"); }
            return n;
        }

        /// <summary>Round-47b — helper SELL/DISCARD stacks: remove up to Count identical non-sealed
        /// instances (name+amount+paid identity). ECHO POLICY (risk R2): res.Amount/Paid/PricePerUnit
        /// deliberately stay the REQUESTER's values — the sell credit is computed from them; only
        /// res.Count is owner truth. Never share an echo helper with the owner-truth branches.
        /// Generalized to BOTH containers 2026-08-25 (borrower trunk sell parity) — the removeWhole
        /// delegate carries the container's own RemoveFromCargo, exactly like TakeWholeInstance.</summary>
        private static void RemoveStackInstances(System.Collections.Generic.List<CargoInstance>? src, StorageOpPayload req, StorageResPayload res,
                                                 Action<CargoInstance> removeWhole)
        {
            int removed = 0;
            if (src != null)
                for (int c = src.Count - 1; c >= 0 && removed < req.Count; c--)
                {
                    var ci = src[c];
                    if (ci == null) continue;
                    bool sealedCi = ci.IsSealed;
                    if (sealedCi) continue;
                    // F-2026-08-25-H (user-approved): native grouping never lets a nested-bearing
                    // instance join a plain row (CargoItem.cs:42 requires nestedCargoInstances
                    // empty) — a routed sell/discard on a plain row must not consume a
                    // same-identity BUNDLE (contents destroyed, seller under-credited). With no
                    // plain match left the op refuses "gone" instead of guessing.
                    if (ci.nestedCargoInstances != null && ci.nestedCargoInstances.Count > 0) continue;
                    if (ci.itemName != req.ItemName || ci.amount != req.Amount || ci.paid != req.Paid) continue;
                    removeWhole(ci);
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
            if (!_echoReplay)   // an echo rollback is a replica-fuller-than-owner display case, not an owner refusal (MINOR-3)
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
                if (!_echoReplay)   // owner-voice log — a replica replay must not claim an owner action (MINOR-3)
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
                if (!_echoReplay)   // owner-voice log (MINOR-3)
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
                EchoBuildingReplica(res);   // instant repaint for the actor (user-approved 2026-08-25); self-gating, display-only
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

        // ── Accessor-side replica echo (user-approved 2026-08-25) ──
        // WHY: an owner's cargo-only interior push rides the ~12s volatile coalesce (round-280 S2,
        // the resend-storm fix), so a guest's own CONFIRMED take/put repainted their shelf up to
        // 12s late (field run 2026-08-25 — the stale panel rows caused 3 harmless double-sell
        // refusals). WHAT: replay the SAME engine mutation bodies against the guest's local replica
        // of the building item — the native cargo callbacks repaint the shelf instantly for the
        // actor; everyone else still waits for the coalesced push, which OVERWRITES this echo with
        // owner truth either way (convergent; an echo miss is display-only and heals ≤12s).
        // BUILDINGS ONLY: a vehicle replica is a ghost manifest, re-synced ~0.1s by MarkFleetDirty.
        // NOT echoed: setstock (native move semantics — ReturnToAShelf / shelf refill — too
        // stateful to replay; the push repaints it). Consume IS echoed (review 2026-08-25 MINOR-1
        // corrected the first cut's premise: the guest consume prefixes return false, so the native
        // decrement — FridgeController.ConsumeItem's ReduceFromCargo — never runs on the replica;
        // the stale row was the "phantom bite" window). One home per mutation shape (ruling 37):
        // the bodies below are the owner apply's own helpers; authorization, owner tails,
        // owner-voice logs (gated on _echoReplay) and result-sending are skipped.
        private static bool _echoReplay;   // main-thread scoped flag: shared bodies skip owner tails/logs while set
        internal static bool SuppressGuestForward;   // H-SELL-2: set while the seller takes down its replica copy at verdict — the guest removal forward (HousingPatches) must not re-send a remove the owner already applied

        private static void EchoBuildingReplica(StorageResPayload res)
        {
            try
            {
                if (res.Ctx == "itemsell") return;   // H-SELL-2: a whole-item sale — its verdict branch in OnTakeResult removes the replica copy; the cargo-shaped echo below would misread it as a loose take
                if (res.Container != ContainerBuilding || !res.Ok) return;   // structural gates — silent by design
                if (ReferenceEquals(res, _lastLocalApplyRes)) return;        // host applied THIS very object locally
                if (res.Op == OpSetStock) return;

                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                BuildingRegistration? reg = null;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == res.AddressKey) { reg = r; break; }
                if (reg == null)
                { Plugin.Logger.LogInfo($"[BStore] echo skipped: no local reg for '{res.AddressKey}'."); return; }
                // REVIEW 2026-08-25 MAJOR-1 — THE ownership guard. The identity check above only
                // covers the host's inline path; a CLIENT that both owns the container and sent the
                // op gets its verdict back DESERIALIZED (new object), and the echo would apply the
                // change a SECOND time to authoritative data (reachable: operator-vs-real-estate
                // ledger split; ownership-flip race). The echo is for REPLICAS only — skip anything
                // this machine authoritatively owns. TrulyMine is the same predicate the round-38d
                // cargo authority shield trusts (field-proven); its known transient (guests read
                // RentedByPlayer=true inside HousingFurniture.Enter) errs toward SKIPPING an echo —
                // a display-only miss the push heals, never a double apply.
                if (MergerFlip.TrulyMine(reg))
                {
                    // WARNING deliberately (review NEW-3): on a CLIENT this line is the MAJOR-1
                    // tripwire — it fires only when owner-resolution and the grant sets disagreed,
                    // the exact anomaly that would have double-applied before this guard existed.
                    Plugin.Logger.LogWarning($"[BStore] echo skipped: '{res.AddressKey}' is locally owned (authoritative copy already applied — owner-as-accessor anomaly).");
                    return;
                }
                if (reg.itemInstances == null) return;
                ItemInstance? item = null;
                foreach (var kv in reg.itemInstances)
                    if (kv.Value != null && (kv.Value.id?.ToString() ?? "") == res.ItemId) { item = kv.Value; break; }
                if (item == null)
                { Plugin.Logger.LogInfo($"[BStore] echo skipped: replica of '{res.AddressKey}' has no item {res.ItemId}."); return; }

                // Rebuild the op the owner applied; res2 is a throwaway verdict the shared bodies
                // can write into (the REAL res must keep its echoed owner-truth fields untouched —
                // Nested is defensively COPIED, never aliased into the live result).
                var req2 = new StorageOpPayload
                {
                    Container = res.Container, AddressKey = res.AddressKey, ItemId = res.ItemId,
                    PlayerId = res.PlayerId, Op = res.Op, Ctx = res.Ctx, ItemName = res.ItemName,
                    Amount = res.Amount, Paid = res.Paid, PricePerUnit = res.PricePerUnit,
                    Count = res.Count,
                    Nested = res.Nested == null ? new System.Collections.Generic.List<CargoNestedInfo>()
                                                : new System.Collections.Generic.List<CargoNestedInfo>(res.Nested),
                };
                var res2 = ResFrom(req2);
                try
                {
                    _echoReplay = true;
                    if (req2.Op == OpTake)
                    {
                        if (req2.Ctx == "stacksell" || req2.Ctx == "stackdiscard")
                            RemoveStackInstances(item.cargoInstances, req2, res2, (ci) => item.RemoveFromCargo(ci));
                        else if (req2.Ctx == "bundlesell" || req2.Ctx == "bundlediscard")
                            TakeBundleInstance(item.cargoInstances, req2, res2, (ci) => item.RemoveFromCargo(ci));
                        else if (req2.Ctx == "boxtake")
                            TakeWholeInstance(item.cargoInstances, req2, res2, (ci) => item.RemoveFromCargo(ci));
                        else if (req2.Ctx == "stationtake")
                            TakeStock(reg, item, req2, res2);
                        else   // "", consume, vehicletake, placereduce — the owner routed these through
                               // TakeLoose too. An owner-upgraded bundle take arrives with res.Ctx
                               // ALREADY "boxtake" (the stamp survives into req2) → routes to
                               // TakeWholeInstance above; this branch only sees genuinely-plain takes.
                            TakeLoose(item.cargoInstances, req2, res2, (ci, amt) => item.ReduceFromCargo(ci, amt),
                                      (ci) => item.RemoveFromCargo(ci));
                    }
                    else if (req2.Op == OpPut)
                    {
                        if (req2.Ctx == "producer" || req2.Ctx == "stationreturn")
                            PutIntoSingleSlot(item, req2, res2);
                        else
                        {
                            var ci = new CargoInstance(req2.ItemName, req2.Amount, req2.PricePerUnit, req2.Paid);
                            DecodeNestedInto(ci, req2.Nested);
                            if (item.TryToAddToCargo(ci)) res2.Ok = true;
                            else if (req2.Ctx == "return" || req2.Ctx == "boxreturn")
                            { item.AddToCargo(ci); res2.Ok = true; }   // mirror the owner's R9 force-landing
                            else RollbackPartialMerge(req2, req2.Amount - ci.amount, item.cargoInstances,
                                                      (s) => item.RemoveFromCargo(s), (s, amt) => item.ReduceFromCargo(s, amt));
                        }
                    }
                }
                finally { _echoReplay = false; }
                if (res2.Ok)
                    Plugin.Logger.LogInfo($"[BStore] echo applied {res.Op}/{(res.Ctx == "" ? "-" : res.Ctx)} {res.Amount}×{res.ItemName} on replica '{res.AddressKey}'.");
                else
                    Plugin.Logger.LogInfo($"[BStore] echo miss: replica had no match for {res.Op}/{res.Ctx} {res.Amount}×{res.ItemName} on '{res.AddressKey}' (display-only; owner push heals).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] replica echo: {ex.Message}"); }
        }

        /// <summary>H-SELL-2: the seller's replica copy of a sold item. Only while the seller is STILL inside that building (the
        /// controller detach needs the loaded interior); otherwise the next interior snapshot heals the replica. Uses the mod's own
        /// RemoveItemTree (review r1 MINOR-3: the native discard can defer into a HudConfirm and escape the flag asynchronously)
        /// with the guest removal forward suppressed — the owner already removed the tree.</summary>
        private static void RemoveSoldReplicaItem(StorageResPayload res)
        {
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                var reg = bm?.buildingRegistration;
                if (!BuildingManager.IsInsideBuilding || reg == null || GameStateReader.AddressKey(reg) != res.AddressKey)
                { Plugin.Logger.LogInfo($"[BStore] itemsell: seller no longer inside '{res.AddressKey}' — replica copy left to the next interior sync."); return; }
                ItemInstance? item = null;
                if (reg.itemInstances != null)
                    foreach (var kv in reg.itemInstances)
                        if (kv.Value != null && (kv.Value.id?.ToString() ?? "") == res.ItemId) { item = kv.Value; break; }
                if (item == null) { Plugin.Logger.LogInfo($"[BStore] itemsell: replica of '{res.AddressKey}' has no item {res.ItemId} (already synced away)."); return; }
                int n;
                SuppressGuestForward = true;
                try { n = RemoveItemTree(reg, item); }
                finally { SuppressGuestForward = false; }
                Plugin.Logger.LogInfo($"[BStore] itemsell: replica copy of {res.ItemId} removed locally ({n} instance(s), forward suppressed).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] itemsell replica removal: {ex.Message}"); }
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
                // Native tail for BOTH verdicts (OnConfirmSell :211 / OnDiscardClick :218): any UI
                // keyed to the cargo event refreshes on the clicker's machine (parity 2026-08-25).
                try { GameEvent.Invoke("ba:gameevent_itemcargochanged"); } catch { }
                return;
            }
            // H-SELL-2 (2026-09-05): whole-item sale from the item popup in a granted building — credit ONLY now, from the
            // owner-computed total, then take the replica copy down through the native discard (forward suppressed).
            if (res.Ctx == "itemsell")
            {
                if (!res.Ok)
                {
                    PassengerHud.Toast(res.Reason == "unsellable" ? "That can't be sold right now."   // user-approved wording 2026-09-05 (review r1 MAJOR-1/MINOR-7)
                                     : res.Reason == "denied"     ? "No access."
                                     :                              "Already gone.");
                    Plugin.Logger.LogInfo($"[BStore] itemsell refused ({res.Reason}) for {res.ItemId} on '{res.AddressKey}' — nothing credited.");
                    return;
                }
                try
                {
                    if (res.Total > 0f)
                    {
                        var data = new System.Collections.Generic.Dictionary<string, string> { { "itemSoldInfo", VehicleStoragePanel.Localize(res.ItemName ?? "") } };   // review r1 MINOR-5: the finance record shows the item's name, not its key
                        GameManager.ChangeMoneySafe(res.Total, new TransactionInfo("ba:transaction_itemsold", data));
                    }
                    Plugin.Logger.LogInfo($"[BStore] itemsell confirmed: '{res.ItemName}' ({res.ItemId}) on '{res.AddressKey}' → ${res.Total:F2} credited (owner-priced).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] itemsell credit: {ex.Message}"); }
                RemoveSoldReplicaItem(res);
                try { GameEvent.Invoke("ba:gameevent_itemcargochanged"); } catch { }
                return;
            }
            // Bundle sell/discard (user-approved 2026-08-25): removal-only, ONE filled bag.
            // Credit is the NATIVE basis — GetSellingPrice over the reconstructed instance WITH
            // its nested contents (GetWorth includes them; the plain stacksell's R2 basis is
            // nested-free by design, which is exactly why bundles needed their own route).
            if (res.Ctx == "bundlesell" || res.Ctx == "bundlediscard")
            {
                if (!res.Ok) { PassengerHud.Toast("Already gone."); return; }
                if (res.Ctx == "bundlesell")
                {
                    try
                    {
                        var priced = new CargoInstance(res.ItemName, res.Amount, res.PricePerUnit, res.Paid);
                        DecodeNestedInto(priced, res.Nested);   // ONE codec — contents priced in
                        float total = priced.GetSellingPrice();
                        var data = new System.Collections.Generic.Dictionary<string, string> { { "itemName", res.ItemName } };
                        GameManager.ChangeMoneySafe(total, new TransactionInfo("ba:transaction_itemsold", data));
                        Plugin.Logger.LogInfo($"[Store] bundle sell confirmed: {res.ItemName}×{res.Amount} + {res.Nested?.Count ?? 0} nested line(s) → ${total:F2} credited locally (nested-inclusive basis).");
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[{Tag(res.Container)}] bundle-sell credit: {ex.Message}"); }
                }
                try { GameEvent.Invoke("ba:gameevent_itemcargochanged"); } catch { }   // native tail, both verdicts
                return;
            }
            if (res.Ctx == "consume")
            {
                // The EATING (hunger effect) happened at click time (round-17 parity) — nothing to
                // deliver. The replica ROW, however, was never decremented locally (the guest
                // prefixes return false, suppressing the native ReduceFromCargo — review 2026-08-25
                // MINOR-1 corrected the first cut's "eaten in place" premise): the echo above now
                // reduces it on Ok, closing the phantom-bite stale-row window. A failed confirm is
                // the phantom-bite race itself: owner fridge unchanged, nothing lost; log only.
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
                        // "return" (R9 hardening, user-approved 2026-08-25): a give-back must be
                        // DISTINGUISHABLE from a normal deposit — the owner force-lands it and the
                        // accessor's confirm consumes nothing (see OnPutResult).
                        Op = OpPut, Ctx = res.Ctx == "boxtake" ? "boxreturn" : "return",
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
                // "return" (R9 hardening): a loose give-back is not a deposit — see the vehicle twin above.
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit,
                                               res.Ctx == "stationtake" ? "stationreturn" : "return");
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
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, 1, res.Paid, res.PricePerUnit, "return");
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
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, 1, res.Paid, res.PricePerUnit, "return");
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
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit, "return");
                Plugin.Logger.LogWarning($"[BStore] vehicletake Ok but '{res.ItemName}' has no vehicleType — returned to the owner (gave back {res.Amount}).");
                return;
            }
            if (PlayerHelper.IsHoldingItem || PlayerHelper.IsUsingVehicle)
            {
                // Hands filled during the round-trip — give it back rather than strand it (risk R9).
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit, "return");
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
                BuildingStorageSync.RequestPut(res.AddressKey, res.ItemId, res.ItemName, res.Amount, res.Paid, res.PricePerUnit, "return");
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
            if (res.Ctx == "return" || res.Ctx == "boxreturn")
            {
                // A give-back landing needs NOTHING locally — the item never entered this
                // machine's hands or truck, so the generic consume below must not run: it could
                // eat a SAME-NAME stack from whatever the accessor happens to be holding (hazard
                // spotted during the R9 hardening — give-backs previously rode ctx "" straight
                // into ConsumeSource). On !Ok the removed goods are stranded owner-side with no
                // local copy — exception-only now that the owner force-lands; log loud.
                if (!res.Ok)
                {
                    PassengerHud.Toast("Couldn't return the item.");
                    Plugin.Logger.LogWarning($"[{Tag(res.Container)}] give-back REFUSED ({res.Reason}) for {res.Amount}×{res.ItemName} — removed goods could not be returned (R9 tripwire).");
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
            if (res.Ctx == "wornHead" || res.Ctx == "wornHand" || res.Ctx == "wornPhone") { UnequipWornAfterStore(res); return; }   // wornPhone: 1.0 phone accessory (sweep-2 backlog find)
            // "wholeput" (parity 2026-08-26): the deposit moved a bag/sealed INSTANCE whole —
            // the source must leave whole too. The plain consume's sealed-skip would leave a
            // deposited sealed box ON the truck (duplication), and its box-contents branch
            // would mis-consume from inside a held container.
            ConsumeSource(res, wholeInstance: res.Ctx == "wholeput");
        }

        /// <summary>Stored OK → drop the deposited item from wherever it came from, and ONLY now:
        /// hands (held directly or as box content) → pushed hand-vehicle. One body for both
        /// containers — the two originals were duplicate implementations (round-12 #1b / B).</summary>
        private static void ConsumeSource(StorageResPayload res, bool wholeInstance = false)
        {
            try
            {
                var held = PlayerHelper.ItemInstanceInHands;
                if (held == null) { RemoveFromAccessorHandVehicle(res.ItemName, res.Amount, wholeInstance); return; }   // truck-sourced deposit
                if (held.itemName == res.ItemName) { PlayerHelper.ItemInstanceInHands = null; return; }   // held the item directly (a whole-put bag lands here — hands clear like native)
                if (wholeInstance) return;   // a whole-put never consumes from INSIDE a held container
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
        private static void RemoveFromAccessorHandVehicle(string itemName, int amount, bool wholeInstance = false)
        {
            try
            {
                var cur = VehicleHelper.GetCurrentVehicle();
                if (cur == null || cur.VehicleType == null || !cur.VehicleType.spawnInPlayerObject) return;
                var src = cur.cargoInstances;
                if (src == null) return;
                if (wholeInstance)
                {
                    // A whole-put deposit moved ONE sealed/bundle instance — remove exactly that
                    // shape (the plain loop below deliberately skips sealed, which would leave
                    // the deposited box on the truck = duplication). Instance-exact amount.
                    for (int i = 0; i < src.Count; i++)
                    {
                        var ci = src[i];
                        if (ci == null || ci.itemName != itemName || ci.amount != amount) continue;
                        bool sealedCi = ci.IsSealed;
                        bool bundleCi = ci.nestedCargoInstances != null && ci.nestedCargoInstances.Count > 0;
                        if (!sealedCi && !bundleCi) continue;
                        cur.RemoveFromCargo(ci);
                        return;
                    }
                    Plugin.Logger.LogWarning($"[Store] whole-put consume: no matching sealed/bundle {amount}×{itemName} on the truck (source changed mid-request).");
                    return;
                }
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
                var ci = res.Ctx == "wornHead" ? acc?.headAccessoryCargoInstance
                       : res.Ctx == "wornPhone" ? acc?.phoneAccessoryCargoInstance   // 1.0: phones are wearable
                       : acc?.handAccessoryCargoInstance;
                if (ci == null || ci.itemName != res.ItemName) return;   // changed in the request→confirm window — leave it
                PlayerHelper.PlayerController.UnEquipAccessory(ci);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[BStore] worn-consume: {ex.Message}"); }
        }
    }
}
