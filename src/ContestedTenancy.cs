using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>Round-162 — CONTESTED-TENANCY RESOLUTION.  Field case '57 ba:street_fifthavenue'
    /// ("Don't Open Business"): BOTH players' save files natively claim the building
    /// (RentedByPlayer=true in each), so both machines draw owner icons, and the interior sync's
    /// receiver-owns shield keeps the two copies from ever reconciling (one side stands in an empty
    /// room the other side has stocked).  Handoff-era plumbing allowed the double claim; exactly one
    /// exists in the field save, but any pair with handoff history could hold more.
    ///
    /// POLICY (user-set, 2026-07-28): the DEVELOPMENT EVIDENCE decides, minimizing loss —
    ///   • essentially-empty claim vs developed claim → the empty side releases (it loses an empty
    ///     room, then receives the winner's interior once the shield lifts);
    ///   • empty vs empty → the ledger owner wins, else the first claimant (loss-free either way);
    ///   • DEVELOPED vs DEVELOPED → NOT TOUCHED: logged and left contested — an automatic pick
    ///     would destroy one side's real work, worse than the standing anomaly.
    ///
    /// MECHANICS: every machine reports its native claims (address + its copy's development score)
    /// once per session at world-ready; the host seeds its own natives the same way, resolves on
    /// arrival, ledgers the winner, and instructs the loser to release.  The loser RE-VERIFIES
    /// against its own live copy before releasing and refuses if its copy is actually developed —
    /// the host then logs the refusal and the state stays contested (belt and braces: a stale score
    /// can never delete real work).</summary>
    internal static class ContestedTenancy
    {
        /// <summary>Development at/below this is "essentially empty" (a lamp or two at most).</summary>
        private const int EmptyThreshold = 3;
        /// <summary>Development at/above this is unambiguously a built-out interior.</summary>
        private const int DevelopedThreshold = 10;

        /// <summary>Furniture + stocked products in THIS machine's copy of the registration.
        /// Returns -1 = UNKNOWN when the item data is absent - round-163 (user): interior data can be
        /// unmaterialized until an area loads, and an unknown must never read as "empty": the arbiter
        /// defers on unknown and the release path refuses on it.</summary>
        /// <summary>Round-167 (user): every building COMES WITH default fixtures — ceiling lamps, the
        /// hand-truck spawner and its trucks, the delivery spot — and counting them polluted the score
        /// (a default-only copy read 23 and blocked the arbitration that is the ONLY reconciliation
        /// path when the bugged state also blocks manual lease termination).  Defaults and their cargo
        /// are excluded; only player-added development counts.</summary>
        internal static bool IsDefaultFixture(string n)   // round-184: shared with the resurrection guard
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (n.StartsWith("ba:itemname_ceilinglamp", StringComparison.Ordinal)) return true;
            return n == "ba:itemname_handtruckspawner"
                || n == "ba:itemname_handtruck"
                || n == "ba:itemname_deliveryspot";
        }

        internal static int DevelopmentScore(BuildingRegistration reg, out int furniture, out int cargo)
        {
            furniture = 0; cargo = 0;
            try
            {
                if (reg?.itemInstances == null) return -1;   // data not materialized - UNKNOWN, not empty
                foreach (var kv in reg.itemInstances)
                {
                    var ii = kv.Value;
                    if (ii == null) continue;
                    string n = ""; try { n = ii.itemName ?? ""; } catch { }
                    if (IsDefaultFixture(n)) continue;       // ships with the building — not development
                    furniture++;
                    try { cargo += ii.cargoInstances?.Count ?? 0; } catch { }
                }
            }
            catch { return -1; }
            return furniture + cargo;
        }
        internal static int DevelopmentScore(BuildingRegistration reg) => DevelopmentScore(reg, out _, out _);

        /// <summary>Round-173 safety: true only when the copy is KNOWN and essentially empty - unknown
        /// data is never empty.  The gate destructive passes must use before touching a building.</summary>
        internal static bool IsKnownEssentiallyEmpty(BuildingRegistration reg, out int score)
        {
            score = DevelopmentScore(reg);
            return score >= 0 && score <= EmptyThreshold;
        }

        // ── Reporting side (host seeds locally; clients send) ───────────────────────────

        private static bool _reported;
        private static bool _seedLogged;

        /// <summary>Round-238: session state dies with the scene.  Declared in round-162 but never
        /// WIRED to the scene-ready reset (pre-existing defect, found building the orphan heal):
        /// _reported stayed true for the whole process — a client's SECOND world in one launch never
        /// re-reported claims — and _claims/_resolved bled across worlds, where a stale claim could
        /// fake a contest in an unrelated world.  Wired + widened now; the orphan adoption below
        /// depends on fresh per-world claims.</summary>
        internal static void ResetSession()
        {
            _reported = false;
            _seedLogged = false;
            _claims.Clear();
            _resolved.Clear();
            _pendingAdopt.Clear();
            _adoptDeferrals.Clear();
            _nextAdoptAt = 0f;
        }

        /// <summary>Called from the client tick loop; sends each natively-claimed building once per
        /// session, after the world is loaded.  Rent/vacate changes after this are routed and
        /// ledgered live, so a one-shot report is sufficient.</summary>
        private static float _nextReconcileAt;

        internal static void TickClient()
        {
            if (!MPClient.IsClientInWorld && !MPServer.IsRunning) return;
            // Round-179: the settled-gate subsumes the round-170 quiesce wait (IsSettled requires the
            // quiesce lifted + a settle margin).  Contract-safe: _reported is only set after an actual
            // send, so a deferral retries next tick.
            if (!MPWorldReady.AssertSettledFor("contested-tenancy")) return;

            // ── Round-171: OFF-MARKET RECONCILE (both roles, 10s) ─────────────────────────────
            // Field: after a completed arbitration (snapshot delivered and applied), the loser's door
            // STILL offered "rent this building" — BizMan's rent button reads reg.AvailableForRent, and
            // something native keeps re-truing it for a building whose tenant is a session player.
            // Rather than hunt every native flow, assert the invariant on a cadence: a session player's
            // building is never on the rent market.  Each re-assertion logs — the tripwire that will
            // name the re-truing culprit's cadence if it keeps firing.
            if (Time.unscaledTime >= _nextReconcileAt)
            {
                _nextReconcileAt = Time.unscaledTime + 10f;
                try
                {
                    var giR = SaveGameManager.Current;
                    if (giR?.BuildingRegistrations != null)
                    {
                        int fixedN = 0;
                        foreach (var reg in giR.BuildingRegistrations)
                        {
                            try
                            {
                                if (reg == null || !reg.AvailableForRent) continue;
                                if (reg.RentedByPlayer) { reg.AvailableForRent = false; continue; }   // natively mine: never on the market
                                string mk = reg.businessOwnerRivalId?.ToString() ?? "";
                                bool sessionTenant = !string.IsNullOrEmpty(mk) && GameStatePatcher.IsSessionPlayerId(mk);
                                bool ledgerOwned = MPServer.IsRunning
                                    && MPServer.BuildingOwners.TryGetValue(GameStateReader.AddressKey(reg), out var lo)
                                    && !string.IsNullOrEmpty(lo);
                                if (!sessionTenant && !ledgerOwned) continue;
                                reg.AvailableForRent = false;
                                fixedN++;
                                if (fixedN <= 5)
                                    Plugin.Logger.LogWarning($"[Contested] '{GameStateReader.AddressKey(reg)}' was back ON THE RENT MARKET despite tenant '{mk}' — re-asserted off-market (tripwire: something native re-trues this).");
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // ── Round-238: orphan-claim adoption pass (host only, 15s cadence) ────────────────
            if (MPServer.IsRunning && Time.unscaledTime >= _nextAdoptAt)
            {
                _nextAdoptAt = Time.unscaledTime + 15f;
                TickOrphanAdoptions();
            }

            if (_reported || !MPClient.IsClientInWorld) return;
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return;
            _reported = true;
            try
            {
                int sent = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    // Round-173 safety: TrulyMine, not raw RentedByPlayer - a MERGER-FLIPPED partner
                    // shop reads rented=true for menus, and claiming it would start a false contest
                    // against the partner's real claim (the test pair has no merger; released pairs do).
                    bool mine = false; try { mine = reg != null && MergerFlip.TrulyMine(reg); } catch { }
                    if (!mine) continue;
                    string addr = ""; try { addr = GameStateReader.AddressKey(reg); } catch { }
                    if (string.IsNullOrEmpty(addr)) continue;
                    string bn = ""; try { bn = reg.BusinessName?.ToString() ?? ""; } catch { }
                    // Round-238: carry the tenancy's money figures — an orphan-claim adoption
                    // broadcasts a normal RentConfirm and needs them for a well-formed payload.
                    float rent = 0f, dep = 0f;
                    try { rent = reg.RentPerDay; dep = reg.lastDeposit; } catch { }
                    MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.NativeClaim, MPConfig.PlayerId,
                        new BuildingOwnershipPayload { AddressKey = addr, OwnerPlayerId = MPConfig.PlayerId, Score = DevelopmentScore(reg), BizName = bn, DailyRent = rent, LastDeposit = dep }));
                    sent++;
                }
                if (sent > 0) Plugin.Logger.LogInfo($"[Contested] reported {sent} native claim(s) to the host.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Contested] claim report: {ex.Message}"); }
        }

        // ── Host side: seed + resolve ────────────────────────────────────────────────────

        /// <summary>addr → (claimant pid, that machine's development score, its local business name,
        /// its rent figures — round-238: an adoption's RentConfirm broadcast needs them)</summary>
        private static readonly Dictionary<string, (string pid, int score, string name, float rent, float dep)> _claims = new();
        /// <summary>Round-165: addresses already arbitrated this session (any outcome incl. left-in-place)
        /// — the seed re-enters its claims every health tick and must not re-fight settled contests.
        /// Deferred (unknown-score) contests are NOT added, so they retry once data materializes.</summary>
        private static readonly HashSet<string> _resolved = new();

        /// <summary>Host, world-ready: its own native claims enter the same arbitration table.</summary>
        internal static void HostSeed()
        {
            if (!MPServer.IsRunning) return;
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return;
            try
            {
                // Round-165: the host's natives go THROUGH THE ARRIVAL HANDLER, exactly like a client's
                // report — field run proved ordering matters (the client's claims arrived BEFORE the
                // seed; a table-write seed then either wiped them (164's bug) or deferred to them
                // forever (164's fix's bug).  Via the handler, whichever side registers second triggers
                // the contest — ordering is irrelevant.  Re-runs are cheap: an already-registered own
                // claim just refreshes, and resolved addresses are skipped via _resolved.
                int seeded = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    // Round-173 safety: TrulyMine - same flip hazard as the client report above.
                    bool mine = false; try { mine = reg != null && MergerFlip.TrulyMine(reg); } catch { }
                    if (!mine) continue;
                    string addr = ""; try { addr = GameStateReader.AddressKey(reg); } catch { }
                    if (string.IsNullOrEmpty(addr)) continue;
                    string bn = ""; try { bn = reg.BusinessName?.ToString() ?? ""; } catch { }
                    HostHandleNativeClaim(MPConfig.PlayerId,
                        new BuildingOwnershipPayload { AddressKey = addr, OwnerPlayerId = MPConfig.PlayerId, Score = DevelopmentScore(reg), BizName = bn });
                    seeded++;
                }
                if (seeded > 0 && !_seedLogged)
                {
                    _seedLogged = true;
                    Plugin.Logger.LogInfo($"[Contested] host entered {seeded} native claim(s) into arbitration.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Contested] host seed: {ex.Message}"); }
        }

        /// <summary>Host: a machine reported a native claim.  First claim registers; a second claim
        /// for the same address is a CONTEST and resolves per the policy.
        /// Round-238b: the whole body runs on the MAIN THREAD — network dispatch used to write
        /// _claims/_resolved directly while the seed (and now the adoption pass) read them on the
        /// main thread: the same race class round-50 moved the rent grant/deny decision for.
        /// Single-threading the tables beats locking them.</summary>
        internal static void HostHandleNativeClaim(string senderPid, BuildingOwnershipPayload p)
        {
            if (!MPServer.IsRunning || p == null || string.IsNullOrEmpty(p.AddressKey)) return;
            GameStatePatcher.EnqueueOnMainThread(() => HostHandleNativeClaimBody(senderPid, p));
        }

        private static void HostHandleNativeClaimBody(string senderPid, BuildingOwnershipPayload p)
        {
            try
            {
                string addr = p.AddressKey;
                if (_resolved.Contains(addr)) return;   // settled this session — no re-arbitration, no spam
                // Round-164: receipt visibility (remote senders only — the host's own seed re-enters
                // claims every health tick and would spam).
                if (senderPid != MPConfig.PlayerId)
                    Plugin.Logger.LogInfo($"[Contested] claim received: '{addr}' from '{senderPid}' (score {p.Score}, name='{p.BizName}').");
                if (!_claims.TryGetValue(addr, out var prior) || prior.pid == senderPid)
                {
                    _claims[addr] = (senderPid, p.Score, p.BizName ?? "", p.DailyRent, p.LastDeposit);
                    // Round-238 (field 20260803-022659, second zombie-ledger report): an unopposed
                    // CLIENT claim used to be recorded here and then NOTHING happened — if the host's
                    // ledger had lost the entry, the host kept telling everyone the building was
                    // nobody's, and its own economy could move an AI rival in over the player.
                    // Nominate it for the adoption pass, which re-verifies everything live at
                    // commitment; a counter-claim at any time converts this into a normal contest.
                    if (senderPid != MPConfig.PlayerId)
                        _pendingAdopt.Add(addr);
                    return;                                   // sole claimant so far — nothing to resolve
                }
                // A real contest exists — arbitration owns this address; the orphan heal stands down.
                _pendingAdopt.Remove(addr);
                _adoptDeferrals.Remove(addr);

                int a = prior.score, b = p.Score;
                // FORENSIC LINE (round-163): every fact needed to trace a "my business disappeared"
                // report to this event - both claimants, both scores, both local business names.
                Plugin.Logger.LogWarning($"[Contested] '{addr}' is claimed by BOTH '{prior.pid}' (score {a}, name='{prior.name}') "
                    + $"and '{senderPid}' (score {b}, name='{p.BizName}').");

                if (a < 0 || b < 0)
                {
                    Plugin.Logger.LogWarning($"[Contested] '{addr}' DEFERRED - a claimant's interior data is not materialized "
                        + $"(scores {a}/{b}; -1 = unknown). Unknown must never read as empty; no ruling.");
                    return;
                }
                string winner, loser; int loserScore; string loserName;
                if (a >= DevelopedThreshold && b >= DevelopedThreshold)
                {
                    _resolved.Add(addr);   // settled: the policy says hands off — do not re-fight each tick
                    Plugin.Logger.LogWarning($"[Contested] '{addr}' LEFT IN PLACE - both copies are developed ({a} vs {b}); an automatic pick would destroy one side's work.");
                    return;
                }
                if (b <= EmptyThreshold && a > b) { winner = prior.pid; loser = senderPid; loserScore = b; loserName = p.BizName ?? ""; }
                else if (a <= EmptyThreshold && b > a) { winner = senderPid; loser = prior.pid; loserScore = a; loserName = prior.name; }
                else
                {
                    // Both essentially empty (or equal): the ledger owner wins, else the prior claimant - loss-free.
                    string ledger = MPServer.BuildingOwners.TryGetValue(addr, out var o) ? o : "";
                    winner = ledger == senderPid ? senderPid : prior.pid;
                    loser = winner == senderPid ? prior.pid : senderPid;
                    loserScore = winner == senderPid ? a : b;
                    loserName = winner == senderPid ? prior.name : (p.BizName ?? "");
                }

                _resolved.Add(addr);
                MPServer.BuildingOwners[addr] = winner;
                _claims[addr] = (winner, Math.Max(a, b), winner == senderPid ? (p.BizName ?? "") : prior.name,
                                 winner == senderPid ? p.DailyRent : prior.rent, winner == senderPid ? p.LastDeposit : prior.dep);
                Plugin.Logger.LogWarning($"[Contested] '{addr}' resolved → '{winner}' (development evidence); "
                    + $"'{loser}' releases its claim (score {loserScore}, its local name was '{loserName}'). "
                    + "If a player reports a business missing at this address, THIS event is the culprit to check.");

                // Round-163: a winning HOST cleans the loser's stale tenant mark off its own record
                // ('57 fifthavenue' carried the loser's mark on the winner's copy - attribution noise).
                if (winner == MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        try
                        {
                            var regW = GameStatePatcher.FindRegistration(addr);
                            if (regW != null && regW.RentedByPlayer)
                            {
                                // Round-168: clear ANY foreign mark, not just the arbitration loser's pid —
                                // the field record carried the ORIGINAL owner's field pid ('RED ROC'), which a
                                // loser-pid match missed.  A natively-mine building's tenant is me; any other
                                // mark is stale.
                                string mk = ""; try { mk = regW.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                                if (!string.IsNullOrEmpty(mk) && mk != MPConfig.PlayerId)
                                {
                                    // Round-169: STAMP OUR OWN ID, never empty — the native rival-translation
                                    // stamps own shops with the owner's id, and presentation (sign/branding
                                    // paths read the mark) expects it.
                                    try { regW.businessOwnerRivalId = MPConfig.PlayerId; } catch { }
                                    Plugin.Logger.LogWarning($"[Contested] '{addr}': stale tenant mark ('{mk}') re-stamped to the winner's own id.");
                                }
                                // Round-168: the loser was promised "their interior will sync in" — but interior
                                // broadcasts fire on CHANGE, and winning an arbitration changes nothing on the
                                // winner's side.  Push the winner's state actively so the loser's shell fills
                                // NOW (field: released copy stayed 23-item defaults vs the winner's 754).
                                try { BusinessSync.PushBusinessNow(regW, "contested-tenancy resolved"); } catch { }
                                try { InteriorSync.PushOwnedBuildingNow(addr); } catch { }
                                // Round-169: the owned-push above reaches SUBSCRIBERS only — the loser is
                                // typically nowhere near the building and subscribed to nothing.  Send the
                                // snapshot to them DIRECTLY so their data fills regardless of distance.
                                try { InteriorSync.SendSnapshotToPlayer(addr, loser); } catch { }
                                Plugin.Logger.LogInfo($"[Contested] '{addr}': winner state pushed (business + interior + direct snapshot to '{loser}').");
                            }
                        }
                        catch { }
                    });
                else
                    // A CLIENT won: its own organic owner-push covers the propagation on its normal
                    // cadence; the ownership broadcast above already flipped presentation everywhere.
                    Plugin.Logger.LogInfo($"[Contested] '{addr}': winner is '{winner}' — its owner-push will propagate its interior on its normal cadence.");

                var release = new BuildingOwnershipPayload { AddressKey = addr, OwnerPlayerId = winner };
                if (loser == MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => ExecuteRelease(release));
                else
                    MPServer.SendToPlayer(loser, MessageEnvelope.Create(MessageType.ReleaseClaim, "host", release));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Contested] resolve: {ex.Message}"); }
        }

        // ── Round-238: ORPHAN-CLAIM ADOPTION (the zombie-ledger heal) ────────────────────
        // Field shape (two reports, different groups: 20260802-121103 + 20260803-022659): the
        // host's BuildingOwners ledger loses a client's entry (deleter still unnamed).  From then
        // on every publish tells the world the building is nobody's, and the host's own economy
        // can move an AI rival in over the player's business (022659: a fruit shop landed on the
        // claimant's gym).  The claim pipeline above already carries the evidence — the client
        // reports the building natively-claimed, and NOBODY contests it — so the repair rides it:
        // an unopposed client claim against an empty ledger is adopted through the SAME writes
        // a normal approved rent performs (no new write path).  AI squatters are
        // closed with the game's own routine (user ruling 2026-08-12: AI eviction yes; host's or
        // another player's holdings NEVER — log only).  Inert on healthy saves: every claim there
        // is already ledgered, and the pass no-ops.

        // Round-238b (user challenge, events-over-timers): NO maturity clock. The original 30s
        // wait was elapsed time standing in for "counter-claims have arrived" — exactly the
        // forbidden proxy. Every safety it pretended to give comes from real state instead:
        // the host's own tenancy is read LIVE from the registration at commitment (that IS the
        // host's claim, read at the source), and a counter-claim landing at ANY time — before
        // or after adoption — still contests through arbitration (adoptions are never marked
        // _resolved). The 15s pass below is the allowed recurring poll of authoritative state.
        /// <summary>Bounded patience with a host copy that reads rented but never forms a contest.</summary>
        private const int AdoptMaxDeferrals = 20;
        /// <summary>Work queue: addresses whose sole claim awaits the next adoption pass.</summary>
        private static readonly HashSet<string> _pendingAdopt = new();
        private static readonly Dictionary<string, int> _adoptDeferrals = new();
        private static float _nextAdoptAt;

        private static void TickOrphanAdoptions()
        {
            if (_pendingAdopt.Count == 0) return;
            var work = new List<string>(_pendingAdopt);   // AdoptOrphanClaim removes as it goes
            foreach (var addr in work) AdoptOrphanClaim(addr);
        }

        /// <summary>Host, main thread. Re-verifies EVERYTHING live at commitment — the claim table
        /// only nominates; the ledger and the host's registration decide.</summary>
        private static void AdoptOrphanClaim(string addr)
        {
            try
            {
                if (_resolved.Contains(addr)) { _pendingAdopt.Remove(addr); _adoptDeferrals.Remove(addr); return; }
                if (!_claims.TryGetValue(addr, out var c) || string.IsNullOrEmpty(c.pid) || c.pid == MPConfig.PlayerId)
                { _pendingAdopt.Remove(addr); _adoptDeferrals.Remove(addr); return; }

                // Ledger already speaks → nothing orphaned here.
                if (MPServer.BuildingOwners.TryGetValue(addr, out var cur) && !string.IsNullOrEmpty(cur))
                {
                    if (cur != c.pid)
                        Plugin.Logger.LogWarning($"[LedgerHeal] '{addr}': claim by '{c.pid}' but the ledger credits '{cur}' — hands off (player-vs-player belongs to arbitration, round-238).");
                    _pendingAdopt.Remove(addr); _adoptDeferrals.Remove(addr);
                    return;
                }

                var reg = GameStatePatcher.FindRegistration(addr);
                if (reg == null)
                {
                    Plugin.Logger.LogWarning($"[LedgerHeal] '{addr}': claim by '{c.pid}' but the host has no such registration — dropped.");
                    _pendingAdopt.Remove(addr); _adoptDeferrals.Remove(addr);
                    return;
                }

                // The host's own native tenancy enters arbitration via HostSeed — if its claim
                // hasn't landed yet, WAIT for the contest to form rather than adopt over it.
                if (reg.RentedByPlayer)
                {
                    int n = _adoptDeferrals.TryGetValue(addr, out var d) ? d + 1 : 1;
                    _adoptDeferrals[addr] = n;
                    if (n == 1)
                        Plugin.Logger.LogInfo($"[LedgerHeal] '{addr}': claim by '{c.pid}' deferred — the host's copy reads rented; waiting for the host's own claim to enter arbitration.");
                    if (n >= AdoptMaxDeferrals)
                    {
                        Plugin.Logger.LogWarning($"[LedgerHeal] '{addr}': host copy still reads rented after {n} checks and no contest formed — left alone (log-only; user rule: never touch the host's holding).");
                        _pendingAdopt.Remove(addr); _adoptDeferrals.Remove(addr);
                    }
                    return;
                }

                string mk = ""; try { mk = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                if (!string.IsNullOrEmpty(mk) && mk != c.pid && GameStatePatcher.IsSessionPlayerId(mk))
                {
                    Plugin.Logger.LogWarning($"[LedgerHeal] '{addr}': claim by '{c.pid}' but the host shows session player '{mk}' as tenant — hands off (user rule: another player's holding is never evicted).");
                    _pendingAdopt.Remove(addr); _adoptDeferrals.Remove(addr);
                    return;
                }

                // AI (or ownerless) business debris in the way → close it exactly the way the
                // game's own economy closes AI shops daily.  Rival aggregates are DERIVED —
                // RefreshRivals rebuilds ownedBusinesses from the registrations and auto-clears
                // rival tags on player-rented regs — so nothing is left dangling.
                bool hasBusiness = false; string bizName = "", bizType = "";
                try
                {
                    bizName = reg.BusinessName?.ToString() ?? "";
                    bizType = reg.businessTypeName ?? "";
                    hasBusiness = !string.IsNullOrEmpty(bizName) || (!string.IsNullOrEmpty(bizType) && bizType != "ba:businesstype_empty");
                }
                catch { }
                if ((hasBusiness || !string.IsNullOrEmpty(mk)) && mk != c.pid)
                {
                    if (hasBusiness && !string.IsNullOrEmpty(mk))
                        try
                        {
                            // The same news-feed event the sim posts for an organic closure.
                            SaveGameManager.Current.marketEvents.Add(new Entities.MarketEvent(
                                Entities.MarketEventType.BusinessClosed, SaveGameManager.Current.Day,
                                reg.BusinessName, reg.Address, BigAmbitions.Rivals.RivalsHelper.GetRivalName(mk),
                                reg.businessTypeName, reg.Neighborhood));
                        }
                        catch { }
                    try { reg.ShutDownAIBusiness(); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[LedgerHeal] '{addr}': ShutDownAIBusiness: {ex.Message}"); }
                    Plugin.Logger.LogWarning($"[LedgerHeal] '{addr}': AI business '{bizName}' ({bizType}, rival '{mk}') CLOSED — it was squatting on '{c.pid}'s building (the zombie-ledger landmine detonating; round-238).");
                }

                // ADOPT — the same writes a normal approved rent performs (HandleRentRequest's
                // grant block), so every downstream consumer sees an ordinary ownership event.
                MPServer.BuildingOwners[addr] = c.pid;
                GameStatePatcher.HostReflectPlayerRent(addr, c.pid);
                MPServer.BroadcastRentConfirmExcept(c.pid, addr, c.rent, c.dep);
                MPServer.RefreshBuildingAccess();
                try { BigAmbitions.Rivals.RivalsHelper.RefreshRivals(); } catch { }
                _pendingAdopt.Remove(addr); _adoptDeferrals.Remove(addr);
                // NOT added to _resolved: a later counter-claim must still be able to contest this
                // adoption through the normal arbitration path.
                Plugin.Logger.LogWarning($"[LedgerHeal] '{addr}' ADOPTED for '{c.pid}' (score {c.score}, name='{c.name}'): the ledger had no entry and nobody else claims it — ledger + host tenancy restored, other clients informed (round-238). The claimant's own sync repopulates the business content.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[LedgerHeal] adopt '{addr}': {ex.Message}"); }
        }

        // ── Release (either side) ────────────────────────────────────────────────────────

        /// <summary>Client received a release instruction for a building it claimed.</summary>
        internal static void ClientHandleRelease(BuildingOwnershipPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
            GameStatePatcher.EnqueueOnMainThread(() => ExecuteRelease(p));
        }

        /// <summary>Clear OUR native claim on the building (the arbitration loser).  RE-VERIFIES the
        /// live copy first: a developed local copy refuses the release — a stale or wrong score must
        /// never delete real work.</summary>
        private static void ExecuteRelease(BuildingOwnershipPayload p)
        {
            try
            {
                var reg = GameStatePatcher.FindRegistration(p.AddressKey);
                if (reg == null || !reg.RentedByPlayer) return;
                int score = DevelopmentScore(reg, out int furn, out int cargo);
                if (score < 0)
                {
                    Plugin.Logger.LogWarning($"[Contested] REFUSED release of '{p.AddressKey}' - this copy's interior data is not materialized (unknown ≠ empty); the contested state stands.");
                    return;
                }
                if (score > EmptyThreshold)
                {
                    Plugin.Logger.LogWarning($"[Contested] REFUSED release of '{p.AddressKey}' - this copy is developed ({furn} furniture + {cargo} products); the contested state stands.");
                    return;
                }
                string name = ""; try { name = reg.BusinessName?.ToString() ?? ""; } catch { }
                reg.RentedByPlayer = false;
                reg.AvailableForRent = false;
                try { reg.businessOwnerRivalId = p.OwnerPlayerId ?? ""; } catch { }
                Plugin.Logger.LogWarning($"[Contested] released our claim on '{p.AddressKey}' - our copy held {furn} furniture + {cargo} products (name='{name}'); "
                    + $"owner is '{p.OwnerPlayerId}'; their interior will sync in. If this machine's player reports a business missing here, this release moved it.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Contested] release: {ex.Message}"); }
        }
    }
}
