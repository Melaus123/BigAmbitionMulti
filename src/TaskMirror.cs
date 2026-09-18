using HarmonyLib;
using Entities;

namespace BigAmbitionsMP
{
    /// <summary>
    /// MIRROR-1 (design .modding/03-systems/mirror-alerts-design-2026-09-17.md, user requirement
    /// 2026-09-17): under a merger the two members operate as one, so the objective panel must read the
    /// same on both machines for the company's shops.  The OWNER's alert list is the TRUTH for its own
    /// businesses: the owner generates tasks exactly as it always did and publishes them on the existing
    /// CompanyLists bundle; a co-member never generates a business-scoped task for a partner shop and
    /// never completes a mirrored one by local state.
    ///
    /// Why a mirror and not local generation (design F3/F4): the owner-only task types read interior item
    /// instances, employee instances or the save-global licence list - none of which a partner's replica
    /// carries - and the "matching" types (MissingRequiredItem / NoProducers / the stock pair) are UNMET BY
    /// ABSENCE on a shop the member has not walked into, which is why the client listed the host's cinema
    /// as missing its screen, booth, kiosk and register.  Copied tasks would then be auto-completed by the
    /// member's own daily check on its first pass.  So: refuse local creation, install the owner's rows,
    /// and shield them from every local remover.
    ///
    /// AUTHORITY: one writer.  The owner is the only machine that creates, completes or edits its tasks.
    /// The only race is owner-completes vs member-installs-the-previous-list: the member shows a stale
    /// alert for at most one publish cadence (30 s), which the design accepts.
    ///
    /// RECURRENCE: the rows ride the CompanyLists bundle, which republishes on every dirty / urgent / day /
    /// membership edge and is re-installed IN FULL on every Apply - so a pass that had to defer (panel not
    /// built yet) or that installed partially is healed by the next bundle.  There is no sender-side "sent"
    /// mark and no timer of our own.
    ///
    /// SAVE: a mirrored task is a runtime display object and must never reach the .hsg.  StripForSave takes
    /// them out at the same choke point the synthetic cashiers use (MPRegisterSync.StripSyntheticsForSave)
    /// and the returned delegate puts the exact objects back after serialization.
    /// </summary>
    public static class TaskMirror
    {
        // ── state ────────────────────────────────────────────────────────────────────────────────

        /// <summary>Mirrored task id -> the pid of the owner whose list it came from.  Session-only: a
        /// mirror never survives a save, so nothing here is persisted.</summary>
        private static readonly Dictionary<string, string> _mirrorIds = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The wire row behind each mirrored id.  Fold E needs ItemName / ProducerItemName after
        /// the install (the description postfix), and the reconcile needs the rest to spot a change.</summary>
        private static readonly Dictionary<string, PwBusinessTask> _rows = new Dictionary<string, PwBusinessTask>(StringComparer.Ordinal);

        /// <summary>Set while THIS machine is drawing a mirrored row.  The creation gate and the
        /// UpdateTodoTask shield both stand down inside it, so the installer can use the game's own
        /// routines.  ThreadStatic for the same reason NotificationRelay._applying is: Apply runs on the
        /// main thread and a stray background call must never see another thread's flag.</summary>
        [ThreadStatic] internal static bool Installing;

        /// <summary>Set while THIS machine is REMOVING a mirrored row (a lift, or the owner-completed
        /// diff).  The three removal shields stand down inside it - that is the only way a mirror leaves.</summary>
        [ThreadStatic] internal static bool Lifting;

        private static readonly HashSet<string> _loggedRefusedAddr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _loggedInertClick  = new HashSet<string>(StringComparer.Ordinal);
        private static bool _loggedDeferred;

        // ── reflection into the panel (the two members the design names are private) ──────────────

        private static readonly System.Reflection.MethodInfo? _mAddToUI =
            AccessTools.Method(typeof(UI.Tasks.TasksUI), "AddTodoTaskToUI", new[] { typeof(TodoTask), typeof(bool) });
        private static readonly System.Reflection.FieldInfo? _fTasksGroup =
            AccessTools.Field(typeof(UI.Tasks.TasksUI), "_tasksGroup");

