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
                        rows.Add(new RivalBusinessInfo
                        {
                            AddressKey   = GameStateReader.AddressKey(reg),
                            BusinessName = reg.BusinessName?.ToString() ?? "",
                            BusinessType = reg.businessTypeName ?? "",
                            WeeklyIncome = wk,
                        });

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
    }
}
