using System.Collections.Generic;
using System.Linq;
using Helpers;   // FinancialSummaryHelper

namespace BigAmbitionsMP
{
    /// <summary>
    /// The LOCAL player's rival-sheet stats, computed exactly the way the NATIVE self-sheet computes them —
    /// the single source both the host (BuildRivalsStatsSnapshot's own-stats block) and the client
    /// (SendRivalsStatsRequest's self-report) publish, so what other players see for you is what you see for
    /// yourself (user principle, round-25). One code path = host and client can't drift apart again.
    ///
    /// Native references (decompile, EA 0.11):
    ///   • header weekly income — SelectedRivalUI:252: FinancialSummaryHelper.GetLastFinancialSummaries(7)
    ///     .Sum(x => x.totalProfit). (The old publication used Σ RentPerDay×7 — RENT EXPENSE, not income —
    ///     while shipping real per-business rows in the same payload: header ≠ Σ rows, the reported bug.)
    ///   • business rows/count — the player's non-residential rented operations, each with the live
    ///     GetAvgWeeklyIncome() (only computable on the owning machine, where the order history lives).
    ///     The residence is a HOME, not a business (the old row list leaked it).
    ///   • primary neighborhood — group own businesses by Neighborhood, take the highest summed income
    ///     (the native self grouping; was never published for players at all).
    /// </summary>
    internal static class RivalSelfStats
    {
        internal static void Build(out int ownedBuildings, out float weeklyIncome, out string neighborhood,
                                   out List<RivalBusinessInfo> rows)
        {
            ownedBuildings = 0; weeklyIncome = 0f; neighborhood = ""; rows = new List<RivalBusinessInfo>();
            try
            {
                try { weeklyIncome = FinancialSummaryHelper.GetLastFinancialSummaries(7).Sum(x => x.totalProfit); }
                catch { }

                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                var hoodIncome = new Dictionary<string, float>();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    try
                    {
                        if (reg.BuildingOwnedByPlayer) ownedBuildings++;
                        bool isResidential = false;
                        try { isResidential = reg.BuildingCached != null && reg.BuildingCached.BuildingType == "ba:buildingtype_residential"; } catch { }
                        if (!reg.RentedByPlayer || isResidential) continue;

                        float wk = 0f;
                        try { wk = reg.GetAvgWeeklyIncome(); } catch { }
                        var row = new RivalBusinessInfo
                        {
                            AddressKey   = GameStateReader.AddressKey(reg),
                            BusinessName = reg.BusinessName?.ToString() ?? "",
                            BusinessType = reg.businessTypeName ?? "",
                            WeeklyIncome = wk,
                        };
                        FillRivalFields(reg, row);   // H-RIVALPARITY-1 A (P3)
                        rows.Add(row);

                        string hood = "";
                        try { hood = reg.Neighborhood ?? ""; } catch { }
                        if (hood.Length > 0)
                        {
                            hoodIncome.TryGetValue(hood, out var s);
                            hoodIncome[hood] = s + wk;
                        }
                    }
                    catch { }
                }
                float best = float.MinValue;
                foreach (var kv in hoodIncome)
                    if (kv.Value > best) { best = kv.Value; neighborhood = kv.Key; }
            }
            catch { }
        }

        /// <summary>H-RIVALPARITY-1 part A (P3, 2026-09-27): the per-player rival fields of one row, computed HERE on the
        /// owner's machine - the only place the order history lives. RivalQualifying is the game's own
        /// RivalTimeline.GetPlayerValues test (decompile RivalTimeline.cs:290-293) in the same order; its RentedByPlayer
        /// half is the caller's (every row is a rented shop) and its neighbourhood half is the host's (it compares
        /// Neighborhood). Sold7d / Selling: the last 7 days' sold amounts per item and the items on the price list.</summary>
        private static void FillRivalFields(BuildingRegistration reg, RivalBusinessInfo row)
        {
            try { row.Neighborhood = reg.Neighborhood ?? ""; } catch { }
            try { row.AvgDaily7 = reg.GetAvgDailyIncome(7); } catch { }
            int day = 0;
            try { day = SaveGameManager.Current.Day; } catch { }
            try
            {
                var data = BusinessTypeHelper.GetData(reg);
                row.RivalQualifying = !(data == null)
                    && data.HasTag(BigAmbitions.Tags.TagRef.Businesstag.generatesrevenue)
                    && data.HasTag(BigAmbitions.Tags.TagRef.Businesstag.allowplayercreation)
                    && global::Buildings.BuildingTypeHelper.GetData(reg).HasTag(BigAmbitions.Tags.TagRef.Buildingtypetag.showinbusinesslist)
                    && !(reg.businessTypeName == "ba:businesstype_headquarters")
                    && reg.orderHistory != null
                    && reg.orderHistory.Any(o => o != null && o.dayNumber > day - 7 && o.itemSales != null && o.itemSales.Any(s => s != null && s.amountSold > 0));
            }
            catch { row.RivalQualifying = false; }
            try
            {
                var sold = new Dictionary<string, int>();
                if (reg.orderHistory != null)
                    foreach (var o in reg.orderHistory)
                    {
                        if (o == null || o.dayNumber <= day - 7 || o.itemSales == null) continue;
                        foreach (var s in o.itemSales)
                        {
                            if (s == null || s.amountSold <= 0 || string.IsNullOrEmpty(s.itemName)) continue;
                            sold.TryGetValue(s.itemName, out var c);
                            sold[s.itemName] = c + s.amountSold;
                        }
                    }
                row.Sold7d = sold;
            }
            catch { }
            try
            {
                var selling = new List<string>();
                if (reg.retailPrices != null)
                    foreach (var rp in reg.retailPrices)
                        if (rp != null && !string.IsNullOrEmpty(rp.itemName) && !selling.Contains(rp.itemName)) selling.Add(rp.itemName);
                row.Selling = selling;
            }
            catch { }
        }
    }
}
