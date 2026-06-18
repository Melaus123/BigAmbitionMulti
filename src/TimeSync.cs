using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Shared time-speed management for the multiplayer session.
    ///
    /// Responsible for two things:
    ///   1. Speed/pause sync — monitors Time.timeScale every frame and flags local
    ///      changes so MPCanvasUI can broadcast them immediately to all peers.
    ///      Applies incoming network speed changes without causing a re-broadcast loop.
    ///
    ///   2. Drift thresholds — constants used by clock-alignment logic so the policy
    ///      lives in one place.
    ///
    /// The guarantee: when every peer has the same Time.timeScale they advance game time
    /// at the same wall-clock rate, so clocks stay naturally in sync.  System 1 handles
    /// pause/speed changes and System 2 (periodic 3-second broadcast) mops up any
    /// floating-point accumulation.
    /// </summary>
    public static class TimeSync
    {
        // ── timeScale application ─────────────────────────────────────────────

        /// <summary>Applies a Time.timeScale value (network sync / startup hold).</summary>
        public static void ApplyNetwork(float scale)
        {
            Time.timeScale = scale;
            string label = scale == 0f ? " (PAUSED)" : $" ({scale:F2}×)";
            Plugin.Logger.LogInfo($"[TimeSync] Network speed applied:{label}");
        }

        // ── Drift alignment thresholds ────────────────────────────────────────

        /// <summary>Drifts below this (game-hours) are ignored — floating-point noise.</summary>
        public const float DRIFT_IGNORE_HOURS = 0.02f;   // ~1 game-minute

        /// <summary>Drifts above this (game-hours) trigger a hard snap rather than a nudge.</summary>
        public const float DRIFT_SNAP_HOURS   = 0.50f;   // 30 game-minutes


        // ── Startup pause hold ────────────────────────────────────────────────
        //
        // When a multiplayer game starts, every player is held at timeScale 0
        // until ALL players have confirmed their scene finished loading.  This
        // prevents the faster-loading player's game clock from advancing while
        // the others are still on the loading screen.  Released automatically
        // by the host once everyone is in-game — no player interaction needed.

        private static bool _startupHold;

        /// <summary>True while the game is frozen waiting for all players to load.</summary>
        public static bool IsStartupHeld => _startupHold;

        /// <summary>Begin the startup hold — freezes the game at timeScale 0 and
        /// engages the game's REAL pause so players can't walk during the wait.</summary>
        public static void BeginStartupHold()
        {
            if (_startupHold) return;
            _startupHold = true;
            ApplyNetwork(0f);
            GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(true));
            MPLoadProfiler.Mark($"FREEZE begin — timeScale now {Time.timeScale}");
            Plugin.Logger.LogInfo("[TimeSync] Startup hold — game paused until all players have loaded.");
        }

        /// <summary>Release the startup hold — resumes the game at normal speed.</summary>
        public static void EndStartupHold()
        {
            if (!_startupHold) return;
            _startupHold = false;
            ApplyNetwork(1f);
            GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(false));
            MPLoadProfiler.Mark("FREEZE end — game running");
            Plugin.Logger.LogInfo("[TimeSync] Startup hold released — game running.");
        }

        /// <summary>
        /// Call every LateUpdate while the hold is active — re-clamps timeScale to
        /// 0 so nothing the game does during load can let time advance.
        /// </summary>
        public static void TickStartupHold()
        {
            if (!_startupHold) return;
            if (Time.timeScale != 0f)
                Time.timeScale = 0f;
        }

        // ── Manual pause ──────────────────────────────────────────────────────
        //
        // The ONLY player-driven pause in the multiplayer time model.  Triggered
        // by the game's pause button (intercepted via a Harmony patch) and shared
        // across all players, so the world pauses/resumes for everyone together.
        // Menu / bench / bed pauses are NOT manual pauses — they are overridden.

        public static bool ManualPaused { get; private set; }

        /// <summary>Sets the shared manual-pause state (from a button press or the
        /// network).  Also drives the game's REAL pause on this machine so a
        /// network-applied pause shows the red border / stops movement exactly
        /// like a local pause press (callable from the poll thread — the IL2CPP
        /// part is marshalled).</summary>
        public static void SetManualPause(bool paused)
        {
            if (ManualPaused == paused) return;
            ManualPaused = paused;
            GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(paused));
            Plugin.Logger.LogInfo($"[TimeSync] Manual pause = {paused}");
        }

        // ── Drift state ───────────────────────────────────────────────────────

        // Remaining game-hours of gradual correction to drip in.
        private static float _correctionHours;
        private static float _correctionElapsed;
        private const  float CORRECTION_REAL_SECS = 3f;  // spread over 3 real seconds

        // ── Authorized-write handshake with the anti-skip watchdog ───────────
        // TickWorldClock (MPCanvasUI) reverts any fast clock advance — which is
        // exactly what a TimeSync snap/drip looks like.  Without this flag the
        // two fight: sync writes host time, watchdog reverts it, repeat (the
        // client's world flickered night↔day every packet — user, 2026-06-12).
        // The watchdog consumes the flag and re-bases its sampling window
        // instead of rejecting.  Its OWN revert writes don't set the flag.
        private static volatile bool _wroteClock;
        public static bool ConsumeClockWrite() { var v = _wroteClock; _wroteClock = false; return v; }

        /// <summary>
        /// Called when a clock-sync packet arrives.  Calculates drift and schedules correction.
        /// </summary>
        public static void ReceiveClockSync(int hostDay, float hostHour)
        {
            // Precedence (forward-compatible): while a consensus skip is active, the skip's fast-run is the SOLE
            // time authority on this machine — routine drift-correction stands down so it can't fight or jump
            // past the fast-run. Phase 2's rate-based drift inherits this same guard.
            if (MPRestSync.SkipActive) return;

            var (localDay, localHour) = GameStateReader.GetGameTime();

            float hostTotal  = hostDay  * 24f + hostHour;
            float localTotal = localDay * 24f + localHour;
            float drift      = hostTotal - localTotal;  // positive = we're behind host

            float absDrift = Mathf.Abs(drift);

            if (absDrift < DRIFT_IGNORE_HOURS)
                return;   // negligible — do nothing

            if (absDrift >= DRIFT_SNAP_HOURS)
            {
                // Large drift — hard snap (only happens if there was a real desync)
                Plugin.Logger.LogWarning($"[TimeSync] Large drift ({drift:+0.##;-0.##} h) — hard snap.");
                GameStateReader.SetGameTime(hostDay, hostHour);
                _wroteClock        = true;   // tell the watchdog this jump is OURS
                _correctionHours   = 0f;
                _correctionElapsed = 0f;
                return;
            }

            // Small–medium drift — schedule a gradual drip over 3 real seconds
            // (accumulate if a second sync arrives before the first finishes)
            _correctionHours  += drift;
            _correctionElapsed = 0f;
            Plugin.Logger.LogInfo($"[TimeSync] Drift {drift:+0.###;-0.###} h → gradual correction.");
        }

        /// <summary>
        /// Call every Update frame (even when paused, but skip if timeScale == 0).
        /// Drips the scheduled clock correction into the game time.
        /// </summary>
        public static void TickClockCorrection()
        {
            if (_correctionHours == 0f) return;
            if (Time.timeScale   == 0f) return;   // paused — hold correction

            _correctionElapsed += Time.unscaledDeltaTime;
            float fraction      = Mathf.Clamp01(_correctionElapsed / CORRECTION_REAL_SECS);

            // Apply a proportional slice this frame
            float applyHours = _correctionHours * (Time.unscaledDeltaTime / CORRECTION_REAL_SECS);
            applyHours = Mathf.Clamp(applyHours, -Mathf.Abs(_correctionHours), Mathf.Abs(_correctionHours));

            var (d, h) = GameStateReader.GetGameTime();
            float total  = d * 24f + h + applyHours;
            int  newDay  = Mathf.Max(0, (int)(total / 24f));
            float newHour = total % 24f;
            if (newHour < 0f) { newDay--; newHour += 24f; }

            GameStateReader.SetGameTime(newDay, newHour);
            _wroteClock = true;   // authorized drip — watchdog re-bases, not rejects
            _correctionHours -= applyHours;

            if (Mathf.Abs(_correctionHours) < 0.001f)
                _correctionHours = 0f;
        }
    }
}
