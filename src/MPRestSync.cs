using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Consensus time-skip ("waiting mechanic", 2026-06-10).
    ///
    /// Vanilla skips run through TimeMachine.StartTimeMachine(goal,...) —
    /// sleep, bench rest, work shifts, gym, shower, TV, swimming, school.
    /// In MP the patch in MPPatches suppresses every native skip and instead:
    ///   * duration &lt; 60 game-minutes → no vote; the activity just runs at
    ///     normal (synced) time — showers etc. are seconds of real time.
    ///   * otherwise → a REST VOTE: everyone sees a banner ("1/3 resting —
    ///     Bob: sleeping until Day 2 07:30"); each player must engage a rest
    ///     item themselves.  When ALL connected players have an active vote
    ///     the HOST fast-forwards the authoritative clock (direct clock
    ///     writes — clients follow via the regular time sync) until the
    ///     EARLIEST goal is reached or any vote drops (activity finished or
    ///     cancelled) — i.e. the first sleeper to wake stops the skip.
    ///
    /// No game structs are constructed or reflected (Timestamp stays
    /// untouched — see the 2026-06-10 generic-struct crash rule): goals are
    /// derived from IPlayerActivity.GetRemainingMinutesForTimeMachine().
    /// </summary>
    public static class MPRestSync
    {
        public const float MinVoteMinutes = 60f;     // shorter activities just run at 1×
        public const float SkipMinutesPerRealSecond = 25f;

        // ── Local vote state ──────────────────────────────────────────────────
        private static bool     _localVoteActive;
        private static double   _localGoal;
        private static float    _nextPollAt;

        // ── Shared state (host-broadcast; used for banner + detector stand-down) ──
        public static readonly List<RestVoteEntry> Votes = new();
        public static int  RequiredVotes;
        public static volatile bool SkipActive;      // detector stand-down everywhere

        // Transient local-only banner line (e.g. "resting at normal speed").
        public static string LocalNotice = "";
        public static float  LocalNoticeUntil;

        // ── Host-only ─────────────────────────────────────────────────────────
        private static readonly Dictionary<string, RestVoteEntry> _hostVotes = new();
        private static double _skipGoalMinutes;

        public static void Reset()
        {
            _localVoteActive = false; _localGoal = 0;
            _localHold = false; _pendingEvalAt = 0f;
            _machine = null;
            Votes.Clear(); RequiredVotes = 0; SkipActive = false;
            _hostVotes.Clear(); _skipGoalMinutes = 0;
            LocalNotice = "";
        }

        // ── Machine-started hook (MPPatches Postfix) ──────────────────────────
        // Classification is DEFERRED ~0.35s: the taxi also starts a TimeMachine
        // and LocalInTaxi isn't reliably set yet at start time — stopping the
        // taxi's machine mid-coroutine was the host crash.  By eval time the
        // taxi identifies itself and we leave it fully native.
        private static float _pendingEvalAt;

        public static void OnMachineStarted() => _pendingEvalAt = Time.unscaledTime + 0.35f;

        private static void EvaluateMachine()
        {
            try
            {
                if (TrafficSync.LocalInTaxi) { Plugin.Logger.LogInfo("[Rest] machine belongs to a TAXI ride — native."); return; }
                if (!MachineRunning()) return;   // already gone (cancelled instantly)

                // Authoritative remaining time from the machine itself —
                // _goalTotalMinutes is set before our hook fires.  (The old
                // source, PlayerActivityUI.GetCurrentActivity, is already null
                // by machine-start time: that was the '' (0 min) bug.)
                var (d, h) = GameStateReader.GetGameTime();
                double now = d * 1440.0 + h * 60.0;
                double goalTotal = ReadMachineGoalMinutes();
                double remaining = goalTotal - now;
                var (_, actName) = GetCurrentActivity();
                if (string.IsNullOrEmpty(actName)) actName = "Rest";

                if (goalTotal <= 0 || remaining <= 0)
                {
                    Plugin.Logger.LogWarning($"[Rest] machine running but goal unreadable (goal={goalTotal:F0}, now={now:F0}) — leaving it frozen; cancel via its button.");
                    return;
                }
                if (remaining < MinVoteMinutes)
                {
                    // Stay seated, NO native overlay (user 2026-06-10): hold the
                    // frozen machine with its canvas hidden; our banner notice
                    // persists until the goal time arrives at 1×.
                    _localHold = true;
                    _localGoal = goalTotal;
                    SetMachineCanvasVisible(false);
                    // Starting the machine also PAUSED the game (its normal mode
                    // pauses the sim and drives time itself) — that pause is the
                    // translucent click-blocking overlay AND a frozen clock.
                    GameStateReader.SetNativePause(false);
                    LocalNotice      = $"Resting until {Fmt(goalTotal)} — under 1h, no group skip needed.";
                    LocalNoticeUntil = float.MaxValue;   // cleared on release
                    Plugin.Logger.LogInfo($"[Rest] '{actName}' ({remaining:F0} min) below vote threshold — held at 1x until {Fmt(goalTotal)}, overlay hidden.");
                    return;
                }
                // Lock the goal as an ABSOLUTE target on a clean 5-min boundary.
                double goal = Math.Ceiling(goalTotal / 5.0) * 5.0;
                if (goal <= now + 1.0) goal = now + 5.0;
                _localGoal       = goal;
                _localVoteActive = true;
                MakeOverlayBackgroundTransparent();   // see the world while waiting
                GameStateReader.SetNativePause(false);   // kill the machine's pause/fade
                SendVote(true, goal, actName);
                Plugin.Logger.LogInfo($"[Rest] vote ON: '{actName}' {remaining:F0} min → goal {Fmt(goal)} (machine held).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] EvaluateMachine: {ex.Message}"); }
        }

        private static double ReadMachineGoalMinutes()
        {
            try
            {
                var m = GetMachine();
                var p = _machineType?.GetProperty("_goalTotalMinutes",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (m == null || p == null) return -1;
                return Convert.ToDouble(p.GetValue(m) ?? -1f);
            }
            catch { return -1; }
        }

        // ── Per-frame tick (main thread, MP active + in game) ─────────────────
        public static void Tick()
        {
            // Deferred machine classification (see OnMachineStarted).
            if (_pendingEvalAt > 0f && Time.unscaledTime >= _pendingEvalAt)
            {
                _pendingEvalAt = 0f;
                EvaluateMachine();
            }

            // Sub-hour HOLD: machine frozen + hidden, player seated — release
            // when the goal time arrives (the activity completed at 1×).
            if (_localHold && Time.unscaledTime >= _nextHoldPollAt)
            {
                _nextHoldPollAt = Time.unscaledTime + 0.5f;
                GameStateReader.SetNativePause(false);   // keep the pause/fade away (no-op when unpaused)
                bool release = false;
                if (!MachineRunning()) release = true;        // cancelled natively
                else
                {
                    var (hd, hh) = GameStateReader.GetGameTime();
                    if (hd * 1440.0 + hh * 60.0 >= _localGoal - 0.1)
                    {
                        Plugin.Logger.LogInfo("[Rest] hold goal reached — releasing machine.");
                        StopLocalMachine();
                        release = true;
                    }
                }
                if (release)
                {
                    _localHold = false;
                    SetMachineCanvasVisible(true);            // restore for future skips
                    LocalNotice = ""; LocalNoticeUntil = 0f;  // clear the persistent notice
                }
            }

            // The frozen machine's clock label would otherwise sit still
            // ("stuck not moving") — drive it from the live clock.
            if ((_localVoteActive || _localHold) && Time.unscaledTime >= _nextLabelAt)
            {
                _nextLabelAt = Time.unscaledTime + 0.25f;
                DriveMachineClockLabel();
            }

            // Vote lifecycle = the machine's own isRunning: covers the native
            // Cancel button, completion, and our own StopLocalMachine calls.
            if (_localVoteActive && Time.unscaledTime >= _nextPollAt)
            {
                _nextPollAt = Time.unscaledTime + 0.5f;
                GameStateReader.SetNativePause(false);   // keep the pause/fade away while waiting
                if (!MachineRunning())
                {
                    _localVoteActive = false;
                    SendVote(false, 0, "");
                    Plugin.Logger.LogInfo("[Rest] vote OFF (machine stopped/cancelled).");
                }
                else if (!SkipActive)
                {
                    // Goal reached while the skip already ended (we were the
                    // earliest sleeper) — close our machine; vote drops next poll.
                    var (d, h) = GameStateReader.GetGameTime();
                    if (d * 1440.0 + h * 60.0 >= _localGoal - 0.5)
                    {
                        Plugin.Logger.LogInfo("[Rest] local goal reached — stopping machine.");
                        StopLocalMachine();
                    }
                }
            }

            if (MPServer.IsRunning) HostTick();
        }

        // ── Local TimeMachine instance helpers ────────────────────────────────
        private static object? _machine;          // typed wrapper, cached per scene
        private static Type?   _machineType;

        private static object? GetMachine()
        {
            try
            {
                if (_machine != null)
                {
                    // cached wrapper may have died with its scene
                    try { if (MachineAlive()) return _machine; } catch { }
                    _machine = null;
                }
                _machineType ??= VehicleManager.FindGameType("Timemachine.TimeMachine")
                              ?? VehicleManager.FindGameType("TimeMachine");
                if (_machineType == null) return null;
                var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_machineType), true);
                if (objs == null || objs.Length == 0) return null;
                _machine = Activator.CreateInstance(_machineType, objs[0].Pointer);
                return _machine;
            }
            catch { return null; }
        }

        private static bool MachineAlive()
        {
            var p = _machineType?.GetProperty("isRunning");
            return p != null && _machine != null && p.GetValue(_machine) is bool;
        }

        private static bool MachineRunning()
        {
            try
            {
                var m = GetMachine();
                var p = _machineType?.GetProperty("isRunning");
                return m != null && p != null && (bool)(p.GetValue(m) ?? false);
            }
            catch { return false; }
        }

        private static bool _localHold;
        private static float _nextHoldPollAt;
        private static float _nextLabelAt;

        /// <summary>Hide/show the native skip overlay (sub-hour hold keeps the
        /// player seated without the screen takeover).</summary>
        private static void SetMachineCanvasVisible(bool visible)
        {
            try
            {
                var m = GetMachine();
                var p = _machineType?.GetProperty("canvas");
                var canvas = p?.GetValue(m) as Canvas;
                if (canvas != null) canvas.enabled = visible;
            }
            catch { }
        }

        /// <summary>For the ≥1h vote overlay: make full-screen background/blur
        /// images fully transparent so the world stays visible while waiting —
        /// labels (TMP) and small images (buttons) keep their look.</summary>
        private static void MakeOverlayBackgroundTransparent()
        {
            try
            {
                var m = GetMachine();
                var p = _machineType?.GetProperty("canvas");
                var canvas = p?.GetValue(m) as Canvas;
                if (canvas == null) return;
                var canvasRT = canvas.GetComponent<RectTransform>();
                float big = (canvasRT != null ? canvasRT.rect.width : Screen.width) * 0.6f;
                int dimmed = 0;
                foreach (var g in canvas.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                {
                    if (g == null) continue;
                    if (g.rectTransform.rect.width >= big)
                    {
                        var c = g.color; c.a = 0f; g.color = c;
                        dimmed++;
                    }
                }
                foreach (var g in canvas.GetComponentsInChildren<UnityEngine.UI.RawImage>(true))
                {
                    if (g == null) continue;
                    if (g.rectTransform.rect.width >= big)
                    {
                        var c = g.color; c.a = 0f; g.color = c;
                        dimmed++;
                    }
                }
                Plugin.Logger.LogInfo($"[Rest] overlay background made transparent ({dimmed} image(s)).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] MakeOverlayBackgroundTransparent: {ex.Message}"); }
        }

        /// <summary>The machine's Update is frozen in MP, so its clock label
        /// never moves — write the live time into it ourselves.</summary>
        private static void DriveMachineClockLabel()
        {
            try
            {
                var m = GetMachine();
                if (m == null || !MachineRunning()) return;
                var p = _machineType?.GetProperty("timeLabel");
                var lbl = p?.GetValue(m) as TMPro.TextMeshProUGUI;
                if (lbl == null) return;
                var (d, h) = GameStateReader.GetGameTime();
                int hh = (int)h;
                int mm = (int)((h - hh) * 60.0);
                lbl.text = $"{hh:D2}:{mm:D2}";
            }
            catch { }
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
                var (d, h) = GameStateReader.GetGameTime();
                double now = d * 1440.0 + h * 60.0;
                if (now >= _skipGoalMinutes)
                {
                    SkipActive = false;
                    Plugin.Logger.LogInfo($"[Rest] skip GOAL reached ({Fmt(_skipGoalMinutes)}).");
                    HostBroadcastState();
                    return;
                }
                double next = Math.Min(now + SkipMinutesPerRealSecond * Time.unscaledDeltaTime, _skipGoalMinutes);
                MPCanvasUI.WriteWorldClockMinutes(next);
            }
        }

        private static void HostBroadcastState()
        {
            var st = new RestSkipStatePayload { Required = MPServer.LobbyPlayers?.Count ?? 1, SkipActive = SkipActive };
            foreach (var v in _hostVotes.Values) st.Votes.Add(v);
            ApplyState(st);                     // host's own banner
            MPServer.BroadcastRestState(st);
        }

        /// <summary>Both roles: adopt the broadcast state (banner + stand-down).</summary>
        public static void ApplyState(RestSkipStatePayload? st)
        {
            if (st == null) return;
            Votes.Clear();
            Votes.AddRange(st.Votes);
            RequiredVotes = st.Required;
            if (!MPServer.IsRunning) SkipActive = st.SkipActive;   // host owns its flag
        }

        // ── Plumbing ──────────────────────────────────────────────────────────
        private static void SendVote(bool active, double goalMinutes, string activity)
        {
            var p = new RestVotePayload { PlayerId = MPConfig.PlayerId, Active = active, GoalMinutes = goalMinutes, Activity = activity };
            if (MPServer.IsRunning) HostHandleVote(p);
            else if (MPClient.IsConnected) MPClient.SendRestVote(p);
        }

        private static (object? act, string name) GetCurrentActivity()
        {
            try
            {
                var uiType = VehicleManager.FindGameType("PlayerActivity.PlayerActivityUI")
                          ?? VehicleManager.FindGameType("PlayerActivityUI");
                if (uiType == null) { Plugin.Logger.LogWarning("[Rest] PlayerActivityUI type NOT FOUND."); return (null, ""); }
                var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(uiType));
                if (objs == null || objs.Length == 0) return (null, "");
                var wrap = Activator.CreateInstance(uiType, objs[0].Pointer);
                var prop = uiType.GetProperty("GetCurrentActivity");
                var act  = prop?.GetValue(wrap);
                if (act == null) return (null, "");
                // Concrete il2cpp class name — the managed wrapper is typed as
                // the INTERFACE (was logging as 'IPlayer').
                string nm;
                try
                {
                    var io = (act as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Il2CppSystem.Object>();
                    nm = io?.GetIl2CppType()?.Name?.Replace("Activity", "") ?? "Rest";
                }
                catch { nm = "Rest"; }
                return (act, nm);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] GetCurrentActivity: {ex.Message}"); return (null, ""); }
        }

        private static int GetRemainingMinutes(object act)
        {
            try
            {
                var m = act.GetType().GetMethod("GetRemainingMinutesForTimeMachine");
                return m != null ? Convert.ToInt32(m.Invoke(act, null)) : 0;
            }
            catch { return 0; }
        }

        public static string Fmt(double totalMinutes)
        {
            int d = (int)(totalMinutes / 1440.0);
            double rem = totalMinutes - d * 1440.0;
            int hh = (int)(rem / 60.0);
            int mm = (int)(rem - hh * 60.0);
            return $"Day {d} {hh:D2}:{mm:D2}";
        }
    }
}
