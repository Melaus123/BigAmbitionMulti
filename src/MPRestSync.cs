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
        private static object?  _localActivity;      // typed wrapper of the voting activity
        private static bool     _localVoteActive;
        private static float    _nextPollAt;

        // ── Shared state (host-broadcast; used for banner + detector stand-down) ──
        public static readonly List<RestVoteEntry> Votes = new();
        public static int  RequiredVotes;
        public static volatile bool SkipActive;      // detector stand-down everywhere

        // ── Host-only ─────────────────────────────────────────────────────────
        private static readonly Dictionary<string, RestVoteEntry> _hostVotes = new();
        private static double _skipGoalMinutes;

        public static void Reset()
        {
            _localActivity = null; _localVoteActive = false;
            Votes.Clear(); RequiredVotes = 0; SkipActive = false;
            _hostVotes.Clear(); _skipGoalMinutes = 0;
        }

        // ── Native-skip interception (called from the MPPatches prefix) ───────
        /// <summary>A native TimeMachine start was suppressed.  Decide vote vs
        /// run-at-1×.  MAIN THREAD (called from the game's own UI flow).</summary>
        public static void OnNativeSkipSuppressed()
        {
            try
            {
                var (act, actName) = GetCurrentActivity();
                if (act == null)
                {
                    Plugin.Logger.LogInfo("[Rest] skip suppressed; no current activity found — runs at 1x.");
                    return;
                }
                int remaining = GetRemainingMinutes(act);
                if (remaining < MinVoteMinutes)
                {
                    Plugin.Logger.LogInfo($"[Rest] '{actName}' ({remaining} min) below vote threshold — runs at 1x.");
                    return;
                }
                var (d, h) = GameStateReader.GetGameTime();
                double goal = d * 1440.0 + h * 60.0 + remaining;
                _localActivity   = act;
                _localVoteActive = true;
                SendVote(true, goal, actName);
                Plugin.Logger.LogInfo($"[Rest] vote ON: '{actName}' {remaining} min → goal {Fmt(goal)}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] OnNativeSkipSuppressed: {ex.Message}"); }
        }

        // ── Per-frame tick (main thread, MP active + in game) ─────────────────
        public static void Tick()
        {
            // Local vote lifecycle: drop it when the activity ends/cancels.
            if (_localVoteActive && Time.unscaledTime >= _nextPollAt)
            {
                _nextPollAt = Time.unscaledTime + 0.5f;
                if (!ActivityStillRunning())
                {
                    _localVoteActive = false;
                    _localActivity   = null;
                    SendVote(false, 0, "");
                    Plugin.Logger.LogInfo("[Rest] vote OFF (activity ended/cancelled).");
                }
            }

            if (MPServer.IsRunning) HostTick();
        }

        private static bool ActivityStillRunning()
        {
            try
            {
                var act = _localActivity;
                if (act == null) return false;
                var m = act.GetType().GetMethod("GetState");
                if (m == null) return false;
                int state = Convert.ToInt32(m.Invoke(act, null));
                // PlayerActivityState: started states are low values; finished/
                // cancelled end the activity.  Empirically: 0=None? — treat
                // anything other than the state captured while running as ended
                // is fragile; instead: the activity is over when the player's
                // CURRENT activity is no longer this instance.
                var (cur, _) = GetCurrentActivity();
                return cur != null && ReferenceEquals(cur.GetType(), act.GetType()) && state == _runningState;
            }
            catch { return false; }
        }
        private static int _runningState = -1;

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
                var uiType = VehicleManager.FindGameType("PlayerActivityUI");
                if (uiType == null) return (null, "");
                var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(uiType));
                if (objs == null || objs.Length == 0) return (null, "");
                var wrap = Activator.CreateInstance(uiType, objs[0].Pointer);
                var prop = uiType.GetProperty("GetCurrentActivity");
                var act  = prop?.GetValue(wrap);
                if (act == null) return (null, "");
                // capture the running state value once, for lifecycle polling
                try
                {
                    var sm = act.GetType().GetMethod("GetState");
                    if (sm != null) _runningState = Convert.ToInt32(sm.Invoke(act, null));
                }
                catch { }
                string nm = act.GetType().Name.Replace("Activity", "");
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
