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
    /// Host validates every op: sender must BE the owner or be MERGED with the owner. INERT without
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

        private static void ScanAssignments(GameInstance gi)
        {
            if (gi.EmployeeInstances == null) return;
            foreach (var e in gi.EmployeeInstances)
            {
                if (e == null || string.IsNullOrEmpty(e.id) || e.assignedAddress == null) continue;
                string addr; try { addr = GameStateReader.AddressKey(e.assignedAddress); } catch { continue; }
                if (!MergerFlip.IsFlipped(addr)) continue;

                // H-ADOPT-1 (harness run T-P0-4, 2026-09-10): the mod's own register stand-ins (BAMP_DUTY_*, the
                // synthetic cashiers a visitor injects for a partner's staffed tills) sit at flipped addresses and are
                // neither injected staff nor candidates - this scan adopted 55 of them OUT to the owner, who ADOPTED 48
                // phantom 'On-Duty Staff' at $0/h into its live roster (the save strip kept the .hsg clean). Never a real hire.
                if (MPRegisterSync.IsSyntheticDuty(e.id)) continue;

                if (MPRegisterSync.IsInjectedStaff(e.id))
                {
                    // Partner staff moved between shops via the dropdown — not supported yet (cross-save
                    // reassignment); revert to the shop the owner's roster says they work at.
                    string home = MPRegisterSync.InjectedAddrOf(e.id);
                    if (home != "" && home != addr)
                    {
                        try { e.assignedAddress = AddressOfKey(gi, home); } catch { }
                        PassengerHud.Toast("Moving a partner's employee between shops isn't supported yet.");
                        Plugin.Logger.LogInfo($"[MergerStaff] reverted unsupported reassignment of partner staff '{e.id}' ({addr} → back to {home}).");
                    }
                    continue;
                }
                bool candidate = false; try { candidate = e.IsCandidate; } catch { }
                if (candidate) continue;   // negotiate first — the transfer happens once they're hired

                _pendingAdopt.TryGetValue(e.id, out var sentAt);
                if (sentAt > 0f && Time.unscaledTime - sentAt < 30f) continue;   // in flight
                _pendingAdopt[e.id] = Time.unscaledTime;

                var p = new EmployeeEditPayload
                {
                    PlayerId = MPConfig.PlayerId, Action = "adopt", AddressKey = addr, EmployeeId = e.id,
                    Wage = e.hourlyWage, Satisfaction = e.satisfaction,
                };
                try { p.Name = e.characterData?.name ?? "Staff"; } catch { }
                try { p.Gender = (int)e.characterData.gender; } catch { }
                try { p.AgeDays = e.characterData?.ageInDays ?? 0; } catch { }
                try
                {
                    var skills = e.characterData?.skills;
                    if (skills != null)
                        foreach (var sk in skills)
                            if (sk != null && !string.IsNullOrEmpty(sk.name))
                                p.Skills.Add(sk.name + "=" + sk.value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                }
                catch { }
                try { if (e.demands != null) p.Demands.AddRange(e.demands); } catch { }
                Plugin.Logger.LogInfo($"[MergerStaff] adopting-out '{p.Name}' ({e.id}) → partner shop '{addr}' (record migrates to the owner's save on confirm).");
                Send(p);
            }
        }

        /// <summary>MPRegisterSync roster apply calls this when an incoming roster carries an id that
        /// also exists as a LOCAL record: true = it's our pending adopt confirmed by the owner — remove
        /// the local original and let the injection take over as the display copy.</summary>
        public static bool ConfirmAdopt(string employeeId)
        {
            if (!_pendingAdopt.Remove(employeeId)) return false;
            Plugin.Logger.LogInfo($"[MergerStaff] adopt of '{employeeId}' CONFIRMED by the owner's roster — local original released.");
            return true;
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

        /// <summary>THE OWNER's machine: apply a routed employee op natively. MAIN THREAD.</summary>
        public static void ApplyOnOwner(EmployeeEditPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (p.Action == "fire")
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
                    BuildingRegistration target = null;
                    foreach (var reg in gi.BuildingRegistrations)
                        if (reg != null && GameStateReader.AddressKey(reg) == p.AddressKey) { target = reg; break; }
                    // P3-B (B3c): a shop this machine SIMULATES for an absent owner is ours to staff for
                    // the duration — the promotion is the same reconstruction, on the same real ids.
                    if (target == null || (!MergerFlip.TrulyMine(target) && !MergerAbsence.SimulatesHere(p.AddressKey)))
                    { Plugin.Logger.LogWarning($"[MergerStaff] routed adopt for '{p.AddressKey}' — neither truly mine nor simulated here, dropped."); return; }

                    // Reconstruct by primary skill (the GenerateCandidate subclass mapping — managers
                    // must keep their class or their plans break).
                    string primary = "";
                    if (p.Skills != null && p.Skills.Count > 0)
                    { int eq = p.Skills[0].IndexOf('='); primary = eq > 0 ? p.Skills[0].Substring(0, eq) : p.Skills[0]; }
                    EmployeeInstance inst = primary switch
                    {
                        "ba:skill_logisticsmanager" => new LogisticsManager(),
                        "ba:skill_hrmanager"        => new HRManager(),
                        "ba:skill_headhunter"       => new Headhunter(),
                        _                           => new EmployeeInstance(),
                    };
                    inst.Initialize();
                    inst.id = p.EmployeeId;   // KEEP the id — shifts the member scheduled + the adopt-confirm match on it
                    inst.hourlyWage = p.Wage;
                    inst.satisfaction = p.Satisfaction > 0f ? p.Satisfaction : 100f;
                    inst.assignedAddress = new Address(target.StreetName, target.StreetNumber);
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
                    Plugin.Logger.LogInfo($"[MergerStaff] ADOPTED '{inst.characterData?.name}' ({inst.id}) into '{p.AddressKey}' at ${p.Wage:F0}/h (from '{p.PlayerId}').");
                    MPRegisterSync.ForceRosterRepublish(p.AddressKey);   // the republish is the member's confirm signal
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
            if (rec == null || string.IsNullOrEmpty(rec.EmployeeId) || string.IsNullOrEmpty(rec.AddressKey)) return false;
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

        private static readonly HashSet<string> _refusedSynthetic = new();   // H-ADOPT-1 refusal log, once per id

        public static void Reset()
        {
            _pendingAdopt.Clear();
            _refusedSynthetic.Clear();
        }
    }
}
