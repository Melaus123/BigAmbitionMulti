using System;
using System.Collections.Generic;
using BigAmbitions.Items;                 // CargoInstance, ItemAmountTarget, ItemType
using Buildings;                          // BuildingRegistration
using Buildings.Factory;                  // FactoryExport
using Buildings.Office.Headquarters;      // LogisticsManagerPlan, BusinessDeliveryInfo
using Entities;                           // Warehouse, DeliveryTransaction, DeliveryItem, LogisticsManagerPlanDestination
using HarmonyLib;                         // AccessTools - the plan's OWN private phone report is reused, never re-written
using Helpers;                            // BuildingHelper, BusinessHelper, ProductMarketHelper

namespace BigAmbitionsMP
{
    /// <summary>
    /// MERGER PHASE 4c PART 2 - CROSS-MEMBER DELIVERIES (D20-7, 2026-09-12).
    ///
    /// THE PROBLEM. One logistics leg is a two-ended write: the native chain
    /// LogisticsManagerPlan.DeliverDestination (LogisticsManagerPlan.cs:74) -&gt;
    /// LogisticsManagerPlanDestination.ProcessStockTarget (:124-176) READS the destination's stock
    /// (:159 CountTotalResourcesInStock), WITHDRAWS from the warehouse's own cargo (:166
    /// BuildingHelper.WithdrawFromCargo) and PLACES the goods on the destination's shelves (:176/:182
    /// ItemHelper.DeliverCargoToBuilding). In a merged company those two buildings can be run by two
    /// different machines, and a replica cannot do the other end's half: its
    /// CountTotalResourcesInStock reads 0 or stale (SharedShopStock.cs:10-18) and its interior is not
    /// the live one. Until now the leg was simply REFUSED (the stop-gap in
    /// MPPatches.Patch_LogisticsPlanLeg_MergerGate).
    ///
    /// THE SHAPE. Each native effect is performed ONCE, natively, on the machine that physically holds
    /// the object (design read Q4), and the two machines are joined by five legs of one wire type
    /// (MessageType.CargoTransfer) with the HOST as the authority in the middle:
    ///
    ///   C1 TAKE-OVER  the leg gate's mixed-owner branch starts a transfer instead of refusing.
    ///   C2 NEED       source -&gt; host -&gt; the destination's runner: "how much of each target do you
    ///                 still need?" - ProcessStockTarget's own arithmetic (target minus what is
    ///                 really on the shelves), run where the shelves are. No answer (nobody runs the
    ///                 destination) = the transfer is refused and NOTHING is withdrawn.
    ///   C3 WITHDRAW   the source withdraws exactly the answered amounts with the game's own
    ///                 WithdrawFromCargo, books its own side (negative delivery transaction), and
    ///                 hands the host the in-transit record, which the host persists in the mod's
    ///                 per-slot manifest.
    ///   C4 DELIVER    the destination's runner rebuilds the cargo and runs the game's own
    ///                 DeliverCargoToBuilding with the two native filters, books its positive delivery
    ///                 transaction, tops its shelves up, and acks the per-item REMAINDER.
    ///   C5 RETURN     the source puts the remainder back with ReturnToCargo and raises the game's own
    ///                 undelivered-items phone report. NO NEW ON-SCREEN TEXT anywhere on this path.
    ///   C6 FAILSAFES  the host's table survives a restart (manifest), re-routes a deliver once when the
    ///                 destination's runner changed, and - after a game day with no ack and ONE last deliver
    ///                 attempt - gives a record that NEVER acknowledged back WHOLE. A record that DID
    ///                 acknowledge is held instead, and its ack re-offered every sweep.
    ///   C7 CLOSED     r2 (review F3): the machine that RUNS the source answers every ack/return with
    ///                 "closed" - a sixth Action on the same wire type - and only that drops the host's
    ///                 record. Every step is idempotent by TransferId AND survives a restart: the delivered
    ///                 ids (destination) and the closed ids (source) are written to cargo-marks.bamp.json
    ///                 beside the manifest, because a member's manifest is a mirror of the host's.
    ///
    /// THE EXPORT LEG IS DIFFERENT, AND THE CODE DECIDES IT: ProcessStockTarget's export branch
    /// (isFactory + an "ba:businesstype_importexport" destination, LogisticsManagerPlan.cs:85) never
    /// reads the destination's stock and never calls DeliverCargoToBuilding at all - it withdraws the
    /// whole target, prices it at ProductMarketHelper.GetProductExportPrice and books factoryExports
    /// plus the export income. Nothing crosses to the other machine, so an export leg needs no need
    /// query, no in-transit record and no deliver leg: the source runs it whole, exactly as native
    /// does, and books the money itself (D20-7: the SOURCE books the export income).
    ///
    /// MONEY (D20-7). Goods moved inside the company keep the game's native export pricing; a
    /// warehouse-&gt;shop leg moves no money at all (there is no such transaction in the native chain and
    /// no delivery fee on this path). The export income is paid with a DIRECT
    /// GameManager.ChangeMoneySafe rather than through LogisticsManagerPlan.PendingTransactions,
    /// because that static list is cleared at the start of every plan's GetPlannedDeliveries
    /// (LogisticsManagerPlan.cs:67) and drained in OnFinishedDeliveries (:135-137) - both long inside
    /// the same synchronous pass, so an amount parked there would be gone before any ack returned.
    ///
    /// THREADING. Everything here touches game state and runs on the Unity main thread: the take-over
    /// is inside the native delivery pass, and every inbound leg is marshalled by the network dispatch
    /// (MPServer/MPClient) exactly like the other merger families.
    /// </summary>
    internal static class CargoTransfer
    {
        internal const string ActNeed = "need", ActOffer = "offer", ActDeliver = "deliver", ActAck = "ack", ActReturn = "return", ActClosed = "closed";

