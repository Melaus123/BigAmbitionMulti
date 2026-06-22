using System;
using UnityEngine;
using Helpers;
using Vehicles.VehicleTypes;
using BigAmbitions.Items;   // CargoInstance

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared vehicle storage — lets a non-owner take from (and put into) another player's UNLOCKED
    /// vehicle storage. Host-authoritative request/grant: the owner's machine is the sole authority on
    /// its own cargo, so the take/put only commits once the owner confirms it. First request wins, so
    /// there is no optimistic local edit to roll back (the take item is delivered to the accessor only
    /// on the owner's grant; nothing duplicates).
    ///
    /// Relay path (host is the broker):
    ///   accessor → host → owner (applies to REAL cargo) → host → accessor (places via the game's own
    ///   logic: into the accessor's pushed hand-truck/flatbed if any with room, else their hands).
    ///
    /// THREADING: OwnerApply() and OnResult() mutate game state (cargoInstances, items in hand) and
    /// MUST be invoked on the Unity main thread. The network dispatch runs off-thread, so callers there
    /// marshal these onto the main thread (see MPServer/MPClient).
    /// </summary>
    public static class VehicleStorageSync
    {
        public const byte OpTake = 0;   // remove Amount of ItemName from the vehicle (accessor receives it)
        public const byte OpPut  = 1;   // add Amount of ItemName to the vehicle (from the accessor)

        // ── Accessor side: start a take / put ────────────────────────────────────

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

        // Deposit what's IN THE PLAYER'S HANDS into the vehicle. A carried item is wrapped in a
        // closedcardboardbox (ItemHelper.InitializeItemInHandsWithCargo), so we deposit the box's CONTENTS
        // (the real item), not the wrapper — otherwise the trunk would show "closed cardboard box".
        public static void RequestDeposit(string realVehicleId, string ownerId)
        {
            try
            {
                var held = PlayerHelper.ItemInstanceInHands;
                if (held == null) return;
                var contents = held.cargoInstances;
                if (contents != null && contents.Count > 0)
                {
                    var snapshot = new System.Collections.Generic.List<CargoInstance>(contents);
                    foreach (var c in snapshot)
                        if (c != null && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                            Send(OpPut, realVehicleId, ownerId, c.itemName, c.amount, c.paid, c.pricePerUnit);
                }
                else   // not a container — deposit the held item itself
                {
                    var c = held.ConvertToCargoInstance();
                    if (c != null && !string.IsNullOrEmpty(c.itemName) && c.amount > 0)
                        Send(OpPut, realVehicleId, ownerId, c.itemName, c.amount, c.paid, c.pricePerUnit);
                }
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

        private static void Send(byte op, string vid, string ownerId, string itemName, int amount, bool paid, float price)
        {
            if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(itemName) || amount <= 0)
                return;
            var req = new VehicleCargoReqPayload
            {
                VehicleId = vid, OwnerId = ownerId, PlayerId = MPConfig.PlayerId,
                Op = op, ItemName = itemName, Amount = amount, Paid = paid, PricePerUnit = price,
            };
            // Host: hand straight to the broker. Client: send to the host, which forwards to the owner.
            if (MPServer.IsRunning) MPServer.HandleVehicleCargoReq(req, MPConfig.PlayerId);
            else                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.VehicleCargoReq, MPConfig.PlayerId, req));
        }

        // ── Owner side: apply the change to the REAL cargo (runs on whoever owns V) ──
        // Returns the verdict to relay back to the accessor. MAIN THREAD ONLY.

        public static VehicleCargoResPayload OwnerApply(VehicleCargoReqPayload req)
        {
            var res = new VehicleCargoResPayload
            {
                VehicleId = req.VehicleId, PlayerId = req.PlayerId, Op = req.Op,
                ItemName = req.ItemName, Amount = req.Amount, Paid = req.Paid, PricePerUnit = req.PricePerUnit,
                Ok = false, Reason = "gone",
            };
            try
            {
                if (PassengerSync.IsLocked(req.VehicleId)) { res.Reason = "locked"; return res; }   // only UNLOCKED storage is shareable (authoritative backstop)
                var list = VehicleHelper.AllPlayerVehicles;
                if (list == null) return res;

                for (int i = 0; i < list.Count; i++)
                {
                    var inst = list[i]?.vehicleInstance;
                    if (inst == null || inst.id != req.VehicleId) continue;

                    if (req.Op == OpTake)
                    {
                        // First matching unsealed stack with enough on hand (first request wins).
                        var src = inst.cargoInstances;
                        if (src != null)
                        {
                            for (int c = 0; c < src.Count; c++)
                            {
                                var ci = src[c];
                                if (ci == null || ci.IsSealed) continue;
                                if (ci.itemName != req.ItemName) continue;   // match by item; carry the owner's REAL paid/price back (manifest is lossy)
                                if (ci.amount < req.Amount) continue;
                                res.Paid = ci.paid;
                                res.PricePerUnit = ci.pricePerUnit;
                                inst.ReduceFromCargo(ci, req.Amount);
                                res.Ok = true; res.Reason = "";
                                break;
                            }
                        }
                    }
                    else // OpPut — capacity-checked by the game's own TryToAddToCargo
                    {
                        var ci = new CargoInstance(req.ItemName, req.Amount, req.PricePerUnit, req.Paid);
                        if (inst.TryToAddToCargo(ci)) { res.Ok = true; res.Reason = ""; }
                        else res.Reason = "full";
                    }
                    break;   // found the vehicle — done either way
                }
                // The cargo change re-syncs to every ghost through VehicleManager's normal fleet broadcast.
                if (res.Ok)
                    Plugin.Logger.LogInfo($"[VStore] owner applied {(req.Op == OpTake ? "TAKE" : "PUT")} {req.Amount}×{req.ItemName} on '{req.VehicleId}' for '{req.PlayerId}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] OwnerApply: {ex.Message}"); res.Ok = false; res.Reason = "error"; }
            return res;
        }

        // ── Accessor side: the owner's verdict came back ─────────────────────────
        // MAIN THREAD ONLY.

        public static void OnResult(VehicleCargoResPayload res)
        {
            try
            {
                if (res.Op == OpTake)
                {
                    if (!res.Ok)
                    {
                        PassengerHud.Toast(res.Reason == "locked" ? "Vehicle locked."
                                         : res.Reason == "full"   ? "No room."
                                         :                          "Already taken.");
                        return;
                    }
                    var ci = new CargoInstance(res.ItemName, res.Amount, res.PricePerUnit, res.Paid);
                    PlaceForAccessor(res.VehicleId, ci);
                }
                else // OpPut
                {
                    if (!res.Ok) { PassengerHud.Toast(res.Reason == "full" ? "Storage full." : "Couldn't store."); return; }
                    // Stored OK → drop it from the accessor's hands. The held item is usually a closedcardboardbox
                    // carrying the real item, so clear when the deposited item is what we hold — directly OR as its
                    // content. (Guards against a swap in the request→grant window by matching the item name.)
                    try
                    {
                        var held = PlayerHelper.ItemInstanceInHands;
                        if (held == null) return;
                        if (held.itemName == res.ItemName) { PlayerHelper.ItemInstanceInHands = null; return; }   // held the item directly
                        // Remove ONLY the content that actually went in; keep anything a near-full trunk refused
                        // (its OpPut comes back !Ok and never reaches here), so partial deposits never drop items.
                        var contents = held.cargoInstances;
                        if (contents != null)
                        {
                            for (int i = 0; i < contents.Count; i++)
                                if (contents[i] != null && contents[i].itemName == res.ItemName) { contents.RemoveAt(i); break; }
                            if (contents.Count == 0) PlayerHelper.ItemInstanceInHands = null;   // box emptied → drop it
                        }
                    }
                    catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] put-consume: {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] OnResult: {ex.Message}"); }
        }

        // Place a taken item exactly like single-player: into the pushed hand-truck/flatbed if the accessor
        // is using one with room, otherwise into EMPTY hands. NEVER overwrite a held item (that deletes it);
        // if there is genuinely no room (a rare race after the capacity pre-check), give it back so the
        // owner's cargo is made whole — never silently drop, overwrite, or force-combine.
        private static void PlaceForAccessor(string vid, CargoInstance ci)
        {
            try
            {
                if (PlayerHelper.IsUsingVehicle)
                {
                    var cur = VehicleHelper.GetCurrentVehicle();
                    if (cur != null && cur.VehicleType != null
                        && cur.VehicleType.spawnInPlayerObject       // a pushed hand-vehicle (hand-truck / flatbed), not a car
                        && cur.VehicleType.maxCargoCapacity > 0
                        && cur.TryToAddToCargo(ci))
                        return;
                }
            }
            catch { }
            try
            {
                if (PlayerHelper.ItemInstanceInHands == null)   // EMPTY hands only — never clobber what's held
                {
                    PlayerHelper.ItemInstanceInHands = ItemHelper.InitializeItemInHandsWithCargo(ci);
                    return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] PlaceForAccessor hands: {ex.Message}"); }
            // No room (race): return the item to the owner so it isn't lost.
            try
            {
                var owner = VehicleManager.OwnerIdFor(vid);
                if (!string.IsNullOrEmpty(owner)) RequestPut(vid, owner, ci.itemName, ci.amount, ci.paid, ci.pricePerUnit);
                Plugin.Logger.LogInfo($"[VStore] no room for taken {ci.amount}×{ci.itemName} — returned to '{vid}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VStore] give-back: {ex.Message}"); }
            PassengerHud.Toast("No room to carry that.");
        }
    }
}
