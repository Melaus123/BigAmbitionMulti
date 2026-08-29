// ── AI-staff on-demand sync + host-claimed poaching (user-approved slice, 2026-08-29) ────────────
//
// FIELD 20260830-042803: on every joining player, the "Employees working at X" (poach) window was
// EMPTY — and always had been, in 0.11 too. reg.aiEmployees is generated at CityGenerator time
// (suppressed on clients) and no snapshot ever carried it. Decisions (user, 2026-08-29):
//   * ON-DEMAND (ruling-25 pattern): the window's open sends one request; the host answers the ONE
//     requester. No steady-state traffic.
//   * BOTH HALVES IN ONE SLICE: a visible list whose hire silently diverges is a UI-vs-behavior
//     defect, so the hire (native AcceptOffer for a rival negotiation) is CLAIMED at the host —
//     two players cannot poach the same person; the loser's negotiation completes as declined.
//
// Determinism makes the data small: AiBusinessEmployeeData regenerates the whole person (name,
// gender, age, wage, demands, secondary skill) from a Random seeded by id.GetHashCode(), so a row
// ships only the serialized fields. The one non-deterministic field pair (primarySkillValue and
// hoursPerWeekDemandName — replacements roll a SMALLER value range than the ctor default) is
// overwritten from the wire after construction.
//
// v1 scope cuts, both logged: poachedEmployees rows are NOT shipped (they reference full
// EmployeeInstances clients don't hold; the window on a client misses them — host-side view is
// complete); a FAILED claim is log-only feedback (any on-screen wording needs approval first).
//
// The receiver OVERWRITES its whole list: clients can locally invent random staff via
// EstimatedWeeklyIncomeHelper's fallback generator (F-2026-08-29-CF) — inventions must not survive.
using System.Collections.Generic;
using HarmonyLib;
using AI.Employees.SalaryNegotiation;
using Buildings.BuildingTypes.Shared;
using UnityEngine;

namespace BigAmbitionsMP
{
    internal static class RivalStaffSync
    {
        // ── Client bookkeeping ───────────────────────────────────────────────
        private static string _openAddr = "";                    // the address the poach window is showing
        private static readonly Dictionary<string, (CandidateSalaryNegotiation neg, float wage, float bonus)> _pending = new();
        internal static bool _committing;                        // true while completing an OK'd claim → AcceptOffer passes native

        internal static void Reset()
        {
            _openAddr = "";
            _pending.Clear();
            _committing = false;
        }

        private static BuildingRegistration? FindReg(string addressKey)
        {
            try
            {
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null || string.IsNullOrEmpty(addressKey)) return null;
                foreach (var r in regs)
                    if (r != null && GameStateReader.AddressKey(r) == addressKey) return r;
            }
            catch { }
            return null;
        }

        private static bool IsAiBusiness(BuildingRegistration reg)
        {
            try { return !reg.RentedByPlayer && !GameStatePatcher.IsAnyPlayerBusiness(reg); }
            catch { return false; }
        }

