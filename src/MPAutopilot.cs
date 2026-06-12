using System;
using UnityEngine;
using Il2CppInterop.Runtime;
using Intro;          // IntroCharacterCustomizer namespace

namespace BigAmbitionsMP
{
    /// <summary>
    /// TESTING AID — NOT part of the shipping mod.
    ///
    /// When the env var BAMP_AUTOROLE is set to "host" or "client", this
    /// drives the new-game setup automatically so the developer doesn't have
    /// to manually click through the F8 panel, lobby, and character editor
    /// on every test cycle.
    ///
    /// Sequence for HOST (BAMP_AUTOROLE=host):
    ///   1. Wait for main menu (~5 s after plugin load).
    ///   2. Call MPServer.Start(MPConfig.Port).
    ///   3. Poll MPServer.LobbyPlayers.Count &gt; 1.  Client present → next.
    ///   4. Call MPServer.StartNewGame(Normal preset).
    ///   5. Wait for IntroCharacterCustomizer.  Auto-invoke StartGame() on it.
    ///   6. Done.
    ///
    /// Sequence for CLIENT (BAMP_AUTOROLE=client):
    ///   1. Wait for main menu (~5 s after plugin load).
    ///   2. Call MPClient.Connect(MPConfig.HostIP, MPConfig.Port).  Retry every 2 s
    ///      while not connected.
    ///   3. Wait for IntroCharacterCustomizer (host's StartGame message creates it).
    ///      Auto-invoke StartGame() on it.
    ///   4. Done.
    ///
    /// Press F2 in-game to abort the autopilot mid-sequence.
    /// </summary>
    public static class MPAutopilot
    {
        public enum Role { None, Host, Client }
        public enum State
        {
            Disabled,
            WaitMenu,
            HostStart,
            HostWaitClient,
            HostStartingGame,
            ClientConnecting,
            WaitCustomizer,
            ConfirmingCustomizer,
            Done,
            Aborted,
        }

        public  static Role  CurrentRole  { get; private set; } = Role.None;
        public  static State CurrentState { get; private set; } = State.Disabled;
        private static float _stateEnteredAt;
        private static float _lastRetryAt;
        private static int   _attempts;
        private static float _customizerFirstSeenAt = -1f;

        /// <summary>
        /// When BAMP_MANUAL_CUSTOMIZER=1 is set in the launcher env, the
        /// autopilot does NOT auto-invoke IntroCharacterCustomizer.StartGame.
        /// Instead it waits for the user to confirm the customizer manually
        /// (detected by the customizer GameObject disappearing).  Lets a
        /// tester pick a custom character name to verify the name flow
        /// without losing the rest of the autopilot.
        /// </summary>
        private static bool _manualCustomizer = false;

        // Settle time after first detecting IntroCharacterCustomizer before
        // invoking StartGame() on it.  Invoking too early (when the customizer
        // GameObject exists but its UI hasn't finished Start()/coroutines)
        // leaves the customizer in a broken half-state with no UI visible.
        // 2s is the minimum that's been confirmed reliable; raise if you see
        // the customizer UI fail to render on slower machines.
        private const float CustomizerSettleSeconds = 2f;

        // Initial wait after plugin load before we start firing actions.
        // BepInEx plugin load takes ~3-5s in itself (which is mostly out of
        // our control), so by the time MPAutopilot.Tick first runs Unity is
        // already several frames into the main loop.  A tiny margin is enough.
        private const float InitialMenuWaitSeconds = 0.25f;

        // Client retry interval when host isn't up yet.  Connection attempts
        // are cheap (just a socket call) so this can be fast.
        private const float ClientRetryIntervalSeconds = 0.5f;

        // Wait between invoking StartGame() and marking us Done.  Just lets
        // the scene-load coroutine start before we stop ticking.
        private const float DoneSettleSeconds = 1f;

