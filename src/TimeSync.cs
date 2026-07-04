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
        private static float _lastLoggedScale = -999f;
        public static void ApplyNetwork(float scale)
        {
            Time.timeScale = scale;
            // Log only on an ACTUAL speed change. The unchanged 1.00× reprint every ~3s was ~2,100
            // lines/session of pure noise; the diagnostic value is the transitions (pause/skip/catch-up).
            if (scale != _lastLoggedScale)
            {
                _lastLoggedScale = scale;
                string label = scale == 0f ? " (PAUSED)" : $" ({scale:F2}×)";
                Plugin.Logger.LogInfo($"[TimeSync] Network speed applied:{label}");
            }
        }

        // ── Drift alignment thresholds ────────────────────────────────────────

        /// <summary>Drifts below this (game-hours) are within tolerance — no correction.</summary>
        public const float DRIFT_IGNORE_HOURS = 0.05f;   // 3 game-minutes (user-set 2026-06-18)

        // (DRIFT_SNAP_HOURS removed 2026-06-18: no hard clock-snap anymore. Beyond the dead-band we correct by
        //  RUNNING time — speed up when behind, freeze when ahead — never by writing the clock. The continuous
        //  correction caps drift, so it never accumulates to a size that would have needed a snap.)


        // ── Startup pause hold ────────────────────────────────────────────────
        //
        // When a multiplayer game starts, every player is held at timeScale 0
        // until ALL players have confirmed their scene finished loading.  This
        // prevents the faster-loading player's game clock from advancing while
        // the others are still on the loading screen.  Released automatically
        // by the host once everyone is in-game — no player interaction needed.

        private static bool _startupHold;

        // Round-18 ("client stuck paused after menu→reload"): the client's hold begins only when ITS loading
        // overlay clears (TickOverlayFreezeGate), but the host's release can arrive EARLIER — e.g. the fence
        // excused this client off a stale "Menu" phase report during the menu detour, or the host simply
        // finished first on a fast reload. EndStartupHold's not-held early-return silently swallowed that
        // release; the hold then began and froze forever (nothing left to release it). Remember an early
        // release and skip the pending hold — ordering-proof, no protocol change. Cleared per load cycle
        // (ResetClockState runs in the scene-ready reset block) so a stale marker can't skip a future hold.
        private static bool _releasedBeforeHold;

        /// <summary>True while the game is frozen waiting for all players to load.</summary>
        public static bool IsStartupHeld => _startupHold;

        /// <summary>Begin the startup hold — freezes the game at timeScale 0 and
        /// engages the game's REAL pause so players can't walk during the wait.</summary>
        public static void BeginStartupHold()
        {
            if (_startupHold) return;
            if (_releasedBeforeHold)
            {
                _releasedBeforeHold = false;
                Plugin.Logger.LogInfo("[TimeSync] Startup hold SKIPPED — the release for this load already arrived (fast host / menu-reload ordering).");
                return;
            }
            _startupHold = true;
            ApplyNetwork(0f);
            GameStatePatcher.EnqueueOnMainThread(() => GameStateReader.SetNativePause(true));
            MPLoadProfiler.Mark($"FREEZE begin — timeScale now {Time.timeScale}");
            Plugin.Logger.LogInfo("[TimeSync] Startup hold — game paused until all players have loaded.");
        }

        /// <summary>Release the startup hold — resumes the game at normal speed.</summary>
        public static void EndStartupHold()
        {
            if (!_startupHold)
            {
                _releasedBeforeHold = true;   // arrived before our hold began — remember it (round-18)
                Plugin.Logger.LogInfo("[TimeSync] Startup release received BEFORE the hold began — remembered; the pending hold will be skipped.");
                return;
            }
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

        // Remaining game-hours of forward catch-up to RUN (we're behind the host). Drained by
        // TickClockCorrection, which drives the game tick rather than writing the clock. AHEAD is handled by the
        // freeze flag below, not here.
        private static float _correctionHours;
        /// <summary>True while we're AHEAD of the host: the RunMainGameTick prefix (MPPatches) zeroes the tick
        /// delta so the clock + economy HOLD until the host catches up — the player and the visible world keep
        /// moving (timeScale untouched). Never rewind. Released by a later sync once we're back in tolerance.</summary>
        public static volatile bool AheadHeld;

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
            {
                // Within tolerance — cancel any pending catch-up and release any freeze.
                _correctionHours = 0f;
                AheadHeld        = false;
                return;
            }

            if (drift > 0f)
            {
                // BEHIND the host — schedule a forward catch-up that RUNS the sim (TickClockCorrection drives
                // the game tick), so the catch-up simulates the economy instead of writing the clock past it.
                // Re-targeted on each sync; no hard snap regardless of size.
                _correctionHours = drift;
                AheadHeld        = false;
                Plugin.Logger.LogInfo($"[TimeSync] behind {drift:+0.###} h → run-forward catch-up.");
            }
            else
            {
                // AHEAD of the host — FREEZE our game-time tick (the RunMainGameTick prefix zeroes its delta)
                // until the host catches up; never rewind. Released by a later sync once back in tolerance.
                _correctionHours = 0f;
                AheadHeld        = true;
                Plugin.Logger.LogInfo($"[TimeSync] ahead {drift:+0.###;-0.###} h → hold clock until host catches up.");
            }
        }

        /// <summary>Clear pending clock-correction state at a session/scene boundary so a fresh
        /// game (single-player, or a new MP session) never inherits leftover catch-up / ahead-hold.</summary>
        public static void ResetClockState()
        {
            _correctionHours = 0f;
            AheadHeld        = false;
            _releasedBeforeHold = false;   // early-release marker is per load cycle (round-18)
        }

        /// <summary>
        /// Call every Update frame (even when paused, but skip if timeScale == 0).
        /// Drips the scheduled clock correction into the game time.
        /// </summary>
        public static void TickClockCorrection()
        {
            if (!MPServer.IsRunning && !MPClient.InMpGame) return;   // never drain MP catch-up outside an MP game (e.g. a disconnect dropped us to single-player) — mirrors the AheadFreeze gate
            if (_correctionHours <= 0f) return;   // only the BEHIND catch-up runs here; AHEAD = the freeze flag
            if (Time.timeScale   == 0f) return;   // paused — hold

            // Close the gap by RUNNING time forward this frame (same engine as the skip), capped per frame, so
            // the catch-up simulates the economy instead of writing the clock past it.
            float advanceMin = Mathf.Min(MPRestSync.SkipMinutesPerRealSecond * Time.unscaledDeltaTime,
                                         MPRestSync.MaxSkipMinutesPerFrame);
            advanceMin = Mathf.Min(advanceMin, _correctionHours * 60f);   // don't overshoot the remaining gap
            if (advanceMin <= 0f) return;

            var gm = InstanceBehavior<GameManager>.Instance;
            if (gm != null) gm.RunMainGameTick(advanceMin);
            _wroteClock       = true;            // authorized fast-advance — the watchdog re-bases, doesn't pin
            _correctionHours -= advanceMin / 60f;
            if (_correctionHours < 0.0001f) _correctionHours = 0f;
        }
    }
}
