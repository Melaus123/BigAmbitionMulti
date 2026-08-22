using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Buildings;                               // BuildingRegistration
using Entities;                                // EmployeeInstance, TodoTaskType, CustomerDemandHelper
using HarmonyLib;
using Helpers;                                 // BuildingHelper, EmployeeHelper
using UI.Smartphone.Apps.MyEmployees;          // MyEmployees, EmployeeScrollerController, EmployeeModel
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// SHARED-SHOP MANAGEMENT (the Business PERMISSION feature) — slice 3: staffing.
    /// Plan: .modding/03-systems/shared-shop-management-plan.md §2.3; rulings 3, 12, 14, 17, 19.
    ///
    /// THIS IS NOT THE MERGER. The merger's employee code (MergerEmployeeSync: fire routing, adopt/migration) is
    /// untouched; everything here keys on "a record copied from an owner who DIRECTLY grants me Business permission".
    ///
    /// What a helper gets:
    ///  • THE OWNER'S BENCH. The owner publishes their hired-but-unassigned employees (SharedStaffPool) whenever that
    ///    set's membership or wages change; the host forwards it only to players holding a direct Business grant from
    ///    that owner (and replays it to a newly granted player). The receiver injects them as real-id records with no
    ///    assigned business (MPRegisterSync.ApplySharedPool) — the same kind of record the roster sync already uses
    ///    for the owner's ASSIGNED staff, so every existing skip (payroll, save strip, goals, specialist lists)
    ///    inherits. Rule 3: only THAT owner's people, never the helper's own, never a third party's.
    ///  • MY EMPLOYEES. The owner's people appear in the helper's My Employees list (teal), and for one of them the
    ///    "assigned business" dropdown offers ONLY that owner's shared shops (the game's own skill filter still
    ///    applies). The game writes the choice to the local copy; a 2 s scan sees the copy disagree with the owner's
    ///    published state and routes "assign"/"unassign" (SharedStaffEdit) to the owner, who performs the native
    ///    reassignment and republishes roster + bench — the copies converge. The helper's OWN employees never see a
    ///    shared shop in that dropdown; if any path still points one at a shared shop, the scan reverts it.
    ///  • GREYED / BLOCKED on the owner's people (rulings 14, 19 + the money ruling): pay bonus, training (the train
    ///    buttons are not offered), fire (button greyed and the action blocked), mass-action selection (toggle greyed);
    ///    no to-do entries about the owner's staff are written into the helper's save.
    ///  • AUTO-FILL on a shared shop works with the owner's staff (the auto-fill guard exempts them).
    ///  • Nothing on screen beyond colour and greying (ruling 17).
    /// </summary>
    public static class SharedShopStaff
    {
        private const string Tag = "[SharedShop]";
        private const float OwnerTickSeconds = 5f;
        private const float ScanSeconds = 2f;
        private const float PendingHoldSeconds = 15f;
        private const int MaxResends = 3;   // unanswered sends before the local copy gives up and reverts to the owner's state

        // ── state ──
        private static float _nextOwnerTick, _nextScan;
        private static string _poolSigSent = "";
        private static readonly Dictionary<string, (string target, float at, int tries)> _inflight = new();   // employeeId → what we sent, when, how many times
        private static readonly Dictionary<string, int> _seq = new();                               // employeeId → last seq (NEVER cleared)
        private static readonly int _seqEpoch = new System.Random().Next(1, int.MaxValue);
        private static readonly Dictionary<string, (int epoch, int seq)> _appliedSeq = new();       // owner: "employee|pid" → last applied
        private static readonly HashSet<string> _logged = new();

        /// <summary>True while EmployeeScrollerController.PopulateAllModels runs — the ONE list that may show the
        /// owner's people (MPPatches' global employee filter and MyEmployees filter consult this).</summary>
        public static bool ListScope { get; private set; }
        /// <summary>True while MyEmployees builds the assigned-business dropdown for one of the owner's people — the
        /// MPPatches foreign-shop filter on that dropdown stands down so the owner's shared shops can be offered.</summary>
        public static bool DropdownForGrantRecord { get; private set; }
        private static MyEmployees _dropdownPage;

        private static Color Tint => HousingMapCues.SharedColor;

        // ── identity ──

        /// <summary>A record copied from an owner who DIRECTLY grants me Business permission (merger membership does not count).</summary>
        public static bool IsFromGrantOwner(string employeeId)
        {
            try
            {
                if (string.IsNullOrEmpty(employeeId) || !MPRegisterSync.IsInjectedStaff(employeeId)) return false;   // a live copy, never a memory of one
                string owner = MPRegisterSync.OwnerOfInjected(employeeId);
                if (owner.Length == 0 || owner == MPConfig.PlayerId) return false;
                return GrantSync.IsGrantedDirect(GrantKind.Business, owner, MPConfig.PlayerId);
            }
            catch { return false; }
        }

        public static bool ShowInMyEmployees(string employeeId) => ListScope && IsFromGrantOwner(employeeId);

        /// <summary>Auto-fill on a shared shop may use the OWNER's copied staff (theirs, at their shop) — nobody else's.</summary>
        public static bool AllowedInAutoFill(string employeeId, BuildingRegistration reg)
        {
            try
            {
                if (reg == null || !IsFromGrantOwner(employeeId)) return false;
                string addr = GameStateReader.AddressKey(reg);
                if (!SharedShopSchedule.IsSharedShop(reg, addr)) return false;
                string stamp = reg.businessOwnerRivalId?.ToString() ?? "";
                return stamp == MPRegisterSync.OwnerOfInjected(employeeId);
            }
            catch { return false; }
        }

        private static string AddrOf(Address a)
        {
            try { return a != null ? GameStateReader.AddressKey(a) : ""; } catch { return ""; }
        }

        // ── tick ──

        /// <summary>MAIN THREAD (MPCanvasUI.Update).</summary>
        public static void Tick()
        {
            try { TickOwner(); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} bench publish: {ex.Message}"); }
            try { TickAssignScan(); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} assignment scan: {ex.Message}"); }
        }

        // ── owner: publish my bench ──

        /// <summary>Republish my bench on the next tick (after a routed assign/unassign, or when my grantees change).</summary>
        public static void PublishPoolNow() { _poolSigSent = ""; _nextOwnerTick = 0f; }

        private static void TickOwner()
        {
            if (Time.unscaledTime < _nextOwnerTick) return;
            _nextOwnerTick = Time.unscaledTime + OwnerTickSeconds;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            if (!GrantSync.GrantsAnyone(GrantKind.Business, MPConfig.PlayerId)) { _poolSigSent = ""; return; }   // nobody to send it to — no walk, no message
            var gi = SaveGameManager.Current;
            if (gi?.EmployeeInstances == null) return;
            var staff = new List<StaffInfo>();
            foreach (var e in gi.EmployeeInstances)
            {
                if (e == null || string.IsNullOrEmpty(e.id)) continue;
                if (MPRegisterSync.IsSyntheticDuty(e.id) || MPRegisterSync.IsInjectedStaff(e.id)) continue;   // stand-ins / other players' copies are not my bench
                bool candidate = false; try { candidate = e.IsCandidate; } catch { }
                if (candidate) continue;                                                                    // hiring is not shared
                bool assigned = false; try { assigned = e.IsAssignedToAnyBusiness(); } catch { }
                if (assigned) continue;                                                                     // the roster sync carries these
                staff.Add(MPRegisterSync.StaffInfoOf(e));
            }
            staff.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            // Signature = membership + name + wage only. NOT satisfaction/availability — those drift every morale
            // tick and would turn this into a chatty broadcast (the lesson from the roster publish, plan §2.9).
            var sb = new System.Text.StringBuilder();
            foreach (var s in staff) sb.Append(s.Id).Append('|').Append(s.Name).Append('|').Append(s.Wage.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            string sig = sb.ToString();
            if (sig == _poolSigSent) return;
            bool first = _poolSigSent.Length == 0;
            _poolSigSent = sig;
            MPRegisterSync.NudgeRosterPublish();   // whoever left the bench is on a roster now — ship that sweep alongside, not 30 s later
            var p = new SharedStaffPoolPayload { PlayerId = MPConfig.PlayerId, Staff = staff };
            if (MPServer.IsRunning) MPServer.HostRouteSharedStaffPool(p, MPConfig.PlayerId);
            else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedStaffPool, MPConfig.PlayerId, p));
            if (!first || staff.Count > 0)
                Plugin.Logger.LogInfo($"{Tag} published my unassigned staff: {staff.Count} (the host hands it only to players I share shops with).");
        }

        // ── receiver: the owner's bench arrives ──

        /// <summary>MAIN THREAD. The bench of an owner — accepted only from an owner who directly grants me.</summary>
        public static void ApplyPool(SharedStaffPoolPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.PlayerId) || p.PlayerId == MPConfig.PlayerId) return;
                if (!GrantSync.IsGrantedDirect(GrantKind.Business, p.PlayerId, MPConfig.PlayerId))
                {
                    if (_logged.Add("pool-nogrant|" + p.PlayerId))
                        Plugin.Logger.LogInfo($"{Tag} bench from '{p.PlayerId}' arrived but they share no shop with me — ignored.");
                    return;
                }
                var staff = p.Staff ?? new List<StaffInfo>();
                if (staff.Count > 200) { Plugin.Logger.LogWarning($"{Tag} bench from '{p.PlayerId}': implausible count {staff.Count} — ignored."); return; }
                if (MPRegisterSync.ApplySharedPool(p.PlayerId, staff)) RefreshMyEmployeesIfOpen();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} ApplyPool: {ex.Message}"); }
        }

        private static void RefreshMyEmployeesIfOpen()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var app = ui != null && ui.fullMenu != null ? ui.fullMenu.myEmployees : null;
                if (app != null && app.gameObject.activeInHierarchy) app.RefreshList();
            }
            catch { }
        }

        // ── editor: detect assignment changes on the owner's copies, route them ──

        private static void TickAssignScan()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            bool refresh = MPRegisterSync.SweepBenchGrace() > 0;   // bench records nobody claimed in time — runs BEFORE the early-out so the grace map never orphans
            if (MPRegisterSync.InjectedCount == 0 && _inflight.Count == 0) { if (refresh) RefreshMyEmployeesIfOpen(); return; }
            var gi = SaveGameManager.Current;
            if (gi?.EmployeeInstances == null) return;
            var snapshot = new List<EmployeeInstance>(gi.EmployeeInstances);   // the scan may drop records
            foreach (var e in snapshot)
            {
                if (e == null || string.IsNullOrEmpty(e.id) || MPRegisterSync.IsSyntheticDuty(e.id)) continue;
                string local = AddrOf(e.assignedAddress);
                if (!MPRegisterSync.IsInjectedStaff(e.id))
                {
                    // MY OWN employee pointed at a shop I only manage for someone else — never allowed (ruling 3).
                    if (local.Length > 0)
                    {
                        var reg = FindReg(local, gi);
                        if (reg != null && SharedShopSchedule.IsSharedShop(reg, local))
                        {
                            try { EmployeeHelper.UnassignEmployeeFromAllWorkshifts(e); } catch { }   // no shifts left dangling on the shared shop's copy
                            try { e.assignedAddress = null; } catch { }
                            Plugin.Logger.LogInfo($"{Tag} my employee '{SafeName(e)}' was pointed at shared shop '{local}' — not allowed, set back to unassigned.");
                            refresh = true;
                        }
                    }
                    continue;
                }
                string owner = MPRegisterSync.OwnerOfInjected(e.id);
                if (owner.Length == 0)
                {
                    // Unreachable today (every bench-record path records the owner in the same frame) — a bench record
                    // without an owner can be neither routed nor reverted, so it is dropped rather than kept forever.
                    if (MPRegisterSync.IsInjectedUnassigned(e.id))
                    {
                        MPRegisterSync.DropInjectedStaff(e.id); _inflight.Remove(e.id); refresh = true;
                        Plugin.Logger.LogWarning($"{Tag} bench record '{SafeName(e)}' has no owner on record — dropped.");
                    }
                    continue;
                }
                if (!GrantSync.IsGrantedDirect(GrantKind.Business, owner, MPConfig.PlayerId))
                {
                    // The grant is gone (owner offline / revoked): their bench leaves my machine; roster copies keep
                    // their own lifecycle (the roster sync has always handled those).
                    if (MPRegisterSync.IsInjectedUnassigned(e.id)) { MPRegisterSync.DropInjectedStaff(e.id); refresh = true; }
                    _inflight.Remove(e.id);
                    continue;
                }
                string known = MPRegisterSync.InjectedAddrOf(e.id);   // "" = on the bench, else the shop the owner's roster says
                if (local == known) { _inflight.Remove(e.id); continue; }
                // The local copy disagrees with the owner's published state → a change made here (the dropdown).
                if (local.Length > 0)
                {
                    var reg = FindReg(local, gi);
                    string stamp = ""; try { stamp = reg?.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                    if (reg == null || !SharedShopSchedule.IsSharedShop(reg, local) || stamp != owner || IsWarehouse(reg))
                    {
                        // Not one of THAT owner's staffable shared shops (e.g. my own shop, or their warehouse — driver
                        // slots are a later slice) — revert to the owner's state.
                        if (!RevertToKnown(e, known, gi)) continue;   // shop not resolvable right now — retried next scan
                        _inflight.Remove(e.id);
                        Plugin.Logger.LogInfo($"{Tag} '{SafeName(e)}' (employee of '{owner}') cannot be assigned to '{local}' — reverted.");
                        refresh = true;
                        continue;
                    }
                }
                int tries = 1;
                if (_inflight.TryGetValue(e.id, out var f) && f.target == local)
                {
                    if (Time.unscaledTime - f.at < PendingHoldSeconds) continue;
                    if (f.tries >= MaxResends)
                    {
                        // The owner never confirmed — e.g. the employee was fired a moment before this assignment, so the
                        // owner no longer knows the id and has nothing to republish. The local copy goes back to the
                        // owner's last published state; the bench sweep then drops a record the owner no longer lists.
                        if (!RevertToKnown(e, known, gi)) continue;   // shop not resolvable right now — retried next scan, nothing sent meanwhile
                        _inflight.Remove(e.id);
                        Plugin.Logger.LogWarning($"{Tag} {(local.Length > 0 ? "assign" : "unassign")} of '{SafeName(e)}' (employee of '{owner}') was not confirmed by the owner after {f.tries} attempts — reverted to '{(known.Length > 0 ? known : "bench")}'.");
                        refresh = true;
                        continue;
                    }
                    tries = f.tries + 1;
                }
                // (A different target restarts the count — each dropdown change is a new intent. Against an id the owner
                // no longer knows, a user flipping between shops defers the give-up for as long as they keep flipping;
                // bounded by their behaviour and resolved the moment they stop.)
                _seq.TryGetValue(e.id, out var seq); seq++; _seq[e.id] = seq;
                var p = new SharedStaffEditPayload
                {
                    PlayerId = MPConfig.PlayerId, EmployeeId = e.id, Seq = seq, SeqEpoch = _seqEpoch,
                    Action = local.Length > 0 ? "assign" : "unassign",
                    AddressKey = local.Length > 0 ? local : known,
                    FromAddressKey = known,
                };
                _inflight[e.id] = (local, Time.unscaledTime, tries);
                Plugin.Logger.LogInfo($"{Tag} routing {p.Action} of '{SafeName(e)}' (employee of '{owner}') {(local.Length > 0 ? "to" : "from")} '{p.AddressKey}' to the owner (seq {seq}{(tries > 1 ? $", attempt {tries}" : "")}).");
                if (MPServer.IsRunning) MPServer.HostRouteSharedStaffEdit(p, MPConfig.PlayerId);
                else if (MPClient.IsConnected) MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedStaffEdit, MPConfig.PlayerId, p));
            }
            if (refresh) RefreshMyEmployeesIfOpen();
        }

        /// <summary>Put the local copy back where the owner's published state says (null = bench). False when that
        /// shop's registration is not resolvable right now (scene load / building sync): the caller retries next scan
        /// instead of writing null, which would read as "unassigned" and route an unassign nobody asked for.</summary>
        private static bool RevertToKnown(EmployeeInstance e, string known, GameInstance gi)
        {
            Address back = null;
            if (known.Length > 0)
            {
                try { back = AddressOfKey(known, gi); } catch { }
                if (back == null)
                {
                    if (_logged.Add("revert-unresolved|" + e.id)) Plugin.Logger.LogInfo($"{Tag} cannot put '{SafeName(e)}' back at '{known}' yet (shop not resolvable) — will retry.");
                    return false;
                }
            }
            try { e.assignedAddress = back; } catch { return false; }
            _logged.Remove("revert-unresolved|" + e.id);
            return true;
        }

        private static bool IsWarehouse(BuildingRegistration reg)
        {
            try { return reg != null && reg.businessTypeName == "ba:businesstype_warehouse"; } catch { return false; }
        }

        private static string SafeName(EmployeeInstance e) { try { return e.characterData?.name ?? e.id; } catch { return e?.id ?? "?"; } }

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

        private static Address AddressOfKey(string addressKey, GameInstance gi)
        {
            var r = FindReg(addressKey, gi);
            return r != null ? new Address(r.StreetName, r.StreetNumber) : null;
        }

        // ── owner: apply a routed assign / unassign ──

        /// <summary>THE OWNER's machine. MAIN THREAD. What MyEmployees' dropdown does natively (MyEmployees.cs:171-187),
        /// on MY real employee, for MY shop; then roster + bench republish so every copy converges.</summary>
        public static void ApplyOnOwner(SharedStaffEditPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.PlayerId) || string.IsNullOrEmpty(p.EmployeeId)) return;
                string key = p.EmployeeId + "|" + p.PlayerId;
                _appliedSeq.TryGetValue(key, out var last);
                if (p.Seq != 0 && last.epoch == p.SeqEpoch && p.Seq <= last.seq)
                { Plugin.Logger.LogInfo($"{Tag} staff edit seq {p.Seq} from '{p.PlayerId}' for '{p.EmployeeId}' is older than seq {last.seq} already applied — ignored."); return; }
                if (p.Seq != 0) _appliedSeq[key] = (p.SeqEpoch, p.Seq);

                EmployeeInstance emp = null;
                try { EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(p.EmployeeId, out emp); } catch { }
                // Unknown id (e.g. fired just before the helper acted): nothing of mine to republish — the helper's scan
                // gives up after MaxResends unanswered sends and reverts its copy (see TickAssignScan).
                if (emp == null) { Plugin.Logger.LogWarning($"{Tag} routed {p.Action}: employee '{p.EmployeeId}' is not mine (unknown) — ignored."); return; }
                if (MPRegisterSync.IsInjectedStaff(emp.id) || MPRegisterSync.IsSyntheticDuty(emp.id)) { Plugin.Logger.LogWarning($"{Tag} routed {p.Action}: '{p.EmployeeId}' is not my employee — ignored."); return; }
                bool candidate = false; try { candidate = emp.IsCandidate; } catch { }
                if (candidate) { Plugin.Logger.LogWarning($"{Tag} routed {p.Action}: '{SafeName(emp)}' is a candidate, not hired — ignored."); return; }

                var gi = SaveGameManager.Current;
                Address oldAddr = emp.assignedAddress;
                string oldKey = AddrOf(oldAddr);
                Address newAddr = null;
                // Owner wins (as for schedule days): the helper acted on a state that is no longer true → decline and
                // republish so their copy snaps to mine.
                string fromKey = p.FromAddressKey ?? "";
                if (fromKey != oldKey)
                {
                    string thought = fromKey.Length > 0 ? fromKey : "bench";
                    string actual  = oldKey.Length > 0 ? oldKey : "bench";
                    Plugin.Logger.LogInfo($"{Tag} routed {p.Action} of '{SafeName(emp)}' by '{p.PlayerId}' declined — they thought the employee was at '{thought}', but it is '{actual}' (owner wins).");
                    RepublishAfterStaffEdit(oldKey, oldKey);
                    return;
                }
                if (p.Action == "assign")
                {
                    var target = FindReg(p.AddressKey, gi);
                    if (target == null || !MergerFlip.TrulyMine(target)) { Plugin.Logger.LogWarning($"{Tag} routed assign of '{SafeName(emp)}' to '{p.AddressKey}' — not my shop, ignored."); RepublishAfterStaffEdit(oldKey, oldKey); return; }
                    string type = ""; try { type = target.businessTypeName ?? ""; } catch { }
                    if (type == "ba:businesstype_headquarters" || type == "ba:businesstype_empty" || type == "ba:businesstype_warehouse" || type.Length == 0)
                    { Plugin.Logger.LogWarning($"{Tag} routed assign of '{SafeName(emp)}' to '{p.AddressKey}' — not a shop that can be staffed through permissions, ignored."); RepublishAfterStaffEdit(oldKey, oldKey); return; }
                    newAddr = new Address(target.StreetName, target.StreetNumber);
                    if (oldKey == p.AddressKey) { RepublishAfterStaffEdit(oldKey, p.AddressKey); return; }   // already there — just confirm
                }
                else if (p.Action == "unassign")
                {
                    // With the owner-wins gate above (fromKey == oldKey) and the helper only sending "unassign" from a
                    // shop it knows, the two "already"/"elsewhere" halves below cannot be reached today — kept as guards.
                    if (oldKey.Length == 0) { RepublishAfterStaffEdit("", ""); return; }   // already unassigned — confirm
                    var cur = FindReg(oldKey, gi);
                    if (cur == null || !MergerFlip.TrulyMine(cur) || oldKey != p.AddressKey)
                    { Plugin.Logger.LogWarning($"{Tag} routed unassign of '{SafeName(emp)}' from '{p.AddressKey}' — they are at '{oldKey}', ignored."); RepublishAfterStaffEdit(oldKey, oldKey); return; }
                }
                else return;

                // The native reassignment (MyEmployees.cs:171-187) in the native order: the to-do kind is decided BEFORE
                // the address changes (so bench → shop files "unassigned", as the game does itself). One deliberate
                // difference: the game reloads the OLD address's cached demand twice (both its arguments still hold the
                // old address at that point); here the new AND the old address are reloaded.
                try { UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule.AbortAutoFillForBusiness(BuildingHelper.GetBuildingRegistration(oldAddr)); } catch { }
                try { EmployeeHelper.UnassignEmployeeFromAllWorkshifts(emp); } catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} unassign shifts: {ex.Message}"); }
                try { if (newAddr != null) CustomerDemandHelper.ReloadCachedFulfilled(newAddr); } catch { }
                try { if (oldAddr != null) CustomerDemandHelper.ReloadCachedFulfilled(oldAddr); } catch { }
                bool wasAssigned = false; try { wasAssigned = emp.IsAssignedToAnyBusiness(); } catch { }
                try { emp.AddTodoTask(!wasAssigned ? TodoTaskType.EmployeeUnassigned : TodoTaskType.EmployeeIdle); } catch { }
                emp.assignedAddress = newAddr;
                try { GlobalEvents.onBuildingRegistrationChange?.Invoke(oldAddr); } catch { }
                try { SaveGameManager.MarkChange(); } catch { }
                Plugin.Logger.LogInfo($"{Tag} applied routed {p.Action} of '{SafeName(emp)}' by '{p.PlayerId}': '{(oldKey.Length > 0 ? oldKey : "bench")}' → '{(newAddr != null ? p.AddressKey : "bench")}'.");
                RefreshMyEmployeesIfOpen();
                RepublishAfterStaffEdit(oldKey, newAddr != null ? p.AddressKey : "");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} ApplyOnOwner: {ex.Message}"); }
        }

        private static void RepublishAfterStaffEdit(string oldKey, string newKey)
        {
            // EvenIfEmpty: the old shop may have just lost its LAST employee — the plain ForceRosterRepublish drops the
            // sent-signature key and the publish tick then treats the empty roster as "never had staff" (not sent), so
            // the helper's copy would stay at that shop and its give-up would later put the employee BACK there.
            try { if (oldKey.Length > 0) MPRegisterSync.ForceRosterRepublishEvenIfEmpty(oldKey); } catch { }
            try { if (newKey.Length > 0 && newKey != oldKey) MPRegisterSync.ForceRosterRepublishEvenIfEmpty(newKey); } catch { }
            PublishPoolNow();
        }

        // ── My Employees: list scope, row tint + selection, details panel, dropdown ──

        [HarmonyPatch(typeof(EmployeeScrollerController), "PopulateAllModels")]
        public static class Patch_EmployeeList_Scope
        {
            static void Prefix() { ListScope = true; }
            static void Finalizer() { ListScope = false; }
        }

        private sealed class RowDefaults { public Color Name; public bool Captured; }
        private static readonly ConditionalWeakTable<EmployeeCellView, RowDefaults> _rowDefaults = new();

        /// <summary>Row: teal name for the owner's people; their mass-action checkbox greyed (no mass fire / train /
        /// bonus / assign on them). Own rows restored (cells are recycled).</summary>
        [HarmonyPatch(typeof(EmployeeCellView), nameof(EmployeeCellView.SetData))]
        public static class Patch_EmployeeCellView_SetData_Tint
        {
            static void Postfix(EmployeeCellView __instance, EmployeeModel data)
            {
                try
                {
                    if (__instance == null || data == null || __instance.employeeName == null) return;
                    var def = _rowDefaults.GetOrCreateValue(__instance);
                    if (!def.Captured) { def.Name = __instance.employeeName.color; def.Captured = true; }
                    bool grant = IsFromGrantOwner(data.employeeInstance?.id);
                    __instance.employeeName.color = grant ? Tint : def.Name;
                    if (__instance.massActionToggle != null) __instance.massActionToggle.interactable = !grant;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} row tint: {ex.Message}"); }
            }
        }

        private static readonly System.Reflection.FieldInfo _fBonusButton = AccessTools.Field(typeof(MyEmployees), "payBonusButton");
        private static readonly System.Reflection.FieldInfo _fFireLabel   = AccessTools.Field(typeof(MyEmployees), "negativeActionButtonLabel");
        private static bool _fireGreyed;   // we only ever write the fire button's state back if WE changed it

        /// <summary>Details panel for one of the owner's people: pay-bonus greyed (money), fire greyed (ruling 19).
        /// Manage-schedule stays live (it opens the shared shop's page); the dropdown is handled below.</summary>
        [HarmonyPatch(typeof(MyEmployees), nameof(MyEmployees.ShowEmployee))]
        public static class Patch_MyEmployees_ShowEmployee_Guards
        {
            static void Postfix(MyEmployees __instance, EmployeeInstance employeeInstance)
            {
                try
                {
                    bool grant = IsFromGrantOwner(employeeInstance?.id);
                    if (grant && _fBonusButton?.GetValue(__instance) is Button bonus) bonus.interactable = false;   // own rows: the game sets it each time
                    if (!grant && !_fireGreyed) return;   // never touch a native button we have not greyed (single-player, candidates)
                    var label = _fFireLabel?.GetValue(__instance) as Component;
                    var fire = label != null ? label.GetComponentInParent<Button>() : null;
                    if (fire != null) { fire.interactable = !grant; _fireGreyed = grant; }
                    else if (grant && _logged.Add("fire-button")) Plugin.Logger.LogWarning($"{Tag} could not find the fire button to grey it (the action itself is blocked).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} details guards: {ex.Message}"); }
            }
        }

        /// <summary>The seam for single-click AND shift-click range selection (ToggleRangeOfEmployees funnels every member
        /// through here): the owner's people are never part of a mass action — mass pay-bonus would spend the helper's
        /// money on them and mass-assign would strip their shifts off the owner's schedule. SELECT-ALL does NOT pass
        /// through here (MyEmployeesMassActionsUI.MassActionToggleAll assigns the list wholesale from
        /// EmployeeHelper.GetEmployeeInstances) — it is safe by a different mechanism: outside the list build (ListScope)
        /// the MPPatches global-query filter strips the owner's records. Do not relax that filter believing this prefix
        /// covers select-all.</summary>
        [HarmonyPatch(typeof(MyEmployeesMassActionsUI), nameof(MyEmployeesMassActionsUI.ToggleSelectedEmployee))]
        public static class Patch_MassActions_ToggleSelected_Guard
        {
            static bool Prefix(EmployeeInstance employeeInstance)
            {
                try { return !IsFromGrantOwner(employeeInstance?.id); } catch { return true; }
            }
        }

        /// <summary>Training is money — the owner's people are never trainable here (the game then hides the train buttons).</summary>
        [HarmonyPatch(typeof(EmployeeInstance), nameof(EmployeeInstance.CanTrainSkill))]
        public static class Patch_EmployeeInstance_CanTrainSkill_Guard
        {
            static void Postfix(EmployeeInstance __instance, ref bool __result)
            {
                try { if (__result && IsFromGrantOwner(__instance?.id)) __result = false; } catch { }
            }
        }

        /// <summary>HARD STOP behind the greyed fire button (ruling 19).</summary>
        [HarmonyPatch(typeof(MyEmployees), "FireEmployee")]
        public static class Patch_MyEmployees_Fire_Block
        {
            static bool Prefix(MyEmployees __instance)
            {
                try
                {
                    var sel = __instance.SelectedEmployeeInstance;
                    if (!IsFromGrantOwner(sel?.id)) return true;
                    if (_logged.Add("fire|" + sel.id)) Plugin.Logger.LogInfo($"{Tag} firing '{SafeName(sel)}' is not allowed through permissions — ignored.");
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>Belt and braces under every fire path: the owner's copied record is never removed here. (Runs first;
        /// the merger's own RemoveEmployee prefix is untouched and keys on its own rules.)</summary>
        [HarmonyPatch(typeof(EmployeeInstance), nameof(EmployeeInstance.RemoveEmployee))]
        [HarmonyPriority(Priority.First)]
        public static class Patch_EmployeeInstance_Remove_GrantGuard
        {
            static bool Prefix(EmployeeInstance __instance)
            {
                try
                {
                    if (!IsFromGrantOwner(__instance?.id)) return true;
                    if (_logged.Add("remove|" + __instance.id)) Plugin.Logger.LogInfo($"{Tag} RemoveEmployee on '{SafeName(__instance)}' (another player's employee) — not allowed through permissions, ignored.");
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>No to-do entries about the owner's staff in the helper's save (they would outlive the copy).</summary>
        [HarmonyPatch(typeof(EmployeeInstance), nameof(EmployeeInstance.AddTodoTask))]
        public static class Patch_EmployeeInstance_AddTodoTask_Guard
        {
            static bool Prefix(EmployeeInstance __instance)
            {
                try { return !IsFromGrantOwner(__instance?.id); } catch { return true; }
            }
        }

        /// <summary>While the dropdown is built for one of the owner's people: the list becomes THAT owner's shared shops.</summary>
        [HarmonyPatch(typeof(MyEmployees), "UpdateBusinessDropdown")]
        public static class Patch_MyEmployees_Dropdown_Scope
        {
            static void Prefix(MyEmployees __instance)
            {
                _dropdownPage = __instance;
                try { DropdownForGrantRecord = IsFromGrantOwner(__instance.SelectedEmployeeInstance?.id); } catch { DropdownForGrantRecord = false; }
            }
            static void Finalizer() { _dropdownPage = null; DropdownForGrantRecord = false; }
        }

        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.GetPlayerBuildingRegistrations))]
        [HarmonyPriority(Priority.Low)]
        public static class Patch_GetPlayerBuildingRegistrations_GrantDropdown
        {
            static void Postfix(List<BuildingRegistration> __result, object[] __args)
            {
                try
                {
                    if (!DropdownForGrantRecord || __result == null) return;
                    if (SharedShopVisibility.InBizManRefresh) return;   // scopes never overlap today; never wipe the BizMan append if they ever did
                    __result.Clear();   // fail CLOSED: an owner's employee is never offered the helper's own shops
                    var sel = _dropdownPage != null ? _dropdownPage.SelectedEmployeeInstance : null;
                    string owner = sel != null ? MPRegisterSync.OwnerOfInjected(sel.id) : "";
                    if (owner.Length == 0) return;
                    var filter = __args != null && __args.Length > 0 ? __args[0] as Delegate : null;   // the page's own PlayerBuildingFilter (skills)
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null) return;
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        string addr = ""; try { addr = GameStateReader.AddressKey(reg); } catch { continue; }
                        if (!SharedShopSchedule.IsSharedShop(reg, addr) || IsWarehouse(reg)) continue;   // warehouses: driver slots are a later slice
                        string stamp = ""; try { stamp = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                        if (stamp != owner) continue;
                        bool ok = true;
                        try { if (filter != null) ok = (bool)filter.DynamicInvoke(reg); }
                        catch (Exception ex) { ok = false; if (_logged.Add("dropdown-filter")) Plugin.Logger.LogWarning($"{Tag} the game's business filter threw for '{addr}': {ex.InnerException?.Message ?? ex.Message} — shop not offered."); }
                        if (ok) __result.Add(reg);
                    }
                    __result.Sort((a, b) => string.CompareOrdinal(a.BusinessName?.ToString() ?? "", b.BusinessName?.ToString() ?? ""));
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} dropdown: {ex.Message}"); }
            }
        }

        public static void Reset()
        {
            _inflight.Clear(); _appliedSeq.Clear(); _poolSigSent = ""; _logged.Clear(); _fireGreyed = false;
            ListScope = false; DropdownForGrantRecord = false; _dropdownPage = null;
        }
    }
}
