# Session Context Log

## Connections & Credentials

## File Paths
- Game install: `C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions\`
- Plugin output: `BepInEx\plugins\BigAmbitionsMP.dll`
- Interop DLLs: `BepInEx\interop\BigAmbitions.dll`
- Source: `C:\code\BigAmbitionsMP\src\`
- BepInEx log: `BepInEx\LogOutput.log`
- Project file: `C:\code\BigAmbitionsMP\BigAmbitionsMP.csproj`

## Schema & Data Structure
- `SaveGameManager.SaveGameStruct` — nested type, NOT standalone `SaveGameStruct`; fields: `alias`, `name`, `characterId`, `saveGameType`, `lastPlayedDate`, `day`, `description`, `isTemporary`, `tags` (rule)
- `SaveGamePathHelper` — class owning `CurrentVersionFolderPath()` (static, returns string) and `GetAllSaveGamesFromVersion(string path)` (static, returns List) (rule)
- `SaveGameManager.Load(SaveGameStruct saveGame, bool loadScene)` — loads a save; `loadScene=true` triggers full scene transition (rule)
- `SaveGameManager.New(GameVariables difficulty)` — creates new game state only, does NOT trigger scene load (rule)
- `UI.Load.LoadScene.LoadGame(bool skipFadeOut)` — triggers game scene load; requires `using UI.Load;` (rule)
- `UI.Load.LoadScene.LoadIntro(bool skipFadeOut)` — loads character creation scene; correct entry point for new game (rule)
- `GameStatePatcher.EnqueueOnMainThread(Action)` — public dispatcher to Unity main thread (rule)
- `GameStatePatcher.DrainQueue()` — must be called from a MonoBehaviour.Update that exists in ALL scenes, not just in-game (rule)
- `Intro.IntroCharacterCustomizer.StartGame()` — called when player clicks Start in character creation; this is what finishes a new-game setup (rule)

## Decisions Made
- 2026-05-18: `DrainQueue()` moved to `MPCanvasUI.Update()` (DontDestroyOnLoad) instead of only in `GameManager.Update` — GameManager only exists in-game, not on main menu (rule)
- 2026-05-18: "Start New Game" uses `LoadScene.LoadIntro(false)`, NOT `SaveGameManager.New() + LoadScene.LoadGame()` — calling `LoadGame()` without character data from the intro scene leaves loading screen stuck at 100% permanently (rule)
- 2026-05-18: "Load Multiplayer Save" uses `SaveGamePathHelper.GetAllSaveGamesFromVersion(SaveGamePathHelper.CurrentVersionFolderPath())` → `SaveGameManager.Load(saves[0], true)` (rule)
- 2026-05-18: MP panel auto-hides on state transition LobbyHost/LobbyClient → Hosting/Connected so it doesn't block clicks on intro scene "Start Game" button (rule)
- 2026-05-18: Probe projects must be created OUTSIDE `C:\code\BigAmbitionsMP\` — subdirectory obj files pollute the main project build with duplicate assembly attributes (rule)
- 2026-05-19: Project backed up to GitHub — private repo https://github.com/Melaus123/BigAmbitionMulti (remote `origin`, branch `main`). `.gitignore` excludes `bin/`, `obj/` (rule)

## Constraints & Requirements
- BepInEx 6.0.0-be.755 IL2CPP on Unity 2022.3.62 — aggressive method stripping (rule)
- Canvas/uGUI (DontDestroyOnLoad MonoBehaviour) is the only reliable Update hook across all scenes (rule)
- All game API calls must be dispatched to main thread via `GameStatePatcher.EnqueueOnMainThread` (rule)
- `using UI.Load;` required for `LoadScene.*` calls (rule)
- `SaveGameManager.New()` alone is NOT sufficient to start a game — character data must come from intro scene (rule)
- MP panel has GraphicRaycaster; if visible it blocks clicks on underlying scene UI — must hide before game transitions (rule)

- `Steamworks.SteamClient.Name` — static string property on `Steamworks.SteamClient` in `Facepunch.Steamworks.Win64.dll` (interop); returns local Steam persona name; requires `SteamClient.IsValid` to be true first (rule)
- `PlayerId` is now auto-detected from `SteamClient.Name` on first run; falls back to `Player-XXXXXX` if Steam not ready; empty config value = auto-detect (rule)
- `IsHost` in config file is noise — never read by `MPConfig.Init()`; role is determined by which UI button the user clicks (rule)

## Open Questions / TODO
- Verify "Start New Game" now fully works: intro → character creation → click Start → enters game
- Test "Load Multiplayer Save" with an actual save file
- After game loads: host should send WorldSnapshot to all connected clients
- Post-game WorldSnapshot: need Harmony patch point for scene-load-complete event
- Join Game flow not tested end-to-end with two players
- Market sync: `GameStateReader.GetMarketEntriesJson()` needs `SaveGameManager.Current != null` (in-session only)
- `SaveGamePathHelper.GetAllSaveGamesFromVersion` sort order (newest-first assumed, not verified) [?]
- F8 panel re-show in-game: user needs to know they can press F8 to get MP panel back after auto-hide

## Current Task State
DONE:
- Lobby system (host waits, clients connect, player list shown, lobby player names)
- "Start New Game": broadcasts to clients → all go to `LoadScene.LoadIntro(false)` → character creation — confirmed working
- "Load Multiplayer Save": broadcasts → all load most recent save via `SaveGameManager.Load(save, true)`
- `DrainQueue()` runs from `MPCanvasUI.Update()` (works on main menu AND in-game)
- MP panel auto-hides when game starts (LobbyHost → Hosting state transition)
- `SaveGameManager.New(difficulty)` called before `LoadScene.LoadIntro(false)` — Start button now works
- Two-instance testing setup: game copied to `C:\BigAmbitions2\`; second instance config set to `PlayerId=Player2`, `IsHost=false`, `HostIP=127.0.0.1`
- PostBuildEvent deploys DLL to both `C:\Program Files (x86)\Steam\...\BepInEx\plugins\` and `C:\BigAmbitions2\BepInEx\plugins\`

- `SaveGameManager.New(difficulty)` MUST be called BEFORE `LoadScene.LoadIntro(false)` — mirrors `MainMenuController.StartNewGame(GameVariables)`. Without it, `SaveGameManager.Current` is null, `IntroCharacterCustomizer.StartGame()` silently returns early after name validation passes, and the Start button appears to do nothing. (rule)
- Harmony patches on `IntroCharacterCustomizer.StartGame()` did not fire even when method was (apparently) called — IL2CPP instance method patching may be unreliable for some scene-loaded MonoBehaviours; static method patches untested in this session. [?] (situational: BepInEx 6 IL2CPP)
- Second game copy `C:\BigAmbitions2\` bypasses single-instance mutex — both instances can run simultaneously (rule)
- Always keep PostBuildEvent deploying to both game dirs — single build updates both instances (rule)
- `SteamAPI_RestartAppIfNecessary()` is called in native C++ and calls `ExitProcess()` if the exe was not launched through Steam — bypasses all Harmony patches and managed code entirely (rule)
- Fix: launch second instance via `C:\BigAmbitions2\launch_client.bat` which sets `SteamAppId`, `SteamGameId`, `SteamOverlayGameId` = 1331550 before starting the exe — fools `SteamAPI_RestartAppIfNecessary` into returning false (rule)
- Big Ambitions Steam AppID = 1331550 (rule)

- `PlayerHelper.GetPosition()` → `Vector3`, `PlayerHelper.PlayerController` → `PlayerController` (static, `using Helpers;`) (rule)
- `PlayerController.Character` → `ThirdPersonCharacter`; `.transform.eulerAngles.y` for yaw (rule)
- `ThirdPersonCharacter.ForceToPosition(Vector3)` / `ForceToRotation(Quaternion)` — teleport methods for applying remote state (rule)
- Remote player visualization: `GameObject.CreatePrimitive(PrimitiveType.Capsule)` + `TextMesh` label; requires `UnityEngine.TextRenderingModule.dll` reference (rule)
- `AddComponent<TextMesh>()` fails at runtime — IL2CPP only AOT-compiles generic instantiations the game itself uses; `TextMesh` is never used by the game so that instantiation doesn't exist. Fix: `go.AddComponent(Il2CppType.Of<TextMesh>()).TryCast<TextMesh>()` (rule)
- Always register the capsule in `_players` dict immediately after `CreatePrimitive`, before any further setup — if later setup throws, the next tick won't try to re-spawn and flood the log (rule)
- `SaveGameManager.Current != null && PlayerHelper.PlayerController != null` — reliable in-game check (rule)
- WorldSnapshot trigger: host schedules 4s delay after detecting in-game; fires `BroadcastWorldSnapshotToAll()` (rule)
- Position sync at 10Hz via `TickPositionSync()` in `MPCanvasUI.Update()` — uses `DeliveryMethod.Unreliable` for low latency (rule)

- Time sync architecture: TWO systems — (1) Time.timeScale monitor: either player pausing/changing speed broadcasts immediately via `GameTimeSync` msg; client sends to host who relays to all; prevents day-skip entirely. (2) 3s heartbeat: host broadcasts {day, hour, speed} every 3s; client applies speed exactly, corrects clock drift gradually (ignore <1 game-min, nudge over 3s if <30 game-min, hard snap if >30 game-min). (rule)
- `TimeSync.PollLocal()` MUST be called BEFORE `DrainQueue()` each frame — prevents echoing back network-applied timeScale changes (rule)
- `TimeSync.ApplyNetwork()` sets `_skipNextPoll=true` before writing `Time.timeScale` to prevent the monitor detecting its own write (rule)
- `Time.unscaledDeltaTime` used for sync timers so paused game doesn't delay heartbeat (rule)
- `GameTimeSyncPayload.Speed = -1` means speed not included; `>= 0` means apply it (rule)
- GameInstance time fields: NOT YET KNOWN — runtime probe logs all day/hour/time/minute/clock/tick fields via C# reflection on first in-game use; check BepInEx log for "GameInstance time-related members" line (situational: until first test run with new build)
- `GameStateReader.GetGameTime()` caches reflected `FieldInfo`/`PropertyInfo` on first call; falls back to (0,0) if probe finds nothing; non-zero result required for BroadcastGameTime to fire (rule)
- Market sync: host broadcasts market snapshot every 60s via `TickMarketSync()` in MPCanvasUI.Update(); also fires 8s after game load (rule)
- In-game player HUD: small overlay top-right corner, F9 toggle, shows local player (green) + remote players (white) from `RemotePlayerManager.GetRemotePlayerIds()`; auto-hides if not connected; built as child of `_canvasGO` (DontDestroyOnLoad) (rule)
- `RemotePlayerManager.GetRemotePlayerIds()` — public static, returns `IReadOnlyList<string>` snapshot of tracked remote player IDs (rule)

DONE (previous):
- Lobby system, Start New Game, Load Multiplayer Save, DrainQueue, auto-hide on game start
- Two-instance testing, dual deploy on build
- Player position sync 10Hz, remote player capsule (blue) + TMP label, smooth lerp, billboard
- WorldSnapshot on host game-load (4s delay)

- `GameManager.ClickSleep()` — public void no-param, on GameManager class (confirmed via DLL string search). PRIMARY intercept point for player sleep. (rule)
- `StartSleeping` / `CancelSleeping` — private void, on a class with `hasHairShampooing` / `Clapping` / `StartAudienceClapping` — NOT on GameManager; likely a character activity class (type name unknown, need runtime probe) (situational: until class name found)
- `StartResting` / `CancelResting` — private void, on a class with `isRecruiting` / `allowResting` / `EnableResting` / `DisableResting` — a separate interactable or NPC class (situational: until class name found)
- `isFastForwarding` — bool field on a class with `ascending` / `disableAging` — character or time manager (situational: until class name found)
- `OnFastForwardToggle(bool)` — on a UI class with `fastForwardToggle` field (probably the game's time UI panel) (situational: until class name found)
- `SkipTime(string)` — public static void, likely a debug console command class, NOT the gameplay sleep API (rule)
- `TimeSync.SKIP_THRESHOLD = 8f` — timeScale above this treated as skip-time, not normal speed button (rule)
- Consensus skip: `ClickSleep` Harmony patch intercepts player sleep, calls `TimeSync.BeginSkipSuppression()` + casts vote; `GameManager.Update` postfix calls `TimeSync.TickSuppression()` AFTER game's own Update to reliably override high timeScale each frame (rule)
- `TimeSync.TickSuppression()` must be called in `GameManager.Update` Postfix (not MPCanvasUI.Update) so it runs AFTER the game's own Update and wins the timeScale race (rule)
- `MPServer.HandleSkipVote()` is public so the ClickSleep Harmony patch can call it directly when running as host (rule)
- Skip-end detection: falling edge on `PollSkipEdge()` while `IsInConsensusSkip` → host broadcasts `TimeSkipEnd{day, hour}` → all clients call `TimeSync.EndConsensusSkip()` which snaps game time and resets state (rule)

- Pause consensus: `MPServer._pauseWants: Dictionary<string,bool>` — tracks ONLY explicit pausers. When player A pauses, only `_pauseWants[A] = true`. Force-paused players (B gets paused by A's pause) are NOT added and do NOT need to explicitly unpause. `AnyPlayerPaused = _pauseWants.Values.Any(v => v)`. (rule)
- WRONG (removed 2026-05-18): `MarkAllWantPaused()` — was writing ALL connected players into `_pauseWants` when any one player paused. This caused the bug: player B (who never pressed pause) being required to explicitly "unpause" to release the lock, even though their UI showed them as unpaused. Removed entirely. (one-off)
- `ProcessLocalSpeedChange` and `ProcessRemoteSpeedChange`: now write `_pauseWants[id] = (speed == 0f)` for the SPECIFIC player only, never touching other players' entries. (rule)
- Clock-rate skip detection in MPCanvasUI: `TickClockRateMonitor()` (called each LateUpdate). Detects bench rest / school / any internal fast-forward that doesn't raise `Time.timeScale`. If `gameHoursPerRealSecond > CLOCK_SKIP_H_PER_S (0.20f)`, starts suppression + casts vote. Resets sentinel on backward clock or single-frame advance > 2h (snap guard). (rule)
- `CLOCK_SKIP_H_PER_S = 0.20f` threshold may need tuning after testing. At 4× normal play typically ~0.07 h/s; bench rest ~0.5-2 h/s. (situational: until first test run with bench rest)
- `MPClient.SkipReadyCount` / `MPClient.SkipTotalCount` — updated on each `TimeSkipStatus` broadcast; used by skip indicator overlay when running as client (rule)
- Skip-time waiting indicator: `_skipIndicatorGO` / `_skipIndicatorTxt`; anchored bottom-center 80px above edge; warm-yellow text; auto-shown by `TickSkipIndicator()` when `TimeSync.IsSuppressingSkip`; counts from `MPServer.SkipVoterCount`/`TotalPlayerCount` (host) or `MPClient.SkipReadyCount`/`SkipTotalCount` (client) (rule)
- `TickTimeScaleMonitor` host path now calls `MPServer.ProcessLocalSpeedChange(newScale)` instead of `BroadcastGameTime(speedOverride)` — routes through pause consensus (rule)
- `BeginSkipSuppression()` must NOT set `_prevWasSkip = true` — TickSuppression clamps timeScale to 1f immediately, causing PollSkipEdge to fire a false falling edge next frame which instantly calls CancelSkip, killing suppression (rule)
- False falling-edge guard in TickTimeScaleMonitor: if `IsSuppressingSkip` is true and a falling skip-edge is detected, ignore it — it's caused by our own suppression, not a genuine player cancel (rule)
- `MPServer.ProcessLocalSpeedChange` must call `TimeSync.ApplyNetwork(0f)` directly (not via EnqueueOnMainThread) when re-suppressing — called from main thread (TickTimeScaleMonitor); enqueueing causes a 1-frame gap where game runs at full speed (rule)
- Client pause consensus: `MPClient.LastServerSpeed` volatile float tracks last server-commanded speed; when client tries to unpause while `LastServerSpeed == 0f`, TickTimeScaleMonitor re-applies pause immediately and sends a "wants to run" vote — host broadcasts all-clear when all players ready (rule)
- REAL root cause of pause stuck bug: `TickTimeScaleMonitor` was in `Update()` but `EnforcePauseConsensus` was in `LateUpdate()`. EventSystem (button clicks) fires between Update and LateUpdate. So: host presses play → EventSystem sets timeScale=1 → LateUpdate re-pauses (ApplyNetwork(0f) + _skipNextPoll=true) → next Update: PollLocal sees _skipNextPoll=true → clears, returns false → ProcessLocalSpeedChange NEVER called → host never removed from _pausedPlayers → permanently stuck. Fix: move TickTimeScaleMonitor to LateUpdate BEFORE EnforcePauseConsensus (same phase = correct state). (rule)
- `_pausedPlayers` / `_skipVoters` not cleared on peer disconnect → disconnecting while paused permanently blocks everyone. Fix: `OnPeerDisconnected` now removes player from both sets, broadcasts unpause/skip-status update as needed (rule)
- Fix: `MPCanvasUI.LateUpdate()` runs after EventSystem — `EnforcePauseConsensus()` and `EnforceSkipSuppression()` act in the same frame as the button click (rule)
- Bug 3 additional root cause: game likely has internal fast-forward multiplier independent of `Time.timeScale`; clamping timeScale doesn't stop game-clock from advancing (rule)
- Fix: `TimeSync.TickSuppression()` added layer 2 — reverts game clock to `_suppressDay`/`_suppressHour` (captured at suppression start) if >~3 game-minutes advance is detected each frame (rule)
- `MPServer.AnyPlayerPaused => _pausedPlayers.Count > 0` used by LateUpdate's `EnforcePauseConsensus()` (rule)

- `GameStateReader` GSC probe (2026-05-18): finds `GameSpeedController` via `AppDomain.CurrentDomain.GetAssemblies()` scan. Logs ALL fields/properties/methods on first call. Caches: `paused`(bool), `isFastForwarding`(bool), `playerGameSpeed`(float), `currentGameSpeed`(float), `GameSpeed`(static prop). Instance captured via Harmony postfix on TogglePause. (rule)
- `GameStateReader.GetGSCDiagnostic()` — returns formatted string of all GSC state; "(GSC instance not yet cached — press pause in-game first)" until TogglePause fires. (rule)
- `GameStateReader.GetGSCPaused()` — returns `GameSpeedController.paused` directly (true = local player chose to pause, not force-paused by network). (rule)
- `GameStateReader.FindGSCMethod(string)` / `CacheGSCInstance(object)` — used by `Patch_GSC_TogglePause` in MPPatches; dynamic patch via TargetMethod() so no compile-time namespace needed. (rule)
- `TickClockRateMonitor` now logs full GSC diagnostic when clock-rate skip detected — discovery record for identifying which game mechanisms trigger time skips (bench rest, school, etc.). Suppression kept as safety net. (rule)
- Build 2026-05-18: 0 errors, 0 warnings. Deployed to both instances. (one-off)
- GSC cache all-null root cause (2026-05-18): `EnsureGSCProbed` was calling `GetField()` but IL2CPP interop exposes ALL instance state as **properties**. `NativeFieldInfoPtr_*` entries in field list are native pointer slots, not readable values. Fix: switch to `GetProperty()` with case-sensitive names: `"Paused"` (capital P), `"isFastForwarding"`, `"playerGameSpeed"`, `"currentGameSpeed"`. (rule)
- `GameStateReader.GetGSCIsFastForwarding()` — returns `GameSpeedController.isFastForwarding` property; authoritative "skip active" flag for bench rest, sleep, any fast-forward regardless of `Time.timeScale` value. (rule)
- `GameInstance.Minute` — `P:Minute(Single)` property, fractional part 0-60; `Hour` is integer 0-23. `GetGameTime()` now adds `minute/60f` so sub-hour advances are visible to clock-rate monitor. (rule)
- `Patch_GSC_Set` — Harmony dynamic patch on `GameSpeedController.Set(GameSpeed)`; caches GSC instance when Set fires (bench skip may never call TogglePause, so Set is needed to ensure instance is cached before TickFastForwardMonitor runs). (rule)
- `TickFastForwardMonitor()` — new method in MPCanvasUI, called each LateUpdate (Step 3). Detects rising/falling edge on `isFastForwarding`. Rising: starts suppression + casts vote if not already in consensus skip. Falling: sends vote=false or broadcasts skip-end. (rule)
- Build 2026-05-18 (session 2): 0 errors, 0 warnings. Deployed to both instances. Includes: TickFastForwardMonitor, Patch_GSC_Set, GSC property fix, Minute precision. (one-off)
- Clock-rate monitor blind spot: `gameElapsed > 2.0f` branch silently discarded large single-frame clock jumps. Now logs them ("Large clock jump … snap or teleport-skip") so a teleport-style skip isn't invisible to the diagnostic. (rule)
- Clock-rate detection already sees minute-granular changes — `GetGameTime()` returns `hour + minute/60f`; "h/s" is just the unit, not the resolution. Diagnostic log now also prints game-min/s for readability. (rule)
- Consensus model decision: **Model A** (shortest skip wins — first player's skip to end snaps everyone to host's current time, others re-trigger to continue). Current `MPServer.HandleSkipVote`/`BroadcastSkipEnd` logic already implements Model A. (rule)
- Skip-vote tracking is currently EDGE-based (vote yes on rising edge, vote no on falling edge) — fragile: a missed falling edge (e.g. closing bench GUI) leaves a player stuck as a permanent yes-voter. PLANNED post-test fix: convert to heartbeat (each client continuously reports current skip state, host derives consensus). (situational: pending bench-skip diagnostic test)
- Build 2026-05-18 (session 2b): 0 errors. Deployed both instances. Diagnostic-only: large-jump logging + game-min/s units. (one-off)
- TEST 2026-05-18: bench-skip diagnostic ran. Result: NONE of 4 detectors fired. (situational: this build)
- GSC member dump confirmed: methods = Awake/Start/Init/SetPause(bool,bool)/TogglePause/DisableTimeControl(bool)/Set(GameSpeed)/Reset/SetPauseState(GameSpeed)/ChangeTimeScale(TimeSpeed,Single)/SetPauseOverlay(GameSpeed)/UpdateVisuals. Props: isFastForwarding(Boolean), Paused(Boolean), playerGameSpeed(GameSpeed), currentGameSpeed(GameSpeed), isTimeControlDisabled(Boolean). (rule)
- Bench/bed time skip is NOT a GameSpeedController mechanism: `isFastForwarding` stayed False through entire test; `Set(GameSpeed)` was never called (no Patch_GSC_Set log). Skip likely advances GameInstance Day/Hour/Minute directly (TemporalBoost). (rule)
- ROOT CAUSE clock-rate monitor never worked: `TickClockRateMonitor` updated `_clockRatePrev*` baseline EVERY frame BEFORE the `realElapsed < 0.05f` guard. At any framerate >20fps every frame is <0.05s, so it always returned at the guard and the rate/large-jump checks were unreachable dead code. (rule)
- FIX: monitor now holds the baseline until `realElapsed >= CLOCK_SAMPLE_WINDOW (0.25s)`, returning WITHOUT updating baseline while the window is short — so frames accumulate against a fixed anchor. Baseline advances only when the window is wide enough to measure. (rule)
- Build 2026-05-18 (session 2c): 0 errors. Deployed both instances. Fixed clock-rate monitor dead-code bug. (one-off)
- Auto-pause startup hold IMPLEMENTED (Option A, fully automatic) 2026-05-18:
  - New messages: `PlayerInGame=6` (client→host), `StartupRelease=7` (host→all); payloads `PlayerInGamePayload`, `StartupReleasePayload`.
  - `TimeSync.BeginStartupHold/EndStartupHold/TickStartupHold` + `IsStartupHeld` — freezes timeScale at 0, re-clamped every LateUpdate.
  - `MPServer._inGamePlayers` HashSet + `_startupReleased` one-shot bool (lock `_startupLock`). `MarkPlayerInGame` releases when `LobbyPlayers.All(in _inGamePlayers)`. `ForceReleaseStartupHold` = timeout safety net. Reset in Stop/StartNewGame/StartLoadGame. OnPeerDisconnected re-checks consensus.
  - `MPCanvasUI`: hold begins in `TickGameLoadDetect` on scene load; LateUpdate Step 0 freezes + skips all monitors while held; `TickStartupTimeout` watchdog force-releases after 90s; skip indicator shows "Waiting for all players to load…".
  - `MPClient`: `SendPlayerInGame`, `HandleStartupRelease`, releases hold on disconnect.
- Build 2026-05-18 (session 2e): 0 errors. Deployed both instances. Auto-pause startup hold. (one-off)
- TEST 3 (2026-05-18): auto-pause + bench `SetGameTime` fix both worked. Bench skip suppression PARTIAL — ~80 game-min skipped before suppression engaged (detection latency overshoot). (situational: this build)
- Bench skip overshoot fix: clock-rate monitor now passes its PRE-detection window-start baseline as the suppression lock point (`BeginSkipSuppression(lockDay,lockHour)` overload) so the revert goes to before the skip, not to where the clock raced to. `CLOCK_SAMPLE_WINDOW` 0.25→0.15s for faster detection. (rule)
- Startup pause: replaced small indicator with dedicated full-screen `_startupScreenGO` overlay naming the players still loading. New `StartupStatus=8` msg + `StartupStatusPayload{WaitingFor}`; host `BroadcastStartupStatus`/`GetStartupWaitingFor`; client `StartupWaitingFor`. (rule)
- Point 3 (pending): MP new game currently loads Story Mode — want Custom mode (story quests would break sync). `GameStateReader.ProbeGameVariables()` added (logs GameVariables fields/props/defaults), called in `MPServer.StartNewGame`. Awaiting test log to identify the mode + difficulty members, then force Custom. Difficulty (easy/normal/hard) UI option also wanted. (situational: pending probe results)
- Build 2026-05-18 (session 2f): 0 errors. Deployed Steam install only — C:\BigAmbitions2 copy failed (game still running, file locked). Needs redeploy after instance closed. (one-off)
- 2026-05-18: session 2f DLL manually copied to C:\BigAmbitions2 after instance closed — both instances now current. (one-off)
- Bench-rest method now a CURRENT diagnostic (user request). `GameStateReader.ProbeGameInstance()` logs GameInstance time/skip/boost method+property names (called on game load); `DumpGameInstanceTimeState()` logs live boost/temporal/skip/fast property values, added to the clock-rate detection log. Goal: find the actual skip method to patch for zero-overshoot. (rule)
- GameVariables probe needs no special action from user — `ProbeGameVariables` reflects the type, runs on host "Start New Game"; SP not needed. (rule)
- Build 2026-05-18 (session 2g): 0 errors. Deployed both instances. GameInstance time-skip discovery probe. (one-off)
- TEST 4 (2026-05-18): overshoot fix works (Reverted now steady ~0.06-0.08h). Startup hold worked. Startup SCREEN never visible — BUG.
- STARTUP SCREEN BUG root cause: auto-hide on game start did `_canvasGO.SetActive(false)` — deactivates the whole canvas; HUD + skip indicator + startup screen are all children of `_canvasGO`, so none can render in-game. FIX: auto-hide / F8 now toggle `_panelRT.gameObject` (the draggable panel) only, leaving `_canvasGO` active. (rule)
- BENCH DEADLOCK root cause (from log): bench rest pauses via a smooth `Time.timeScale` tween 1→0 (0.11,0.03,0.01,0). `TickTimeScaleMonitor` broadcasts every step → propagates the freeze to ALL players → a 2nd player can't move to their own bench → skip consensus can never form. Same applies to ANY menu that pauses. (rule)
- GameInstance probe: NO time/skip methods on GameInstance; `timeEnteredTemporalBoost` empty during bench skip. Bench skip writes Day/Hour/Minute directly; trigger method is on a separate resting/bench class, not GameInstance. (rule)
- GameVariables: has `difficulty`(enum, default Normal) + `tutorialEnabled`(bool, default True) + many sandbox multipliers. NO explicit story/custom-mode field. Hypothesis: story mode ⇔ `tutorialEnabled=true`; custom = `tutorialEnabled=false`. [?] (situational: pending confirmation)
- Added `GameStateReader.GetGSCTimeControlDisabled()` + `timeCtrlDisabled` in GSC diagnostic — `isTimeControlDisabled` is the candidate discriminator for "menu pause vs pause-button press" (needed for the deadlock fix). (rule)
- Build 2026-05-18 (session 2h): 0 errors. Deployed both. Startup-screen panel-hide fix + isTimeControlDisabled diagnostic. (one-off)

## TIME MODEL DECISION (2026-05-18) — wall-clock / no-skip model
- DECIDED: multiplayer abandons elastic time. The world runs at permanent 1× real-time. No fast-forward, no time skips, no menu pauses. The ONLY pause is the deliberate pause button (shared/consensus). Energy need disabled (no sleep-skip). Tutorial disabled (story quests would desync). (rule)
- Rationale: elastic time is inherently fragile in MP (two players = two timelines). One wall-clock timeline eliminates skip-consensus, suppression, the deadlock, and menu-desync entirely. (rule)
- Dedicated-server idea explicitly DEFERRED to a later revision; offline-company simulation also deferred. (situational: revisit later)
- IMPLEMENTED 2026-05-18 (session 3a):
  - `TimeSync.ManualPaused` + `SetManualPause(bool)` — the only player pause.
  - `MPCanvasUI.LateUpdate` rewritten: forces `Time.timeScale = (ManualPaused||IsStartupHeld) ? 0 : 1` every frame → menus/benches/beds can no longer pause the world. Also `Postfix_GameManagerUpdate` enforces the same (2nd point, after game Update).
  - `TickWorldClock()` (MPCanvasUI) — windowed skip detector (`WC_SAMPLE_WINDOW 0.15s`, `WC_SKIP_H_PER_S 0.20`): on a skip, pins the clock to the pre-skip value until the skip mechanism settles → skips simply never work. `ResetWorldClock()` on game load.
  - `Patch_GSC_TogglePause` repurposed: pause button → reads absolute `GetGSCPaused()` → `SetManualPause` + broadcast. Confirmed TogglePause does NOT fire for benches/menus, so it's a clean pause-only signal.
  - New `ManualPause=9` message + `ManualPausePayload{Paused}`; `MPServer.BroadcastManualPause`/`HandleClientManualPause` (relays), `MPClient.SendManualPause`/`HandleManualPause`.
  - `MPServer.MakeGameVariables()` — `tutorialEnabled=false`, `disableEnergy=true` (try/catch on the setters); used by all 4 New() call sites in MPServer+MPClient.
  - `Prefix_ClickSleep` gutted to `return true` (no skip consensus).
  - OLD skip-consensus + pause-consensus code (TickTimeScaleMonitor, EnforcePauseConsensus, EnforceSkipSuppression, TickFastForwardMonitor, TickClockRateMonitor, HandleSkipVote, BeginSkipSuppression, etc.) left as DEAD code (not called) — Stage 2 cleanup pending. (rule)
- DEFERRED follow-up: difficulty picker (Easy/Normal/Hard) in host lobby UI — `MakeGameVariables` currently uses default difficulty (Normal). (situational: pending)
- Build 2026-05-18 (session 3a): 0 errors, 0 warnings. Deployed both instances. (one-off)
- TEST (2026-05-18): wall-clock time model CONFIRMED working — Custom-mode new game, menus don't pause, skips don't work, manual pause, startup screen all verified. Startup screen needs staggered player connect times to be visible (no minimum-duration added — reverted, by user request). (rule)
- Build 2026-05-18 (session 3b): 0 errors. Deployed both. (reverted a brief min-duration experiment). (one-off)
- REMAINING: difficulty picker (Easy/Normal/Hard) in host lobby UI; Stage-2 cleanup of dead skip/pause-consensus code. Deferred tracks: vehicle sync, dedicated server, offline-company simulation. (situational: next work)
- GameVariables PROBE RESULT (2026-05-18): `Difficulty` enum = {Custom, Easy, Normal, Hard}. `difficulty` is a PURE LABEL — setting it changes NONE of the other 23 settings. No preset method on GameVariables. Vanilla Easy/Hard presets live in the game's new-game menu, which the MP flow bypasses → the mod owns GameVariables 100% and must define its own presets. (rule)
- GameVariables = 23 settings + `difficulty`. Normal/default values: startingAge=18, disableAging=F, disableEnergy=F, disableHappiness=F, allCoursesUnlocked=F, startingMoney=4200, taxPercentage=10, daysPerYear=60, marketPriceMultiplier=1, employeeHourlySalaryMultiplier=1, bankInterestMultiplier=1, tutorialEnabled=T, bankInterestRate=-0.5, rivalsDifficultyMultiplier=1, disableVehicleDamage=F, disableVehicleFuel=F, allContactsUnlocked=F, baseCustomerPromotionMultiplier=0.5, wholesaleUrgentFeeMultiplier=0.2, importerUrgentFeeMultiplier=0.75, disableWholesaleAndImportLimits=F, allProductsAvailableFromImporters=F, exportMultiplier=0.65. (rule)
- Per-player difficulty rule (user-decided): clients may override only PLAYER-level settings (startingAge/Money, disableAging/Energy/Happiness, allCourses/Contacts, vehicle damage/fuel); WORLD-level settings (tax, daysPerYear, all multipliers, rivals, fees, import/export) always match the host. (rule)
- Decision: mod-defined difficulty presets (user-accepted, tune later). (rule)
- IMPLEMENTED 2026-05-18 (session 3c) — settings networking + difficulty selector:
  - `GameVariablesDto` (Protocol.cs) — plain serialisable mirror of all 24 GameVariables; defaults = vanilla Normal + MP overrides (TutorialEnabled=false, DisableEnergy=true). `StartGamePayload.Settings` carries it.
  - `MPServer.Preset(string)` — Easy/Normal/Hard preset DTOs (tweaks startingMoney, taxPercentage, rivalsDifficultyMultiplier, employeeHourlySalaryMultiplier; rest baseline). `BuildGameVariables(dto)` converts DTO→GameVariables (difficulty enum set via reflection). `MakeGameVariables()` now = `BuildGameVariables(Preset("Normal"))`.
  - `MPServer.StartNewGame(GameVariablesDto)` — networks host's settings; client `HandleStartGame(env,isNew)` applies the host's DTO.
  - Host lobby pane: click-to-cycle Difficulty selector (`_rtLHDifficulty`/`_txtLHDifficulty`, `OnCycleDifficulty`); `OnStartNew` sends `Preset(_selectedDifficulty)`.
  - REMAINING for element #1: full 23-toggle editor UI + "delegate difficulty" per-client overrides.
- Build 2026-05-18 (session 3c): 0 errors, 0 warnings. Deployed both instances. (one-off)
- IMPLEMENTED 2026-05-18 (session 3d) — settings editor UI (step 3):
  - `_hostSettings` (live edited DTO); host lobby "⚙ Game Settings…" button opens a modal panel.
  - `BuildSettingsPanel` — modal overlay, all 23 settings grouped WORLD/PLAYER. `SettingsBoolRow` (ON/OFF toggle), `SettingsNumRow` (◄ ► stepper). Editing any value → `MarkCustom()` (difficulty="Custom").
  - `_settingsHits` list (RectTransform→Action) for click routing; `_settingsRefreshers` re-display values; cycling the difficulty preset reloads `_hostSettings` + refreshes the panel.
  - `BuildSettingsPanel` wrapped in its own try/catch so a UI fault can't kill the whole canvas.
  - REMAINING for element #1: step 4 — "delegate difficulty" + client-side per-player (player-level only) override picker.
- Build 2026-05-18 (session 3d): 0 errors, 0 warnings. Deployed both instances. (one-off)
- IMPLEMENTED 2026-05-18 (session 3e) — settings editor polish:
  - Hover tooltips: `_settingsTips` (label rect→description); `TickSettingsPanel` shows a cursor-following `_tooltipGO` while hovering a setting name.
  - Click-to-type: numeric values are now editable fields (`NumField`/`_numFields`); click a value → `BeginSettingsEdit`, type digits/./- , Enter or click-away → `CommitSettingsEdit` (parse, clamp, apply). ◄ ► steppers still work.
  - Settings char input routed via `_editField`/`_editBuffer` in the Update inputString block (separate from the name/IP/port `_focus` system).
- Build 2026-05-18 (session 3e): 0 errors, 0 warnings. Deployed both instances. (one-off)
- IMPLEMENTED 2026-05-18 (session 3f) — tooltip-left-of-cursor fix + step 4 (delegate difficulty):
  - Tooltip now placed LEFT of the cursor (cursor body extends down-right), right-side fallback.
  - `MPServer.AllowPlayerDifficulty` + `SetAllowPlayerDifficulty` (re-broadcasts lobby); `LobbyUpdatePayload`/`StartGamePayload` carry the flag; `MergePlayerDifficulty(host,clientDiff)` = host WORLD fields + client preset PLAYER fields.
  - `MPClient.AllowPlayerDifficulty`/`ChosenDifficulty`; `HandleStartGame` merges when delegated.
  - Host lobby: "Per-player difficulty: ON/OFF" toggle. Client lobby: difficulty cycler (active only when host allowed it).
  - NOTE: with current presets, per-player difficulty only varies StartingMoney (other preset diffs are world-level). Deepen later by tuning preset player-level values.
- Build 2026-05-18 (session 3f): 0 errors, 0 warnings. Deployed both instances. Element #1 (difficulty) feature-complete pending test. (one-off)
- REWORK 2026-05-18 (session 3g) — per-player difficulty → per-player STARTING CASH (user: difficulty abstraction was misleading; only starting money was per-player-relevant):
  - `AllowPlayerDifficulty` → `EnforceStartingCash` everywhere (Protocol/MPServer/MPClient), boolean meaning flipped (true=host enforces, default true).
  - `MergePlayerDifficulty` deleted; client `HandleStartGame` just overrides `StartingMoney` with `MPClient.ChosenStartingCash` when `!EnforceStartingCash`.
  - Host lobby toggle relabelled "Starting cash: ENFORCED / per-player" (`OnToggleEnforceCash`).
  - Client lobby: difficulty cycler replaced with a typed cash field (`MakeField`, `_focus==5`, `_clientCash`→`MPClient.ChosenStartingCash`); shows "(set by host)" when enforced.
- Build 2026-05-18 (session 3g): 0 errors, 0 warnings. Deployed both instances. (one-off)

## STAGE-2 CLEANUP (2026-05-19)
- Deleted all dead skip-consensus + pause-consensus code: `TimeSkip*` messages/payloads (Protocol); MPClient skip handlers + `LastServerSpeed`/`Skip*Count`/`SendSkipVote`/`SendTimeSpeedChange`; MPServer `HandleSkipVote`/`HandleClient*`/`Broadcast Skip*`/`ProcessLocal/RemoteSpeedChange`/`_skipVoters`/`_pauseWants`/`AnyPlayerPaused`; MPCanvasUI dead monitors (`TickTimeScaleMonitor`,`EnforcePauseConsensus`,`EnforceSkipSuppression`,`TickFastForwardMonitor`,`TickClockRateMonitor`,`TickSkipIndicator`,`BuildSkipIndicator`); TimeSync `PollLocal`/skip-suppression cluster (`BeginSkipSuppression`,`TickSuppression`,etc.). `ApplyNetwork` simplified. (rule)
- NEAR-MISS during cleanup: a bulk line-range delete of MPCanvasUI also removed 5 LIVE methods interleaved with the dead monitors (`IsInGame`,`TickGameLoadDetect`,`TickStartupTimeout`,`TickWorldSnapshot`,`TickPositionSync`) — reconstructed from conversation history; build green but these should be sanity-tested in-game. No git repo → no clean restore available. (rule)
- Item 3 (dedicated server / offline-company sim) REMOVED from the roadmap per user. (rule)
- NEXT FEATURE (user-requested): remote players rendered as real animated character models instead of capsule placeholders. Needs a discovery probe of the character hierarchy + Animator first. (situational: next work)
- Build 2026-05-19 (session 3h, cleanup): 0 errors, 0 warnings. Deployed both instances. (one-off)
- `UnityEngine.AnimationModule.dll` interop reference added to the csproj — required for `Animator`/`AnimatorControllerParameter` (like TextRenderingModule was for TextMesh). (rule)
- `RemotePlayerManager.ProbeLocalCharacter()` — one-time discovery probe: logs the local character's GameObject hierarchy + components (depth 4), the Animator (controller/avatar/isHuman/parameters), and all SkinnedMeshRenderers. Called from `MPCanvasUI.TickPositionSync` (self-guards via `_characterProbed`). Data needed to build real animated remote-player models. (rule)
- Build 2026-05-19 (session 3i): 0 errors, 0 warnings. Deployed both. Character discovery probe. (one-off)
- CHARACTER PROBE RESULTS (2026-05-19): hierarchy = `Player`[PlayerController,ThirdPersonCharacter,NavMeshAgent,...] → `Model`[Animator,RigBuilder,BoneRenderer,StepTrigger,AnimationObjectSpawner,AnimationTriggerEvents,SkinnedMeshCombiner] → `Armature`(skeleton+IK rigs) + `Male`/`Female`(clothing-variant SkinnedMeshRenderers, AppearanceElementVariant). Animator: controller `CharacterAnimator`, avatar `ArmatureLODAvatar`, isHuman, 115 params. Locomotion params: `Forward`(Float), `Running`(Bool); also `AnimationSpeed`,`Sitting`,`Laying`,`Swimming`,`Dancing`,etc. (rule)
- IMPLEMENTED 2026-05-19 (session 3j) — real animated remote-player models:
  - `RemotePlayerManager.SpawnRemotePlayer` clones the local `Player/Model` GameObject under a root GO; `StripModelScripts` destroys RigBuilder/BoneRenderer/StepTrigger/AnimationObjectSpawner/AnimationTriggerEvents/SkinnedMeshCombiner (keeps Animator+meshes+skeleton). Capsule fallback if the clone fails.
  - `RemotePlayerMover` drives the clone's Animator: `Forward`=smoothed movement speed, `Running`=speed>3.5. Tune thresholds after testing.
  - Remote players currently look like a copy of the LOCAL player's appearance (cloned model). Per-player appearance sync is a future follow-up.
- Build 2026-05-19 (session 3j): 0 errors, 0 warnings. Deployed both instances. (one-off)
- TEST (2026-05-19): real animated player models confirmed working — bodies + walk/idle/run animations look good (minor anim deviation, deferred). NEXT: sync each remote player's actual appearance (currently they look like a clone of the local player).
- `RemotePlayerManager.ProbeAppearance()` — discovery probe: logs the `AppearanceSetter` component's props/methods, per-gender per-category active-variant state, and `AppearanceElementVariant` members. Called from `TickPositionSync` (self-guards). Data needed to design per-player appearance sync. (rule)
- Build 2026-05-19 (session 3k): 0 errors, 0 warnings. Deployed both instances. Appearance discovery probe. (one-off)
- APPEARANCE PROBE RESULT: `AppearanceSetter`/`AppearanceElementVariant` have no usable interop wrapper (resolve as `Component`) — not needed. Appearance = gender (Male/Female GameObject active) + exactly ONE active child per category under the active gender; inactive gender leaves all children active (irrelevant). Universal Model prefab contains all variants → syncing the selection reproduces any look. Colours/body-shape NOT covered (future). (rule)
- IMPLEMENTED 2026-05-19 (session 3l) — appearance variant sync:
  - `PlayerAppearancePayload{PlayerId,Gender,Variants:Dict}` + `AppearanceSyncPayload{List}`; messages `PlayerAppearance=32` (client→host), `AppearanceSync=33` (host→all, full set).
  - `RemotePlayerManager`: `ReadLocalAppearance` (gender + active variant per category), `SetAppearance` (store + apply if model spawned), `ApplyAppearance` (toggle gender + per-category SetActive), `GetAllAppearances`, `_appearances` registry. `SpawnRemotePlayer` applies stored appearance on spawn.
  - `MPServer.HandleClientAppearance`/`RegisterHostAppearance`/`BroadcastAppearanceSync` (full-set broadcast — handles late join). `MPClient.SendAppearance`/`HandleAppearanceSync`.
  - `MPCanvasUI.TrySendLocalAppearance` — reads + sends local appearance once in-game (retries until character ready); reset on game load.
  - Verification logging: `[Appearance] Local appearance: <gender — Cat=Variant...>` and `[Appearance] Applied to '<player>': ...`.
- Build 2026-05-19 (session 3l): 0 errors, 0 warnings. Deployed both instances. (one-off)
- `RemotePlayerManager.ProbeColors()` — colour discovery probe: for each active body/clothing/hair renderer logs material name, shader, all Color-type shader properties + values, and whether a MaterialPropertyBlock is present. Called from `TickPositionSync`. Data needed to sync skin/hair/clothing colours. (rule)
- Build 2026-05-19 (session 3m): 0 errors, 0 warnings. Deployed both instances. Colour discovery probe. (one-off)
- COLOUR PROBE RESULT (2026-05-19): colours live in per-renderer instanced materials (no MaterialPropertyBlocks). Skin=`_MaskColorRed` on `SH_CharacterStandard`; hair/beard=`_BaseColor`; clothing=`_MaskColorRed/Green/Blue`+`_SpecularColor*` on `SH_CharacterClothes`/`SH_CharacterClothesArray`. (rule)
- IMPLEMENTED 2026-05-19 (session 3n) — colour sync:
  - `ColorEntry{Cat,Mat,Prop,R,G,B,A}` + `PlayerAppearancePayload.Colors:List<ColorEntry>` (Protocol).
  - `RemotePlayerManager.CaptureColors` — generic: every Color-type shader property on every material of each active variant's SkinnedMeshRenderer; called from `ReadLocalAppearance`'s variant loop.
  - `ApplyColors` — groups Colors by category, finds each active variant's SMR, uses `.materials` (forces per-renderer instanced copies so the local player isn't recoloured), `SetColor` each entry; called at end of `ApplyAppearance`.
- Build 2026-05-19 (session 3n): 0 errors, 0 warnings. Deployed both instances. Colour sync. (one-off)
- TEST (2026-05-19): colour sync confirmed working — remote players show actual skin/hair/clothing colours, local player unaffected. (rule)
- `RemotePlayerManager.ProbeMorphs()` — discovery probe: logs non-zero blendshape weights on each active variant SMR + Armature bones with non-default localScale; summary line says whether morphs are in use. Called from `TickPositionSync` (self-guards). Decides whether body-shape/face-morph sync is needed before moving to vehicle sync. (situational: pending test result)
- Build 2026-05-19 (session 3o): 0 errors, 0 warnings. Deployed both instances. Body-shape/morph discovery probe. (one-off)
- MORPH PROBE RESULT (2026-05-19): body shape IS blendshape-driven, 0 scaled bones. Each body/clothing variant SMR has 3 blendshapes; `Fat`/`fat` is the weight slider (mirrored across Head/Torso/Legs/Feet); 2 other blendshapes per renderer (zero in test = untouched, likely muscle/thin). Hair has 2 (both zero). → sync must capture ALL blendshape weights by name. (rule)
- IMPLEMENTED 2026-05-19 (session 3p) — body-morph sync:
  - `BlendEntry{Cat,Shape,Weight}` + `PlayerAppearancePayload.Blends:List<BlendEntry>` (Protocol).
  - `RemotePlayerManager.CaptureBlendShapes` — every blendshape weight (by name) on each active variant's SMR; called from `ReadLocalAppearance` loop.
  - `ApplyBlendShapes` — groups by category, finds active variant SMR, `GetBlendShapeIndex(name)`→`SetBlendShapeWeight`; called at end of `ApplyAppearance`.
- Build 2026-05-19 (session 3p): 0 errors, 0 warnings. Deployed both instances. Body-morph sync. (one-off)
- ANIM DEVIATION root cause: `RemotePlayerMover` does NOT sync real animator state — it re-derives speed from remote position deltas (smoothed, lagged) and feeds Forward/Running. The lag/jitter IS the slight deviation. FIX = network the owner's REAL animator params instead of deriving. (rule)
- `RemotePlayerManager.ProbeAnimatorLive()` — live probe: samples local Animator every frame, logs each param the first time it changes from baseline (`[AnimProbe]` tag). Floats/Bools/Ints sampled; triggers skipped (momentary). Reveals the minimal param set to network. Called from `TickPositionSync` (runs every frame). (rule)
- Planned anim-sync design (lean, after probe): minimal param set only, rides existing 10Hz PlayerMove packet, floats quantized to 1 byte, bools packed + sent on-change only (~2-4 bytes/player/update). (situational: pending probe result)
- Build 2026-05-19 (session 3q): 0 errors, 0 warnings. Deployed both instances. Live animator probe. (one-off)
- ANIM PROBE RESULT (2026-05-19): light play caught only 5/115 params (Forward,BoredAnimation floats; Sitting,HoldingBox,OnScooter bools). Param types: ~9 Float, ~59 Bool/Int, 47 Trigger. Discovery-by-doing is unworkable (47 context-specific triggers never reachable on a fresh save) → switched to GENERIC full-animator mirror. (rule)
- IMPLEMENTED 2026-05-19 (session 3r) — generic animator sync:
  - DESIGN: network the animator's INPUT state, not enumerated animations. Param index = position in `Animator.parameters` (same controller asset everywhere → index is portable). Floats/ints sent in FULL each tick (self-healing over Unreliable); bools as list of true indices (receiver sets every bool explicitly = default-agnostic); triggers are momentary → caught by Harmony patch + sent RELIABLE.
  - `PlayerPositionPayload` + `AnimF:Dict<int,float>`, `AnimI:Dict<int,int>`, `AnimB:List<int>`. New `PlayerAnimTrigger=34` msg + `AnimTriggerPayload{PlayerId,ParamIndex}`.
  - `RemotePlayerMover` REWRITTEN: removed the derive-speed-from-position block (that lag WAS the deviation). Now `ApplyAnimState`/`FireTrigger` + param-index map; floats lerped (AnimFLerp=14) toward target, bools/ints set directly, triggers fire-and-forget.
  - `RemotePlayerManager`: `GetLocalAnimator` (cached, builds trigger hash/name→index maps), `ReadLocalAnimState`, `ResolveTriggerIndex`, `SendLocalTrigger`, `ApplyTrigger`. `SpawnOrUpdate` now takes the payload. `_localAnim`+trigger maps reset in `RemoveAll`.
  - `MPPatches`: `Postfix_Animator_SetTrigger_Name/_Hash` on `UnityEngine.Animator.SetTrigger(string)`/`(int)` — instance-checked against local animator; routes via `SendLocalTrigger`. Engine-method patch, wrapped in try/catch.
  - `MPServer.HandleAnimTrigger`/`BroadcastAnimTrigger`, `MPClient.HandleAnimTrigger`/`SendAnimTrigger` — trigger relay (reliable). `MPCanvasUI.TickPositionSync` calls `ReadLocalAnimState`.
- Build 2026-05-19 (session 3r): 0 errors, 0 warnings. Deployed both instances. Generic animator sync. (one-off)
- TEST (2026-05-19): generic animator sync confirmed working — remote players animate correctly, deviation gone. (rule)
- NEXT FEATURE: vehicle sync. Breaks into 4 sub-problems: (1) vehicle existence/spawn, (2) transform sync, (3) driver association (character attach/hide on enter/exit), (4) vehicle identity (model+colour). (rule)
- `VehicleManager.cs` — new vehicle subsystem (starts as discovery probe, will grow into the sync). `ProbeVehicles()` called from `TickPositionSync`: (1) `ScanAssembliesForVehicleTypes` — lists every type with a vehicle-ish name; (2) `ProbePlayerMembers` — dumps PlayerController + ThirdPersonCharacter props/methods (reveals the vehicle reference + entry/exit API); (3) `ProbeParentChanges` — live, dumps the character's ancestor chain when its transform parent changes (catches the vehicle GO on enter/exit). (rule)
- Build 2026-05-19 (session 3s): 0 errors, 0 warnings. Deployed both instances. Vehicle discovery probe. (one-off)
- VEHICLE PROBE 1 RESULT (2026-05-19): Driver association — entering a vehicle REPARENTS the whole `Player` GO as a child of the vehicle GO AND sets `Player.activeSelf=false` (character hidden by deactivation); exiting reparents back to `GameManager`, reactivates. Vehicle GO name encodes model (e.g. `VordV150(Clone)`); components: `Rigidbody, CarFeatures, CarController, VehicleController, FuelModuleWrapper, WheelControllerManager, DamageHandler, VehicleDeformationController, ...`. Vehicle classes: `CarController`,`ScooterController`,`VehicleController`,`VehicleInstance`,`VehicleSpawnerController`,`Helpers.VehicleHelper`,`Vehicles.VehicleTypes.VehicleType/TypeName/TypeHelper`,`Data.VehicleColors.VehicleColor`,`Entities.VehicleSlot`. (rule)
- Scooters: confirmed by user — ridden scooter is an ANIMATION (scooter prop part of model, driven by `OnScooter` anim bool) → already synced by the generic animator sync. CARS are real separate objects needing full sync. (rule)
- VEHICLE PLAN: phase 1 done (discovery). Phase 2 = probe #2 (vehicle-system class APIs + spawn/identity). Phase 3 = transform sync + remote spawn + driver association. (rule)
- `VehicleManager.ProbeVehicleSystem` (probe #2) — dumps DeclaredOnly members of VehicleHelper/VehicleSpawnerController/VehicleController/CarController/CarFeatures/VehicleInstance/VehicleTypeHelper/VehicleType/VehicleTypeName/VehicleSlot/VehicleColor; `ProbeDrivenVehicle` — live, dumps the driven vehicle's components + renderers/materials once. Assembly-scan keyword "car" removed (matched noise). (rule)
- Build 2026-05-19 (session 3t): 0 errors, 0 warnings. Deployed both instances. Vehicle probe #2. (one-off)
- VEHICLE PROBE 2 RESULT (2026-05-19): `Helpers.VehicleHelper` (static) is the toolkit — `IsInsideMotorVehicle()→bool`, `GetCurrentVehicle()→VehicleInstance`, `GetCurrentVehicleBase()→VehicleController`, `CreateAndSpawnVehicle(VehicleInstance,Vector3,Quaternion)→VehicleController`, `TeleportVehicle(VehicleController,Vector3,Quaternion)`, `RegisterPlayerVehicle`/`UnregisterPlayerVehicle(VehicleController)`, `LoadVehiclePrefabAsync(VehicleTypeName)`. `VehicleInstance` (global ns) = saved vehicle data: `id`,`vehicleTypeName(VehicleTypeName enum)`,`vehicleColorName(string)`,position/rotation,fuel,etc. `VehicleController` (global ns): `vehicleInstance`,`vehicleType`,`SetFreeze(bool)`,`EnterVehicle/ExitVehicle`. `CarFeatures.SetColor(VehicleColor)`. `VehicleTypeName` enum = 20 models (VordV150, MersaidiS500, Bima320, ElectricScooter, ...). (rule)
- IMPLEMENTED 2026-05-19 (session 3u) — vehicle sync (Phase 3, "real game spawn" per user):
  - `VehicleSync=35` msg + `VehicleSyncPayload{PlayerId,Driving,TypeName,ColorName,VehicleId,X,Y,Z,Qx..Qw}` (full quaternion — cars pitch/roll).
  - `VehicleManager`: `ReadLocalVehicle` (detect via `IsInsideMotorVehicle`, identity from `GetCurrentVehicle`, transform from `GetCurrentVehicleBase`); `ApplyVehicleState`→`SpawnRemoteVehicle` (`new VehicleInstance()` + set id/type/colour → `CreateAndSpawnVehicle` → `UnregisterPlayerVehicle` to keep it out of our save + `SetFreeze(true)` + `Rigidbody.isKinematic=true`); `TickSmoothing` lerps ghost transform; `DespawnRemoteVehicle`/`DespawnAll`.
  - `RemotePlayerManager.SetDriving` hides the remote walking model while driving; despawn hooked into `Remove`/`RemoveAll`.
  - `MPServer`/`MPClient` Handle+Broadcast/Send VehicleSync (reliable relay); `MPCanvasUI.TickPositionSync` sends vehicle state + calls `TickSmoothing`. `VehicleManager.ProbeVehicles` call removed (discovery done; probe methods kept in file).
  - KNOWN GAPS pending test: no visible driver IN the ghost car (remote walking model just hidden); `CreateAndSpawnVehicle` behaviour with a synthetic half-populated `VehicleInstance` unverified (may NPE on null cargo lists — logged).
- Build 2026-05-19 (session 3u): 0 errors, 0 warnings. Deployed both instances. Vehicle sync. (one-off)
- TEST (2026-05-19): vehicle sync worked. User feedback: (1) want parked cars to persist + be visible (not despawn on exit); (2) need ownership concept — only owner can use a car; (3) ghost showed a false "owned" ICON on non-owner's screen (likely the VehicleController.poi point-of-interest). Also: want AI/world traffic synced. User chose "everything incl. AI traffic". (rule)
- IMPLEMENTED 2026-05-19 (session 3v) — Phase 4: owned-vehicle fleet registry:
  - Model change: per-vehicle `VehicleSyncPayload` → fleet model. `VehicleEntry{VehicleId,TypeName,ColorName,Driving,X..Qw}` + `VehicleFleetPayload{OwnerId,List<VehicleEntry>}`. `VehicleSync=35` msg now carries the fleet.
  - `ReadLocalFleet` enumerates `VehicleHelper.AllPlayerVehicles` (List<VehicleController>) → every owned vehicle (parked + driven, `Driving=vc.controlledByPlayer`); sent 10Hz. Fleet is the complete truth for that owner.
  - `ApplyVehicleFleet`: ghosts keyed by VehicleId, PERSIST (no despawn on park); a vehicle absent from the owner's fleet = sold → `DespawnByVehicleId`. `DespawnAllOwnedBy` on owner disconnect.
  - Ownership: ghost spawn now also disables `vc.poi.gameObject` (kills false owned-icon), sets `vc.enabled=false` + CarController disabled (non-interactable — nobody can enter another's car), and adds a world-space `CreateOwnerLabel` ("<owner>'s car", billboarded in TickSmoothing).
  - Late-join handled implicitly: fleets re-broadcast 10Hz so a joiner gets everything within ~100ms.
- Build 2026-05-19 (session 3v): 0 errors, 0 warnings. Deployed both instances. Phase 4 vehicle fleet registry. (one-off)
- VEHICLE NEXT: Phase 5 = AI/world traffic sync (host-authoritative) — needs a discovery probe of the traffic system first. (situational: next work)
- TEST Phase 4 (2026-05-19): parked-car persistence WORKS. BUT: ghost still shows false ownership icon for BOTH players; ghost car was ENTERABLE/drivable by the other player; ghost appears on map everywhere → game genuinely treats `CreateAndSpawnVehicle` output as an owned vehicle. `UnregisterPlayerVehicle` + `poi.SetActive(false)` insufficient. (rule)
- ROOT CAUSE: `CreateAndSpawnVehicle` produces a real game-tracked vehicle; the `VehicleController`/`CarController` components ARE the ownership/IO/ticket/map behaviour. Must destroy them to demote the ghost to a pure prop. (rule)
- IMPLEMENTED 2026-05-19 (session 3w) — ghost demotion: `SpawnRemoteVehicle` now, post-spawn, `UnregisterPlayerVehicle` + destroys `vc.poi.gameObject` + `StripVehicleComponents` destroys VehicleController/CarController/DamageHandler/VehicleDeformationController/WheelControllerManager/FuelModuleWrapper/SpeedLimiterModuleWrapper/FlipOverModuleWrapper/VariableCenterOfMass (visual comps incl. CarFeatures kept). Rigidbody kinematic. Diagnostic logs `AllPlayerVehicles` count before→after-spawn→after-strip + poi name. (rule)
- Build 2026-05-19 (session 3w): 0 errors, 0 warnings. Deployed both instances. Vehicle ghost demotion. (one-off)
- TEST (2026-05-19): ghost demotion WORKS PERFECTLY. Diagnostic `AllPlayerVehicles 0→1→0` confirms clean de-registration. Owner keeps ownership icon + map presence + entry on THEIR screen (real car untouched); non-owners see a stripped prop — no icon, not on their map, not enterable. Key lesson: DESTROYING the gameplay components works where merely disabling them did not. (rule)
- Vehicle ownership model: a player buys cars normally in their own game (real owned vehicle, never touched by the mod). Their game broadcasts `VehicleFleetPayload{OwnerId=self}`; every OTHER machine spawns a stripped ghost tagged with that OwnerId. The owner's machine ignores its own broadcast (`OwnerId==MPConfig.PlayerId` skip) so it never ghosts its own car. Ownership = who broadcast the fleet; cars are never transferred between games. (rule)
- `VehicleManager.ProbeTraffic()` (Phase 5 prep) — discovery probe: assembly scan for traffic-named types; scene scan 25s after level load — counts VehicleControllers (player-owned vs AI), counts CarController/AiCarHorn/AiCarMusic/Pedestrian, dumps a sample AI vehicle's hierarchy + AI-ish component APIs. Called from `TickPositionSync` (self-guards). (rule)
- Build 2026-05-19 (session 3x): 0 errors, 0 warnings. Deployed both instances. AI traffic discovery probe. (one-off)
- TRAFFIC PROBE 1 RESULT (2026-05-19): AI traffic = third-party asset **GleyTrafficSystem** (Gley Traffic System). Does NOT use VehicleController/CarController (0 found). AI cars have `AiCarHorn` (16 live) / `AiCarMusic` (12). KEY: `GleyTrafficSystem.TrafficComponentMultiplayer` exists — Gley's plugin has built-in multiplayer support. Classes: `TrafficManager`,`TrafficVehicles`,`TrafficComponent`,`TrafficComponentMultiplayer`,`TrafficDespawner`,`TrafficLights*`. Plan: leverage Gley's MP mode rather than build traffic sync from scratch. (rule)
- `VehicleManager.ScanGleyApi()` (traffic probe #2) — dumps DeclaredOnly members of GleyTrafficSystem.TrafficManager/API/TrafficVehicles/TrafficComponent/TrafficComponentMultiplayer + AiCarHorn + TrafficDespawner; samples a live AI car (found via AiCarHorn) hierarchy. (rule)
- Build 2026-05-19 (session 3y): 0 errors, 0 warnings. Deployed both instances. Traffic probe #2 (Gley API dump). (one-off)
- TRAFFIC PROBE 2 RESULT (2026-05-19): `TrafficComponentMultiplayer` = Gley's MULTI-CAMERA (split-screen) support (`players` array), NOT network sync. `TrafficManager` (singleton `Instance`) = Burst/Jobs sim, all vehicle state in NativeArrays; key methods: `GetVehicleList()`, `AddVehicle(Vector3,VehicleTypes)`, `RemoveVehicle`, `ClearTraffic()`, `SetTrafficDensity(int)`, `SetPause(bool)`, `UpdateCamera(Transform[])`, `Initialize(Transform[],...)`. `TrafficVehicles` = pool (`allVehicles`,`idleVehicles`,`trafficHolder`), public `GetVehicleList()→List<VehicleComponent>`. `TrafficComponent` (singular `player` anchor) is what the game uses. Traffic cars: under `TrafficHolder`, each has `VehicleComponent`+`Rigidbody`+`CarFeatures`+`RandomVehicleColor`+`TaxiController`(taxis)+etc. Models overlap player VehicleTypeName (FreightTruckT1, HonzaMimic, MersaidiS500...) plus traffic-only (Taxi). (rule)
- USER DECISION (2026-05-19): build FULL host-authoritative traffic sync. ("sync only nearby" rejected — would jar when players converge.) Phase 5a = host enumerate+broadcast traffic, clients disable local + render ghosts. Phase 5b = host simulates traffic citywide around all players (DensityManager anchor problem). (rule)
- `VehicleManager.ScanGleyInternals()` (traffic probe #3) — dumps VehicleComponent/VehiclePool/VehicleTypes/DensityManager/VehiclePositioningSystem/WaypointManager/DrivingAI member APIs — the design data for Phase 5a. (rule)
- Build 2026-05-19 (session 3z): 0 errors, 0 warnings. Deployed both instances. Traffic probe #3 (Gley internals). (one-off)
- TRAFFIC PROBE 3 RESULT (2026-05-19): `VehicleComponent` (per traffic car) — `transform`, `GetVehicleType()→VehicleTypes{Car,Truck}`, `GetIndex()`, `GetCurrentSpeed()`, `GetVelocity()`, `ActivateVehicle`/`DeactivateVehicle`. `VehiclePool.trafficCars(Il2CppReferenceArray)` = traffic-car prefab array. `DensityManager.UpdateCameraPositions(Transform[])` + `AddVehicleAtPosition(Vector3,VehicleTypes)` — the Phase-5b citywide-anchor hooks. `VehicleTypes` enum = just Car,Truck (model identity = GameObject name, e.g. FreightTruckT1(Clone)N). (rule)
- GleyTrafficSystem types live in `ExternalPlugins.dll` (interop) — added that reference to the csproj; the Gley API is directly callable in code (no reflection needed). (rule)
- IMPLEMENTED 2026-05-19 (session 4a) — Phase 5a foundation: `TrafficSync.cs`. `Tick()` (role-based, 25s after level load): host `HostEnumerate()` logs `TrafficManager.Instance.trafficVehicles.GetVehicleList()` (count + sample cars); client `DisableLocalTraffic()` = `SetTrafficDensity(0)`+`ClearTraffic()`. `Reset()` on game load. Wired into `MPCanvasUI` (TickPositionSync + game-load reset). Broadcast + ghost layer is next. (rule)
- Build 2026-05-19 (session 4a): 0 errors, 0 warnings. Deployed both instances. Traffic sync foundation. (one-off)
- TEST 4a (2026-05-19): CORRECTION — earlier claim "BigAmbitions2 has no traffic" was WRONG (overreach from a single one-shot scan that happened to read 0). User confirms BOTH instances always have traffic. The 4a test actually showed: client `DisableLocalTraffic` WORKS (client traffic vanished as intended); the host one-shot enumeration logged 0 due to bad timing, not absent traffic. Either instance can host. (rule)
- `HostEnumerate` hardened: uses `FindObjectsOfType<VehicleComponent>` (robust). `Tick` host enumeration made PERIODIC (every 5s via unscaled timer) instead of one-shot — one-shot snapshots are unreliable. (rule)
- Build 2026-05-19 (session 4b): 0 errors, 0 warnings. Deployed both instances. Periodic host enumeration. (one-off)
- TEST 4b (2026-05-19): host enumeration WORKS — `FindObjectsOfType<VehicleComponent>` gives 24 active traffic cars with stable `index` (=`(Clone)NN` suffix), model name (GameObject name: VordTiaraVic/DeliveryTruck/VordPony/VordV150/UMCNunavut...), VehicleTypes Car/Truck, live position+speed. BUT: client one-shot `SetTrafficDensity(0)`+`ClearTraffic()` did NOT stick — Gley's density manager respawned traffic; client still showed its own traffic. (rule)
- `SuppressLocalTraffic` (client) — runs every frame after 20s: `SetTrafficDensity(0)` + `SetPause(true)` every frame (pinned), `ClearTraffic()` every 2s, logs car count at each clear. (rule)
- Build 2026-05-19 (session 4c): 0 errors, 0 warnings. Deployed both instances. Continuous client traffic suppression. (one-off)
- TEST 4c (2026-05-19): client traffic suppression CONFIRMED (`0 car(s) present at clear`). Host enumeration confirmed (~24 cars). Foundation complete. (rule)
- IMPLEMENTED 2026-05-19 (session 4d) — Phase 5a traffic broadcast + ghosts:
  - `TrafficSnapshot=36` msg + `TrafficCarDto{Index,Model,X..Qw}` + `TrafficSnapshotPayload{Cars}`.
  - Host: `TrafficSync.BuildSnapshot` (FindObjectsOfType<VehicleComponent>, model = GameObject name minus `(Clone)NN`), broadcast every 0.2s (~5Hz) reliable.
  - Client: `ApplySnapshot` — ghosts keyed by Gley pool Index; spawn via `VehicleManager.SpawnVisualGhost` (reused player-ghost spawn: CreateAndSpawnVehicle→demote to prop); despawn cars absent from snapshot; `TickGhosts` lerps.
  - `VehicleManager.SpawnVisualGhost(typeName,pos,rot)` — extracted reusable visual-ghost spawn (Enum.TryParse VehicleTypeName, fallback VordV150 for non-player models e.g. Taxi).
  - `TrafficSync.DespawnAllGhosts` hooked into `RemotePlayerManager.RemoveAll`.
- Build 2026-05-19 (session 4d): 0 errors, 0 warnings. Deployed both instances. Traffic broadcast + client ghosts. (one-off)
- TEST 4d (2026-05-19): traffic broadcast WORKS — client sees host's traffic when players near each other. Issues: (1) client ~0.3s behind + jitter (latency/rate/lerp — partly inherent); (2) traffic doesn't follow client when host leaves area (Phase 5b — Gley spawns only near host); (4) car colours don't match (RandomVehicleColor per-instance); (5) taxis missing — replaced by VordV150 fallback (Taxi not a player VehicleTypeName). (rule)
- IMPLEMENTED 2026-05-19 (session 4e) — traffic ghosts from Gley's own prefab pool (fixes #5 taxis + lighter spawn): `TrafficSync.BuildPrefabMap` reads `TrafficComponent.Instance.vehiclePool.trafficCars` → model→prefab map (handles GameObject[] or Component[] element type defensively); `SpawnTrafficGhost` = `Instantiate(prefab)` + strip Gley/AI/audio/LOD components (`_killTrafficComponents`) + kinematic Rigidbody. Replaces the CreateAndSpawnVehicle path for traffic. Correct models incl. Taxi; much lighter than CreateAndSpawnVehicle. (rule)
- Build 2026-05-19 (session 4e): 0 errors, 0 warnings. Deployed both instances. Traffic ghosts from Gley prefab pool. (one-off)
- TRAFFIC TODO: #2 spawn-following (Phase 5b, host citywide traffic via DensityManager anchors); #4 colour sync; #1 latency tuning. (situational: next traffic work)
- TAXI DESIGN (2026-05-19): taxi = a clickable point that opens a fast-travel menu (pay fee → teleport local player); it does NOT drive you. Movement (host-dictated, ghost-lerped) and interaction (local, per-player, like a bench) ARE separable → host-synced taxi CAN stay functional. Fix = on taxi ghosts keep `TaxiController` + click hook, strip only AI/physics. Caveat: client hailing → host taxi keeps moving → ghost moves during menu (mitigate: freeze ghost while its menu open). (rule)
- `VehicleManager.ProbeTaxi()` — dumps `TaxiController` API + a live taxi's component hierarchy (to learn TaxiController's deps + the clickable hook). Called from `TickPositionSync`, 25s gate, self-guards. (rule)
- Build 2026-05-19 (session 4f): 0 errors, 0 warnings. Deployed both instances. Taxi discovery probe. (one-off)
- BUG: `ProbeTaxi` call was placed AFTER the `!MPServer.IsRunning && !MPClient.IsConnected` gate in TickPositionSync → never ran in a single-instance test. Moved it up into the probe cluster (before the gate). (rule)
- Build 2026-05-19 (session 4g): 0 errors, 0 warnings. Deployed both instances. ProbeTaxi call moved before the connection gate. (one-off)
- TAXI PROBE RESULT (2026-05-19): `TaxiController` is tiny — methods `OnClickToUseTaxi()` (public, click entry → menu), `RequestVehicleStop()` (private, stops taxi), props `_vehicleComponent(VehicleComponent)`, `_lastDriveAction(SpecialDriveActionTypes)`. So a functional taxi ghost = keep `TaxiController` + `VehicleComponent` (the ref it holds) + colliders. (rule)
- IMPLEMENTED 2026-05-19 (session 4h) — Build A: interactable taxi ghosts. `SpawnTrafficGhost` special-cases `model=="Taxi"`: keeps `TaxiController` (enabled) + `VehicleComponent` (disabled — ref valid for TaxiController, dead AI fires nothing); strips the rest. Non-taxi ghosts unchanged. (rule)
- Build 2026-05-19 (session 4h): 0 errors, 0 warnings. Deployed both instances. Interactable taxi ghosts (Build A). (one-off)
- TAXI NEXT (Build B): host-authoritative ref-counted stop — any player hailing a taxi → host stops its real taxi N → ghosts follow → resume when all hailers release. (situational)
- REGRESSION (2026-05-19): build 4e (Gley-prefab ghost spawn) was never tested — it broke client traffic display (`BuildPrefabMap` silently produced an empty map → `SpawnTrafficGhost` returned null → no ghosts). Fix 4i: `BuildPrefabMap` now logs every failure path + the `trafficCars` element type; `SpawnTrafficGhost` falls back to `VehicleManager.SpawnVisualGhost` (CreateAndSpawnVehicle path, the 4d-working one) when the Gley prefab is unavailable — so traffic always shows. (rule)
- Build 2026-05-19 (session 4i): 0 errors, 0 warnings. Deployed both instances. Traffic-ghost fallback + BuildPrefabMap diagnostics. (one-off)
- DIAGNOSIS (2026-05-19): `VehiclePool.trafficCars` is `Il2CppReferenceArray<GleyTrafficSystem.CarType>` — a Gley wrapper class, not GameObject/Component. So the prefab lives INSIDE CarType. Fix 4j: `ExtractPrefab(CarType)` reflects CarType's properties for a GameObject-valued member (prefers names containing prefab/vehicle/car); logs CarType members once. (rule)
- Build 2026-05-19 (session 4j): 0 errors, 0 warnings. Deployed both instances. CarType prefab extraction by reflection. (one-off)
- TEST 4j (2026-05-19): taxi ghosts spawn as real taxis; clicking → player walks to taxi → teleport works. Remaining: (A) host taxi doesn't STOP when client hails (host unaware → ghost keeps moving, hard to reach) = Build B; (B) far from host, client's own local traffic spawns/clears in a war = teleporting/phasing cars. (rule)
- FIX 4k (issue B): client traffic suppression changed from density-0+pause+periodic-ClearTraffic (lost the race in newly-entered grid areas) to disabling the `TrafficManager` component outright (`tm.enabled=false`) + one ClearTraffic — no sim, no spawner, anywhere. Caveat: traffic lights may freeze on the client (cosmetic; client has no local traffic anyway). (rule)
- Build 2026-05-19 (session 4k): 0 errors, 0 warnings. Deployed both instances. Client traffic killed via TrafficManager disable. (one-off)
- TEST 4k (2026-05-19): both fixes confirmed (taxis work, far-from-host teleporting gone). User wants traffic lights synced to host; chose "all 4 remaining items in one go".
- IMPLEMENTED 2026-05-19 (session 4l) — 3 of 4 + 2 probes:
  - TAXI STOP (Build B): `TaxiHail=37` msg + `TaxiHailPayload`. Harmony dynamic patch `Patch_TaxiController_OnClick` (postfix on `TaxiController.OnClickToUseTaxi`) → `TrafficSync.OnLocalTaxiHailed` → client sends `SendTaxiHail(index)` to host (host's own click already stops via SP flow). `MPServer.HandleTaxiHail` → `TrafficSync.HostStopTaxi(index)` finds the real traffic taxi by `VehicleComponent.GetIndex()`, reflection-invokes private `TaxiController.RequestVehicleStop()`. `ResolveTaxiIndex` = ghost reverse-lookup (client) / VehicleComponent index (host).
  - CITYWIDE (Phase 5b): `TrafficSync.UpdateTrafficAnchors` (host, every frame) feeds all player transforms (host + remote-player ghosts via `RemotePlayerManager.GetRemotePlayerTransforms`) to `TrafficManager.densityManager.UpdateCameraPositions(Il2CppReferenceArray<Transform>)`.
  - LATENCY: traffic broadcast 0.2s→0.1s (10Hz); GhostLerp 12→14.
  - PROBES: `VehicleManager.ProbeTrafficExtras` dumps GleyTrafficSystem intersection/light classes + RandomVehicleColor/VehicleColor (for lights sync + colour sync next build).
- Build 2026-05-19 (session 4l): 0 errors, 0 warnings. Deployed both instances. Taxi stop + citywide + latency + lights/colour probe. (one-off)
- TEST 4l: taxi stop failed (`RequestVehicleStop method not found` — `FindComponentByName` returns base `Component`, so `GetType()` gave `Component` not `TaxiController`); citywide failed silently (`UpdateCameraPositions` is the wrong anchor lever). Latency better. (rule)
- USER DIRECTIVE (2026-05-19, firm): vehicles incl. taxis must NOT desync — taxi stop stays HOST-AUTHORITATIVE as agreed. Do NOT change an approach the user specified without explicit permission. A mid-implementation pivot to a local-freeze (desync) was reverted. (rule)
- FIX 4m: taxi stop reflection — `TaxiController` IS referenceable (global ns, `BigAmbitions.dll`). `HostStopTaxi` now `tcComp.TryCast<TaxiController>()` then `typeof(TaxiController).GetMethod("RequestVehicleStop", NonPublic|Instance).Invoke(taxi,null)`. Citywide: `UpdateTrafficAnchors` now calls `TrafficManager.UpdateCamera` AND `densityManager.UpdateCameraPositions` with all player anchors + one-time diagnostic. (rule)
- Build 2026-05-19 (session 4m): 0 errors, 0 warnings. Deployed both instances. Host-authoritative taxi stop (fixed) + citywide retry. (one-off)
- TEST 4m: taxi stop WORKS (host-authoritative, synced). Citywide `UpdateCamera` WORKS — traffic follows the client — but SPARSE (denser near host). Taxi doesn't auto-resume (stuck). (rule)
- FIX 4n: (1) taxi resume — `HostStopTaxi` now schedules `_taxiResumeAt[index]=now+18s`; `TickTaxiResumes` (host, each frame) invokes the game's resume closure `TaxiController._OnClickToUseTaxi_b__8_0()` when due. `FindTaxiByIndex` shared helper. (2) sparseness — `UpdateTrafficAnchors` now also calls `densityManager.UpdateMaxCars(24 * playerCount)` so the traffic budget scales with player count. (rule)
- Build 2026-05-19 (session 4n): 0 errors, 0 warnings. Deployed both instances. Taxi auto-resume + player-scaled traffic density. (one-off)
- TEST 4n: density fix CONFIRMED (far-from-host traffic good now). Taxi resume WRONG — `_OnClickToUseTaxi_b__8_0` is the MENU-open callback, not resume; invoking it popped the host's taxi menu. (rule)
- FIX 4o: `HostResumeTaxi` now restores the drive action directly — reflection-reads TaxiController's `_vehicleComponent` + `_lastDriveAction` (saved by RequestVehicleStop), calls `VehicleComponent.SetCurrentAction(lastDriveAction)`. The direct inverse of the stop, no menu. (rule)
- Build 2026-05-19 (session 4o): 0 errors, 0 warnings. Deployed both instances. Taxi resume via drive-action restore. (one-off)
- TEST 4o: taxi resume failed — `VehicleComponent.SetCurrentAction` only updates the component's local copy; Gley's Burst job reads drive state from `TrafficManager`'s NativeArrays, not the component. (rule)
- FIX 4p: `HostResumeTaxi` now reflection-invokes private `TrafficManager.UpdateDrivingState(int index, SpecialDriveActionTypes, float)` with the taxi's saved `_lastDriveAction` + `_lastActionValue` — writes the job-level state, same layer RequestVehicleStop operates at. If this also fails, fallback = `TrafficManager.RemoveVehicle(index)` (despawn the stuck taxi). (rule)
- Build 2026-05-19 (session 4p): 0 errors, 0 warnings. Deployed both instances. Taxi resume via TrafficManager.UpdateDrivingState. (one-off)
- TEST 4p: taxi resume CONFIRMED working. User added a 3rd item: client traffic shows vehicles "very quickly moved across the screen" (jarring slides). (rule)
- IMPLEMENTED 2026-05-19 (session 4q):
  - JARRING FIX: pool-index reuse caused ghosts to slide to a reused slot's far car. `ApplySnapshot` now tracks `TrafficGhost.Model` — respawns on model mismatch; snaps the transform (no slide) on a position jump > `SnapDistance` (18u).
  - TRAFFIC LIGHTS: `TrafficLights=38` msg + `LightStateDto{Index,Road,Yellow}` + `TrafficLightsPayload`. Host `BuildLightSnapshot` reads `IntersectionManager.allIntersections` → each `TrafficLightsIntersection.currentRoad`/`yellowLight`, broadcasts every 0.5s. Client `ApplyTrafficLights` → `ChangeAllRoadsExceptSelectd(road,Red)` + `ChangeCurrentRoadColors(road,Yellow|Green)` + `ApplyColorChanges()`.
  - COLOUR PROBE: `VehicleManager.ProbeCarColor` dumps a traffic car's renderers/materials — Color shader props with shared-material AND MaterialPropertyBlock values — to locate where the body colour lives.
- Build 2026-05-19 (session 4q): 0 errors, 0 warnings. Deployed both instances. Jarring fix + traffic-light sync + colour probe. (one-off)
- TEST 4q: jarring GONE, lights SYNC. But colour probe never fired — `FindGameType("VehicleComponent")` returns null (it's `GleyTrafficSystem.VehicleComponent`, namespaced; FindGameType needs the full name). Fix 4r: VehicleManager `using GleyTrafficSystem` + `Il2CppType.Of<VehicleComponent>()` directly. (rule)
- Build 2026-05-19 (session 4r): 0 errors, 0 warnings. Deployed both instances. ProbeCarColor namespace fix. (one-off)
- COLOUR PROBE RESULT (2026-05-19): traffic car body renderer (`<Model>_Body`) uses shader `Shader Graphs/SH_Vehicle` with TWO custom Color props named `Color_<guid>` (non-`_`-prefixed) = the car's tint + fresnel. MaterialPropertyBlocks are EMPTY → colour is on instanced materials. (DeliveryTruck/Taxi etc. = fixed-livery shared materials — harmless to sync.)
- IMPLEMENTED 2026-05-19 (session 4s) — traffic colour sync: `TrafficCarDto` += R1/G1/B1/R2/G2/B2. Host `GetVehicleBodyColors` reads the 2 non-`_` Color props off the SH_Vehicle body material (cached per index+model in `_carColorCache`); client `ApplyVehicleBodyColors` writes them onto the ghost's instanced SH_Vehicle material on spawn. (rule)
- Build 2026-05-19 (session 4s): 0 errors, 0 warnings. Deployed both instances. Traffic car colour sync. (one-off)
- TEST 4s: colour sync did NOT match — the colour probe had sampled a DeliveryTruck (fixed-livery, shared material, empty MPB) — a misleading sample. Regular cars likely store colour in the `CarFeatures` MaterialPropertyBlock. (rule)
- FIX 4t: `ProbeCarColor` reworked — skips fixed-livery models (Truck/Taxi/Ambulance/Police/Delivery/Freight), samples up to 3 ordinary cars, dumps only the SH_Vehicle body renderers (shared + MPB values). Re-probe to find where a regular car's colour actually lives. (rule)
- Build 2026-05-19 (session 4t): 0 errors, 0 warnings. Deployed both instances. ProbeCarColor — sample regular cars. (one-off)
- COLOUR PROBE 2 RESULT (2026-05-19): regular cars (VordTiaraVic, UMCNunavut) — the per-car body colour is in the renderer's **MaterialPropertyBlock** (two cars, same shared `M_TaxiCivVersion` material, DIFFERENT mpb values). The active colour has alpha≈1; the inactive fallback alpha 0. Fixed-livery vehicles keep colour on the shared material. (rule)
- FIX 4u: `GetVehicleBodyColors` reads from `renderer.GetPropertyBlock` (MPB), picks MPB-vs-shared by alpha≥0.5. `ApplyVehicleBodyColors` writes via `GetPropertyBlock`→`SetColor`→`SetPropertyBlock` (was writing the instanced material — which the shader ignores in favour of the MPB). (rule)
- Build 2026-05-19 (session 4u): 0 errors, 0 warnings. Deployed both instances. Traffic colour sync via MaterialPropertyBlock. (one-off)
- TEST 4u: colours STILL don't match; jarring movement seen once or twice (mostly fixed, not fully). (rule)
- FIX 4v: `GetVehicleBodyColors` was reusing a STATIC MaterialPropertyBlock → block-less renderers leave stale values → wrong colour cached permanently. Now uses a fresh MPB per read. Added `[TrafficColor]` diagnostics: host logs first 8 cars' read colours, client logs first 8 ghosts' applied colours + renderer count. SnapDistance 18→12 (catch nearer index-reuse slides). (rule)
- Build 2026-05-19 (session 4v): 0 errors, 0 warnings. Deployed both instances. Colour: fresh MPB + diagnostics; tighter snap. (one-off)
- COLOUR DIAGNOSIS (2026-05-19): `[TrafficColor]` logs proved host-read == client-applied EXACTLY (pipeline perfect). BUT multiple cars read identical `0.737/0.482` = the PRE-COLOUR DEFAULT. Root cause: `RandomVehicleColor` colours a car a moment AFTER spawn; the host reads+caches on first sight → caches the default permanently. (rule)
- FIX 4w: (1) host caches a car's colour only once `RandomVehicleColor._initialized` is true (`IsCarColorReady`; fixed-livery vehicles = no RVC = ready immediately). (2) `TrafficGhost` stores last-applied C1/C2; `ApplySnapshot` re-applies colour to an existing ghost when it changes — so a late-corrected colour reaches a ghost that spawned early. (rule)
- Build 2026-05-19 (session 4w): 0 errors, 0 warnings. Deployed both instances. Colour: wait for RandomVehicleColor._initialized + re-apply on change. (one-off)
- TEST 4w: colours STILL don't match after the timing fix. (rule)
- DIAGNOSTIC 4x: added `DumpFullCarColor` — host dumps real car[N]'s full SH_Vehicle body-colour state (every Color prop, shared + MPB), client dumps ghost[N]'s; first 4 indices each, tagged `[ColorDump] HOST/CLIENT idx=N`. Diffing host vs client for the same idx pinpoints the exact differing property. No behaviour change this build. (rule)
- Build 2026-05-19 (session 4x): 0 errors, 0 warnings. Deployed both instances. Colour dump-and-compare diagnostic. (one-off)
- COLOUR DUMP RESULT 4x (2026-05-19): host car[N] and client ghost[N] dumps are BYTE-IDENTICAL → sync is perfect. BUT VordV150 and VordPony BOTH have `Color_3d0f`/`Color_f78f` MPB = `0.737/0.482` (identical) — so those 2 props are a CONSTANT, NOT the per-car paint colour. The real paint colour is a property the dump missed (only dumped Color-type; tint is likely a Vector or Float shader prop). (rule)
- DIAGNOSTIC 4y: `DumpFullCarColor` widened — dumps EVERY shader property (Color/Vector/Float, shared + MPB) of the body renderer (skips wheels). Diffing VordV150 vs VordPony reveals which property is the actual paint colour. (rule)
- Build 2026-05-19 (session 4y): 0 errors, 0 warnings. Deployed both instances. Colour diagnostic — all property types. (one-off)
- COLOUR ROOT CAUSE FOUND (2026-05-19): 4y dump — ghost idx=16 `Color_3d0f` MPB = (0.773,0.639,0.149) real gold → `Color_3d0f`/`Color_f78f` ARE the right props (earlier 0.737/0.482 = pre-colour default). KEY: host log had ZERO `[ColorDump HOST]` lines → host never cached ANY colour → broadcast white for every car → client re-apply-on-change compares vs white default → `white==white` → never applied → ghosts kept their own RandomVehicleColor random colours. Cause: `IsCarColorReady` keyed on `RandomVehicleColor._initialized` which is ALWAYS false. (rule)
- FIX 4z: removed `IsCarColorReady`/`_initialized` gate; replaced with a time delay — host caches a car's colour 2.5s after first seeing it (`_carFirstSeen`, `ColorReadDelay`), enough for RandomVehicleColor to finish. Client re-apply-on-change then propagates the real colour once it arrives. (rule)
- Build 2026-05-19 (session 4z): 0 errors, 0 warnings. Deployed both instances. Colour: time-delayed read (replaces broken _initialized gate). (one-off)
- COLOUR REAL ROOT CAUSE (2026-05-19): diffed two ColorDumps of the SAME index 14 — `Color_3d0f` MPB = (0.235,0,0) red one moment, (0.424,0.424,0.424) grey another (`_Dirtiness`/`_BlinkerOffset` also differ → genuinely two different cars). Gley REUSES pool indices: a car despawns, a different car (often SAME model) spawns at the same index. `_carColorCache` keyed by index+model never invalidated on same-model reuse → host broadcast the FIRST car's colour forever for that slot. The 2 colour props were always correct; the cache was stale. (Earlier "0.737/0.482 pre-colour default" theory was wrong — that was the static-MPB-reuse bug, fixed in 4v. The `_initialized` gate and 2.5s delay were fixes for a non-problem.) (rule)
- FIX 5a: removed `_carColorCache` + `_carFirstSeen` + delay entirely. `GetVehicleBodyColors(index,go)` now reads the colour LIVE every snapshot (caches only the body Renderer ref per index — pooled GO, stays valid). No cache = no staleness. Client re-apply-on-change propagates recycled colours. (rule)
- Build 2026-05-19 (session 5a): 0 errors, 0 warnings. Deployed both instances. Colour: live per-snapshot read (no cache). (one-off)
- TEST 5a: traffic colours match — except a box truck's CAB (separately-coloured from the cargo box). Cause: one body colour read + applied to ALL SH_Vehicle renderers; box-trucks have 2 distinct-coloured renderers. (rule)
- FIX 5b: per-renderer colour sync. `TrafficCarDto.Colors` = `List<float>` (6 per SH_Vehicle renderer group). Host `ReadBodyColors` reads each SH_Vehicle renderer (collapses to 1 group if uniform — regular cars). Client `ApplyVehicleBodyColors` applies group i → renderer i (same prefab → matching order), or the single group to all. `_carRenderers` caches the per-index renderer list. (rule)
- Build 2026-05-19 (session 5b): 0 errors, 0 warnings. Deployed both instances. Per-renderer traffic colour sync. (one-off)
- TEST 2 (2026-05-18, fixed monitor): clock-rate monitor FIRED — bench skip = `58.4 game-min/s` (~1 game-hr/real-sec), a FAST RAMP not a teleport. `timeScale=0.000` throughout — bench skip is a pure internal-clock advance, GSC `isFastForwarding` stays False. (rule)
- ROOT CAUSE suppression never worked: `SetGameTime` passed the fractional float `hourOfDay` into `GameInstance.Hour` which is an **Int32** property → "Single cannot be converted to Int32" thrown every frame → clock-revert was a no-op (logged "Reverted X h" growing 0.06→0.73+ because nothing was actually put back). (rule)
- FIX: `SetGameTime` now splits fractional hour into `int hourInt` + `float minute` and writes Hour(Int32) and Minute(Single) to their correctly-typed properties separately. Probe now logs Day/Hour/Minute `.CanWrite`. (rule)
- Build 2026-05-18 (session 2d): 0 errors. Deployed both instances. Fixed SetGameTime Int32/Single type mismatch. (one-off)

DONE (this session):
- Game time sync framework: protocol, server broadcast, client receive/apply, reflection probe
- Toggleable in-game player HUD (F9): top-right overlay, local=green, remote=white
- Periodic market sync every 60s (host)
- Consensus time-skip: ClickSleep intercepted, suppression via GameManager.Update postfix, vote/start/end protocol, snap-sync on end
- Pause consensus: any player pausing pauses everyone; only the pauser needs to explicitly unpause
- Skip-time waiting indicator: bottom-center overlay, auto-shown while waiting for consensus, "X / Y players ready"
- Pause bug fix: removed `MarkAllWantPaused` — only the pausing player is tracked, force-paused players are not required to unpause
- GSC probe + Harmony TogglePause capture + diagnostic logging in clock-rate monitor

NEXT:
- **Bug 1 test (client unpauses while host still paused)**: both players pause → client unpauses → game should stay paused because host is still in `_pausedPlayers`. If bug persists, check whether `GSC.Paused` monitor in `TickTimeScaleMonitor` fires correctly for client when force-paused.
- **Bug 2 test (bench skip)**: one player opens bench GUI → clicks "skip time ahead" → check BOTH logs for `[Patch] GSC.Set fired with isFastForwarding=true` AND `[UI] isFastForwarding: false→true`. If not seen, `isFastForwarding` may not flip on bench skip → investigate `TemporalBoost` / `timeEnteredTemporalBoost` on GameInstance.
- After bench skip: check if time suppression holds (clock doesn't advance for non-consenting player)
- After Bug 2 confirmed: discover other time-skip triggers (school, other "wait" activities)
- Verify GSC instance is cached via `Patch_GSC_Set` BEFORE TogglePause fires (check log order)

2026-05-19 — Box-truck cab colour FIX (Step 1 diagnostic → Step 1b multi-mat → Step 3 fix):
- ROOT CAUSE: a single `Renderer.sharedMaterial` only returns sub-mesh slot 0. The Freightliner's body MeshRenderer has 3 slots: [0] `M_Freightliner Truck_Back` (HDRP/Lit, trailer), [1] `M_Freightliner Truck_Cabin` (SH_Vehicle, the recoloured cab), [2] `M_GlassTransCars` (HDRP/Lit, windows). Our color sync only looked at slot [0] and so never saw or wrote the cab material → ghost cab got whatever colour the client's local `RandomVehicleColor` happened to roll. Confirmed: HOST cab MPB = RGBA(0.376, 0.376, 0.376) gray, CLIENT cab MPB = RGBA(0.059, 0.133, 0.282) dark blue. (rule)
- `Renderer.sharedMaterials` (plural, IL2CPP `Il2CppReferenceArray<Material>`) returns all sub-mesh slots — use this, not `sharedMaterial`, whenever a single renderer can host multiple meshes/materials (trucks, multi-paint vehicles). (rule)
- `MaterialPropertyBlock` is per-Renderer, NOT per-material-slot. Setting `Color_3d0f...` via MPB on a renderer affects every material slot whose shader has that property. So we only need ONE SH_Vehicle material on a renderer to discover property names, and ONE MPB write per renderer to apply the colour to every SH_Vehicle slot. (rule)
- FIX: `FindShVehicleMaterial(Renderer)` scans `sharedMaterials[]` for the first SH_Vehicle slot; `GetCarRenderers`, `ReadRendererColors`, `ApplyVehicleBodyColors` now all use it instead of `sharedMaterial`. (rule)
- DeliveryTruck (UPS): 1 SH_Vehicle renderer in slot [0] (`M_UPSTruckBody`), 131 total renderers because the prefab embeds a full driver character (hair/beard/face/clothes). Driver renderers have `(Instance)` material suffix on host but not on client ghost — character visuals diverge, but invisible at normal play distance. (rule)
- FreightTruckT1: 0 SH_Vehicle renderers at slot [0] (back is HDRP/Lit); cab is at body slot [1]. Without the multi-slot fix, sync silently broadcast white and the ghost showed a random local colour. (rule)
- Diagnostic `DumpFullCarColor` now iterates `sharedMaterials[]` (tagged `[i.j]`) and dumps Texture references too — kept in code for future investigations. (rule)
- CONFIRMED 2026-05-19: Freightliner cab now matches host↔client in-game. Traffic colour sync complete for all observed truck variants. (rule)

2026-05-19 — Backlog cycle: items #1, #2, #5 deployed.
- #1 (name persistence): MPConfig.SetRuntime writes the chosen PlayerId back to BepInEx config (auto-saved on assign). MPMenuUI.Awake reads MPConfig.PlayerId into _name so the F8 panel pre-fills with the last-used name. (rule)
- #2 (char-gen default name): New MPCanvasUI.TickIntroNamePrefill watches for Intro.IntroCharacterCustomizer, scans its TMP_InputField children, logs each ([IntroName] tag), and pre-fills empty fields with MPConfig.PlayerId (splits on first space for first/last-name forms). Never clobbers typed text. Re-armed on game (re)load. (rule)
- IntroCharacterCustomizer's namespace is `Intro` (use `using Intro;`). (rule)
- #5 (taxi lockup): TaxiController.TaxiTravel uses fast-forward to advance the world clock through the trip; our world-clock pinner reverted every advance so the ride hung. Fix: Patch_TaxiController_TaxiTravel.Prefix → TrafficSync.OnTaxiTravelStart sets LocalInTaxi=true; Patch_TaxiController_CompletedTaxiRide.Prefix → OnTaxiTravelEnd clears it. TickWorldClock skips suppression and slides its measurement window while LocalInTaxi, so the post-ride clock isn't re-detected as a skip. (rule)
- TaxiController members confirmed via DLL string search: OnClickToUseTaxi, TaxiTravel, CompletedTaxiRide, isFastForwarding, HasFastForward, taxiTravelButton(Text). (rule)
- NEXT in backlog: #7+#6 (interior/exterior split — host entering building kills traffic + client black-screens on building entry; likely same root cause), then #4 (vehicles avoid clients), then #3 (parked vehicle sync).

2026-05-19 — Backlog cycle: items #7 fix + diagnostic, #6 diagnostic only, #4 fix deployed.
- #7 (host building entry kills world traffic): hypothesis — host's character transform moves to interior position when they enter a building, so traffic anchors point inside (no roads). Fix: TrafficSync.LocalInBuilding flag; UpdateTrafficAnchors omits host's transform while flag is set. Patches on EnteredBuilding (Postfix) / ExitFromBuilding (Prefix) flip the flag. (rule)
- #6 (client black-screen on building entry): diagnostic only this round — Harmony patches on EnterBuildingCoroutine / ExitFromBuildingCoroutine log start; EnteredBuilding postfix logs success. After test we'll know if the coroutine ever starts on the client, if it completes, or where it stalls. (situational: until logs collected)
- New helper VehicleManager.FindAllMethodsByName(string) returns every method with that name across all loaded assemblies (logs each), used by Harmony patches whose declaring type might be one of several controllers (CityBuildingController, CasinoBuildingController, etc.). Each match becomes its own Harmony patch via the TargetMethods() pattern. (rule)
- #4 (vehicles avoid clients): RemotePlayer root now has a CapsuleCollider (h=1.8, r=0.4, center y=0.9, non-trigger) + kinematic Rigidbody (no gravity), and the root's layer is set to match the local character's layer. Gley raycasts `playerLayer` to detect the host; matching layer + a real collider makes remote players visible to traffic the same way. (rule)
- NEXT: collect [Building] logs from the client side to diagnose #6. After #6 is understood, #3 (parked vehicle sync — the biggest item).

2026-05-19 — Iteration 2 on backlog items:
- #1 (name didn't persist on first try): BepInEx 6 BE's auto-save-on-Value-assign doesn't always flush before a hard process exit (X-button). FIX: SetRuntime now calls `_playerIdEntry.ConfigFile?.Save()` explicitly. (rule)
- #5 (taxi still locked up — root cause #2): MPCanvasUI.LateUpdate force-writes `Time.timeScale = 1` every frame, which CLAMPED the cab's intended ~8× fast-forward back to 1×. Suppressing the world-clock pinner wasn't enough — also needed to skip the timeScale pin during taxi. FIX: LateUpdate returns early when `TrafficSync.LocalInTaxi` is set. (rule)
- #7 (still killed traffic — root cause: solo testing): My anchor-drop only helps when a client outside provides an exterior anchor. In solo, removing host's anchor left zero → Gley stopped spawning. FIX: persistent "ghost anchor" GameObject (`BAMP_TrafficGhostAnchor`, DontDestroyOnLoad) pinned at the host's LAST outside position; used whenever LocalInBuilding=true. Reset cleared on game (re)load. (rule)
- Lesson: when patching unverified hypotheses, always also account for OTHER MP-mod mechanisms touching the same state. The world-clock pinner AND the timeScale pinner BOTH suppress fast-forward; one fix without the other still hangs. (rule)

2026-05-19 — Iteration 3: critical Harmony failure fix.
- ROOT CAUSE of why iterations 1 & 2 mostly didn't work: my Patch_TaxiController_TaxiTravel.TargetMethod() returned null because TaxiTravel is NOT on TaxiController (likely on PermanentTaxiController or similar). Harmony treats `null` from TargetMethod as a HARD error and aborts ALL of PatchAll → silently drops every other Harmony patch in the assembly (taxi-stop, ClickSleep, GSC.Set, EnteredBuilding, ExitFromBuilding, Animator.SetTrigger, etc). The plugin still partially loaded (MPCanvasUI was already attached) so non-Harmony features (F8 panel, position sync, intro-name prefill, remote-player colliders) worked, masking the catastrophe. (rule)
- FIX 1: Patch_TaxiTravel / Patch_CompletedTaxiRide now use `TargetMethods()` (plural) returning `FindAllMethodsByName(name)`. Empty enumerable is legal — patch is just skipped if no matches. Pattern from the building patches I added in this session. (rule)
- FIX 2: Plugin.Load wraps `_harmony.PatchAll()` in try/catch so any single broken patch still leaves the rest applied. Logs `[Plugin] PatchAll completed: N methods patched` on success so we can spot count regressions. (rule)
- The F8 panel is in MPCanvasUI.cs (uGUI/Canvas implementation). MPMenuUI.cs was an OLD IMGUI prototype that was never instantiated — confirmed by grepping the codebase for `MPMenuUI` references (only the file itself and the context log mention it). DELETED MPMenuUI.cs to avoid future confusion. My iteration 1 #1 fix went to that dead file by mistake. (rule)
- #1 actual fix: MPCanvasUI.Awake now reads MPConfig.PlayerId/Port/HostIP into _name/_port/_ip before the canvas builds. Logs `[UI] F8 panel pre-fill: name='X'` on startup so we can verify pre-fill happened. (rule)

2026-05-19 — Iteration 4: PatchAll-aborts-globally fix + Harmony class isolation.
- OBSERVED in iteration-3 log: TaxiTravel lives on `UI.InGameUI.BuildingResume`, NOT TaxiController. There is no CompletedTaxiRide method anywhere in the loaded assemblies. (rule)
- ROOT CAUSE of #7 still failing in iteration 3: Harmony's `PatchAll()` iterates `[HarmonyPatch]` classes via `CollectionExtensions.Do`, and the first throw ABORTS the whole sequence. `Patch_CompletedTaxiRide.TargetMethods()` returned empty IEnumerable → Harmony threw `ArgumentException: Undefined target method` → PatchAll iteration aborted at that point → every `[HarmonyPatch]` class ordered AFTER it (Patch_EnteredBuilding, Patch_ExitFromBuilding, Patch_EnterBuildingCoroutine, Patch_ExitFromBuildingCoroutine) silently never applied. My iteration-3 try/catch around PatchAll() caught the outer throw, but couldn't undo the abort. (rule)
- FIX 1: Replace `_harmony.PatchAll()` with manual per-class iteration in Plugin.Load — `foreach type with [HarmonyPatch] attribute: try { _harmony.CreateClassProcessor(type).Patch(); } catch { log }`. Now one bad class can't take down the rest. Logs `[Plugin] Patched <Name>: N method(s)` per class plus a summary line. (rule)
- FIX 2: Removed Patch_CompletedTaxiRide entirely. Added Postfix to Patch_TaxiTravel that clears LocalInTaxi — since TaxiTravel is the ride coroutine itself, Postfix fires when the coroutine completes (= ride ended). (rule)
- DIAGNOSTIC marker `// CLAUDE-DIAGNOSTIC` on the per-class patch-count log in Plugin.cs, per user's debugging-protocol convention.
- NEW LESSON: Harmony's `PatchAll()` provides NO failure isolation between patch classes. For any plugin with dynamic / discovery-based TargetMethods, prefer manual per-class iteration. (rule)