        /// <summary>The panel, but ONLY when it can actually take a row: the design's install condition is
        /// `tasksUI` alive AND its private `_tasksGroup` non-null, because UpdateTodoTask (TasksUI.cs:384-386)
        /// dereferences `_tasksGroup.Find(task.id)` with no null check - the installer must never add DATA
        /// without a ROW.  Null here means "defer this pass"; the next bundle retries.</summary>
        private static UI.Tasks.TasksUI? DrawablePanel()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var panel = ui != null ? ui.tasksUI : null;
                if (panel == null) return null;
                if (_mAddToUI == null || _fTasksGroup == null) return null;
                if (TasksGroup(panel) == null) return null;
                return panel;
            }
            catch { return null; }
        }

        private static UnityEngine.Transform? TasksGroup(UI.Tasks.TasksUI panel)
        {
            try { return _fTasksGroup?.GetValue(panel) as UnityEngine.Transform; }
            catch { return null; }
        }

        private static bool RowExists(UI.Tasks.TasksUI panel, string id)
        {
            try
            {
                var group = TasksGroup(panel);
                return group != null && !string.IsNullOrEmpty(id) && group.Find(id) != null;
            }
            catch { return false; }
        }

        // ── shared predicates ────────────────────────────────────────────────────────────────────

        /// <summary>BUSINESS-SCOPED = every task type except the genuinely PERSONAL ones (design §4: taxes,
        /// hunger, health and the two vehicle alerts stay per-person; UnusedVar is not a real type).</summary>
        public static bool IsBusinessScoped(TodoTaskType type)
        {
            switch (type)
            {
                case TodoTaskType.PayTaxes:
                case TodoTaskType.LowHunger:
                case TodoTaskType.LowHealth:
                case TodoTaskType.VehicleNeedsRepair:
                case TodoTaskType.VehicleNeedsFuel:
                case TodoTaskType.UnusedVar:
                    return false;
                default:
                    return true;
            }
        }

        public static bool IsMirrored(string? id)
            => !string.IsNullOrEmpty(id) && _mirrorIds.ContainsKey(id!);

        /// <summary>The address key of a task, or "" when the task carries no usable address.  An Address
        /// object can exist and still be UNDEFINED (Streets/AddressHelper.cs:27-33), and AddressKey would
        /// then build a key out of two empty halves - so the streetName is tested first.</summary>
        internal static string KeyOf(Address? a)
        {
            if (a == null) return "";
            try { if (string.IsNullOrEmpty(a.streetName)) return ""; } catch { return ""; }
            try { return GameStateReader.AddressKey(a); } catch { return ""; }
        }

        /// <summary>A PARTNER SHOP for the purposes of this mechanism: flipped onto my screens by the
        /// merger, but simulated somewhere else.  An address this machine SIMULATES for an absent owner is
        /// deliberately NOT one - this machine IS the generator for it (design, Absence).</summary>
        private static bool IsPartnerShop(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            try { return MergerFlip.IsFlipped(key) && !MergerAbsence.SimulatesHere(key); }
            catch { return false; }
        }

        /// <summary>OWNER SIDE: is this task one of MINE to publish?  Addressed rows are held to true
        /// ownership (MergerFlip.TrulyMine), never the presentation flip, so a partner's shop that is
        /// flipped onto my screens is not republished from here.  An UNADDRESSED row (the EmployeeIdle /
        /// EmployeeUnassigned shape the probe showed with addr '-') is mine when its employee is a locally
        /// NATIVE person: our synthetic duty stand-ins and a partner's injected roster copies are not.</summary>
        internal static bool MineToPublish(TodoTask? t)
        {
            if (t == null) return false;
            if (!IsBusinessScoped(t.type)) return false;
            string key = KeyOf(t.address);
            if (key.Length > 0)
            {
                BuildingRegistration? reg = null;
                try { reg = Helpers.BuildingHelper.GetBuildingRegistration(t.address); } catch { reg = null; }
                if (reg == null) return false;
                try { return MergerFlip.TrulyMine(reg); } catch { return false; }
            }
            string eid = "";
            try { eid = t.employeeId ?? ""; } catch { }
            if (eid.Length == 0) return false;
            if (eid.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) return false;
            try { if (MPRegisterSync.IsInjectedStaff(eid)) return false; } catch { }
            return true;
        }

        // ── OWNER SIDE: fill the wire ────────────────────────────────────────────────────────────

        /// <summary>The owner's business-scoped tasks as wire rows, straight out of the live list.  Called
        /// from the paperwork bundle build, so it inherits that build's settled-world gate.</summary>
        public static List<PwBusinessTask> BuildRows()
        {
            var rows = new List<PwBusinessTask>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.TodoTasks == null) return rows;
                foreach (var t in gi.TodoTasks)
                {
                    if (t == null) continue;
                    if (IsMirrored(t.id)) continue;        // never republish somebody else's row back at them
                    if (!MineToPublish(t)) continue;
                    rows.Add(new PwBusinessTask
                    {
                        Id                  = t.id ?? "",
                        Type                = t.type.ToString(),
                        AddressKey          = KeyOf(t.address),
                        EmployeeId          = t.employeeId ?? "",
                        ItemName            = t.itemName ?? "",
                        ItemInstanceId      = t.itemInstanceId ?? "",
                        ProducerItemName    = ProducerNameFor(t),
                        BusinessRequirement = t.businessRequirement ?? "",
                        Priority            = (int)t.priority,
                        PriorityOffset      = t.priorityOffset,
                        RemainingDays       = t.remainingDays,
                        OwnerPid            = MPConfig.PlayerId,
                    });
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] owner rows: {ex.Message}"); }
            return rows;
        }

        /// <summary>Fold E: the stock alerts' words name the PRODUCER item, which GetTodoDescription
        /// (TasksUI.cs:747-785) reads out of the LOCAL registration's itemInstances - a lookup a member
        /// cannot do for a shop it has never entered.  The owner resolves it here, the same way, and the
        /// name travels.</summary>
        private static string ProducerNameFor(TodoTask t)
        {
            try
            {
                if (t.type != TodoTaskType.LowStock && t.type != TodoTaskType.EmptyStock) return "";
                if (string.IsNullOrEmpty(t.itemInstanceId)) return "";
                var reg = Helpers.BuildingHelper.GetBuildingRegistration(t.address);
                if (reg?.itemInstances == null) return "";
                if (reg.itemInstances.TryGetValue(t.itemInstanceId, out var inst) && inst != null)
                    return inst.itemName ?? "";
            }
            catch { }
            return "";
        }

        // ── MEMBER SIDE: install ─────────────────────────────────────────────────────────────────

        /// <summary>The member's install for ADDRESSED rows.  `addrs` is exactly the set of this owner's
        /// addresses that passed the flip test and are not simulated here - the same set CompanyLists
        /// installed the agreement copies into.</summary>
        public static void Install(CompanyListsPayload p, HashSet<string> addrs)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.OwnerPid) || addrs == null) return;
                var wanted = new List<PwBusinessTask>();
                foreach (var r in p.BusinessTasks ?? new List<PwBusinessTask>())
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                    if (string.IsNullOrEmpty(r.AddressKey) || !addrs.Contains(r.AddressKey)) continue;
                    wanted.Add(r);
                }
                Reconcile(p.OwnerPid, wanted, addressed: true, addrs: addrs);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] install '{p?.OwnerPid}': {ex.Message}"); }
        }

        /// <summary>Fold F: the rows with NO address (the EmployeeIdle / EmployeeUnassigned shape).  They
        /// cannot be selected per address, so they are selected per PERSON: the row installs when its
        /// employee is one of THIS owner's injected roster copies on this machine.  The owner pid travels
        /// on the row, so a roster that cannot name an owner for an injected id still resolves.</summary>
        public static void InstallUnaddressed(CompanyListsPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.OwnerPid)) return;
                var wanted = new List<PwBusinessTask>();
                foreach (var r in p.BusinessTasks ?? new List<PwBusinessTask>())
                {
                    if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                    if (!string.IsNullOrEmpty(r.AddressKey)) continue;
                    string eid = r.EmployeeId ?? "";
                    if (eid.Length == 0) continue;
                    bool injected = false;
                    try { injected = MPRegisterSync.IsInjectedStaff(eid); } catch { }
                    if (!injected) continue;                    // a locally-native person's row is the member's own
                    string owner = "";
                    try { owner = MPRegisterSync.OwnerOfInjected(eid) ?? ""; } catch { }
                    // The roster's owner wins when it knows one; when it does not, the wire row's own
                    // OwnerPid is the authority (this bundle came from that owner and no other).
                    if (owner.Length > 0 && !string.Equals(owner, p.OwnerPid, StringComparison.Ordinal)) continue;
                    wanted.Add(r);
                }
                Reconcile(p.OwnerPid, wanted, addressed: false, addrs: null);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] unaddressed install '{p?.OwnerPid}': {ex.Message}"); }
        }

        /// <summary>One bucket of one owner's list, made to match: local rows for those addresses purged,
        /// missing rows drawn, changed rows updated in place, and rows the owner has completed lifted.
        /// The two buckets (addressed / unaddressed) reconcile independently, so neither sweep can lift
        /// the other's rows when only one of the two passes has run.</summary>
        private static void Reconcile(string ownerPid, List<PwBusinessTask> wanted, bool addressed, HashSet<string>? addrs)
        {
            var panel = DrawablePanel();
            if (panel == null)
            {
                if (!_loggedDeferred) { _loggedDeferred = true; Plugin.Logger.LogInfo("[Tasks] mirror deferred - panel not built yet"); }
                return;
            }
            _loggedDeferred = false;
            var gi = SaveGameManager.Current;
            if (gi?.TodoTasks == null) return;

            // 1. The member's OWN business-scoped tasks at a partner address go: the owner's list is the
            //    truth there, and the creation gate stops them coming back.
            int purged = 0;
            if (addressed && addrs != null)
                foreach (var key in addrs)
                    purged += PurgeLocalAt(panel, gi, key);
            else if (!addressed)
                purged += PurgeLocalUnaddressed(panel, gi, ownerPid);   // review MEDIUM-3

            // 2. Install / update.
            var live = new HashSet<string>(StringComparer.Ordinal);
            int drawn = 0;
            foreach (var r in wanted)
            {
                live.Add(r.Id);
                var existing = FindById(gi, r.Id);
                if (existing != null && IsMirrored(r.Id))
                {
                    // Fold r1 (rig T-HQPARITY3-20260917-204700): every mirrored row was re-updated on every
                    // bundle - 16 UpdateTodoTask calls per 30 s. Only a CHANGED row (or one whose row object
                    // a scene rebuild destroyed) is touched now.
                    if (!_rows.TryGetValue(r.Id, out var had) || RowDiffers(had, r) || !RowExists(panel, r.Id))
                    { UpdateInPlace(panel, existing, r); _rows[r.Id] = r; }
                    continue;
                }
                if (existing != null) continue;                 // an id collision with a local task - leave it alone
                var task = BuildTask(r);
                if (task == null) continue;
                bool was = Installing;
                Installing = true;
                // Review HIGH-1: the id is REGISTERED BEFORE the draw. A throw inside the game's own
                // AddTodoTaskToUI used to leave the data in the list with no row and no mirror mark -
                // unshielded, unstripped at save, never struck through (the round-246 permanent alert,
                // persisted). Now a failed draw withdraws the data and the mark together.
                _mirrorIds[r.Id] = ownerPid;
                _rows[r.Id] = r;
                try
                {
                    gi.TodoTasks.Add(task);
                    _mAddToUI!.Invoke(panel, new object[] { task, true });
                    drawn++;
                }
                catch (Exception ex)
                {
                    try { gi.TodoTasks.Remove(task); } catch { }
                    _mirrorIds.Remove(r.Id); _rows.Remove(r.Id);
                    Plugin.Logger.LogWarning($"[Tasks] mirror draw of '{r.Id}' ({r.Type}) failed - row withdrawn: {ex.Message}");
                }
                finally { Installing = was; }
            }

            // 3. Everything of THIS owner in THIS bucket that the new list no longer names has been
            //    completed by the owner - it leaves the member's panel too.
            int lifted = 0;
            foreach (var id in new List<string>(_mirrorIds.Keys))
            {
                if (!string.Equals(_mirrorIds[id], ownerPid, StringComparison.Ordinal)) continue;
                if (live.Contains(id)) continue;
                bool rowAddressed = _rows.TryGetValue(id, out var had) && !string.IsNullOrEmpty(had?.AddressKey);
                if (rowAddressed != addressed) continue;
                if (RemoveMirror(panel, gi, id)) lifted++;
            }

            if (drawn > 0 || lifted > 0 || purged > 0)
            {
                string tag = ownerPid;
                try { tag = MergerAbsence.DisplayOwnerTag(ownerPid); } catch { }
                int addrCount = addressed && addrs != null ? addrs.Count : 0;
                int unaddressed = addressed ? 0 : drawn;
                Plugin.Logger.LogInfo($"[Tasks] mirrored {drawn} task(s) from '{tag}' ({addrCount} address(es), {unaddressed} unaddressed), lifted {lifted}");
            }
        }

        private static TodoTask? FindById(GameInstance gi, string id)
        {
            var list = gi.TodoTasks;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && string.Equals(list[i].id, id, StringComparison.Ordinal)) return list[i];
            return null;
        }

        /// <summary>The owner's row as a live TodoTask.  The id is the OWNER's, unchanged (fold H: rows are
        /// found by Transform.Find(id), so a rewritten id would break the game's own lookup as surely as a
        /// native one containing a path separator does).  The Address is taken from the LOCAL registration
        /// so that every native consumer - the description prefix, the BizMan click - resolves it.</summary>
        private static TodoTask? BuildTask(PwBusinessTask r)
        {
            try
            {
                TodoTaskType type;
                if (!Enum.TryParse(r.Type ?? "", out type)) return null;
                if (!Enum.IsDefined(typeof(TodoTaskType), type)) return null;
                Address? addr = null;
                if (!string.IsNullOrEmpty(r.AddressKey))
                {
                    addr = AddressFor(r.AddressKey);
                    if (addr == null) return null;              // the flip has not landed yet - the next bundle retries
                }
                return new TodoTask
                {
                    id                  = r.Id,
                    type                = type,
                    address             = addr,
                    itemName            = string.IsNullOrEmpty(r.ItemName) ? null : r.ItemName,
                    itemInstanceId      = string.IsNullOrEmpty(r.ItemInstanceId) ? null : r.ItemInstanceId,
                    employeeId          = string.IsNullOrEmpty(r.EmployeeId) ? null : r.EmployeeId,
                    priority            = (Enums.Priority)r.Priority,
                    priorityOffset      = r.PriorityOffset,
                    remainingDays       = r.RemainingDays,
                    businessRequirement = string.IsNullOrEmpty(r.BusinessRequirement) ? null : r.BusinessRequirement,
                };
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] build row '{r?.Id}': {ex.Message}"); return null; }
        }

        private static Address? AddressFor(string key)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return null;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    string k; try { k = GameStateReader.AddressKey(reg); } catch { continue; }
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return reg.Address;
                }
            }
            catch { }
            return null;
        }

        /// <summary>A field the owner changed (priority, days, the stock numbers behind the words).  The
        /// priority goes through the game's OWN UpdateTodoTask so the row recolours and the panel height
        /// re-measures; our shield on that call stands down for the installer.  A row destroyed by a scene
        /// rebuild is redrawn rather than updated - UpdateTodoTask has no null check on its lookup.</summary>
        private static void UpdateInPlace(UI.Tasks.TasksUI panel, TodoTask task, PwBusinessTask r)
        {
            try
            {
                task.itemName            = string.IsNullOrEmpty(r.ItemName) ? null : r.ItemName;
                task.itemInstanceId      = string.IsNullOrEmpty(r.ItemInstanceId) ? null : r.ItemInstanceId;
                task.employeeId          = string.IsNullOrEmpty(r.EmployeeId) ? null : r.EmployeeId;
                task.priorityOffset      = r.PriorityOffset;
                task.remainingDays       = r.RemainingDays;
                task.businessRequirement = string.IsNullOrEmpty(r.BusinessRequirement) ? null : r.BusinessRequirement;
                var want = (Enums.Priority)r.Priority;
                bool was = Installing;
                Installing = true;
                try
                {
                    if (RowExists(panel, task.id)) panel.UpdateTodoTask(task, want);
                    else { task.priority = want; _mAddToUI!.Invoke(panel, new object[] { task, true }); }
                }
                finally { Installing = was; }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] update mirrored '{r?.Id}': {ex.Message}"); }
        }

        /// <summary>Remove ONE mirrored row through the game's own routine (row + data + panel height,
        /// atomically - the round-246 lesson: a data-only removal orphans the row).</summary>
        private static bool RemoveMirror(UI.Tasks.TasksUI panel, GameInstance gi, string id)
        {
            try
            {
                var task = FindById(gi, id);
                if (task == null) { _mirrorIds.Remove(id); _rows.Remove(id); return false; }
                bool was = Lifting;
                Lifting = true;
                // Review LOW: the mark comes off AFTER the removal, so the owner-dirty postfix on
                // InstantlyCompleteTodoTask still sees a mirrored task and stays silent.
                try { panel.InstantlyCompleteTodoTask(task); }
                finally { Lifting = was; _mirrorIds.Remove(id); _rows.Remove(id); }   // re-check R5: off on every path, still after the call
                if (gi.TodoTasks.Contains(task)) gi.TodoTasks.Remove(task);   // headless / no row drawn
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] lift '{id}': {ex.Message}"); return false; }
        }

        /// <summary>The member's own business-scoped tasks at one partner address.  They predate the
        /// mirror (or a build that had none) and must not sit beside the owner's list saying something
        /// different.</summary>
        /// <summary>Review MEDIUM-3: the unaddressed bucket's local purge. A machine that STOOD IN for
        /// this owner generated null-address employee rows for the owner's people natively; when the owner
        /// returns those rows are not mirrored and no address names them, so they would sit beside the
        /// mirrored ones until the 10-minute integrity tick. They are the owner's people (injected here
        /// again, and the roster names this owner or no owner), so they go the way PurgeLocalAt's do.</summary>
        private static int PurgeLocalUnaddressed(UI.Tasks.TasksUI panel, GameInstance gi, string ownerPid)
        {
            int n = 0;
            try
            {
                for (int i = gi.TodoTasks.Count - 1; i >= 0; i--)
                {
                    var t = gi.TodoTasks[i];
                    if (t == null || IsMirrored(t.id)) continue;
                    if (!IsBusinessScoped(t.type)) continue;
                    if (t.address != null) continue;
                    string eid = t.employeeId ?? "";
                    if (eid.Length == 0) continue;
                    bool injected = false; try { injected = MPRegisterSync.IsInjectedStaff(eid); } catch { }
                    if (!injected) continue;
                    string owner = ""; try { owner = MPRegisterSync.OwnerOfInjected(eid) ?? ""; } catch { }
                    if (owner.Length > 0 && !string.Equals(owner, ownerPid, StringComparison.Ordinal)) continue;
                    try { panel.InstantlyCompleteTodoTask(t); } catch { }
                    if (gi.TodoTasks.Contains(t)) gi.TodoTasks.Remove(t);
                    n++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] purge local unaddressed for '{ownerPid}': {ex.Message}"); }
            return n;
        }

        /// <summary>Fold r1: the wire row changed in a way the panel must see.</summary>
        private static bool RowDiffers(PwBusinessTask a, PwBusinessTask b)
        {
            if (a == null || b == null) return true;
            return !string.Equals(a.Type, b.Type, StringComparison.Ordinal)
                || !string.Equals(a.AddressKey ?? "", b.AddressKey ?? "", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.EmployeeId ?? "", b.EmployeeId ?? "", StringComparison.Ordinal)
                || !string.Equals(a.ItemName ?? "", b.ItemName ?? "", StringComparison.Ordinal)
                || !string.Equals(a.ItemInstanceId ?? "", b.ItemInstanceId ?? "", StringComparison.Ordinal)
                || !string.Equals(a.ProducerItemName ?? "", b.ProducerItemName ?? "", StringComparison.Ordinal)
                || !string.Equals(a.BusinessRequirement ?? "", b.BusinessRequirement ?? "", StringComparison.Ordinal)
                || a.Priority != b.Priority || a.PriorityOffset != b.PriorityOffset || a.RemainingDays != b.RemainingDays;
        }

        /// <summary>SuspendOwner ruling (review 2026-09-17): when THIS machine becomes the stand-in for an
        /// absent owner, that owner's mirrored rows are lifted and the game's own per-business regeneration
        /// is asked at once for every address this machine now simulates, so the panel does not sit empty
        /// until the next daily edge. The creation gate stands down for those addresses on its own
        /// (SimulatesHere is true), and MineToPublish still needs TrulyMine, so nothing is double-published.</summary>
        public static int OnStandIn(string ownerPid) { try { return Lift(ownerPid); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] stand-in for '{ownerPid}': {ex.Message}"); return 0; } }

        /// <summary>Re-check R4: called from MergerAbsence.ApplyHandover AFTER the loop that sets the absence
        /// marks (SuspendOwner runs before them, so a regeneration there found SimulatesHere false everywhere
        /// and did nothing).</summary>
        public static void RegenerateStandIn(string ownerPid, IEnumerable<string>? addressKeys)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || addressKeys == null) return;
                foreach (var key in addressKeys)
                {
                    if (string.IsNullOrEmpty(key) || !MergerAbsence.SimulatesHere(key)) continue;
                    var addr = AddressFor(key);
                    var reg = addr != null ? Helpers.BuildingHelper.GetBuildingRegistration(addr) : null;
                    if (reg == null) continue;
                    try { UI.Tasks.TasksUI.UpdateTasksFromBusiness(reg); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] stand-in regenerate at '{key}': {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] stand-in regenerate for '{ownerPid}': {ex.Message}"); }
        }

        private static int PurgeLocalAt(UI.Tasks.TasksUI panel, GameInstance gi, string key)
        {
            int n = 0;
            try
            {
                for (int i = gi.TodoTasks.Count - 1; i >= 0; i--)
                {
                    var t = gi.TodoTasks[i];
                    if (t == null || IsMirrored(t.id)) continue;
                    if (!IsBusinessScoped(t.type)) continue;
                    if (!string.Equals(KeyOf(t.address), key, StringComparison.OrdinalIgnoreCase)) continue;
                    try { panel.InstantlyCompleteTodoTask(t); } catch { }
                    if (gi.TodoTasks.Contains(t)) gi.TodoTasks.Remove(t);
                    n++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] purge local at '{key}': {ex.Message}"); }
            return n;
        }

        // ── MEMBER SIDE: lift ────────────────────────────────────────────────────────────────────

        /// <summary>Retire mirrored rows - one owner's (un-flip, unmerge, that owner left) or, with an
        /// empty pid, every one of them.  The refusal in the creation gate ends with the flip on its own,
        /// so a member's OWN shops are never touched by either half.</summary>
        public static int Lift(string? ownerPid)
        {
            int n = 0;
            try
            {
                if (_mirrorIds.Count == 0) return 0;
                var gi = SaveGameManager.Current;
                var panel = DrawablePanel();
                foreach (var id in new List<string>(_mirrorIds.Keys))
                {
                    if (!string.IsNullOrEmpty(ownerPid)
                        && !string.Equals(_mirrorIds[id], ownerPid, StringComparison.Ordinal)) continue;
                    if (gi?.TodoTasks == null || panel == null) { _mirrorIds.Remove(id); _rows.Remove(id); n++; continue; }
                    if (RemoveMirror(panel, gi, id)) n++;
                }
                if (n > 0)
                    Plugin.Logger.LogInfo($"[Tasks] mirrored 0 task(s) from '{(string.IsNullOrEmpty(ownerPid) ? "every owner" : ownerPid)}' (0 address(es), 0 unaddressed), lifted {n}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] lift '{ownerPid}': {ex.Message}"); }
            return n;
        }

        // ── the creation gate's extra clause (fold A / design step 1) ────────────────────────────

        /// <summary>Called from Patch_TasksUI_AddNewTodoTask_ForeignGate.  TRUE means "refuse this task":
        /// it is business-scoped, its address is a partner shop this machine does not simulate, and the
        /// mirror installer is not the one asking.  This is what ends the false "missing required item /
        /// no producers" alerts a member saw for a shop it had never walked into.</summary>
        internal static bool RefuseLocalCreation(TodoTaskType type, Address? address)
        {
            try
            {
                if (Installing) return false;
                if (!IsBusinessScoped(type)) return false;
                string key = KeyOf(address);
                if (!IsPartnerShop(key)) return false;
                if (_loggedRefusedAddr.Add(key))
                    Plugin.Logger.LogInfo($"[Tasks] local task {type} at {key} refused - a partner's shop, the owner's list is the truth");
                return true;
            }
            catch { return false; }
        }

        /// <summary>Fold B: the LOAD-TIME sweep only.  A save written by an older build can hold tasks at
        /// an address the merger now flips; the live 10-minute purge must never do this, because it would
        /// delete the mirror it has just installed.</summary>
        internal static bool IsStaleFlippedTodo(TodoTask? t)
        {
            try
            {
                if (t == null || IsMirrored(t.id)) return false;
                if (!IsBusinessScoped(t.type)) return false;
                return IsPartnerShop(KeyOf(t.address));
            }
            catch { return false; }
        }

        // ── SAVE (design step 6 / caveat I) ──────────────────────────────────────────────────────

        /// <summary>SAVE-path strip, called from MPRegisterSync.StripSyntheticsForSave so that both save
        /// choke points (MPSaveCoordinator PerformLocalSave, OfflineForkSave) are covered by the ONE
        /// restore Action they already carry.  Mirrored tasks are display copies of another member's
        /// alerts: a .hsg must hold none of them, or a later single-player load would show a partner's
        /// objectives with nothing behind them.</summary>
        public static System.Action StripForSave(string when)
        {
            var removed = new List<TodoTask>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.TodoTasks == null || _mirrorIds.Count == 0) return () => { };
                for (int i = gi.TodoTasks.Count - 1; i >= 0; i--)
                {
                    var t = gi.TodoTasks[i];
                    if (t == null || !IsMirrored(t.id)) continue;
                    removed.Add(t);
                    gi.TodoTasks.RemoveAt(i);
                }
                if (removed.Count > 0)
                    Plugin.Logger.LogInfo($"[Tasks] stripped {removed.Count} mirrored task(s) for save ({when}); restore after serialize.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] save strip ({when}): {ex.Message}"); }
            if (removed.Count == 0) return () => { };
            return () =>
            {
                try
                {
                    var gi = SaveGameManager.Current;
                    if (gi?.TodoTasks == null) return;
                    foreach (var t in removed)
                    {
                        if (t == null || string.IsNullOrEmpty(t.id)) continue;
                        if (FindById(gi, t.id) == null) gi.TodoTasks.Add(t);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] save restore ({when}): {ex.Message}"); }
            };
        }

        // ══ HARMONY ══════════════════════════════════════════════════════════════════════════════
        //
        // Three REMOVAL shields, one WRITE shield, one CLICK guard, one DESCRIPTION filler and the
        // owner-side dirty marks.  Every body is wrapped: a throw here must never take a native call down.

        /// <summary>Shield 1 of 3.  The daily completion check (TasksUI.cs:582-741) ends every one of its
        /// branches in CompleteTodoTask, and its tests read LOCAL state the member does not have for a
        /// partner's shop - so without this a mirrored alert would strike itself through on its first
        /// pass.  Only the lift path may complete a mirror.</summary>
        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.CompleteTodoTask))]
        public static class Patch_TasksUI_CompleteTodoTask_MirrorShield
        {
            static bool Prefix(TodoTask task)
            {
                try { if (task != null && IsMirrored(task.id) && !Lifting) return false; }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] CompleteTodoTask shield: {ex.Message}"); }
                return true;
            }
        }

        /// <summary>Shield 2 of 3.  The instant remover is what the integrity purge and several business
        /// paths use; the same rule applies.</summary>
        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.InstantlyCompleteTodoTask))]
        public static class Patch_TasksUI_InstantlyCompleteTodoTask_MirrorShield
        {
            static bool Prefix(TodoTask task)
            {
                try { if (task != null && IsMirrored(task.id) && !Lifting) return false; }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] InstantlyCompleteTodoTask shield: {ex.Message}"); }
                return true;
            }
        }

        /// <summary>Shield 3 of 3 (fold C).  InstantlyCompleteListOfTasks removes RAW from the list and is
        /// reached live from a member restocking a partner's shelf (ItemController.cs:986/:1191), from
        /// UpdateTasksFromBusiness (TasksUI.cs:576-578, which then REGENERATES locally - caught by the
        /// creation gate) and from four other call sites.  Refusing the whole call would drop the
        /// member's OWN tasks in the same batch, so the SEQUENCE is filtered instead.</summary>
        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.InstantlyCompleteListOfTasks))]
        public static class Patch_TasksUI_InstantlyCompleteListOfTasks_MirrorShield
        {
            static void Prefix(ref IEnumerable<TodoTask> tasks)
            {
                try
                {
                    if (Lifting || tasks == null || _mirrorIds.Count == 0) return;
                    var all = new List<TodoTask>(tasks);
                    bool any = false;
                    foreach (var t in all)
                        if (t != null && IsMirrored(t.id)) { any = true; break; }
                    if (!any) return;
                    var kept = new List<TodoTask>();
                    foreach (var t in all)
                        if (t == null || !IsMirrored(t.id)) kept.Add(t!);
                    tasks = kept;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] InstantlyCompleteListOfTasks shield: {ex.Message}"); }
            }
        }

        /// <summary>Fold D.  Local cleanliness raises the priority of a DirtyFloors task through
        /// UpdateTodoTask (TasksUI.cs:686) - a WRITE to the owner's row from a machine that is not its
        /// writer.  Refused, unless the installer is the caller putting the owner's own new priority in.</summary>
        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.UpdateTodoTask))]
        public static class Patch_TasksUI_UpdateTodoTask_MirrorShield
        {
            static bool Prefix(TodoTask task)
            {
                try { if (task != null && IsMirrored(task.id) && !Installing) return false; }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] UpdateTodoTask shield: {ex.Message}"); }
                return true;
            }
        }

        /// <summary>Design step 5, user ruling 2026-09-17 - PARITY: a mirrored alert does what it does for
        /// the owner.  Address-typed clicks open BizMan on the flipped shop natively; employee-typed clicks
        /// open My Employees on that person, because a partner's staff ARE present here as the roster's
        /// injected copies.  The ONE inert case is an employee id this machine cannot resolve, which would
        /// NRE inside DelayShowEmployee (TasksUI.cs:889/:903) - that is refused and logged, and the log
        /// line is a bug to chase, not a design outcome.  UnhappyEmployee needs no guard: its ClickTask
        /// branch (:927-929) is a no-op.</summary>
        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.ClickTask))]
        public static class Patch_TasksUI_ClickTask_MirrorParity
        {
            static bool Prefix(TodoTask task)
            {
                try
                {
                    if (task == null || !IsMirrored(task.id)) return true;
                    if (task.type != TodoTaskType.EmployeeIdle && task.type != TodoTaskType.EmployeeUnassigned) return true;
                    Entities.EmployeeInstance? who = null;
                    try { who = Helpers.EmployeeHelper.GetEmployeeById(task.employeeId, showError: false); } catch { who = null; }
                    if (who != null) return true;
                    if (_loggedInertClick.Add(task.id ?? ""))
                        Plugin.Logger.LogInfo($"[Tasks] mirrored task {task.id} ({task.type}) click: employee '{task.employeeId ?? ""}' not resolved - inert");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] ClickTask parity guard: {ex.Message}"); }
                return true;
            }
        }

        /// <summary>Fold E.  GetTodoDescription resolves the PRODUCER item out of the LOCAL registration's
        /// itemInstances and, when it cannot, returns a key with NO Arguments - a stock row would draw
        /// without its words on a member who has never entered the shop.  The owner resolved both names
        /// before publishing, so they are put back here, and only for a mirrored stock row whose Arguments
        /// really did come back empty.  The prefix (the business name) needs nothing: it resolves natively
        /// from the flipped registration.</summary>
        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.GetTodoDescription))]
        public static class Patch_TasksUI_GetTodoDescription_MirrorWords
        {
            static void Postfix(TodoTask task, ref Localizor.LanguageChangeEvent.LanguageChangeEventDataHolder __result)
            {
                try
                {
                    if (task == null || !IsMirrored(task.id)) return;
                    if (task.type != TodoTaskType.LowStock && task.type != TodoTaskType.EmptyStock) return;
                    if (__result.Arguments != null) return;
                    if (!_rows.TryGetValue(task.id, out var r) || r == null) return;
                    __result.Arguments = new { itemname = r.ItemName ?? "", producer_itemname = r.ProducerItemName ?? "" };
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] GetTodoDescription words: {ex.Message}"); }
            }
        }

        // ── OWNER SIDE: the dirty marks ──────────────────────────────────────────────────────────
        //
        // The list rides the existing bundle, so a change to one of MY business tasks only has to say
        // "the bundle is stale".  30 s cadence, the same as the plans: a completed alert leaves the
        // partner's panel within one cadence.

        private static void MarkOwnChange(TodoTask? t)
        {
            try
            {
                if (t == null) return;
                if (!MergerSync.IAmMember) return;
                if (IsMirrored(t.id)) return;        // a mirror changing is the OWNER's doing, not mine
                if (!MineToPublish(t)) return;
                PaperworkSync.MarkDirty();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] dirty mark: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.AddNewTodoTask))]
        public static class Patch_TasksUI_AddNewTodoTask_OwnerDirty
        {
            static void Postfix(TodoTask __result)
            {
                try { if (__result != null) MarkOwnChange(__result); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] AddNewTodoTask dirty: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.CompleteTodoTask))]
        public static class Patch_TasksUI_CompleteTodoTask_OwnerDirty
        {
            static void Postfix(TodoTask task)
            {
                try { MarkOwnChange(task); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] CompleteTodoTask dirty: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.InstantlyCompleteTodoTask))]
        public static class Patch_TasksUI_InstantlyCompleteTodoTask_OwnerDirty
        {
            static void Postfix(TodoTask task)
            {
                try { MarkOwnChange(task); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] InstantlyCompleteTodoTask dirty: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.InstantlyCompleteListOfTasks))]
        public static class Patch_TasksUI_InstantlyCompleteListOfTasks_OwnerDirty
        {
            // The shield above has already filtered the sequence, so what arrives here is what will go.
            static void Postfix(IEnumerable<TodoTask> tasks)
            {
                try { if (tasks != null) foreach (var t in tasks) MarkOwnChange(t); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] InstantlyCompleteListOfTasks dirty: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.UpdateTodoTask))]
        public static class Patch_TasksUI_UpdateTodoTask_OwnerDirty
        {
            static void Postfix(TodoTask task)
            {
                try { MarkOwnChange(task); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tasks] UpdateTodoTask dirty: {ex.Message}"); }
            }
        }
    }
}