        private const string ExportBusinessType = "ba:businesstype_importexport";
        private const string MsgNoStock   = "ba:messagetype_phone_logistics_manager_delivery_no_stock";
        private const string MsgNoShelves = "ba:messagetype_phone_logistics_manager_delivery_no_available_space";

        // -- SOURCE SIDE --

        /// <summary>One transfer this machine STARTED and has not finished. It holds the live plan and
        /// destination objects so the phone report is raised by the game's own method on the real plan,
        /// and the withdrawn amounts so a remainder (or a give-back) goes home to the same warehouse.</summary>
        internal sealed class Pending
        {
            public string TransferId = "", PlanId = "", SourceKey = "", DestKey = "";
            public bool   IsExport;
            public int    Day, Hour;
            public bool   Withdrawn;
            public LogisticsManagerPlan? Plan;
            public LogisticsManagerPlanDestination? Destination;
            public readonly List<CargoTransferItem> Items   = new();       // what was actually taken
            public readonly List<ItemAmountTarget>  NoStock = new();       // the native "no stock" rows
        }

        internal static readonly Dictionary<string, Pending> _pending = new();

        /// <summary>Ids this machine has already started, so one delivery pass that runs twice inside a
        /// game hour starts ONE transfer (the id itself is the dedupe key - plan + destination + day +
        /// hour). Bounded: cleared whenever it grows past far more than a day's worth of legs.</summary>
        private static readonly HashSet<string> _started = new();

        // -- DESTINATION SIDE --

        /// <summary>Ids this machine has already DELIVERED, with the ack it sent. A repeated "deliver"
        /// for one of these re-acks with the identical figures and places nothing - the precedent is
        /// SharedShopSchedule's _appliedSeq (SharedShopSchedule.cs:71). Bounded the same way.</summary>
        private static readonly Dictionary<string, CargoTransferPayload> _applied = new();

        /// <summary>r2 (F3). Ids whose ack/return this machine has already APPLIED: the remainder is back in
        /// the warehouse, the source's books are written and the host has been told "closed". A leg re-sent
        /// for one of these is answered "closed" again and moves nothing. PERSISTED, because the host goes on
        /// re-offering it across a restart of this machine.</summary>
        private static readonly Dictionary<string, int> _closed = new();

        /// <summary>Ids this machine has already refused to close once (it does not run that source), so the
        /// host's once-per-sweep re-offer does not fill the log with the same line.</summary>
        private static readonly HashSet<string> _notMineLogged = new();

        private static int DayNow()  { try { return SaveGameManager.Current?.Day  ?? 0; } catch { return 0; } }
        private static int HourNow() { try { return SaveGameManager.Current?.Hour ?? 0; } catch { return 0; } }

        private static void Trim<T>(Dictionary<string, T> d) { if (d.Count > 2000) d.Clear(); }
        private static void Trim(HashSet<string> s)          { if (s.Count > 2000) s.Clear(); }

        private static void Send(CargoTransferPayload p)
        {
            p.PlayerId = MPConfig.PlayerId;
            if (MPServer.IsRunning) MPServer.HostRouteCargoTransfer(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CargoTransfer, MPConfig.PlayerId, p));
            else Plugin.Logger.LogWarning($"[Cargo] transfer {p.TransferId} refused: this machine is not connected to a session.");
        }

        private static CargoTransferPayload Leg(Pending t, string action)
            => new CargoTransferPayload
            {
                Action = action, TransferId = t.TransferId, PlanId = t.PlanId,
                SourceKey = t.SourceKey, DestKey = t.DestKey, Day = t.Day, Hour = t.Hour,
            };

        // == C1 TAKE-OVER ==

        /// <summary>MAIN THREAD, from the leg gate's mixed-owner branch (MPPatches). TRUE = this leg is
        /// now a routed transfer and the native method must NOT run. FALSE = this machine could not
        /// start one (no plan id, no warehouse, no destination registration), so the caller keeps its
        /// old refusal. Called once per (plan, destination) per delivery pass; a second call inside the
        /// same game hour mints the same TransferId and is dropped here.</summary>
        internal static bool TakeOver(LogisticsManagerPlan plan, LogisticsManagerPlanDestination destination)
        {
            try
            {
                if (plan == null || destination == null) return false;
                string planId = plan.id ?? "";
                string srcKey = "", dstKey = "";
                try { srcKey = GameStateReader.AddressKey(plan.targetAddress); } catch { }
                try { dstKey = GameStateReader.AddressKey(destination.deliveryTargetAddress); } catch { }
                if (planId.Length == 0 || srcKey.Length == 0 || dstKey.Length == 0) return false;

                var warehouse = BuildingHelper.GetBuildingRegistration(plan.targetAddress) as Warehouse;
                if (warehouse == null || !warehouse.RentedByPlayer) return false;   // the native method's own two guards (:76-80)

                int day = DayNow(), hour = HourNow();
                string tid = planId + "|" + dstKey + "|" + day + "|" + hour;
                if (_pending.ContainsKey(tid) || _started.Contains(tid)) return true;   // this pass already started it

                var destReg = BuildingHelper.GetBuildingRegistration(destination.deliveryTargetAddress);
                if (destReg == null) return false;
                bool isExport = plan.isFactory && destReg.businessTypeName == ExportBusinessType;

                var t = new Pending
                {
                    TransferId = tid, PlanId = planId, SourceKey = srcKey, DestKey = dstKey,
                    IsExport = isExport, Day = day, Hour = hour, Plan = plan, Destination = destination,
                };
                _pending[tid] = t; _started.Add(tid); Trim(_pending); Trim(_started);
                Plugin.Logger.LogInfo($"[Cargo] transfer {tid} started - '{srcKey}' -> '{dstKey}' ({(isExport ? "export" : "stock")}, {(destination.stockTargets != null ? destination.stockTargets.Count : 0)} target(s)).");

                // An EXPORT leg has no destination-side effect at all in the native code, so it neither
                // asks nor waits: it runs here, whole, exactly as ProcessStockTarget's export branch does.
                if (isExport) { RunExportLocally(t); return true; }

                var ask = Leg(t, ActNeed);
                // r3 (H3): ONE row per item name. A destination holding the same item in two stock targets
                // asked twice, and every cap downstream is per item - two rows each inside the answered
                // figure together carried twice the need, while native re-reads the shelves per target and
                // the second pass would have delivered nothing.
                var want = new Dictionary<string, int>();
                var wantOrder = new List<string>();
                if (destination.stockTargets != null)
                    foreach (var st in destination.stockTargets)
                    {
                        if (st == null || st.targetAmount <= 0) continue;              // the native method's first line (:126)
                        string name = st.itemName ?? "";
                        if (name.Length == 0) continue;
                        if (!want.ContainsKey(name)) wantOrder.Add(name);
                        want[name] = (want.TryGetValue(name, out var had) ? had : 0) + st.targetAmount;
                    }
                foreach (var name in wantOrder)
                    ask.Items.Add(new CargoTransferItem { ItemName = name, Amount = want[name] });
                if (ask.Items.Count == 0) { Close(t, "no stock target asks for anything"); return true; }
                Send(ask);
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] take-over: {ex.GetType().Name}: {ex.Message}"); return false; }
        }

