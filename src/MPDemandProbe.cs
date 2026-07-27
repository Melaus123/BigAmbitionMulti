// PROBE-START: Demand — round-102 (client sees all product demand at 99-100%)
// QUESTION: the diagnosis (providers==0 on clients because AI shops have no cached product
// lists) is high-confidence arithmetic. What is NOT known is why the host's 60s MarketSnapshot
// — which carries demand and which the reporter's client demonstrably received 4 times — does
// not correct it. Two candidates, and the fix differs per candidate:
//   (a) APPLY IS INERT: ApplyMarketSnapshot can only UPDATE pre-existing entries/neighborhood
//       rows; it cannot ADD a missing item entry or a missing neighborhood row (same dead-guard
//       class as the round-99 colors). If the client's rows are missing, the host's values land
//       nowhere.
//   (b) CLOBBER: a client-side recompute runs AFTER the apply and overwrites the host's values
//       with a providers==0 result. Client-reachable recompute paths that are NOT suppressed
//       today: ProductMarketHelper.OnInitializeCity, the OnGameLoaded callback registered in
//       Init, BuildingRegistration.RemoveUnusedRetailPrices, BuildingHelper, BusinessHelper.
// This probe separates them: it reports the client's provider-dictionary size and a demand
// histogram on a timer, logs every client-side demand recompute as it happens (candidate b),
// and ApplyMarketSnapshot reports matched-vs-missing rows (candidate a — see the [PROBE] Demand
// APPLY line emitted from GameStatePatcher).
using System.Collections.Generic;
using UnityEngine;

namespace BigAmbitionsMP
{
    internal static class MPDemandProbe
    {
        private const float IntervalSeconds = 15f;
        private static float _nextAt;
        private static System.Reflection.FieldInfo? _providersField;

        /// <summary>Size of the native private providers dictionary (0 => every demand
        /// computation on this machine yields 99-100%).</summary>
        internal static int ProvidersCount()
        {
            try
            {
                _providersField ??= HarmonyLib.AccessTools.Field(
                    typeof(Helpers.ProductMarketHelper), "ProvidersPerItemPerNeighborhood");
                if (_providersField?.GetValue(null) is System.Collections.ICollection c) return c.Count;
            }
            catch { }
            return -1;
        }

        public static void Tick()
        {
            // Round-102c: SP included ON PURPOSE. The client's Item.limitDemandToNeighbourhoods is
            // EMPTY while the host's is populated — same install, same asset. The open question is
            // whether that is caused by our JOIN path or is just how the game behaves for that kind
            // of world, and only a single-player sample can separate them. The one-shot
            // ROWSET-INPUTS/GATES dump is what matters here; the periodic line stays MP-only.
            bool mp = MPServer.IsRunning || MPClient.IsConnected;
            if (!mp) { try { if (SaveGameManager.Current != null) LogRowSetInputs("SP"); } catch { } return; }
            try
            {
                float now = Time.unscaledTime;
                if (_nextAt > 0f && now < _nextAt) return;
                _nextAt = now + IntervalSeconds;

                var gi = SaveGameManager.Current;
                if (gi?.productMarketEntries == null) return;

                int entries = 0, rows = 0, at99to100 = 0, below = 0, zeroProviders = 0;
                var samples = new System.Text.StringBuilder();
                int shown = 0;
                foreach (var e in gi.productMarketEntries)
                {
                    if (e == null) continue;
                    entries++;
                    if (e.demandValues == null) continue;
                    foreach (var nd in e.demandValues)
                    {
                        if (nd == null) continue;
                        rows++;
                        if (nd.demand >= 99) at99to100++; else below++;
                        if (nd.providers == 0) zeroProviders++;
                        if (shown < 3 && nd.neighborhood != "ba:neighborhood_global")
                        { samples.Append($" [{e.itemName}@{nd.neighborhood} d={nd.demand} p={nd.providers}]"); shown++; }
                    }
                }
                string role = MPServer.IsRunning ? "HOST" : "CLIENT";
                Plugin.Logger.LogInfo(
                    $"[PROBE] Demand/{role} providersDict={ProvidersCount()} entries={entries} rows={rows} " +
                    $"| demand>=99: {at99to100}  <99: {below}  | rows with providers==0: {zeroProviders} |{samples}");
                LogRowSetInputs(role);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PROBE] Demand: {ex.Message}"); }
        }

