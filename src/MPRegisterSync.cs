using System;
using System.Collections.Generic;
using BigAmbitions.Characters.Skills;   // SkillName
using Entities;                          // EmployeeInstance
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Player-staffed cash registers (Wave-2; design:
    /// .modding/03-systems/cross-player-shopping.md).
    ///
    /// Duty mirrors the game's own Work mechanic (user spec): the owner clicks
    /// their register, picks Work, a WorkActivity runs, we broadcast ON duty;
    /// stopping work broadcasts OFF.  No keybinds anywhere.
    ///
    /// Buying in a duty-staffed shop: shelves/basket are native; the register
    /// click routes to the game's SELF-CHECKOUT flow (Patch_RegisterInteract_
    /// SelfCheckout) and the ORDER is finalized by the MP finalizer
    /// (Patch_MPOrderFinalizer) — charge from the synced store table, revenue
    /// + authoritative stock decrement on the owner side via RemoteSale.
    ///
    /// TWO duty kinds (user ruling 2026-06-12):
    ///  * PERSONAL — the owner works the register; their avatar is the visual,
    ///    nothing is spawned.
    ///  * EMPLOYEE — the owner's hired staff covers the station (data-driven
    ///    scan on the owner's machine).  Receivers spawn a VISIBLE synthetic
    ///    staff NPC for immersion (roster + WorkShift injection + forced
    ///    staffing gate — resurrected from the deleted owner-mime experiment,
    ///    where invisible-NPC-as-owner was the wrong idea; visible-NPC-as-staff
    ///    is the right one).  Commerce never depends on the NPC: the buyer
    ///    "never knows if it's a system auto ringing them up or the employee."
    ///
    /// Registers are keyed by rounded world position (stable across peers --
    /// interiors are host-snapshot replicas at identical coordinates;
    /// verified cross-machine 2026-06-11: '900:0:-4' matched on both).
    /// </summary>
    public static class MPRegisterSync
    {
        // posKey → cashier.  CONCURRENT: Apply runs on the network poll thread
        // (RegisterCashier handler) and the main thread (local duty echo), while
        // the checkout-routing Harmony patches read it on the main thread.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string playerId, Vector3 pos, bool employee, string address, string stationId)> _cashiers = new();

        public static void Reset()
        {
            // Remove the synthetic duty NPCs we injected from the live game first,
            // so a scene-exit/disconnect autosave can't write them into a save (they
            // are runtime-only and would otherwise orphan — they live in gi.Employee-
            // Instances but not in _synthetics after a load).  RemoveSynthetic clears
            // each from gi + the dictionary + its work shifts; iterate a key copy
            // because it mutates _synthetics.
            foreach (var key in new List<string>(_synthetics.Keys)) RemoveSynthetic(key);
            _cashiers.Clear(); _empDuty.Clear(); _synthetics.Clear(); _onDuty = false; CurrentShopOwner = ""; CurrentShopAddress = "";
        }

        // ── Current building context (set by the building entry patch) ────────
        // After the rival-translation, businessOwnerRivalId holds the OWNING
        // PLAYER's id for player businesses (a real rival GUID for AI ones —
        // the host validates against the lobby roster, so AI ids fall out).
        public static string CurrentShopOwner   { get; private set; } = "";
        public static string CurrentShopAddress { get; private set; } = "";

        public static void SetCurrentShop(string ownerId, string address)
        {
            CurrentShopOwner = ownerId ?? "";
            CurrentShopAddress = address ?? "";
        }

        private static string Key(Vector3 p)
            => $"{Mathf.RoundToInt(p.x)}:{Mathf.RoundToInt(p.y)}:{Mathf.RoundToInt(p.z)}";

        /// <summary>Apply an on/off-duty state (local echo + remote).</summary>
        public static void Apply(RegisterCashierPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.PlayerId)) return;
            var pos = new Vector3(p.X, p.Y, p.Z);
            string k = Key(pos);
            if (p.On)
            {
                _cashiers[k] = (p.PlayerId, pos, p.Employee, p.Address ?? "", p.StationId ?? "");
                Plugin.Logger.LogInfo($"[Register] '{p.PlayerId}' ON duty at {k}{(p.Employee ? " (employee)" : "")}.");
                // EMPLOYEE duty on another machine → spawn the visible staff
                // NPC (immersion; commerce works without it).  Main-thread:
                // Apply can run on the network poll thread.
                if (p.Employee && p.PlayerId != MPConfig.PlayerId && !string.IsNullOrEmpty(p.Address))
                {
                    string addr = p.Address; string pid = p.PlayerId; string st = p.StationId;
                    var dutyPos = pos;
                    GameStatePatcher.EnqueueOnMainThread(() => TryStaffSynthetic(addr, pid, st, dutyPos));
                }
            }
            else if (_cashiers.TryGetValue(k, out var e) && e.playerId == p.PlayerId)
            {
                _cashiers.TryRemove(k, out _);
                Plugin.Logger.LogInfo($"[Register] '{p.PlayerId}' OFF duty at {k}.");
                if (e.employee && p.PlayerId != MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => RemoveSyntheticAtStation(k));
            }
        }

        /// <summary>A player disconnected — their duty posts clear.</summary>
        public static void RemovePlayer(string playerId)
        {
            var dead = new List<string>();
            foreach (var kv in _cashiers) if (kv.Value.playerId == playerId) dead.Add(kv.Key);
            foreach (var k in dead)
            {
                bool wasEmployee = _cashiers[k].employee;
                _cashiers.TryRemove(k, out _);
                if (wasEmployee && playerId != MPConfig.PlayerId)
                {
                    var key = k;
                    GameStatePatcher.EnqueueOnMainThread(() => RemoveSyntheticAtStation(key));
                }
            }
            if (dead.Count > 0) Plugin.Logger.LogInfo($"[Register] cleared {dead.Count} duty post(s) of departed '{playerId}'.");
        }

        private static Controllers.CashRegisterController? FindNearestRegister(Vector3 from, float maxDist)
        {
            Controllers.CashRegisterController? best = null;
            float bestD2 = maxDist * maxDist;
            var arr = UnityEngine.Object.FindObjectsOfType(typeof(Controllers.CashRegisterController));
            if (arr != null)
                foreach (var o in arr)
                {
                    var c = o as Controllers.CashRegisterController;
                    if (c == null) continue;
                    float d2 = (c.transform.position - from).sqrMagnitude;
                    if (d2 < bestD2) { bestD2 = d2; best = c; }
                }
            return best;
        }

        // ── Duty: driven by the NATIVE Work mechanic (user spec) — click your
        // register → "Work" → the game runs a WorkActivity; we mirror that
        // activity into the duty map.  Stop working (any way the game allows)
        // → off duty.  No extra keybind for the cashier.
        private static float _nextDutyAt;
        private static bool _onDuty;
        private static Vector3 _dutyPos;

        public static void TickDuty()
        {
            if (Time.unscaledTime < _nextDutyAt) return;
            _nextDutyAt = Time.unscaledTime + 1f;
            try
            {
                bool working = MPRestSync.CurrentActivityName() == "Work";
                if (working && !_onDuty)
                {
                    // The EXACT station from the live WorkActivity (it holds
                    // _employeeStationController — decompile sweep 2026-06-12;
                    // replaces the nearest-register-in-5m guess that could bind
                    // duty to the wrong till in dense shops).
                    Controllers.CashRegisterController? reg = null;
                    try
                    {
                        var act = MPRestSync.CurrentActivityObject;
                        if (act != null)
                            reg = MPReflect.Get(act.GetType(), act, "_employeeStationController")
                                  as Controllers.CashRegisterController;
                    }
                    catch { }
                    if (reg == null)
                    {
                        // Fallback: proximity (activity shape drifted / non-register station type).
                        var ch = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                        if (ch == null) return;
                        reg = FindNearestRegister(ch.position, 5f);
                    }
                    if (reg == null) return;   // working some other station — not register duty
                    _onDuty = true;
                    _dutyPos = reg.transform.position;
                    // If this station was broadcast as EMPLOYEE-staffed (the
                    // schedule query also matches the working owner), retract
                    // that first — personal duty owns the till now.
                    string myKey = Key(_dutyPos);
                    if (_empDuty.ContainsKey(myKey))
                    {
                        SendToggle(_empDuty[myKey], false);
                        _empDuty.Remove(myKey);
                    }
                    SendToggle(_dutyPos, true);
                    // Worker-side price-table snapshot — cross-machine evidence
                    // pairing for any future price dispute.
                    try
                    {
                        var gi = SaveGameManager.Current;
                        if (gi != null)
                            foreach (var r in gi.BuildingRegistrations)
                                if (r != null && GameStateReader.AddressKey(r) == CurrentShopAddress)
                                { LogShopPrices(r, "[Register/prices]"); break; }
                    }
                    catch { }
                }
                else if (!working && _onDuty)
                {
                    _onDuty = false;
                    SendToggle(_dutyPos, false);
                }

                TickEmployeeDuty();                               // employee-staffed stations (data-driven, 5s)
                TickStaffEvaluator();                             // spawn the visible staff NPC (5s)
                MPPatches.Patch_MPOrderFinalizer.TickPending();   // service-moment completion
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Register] duty: {ex.Message}"); }
        }

        /// <summary>Is the register at this world position staffed by another
        /// player?  The duty map's ONE consumer-facing question — gates the
        /// self-checkout routing and the queue guard.</summary>
        public static bool IsStaffedByOtherPlayer(Vector3 registerPos)
            => _cashiers.TryGetValue(Key(registerPos), out var e) && e.playerId != MPConfig.PlayerId;

        /// <summary>Snapshot of the current duty posts, as ON payloads — for
        /// re-syncing a (re)joining peer.  Duty is EVENT-tracked (broadcast only
        /// when it changes via SendToggle), and a (re)connect runs Reset() during
        /// the world reload, wiping the peer's map.  The reconnect resync re-sends
        /// world/rivals/business but NOT duty, so a reconnected client saw staffed
        /// tills as unstaffed ("no employees") until a toggle re-broadcast (field
        /// bug 2026-06-13: host personally on the till, client reconnected mid-
        /// session, couldn't buy until the host toggled off/on).  The host's map is
        /// the authoritative union (it applies + relays every RegisterCashier).</summary>
        public static List<RegisterCashierPayload> SnapshotDuty()
        {
            var list = new List<RegisterCashierPayload>();
            foreach (var kv in _cashiers)
            {
                var e = kv.Value;
                list.Add(new RegisterCashierPayload
                {
                    PlayerId = e.playerId, X = e.pos.x, Y = e.pos.y, Z = e.pos.z,
                    On = true, Employee = e.employee, Address = e.address, StationId = e.stationId
                });
            }
            return list;
        }

        private static void SendToggle(Vector3 pos, bool on, string? address = null,
                                       bool employee = false, string stationId = "")
        {
            var p = new RegisterCashierPayload
            {
                PlayerId = MPConfig.PlayerId, X = pos.x, Y = pos.y, Z = pos.z, On = on,
                Address = address ?? CurrentShopAddress,   // personal duty: worker is inside the shop
                Employee = employee,
                StationId = stationId
            };
            Apply(p);   // local echo first — instant feedback
            var env = MessageEnvelope.Create(MessageType.RegisterCashier, MPConfig.PlayerId, p);
            if (MPServer.IsRunning) MPServer.BroadcastAny(env);
            else MPClient.SendEnvelope(env);
        }

        // ── Employee-staffed registers (user batch, 2026-06-12): a shop run by
        // MY HIRED EMPLOYEES must be shoppable for visitors even when I'm not
        // at the till.  Their machines can't simulate my employees (the
        // staffing evaluator refuses rival-translated shops), so the OWNER's
        // machine answers from DATA: every 5s, for each of my businesses, each
        // checkout-station item instance, ask the game's own
        // IsEmployeeStationEmployedAtHour and broadcast duty ON/OFF.  Visitors
        // then buy through the same self-checkout + RemoteSale path.  KNOWN
        // cosmetic gap: visitors see no cashier body at employee-run tills.
        private static float _nextEmpScanAt;
        private static readonly Dictionary<string, Vector3> _empDuty = new();   // posKey → pos

        private static void TickEmployeeDuty()
        {
            if (Time.unscaledTime < _nextEmpScanAt) return;
            _nextEmpScanAt = Time.unscaledTime + 5f;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                var live = new Dictionary<string, (Vector3 pos, string addr, string stationId)>();
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    bool mine; try { mine = reg.RentedByPlayer; } catch { continue; }
                    if (!mine || reg.itemInstances == null) continue;
                    string addr = GameStateReader.AddressKey(reg);
                    foreach (var kv in reg.itemInstances)
                    {
                        var ii = kv.Value;
                        if (ii == null) continue;
                        var iname = ii.itemName;
                        if (iname != "ba:itemname_checkoutcounterright"
                            && iname != "ba:itemname_checkoutcounterleft"
                            && iname != "ba:itemname_cashregister") continue;
                        bool staffed = false;
                        string stationId = ii.id?.ToString() ?? "";
                        try { staffed = Helpers.EmployeeHelper.IsEmployeeStationEmployedAtHour(reg, stationId, -1); }
                        catch { }
                        if (!staffed) continue;
                        var pos = new Vector3(ii.position.x, ii.position.y, ii.position.z);
                        // The OWNER personally working reads as "employed" to the
                        // schedule query — that's PERSONAL duty, not staff (user
                        // saw a staff NPC spawn on top of their working avatar).
                        if (_onDuty && Key(_dutyPos) == Key(pos)) continue;
                        live[Key(pos)] = (pos, addr, stationId);
                    }
                }
                // Newly staffed stations → ON; no-longer-staffed → OFF.
                foreach (var kv in live)
                    if (!_empDuty.ContainsKey(kv.Key))
                    {
                        _empDuty[kv.Key] = kv.Value.pos;
                        SendToggle(kv.Value.pos, true, kv.Value.addr, employee: true, stationId: kv.Value.stationId);
                        Plugin.Logger.LogInfo($"[Register] employee-staffed station ON at {kv.Key} ({kv.Value.addr}).");
                    }
                var stale = new List<string>();
                foreach (var kv in _empDuty)
                    if (!live.ContainsKey(kv.Key)) stale.Add(kv.Key);
                foreach (var k in stale)
                {
                    // Don't stomp PERSONAL duty at the same register.
                    if (!(_onDuty && Key(_dutyPos) == k)) SendToggle(_empDuty[k], false);
                    _empDuty.Remove(k);
                    Plugin.Logger.LogInfo($"[Register] employee-staffed station OFF at {k}.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Register] employee duty: {ex.Message}"); }
        }

        /// <summary>The CURRENT shop's set price for an item (synced store
        /// table — the only charge source that matters per user), or -1 when
        /// the table has no entry.</summary>
        public static float GetShopPrice(string itemName)
            => GetShopPriceAt(CurrentShopAddress, itemName);

        /// <summary>Same lookup for an arbitrary shop address key.</summary>
        public static float GetShopPriceAt(string addressKey, string itemName)
        {
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null || string.IsNullOrEmpty(addressKey)) return -1f;
                foreach (var r in gi.BuildingRegistrations)
                {
                    if (r == null || GameStateReader.AddressKey(r) != addressKey) continue;
                    var prices = r.retailPrices;
                    if (prices != null)
                        for (int i = 0; i < prices.Count; i++)
                        {
                            var rp = prices[i];
                            if (rp != null && rp.itemName == itemName) return rp.price;
                        }
                    return -1f;
                }
            }
            catch { }
            return -1f;
        }

        // ── Visible staff NPC (employee duty only; user ruling 2026-06-12) ────
        // Receivers inject a real EmployeeInstance (game's own factory) into
        // the roster + a blanket WorkShift on the station, force the staffing
        // gate (the evaluator refuses rival-translated shops), and invoke the
        // evaluator until the game spawns the serving NPC.  The NPC stays
        // VISIBLE — it represents the owner's staff.  One synthetic per
        // ADDRESS (v1; multi-station shops get one body).  Commerce never
        // depends on this: self-checkout + RemoteSale run regardless.
        private static readonly Dictionary<string, (string playerId, EmployeeInstance inst, Vector3 pos)> _synthetics = new();
        private static float _nextEvalAt;

        /// <summary>Is this station under EMPLOYEE duty (used by the staffing
        /// gate override — personal duty must never force the gate).</summary>
        public static bool IsEmployeeDutyStation(Vector3 pos)
            => _cashiers.TryGetValue(Key(pos), out var e) && e.employee;

        private static void TryStaffSynthetic(string addressKey, string playerId, string stationId, Vector3 dutyPos)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || _synthetics.ContainsKey(addressKey)) return;
                var gi = SaveGameManager.Current;
                if (gi == null) return;

                BuildingRegistration? reg = null;
                foreach (var r in gi.BuildingRegistrations)
                    if (r != null && GameStateReader.AddressKey(r) == addressKey) { reg = r; break; }
                if (reg == null)
                {
                    Plugin.Logger.LogWarning($"[SynthStaff] no registration found for '{addressKey}' — cannot staff.");
                    return;
                }

                var inst = Helpers.EmployeeHelper.CreateAIEmployeeInstance("ba:skill_customerservice");
                if (inst == null) { Plugin.Logger.LogWarning("[SynthStaff] factory returned null."); return; }
                inst.id = $"BAMP_DUTY_{playerId}_{addressKey.Replace(' ', '_')}";
                inst.hourlyWage = 0f;
                inst.satisfaction = 100f;
                inst.assignedAddress = new Address(reg.StreetName, reg.StreetNumber);
                // REQUIRED: the AI-employee factory leaves characterData.name null, but this synthetic is added
                // to gi.EmployeeInstances + EmployeeInstancesDictionary, which the game iterates. A null name
                // throws ArgumentNullException in the phone Contacts list-build (ContactScrollerController
                // .BuildEmployeeByNameLookup → ContainsKey(null)) and in EmployeeTooltip (name.Localize()) —
                // breaking the Contacts app. Any non-empty name closes both. (2026-06-19 bug fix.)
                inst.characterData.name = "On-Duty Staff";
                // Also give it a sane adult age — the factory leaves ageInDays = 0, which reads as a newborn
                // and skews the retirement-notice math in the daily employee tick. Use the game's own helper.
                inst.characterData.ageInDays = Helpers.RecruitmentHelper.GetRandomEmployeeAgeInDays();

                // Backstop against duplicate roster entries: a prior save could have
                // persisted a synthetic with this same deterministic id before the
                // load-boundary strip ran.  Drop any existing match before adding.
                for (int i = gi.EmployeeInstances.Count - 1; i >= 0; i--)
                    if (gi.EmployeeInstances[i]?.id == inst.id) gi.EmployeeInstances.RemoveAt(i);

                gi.EmployeeInstances.Add(inst);
                try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[inst.id] = inst; } catch { }
                _synthetics[addressKey] = (playerId, inst, dutyPos);

                int shifts = 0;
                if (!string.IsNullOrEmpty(stationId) && reg.scheduleDays != null)
                    for (int i = 0; i < reg.scheduleDays.Count; i++)
                    {
                        var day = reg.scheduleDays[i];
                        if (day == null) continue;
                        day.AddWorkShift(new WorkShift
                        {
                            startingHour   = 0,
                            endingHour     = 24,
                            employeeId     = inst.id,
                            itemInstanceId = stationId,
                            type           = WorkShiftType.Default,
                        });
                        shifts++;
                    }
                Plugin.Logger.LogInfo(
                    $"[SynthStaff] staff NPC injected for '{playerId}' at '{addressKey}' " +
                    $"({shifts} shift(s), station '{stationId}').");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] staff '{addressKey}': {ex}"); }
        }

        /// <summary>Duty went OFF at a station — remove the synthetic whose
        /// duty position matches.</summary>
        private static void RemoveSyntheticAtStation(string posKey)
        {
            string? addr = null;
            foreach (var kv in _synthetics)
                if (Key(kv.Value.pos) == posKey) { addr = kv.Key; break; }
            if (addr != null) RemoveSynthetic(addr);
        }

        private static void RemoveSynthetic(string addressKey)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || !_synthetics.TryGetValue(addressKey, out var s)) return;
                _synthetics.Remove(addressKey);
                try { Helpers.EmployeeHelper.UnassignEmployeeFromAllWorkshifts(s.inst); }
                catch (Exception ux) { Plugin.Logger.LogWarning($"[SynthStaff] unassign: {ux.Message}"); }
                var gi = SaveGameManager.Current;
                try
                {
                    if (gi?.BuildingRegistrations != null)
                        foreach (var r in gi.BuildingRegistrations)
                        {
                            if (r == null || GameStateReader.AddressKey(r) != addressKey) continue;
                            if (r.scheduleDays != null)
                                for (int i = 0; i < r.scheduleDays.Count; i++)
                                {
                                    var day = r.scheduleDays[i];
                                    if (day?.workShifts == null) continue;
                                    var dead = new List<WorkShift>();
                                    for (int j = 0; j < day.workShifts.Count; j++)
                                    {
                                        var w = day.workShifts[j];
                                        if (w != null && w.employeeId == s.inst.id) dead.Add(w);
                                    }
                                    foreach (var w in dead) day.RemoveWorkShift(w);
                                }
                            break;
                        }
                }
                catch (Exception sx) { Plugin.Logger.LogWarning($"[SynthStaff] shift strip: {sx.Message}"); }
                if (gi?.EmployeeInstances != null) gi.EmployeeInstances.Remove(s.inst);
                try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(s.inst.id); } catch { }
                Plugin.Logger.LogInfo($"[SynthStaff] staff NPC removed at '{addressKey}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] remove '{addressKey}': {ex.Message}"); }
        }

        /// <summary>Remove ORPHANED synthetic duty employees (id prefix "BAMP_DUTY_")
        /// left in gi.EmployeeInstances by a prior save.  Synthetic staff are
        /// runtime-only and tracked in _synthetics; after a load _synthetics is empty
        /// but the deserialized copies remain in the roster, where they accumulate one
        /// duplicate per save/load cycle and are unreachable for cleanup.  Run at the
        /// world-ready load boundary, where no live serving NPC exists yet, so it is
        /// safe to strip every such entry NOT currently tracked in _synthetics (the
        /// live ones — e.g. injected during join quiesce — are kept).</summary>
        public static int StripOrphanSyntheticEmployees(string when)
        {
            int removed = 0;
            try
            {
                var gi = SaveGameManager.Current;
                var list = gi?.EmployeeInstances;
                if (list == null) return 0;
                var live = new HashSet<string>();
                foreach (var kv in _synthetics)
                    if (kv.Value.inst != null && !string.IsNullOrEmpty(kv.Value.inst.id)) live.Add(kv.Value.inst.id);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    string id = list[i]?.id ?? "";
                    if (!id.StartsWith("BAMP_DUTY_") || live.Contains(id)) continue;
                    // Strip this employee's work shifts from every registration first.
                    try
                    {
                        if (gi!.BuildingRegistrations != null)
                            foreach (var r in gi.BuildingRegistrations)
                            {
                                if (r?.scheduleDays == null) continue;
                                for (int d = 0; d < r.scheduleDays.Count; d++)
                                {
                                    var day = r.scheduleDays[d];
                                    if (day?.workShifts == null) continue;
                                    var dead = new List<WorkShift>();
                                    for (int j = 0; j < day.workShifts.Count; j++)
                                        if (day.workShifts[j]?.employeeId == id) dead.Add(day.workShifts[j]);
                                    foreach (var w in dead) day.RemoveWorkShift(w);
                                }
                            }
                    }
                    catch (Exception sx) { Plugin.Logger.LogWarning($"[SynthStaff] orphan shift strip ({when}): {sx.Message}"); }
                    list.RemoveAt(i);
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
                    removed++;
                    Plugin.Logger.LogInfo($"[SynthStaff] orphan staff stripped ({when}): '{id}'.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] orphan strip ({when}): {ex.Message}"); }
            return removed;
        }

        /// <summary>SAVE-path strip (anti-pattern Class 5): the synthetic register cashiers (BAMP_DUTY_*) and
        /// their injected WorkShifts are MP-only runtime objects and must NOT be serialised into the .hsg —
        /// they would leak into a single-player load, where the world-ready cleanup never runs.  Unlike
        /// StripOrphanSyntheticEmployees, this removes ALL of them (live ones included), then RESTORES the exact
        /// objects via the returned delegate so the live MP session is undisturbed (only the on-disk bytes are
        /// clean).  Call the delegate AFTER serialization finishes (JoinSaveGameThreads) — NEVER before, since
        /// mutating gi mid-serialise corrupts the save.  Safe to call on a save that has none (returns a no-op).</summary>
        public static System.Action StripSyntheticsForSave(string when)
        {
            var removedEmployees = new List<EmployeeInstance>();
            var removedShifts    = new List<(ScheduleDay day, WorkShift shift)>();
            try
            {
                var gi   = SaveGameManager.Current;
                var list = gi?.EmployeeInstances;
                if (list == null) return () => { };
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var emp = list[i];
                    string id = emp?.id ?? "";
                    if (!id.StartsWith("BAMP_DUTY_")) continue;
                    // Capture + strip this synthetic's work shifts from every registration.
                    try
                    {
                        if (gi!.BuildingRegistrations != null)
                            foreach (var r in gi.BuildingRegistrations)
                            {
                                if (r?.scheduleDays == null) continue;
                                for (int d = 0; d < r.scheduleDays.Count; d++)
                                {
                                    var day = r.scheduleDays[d];
                                    if (day?.workShifts == null) continue;
                                    var dead = new List<WorkShift>();
                                    for (int j = 0; j < day.workShifts.Count; j++)
                                        if (day.workShifts[j]?.employeeId == id) dead.Add(day.workShifts[j]);
                                    foreach (var w in dead) { removedShifts.Add((day, w)); day.RemoveWorkShift(w); }
                                }
                            }
                    }
                    catch (Exception sx) { Plugin.Logger.LogWarning($"[SynthStaff] save shift strip ({when}): {sx.Message}"); }
                    removedEmployees.Add(emp!);
                    list.RemoveAt(i);
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
                }
                if (removedEmployees.Count > 0)
                    Plugin.Logger.LogInfo($"[SynthStaff] stripped {removedEmployees.Count} synthetic(s) for save ({when}); restore after serialize.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] save strip ({when}): {ex.Message}"); }

            // Restore delegate — re-add the EXACT objects after serialization completes (dup-guarded; the main
            // thread is blocked through the save, so no tick can re-inject during the window, but be defensive).
            return () =>
            {
                try
                {
                    var gi = SaveGameManager.Current;
                    if (gi?.EmployeeInstances == null) return;
                    foreach (var emp in removedEmployees)
                    {
                        if (emp == null || string.IsNullOrEmpty(emp.id)) continue;
                        bool exists = false;
                        for (int i = 0; i < gi.EmployeeInstances.Count; i++)
                            if (gi.EmployeeInstances[i]?.id == emp.id) { exists = true; break; }
                        if (!exists) gi.EmployeeInstances.Add(emp);
                        try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[emp.id] = emp; } catch { }
                    }
                    foreach (var (day, shift) in removedShifts)
                    {
                        if (day?.workShifts == null || shift == null) continue;
                        bool present = false;
                        for (int j = 0; j < day.workShifts.Count; j++)
                            if (ReferenceEquals(day.workShifts[j], shift)) { present = true; break; }
                        if (!present) day.AddWorkShift(shift);
                    }
                    if (removedEmployees.Count > 0)
                        Plugin.Logger.LogInfo($"[SynthStaff] restored {removedEmployees.Count} synthetic(s) after save ({when}).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] save restore ({when}): {ex.Message}"); }
            };
        }

        /// <summary>Invoke the game's staffing evaluator (UpdateEmployee — the
        /// disasm-mapped spawner) on unstaffed synthetic stations every 5s;
        /// nothing calls it for rival-translated shops natively.</summary>
        private static void TickStaffEvaluator()
        {
            if (_synthetics.Count == 0) return;
            if (Time.unscaledTime < _nextEvalAt) return;
            _nextEvalAt = Time.unscaledTime + 5f;
            foreach (var kv in _synthetics)
            {
                try
                {
                    var reg = FindNearestRegister(kv.Value.pos, 2f);
                    if (reg == null) continue;                    // interior not loaded here
                    if (reg.employeeInstance != null) continue;   // already staffed
                    Plugin.Logger.LogInfo($"[SynthStaff] invoking UpdateEmployee(false) on register at '{kv.Key}'.");
                    reg.UpdateEmployee(false);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] evaluator: {ex.Message}"); }
            }
        }

        /// <summary>Dump the shop's SET price table (the only price source that
        /// matters at checkout per user — cargo pricePerUnit is NOT it).</summary>
        internal static void LogShopPrices(BuildingRegistration reg, string tag)
        {
            try
            {
                var prices = reg.retailPrices;
                if (prices == null || prices.Count == 0)
                {
                    Plugin.Logger.LogInfo($"{tag} '{GameStateReader.AddressKey(reg)}' retailPrices EMPTY (defaults not materialized).");
                    return;
                }
                var sb = new System.Text.StringBuilder($"{tag} '{GameStateReader.AddressKey(reg)}' retailPrices: ");
                for (int i = 0; i < prices.Count && i < 8; i++)
                {
                    var rp = prices[i];
                    if (rp != null) sb.Append($"{rp.itemName}=${rp.price:F2} ");
                }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{tag} read: {ex.Message}"); }
        }

    }
}
