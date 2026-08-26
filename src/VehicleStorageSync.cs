using System;
using UnityEngine;
using Helpers;
using Vehicles.VehicleTypes;
using BigAmbitions.Items;   // CargoInstance

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared vehicle storage — pure per-container SENDER facade since unification Stage B
    /// (2026-08, design: .modding/03-systems/storage-unification-2026-08.md). The request side
    /// (capacity pre-check, deposit source resolution + dedup, Silent mirrors) lives here; the
    /// wire is the unified StorageOp/StorageRes family (195/196, v16) and ALL apply/result logic
    /// is StorageSync, the one engine both containers share (ruling 37).
    ///
    /// Host-authoritative request/grant: the owner's machine is the sole authority on its own
    /// cargo — the take/put only commits once the owner confirms (first request wins; no
    /// optimistic local edit to roll back). Relay: accessor → host → owner → host → accessor.
    /// THREADING: the engine's OwnerApply()/OnResult() mutate game state and MUST run on the
    /// Unity main thread; the network dispatch marshals them (see MPServer/MPClient).
    /// </summary>
    public static class VehicleStorageSync
    {
        public const byte OpTake     = 0;   // remove Amount of ItemName from the vehicle (accessor receives it)
        public const byte OpPut      = 1;   // add Amount of ItemName to the vehicle (from the accessor)
        public const byte OpMarkPaid = 2;   // flip Amount of unpaid ItemName stacks to paid (borrowed-vehicle checkout mirror)

        // ── LOCAL byte → engine op string. The byte survives only as this facade's API surface
        // (MirrorToOwner's five call sites keep their byte argument); the WIRE carries the string
        // since Stage B. An unknown byte maps to "" and the engine refuses it "unsupported"
        // (ruling 39) — the D-1 sentinel, now enforced engine-side. ──
        private static string OpName(byte op) => op == OpTake     ? StorageSync.OpTake
                                               : op == OpPut      ? StorageSync.OpPut
                                               : op == OpMarkPaid ? StorageSync.OpMarkPaid
                                               :                    "";

        // ── Accessor side: start a take / put (unchanged) ────────────────────────

        public static void RequestTake(string realVehicleId, string ownerId, string itemName, int amount, bool paid, float price, string ctx = "")
        {
            // Respect the taker's capacity BEFORE asking the owner to remove it — the owner is authoritative
            // and drops the item on grant, so if we can't hold it we'd lose/overwrite it. Mirrors the host's
            // own ClickItem: a pushed hand-truck/flatbed with room, else EMPTY hands (you carry one).
            // ctx "boxtake" (Stage C / M5): the row is a SEALED box — the whole instance moves,
            // contents echoed, exactly like the owner's native ClickItem (which has no seal check).
            if (!AccessorCanHold()) { PassengerHud.Toast("No room to carry that."); return; }
            Send(OpTake, realVehicleId, ownerId, itemName, amount, paid, price, ctx: ctx);
        }

        public static void RequestPut(string realVehicleId, string ownerId, string itemName, int amount, bool paid, float price)
            => Send(OpPut, realVehicleId, ownerId, itemName, amount, paid, price);

        /// <summary>Sell/discard parity (user 2026-08-25): the borrower's panel routes the native
        /// card buttons — SELL removes the WHOLE grouped row (count = group size, native OnSellClick
        /// loop) and the MONEY credits the requester's own wallet at verdict time (native
        /// whoever-sells-pockets-it; risk R2 credit basis = Amount×Price×Count); DISCARD removes
        /// exactly ONE instance per click (count = 1 — native OnDiscardClick passes only
        /// firstCargoInstance; the sell/discard multiplicity asymmetry is native and deliberate).
        /// No capacity pre-check — nothing is delivered.</summary>
        public static void RequestStackOp(string realVehicleId, string itemName, int amount, bool paid, float price, int count, bool sell)
        {
            if (string.IsNullOrEmpty(realVehicleId) || string.IsNullOrEmpty(itemName) || count <= 0 || amount <= 0) return;
            StorageSync.SendOp(new StorageOpPayload
            {
                Container = StorageSync.ContainerVehicle, VehicleId = realVehicleId, PlayerId = MPConfig.PlayerId,
                Op = StorageSync.OpTake, Ctx = sell ? "stacksell" : "stackdiscard",
                ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price, Count = count,
            });
        }

        // Deposit what the accessor is CARRYING into the vehicle — hands first, else a pushed hand-truck/
        // flatbed (round-12 #1b). A carried item is wrapped in a closedcardboardbox, so we deposit the box's
        // CONTENTS (the real item), not the wrapper. NOTHING is removed locally on send — the source stack
        // leaves hands/hand-truck only when the owner CONFIRMS (OnResult), so a full trunk can never eat items.
        // One deposit INTENT can reach here by two routes at once (the PassengerRide walk-deposit CTA and
        // the native AddHeldItemToStorage redirect fired for the same box — 2026-07-07 field-confirmed
        // duplication). Dedup at the single funnel both routes pass through, keyed on vehicle + CONTENT
        // signature (risk R6: keep this construction byte-identical). Per-machine state.
        private static string _lastDepositSig = "";
        private static float  _lastDepositAt  = -999f;
        private const  float  DepositDedupSeconds = 2.5f;

        public static void RequestDeposit(string realVehicleId, string ownerId)
        {
            try
            {
                // Resolve the source FIRST (hands-box contents / bare held item / pushed hand-truck stacks),
                // so the dedup can sign exactly what would be sent.
                var toSend = new System.Collections.Generic.List<CargoInstance>();
                var held = PlayerHelper.ItemInstanceInHands;
                if (held != null)
                {
                    var contents = held.cargoInstances;
                    if (contents != null && contents.Count > 0)
                    {
                        foreach (var c in new System.Collections.Generic.List<CargoInstance>(contents))
                            if (c != null && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                                toSend.Add(c);
                    }
                    else   // not a container — deposit the held item itself
                    {
                        var c = held.ConvertToCargoInstance();
                        if (c != null && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                            toSend.Add(c);
                    }
                }
                else
                {
                    // Hands empty → pushed hand-vehicle as the source. Sealed stacks stay on the truck
                    // FOR NOW: the v16 wire CAN carry nested contents (the one codec), but wiring the
                    // deposit path for whole sealed boxes is Stage C work — an unwired send here would
                    // still strip them (F-2026-08-25-F: IsSealed re-derives fine; contents are the loss).
                    var cur = VehicleHelper.GetCurrentVehicle();
                    if (cur == null || cur.VehicleType == null || !cur.VehicleType.spawnInPlayerObject) return;
                    var src = cur.cargoInstances;
                    if (src == null || src.Count == 0) { PassengerHud.Toast("Nothing to store."); return; }
                    foreach (var c in new System.Collections.Generic.List<CargoInstance>(src))
                        if (c != null && !c.IsSealed && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                            toSend.Add(c);
                }
                if (toSend.Count == 0) return;

                var sb = new System.Text.StringBuilder(realVehicleId).Append('|');
                foreach (var c in toSend)
                    sb.Append(c.itemName).Append('=').Append(c.amount).Append('=').Append(c.paid ? '1' : '0').Append(';');
                string sig = sb.ToString();
                if (sig == _lastDepositSig && Time.unscaledTime - _lastDepositAt < DepositDedupSeconds)
                {
                    Plugin.Logger.LogInfo($"[VStore] duplicate deposit for '{realVehicleId}' suppressed ({Time.unscaledTime - _lastDepositAt:F1}s after the first, identical content — double-routed intent).");
                    return;
                }
                _lastDepositSig = sig; _lastDepositAt = Time.unscaledTime;

                foreach (var c in toSend)
                    Send(OpPut, realVehicleId, ownerId, c.itemName, c.amount, c.paid, c.pricePerUnit);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] RequestDeposit: {ex.Message}"); }
        }

        // Can the accessor actually receive a taken item right now? Pushed hand-vehicle with a free slot,
        // otherwise empty hands. (More restrictive than merging into a held box — matches "hands hold one".)
        private static bool AccessorCanHold()
        {
            try
            {
                if (PlayerHelper.IsUsingVehicle)
                {
                    var cur = VehicleHelper.GetCurrentVehicle();
                    if (cur != null && cur.VehicleType != null && cur.VehicleType.spawnInPlayerObject && cur.VehicleType.maxCargoCapacity > 0)
                    {
                        var cargo = cur.GetCargoInstances();
                        return cargo == null || cargo.Count < cur.VehicleType.maxCargoCapacity;
                    }
                }
                return PlayerHelper.ItemInstanceInHands == null;   // on foot → only with empty hands
            }
            catch { return false; }
        }

        // ownerId is NO LONGER on the wire — ruling 38 landed STRONGER than written: the claim is
        // not "demoted to a logged cross-check", it is REMOVED from the wire entirely (the host
        // resolves the owner from PassengerSync.OwnerOf; there is nothing authoritative on the
        // sender side to cross-check against). The parameter survives only so the many call sites
        // stay untouched; it is deliberately unused.
        private static void Send(byte op, string vid, string ownerId, string itemName, int amount, bool paid, float price, bool silent = false, string ctx = "")
        {
            if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(itemName) || amount <= 0)
                return;
            _ = ownerId;
            StorageSync.SendOp(new StorageOpPayload
            {
                Container = StorageSync.ContainerVehicle, VehicleId = vid, PlayerId = MPConfig.PlayerId,
                Op = OpName(op), Ctx = ctx, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price, Silent = silent,
            });
        }

        /// <summary>Bundle sell/discard (user-approved 2026-08-25): remove ONE filled bag whole —
        /// on a sell the money credits the requester at the NATIVE nested-inclusive basis
        /// (computed from the owner's echoed contents at verdict time). Removal-only, no
        /// capacity pre-check, no delivery.</summary>
        public static void RequestBundleOp(string realVehicleId, string itemName, int amount, bool paid, float price, bool sell)
        {
            if (string.IsNullOrEmpty(realVehicleId) || string.IsNullOrEmpty(itemName) || amount <= 0) return;
            StorageSync.SendOp(new StorageOpPayload
            {
                Container = StorageSync.ContainerVehicle, VehicleId = realVehicleId, PlayerId = MPConfig.PlayerId,
                Op = StorageSync.OpTake, Ctx = sell ? "bundlesell" : "bundlediscard",
                ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price, Count = 1,
            });
        }

        /// <summary>v17 (proposal 2): ask the owner for the trunk's FULL cargo detail — fired by
        /// the panel on open and on manifest movement while open. Event-driven; the answer feeds
        /// display only (StorageSync.OnTrunkDetail → panel), never game state.</summary>
        public static void RequestTrunkDetail(string realVehicleId, string sig)
        {
            if (string.IsNullOrEmpty(realVehicleId)) return;
            StorageSync.DebugWireCheck();   // MINOR-G: a detail-only session still self-tests its wire
            var req = new TrunkDetailReqPayload { VehicleId = realVehicleId, PlayerId = MPConfig.PlayerId, Sig = sig ?? "" };
            if (MPServer.IsRunning) MPServer.HandleTrunkDetailReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.TrunkDetailReq, MPConfig.PlayerId, req));
        }

        // ── Borrowed-cart shopping (Option A, user-approved 2026-07-07) ──────────────────────────
        // MIRROR of a native cargo mutation that already ran on the possessed pushed proxy: replay the
        // same change on the owner's REAL vehicle, fire-and-forget (Silent — the accessor consumed/placed
        // the item natively; OnResult must not double-place, clear hands, or toast). The next fleet
        // re-sync overwrites the replica with the owner's truth, which now matches — convergent.
        internal static void MirrorToOwner(byte op, VehicleInstance proxyInst, string itemName, int amount, bool paid, float price)
        {
            try
            {
                if (proxyInst == null || amount <= 0 || string.IsNullOrEmpty(itemName)) return;
                string realVid = proxyInst.id != null && proxyInst.id.StartsWith("BAMP_") ? proxyInst.id.Substring(5) : proxyInst.id;
                // Ruling 38 retired the old "no owner known → mirror LOST" failure: the HOST
                // resolves the owner now, so a mirror routes even when this machine's owner map
                // is momentarily cold (the fleet-note race that used to lose them).
                Send(op, realVid, ownerId: "", itemName, amount, paid, price, silent: true);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] MirrorToOwner: {ex.Message}"); }
        }
    }
}
