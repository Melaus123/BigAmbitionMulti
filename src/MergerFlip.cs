using System;
using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Merger slice 3 — the OWNERSHIP FLIP (.modding/03-systems/company-merger-map.md §12/§13).
    ///
    /// Native menus decide "mine?" via BuildingRegistration.RentedByPlayer. Every machine already
    /// holds merged partners' registrations locally (as rival-owned session-player businesses), so
    /// the flip sets RentedByPlayer = true on them LOCALLY (parking the rival identity) and every
    /// native list/dialog/CTA — present and future — shows them as the member's own with zero
    /// per-menu work.
    ///
    /// The flag also drives SIMULATION, so three protections make the flip safe:
    ///  • AUTHORITY VEIL: around each audited money/state pass (§13-A), ALL flipped regs revert to
    ///    native truth (flag off, rival id back), so wages/rent/taxes/marketing/summaries run
    ///    exactly as un-merged — each cost/credit fires once, on its real owner's machine. The veil
    ///    is a nesting counter; a Finalizer guarantees re-flip even when the veiled pass throws.
    ///  • SERVE SCOPE: the veil's NARROW sibling for the LIVE serve chain (H-SALEHOLE-1 H2). Every
    ///    owner-gated step of a native serve reads BuildingManager.IsPlayerOwnedBusiness, i.e. the same
    ///    flag; inside each serve step ONLY the reg of the building the local player is in reverts, so a
    ///    member in a partner's shop behaves like a permission helper — the owner-GATED steps take nothing
    ///    from the replica and record nothing locally, and every paid NPC order is forwarded to the owner.
    ///    NOT covered (same as the permission path): the two UNGATED native order records
    ///    (SelfServiceEmployee ~:150, TicketKioskController ~:144) and the player-as-customer paths.
    ///  • SAVE STRIP: the whole flip reverts around PerformLocalSave (same choke point + restore-in-
    ///    finally as the synthetic cashiers, ANTIPATTERNS Class 5) so a save can never claim
    ///    ownership of a partner's business.
    ///  • RECONCILE TICK: desired state = the host-pushed building keys of MY merger group whose
    ///    rental-ledger tenant is NOT me (H-MERGEROWNFLIP-1: the ledger, never the building's own flag,
    ///    decides "mine or a partner's"); membership/ownership changes and dissolve converge within a
    ///    second, restoring parked rival identities exactly.
    ///
    /// INERTNESS: no merger → desired set empty → nothing ever flips; every veil push/pop is a
    /// no-op loop over an empty table.
    /// </summary>
    public static class MergerFlip
    {
        // addressKey → parked rival id (the reg's businessOwnerRivalId before the flip).
        private static readonly Dictionary<string, string> _flipped = new();
        private static int   _veilDepth;
        private static float _nextTick;
        private static float _nextProbe;   // DIAG [FlipProbe] heartbeat throttle
        // H-MERGEROWNFLIP-1 step 4: keys this machine RENTED natively while they sat in the flip table (OnNativeRent).
        // They stay out of the flip and out of REPAIR until the ledger names me, the rent is rolled back (flag false)
        // or AdoptWaitSeconds pass. Value = unscaled time of the rent.
        private static readonly Dictionary<string, float> _adopted = new();
        private const float AdoptWaitSeconds = 60f;
        // H-MERGEROWNFLIP-1 step 6: the save object the flip table was built against (Reset keeps the table for it).
        private static object? _tableSave;
        private static int _stripSkipLogged;   // step 5 tripwire line budget (per table lifetime)
        private const int StripSkipBudget = 20;

        public static int FlippedCount => _flipped.Count;
        public static bool IsFlipped(string addressKey) => !string.IsNullOrEmpty(addressKey) && _flipped.ContainsKey(addressKey);
        /// <summary>The TRUE owner (parked runner pid) of a flipped shop — "" when it is not flipped.</summary>
        public static string ParkedRunner(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return "";
            return _flipped.TryGetValue(addressKey, out var runner) ? (runner ?? "") : "";
        }
        /// <summary>Phase 0 (2026-09-10, fixes merger map §21.1): the owner's heartbeat keeps attributing a partner's shop to the
        /// partner, and the ownership apply used to re-stamp businessOwnerRivalId on a FLIPPED reg - which made
        /// IsForeignPlayerBusiness true again and re-engaged every helper guard (assign dropdowns, the class-6 sweep).
        /// The apply now calls this instead: the stamp is PARKED (so a later flip OFF restores the current truth) and the
        /// live field stays empty. Returns true when the key is flipped (caller must then leave the field empty).</summary>
        public static bool ParkRunnerIfFlipped(string addressKey, string runner)
        {
            if (string.IsNullOrEmpty(addressKey) || !_flipped.ContainsKey(addressKey)) return false;
            _flipped[addressKey] = runner ?? "";
            return true;
        }

        /// <summary>TRUE ownership for the MOD's sync layer: RentedByPlayer minus the flip. Every
        /// publisher scan, authority gate, and receiver guard in OUR code must use this instead of
        /// the raw flag — the flip is PRESENTATION for native menus only; treating a flipped reg as
        /// own in the sync layer makes members broadcast ownership claims over partner shops
        /// (outbound) and reject the true owner's pushes (inbound) — the run-4 inversion class.</summary>
        public static bool TrulyMine(BuildingRegistration reg)
        {
            if (reg == null) return false;
            bool rented; try { rented = reg.RentedByPlayer; } catch { return false; }
            if (!rented) return false;
            if (_flipped.Count == 0) return true;   // inert without a merger
            try { return !_flipped.ContainsKey(GameStateReader.AddressKey(reg)); } catch { return true; }
        }

        /// <summary>"THIS machine books that shop": really mine, OR I am the STAND-IN simulating it for an absent
        /// owner (the reg stays flipped through the veil there, so TrulyMine alone answers false on the one
        /// machine that runs the shop - batch 16 review HIGH-1). Same predicate as SharedShopStaff.CommitsHere.
        /// Use it wherever a guard asks "is this machine the single writer for this shop's stock/sales?".</summary>
        public static bool BooksHere(BuildingRegistration reg)
        {
            if (reg == null) return false;
            if (TrulyMine(reg)) return true;
            try
            {
                if (MergerAbsence.SimulatedCount == 0 || !reg.RentedByPlayer) return false;   // cheap outs before the key string
                return MergerAbsence.SimulatesHere(GameStateReader.AddressKey(reg));
            }
            catch { return false; }
        }

        // ── Reconcile (MAIN THREAD, 1 Hz from MPCanvasUI.Update; also drives the host's AccessSets backstop) ─────────────
        private static float _nextHostPush;

        public static void Tick()
        {
            if (UnityEngine.Time.unscaledTime < _nextTick) return;
            _nextTick = UnityEngine.Time.unscaledTime + 1f;
            // H-MERGEROWNFLIP-1 fold F2: Reset may KEEP the table for the same save object; a table (or waiting list)
            // built against a PREVIOUS world must never reach this one. Clear both WITHOUT touching building records
            // (they belong to the old save). A transiently null Current is left alone (no records to reconcile then).
            try
            {
                if ((_flipped.Count > 0 || _adopted.Count > 0) && SaveGameManager.Current != null && !ReferenceEquals(SaveGameManager.Current, _tableSave))
                {
                    int droppedFlips = _flipped.Count, droppedAdopted = _adopted.Count;
                    _flipped.Clear(); _adopted.Clear(); _tableSave = null; _stripSkipLogged = 0;
                    Plugin.Logger.LogWarning($"[Merger] flip table dropped: a different save is loaded ({droppedFlips} flip(s), {droppedAdopted} adopted key(s); building records untouched).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] flip table save check: {ex.Message}"); }
            // H-MERGEROWNFLIP-1 fold F5: an adopted own rent expires on its OWN 60 s clock, not only when the
            // reconcile loop happens to visit its building (a missing reg would otherwise keep it out of the flip forever).
            try { ExpireAdopted(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] adopted expiry: {ex.Message}"); }
            try { MergerAbsence.Tick(); } catch { }   // P3-B: the host's PACED hand-over snapshots, one per tick
            try { CompanyBooks.Tick(); } catch { }    // P4a M0: the MEMBERSHIP EDGE - publish/apply on join, clear on the way out (nothing else fires on formation)
            try { CompanyFeed.Tick(); } catch { }     // P4b: the same edge for the shared transaction feed - a departed owner's rows go from the registry
            try { CampaignMirror.Tick(); } catch { }  // H-MERGERCAMPAIGN-1: and for the mirrored recruitment campaigns
            try { ImportTransfer.Tick(); } catch { }  // H-MERGERIMPORT-1 F2: re-offer a PAID, unacknowledged import line (session-settled edge + once per game hour)
            try { AccessSets.Tick(); } catch { }      // H-MERGERSTOCK-2: HOST - the access sets' input backstop (rebuilds on ANY input change, merged or not)
            if (_veilDepth > 0)
            {
                // DIAG [FlipProbe] (2026-07-07, host stuck-flip: no 'flip OFF' after dissolve): a
                // wedged veil depth would silently halt reconciliation forever — make it LOUD.
                Plugin.Logger.LogWarning($"[FlipProbe] tick skipped — veil depth {_veilDepth} at tick time (flipped={_flipped.Count}). A depth stuck >0 means an unbalanced VeilPush.");
                return;
            }

            // Host: keep members' flip sets current as company buildings are rented/vacated.
            if (MPServer.IsRunning && UnityEngine.Time.unscaledTime >= _nextHostPush)
            {
                _nextHostPush = UnityEngine.Time.unscaledTime + 10f;
                try { MPServer.RebroadcastMergerState(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] state push: {ex.Message}"); }
            }

            // PROBE-START: P-LEASEPROBE  (log-only; once per loaded world: Harmony patch owners on the lease methods)
            try { LeaseProbe.Tick(); } catch { }
            // PROBE-END: P-LEASEPROBE

            try
            {
                var desired = DesiredKeys();
                if (desired.Count == 0 && _flipped.Count == 0 && _adopted.Count == 0) return;   // inert path
                // DIAG [FlipProbe]: heartbeat whenever reconcile has real work-state — ties every
                // stuck-flip report to whether the tick RAN and what it believed (10s cadence).
                if (UnityEngine.Time.unscaledTime >= _nextProbe)
                {
                    _nextProbe = UnityEngine.Time.unscaledTime + 10f;
                    Plugin.Logger.LogInfo($"[FlipProbe] tick alive: desired={desired.Count} flipped={_flipped.Count} veil={_veilDepth} member={MergerSync.IAmMember}");
                }

                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return;

                // PROBE-START: P-LEASEPROBE  (log-only; once per loaded world, BEFORE its first flip: each company
                // building's flag, business name and ledger tenant)
                try { LeaseProbe.LoadDumpOnce(); } catch { }
                // PROBE-END: P-LEASEPROBE

                int flippedOnThisTick = 0;   // HQ-PARITY-6 P1
                foreach (var reg in regs)
                {
                    if (reg == null) continue;
                    string key;
                    try { key = GameStateReader.AddressKey(reg); } catch { continue; }
                    if (string.IsNullOrEmpty(key)) continue;

                    // H-MERGEROWNFLIP-1 step 4: an adopted own rent leaves the waiting list once the ledger names me
                    // (the ordinary state), the rent was rolled back here (flag false), or the ledger never confirmed it.
                    if (_adopted.Count > 0 && _adopted.TryGetValue(key, out var adoptedAt))
                    {
                        bool rentedNow = false; try { rentedNow = reg.RentedByPlayer; } catch { }
                        if (LedgerSaysMine(key) || !rentedNow)
                            _adopted.Remove(key);
                        else if (UnityEngine.Time.unscaledTime - adoptedAt > AdoptWaitSeconds)
                        {
                            _adopted.Remove(key);
                            TryLedgerOwner(key, out var adOwner);
                            Plugin.Logger.LogWarning($"[Merger] adopted own rent '{key}' was not confirmed by the ledger in {AdoptWaitSeconds:0} s (tenant '{adOwner}') - handed back to the flip/REPAIR.");
                        }
                        else continue;   // still waiting for the ledger: neither REPAIR nor the flip touches it
                    }

                    // CONTAMINATION REPAIR (2026-07-07 field runs 1-2): a save written while a flip
                    // leaked now claims a partner's building as a NATIVE tenancy — the claim survives
                    // dissolve. Heal: RentedByPlayer on a building the host's operator ledger attributes
                    // to ANOTHER player, outside an active flip (and not an own rent still waiting for
                    // the ledger, step 4 above), is never legitimate — clear it. (Next tick re-flips it
                    // properly if we're merged.)
                    if (reg.RentedByPlayer && !_flipped.ContainsKey(key) && OwnedByAnother(key))
                    {
                        reg.RentedByPlayer = false;
                        RefreshPoi(reg);
                        Plugin.Logger.LogWarning($"[Merger] REPAIR '{key}': cleared leaked tenancy (owned by another player; not an active flip).");
                        continue;
                    }

                    if (desired.TryGetValue(key, out var ledgerOwner))
                    {
                        if (_flipped.ContainsKey(key)) continue;   // already flipped
                        // H-MERGEROWNFLIP-1 step 3: the building's flag is NO LONGER the "genuinely mine" test -
                        // DesiredKeys already left out every key whose ledger tenant is me. The flag used to decide
                        // it, so a lease that ended here unreported (or a load after one) flipped a member's OWN
                        // rental into a "partner building" (bundle 20260923-222227). Only a key this machine has NO
                        // ledger answer for (null) keeps the old flag skip. A partner key that already reads rented
                        // outside the table is REPAIR's job above.
                        if (ledgerOwner == null && reg.RentedByPlayer) continue;
                        _flipped[key] = reg.businessOwnerRivalId ?? "";
                        _tableSave = SaveGameManager.Current;
                        reg.businessOwnerRivalId = "";
                        reg.RentedByPlayer = true;
                        RefreshPoi(reg);
                        flippedOnThisTick++;
                        Plugin.Logger.LogInfo($"[Merger] flip ON  '{key}' (company building now shows as own; ledger tenant '{ledgerOwner ?? "?"}').");
                        // PROBE-START: P-LEASEPROBE  (log-only; WARNING when the ledger names this machine's player)
                        try { LeaseProbe.OnFlipOn(key); } catch { }
                        // PROBE-END: P-LEASEPROBE
                    }
                    else if (_flipped.TryGetValue(key, out var parkedRival))
                    {
                        // H-MERGEROWNFLIP-1 fold F1: the key left the desired set BECAUSE the ledger now names me (a
                        // co-member hub sale whose ledger push beat the takeover, or a takeover this machine already
                        // ran) - it is MY building now: ADOPT it (leave the table, keep the flag, clear the partner id)
                        // instead of restoring the seller's identity and un-renting the buyer's own building.
                        bool adoptOff = false;
                        try { adoptOff = reg.RentedByPlayer && LedgerSaysMine(key); } catch { }
                        if (adoptOff)
                        {
                            _flipped.Remove(key);
                            reg.businessOwnerRivalId = "";
                            try { CompanyLists.OnUnflipped(key); } catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] un-flip clear refused: {ex.Message}"); }
                            RefreshPoi(reg);
                            Plugin.Logger.LogInfo($"[Merger] flip OFF -> adopted '{key}' (ledger names me)");
                            continue;
                        }
                        reg.RentedByPlayer = false;
                        reg.businessOwnerRivalId = parkedRival;
                        _flipped.Remove(key);
                        try { CompanyLists.OnUnflipped(key); } catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] un-flip clear refused: {ex.Message}"); }   // wave 4 (V1): a building that stopped being a company building shows no partner agreements
                        RefreshPoi(reg);
                        Plugin.Logger.LogInfo($"[Merger] flip OFF '{key}' (left merger / ownership changed).");
                    }
                }

                // HQ-PARITY-6 P1: THE FLIP IS THE EVENT THAT UN-SKIPS THE DISPLAY COPIES.  The list
                // installer only copies a partner's paperwork onto an address the flip has ALREADY turned
                // on (CompanyLists.Apply: "N address(es) not flipped here"), so the first bundle to arrive
                // after a merge forms lands empty and the screens stay bare until the owner happens to
                // publish again - over a minute in the field. Re-applying the held bundles right here,
                // the moment buildings come on, closes that gap without a timer.
                // FOLD b H1/H2: ReapplyOwnersPendingFlip, NOT ReinstallOwner - it re-runs Apply for the
                // owners whose last install actually skipped addresses, and it deliberately leaves the plan
                // overlay's suspension alone (that belongs to MergerAbsence.UndoLocal: resuming it here would
                // double a stood-in-for partner's rows against the real plans the absence installer put in).
                if (flippedOnThisTick > 0)
                {
                    try { CompanyLists.ReapplyOwnersPendingFlip(flippedOnThisTick + " building(s) flipped this tick"); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] re-install after flip: {ex.Message}"); }
                }

                // Flipped keys whose reg vanished (scene churn) — drop the stale tracking.
                if (_flipped.Count > 0)
                {
                    var live = new HashSet<string>();
                    foreach (var reg in regs)
                    { try { var k = GameStateReader.AddressKey(reg); if (!string.IsNullOrEmpty(k)) live.Add(k); } catch { } }
                    var stale = new List<string>();
                    foreach (var k in _flipped.Keys) if (!live.Contains(k)) stale.Add(k);
                    foreach (var k in stale) _flipped.Remove(k);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] flip tick: {ex.Message}"); }
        }

        /// <summary>Partner-owned building addressKeys of MY merger group (host-pushed via
        /// MergerState.BuildingKeys), each mapped to its ledger TENANT (null = this machine has no ledger
        /// answer). H-MERGEROWNFLIP-1 step 2: my OWN buildings are excluded BY THE LEDGER - keys whose tenant
        /// is me ("host" and my own id both, on the host) - so they never enter the flip table (the save
        /// strip would otherwise strip a real tenancy). Own rents still waiting for the ledger (step 4) are
        /// excluded too.</summary>
        private static Dictionary<string, string?> DesiredKeys()
        {
            var map = new Dictionary<string, string?>();
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return map;
            foreach (var k in MergerSync.MyGroupBuildingKeys)
            {
                if (string.IsNullOrEmpty(k) || map.ContainsKey(k) || _adopted.ContainsKey(k)) continue;
                if (TryLedgerOwner(k, out var owner))
                {
                    if (!string.IsNullOrEmpty(owner) && owner == MPConfig.PlayerId) continue;   // the ledger says MINE
                    map[k] = owner;
                }
                else map[k] = null;
            }
            return map;
        }

        /// <summary>H-MERGEROWNFLIP-1: the rental-ledger TENANT of a key, in PlayerId space, as THIS machine knows
        /// it. Host: its live BuildingOwners ("host" = my id; a reserved offline owner's stable id is returned as
        /// is - never my id). Client: MY company's host-pushed BuildingOwnerPids (an offline owner is ""). False
        /// when there is no answer here (not a company building of mine on a client, not in the host's ledger, or
        /// a payload whose owner list does not line up with its keys).</summary>
        public static bool TryLedgerOwner(string key, out string owner)
        {
            owner = "";
            if (string.IsNullOrEmpty(key)) return false;
            try
            {
                if (MPServer.IsRunning)
                {
                    if (!MPServer.BuildingOwners.TryGetValue(key, out var o) || string.IsNullOrEmpty(o)) return false;
                    owner = o == "host" ? MPConfig.PlayerId : o;
                    return true;
                }
                if (!MPClient.IsClientInWorld) return false;
                var g = MergerSync.MyGroup;
                var keys = g?.BuildingKeys; var owners = g?.BuildingOwnerPids;
                if (keys == null || owners == null || owners.Count != keys.Count) return false;
                int i = keys.IndexOf(key);
                if (i < 0) return false;
                owner = owners[i] ?? "";
                return true;
            }
            catch { return false; }
        }

        /// <summary>H-MERGEROWNFLIP-1: does the rental ledger name THIS machine's player as the tenant? Same
        /// normalisation as OwnedByAnother ("host" and my own id are both me on the host). False when unknown.</summary>
        public static bool LedgerSaysMine(string key)
            => TryLedgerOwner(key, out var o) && !string.IsNullOrEmpty(o) && o == MPConfig.PlayerId;

        /// <summary>H-MERGEROWNFLIP-1 step 9 (HOST): the ledger names ANOTHER player as the tenant - any non-empty
        /// value other than "host" and my own id, which includes an offline member's reserved stable id.</summary>
        public static bool HostLedgerNamesOther(string key, out string owner)
        {
            owner = "";
            try
            {
                if (!MPServer.IsRunning || string.IsNullOrEmpty(key)) return false;
                if (!MPServer.BuildingOwners.TryGetValue(key, out var o) || string.IsNullOrEmpty(o)) return false;
                if (o == "host" || o == MPConfig.PlayerId) return false;
                owner = o;
                return true;
            }
            catch { return false; }
        }

        /// <summary>H-MERGEROWNFLIP-1 fold F5: drop _adopted entries older than AdoptWaitSeconds, whatever the loop
        /// visits. Quiet when the ledger already names me (the ordinary exit), else the same hand-back warning as the loop.</summary>
        private static void ExpireAdopted()
        {
            if (_adopted.Count == 0) return;
            float now = UnityEngine.Time.unscaledTime;
            List<string>? expired = null;
            foreach (var kv in _adopted)
                if (now - kv.Value > AdoptWaitSeconds) (expired ??= new List<string>()).Add(kv.Key);
            if (expired == null) return;
            foreach (var k in expired)
            {
                _adopted.Remove(k);
                if (LedgerSaysMine(k)) continue;
                TryLedgerOwner(k, out var exOwner);
                Plugin.Logger.LogWarning($"[Merger] adopted own rent '{k}' was not confirmed by the ledger in {AdoptWaitSeconds:0} s (tenant '{exOwner}') - handed back to the flip/REPAIR.");
            }
        }

        /// <summary>H-MERGEROWNFLIP-1 (DEV lever read): is this key an own rent still waiting for the ledger?</summary>
        public static bool IsAdopted(string key) => !string.IsNullOrEmpty(key) && _adopted.ContainsKey(key);

        /// <summary>H-MERGEROWNFLIP-1 step 4, the ADOPT event (Patch_RentBuilding postfix and, fold F1, the
        /// BuildingRegistration.AddToPlayer postfix that every takeover path runs - both roles): this machine
        /// has just rented the building natively. If it sat in the flip table, it leaves it NOW - the flag stays
        /// true (the native rent set it), the partner id is cleared rather than parked, and the key waits in
        /// _adopted until the ledger names me, so neither the flip nor REPAIR takes the new lease back. A ledger
        /// push that later makes a still-flipped key mine is the ordinary flip OFF in Tick. MAIN THREAD.</summary>
        public static void OnNativeRent(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || !_flipped.TryGetValue(key, out var parked)) return;
                _flipped.Remove(key);
                _adopted[key] = UnityEngine.Time.unscaledTime;
                var reg = GameStatePatcher.FindRegistration(key);
                if (reg != null)
                {
                    reg.RentedByPlayer = true;
                    reg.businessOwnerRivalId = "";
                    RefreshPoi(reg);
                }
                try { CompanyLists.OnUnflipped(key); } catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] un-flip clear refused: {ex.Message}"); }   // the partner's display copies leave with the flip
                Plugin.Logger.LogWarning($"[Merger] own rent ADOPTED '{key}': it was flipped (partner '{parked}'); it leaves the flip table, stays rented here, the partner id is cleared.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] adopt own rent '{key}': {ex.Message}"); }
        }

        /// <summary>H-MERGEROWNFLIP-1 step 5: the save strip, the veil and the serve scope never revert a key the
        /// ledger calls mine. After steps 2-4 no such key can be in the table - this line is the regression tripwire.</summary>
        private static bool SkipOwnLease(string key, string pass)
        {
            if (!LedgerSaysMine(key)) return false;
            if (_stripSkipLogged < StripSkipBudget)
            {
                _stripSkipLogged++;
                Plugin.Logger.LogWarning($"[Merger] strip skipped own lease {key} ({pass}; the ledger names this player - it should never be in the flip table){(_stripSkipLogged == StripSkipBudget ? " (line budget reached)" : "")}.");
            }
            return true;
        }

        /// <summary>Refresh the city-map POI after a flip transition — the same calls the native
        /// terminate flow makes (map colors are computed from reg state at POI-update time; without
        /// this, merged buildings kept whatever color pre-dated the flip: green-for-renter vs
        /// teal-for-partner, field report 2026-07-07).</summary>
        private static void RefreshPoi(BuildingRegistration reg)
        {
            try
            {
                InstanceBehavior<CityManager>.Instance?.FindCityBuildingController(reg.Address)?.UpdatePoi();
                InstanceBehavior<UI.UIs>.Instance?.mapFilters.ApplyFilters();
            }
            catch { }
        }

        /// <summary>Does the operator ledger attribute this address to ANOTHER player? Host reads
        /// its live authoritative map; clients read the host-pushed OtherOwnedKeys set. Inert in
        /// single-player (both sources empty).</summary>
        private static bool OwnedByAnother(string key)
        {
            if (MPServer.IsRunning)
            {
                if (!MPServer.BuildingOwners.TryGetValue(key, out var owner) || string.IsNullOrEmpty(owner)) return false;
                string ownerPid = owner == "host" ? MPConfig.PlayerId : owner;
                return ownerPid != MPConfig.PlayerId;
            }
            return MPClient.IsClientInWorld && GrantSync.IsOtherOwned(key);
        }

        // ── Authority veil (§13-A passes) + save strip ────────────────────────
        /// <summary>Revert every flipped reg to native truth. Nesting-counted: only the OUTERMOST
        /// push/pop touches the flags (veiled native passes call each other).</summary>
        public static void VeilPush() => Push(saveStrip: false);
        public static void VeilPop()  => Pop();

        /// <summary>P3-B: the SAVE strip's push. Same revert, but the simulated-address exception is
        /// NOT honoured — a .hsg must never claim an absent partner's tenancy, on the simulator least
        /// of all (its copy of that shop is a hand-over, not a purchase).</summary>
        public static void SaveStripPush() => Push(saveStrip: true);
        public static void SaveStripPop()  => Pop();

        private static bool _saveStrip;   // true while the OUTERMOST push is the save strip

        /// <summary>MERGER PHASE 4a: the company-books overlay reads this to stay INERT while a
        /// veiled pass runs (D19-2 - GenerateTaxes and CreateFinancialSummary must see own books
        /// only). The field itself stays private; this is the read-only accessor.</summary>
        public static int VeilDepth => _veilDepth;

        private static void Push(bool saveStrip)
        {
            if (_veilDepth++ > 0) return;
            _saveStrip = saveStrip;
            // PHASE 4a: the overlay is not just "not re-applied" while veiled - it is physically
            // LIFTED (rows removed, folded totals subtracted) for the whole veiled pass and for the
            // whole save, then put back by the Pop below. Without this the veiled tax Step would bill
            // every member for the whole company's sales (design read Q3-b).
            try { CompanyBooks.SuspendPush(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] veil lift refused: {ex.Message} - the overlay may be live during a veiled pass."); }
            // PHASE 4b r3: the shared transaction feed needs NO lift here. Nothing is ever enqueued
            // into gi.Transactions any more - the partner rows are built at the screen layer from a
            // mod-side registry - so CreateFinancialSummary's two passes over that queue
            // (FinancialSummaryHelper :58 and :150) already see this member's own entries only.
            if (_flipped.Count == 0) return;
            // DIAG [FlipProbe]: outermost push with live flips — the save-leak tracer (a save with
            // flips but NO such line before it means a save path bypassed the strip).
            Plugin.Logger.LogInfo($"[FlipProbe] veil ON — {_flipped.Count} flip(s) reverted to native truth{(saveStrip ? " (save strip: simulated addresses included)" : "")}.");
            ApplyAll(flip: false, honourSimulated: !saveStrip);
        }

        private static void Pop()
        {
            if (--_veilDepth > 0) return;
            if (_veilDepth < 0) _veilDepth = 0;   // defensive — unmatched pop must not wedge the veil
            bool wasSaveStrip = _saveStrip; _saveStrip = false;
            if (_flipped.Count > 0)
            {
                Plugin.Logger.LogInfo($"[FlipProbe] veil OFF — {_flipped.Count} flip(s) restored.");
                ApplyAll(flip: true, honourSimulated: !wasSaveStrip);
                // Batch 16 review MEDIUM-3: a veil that went up and down INSIDE a serve scope has just re-flipped
                // the scope's reg - put it back to native truth for the rest of that serve slice (the scope's own
                // pop re-flips it). Unreachable today (no veiled pass is called from a serve body); cheap insurance.
                if (_scopeDepth > 0 && _scopeReg != null) ApplyOne(_scopeReg, _scopeParked, flip: false);
            }
            // PHASE 4a: put the company-books overlay back (AFTER the re-flip, so the re-apply sees
            // the flipped world it was built against).
            try { CompanyBooks.SuspendPop(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] veil restore refused: {ex.Message} - the overlay stays lifted until the next re-apply."); }
        }

        private static void ApplyAll(bool flip, bool honourSimulated)
        {
            try
            {
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return;
                foreach (var reg in regs)
                {
                    if (reg == null) continue;
                    string key;
                    try { key = GameStateReader.AddressKey(reg); } catch { continue; }
                    if (string.IsNullOrEmpty(key) || !_flipped.TryGetValue(key, out var parkedRival)) continue;
                    // P3-B (B3a): an address THIS machine simulates for an absent owner stays flipped
                    // THROUGH the authority veil — it is the only machine running that shop, so its
                    // wage/rent/marketing/summary passes must see it as owned. One machine only (a mark
                    // names a single simulator), and NEVER for the save strip (SaveStripPush: false).
                    if (honourSimulated && MergerAbsence.SimulatesHere(key)) continue;
                    // H-MERGEROWNFLIP-1 step 5: never revert (or re-flip) a lease the ledger calls mine. Only the
                    // revert direction logs, so one veiled pass writes one tripwire line, not two.
                    if (flip ? LedgerSaysMine(key) : SkipOwnLease(key, honourSimulated ? "veil" : "save strip")) continue;
                    ApplyOne(reg, parkedRival, flip);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] veil apply({flip}): {ex.Message}"); }
        }

        /// <summary>THE per-reg revert/re-flip, in ONE place — the authority veil, the save strip and the serve
        /// scope all go through it, so a flipped reg is always restored identically (flag + parked rival id).
        /// Both are PLAIN FIELDS on BuildingRegistration (BuildingRegistration.cs:44 RentedByPlayer, :118
        /// businessOwnerRivalId): no property setter, so the write fires no event, marks nothing dirty and
        /// refreshes no UI on its own.</summary>
        private static void ApplyOne(BuildingRegistration reg, string parkedRival, bool flip)
        {
            reg.RentedByPlayer = flip;
            reg.businessOwnerRivalId = flip ? "" : parkedRival;
        }

        // ── SERVE SCOPE (H-SALEHOLE-1 H2, 2026-09-19) ─────────────────────────
        // WHAT IT FIXES: every owner-gated step of the native serve chain reads
        // BuildingManager.IsPlayerOwnedBusiness, which IS buildingRegistration.RentedByPlayer
        // (BuildingManager.cs:184). On a MEMBER standing in a flipped partner shop that reads TRUE, so that
        // machine drained its own REPLICA's shelves, priced from its own table, charged paper bags and
        // recorded orders the real owner never saw. The permission HELPER gets all of this right for exactly
        // the opposite reason: there the flag is false, so each native step skips itself and
        // Patch_Order_Pay_HelperForward sends the paid order to the owner, who adopts it once.
        // WHY NOT THE FULL VEIL: Push also physically LIFTS the CompanyBooks overlay and reverts EVERY
        // flipped reg — per frame, per serving employee that is heavy and side-effectful.
        // WHAT THIS IS: the narrowest sibling — ONE reg (the building the local player is in, read LIVE at
        // the moment of commitment), no books lift, nesting-counted, and a no-op when nothing is flipped,
        // when we are not in a flipped address, or when this machine is the absent owner's STAND-IN. It is
        // pushed and popped INSIDE each coroutine slice, so it never spans a frame; Unity is single-threaded
        // and nothing runs between the Prefix and the Finalizer but the wrapped method itself, so no other
        // system can observe the reg mid-revert.
        // VEIL INTERACTION: while _veilDepth > 0 the reg is ALREADY native truth — the scope does nothing
        // and remembers that, so its pop restores nothing. If a veil goes up and down inside the scope, the
        // veil's own Pop re-flips everything and then RE-REVERTS this scope's reg (see Pop), so the rest of
        // the serve slice still runs on native truth; the pop below declines to touch the reg while a veil
        // is still up.
        private static int _scopeDepth;
        private static BuildingRegistration? _scopeReg;   // the ONE reg this scope reverted (null = nothing to restore)
        private static string _scopeParked = "";

        /// <summary>Serve-scoped un-flip. Balanced by <see cref="ServeScopePop"/> from a Harmony Finalizer.</summary>
        public static void ServeScopePush()
        {
            if (_scopeDepth++ > 0) return;
            _scopeReg = null;
            try
            {
                if (_flipped.Count == 0) return;   // inert without a merger — one int compare and out
                if (_veilDepth > 0) return;        // already native truth; the veil owns the flags
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null) return;           // not inside a building
                string key = GameStateReader.AddressKey(reg);
                if (string.IsNullOrEmpty(key) || !_flipped.TryGetValue(key, out var parkedRival)) return;   // the local building is not flipped
                // P3-B (B3a), the SAME exception Push honours: an address this machine simulates for an
                // absent owner is the ONLY machine running that shop, so its serve chain must keep booking
                // locally — leave it flipped.
                if (MergerAbsence.SimulatesHere(key)) return;
                if (SkipOwnLease(key, "serve scope")) return;   // H-MERGEROWNFLIP-1 step 5
                _scopeReg = reg; _scopeParked = parkedRival ?? "";
                ApplyOne(reg, _scopeParked, flip: false);
            }
            catch (Exception ex) { _scopeReg = null; Plugin.Logger.LogWarning($"[Merger] serve scope push: {ex.Message}"); }
        }

        /// <summary>Restore the one reg the matching push reverted.</summary>
        public static void ServeScopePop()
        {
            if (--_scopeDepth > 0) return;
            if (_scopeDepth < 0) _scopeDepth = 0;   // defensive — an unmatched pop must not wedge the scope
            var reg = _scopeReg; _scopeReg = null;
            if (reg == null) return;
            try
            {
                if (_veilDepth > 0) return;   // a veil is up now: it owns the flags and its own Pop re-flips this reg
                ApplyOne(reg, _scopeParked, flip: true);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] serve scope pop: {ex.Message}"); }
        }

        /// <summary>Scene boundary: clear tracking WITHOUT touching objects - but, H-MERGEROWNFLIP-1 step 6, the
        /// FLIP TABLE only when a DIFFERENT save object is loaded. The game-scene load that follows a load keeps
        /// the same building records (Confirmed, bundle 20260923-222227: six flips made during Loading still read
        /// rented after 'Game scene loaded'); clearing the table there lost every parked partner id and left the
        /// survivors to REPAIR. Kept only with no veil and no serve scope up - either leaves its regs at native
        /// truth, which an empty table matches. The group model is cleared just before this call
        /// (MergerSync.ResetSceneState), so kept flips go OFF (parked ids restored) until the next merger state
        /// re-flips them.</summary>
        public static void Reset()
        {
            bool keep = false;
            try { keep = _flipped.Count > 0 && _veilDepth == 0 && _scopeDepth == 0 && _tableSave != null && ReferenceEquals(SaveGameManager.Current, _tableSave); } catch { }
            if (keep) Plugin.Logger.LogInfo($"[Merger] scene reset KEPT {_flipped.Count} flip(s): the same save's building records are still loaded.");
            else { _flipped.Clear(); _adopted.Clear(); _tableSave = null; _stripSkipLogged = 0; }
            _veilDepth = 0; _saveStrip = false;
            // PHASE 4a: the books overlay clears its TRACKING on the same contract - no RemoveAll, because
            // by the time this runs a DIFFERENT save's records are already loaded (review r2 M2). The
            // active clear on dissolve/unmerge/disconnect is CompanyBooks.Tick's membership edge.
            try { CompanyBooks.Reset(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] tracking clear refused: {ex.Message}"); }
            try { CompanyFeed.Reset(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] tracking clear refused: {ex.Message}"); }
            // WAVE 4 (V1): the display-copy REGISTRY is dropped here. ClearAll does call RemoveInstalled,
            // but that lift is REFERENCE-based (it looks for the exact objects it installed), so on a
            // different save's already-loaded lists it simply finds nothing and removes nothing - safe at a
            // scene boundary. The active lift on un-flip/unmerge/disconnect is OnUnflipped and MPClient's
            // drop hook.
            try { CompanyLists.ClearAll("session/scene reset"); } catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] tracking clear refused: {ex.Message}"); }
        }
    }
}
