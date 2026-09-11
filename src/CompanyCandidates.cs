using System;
using System.Collections.Generic;
using AI.Employees.SalaryNegotiation;          // CandidateSalaryNegotiation (the hire seam)
using Entities;                                // EmployeeInstance, CandidateInfo
using HarmonyLib;
using Helpers;                                 // EmployeeHelper, RecruitmentHelper
using UI.Smartphone.Apps.MyEmployees;          // MyEmployees, CandidateModel
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// MERGER PHASE 4b (PEOPLE) - P1, THE SHARED CANDIDATE POOL (D20-1).
    ///
    /// A candidate pool is strictly per save: every generator writes SaveGameManager.Current's
    /// CandidateEmployeeInstances (decompile Helpers/RecruitmentHelper.cs:80), and job-board
    /// generation for a merged shop is already OWNER-ONLY (BusinessHelper.cs:381-383 behind the
    /// merger authority veil), so a member neither double-generates for a partner's shop nor ever
    /// sees what that shop's board produced. This file is the two legs that close that gap:
    ///
    ///  - PUBLISH + INJECT. Each member publishes its own candidate rows (the injected-staff record
    ///    shape, StaffInfo, plus the candidate extras: the ORIGIN's expiry clock, the job-board flag
    ///    and the demands) whenever that set changes; the host fans one member's rows out to that
    ///    member's ONLINE co-members and replays them to a joiner. A receiver injects them into its
    ///    own CandidateEmployeeInstances as tagged display copies - the same idea as the injected
    ///    staff records, with the same two guarantees: they never reach a .hsg (the save strip in
    ///    MPRegisterSync.StripSyntheticsForSave) and they sit out the native pass that would age them
    ///    (EmployeeHelper.RunHourly decrements hoursUntilExpiring and DELETES at zero, decompile
    ///    Helpers/EmployeeHelper.cs:242-246 - so expiry is driven by the ORIGIN's clock through the
    ///    republish, never counted locally, and copies cannot vanish at different times).
    ///
    ///  - THE CLAIM. Opening a salary negotiation on a company candidate claims it at the host, which
    ///    grants to the first asker and refuses the rest (with a log). The precedent is exact:
    ///    RivalStaffSync.DeferAcceptToHost (RivalStaffSync.cs:216-236) swallows the native call and
    ///    re-invokes it flag-gated on a GRANTED verdict. The verdict is broadcast to the whole company
    ///    INCLUDING the origin, so every copy - and the origin's real record - knows it is taken.
    ///    Claimed rows render with the claimant's own player colour and a greyed mass-action checkbox:
    ///    both are renderings this mod already uses (PlayerColours.TagOpen on the My Employees rows,
    ///    the ruling-23 checkbox greying), so nothing new appears on screen.
    ///
    /// A hire needs NO new route: it commits into the hirer's own EmployeeInstances through the game's
    /// own EmployeeHelper.HireCandidate, and a hire aimed at a partner shop is then carried by the
    /// existing adopt migration (MergerEmployeeSync). What the hire does need is the ORIGIN's copy
    /// removed - that is the routed "hired" leg below. INERT outside a merger: no publish, no claim,
    /// no injection, and every patch falls straight through.
    /// </summary>
    public static class CompanyCandidates
    {
        private const string Tag = "[Candidates]";
        private const float  PublishSeconds     = 5f;
        private const float  KeepaliveSeconds   = 30f;
        private const float  PendingHoldSeconds = 15f;
        private const int    MaxRowsPerOwner    = 100;

        // -- state --
        private static float _nextTick;
        private static string _sigSent;                                                                        // r3 MINOR-2: null = nothing published yet; no real signature equals null, so an EMPTY pool still goes out once
        private static readonly Dictionary<string, (string owner, EmployeeInstance inst)> _injected = new();   // candidateId -> origin + display copy
        private static readonly Dictionary<string, HashSet<string>> _poolByOwner = new();                      // owner pid -> last published ids
        private static readonly Dictionary<string, string> _claims = new();                                    // candidateId -> claimant pid
        private static readonly Dictionary<string, float> _pending = new();                                    // candidateId -> when we asked to negotiate
        private static readonly Dictionary<string, float> _keepalive = new();                                  // candidateId -> last claim re-assert
        private static readonly HashSet<string> _logged = new();
        private static bool _committing;                                                                       // re-invoking the native negotiation on a GRANTED verdict
        private static bool _publishedOnce;                                                                    // RIG-3: my own pool has been offered to the host at least once this connection
        private static bool _poolSeen;                                                                         // U3(c): a co-member's pool has ARRIVED at least once this connection
        private static readonly HashSet<string> _everCopied = new();                                           // r3 MINOR-4: candidate ids this session ever held as a COMPANY COPY - the only ids the orphan sweep may close
        private static readonly HashSet<string> _reassertOnly = new();                                         // r4 MINOR-1: claims asked by the RE-ASSERT, which must never open a dialog

        // -- identity --

        /// <summary>A display copy of a partner's candidate (never one of this save's own).</summary>
        public static bool IsInjectedCandidate(string id)
            => !string.IsNullOrEmpty(id) && _injected.ContainsKey(id);

        public static string OwnerOfCandidate(string id)
            => !string.IsNullOrEmpty(id) && _injected.TryGetValue(id, out var v) ? v.owner : "";

        /// <summary>Who holds this candidate right now ("" = free). Answered for the origin's REAL
        /// record too - the origin must not negotiate with somebody the host gave to a partner.</summary>
        public static string ClaimantOf(string id)
            => !string.IsNullOrEmpty(id) && _claims.TryGetValue(id, out var c) ? c ?? "" : "";

        public static int InjectedCount => _injected.Count;

        // -- tick --

        /// <summary>MAIN THREAD (MPCanvasUI.Update).</summary>
        public static void Tick()
        {
            try
            {
                if (Time.unscaledTime < _nextTick) return;
                _nextTick = Time.unscaledTime + PublishSeconds;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (!MergerSync.IAmMember)
                {
                    if (_injected.Count > 0) ClearAll("this player is not in a company");
                    _sigSent = null;
                    return;
                }
                // RIG-3 (rig run 4): the re-assert used to run BEFORE this machine had ever published its
                // own pool, so the host had nothing listing the candidate and logged "no member lists that
                // candidate, dropped" three times before finally granting. Publish FIRST, and hold the
                // re-assert until PublishMine has run once since this connection.
                PublishMine();
                if (_publishedOnce) ReassertClaims();
                SweepClaims();
                SweepPending();
                SweepOrphanNegotiations();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} tick: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Republish on the next tick (after a hire, a discard, or a claim change).</summary>
        public static void PublishNow() { _sigSent = null; _nextTick = 0f; }

        // -- origin: publish my own candidates --

        private static void PublishMine()
        {
            var gi = SaveGameManager.Current;
            if (gi?.CandidateEmployeeInstances == null) return;
            _publishedOnce = true;                                  // RIG-3: the re-assert may run from here on
            var rows = new List<CandidateRow>();
            foreach (var e in gi.CandidateEmployeeInstances)
            {
                if (e == null || string.IsNullOrEmpty(e.id)) continue;
                if (_injected.ContainsKey(e.id)) continue;             // a partner's copy is never my pool
                bool isCand = false; try { isCand = e.IsCandidate; } catch { }
                if (!isCand) continue;
                var row = new CandidateRow { Staff = MPRegisterSync.StaffInfoOf(e), ClaimedBy = ClaimantOf(e.id) };
                try { row.HoursUntilExpiring = e.candidateInfo.hoursUntilExpiring; } catch { }
                try { row.FromJobBoard = e.candidateInfo.fromJobBoard; } catch { }
                try { if (e.demands != null) row.Demands.AddRange(e.demands); } catch { }
                rows.Add(row);
                if (rows.Count >= MaxRowsPerOwner) break;
            }
            rows.Sort((a, b) => string.CompareOrdinal(a.Staff.Id, b.Staff.Id));
            // Signature = membership + name + wage + the ORIGIN's expiry hour. The expiry belongs in it
            // on purpose: it is the field every copy must take from here rather than count locally, and
            // the game moves it once an hour - one small message per game hour, not per frame.
            var sb = new System.Text.StringBuilder();
            foreach (var r in rows)
                sb.Append(r.Staff.Id).Append('|').Append(r.Staff.Name).Append('|')
                  .Append(r.Staff.Wage.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('|').Append(r.HoursUntilExpiring).Append('|').Append(r.ClaimedBy).Append(';');
            string sig = sb.ToString();
            if (sig == _sigSent) return;
            bool first = _sigSent == null;
            _sigSent = sig;
            var p = new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "pool", OwnerPid = MPConfig.PlayerId, Candidates = rows };
            Send(p);
            if (!first || rows.Count > 0)
                Plugin.Logger.LogInfo($"{Tag} published my candidate pool: {rows.Count} (the host hands it to my company only).");
        }

        private static void Send(CompanyCandidatesPayload p)
        {
            if (MPServer.IsRunning) MPServer.HostRouteCompanyCandidates(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.CompanyCandidates, MPConfig.PlayerId, p));
        }

        // -- receiver --

        /// <summary>MAIN THREAD. One of the three inbound legs, from the host.</summary>
        public static void Receive(CompanyCandidatesPayload p)
        {
            try
            {
                if (p == null) return;
                switch (p.Action)
                {
                    case "pool":
                        if (string.IsNullOrEmpty(p.OwnerPid) || p.OwnerPid == MPConfig.PlayerId) return;
                        if (!MergerSync.MergedRuntime(p.OwnerPid, MPConfig.PlayerId))
                        {
                            if (_logged.Add("pool-nomember|" + p.OwnerPid))
                                Plugin.Logger.LogInfo($"{Tag} a candidate pool from '{p.OwnerPid}' arrived but we are not in one company - ignored.");
                            return;
                        }
                        ApplyPool(p.OwnerPid, p.Candidates ?? new List<CandidateRow>());
                        return;

                    case "verdict":
                        ApplyVerdict(p);
                        return;

                    case "hire-verdict":
                        ApplyHireVerdict(p);
                        return;

                    case "hired":
                        // THE ORIGIN: a co-member hired somebody out of my pool - my own record goes
                        // through the game's own discard, exactly as declining one does.
                        OnPartnerHiredMine(p.CandidateId, p.PlayerId);
                        return;

                    default:
                        Plugin.Logger.LogWarning($"{Tag} unknown action '{p.Action}' from '{p.PlayerId}' - ignored.");
                        return;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} Receive: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void ApplyPool(string ownerPid, List<CandidateRow> rows)
        {
            var gi = SaveGameManager.Current;
            if (gi?.CandidateEmployeeInstances == null) return;
            _poolSeen = true;   // U3(c): the join window is over - the orphan sweep may run
            if (rows.Count > MaxRowsPerOwner)
            { Plugin.Logger.LogWarning($"{Tag} pool from '{ownerPid}': implausible count {rows.Count} - ignored."); return; }
            var want = new HashSet<string>();
            int added = 0, updated = 0, gone = 0;
            foreach (var row in rows)
            {
                string id = row?.Staff?.Id ?? "";
                if (id.Length == 0) continue;
                want.Add(id);
                SetClaim(id, row.ClaimedBy ?? "");
                if (_injected.TryGetValue(id, out var have))
                {
                    // T4 (review r1 MAJOR-5): the two DIRECT removals in the game (ContactsApp.RemoveMessage
                    // and Contact.CleanOldMessages) take the instance out of both lists without ever calling
                    // DiscardCandidate, so the guard never sees them. If this copy's instance has gone, the
                    // update branch puts it back from the DTO instead of re-stamping an orphan.
                    bool stillListed = false;
                    for (int k = 0; k < gi.CandidateEmployeeInstances.Count; k++)
                        if (gi.CandidateEmployeeInstances[k]?.id == id) { stillListed = true; break; }
                    if (!stillListed)
                    {
                        gi.CandidateEmployeeInstances.Add(have.inst);
                        try { EmployeeHelper.EmployeeInstancesDictionary[id] = have.inst; } catch { }
                        if (_logged.Add("readd|" + id))
                            Plugin.Logger.LogInfo($"{Tag} the copy of '{id}' had been removed here without a discard - put back from '{ownerPid}'s pool.");
                    }
                    Stamp(have.inst, row);
                    _injected[id] = (ownerPid, have.inst);
                    _everCopied.Add(id);
                    updated++;
                    continue;
                }
                bool existsLocally = false;
                try { existsLocally = EmployeeHelper.EmployeeInstancesDictionary.ContainsKey(id); } catch { }
                if (existsLocally) continue;                       // never shadow a real local record (my own hire of this id)
                var inst = EmployeeHelper.CreateAIEmployeeInstance("ba:skill_customerservice");
                if (inst == null) continue;
                inst.id = id;
                inst.assignedAddress = null;                       // a copy is never pre-placed in one of MY shops
                try { inst.candidateInfo = new CandidateInfo(); } catch { }
                Stamp(inst, row);
                for (int i = gi.CandidateEmployeeInstances.Count - 1; i >= 0; i--)
                    if (gi.CandidateEmployeeInstances[i]?.id == id) gi.CandidateEmployeeInstances.RemoveAt(i);
                gi.CandidateEmployeeInstances.Add(inst);
                try { EmployeeHelper.EmployeeInstancesDictionary[id] = inst; } catch { }
                _injected[id] = (ownerPid, inst);
                _everCopied.Add(id);
                added++;
            }
            // ABSOLUTE set: a row this owner stopped listing has been hired, discarded or expired on the
            // machine that owns it, so the copy goes at once. No grace period like the bench's - the
            // expiry clock here never ticks, so a stale copy would otherwise never leave on its own.
            if (_poolByOwner.TryGetValue(ownerPid, out var prev))
                foreach (var id in prev)
                {
                    if (want.Contains(id)) continue;
                    if (!_injected.TryGetValue(id, out var v) || v.owner != ownerPid) continue;
                    RemoveInjected(id, destroy: true);
                    gone++;
                }
            _poolByOwner[ownerPid] = want;
            if (added + updated + gone > 0)
            {
                if (added + gone > 0)
                    Plugin.Logger.LogInfo($"{Tag} pool of '{ownerPid}' applied: +{added} ~{updated} -{gone} (total company copies {_injected.Count}).");
                RefreshIfOpen();
            }
        }

        /// <summary>The wire's fields onto one copy. The expiry is the ORIGIN's, never a local count.</summary>
        private static void Stamp(EmployeeInstance inst, CandidateRow row)
        {
            var s = row.Staff ?? new StaffInfo();
            try { inst.characterData.name = string.IsNullOrEmpty(s.Name) ? "Candidate" : s.Name; } catch { }
            try { if (s.Gender >= 0) inst.characterData.gender = (BigAmbitions.Characters.Gender)s.Gender; } catch { }
            try { inst.characterData.ageInDays = s.AgeDays > 0 ? s.AgeDays : RecruitmentHelper.GetRandomEmployeeAgeInDays(); } catch { }
            try { if (s.Wage > 0f) inst.hourlyWage = s.Wage; } catch { }
            try { if (s.Satisfaction > 0f) inst.satisfaction = s.Satisfaction; } catch { }
            try
            {
                if (s.Skills != null && s.Skills.Count > 0)
                {
                    // characterData.skills is THE list every display and skill test reads (the same
                    // decompile note the injected-staff fidelity carries, EmployeeInstance :211/:226).
                    var skills = inst.characterData.skills;
                    skills.Clear();
                    foreach (var pair in s.Skills)
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
            try { if (row.Demands != null) { inst.demands.Clear(); inst.demands.AddRange(row.Demands); } } catch { }
            try
            {
                if (inst.candidateInfo == null) inst.candidateInfo = new CandidateInfo();
                inst.candidateInfo.hoursUntilExpiring = row.HoursUntilExpiring > 0 ? row.HoursUntilExpiring : 1;
                inst.candidateInfo.fromJobBoard = row.FromJobBoard;
            }
            catch { }
        }

        private static void ApplyVerdict(CompanyCandidatesPayload p)
        {
            string id = p.CandidateId ?? "";
            if (id.Length == 0) return;
            SetClaim(id, p.ClaimedBy ?? "");
            bool mine = (p.ClaimedBy ?? "") == MPConfig.PlayerId;
            if (_pending.ContainsKey(id))
            {
                _pending.Remove(id);
                // r4 MINOR-1 (rig run, build A): a claim RE-ASSERTED at connect is for a negotiation that is
                // ALREADY OPEN here - opening a second one threw "begin negotiation: Object reference not set
                // to an instance of an object" on the host. Only a claim the player just asked for through
                // MyEmployees.NegotiateWithCandidate (or the harness lever) continues into the dialog.
                bool reassert = _reassertOnly.Remove(id);
                if (p.Ok && mine && reassert)
                    Plugin.Logger.LogInfo($"{Tag} claim of '{id}' re-asserted and GRANTED - the negotiation already open here keeps it; nothing new opened.");
                else if (p.Ok && mine) BeginNegotiationNow(id);
                else Plugin.Logger.LogWarning($"{Tag} claim of '{id}' REFUSED by the host - '{p.ClaimedBy}' is already talking to them; nothing opened here.");
            }
            if (mine) _keepalive[id] = Time.unscaledTime;
            RefreshIfOpen();
        }

        private static void SetClaim(string id, string claimant)
        {
            if (string.IsNullOrEmpty(claimant)) { _claims.Remove(id); _keepalive.Remove(id); }
            else _claims[id] = claimant;
        }

        // -- claims --

        /// <summary>Hold the claim only while a live negotiation exists here, and re-assert it every 30 s
        /// so the host can let a dropped claimant's candidate go (the schedule session's keepalive shape).</summary>
        private static void SweepClaims()
        {
            List<string> held = null;
            foreach (var kv in _claims)
                if (kv.Value == MPConfig.PlayerId) (held ??= new List<string>()).Add(kv.Key);
            if (held == null) return;
            foreach (var id in held)
            {
                if (!HasLiveNegotiation(id))
                {
                    string owner = OwnerOfCandidate(id);
                    SetClaim(id, "");
                    Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "release", CandidateId = id, OwnerPid = owner });
                    Plugin.Logger.LogInfo($"{Tag} released the claim on '{id}' - no negotiation is open here any more.");
                    continue;
                }
                _keepalive.TryGetValue(id, out var at);
                if (Time.unscaledTime - at < KeepaliveSeconds) continue;
                _keepalive[id] = Time.unscaledTime;
                Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "claim", CandidateId = id, OwnerPid = OwnerOfCandidate(id) });
            }
        }

        /// <summary>T2 (review r1 MAJOR-3): Reset() wipes the claim table on every reconnect, so a member
        /// with a negotiation still open stopped re-asserting and the host's 120 s sweep handed their
        /// candidate to somebody else. The live negotiations ARE the claim: anything open here that the
        /// table does not already answer for is re-claimed at once, every tick, so a reconnect re-asserts
        /// within one publish interval. (The host also refuses the ACCEPT of a candidate it has already
        /// granted, so even a wrongly freed claim can no longer produce two hires of one person.)</summary>
        private static void ReassertClaims()
        {
            try
            {
                var list = SaveGameManager.Current?.candidateSalaryNegotiations;
                if (list == null) return;
                foreach (var n in list)
                {
                    if (n == null || n.completed || n.employeeInstance == null) continue;
                    string id = n.employeeInstance.id ?? "";
                    if (id.Length == 0) continue;
                    if (!IsInjectedCandidate(id) && !IsMyPublishedCandidate(id)) continue;
                    if (ClaimantOf(id).Length > 0 || _pending.ContainsKey(id)) continue;
                    _pending[id] = Time.unscaledTime;
                    _reassertOnly.Add(id);                      // r4 MINOR-1: this grant must NOT open a second dialog
                    Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "claim", CandidateId = id, OwnerPid = OwnerOfCandidate(id) });
                    if (_logged.Add("reassert|" + id))
                        Plugin.Logger.LogInfo($"{Tag} re-asserting the claim on '{id}' - a negotiation is still open here.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} re-assert: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void SweepPending()
        {
            if (_pending.Count == 0) return;
            List<string> dead = null;
            foreach (var kv in _pending)
                if (Time.unscaledTime - kv.Value > PendingHoldSeconds) (dead ??= new List<string>()).Add(kv.Key);
            if (dead == null) return;
            foreach (var id in dead)
            {
                _pending.Remove(id);
                _reassertOnly.Remove(id);
                Plugin.Logger.LogWarning($"{Tag} the host never answered the claim on '{id}' - nothing opened here (it can be tried again).");
            }
        }

        /// <summary>T7(b), RIG ONLY: ids the `claim <id> hold` verb pins. It stands in for a live
        /// negotiation so the 5 s sweep cannot release the claim while a harness run is in the middle of a
        /// race. In memory, never published, never persisted; `claim <id> drop` clears it.</summary>
        private static readonly HashSet<string> _verbHeld = new();

        public static void SetVerbHold(string id, bool on)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (on) { _verbHeld.Add(id); Plugin.Logger.LogInfo($"{Tag} '{id}' is held by the harness - the sweep will not release this claim."); }
            else _verbHeld.Remove(id);
        }

        /// <summary>T7(b): drop the harness hold AND give the claim back, the same message the sweep sends.</summary>
        public static void DropClaim(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return;
                _verbHeld.Remove(id);
                if (ClaimantOf(id) != MPConfig.PlayerId) return;
                string owner = OwnerOfCandidate(id);
                SetClaim(id, "");
                Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "release", CandidateId = id, OwnerPid = owner });
                Plugin.Logger.LogInfo($"{Tag} released the claim on '{id}' (harness drop).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} DropClaim: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static bool HasLiveNegotiation(string id)
        {
            try
            {
                if (_verbHeld.Contains(id)) return true;
                var list = SaveGameManager.Current?.candidateSalaryNegotiations;
                if (list == null) return false;
                foreach (var n in list)
                    if (n != null && !n.completed && n.employeeInstance != null && n.employeeInstance.id == id) return true;
            }
            catch { }
            return false;
        }

        /// <summary>The claim came back GRANTED: run the game's own negotiation opener, flag-gated so the
        /// prefix below lets it through (the RivalStaffSync._committing shape).</summary>
        private static void BeginNegotiationNow(string id)
        {
            try
            {
                EmployeeInstance inst = null;
                try { EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(id, out inst); } catch { }
                if (inst == null)
                {
                    var gi = SaveGameManager.Current;
                    if (gi?.CandidateEmployeeInstances != null)
                        foreach (var c in gi.CandidateEmployeeInstances) if (c?.id == id) { inst = c; break; }
                }
                var page = InstanceBehavior<UI.UIs>.Instance?.fullMenu?.myEmployees;
                if (inst == null || page == null)
                { Plugin.Logger.LogWarning($"{Tag} claim of '{id}' granted but the candidate or the My Employees page is gone here - nothing opened."); return; }
                Plugin.Logger.LogInfo($"{Tag} claim of '{id}' GRANTED - opening the game's own salary negotiation.");
                try { _committing = true; page.NegotiateWithCandidate(inst); }
                finally { _committing = false; }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} begin negotiation: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>The negotiation seam (MyEmployees.NegotiateWithCandidate, decompile :600-613 - the ONE
        /// entry that mints a CandidateSalaryNegotiation). True = swallowed here.</summary>
        public static bool DeferNegotiationToHost(EmployeeInstance e)
        {
            try
            {
                if (_committing) return false;                                  // completing a granted claim
                if (e == null || string.IsNullOrEmpty(e.id)) return false;
                if (!MergerSync.IAmMember) return false;                        // no company: the pool is mine alone
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return false;
                string id = e.id;
                bool isCand = false; try { isCand = e.IsCandidate; } catch { }
                if (!isCand) return false;                                      // rival poaching keeps its own claim (RivalStaffSync)
                if (!IsInjectedCandidate(id) && !IsMyPublishedCandidate(id)) return false;
                string claimant = ClaimantOf(id);
                if (claimant == MPConfig.PlayerId) return false;                // already ours - open it natively
                if (claimant.Length > 0)
                {
                    if (_logged.Add("claimed|" + id + "|" + claimant))
                        Plugin.Logger.LogInfo($"{Tag} '{id}' is already being talked to by '{claimant}' - this machine does not open a second negotiation.");
                    return true;
                }
                if (_pending.ContainsKey(id)) return true;                      // asked a moment ago
                _pending[id] = Time.unscaledTime;
                Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "claim", CandidateId = id, OwnerPid = OwnerOfCandidate(id) });
                Plugin.Logger.LogInfo($"{Tag} claiming '{id}' from the host before opening a negotiation.");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} defer negotiation: {ex.GetType().Name}: {ex.Message}"); return false; }
        }

        // -- T2: the hire itself (review r1 MAJOR-3) --

        private static readonly Dictionary<string, (CandidateSalaryNegotiation neg, float wage, float bonus)> _pendingAccept = new();
        private static bool _hiring;     // re-running the native accept on a GRANTED verdict

        /// <summary>THE HIRE SEAM. CandidateSalaryNegotiation.AcceptOffer is where a candidate becomes an
        /// employee, and until now only a RIVAL negotiation was gated there (RivalStaffSync.cs:222 returns
        /// false for anything else), so two members could both accept the same company candidate and end up
        /// with one employee id in two saves. The shape is the poach claim's, exactly: swallow the native
        /// call, ask the host, and re-invoke it flag-gated on a GRANT. True = swallowed here.</summary>
        public static bool DeferAcceptToHost(CandidateSalaryNegotiation neg, float wage, float bonus)
        {
            try
            {
                if (_hiring) return false;                                       // completing a granted hire
                if (neg == null || neg.employeeInstance == null) return false;
                if (neg.isRival) return false;                                   // the poach claim owns that one
                if (!MergerSync.IAmMember) return false;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return false;
                string id = neg.employeeInstance.id ?? "";
                if (id.Length == 0) return false;
                // U3(a) (re-check r3 MAJOR-3): an ORPHAN negotiation - one whose candidate is NEITHER in this
                // save's own candidate list NOR a live company copy - can never be accepted. The gate used to
                // fire only while a COPY was present, so a member who negotiated, dropped, reconnected (copy
                // gone, and before r3 no held notice either) and pressed Accept ran the game's own hire and
                // minted a SECOND real employee with somebody else's id: EmployeeHelper.cs:531 guards only
                // GetEmployeeInstances().Contains(candidate), which an id hired on another machine passes.
                // Closed through the poach-refusal path - the dialog goes, with no new wording anywhere.
                bool copy = IsInjectedCandidate(id);
                if (!copy && !IsMyPublishedCandidate(id))
                {
                    Plugin.Logger.LogWarning($"{Tag} accept of '{id}' REFUSED here - that candidate is no longer in my list or in the company pool (somebody else has them); the negotiation is closed.");
                    try { neg.completed = true; neg.accepted = false; } catch { }
                    RefreshIfOpen();
                    return true;
                }
                if (!copy) return false;                                         // my own record: nobody else can hold it
                if (_pendingAccept.ContainsKey(id)) return true;                 // asked a moment ago
                _pendingAccept[id] = (neg, wage, bonus);
                Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "accept", CandidateId = id, OwnerPid = OwnerOfCandidate(id) });
                Plugin.Logger.LogInfo($"{Tag} accept of '{id}' deferred - asking the host whether this company candidate is still free.");
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} defer accept: {ex.GetType().Name}: {ex.Message}"); return false; }
        }

        /// <summary>The host's answer to an accept. GRANTED re-runs the game's own AcceptOffer end to end
        /// (hire, wage, bonus charge); REFUSED closes the negotiation exactly as the poach refusal does -
        /// the dialog goes away with no new wording anywhere (RivalStaffSync.OnPoachResult:158-165).</summary>
        private static void ApplyHireVerdict(CompanyCandidatesPayload p)
        {
            string id = p.CandidateId ?? "";
            if (id.Length == 0) return;
            if (!_pendingAccept.TryGetValue(id, out var pend)) return;
            _pendingAccept.Remove(id);
            if (p.Ok)
            {
                Plugin.Logger.LogInfo($"{Tag} hire of '{id}' GRANTED by the host - completing the game's own accept.");
                try { _hiring = true; pend.neg.AcceptOffer(pend.wage, pend.bonus); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} accept after grant: {ex.GetType().Name}: {ex.Message}"); }
                finally { _hiring = false; }
                return;
            }
            Plugin.Logger.LogWarning($"{Tag} hire of '{id}' REFUSED by the host - somebody in the company got there first; the negotiation is closed here.");
            try { pend.neg.completed = true; pend.neg.accepted = false; } catch { }
            RefreshIfOpen();
        }

        /// <summary>U3(c) (re-check r3 MAJOR-3): an ORPHAN negotiation is one whose candidate is neither in
        /// this save's own candidate list nor a live company copy - the copy went (the owner stopped listing
        /// them, somebody else hired them, a reconnect wiped the copies) while the negotiation stayed open.
        /// Nothing completed those: RemoveInjected(destroy:true) and ClearAll only take the RECORD away, and
        /// they deliberately still do - the negotiation belongs to this sweep, which is the one place that
        /// can tell "gone because somebody hired them" from "gone for a moment during a join".
        /// The close is the GAME's own: EmployeeHelper.DiscardCandidate -> FinishPendingNegotiation
        /// (decompile Helpers/EmployeeHelper.cs:551-567) sets completed/accepted exactly as declining does.
        /// Gated on membership AND on the join window being over (a client: the first pool has arrived;
        /// the host: my own pool has been published once - no pool ever 'arrives' there), so the join
        /// window - when every copy is legitimately still missing - can never close a valid negotiation.
        /// Narrowed further to ids this session actually held as a company copy (_everCopied), so a
        /// candidate of MY OWN whose contacts message was deleted is never swept.
        /// Rival/poach negotiations are left alone: RivalStaffSync owns those.</summary>
        private static void SweepOrphanNegotiations()
        {
            try
            {
                if (!MergerSync.IAmMember) return;
                // r3 MINOR-2: on the HOST every pool is local, so nothing ever "arrives" - one own publish is
                // the same proof the join window is over. On a client the first incoming pool still arms it.
                if (!_poolSeen && !(MPServer.IsRunning && _publishedOnce)) return;
                var list = SaveGameManager.Current?.candidateSalaryNegotiations;
                if (list == null || list.Count == 0) return;
                List<EmployeeInstance> orphans = null;
                foreach (var n in list)
                {
                    if (n == null || n.completed || n.employeeInstance == null) continue;
                    bool rival = false; try { rival = n.isRival || n.isPoached; } catch { }
                    if (rival) continue;
                    string id = n.employeeInstance.id ?? "";
                    if (id.Length == 0) continue;
                    // r3 MINOR-4: ONLY an id this session actually held as a company copy. One of MY OWN
                    // candidates whose contacts message was deleted also looks orphaned, and closing that
                    // negotiation is not this sweep's job.
                    if (!_everCopied.Contains(id)) continue;
                    if (IsInjectedCandidate(id) || IsMyPublishedCandidate(id)) continue;
                    (orphans ??= new List<EmployeeInstance>()).Add(n.employeeInstance);
                }
                if (orphans == null) return;
                foreach (var inst in orphans)
                {
                    string id = inst.id ?? "";
                    try { EmployeeHelper.DiscardCandidate(inst); }
                    catch (Exception dx) { Plugin.Logger.LogWarning($"{Tag} closing the stale negotiation for '{id}': {dx.GetType().Name}: {dx.Message}"); continue; }
                    SetClaim(id, "");
                    Plugin.Logger.LogWarning($"{Tag} closed a stale negotiation for '{id}' - that candidate is neither in my list nor in the company pool any more, so nobody can be hired twice.");
                }
                RefreshIfOpen();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} stale-negotiation sweep: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static bool IsMyPublishedCandidate(string id)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.CandidateEmployeeInstances == null) return false;
                foreach (var c in gi.CandidateEmployeeInstances) if (c?.id == id) return true;
            }
            catch { }
            return false;
        }

        // -- hire / discard --

        /// <summary>The game's own hire ran here. On a partner's copy the record has just become THIS
        /// save's real employee (the one path by which a partner's person may become a native record), so
        /// the tag comes off and the ORIGIN is told to drop its own copy. The adopt migration does the
        /// rest when the new hire is pointed at a partner's shop.</summary>
        public static void OnHired(EmployeeInstance candidate)
        {
            try
            {
                string id = candidate?.id ?? "";
                if (id.Length == 0) return;
                if (_injected.TryGetValue(id, out var have))
                {
                    _injected.Remove(id);                                        // keep the RECORD: it is ours now
                    foreach (var kv in _poolByOwner) kv.Value.Remove(id);
                    Plugin.Logger.LogInfo($"{Tag} hired '{id}' out of '{have.owner}'s pool - the record is this save's now; telling the origin to drop theirs.");
                    Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "hired", CandidateId = id, OwnerPid = have.owner });
                }
                else if (!IsMyPublishedCandidate(id)) { SetClaim(id, ""); return; }
                SetClaim(id, "");
                PublishNow();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} OnHired: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>THE ORIGIN: a co-member hired somebody from my pool - run the game's own discard on my
        /// record so it leaves my list exactly as a declined candidate does.</summary>
        private static void OnPartnerHiredMine(string id, string byPid)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return;
                if (_injected.ContainsKey(id)) return;                           // not mine to discard
                EmployeeInstance mine = null;
                var gi = SaveGameManager.Current;
                if (gi?.CandidateEmployeeInstances != null)
                    foreach (var c in gi.CandidateEmployeeInstances) if (c?.id == id) { mine = c; break; }
                SetClaim(id, "");
                if (mine == null) { Plugin.Logger.LogInfo($"{Tag} '{byPid}' hired '{id}' - not in my pool any more, nothing to drop."); return; }
                try { EmployeeHelper.DiscardCandidate(mine); }
                catch (Exception dx) { Plugin.Logger.LogWarning($"{Tag} discard of '{id}' after '{byPid}' hired them: {dx.GetType().Name}: {dx.Message}"); }
                Plugin.Logger.LogInfo($"{Tag} '{byPid}' hired '{id}' out of my pool - my own candidate record discarded.");
                PublishNow();
                RefreshIfOpen();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} OnPartnerHiredMine: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>A discard aimed at a PARTNER's copy: the candidate is not ours to destroy. The game's
        /// own DECLINE path ends here - but deleting the negotiation MESSAGE does NOT (review r1 MAJOR-5):
        /// ContactsApp.RemoveMessage (decompile :456) and Contact.CleanOldMessages (decompile :174) each
        /// strike the instance out of CandidateEmployeeInstances and the dictionary by hand, without ever
        /// calling DiscardCandidate, so this guard never sees them - RepairAfterMessagePurge below is that
        /// half. The copy stays, the local negotiation is closed so the claim can go back to the pool, and
        /// the origin keeps them. True = the native discard must not run.</summary>
        public static bool RefuseDiscard(EmployeeInstance candidate)
        {
            try
            {
                string id = candidate?.id ?? "";
                if (id.Length == 0 || !_injected.TryGetValue(id, out var have)) return false;
                try
                {
                    var list = SaveGameManager.Current?.candidateSalaryNegotiations;
                    if (list != null)
                        foreach (var n in list)
                            if (n != null && n.employeeInstance != null && n.employeeInstance.id == id) { n.completed = true; n.accepted = false; }
                }
                catch { }
                if (ClaimantOf(id) == MPConfig.PlayerId)
                {
                    SetClaim(id, "");
                    Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "release", CandidateId = id, OwnerPid = have.owner });
                }
                Plugin.Logger.LogInfo($"{Tag} declined '{id}' - they belong to '{have.owner}'s pool, so the record stays there and the claim is released.");
                RefreshIfOpen();
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} RefuseDiscard: {ex.GetType().Name}: {ex.Message}"); return false; }
        }

        /// <summary>U4 (re-check r3 MAJOR-4): TRUE only while ContactsApp.RemoveMessage runs. The removal
        /// itself sits in a LOCAL function (decompile UI.../ContactsApp.cs:424 -> RemoveMessageFunction at
        /// :456) which is called inline ONLY under PerformActionWithoutConfirm.Pressing(); the normal path
        /// hands that function to HudConfirm.Show as the confirm action, and it runs LONG AFTER RemoveMessage
        /// has returned - so the postfix below was repairing before anything had been removed. This flag lets
        /// the shared HudConfirm wrapper (SharedShopStaff.Patch_HudConfirm_MassTrainWindow, the TRAIN
        /// mechanism reused verbatim) run the repair AFTER the confirmed action instead.</summary>
        public static bool MessagePurgeArmed { get; private set; }

        public static void ArmMessagePurge(bool on) => MessagePurgeArmed = on;

        /// <summary>T4: the two DIRECT removals put a partner's copy out of both lists without a discard,
        /// which would otherwise destroy the company's view of a candidate the origin still holds (and
        /// leave the claim standing). Called right after each of them: anything of ours that vanished goes
        /// straight back, and a claim we hold with no negotiation left is released by the 5 s sweep.</summary>
        public static void RepairAfterMessagePurge()
        {
            try
            {
                if (_injected.Count == 0) return;
                var gi = SaveGameManager.Current;
                if (gi?.CandidateEmployeeInstances == null) return;
                int back = 0;
                foreach (var kv in _injected)
                {
                    string id = kv.Key;
                    bool there = false;
                    for (int i = 0; i < gi.CandidateEmployeeInstances.Count; i++)
                        if (gi.CandidateEmployeeInstances[i]?.id == id) { there = true; break; }
                    if (there) continue;
                    gi.CandidateEmployeeInstances.Add(kv.Value.inst);
                    try { EmployeeHelper.EmployeeInstancesDictionary[id] = kv.Value.inst; } catch { }
                    back++;
                }
                if (back > 0)
                {
                    Plugin.Logger.LogInfo($"{Tag} a deleted message took {back} company candidate copy(ies) with it - put back (the record belongs to the origin).");
                    RefreshIfOpen();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} message-purge repair: {ex.GetType().Name}: {ex.Message}"); }
        }

        // -- lifecycle / strips --

        /// <summary>U3(d): this deliberately leaves candidateSalaryNegotiations ALONE. Completing a
        /// negotiation here would close one during the join window, when every copy is legitimately missing
        /// for a moment; SweepOrphanNegotiations is the gated place that can tell the two apart.</summary>
        private static void RemoveInjected(string id, bool destroy)
        {
            if (!_injected.TryGetValue(id, out var have)) return;
            _injected.Remove(id);
            _claims.Remove(id);
            _keepalive.Remove(id);
            _pending.Remove(id);
            if (!destroy) return;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.CandidateEmployeeInstances != null) gi.CandidateEmployeeInstances.Remove(have.inst);
            }
            catch { }
            try { EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
        }

        public static void ClearAll(string why)
        {
            try
            {
                var ids = new List<string>(_injected.Keys);
                foreach (var id in ids) RemoveInjected(id, destroy: true);
                _poolByOwner.Clear(); _claims.Clear(); _pending.Clear(); _keepalive.Clear(); _everCopied.Clear(); _reassertOnly.Clear();
                _publishedOnce = false; _poolSeen = false;   // RIG-3 / U3(c): both gates re-arm on the next connection
                _sigSent = null;   // r4 re-check: a reconnect must re-publish the pool even when its signature is unchanged (the host's store may be stale)
                if (ids.Count > 0) { Plugin.Logger.LogInfo($"{Tag} dropped {ids.Count} company candidate copy(ies) ({why})."); RefreshIfOpen(); }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} ClearAll: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Lift every injected copy out of the candidate list for the duration of a native pass
        /// or a save, and hand back what to put back. Two callers: the hourly pass that ages and DELETES
        /// candidates (their clock is the origin's) and the save choke point (a copy never reaches a .hsg).</summary>
        public static List<EmployeeInstance> StripInjected(string context)
        {
            var stripped = new List<EmployeeInstance>();
            try
            {
                if (_injected.Count == 0) return stripped;
                var list = SaveGameManager.Current?.CandidateEmployeeInstances;
                if (list == null) return stripped;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    string id = list[i]?.id ?? "";
                    if (id.Length == 0 || !_injected.ContainsKey(id)) continue;
                    stripped.Add(list[i]);
                    list.RemoveAt(i);
                    try { EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
                }
                if (stripped.Count > 0 && _logged.Add("strip|" + context))
                    Plugin.Logger.LogInfo($"{Tag} {stripped.Count} company candidate copy(ies) sit out {context} (their expiry clock is the origin's).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} strip ({context}): {ex.GetType().Name}: {ex.Message}"); }
            return stripped;
        }

        private static List<EmployeeInstance> _hourlyStash;

        /// <summary>The hourly pass' half of the pair above. Harmony allows one __state per patch and the
        /// employee strip already uses it, so the candidate stash lives here; the pass is main-thread and
        /// non-reentrant, and a restore with nothing stashed is a no-op.</summary>
        public static void StripForHourlyPass() => _hourlyStash = StripInjected("the hourly employee pass");

        public static void RestoreAfterHourlyPass()
        {
            var s = _hourlyStash; _hourlyStash = null;
            RestoreInjected(s, "the hourly employee pass");
        }

        public static void RestoreInjected(List<EmployeeInstance> stripped, string context)
        {
            try
            {
                if (stripped == null || stripped.Count == 0) return;
                var list = SaveGameManager.Current?.CandidateEmployeeInstances;
                if (list == null) return;
                foreach (var c in stripped)
                {
                    if (c == null || string.IsNullOrEmpty(c.id)) continue;
                    bool there = false;
                    for (int i = 0; i < list.Count; i++) if (list[i]?.id == c.id) { there = true; break; }
                    if (!there) list.Add(c);
                    try { EmployeeHelper.EmployeeInstancesDictionary[c.id] = c; } catch { }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} restore ({context}): {ex.GetType().Name}: {ex.Message}"); }
        }

        private static void RefreshIfOpen()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var app = ui != null && ui.fullMenu != null ? ui.fullMenu.myEmployees : null;
                if (app != null && app.gameObject.activeInHierarchy) app.RefreshList();
            }
            catch { }
        }

        public static void Reset()
        {
            _injected.Clear(); _poolByOwner.Clear(); _claims.Clear(); _pending.Clear(); _keepalive.Clear(); _everCopied.Clear();
            _reassertOnly.Clear();
            _logged.Clear(); _sigSent = null; _committing = false; _nextTick = 0f;
            _pendingAccept.Clear(); _verbHeld.Clear(); _hiring = false;
            _publishedOnce = false; _poolSeen = false; MessagePurgeArmed = false;
        }

        /// <summary>TestDrive readout: one line per candidate this machine can see.</summary>
        public static List<string> Readout()
        {
            var outp = new List<string>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.CandidateEmployeeInstances == null) return outp;
                foreach (var c in gi.CandidateEmployeeInstances)
                {
                    if (c == null || string.IsNullOrEmpty(c.id)) continue;
                    string nm = ""; try { nm = c.characterData?.name ?? ""; } catch { }
                    int hrs = 0; try { hrs = c.candidateInfo?.hoursUntilExpiring ?? 0; } catch { }
                    float wage = 0f; try { wage = c.hourlyWage; } catch { }
                    string owner = OwnerOfCandidate(c.id);
                    outp.Add($"{c.id}|{nm}|wage={wage.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|expires={hrs}"
                           + $"|origin={(owner.Length > 0 ? owner : "mine")}|claimed={ClaimantOf(c.id)}");
                }
            }
            catch { }
            return outp;
        }

        /// <summary>TestDrive lever: ask the host for this candidate exactly as opening a negotiation does.</summary>
        public static bool CommitClaim(string candidateId)
        {
            try
            {
                if (string.IsNullOrEmpty(candidateId)) return false;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return false;
                _pending[candidateId] = Time.unscaledTime;
                _reassertOnly.Remove(candidateId);              // the harness lever stands in for a real click: it DOES open
                Send(new CompanyCandidatesPayload { PlayerId = MPConfig.PlayerId, Action = "claim", CandidateId = candidateId, OwnerPid = OwnerOfCandidate(candidateId) });
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} CommitClaim: {ex.GetType().Name}: {ex.Message}"); return false; }
        }
    }

    // -- patches ---------------------------------------------------------------

    /// <summary>THE CLAIM SEAM. MyEmployees.NegotiateWithCandidate is the one entry that mints a
    /// CandidateSalaryNegotiation (decompile :600-613). On a company candidate the call is swallowed and
    /// re-run flag-gated when the host grants - the RivalStaffSync.DeferAcceptToHost shape.</summary>
    [HarmonyPatch(typeof(MyEmployees), nameof(MyEmployees.NegotiateWithCandidate))]
    public static class Patch_MyEmployees_Negotiate_ClaimAtHost
    {
        static bool Prefix(EmployeeInstance employeeInstance)
        {
            try { return !CompanyCandidates.DeferNegotiationToHost(employeeInstance); }
            catch { return true; }
        }
    }

    /// <summary>A partner's candidate is never DESTROYED here (the decline path, and deleting the
    /// negotiation message, both end in this one call).</summary>
    [HarmonyPatch(typeof(EmployeeHelper), nameof(EmployeeHelper.DiscardCandidate))]
    public static class Patch_EmployeeHelper_Discard_CompanyGuard
    {
        static bool Prefix(EmployeeInstance candidate)
        {
            try { return !CompanyCandidates.RefuseDiscard(candidate); }
            catch { return true; }
        }
    }

    /// <summary>The hire commit (decompile Helpers/EmployeeHelper.cs:529-549): a partner's copy has just
    /// become this save's real employee - drop the tag and tell the origin.</summary>
    [HarmonyPatch(typeof(EmployeeHelper), nameof(EmployeeHelper.HireCandidate))]
    public static class Patch_EmployeeHelper_Hire_CompanyConsume
    {
        static void Postfix(EmployeeInstance candidate)
        {
            try { CompanyCandidates.OnHired(candidate); } catch { }
        }
    }

    /// <summary>THE HIRE SEAM (T2). The rival poach already claims this method for AI-shop staff and
    /// returns false for everything else; a COMPANY candidate is claimed here, on the same shape.</summary>
    [HarmonyPatch(typeof(CandidateSalaryNegotiation), nameof(CandidateSalaryNegotiation.AcceptOffer))]
    public static class Patch_SalaryNegotiation_Accept_CompanyClaim
    {
        static bool Prefix(CandidateSalaryNegotiation __instance, float hourlyWageAmount, float bonusAmount)
        {
            try { return !CompanyCandidates.DeferAcceptToHost(__instance, hourlyWageAmount, bonusAmount); }
            catch { return true; }
        }
    }

    /// <summary>T4, repaired by U4 (re-check r3 MAJOR-4). Deleting ONE message: the removal sits in a local
    /// function inside RemoveMessage (decompile ContactsApp.cs:424 -> :456) which no prefix can gate on its
    /// own, so the copy is put back afterwards instead. But that local function is called INLINE only under
    /// PerformActionWithoutConfirm.Pressing() - the ordinary click goes through HudConfirm.Show and runs it
    /// only when the player confirms, LONG AFTER this method has returned. So this patch does both halves:
    /// the finalizer repairs the inline path, and the arming flag lets the shared HudConfirm wrapper repair
    /// the confirm path once the confirmed action has actually run.</summary>
    [HarmonyPatch(typeof(UI.Smartphone.Apps.Contacts.ContactsApp), "RemoveMessage")]
    public static class Patch_ContactsApp_RemoveMessage_KeepCompanyCopy
    {
        static void Prefix() { try { CompanyCandidates.ArmMessagePurge(true); } catch { } }

        static void Finalizer()
        {
            try { CompanyCandidates.RepairAfterMessagePurge(); } catch { }
            try { CompanyCandidates.ArmMessagePurge(false); } catch { }
        }
    }

    /// <summary>T4: the AUTO prune of old messages (decompile Entities/Contact.cs:148 -> :174) does the
    /// same removal on a timer, so the same repair follows it.</summary>
    [HarmonyPatch(typeof(Entities.Contact), nameof(Entities.Contact.CleanOldMessages))]
    public static class Patch_Contact_CleanOldMessages_KeepCompanyCopy
    {
        static void Postfix()
        {
            try { CompanyCandidates.RepairAfterMessagePurge(); } catch { }
        }
    }

    /// <summary>Candidate row: a claimed person carries the CLAIMANT's own player colour (the same
    /// rich-text tag the My Employees rows use for a partner's staff - PlayerColours.TagOpen) and their
    /// mass-action checkbox greys out (the ruling-23 rendering). No new words anywhere.</summary>
    [HarmonyPatch(typeof(CandidateCellView), nameof(CandidateCellView.SetData))]
    public static class Patch_CandidateCellView_SetData_Claim
    {
        static void Postfix(CandidateCellView __instance, CandidateModel data)
        {
            try
            {
                if (__instance == null || data == null) return;
                string id = data.employeeInstance?.id ?? "";
                if (id.Length == 0) return;
                string claimant = CompanyCandidates.ClaimantOf(id);
                // MINOR-6 r2: native CandidateCellView.SetData never touches interactable, so a cell
                // recycled from a claimed row kept the greyed checkbox. It is assigned on EVERY pass now,
                // the way the My Employees rows do it (SharedShopStaff.cs:733-736).
                if (__instance.massActionToggle != null)
                    __instance.massActionToggle.interactable = claimant.Length == 0 || claimant == MPConfig.PlayerId;
                if (claimant.Length == 0) return;
                if (__instance.employeeName != null)
                    __instance.employeeName.text = PlayerColours.TagOpen(claimant) + __instance.employeeName.text + "</color>";
            }
            catch { }
        }
    }
}
