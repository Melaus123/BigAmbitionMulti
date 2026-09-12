using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Entities;   // EmployeeInstance

namespace BigAmbitionsMP
{
    /// <summary>
    /// Employee axis — merger slice 5 (merger map §12). The roster sync (round 30) already puts every
    /// partner employee into each member's save as an injected REAL-id record, and the ownership flip
    /// already makes native lists include partner shops — so "seeing" partner staff is display
    /// fidelity (real wage/skills on the injected records, done in MPRegisterSync), and this file is
    /// the WRITE half: employee mutations made from a member's native menus route to the owner.
    ///
    ///  • FIRE: MyEmployees' fire button runs EmployeeInstance.RemoveEmployee. On an INJECTED record
    ///    that would only strip the local replica (divergence) — instead the local record is dropped
    ///    optimistically and the op routes to the owner, who runs the native RemoveEmployee (shift
    ///    unassign, autofill abort, HR plans, security recalc) and force-republishes the roster.
    ///  • SCHEDULE (hours + shifts): RETIRED here 2026-09-10. The wholesale write-back this file used to
    ///    run (2s scan → whole-schedule send → unvalidated rebuild on the owner) is gone; merged shops now
    ///    travel the VALIDATED per-day pipeline in SharedShopSchedule (IsScheduleManaged is true for them),
    ///    and a routed "schedule" op from an old build is logged and ignored.
    ///
    /// Host validates every op (MPServer.HostRouteEmployeeEdit): the FIRE op needs the owner himself or a
    /// co-member of the same company (2026-09-12 - a business helper may not fire the owner's staff); the
    /// other ops (assign/adopt) also accept a direct Business grant, the same union the host gates read;
    /// the transfer legs branch off to HostRouteTransfer before that gate. INERT without
    /// a merger: no injected records → the fire patch passes through; no flipped shops → no scan.
    /// </summary>
    public static class MergerEmployeeSync
    {
        // ── FIRE routing ──────────────────────────────────────────────────────

        [HarmonyPatch(typeof(EmployeeInstance), nameof(EmployeeInstance.RemoveEmployee))]
        public static class Patch_EmployeeInstance_Remove_MergerRoute
        {
            static bool Prefix(EmployeeInstance __instance)
            {
                try
                {
                    string id = __instance?.id ?? "";
                    if (!MPRegisterSync.IsInjectedStaff(id)) return true;   // my own employee → native
                    string addr = MPRegisterSync.InjectedAddrOf(id);
                    Plugin.Logger.LogInfo($"[MergerStaff] routed FIRE '{id}' @ '{addr}' → owner.");
                    Send(new EmployeeEditPayload { PlayerId = MPConfig.PlayerId, Action = "fire", AddressKey = addr, EmployeeId = id });
                    // Optimistic local removal (the injected record + dictionary entry) so the list
                    // reflects the fire immediately; the owner's roster republish is the confirmation
                    // (and re-adds it if the owner machine never processed the op — self-healing).
                    MPRegisterSync.DropInjectedStaff(id);
                    return false;   // never run native RemoveEmployee on a replica record
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerStaff] fire route: {ex.Message}"); return true; }
            }
        }

        // ── flipped-shop scan (assignments only — the schedule write-back retired 2026-09-10) ──

        private static float _nextScan;

        /// <summary>U3 PACING (user ruling 2026-09-12): a MyEmployees mass-assign moves N records in one
        /// frame, so one scan used to send N legs at once and the host's SharedRateOk (10 per sender per
        /// second) dropped the rest silently - copies left pointing at a shop that never adopted them.  The
        /// scan now sends at most LegsPerScan per pass; a record past the budget is left EXACTLY as the
        /// player set it, and the next pass (2 s later) re-detects it and sends it.  The 30 s in-flight
        /// guard is keyed on a request that was actually sent, so a deferred record is not held by it.</summary>
        private const int LegsPerScan = 6;
        private static int _legsThisScan, _deferredThisScan;

        /// <summary>True = this scan may still send a leg (and one is now booked).  False = over budget: the
        /// caller must leave the record untouched.</summary>
        private static bool TakeLeg()
        {
            if (_legsThisScan >= LegsPerScan) { _deferredThisScan++; return false; }
            _legsThisScan++;
            return true;
        }