        // Round-102b: WHY do host and client disagree on which demand rows may exist?
        // CanNeighborhoodHaveItemDemand is fed by ItemHelper.IsItemSoldInBuildingType, which
        // reads ItemsSoldInBuildingType — a POPULATE-ONCE cache cleared only at process start
        // (ItemHelper.ResetStaticData) and built from BusinessTypeHelper's own populate-once
        // PlayerAvailableBusinessTypes. If either is filled at a different moment on the two
        // machines, the legal row set diverges permanently. This dumps both caches AND calls
        // the decision function directly for the two items that actually diverged.
        private static bool _dumped;
        private static string _lastLimitSig = "";

        /// <summary>Round-102d: the dump below is ONE-SHOT and fires early on a client — so
        /// "empty on the client" could partly be WHEN I sampled. This runs every tick-interval
        /// and logs only when the item's restriction list CHANGES, which answers the question the
        /// one-shot cannot: does the client's list stay empty for the whole session (a real data
        /// gap) or fill in later (an ORDERING gap, fixable by deferring the client's first demand
        /// computation)? Cheap: two array reads, logs only on change.</summary>
        private static void WatchLimitList(string role)
        {
            try
            {
                string sig = "";
                foreach (var item in new[] { "ba:itemname_theaterticket", "ba:itemname_cheeseplatter" })
                {
                    var it = BigAmbitions.Items.ItemsGetter.GetByName(item);
                    var lim = it?.limitDemandToNeighbourhoods;
                    sig += it == null ? "|NULL" : (lim == null || lim.Length == 0 ? "|EMPTY" : "|" + string.Join(",", lim));
                }
                if (sig == _lastLimitSig) return;
                bool first = _lastLimitSig.Length == 0;
                _lastLimitSig = sig;
                Plugin.Logger.LogWarning(
                    $"[PROBE] Demand/{role} LIMIT-LIST {(first ? "initial" : "CHANGED")} (theaterticket,cheeseplatter) = {sig.TrimStart('|').Replace("ba:neighborhood_", "")}");
            }
            catch { }
        }

        private static void LogRowSetInputs(string role)
        {
            WatchLimitList(role);   // runs every pass; the block below stays one-shot
            if (_dumped) return;
            _dumped = true;
            try
            {
                int businessTypes = -1, playerAvailable = -1, soldMapKeys = -1, officeItems = -1;
                bool officeHasTicket = false, officeHasCheese = false;
                try
                {
                    if (HarmonyLib.AccessTools.Field(typeof(Helpers.BusinessTypeHelper), "BusinessTypes")
                            ?.GetValue(null) is System.Collections.ICollection bt) businessTypes = bt.Count;
                    if (HarmonyLib.AccessTools.Field(typeof(Helpers.BusinessTypeHelper), "PlayerAvailableBusinessTypes")
                            ?.GetValue(null) is System.Collections.ICollection pa) playerAvailable = pa.Count;
                    if (HarmonyLib.AccessTools.Field(typeof(ItemHelper), "ItemsSoldInBuildingType")
                            ?.GetValue(null) is Dictionary<string, HashSet<string>> sold)
                    {
                        soldMapKeys = sold.Count;
                        if (sold.TryGetValue("ba:buildingtype_office", out var officeSet) && officeSet != null)
                        {
                            officeItems = officeSet.Count;
                            officeHasTicket = officeSet.Contains("ba:itemname_theaterticket");
                            officeHasCheese = officeSet.Contains("ba:itemname_cheeseplatter");
                        }
                    }
                }
                catch { }

                // The decision itself, for the exact pairs that diverged.
                string verdicts = "";
                try
                {
                    foreach (var item in new[] { "ba:itemname_theaterticket", "ba:itemname_cheeseplatter" })
                        foreach (var nb in new[] { "ba:neighborhood_garmentdistrict", "ba:neighborhood_murrayhill" })
                            verdicts += $" {item.Replace("ba:itemname_", "")}@{nb.Replace("ba:neighborhood_", "")}=" +
                                        Helpers.ProductMarketHelper.CanNeighborhoodHaveItemDemand(nb, item);
                }
                catch (System.Exception ex) { verdicts = " <call failed: " + ex.Message + ">"; }

                // Round-102c: the verdict alone doesn't say WHICH of the three gates decided it.
                // Evaluate each gate separately, exactly as CanNeighborhoodHaveItemDemand does:
                //   gate 1  item resolves? does its limitDemandToNeighbourhoods exclude this nbhd? (=> false)
                //   gate 2  does this neighbourhood have office businesses?                        (=> true)
                //   gate 3  is the item sold in office-type buildings?                             (=> !that)
                // Host and client return DIFFERENT verdicts from identical config, so exactly one
                // of these reads differently per machine — this names it.
                foreach (var item in new[] { "ba:itemname_theaterticket", "ba:itemname_cheeseplatter" })
                {
                    foreach (var nb in new[] { "ba:neighborhood_garmentdistrict", "ba:neighborhood_murrayhill" })
                    {
                        string g1 = "?", g2 = "?", g3 = "?";
                        try
                        {
                            var it = BigAmbitions.Items.ItemsGetter.GetByName(item);
                            if (it == null) g1 = "ITEM-NULL(gate skipped)";
                            else
                            {
                                var lim = it.limitDemandToNeighbourhoods;
                                g1 = (lim == null || lim.Length == 0)
                                     ? "no-limit-list(gate skipped)"
                                     : $"limited-to[{string.Join(",", lim).Replace("ba:neighborhood_", "")}] excludesThis={System.Array.IndexOf(lim, nb) == -1}";
                            }
                        }
                        catch (System.Exception ex) { g1 = "ERR:" + ex.Message; }
                        try { g2 = NeighborhoodHelper.GetData(nb).hasOfficeBusinesses.ToString(); }   // global namespace
                        catch (System.Exception ex) { g2 = "ERR:" + ex.Message; }
                        try { g3 = ItemHelper.IsItemSoldInBuildingType(item, "ba:buildingtype_office").ToString(); }
                        catch (System.Exception ex) { g3 = "ERR:" + ex.Message; }
                        Plugin.Logger.LogWarning(
                            $"[PROBE] Demand/{role} GATES {item.Replace("ba:itemname_", "")}@{nb.Replace("ba:neighborhood_", "")}: " +
                            $"g1(itemLimit)={g1} | g2(hasOfficeBusinesses)={g2} | g3(soldInOffice)={g3}");
                    }
                }

                Plugin.Logger.LogWarning(
                    $"[PROBE] Demand/{role} ROWSET-INPUTS: BusinessTypes={businessTypes} PlayerAvailable={playerAvailable} " +
                    $"ItemsSoldInBuildingType keys={soldMapKeys} office items={officeItems} " +
                    $"office contains theaterticket={officeHasTicket} cheeseplatter={officeHasCheese} " +
                    $"| CanNeighborhoodHaveItemDemand:{verdicts}");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PROBE] Demand ROWSET-INPUTS: {ex.Message}"); }
        }
    }

