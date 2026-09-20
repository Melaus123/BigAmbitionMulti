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
    ///  • RECONCILE TICK: desired state = the host-pushed building keys of MY merger group;
    ///    membership/ownership changes and dissolve converge within a second, restoring parked
    ///    rival identities exactly.
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

        // ── Reconcile (MAIN THREAD, 1 Hz from MPCanvasUI.Update) ─────────────
        private static float _nextHostPush;

        public static void Tick()
        {
            if (UnityEngine.Time.unscaledTime < _nextTick) return;
            _nextTick = UnityEngine.Time.unscaledTime + 1f;
            try { MergerAbsence.Tick(); } catch { }   // P3-B: the host's PACED hand-over snapshots, one per tick
            try { CompanyBooks.Tick(); } catch { }    // P4a M0: the MEMBERSHIP EDGE - publish/apply on join, clear on the way out (nothing else fires on formation)
            try { CompanyFeed.Tick(); } catch { }     // P4b: the same edge for the shared transaction feed - a departed owner's rows go from the registry
            try { CampaignMirror.Tick(); } catch { }  // H-MERGERCAMPAIGN-1: and for the mirrored recruitment campaigns
            try { ImportTransfer.Tick(); } catch { }  // H-MERGERIMPORT-1 F2: re-offer a PAID, unacknowledged import line (session-settled edge + once per game hour)
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

            try
            {
                var desired = DesiredKeys();
                if (desired.Count == 0 && _flipped.Count == 0) return;   // inert path
                // DIAG [FlipProbe]: heartbeat whenever reconcile has real work-state — ties every
                // stuck-flip report to whether the tick RAN and what it believed (10s cadence).
                if (UnityEngine.Time.unscaledTime >= _nextProbe)
                {
                    _nextProbe = UnityEngine.Time.unscaledTime + 10f;
                    Plugin.Logger.LogInfo($"[FlipProbe] tick alive: desired={desired.Count} flipped={_flipped.Count} veil={_veilDepth} member={MergerSync.IAmMember}");
                }

                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return;

                int flippedOnThisTick = 0;   // HQ-PARITY-6 P1
                foreach (var reg in regs)
                {
                    if (reg == null) continue;
                    string key;
                    try { key = GameStateReader.AddressKey(reg); } catch { continue; }
                    if (string.IsNullOrEmpty(key)) continue;

                    // CONTAMINATION REPAIR (2026-07-07 field runs 1-2): a save written while a flip
                    // leaked now claims a partner's building as a NATIVE tenancy — the flip's
                    // "genuinely mine" skip then makes it both un-flippable and un-revertable, and
                    // the claim survives dissolve. Heal: RentedByPlayer on a building the host's
                    // operator ledger attributes to ANOTHER player, outside an active flip, is never
                    // legitimate — clear it. (Next tick re-flips it properly if we're merged.)
                    if (reg.RentedByPlayer && !_flipped.ContainsKey(key) && OwnedByAnother(key))
                    {
                        reg.RentedByPlayer = false;
                        RefreshPoi(reg);
                        Plugin.Logger.LogWarning($"[Merger] REPAIR '{key}': cleared leaked tenancy (owned by another player; not an active flip).");
                        continue;
                    }

                    if (desired.Contains(key))
                    {
                        if (_flipped.ContainsKey(key) || reg.RentedByPlayer) continue;   // already flipped / genuinely mine
                        _flipped[key] = reg.businessOwnerRivalId ?? "";
                        reg.businessOwnerRivalId = "";
                        reg.RentedByPlayer = true;
                        RefreshPoi(reg);
                        flippedOnThisTick++;
                        Plugin.Logger.LogInfo($"[Merger] flip ON  '{key}' (company building now shows as own).");
                    }
                    else if (_flipped.TryGetValue(key, out var parkedRival))
                    {
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
        /// MergerState.BuildingKeys). My OWN buildings are excluded — they're natively rented and
        /// must never enter the flip table (the save strip would otherwise strip a real tenancy).</summary>
        private static HashSet<string> DesiredKeys()
        {
            var set = new HashSet<string>();
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return set;
            foreach (var k in MergerSync.MyGroupBuildingKeys)
                if (!string.IsNullOrEmpty(k)) set.Add(k);
            return set;
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

        /// <summary>Scene boundary: the regs died with the scene — clear tracking WITHOUT touching
        /// objects (the fresh scene's regs arrive unflipped; the tick re-applies from state).</summary>
        public static void Reset()
        {
            _flipped.Clear(); _veilDepth = 0; _saveStrip = false;
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
