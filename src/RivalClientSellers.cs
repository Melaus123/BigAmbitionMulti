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
    /// This postfix corrects, after the game's own count, every OTHER player's shop that has a self-report row (fold E1,
    /// 2026-09-27): the shop's self-report REPLACES the game's count for it - whatever the host copy's product list holds.
    /// The host copy can be stale (the helper Pricing / Deliveries tabs overwrite it, SharedShopPrices.cs:362-364,
    /// SharedShopWorkTabs.cs:1840-1843; CompetitionHelper.cs:943 keeps old lists), so for each item of that list the -1
    /// is taken exactly where the game counted it (its own test, :350: RentedByPlayer || !checkFillState ||
    /// GetShelfFillState > 0, floored at 0), then +1 per item of the owner's Selling list (RivalSelfStats: the owner's own
    /// cachedAvailableProducts, the list the owner's machine counts). A shop with no self-report row keeps the game's
    /// count (the behaviour before this change). The owner comes from the ownership ledger first (MPRivalFairness
    /// OwnerPidOf), never from RentedByPlayer: a merged partner's shop the ownership flip shows as the host's own is
    /// still counted once, for its real owner, as the merger authority veil does for every audited pass (MPPatches
    /// Patch_MergerAuthorityVeil). The owner's machine counts its own shop as RentedByPlayer, i.e. whatever its fill
    /// state - so does this. The host's own shops, homes (the self-report leaves them out) and AI shops are untouched.
    /// Clients keep receiving demand from the host (market snapshot). The first self-report of each player in a session
    /// recounts the table at once (fold E3, OnSelfReport). Not folded (manager 2026-09-27): after a host restart the
    /// reports are gone until each owner reconnects - their shops return then.
    /// Settings key RivalClientSellers (default ON); false = the game's count alone (the behaviour before this change).
    /// </summary>
    [HarmonyPatch(typeof(Helpers.ProductMarketHelper), nameof(Helpers.ProductMarketHelper.FillProvidersDictionary))]
    public static class RivalClientSellers
    {
        private static Dictionary<(string, string), int> _table;
        private static bool _tableTried, _offLogged;

        /// <summary>The last recount's corrections (DEV readout / log).</summary>
        internal static int LastAdded, LastReplaced, LastShops, LastNoReport;

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

        /// <summary>The self-report row of the shop at addr. Fold E4: the NEWEST report among every pid that shares the
        /// owner's stable id (a member reconnected under another pid) is the one read - not the ledger pid's first.</summary>
        internal static RivalBusinessInfo RowOf(string owner, string addr)
        {
            if (string.IsNullOrEmpty(owner)) return null;
            string best = owner;
            double bestAge = MPServer.SelfReportAgeSeconds(owner);
            string stable = "";
            try { MPServer.StableIdByPlayer.TryGetValue(owner, out stable); } catch { }
            if (!string.IsNullOrEmpty(stable))
            {
                try
                {
                    foreach (var kv in MPServer.StableIdByPlayer)
                    {
                        if (kv.Key == owner || kv.Value != stable) continue;
                        double a = MPServer.SelfReportAgeSeconds(kv.Key);
                        if (a < bestAge) { bestAge = a; best = kv.Key; }
                    }
                }
                catch { }
            }
            if (bestAge == double.MaxValue) return null;
            foreach (var r in MPServer.SelfReportRows(best))
                if (r != null && r.AddressKey == addr) return r;
            return null;
        }

        /// <summary>Fold E7: the items ANOTHER player's shop sells by its owner's own self-report (null = no row: the
        /// caller keeps its own reading). Host only; never for the host's own shops.</summary>
        internal static List<string> SelfReportedSellingOf(string owner, string addr)
        {
            try
            {
                if (!MPServer.IsRunning) return null;
                if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId || GameStatePatcher.IsHostLedgerId(owner)) return null;
                var row = RowOf(owner, addr);
                if (row == null) return null;
                var list = new List<string>();
                if (row.Selling != null) foreach (var s in row.Selling) if (!string.IsNullOrEmpty(s)) list.Add(s);
                return list;
            }
            catch { return null; }
        }

        /// <summary>Fold E3 (HOST, poll thread): a player's FIRST self-report of the session recounts the seller table on
        /// the main thread with the daily recount's own call (CompetitionHelper.cs:103, FillProvidersDictionary() -
        /// checkFillState default true), so the table stops leaving that player's shops out before the next day roll.
        /// Called BEFORE the report is stored; the report tables are cleared at session start (MPServer :1503-1504).</summary>
        internal static void OnSelfReport(string pid)
        {
            try
            {
                if (string.IsNullOrEmpty(pid) || !MPServer.IsRunning) return;
                if (MPServer.SelfReportAgeSeconds(pid) != double.MaxValue) return;   // not this player's first report
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    try
                    {
                        if (!MPServer.IsRunning || SaveGameManager.Current?.BuildingRegistrations == null) return;
                        Plugin.Logger.LogInfo($"[RivalSellers] first self-report from '{pid}' this session - the seller table is recounted now (the daily recount's own call).");
                        Helpers.ProductMarketHelper.FillProvidersDictionary();
                    }
                    catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalSellers] error recounting after the first self-report from '{pid}': {ex.GetType().Name}: {ex.Message}"); }
                });
            }
            catch { }
        }

        static void Postfix(bool checkFillState)
        {
            try
            {
                if (!MPServer.IsRunning) return;
                if (!MPConfig.RivalClientSellersLive())
                {
                    if (!_offLogged) { _offLogged = true; Plugin.Logger.LogInfo("[RivalSellers] RivalClientSellers=false: the game's seller count alone (other players' shops are counted from their host copies)."); }
                    return;
                }
                _offLogged = false;
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                var table = Table();
                if (table == null) return;
                int added = 0, replaced = 0, shops = 0, noReport = 0;
                var detail = new List<string>();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    try
                    {
                        if (reg.AvailableForRent) continue;
                        string owner = MPRivalFairness.Patch_CompetitionSeesAllPlayers.OwnerPidOf(reg);
                        // E5: the host's own (either id) and AI: native only
                        if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId || GameStatePatcher.IsHostLedgerId(owner)) continue;
                        // E5: homes - the self-report leaves residential buildings out (RivalSelfStats :43-45), so no row is ever there
                        bool isResidential = false;
                        try { isResidential = reg.BuildingCached != null && reg.BuildingCached.BuildingType == "ba:buildingtype_residential"; } catch { }
                        if (isResidential) continue;
                        var building = Helpers.BuildingHelper.GetBuilding(reg.Address);
                        if (building == null || building.SpecialService != null) continue;
                        string nb = building.Neighbourhood;
                        if (string.IsNullOrEmpty(nb)) continue;
                        string addr = GameStateReader.AddressKey(reg);
                        var row = RowOf(owner, addr);
                        if (row == null)
                        {
                            // the game's count stands; only an ONLINE owner without a report is worth a line
                            if (MPServer.IsOnlinePid(owner)) noReport++;
                            continue;
                        }
                        // E1: take back exactly what the game counted for this shop (its own test, :350; floor 0)
                        int r = 0;
                        var cached = reg.cachedAvailableProducts;
                        if (cached != null)
                            foreach (var item in cached)
                            {
                                bool counted;
                                try { counted = reg.RentedByPlayer || !checkFillState || global::Controllers.PlayerItemPurchaser.GetShelfFillState(item, reg) > 0f; }
                                catch { counted = false; }
                                if (!counted) continue;
                                var k = (item, nb);
                                if (table.TryGetValue(k, out int c) && c > 0) { table[k] = c - 1; r++; }
                            }
                        // ... and count the owner's own list instead
                        int n = 0;
                        if (row.Selling != null)
                            foreach (var item in row.Selling)
                            {
                                if (string.IsNullOrEmpty(item)) continue;
                                var k = (item, nb);
                                table.TryGetValue(k, out int c);
                                table[k] = c + 1;
                                n++;
                            }
                        if (r == 0 && n == 0) continue;
                        shops++;
                        added += n;
                        replaced += r;
                        detail.Add($"{addr}({owner}) -{r} +{n}");
                    }
                    catch { }
                }
                LastAdded = added; LastReplaced = replaced; LastShops = shops; LastNoReport = noReport;
                if (added > 0 || replaced > 0 || noReport > 0)
                    Plugin.Logger.LogInfo($"[RivalSellers] seller count: +{added} item seller(s) from {shops} other player shop(s)' own self-report lists, replacing {replaced} the game counted from their host copies{(noReport > 0 ? $"; {noReport} shop(s) of online players without a self-report yet - the game's count stands" : "")}: {string.Join(" ; ", detail)}.");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalSellers] error correcting other players' shops in the seller count: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
