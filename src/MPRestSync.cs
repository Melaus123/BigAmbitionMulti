using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Consensus time-skip v3 ("our wiring", user-designed 2026-06-10).
    ///
    /// PRINCIPLE: the game's skip engine (TimeMachine) NEVER stays alive in
    /// MP.  Sitting just sits — pure native behavior, normal time.  Waiting
    /// is OUR system: while seated, a small "Wait until…" button (MPCanvasUI)
    /// raises a vote with an absolute goal (minimum 1 hour ahead).  When ALL
    /// players have an active vote the HOST races the authoritative clock to
    /// the EARLIEST goal; standing up or cancelling drops your vote and stops
    /// the skip.  No native overlay, no pause, no hidden mechanics.
    ///
    /// The native skip button is neutralized, not fought: if pressed, the
    /// engine starts and is immediately shut down through ITS OWN off switch
    /// (complete, self-consistent teardown) and a notice points to our button.
    /// A watchdog clears any leftover time-freeze every second — a hard-lock
    /// is structurally impossible.
    /// </summary>
    public static class MPRestSync
    {
        public const float SkipMinutesPerRealSecond = 25f;
        // Defensive ceiling on game-minutes simulated in a SINGLE frame during a skip: a frame-time spike
        // (alt-tab, stall) must not dump many hours of economy into one frame (~70ms/simulated-hour). At the
        // normal 25 min/s rate a frame advances <1 min, so this only ever caps a recovery frame after a spike.
        public const float MaxSkipMinutesPerFrame = 60f;

        // ── Local state ───────────────────────────────────────────────────────
        public static bool   Seated       { get; private set; }

        /// <summary>What the DOCK should follow: after StandUp, the same
        /// activity instance is suppressed — a half-cancelled approach (click
        /// bench, walk away) left Seated wedged true and the dock stuck open
        /// with no X (user, 2026-06-11).  A NEW activity un-suppresses.</summary>
        public static bool SeatedForUi => Seated && !ReferenceEquals(_curActRef, _suppressedActRef);
        private static object? _suppressedActRef;
        private static object? _curActRef;

        /// <summary>True once the avatar has physically ARRIVED in the activity (sitting / performing) —
        /// NOT while still choosing or walking over (MovingTowardsActivity).  This DELAYS the dock until
        /// the player is seated; it gates NOTHING else (the activity, the auto-start, and the vanilla-panel
        /// suppression all run regardless), and it can't gate the dock OUT — it reads the game's own LIVE
        /// state (HasNavigationDisabled && not moving-towards), so once seated it reliably shows.</summary>
        public static bool AvatarInActivity()
        {
            try
            {
                var (ui, uiType) = GetActivityUiCached();
                if (ui == null || uiType == null) return false;
                bool navDisabled = (bool)(uiType.GetMethod("HasNavigationDisabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null) ?? false);
                bool moving = (bool)(uiType.GetProperty("IsMovingTowardsActivity",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) ?? false);
                return navDisabled && !moving;
            }
            catch { return false; }
        }

        public static string ActivityName { get; private set; } = "";
        public static int    ActivityState { get; private set; } = -1;   // PlayerActivityState; -1 = none
        private static bool   _localVoteActive;
        private static double _localGoal;
        private static float  _nextPollAt;

        public static bool LocalVoteActive => _localVoteActive;
        public static double LocalGoal     => _localGoal;

        /// <summary>The live activity object (null when not seated) — lets
        /// MPRegisterSync read WorkActivity._employeeStationController, the
        /// EXACT station being worked (replaces the nearest-register-in-5m
        /// guess; decompile sweep 2026-06-12).</summary>
        public static object? CurrentActivityObject => _curAct;

        // ── Dock data: passthrough of the activity's own buttons ─────────────
        public sealed class DockButton
        {
            public string  Label = "";
            public object? OnClick;
        }
        public static readonly List<DockButton> DockButtons = new();
        /// <summary>Index of the Stop/Cancel button in DockButtons (-1 = none) —
        /// rendered as the dock's header X.</summary>
        public static int CancelButtonIndex { get; private set; } = -1;
        private static object? _lastAutoStartedActRef;   // activity instance we last auto-pressed Start on — fire once per instance, no time gate

        // ── Shared state (host-broadcast; banner + detector stand-down) ──────
        public static readonly List<RestVoteEntry> Votes = new();
        public static int  RequiredVotes;
        public static volatile bool SkipActive;

        // ── Host-only ─────────────────────────────────────────────────────────
        private static readonly Dictionary<string, RestVoteEntry> _hostVotes = new();
        private static double _skipGoalMinutes;

        public static void Reset()
        {
            Seated = false; ActivityName = ""; ActivityState = -1;
            DockButtons.Clear();
            _localVoteActive = false; _localGoal = 0;
            _machine = null;
            Votes.Clear(); RequiredVotes = 0; SkipActive = false;
            _hostVotes.Clear(); _skipGoalMinutes = 0;
            TimeSync.AheadHeld = false;   // drop any stale ahead-hold so it can't freeze the clock
        }

        /// <summary>On RECONNECT, clear ONLY the consensus/skip state — the host's vote tally and any
        /// in-flight skip are gone on its side, so a stale SkipActive or leftover vote rows would wedge the
        /// dock or keep the world-clock detector standing down. LOCAL seating (Seated / ActivityName / the
        /// live activity refs / DockButtons) is PRESERVED — the player is still in their activity after
        /// rejoining. Harmless on the initial connect (state already empty).</summary>
        public static void ClearVotesOnReconnect()
        {
            _localVoteActive = false; _localGoal = 0;
            Votes.Clear(); RequiredVotes = 0; SkipActive = false;
            _hostVotes.Clear(); _skipGoalMinutes = 0;
            TimeSync.AheadHeld = false;   // drop any stale ahead-hold so it can't freeze the clock post-reconnect
        }

        // ── Taxi v2: INSTANT ARRIVAL (user-chosen, 2026-06-10) ───────────────
        // The ride's completion handler (TaxiSystem.OnTimeMachineEnded) is what
        // teleports the player — so: machine starts, we hide its misleading
        // overlay (frozen clock, "Day 123") and stop it through its own off
        // switch a beat later.  Ride completes instantly, clock never moves.
        private static float _taxiPendingUntil;
        private static float _taxiStopAt;

        public static bool TaxiRidePending => Time.unscaledTime < _taxiPendingUntil;

        public static void OnTaxiRideStarting()
        {
            _taxiPendingUntil = Time.unscaledTime + 8f;
            Plugin.Logger.LogInfo("[Taxi] ride starting — instant-arrival mode armed.");
        }

        public static void OnTaxiMachineStarted()
        {
            SetMachineCanvasVisible(false);            // no frozen-clock overlay
            _taxiStopAt = Time.unscaledTime + 0.3f;    // let the caller settle first
            Plugin.Logger.LogInfo("[Taxi] ride machine started — stopping for instant arrival.");
        }

        // ── Native skip engine → clean neutralization (MPPatches Postfix) ─────
        public static void OnNativeSkipButtonPressed()
        {
            StopLocalMachine();   // the game's own complete teardown
            Plugin.Logger.LogInfo("[Rest] native skip engine start neutralized (stopped through its own off switch).");
        }

        // ── Our wait API (called by the MPCanvasUI wait button/panel) ─────────
        public static double NowMinutes()
        {
            var (d, h) = GameStateReader.GetGameTime();
            return d * 1440.0 + h * 60.0;
        }

        /// <summary>Earliest other player's goal, for the "Match" button.  0 = none.</summary>
        public static double OtherVoteGoal(out string who)
        {
            who = "";
            double best = 0;
            foreach (var v in Votes)
            {
                if (v.PlayerId == MPConfig.PlayerId) continue;
                if (best == 0 || v.GoalMinutes < best) { best = v.GoalMinutes; who = v.PlayerId; }
            }
            return best;
        }

        /// <summary>Toggle/update the skip request.  goalMinutes is absolute
        /// (total game-minutes); clamped to a few minutes ahead — no other
        /// minimum (user removed the 1h floor).</summary>
        public static void SetSkipRequest(bool on, double goalMinutes = 0)
        {
            if (!on)
            {
                if (!_localVoteActive) return;
                _localVoteActive = false;
                SendVote(false, 0, "");
                Plugin.Logger.LogInfo("[Rest] skip request OFF.");
                return;
            }
            if (!Seated)
            {
                // Never swallow a player's commit silently (Goonie report, 2026-07-09): a click landing
                // in a seated-flag flicker was indistinguishable from "never clicked" in the logs.
                Plugin.Logger.LogInfo("[Rest] skip request IGNORED — not seated at click time.");
                return;
            }
            double now = NowMinutes();
            if (goalMinutes < now + 5) goalMinutes = now + 5;
            goalMinutes = Math.Ceiling(goalMinutes / 5.0) * 5.0;
            _localGoal = goalMinutes;
            _localVoteActive = true;
            EnsureActivityCovers(goalMinutes);   // game must not auto-stand us mid-vote
            SendVote(true, goalMinutes, ActivityName);
            Plugin.Logger.LogInfo($"[Rest] skip request ON: until {Fmt(goalMinutes)} ({ActivityName}).");
        }


        /// <summary>Guaranteed stand-up: the activity's own Stop/Cancel button
        /// when present, else the activity's Finish() directly (concrete cast).
        /// The exit must never depend on a button existing.</summary>
        private static float _suppressAutoStartUntil;
        private static float _navHealNext;
        private static float _notSeatedSince;
        private static System.Reflection.FieldInfo? _navBlockerSetField;
        private static System.Reflection.FieldInfo? _navAgentField;
        private static System.Reflection.FieldInfo? _sittingOnField;
        private static System.Reflection.FieldInfo? _charRbField;   // round-195: kinematic-wedge check
        private static float _cartBadSince = -1f;   // round-83 tripwire: when cart-mode machinery went bad
        private static float _cartWarnNext;         // round-83 tripwire: re-warn throttle
        private static readonly Dictionary<NavigationBlocker, float> _foreignHeldSince = new();   // round-90 foreign-blocker watch
        private static float _foreignWarnNext;

        /// <summary>The nine ACTIVITY-class navigation blockers — the IPlayerActivity states whose
        /// UI our dock replaces (and whose lifecycle we therefore own in MP). The nav-heal watchdog
        /// may release ONLY these; every other NavigationBlocker key belongs to a different system
        /// (menus, vehicles, placement, scripted sequences) with its own lifecycle.</summary>
        private static readonly NavigationBlocker[] ActivityBlockers =
        {
            NavigationBlocker.RestActivity,   NavigationBlocker.SleepActivity,
            NavigationBlocker.WorkActivity,   NavigationBlocker.WorkoutActivity,
            NavigationBlocker.HygieneActivity, NavigationBlocker.EntertainActivity,
            NavigationBlocker.StudyActivity,  NavigationBlocker.SwimmingActivity,
            NavigationBlocker.PaidActivity,
        };

        public static void StandUp()
        {
            try
            {
                // Standing must STAY stood: without this cooldown the auto-sit
                // re-engaged the lingering activity ~1.5s after every cancel
                // (visible stand, then silently busy again = movement lock).
                _suppressAutoStartUntil = Time.unscaledTime + 4f;
                // The dock must not re-show for THIS activity instance even if
                // the game keeps it half-alive (walk-away wedge).
                _suppressedActRef = _curActRef;

                // Decide from the activity's LIVE state, never the poll snapshot:
                // DockButtons/CancelButtonIndex can be up to 0.5s stale, and auto-start
                // + already-standing-at-the-station advances NotStarted→Started within
                // milliseconds. The Cancel button (CancelResting/CancelWorking) is
                // STATE-ONLY — clicked against an advanced activity it skips the whole
                // physical teardown (employee brain left on the player character,
                // station occupied, blocker armed: round-195, field 20260730-194909
                // 'stuck in cashier', 64s+ frozen). Only a still-NotStarted activity
                // may use Cancel; anything further along gets the game's own full
                // teardown, Finish() (identical to the Stop button).
                var act = _curAct;
                int liveState = -1;
                try
                {
                    if (act != null)
                    {
                        var sm = act.GetType().GetMethod("GetState");
                        if (sm != null) liveState = Convert.ToInt32(sm.Invoke(act, null));
                    }
                }
                catch { }

                if (liveState == (int)PlayerActivityState.NotStarted && CancelButtonIndex >= 0)
                    InvokeDockButton(CancelButtonIndex);
                else if (act != null)
                {
                    // Mono: the object IS its concrete type — call Finish directly.
                    act.GetType().GetMethod("Finish")?.Invoke(act, null);
                    Plugin.Logger.LogInfo($"[Rest] StandUp via Finish() (live state {liveState}).");
                }
                else if (CancelButtonIndex >= 0) InvokeDockButton(CancelButtonIndex);   // no activity readable — legacy path

                // NO force-clearing of the UI's activity slot here: the movement
                // lock it tried to fix was actually the input-suppression latch
                // (fixed at the source), and nulling the slot out-of-band skips
                // the game's natural teardown — which is what frees the SEAT
                // (bench became unusable after standing, 2026-06-10).  Let the
                // UI's own Update see the finished state and tear down properly.
                _curAct = null;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] StandUp: {ex.Message}"); }
        }

        public static void InvokeDockButton(int index)
        {
            try
            {
                if (index < 0 || index >= DockButtons.Count) return;
                var oc = DockButtons[index].OnClick;
                if (oc == null) return;
                oc.GetType().GetMethod("Invoke", Type.EmptyTypes)?.Invoke(oc, null);
                Plugin.Logger.LogInfo($"[Rest] dock button '{DockButtons[index].Label}' invoked.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] InvokeDockButton: {ex.Message}"); }
        }

        /// <summary>The current activity's remaining minutes (0 if unknown).
        /// Uses the activity cached by the last seated-poll (no scene walks).</summary>
        public static int RemainingActivityMinutes()
        {
            try
            {
                var act = _curAct;
                if (act == null) return 0;
                var m = act.GetType().GetMethod("GetRemainingMinutesForTimeMachine");
                return m != null ? Math.Max(0, Convert.ToInt32(m.Invoke(act, null))) : 0;
            }
            catch { return 0; }
        }

        /// <summary>Make sure the activity itself lasts at least until the goal,
        /// so the game can't auto-stand the player mid-vote or mid-skip.
        /// WRITES THE ACTIVITY'S OWN DURATION FIELD (the *_minutesTo*** int) —
        /// the slider API (ChangeSliderValue) silently no-ops with the native
        /// panel hidden, which is why players kept auto-standing and skips
        /// self-cancelled the moment they started.</summary>
        public static void EnsureActivityCovers(double goalMinutes)
        {
            try
            {
                var act = _curAct;
                if (act == null) return;
                double need = goalMinutes - NowMinutes();
                int rem = RemainingActivityMinutes();
                if (need <= rem + 1) return;

                // Mono: GetType() already yields the concrete activity class
                // (the IL2CPP interface-wrapper downcast is gone).
                object target = act;
                var t = act.GetType();

                if (!_durProps.TryGetValue(t, out var member))
                {
                    member = null;
                    const System.Reflection.BindingFlags bf =
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    foreach (var p in t.GetProperties(bf))
                        if (p.PropertyType == typeof(int) && p.CanRead && p.CanWrite
                            && p.Name.IndexOf("minutesTo", StringComparison.OrdinalIgnoreCase) >= 0)
                        { member = p; break; }
                    // EA 0.11 (Mono): the durations are private int FIELDS
                    // (_minutesToRest/_minutesToWork/_minutesToSleep) — the
                    // property-only scan logged "NOT FOUND" and silently
                    // no-opped, so the native duration expired the moment the
                    // skip raced the clock: auto-stand → vote drop → skip
                    // cancelled (the recurring bench bug, user 2026-06-12).
                    if (member == null)
                        foreach (var f in t.GetFields(bf))
                            if (f.FieldType == typeof(int)
                                && f.Name.IndexOf("minutesTo", StringComparison.OrdinalIgnoreCase) >= 0)
                            { member = f; break; }
                    _durProps[t] = member;
                    Plugin.Logger.LogInfo($"[Rest] duration field for {t.Name}: {(member != null ? member.Name : "NOT FOUND")}");
                }
                if (member == null) return;
                int total = Convert.ToInt32(MPReflect.Get(member, target) ?? 0);
                int delta = (int)Math.Ceiling(need - rem);
                MPReflect.Set(member, target, total + delta);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] EnsureActivityCovers: {ex.Message}"); }
        }
        private static readonly Dictionary<Type, System.Reflection.MemberInfo?> _durProps = new();

        /// <summary>All session player names (for the who-voted checklist).</summary>
        public static IReadOnlyList<string> AllPlayers()
            => MPServer.IsRunning ? MPServer.LobbyPlayers
             : MPClient.IsConnected ? MPClient.LobbyPlayers : new List<string>();

        public static bool HasVote(string playerId, out double goal)
        {
            foreach (var v in Votes)
                if (v.PlayerId == playerId) { goal = v.GoalMinutes; return true; }
            goal = 0; return false;
        }

        // ── Per-frame tick (main thread, MP active + in game) ─────────────────
        public static void Tick()
        {
            // Taxi instant arrival runs at FRAME cadence (the 0.3s beat matters):
            // stop the ride's machine — its end handler teleports the player;
            // the clock never moved.
            if (_taxiStopAt > 0f && Time.unscaledTime >= _taxiStopAt)
            {
                _taxiStopAt = 0f;
                _taxiPendingUntil = 0f;
                StopLocalMachine();
                SetMachineCanvasVisible(true);   // restore for future rest skips
                Plugin.Logger.LogInfo("[Taxi] instant arrival — machine stopped, no time cost.");
            }

            if (Time.unscaledTime < _nextPollAt) return;
            _nextPollAt = Time.unscaledTime + 0.5f;

            // Watchdog: nothing may freeze time outside our explicit systems.
            // (Not while a taxi ride is mid-handoff — the ride machine briefly
            // owns the pause state.)
            if (!TimeSync.ManualPaused && !TimeSync.IsStartupHeld && !TaxiRidePending)
                GameStateReader.EnsureTimeNotLocked();

            // Seated state from the game's activity system.
            UpdateSeated();

            // Self-healing nav watchdog (round-73 — REPLACES the old diagnostic, which was
            // BLIND in MP: its signal, PlayerActivityUI.HasNavigationDisabled (:157), only
            // reports while IsPanelOpen — and Rest v5 force-hides that panel, so it said
            // "fine" straight through two field strandings (Baydos 20260723-165848 + our
            // live repro). Read the PlayerController's blocker set directly instead.
            //
            // ROOT CAUSE (confirmed live 2026-07-24, NavProbe): the native activity arms its
            // navigation blocker from a DEFERRED lambda (RestActivity.<StartResting>b__15_1,
            // fired later by PlayerController.Update). A fast exit — which our dock's
            // auto-start + instant cancel / movement-key hatch makes one frame wide — races
            // it in either order and strands the lock: navDisabled forever, avatar stuck
            // seated on every machine, dock + hatch gated off by the same desync, chair
            // owned by the half-dead activity. Vanilla can't reach the race (its panel flow
            // makes the earliest exit seconds late).
            //
            // The heal: out of ANY activity for a SUSTAINED 2s (Seated covers all nine
            // IPlayerActivity kinds) but an ACTIVITY-class blocker still held → release it
            // via the game's own unset call, loudly. Non-activity blockers (Map, Vehicle,
            // PlacementMode, …) belong to other systems and are never touched.
            if (Seated || SkipActive)
            {
                _notSeatedSince = Time.unscaledTime;
                if (_foreignHeldSince.Count > 0) _foreignHeldSince.Clear();   // round-90: activities hold keys legitimately
            }
            else if ((MPServer.IsRunning || MPClient.IsClientInWorld)
                     && Time.unscaledTime >= _navHealNext
                     && Time.unscaledTime - _notSeatedSince >= 2f)
            {
                _navHealNext = Time.unscaledTime + 2f;
                try
                {
                    var pc = InstanceBehavior<GameManager>.Instance?.playerController;
                    if (pc != null)
                    {
                        // 1. Blocker layer: release stranded ACTIVITY-class keys; note any FOREIGN key
                        //    (another system's lock) — its presence means hands off the physical layer.
                        bool foreignHeld = false;
                        _navBlockerSetField ??= HarmonyLib.AccessTools.Field(typeof(PlayerController), "_activeNavigationBlockers");
                        if (_navBlockerSetField?.GetValue(pc) is System.Collections.IEnumerable held)
                        {
                            List<NavigationBlocker>? stranded = null;
                            List<NavigationBlocker>? foreignNow = null;
                            foreach (var b in held)
                            {
                                if (b is not NavigationBlocker key) continue;
                                if (System.Array.IndexOf(ActivityBlockers, key) >= 0)
                                    (stranded ??= new List<NavigationBlocker>()).Add(key);
                                else { foreignHeld = true; (foreignNow ??= new List<NavigationBlocker>()).Add(key); }
                            }
                            // Round-90 (user-approved) FOREIGN-BLOCKER WATCH — log-only, never heals.
                            // ANY held blocker silently no-ops the building exit trigger
                            // (ExitZoneDespawner:18) AND freezes WASD (PlayerController:241-247), and
                            // the heal below deliberately never touches non-activity keys — so a
                            // stranded Map/Placement/DeliveryJob/... lock was the last "stuck in the
                            // building" shape no instrument could see (field 20260725-000353 class).
                            // Name it after 30s of persistence, re-warn each 60s.
                            try
                            {
                                float nowT = Time.unscaledTime;
                                if (foreignNow == null)
                                {
                                    if (_foreignHeldSince.Count > 0) _foreignHeldSince.Clear();
                                }
                                else
                                {
                                    foreach (var k in new List<NavigationBlocker>(_foreignHeldSince.Keys))
                                        if (!foreignNow.Contains(k)) _foreignHeldSince.Remove(k);
                                    foreach (var k in foreignNow)
                                        if (!_foreignHeldSince.ContainsKey(k)) _foreignHeldSince[k] = nowT;
                                    if (nowT >= _foreignWarnNext)
                                        foreach (var kv in _foreignHeldSince)
                                            if (nowT - kv.Value >= 30f)
                                            {
                                                _foreignWarnNext = nowT + 60f;
                                                Plugin.Logger.LogWarning(
                                                    $"[Rest] FOREIGN BLOCKER HELD (round-90, log-only): '{kv.Key}' for {nowT - kv.Value:F0}s outside any activity — building exits silently dead and WASD frozen while it persists. No action taken.");
                                                break;
                                            }

                                    // Round-198 (field 20260730-221621): the two TRANSIT keys upgrade from
                                    // log-only to HEAL under provably-safe conditions — a dead map-close
                                    // coroutine (null POI, or any other mid-close death) strands 'Map'
                                    // (and, via the subway ride waiting on that close, 'Subway' + the
                                    // riding flag + the pause) FOREVER; nothing legitimately holds Map
                                    // with the map closed for 30s, and no real ride lasts 2 minutes.
                                    // The heal mirrors what the dead completion would have done — never
                                    // warps, never touches a pushed cart.
                                    if (_foreignHeldSince.TryGetValue(NavigationBlocker.Map, out var mapSince)
                                        && nowT - mapSince >= 30f && !CityMap.IsOpen)
                                    {
                                        pc.UnsetNavigationBlocker(NavigationBlocker.Map);
                                        _foreignHeldSince.Remove(NavigationBlocker.Map);
                                        Plugin.Logger.LogWarning("[Rest] TRANSIT HEAL: released stranded 'Map' blocker (held 30s+ with the map closed — a dead map-close skipped its release; round-198). Movement restored.");
                                    }
                                    if (_foreignHeldSince.TryGetValue(NavigationBlocker.Subway, out var subSince)
                                        && nowT - subSince >= 120f)
                                    {
                                        pc.UnsetNavigationBlocker(NavigationBlocker.Subway);
                                        _foreignHeldSince.Remove(NavigationBlocker.Subway);
                                        try { pc.Character?.ToggleVisibility(show: true); } catch { }
                                        try { HarmonyLib.AccessTools.PropertySetter(typeof(SubwaySystem), "IsRiding")?.Invoke(null, new object[] { false }); } catch { }
                                        Plugin.Logger.LogWarning("[Rest] TRANSIT HEAL: released stranded 'Subway' blocker (held 120s+ — the ride's completion never ran; visibility restored, riding flag cleared; round-198). Movement restored.");
                                    }
                                }
                            }
                            catch { }
                            if (stranded != null)
                                foreach (var key in stranded)
                                {
                                    pc.UnsetNavigationBlocker(key);
                                    Plugin.Logger.LogWarning($"[Rest] NAV HEAL: released stranded '{key}' blocker (no activity for 2s — deferred-set race, round-73). Movement restored.");
                                }
                        }

                        // 2. Physical layer (round-73b, live repro: blocker healed but the avatar stayed
                        //    seated — could spin, not walk): the raced CancelResting skips the ENTIRE
                        //    physical teardown that Finish() runs — SitOnChair left the nav agent disabled
                        //    (updatePosition/Rotation off, enabled=false), the rigidbody kinematic, and the
                        //    seat claimed. ThirdPersonCharacter.Reset() (:771) is the game's own catch-all
                        //    restore (re-enables the agent, releases the seat THROUGH the seat's callback,
                        //    clears the anchor + kinematic state) and is what every proper teardown calls.
                        //    STRICT ownership guard: never while ANY foreign blocker is held (vehicle,
                        //    casino, map, placement, sequences — those systems legitimately pin the
                        //    character) and never while using a vehicle.
                        if (!foreignHeld && !Helpers.PlayerHelper.IsUsingVehicle)
                        {
                            var ch = pc.Character;
                            if (ch != null)
                            {
                                bool agentOff = false, seatHeld = false, posOff = false, kinematic = false;
                                try
                                {
                                    _navAgentField ??= HarmonyLib.AccessTools.Field(ch.GetType(), "navmeshAgent");
                                    if (_navAgentField?.GetValue(ch) is UnityEngine.AI.NavMeshAgent ag)
                                    {
                                        agentOff = !ag.enabled;
                                        posOff = ag.enabled && !ag.updatePosition;   // round-195: ForceToTransform leaves this off
                                    }
                                }
                                catch { }
                                try
                                {
                                    _sittingOnField ??= HarmonyLib.AccessTools.Field(ch.GetType(), "isSittingOn");
                                    seatHeld = _sittingOnField?.GetValue(ch) is UnityEngine.Object o && o != null;
                                }
                                catch { }
                                try
                                {
                                    _charRbField ??= HarmonyLib.AccessTools.Field(ch.GetType(), "characterRigidbody");
                                    if (_charRbField?.GetValue(ch) is Rigidbody rb) kinematic = rb.isKinematic;
                                }
                                catch { }
                                // Round-195 (field 20260730-194909 'stuck in cashier'): a raced
                                // CancelWorking leaves the STATION's Employee brain component ON the
                                // player's character (WorkActivity Started assigns the player as the
                                // station's employee; only Finish() unassigns). Outside any activity
                                // that component is always residue — the game never staffs the player
                                // without a WorkActivity in the slot. Release through the game's own
                                // UnassignEmployee (destroys the component player-safely, restores
                                // appearance/hands, frees the station) exactly as Finish() would.
                                Employee emp = null;
                                try { emp = ch.GetComponent<Employee>(); } catch { }
                                if (agentOff || seatHeld || posOff || kinematic || emp != null)
                                {
                                    if (emp != null)
                                    {
                                        try
                                        {
                                            var st = emp.employeeStationController;
                                            if (st != null && ReferenceEquals(st.employee, emp)) st.UnassignEmployee();
                                            else UnityEngine.Object.Destroy(emp);   // station lost the back-ref — remove the brain directly
                                        }
                                        catch { }
                                        try { Helpers.EnergyHelper.RemoveEnergySpender("work"); } catch { }
                                    }
                                    ch.Reset();
                                    Plugin.Logger.LogWarning($"[Rest] NAV HEAL: physical restore via Character.Reset() (agentOff={agentOff} seatHeld={seatHeld} posOff={posOff} kinematic={kinematic} employeeResidue={emp != null}) — a raced cancel skipped the native teardown (round-73b/195).");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] nav heal: {ex.Message}"); }
            }

            // Round-83 (user-approved): SILENT-CLASS TRIPWIRE — log-only, never heals, never
            // touches behavior. The round-73 heal above deliberately skips players using a
            // vehicle, so a player stranded WHILE attached to a hand truck/flatbed (KILOKEN
            // 20260724-170745: own loaded flatbed, map-taxi mid-push, auto-entry at own shop,
            // frozen at the door — the ring held 91s of NOTHING) leaves zero log evidence.
            // While pushing, the character is agent-driven: agent enabled + updatePosition on
            // + non-kinematic is the only healthy steady state; door transitions break it only
            // transiently. Any of them wrong for 5s+ → ONE warn naming the exact broken
            // component (re-warned each 60s while it persists), so the next field report of
            // this class names its own cause.
            try
            {
                HandTruck? cart = null;
                if (MPServer.IsRunning || MPClient.IsClientInWorld)
                    try { cart = Helpers.VehicleHelper.GetCurrentVehicleBase() as HandTruck; } catch { }
                if (cart == null) _cartBadSince = -1f;
                else
                {
                    var chC = InstanceBehavior<GameManager>.Instance?.playerController?.Character;
                    bool cAgentOff = false, cPosOff = false, cKinematic = false;
                    if (chC != null)
                    {
                        _navAgentField ??= HarmonyLib.AccessTools.Field(chC.GetType(), "navmeshAgent");
                        if (_navAgentField?.GetValue(chC) is UnityEngine.AI.NavMeshAgent cAg)
                        { cAgentOff = !cAg.enabled; cPosOff = cAg.enabled && !cAg.updatePosition; }
                        cKinematic = chC.isKinematic;
                    }
                    if (!(cAgentOff || cPosOff || cKinematic)) _cartBadSince = -1f;
                    else
                    {
                        if (_cartBadSince < 0f) { _cartBadSince = Time.unscaledTime; _cartWarnNext = 0f; }
                        if (Time.unscaledTime - _cartBadSince >= 5f && Time.unscaledTime >= _cartWarnNext)
                        {
                            _cartWarnNext = Time.unscaledTime + 60f;
                            string vType = "?", vId = "", vMine = "?";
                            try
                            {
                                vType = cart.vehicleInstance?.vehicleTypeName ?? cart.name;
                                vId   = cart.vehicleInstance?.id ?? "";
                                vMine = (cart.vehicleInstance != null
                                         && SaveGameManager.Current?.VehicleInstances?.Contains(cart.vehicleInstance) == true)
                                        ? "True" : "False";
                            }
                            catch { }
                            string blockers = "";
                            try
                            {
                                var pcC = InstanceBehavior<GameManager>.Instance?.playerController;
                                _navBlockerSetField ??= HarmonyLib.AccessTools.Field(typeof(PlayerController), "_activeNavigationBlockers");
                                if (pcC != null && _navBlockerSetField?.GetValue(pcC) is System.Collections.IEnumerable heldC)
                                    foreach (var b in heldC) blockers += (blockers.Length > 0 ? "," : "") + b;
                            }
                            catch { }
                            Plugin.Logger.LogWarning(
                                $"[Rest] CART STUCK (round-83, log-only): pushing '{vType}' id='{vId}' mine={vMine} — "
                                + $"agentOff={cAgentOff} updatePosOff={cPosOff} kinematic={cKinematic} blockers=[{blockers}] "
                                + $"held {Time.unscaledTime - _cartBadSince:F0}s. No action taken.");
                        }
                    }
                }
            }
            catch { }

            // Sitting is INDEFINITE: the game's default duration (30 min) was
            // auto-standing players while they pondered the dock ("the window
            // auto-closed").  Top the activity up so only X / walking ends it.
            if (Seated)
            {
                double need = _localVoteActive ? Math.Max(30, _localGoal - NowMinutes()) : 30;
                EnsureActivityCovers(NowMinutes() + need + 10);
            }

            // Vote lifecycle: standing up (or losing the activity) drops it.
            if (_localVoteActive)
            {
                if (!Seated)
                {
                    _localVoteActive = false;
                    SendVote(false, 0, "");
                    Plugin.Logger.LogInfo("[Rest] vote OFF (stood up).");
                }
                else if (!SkipActive && NowMinutes() >= _localGoal - 0.1)
                {
                    _localVoteActive = false;
                    SendVote(false, 0, "");
                    Plugin.Logger.LogInfo("[Rest] vote OFF (goal time reached) — standing up.");
                    StandUp();   // wake at the chosen time, like vanilla — movement restored
                }
            }

            if (MPServer.IsRunning) HostTick();
        }

        private static void UpdateSeated()
        {
            try
            {
                var (act, nm) = GetCurrentActivity();
                // ANY PlayerActivityUI activity is ours — the vanilla panel is dead, so we replace it for
                // every activity (Rest/Sleep/Work/Workout/Hygiene/Entertain/Study/Swimming/Paid). The taxi
                // is NOT an IPlayerActivity, so it never reaches here — there is nothing to exclude.
                bool seated = act != null;
                if (seated != Seated)
                    Plugin.Logger.LogInfo($"[Rest] seated → {seated}{(seated ? $" ({nm})" : "")}");
                Seated = seated;
                _curAct = seated ? act : null;
                _curActRef = _curAct;   // Mono: object identity replaces pointer identity
                if (!seated) _suppressedActRef = null;   // gone — clear the wedge guard
                ActivityName = seated ? nm : "";
                ActivityState = -1;
                DockButtons.Clear();
                if (!seated) return;

                // (Instant-study removed 2026-06-12: superseded by the honorary-
                //  degree dialog — the school door now opens a confirm GUI and
                //  the StudyActivity never starts in MP.)

                // State + button passthrough for the dock.
                try
                {
                    var sm = act!.GetType().GetMethod("GetState");
                    if (sm != null) ActivityState = Convert.ToInt32(sm.Invoke(act, null));
                }
                catch { }

                // Round-194 (field 20260729-220133): a Finished activity stranded in the
                // slot while the panel GameObject is INACTIVE is a permanent wedge — the
                // game's teardown (OnActivityFinished: slot clear, mouse reset, time-control
                // restore) runs ONLY from the panel's own Update, and the native walk-away
                // cancel (CancelActivityMovement) deactivates the panel while KEEPING the
                // slot. Any button-invoke that lands Finished in that state (our dock's
                // CancelRest did) locks out every activity start and vehicle entry.
                // Reactivate the panel so the game runs its OWN teardown — it deactivates
                // itself again in the same pass. No out-of-band slot clearing (2026-06-10:
                // that skips the seat release and bricks the bench).
                if (ActivityState == (int)PlayerActivityState.Finished
                    && (MPServer.IsRunning || MPClient.IsClientInWorld))
                {
                    try
                    {
                        var (ui, _) = GetActivityUiCached();
                        if (ui is MonoBehaviour mb && !mb.gameObject.activeSelf)
                        {
                            mb.gameObject.SetActive(true);
                            Plugin.Logger.LogWarning("[Rest] WEDGE HEAL: Finished activity stranded with panel inactive — panel reactivated so the game's own teardown runs (round-194).");
                        }
                    }
                    catch { }
                }
                try
                {
                    var gb = act!.GetType().GetMethod("GetButtons");
                    if (gb?.Invoke(act, null) is System.Collections.IEnumerable arr)
                    {
                        foreach (var b in arr)
                        {
                            if (b == null || DockButtons.Count >= 4) continue;
                            var bt = b.GetType();
                            bool inter = true;
                            try { inter = (bool)(MPReflect.Get(bt, b, "interactable") ?? true); } catch { }
                            if (!inter) continue;
                            string label = "";
                            try { label = MPReflect.Get(bt, b, "name") as string ?? ""; } catch { }
                            if (string.IsNullOrEmpty(label))
                            {
                                try { label = MPReflect.Get(bt, b, "key") as string ?? ""; } catch { }
                                if (label.Contains('.')) label = label.Substring(label.LastIndexOf('.') + 1);
                            }
                            object? oc = null;
                            try { oc = MPReflect.Get(bt, b, "onClick"); } catch { }
                            DockButtons.Add(new DockButton { Label = string.IsNullOrEmpty(label) ? "Action" : label, OnClick = oc });
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] buttons read: {ex.Message}"); }

                // Classify: Start is AUTO-pressed (click bench → character sits,
                // no redundant button); Stop/Cancel renders as the header X.
                CancelButtonIndex = -1;
                int startIdx = -1;
                for (int i = 0; i < DockButtons.Count; i++)
                {
                    string l = DockButtons[i].Label.ToLowerInvariant();
                    if (startIdx < 0 && l.Contains("start")) startIdx = i;
                    else if (CancelButtonIndex < 0 && (l.Contains("stop") || l.Contains("cancel"))) CancelButtonIndex = i;
                }
                // Auto-press Start so clicking a bench walks you over and sits — no redundant native Start
                // button. Fire ONCE per activity INSTANCE (tracked by ref), IMMEDIATELY: the old 1.5s
                // re-fire gate was a time-based stand-in for "don't press the same activity twice" and it
                // stalled every click. The post-StandUp window still guards the re-sit, but ONLY for the
                // instance we stood up from — so a NEW bench starts walking instantly (user 2026-06-22).
                bool alreadyAutoStarted = ReferenceEquals(_curActRef, _lastAutoStartedActRef);
                bool suppressedNow = ReferenceEquals(_curActRef, _suppressedActRef) && Time.unscaledTime < _suppressAutoStartUntil;
                if (startIdx >= 0 && !alreadyAutoStarted && !suppressedNow)
                {
                    _lastAutoStartedActRef = _curActRef;
                    InvokeDockButton(startIdx);
                    Plugin.Logger.LogInfo("[Rest] auto-start invoked — sit immediately, no Start button.");
                }
            }
            catch { }
        }

        // ── Host: consensus + clock executor ─────────────────────────────────
        public static void HostHandleVote(RestVotePayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.PlayerId)) return;
            if (p.Active)
                _hostVotes[p.PlayerId] = new RestVoteEntry { PlayerId = p.PlayerId, GoalMinutes = p.GoalMinutes, Activity = p.Activity };
            else
                _hostVotes.Remove(p.PlayerId);
            HostBroadcastState();
        }

        private static void HostTick()
        {
            int required = MPServer.LobbyPlayers?.Count ?? 1;
            bool consensus = required > 0 && _hostVotes.Count >= required;

            if (!SkipActive && consensus)
            {
                _skipGoalMinutes = double.MaxValue;
                foreach (var v in _hostVotes.Values)
                    if (v.GoalMinutes < _skipGoalMinutes) _skipGoalMinutes = v.GoalMinutes;
                SkipActive = true;
                Plugin.Logger.LogInfo($"[Rest] CONSENSUS ({_hostVotes.Count}/{required}) — skipping to {Fmt(_skipGoalMinutes)}.");
                HostBroadcastState();
            }
            else if (SkipActive && !consensus)
            {
                SkipActive = false;
                Plugin.Logger.LogInfo("[Rest] skip STOPPED (a vote dropped).");
                HostBroadcastState();
            }

            if (SkipActive)
            {
                double now = NowMinutes();
                if (now >= _skipGoalMinutes)
                {
                    SkipActive = false;
                    Plugin.Logger.LogInfo($"[Rest] skip GOAL reached ({Fmt(_skipGoalMinutes)}).");
                    HostBroadcastState();
                    return;
                }
                // The executor runs at frame rate, not at this 0.5s poll.
            }
        }

        /// <summary>Per-frame skip executor — runs on HOST and CLIENTS while a consensus skip is active.
        /// Instead of WRITING the clock forward (which silently bypassed the game's per-hour/per-day economy
        /// tick → no business revenue for the skipped time), this DRIVES the game's own RunMainGameTick fast,
        /// so the skipped hours are actually simulated on every machine — each owner earns their own income
        /// locally (cash is self-reported per machine). The shared world stays consistent via the existing
        /// client-suppressions + the post-skip re-broadcast, exactly as in normal (un-skipped) play.
        /// Called every frame from MPCanvasUI for smoothness; Tick() itself is throttled to 0.5s.</summary>
        public static void TickSkipFrame()
        {
            if (!SkipActive) return;
            double now = NowMinutes();
            if (now >= _skipGoalMinutes) return;   // reached the goal; HostTick / the state broadcast closes it out
            double advance = Math.Min(_skipGoalMinutes - now,
                                      Math.Min(SkipMinutesPerRealSecond * Time.unscaledDeltaTime, MaxSkipMinutesPerFrame));
            if (advance <= 0d) return;
            var gm = InstanceBehavior<GameManager>.Instance;
            if (gm != null) gm.RunMainGameTick((float)advance);   // advances the clock AND runs the skipped economy
        }

        private static void HostBroadcastState()
        {
            var st = new RestSkipStatePayload { Required = MPServer.LobbyPlayers?.Count ?? 1, SkipActive = SkipActive, GoalMinutes = _skipGoalMinutes };
            foreach (var v in _hostVotes.Values) st.Votes.Add(v);
            ApplyState(st);
            MPServer.BroadcastRestState(st);
        }

        /// <summary>Host: a player disconnected — drop their time-skip vote so consensus is re-evaluated
        /// against who's actually present (a lingering ghost vote would keep a skip running, and a lone
        /// remaining player couldn't stop it). HostTick re-checks consensus on the next tick.</summary>
        public static void RemovePlayer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            if (_hostVotes.Remove(playerId)) HostBroadcastState();
        }

        public static void ApplyState(RestSkipStatePayload? st)
        {
            if (st == null) return;
            Votes.Clear();
            Votes.AddRange(st.Votes);
            RequiredVotes = st.Required;
            if (!MPServer.IsRunning)
            {
                SkipActive       = st.SkipActive;
                _skipGoalMinutes = st.GoalMinutes;   // clients need the goal to fast-run their own sim to it
            }
        }

        // ── Plumbing ──────────────────────────────────────────────────────────
        private static void SendVote(bool active, double goalMinutes, string activity)
        {
            var p = new RestVotePayload { PlayerId = MPConfig.PlayerId, Active = active, GoalMinutes = goalMinutes, Activity = activity };
            if (MPServer.IsRunning) HostHandleVote(p);
            else if (MPClient.IsConnected) MPClient.SendRestVote(p);
        }

        // The live PlayerActivityUI + current activity.  Read the instance FRESH every call from the plain
        // UIs.playerActivityUI field (cheap) — caching the instance let a stale wrapper report
        // GetCurrentActivity==null while the game's current panel had a live activity, which made the
        // vanilla panel leak and the dock never appear (2026-06-22).  Only the Type is cached.
        private static Type?   _uiType;
        private static object? _curAct;

        private static (object? ui, Type? type) GetActivityUiCached()
        {
            try
            {
                var ui = UI.UIs.Instance?.playerActivityUI;
                if (ui == null) return (null, null);
                _uiType ??= ui.GetType();
                return (ui, _uiType);
            }
            catch { return (null, null); }
        }

        /// <summary>Current player activity short name ("" = none).  The game
        /// strips "Activity": working a station reads as "Work" — drives the
        /// register-duty broadcast (MPRegisterSync.TickDuty).</summary>
        public static string CurrentActivityName()
        {
            var (_, nm) = GetCurrentActivity();
            return nm;
        }

        private static (object? act, string name) GetCurrentActivity()
        {
            try
            {
                var (ui, uiType) = GetActivityUiCached();
                if (ui == null) return (null, "");
                var act = uiType!.GetProperty("GetCurrentActivity")?.GetValue(ui);
                if (act == null) return (null, "");
                string nm;
                try
                {
                    var io = act;
                    nm = io?.GetType()?.Name?.Replace("Activity", "") ?? "Rest";
                }
                catch { nm = "Rest"; }
                return (act, nm);
            }
            catch { return (null, ""); }
        }

        // ── Native TimeMachine helpers (neutralizer only) ─────────────────────
        private static object? _machine;
        private static Type?   _machineType;

        private static object? GetMachine()
        {
            try
            {
                _machineType ??= VehicleManager.FindGameType("Timemachine.TimeMachine")
                              ?? VehicleManager.FindGameType("TimeMachine");
                if (_machineType == null) return null;
                if (_machine == null)
                {
                    var objs = UnityEngine.Object.FindObjectsOfType(_machineType, true);
                    if (objs == null || objs.Length == 0) return null;
                    _machine = objs[0];   // Mono: the found object IS the typed instance
                }
                return _machine;
            }
            catch { return null; }
        }

        private static void StopLocalMachine()
        {
            try
            {
                var m = GetMachine();
                var mm = _machineType?.GetMethod("StopTimeMachine");
                if (m != null && mm != null) mm.Invoke(m, new object[] { 0f });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] StopLocalMachine: {ex.Message}"); }
        }

        /// <summary>Hide/show the native skip overlay (taxi rides hide it: its
        /// frozen clock and wrong day are misleading; restored after).</summary>
        private static void SetMachineCanvasVisible(bool visible)
        {
            try
            {
                var m = GetMachine();
                var canvas = MPReflect.Get(_machineType, m, "canvas") as Canvas;
                if (canvas != null) canvas.enabled = visible;
            }
            catch { }
        }

        public static string Fmt(double totalMinutes)
        {
            int d = (int)(totalMinutes / 1440.0);
            double rem = totalMinutes - d * 1440.0;
            int hh = (int)(rem / 60.0);
            int mm = (int)(rem - hh * 60.0);
            return $"Day {d} · {hh:D2}:{mm:D2}";
        }

        /// <summary>Day and time as separate strings (for clear UI display).</summary>
        public static (string day, string time) FmtParts(double totalMinutes)
        {
            int d = (int)(totalMinutes / 1440.0);
            double rem = totalMinutes - d * 1440.0;
            int hh = (int)(rem / 60.0);
            int mm = (int)(rem - hh * 60.0);
            return ($"Day {d}", $"{hh:D2}:{mm:D2}");
        }

        /// <summary>The NEXT occurrence of a clock time (today if still ahead,
        /// else tomorrow) as total game-minutes.</summary>
        public static double NextOccurrence(int hour, int minute = 0)
        {
            double now = NowMinutes();
            int day = (int)(now / 1440.0);
            double candidate = day * 1440.0 + hour * 60.0 + minute;
            if (candidate <= now + 1) candidate += 1440.0;
            return candidate;
        }
    }
}
