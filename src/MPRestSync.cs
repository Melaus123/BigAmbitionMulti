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

        // Activities OUR system manages.  Anything else (the TAXI is an
        // activity too — auto-pressing its Start mid-destination-pick crashed
        // the game) is invisible to us and stays fully native.
        private static readonly string[] RestClassNames =
            { "Rest", "Sleep", "Work", "Workout", "Hygiene", "Entertain", "Swimming", "Study" };

        public static bool IsRestClassName(string name)
        {
            foreach (var r in RestClassNames)
                if (string.Equals(name, r, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Live check for the HidePanel patch: is the CURRENT activity
        /// one of ours?  Uses the cached UI wrapper (no scene walk).</summary>
        public static bool IsCurrentActivityRestClass()
        {
            try
            {
                var (act, nm) = GetCurrentActivity();
                return act != null && IsRestClassName(nm);
            }
            catch { return false; }
        }

        // ── Local state ───────────────────────────────────────────────────────
        public static bool   Seated       { get; private set; }

        /// <summary>What the DOCK should follow: after StandUp, the same
        /// activity instance is suppressed — a half-cancelled approach (click
        /// bench, walk away) left Seated wedged true and the dock stuck open
        /// with no X (user, 2026-06-11).  A NEW activity un-suppresses.</summary>
        public static bool SeatedForUi => Seated && !ReferenceEquals(_curActRef, _suppressedActRef);
        private static object? _suppressedActRef;
        private static object? _curActRef;

        public static string ActivityName { get; private set; } = "";
        public static int    ActivityState { get; private set; } = -1;   // PlayerActivityState; -1 = none
        private static bool   _localVoteActive;
        private static double _localGoal;
        private static float  _nextPollAt;

        public static bool LocalVoteActive => _localVoteActive;
        public static double LocalGoal     => _localGoal;

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
        private static float _lastAutoStartAt;

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
            if (!Seated) return;
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
        private static string _lastForeignActivity = "";

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

                if (CancelButtonIndex >= 0) InvokeDockButton(CancelButtonIndex);
                else
                {
                    var act = _curAct;
                    // Mono: the object IS its concrete type — call Finish directly.
                    if (act != null)
                    {
                        act.GetType().GetMethod("Finish")?.Invoke(act, null);
                        Plugin.Logger.LogInfo("[Rest] StandUp via Finish() fallback.");
                    }
                }

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
        public static List<string> AllPlayers()
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

            // Self-healing nav watchdog: NOT seated but navigation disabled =
            // a lingering activity state (the lock the user kept hitting).
            // Name the guilty flag and force the close-out again.
            if (!Seated && Time.unscaledTime >= _navHealNext)
            {
                _navHealNext = Time.unscaledTime + 2f;
                try
                {
                    var (ui, uiType) = GetActivityUiCached();
                    if (ui != null && uiType != null)
                    {
                        bool nav = (bool)(uiType.GetMethod("HasNavigationDisabled", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null) ?? false);
                        if (nav)
                        {
                            bool waiting = (bool)(uiType.GetProperty("IsWaiting", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) ?? false);
                            bool panel   = (bool)(uiType.GetProperty("IsPanelOpen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) ?? false);
                            bool moving  = (bool)(uiType.GetProperty("IsMovingTowardsActivity", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) ?? false);
                            // Diagnostic only — slot force-nulling breaks the
                            // seat's release (bench unusable); if this ever
                            // fires we fix the real cause instead.
                            Plugin.Logger.LogWarning($"[Rest] NAV LOCK while not seated (IsWaiting={waiting} IsPanelOpen={panel} IsMoving={moving}).");
                        }
                    }
                }
                catch { }
            }

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
                // NON-rest activities (taxi!) are none of our business.
                bool seated = act != null && IsRestClassName(nm);
                if (act != null && !IsRestClassName(nm) && nm != _lastForeignActivity)
                {
                    _lastForeignActivity = nm;
                    Plugin.Logger.LogInfo($"[Rest] foreign activity '{nm}' — fully native, ignored.");
                }
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

                // State + button passthrough for the dock.
                try
                {
                    var sm = act!.GetType().GetMethod("GetState");
                    if (sm != null) ActivityState = Convert.ToInt32(sm.Invoke(act, null));
                }
                catch { }
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
                if (startIdx >= 0 && Time.unscaledTime - _lastAutoStartAt > 1.5f
                    && Time.unscaledTime >= _suppressAutoStartUntil)
                {
                    _lastAutoStartAt = Time.unscaledTime;
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

        /// <summary>Host clock executor — called every frame from MPCanvasUI so
        /// the skip is smooth (Tick() itself is throttled to 0.5s).</summary>
        public static void HostSkipFrame()
        {
            if (!MPServer.IsRunning || !SkipActive) return;
            double now = NowMinutes();
            if (now >= _skipGoalMinutes) return;   // HostTick will close it out
            double next = Math.Min(now + SkipMinutesPerRealSecond * Time.unscaledDeltaTime, _skipGoalMinutes);
            MPCanvasUI.WriteWorldClockMinutes(next);
        }

        private static void HostBroadcastState()
        {
            var st = new RestSkipStatePayload { Required = MPServer.LobbyPlayers?.Count ?? 1, SkipActive = SkipActive };
            foreach (var v in _hostVotes.Values) st.Votes.Add(v);
            ApplyState(st);
            MPServer.BroadcastRestState(st);
        }

        public static void ApplyState(RestSkipStatePayload? st)
        {
            if (st == null) return;
            Votes.Clear();
            Votes.AddRange(st.Votes);
            RequiredVotes = st.Required;
            if (!MPServer.IsRunning) SkipActive = st.SkipActive;
        }

        // ── Plumbing ──────────────────────────────────────────────────────────
        private static void SendVote(bool active, double goalMinutes, string activity)
        {
            var p = new RestVotePayload { PlayerId = MPConfig.PlayerId, Active = active, GoalMinutes = goalMinutes, Activity = activity };
            if (MPServer.IsRunning) HostHandleVote(p);
            else if (MPClient.IsConnected) MPClient.SendRestVote(p);
        }

        // Cached PlayerActivityUI wrapper + current activity.  FindObjectsOfType
        // is a full scene walk — doing it 3× per 0.5s poll caused rhythmic ghost
        // pulsing while the dock was open (same disease as the old traffic
        // hitch).  Find once, reuse; re-find only when the cached wrapper dies.
        private static object? _uiWrap;
        private static Type?   _uiType;
        private static object? _curAct;

        private static (object? ui, Type? type) GetActivityUiCached()
        {
            try
            {
                if (_uiWrap != null && _uiType != null)
                {
                    try { _uiType.GetProperty("GetCurrentActivity")?.GetValue(_uiWrap); return (_uiWrap, _uiType); }
                    catch { _uiWrap = null; }   // died with its scene — re-find
                }
                _uiType ??= VehicleManager.FindGameType("PlayerActivity.PlayerActivityUI")
                         ?? VehicleManager.FindGameType("PlayerActivityUI");
                if (_uiType == null) return (null, null);
                var objs = UnityEngine.Object.FindObjectsOfType(_uiType);
                if (objs == null || objs.Length == 0) return (null, null);
                _uiWrap = objs[0];   // Mono: the found object IS the typed instance
                return (_uiWrap, _uiType);
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
