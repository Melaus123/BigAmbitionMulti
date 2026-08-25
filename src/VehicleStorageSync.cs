using System;
using UnityEngine;
using Helpers;
using Vehicles.VehicleTypes;
using BigAmbitions.Items;   // CargoInstance

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared vehicle storage — WIRE ADAPTER since unification Stage A (2026-08-25, design:
    /// .modding/03-systems/storage-unification-2026-08.md). The request side (capacity pre-check,
    /// deposit source resolution + dedup, Silent mirrors) lives here unchanged; the owner-apply
    /// and result handling moved VERBATIM into StorageSync, the one engine both containers share
    /// (ruling 37). The VehicleCargoReq/Res wire (125/126) is unchanged in Stage A; Stage B
    /// replaces it with the unified StorageOp/Res family and this adapter collapses.
    ///
    /// Host-authoritative request/grant: the owner's machine is the sole authority on its own
    /// cargo — the take/put only commits once the owner confirms (first request wins; no
    /// optimistic local edit to roll back). Relay: accessor → host → owner → host → accessor.
    /// THREADING: OwnerApply()/OnResult() mutate game state and MUST run on the Unity main
    /// thread; the network dispatch marshals them (see MPServer/MPClient).
    /// </summary>
    public static class VehicleStorageSync
    {
        public const byte OpTake     = 0;   // remove Amount of ItemName from the vehicle (accessor receives it)
        public const byte OpPut      = 1;   // add Amount of ItemName to the vehicle (from the accessor)
        public const byte OpMarkPaid = 2;   // flip Amount of unpaid ItemName stacks to paid (borrowed-vehicle checkout mirror)

        // ── wire byte ↔ engine op string (the byte space COLLIDES with the building channel's —
        // 2 means MarkPaid here, SetStock there — which is exactly why the engine's op is a string).
        // Purity review D-1: an UNKNOWN byte maps to the "" sentinel and is refused "unsupported"
        // (ruling 39) — HEAD's if/else-if fallthrough silently ran PUT for it. ──
        private static string OpName(byte op) => op == OpTake     ? StorageSync.OpTake
                                               : op == OpPut      ? StorageSync.OpPut
                                               : op == OpMarkPaid ? StorageSync.OpMarkPaid
                                               :                    "";
        private static byte OpByte(string op) => op == StorageSync.OpTake ? OpTake
                                               : op == StorageSync.OpPut  ? OpPut
                                               :                            OpMarkPaid;

        private static StorageSync.StorageOpData ToEngine(VehicleCargoReqPayload req) => new()
        {
            Container = StorageSync.ContainerVehicle, VehicleId = req.VehicleId,
            PlayerId = req.PlayerId, Op = OpName(req.Op), ItemName = req.ItemName,
            Amount = req.Amount, Paid = req.Paid, PricePerUnit = req.PricePerUnit, Silent = req.Silent,
        };

        private static StorageSync.StorageResData ToEngine(VehicleCargoResPayload res) => new()
        {
            Container = StorageSync.ContainerVehicle, VehicleId = res.VehicleId,
            PlayerId = res.PlayerId, Op = OpName(res.Op), ItemName = res.ItemName,
            Amount = res.Amount, Paid = res.Paid, PricePerUnit = res.PricePerUnit,
            Silent = res.Silent, Ok = res.Ok, Reason = res.Reason,
        };

        private static VehicleCargoResPayload ToWire(StorageSync.StorageResData res) => new()
        {
            VehicleId = res.VehicleId, PlayerId = res.PlayerId, Op = OpByte(res.Op),
            ItemName = res.ItemName, Amount = res.Amount, Paid = res.Paid,
            PricePerUnit = res.PricePerUnit, Ok = res.Ok, Reason = res.Reason, Silent = res.Silent,
        };

        // ── Owner / accessor seams (MAIN THREAD ONLY) — engine delegation ────────
        public static VehicleCargoResPayload OwnerApply(VehicleCargoReqPayload req)
        {
            if (OpName(req.Op).Length == 0)   // off-contract byte: refuse without touching the engine
            {
                Plugin.Logger.LogWarning($"[VStore] unknown op byte {req.Op} from '{req.PlayerId}' — refused (unsupported).");
                return new VehicleCargoResPayload
                {
                    VehicleId = req.VehicleId, PlayerId = req.PlayerId, Op = req.Op, ItemName = req.ItemName,
                    Amount = req.Amount, Paid = req.Paid, PricePerUnit = req.PricePerUnit,
                    Ok = false, Reason = "unsupported", Silent = req.Silent,
                };
            }
            return ToWire(StorageSync.OwnerApply(ToEngine(req)));
        }

        public static void OnResult(VehicleCargoResPayload res)
            => StorageSync.OnResult(ToEngine(res));

        // ── Accessor side: start a take / put (unchanged) ────────────────────────

        public static void RequestTake(string realVehicleId, string ownerId, string itemName, int amount, bool paid, float price)
        {
            // Respect the taker's capacity BEFORE asking the owner to remove it — the owner is authoritative
            // and drops the item on grant, so if we can't hold it we'd lose/overwrite it. Mirrors the host's
            // own ClickItem: a pushed hand-truck/flatbed with room, else EMPTY hands (you carry one).
            if (!AccessorCanHold()) { PassengerHud.Toast("No room to carry that."); return; }
            Send(OpTake, realVehicleId, ownerId, itemName, amount, paid, price);
        }

        public static void RequestPut(string realVehicleId, string ownerId, string itemName, int amount, bool paid, float price)
            => Send(OpPut, realVehicleId, ownerId, itemName, amount, paid, price);

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
                    // Hands empty → pushed hand-vehicle as the source. Sealed stacks stay on the truck: the
                    // legacy vehicle wire has no Nested band, so routing one through would silently DELETE
                    // its contents owner-side (F-2026-08-25-F — IsSealed itself is type-derived and would
                    // re-derive fine; the CONTENTS are what the wire cannot carry until Stage B).
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

        private static void Send(byte op, string vid, string ownerId, string itemName, int amount, bool paid, float price, bool silent = false)
        {
            if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(itemName) || amount <= 0)
                return;
            var req = new VehicleCargoReqPayload
            {
                VehicleId = vid, OwnerId = ownerId, PlayerId = MPConfig.PlayerId,
                Op = op, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price, Silent = silent,
            };
            // Host: hand straight to the broker. Client: send to the host, which forwards to the owner.
            if (MPServer.IsRunning) MPServer.HandleVehicleCargoReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.VehicleCargoReq, MPConfig.PlayerId, req));
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
                string owner = VehicleManager.OwnerIdFor(realVid);
                if (string.IsNullOrEmpty(owner))
                {
                    Plugin.Logger.LogWarning($"[VStore] mirror {op} {amount}×{itemName} on '{realVid}': no owner known — mirror LOST (re-sync will revert the local change).");
                    return;
                }
                Send(op, realVid, owner, itemName, amount, paid, price, silent: true);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] MirrorToOwner: {ex.Message}"); }
        }
    }
}
