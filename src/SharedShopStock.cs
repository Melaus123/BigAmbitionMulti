using System.Collections.Generic;
using Buildings;                               // BuildingRegistration
using HarmonyLib;
using Helpers;                                 // BuildingHelper

namespace BigAmbitionsMP
{
    /// <summary>
    /// SHARED-SHOP MANAGEMENT — the ONE place a helper's owner-counted figures come from: units on hand,
    /// and (deliveries only) units sold in the last seven days.
    ///
    /// Every BizMan surface that prints stock asks `BuildingHelper.CountTotalResourcesInStock`, which walks
    /// that building's INTERIOR items. On the owner's machine that is the truth; on a helper's it walks their
    /// replica, and a building's interior only reaches players standing inside it (InteriorSync subscribes on
    /// entry) — so from anywhere else the count reads zero, or whatever was there on the last visit. Two field
    /// reports of the same defect: pricing 2026-08-22, deliveries 2026-08-26.
    ///
    /// The owner counts with the game's own routine and sends the numbers with that tab's snapshot; this holds
    /// them and ONE postfix substitutes them while that shop's shared surface is the one on screen. Outside
    /// that the native count stands everywhere, for every building.
    ///
    /// Tables are keyed PER SURFACE as well as per address (review M1). The two surfaces carry different item
    /// sets, and a shared per-address table meant a pricing visit silently replaced the deliveries table with a
    /// narrower one: every row outside the shop's sale list — the business type's primary products, paper bags,
    /// rows for delisted items — then fell through to the replica's own count, unlogged, and the deliveries
    /// tab's silent-if-unchanged refresh had no reason to correct it.
    ///
    /// COVERAGE differs per surface, and the difference is load-bearing:
    ///  • deliveries — COMPLETE. The owner sends a figure for every row that can appear (the tab's whole edit
    ///    domain plus every row already on a contract), so an item held with no figure reads 0 and logs once
    ///    rather than being quietly filled in from the replica. Plausible-wrong must never pose as owner truth.
    ///  • pricing — PARTIAL. The owner sends the shop's sale list, which is exactly the rows that tab draws.
    ///    Anything else falls through to the native count, as it has since 2026-08-22.
    /// </summary>
    public static class SharedShopStock
    {
        private const string Tag = "[SharedShop]";
        internal const string Deliveries = "deliveries";
        internal const string Pricing    = "pricing";

        private static readonly Dictionary<string, Dictionary<string, int>> _byKey = new();
        // "Sold last week" is carried for the DELIVERIES surface only — the pricing tab does not use the
        // native helper it substitutes, so there is no second surface to key against.
        private static readonly Dictionary<string, Dictionary<string, int>> _soldByAddr = new();
        private static readonly HashSet<string> _logged = new();

        private static string KeyOf(string surface, string addr) => surface + "|" + addr;

        public static void Reset() { _byKey.Clear(); _soldByAddr.Clear(); _logged.Clear(); }

        /// <summary>Replace one surface's counts for one shop. The table survives that surface closing
        /// (ruling 36: a reopened tab renders held truth at once); the postfix gate, not the table's lifetime,
        /// is what keeps the substitution scoped.</summary>
        internal static void Set(string surface, string addressKey, IEnumerable<StockInfo>? rows)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            var map = new Dictionary<string, int>();
            if (rows != null)
                foreach (var r in rows)
                    if (r != null && !string.IsNullOrEmpty(r.ItemName)) map[r.ItemName] = r.Count;
            _byKey[KeyOf(surface, addressKey)] = map;
        }

