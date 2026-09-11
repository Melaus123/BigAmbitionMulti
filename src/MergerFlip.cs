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

        // ── Reconcile (MAIN THREAD, 1 Hz from MPCanvasUI.Update) ─────────────
        private static float _nextHostPush;

        public static void Tick()
        {
            if (UnityEngine.Time.unscaledTime < _nextTick) return;
            _nextTick = UnityEngine.Time.unscaledTime + 1f;
            try { MergerAbsence.Tick(); } catch { }   // P3-B: the host's PACED hand-over snapshots, one per tick
            try { CompanyBooks.Tick(); } catch { }    // P4a M0: the MEMBERSHIP EDGE - publish/apply on join, clear on the way out (nothing else fires on formation)
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
                        Plugin.Logger.LogInfo($"[Merger] flip ON  '{key}' (company building now shows as own).");
                    }
                    else if (_flipped.TryGetValue(key, out var parkedRival))
                    {
                        reg.RentedByPlayer = false;
                        reg.businessOwnerRivalId = parkedRival;
                        _flipped.Remove(key);
                        RefreshPoi(reg);
                        Plugin.Logger.LogInfo($"[Merger] flip OFF '{key}' (left merger / ownership changed).");
                    }
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
                    reg.RentedByPlayer = flip;
                    reg.businessOwnerRivalId = flip ? "" : parkedRival;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] veil apply({flip}): {ex.Message}"); }
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
        }
    }
}
