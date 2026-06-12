using System;
using System.Collections.Generic;
using BigAmbitions.Characters.Skills;   // SkillName
using Entities;                          // EmployeeInstance
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Player-staffed cash registers (Wave-2; design:
    /// .modding/03-systems/cross-player-shopping.md).
    ///
    /// The game's own flows dead-end for us: an owner interacting with their
    /// register gets the WORK/management UI, and on other machines the
    /// register looks unstaffed, so a visiting player gets nothing.
    /// Duty mirrors the game's own Work mechanic (user spec): the owner clicks
    /// their register, picks Work, a WorkActivity runs, we broadcast ON duty;
    /// stopping work broadcasts OFF.  No keybinds anywhere.  Buying stays the
    /// NATIVE flow (pick items, click register, walk up, pay); the duty map
    /// exists solely so the future staffed-gate patch can answer 'is a player
    /// working this register' on the customer's machine.
    /// Registers are keyed by rounded world position (stable across peers --
    /// interiors are host-snapshot replicas at identical coordinates;
    /// verified cross-machine 2026-06-11: '900:0:-4' matched on both).
    /// </summary>
    public static class MPRegisterSync
    {
        // posKey → cashier playerId
        private static readonly Dictionary<string, (string playerId, Vector3 pos)> _cashiers = new();

        public static void Reset() { _cashiers.Clear(); _synthetics.Clear(); _hiddenNpcs.Clear(); _onDuty = false; CurrentShopOwner = ""; CurrentShopAddress = ""; }

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
                _cashiers[k] = (p.PlayerId, pos);
                Plugin.Logger.LogInfo($"[Register] '{p.PlayerId}' ON duty at {k}.");
                // Synthetic staffing on every machine EXCEPT the worker's own
                // (there the real player works natively).  Main-thread: Apply
                // can run on the network poll thread; staffing touches IL2CPP.
                if (p.PlayerId != MPConfig.PlayerId && !string.IsNullOrEmpty(p.Address))
                {
                    string addr = p.Address; string pid = p.PlayerId; string st = p.StationId;
                    var dutyPos = pos;
                    GameStatePatcher.EnqueueOnMainThread(() => TryStaffSynthetic(addr, pid, st, dutyPos));
                }
            }
            else if (_cashiers.TryGetValue(k, out var e) && e.playerId == p.PlayerId)
            {
                _cashiers.Remove(k);
                Plugin.Logger.LogInfo($"[Register] '{p.PlayerId}' OFF duty at {k}.");
                if (p.PlayerId != MPConfig.PlayerId && !string.IsNullOrEmpty(p.Address))
                {
                    string addr = p.Address;
                    GameStatePatcher.EnqueueOnMainThread(() => RemoveSynthetic(addr));
                }
            }
        }

        /// <summary>A player disconnected — their duty posts clear.</summary>
        public static void RemovePlayer(string playerId)
        {
            var dead = new List<string>();
            foreach (var kv in _cashiers) if (kv.Value.playerId == playerId) dead.Add(kv.Key);
            foreach (var k in dead) _cashiers.Remove(k);
            if (dead.Count > 0) Plugin.Logger.LogInfo($"[Register] cleared {dead.Count} duty post(s) of departed '{playerId}'.");
            GameStatePatcher.EnqueueOnMainThread(() => RemoveSyntheticsOf(playerId));
        }

        private static Controllers.CashRegisterController? FindNearestRegister(Vector3 from, float maxDist)
        {
            Controllers.CashRegisterController? best = null;
            float bestD2 = maxDist * maxDist;
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Controllers.CashRegisterController>());
            if (arr != null)
                foreach (var o in arr)
                {
                    var c = o.TryCast<Controllers.CashRegisterController>();
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
                    var ch = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                    if (ch == null) return;
                    var reg = FindNearestRegister(ch.position, 5f);
                    if (reg == null) return;   // working some other station — not register duty
                    _onDuty = true;
                    _dutyPos = reg.transform.position;
                    // Register's ItemInstance id — receivers bind the synthetic's
                    // WorkShift to it (the station's staffing query keys on it).
                    string stationId = "";
                    try { stationId = reg._itemInstance?.id ?? ""; } catch { }
                    SendToggle(_dutyPos, true, stationId);
                    // Worker-side price-table snapshot — pairs with the buyer-side
                    // [SynthStaff/prices] line for cross-machine comparison.
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

                TickHideSyntheticBodies();   // v2 polish — same 1s cadence
                TickStaffEvaluator();        // invoke the mapped evaluator directly
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Register] duty: {ex.Message}"); }
        }

        // ── Direct evaluator invocation (disasm-mapped 2026-06-12) ────────────
        // UpdateEmployee(bool) IS the staffing evaluator+spawner (gates:
        // ShouldUpdateEmployee → ItemHelper.HasAnyMissingRequirements →
        // GetEmployeeWorkShift → GetEmployeeById → availability → CreatePrefab).
        // Nothing calls it for rival-translated shops on this machine, so we
        // call it ourselves on unstaffed duty registers every 5s; the gate
        // probes ([StaffEval]) log exactly where it passes or bails.
        private static float _nextEvalAt;

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
                    if (reg == null) continue;            // interior not loaded here
                    if (reg.employeeInstance != null) continue;   // already staffed
                    Plugin.Logger.LogInfo($"[SynthStaff] invoking UpdateEmployee(false) on register at '{kv.Key}'.");
                    reg.UpdateEmployee(false);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] evaluator: {ex.Message}"); }
            }
        }

        // ── v2 polish: the synthetic employee's NPC body is plumbing — the
        // working PLAYER's avatar is the visual (user spec: no second body at
        // the register).  Once the NPC mans a duty register, strip its
        // renderers; it keeps serving invisibly.  NPC despawns with removal,
        // so no un-hide path is needed.
        private static readonly HashSet<IntPtr> _hiddenNpcs = new();

        private static void TickHideSyntheticBodies()
        {
            if (_cashiers.Count == 0) return;
            foreach (var kv in _cashiers)
            {
                if (kv.Value.playerId == MPConfig.PlayerId) continue;
                Controllers.CashRegisterController? reg = null;
                try { reg = FindNearestRegister(kv.Value.pos, 2f); } catch { }
                if (reg == null) continue;
                try
                {
                    var inst = reg.employeeInstance;
                    if (inst == null || inst.id == null || !inst.id.StartsWith("BAMP_DUTY_")) continue;
                    var emp = reg.employee;
                    if (emp == null) continue;
                    var ptr = emp.Pointer;
                    if (_hiddenNpcs.Contains(ptr)) continue;
                    int n = 0;
                    var rends = emp.GetComponentsInChildren<Renderer>(true);
                    if (rends != null)
                        foreach (var r in rends)
                            if (r != null && r.enabled) { r.enabled = false; n++; }
                    _hiddenNpcs.Add(ptr);
                    Plugin.Logger.LogInfo(
                        $"[SynthStaff] NPC body hidden at {kv.Key} ({n} renderer(s)) — the worker's avatar is the visual.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] hide: {ex.Message}"); }
            }
        }

        /// <summary>Is the register at this world position staffed by another
        /// player?  This is the duty map's ONE consumer-facing question — the
        /// future staffed-gate patch asks it so the NATIVE customer flow
        /// (pick items → click register → walk up → pay) passes when a player
        /// is the cashier.  (The F4 buy bypass was removed — user: buying must
        /// work the way the game normally does it.)</summary>
        public static bool IsStaffedByOtherPlayer(Vector3 registerPos)
            => _cashiers.TryGetValue(Key(registerPos), out var e) && e.playerId != MPConfig.PlayerId;

        private static void SendToggle(Vector3 pos, bool on, string stationId = "")
        {
            var p = new RegisterCashierPayload
            {
                PlayerId = MPConfig.PlayerId, X = pos.x, Y = pos.y, Z = pos.z, On = on,
                Address = CurrentShopAddress,   // worker is inside the shop when toggling
                StationId = stationId
            };
            Apply(p);   // local echo first — instant feedback
            var env = MessageEnvelope.Create(MessageType.RegisterCashier, MPConfig.PlayerId, p);
            if (MPServer.IsRunning) MPServer.BroadcastAny(env);
            else MPClient.SendEnvelope(env);
        }

        // ── Synthetic staffing (Wave-2, user ruling 2026-06-11) ───────────────
        // The native customer flow needs a REAL serving employee on the
        // customer's machine; the working player is invisible to the game
        // there.  While a player is on duty, every OTHER machine injects a
        // synthetic EmployeeInstance (built by the game's own factory) into the
        // global roster assigned to that shop — the game's employee simulation
        // then spawns/serves natively, exactly like the proven pre-session-hire
        // path.  Removed on duty-off / player leave.  The duty OWNER's machine
        // never injects (the real player works there natively), so the
        // authoritative host save can only pick one up if a CLIENT-owned shop
        // is involved — synthetic ids are prefixed for a later save-strip.
        private static readonly Dictionary<string, (string playerId, EmployeeInstance inst, Vector3 pos)> _synthetics = new();

        /// <summary>True when this world position is a register some player is
        /// working (probe filter — keeps station-evaluator logging on-topic).</summary>
        public static bool IsDutyStation(Vector3 pos) => _cashiers.ContainsKey(Key(pos));

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

                int before = gi.EmployeeInstances?.Count ?? -1;
                var inst = Helpers.EmployeeHelper.CreateAIEmployeeInstance(SkillName.CustomerService);
                if (inst == null) { Plugin.Logger.LogWarning("[SynthStaff] factory returned null."); return; }
                inst.id = $"BAMP_DUTY_{playerId}_{addressKey.Replace(' ', '_')}";
                // (Name override REMOVED 2026-06-12: it is the one injection-path
                //  delta between the WORKING run-3 build and every failing build
                //  since — and the user ruled the name irrelevant anyway.  Bisect:
                //  this build restores run-3 injection semantics exactly.)
                inst.hourlyWage = 0f;
                inst.satisfaction = 100f;
                inst.assignedAddress = new Address(reg.StreetName, reg.StreetNumber);

                var stations = new Il2CppSystem.Collections.Generic.List<BigAmbitions.Items.ItemName>();
                stations.Add(BigAmbitions.Items.ItemName.CheckoutCounterRight);
                stations.Add(BigAmbitions.Items.ItemName.CheckoutCounterLeft);
                stations.Add(BigAmbitions.Items.ItemName.CashRegister);
                inst.assignedWorkStationItems = stations;

                var days = new Il2CppSystem.Collections.Generic.List<BigAmbitions.DayNightCycle.DayOfWeekOrdered>();
                for (int d = 1; d <= 7; d++) days.Add((BigAmbitions.DayNightCycle.DayOfWeekOrdered)d);
                inst.assignedWeeklyDays = days;
                inst.assignedWeeklyHours = 168;   // semantics unverified — observe + log

                gi.EmployeeInstances.Add(inst);
                try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[inst.id] = inst; } catch { }
                _synthetics[addressKey] = (playerId, inst, dutyPos);
                Plugin.Logger.LogInfo(
                    $"[SynthStaff] injected '{inst.id}' for '{playerId}' at '{addressKey}' " +
                    $"(roster {before}→{gi.EmployeeInstances.Count}; days=7 hours=168 wage=0).");

                // THE missing link (fresh-dump recon 2026-06-12): staffing is
                // driven by WorkShift entries inside the registration's
                // scheduleDays — the station asks GetEmployeeAtStationAndHour
                // (reg, itemInstanceId, hour); the employee-side summary fields
                // are never consulted.  One blanket shift per day, keyed to the
                // worker-reported register instance id.
                int shifts = 0;
                if (string.IsNullOrEmpty(stationId))
                    Plugin.Logger.LogWarning("[SynthStaff] no stationId in duty payload — cannot create work shifts.");
                else if (reg.scheduleDays == null)
                    Plugin.Logger.LogWarning("[SynthStaff] registration has no scheduleDays — cannot create work shifts.");
                else
                {
                    for (int i = 0; i < reg.scheduleDays.Count; i++)
                    {
                        var day = reg.scheduleDays[i];
                        if (day == null) continue;
                        var ws = new WorkShift
                        {
                            startingHour   = 0,
                            endingHour     = 24,
                            employeeId     = inst.id,
                            itemInstanceId = stationId,
                            type           = WorkShiftType.Default,
                        };
                        day.AddWorkShift(ws);
                        shifts++;
                    }
                    Plugin.Logger.LogInfo($"[SynthStaff] {shifts} work shift(s) added for station '{stationId}'.");
                }

                // (RunHourly kick REMOVED 2026-06-12: didn't help in runs 5/7 and
                //  an off-cycle scheduler tick has unknown side effects — part of
                //  the revert-to-run-3 bisect.  Time log + price dump are pure
                //  reads and stay.)
                try { var (d, h) = GameStateReader.GetGameTime();
                      Plugin.Logger.LogInfo($"[SynthStaff] game time day={d} hr={h:F1}."); }
                catch { }
                LogShopPrices(reg, "[SynthStaff/prices]");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] staff '{addressKey}': {ex}"); }
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

        private static void RemoveSynthetic(string addressKey)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || !_synthetics.TryGetValue(addressKey, out var s)) return;
                _synthetics.Remove(addressKey);
                try { Helpers.EmployeeHelper.UnassignEmployeeFromAllWorkshifts(s.inst); }
                catch (Exception ux) { Plugin.Logger.LogWarning($"[SynthStaff] unassign: {ux.Message}"); }
                var gi = SaveGameManager.Current;
                // Strip our work shifts from the registration's schedule (the
                // game-helper unassign above may already do it — belt+braces;
                // duplicates are harmless to remove).
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
                Plugin.Logger.LogInfo($"[SynthStaff] removed synthetic at '{addressKey}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] remove '{addressKey}': {ex.Message}"); }
        }

        private static void RemoveSyntheticsOf(string playerId)
        {
            var dead = new List<string>();
            foreach (var kv in _synthetics) if (kv.Value.playerId == playerId) dead.Add(kv.Key);
            foreach (var k in dead) RemoveSynthetic(k);
        }

    }
}
