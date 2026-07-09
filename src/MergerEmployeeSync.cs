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
    ///  • SCHEDULE (hours + shifts): native BizMan edits the FLIPPED replica's scheduleDays locally.
    ///    A 2s scan diffs each flipped shop's schedule against the last owner-applied signature; a
    ///    local edit sends the whole schedule to the owner (last-writer-wins — co-op semantics), who
    ///    applies it wholesale; the BusinessSync heartbeat republishes and every member converges.
    ///    While an edit is in flight the owner heartbeat is HELD for that shop (15s cap) so the
    ///    member's edit isn't clobbered by a pre-edit snapshot racing back.
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

        // ── SCHEDULE write-back (hours + shifts) ──────────────────────────────

        private static readonly Dictionary<string, string> _ownerSig   = new();   // addr → sig last applied FROM the owner
        private static readonly Dictionary<string, string> _sentSig    = new();   // addr → sig we sent (pending confirmation)
        private static readonly Dictionary<string, float>  _sentAt     = new();
        private const float PendingHoldSeconds = 15f;
        private static float _nextScan;

        /// <summary>MAIN THREAD (MPCanvasUI.Update). Detect local edits to flipped shops' schedules,
        /// own-employee assignments into flipped shops (→ record migration), and unsupported
        /// reassignments of injected partner staff (→ revert + toast).</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 2f;
            if (!MergerSync.IAmMember || MergerFlip.FlippedCount == 0) return;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string addr; try { addr = GameStateReader.AddressKey(reg); } catch { continue; }
                    if (!MergerFlip.IsFlipped(addr)) continue;
                    string sig = ScheduleSig(reg);
                    if (!_ownerSig.TryGetValue(addr, out var known)) { _ownerSig[addr] = sig; continue; }   // baseline on first sight
                    if (sig == known) continue;                                        // matches the owner's truth
                    _sentAt.TryGetValue(addr, out var at);
                    if (_sentSig.TryGetValue(addr, out var sent) && sent == sig
                        && Time.unscaledTime - at < PendingHoldSeconds) continue;      // already in flight
                    _sentSig[addr] = sig; _sentAt[addr] = Time.unscaledTime;
                    var p = new EmployeeEditPayload { PlayerId = MPConfig.PlayerId, Action = "schedule", AddressKey = addr };
                    SerializeSchedule(reg, p.Schedule);
                    Plugin.Logger.LogInfo($"[MergerStaff] schedule edit detected @ '{addr}' — routing {p.Schedule.Count} day(s) to the owner.");
                    Send(p);
                }
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

        /// <summary>GameStatePatcher calls this before applying an owner heartbeat's schedule to a reg:
        /// while OUR edit is in flight, the (pre-edit) owner snapshot must not clobber it.</summary>
        public static bool HoldScheduleApply(string addressKey)
        {
            if (!_sentSig.ContainsKey(addressKey)) return false;
            _sentAt.TryGetValue(addressKey, out var at);
            if (Time.unscaledTime - at >= PendingHoldSeconds)
            { _sentSig.Remove(addressKey); _sentAt.Remove(addressKey); return false; }   // timed out — let truth through
            return true;
        }

        /// <summary>GameStatePatcher calls this after applying an owner heartbeat's schedule: record the
        /// owner's truth as the diff baseline; an arriving echo of our own edit clears the pending hold.</summary>
        public static void NoteOwnerScheduleApplied(string addressKey, BuildingRegistration reg)
        {
            try
            {
                string sig = ScheduleSig(reg);
                _ownerSig[addressKey] = sig;
                if (_sentSig.TryGetValue(addressKey, out var sent) && sent == sig)
                { _sentSig.Remove(addressKey); _sentAt.Remove(addressKey); }
            }
            catch { }
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
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null) return;
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null || GameStateReader.AddressKey(reg) != p.AddressKey) continue;
                        if (!MergerFlip.TrulyMine(reg)) { Plugin.Logger.LogWarning($"[MergerStaff] routed schedule for '{p.AddressKey}' — not truly mine, dropped."); return; }
                        ApplyScheduleDtos(reg, p.Schedule);
                        Plugin.Logger.LogInfo($"[MergerStaff] applied routed SCHEDULE @ '{p.AddressKey}' ({p.Schedule?.Count ?? 0} day(s), by '{p.PlayerId}').");
                        return;
                    }
                }
                else if (p.Action == "adopt")
                {
                    // A member staffed MY shop with THEIR employee — the record migrates into MY save.
                    bool exists = false;
                    try { exists = Helpers.EmployeeHelper.EmployeeInstancesDictionary.ContainsKey(p.EmployeeId ?? ""); } catch { }
                    if (exists) { MPRegisterSync.ForceRosterRepublish(p.AddressKey); return; }   // idempotent (retry after a lost confirm)
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null || gi.EmployeeInstances == null) return;
                    BuildingRegistration target = null;
                    foreach (var reg in gi.BuildingRegistrations)
                        if (reg != null && GameStateReader.AddressKey(reg) == p.AddressKey) { target = reg; break; }
                    if (target == null || !MergerFlip.TrulyMine(target))
                    { Plugin.Logger.LogWarning($"[MergerStaff] routed adopt for '{p.AddressKey}' — not truly mine, dropped."); return; }

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
                    gi.EmployeeInstances.Add(inst);
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[inst.id] = inst; } catch { }
                    Plugin.Logger.LogInfo($"[MergerStaff] ADOPTED '{inst.characterData?.name}' ({inst.id}) into '{p.AddressKey}' at ${p.Wage:F0}/h (from '{p.PlayerId}').");
                    MPRegisterSync.ForceRosterRepublish(p.AddressKey);   // the republish is the member's confirm signal
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerStaff] ApplyOnOwner: {ex.Message}"); }
        }

        // ── Schedule (de)serialization — the BusinessSync/GameStatePatcher shapes, kept in step ────

        public static void SerializeSchedule(BuildingRegistration reg, List<ScheduleDayInfo> into)
        {
            try
            {
                if (reg.scheduleDays == null) return;
                foreach (var sd in reg.scheduleDays)
                {
                    if (sd == null) continue;
                    var dto = new ScheduleDayInfo { Day = (int)sd.day, IsOpen = sd.isOpen };
                    if (sd.openingHourSlots != null)
                        foreach (var s in sd.openingHourSlots)
                            if (s != null) dto.OpeningHourSlots.Add(new OpeningHourSlotInfo { StartingHour = s.startingHour, EndingHour = s.endingHour });
                    if (sd.workShifts != null)
                        foreach (var w in sd.workShifts)
                            if (w != null) dto.WorkShifts.Add(new WorkShiftInfo
                            {
                                EmployeeId = w.employeeId ?? "", ItemInstanceId = w.itemInstanceId ?? "",
                                StartingHour = w.startingHour, EndingHour = w.endingHour, Type = (int)w.type,
                            });
                    into.Add(dto);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MergerStaff] serialize: {ex.Message}"); }
        }

        public static void ApplyScheduleDtos(BuildingRegistration reg, List<ScheduleDayInfo> days)
        {
            if (reg.scheduleDays == null || days == null || days.Count == 0) return;
            reg.scheduleDays.Clear();
            foreach (var d in days)
            {
                var sd = new ScheduleDay { day = (BigAmbitions.DayNightCycle.DayOfWeekOrdered)d.Day, isOpen = d.IsOpen };
                if (d.OpeningHourSlots != null)
                    foreach (var slot in d.OpeningHourSlots)
                        sd.openingHourSlots.Add(new OpeningHourSlot(slot.StartingHour, slot.EndingHour));
                if (d.WorkShifts != null)
                    foreach (var shift in d.WorkShifts)
                        if (shift != null) sd.AddWorkShift(new WorkShift
                        {
                            employeeId = shift.EmployeeId ?? "", itemInstanceId = shift.ItemInstanceId ?? "",
                            startingHour = shift.StartingHour, endingHour = shift.EndingHour,
                            type = (WorkShiftType)shift.Type,
                        });
                reg.scheduleDays.Add(sd);
            }
        }

        public static string ScheduleSig(BuildingRegistration reg)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                if (reg.scheduleDays != null)
                    foreach (var sd in reg.scheduleDays)
                    {
                        if (sd == null) continue;
                        sb.Append((int)sd.day).Append(sd.isOpen ? 'o' : 'c');
                        if (sd.openingHourSlots != null)
                            foreach (var s in sd.openingHourSlots) if (s != null) sb.Append(s.startingHour).Append('-').Append(s.endingHour).Append(',');
                        sb.Append('#');
                        if (sd.workShifts != null)
                            foreach (var w in sd.workShifts) if (w != null) sb.Append(w.employeeId).Append('@').Append(w.itemInstanceId).Append(':').Append(w.startingHour).Append('-').Append(w.endingHour).Append('/').Append((int)w.type).Append(';');
                        sb.Append('|');
                    }
            }
            catch { }
            return sb.ToString();
        }

        public static void Reset()
        {
            _ownerSig.Clear(); _sentSig.Clear(); _sentAt.Clear(); _pendingAdopt.Clear();
        }
    }
}