        // == C2 NEED - the DESTINATION runner answers ==

        /// <summary>MAIN THREAD, destination runner. ProcessStockTarget's own need arithmetic, mirrored
        /// where the shelves really are: LogisticsManagerPlanDestination.cs:159-163 is
        /// `targetAmount - BuildingHelper.CountTotalResourcesInStock(deliveryTargetAddress, itemName)`,
        /// and a target already met (num2 &lt;= 0) contributes nothing. Nothing is written here.</summary>
        private static void AnswerNeed(CargoTransferPayload p)
        {
            var reply = new CargoTransferPayload
            {
                Action = ActNeed, Answer = true, TransferId = p.TransferId, PlanId = p.PlanId,
                SourceKey = p.SourceKey, DestKey = p.DestKey, Day = p.Day, Hour = p.Hour,
                TargetPid = p.SourcePid,
            };
            try
            {
                var reg = GameStatePatcher.FindRegistration(p.DestKey ?? "");
                if (reg == null)
                {
                    reply.Reason = "this machine does not hold that destination";
                    Plugin.Logger.LogWarning($"[Cargo] transfer {p.TransferId} refused: {reply.Reason} ('{p.DestKey}').");
                    Send(reply); return;
                }
                // r3 (H3): ONE row per item, and the shortfall counted ONCE against the shelves. Two rows of
                // one item each read the same shelf count and together asked for twice what is missing.
                var asked = new Dictionary<string, int>();
                var askOrder = new List<string>();
                if (p.Items != null)
                    foreach (var it in p.Items)
                    {
                        if (it == null || it.Amount <= 0 || string.IsNullOrEmpty(it.ItemName)) continue;
                        if (!asked.ContainsKey(it.ItemName)) askOrder.Add(it.ItemName);
                        asked[it.ItemName] = (asked.TryGetValue(it.ItemName, out var had) ? had : 0) + it.Amount;
                    }
                int short_ = 0;
                foreach (var name in askOrder)
                {
                    int have = 0;
                    try { have = BuildingHelper.CountTotalResourcesInStock(reg.Address, name); } catch { }
                    int need = asked[name] - have;
                    if (need <= 0) continue;
                    reply.Items.Add(new CargoTransferItem { ItemName = name, Amount = need });
                    short_ += need;
                }
                Plugin.Logger.LogInfo($"[Cargo] transfer {p.TransferId} need answered - {reply.Items.Count} item(s), {short_} unit(s) short at '{p.DestKey}'.");
            }
            catch (Exception ex)
            {
                reply.Items.Clear();
                reply.Reason = $"{ex.GetType().Name}: {ex.Message}";
                Plugin.Logger.LogWarning($"[Cargo] transfer {p.TransferId} refused: the need could not be counted - {reply.Reason}");
            }
            Send(reply);
        }

        // == C3 WITHDRAW - the SOURCE takes exactly what was answered ==

