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
        public const byte OpTake     = 0;   // remove Amount of ItemName from the vehicle (accessor receives it)
        public const byte OpPut      = 1;   // add Amount of ItemName to the vehicle (from the accessor)
        public const byte OpMarkPaid = 2;   // flip Amount of unpaid ItemName stacks to paid (borrowed-cart checkout mirror)

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

        // Deposit what the accessor is CARRYING into the vehicle — hands first, else a pushed hand-truck/
        // flatbed (round-12 #1b: parity with the owner's "add to storage", which merges the CURRENT vehicle's
        // cargo into the car; the old hands-only read silently no-opped for a hand-truck). A carried item is
        // wrapped in a closedcardboardbox (ItemHelper.InitializeItemInHandsWithCargo), so we deposit the box's
        // CONTENTS (the real item), not the wrapper — otherwise the trunk would show "closed cardboard box".
        // NOTHING is removed locally on send — the source stack leaves hands/hand-truck only when the owner
        // CONFIRMS (OnResult), so a full trunk can never eat items.
        // One deposit INTENT can reach here by two routes at once (the PassengerRide walk-deposit CTA and
        // the native AddHeldItemToStorage redirect fired for the same box — 2026-07-07 field-confirmed
        // duplication: owner applied BOTH trust-based PUTs, +2 for 1 box). Owner-side has no "does the
        // sender still have it" check, so dedup at the single funnel both routes pass through. Keyed on
        // vehicle + CONTENT signature (user-refined 2026-07-07): the double-route always re-sends the
        // IDENTICAL source snapshot (both routes fire from one intent, same box in hand), while any
        // legitimate follow-up necessarily carries different content (the previous load left the source
        // on the owner's grant) — so no UI shape, however fast, can ever be throttled. Per-machine state:
        // each player dedups only their own sends; simultaneous deposits by two players never interact.
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
                    // Hands empty → pushed hand-vehicle as the source. Sealed stacks stay on the truck: our wire
                    // has no Sealed flag, so routing one through would silently UNSEAL it owner-side.
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
        // same change on the owner's REAL cart, fire-and-forget (Silent — the accessor consumed/placed
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

        // ── Owner side: apply the change to the REAL cargo (runs on whoever owns V) ──
        // Returns the verdict to relay back to the accessor. MAIN THREAD ONLY.

        public static VehicleCargoResPayload OwnerApply(VehicleCargoReqPayload req)
        {
            var res = new VehicleCargoResPayload
            {
                VehicleId = req.VehicleId, PlayerId = req.PlayerId, Op = req.Op,
                ItemName = req.ItemName, Amount = req.Amount, Paid = req.Paid, PricePerUnit = req.PricePerUnit,
                Ok = false, Reason = "gone", Silent = req.Silent,
            };
            try
            {
                if (PassengerSync.IsLocked(req.VehicleId) && !GrantSync.IsGranted(MPConfig.PlayerId, req.PlayerId)) { res.Reason = "locked"; return res; }   // locked storage opens only to a granted key-holder (authoritative backstop)
                var list = VehicleHelper.AllPlayerVehicles;
                if (list == null) return res;

                for (int i = 0; i < list.Count; i++)
                {
                    var inst = list[i]?.vehicleInstance;
                    if (inst == null || inst.id != req.VehicleId) continue;

                    if (req.Op == OpTake)
                    {
                        // First matching unsealed stack with enough on hand (first request wins).
                        // Prefer a stack whose paid flag matches the request (mirrored takes name the exact
                        // stack the borrower consumed natively); fall back to any match so UI takes keep working.
                        var src = inst.cargoInstances;
                        if (src != null)
                        {
                            for (int pass = 0; pass < 2 && !res.Ok; pass++)
                                for (int c = 0; c < src.Count; c++)
                                {
                                    var ci = src[c];
                                    if (ci == null || ci.IsSealed) continue;
                                    if (ci.itemName != req.ItemName) continue;   // match by item; carry the owner's REAL paid/price back (manifest is lossy)
                                    if (pass == 0 && ci.paid != req.Paid) continue;
                                    if (ci.amount < req.Amount) continue;
                                    res.Paid = ci.paid;
                                    res.PricePerUnit = ci.pricePerUnit;
                                    inst.ReduceFromCargo(ci, req.Amount);
                                    res.Ok = true; res.Reason = "";
                                    break;
                                }
                        }
                    }
                    else if (req.Op == OpMarkPaid)
                    {
                        // Borrowed-cart checkout mirror: the borrower paid at a register — flip the same
                        // amount of unpaid ItemName stacks to paid on the REAL cart (split when partial).
                        int remaining = req.Amount;
                        var src = inst.cargoInstances;
                        if (src != null)
                        {
                            for (int c = src.Count - 1; c >= 0 && remaining > 0; c--)
                            {
                                var ci = src[c];
                                if (ci == null || ci.IsSealed || ci.paid) continue;
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
                    Plugin.Logger.LogInfo($"[VStore] owner applied {(req.Op == OpTake ? "TAKE" : req.Op == OpMarkPaid ? "MARK-PAID" : "PUT")}{(req.Silent ? " (mirror)" : "")} {req.Amount}×{req.ItemName} on '{req.VehicleId}' for '{req.PlayerId}'.");
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
                // Mirrors of native actions (borrowed-cart shopping): the local side already consumed or
                // placed the item natively — a grant needs NOTHING here, and a failure is state drift the
                // next fleet re-sync repairs. Log failures so drift is visible; never toast or place.
                if (res.Silent)
                {
                    if (!res.Ok)
                        Plugin.Logger.LogWarning($"[VStore] mirror {(res.Op == OpTake ? "TAKE" : res.Op == OpMarkPaid ? "MARK-PAID" : "PUT")} {res.Amount}×{res.ItemName} on '{res.VehicleId}' FAILED owner-side ({res.Reason}) — replica reverts on next re-sync.");
                    return;
                }
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
                        if (held == null) { RemoveFromHandVehicle(res); return; }   // hand-truck-sourced deposit (round-12 #1b)
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

        // The owner confirmed a PUT that was sourced from the accessor's pushed hand-truck/flatbed (hands
        // were empty at send AND at confirm) — now, and only now, remove that stack from the truck. Uses the
        // native chokepoints (RemoveFromCargo invokes onItemsInCargoUpdated → the truck's box visuals update);
        // the truck is the accessor's OWN vehicle (no BAMP_ prefix), so the proxy guard passes it. (round-12 #1b)
        private static void RemoveFromHandVehicle(VehicleCargoResPayload res)
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
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[VStore] truck-consume: {ex.Message}"); }
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
                        && cur.id != "BAMP_" + vid                   // taking from the cart I'm PUSHING = "I want it in hands" —
                                                                     // placing it back would round-trip the take into a no-op
                        && cur.TryToAddToCargo(ci))
                        return;
                }
            }
            catch { }
            try
            {
                // Round-34 (probe-confirmed stack): a STALE ActiveVehicleId that resolves to NO local vehicle
                // (a ghost/proxy id left behind) makes the native hands pipeline NRE — ItemPanelUI.
                // SetUnpaidAmount sees IsUsingVehicle=true and derefs GetCurrentVehicle()=null — so every
                // take bounced back as "No room to carry that." The state is provably inconsistent (the
                // game itself can't run with it): repair it, then deliver normally.
                if (PlayerHelper.IsUsingVehicle && VehicleHelper.GetCurrentVehicle() == null)
                {
                    Plugin.Logger.LogWarning($"[VStore] stale ActiveVehicleId='{SaveGameManager.Current?.ActiveVehicleId}' resolves to no vehicle — cleared (take-to-hands repair).");
                    // NULL, never "" (round-37m, THE dead-shelf root): the native on-foot contract is
                    // ActiveVehicleId == null (every native exit writes null). ShelfCtaBehavior.GetCta checks
                    // `!= null` and dereferences GetCurrentVehicle().VehicleType — an empty string passes the
                    // check, resolves no vehicle, and NREs INSIDE the hover chain, killing OnIoEnter for every
                    // storage shelf while on foot. This very repair wrote "" and poisoned the save.
                    try { SaveGameManager.Current.ActiveVehicleId = null; } catch { }
                }
                if (PlayerHelper.ItemInstanceInHands == null)   // EMPTY hands only — never clobber what's held
                {
                    PlayerHelper.ItemInstanceInHands = ItemHelper.InitializeItemInHandsWithCargo(ci);
                    // Into HANDS → close the panel (mirrors the owner's native UI: you carry one, so it
                    // closes) — but ONLY the panel session this take belongs to: a LATE result from an
                    // earlier click must not close a freshly-reopened panel (round-35, probe-confirmed).
                    if (VehicleStoragePanel.IsOpenFor(vid)) VehicleStoragePanel.Close();
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
