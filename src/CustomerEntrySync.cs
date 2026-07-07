using System;
using System.Collections.Generic;
using AI.Customers.CustomerEntries;
using BigAmbitions.DayNightCycle;
using Buildings;
using Entities;
using HarmonyLib;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>Round-39d — Phase 3 (register work) slice 2, step 1: CUSTOMER PRESENCE on non-owner
    /// machines. NPC shoppers are machine-local: IndoorCustomerSpawner spawns them from
    /// CustomerEntriesHelper's per-address CustomerEntry list, gated on isOpen + customerCapacity. That
    /// list gets player-business entries ONLY on the owner's machine (UpdateCustomerEntriesForPlayerBusiness
    /// early-returns on !RentedByPlayer), and the AI fallback (GenerateAiEntries) permanently CACHES an
    /// EMPTY list when it runs before the shop has products — so a helper in the owner's shop saw no
    /// shoppers, ever (field-confirmed 2026-07-07). The owner's entries ride the interior snapshot
    /// (BuildSnapshot → CustomerEntries); receivers seed their local table here and the game's own
    /// spawner takes over. Guest-side commerce stays COSMETIC for now — every economic step gates on the
    /// unflipped IsPlayerOwnedBusiness (order recording, revenue, bag charges), so nothing double-counts;
    /// order forwarding is slice-2 step 2.</summary>
    internal static class CustomerEntrySync
    {
        private const int MaxEntries = 300;   // wire sanity cap; a shop day is typically tens

        private static readonly System.Reflection.FieldInfo? _entriesField =
            AccessTools.Field(typeof(CustomerEntriesHelper), "BusinessCustomerEntries");

        private static Dictionary<Address, List<CustomerEntry>>? Table()
            => _entriesField?.GetValue(null) as Dictionary<Address, List<CustomerEntry>>;

        // ── Round-39f: entry identity (the order-forwarding claim key) ─────────────────────────────
        // Spawn times MUTATE (TrySpawnCustomer rewrites spawnTime/order.timestamp on late spawns), so
        // identity is a minted GUID: the OWNER stamps each entry at first capture (weak-keyed — dies
        // with the entry object, so regenerated schedules mint fresh ids); receivers map each seeded
        // entry's ORDER object to the id (the live Customer holds that Order ref through the whole
        // shopping trip, surviving table re-seeds).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CustomerEntry, string> _ownerIds = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Order, string> _seededOrderIds = new();
        private static int _idCounter;

        private static string IdOf(CustomerEntry e)
        {
            if (_ownerIds.TryGetValue(e, out var id)) return id;
            id = $"{MPConfig.PlayerId}-{System.Threading.Interlocked.Increment(ref _idCounter)}";
            _ownerIds.Add(e, id);
            return id;
        }

        /// <summary>The synced-schedule id of a PAID order on a receiver machine — null for orders that
        /// didn't come from the owner's schedule (nothing to forward against).</summary>
        internal static string? EntryIdOf(Order order)
            => order != null && _seededOrderIds.TryGetValue(order, out var id) ? id : null;

        /// <summary>OWNER side (BuildSnapshot): capture this business's pending shopper schedule.</summary>
        internal static List<CustomerEntryInfo> CaptureFor(BuildingRegistration reg)
        {
            var list = new List<CustomerEntryInfo>();
            try
            {
                var table = Table();
                if (table == null || reg == null || !table.TryGetValue(reg.Address, out var entries) || entries == null)
                    return list;
                for (int i = 0; i < entries.Count && list.Count < MaxEntries; i++)
                {
                    var e = entries[i];
                    if (e?.spawnTime == null) continue;
                    var dto = new CustomerEntryInfo
                    {
                        EntryId     = IdOf(e),
                        SpawnDay    = e.spawnTime.Day,
                        SpawnHour   = e.spawnTime.Hour,
                        SpawnMinute = e.spawnTime.Minute,
                        Completed   = e.completed,
                    };
                    var oes = e.order?.entries;
                    if (oes != null)
                        for (int j = 0; j < oes.Count; j++)
                        {
                            var oe = oes[j];
                            if (oe == null || string.IsNullOrEmpty(oe.itemName)) continue;
                            dto.Items.Add(new OrderEntryInfo
                            {
                                ItemName       = oe.itemName,
                                Price          = oe.price,
                                WholesalePrice = oe.wholesalePrice,
                            });
                        }
                    var dem = e.order?.customerDemandTypes;
                    if (dem != null)
                        for (int j = 0; j < dem.Count; j++)
                            if (!string.IsNullOrEmpty(dem[j])) dto.Demands.Add(dem[j]);
                    list.Add(dto);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] capture: {ex.Message}"); }
            return list;
        }

        /// <summary>RECEIVER side (interior apply): seed the local entry table so the native spawner
        /// produces the owner's shoppers. Local completed flags are preserved by identity key so a
        /// re-push never re-spawns a customer this machine already spawned. Never runs on the owner
        /// (their table is the source). MAIN THREAD.</summary>
        internal static void SeedFor(BuildingRegistration reg, List<CustomerEntryInfo> entries)
        {
            try
            {
                if (reg == null || entries == null) return;
                if (reg.RentedByPlayer) return;   // I own it → my table is authoritative
                var table = Table();
                if (table == null) return;

                // EntryIds this machine already SPAWNED (a re-push must never re-spawn them).
                var doneLocally = new HashSet<string>();
                if (table.TryGetValue(reg.Address, out var prev) && prev != null)
                    foreach (var p in prev)
                        if (p != null && p.completed && p.order != null
                            && _seededOrderIds.TryGetValue(p.order, out var pid))
                            doneLocally.Add(pid);

                var fresh = new List<CustomerEntry>(entries.Count);
                foreach (var d in entries)
                {
                    if (d == null) continue;
                    var ce = new CustomerEntry
                    {
                        spawnTime = new Timestamp(d.SpawnDay, d.SpawnHour, d.SpawnMinute),
                        completed = d.Completed,
                    };
                    if (d.Items != null)
                        foreach (var it in d.Items)
                        {
                            if (it == null || string.IsNullOrEmpty(it.ItemName)) continue;
                            ce.order.entries.Add(new OrderEntry
                            {
                                itemName       = it.ItemName,
                                price          = it.Price,
                                wholesalePrice = it.WholesalePrice,
                            });
                        }
                    if (d.Demands != null)
                        foreach (var dm in d.Demands)
                            if (!string.IsNullOrEmpty(dm)) ce.order.customerDemandTypes.Add(dm);
                    ce.order.timestamp = ce.spawnTime;
                    if (!string.IsNullOrEmpty(d.EntryId))
                    {
                        _seededOrderIds.Add(ce.order, d.EntryId);   // fresh Order object each seed → no key clash
                        if (!ce.completed && doneLocally.Contains(d.EntryId))
                            ce.completed = true;   // already spawned here — don't spawn twice
                    }
                    fresh.Add(ce);
                }
                table[reg.Address] = fresh;

                // The spawner refuses when customerCapacity is 0 — the replica's is often unset (the
                // native computation runs owner-side). Data-level recompute from the synced items.
                try { BusinessHelper.UpdateCustomerCapacity(reg); } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Customers] seed: {ex.Message}"); }
        }

        // ── Round-39f: owner-side adoption of a helper-machine sale ────────────────────────────────

        /// <summary>Idempotency ledger — every forwarded EntryId is processed at most once per session.</summary>
        private static readonly HashSet<string> _processedForwards = new();

        /// <summary>OWNER, MAIN THREAD: a customer order paid on a helper's machine arrives. Claim the
        /// source entry (my table is the single-writer ledger: already-completed = my machine spawned or
        /// served that customer → reject, my side counts it), deduct the sold items + a paper bag from
        /// REAL stock (a forwarded sale must drain shelves like a live one, or revenue is minted from
        /// nothing), record the order in reg.unprocessedCompletedOrders (the hourly calculator subtracts
        /// it from the simulated quota — native anti-double-count), and re-sync.</summary>
        internal static void OwnerAdoptForwardedOrder(HelperOrderPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.EntryId)) return;
                if (_processedForwards.Contains(p.EntryId)) return;

                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                BuildingRegistration? reg = null;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == p.AddressKey) { reg = r; break; }
                if (reg == null || !reg.RentedByPlayer)
                {
                    Plugin.Logger.LogWarning($"[Business] forwarded order for '{p.AddressKey}' — not my business, dropped.");
                    return;
                }

                // Claim. Completed on MY table = my spawner consumed that customer (I'm inside; my live
                // body will/did complete it natively) — reject so it counts exactly once.
                bool claimed = false, known = false;
                var table = Table();
                if (table != null && table.TryGetValue(reg.Address, out var entries) && entries != null)
                    foreach (var e in entries)
                    {
                        if (e == null || !_ownerIds.TryGetValue(e, out var id) || id != p.EntryId) continue;
                        known = true;
                        if (e.completed) { _processedForwards.Add(p.EntryId); }
                        else             { e.completed = true; claimed = true; }
                        break;
                    }
                if (known && !claimed)
                {
                    Plugin.Logger.LogInfo($"[Business] forwarded order {p.EntryId} rejected — entry already consumed on the owner's machine (counts natively).");
                    return;
                }
                // Unknown id = the schedule rotated since the helper's copy; adopt anyway (the order
                // still displaces the sim quota) — logged for the audit trail.

                // Deduct real stock: one display-slot unit per paid item; items with no stock left are
                // dropped from the sale (the shop can't sell what it doesn't have).
                var o = new Order { completed = true, timestamp = TimeHelper.Now() };
                try { o.cleanliness = Buildings.BuildingTypes.Shared.Dirtiness.BuildingCleanlinessHelper.GetCleanliness(reg); } catch { }
                int sold = 0, dropped = 0;
                if (p.Items != null)
                    foreach (var it in p.Items)
                    {
                        if (it == null || string.IsNullOrEmpty(it.ItemName)) continue;
                        if (DeductDisplayStock(reg, it.ItemName))
                        {
                            o.entries.Add(new OrderEntry
                            {
                                itemName       = it.ItemName,
                                price          = it.Price,
                                wholesalePrice = it.WholesalePrice,
                                available      = true,
                                paid           = true,
                            });
                            sold++;
                        }
                        else dropped++;
                    }
                if (sold == 0)
                {
                    _processedForwards.Add(p.EntryId);
                    Plugin.Logger.LogInfo($"[Business] forwarded order {p.EntryId} had no coverable items (stock raced away) — dropped.");
                    return;
                }

                // Paper bag: the sale consumes one from the register, priced like a live checkout
                // (FullServiceEmployee :113 uses the register slot's pricePerUnit as the bag wholesale).
                bool bagged = false;
                try
                {
                    bool needsBags = true;
                    try { needsBags = BusinessTypeHelper.GetData(reg)?.HasTag(BigAmbitions.Tags.TagRef.Businesstag.customersneedpaperbags) ?? true; } catch { }
                    if (needsBags)
                        foreach (var kv in reg.itemInstances)
                        {
                            var ii = kv.Value;
                            if (ii?.ItemCached == null || (ii.ItemCached.type & BigAmbitions.Items.ItemType.PointOfSale) == 0) continue;
                            var slot = (ii.cargoInstances != null && ii.cargoInstances.Count == 1) ? ii.cargoInstances[0] : null;
                            if (slot == null || string.IsNullOrEmpty(slot.itemName) || slot.amount <= 0) continue;
                            o.AddPaperBagEntry(slot.pricePerUnit);
                            slot.amount--;
                            try { ii.OnItemsInCargoUpdated()?.Invoke(); } catch { }
                            bagged = true;
                            break;
                        }
                }
                catch { }

                reg.unprocessedCompletedOrders.Add(o);
                _processedForwards.Add(p.EntryId);
                BuildingStorageSync.OwnerBusinessTail(reg);
                InteriorSync.PushOwnedBuildingNow(p.AddressKey);
                Plugin.Logger.LogInfo($"[Business] adopted helper-served order {p.EntryId} from '{p.PlayerId}' @'{p.AddressKey}': {sold} item(s){(bagged ? " +bag" : "")}{(dropped > 0 ? $" ({dropped} out-of-stock dropped)" : "")}{(known ? "" : " (entry unknown — schedule rotated)")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Business] adopt forwarded order: {ex.Message}"); }
        }

        /// <summary>Reduce ONE unit of <paramref name="itemName"/> from a display (showcase-shelf) stock
        /// slot — what a live customer's grab does physically. Display-only on purpose: customers never
        /// shop from storage.</summary>
        private static bool DeductDisplayStock(BuildingRegistration reg, string itemName)
        {
            try
            {
                if (reg.itemInstances == null) return false;
                foreach (var kv in reg.itemInstances)
                {
                    var ii = kv.Value;
                    if (ii?.ItemCached == null || (ii.ItemCached.type & BigAmbitions.Items.ItemType.ShowcaseShelf) == 0) continue;
                    var slots = ii.cargoInstances;
                    if (slots == null) continue;
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var s = slots[i];
                        if (s == null || s.itemName != itemName || s.amount <= 0) continue;
                        s.amount--;
                        try { ii.OnItemsInCargoUpdated()?.Invoke(); } catch { }
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
