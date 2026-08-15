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
        public const string SyntheticDutyEmployeeIdPrefix = "BAMP_DUTY_";

        // posKey → cashier.  CONCURRENT: Apply runs on the network poll thread
        // (RegisterCashier handler) and the main thread (local duty echo), while
        // the checkout-routing Harmony patches read it on the main thread.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string playerId, Vector3 pos, bool employee, string address, string stationId)> _cashiers = new();

        /// <summary>[CrossOwner] probe: employee ids already reported this session.</summary>
        private static readonly HashSet<string> _crossOwnerLogged = new();

        public static void Reset()
        {
            // Remove the synthetic duty NPCs we injected from the live game first,
            // so a scene-exit/disconnect autosave can't write them into a save (they
            // are runtime-only and would otherwise orphan — they live in gi.Employee-
            // Instances but not in _synthetics after a load).  RemoveSynthetic clears
            // each from gi + the dictionary + its work shifts; iterate a key copy
            // because it mutates _synthetics.
            foreach (var key in new List<string>(_synthetics.Keys)) RemoveSynthetic(key);
            // WS3: injected roster records die with the scene too (runtime-only, like the synthetics).
            foreach (var id in new List<string>(_injectedStaff.Keys)) RemoveInjectedStaff(id);
            lock (_rosterByAddr) { _rosterByAddr.Clear(); }
            _rosterApplied.Clear(); _rosterSigSent.Clear(); _rosterLogSig.Clear();
            _cashiers.Clear(); _empDuty.Clear(); _synthetics.Clear(); _crossOwnerLogged.Clear(); _onDuty = false; CurrentShopOwner = ""; CurrentShopAddress = "";
        }

        // ── Current building context (set by the building entry patch) ────────
        // After the rival-translation, businessOwnerRivalId holds the OWNING
        // PLAYER's id for player businesses (a real rival GUID for AI ones —
        // the host validates against the lobby roster, so AI ids fall out).
        public static string CurrentShopOwner   { get; private set; } = "";
        public static string CurrentShopAddress { get; private set; } = "";

        public static void SetCurrentShop(string ownerId, string address)
        {
            // DIAG [ShopCtx] (2026-07-07, flatbed cluster): the interior mask hid a hand-vehicle
            // ghost with mine='' while the player stood INSIDE the tagged building — trace every
            // context transition so the next run shows whether entry set it and what cleared it.
            if (CurrentShopAddress != (address ?? ""))
                Plugin.Logger.LogInfo($"[ShopCtx] '{CurrentShopAddress}' → '{address ?? ""}' (owner '{ownerId ?? ""}')");
            CurrentShopOwner = ownerId ?? "";
            CurrentShopAddress = address ?? "";
        }

        // ── Context self-heal (2026-07-07, [ShopCtx]-proven): entering a building while PUSHING a
        // hand vehicle skips the DelayedEnterBuildingActions entry hook entirely — the context stayed
        // '' and the interior mask hid the pushed flatbed from its own pusher. Poll the game's OWN
        // authority (BuildingManager.buildingRegistration: set while indoors, nulled by ResetIndoors)
        // so the context converges regardless of WHICH native entry/exit path ran. Frontload principle:
        // never gate correctness on a specific flow having fired.
        private static float _ctxNextPoll, _ctxEmptySince = -1f;

        public static void TickContextHeal()
        {
            if (UnityEngine.Time.unscaledTime < _ctxNextPoll) return;
            _ctxNextPoll = UnityEngine.Time.unscaledTime + 0.5f;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            try
            {
                var bm  = InstanceBehavior<BuildingManager>.Instance;
                var reg = bm != null ? bm.buildingRegistration : null;
                if (reg != null)
                {
                    _ctxEmptySince = -1f;
                    string key = GameStateReader.AddressKey(reg);
                    if (!string.IsNullOrEmpty(key) && key != CurrentShopAddress)
                        SetCurrentShop(reg.businessOwnerRivalId?.ToString() ?? "", key);   // hook missed → heal
                }
                else if (!string.IsNullOrEmpty(CurrentShopAddress))
                {
                    // Exit hook missed too (hand-vehicle exit): clear after 2s of provably-outdoors —
                    // hysteresis absorbs the null reads during entry/exit transitions.
                    if (_ctxEmptySince < 0f) _ctxEmptySince = UnityEngine.Time.unscaledTime;
                    else if (UnityEngine.Time.unscaledTime - _ctxEmptySince > 2f)
                        SetCurrentShop("", "");
                }
                else _ctxEmptySince = -1f;
            }
            catch { }
        }

        private static string Key(Vector3 p)
            => $"{Mathf.RoundToInt(p.x)}:{Mathf.RoundToInt(p.y)}:{Mathf.RoundToInt(p.z)}";

#if BAMP_DEV
        /// <summary>Round-263 test hooks (TestDrive 'schedfilter'): inject/remove a duty
        /// synthetic at a fabricated station so the schedule-surface filter can be
        /// exercised headlessly. Same code path as real duty (TryStaffSynthetic /
        /// RemoveSynthetic) — no separate write path.</summary>
        internal static string TestInjectSynthetic(string addressKey)
        {
            var pos = new Vector3(-9999f, -9999f, -9999f);
            TryStaffSynthetic(addressKey, "TESTDUTY", "BAMP_TESTSTATION", pos);
            return Key(pos);
        }
        internal static void TestRemoveSynthetic(string stationKey) => RemoveSynthetic(stationKey);