2026-05-19 — Backlog #7 FIXED.
- OBSERVED: world scene + TrafficManager singleton are alive throughout host's building entry. The traffic kill is via `TrafficManager.SetPause(true)` + `TrafficManager.ClearTraffic()` called BEFORE the entry signal (`BuildingManager.DelayedEnterBuildingActions`) fires. Across a whole session those were the only invocations of those methods. (rule)
- FIX: in MP, unconditionally Prefix-skip `TrafficManager.ClearTraffic`, `ClearTrafficOnArea`, and `SetPause(true)` while `MPServer.IsRunning`. `SetPause(false)` passes through. Traffic stays alive for clients regardless of host building visits. Confirmed by user 2026-05-19. (rule)
- Entry-flag flipper: `BuildingManager.DelayedEnterBuildingActions` is the actual on-foot entry signal — `EnterBuildingCoroutine` is patched but its Prefix never fires (likely IL2CPP inlining). Defensive secondary handlers on `EnterBuildingWithVehicle` / `EnterParking` cover vehicle/parking entry paths. (rule)
- CLEANUP: removed all `CLAUDE-DIAGNOSTIC`-marked code from this investigation — `TickBuildingDiag` heartbeat+scene+TM watcher in MPCanvasUI, the never-firing patch classes (`Patch_EnterBuildingCoroutine`, `Patch_EnteredBuilding`, `Patch_CmdEnterBuilding`, `Patch_EnterParkingCoroutine`, `Patch_EnterBuildingCoroutine_Flag`), and the now-stale comment blocks about LocalInBuilding gating. The kept code has plain explanatory comments. (one-off)