        /// <summary>The owner's "units sold in the last 7 days" per item, for one shop's deliveries tab.</summary>
        internal static void SetSold(string addressKey, IEnumerable<StockInfo>? rows)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            var map = new Dictionary<string, int>();
            if (rows != null)
                foreach (var r in rows)
                    if (r != null && !string.IsNullOrEmpty(r.ItemName)) map[r.ItemName] = r.Count;
            _soldByAddr[addressKey] = map;
        }

        /// <summary>Drop everything held for one shop — carried owner figures must not outlive the grant that
        /// justified them.</summary>
        internal static void Clear(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return;
            _byKey.Remove(KeyOf(Deliveries, addressKey));
            _byKey.Remove(KeyOf(Pricing, addressKey));
            _soldByAddr.Remove(addressKey);
            // The "we held no figure" keys go too (review MINOR): otherwise a genuine NEW coverage gap for the
            // same shop after a re-grant is suppressed for the rest of the session and the 0 appears unexplained.
            // One clause is enough and is the safer one: every key is "<kind>|<addr>|<item>", so an
            // EndsWith test could never match a real key, and could only ever fire on an item name that
            // happened to equal an address — deleting a DIFFERENT shop's entry (fix-verification NIT).
            _logged.RemoveWhere(k => k.Contains("|" + addressKey + "|"));
        }

        /// <summary>Units on hand, as the OWNER counted them, for the shared surface currently on screen.
        /// Scoped to that one screen and that one address: everywhere else the native count is untouched.</summary>
        // 1.0 update 2026-09-01 added a 5th parameter (bool includeBoxItemInstances = true); the old 4-type
        // shape no longer resolves ("Undefined target method", field 20260901-132829) and the patch went dead.
        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.CountTotalResourcesInStock),
                      typeof(BuildingRegistration), typeof(string), typeof(bool), typeof(bool), typeof(bool))]
        public static class Patch_CountTotalResourcesInStock_OwnerFigure
        {
            static void Postfix(BuildingRegistration buildingRegistration, string itemName, ref int __result)
            {
                try
                {
                    if (_byKey.Count == 0 || buildingRegistration == null || string.IsNullOrEmpty(itemName)) return;
                    // Cheapest gate FIRST (review MINOR): this is a postfix on a general-purpose counting
                    // routine that other native systems call every frame, and building an address key
                    // allocates a string. No shared surface open → get out before that.
                    string delivAddr = SharedShopWorkTabs.OpenTab == Deliveries ? SharedShopWorkTabs.OpenAddr : "";
                    string priceAddr = SharedShopPrices.OpenAddr;
                    if (delivAddr.Length == 0 && priceAddr.Length == 0) return;
                    string addr = GameStateReader.AddressKey(buildingRegistration);
                    if (string.IsNullOrEmpty(addr)) return;
                    // The deliveries branch is TERMINAL, table or no table (fix-verification M1): the pricing
                    // session's address is NOT cleared when the helper switches tabs on the same shop, so a
                    // chained fall-through let the narrower pricing table answer deliveries rows in the gap
                    // before the first deliveries snapshot lands — the exact silent mis-substitution this
                    // per-surface keying exists to close.
                    if (delivAddr == addr)
                    {
                        // COMPLETE coverage: an item with no carried figure is a gap in OUR carry, not a
                        // licence to show this machine's own count of a shop it does not hold.
                        if (_byKey.TryGetValue(KeyOf(Deliveries, addr), out var dmap)
                            && dmap.TryGetValue(itemName, out var owned)) { __result = owned; return; }
                        if (_logged.Add("nostock|" + addr + "|" + itemName))
                            Plugin.Logger.LogWarning($"{Tag} no owner stock figure for '{itemName}' at '{addr}' — showing 0 rather than this machine's own count.");
                        __result = 0;
                        return;
                    }
                    if (priceAddr == addr && _byKey.TryGetValue(KeyOf(Pricing, addr), out var pmap)
                        && pmap.TryGetValue(itemName, out var pOwned))
                        __result = pOwned;    // PARTIAL: anything unlisted keeps the native count, as before
                }
                catch { }
            }
        }

        /// <summary>"Sold last week" on an expanded deliveries row. Native sums THIS shop's own orderHistory
        /// over the last 7 days, and a replica's is empty — so the helper read 0, or, if they had opened
        /// Inventory &amp; Pricing for the same shop earlier in the session, the RIGHT number, because that tab's
        /// carry writes real itemSales onto the replica. A figure that is correct only depending on which tab
        /// you visited first is worse than one that is consistently wrong, because it looks trustworthy.
        ///
        /// Scoped exactly like the stock substitution, and deliberately NOT extended to the aggregate
        /// `ProductsSoldLastWeek(itemName)`: that one loops the player's own rented registrations, and a shared
        /// shop's replica only counts as rented inside our render window, which cannot overlap the purchasing
        /// agent's screen. So the owner's figures can never leak into the helper's own totals.</summary>
        [HarmonyPatch(typeof(FinancialSummaryHelper), nameof(FinancialSummaryHelper.ProductsSoldLastWeekInRegistration))]
        public static class Patch_ProductsSoldLastWeek_OwnerFigure
        {
            static void Postfix(string itemName, BuildingRegistration registration, ref int __result)
            {
                try
                {
                    if (_soldByAddr.Count == 0 || registration == null || string.IsNullOrEmpty(itemName)) return;
                    if (SharedShopWorkTabs.OpenTab != Deliveries || SharedShopWorkTabs.OpenAddr.Length == 0) return;
                    string addr = GameStateReader.AddressKey(registration);
                    if (string.IsNullOrEmpty(addr) || addr != SharedShopWorkTabs.OpenAddr) return;
                    // COMPLETE coverage, same rule as stock: a row we hold no figure for reads 0 and logs,
                    // never the replica's own sum — which is either empty or, worse, a leftover from the
                    // pricing tab's carry that would pose as this tab's truth.
                    if (_soldByAddr.TryGetValue(addr, out var map) && map.TryGetValue(itemName, out var owned))
                    { __result = owned; return; }
                    if (_logged.Add("nosold|" + addr + "|" + itemName))
                        Plugin.Logger.LogWarning($"{Tag} no owner sold-last-week figure for '{itemName}' at '{addr}' — showing 0 rather than this machine's own sum.");
                    __result = 0;
                }
                catch { }
            }
        }
    }
}
