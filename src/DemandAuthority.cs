using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Sweep item 4 (2026-08-18, user-approved): market DEMAND is host-authoritative.
    /// The host broadcasts market entries on a cadence and clients apply them ("[Demand] host
    /// market applied", add-if-missing since round-102i).  The native recompute entry points
    /// rebuild every demand row from the LOCAL provider dictionary — which on a client is
    /// near-empty (field: providersDict=0/17/127 vs host 395-408; root: AI shops carry empty
    /// cachedAvailableProducts on clients, measured 242/242) — so any client-side trigger
    /// (BizMan → Shut down business → confirm was caught live in bundles 20260806-160214/170650
    /// and 20260808-111258) overwrote host-synced demand wholesale; field symptom "the second
    /// player's demand all reads 99-100%" (20260726-184204).
    ///
    /// The round-102 Demand probe's own decision rule prescribed this exact fix: "a RECOMPUTE
    /// line on the CLIENT after an APPLY line => suppress client recompute."  The field sweep
    /// delivered that evidence; the client half of the probe graduates to these guards (the
    /// probe classes remain HOST-only detectors).  Clients keep whatever the host last applied
    /// (pre-first-apply: their save's values — still better than a providers==0 rewrite);
    /// single-player and host behavior untouched.</summary>
    [HarmonyPatch(typeof(Helpers.ProductMarketHelper), nameof(Helpers.ProductMarketHelper.UpdateMarketDemands))]
    public static class Patch_UpdateMarketDemands_ClientAuthorityGuard
    {
        private static bool _logged;
        static bool Prefix()
        {
            if (!MPClient.IsConnected || MPServer.IsRunning) return true;   // SP + host: native runs
            if (!_logged)
            {
                _logged = true;
                Plugin.Logger.LogInfo("[Demand] client-side RECOMPUTE-ALL suppressed — demand is host-authoritative (sweep item 4; logged once per launch).");
            }
            return false;
        }
    }

    /// <summary>Single-item variant of the same guard (fires per item from
    /// BuildingRegistration.RemoveUnusedRetailPrices and friends).</summary>
    [HarmonyPatch(typeof(Helpers.ProductMarketHelper), nameof(Helpers.ProductMarketHelper.UpdateMarketDemand))]
    public static class Patch_UpdateMarketDemand_ClientAuthorityGuard
    {
        private static bool _logged;
        static bool Prefix()
        {
            if (!MPClient.IsConnected || MPServer.IsRunning) return true;
            if (!_logged)
            {
                _logged = true;
                Plugin.Logger.LogInfo("[Demand] client-side RECOMPUTE-ONE suppressed — demand is host-authoritative (sweep item 4; logged once per launch).");
            }
            return false;
        }
    }
}
