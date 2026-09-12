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
    /// RIVAL-FAIR-2 (user ruling 2026-09-12) widens two more player-selectors,
    /// both by the H-SWAP-1 call-scoped tenancy raise (Patch_AiPass_TenancyRaise
    /// .Begin/End) rather than by a postfix, because the vanilla code TESTS
    /// RentedByPlayer inside its own loop:
    ///   R1  RivalDefenseHelper.ActivateLowDemand — the SITE list a rival opens
    ///       competing shops on now excludes every player's rented shop, not just
    ///       the host's (Patch_LowDemandSiteGuard).
    ///   R4  CompetitionHelper.TryGetLowestPlayerRetailPrice — the everyday
    ///       undercut scan now sees every player's shelf prices, so the rival
    ///       undercuts the LOWEST player price in the neighbourhood whoever set it
    ///       (Patch_UndercutSeesAllPlayers).  Its precondition — the host's
    ///       replicas of client shops carrying current retail prices — is met by
    ///       MPPriceSync.Apply (src/MPPriceSync.cs:130-170), which writes the
    ///       replica's retailPrices on every machine from the owner's 5 s changed
    ///       scan plus a 30 s re-assert heartbeat (:37-38).
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
                if (!MPServer.IsRunning && !MPClient.IsConnected && !MPClient.InMpGame && !MPClient.OfflineFork) return true;   // H-FORK-1: holds in the offline fork
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
                    // R1 INTERACTION (review r1 MINOR-3): while Patch_AiPass_TenancyRaise is up, every
                    // player-held registration reads RentedByPlayer == true, so native's own filter
                    // (`where x.RentedByPlayer`, RivalDefenseHelper.cs:229-235) already fills topNumber
                    // from ANY player's shop - but ranked by the HOST's replica GetAvgDailyIncome(7),
                    // which for a client shop is empty/stale.  And SessionRegsIn SKIPS RentedByPlayer
                    // regs (:46), which under the raise is all of them, so the top-up below contributes
                    // nothing at all.  Under the raise, rebuild the whole list on one honest scale.
                    if (Patch_AiPass_TenancyRaise.RaisedIn(neighborhood) > 0)
                    {
                        RebuildUnderRaise(topNumber, neighborhood, ref __result);
                        return;
                    }
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

            /// <summary>Under the R1 raise only: one ranked list of the host's OWN rented shops plus
            /// every session shop in the neighbourhood, headquarters excluded, highest first, capped at
            /// topNumber.  Ownership CANNOT be read from RentedByPlayer here - the raise set it true for
            /// everyone - so the owner lookup is the same one CompanyMessages.PlayersWithBusinessIn
            /// uses: the host's ownership ledger MPServer.BuildingOwners keyed by address, falling back
            /// to the player pid the mod stamps into businessOwnerRivalId (GameStatePatcher
            /// .IsAnyPlayerBusiness gates that fallback so an AI rival's stamp can never match).
            /// SCALE: native ranks by GetAvgDailyIncome(7), a DAILY average, while the session bridge
            /// MPServer.SessionBusinessWeeklyIncome reports a WEEKLY figure - so the weekly one is
            /// divided by 7 to meet it.  (The pre-raise top-up never ranked the two against each other,
            /// it only appended session shops after the native ones, so there was no existing
            /// convention to preserve; this is the first place the two scales actually meet.)
            /// A client shop whose owner is unknown AND whose stats have not arrived ranks 0 and sorts
            /// to the bottom - the same place native's stale replica income would have put it.</summary>
            private static void RebuildUnderRaise(int topNumber, string neighborhood, ref List<BuildingRegistration> __result)
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                var ranked = new List<(BuildingRegistration reg, float daily)>();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    try
                    {
                        if (reg.Neighborhood != neighborhood) continue;
                        if (!reg.RentedByPlayer) continue;                                   // AI / unrented: native never lists it either
                        if (reg.businessTypeName == "ba:businesstype_headquarters") continue;
                        string owner = OwnerPidOf(reg);
                        if (owner.Length > 0 && owner != MPConfig.PlayerId)
                            ranked.Add((reg, MPServer.SessionBusinessWeeklyIncome(GameStateReader.AddressKey(reg)) / 7f));
                        else
                            ranked.Add((reg, reg.GetAvgDailyIncome(7)));                     // truly the host's own: native's own measure
                    }
                    catch { }
                }
                ranked.Sort((a, b) => b.daily.CompareTo(a.daily));
                var rebuilt = new List<BuildingRegistration>();
                foreach (var c in ranked)
                {
                    if (rebuilt.Count >= topNumber) break;
                    rebuilt.Add(c.reg);
                }
                // Fold d (re-check r2): never hand native FEWER entries than its own filter produced - the
                // caller indexes [0] unchecked (RivalDefenseHelper.ActivateLowDemand :108). Native's own
                // filter (RentedByPlayer + neighbourhood + not HQ) and ours agree, so this is a guard
                // against a rebuild that finds nothing where native found something, not an expected path.
                if (rebuilt.Count == 0 && __result != null && __result.Count > 0) return;
                __result = rebuilt;
            }

            /// <summary>Host-side owner of a registration, valid even while the tenancy raise makes
            /// RentedByPlayer meaningless: the ownership ledger first (authoritative, and it survives a
            /// disconnect), then the pid stamped into businessOwnerRivalId.  Empty = the host's own (or
            /// a shop not yet stamped).</summary>
            private static string OwnerPidOf(BuildingRegistration reg)
            {
                try { if (MPServer.BuildingOwners.TryGetValue(GameStateReader.AddressKey(reg), out var o) && !string.IsNullOrEmpty(o)) return o; } catch { }
                try { if (GameStatePatcher.IsAnyPlayerBusiness(reg)) return reg.businessOwnerRivalId ?? ""; } catch { }
                return "";
            }
        }

        // ── R1: THE LOW-DEMAND SITE GUARD (RIVAL-FAIR-2, 2026-09-12) ─────────
        // RivalDefenseHelper.ActivateLowDemand (decompile :104-136) picks the
        // premises a rival opens competing shops on.  Its FIRST list (:109-111)
        // takes only AvailableForRent registrations — a rented shop is never one,
        // so that half is already safe.  The TOP-UP at :112-118 is not: it filters
        // `!x.RentedByPlayer`, and on the HOST a CLIENT's rented shop reads
        // RentedByPlayer == false (that flag is the LOCAL player's own tenancy), so
        // a client's shop can be chosen and CompetitionHelper.StartNewCompetitorBusiness
        // (:129 / Helpers/CompetitionHelper.cs:708-722) OVERWRITES the registration
        // in place — businessTypeName, BusinessName, Layout, businessOwnerRivalId.
        // For the duration of ONE call every player-held registration that reads
        // false is raised to true, so the vanilla filter takes its own "skip the
        // player's building" branch for EVERY player, and the Finalizer lowers them
        // again on the normal and the throwing path.
        // NOTE: `bestBusiness` (:108) may legitimately be a CLIENT's shop — the
        // widened selector above (Patch_CompetitionSeesAllPlayers) feeds session
        // businesses into GetTopIncomeBusinesses on purpose, so the rival still
        // reacts to what any player built.  Only the SITE list is shielded.
        [HarmonyPatch(typeof(RivalDefenseHelper), "ActivateLowDemand")]
        public static class Patch_LowDemandSiteGuard
        {
            static void Prefix(string neighborhood, ref bool __state)
            {
                __state = false;
                try
                {
                    if (!MPServer.IsRunning) return;
                    __state = true;   // set BEFORE the raise: the Finalizer must lower it even if this throws
                    Patch_AiPass_TenancyRaise.Begin("low-demand site guard");
                    int n = Patch_AiPass_TenancyRaise.RaisedIn(neighborhood);
                    // One line per call even at n == 0 — a low-demand wave is rare and its
                    // absence from the log would be the ambiguous case.
                    Plugin.Logger.LogInfo($"[RivalGuard] low-demand wave in '{neighborhood}': {n} player-held building(s) shielded from site selection.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalGuard] low-demand prefix: {ex.Message}"); }
            }

            static Exception Finalizer(Exception __exception, bool __state)
            {
                try { if (__state) Patch_AiPass_TenancyRaise.End(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalGuard] low-demand finalizer: {ex.Message}"); }
                return __exception;
            }
        }

        // ── R4: EVERYDAY UNDERCUTTING SEES EVERY PLAYER ──────────────────────
        // Helpers/CompetitionHelper.cs:957-975 TryGetLowestPlayerRetailPrice loops
        // the registration table and `continue`s unless RentedByPlayer — on the host
        // that is the host's own shops alone, so a rival only ever undercut the host.
        // The same call-scoped raise makes the test pass for every player's shop in
        // the neighbourhood.  The scan is host-only in effect anyway: the daily
        // competition pass is client-suppressed (Patch_CompetitionHelper_RunDaily_SkipOnClient).
        [HarmonyPatch]
        public static class Patch_UndercutSeesAllPlayers
        {
            private static int _lines;              // at most 3 scan lines per session
            private static int _dayLogged = -1;     // day source: SaveGameManager.Current.Day
            private static int _dayCalls, _dayShops;

            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var found = new List<System.Reflection.MethodBase>();
                try
                {
                    // By NAME + ARG TYPES: the method is private static and takes an out float.
                    var m = HarmonyLib.AccessTools.Method(typeof(Helpers.CompetitionHelper), "TryGetLowestPlayerRetailPrice",
                        new Type[] { typeof(string), typeof(string), typeof(float).MakeByRefType() });
                    if (m != null) found.Add(m);
                    else Plugin.Logger.LogWarning("[RivalGuard] CompetitionHelper.TryGetLowestPlayerRetailPrice(string,string,out float) not found - the undercut scan still sees the host's shops only.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalGuard] undercut target scan: {ex.Message}"); }
                return found;
            }

            static void Prefix(string itemName, string neighborhood, ref bool __state)
            {
                __state = false;
                try
                {
                    if (!MPServer.IsRunning) return;
                    __state = true;
                    Patch_AiPass_TenancyRaise.Begin("undercut scan");
                    int n = Patch_AiPass_TenancyRaise.RaisedIn(neighborhood);
                    int day = -1;
                    try { day = SaveGameManager.Current?.Day ?? -1; } catch { }
                    if (day != _dayLogged)
                    {
                        if (_dayLogged >= 0 && _dayCalls > 0)
                            Plugin.Logger.LogInfo($"[RivalGuard] undercut scans yesterday: {_dayCalls} call(s), {_dayShops} player-held shop(s) in scope.");
                        _dayLogged = day; _dayCalls = 0; _dayShops = 0;
                    }
                    _dayCalls++; _dayShops += n;
                    if (_lines < 3)
                    {
                        _lines++;
                        Plugin.Logger.LogInfo($"[RivalGuard] undercut scan '{itemName}' in '{neighborhood}': {n} player-held shop(s) in scope.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalGuard] undercut prefix: {ex.Message}"); }
            }

            static Exception Finalizer(Exception __exception, bool __state)
            {
                try { if (__state) Patch_AiPass_TenancyRaise.End(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalGuard] undercut finalizer: {ex.Message}"); }
                return __exception;
            }
        }
    }
}
