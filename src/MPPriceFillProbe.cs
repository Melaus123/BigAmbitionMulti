// PROBE-START: PriceFill — round-101 item-3 (AI retail-price LOSS during a session)
// TEST 1 RESULT (2026-07-26) refuted the "lazy fill on old saves" theory:
//   * host at LOAD already has prices for 239/242 eligible AI shops (3 empty);
//   * after an in-game day rollover: unchanged, and the recalc queue filled to 224 then
//     drained to 0 — daily repricing works;
//   * client agrees exactly (same 3 empty), so steady state is HEALTHY.
// But an EARLIER session on the SAME .hsg at the SAME in-game hour, sampled LATE, showed
// ~144 AI shops with prices on the client and NONE on the host. Tables are filled at load
// => the host LOSES them mid-session. Mechanism unknown; drill mislabeling, MPPriceSync
// publishing empties, and mod-side mass clears are all ruled out by inspection.
//
// TEST 2 (this version): catch the MOMENT of loss and its context. Polls every 10s, logs
// only when the empty-price set GROWS (plus first tick and each day rollover), and reports:
//   * how many newly went empty + up to 3 addresses;
//   * whether those regs ALSO lost their cached product list — the key discriminator:
//       prices gone, products intact  => something clears prices specifically;
//       prices AND products gone      => the whole business record was reset/emptied;
//   * context at that moment: day/hour, whether the local player is inside a building,
//     placement/designer mode, and the recalc queue depth.
using UnityEngine;

namespace BigAmbitionsMP
{
    internal static class MPPriceFillProbe
    {
        private const float IntervalSeconds = 10f;
        private static float _nextAt;
        private static int   _lastDay = -1;
        private static readonly System.Collections.Generic.HashSet<string> _emptyPrev =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        public static void Tick()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;

                int day = gi.Day;
                bool dayRolled = _lastDay >= 0 && day != _lastDay;
                bool firstTick = _nextAt == 0f;
                float now = Time.unscaledTime;
                if (!dayRolled && !firstTick && now < _nextAt) return;
                _nextAt = now + IntervalSeconds;
                _lastDay = day;
                // The probe SHIPS (kept as a tripwire "as long as it's low cost"), so it reports
                // its OWN cost: the [Perf] line carries PriceProbe=<per-frame avg>/<worst pass>.
                // One pass = a walk of every registration with two dictionary lookups each.
                // If the worst-pass figure is ever non-trivial, pull the probe.
                long _pp = MPPerf.Begin();

                int aiTotal = 0, notEligible = 0, eligible = 0;
                int pricesEmpty = 0, prodsEmpty = 0, gateClosed = 0, gateOpenNoPrices = 0;
                var emptyNow = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                var newlyEmpty = new System.Text.StringBuilder();
                int newCount = 0, newAlsoLostProds = 0, newKeptProds = 0;

                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    try
                    {
                        if (reg.RentedByPlayer) continue;
                        if (GameStatePatcher.IsAnyPlayerBusiness(reg)) continue;
                        string type = reg.businessTypeName ?? "";
                        if (type.Length == 0 || type == "ba:businesstype_empty") continue;
                        aiTotal++;

                        bool allowCreate = false;
                        try { allowCreate = Helpers.BusinessTypeHelper.GetData(reg).HasTag(BigAmbitions.Tags.TagRef.Businesstag.allowplayercreation); }
                        catch { }
                        if (!allowCreate) { notEligible++; continue; }
                        eligible++;

                        int nPrices = reg.retailPrices?.Count ?? 0;
                        int nProds  = reg.cachedAvailableProducts?.Count ?? 0;
                        if (nPrices == 0) pricesEmpty++;
                        if (nProds  == 0) prodsEmpty++;
                        if (nPrices == 0 && nProds == 0) gateClosed++;
                        else if (nPrices == 0 && nProds > 0) gateOpenNoPrices++;

                        if (nPrices == 0)
                        {
                            string addr = GameStateReader.AddressKey(reg);
                            emptyNow.Add(addr);
                            if (!_emptyPrev.Contains(addr) && !firstTick)
                            {
                                newCount++;
                                if (nProds == 0) newAlsoLostProds++; else newKeptProds++;
                                if (newCount <= 3)
                                    newlyEmpty.Append($" [{addr} '{reg.BusinessName}' type={type} prods={nProds}]");
                            }
                        }
                    }
                    catch { }
                }

                bool grew = newCount > 0;
                _emptyPrev.Clear();
                foreach (var a in emptyNow) _emptyPrev.Add(a);
                MPPerf.End("PriceProbe", _pp);   // the walk above IS the cost; the logging below is trivial
                if (!grew && !firstTick && !dayRolled) return;   // quiet unless something changed

                int queued = -1;
                try { queued = InstanceBehavior<GameManager>.Instance.pendingRetailPriceRecalculations.Count; } catch { }
                string inside = "";
                try { inside = MPRegisterSync.CurrentShopAddress ?? ""; } catch { }
                bool placing = false;
                try { placing = BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode; } catch { }

                string role = MPServer.IsRunning ? "HOST" : "CLIENT";
                string ctx = $"day={day} h{gi.Hour}{(dayRolled ? " (DAY ROLLED)" : "")} inside='{inside}' placing={placing} recalcQueue={queued}";
                Plugin.Logger.LogInfo(
                    $"[PROBE] PriceFill/{role} {ctx}: aiRegs={aiTotal} eligible={eligible} notEligible={notEligible} " +
                    $"| pricesEmpty={pricesEmpty} prodsEmpty={prodsEmpty} | gateClosed={gateClosed} gateOpenNoPrices={gateOpenNoPrices}");
                if (grew)
                    Plugin.Logger.LogWarning(
                        $"[PROBE] PriceFill/{role} LOST PRICES on {newCount} shop(s) since the last pass " +
                        $"(alsoLostProducts={newAlsoLostProds} keptProducts={newKeptProds}) | {ctx} |{newlyEmpty}");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PROBE] PriceFill: {ex.Message}"); }
        }
    }
}
// PROBE-END: PriceFill