        public static void Init()
        {
            string? r = null;
            try { r = Environment.GetEnvironmentVariable("BAMP_AUTOROLE"); } catch { }
            if (string.IsNullOrWhiteSpace(r))
            {
                CurrentState = State.Disabled;
                Plugin.Logger.LogInfo("[Autopilot] BAMP_AUTOROLE not set — manual mode.");
                return;
            }
            r = r.Trim().ToLowerInvariant();
            if (r == "host")   { CurrentRole = Role.Host;   }
            else if (r == "client") { CurrentRole = Role.Client; }
            else
            {
                Plugin.Logger.LogWarning($"[Autopilot] Unrecognised BAMP_AUTOROLE='{r}' — manual mode.");
                CurrentState = State.Disabled;
                return;
            }
            CurrentState    = State.WaitMenu;
            _stateEnteredAt = Time.realtimeSinceStartup;
            _attempts       = 0;

            // Optional: BAMP_MANUAL_CUSTOMIZER=1 disables the auto-confirm of
            // IntroCharacterCustomizer.StartGame so the tester can pick a
            // custom character name.  Autopilot still drives everything else
            // (lobby, connect, etc.); waits for the human to click Continue.
            try
            {
                var m = Environment.GetEnvironmentVariable("BAMP_MANUAL_CUSTOMIZER");
                _manualCustomizer = !string.IsNullOrWhiteSpace(m) && (m.Trim() == "1"
                                  || m.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            }
            catch { }

            Plugin.Logger.LogInfo($"[Autopilot] BAMP_AUTOROLE='{r}' — role={CurrentRole} state={CurrentState} manualCustomizer={_manualCustomizer}");
        }

        private static void Transition(State next)
        {
            Plugin.Logger.LogInfo($"[Autopilot/{CurrentRole}] {CurrentState} → {next} (t={Time.realtimeSinceStartup - _stateEnteredAt:F1}s in prev)");
            CurrentState    = next;
            _stateEnteredAt = Time.realtimeSinceStartup;
            _attempts       = 0;
        }

        private static bool _f2Down;
        public  static void Tick()
        {
            // F2 abort.
            try
            {
                bool f2 = Input.GetKey(KeyCode.F2);
                if (f2 && !_f2Down && CurrentState != State.Disabled && CurrentState != State.Done && CurrentState != State.Aborted)
                {
                    Plugin.Logger.LogInfo("[Autopilot] F2 pressed — aborting.");
                    Transition(State.Aborted);
                }
                _f2Down = f2;
            }
            catch { }

            if (CurrentState == State.Disabled || CurrentState == State.Done || CurrentState == State.Aborted) return;

            float now    = Time.realtimeSinceStartup;
            float inState = now - _stateEnteredAt;

            try
            {
                switch (CurrentState)
                {
                    case State.WaitMenu:
                        // Brief settle only.  The UI theme is frontloaded independently
                        // (MPCanvasUI.TickThemeCapture), so the autopilot no longer has to
                        // wait for a menu injection — nothing breaks if we proceed fast.
                        if (inState < InitialMenuWaitSeconds) return;
                        if (CurrentRole == Role.Host)   Transition(State.HostStart);
                        else                            Transition(State.ClientConnecting);
                        return;

                    case State.HostStart:
                        if (MPServer.IsRunning) { Transition(State.HostWaitClient); return; }
                        Plugin.Logger.LogInfo($"[Autopilot/Host] Calling MPServer.Start({MPConfig.Port})");
                        MPServer.Start(MPConfig.Port);
                        return;

                    case State.HostWaitClient:
                        // AUTO-START RETIRED (2026-06-12): auto-calling
                        // StartNewGame the instant a client connects collided
                        // with the user driving the menus three times (manual
                        // re-host cuts the client mid-init — the original
                        // double-init stall trap).  The autopilot now ONLY
                        // hosts the server; the user starts from the lobby
                        // (their actual workflow: Lobby Start, new or load).
                        Plugin.Logger.LogInfo("[Autopilot/Host] hosting ready — start the game from the lobby (auto-start retired).");
                        Transition(State.Done);
                        return;

                    case State.HostStartingGame:
                        Plugin.Logger.LogInfo("[Autopilot/Host] Calling MPServer.StartNewGame(Normal)");
                        try { MPServer.StartNewGame(MPServer.Preset("Normal")); }
                        catch (Exception ex)
                        {
                            Plugin.Logger.LogError($"[Autopilot/Host] StartNewGame threw: {ex.Message}");
                            Transition(State.Aborted);
                            return;
                        }
                        Transition(State.WaitCustomizer);
                        return;

                    case State.ClientConnecting:
                        if (MPClient.IsConnected) { Transition(State.WaitCustomizer); return; }
                        // First call, or after retry interval, → connect.
                        if (_attempts == 0 || now - _lastRetryAt > ClientRetryIntervalSeconds)
                        {
                            _attempts++;
                            _lastRetryAt = now;
                            Plugin.Logger.LogInfo($"[Autopilot/Client] Connecting to {MPConfig.HostIP}:{MPConfig.Port} (attempt #{_attempts})");
                            try { MPClient.Connect(MPConfig.HostIP, MPConfig.Port); }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[Autopilot/Client] Connect threw: {ex.Message}"); }
                        }
                        // Give up after 60s of retries.
                        if (inState > 60f)
                        {
                            Plugin.Logger.LogError("[Autopilot/Client] No connection after 60s — aborting.");
                            Transition(State.Aborted);
                        }
                        return;

                    case State.WaitCustomizer:
                        // Two-phase: first see the customizer exist, then wait
                        // CustomizerSettleSeconds for its UI to finish setting
                        // up, then invoke StartGame.  Invoking too early left
                        // the customizer with no UI rendered (observed once).
                        var customizer = FindCustomizer();
                        if (customizer != null)
                        {
                            if (_customizerFirstSeenAt < 0f)
                            {
                                _customizerFirstSeenAt = now;
                                if (_manualCustomizer)
                                    Plugin.Logger.LogInfo($"[Autopilot/{CurrentRole}] Customizer detected — MANUAL mode, waiting for human to click Continue.");
                                else
                                    Plugin.Logger.LogInfo($"[Autopilot/{CurrentRole}] Customizer detected — settling for {CustomizerSettleSeconds:F0}s before StartGame()");
                            }
                            else if (!_manualCustomizer && now - _customizerFirstSeenAt >= CustomizerSettleSeconds)
                            {
                                // Wave 6 gate: don't auto-confirm until host's
                                // RivalsSnapshot has arrived.  Once the
                                // customizer's StartGame fires, SaveGameManager.New
                                // → GenerateRivals runs immediately; we need
                                // host's UUID queue populated by then.
                                if (CurrentRole == Role.Client && !GameStatePatcher.ClientRivalsReady)
                                {
                                    if (_attempts != (int)inState)
                                    {
                                        _attempts = (int)inState;
                                        Plugin.Logger.LogInfo($"[Autopilot/Client] customizer ready, but waiting for host RivalsSnapshot ({inState:F0}s)…");
                                    }
                                    return;
                                }
                                if (TryConfirmCustomizer())
                                    Transition(State.ConfirmingCustomizer);
                            }
                            // In manual mode we sit here doing nothing until the
                            // user finishes — they click Continue, the
                            // IntroCharacterCustomizer GameObject destroys itself,
                            // and the FindCustomizer()==null branch below fires.
                        }
                        else if (_customizerFirstSeenAt > 0f)
                        {
                            // Customizer disappeared after we'd seen it — user
                            // confirmed manually (in _manualCustomizer mode) or
                            // some other code closed it.  Move to Done.
                            Plugin.Logger.LogInfo($"[Autopilot/{CurrentRole}] Customizer gone (manual confirm or external) — transitioning to Done.");
                            Transition(State.Done);
                        }
                        // Heartbeat every 10s while we're still waiting for it to appear.
                        else if (inState > 10f && _attempts != (int)inState / 10)
                        {
                            _attempts = (int)inState / 10;
                            Plugin.Logger.LogInfo($"[Autopilot/{CurrentRole}] waiting for IntroCharacterCustomizer... ({inState:F0}s)");
                        }
                        return;

                    case State.ConfirmingCustomizer:
                        // After invoking StartGame(), wait a moment for the
                        // game scene to load, then call ourselves Done.
                        if (inState > DoneSettleSeconds) { Transition(State.Done); }
                        return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Autopilot] Tick exception in state {CurrentState}: {ex.Message}");
            }
        }

        /// <summary>Returns the live IntroCharacterCustomizer or null.</summary>
        private static IntroCharacterCustomizer? FindCustomizer()
        {
            try
            {
                var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<IntroCharacterCustomizer>());
                if (found == null || found.Length == 0) return null;
                return found[0].TryCast<IntroCharacterCustomizer>();
            }
            catch { return null; }
        }

        /// <summary>
        /// Invoke IntroCharacterCustomizer.StartGame via reflection.  Caller
        /// must have already given the customizer time to fully initialise
        /// (see CustomizerSettleSeconds).
        /// </summary>
        private static bool TryConfirmCustomizer()
        {
            try
            {
                var customizer = FindCustomizer();
                if (customizer == null) return false;
                var t = customizer.GetType();
                var m = t.GetMethod("StartGame",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Instance);
                if (m == null)
                {
                    Plugin.Logger.LogWarning("[Autopilot] IntroCharacterCustomizer.StartGame method not found via reflection.");
                    return false;
                }
                Plugin.Logger.LogInfo("[Autopilot] Invoking IntroCharacterCustomizer.StartGame()");
                m.Invoke(customizer, null);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Autopilot] TryConfirmCustomizer: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