    /// <summary>Candidate (b): log EVERY demand recompute, per role, with the caller. On a client
    /// each of these overwrites host-synced demand with a providers==0 result.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Helpers.ProductMarketHelper), nameof(Helpers.ProductMarketHelper.UpdateMarketDemands))]
    public static class Probe_UpdateMarketDemands
    {
        static void Prefix()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                string role = MPServer.IsRunning ? "HOST" : "CLIENT";
                Plugin.Logger.LogWarning(
                    $"[PROBE] Demand/{role} RECOMPUTE-ALL (UpdateMarketDemands) providersDict={MPDemandProbe.ProvidersCount()} " +
                    $"— on a client this rewrites every demand value from the LOCAL provider count. Caller:\n{new System.Diagnostics.StackTrace(2, false)}");
            }
            catch { }
        }
    }

    /// <summary>Candidate (b), single-item variant (BuildingRegistration.RemoveUnusedRetailPrices
    /// and friends). Throttled — this one can fire per item in a loop.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Helpers.ProductMarketHelper), nameof(Helpers.ProductMarketHelper.UpdateMarketDemand))]
    public static class Probe_UpdateMarketDemand
    {
        private static float _nextLog;
        private static int _suppressed;
        static void Prefix(string itemName)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (UnityEngine.Time.unscaledTime < _nextLog) { _suppressed++; return; }
                _nextLog = UnityEngine.Time.unscaledTime + 5f;
                string role = MPServer.IsRunning ? "HOST" : "CLIENT";
                Plugin.Logger.LogWarning(
                    $"[PROBE] Demand/{role} RECOMPUTE-ONE '{itemName}' providersDict={MPDemandProbe.ProvidersCount()}" +
                    (_suppressed > 0 ? $" (+{_suppressed} more in the last 5s)" : "") +
                    $"\n{new System.Diagnostics.StackTrace(2, false)}");
                _suppressed = 0;
            }
            catch { }
        }
    }
}
// PROBE-END: Demand
