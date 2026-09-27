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
    /// Timeline activation is PER PLAYER since H-RIVALPARITY-1 part A
    /// (2026-09-27): the pooled, divide-by-N GetPlayerValues postfix is gone -
    /// see MPRivalAttention (per-key counts and checks; a merged host adds only
    /// its co-members' qualifying shops).
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

        // ── Timeline activation: per player (H-RIVALPARITY-1 A, 2026-09-27) ───
        // The pooled Patch_RivalSeesAllPlayers (every session player's shops added
        // to the host's count, then divided by N) was removed: the host's own
        // GetPlayerValues is native again, a merged host adds only its co-members'
        // qualifying shops (MPRivalAttention.Patch_MergedHostCoMembers), and every
        // other player's rival attention is counted and checked per key in
        // MPRivalAttention.

        // ── Price-war targeting: the AIMED key's own best sellers ─────────────
        // H-RIVALPARITY-1 part B (2026-09-27, design (c)): the pooled top-up (any
        // session player's price-list items appended) is gone. A war is aimed at ONE
        // key (MPRivalAttention.Target; null = the host key): its items sold in the
        // last 7 days that the rival also sells, retail products only, most sold
        // first - the host's own list is native's untouched (solo = native).
        [HarmonyPatch(typeof(RivalDefenseHelper), "GetTopSellingProducts")]
        public static class Patch_PriceWarSeesAllPlayers
        {
            static void Postfix(int topNumber, string neighborhood, ref List<string> __result)
            {
                if (!MPServer.IsRunning) return;
                try
                {
                    var aimed = MPRivalAttention.TopSellingFor(topNumber, neighborhood);
                    if (aimed != null) __result = aimed;
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

        // ── Competing-store targeting: the AIMED key's own shops ──────────────
        // H-RIVALPARITY-1 part B (2026-09-27, design (c)): a copycat wave copies the
        // most profitable type of the key it is aimed at (MPRivalAttention.Target;
        // null = the host key) - that key's shops only, ranked on one daily scale
        // (the host's own GetAvgDailyIncome(7), a member's self-reported AvgDaily7).
        // Under the R1 tenancy raise native's own list would hold EVERY player's
        // shop, so it is always rebuilt then; the host key outside the raise keeps
        // native's list (solo = native). MPRivalAttention.WaveGate refuses a wave
        // for a key with no shop, so the caller's unchecked [0] never meets empty.
        [HarmonyPatch(typeof(RivalDefenseHelper), "GetTopIncomeBusinesses")]
        public static class Patch_CompetitionSeesAllPlayers
        {
            static void Postfix(int topNumber, string neighborhood, ref List<BuildingRegistration> __result)
            {
                if (!MPServer.IsRunning) return;
                try
                {
                    var aimed = MPRivalAttention.TopIncomeFor(topNumber, neighborhood, Patch_AiPass_TenancyRaise.RaisedIn(neighborhood) > 0);
                    if (aimed != null) __result = aimed;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalFair] GetTopIncomeBusinesses: {ex.Message}"); }
            }

            /// <summary>Host-side owner of a registration, valid even while the tenancy raise makes
            /// RentedByPlayer meaningless: the ownership ledger first (authoritative, and it survives a
            /// disconnect), then the pid stamped into businessOwnerRivalId.  Empty = the host's own (or
            /// a shop not yet stamped).</summary>
            internal static string OwnerPidOf(BuildingRegistration reg)
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
            static bool Prefix(string neighborhood, Enums.Priority aggression, ref bool __result, ref bool __state)
            {
                __state = false;
                try
                {
                    if (!MPServer.IsRunning) return true;
                    // H-RIVALPARITY-1 part B (design (e)): the aimed key's copycat rules (no shop / once per key /
                    // one wave at a time / total cap) decide BEFORE anything is raised - a refusal runs nothing.
                    if (!MPRivalAttention.WaveGate(neighborhood, (int)aggression)) { __result = false; return false; }
                    __state = true;   // set BEFORE the raise: the Finalizer must lower it even if this throws
                    Patch_AiPass_TenancyRaise.Begin("low-demand site guard");
                    int n = Patch_AiPass_TenancyRaise.RaisedIn(neighborhood);
                    // One line per call even at n == 0 — a low-demand wave is rare and its
                    // absence from the log would be the ambiguous case.
                    Plugin.Logger.LogInfo($"[RivalGuard] low-demand wave in '{neighborhood}': {n} player-held building(s) shielded from site selection.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalGuard] low-demand prefix: {ex.Message}"); }
                return true;
            }

            static void Postfix(bool __result)
            {
                try { if (MPServer.IsRunning) MPRivalAttention.AfterWave(__result); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalWave] error recording the wave: {ex.Message}"); }
            }

            static Exception Finalizer(Exception __exception, bool __state)
            {
                try { if (__state) Patch_AiPass_TenancyRaise.End(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalGuard] low-demand finalizer: {ex.Message}"); }
                try { MPRivalAttention.WaveDone(); } catch { }
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
