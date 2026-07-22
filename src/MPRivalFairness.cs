using BigAmbitions.Rivals;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Fair rival AI (user ruling 2026-06-12): event generation is HOST-
    /// authoritative, and every retaliation trigger in the game reads ONLY
    /// "the player" (RentedByPlayer) — on the host that's the host alone, so
    /// rivals would activate against, undercut and out-compete the host
    /// exclusively.  These postfixes widen the host's player-selectors to ALL
    /// session players ("one collective threat", user-chosen), with success
    /// data for client businesses bridged from their self-reported stats (the
    /// host's replicas have empty order history).
    ///
    /// Threshold scaling (user: pooled presence trips caps instantly): the
    /// pooled GetPlayerValues result is NORMALIZED by session player count —
    /// income / N and business count ceil(/N) — which is mathematically the
    /// same as multiplying every timeline threshold by N, with one patch.
    ///
    /// Employee poaching is OFF ENTIRELY in MP (Patch_NoPoachingInMP below —
    /// user ruling 2026-06-12): it could only ever hit the host, and a
    /// host-only penalty is worse than no mechanic.  Independent of the
    /// difficulty presets/rivalsDifficultyMultiplier — no setting re-enables
    /// it.  All other patches host-only (clients suppress the sim).
    /// </summary>
    public static class MPRivalFairness
    {
        private static int SessionPlayerCount()
        {
            try { return System.Math.Max(1, MPRestSync.AllPlayers().Count); }
            catch { return 1; }
        }

        /// <summary>Session-player registrations in a neighborhood (the host's
        /// replicas — businessOwnerRivalId carries the player id).</summary>
        private static List<BuildingRegistration> SessionRegsIn(string neighborhood)
        {
            var outList = new List<BuildingRegistration>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return outList;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null || reg.RentedByPlayer) continue;   // host's own are already native
                    try
                    {
                        if (reg.Neighborhood != neighborhood) continue;
                        if (!GameStatePatcher.IsAnyPlayerBusiness(reg)) continue;
                        outList.Add(reg);
                    }
                    catch { }
                }
            }
            catch { }
            return outList;
        }

        // ── Timeline activation: pooled presence, per-player-scaled ─────────
        [HarmonyPatch(typeof(RivalTimeline), "GetPlayerValues")]
        public static class Patch_RivalSeesAllPlayers
        {
            static void Postfix(SpecialRival rival, ref (List<BuildingRegistration>, float) __result)
            {
                if (!MPServer.IsRunning) return;
                try
                {
                    var list   = __result.Item1 ?? new List<BuildingRegistration>();
                    float income = __result.Item2;
                    foreach (var reg in SessionRegsIn(rival.primaryNeighborhood))
                    {
                        // "Succeeding" bridge: replicas have no order history —
                        // the client's self-reported per-business weekly income
                        // (rivals-leaderboard channel) stands in for it.
                        float wk = MPServer.SessionBusinessWeeklyIncome(GameStateReader.AddressKey(reg));
                        if (wk <= 0f) continue;
                        list.Add(reg);
                        income += wk;
                    }

                    // Normalize pooled values by player count == scale every
                    // threshold by N (collective threat, SP-paced triggers).
                    int n = SessionPlayerCount();
                    if (n > 1)
                    {
                        income /= n;
                        int keep = (int)System.Math.Ceiling(list.Count / (double)n);
                        if (list.Count > keep) list.RemoveRange(keep, list.Count - keep);
                    }
                    __result = (list, income);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalFair] GetPlayerValues: {ex.Message}"); }
            }
        }

        // ── Price-war targeting: include what session players sell ───────────
        [HarmonyPatch(typeof(RivalDefenseHelper), "GetTopSellingProducts")]
        public static class Patch_PriceWarSeesAllPlayers
        {
            static void Postfix(int topNumber, string neighborhood, ref List<string> __result)
            {
                if (!MPServer.IsRunning) return;
                try
                {
                    if (__result == null) __result = new List<string>();
                    if (__result.Count >= topNumber) return;   // host sales already filled it
                    foreach (var reg in SessionRegsIn(neighborhood))
                    {
                        var prices = reg.retailPrices;
                        if (prices == null) continue;
                        for (int i = 0; i < prices.Count && __result.Count < topNumber; i++)
                        {
                            var rp = prices[i];
                            if (rp == null || string.IsNullOrEmpty(rp.itemName) || __result.Contains(rp.itemName)) continue;
                            try
                            {
                                var item = BigAmbitions.Items.ItemsGetter.GetByName(rp.itemName);
                                if (item == null || (item.type & BigAmbitions.Items.ItemType.RetailProduct) == 0) continue;
                            }
                            catch { continue; }
                            __result.Add(rp.itemName);   // rival only cuts items IT also sells — extra names match nothing
                        }
                        if (__result.Count >= topNumber) break;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalFair] GetTopSellingProducts: {ex.Message}"); }
            }
        }

        // ── Employee poaching: OFF in MP (user ruling 2026-06-12) ────────────
        // The poach mutates the TARGET player's staff, and a client's
        // employees only exist on the client's machine — so this mechanic
        // could only ever hit the HOST: a host-only penalty is worse than no
        // mechanic.  Disabled entirely in MP until/unless a routed-command
        // version ships; the timeline treats it as a failed activation and
        // moves on (the method already returns false for "couldn't act").
        [HarmonyPatch(typeof(RivalDefenseHelper), "ActivateHireEmployees")]
        public static class Patch_NoPoachingInMP
        {
            static bool Prefix(ref bool __result)
            {
                // Round-55 (Westi 2026-07-22): gate on InMpGame, not IsConnected — a DISCONNECT with
                // the world still loaded (reconnect window) dropped IsConnected while the game kept
                // running, the suppression lapsed, and the native poach fired — straight into the
                // lingering synthetic (its removal rides an off-duty message a dead link never
                // delivers). InMpGame is the predicate the time system already holds through that
                // exact window.
                if (!MPServer.IsRunning && !MPClient.IsConnected && !MPClient.InMpGame) return true;
                __result = false;
                Plugin.Logger.LogInfo("[RivalFair] rival employee-poach suppressed (MP fairness — host-only mechanic).");
                return false;
            }
        }

        // ── Competing-store targeting: rank session shops by bridged income ──
        [HarmonyPatch(typeof(RivalDefenseHelper), "GetTopIncomeBusinesses")]
        public static class Patch_CompetitionSeesAllPlayers
        {
            static void Postfix(int topNumber, string neighborhood, ref List<BuildingRegistration> __result)
            {
                if (!MPServer.IsRunning) return;
                try
                {
                    if (__result == null) __result = new List<BuildingRegistration>();
                    var candidates = new List<(BuildingRegistration reg, float wk)>();
                    foreach (var reg in SessionRegsIn(neighborhood))
                    {
                        if (reg.businessTypeName == "ba:businesstype_headquarters") continue;
                        float wk = MPServer.SessionBusinessWeeklyIncome(GameStateReader.AddressKey(reg));
                        if (wk > 0f) candidates.Add((reg, wk));
                    }
                    if (candidates.Count == 0) return;
                    candidates.Sort((a, b) => b.wk.CompareTo(a.wk));
                    foreach (var c in candidates)
                    {
                        if (__result.Count >= topNumber) break;
                        if (!__result.Contains(c.reg)) __result.Add(c.reg);
                    }
                    // NOTE: also self-heals a latent native crash — with only
                    // CLIENT businesses triggering the rivalry, the native list
                    // is empty and the caller indexes [0].
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalFair] GetTopIncomeBusinesses: {ex.Message}"); }
            }
        }
    }
}