        /// <summary>MAIN THREAD (MPCanvasUI.Update). Detect own-employee assignments into flipped shops
        /// (→ record migration) and unsupported reassignments of injected partner staff (→ revert + toast).
        /// Schedules are NOT scanned here — they travel the validated SharedShopSchedule path (2026-09-10).</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 2f;
            if (!MergerSync.IAmMember || MergerFlip.FlippedCount == 0) return;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                ScanAssignments(gi);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerStaff] scan: {ex.Message}"); }
        }

        // ── HIRE/ASSIGN into a partner shop = record migration ("adopt") ──────
        // The assignment surface is a plain dropdown write (CandidateCellView :81 — no chokepoint), so
        // detection: one of MY OWN hired employees pointing at a FLIPPED address means "the member
        // staffed a partner shop from their menu". The record must MIGRATE into the owner's save —
        // that's where the staffing engine and payroll for the shop live; a local record would be a
        // ghost no machine simulates. Keep-until-confirmed: the local record is removed only when the
        // owner's roster republish carries the id back (no loss on a dropped op; 30s retry).

        private static readonly Dictionary<string, float> _pendingAdopt = new();   // employeeId → sentAt

        /// <summary>U1 (re-check r3 MAJOR-1 + rig run 4 RIG-1): where each of MY OWN records was last seen
        /// SETTLED ("" = the bench). The assignment dropdown and the `transfer` verb write assignedAddress
        /// BEFORE this 2 s scan ever sees the record, so by the time a move is detected the live field
        /// already reads the DESTINATION - the real source building was lost and EVERY own-employee move was
        /// requested "from bench", which bypassed the host's from-end validation and left the give-back with
        /// no address to return to. This map is refreshed on every pass BEFORE the change test and FROZEN
        /// while a request is in flight, so the request (and the host's give-back) names where they came
        /// from.</summary>
        private static readonly Dictionary<string, string> _lastOwnAddr = new();

        /// <summary>U1: transfer id -> the game day+hour a failed re-adopt was last logged. The host never
        /// drops a record it holds, so it re-offers the give-back every game hour; the log is throttled to
        /// match instead of repeating on every offer.</summary>
        private static readonly Dictionary<string, (int day, int hour)> _returnFailLog = new();

        private static void ScanAssignments(GameInstance gi)
        {
            if (gi.EmployeeInstances == null) return;
            _legsThisScan = 0; _deferredThisScan = 0;      // U3: the per-scan leg budget
            var seenOwn = new HashSet<string>();
            foreach (var e in gi.EmployeeInstances)
            {
                if (e == null || string.IsNullOrEmpty(e.id)) continue;
                // Phase 4b (people) P2: an UNASSIGN of a partner's employee is a move too, so a null address
                // is read as the empty key here instead of skipping the record outright.
                string addr = "";
                try { if (e.assignedAddress != null) addr = GameStateReader.AddressKey(e.assignedAddress); } catch { continue; }

                // H-ADOPT-1 (harness run T-P0-4, 2026-09-10): the mod's own register stand-ins (BAMP_DUTY_*, the
                // synthetic cashiers a visitor injects for a partner's staffed tills) sit at flipped addresses and are
                // neither injected staff nor candidates - this scan adopted 55 of them OUT to the owner, who ADOPTED 48
                // phantom 'On-Duty Staff' at $0/h into its live roster (the save strip kept the .hsg clean). Never a real hire.
                if (MPRegisterSync.IsSyntheticDuty(e.id)) continue;

                if (MPRegisterSync.IsInjectedStaff(e.id))
                {
                    // MERGER PHASE 4b (PEOPLE) P2 (D20-4): a partner's employee pointed somewhere else used to be
                    // reverted with a toast. It now MOVES - see TryMovePartnerStaff for the two shapes (a move
                    // inside that one partner's shops is an ordinary routed assign; a move to a shop ANOTHER
                    // machine runs is the two-phase release/adopt).
                    string home = MPRegisterSync.InjectedAddrOf(e.id);
                    if (home != addr) TryMovePartnerStaff(gi, e, home, addr);
                    else ConfirmTransfer(e.id, addr);
                    continue;
                }
                // U1/RIG-1: remember where this record stands BEFORE the change test below, and FREEZE the
                // memory while a request for it is in flight - a 30 s retry must still name the real source.
                bool candidate = false; try { candidate = e.IsCandidate; } catch { }
                seenOwn.Add(e.id);
                string prevOwn = _lastOwnAddr.TryGetValue(e.id, out var pv) ? pv : addr;
                bool outbound = addr.Length > 0 && !candidate && MergerFlip.IsFlipped(addr);
                _pendingAdopt.TryGetValue(e.id, out var sentAt);
                bool inFlight = sentAt > 0f && Time.unscaledTime - sentAt < 30f;
                if (!outbound && !inFlight) _lastOwnAddr[e.id] = addr;   // settled on the machine that runs it

                if (addr.Length == 0 || !MergerFlip.IsFlipped(addr)) continue;   // my own record only migrates INTO a partner's shop
                if (candidate) continue;   // negotiate first — the transfer happens once they're hired

                // RIG-12 + T1(g) r2: MY OWN employee pointed at a PARTNER's shop used to take the adopt-OUT
                // path - the owner adopted first and this machine released only when their roster came back,
                // so BOTH saves held the record for a round trip and an hourly wage tick inside that window
                // paid twice. It is the same host-held transfer as every other direction now: release first,
                // the host holds, the destination adopts. The old "adopting-out ... on confirm" sender is
                // retired (it was reached only from here, behind MergerFlip.IsFlipped - merged-only).
                if (inFlight) continue;
                if (!TakeLeg()) continue;                  // U3: over budget - the record stays as the player set it
                _pendingAdopt[e.id] = Time.unscaledTime;
                // U1: the FROM end is where this record was last seen, not what the dropdown has already
                // written. Equal to the destination (first sight, or they were already standing there) means
                // the requester's own save simply holds them - the host then takes the REQUESTER as the
                // source runner, which is also the bench shape.
                RequestTransfer(e.id, prevOwn == addr ? "" : prevOwn, addr);
            }
            // A record that has left this save (a completed transfer, a fire) takes its memory with it.
            if (_lastOwnAddr.Count > 0)
            {
                List<string> stale = null;
                foreach (var kv in _lastOwnAddr) if (!seenOwn.Contains(kv.Key)) (stale ??= new List<string>()).Add(kv.Key);
                if (stale != null) foreach (var k in stale) _lastOwnAddr.Remove(k);
            }
            if (_deferredThisScan > 0)
                Plugin.Logger.LogInfo($"[Transfer] {_deferredThisScan} move(s) wait for the next scan ({LegsPerScan} per 2 s keeps under the host's cap).");
        }

        // ── MERGER PHASE 4b (PEOPLE) P2: EMPLOYEE MIXING ──────────────────────
        // Moving a partner's employee is two different problems wearing one dropdown:
        //  * to another shop of THAT SAME partner - one machine runs both ends, so it is the ordinary
        //    routed assign the owner already performs natively (SharedShopStaff.CommitAssign);
        //  * to a shop a DIFFERENT machine runs (another partner's, or one of mine) - the RECORD has to
        //    change save. That cannot be one message: both machines would briefly hold a live record with
        //    the same real id, and payroll is id-keyed, so the employee would be paid twice for the length
        //    of the overlap. So it is RELEASE first, ADOPT second, under one transfer id, and if the adopt
        //    refuses, the source re-adopts its own record ("unrelease") - nobody is ever lost.

        private static readonly Dictionary<string, (string tid, float at, string target)> _pendingTransfer = new();   // employeeId -> in flight
        private static readonly HashSet<string> _transfersDone = new();                                               // transfer ids already applied here

        /// <summary>MAIN THREAD. The local copy of a partner's employee disagrees with the owner's published
        /// placement, i.e. somebody used the dropdown here. `home` is where the owner says they work ("" = none
        /// on record), `target` where this machine now points them ("" = unassigned).</summary>
        private static void TryMovePartnerStaff(GameInstance gi, EmployeeInstance e, string home, string target)
        {
            string id = e.id;
            string owner = MPRegisterSync.OwnerOfInjected(id);
            if (owner.Length == 0) return;                                             // no owner on record: nothing to route to
            // A DIRECT Business grant has its own, older pipeline (SharedShopStaff's assignment scan); this
            // branch is for merger copies only, so the two can never both route the same change.
            if (GrantSync.IsGrantedDirect(GrantKind.Business, owner, MPConfig.PlayerId)) return;
            // HO-1c L2 2026-09-12: a BENCH copy of a CO-MEMBER's employee can now be placed.  bench -> the SAME
            // owner's shop takes the routed-assign branch below (CommitAssign with an empty home; the owner's own
            // applier places them off its bench).  bench -> ANOTHER member's shop asks for a host-held transfer
            // whose SOURCE is the OWNER, not the asker: the host reads its published bench and points the release
            // leg there (MPServer.cs:7345), instead of the plain "string src = senderPid;" (MPServer.cs:7328)
            // that made the asker the source; "one machine runs both ends" (MPServer.cs:7356) still refuses the
            // requester's OWN bench.  A bench copy of a NON-member is a direct grant's view - that pipeline is
            // untouched, so it stays put.
            if (home.Length == 0 && !MergerSync.IsMemberPid(owner)) return;             // not a merger record

            if (_pendingTransfer.TryGetValue(id, out var f))
            {
                if (Time.unscaledTime - f.at < 30f) return;                            // in flight
                _pendingTransfer.Remove(id);
                try { e.assignedAddress = AddressOfKey(gi, home); } catch { }
                Plugin.Logger.LogWarning($"[Transfer] '{id}' was not confirmed for '{f.target}' after 30 s - the copy goes back to '{home}' (nothing moved).");
                return;
            }

            // UNASSIGN, or a move inside the SAME owner's shops: one machine runs both ends.
            string destOwner = "";
            if (target.Length > 0)
            {
                var treg = FindRegByKey(gi, target);
                if (treg == null)
                {
                    try { e.assignedAddress = AddressOfKey(gi, home); } catch { }
                    Plugin.Logger.LogInfo($"[Transfer] '{id}' was pointed at '{target}', which this machine cannot resolve - put back at '{home}'.");
                    return;
                }
                destOwner = MergerFlip.TrulyMine(treg) ? MPConfig.PlayerId : MergerFlip.ParkedRunner(target);
                if (destOwner.Length == 0)
                {
                    try { e.assignedAddress = AddressOfKey(gi, home); } catch { }
                    Plugin.Logger.LogInfo($"[Transfer] '{id}' was pointed at '{target}', which belongs to nobody in this company - put back at '{home}'.");
                    return;
                }
            }
            if (target.Length == 0 || destOwner == owner)
            {
                if (!TakeLeg()) return;                    // U3: over budget - nothing is written back, the next scan sends it
                if (SharedShopStaff.CommitAssign(id, home, target))
                {
                    _pendingTransfer[id] = ("", Time.unscaledTime, target);
                    Plugin.Logger.LogInfo($"[Transfer] '{id}' stays with '{owner}': routed {(target.Length == 0 ? "unassign" : "assign")} '{(home.Length == 0 ? "bench" : home)}' -> '{(target.Length == 0 ? "bench" : target)}'.");
                }
                else
                {
                    try { e.assignedAddress = AddressOfKey(gi, home); } catch { }
                    Plugin.Logger.LogWarning($"[Transfer] '{id}': no route out of this machine - put back at '{home}'.");
                }
                return;
            }

            // CROSS-OWNER: the record itself has to move, and the HOST is the authority for the move
            // (T1). This machine only asks; it never tells the source to let go and it never posts the
            // record at the destination itself.
            if (!TakeLeg()) return;                        // U3: over budget - the next scan sends it
            RequestTransfer(id, home, target);
        }

        /// <summary>T1(a): ask the host to move one record from one shop to another. The host validates
        /// both ends, asks the SOURCE runner to release, HOLDS the record while it is in the air, and
        /// hands it back to the source if the destination refuses or never answers. Nothing is released
        /// here and nothing is adopted here on the strength of this message.</summary>
        private static void RequestTransfer(string employeeId, string fromKey, string toKey)
        {
            if (string.IsNullOrEmpty(employeeId) || string.IsNullOrEmpty(toKey)) return;
            string tid = Guid.NewGuid().ToString("N");
            _pendingTransfer[employeeId] = (tid, Time.unscaledTime, toKey);
            Plugin.Logger.LogInfo($"[Transfer] {tid}: requested - asking the host to move '{employeeId}' from '{(fromKey.Length == 0 ? "bench" : fromKey)}' to '{toKey}'.");
            Send(new EmployeeEditPayload
            {
                PlayerId = MPConfig.PlayerId, Action = "transfer-request",
                AddressKey = fromKey ?? "", OtherAddressKey = toKey,
                EmployeeId = employeeId, TransferId = tid,
            });
        }

        /// <summary>r4 MINOR-3: THE SOURCE REFUSED. Until now every one of the four refusals above was a
        /// LOCAL log, so the host held a "requested" entry for a whole game hour and the initiator's copy
        /// stood at the destination until its own 30 s give-back. The host is answered at once instead; it
        /// closes the entry and tells the initiator. The local cancel below covers the case where THIS
        /// machine is both source and initiator (no round trip can help there).</summary>
        private static void RefuseRelease(EmployeeEditPayload p)
        {
            try
            {
                Send(new EmployeeEditPayload
                {
                    PlayerId = MPConfig.PlayerId, Action = "release-refused",
                    EmployeeId = p.EmployeeId ?? "", TransferId = p.TransferId ?? "",
                    AddressKey = p.AddressKey ?? "", OtherAddressKey = p.OtherAddressKey ?? "",
                });
                CancelPendingTransfer(p.EmployeeId, "the source would not release them");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] release refusal: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>r4 MINOR-3: the initiator's copy goes home NOW rather than at its 30 s give-back. A
        /// no-op when this machine has nothing in flight for that employee.</summary>
        public static void CancelPendingTransfer(string employeeId, string why)
        {
            try
            {
                if (string.IsNullOrEmpty(employeeId)) return;
                if (!_pendingTransfer.Remove(employeeId)) return;
                var gi = SaveGameManager.Current;
                EmployeeInstance? e = null;
                try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(employeeId, out e); } catch { }
                string home = ""; try { home = MPRegisterSync.InjectedAddrOf(employeeId); } catch { }
                if (e != null && gi != null && home.Length > 0)
                    try { e.assignedAddress = AddressOfKey(gi, home); } catch { }
                Plugin.Logger.LogWarning($"[Transfer] '{employeeId}': {why} - the copy goes back to '{(home.Length == 0 ? "the bench" : home)}' now (nothing moved).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] cancel pending: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>MINOR-10 r2: the caller is the MERGER SCAN above (ScanAssignments), which sees the
        /// partner's copy sitting where this machine asked for it once the owner's roster has caught up -
        /// not the MPRegisterSync roster apply the old comment named. The move has landed, so the pending
        /// entry goes. Never removes a record.</summary>
        public static bool ConfirmTransfer(string employeeId, string addressKey)
        {
            if (!_pendingTransfer.TryGetValue(employeeId, out var f)) return false;
            if (f.target.Length > 0 && f.target != addressKey) return false;
            _pendingTransfer.Remove(employeeId);
            Plugin.Logger.LogInfo($"[Transfer] '{employeeId}' is now on the roster of '{addressKey}' - the move is confirmed.");
            return true;
        }

        /// <summary>THE WHOLE RECORD as the wire carries it (the P3-A "record" shape, reused verbatim so the
        /// adopt reconstruction on the other side resumes a life instead of stamping a fresh hire).</summary>
        public static EmployeeEditPayload RecordOf(EmployeeInstance e, string addressKey, string action)
        {
            var r = new EmployeeEditPayload { PlayerId = MPConfig.PlayerId, Action = action, AddressKey = addressKey, EmployeeId = e?.id ?? "" };
            if (e == null) return r;
            try { r.Wage = e.hourlyWage; r.Satisfaction = e.satisfaction; } catch { }
            try { r.DayHired = e.dayHired; r.NextSickDay = e.nextSickDay; } catch { }
            try { r.WorkedHoursToday = e.workedHoursToday; r.WorkedHoursThisWeek = e.workedHoursThisWeek; } catch { }
            try { r.WorkedDays = e.workedDays; r.AssignedWeeklyHours = e.assignedWeeklyHours; } catch { }
            try { r.IsAbsent = e.isAbsent; r.IsReplaced = e.isReplaced; r.IsBeingReplaced = e.isBeingReplaced; } catch { }
            try { r.IsTrainingDay = e.isTrainingDay; r.HasSendQuitWarning = e.hasSendQuitWarning; r.SendRetirementNotice = e.sendRetirementNotice; } catch { }
            try { r.AssignedHrManagerPlanId = e.assignedHrManagerPlanId ?? ""; } catch { }
            try { r.InitialCombinedSkillAmount = e.initialCombinedSkillAmount; r.PresetId = e.presetId ?? ""; } catch { }
            try { var cd = e.characterData; if (cd != null) { r.Name = cd.name ?? "Staff"; r.Gender = (int)cd.gender; r.AgeDays = cd.ageInDays; } } catch { }
            try
            {
                var sk = e.characterData?.skills;
                if (sk != null)
                    foreach (var s in sk)
                        if (s != null && !string.IsNullOrEmpty(s.name))
                            r.Skills.Add(s.name + "=" + s.value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
            try { if (e.demands != null) r.Demands.AddRange(e.demands); } catch { }
            try { if (e.assignedWorkStationItems != null) r.AssignedWorkStationItems.AddRange(e.assignedWorkStationItems); } catch { }
            try { if (e.assignedWeeklyDays != null) foreach (var d in e.assignedWeeklyDays) r.AssignedWeeklyDays.Add((int)d); } catch { }
            try { if (e.trainingSession != null) { r.TrainingSkill = e.trainingSession.skill ?? ""; r.TrainingStartDay = e.trainingSession.startDay; } } catch { }
            try
            {
                var c = e.complaintData;
                if (c != null)
                {
                    r.ComplaintIsComplaining = c.isComplaining;
                    r.ComplaintHoursUntilNext = c.hoursUntilNextComplaint;
                    r.ComplaintDeadlineHours = c.complaintDeadlineHours;
                    r.ComplaintHasRival = c.hasRival;
                }
            }
            catch { }
            return r;
        }

        /// <summary>T1(f): the answer every relayed leg owes the host. Sent even when nothing changed here -
        /// an acknowledgement is what releases the entry the host is holding.</summary>
        private static void AckTransfer(EmployeeEditPayload p, string action)
        {
            Send(new EmployeeEditPayload
            {
                PlayerId = MPConfig.PlayerId, Action = action,
                EmployeeId = p.EmployeeId ?? "", TransferId = p.TransferId ?? "",
                AddressKey = p.AddressKey ?? "", OtherAddressKey = p.OtherAddressKey ?? "",
            });
        }

        /// <summary>A field-for-field copy of a record payload. The host keeps one of these while a
        /// transfer is in the air, and PromoteRecord rewrites Action on whatever it is handed.</summary>
        public static EmployeeEditPayload CloneRecord(EmployeeEditPayload s)
        {
            var r = new EmployeeEditPayload();
            if (s == null) return r;
            try { r = (EmployeeEditPayload)Newtonsoft.Json.JsonConvert.DeserializeObject(Newtonsoft.Json.JsonConvert.SerializeObject(s), typeof(EmployeeEditPayload)); }
            catch { }
            return r ?? new EmployeeEditPayload();
        }

        /// <summary>RIG-13, WIDENED BY RIG-2 (rig run 4): EVERY structure in the save that stores an employee
        /// id as TEXT, cleared BEFORE the record is removed - which is also why the lookups inside it still
        /// resolve. The game resolves most of these through EmployeeHelper.GetEmployeeById with showError
        /// TRUE (decompile Helpers/EmployeeHelper.cs:472-486), so ONE id left behind anywhere prints
        /// `Employee with ID &lt;id&gt; not found` the next time its holder is walked. That is exactly what the
        /// rig saw once on the SOURCE right after a release with no shift to blame (the old
        /// "cleared N leftover shift" line never fired), so the shift sweep alone was not the whole set.
        /// Each structure is counted and logged under its own name (`[Transfer] scrub: &lt;structure&gt; x N`):
        ///   shift            WorkShift.employeeId                   (WorkShift.cs:10; read ScheduleDay.cs:48)
        ///   vehicle-slot     VehicleSlot.employeeDriverId           (Entities/VehicleSlot.cs:11; read :37)
        ///   schedule-cache   ScheduleHelper.WorkShiftsBy*Id         (UI...Schedule/ScheduleHelper.cs:55-57; read :145)
        ///   todo             TodoTask.employeeId                    (Entities/TodoTask.cs:19; read UI.Tasks/TasksUI.cs:591/600)
        ///   hr-plan          HrManagerPlan.assignedEmployeeId + assignedEmployees (HrManagerPlan.cs:14/:18; read :38/:62)
        ///   headhunter-plan  HeadhunterPlan.assignedEmployeeId      (HeadhunterPlan.cs:21; read :49)
        ///   logistics-plan   LogisticsManagerPlan.assignedEmployeeId (LogisticsManagerPlan.cs:34; read :45)
        ///   pricing-plan     PricingManagerPlan.assignedEmployeeId  (PricingManagerPlan.cs:19; read :49)
        ///   import-agent     ImportPartnership.employeeInstanceId   (Entities/ImportPartnership.cs; read :64/:69)
        ///   rival-defense    DefenseState.affectedEmployeeIds       (BigAmbitions.Rivals/DefenseState.cs:21; read RivalsHelper.cs:302)
        /// UnassignEmployeeFromAllWorkshifts only walks the employee's OWN assigned building (decompile
        /// :372-393), so a shift left anywhere else survived it - hence the whole-registration walk here.
        /// The mod's own publish path needs no scrub: the roster build walks gi.EmployeeInstances itself
        /// (MPRegisterSync.cs:1847) and the schedule digest reads shifts, neither resolving an id.</summary>
        /// <summary>r3 MINOR-3: gi's plan lists also hold wave-4 items THIS machine installed - a partner's
        /// tagged DISPLAY copy (CompanyPlans.cs:661-690) and an absent owner's tagged install
        /// (MergerAbsence.cs:796-868). Neither is this save's own plan, so a release must not blank an id
        /// on them. Same tag registry CompanyPlans.Mine&lt;T&gt; reads; IsTaggedInstall covers both tag
        /// shapes at once (a "display:" tag and a real pid), which is exactly the pair to skip.</summary>
        private static bool ForeignPlan(object plan)
        { try { return plan != null && MergerAbsence.IsTaggedInstall(plan); } catch { return false; } }

        private static void ScrubEmployeeReferences(GameInstance gi, EmployeeInstance rel)
        {
            try
            {
                string id = rel?.id ?? "";
                if (id.Length == 0 || gi == null) return;
                void Note(string what, int n)
                { if (n > 0) Plugin.Logger.LogInfo($"[Transfer] scrub: {what} x {n} (leftover reference(s) to '{id}')."); }

                int shifts = 0, slots = 0;
                if (gi.BuildingRegistrations != null)
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg?.scheduleDays != null)
                            foreach (var day in reg.scheduleDays)
                            {
                                if (day?.workShifts == null) continue;
                                bool hit = false;
                                for (int i = day.workShifts.Count - 1; i >= 0; i--)
                                { var ws = day.workShifts[i]; if (ws != null && ws.employeeId == id) { hit = true; break; } }
                                if (!hit) continue;
                                day.RemoveAllWorkShiftsThatMatchPredicate(x => x != null && x.employeeId == id);
                                shifts++;
                            }
                        try
                        {
                            if (reg is Warehouse wh && wh.vehicleSlots != null)
                                foreach (var slot in wh.vehicleSlots)
                                    if (slot != null && slot.employeeDriverId == id) { slot.employeeDriverId = null; slots++; }
                        }
                        catch { }
                    }
                Note("shift", shifts);
                Note("vehicle-slot", slots);
                Note("schedule-cache", ScrubScheduleCaches(id));

                int todos = 0;
                try { if (gi.TodoTasks != null) todos = gi.TodoTasks.RemoveAll(t => t != null && t.employeeId == id); } catch { }
                Note("todo", todos);

                int hr = 0;
                try
                {
                    if (gi.hrManagerPlans != null)
                        foreach (var pl in gi.hrManagerPlans)
                        {
                            if (pl == null || ForeignPlan(pl)) continue;
                            if (pl.assignedEmployeeId == id) { pl.assignedEmployeeId = null; hr++; }
                            if (pl.assignedEmployees != null) hr += pl.assignedEmployees.RemoveAll(x => x == id);
                        }
                }
                catch { }
                Note("hr-plan", hr);

                int hh = 0, lg = 0, pr = 0, imp = 0, rv = 0;
                try { if (gi.headhunterPlans != null) foreach (var pl in gi.headhunterPlans) if (pl != null && !ForeignPlan(pl) && pl.assignedEmployeeId == id) { pl.assignedEmployeeId = null; hh++; } } catch { }
                Note("headhunter-plan", hh);
                try { if (gi.logisticsManagerPlans != null) foreach (var pl in gi.logisticsManagerPlans) if (pl != null && !ForeignPlan(pl) && pl.assignedEmployeeId == id) { pl.assignedEmployeeId = null; lg++; } } catch { }
                Note("logistics-plan", lg);
                try { if (gi.pricingManagerPlans != null) foreach (var pl in gi.pricingManagerPlans) if (pl != null && !ForeignPlan(pl) && pl.assignedEmployeeId == id) { pl.assignedEmployeeId = null; pr++; } } catch { }
                Note("pricing-plan", pr);
                try { if (gi.importPartnerships != null) foreach (var ip in gi.importPartnerships) if (ip != null && !ForeignPlan(ip) && ip.employeeInstanceId == id) { ip.employeeInstanceId = null; imp++; } } catch { }
                Note("import-agent", imp);
                try
                {
                    if (gi.specialRivalStates != null)
                        foreach (var rs in gi.specialRivalStates)
                        {
                            if (rs?.defenseStates == null) continue;
                            foreach (var ds in rs.defenseStates)
                                if (ds?.affectedEmployeeIds != null) rv += ds.affectedEmployeeIds.RemoveAll(x => x == id);
                        }
                }
                catch { }
                Note("rival-defense", rv);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] scrub: {ex.Message}"); }
        }

        /// <summary>RIG-2: the SCHEDULE screen keeps two STATIC caches of WorkShift objects, by workstation
        /// and by employee (decompile UI.Smartphone.Apps.BizMan.Schedule/ScheduleHelper.cs:55-57 - both
        /// private properties). Taking the shift out of its ScheduleDay does not touch them, and
        /// GetWorkShiftsByEmployeeId (:145) hands them straight back out, so they are cleared by reflection
        /// here. Returns how many entries went.</summary>
        private static int ScrubScheduleCaches(string id)
        {
            int n = 0;
            try
            {
                var t = typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper);
                foreach (var name in new[] { "WorkShiftsByEmployeeId", "WorkShiftsByWorkstationId" })
                {
                    var pi = HarmonyLib.AccessTools.Property(t, name);
                    var dict = pi?.GetValue(null) as System.Collections.IDictionary;
                    if (dict == null) continue;
                    if (name == "WorkShiftsByEmployeeId")
                    { if (dict.Contains(id)) { dict.Remove(id); n++; } continue; }
                    var keys = new List<object>();
                    foreach (var k in dict.Keys) keys.Add(k);
                    foreach (var k in keys)
                        if (dict[k] is List<WorkShift> lst) n += lst.RemoveAll(x => x != null && x.employeeId == id);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] schedule-cache scrub: {ex.Message}"); }
            return n;
        }

        private static BuildingRegistration FindRegByKey(GameInstance gi, string addressKey)
        {
            try
            {
                if (gi?.BuildingRegistrations == null || string.IsNullOrEmpty(addressKey)) return null;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == addressKey) return r;
            }
            catch { }
            return null;
        }

        /// <summary>MPRegisterSync roster apply calls this when an incoming roster carries an id that also
        /// exists as a LOCAL record. RIG-12 / T1(g) r2: it must now answer FALSE and remove nothing. The
        /// adopt-OUT path it used to confirm is retired - my own employee sent to a partner's shop goes
        /// through the host-held transfer, which removes the local original at the RELEASE, before anybody
        /// adopts. Removing a record here as well would take an employee out of this save while the host
        /// still had the move in flight, which is exactly the loss T1 exists to prevent. The pending mark
        /// is still cleared: it is the 30 s in-flight guard the scan sets before asking.</summary>
        public static bool ConfirmAdopt(string employeeId)
        {
            if (!_pendingAdopt.Remove(employeeId)) return false;
            Plugin.Logger.LogInfo($"[MergerStaff] the owner's roster now carries '{employeeId}' - the local original is NOT touched here (the transfer's release already did that).");
            return false;
        }

        private static Address AddressOfKey(GameInstance gi, string addressKey)
        {
            foreach (var r in gi.BuildingRegistrations)
                if (r != null && GameStateReader.AddressKey(r) == addressKey) return new Address(r.StreetName, r.StreetNumber);
            return null;
        }

        // ── Wire ──────────────────────────────────────────────────────────────

        private static void Send(EmployeeEditPayload p)
        {
            if (MPServer.IsRunning) MPServer.HostRouteEmployeeEdit(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.MergerEmployeeEdit, MPConfig.PlayerId, p));
        }

        /// <summary>CROSS-HR-2 T1: the plan RUNNER's training leg onto the same routed carrier. Public only
        /// because the patch that measures native's writes lives in MPPatches; the wire is unchanged.</summary>
        public static void SendHrTrain(EmployeeEditPayload p) { if (p != null) Send(p); }

        /// <summary>CROSS-HR-3 A2: the plan RUNNER's TAG leg, the same carrier and the same host route as the
        /// training leg (MPServer.HostRouteHrTrain takes both actions).  Public for CompanyPlans, which is
        /// where the HR appliers live; the wire is unchanged.</summary>
        public static void SendHrTag(EmployeeEditPayload p) { if (p != null) Send(p); }

        /// <summary>CROSS-HR-3b B2: the OWNER's ANSWER to a tag it could not write, on the same carrier and the
        /// same host route (Action "hrtag-refused"; `OwnerPid` addresses the plan's RUNNER - the original sender -
        /// and `Name`, the carrier's only free label field, carries the short reason).  Without it the runner
        /// keeps a list entry standing on a tag that never landed and nothing ever reconciles it.  A CLEAR is
        /// never answered this way: an empty plan id added nothing there to take back.</summary>
        private static void RefuseHrTag(EmployeeEditPayload p, string want, string why)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.PlayerId) || string.IsNullOrEmpty(want)) return;
                Send(new EmployeeEditPayload
                {
                    PlayerId   = MPConfig.PlayerId,
                    Action     = "hrtag-refused",
                    EmployeeId = p.EmployeeId ?? "",
                    OwnerPid   = p.PlayerId,
                    AssignedHrManagerPlanId = want,
                    Name       = why ?? "",
                });
                Plugin.Logger.LogInfo($"[CrossHR] hrtag REFUSAL answered to '{p.PlayerId}' for employee '{p.EmployeeId}' (plan '{want}', {why}) - the runner takes its list entry back.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] hrtag refusal answer for '{p.EmployeeId}': {ex.Message}"); }
        }

        /// <summary>CROSS-HR-2 T3: the stamps ("planId|day|employeeId") this machine has already mirrored onto
        /// its real records. Per session (Reset clears it): a resend, or a daily pass that ran twice, changes
        /// nothing the second time.</summary>
        private static readonly HashSet<string> _hrTrainApplied = new();

        /// <summary>CROSS-HR-2b: what the LAST daily training pass on this machine did (the `hrtrain` rig lever reads it back;
        /// nothing else does). Set by the TrainEmployees postfix, also when it sent nothing.</summary>
        public static string LastTrainPlan = ""; public static int LastTrainInjected, LastTrainLegs, LastTrainUnchanged;
        public static void NoteTrainPass(string planId, int injected, int legs, int unchanged)
        { LastTrainPlan = planId ?? ""; LastTrainInjected = injected; LastTrainLegs = legs; LastTrainUnchanged = unchanged; }

        /// <summary>THE OWNER's machine: apply a routed employee op natively. MAIN THREAD.</summary>
        public static void ApplyOnOwner(EmployeeEditPayload p)
        {
            try
            {
                if (p == null) return;
                // T1: a RELEASE of a record this save holds outright (my own employee, or one off the
                // bench) names no from-address - every other op is still address-gated.
                // "drop" is U2's late-adopt undo; "adopt" appears here because PromoteRecord rewrites Action
                // to it, and U1's give-back to the BENCH legitimately carries no address at all.
                bool transferLeg = !string.IsNullOrEmpty(p.TransferId)
                                && (p.Action == "release" || p.Action == "adopt-in" || p.Action == "return"
                                 || p.Action == "drop"    || p.Action == "adopt"
                                 || p.Action == "transfer-refused");   // r4 MINOR-3: the host's word to the initiator carries no address
                // CROSS-HR-3 A2: a TAG leg is no more an address edit than a training leg is - a benched real
                // record has no address at all, and the tag is still the owner's to write.  CROSS-HR-3b B2: nor
                // is the ANSWER to a refused tag - that one is addressed by PID, back to the plan's runner.
                if (string.IsNullOrEmpty(p.AddressKey) && !transferLeg && p.Action != "hrtrain" && p.Action != "hrtag"
                 && p.Action != "hrtag-refused") return;
                if (p.Action == "hrtrain")
                {
                    // CROSS-HR-2 T3, THE OWNER APPLIES ONCE. The leg carries what the partner's plan just did
                    // to its COPY of this record; here it is mirrored onto the REAL one and republished, so
                    // the runner's copy follows on the next roster push. Nothing about money is in the leg.
                    string stamp = p.Stamp ?? "";
                    if (stamp.Length == 0)
                    { Plugin.Logger.LogWarning($"[CrossHR] routed hrtrain for '{p.EmployeeId}' carries no stamp - dropped (it could not be applied once)."); return; }
                    EmployeeInstance? emp = null;
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(p.EmployeeId ?? "", out emp); } catch { }
                    if (emp == null)
                    { Plugin.Logger.LogWarning($"[CrossHR] routed hrtrain: employee '{p.EmployeeId}' is not on this machine - dropped."); return; }
                    if (MPRegisterSync.IsInjectedStaff(emp.id))
                    { Plugin.Logger.LogWarning($"[CrossHR] routed hrtrain: '{emp.id}' is an INJECTED copy here, not the real record - dropped (mis-route)."); return; }
                    if (!_hrTrainApplied.Add(stamp))
                    { Plugin.Logger.LogInfo($"[CrossHR] routed hrtrain '{stamp}' already applied - dropped (the day's training happens once)."); return; }
                    float wageWas = emp.hourlyWage;   // for the log line
                    int raised = 0;
                    if (p.SkillDeltas != null)
                        foreach (var pair in p.SkillDeltas)
                        {
                            if (string.IsNullOrEmpty(pair)) continue;
                            int eq = pair.IndexOf('=');
                            if (eq <= 0) continue;
                            string nm = pair.Substring(0, eq);
                            string dv = pair.Substring(eq + 1).TrimStart('+');
                            if (!float.TryParse(dv, System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out float d) || d <= 0f) continue;
                            var skills = emp.characterData?.skills;
                            if (skills == null) continue;
                            foreach (var sk in skills)
                                if (sk != null && string.Equals(sk.name, nm, StringComparison.Ordinal))
                                {
                                    // Native's clamp ran on the RUNNER against the COPY's value (HrManagerPlan.cs:84
                                    // Mathf.Min(trainingTarget - value, 2f), :92 for the secondary skills) and that copy
                                    // can be stale-low. r1 MINOR-2: re-clamp here against the REAL value and the plan's
                                    // trainingTarget carried on the leg - a skill already at or past the target does not
                                    // move (native's own num3 <= 0 would have skipped it there), and nothing passes 100.
                                    float cap = p.TrainingTarget > 0 ? Mathf.Min((float)p.TrainingTarget, 100f) : 100f;
                                    float ceiling = Mathf.Max(sk.value, cap);   // already past the target: stays put
                                    float next = Mathf.Min(sk.value + d, ceiling);
                                    if (next > sk.value)
                                    {
                                        float previous = sk.value;
                                        sk.value = next; raised++;
                                        // r2 MINOR-2: the wage by the game's OWN formula on the REAL record (EmployeeInstance.cs:293-301:
                                        // hourlyWage *= f^p from this skill's old and new values) - exactly what native did on the copy,
                                        // computed where the real values are, and needing nothing from the wire (a copy whose wage was 0
                                        // carried no usable ratio). Native's own guard: no move when the value did not rise.
                                        try { emp.IncreaseWageFromTraining(sk, previous); } catch { }
                                    }
                                    break;
                                }
                        }
                    // The wage moved inside the loop, by the game's own IncreaseWageFromTraining per raised skill; the leg's
                    // WageRatio and WageDelta are what the COPY did on the runner, logged beside what the real record did.
                    Plugin.Logger.LogInfo($"[CrossHR] applied routed training to '{emp.id}' ({raised} skill(s), wage {wageWas.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} -> {emp.hourlyWage.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}; the copy moved x{p.WageRatio.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}) for partner plan '{p.AssignedHrManagerPlanId}' (stamp '{stamp}', from '{p.PlayerId}').");
                    // The REAL record's shop republishes so the runner's copy catches up (the leg's address is the copy's as the
                    // runner saw it, and a bench copy has none). A BENCHED real record is not in the roster walk (MPRegisterSync
                    // publishes per registration): it travels on the BENCH publish instead, and CROSS-HR-3 A6 makes that publish
                    // follow a trained skill - the bench signature now carries each skill (SharedShopStaff.cs, id|name|wage|skills)
                    // and NudgeBenchPublish zeroes the owner tick, so a skills-only move (the wage unchanged at two decimals) no
                    // longer leaves the runner's bench copy stale until some unrelated change happens to move it.
                    string realAddr = ""; try { realAddr = GameStateReader.AddressKey(emp.assignedAddress) ?? ""; } catch { }
                    if (realAddr.Length > 0) MPRegisterSync.ForceRosterRepublish(realAddr);
                    else SharedShopStaff.NudgeBenchPublish();
                }
                else if (p.Action == "hrtag")
                {
                    // CROSS-HR-3 A2, THE TAG BELONGS ON THE REAL RECORD.  A co-member's HR plan took one of this
                    // save's people onto its list; that plan runs THERE, but native reads assignedHrManagerPlanId
                    // HERE (EmployeeInstance.OnRemove, the HR panes, the daily pass), so the id has to be written
                    // here AND resolve here - which it does only through the SHADOW of that plan, installed for
                    // every partner feed by CROSS-HR-1 (PaperworkSync.cs:956/:973). No shadow, no write: a tag
                    // naming nothing would be persisted into this save's .hsg. An empty plan id CLEARS.
                    string want = p.AssignedHrManagerPlanId ?? "";
                    EmployeeInstance? emp = null;
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(p.EmployeeId ?? "", out emp); } catch { }
                    if (emp == null)
                    {
                        // CROSS-HR-2 T3b: a CLEAR for a record that has LEFT this save is correct, not a mis-route -
                        // the runner is answering the unassign this very machine sent when the worker went. One info
                        // line, no warning.
                        if (want.Length == 0)
                            Plugin.Logger.LogInfo($"[CrossHR] routed hrtag CLEAR for employee '{p.EmployeeId}' (plan '-'): that record has left this machine - nothing to clear (from '{p.PlayerId}').");
                        else
                        {
                            Plugin.Logger.LogWarning($"[CrossHR] routed hrtag for employee '{p.EmployeeId}' (plan '{want}'): that record is not on this machine - dropped.");
                            RefuseHrTag(p, want, "that record is not on this machine");   // CROSS-HR-3b B2
                        }
                        return;
                    }
                    if (MPRegisterSync.IsInjectedStaff(emp.id))
                    { Plugin.Logger.LogWarning($"[CrossHR] routed hrtag for employee '{emp.id}' (plan '{want}'): '{emp.id}' is an INJECTED copy here, not the real record - dropped (mis-route)."); return; }
                    if (want.Length > 0)
                    {
                        bool resolves = false;
                        try { resolves = Buildings.Office.Headquarters.HrManagerHelper.GetPlanFromId(want) != null; } catch { }
                        if (!resolves)
                        {
                            Plugin.Logger.LogWarning($"[CrossHR] routed hrtag for employee '{emp.id}' REFUSED: HR plan '{want}' does not resolve on this machine (no shadow of it here) - the tag would name nothing. From '{p.PlayerId}'.");
                            RefuseHrTag(p, want, "that HR plan does not resolve on this machine");   // CROSS-HR-3b B2
                            return;
                        }
                    }
                    string had = emp.assignedHrManagerPlanId ?? "";
                    if (had == want)
                    { Plugin.Logger.LogInfo($"[CrossHR] routed hrtag for employee '{emp.id}' (plan '{(want.Length > 0 ? want : "-")}') already stood here - no-op (from '{p.PlayerId}')."); return; }
                    emp.assignedHrManagerPlanId = want.Length > 0 ? want : null;
                    Plugin.Logger.LogInfo($"[CrossHR] {(want.Length > 0 ? "applied" : "cleared")} routed hrtag on employee '{emp.id}': plan '{(had.Length > 0 ? had : "-")}' -> '{(want.Length > 0 ? want : "-")}' (from '{p.PlayerId}').");
                    // CROSS-HR-3b B2: the republish below is about the REST of the record.  The roster does NOT
                    // carry assignedHrManagerPlanId (MPRegisterSync.StaffInfoOf writes id/name/gender/available/
                    // wage/satisfaction/age/skills), so the runner's copy keeps the tag its own plan gave it -
                    // which is the tag that has just landed here.  A tag that does NOT land is answered instead.
                    string tagAddr = ""; try { tagAddr = GameStateReader.AddressKey(emp.assignedAddress) ?? ""; } catch { }
                    if (tagAddr.Length > 0) MPRegisterSync.ForceRosterRepublish(tagAddr);
                    else SharedShopStaff.NudgeBenchPublish();
                }
                else if (p.Action == "hrtag-refused")
                {
                    // CROSS-HR-3b B2, THE RUNNER TAKES THE ENTRY BACK.  The list entry this machine made stands on
                    // a tag that never landed: the owner could not write it (no record there, or this plan does not
                    // resolve there), or the host had nobody to deliver it to.  Undo the pair the applier or the
                    // pane wrote here.  Only a REAL plan of this machine's own is touched - a shadow or a display
                    // install is a partner's row drawn from their feed, and no list of ours to edit.
                    string undoPlan = p.AssignedHrManagerPlanId ?? "";
                    string undoEid  = p.EmployeeId ?? "";
                    string why      = string.IsNullOrEmpty(p.Name) ? "no reason given" : p.Name;
                    if (undoPlan.Length == 0 || undoEid.Length == 0)
                    { Plugin.Logger.LogWarning($"[CrossHR] hrtag-refused from '{p.PlayerId}' names no employee or no plan - dropped."); return; }
                    Buildings.Office.Headquarters.HrManagerPlan? undo = null;
                    try { undo = Buildings.Office.Headquarters.HrManagerHelper.GetPlanFromId(undoPlan); } catch { }
                    bool displayInstall = false;
                    try { displayInstall = undo != null && (MergerAbsence.IsDisplayInstall(undo) || CompanyPlans.IsOverlayPlan(undo)); } catch { }   // r2 MINOR-4: nor a drawn partner row
                    if (undo == null || displayInstall) return;   // not one of this machine's own plans: nothing of ours
                    var undoList = undo!.assignedEmployees;   // (the null test above sits past a try/catch, which the nullable flow does not carry - CS8602 otherwise)
                    bool wasListed = undoList != null && undoList.Contains(undoEid);
                    if (wasListed) undoList!.Remove(undoEid);
                    EmployeeInstance? copy = null;
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(undoEid, out copy); } catch { }
                    bool wasTagged = false;
                    if (copy != null && copy.assignedHrManagerPlanId == undoPlan) { copy.assignedHrManagerPlanId = null; wasTagged = true; }
                    if (!wasListed && !wasTagged) return;         // idempotent: a second answer says nothing, changes nothing
                    Plugin.Logger.LogInfo($"[CrossHR] hr assign of '{undoEid}' on plan '{undoPlan}' UNDONE - the owner '{p.PlayerId}' refused the tag ({why}).");
                    try { MPPatches.MergerHrPaneRedrawIfOn(undoPlan); } catch { }
                }
                else if (p.Action == "fire")
                {
                    EmployeeInstance emp = null;
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(p.EmployeeId ?? "", out emp); } catch { }
                    if (emp == null) { Plugin.Logger.LogWarning($"[MergerStaff] routed fire: employee '{p.EmployeeId}' not found (already gone?)."); return; }
                    if (MPRegisterSync.IsInjectedStaff(emp.id)) return;   // never fire a record WE injected (mis-route)
                    Plugin.Logger.LogInfo($"[MergerStaff] applying routed FIRE '{emp.id}' @ '{p.AddressKey}' (by '{p.PlayerId}').");
                    emp.RemoveEmployee();                                  // full native semantics
                    MPRegisterSync.ForceRosterRepublish(p.AddressKey);     // confirmation reaches every member fast
                }
                else if (p.Action == "schedule")
                {
                    Plugin.Logger.LogWarning($"[MergerStaff] routed SCHEDULE from '{p.PlayerId}' ignored — retired 2026-09-10 (merged schedules travel the validated SharedShopSchedule path).");
                    return;
                }
                else if (p.Action == "release")
                {
                    // T1(b), on the SOURCE runner, asked by the HOST: let go of MY record so exactly one
                    // machine holds it - and the holder for the next moment is the host itself.
                    // MAJOR-1 r2: the dedupe is per (TransferId, STAGE), never per id. A release already
                    // done answers "already released" and removes nothing a second time; the give-back
                    // legs below carry their own stage and can never be swallowed by it.
                    if (!string.IsNullOrEmpty(p.TransferId) && _transfersDone.Contains(p.TransferId + "|release"))
                    { Plugin.Logger.LogInfo($"[Transfer] {p.TransferId}: release of '{p.EmployeeId}' - already released from here; the host holds the record."); return; }
                    EmployeeInstance rel = null;
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(p.EmployeeId ?? "", out rel); } catch { }
                    if (rel == null) { Plugin.Logger.LogWarning($"[Transfer] {p.TransferId}: release of '{p.EmployeeId}' - not my employee (already gone?), nothing sent."); RefuseRelease(p); return; }
                    if (MPRegisterSync.IsInjectedStaff(rel.id)) { Plugin.Logger.LogWarning($"[Transfer] {p.TransferId}: release of '{p.EmployeeId}' - that is a copy here, not my record; refused."); RefuseRelease(p); return; }
                    string relAt = ""; try { relAt = rel.assignedAddress != null ? GameStateReader.AddressKey(rel.assignedAddress) : ""; } catch { }
                    // An EMPTY from-address is the host saying "you hold the record, wherever they stand":
                    // my own employee moved to a partner's shop, or one off the bench. A NAMED one is the
                    // owner-wins check - the copy that asked may be stale.
                    // RIG-1: on the REQUESTER's own machine the dropdown has already written assignedAddress,
                    // so the record may legitimately stand at the DESTINATION end of this very move. Either
                    // end is fine; anywhere else means the copy that asked was stale and the owner wins.
                    bool atFromEnd = string.IsNullOrEmpty(p.AddressKey) || string.Equals(relAt, p.AddressKey, StringComparison.OrdinalIgnoreCase);
                    bool atToEnd   = !string.IsNullOrEmpty(p.OtherAddressKey) && string.Equals(relAt, p.OtherAddressKey, StringComparison.OrdinalIgnoreCase);
                    if (!atFromEnd && !atToEnd)
                    { Plugin.Logger.LogWarning($"[Transfer] {p.TransferId}: release of '{p.EmployeeId}' - they are at '{relAt}', not '{p.AddressKey}'; refused (owner wins)."); MPRegisterSync.ForceRosterRepublish(relAt); RefuseRelease(p); return; }
                    if (string.IsNullOrEmpty(p.OtherAddressKey))
                    { Plugin.Logger.LogWarning($"[Transfer] {p.TransferId}: release of '{p.EmployeeId}' with no destination - refused."); RefuseRelease(p); return; }

                    var handover = RecordOf(rel, p.OtherAddressKey, "released");
                    handover.TransferId = p.TransferId;
                    handover.OtherAddressKey = p.AddressKey ?? "";

                    var relGi = SaveGameManager.Current;
                    try { UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule.AbortAutoFillForBusiness(Helpers.BuildingHelper.GetBuildingRegistration(rel.assignedAddress)); } catch { }
                    try { Helpers.EmployeeHelper.UnassignEmployeeFromAllWorkshifts(rel); } catch (Exception uex) { Plugin.Logger.LogWarning($"[Transfer] release unassign shifts: {uex.Message}"); }
                    ScrubEmployeeReferences(relGi, rel);   // RIG-13 + RIG-2: every structure naming this id, before the record goes
                    try { if (relGi?.EmployeeInstances != null) relGi.EmployeeInstances.Remove(rel); } catch { }
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(rel.id); } catch { }
                    try { SaveGameManager.MarkChange(); } catch { }
                    if (!string.IsNullOrEmpty(p.TransferId)) _transfersDone.Add(p.TransferId + "|release");
                    Plugin.Logger.LogInfo($"[Transfer] {p.TransferId}: released to the host - '{handover.Name}' ({rel.id}) left '{p.AddressKey}' for '{p.OtherAddressKey}'.");
                    MPRegisterSync.ForceRosterRepublish(string.IsNullOrEmpty(p.AddressKey) ? relAt : p.AddressKey);
                    Send(handover);
                }
                else if (p.Action == "adopt-in" || p.Action == "return")
                {
                    // T1(c)/(d), relayed BY THE HOST: "adopt-in" on the destination runner, "return" back on
                    // the source when the destination refused, has gone, or never answered within a game
                    // hour. Both rebuild the real record through the SAME adopt reconstruction and then
                    // ACKNOWLEDGE to the host, which is what closes the entry it is holding.
                    // MAJOR-1 r2: the stage is part of the dedupe key, so a give-back can never be
                    // swallowed by the release that preceded it. A repeat acks again and adopts nothing.
                    bool back = p.Action == "return";
                    string stageKey = (p.TransferId ?? "") + (back ? "|return" : "|adopt");
                    if (!string.IsNullOrEmpty(p.TransferId) && _transfersDone.Contains(stageKey))
                    { AckTransfer(p, back ? "returned" : "adopted"); MPRegisterSync.ForceRosterRepublish(p.AddressKey); return; }
                    if (!back)
                    {
                        var dstReg = FindRegByKey(SaveGameManager.Current, p.AddressKey);
                        bool runsHere = dstReg != null && (MergerFlip.TrulyMine(dstReg) || MergerAbsence.SimulatesHere(p.AddressKey));
                        if (!runsHere)
                        {
                            Plugin.Logger.LogWarning($"[Transfer] {p.TransferId}: refused: cannot take '{p.EmployeeId}' at '{p.AddressKey}' (not mine and not simulated here) - the host takes the record back.");
                            AckTransfer(p, "transfer-refused");
                            return;
                        }
                    }
                    var rebuilt = CloneRecord(p);                      // PromoteRecord rewrites Action; the ack needs the original
                    if (!PromoteRecord(rebuilt))
                    {
                        if (back)
                        {
                            // U1: the host NEVER drops a record it is holding, so a failed give-back is
                            // re-offered on its hourly tick - the log is throttled to once per GAME hour so a
                            // stuck give-back cannot flood the field log.
                            int gd = 0, gh = 0;
                            try { gd = SaveGameManager.Current?.Day ?? 0; gh = SaveGameManager.Current?.Hour ?? 0; } catch { }
                            string tk = p.TransferId ?? "";
                            if (!_returnFailLog.TryGetValue(tk, out var lastLog) || lastLog.day != gd || lastLog.hour != gh)
                            {
                                _returnFailLog[tk] = (gd, gh);
                                Plugin.Logger.LogWarning($"[Transfer] {p.TransferId}: re-adopting my own '{p.EmployeeId}' FAILED - the host still holds the record and will offer it again.");
                            }
                            return;
                        }
                        Plugin.Logger.LogWarning($"[Transfer] {p.TransferId}: refused: adopting '{p.EmployeeId}' at '{p.AddressKey}' FAILED - the host takes the record back.");
                        AckTransfer(p, "transfer-refused");
                        return;
                    }
                    if (!string.IsNullOrEmpty(p.TransferId)) _transfersDone.Add(stageKey);
                    Plugin.Logger.LogInfo($"[Transfer] {p.TransferId}: {(back ? "TOOK BACK" : "ADOPTED")} '{p.Name}' ({p.EmployeeId}) at '{p.AddressKey}' - one save holds the record again.");
                    MPRegisterSync.ForceRosterRepublish(p.AddressKey);
                    AckTransfer(p, back ? "returned" : "adopted");
                }
                else if (p.Action == "transfer-refused")
                {
                    // r4 MINOR-3, relayed BY THE HOST to the INITIATOR: the source would not let go, so the
                    // move is off before anything was released. The only thing to undo is this machine's own
                    // optimistic dropdown write - which used to sit until the 30 s give-back noticed.
                    CancelPendingTransfer(p.EmployeeId, $"the host refused the move ({p.TransferId})");
                }
                else if (p.Action == "drop")
                {
                    // U2 (re-check r3 MAJOR-2), relayed BY THE HOST: my "adopted" acknowledgement arrived
                    // after the host had already handed the record back to the source, so this save and the
                    // source's both hold a live record with the same id - and payroll is id-keyed, so it
                    // would be paid twice. This leg removes what was adopted under that transfer id and
                    // acknowledges "dropped". Idempotent by (TransferId, stage) like every other leg.
                    string dropKey = (p.TransferId ?? "") + "|drop";
                    if (!string.IsNullOrEmpty(p.TransferId) && _transfersDone.Contains(dropKey))
                    { AckTransfer(p, "dropped"); return; }
                    EmployeeInstance? dr = null;
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(p.EmployeeId ?? "", out dr); } catch { }
                    var dgi = SaveGameManager.Current;
                    if (dr == null && dgi?.EmployeeInstances != null)
                        foreach (var x in dgi.EmployeeInstances) if (x != null && x.id == p.EmployeeId) { dr = x; break; }
                    if (dr != null)
                    {
                        try { Helpers.EmployeeHelper.UnassignEmployeeFromAllWorkshifts(dr); } catch { }
                        ScrubEmployeeReferences(dgi, dr);
                        try { if (dgi?.EmployeeInstances != null) dgi.EmployeeInstances.Remove(dr); } catch { }
                        try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(dr.id); } catch { }
                        try { SaveGameManager.MarkChange(); } catch { }
                        MPRegisterSync.ForceRosterRepublish(p.AddressKey);
                    }
                    if (!string.IsNullOrEmpty(p.TransferId)) _transfersDone.Add(dropKey);
                    Plugin.Logger.LogWarning($"[Transfer] {p.TransferId}: dropped - the host had already returned the record.");
                    AckTransfer(p, "dropped");
                }
                else if (p.Action == "adopt")
                {
                    // A member staffed MY shop with THEIR employee — the record migrates into MY save.
                    if (MPRegisterSync.IsSyntheticDuty(p.EmployeeId ?? ""))   // H-ADOPT-1: a stand-in is never anyone's staff (belt and braces to the scan's skip)
                    {
                        if (_refusedSynthetic.Add(p.EmployeeId ?? ""))   // once per id (review 2026-09-10 #4: a pre-fix member retries every 30 s)
                            Plugin.Logger.LogWarning($"[MergerStaff] routed adopt of a synthetic stand-in '{p.EmployeeId}' @ '{p.AddressKey}' from '{p.PlayerId}' — refused (further refusals of this id are silent).");
                        return;
                    }
                    bool exists = false;
                    try { exists = Helpers.EmployeeHelper.EmployeeInstancesDictionary.ContainsKey(p.EmployeeId ?? ""); } catch { }
                    if (exists && MPRegisterSync.IsInjectedStaff(p.EmployeeId ?? ""))
                    {
                        // H-ADOPT-2 (harness run T-P0-5, 2026-09-10): the record already here is the member's INJECTED COPY from
                        // their roster publish (injected records keep the real id) - not a completed adoption. The old
                        // 'idempotent' return below mistook it for one and the transfer silently never happened. Forget the
                        // copy and reconstruct the real record below (which also becomes payroll-real: it leaves the registry).
                        MPRegisterSync.ForgetInjectedForAdopt(p.EmployeeId ?? "");
                        Plugin.Logger.LogInfo($"[MergerStaff] adopt of '{p.EmployeeId}' @ '{p.AddressKey}' replaces this machine's injected copy (H-ADOPT-2).");
                        exists = false;
                    }
                    if (exists) { MPRegisterSync.ForceRosterRepublish(p.AddressKey); return; }   // idempotent (retry after a lost confirm)
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null || gi.EmployeeInstances == null) return;
                    // U1 (re-check r3 MAJOR-1): an EMPTY address means "re-adopt to the BENCH". A give-back
                    // for somebody who was genuinely unassigned before the move names no source building, and
                    // that re-adopt must still succeed - a record the host is holding can never be refused
                    // its way home, or it exists in no save at all.
                    bool toBench = string.IsNullOrEmpty(p.AddressKey);
                    BuildingRegistration target = null;
                    if (!toBench)
                    {
                        foreach (var reg in gi.BuildingRegistrations)
                            if (reg != null && GameStateReader.AddressKey(reg) == p.AddressKey) { target = reg; break; }
                        // P3-B (B3c): a shop this machine SIMULATES for an absent owner is ours to staff for
                        // the duration — the promotion is the same reconstruction, on the same real ids.
                        if (target == null || (!MergerFlip.TrulyMine(target) && !MergerAbsence.SimulatesHere(p.AddressKey)))
                        { Plugin.Logger.LogWarning($"[MergerStaff] routed adopt for '{p.AddressKey}' — neither truly mine nor simulated here, dropped."); return; }
                    }

                    // Reconstruct by primary skill - ALL FOUR of GenerateCandidate's subclass cases
                    // (decompile Helpers/RecruitmentHelper.cs:44-49). A manager must keep its class or
                    // its plans break: r3 MAJOR-1, a PricingManager rebuilt as a bare EmployeeInstance
                    // never runs UnAssignWork(), so PricingManagerPlan keeps re-pricing at skill 0.
                    string primary = "";
                    if (p.Skills != null && p.Skills.Count > 0)
                    { int eq = p.Skills[0].IndexOf('='); primary = eq > 0 ? p.Skills[0].Substring(0, eq) : p.Skills[0]; }
                    EmployeeInstance inst = primary switch
                    {
                        "ba:skill_logisticsmanager" => new LogisticsManager(),
                        "ba:skill_hrmanager"        => new HRManager(),
                        "ba:skill_headhunter"       => new Headhunter(),
                        "ba:skill_pricingmanager"   => new PricingManager(),
                        _                           => new EmployeeInstance(),
                    };
                    inst.Initialize();
                    inst.id = p.EmployeeId;   // KEEP the id — shifts the member scheduled + the adopt-confirm match on it
                    inst.hourlyWage = p.Wage;
                    inst.satisfaction = p.Satisfaction > 0f ? p.Satisfaction : 100f;
                    inst.assignedAddress = toBench ? null : new Address(target.StreetName, target.StreetNumber);
                    try { inst.characterData.name = string.IsNullOrEmpty(p.Name) ? "Staff" : p.Name; } catch { }
                    try { if (p.Gender >= 0) inst.characterData.gender = (BigAmbitions.Characters.Gender)p.Gender; } catch { }
                    try { inst.characterData.ageInDays = p.AgeDays > 0 ? p.AgeDays : Helpers.RecruitmentHelper.GetRandomEmployeeAgeInDays(); } catch { }
                    try
                    {
                        var skills = inst.characterData.skills;
                        skills.Clear();
                        foreach (var pair in p.Skills ?? new List<string>())
                        {
                            int eq = pair.IndexOf('=');
                            if (eq <= 0) continue;
                            float.TryParse(pair.Substring(eq + 1), System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out var val);
                            skills.Add(new BigAmbitions.Characters.Skills.Skill { name = pair.Substring(0, eq), value = val });
                        }
                    }
                    catch { }
                    try { if (p.Demands != null) { inst.demands.Clear(); inst.demands.AddRange(p.Demands); } } catch { }
                    for (int i = gi.EmployeeInstances.Count - 1; i >= 0; i--)
                        if (gi.EmployeeInstances[i]?.id == inst.id) gi.EmployeeInstances.RemoveAt(i);
                    // H-EMP-2 (review r7 #3): the game's hire step stamps dayHired and rolls the first sick day; a fresh record left at 0
                    // is marked absent on the first daily pass (nextSickDay <= Day). Rolled after satisfaction is set (the roll reads it).
                    try { inst.dayHired = SaveGameManager.Current.Day; inst.nextSickDay = Helpers.EmployeeHelper.GetNextSickDay(inst); inst.complaintData?.ResetHoursUntilNextComplaint(); }   // review r8 #1: the game's hire step also grants the complaint grace
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerStaff] H-EMP-2 stamp failed for '{inst.id}': {ex.Message}"); }
                    gi.EmployeeInstances.Add(inst);
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[inst.id] = inst; } catch { }
                    Plugin.Logger.LogInfo($"[MergerStaff] ADOPTED '{inst.characterData?.name}' ({inst.id}) into '{(toBench ? "the bench" : p.AddressKey)}' at ${p.Wage:F0}/h (from '{p.PlayerId}').");
                    MPRegisterSync.ForceRosterRepublish(p.AddressKey);   // the member's confirm signal (a no-op for the bench)
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerStaff] ApplyOnOwner: {ex.Message}"); }
        }

        /// <summary>MERGER PHASE 3-B (B3c): promote an ABSENT owner's employee record — carried in the
        /// P3-A paperwork bundle as Action="record" — into a full local EmployeeInstance on the machine
        /// that simulates that shop, through the SAME adopt reconstruction the merger staff transfer
        /// uses. That path keeps the REAL id and forgets this machine's injected copy, which is exactly
        /// what the three employee-pass strips and the wage skip consult (they exclude by injected-ness)
        /// — so the promotion alone makes the record work, get paid, get sick and train here. The
        /// extended P3-A fields the adopt path never needed are stamped afterwards, so the record
        /// RESUMES its life instead of starting as a fresh hire. Idempotent: a re-send repeats it.</summary>
        public static bool PromoteRecord(EmployeeEditPayload rec)
        {
            // U1: an EMPTY AddressKey is legitimate on the TRANSFER give-back path only ("back to the
            // bench"); every other caller (the P3-B promotion, the P3-C returned record) always names one.
            if (rec == null || string.IsNullOrEmpty(rec.EmployeeId)) return false;
            if (string.IsNullOrEmpty(rec.AddressKey) && string.IsNullOrEmpty(rec.TransferId)) return false;
            string was = rec.Action;
            try { rec.Action = "adopt"; ApplyOnOwner(rec); }
            finally { rec.Action = was; }

            EmployeeInstance? inst = null;
            try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(rec.EmployeeId, out inst); } catch { }
            if (inst == null) return false;
            StampExtendedFields(inst, rec);
            return true;
        }

        /// <summary>The P3-A record fields the adopt reconstruction never needed (it built a NEW hire).
        /// Each in its own try so one renamed field cannot cost the whole promotion.</summary>
        private static void StampExtendedFields(EmployeeInstance inst, EmployeeEditPayload p)
        {
            try { if (p.DayHired > 0) inst.dayHired = p.DayHired; } catch { }
            try { if (p.NextSickDay > 0) inst.nextSickDay = p.NextSickDay; } catch { }
            try { inst.workedHoursToday = p.WorkedHoursToday; inst.workedHoursThisWeek = p.WorkedHoursThisWeek; } catch { }
            try { inst.workedDays = p.WorkedDays; inst.assignedWeeklyHours = p.AssignedWeeklyHours; } catch { }
            try { inst.isAbsent = p.IsAbsent; inst.isReplaced = p.IsReplaced; inst.isBeingReplaced = p.IsBeingReplaced; } catch { }
            try { inst.isTrainingDay = p.IsTrainingDay; inst.hasSendQuitWarning = p.HasSendQuitWarning; } catch { }
            try { inst.sendRetirementNotice = p.SendRetirementNotice; } catch { }
            try { if (!string.IsNullOrEmpty(p.AssignedHrManagerPlanId)) inst.assignedHrManagerPlanId = p.AssignedHrManagerPlanId; } catch { }
            try { if (p.InitialCombinedSkillAmount > 0f) inst.initialCombinedSkillAmount = p.InitialCombinedSkillAmount; } catch { }
            try { if (!string.IsNullOrEmpty(p.PresetId)) inst.presetId = p.PresetId; } catch { }
            try
            {
                var ws = inst.assignedWorkStationItems;
                if (ws != null && p.AssignedWorkStationItems != null) { ws.Clear(); ws.AddRange(p.AssignedWorkStationItems); }
            }
            catch { }
            try
            {   // the weekly-day enum is not csproj-named here — go through IList so the element type
                // comes from the live list itself (the same trick the absence list installer uses).
                var days = inst.assignedWeeklyDays as System.Collections.IList;
                var et   = days?.GetType().GetGenericArguments();
                if (days != null && et != null && et.Length == 1 && p.AssignedWeeklyDays != null)
                {
                    days.Clear();
                    foreach (var d in p.AssignedWeeklyDays) days.Add(System.Enum.ToObject(et[0], d));
                }
            }
            catch { }
        }

        /// <summary>MERGER PHASE 3-C (C2d): the RETURNED OWNER takes the simulated record of one of its
        /// OWN employees back. Matched BY ID against this machine's real records:
        ///   - an id this save ALREADY holds is UPDATED IN PLACE - never duplicated and never re-hired
        ///     (the adopt reconstruction would stamp a fresh dayHired, roll a new sick day and grant a
        ///     new complaint grace, which would erase exactly the absence this leg is carrying back);
        ///   - an id this save does NOT hold is a hire the simulator made while the owner was away, and
        ///     comes in through PromoteRecord - the same reconstruction P3-B promotes with, which
        ///     removes any same-id record first, so it cannot duplicate either.
        /// Returns 1 = updated, 2 = added, 0 = neither.</summary>
        public static int ApplyReturnedRecord(EmployeeEditPayload rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.EmployeeId) || string.IsNullOrEmpty(rec.AddressKey)) return 0;
            EmployeeInstance? inst = null;
            try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(rec.EmployeeId, out inst); } catch { }
            if (inst == null)
            {
                // The dictionary is a cache; the save's own list is the truth.
                try
                {
                    var list = SaveGameManager.Current?.EmployeeInstances;
                    if (list != null)
                        foreach (var e in list)
                            if (e != null && e.id == rec.EmployeeId) { inst = e; break; }
                }
                catch { }
            }
            if (inst == null) return PromoteRecord(rec) ? 2 : 0;
            StampLiveFields(inst, rec);
            StampExtendedFields(inst, rec);
            return 1;
        }

        /// <summary>The fields the ADOPT reconstruction sets while BUILDING a record, written onto an
        /// EXISTING one instead. Each in its own try so one renamed game field cannot cost the rest.</summary>
        private static void StampLiveFields(EmployeeInstance inst, EmployeeEditPayload p)
        {
            try { inst.hourlyWage = p.Wage; } catch { }
            try { if (p.Satisfaction > 0f) inst.satisfaction = p.Satisfaction; } catch { }
            try
            {
                var a = MergerAbsence.AddressOfKey(p.AddressKey);
                if (a != null) inst.assignedAddress = a;   // the simulator may have moved them between MY shops
            }
            catch { }
            try
            {
                if (p.Skills != null && p.Skills.Count > 0)
                {
                    var skills = inst.characterData.skills;
                    skills.Clear();
                    foreach (var pair in p.Skills)
                    {
                        int eq = pair == null ? -1 : pair.IndexOf('=');
                        if (eq <= 0) continue;
                        float.TryParse(pair.Substring(eq + 1), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var val);
                        skills.Add(new BigAmbitions.Characters.Skills.Skill { name = pair.Substring(0, eq), value = val });
                    }
                }
            }
            catch { }
            try { if (p.Demands != null && p.Demands.Count > 0) { inst.demands.Clear(); inst.demands.AddRange(p.Demands); } } catch { }
            try
            {
                // TRAINING: the record resumes the session the simulator had it in (or none).
                if (string.IsNullOrEmpty(p.TrainingSkill)) inst.trainingSession = null;
                else
                {
                    if (inst.trainingSession == null)
                    {
                        var f = FieldOnType(inst.GetType(), "trainingSession");
                        if (f != null) f.SetValue(inst, Activator.CreateInstance(f.FieldType));
                    }
                    var ts = inst.trainingSession;
                    if (ts != null) { ts.skill = p.TrainingSkill; ts.startDay = p.TrainingStartDay; }
                }
            }
            catch { }
            try
            {
                var c = inst.complaintData;
                if (c != null)
                {
                    c.isComplaining            = p.ComplaintIsComplaining;
                    c.hoursUntilNextComplaint  = p.ComplaintHoursUntilNext;
                    c.complaintDeadlineHours   = p.ComplaintDeadlineHours;
                    c.hasRival                 = p.ComplaintHasRival;
                }
            }
            catch { }
        }

        private static System.Reflection.FieldInfo? FieldOnType(Type t, string name)
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public
                                                   | System.Reflection.BindingFlags.NonPublic
                                                   | System.Reflection.BindingFlags.Instance;
            for (var ty = t; ty != null; ty = ty.BaseType)
            {
                var f = ty.GetField(name, F);
                if (f != null) return f;
            }
            return null;
        }

        private static readonly HashSet<string> _refusedSynthetic = new();   // H-ADOPT-1 refusal log, once per id

        public static void Reset()
        {
            _pendingAdopt.Clear();
            _refusedSynthetic.Clear();
            _pendingTransfer.Clear();
            _transfersDone.Clear();
            _lastOwnAddr.Clear();
            _returnFailLog.Clear();
            _hrTrainApplied.Clear();
        }
    }
}
