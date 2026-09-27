#nullable disable
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>
    /// H-RIVALPARITY-1 part E (P1, user-approved 2026-09-27; measured T-RIVALSELLERS-20260927-015430 /
    /// T-RIVALSELLERS2-20260927-015913). HOST ONLY.
    ///
    /// The game counts the sellers of every item per neighbourhood in ProductMarketHelper.FillProvidersDictionary
    /// (decompile Build 3682 ProductMarketHelper.cs:329-356; at load and daily from CompetitionHelper.RunDaily :103) and
    /// derives each neighbourhood's demand from that count (UpdateMarketDemand :249 -> CalculateDemand :403-410), which the
    /// estimated weekly income of every shop reads (EstimatedWeeklyIncomeHelper; the rival's daily valuation,
    /// CompetitionHelper.cs:225-236). A shop with an EMPTY product list (cachedAvailableProducts, :334) is skipped. On the
    /// host, the copy of ANOTHER player's shop loses its list at the host's first day-roll recount (the shelves live on the
    /// owner's machine), so after that recount the client's shops are no sellers on the host: the host's demand stays high
    /// and a client cannot lower a rival's income the way the host can.
    ///
    /// This postfix adds, after the game's own count, +1 per item for every OTHER player's shop whose host copy has an
    /// EMPTY product list - exactly the shops the game skipped, so nothing is counted twice - from that shop's self-report
    /// Selling list (RivalSelfStats: the owner's own cachedAvailableProducts, the list the owner's machine counts). The
    /// owner comes from the ownership ledger first (MPRivalFairness OwnerPidOf), never from RentedByPlayer: a merged
    /// partner's shop the ownership flip shows as the host's own is still counted once, for its real owner, as the merger
    /// authority veil does for every audited pass (MPPatches Patch_MergerAuthorityVeil). The owner's machine counts its
    /// own shop as RentedByPlayer, i.e. whatever its fill state (:350) - so does this. The host's own shops and AI shops
    /// are untouched. Clients keep receiving demand from the host (market snapshot).
    /// Settings key RivalClientSellers (default ON); false = the game's count alone (the behaviour before this change).
    /// </summary>
    [HarmonyPatch(typeof(Helpers.ProductMarketHelper), nameof(Helpers.ProductMarketHelper.FillProvidersDictionary))]
    public static class RivalClientSellers
    {
        private static Dictionary<(string, string), int> _table;
        private static bool _tableTried, _offLogged;

        /// <summary>The last recount's additions (DEV readout / log).</summary>
        internal static int LastAdded, LastShops, LastNoReport;

        private static Dictionary<(string, string), int> Table()
        {
            if (_table != null || _tableTried) return _table;
            _tableTried = true;
            try
            {
                var f = AccessTools.Field(typeof(Helpers.ProductMarketHelper), "ProvidersPerItemPerNeighborhood");
                _table = f?.GetValue(null) as Dictionary<(string, string), int>;
            }
            catch { _table = null; }
            if (_table == null) Plugin.Logger.LogWarning("[RivalSellers] the game's seller table (ProductMarketHelper.ProvidersPerItemPerNeighborhood) was not found - other players' shops are not added.");
            return _table;
        }

        /// <summary>The self-report row of the shop at addr: the owner pid's latest report, else any other pid known
        /// for the owner's stable id (a member reconnected under another pid).</summary>
        private static RivalBusinessInfo RowOf(string owner, string addr)
        {
            foreach (var r in MPServer.SelfReportRows(owner))
                if (r != null && r.AddressKey == addr) return r;
            string stable = "";
            try { MPServer.StableIdByPlayer.TryGetValue(owner, out stable); } catch { }
            if (string.IsNullOrEmpty(stable)) return null;
            try
            {
                foreach (var kv in MPServer.StableIdByPlayer)
                {
                    if (kv.Key == owner || kv.Value != stable) continue;
                    foreach (var r in MPServer.SelfReportRows(kv.Key))
                        if (r != null && r.AddressKey == addr) return r;
                }
            }
            catch { }
            return null;
        }

        static void Postfix()
        {
            try
            {
                if (!MPServer.IsRunning) return;
                if (!MPConfig.RivalClientSellersLive())
                {
                    if (!_offLogged) { _offLogged = true; Plugin.Logger.LogInfo("[RivalSellers] RivalClientSellers=false: the game's seller count alone (other players' shops with an empty product list on the host are not counted)."); }
                    return;
                }
                _offLogged = false;
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                var table = Table();
                if (table == null) return;
                int added = 0, shops = 0, noReport = 0;
                var detail = new List<string>();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    try
                    {
                        if (reg.AvailableForRent) continue;
                        if (reg.cachedAvailableProducts != null && reg.cachedAvailableProducts.Count > 0) continue;   // the game judged this shop itself
                        string owner = MPRivalFairness.Patch_CompetitionSeesAllPlayers.OwnerPidOf(reg);
                        if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) continue;   // the host's own / AI: native only
                        var building = Helpers.BuildingHelper.GetBuilding(reg.Address);
                        if (building == null || building.SpecialService != null) continue;
                        string addr = GameStateReader.AddressKey(reg);
                        var row = RowOf(owner, addr);
                        if (row == null) { noReport++; continue; }
                        if (row.Selling == null || row.Selling.Count == 0) continue;
                        string nb = building.Neighbourhood;
                        if (string.IsNullOrEmpty(nb)) continue;
                        shops++;
                        int n = 0;
                        foreach (var item in row.Selling)
                        {
                            if (string.IsNullOrEmpty(item)) continue;
                            var k = (item, nb);
                            table.TryGetValue(k, out int c);
                            table[k] = c + 1;
                            n++;
                        }
                        added += n;
                        detail.Add($"{addr}({owner}) +{n}");
                    }
                    catch { }
                }
                LastAdded = added; LastShops = shops; LastNoReport = noReport;
                if (added > 0 || noReport > 0)
                    Plugin.Logger.LogInfo($"[RivalSellers] seller count: +{added} item seller(s) from {shops} other player shop(s) with an empty product list on the host (their own self-report lists){(noReport > 0 ? $"; {noReport} such shop(s) without a self-report yet - not counted" : "")}: {string.Join(" ; ", detail)}.");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalSellers] error adding other players' shops to the seller count: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
