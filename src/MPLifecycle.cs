using System;
using Intro;
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
                    // CHARACTER CREATION is part of loading, not "Menu": a
                    // new-game client sits here user-paced for minutes — the
                    // Menu misclassification got loading clients excused from
                    // the host's fence (2026-06-11).
                    if (IntroActive()) { Set(MPPhase.Loading, "intro/character creation"); return; }
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
                float hourAge = 0f;
                try
                {
                    var (_, hour) = GameStateReader.GetGameTime();
                    if (Math.Abs(hour - _lastHour) > 0.001f) { _lastHour = hour; _lastHourChangeAt = Time.unscaledTime; }
                    // Paused states legitimately stop the clock — don't hold
                    // readiness hostage to them.  Sweep item 9a (2026-08-18): the THIRD
                    // legitimate freeze was missing — TimeSync.AheadHeld (a client ahead of
                    // the host deliberately freezes its tick until the host catches up).
                    // Without it the detector demoted Running→Loading on every hold and the
                    // phase machine flapped 128-200 cycles/session in the field, each cycle
                    // firing full resyncs (~123-car ParkedSync) into an already-loaded wire.
                    // Round-276 (field 20260818-215459): an excused freeze RE-BASES the
                    // staleness budget instead of merely masking it.  The old OR left
                    // _lastHourChangeAt aging underneath the excuse, so the instant the
                    // flag dropped the full accumulated staleness (10-20s in the field)
                    // was live, and one sample landing before the next clock tick demoted
                    // a RUNNING client to Loading — which the host then treated as a join.
                    // Re-basing also ends the one-flag-per-newly-discovered-freeze pattern
                    // (9a's third flag was the previous instance of this bug class).
                    bool excused = TimeSync.ManualPaused || TimeSync.IsStartupHeld || TimeSync.AheadHeld;
                    if (excused) _lastHourChangeAt = Time.unscaledTime;
                    hourAge = Time.unscaledTime - _lastHourChangeAt;
                    clockAlive = excused || hourAge < 6f;
                }
                catch { }

                bool ready = clockAlive && !overlayUp;
                if (!ready)
                {
                    Set(MPPhase.Loading, $"clock={clockAlive} overlay={overlayUp} manual={TimeSync.ManualPaused} startup={TimeSync.IsStartupHeld} ahead={TimeSync.AheadHeld} hourAge={hourAge:F1}s");
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

        /// <summary>True while the intro/character-creation scene is up.
        /// Round-207g (principle-3 sweep): this was FindObjectOfType EVERY FRAME
        /// through the whole menu/creation/pre-spawn phase — a per-frame
        /// whole-scene scan (the Pre.A load-phase cost). Registry instead: the
        /// Awake patch below stashes the instance; Unity's destroyed-object null
        /// semantics deregister it automatically when the intro scene unloads.
        /// (No OnDestroy exists on the native class to hook — the fake-null
        /// check IS the deregistration.)</summary>
        internal static IntroCharacterCustomizer? IntroInstance;

        private static bool IntroActive()
        {
            try { return IntroInstance != null; }   // Unity overload: destroyed → null
            catch { return false; }
        }

        [HarmonyLib.HarmonyPatch(typeof(IntroCharacterCustomizer), "Awake")]
        private static class Patch_IntroCustomizer_Register
        {
            static void Postfix(IntroCharacterCustomizer __instance) => IntroInstance = __instance;
        }

        /// <summary>Round-276 probe: the reason string of the most recent transition —
        /// rides the phase report so the HOST's log carries the client-side discriminators
        /// (field 20260818-215459: peer logs were uncollectable through the congestion,
        /// leaving the demotion cause unprovable).</summary>
        public static string LastSetReason { get; private set; } = "";

        private static void Set(MPPhase next, string why)
        {
            if (next == Phase) return;
            var prev = Phase;
            Phase = next;
            LastSetReason = why;
            if (next == MPPhase.Loading) { _loadingSince = Time.unscaledTime; _stuckWarned = false; }
            else _loadingSince = 0f;
            Plugin.Logger.LogInfo($"[Lifecycle] {prev} → {next} ({why})");
            try { PhaseChanged?.Invoke(prev, next); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Lifecycle] subscriber: {ex.Message}"); }
        }
    }
}