        // ── Client: window opened → ask the host ─────────────────────────────
        internal static void NotePopupShown(BuildingRegistration reg)
        {
            try
            {
                if (MPServer.IsRunning || !MPClient.IsClientInWorld) return;   // host/native reads its own truth
                if (reg == null || !IsAiBusiness(reg)) return;
                _openAddr = GameStateReader.AddressKey(reg);
                MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.RivalStaffReq, MPConfig.PlayerId,
                    new RivalStaffReqPayload { AddressKey = _openAddr }));
                Plugin.Logger.LogInfo($"[RivalStaff] window open for '{_openAddr}' → staff requested from host (local rows: {reg.aiEmployees?.Count ?? 0}).");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalStaff] popup note: {ex.Message}"); }
        }

        // ── Both sides: build rows from a registration (host truth) ──────────
        internal static List<RivalStaffRow> RowsOf(BuildingRegistration reg)
        {
            var rows = new List<RivalStaffRow>();
            try
            {
                if (reg.aiEmployees != null)
                    foreach (var e in reg.aiEmployees)
                    {
                        if (e == null || e.isPoached) continue;   // v1: poached rows stay host-only
                        rows.Add(new RivalStaffRow
                        {
                            Id = e.id ?? "", Skill = e.primarySkillName ?? "",
                            SkillValue = e.primarySkillValue, HoursDemand = e.hoursPerWeekDemandName ?? "",
                            NegotiationFinished = e.isNegotiationFinished, ReenableAtDay = e.reenableNegotiationAtDay,
                        });
                    }
            }
            catch { }
            return rows;
        }

        // ── Client: apply host rows (overwrite, then repaint if the window shows this shop) ──
        internal static void ApplyRows(string addressKey, List<RivalStaffRow> rows)
        {
            try
            {
                var reg = FindReg(addressKey);
                if (reg == null || rows == null) return;
                var list = new List<AiBusinessEmployeeData>(rows.Count);
                foreach (var r in rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                    // Ctor derives value/demand from the id seed — then host truth overwrites both
                    // (a replacement's value was rolled in a smaller range on the host).
                    var d = new AiBusinessEmployeeData(r.Id, r.Skill, reg.Address);
                    d.primarySkillValue = r.SkillValue;
                    d.hoursPerWeekDemandName = r.HoursDemand;
                    d.isNegotiationFinished = r.NegotiationFinished;
                    d.reenableNegotiationAtDay = r.ReenableAtDay;
                    list.Add(d);
                }
                reg.aiEmployees = list;
                Plugin.Logger.LogInfo($"[RivalStaff] '{addressKey}': {list.Count} staff row(s) applied from host.");
                // Ruling 32 live parity: the open window repaints now, not on reopen.
                if (_openAddr == addressKey)
                {
                    var ui = InstanceBehavior<UI.UIs>.Instance?.rivalEmployeesUi;
                    if (ui != null && ui.gameObject.activeInHierarchy) ui.Show(reg);
                }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalStaff] apply rows: {ex.Message}"); }
        }

        internal static void OnStaffRes(RivalStaffResPayload p)
        {
            if (p != null) ApplyRows(p.AddressKey, p.Rows);
        }

        // ── Client: the deferred hire's verdict ──────────────────────────────
        internal static void OnPoachResult(PoachResultPayload p)
        {
            try
            {
                if (p == null) return;
                string key = p.AddressKey + "|" + p.EmployeeId;
                if (_pending.TryGetValue(key, out var pend))
                {
                    _pending.Remove(key);
                    if (p.Ok)
                    {
                        // Native commit end-to-end (hire, wage, satisfaction, bonus charge, local
                        // ReplaceAiBusinessEmployee) — the flag lets the prefix pass it through, and
                        // the host rows applied right after overwrite the locally-rolled replacement.
                        try { _committing = true; pend.neg.AcceptOffer(pend.wage, pend.bonus); }
                        finally { _committing = false; }
                        Plugin.Logger.LogInfo($"[Poach] host GRANTED '{p.EmployeeId}' at '{p.AddressKey}' — hire completed natively.");
                    }
                    else
                    {
                        // Already gone (another player got there first, or the day rolled them out).
                        // Log-only feedback in v1 (on-screen wording needs approval); the repaint
                        // below makes the person visibly disappear from the window.
                        try { Helpers.EmployeeHelper.DiscardCandidate(pend.neg.employeeInstance); } catch { }
                        try { pend.neg.completed = true; pend.neg.accepted = false; } catch { }
                        Plugin.Logger.LogWarning($"[Poach] host REFUSED '{p.EmployeeId}' at '{p.AddressKey}' — no longer available; negotiation closed.");
                    }
                }
                ApplyRows(p.AddressKey, p.Rows);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Poach] result: {ex.Message}"); }
        }

        // ── Host: answer a staff request ─────────────────────────────────────
        internal static void HostAnswerStaffReq(RivalStaffReqPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                var reg = FindReg(p.AddressKey);
                if (reg == null || !IsAiBusiness(reg)) return;   // player shops never answer here
                MPServer.SendToPid(senderPid, MessageEnvelope.Create(MessageType.RivalStaffRes, "host",
                    new RivalStaffResPayload { AddressKey = p.AddressKey, Rows = RowsOf(reg) }));
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[RivalStaff] host answer: {ex.Message}"); }
        }

        // ── Host: arbitrate a poach claim ────────────────────────────────────
        internal static void HostHandlePoachClaim(PoachClaimPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.EmployeeId)) return;
                var reg = FindReg(p.AddressKey);
                bool ok = false;
                if (reg != null && IsAiBusiness(reg))
                {
                    AiBusinessEmployeeData? row = null;
                    try { row = reg.aiEmployees?.Find(x => x != null && x.id == p.EmployeeId && !x.isPoached); } catch { }
                    if (row != null)
                    {
                        // Native bookkeeping on the AUTHORITATIVE copy: removes the person and rolls
                        // the replacement. The claimant gets the post-claim rows below; everyone else
                        // converges on their next window open (on-demand pattern by decision).
                        try { reg.ReplaceAiBusinessEmployee(p.EmployeeId); ok = true; } catch (System.Exception rx)
                        { Plugin.Logger.LogWarning($"[Poach] host replace threw: {rx.Message}"); }
                    }
                }
                Plugin.Logger.LogInfo($"[Poach] claim by '{senderPid}' for '{p.EmployeeId}' at '{p.AddressKey}' → {(ok ? "GRANTED" : "refused (gone)")}.");
                MPServer.SendToPid(senderPid, MessageEnvelope.Create(MessageType.PoachResult, "host",
                    new PoachResultPayload { AddressKey = p.AddressKey, EmployeeId = p.EmployeeId, Ok = ok, Rows = reg != null ? RowsOf(reg) : new List<RivalStaffRow>() }));
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Poach] host claim: {ex.Message}"); }
        }

        // ── Client: intercept the rival-hire commit ──────────────────────────
        internal static bool DeferAcceptToHost(CandidateSalaryNegotiation neg, float wage, float bonus)
        {
            try
            {
                if (_committing) return false;                                    // completing a granted claim
                if (MPServer.IsRunning || !MPClient.IsClientInWorld) return false; // host/native commit directly
                if (neg == null || !neg.isRival) return false;
                var addr = neg.employeeInstance?.assignedAddress;
                if (addr == null) return false;
                var reg = Helpers.BuildingHelper.GetBuildingRegistration(addr);
                if (reg == null || !IsAiBusiness(reg)) return false;              // only AI shops are claimed
                string key = GameStateReader.AddressKey(reg);
                string id  = neg.employeeInstance?.id ?? "";
                if (string.IsNullOrEmpty(id)) return false;
                _pending[key + "|" + id] = (neg, wage, bonus);
                MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.PoachClaim, MPConfig.PlayerId,
                    new PoachClaimPayload { AddressKey = key, EmployeeId = id }));
                Plugin.Logger.LogInfo($"[Poach] accept deferred — claiming '{id}' at '{key}' from the host.");
                return true;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Poach] defer: {ex.Message}"); return false; }
        }
    }

    /// <summary>Window open: on a client this asks the host for the shop's real staff; the (possibly
    /// stale) local list shows immediately and the reply repaints in place.</summary>
    [HarmonyPatch(typeof(RivalEmployeesUi), nameof(RivalEmployeesUi.Show))]
    public static class Patch_RivalEmployeesUi_Show_RequestStaff
    {
        static void Prefix(BuildingRegistration buildingRegistration)
        {
            try { RivalStaffSync.NotePopupShown(buildingRegistration); } catch { }
        }
    }

    /// <summary>The rival-hire commit. On a client the accept is CLAIMED at the host first — the
    /// prefix swallows the native call and re-invokes it (flag-gated) only on a granted verdict.</summary>
    [HarmonyPatch(typeof(CandidateSalaryNegotiation), nameof(CandidateSalaryNegotiation.AcceptOffer))]
    public static class Patch_SalaryNegotiation_Accept_ClaimAtHost
    {
        static bool Prefix(CandidateSalaryNegotiation __instance, float hourlyWageAmount, float bonusAmount)
        {
            return !RivalStaffSync.DeferAcceptToHost(__instance, hourlyWageAmount, bonusAmount);
        }
    }
}
