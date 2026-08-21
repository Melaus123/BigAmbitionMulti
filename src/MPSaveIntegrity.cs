using System;
using System.Collections.Generic;

namespace BigAmbitionsMP
{
    /// <summary>Load-time save-integrity sweep (campaign green-lit 2026-07-12):
    /// the recurring failure class here is a DANGLING SAVE REFERENCE (item name,
    /// employee id, rival id) hitting an unguarded native lookup inside a loop —
    /// one bad record kills a whole subsystem (payroll runaway, office-staff
    /// freeze, "New Text" shifts, customer-less stores).  This sweep fixes the
    /// data ONCE at world-ready instead of shielding every consumer: repair only
    /// where provably native-legal (argument documented per class), detect-only
    /// otherwise.  Wide net, quiet logs: silent on healthy saves; every action
    /// logged and summarized into bug reports (IntegrityFindings) so the field
    /// tells us which classes recur and which producers to hunt upstream.</summary>
    public static class MPSaveIntegrity
    {
        /// <summary>Per-class counts from the latest sweep ("" = clean) —
        /// stamped into report.md next to PatchIssues.</summary>
        public static string LastSummary = "";

        private const int LogCapPerClass = 10;

        public static void RunSweep(string reason)
        {
            // Round-157: translate the session ledger into the native world FIRST — before anything
            // native can act on a just-loaded (possibly just-handed-off) world, and before this
            // sweep's own detectors read state the translation would have fixed.
            try { GameStatePatcher.ReassertLedgerWorldState(reason); } catch { }

            var parts = new List<string>();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                // ── Class 1: item-names (REPAIR) ─────────────────────────────
                // reg.cachedAvailableProducts entries whose item no longer
                // resolves (content mod removed).  Native-legal removal: an
                // unresolvable item cannot be sold, and the game's own
                // RemoveSeasonalItems exists to remove unsellable entries —
                // which is exactly where the 2026-07-12 report NRE'd 3,467
                // times, silently killing a store's customer spawning.
                int itemFixed = 0, itemLogged = 0;
                try
                {
                    if (gi.BuildingRegistrations != null)
                        foreach (var reg in gi.BuildingRegistrations)
                        {
                            var list = reg?.cachedAvailableProducts;
                            if (list == null || list.Count == 0) continue;
                            for (int i = list.Count - 1; i >= 0; i--)
                            {
                                string name = list[i];
                                bool dead;
                                try { dead = string.IsNullOrEmpty(name) || BigAmbitions.Items.ItemsGetter.GetByName(name) == null; }
                                catch { dead = true; }
                                if (!dead) continue;
                                list.RemoveAt(i);
                                itemFixed++;
                                if (itemLogged++ < LogCapPerClass)
                                    Plugin.Logger.LogWarning($"[Integrity] {reason}: '{reg!.BusinessName}' sale cache referenced '{name}' — unresolvable (content mod removed?), removed.");
                            }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] item-names: {ex.Message}"); }
                if (itemFixed > 0) parts.Add($"item-names×{itemFixed} repaired");

                // ── Class 5: vehicle-slots (REPAIR) ──────────────────────────
                // A Warehouse/factory reg whose vehicleSlots list is shorter than
                // the building's bay count: parking a truck throws index-out-of-
                // range (assignment aborts — "factory not taking in delivery
                // truck", RED ROC 2026-07-13) and driver assignment has no slot
                // to bind.  Native-legal by PRECEDENT: the game's own EA06
                // compat fix (FixNumberOfVehicleSlotsInWarehouses) appends
                // missing slots exactly like this — but it is version-gated to
                // old saves and never re-runs.  Producers: native sizing yields
                // 0 when the building lookup isn't ready (?? 0), and our
                // business-type apply bypassed native setup (guarded now).
                int slotsFixed = 0, slotsLogged = 0;
                try
                {
                    if (gi.BuildingRegistrations != null)
                        foreach (var reg in gi.BuildingRegistrations)
                        {
                            if (reg is not Entities.Warehouse w) continue;
                            // Round-273 (user-approved 2026-08-17): native-parity filter. The game's
                            // own EA06 fixes repair ONLY player-rented warehouses (RentedByPlayer),
                            // and ResetVehicleSlots fills slots at rent time — unowned/AI warehouse-
                            // type buildings sit at zero slots BY DESIGN. Without this filter the
                            // sweep re-"repaired" the whole city every load (the vehicle-slots×131
                            // header constant: three field worlds + a fresh gen, identical count).
                            // Player-rented only — the RED ROC factory case stays covered.
                            if (!w.RentedByPlayer) continue;
                            int expected = 0;
                            try { expected = Buildings.BuildingSizeHelper.GetData(Helpers.BuildingHelper.GetBuilding(w.Address))?.numberOfVehicleSlots ?? 0; }
                            catch { }
                            if (expected <= 0) continue;   // unknown → never shrink/guess
                            w.vehicleSlots ??= new List<Entities.VehicleSlot>();
                            int missing = expected - w.vehicleSlots.Count;
                            if (missing <= 0) continue;
                            for (int i = 0; i < missing; i++) w.vehicleSlots.Add(new Entities.VehicleSlot());
                            slotsFixed += missing;
                            if (slotsLogged++ < LogCapPerClass)
                                Plugin.Logger.LogWarning($"[Integrity] {reason}: '{w.BusinessName}' had {expected - missing}/{expected} vehicle slot(s) — appended {missing} (native EA06-fix precedent).");
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] vehicle-slots: {ex.Message}"); }
                if (slotsFixed > 0) parts.Add($"vehicle-slots×{slotsFixed} repaired");

                // -- Class 6: truncated drawn queues (REPAIR, round-137) --------------
                // customPositions = [anchors..., zero separator, spots...].  A copy frozen mid-build
                // carries the anchors plus EXACTLY ONE stored spot; that lone spot makes the native
                // rebuild guard skip SetUpWaitingLine, capping the till at one queue place every load
                // (field: same register, 8 entries on the placing machine, 4 here).  Dropping the lone
                // spot lets the native loader rebuild the line by pathing between the anchors.
                int queueFixed = 0, queueLogged = 0;
                try
                {
                    if (gi.BuildingRegistrations != null)
                        foreach (var reg in gi.BuildingRegistrations)
                        {
                            if (reg?.itemInstances == null) continue;
                            foreach (var kvq in reg.itemInstances)
                            {
                                var iiq = kvq.Value;
                                if (iiq?.customPositions == null) continue;
                                // Round-143: the origin-staging poison (head anchor >10m from the item) -
                                // drop the whole list; the native loader rebuilds the line at the true pose.
                                if (MPRegisterSync.IsPoisonedQueue(iiq.customPositions, iiq.position))
                                {
                                    iiq.customPositions = null;
                                    queueFixed++;
                                    if (queueLogged++ < LogCapPerClass)
                                        Plugin.Logger.LogWarning($"[Integrity] {reason}: '{reg.BusinessName}' item '{iiq.itemName}' had queue data anchored far from the register (origin-staging artifact) - dropped so the line rebuilds at the item's true position.");
                                    continue;
                                }
                                if (MPRegisterSync.RepairTruncatedQueue(iiq.customPositions) > 0)
                                {
                                    queueFixed++;
                                    if (queueLogged++ < LogCapPerClass)
                                        Plugin.Logger.LogWarning($"[Integrity] {reason}: '{reg.BusinessName}' item '{iiq.itemName}' carried a truncated drawn queue (anchors + one stored spot) - dropped the spot so the line rebuilds at its real length.");
                                }
                            }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] drawn-queues: {ex.Message}"); }
                if (queueFixed > 0) parts.Add($"drawn-queues×{queueFixed} repaired");

                // ── Classes 2+3: duty shifts (REPAIR) + real-id shift orphans
                // (DETECT) — the existing zero-false-positive sweep; its
                // wider-net pass logs [ScheduleDiag] details for real ids.
                int dutyFixed = 0;
                try { dutyFixed = MPRegisterSync.RepairOrphanDutyShifts(reason); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] duty-shifts: {ex.Message}"); }
                if (dutyFixed > 0) parts.Add($"duty-shifts×{dutyFixed} repaired");
                int realOrphans = MPRegisterSync.LastRealIdOrphans;
                if (realOrphans > 0) parts.Add($"shift-employees×{realOrphans} logged");

                // ── Class 4b: rival-less world (DETECT-ONLY, round-256) ──────
                // gi.rivalStates is the id list ALL rival identity derives from
                // (native load: FillData(rivalStates ids)); native GenerateRivals
                // always writes exactly 19 entries (4 special fixed-id + 7
                // wholesale + 8 import UUIDs) and can never leave it empty. Empty +
                // a populated world = generation ran without rivals (lost id
                // race at a client new-game start, field 20260809-132321) or an
                // externally damaged save. Detect-only by user ruling 2026-08-15
                // — no heal until more field pictures accumulate.
                try
                {
                    int rivalStates = 0; try { rivalStates = gi.rivalStates?.Count ?? 0; } catch { }
                    int regsTotal   = 0; try { regsTotal   = gi.BuildingRegistrations?.Count ?? 0; } catch { }
                    if (rivalStates == 0 && regsTotal > 0)
                    {
                        parts.Add("rivalStates EMPTY (rival-less world)");
                        Plugin.Logger.LogError($"[Integrity] {reason}: gi.rivalStates is EMPTY with {regsTotal} building registration(s) — this world generated WITHOUT rivals (round-256 family: lost id race at world creation, or external save damage). Detect-only; expect unresolvable rival refs below.");
                    }
                }
                catch { }

                // ── Class 4: rival-refs (DETECT-ONLY) ────────────────────────
                // Registration owner-rival ids that resolve to no rival and no
                // session player — the rival-poor / contamination families.
                // Repair deliberately withheld: an erased AI landlord is
                // unrecoverable (worldgen-only assignment), so we count and
                // name, never guess.
                int rivalRefs = 0, rivalLogged = 0;
                try
                {
                    if (gi.BuildingRegistrations != null)
                        foreach (var reg in gi.BuildingRegistrations)
                        {
                            if (reg == null) continue;
                            foreach (var id in new[] { reg.businessOwnerRivalId, reg.buildingOwnerRivalId })
                            {
                                if (string.IsNullOrEmpty(id)) continue;
                                bool sessionPlayer = false;
                                try { sessionPlayer = GameStatePatcher.IsSessionPlayerId(id); } catch { }
                                if (sessionPlayer) { continue; }   // contamination sweep's territory
                                bool known = false;
                                try { known = BigAmbitions.Rivals.RivalsHelper.GetRivalData(id) != null; } catch { }
                                if (known) continue;
                                rivalRefs++;
                                if (rivalLogged++ < LogCapPerClass)
                                    Plugin.Logger.LogWarning($"[Integrity] {reason}: '{reg.BusinessName}' references rival '{id}' — unresolvable; left in place (deed repair is not provable).");
                            }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] rival-refs: {ex.Message}"); }
                if (rivalRefs > 0) parts.Add($"rival-refs×{rivalRefs} logged");

                // ── Class 9: stacked starter items (REPAIR, round-260) ───────
                // A denied optimistic rent used to leak one delivery spot + one
                // hand-truck spawner per attempt (field 20260810-232704: 4 denials
                // → 5 stacked delivery spots). The source leak is fixed in
                // RollbackRent; this repairs saves that already carry the ghosts.
                // SAFETY: only duplicates at the SAME position are removed (the
                // leak stacks at the layout default) — large layouts may define
                // several legitimate spots at distinct positions, and a player
                // may have dragged a leaked ghost elsewhere; those are left.
                try
                {
                    int stackRemoved = 0, stackLogged = 0;
                    if (gi.BuildingRegistrations != null)
                        foreach (var reg in gi.BuildingRegistrations)
                        {
                            if (reg?.itemInstances == null) continue;
                            try
                            {
                                var seen = new HashSet<string>();
                                var drop = new List<string>();
                                foreach (var kv in reg.itemInstances)
                                {
                                    string n = kv.Value?.itemName?.ToString() ?? "";
                                    if (n != "ba:itemname_deliveryspot" && n != "ba:itemname_handtruckspawner") continue;
                                    var p = kv.Value!.position;
                                    string sig = $"{n}|{p.x:F2}|{p.y:F2}|{p.z:F2}";
                                    if (!seen.Add(sig)) drop.Add(kv.Key);
                                }
                                int before = drop.Count;
                                foreach (var k in drop) if (reg.itemInstances.Remove(k)) stackRemoved++;
                                if (before > 0 && stackLogged++ < LogCapPerClass)
                                    Plugin.Logger.LogWarning($"[Integrity] {reason}: '{GameStateReader.AddressKey(reg)}' had {before} starter item(s) stacked at identical positions — removed (round-260 leak repair).");
                            }
                            catch { }
                        }
                    if (stackRemoved > 0) parts.Add($"stacked-starter-items×{stackRemoved} repaired");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] starter-item dedupe: {ex.Message}"); }

                // ── Class 10: stale retail prices on EMPTY buildings (REPAIR, round-291) ──
                // Native ShutdownBusiness empties a closed business's price table, but
                // the publish→apply path read "no prices sent" as "keep yours", so every
                // other machine kept the dead shop's prices and SAVED them (TESTBATCH-0813:
                // 11 regs, measured in the client's .hsg 2026-08-21). The live apply now
                // clears them (GameStatePatcher, round-291); this heals saves that already
                // carry the residue. Invariant restored = native's own post-shutdown state:
                // empty type ⇒ no prices. Touches only nameless, typeless, unrented shells.
                try
                {
                    int staleRegs = 0, stalePrices = 0;
                    if (gi.BuildingRegistrations != null)
                        foreach (var reg in gi.BuildingRegistrations)
                        {
                            if (reg == null || reg.RentedByPlayer) continue;
                            string t = ""; try { t = reg.businessTypeName ?? ""; } catch { }
                            if (t.Length > 0 && t != "ba:businesstype_empty") continue;
                            bool named = false; try { named = !string.IsNullOrEmpty(reg.BusinessName); } catch { }
                            if (named) continue;
                            int n = 0;
                            try { n = (reg.retailPrices?.Count ?? 0) + (reg.storedRetailPrices?.Count ?? 0); } catch { }
                            if (n == 0) continue;
                            try { reg.retailPrices?.Clear(); } catch { }
                            try { reg.storedRetailPrices?.Clear(); } catch { }
                            staleRegs++; stalePrices += n;
                        }
                    if (staleRegs > 0)
                    {
                        parts.Add($"stale-prices×{staleRegs} cleared");
                        Plugin.Logger.LogWarning($"[Integrity] {reason}: {staleRegs} empty building(s) still carried {stalePrices} retail price(s) from a business that closed elsewhere — cleared (round-291; the publish path now clears them live).");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] stale-prices: {ex.Message}"); }

                // ── Class 6: cross-owner staff (REPAIR) ──────────────────────
                // An OWN employee whose assignedAddress points at ANOTHER
                // player's business (2026-07-16 report: the native assign-to-
                // business dropdowns listed partner shops, so players "swapped"
                // employees across saves).  The record exists only on THIS
                // machine — roster sync publishes own-shop staff only — so the
                // partner sees ghost workers they never hired while the record
                // keeps generating demands here.  Native-legal repair: this IS
                // the dropdown's own "Unassigned" path (abort auto-fill, clear
                // work shifts, null the address, todo task) — the employee stays
                // hired and just needs a workplace again.  The dropdowns are
                // filtered now, so this heals old damage rather than racing new.
                int crossFixed = 0, crossLogged = 0;
                try
                {
                    if (gi.EmployeeInstances != null)
                        foreach (var emp in gi.EmployeeInstances)
                        {
                            if (emp == null || emp.assignedAddress == null) continue;
                            string eid = ""; try { eid = emp.id ?? ""; } catch { }
                            if (eid.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix)) continue;   // runtime stand-ins
                            try { if (MPRegisterSync.IsInjectedStaff(eid)) continue; } catch { }          // partner copies — theirs, not ours
                            BuildingRegistration? reg = null;
                            try { reg = Helpers.BuildingHelper.GetBuildingRegistration(emp.assignedAddress); } catch { }
                            if (!GameStatePatcher.IsForeignPlayerBusiness(reg)) continue;
                            // ROUND-247 fix B (field 20260806-160214 'BANGER'): this repair DESTROYS
                            // data (unassigns staff), so the "another player's business" verdict must
                            // PROVE itself — the runner stamp has to resolve to an actual session
                            // player (the cross-save assignment case this class was built for).  In
                            // the field the stamp was an unresolvable AI rival id mirrored over the
                            // player's OWN shop by a host rollback record (pre-247-fix-A), and this
                            // sweep stripped 12 of their employees, twice.  Unresolvable stamp =
                            // detect-only: name it, touch nothing.
                            string stamp = ""; try { stamp = reg!.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                            if (!GameStatePatcher.IsSessionPlayerId(stamp))
                            {
                                if (crossLogged++ < LogCapPerClass)
                                    Plugin.Logger.LogWarning($"[Integrity] {reason}: employee assigned to '{reg!.BusinessName}' whose owner stamp '{stamp}' resolves to NO session player — left in place (round-247; likely a scarred own shop, not a cross-owner assignment).");
                                continue;
                            }
                            try { UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule.AbortAutoFillForBusiness(reg); } catch { }
                            try { Helpers.EmployeeHelper.UnassignEmployeeFromAllWorkshifts(emp); } catch { }
                            emp.assignedAddress = null;
                            try { emp.AddTodoTask(Entities.TodoTaskType.EmployeeUnassigned); } catch { }
                            crossFixed++;
                            if (crossLogged++ < LogCapPerClass)
                            {
                                string en = ""; try { en = emp.characterData?.name ?? ""; } catch { }
                                Plugin.Logger.LogWarning($"[Integrity] {reason}: employee '{en}' was assigned to another player's business '{reg!.BusinessName}' — unassigned (cross-owner staffing can't sync).");
                            }
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] cross-owner-staff: {ex.Message}"); }
                if (crossFixed > 0) parts.Add($"cross-owner-staff×{crossFixed} repaired");

                // ── Class 7: foreign todos (REPAIR) ──────────────────────────
                // Field 2026-07-20: the objectives panel alerted about on-duty
                // staff that wasn't the player's.  ANY todo referencing an
                // injected/synthetic employee id or a partner-owned business is
                // wrong by definition — native todo generation has no concept of
                // "someone else's staff/shop".  ROUND-246 restructure: creation
                // is now REFUSED at the native chokepoint (see the AddNewTodoTask
                // gate below), so this purge only heals todos persisted by older
                // versions — and it removes via the game's row+data routine, not
                // data-only (data-only removal live-orphaned sidebar rows: the
                // 20260806-010308 permanent "On-Duty Staff" alerts).
                int todosFixed = PurgeForeignTodos(reason);
                if (todosFixed > 0) parts.Add($"foreign-todos×{todosFixed} repaired");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] sweep ({reason}): {ex.Message}"); }

            LastSummary = string.Join("; ", parts);
            if (LastSummary.Length > 0)
                Plugin.Logger.LogWarning($"[Integrity] {reason}: {LastSummary}.");
            else
                // Round-256 (T256 agent finding): a clean sweep must SAY it ran —
                // every pass-if-absent check on integrity lines is vacuous unless
                // the log proves the sweep executed.
                Plugin.Logger.LogInfo($"[Integrity] {reason}: sweep clean.");
        }

        /// <summary>Class 7's worker, ALSO called periodically (the 10-min duty
        /// tick) because foreign todos are created LIVE mid-session — a load-time
        /// sweep alone would leave the objectives panel wrong until next load.
        /// Foreign = references an injected/synthetic employee id (live-session
        /// knowledge only) or a partner-owned business (persisted stamp — works
        /// offline too).</summary>
        public static int PurgeForeignTodos(string reason)
        {
            int removed = 0, logged = 0;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.TodoTasks == null) return 0;
                for (int i = gi.TodoTasks.Count - 1; i >= 0; i--)
                {
                    var t = gi.TodoTasks[i];
                    if (t == null) continue;
                    string eid = "";
                    try { eid = t.employeeId ?? ""; } catch { }
                    if (!IsForeignTodoRef(eid, t.address)) continue;
                    // ROUND-246 (field 20260806-010308): removal must go through the game's own
                    // routine, which destroys the sidebar ROW together with the DATA.  This loop
                    // used to RemoveAt the data directly — native-legal at LOAD time (the game's
                    // compat fixes do the same, before the sidebar is built), but this method also
                    // runs LIVE on the 10-min tick, and a data-only removal there orphans the row:
                    // the native completion checker iterates the DATA list, so an orphaned row can
                    // never be struck through or removed — the field report's "permanent" alerts,
                    // and clicking one NREs the My Employees app on the dead synthetic id.
                    bool removedViaUi = false;
                    try
                    {
                        var ui = InstanceBehavior<UI.UIs>.Instance != null ? InstanceBehavior<UI.UIs>.Instance.tasksUI : null;
                        if (ui != null)
                        {
                            ui.InstantlyCompleteTodoTask(t);   // row + data + panel height, atomically
                            removedViaUi = !gi.TodoTasks.Contains(t);
                        }
                    }
                    catch { }
                    if (!removedViaUi) gi.TodoTasks.Remove(t); // headless (menu/load) — no row exists yet
                    removed++;
                    if (logged++ < LogCapPerClass)
                        Plugin.Logger.LogWarning($"[Integrity] {reason}: todo '{t.type}' referenced another player's staff/business — removed (objectives are own-only).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] foreign-todos: {ex.Message}"); }
            return removed;
        }

        /// <summary>Round-246 shared predicate — the ONE definition of "foreign todo
        /// reference", used by both the purge above and the creation gate below so the
        /// two can never drift apart.  Foreign = the employee id is one of our synthetic
        /// duty stand-ins or an injected partner-roster copy, OR the address resolves to
        /// a business owned by another session player (persisted stamp — works offline).
        /// A player's own staff and shops can never match; on a vanilla SP save nothing
        /// matches.</summary>
        internal static bool IsForeignTodoRef(string employeeId, Address? address)
        {
            string eid = employeeId ?? "";
            if (eid.Length > 0)
            {
                if (eid.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix)) return true;
                try { if (MPRegisterSync.IsInjectedStaff(eid)) return true; } catch { }
            }
            try
            {
                var reg = address != null ? Helpers.BuildingHelper.GetBuildingRegistration(address) : null;
                if (GameStatePatcher.HideFromOwnAssetLists(reg)) return true;
            }
            catch { }
            return false;
        }
    }

    /// <summary>Round-246 fix A — FOREIGN TODOS NEVER GET CREATED (field 20260806-010308
    /// "On-Duty Staff ... Permanent").  Native todo generation has no concept of "someone
    /// else's staff/shop": walking into a partner's business (item events regenerate the
    /// current building's todos) or any employee sweep can mint alerts about our duty
    /// stand-ins or the partner's shops.  Class-7's periodic purge then removed the DATA
    /// and orphaned the sidebar ROW — the report's permanent, click-to-crash entries.
    /// Gating here, at TasksUI.AddNewTodoTask — the single construction chokepoint every
    /// creation path funnels through (CreateTodoTask wraps it; TaxHelper calls it
    /// directly) — means no data AND no row, atomically: nothing to sweep later.  The
    /// purge stays as the healer for todos already persisted in older saves.</summary>
    [HarmonyLib.HarmonyPatch(typeof(UI.Tasks.TasksUI), nameof(UI.Tasks.TasksUI.AddNewTodoTask))]
    public static class Patch_TasksUI_AddNewTodoTask_ForeignGate
    {
        private static int _logged;

        static bool Prefix(ref Entities.TodoTask? __result, Entities.TodoTaskType type, Address address, string employeeId)
        {
            try
            {
                if (!MPSaveIntegrity.IsForeignTodoRef(employeeId, address)) return true;
            }
            catch { return true; }   // our failure must never block a legit todo
            __result = null;         // callers already handle null (duplicate-exists path)
            if (_logged++ < 12)
                Plugin.Logger.LogInfo($"[Integrity] todo '{type}' about another player's staff/business refused at creation (round-246; emp='{employeeId ?? ""}').");
            return false;
        }
    }
}
