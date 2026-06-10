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
        public const float SkipMinutesPerRealSecond = 25f;

        // ── Local state ───────────────────────────────────────────────────────
        public static bool   Seated       { get; private set; }
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

        /// <summary>Drive the native duration slider (activity length) without
        /// the native panel — PlayerActivityUI.ChangeSliderValue.</summary>
        public static void ChangeDuration(float minutes)
        {
            try
            {
                var (ui, uiType) = GetActivityUi();
                if (ui == null) return;
                uiType!.GetMethod("ChangeSliderValue")?.Invoke(ui, new object[] { minutes, true });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] ChangeDuration: {ex.Message}"); }
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

        /// <summary>The current activity's remaining minutes (0 if unknown).</summary>
        public static int RemainingActivityMinutes()
        {
            try
            {
                var (act, _) = GetCurrentActivity();
                if (act == null) return 0;
                var m = act.GetType().GetMethod("GetRemainingMinutesForTimeMachine");
                return m != null ? Math.Max(0, Convert.ToInt32(m.Invoke(act, null))) : 0;
            }
            catch { return 0; }
        }

        /// <summary>Make sure the activity itself lasts at least until the goal,
        /// so the game can't auto-stand the player mid-vote.  Silent wiring.</summary>
        public static void EnsureActivityCovers(double goalMinutes)
        {
            try
            {
                double need = goalMinutes - NowMinutes();
                int rem = RemainingActivityMinutes();
                if (need > rem + 1) ChangeDuration((float)(need - rem));
            }
            catch { }
        }

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
                            try { inter = (bool)(bt.GetProperty("interactable")?.GetValue(b) ?? true); } catch { }
                            if (!inter) continue;
                            string label = "";
                            try { label = bt.GetProperty("name")?.GetValue(b) as string ?? ""; } catch { }
                            if (string.IsNullOrEmpty(label))
                            {
                                try { label = bt.GetProperty("key")?.GetValue(b) as string ?? ""; } catch { }
                                if (label.Contains('.')) label = label.Substring(label.LastIndexOf('.') + 1);
                            }
                            object? oc = null;
                            try { oc = bt.GetProperty("onClick")?.GetValue(b); } catch { }
                            DockButtons.Add(new DockButton { Label = string.IsNullOrEmpty(label) ? "Action" : label, OnClick = oc });
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] buttons read: {ex.Message}"); }
            }
            catch { }
        }

        private static (object? ui, Type? type) GetActivityUi()
        {
            try
            {
                var uiType = VehicleManager.FindGameType("PlayerActivity.PlayerActivityUI")
                          ?? VehicleManager.FindGameType("PlayerActivityUI");
                if (uiType == null) return (null, null);
                var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(uiType));
                if (objs == null || objs.Length == 0) return (null, null);
                return (Activator.CreateInstance(uiType, objs[0].Pointer), uiType);
            }
            catch { return (null, null); }
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