2026-05-19 — Backlog #3 (parked-vehicle sync) phases 3a + 3b + 3c shipped together.
- OBSERVED: 558 ParkingLaneGenerator instances; Helpers.ParkingSimulator is the static pool keyed by vehicle name (string); RequestParkedVehicle/ReleaseParkedVehicle are the only spawn/release entry points. (rule)
- Architecture (host-authoritative snapshot, like TrafficSync): Harmony Postfix on ParkingSimulator.RequestParkedVehicle adds the GameObject to ParkedVehicleSync._hostTracked on the host. Prefix on ReleaseParkedVehicle removes it. Every 3s the host builds a ParkedSnapshotPayload (Key=GameObject.GetInstanceID, Model, Pos, Rot, SH_Vehicle Colors) and broadcasts ParkedSnapshot (MessageType=39). (rule)
- Client receives ParkedSnapshot → ApplySnapshot diffs against _clientGhosts: missing Keys → ParkingSimulator.RequestParkedVehicle(model) from the client's local pool; stale Keys → ReleaseParkedVehicle back to pool. Position/rotation/color applied per snapshot. (rule)
- Client suppression: Patch_GenerateParkedVehicles Prefix returns false when MPClient.IsConnected, so the client's lane RNG doesn't spawn competing cars. (rule)
- Color machinery (SH_Vehicle two-Color-per-renderer reader/applier) duplicated in ParkedVehicleSync rather than refactoring out of TrafficSync — same logic, kept independent for simpler change. May factor later. (one-off)
- IL2CPP gotcha: `new UnityAction(MethodGroup)` ctor wants IntPtr not method-group — would have needed a delegate-bridging shim to subscribe to ParkingSimulator.ParkingLaneRegeneration. Switched the entire capture path to static-method Postfixes which sidesteps the issue and is simpler / more robust. (rule)
- NEXT: in-game test confirms snapshot arrives at client and ghosts appear at matching positions/models/colors. If client ghosts mismatch host visuals, the most likely culprit is the SpawnGhost path — client's `Helpers.ParkingSimulator.RequestParkedVehicle` may need its own pool init.

