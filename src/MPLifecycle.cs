using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// THE session lifecycle — single source of truth (consolidation stage 3,
    /// 2026-06-11; design: .modding/03-systems/lifecycle.md).
    ///
    /// SHADOW MODE for now: derives the phase from observable evidence every
    /// frame and logs transitions ("[Lifecycle] A → B"); nothing consumes it
    /// yet.  Consumers migrate one per test in stage 4, then the per-system
    /// heuristics (quiesce timer, dual holds, scattered gates) are retired.
    ///
    /// Confirmed lifecycle laws this encodes (see findings):
    ///  - PlayerController existence ≠ world ready: load-finish runs LONG
    ///    after spawn.  WorldReady = player exists AND the game clock is
    ///    ADVANCING (pause-aware) AND the loading overlay is DOWN — the two
    ///    signals that actually discriminated broken loads from healthy ones.
    ///    (CharacterController was disproved as evidence by the first shadow
    ///    run: the player has no such component — movement is NavMeshAgent /
    ///    ThirdPersonCharacter.)
    ///  - A Loading phase that never reaches WorldReady is a defect: logged
    ///    loudly after 60s (this is the mid-join acceptance instrumentation).
    /// </summary>
    public static class MPLifecycle
    {
        public enum MPPhase { None, Menu, Lobby, Loading, WorldReady, Running }

        public static MPPhase Phase { get; private set; } = MPPhase.None;

        /// <summary>Fired on every transition (old, new).  Subscribers arrive
        /// in stage 4 — keep handlers cheap and exception-safe.</summary>
        public static event Action<MPPhase, MPPhase>? PhaseChanged;

        private static float _nextCheckAt;
        private static float _loadingSince;
        private static bool  _stuckWarned;
        private static float _lastHour = -1f;
        private static float _lastHourChangeAt;
        private static float _readyStableSince;

        public static void Reset()
        {
            Set(MPPhase.None, "reset");
            _loadingSince = 0f; _stuckWarned = false;
            _lastHour = -1f; _lastHourChangeAt = 0f; _readyStableSince = 0f;
        }

        /// <summary>Main thread, every frame (cheap: internals throttled).</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextCheckAt) return;
            _nextCheckAt = Time.unscaledTime + 0.5f;
            try
            {
                bool inMp = MPServer.IsRunning || MPClient.IsConnected;
                if (!inMp) { if (Phase != MPPhase.None) Set(MPPhase.None, "MP ended"); return; }

                bool inLobby = (MPServer.IsRunning && MPServer.IsInLobby)
                            || (MPClient.IsConnected && MPClient.IsInLobby);

                Component? pc = null;
                try { pc = Helpers.PlayerHelper.PlayerController; } catch { }
                bool overlayUp = MPCanvasUI.IsLoadingOverlayUp();

                if (pc == null)
                {
                    if (inLobby) { Set(MPPhase.Lobby, "lobby roster active"); return; }
                    if (overlayUp) { Set(MPPhase.Loading, "overlay up, no player"); return; }
                    Set(MPPhase.Menu, "no player, no overlay");
                    return;
                }

                // Player exists — Loading until the REAL load-finish evidence.
                // (CharacterController was WRONG evidence: the player has no
                // such component — movement is NavMeshAgent/ThirdPersonCharacter
                // — so cc=False was a constant, and healthy sessions sat
                // "stuck" forever.  Shadow-run finding, 2026-06-11.  The
                // discriminators that actually separated broken loads from
                // healthy ones: game clock advancing + overlay down.)
                bool clockAlive = false;
                try
                {
                    var (_, hour) = GameStateReader.GetGameTime();
                    if (Math.Abs(hour - _lastHour) > 0.001f) { _lastHour = hour; _lastHourChangeAt = Time.unscaledTime; }
                    // Paused states legitimately stop the clock — don't hold
                    // readiness hostage to them.
                    clockAlive = (Time.unscaledTime - _lastHourChangeAt) < 6f
                              || TimeSync.ManualPaused || TimeSync.IsStartupHeld;
                }
                catch { }

                bool ready = clockAlive && !overlayUp;
                if (!ready)
                {
                    Set(MPPhase.Loading, $"clock={clockAlive} overlay={overlayUp}");
                    if (_loadingSince > 0f && !_stuckWarned && Time.unscaledTime - _loadingSince > 60f)
                    {
                        _stuckWarned = true;
                        Plugin.Logger.LogWarning($"[Lifecycle] STUCK IN LOADING >60s (clock={clockAlive} overlay={overlayUp}) — load-finish never completed.");
                    }
                    return;
                }

                if (Phase != MPPhase.WorldReady && Phase != MPPhase.Running)
                {
                    Set(MPPhase.WorldReady, "clock alive + overlay down");
                    _readyStableSince = Time.unscaledTime;
                    return;
                }
                if (Phase == MPPhase.WorldReady && Time.unscaledTime - _readyStableSince > 3f)
                    Set(MPPhase.Running, "ready 3s stable");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Lifecycle] tick: {ex.Message}"); }
        }

        private static void Set(MPPhase next, string why)
        {
            if (next == Phase) return;
            var prev = Phase;
            Phase = next;
            if (next == MPPhase.Loading) { _loadingSince = Time.unscaledTime; _stuckWarned = false; }
            else _loadingSince = 0f;
            Plugin.Logger.LogInfo($"[Lifecycle] {prev} → {next} ({why})");
            try { PhaseChanged?.Invoke(prev, next); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Lifecycle] subscriber: {ex.Message}"); }
        }
    }
}
