using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
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
        public const double MinVoteMinutes = 60;     // minimum wait length
        public const float  SkipMinutesPerRealSecond = 25f;

        // ── Local state ───────────────────────────────────────────────────────
        public static bool   Seated       { get; private set; }
        public static string ActivityName { get; private set; } = "";
        private static bool   _localVoteActive;
        private static double _localGoal;
        private static float  _nextPollAt;

        public static bool LocalVoteActive => _localVoteActive;
        public static double LocalGoal     => _localGoal;

        // ── Shared state (host-broadcast; banner + detector stand-down) ──────
        public static readonly List<RestVoteEntry> Votes = new();
        public static int  RequiredVotes;
        public static volatile bool SkipActive;

        // Transient local-only banner line.
        public static string LocalNotice = "";
        public static float  LocalNoticeUntil;

        // ── Host-only ─────────────────────────────────────────────────────────
        private static readonly Dictionary<string, RestVoteEntry> _hostVotes = new();
        private static double _skipGoalMinutes;

        public static void Reset()
        {
            Seated = false; ActivityName = "";
            _localVoteActive = false; _localGoal = 0;
            _machine = null;
            Votes.Clear(); RequiredVotes = 0; SkipActive = false;
            _hostVotes.Clear(); _skipGoalMinutes = 0;
            LocalNotice = ""; LocalNoticeUntil = 0f;
        }

        // ── Native skip button → clean neutralization (MPPatches Postfix) ─────
        public static void OnNativeSkipButtonPressed()
        {
            StopLocalMachine();   // the game's own complete teardown
            LocalNotice      = "Time skip is group-based in multiplayer — use the \"Wait until…\" button while seated.";
            LocalNoticeUntil = Time.unscaledTime + 7f;
            Plugin.Logger.LogInfo("[Rest] native skip press neutralized (machine stopped through its own off switch).");
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

        public static void RequestWait(double goalMinutes)
        {
            if (!Seated)
            {
                LocalNotice = "Sit somewhere first (bench / bed / car) to request a wait.";
                LocalNoticeUntil = Time.unscaledTime + 5f;
                return;
            }
            double now = NowMinutes();
            if (goalMinutes < now + MinVoteMinutes) goalMinutes = now + MinVoteMinutes;
            goalMinutes = Math.Ceiling(goalMinutes / 5.0) * 5.0;   // clean 5-min boundary
            _localGoal = goalMinutes;
            _localVoteActive = true;
            SendVote(true, goalMinutes, ActivityName);
            Plugin.Logger.LogInfo($"[Rest] wait requested: until {Fmt(goalMinutes)} ({ActivityName}).");
        }

        public static void CancelWait()
        {
            if (!_localVoteActive) return;
            _localVoteActive = false;
            SendVote(false, 0, "");
            Plugin.Logger.LogInfo("[Rest] wait cancelled by player.");
        }

        // ── Per-frame tick (main thread, MP active + in game) ─────────────────
        public static void Tick()
        {
            if (Time.unscaledTime < _nextPollAt) return;
            _nextPollAt = Time.unscaledTime + 0.5f;

            // Watchdog: nothing may freeze time outside our explicit systems.
            if (!TimeSync.ManualPaused && !TimeSync.IsStartupHeld)
                GameStateReader.EnsureTimeNotLocked();

            // Seated state from the game's activity system.
            UpdateSeated();

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
                    Plugin.Logger.LogInfo("[Rest] vote OFF (goal time reached).");
                }
            }

            if (MPServer.IsRunning) HostTick();
        }

        private static void UpdateSeated()
        {
            try
            {
                var (act, nm) = GetCurrentActivity();
                bool seated = act != null;
                if (seated != Seated)
                    Plugin.Logger.LogInfo($"[Rest] seated → {seated}{(seated ? $" ({nm})" : "")}");
                Seated = seated;
                if (seated) ActivityName = nm;
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

        private static (object? act, string name) GetCurrentActivity()
        {
            try
            {
                var uiType = VehicleManager.FindGameType("PlayerActivity.PlayerActivityUI")
                          ?? VehicleManager.FindGameType("PlayerActivityUI");
                if (uiType == null) return (null, "");
                var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(uiType));
                if (objs == null || objs.Length == 0) return (null, "");
                var wrap = Activator.CreateInstance(uiType, objs[0].Pointer);
                var act  = uiType.GetProperty("GetCurrentActivity")?.GetValue(wrap);
                if (act == null) return (null, "");
                string nm;
                try
                {
                    var io = (act as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Il2CppSystem.Object>();
                    nm = io?.GetIl2CppType()?.Name?.Replace("Activity", "") ?? "Rest";
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
                    var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_machineType), true);
                    if (objs == null || objs.Length == 0) return null;
                    _machine = Activator.CreateInstance(_machineType, objs[0].Pointer);
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