2026-05-20 — Cpp2IL decompile breakthrough on client-can't-exit-building bug.
- Cpp2IL pre-release `2022.1.0-pre-release.21` (downloaded to `/c/code/cpp2il/v2022.1/Cpp2IL.exe`) supports IL2CPP metadata v31.1, which 2022.0.7 stable does not. Use `--use-processor attributeanalyzer,attributeinjector,callanalyzer,nativemethoddetector` + `--output-as dll_il_recovery`; decompile resulting DLLs with `ilspycmd 9.1.0.7988` (newer versions need .NET 9). Method bodies are still `throw null` stubs, but `[CalledBy] / [Calls] / [CallerCount]` attributes give the static call graph — that's what's actionable. (rule)
- ROOT CAUSE LEAD: `ExitZone.OnTriggerEnter` is NOT the building-exit trigger. It only adds the collider to a list and calls `UpdateDoorState` (door animation). The real exit handler is `ExitZoneDespawner.OnTriggerEnter` — its `[Calls]` set: `CompareTag`, `PlayerController.NavigationDisabled`, `VehicleHelper.IsInsideMotorVehicle`, `BuildingManager.IsInsideBuilding`, `UndergroundParkingManager.IsInsideParking`, `PlayerHelper.ItemInHands/HasPaidForAllItems/CanLeaveHome`, then `StartCoroutine(BuildingManager.ExitFromBuildingCoroutine)` or `KickOutPlayer` / `ExitParking`, with `Notifications.Show` for refusal paths. (rule)
- Implication: the `// CLAUDE-DIAGNOSTIC` exit-trigger probe in MPCanvasUI was watching the wrong class. The bug is either (a) the client's player collider never enters the ExitZoneDespawner trigger volume, OR (b) it enters but one of the above guards rejects it. A Harmony Prefix on `ExitZoneDespawner.OnTriggerEnter` that logs `other.name`, `other.tag`, `other.gameObject.layer`, and snapshots `BuildingManager.IsInsideBuilding`, `PlayerController.NavigationDisabled`, `VehicleHelper.IsInsideMotorVehicle`, `PlayerHelper.HasPaidForAllItems`, `PlayerHelper.CanLeaveHome` will pinpoint which one. (situational: until exit-bug fix lands)
- BuildingManager full decompile saved at `/c/code/cpp2il/BuildingManager-decompiled.cs` (2270 lines); ExitZone at `/c/code/cpp2il/ExitZone-decompiled.cs`; ExitZoneDespawner at `/c/code/cpp2il/ExitZoneDespawner-decompiled.cs`. Diffable C# tree (signature-only, no bodies — but a fast cross-class grep target) at `/c/code/cpp2il/output-2022.1/DiffableCs/`. (rule)

