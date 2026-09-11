using System;
using System.Collections.Generic;
using Buildings;                               // BuildingRegistration
using Entities;                                // OrderHistoryEntry
using HarmonyLib;
using Helpers;                                 // BuildingHelper
using UI.Smartphone.Apps.BizMan;               // BizManInventoryPricing
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// SHARED-SHOP MANAGEMENT (the Business PERMISSION feature) — slice 4: pricing. Plan §2.4, rulings 12, 17, 25.
    ///
    /// Everything keys on "a shop another player RUNS that I may price": a DIRECT Business grant, or — since merger
    /// phase 2 wave 1 (2026-09-11) — a MERGER-FLIPPED partner shop, which takes the SAME routed edit. Own shops, AI
    /// shops and single-player keep the native path untouched. (The sales/products snapshot below stays DIRECT-GRANT
    /// only; a flipped shop routes its price edits and nothing else yet.)
    ///
    /// What a helper gets:
    ///  • THE PRICING TAB on a shared shop (the allow-list gains "InventoryPricing"), with the game's own editor.
    ///  • REAL SALES FIGURES (ruling 25). The tab reads `orderHistory`, which is local-only — a replica's is empty,
    ///    so a helper would price blind against zeros. On tab open the editor asks the owner (SharedSalesHistory
    ///    "request"); the owner answers ONE editor with the last 14 days, carrying only the three fields the tab sums
    ///    (item, units, revenue). Fourteen days is exactly what it consumes: two 7-day windows.
    ///  • PRICE EDITS ROUTED. The game writes the local copy as always; we notice, wait for 1 s of quiet (the native
    ///    editor fires per keystroke AND per +/- click — ruled 2026-08-21), and route the item's new price to the
    ///    owner, who writes BOTH native lists. MPPriceSync's existing broadcast then carries it to everyone, so this
    ///    slice adds no echo of its own.
    ///  • NO SNAP-BACK. Between the local write and the owner's confirmation, the owner's routine price re-assert
    ///    would otherwise pull the number back under the helper's cursor. Items with an edit in flight are held out
    ///    of that re-assert (15 s), per item — everything else in the same message still applies.
    ///  • HONEST PRICE GUIDES. The native yellow/red guide asks "does a rival nearby sell this?", testing "someone
    ///    else owns it AND I do not rent it". On the helper's machine the shop being priced passes both and counts
    ///    itself as its own competitor, so the helper saw different guide colours than the owner (field 2026-08-22).
    ///    Tenancy is raised for that one scan, exactly as the owner's machine would have it.
    ///  • Nothing on screen beyond that (ruling 17): no toasts, no added text.
    /// </summary>
    public static class SharedShopPrices
    {
        private const string Tag = "[SharedShop]";
        private const float QuietSeconds  = 1f;    // coalesce: send after this much silence (RULED 2026-08-21)
        private const float HoldSeconds   = 15f;   // an edited item is held out of the owner's re-assert for this long
        private const int   HistoryDays   = 14;    // exactly what the tab sums (two 7-day windows)
        private const int   MaxHistoryRows = 4000; // sanity cap on a received snapshot

        // ── editor state ──
        private static string _openAddr = "";                                             // the shared shop whose pricing tab is open here
        private static readonly Dictionary<string, float> _baseline = new();              // item → the price we believe the OWNER has
        private static readonly Dictionary<string, (float price, float at)> _dirty = new();// item → last seen local price, when
        private static readonly Dictionary<string, (float price, float at)> _held = new(); // item → value we routed, when (suppresses the re-assert)
        // Units on hand live in SharedShopStock (one table, one substitution, shared with the deliveries tab).
        private static readonly Dictionary<string, int> _seq = new();                      // item → last seq (never cleared)
        private static readonly int _seqEpoch = new System.Random().Next(1, int.MaxValue);

        // ── owner state ──
        private static readonly Dictionary<string, (int epoch, int seq)> _appliedSeq = new();   // "addr|item|pid" → last applied
        private static readonly HashSet<string> _logged = new();

        public static void Reset()
        {
            _openAddr = ""; _baseline.Clear(); _dirty.Clear(); _held.Clear(); _appliedSeq.Clear(); _logged.Clear();
            SharedShopStock.Reset();
        }

        /// <summary>The shared shop whose pricing tab is open here, or "" — read by SharedShopStock's
        /// substitution, which is scoped to the surface on screen.</summary>
        internal static string OpenAddr => _openAddr;

        /// <summary>Is an edit of this item in flight here? MPPriceSync asks before overwriting a received price, and
        /// passes the value that arrived so a hold can end the moment the owner's copy agrees with ours.
        ///
        /// Keyed by ADDRESS + item and deliberately independent of which tab is open: keying it on the open session
        /// meant that leaving the tab dropped every hold, and an owner broadcast still carrying the pre-edit price
        /// then overwrote the new one — the field report of "switched tabs and it went back to an older value that is
        /// no longer true" (2026-08-22).</summary>
        internal static bool HoldsItem(string addressKey, string itemName, float incomingPrice)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemName)) return false;
                string key = addressKey + "|" + itemName;
                if (!_held.TryGetValue(key, out var h)) return false;
                if (!float.IsNaN(incomingPrice) && Mathf.Approximately(incomingPrice, h.price)) { _held.Remove(key); return false; }   // converged
                if (Time.unscaledTime - h.at <= HoldSeconds) return true;
                _held.Remove(key);   // the owner never confirmed — stop fighting their copy
                return false;
            }
            catch { return false; }
        }

        private static string AddrOf(BuildingRegistration reg)
        {
            try { return reg != null ? GameStateReader.AddressKey(reg) : ""; } catch { return ""; }
        }

        /// <summary>Merger phase 2 wave 1 (2026-09-11): the registrations whose price edits are ROUTED to the shop's
        /// owner instead of being kept here — a DIRECT-grant shared shop, or a merger-flipped partner shop (locally
        /// RentedByPlayer through the flip, but not MergerFlip.TrulyMine). A member's native cell write otherwise hits
        /// their REPLICA only: the owner never learns of it, does not publish it, and their routine re-assert
        /// (MPPriceSync.Apply) pulls the old number back within ~30 s. Both kinds ride the one SharedPriceEdit hop.</summary>
        internal static bool PriceRouted(BuildingRegistration reg, string addr)
            => SharedShopSchedule.IsSharedShop(reg, addr) || SharedShopSchedule.IsMergedShop(reg, addr);

        /// <summary>The registration the BizMan page is currently showing, or null.</summary>
        private static BuildingRegistration OpenPageReg()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var page = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                return page != null ? page.buildingRegistration : null;
            }
            catch { return null; }
        }

        // ── tick: coalesce and send ──

        /// <summary>MAIN THREAD (MPCanvasUI.Update). Inert unless a shared shop's pricing tab is open here.</summary>
        public static void Tick()
        {
            try
            {
                if (_dirty.Count == 0) return;
                if (_openAddr.Length == 0) { _dirty.Clear(); return; }
                float now = Time.unscaledTime;
                List<string> ready = null;
                foreach (var kv in _dirty)
                    if (now - kv.Value.at >= QuietSeconds) (ready ??= new List<string>()).Add(kv.Key);
                if (ready == null) return;
                foreach (var item in ready)
                {
                    var d = _dirty[item];
                    _dirty.Remove(item);
                    // Only a real difference from the owner's state is worth a message. The game re-writes the same
                    // value into the field whenever a row is (re)bound, so without this every scroll would "edit".
                    if (_baseline.TryGetValue(item, out var b) && Mathf.Approximately(b, d.price)) continue;
                    Commit(_openAddr, item, d.price);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} price tick: {ex.Message}"); }
        }

        /// <summary>Record ONE committed price and route it: the hold that keeps the owner's re-assert off this item
        /// until the publish comes back, then the message. Shared by the tab's quiet timer and the dev lever.</summary>
        private static void Commit(string addr, string itemName, float price)
        {
            _baseline[itemName] = price;   // (tab session: one open address at a time; the lever below never seeds it)
            _held[addr + "|" + itemName] = (price, Time.unscaledTime);
            Send(addr, itemName, price);
        }

        /// <summary>DEV LEVER (TestDrive "setprice"): commit a price the way the pricing tab does and report whether
        /// the seam routed it. The tab has NO named setter to call — its write is the anonymous onValueChanged
        /// listener wired in InventoryProductCellView.Start (:114-115), which assigns the model's
        /// RetailPriceReference.price AND StoredRetailPriceReference.price, i.e. the reg's two native rows. This does
        /// exactly that, then takes the same PriceRouted decision the tab's session gate takes.</summary>
        internal static bool CommitPriceEdit(BuildingRegistration reg, string addressKey, string itemName, float price)
        {
            if (reg == null || string.IsNullOrEmpty(addressKey) || string.IsNullOrEmpty(itemName)) return false;
            // r2 (review MINOR-1): decide BEFORE writing - a foreign address that is neither mine nor routed must not be
            // touched by the lever (the tab cannot reach such a shop at all).
            bool routed = PriceRouted(reg, addressKey);
            if (!routed && !MergerFlip.TrulyMine(reg)) return false;
            bool wrote = false;
            if (reg.retailPrices != null)
                foreach (var rp in reg.retailPrices)
                    if (rp != null && rp.itemName == itemName) { rp.price = price; wrote = true; break; }
            if (reg.storedRetailPrices != null)
                foreach (var rp in reg.storedRetailPrices)
                    if (rp != null && rp.itemName == itemName) { rp.price = price; break; }
            if (!wrote || !routed) return false;
            // r2 (review MINOR-2): the lever does not seed _baseline (item-keyed, meant for the ONE open tab address);
            // it holds the item and sends, which is all the tab's Commit does that matters off-tab.
            _held[addressKey + "|" + itemName] = (price, Time.unscaledTime);
            Send(addressKey, itemName, price);
            return true;
        }

        private static void Send(string addr, string itemName, float price)
        {
            _seq.TryGetValue(itemName, out var seq); seq++; _seq[itemName] = seq;
            var p = new SharedPriceEditPayload
            {
                PlayerId = MPConfig.PlayerId, AddressKey = addr, ItemName = itemName,
                Price = price, Seq = seq, SeqEpoch = _seqEpoch,
            };
            string parked = ""; try { parked = MergerFlip.ParkedRunner(addr); } catch { }
            string shown = price.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            if (parked.Length > 0)
                Plugin.Logger.LogInfo($"[Merger] price edit routed to owner '{parked}' for '{addr}' ({itemName} -> {shown})");
            else
                Plugin.Logger.LogInfo($"{Tag} routing price of '{itemName}' at '{addr}' to the owner: {shown} (seq {seq}).");
            if (MPServer.IsRunning) MPServer.HostRouteSharedPriceEdit(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedPriceEdit, MPConfig.PlayerId, p));
        }

        // ── owner side: apply a routed price ──

        /// <summary>MAIN THREAD. Write one item's price into BOTH native lists, exactly as the game's own editor does
        /// (retailPrices drives what customers pay; storedRetailPrices is what the tab re-seeds from). MPPriceSync's
        /// scan sees the changed list on its next pass and broadcasts it — no echo of our own.</summary>
        public static void ApplyOnOwner(SharedPriceEditPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.ItemName)) return;
                if (p.PlayerId == MPConfig.PlayerId) return;
                // Merger phase 2 wave 1: the UNION check — a DIRECT Business grant, or membership of my merged
                // company (the same widening HostRouteEmployeeEdit already relies on). Decomposed rather than one
                // GrantSync.IsGranted call so the applied log can say which of the two let the edit in.
                bool direct = GrantSync.IsGrantedDirect(GrantKind.Business, MPConfig.PlayerId, p.PlayerId);
                bool merged = !direct && MergerSync.MergedRuntime(MPConfig.PlayerId, p.PlayerId);
                if (!direct && !merged)
                {
                    if (_logged.Add("price-nogrant|" + p.PlayerId))
                        Plugin.Logger.LogWarning($"{Tag} price edit from '{p.PlayerId}' refused: no Business grant from me and not a member of my company — ignored.");
                    return;
                }
                // IsSaneMoney FIRST (audit 2026-08-26): `p.Price < 0f || p.Price > 10000f` is FALSE for
                // NaN — both comparisons are — so a NaN rode straight into retailPrices below. The
                // project already owns the right predicate and the ordinary price channel uses it
                // (MPServer.cs:1769); this one did not. Newtonsoft accepts NaN/Infinity tokens by default.
                if (!MPServer.IsSaneMoney(p.Price) || p.Price < 0f || p.Price > 10000f)
                { Plugin.Logger.LogWarning($"{Tag} price edit from '{p.PlayerId}': {p.Price} out of range — ignored."); return; }
                var reg = GameStatePatcher.FindRegistration(p.AddressKey);
                if (reg == null) return;
                // W3-0 r1 (F1): the real owner applies — and so does the machine SIMULATING an absent
                // owner's businesses, whose lifted copy IS the live state and whose owner-style pushes carry
                // the edit onward. Anywhere else the edit is not ours to run, and a routed edit never dies in
                // silence. (`merged` above is company MEMBERSHIP between me and the sender — no address in it —
                // so the [Merger] applied line below fires on the simulator exactly as it does on the owner.)
                bool runsHere = MergerFlip.TrulyMine(reg);
                if (!runsHere) { try { runsHere = MergerAbsence.SimulatesHere(p.AddressKey); } catch { } }
                if (!runsHere)
                {
                    if (_logged.Add("price-notmine|" + p.AddressKey))
                        Plugin.Logger.LogWarning($"{Tag} routed price for '{p.AddressKey}' — not run here, dropped");
                    return;
                }
                string key = p.AddressKey + "|" + p.ItemName + "|" + p.PlayerId;
                if (_appliedSeq.TryGetValue(key, out var last) && last.epoch == p.SeqEpoch && p.Seq <= last.seq) return;   // a delayed duplicate never undoes a newer edit
                _appliedSeq[key] = (p.SeqEpoch, p.Seq);

                bool wrote = false;
                if (reg.retailPrices != null)
                    foreach (var rp in reg.retailPrices)
                        if (rp != null && rp.itemName == p.ItemName) { rp.price = p.Price; wrote = true; break; }
                if (reg.storedRetailPrices != null)
                    foreach (var rp in reg.storedRetailPrices)
                        if (rp != null && rp.itemName == p.ItemName) { rp.price = p.Price; break; }
                if (!wrote)
                {
                    // The shop does not stock that item (list re-seeded, product removed) — nothing to write, and
                    // inventing a row would put a price on something the shop cannot sell.
                    if (_logged.Add("price-noitem|" + p.ItemName))
                        Plugin.Logger.LogInfo($"{Tag} price edit for '{p.ItemName}' at '{p.AddressKey}' — that item is not in the shop's list, ignored.");
                    return;
                }
                Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' set '{p.ItemName}' at '{p.AddressKey}' to {p.Price:F2} — applied.");
                if (merged) Plugin.Logger.LogInfo($"[Merger] price edit applied for '{p.AddressKey}' from '{p.PlayerId}'");
                MPPriceSync.PublishNow(p.AddressKey);
                RefreshPricingTabIfOpen(p.AddressKey);   // the owner may be looking at this very tab
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} price apply: {ex.Message}"); }
        }

        // ── sales history (ruling 25) ──

        public static void HandleSalesHistory(SharedSalesHistoryPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (p.Action == "request") OwnerAnswerHistory(p);
                else if (p.Action == "snapshot") ApplyHistorySnapshot(p);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} sales history: {ex.Message}"); }
        }

        /// <summary>OWNER, MAIN THREAD: answer ONE editor with the recent per-item sales of that shop.</summary>
        private static void OwnerAnswerHistory(SharedSalesHistoryPayload req)
        {
            // Merger phase 2 wave 2 (2026-09-11): the READ-side UNION — a DIRECT Business grant, or membership of
            // my merged company. Decomposed rather than one GrantSync.IsGranted call so the sending log can say
            // which of the two let the request in (the shape ApplyOnOwner already uses for routed price edits).
            bool histDirect = GrantSync.IsGrantedDirect(GrantKind.Business, MPConfig.PlayerId, req.PlayerId);
            bool histMerged = !histDirect && MergerSync.MergedRuntime(MPConfig.PlayerId, req.PlayerId);
            if (!histDirect && !histMerged)
            {
                if (_logged.Add("hist-nogrant|" + req.PlayerId))
                    Plugin.Logger.LogInfo($"{Tag} sales-history request from '{req.PlayerId}' but they hold no Business grant from me and are not a member of my company — ignored.");
                return;
            }
            var reg = GameStatePatcher.FindRegistration(req.AddressKey);
            if (reg == null || !MergerFlip.TrulyMine(reg)) return;
            var gi = SaveGameManager.Current;
            if (gi == null) return;
            int today = gi.Day;
            var reply = new SharedSalesHistoryPayload
            {
                PlayerId = MPConfig.PlayerId, Action = "snapshot", AddressKey = req.AddressKey,
                ToPid = req.PlayerId, OwnerDay = today,
            };
            try
            {
                // The product list is DERIVED locally from a shop's own shelves (BusinessHelper
                // .UpdateCachedAvailableProducts reads itemInstances) and never syncs, so a replica's is empty and
                // the tab lists nothing to price (field 2026-08-22). It rides this same on-demand reply rather than
                // being recomputed here from an interior that may not have loaded yet.
                if (reg.cachedAvailableProducts != null)
                    foreach (var prod in reg.cachedAvailableProducts)
                    {
                        if (string.IsNullOrEmpty(prod)) continue;
                        reply.Products.Add(prod);
                        // Units on hand are counted from the shop's INTERIOR, which the helper may never have loaded
                        // — their column read wrong (field 2026-08-22). Counted here with the game's own helper, on
                        // the machine whose interior is authoritative.
                        try { reply.Stock.Add(new StockInfo { ItemName = prod, Count = BuildingHelper.CountTotalResourcesInStock(reg, prod, includeProducers: true, includePallets: false) }); }
                        catch { }
                    }
            }
            catch { }
            int rows = 0;
            try
            {
                if (reg.orderHistory != null)
                    foreach (var h in reg.orderHistory)
                    {
                        if (h == null || h.dayNumber < today - HistoryDays || h.dayNumber > today) continue;
                        var day = new SalesDayInfo { DayNumber = h.dayNumber };
                        if (h.itemSales != null)
                            foreach (var s in h.itemSales)
                            {
                                if (s == null || string.IsNullOrEmpty(s.itemName)) continue;
                                day.Items.Add(new SalesItemInfo { ItemName = s.itemName, AmountSold = s.amountSold, TotalPrice = s.totalPrice });
                                if (++rows >= MaxHistoryRows) break;
                            }
                        reply.Days.Add(day);
                        if (rows >= MaxHistoryRows) break;
                    }
            }
            catch { }
            Plugin.Logger.LogInfo($"{Tag} sending '{req.PlayerId}' the last {HistoryDays} days of sales for '{req.AddressKey}': {reply.Days.Count} day(s), {rows} row(s), {reply.Products.Count} product(s) on sale.{(histMerged ? " (merged)" : "")}");
            if (MPServer.IsRunning) MPServer.HostRouteSharedSalesHistory(reply, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedSalesHistory, MPConfig.PlayerId, reply));
        }

        /// <summary>EDITOR, MAIN THREAD: put the owner's figures into the replica so the tab's own maths works.
        /// The replica's orderHistory is otherwise empty, so this REPLACES the window it covers rather than merging.
        /// Only the three fields the tab sums are carried; the rest of an entry stays at its defaults, which is why
        /// nothing else may be taught to read a replica's history.</summary>
        private static void ApplyHistorySnapshot(SharedSalesHistoryPayload p)
        {
            var reg = GameStatePatcher.FindRegistration(p.AddressKey);
            if (reg == null) return;
            var gi = SaveGameManager.Current;
            if (gi == null) return;
            // What the shop sells, straight from the owner: without it the tab has no rows to price at all.
            if (p.Products != null && p.Products.Count > 0)
            {
                if (reg.cachedAvailableProducts == null) reg.cachedAvailableProducts = new List<string>();
                reg.cachedAvailableProducts.Clear();
                foreach (var prod in p.Products) if (!string.IsNullOrEmpty(prod)) reg.cachedAvailableProducts.Add(prod);
            }
            // PARTIAL coverage by design: this list is the shop's sale list, which is exactly the rows this
            // tab draws. SharedShopStock leaves anything else on the native count. Keyed to THIS surface —
            // the deliveries tab's wider table for the same shop must not be replaced by this narrower one
            // (review M1).
            SharedShopStock.Set(SharedShopStock.Pricing, p.AddressKey, p.Stock);
            int shift = gi.Day - p.OwnerDay;   // normally 0 (one world clock); rebased so the tab's day arithmetic lands on the same window
            if (reg.orderHistory == null) reg.orderHistory = new List<OrderHistoryEntry>();
            int lo = gi.Day - HistoryDays, hi = gi.Day;
            // Slice 7a: the Insight carry writes totalCustomers/hourReports on the SAME window this
            // REPLACE covers — keep the outgoing entries so those fields survive a pricing refresh
            // (they re-attach below; days the snapshot omits are re-added with their sales cleared).
            var keepCustomers = new Dictionary<int, OrderHistoryEntry>();
            foreach (var h in reg.orderHistory)
                if (h != null && h.dayNumber >= lo && h.dayNumber <= hi) keepCustomers[h.dayNumber] = h;
            reg.orderHistory.RemoveAll(h => h == null || (h.dayNumber >= lo && h.dayNumber <= hi));
            int rows = 0;
            foreach (var d in p.Days)
            {
                if (d == null) continue;
                var e = new OrderHistoryEntry
                {
                    dayNumber = d.DayNumber + shift,
                    itemSales = new List<OrderHistoryEntry.ItemReport>(),
                    hourReports = new List<OrderHistoryEntry.HourReport>(),   // never null: the game LINQs over these
                };
                if (keepCustomers.TryGetValue(e.dayNumber, out var old))
                {
                    e.totalCustomers = old.totalCustomers;
                    if (old.hourReports != null && old.hourReports.Count > 0) e.hourReports = old.hourReports;
                }
                float revenue = 0f;
                foreach (var it in d.Items)
                {
                    if (it == null || string.IsNullOrEmpty(it.ItemName)) continue;
                    e.itemSales.Add(new OrderHistoryEntry.ItemReport
                    {
                        itemName = it.ItemName, amountSold = it.AmountSold, totalPrice = it.TotalPrice,
                        itemSoldBreakdownEntries = Array.Empty<ItemSoldPerPriceEntry>(),   // the per-price tooltip has no data here
                    });
                    revenue += it.TotalPrice;
                    rows++;
                }
                e.totalRevenue = revenue;
                reg.orderHistory.Add(e);
            }
            // Days the pricing snapshot omits had no sales — re-add their kept entry (customers intact,
            // sales cleared) so the Insight chart never loses a day to a pricing refresh.
            foreach (var kv in keepCustomers)
                if (!reg.orderHistory.Exists(h => h != null && h.dayNumber == kv.Key))
                { kv.Value.itemSales?.Clear(); kv.Value.totalRevenue = 0f; reg.orderHistory.Add(kv.Value); }
            Plugin.Logger.LogInfo($"{Tag} applied the owner's figures for '{p.AddressKey}': {p.Days.Count} day(s), {rows} row(s), {p.Products?.Count ?? 0} product(s) on sale.");
            RefreshPricingTabIfOpen(p.AddressKey);
        }

        private static void RefreshPricingTabIfOpen(string addressKey)
        {
            try
            {
                // NOT gated on our editing session: the OWNER has none, and their page is exactly the one that
                // showed stale numbers after a helper's edit (the tab raises no change event of its own).
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var page = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                if (page == null || AddrOf(page.buildingRegistration) != addressKey) return;
                var tab = page.bizManInventoryPricing;
                if (tab != null && tab.gameObject.activeInHierarchy) tab.RefreshData();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} pricing refresh: {ex.Message}"); }
        }

        // ── the tab: open / close, baseline, request ──

        [HarmonyPatch(typeof(BizManInventoryPricing), nameof(BizManInventoryPricing.RefreshData))]
        public static class Patch_InventoryPricing_RefreshData_Session
        {
            static void Postfix()
            {
                try
                {
                    var reg = OpenPageReg();
                    string addr = AddrOf(reg);
                    if (reg == null || !PriceRouted(reg, addr)) { CloseSession(); return; }
                    bool reopened = _openAddr != addr;
                    _openAddr = addr;
                    if (reopened) { _baseline.Clear(); _dirty.Clear(); }   // _held is address-keyed and expires on its own — never cleared here
                    // Baseline = what the owner's copy says right now. Every later comparison is against this, so a
                    // row merely being redrawn (the game rewrites the same number into the field) is never an edit.
                    if (reg.retailPrices != null)
                        foreach (var rp in reg.retailPrices)
                            if (rp != null && rp.itemName != null && !_baseline.ContainsKey(rp.itemName)) _baseline[rp.itemName] = rp.price;
                    // Wave 2 (2026-09-11) widened all three gates of the snapshot — requester (here), host
                    // (MPServer.HostRouteSharedSalesHistory) and owner (OwnerAnswerHistory above) — to the grant
                    // UNION, so a company member's NATIVE pricing tab on a merger-flipped partner shop lists the
                    // owner's products and recent sales instead of the replica's empty ones. Same predicate as the
                    // wave-1 price routing, so the tab that asks is exactly the tab whose edits route.
                    if (reopened && PriceRouted(reg, addr)) RequestHistory(addr, SharedShopSchedule.IsMergedShop(reg, addr));
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} pricing session: {ex.Message}"); }
            }
        }

        /// <summary>Closing the session. BizManInventoryPricing has NO OnDisable (its methods are Awake, OnEnable,
        /// RefreshData and the three Refresh* helpers) — patching one cost a failed hook and a startup warning on
        /// 2026-08-22. The BizMan PAGE's OnDisable is the real seam, and it covers every way the tab goes away:
        /// closing the app, switching business, or switching tab (which re-runs RefreshData and re-opens anyway).</summary>
        [HarmonyPatch(typeof(BizManBusiness), "OnDisable")]
        public static class Patch_BizManBusiness_OnDisable_PriceSession
        {
            static void Postfix() { try { CloseSession(); } catch { } }
        }

        private static void CloseSession()
        {
            if (_openAddr.Length == 0) return;
            _openAddr = ""; _baseline.Clear(); _dirty.Clear();   // _held survives: it is address-keyed and expires on time
        }

        private static void RequestHistory(string addr, bool merged = false)
        {
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            var p = new SharedSalesHistoryPayload { PlayerId = MPConfig.PlayerId, Action = "request", AddressKey = addr };
            Plugin.Logger.LogInfo($"{Tag} pricing tab opened on '{addr}'{(merged ? " (merged)" : "")} — asking the owner for its recent sales.");
            if (MPServer.IsRunning) MPServer.HostRouteSharedSalesHistory(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedSalesHistory, MPConfig.PlayerId, p));
        }

        // ── the editor row ──

        /// <summary>The game's price write lives in an anonymous listener with no named method to postfix, so OUR
        /// listener is appended to the same event: it runs after the game's, reads the value the game has just
        /// committed, and records it. No compiler-generated lambda is patched (those names shift between builds —
        /// round-96's retired signature-scanned patch is the standing warning).</summary>
        [HarmonyPatch(typeof(InventoryProductCellView), "Start")]
        public static class Patch_InventoryProductCellView_Start_Listen
        {
            static void Postfix(InventoryProductCellView __instance)
            {
                try
                {
                    var field = __instance != null ? __instance.retailPrice?.tmpInputField : null;   // 1.0: retailPrice is the game's InputField wrapper now
                    if (field == null) return;
                    field.onValueChanged.AddListener(delegate (string _)
                    {
                        try
                        {
                            if (_openAddr.Length == 0) return;
                            var model = _fCellModel?.GetValue(__instance);
                            string item = HousingMapCues.GetMember(HousingMapCues.GetMember(model, "Item"), "itemName") as string;
                            if (string.IsNullOrEmpty(item)) return;
                            var priceRef = HousingMapCues.GetMember(model, "RetailPriceReference");
                            if (HousingMapCues.GetMember(priceRef, "price") is not float price) return;
                            _dirty[item] = (price, Time.unscaledTime);   // the 1 s quiet timer restarts on every keystroke
                        }
                        catch { }
                    });
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} price listener: {ex.Message}"); }
            }
        }

        private static readonly System.Reflection.FieldInfo _fCellModel = AccessTools.Field(typeof(InventoryProductCellView), "_data");

        // The units-on-hand substitution moved to SharedShopStock (2026-08-26): the deliveries tab needs the
        // same figures over a WIDER set of items, and two copies of one mechanism is the shape that gets fixed
        // in one place and forgotten in the other (ruling 37).

        /// <summary>The native price guide asks "does a rival nearby sell this?" as "someone else owns it AND I do not
        /// rent it" — on this machine the shop being priced answers yes to both and competes with ITSELF, shifting the
        /// yellow/red guides away from what the owner sees. Tenancy is raised for that one scan and lowered after.</summary>
        [HarmonyPatch(typeof(InventoryProductCellView), nameof(InventoryProductCellView.SetData))]
        public static class Patch_InventoryProductCellView_SetData_Competitor
        {
            static void Prefix(out (BuildingRegistration reg, bool raised) __state)
            {
                __state = (null, false);
                try
                {
                    var reg = OpenPageReg();
                    string addr = AddrOf(reg);
                    if (reg == null || !SharedShopSchedule.IsSharedShop(reg, addr)) return;
                    __state = (reg, SharedShopVisibility.RaiseTenancy(reg, addr));
                }
                catch { }
            }
            static void Finalizer((BuildingRegistration reg, bool raised) __state)
            {
                try { SharedShopVisibility.LowerTenancy(__state.reg, __state.raised); } catch { }
            }
        }
    }
}