        private static void OnNeedAnswer(CargoTransferPayload p)
        {
            if (!_pending.TryGetValue(p.TransferId ?? "", out var t) || t == null) return;   // idempotent: already closed
            if (t.Withdrawn) return;                                                         // a duplicated answer withdraws nothing twice
            if (!string.IsNullOrEmpty(p.Reason)) { Close(t, p.Reason); return; }
            if (p.Items == null || p.Items.Count == 0) { Close(t, "the destination needs nothing"); return; }

            var warehouse = t.Plan != null ? BuildingHelper.GetBuildingRegistration(t.Plan.targetAddress) as Warehouse : null;
            if (warehouse == null) { Close(t, "this machine no longer holds that warehouse"); return; }

            var stock = new List<CargoInstance>();
            int units = 0;
            // r3 (H3): ONE withdraw per item, for the SUMMED amount. Two rows of one item withdrew twice,
            // and the host's per-item cap could not see that the two together exceeded what it answered.
            var take = new Dictionary<string, int>();
            var takeOrder = new List<string>();
            foreach (var it in p.Items)
            {
                if (it == null || it.Amount <= 0 || string.IsNullOrEmpty(it.ItemName)) continue;
                if (!take.ContainsKey(it.ItemName)) takeOrder.Add(it.ItemName);
                take[it.ItemName] = (take.TryGetValue(it.ItemName, out var had) ? had : 0) + it.Amount;
            }
            foreach (var name in takeOrder)
            {
                int amount = take[name];
                try
                {
                    BuildingHelper.GetItemsWithStock(warehouse, name, stock);                 // :165
                    var cargo = BuildingHelper.WithdrawFromCargo(stock, amount);              // :166
                    if (cargo == null || cargo.amount < amount)
                        t.NoStock.Add(new ItemAmountTarget(name, amount - (cargo != null ? cargo.amount : 0)));   // :168
                    if (cargo != null && cargo.amount > 0)
                    {
                        t.Items.Add(new CargoTransferItem { ItemName = name, Amount = cargo.amount, PricePerUnit = cargo.pricePerUnit });
                        units += cargo.amount;
                    }
                    // r2 (F1) NO RemoveEmptyCargo HERE. The native pass RETURNS first and removes the empties
                    // afterwards (LogisticsManagerPlanDestination.cs:186 then :188). Deleting the emptied
                    // instances now leaves ReturnToCargo (BuildingHelper.cs:494-516) with no instance of the
                    // item to merge into, and the game's own editor warning at :517-519 says the leftover
                    // "might be lost" - on the ordinary "the warehouse held no more than the need" leg that is
                    // EVERY instance, so the whole remainder would vanish. The empties go at CLOSE instead.
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] transfer {t.TransferId}: withdrawing '{name}': {ex.GetType().Name}: {ex.Message}"); }
            }
            t.Withdrawn = true;

            if (t.Items.Count == 0) { Close(t, "the warehouse holds none of what was asked for"); return; }
            // r2 (F5) the source's NEGATIVE delivery record is not written here. This point knows only what
            // was WITHDRAWN; native books what was DELIVERED (ProcessStockTarget returns
            // `amount - cargoInstance.amount`, :189, consumed by Deliver :49-52). It is written at CLOSE,
            // when the per-item remainder is finally known.
            try { InstanceBehavior<GameManager>.Instance.shouldUpdateAfterDeliveries = true; } catch { }
            Plugin.Logger.LogInfo($"[Cargo] transfer {t.TransferId} withdrawn - {t.Items.Count} item(s), {units} unit(s) out of '{t.SourceKey}'.");

            var offer = Leg(t, ActOffer);
            offer.Items.AddRange(t.Items);
            Send(offer);
        }