2026-05-20 — Deploy checkpoint: ExitZoneDespawner + ExitFromBuilding diagnostic patches shipped.
- Two new Harmony Prefix patches in MPPatches.cs marked `// CLAUDE-DIAGNOSTIC`: `Patch_ExitZoneDespawner_OnTriggerEnter_Diag` (uses FindGameType("ExitZoneDespawner") + GetMethod("OnTriggerEnter") since the type is in global namespace) and `Patch_ExitFromBuilding_Diag` (FindAllMethodsByName since multiple classes may host the name). Both log SIDE = HOST/CLIENT/SP plus collider tag/layer/GameObject name. (situational: until exit-bug fix lands)
- A/B decision tree for the next test:
   * Despawner prefix fires on host, prefix fires on client too → guard condition mismatch (next: snapshot IsInsideBuilding / NavigationDisabled / IsInsideMotorVehicle / HasPaidForAllItems / CanLeaveHome inside the prefix and compare)
   * Despawner prefix fires on host but NEVER on client → collider/layer/physics problem (player collider or trigger volume on client side is malformed)
   * ExitFromBuilding fires on host but not client (with despawner firing on both) → narrow to the specific guard between them

2026-05-20 — Test 1 result: trigger DOES fire on client; guard rejection confirmed.
- Host log: `[ExitDespawner/HOST] OnTriggerEnter ... by collider='Player' layer=6` and `collider='PlayerTriggerCollider' layer=11`, then `[Building] ExitFromBuilding` (from older Patch_ExitFromBuilding) and `[ExitDiag/PATCH] ExitFromBuildingCoroutine`. Full chain executes.
- Client log: same two `[ExitDespawner/CLIENT] OnTriggerEnter` lines fire — both colliders, same tag='Player', same layers 6 and 11. But NO `[ExitFromBuilding]` and NO `[ExitFromBuildingCoroutine]` follow. The exit chain dies INSIDE ExitZoneDespawner.OnTriggerEnter on the client. (rule)
- Therefore: it is NOT a collider/physics/layer/tag problem. It IS a guard-condition rejection inside the despawner. Categories: `IsInsideBuilding`/`IsInsideParking` (state-mismatch), `NavigationDisabled` (player-control state), `IsInsideMotorVehicle` (vehicle state), `HasPaidForAllItems`/`CanLeaveHome` (player-state). (rule)
- Side note: my redundant `Patch_ExitFromBuilding_Diag` reported `0 method(s) patched` while the existing `Patch_ExitFromBuilding` reports `1 method(s)` — Harmony silently de-dupes a second prefix on the same MethodInfo OR our Plugin counter logic short-circuits the second class. Doesn't matter — removed the dup. (one-off)

2026-05-20 — Deploy checkpoint: guard-value probes shipped.
- Added `_despawnerDepth` counter + six guard probes (`IsInsideBuilding`, `IsInsideParking`, `NavigationDisabled`, `IsInsideMotorVehicle`, `HasPaidForAllItems`, `CanLeaveHome`) plus a `Notifications.Show` probe. Each logs only when `_despawnerDepth > 0`, so log noise stays minimal. All marked `// CLAUDE-DIAGNOSTIC`. (situational: until exit-bug fix lands)
- Build clean (1 unrelated warning in TrafficSync.cs:252), deployed to both plugin paths.

2026-05-20 — Test 2 result: client short-circuits earlier than NavigationDisabled.
- Host trigger fire → 3 guards then exit chain: `NavigationDisabled=False → HasPaidForAllItems=True → CanLeaveHome=True → ExitFromBuildingCoroutine`. IsInsideMotorVehicle not called (not on this branch). (rule)
- Client trigger fires 9+ times (player walks in/out) → ZERO of the four working guard probes ever fire. So the despawner's OnTriggerEnter returns BEFORE NavigationDisabled is reached. (rule)
- IL2CPP gotcha: `Type.GetProperty(...).GetGetMethod(true)` returned null for `BuildingManager.IsInsideBuilding` and `UndergroundParkingManager.IsInsideParking` (Harmony then threw "Patching exception in method null"). The same calls work for `PlayerController.NavigationDisabled` and the rest. Fix: switch to `FindAllMethodsByName("get_IsInsideBuilding")` filtered by `DeclaringType.Name`. (rule)
- Notifications.Show patch matched `UnityEngine.UI.Extensions.SimpleMenu<T>::Show` (open-generic) and failed. Fix: filter `DeclaringType.Name == "Notifications"` and skip ContainsGenericParameters types. (rule)

2026-05-20 — Deploy checkpoint: fixed IsInsideBuilding/IsInsideParking probes + added CompareTag probe + Despawner field snapshot.
- New: `Patch_ExitGuard_CompareTag` (filters by `_despawnerDepth > 0`, logs `gameObject.name`, the arg string, and the result).
- New: `LogDespawnerFields(__instance)` called from despawner Prefix — logs `isCasinoExit`, `isParkingExit`, `exitToZoneId` from the trigger object. (situational: exit-bug)
- Fixed: IsInsideBuilding / IsInsideParking probes now patch via `get_X` method name; Notifications.Show filtered to its own type only.
- Expected outcome on next test:
   * If CompareTag returns false on client → that's the short-circuit (the player object lost the "Player" tag or there's a tag-pool mismatch).
   * If CompareTag returns true but IsInsideBuilding returns false on client → BuildingManager state mismatch (most likely cause).
   * If isCasinoExit or isParkingExit are true on the client's despawner only → MP scene state inconsistency (unlikely but ruled out by inspection).

2026-05-20 — Test 3 result: short-circuit is BEFORE NavigationDisabled and AFTER the Prefix.
- Despawner fields IDENTICAL host vs client: `casino=False parking=False exitToZoneId=-1`. So it's not a parking/casino dispatch branch difference.
- Host (after fix): `Prefix → DespawnerFields → NavigationDisabled → IsInsideBuilding=True → HasPaid=True → CanLeaveHome=True → Coroutine`. IsInsideBuilding now successfully patched via `FindAllMethodsByName("get_IsInsideBuilding")` (returns 1 method). (rule)
- Client: `Prefix → DespawnerFields → (nothing)`. The despawner's OnTriggerEnter returns silently between the field reads and the first method call (NavigationDisabled). (rule)
- The only checks left that would short-circuit in that gap, given casino/parking are both false, are flag checks: `enteringBuilding` / `exitingBuilding` on BuildingManager (private bool fields at 0x110/0x111 per cpp2il dump), or possibly a null-check on `building`. Hypothesis: `enteringBuilding` got stuck true on the client (entry coroutine never completed under MP timing). (rule)
- CompareTag probe was removed — Harmony rejected the FindAllMethodsByName matches as `null` (likely open-generic or native-only Component override). Indirect inference: the Prefix logs tag='Player' and the method still short-circuits, so CompareTag passed. (one-off)

2026-05-20 — Deploy checkpoint: BuildingManager singleton-state snapshot probe shipped.
- `LogDespawnerFields` extended to also reach the BuildingManager singleton via `InstanceBehavior<BuildingManager>.GetInstance(false)` (walked via `BaseType` chain), then read `enteringBuilding`, `exitingBuilding`, and whether `building` is null via reflection. Logs `[ExitGuard/{side}] BM.entering=... exiting=... building==null:...`.
- If next test shows `entering=True` on client but `False` on host → root cause confirmed: entry coroutine never finished. Fix candidates: force-clear `enteringBuilding` on a delay, force-complete the EnterBuildingCoroutine on the client, or find what's breaking the coroutine in the first place.
- If `building==null:true` on client but `false` on host → IsInsideBuilding logic relies on that field; the singleton lost its building reference somehow.
- If both look normal (entering=False, exiting=False, building!=null) on client — the short-circuit is from yet a different early-return, possibly involving an InstanceBehavior.GetInstance returning null for another singleton.

2026-05-20 — Test 4 result: BM.entering/exiting flags IDENTICAL on host vs client.
- Host: `BM.entering=False exiting=False building==null:?` → continues to NavigationDisabled, etc. → Coroutine.
- Client: same values exactly → STOPS. (rule)
- `building==null:?` on both means our reflection field-read for `building` threw silently (likely the type doesn't expose it via that name OR it's a property not a field). Minor — not the deciding factor since both sides match.
- No exceptions, no errors, no other log activity between trigger and silence on client. The despawner's native body returns to control immediately after our Prefix.
- Therefore the bug is even EARLIER than enteringBuilding/exitingBuilding flags. Only candidates left: (a) IL2CPP `CompareTag("Player")` returning false despite `.tag == "Player"` (tag-hash-cache miss), (b) a singleton-fetch via `InstanceBehavior<X>.GetInstance(false)` returning null on the client, (c) something we haven't even thought of yet.

2026-05-20 — Deploy checkpoint: CompareTag direct-call + Postfix + ItemInHands/SetNewDestination/MouseController.Reset probes shipped.
- Despawner Prefix now calls `other.CompareTag("Player")` directly in managed code and logs the bool — if this returns False on client despite `.tag == "Player"`, we have the smoking gun (IL2CPP tag-hash cache desync).
- Postfix now logs `Postfix — original returned`: if Prefix fires but Postfix doesn't on client, the original method threw an exception silently.
- Three new probes (ItemInHands, SetNewDestination, MouseController.Reset) cover the remaining [Calls] entries in the despawner's call graph that we hadn't yet probed.

2026-05-20 — Test 5 result: CompareTag passes, Postfix fires, no other probes fire — bug is on a NULL-CHECK we can't intercept.
- Host: full chain logs as expected and exits via coroutine.
- Client: `Prefix → direct CompareTag('Player')=True → DespawnerFields=False/False/-1 → BM.entering=False exiting=False → Postfix — original returned`. The body runs to completion, returns cleanly, but never calls ANY of the [Calls] in its graph (NavigationDisabled, IsInsideBuilding, IsInsideParking, ItemInHands, HasPaidForAllItems, CanLeaveHome, IsInsideMotorVehicle, SetNewDestination, MouseController.Reset — none of them fire). (rule)
- Conclusion: between CompareTag (returns true) and the first guard property access there is an EARLY RETURN we can't see via Harmony — most plausibly a `if (Instance == null) return;` on one of the singletons (PlayerController, BuildingManager, UndergroundParkingManager, MouseController). Generic `InstanceBehavior<T>.GetInstance` cannot be patched cleanly because it's open-generic. (rule)

2026-05-20 — Deploy checkpoint: singleton-Instance probe shipped.
- New helper `LogSingletonState()` invoked from despawner Prefix logs `Instance=null` vs `Instance=NOT-null` for PlayerController, BuildingManager, UndergroundParkingManager, MouseController via `InstanceBehavior<T>.GetInstance(false)` walked through BaseType chain.
- IL2CPP gotcha hit and fixed: `UnityEngine.Object.FindObjectsOfType(System.Type)` doesn't accept managed `System.Type` — needs `Il2CppSystem.Type` via `Il2CppType.From(Type)`. Dropped scene-count for now to keep build clean; can re-add via Il2CppType.From if needed. (rule)
- Expected outcome: one of the singletons should report `Instance=null` on client and `NOT-null` on host. That singleton is the root cause — we'd then find what makes it null on client (likely a destroy event our patches need to suppress or a registration step our patches are missing).

