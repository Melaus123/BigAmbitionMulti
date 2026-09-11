using System;
using System.Collections.Generic;
using Buildings;                               // BuildingRegistration
using Entities;                                // EmployeeInstance, TodoTaskType, CustomerDemandHelper
using HarmonyLib;
using Helpers;                                 // BuildingHelper, EmployeeHelper
using UI.Smartphone.Apps.MyEmployees;          // MyEmployees, EmployeeScrollerController, EmployeeModel
using UI.Smartphone.Apps.MyEmployees.Types;    // AssignBusinessMassAction (bulk assign, ruling 23)
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
    ///    buttons are not offered), fire (button greyed and the action blocked); no to-do entries about the owner's
    ///    staff are written into the helper's save.
    ///  • BULK ACTIONS (ruling 23, 2026-08-22): the owner's people CAN be bulk-selected — bulk assign is much of the
    ///    point of managing their shop — but a selection holds ONE group: yours, or one owner's. The other group's
    ///    checkboxes grey out, and select-all follows the group already started. For an owner's group the action menu
    ///    offers ASSIGN only, and its shop list is that owner's shared shops; the game's own errors (skills, training)
    ///    behave natively. The assignment itself needs no new transport — it lands on the local copy exactly as the
    ///    single-employee dropdown does, and the 2 s scan below routes each change to the owner.
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

        /// <summary>Merger phase 2 wave 3 (W3-3): a record copied from an owner whose writes I must ROUTE —
        /// a direct Business grant OR merger membership, the same UNION the host gates now use. The narrower
        /// IsFromGrantOwner still decides the direct-grant-only SURFACES (which list shows the record, whose
        /// shops the assign dropdown offers); this decides whether a WRITE here may stand.</summary>
        public static bool IsFromRoutedOwner(string employeeId)
        {
            try
            {
                if (string.IsNullOrEmpty(employeeId) || !MPRegisterSync.IsInjectedStaff(employeeId)) return false;
                string owner = MPRegisterSync.OwnerOfInjected(employeeId);
                if (owner.Length == 0 || owner == MPConfig.PlayerId) return false;
                return GrantSync.IsGranted(GrantKind.Business, owner, MPConfig.PlayerId);
            }
            catch { return false; }
        }

        /// <summary>W3-0/W3-3: is THIS machine the one that commits for that building — the owner's own, or,
        /// while the owner is away, the machine simulating their businesses (whose lifted copy is the live
        /// state and still reads as flipped, so TrulyMine alone would refuse its own routed work).</summary>
        private static bool CommitsHere(BuildingRegistration reg, string addressKey)
        {
            if (reg == null) return false;
            if (MergerFlip.TrulyMine(reg)) return true;
            try { return MergerAbsence.SimulatesHere(addressKey); } catch { return false; }
        }

        /// <summary>W3-6 lever + any future UI seam: send ONE routed staff op ("raise") for an employee copied
        /// from an owner I route to. True = it left this machine on a route; false = not a routed record (the
        /// caller's own write stands) or nothing to send it over.</summary>
        public static bool CommitStaffOp(string employeeId, string addressKey, string op, float wage)
        {
            try
            {
                if (string.IsNullOrEmpty(employeeId) || string.IsNullOrEmpty(op)) return false;
                if (!IsFromRoutedOwner(employeeId)) return false;
                _seq.TryGetValue(employeeId, out var seq); seq++; _seq[employeeId] = seq;
                var p = new SharedStaffEditPayload
                {
                    PlayerId = MPConfig.PlayerId, EmployeeId = employeeId, Seq = seq, SeqEpoch = _seqEpoch,
                    Action = op, AddressKey = addressKey ?? "", FromAddressKey = addressKey ?? "", Wage = wage,
                };
                Plugin.Logger.LogInfo($"[Merger] staff op routed to owner '{MPRegisterSync.OwnerOfInjected(employeeId)}' for '{p.AddressKey}' ({op} {employeeId})");
                if (MPServer.IsRunning) { MPServer.HostRouteSharedStaffEdit(p, MPConfig.PlayerId); return true; }
                if (MPClient.IsConnected) { MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedStaffEdit, MPConfig.PlayerId, p)); return true; }
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} CommitStaffOp: {ex.Message}"); return false; }
        }

        /// <summary>Phase 4b (people) P2: the SAME-owner half of employee mixing. A partner's employee
        /// moved from one of that partner's shops to ANOTHER of that partner's shops is not a transfer at
        /// all - one machine runs both ends - so it is the ordinary routed assign the owner-side apply
        /// below already performs. The cross-OWNER half is the two-phase release/adopt in
        /// MergerEmployeeSync, which is the only case where a record has to change save.</summary>
        public static bool CommitAssign(string employeeId, string fromKey, string toKey)
        {
            try
            {
                if (string.IsNullOrEmpty(employeeId)) return false;
                if (!IsFromRoutedOwner(employeeId)) return false;
                _seq.TryGetValue(employeeId, out var seq); seq++; _seq[employeeId] = seq;
                bool unassign = string.IsNullOrEmpty(toKey);
                var p = new SharedStaffEditPayload
                {
                    PlayerId = MPConfig.PlayerId, EmployeeId = employeeId, Seq = seq, SeqEpoch = _seqEpoch,
                    Action = unassign ? "unassign" : "assign",
                    AddressKey = unassign ? (fromKey ?? "") : toKey,
                    FromAddressKey = fromKey ?? "",
                };
                Plugin.Logger.LogInfo($"[Merger] staff {p.Action} routed to owner '{MPRegisterSync.OwnerOfInjected(employeeId)}' ('{p.FromAddressKey}' -> '{p.AddressKey}', {employeeId})");
                if (MPServer.IsRunning) { MPServer.HostRouteSharedStaffEdit(p, MPConfig.PlayerId); return true; }
                if (MPClient.IsConnected) { MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.SharedStaffEdit, MPConfig.PlayerId, p)); return true; }
                return false;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} CommitAssign: {ex.Message}"); return false; }
        }

        // ── bulk (mass) actions: ONE GROUP per selection (ruling 23) ──

        /// <summary>Which group a row belongs to: "" = mine, an owner id = a record copied from that granting owner,
        /// null = not a real record. Merged-partner copies answer "" on purpose — their bulk actions are the merger's
        /// business and stay exactly as they were. A selection may hold only ONE group, because the two follow
        /// different rules (mine are assigned here and now; theirs are routed to the owner and only ever to THAT
        /// owner's shared shops), and one bulk action cannot honour both.</summary>
        private static string GroupOf(EmployeeInstance e)
        {
            try
            {
                if (e == null || string.IsNullOrEmpty(e.id)) return null;
                if (!IsFromGrantOwner(e.id)) return "";
                return MPRegisterSync.OwnerOfInjected(e.id);
            }
            catch { return null; }
        }

        /// <summary>The group the current selection belongs to; null when nothing is selected — the group is then
        /// undecided and the next tick decides it.</summary>
        private static string SelectionGroup()
        {
            try
            {
                var sel = MyEmployeesMassActionsUI.massActionSelectedEmployees;
                if (sel == null) return null;
                for (int i = 0; i < sel.Count; i++) { string g = GroupOf(sel[i]); if (g != null) return g; }
                return null;
            }
            catch { return null; }
        }

        private static bool _menuGroupKnown;      // has the action menu been built for _menuGroup yet?
        private static string _menuGroup;         // the group it was built for

        private static void RefreshRows()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var page = ui != null && ui.fullMenu != null ? ui.fullMenu.myEmployees : null;
                if (page == null || !page.gameObject.activeInHierarchy) return;
                page.GetCurrentScroller()?.RefreshActiveCellViews();
            }
            catch { }
        }

        /// <summary>Row greying and the action menu both depend on which group is selected, and the game refreshes
        /// neither when a single row is ticked — so we do, but only on the transitions that change anything (an empty
        /// selection gaining a group, the last tick being removed, a swap). Refreshing on every click would rebuild
        /// the menu under the player's cursor for nothing.</summary>
        private static void SyncMassActionUiIfGroupChanged()
        {
            try
            {
                string g = SelectionGroup();
                if (_menuGroupKnown && g == _menuGroup) return;
                _menuGroupKnown = true; _menuGroup = g;
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var page = ui != null && ui.fullMenu != null ? ui.fullMenu.myEmployees : null;
                if (page == null || !page.gameObject.activeInHierarchy) return;
                try { if (page.massActionsUI != null) page.massActionsUI.UpdateMassActionDropdown(page.CurrentTab); } catch { }
                RefreshRows();
            }
            catch { }
        }

        /// <summary>Auto-fill on a shared shop may use the OWNER's copied staff (theirs, at their shop) — nobody else's.
        /// On a MERGED shop (phase 0, 2026-09-10) the same rule runs off merger membership instead: the injected
        /// record's owner must be the shop's TRUE owner (the runner parked by the flip) and merged with this player
        /// at runtime. That branch is tested BEFORE IsFromGrantOwner, which only ever passes on a direct grant.</summary>
        public static bool AllowedInAutoFill(string employeeId, BuildingRegistration reg)
        {
            try
            {
                if (reg == null || string.IsNullOrEmpty(employeeId)) return false;
                string addr = GameStateReader.AddressKey(reg);
                if (SharedShopSchedule.IsMergedShop(reg, addr))
                {
                    if (!MPRegisterSync.IsInjectedStaff(employeeId)) return false;   // a live copy, never a memory of one
                    string mergedOwner = MPRegisterSync.OwnerOfInjected(employeeId);
                    if (mergedOwner.Length == 0 || mergedOwner == MPConfig.PlayerId) return false;
                    return mergedOwner == MergerFlip.ParkedRunner(addr) && MergerSync.MergedRuntime(mergedOwner, MPConfig.PlayerId);
                }
                if (!IsFromGrantOwner(employeeId)) return false;
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
            // Phase 4b (people): the company candidate pool rides the same main-thread tick this
            // file already owns, rather than adding a second entry point in the UI update.
            try { CompanyCandidates.Tick(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Candidates] tick: {ex.GetType().Name}: {ex.Message}"); }
            // Phase 4b (people) P4: the phone relay rides the same tick - it publishes nothing on a timer,
            // it only drops the copies when this player stops being a company member.
            try { CompanyMessages.Tick(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Messages] tick: {ex.GetType().Name}: {ex.Message}"); }
            // Phase 4b (people) P2 r2 (T1): the host's in-transit transfers are swept on the same
            // main-thread tick - a recurring check with a confirmed exit, not a one-shot timer.
            try { if (MPServer.IsRunning) MPServer.HostTransfersTick(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Transfer] tick: {ex.Message}"); }
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

                // W3-3: ops that do not MOVE anybody are applied BEFORE the owner-wins placement gate — a
                // raise has no "where did you think they were" for the owner and the helper to disagree about.
                if (p.Action == "raise")
                {
                    float want = p.Wage;
                    if (!(want > 0f) || want > 10000f)
                    { Plugin.Logger.LogWarning($"{Tag} routed raise of '{SafeName(emp)}' by '{p.PlayerId}': implausible wage {want} — ignored."); return; }
                    // W3-0 r1 (F2/MAJOR): a Business grant and a company membership are per-ADDRESS. Without
                    // this test, holding ONE of my shops let the sender set the wage of ANY of my employees —
                    // bench, headquarters, drivers included. The employee must be AT the address they routed on.
                    string atKey = (AddrOf(emp.assignedAddress) ?? "").Trim();
                    string wantKey = (p.AddressKey ?? "").Trim();
                    if (wantKey.Length == 0 || !string.Equals(atKey, wantKey, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Logger.LogWarning($"{Tag} routed raise of '{SafeName(emp)}' by '{p.PlayerId}': employee is not at '{wantKey}' — ignored.");
                        return;
                    }
                    try { emp.hourlyWage = want; } catch (Exception rex) { Plugin.Logger.LogWarning($"{Tag} routed raise: {rex.Message}"); return; }
                    try { SaveGameManager.MarkChange(); } catch { }
                    string rkey = AddrOf(emp.assignedAddress);
                    Plugin.Logger.LogInfo($"{Tag} applied routed raise of '{SafeName(emp)}' from '{p.PlayerId}' — hourly wage {want.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}.");
                    RefreshMyEmployeesIfOpen();
                    RepublishAfterStaffEdit(rkey, rkey);
                    return;
                }

                // Phase 4b (money): a BONUS moves nobody either, so it is applied before the placement gate too.
                // THE MONEY: the game's own GiveBonus charges the wallet HERE and sets satisfaction on the real
                // record (decompile EmployeeInstance.cs:1022-1039) — which is exactly what must happen, once, on
                // this machine; the sender's figure is only a bound, never the charge.
                if (p.Action == "bonus")
                {
                    float sent = p.Wage;
                    // GetBonusAmount is (100 - satisfaction) * hourlyWage * 8 * 30 / 100 (EmployeeInstance.cs:994-997);
                    // with wave 3's wage ceiling of 10000 no honest bonus can exceed 10000 * 8 * 30.
                    if (!(sent > 0f) || sent > 2400000f)
                    { Plugin.Logger.LogWarning($"{Tag} routed bonus of '{SafeName(emp)}' by '{p.PlayerId}': implausible amount {sent} — ignored."); return; }
                    // Same per-ADDRESS test as the raise (W3-0 r1): holding one of my shops must not buy a bonus
                    // for any of my employees — bench, headquarters and drivers included.
                    string batKey = (AddrOf(emp.assignedAddress) ?? "").Trim();
                    string bwantKey = (p.AddressKey ?? "").Trim();
                    if (bwantKey.Length == 0 || !string.Equals(batKey, bwantKey, StringComparison.OrdinalIgnoreCase))
                    { Plugin.Logger.LogWarning($"{Tag} routed bonus of '{SafeName(emp)}' by '{p.PlayerId}': employee is not at '{bwantKey}' — ignored."); return; }
                    // The gate the native BUTTON uses (MyEmployees.cs:499-500 → CanGiveBonus): an amount worth
                    // paying and the 30-day cooldown clear — read on MY record, which is the true one.
                    bool bcan = false; try { bcan = emp.CanGiveBonus(); } catch { }
                    if (!bcan)
                    { Plugin.Logger.LogWarning($"{Tag} routed bonus of '{SafeName(emp)}' by '{p.PlayerId}': no bonus is available for them right now (cooldown or amount) — ignored."); return; }
                    float bown = 0f; try { bown = emp.GetBonusAmount(); } catch { }
                    bool bpaid = false;
                    try { bpaid = emp.GiveBonus(); } catch (Exception bex) { Plugin.Logger.LogWarning($"{Tag} routed bonus: {bex.Message}"); return; }
                    if (!bpaid)   // the game's own answer — ChangeMoneySafe declined (funds)
                    { Plugin.Logger.LogWarning($"{Tag} routed bonus of '{SafeName(emp)}' by '{p.PlayerId}': the game refused the payment — nothing charged."); return; }
                    try { SaveGameManager.MarkChange(); } catch { }
                    string bkey = AddrOf(emp.assignedAddress);
                    Plugin.Logger.LogInfo($"{Tag} applied routed bonus of '{SafeName(emp)}' from '{p.PlayerId}' ({bown.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}).");
                    RefreshMyEmployeesIfOpen();
                    RepublishAfterStaffEdit(bkey, bkey);   // the new satisfaction reaches every copy
                    return;
                }

                // Phase 4b (people) P3: TRAINING moves nobody either, so it is applied before the placement
                // gate as well. The game's own bulk train is ChangeMoneySafe(-cost) and then a trainingSession
                // (decompile TrainPrimarySkillMassAction.cs:28-46); run HERE it is the company's money leaving
                // once, on the machine whose real record it buys, and the skill it buys is the one that lasts.
                // The sender's figure is a BOUND only - the cost is recomputed from this machine's record.
                if (p.Action == "train")
                {
                    float tsent = p.Wage;
                    if (!(tsent > 0f) || tsent > 10000000f)
                    { Plugin.Logger.LogWarning($"{Tag} routed training of '{SafeName(emp)}' by '{p.PlayerId}': implausible cost {tsent} - ignored."); return; }
                    // The same per-ADDRESS test as the raise and the bonus (W3-0 r1): holding one of my shops
                    // must not buy training for any of my employees - bench, headquarters and drivers included.
                    string tatKey = (AddrOf(emp.assignedAddress) ?? "").Trim();
                    string twantKey = (p.AddressKey ?? "").Trim();
                    if (twantKey.Length == 0 || !string.Equals(tatKey, twantKey, StringComparison.OrdinalIgnoreCase))
                    { Plugin.Logger.LogWarning($"{Tag} routed training of '{SafeName(emp)}' by '{p.PlayerId}': employee is not at '{twantKey}' - ignored."); return; }
                    BigAmbitions.Characters.Skills.Skill tskill = null;
                    try { tskill = emp.characterData.skills[0]; } catch { }
                    if (tskill == null)
                    { Plugin.Logger.LogWarning($"{Tag} routed training of '{SafeName(emp)}' by '{p.PlayerId}': no primary skill on my record - ignored."); return; }
                    bool tcan = false; try { tcan = emp.CanTrainSkill(tskill); } catch { }
                    if (!tcan)
                    { Plugin.Logger.LogWarning($"{Tag} routed training of '{SafeName(emp)}' by '{p.PlayerId}': they cannot be trained right now (already training, away, or at full skill) - ignored."); return; }
                    int tinc = Mathf.Min(Mathf.CeilToInt(100f - tskill.value), 10);
                    float tcost = 0f; try { tcost = EmployeeHelper.GetTrainingCost(emp, tskill.name, tinc); } catch { }
                    if (!(tcost > 0f))
                    { Plugin.Logger.LogWarning($"{Tag} routed training of '{SafeName(emp)}' by '{p.PlayerId}': the game prices it at {tcost} - ignored."); return; }
                    bool tpaid = false;
                    try
                    {
                        var tdata = new Dictionary<string, string> { { "employee", SafeName(emp) }, { "skillName", tskill.name } };
                        var tinfo = new TransactionInfo("ba:transaction_employeetraining", tdata);
                        tinfo.SetTaxDeductibleName("ba:transaction_employeetraining_label");
                        tpaid = GameManager.ChangeMoneySafe(0f - tcost, tinfo);
                    }
                    catch (Exception tex) { Plugin.Logger.LogWarning($"{Tag} routed training: {tex.Message}"); return; }
                    if (!tpaid)
                    { Plugin.Logger.LogWarning($"{Tag} routed training of '{SafeName(emp)}' by '{p.PlayerId}': the game refused the payment - nothing charged."); return; }
                    try { EmployeeHelper.UnassignEmployeeFromAllWorkshifts(emp); } catch (Exception uex) { Plugin.Logger.LogWarning($"{Tag} routed training unassign shifts: {uex.Message}"); }
                    try { emp.trainingSession = new EmployeeInstance.TrainingInstance { skill = tskill.name, startDay = SaveGameManager.Current.Day }; }
                    catch (Exception sex) { Plugin.Logger.LogWarning($"{Tag} routed training session: {sex.Message}"); }
                    try { SaveGameManager.MarkChange(); } catch { }
                    string tkey = AddrOf(emp.assignedAddress);
                    Plugin.Logger.LogInfo($"{Tag} applied routed training of '{SafeName(emp)}' from '{p.PlayerId}' - {tskill.name} +{tinc} for {tcost.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}.");
                    RefreshMyEmployeesIfOpen();
                    RepublishAfterStaffEdit(tkey, tkey);
                    return;
                }

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
                    if (target == null || !CommitsHere(target, p.AddressKey)) { Plugin.Logger.LogWarning($"{Tag} routed assign of '{SafeName(emp)}' to '{p.AddressKey}' — not my shop, ignored."); RepublishAfterStaffEdit(oldKey, oldKey); return; }
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
                    if (cur == null || !CommitsHere(cur, oldKey) || oldKey != p.AddressKey)
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
            // The old shop may have just lost its LAST employee — ForceRosterRepublish publishes an empty roster too
            // (sentinel key), otherwise the helper's copy would stay at that shop and its give-up would later put the
            // employee BACK there.
            try { if (oldKey.Length > 0) MPRegisterSync.ForceRosterRepublish(oldKey); } catch { }
            try { if (newKey.Length > 0 && newKey != oldKey) MPRegisterSync.ForceRosterRepublish(newKey); } catch { }
            PublishPoolNow();
        }

        // ── My Employees: list scope, row tint + selection, details panel, dropdown ──

        [HarmonyPatch(typeof(EmployeeScrollerController), "PopulateAllModels")]
        public static class Patch_EmployeeList_Scope
        {
            static void Prefix() { ListScope = true; }
            static void Finalizer() { ListScope = false; }
        }

        // 2026-09-05 per-owner colours: the opener is now built PER ROW from that row's owner
        // (PlayerColours.TagOpen), so two owners' people are told apart. Unknown owner → the old teal.

        /// <summary>Row: teal name for the owner's people (a rich-text tag in the label text — see the body comment);
        /// their mass-action checkbox greyed (no mass fire / train / bonus / assign on them).</summary>
        [HarmonyPatch(typeof(EmployeeCellView), nameof(EmployeeCellView.SetData))]
        public static class Patch_EmployeeCellView_SetData_Tint
        {
            static void Postfix(EmployeeCellView __instance, EmployeeModel data)
            {
                try
                {
                    if (__instance == null || data == null) return;
                    if (__instance.employeeName == null) return;
                    bool grant = IsFromGrantOwner(data.employeeInstance?.id);
                    // The colour lives INSIDE the text (a TMP rich-text tag), never on the label component:
                    // BaTable.GetCellView calls VisualizeSelected(false) right after SetData, and its SetTextColors
                    // paints every child label WHITE unless its colour is one the game itself uses (red/yellow/
                    // orange) — a colour set on the component here was overwritten before the player ever saw it
                    // (probe P-SHAREDSTAFF-LIST, 2026-08-22). Native SetData rebuilds the text from the model on
                    // every (re)bind, so recycled cells never stack tags and own rows need no restore.
                    if (grant) __instance.employeeName.text = PlayerColours.TagOpen(MPRegisterSync.OwnerOfInjected(data.employeeInstance?.id)) + __instance.employeeName.text + "</color>";
                    // Ruling 23: tickable while this row's group is the one being worked on (or nothing is selected
                    // yet). Native never disables this toggle, so writing true is what the game would have left.
                    if (__instance.massActionToggle != null)
                    {
                        string sg = SelectionGroup(), rg = GroupOf(data.employeeInstance);
                        __instance.massActionToggle.interactable = (sg == null || rg == null || sg == rg);
                    }
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

        /// <summary>Phase 4b: is this a record whose PAID op (a bonus, and from part 2 of the phase a
        /// training run) must be routed? An injected copy of a partner's employee standing at an address
        /// this machine only borrows through the merger (flipped). Direct-grant records are deliberately
        /// excluded - their bonus button is greyed and their bulk menu offers assign only, and that
        /// behaviour is not widened here.</summary>
        private static bool IsRoutedMergedOp(EmployeeInstance e, out string addrKey)
        {
            addrKey = "";
            try
            {
                if (e == null || string.IsNullOrEmpty(e.id)) return false;
                if (!MPRegisterSync.IsInjectedStaff(e.id)) return false;   // a live copy of somebody else's record
                if (IsFromGrantOwner(e.id)) return false;                  // direct grant: today's behaviour stands
                string k = (AddrOf(e.assignedAddress) ?? "").Trim();
                if (k.Length == 0 || !MergerFlip.IsFlipped(k)) return false;
                addrKey = k;
                return true;
            }
            catch { addrKey = ""; return false; }
        }

        /// <summary>THE MONEY FIX (phase 4b). EmployeeInstance.GiveBonus (decompile EmployeeInstance.cs:1022-1039)
        /// is the game's ONE bonus commit — the details-panel button (MyEmployees.cs:540-542,
        /// OnPayBonusButtonClick → GiveBonus) and the bulk "pay bonuses" action (PayBonusesMassAction.GiveBonuses)
        /// both end here. Run on a merged partner's employee it charged THIS machine's wallet — the shared one —
        /// while the satisfaction it bought landed on the display copy that the next roster publish overwrites:
        /// money spent, nothing bought. So on such a record the native body is skipped entirely (no charge, no
        /// local effect) and the op goes to whoever runs the address; false as the result is the plain truth
        /// ("not paid here") and leaves the panel exactly as the game leaves a refused bonus — no new text.</summary>
        [HarmonyPatch(typeof(EmployeeInstance), nameof(EmployeeInstance.GiveBonus))]
        public static class Patch_EmployeeInstance_GiveBonus_Route
        {
            static bool Prefix(EmployeeInstance __instance, ref bool __result)
            {
                try
                {
                    if (!IsRoutedMergedOp(__instance, out string addrKey)) return true;   // mine / single player: native
                    __result = false;
                    float amount = 0f; try { amount = __instance.GetBonusAmount(); } catch { }
                    if (!(amount > 0f))
                    { Plugin.Logger.LogWarning($"[Merger] bonus NOT routed for '{addrKey}' ({__instance.id}): the amount reads {amount} on this copy."); return false; }
                    if (!CommitStaffOp(__instance.id, addrKey, "bonus", amount))
                    { Plugin.Logger.LogWarning($"[Merger] bonus NOT routed for '{addrKey}' ({__instance.id}): no route out of this machine — nothing was charged here."); return false; }
                    Plugin.Logger.LogInfo($"[Merger] bonus routed to owner '{MPRegisterSync.OwnerOfInjected(__instance.id)}' for '{addrKey}' ({__instance.id}, {amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} bonus intercept: {ex.Message}"); return true; }
            }
        }

        /// <summary>The seam for single-click AND shift-click range selection (ToggleRangeOfEmployees funnels every
        /// member through here). Ruling 23: the owner's people MAY be ticked — what may not happen is a selection
        /// holding two groups at once. The other group's checkboxes are greyed in the row patch above, so this is the
        /// back stop for any path that reaches the list directly; a refusal is silent (ruling 17) and forces a row
        /// refresh, which puts a checkbox the game had already flipped back where it belongs.
        /// SELECT-ALL does NOT pass through here — see Patch_MassActions_ToggleAll_Group.</summary>
        [HarmonyPatch(typeof(MyEmployeesMassActionsUI), nameof(MyEmployeesMassActionsUI.ToggleSelectedEmployee))]
        public static class Patch_MassActions_ToggleSelected_Guard
        {
            static bool Prefix(EmployeeInstance employeeInstance, out bool __state)
            {
                __state = false;
                try
                {
                    string sg = SelectionGroup(), rg = GroupOf(employeeInstance);
                    if (sg == null || rg == null || sg == rg) return true;
                    __state = true;   // refused → the row's checkbox must be put back
                    if (_logged.Add("mixed-selection"))
                        Plugin.Logger.LogInfo($"{Tag} a bulk selection holds one group at a time (yours, or one owner's) — this tick was not added.");
                    return false;
                }
                catch { return true; }
            }
            static void Postfix(bool __state)
            {
                if (__state) RefreshRows();
                SyncMassActionUiIfGroupChanged();
            }
        }

        /// <summary>SELECT ALL. Vanilla replaces the selection with everyone currently listed, taken from the GLOBAL
        /// employee query — which the MPPatches filter narrows to my own people, so vanilla select-all can never reach
        /// the owner's rows. Ruling 23: select-all follows the group already started — with one of an owner's people
        /// ticked it takes all of THAT owner's rows in the list instead. Same replace-not-extend behaviour as vanilla,
        /// and the same respect for the search box and filters, because the scroller's data IS the filtered list.
        /// With nothing selected the group is undecided and vanilla stands (my own people).
        /// The group must be read in a PREFIX: the native body replaces the selection before a postfix could see it.</summary>
        [HarmonyPatch(typeof(MyEmployeesMassActionsUI), "MassActionToggleAll")]
        public static class Patch_MassActions_ToggleAll_Group
        {
            static void Prefix(out string __state) { __state = SelectionGroup(); }

            static void Postfix(bool toggled, string __state)
            {
                try
                {
                    if (!toggled || string.IsNullOrEmpty(__state)) { SyncMassActionUiIfGroupChanged(); return; }
                    var ui = InstanceBehavior<UI.UIs>.Instance;
                    var page = ui != null && ui.fullMenu != null ? ui.fullMenu.myEmployees : null;
                    var rows = page != null && page.employeeScrollerController != null ? page.employeeScrollerController.data : null;
                    var sel = MyEmployeesMassActionsUI.massActionSelectedEmployees;
                    if (rows == null || sel == null) return;
                    sel.Clear();
                    foreach (var m in rows)
                        if (m != null && GroupOf(m.employeeInstance) == __state) sel.Add(m.employeeInstance);
                    Plugin.Logger.LogInfo($"{Tag} select-all took {sel.Count} of the {__state}'s listed people (the group already being worked on).");
                    SyncMassActionUiIfGroupChanged();
                    RefreshRows();
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} select-all: {ex.Message}"); }
            }
        }

        private static readonly System.Reflection.FieldInfo _fMassActionTypes    = AccessTools.Field(typeof(MyEmployeesMassActionsUI), "_massActionsTypes");
        private static readonly System.Reflection.FieldInfo _fMassActionDropdown = AccessTools.Field(typeof(MyEmployeesMassActionsUI), "massActionDropdown");

        /// <summary>With one of an owner's groups selected the action menu offers ASSIGN only — fire, training and pay
        /// bonus stay forbidden (rulings 14, 19 and the money ruling), and listing an action that would be refused is a
        /// menu that lies. The action is recognised by TYPE, not by its name string, because those names live in game
        /// data we cannot read. The private type array is rewritten alongside the visible options: DoMassAction indexes
        /// INTO that array, so filtering only what is drawn would fire the wrong action.</summary>
        [HarmonyPatch(typeof(MyEmployeesMassActionsUI), nameof(MyEmployeesMassActionsUI.UpdateMassActionDropdown))]
        public static class Patch_MassActionMenu_Scope
        {
            static void Postfix(MyEmployeesMassActionsUI __instance)
            {
                try
                {
                    if (string.IsNullOrEmpty(SelectionGroup())) return;   // nothing selected, or my own people → the game's own menu
                    if (_fMassActionTypes == null || _fMassActionDropdown == null)
                    {
                        // Never silent: without these the menu would keep offering fire/train/bonus on another
                        // player's staff (they would still be refused, but the menu would be lying).
                        if (_logged.Add("bulk-menu-fields"))
                            Plugin.Logger.LogWarning($"{Tag} cannot narrow the bulk action menu (types field {( _fMassActionTypes == null ? "MISSING" : "ok")}, dropdown field {( _fMassActionDropdown == null ? "MISSING" : "ok")}) — the forbidden actions stay blocked, but they are still listed.");
                        return;
                    }
                    if (!(_fMassActionTypes.GetValue(__instance) is string[] types) || types.Length == 0) return;
                    var keep = new List<string>();
                    foreach (var t in types)
                        if (EmployeeMassActionHelper.GetMassAction(t) is AssignBusinessMassAction) keep.Add(t);
                    if (keep.Count == types.Length) return;
                    _fMassActionTypes.SetValue(__instance, keep.ToArray());
                    if (_fMassActionDropdown?.GetValue(__instance) is UI.Elements.Dropdown dd) dd.SetOptions(new List<string>(keep));
                    if (_logged.Add("bulk-menu"))
                        Plugin.Logger.LogInfo($"{Tag} bulk actions on another player's staff: {keep.Count} of {types.Length} offered (assign only).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} bulk action menu: {ex.Message}"); }
            }
        }

        /// <summary>Set while the BULK assign panel is being built or resolved for one of an owner's groups, so the
        /// shop list below becomes that owner's shared shops ("" = not in that scope). Both halves are covered because
        /// the game builds the labels and then re-queries the SAME call to turn the chosen index back into a shop —
        /// scoping one and not the other would map the player's choice onto a different building.</summary>
        private static string _massAssignOwner = "";

        /// <summary>True while the BULK assign panel is scoped to a granting owner AND this registration is one of
        /// THAT owner's staffable shared shops. The 2026-07-16 partner-business veto in MPPatches stands down on
        /// exactly this, and nothing wider: outside the panel, and for anyone else's buildings, the veto is unchanged.
        /// (Field 2026-08-22: without this the panel offered nothing but "Unassign" — the game's own filter said yes
        /// and our older guard overrode it to no.)</summary>
        internal static bool AllowsInMassAssign(BuildingRegistration reg)
        {
            try
            {
                if (reg == null || _massAssignOwner.Length == 0) return false;
                string addr; try { addr = GameStateReader.AddressKey(reg); } catch { return false; }
                if (!SharedShopSchedule.IsSharedShop(reg, addr) || IsWarehouse(reg)) return false;
                string stamp = ""; try { stamp = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                return stamp == _massAssignOwner;
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(AssignBusinessMassAction), nameof(AssignBusinessMassAction.Perform))]
        public static class Patch_MassAssign_Perform_Scope
        {
            static void Prefix() { _massAssignOwner = SelectionGroup() ?? ""; }
            static void Finalizer() { _massAssignOwner = ""; }
        }

        /// <summary>The index→shop resolve. The confirmation that follows looks the shop up by ADDRESS, not by index,
        /// so it needs no scope of its own.</summary>
        [HarmonyPatch(typeof(AssignBusinessMassAction), "AssignBusiness")]
        public static class Patch_MassAssign_Resolve_Scope
        {
            static void Prefix() { _massAssignOwner = SelectionGroup() ?? ""; }
            static void Finalizer() { _massAssignOwner = ""; }
        }

        /// <summary>Phase 4b (people) P3: TRUE only while the game's own bulk-training CONFIRMATION runs. The
        /// mass action's per-employee work lives in an anonymous confirm lambda, so "the player has just
        /// confirmed a training run" cannot be told from "the details panel is asking whether to draw a Train
        /// button" by the call site alone - the delegate itself is bracketed instead (the two patches below).</summary>
        private static bool _inMassTrain;
        private static bool _massTrainArming;

        /// <summary>Perform() only SHOWS the confirmation - it hands HudConfirm the delegate that does the work,
        /// synchronously - so this window is exactly "we are building that dialog".</summary>
        [HarmonyPatch(typeof(TrainPrimarySkillMassAction), nameof(TrainPrimarySkillMassAction.Perform))]
        public static class Patch_MassTrain_Perform_Arm
        {
            static void Prefix() { _massTrainArming = true; }
            static void Finalizer() { _massTrainArming = false; }
        }

        /// <summary>THE SHARED HudConfirm.Show WRAPPER. Two features ride it, each behind its own arming
        /// flag, and neither wraps anything while its flag is down:
        ///  * the bulk-train confirmation (phase 4b P3) - the flag is up for the wrapped action's duration
        ///    and for nothing else, which is what makes CanTrainSkill tell a commit from a question;
        ///  * U4 (re-check r3 MAJOR-4) - deleting a contacts MESSAGE. ContactsApp.RemoveMessage only BUILDS
        ///    this dialog; the removal that takes a partner's candidate copy out of both lists is the
        ///    confirm action itself, so the company-copy repair has to run AFTER it, not in a postfix on
        ///    RemoveMessage (which returns first). CompanyCandidates.MessagePurgeArmed is that flag.
        /// The overload is found by shape (the two Show overloads differ in their first parameter), the same
        /// reflection the work tabs already use on this type.</summary>
        [HarmonyPatch]
        public static class Patch_HudConfirm_MassTrainWindow
        {
            static System.Reflection.MethodBase TargetMethod()
            {
                System.Reflection.MethodBase fallback = null;
                foreach (var m in typeof(HudConfirm).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != "Show") continue;
                    var ps = m.GetParameters();
                    if (fallback == null) fallback = m;
                    if (ps.Length > 2 && ps[0].ParameterType != typeof(string) && ps[2].ParameterType == typeof(Action)) return m;
                }
                return fallback;
            }

            static void Prefix(ref Action onConfirmAction)
            {
                try
                {
                    if (onConfirmAction == null) return;
                    bool train = _massTrainArming;
                    bool purge = false;
                    try { purge = CompanyCandidates.MessagePurgeArmed; } catch { }
                    if (!train && !purge) return;
                    var inner = onConfirmAction;
                    onConfirmAction = () =>
                    {
                        bool was = _inMassTrain;
                        if (train) _inMassTrain = true;
                        try { inner(); }
                        finally
                        {
                            if (train) _inMassTrain = was;
                            // U4: the message whose deletion has just been confirmed may have taken a
                            // partner's candidate copy out of both lists with it - put it back now that the
                            // confirmed action has actually run (the origin's record is what decides).
                            if (purge) { try { CompanyCandidates.RepairAfterMessagePurge(); } catch { } }
                        }
                    };
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} confirm-dialog wrapper: {ex.Message}"); }
            }
        }

        /// <summary>Training is money. For a DIRECT-GRANT record the answer is still a flat no (rulings 14, 19):
        /// the owner's people are not trainable on a helper's machine, and the game then hides the train buttons.
        /// For a MERGED partner's employee phase 4b (people) P3 changes the answer - the menu may offer it, and
        /// the moment the player confirms the bulk run the per-employee effect is ROUTED to whoever runs that
        /// shop (who pays once from the shared wallet and sets the trainingSession on the REAL record) while the
        /// local replica is left untouched: exactly the shape of the bonus fix beside it. A merger copy this
        /// machine cannot route for (no flipped address) keeps the old refusal.
        /// CanTrainSkill is the ONE seam both training paths pass through (MyEmployees.cs:373-374 and
        /// TrainPrimarySkillMassAction.cs:24), and the writes live in anonymous confirm lambdas, so it is also
        /// the only place the effect can be intercepted PER EMPLOYEE - which is what makes a MIXED bulk
        /// selection work: every record is decided on its own, and every refusal is logged against its id.</summary>
        [HarmonyPatch(typeof(EmployeeInstance), nameof(EmployeeInstance.CanTrainSkill))]
        public static class Patch_EmployeeInstance_CanTrainSkill_Guard
        {
            static void Postfix(EmployeeInstance __instance, ref bool __result)
            {
                try
                {
                    if (!__result) return;
                    string id = __instance?.id ?? "";
                    if (id.Length == 0) return;
                    if (IsFromGrantOwner(id)) { __result = false; return; }                  // direct grant: unchanged
                    if (!IsRoutedMergedOp(__instance, out string addrKey))
                    { if (IsFromRoutedOwner(id)) __result = false; return; }                 // a merger copy with nowhere to route
                    // MINOR-8 r2: the DETAILS-PANEL train button cannot be routed. Its click handler is an
                    // anonymous onClick listener built inside MyEmployees.ShowEmployee (decompile :373-418),
                    // and for an ASSIGNED employee - which every merger copy at a partner's shop is - it
                    // short-circuits into Notifications.ShowError("myemployees_unassign_for_training")
                    // before it reaches HudConfirm, so there is no seam to arm the flag from and no way to
                    // stop the game's own error firing. The button therefore stays HIDDEN here, exactly as
                    // it was before P3: answering the question with false is what hides it
                    // (buttonByName.gameObject.SetActive(employeeInstance.CanTrainSkill(skill))).
                    // The BULK train is the routed path and is unaffected.
                    if (!_inMassTrain) { __result = false; return; }                          // a question, not a commit
                    __result = false;                                                        // the replica is never trained here
                    float cost = 0f;
                    try
                    {
                        var sk = __instance.characterData.skills[0];
                        cost = EmployeeHelper.GetTrainingCost(__instance, sk.name, Mathf.Min(Mathf.CeilToInt(100f - sk.value), 10));
                    }
                    catch { }
                    if (!(cost > 0f))
                    { Plugin.Logger.LogWarning($"[Merger] training NOT routed for '{addrKey}' ({id}): the cost reads {cost} on this copy."); return; }
                    if (!CommitStaffOp(id, addrKey, "train", cost))
                    { Plugin.Logger.LogWarning($"[Merger] training NOT routed for '{addrKey}' ({id}): no route out of this machine - nothing was charged here."); return; }
                    Plugin.Logger.LogInfo($"[Merger] training routed to owner '{MPRegisterSync.OwnerOfInjected(id)}' for '{addrKey}' ({id}, {cost.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} train intercept: {ex.Message}"); }
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

        /// <summary>No to-do entries about another player's staff in this save (they would outlive the copy).
        /// Wave 3 (W3-3) widens it to the routed UNION so a merger-flipped partner's employee is covered too.
        /// Suppression, not a route: the owner's own engine files the same to-do on the real record whenever
        /// it is due (EmployeeHelper.cs:401/:545), so routing one would duplicate their bookkeeping.</summary>
        [HarmonyPatch(typeof(EmployeeInstance), nameof(EmployeeInstance.AddTodoTask))]
        public static class Patch_EmployeeInstance_AddTodoTask_Guard
        {
            static bool Prefix(EmployeeInstance __instance)
            {
                try { return !IsFromRoutedOwner(__instance?.id); } catch { return true; }
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

        /// <summary>Serves BOTH "assign to business" surfaces for an owner's people: the single-employee dropdown and
        /// the bulk assign panel. One body, so the two can never drift into offering different shops.</summary>
        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.GetPlayerBuildingRegistrations))]
        [HarmonyPriority(Priority.Low)]
        public static class Patch_GetPlayerBuildingRegistrations_OwnerShopScope
        {
            static void Postfix(List<BuildingRegistration> __result, object[] __args)
            {
                try
                {
                    if (__result == null) return;
                    if (SharedShopVisibility.InBizManRefresh) return;   // scopes never overlap today; never wipe the BizMan append if they ever did
                    string owner;
                    if (DropdownForGrantRecord)
                    {
                        var sel = _dropdownPage != null ? _dropdownPage.SelectedEmployeeInstance : null;
                        owner = sel != null ? MPRegisterSync.OwnerOfInjected(sel.id) : "";
                    }
                    else if (_massAssignOwner.Length > 0) owner = _massAssignOwner;
                    else return;                                        // not one of our scopes — the game's own list stands
                    ScopeToOwnerShops(__result, owner, __args != null && __args.Length > 0 ? __args[0] as Delegate : null);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"{Tag} shop list: {ex.Message}"); }
            }
        }

        /// <summary>Replace the list with ONLY that owner's staffable shared shops, still passing the game's own
        /// filter. Fails CLOSED: an owner's employee is never offered the helper's own shops, so an owner we cannot
        /// identify yields an empty list rather than the wrong one.
        ///
        /// The game's filters for "which business can this employee go to" are gated on RentedByPlayer — "I rent this"
        /// — which is FALSE for another player's shop on this machine, so every one of the owner's shops was rejected
        /// and the bulk panel offered nothing but "Unassign" (field 2026-08-22). Tenancy is therefore raised for the
        /// single synchronous filter call and lowered in a finally: the flag means "mine" to every other system in the
        /// game, so it may never outlive the call that needs it.</summary>
        private static void ScopeToOwnerShops(List<BuildingRegistration> result, string owner, Delegate filter)
        {
            result.Clear();
            if (string.IsNullOrEmpty(owner)) return;
            var gi = SaveGameManager.Current;
            if (gi?.BuildingRegistrations == null) return;
            int candidates = 0;
            foreach (var reg in gi.BuildingRegistrations)
            {
                if (reg == null) continue;
                string addr = ""; try { addr = GameStateReader.AddressKey(reg); } catch { continue; }
                if (!SharedShopSchedule.IsSharedShop(reg, addr) || IsWarehouse(reg)) continue;   // warehouses: driver slots are a later slice
                string stamp = ""; try { stamp = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                if (stamp != owner) continue;
                candidates++;
                bool ok = true;
                if (filter != null)
                {
                    // Tenancy raised for the call: the game's own filter opens with RentedByPlayer ("I rent this"),
                    // false for another player's shop here. Necessary but NOT sufficient — the mod's own 2026-07-16
                    // partner-business veto sits on the same filter and had to stand down too (AllowsInMassAssign).
                    bool raised = SharedShopVisibility.RaiseTenancy(reg, addr);
                    try { ok = (bool)filter.DynamicInvoke(reg); }
                    catch (Exception ex) { ok = false; if (_logged.Add("dropdown-filter")) Plugin.Logger.LogWarning($"{Tag} the game's business filter threw for '{addr}': {ex.InnerException?.Message ?? ex.Message} — shop not offered."); }
                    finally { SharedShopVisibility.LowerTenancy(reg, raised); }
                }
                if (ok) result.Add(reg);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.BusinessName?.ToString() ?? "", b.BusinessName?.ToString() ?? ""));
            // A shop list that comes out short is the symptom the player sees as "my choices are missing": say which
            // half lost them — none of that owner's shops found, or found and then filtered away.
            if (_logged.Add($"shoplist|{owner}|{candidates}|{result.Count}"))
                Plugin.Logger.LogInfo($"{Tag} shops offered for '{owner}': {result.Count} of {candidates} shared shop(s) of theirs on this machine.");
        }

        public static void Reset()
        {
            _inflight.Clear(); _appliedSeq.Clear(); _poolSigSent = ""; _logged.Clear(); _fireGreyed = false;
            ListScope = false; DropdownForGrantRecord = false; _dropdownPage = null;
            _massAssignOwner = ""; _menuGroupKnown = false; _menuGroup = null;
            _inMassTrain = false; _massTrainArming = false;
            try { CompanyCandidates.Reset(); } catch { }
            try { CompanyMessages.Reset(); } catch { }
        }
    }
}