        /// <summary>The source's own books, exactly as the native pass writes them: one negative
        /// delivery transaction on the warehouse the goods left, then the game's own 60-record trim
        /// (LogisticsManagerPlanDestination.Deliver :57-59, its local CleanupOldDeliveries).</summary>
        private static void BookSourceDelivery(Warehouse warehouse, List<DeliveryItem> items)
        {
            try
            {
                if (warehouse == null || items == null || items.Count == 0) return;
                var tx = new DeliveryTransaction();
                tx.SetItems(items);
                warehouse.deliveryTransactions.Add(tx);
                CleanupOldDeliveries(warehouse);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] the source's delivery record: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void CleanupOldDeliveries(Warehouse w)
        {
            try { if (w.deliveryTransactions.Count > 60) w.deliveryTransactions.RemoveRange(0, w.deliveryTransactions.Count - 60); }
            catch { }
        }

        /// <summary>THE EXPORT LEG, whole, on the source - ProcessStockTarget :130-155 plus Deliver
        /// :60-86. Nothing is routed: no shelf of the destination is touched natively, so there is
        /// nothing for the other machine to do and nothing in transit to lose. The income is the game's
        /// own export price, paid straight through ChangeMoneySafe (see the file header for why
        /// PendingTransactions cannot carry it).</summary>
        private static void RunExportLocally(Pending t)
        {
            try
            {
                var warehouse = t.Plan != null ? BuildingHelper.GetBuildingRegistration(t.Plan.targetAddress) as Warehouse : null;
                var destReg   = t.Destination != null ? BuildingHelper.GetBuildingRegistration(t.Destination.deliveryTargetAddress) : null;
                if (warehouse == null || destReg == null) { Close(t, "the export leg's warehouse or destination is not on this machine"); return; }

                var stock   = new List<CargoInstance>();
                var moved   = new List<DeliveryItem>();
                var exports = new Dictionary<string, FactoryExport>();
                float profits = 0f; int units = 0;
                if (t.Destination != null && t.Destination.stockTargets != null)
                    foreach (var st in t.Destination.stockTargets)
                    {
                        if (st == null || st.targetAmount <= 0) continue;
                        BuildingHelper.GetItemsWithStock(warehouse, st.itemName, stock);
                        var cargo = BuildingHelper.WithdrawFromCargo(stock, st.targetAmount);
                        if (cargo == null || cargo.amount < st.targetAmount)
                            t.NoStock.Add(new ItemAmountTarget(st.itemName, st.targetAmount - (cargo != null ? cargo.amount : 0)));
                        if (cargo == null || cargo.amount == 0) { BuildingHelper.RemoveEmptyCargo(stock); continue; }
                        float unitPrice = ProductMarketHelper.GetProductExportPrice(st.itemName);
                        profits += unitPrice * cargo.amount;
                        if (!exports.TryGetValue(st.itemName, out var fx))
                        { fx = new FactoryExport { itemName = st.itemName }; exports[st.itemName] = fx; }
                        fx.amount     += cargo.amount;
                        fx.totalPrice += unitPrice * cargo.amount;
                        moved.Add(new DeliveryItem(st.itemName, -cargo.amount));
                        units += cargo.amount;
                        BuildingHelper.RemoveEmptyCargo(stock);
                    }
                if (moved.Count == 0) { Close(t, "the warehouse holds none of the export targets"); return; }

                BookSourceDelivery(warehouse, moved);
                if (profits > 0f)
                {
                    foreach (var export in exports.Values)
                    {
                        var existing = warehouse.factoryExports.Find(x => x.itemName == export.itemName);
                        if (existing != null) { existing.totalPrice += export.totalPrice; existing.amount += export.amount; }
                        else warehouse.factoryExports.Add(export);
                        try { ProductMarketHelper.UpdateMarketDemand(export.itemName); } catch { }
                    }
                    t.NoStock.Clear();                                     // native clears both report lists on a paid export (:85-86)
                    var data = new Dictionary<string, string> { { "businessName", destReg.BusinessName } };
                    try { GameManager.ChangeMoneySafe(profits, new TransactionInfo("ba:transaction_exportedgoods", data)); }
                    catch (Exception mx) { Plugin.Logger.LogWarning($"[Cargo] transfer {t.TransferId}: the export income could not be paid - {mx.GetType().Name}: {mx.Message}"); }
                }
                try { InstanceBehavior<GameManager>.Instance.shouldUpdateAfterDeliveries = true; } catch { }
                Plugin.Logger.LogInfo($"[Cargo] transfer {t.TransferId} delivered {units} (remainder 0) - exported from '{t.SourceKey}' to '{t.DestKey}'.");
                RaiseReport(t, null);
                _pending.Remove(t.TransferId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Cargo] transfer {t.TransferId}: the export leg failed - {ex.GetType().Name}: {ex.Message}");
                _pending.Remove(t.TransferId);
            }
        }

        // == C4 DELIVER - the DESTINATION runner places the goods ==

        /// <summary>MAIN THREAD, destination runner. The cargo is rebuilt from the three fields the
        /// native withdraw detached (BuildingHelper.cs:469), then handed to the game's own
        /// DeliverCargoToBuilding TWICE with the native filters (ItemHelper.cs:1087 via :176 then
        /// :182); whatever will not fit stays on the cargo and is acked as the remainder. Idempotent:
        /// a repeat re-acks with the identical figures and places nothing.</summary>
        private static void ApplyDeliver(CargoTransferPayload p)
        {
            string tid = p.TransferId ?? "";
            if (_applied.TryGetValue(tid, out var already) && already != null)
            {
                Plugin.Logger.LogInfo($"[Cargo] transfer {tid} delivered already - acknowledged again, nothing placed.");
                Send(already); return;
            }
            var ack = new CargoTransferPayload
            {
                Action = ActAck, TransferId = tid, PlanId = p.PlanId, SourceKey = p.SourceKey,
                DestKey = p.DestKey, Day = p.Day, Hour = p.Hour, TargetPid = p.SourcePid,
            };
            try
            {
                var destReg = GameStatePatcher.FindRegistration(p.DestKey ?? "");
                if (destReg == null)
                {
                    ack.Reason = "this machine does not hold that destination";
                    if (p.Items != null)
                        foreach (var it in p.Items)
                            if (it != null) ack.Items.Add(new CargoTransferItem { ItemName = it.ItemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = it.Amount });
                    Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: {ack.Reason} ('{p.DestKey}') - all of it goes back.");
                    MarkApplied(tid, ack); Send(ack); return;
                }

                var placed = new List<DeliveryItem>();
                int delivered = 0, remainder = 0;
                if (p.Items != null)
                    foreach (var it in p.Items)
                    {
                        if (it == null || it.Amount <= 0 || string.IsNullOrEmpty(it.ItemName)) continue;
                        string itemName = it.ItemName;
                        var cargo = new CargoInstance(itemName, it.Amount, it.PricePerUnit);
                        try { PlaceCargo(cargo, destReg); }                                    // :176 then :182
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: placing '{itemName}': {ex.GetType().Name}: {ex.Message}"); }
                        int left = cargo.amount, took = it.Amount - left;
                        if (took > 0) { placed.Add(new DeliveryItem(itemName, took)); delivered += took; }
                        remainder += left;
                        ack.Items.Add(new CargoTransferItem { ItemName = itemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = left });
                    }

                // The destination's own books, as the native pass writes them (:88-97): a warehouse
                // destination records the POSITIVE delivery, and every destination has its showcase
                // shelves and tills topped up from its own storage afterwards (:103 - the export branch
                // that skips this never reaches a deliver leg at all).
                if (destReg is Warehouse destWarehouse && placed.Count > 0)
                {
                    try
                    {
                        var tx = new DeliveryTransaction { dayOfDelivery = DayNow() };
                        foreach (var d in placed) tx.deliveryItems.Add(new DeliveryItem(d.itemName, d.amountDelivered));
                        destWarehouse.deliveryTransactions.Add(tx);
                        CleanupOldDeliveries(destWarehouse);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: the destination's delivery record: {ex.GetType().Name}: {ex.Message}"); }
                }
                try { BusinessHelper.FillUpEmptyShowcaseShelvesAndPointsOfSales(destReg.Address); } catch { }
                try { InstanceBehavior<GameManager>.Instance.shouldUpdateAfterDeliveries = true; } catch { }

                Plugin.Logger.LogInfo($"[Cargo] transfer {tid} delivered {delivered} (remainder {remainder}) at '{p.DestKey}'.");
            }
            catch (Exception ex)
            {
                ack.Items.Clear();
                if (p.Items != null)
                    foreach (var it in p.Items)
                        if (it != null) ack.Items.Add(new CargoTransferItem { ItemName = it.ItemName, Amount = it.Amount, PricePerUnit = it.PricePerUnit, Remainder = it.Amount });
                ack.Reason = $"{ex.GetType().Name}: {ex.Message}";
                Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: the delivery failed - {ack.Reason}");
            }
            MarkApplied(tid, ack);          // r2 (F6c): PERSISTED - a restart must not shelve the goods twice
            Send(ack);
        }

        // == C5 RETURN + the phone report - back on the SOURCE ==

        /// <summary>MAIN THREAD, the machine that RUNS SourceKey (r2 F3). The leg is SELF-CONTAINED - it
        /// names both ends, every item with its amount, price and remainder, and the two flags - so a
        /// stand-in runner that never had the _pending row (a reconnect, an absence hand-over, a restart)
        /// can still finish the transfer. _pending is a HINT only now: it supplies the live plan for the
        /// game's own phone report when this machine happens to be the one that started the leg. The
        /// remainder goes home through the game's own ReturnToCargo (:186), which needs a FRESH
        /// GetItemsWithStock first because that call is what fills the parent map the return walks
        /// (BuildingHelper.cs:445) - and the empties are removed only AFTER it (:188, r2 F1).
        /// everythingBack = a give-back for a record that never acknowledged: every unit comes home.
        /// Idempotent by _closed, which is persisted, and every outcome answers the host "closed".</summary>
        private static void ApplyClose(CargoTransferPayload p, bool everythingBack)
        {
            string tid = p.TransferId ?? "";
            if (_closed.ContainsKey(tid))
            {
                Plugin.Logger.LogInfo($"[Cargo] transfer {tid} closed already - confirmed again, nothing moved.");
                SendClosed(p); return;
            }

            var reg = GameStatePatcher.FindRegistration(p.SourceKey ?? "");
            bool mine = false;
            try { mine = reg != null && (MergerFlip.TrulyMine(reg) || MergerAbsence.SimulatesHere(p.SourceKey ?? "")); } catch { }
            if (!mine)
            {
                if (_notMineLogged.Add(tid))
                    Plugin.Logger.LogWarning($"[Cargo] transfer {tid} refused: this machine does not run the source '{p.SourceKey}' - the host keeps the leg until one does.");
                Trim(_notMineLogged);
                return;                       // NOT closed: the host re-offers it until a real runner answers
            }

            _pending.TryGetValue(tid, out var hint);
            var warehouse = reg as Warehouse;
            var noShelves = new List<ItemAmountTarget>();
            var booked    = new List<DeliveryItem>();
            var stock     = new List<CargoInstance>();
            int back = 0, delivered = 0, retaken = 0, missed = 0, notBack = 0;   // r3 (H5): notBack = a remainder that found no home
            try
            {
                if (p.Items != null)
                    foreach (var it in p.Items)
                    {
                        if (it == null || string.IsNullOrEmpty(it.ItemName)) continue;
                        int left = everythingBack ? it.Amount : it.Remainder;
                        if (left < 0) left = 0;
                        if (left > it.Amount) left = it.Amount;
                        int got = it.Amount - left;

                        // r2 (F2) THE LATE ACKNOWLEDGEMENT. The whole record already came home, so those
                        // `got` units are in this warehouse AND on the destination's shelves. Take them out
                        // again with the game's own withdraw - best effort, and a shortfall is logged.
                        if (p.WithdrawAgain)
                        {
                            if (got <= 0 || warehouse == null) continue;
                            try
                            {
                                BuildingHelper.GetItemsWithStock(warehouse, it.ItemName, stock);
                                var again = BuildingHelper.WithdrawFromCargo(stock, got);
                                int took = again != null ? again.amount : 0;
                                retaken += took; missed += got - took;
                                if (took > 0) booked.Add(new DeliveryItem(it.ItemName, -took));
                                BuildingHelper.RemoveEmptyCargo(stock);
                            }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: withdrawing '{it.ItemName}' again: {ex.GetType().Name}: {ex.Message}"); }
                            continue;
                        }

                        delivered += got;
                        if (got > 0) booked.Add(new DeliveryItem(it.ItemName, -got));   // r2 (F5): DELIVERED, not withdrawn
                        if (left == 0)
                        {
                            if (warehouse != null)
                                try { BuildingHelper.GetItemsWithStock(warehouse, it.ItemName, stock); BuildingHelper.RemoveEmptyCargo(stock); } catch { }
                            continue;                                                   // r2 (F1): the empties go here
                        }
                        noShelves.Add(new ItemAmountTarget(it.ItemName, left));
                        if (warehouse == null)
                        {
                            // r3 (H5): a source registration that is NOT a Warehouse (a factory) has no cargo
                            // to merge back into. Place the remainder the way a delivery is placed - the same
                            // call C4 uses - and log whatever will not fit as NOT returned. The old branch
                            // counted every unit as returned and had returned nothing at all.
                            try
                            {
                                var home = new CargoInstance(it.ItemName, left, it.PricePerUnit);
                                PlaceCargo(home, reg);
                                back += left - home.amount;
                                notBack += home.amount;
                                if (home.amount > 0)
                                    Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: {home.amount} of {left} unit(s) of '{it.ItemName}' could NOT be put back into '{p.SourceKey}' - it has no room for them.");
                            }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: returning '{it.ItemName}': {ex.GetType().Name}: {ex.Message}"); }
                            continue;
                        }
                        try
                        {
                            var home = new CargoInstance(it.ItemName, left, it.PricePerUnit);
                            BuildingHelper.GetItemsWithStock(warehouse, it.ItemName, stock);   // :186
                            BuildingHelper.ReturnToCargo(stock, home);
                            back += left - home.amount;   // r4 (I4): only what the merge ACTUALLY took - counting the whole remainder up front printed "returned N unit(s)" for units that never went home
                            // r2 (F1): if no instance of the item is left in the warehouse (another pass
                            // removed the empties), or none has room, ReturnToCargo merges nothing and what
                            // is left on the cargo would simply cease to exist. Place it the way the game
                            // places a DELIVERY into a building instead - the same call C4 uses.
                            if (home.amount > 0)
                            {
                                int stranded = home.amount;
                                PlaceCargo(home, reg);
                                back += stranded - home.amount;   // r4 (I4): and only what the placement took
                                notBack += home.amount;           // the rest is in no warehouse at all
                                Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: the remainder had no cargo slot left - placed as a delivery ({stranded - home.amount} of {stranded} unit(s) of '{it.ItemName}').");
                            }
                            BuildingHelper.RemoveEmptyCargo(stock);                            // :188, AFTER the return
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: returning '{it.ItemName}': {ex.GetType().Name}: {ex.Message}"); }
                    }

                // r2 (F5): the source's own books, written now that the delivered figure is known.
                if (warehouse != null && booked.Count > 0) BookSourceDelivery(warehouse, booked);
                if (p.WithdrawAgain)
                    Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: withdrawn again - {retaken} unit(s) taken back out of '{p.SourceKey}'{(missed > 0 ? $", {missed} unit(s) short" : "")}.");
                else if (back > 0)
                    Plugin.Logger.LogInfo($"[Cargo] transfer {tid} returned - {back} unit(s) back into '{p.SourceKey}'{(string.IsNullOrEmpty(p.Reason) ? "" : " (" + p.Reason + ")")}.");
                else if (notBack == 0)   // r3 (H5): with a remainder that found no home the warning above is the truthful line
                    Plugin.Logger.LogInfo($"[Cargo] transfer {tid} delivered {delivered} (remainder 0) - nothing to return.");
                try { InstanceBehavior<GameManager>.Instance.shouldUpdateAfterDeliveries = true; } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] transfer {tid}: the return leg failed - {ex.GetType().Name}: {ex.Message}"); }

            if (hint != null && !p.WithdrawAgain) RaiseReport(hint, noShelves);
            else if (hint == null && noShelves.Count > 0)
                Plugin.Logger.LogInfo($"[Cargo] transfer {tid}: the undelivered-items report is skipped - this machine holds no plan for that leg.");
            _pending.Remove(tid);
            MarkClosed(tid, p.Day);
            SendClosed(p);
        }

        /// <summary>The game's own placement, shared by C4 and by the source when a remainder has nothing
        /// left to merge back into (r2 F1): DeliverCargoToBuilding with the two native filters
        /// (LogisticsManagerPlanDestination.cs:176 then :182).</summary>
        private static void PlaceCargo(CargoInstance cargo, BuildingRegistration reg)
        {
            if (cargo == null || cargo.amount <= 0 || reg == null) return;
            string itemName = cargo.itemName;
            ItemHelper.DeliverCargoToBuilding(cargo, reg,
                x => (x.ItemCached.type & (ItemType.PointOfSale | ItemType.ShowcaseShelf)) != 0
                     && x.GetStockInstance().itemName == itemName);            // :176
            if (cargo.amount > 0)
                ItemHelper.DeliverCargoToBuilding(cargo, reg,
                    x => (x.ItemCached.type & ItemType.StorageShelf) != 0);    // :182
        }

        /// <summary>The confirmation that finally lets the host drop its record (r2 F3).</summary>
        private static void SendClosed(CargoTransferPayload p)
            => Send(new CargoTransferPayload
            {
                Action = ActClosed, TransferId = p.TransferId ?? "", PlanId = p.PlanId ?? "",
                SourceKey = p.SourceKey ?? "", DestKey = p.DestKey ?? "", Day = p.Day, Hour = p.Hour,
            });

        // == r2: THE PERSISTED IDEMPOTENCE MARKS (F3 closed ids, F6c applied ids) ==

        private const int MarkKeep = 500;

        private static void MarkApplied(string tid, CargoTransferPayload ack)
        {
            if (string.IsNullOrEmpty(tid) || ack == null) return;
            _applied[tid] = ack; Trim(_applied); PruneApplied(); PersistMarks();
        }

        private static void MarkClosed(string tid, int day)
        {
            if (string.IsNullOrEmpty(tid)) return;
            _closed[tid] = day; _notMineLogged.Remove(tid); PruneClosed(); PersistMarks();
        }

        private static void PruneClosed()
        {
            if (_closed.Count <= MarkKeep) return;
            var keys = new List<string>(_closed.Keys);
            keys.Sort((a, b) => _closed[b].CompareTo(_closed[a]));
            for (int i = MarkKeep; i < keys.Count; i++) _closed.Remove(keys[i]);
        }

        private static void PruneApplied()
        {
            if (_applied.Count <= MarkKeep) return;
            var keys = new List<string>(_applied.Keys);
            keys.Sort((a, b) => (_applied[b] != null ? _applied[b].Day : 0).CompareTo(_applied[a] != null ? _applied[a].Day : 0));
            for (int i = MarkKeep; i < keys.Count; i++) _applied.Remove(keys[i]);
        }

        private static void PersistMarks()
        {
            try { MPSaveCoordinator.PersistCargoMarksNow(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] the idempotence marks could not be written - {ex.GetType().Name}: {ex.Message}"); }
        }

        internal static List<MpCargoMarkEntry> SnapshotClosed()
        {
            var list = new List<MpCargoMarkEntry>();
            try { foreach (var kv in _closed) list.Add(new MpCargoMarkEntry { TransferId = kv.Key, Day = kv.Value }); } catch { }
            list.Sort((x, y) => string.CompareOrdinal(x.TransferId, y.TransferId));
            return list;
        }

        internal static List<MpCargoMarkEntry> SnapshotApplied()
        {
            var list = new List<MpCargoMarkEntry>();
            try
            {
                foreach (var kv in _applied)
                {
                    if (kv.Value == null) continue;
                    string json = "";
                    try { json = Newtonsoft.Json.JsonConvert.SerializeObject(kv.Value); } catch { }
                    if (json.Length == 0) continue;
                    list.Add(new MpCargoMarkEntry { TransferId = kv.Key, Day = kv.Value.Day, PayloadJson = json });
                }
            }
            catch { }
            list.Sort((x, y) => string.CompareOrdinal(x.TransferId, y.TransferId));
            return list;
        }

        /// <summary>ADDITIVE, as the ruling says: a restore is a UNION, so the per-machine file and the
        /// manifest can both be applied at a load and neither erases the other.</summary>
        internal static void RestoreLocalState(List<MpCargoMarkEntry>? closed, List<MpCargoMarkEntry>? applied)
        {
            try
            {
                if (closed != null)
                    foreach (var c in closed)
                        if (c != null && !string.IsNullOrEmpty(c.TransferId)) _closed[c.TransferId] = c.Day;
                if (applied != null)
                    foreach (var a in applied)
                    {
                        if (a == null || string.IsNullOrEmpty(a.TransferId) || string.IsNullOrEmpty(a.PayloadJson)) continue;
                        if (_applied.ContainsKey(a.TransferId)) continue;
                        try
                        {
                            var ack = Newtonsoft.Json.JsonConvert.DeserializeObject<CargoTransferPayload>(a.PayloadJson);
                            if (ack != null) _applied[a.TransferId] = ack;
                        }
                        catch { }
                    }
                PruneClosed(); PruneApplied();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] the idempotence marks could not be restored - {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>r2 (F6a): the per-WORLD state. _started was never cleared on a world load, so loading a
        /// save back to the same game day and hour left every id of that hour already "started" and the leg
        /// silently did nothing. Called from the two world-load seams this family owns (the host's manifest
        /// restore and the client's load-data path); the persisted marks are read back straight after.</summary>
        internal static void ResetSession()
        {
            _pending.Clear(); _started.Clear(); _applied.Clear(); _closed.Clear(); _notMineLogged.Clear();
        }

        /// <summary>The GAME'S OWN phone report, nothing new: LogisticsManagerPlan.SendDeliveryReport is
        /// private and is normally reached through OnFinishedDeliveries (:126/:133), whose two static
        /// dictionaries are cleared at the start of every plan's GetPlannedDeliveries (:67) and drained
        /// inside the same synchronous pass - so an ack arriving later would find them emptied. The
        /// method is therefore invoked directly on the plan, with the game's own two message keys. The
        /// "no stock" half is skipped for a factory plan exactly as native does (LogisticsManagerPlan.cs
        /// :82, `bool flag = !isFactory && ...`).</summary>
        private static void RaiseReport(Pending t, List<ItemAmountTarget>? noShelves)
        {
            try
            {
                if (t.Plan == null) return;
                bool wantStock   = !t.Plan.isFactory && t.NoStock.Count > 0;
                bool wantShelves = noShelves != null && noShelves.Count > 0;
                if (!wantStock && !wantShelves) return;
                var destReg = t.Destination != null ? BuildingHelper.GetBuildingRegistration(t.Destination.deliveryTargetAddress) : null;
                if (destReg == null) return;
                var send = AccessTools.Method(typeof(LogisticsManagerPlan), "SendDeliveryReport");
                if (send == null)
                { Plugin.Logger.LogWarning("[Cargo] the plan's own delivery report method was not found - undelivered items go unreported."); return; }
                if (wantStock)
                    send.Invoke(t.Plan, new object[] { new List<BusinessDeliveryInfo> { new BusinessDeliveryInfo(destReg.BusinessName, new List<ItemAmountTarget>(t.NoStock)) }, MsgNoStock });
                if (wantShelves)
                    send.Invoke(t.Plan, new object[] { new List<BusinessDeliveryInfo> { new BusinessDeliveryInfo(destReg.BusinessName, new List<ItemAmountTarget>(noShelves!)) }, MsgNoShelves });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] the delivery report could not be raised - {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>A transfer that ends before anything reached the destination: logged and dropped,
        /// with the native "no stock" rows still reported if the source had already collected any.</summary>
        internal static void Close(Pending t, string why)
        {
            Plugin.Logger.LogInfo($"[Cargo] transfer {t.TransferId} refused: {why}.");
            if (t.Withdrawn) RaiseReport(t, null);
            _pending.Remove(t.TransferId);
        }

        // == THE ONE INBOUND SEAM ==

        /// <summary>MAIN THREAD. Every leg the host hands this machine. Which half of the transfer this
        /// machine is playing is decided by the leg itself, never guessed: "need" (the ask) and
        /// "deliver" are the destination's, the "need" reply, "ack" and "return" are the source's, and
        /// "closed" only ever travels the other way (member -> host).</summary>
        public static void Receive(CargoTransferPayload p)
        {
            try
            {
                if (p == null) return;
                switch (p.Action)
                {
                    case ActNeed:    if (p.Answer) OnNeedAnswer(p); else AnswerNeed(p); break;
                    case ActDeliver: ApplyDeliver(p); break;
                    case ActAck:     ApplyClose(p, everythingBack: false); break;
                    // r2 (F2): a give-back AFTER an acknowledgement carries the REMAINDERS only - every unit
                    // the destination already shelved stays there. Only a record that never acknowledged
                    // comes home whole.
                    case ActReturn:  ApplyClose(p, everythingBack: !p.RemaindersOnly); break;
                    case ActClosed:
                        Plugin.Logger.LogWarning($"[Cargo] transfer {p.TransferId} refused: 'closed' is the host's leg and never travels to a member (sender '{p.PlayerId}').");
                        break;
                    default:
                        Plugin.Logger.LogWarning($"[Cargo] transfer {p.TransferId} refused: unknown leg '{p.Action}'.");
                        break;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Cargo] Receive: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