2026-05-20 — Test 6 result + tooling limitations: no further diagnostic angles via Harmony.
- Singleton inventory matched on host vs client (probe was partly broken: PlayerController/MouseController showed `no GetInstance() found` then `Instance=null` on BOTH sides — they don't inherit from InstanceBehavior). UndergroundParkingManager type not found in any loaded assembly. BuildingManager NOT-null on both. No distinguishing signal. (rule)
- Cpp2IL.Plugin.ControlFlowGraph (downloaded into Plugins/Plugins/ subdir to match plugin loader's path expectation) added a `cfg` output format but crashed mid-dump with `MissingFieldException` (plugin's own bug, line 229 of ControlFlowGraphOutputFormat.cs). No usable CFG output. (rule)
- `CallsUnknownMethods(Count = 39)` on the despawner's OnTriggerEnter means Cpp2IL itself couldn't statically resolve 39 calls. One of them is the early-return condition. Can't reach further without a different tool (ghidra, ida, runtime native-debug).

2026-05-20 — Deploy checkpoint: bandaid fix for client-can't-exit-building shipped.
- New flag `_exitFromBuildingSeenDuringDespawn` set true in Patch_ExitFromBuilding Prefix when `_despawnerDepth > 0`. New `_currentDespawnerInstance` snapshot.
- ExitZoneDespawner Postfix: on CLIENT only, when the flag is still false AND the despawner is regular (`isCasinoExit=False, isParkingExit=False`), reflect into `BuildingManager.Instance.ExitFromBuilding(exitToZoneId, true)` and call it manually.
- Bandaid is scoped: host path untouched, casino/parking paths untouched, scenes the despawner didn't actually fire on are untouched.
- All bandaid code marked `// CLAUDE-DIAGNOSTIC` — clean it up to plain comments once user confirms it works in-game.
- Test plan: client walks into the building exit zone after host has loaded. Expected log: `[ExitBandaid/CLIENT] despawner silently rejected exit — kicking ExitFromBuilding(-1) manually` then `[ExitBandaid/CLIENT] ExitFromBuilding(-1) invoked.` and the client should actually exit the building. Side effects (item-clearing, payment notification, mouse reset) won't run — acceptable given how cleanly client state matched host's pre-exit state.

2026-05-20 — Test 7 result: bandaid actually worked (visual exit + correct teleport), but re-entry is now broken.
- User confirmed: client exit succeeded, landed near host at the outdoor spawn point. Bandaid is end-to-end functional in the right conditions. (rule)
- Re-entry attempt to the same building failed on the client. New bug: same class as the exit — entry-side flow short-circuits silently somewhere. (rule)
- Re-interpretation of earlier "bandaid didn't work" results: likely the double-trigger (Player + PlayerTriggerCollider colliders) made two parallel ExitFromBuildingCoroutine state machines that garbled each other. This time only one likely fired. (rule)

2026-05-20 — Deploy checkpoint: bandaid de-double + entry-side probes shipped.
- `_lastBandaidKickTime` 3s cooldown: second despawner trigger within 3s of a successful kick is logged + skipped. Earlier `return;` inside Postfix would have skipped the depth-decrement at the bottom — restructured to if/else-if so the decrement still runs. (rule)
- New `_interactDepth` counter and 4 entry probes:
   * `Patch_EntryProbe_CBCInteract` — Prefix logs entry, Postfix logs return value. Increments/decrements _interactDepth.
   * `Patch_EntryProbe_CanEnterBuilding` — Postfix logs result during interact window. This is the most likely culprit per call graph.
   * `Patch_EntryProbe_IsBuildingBlocked` — Postfix logs result during interact window.
   * `Patch_EntryProbe_EnterBuilding` — Prefix logs when fired (terminal success signal).
- `Patch_Notifications_Show_Diag` extended to also fire during the interact window — any refusal-message text gets captured.
- Expected decision tree:
   * CBC.Interact() doesn't fire on click → user-input layer broken (very unlikely)
   * Interact fires but returns False without other probes → CanEnterBuilding or another early check rejected
   * CanEnterBuilding=False on client, True on host → ROOT CAUSE: scoped to whatever state CanEnterBuilding reads (probably BuildingRegistration.AvailableForRent or a "open" flag)
   * IsBuildingBlocked=True on client, False on host → some service-state mismatch
   * Notifications.Show fires with a specific message → game's own refusal reason
   * All probes pass but EnterBuilding still doesn't fire → another logic gap we'd need to investigate

2026-05-20 — Test 8 result: bandaid de-double works, re-entry bug is same-class as exit bug.
- Bandaid de-double confirmed working: log shows `[ExitBandaid/CLIENT] duplicate trigger within 0.00s — skipping.` for the second collider. (rule)
- Exit succeeded again (user landed at outdoor spawn near host). (rule)
- Re-entry attempts: `CBC.Interact` → `CanEnterBuilding=True` → `IsBuildingBlocked=False` → `BM.EnterBuilding called` → `Interact() returned True`. All probes pass. But `BuildingManager.LoadBuilding` (which fires inside EnterBuildingCoroutine on the working 1st entry) NEVER fires on subsequent attempts. (rule)
- Direct comparison of 1st (worked) vs 2nd (failed) entry chain: identical up through CBC.Interact returning True. After that, 1st has 20+ BMTrace lines (LoadBuilding, ToggleLayout, ApplyInteriorDesign, FillBuildingDirtSpotObjects, ...). 2nd has nothing — just GameTimeSync ticks. (rule)
- Therefore EnterBuilding ran but never started its coroutine. Same silent short-circuit pattern as the despawner's OnTriggerEnter — early return on a check we can't intercept. (rule)
- Top hypothesis: `BuildingManager.exitingBuilding` stuck True on client because the bandaid-kicked ExitFromBuildingCoroutine ran the visible steps but its terminal cleanup ("exitingBuilding = false") never executed cleanly. EnterBuilding's body likely starts with `if (this.exitingBuilding || this.enteringBuilding) return false;`. (rule)

2026-05-20 — Deploy checkpoint: BM-state probe + EnterBuilding Postfix shipped.
- `LogBuildingManagerStateOnly()` helper reads BM singleton's enteringBuilding, exitingBuilding, building (and resolves building.gameObject.name for clarity).
- Called from `Patch_EntryProbe_CBCInteract` Prefix — logs once per click.
- `Patch_EntryProbe_EnterBuilding` now has Postfix logging the return value (and Prefix logs the building name argument).
- Expected next-test outcome:
   * `BM.exiting=True` on client during re-entry attempt → hypothesis confirmed; fix candidate is to force-clear the flag once the exit chain's observable cleanup steps have completed.
   * `BM.exiting=False` but EnterBuilding returns False → another guard we'll need to dig into.
   * `BM.exiting=False` and EnterBuilding returns True but no LoadBuilding → coroutine factory ran but state machine didn't progress (much harder to fix).

2026-05-20 — Test 9 result: re-entry coroutine hangs after phase-1 (player walk) completes; phase-2 (teleport in) never fires.
- Entry data layer 100% clean on every click: `BM.entering=False exiting=False` (no stuck flags), `CanEnterBuilding=True`, `IsBuildingBlocked=False`, `BM.EnterBuilding(building='Building') returned True` (coroutine scheduled successfully). (rule)
- The "stuck exitingBuilding flag" hypothesis is now BUSTED — both flags read False on both working 1st entry and failing 2nd+ entries. (rule)
- Critical timing comparison between 1st (worked) and 2nd (failed) entry:
   * 1st: EnterBuilding returns True → `BMTrace: LoadBuilding` fires immediately on the next log line. No player walking. (User was already at the entry spot when they clicked.)
   * 2nd: EnterBuilding returns True → player velocity ticks `0 → 6 → 0` (player walks to entry spot and stops). NO LoadBuilding follows. (rule)
- Therefore the entry coroutine has at least two phases: (1) walk-to-destination (player movement via SetNewDestination + yield until arrived), (2) screen-fade + scene-load + teleport-in. The hang is between phase 1 and phase 2 on subsequent entries. (rule)
- EnterBuildingCoroutine state machine class `<EnterBuildingCoroutine>d__55` MoveNext body is marked `[ContainsInvalidInstructions]` by cpp2il — body is NOT decompilable. Same applies to ExitFromBuildingCoroutine's d__87 MoveNext. We can patch them as black boxes but cannot read their conditions. (rule)

2026-05-20 — Significant discoveries not previously logged in detail.
- Manual `BuildingManager.ExitFromBuilding(targetExitId, true)` is the API that triggers the entire exit chain reliably. Once invoked on the client, the full sequence runs: ResetIndoors → SerializeInteriorDesign → HideDirtInCurrentBuilding → DisableAllSizesAndLayouts → OnExitBuilding events fan out to LoudSpeakersManager / PurchaseUI / CityMapHider (50+ times) / GameManager / IndoorCustomerSpawner / TimeOfDayController / PedestrianSpawner / CashRegisterController / Employee → `BM.IsInsideBuilding True→False` → CityManager.PositionPlayerInScene fires (via OnScenesLoaded) → TryToTeleportPlayerToLastPosition OR TeleportPlayerToSavePoint actually moves the player. (rule)
- The despawner's OnTriggerEnter (ExitZoneDespawner) is the NORMAL game entry point for exit, but it short-circuits silently on the client on a check we can't see (CallsUnknownMethods=39 in cpp2il static analysis). Our bandaid bypasses that by calling ExitFromBuilding directly. (rule)
- Exit visual transition is gated on `CityManager.OnScenesLoaded`. On client this fires correctly when the data exit runs — proving the scene-load callback wiring isn't broken on the client. (rule)
- The double-trigger fire (`Player` collider then `PlayerTriggerCollider` ~ms later) inside the same despawner can start TWO parallel exit coroutines on the client, which garble the visual teleport. 3s cooldown on the bandaid invocation fixes this. (rule)
- Successful exit on the client leaves the player visually outside at the correct outdoor position (BSProbe-confirmed) but leaves SOMETHING in a state that prevents the entry coroutine's phase 2 from completing on the next entry attempt. The exact state-mismatch is currently unknown — none of `entering`, `exiting`, or `building` flags differ between successful 1st entry and failing 2nd entry. (rule)
- Cpp2IL.Plugin.ControlFlowGraph crash is REPRODUCIBLE — multiple runs yielded `MissingFieldException` on dozens of methods including the ones we care about, and zero output files. Plugin is incompatible with the game's metadata. No "real" decompiled bodies available without heavier tooling (ghidra/ida). (rule)
- IL2CPP reflection gotcha: BuildingManager.building field is in cpp2il dump as `public Building building` at offset 0x110, but reflection's `GetField("building", Public|NonPublic|Instance)` returns null. Backing field has been renamed by the IL2CPP-managed bridge. Has not been critical so far — IsInsideBuilding state via BSProbe gives us equivalent info. (rule)
- Top current hypothesis for the re-entry hang: our BlackOverlay-canvas suppression (the fix shipped for backlog #6 "client freezes black screen on entry") interferes with the entry coroutine's fade-to-black yield specifically when the player walks to the destination (phase-1 with movement). When the player is already at the destination (no walk), the timing happens to work; when there's a walk, the coroutine and our suppressor are out of phase and the fade-complete check never returns true. Untested yet — F12 toggle test pending. (situational: until F12 test result)

2026-05-20 — Test 10 (F12 BlackOverlay toggle) result: hypothesis BUSTED.
- User toggled F12 multiple times. Clicks DID register through the visible BlackOverlay canvas (5+ `CBC.Interact()` fired with suppression OFF). So the overlay doesn't block raycasts/clicks. (rule)
- Entry chain still fired identically (CanEnterBuilding=True, EnterBuilding returned True). Still no LoadBuilding. (rule)
- Therefore BlackOverlay suppression is NOT the cause of the re-entry hang. Hypothesis eliminated. (rule)

2026-05-20 — Deploy checkpoint: EnterBuildingCoroutine state machine MoveNext probe shipped.
- `Patch_EBC_MoveNext_Diag` walks `BuildingManager.GetNestedTypes()` looking for the `<EnterBuildingCoroutine>d__55` class, then patches its MoveNext directly.
- Prefix + Postfix both read the `<>1__state` field (the IEnumerator state machine's current state) and log it on entry/exit.
- A monotonic counter `_ebcMoveNextCallCount` increments each call so we see ALL invocations across all entry attempts.
- This is the deepest we can probe without external native tooling — d__55.MoveNext body itself is undecompilable, but we'll see HOW MANY state transitions the coroutine makes before hanging.
- Expected outcome:
   * 1st (working) entry: probably 4-8 MoveNext calls progressing through distinct state values (0 → 1 → 2 → -1 etc.).
   * 2nd (failing) entry: stuck at one state value, OR MoveNext fires once and never again, OR MoveNext fires repeatedly with the same state indicating an infinite spin.
   * The state value where MoveNext stops moving forward = the yield point that's broken.

2026-05-20 — Test 11 (EBC MoveNext probe) result: ROOT CAUSE narrowed dramatically.
- Patch_EBC_MoveNext_Diag bound successfully to `BuildingManager+_EnterBuildingCoroutine_d__55` (note IL2CPP-Interop renamed the class — angle brackets became underscores). (rule)
- 1st (working) entry: MoveNext fires 3+ times (call #1 fires synchronously inside EnterBuilding's StartCoroutine, then #2 / #3 on subsequent frames). state-on-entry/exit always shows -999 → our reflection lookup of `<>1__state` field failed (renamed by IL2CPP-Interop). (rule)
- 2nd+ (failing) entries: MoveNext NEVER fires once across 5 separate user clicks, even though EnterBuilding returns True every time. So the coroutine state machine isn't even getting its first synchronous pump. (rule)
- Therefore: either (a) EnterBuilding's body has a code path that returns true WITHOUT calling StartCoroutine on subsequent calls, OR (b) StartCoroutine is called but the MonoBehaviour hosting the coroutine is disabled (Unity silently refuses to pump MoveNext on a disabled MB). (rule)
- Top hypothesis: BuildingManager's MonoBehaviour or its GameObject is in a disabled state after the first exit, which is why StartCoroutine no-ops silently on subsequent entries. (rule)
- BlackOverlay suppression hypothesis (round 10) ruled out: even with F12 toggling overlay back to enabled, MoveNext still never fires on the second entry. Confirms suppression isn't the cause. (rule)

2026-05-20 — Deploy checkpoint: MB-host state probe + field-name discovery shipped.
- `LogBuildingManagerStateOnly()` now also logs `BM.MonoBehaviour enabled=X activeInHierarchy=Y` so we can directly test the disabled-MB hypothesis.
- Same helper enumerates BM instance fields with FieldType containing "Building" OR Name containing "building" to finally identify the renamed `building` backing field (cpp2il sees it as `public Building building`, reflection couldn't find it). Logs each one's resolved value.
- Decision tree for next test:
   * `enabled=False` or `activeInHierarchy=False` on failing attempt → root cause confirmed; fix is to force-enable BM before the entry chain (or skip the disable that exit caused).
   * Both flags true on failing attempt but BM.field.building != null → "already inside" guard in EnterBuilding; fix is to manually clear that field after exit.
   * Both flags true and building==null but MoveNext still doesn't fire → some flag we haven't yet probed gates the StartCoroutine call.

2026-05-20 — Test 12 (MB-host probe) result: disabled-MonoBehaviour hypothesis BUSTED.
- `BM.MonoBehaviour enabled=True activeInHierarchy=True` on BOTH the working 1st entry AND the failing 2nd+ entries. The MB hosting the coroutine is fully alive on both paths. (rule)
- Field-enumeration filter (`name.Contains("building")` or type-name `Building`) logged NOTHING — IL2CPP-Interop isn't surfacing the `building` backing field under any name containing "building". Either the backing field is renamed past recognition (e.g., a generic-mangled name) or `Type.GetFields()` doesn't return it for this IL2CPP wrapper. (rule)
- Combined-state snapshot for failing entries: `entering=False`, `exiting=False`, `IsInsideBuilding=False` (so `building==null` per its definition), `enabled=True`, `activeInHierarchy=True`, `EnterBuilding returns True`, `MoveNext never fires`. All readable state looks pristine; the differing state must be something we can't read via current reflection. (rule)
- Next probe will dump ALL fields unfiltered to find what we're missing.

2026-05-20 — Deploy checkpoint: full BM-field-dump probe shipped.
- `LogBuildingManagerStateOnly` now enumerates EVERY instance field via `GetFields(Public|NonPublic|Instance)`, logs the count, and logs each field's name + type + value (with safe stringification — UnityObject.name for MonoBehaviours, truncated ToString for everything else).
- Expected outcome: a comparison snapshot of every reflectable BM field at the "before 1st entry" timestamp vs "before 2nd entry" timestamp. Whichever field's value differs is a candidate for the silent guard.
- If `GetFields` returns 0 (entire field table inaccessible via reflection), we'll need a different approach — perhaps GetMembers or going through IL2CPP-Interop's typed accessors directly.

2026-05-20 — Test 13 (full BM field dump) result: REFLECTION GAP DISCOVERED.
- `Type.GetFields(Public|NonPublic|Instance)` on `BuildingManager` returns only 2 fields: `isWrapped` (Boolean) and `pooledPtr` (IntPtr). Both are IL2CPP-Interop's own bookkeeping. (rule)
- Game's native fields (`building`, `enteringBuilding`, `exitingBuilding`, `currentBuildingVersion`, `currentLayout`, `exitZones`, etc. per cpp2il dump) are NOT exposed via standard reflection on the IL2CPP wrapper. (rule)
- pooledPtr identical across the two snapshots — same native instance, so it's not an instance-identity bug. (rule)
- Implication: the game's state must be read through the wrapper's get_* METHODS, not as fields. Earlier rounds confirmed this works (e.g., `get_IsInsideBuilding` was patchable as a method). (rule)

2026-05-20 — Deploy checkpoint: getter-method enumeration shipped.
- `LogBuildingManagerStateOnly` now enumerates all instance methods on BuildingManager whose name starts with `get_`, takes 0 params, and returns non-void. For each, invokes and logs the result with safe stringification.
- Should reveal every readable property value (IsInsideBuilding, CanBuildOnCurrentBuilding, IsPlayerOwnedBusiness, and likely many more — including the elusive `building` reference if there's a `get_building` exposed).
- Decision tree for next test:
   * The dump on failing entry shows a `get_*` value that differs from the working 1st entry → that's the silent gating state we've been hunting.
   * All getters return identical values on both attempts → state difference lives outside BuildingManager (in CityManager, BuildingHelper, a static class, or the building's CityBuildingController).

2026-05-21 — ROOT CAUSE FOUND for client re-entry hang (test 14).
- Getter enumeration of BuildingManager exposed all native game state. Diff between working 1st entry and failing 2nd entry:
   * `get_enteringBuilding`: False → **True** (stuck after exit)
   * `get_exitingBuilding`:  False → **True** (stuck after exit)
   * `get_isOpen`:           False → **True**
   * `get_currentBuildingVersion`: null → **BuildingStructureC1**
   * `get_interiorBounds`:   (0,0,0) → **(950.13, 0, 0) extents (7.13, 0, 7)**
   * `get_allItemControllers`: List → **null**
- THE SILENT GUARD: EnterBuilding's body checks something like `if (this.enteringBuilding || this.exitingBuilding) return true;` — returns true (claims success) without calling StartCoroutine. That's why MoveNext never fires on the failing attempt. (rule)
- Why the bandaid exit leaves state stuck: ExitFromBuildingCoroutine runs its visible cleanup methods (ResetIndoors, DisableAllSizesAndLayouts, OnExitBuilding events) but hangs/garbage-collects before reaching the terminal state-reset step that would clear these flags. (rule)
- HUGE GOTCHA discovered: ALL prior `LogBuildingManagerStateOnly` probes that read `enteringBuilding`/`exitingBuilding`/`building` via `Type.GetField()` were LYING. They returned False/null because they were reading IL2CPP-Interop's bookkeeping fields (the wrapper has only `isWrapped` and `pooledPtr` as actual managed fields). The game's native state must be read via `get_*` METHODS on the wrapper. Six rounds of probes (incl. the "BM.entering=False exiting=False" conclusions) were misleading. The getter-method enumeration in this round finally exposed ground truth. (rule)

2026-05-21 — Deploy checkpoint: re-entry fix shipped.
- New `ForceResetBMStateIfStuck(bmType, bmInst, side)` helper. Reads `get_enteringBuilding` and `get_exitingBuilding` via SafeBoolGet (proper getter-method invocation, NOT GetField). If either is True after the bandaid exit, calls `set_enteringBuilding(false)` / `set_exitingBuilding(false)` via SafeBoolSet. Also force-nulls `set_building(null)`. Logs before/after values.
- Invoked from the bandaid path: right after `ExitFromBuilding(exitToZoneId, true)` returns, the reset is enqueued onto GameStatePatcher's main-thread queue so it runs a frame later — giving the coroutine an opportunity to do its own cleanup first. If the coroutine completed cleanly, the reset is a no-op ("post-exit state clean — no reset needed"). If it stuck, we force-clear.
- Expected outcome: client can exit AND re-enter buildings in the same session. The re-entry's coroutine should finally pump MoveNext properly because the silent guards are no longer set.

2026-05-21 — ✅ FIX CONFIRMED: client can enter and exit buildings repeatedly in MP.
- User tested multiple enter/exit cycles with the round-13 force-state-reset in place. All cycles succeeded. This is the first known working client enter/exit loop in Big Ambitions MP (no community mod has attempted this — verified by web search earlier in session). (rule)
- User decision: KEEP the bandaid for now while continuing to investigate the fundamental cause. (rule)

═══════════════════════════════════════════════════════════════════
BANDAID REGISTRY — client-can't-exit-or-reenter workaround
═══════════════════════════════════════════════════════════════════
- Gate flag: `MPPatches.ExitEntryBandaidEnabled` (default TRUE)
- Runtime toggle: F3 in-game on client. Logs `[ClientFix] ExitEntryBandaid toggled → True/False`.
- Code search marker: grep for `CLAUDE-DIAGNOSTIC[BANDAID]` to find ALL related code points.

What the bandaid does (two pieces, both gated):
  1) `Patch_ExitZoneDespawner_OnTriggerEnter_Diag.Postfix` — on client, if the despawner short-circuited and isCasinoExit/isParkingExit are both false, manually invokes `BuildingManager.ExitFromBuilding(exitToZoneId, true)` via reflection. 3-second cooldown de-doubles the duplicate-collider trigger.
  2) `ForceResetBMStateIfStuck` (called via main-thread queue after #1) — reads `get_enteringBuilding` / `get_exitingBuilding` via reflection on the BM IL2CPP wrapper. If either is stuck True (the exit coroutine didn't reach its terminal state-reset), force-clears via `set_enteringBuilding(false)` / `set_exitingBuilding(false)` and nulls `set_building(null)`. Without this, EnterBuilding silently early-returns true on subsequent re-entries.

To remove the bandaid (for testing a proper fix or before release):
  - Set `ExitEntryBandaidEnabled = false` (or press F3 in-game).
  - When OFF: client can't exit (original bug returns). That's expected and useful for A/B verification of any proposed fundamental fix.
═══════════════════════════════════════════════════════════════════

2026-05-21 — Plan: continue investigating fundamental cause.
- The bandaid masks two symptoms but doesn't fix the underlying coroutine-doesn't-complete pattern that affects both ExitFromBuildingCoroutine (visible cleanup runs, terminal reset doesn't) and the original ExitZoneDespawner.OnTriggerEnter short-circuit.
- Diagnostic options remaining without external tools:
  a) Patch d__87 MoveNext (ExitFromBuildingCoroutine state machine) — same approach as we used for d__55 (EnterBuildingCoroutine). Count MoveNext calls + log <>1__state field on each invocation. Would tell us at WHICH state the exit coroutine bails out without reaching the terminal reset.
  b) Look for what's DIFFERENT about client coroutines: do any of OUR Harmony patches (esp. on MonoBehaviour methods) interfere with coroutine completion? Kill switch + bandaid combined test would isolate this.
  c) Static state diff: enumerate static fields on BuildingManager (and CityManager, BuildingHelper) at "before first entry" vs "after first exit". Find what changes and might be the upstream cause of the short-circuit.
- Heavier escalation if (a)-(c) don't pan out: Ghidra to read the actual native code of ExitZoneDespawner.OnTriggerEnter (find the "first domino" guard), BuildingManager.EnterBuilding (confirm the enteringBuilding/exitingBuilding guard), and the two coroutine MoveNexts.
- Next step proposed: patch d__87 (exit coroutine state machine) MoveNext to see how many states it pumps through on client vs host, and identify the hang point.

2026-05-21 — Deploy checkpoint: bandaid gating + F3 toggle shipped.
- `MPPatches.ExitEntryBandaidEnabled` static bool, default true. Gates the two bandaid code paths in `Patch_ExitZoneDespawner_OnTriggerEnter_Diag.Postfix`.
- F3 toggle added in MPCanvasUI.TickToggleClientSuppressions (alongside the other F-key toggles). Edge-triggered, logs the new state.
- All bandaid code points tagged with `// CLAUDE-DIAGNOSTIC[BANDAID]` for easy grep.
- Built clean (3 unrelated nullable warnings), deployed to both plugin paths.

2026-05-21 — Deploy checkpoint: Patch_EXC_MoveNext_Diag (ExitFromBuildingCoroutine state machine probe) shipped.
- Patches `BuildingManager+<ExitFromBuildingCoroutine>d__87.MoveNext` directly via nested-type lookup (same approach as d__55).
- `_excMoveNextCallCount` monotonic counter increments each invocation. Prefix + Postfix log it with state-on-entry/exit values.
- One-shot field-name discovery: on call #1, dumps EVERY field on the state machine's wrapper with name, type, value. This identifies the actual state field name on the IL2CPP-Interop wrapper (`<>1__state` returned -999 for d__55; expect similar here). The dump happens once across the whole session.
- `ReadStateMachineState(obj)` helper tries 5 likely state-field names (`<>1__state`, `_1__state`, `__1__state`, `_state`, `state`), then falls back to "first int field" — covers IL2CPP-Interop renamings.
- Goal: compare HOST exit chain (works naturally) vs CLIENT exit chain (with bandaid ON). Diff in MoveNext counts + state transitions points at the broken yield where the client's coroutine bails out before reaching the terminal state-reset.

2026-05-21 — Test 14 (Patch_EXC_MoveNext_Diag) result: FUNDAMENTAL BUG IDENTIFIED.
- HOST: 5 MoveNext calls on d__87 (ExitFromBuildingCoroutine), final one returns False (coroutine completes cleanly). Two of the 5 are from a second coroutine instance started by the duplicate Player+PlayerTriggerCollider trigger (host doesn't have our de-double cooldown). (rule)
- CLIENT: 3 MoveNext calls. Call #3 throws `Il2CppException: System.NullReferenceException` inside `BuildingManager+<ExitFromBuildingCoroutine>d__87.MoveNext()`. Coroutine dies before completing. (rule)
- Surprising finding: in THIS test run, NO BM cleanup methods (ResetIndoors, SerializeInteriorDesign, etc.) fired on the client between MoveNext #1 and the NRE — unlike in earlier rounds where they did fire. Difference: previous rounds may have hit different coroutine state-machine timing. (rule)
- HYPOTHESIS for why coroutine NREs: between MoveNext #1 and #2, our state-reset's `set_building(null)` runs (force-clearing the building reference). By MoveNext #3, the coroutine tries to access `this.<>4__this.building.SomeProperty` or similar → NRE. So our state-reset may be CAUSING the NRE, not just masking the symptom. (rule)
- If hypothesis is correct: the actual fundamental bug is just the despawner first-domino short-circuit. With ExitFromBuilding kicked, the coroutine would complete naturally on its own — no force-reset needed.
- IL2CPP-Interop renames `<>1__state` field beyond our 5 candidate names — `ReadStateMachineState` returns -999. The d__87 wrapper has only `isWrapped` + `pooledPtr` (same as BM wrapper). Field-table is entirely behind IL2CPP-Interop's interop; native fields are exposed only via wrapper methods. (rule)

2026-05-21 — Deploy checkpoint: bandaid split into two independently togglable pieces.
- `MPPatches.ExitBandaidKickEnabled` — gates the manual ExitFromBuilding call. Default TRUE. Without this, original "client can't exit" returns.
- `MPPatches.ExitBandaidStateResetEnabled` — gates the `ForceResetBMStateIfStuck` call (force-clears enteringBuilding / exitingBuilding / nulls building). Default TRUE.
- `MPPatches.ExitEntryBandaidEnabled` (legacy) is now a wrapper that gets/sets both at once (used by F3 toggle).
- F3 still toggles BOTH together for quick on/off. For round-14 hypothesis testing, the user can edit MPPatches at startup to set `ExitBandaidStateResetEnabled = false` keeping `ExitBandaidKickEnabled = true` — that tests whether the coroutine completes naturally without our interference.

2026-05-21 — Deploy checkpoint: state-reset default flipped to FALSE for round-15 hypothesis test.
- `MPPatches.ExitBandaidStateResetEnabled = false` (was true). Kick remains true.
- This tests whether the coroutine NRE was self-inflicted by our `set_building(null)` call, OR an independent bug in the game's coroutine.
- If re-entry works without state-reset → state-reset was always unnecessary; remove it permanently and the bandaid stays half-sized.
- If re-entry fails without state-reset → coroutine genuinely doesn't reset flags on client; we need state-reset, but should make it less destructive (don't null `building`, only clear bools).

2026-05-21 — Test 15 result: state-reset is NOT what causes the NRE.
- With `ExitBandaidStateResetEnabled = false`, MoveNext #3 on client STILL throws `Il2CppException: NullReferenceException` inside d__87.MoveNext. (rule)
- Flags `entering=True exiting=True` on re-entry attempt — coroutine never cleared them naturally (it died at the NRE before reaching the terminal step). (rule)
- Re-entry fails just as before. Bandaid is genuinely necessary; state-reset isn't responsible for the NRE; the NRE is a real game-side bug in the client coroutine. (rule)
- Restored `ExitBandaidStateResetEnabled = true` default. Bandaid is back to fully working.
- Open question: what IS null inside d__87.MoveNext that throws? Cpp2IL static [Calls] list points at: BuildingHelper.CanEnterBuilding(Address), Building.get_Address (would NRE if `<>4__this.building` is null even WITHOUT our intervention), CityManager.SetTrafficSpawnDistanceTarget(Transform), CameraHelper.GetCurrentCamera, Enumerable.FirstOrDefault on ExitZones (could return null if no match), various GarageDoor/VehicleController stuff, UiFader.Fade/UnFade returning IEnumerator. Many candidates.

2026-05-21 — Deploy checkpoint: d__87 captured-locals dump probe shipped.
- `DumpStateMachineLocals(smInstance, type, side, callNo)` enumerates every parameterless `get_*` method on the d__87 wrapper and invokes each. Logs `[EXC/{side}] d__87.{getter} (call #N) = {value}` for every call.
- Filters out: get_Current (pollutes), get_Pointer/get_ObjectClass/get_WasCollected (IL2CPP plumbing).
- Goal: get a snapshot of every accessible state-machine field at MoveNext #1, #2, AND #3 on client. The diff between #3 and host's equivalent will show what's about to be dereferenced.
- Same probe pattern as `LogBuildingManagerStateOnly`'s getter enumeration — that successfully exposed the stuck flags last round. Should work for nested state-machine types too.
- Will be verbose (many getters * multiple calls). One run is enough.

2026-05-21 — Test 16 (d__87 getter dump) result: state machine internals readable.
- IL2CPP-Interop renames `<>` to `__` in nested-class getter names. So `<>1__state` becomes `get___1__state` (three underscores between the `__1` and `__state`), `<>4__this` becomes `get___4__this`, etc. Once we know this, the entire state-machine internals are readable via reflection. (rule)
- State progression on client: state 0 → 1 → 2 → NRE during state 2's body. `__4__this` (BuildingManager reference) stays valid on all 3 calls — NOT what's null. `__8__1` (captured-locals display class) is null on call #1 (not allocated yet), populated on calls #2 and #3 — also looks valid. (rule)
- The NRE is therefore on something inside the display class OR a new local computed during state 2 execution, OR a field accessed via `__4__this.<something>`. (rule)
- Force-reset DID run between MoveNext #1 and #2 (set_enteringBuilding(false), set_exitingBuilding(false), implied set_building(null) from IsInsideBuilding flipping right after). Coroutine survives state 0 and 1 transitions just fine, NREs in state 2.

2026-05-21 — Deploy checkpoint: round 17 — recurse into `__8__1` display class.
- `DumpDisplayClass(dc, side, callNo)` enumerates parameterless getters on the d__87 captured-locals object and logs each value.
- Auto-invoked from `DumpStateMachineLocals` when `get___8__1` returns non-null.
- Expected: state 2 captures locals like the looked-up `ExitZone`, references to `PlayerController`, audio sources, possibly a Camera or fade overlay. Any of these reading null on the client = the NRE source.

2026-05-21 — Test 17 (display class recursion) result: DisplayClass87_0 only captures 2 things — `targetExitId` (-1) and `__4__this` (BM backreference). Both are valid. (rule)
- Critical observation: ZERO BM cleanup methods fire on the client during this exit attempt. No ResetIndoors, no SerializeInteriorDesign, no OnExitBuilding events on any object. The coroutine NREs SO EARLY that none of its visible work happens. (rule)
- The visual exit observed by the user (player lands at outdoor spawn) is NOT from the coroutine running cleanly — it's a side effect of our state-reset's `set_building(null)` which causes IsInsideBuilding → False and possibly triggers CityManager.OnScenesLoaded downstream. (rule)
- State 2's first executable instruction is what NREs. Since the display class doesn't capture much, the null reference must be a field accessed via `this.__4__this.<bmField>`. Top suspects: building, cityBuildingController, buildingRegistration, currentLayout. (rule)

2026-05-21 — Deploy checkpoint: round 18 — targeted BM-state probe at MoveNext call time.
- `DumpTargetedBMGetters(bmInst, side, callNo)` invokes a curated list of getters on the BM instance passed via `__4__this` from the state machine: building, buildingRegistration, cityBuildingController, indoorVolume, parkingVolume, casinoVolume, currentLayout, currentBuildingVersion, businessType, exitZones, allItemControllers, isOpen, enteringBuilding, exitingBuilding, IsInsideBuilding, IsPlayerOwnedBusiness.
- Logs each on every MoveNext call. We'll see the BM state evolve across state 0 → 1 → 2 → NRE. The field that's non-null at call #1 (which works) but null at call #3 (when state 2 NREs) is the most likely culprit.
- If our state-reset's `set_building(null)` is what's making `building` null between calls #1 and #2, we'll see it directly in the log.

2026-05-21 — Test 18 (targeted BM-getter snapshot at MoveNext call time): NRE SOURCE IDENTIFIED.
- At MoveNext #1: `bm.get_building = Buildings.Building` (non-null), `enteringBuilding=True`, all other BM fields populated. Coroutine state 0 just ran.
- State-reset block fires BETWEEN MoveNext #1 and #2: `set_enteringBuilding(False)` + `set_exitingBuilding(False)` + implicit `set_building(null)`.
- At MoveNext #2: `bm.get_building = null` (we just nulled it), `enteringBuilding=False`, `exitingBuilding=False`. State 1 runs without accessing building — returns True.
- At MoveNext #3: `bm.get_building = null` still. State 2 tries to access `this.__4__this.building.SomeField` → NRE.
- THE NRE IS CAUSED BY OUR `set_building(null)` CALL IN THE STATE-RESET. (rule, pending round-19 verification)
- All other BM fields stay populated throughout (buildingRegistration, cityBuildingController, indoorVolume, exitZones, allItemControllers, isOpen, etc.) — only `building` flips. (rule)
- Apparent conflict with round 15 result (state-reset OFF entirely, NRE still happened) — needs verification. Possibilities: (a) round 15's test wasn't run with the new build deployed, (b) something else also nulls building in state 1 of the coroutine, (c) the round 15 NRE was on a different field that's null on client only.

2026-05-21 — Deploy checkpoint: round 19 — remove `set_building(null)` from state-reset.
- Bool clears (enteringBuilding=false, exitingBuilding=false) retained — they're what unsticks re-entry.
- `set_building(null)` REMOVED — to test whether removing this prevents the NRE in MoveNext #3.
- Expected outcomes:
   * NRE goes away AND re-entry works → ROOT CAUSE: our own state-reset was causing the NRE all along. The natural coroutine should now complete (or at least not crash); the bool-clears are the safety net.
   * NRE still happens → confirms the coroutine itself has a bug independent of our `building`-nulling. Then we look for what state 1's code does to `building` (probably ResetIndoors or similar method nulls it).

═══════════════════════════════════════════════════════════════════
TESTING AID: BAMP_AUTOROLE launcher + autopilot (added 2026-05-21)
═══════════════════════════════════════════════════════════════════
- Not part of the shipping mod — purely a developer convenience to skip
  the manual click-through (host F8, connect F8, lobby start, character
  editor confirm) on every bug-test iteration.
- Disabled when env var `BAMP_AUTOROLE` is unset (default for normal play).
- Files:
   * `src/MPAutopilot.cs` — state machine driven from MPCanvasUI.LateUpdate.
   * `launch-mp-test.bat` — double-click this to start both instances.
   * `_launch_host_internal.bat` — sets BAMP_AUTOROLE=host, runs Steam exe.
   * `_launch_client_internal.bat` — sets BAMP_AUTOROLE=client, runs C:\BigAmbitions2\launch_client.bat.
- Hook points:
   * Plugin.Load → `MPAutopilot.Init()`
   * MPCanvasUI.LateUpdate → `MPAutopilot.Tick()`
- States: WaitMenu → HostStart / ClientConnecting → HostWaitClient (host)
  → HostStartingGame (host) → WaitCustomizer → ConfirmingCustomizer → Done.
- In-game runtime control: F2 aborts the autopilot mid-sequence.
- Customizer confirm method: `IntroCharacterCustomizer.StartGame()` invoked
  via reflection.  Assumes that method exists on the IL2CPP wrapper.  If
  the name differs at runtime, log warns and the autopilot stops at the
  customizer state — manual click required as fallback.
═══════════════════════════════════════════════════════════════════

2026-05-21 — Autopilot bugfix: host env var swallowed by Steam relaunch.
- First test: client autopilot fired and tried connecting 30× (60s timeout), but host's BepInEx log showed `[Autopilot] BAMP_AUTOROLE not set — manual mode.` Host instance never even saw the env var.
- Root cause: Steam install at `C:\Program Files (x86)\Steam\steamapps\common\Big Ambitions\` triggers SteamAPI_RestartAppIfNecessary() which relaunches the exe through Steam, dropping our custom env vars in the relaunch.
- Fix: `_launch_host_internal.bat` now also sets `SteamAppId=1331550 / SteamGameId=1331550 / SteamOverlayGameId=1331550` — same workaround `launch_client.bat` uses for the second install. With these set, SteamAPI sees "already a Steam launch" and skips the relaunch, preserving our BAMP_AUTOROLE.
- No mod-side code change. The autopilot was correct; only the env-var pipeline broke.

2026-05-21 — Autopilot fixes (3 issues): customizer hang, cmd-window litter, unnecessary pauses.
- **Customizer hang**: previous run invoked `IntroCharacterCustomizer.StartGame()` ~0.9s after the customizer GameObject appeared — too fast.  Customizer's UI was still initializing (Start()/coroutines hadn't finished).  Pulling StartGame() out from under it left the customizer in a half-loaded state with no UI rendered at all.  Fixed: WaitCustomizer state now uses two-phase logic — first detects the customizer existence, records timestamp, then waits `CustomizerSettleSeconds = 5f` before invoking StartGame.  Gives the UI time to fully render.
- **CMD window litter**: was using `start "TITLE" "file.bat"` which spawns a new console that doesn't auto-close after the bat finishes.  Changed to `start "" cmd /c "file.bat"` — the `cmd /c` exits when the command completes, taking its window with it.  Top-level launcher also exits immediately (no trailing pause).
- **Unnecessary 5s delay**: removed the `timeout /t 5 /nobreak` between host and client launches.  The client's autopilot already retries every 2s for 60s — it doesn't need a head-start.  Host autopilot waits 5s in WaitMenu before calling Start anyway, so client has time even with simultaneous launch.

2026-05-21 — Autopilot speed tuning.
- All hardcoded delays extracted to named constants in MPAutopilot.cs:
   * `InitialMenuWaitSeconds = 1f` (was 5f) — wait after plugin load before firing host/connect.
   * `ClientRetryIntervalSeconds = 0.5f` (was 2f) — client connect-retry cadence.
   * `CustomizerSettleSeconds = 2f` (was 5f) — minimum time confirmed reliable.
   * `DoneSettleSeconds = 1f` (was 3f) — wait after StartGame() before marking Done.
- Net savings ~9-10 seconds per cycle (from ~23s end-to-end to ~13s).
- If customizer UI fails to render on slower machines, raise `CustomizerSettleSeconds` first.

2026-05-21 — Test 19 result + ROOT-CAUSE OF NRE FINALLY IDENTIFIED.
- With `set_building(null)` removed, `building` stays valid through all 3 MoveNext calls. (rule)
- The full visible exit chain runs: ResetIndoors → SerializeInteriorDesign → HideDirtInCurrentBuilding → DisableAllSizesAndLayouts → OnExitBuilding events on all listeners (CityMapHider many times, GameManager, LoudSpeakersManager, PurchaseUI, IndoorCustomerSpawner, TimeOfDayController, PedestrianSpawner, etc.). (rule)
- Re-entry SUCCEEDED on this test cycle (`CHANGE BM.IsInsideBuilding: False → True` after the re-entry click). (rule)
- IL2CPP exception finally has TWO frames in its stack trace:
   ```
   at AiCarMusic.<Start>b__1_1 (Address _) [0x00000]
   at BuildingManager+<ExitFromBuildingCoroutine>d__87.MoveNext ()
   ```
- ROOT CAUSE OF NRE: AiCarMusic.<Start>b__1_1(Address) — a compiler-generated lambda registered in AiCarMusic.Start() as a callback for some building-Address event (likely OnExitBuilding).  Per cpp2il [Calls], the lambda calls DoPlay(), which calls GlobalEvents.RegisterOnGameLoadedCallback + 2 unknown methods.  Something dereferenced inside there is null on the client (probably musicPlayer AudioSource or a singleton). (rule)
- AiCarMusic is the NPC-car-music subsystem.  Plays driving music for AI cars (not the player).  On the client, this subsystem isn't fully initialised (likely because we suppress AI traffic on the client) — its callback dereferences a null reference when fired during the building-exit event. (rule)

2026-05-21 — Deploy checkpoint: Patch_AiCarMusic_StartB_NoOpOnClient shipped — real root-cause fix.
- Patches `AiCarMusic.<Start>b__1_0` AND `<Start>b__1_1` with a Prefix that returns false (skip original) when `MPClient.IsConnected`.  On host they run unchanged.
- Effect: the exit coroutine should no longer NRE on the client.  It should reach its terminal state-reset step naturally.
- If natural completion works, the bandaid's force-reset (set_enteringBuilding(false)/set_exitingBuilding(false)) becomes redundant.  Test by `MPPatches.ExitBandaidStateResetEnabled = false`.
- The kick bandaid (manual ExitFromBuilding call) is still required — the despawner's silent short-circuit remains an unsolved upstream bug, but it's a separate problem with its own fix.

2026-05-21 — Round 20 result: AiCarMusic patch installed and works.
- Original lookup with `m.Name.Contains("Start") && m.Name.Contains("b__1_")` failed because IL2CPP-Interop renames `<Start>` to `_Start_`.  Broadened filter to just `b__1` substring + parameter signature [Address].  Now matches both `_Start_b__1_0` and `_Start_b__1_1`. (rule)
- Both lambdas successfully suppressed on client: `[Plugin] Patched Patch_AiCarMusic_StartB_NoOpOnClient: 2 method(s)`. (rule)
- The b__1_0 lambda fires HUNDREDS OF TIMES during normal play (every AI car triggers it) — not just on exit.  So we were silently eating these NREs across the whole session, not just during exit.  Suppressing it likely improves general client stability beyond just the exit fix. (rule)
- Re-entry succeeded; ZERO Il2CppInterop NREs in the log after the patch was applied. (rule)
- Log throttled: prefix now logs only the 1st and every 500th invocation, since hundreds of "suppressed" lines per session is noise.

2026-05-21 — Deploy checkpoint: Round 21 test — state-reset disabled, expecting natural coroutine completion.
- `ExitBandaidStateResetEnabled = false` (default flipped, was true after round-15 found NRE survived without it).
- Now that the underlying NRE is patched (AiCarMusic suppressed), the exit coroutine should reach its terminal state-reset on its own.
- If exit + re-entry both still work with state-reset OFF → state-reset retires permanently.  Bandaid becomes just the kick (ExitFromBuilding call) + AiCarMusic suppression.
- If anything breaks → re-enable state-reset.

2026-05-21 — ROUND 21 RESULT: STATE-RESET BANDAID NO LONGER NEEDED. ✅
- With `ExitBandaidStateResetEnabled = false` AND AiCarMusic patch active, exit coroutine runs to COMPLETION on the client:
   * d__87 MoveNext: 6 calls (was 3 when NRE killed it early).
   * Final call returned False = terminal state.
   * No Il2CppInterop NRE anywhere in the log.
- `BSProbe CHANGE BM.IsInsideBuilding: True → False` fires from the GAME's own code (line 40110), not from our force-reset. (rule)
- Re-entry succeeded naturally: `BSProbe CHANGE BM.IsInsideBuilding: False → True` (line 40227). (rule)
- The bandaid has SHRUNK from ~80 lines to just two essential pieces:
   1. **Kick** (manual ExitFromBuilding) — still needed because the despawner's OnTriggerEnter silently short-circuits on the client (a different, unsolved upstream bug — but cheap and stable to bandaid).
   2. **AiCarMusic suppression** — REAL surgical fix for the actual bug.  Prefixes `_Start_b__1_0` and `_Start_b__1_1` to return false on client.  Prevents a NullReferenceException in the audio-callback lambda that fires from many places (every AI car).  Also fixes other silent NREs we hadn't noticed.
- State-reset code still in the file but DEAD (the flag gates it).  Can be cleaned up in a future pass.  Keeping it for now in case we discover an edge case that needs it back.

═══════════════════════════════════════════════════════════════════
REFERENCE — Key discoveries from the building enter/exit investigation
═══════════════════════════════════════════════════════════════════
(Recorded 2026-05-21 after 21+ test rounds. Applies to all future work
on this game and to BepInEx-IL2CPP mods for Unity 2022+ metadata v31.)

## IL2CPP-Interop reflection gotchas

- **`Type.GetFields()` returns ONLY the wrapper bookkeeping fields**
  (`isWrapped`, `pooledPtr`) — not the game native fields. Game state must
  be read through `get_*` METHODS, not fields. Standard reflection is
  misleading: `GetField("building")` returns null on `BuildingManager`
  even though the field exists at offset 0x110 per cpp2il.

- **Property reflection is broken too.** `GetProperty(name).GetGetMethod(true)`
  returns null for IL2CPP-Interop wrapped types. Use
  `GetMethod("get_X", BindingFlags.Instance | Public | NonPublic)`
  directly. True for `BuildingManager.IsInsideBuilding`,
  `UndergroundParkingManager.IsInsideParking`, and all auto-generated
  property getters. Harmony patches via `MethodType.Property` will fail
  similarly; patch the `get_X` method by name instead.

- **Compiler-generated identifier renaming.** cpp2il dumps show C# names
  like `<Start>b__1_1` or `<EnterBuildingCoroutine>d__55`. At runtime
  through IL2CPP-Interop, `<>` becomes `_`:
    * `<Start>b__1_1` → `_Start_b__1_1`
    * `<EnterBuildingCoroutine>d__55` → `_EnterBuildingCoroutine_d__55`
    * `<>1__state` → `_1__state`, `<>2__current` → `_2__current`
    * `<>4__this` → `_4__this`, `<>8__1` → `_8__1`
  When patching these, search by substring (`b__1_`, `d__55`) — do not
  attempt exact-name matching, since the underscore conversion is not
  consistent across the leading `<>` (sometimes one, sometimes two).

- **Native fields hidden but accessible via getters.** To dump state of
  an IL2CPP-wrapped class, enumerate `GetMethods` filtered to `get_*`
  with zero parameters and non-void return — invoke each. This is the
  only reliable way to see what is actually in there. Pattern proven
  in round 18 (`DumpTargetedBMGetters`).

## Big Ambitions building enter/exit architecture

- **Entry flow** has two phases:
   1. CBC.Interact() click handler → BM.EnterBuilding() returns true →
      StartCoroutine(EnterBuildingCoroutine d__55).
   2. Player walks to entry spot; coroutine yields until arrival, then
      fires LoadBuilding → ToggleLayout → ApplyInteriorDesign → ... →
      flips IsInsideBuilding True.

- **Exit flow** is despawner-triggered:
   1. Player walks into ExitZoneDespawner trigger volume.
   2. Despawner OnTriggerEnter checks tag + many guards (CanLeaveHome,
      HasPaidForAllItems, NavigationDisabled, IsInsideMotorVehicle, etc.).
   3. If all pass: StartCoroutine(ExitFromBuildingCoroutine d__87).
   4. d__87 fires ResetIndoors → SerializeInteriorDesign →
      HideDirtInCurrentBuilding → DisableAllSizesAndLayouts →
      OnExitBuilding events fan out to: LoudSpeakersManager, PurchaseUI,
      CityMapHider (many), GameManager, IndoorCustomerSpawner,
      TimeOfDayController, PedestrianSpawner, CashRegisterController,
      Employee, and **AiCarMusic** (this last one was the NRE source).
   5. CityManager.PositionPlayerInScene (via OnScenesLoaded callback)
      teleports player to the outdoor spawn point.
   6. IsInsideBuilding flips False.

- **Coroutine state-machine field shape (d__55 / d__87)**:
   * `_1__state` (int) — current state, 0/1/2/.../-1.
   * `_2__current` (object) — yield instruction returned.
   * `_4__this` — back-reference to BuildingManager.
   * `_8__1` (DisplayClass) — captured locals that span yields. Smaller
     than expected: d__87 DisplayClass87_0 only captures `targetExitId`
     and `__4__this`. Most coroutine locals are fresh stack vars during
     each MoveNext execution.

- **Two callers of d__87 typically**: despawner trigger fires twice in
  quick succession (once each for Player and PlayerTriggerCollider).
  On host, the second invocation early-returns via an idempotency check
  inside the coroutine (returns False on first MoveNext). Our bandaid
  needs a 3s cooldown to avoid double-kicking.

## The bug we found

- **`AiCarMusic.<Start>b__1_*`** — compiler-generated lambdas registered
  in `AiCarMusic.Start()` via `Delegate.Combine`. Subscribe to the game
  address-event system; fire on enter/exit. Each lambda calls `DoPlay()`
  which dereferences something null on the client (likely `musicPlayer`
  AudioSource or a GlobalEvents singleton not initialised on client).

- **Fix**: Harmony Prefix on both lambdas that returns false when
  `MPClient.IsConnected`. Six-line patch
  (`Patch_AiCarMusic_StartB_NoOpOnClient`) fully eliminates the NRE.
  Side effect: client may miss AI-car driving music — imperceptible
  since most AI traffic is already suppressed on the client.

- **The NRE fires HUNDREDS of times per session in normal play.** Not
  just on exit. Every AI car triggers it. We were silently eating these
  before this investigation — likely contributing to subtle client
  instabilities we had not isolated.

═══════════════════════════════════════════════════════════════════
UNRESOLVED — Minor issues parked for future revisit
═══════════════════════════════════════════════════════════════════
(2026-05-21 — these do not block shipping. Workarounds-with-bandaids
that could in theory be cleanly fixed if we invest more time.)

1. **ExitZoneDespawner.OnTriggerEnter silent short-circuit on client.**
   The original "first domino" we never figured out. After host loads,
   despawner OnTriggerEnter fires on client (Prefix runs) but the body
   short-circuits before reaching ANY of its `[Calls]` (no
   NavigationDisabled, no IsInsideBuilding, no notifications, nothing).
   The rejecting check lives in `CallsUnknownMethods(Count = 39)` —
   cpp2il could not statically resolve them.
   Workaround: kick bandaid (`Patch_ExitZoneDespawner_OnTriggerEnter_Diag`
   Postfix calls ExitFromBuilding manually). Stable and cheap.
   Real fix would require reading native code with Ghidra (tooling set
   up but not executed). Cpp2IL.Plugin.ControlFlowGraph crashes on this
   game so the easier route is blocked.

2. **CityManager.OnScenesLoaded** — identified as the mechanism that
   triggers visual exit (teleport player out + camera transition) but
   never instrumented deeply. Fires naturally on client. Worth deeper
   understanding when we work on parking-exit or casino-exit paths.

3. **BlackOverlay canvas suppression** still active in MPCanvasUI
   TickSuppressBlackOverlay. F12 toggles it off at runtime. Shipped for
   backlog #6 (client freezes black screen on entry). Ruled out as a
   cause of the building-exit hang (round 10), but still active for #6.
   Do not remove without re-testing #6.

4. **`set_building` writeback via reflection** — we proved we can write
   to it (round 13 nulled it) but proved nulling it is destructive
   (causes NREs in state 2+ of d__87). If we ever need to write to it
   defensively, syntax works but side effects need careful handling.

5. **`Patch_MB_StartCoroutine_Diag` failed** with HarmonyException.
   Tried to patch `UnityEngine.MonoBehaviour.StartCoroutine(IEnumerator)`
   but Harmony could not find method on IL2CPP-wrapped MonoBehaviour.
   If we ever need to instrument StartCoroutine, the IL2CPP-Interop
   parameter type is probably `Il2CppSystem.Collections.IEnumerator`
   not `System.Collections.IEnumerator`. Worth a try.

6. **`Cpp2IL.Plugin.ControlFlowGraph`** — produces real decompiled
   bodies when it works, but crashes deterministically on this game
   metadata with `MissingFieldException` at line 229 of
   ControlFlowGraphOutputFormat.cs. Tried multiple times. A newer
   plugin version would give us readable bodies for all the
   `[ContainsInvalidInstructions]` methods we currently can not see.

7. **Autopilot `IntroCharacterCustomizer.StartGame()` invocation** via
   reflection works but we never validated what happens if the
   character editor has unexpected input. Currently accepts whatever
   defaults are in fields plus the pre-filled name.

8. **No load-game launcher** — only "new game" is automated. If a test
   ever needs to load a specific save, `MPServer.StartLoadGame()`
   exists and would need wiring into the autopilot.

9. **AiCarMusic suppression side-effect not measured.** The lambda real
   purpose is "play music when an AI car enters/leaves the player
   audible range." By suppressing it on client, the client may have
   slightly different ambient audio than host. Unlikely noticeable,
   but if anyone reports missing AI traffic sound on the client, this
   is the cause.

2026-05-21 — Major cleanup pass after investigation closed.
- MPPatches.cs: 2292 → 699 lines (1593 deletions, ~70% reduction).
- MPCanvasUI.cs: 2307 → 1937 lines (370 deletions, ~16% reduction).
- Removed 30 dead diagnostic patch classes (ExitGuard_*, EntryProbe_*,
  EBC/EXC MoveNext probes, BMTrace, DelayedCall/SendMessage/Invoke probes,
  Notifications_Show_Diag, ExitDiag_*).
- Removed dead helpers: ForceResetBMStateIfStuck, SafeBoolGet, SafeBoolSet,
  LogBuildingManagerStateOnly, LogSingletonState, LogOneSingleton,
  LogDespawnerFields, DumpStateMachineLocals, DumpDisplayClass,
  DumpTargetedBMGetters, ReadStateMachineState.
- Removed dead state fields: _interactDepth, _ebcMoveNextCallCount,
  _excMoveNextCallCount, _excFirstCallFieldsLogged,
  ExitBandaidStateResetEnabled, ExitEntryBandaidEnabled.
- Removed dead UI ticks: TickExitTriggerDiag, TickBuildingStateProbe,
  and their helpers (BspCheckBlackOverlay, BspInit, etc.).
- Simplified the bandaid Postfix to ~30 lines of clean reflection
  (was ~150 lines with diagnostic logging interspersed).
- 13 active Harmony patches remain, all functional:
  GSC_TogglePause, GSC_Set, TaxiController_OnClick, TaxiTravel,
  DelayedEnterBuilding (calls TrafficSync.OnEnteredBuilding),
  TM_ClearTraffic / ClearTrafficOnArea / SetPause (backlog #7),
  ParkingSim_Request / Release, GenerateParkedVehicles (backlog #3),
  ExitZoneDespawner_OnTriggerEnter_Diag (the kick bandaid),
  AiCarMusic_StartB_NoOpOnClient (the surgical root-cause fix).
- F3 toggle in MPCanvasUI now flips only `ExitBandaidKickEnabled`.
- Build clean (1 unrelated nullable warning in TrafficSync.cs:252).

2026-05-21 — Business sync Phase 1 shipped (exterior business state).
- New files: src/BusinessSync.cs (host-side change detector).
- Protocol additions: MessageType.BusinessSnapshot (50), MessageType.BusinessChange (51); DTOs BusinessInfo and BusinessSnapshotPayload/BusinessChangePayload.
- Tier A fields (always sync'd to all): BusinessName, businessTypeName, temporarilyClosed.
- Tier B fields (close-up detail; Phase 1 sends to all on connect, no distance culling yet): BusinessDescription, signAppearanceSettings (SignType + signLight + lamp), logoSettings (logoShape + font + logoColor + fontColor + backgroundColor).
- SerializableColor handled by passing the packed int unchanged — host writes the int into reg.signAppearanceSettings.signLight, client reads it identically. No bit-layout knowledge needed.
- Change detection: BusinessSync.Tick() polls every 2s on host, diffs each BuildingRegistration against last-broadcast snapshot, broadcasts BusinessChange for any building whose fields changed.
- Connect bootstrap: full BusinessSnapshot sent (a) on late-join Welcome and (b) once all players are in-game after a fresh start (via ReleaseStartupHold).
- Client side: ApplyBusinessSnapshot / ApplyBusinessChange in GameStatePatcher, gated under existing ClientApplyOwnership flag for kill-switch compatibility.
- Build clean, deployed to both plugin paths.

2026-05-21 — Business sync Phase 1 v2: visual refresh added.
- Test 1 finding: data sync was working (BusinessTypeName + temporarilyClosed propagated on client) but the visible sign + business name + logo did NOT refresh.  Cause: writes to BuildingRegistration update the DATA model, but the building exterior sign / logo / map POI are GameObjects initialised on first load from the registration — they do not auto-rebind to data changes.
- Identified refresh path via cpp2il: `BuildingLogoSignController.UpdateSign(reg)` is called by `CityBuildingController.UpdateSign()`.  Also `CityBuildingController.UpdatePoi(null)` refreshes the map POI marker.
- Added FindCBC(addressKey) helper in GameStatePatcher — scans FindObjectsOfType<CityBuildingController> once and caches per-address.  GameStatePatcher.ResetCBCCache() should be called on game load (TODO: wire into game-loaded callback).
- After each ApplyBusinessInfoLocal write, calls `cbc.UpdateSign()` and `cbc.UpdatePoi(null)` to repaint the visuals.
- Stale AI businesses: BusinessSync already iterates ALL building registrations (including those with BusinessTypeName.Empty), so the client receives "Empty" for buildings without businesses on host.  With UpdateSign now firing, signs/logos for those should clear.  If they don't clear visually, may need additional refresh for AI-business spawner state.

2026-05-21 — Business sync Phase 1 v3: deeper refresh + diagnostic logging.
- Test 2 finding: cbc.UpdateSign() + cbc.UpdatePoi(null) alone did not fix the visible sign / business name / map name.  Stale-AI-business issue (test 4) is fixed.
- Hypothesis: cbc.UpdateSign() defers to LOD-state-based logic (BuildingSignController and BuildingLogoSignController both implement ICullable with OnLod0/1/2 paths).  When the building is at LOD2 or culled, the update may be a no-op.  Direct ConfigureSign / UpdateSign on the child controllers should force a refresh regardless.
- v3 walks cbc.GetComponentsInChildren<BuildingSignController> and <BuildingLogoSignController>, calls ConfigureSign(reg) / UpdateSign(reg) directly on each.  Logs the refresh count per address.
- Also logs "[Patcher] Refresh <addr>: name='X' type=Y sign+logo refreshed=N" so we can verify the call site is firing.
- If sign still doesn't show after v3: the issue is deeper (e.g. an asynchronous texture load that requires server-only state, or a separate text-mesh component we haven't found).

2026-05-21 — Business sync Phase 1 v4: logo PNG regeneration on client.
- Test 3 finding: host and client diagnostic logs match field-for-field (name, type, signType, logo colors all wire-identical).  Wire is clean.  Issue is purely visual: the host's player-customized sign logo is stored as a PNG file on disk (at LogoHelper.GetPlayerBusinessLogoPath(businessName)) — generated by the BizMan UI when the player customizes the logo.  The client has no such PNG on disk → BuildingLogoSignController.UpdateSign loads a default/generic instead.
- Fix: after applying business info on client, if logoShape and BusinessName are non-empty, call `BusinessLogoGenerator.Create(name, settings, savePath, isPlayerBusiness:true, null)` to regenerate the PNG locally from the synced LogoSettings (host and client have identical settings, so output is identical).
- Generation is asynchronous (coroutine internal to the game).  We queue a deferred sign refresh ~3s out via `_pendingLogoRefreshes` list, drained per-frame from `MPCanvasUI.LateUpdate`.  Once the PNG is on disk, the deferred refresh re-invokes the sign controllers to load it.
- Side effect: BusinessLogoGenerator.Create writes a PNG to the client's local save folder.  Harmless but worth noting if save-folder hygiene matters later.

2026-05-21 — ✅ Business sync Phase 1 COMPLETE.
- All exterior business state synced host → clients: name, type, description, sign type/colors, logo (shape/font/colors), open-closed flag, and the actual rendered sign textures (Billboard/SquareSign/WideSign JPGs).
- Lessons learned along the way (all in the new REFERENCE section):
   * IL2CPP-Interop hides game native fields from Type.GetField — use direct compile-time access (`LogoHelper.BusinessLogoTextures.Clear()`) instead of reflection.
   * NEVER Destroy() textures that may still be referenced by other GameObjects — caused cross-business sign contamination.
   * NEVER iterate Il2CppSystem.Dictionary in foreach when the key is a ValueTuple — crashed the client.
   * GetPlayerBusinessLogoPath returns a directory, not a file — contains multiple per-size JPGs.
   * GetBusinessLogoTexture takes a `playerBusiness` bool; needs to be true on client for host's businesses (we patched it).
- Final architecture (~250 lines code, polling-based, full table on connect, deltas on change):
   * src/BusinessSync.cs (host change detector + JPG read)
   * src/GameStatePatcher.cs ApplyBusinessSnapshot/ApplyBusinessChange + ApplyBusinessInfoLocal (writes fields + JPG files + invalidates cache + queues deferred sign-refresh)
   * src/MPPatches.cs Patch_GetBusinessLogoTexture_ForceClient (forces player-business lookup path when files exist on disk)
   * Protocol: BusinessInfo / BusinessSnapshotPayload / BusinessChangePayload, MessageType BusinessSnapshot (50) + BusinessChange (51)

═══════════════════════════════════════════════════════════════════
BACKLOG — Business sync remaining phases
═══════════════════════════════════════════════════════════════════
- **Phase 1b: Rental marketplace sync** — building rental state (AvailableForRent, RentPerDay, lastDeposit, BuildingForSale flag, and ownership-driven "for sale" listings by other players).  Currently we only mark BUYABLE-BY-OTHERS buildings as unavailable; we don't actively sync the AVAILABLE state, prices, deposits, or owned-but-listed-for-sale buildings.  Result: buildings the host can rent may not show as rentable on client (or show with different prices).
- **Phase 2: Interior layout sync on entry** — building interior items (Layout string, itemInstances, interiorDesigns, retailPrices, dirtSpots).  Only sync when a player enters a building; host snapshots and ships InteriorSnapshot.  See REFERENCE for shape details.
- **Phase 3: Shopper sync when 2+ inside** — first player in is authoritative for customer positions, queue state, item-in-hand.  Second player on entry: subscribe to shopper broadcasts.  When everyone leaves, sync stops.
- **Phase 4: Save persistence (Option A: host owns everything)** — single .bamp.sav JSON file containing world state + per-player snapshots.  Host saves on demand + auto-save + on player disconnect.  Client never saves.  MP saves NOT loadable in single-player (different file format, different folder) — prevents the "earn $$$ in SP then bring to MP" cheat path.

2026-05-21 — Business sync Phase 1b shipped: rental marketplace state.
- Added 3 fields to BusinessInfo: AvailableForRent, RentPerDay, LastDeposit.
- Host's ReadInfo populates them from reg.  Client's ApplyBusinessInfoLocal writes them back.  EqualInfo includes them in the diff so changes are detected and broadcast.
- Expected effect: buildings the host considers vacant+rentable should now show as for-rent on client too — overriding the client's local AI-economy assignments that diverge from host's world state.
- Polling continues to catch ALL changes regardless of cause (player action, AI economy churn, scheduled events) — the polling design is event-source agnostic, which means we don't need to subscribe to specific event types to catch state mutations.
- Note: existing MarkBuildingUnavailable code (sets AvailableForRent=false for buildings owned by other players in BuildingOwners) is now structurally redundant with Phase 1b but harmless — left in place to avoid disturbing tested behavior; can be removed in a future cleanup pass.

2026-05-21 — Phase 1b residential/warehouse anomaly observed.
- User test: open map, toggle "for rent" filter across categories.  Retail/Office match host↔client.  Residential and Warehouse: CLIENT shows MORE for-rent entries than host.
- User hypothesis (likely correct): client's local AI economy generates additional for-rent residential/warehouse buildings on top of what host directs.  Either (a) those buildings aren't in host's BuildingRegistrations list at all (so they never get into the snapshot), (b) the client's AI keeps flipping them back to for-rent after we apply, or (c) the map filter for residential/warehouse reads from a different list (e.g. BuildingHelper.AllBuildingRegistrationDictionary, BuildingHelper.allBuildings) that bypasses gi.BuildingRegistrations entirely.  (situational: Phase 1b investigation)
- gi.BuildingRegistrations is a save-list — may not include every building in the city.  BuildingHelper has static dictionaries (AllBuildingRegistrationDictionary, AllBuildingDictionary) populated from "BuildingsDefinitions" AddressableLabel that DO cover the whole city.  If the map filter consults the static dict, we need to iterate that instead of (or in addition to) gi.BuildingRegistrations. [?] (situational: Phase 1b investigation)
- Diagnostic plan: count per-BuildingType (Residential/Retail/Office/Warehouse/...) (a) regs in gi.BuildingRegistrations on host and AvailableForRent count, (b) same on client BEFORE applying snapshot, (c) same on client AFTER applying.  If host count <<< client count for residential/warehouse, the buildings aren't in gi.BuildingRegistrations at all → iterate AllBuildingRegistrationDictionary.  If host count == client-after but client UI still shows more, the map reads from a different source.  (rule)
- BuildingType enum: Residential=0, Retail=1, Office=2, Warehouse=3, Special=4, Cinema=5, Theater=6 (Buildings.BuildingType in BigAmbitions.dll).  Accessible via reg.BuildingCached.BuildingType (lazy-loaded via BuildingHelper). (rule)

2026-05-21 — Phase 1b diagnostic build deployed.
- BusinessSync.BuildFullSnapshot now counts per-BuildingType (totalRegs / forRent) and logs the breakdown.
- BusinessSync.TryGetBuildingTypeIndex(reg) + LogTypeBreakdown(label, …) made public for client-side reuse.
- GameStatePatcher.ApplyBusinessSnapshot logs THREE breakdowns: BEFORE applying (client's pre-sync state), PAYLOAD (what host sent, broken down by client-side BuildingType lookup of each address), and AFTER applying.
- Expected diagnostic outputs after a test:
   * Host log: "BuildFullSnapshot: totalRegs=N | Residential=A/BforRent Retail=…"
   * Client log: same three lines for BEFORE/PAYLOAD/AFTER.
- If host's Residential/Warehouse counts are << what's visible on host's map filter, gi.BuildingRegistrations is missing those buildings → must iterate BuildingHelper.AllBuildingRegistrationDictionary instead.
- If PAYLOAD's Residential/Warehouse counts match host's BuildFullSnapshot but client's BEFORE has more, sync is fine but client AI is regenerating them after — need to purge between snapshot apply and map refresh.
- Build clean, deployed to BepInEx plugins dirs.

2026-05-21 — Phase 1b diagnostic result #1: AvailableForRent is NOT the for-rent flag.
- Test result: host BuildFullSnapshot and client BEFORE/PAYLOAD/AFTER all show IDENTICAL counts.  825 buildings on both sides (Residential=335 Retail=284 Office=86 Warehouse=74 Special=38 Cinema=4 Theater=4), and `AvailableForRent=true` for ZERO buildings on both sides.
- Conclusion: gi.BuildingRegistrations is exhaustive (all 825 city buildings present), and our sync of AvailableForRent is wire-clean.  But the map's "for rent" filter reads SOMETHING ELSE — `AvailableForRent` is always false in this game version.  (rule)
- Candidate signals on BuildingRegistration that distinguish rentable vs occupied: BusinessTypeName==Empty(0), RentedByPlayer, buildingOwnerRivalId, businessOwnerRivalId.  For Residential buildings there's no BusinessTypeName concept (they aren't businesses), so the differentiator there is buildingOwnerRivalId/RentedByPlayer.  For Warehouse, same.  (rule)
- Per the decompile, BuildingRegistration.GetPOIIcon (the on-map dot) is determined by BusinessTypeName + BuildingType (no AvailableForRent involvement).  The map's CityMapFilters.ApplyFilters then shows POIs by category.  (rule)
- buildingOwnerRivalId and businessOwnerRivalId are NOT currently in BusinessInfo payload.  If those drive the for-rent state and they're not in sync, that's the cause.  (situational: Phase 1b investigation)

2026-05-21 — Phase 1b diagnostic v2 deployed.
- Extended BusinessSync.TypeStats class: per BuildingType records total + 5 candidate signals (AvailForRent, EmptyBizType, PlayerRented, RivalOwns, RivalBiz).
- Host logs "BuildFullSnapshot: …" with the full breakdown.  Client logs BEFORE and AFTER (PAYLOAD removed — only the reg-level signals matter, and the payload doesn't carry the rival ids yet).
- Next test will show which signal differs between host and client for Residential/Warehouse — that's the one we need to add to BusinessInfo.
- Build clean, deployed.

2026-05-21 — Phase 1b diagnostic v2 result: BuildingRegistration fields IDENTICAL on both sides.
- Both host and client report 825 buildings, identical totals per type, identical (avail=0 empty=N playerR=0 rivalOwn=0 rivalBiz=0) on EVERY type.  AvailableForRent and rival-ownership fields are all zero on both sides at this point in the game.
- The map UI nonetheless shows "a few additional" for-rent buildings on client vs host.  Data is identical → the discrepancy must come from a source we haven't checked.  (situational: Phase 1b investigation)
- Decompile of CityMapFilters.ApplyFilters: calls IsBuildingForSale(CityBuildingController) which (per the compiler-generated lambda) iterates BuildingForSale list AND RealEstate list.  A building present in EITHER list is "for sale" → hidden from the for-rent filter.  (rule)
- gi.buildingsForSale is RNG-populated from a subset of city buildings (the buy marketplace).  Different RNG seed → different sets.  If host has building X in buildingsForSale, host hides it from for-rent.  If client doesn't have X in its buildingsForSale, client shows X as for-rent → CLIENT EXTRAS.  This matches the user's observation exactly.  [?] (situational: Phase 1b investigation — hypothesis pending diagnostic confirmation)
- gi.realEstate is the player's owned-property list.  Empty on both sides for our test (player hasn't bought anything).

2026-05-21 — Phase 1b diagnostic v3 deployed.
- Added BusinessSync.LogForSaleAndRealEstate(label, gi) — logs gi.buildingsForSale.Count + gi.realEstate.Count plus a per-BuildingType breakdown of each list.
- Host's BuildFullSnapshot and client's BEFORE/AFTER all log this new breakdown alongside the TypeStats.
- Next test should show host has N entries in buildingsForSale while client has M (or zero), confirming the hypothesis.  Then the fix is to add a "ForSale" list to the protocol and sync it as part of the snapshot.
- Build clean, deployed.

2026-05-22 — Phase 1b v3 result: buildingsForSale and realEstate ALSO identical (both empty).
- Host BuildFullSnapshot: forSale=0 realEstate=0 (all per-type breakdown zero).
- Client BEFORE / AFTER: forSale=0 realEstate=0 (matching).
- Hypothesis falsified: this isn't the source of the discrepancy either.  At early game state, both lists are empty (the marketplace presumably populates on a day-tick event we haven't yet observed).
- Remaining places the for-rent map filter could differ between host and client when gi.BuildingRegistrations + buildingsForSale + realEstate are all identical: (a) CityBuildingController GameObjects in the scene (the filter iterates CBCs, not registrations directly), (b) some per-CBC cache (highlights, hidden state) that wasn't refreshed by our snapshot apply, (c) BuildingRegistration sub-objects we haven't dumped (e.g., scheduleDays, RealEstate.BuildingRegistration link).  (situational: Phase 1b investigation)

2026-05-22 — Phase 1b diagnostic v4 deployed.
- Added BusinessSync.LogSceneCBCCounts(label) — calls FindObjectsOfType<CityBuildingController> and prints per-BuildingType count + count of CBCs whose reg.BusinessTypeName is Empty (the "for-rent eligible" set).
- If the scene CBC count differs from gi.BuildingRegistrations count, the map UI is operating on a different set of objects than what our sync touches.  Likely candidates for divergence: CBCs that aren't backed by a BuildingRegistration at all, or duplicate CBCs spawned during world-streaming.
- Build clean, deployed.

2026-05-22 — Phase 1b v4 result: scene CBC populations also IDENTICAL.
- Host BuildFullSnapshot sceneCBCs: total=825 noReg=0 (R335/335empty Re284/274empty O86/85empty W74/74empty S38/0empty C4/4empty T4/4empty).  Client BEFORE+AFTER match host EXACTLY.  Every layer of state we can measure is identical between host and client at snapshot apply time.  (rule)
- The user's "few additional" buildings highlighted in for-rent on client are real but not visible in any data field we measure at snapshot apply time.  Possible causes: (a) state drift between snapshot apply and when user opens map — AI economy or scheduled tick mutates something asymmetrically, (b) CityMapFilters caches `_searchableBuildings` at Start() and never refreshes — so post-load CBCs aren't in its set, (c) some PointOfInterest/CityBuildingController UI cache that ApplyFilters reads instead of the registration data.
- Strategy: capture state AT THE MOMENT the filter actually runs.  Harmony-patch CityMapFilters.ApplyFilters Prefix and dump TypeStats + sceneCBC counts there.  Eliminates timing ambiguity.

2026-05-22 — Phase 1b diagnostic v5 deployed.
- Added Patch_CityMapFilters_ApplyFilters_Diag (Harmony Prefix on CityMapFilters.ApplyFilters, throttled to 2s).
- Fires every time the user toggles a filter on the map.  Logs role (HOST/CLIENT), TypeStats, forSale/realEstate counts, and sceneCBC counts at the EXACT moment ApplyFilters runs.
- If host and client diagnostic at filter time STILL match, the filter is using an input we haven't found.  If they differ, the divergence happens between snapshot apply and filter use — pointing at AI economy or some other tick.
- Build clean, deployed.

2026-05-22 — KEY INSIGHT from user screenshot: extras are pre-sync survivors, not new additions.
- User took a screenshot of client's "for rent" highlights BEFORE our snapshot applied.  After snapshot, the "extras" beyond host's set EXACTLY match the pre-sync state — i.e., our wipe is not 100% effective.  A handful of residential/warehouse buildings retain their pre-sync state through our apply.  (rule)
- This invalidates the "data identical" reading of the diagnostics — our diagnostic captures aggregate counts (335 Residential empty on both sides), but the per-building IDENTITY of which 335 are empty differs.  Host has buildings A,B,C empty; client has B,C,D empty.  Counts equal, sets differ — and our snapshot apply doesn't always overwrite buildings the client has assigned differently than host.  [?] (situational: Phase 1b investigation)
- User executive decision: switch architecture from "wipe then mirror" to "suppress generation, verify wipe, mirror."  (rule)

2026-05-22 — Phase 1 + 1b pushed to GitHub.
- Commit 97fee28 to origin/main (https://github.com/Melaus123/BigAmbitionMulti).  10 files, +2882/-675.  New files: src/BusinessSync.cs, src/MPAutopilot.cs.  Modified: GameStatePatcher, MPCanvasUI, MPClient, MPPatches, MPServer, Plugin, Protocol, context log.
- Launcher .bat files (_launch_client_internal.bat, _launch_host_internal.bat, launch-mp-test.bat) left as untracked-local-only per user (contain absolute paths + Steam app IDs).  (situational: this repo)
- Inline identity used for commit (`git -c user.email=... -c user.name=... commit`) — no persistent git config modification.  Prior commits use the same identity allscott <allscott20@gmail.com>.  (rule)

2026-05-22 — ✅ Phase 1b COMPLETE.  Suppress-then-mirror architecture working.
- For-rent test: client's map highlights now match host's exactly (no extras), confirmed by user.
- For-sale test: client's map highlights match host's exactly after deploy of buildingsForSale sync + RealEstateHelper.RunDaily suppression + RefreshMapFilters hook, confirmed by user.
- Architectural learnings captured below in REFERENCE; key takeaway is "suppress generation, mirror authoritative state, refresh UI explicitly."  (rule)

2026-05-22 — ✅ Phase 2 COMPLETE (interior sync — designs, prices, dirt, items).
- Confirmed working by user: client now sees host's items (shelves, products, furniture) inside buildings, including live updates within 2s of host modifications.
- Architecture proven: subscription-based on entry, polling-based diff broadcast while inside, element-level/per-item refresh primitives on apply.  Same pattern usable for any future per-object sync work.

2026-05-22 — Phase 2b deployed (items: shelves, products, furniture).
- Per-item primitive identified: BuildingManager.InstantiateSingleInstance(ItemInstance, bool onlyVisual).  The items analog of InteriorElement.Deserialize.
- Removal primitive: UnityEngine.Object.Destroy on ItemController.gameObject — filtered to ItemControllers whose ItemHelper.GetBuildingRegistration matches the active reg (don't wipe inventory items etc.).
- Protocol: added ItemInstanceInfo + nested DTOs (CargoInstanceInfo, NestedCargoInstanceInfo, AttachableChildInfo, CustomColorInfo, PlayerItemPurchaserSettingsInfo, Vector3Info) to Protocol.cs.  Active fields only — 11 [Obsolete] ItemInstance fields skipped.  Enums → int, SerializableVector3/Quaternion → flat floats, SerializableColor → packed int (Phase 1 pattern).
- Host: InteriorSync.SerializeItemInstance walks reg.itemInstances dict; InteriorSync.Tick's hash now includes per-item id+name+position (rounded to cm)+rotation (rounded)+state+alias+cargo so item adds/moves/removes/restocks trigger broadcasts.
- Client: GameStatePatcher.DeserializeItemInstance rebuilds IL2CPP ItemInstance from DTO.  ApplyInteriorSnapshot clears reg.itemInstances dict and repopulates from payload.  TryRefreshActiveInteriorIfMatches now also calls RefreshItemsForActiveBuilding which (a) destroys existing ItemController GameObjects matching the active reg, (b) re-spawns via InstantiateSingleInstance(ii, onlyVisual:false) so items participate in employee/customer systems on client.
- Compile gotchas: ItemController.ItemInstance is the property name (uppercase, via getter), not .itemInstance.  CustomColorChannel lives in BigAmbitions.Items namespace (transitively from BigAmbitions.Items.dll, already referenced).  (rule)
- Build clean, deployed.  Test plan: walk client into host's coffee shop after host has placed shelves + products; verify items show on client.  Host adds/moves an item; verify the broadcast fires within 2s and client visual updates.

2026-05-22 — ✅ Phase 2a interior refresh SOLVED via element-level Deserialize.
- After multiple test cycles, found the canonical primitive: `InteriorElement.Deserialize(SerializedInteriorDesign)`.  Walks the scene's InteriorElement components, matches each by UUID to a SerializedInteriorDesign in reg.interiorDesigns, calls Deserialize on each.  Works repeatedly — every paint by host gets reflected on client.
- DISPROVEN candidates (do NOT use for live re-paint):
   * `BuildingManager.LoadBuilding(false)` — returns true but doesn't re-apply designs to existing InteriorElements.  Probably only sets up materials on first-time element initialization.
   * `BuildingManager.ApplyInteriorDesign(building, elements)` — same.  Likely resolves the registration via building.GetRegistration() which returns a different instance than gi.BuildingRegistrations (probably BuildingHelper.AllBuildingRegistrationDictionary's static copy).  So even after our snapshot updates gi.BuildingRegistrations, ApplyInteriorDesign reads stale data.  (rule)
- The hash diagnostic that proved this: client-side log showed three distinct full-list hashes across three host paint actions (0xFEDA2C09 → 0x14AEF081 → 0x7E1CB21D).  Data was reaching the client correctly.  Yet ApplyInteriorDesign/LoadBuilding didn't re-paint.  Element.Deserialize did.  (rule)
- ARCHITECTURAL LESSON for future sync work: prefer the LOWEST-LEVEL primitive that operates directly on the scene GameObject (component-method), not the high-level "apply everything" methods.  High-level methods may resolve state from a different registration instance, skip when "already loaded," or only run on first-time setup.  Element-level methods bypass all that.  (rule)
- For Phase 2b (items), the analogous primitive is the per-item spawn/update method — not LoadItems or InstantiateInstances (which look like the high-level "apply everything" variants).  We'll need to find what gets called when a player single-places an item.

2026-05-22 — Phase 2a refresh v3: revert to LoadBuilding(false) with diagnostic + dirt-noise filter.
- Earlier "LoadBuilding has internal caching" claim was unsupported speculation — inspection of BuildingManager fields finds no isLoaded/loadedBuilding cache.  Apparent first-works-rest-don't behavior more likely caused by (a) host's later changes not yet committed to reg.interiorDesigns at poll time, (b) polling firing on dirt-fluctuation noise so "subsequent broadcasts" carried identical design data.  (rule)
- Reverted to LoadBuilding(false) as the canonical "reload everything from current reg.*" call.  This is the unified approach: works for designs, layout, items, future fields — the game's own one-stop entry-time loader.
- Added diagnostic: log reg field counts BEFORE and AFTER LoadBuilding so we can confirm it's actually reading fresh data each call.  Returns bool (success); we log that too.
- Tightened InteriorSync hash: round DirtSpot.Dirtiness to 1 decimal place before hashing so NPC-walking micro-fluctuations stop triggering broadcasts.  Real changes still detected.  (rule)
- Build clean, deployed.  Goal: confirm whether LoadBuilding is the single universal refresh, or whether per-type refresh methods are truly necessary.

2026-05-22 — Phase 2a refresh v2: switch from LoadBuilding to ApplyInteriorDesign.
- Symptom from second test: AI buildings refreshed correctly, but the host-OWNED building (host bought + named 'test', 15 FourthAvenue, a CoffeeShop) only refreshed ONCE on initial entry.  Subsequent host changes were broadcast by the polling tick (3 diffs visible in host log) and applied to client's reg.* fields successfully (3 receipts in client log) but visual stayed at the first refreshed state.
- Diagnosis: LoadBuilding(false) likely has internal "already loaded" caching — after the first call it short-circuits subsequent invocations, so further ApplyInteriorSnapshot calls update data but the visual stays frozen.  (rule)
- Fix: switch to BuildingManager.ApplyInteriorDesign(Building, InteriorElement[]) — a focused static method that re-paints walls/floor/ceiling from the registration's current interiorDesigns.  No "already loaded" gate.  Doesn't re-instantiate items either (Phase 2b concern).
- Building reference obtained via reg.BuildingCached (IL2CPP property).  InteriorElement[] gathered via FindObjectsOfType<InteriorElement> at refresh time.
- Build clean, deployed.

2026-05-22 — Phase 2a refresh: trigger LoadBuilding on snapshot apply for active interior.
- Symptom from first test: client interior data IS applied (log shows "Interior applied: designs=290 dirt=225") but walls/floor stay default visually.  Writing to reg.interiorDesigns updates the data model but doesn't re-paint the meshes — same issue we had with Phase 1 signs.
- Fix: after ApplyInteriorSnapshot writes fields, call BuildingManager.LoadBuilding(false) IFF the local player is currently inside that building (BuildingManager.buildingRegistration matches the address).  LoadBuilding re-runs the full layout/designs/items pipeline from the now-fresh data.  No-op when outside.
- LoadBuilding(false) is heavy (re-instantiates items GameObjects), but Phase 2a's items aren't synced yet anyway so flicker is acceptable for now.  Phase 2b may need a lighter ApplyInteriorDesign-only call.
- Layout='' observed for InteriorInstallationFirm businesses on host — that's apparently their actual state (these are showroom-style businesses with no fixed layout template).  Not a sync bug.  (situational: AI businesses with empty Layout — investigate further only if visible problems persist for retail/office buildings)
- Build clean, deployed.

2026-05-22 — Phase 1c hotfix: schedule sync added so client businesses aren't "always closed."
- Symptom: client saw every business as closed and couldn't enter them — Phase 1b suppression skipped SetRivalBuildings etc. which had been populating default operating-hour schedules.  scheduleDays was empty on client after sync.
- Added ScheduleDayInfo (Day, IsOpen, OpeningHourSlots) + OpeningHourSlotInfo (StartingHour, EndingHour) DTOs to Protocol.cs.  Added `SharedSchedule` (bool) + `Schedule` (List<ScheduleDayInfo>) to BusinessInfo.
- BusinessSync.ReadInfo now walks reg.scheduleDays → DTOs; ApplyBusinessInfoLocal clears reg.scheduleDays and rebuilds from payload.  EqualInfo + ScheduleEqual added for change detection.
- Skipped reg.scheduleDays[i].workShifts (employee assignments) for now — separate concern; sync them later.  (situational: Phase 1c minimal)
- csproj: added DayNightCycle reference (DayOfWeekOrdered enum lives there).
- Build clean, deployed.

2026-05-22 — Phase 2a (interior sync plumbing + simple fields) deployed.
- New file src/InteriorSync.cs (host-side per-building subscriber set + diff-poll Tick).
- Protocol: MessageType.InteriorRequest (60), InteriorSnapshot (61), PlayerExitedBuilding (62).  Payload types InteriorRequestPayload, InteriorSnapshotPayload, PlayerExitedBuildingPayload + helper DTOs InteriorDesignInfo, InteriorMaterialInfo, RetailPriceInfo, DirtSpotInfo.
- Subscription model: client sends InteriorRequest on entering building X, host adds peer to X's subscriber set and sends initial snapshot.  While subscribed, every Tick (2s) host hashes the building's interior state and broadcasts to subscribers if changed.  Client sends PlayerExitedBuilding on exit; host removes peer.  HandlePeerDisconnected cleans up on drop.
- Entry hook: extended Patch_DelayedEnterBuilding Prefix — reads BuildingManager.__instance.buildingRegistration to get the address, sends InteriorRequest.  Existing TrafficSync.OnEnteredBuilding call kept intact.
- Exit hook: new Patch_ExitFromBuilding_InteriorSync Prefix on BuildingManager.ExitFromBuilding — reads buildingRegistration BEFORE the method runs, sends PlayerExitedBuilding.
- Client apply (GameStatePatcher.ApplyInteriorSnapshot): overwrites reg.Layout, reg.interiorDesigns, reg.retailPrices, reg.dirtSpots from the payload.  ItemInstances explicitly deferred to Phase 2b.
- Server: SendInteriorSnapshotTo(peer, snap) for initial reply; BroadcastInteriorSnapshotTo(peerIds, snap) for diff push to a building's subscribers.
- Tick: InteriorSync.Tick() added to the existing MPCanvasUI tick chain, just after BusinessSync.Tick().
- csproj: added BigAmbitions.InteriorDesigner reference (SerializedInteriorDesign lives there).
- Compile gotcha: IL2CPP-Interop ItemName enum lives in BigAmbitions.Items namespace (not Enums.ItemName); cast `(BigAmbitions.Items.ItemName)rp.ItemName`.  (rule)
- Compile gotcha: Harmony Prefix with `BuildingManager __instance` works directly (typed); use `object` only when the type isn't known at compile time, and then .TryCast<T>().  (rule)
- Build clean, deployed to both BepInEx plugins dirs.

═══════════════════════════════════════════════════════════════════
REFERENCE — Phase 1b key learnings (for future sync work)
═══════════════════════════════════════════════════════════════════

1. **"Wipe-then-mirror" is fragile; "suppress-then-mirror" is robust.**
   Letting the client run its own generator then overwriting fields one-by-one leaves residual state in fields you didn't sync.  Better: Harmony-patch the generator functions to no-op on client, so the client never makes independent assignments.  Then mirror the host's authoritative state on top.  The verification pass (count any reg in a non-default state before applying) lets you confirm the suppression is complete.

2. **Game generator methods named for OUTPUT often do INPUT.**
   `MarkBuildingsForRentAndGetAvailableOnes` doesn't decide what becomes for-rent — it just catalogs whatever was left UNASSIGNED by the preceding methods.  The assignment methods are `SetupResidentialBuildings`, `SetupWarehouseBuildings`, `DistributeBuildingsToRivals`, `SetupRivalFactories`, `SetRivalBuildings`.  When suppressing, target the assignment methods, not the cataloging method (which is the safe baseline).

3. **`AvailableForRent` is not the for-rent flag.**
   Despite the name, it's `false` for every building on both sides at fresh-game time.  The map's "for rent" highlight is computed from `BusinessTypeName == Empty` AND not-in-`buildingsForSale` AND not-in-`realEstate` AND no rival ownership.  Don't trust field names — verify with the decompile of the consumer (`CityMapFilters.ApplyFilters` in this case).

4. **`gi.buildingsForSale` is RNG-populated per side; must be synced.**
   `RealEstateHelper.UpdateBuildingsForSale` picks ~3 buildings per neighborhood daily.  Different RNG → different picks → different for-sale set.  Wipe-and-rebuild the entire list on each snapshot rather than diff-and-patch (it's small; the simplicity is worth it).  Hash-diff on host detects daily changes and rebroadcasts.

5. **UI doesn't auto-refresh when data changes — call the game's own refresher.**
   `CityMapFilters.ApplyFilters()` is the function the game itself calls after any state change that affects map highlights.  Call it ourselves after each snapshot apply.  Without this, data is correct but visuals stay frozen until the user toggles a filter manually.  Same pattern likely applies to other UI panels (BizMan, market insider).

6. **Per-player state vs. world state.**
   `gi.realEstate` is each player's OWN owned-property list, not a shared list.  Don't blanket-sync it host→client; clients should see host's owned buildings as "owned by someone else."  Currently we sidestep this by removing the building from `buildingsForSale` (so it can't be highlighted as for-sale).  Richer per-player ownership display is a Phase 4 concern.

7. **Suppression must let the skeleton run.**
   `CityGenerator.PopulateBuildings` (creates the 825 `BuildingRegistration` entries in `gi.BuildingRegistrations`) and `EnsureTutorialBuildingsAreAvailable` must NOT be suppressed — otherwise the snapshot has no registrations to write to.  Only suppress the "make a choice" methods.

8. **Verification before AND after the apply is worth the code.**
   `VerifyCleanBaseline` (before) confirms suppression took effect (`dirty=0`).  `VerifyMatchesPayload` (after) confirms the apply wrote every field successfully (`matched=N mismatchBiz=0`).  When something breaks later, those two logs immediately localize the issue.

═══════════════════════════════════════════════════════════════════

2026-05-22 — Map filter refresh hook added.
- New helper GameStatePatcher.RefreshMapFilters() — calls FindObjectsOfType<CityMapFilters> and invokes ApplyFilters() on each instance.  No-op if no CityMapFilters in scene yet.
- Invoked at the end of ApplyBusinessSnapshot, after VerifyMatchesPayload.  Mirrors what the game does natively after rent/sale/RealEstateHelper.RunDaily ticks — without it the client's map data is correct but colored highlights stay frozen until the user toggles a filter manually.  (rule)
- Build clean, deployed.

2026-05-22 — For-sale marketplace (gi.buildingsForSale) sync deployed.
- New protocol type BuildingForSaleInfo (AddressKey, BuildingPrice, SquareMeters, AcceptOfferRate).  Added List<BuildingForSaleInfo> BuildingsForSale to BusinessSnapshotPayload.
- Host: BusinessSync.ReadBuildingsForSale walks gi.buildingsForSale and packages every entry into the payload.  BuildFullSnapshot includes it.  BusinessSync.CheckBuildingsForSaleChange polls each Tick — hashes the list, if changed (e.g. after daily real-estate update) broadcasts a fresh full snapshot via MPServer.BroadcastBusinessSnapshot.
- Client: GameStatePatcher.ApplyBuildingsForSale wipes gi.buildingsForSale and rebuilds from the payload (one BuildingForSale per entry, with reg.Address pulled from the local registration).  Wipe count + add count logged.
- Suppression: Patch_RealEstateHelper_RunDaily_SkipOnClient — Harmony Prefix returns false on client, skipping the entire daily real-estate tick (UpdateBuildingsForSale, UpdatePlayerRealEstate, SimulateCompetitorBuyingAIBuildings, SimulateCompetitorBuyingPlayerBuildings).  Host's snapshot is the sole authority for marketplace state.
- gi.realEstate (player-owned property) NOT yet synced — empty for both at fresh-game time, becomes relevant once any player buys a building.  Backlog.
- Build clean, deployed.

2026-05-22 — Phase 1b suppression + verification deployed.
- New Harmony Prefix patches in MPPatches.cs (gated on MPClient.IsConnected, so host/SP unchanged):
   * Patch_CityGenerator_SetupResidentialBuildings_SkipOnClient
   * Patch_CityGenerator_SetupWarehouseBuildings_SkipOnClient
   * Patch_CityGenerator_DistributeBuildingsToRivals_SkipOnClient
   * Patch_CityGenerator_SetupRivalFactories_SkipOnClient
   * Patch_CityGenerator_SetRivalBuildings_SkipOnClient
   * Each Prefix returns false on client, allowing PopulateBuildings + EnsureTutorialBuildingsAreAvailable to still run (registration skeleton needs to exist for the snapshot to write to).
- New verification passes in GameStatePatcher.ApplyBusinessSnapshot:
   * VerifyCleanBaseline(payload) — runs BEFORE applying; counts regs with businessTypeName != Empty, buildingOwnerRivalId, businessOwnerRivalId, RentedByPlayer.  If non-zero, logs warning ("Baseline NOT clean — suppression may be incomplete").  Logs first 5 example addresses.
   * VerifyMatchesPayload(payload) — runs AFTER applying; per address compares client's BusinessTypeName to what host sent.  Logs first 5 mismatches.  Expected: zero mismatches.
- Expected diagnostic flow after test:
   * "Suppress: SetupResidentialBuildings skipped on client" + 4 sibling lines logged once during city init on client.
   * "Baseline check (BEFORE apply): total=825 dirty=0" — confirms suppression worked.
   * "Baseline is 100% clean ✓"
   * "Business snapshot applied: 825/825 buildings."
   * "Match check (AFTER apply): matched=825 mismatchBiz=0 addrNotInPayload=0"
   * "Client now mirrors host's snapshot 100% ✓"
- Build clean, deployed.