#endif

        // ── Round-120: personal-duty stand-ins whose BODY must stay hidden ──────────────────────────────
        // stationKey → addressKey.  The game spawns a serving NPC from the roster record a frame or more
        // after we inject it, so concealment cannot happen at injection time — it is retried on a slow tick
        // until the body exists.  Hiding uses the game's own ThirdPersonCharacter.ToggleVisibility, NOT a
        // root SetActive: round-87 proved that deactivating a character at the root strips its collider and
        // traffic then drives through where it stood.
        private static readonly Dictionary<string, string> _hideBodyAt = new();
        private static readonly HashSet<string> _hidden = new();
        private static readonly HashSet<string> _hideMissLogged = new();   // round-125: one "not present yet" line per station
        private static readonly Dictionary<string, ThirdPersonCharacter> _hiddenTpc = new();   // round-126: per-frame re-assert target
        private static readonly List<Renderer> _rendBuf = new();                              // round-126: allocation-free renderer walk
        private static float _nextHideSweep;

        /// <summary>Round-120: is this player personally working a till (not an employee post)?  Used to drop
        /// player-to-player physical collision while someone is behind a counter.</summary>
        internal static bool IsOnPersonalDuty(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return false;
            foreach (var kv in _cashiers)
                if (!kv.Value.employee && kv.Value.playerId == playerId) return true;
            return false;
        }

        /// <summary>MAIN THREAD, slow tick: conceal the body of any personal-duty stand-in once the game has
        /// spawned it.  The record stays — it is what makes customers queue at that till — but the visible
        /// person there is the actual player's synced avatar, so a second NPC beside them would read as two
        /// staff at one register.</summary>
        public static void TickHideSynthetics()
        {
            try
            {
                if (_hideBodyAt.Count == 0) return;

                // ROUND-126: re-assert the concealment EVERY FRAME for bodies we have already found.
                // A serve gives the worker a bag (ThirdPersonCharacter.SetHandContent parents the prop UNDER
                // the character, ThirdPersonCharacter.cs:575), and each grab creates a NEW object — so with a
                // 1s sweep every bag was visible for up to a second, repeatedly, which reads as "the bag isn't
                // hidden".  Same for any other held prop in any other shop type.  Allocation-free via the
                // List overload of GetComponentsInChildren, so per-frame is cheap for one or two stand-ins.
                foreach (var key in _hidden)
                {
                    if (!_hiddenTpc.TryGetValue(key, out var ht) || ht == null) continue;
                    try
                    {
                        _rendBuf.Clear();
                        ht.GetComponentsInChildren<Renderer>(true, _rendBuf);
                        for (int i = 0; i < _rendBuf.Count; i++)
                            if (_rendBuf[i] != null && _rendBuf[i].enabled) _rendBuf[i].enabled = false;
                    }
                    catch { }
                }

                if (Time.unscaledTime < _nextHideSweep) return;
                _nextHideSweep = Time.unscaledTime + 1f;

                // ROUND-125: search ALL EmployeeStationControllers, not only CashRegisterControllers.
                // FindNearestRegister is CashRegisterController-only, so a till of any other station type was
                // invisible to this sweep and its stand-in body was never hidden — field symptom: the hide fired
                // for one till and not the other, leaving a visible worker standing on that player's avatar.
                // This is the same mistake round-30 already fixed elsewhere ("any EmployeeStationController
                // counts — the CashRegisterController-only search was the visitor-side twin of the owner-side
                // checkout whitelist"), so the all-station scan is the established shape here.
                // Round-186 (field 'laggy as hell when driving', bamp-bug-20260729-133551): this 1Hz
                // sweep ran a WHOLE-SCENE FindObjectsOfType (~85-95ms with the city streamed) for as
                // long as any other player sat on personal duty — and with that station's interior
                // not loaded here, it retried forever finding nothing.  The round-166 registry is
                // the sweep-free answer: stations announce themselves; this just reads the list.
                var stationObjs = GetStationsCached();
                foreach (var kv in _hideBodyAt)
                {
                    var reg = NearestStationIn(stationObjs, ParseKey(kv.Key), 2.5f);
                    var emp = reg?.employee;
                    var tpc = emp?.employeeTpc;
                    if (tpc == null)
                    {
                        // Say so once per station: a silent skip here is indistinguishable from "the feature
                        // does not work", which is exactly how the last two rounds were spent.
                        if (_hideMissLogged.Add(kv.Key))
                            Plugin.Logger.LogInfo($"[SynthStaff] stand-in body at {kv.Key} not present yet "
                                + $"(station={(reg == null ? "NOT FOUND" : "found")}, employee={(emp == null ? "none" : "no tpc")}) — retrying each sweep.");
                        continue;
                    }
                    if (emp!.employeeInstance == null
                        || emp.employeeInstance.id == null
                        || !emp.employeeInstance.id.StartsWith(SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal))
                        continue;                                   // a REAL employee is on this till — never hide them
                    // ROUND-124 — RENDERERS ONLY.  My first cut called ThirdPersonCharacter.ToggleVisibility(false),
                    // which I had NOT read: it does navmeshAgent.enabled = false, capsuleCollider.enabled = false,
                    // gameObject.SetActive(false), isKinematic = true and localScale = zero.  A DEACTIVATED object
                    // runs no coroutines, so the stand-in could not serve at all — field symptom: "customer went to
                    // the till and just stood there doing nothing".  I had even written a comment warning against a
                    // root SetActive and then called a method that performs one internally.
                    //
                    // Disabling renderers hides the body while the agent, collider, Update and the serve coroutine
                    // all keep running.  Re-asserted every sweep rather than once, because the character re-enables
                    // its own renderers on appearance changes.
                    int off = 0;
                    try
                    {
                        foreach (var r in tpc.GetComponentsInChildren<Renderer>(true))
                            if (r != null && r.enabled) { r.enabled = false; off++; }
                    }
                    catch { continue; }
                    _hiddenTpc[kv.Key] = tpc;
                    if (off > 0 && _hidden.Add(kv.Key))
                        Plugin.Logger.LogInfo($"[SynthStaff] hid the stand-in body at {kv.Key} in '{kv.Value}' ({off} renderer(s)) — "
                            + "the player working that till is already visible there as their own avatar; the stand-in stays ACTIVE so it can serve.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] hide sweep: {ex.Message}"); }
        }

        /// <summary>Round-143 - the ORIGIN-STAGING poison, measured end to end by [QueueInit]: the game
        /// runs a register's queue-init while the item is IN HAND / being staged (transform AND data position
        /// both (0,0,0), no queue data yet), which writes a line anchored at the world origin into the item's
        /// customPositions.  Vanilla heals that locally by re-initializing after placement, but the staged
        /// intermediate reached the other machine over our wire and froze there (customPositions predated the
        /// diff hash), and round-137's owner-authoritative sync then kept restoring it.  The signature is
        /// unambiguous: anchor 0 is the front-of-counter position and always sits within arm's reach of the
        /// register - the poisoned one is ~950m away.  Data failing this check is dropped so the native
        /// loader rebuilds the line at the item's true position (the same healing vanilla does).</summary>
        internal static bool IsPoisonedQueue(System.Collections.Generic.List<SerializableVector3> list, Vector3 itemPos)
        {
            if (list == null || list.Count == 0) return false;
            Vector3 a0 = list[0];
            return Vector3.Distance(a0, itemPos) > 10f;
        }

        /// <summary>Round-137 - repair the TRUNCATED-QUEUE signature.  ItemInstance.customPositions encodes a
        /// drawn queue as [anchors..., Vector3.zero separator, spots...] (WaitingLineData.SplitAnchorsAndSpots).
        /// A list that decodes to two-or-more anchors with EXACTLY ONE stored spot is the corruption this
        /// session field-proved (same register: 8 entries on the placing machine, 4 on the receiver): the lone
        /// stored spot makes the native rebuild guard (spots.Count == 0) skip SetUpWaitingLine, so the till is
        /// capped at ONE queue place every load, forever.  Dropping that lone spot leaves [anchors + separator],
        /// which decodes to zero spots - and the native guard then rebuilds the line by pathing between the
        /// anchors.  A legitimately tiny drawn queue (anchors under 0.8m apart) rebuilds to the same single
        /// spot, so the repair cannot shrink anything real.</summary>
        internal static int RepairTruncatedQueue(System.Collections.Generic.List<SerializableVector3> list)
        {
            if (list == null || list.Count < 4) return 0;
            int sep = -1;
            for (int i = 0; i < list.Count; i++) { Vector3 v = list[i]; if (v == Vector3.zero) { sep = i; break; } }
            if (sep < 2) return 0;                        // fewer than two anchors -> not the signature
            if (list.Count - sep - 1 != 1) return 0;      // only the exact one-stored-spot truncation
            list.RemoveAt(list.Count - 1);
            return 1;
        }

        /// <summary>Round-137 - after the in-place item apply copies a changed drawn queue, rebuild the LIVE
        /// line (the interior is only loaded when the local player is inside; elsewhere the next interior load
        /// rebuilds from the repaired data anyway).  Uses the game's own SetCustomAnchors -> SetUpWaitingLine.</summary>
        internal static void RebuildDrawnQueueAt(Vector3 pos, System.Collections.Generic.List<SerializableVector3> customPositions)
        {
            try
            {
                var stations = GetStationsCached();   // round-186: registry, never a scene walk
                var st = NearestStationIn(stations, pos, 2.5f);
                var wl = (st as EmployeeStations.IWaitingLineHolder)?.GetWaitingLine();
                if (wl == null) return;
                var anchors = new System.Collections.Generic.List<Vector3>();
                if (customPositions != null)
                    foreach (var sp in customPositions) { Vector3 v = sp; if (v == Vector3.zero) break; anchors.Add(v); }
                if (anchors.Count < 2) return;
                // ROUND-140: showChanges:true also runs visuals.Show(), which displays the interior designer's
                // queue markers (the anchor flag and spot dots) in normal play - the client saw the flag standing
                // in the shop (field report 2026-07-28).  Those visuals are only ever meant for the queue tool.
                // Set the data silently, rebuild the spots ourselves, and force the markers hidden.
                wl.SetCustomAnchors(anchors, showChanges: false);
                try { wl.creator.SetUpWaitingLine(); } catch { }
                try { wl.visuals.Hide(); } catch { }
                Plugin.Logger.LogInfo($"[QueueSync] drawn queue rebuilt at {Key(pos)} from {anchors.Count} anchor(s) - capacity follows the updated data.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[QueueSync] rebuild: {ex.Message}"); }
        }

        /// <summary>Task-28 fix 3 — the CLEAR counterpart of RebuildDrawnQueueAt: the owner's copy
        /// carries no line data, so reset the live line to its template at the item's real position
        /// (the game's own creator.Reset), then re-null the instance copy — Reset's onDataChange
        /// callback writes the rebuilt template line back into it, and the data must keep matching
        /// the owner's (null) so repeated empty pushes stay no-ops.</summary>
        internal static void ResetQueueAt(Vector3 pos, BigAmbitions.Items.ItemInstance owner)
        {
            try
            {
                var stations = GetStationsCached();   // round-186: registry, never a scene walk
                var st = NearestStationIn(stations, pos, 2.5f);
                var wl = (st as EmployeeStations.IWaitingLineHolder)?.GetWaitingLine();
                if (wl == null) return;   // interior not loaded here — the nulled data rebuilds the line natively on next load
                try { wl.creator?.Reset(); } catch { }
                try { wl.visuals.Hide(); } catch { }
                try { if (owner != null) owner.customPositions = null; } catch { }
                Plugin.Logger.LogInfo($"[QueueSync] drawn queue cleared at {Key(pos)} — line reset to its template at the item's position.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[QueueSync] clear: {ex.Message}"); }
        }

        /// <summary>Inverse of Key() — the sweep needs a position to find the till again.</summary>
        private static Vector3 ParseKey(string k)
        {
            try
            {
                var parts = k.Split(':');
                if (parts.Length != 3) return Vector3.zero;
                return new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
            }
            catch { return Vector3.zero; }
        }

        /// <summary>Round-119: the rounded-position key of the till THIS player is personally working
        /// ("" = not on personal duty).  Position keys are the identifier that has been verified to match
        /// across machines, which is why the serve mirror addresses tills this way rather than by station id.</summary>
        internal static string LocalPersonalDutyKey()
        {
            foreach (var kv in _cashiers)
                if (!kv.Value.employee && kv.Value.playerId == MPConfig.PlayerId) return kv.Key;
            return "";
        }

        /// <summary>Round-119: the position key for a station, so the simulator can name the till it just
        /// served at in the same terms every machine already uses for duty.</summary>
        internal static string StationKeyOf(Vector3 pos) => Key(pos);

        /// <summary>Round-41 (customer puppets): the player on PERSONAL register duty at this address
        /// ("" = none). The simulator election gives the register worker authority — serving requires
        /// simulating (the serve loop needs real customer AI on the serving machine).</summary>
        internal static string PersonalDutyHolderAt(string addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return "";
            foreach (var kv in _cashiers)
                if (!kv.Value.employee && kv.Value.address == addressKey) return kv.Value.playerId;
            return "";
        }

        /// <summary>Apply an on/off-duty state (local echo + remote).</summary>
        public static void Apply(RegisterCashierPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.PlayerId)) return;
            var pos = new Vector3(p.X, p.Y, p.Z);
            string k = Key(pos);
            if (p.On)
            {
                _cashiers[k] = (p.PlayerId, pos, p.Employee, p.Address ?? "", p.StationId ?? "");
#if BAMP_DEV
                Plugin.Logger.LogInfo($"[Register] '{p.PlayerId}' ON duty at {k}{(p.Employee ? " (employee)" : "")}.");   // round-187b: per-toggle detail is dev-only (field logs carried 15k of these; StaffDiag summarizes)
#endif
                // Duty on ANOTHER machine → stand the till up locally.  Main-thread: Apply can run on the
                // network poll thread.
                //
                // ROUND-120: this used to require p.Employee, i.e. only a shop run by the owner's HIRED staff
                // got a stand-in.  A PERSONAL duty (a player working the till themselves) sends
                // Employee=false, so their till was staffed on THEIR machine only — and since exactly one
                // machine simulates the customers, the simulator saw a single staffed till and queued
                // everyone there.  Field-confirmed by the user's two-register test: both duties known on both
                // machines, "synthetics=0" on the host, every customer at one register.
                //
                // A personal-duty stand-in is the MECHANISM (it is what makes customers queue and be served);
                // its BODY is redundant, because the player it stands in for is already visible there as a
                // synced avatar.  So personal-duty stand-ins are marked for concealment — see TickHideSynthetics.
                if (p.PlayerId != MPConfig.PlayerId && !string.IsNullOrEmpty(p.Address))
                {
                    string addr = p.Address; string pid = p.PlayerId; string st = p.StationId;
                    var dutyPos = pos;
                    bool personal = !p.Employee;
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        TryStaffSynthetic(addr, pid, st, dutyPos);
                        if (personal) _hideBodyAt[Key(dutyPos)] = addr;   // the player's own avatar is the visual
                    });
                }
            }
            else if (_cashiers.TryGetValue(k, out var e) && e.playerId == p.PlayerId)
            {
                _cashiers.TryRemove(k, out _);
#if BAMP_DEV
                Plugin.Logger.LogInfo($"[Register] '{p.PlayerId}' OFF duty at {k}.");   // round-187b: dev-only
#endif
                // Round-120: mirror the ON gate — a personal-duty stand-in must be torn down too, or the till
                // stays staffed by a phantom after the player walks away.
                if (p.PlayerId != MPConfig.PlayerId)
                    GameStatePatcher.EnqueueOnMainThread(() => { _hideBodyAt.Remove(k); RemoveSyntheticAtStation(k); });
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
            var arr = GetStationsCached();   // round-186: registry (registers are stations); the cast below filters
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
            // Round-187 (user directive, field 20260729-141854: a pure-SP session logged 13,808
            // [Register] lines): the mod is MULTIPLAYER-ONLY — duty tracking, the employee-station
            // scan and their logging have no business running in plain single-player.
            if (!MPServer.IsRunning && !MPClient.InMpGame) return;
            TickEmployeeDutySliced();   // round-161: every frame under a ~2ms budget (was a 60-65ms single-frame scan)
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
                    // ROUND-122: carry the till's ITEM-INSTANCE id.  Without it the receiver's stand-in is
                    // created with "0 shift(s), station ''" — and the work shift is the ONLY thing that tells
                    // the game which station a worker mans, so the till was never actually staffed on the
                    // other machine and its customer AI had nothing to queue for.  That is why two players on
                    // two tills still saw every customer at one register even after the gate, per-till and id
                    // fixes: those got a stand-in created, this is what puts it BEHIND THE COUNTER.
                    string myStationId = "";
                    try { myStationId = reg.ItemInstance?.id ?? ""; } catch { }
                    if (string.IsNullOrEmpty(myStationId))
                        Plugin.Logger.LogWarning("[Register] personal duty: could not resolve the till's item-instance id — "
                            + "the stand-in on other machines will have no shift and that till will not serve.");
                    SendToggle(_dutyPos, true, stationId: myStationId);
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

                // (employee-staffed stations now scanned by TickEmployeeDutySliced above — round-161)
                // Round-164: the 60-70ms worst-frame survived the round-161 slice, so the real consumer
                // is one of THESE five — sub-bracketed so the perf line names it instead of us guessing.
                long _s1 = MPPerf.Begin(); TickStaffEvaluator();                           MPPerf.End("StaffEval",  _s1);
                long _s2 = MPPerf.Begin(); TickRosterPublish();                            MPPerf.End("RosterPub",  _s2);
                long _s3 = MPPerf.Begin(); TickRosterApply();                              MPPerf.End("RosterApp",  _s3);
                long _s4 = MPPerf.Begin(); MPPatches.Patch_MPOrderFinalizer.TickPending(); MPPerf.End("OrderFin",   _s4);
                long _s5 = MPPerf.Begin(); LogStaffDiagOwner();                            MPPerf.End("StaffDiag",  _s5);
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

        // Round-161 — the 5s scan walked EVERY registration + every owned shop's schedules in ONE
        // frame: 60-65ms worst-frame on a 28-business save, the recurring hitch both the field pair's
        // machines and the rig felt.  Same work, same cadence, sliced: a sweep still starts every 5s,
        // but processes registrations under a ~2ms/frame budget and commits the ON/OFF transitions
        // when the sweep completes a few frames later — well inside the cadence, and transitions are
        // computed against a coherent single-sweep snapshot exactly as before.
        private static List<BuildingRegistration>? _empSweepRegs;
        private static int _empSweepIx;
        private static Dictionary<string, (Vector3 pos, string addr, string stationId)>? _empSweepLive;
        private static readonly System.Diagnostics.Stopwatch _empSweepSw = new();

        private static void TickEmployeeDutySliced()
        {
            try
            {
                if (_empSweepRegs == null)
                {
                    if (Time.unscaledTime < _nextEmpScanAt) return;
                    _nextEmpScanAt = Time.unscaledTime + 5f;
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null) return;
                    _empSweepRegs = new List<BuildingRegistration>(gi.BuildingRegistrations);
                    _empSweepIx   = 0;
                    _empSweepLive = new Dictionary<string, (Vector3 pos, string addr, string stationId)>();
                }
                _empSweepSw.Restart();
                var live = _empSweepLive!;
                while (_empSweepIx < _empSweepRegs.Count)
                {
                    if (_empSweepSw.Elapsed.TotalMilliseconds > 2.0) return;   // budget spent — resume next frame
                    var reg = _empSweepRegs[_empSweepIx++];
                    if (reg == null) continue;
                    bool mine; try { mine = MergerFlip.TrulyMine(reg); } catch { continue; }   // TrulyMine: no station/duty claims on flipped shops
                    if (!mine || reg.itemInstances == null) continue;
                    string addr = GameStateReader.AddressKey(reg);
                    // Round-30 (WS2): stations are derived STRUCTURALLY from the schedule — any item
                    // instance a work shift references IS a workstation. The old 3-name checkout whitelist
                    // (checkoutcounterright/left, cashregister) silently missed every other station type —
                    // shops staffed at non-checkout stations broadcast nothing and visitors saw no one
                    // (StaffDiag's UNRECOGNIZED-till-like counter existed for exactly this suspicion).
                    var stationIds = new HashSet<string>();
                    try
                    {
                        if (reg.scheduleDays != null)
                            foreach (var sd in reg.scheduleDays)
                                if (sd?.workShifts != null)
                                    foreach (var w in sd.workShifts)
                                        if (w != null && !string.IsNullOrEmpty(w.itemInstanceId)
                                            && !string.IsNullOrEmpty(w.employeeId)
                                            // ROUND-123: never count OUR OWN stand-in as a hired employee.
                                            // TryStaffSynthetic gives the stand-in a blanket 0-24h shift on the
                                            // till so the game staffs it — and this scan reads the schedule
                                            // structurally, so that shift looked exactly like a real hire.  The
                                            // result was a FEEDBACK LOOP: a stand-in for player B's till made
                                            // this machine broadcast the same till as EMPLOYEE-staffed under
                                            // its own name, the other machine then injected a stand-in for THAT
                                            // duty, and employee-duty stand-ins are not concealed — so a visible
                                            // worker appeared standing on top of player B.  Field-confirmed:
                                            // "[Register] 'melaus' ON duty at 952:1:-2 (employee)" four lines
                                            // after a stand-in was injected for Client1 at 952.
                                            && !IsSyntheticDuty(w.employeeId))
                                            stationIds.Add(w.itemInstanceId);
                    }
                    catch { }
                    foreach (var stationId in stationIds)
                    {
                        BigAmbitions.Items.ItemInstance? ii = null;
                        try { reg.itemInstances.TryGetValue(stationId, out ii); } catch { }
                        if (ii == null) continue;
                        bool staffed = false;
                        // CURRENT HOUR, not -1 (RED ROC reports, 2026-07-09): the native query does a plain
                        // InRange(hour, shift) — hour -1 NEVER matches a shift, so this check was false for
                        // every scheduled employee since the feature shipped (2026-06-12). Only the
                        // owner-personally-working early-return could pass — employee-run shops never
                        // broadcast duty, visitors never saw synthetic staff (their logs: synthetics=0,
                        // dutyToggles=0, every register "unstaffed" at every hour).
                        try { staffed = Helpers.EmployeeHelper.IsEmployeeStationEmployedAtHour(reg, stationId, SaveGameManager.Current.Hour); }
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
#if BAMP_DEV
                        Plugin.Logger.LogInfo($"[Register] employee-staffed station ON at {kv.Key} ({kv.Value.addr}).");   // round-187b: dev-only
#endif
                    }
                var stale = new List<string>();
                foreach (var kv in _empDuty)
                    if (!live.ContainsKey(kv.Key)) stale.Add(kv.Key);
                foreach (var k in stale)
                {
                    // Don't stomp PERSONAL duty at the same register.
                    if (!(_onDuty && Key(_dutyPos) == k)) SendToggle(_empDuty[k], false);
                    _empDuty.Remove(k);
#if BAMP_DEV
                    Plugin.Logger.LogInfo($"[Register] employee-staffed station OFF at {k}.");   // round-187b: dev-only
#endif
                }
                _empSweepRegs = null; _empSweepLive = null;   // sweep complete
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Register] employee duty: {ex.Message}");
                _empSweepRegs = null; _empSweepLive = null;   // abandon this sweep; the next starts fresh
            }
        }

        // ── [StaffDiag] comprehensive staffing diagnostic (2026-06-25) ──────────────────────────────────
        // Bug: a visitor entering another player's shop sees NO workers. The chain (TickEmployeeDuty detects a
        // staffed till → broadcasts duty → visitor's Apply injects a BAMP_DUTY_ synthetic) logged ZERO activity
        // in a report, so the OWNER's detection never fired — but the existing logs only fire on a successful
        // ON-transition, so a "detected nothing" pass is invisible. This dumps the FULL staffing picture on
        // BOTH sides so ONE report (with the partner's log, which the report tool already requests) pins the
        // break whichever link failed. Release-safe + throttled. REMOVE once the no-workers root is found.
        private static float _nextStaffDiagAt = -999f;

        // One-line full snapshot of a shop's staffing — used by both the owner scan and the visitor entry dump.
        // Covers every candidate root: ownership, recognized-vs-unrecognized checkout items, the game's
        // IsEmployeeStationEmployedAtHour verdict per till, and the work-shift roster (synthetic vs real-present
        // vs real-MISSING vs real-ORPHANED employees) + the current hour.
        private static void DumpShopStaffing(BuildingRegistration reg, string addr, string tag, bool forced = false)
        {
            try
            {
                if (reg == null) return;
                bool mine = false; try { mine = MergerFlip.TrulyMine(reg); } catch { }   // TrulyMine (merger flip excluded)
                string bldgOwner = ""; string bizOwner = "";
                try { bldgOwner = reg.buildingOwnerRivalId?.ToString() ?? ""; } catch { }
                try { bizOwner  = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                int hour = -1; try { hour = (int)GameStateReader.GetGameTime().hourOfDay; } catch { }

                int nTills = 0, unrecognized = 0;
                bool tillGap = false;   // a till reads unstaffed while a shift SHOULD cover this hour — the real anomaly
                var tills = new System.Text.StringBuilder();
                try
                {
                    if (reg.itemInstances != null)
                        foreach (var kv in reg.itemInstances)
                        {
                            var ii = kv.Value; if (ii == null) continue;
                            string n = ii.itemName ?? "";
                            if (n == "ba:itemname_checkoutcounterright" || n == "ba:itemname_checkoutcounterleft" || n == "ba:itemname_cashregister")
                            {
                                nTills++;
                                string sid = ii.id?.ToString() ?? "";
                                // CURRENT HOUR, not -1 — see TickEmployeeDuty: -1 never matches a shift, so
                                // this diag reported every register "unstaffed" regardless of the schedule.
                                bool staffed = false; try { staffed = Helpers.EmployeeHelper.IsEmployeeStationEmployedAtHour(reg, sid, SaveGameManager.Current.Hour); } catch { }
                                if (!staffed && StationHasShiftNow(reg, sid, hour)) tillGap = true;
                                tills.Append($"{n.Replace("ba:itemname_", "")}={(staffed ? "STAFFED" : "unstaffed")} ");
                            }
                            else if (n.IndexOf("register", StringComparison.OrdinalIgnoreCase) >= 0
                                  || n.IndexOf("checkout", StringComparison.OrdinalIgnoreCase) >= 0
                                  || n.IndexOf("counter",  StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                unrecognized++;   // a till-like item NOT in the recognized set (root b)
                                // 2026-07-09 (RED ROC review): the counter alone was unresolvable from a
                                // report — the NAME says whether it's a real serving station the whitelist
                                // misses (ticket booth?) or just furniture matching "counter". Once per type.
                                if (_unrecognizedTillNames.Add(n))
                                {
                                    // Round-53b (KuyuSuyu review, 'counter2'): the NAME can't answer whether
                                    // this is a staffable station the whitelist misses or plain furniture —
                                    // the item's native TYPE FLAGS can (PointOfSale/EmployeeWorkstation =
                                    // needs the whitelist; anything else = name-heuristic false positive).
                                    string flags = "";
                                    try { var it = BigAmbitions.Items.ItemsGetter.GetByName(n); flags = it == null ? "unknown-item" : it.type.ToString(); } catch { flags = "unreadable"; }
                                    Plugin.Logger.LogWarning($"[StaffDiag] unrecognized till-like item type: '{n}' (first seen at '{addr}', native type flags: [{flags}])");
                                }
                            }
                        }
                }
                catch { }

                int shifts = 0, synth = 0, present = 0, missing = 0, orphan = 0;
                try
                {
                    if (reg.scheduleDays != null)
                        foreach (var sd in reg.scheduleDays)
                        {
                            if (sd?.workShifts == null) continue;
                            foreach (var w in sd.workShifts)
                            {
                                if (w == null) continue;
                                shifts++;
                                string eid = w.employeeId ?? "";
                                if (eid.StartsWith(SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) { synth++; continue; }
                                if (!Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(eid, out var e) || e == null) { missing++; continue; }
                                bool resolvable = false;
                                try { resolvable = Helpers.BuildingHelper.GetBuildingRegistration(e.assignedAddress) != null; } catch { }
                                if (resolvable) present++; else orphan++;
                            }
                        }
                }
                catch { }

                // Any SCHEDULED station (office desk, factory bench, DJ booth…) with a shift due now but
                // no employee working = the same gap class as a till (RED ROC "office staff not giving
                // outputs", 2026-07-09 — offices have no till, so the old diag never looked at them).
                int schedStations = 0, schedStaffedNow = 0; bool stationGap = false;
                try
                {
                    var stationIds = new HashSet<string>();
                    if (reg.scheduleDays != null)
                        foreach (var sd in reg.scheduleDays)
                            if (sd?.workShifts != null)
                                foreach (var w in sd.workShifts)
                                    if (w != null && !string.IsNullOrEmpty(w.itemInstanceId) && !string.IsNullOrEmpty(w.employeeId))
                                        stationIds.Add(w.itemInstanceId);
                    schedStations = stationIds.Count;
                    foreach (var sid in stationIds)
                    {
                        bool st = false; try { st = Helpers.EmployeeHelper.IsEmployeeStationEmployedAtHour(reg, sid, SaveGameManager.Current.Hour); } catch { }
                        if (st) schedStaffedNow++;
                        else if (StationHasShiftNow(reg, sid, hour)) stationGap = true;
                    }
                }
                catch { }

                // Anomaly gate (2026-07-09, RED ROC report review): the hourly narrator buried real
                // signal — a report carried 100+ healthy snapshots ("unstaffed at 10pm, no shift due" is
                // EXPECTED). Speak only when the data contradicts itself: shift employees the roster
                // can't resolve (MISSING), employees whose shop can't be resolved (ORPHAN), or any
                // station unstaffed during an hour a shift SHOULD cover (tillGap/stationGap). Visitor-
                // side entry dumps (forced=true) keep logging unconditionally — one line per entry is
                // the cross-machine evidence pair and self-throttles by the entry event itself.
                bool anomaly = missing > 0 || orphan > 0 || tillGap || stationGap;
                if (!anomaly && !forced) return;
                Plugin.Logger.LogWarning(
                    $"{tag} '{addr}' mine={mine} bldgOwner='{bldgOwner}' bizOwner='{bizOwner}' h{hour} | tills={nTills} [{tills.ToString().Trim()}]"
                    + $" stations={schedStations} staffedNow={schedStaffedNow}"
                    + (tillGap ? " TILL-GAP(unstaffed with shift due)" : "")
                    + (stationGap ? " STATION-GAP(scheduled station unstaffed with shift due)" : "")
                    + (unrecognized > 0 ? $" +{unrecognized} UNRECOGNIZED-till-like" : "")
                    + $" | shifts={shifts} (synthetic={synth} realPresent={present} realMISSING={missing} realORPHAN={orphan})");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"{tag} dump '{addr}': {ex.Message}"); }
        }

        private static readonly HashSet<string> _unrecognizedTillNames = new();
        private static string _lastStaffSummary = "";
        private static float _nextStaffSummaryHeartbeatAt;

        // ── [EconProbe] midnight digest (approved diagnostics batch, 2026-07-09) ──────────────────
        // One line per OWNED business per game-day, read straight from the native ledgers: yesterday's
        // orderHistory entry (customers + revenue — what the game itself deposited) + today's customer-
        // entry stats. Answers "my X isn't earning" from the report alone, offices and retail alike.
        private static int _lastDigestDay = -1;

        public static void TickEconDigest()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                int day = gi.Day;
                if (_lastDigestDay < 0) { _lastDigestDay = day; return; }   // arm on first sight
                if (day == _lastDigestDay) return;
                _lastDigestDay = day;

                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    bool mine; try { mine = MergerFlip.TrulyMine(reg); } catch { continue; }
                    if (!mine) continue;
                    string biz = ""; try { biz = reg.businessTypeName ?? ""; } catch { }
                    if (string.IsNullOrEmpty(biz) || biz == "ba:businesstype_empty") continue;

                    int customers = -1; float revenue = -1f;
                    try
                    {
                        if (reg.orderHistory != null)
                            foreach (var h in reg.orderHistory)
                                if (h != null && h.dayNumber == day - 1) { customers = h.totalCustomers; revenue = h.totalRevenue; break; }
                    }
                    catch { }
                    var (entries, entriesDone) = CustomerEntrySync.EntryStatsFor(reg.Address);
                    Plugin.Logger.LogInfo($"[EconProbe] day {day - 1} digest '{GameStateReader.AddressKey(reg)}' biz='{reg.BusinessName}' ({biz.Replace("ba:businesstype_", "")}): " +
                                          $"customers={customers} revenue=${revenue:N0} todayEntries={entries}/{entriesDone}done" +
                                          (customers == 0 || revenue == 0f ? "  ← EARNED NOTHING yesterday" : customers < 0 ? "  ← no ledger row for yesterday" : ""));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[EconProbe] digest: {ex.Message}"); }
        }

        // ── Duty-broadcast activation summary (TEMPORARY probe, 2026-07-09) ───────────────────────
        // The hour=-1 fix ACTIVATES schedule-driven duty broadcasting for the first time in the field —
        // this watches the activation in both failure directions (all-zero while shops have shifts =
        // the fix didn't take; runaway counts = over-fire). 10-min cadence while in MP. RETIRE after
        // a couple of clean field sessions (probe lifecycle).
        private static float _nextDutySummaryAt;

        public static void TickDutySummary()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (Time.unscaledTime < _nextDutySummaryAt) return;
                _nextDutySummaryAt = Time.unscaledTime + 600f;

                int shopsWithShifts = 0;
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations != null)
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        bool mine; try { mine = MergerFlip.TrulyMine(reg); } catch { continue; }
                        if (!mine || reg.scheduleDays == null) continue;
                        foreach (var sd in reg.scheduleDays)
                            if (sd?.workShifts != null && sd.workShifts.Count > 0) { shopsWithShifts++; break; }
                    }
                // ── [CrossOwner] probe (approved 2026-07-16): the load-time sweep
                // (MPSaveIntegrity Class 6) cleans cross-owner assignments at
                // world-ready and the known assign dropdowns are filtered, so an
                // OWN employee sitting on a partner business MID-SESSION proves an
                // unguarded producer we haven't found yet.  Names the record and
                // timestamps the damage (ring log correlation); one line per
                // employee per session.  Doubles as a permanent invariant tripwire
                // — silence across field sessions = the dropdowns were the only
                // holes.
                try
                {
                    if (gi?.EmployeeInstances != null)
                        foreach (var emp in gi.EmployeeInstances)
                        {
                            if (emp == null || emp.assignedAddress == null) continue;
                            string eid = ""; try { eid = emp.id ?? ""; } catch { }
                            if (eid.Length == 0 || eid.StartsWith(SyntheticDutyEmployeeIdPrefix)) continue;
                            if (_injectedStaff.ContainsKey(eid) || _crossOwnerLogged.Contains(eid)) continue;
                            BuildingRegistration? xreg = null;
                            try { xreg = Helpers.BuildingHelper.GetBuildingRegistration(emp.assignedAddress); } catch { }
                            if (!GameStatePatcher.IsForeignPlayerBusiness(xreg)) continue;
                            _crossOwnerLogged.Add(eid);
                            string en = ""; try { en = emp.characterData?.name ?? ""; } catch { }
                            Plugin.Logger.LogWarning($"[CrossOwner] employee '{en}' ({eid}) is assigned to partner business '{xreg!.BusinessName}' MID-SESSION — an unguarded assign path still exists.");
                        }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossOwner] probe: {ex.Message}"); }

                // Foreign-todo purge rides the same 10-min cadence (field 2026-07-20:
                // the objectives panel alerted about another player's staff LIVE —
                // the load-time sweep alone can't clear mid-session creations).
                try { MPSaveIntegrity.PurgeForeignTodos("periodic"); } catch { }

                if (shopsWithShifts == 0 && _empDuty.Count == 0 && _cashiers.Count == 0 && _synthetics.Count == 0) return;   // nothing to watch
                Plugin.Logger.LogInfo($"[Register] duty summary (10m): empDutyOut={_empDuty.Count} dutyIn={_cashiers.Count} synthetics={_synthetics.Count} ownShopsWithShifts={shopsWithShifts}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Register] duty summary: {ex.Message}"); }
        }

        /// <summary>Does any work shift reference this station for the CURRENT hour today? (The
        /// "should be covered" half of the till-gap anomaly test.)</summary>
        private static bool StationHasShiftNow(BuildingRegistration reg, string stationId, int hour)
        {
            try
            {
                if (hour < 0 || string.IsNullOrEmpty(stationId)) return false;
                var today = Helpers.BuildingHelper.GetTodaySchedule(reg);
                if (today?.workShifts == null) return false;
                foreach (var w in today.workShifts)
                    if (w != null && w.itemInstanceId == stationId
                        && !string.IsNullOrEmpty(w.employeeId)
                        && hour >= w.startingHour && hour < w.endingHour)
                        return true;
            }
            catch { }
            return false;
        }

        // OWNER side: every 60s, snapshot every shop the local player owns (the source of the duty broadcast).
        // The SUMMARY line ALWAYS logs (even ownedShops=0) so its presence confirms the tick runs at all, and
        // ownedShops=0 while the player owns shops = an ownership/RentedByPlayer gap (root c).
        public static void LogStaffDiagOwner()
        {
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            if (Time.unscaledTime - _nextStaffDiagAt < 60f) return;
            _nextStaffDiagAt = Time.unscaledTime;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return;
                int owned = 0, ownedWithTill = 0;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    if (reg == null) continue;
                    bool mine = false; try { mine = MergerFlip.TrulyMine(reg); } catch { }   // TrulyMine (merger flip excluded)
                    if (!mine) continue;
                    owned++;
                    bool hasTill = false;
                    try
                    {
                        if (reg.itemInstances != null)
                            foreach (var kv in reg.itemInstances)
                            {
                                var n = kv.Value?.itemName ?? "";
                                if (n == "ba:itemname_checkoutcounterright" || n == "ba:itemname_checkoutcounterleft" || n == "ba:itemname_cashregister") { hasTill = true; break; }
                            }
                    }
                    catch { }
                    // Offices/factories have no till but DO have scheduled stations — the old till-only
                    // gate made "office staff not producing" invisible to every report (2026-07-09).
                    bool hasScheduledStations = false;
                    try
                    {
                        if (reg.scheduleDays != null)
                            foreach (var sd in reg.scheduleDays)
                                if (sd?.workShifts != null && sd.workShifts.Count > 0) { hasScheduledStations = true; break; }
                    }
                    catch { }
                    if (!hasTill && !hasScheduledStations) continue;
                    if (hasTill) ownedWithTill++;
                    DumpShopStaffing(reg, GameStateReader.AddressKey(reg), "[StaffDiag/own]");   // anomaly-gated (2026-07-09)
                }
                // Summary: on CHANGE or as a 10-min heartbeat (was every 60s — a per-minute narrator).
                // Its presence still proves the tick runs; ownedShops=0 while shops are owned = root c.
                string summary = $"[StaffDiag/own] SUMMARY ownedShops={owned} withCheckout={ownedWithTill} synthetics={_synthetics.Count} dutyToggles={_cashiers.Count}";
                if (summary != _lastStaffSummary || Time.unscaledTime >= _nextStaffSummaryHeartbeatAt)
                {
                    _lastStaffSummary = summary;
                    _nextStaffSummaryHeartbeatAt = Time.unscaledTime + 600f;
                    Plugin.Logger.LogWarning(summary);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffDiag/own] {ex.Message}"); }
        }

        // VISITOR side: on entering a shop (with a till) — dump what THIS machine has for it: the synced
        // work-shifts + whether a duty toggle / synthetic arrived. Pairs with the owner dump to span the chain.
        public static void LogStaffDiagOnEntry(BuildingRegistration reg, string addr)
        {
            try
            {
                if ((!MPServer.IsRunning && !MPClient.IsClientInWorld) || reg == null || string.IsNullOrEmpty(addr)) return;
                bool hasTill = false;
                try
                {
                    if (reg.itemInstances != null)
                        foreach (var kv in reg.itemInstances)
                        {
                            var n = kv.Value?.itemName ?? "";
                            if (n == "ba:itemname_checkoutcounterright" || n == "ba:itemname_checkoutcounterleft" || n == "ba:itemname_cashregister") { hasTill = true; break; }
                        }
                }
                catch { }
                if (!hasTill) return;   // not a till shop — irrelevant to the no-workers bug
                bool synthHere = false;
                try { foreach (var kv in _synthetics) if (kv.Value.addressKey == addr) { synthHere = true; break; } } catch { }
                int togglesHere = 0; try { foreach (var kv in _cashiers) if (kv.Value.address == addr) togglesHere++; } catch { }
                DumpShopStaffing(reg, addr, "[StaffDiag/visit]", forced: true);   // event-driven (one per entry) — keep unconditional
                Plugin.Logger.LogWarning($"[StaffDiag/visit] '{addr}' synthetic-here={synthHere} duty-toggles-here={togglesHere}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffDiag/visit] '{addr}': {ex.Message}"); }
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
        // ROUND-120: keyed by STATION (rounded duty position), NOT by building.  It used to be one entry per
        // ADDRESS, and TryStaffSynthetic's "already have one here?" guard therefore refused a second stand-in
        // anywhere in the same shop — so with two players on two tills only the FIRST till was ever staffed on
        // the other machines, and the simulator queued every customer at the one till it could see.  That was
        // invisible while the feature only served employee-staffed shops (one stand-in per shop was enough).
        private static readonly Dictionary<string, (string playerId, string addressKey, EmployeeInstance inst, Vector3 pos)> _synthetics = new();
        private static float _nextEvalAt;

        /// <summary>Is this station under EMPLOYEE duty (used by the staffing
        /// gate override — personal duty must never force the gate).</summary>
        public static bool IsEmployeeDutyStation(Vector3 pos)
            => _cashiers.TryGetValue(Key(pos), out var e) && e.employee;

        /// <summary>Round-144 - true when this position is a till WE staffed with a stand-in.  Scope guard
        /// for the ShouldUpdateEmployee ownership bypass (BusinessPatches): only OUR stations get it.</summary>
        internal static bool HasSyntheticNear(Vector3 pos)
        {
            try
            {
                string k = Key(pos);
                foreach (var v in _synthetics.Values)
                    if (Key(v.pos) == k) return true;
            }
            catch { }
            return false;
        }

        private static void TryStaffSynthetic(string addressKey, string playerId, string stationId, Vector3 dutyPos)
        {
            try
            {
                string stationKey = Key(dutyPos);
                if (string.IsNullOrEmpty(addressKey) || _synthetics.ContainsKey(stationKey)) return;
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
                // ROUND-121: the id MUST include the station.  It used to be player+address only, which was
                // unique while the registry allowed one stand-in per shop.  Now that stand-ins are per TILL, a
                // shop with several staffed stations produces several records with the SAME player and address
                // — and the duplicate-guard below would delete each previous roster entry as the next was
                // added, leaving one staffed till and two records pointing at instances no longer in the
                // roster.  That is the very symptom the per-till re-key was meant to remove.
                inst.id = $"{SyntheticDutyEmployeeIdPrefix}{playerId}_{addressKey.Replace(' ', '_')}_{stationKey}";
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
                _synthetics[stationKey] = (playerId, addressKey, inst, dutyPos);

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
            RemoveSynthetic(posKey);   // round-120: the registry IS keyed by station now
        }

        private static void RemoveSynthetic(string stationKey)
        {
            try
            {
                if (string.IsNullOrEmpty(stationKey) || !_synthetics.TryGetValue(stationKey, out var s)) return;
                _synthetics.Remove(stationKey);
                string addressKey = s.addressKey;   // round-120: the rest of this method is address-scoped
                // Freeze-correlation probe (2026-07-16): the record removal below makes
                // the native engine despawn the serving NPC; if the LOCAL player is
                // standing in this shop, that despawn is the suspected trigger for the
                // stuck-selection freeze (see Patch_MouseController_ResetSelected_
                // FreezeGuard).  One line so field logs pair trigger and guard.
                try
                {
                    if (!string.IsNullOrEmpty(CurrentShopAddress) && CurrentShopAddress == addressKey)
                        Plugin.Logger.LogInfo($"[SynthStaff] removing staff at '{addressKey}' while the local player is INSIDE — if a freeze follows, expect an [IoGuard] line next.");
                }
                catch { }
                // A schedule auto-fill may be IN FLIGHT for this business on a background thread
                // (ScheduleAutoFillerHelper: new Thread(FillWithEmployees)).  Native roster mutations
                // abort it (fire → EmployeeInstance.RemoveEmployee → AbortAutoFillForBusiness); ours
                // must too, or the filler keeps writing shifts for the id we sweep below — those
                // become permanently orphaned ("New Text" field report, 2026-07-09).  Cheap no-op
                // when nothing is running; null-guarded internally.
                try
                {
                    var fillReg = Helpers.BuildingHelper.GetBuildingRegistration(s.inst.assignedAddress);
                    if (fillReg != null) UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule.AbortAutoFillForBusiness(fillReg);
                }
                catch { }
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
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] remove at station '{stationKey}': {ex.Message}"); }
        }

        /// <summary>Remove ORPHANED synthetic duty employees.
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
                // WS3: injected roster records that leaked into a save have REAL ids (no prefix). Post-load
                // the registry is empty, so detect them structurally: an employee assigned to a building the
                // LOCAL player does not rent is not the local player's hire — the game never creates that.
                // (Unresolvable addresses are SKIPPED — never strip on uncertainty.)
                bool NotMyBuilding(EmployeeInstance? e)
                {
                    try
                    {
                        if (e?.assignedAddress == null) return false;
                        var r = Helpers.BuildingHelper.GetBuildingRegistration(e.assignedAddress);
                        return r != null && !r.RentedByPlayer;
                    }
                    catch { return false; }
                }
                var liveInjected = new HashSet<string>(_injectedStaff.Keys);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    string id = list[i]?.id ?? "";
                    bool synthetic = id.StartsWith(SyntheticDutyEmployeeIdPrefix);
                    bool orphanInjected = !synthetic && !liveInjected.Contains(id) && NotMyBuilding(list[i]);
                    if (orphanInjected)
                    {
                        // Records only — their shifts are the SYNCED schedule, not ours to strip.
                        try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
                        list.RemoveAt(i);
                        removed++;
                        Plugin.Logger.LogInfo($"[StaffRoster] orphan injected staff stripped ({when}): '{id}'.");
                        continue;
                    }
                    if (!synthetic || live.Contains(id)) continue;
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

                // Round-55 (Westi 2026-07-22): a native system that messaged the synthetic (the
                // rival poach offer, pre-fix) left a persistent "On-Duty Staff" CONTACT thread in
                // the save — contacts are keyed by character NAME and survive the employee strip.
                // No legitimate contact ever carries our invented name; purge it.
                try
                {
                    var contacts = gi?.Contacts;
                    if (contacts != null)
                    {
                        int purged = contacts.RemoveAll(c => c != null && c.id == "On-Duty Staff");
                        if (purged > 0)
                            Plugin.Logger.LogInfo($"[SynthStaff] purged {purged} stale 'On-Duty Staff' contact thread(s) ({when}) — native message to a synthetic (round-55).");
                    }
                }
                catch (Exception cx) { Plugin.Logger.LogWarning($"[SynthStaff] contact purge ({when}): {cx.Message}"); }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] orphan strip ({when}): {ex.Message}"); }
            return removed;
        }

        /// <summary>REPAIR ("New Text" field report, 2026-07-09): remove WorkShifts whose employeeId
        /// carries our BAMP_DUTY_ prefix but has NO matching employee record anywhere in the roster.
        /// Creation vector: schedule auto-fill runs on a BACKGROUND thread (ScheduleAutoFillerHelper:
        /// new Thread(FillWithEmployees)) with the business roster INCLUDING our synthetic (its
        /// assignedAddress is the shop, so the BizMan employee query returns it); when duty ends
        /// mid-fill, RemoveSynthetic sweeps the record + existing shifts but the filler then writes
        /// NEW shifts for the dead id.  Every cleanup we had iterated EMPLOYEES — an id existing only
        /// in shifts survived every sweep and serialized forever.  In the schedule UI such a shift
        /// renders as "New Text" (WorkShiftSlider.UpdateState NREs before setting the label) and
        /// cannot be removed (UpdateEmployeeAfterWorkShiftChange throws before the UI refresh).
        /// The prefix is ours alone → zero-false-positive removal, any mode, any ownership.
        /// DIAGNOSTIC (wide net, quiet logs): also LOGS — never removes — unresolvable REAL-id
        /// shifts on the player's own businesses, in case field ghosts are not prefix-tagged
        /// (would mean this diagnosis is wrong/partial; next report's log settles it).  Real-id
        /// shifts on OTHER owners' businesses are the synced schedule — expected, not logged.</summary>
        /// <summary>Real-id orphan count from the latest repair pass — consumed by
        /// MPSaveIntegrity's sweep summary (detect-only class).</summary>
        public static int LastRealIdOrphans;

        public static int RepairOrphanDutyShifts(string when)
        {
            int removed = 0, realIdOrphans = 0;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null) return 0;
                var known = new HashSet<string>();
                if (gi.EmployeeInstances != null)
                    foreach (var e in gi.EmployeeInstances)
                        if (!string.IsNullOrEmpty(e?.id)) known.Add(e.id);
                foreach (var r in gi.BuildingRegistrations)
                {
                    if (r?.scheduleDays == null) continue;
                    bool mine = false; try { mine = r.RentedByPlayer; } catch { }
                    for (int d = 0; d < r.scheduleDays.Count; d++)
                    {
                        var day = r.scheduleDays[d];
                        if (day?.workShifts == null) continue;
                        List<WorkShift>? dead = null;
                        for (int j = 0; j < day.workShifts.Count; j++)
                        {
                            var w = day.workShifts[j];
                            string id = w?.employeeId ?? "";
                            if (string.IsNullOrEmpty(id) || known.Contains(id)) continue;
                            if (id.StartsWith(SyntheticDutyEmployeeIdPrefix))
                            {
                                (dead ??= new List<WorkShift>()).Add(w!);
                                Plugin.Logger.LogWarning($"[ScheduleRepair] orphan duty shift ({when}): '{id}' biz='{r.BusinessName}' station='{w!.itemInstanceId}' day={d} h{w.startingHour}-{w.endingHour} — removing.");
                            }
                            else if (mine && realIdOrphans < 20)
                            {
                                realIdOrphans++;
                                Plugin.Logger.LogWarning($"[ScheduleDiag] REAL-ID ORPHAN shift ({when}): '{id}' biz='{r.BusinessName}' station='{w!.itemInstanceId}' day={d} h{w.startingHour}-{w.endingHour} — left in place.");
                            }
                        }
                        if (dead != null) { foreach (var w in dead) day.RemoveWorkShift(w); removed += dead.Count; }
                    }
                }
                if (removed > 0 || realIdOrphans > 0)
                    Plugin.Logger.LogWarning($"[ScheduleRepair] {when}: removed {removed} orphan duty shift(s); {realIdOrphans} real-id orphan(s) logged only.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ScheduleRepair] {when}: {ex.Message}"); }
            LastRealIdOrphans = realIdOrphans;
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
            // Orphan duty shifts (id-only leftovers with no record) must never serialize — sweep
            // them permanently before capturing the live synthetics (no restore for garbage).
            try { RepairOrphanDutyShifts(when + "-save"); } catch { }
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
                    // WS3: injected roster records (REAL ids, registry-tracked) are runtime-only exactly like
                    // the synthetics — strip the RECORDS from the save (their shifts are the synced schedule
                    // and stay; the ids simply read as missing to a single-player load, which is native-shaped).
                    if (_injectedStaff.ContainsKey(id))
                    {
                        removedEmployees.Add(emp!);
                        list.RemoveAt(i);
                        try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
                        continue;
                    }
                    if (!id.StartsWith(SyntheticDutyEmployeeIdPrefix)) continue;
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

        // ── WS3 (round-30): FULL STAFF-ROSTER SYNC ─────────────────────────────────────────────────────
        // The synced schedule (BusinessSync) already names REAL employee ids per station+hour; the only
        // thing other machines lack is the employee RECORDS those ids point to (gi.EmployeeInstances is
        // per-save). Owners publish a compact roster per business (30s heartbeat, signature-gated, so any
        // hire/fire/sickness reaches every machine within ~30s); receivers inject lightweight records with
        // the REAL ids so the game's OWN staffing engine — with the [StaffEval] gate override extended to
        // roster shops — spawns EVERY scheduled worker at the right station at the right hour, natively.
        // Injected records are runtime-only: registry-tracked, removed on Reset, stripped at the save
        // boundary, and orphan-swept after load via the not-my-building rule.

        // received rosters: addr → (publisher, staff list, sig, appliedSig)
        private static readonly Dictionary<string, (string pid, List<StaffInfo> staff, string sig)> _rosterByAddr = new();
        private static readonly Dictionary<string, string> _rosterApplied = new();            // addr → sig actually injected
        private static readonly Dictionary<string, (string addr, EmployeeInstance inst)> _injectedStaff = new();   // real-id records we injected
        private static readonly Dictionary<string, string> _rosterSigSent = new();            // owner side: addr → last published sig
        private static readonly Dictionary<string, string> _rosterLogSig  = new();            // round-189: addr → last LOGGED membership (log speaks on hire/fire/rename only)
        private static float _nextRosterPublishAt, _nextRosterApplyAt;

        /// <summary>Does this machine hold a synced staff roster for the address? (Gate-override consumer.)</summary>
        public static bool HasRosterFor(string addressKey)
            => !string.IsNullOrEmpty(addressKey) && _rosterByAddr.ContainsKey(addressKey);

        /// <summary>Is this employee id one WE injected from another player's roster? (Payroll-skip consumer:
        /// the payroll skip is keyed on THIS — which is why injected records can carry the REAL wage for
        /// display, slice 5 — and the fire route decides replica-vs-native on it.)</summary>
        public static bool IsInjectedStaff(string employeeId)
            => !string.IsNullOrEmpty(employeeId) && _injectedStaff.ContainsKey(employeeId);

        /// <summary>Round-100: is this a synthetic duty stand-in ("On-Duty Staff", BAMP_DUTY_ ids)?
        /// Synthetics are NEVER anyone's real staff — display/goal filters strip them
        /// unconditionally (no merged-partner exemption; S7 field report: ten "On-Duty Staff"
        /// rows in a business-less client's MyEmployees, one per staffed host shop).</summary>
        public static bool IsSyntheticDuty(string employeeId)
            => !string.IsNullOrEmpty(employeeId) && employeeId.StartsWith(SyntheticDutyEmployeeIdPrefix);

        /// <summary>World-health consumer (2026-07-09): how many partner-staff records are injected.</summary>
        public static int InjectedCount => _injectedStaff.Count;

        /// <summary>Slice 5: the shop an injected record belongs to ("" if not injected).</summary>
        public static string InjectedAddrOf(string employeeId)
            => !string.IsNullOrEmpty(employeeId) && _injectedStaff.TryGetValue(employeeId, out var v) ? v.addr : "";

        /// <summary>Is this injected record a MERGED partner's employee? Injected records exist to
        /// staff stations in OTHER players' shops — they are NOT the local player's employees, and
        /// MyEmployees must not list them (RED ROC report, 2026-07-09: "many of my friend's customer
        /// service employees are showing"). Under a merger they ARE ours by contract (slice 5) —
        /// resolve the shop's roster publisher and ask the merger.</summary>
        public static bool IsInjectedFromMergedPartner(string employeeId)
        {
            try
            {
                if (!_injectedStaff.TryGetValue(employeeId ?? "", out var v)) return false;
                lock (_rosterByAddr)
                    if (_rosterByAddr.TryGetValue(v.addr, out var r))
                        return MergerSync.IsMemberPid(r.pid);
            }
            catch { }
            return false;
        }

        /// <summary>Slice 5: optimistic local removal of an injected record (routed fire) — the owner's
        /// roster republish is the durable confirmation (or restores it if the op was lost).</summary>
        public static void DropInjectedStaff(string employeeId) => RemoveInjectedStaff(employeeId);

        /// <summary>Slice 5: owner-side nudge after a routed employee op — republish this shop's roster
        /// NOW instead of waiting out the 30s heartbeat.</summary>
        public static void ForceRosterRepublish(string addressKey)
        {
            try { _rosterSigSent.Remove(addressKey); _nextRosterPublishAt = 0f; } catch { }
        }

        private static string RosterSig(List<StaffInfo> staff)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in staff)
                sb.Append(s.Id).Append('|').Append(s.Name).Append('|').Append(s.Gender).Append('|').Append(s.Available)
                  .Append('|').Append(s.Wage.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('|').Append(s.Satisfaction.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('|').Append(s.Skills != null ? string.Join(",", s.Skills) : "")
                  .Append(';');
            return sb.ToString();
        }

        // OWNER: publish each owned business's roster when it changes (and once at session start — the sig
        // cache starts empty). An emptied roster (everyone fired) publishes too: empty sig ≠ old sig.
        private static void TickRosterPublish()
        {
            if (Time.unscaledTime < _nextRosterPublishAt) return;
            _nextRosterPublishAt = Time.unscaledTime + 30f;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.BuildingRegistrations == null || gi.EmployeeInstances == null) return;
                foreach (var reg in gi.BuildingRegistrations)
                {
                    bool mine; try { mine = MergerFlip.TrulyMine(reg); } catch { continue; }   // TrulyMine: never publish a partner shop roster
                    if (!mine) continue;
                    string addr = GameStateReader.AddressKey(reg);
                    var staff = new List<StaffInfo>();
                    foreach (var e in gi.EmployeeInstances)
                    {
                        if (e == null || string.IsNullOrEmpty(e.id)) continue;
                        if (e.id.StartsWith(SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) continue;   // stand-ins aren't staff
                        if (_injectedStaff.ContainsKey(e.id)) continue;                                            // never republish others' staff
                        bool here = false;
                        try { here = e.assignedAddress != null && e.assignedAddress.streetName == reg.StreetName && e.assignedAddress.streetNumber == reg.StreetNumber; } catch { }
                        if (!here) continue;
                        bool avail = true; try { avail = e.IsEmployeeAvailable(); } catch { }
                        int gender = -1;   try { gender = (int)e.characterData.gender; } catch { }
                        string nm = "";    try { nm = e.characterData?.name ?? ""; } catch { }
                        var si = new StaffInfo { Id = e.id, Name = nm, Gender = gender, Available = avail };
                        // Slice 5 display fidelity — a member's MyEmployees shows the partner's staff
                        // with real numbers (their payroll skip is id-keyed; a real wage can't double-bill).
                        try { si.Wage = e.hourlyWage; } catch { }
                        try { si.Satisfaction = e.satisfaction; } catch { }
                        try { si.AgeDays = e.characterData?.ageInDays ?? 0; } catch { }
                        try
                        {
                            // characterData.skills is THE skill list (HasSkill/GetPrimarySkill read it;
                            // EmployeeInstance.skills is a decoy — decompile-verified :211/:226).
                            var skills = e.characterData?.skills;
                            if (skills != null)
                                foreach (var sk in skills)
                                    if (sk != null && !string.IsNullOrEmpty(sk.name))
                                        si.Skills.Add(sk.name + "=" + sk.value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        catch { }
                        staff.Add(si);
                    }
                    staff.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                    string sig = RosterSig(staff);
                    if (_rosterSigSent.TryGetValue(addr, out var prev) && prev == sig) continue;
                    if (staff.Count == 0 && !_rosterSigSent.ContainsKey(addr)) continue;   // never had staff — nothing to say
                    _rosterSigSent[addr] = sig;
                    var p = new PlayerStaffRosterPayload { PlayerId = MPConfig.PlayerId, AddressKey = addr, Staff = staff };
                    var env = MessageEnvelope.Create(MessageType.PlayerStaffRoster, MPConfig.PlayerId, p);
                    if (MPServer.IsRunning) MPServer.BroadcastAny(env); else MPClient.SendEnvelope(env);
                    // Round-189 (user call): the publish sig deliberately includes DRIFTING fields
                    // (satisfaction, availability) so remote staff panels stay current — but that
                    // made this line fire on every morale tick (1,407 'published' lines in one
                    // field log).  The LOG speaks only when membership changes (hire/fire/rename);
                    // the broadcast above is unchanged.
                    var logSigSb = new System.Text.StringBuilder();
                    foreach (var s in staff) logSigSb.Append(s.Id).Append('|').Append(s.Name).Append(';');
                    string logSig = logSigSb.ToString();
                    if (!_rosterLogSig.TryGetValue(addr, out var prevLog) || prevLog != logSig)
                    {
                        _rosterLogSig[addr] = logSig;
                        Plugin.Logger.LogInfo($"[StaffRoster] published '{addr}': {staff.Count} staff (membership changed).");
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] publish: {ex.Message}"); }
        }

        /// <summary>Receiver: store an incoming roster (any thread) — injection happens on the main-thread
        /// apply tick, which also retries addresses whose registration hasn't synced yet.</summary>
        public static void ApplyRoster(PlayerStaffRosterPayload? p)
        {
            if (p == null || string.IsNullOrEmpty(p.AddressKey) || string.IsNullOrEmpty(p.PlayerId)) return;
            if (p.PlayerId == MPConfig.PlayerId) return;   // own echo
            var staff = p.Staff ?? new List<StaffInfo>();
            staff.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            lock (_rosterByAddr) { _rosterByAddr[p.AddressKey] = (p.PlayerId, staff, RosterSig(staff)); }
            _nextRosterApplyAt = 0f;   // apply promptly
        }

        /// <summary>All rosters this machine knows (host: for the join replay).</summary>
        public static List<PlayerStaffRosterPayload> SnapshotRosters()
        {
            var list = new List<PlayerStaffRosterPayload>();
            try
            {
                lock (_rosterByAddr)
                    foreach (var kv in _rosterByAddr)
                        list.Add(new PlayerStaffRosterPayload { PlayerId = kv.Value.pid, AddressKey = kv.Key, Staff = kv.Value.staff });
                // The host's OWN rosters aren't in _rosterByAddr — force a fresh publish sweep instead
                // (cheap; the joiner gets them within the next 30s heartbeat, or instantly via this nudge).
                _rosterSigSent.Clear();
                _nextRosterPublishAt = 0f;
            }
            catch { }
            return list;
        }

        // Receiver main-thread apply: inject/update/remove records so gi matches the synced rosters.
        private static void TickRosterApply()
        {
            if (Time.unscaledTime < _nextRosterApplyAt) return;
            _nextRosterApplyAt = Time.unscaledTime + 10f;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi?.EmployeeInstances == null || gi.BuildingRegistrations == null) return;
                List<(string addr, (string pid, List<StaffInfo> staff, string sig) v)> pending = new();
                lock (_rosterByAddr)
                    foreach (var kv in _rosterByAddr)
                        if (!_rosterApplied.TryGetValue(kv.Key, out var applied) || applied != kv.Value.sig)
                            pending.Add((kv.Key, kv.Value));
                foreach (var (addr, v) in pending)
                {
                    BuildingRegistration? reg = null;
                    foreach (var r in gi.BuildingRegistrations)
                        if (r != null && GameStateReader.AddressKey(r) == addr) { reg = r; break; }
                    if (reg == null) continue;   // building not synced yet — retried next tick

                    var want = new Dictionary<string, StaffInfo>();
                    foreach (var s in v.staff) if (s != null && !string.IsNullOrEmpty(s.Id)) want[s.Id] = s;

                    // Remove injected records for this address that left the roster (fired/moved). Shifts are
                    // NOT touched — they're the synced schedule; a dangling id there just reads as missing.
                    var gone = new List<string>();
                    foreach (var kv in _injectedStaff)
                        if (kv.Value.addr == addr && !want.ContainsKey(kv.Key)) gone.Add(kv.Key);
                    foreach (var id in gone) RemoveInjectedStaff(id);

                    int added = 0, updated = 0;
                    foreach (var s in want.Values)
                    {
                        if (_injectedStaff.TryGetValue(s.Id, out var have))
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(s.Name)) have.inst.characterData.name = s.Name;
                                have.inst.isAbsent = !s.Available; have.inst.isReplaced = false;
                                ApplyStaffFidelity(have.inst, s);   // slice 5: wage/skills/satisfaction stay current
                            }
                            catch { }
                            updated++;
                            continue;
                        }
                        bool existsLocally = false;
                        try { existsLocally = Helpers.EmployeeHelper.EmployeeInstancesDictionary.ContainsKey(s.Id); } catch { }
                        if (existsLocally)
                        {
                            // Slice 5 adopt-confirm: the owner's roster carrying an id we have PENDING-ADOPT
                            // means the record now lives in THEIR save — release our local original and fall
                            // through to inject the display copy. Any other local-id collision keeps the
                            // defensive skip (never shadow a genuinely local record).
                            if (!MergerEmployeeSync.ConfirmAdopt(s.Id)) continue;
                            try
                            {
                                for (int i = gi.EmployeeInstances.Count - 1; i >= 0; i--)
                                    if (gi.EmployeeInstances[i]?.id == s.Id) gi.EmployeeInstances.RemoveAt(i);
                                Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(s.Id);
                            }
                            catch { }
                        }
                        var inst = Helpers.EmployeeHelper.CreateAIEmployeeInstance("ba:skill_customerservice");
                        if (inst == null) continue;
                        inst.id = s.Id;                       // REAL id — the synced shifts match it natively
                        inst.hourlyWage = 0f;                 // overwritten by fidelity below when the wire carries it
                        inst.satisfaction = 100f;
                        inst.assignedAddress = new Address(reg.StreetName, reg.StreetNumber);
                        inst.characterData.name = string.IsNullOrEmpty(s.Name) ? "Staff" : s.Name;
                        inst.characterData.ageInDays = Helpers.RecruitmentHelper.GetRandomEmployeeAgeInDays();
                        try { if (s.Gender >= 0) inst.characterData.gender = (BigAmbitions.Characters.Gender)s.Gender; } catch { }
                        inst.isAbsent = !s.Available; inst.isReplaced = false;
                        ApplyStaffFidelity(inst, s);          // slice 5: real wage/skills/satisfaction/age for display
                        for (int i = gi.EmployeeInstances.Count - 1; i >= 0; i--)
                            if (gi.EmployeeInstances[i]?.id == inst.id) gi.EmployeeInstances.RemoveAt(i);
                        gi.EmployeeInstances.Add(inst);
                        try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[inst.id] = inst; } catch { }
                        _injectedStaff[inst.id] = (addr, inst);
                        added++;
                    }
                    _rosterApplied[addr] = v.sig;
                    if (added + updated + gone.Count > 0)
                        Plugin.Logger.LogInfo($"[StaffRoster] applied '{addr}': +{added} ~{updated} -{gone.Count} (total injected {_injectedStaff.Count}).");

                    // If the local player is INSIDE this shop, prod every station to re-evaluate now
                    // (otherwise the native hourly re-evaluation picks the changes up).
                    try
                    {
                        if (CurrentShopAddress == addr)
                        {
                            var arr = GetStationsCached();   // round-186: registry, never a scene walk
                            if (arr != null)
                                foreach (var o in arr)
                                    (o as EmployeeStationController)?.UpdateEmployee(false);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] apply: {ex.Message}"); }
        }

        /// <summary>Slice 5: apply the wire's display-fidelity fields to an injected record. Wage is
        /// safe to be real — the payroll skip keys on the injected-id registry, not on wage 0. Skills
        /// replace the placeholder so MyEmployees/BizMan show the partner staff's true role/levels.</summary>
        private static void ApplyStaffFidelity(EmployeeInstance inst, StaffInfo s)
        {
            try
            {
                if (s.Wage > 0f) inst.hourlyWage = s.Wage;
                if (s.Satisfaction > 0f) inst.satisfaction = s.Satisfaction;
                if (s.AgeDays > 0) inst.characterData.ageInDays = s.AgeDays;
                if (s.Skills != null && s.Skills.Count > 0)
                {
                    // characterData.skills is THE list — HasSkill/GetPrimarySkill/every display reads it
                    // (EmployeeInstance.skills is a decoy; decompile-verified EmployeeInstance :211/:226).
                    var skills = inst.characterData.skills;
                    skills.Clear();
                    foreach (var pair in s.Skills)
                    {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0) continue;
                        float.TryParse(pair.Substring(eq + 1), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var val);
                        skills.Add(new BigAmbitions.Characters.Skills.Skill { name = pair.Substring(0, eq), value = val });
                    }
                }
            }
            catch { }
        }

        private static void RemoveInjectedStaff(string id)
        {
            try
            {
                if (!_injectedStaff.TryGetValue(id, out var have)) return;
                _injectedStaff.Remove(id);
                // Same auto-fill hazard as RemoveSynthetic: an in-flight background fill holding
                // this record would write shifts for an id that is about to lose its record.
                try
                {
                    var fillReg = Helpers.BuildingHelper.GetBuildingRegistration(have.inst?.assignedAddress);
                    if (fillReg != null) UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule.AbortAutoFillForBusiness(fillReg);
                }
                catch { }
                var gi = SaveGameManager.Current;
                if (gi?.EmployeeInstances != null) gi.EmployeeInstances.Remove(have.inst);
                try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
                Plugin.Logger.LogInfo($"[StaffRoster] removed injected staff '{id}' ({have.addr}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] remove '{id}': {ex.Message}"); }
        }

        // ── Round-196: business-sale staff migration (user ruling: workers transfer) ──

        /// <summary>HOST: the sold shop's roster — the stored copy from the seller's
        /// heartbeat publishes, or built fresh from local records when the seller is
        /// this machine. A copy, safe to ship in the Finalize payload.</summary>
        internal static List<StaffInfo> RosterCopyFor(string addr, string sellerPid)
        {
            try
            {
                if (sellerPid != MPConfig.PlayerId)
                {
                    lock (_rosterByAddr)
                        if (_rosterByAddr.TryGetValue(addr, out var v) && v.pid == sellerPid)
                            return new List<StaffInfo>(v.staff);
                    return new List<StaffInfo>();
                }
                return BuildLocalRoster(addr);
            }
            catch { return new List<StaffInfo>(); }
        }

        private static List<StaffInfo> BuildLocalRoster(string addr)
        {
            var staff = new List<StaffInfo>();
            try
            {
                var gi = SaveGameManager.Current;
                var reg = GameStatePatcher.FindRegistration(addr);
                if (gi?.EmployeeInstances == null || reg == null) return staff;
                foreach (var e in gi.EmployeeInstances)
                {
                    if (e == null || string.IsNullOrEmpty(e.id)) continue;
                    if (e.id.StartsWith(SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) continue;
                    if (_injectedStaff.ContainsKey(e.id)) continue;
                    bool here = false;
                    try { here = e.assignedAddress != null && e.assignedAddress.streetName == reg.StreetName && e.assignedAddress.streetNumber == reg.StreetNumber; } catch { }
                    if (!here) continue;
                    bool avail = true; try { avail = e.IsEmployeeAvailable(); } catch { }
                    int gender = -1;   try { gender = (int)e.characterData.gender; } catch { }
                    string nm = "";    try { nm = e.characterData?.name ?? ""; } catch { }
                    var si = new StaffInfo { Id = e.id, Name = nm, Gender = gender, Available = avail };
                    try { si.Wage = e.hourlyWage; } catch { }
                    try { si.Satisfaction = e.satisfaction; } catch { }
                    try { si.AgeDays = e.characterData?.ageInDays ?? 0; } catch { }
                    try
                    {
                        var skills = e.characterData?.skills;
                        if (skills != null)
                            foreach (var sk in skills)
                                if (sk != null && !string.IsNullOrEmpty(sk.name))
                                    si.Skills.Add(sk.name + "=" + sk.value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    catch { }
                    staff.Add(si);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] local roster '{addr}': {ex.Message}"); }
            return staff;
        }

        /// <summary>BUYER: the injected mirrors at a purchased shop become OUR real
        /// employees — same records, same real ids (the synced shifts keep matching),
        /// just no longer tracked as injected (so payroll, HR, and publishes treat
        /// them as ours). Roster tracking for the address is cleared so no stale
        /// partner roster ever re-processes them.</summary>
        internal static int PromoteInjectedForTransfer(string addr)
        {
            int n = 0;
            try
            {
                var promote = new List<string>();
                foreach (var kv in _injectedStaff)
                    if (kv.Value.addr == addr) promote.Add(kv.Key);
                foreach (var id in promote) { _injectedStaff.Remove(id); n++; }
                lock (_rosterByAddr) { _rosterByAddr.Remove(addr); }
                _rosterApplied.Remove(addr);
                if (n > 0) Plugin.Logger.LogInfo($"[StaffRoster] PROMOTED {n} injected record(s) at '{addr}' to real staff (business purchased — round-196).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] promote '{addr}': {ex.Message}"); }
            return n;
        }

        /// <summary>BUYER: create real records for wire staff not present locally
        /// (a buyer who never received the seller's roster — e.g. fresh joiner).
        /// Appearance regenerates locally (not in the wire format — disclosed gap).</summary>
        internal static int EnsureRealStaff(string addr, List<StaffInfo>? staff)
        {
            int created = 0;
            try
            {
                if (staff == null || staff.Count == 0) return 0;
                var gi = SaveGameManager.Current;
                var reg = GameStatePatcher.FindRegistration(addr);
                if (gi?.EmployeeInstances == null || reg == null) return 0;
                foreach (var s in staff)
                {
                    if (s == null || string.IsNullOrEmpty(s.Id)) continue;
                    bool exists = false;
                    try { exists = Helpers.EmployeeHelper.EmployeeInstancesDictionary.ContainsKey(s.Id); } catch { }
                    if (!exists)
                        foreach (var e in gi.EmployeeInstances)
                            if (e?.id == s.Id) { exists = true; break; }
                    if (exists) continue;
                    var inst = Helpers.EmployeeHelper.CreateAIEmployeeInstance("ba:skill_customerservice");
                    if (inst == null) continue;
                    inst.id = s.Id;
                    inst.assignedAddress = new Address(reg.StreetName, reg.StreetNumber);
                    inst.characterData.name = string.IsNullOrEmpty(s.Name) ? "Staff" : s.Name;
                    inst.characterData.ageInDays = Helpers.RecruitmentHelper.GetRandomEmployeeAgeInDays();
                    try { if (s.Gender >= 0) inst.characterData.gender = (BigAmbitions.Characters.Gender)s.Gender; } catch { }
                    inst.isAbsent = !s.Available; inst.isReplaced = false;
                    ApplyStaffFidelity(inst, s);
                    gi.EmployeeInstances.Add(inst);
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[inst.id] = inst; } catch { }
                    created++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] ensure-real '{addr}': {ex.Message}"); }
            return created;
        }

        /// <summary>SELLER: real staff records at a sold shop leave this save —
        /// they live in the buyer's save now (never touches injected/synthetic).</summary>
        internal static int DropRealStaffAt(string addr)
        {
            int n = 0;
            try
            {
                var gi = SaveGameManager.Current;
                var reg = GameStatePatcher.FindRegistration(addr);
                if (gi?.EmployeeInstances == null || reg == null) return 0;
                try { UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule.AbortAutoFillForBusiness(reg); } catch { }
                for (int i = gi.EmployeeInstances.Count - 1; i >= 0; i--)
                {
                    var e = gi.EmployeeInstances[i];
                    if (e == null || string.IsNullOrEmpty(e.id)) continue;
                    if (e.id.StartsWith(SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) continue;
                    if (_injectedStaff.ContainsKey(e.id)) continue;
                    bool here = false;
                    try { here = e.assignedAddress != null && e.assignedAddress.streetName == reg.StreetName && e.assignedAddress.streetNumber == reg.StreetNumber; } catch { }
                    if (!here) continue;
                    string eid = e.id;
                    gi.EmployeeInstances.RemoveAt(i);
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(eid); } catch { }
                    n++;
                }
                _rosterSigSent.Remove(addr);   // this address is no longer ours to publish
                if (n > 0) Plugin.Logger.LogInfo($"[StaffRoster] transferred out {n} real staff record(s) at '{addr}' (business sold — round-196).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] drop-real '{addr}': {ex.Message}"); }
            return n;
        }

        /// <summary>Round-197: the CURRENT register-duty state, sent to one player —
        /// duty toggles are one-shot events a joiner's quiesce window may have
        /// dropped (the shop then looks unstaffed until the next toggle).</summary>
        internal static int SendDutyStateTo(string pid)
        {
            int n = 0;
            try
            {
                foreach (var kv in _cashiers)
                {
                    var (playerId, pos, employee, address, stationId) = kv.Value;
                    if (playerId == pid) continue;   // their own duty state died with their session
                    var p = new RegisterCashierPayload
                    {
                        PlayerId = playerId, X = pos.x, Y = pos.y, Z = pos.z,
                        On = true, Address = address, StationId = stationId, Employee = employee,
                    };
                    MPServer.SendToPlayer(pid, MessageEnvelope.Create(MessageType.RegisterCashier, "host", p));
                    n++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Register] duty re-send: {ex.Message}"); }
            return n;
        }

        /// <summary>World-ready reconciliation (round-196): a REAL (non-injected,
        /// non-synthetic) employee assigned to a business this machine does not own
        /// is residue of an interrupted transfer (e.g. seller crashed after the buyer
        /// claimed) — the record's home is the owner's save. Remove it, loudly.
        /// Unknown stays untouched: no registration ⇒ no verdict.</summary>
        public static int DemoteForeignAssignedStaff(string when)
        {
            int n = 0;
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return 0;
                var gi = SaveGameManager.Current;
                if (gi?.EmployeeInstances == null || gi.BuildingRegistrations == null) return 0;
                for (int i = gi.EmployeeInstances.Count - 1; i >= 0; i--)
                {
                    var e = gi.EmployeeInstances[i];
                    if (e == null || string.IsNullOrEmpty(e.id) || e.assignedAddress == null) continue;
                    if (e.id.StartsWith(SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) continue;
                    if (_injectedStaff.ContainsKey(e.id)) continue;
                    BuildingRegistration? reg = null;
                    foreach (var r in gi.BuildingRegistrations)
                    {
                        bool match = false;
                        try { match = r != null && e.assignedAddress.streetName == r.StreetName && e.assignedAddress.streetNumber == r.StreetNumber; } catch { }
                        if (match) { reg = r; break; }
                    }
                    if (reg == null) continue;   // unknown ≠ foreign — never delete on missing data
                    bool mine; try { mine = MergerFlip.TrulyMine(reg); } catch { continue; }
                    if (mine || reg.RentedByPlayer) continue;
                    string eid = e.id, nm = ""; try { nm = e.characterData?.name ?? ""; } catch { }
                    gi.EmployeeInstances.RemoveAt(i);
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(eid); } catch { }
                    n++;
                    Plugin.Logger.LogWarning($"[StaffRoster] RECONCILED ({when}): real employee '{nm}' ({eid}) was assigned to '{GameStateReader.AddressKey(reg)}' which this machine does not own — record removed (interrupted-transfer residue, round-196).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] reconcile: {ex.Message}"); }
            return n;
        }

        /// <summary>Invoke the game's staffing evaluator (UpdateEmployee — the
        /// disasm-mapped spawner) on unstaffed synthetic stations every 5s;
        /// nothing calls it for rival-translated shops natively.</summary>
        // Round-166 - LIVE STATION REGISTRY: even one cached FindObjectsOfType refresh cost ~60ms
        // per 30s on the 826-building save (StaffEval bucket).  Stations announce themselves at
        // Start (Harmony postfix below) and destroyed ones prune lazily on read (Unity's overloaded
        // null), so the evaluator never scans the scene at all.
        private static readonly List<EmployeeStationController> _stationRegistry = new();
        private static UnityEngine.Object[] _stationSnapshot = System.Array.Empty<UnityEngine.Object>();
        private static bool _stationRegistryDirty = true;

        internal static void RegisterStation(EmployeeStationController st)
        {
            try { if (st != null && !_stationRegistry.Contains(st)) { _stationRegistry.Add(st); _stationRegistryDirty = true; } } catch { }
        }

        /// <summary>The live stations, pruned of destroyed entries - no scene walk, ever.</summary>
        private static UnityEngine.Object[] GetStationsCached()
        {
            bool pruned = _stationRegistry.RemoveAll(st => st == null) > 0;   // Unity null = destroyed
            if (pruned || _stationRegistryDirty)
            {
                _stationSnapshot = _stationRegistry.ToArray();
                _stationRegistryDirty = false;
            }
            return _stationSnapshot;
        }

        /// <summary>Round-166: every station announces itself when its Start runs (interior load,
        /// item placement, guest-edit apply alike) - the registry's single producer.</summary>
        [HarmonyLib.HarmonyPatch(typeof(EmployeeStationController), "Start")]
        public static class Patch_EmployeeStation_Start_Registry
        {
            static void Postfix(EmployeeStationController __instance) => RegisterStation(__instance);
        }


        private static void TickStaffEvaluator()
        {
            if (_synthetics.Count == 0) return;
            if (Time.unscaledTime < _nextEvalAt) return;
            _nextEvalAt = Time.unscaledTime + 5f;
            // Round-98 (field "lag spike every ~6s", JP report + S7 repro): this pass used to run
            // FindObjectsOfType — a FULL-SCENE walk — once PER synthetic station, every 5s, even
            // with no interior loaded (the null-station check came after the scan): ~630ms/pass
            // on a client in a built-up world. Stations only exist inside the ONE interior loaded
            // around the local player, so skip the pass unless a synthetic is plausibly near us,
            // then scan the scene ONCE for all synthetics.
            Vector3 me;
            try { me = Helpers.PlayerHelper.GetPosition(); } catch { return; }
            const float NearRadius2 = 150f * 150f;   // interiors sit on distant coordinate islands — generous
            bool anyNear = false;
            foreach (var kv in _synthetics)
                if ((kv.Value.pos - me).sqrMagnitude <= NearRadius2) { anyNear = true; break; }
            if (!anyNear) return;   // street/other-island: no synthetic till can be loaded here
            // Round-165: FindObjectsOfType here WAS the hitch — StaffEval worst=62ms, convicted by the
            // named perf buckets (a full-scene walk on an 826-building save, every 5s pass).  Stations
            // exist only in the loaded interior, which changes when the local player crosses a door —
            // cache per shop address with a 30s TTL (one refresh hitch per 30s worst case, at the door
            // it rides the interior-load hitch that exists anyway).
            var stations = GetStationsCached();
            foreach (var kv in _synthetics)
            {
                try
                {
                    if ((kv.Value.pos - me).sqrMagnitude > NearRadius2) continue;   // its interior can't be the loaded one
                    // Round-30 (WS2): any EmployeeStationController counts — the old CashRegisterController-
                    // only search was the visitor-side twin of the owner-side checkout whitelist.
                    var st = NearestStationIn(stations, kv.Value.pos, 2f);
                    if (st == null) continue;                    // interior not loaded here
                    if (st.employeeInstance != null) continue;   // already staffed
                    Plugin.Logger.LogInfo($"[SynthStaff] invoking UpdateEmployee(false) on station at '{kv.Key}'.");
                    st.UpdateEmployee(false);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[SynthStaff] evaluator: {ex.Message}"); }
            }
        }

        private static EmployeeStationController? NearestStationIn(UnityEngine.Object[]? stations, Vector3 from, float maxDist)
        {
            EmployeeStationController? best = null;
            float bestD2 = maxDist * maxDist;
            if (stations != null)
                foreach (var o in stations)
                {
                    var c = o as EmployeeStationController;
                    if (c == null) continue;
                    float d2 = (c.transform.position - from).sqrMagnitude;
                    if (d2 < bestD2) { bestD2 = d2; best = c; }
                }
            return best;
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
