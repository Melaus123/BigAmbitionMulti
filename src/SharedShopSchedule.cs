using System;
using System.Collections.Generic;
using AI.Customers.CustomerEntries;            // CustomerEntriesHelper (hours drive customer entries)
using BigAmbitions.DayNightCycle;              // DayOfWeekOrdered
using Buildings;                               // BuildingRegistration
using Entities;                                // EmployeeInstance, TodoTaskType
using HarmonyLib;
using Helpers;                                 // EmployeeHelper, BusinessTypeHelper, UpdateSecurityLevel (extension)
using UI.Smartphone.Apps.BizMan.Schedule;      // ScheduleHelper, BizManSchedule, WorkShiftDrag, ScheduleDaySelectionController
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// SHARED-SHOP MANAGEMENT (the Business PERMISSION feature) — slice 1: the schedule write path.
    /// Plan: .modding/03-systems/shared-shop-management-plan.md §2.5/§2.6/§2.8/§2.9.
    ///
    /// THIS IS NOT THE MERGER. The company merger (MergerSync / MergerFlip / MergerEmployeeSync) is a separate
    /// feature with its own messages and its own rules. Since 2026-09-10 (merger phase 0) the SCHEDULE path also serves
    /// merger-flipped shops through IsScheduleManaged/IsMergedShop (the merger's own write-back was retired); every
    /// other shared-shop surface still keys on IsSharedShop, which EXCLUDES flipped shops (ruling 12: the permission
    /// effort touches only other players' businesses reached through a Business grant).
    ///
    /// A SHARED SHOP on this machine = a registration the host lists in the local player's shared-manage set
    /// (GrantSync.IsSharedManage — a DIRECT Business grant from its operator, owner online; merger membership never
    /// counts) that belongs to another player (GameStatePatcher.IsForeignPlayerBusiness) and is not merger-flipped.
    ///
    /// EDITOR (the permitted player's machine): the game's own schedule screen edits the local REPLICA of the shared
    /// shop. A 2 s scan compares each day's signature with the owner-truth baseline and sends the changed days only
    /// WHEN THIS MACHINE CAN NAME THE PLAYER ACTION BEHIND THE DIFFERENCE (H-SCHEDWIPE-1: the Schedule tab is open on
    /// that shop, its auto-fill is running, or one of the three off-tab player mutators just ran here). A difference
    /// with no such intent is the replica changing itself and is never routed; the owner's copy is asked for instead.
    /// Each sent day is stamped with the baseline signature it was edited against (SharedScheduleEdit → host → owner). In-flight
    /// days are held 15 s for the owner's echo; a lost message simply re-sends. Signatures are PER DAY, sorted, and
    /// exclude duty stand-ins (BAMP_DUTY_*) on both sides.
    /// OWNER: per day — base signature == my current day → apply (the only checks: every shift's employee still
    /// works here, every workstation still exists; the editor's own game enforced every schedule rule); otherwise
    /// the owner wins and the day is reported back as rejected. My own duty stand-ins in an applied day survive. The
    /// per-employee bookkeeping the game does through its open screen runs here directly. Then ONE snapshot goes
    /// back to the editor (targeted, never broadcast — P1); everyone else converges on the business heartbeat.
    /// SESSION (§2.8): opening a shared shop's Schedule tab sends "open" with the held signature; the owner replies
    /// with a snapshot (or a ~0.1 KB "unchanged" — P3) and pushes every change to that shop within 2 s while the
    /// tab stays open (the game's OnWorkShiftChanged event forces a next-frame scan); closing the tab sends "close";
    /// a 60 s keepalive / 150 s expiry covers disconnects.
    /// OWNER TRUTH at the editor (snapshot or heartbeat) reconciles per day: rejected → take it; owner's day unchanged
    /// since baseline → keep any local edit; owner's day changed → an echo of our own edit is taken silently, anything
    /// else means the owner wins. Never under a drag (deferred to the next tick). Nothing is put on screen — every
    /// outcome goes to the log only (user ruling 2026-08-21: no in-game messages from this feature).
    /// </summary>
    public static class SharedShopSchedule
    {
        private const float PendingHoldSeconds   = 15f;
        private const float ScanSeconds          = 2f;
        private const float KeepaliveSeconds     = 60f;
        private const float SessionExpirySeconds = 150f;
        private const string Tag = "[SharedShop]";

        private static float _nextScan;
        private static bool  _eventHooked;

        // editor side
        private static readonly Dictionary<string, Dictionary<int, string>>                _baseline = new();   // addr → day → owner-truth sig
        private static readonly Dictionary<string, Dictionary<int, (string sig, float at)>> _inflight = new();   // addr → day → sent sig, when
        private static readonly Dictionary<string, int> _seq = new();                                             // addr → last seq sent — NEVER cleared (monotonic per process under one SeqEpoch)
        private static readonly Dictionary<string, (List<ScheduleDayInfo> days, List<int> force, string why)> _pendingTruth = new();   // held while dragging
        private static readonly HashSet<string> _pendingRedraw = new();   // an open tab to redraw once the player's drag ends (owner side)
        private static readonly int _seqEpoch = new System.Random().Next(1, int.MaxValue);   // per process: a restarted editor's counter starts over and must not be silenced
        private static string _openSessionAddr = "";   // the ONE shared shop whose Schedule tab is open on this machine
        private static float  _lastKeepalive;

        // ── H-SCHEDWIPE-1 (2026-09-18): a day leaves this machine only when a PLAYER changed it here ──────────
        // The scan used to route ANY per-day difference of a replica to the owner. A replica that emptied itself
        // with the tab shut therefore routed EMPTY days; the owner applied them (ValidateRefs only walks the shifts
        // that are PRESENT, so an empty day validates) and the heartbeat carried the loss back — both saves lost
        // the workers. The gate below is the fix; the SELF-CHANGE probe next to it is how we find the writer.
        private static readonly HashSet<string> _touched = new();                        // addr → an off-tab PLAYER mutator ran here; a one-shot latch consumed by the next scan
        private static readonly Dictionary<string, HashSet<int>> _selfChanged = new();   // addr → days that changed with no intent — never sent, waiting for the owner's truth
        private static readonly HashSet<string> _truthAskLogged = new();                 // addr → the "awaiting the owner's truth" line is printed once, and marks an ask we must close
        private static readonly Dictionary<string, Dictionary<int, (List<string> ids, List<WorkShift> list)>> _baseIds = new();   // addr → day → the employee ids the baseline day held + the list object they lived in (probe)
        private static readonly Dictionary<string, Dictionary<int, string>> _ownBase = new();                                     // OWNER probe: addr → day → sig, re-baselined every beat so it reports deltas
        private static readonly Dictionary<string, Dictionary<int, (List<string> ids, List<WorkShift> list)>> _ownIds = new();

        /// <summary>A native full-roster employee pass (RunDaily / RunHourly / complaints) brackets itself here, so a
        /// shift the SIMULATION removes inside one is never mistaken for the player's hand. Set by MPPatches'
        /// StripModEmployeeRecords / RestoreModEmployeeRecords, which already wrap exactly those passes.</summary>
        internal static int    InEmployeePass;
        internal static int    LastStripFrame   = -1;
        internal static int    LastRestoreFrame = -1;
        internal static string LastStripContext = "";

        private const int ProbeBudget = 40;                        // SELF-CHANGE lines per session; both roles share the budget
        private static int _probeLines;
        private static readonly HashSet<string> _probed = new();   // "addr|day|sig" already reported
        // F8: the owner-side probe is EVENT-DRIVEN. The suspected writer only lives inside a native full-roster
        // employee pass, so after the one baseline pass the probe looks again only on the first beat after a
        // strip/restore moved these frame stamps. Nothing walks the registrations on the beats in between.
        private static bool _ownBaseTaken;
        private static int  _probeSeenStrip   = -1;
        private static int  _probeSeenRestore = -1;
        private static bool OwnProbeDue => !_ownBaseTaken || LastStripFrame != _probeSeenStrip || LastRestoreFrame != _probeSeenRestore;

        // owner side
        private static readonly Dictionary<string, Dictionary<string, float>> _sessions      = new();   // addr → editor pid → last heard
        private static readonly Dictionary<string, string>                    _lastPushedSig = new();   // addr → sig the session editors hold
        private static readonly Dictionary<string, (int epoch, int seq)>      _appliedSeq    = new();   // "addr|pid" → last applied

        private static readonly string[] DayNamesFallback = { "", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        /// <summary>A registration this machine may MANAGE through a DIRECT Business grant (host-pushed SharedManageKeys —
        /// merger membership never counts) and does not own. Merger-flipped shops are explicitly NOT shared shops
        /// (they are schedule-managed through IsScheduleManaged since 2026-09-10).</summary>
        public static bool IsSharedShop(BuildingRegistration reg, string addr)
        {
            if (reg == null || string.IsNullOrEmpty(addr)) return false;
            try
            {
                if (!GrantSync.IsSharedManage(addr)) return false;   // the host's direct-grant list is the authority
                if (MergerFlip.IsFlipped(addr)) return false;
                try { if (reg.businessTypeName == "ba:businesstype_headquarters") return false; } catch { }   // HQ menus are never shared (ruling 2026-08-21); the host list excludes them too
                // Another player's shop on this machine is NOT RentedByPlayer here (that flag is the local player's own
                // tenancy; the heartbeat never sets it for other players' shops — field test 2026-08-21). So the only
                // local sanity check is "not unmistakably mine": rented here with no other runner stamped.
                string stamp = ""; try { stamp = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                bool rented = false; try { rented = reg.RentedByPlayer; } catch { }
                if (rented && string.IsNullOrEmpty(stamp)) return false;
                return stamp != MPConfig.PlayerId;
            }
            catch { return false; }
        }

        /// <summary>A merger-flipped shop this machine helps run (phase 0, 2026-09-10). NOT a shared shop: only the
        /// schedule pipeline and auto-fill read merged shops this way — visibility, prices and work tabs stay
        /// permission-only for WRITES except prices (phase 2 wave 1 routes them); READS of prices, stock and work-tab info follow merged shops since wave 2; the remaining write routes are waves 3-4. HEADQUARTERS ARE IN for merged shops (unlike shared shops, ruling 27): the retired merger
        /// write-back covered partner HQ schedules and the merger will manage HQ fully (plan phase 4c); the validated
        /// pipeline carries HQ scheduling unchanged (merger map §22.x). Review 2026-09-10 #4.</summary>
        public static bool IsMergedShop(BuildingRegistration reg, string addr)
        {
            if (reg == null || string.IsNullOrEmpty(addr)) return false;
            try
            {
                if (!MergerFlip.IsFlipped(addr)) return false;
                if (!MergerSync.IAmMember) return false;
                return true;   // HQ included (see the summary above)
            }
            catch { return false; }
        }

        /// <summary>Phase 0, 2026-09-10: the schedule pipeline serves DIRECT-grant shops and MERGED shops; every other
        /// shared-shop surface keeps IsSharedShop.</summary>
        public static bool IsScheduleManaged(BuildingRegistration reg, string addr) => IsSharedShop(reg, addr) || IsMergedShop(reg, addr);

        /// <summary>MAIN THREAD (MPCanvasUI.Update, 2 s). Inert unless this player helps somewhere, hosts an editing
        /// session, is in a merger, has deferred work, or a native employee pass has run since the owner-side probe
        /// last looked (F8).</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            bool helper = GrantSync.SharedManageCount > 0 || MergerFlip.FlippedCount > 0;   // merged shops are schedule-managed too (phase 0, 2026-09-10)
            // S2: the OWNER watches its own shops too (it holds injected partner records named in its own schedule).
            // F8: that costs a beat only for the one baseline pass and then only after a native employee pass has
            // run; once the probe budget is spent this term goes false for good.
            bool probing = _probeLines < ProbeBudget && (MPServer.IsRunning || MPClient.IsClientInWorld) && OwnProbeDue;
            if (!helper && !probing && _sessions.Count == 0 && _pendingTruth.Count == 0 && _pendingRedraw.Count == 0
                && _openSessionAddr.Length == 0 && _baseline.Count == 0) return;
            try
            {
                EnsureEventHooked();
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                ScanEdits(gi, helper);   // also prunes editor state for shops no longer shared (ruling 12)
                TickDeferred(gi);
                TickEditorSession(gi);
                TickOwnerSessions(gi);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} tick: {ex.Message}"); }
        }

        /// <summary>The game's own "a shift changed" event (add/remove/clear — not edit/move/hours, which the scan
        /// catches within 2 s) → scan on the next frame instead of the next tick.</summary>
        private static void EnsureEventHooked()
        {
            if (_eventHooked) return;
            _eventHooked = true;
            try { ScheduleHelper.OnWorkShiftChanged.AddListener(OnNativeShiftChanged); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} could not hook OnWorkShiftChanged ({ex.Message}) — the 2 s scan still covers every edit."); }
        }
        private static void OnNativeShiftChanged() { _nextScan = 0f; }

        // ── Editor: detect local edits, send the changed days ─────────────────

        private static void ScanEdits(GameInstance gi, bool anyShared)
        {
            HashSet<string> seen = null;
            if (anyShared)
            {
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null || reg.scheduleDays == null) continue;
                    string addr; try { addr = GameStateReader.AddressKey(reg); } catch { continue; }
                    if (!IsScheduleManaged(reg, addr)) continue;   // membership decided by IsScheduleManaged alone — the prune below must agree with TryApplyOwnerTruth's gate
                    (seen ??= new()).Add(addr);
                    if (IsAutoFilling(reg))
                    {   // The fill is still writing — its days go as one change-set when it is done. F1: a native fill
                        // OUTLIVES the tab (BizManSchedule.cs:146-149, ScheduleAutoFillerHelper.cs:82-97), so the
                        // intent is latched HERE, while the fill is visibly running; the first scan that meets the
                        // finished fill then finds it and routes the result instead of force-reverting it.
                        _touched.Add(addr);
                        continue;
                    }
                    var sigs = DaySigs(reg);
                    if (!_baseline.TryGetValue(addr, out var baseline)) { _baseline[addr] = sigs; _baseIds[addr] = DayIdsOf(reg); continue; }   // first sight = baseline
                    _inflight.TryGetValue(addr, out var inflight);
                    List<ScheduleDay> changed = null; List<string> bases = null;
                    // H-SCHEDWIPE-1: what makes a difference an EDIT rather than the replica changing itself. A
                    // running fill cannot reach this line (it is skipped above), so the fill's intent arrives the
                    // only way it can — as the latch F1 set while it was running.
                    bool tabOpen = IsScheduleTabOpenFor(reg);
                    bool intent  = tabOpen || _touched.Contains(addr);
                    foreach (var sd in reg.scheduleDays)
                    {
                        if (sd == null) continue;
                        int day = (int)sd.day;
                        sigs.TryGetValue(day, out var sig); sig ??= "";
                        baseline.TryGetValue(day, out var b); b ??= "";
                        if (sig == b) continue;                                                           // matches the owner's truth
                        if (inflight != null && inflight.TryGetValue(day, out var f) && f.sig == sig
                            && Time.unscaledTime - f.at < PendingHoldSeconds) continue;                  // already in flight
                        if (!intent)
                        {   // Nothing the player did here explains this day. Do not send it, and do NOT re-baseline —
                            // the baseline stays the owner's truth, which is what makes the forced take-back below work.
                            ProbeSelfChange(reg, addr, day, sd, tabOpen, filling: false, isOwner: false);   // a fill never reaches this line
                            if (!_selfChanged.TryGetValue(addr, out var self)) _selfChanged[addr] = self = new HashSet<int>();
                            self.Add(day);
                            continue;
                        }
                        // F4: a real edit on a day we were waiting for the owner's copy of cancels that wait — an
                        // owner snapshot in flight must never force-revert the edit the player has just made.
                        ForgetSelfChanged(addr, day);
                        (changed ??= new()).Add(sd); (bases ??= new()).Add(b);
                    }
                    // F2: the latch is consumed HERE — by the one scan that actually evaluated the gate for this
                    // address. The earlier blanket clear also ate latches for shops this scan skipped (auto-filling,
                    // no baseline yet, not managed this beat), which is how an intent could be lost entirely.
                    _touched.Remove(addr);
                    if (_selfChanged.ContainsKey(addr)) AskOwnerForTruth(addr);
                    if (changed == null) continue;
                    _seq.TryGetValue(addr, out var seq); seq++; _seq[addr] = seq;
                    var p = new SharedScheduleEditPayload { PlayerId = MPConfig.PlayerId, AddressKey = addr, Seq = seq, SeqEpoch = _seqEpoch };
                    if (inflight == null) _inflight[addr] = inflight = new();
                    for (int i = 0; i < changed.Count; i++)
                    {
                        int day = (int)changed[i].day;
                        p.Days.Add(SerializeDay(changed[i], stripSynthetic: true));
                        p.BaseSigs.Add(bases[i]);
                        inflight[day] = (sigs.TryGetValue(day, out var s) ? s : "", Time.unscaledTime);
                    }
                    Plugin.Logger.LogInfo($"{Tag} edit detected @ '{addr}' — routing {p.Days.Count} changed day(s) to the owner (seq {seq}).");
                    SendEdit(p);
                }
            }
            // Ruling 12: a shop that stopped being shared (owner offline, grant revoked) goes back to native behaviour
            // at once — drop every trace of the editing state so the heartbeat replaces it verbatim. _seq stays.
            if (_baseline.Count > 0)
            {
                List<string> gone = null;
                foreach (var k in _baseline.Keys) if (seen == null || !seen.Contains(k)) (gone ??= new()).Add(k);
                if (gone != null)
                    foreach (var k in gone)
                    {
                        _baseline.Remove(k); _inflight.Remove(k); _pendingTruth.Remove(k); _pendingRedraw.Remove(k);
                        _baseIds.Remove(k); _selfChanged.Remove(k); _truthAskLogged.Remove(k);
                        Plugin.Logger.LogInfo($"{Tag} '{k}' is no longer shared with this player — schedule editing state dropped, native sync resumes.");
                    }
            }
            ProbeOwnShops(gi);   // reads _touched for MY OWN shops, so it runs before the drop below
            // F2: a latch for an address this scan did not find schedule-managed can never be read by the gate, so it
            // goes with the rest of that shop's state. (My own shops are dropped here too, after the probe has read
            // them — the probe is log-only, so at worst a beat on which it did not look prints one extra line.)
            if (_touched.Count > 0)
            {
                List<string>? staleLatch = null;
                foreach (var k in _touched) if (seen == null || !seen.Contains(k)) (staleLatch ??= new()).Add(k);
                if (staleLatch != null) foreach (var k in staleLatch) _touched.Remove(k);
            }
        }

        /// <summary>F4: this day is no longer waiting for the owner's copy. When the last day of an address clears,
        /// the session the ask opened is closed again, exactly as the arrival path does.</summary>
        private static void ForgetSelfChanged(string addr, int day)
        {
            if (!_selfChanged.TryGetValue(addr, out var self) || !self.Remove(day)) return;
            if (self.Count > 0) return;
            _selfChanged.Remove(addr);
            if (_truthAskLogged.Remove(addr) && _openSessionAddr != addr)
                SendSession(new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "close", AddressKey = addr });
        }

        /// <summary>S2 probe, OWNER side: my OWN shops are watched by the same beat, because the owner holds injected
        /// partner records named in its own schedule and strips them for the same native passes. The owner has no
        /// owner-truth baseline of its own, so the FIRST sight of a shop simply becomes the baseline (the same
        /// first-sight rule the editor side uses) and only later deltas can print; every pass re-baselines, so the log
        /// reports deltas instead of one stuck difference. Log-only, and the whole pass stops for good once the
        /// budget is spent. F8: after the baseline pass this runs ONLY on the first beat following a native employee
        /// strip/restore — the one window the suspected writer lives in — so nothing walks the registrations on the
        /// beats in between.</summary>
        private static void ProbeOwnShops(GameInstance gi)
        {
            try
            {
                if (_probeLines >= ProbeBudget) { if (_ownBase.Count > 0) { _ownBase.Clear(); _ownIds.Clear(); } return; }
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (!OwnProbeDue) return;
                _probeSeenStrip = LastStripFrame; _probeSeenRestore = LastRestoreFrame; _ownBaseTaken = true;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null || reg.scheduleDays == null) continue;
                    if (!MergerFlip.TrulyMine(reg)) continue;
                    string addr; try { addr = GameStateReader.AddressKey(reg); } catch { continue; }
                    if (string.IsNullOrEmpty(addr)) continue;
                    var sigs = DaySigs(reg);
                    if (!_ownBase.TryGetValue(addr, out var prev)) { _ownBase[addr] = sigs; _ownIds[addr] = DayIdsOf(reg); continue; }
                    bool tabOpen = IsScheduleTabOpenFor(reg);
                    bool filling = IsAutoFilling(reg);
                    if (!(tabOpen || filling || _touched.Contains(addr)))
                        foreach (var sd in reg.scheduleDays)
                        {
                            if (sd == null) continue;
                            int day = (int)sd.day;
                            sigs.TryGetValue(day, out var sig); sig ??= "";
                            prev.TryGetValue(day, out var b); b ??= "";
                            if (sig == b) continue;
                            ProbeSelfChange(reg, addr, day, sd, tabOpen, filling, isOwner: true);
                            if (_probeLines >= ProbeBudget) break;
                        }
                    _ownBase[addr] = sigs; _ownIds[addr] = DayIdsOf(reg);
                    if (_probeLines >= ProbeBudget) return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} WARNING ProbeOwnShops (S2 probe): {ex.Message}"); }
        }

        /// <summary>S2 mutator probe (H-SCHEDWIPE-1, log-only, both machines): one line naming everything that could
        /// explain a schedule day changing here with nobody editing it. ProbeBudget lines per session, and each
        /// address+day+signature prints once — the editor side deliberately never re-baselines such a day, so
        /// without that key the same difference would print on every beat.</summary>
        private static void ProbeSelfChange(BuildingRegistration reg, string addr, int day, ScheduleDay sd, bool tabOpen, bool filling, bool isOwner)
        {
            try
            {
                if (_probeLines >= ProbeBudget) return;
                if (!_probed.Add($"{addr}|{day}|{DaySig(sd)}")) return;
                _probeLines++;
                var store = isOwner ? _ownIds : _baseIds;
                List<string>? before = null; List<WorkShift>? wasList = null;   // net48 nullable: both stay null when this day has no baseline record yet
                if (store.TryGetValue(addr, out var perDay) && perDay.TryGetValue(day, out var rec)) { before = rec.ids; wasList = rec.list; }
                var now = IdsOf(sd);
                var dropped = new List<string>();
                if (before != null)
                    foreach (var id in before)
                    {
                        if (now.Contains(id)) continue;
                        bool inj = false, co = false;
                        try { inj = MPRegisterSync.IsInjectedStaff(id); } catch { }
                        try { co  = CompanyPlans.CoMemberCopyHere(id); } catch { }
                        dropped.Add($"{id}[{(inj ? "injected" : "-")}/{(co ? "mergedPartner" : "-")}/{(IsSynthetic(id) ? "synthetic" : "-")}]");
                    }
                int frame = Time.frameCount;
                string rented = "?", stamp = "?", type = "?";
                try { rented = reg.RentedByPlayer.ToString(); } catch { }
                try { stamp  = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                try { type   = reg.businessTypeName ?? ""; } catch { }
                int gday = -1; float ghour = -1f;
                try { var gt = GameStateReader.GetGameTime(); gday = gt.day; ghour = gt.hourOfDay; } catch { }
                Plugin.Logger.LogWarning($"{Tag} SELF-CHANGE {(isOwner ? "owner" : "replica")} '{addr}' {DayName(day)}: shifts {(before != null ? before.Count : -1)}->{now.Count}"
                    + $", dropped [{string.Join(", ", dropped)}]"
                    + $", tabOpen={tabOpen} drag={IsDragging()} autofill={filling}"
                    + $", frame={frame} sinceStrip={(LastStripFrame < 0 ? -1 : frame - LastStripFrame)} sinceRestore={(LastRestoreFrame < 0 ? -1 : frame - LastRestoreFrame)}"
                    + $" stripCtx='{LastStripContext}' inPass={InEmployeePass}, listReplaced={(wasList != null && !ReferenceEquals(wasList, sd.workShifts))}"
                    + $", RentedByPlayer={rented}, ownerStamp='{stamp}', type='{type}', gameDay={gday} hour={ghour:0.0} ({_probeLines}/{ProbeBudget})");
            }
            catch { }
        }

        /// <summary>H-SCHEDWIPE-1 (iii)+(v): a day that changed here with no player behind it is never sent; we ask the
        /// owner for their whole schedule instead — the way a session "open" carrying a signature that is not the
        /// owner's own already makes OwnerOpen reply with a full copy. The sentinel signature can never equal a real
        /// one, so the reply is always the full copy. Retried on every scan beat until the truth lands (an owner that
        /// cannot be reached simply never answers), and said once per address. The session the ask opens is closed
        /// again the moment the truth arrives, unless a real Schedule tab is open on that shop here.</summary>
        private static void AskOwnerForTruth(string addr)
        {
            try
            {
                SendSession(new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "open", AddressKey = addr, Sig = "\u0001self-change-ask" });
                if (_truthAskLogged.Add(addr))
                    Plugin.Logger.LogWarning($"{Tag} '{addr}': a schedule day changed here with no player edit behind it — nothing was routed to the owner; awaiting the owner's truth (asked again on every 2 s scan until it lands).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} WARNING truth request '{addr}': {ex.Message}"); }
        }

        private static List<string> IdsOf(ScheduleDay sd)
        {
            var ids = new List<string>();
            try { if (sd != null && sd.workShifts != null) foreach (var w in sd.workShifts) if (w != null && !string.IsNullOrEmpty(w.employeeId)) ids.Add(w.employeeId); }
            catch { }
            return ids;
        }

        private static Dictionary<int, (List<string> ids, List<WorkShift> list)> DayIdsOf(BuildingRegistration reg)
        {
            var d = new Dictionary<int, (List<string> ids, List<WorkShift> list)>();
            try { if (reg != null && reg.scheduleDays != null) foreach (var sd in reg.scheduleDays) if (sd != null) d[(int)sd.day] = (IdsOf(sd), sd.workShifts); }
            catch { }
            return d;
        }

        private static void BaseIdsDay(string addr, int day, ScheduleDay sd)
        {
            try
            {
                if (!_baseIds.TryGetValue(addr, out var per)) _baseIds[addr] = per = new Dictionary<int, (List<string> ids, List<WorkShift> list)>();
                per[day] = (IdsOf(sd), sd.workShifts);
            }
            catch { }
        }

        private static BuildingRegistration FindReg(string addressKey, GameInstance gi = null)
        {
            try
            {
                gi ??= SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null || string.IsNullOrEmpty(addressKey)) return null;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == addressKey) return r;
            }
            catch { }
            return null;
        }

        // ── Editor: the editing session (§2.8) ────────────────────────────────

        /// <summary>The Schedule tab of a business finished loading here — a shared shop opens (or keeps) the session
        /// with its owner; anything else closes a session left over from another shop. Inert in single-player.</summary>
        [HarmonyPatch(typeof(BizManSchedule), nameof(BizManSchedule.LoadScheduler))]
        public static class Patch_BizManSchedule_LoadScheduler_SharedSession
        {
            static void Postfix()
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                    var reg = ScheduleHelper.Business != null ? ScheduleHelper.Business.buildingRegistration : null;
                    string addr = ""; try { if (reg != null) addr = GameStateReader.AddressKey(reg); } catch { }
                    if (reg != null && IsScheduleManaged(reg, addr)) OpenEditorSession(addr, reg);
                    else if (_openSessionAddr.Length > 0) CloseEditorSession("another shop's schedule was opened");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} LoadScheduler hook: {ex.Message}"); }
            }
        }

        /// <summary>The Schedule tab went away (tab switch, screen closed) — end the session.</summary>
        [HarmonyPatch(typeof(BizManSchedule), "OnDisable")]
        public static class Patch_BizManSchedule_OnDisable_SharedSession
        {
            static void Postfix()
            {
                try { if (_openSessionAddr.Length > 0) CloseEditorSession("schedule tab closed"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} OnDisable hook: {ex.Message}"); }
            }
        }

        private static void OpenEditorSession(string addr, BuildingRegistration reg)
        {
            if (_openSessionAddr == addr) return;
            if (_openSessionAddr.Length > 0) CloseEditorSession("switched to another shared shop");
            _openSessionAddr = addr;
            _lastKeepalive = Time.unscaledTime;
            SendSession(new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "open", AddressKey = addr, Sig = ScheduleSig(reg) });
            Plugin.Logger.LogInfo($"{Tag} opened the schedule of shared shop '{addr}' — asked the owner for a fresh copy; their changes arrive live while it stays open.");
        }

        private static void CloseEditorSession(string why)
        {
            string addr = _openSessionAddr;
            if (addr.Length > 0)
            {   // F3: a resize / move / paste in the last <2 s before the tab goes away raises no OnWorkShiftChanged
                // and nothing scans on close, so those edits would be met by the next scan with the tab already shut
                // and read as the replica changing itself. Latch the intent and pull the next scan into this frame,
                // before the address is forgotten. Event-driven: the close IS the event, there is no delay.
                _touched.Add(addr);
                _nextScan = 0f;
            }
            _openSessionAddr = "";
            if (addr.Length == 0) return;
            SendSession(new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "close", AddressKey = addr });
            Plugin.Logger.LogInfo($"{Tag} closed the editing session for '{addr}' ({why}).");
        }

        /// <summary>Work held back by a drag in progress — once the hand lets go.</summary>
        private static void TickDeferred(GameInstance gi)
        {
            if (IsDragging()) return;
            if (_pendingTruth.Count > 0)
            {   // a fill still running on a pending shop keeps that shop's truth waiting; the rest proceed
                var stillFilling = new List<string>();
                foreach (var k in _pendingTruth.Keys) { var r = FindReg(k, gi); if (r != null && IsAutoFilling(r)) stillFilling.Add(k); }
                if (stillFilling.Count == _pendingTruth.Count && _pendingRedraw.Count == 0) return;
            }
            if (_pendingRedraw.Count > 0)
            {
                var keys = new List<string>(_pendingRedraw);
                _pendingRedraw.Clear();
                foreach (var k in keys) { var r = FindReg(k, gi); if (r != null) RedrawScheduleTab(r); }
            }
            if (_pendingTruth.Count > 0)
            {
                var keys = new List<string>(_pendingTruth.Keys);
                foreach (var k in keys)
                {
                    var pend = _pendingTruth[k];
                    _pendingTruth.Remove(k);
                    var r = FindReg(k, gi);
                    if (r != null) TryApplyOwnerTruth(r, pend.days, k, pend.why + ", after drag", pend.force);
                }
            }
        }

        private static void TickEditorSession(GameInstance gi)
        {
            if (_openSessionAddr.Length == 0) return;
            var reg = FindReg(_openSessionAddr, gi);
            if (reg == null) { CloseEditorSession("shop no longer exists here"); return; }
            if (!IsScheduleTabOpenFor(reg)) { CloseEditorSession("schedule tab no longer open"); return; }
            if (!IsScheduleManaged(reg, _openSessionAddr)) { CloseEditorSession("no longer schedule-managed for this player"); return; }
            if (Time.unscaledTime - _lastKeepalive >= KeepaliveSeconds)
            {
                _lastKeepalive = Time.unscaledTime;
                SendSession(new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "open", AddressKey = _openSessionAddr, Sig = ScheduleSig(reg) });
            }
        }

        // ── Owner: sessions, live push ─────────────────────────────────────────

        /// <summary>A session message addressed to THIS machine (host-relayed, or the host's own). MAIN THREAD.</summary>
        public static void HandleSession(ScheduleSessionPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.PlayerId)) return;
                p.Schedule ??= new List<ScheduleDayInfo>();   // an explicit null on the wire must not become an NRE
                p.RejectedDays ??= new List<int>();
                switch (p.Action)
                {
                    case "open":     OwnerOpen(p);      break;
                    case "close":    OwnerClose(p);     break;
                    case "snapshot": EditorSnapshot(p); break;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} HandleSession({p?.Action}): {ex.Message}"); }
        }

        private static void OwnerOpen(ScheduleSessionPayload p)
        {
            var reg = FindReg(p.AddressKey);
            if (reg == null || !MergerFlip.TrulyMine(reg)) { Plugin.Logger.LogWarning($"{Tag} session open for '{p.AddressKey}' from '{p.PlayerId}' — not my shop, ignored."); return; }
            if (!_sessions.TryGetValue(p.AddressKey, out var pids)) _sessions[p.AddressKey] = pids = new();
            bool isNew = !pids.ContainsKey(p.PlayerId);
            pids[p.PlayerId] = Time.unscaledTime;
            string sig = ScheduleSig(reg);
            var reply = new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "snapshot", AddressKey = p.AddressKey, ToPid = p.PlayerId, Sig = sig };
            bool current = p.Sig == sig;
            if (!current) SerializeSchedule(reg, reply.Schedule);   // P3: a current copy gets the ~0.1 KB "unchanged" reply
            _lastPushedSig[p.AddressKey] = sig;
            if (isNew) Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' opened the schedule of my shop '{p.AddressKey}' — {(current ? "their copy is current" : "sent them a fresh copy")}; my changes go to them live while it stays open.");
            SendSession(reply);
        }

        private static void OwnerClose(ScheduleSessionPayload p)
        {
            if (_sessions.TryGetValue(p.AddressKey, out var pids) && pids.Remove(p.PlayerId))
            {
                if (pids.Count == 0) { _sessions.Remove(p.AddressKey); _lastPushedSig.Remove(p.AddressKey); }
                Plugin.Logger.LogInfo($"{Tag} '{p.PlayerId}' closed the schedule of '{p.AddressKey}'.");
            }
        }

        private static void TickOwnerSessions(GameInstance gi)
        {
            if (_sessions.Count == 0) return;
            float now = Time.unscaledTime;
            List<string> drop = null;
            foreach (var kv in _sessions)
            {
                var pids = kv.Value;
                List<string> expired = null;
                foreach (var e in pids) if (now - e.Value > SessionExpirySeconds) (expired ??= new()).Add(e.Key);
                if (expired != null)
                    foreach (var x in expired) { pids.Remove(x); Plugin.Logger.LogInfo($"{Tag} editor '{x}' went quiet on '{kv.Key}' — session expired."); }
                var reg = pids.Count > 0 ? FindReg(kv.Key, gi) : null;
                if (reg == null || !MergerFlip.TrulyMine(reg)) { (drop ??= new()).Add(kv.Key); continue; }
                string sig = ScheduleSig(reg);
                if (_lastPushedSig.TryGetValue(kv.Key, out var last) && last == sig) continue;
                _lastPushedSig[kv.Key] = sig;
                foreach (var pid in pids.Keys)
                {
                    var snap = new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "snapshot", AddressKey = kv.Key, ToPid = pid, Sig = sig };
                    SerializeSchedule(reg, snap.Schedule);
                    SendSession(snap);
                }
                Plugin.Logger.LogInfo($"{Tag} my schedule of '{kv.Key}' changed — pushed to {pids.Count} editor(s) now, not at the next heartbeat.");
            }
            if (drop != null) foreach (var k in drop) { _sessions.Remove(k); _lastPushedSig.Remove(k); }
        }

        /// <summary>After a routed edit: the echo to the editor (with any rejected days) plus a plain snapshot to every
        /// other editor with that shop open. Targeted sends only (P1).</summary>
        private static void EchoAfterRoutedWrite(BuildingRegistration reg, string addr, string editorPid, List<int> rejected, string reason)
        {
            string sig = ScheduleSig(reg);
            if (_sessions.ContainsKey(addr)) _lastPushedSig[addr] = sig;   // the push cache belongs to open sessions only (no leak for session-less edits)
            var echo = new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "snapshot", AddressKey = addr, ToPid = editorPid, Sig = sig, Reason = reason ?? "" };
            if (rejected != null) echo.RejectedDays.AddRange(rejected);
            SerializeSchedule(reg, echo.Schedule);
            SendSession(echo);
            if (_sessions.TryGetValue(addr, out var pids))
                foreach (var pid in pids.Keys)
                {
                    if (pid == editorPid) continue;
                    var snap = new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "snapshot", AddressKey = addr, ToPid = pid, Sig = sig };
                    SerializeSchedule(reg, snap.Schedule);
                    SendSession(snap);
                }
        }

        // ── Wire ──────────────────────────────────────────────────────────────

        private static void SendEdit(SharedScheduleEditPayload p)
        {
            if (MPServer.IsRunning) MPServer.HostRouteSharedScheduleEdit(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedScheduleEdit, MPConfig.PlayerId, p));
        }

        private static void SendSession(ScheduleSessionPayload p)
        {
            if (MPServer.IsRunning) MPServer.HostRouteScheduleSession(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.ScheduleSession, MPConfig.PlayerId, p));
        }

        /// <summary>Payload sanity for anything carrying schedule days (host AND owner check it): at most 7 days,
        /// Monday=1..Sunday=7 (the game keeps exactly seven, by position), no duplicates, hard ceilings per day.</summary>
        public static bool ScheduleShapeOk(List<ScheduleDayInfo> days, out string why)
        {
            why = "";
            if (days == null) return true;
            if (days.Count > 7) { why = $"{days.Count} days"; return false; }
            var seenDays = new HashSet<int>();
            foreach (var d in days)
            {
                if (d == null) continue;
                if (d.Day < 1 || d.Day > 7) { why = $"day index {d.Day}"; return false; }
                if (!seenDays.Add(d.Day)) { why = $"day {d.Day} listed twice"; return false; }
                if ((d.OpeningHourSlots?.Count ?? 0) > 24) { why = $"{d.OpeningHourSlots.Count} hour slots in one day"; return false; }
                if ((d.WorkShifts?.Count ?? 0) > 200) { why = $"{d.WorkShifts.Count} shifts in one day"; return false; }
                if (d.WorkShifts != null)
                    foreach (var w in d.WorkShifts)
                        if (w != null && (w.StartingHour < 0 || w.StartingHour > 48 || w.EndingHour < 0 || w.EndingHour > 48))
                        { why = $"shift hours {w.StartingHour}-{w.EndingHour}"; return false; }
            }
            return true;
        }

        // ── Owner: routed apply ────────────────────────────────────────────────

        /// <summary>THE OWNER's machine: apply a permitted player's schedule edit. MAIN THREAD.</summary>
        public static void ApplyOnOwner(SharedScheduleEditPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.PlayerId)) return;
                p.Days ??= new List<ScheduleDayInfo>();
                p.BaseSigs ??= new List<string>();
                var reg = FindReg(p.AddressKey);
                if (reg == null) { Plugin.Logger.LogWarning($"{Tag} routed schedule for unknown '{p.AddressKey}' — dropped."); return; }
                // W3-0 r1 (F1): mine, or an absent owner's business this machine simulates (its lifted copy
                // is the live state) — anywhere else the resolver mis-delivered and we say so.
                bool runsHere = MergerFlip.TrulyMine(reg);
                if (!runsHere) { try { runsHere = MergerAbsence.SimulatesHere(p.AddressKey); } catch { } }
                if (!runsHere) { Plugin.Logger.LogWarning($"{Tag} routed schedule for '{p.AddressKey}' — not my shop and not simulated here, dropped."); return; }
                if (!ScheduleShapeOk(p.Days, out var why)) { Plugin.Logger.LogWarning($"{Tag} routed schedule for '{p.AddressKey}' from '{p.PlayerId}' dropped — {why}."); return; }
                string key = p.AddressKey + "|" + p.PlayerId;
                _appliedSeq.TryGetValue(key, out var last);
                if (p.Seq != 0 && last.epoch == p.SeqEpoch && p.Seq <= last.seq)
                { Plugin.Logger.LogInfo($"{Tag} schedule edit seq {p.Seq} from '{p.PlayerId}' @ '{p.AddressKey}' is older than seq {last.seq} already applied — ignored (a delayed duplicate can never undo a newer edit)."); return; }
                if (p.Seq != 0) _appliedSeq[key] = (p.SeqEpoch, p.Seq);   // a new epoch = the editor restarted; its counter starts over
                ApplyRoutedDays(reg, p, out var applied, out var rejected, out var reason);
                Plugin.Logger.LogInfo($"{Tag} routed schedule @ '{p.AddressKey}' by '{p.PlayerId}': applied {applied.Count} day(s){(rejected.Count > 0 ? $", rejected {rejected.Count} ({reason})" : "")}.");
                EchoAfterRoutedWrite(reg, p.AddressKey, p.PlayerId, rejected, reason);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} ApplyOnOwner: {ex.Message}"); }
        }

        /// <summary>Per-day validated apply (§2.6): base signature must equal my current day (else the owner wins); the
        /// owner must not have their hand on that day (drag in progress); every shift must reference an employee still
        /// assigned here and a workstation still in the building. My duty stand-ins in an applied day are preserved.
        /// Then the bookkeeping the game normally does through the open schedule screen.</summary>
        private static void ApplyRoutedDays(BuildingRegistration reg, SharedScheduleEditPayload p, out List<int> applied, out List<int> rejected, out string reason)
        {
            applied = new(); rejected = new();
            var reasons = new List<string>();
            var mine = DaySigs(reg);
            var affected = new HashSet<string>();
            for (int i = 0; i < p.Days.Count; i++)
            {
                var d = p.Days[i];
                if (d == null) continue;
                string baseSig = i < p.BaseSigs.Count ? (p.BaseSigs[i] ?? "") : "";
                mine.TryGetValue(d.Day, out var cur); cur ??= "";
                if (baseSig != cur) { rejected.Add(d.Day); reasons.Add($"{DayName(d.Day)}: changed by the owner meanwhile"); continue; }
                if (OwnerHasHandOnDay(reg, d.Day)) { rejected.Add(d.Day); reasons.Add($"{DayName(d.Day)}: the owner is editing it right now"); continue; }
                string bad = ValidateRefs(reg, p.AddressKey, d);
                if (bad != null) { rejected.Add(d.Day); reasons.Add($"{DayName(d.Day)}: {bad}"); continue; }
                var sd = FindDay(reg, d.Day);
                if (sd == null) { rejected.Add(d.Day); reasons.Add($"{DayName(d.Day)}: not a day this shop has"); continue; }   // never grow the owner's 7-day list
                if (sd.workShifts != null)
                    foreach (var w in sd.workShifts) if (w != null && !IsSynthetic(w.employeeId) && !string.IsNullOrEmpty(w.employeeId)) affected.Add(w.employeeId);
                ReplaceDay(sd, d, keepSynthetic: true);
                if (d.WorkShifts != null)
                    foreach (var w in d.WorkShifts) if (w != null && !string.IsNullOrEmpty(w.EmployeeId)) affected.Add(w.EmployeeId);
                applied.Add(d.Day);
            }
            reason = string.Join("; ", reasons);
            if (applied.Count > 0) AfterRoutedWrite(reg, affected);
        }

        private static string ValidateRefs(BuildingRegistration reg, string addr, ScheduleDayInfo d)
        {
            if (d.WorkShifts == null) return null;
            foreach (var w in d.WorkShifts)
            {
                if (w == null) continue;
                if (IsSynthetic(w.EmployeeId)) return "carried a register stand-in";   // the editor strips these; a payload with one is malformed
                EmployeeInstance emp = null;
                try { EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(w.EmployeeId ?? "", out emp); } catch { }
                if (emp == null) return "an employee in it no longer works for me";
                string at = ""; try { at = emp.assignedAddress != null ? GameStateReader.AddressKey(emp.assignedAddress) : ""; } catch { }
                if (at != addr) { string nm = ""; try { nm = emp.characterData?.name ?? ""; } catch { } return $"'{nm}' is no longer assigned to this shop"; }
                if (!string.IsNullOrEmpty(w.ItemInstanceId) && (reg.itemInstances == null || !reg.itemInstances.ContainsKey(w.ItemInstanceId)))
                    return "a workstation in it no longer exists";
            }
            return null;
        }

        /// <summary>What ScheduleHelper.UpdateEmployeesAfterWorkShiftChange does — minus the parts that need the BizMan
        /// screen open (it reads the static ScheduleHelper.Business). Per affected employee: weekly hours, station items,
        /// idle todo when no shift is left. Then security level, the todo sweep flag, customer entries (what the game
        /// recomputes when the owner closes the tab), the registration-change event, MarkChange. If the owner has this
        /// very schedule open, redraw it (after any drag ends).</summary>
        private static void AfterRoutedWrite(BuildingRegistration reg, HashSet<string> affected)
        {
            foreach (var id in affected)
            {
                EmployeeInstance emp = null;
                try { EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(id, out emp); } catch { }
                if (emp == null) continue;
                try { emp.UpdateWeeklyHoursAndDays(reg.scheduleDays); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} weekly hours for '{id}': {ex.Message}"); }
                try { emp.UpdateAssignedWorkStationItems(); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} station items for '{id}': {ex.Message}"); }
                try { if (!emp.IsAssignedToAnyWorkShift()) { emp.UnAssignWork(); emp.AddTodoTask(TodoTaskType.EmployeeIdle); } }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} idle check for '{id}': {ex.Message}"); }
            }
            try { if (BusinessTypeHelper.GetData(reg)?.HasTag(BigAmbitions.Tags.TagRef.Businesstag.allowtheft) ?? false) reg.UpdateSecurityLevel(); } catch { }
            try { var ui = InstanceBehavior<UI.UIs>.Instance; if (ui != null && ui.tasksUI != null) ui.tasksUI.forceCheckForCompletedTodoTasks = true; } catch { }
            try { CustomerEntriesHelper.UpdateCustomerEntriesForPlayerBusiness(reg, TimeHelper.GetDayOfWeek()); } catch { }
            try { GlobalEvents.onBuildingRegistrationChange?.Invoke(reg.Address); } catch { }
            try { SaveGameManager.MarkChange(); } catch { }
            bool hq = false; try { hq = reg.businessTypeName == "ba:businesstype_headquarters"; } catch { }
            if (IsScheduleTabOpenFor(reg))
            {
                if (hq) try { ScheduleHelper.UpdateHQPlans(); } catch { }
                if (IsDragging()) { string k = ""; try { k = GameStateReader.AddressKey(reg); } catch { } if (k.Length > 0) _pendingRedraw.Add(k); }
                else RedrawScheduleTab(reg);
            }
            else if (hq)
                Plugin.Logger.LogInfo($"{Tag} HQ plan assignments refresh when this HQ's schedule is next opened here (the game's refresh needs the screen open).");
        }

        // ── Owner truth arriving at the editor (snapshot or heartbeat) ─────────

        private static void EditorSnapshot(ScheduleSessionPayload p)
        {
            var reg = FindReg(p.AddressKey);
            if (reg == null) return;
            if (MergerFlip.TrulyMine(reg)) { Plugin.Logger.LogWarning($"{Tag} snapshot for '{p.AddressKey}' but that shop is mine — ignored."); return; }
            if (p.Schedule.Count == 0)
            {
                if (!_baseline.ContainsKey(p.AddressKey)) { _baseline[p.AddressKey] = DaySigs(reg); _baseIds[p.AddressKey] = DayIdsOf(reg); }   // "unchanged": our held copy IS the owner's truth
                return;
            }
            if (p.RejectedDays.Count > 0)
                Plugin.Logger.LogInfo($"{Tag} the owner of '{p.AddressKey}' did not take {p.RejectedDays.Count} day(s): {p.Reason}");
            if (!TryApplyOwnerTruth(reg, p.Schedule, p.AddressKey, "owner snapshot", p.RejectedDays))
                Plugin.Logger.LogInfo($"{Tag} snapshot for '{p.AddressKey}' arrived after it stopped being shared — ignored.");
        }

        /// <summary>Owner truth for a SCHEDULE-MANAGED shop — shared (direct grant) or merged — from a targeted snapshot
        /// or the business heartbeat (GameStatePatcher calls this first; false = not schedule-managed, run the native replace). Reconciled PER DAY against the baseline
        /// (see the class summary); never under a drag; the open Schedule tab is redrawn.</summary>
        public static bool TryApplyOwnerTruth(BuildingRegistration reg, List<ScheduleDayInfo> days, string addr, string why, List<int> forceDays = null)
        {
            if (reg == null || reg.scheduleDays == null || string.IsNullOrEmpty(addr)) return false;
            if (!IsScheduleManaged(reg, addr)) return false;   // live gate only — stale editing state must never keep a shop managed
            if (days == null || days.Count == 0) return true;
            try
            {
                if ((IsDragging() && IsScheduleTabOpenFor(reg)) || IsAutoFilling(reg)) { _pendingTruth[addr] = (days, forceDays, why); return true; }   // never swap the schedule under the player's hand or a running auto-fill
                _pendingTruth.Remove(addr);

                // H-SCHEDWIPE-1 (iv): days this machine changed with nobody editing them are FORCED from the owner's
                // copy. It has to be the force branch: the ordinary branch below skips a day whose incoming signature
                // still equals our baseline, and that is exactly this case — we deliberately never re-baselined.
                List<int>? selfOnly = null;   // days forced ONLY because this machine changed them on its own (never the owner's RejectedDays)
                if (_selfChanged.TryGetValue(addr, out var self) && self.Count > 0)
                {
                    var forced = forceDays != null ? new List<int>(forceDays) : new List<int>();
                    foreach (var sd0 in self) if (!forced.Contains(sd0)) { forced.Add(sd0); (selfOnly ??= new()).Add(sd0); }
                    forceDays = forced;
                    _selfChanged.Remove(addr);
                    if (_truthAskLogged.Remove(addr) && _openSessionAddr != addr)
                        SendSession(new ScheduleSessionPayload { PlayerId = MPConfig.PlayerId, Action = "close", AddressKey = addr });   // the ask's session ends with the ask
                    Plugin.Logger.LogWarning($"{Tag} '{addr}': the owner's truth arrived for {self.Count} day(s) this machine had changed on its own ({why}) — taking their copy verbatim.");
                }

                var local = DaySigs(reg);
                if (!_baseline.TryGetValue(addr, out var baseline)) { _baseline[addr] = baseline = new Dictionary<int, string>(local); _baseIds[addr] = DayIdsOf(reg); }
                _inflight.TryGetValue(addr, out var inflight);
                List<int> lost = null; int taken = 0, echoed = 0;
                foreach (var d in days)
                {
                    if (d == null) continue;
                    int day = d.Day;
                    string incoming = DaySig(d);
                    local.TryGetValue(day, out var l); l ??= "";
                    baseline.TryGetValue(day, out var b); b ??= "";
                    bool dirty = l != b;                                   // a local edit not yet confirmed by the owner
                    (string sig, float at) f = default;
                    bool hasFlight = inflight != null && inflight.TryGetValue(day, out f);
                    // F4 (narrowed by the re-check, 2026-09-18): a day forced ONLY because this machine changed it on
                    // its own is not forced while an edit of ours is in flight for it - the ordinary branch below
                    // reconciles that (our own echo, or the owner wins). The owner's REJECTED days are always forced:
                    // a rejected day is by definition in flight, and the take-back is what rejection exists for.
                    bool force = forceDays != null && forceDays.Contains(day)
                                 && !(hasFlight && selfOnly != null && selfOnly.Contains(day));
                    var sd = FindDay(reg, day);
                    if (sd == null)
                    {
                        if (reg.scheduleDays.Count >= 7 || day < 1 || day > 7) continue;   // never grow past the game's seven
                        // A replica still filling in: insert at the Monday..Sunday position — the game addresses days by
                        // list index (GetScheduleDay(i) => ScheduleDays[i-1]) and the day buttons pair by position.
                        sd = new ScheduleDay { day = (DayOfWeekOrdered)day };
                        int at = 0; while (at < reg.scheduleDays.Count && reg.scheduleDays[at] != null && (int)reg.scheduleDays[at].day < day) at++;
                        reg.scheduleDays.Insert(at, sd);
                    }
                    if (force)
                    {
                        bool differs = incoming != l;
                        ReplaceDay(sd, d, keepSynthetic: true);
                        baseline[day] = incoming; BaseIdsDay(addr, day, sd); inflight?.Remove(day);
                        if (differs) (lost ??= new()).Add(day);
                        continue;
                    }
                    if (incoming == b) continue;                           // the owner's day is unchanged since our baseline — keep any local edit
                    bool echo = hasFlight && f.sig == incoming;            // the owner applied OUR edit
                    ReplaceDay(sd, d, keepSynthetic: true);                // any local duty stand-in shifts stay (the owner's never arrive)
                    baseline[day] = incoming; BaseIdsDay(addr, day, sd); inflight?.Remove(day);
                    if (echo) echoed++;
                    else { taken++; if (dirty) (lost ??= new()).Add(day); }
                }
                // Log only — this feature puts nothing on screen (user ruling 2026-08-21).
                if (lost != null)
                    Plugin.Logger.LogInfo($"{Tag} '{addr}': the owner's version replaced what this machine held on {DayNames(lost)} ({why}) — a local edit, or a day this replica had changed on its own.");
                if (echoed > 0 || taken > 0)
                    Plugin.Logger.LogInfo($"{Tag} '{addr}' updated from the owner ({why}): {echoed} day(s) confirmed ours, {taken} day(s) changed by them.");
                if (IsScheduleTabOpenFor(reg)) RedrawScheduleTab(reg);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} TryApplyOwnerTruth '{addr}' ({why}): {ex.Message}"); }
            return true;
        }

        // ── Day helpers ────────────────────────────────────────────────────────

        public static bool IsSynthetic(string employeeId)
            => !string.IsNullOrEmpty(employeeId) && employeeId.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal);

        internal static ScheduleDay FindDay(BuildingRegistration reg, int day)
        {
            foreach (var sd in reg.scheduleDays) if (sd != null && (int)sd.day == day) return sd;
            return null;
        }

        /// <summary>Rewrite one ScheduleDay IN PLACE from its DTO (the UI holds references to the day objects).
        /// keepSynthetic: this machine's own duty stand-ins survive (the other side never sees or sends them).</summary>
        internal static void ReplaceDay(ScheduleDay sd, ScheduleDayInfo d, bool keepSynthetic)
        {
            sd.isOpen = d.IsOpen;
            sd.openingHourSlots ??= new List<OpeningHourSlot>();
            sd.openingHourSlots.Clear();
            if (d.OpeningHourSlots != null)
                foreach (var slot in d.OpeningHourSlots)
                    if (slot != null) sd.openingHourSlots.Add(new OpeningHourSlot(slot.StartingHour, slot.EndingHour));
            List<WorkShift> keep = null;
            sd.workShifts ??= new List<WorkShift>();
            if (keepSynthetic)
                foreach (var w in sd.workShifts) if (w != null && IsSynthetic(w.employeeId)) (keep ??= new()).Add(w);
            sd.workShifts.Clear();
            if (d.WorkShifts != null)
                foreach (var shift in d.WorkShifts)
                {
                    if (shift == null) continue;
                    if (keepSynthetic && IsSynthetic(shift.EmployeeId)) continue;
                    sd.AddWorkShift(new WorkShift
                    {
                        employeeId = shift.EmployeeId ?? "", itemInstanceId = shift.ItemInstanceId ?? "",
                        startingHour = shift.StartingHour, endingHour = shift.EndingHour,
                        type = (WorkShiftType)shift.Type,
                    });
                }
            if (keep != null) foreach (var k in keep) sd.AddWorkShift(k);
        }

        public static ScheduleDayInfo SerializeDay(ScheduleDay sd, bool stripSynthetic)
        {
            var dto = new ScheduleDayInfo { Day = (int)sd.day, IsOpen = sd.isOpen };
            if (sd.openingHourSlots != null)
                foreach (var s in sd.openingHourSlots)
                    if (s != null) dto.OpeningHourSlots.Add(new OpeningHourSlotInfo { StartingHour = s.startingHour, EndingHour = s.endingHour });
            if (sd.workShifts != null)
                foreach (var w in sd.workShifts)
                {
                    if (w == null) continue;
                    if (stripSynthetic && IsSynthetic(w.employeeId)) continue;
                    dto.WorkShifts.Add(new WorkShiftInfo
                    {
                        EmployeeId = w.employeeId ?? "", ItemInstanceId = w.itemInstanceId ?? "",
                        StartingHour = w.startingHour, EndingHour = w.endingHour, Type = (int)w.type,
                    });
                }
            return dto;
        }

        /// <summary>All days — the owner's snapshot. Duty stand-ins stripped, exactly like the business heartbeat
        /// (BusinessSync.ReadInfo skips IsSyntheticDutyEmployee): the editor never sees or sends them.</summary>
        public static void SerializeSchedule(BuildingRegistration reg, List<ScheduleDayInfo> into)
        {
            try
            {
                if (reg.scheduleDays == null) return;
                foreach (var sd in reg.scheduleDays)
                    if (sd != null) into.Add(SerializeDay(sd, stripSynthetic: true));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} serialize: {ex.Message}"); }
        }

        // Per-day signature: open flag + hour slots + every REAL shift, both lists sorted so the order the game happens
        // to keep them in can never differ between machines. The ScheduleDay and ScheduleDayInfo forms MUST stay
        // byte-identical — they are compared against each other across the wire.
        private static string BuildDaySig(int day, bool isOpen, List<string> slots, List<string> shifts)
        {
            slots.Sort(string.CompareOrdinal); shifts.Sort(string.CompareOrdinal);
            var sb = new System.Text.StringBuilder();
            sb.Append(day).Append(isOpen ? 'o' : 'c');
            foreach (var s in slots) sb.Append(s).Append(',');
            sb.Append('#');
            foreach (var s in shifts) sb.Append(s).Append(';');
            return sb.ToString();
        }

        public static string DaySig(ScheduleDay sd)
        {
            var slots = new List<string>(); var shifts = new List<string>();
            try
            {
                if (sd.openingHourSlots != null)
                    foreach (var s in sd.openingHourSlots) if (s != null) slots.Add($"{s.startingHour}-{s.endingHour}");
                if (sd.workShifts != null)
                    foreach (var w in sd.workShifts)
                        if (w != null && !IsSynthetic(w.employeeId)) shifts.Add($"{w.employeeId}@{w.itemInstanceId}:{w.startingHour}-{w.endingHour}/{(int)w.type}");
                return BuildDaySig((int)sd.day, sd.isOpen, slots, shifts);
            }
            catch { return ""; }
        }

        public static string DaySig(ScheduleDayInfo d)
        {
            var slots = new List<string>(); var shifts = new List<string>();
            try
            {
                if (d.OpeningHourSlots != null)
                    foreach (var s in d.OpeningHourSlots) if (s != null) slots.Add($"{s.StartingHour}-{s.EndingHour}");
                if (d.WorkShifts != null)
                    foreach (var w in d.WorkShifts)
                        if (w != null && !IsSynthetic(w.EmployeeId)) shifts.Add($"{w.EmployeeId ?? ""}@{w.ItemInstanceId ?? ""}:{w.StartingHour}-{w.EndingHour}/{w.Type}");
                return BuildDaySig(d.Day, d.IsOpen, slots, shifts);
            }
            catch { return ""; }   // same failure value as the ScheduleDay overload — the two must stay symmetric
        }

        public static Dictionary<int, string> DaySigs(BuildingRegistration reg)
        {
            var dict = new Dictionary<int, string>();
            try
            {
                if (reg.scheduleDays != null)
                    foreach (var sd in reg.scheduleDays)
                        if (sd != null) dict[(int)sd.day] = DaySig(sd);
            }
            catch { }
            return dict;
        }

        /// <summary>Whole-schedule signature (stand-ins excluded): the "is your copy current?" comparison.</summary>
        public static string ScheduleSig(BuildingRegistration reg)
        {
            var sigs = DaySigs(reg);
            var keys = new List<int>(sigs.Keys); keys.Sort();
            var sb = new System.Text.StringBuilder();
            foreach (var k in keys) sb.Append(sigs[k]).Append('|');
            return sb.ToString();
        }

        private static string DayName(int day)
        {
            string s = "";
            try { s = ((DayOfWeekOrdered)day).ToString(); } catch { }
            if (string.IsNullOrEmpty(s) || char.IsDigit(s[0]) || s[0] == '-')
                s = day >= 1 && day < DayNamesFallback.Length ? DayNamesFallback[day] : $"day {day}";
            return s;
        }
        private static string DayNames(List<int> days)
        {
            days.Sort();
            var names = new List<string>();
            foreach (var d in days) names.Add(DayName(d));
            return string.Join(", ", names);
        }

        // ── Schedule UI helpers (either machine) ───────────────────────────────

        /// <summary>SCHEDULE-2 fold (review M3): the same "not under the player's hand" test TryApplyOwnerTruth uses
        /// (:589), for the heartbeat's in-place branch in GameStatePatcher.</summary>
        internal static bool ScheduleBusy(BuildingRegistration reg)
        {
            try { return (IsDragging() && IsScheduleTabOpenFor(reg)) || IsAutoFilling(reg); } catch { return false; }
        }

        private static bool IsDragging()
        {
            try { return WorkShiftDrag.CurrentDraggedWorkShift != null; } catch { return false; }
        }

        private static readonly System.Reflection.FieldInfo _fActiveFillers = AccessTools.Field(typeof(BizManSchedule), "_activeAutoFillers");

        /// <summary>Is the game's auto-fill (a background thread writing shifts into this registration's days) running
        /// here? Owner truth must not land under it, and the edit scan waits for it to finish so the fill ships as one
        /// change-set instead of a trickle.</summary>
        private static bool IsAutoFilling(BuildingRegistration reg)
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var sched = ui != null && ui.fullMenu != null ? ui.fullMenu.schedule : null;
                if (sched == null || _fActiveFillers == null) return false;
                if (_fActiveFillers.GetValue(sched) is not System.Collections.IEnumerable fillers) return false;
                foreach (var f in fillers)
                    if (f is Buildings.Schedule.ScheduleAutoFiller af && ReferenceEquals(af.Registration, reg)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>The local player is dragging a shift on THIS shop's schedule and is looking at THIS day. A drag does
        /// not change the day list until the drop (WorkShiftDrag.OnBeginDrag only touches the helper's cache), so a
        /// routed edit for that day would pass the signature check and detach the dragged shift. Owner wins instead.</summary>
        private static bool OwnerHasHandOnDay(BuildingRegistration reg, int day)
        {
            try
            {
                if (!IsDragging() || !IsScheduleTabOpenFor(reg)) return false;
                var cur = ScheduleHelper.CurrentScheduleDay;
                return cur != null && (int)cur.day == day;
            }
            catch { return false; }
        }

        /// <summary>Is THIS machine's BizMan Schedule tab currently showing this registration?</summary>
        internal static bool IsScheduleTabOpenFor(BuildingRegistration reg)
        {
            try
            {
                var biz = ScheduleHelper.Business;
                if (biz == null || !ReferenceEquals(biz.buildingRegistration, reg)) return false;
                var sched = biz.bizManSchedule;
                return sched != null && sched.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        private static readonly System.Reflection.FieldInfo  _fDaySel   = AccessTools.Field(typeof(BizManSchedule), "daySelectionController");
        private static readonly System.Reflection.FieldInfo  _fButtons  = AccessTools.Field(typeof(ScheduleDaySelectionController), "_scheduleDayButtons");
        private static readonly System.Reflection.FieldInfo  _fSelected = AccessTools.Field(typeof(ScheduleDaySelectionController), "_selectedDayIndex");
        private static readonly System.Reflection.MethodInfo _mSelect   = AccessTools.Method(typeof(ScheduleDaySelectionController), "OnDaySelected", new[] { typeof(int) });

        /// <summary>Redraw the open Schedule tab from the registration's CURRENT days without jumping the player's
        /// selected day: what LoadScheduler does (re-fetch staff + workstations, day buttons) then re-select the day
        /// they are looking at.</summary>
        private static bool _reflectionMissLogged;
        internal static void RedrawScheduleTab(BuildingRegistration reg)
        {
            try
            {
                if (!IsScheduleTabOpenFor(reg)) return;
                if ((_fDaySel == null || _fButtons == null || _fSelected == null || _mSelect == null) && !_reflectionMissLogged)
                {   // a game update renamed a private member — say so once instead of silently never redrawing
                    _reflectionMissLogged = true;
                    Plugin.Logger.LogWarning($"{Tag} schedule redraw disabled: a private schedule-UI member was not found (daySel={_fDaySel != null}, buttons={_fButtons != null}, selected={_fSelected != null}, select={_mSelect != null}).");
                }
                var sched = ScheduleHelper.Business.bizManSchedule;
                ScheduleHelper.FetchEmployees(reg.Address);
                ScheduleHelper.FetchWorkstations();
                var dsc = _fDaySel?.GetValue(sched) as ScheduleDaySelectionController;
                if (dsc == null) return;
                var buttons = _fButtons?.GetValue(dsc) as List<ScheduleDayButton>;
                var days = reg.scheduleDays;
                if (buttons != null && days != null)
                    for (int i = 0; i < buttons.Count && i < days.Count; i++)
                        try { if (days[i] != null) buttons[i].UpdateDayButton(days[i].isOpen); } catch { }
                int sel = _fSelected != null ? (int)_fSelected.GetValue(dsc) : 0;
                if (sel < 1 || sel > 7) { try { sel = (int)TimeHelper.GetDayOfWeek(); } catch { sel = 1; } }   // before the first UpdateState it is 0 (→ ScheduleDays[-1])
                if (sel < 1 || sel > 7) sel = 1;
                _mSelect?.Invoke(dsc, new object[] { sel });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} redraw: {ex.InnerException?.Message ?? ex.Message}"); }   // reflection wraps the real exception
        }

        // ── H-SCHEDWIPE-1: the three OFF-TAB player mutators ──────────────────
        // The design read of 2026-09-18 found exactly three native routes that rewrite a day's shifts with no
        // Schedule tab open and without raising OnWorkShiftChanged. Each prefix resolves the affected address LIVE
        // (UnassignEmployeeFromAllWorkshifts clears assignedAddress itself, so a postfix would have nothing left to
        // read) and latches it for the next scan. The first of the three is ALSO a simulation route — retirement
        // inside EmployeeHelper.RunDaily calls it — so the latch is refused while a native full-roster pass is
        // bracketed by InEmployeePass.
        private static void Touch(string addr)
        {
            try
            {
                if (string.IsNullOrEmpty(addr)) return;
                if (InEmployeePass > 0) return;                                                 // the simulation's hand, not the player's
                if (GrantSync.SharedManageCount == 0 && MergerFlip.FlippedCount == 0) return;   // nothing here is schedule-managed, so nothing would read the latch
                _touched.Add(addr);
            }
            catch { }
        }

        /// <summary>A player unassigning an employee (HR screens, firing) clears every shift of theirs.</summary>
        [HarmonyPatch(typeof(EmployeeHelper), nameof(EmployeeHelper.UnassignEmployeeFromAllWorkshifts))]
        public static class Patch_Unassign_ScheduleIntent
        {
            static void Prefix(EmployeeInstance employeeInstance)
            {
                try { if (employeeInstance != null && employeeInstance.assignedAddress != null) Touch(GameStateReader.AddressKey(employeeInstance.assignedAddress)); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} WARNING Patch_Unassign_ScheduleIntent: {ex.Message}"); }
            }
        }

        /// <summary>A player removing a workstation drops the shifts bound to it.</summary>
        [HarmonyPatch(typeof(BuildingRegistration), nameof(BuildingRegistration.RemoveWorkShiftsForItem))]
        public static class Patch_RemoveWorkShiftsForItem_ScheduleIntent
        {
            static void Prefix(BuildingRegistration __instance)
            {
                try { if (__instance != null) Touch(GameStateReader.AddressKey(__instance)); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} WARNING Patch_RemoveWorkShiftsForItem_ScheduleIntent: {ex.Message}"); }
            }
        }

        /// <summary>A player picking an assignable item up out of the building it was placed in.</summary>
        [HarmonyPatch(typeof(ItemHelper), nameof(ItemHelper.RemoveFromWorkShifts))]
        public static class Patch_RemoveFromWorkShifts_ScheduleIntent
        {
            static void Prefix(Address initialAddress)
            {
                try { Touch(GameStateReader.AddressKey(initialAddress)); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} WARNING Patch_RemoveFromWorkShifts_ScheduleIntent: {ex.Message}"); }
            }
        }

        /// <summary>Scene teardown. _seq survives on purpose (monotonic per process under one SeqEpoch).</summary>
        public static void Reset()
        {
            _baseline.Clear(); _inflight.Clear(); _pendingTruth.Clear(); _pendingRedraw.Clear();
            _sessions.Clear(); _lastPushedSig.Clear(); _appliedSeq.Clear();
            _touched.Clear(); _selfChanged.Clear(); _truthAskLogged.Clear(); _baseIds.Clear();
            _ownBase.Clear(); _ownIds.Clear();   // the probe's own budget and its printed keys are per PROCESS, not per world
            _ownBaseTaken = false; _probeSeenStrip = -1; _probeSeenRestore = -1;
            // F7: a world that went away mid-pass would otherwise leave the bracket counter stuck above zero, and
            // every off-tab player mutator after it would be read as the simulation's hand for good.
            InEmployeePass = 0;
            _openSessionAddr = "";
        }
    }
}
