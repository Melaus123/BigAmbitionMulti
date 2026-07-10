using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using Helpers;
using Intro;
using UI.Load;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Canvas/uGUI multiplayer panel.  Toggle F8.  Draggable via title bar.
    ///
    /// CanvasScaler = ConstantPixelSize/scaleFactor 1 → 1 canvas unit = 1 screen pixel.
    /// All layout dimensions are therefore plain pixels.
    /// </summary>
    public class MPCanvasUI : MonoBehaviour
    {
        // ── colours ──────────────────────────────────────────────────────────
        private static readonly Color C_BG       = new Color(0.12f, 0.12f, 0.14f, 0.97f);
        private static readonly Color C_HDR      = new Color(0.20f, 0.40f, 0.70f, 1.00f);
        private static readonly Color C_FIELD    = new Color(0.18f, 0.18f, 0.22f, 1.00f);
        private static readonly Color C_FIELDFOC = new Color(0.25f, 0.35f, 0.55f, 1.00f);
        private static readonly Color C_BTN      = new Color(0.22f, 0.48f, 0.22f, 1.00f);
        private static readonly Color C_BTNHOV   = new Color(0.28f, 0.60f, 0.28f, 1.00f);
        private static readonly Color C_BTNBLUE  = new Color(0.18f, 0.38f, 0.65f, 1.00f);
        private static readonly Color C_STOP     = new Color(0.55f, 0.20f, 0.20f, 1.00f);
        private static readonly Color C_WHITE    = Color.white;
        private static readonly Color C_RED      = new Color(1f,    0.35f, 0.35f, 1f);
        private static readonly Color C_GREEN    = new Color(0.35f, 1f,    0.35f, 1f);
        private static readonly Color C_LBLGREY  = new Color(0.70f, 0.70f, 0.75f, 1f);
        private static readonly Color C_YELLOW   = new Color(1f,    0.85f, 0.20f, 1f);

        // ── layout (pixels) ───────────────────────────────────────────────────
        private const float W      = 540f;
        private const float HDR    = 40f;
        private const float PAD    = 12f;
        private const float FW     = W - PAD * 2;   // 516
        private const float LH     = 22f;
        private const float LADV   = 30f;
        private const float FH     = 32f;
        private const float FADV   = 42f;
        private const float BH     = 36f;
        private const float BADV   = 48f;
        private const float SGAP   = 12f;
        private const float CSTART = -(HDR + 10f);  // -50

        private const int SZ_HDR = 16;
        private const int SZ_LBL = 14;
        private const int SZ_FLD = 15;
        private const int SZ_BTN = 15;
        private const int SZ_STS = 14;
        private const int SZ_SEP = 10;

        // ── model ─────────────────────────────────────────────────────────────
        // Hidden by default now that the native main-menu "Multiplayer" entry is the
        // way in.  F8 still toggles the legacy panel as a dev fallback.
        private bool   _visible = false;
        // #1 — initialised from MPConfig in Awake() so the F8 panel pre-fills
        // with the previously-used (or Steam-detected) name, not "Player1".
        private string _name    = "Player1";
        private string _port    = "7777";
        private string _ip      = "127.0.0.1";

        // ── state transition tracking (for auto-hide) ─────────────────────────
        private MPState _prevState = MPState.Idle;

        // ── player sync ───────────────────────────────────────────────────────
        private float _posSyncTimer;
        private float _worldSnapshotTimer;
        private bool  _wasInGame;

        // Startup pause hold — host-side watchdog so the game can't freeze forever
        // if a player gets stuck loading and never reports in-game.
        private float _startupHoldElapsed;
        private const float STARTUP_HOLD_TIMEOUT = 180f;   // 3 min (user spec) — countdown shown on the wait screen

        // Sends the local player's appearance once, after the character is ready.

        // ── periodic host-side sync timers ────────────────────────────────────
        /// <summary>Counts down to next game-time/speed heartbeat (host, every 3 s).</summary>
        private float _timeSyncTimer = 3f;
        /// <summary>Counts down to next market broadcast (host, ~60 s).</summary>
        private float _marketSyncTimer = 60f;
        // Portrait is written to disk lazily (after we first broadcast the
        // profile), so the initial profile carries portrait=none.  Re-send the
        // profile once the portrait file appears, until it goes out once.
        private float _profileResendTimer = 5f;
        private float _profileResendElapsed = 0f;
        private bool  _profileNameConfirmed = false;   // a REAL character name (not the PlayerId fallback) has gone out

        // ── in-game player HUD (F9 toggle) ────────────────────────────────────
        private bool        _hudVisible;
        private GameObject? _hudGO;
        private TextMeshProUGUI?   _hudHeader;
        private TextMeshProUGUI[]  _hudRows = new TextMeshProUGUI[8];

        // ── in-game multiplayer window (Phase 6: draggable; players + chat;
        //    resize + transparency).  Replaces the old fixed HUD; F9 toggles it. ──
        private bool          _mpWinVisible;
        private bool          _mpWasInGame;          // tracks in-game transitions for auto-show/hide
        private GameObject?   _mpWin;
        private RectTransform? _mpWinRT;
        private RectTransform? _mpTitleRT;          // drag handle
        private TextMeshProUGUI?  _mpRosterLabel;   private RectTransform? _mpRosterRT;  // single multi-line roster
        private TextMeshProUGUI?  _mpPlayersHdr;     // "Players (N)" (always active)
        private TextMeshProUGUI?  _mpCollapseLbl;   private RectTransform? _mpCollapseRT;  // ▾/▸ toggle
        private bool          _mpPlayersCollapsed;
        private TextMeshProUGUI?  _mpChatLog;        private RectTransform? _mpChatPanelRT;
        private TMP_InputField?   _mpChatInputField; private RectTransform? _mpChatInputRT;
        private string        _mpChatInput = "";    private bool _mpChatFocus;
        private RectTransform? _mpSendRT;
        private RectTransform? _mpGripRT;            // bottom-left corner resize handle
        private bool          _mpResizing;           private Vector2 _mpResizeStartMouse; private Vector2 _mpResizeStartSize;
        private RectTransform? _mpOpacityTrackRT; private RectTransform? _mpOpacityFillRT; private RectTransform? _mpOpacityKnobRT;
        private bool          _mpDragging;          private Vector2 _mpDragLast;
        private bool          _mpOpacityDragging;
        private int           _mpChatVersionSeen = -1;
        private string        _mpRosterSig = "\0";    // signature of the roster+target, to rebuild chips only on change

        // Chat redesign: recipient chips, close button, To-prefix.
        private RectTransform?    _mpCloseRT;
        private GameObject?       _mpChipsRow;     private RectTransform? _mpChipsRowRT;
        private GameObject?       _mpChipsContent;
        private float             _chipsTotalW;
        private float             _chipScroll;
        private TextMeshProUGUI?  _mpToLbl;  private RectTransform? _mpToRT;
        private readonly List<(RectTransform rt, Image img, string who)> _mpChips = new();
        private string            _chatTarget = "";   // "" = everyone
        private const float CHIP_H  = 24f;
        private const float MP_TO_W = 86f;
        private bool          _mpChatNavBlocked;           // true while chat focus is suppressing player movement
        private float         _mpOpacity = 0.88f;   // current window opacity [0.1..1]
        private bool          _mpStyled;            // rounded-corner sprite applied (lazy, once assets captured)
        private int           _mpChatScroll;          // lines scrolled back from newest (0 = newest)
        // Images whose alpha follows the opacity slider (background + chrome ONLY).
        // Text, the slider itself, the roster and chat stay fully opaque so they're
        // readable even at opacity 0.
        private readonly List<(Image img, float baseA)> _mpFade = new();

        // ── startup pause screen (full-screen "waiting for players") ──────────
        private GameObject?      _startupScreenGO;
        private TextMeshProUGUI? _startupScreenTxt;

        // ── drag ─────────────────────────────────────────────────────────────
        private RectTransform _panelRT = null!;
        private bool    _dragging;
        private Vector2 _dragOff;

        // ── focus  (0=none 1=name 2=port 3=ip 4=joinPort) ────────────────────
        private int _focus;

        // ── root objects ──────────────────────────────────────────────────────
        private GameObject _canvasGO = null!;

        // ── per-state panes ───────────────────────────────────────────────────

        // ── Idle pane interactive elements ────────────────────────────────────
        private Image           _imgName     = null!;
        private TextMeshProUGUI _txtName     = null!;
        private RectTransform   _rtName      = null!;
        private Image           _imgPort     = null!;
        private TextMeshProUGUI _txtPort     = null!;
        private RectTransform   _rtPort      = null!;
        private Image           _imgIp       = null!;
        private TextMeshProUGUI _txtIp       = null!;
        private RectTransform   _rtIp        = null!;
        private Image           _imgJoinPort = null!;
        private TextMeshProUGUI _txtJoinPort = null!;
        private RectTransform   _rtJoinPort  = null!;
        private Image           _imgHostBtn  = null!;
        private RectTransform   _rtHostBtn   = null!;
        private Image           _imgJoinBtn  = null!;
        private RectTransform   _rtJoinBtn   = null!;

        // ── Lobby-host live labels ────────────────────────────────────────────
        private string          _clientCash       = "4200";
        private string _selectedDifficulty = "Normal";
        private static readonly string[] _difficulties = { "Easy", "Normal", "Hard" };

        // ── Game settings editor ──────────────────────────────────────────────
        private GameVariablesDto _hostSettings = MPServer.Preset("Normal");
        private GameObject?   _settingsPanelGO;
        private Image?        _settingsContentImg;   // content bg — rounded with the menu sprite on first open
        private bool          _settingsHidLobby;      // remembers we hid the lobby behind the settings modal
        private RectTransform _rtSettingsClose = null!;
        private bool _settingsOpen;
        private readonly List<(RectTransform rt, System.Action act)> _settingsHits = new();
        private readonly List<System.Action> _settingsRefreshers = new();
        private readonly List<(RectTransform rt, string desc)> _settingsTips = new();
        private readonly List<NumField> _numFields = new();
        private int    _editField  = -1;       // index into _numFields, or -1
        private string _editBuffer = "";
        private GameObject?      _tooltipGO;
        private TextMeshProUGUI? _tooltipTxt;

        /// <summary>A click-to-type numeric value field in the settings panel.</summary>
        private sealed class NumField
        {
            public RectTransform   Rt  = null!;
            public TextMeshProUGUI Lbl = null!;
            public System.Func<float>   Get = null!;
            public System.Action<float> Set = null!;
            public float  Min, Max;
            public string Fmt = "0";
        }

        // ── Lobby-client live labels ──────────────────────────────────────────

        // ── In-game host labels ───────────────────────────────────────────────
        private RectTransform   _rtStopBtn      = null!;

        // ── In-game client labels ─────────────────────────────────────────────
        private TextMeshProUGUI _txtConnAs      = null!;
        private TextMeshProUGUI _txtConnHost    = null!;
        private RectTransform   _rtDiscBtn      = null!;

        // ── Connecting labels ─────────────────────────────────────────────────
        private RectTransform   _rtCancelBtn    = null!;

        // ── Status bar ────────────────────────────────────────────────────────
        private TextMeshProUGUI _txtStatus      = null!;

        // ── hit-test rect for title bar ───────────────────────────────────────
        private RectTransform _rtHdr = null!;

        // ── deferred init ─────────────────────────────────────────────────────
        private bool _built;
        private int  _initDelay;
        private bool _crashReportPopupVisible;
        private string _crashReportMessage = "";
        private readonly List<string> _crashReportAttachments = new();
        private GameObject? _crashReportGO;
        private RectTransform? _crashReportRT;
        private RectTransform? _crashReportInputRT;
        private RectTransform? _crashReportTagRT;
        private RectTransform? _crashReportAttachRT;
        private RectTransform? _crashReportSendRT;
        private RectTransform? _crashReportDismissRT;
        private TMP_InputField? _crashReportInputField;
        private bool   _crashReportSending;       // an upload is in flight: block re-send + hold the status line
        private string _crashReportResult = "";   // "" = none; else the status line to display (submit/sent/failed)
        private float  _crashReportAutoCloseAt;    // >0 = unscaled time to auto-close the popup after a success
        private TextMeshProUGUI? _crashReportTitleLbl;
        private TextMeshProUGUI? _crashReportBodyLbl;
        private TextMeshProUGUI? _crashReportTagLbl;
        private TextMeshProUGUI? _crashReportStatusLbl;
        private bool _crashReportFocus = true;
        private bool _crashReportAutoFocusPending;
        private bool _crashReportIsCrash = true;
        private Sprite? _crashReportStyledWith;
        private int _bugReportTagIndex;

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>The live UI component (one DontDestroyOnLoad instance) so
        /// Harmony patches can reach instance state (e.g. chat typing focus).</summary>
        public static MPCanvasUI? Instance { get; private set; }

        /// <summary>Clear chat typing-focus + input suppression.  Called from the
        /// HandleEscapeClick guard so Escape always escapes our input block instead
        /// of crashing the game's escape handler.</summary>
        public static void ClearChatFocus()
        {
            if (Instance != null) Instance._mpChatFocus = false;
            MPChat.SuppressGameInput = false;
        }

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            // #1 — pre-fill the F8 panel from persisted MPConfig (saved last
            // time the user clicked Host/Join).  Awake runs after Plugin.Load
            // → MPConfig.Init, so PlayerId is already resolved here.
            if (!string.IsNullOrWhiteSpace(MPConfig.PlayerId))
                _name = MPConfig.PlayerId;
            _port = MPConfig.Port.ToString();
            _ip   = MPConfig.HostIP;
            Plugin.Logger.LogInfo(
                $"[UI] F8 panel pre-fill: name='{_name}' port={_port} ip={_ip}");

            if (MPBugReport.PendingCrashDetected)
            {
                _crashReportPopupVisible = true;
                _crashReportIsCrash = true;
                _crashReportMessage = "Previous game session appears to have crashed.";
                _crashReportAttachments.Clear();
                _crashReportAutoFocusPending = true;
            }

            // Stage-4 migration #1: the join quiesce ends on the lifecycle
            // WorldReady EVENT (replaces the hand-tuned 4s timer).
            MPLifecycle.PhaseChanged += OnLifecyclePhase;
        }

        // World-integrity one-shot (approved diagnostics batch, 2026-07-09): armed at scene-ready,
        // fires once ~30s later when the rival cache has filled.
        private float _worldHealthAt = -1f;

        private void TickWorldHealth()
        {
            if (_worldHealthAt < 0f || Time.unscaledTime < _worldHealthAt) return;
            _worldHealthAt = -1f;
            if (MPServer.IsRunning || MPClient.IsConnected)
                GameStatePatcher.LogWorldHealth("scene ready +30s");
        }

        // Task #5 (Prabaha report, 2026-07-08): a leftover marker said NOTHING about where the dead
        // session was — menu-kill loop and mid-game crash looked identical. Stamp phase + uptime ~30s.
        private float _nextCrashHeartbeat;

        private void TickCrashHeartbeat()
        {
            if (Time.unscaledTime < _nextCrashHeartbeat) return;
            _nextCrashHeartbeat = Time.unscaledTime + 30f;
            string phase;
            try
            {
                phase = !IsInGame() ? "main menu"
                      : MPServer.IsRunning ? $"in-game (host, day {SafeDay()})"
                      : MPClient.IsConnected ? $"in-game (client, day {SafeDay()})"
                      : $"in-game (offline, day {SafeDay()})";
            }
            catch { phase = "unknown"; }
            MPBugReport.Heartbeat(phase);
            TickDuplicateInstallWarning();
            TickSteamProbe();
        }

        private static bool _steamProbed;
        /// <summary>Steam-connect campaign, slice-1 probe: one line confirming we can
        /// link + read the game-initialized Facepunch SteamClient.  Gates slice 2
        /// (relay transport rides the same client).  Logs once, first heartbeat
        /// where Steam reports valid; silent otherwise.</summary>
        private static void TickSteamProbe()
        {
            if (_steamProbed) return;
            try
            {
                if (!Steamworks.SteamClient.IsValid) return;
                _steamProbed = true;
                Plugin.Logger.LogInfo($"[Steam] client valid: app={Steamworks.SteamClient.AppId} id={Steamworks.SteamClient.SteamId} name='{Steamworks.SteamClient.Name}'.");
            }
            catch (Exception ex)
            {
                _steamProbed = true;   // one shot either way — a throw means the link/runtime is the story
                Plugin.Logger.LogWarning($"[Steam] probe failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private bool _duplicateWarned;
        /// <summary>A second copy of this mod (ModsLocal + Workshop subscription)
        /// refuses to start (Plugin double-install guard) and records itself in an
        /// AppDomain slot — assembly statics are per-copy, so the slot is the only
        /// channel.  The LIVE copy announces it here, once, so the player learns
        /// to remove one install instead of wondering why the Mods list shows two.</summary>
        private void TickDuplicateInstallWarning()
        {
            if (_duplicateWarned) return;
            string? dup = null;
            try { dup = System.AppDomain.CurrentDomain.GetData("BAMP_DUPLICATE_ROOT") as string; } catch { }
            if (string.IsNullOrEmpty(dup)) return;
            _duplicateWarned = true;
            Plugin.Logger.LogWarning($"[Plugin] Duplicate install detected: a second copy at '{dup}' was refused. Remove either the Workshop subscription or the ModsLocal folder.");
            try { ModEntry.PatchIssues.Add($"duplicate install refused: {dup}"); } catch { }
            try
            {
                UI.Notification.Notifications.Show(UI.Notification.NotificationType.Error,
                    $"{MyPluginInfo.SHORT_NAME} is installed twice (Steam Workshop + local mods). The extra copy was disabled — please remove one install.");
            }
            catch { }
        }

        private static int SafeDay()
        { try { return SaveGameManager.Current?.Day ?? 0; } catch { return 0; } }

        private void OnApplicationQuit()
        {
            MPBugReport.MarkCleanShutdown();
            // Persist the local player's CURRENT character on a raw window-close (X / quit-to-desktop), the
            // same as the Save-and-Exit button — otherwise a window-close ships NOTHING and the session
            // keeps only what the last periodic autosave (or the host's host-only disconnect checkpoint)
            // captured, so a member can return as a brand-new player. PerformLocalSave is synchronous
            // (JoinSaveGameThreads blocks), so the .hsg is on disk before we exit; on one machine it lands
            // in the shared session folder the host's disconnect carry-forward then reads.
            try
            {
                if (IsInGame())
                {
                    if (MPServer.IsRunning)
                    {
                        // Round-37: the quit-save goes to the DISCONNECT variant — never the manual base
                        // (this direct PerformLocalSave(active) was one of the two "my save advanced
                        // without me saving" leaks; the join-save was the other).
                        Plugin.Logger.LogInfo("[MPSave] OnApplicationQuit — host quit-checkpoint before exit.");
                        MPSaveCoordinator.HostQuitCheckpoint();
                    }
                    else if (MPClient.IsConnected || MPClient.SessionEnded)
                    {
                        // Client: write the designated disconnect save (+ marker) — the trusted-newer file
                        // Phase 3 may restore on rejoin if the host's stored copy is older.
                        MPSaveCoordinator.WriteClientDisconnectSave();
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] OnApplicationQuit save: {ex.Message}"); }
        }

        private void OnLifecyclePhase(MPLifecycle.MPPhase prev, MPLifecycle.MPPhase next)
        {
            // LEFT THE WORLD → force-hide every in-game panel unconditionally
            // (the rest dock once survived all the way into the main menu).
            if (next == MPLifecycle.MPPhase.Menu || next == MPLifecycle.MPPhase.None
                || next == MPLifecycle.MPPhase.Lobby)
            {
                try
                {
                    if (_dock != null && _dock.activeSelf) { _dock.SetActive(false); Plugin.Logger.LogInfo("[Lifecycle] left world — rest dock force-hidden."); }
                    if (_hub != null && !_hubNative && _hub.activeSelf) { _hub.SetActive(false); _hubVisible = false; }
                    if (_joinPop != null && _joinPop.activeSelf) _joinPop.SetActive(false);
                }
                catch { }
            }

            // Fence visibility: the host excuses clients parked in Menu
            // (a connected client who cancels a load never disconnects —
            // the old fence waited the full 90s timeout on them).
            if (MPClient.IsConnected) MPClient.SendPhaseReport(next.ToString());

            if (next != MPLifecycle.MPPhase.WorldReady) return;
            MPClient.EndJoinQuiesce();
            GameStatePatcher.StripGhostVehicles("world-ready");   // leaked-ghost hygiene (data only)
            MPRegisterSync.StripOrphanSyntheticEmployees("world-ready");   // clear duty-staff a prior save left behind
            MPRegisterSync.RepairOrphanDutyShifts("world-ready");          // remove id-only duty shifts ("New Text" report) + log real-id orphans
            ApplyFreshSpawnWarp();  // fresh-character joins: designated start, not the prefab spot
            ApplySpawnSidestep();   // fresh games: one navmesh-validated de-stack, placement final
            // Placement diagnostic: position-restore runs in load-finish —
            // still at the default spawn here = it was skipped.
            try
            {
                var ch = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                if (ch != null) Plugin.Logger.LogInfo($"[UI] world-ready position: ({ch.position.x:F1}, {ch.position.y:F1}, {ch.position.z:F1})");
            }
            catch { }
        }

        // ── Update ────────────────────────────────────────────────────────────

        private void Update()
        {
            // Resolve + cache the save version folder on the MAIN thread, unconditionally
            // and as early as possible.  The network poll thread reaches MpCharacterFolder
            // (→ IL2CPP CurrentVersionFolderPath) when it handles a client's uploaded save
            // (host) OR a host-sent save to load (client) — and on the client that can
            // arrive before it is in-game.  Caching here, every frame from the start,
            // guarantees the path is ready so NO poll-thread handler ever calls IL2CPP.
            MPSaveManager.EnsureVersionCached();
#if BAMP_DEV
            // DIAG:INVESTIGATION(passenger-doors) — F6 spawns a visible row of the next few
            //   not-yet-seen vehicle types (their wheel/door data is dumped). F5 was unreliable
            //   (likely game-bound); F6 is the proven ablation key. Entry log proves it fired.
            if (Input.GetKeyDown(KeyCode.F6))
            {
                Plugin.Logger.LogInfo("[VehProbe] F6 pressed — spawning the next uncollected vehicle batch.");
                VehicleManager.DevProbeUncollected(5);
            }
            // F8: dev money cheat — add $1,000,000 to the local wallet (notice + log).
            if (Input.GetKeyDown(KeyCode.F8))
            {
                MPHub.ApplyMoneyDelta(1_000_000f, "DEV cheat (F8)", true);
                Plugin.Logger.LogInfo("[DevCheat] F8 — +$1,000,000.");
            }
#endif
            TickThemeCapture();      // frontload native font + rounded sprite (no timing dependency)
            TickCrashHeartbeat();    // task #5: stamp the session marker with where-we-are (~30s)
            MPLifecycle.Tick();      // single-source phase tracker (stage 4: first consumer live)
            MPRegisterSync.TickDuty();   // mirror the native Work activity into register duty (1s self-throttle)
            MPHub.TickSalePopups();      // rising +$ worker feedback (per-frame: smooth rise/fade)
            PassengerRide.Update();      // passenger ride: click-to-board → pin-to-seat → exit + remote riders
            PassengerHud.Tick();         // passenger's in-ride "Exit Vehicle" panel
            VehicleStoragePanel.Tick();  // non-owner shared-storage panel (refresh on cargo change / auto-close)
            TimeSync.TickStartupHold();  // round-36: had NO caller (dead since inception) — the hold's timeScale re-clamp
            GameStateReader.TickPendingNativePause();   // round-36c: converge the pause flag onto the last
                                                        // requested state (rate-limit drops lost it before)
            CustomerPuppets.Tick();      // round-41: simulator election (host) + puppet stream/render (both-inside shops)
            MergerFlip.Tick();           // merger slice 3: ownership-flip reconcile (1 Hz) + host state push (10s)
            MergerEmployeeSync.Tick();   // merger slice 5: schedule write-back scan on flipped shops (2s)
            MPRegisterSync.TickContextHeal();   // building-context self-heal (hand-vehicle entry skips the entry hook)
            MPRegisterSync.TickEconDigest();    // [EconProbe] one line per owned business per game-day (2026-07-09)
            MPRegisterSync.TickDutySummary();   // TEMPORARY: duty-broadcast activation watch, 10m (2026-07-09)
            TickWorldHealth();                  // one-shot world-integrity line ~30s after scene-ready (2026-07-09)
            RemotePlayerManager.TickVehicleCollisionIgnores();   // remote avatars must not shove vehicles
            TickMenuIntegration();   // Phase 5 — inject native "Multiplayer" button on the main menu
            MPSaveCoordinator.TickPendingLoad();   // mid-join menu detour completion
            // (quiesce-off 4s timer RETIRED 2026-06-11 — stage-4 migration #1:
            //  the quiesce now ends on the lifecycle WorldReady EVENT; see
            //  OnLifecyclePhase below.)
            TickOverlayWatchdog();   // stuck loading screen over a live world → force-dismiss
            TickJoinDialog();        // Phase 5 — connect-dialog input (when open)
            TickLobbyWindow();       // Phase 5 — lobby window input (when open)
            TickSavePicker();        // Phase 5 — save-picker input (when open)

            // Overlay-aware freeze gate — freezes once our loading overlay clears
            // (must run BEFORE TickStartupScreen so the wait screen appears the same
            // frame we freeze, with no flash of the un-synced world).
            MPSaveCoordinator.DiagPhase("Update: TickOverlayFreezeGate"); TickOverlayFreezeGate();

            // Startup pause screen — full-screen "waiting for players" overlay
            TickStartupScreen();

            // Rival-stats freshness (round-25): each machine re-publishes its self-stats every couple of
            // minutes (client push → host re-broadcasts the merged snapshot to everyone; host pushes its own
            // directly). Before this, another player's rival sheet only updated when THAT player opened
            // their own rivals app — arbitrarily stale for every viewer.
            TickRivalStatsPush();

            // Settings panel — hover tooltips + click-to-type field cursor
            TickSettingsPanel();

            // Drain the main-thread action queue
            // NOTE: TickTimeScaleMonitor was moved to LateUpdate so it runs AFTER Unity's
            // EventSystem processes button clicks (speed buttons, pause, sleep, rest).
            // EventSystem fires after ALL Update() calls, so Update-phase monitoring is
            // always 1 frame behind any button-click-driven timeScale change. — runs in ALL scenes (GameManager.Update
            // only exists in-game; this canvas is DontDestroyOnLoad so it's always active).
            MPSaveCoordinator.DiagPhase("Update: DrainQueue");
            long _dq = MPPerf.Begin(); GameStatePatcher.DrainQueue(); MPPerf.End("Drain", _dq);

            // Per-frame clock drift correction (drips small adjustments in if scheduled)
            MPSaveCoordinator.DiagPhase("Update: TickClockCorrection");
            TimeSync.TickClockCorrection();

            // Player sync ticks — run regardless of panel visibility or build state
            MPSaveCoordinator.DiagPhase("Update: TickGameLoadDetect");   TickGameLoadDetect();
            MPSaveCoordinator.DiagPhase("Update: TickStartupTimeout");   TickStartupTimeout();
            MPSaveCoordinator.DiagPhase("Update: TickWorldSnapshot");    long _ws = MPPerf.Begin(); TickWorldSnapshot(); MPPerf.End("WorldSnap", _ws);
            MPSaveCoordinator.DiagPhase("Update: TickPositionSync");     long _ps = MPPerf.Begin(); TickPositionSync(); MPPerf.End("PosSync*", _ps);
            MPSaveCoordinator.DiagPhase("Update: TickTimeSync");         TickTimeSync();
            MPSaveCoordinator.DiagPhase("Update: TickMarketSync");       TickMarketSync();
            MPSaveCoordinator.DiagPhase("Update: TickProfileResend");    TickProfileResend();
            MPSaveCoordinator.DiagPhase("Update: TickSuppressBlackOverlay"); TickSuppressBlackOverlay(); // backlog #6 fix
            // (F3-F12 diagnostic toggle tick removed 2026-06-10.)
            MPSaveCoordinator.DiagPhase("Update: TickMpSave");           TickMpSave();   // Phase 4 — suppress SP autosave, upload saves, host autosave
            MPSaveCoordinator.DiagPhase("Update: TickCashSync");         TickCashSync(); // Phase 4 — live cash stream to host
#if BAMP_DEV
            TickAnimProbe();   // run-animation pipeline snapshot (SP vs host diff)
#endif
            MPSaveCoordinator.DiagTick();    // count down the diagnostic window

            // Perf attribution: frame stats + the per-system summary every 10s.
            // ("PosSync*" wraps the whole sync chain; Probes/Parked/BizHost/etc.
            //  are subsets of it.  Anything choppy NOT showing in a bracket is
            //  either the game itself or our Harmony patch bodies.)
            MPPerf.FrameTick(Time.unscaledDeltaTime);

            if (!_built)
            {
                if (++_initDelay < 30) return;
                try   { BuildCanvas(); _built = true; Plugin.Logger.LogInfo("[UI] Canvas built OK."); }
                catch (Exception ex) { _built = true; Plugin.Logger.LogError($"[UI] Build failed: {ex}"); }
                return;
            }

            if (_canvasGO == null) return;
            TickCrashReportPopup();

            // The MP window is an IN-GAME widget: auto-shown when a game is live,
            // forced hidden everywhere else (menu/intro).  F9 toggles it while in
            // game.  This is what keeps it from lingering after "exit to menu".
            bool mpInGame = _mpWin != null && IsInGame()
                          && (MPServer.IsRunning || MPClient.IsConnected);
            if (!mpInGame && _mpWin != null)
            {
                _mpWinVisible = false;
                if (_mpWin.activeSelf) _mpWin.SetActive(false);
                _mpChatFocus = false; _mpDragging = false; _mpOpacityDragging = false; _mpResizing = false;
                SyncChatNavBlock(false); MPChat.SuppressGameInput = false;
            }
            if (mpInGame && !_mpWasInGame)
            {
                // Default CLOSED on entering a game (user 2026-06-10) — the
                // phone Chat button opens it; the badge/pulse signal unread.
                _mpWinVisible = false;
                StyleMpWindow();        // round corners + native font once assets are captured
                _mpWin!.SetActive(false);
            }
            else if (!mpInGame && _mpWasInGame)
            {
                _mpWinVisible = false;  // leaving the game → always hide
                if (_mpWin != null) _mpWin.SetActive(false);
                _mpChatFocus = false; _mpDragging = false; _mpOpacityDragging = false; _mpResizing = false;
                SyncChatNavBlock(false); MPChat.SuppressGameInput = false;
            }
            _mpWasInGame = mpInGame;

            // (F10 phone-probe keybind removed 2026-06-10 — phone work done;
            //  MPPhoneProbe.Run() can be re-wired if ever needed.)

            // (F3 test row removed for release 2026-06-10 — no debug keys ship.)

            // (F7 test-row removed 2026-06-10 — capture complete: offsets are
            //  zero for all open types; passive RideProbe sampling remains.)

            // BizPhone Chat button: inject once per game scene while MP active;
            // its click requests an MP-window toggle (same as F9).  The icon
            // pulses while chat lines are unread (window closed).
            MPPhoneButton.Tick(IsInGame(), MPServer.IsRunning || MPClient.IsConnected);
            MPPhoneButton.TickPulse(_mpWinVisible);
            MPPhoneButton.TickHubPulse(_hubVisible);
            if (MPPhoneButton.OpenRequested)
            {
                MPPhoneButton.OpenRequested = false;
                if (mpInGame) ToggleMpWindow();
            }
            if (MPPhoneButton.HubOpenRequested)
            {
                MPPhoneButton.HubOpenRequested = false;
                if (mpInGame)
                {
                    // Native full-menu app when ready; standalone fallback.
                    if (_hubVisible) { _hubVisible = false; if (_hubNative) MPHubNativePage.CloseMenu(); }
                    else ShowHubNative();
                }
            }

            // (F9 toggle removed — the BizPhone Chat button + [X] are the toggles.)

            // (Ctrl+S removed — the pause-menu Save now routes to the MP save.)

            // In-game multiplayer window: refresh content + handle its input (drag,
            // opacity slider, resize, chat) whenever it's visible — independent of
            // the F8 panel gate below.
            if (_mpWin != null && _mpWinVisible)
            {
                RefreshMpWindow();
                TickMpWindow();
            }

            // The only per-frame input left for this canvas is the deep settings /
            // "Customize" overlay (the F8 panel is gone; the native menu/lobby/F9
            // window handle everything else, with their own ticks above).
            if (!_settingsOpen) return;

            var mpScreen = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            // Settings value field — type a number directly into the focused field.
            if (_editField >= 0)
            {
                foreach (char c in Input.inputString)
                {
                    if (c == '\b') { if (_editBuffer.Length > 0) _editBuffer = _editBuffer.Substring(0, _editBuffer.Length - 1); }
                    else if (c == '\n' || c == '\r') { CommitSettingsEdit(); break; }
                    else if ((c >= '0' && c <= '9') || c == '.' || c == '-') { if (_editBuffer.Length < 12) _editBuffer += c; }
                }
            }

            // Settings clicks — steppers, value-field focus, close.
            if (Input.GetMouseButtonDown(0))
            {
                CommitSettingsEdit();   // commit any in-progress typing (click-away)
                bool done = false;
                foreach (var h in _settingsHits)
                    if (RectHit(h.rt, mpScreen)) { h.act(); done = true; break; }
                if (!done)
                    for (int fi = 0; fi < _numFields.Count; fi++)
                        if (RectHit(_numFields[fi].Rt, mpScreen)) { BeginSettingsEdit(fi); done = true; break; }
                if (!done && RectHit(_rtSettingsClose, mpScreen)) OnCloseSettings();
            }
        }

        // ── LateUpdate — runs AFTER all MonoBehaviour.Updates AND EventSystem ──
        //
        // Unity processes button-click handlers via the EventSystem, which fires
        // AFTER all Update() calls in the same frame.  Any timeScale change triggered
        // by a game UI button (sleep, pause, rest, speed buttons) happens AFTER
        // Update() has run.  LateUpdate() is therefore the correct place for:
        //
        //   1. TickTimeScaleMonitor — detects button-driven timeScale changes in the
        //      SAME frame they occur, not one frame later.
        //
        //   2. EnforcePauseConsensus — re-applies paused state AFTER the monitor has
        //      updated _pausedPlayers / voted, so it acts on fresh state.
        //
        //   3. EnforceSkipSuppression — clamps skip-timeScale in the same frame the
        //      game sets it, even if set by a non-GameManager MonoBehaviour.

        private void LateUpdate()
        {
            // SOLE OWNER of the input-suppression flag: computed fresh every
            // frame from live contributions — a stale latch once locked a
            // player's keyboard permanently (2026-06-10).
            MPChat.SuppressGameInput = _chatSuppress || _restUiHover || _hubUiHover || _joinPopHover || _crashReportPopupVisible;
            // Player-movement / click-to-move block — owned here (LateUpdate runs every frame, even
            // when the MP window is closed) so the bug-report/crash popup blocks input like the chat does.
            SyncChatNavBlock(_chatSuppress || _crashReportPopupVisible);

            // STICKY MP-game latch: keep native-time suppression engaged across a transient drop+reconnect.
            // Set true whenever we're in the game scene and hosting or live-connected; NEVER cleared here —
            // only on exit-to-menu (scene unload) and the offline-fork dismiss. The live IsConnected briefly
            // reads false on a reconnect, which used to lapse the gate below and let the vanilla skip UI run.
            if (IsInGame() && (MPServer.IsRunning || MPClient.IsConnected)) MPClient.InMpGame = true;

            if (!MPServer.IsRunning && !MPClient.InMpGame) return;

            // Self-check: surface (throttled) when the sticky flag is the ONLY thing holding suppression —
            // we're in an MP game but momentarily disconnected (the reconnect window). Confirms the fix holds.
            if (MPClient.InMpGame && !MPServer.IsRunning && !MPClient.IsConnected
                && Time.unscaledTime >= _inMpGameHoldLogNext)
            {
                _inMpGameHoldLogNext = Time.unscaledTime + 3f;
                Plugin.Logger.LogInfo("[Skip] InMpGame holding native-time suppression while disconnected (reconnect window).");
            }

            // The startup hold freezes the game from the moment our loading overlay
            // clears (TickOverlayFreezeGate) until every player has truly entered.
            // Enforce it here directly so the freeze never depends on another patch
            // (the GameManager.Update backup was itself silently dead for a while).
            // SessionEnded = host lost in-game: same hard freeze, so the MP
            // character can't keep playing/grinding offline.
            if (TimeSync.IsStartupHeld || MPClient.SessionEnded)
            {
                try { Time.timeScale = 0f; } catch { }
                return;
            }

            if (!IsInGame()) return;

            try
            {
                // Backlog #5 — during a taxi ride the game intentionally bumps
                // Time.timeScale to ~8× to fast-forward through the trip.  If
                // we keep pinning timeScale to 1× the ride takes minutes of
                // real time; the user perceives it as a lock-up.  Step out of
                // the way entirely while LocalInTaxi is set.
                if (TrafficSync.LocalInTaxi) return;

                // The mod owns Time.timeScale.  The world runs at 1× real-time
                // always; it stops ONLY for a deliberate manual pause or the
                // startup load hold.  Forcing it here (after the EventSystem and
                // the game's own Update) overrides menu / bench / bed pauses —
                // opening a menu no longer stops time for anyone.
                bool frozen = TimeSync.ManualPaused || TimeSync.IsStartupHeld;
                Time.timeScale = frozen ? 0f : 1f;

                // World-clock guardian: taxi 1× clamp + unaccounted-acceleration
                // net (known skips are suppressed at the TimeMachine patch).
                if (!frozen)
                    TickWorldClock();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[UI] LateUpdate error: {ex.Message}");
            }
        }

        // ── World clock — the MP time architecture (overhauled 2026-06-10) ────
        //
        //   Layer 1 (primary): Patch_TimeMachine_Start_Consensus suppresses
        //     EVERY native skip at the source; MPRestSync turns long ones into
        //     consensus votes and the HOST executes sanctioned skips by writing
        //     the clock directly (clients follow via the regular time sync).
        //   Layer 2 (taxi): the ride keeps its visual fast-forward, but this
        //     clamp holds GAME TIME to the measured 1× rate during it.
        //   Layer 3 (anomaly net): the rate detector below — anything that
        //     still races the clock is an unaccounted mechanism: pin + warn.

        private const float  WC_SAMPLE_WINDOW = 0.15f;  // s — rate measurement window
        private const double WC_SKIP_H_PER_S  = 0.20;   // game-h/real-s above this = a skip
        private const double WC_SETTLE_SLACK  = 0.02;   // game-h — skip considered over below this

        private static double _wcWindowStartHours = -1;
        private static float  _wcWindowStartReal;
        private static bool   _wcRejecting;
        private static double _wcLockHours;
        // Live 1× clock-rate estimate (game-hours per real-second; EMA over
        // healthy windows) and the taxi-clamp anchor state.
        private static double _wcNormalRate = 1.0 / 60.0;
        private static bool   _wcInTaxiClamp;
        private static double _wcTaxiAnchorHours;
        private static float  _wcTaxiAnchorReal;
        private static float  _inMpGameHoldLogNext;   // throttle for the reconnect-window self-check log

        /// <summary>Resets the world-clock skip detector — call on game (re)load.</summary>
        private static void ResetWorldClock()
        {
            _wcWindowStartHours = -1;
            _wcRejecting        = false;
        }

        private static void TickWorldClock()
        {
            var (d, h) = GameStateReader.GetGameTime();
            if (d == 0 && h == 0f) return;            // clock not ready yet
            double nowHours = d * 24.0 + h;
            float  realNow  = Time.unscaledTime;

            // TimeSync just wrote the clock (snap or drift drip) — that jump is
            // AUTHORIZED.  Re-base the sampling window at the new time instead
            // of treating it as a skip; without this the watchdog reverted every
            // sync write and the client's clock flickered night↔day forever
            // (user, 2026-06-12).
            if (TimeSync.ConsumeClockWrite())
            {
                _wcWindowStartHours = -1;             // fresh window from next tick
                _wcRejecting        = false;
                return;
            }

            // Backlog #5 — exempt taxi travel.  The game's TaxiTravel coroutine
            // genuinely needs to fast-forward the clock to complete the ride;
            // pinning the clock would lock the player in the cab.  While the
            // local player is in a taxi we stop measuring rate and stay out of
            // the way; the moment the ride ends we re-arm the detector at the
            // post-ride clock (so the new time isn't seen as a skip retroactively).
            // Consensus skip: the HOST deliberately races the clock and clients
            // follow it — stand down entirely.
            if (MPRestSync.SkipActive)
            {
                _wcWindowStartHours = nowHours;        // keep window glued to live time
                _wcWindowStartReal  = realNow;
                _wcRejecting        = false;
                _wcInTaxiClamp      = false;
                return;
            }

            // Taxi: the ride keeps its local fast-forward (timescale exempt in
            // LateUpdate — the trip completes in seconds) but GAME TIME flows
            // at the measured normal rate, so the ride costs the same time it
            // would for everyone else.  (Overhaul 2026-06-10: replaces the full
            // exemption that let the rider's clock genuinely race ahead.)
            if (TrafficSync.LocalInTaxi)
            {
                if (!_wcInTaxiClamp)
                {
                    _wcInTaxiClamp     = true;
                    _wcTaxiAnchorHours = nowHours;
                    _wcTaxiAnchorReal  = realNow;
                    Plugin.Logger.LogInfo($"[Time] taxi clock clamp ON (1x = {_wcNormalRate * 3600.0:F1} game-min/real-min).");
                }
                double expected = _wcTaxiAnchorHours + (realNow - _wcTaxiAnchorReal) * _wcNormalRate;
                if (nowHours > expected + WC_SETTLE_SLACK) WriteWorldClock(expected);
                return;
            }
            if (_wcInTaxiClamp)
            {
                _wcInTaxiClamp      = false;
                _wcWindowStartHours = -1;              // re-arm the window fresh
                _wcRejecting        = false;
                Plugin.Logger.LogInfo("[Time] taxi clock clamp OFF.");
                return;
            }

            // Currently rejecting a skip — keep pinning the clock until it settles.
            if (_wcRejecting)
            {
                if (nowHours > _wcLockHours + WC_SETTLE_SLACK)
                {
                    WriteWorldClock(_wcLockHours);     // skip still ramping — revert it
                    return;
                }
                _wcRejecting        = false;          // skip stopped — resume normal flow
                _wcWindowStartHours = -1;
                Plugin.Logger.LogInfo("[UI] Time skip ended — world clock resumed.");
                return;
            }

            if (_wcWindowStartHours < 0)
            {
                _wcWindowStartHours = nowHours;
                _wcWindowStartReal  = realNow;
                return;
            }

            float realElapsed = realNow - _wcWindowStartReal;
            if (realElapsed < WC_SAMPLE_WINDOW) return;

            double rate = (nowHours - _wcWindowStartHours) / realElapsed;
            if (rate > WC_SKIP_H_PER_S)
            {
                // ANOMALY: every known skip is suppressed at the TimeMachine
                // choke point, so an acceleration reaching here is a mechanism
                // we haven't accounted for — pin the clock and SHOUT.
                _wcRejecting = true;
                _wcLockHours = _wcWindowStartHours;
                WriteWorldClock(_wcLockHours);
                Plugin.Logger.LogWarning(
                    $"[Time] UNACCOUNTED time acceleration ({rate * 60:F1} game-h/min) — " +
                    $"clock pinned at day {(int)(_wcLockHours / 24.0)} hour {_wcLockHours % 24.0:F2}.  " +
                    "Report this: a skip mechanism bypassed the TimeMachine patch.");
            }
            else
            {
                // Healthy 1× window — refine the live normal-rate estimate
                // (powers the taxi clamp) and slide the window forward.
                if (rate > 0)
                    _wcNormalRate = _wcNormalRate * 0.9 + rate * 0.1;
                _wcWindowStartHours = nowHours;
                _wcWindowStartReal  = realNow;
            }
        }

        /// <summary>Consensus-skip executor entry (MPRestSync, host only):
        /// advance the authoritative clock to the given total game-minutes.</summary>
        public static void WriteWorldClockMinutes(double totalMinutes) => WriteWorldClock(totalMinutes / 60.0);

        // ── Rest-vote banner — small top-center overlay, NON-interactive (no
        // raycast target, no input capture: ignorable while playing). ─────────
        private TextMeshProUGUI? _restBanner;
        private string _restBannerSig = "\0";

        private void TickRestBanner()
        {
            try
            {
                int n = MPRestSync.Votes.Count;
                string sig = n == 0 ? ""
                    : $"{n}|{MPRestSync.RequiredVotes}|{MPRestSync.SkipActive}|{string.Join(",", MPRestSync.Votes.ConvertAll(v => v.PlayerId + v.Activity + (int)v.GoalMinutes))}";
                if (sig == _restBannerSig) return;
                _restBannerSig = sig;

                if (n == 0)
                {
                    if (_restBanner != null) _restBanner.gameObject.SetActive(false);
                    return;
                }
                if (_restBanner == null)
                {
                    if (_canvasGO == null) return;
                    var go = MakeGO("BAMP_RestBanner", _canvasGO.transform);
                    var rt = go.GetComponent<RectTransform>();
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(0f, -150f); rt.sizeDelta = new Vector2(860f, 54f);
                    _restBanner = go.AddComponent<TextMeshProUGUI>();
                    _restBanner.fontSize = 17; _restBanner.alignment = TextAlignmentOptions.Top;
                    _restBanner.raycastTarget = false;          // never blocks clicks
                    _restBanner.enableWordWrapping = true;
                    ApplyFont(_restBanner);
                }
                _restBanner.gameObject.SetActive(true);

                // For other players: who is requesting the skip, doing what,
                // until when — plus the count.
                var sb = new System.Text.StringBuilder();
                if (MPRestSync.SkipActive)
                    sb.Append("<color=#8CE08C><b>Skipping time…</b></color>  ");
                else
                    sb.Append($"<color=#FFD27A><b>Time-skip votes {n}/{MPRestSync.RequiredVotes}</b></color>  ");
                for (int i = 0; i < MPRestSync.Votes.Count; i++)
                {
                    var v = MPRestSync.Votes[i];
                    if (i > 0) sb.Append("   ");
                    sb.Append($"<color=#CFE3FF>{MPNames.Resolve(v.PlayerId)}</color>: {NiceActivity(v.Activity)} until {MPRestSync.Fmt(v.GoalMinutes)}");
                }
                _restBanner.text = sb.ToString();
            }
            catch { }
        }

        private static string NiceActivity(string a) => a switch
        {
            "Sleep" => "sleeping",
            "Rest"  => "resting on bench",
            "Work"  => "working a shift",
            "Workout" => "working out",
            "Study" => "studying",
            "Entertain" => "relaxing",
            "Hygiene" => "showering",
            "Swimming" => "swimming",
            _ => string.IsNullOrEmpty(a) ? "resting" : a.ToLowerInvariant(),
        };

        // ── Rest dock (v7) — chat-program styling, centered "Rest until".
        // Left: who-voted checklist (+match).  Center: day (amber when not
        // today) over a large centered time, nudge row, preset row, toggle.
        // Header: activity title + X (the native Stop/Cancel). ───────────────
        private GameObject? _dock;          private RectTransform? _dockRT;
        private TextMeshProUGUI? _dockTitle, _dockDay, _dockTime, _dockPlayers, _dockSkipLbl;
        private RectTransform? _dockXRT;
        private RectTransform? _tgtM1h, _tgtM15, _tgtP15, _tgtP1h, _skipToggleRT;
        private bool _skipCommitNudge;   // a wake target was adjusted without committing → pulse "Request time skip"
        private readonly RectTransform?[] _presetRT = new RectTransform?[4];
        private static readonly int[] PresetHours = { 7, 12, 18, 22 };
        private readonly RectTransform?[] _matchRT = new RectTransform?[3];
        private readonly double[] _matchGoal = new double[3];
        private Image? _skipToggleImg;
        private double _skipTarget;
        private bool _restUiHover;

        private void TickRestUI()
        {
            try
            {
                // SeatedForUi (not Seated): a half-cancelled bench approach
                // wedged the dock open with no X (user, 2026-06-11).
                // inActivity = in ANY activity state — drives the escape hatch (always available).
                // show = the dock's VISIBILITY, DELAYED until the avatar has physically sat down
                // (AvatarInActivity), so clicking a bench walks you over first instead of popping the
                // dock instantly. The delay never PREVENTS the dock — AvatarInActivity reads the game's
                // live state, so once seated it reliably shows.
                bool inActivity = MPRestSync.SeatedForUi;
                bool show = inActivity && MPRestSync.AvatarInActivity();
                _restUiHover = false;   // recomputed below; EVERY early return must leave it false

                // ESCAPE HATCH — independent of any UI existing: movement keys
                // always stand you up (a half-built dock once trapped a player).
                if (inActivity && !_mpChatFocus &&
                    (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A) ||
                     Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D)))
                {
                    Plugin.Logger.LogInfo("[RestDock] movement key — standing up.");
                    MPRestSync.StandUp();
                    return;
                }

                if (_dock == null)
                {
                    if (!show || _canvasGO == null || _dockBuildFailed) return;
                    BuildDock();
                    if (_dock == null) return;
                }
                if (_dock!.activeSelf != show) _dock.SetActive(show);
                _restUiHover = false;
                if (!show) return;

                bool voting = MPRestSync.LocalVoteActive;

                if (_dockTitle != null)
                    _dockTitle.text = NiceActivity(MPRestSync.ActivityName);

                // The X is UNCONDITIONAL: it used to mirror the game's
                // per-state cancel-button list, which blinks during activity
                // transitions and vanished entirely in the wedge (user,
                // 2026-06-11).  StandUp() already handles the no-cancel-button
                // case, so the dependency never bought anything.
                if (_dockXRT != null && !_dockXRT.gameObject.activeSelf)
                    _dockXRT.gameObject.SetActive(true);

                // Destination time: default is a SHORT wait (now + 1h) — the
                // old next-morning default silently meant "until tomorrow".
                double now = MPRestSync.NowMinutes();
                if (_skipTarget <= 0) _skipTarget = Math.Ceiling((now + 60) / 15.0) * 15.0;
                double minT = now + 5;
                if (_skipTarget < minT) _skipTarget = Math.Ceiling(minT / 5.0) * 5.0;
                double shown = voting ? MPRestSync.LocalGoal : _skipTarget;
                var (dStr, tStr) = MPRestSync.FmtParts(shown);
                if (_dockDay != null)
                {
                    int dayDiff = (int)(shown / 1440.0) - (int)(now / 1440.0);
                    _dockDay.text = dayDiff switch
                    {
                        0 => $"today · {dStr}",
                        1 => $"<b>tomorrow · {dStr}</b>",
                        _ => $"<b>in {dayDiff} days · {dStr}</b>",
                    };
                }
                if (_dockTime != null) _dockTime.text = tStr;

                if (_dockSkipLbl != null)
                    _dockSkipLbl.text = voting ? "Skip requested — click to cancel" : "Request time skip";
                if (voting) _skipCommitNudge = false;   // committed — the nudge did its job
                if (_skipToggleImg != null)
                {
                    // Goonie report (2026-07-09): a player adjusted a wake time and waited — nothing
                    // happens until "Request time skip" is clicked, and nothing SAID so. Once a target
                    // is touched without a vote, pulse the commit button gently until it's clicked.
                    var baseCol = voting ? new Color(0.24f, 0.60f, 0.31f, 1f) : new Color(0.19f, 0.42f, 0.67f, 1f);
                    if (!voting && _skipCommitNudge)
                    {
                        // Full-swing pulse (user: the soft one was too easy to miss): breathe the button
                        // all the way to a bright blue-white, ~0.8 Hz — unmissable, layout untouched.
                        float w = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5.0f);
                        baseCol = Color.Lerp(baseCol, new Color(0.48f, 0.80f, 1.0f, 1f), w);
                    }
                    _skipToggleImg.color = baseCol;
                }

                // Left column: compact who-voted checklist + match buttons.
                if (_dockPlayers != null)
                {
                    var sb = new System.Text.StringBuilder();
                    if (MPRestSync.SkipActive) sb.Append("<color=#8CE08C><b>Skipping time…</b></color>\n");
                    int row = MPRestSync.SkipActive ? 1 : 0, matchIdx = 0;
                    for (int i = 0; i < 3; i++) { _matchGoal[i] = 0; _matchRT[i]?.gameObject.SetActive(false); }
                    foreach (var pl in MPRestSync.AllPlayers())
                    {
                        bool me = pl == MPConfig.PlayerId;
                        if (MPRestSync.HasVote(pl, out double g))
                        {
                            var (gd, gt) = MPRestSync.FmtParts(g);
                            sb.Append($"<color=#8CE08C>{pl}{(me ? " (you)" : "")}</color>\n<color=#CFE3FF>   {gd} {gt}</color>\n");
                            if (!me && matchIdx < 3 && _matchRT[matchIdx] != null)
                            {
                                _matchGoal[matchIdx] = g;
                                var mrt = _matchRT[matchIdx]!;
                                mrt.anchoredPosition = new Vector2(144f, -52f - row * 16f);
                                mrt.gameObject.SetActive(true);
                                matchIdx++;
                            }
                            row += 2;
                        }
                        else
                        {
                            sb.Append($"<color=#8A93A6>{pl}{(me ? " (you)" : "")} — not voted</color>\n");
                            row++;
                        }
                    }
                    _dockPlayers.text = sb.ToString();
                }

                // Hover + drag + clicks.  (_restUiHover feeds the LateUpdate-
                // owned suppression flag — never write the flag directly.)
                var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
                _restUiHover = RectHit(_dockRT, mp) || _dockDragging;

                if (Input.GetMouseButtonUp(0)) _dockDragging = false;
                if (_dockDragging && _dockRT != null)
                {
                    Vector2 d = mp - _dockDragLast;
                    _dockDragLast = mp;
                    _dockRT.anchoredPosition += d;
                    _dockSavedPos = _dockRT.anchoredPosition;   // remembered for next open
                    return;
                }

                if (!Input.GetMouseButtonDown(0) || !_restUiHover) return;

                if (_dockXRT != null && RectHit(_dockXRT, mp))
                { MPRestSync.StandUp(); return; }   // StandUp prefers the game's cancel button, falls back otherwise

                if (RectHit(_dockHdrRT, mp))
                { _dockDragging = true; _dockDragLast = mp; return; }

                for (int i = 0; i < 3; i++)
                    if (_matchRT[i] != null && _matchRT[i]!.gameObject.activeSelf && _matchGoal[i] > 0 && RectHit(_matchRT[i], mp))
                    {
                        _skipTarget = _matchGoal[i];
                        MPRestSync.SetSkipRequest(true, _skipTarget);
                        return;
                    }

                // Every dock interaction logs ONE line: reporters rarely answer follow-ups, so the
                // click trail in the log is the interview (Goonie report, 2026-07-09 — a session of
                // pre-vote preset clicks was completely invisible).
                double step = 0;
                if      (RectHit(_tgtM1h, mp)) step = -60;
                else if (RectHit(_tgtM15, mp)) step = -15;
                else if (RectHit(_tgtP15, mp)) step = 15;
                else if (RectHit(_tgtP1h, mp)) step = 60;
                if (step != 0)
                {
                    _skipTarget = Math.Max(minT, (voting ? MPRestSync.LocalGoal : _skipTarget) + step);
                    if (voting) MPRestSync.SetSkipRequest(true, _skipTarget);
                    else _skipCommitNudge = true;   // target touched, vote not started → pulse the commit button
                    var (sd2, st2) = MPRestSync.FmtParts(_skipTarget);
                    Plugin.Logger.LogInfo($"[Rest] dock click: step {step:+0;-0}m → target {sd2} {st2} (voting={voting}).");
                    return;
                }
                for (int i = 0; i < _presetRT.Length; i++)
                    if (RectHit(_presetRT[i], mp))
                    {
                        _skipTarget = MPRestSync.NextOccurrence(PresetHours[i]);
                        if (voting) MPRestSync.SetSkipRequest(true, _skipTarget);
                        else _skipCommitNudge = true;   // ditto — "6:00" alone does not start the wait
                        var (pd2, pt2) = MPRestSync.FmtParts(_skipTarget);
                        Plugin.Logger.LogInfo($"[Rest] dock click: preset {PresetHours[i]}:00 → target {pd2} {pt2} (voting={voting}).");
                        return;
                    }
                if (RectHit(_skipToggleRT, mp))
                {
                    Plugin.Logger.LogInfo($"[Rest] dock click: skip toggle (voting={voting}, seated={MPRestSync.Seated}).");
                    if (voting) MPRestSync.SetSkipRequest(false);
                    else        MPRestSync.SetSkipRequest(true, _skipTarget);
                }
            }
            catch { }
        }

        private bool _dockBuildFailed;
        private Sprite? _dockSprite;
        // Chat's per-frame contribution to the input-suppression flag (the
        // flag itself is owned by LateUpdate).
        private bool _chatSuppress;
        // Drag + position memory (per session: re-opens where you left it).
        private RectTransform? _dockHdrRT;
        private bool _dockDragging;
        private Vector2 _dockDragLast;
        private static Vector2? _dockSavedPos;

        private void BuildDock()
        {
            try
            {
                _dock = MakeGO("BAMP_RestDock", _canvasGO!.transform);
                _dockRT = _dock.GetComponent<RectTransform>();
                _dockRT.anchorMin = _dockRT.anchorMax = _dockRT.pivot = new Vector2(0.5f, 0f);
                _dockRT.anchoredPosition = new Vector2(0f, 120f);
                _dockRT.sizeDelta = new Vector2(700f, 212f);
                // Same rounded-corner source the chat window uses: the captured
                // menu sprite is DEAD in-game — fall back to our immortal one.
                _dockSprite = IsAlive(_panelSprite) ? _panelSprite : EnsureRoundedSprite();
                Plugin.Logger.LogInfo($"[RestDock] sprite source: {(_dockSprite == null ? "NONE (square!)" : ReferenceEquals(_dockSprite, _panelSprite) ? "captured-native" : "owned-rounded")}");
                var bg = _dock.AddComponent<Image>();
                bg.color = new Color(0.07f, 0.08f, 0.11f, 0.96f);
                if (_dockSprite != null) { try { bg.sprite = _dockSprite; bg.type = Image.Type.Sliced; } catch { } }

                // Restore the last position the player dragged it to.
                if (_dockSavedPos.HasValue) _dockRT.anchoredPosition = _dockSavedPos.Value;

                // Header band - chat title-bar look; title + red X; DRAG HANDLE.
                var hdr = MakeGO("Hdr", _dock.transform);
                _dockHdrRT = hdr.GetComponent<RectTransform>();
                Stretch(_dockHdrRT, 0f, 0f, 0f, 28f, top: true);
                var hImg = hdr.AddComponent<Image>();
                hImg.color = new Color(0.18f, 0.23f, 0.36f, 1f);
                if (_dockSprite != null) { try { hImg.sprite = _dockSprite; hImg.type = Image.Type.Sliced; } catch { } }
                _dockTitle = MakeLabel(hdr.transform, "", 14, C_WHITE, 14f, 0f, 300f, 28f, TextAlignmentOptions.Left);
                ApplyFont(_dockTitle);
                var (xRT, xLbl) = MakeDockButton("X", default, 30f, new Color(0.56f, 0.27f, 0.20f, 1f), 20f);
                xRT.SetParent(hdr.transform, false);
                xRT.anchorMin = xRT.anchorMax = xRT.pivot = new Vector2(1f, 0.5f);
                xRT.anchoredPosition = new Vector2(-7f, 0f);
                xLbl.fontSize = 12;
                _dockXRT = xRT;
                xRT.gameObject.SetActive(false);

                // Left column: players header + checklist + match chips.
                var ph = MakeLabel(_dock.transform, "players", 11, C_LBLGREY, 14f, -34f, 80f, 14f, TextAlignmentOptions.Left);
                ApplyFont(ph);
                _dockPlayers = MakeLabel(_dock.transform, "", 12, C_WHITE, 14f, -52f, 188f, 152f, TextAlignmentOptions.TopLeft);
                ApplyFont(_dockPlayers);
                for (int i = 0; i < 3; i++)
                {
                    var (mrt, mlbl) = MakeDockButton("match", default, 56f, new Color(0.24f, 0.19f, 0.34f, 1f), 17f);
                    mrt.anchorMin = mrt.anchorMax = mrt.pivot = new Vector2(0f, 1f);
                    mrt.anchoredPosition = new Vector2(144f, -52f - i * 17f);
                    mlbl.fontSize = 10;
                    mlbl.color = new Color(0.81f, 0.75f, 0.94f, 1f);
                    _matchRT[i] = mrt;
                    mrt.gameObject.SetActive(false);
                }

                // Divider between players and the time section.
                var div = MakeGO("Div", _dock.transform);
                var drt = div.GetComponent<RectTransform>();
                drt.anchorMin = drt.anchorMax = drt.pivot = new Vector2(0f, 1f);
                drt.anchoredPosition = new Vector2(212f, -36f);
                drt.sizeDelta = new Vector2(1.5f, 166f);
                div.AddComponent<Image>().color = new Color(0.27f, 0.30f, 0.37f, 0.8f);

                // Center stack (x 224..686): the clock is the centerpiece.
                const float CX = 224f, CW = 462f;
                var until = MakeLabel(_dock.transform, "rest until", 11, C_LBLGREY, CX, -34f, CW, 14f, TextAlignmentOptions.Center);
                ApplyFont(until);
                _dockDay = MakeLabel(_dock.transform, "", 13, new Color(1f, 0.82f, 0.48f, 1f), CX, -50f, CW, 18f, TextAlignmentOptions.Center);
                ApplyFont(_dockDay);
                _dockTime = MakeLabel(_dock.transform, "", 42, C_WHITE, CX, -64f, CW, 58f, TextAlignmentOptions.Center);
                ApplyFont(_dockTime);
                _dockTime.enableWordWrapping = false;
                _dockTime.overflowMode = TextOverflowModes.Overflow;   // a 42pt line MUST render (48px box truncated it to nothing)

                // Nudges (grey) and presets (blue) - distinct rounded buttons.
                float bw = 60f, gap = 8f;
                float rowW = bw * 4 + gap * 3;
                float x0 = CX + (CW - rowW) / 2f;
                var nudgeCol = new Color(0.24f, 0.27f, 0.37f, 1f);    // brighter than v8 — vibrancy per mockup
                (_tgtM1h, _) = MakeDockButton("-1h",  new Vector2(x0,                 68f), bw, nudgeCol, 25f);
                (_tgtM15, _) = MakeDockButton("-15m", new Vector2(x0 + (bw + gap),     68f), bw, nudgeCol, 25f);
                (_tgtP15, _) = MakeDockButton("+15m", new Vector2(x0 + (bw + gap) * 2, 68f), bw, nudgeCol, 25f);
                (_tgtP1h, _) = MakeDockButton("+1h",  new Vector2(x0 + (bw + gap) * 3, 68f), bw, nudgeCol, 25f);
                for (int i = 0; i < PresetHours.Length; i++)
                {
                    var (prt, plbl) = MakeDockButton($"{PresetHours[i]:D2}:00", new Vector2(x0 + (bw + gap) * i, 38f), bw, new Color(0.17f, 0.30f, 0.49f, 1f), 25f);
                    plbl.color = new Color(0.76f, 0.87f, 0.98f, 1f);
                    _presetRT[i] = prt;
                }

                // Toggle - wide, centered.
                var (tgRT, tgLbl) = MakeDockButton("", new Vector2(CX + (CW - 340f) / 2f, 6f), 340f, new Color(0.19f, 0.42f, 0.67f, 1f), 27f);
                _skipToggleRT  = tgRT;
                _dockSkipLbl   = tgLbl;
                _skipToggleImg = tgRT.GetComponent<Image>();
                _dockSkipLbl.fontSize = 13;

                _dock.SetActive(false);
                Plugin.Logger.LogInfo("[RestDock] dock built OK.");
            }
            catch (Exception ex)
            {
                // A half-built dock trapped a player once (no X, no time) -
                // never again: log LOUDLY and disable so the hatch (movement
                // keys) is the only thing that matters.
                _dockBuildFailed = true;
                Plugin.Logger.LogError($"[RestDock] BuildDock FAILED: {ex}");
                try { if (_dock != null) { UnityEngine.Object.Destroy(_dock); _dock = null; } } catch { }
            }
        }

        private (RectTransform rt, TextMeshProUGUI lbl) MakeDockButton(string label, Vector2 pos, float w, Color bg, float h = 30f)
        {
            var go = MakeGO("BAMP_Dock_" + label + pos.x, _dock!.transform);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(w, h);
            var img = go.AddComponent<Image>();
            img.color = bg;
            if (_dockSprite != null) { try { img.sprite = _dockSprite; img.type = Image.Type.Sliced; } catch { } }
            var lbl = MakeLabel(go.transform, label, 12, C_WHITE, 0f, 0f, w, h, TextAlignmentOptions.Center);
            ApplyFont(lbl);
            var lrt = lbl.rectTransform; lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            return (rt, lbl);
        }

        // ── Business Hub — full-page MP app (segregated from native pages by
        //    design; native style used as TEMPLATE: grey page, blue stripe,
        //    category tabs, rounded boxes).  Tabs: Transfers | Loans. ─────────
        private bool _hubVisible;
        private bool _hubUiHover;
        private GameObject? _hub;            private RectTransform? _hubRT;
        private TextMeshProUGUI? _hubAmountLbl, _hubTermsLbl;
        private RectTransform? _hubXRT, _hubSendRT, _hubLoanRT;
        private readonly RectTransform?[] _hubChipRT = new RectTransform?[4];
        private readonly Image?[] _hubChipImg = new Image?[4];
        private readonly TextMeshProUGUI?[] _hubChipLbl = new TextMeshProUGUI?[4];
        private readonly string[] _hubChipWho = new string[4];
        private int _hubFilter;                               // 0=all 1=gifts 2=loans
        private readonly RectTransform?[] _hubFilterRT = new RectTransform?[3];
        private readonly Image?[] _hubFilterImg = new Image?[3];
        // Row-scroll lists (incoming + outgoing offers) + active-loans scroll.
        // Buttons live INSIDE scrolled rows; clicks resolve via this registry,
        // gated on each row's viewport so masked-off rows can't be clicked.
        private readonly List<(RectTransform rt, RectTransform vp, string id, byte act)> _hubRowBtns = new();   // act: 0=accept 1=decline 2=cancel 3=repay-confirm 4=repay-partial 5=repay-full
        private RectTransform? _hubInVp, _hubInContent, _hubOutVp, _hubOutContent;
        private RectTransform? _hubLoansContent, _hubLoansVp;
        private string _hubRepayArm = ""; private float _hubRepayArmAt; private float _hubRepayArmAmt;   // repay confirm: armed loan id, arm time, amount (0 = full)
        // Tabs.
        private int _hubTab;                                  // 0=Transfers 1=Loans 2=Permissions
        private GameObject? _hubPageTransfers, _hubPageLoans, _hubPagePermissions;
        private readonly RectTransform?[] _hubTabRT = new RectTransform?[3];
        private readonly Image?[] _hubTabImg = new Image?[3];
        private readonly TextMeshProUGUI?[] _hubTabLbl = new TextMeshProUGUI?[3];
        // Permissions tab: scrollable per-player VEHICLE-key list. Its own button registry so the
        // offers rebuild (which Clears _hubRowBtns) can't wipe it.
        private RectTransform? _hubPermVp, _hubPermContent;
        private readonly List<(RectTransform rt, RectTransform vp, string id, byte act, GrantKind kind)> _hubPermBtns = new();
        private string _hubPermSig = "";
        // Merger confirm modal (user 2026-07-07: proposing/accepting is a BIG decision — explain it).
        private GameObject? _mergerConfirmGO;
        private TextMeshProUGUI? _mergerConfirmLbl;
        private RectTransform? _rtMergerOk, _rtMergerCancel;
        private string _mergerConfirmMode = "", _mergerConfirmPid = "";
        private string _hubTarget = "";
        private double _hubAmount = 10000;
        private int _hubSeenVersion = -1;
        private string _hubRosterSig = "";
        // Typed inputs (click value → type digits; Enter/click-away commits).
        // Defaults MATCH THE BANK's fixed deal: 20% total over 244 days.
        private bool _hubAmountFocus;
        private bool _hubFreshFocus;       // first keystroke replaces the value
        private string _hubAmountStr = "10000";
        private bool _hubRateFocus;
        private string _hubRateStr = "20";
        private bool _hubTermFocus;
        private string _hubTermStr = "244";
        private RectTransform? _hubAmountRT, _hubRateRT, _hubTermRT;
        private TextMeshProUGUI? _hubRateLbl, _hubTermLbl;
        // Partial-repayment amount (Active-loans header) — mirrors the amount field above.
        private bool _hubPartialFocus;
        private string _hubPartialStr = "1000";
        private double _hubPartialAmount = 1000;
        private RectTransform? _hubPartialRT; private TextMeshProUGUI? _hubPartialLbl, _hubPartialHint;
        // Native-template palette (tuneable in one place).
        private static readonly Color HubPageGrey   = new Color(0.145f, 0.155f, 0.175f, 0.99f);
        private static readonly Color HubStripeBlue = new Color(0.20f, 0.47f, 0.78f, 1f);
        private static readonly Color HubBoxGrey    = new Color(0.19f, 0.205f, 0.23f, 1f);
        private static readonly Color HubTabOff     = new Color(0.17f, 0.18f, 0.20f, 1f);
        private static readonly Color HubTabOn      = new Color(0.20f, 0.42f, 0.68f, 1f);
        // Host-dependent theme: standalone window (dark) vs native full-menu
        // page (white boxes, dark ink — the native pages' vocabulary).
        private bool _hubNative;
        private Camera? _hubCam;                    // FullMenu canvas camera (null = overlay)
        private Color _inkHi, _inkLo, _boxCol;
        private string _colFrom = "", _colGood = "", _colOwe = "", _colMuted = "";

        /// <summary>Hub hit-test honoring the host canvas's camera.</summary>
        private bool HubHit(RectTransform? rt, Vector2 screenPos)
        {
            if (rt == null) return false;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenPos, _hubCam, out var local);
            return rt.rect.Contains(local);
        }

        // ── F3 test rig REMOVED for public release (2026-06-12): it spawned a
        // real owned flatbed pre-loaded with sellable stock and was exempt from
        // the ghost strip, so it persisted into saves — an economy/clutter vector
        // (sweep finding M1).  The BAMP_TESTRIG strip exemption in
        // GameStatePatcher is retained as a backstop for any pre-existing save.

        // ── Spawn de-stack v2 (stage-4): every player loads on the SAME spawn
        // point and the capsules shove each other.  ONE navmesh-validated
        // sidestep at the WorldReady EVENT — after the game''s placement is
        // FINAL, so nothing fights it.  The old offset+re-assert pin machinery
        // (rubberband on first movement; fought save restores) is DELETED.
        // The spawn is a SIDEWALK strip between road and buildings (user):
        // candidates ring outward in small steps and only WALKABLE positions
        // (NavMesh.SamplePosition) qualify — building interiors and bad
        // directions fail validation and are skipped.
        private bool _spawnSidestepDone;

        /// <summary>Fresh-character join: warp to the DESIGNATED new-player
        /// start (gi.LastPlayerPosition's default 215,0,0 — what a normal new
        /// game uses).  The native placement no-ops on these joins because the
        /// game's continuous position writer stomps the default during our long
        /// fenced load (see MPClient.PendingFreshSpawn).</summary>
        private void ApplyFreshSpawnWarp()
        {
            try
            {
                if (!MPClient.PendingFreshSpawn) return;
                MPClient.PendingFreshSpawn = false;
                var ch = Helpers.PlayerHelper.PlayerController?.Character;
                if (ch == null) return;
                var start = new Vector3(215f, 0f, 0f);   // GameInstance.LastPlayerPosition default = designated start
                if (UnityEngine.AI.NavMesh.SamplePosition(start, out var hit, 25f, -1))
                {
                    try { ch.navmeshAgent.Warp(hit.position); }
                    catch { ch.transform.position = hit.position; }
                    try { var gi = SaveGameManager.Current; if (gi != null) gi.LastPlayerPosition = hit.position; } catch { }
                    Plugin.Logger.LogInfo($"[Spawn] fresh join → designated start ({hit.position.x:F1}, {hit.position.y:F1}, {hit.position.z:F1}).");
                }
                else Plugin.Logger.LogWarning("[Spawn] fresh join: designated start not on navmesh — left at native placement.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Spawn] fresh-join warp: {ex.Message}"); }
        }

        private void ApplySpawnSidestep()
        {
            try
            {
                if (_spawnSidestepDone) return;
                _spawnSidestepDone = true;
                // Fresh games only: loaded sessions restore real positions.
                if (!string.IsNullOrEmpty(MPSaveCoordinator.ActiveSessionName)) return;
                var players = MPRestSync.AllPlayers();
                if (players.Count < 2) return;
                var sorted = new List<string>(players);
                sorted.Sort(StringComparer.Ordinal);
                int idx = sorted.IndexOf(MPConfig.PlayerId);
                if (idx <= 0) { Plugin.Logger.LogInfo($"[Spawn] sidestep: idx={idx} keeps the spot."); return; }

                var ch = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                if (ch == null) return;
                var origin = ch.position;

                // Ordered candidates: 8 compass directions × growing rings of
                // 1.4m; the (idx-1)-th WALKABLE one is this player''s spot.
                int want = idx - 1, seen = 0;
                for (int ring = 1; ring <= 3; ring++)
                {
                    for (int d = 0; d < 8; d++)
                    {
                        float ang = d * Mathf.PI / 4f;
                        var target = origin + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (1.4f * ring);
                        UnityEngine.AI.NavMeshHit hit;
                        if (!UnityEngine.AI.NavMesh.SamplePosition(target, out hit, 0.6f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                        if (Mathf.Abs(hit.position.y - origin.y) > 1f) continue;   // no roofs/basements
                        if (seen++ < want) continue;
                        ch.position = hit.position;
                        try { Physics.SyncTransforms(); } catch { }
                        Plugin.Logger.LogInfo($"[Spawn] sidestep: idx={idx} → walkable offset ({hit.position.x - origin.x:F1},{hit.position.z - origin.z:F1}) ring={ring}.");
                        return;
                    }
                }
                Plugin.Logger.LogWarning("[Spawn] sidestep: no walkable candidate found — staying put (stacked spawn).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Spawn] sidestep: {ex.Message}"); }
        }

        // ── Mid-game join approval (HOST): requests park at the door; this
        //    RIGHT-SIDE panel lists them — each approved/rejected separately,
        //    pulsing briefly on arrival (obvious, but out of the action). ────
        private GameObject? _joinPop;
        private RectTransform? _joinPopRT;
        private bool _joinPopHover;
        private string _joinPopSig = "";
        private float _joinPopPulseUntil;
        private readonly List<(RectTransform rt, int peerId, bool accept)> _joinPopBtns = new();

        private void TickJoinPopup()
        {
            try
            {
                _joinPopHover = false;
                if (!MPServer.IsRunning) { if (_joinPop != null && _joinPop.activeSelf) _joinPop.SetActive(false); return; }
                var pending = MPServer.PendingJoinList;
                if (pending.Count == 0)
                {
                    _joinPopSig = "";
                    if (_joinPop != null && _joinPop.activeSelf) _joinPop.SetActive(false);
                    return;
                }
                if (_canvasGO == null) return;
                var sprite = IsAlive(_panelSprite) ? _panelSprite : EnsureRoundedSprite();
                if (_joinPop == null)
                {
                    _joinPop = MakeGO("BAMP_JoinPop", _canvasGO.transform);
                    _joinPopRT = _joinPop.GetComponent<RectTransform>();
                    _joinPopRT.anchorMin = _joinPopRT.anchorMax = new Vector2(1f, 0.72f);
                    _joinPopRT.pivot = new Vector2(1f, 1f);
                    _joinPopRT.anchoredPosition = new Vector2(-14f, 0f);
                    _joinPopRT.sizeDelta = new Vector2(380f, 80f);
                    var bg = _joinPop.AddComponent<Image>();
                    bg.color = new Color(0.10f, 0.11f, 0.15f, 0.97f);
                    if (sprite != null) { try { bg.sprite = sprite; bg.type = Image.Type.Sliced; } catch { } }
                }
                // Rebuild rows when the queue changes.
                string sig = "";
                foreach (var p in pending) sig += p.peerId + "|" + p.playerId + ";";
                if (sig != _joinPopSig)
                {
                    bool newcomer = sig.Length > _joinPopSig.Length;
                    _joinPopSig = sig;
                    if (newcomer) _joinPopPulseUntil = Time.unscaledTime + 2.5f;
                    _joinPopBtns.Clear();
                    for (int i = _joinPop!.transform.childCount - 1; i >= 0; i--)
                        UnityEngine.Object.Destroy(_joinPop.transform.GetChild(i).gameObject);
                    var hdrLbl = MakeLabel(_joinPop.transform, "<b>JOIN REQUESTS</b>", 13, C_LBLGREY, 14f, -8f, 240f, 20f, TextAlignmentOptions.Left);
                    ApplyFont(hdrLbl);
                    int shown = Math.Min(pending.Count, 4);
                    for (int i = 0; i < shown; i++)
                    {
                        float y = -32f - i * 40f;
                        var nm = MakeLabel(_joinPop.transform, $"<b>{pending[i].playerId}</b>", 13, C_WHITE, 14f, y - 6f, 170f, 26f, TextAlignmentOptions.Left);
                        ApplyFont(nm);
                        var (aRT, aLbl) = MakeHubButton("accept", Vector2.zero, 78f, new Color(0.24f, 0.50f, 0.33f, 1f), 28f, sprite, _joinPop.transform);
                        SetTopLeft(aRT, 196f, y); aLbl.fontSize = 12;
                        var (dRT, dLbl) = MakeHubButton("reject", Vector2.zero, 78f, new Color(0.45f, 0.24f, 0.22f, 1f), 28f, sprite, _joinPop.transform);
                        SetTopLeft(dRT, 282f, y); dLbl.fontSize = 12;
                        _joinPopBtns.Add((aRT, pending[i].peerId, true));
                        _joinPopBtns.Add((dRT, pending[i].peerId, false));
                    }
                    if (pending.Count > shown)
                    {
                        var more = MakeLabel(_joinPop.transform, $"<color=#9AA3B2>… and {pending.Count - shown} more waiting</color>", 12, C_LBLGREY, 14f, -32f - shown * 40f - 4f, 320f, 18f, TextAlignmentOptions.Left);
                        ApplyFont(more);
                    }
                    _joinPopRT!.sizeDelta = new Vector2(380f, 40f + shown * 40f + (pending.Count > shown ? 24f : 0f));
                }
                if (!_joinPop!.activeSelf) _joinPop.SetActive(true);
                // Attention = COLOR glow, not scale: a scale pulse moved the
                // buttons under the cursor (user feedback, 2026-06-11).
                var bgImg = _joinPop.GetComponent<Image>();
                if (bgImg != null)
                {
                    float k = Time.unscaledTime < _joinPopPulseUntil
                        ? (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 7f)) * 0.8f : 0f;
                    bgImg.color = Color.Lerp(new Color(0.10f, 0.11f, 0.15f, 0.97f),
                                             new Color(0.17f, 0.32f, 0.55f, 0.99f), k);
                }
                _joinPop.transform.localScale = Vector3.one;

                var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
                _joinPopHover = RectHit(_joinPopRT, mp);
                if (!Input.GetMouseButtonDown(0) || !_joinPopHover) return;
                foreach (var (rt, peerId, accept) in _joinPopBtns)
                    if (RectHit(rt, mp))
                    {
                        if (accept) MPServer.AcceptPendingJoin(peerId);
                        else MPServer.RejectPendingJoin(peerId);
                        _joinPopSig = "";   // force row rebuild
                        return;
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[JoinPop] {ex.Message}"); }
        }
        // (Legacy-pane lobby kick removed 2026-06-11 — the real kick [X] lives in the native lobby window roster rows.)

        // ── Honorary-degree training dialog (user spec 2026-06-12) ────────────
        // School door → confirm GUI with course + FULL cost; Accept charges the
        // whole remaining course (native tuition transaction, tax deductible)
        // and credits completion through the same fields + event the game's own
        // StudyActivity.Finish uses.  Cancel walks away free.
        private static object? _trainPending;            // intercepted StudyActivity
        public  static void RequestTrainingDialog(object studyActivity) => _trainPending = studyActivity;
        private GameObject?    _trainPop;
        private RectTransform? _trainPopRT, _trainAcceptRT, _trainCancelRT;
        private object?        _trainShownFor;

        private void TickTrainingPopup()
        {
            try
            {
                if (_trainPending == null)
                {
                    if (_trainPop != null && _trainPop.activeSelf) _trainPop.SetActive(false);
                    _trainShownFor = null;
                    return;
                }
                var act = _trainPending;
                var settings = MPReflect.Get(act.GetType(), act, "_diplomaSettings") as DiplomaSettings;
                var diploma  = MPReflect.Get(act.GetType(), act, "_diploma") as Diploma;
                if (settings == null || diploma == null) { _trainPending = null; return; }
                // Prerequisite gate: an advanced course can't be taken until its
                // required diploma is held.  Native StudyActivity.StartStudying (and
                // BizManSettings) enforce exactly this; our honorary-degree dialog
                // bypasses the native start, so mirror the check here for the local
                // player or a client could buy a course out of order.
                if (settings.requiredDiploma != DiplomaName.Undefined
                    && !EducationHelper.HasCompletedDiploma(settings.requiredDiploma))
                {
                    try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Error,
                        $"Requires the {settings.requiredDiploma} course first."); } catch { }
                    _trainPending = null; return;
                }
                int remaining = settings.RequiredHours * 60 - diploma.minutesStudied;
                if (remaining <= 0)
                {
                    try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Info, "Course already completed."); } catch { }
                    _trainPending = null; return;
                }
                int cost = Mathf.CeilToInt(remaining / 60f * settings.PricePerHour);
                string course = diploma.name.ToString();

                if (!ReferenceEquals(_trainShownFor, act))
                {
                    _trainShownFor = act;
                    var sprite = IsAlive(_panelSprite) ? _panelSprite : EnsureRoundedSprite();
                    if (_trainPop == null)
                    {
                        _trainPop = MakeGO("BAMP_TrainPop", _canvasGO.transform);
                        _trainPopRT = _trainPop.GetComponent<RectTransform>();
                        _trainPopRT.anchorMin = _trainPopRT.anchorMax = new Vector2(0.5f, 0.55f);
                        _trainPopRT.pivot = new Vector2(0.5f, 0.5f);
                        _trainPopRT.anchoredPosition = Vector2.zero;
                        _trainPopRT.sizeDelta = new Vector2(430f, 150f);
                        var bg = _trainPop.AddComponent<Image>();
                        bg.color = new Color(0.10f, 0.11f, 0.15f, 0.97f);
                        if (sprite != null) { try { bg.sprite = sprite; bg.type = Image.Type.Sliced; } catch { } }
                    }
                    for (int i = _trainPop.transform.childCount - 1; i >= 0; i--)
                        UnityEngine.Object.Destroy(_trainPop.transform.GetChild(i).gameObject);
                    var hdr = MakeLabel(_trainPop.transform, $"<b>TRAINING — {course}</b>", 14, C_WHITE, 16f, -10f, 398f, 22f, TextAlignmentOptions.Left);
                    ApplyFont(hdr);
                    var body = MakeLabel(_trainPop.transform,
                        $"Full course: {remaining / 60}h {remaining % 60:D2}m studied instantly\nTotal cost: <b>${cost:N0}</b> (honorary degree)",
                        13, C_LBLGREY, 16f, -36f, 398f, 44f, TextAlignmentOptions.Left);
                    ApplyFont(body);
                    var (aRT, aLbl) = MakeHubButton("accept", Vector2.zero, 120f, new Color(0.24f, 0.50f, 0.33f, 1f), 32f, sprite, _trainPop.transform);
                    SetTopLeft(aRT, 85f, -102f); aLbl.fontSize = 13;
                    var (cRT, cLbl) = MakeHubButton("cancel", Vector2.zero, 120f, new Color(0.45f, 0.24f, 0.22f, 1f), 32f, sprite, _trainPop.transform);
                    SetTopLeft(cRT, 225f, -102f); cLbl.fontSize = 13;
                    _trainAcceptRT = aRT; _trainCancelRT = cRT;
                }
                if (!_trainPop!.activeSelf) _trainPop.SetActive(true);
                _trainPop.transform.localScale = Vector3.one;

                if (!Input.GetMouseButtonDown(0)) return;
                var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
                if (_trainCancelRT != null && RectHit(_trainCancelRT, mp)) { _trainPending = null; return; }
                if (_trainAcceptRT == null || !RectHit(_trainAcceptRT, mp)) return;

                // Mirror StudyActivity.Finish()'s bookkeeping for the FULL course.
                var data = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "diplomaName", course },
                    { "hours",   (remaining / 60).ToString() },
                    { "minutes", (remaining % 60).ToString() },
                    { "taxDeductibleName", "businesstype_school" },
                };
                bool paid = false;
                try
                {
                    paid = GameManager.ChangeMoneySafe(-cost,
                        new TransactionInfo("ba:transaction_tuitionfee", data, isTaxDeductible: true),
                        null, null, force: false);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Train] charge: {ex.Message}"); }
                if (paid)
                {
                    diploma.minutesStudied = settings.RequiredHours * 60;
                    diploma.completed = true;
                    try { GameEvent.Invoke("ba:gameevent_diplomagranted"); } catch { }
                    try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Success, $"Honorary degree purchased — {course}"); } catch { }
                    Plugin.Logger.LogInfo($"[Train] honorary degree '{course}' purchased: ${cost} for {remaining} course-minute(s).");
                }
                else Plugin.Logger.LogInfo($"[Train] purchase declined (insufficient funds for ${cost}).");
                _trainPending = null;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Train] {ex.Message}"); _trainPending = null; }
        }

        /// <summary>Open the Business page inside the native full menu (and the
        /// menu itself if closed).  Falls back to the standalone window when
        /// the injection isn't ready.</summary>
        private void ShowHubNative()
        {
            try
            {
                if (!MPHubNativePage.Ready || MPHubNativePage.ContentRoot == null)
                { _hubVisible = !_hubVisible; return; }   // fallback: standalone toggle
                // Rebuild if the hub was last built for the other host.
                if (_hub != null && !_hubNative) { UnityEngine.Object.Destroy(_hub); _hub = null; }
                _hubCam = MPHubNativePage.UiCamera;
                if (_hub == null) BuildHubWindow(MPHubNativePage.ContentRoot);
                MPHubNativePage.OpenMenuToBusiness();
                _hubVisible = _hub != null;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Hub] ShowHubNative: {ex.Message}"); }
        }

        private void TickHubWindow()
        {
            try
            {
                // Native top-row "Business" button clicked (menu already open).
                if (MPHubNativePage.OpenRequested)
                {
                    MPHubNativePage.OpenRequested = false;
                    ShowHubNative();
                }
                // Native-hosted: the page's active state is the master (a
                // native app click hides us via the ShowApp patch).
                if (_hubNative && _hub != null)
                    _hubVisible = MPHubNativePage.PageActive;

                if (_hub == null)
                {
                    if (!_hubVisible || _canvasGO == null) return;
                    BuildHubWindow();
                    if (_hub == null) return;
                }
                if (_hub!.activeSelf != _hubVisible) _hub.SetActive(_hubVisible);
                _hubUiHover = false;
                if (!_hubVisible) return;

                // Player chips (everyone but me).
                var players = MPRestSync.AllPlayers();
                string sig = string.Join(",", players) + "|" + _hubTarget;
                if (sig != _hubRosterSig)
                {
                    _hubRosterSig = sig;
                    int slot = 0;
                    bool targetOk = false;
                    foreach (var pl in players)
                    {
                        if (pl == MPConfig.PlayerId || slot >= 4) continue;
                        _hubChipWho[slot] = pl;
                        if (_hubChipLbl[slot] != null) _hubChipLbl[slot]!.text = pl;
                        _hubChipRT[slot]?.gameObject.SetActive(true);
                        if (_hubTarget == pl) targetOk = true;
                        slot++;
                    }
                    for (int i = slot; i < 4; i++) { _hubChipWho[i] = ""; _hubChipRT[i]?.gameObject.SetActive(false); }
                    if (!targetOk) _hubTarget = slot > 0 ? _hubChipWho[0] : "";
                    for (int i = 0; i < 4; i++)
                        if (_hubChipImg[i] != null)
                            _hubChipImg[i]!.color = _hubChipWho[i] == _hubTarget && _hubTarget != ""
                                ? MpPurple : new Color(0.18f, 0.20f, 0.27f, 1f);
                }

                // Typed inputs: amount (digits) and rate (digits + dot).
                HandleHubTyping();
                bool caretOn = Mathf.FloorToInt(Time.unscaledTime * 2f) % 2 == 0;
                if (_hubAmountLbl != null)
                    _hubAmountLbl.text = _hubAmountFocus
                        ? $"<b>${_hubAmountStr}</b>{(caretOn ? "|" : "")}"
                        : $"<b>${_hubAmount:N0}</b>";
                if (_hubRateLbl != null)
                    _hubRateLbl.text = _hubRateFocus
                        ? $"<b>{_hubRateStr}</b>{(caretOn ? "|" : "")} %"
                        : $"<b>{HubRate():F0}</b> %";
                if (_hubTermLbl != null)
                    _hubTermLbl.text = _hubTermFocus
                        ? $"<b>{_hubTermStr}</b>{(caretOn ? "|" : "")} days"
                        : $"<b>{HubTerm()}</b> days";
                if (_hubPartialLbl != null)
                    _hubPartialLbl.text = _hubPartialFocus
                        ? $"<b>${_hubPartialStr}</b>{(caretOn ? "|" : "")}"
                        : $"<b>${_hubPartialAmount:N0}</b>";
                if (_hubTermsLbl != null)
                {
                    // Bank convention: rate = TOTAL premium over the term.
                    // EXACT dailies (no ceiling) — rounding up inflated the
                    // effective rate: a 20% offer displayed as 22% (2026-06-10).
                    double pct = HubRate();
                    int term = HubTerm();
                    double di = Math.Max(0, _hubAmount * pct / 100.0 / term);
                    double dp = Math.Max(1, _hubAmount / term);
                    string termsCol = _hubNative ? "#8795A0" : "#9AA3B2";   // native muted (dump-confirmed)
                    _hubTermsLbl.text = $"<color={termsCol}>loan: ${di:N0}/day interest + ${dp:N0}/day payment over {term} days · total interest ${_hubAmount * pct / 100.0:N0} ({pct:F0}%) · bank: 20% / 244d</color>";
                }

                // Tab pages + active-tab styling.
                if (_hubPageTransfers != null && _hubPageTransfers.activeSelf != (_hubTab == 0)) _hubPageTransfers.SetActive(_hubTab == 0);
                if (_hubPageLoans != null && _hubPageLoans.activeSelf != (_hubTab == 1)) _hubPageLoans.SetActive(_hubTab == 1);
                if (_hubPagePermissions != null && _hubPagePermissions.activeSelf != (_hubTab == 2)) _hubPagePermissions.SetActive(_hubTab == 2);
                for (int i = 0; i < 3; i++)
                {
                    if (_hubTabImg[i] != null) _hubTabImg[i]!.color = _hubTab == i ? HubTabOn : HubTabOff;
                    if (_hubTabLbl[i] != null) _hubTabLbl[i]!.color = _hubTab == i ? C_WHITE : C_LBLGREY;
                }
                if (_hubTab == 2) RefreshPermissionsList();

                // Offer lists (rebuild on Version change).
                if (MPHub.Version != _hubSeenVersion)
                {
                    _hubSeenVersion = MPHub.Version;

                    // INCOMING — shared, category-agnostic, filtered, SCROLLED
                    // rows with inline accept/decline.
                    _hubRowBtns.Clear();
                    var incoming = new List<LoanOfferPayload>();
                    foreach (var o in MPHub.IncomingOffers)
                        if (_hubFilter == 0 || (_hubFilter == 1) == (o.Kind == "gift")) incoming.Add(o);
                    ClearScrollContent(_hubInContent);
                    const float InRowH = 46f;
                    if (_hubInContent != null && _hubInVp != null)
                    {
                        for (int i = 0; i < incoming.Count; i++)
                        {
                            var o = incoming[i];
                            string txt = o.Kind == "gift"
                                ? $"<color={_colGood}>{o.From}</color>: <b>${o.Principal:N0}</b> gift"
                                : $"<color={_colFrom}>{o.From}</color>: <b>${o.Principal:N0}</b> loan · <b>{MPHub.OfferTotalPct(o):F0}%</b> / {MPHub.OfferTermDays(o)}d (${o.DailyInterest:N0}+${o.DailyPayment:N0}/day)";
                            AddHubRow(_hubInContent, _hubInVp, i, InRowH, txt,
                                ("accept",  new Color(0.24f, 0.50f, 0.33f, 1f), o.Id, (byte)0),
                                ("decline", new Color(0.45f, 0.24f, 0.22f, 1f), o.Id, (byte)1));
                        }
                        if (incoming.Count == 0)
                        {
                            var none = MakeLabel(_hubInContent.transform, $"<color={_colMuted}>no pending offers</color>", 12, _inkLo, 6f, -4f, 400f, 20f, TextAlignmentOptions.TopLeft);
                            ApplyFont(none);
                        }
                        _hubInContent.sizeDelta = new Vector2(0f, Mathf.Max(160f, incoming.Count * InRowH + 4f));
                    }

                    // OUTGOING — all kinds together, scrolled, X cancels.
                    ClearScrollContent(_hubOutContent);
                    const float OutRowH = 42f;
                    if (_hubOutContent != null && _hubOutVp != null)
                    {
                        var outs = MPHub.OutgoingOffers;
                        for (int i = 0; i < outs.Count; i++)
                        {
                            var o = outs[i];
                            string txt = o.Kind == "gift"
                                ? $"<b>${o.Principal:N0}</b> gift → <color={_colGood}>{o.To}</color>"
                                : $"<b>${o.Principal:N0}</b> loan → <color={_colFrom}>{o.To}</color> · {MPHub.OfferTotalPct(o):F0}% / {MPHub.OfferTermDays(o)}d";
                            AddHubRow(_hubOutContent, _hubOutVp, i, OutRowH, txt,
                                ("X", new Color(0.45f, 0.24f, 0.22f, 1f), o.Id, (byte)2));
                        }
                        if (outs.Count == 0)
                        {
                            var none = MakeLabel(_hubOutContent.transform, $"<color={_colMuted}>none pending</color>", 12, _inkLo, 6f, -4f, 300f, 20f, TextAlignmentOptions.TopLeft);
                            ApplyFont(none);
                        }
                        _hubOutContent.sizeDelta = new Vector2(0f, Mathf.Max(160f, outs.Count * OutRowH + 4f));
                    }

                    // ACTIVE LOANS — one row per loan; loans you OWE get a Pay off
                    // button (two-click confirm), unless the lender is offline (the
                    // host can't credit them). Loans owed TO you are display-only.
                    if (_hubRepayArm != "" && Time.unscaledTime - _hubRepayArmAt > 6f) _hubRepayArm = "";
                    ClearScrollContent(_hubLoansContent);
                    const float LoanRowH = 44f;
                    if (_hubLoansContent != null && _hubLoansVp != null)
                    {
                        var mine = new List<LoanEntry>();
                        foreach (var ln in MPHub.Loans)
                            if (ln.Borrower == MPConfig.PlayerId || ln.Lender == MPConfig.PlayerId) mine.Add(ln);
                        if (MPHub.Loans.Count > 0 && mine.Count == 0)
                            Plugin.Logger.LogWarning($"[Hub] {MPHub.Loans.Count} active loan(s) but none match my id '{MPConfig.PlayerId}'");
                        var present = MPRestSync.AllPlayers();
                        bool anyRepayable = false;
                        // While the partial field is focused, show the LIVE typed value on the buttons; else the committed one.
                        float pShow = (_hubPartialFocus && long.TryParse(_hubPartialStr, out var pParse) && pParse > 0) ? pParse : (float)_hubPartialAmount;
                        for (int i = 0; i < mine.Count; i++)
                        {
                            var ln = mine[i];
                            int dleft = (int)Math.Ceiling(ln.Remaining / Math.Max(1f, ln.DailyPayment));
                            if (ln.Borrower == MPConfig.PlayerId)
                            {
                                string txt = $"you owe <color={_colOwe}>{ln.Lender}</color>: <b>${ln.Remaining:N0}</b> left · ${ln.DailyInterest:N0}+${ln.DailyPayment:N0}/day · ~{dleft}d";
                                if (!present.Contains(ln.Lender))
                                    AddHubRow(_hubLoansContent, _hubLoansVp, i, LoanRowH, txt + $"  <color={_colMuted}>· lender offline</color>");
                                else if (_hubRepayArm == ln.Id)   // armed → single confirm button showing the amount
                                {
                                    anyRepayable = true;
                                    float amt = _hubRepayArmAmt <= 0f ? ln.Remaining : Math.Min(_hubRepayArmAmt, ln.Remaining);
                                    AddHubRow(_hubLoansContent, _hubLoansVp, i, LoanRowH, txt,
                                        ($"confirm ${amt:N0}", new Color(0.62f, 0.40f, 0.16f, 1f), ln.Id, (byte)3));
                                }
                                else
                                {
                                    anyRepayable = true;
                                    float pAmt = Math.Min(pShow, ln.Remaining);
                                    // full on the RIGHT (first tuple = rightmost in AddHubRow), partial on the LEFT
                                    AddHubRow(_hubLoansContent, _hubLoansVp, i, LoanRowH, txt,
                                        ($"full ${ln.Remaining:N0}", new Color(0.24f, 0.50f, 0.33f, 1f), ln.Id, (byte)5),
                                        ($"partial ${pAmt:N0}", new Color(0.28f, 0.36f, 0.48f, 1f), ln.Id, (byte)4));
                                }
                            }
                            else
                            {
                                string txt = $"<color={_colGood}>{ln.Borrower}</color> owes you: <b>${ln.Remaining:N0}</b> left · ${ln.DailyInterest:N0}+${ln.DailyPayment:N0}/day · ~{dleft}d";
                                AddHubRow(_hubLoansContent, _hubLoansVp, i, LoanRowH, txt);
                            }
                        }
                        // The "partial $" field only matters when you have a loan you can actually repay.
                        if (_hubPartialRT != null) _hubPartialRT.gameObject.SetActive(anyRepayable);
                        if (_hubPartialHint != null) _hubPartialHint.gameObject.SetActive(anyRepayable);
                        if (mine.Count == 0)
                        {
                            var none = MakeLabel(_hubLoansContent.transform, $"<color={_colMuted}>no active loans</color>", 12, _inkLo, 6f, -4f, 400f, 20f, TextAlignmentOptions.TopLeft);
                            ApplyFont(none);
                        }
                        _hubLoansContent.sizeDelta = new Vector2(0f, Mathf.Max(118f, mine.Count * LoanRowH + 4f));
                    }
                }
                // Hover + clicks.  In the NATIVE page the menu itself already
                // blocks gameplay input — our suppression flag there only
                // fought the menu's own shortcuts (ESC took ~6 presses and
                // movement stayed locked after close, 2026-06-10).
                var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
                _hubUiHover = !_hubNative && (HubHit(_hubRT, mp) || _hubAmountFocus || _hubRateFocus || _hubTermFocus);
                if (!Input.GetMouseButtonDown(0)) return;

                // Merger confirm modal swallows every click while open: Confirm executes, anything
                // else (Cancel, scrim, elsewhere) closes without acting.
                if (_mergerConfirmGO != null && _mergerConfirmGO.activeSelf)
                {
                    if (HubHit(_rtMergerOk, mp))
                    {
                        if (_mergerConfirmMode == "propose" && !string.IsNullOrEmpty(_mergerConfirmPid))
                        {
                            if (MPServer.IsRunning) MPServer.HostMergerAction("propose", _mergerConfirmPid, MPConfig.PlayerId);
                            else                    MPClient.SendMergerAction("propose", _mergerConfirmPid);
                            MergerSync.OutgoingToPid = _mergerConfirmPid;
                        }
                        else if (_mergerConfirmMode == "accept")
                        {
                            if (MPServer.IsRunning) MPServer.HostMergerAction("accept", "", MPConfig.PlayerId);
                            else                    MPClient.SendMergerAction("accept");
                            MergerSync.IncomingFromPid = "";
                        }
                        _hubPermSig = "";
                    }
                    _mergerConfirmGO.SetActive(false);
                    return;
                }

                if (!HubHit(_hubRT, mp)) { CommitHubInputs(); return; }   // click-away commits typing

                if (HubHit(_hubAmountRT, mp)) { _hubAmountFocus = true; _hubRateFocus = false; _hubTermFocus = false; _hubPartialFocus = false; _hubFreshFocus = true; _hubAmountStr = ((long)_hubAmount).ToString(); return; }
                if (_hubTab == 1 && HubHit(_hubRateRT, mp)) { _hubRateFocus = true; _hubAmountFocus = false; _hubTermFocus = false; _hubPartialFocus = false; _hubFreshFocus = true; _hubRateStr = HubRate().ToString("F0"); return; }
                if (_hubTab == 1 && HubHit(_hubTermRT, mp)) { _hubTermFocus = true; _hubAmountFocus = false; _hubRateFocus = false; _hubPartialFocus = false; _hubFreshFocus = true; _hubTermStr = HubTerm().ToString(); return; }
                if (_hubTab == 1 && HubHit(_hubPartialRT, mp)) { _hubPartialFocus = true; _hubAmountFocus = false; _hubRateFocus = false; _hubTermFocus = false; _hubFreshFocus = true; _hubPartialStr = ((long)_hubPartialAmount).ToString(); return; }
                CommitHubInputs();

                if (HubHit(_hubXRT, mp))
                {
                    _hubVisible = false;
                    if (_hubNative) MPHubNativePage.CloseMenu();
                    return;
                }
                for (int i = 0; i < 3; i++)
                    if (HubHit(_hubTabRT[i], mp)) { _hubTab = i; _hubSeenVersion = -1; _hubPermSig = ""; return; }
                for (int i = 0; i < 3; i++)
                    if (HubHit(_hubFilterRT[i], mp)) { _hubFilter = i; _hubSeenVersion = -1; RefreshHubFilterChips(); return; }
                for (int i = 0; i < 4; i++)
                    if (_hubChipRT[i] != null && _hubChipRT[i]!.gameObject.activeSelf && HubHit(_hubChipRT[i], mp))
                    { _hubTarget = _hubChipWho[i]; _hubRosterSig = ""; return; }

                if (_hubTab == 0 && HubHit(_hubSendRT, mp)) { MPHub.OfferGift(_hubTarget, (float)_hubAmount); return; }
                if (_hubTab == 1 && HubHit(_hubLoanRT, mp))
                {
                    // EXACT dailies (no ceiling) — the typed rate IS the
                    // effective rate (20% stays 20%, not 22%).
                    double pct = HubRate();
                    int term = HubTerm();
                    double di = Math.Max(0, _hubAmount * pct / 100.0 / term);
                    double dp = Math.Max(1, _hubAmount / term);
                    MPHub.OfferLoan(_hubTarget, (float)_hubAmount, (float)di, (float)dp);
                    return;
                }
                // Permissions tab: per-player key toggles (separate registry from the offers rows).
                foreach (var (rt, vp, id, act, kind) in _hubPermBtns)
                {
                    if (id == "" || rt == null || !rt.gameObject.activeInHierarchy) continue;
                    if (!HubHit(vp, mp) || !HubHit(rt, mp)) continue;
                    if (act == 6 && id.StartsWith("pid:"))            // toggle an ONLINE player's key (per kind)
                    {
                        string pid = id.Substring(4);
                        bool now = !GrantSync.IsGranted(kind, MPConfig.PlayerId, pid);
                        if (MPServer.IsRunning) MPServer.HostSetGrant(kind, pid, now);
                        else                    MPClient.SendPermissionGrant(kind, pid, now);
                        _hubPermSig = "";   // force the list to rebuild with the new state
                    }
                    else if (act == 7 && id.StartsWith("stable:"))    // toggle an OFFLINE grantee's key (per kind)
                    {
                        string handle = id.Substring(7);
                        bool cur = false;
                        foreach (var g in GrantSync.MyGrantees()) if (g.Handle == handle) { cur = g.Kinds.Contains(kind); break; }
                        if (MPServer.IsRunning) MPServer.HostSetGrantOffline(kind, handle, !cur);
                        else                    MPClient.SendPermissionSetOffline(kind, handle, !cur);
                        _hubPermSig = "";
                    }
                    else if (act == 8 && id.StartsWith("pid:"))       // merger: propose → confirm popup first
                    {
                        ShowMergerConfirm("propose", id.Substring(4));
                    }
                    else if (act == 9)                                // accept the incoming proposal → confirm popup
                    {
                        ShowMergerConfirm("accept", MergerSync.IncomingFromPid);
                    }
                    else if (act == 10)                               // decline it (no confirm — declining is safe)
                    {
                        if (MPServer.IsRunning) MPServer.HostMergerAction("decline", "", MPConfig.PlayerId);
                        else                    MPClient.SendMergerAction("decline");
                        MergerSync.IncomingFromPid = "";
                        _hubPermSig = "";
                    }
                    else if (act == 11)                               // leave the merger (a last pair dissolves it)
                    {
                        if (MPServer.IsRunning) MPServer.HostMergerAction("leave", "", MPConfig.PlayerId);
                        else                    MPClient.SendMergerAction("leave");
                        _hubPermSig = "";
                    }
                    else if (act == 12)                               // withdraw my pending proposal (no confirm)
                    {
                        if (MPServer.IsRunning) MPServer.HostMergerAction("unpropose", "", MPConfig.PlayerId);
                        else                    MPClient.SendMergerAction("unpropose");
                        MergerSync.OutgoingToPid = "";
                        _hubPermSig = "";
                    }
                    return;
                }

                // Scrolled-row buttons: viewport gate keeps masked rows inert.
                foreach (var (rt, vp, id, act) in _hubRowBtns)
                {
                    if (id == "" || rt == null || !rt.gameObject.activeInHierarchy) continue;
                    if (!HubHit(vp, mp) || !HubHit(rt, mp)) continue;
                    if (act == 0) MPHub.AnswerOffer(id, true);
                    else if (act == 1) MPHub.AnswerOffer(id, false);
                    else if (act == 2) MPHub.CancelOffer(id);
                    else if (act == 3)   // confirm (second click) → execute the armed repayment
                    {
                        if (_hubRepayArm == id && Time.unscaledTime - _hubRepayArmAt < 6f)
                            MPHub.RequestRepay(id, _hubRepayArmAmt);
                        _hubRepayArm = ""; _hubSeenVersion = -1;
                    }
                    else if (act == 4)   // partial → arm with the typed partial amount
                    { _hubRepayArm = id; _hubRepayArmAmt = (float)_hubPartialAmount; _hubRepayArmAt = Time.unscaledTime; _hubSeenVersion = -1; }
                    else if (act == 5)   // full → arm full payoff
                    { _hubRepayArm = id; _hubRepayArmAmt = 0f; _hubRepayArmAt = Time.unscaledTime; _hubSeenVersion = -1; }
                    return;
                }
            }
            catch { }
        }

        private void RefreshHubFilterChips()
        {
            for (int i = 0; i < 3; i++)
                if (_hubFilterImg[i] != null)
                    _hubFilterImg[i]!.color = _hubFilter == i ? HubTabOn : (_hubNative ? new Color(1f, 1f, 1f, 0.12f) : new Color(0.18f, 0.20f, 0.27f, 1f));
        }

        private void BuildHubWindow(Transform? host = null)
        {
            try
            {
                // Theme by host: native page = white boxes + dark ink (the
                // native vocabulary); standalone window = dark theme.
                _hubNative = host != null;
                // TRUE-UP 2026-06-11 (.modding/03-systems/native-ui-style.md):
                // the dump showed native pages are NOT white-box + dark ink —
                // lists sit on TRANSLUCENT DARK NAVY (#262B40@0.43) with white
                // text; muted = #8795A0, idle labels #CACDCE.  The old white
                // theme was a guess; these values are renderer-confirmed.
                _inkHi   = _hubNative ? C_WHITE : C_WHITE;
                _inkLo   = _hubNative ? new Color(0.792f, 0.804f, 0.808f, 1f) : C_LBLGREY;   // #CACDCE
                _boxCol  = _hubNative ? new Color(0.149f, 0.169f, 0.251f, 0.43f) : HubBoxGrey; // #262B40@0.43
                _colFrom = "#FFD27A";
                _colGood = "#8CE08C";
                _colOwe  = "#CFE3FF";
                _colMuted= _hubNative ? "#8795A0" : "#6B7384";

                _hub = MakeGO("BAMP_Hub", host ?? _canvasGO!.transform);
                _hubRT = _hub.GetComponent<RectTransform>();
                _hubRT.anchorMin = _hubRT.anchorMax = _hubRT.pivot = new Vector2(0.5f, 0.5f);
                _hubRT.anchoredPosition = new Vector2(0f, 0f);
                _hubRT.sizeDelta = new Vector2(980f, _hubNative ? 620f : 668f);   // standalone carries its header
                if (_hubNative) _hubRT.localScale = new Vector3(1.35f, 1.35f, 1f);   // fills the 1920x875 root
                // ALWAYS the owned pure-white rounded sprite: tint colors are
                // designed against white; the captured native sprite has a grey
                // cast that turned the white boxes dirty (2026-06-11).
                var sprite = EnsureRoundedSprite();
                var bg = _hub.AddComponent<Image>();
                bg.color = _hubNative ? new Color(0f, 0f, 0f, 0f) : HubPageGrey;   // native shell IS the background
                if (sprite != null) { try { bg.sprite = sprite; bg.type = Image.Type.Sliced; } catch { } }

                // Header band only for the STANDALONE window — in the native
                // shell the top bar (title, money, close) already exists, so
                // the band was a dead strip eating screen space.
                float T = _hubNative ? 48f : 0f;   // vertical reclaim shift
                _hubXRT = null;
                if (!_hubNative)
                {
                    var hdr = MakeGO("Hdr", _hub.transform);
                    var hrt = hdr.GetComponent<RectTransform>();
                    Stretch(hrt, 0f, 0f, 0f, 56f, top: true);
                    var hImg = hdr.AddComponent<Image>();
                    hImg.color = new Color(0.115f, 0.125f, 0.145f, 1f);
                    if (sprite != null) { try { hImg.sprite = sprite; hImg.type = Image.Type.Sliced; } catch { } }
                    var title = MakeLabel(hdr.transform, "<b>BUSINESS HUB</b>", 19, C_WHITE, 24f, 0f, 320f, 56f, TextAlignmentOptions.Left);
                    ApplyFont(title);
                    var (xRT, xLbl) = MakeHubButton("X", new Vector2(0f, 0f), 32f, new Color(0.56f, 0.27f, 0.20f, 1f), 32f, sprite);
                    xRT.SetParent(hdr.transform, false);
                    xRT.anchorMin = xRT.anchorMax = xRT.pivot = new Vector2(1f, 0.5f);
                    xRT.anchoredPosition = new Vector2(-14f, 0f);
                    xLbl.fontSize = 14;
                    _hubXRT = xRT;
                }

                // The blue stripe (native page signature; the native shell
                // brings its own splitter, so only the standalone draws one).
                if (!_hubNative)
                {
                    var stripe = MakeGO("Stripe", _hub.transform);
                    var srt = stripe.GetComponent<RectTransform>();
                    srt.anchorMin = new Vector2(0f, 1f); srt.anchorMax = new Vector2(1f, 1f); srt.pivot = new Vector2(0.5f, 1f);
                    srt.anchoredPosition = new Vector2(0f, -56f); srt.sizeDelta = new Vector2(0f, 5f);
                    stripe.AddComponent<Image>().color = HubStripeBlue;
                }

                // Shared band: target chips + amount (used by every tab).
                // ── Shared band: target + amount on ONE row. ─────────────────
                var toLbl = MakeLabel(_hub.transform, "to:", 13, C_LBLGREY, 24f, T - 74f, 34f, 26f, TextAlignmentOptions.Left);
                ApplyFont(toLbl);
                for (int i = 0; i < 4; i++)
                {
                    var (crt, clbl) = MakeHubButton("", new Vector2(0f, 0f), 134f, new Color(0.18f, 0.20f, 0.27f, 1f), 30f, sprite);
                    SetTopLeft(crt, 60f + i * 144f, T - 72f);
                    _hubChipRT[i] = crt; _hubChipLbl[i] = clbl;
                    _hubChipImg[i] = crt.GetComponent<Image>();
                    clbl.fontSize = 13;
                    crt.gameObject.SetActive(false);
                }
                var amLbl = MakeLabel(_hub.transform, "amount:", 13, C_LBLGREY, 648f, T - 74f, 70f, 26f, TextAlignmentOptions.Left);
                ApplyFont(amLbl);
                (_hubAmountRT, _hubAmountLbl) = MakeHubInput(_hub.transform, 724f, T - 68f, 232f, 34f, 20, sprite);

                // Category tab row.
                string[] tabNames = { "Transfers", "Loans", "Permissions" };
                for (int i = 0; i < 3; i++)
                {
                    var (trt, tlbl) = MakeHubButton(tabNames[i], new Vector2(0f, 0f), 170f, HubTabOff, 38f, sprite);
                    SetTopLeft(trt, 24f + i * 178f, T - 118f);
                    tlbl.fontSize = 14;
                    _hubTabRT[i] = trt;
                    _hubTabImg[i] = trt.GetComponent<Image>();
                    _hubTabLbl[i] = tlbl;
                }

                // ── Page: Transfers (the gift action — offers live below). ───
                _hubPageTransfers = MakeGO("PageTransfers", _hub.transform);
                var ptrt = _hubPageTransfers.GetComponent<RectTransform>();
                ptrt.anchorMin = new Vector2(0f, 0f); ptrt.anchorMax = new Vector2(1f, 1f);
                ptrt.offsetMin = new Vector2(0f, 248f); ptrt.offsetMax = new Vector2(0f, T - 164f);
                var pt = _hubPageTransfers.transform;

                MakeHubBox(pt, 16f, -2f, 948f, 56f, sprite);
                (_hubSendRT, _) = MakeHubButton("Offer gift", new Vector2(0f, 0f), 190f, new Color(0.19f, 0.42f, 0.67f, 1f), 36f, sprite);
                _hubSendRT.SetParent(pt, false); SetTopLeft(_hubSendRT, 28f, -12f);
                var giftCap = MakeLabel(pt, "gifts require the receiver's accept — no silent handouts", 12, _inkLo, 236f, -20f, 600f, 22f, TextAlignmentOptions.Left);
                ApplyFont(giftCap);

                // ── Page: Loans (terms + the BIG scrolled active ledger). ────
                _hubPageLoans = MakeGO("PageLoans", _hub.transform);
                var plrt = _hubPageLoans.GetComponent<RectTransform>();
                plrt.anchorMin = new Vector2(0f, 0f); plrt.anchorMax = new Vector2(1f, 1f);
                plrt.offsetMin = new Vector2(0f, 248f); plrt.offsetMax = new Vector2(0f, T - 164f);
                var pl = _hubPageLoans.transform;

                MakeHubBox(pl, 16f, -2f, 948f, 78f, sprite);
                var rtLbl = MakeLabel(pl, "interest:", 13, _inkLo, 28f, -16f, 70f, 24f, TextAlignmentOptions.Left);
                ApplyFont(rtLbl);
                (_hubRateRT, _hubRateLbl) = MakeHubInput(pl, 102f, -10f, 110f, 32f, 15, sprite);
                var tmLbl = MakeLabel(pl, "term:", 13, _inkLo, 232f, -16f, 46f, 24f, TextAlignmentOptions.Left);
                ApplyFont(tmLbl);
                (_hubTermRT, _hubTermLbl) = MakeHubInput(pl, 282f, -10f, 140f, 32f, 15, sprite);
                (_hubLoanRT, _) = MakeHubButton("Offer loan", new Vector2(0f, 0f), 190f, new Color(0.24f, 0.50f, 0.33f, 1f), 32f, sprite);
                _hubLoanRT.SetParent(pl, false); SetTopLeft(_hubLoanRT, 740f, -10f);
                _hubTermsLbl = MakeLabel(pl, "", 12, _inkLo, 28f, -52f, 920f, 22f, TextAlignmentOptions.Left);
                ApplyFont(_hubTermsLbl);

                MakeHubBox(pl, 16f, -90f, 948f, 162f, sprite);
                var loansHdr = MakeLabel(pl, "<b>ACTIVE LOANS</b>", 12, _inkLo, 28f, -98f, 220f, 18f, TextAlignmentOptions.Left);
                ApplyFont(loansHdr);
                _hubPartialHint = MakeLabel(pl, "partial $", 12, _inkLo, 686f, -100f, 70f, 20f, TextAlignmentOptions.Right);
                ApplyFont(_hubPartialHint);
                (_hubPartialRT, _hubPartialLbl) = MakeHubInput(pl, 762f, -96f, 180f, 26f, 14, sprite);
                var (lvp, lct) = MakeHubScroll(pl, 28f, -122f, 904f, 118f, sprite);
                _hubLoansVp = lvp;
                _hubLoansContent = lct;

                // ── Page: Permissions (per-player VEHICLE keys; scrolls for a full lobby). ───
                _hubPagePermissions = MakeGO("PagePermissions", _hub.transform);
                var pprt = _hubPagePermissions.GetComponent<RectTransform>();
                pprt.anchorMin = new Vector2(0f, 0f); pprt.anchorMax = new Vector2(1f, 1f);
                pprt.offsetMin = new Vector2(0f, 248f); pprt.offsetMax = new Vector2(0f, T - 164f);
                var pp = _hubPagePermissions.transform;
                MakeHubBox(pp, 16f, -2f, 948f, 200f, sprite);
                var permHdr = MakeLabel(pp, "<b>VEHICLE KEYS</b>  <color=" + (_hubNative ? "#8795A0" : "#9AA3B2") + ">— who can use your vehicles as their own (housing + business coming)</color>",
                                        12, _inkLo, 28f, -10f, 900f, 18f, TextAlignmentOptions.Left);
                ApplyFont(permHdr);
                var (pvp, pct) = MakeHubScroll(pp, 28f, -36f, 908f, 156f, sprite);
                _hubPermVp = pvp;
                _hubPermContent = pct;

                // ── Shared bottom: INCOMING (left) + OUTGOING (right). ───────
                MakeHubBox(_hub.transform, 16f, T - 426f, 560f, 222f, sprite);
                var inHdr = MakeLabel(_hub.transform, "<b>INCOMING OFFERS</b>", 12, _inkLo, 28f, T - 434f, 180f, 18f, TextAlignmentOptions.Left);
                ApplyFont(inHdr);
                string[] filters = { "all", "gifts", "loans" };
                for (int i = 0; i < 3; i++)
                {
                    var (frt, flbl) = MakeHubButton(filters[i], new Vector2(0f, 0f), 58f, HubTabOff, 22f, sprite);
                    SetTopLeft(frt, 320f + i * 64f, T - 432f);
                    flbl.fontSize = 11;
                    _hubFilterRT[i] = frt;
                    _hubFilterImg[i] = frt.GetComponent<Image>();
                }
                (_hubInVp, _hubInContent) = MakeHubScroll(_hub.transform, 28f, T - 460f, 536f, 178f, sprite);

                MakeHubBox(_hub.transform, 584f, T - 426f, 380f, 222f, sprite);
                var outHdr = MakeLabel(_hub.transform, "<b>YOUR PENDING OFFERS</b>", 12, _inkLo, 596f, T - 434f, 280f, 18f, TextAlignmentOptions.Left);
                ApplyFont(outHdr);
                (_hubOutVp, _hubOutContent) = MakeHubScroll(_hub.transform, 596f, T - 460f, 356f, 178f, sprite);
                RefreshHubFilterChips();
                _hubPageLoans.SetActive(false);
                _hubPagePermissions.SetActive(false);
                _hubSeenVersion = -1;
                _hub.SetActive(false);
                Plugin.Logger.LogInfo("[Hub] full-page window built OK.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Hub] BuildHubWindow FAILED: {ex}");
                try { if (_hub != null) { UnityEngine.Object.Destroy(_hub); _hub = null; } } catch { }
                _hubVisible = false;
            }
        }

        /// <summary>Vertical scroll view with a REAL scrollbar (track + handle)
        /// — the "side scroller thingy" that shows where you are in the list.</summary>
        private (RectTransform vp, RectTransform content) MakeHubScroll(Transform parent, float x, float y, float w, float h, Sprite? sprite)
        {
            var vpGO = MakeGO("BAMP_Vp", parent);
            var vpRT = vpGO.GetComponent<RectTransform>();
            vpRT.anchorMin = vpRT.anchorMax = vpRT.pivot = new Vector2(0f, 1f);
            vpRT.anchoredPosition = new Vector2(x, y);
            vpRT.sizeDelta = new Vector2(w - 14f, h);
            vpGO.AddComponent<RectMask2D>();
            var vpImg = vpGO.AddComponent<Image>();
            vpImg.color = new Color(0f, 0f, 0f, 0.004f);     // raycast target for the wheel

            var contentGO = MakeGO("Content", vpGO.transform);
            var cRT = contentGO.GetComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0f, 1f); cRT.anchorMax = new Vector2(1f, 1f);
            cRT.pivot = new Vector2(0.5f, 1f);
            cRT.anchoredPosition = Vector2.zero;
            cRT.sizeDelta = new Vector2(0f, h);

            // Scrollbar: slim track at the right edge, rounded handle.
            var barGO = MakeGO("Bar", parent);
            var barRT = barGO.GetComponent<RectTransform>();
            barRT.anchorMin = barRT.anchorMax = barRT.pivot = new Vector2(0f, 1f);
            barRT.anchoredPosition = new Vector2(x + w - 10f, y);
            barRT.sizeDelta = new Vector2(8f, h);
            var trackImg = barGO.AddComponent<Image>();
            trackImg.color = _hubNative ? new Color(1f, 1f, 1f, 0.08f) : new Color(0.10f, 0.11f, 0.14f, 1f);
            if (sprite != null) { try { trackImg.sprite = sprite; trackImg.type = Image.Type.Sliced; } catch { } }
            var handleGO = MakeGO("Handle", barGO.transform);
            var handleRT = handleGO.GetComponent<RectTransform>();
            handleRT.anchorMin = Vector2.zero; handleRT.anchorMax = Vector2.one;
            handleRT.offsetMin = new Vector2(1f, 1f); handleRT.offsetMax = new Vector2(-1f, -1f);
            var handleImg = handleGO.AddComponent<Image>();
            handleImg.color = _hubNative ? new Color(0.490f, 0.506f, 0.525f, 1f) : new Color(0.34f, 0.38f, 0.46f, 1f);   // native #7D8186
            if (sprite != null) { try { handleImg.sprite = sprite; handleImg.type = Image.Type.Sliced; } catch { } }
            var bar = barGO.AddComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.handleRect = handleRT;
            bar.targetGraphic = handleImg;

            var sr = vpGO.AddComponent<ScrollRect>();
            sr.content = cRT;
            sr.viewport = vpRT;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 18f;
            sr.verticalScrollbar = bar;
            return (vpRT, cRT);
        }

        private static void ClearScrollContent(RectTransform? content)
        {
            if (content == null) return;
            for (int i = content.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(content.GetChild(i).gameObject);
        }

        /// <summary>One scrolled row: rich-text label + inline buttons at the
        /// right edge.  Buttons register in _hubRowBtns for the click pass.</summary>
        private void AddHubRow(RectTransform content, RectTransform vp, int idx, float rowH, string text,
                               params (string label, Color col, string id, byte act)[] btns)
        {
            var rowGO = MakeGO("Row", content.transform);
            var rowRT = rowGO.GetComponent<RectTransform>();
            rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
            rowRT.pivot = new Vector2(0.5f, 1f);
            rowRT.anchoredPosition = new Vector2(0f, -idx * rowH);
            rowRT.sizeDelta = new Vector2(0f, rowH - 4f);
            float btnW = 0f;
            foreach (var b in btns) btnW += (b.label.Length > 2 ? Mathf.Clamp(b.label.Length * 7.5f + 14f, 64f, 150f) : 26f) + 6f;
            var lbl = MakeLabel(rowGO.transform, text, 12, _inkHi, 4f, 0f, 10f, rowH - 4f, TextAlignmentOptions.TopLeft);
            ApplyFont(lbl);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(1f, 1f);
            lrt.offsetMin = new Vector2(4f, 0f); lrt.offsetMax = new Vector2(-(btnW + 6f), 0f);
            float bx = 0f;
            var sprite = IsAlive(_panelSprite) ? _panelSprite : null;
            foreach (var b in btns)
            {
                float w = b.label.Length > 2 ? Mathf.Clamp(b.label.Length * 7.5f + 14f, 64f, 150f) : 26f;
                bx += w + 6f;
                var (brt, blbl) = MakeHubButton(b.label, Vector2.zero, w, b.col, 24f, sprite);
                brt.SetParent(rowGO.transform, false);
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 0.5f);
                brt.anchoredPosition = new Vector2(-(bx - w - 0f) - 2f, 0f);
                brt.sizeDelta = new Vector2(w, 24f);
                blbl.fontSize = 11;
                _hubRowBtns.Add((brt, vp, b.id, b.act));
            }
        }

        /// <summary>Permissions tab: rebuild the per-player VEHICLE-key list (online players + my
        /// offline grantees), gated by a signature so it only rebuilds on a real change.</summary>
        private void RefreshPermissionsList()
        {
            if (_hubPermContent == null || _hubPermVp == null) return;
            string me = MPConfig.PlayerId;
            var players = MPRestSync.AllPlayers();
            var mine = GrantSync.MyGrantees();   // my grantees incl. offline (handle + name + online)

            var sb = new System.Text.StringBuilder();
            foreach (var pl in players)
                if (pl != me) sb.Append(pl)
                    .Append(GrantSync.IsGranted(GrantKind.Vehicle, me, pl) ? '1' : '0')
                    .Append(GrantSync.IsGranted(GrantKind.Housing, me, pl) ? '1' : '0')
                    .Append(GrantSync.IsGranted(GrantKind.Business, me, pl) ? '1' : '0')
                    .Append(MergerSync.IsMemberPid(pl) ? 'M' : MergerSync.InAnyGroup(pl) ? 'O' : '-').Append(';');
            sb.Append('|').Append(MergerSync.IAmMember ? 'M' : '-')
              .Append(MergerSync.IncomingFromPid).Append('/').Append(MergerSync.OutgoingToPid)
              .Append('/').Append(string.Join(",", MergerSync.MemberNames)).Append('|');
            foreach (var g in mine)
                if (!g.Online) { sb.Append(g.Handle).Append(g.Name); foreach (var k in g.Kinds) sb.Append((int)k); sb.Append(';'); }
            string sig = sb.ToString();
            if (sig == _hubPermSig) return;
            _hubPermSig = sig;

            _hubPermBtns.Clear();
            ClearScrollContent(_hubPermContent);
            const float RowH = 34f;
            int idx = 0;
            var purple = new Color(0.35f, 0.31f, 0.81f, 1f);
            var grey   = new Color(0.20f, 0.22f, 0.30f, 1f);
            string muted = _hubNative ? "#8795A0" : "#9AA3B2";

            // Merger slice 1 — company header rows above the key list.
            // DEV-ONLY until the merger campaign ships (user 2026-07-07: next release lands before the
            // feature is ready). The entire merger stack is inert without the UI: no button → no
            // proposal → no state → every merger patch is a pass-through (the inertness contract, §13).
#if BAMP_DEV
            if (MergerSync.IAmMember)
            {
                AddPermRow(idx++, RowH, $"<b>Merged company</b>  <color={muted}>{string.Join(", ", MergerSync.MemberNames)}</color>",
                    ("Leave merger", grey, "merger", (byte)11, GrantKind.Vehicle));
            }
            if (!string.IsNullOrEmpty(MergerSync.IncomingFromPid))
            {
                AddPermRow(idx++, RowH, $"<b>{MergerSync.IncomingFromPid}</b> proposes a company merger",
                    ("Accept",  purple, "merger", (byte)9,  GrantKind.Vehicle),
                    ("Decline", grey,   "merger", (byte)10, GrantKind.Vehicle));
            }
#endif

            // Each row: the player + a Vehicle key toggle and a Housing key toggle (purple = granted).
            var onlineNames = new HashSet<string>(players);
            foreach (var pl in players)
            {
                if (pl == me) continue;
                bool gv = GrantSync.IsGranted(GrantKind.Vehicle, me, pl);
                bool gh = GrantSync.IsGranted(GrantKind.Housing, me, pl);
                bool gb = GrantSync.IsGranted(GrantKind.Business, me, pl);
#if BAMP_DEV
                // Merger chip (DEV-ONLY until the campaign ships): member of MY company → static
                // "Merged"; member of ANOTHER company → static "In a company" (they must leave it
                // before they can be proposed to); my proposal pending → "Cancel offer" (act 12
                // withdraws — host arms a re-propose cooldown so withdraw/re-propose can't be used to
                // spam notifications); else a Merge button (act 8 → confirm popup). A merger implies
                // every key, so the kind toggles read fully granted while merged (IsGranted unions).
                var mchip = (MergerSync.IAmMember && MergerSync.IsMemberPid(pl)) ? ("Merged", purple, "", (byte)0, GrantKind.Vehicle)
                          : MergerSync.InAnyGroup(pl)                            ? ("In a company", grey, "", (byte)0, GrantKind.Vehicle)
                          : MergerSync.OutgoingToPid == pl                       ? ("Cancel offer", grey, "merger", (byte)12, GrantKind.Vehicle)
                          : ("Merge", grey, "pid:" + pl, (byte)8, GrantKind.Vehicle);
                AddPermRow(idx++, RowH, pl,
                    ("Vehicle",  gv ? purple : grey, "pid:" + pl, (byte)6, GrantKind.Vehicle),
                    ("Housing",  gh ? purple : grey, "pid:" + pl, (byte)6, GrantKind.Housing),
                    ("Business", gb ? purple : grey, "pid:" + pl, (byte)6, GrantKind.Business),
                    mchip);
#else
                AddPermRow(idx++, RowH, pl,
                    ("Vehicle",  gv ? purple : grey, "pid:" + pl, (byte)6, GrantKind.Vehicle),
                    ("Housing",  gh ? purple : grey, "pid:" + pl, (byte)6, GrantKind.Housing),
                    ("Business", gb ? purple : grey, "pid:" + pl, (byte)6, GrantKind.Business));
#endif
            }
            // Offline grantees (not in the live roster) — same two toggles, by StableId handle.
            foreach (var g in mine)
            {
                if (g.Online || onlineNames.Contains(g.Name)) continue;
                string nm = string.IsNullOrEmpty(g.Name) ? "(unknown)" : g.Name;
                AddPermRow(idx++, RowH, $"{nm}  <color={muted}>(offline)</color>",
                    ("Vehicle",  g.Kinds.Contains(GrantKind.Vehicle)  ? purple : grey, "stable:" + g.Handle, (byte)7, GrantKind.Vehicle),
                    ("Housing",  g.Kinds.Contains(GrantKind.Housing)  ? purple : grey, "stable:" + g.Handle, (byte)7, GrantKind.Housing),
                    ("Business", g.Kinds.Contains(GrantKind.Business) ? purple : grey, "stable:" + g.Handle, (byte)7, GrantKind.Business));
            }

            _hubPermContent.sizeDelta = new Vector2(_hubPermContent.sizeDelta.x, Mathf.Max(idx * RowH, _hubPermVp.rect.height));
        }

        /// <summary>Merger confirm popup (build-once): proposing/accepting a merger is a big decision —
        /// spell out exactly what is being agreed to before anything is sent (user, 2026-07-07).</summary>
        private void ShowMergerConfirm(string mode, string pid)
        {
            if (string.IsNullOrEmpty(pid) || _hub == null) return;
            if (_mergerConfirmGO == null)
            {
                _mergerConfirmGO = MakeGO("BAMP_MergerConfirm", _hub.transform);
                var srt = _mergerConfirmGO.GetComponent<RectTransform>();
                srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;   // scrim over the whole hub
                srt.offsetMin = Vector2.zero; srt.offsetMax = Vector2.zero;
                _mergerConfirmGO.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.62f);

                var box = MakeGO("Box", _mergerConfirmGO.transform);
                var brt = box.GetComponent<RectTransform>();
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0.5f, 0.5f);
                brt.sizeDelta = new Vector2(470f, 220f);
                var bimg = box.AddComponent<Image>(); bimg.color = _boxCol;
                var sprite = IsAlive(_panelSprite) ? _panelSprite : null;
                if (sprite != null) { try { bimg.sprite = sprite; bimg.type = Image.Type.Sliced; } catch { } }

                _mergerConfirmLbl = MakeLabel(box.transform, "", 13, _inkHi, 18f, -14f, 434f, 140f, TextAlignmentOptions.TopLeft);
                ApplyFont(_mergerConfirmLbl); _mergerConfirmLbl.enableWordWrapping = true;

                var (ok, okLbl) = MakeHubButton("Confirm", Vector2.zero, 110f, new Color(0.35f, 0.31f, 0.81f, 1f), 30f, sprite);
                ok.SetParent(box.transform, false);
                ok.anchorMin = ok.anchorMax = ok.pivot = new Vector2(1f, 0f);
                ok.anchoredPosition = new Vector2(-16f, 14f); ok.sizeDelta = new Vector2(110f, 30f);
                okLbl.fontSize = 12; _rtMergerOk = ok;

                var (cx, cxLbl) = MakeHubButton("Cancel", Vector2.zero, 90f, new Color(0.20f, 0.22f, 0.30f, 1f), 30f, sprite);
                cx.SetParent(box.transform, false);
                cx.anchorMin = cx.anchorMax = cx.pivot = new Vector2(1f, 0f);
                cx.anchoredPosition = new Vector2(-136f, 14f); cx.sizeDelta = new Vector2(90f, 30f);
                cxLbl.fontSize = 12; _rtMergerCancel = cx;
            }
            _mergerConfirmMode = mode; _mergerConfirmPid = pid;
            string terms = "A merger runs your companies as <b>one</b>: every member gets full access to " +
                           "everything the others own — businesses, registers and storage, homes, and " +
                           "vehicles — including selling and spending on the company's behalf. Any member " +
                           "can leave the merger at any time.";
            if (_mergerConfirmLbl != null)
                _mergerConfirmLbl.text = mode == "propose"
                    ? $"<b>Propose merging companies with {pid}?</b>\n\n{terms}\n\nThey will be asked to accept."
                    : $"<b>Merge companies with {pid}?</b>\n\n{terms}";
            _mergerConfirmGO.SetActive(true);
            _mergerConfirmGO.transform.SetAsLastSibling();   // above the tab content
        }

        /// <summary>One Permissions-tab row: name label + a key toggle, registered in _hubPermBtns.</summary>
        private void AddPermRow(int idx, float rowH, string text,
                                params (string label, Color col, string id, byte act, GrantKind kind)[] btns)
        {
            if (_hubPermContent == null || _hubPermVp == null) return;
            var rowGO = MakeGO("PermRow", _hubPermContent.transform);
            var rowRT = rowGO.GetComponent<RectTransform>();
            rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
            rowRT.pivot = new Vector2(0.5f, 1f);
            rowRT.anchoredPosition = new Vector2(0f, -idx * rowH);
            rowRT.sizeDelta = new Vector2(0f, rowH - 4f);
            float btnW = 0f;
            foreach (var b in btns) btnW += Mathf.Clamp(b.label.Length * 7.5f + 14f, 64f, 150f) + 6f;
            var lbl = MakeLabel(rowGO.transform, text, 12, _inkHi, 4f, 0f, 10f, rowH - 4f, TextAlignmentOptions.Left);
            ApplyFont(lbl);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(1f, 1f);
            lrt.offsetMin = new Vector2(4f, 0f); lrt.offsetMax = new Vector2(-(btnW + 6f), 0f);
            float bx = 0f;
            var sprite = IsAlive(_panelSprite) ? _panelSprite : null;
            foreach (var b in btns)
            {
                float w = Mathf.Clamp(b.label.Length * 7.5f + 14f, 64f, 150f);
                bx += w + 6f;
                var (brt, blbl) = MakeHubButton(b.label, Vector2.zero, w, b.col, 24f, sprite);
                brt.SetParent(rowGO.transform, false);
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 0.5f);
                brt.anchoredPosition = new Vector2(-(bx - w) - 2f, 0f);
                brt.sizeDelta = new Vector2(w, 24f);
                blbl.fontSize = 11;
                _hubPermBtns.Add((brt, _hubPermVp, b.id, b.act, b.kind));
            }
        }

        /// <summary>Shaded rounded INPUT box + inner value label (click → type;
        /// the shading is the affordance that says "text field").</summary>
        private (RectTransform rt, TextMeshProUGUI lbl) MakeHubInput(Transform parent, float x, float y, float w, float h, int fontSize, Sprite? sprite)
        {
            var go = MakeGO("BAMP_HubInput", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(w, h);
            var img = go.AddComponent<Image>();
            img.color = _hubNative ? new Color(1f, 1f, 1f, 0.10f) : new Color(0.10f, 0.11f, 0.145f, 1f);   // shaded well on the navy box
            if (sprite != null) { try { img.sprite = sprite; img.type = Image.Type.Sliced; } catch { } }
            var lbl = MakeLabel(go.transform, "", fontSize, _inkHi, 10f, 0f, w - 20f, h, TextAlignmentOptions.Left);
            ApplyFont(lbl);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(1f, 1f);
            lrt.offsetMin = new Vector2(10f, 0f); lrt.offsetMax = new Vector2(-10f, 0f);
            return (rt, lbl);
        }

        /// <summary>Rounded section box in the native-page style.</summary>
        private void MakeHubBox(Transform parent, float x, float y, float w, float h, Sprite? sprite)
        {
            var go = MakeGO("Box", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(w, h);
            var img = go.AddComponent<Image>();
            img.color = _boxCol;
            if (sprite != null) { try { img.sprite = sprite; img.type = Image.Type.Sliced; } catch { } }
        }

        private double HubRate()
            => double.TryParse(_hubRateStr, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var r)
               ? Math.Min(Math.Max(r, 0.0), 1000.0) : 20.0;

        private int HubTerm()
            => int.TryParse(_hubTermStr, out var t) ? Math.Min(Math.Max(t, 1), 999) : 244;

        private void CommitHubInputs()
        {
            if (_hubAmountFocus)
            {
                _hubAmountFocus = false;
                if (long.TryParse(_hubAmountStr, out var v) && v > 0) _hubAmount = Math.Max(100, v);
            }
            if (_hubRateFocus)
            {
                _hubRateFocus = false;
                _hubRateStr = HubRate().ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (_hubTermFocus)
            {
                _hubTermFocus = false;
                _hubTermStr = HubTerm().ToString();
            }
            if (_hubPartialFocus)
            {
                _hubPartialFocus = false;
                if (long.TryParse(_hubPartialStr, out var pv) && pv > 0) _hubPartialAmount = Math.Max(1, pv);
                _hubSeenVersion = -1;   // re-render rows so the "partial $X" buttons reflect the committed amount
            }
        }

        private void HandleHubTyping()
        {
            if (!_hubAmountFocus && !_hubRateFocus && !_hubTermFocus && !_hubPartialFocus) return;
            foreach (char c in Input.inputString)
            {
                // First keystroke after focusing REPLACES the value — the term
                // box was effectively uneditable ("244" already filled the cap
                // and its backspace branch was missing, 2026-06-10).
                if (_hubFreshFocus && (char.IsDigit(c) || c == '.'))
                {
                    if (_hubAmountFocus) _hubAmountStr = "";
                    else if (_hubRateFocus) _hubRateStr = "";
                    else if (_hubTermFocus) _hubTermStr = "";
                    else if (_hubPartialFocus) _hubPartialStr = "";
                }
                if (c != '\n' && c != '\r') _hubFreshFocus = false;

                if (c == '\b')
                {
                    if (_hubAmountFocus && _hubAmountStr.Length > 0) _hubAmountStr = _hubAmountStr.Substring(0, _hubAmountStr.Length - 1);
                    else if (_hubRateFocus && _hubRateStr.Length > 0) _hubRateStr = _hubRateStr.Substring(0, _hubRateStr.Length - 1);
                    else if (_hubTermFocus && _hubTermStr.Length > 0) _hubTermStr = _hubTermStr.Substring(0, _hubTermStr.Length - 1);
                    else if (_hubPartialFocus && _hubPartialStr.Length > 0) _hubPartialStr = _hubPartialStr.Substring(0, _hubPartialStr.Length - 1);
                }
                else if (c == '\n' || c == '\r') CommitHubInputs();
                else if (_hubAmountFocus && char.IsDigit(c) && _hubAmountStr.Length < 9) _hubAmountStr += c;
                else if (_hubRateFocus && (char.IsDigit(c) || (c == '.' && !_hubRateStr.Contains('.'))) && _hubRateStr.Length < 5) _hubRateStr += c;
                else if (_hubTermFocus && char.IsDigit(c) && _hubTermStr.Length < 3) _hubTermStr += c;
                else if (_hubPartialFocus && char.IsDigit(c) && _hubPartialStr.Length < 9) _hubPartialStr += c;
            }
            if (_hubPartialFocus && !string.IsNullOrEmpty(Input.inputString)) _hubSeenVersion = -1;   // live-update the partial buttons as you type
            if (Input.GetKeyDown(KeyCode.Escape)) { _hubAmountFocus = false; _hubRateFocus = false; _hubTermFocus = false; _hubPartialFocus = false; }
        }

        private void SetTopLeft(RectTransform? rt, float x, float y)
        {
            if (rt == null) return;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
        }

        private (RectTransform rt, TextMeshProUGUI lbl) MakeHubButton(string label, Vector2 pos, float w, Color bg, float h, Sprite? sprite, Transform? parent = null)
        {
            // Explicit parent for non-hub callers (join popup etc.) — the _hub
            // default threw when the hub hadn't been built yet (popup no-show).
            var host = parent ?? _hub?.transform ?? _canvasGO!.transform;
            var go = MakeGO("BAMP_Hub_" + label + pos.x, host);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(w, h);
            var img = go.AddComponent<Image>();
            img.color = bg;
            if (sprite != null) { try { img.sprite = sprite; img.type = Image.Type.Sliced; } catch { } }
            var lbl = MakeLabel(go.transform, label, 11, C_WHITE, 0f, 0f, w, h, TextAlignmentOptions.Center);
            ApplyFont(lbl);
            var lrt = lbl.rectTransform; lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            return (rt, lbl);
        }

        private static void WriteWorldClock(double totalHours)
        {
            if (totalHours < 0) totalHours = 0;
            int   day  = (int)(totalHours / 24.0);
            float hour = (float)(totalHours - day * 24.0);
            GameStateReader.SetGameTime(day, hour);
        }

        // ── Game-load detection & player sync ─────────────────────────────────

        private static bool IsInGame()
        {
            try { return SaveGameManager.Current != null && PlayerHelper.PlayerController != null; }
            catch { return false; }
        }

#if BAMP_DEV
        private float _animProbeNext;
        private Vector3 _animProbeLastPos;
        private Vector3 _animProbeLastPosH;
        private Vector3 _animProbeLastPosA;
        /// <summary>DEV anim probe — every 0.5s in-game (tagged SP/HOST/CLIENT), logs
        /// the local player's run-animation pipeline: NavMeshAgent (is it moving?) →
        /// Animator state (enabled/speed/cull/updateMode) → every FLOAT param (the
        /// locomotion drivers) → current state per layer (hash + normalizedTime, so
        /// we can see if it's frozen).  Run in SP (works) then host (broken) and diff
        /// the lines: the field that differs is where the chain breaks.</summary>
        // DIAG:INVESTIGATION(anim-norun) — open "no running animation" host bug. Called only
        //   under #if BAMP_DEV; remove when that bug is closed. See docs/DIAGNOSTICS.md.
        private void TickAnimProbe()
        {
            if (!IsInGame()) return;
            if (Time.unscaledTime < _animProbeNext) return;
            _animProbeNext = Time.unscaledTime + 0.5f;
            try
            {
                var ch = PlayerHelper.PlayerController?.Character;
                if (ch == null) return;
                var model = ch.transform.Find("Model");
                var anim  = (model != null ? model.GetComponent<Animator>() : null) ?? ch.GetComponentInChildren<Animator>();
                var agent = ch.GetComponent<UnityEngine.AI.NavMeshAgent>();
                string role = MPServer.IsRunning ? "HOST" : (MPClient.IsConnected ? "CLIENT" : "SP");

                var sb = new System.Text.StringBuilder();
                sb.Append($"[AnimProbe/{role}] ");
                sb.Append(agent == null ? "agent=NULL"
                    : $"agent(en={agent.enabled},onMesh={agent.isOnNavMesh},vel={agent.velocity.magnitude:F2},hasPath={agent.hasPath})");
                if (anim == null) { sb.Append(" anim=NULL"); Plugin.Logger.LogInfo(sb.ToString()); return; }
                sb.Append($" anim(en={anim.enabled},speed={anim.speed:F2},root={anim.applyRootMotion},upd={anim.updateMode},cull={anim.cullingMode})");
                // Did the character actually translate, and is the game suppressing input?
                // Three position sources: the Character root (what locomotion/the
                // probe read), the game's reported player pos, and the Animator's
                // own transform (the visible Model).  The one that moves while the
                // others don't is where the disconnect is.  chId catches a stale ref.
                var pos = ch.transform.position;
                float movedC = _animProbeLastPos == default ? 0f : (pos - _animProbeLastPos).magnitude;
                _animProbeLastPos = pos;
                Vector3 posH = pos; try { posH = PlayerHelper.GetPosition(); } catch { }
                float movedH = _animProbeLastPosH == default ? 0f : (posH - _animProbeLastPosH).magnitude;
                _animProbeLastPosH = posH;
                var posA = anim.transform.position;
                float movedA = _animProbeLastPosA == default ? 0f : (posA - _animProbeLastPosA).magnitude;
                _animProbeLastPosA = posA;
                bool hasInput = false; try { hasInput = GameManager.HasInputSelected(); } catch { }
                sb.Append($" chId={ch.GetInstanceID()} movedRoot={movedC:F2} movedPlayerPos={movedH:F2} movedModel={movedA:F2} posRoot=({pos.x:F1},{pos.z:F1}) posPlayer=({posH.x:F1},{posH.z:F1}) input(supp={MPChat.SuppressGameInput},hasInput={hasInput})");
                var ps = anim.parameters;
                for (int i = 0; i < ps.Length; i++)
                    if (ps[i].type == AnimatorControllerParameterType.Float)
                        sb.Append($" {ps[i].name}={anim.GetFloat(ps[i].name):F2}");
                for (int l = 0; l < anim.layerCount && l < 3; l++)
                {
                    var st = anim.GetCurrentAnimatorStateInfo(l);
                    sb.Append($" L{l}(hash={st.fullPathHash},nt={(st.normalizedTime % 1f):F2},w={anim.GetLayerWeight(l):F2})");
                }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AnimProbe] {ex.Message}"); }
        }
#endif

        private static float _rivalStatsNextPush;
        private void TickRivalStatsPush()
        {
            if (Time.unscaledTime < _rivalStatsNextPush) return;
            _rivalStatsNextPush = Time.unscaledTime + 120f;
            if (!IsInGame()) return;
            try
            {
                if (MPClient.IsConnected)     MPClient.SendRivalsStatsRequest();       // self-report; host broadcasts merged snapshot on receipt
                else if (MPServer.IsRunning)  MPServer.BroadcastRivalsStatsSnapshot(); // host-only session: push host stats to any joiners
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UI] TickRivalStatsPush: {ex.Message}"); }
        }

        private void TickGameLoadDetect()
        {
            bool inGame = IsInGame();
            if (inGame && !_wasInGame)
            {
                Plugin.Logger.LogInfo("[UI] Game scene loaded — player sync active.");
                // Quiesce ends a few seconds LATER: resuming the stream the
                // same frame killed the game's load-finish fade (2 GM NREs at
                // exactly quiesce-OFF; loading screen never faded, 2026-06-11).
                // (4s quiesce timer retired — WorldReady event owns it now)

                // Fresh world-clock skip detector + appearance state for this session.
                ResetWorldClock();
                TimeSync.ResetClockState();   // drop any leftover MP clock catch-up / ahead-hold so a fresh game (SP or a new MP session) doesn't inherit it
                TrafficSync.Reset();
                ParkedVehicleSync.Reset();
                MPRegisterSync.Reset();   // duty posts die with the scene
                PassengerSync.Reset();    // passenger seats/locks die with the scene
                MergerSync.ResetSceneState(); // merger runtime + pending-proposal UI state (same lifecycle as grants)
                MergerFlip.Reset();           // flip tracking dies with the scene's regs (tick re-applies from state)
                MergerEmployeeSync.Reset();   // schedule write-back baselines die with the regs too (slice 5)
                GrantSync.ResetSceneState();  // runtime grants + local caches die with the scene; the DURABLE store
                                              // survives — its lifecycle is session boundaries (StartNewGame /
                                              // manifest restore), NOT scene loads. The old full Reset() here fired
                                              // after the load-time restore and wiped every restored grant → "shares
                                              // lost on load" + the join-refresh persisted 0 over the manifest (2026-06-30).
                MPServer.RebuildGrantsAfterSceneReset();  // host: immediate store→runtime rebuild + re-broadcast, so
                                              // enforcement never depends on which machine's scene finishes first
                InteriorSync.Reset();     // interior subs + owner-snapshot caches die with the scene — a prior session's Authoritative=true snapshot must not bleed into a new world (was never wired up)
                GameStatePatcher.SweepPurchaserPollution("scene ready");   // heal saves polluted by the pre-round-34 purchaser injectio
                GameStatePatcher.SweepRivalFieldContamination("scene ready");   // heal renters written into the DEED field (rent-vs-deed split 2026-07-09)
                _worldHealthAt = Time.unscaledTime + 30f;   // world-integrity line AFTER the rival cache fills (30s)
                // Round-35: a save can carry a STALE BAMP_ proxy id in ActiveVehicleId (borrowed cart
                // despawned mid-push before the round-34b exit guard existed). IsUsingVehicle then reads
                // true forever with no resolvable vehicle → box clicks NRE at ItemController.Interact:509
                // ("owner couldn't access the shelf"). BAMP_ ids are ours and never valid across sessions.
                try
                {
                    var av = SaveGameManager.Current?.ActiveVehicleId;
                    if (!string.IsNullOrEmpty(av) && av.StartsWith("BAMP_", StringComparison.Ordinal))
                    {
                        SaveGameManager.Current.ActiveVehicleId = null;
                        Plugin.Logger.LogWarning($"[Vehicle] scene-ready sweep: cleared stale proxy ActiveVehicleId='{av}' from the loaded save.");
                    }
                    // Round-37m: normalize EMPTY to NULL — the native on-foot contract is null; a persisted ""
                    // (written by the pre-37m take-repair) passes ShelfCtaBehavior's `!= null` check and NREs
                    // the hover chain on every storage shelf. Heals poisoned saves at load.
                    else if (av != null && av.Length == 0)
                    {
                        SaveGameManager.Current.ActiveVehicleId = null;
                        Plugin.Logger.LogWarning("[Vehicle] scene-ready sweep: normalized empty ActiveVehicleId to null (dead-shelf-hover poison, round-37m).");
                    }
                }
                catch { }
                // Round-39: in MP the pause is network-arbitrated (startup hold / votes) — a save carrying
                // LastPlayerPause=true makes GameSpeedController boot playerGameSpeed PAUSED on every load
                // (GSC :64 reads it), an SP pause preference restoring into an MP session as a half-pause.
                // Saves got poisoned by the pre-39 corpse-invoke bug (TogglePause on a destroyed GSC wrote
                // the flag before throwing); a save written mid-vote-pause carries it legitimately too.
                try
                {
                    if (SaveGameManager.Current != null && SaveGameManager.Current.LastPlayerPause)
                    {
                        SaveGameManager.Current.LastPlayerPause = false;
                        Plugin.Logger.LogWarning("[GSC] scene-ready sweep: cleared LastPlayerPause=true (MP pause is network-arbitrated; a restored SP pause preference reads as a half-pause).");
                    }
                }
                catch { }
                MPAudit.Reset();          // divergence streaks/throttle die with the session (else stale [Audit] state pollutes the bug-report log across same-process sessions)
                CustomerPuppets.Reset();  // round-41: puppet bodies + authority table die with the scene
                MPStockSync.Reset();      // per-shop stock digests die with the session
                _appearanceSig = ""; _appearanceNextAt = 0f;
                _blackOverlayCanvas = null;     // re-scan on fresh game load (#6)
                _blackOverlayFindTimer = 0f;

                // Startup pause hold — frontload the world sync the moment our scene
                // loads (report in-game so the host serves snapshots NOW), but DEFER
                // the actual freeze until our loading OVERLAY has cleared.  The native
                // overlay fades at normal time; freezing before it clears could stall
                // its own fade-out (timeScale=0) → deadlock.  TickOverlayFreezeGate
                // does the BeginStartupHold + world-ready report once the overlay is
                // gone — so the freeze holds until every player (especially the host,
                // usually last to finish loading) has TRULY entered the game.

                if (MPServer.IsRunning || MPClient.IsConnected)
                {
                    MPClient.InMpGame = true;   // sticky: entering an MP game world (survives transient drops)
                    _sceneLoadedPendingFreeze = true;
                    _pendingFreezeElapsed = 0f;
                    _freezeGateDiagNext = 0f;   // heartbeat restarts with the gate
                    if (MPServer.IsRunning)
                        MPServer.MarkPlayerInGame(MPConfig.PlayerId);   // host serves snapshots now
                    else
                        MPClient.SendPlayerInGame();
                }

                if (MPServer.IsRunning)
                {
                    // Delay so all clients have time to finish loading their scene
                    _worldSnapshotTimer = 4f;
                    Plugin.Logger.LogInfo("[UI] WorldSnapshot scheduled in 4 s.");
                }

                // Trigger a time sync and market sync shortly after game loads.
                _timeSyncTimer   = 5f;
                _marketSyncTimer = 8f;
            }
            else if (!inGame && _wasInGame)
            {
                Plugin.Logger.LogInfo("[UI] Left game scene — cleaning up remote players.");
                GameStatePatcher.EnqueueOnMainThread(() => RemotePlayerManager.RemoveAll());
                _sceneLoadedPendingFreeze = false;
                // Per-world apply caches: the CBC objects die with the scene and
                // the next world's sign states start fresh — stale entries would
                // wrongly skip (or target dead controllers for) the first repaints.
                GameStatePatcher.ResetCBCCache();
                GameStatePatcher.ResetVisualSigCache();
                TrafficSync.InvalidateVehiclePool();   // pool objects die with the scene
                MPPhoneButton.Reset();                 // re-inject in the next game scene
                MPHandIk.Reset();                      // cached refs die with the scene
                MPPriceSync.Reset();                   // price hashes are per-session
                MPRestSync.Reset();                    // votes/skip die with the session
                MPHub.Reset(); _hubVisible = false;    // hub ledger is per-session
                MPHubNativePage.Reset(); _hub = null; _hubNative = false;   // page died with the scene
                _spawnSidestepDone = false;   // next session de-stacks again
                // The session-over lock is for the in-game world only — back at
                // the menu the player is free to host/join again.
                MPClient.SessionEnded = false;
                MPClient.InMpGame    = false;   // left the MP game world → native time allowed again (SP/menu)

                // Leaving the game scene ends the MP session (exit to main menu).
                // Tear the network down so the next Host/Join starts clean — a
                // zombie server otherwise keeps the port bound and the next
                // Start() fails with AddressAlreadyInUse while IsRunning still
                // reads true (host loads alone; client's join times out).
                // NOTE: if a future flow reloads a save MID-session (scene drops
                // out of game while staying connected), gate this on a
                // load-in-progress flag.
                try
                {
                    if (MPServer.IsRunning)
                    {
                        Plugin.Logger.LogInfo("[UI] Exited to menu — stopping host session.");
                        MPServer.Stop();
                    }
                    else if (MPClient.IsConnected || MPClient.IsConnecting)
                    {
                        Plugin.Logger.LogInfo("[UI] Exited to menu — disconnecting from host.");
                        MPClient.Disconnect();
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[UI] MP teardown on scene exit: {ex.Message}"); }
            }
            _wasInGame = inGame;
        }

        /// <summary>Toggle the in-game MP window (F9 key + BizPhone Chat button).</summary>
        private void ToggleMpWindow()
        {
            _mpWinVisible = !_mpWinVisible;
            StyleMpWindow();
            _mpWin!.SetActive(_mpWinVisible);
            if (_mpWinVisible) SetMpOpacity(_mpOpacity);   // re-assert opacity on show
            else { _mpChatFocus = false; _mpDragging = false; _mpOpacityDragging = false; _mpResizing = false; SyncChatNavBlock(false); MPChat.SuppressGameInput = false; }
        }

        // ── Camera audit (highlight wrong-target investigation) ───────────────
        // The hover pick ray is cast FROM A CAMERA; if a cloned ghost smuggled an
        // enabled camera into the scene, picking happens from the wrong eye and
        // highlights land on buildings away from the cursor.  Log the enabled-
        // camera set whenever it changes.

        // ── Overlay-aware freeze gate ─────────────────────────────────────────
        // Our scene has loaded (PlayerController exists) but the native loading
        // OVERLAY may still be fading.  We freeze (and report ourselves truly in
        // the game) only once that overlay is gone — so nobody unfreezes onto a
        // half-loaded world and the hold survives until the host (usually last to
        // finish loading) has fully entered.  Deferring the freeze past the overlay
        // also avoids stalling the overlay's own fade-out under timeScale=0.
        private bool  _sceneLoadedPendingFreeze;
        private float _pendingFreezeElapsed;
        private float _freezeGateDiagNext;   // [FreezeGate] heartbeat schedule
        // Fail-safe only.  A real staggered load took ~57s, so 20s released far too
        // soon; this just guarantees we can never hang forever if detection breaks.
        private const float PENDING_FREEZE_MAX = 180f;

        // "World is populated" thresholds — the host won't declare itself ready until
        // it has actually spawned at least this many driving + parked vehicles, so
        // nobody unpauses onto an empty street.  Counts are logged each second so we
        // can tune these from real data.
        private const int CARS_PARKED_MIN  = 5;
        private const int CARS_TRAFFIC_MIN = 3;
        private static float _carCheckNext;
        private static int   _carParkedCached;
        private static int   _carTrafficCached;
        private float        _hostReadyDiagNext;

        private void TickOverlayFreezeGate()
        {
            if (!_sceneLoadedPendingFreeze) return;
            if (!(MPServer.IsRunning || MPClient.IsConnected))
            {
                // LOUD: this is the gate's only silent kill — if it fires, the
                // 180s fail-safe dies with it (2026-06-11 stuck-load suspect #1).
                Plugin.Logger.LogWarning(
                    $"[FreezeGate] pendingFreeze CLEARED by connection check (IsRunning={MPServer.IsRunning} " +
                    $"IsConnected={MPClient.IsConnected}) at elapsed={_pendingFreezeElapsed:F0}s — fail-safe disarmed.");
                _sceneLoadedPendingFreeze = false; return;
            }

            _pendingFreezeElapsed += Time.unscaledDeltaTime;
            bool overlayGone = !IsLoadingOverlayUp();
            bool fallback    = _pendingFreezeElapsed >= PENDING_FREEZE_MAX;

            // Heartbeat every 15s while pending — proves the gate is alive and
            // shows exactly why it hasn't fired (2026-06-11: fail-safe never
            // fired in 10 min with no evidence of which early-return ate it).
            if (_pendingFreezeElapsed >= _freezeGateDiagNext)
            {
                _freezeGateDiagNext = _pendingFreezeElapsed + 15f;
                Plugin.Logger.LogInfo(
                    $"[FreezeGate] pending elapsed={_pendingFreezeElapsed:F0}s overlayGone={overlayGone} " +
                    $"fallback@{PENDING_FREEZE_MAX:F0}s host={MPServer.IsRunning}");
            }

            if (MPServer.IsRunning)
            {
                // The HOST is the authoritative "everyone starts here" trigger.  Do NOT
                // declare it in-world until BOTH its game loading-screen overlay is gone
                // AND the world is actually populated (driving + parked cars spawned) —
                // so clients unpause onto a live, fully-loaded world, all together.  The
                // host runs at normal speed until then (cars can't spawn while frozen).
                bool carsReady = HostWorldHasVehicles();
                DiagHostReadiness(overlayGone, carsReady);
                if (!((overlayGone && carsReady) || fallback)) return;

                _sceneLoadedPendingFreeze = false;
                TimeSync.BeginStartupHold();
                _startupHoldElapsed = 0f;
                MPLoadProfiler.Mark(
                    $"HOST fully loaded → world-ready (overlayGone={overlayGone}, carsReady={carsReady}, " +
                    $"parked={_carParkedCached}, traffic={_carTrafficCached}, fallback={fallback}, elapsed {_pendingFreezeElapsed:F1}s)");
                MPServer.SeedHostName();   // host name in the map before any rivals/business snapshot
                MPServer.MarkWorldReady(MPConfig.PlayerId, hostSelf: true);
            }
            else
            {
                // The CLIENT freezes as soon as its OWN overlay clears, then waits on the
                // MP screen until the host reports fully loaded.  WorldReady is sent once
                // the world sync is also applied (here or from ApplyBusinessSnapshot).
                if (!(overlayGone || fallback)) return;

                _sceneLoadedPendingFreeze = false;
                TimeSync.BeginStartupHold();
                _startupHoldElapsed = 0f;
                MPLoadProfiler.Mark(
                    $"CLIENT overlay gone → frozen, waiting for host (overlayGone={overlayGone}, fallback={fallback}, elapsed {_pendingFreezeElapsed:F1}s)");
                if (MPClient.WorldSyncApplied)
                    MPClient.SendWorldReady();
            }
        }

        /// <summary>Host-only: true once the host has spawned enough driving + parked
        /// vehicles that the world looks alive.  Counts are throttled + cached.</summary>
        private static bool HostWorldHasVehicles()
        {
            float now = Time.unscaledTime;
            if (now >= _carCheckNext)
            {
                _carCheckNext = now + 0.5f;
                try { _carParkedCached  = ParkedVehicleSync.HostTrackedCount; } catch { }
                try { _carTrafficCached = TrafficSync.HostTrafficCount();     } catch { }
            }
            return _carParkedCached >= CARS_PARKED_MIN && _carTrafficCached >= CARS_TRAFFIC_MIN;
        }

        private void DiagHostReadiness(bool overlayGone, bool carsReady)
        {
            float now = Time.unscaledTime;
            if (now < _hostReadyDiagNext) return;
            _hostReadyDiagNext = now + 1f;
            MPLoadProfiler.Mark(
                $"HOST loading… overlayGone={overlayGone} parked={_carParkedCached}/{CARS_PARKED_MIN} " +
                $"traffic={_carTrafficCached}/{CARS_TRAFFIC_MIN} carsReady={carsReady} elapsed={_pendingFreezeElapsed:F1}s");
        }

        /// <summary>True while the game's loading-screen overlay is still visibly up.
        /// Tracks the actual <c>LoadingScreen</c> object + its CanvasGroup fade — NOT the
        /// <c>LoadScene.isLoading</c> flag, which the loadprof showed stays stuck true and
        /// never reports "gone".  Throttled (FindObjectOfType isn't free).</summary>
        private static float _overlayCheckNext;
        private static bool  _overlayCheckCached = true;
        // Once the world is live AND the overlay has gone, the loading screen
        // cannot reappear without a fresh load — so we latch "gone" and STOP the
        // per-0.25s full-scene FindObjectOfType walk.  That walk (a whole-city
        // object-table scan, worst-case because in-game there's no LoadingScreen
        // to find) was a ~58ms main-thread hitch every 0.25s = the razor-sharp
        // ~257ms MP-only stutter.  Re-arms on the next load (PlayerController→null).
        // (The sibling menu scan in TickMenuIntegration was already guarded the
        //  same way — "twice a second, forever … rhythmic car stutter"; this is
        //  the twin that was missed.)
        private static bool _overlayConfirmedGone;
        internal static bool IsLoadingOverlayUp()
        {
            bool worldUp = false;
            try { worldUp = Helpers.PlayerHelper.PlayerController != null; } catch { }
            if (!worldUp) _overlayConfirmedGone = false;   // fresh load → re-arm the scan
            if (_overlayConfirmedGone) return false;       // latched gone → skip the scan

            float now = Time.unscaledTime;
            if (now < _overlayCheckNext) return _overlayCheckCached;
            _overlayCheckNext = now + 0.25f;

            bool up;
            try
            {
                var lsObj = UnityEngine.Object.FindObjectOfType(typeof(LoadingScreen));
                if (lsObj == null) up = false;                  // no active LoadingScreen → gone
                else
                {
                    var go = (lsObj as LoadingScreen)?.gameObject;
                    if (go == null || !go.activeInHierarchy) up = false;
                    else
                    {
                        // Present — but a fully faded-out CanvasGroup means it's gone.
                        var cg = go.GetComponentInChildren<CanvasGroup>(true);
                        up = (cg == null) ? true : (cg.alpha >= 0.05f);
                    }
                }
            }
            catch { up = false; }    // never hang the startup on a detection error
            _overlayCheckCached = up;
            if (!up && worldUp) _overlayConfirmedGone = true;   // confirmed gone with world live → latch off
            return up;
        }

        // (LoadTrace v2 removed 2026-06-12 dead-code sweep — stuck-load was
        //  localized + fixed: overlay watchdog demoted, fence fallback added.)

        // ── Overlay watchdog: the game's loading screen can survive its own
        // load-finish step (a transient GameManager hiccup kills the fade) and
        // sit forever over a perfectly healthy world.  Force-dismiss. ─────────
        // (_quiesceOffAt retired — stage-4 migration #1)
        private float _overlayStuckSince;

        private void TickOverlayWatchdog()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) { _overlayStuckSince = 0f; return; }
                bool worldUp = false;
                try { worldUp = Helpers.PlayerHelper.PlayerController != null; } catch { }
                if (!worldUp || !IsLoadingOverlayUp()) { _overlayStuckSince = 0f; return; }
                if (_overlayStuckSince == 0f) { _overlayStuckSince = Time.unscaledTime; return; }
                if (Time.unscaledTime - _overlayStuckSince < 30f) return;
                _overlayStuckSince = 0f;
                // DIAGNOSTIC ONLY.  The force-dismiss variant KILLED the game's
                // load-finish coroutine on EVERY host load (PlayerController
                // spawns long before the overlay legitimately drops, so the 12s
                // "stuck" check tripped on normal loads; killing LoadingScreen
                // killed its coroutine → controller dead, clock frozen, HUD
                // half-bound — localized via LoadTrace + fired-count,
                // 2026-06-11).  NEVER touch the LoadingScreen object.
                Plugin.Logger.LogWarning("[UI] overlay still up 30s after world spawn (diagnostic only — no action taken).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UI] overlay watchdog: {ex.Message}"); }
        }

        /// <summary>
        /// Host-side watchdog: if the startup hold lasts longer than the timeout
        /// (a player stuck loading and never reporting in-game), force-release so
        /// the game can never freeze permanently.
        /// </summary>
        private void TickStartupTimeout()
        {
            if (!TimeSync.IsStartupHeld) { _startupHoldElapsed = 0f; return; }
            if (!MPServer.IsRunning) return;   // only the host decides the release

            _startupHoldElapsed += Time.unscaledDeltaTime;
            if (_startupHoldElapsed >= STARTUP_HOLD_TIMEOUT)
            {
                Plugin.Logger.LogWarning(
                    $"[UI] Startup hold exceeded {STARTUP_HOLD_TIMEOUT}s — force-releasing.");
                MPServer.ForceReleaseStartupHold();
                _startupHoldElapsed = 0f;
            }
        }

        /// <summary>Sends the local player's appearance whenever it CHANGES
        /// (5s hash-gated poll).  The old one-shot captured the model's DEFAULT
        /// wardrobe before the save dressed the character — both machines
        /// broadcast identical defaults and ghosts looked like mirror copies
        /// (2026-06-11); it also never re-sent after a clothing change.</summary>
        private string _appearanceSig = "";
        private float _appearanceNextAt;

        private void TrySendLocalAppearance()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;
            if (Time.unscaledTime < _appearanceNextAt) return;
            _appearanceNextAt = Time.unscaledTime + 5f;
            if (IsLoadingOverlayUp()) return;     // mid-load reads = default outfit
            var dto = RemotePlayerManager.ReadLocalAppearance();
            if (dto == null) return;              // character not ready — retry next poll
            // FULL signature (variants + colors + blends): Summary() missed
            // color changes, so post-load tint corrections never re-sent.
            string sig = RemotePlayerManager.FullSig(dto);
            if (sig == _appearanceSig) return;
            bool resend = _appearanceSig != "";
            _appearanceSig = sig;
            Plugin.Logger.LogInfo($"[Appearance] Local appearance {(resend ? "RE-sent (changed)" : "sent")}: {RemotePlayerManager.Summary(dto)} ({dto.Colors.Count} colors, {dto.Blends.Count} blends)");
            if (MPServer.IsRunning) MPServer.RegisterHostAppearance(dto);
            else                    MPClient.SendAppearance(dto);
        }

        private void TickWorldSnapshot()
        {
            if (_worldSnapshotTimer <= 0f) return;
            _worldSnapshotTimer -= Time.deltaTime;
            if (_worldSnapshotTimer > 0f) return;

            _worldSnapshotTimer = 0f;
            MPServer.BroadcastWorldSnapshotToAll();
            Plugin.Logger.LogInfo("[UI] WorldSnapshot broadcast fired.");
        }

        // ── Intro-scene name pre-fill (#2) ───────────────────────────────────
        // When the character-creation screen is created, pre-fill its name field
        // with the name the user set in the F8 lobby panel (MPConfig.PlayerId) so
        // they don't retype it.  EVENT-DRIVEN: a Harmony Postfix on
        // IntroCharacterCustomizer.Start (MPPatches) calls this ONCE when the screen
        // appears.  Replaces a per-frame FindObjectsOfType that, after a save load
        // (char-gen never runs), scanned the whole object table forever — a 90->12fps
        // single-player drain (perf log 2026-06-14).
        internal static void PrefillIntroName(IntroCharacterCustomizer customizer)
        {
            try
            {
                if (customizer == null) return;
                var fields = customizer.GetComponentsInChildren(typeof(TMP_InputField), true);
                if (fields == null || fields.Length == 0) return;

                var preferred = MPConfig.PlayerId;
                if (string.IsNullOrWhiteSpace(preferred)) return;

                // Split on first space for first/last-name dual-field forms.
                string first = preferred, last = "";
                int sp = preferred.IndexOf(' ');
                if (sp > 0) { first = preferred.Substring(0, sp); last = preferred.Substring(sp + 1); }

                int filled = 0;
                for (int i = 0; i < fields.Length && filled < 2; i++)
                {
                    var f = fields[i] as TMP_InputField;
                    if (f == null) continue;
                    // Only fill if the field is currently empty / placeholder — never clobber typed text.
                    var cur = f.text;
                    if (!string.IsNullOrWhiteSpace(cur)) continue;
                    string fill = filled == 0 ? first : last;
                    if (string.IsNullOrEmpty(fill)) { filled++; continue; }
                    f.text = fill;
                    Plugin.Logger.LogInfo($"[IntroName] pre-filled field[{i}] '{f.gameObject.name}' ← '{fill}'");
                    filled++;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[IntroName] prefill: {ex.Message}"); }
        }

        // (Legacy diagnostic F-key suite REMOVED 2026-06-10 — F3 exit-bandaid
        //  toggle, F4 master kill switch, F5/F6 parked-vehicle toggles, F11
        //  traffic suppression.  All were enter/exit-investigation tooling;
        //  the bandaid is retired and no test toggles ship with the mod.)

        // ── Backlog #6 fix: keep 'BlackOverlay' canvas suppressed on client ──
        //
        // OBSERVED 2026-05-20 MARK_BEFORE vs MARK_BLACK diff: a Canvas named
        // 'BlackOverlay' (mode=ScreenSpaceOverlay, sort=3) is enabled only in
        // the broken state.  In SP and on host the game disables it after the
        // building-entry transition completes; on the client the disable step
        // never runs, leaving an opaque black overlay covering the world.
        // BAMP_Canvas (sort=999) is above it, which is why the F8 panel stays
        // visible.
        //
        // Fix: cache the canvas on first sighting, then disable it whenever
        // it's enabled.  Cheap (~one assignment per frame in the worst case);
        // disabling the Canvas component preserves the GameObject so the
        // game's own state machine isn't disturbed.
        private static Canvas? _blackOverlayCanvas;
        private static float _blackOverlayFindTimer;
        // The BlackOverlay only gets stuck-on right after a (broken) client building-entry transition. Without a
        // stop condition the uncached scan below walked the whole object table every 2s FOREVER on a normal
        // client (anti-pattern Class 1). Arm a short scan window on each building entry instead; idle otherwise.
        private static int _blackOverlayScanTriesLeft;
        internal static void ArmBlackOverlayScan() => _blackOverlayScanTriesLeft = 4;  // ~8s of 2s-throttled scans post-entry
        private void TickSuppressBlackOverlay()
        {
            if (!MPClient.IsConnected) return;
            if (!IsInGame()) return;

            // If we've lost the cached reference (Unity destroyed the canvas
            // on scene change, etc.), rescan periodically.
            if (_blackOverlayCanvas == null)
            {
                // Only scan within a window armed by a building entry — no entry, no forever-poll.
                if (_blackOverlayScanTriesLeft <= 0) return;
                _blackOverlayFindTimer -= Time.unscaledDeltaTime;
                if (_blackOverlayFindTimer > 0f) return;
                _blackOverlayFindTimer = 2f;
                _blackOverlayScanTriesLeft--;

                try
                {
                    var canvases = UnityEngine.Object.FindObjectsOfType(typeof(Canvas));
                    if (canvases == null) return;
                    for (int i = 0; i < canvases.Length; i++)
                    {
                        var c = canvases[i] as Canvas;
                        if (c == null) continue;
                        if (c.gameObject.name == "BlackOverlay")
                        {
                            _blackOverlayCanvas = c;
                            Plugin.Logger.LogInfo("[ClientFix] Found BlackOverlay canvas; will keep disabled on client.");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[ClientFix] BlackOverlay search: {ex.Message}");
                }
                return;
            }

            // Cached — re-disable if the game turned it back on.
            try
            {
                if (_blackOverlayCanvas != null && _blackOverlayCanvas.enabled)
                    _blackOverlayCanvas.enabled = false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ClientFix] BlackOverlay suppress: {ex.Message}");
                _blackOverlayCanvas = null;  // rescan next pass
            }
        }

        private void TickPositionSync()
        {
            if (!IsInGame()) return;
            // Everything below is multiplayer machinery (sync ticks + MP UI ticks).
            // It must NOT run in single-player — it was leaking ~12ms/frame with
            // 100ms+ spikes into SP (perf log 2026-06-14).  One gate covers it all.
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;

            // (Discovery-probe block removed 2026-06-12 dead-code sweep: all
            //  verified log-only with missions complete — see .modding/04-probes.md.)
            long _pt = MPPerf.Begin(); ParkedVehicleSync.Tick();    MPPerf.End("Parked",   _pt);   // backlog #3 phase 3a — host capture
            _pt = MPPerf.Begin(); BusinessSync.Tick();         MPPerf.End("BizHost",  _pt);   // host change detection (time-boxed sweep)
            _pt = MPPerf.Begin(); BusinessSync.TickClient();   MPPerf.End("BizClient",_pt);   // client pushes own businesses up
            _pt = MPPerf.Begin(); MPPriceSync.Tick();          MPPerf.End("Price",    _pt);   // live retail prices of own businesses (both roles)
            _pt = MPPerf.Begin(); MPStockSync.Tick();          MPPerf.End("Stock",    _pt);   // own shops' stocked-shelf digest (so un-entered shops aren't judged "full")
            _pt = MPPerf.Begin(); MPAudit.Tick();              MPPerf.End("Audit",    _pt);   // client → host state-hash audit (30s); host logs [Audit] MISMATCH on divergence (Release-enabled 2026-06-24 for bug-report evidence)
            _pt = MPPerf.Begin(); MPRestSync.Tick();           MPPerf.End("Rest",     _pt);   // votes, seated state, watchdog (0.5s)
            MPRestSync.TickSkipFrame();   // per-frame skip executor (host + clients) — drives the sim fast to the goal
            MPHub.HostTick();             // loan ledger: daily interest/payment drafts
            MPServer.TickFencePrune();    // excuse menu-bailed clients from the load fence
            TickRestBanner();
            TickRestUI();
            MPHubNativePage.Tick();       // "Business" in the native full menu
            // (spawn sidestep moved to the WorldReady event — OnLifecyclePhase)
            TickJoinPopup();              // host approval panel for mid-game joiners
            TickTrainingPopup();          // honorary-degree confirm dialog (school doors)
            TickHubWindow();
            _pt = MPPerf.Begin(); InteriorSync.Tick();         MPPerf.End("Interior", _pt);   // diff-push to subscribed clients
            _pt = MPPerf.Begin(); InteriorSync.TickClientOwner(); MPPerf.End("InteriorOwner", _pt);   // owner pushes their own shop interior to host
            InteriorSync.TickDirtWatch();   // [DirtWatch] diagnostic — does a shop's dirt ever go down, or only up? (throttled 60s, anomaly-gated, removable)
            _pt = MPPerf.Begin(); GameStatePatcher.DrainPendingLogoRefreshes(); MPPerf.End("LogoRefresh", _pt);

            // Send our character appearance once the character is ready.
            TrySendLocalAppearance();

            if (!MPServer.IsRunning && !MPClient.IsConnected)
            {
                // RideProbe ground-truth capture works in single-player too.
                return;
            }

            // Smooth remote vehicles toward their networked transform (every frame).
            _pt = MPPerf.Begin(); VehicleManager.TickSmoothing(); MPPerf.End("Smooth", _pt);

            // Traffic sync foundation (host enumerates / client disables local traffic).
            _pt = MPPerf.Begin(); TrafficSync.Tick(); MPPerf.End("Traffic", _pt);

            _posSyncTimer += Time.deltaTime;
            if (_posSyncTimer < 0.1f) return; // ~10 Hz
            _posSyncTimer = 0f;

            try
            {
                var pc = PlayerHelper.PlayerController;
                if (pc == null) return;

                var pos  = PlayerHelper.GetPosition();
                float rotY = pc.Character != null
                    ? pc.Character.transform.eulerAngles.y
                    : 0f;

                var payload = new PlayerPositionPayload
                {
                    PlayerId = MPConfig.PlayerId,
                    X = pos.x, Y = pos.y, Z = pos.z,
                    RotY = rotY,
                    T = Time.unscaledTime,
                    Bldg = MPRegisterSync.CurrentShopAddress   // "" outdoors; cross-interior mask
                };
                RemotePlayerManager.ReadLocalAnimState(payload);
                MPHandIk.FillPayload(payload);   // hand-IK mirror while pushing

                if (MPServer.IsRunning)
                    MPServer.BroadcastPlayerPosition(payload);
                else if (MPClient.IsConnected)
                    MPClient.SendPlayerPosition(payload);

                // Vehicle fleet — every owned vehicle (parked + driven).
                var vp = VehicleManager.ReadLocalFleet();
                if (vp != null)
                {
                    if (MPServer.IsRunning)        MPServer.BroadcastVehicleSync(vp);
                    else if (MPClient.IsConnected) MPClient.SendVehicleSync(vp);
                }
                VehicleManager.TickDriveSync();   // handoff: broadcast the pose of a borrowed car I'm driving
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[UI] TickPositionSync error: {ex.Message}");
            }
        }

        // ── Periodic host sync ticks ──────────────────────────────────────────

        /// <summary>
        /// Host broadcasts current day/time/speed every 3 seconds as a drift-alignment heartbeat.
        /// This is a safety net — with timeScale sync, drift should be sub-second.
        /// The first broadcast fires 5 s after entering the game (set in TickGameLoadDetect).
        /// </summary>
        private void TickTimeSync()
        {
            if (!IsInGame() || !MPServer.IsRunning) return;

            _timeSyncTimer -= Time.unscaledDeltaTime; // use unscaled so pauses don't delay it
            if (_timeSyncTimer > 0f) return;
            _timeSyncTimer = 3f;

            try { MPServer.BroadcastGameTime(); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[UI] TickTimeSync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Host broadcasts market prices to all clients every ~60 seconds to prevent price divergence.
        /// </summary>
        private void TickMarketSync()
        {
            if (!IsInGame() || !MPServer.IsRunning) return;

            _marketSyncTimer -= Time.deltaTime;
            if (_marketSyncTimer > 0f) return;
            _marketSyncTimer = 60f;

            try
            {
                var json = GameStateReader.GetMarketEntriesJson();
                if (json != "[]")
                {
                    MPServer.BroadcastMarketSnapshot(json);
                    Plugin.Logger.LogInfo("[UI] Periodic market snapshot broadcast.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[UI] TickMarketSync error: {ex.Message}");
            }
        }

        /// <summary>
        /// The character portrait PNG/JPG is written to disk lazily — AFTER the
        /// initial profile broadcast — so that first send carries no portrait.
        /// Once the file appears, re-send the profile (with portrait + age) so
        /// remote players get the real face.  Sends once, then stops; gives up
        /// after a couple of minutes.  Resets when leaving the game.
        /// </summary>
        private void TickProfileResend()
        {
            bool connected = MPClient.IsConnected || MPServer.IsRunning;
            if (!IsInGame() || !connected)
            {
                // Reset on leave/disconnect so a RECONNECT re-sends the profile —
                // the reconnecting client's name map (and others' view of it) was
                // wiped by the world reload.
                GameStatePatcher.LocalPortraitSent = false;
                _profileNameConfirmed = false;
                _profileResendElapsed = 0f; _profileResendTimer = 5f; return;
            }
            // Done only once BOTH a real character name AND the portrait have gone out.
            if (GameStatePatcher.LocalPortraitSent && _profileNameConfirmed) return;

            _profileResendTimer -= Time.unscaledDeltaTime;
            _profileResendElapsed += Time.unscaledDeltaTime;
            if (_profileResendTimer > 0f) return;
            _profileResendTimer = 3f;
            if (_profileResendElapsed > 600f) { GameStatePatcher.LocalPortraitSent = true; _profileNameConfirmed = true; return; }   // give up after ~10 min

            try
            {
                // Decoupled name vs portrait (the old gate waited for the portrait,
                // so a late character name — player still in char-gen at first send —
                // never propagated and the PlayerId fallback stuck).  Re-send when
                // EITHER becomes newly available; stop when both are confirmed.
                bool nameReal      = MPNames.LocalCharacterName() != MPConfig.PlayerId;
                bool portraitReady = !string.IsNullOrEmpty(GameStatePatcher.ReadLocalPortraitBase64());
                bool nameNew     = nameReal      && !_profileNameConfirmed;
                bool portraitNew = portraitReady && !GameStatePatcher.LocalPortraitSent;
                if (!nameNew && !portraitNew) return;   // nothing new yet — wait, don't spam

                if (MPClient.IsConnected) MPClient.SendPlayerProfile();
                else if (MPServer.IsRunning) MPServer.BroadcastHostProfile();
                if (nameReal) _profileNameConfirmed = true;
                Plugin.Logger.LogInfo($"[UI] Re-sent player profile (nameReady={nameReal}, portrait={portraitReady}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UI] TickProfileResend: {ex.Message}"); }
        }

        // ── Phase 4: coordinated MP save ─────────────────────────────────────────

        private float _autosaveTimer = -1f;   // <0 = uninitialised (don't fire on entry)

        /// <summary>While in an MP session: suppress the game's own (SP-folder)
        /// autosave, ship any finished client save up to the host, and — on the
        /// host — drive the coordinated autosave on the SP-mirrored cadence.</summary>
        private void TickMpSave()
        {
            // SessionEnded counts as "in MP": the connection is gone but the MP
            // character is still loaded — native autosave must STAY suppressed or
            // it would write the MP character into the single-player folder.
            bool inMp = (MPServer.IsRunning || MPClient.IsConnected || MPClient.SessionEnded) && IsInGame();
            if (!inMp) { _autosaveTimer = -1f; return; }

            // (Version folder is cached unconditionally at the top of Update so the
            //  poll thread never calls IL2CPP via MpCharacterFolder.)

            // The host-coordinated save replaces the native one (which would write
            // to the single-player folder, uncoordinated + visible in the SP menu).
            MPSaveCoordinator.SuppressNativeAutosave();

            // Client side: ship a just-finished local save to the host.
            MPSaveCoordinator.TickPendingUploads();

            // Overlay any host-restored cash once a freshly-loaded game has settled.
            MPSaveCoordinator.TickCashApply();

            if (!MPServer.IsRunning) return;   // only the host schedules autosaves
            if (_autosaveTimer < 0f) { _autosaveTimer = MPSaveCoordinator.AutosaveIntervalSeconds(); return; }
            _autosaveTimer -= Time.unscaledDeltaTime;
            if (_autosaveTimer > 0f) return;
            _autosaveTimer = MPSaveCoordinator.AutosaveIntervalSeconds();
            try { MPSaveCoordinator.HostSaveNow("autosave"); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UI] TickMpSave autosave: {ex.Message}"); }
        }

        // ── Phase 4: live cash stream (loss-minimization) ────────────────────────

        private float _cashSyncTimer;
        private float _lastCash = float.NaN;

        /// <summary>Reports the local player's current money to the host every few
        /// seconds (on change), so the host always holds a near-current cash figure
        /// to restore on reconnect — a crash then costs at most a few seconds of
        /// earnings rather than everything since the last autosave.</summary>
        private void TickCashSync()
        {
            bool inMp = (MPServer.IsRunning || MPClient.IsConnected) && IsInGame();
            if (!inMp) { _lastCash = float.NaN; return; }
            _cashSyncTimer -= Time.unscaledDeltaTime;
            if (_cashSyncTimer > 0f) return;
            _cashSyncTimer = 3f;

            float money;
            try { var gi = SaveGameManager.Current; if (gi == null) return; money = gi.Money; }
            catch { return; }
            if (!float.IsNaN(_lastCash) && Math.Abs(money - _lastCash) < 0.005f) return;   // unchanged
            _lastCash = money;

            if (MPServer.IsRunning)        MPServer.RecordCash(MPConfig.PlayerId, money);   // host records its own
            else if (MPClient.IsConnected) MPClient.SendCashSync(money);
        }

        // ── Phase 5: native main-menu integration ───────────────────────────────
        // Inject a "Multiplayer" button into the game's own main menu, cloned from a
        // real menu button so it matches the game's styling — the native entry point
        // that replaces the floating F8 panel as the way in.

        private enum MpView { Main, Submenu, Lobby }
        private MpView _view = MpView.Main;
        private bool  _mpMenuInjected;
        private float _menuCheckTimer;
        private GameObject? _mpButton;                       // the purple "Multiplayer" entry
        private readonly List<GameObject> _origMenuButtons = new();   // the game's normal buttons (hidden in MP view)
        private readonly List<GameObject> _mpSubmenu       = new();   // Host New / Host Saved / Join / Back
        private readonly List<GameObject> _mpLobby         = new();   // lobby view: status + Start + Leave
        private TMP_Text?  _lobbyText;       // status + player list
        private GameObject? _lobbyStartGO;   // host-only "Start Game"
        private bool        _lobbyLoadMode;  // true = Host Saved (load), false = Host New
        /// <summary>True once the native font/sprite have been captured from the main
        /// menu.  The autopilot waits for this before auto-hosting so launcher-driven
        /// runs style their UI identically to manual play.</summary>
        internal static bool ThemeReady;

        private TMP_FontAsset? _gameFont;    // captured from a menu button so our windows look native
        private float _menuFontSize = 15f;   // the menu buttons' text size, so our window buttons match
        private Button? _menuTemplate;       // a real menu button, for cloning native-styled buttons into our windows
        private Sprite? _panelSprite;        // the menu button's rounded sprite, reused for rounded window backgrounds

        // ── Connect (Join) dialog ───────────────────────────────────────────────
        private GameObject? _joinDialog;
        private TextMeshProUGUI? _joinIpLbl, _joinPortLbl;
        private RectTransform? _joinIpRT, _joinPortRT;
        private RectTransform? _joinConnectRT, _joinBackRT;
        private int    _joinFocus;                       // 0=none, 1=ip, 2=port
        private string _joinIp   = "127.0.0.1";
        private string _joinPort = "7777";

        // ── Lobby window ─────────────────────────────────────────────────────────
        private GameObject? _lobbyWindow;
        private TextMeshProUGUI? _lwConnInfo;
        private TextMeshProUGUI[] _lwRoster = new TextMeshProUGUI[6];
        private GameObject? _lwDiffEasy, _lwDiffNormal, _lwDiffHard, _lwCustomize, _lwStart;
        private RectTransform? _rtDiffEasy, _rtDiffNormal, _rtDiffHard, _rtCustomize, _rtLwStart, _rtLwLeave;
        // Per-player roster columns.  Cash = host-dictated (host edits any row);
        // Age = self-edited (each player edits only their own row).  This is the
        // ONLY starting-cash UI now — the old base field + customize-page cash are
        // gone (the difficulty preset still provides each row's default cash).
        private TextMeshProUGUI[] _lwRowCashLbl = new TextMeshProUGUI[6];
        private RectTransform?[]  _rtLwRowCash  = new RectTransform?[6];
        private string[]          _lwRowCash    = new string[6];
        private int               _lwRowCashFocus = -1;
        private TextMeshProUGUI[] _lwRowAgeLbl  = new TextMeshProUGUI[6];
        private RectTransform?[]  _rtLwRowAge   = new RectTransform?[6];
        private RectTransform?[]  _rtLwKick     = new RectTransform?[6];   // host kick [X] per roster row
        private string[]          _lwRowAge     = new string[6];
        private int               _lwRowAgeFocus = -1;
        private TextMeshProUGUI?  _lwRowCashHdr; private TextMeshProUGUI? _lwRowAgeHdr; private TextMeshProUGUI? _lwDiffHdr;
        private GameObject? _lwShowIp; private RectTransform? _rtShowIp;
        private bool _showIp;   // IP hidden by default (streamer-safe); toggle to reveal

        // ── "Host Saved Game" save picker — grouped by playthrough, scrollable ──
        private GameObject? _savePicker;
        private TextMeshProUGUI? _spInfo;
        private RectTransform? _rtSpLoad, _rtSpBack, _rtSpRecov;
        private RectTransform? _spViewport, _spContent, _spTrack, _spThumb;
        private float  _spScroll, _spContentH;
        private string _spExpanded   = "";   // base name of the expanded playthrough (accordion)
        private string _spSelSession = "";   // session selected to host
        private bool   _spShowRecovery;      // native-parity: automatic saves hidden behind a toggle,
                                             // like vanilla's "show recover saves" (2026-07-07)
        // Plain ref type (NOT a [Serializable] ValueTuple) — a List<ValueTuple<RectTransform,string>>
        // field on a MonoBehaviour trips Unity's type processing at load and the whole mod assembly
        // fails to load.  A non-serializable class field is skipped cleanly.
        private sealed class SpHit { public readonly RectTransform Rt; public readonly string Key; public SpHit(RectTransform rt, string key) { Rt = rt; Key = key; } }
        private readonly System.Collections.Generic.List<SpHit> _spHeaderHits = new();
        private readonly System.Collections.Generic.List<SpHit> _spVarHits   = new();
        private TextMeshProUGUI? _lwLoadInfo;   // "Resuming save: …" line in the lobby (load mode)

        private static readonly Color MpPurple = new Color(0.64f, 0.36f, 0.95f, 1f);   // brighter, more saturated violet

        private float _themeScanTimer;

        /// <summary>Frontloads the native look: grabs the game's font + a rounded
        /// 9-sliced sprite from ANY loaded UI, as early as possible and independent of
        /// the menu injection / what the player clicks.  Runs from startup until both
        /// are captured (then stops), so our windows (esp. the in-game F9 window) style
        /// themselves natively no matter how fast anyone — or the autopilot — proceeds.
        /// The menu injection later refines _panelSprite to the exact menu-button sprite.</summary>
        private void TickThemeCapture()
        {
            // Captured assets are game-owned and can be DESTROYED by scene/view
            // unloads (Addressables) — detect that (managed ref set but native
            // object dead), clear, and let the scan re-capture.  `is not null`
            // bypasses the overloaded ==, so this also fires when interop
            // null-equality already reports the object as gone.
            if (_gameFont is not null && !IsAlive(_gameFont))
            { _gameFont = null; Plugin.Logger.LogInfo("[MenuUI] Captured font was unloaded — recapturing."); }
            if (_panelSprite is not null && !IsAlive(_panelSprite))
            { _panelSprite = null; Plugin.Logger.LogInfo("[MenuUI] Captured sprite was unloaded — recapturing."); }

            if (_gameFont != null && _panelSprite != null) { ThemeReady = true; return; }
            // No scanning IN-GAME: the menu sources aren't there, and the scans
            // (FindObjectOfType/FindObjectsOfType) walk the object table — a
            // periodic main-thread hitch.  If assets died mid-game our windows
            // fall back to the owned sprite/default font until back at the menu.
            if (IsInGame()) return;
            _themeScanTimer -= Time.unscaledDeltaTime;
            if (_themeScanTimer > 0f) return;
            _themeScanTimer = 0.5f;
            try
            {
                // PREFERRED source: the main menu's own buttons (same template the
                // injection refinement uses).  The generic "first 9-sliced sprite
                // anywhere" scan below never completes at the menu (the menu sprites
                // carry no border data), so under the bat autopilot it used to fire
                // only IN-GAME — grabbing a random HUD sprite that left the F9
                // window transparent/unrounded.  Capturing from the menu button
                // directly completes within ~1s of the menu appearing instead.
                if (_gameFont == null || _panelSprite == null)
                {
                    var mmc = UnityEngine.Object.FindObjectOfType<MainMenuController>();
                    var startView = mmc != null ? mmc.startView : null;
                    if (startView != null)
                    {
                        foreach (var b in startView.GetComponentsInChildren<Button>(true))
                        {
                            if (b == null) continue;
                            if (_gameFont == null)
                            {
                                var t = b.GetComponentInChildren<TMP_Text>(true);
                                if (t != null && t.font != null) { _gameFont = t.font; if (t.fontSize > 4f) _menuFontSize = t.fontSize; }
                            }
                            if (_panelSprite == null)
                            {
                                var img = b.image ?? b.GetComponentInChildren<Image>(true);
                                if (img != null && img.sprite != null) _panelSprite = img.sprite;
                            }
                            if (_gameFont != null && _panelSprite != null) break;
                        }
                    }
                }

                // Fallbacks: any font is safe anywhere; a generic sprite is only
                // trustworthy OUTSIDE the game scene (in-game the first 9-sliced
                // sprite is some random HUD element — worse than no styling).
                if (_gameFont == null)
                {
                    foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>())
                        if (t != null && t.font != null) { _gameFont = t.font; break; }
                }
                if (_panelSprite == null && !IsInGame())
                {
                    foreach (var img in UnityEngine.Object.FindObjectsOfType<Image>())
                        if (img != null && img.sprite != null && img.sprite.border != Vector4.zero) { _panelSprite = img.sprite; break; }
                }
                if (_gameFont != null && _panelSprite != null)
                {
                    ThemeReady = true;
                    Plugin.Logger.LogInfo("[MenuUI] Theme frontloaded (font + rounded sprite captured eagerly).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] theme scan: {ex.Message}"); }
        }

        private void TickMenuIntegration()
        {
            _menuCheckTimer -= Time.unscaledDeltaTime;
            if (_menuCheckTimer > 0f) return;
            _menuCheckTimer = 0.5f;   // 2×/sec at the MENU — keeps the lobby list fresh

            // In-game there is no main menu — but FindObjectOfType still walks
            // the object table to prove it, twice a second, forever (a measured
            // periodic main-thread hitch = rhythmic car stutter).  Skip the
            // search entirely in-game; reset the injected flag so returning to
            // the menu re-injects.  MUST also hide our menu windows here: a
            // session LOAD jumps straight into the game (no intro scene), so
            // the mmc-null hide below never ran and the lobby window sat over
            // the loaded world on BOTH machines (2026-06-11).
            if (IsInGame())
            {
                _mpMenuInjected = false;
                if (_lobbyWindow != null && _lobbyWindow.activeSelf) { try { _lobbyWindow.SetActive(false); } catch { } }
                if (_joinDialog != null && _joinDialog.activeSelf)  { try { _joinDialog.SetActive(false);  } catch { } }
                if (_savePicker != null && _savePicker.activeSelf)  { try { _savePicker.SetActive(false);  } catch { } }
                _view = MpView.Main;
                return;
            }

            MainMenuController? mmc = null;
            try { mmc = UnityEngine.Object.FindObjectOfType<MainMenuController>(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] find MainMenuController: {ex.Message}"); }

            if (mmc == null)   // not on the menu → forget the (now-destroyed) UI so we re-inject next visit
            {
                _mpMenuInjected = false; _mpButton = null; _lobbyText = null; _view = MpView.Main;
                _origMenuButtons.Clear(); _mpSubmenu.Clear(); _mpLobby.Clear(); _lwRowCashFocus = -1; _lwRowAgeFocus = -1;
                if (_joinDialog != null)  { try { _joinDialog.SetActive(false);  } catch { } }
                if (_lobbyWindow != null) { try { _lobbyWindow.SetActive(false); } catch { } }
                if (_savePicker != null)  { try { _savePicker.SetActive(false);  } catch { } }
                return;
            }

            if (!_mpMenuInjected)
            {
                _mpMenuInjected = true;   // attempt once per menu visit (success or fail) — no spam
                try { InjectMultiplayerMenu(mmc); }
                catch (Exception ex) { Plugin.Logger.LogError($"[MenuUI] InjectMultiplayerMenu: {ex}"); }
            }
            else if (_view == MpView.Lobby)
            {
                RefreshLobbyWindow();   // keep the player list current while in the lobby
            }
        }

        private void InjectMultiplayerMenu(MainMenuController mmc)
        {
            var startView = mmc.startView;
            if (startView == null) { Plugin.Logger.LogWarning("[MenuUI] startView is null."); return; }

            // Find the main button column by locating LoadGameButton — its parent is
            // the container the menu buttons live in.
            var buttons = startView.GetComponentsInChildren<Button>(true);
            Button? template = null;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].gameObject.name == "LoadGameButton") { template = buttons[i]; break; }
            }
            if (template == null) { Plugin.Logger.LogWarning("[MenuUI] LoadGameButton not found."); return; }

            // Capture the game's font + a rounded button sprite so our custom windows
            // (connect dialog, lobby) render with native styling.
            try { var t = template.GetComponentInChildren<TMP_Text>(true); if (t != null) { _gameFont = t.font; if (t.fontSize > 4f) _menuFontSize = t.fontSize; } } catch { }
            _menuTemplate = template;
            try { var img = template.image ?? template.GetComponentInChildren<Image>(true); if (img != null && img.sprite != null) _panelSprite = img.sprite; } catch { }
            // Signal that the native font/sprite are captured.  The autopilot waits for
            // this before auto-hosting so bat-launched runs go through the same theme-
            // capture path as manual play (otherwise the F9 window etc. stay unstyled).
            if (_gameFont != null || _panelSprite != null) ThemeReady = true;

            var container = template.transform.parent;

            // Capture the game's normal column buttons (siblings of LoadGameButton) so
            // we can hide/restore them when toggling the MP submenu.
            _origMenuButtons.Clear();
            for (int i = 0; i < container.childCount; i++)
            {
                var ch = container.GetChild(i).gameObject;
                if (ch.GetComponent<Button>() != null) _origMenuButtons.Add(ch);
            }

            // The purple "Multiplayer" entry, placed right after Load Game.
            _mpButton = CloneMenuButton(template, "BAMP_Multiplayer", "Multiplayer", OnMultiplayerClicked, MpPurple);
            _mpButton.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

            // The MP submenu (built hidden) — shown when Multiplayer is clicked.
            _mpSubmenu.Clear();
            // Submenu buttons keep the normal gray styling (only the top-level
            // Multiplayer entry is purple).
            _mpSubmenu.Add(CloneMenuButton(template, "BAMP_HostNew",   "Host New Game",   OnMpHostNew,   null));
            _mpSubmenu.Add(CloneMenuButton(template, "BAMP_HostSaved", "Host Saved Game", OnMpHostSaved, null));
            _mpSubmenu.Add(CloneMenuButton(template, "BAMP_Join",      "Join Game",       OnMpJoin,      null));
            _mpSubmenu.Add(CloneMenuButton(template, "BAMP_Back",      "Back",            OnMpBack,      null));
            foreach (var b in _mpSubmenu) b.SetActive(false);

            // (The lobby is a proper window built lazily under our canvas — see
            //  BuildLobbyWindow — not a button-column view.)

            _view = MpView.Main;
            Plugin.Logger.LogInfo($"[MenuUI] Injected Multiplayer menu ({_origMenuButtons.Count} original buttons captured).");
        }

        /// <summary>Clone a real menu button so it matches the game's styling, set its
        /// label + click action, optional tint.  Returns the clone GameObject.</summary>
        private GameObject CloneMenuButton(Button template, string name, string label, Action onClick, Color? tint)
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            clone.name = name;
            clone.SetActive(true);

            // Label — set TMP text and disable any localization component that would
            // otherwise revert it to the original key.
            try
            {
                var mbs = clone.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = 0; i < mbs.Length; i++)
                {
                    var c = mbs[i];
                    try { if (c != null && c.GetType().Name.Contains("Localization")) c.enabled = false; } catch { }
                }
                var tmp = clone.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null) tmp.text = label;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] label '{label}': {ex.Message}"); }

            // Tint
            if (tint.HasValue)
            {
                try { var b = clone.GetComponent<Button>(); if (b != null && b.targetGraphic != null) b.targetGraphic.color = tint.Value; }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] tint '{label}': {ex.Message}"); }
            }

            // Click — replace the whole event so the original prefab's persistent
            // listeners (e.g. open Load Game) don't also fire.  Force interactable:
            // the LoadGameButton template is disabled by the game when the player has
            // no saved games (MainMenuController), and an Instantiate clone inherits
            // that darkened, un-clickable state — which dead-clicked the Multiplayer
            // button for any brand-new player.  This is our own button; keep it live.
            var btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                btn.interactable = true;
                btn.enabled = true;
                btn.onClick = new Button.ButtonClickedEvent();
                btn.onClick.AddListener(new UnityEngine.Events.UnityAction(onClick.Invoke));
            }
            return clone;
        }

        /// <summary>Clone a menu button but make it a non-interactive label (keeps
        /// the game's font/background, no click).  Used for the lobby status line.</summary>
        private GameObject CloneMenuLabel(Button template, string name, string text)
        {
            var go = CloneMenuButton(template, name, text, () => { }, null);
            try { var b = go.GetComponent<Button>(); if (b != null) { b.interactable = false; b.enabled = false; } } catch { }
            return go;
        }

        /// <summary>Clone a real menu button into any parent at a fixed position/size
        /// (native rounded styling + game font).  Click works via onClick where a
        /// raycaster exists, and via RectHit in our window ticks regardless.</summary>
        private GameObject? CloneButtonInto(Transform parent, string name, string label, Action onClick, float x, float y, float w, float h)
        {
            if (_menuTemplate == null) return null;
            var clone = UnityEngine.Object.Instantiate(_menuTemplate.gameObject, parent);
            clone.name = name;
            clone.SetActive(true);
            SetAnchored(clone.GetComponent<RectTransform>(), x, y, w, h);
            try
            {
                var mbs = clone.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = 0; i < mbs.Length; i++) { var c = mbs[i]; try { if (c != null && c.GetType().Name.Contains("Localization")) c.enabled = false; } catch { } }
                var tmp = clone.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null)
                {
                    tmp.text = label;
                    // The clone was resized to our window's button size; force the label
                    // to fill + center so it's always visible (the original layout was
                    // sized for the much larger menu button → text ended up off/clipped).
                    var trt = tmp.rectTransform;
                    trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                    trt.offsetMin = new Vector2(6f, 0f); trt.offsetMax = new Vector2(-6f, 0f);
                    tmp.alignment = TextAlignmentOptions.Center;
                    // Use the menu's own button font size so our window buttons match the
                    // menu buttons (HOST NEW GAME) instead of auto-filling (which rendered
                    // too big).  Fixed size, no auto-fill.
                    tmp.enableAutoSizing = false;
                    tmp.fontSize = UnityEngine.Mathf.Clamp(_menuFontSize, 10f, 17f);
                    tmp.overflowMode = TextOverflowModes.Overflow;
                    if (tmp.gameObject != clone) tmp.gameObject.SetActive(true);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] CloneButtonInto '{label}': {ex.Message}"); }
            try { var btn = clone.GetComponent<Button>(); if (btn != null) { btn.interactable = true; btn.enabled = true; btn.onClick = new Button.ButtonClickedEvent(); btn.onClick.AddListener(new UnityEngine.Events.UnityAction(onClick.Invoke)); } } catch { }
            return clone;
        }

        private void ShowView(MpView v)
        {
            _view = v;
            bool main = v == MpView.Main, sub = v == MpView.Submenu, lob = v == MpView.Lobby;
            foreach (var o in _origMenuButtons) { try { o.SetActive(main); } catch { } }
            if (_mpButton != null) { try { _mpButton.SetActive(main); } catch { } }
            foreach (var m in _mpSubmenu) { try { m.SetActive(sub); } catch { } }
            if (lob && _lobbyWindow == null) BuildLobbyWindow();
            if (_lobbyWindow != null) { try { _lobbyWindow.SetActive(lob); } catch { } }
            if (lob) RefreshLobbyWindow();
        }

        private void OnMultiplayerClicked() { Plugin.Logger.LogInfo("[MenuUI] Multiplayer → submenu"); ShowView(MpView.Submenu); }
        private void OnMpBack()             { Plugin.Logger.LogInfo("[MenuUI] Back → main menu");   ShowView(MpView.Main); }

        private void OnMpHostNew()
        {
            Plugin.Logger.LogInfo("[MenuUI] Host New Game → hosting");
            _lobbyLoadMode = false;
            try { OnHost(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] host: {ex.Message}"); }
            ShowView(MpView.Lobby);
        }

        private void OnMpHostSaved()
        {
            Plugin.Logger.LogInfo("[MenuUI] Host Saved Game → save picker");
            ShowSavePicker(true);   // choose WHICH save first (local disk; no server needed yet)
        }

        // ── Save picker (lists MP sessions to choose which to host) ────────────

        private void ShowSavePicker(bool show)
        {
            if (show && _savePicker == null) BuildSavePicker();
            if (show) RefreshSavePicker();
            if (_savePicker != null) { try { _savePicker.SetActive(show); } catch { } }
        }

        private static Sprite? _triSprite;
        /// <summary>A small right-pointing white triangle sprite, generated once — the playthrough
        /// expand/collapse chevron (the game font lacks ▾/▸ glyphs, so a font glyph renders as "?").</summary>
        private static Sprite TriangleSprite()
        {
            if (_triSprite != null) return _triSprite;
            const int s = 32;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[s * s];
            float mid = (s - 1) / 2f;
            for (int yy = 0; yy < s; yy++)
                for (int xx = 0; xx < s; xx++)
                {
                    float t = (float)xx / (s - 1);          // 0 left .. 1 right
                    float halfH = (1f - t) * (s / 2f);      // base at the left edge, apex at the right
                    px[yy * s + xx] = (Mathf.Abs(yy - mid) <= halfH) ? new Color32(255, 255, 255, 255)
                                                                     : new Color32(255, 255, 255, 0);
                }
            tex.SetPixels32(px); tex.Apply();
            _triSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
            return _triSprite;
        }

        // ── Load-picker palette ──
        // Solid, PRE-COMPOSITED equivalents of the mockup's translucent layers.  The alpha versions
        // muddied/over-brightened in-game (glossy button sprite + alpha stacking), losing the layer
        // differentiation, so these flat colours reproduce the mockup's APPEARANCE exactly.
        private static readonly Color C_LD_PANEL = new Color(0.078f, 0.098f, 0.149f, 0.98f); // #141926 dialog (darkest)
        private static readonly Color C_LD_LIST  = new Color(0.133f, 0.165f, 0.247f, 1f);    // #222a3f list container
        private static readonly Color C_LD_CARD  = new Color(0.184f, 0.212f, 0.282f, 1f);    // #2f3648 playthrough card (lighter float)
        private static readonly Color C_LD_VAR   = new Color(0.149f, 0.169f, 0.227f, 1f);    // #262b3a variant row (recessed, darker)
        private static readonly Color C_LD_SEL   = new Color(0.192f, 0.278f, 0.400f, 1f);    // #314766 selected variant (muted blue)
        private static readonly Color C_LD_ACC   = new Color(0.373f, 0.627f, 0.902f, 1f);    // #5fa0e6 accent
        private static readonly Color C_LD_TXT   = new Color(0.933f, 0.945f, 0.965f, 1f);    // #eef1f6 text
        private static readonly Color C_LD_MUT   = new Color(0.545f, 0.592f, 0.643f, 1f);    // #8b97a4 muted
        private static readonly Color C_LD_SBTRK = new Color(0.208f, 0.227f, 0.275f, 1f);    // scrollbar track (subtle)
        private static readonly Color C_LD_SBTHB = new Color(0.49f,  0.506f, 0.525f, 0.90f); // #7d8186 scrollbar thumb

        /// <summary>Friendly playthrough title: the auto MP name "MP 2026-06-23 1614" → "MP Jun 23, 4:14 PM"
        /// (the time disambiguates same-day runs); custom names (hqtest, …) pass through unchanged.</summary>
        private static string FriendlyName(string b)
        {
            try {
                if (!string.IsNullOrEmpty(b) && b.StartsWith("MP ") && b.Length >= 18)
                {
                    var rest = b.Substring(3).Trim();   // "2026-06-23 1614"
                    if (DateTime.TryParseExact(rest, "yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture,
                                               System.Globalization.DateTimeStyles.None, out var dt))
                        return "MP " + dt.ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
                }
            } catch { }
            return b ?? "";
        }

        /// <summary>Recolour a cloned native button to an accent fill with a contrasting label (the mockup's
        /// primary "Host this save").  normalColor stays white so Image.color shows through.</summary>
        private static void TintButton(GameObject go, Color bg, Color fg)
        {
            if (go == null) return;
            try {
                var img = go.GetComponent<Image>() ?? go.GetComponentInChildren<Image>(true);
                if (img != null) img.color = bg;
                var btn = go.GetComponent<Button>();
                if (btn != null) { var cb = btn.colors; cb.normalColor = Color.white; cb.highlightedColor = new Color(1f, 1f, 1f, 0.92f); cb.pressedColor = new Color(0.88f, 0.88f, 0.88f, 1f); cb.selectedColor = Color.white; btn.colors = cb; }
                var txt = go.GetComponentInChildren<TMP_Text>(true); if (txt != null) txt.color = fg;
            } catch { }
        }

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> _iconCache = new();
        /// <summary>A small monochrome icon per variant kind, drawn as a sprite (the game font has no icon glyphs):
        /// Main=disk, Autosave=clock, Disconnect=X, Recover=alert triangle.  Caller tints via Image.color.</summary>
        private static Sprite IconSprite(string kind)
        {
            string key = kind ?? "";
            if (_iconCache.TryGetValue(key, out var cached)) return cached;
            const int s = 28;
            var px = new Color32[s * s];
            var ON = new Color32(255, 255, 255, 255); var OFF = new Color32(255, 255, 255, 0);
            float c = (s - 1) / 2f;
            for (int yy = 0; yy < s; yy++)
                for (int xx = 0; xx < s; xx++)
                {
                    float dx = xx - c, dy = yy - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    bool on;
                    switch (kind)
                    {
                        case "Autosave":   // clock: ring + minute/hour hands
                            on = (r <= c * 0.92f && r >= c * 0.60f)
                                 || (Mathf.Abs(dx) <= 1.3f && dy >= 0f && dy <= c * 0.62f)
                                 || (Mathf.Abs(dy) <= 1.3f && dx >= 0f && dx <= c * 0.42f);
                            break;
                        case "Disconnect": // X
                            on = (Mathf.Abs(dx - dy) <= 2.0f || Mathf.Abs(dx + dy) <= 2.0f) && r <= c * 0.95f;
                            break;
                        case "Recover":    // up-pointing alert triangle
                        {
                            float t = yy / (float)(s - 1);        // 0 bottom .. 1 top (texture y is up)
                            float half = (1f - t) * (c * 0.95f);  // wide base at bottom, apex at top
                            on = Mathf.Abs(dx) <= half && yy >= 3;
                            break;
                        }
                        case "Checkpoint": // bookmark: solid ribbon with a V-notch cut from the bottom
                        {
                            bool inRibbon = Mathf.Abs(dx) <= c * 0.50f && Mathf.Abs(dy) <= c * 0.88f;
                            bool notch    = yy < s * 0.34f && Mathf.Abs(dx) <= (s * 0.34f - yy) * 0.5f;
                            on = inRibbon && !notch;
                            break;
                        }
                        case "Auto checkpoint": // small clock ring + center dot (kin of Autosave, visibly lighter)
                            on = (r <= c * 0.88f && r >= c * 0.64f) || r <= c * 0.18f;
                            break;
                        default:           // "Main" — disk/floppy: rounded square, clipped top-right, label slot
                        {
                            float hs = c * 0.80f;
                            bool inSq = Mathf.Abs(dx) <= hs && Mathf.Abs(dy) <= hs;
                            bool notch = dx > hs - c * 0.36f && dy > hs - c * 0.36f;
                            on = inSq && !notch;
                            if (on && dy < -c * 0.10f && Mathf.Abs(dx) <= hs * 0.60f) on = false;
                            break;
                        }
                    }
                    px[yy * s + xx] = on ? ON : OFF;
                }
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px); tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
            _iconCache[key] = sp; return sp;
        }

        // ── Flat, rounded 9-slice sprite — gives FLAT colour fills with rounded corners (the mockup look).
        // The game's own button sprite has a gloss/gradient that muddied the flat shading. ──
        private static Sprite _roundSprite;
        private static Sprite RoundedRectSprite()
        {
            if (_roundSprite != null) return _roundSprite;
            const int s = 40; const float rad = 10f;
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = Mathf.Max(rad - x, x - (s - 1 - rad), 0f);
                    float dy = Mathf.Max(rad - y, y - (s - 1 - rad), 0f);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    byte a = (byte)(Mathf.Clamp01(rad - dist + 0.5f) * 255f);
                    px[y * s + x] = new Color32(255, 255, 255, a);
                }
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px); tex.Apply();
            _roundSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(rad, rad, rad, rad));
            return _roundSprite;
        }

        private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; var ap = p - a;
            float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / Mathf.Max(Vector2.Dot(ab, ab), 0.0001f));
            return Vector2.Distance(p, a + ab * t);
        }

        // Thin ">" chevron (matches the mockup's ti-chevron) — rotated to point down when expanded.
        private static Sprite _chevSprite;
        private static Sprite ChevronSprite()
        {
            if (_chevSprite != null) return _chevSprite;
            const int s = 32; const float th = 2.6f;
            var px = new Color32[s * s];
            Vector2 a1 = new Vector2(s * 0.36f, s * 0.24f), a2 = new Vector2(s * 0.36f, s * 0.76f), b = new Vector2(s * 0.66f, s * 0.5f);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    var p = new Vector2(x, y);
                    float d = Mathf.Min(SegDist(p, a1, b), SegDist(p, a2, b));
                    byte a = (byte)(Mathf.Clamp01(th - d + 0.5f) * 255f);
                    px[y * s + x] = new Color32(255, 255, 255, a);
                }
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px); tex.Apply();
            _chevSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
            return _chevSprite;
        }

        private static Image AddRoundedBG(GameObject go, Color c)
        {
            var img = go.AddComponent<Image>();
            img.sprite = RoundedRectSprite(); img.type = Image.Type.Sliced; img.color = c;
            return img;
        }

        private RectTransform MakeFlatButton(Transform parent, string name, string label, float x, float y, float w, float h, Color bg, Color fg, int fontSize, bool bold)
        {
            var go = MakeGO(name, parent); var rt = go.GetComponent<RectTransform>();
            SetAnchored(rt, x, y, w, h);
            AddRoundedBG(go, bg);
            var lbl = MakeLabel(go.transform, label, fontSize, fg, 0f, 0f, w, h, TextAlignmentOptions.Center);
            if (bold) lbl.fontStyle = FontStyles.Bold;
            ApplyFont(lbl);
            return rt;
        }

        private void BuildSavePicker()
        {
            if (_savePicker != null || _canvasGO == null) return;
            _savePicker = MakeGO("BAMP_SavePicker", _canvasGO.transform);
            var prt = _savePicker.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(640f, 540f);
            prt.anchoredPosition = Vector2.zero;
            prt.localScale = new Vector3(1.5f, 1.5f, 1f);   // 50% bigger all around (uniform — keeps layout + hit-testing correct)
            AddRoundedBG(_savePicker, C_LD_PANEL);

            // Title + subtitle — LEFT-aligned at the top-left (mockup), not centered.
            var spTitle = MakeLabel(_savePicker.transform, "Load multiplayer save", 19, C_LD_TXT, 20f, -16f, 600f, 26f, TextAlignmentOptions.Left);
            spTitle.fontStyle = FontStyles.Bold; ApplyFont(spTitle);
            _spInfo = MakeLabel(_savePicker.transform, "Choose a playthrough to host.", 12, C_LD_MUT, 20f, -44f, 600f, 18f, TextAlignmentOptions.Left); ApplyFont(_spInfo);

            // List container — FLAT #222a3f, rounded, clipped.
            var vp = MakeGO("SpViewport", _savePicker.transform);
            _spViewport = vp.GetComponent<RectTransform>();
            SetAnchored(_spViewport, 18f, -72f, 604f, 410f);
            AddRoundedBG(vp, C_LD_LIST);
            vp.AddComponent<RectMask2D>();

            var ct = MakeGO("SpContent", vp.transform);
            _spContent = ct.GetComponent<RectTransform>();
            SetAnchored(_spContent, 0f, 0f, 604f, 410f);

            // Scrollbar (thin flat pills).
            var tk = MakeGO("SpTrack", _savePicker.transform);
            _spTrack = tk.GetComponent<RectTransform>();
            SetAnchored(_spTrack, 609f, -80f, 6f, 394f);
            tk.AddComponent<Image>().color = C_LD_SBTRK;
            var th = MakeGO("SpThumb", tk.transform);
            _spThumb = th.GetComponent<RectTransform>();
            SetAnchored(_spThumb, 0f, 0f, 6f, 80f);
            th.AddComponent<Image>().color = C_LD_SBTHB;

            // Buttons — right-aligned at the bottom: [Back] [Host this save], Host rightmost (mockup).
            // Bottom-left: the recovery-saves toggle (native load screen hides "Recover" saves the same way).
            _rtSpLoad = MakeFlatButton(_savePicker.transform, "BAMP_SpLoad", "Host this save", 462f, -498f, 160f, 34f, C_LD_ACC, new Color(0.04f, 0.086f, 0.149f, 1f), 13, true);
            _rtSpBack = MakeFlatButton(_savePicker.transform, "BAMP_SpBack", "Back", 362f, -498f, 88f, 34f, new Color(0.196f, 0.227f, 0.298f, 1f), C_LD_MUT, 13, false);
            _rtSpRecov = MakeFlatButton(_savePicker.transform, "BAMP_SpRecov", "Show recovery saves", 8f, -498f, 190f, 34f, new Color(0.196f, 0.227f, 0.298f, 1f), C_LD_MUT, 13, false);
            _savePicker.SetActive(false);
        }

        private void RefreshSavePicker()
        {
            if (_spContent == null) return;
            _spHeaderHits.Clear(); _spVarHits.Clear();
            for (int i = _spContent.childCount - 1; i >= 0; i--)
            { try { var c = _spContent.GetChild(i).gameObject; c.SetActive(false); Destroy(c); } catch { } }

            List<MPSaveManager.MpPlaythrough> runs;
            try { runs = MPSaveManager.ListPlaythroughs(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] ListPlaythroughs: {ex.Message}"); runs = new(); }

            if (_spInfo != null)
                _spInfo.text = runs.Count == 0 ? "No multiplayer saves found."
                                               : "Choose a playthrough to host — expand to pick a save.";
            if (_rtSpRecov != null)
            {
                var t = _rtSpRecov.GetComponentInChildren<TextMeshProUGUI>(true);
                if (t != null) t.text = _spShowRecovery ? "Hide recovery saves" : "Show recovery saves";
            }

            if (runs.Count > 0)
            {
                if (string.IsNullOrEmpty(_spExpanded) || !runs.Exists(r => r.Base == _spExpanded)) _spExpanded = runs[0].Base;
                if (string.IsNullOrEmpty(_spSelSession) || !SessionListed(runs, _spSelSession)) _spSelSession = FirstVariantOf(runs, _spExpanded);
            }
            else { _spExpanded = ""; _spSelSession = ""; }

            const float CARD_X = 8f, CARD_W = 576f, HROW = 52f, VINDENT = 28f, VROW = 36f, VGAP = 5f, VPAD_TOP = 4f, VPAD_BOT = 8f, CARD_GAP = 8f;
            float rowW = CARD_W - VINDENT - 12f;
            float y = -8f;   // top inset inside the list
            foreach (var run in runs)
            {
                bool expanded = run.Base == _spExpanded;
                var vis = VisibleVariants(run);
                int nv = expanded ? vis.Count : 0;
                float cardH = HROW + (nv > 0 ? VPAD_TOP + nv * VROW + (nv - 1) * VGAP + VPAD_BOT : 0f);

                // Playthrough card — a FLAT, lighter float on the darker list (the mockup's hierarchy).
                var cardGO = MakeGO("SpCard", _spContent); var cardRT = cardGO.GetComponent<RectTransform>();
                SetAnchored(cardRT, CARD_X, y, CARD_W, cardH);
                AddRoundedBG(cardGO, C_LD_CARD);

                // Header — the click target that expands/collapses this playthrough.
                var hgo = MakeGO("SpHdr", cardGO.transform); var hrt = hgo.GetComponent<RectTransform>();
                SetAnchored(hrt, 0f, 0f, CARD_W, HROW);
                var chGO = MakeGO("SpChev", hgo.transform); var chRT = chGO.GetComponent<RectTransform>();
                chRT.anchorMin = chRT.anchorMax = new Vector2(0f, 1f); chRT.pivot = new Vector2(0.5f, 0.5f);
                chRT.sizeDelta = new Vector2(16f, 16f); chRT.anchoredPosition = new Vector2(22f, -HROW / 2f);
                chRT.localRotation = Quaternion.Euler(0f, 0f, expanded ? -90f : 0f);
                var chImg = chGO.AddComponent<Image>(); chImg.sprite = ChevronSprite(); chImg.color = C_LD_MUT;
                string players = (run.Players ?? "").Replace("—", "-"); if (string.IsNullOrEmpty(players)) players = "-";
                var hlbl = MakeLabel(hgo.transform,
                    $"<b>{FriendlyName(run.Base)}</b>\n<size=80%><color=#8B97A4>{players}</color></size>",
                    15, C_LD_TXT, 44f, 0f, CARD_W - 200f, HROW, TextAlignmentOptions.Left);
                ApplyFont(hlbl); hlbl.enableWordWrapping = false;
                string dayPart = run.NewestDay >= 1 ? $"Day {run.NewestDay} · " : "";
                ApplyFont(MakeLabel(hgo.transform, $"{dayPart}{vis.Count} save{(vis.Count == 1 ? "" : "s")}",
                    12, C_LD_MUT, CARD_W - 152f, 0f, 140f, HROW, TextAlignmentOptions.Right));
                _spHeaderHits.Add(new SpHit(hrt, run.Base));

                if (expanded)
                {
                    // Does this card hold more than one save NAME? (playthrough-id grouping can merge
                    // several named saves of one world — label rows by name where it's ambiguous)
                    var baseNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in run.Variants) baseNames.Add(MPSaveManager.StripToBase(v.SessionName));
                    bool multiName = baseNames.Count > 1;

                    float vy = HROW + VPAD_TOP;
                    foreach (var v in vis)
                    {
                        bool sel = v.SessionName == _spSelSession;
                        var vgo = MakeGO("SpVar", cardGO.transform); var vrt = vgo.GetComponent<RectTransform>();
                        SetAnchored(vrt, VINDENT, -vy, rowW, VROW);
                        AddRoundedBG(vgo, sel ? C_LD_SEL : C_LD_VAR);
                        if (sel)   // 3px accent bar on the selected row's left edge (mockup)
                        {
                            var barGO = MakeGO("SpSelBar", vgo.transform); var barRT = barGO.GetComponent<RectTransform>();
                            SetAnchored(barRT, 0f, 0f, 3f, VROW); barGO.AddComponent<Image>().color = C_LD_ACC;
                        }
                        var icGO = MakeGO("SpIcon", vgo.transform); var icRT = icGO.GetComponent<RectTransform>();
                        icRT.anchorMin = icRT.anchorMax = new Vector2(0f, 1f); icRT.pivot = new Vector2(0.5f, 0.5f);
                        icRT.sizeDelta = new Vector2(16f, 16f); icRT.anchoredPosition = new Vector2(18f, -VROW / 2f);
                        var icImg = icGO.AddComponent<Image>(); icImg.sprite = IconSprite(v.Kind); icImg.color = sel ? C_LD_ACC : C_LD_MUT;
                        ApplyFont(MakeLabel(vgo.transform, SpRowLabel(v, multiName), 14, C_LD_TXT, 38f, 0f, rowW - 210f, VROW, TextAlignmentOptions.Left));
                        ApplyFont(MakeLabel(vgo.transform, VariantMeta(v), 12, C_LD_MUT, rowW - 172f, 0f, 162f, VROW, TextAlignmentOptions.Right));
                        _spVarHits.Add(new SpHit(vrt, v.SessionName));
                        vy += VROW + VGAP;
                    }
                }
                y -= cardH + CARD_GAP;
            }

            _spContentH = -y + 8f;
            float vh = _spViewport != null ? _spViewport.rect.height : 410f;
            _spContent.sizeDelta = new Vector2(604f, Mathf.Max(_spContentH, vh));
            ClampScroll(); UpdateSpScrollVisual();
        }

        private static string VariantLabel(string kind)
        {
            if (kind == "Autosave")        return "Autosave";
            if (kind == "Disconnect")      return "Disconnect checkpoint";
            if (kind == "Recover")         return "Recover (crash)";
            if (kind == "Checkpoint")      return "Save point";        // legacy round-37 frozen copy (creation retired 2026-07-07)
            if (kind == "Auto checkpoint") return "Auto save point";   // legacy round-37 frozen copy (creation retired 2026-07-07)
            return "Main save";
        }

        /// <summary>Row label. On a single-name card, kinds label the rows as before. When
        /// playthrough-id grouping merges several save NAMES into one card, every row carries its
        /// save's name (native shows the typed name per save row), automatic rows kind-prefixed.</summary>
        private static string SpRowLabel(MPSaveManager.MpVariant v, bool multiName)
        {
            if (!multiName) return VariantLabel(v.Kind);
            string name = FriendlyName(MPSaveManager.StripToBase(v.SessionName));
            return v.Kind == "Main" ? name : VariantLabel(v.Kind) + " · " + name;
        }

        private static string VariantMeta(MPSaveManager.MpVariant v)
        {
            try {
                var lt = DateTimeOffset.FromUnixTimeSeconds(v.SavedAtUnix).LocalDateTime;
                return v.Day >= 1 ? $"Day {v.Day} · {lt.ToString("h:mm tt")}" : lt.ToString("MMM d, h:mm tt");
            } catch { return v.Day >= 1 ? $"Day {v.Day}" : ""; }
        }

        /// <summary>The variant rows the picker shows for a playthrough. With the recovery toggle
        /// off, automatic saves (autosave rotation / recover / disconnect — and legacy auto
        /// checkpoints) are hidden, mirroring the native load screen's collapsed "Recover" list.
        /// A lineage with ONLY recovery saves shows them regardless — it must stay hostable.</summary>
        private List<MPSaveManager.MpVariant> VisibleVariants(MPSaveManager.MpPlaythrough run)
        {
            if (_spShowRecovery) return run.Variants;
            var vis = run.Variants.FindAll(v => !IsRecoveryKind(v.Kind));
            return vis.Count > 0 ? vis : run.Variants;
        }

        private static bool IsRecoveryKind(string kind)
            => kind == "Autosave" || kind == "Auto checkpoint" || kind == "Disconnect" || kind == "Recover";

        private string FirstVariantOf(List<MPSaveManager.MpPlaythrough> runs, string baseName)
        {
            foreach (var r in runs) if (r.Base == baseName && r.Variants.Count > 0) return VisibleVariants(r)[0].SessionName;
            return runs.Count > 0 && runs[0].Variants.Count > 0 ? VisibleVariants(runs[0])[0].SessionName : "";
        }

        private bool SessionListed(List<MPSaveManager.MpPlaythrough> runs, string session)
        {
            foreach (var r in runs) foreach (var v in VisibleVariants(r)) if (v.SessionName == session) return true;
            return false;
        }

        private float MaxScroll() => Mathf.Max(0f, _spContentH - (_spViewport != null ? _spViewport.rect.height : 388f));
        private void  ClampScroll() => _spScroll = Mathf.Clamp(_spScroll, 0f, MaxScroll());

        private void UpdateSpScrollVisual()
        {
            if (_spContent != null) _spContent.anchoredPosition = new Vector2(0f, _spScroll);
            if (_spThumb == null) return;
            float vh  = _spViewport != null ? _spViewport.rect.height : 388f;
            float th  = _spTrack != null ? _spTrack.rect.height : 388f;
            float max = MaxScroll();
            if (_spContentH <= vh + 1f || max <= 0f) { _spThumb.gameObject.SetActive(false); return; }
            _spThumb.gameObject.SetActive(true);
            float thumbH = Mathf.Clamp(vh / _spContentH * th, 28f, th);
            SetAnchored(_spThumb, 0f, -(_spScroll / max) * (th - thumbH), 6f, thumbH);
        }

        private void TickSavePicker()
        {
            if (_savePicker == null || !_savePicker.activeSelf) return;
            var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            // Mouse-wheel scroll while the cursor is over the list.
            if (_spViewport != null && RectHit(_spViewport, mp))
            {
                float sw = Input.mouseScrollDelta.y;
                if (sw != 0f) { _spScroll = Mathf.Clamp(_spScroll - sw * 42f, 0f, MaxScroll()); UpdateSpScrollVisual(); }
            }

            if (!Input.GetMouseButtonDown(0)) return;
            if (RectHit(_rtSpBack, mp)) { OnSpBack(); return; }
            if (RectHit(_rtSpLoad, mp)) { OnSpLoad(); return; }
            if (RectHit(_rtSpRecov, mp)) { _spShowRecovery = !_spShowRecovery; RefreshSavePicker(); return; }
            if (_spViewport == null || !RectHit(_spViewport, mp)) return;   // list clicks only count inside the viewport
            foreach (var h in _spHeaderHits)
                if (RectHit(h.Rt, mp)) { _spExpanded = (_spExpanded == h.Key) ? "" : h.Key; RefreshSavePicker(); return; }
            foreach (var v in _spVarHits)
                if (RectHit(v.Rt, mp)) { _spSelSession = v.Key; RefreshSavePicker(); return; }
        }

        private void OnSpLoad()
        {
            if (string.IsNullOrEmpty(_spSelSession)) { SetStatus("Pick a save first.", true); return; }
            string name = _spSelSession;
            Plugin.Logger.LogInfo($"[MenuUI] Save picker → host session '{name}'.");
            ShowSavePicker(false);
            _lobbyLoadMode = true;
            try { OnHost(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] host: {ex.Message}"); }
            MPServer.ChosenLoadSession = name;   // set AFTER OnHost so Start() can't clear it
            ShowView(MpView.Lobby);
        }

        private void OnSpBack() { ShowSavePicker(false); ShowView(MpView.Submenu); }

        private void OnMpJoin()
        {
            Plugin.Logger.LogInfo("[MenuUI] Join Game → connect dialog");
            ShowJoinDialog(true);
        }

        // ── Connect (Join) dialog — a styled centered window for IP/port entry ────

        private void ApplyFont(TMP_Text? t) { if (t != null && IsAlive(_gameFont)) { try { t.font = _gameFont; } catch { } } }
        private void ApplyFontIn(GameObject go) { try { var t = go.GetComponentInChildren<TMP_Text>(true); ApplyFont(t); } catch { } }

        private void ShowJoinDialog(bool show)
        {
            if (show && _joinDialog == null) BuildJoinDialog();
            if (_joinDialog != null) { try { _joinDialog.SetActive(show); } catch { } }
            if (show) _joinFocus = 0;
        }

        private void BuildJoinDialog()
        {
            if (_joinDialog != null || _canvasGO == null) return;
            _joinIp = MPConfig.HostIP; _joinPort = MPConfig.Port.ToString();

            _joinDialog = MakeGO("BAMP_JoinDialog", _canvasGO.transform);
            var prt = _joinDialog.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(440f, 250f);
            prt.anchoredPosition = Vector2.zero;
            var bg = _joinDialog.AddComponent<Image>();
            bg.color = new Color(0.10f, 0.10f, 0.13f, 0.97f);
            if (_panelSprite != null) { try { bg.sprite = _panelSprite; bg.type = Image.Type.Sliced; } catch { } }   // rounded corners

            const float W = 440f;
            ApplyFont(MakeLabel(_joinDialog.transform, "Join Multiplayer", 26, C_WHITE, 0f, -14f, W, 36f, TextAlignmentOptions.Center));

            ApplyFont(MakeLabel(_joinDialog.transform, "Host IP", SZ_FLD, C_WHITE, 30f, -74f, 110f, 30f, TextAlignmentOptions.Left));
            var (_, ipLbl, ipRT) = MakeField(_joinDialog.transform, _joinIp, 150f, -74f, 260f, 30f);
            _joinIpLbl = ipLbl; _joinIpRT = ipRT; ApplyFont(ipLbl);

            ApplyFont(MakeLabel(_joinDialog.transform, "Port", SZ_FLD, C_WHITE, 30f, -114f, 110f, 30f, TextAlignmentOptions.Left));
            var (_, portLbl, portRT) = MakeField(_joinDialog.transform, _joinPort, 150f, -114f, 260f, 30f);
            _joinPortLbl = portLbl; _joinPortRT = portRT; ApplyFont(portLbl);

            // Native-styled buttons cloned from the menu (rounded + game font).
            _joinConnectRT = CloneButtonInto(_joinDialog.transform, "BAMP_JoinConnect", "Connect", OnJoinConnect, 50f,  -182f, 160f, 44f)?.GetComponent<RectTransform>();
            _joinBackRT    = CloneButtonInto(_joinDialog.transform, "BAMP_JoinBack",    "Back",    OnJoinBack,    230f, -182f, 160f, 44f)?.GetComponent<RectTransform>();

            _joinDialog.SetActive(false);
        }

        /// <summary>MAIN THREAD per frame while the dialog is open — click focus + typing.</summary>
        private void TickJoinDialog()
        {
            if (_joinDialog == null || !_joinDialog.activeSelf) return;
            var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            if (Input.GetMouseButtonDown(0))
            {
                if (RectHit(_joinConnectRT, mp)) { OnJoinConnect(); return; }
                if (RectHit(_joinBackRT,    mp)) { OnJoinBack();    return; }
                _joinFocus = RectHit(_joinIpRT, mp) ? 1 : RectHit(_joinPortRT, mp) ? 2 : 0;
            }

            // Ctrl+V paste — Input.inputString does not carry clipboard pastes, so the
            // box couldn't be pasted into.  Read the clipboard explicitly.
            if (_joinFocus != 0 && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.V))
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (char ch in GUIUtility.systemCopyBuffer ?? "") if (!char.IsControl(ch)) sb.Append(ch);
                    if (_joinFocus == 1) _joinIp += sb.ToString(); else _joinPort += sb.ToString();
                }
                catch { }
            }

            if (_joinFocus != 0 && Input.inputString.Length > 0)
            {
                string s = _joinFocus == 1 ? _joinIp : _joinPort;
                foreach (char c in Input.inputString)
                {
                    if (c == '\b') { if (s.Length > 0) s = s.Substring(0, s.Length - 1); }
                    else if (c == '\n' || c == '\r') { OnJoinConnect(); return; }
                    else if (!char.IsControl(c)) s += c;
                }
                if (_joinFocus == 1) _joinIp = s; else _joinPort = s;
            }

            // Blinking caret on the focused field — there was no cursor before, so it
            // wasn't clear typing was going anywhere.
            bool joinCaret = Mathf.FloorToInt(Time.unscaledTime * 2f) % 2 == 0;
            if (_joinIpLbl   != null) _joinIpLbl.text   = _joinIp   + (_joinFocus == 1 && joinCaret ? "|" : "");
            if (_joinPortLbl != null) _joinPortLbl.text = _joinPort + (_joinFocus == 2 && joinCaret ? "|" : "");
        }

        private void OnJoinConnect()
        {
            _ip = _joinIp.Trim(); _port = _joinPort.Trim();
            Plugin.Logger.LogInfo($"[MenuUI] Connect → {_ip}:{_port}");
            ShowJoinDialog(false);
            try { OnJoin(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] connect: {ex.Message}"); }
            ShowView(MpView.Lobby);
        }

        private void OnJoinBack() { Plugin.Logger.LogInfo("[MenuUI] Join dialog → back"); ShowJoinDialog(false); ShowView(MpView.Submenu); }

        private void OnLobbyStart()
        {
            Plugin.Logger.LogInfo($"[MenuUI] Lobby Start (load={_lobbyLoadMode})");
            try { if (_lobbyLoadMode) OnStartLoad(); else OnStartNew(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] start: {ex.Message}"); }
        }

        private void OnLobbyLeave()
        {
            Plugin.Logger.LogInfo("[MenuUI] Lobby Leave");
            try { if (MPServer.IsRunning) OnStop(); else OnDisc(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] leave: {ex.Message}"); }
            ShowView(MpView.Submenu);
        }

        // ── Lobby window — native styled window (roster + difficulty + start/leave) ──

        private static void SetActiveSafe(GameObject? go, bool a) { if (go != null) { try { go.SetActive(a); } catch { } } }

        private void BuildLobbyWindow()
        {
            if (_lobbyWindow != null || _canvasGO == null) return;
            _lobbyWindow = MakeGO("BAMP_LobbyWindow", _canvasGO.transform);
            var prt = _lobbyWindow.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(540f, 520f);
            prt.anchoredPosition = Vector2.zero;
            var bg = _lobbyWindow.AddComponent<Image>();
            bg.color = new Color(0.10f, 0.10f, 0.13f, 0.97f);
            if (_panelSprite != null) { try { bg.sprite = _panelSprite; bg.type = Image.Type.Sliced; } catch { } }

            var grey = new Color(0.70f, 0.70f, 0.75f, 1f);
            ApplyFont(MakeLabel(_lobbyWindow.transform, "Multiplayer Lobby", 26, C_WHITE, 0f, -16f, 540f, 36f, TextAlignmentOptions.Center));
            // Wide + two-line + wrapping: the kick/reject reason was clipped
            // by the old 350×28 single-line field (user, 2026-06-11).
            _lwConnInfo = MakeLabel(_lobbyWindow.transform, "", SZ_FLD, C_WHITE, 28f, -50f, 350f, 44f, TextAlignmentOptions.TopLeft); ApplyFont(_lwConnInfo);
            _lwConnInfo.enableWordWrapping = true;
            _lwConnInfo.fontSize = SZ_FLD - 2;
            _lwShowIp   = CloneButtonInto(_lobbyWindow.transform, "BAMP_ShowIp", "Show IP", OnToggleShowIp, 390f, -58f, 124f, 30f); _rtShowIp = _lwShowIp?.GetComponent<RectTransform>();
            // Roster header — Players | Starting $ (host-set) | Age (self-set).
            ApplyFont(MakeLabel(_lobbyWindow.transform, "Players", SZ_FLD, grey, 28f, -92f, 200f, 26f, TextAlignmentOptions.Left));
            _lwRowCashHdr = MakeLabel(_lobbyWindow.transform, "Starting $", SZ_LBL, grey, 300f, -92f, 110f, 26f, TextAlignmentOptions.Left); ApplyFont(_lwRowCashHdr);
            _lwRowAgeHdr  = MakeLabel(_lobbyWindow.transform, "Age",        SZ_LBL, grey, 430f, -92f, 80f,  26f, TextAlignmentOptions.Left); ApplyFont(_lwRowAgeHdr);
            for (int i = 0; i < _lwRoster.Length; i++)
            {
                float ry = -118f - i * 26f;
                ApplyFont(_lwRoster[i] = MakeLabel(_lobbyWindow.transform, "", SZ_FLD, C_WHITE, 44f, ry, 250f, 24f, TextAlignmentOptions.Left));
                // Cash field (host-dictated) + Age field (self-edited). Hidden until the row is occupied.
                var (_, rcLbl, rcRT) = MakeField(_lobbyWindow.transform, "", 300f, ry - 1f, 118f, 22f);
                ApplyFont(rcLbl); rcLbl.fontSize = SZ_LBL;
                _lwRowCashLbl[i] = rcLbl; _rtLwRowCash[i] = rcRT; _lwRowCash[i] = "";
                SetActiveSafe(rcRT != null ? rcRT.gameObject : null, false);
                var (_, raLbl, raRT) = MakeField(_lobbyWindow.transform, "", 430f, ry - 1f, 70f, 22f);
                ApplyFont(raLbl); raLbl.fontSize = SZ_LBL;
                _lwRowAgeLbl[i] = raLbl; _rtLwRowAge[i] = raRT; _lwRowAge[i] = "";
                SetActiveSafe(raRT != null ? raRT.gameObject : null, false);
            }

            // Shown only in load mode (in place of the new-game controls).
            _lwLoadInfo = MakeLabel(_lobbyWindow.transform, "", 18, C_YELLOW, 0f, -300f, 540f, 28f, TextAlignmentOptions.Center);
            ApplyFont(_lwLoadInfo); _lwLoadInfo.gameObject.SetActive(false);

            _lwDiffHdr = MakeLabel(_lobbyWindow.transform, "Difficulty", SZ_FLD, grey, 28f, -292f, 110f, 26f, TextAlignmentOptions.Left); ApplyFont(_lwDiffHdr);
            _lwDiffEasy   = CloneButtonInto(_lobbyWindow.transform, "BAMP_DiffEasy",   "Easy",   () => SetDifficulty("Easy"),   150f, -320f, 120f, 36f); _rtDiffEasy   = _lwDiffEasy?.GetComponent<RectTransform>();
            _lwDiffNormal = CloneButtonInto(_lobbyWindow.transform, "BAMP_DiffNormal", "Normal", () => SetDifficulty("Normal"), 278f, -320f, 120f, 36f); _rtDiffNormal = _lwDiffNormal?.GetComponent<RectTransform>();
            _lwDiffHard   = CloneButtonInto(_lobbyWindow.transform, "BAMP_DiffHard",   "Hard",   () => SetDifficulty("Hard"),   406f, -320f, 110f, 36f); _rtDiffHard   = _lwDiffHard?.GetComponent<RectTransform>();

            _lwCustomize = CloneButtonInto(_lobbyWindow.transform, "BAMP_Customize", "Customize", OnCustomize, 196f, -372f, 148f, 36f); _rtCustomize = _lwCustomize?.GetComponent<RectTransform>();

            _lwStart   = CloneButtonInto(_lobbyWindow.transform, "BAMP_LwStart", "Start Game", OnLobbyStart, 110f, -456f, 160f, 42f); _rtLwStart = _lwStart?.GetComponent<RectTransform>();
            var leave  = CloneButtonInto(_lobbyWindow.transform, "BAMP_LwLeave", "Leave",      OnLobbyLeave, 290f, -456f, 160f, 42f); _rtLwLeave = leave?.GetComponent<RectTransform>();

            _lobbyWindow.SetActive(false);
        }

        private void SetDifficulty(string d)
        {
            _selectedDifficulty = d;
            try { _hostSettings = MPServer.Preset(d); } catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] preset {d}: {ex.Message}"); }
            // Difficulty sets the base starting cash; the per-row fields default to it
            // (and any explicit per-player overrides are kept).  Force a roster refresh.
            _lwRowCashFocus = -1;
            Plugin.Logger.LogInfo($"[MenuUI] Difficulty → {d} (base cash {_hostSettings.StartingMoney})");
            HighlightDifficulty();
        }

        private void HighlightDifficulty()
        {
            TintSel(_lwDiffEasy,   _selectedDifficulty == "Easy");
            TintSel(_lwDiffNormal, _selectedDifficulty == "Normal");
            TintSel(_lwDiffHard,   _selectedDifficulty == "Hard");
        }
        private static void TintSel(GameObject? go, bool sel)
        {
            if (go == null) return;
            try { var b = go.GetComponent<Button>(); if (b != null && b.targetGraphic != null) b.targetGraphic.color = sel ? Color.white : new Color(0.55f, 0.55f, 0.60f, 1f); } catch { }
        }

        private void OnCustomize()
        {
            // Open the deep-settings panel (year length, tax, etc.), seeded from the
            // currently-selected difficulty (_hostSettings).  It edits the same
            // _hostSettings the lobby uses, so changes carry into the started game.
            Plugin.Logger.LogInfo("[MenuUI] Customize → deep settings panel.");
            try { OnOpenSettings(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuUI] customize: {ex.Message}"); }
        }
        private void OnToggleShowIp() { _showIp = !_showIp; Plugin.Logger.LogInfo($"[MenuUI] Show IP = {_showIp}"); RefreshLobbyWindow(); }

        private void RefreshLobbyWindow()
        {
            if (_lobbyWindow == null) return;
            bool host = MPServer.IsRunning;

            if (_lwConnInfo != null)
            {
                string info;
                if (host)
                {
                    if (!_showIp) info = "Others join at:   ••••••••   (hidden)";
                    else
                    {
                        int port = MPConfig.Port;
                        string pub = MPNet.PublicIp;
                        // LAN line (user request 2026-07-04): same-network players join with the LAN ip —
                        // no port-forwarding needed for them. Shown alongside whatever internet line applies.
                        string lan = MPNet.LanIp;
                        string lanLine = string.IsNullOrEmpty(lan) ? "" : $"\nSame network (LAN):   {lan} : {port}";
                        if (!string.IsNullOrEmpty(pub))
                            info = $"Internet:   {pub} : {port}{lanLine}\n<size=15><color=#AAAAAA>(internet friends need UDP {port} forwarded on your router; LAN friends join the LAN line directly)</color></size>";
                        else if (!MPNet.PublicIpTried)
                            info = $"Internet:   (looking up your IP…){lanLine}";
                        else
                            info = $"Others join at:   {MPConfig.HostIP} : {port}{lanLine}";
                    }
                }
                else if (MPClient.IsConnected)   info = "Connected to host";
                else if (MPClient.IsConnecting)  info = "Connecting…";
                else
                {
                    // Neither hosting nor connected — a failed host bind or a
                    // failed/dropped join.  Say so instead of sitting on a stale
                    // "Connecting…" that looks like a live lobby.
                    string why = MPClient.LastDisconnectReason;
                    info = $"<color=#FF7070>Not connected{(string.IsNullOrEmpty(why) ? "" : " — " + why)}.  Leave and retry.</color>";
                }
                try { _lwConnInfo.text = info; } catch { }
            }
            // Show/Hide IP toggle — host only; label reflects state.
            SetActiveSafe(_lwShowIp, host);
            if (_lwShowIp != null) { try { var t = _lwShowIp.GetComponentInChildren<TMP_Text>(true); if (t != null) t.text = _showIp ? "Hide IP" : "Show IP"; } catch { } }

            var players = host ? MPServer.LobbyPlayers : MPClient.LobbyPlayers;
            for (int i = 0; i < _lwRoster.Length; i++)
            {
                if (_lwRoster[i] == null) continue;
                string txt = (players != null && i < players.Count) ? (i == 0 ? "★  " : "    ") + players[i] : "";
                try { _lwRoster[i].text = txt; } catch { }
            }

            // Load mode = resuming a saved game: the new-game settings (difficulty,
            // cash, age) don't apply, so hide them and show which save we're resuming.
            // The client learns the host's load mode + save name from LobbyUpdate, so
            // a joining client also hides the age field for a loaded game.
            bool loadMode = host ? _lobbyLoadMode : MPClient.HostLoadMode;
            string loadName = host ? MPServer.ChosenLoadSession : MPClient.HostLoadSession;
            SetActiveSafe(_lwLoadInfo != null ? _lwLoadInfo.gameObject : null, loadMode);
            if (loadMode && _lwLoadInfo != null)
                _lwLoadInfo.text = string.IsNullOrEmpty(loadName)
                    ? "Resuming saved game" : $"Resuming save:  {loadName}";

            int baseCash = _hostSettings != null ? _hostSettings.StartingMoney : 0;
            int baseAge  = _hostSettings != null ? _hostSettings.StartingAge   : 18;
            // Cash column = host-only + new-game only; age column = all players + new-game only.
            SetActiveSafe(_lwRowCashHdr != null ? _lwRowCashHdr.gameObject : null, host && !loadMode);
            SetActiveSafe(_lwRowAgeHdr  != null ? _lwRowAgeHdr.gameObject  : null, !loadMode);

            for (int i = 0; i < _lwRoster.Length; i++)
            {
                bool occupied = players != null && i < players.Count && !string.IsNullOrEmpty(players[i]);
                string nm = occupied ? players![i] : "";
                bool isLocal = occupied && nm == MPConfig.PlayerId;

                // CASH — host only, new-game only, editable by host on any row.
                bool showCash = host && occupied && !loadMode;
                SetActiveSafe(_rtLwRowCash[i] != null ? _rtLwRowCash[i]!.gameObject : null, showCash);
                if (showCash && _lwRowCashFocus != i)
                {
                    _lwRowCash[i] = MPServer.StartingCashFor(nm, baseCash).ToString();
                    if (_lwRowCashLbl[i] != null) { try { _lwRowCashLbl[i].text = _lwRowCash[i]; } catch { } }
                }

                // AGE — new-game only.  HOST sees all (it bakes everyone's
                // settings); a CLIENT sees ONLY ITS OWN editable age — other
                // players' ages are irrelevant noise to them (user, 2026-06-11).
                bool showAge = occupied && !loadMode && (host || isLocal);
                SetActiveSafe(_rtLwRowAge[i] != null ? _rtLwRowAge[i]!.gameObject : null, showAge);
                if (showAge && _lwRowAgeFocus != i)
                {
                    _lwRowAge[i] = DisplayAgeFor(nm, isLocal, baseAge).ToString();
                    if (_lwRowAgeLbl[i] != null)
                    {
                        try { _lwRowAgeLbl[i].text = _lwRowAge[i]; _lwRowAgeLbl[i].color = isLocal ? C_WHITE : new Color(0.6f,0.6f,0.65f,1f); } catch { }
                    }
                }

                // KICK [X] — host only, never on its own row (row 0 = host).
                bool showKick = host && occupied && i > 0;
                if (showKick && _rtLwKick[i] == null && _lwRoster[i] != null)
                {
                    var sprite = IsAlive(_panelSprite) ? _panelSprite : EnsureRoundedSprite();
                    var (xRT, xLbl) = MakeHubButton("X", Vector2.zero, 24f, new Color(0.45f, 0.24f, 0.22f, 1f), 20f, sprite, _lwRoster[i].transform);
                    xRT.anchorMin = xRT.anchorMax = xRT.pivot = new Vector2(1f, 0.5f);
                    xRT.anchoredPosition = new Vector2(-4f, 0f);
                    xLbl.fontSize = 11;
                    _rtLwKick[i] = xRT;
                }
                if (_rtLwKick[i] != null && _rtLwKick[i]!.gameObject.activeSelf != showKick)
                    _rtLwKick[i]!.gameObject.SetActive(showKick);
            }

            // New-game controls — host only, hidden in load mode.
            bool newGame = host && !loadMode;
            SetActiveSafe(_lwDiffHdr != null ? _lwDiffHdr.gameObject : null, newGame);
            SetActiveSafe(_lwDiffEasy, newGame); SetActiveSafe(_lwDiffNormal, newGame); SetActiveSafe(_lwDiffHard, newGame);
            SetActiveSafe(_lwCustomize, newGame);
            SetActiveSafe(_lwStart, host);   // Start shown to host in both modes
            if (newGame) HighlightDifficulty();
        }

        /// <summary>The age to display for a player in the lobby roster.  Local row =
        /// our own chosen age; others = the synced value (host's map / client's synced
        /// ages), falling back to the difficulty default.</summary>
        private int DisplayAgeFor(string playerId, bool isLocal, int baseAge)
        {
            try
            {
                if (MPServer.IsRunning) return MPServer.StartingAgeFor(playerId, baseAge);
                if (isLocal) return MPClient.ChosenStartingAge > 0 ? MPClient.ChosenStartingAge : baseAge;
                return MPClient.LobbyAges.TryGetValue(playerId, out var a) && a > 0 ? a : baseAge;
            }
            catch { return baseAge; }
        }

        /// <summary>MAIN THREAD per frame while the lobby window is open — clicks + cash typing.</summary>
        private void TickLobbyWindow()
        {
            if (_lobbyWindow == null || !_lobbyWindow.activeSelf) return;
            bool host = MPServer.IsRunning;
            var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            var rosterPlayers = host ? MPServer.LobbyPlayers : MPClient.LobbyPlayers;

            if (Input.GetMouseButtonDown(0))
            {
                if (RectHit(_rtLwLeave, mp)) { OnLobbyLeave(); return; }
                if (host)
                {
                    // KICK [X] on a roster row.
                    for (int i = 1; i < _rtLwKick.Length; i++)
                        if (_rtLwKick[i] != null && _rtLwKick[i]!.gameObject.activeSelf && RectHit(_rtLwKick[i]!, mp))
                        {
                            if (rosterPlayers != null && i < rosterPlayers.Count)
                            {
                                Plugin.Logger.LogInfo($"[MenuUI] kick [X] → '{rosterPlayers[i]}'");
                                MPServer.KickFromLobby(rosterPlayers[i]);
                            }
                            return;
                        }
                    if (RectHit(_rtShowIp, mp))     { OnToggleShowIp();   return; }
                    if (RectHit(_rtLwStart, mp))    { OnLobbyStart();      return; }
                    if (!_lobbyLoadMode)   // new-game controls are hidden in load mode
                    {
                        if (RectHit(_rtDiffEasy, mp))   { SetDifficulty("Easy");   return; }
                        if (RectHit(_rtDiffNormal, mp)) { SetDifficulty("Normal"); return; }
                        if (RectHit(_rtDiffHard, mp))   { SetDifficulty("Hard");   return; }
                        if (RectHit(_rtCustomize, mp))  { OnCustomize();      return; }
                    }
                }
                // Cash focus — host only, any occupied row.
                _lwRowCashFocus = -1; _lwRowAgeFocus = -1;
                if (host)
                    for (int i = 0; i < _rtLwRowCash.Length; i++)
                        if (_rtLwRowCash[i] != null && _rtLwRowCash[i]!.gameObject.activeSelf && RectHit(_rtLwRowCash[i], mp)) { _lwRowCashFocus = i; break; }
                // Age focus — your OWN row only (host or client).
                if (_lwRowCashFocus < 0)
                    for (int i = 0; i < _rtLwRowAge.Length; i++)
                        if (_rtLwRowAge[i] != null && _rtLwRowAge[i]!.gameObject.activeSelf && RectHit(_rtLwRowAge[i], mp))
                        {
                            if (rosterPlayers != null && i < rosterPlayers.Count && rosterPlayers[i] == MPConfig.PlayerId) _lwRowAgeFocus = i;
                            break;
                        }
                // Highlight the focused field so it's obviously editable.
                HighlightLobbyFocus();
            }

            // CASH typing (host).
            if (host && _lwRowCashFocus >= 0 && Input.inputString.Length > 0)
            {
                int i = _lwRowCashFocus;
                string s = TypeDigits(_lwRowCash[i], 9);
                _lwRowCash[i] = s;
                if (_lwRowCashLbl[i] != null) _lwRowCashLbl[i].text = s;
                if (rosterPlayers != null && i < rosterPlayers.Count && !string.IsNullOrEmpty(rosterPlayers[i]))
                {
                    if (int.TryParse(s, out int v)) MPServer.StartingCashByPlayer[rosterPlayers[i]] = v;
                    else                            MPServer.StartingCashByPlayer.Remove(rosterPlayers[i]);   // empty → base
                }
            }
            // AGE typing (own row).
            else if (_lwRowAgeFocus >= 0 && Input.inputString.Length > 0)
            {
                int i = _lwRowAgeFocus;
                string s = TypeDigits(_lwRowAge[i], 3);
                _lwRowAge[i] = s;
                if (_lwRowAgeLbl[i] != null) _lwRowAgeLbl[i].text = s;
                if (int.TryParse(s, out int v))
                {
                    v = Mathf.Clamp(v, 16, 99);
                    if (MPServer.IsRunning) MPServer.SetStartingAge(MPConfig.PlayerId, v);   // host's own age (+ broadcast)
                    else { MPClient.ChosenStartingAge = v; MPClient.SendLobbyPref(v); }       // client reports its age
                }
            }
        }

        /// <summary>Applies Input.inputString (digits + backspace) to a numeric string buffer.</summary>
        private static string TypeDigits(string s, int maxLen)
        {
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (s.Length > 0) s = s.Substring(0, s.Length - 1); }
                else if (char.IsDigit(c) && s.Length < maxLen) s += c;
            }
            return s;
        }

        /// <summary>Tints the focused lobby field so the player can see it's active.</summary>
        private void HighlightLobbyFocus()
        {
            for (int i = 0; i < _rtLwRowCash.Length; i++)
            {
                var ci = _rtLwRowCash[i]?.GetComponent<Image>();
                if (ci != null) ci.color = (i == _lwRowCashFocus) ? C_FIELDFOC : C_FIELD;
                var ai = _rtLwRowAge[i]?.GetComponent<Image>();
                if (ai != null) ai.color = (i == _lwRowAgeFocus) ? C_FIELDFOC : C_FIELD;
            }
        }

        // ── Player HUD ────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the HUD row text each frame it's visible.
        /// Row 0 = local player, rows 1+ = remote players from RemotePlayerManager.
        /// </summary>
        // (RefreshHUD excised 2026-06-11 — dead, no callers.)

        // ── Canvas construction ───────────────────────────────────────────────

        private void BuildCanvas()
        {
            _canvasGO = new GameObject("BAMP_Canvas");
            DontDestroyOnLoad(_canvasGO);

            var canvas = _canvasGO.AddComponent<Canvas>();
            canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 999;

            var scaler = _canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            _canvasGO.AddComponent<GraphicRaycaster>();

            // NOTE: the old draggable F8 debug panel was removed — the native main-menu
            // Multiplayer button / submenu / lobby + the in-game F9 window now cover
            // everything it did.  The deep settings panel (reused by the lobby's
            // "Customize") is a SEPARATE object (BuildSettingsPanel) and is kept.

            // In-game multiplayer window (players + chat; F9 to toggle).
            try { BuildMpWindow(_canvasGO.transform); }
            catch (Exception ex) { Plugin.Logger.LogError($"[UI] BuildMpWindow failed: {ex}"); }

            BuildStartupScreen(_canvasGO.transform);
            try { BuildSettingsPanel(_canvasGO.transform); }
            catch (Exception ex) { Plugin.Logger.LogError($"[UI] BuildSettingsPanel failed: {ex}"); }
        }

        // ── HUD builder ───────────────────────────────────────────────────────

        private void BuildCrashReportPopup(Transform canvasRoot)
        {
            _crashReportGO = MakeGO("BAMP_CrashReportPopup", canvasRoot);
            var ort = _crashReportGO.GetComponent<RectTransform>();
            ort.anchorMin = Vector2.zero;
            ort.anchorMax = Vector2.one;
            ort.offsetMin = ort.offsetMax = Vector2.zero;

            var shade = _crashReportGO.AddComponent<Image>();
            shade.color = new Color(0f, 0f, 0f, 0.68f);

            var panel = MakeGO("Panel", _crashReportGO.transform);
            _crashReportRT = panel.GetComponent<RectTransform>();
            _crashReportRT.anchorMin = _crashReportRT.anchorMax = new Vector2(0.5f, 0.5f);
            _crashReportRT.pivot = new Vector2(0.5f, 0.5f);
            _crashReportRT.sizeDelta = new Vector2(620f, 366f);
            _crashReportRT.anchoredPosition = Vector2.zero;

            var panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0.08f, 0.09f, 0.12f, 0.98f);

            var header = MakeGO("Header", panel.transform);
            var hrt = header.GetComponent<RectTransform>();
            Stretch(hrt, 0f, 0f, 0f, 46f, top: true);
            var hi = header.AddComponent<Image>();
            hi.color = C_HDR;

            _crashReportTitleLbl = MakeLabel(header.transform, "Crash report", 18, C_WHITE, 18f, 0f, 320f, 46f, TextAlignmentOptions.Left);
            ApplyFont(_crashReportTitleLbl);
            var tag = MakeLabel(header.transform, MyPluginInfo.SHORT_NAME, 12, new Color(0.78f, 0.84f, 0.94f, 1f), 456f, 0f, 140f, 46f, TextAlignmentOptions.Right);
            ApplyFont(tag);

            _crashReportBodyLbl = MakeLabel(panel.transform,
                "The previous game session did not close cleanly. Describe what happened, what you were doing, and whether you were host or client.",
                14, C_LBLGREY, 22f, -62f, 576f, 40f, TextAlignmentOptions.TopLeft);
            _crashReportBodyLbl.enableWordWrapping = true;
            ApplyFont(_crashReportBodyLbl);

            var sprite = IsAlive(_panelSprite) ? _panelSprite : EnsureRoundedSprite();
            (_crashReportTagRT, _crashReportTagLbl) = MakeHubButton("Type: Bugs", Vector2.zero, 178f, new Color(0.28f, 0.32f, 0.42f, 1f), 28f, sprite, panel.transform);
            SetTopLeft(_crashReportTagRT, 22f, -108f);
            _crashReportTagLbl.fontSize = 12;

            var input = MakeGO("Input", panel.transform);
            _crashReportInputRT = input.GetComponent<RectTransform>();
            SetAnchored(_crashReportInputRT, 22f, -144f, 576f, 112f);
            var inputImg = input.AddComponent<Image>();
            inputImg.color = C_FIELD;

            var viewport = MakeGO("TextArea", input.transform);
            var vrt = viewport.GetComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(12f, 8f); vrt.offsetMax = new Vector2(-12f, -8f);
            viewport.AddComponent<RectMask2D>();

            var textGO = MakeGO("Text", viewport.transform);
            var trt = textGO.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            var inputText = textGO.AddComponent<TextMeshProUGUI>();
            inputText.fontSize = 14; inputText.color = C_WHITE;
            inputText.alignment = TextAlignmentOptions.TopLeft;
            inputText.enableWordWrapping = true;
            inputText.overflowMode = TextOverflowModes.ScrollRect;
            ApplyFont(inputText);

            _crashReportInputField = input.AddComponent<TMP_InputField>();
            _crashReportInputField.textViewport = vrt;
            _crashReportInputField.textComponent = inputText;
            _crashReportInputField.targetGraphic = inputImg;
            _crashReportInputField.lineType = TMP_InputField.LineType.MultiLineNewline;
            _crashReportInputField.characterLimit = 700;
            _crashReportInputField.text = _crashReportMessage;
            _crashReportInputField.caretWidth = 2;
            _crashReportInputField.customCaretColor = true;
            _crashReportInputField.caretColor = C_WHITE;
            _crashReportInputField.selectionColor = new Color(0.30f, 0.50f, 0.85f, 0.55f);
            _crashReportInputField.onValueChanged.AddListener(v => _crashReportMessage = TrimCrashReportMessage(v));
            _crashReportInputField.onSelect.AddListener(_ => _crashReportFocus = true);
            _crashReportInputField.onDeselect.AddListener(_ => _crashReportFocus = false);

            _crashReportStatusLbl = MakeLabel(panel.transform, "", 12, C_LBLGREY, 22f, -266f, 576f, 22f, TextAlignmentOptions.Left);
            ApplyFont(_crashReportStatusLbl);

            (_crashReportAttachRT, var attachLbl) = MakeHubButton("Attach files", Vector2.zero, 150f, new Color(0.28f, 0.32f, 0.42f, 1f), 36f, sprite, panel.transform);
            SetTopLeft(_crashReportAttachRT, 22f, -306f);
            attachLbl.fontSize = 13;

            (_crashReportSendRT, var sendLbl) = MakeHubButton("Send report", Vector2.zero, 148f, C_BTNBLUE, 36f, sprite, panel.transform);
            SetTopLeft(_crashReportSendRT, 286f, -306f);
            sendLbl.fontSize = 13;

            (_crashReportDismissRT, var dismissLbl) = MakeHubButton("Dismiss", Vector2.zero, 132f, C_STOP, 36f, sprite, panel.transform);
            SetTopLeft(_crashReportDismissRT, 450f, -306f);
            dismissLbl.fontSize = 13;

            _crashReportGO.SetActive(false);
        }

        private void TickCrashReportPopup()
        {
            if (!_crashReportPopupVisible)
            {
                if (_crashReportGO != null && _crashReportGO.activeSelf) _crashReportGO.SetActive(false);
                return;
            }
            if (_canvasGO == null) return;
            if (_crashReportGO == null) BuildCrashReportPopup(_canvasGO.transform);
            StyleCrashReportPopup();
            if (_crashReportGO != null && !_crashReportGO.activeSelf) _crashReportGO.SetActive(true);
            if (_crashReportAutoFocusPending && _crashReportInputField != null)
            {
                try
                {
                    EventSystem.current?.SetSelectedGameObject(_crashReportInputField.gameObject);
                    _crashReportInputField.ActivateInputField();
                    _crashReportInputField.MoveTextEnd(false);
                }
                catch { }
                _crashReportAutoFocusPending = false;
            }

            if (_crashReportAutoCloseAt > 0f && Time.unscaledTime >= _crashReportAutoCloseAt) { DismissCrashReportPopup(); return; }
            RefreshCrashReportText();

            var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            if (Input.GetMouseButtonDown(0))
            {
                if (_crashReportSendRT != null && RectHit(_crashReportSendRT, mp)) { SendCrashReportPopup(); return; }
                if (_crashReportDismissRT != null && RectHit(_crashReportDismissRT, mp)) { DismissCrashReportPopup(); return; }
                if (_crashReportAttachRT != null && RectHit(_crashReportAttachRT, mp)) { AttachFilesToBugReport(); return; }
                if (_crashReportTagRT != null && RectHit(_crashReportTagRT, mp)) { NextBugReportTag(); return; }
                _crashReportFocus = _crashReportInputRT != null && RectHit(_crashReportInputRT, mp);
            }

            if (!_crashReportFocus) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { _crashReportFocus = false; return; }
        }

        /// <summary>Entry point for the recycled native top-bar bug-report button (2026-07-08).</summary>
        internal void OpenManualBugReport()
        {
            _crashReportIsCrash = false;
            _crashReportMessage = "";
            _crashReportAttachments.Clear();
            _bugReportTagIndex = 0;
            _crashReportSending = false; _crashReportResult = ""; _crashReportAutoCloseAt = 0f;
            _crashReportPopupVisible = true;
            _crashReportFocus = true;
            _crashReportAutoFocusPending = true;
            if (_crashReportInputField != null)
                _crashReportInputField.SetTextWithoutNotify("");
            MPChat.AddNotice("bug report window opened");
        }

        private void AttachFilesToBugReport()
        {
            // Show the OS file picker OUT OF PROCESS (NativeFilePicker → PowerShell) on a background
            // thread, so it can never crash the game (the old in-process native dialog did, 2026-06-19).
            // The main thread is never blocked; results are marshalled back when the user finishes picking.
            if (_crashReportStatusLbl != null)
                _crashReportStatusLbl.text = "Opening the file picker — choose your screenshots/videos there…";
            var t = new Thread(() =>
            {
                string[] files;
                try { files = NativeFilePicker.PickBugReportAttachments(); }
                catch { files = Array.Empty<string>(); }
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    foreach (var f in files)
                        if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)
                            && !_crashReportAttachments.Contains(f, StringComparer.OrdinalIgnoreCase))
                            _crashReportAttachments.Add(f);
                    RefreshCrashReportText();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void StyleCrashReportPopup()
        {
            if (_crashReportGO == null) return;
            var sprite = IsAlive(_panelSprite) ? _panelSprite : EnsureRoundedSprite();
            if (sprite != null && !ReferenceEquals(_crashReportStyledWith, sprite))
            {
                foreach (var img in _crashReportGO.GetComponentsInChildren<Image>(true))
                {
                    if (img.gameObject == _crashReportGO) continue;
                    try { img.sprite = sprite; img.type = Image.Type.Sliced; } catch { }
                }
                _crashReportStyledWith = sprite;
            }
            if (IsAlive(_gameFont))
                foreach (var t in _crashReportGO.GetComponentsInChildren<TMP_Text>(true)) ApplyFont(t);
        }

        private void RefreshCrashReportText()
        {
            if (_crashReportTitleLbl != null)
                _crashReportTitleLbl.text = _crashReportIsCrash ? "Crash report" : "Bug report";
            if (_crashReportBodyLbl != null)
            {
                // Task #5: tell the player (and the report reader) WHERE the previous session died —
                // "last alive at the main menu, uptime 41s" reads very differently from a mid-game crash.
                string hint = MPBugReport.PendingCrashHint;
                _crashReportBodyLbl.text = _crashReportIsCrash
                    ? "The previous game session did not close cleanly. Describe what happened, what you were doing, and whether you were host or client."
                      + (string.IsNullOrEmpty(hint) ? "" : "\n" + hint)
                    : "Describe the bug, what you were doing, and whether you were host or client. Attach screenshots or short videos if they help.";
            }
            if (_crashReportInputField != null && _crashReportInputField.text != _crashReportMessage)
                _crashReportInputField.SetTextWithoutNotify(_crashReportMessage);
            RefreshCrashReportTagText();
            if (_crashReportStatusLbl != null)
            {
                if (!string.IsNullOrEmpty(_crashReportResult))
                {
                    _crashReportStatusLbl.text = _crashReportResult;   // submit/sent/failed — hold it, don't overwrite
                }
                else
                {
                    string attach = _crashReportAttachments.Count == 0
                        ? "Optional: click 'Attach files' to add screenshots/videos. Your logs are always included."
                        : $"{_crashReportAttachments.Count} file(s) attached. Files over 24 MB are skipped.";
                    bool uploadOn = MPConfig.BugReportRelayUrlLive().Length > 0 || MPConfig.BugReportDiscordWebhookUrlLive().Length > 0;
                    _crashReportStatusLbl.text = (uploadOn
                        ? "Uploads one zip: description, logs, report and attachments. "
                        : "Discord upload is not configured. A local report folder will be saved. ") + attach;
                }
            }
        }

        private void SendCrashReportPopup()
        {
            if (_crashReportSending) return;   // already in flight — ignore double-clicks
            try
            {
                if (_crashReportInputField != null) _crashReportMessage = _crashReportInputField.text ?? "";
                string prefix = _crashReportIsCrash ? "previous crash: " : "manual bug report: ";
                _crashReportSending = true;
                _crashReportResult = "Submitting report...";   // shown by RefreshCrashReportText; popup stays open
                var result = MPBugReport.Create(prefix + _crashReportMessage, openFolder: false,
                    attachments: _crashReportAttachments, discordTagIds: SelectedDiscordForumTagIds(),
                    onUploadComplete: (ok, folder) => GameStatePatcher.EnqueueOnMainThread(() => OnReportUploadDone(ok)),
                    includeCrashArtifacts: _crashReportIsCrash);   // Unity crash folder rides CRASH reports only
                if (_crashReportIsCrash) MPBugReport.AcknowledgePendingCrash();
                if (!result.DiscordUploadQueued)   // nothing to upload — report saved locally, done
                {
                    _crashReportSending = false;
                    _crashReportResult = "Saved locally (Discord upload not configured).";
                    _crashReportAutoCloseAt = Time.unscaledTime + 2.5f;
                }
            }
            catch (Exception ex)
            {
                _crashReportSending = false;
                _crashReportResult = "Report failed: " + ex.Message;
                Plugin.Logger.LogWarning($"[BugReport] crash popup send failed: {ex.Message}");
            }
        }

        // Main-thread callback when the async upload finishes (relay/Discord result).
        private void OnReportUploadDone(bool ok)
        {
            if (!_crashReportPopupVisible) return;   // user already closed the popup
            _crashReportSending = false;
            if (ok)
            {
                _crashReportResult = "Report sent. You can close this window.";
                _crashReportAutoCloseAt = Time.unscaledTime + 2f;   // let them see it, then auto-close
            }
            else
            {
                _crashReportResult = "Upload failed - saved locally. You can attach the report folder manually.";
                // keep the popup open so they see it; no auto-close
            }
        }

        private void DismissCrashReportPopup()
        {
            if (_crashReportIsCrash) MPBugReport.AcknowledgePendingCrash();
            _crashReportSending = false; _crashReportResult = ""; _crashReportAutoCloseAt = 0f;
            _crashReportPopupVisible = false;
            if (_crashReportGO != null) _crashReportGO.SetActive(false);
        }

        private void NextBugReportTag()
        {
            if (_crashReportIsCrash) return;
            var tags = MPConfig.BugReportDiscordBugTagsLive();
            if (tags.Count == 0)
            {
                if (_crashReportStatusLbl != null)
                    _crashReportStatusLbl.text = "No Discord bug tags are configured yet. Add BugReportDiscordBugTags to the mod config.";
                MPChat.AddNotice("no Discord bug tags configured");
                return;
            }
            _bugReportTagIndex = (_bugReportTagIndex + 1) % tags.Count;
            RefreshCrashReportTagText();
        }

        private IEnumerable<string> SelectedDiscordForumTagIds()
        {
            if (_crashReportIsCrash)
            {
                string crashTag = MPConfig.BugReportDiscordCrashTagIdLive();
                if (!string.IsNullOrWhiteSpace(crashTag)) yield return crashTag;
                yield break;
            }

            var tags = MPConfig.BugReportDiscordBugTagsLive();
            if (tags.Count == 0) yield break;
            _bugReportTagIndex = Mathf.Clamp(_bugReportTagIndex, 0, tags.Count - 1);
            yield return tags[_bugReportTagIndex].Id;
        }

        private static IEnumerable<string> DefaultBugDiscordForumTagIds()
        {
            var tags = MPConfig.BugReportDiscordBugTagsLive();
            if (tags.Count > 0)
                yield return tags[0].Id;
        }

        private void RefreshCrashReportTagText()
        {
            if (_crashReportTagRT != null)
                _crashReportTagRT.gameObject.SetActive(true);
            if (_crashReportTagLbl == null) return;

            if (_crashReportIsCrash)
            {
                _crashReportTagLbl.text = string.IsNullOrWhiteSpace(MPConfig.BugReportDiscordCrashTagIdLive())
                    ? "Type: Crash (not configured)"
                    : "Type: Crash";
                return;
            }

            var tags = MPConfig.BugReportDiscordBugTagsLive();
            if (tags.Count == 0)
            {
                _crashReportTagLbl.text = "Type: configure tags";
                return;
            }

            _bugReportTagIndex = Mathf.Clamp(_bugReportTagIndex, 0, tags.Count - 1);
            _crashReportTagLbl.text = "Type: " + tags[_bugReportTagIndex].Label;
        }

        private static string TrimCrashReportMessage(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r", "");
            return value.Length > 700 ? value.Substring(0, 700) : value;
        }

        // (2026-06-16) Removed 8 dead hand-rolled text-editing helpers (TrimTextLimit, RenderTextWithCaret,
        // ControlDown, CleanClipboardForInput, InsertTextAtCaret, MoveCaretVertical, EditTextBuffer,
        // EscapeCrashText) — leftovers from the pre-TMP_InputField chat input; EditTextBuffer (the entry
        // point) had zero callers and the rest only called each other.

        private const float HUD_W = 230f;
        private const float HUD_ROW_H = 22f;
        private const float HUD_PAD = 8f;

        private void BuildHUD(Transform canvasRoot)
        {
            // Container anchored to top-right corner of the screen
            _hudGO = MakeGO("BAMP_HUD", canvasRoot);
            var rt = _hudGO.GetComponent<RectTransform>();
            // Anchor top-right; pivot top-right
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
            // 10px from top-right edge; height calculated below
            const int maxRows = 8;
            float totalH = HUD_ROW_H + maxRows * HUD_ROW_H + HUD_PAD * 2f;
            rt.sizeDelta        = new Vector2(HUD_W, totalH);
            rt.anchoredPosition = new Vector2(-10f, -10f);

            // Background
            var bg = _hudGO.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.08f, 0.10f, 0.82f);

            // Header row
            _hudHeader = MakeLabel(_hudGO.transform, "[MP]  F9",
                12, new Color(0.55f, 0.85f, 1f, 1f),
                HUD_PAD, -HUD_PAD, HUD_W - HUD_PAD * 2f, HUD_ROW_H,
                TextAlignmentOptions.Left);

            // Player rows
            for (int i = 0; i < maxRows; i++)
            {
                float rowY = -(HUD_PAD + HUD_ROW_H + i * HUD_ROW_H);
                _hudRows[i] = MakeLabel(_hudGO.transform, "",
                    11, i == 0 ? new Color(0.4f, 1f, 0.4f, 1f)   // local player — green
                                : new Color(0.9f, 0.9f, 0.9f, 1f), // remote — white
                    HUD_PAD, rowY, HUD_W - HUD_PAD * 2f, HUD_ROW_H,
                    TextAlignmentOptions.Left);
            }

            // Starts hidden — user presses F9 to show
            _hudGO.SetActive(false);
        }

        // ── In-game multiplayer window (Phase 6) ──────────────────────────────
        // Draggable, corner-resizable window anchored top-right.  Compact collapsible
        // players list + a chat log that takes the majority of the height + an input
        // row.  The opacity slider fades ONLY the background/chrome — the slider,
        // players list and chat text stay fully readable even at opacity 0.  Manual
        // hit-testing throughout (RectHit + Input.mousePosition), like the lobby.

        private const float MPW_W   = 340f;   // default width  (freely resizable)
        private const float MPW_H   = 430f;   // default height (freely resizable)
        private const float MP_TITLE_H = 26f;
        private const float MP_ROW  = 20f;    // roster line height (matches font 13 line spacing)
        private const float MP_PAD  = 8f;
        private const float MP_INPUT_H = 26f;
        private const float MP_SEND_W  = 58f;
        private const float MP_ROSTER_TOP = 34f;   // chips row sits right under the title bar (opacity lives IN the bar)

        private Image AddFade(Image img) { _mpFade.Add((img, img.color.a)); return img; }

        /// <summary>Anchor a RectTransform as a stretch within its parent, inset by
        /// (left, top, right, bottom).  If <paramref name="top"/> is set, it instead
        /// anchors as a full-width bar of height=<paramref name="bottom"/> pinned to
        /// the parent's top, <paramref name="topY"/> below the top edge.</summary>
        private static void Stretch(RectTransform rt, float left, float topY, float right, float bottom, bool top = false)
        {
            if (top)
            {
                rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
                rt.offsetMin = new Vector2(left, -(topY + bottom));   // bottom == height here
                rt.offsetMax = new Vector2(-right, -topY);
            }
            else
            {
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(left, bottom);
                rt.offsetMax = new Vector2(-right, -topY);
            }
        }

        private void BuildMpWindow(Transform canvasRoot)
        {
            _mpFade.Clear();
            _mpWin   = MakeGO("BAMP_MpWindow", canvasRoot);
            _mpWinRT = _mpWin.GetComponent<RectTransform>();
            _mpWinRT.anchorMin = _mpWinRT.anchorMax = _mpWinRT.pivot = new Vector2(1f, 1f); // top-right
            _mpWinRT.sizeDelta        = new Vector2(MPW_W, MPW_H);
            _mpWinRT.anchoredPosition = new Vector2(-12f, -12f);

            var bg = _mpWin.AddComponent<Image>();
            bg.color = new Color(0.07f, 0.08f, 0.11f, 0.96f);
            if (_panelSprite != null) { try { bg.sprite = _panelSprite; bg.type = Image.Type.Sliced; } catch { } }
            AddFade(bg);

            var grey = C_LBLGREY;

            // Title bar — drag handle.
            var titleGO = MakeGO("Title", _mpWin.transform);
            _mpTitleRT = titleGO.GetComponent<RectTransform>();
            Stretch(_mpTitleRT, 0f, 0f, 0f, MP_TITLE_H, top: true);   // full-width bar pinned to the top
            var titleImg = titleGO.AddComponent<Image>();
            titleImg.color = new Color(0.15f, 0.18f, 0.27f, 1f);
            if (_panelSprite != null) { try { titleImg.sprite = _panelSprite; titleImg.type = Image.Type.Sliced; } catch { } }
            AddFade(titleImg);
            ApplyFont(MakeLabel(titleGO.transform, "Chat", 14, C_WHITE, 10f, 0f, 120f, MP_TITLE_H, TextAlignmentOptions.Left));
            // Close [X] — right edge of the title bar (phone button re-opens).
            var closeGO = MakeGO("Close", titleGO.transform);
            _mpCloseRT = closeGO.GetComponent<RectTransform>();
            _mpCloseRT.anchorMin = _mpCloseRT.anchorMax = _mpCloseRT.pivot = new Vector2(1f, 0.5f);
            _mpCloseRT.anchoredPosition = new Vector2(-8f, 0f);
            _mpCloseRT.sizeDelta = new Vector2(26f, 20f);
            var closeLbl = closeGO.AddComponent<TextMeshProUGUI>();
            closeLbl.text = "X"; closeLbl.fontSize = 13; closeLbl.alignment = TextAlignmentOptions.Center;
            closeLbl.color = new Color(0.85f, 0.58f, 0.58f, 1f);
            ApplyFont(closeLbl);

            // (Chat-bar "Report" button REMOVED 2026-07-08 — too hidden; the native top-bar bug-report
            //  button, which the game disables on modded saves, is recycled as the entry point instead:
            //  Patch_ReportBugButton_ModTakeover in MPBugReport.cs.)

            // Opacity slider — lives IN the title bar (right side, before [X])
            // so no vertical space is spent on it.  Slim track + fill + knob.
            const float TRK_W = 110f, TRK_H = 6f;
            var trackGO = MakeGO("OpacityTrack", titleGO.transform);
            _mpOpacityTrackRT = trackGO.GetComponent<RectTransform>();
            _mpOpacityTrackRT.anchorMin = _mpOpacityTrackRT.anchorMax = _mpOpacityTrackRT.pivot = new Vector2(1f, 0.5f);
            _mpOpacityTrackRT.anchoredPosition = new Vector2(-44f, 0f);
            _mpOpacityTrackRT.sizeDelta = new Vector2(TRK_W, TRK_H);
            var trackImg = trackGO.AddComponent<Image>();
            trackImg.color = new Color(0.26f, 0.26f, 0.32f, 1f);
            if (_panelSprite != null) { try { trackImg.sprite = _panelSprite; trackImg.type = Image.Type.Sliced; } catch { } }
            var fillGO = MakeGO("OpacityFill", trackGO.transform);
            _mpOpacityFillRT = fillGO.GetComponent<RectTransform>();
            SetAnchored(_mpOpacityFillRT, 0f, 0f, TRK_W * _mpOpacity, TRK_H);
            var fillImg = fillGO.AddComponent<Image>();
            fillImg.color = MpPurple;
            if (_panelSprite != null) { try { fillImg.sprite = _panelSprite; fillImg.type = Image.Type.Sliced; } catch { } }
            var knobGO = MakeGO("OpacityKnob", trackGO.transform);
            _mpOpacityKnobRT = knobGO.GetComponent<RectTransform>();
            SetAnchored(_mpOpacityKnobRT, TRK_W * _mpOpacity - 6f, 4f, 12f, 14f);  // straddles the track
            var knobImg = knobGO.AddComponent<Image>();
            knobImg.color = C_WHITE;
            if (_panelSprite != null) { try { knobImg.sprite = _panelSprite; knobImg.type = Image.Type.Sliced; } catch { } }

            // Recipient chips row — "All" + one chip per player.  The selected
            // chip is WHERE your next message goes; chips double as the roster.
            ApplyFont(MakeLabel(_mpWin.transform, "To:", 11, grey, 10f, -(MP_ROSTER_TOP + 3f), 24f, CHIP_H, TextAlignmentOptions.Left));
            _mpChipsRow = MakeGO("Chips", _mpWin.transform);
            _mpChipsRowRT = _mpChipsRow.GetComponent<RectTransform>();
            Stretch(_mpChipsRowRT, MP_PAD + 26f, MP_ROSTER_TOP, MP_PAD, CHIP_H, top: true);
            // Clip + horizontally scroll (mouse wheel) when many/long player
            // names overflow the row — chips live on an inner content rect.
            try { _mpChipsRow.AddComponent<RectMask2D>(); } catch { }
            _mpChipsContent = MakeGO("Content", _mpChipsRow.transform);
            var ccrt = _mpChipsContent.GetComponent<RectTransform>();
            ccrt.anchorMin = new Vector2(0f, 0f); ccrt.anchorMax = new Vector2(0f, 1f);
            ccrt.pivot = new Vector2(0f, 0.5f);
            ccrt.anchoredPosition = Vector2.zero;
            ccrt.sizeDelta = new Vector2(2000f, 0f);

            // Chat log — background (fades) + text (stays opaque, fills the bg).
            var logGO = MakeGO("ChatPanel", _mpWin.transform);
            _mpChatPanelRT = logGO.GetComponent<RectTransform>();
            // Stretch-fill the flexible middle; top inset is set per roster size in LayoutMpWindow.
            Stretch(_mpChatPanelRT, MP_PAD, MP_ROSTER_TOP, MP_PAD, MP_INPUT_H + MP_PAD * 2f);
            var logBg = logGO.AddComponent<Image>();
            logBg.color = new Color(0.03f, 0.03f, 0.05f, 0.55f);
            if (_panelSprite != null) { try { logBg.sprite = _panelSprite; logBg.type = Image.Type.Sliced; } catch { } }
            AddFade(logBg);
            var chatTextGO = MakeGO("ChatText", logGO.transform);
            var ctrt = chatTextGO.GetComponent<RectTransform>();
            ctrt.anchorMin = Vector2.zero; ctrt.anchorMax = Vector2.one;
            ctrt.offsetMin = new Vector2(8f, 6f); ctrt.offsetMax = new Vector2(-8f, -4f);
            _mpChatLog = chatTextGO.AddComponent<TextMeshProUGUI>();
            _mpChatLog.fontSize = 12; _mpChatLog.color = new Color(0.88f, 0.91f, 0.97f, 1f);
            _mpChatLog.alignment = TextAlignmentOptions.BottomLeft;
            _mpChatLog.enableWordWrapping = true;
            _mpChatLog.overflowMode = TextOverflowModes.Truncate;
            ApplyFont(_mpChatLog);

            // "To X >" prefix — always states where the message will go.
            var toGO = MakeGO("ToLbl", _mpWin.transform);
            _mpToRT = toGO.GetComponent<RectTransform>();
            _mpToRT.anchorMin = _mpToRT.anchorMax = _mpToRT.pivot = new Vector2(0f, 0f);
            _mpToRT.anchoredPosition = new Vector2(MP_PAD, MP_PAD);
            _mpToRT.sizeDelta = new Vector2(MP_TO_W, MP_INPUT_H);
            _mpToLbl = toGO.AddComponent<TextMeshProUGUI>();
            _mpToLbl.fontSize = 11; _mpToLbl.alignment = TextAlignmentOptions.Left;
            _mpToLbl.color = grey; _mpToLbl.enableWordWrapping = false;
            _mpToLbl.overflowMode = TextOverflowModes.Ellipsis;
            ApplyFont(_mpToLbl);

            // Chat input — anchored bottom, stretches horizontally (leaves room
            // for the To-prefix on the left and Send on the right).
            var inGO = MakeGO("ChatInput", _mpWin.transform);
            _mpChatInputRT = inGO.GetComponent<RectTransform>();
            _mpChatInputRT.anchorMin = new Vector2(0f, 0f); _mpChatInputRT.anchorMax = new Vector2(1f, 0f); _mpChatInputRT.pivot = new Vector2(0f, 0f);
            _mpChatInputRT.offsetMin = new Vector2(MP_PAD + MP_TO_W + 4f, MP_PAD);
            _mpChatInputRT.offsetMax = new Vector2(-(MP_SEND_W + MP_PAD * 2f), MP_PAD + MP_INPUT_H);
            var inImg = inGO.AddComponent<Image>(); inImg.color = C_FIELD;
            if (_panelSprite != null) { try { inImg.sprite = _panelSprite; inImg.type = Image.Type.Sliced; } catch { } }
            var inputViewport = MakeGO("TextArea", inGO.transform);
            var ivrt = inputViewport.GetComponent<RectTransform>();
            ivrt.anchorMin = Vector2.zero; ivrt.anchorMax = Vector2.one;
            ivrt.offsetMin = new Vector2(7f, 0f); ivrt.offsetMax = new Vector2(-7f, 0f);
            inputViewport.AddComponent<RectMask2D>();
            var inTextGO = MakeGO("Text", inputViewport.transform);
            var itrt = inTextGO.GetComponent<RectTransform>();
            itrt.anchorMin = Vector2.zero; itrt.anchorMax = Vector2.one; itrt.offsetMin = Vector2.zero; itrt.offsetMax = Vector2.zero;
            var chatText = inTextGO.AddComponent<TextMeshProUGUI>();
            chatText.fontSize = SZ_FLD; chatText.color = C_WHITE; chatText.alignment = TextAlignmentOptions.Left;
            chatText.enableWordWrapping = false; chatText.overflowMode = TextOverflowModes.ScrollRect;
            ApplyFont(chatText);

            _mpChatInputField = inGO.AddComponent<TMP_InputField>();
            _mpChatInputField.textViewport = ivrt;
            _mpChatInputField.textComponent = chatText;
            _mpChatInputField.targetGraphic = inImg;
            _mpChatInputField.lineType = TMP_InputField.LineType.SingleLine;
            _mpChatInputField.characterLimit = 120;
            _mpChatInputField.caretWidth = 2;
            _mpChatInputField.customCaretColor = true;
            _mpChatInputField.caretColor = C_WHITE;
            _mpChatInputField.selectionColor = new Color(0.30f, 0.50f, 0.85f, 0.55f);
            _mpChatInputField.onValueChanged.AddListener(v => _mpChatInput = v ?? "");
            _mpChatInputField.onSelect.AddListener(_ => _mpChatFocus = true);
            _mpChatInputField.onDeselect.AddListener(_ => _mpChatFocus = false);
            _mpChatInputField.onSubmit.AddListener(_ => SubmitMpChat());

            // Send — anchored bottom-right.
            var sGO = MakeGO("Send", _mpWin.transform);
            _mpSendRT = sGO.GetComponent<RectTransform>();
            _mpSendRT.anchorMin = _mpSendRT.anchorMax = new Vector2(1f, 0f); _mpSendRT.pivot = new Vector2(1f, 0f);
            _mpSendRT.anchoredPosition = new Vector2(-MP_PAD, MP_PAD); _mpSendRT.sizeDelta = new Vector2(MP_SEND_W, MP_INPUT_H);
            var sImg = sGO.AddComponent<Image>(); sImg.color = new Color(0.20f, 0.36f, 0.60f, 1f);
            if (_panelSprite != null) { try { sImg.sprite = _panelSprite; sImg.type = Image.Type.Sliced; } catch { } }
            var sTextGO = MakeGO("t", sGO.transform);
            var strt = sTextGO.GetComponent<RectTransform>(); strt.anchorMin = Vector2.zero; strt.anchorMax = Vector2.one; strt.offsetMin = Vector2.zero; strt.offsetMax = Vector2.zero;
            var sTmp = sTextGO.AddComponent<TextMeshProUGUI>(); sTmp.text = "Send"; sTmp.fontSize = SZ_BTN; sTmp.color = C_WHITE; sTmp.alignment = TextAlignmentOptions.Center; ApplyFont(sTmp);

            // Resize grip — bottom-left corner, drawn as the standard three
            // diagonal stripes (was a plain circle, which reads as a button).
            var gripGO = MakeGO("ResizeGrip", _mpWin.transform);
            _mpGripRT = gripGO.GetComponent<RectTransform>();
            _mpGripRT.anchorMin = _mpGripRT.anchorMax = _mpGripRT.pivot = new Vector2(0f, 0f);
            _mpGripRT.anchoredPosition = new Vector2(2f, 2f); _mpGripRT.sizeDelta = new Vector2(16f, 16f);
            var gripBg = gripGO.AddComponent<Image>();          // invisible hit area
            gripBg.color = new Color(0f, 0f, 0f, 0f);
            // Three PARALLEL stripes: oriented -45° (running upper-left to
            // lower-right), centers stepped along the corner diagonal, shorter
            // toward the corner.  (v1 offset the stripes ALONG their own
            // direction — collinear, so they rendered as one line.)
            var stripeCol = new Color(0.55f, 0.55f, 0.66f, 0.9f);
            for (int i = 0; i < 3; i++)
            {
                var sgo = MakeGO("g" + i, gripGO.transform);
                var sgr = sgo.GetComponent<RectTransform>();
                sgr.anchorMin = sgr.anchorMax = new Vector2(0f, 0f);
                sgr.pivot = new Vector2(0.5f, 0.5f);
                float c = 2.5f + i * 2.6f;                       // center distance from the corner
                sgr.anchoredPosition = new Vector2(c, c);
                sgr.sizeDelta = new Vector2(5f + i * 4.6f, 1.8f); // longer away from the corner
                sgr.localRotation = Quaternion.Euler(0f, 0f, -45f);
                AddFade(sgo.AddComponent<Image>()).color = stripeCol;
            }

            SetMpOpacity(_mpOpacity);
            LayoutMpWindow(1);         // initial roster + chat sizing
            _mpWin.SetActive(false);   // hidden until F9
        }

        /// <summary>Positions the chat area below the fixed chips row.</summary>
        private void LayoutMpWindow(int count)
        {
            if (_mpChatPanelRT != null)
                _mpChatPanelRT.offsetMax = new Vector2(-MP_PAD, -(MP_ROSTER_TOP + CHIP_H + 6f));
        }

        /// <summary>Rebuild the recipient chips ("All" + each other player).</summary>
        private void RebuildChips(IReadOnlyList<string>? players)
        {
            foreach (var c in _mpChips) { try { UnityEngine.Object.Destroy(c.rt.gameObject); } catch { } }
            _mpChips.Clear();
            if (_mpChipsRow == null) return;

            // Target sanity — if the selected player left, fall back to All.
            if (_chatTarget != "" && (players == null || !players.Contains(_chatTarget)))
                _chatTarget = "";

            float x = 0f;
            AddChip("All", "", ref x);
            if (players != null)
                foreach (var p in players)
                    if (!string.IsNullOrEmpty(p) && p != MPConfig.PlayerId)
                        AddChip(MPNames.Resolve(p), p, ref x);
            _chipsTotalW = x;
            _chipScroll  = 0f;
            if (_mpChipsContent != null)
                _mpChipsContent.GetComponent<RectTransform>().anchoredPosition = Vector2.zero;
        }

        private void AddChip(string label, string who, ref float x)
        {
            var go = MakeGO("Chip_" + label, (_mpChipsContent != null ? _mpChipsContent : _mpChipsRow!).transform);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
            float w = Mathf.Max(42f, 18f + label.Length * 7f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(w, CHIP_H - 4f);
            x += w + 6f;

            bool sel = _chatTarget == who;
            var img = go.AddComponent<Image>();
            img.color = sel ? MpPurple : new Color(0.18f, 0.20f, 0.27f, 1f);
            if (_panelSprite != null) { try { img.sprite = _panelSprite; img.type = Image.Type.Sliced; } catch { } }

            var lbl = MakeLabel(go.transform, label, 11, sel ? C_WHITE : new Color(0.75f, 0.78f, 0.85f, 1f),
                                0f, 0f, w, CHIP_H - 4f, TextAlignmentOptions.Center);
            ApplyFont(lbl);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;

            _mpChips.Add((rt, img, who));
        }

        // Stable per-player chat colours (self is always green).
        private static readonly string[] ChatPalette = { "#6FB1FF", "#FFB46F", "#FF6F9C", "#6FFFE0", "#D8A6FF", "#FFE66F" };
        private const string PrivateColor = "#E08CFF";

        private string RenderChatLine(ChatLine l)
        {
            // Neutralize any rich-text tags typed into chat ("<b>" → "< b>").
            static string Esc(string s) => s.Replace("<", "< ");
            if (l.Notice) return $"<color=#9AA3B2><i>— {Esc(l.Text)}</i></color>";
            // Resolve sender/recipient ids to in-character names for display; the
            // ids themselves remain the routing/colour key.
            string fromName = Esc(MPNames.Resolve(l.From));
            if (!string.IsNullOrEmpty(l.To))
            {
                bool mine = l.From == MPConfig.PlayerId;
                string tag = mine ? $"[To {Esc(MPNames.Resolve(l.To))}]" : $"[From {fromName}]";
                return $"<color={PrivateColor}>{tag}</color>  {Esc(l.Text)}";
            }
            string col = l.From == MPConfig.PlayerId
                ? "#5BFF5B"
                : ChatPalette[Mathf.Abs(l.From.GetHashCode()) % ChatPalette.Length];
            return $"<color={col}>{fromName}:</color>  {Esc(l.Text)}";
        }

        private void RefreshMpWindow()
        {
            if (_mpWin == null) return;
            StyleMpWindow();   // self-gated: styles once captured, restyles if a better sprite lands

            // Roster source = the connected-players list (persists in-game + is kept
            // fresh by the host re-broadcasting LobbyUpdate on connect/disconnect).
            IReadOnlyList<string>? players = MPServer.IsRunning ? MPServer.LobbyPlayers
                                  : MPClient.IsConnected ? MPClient.LobbyPlayers : null;
            int count = players?.Count ?? 0;

            // Rebuild the recipient chips when the player set or target changes
            // (not every frame).
            string sig = (players == null ? "" : string.Join("", players)) + "|" + _chatTarget;
            if (sig != _mpRosterSig)
            {
                _mpRosterSig = sig;
                LayoutMpWindow(count);
                RebuildChips(players);
            }

            // Chat log - newest lines that fit (auto-scroll), wheel scrollback.
            if (_mpChatLog != null && _mpChatPanelRT != null)
            {
                var all = MPChat.Snapshot(500);
                int total = all.Count;
                int visible = Mathf.Max(1, Mathf.FloorToInt((_mpChatPanelRT.rect.height - 12f) / 15f));
                _mpChatScroll = Mathf.Clamp(_mpChatScroll, 0, Mathf.Max(0, total - visible));
                int end   = total - _mpChatScroll;
                int start = Mathf.Max(0, end - visible);
                try
                {
                    if (total == 0)
                        _mpChatLog.text = "<i>No messages yet.  Type below and press Enter.</i>";
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int i = start; i < end; i++)
                        {
                            sb.Append(RenderChatLine(all[i]));
                            if (i < end - 1) sb.Append('\n');
                        }
                        _mpChatLog.text = sb.ToString();
                    }
                }
                catch { }
            }

            // Input prefix ("To X >") + chat input (+ blinking caret while focused).
            if (_mpToLbl != null)
            {
                try
                {
                    _mpToLbl.text = _chatTarget == ""
                        ? "To All  >"
                        : $"<color={PrivateColor}>To {_chatTarget}  ></color>";
                }
                catch { }
            }
            if (_mpChatInputField != null && _mpChatInputField.text != _mpChatInput)
                _mpChatInputField.SetTextWithoutNotify(_mpChatInput);
        }

        private void TickMpWindow()
        {
            if (_mpWin == null || !_mpWin.activeSelf) { _chatSuppress = false; return; }
            var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            // Chat's contribution to the input-suppression flag.  FOCUS-ONLY: we
            // force the game's keyboard/shortcut gate ONLY while the chat box is
            // actually focused for typing.  Clicks that land ON the window are
            // already blocked by the game itself (MouseController gates every world
            // click on EventSystem.IsPointerOverGameObject, and the window's
            // background Image is a raycast target), so hovering/dragging the window
            // must NOT freeze movement — the old hover hit-test did, and also broke
            // GameManager.HandleEscapeClick (forced HasInputSelected with nothing
            // selected → NRE on car exit).  The FLAG is still computed fresh every
            // frame in Update (no latching — that once locked the keyboard, 2026-06-10).
            _mpChatFocus = _mpChatInputField != null && _mpChatInputField.isFocused;
            _chatSuppress = _mpChatFocus;

            if (Input.GetMouseButtonDown(0))
            {
                bool chipHit = false;
                foreach (var chip in _mpChips)
                    if (RectHit(chip.rt, mp))
                    { _chatTarget = chip.who; _mpRosterSig = "\0"; chipHit = true; break; }   // sig reset retints chips

                if      (chipHit)                         { }
                else if (RectHit(_mpCloseRT, mp))         { ToggleMpWindow(); return; }
                else if (RectHit(_mpGripRT, mp))          { _mpResizing = true; _mpResizeStartMouse = mp; _mpResizeStartSize = _mpWinRT != null ? _mpWinRT.sizeDelta : new Vector2(MPW_W, MPW_H); }
                else if (RectHit(_mpOpacityTrackRT, mp))  { _mpOpacityDragging = true; ApplyOpacityFromMouse(mp); }   // before title: the track lives IN the bar
                else if (RectHit(_mpTitleRT, mp))         { _mpDragging = true; _mpDragLast = mp; }
                else if (RectHit(_mpSendRT, mp))          SubmitMpChat();
            }
            if (Input.GetMouseButtonUp(0)) { _mpDragging = false; _mpOpacityDragging = false; _mpResizing = false; }

            // Drag the window by the title bar.
            if (_mpDragging && _mpWinRT != null)
            {
                Vector2 d = mp - _mpDragLast;
                _mpDragLast = mp;
                var p = _mpWinRT.anchoredPosition + d;
                p.x = Mathf.Clamp(p.x, -(Screen.width  - 60f), 0f);
                p.y = Mathf.Clamp(p.y, -(Screen.height - 40f), 0f);
                _mpWinRT.anchoredPosition = p;
            }

            // Corner-grip resize — free width AND height (drag the bottom-left corner
            // out).  Changes the window's actual size (sizeDelta), NOT its scale, so
            // text stays the same size and the content reflows via its anchors.
            if (_mpResizing && _mpWinRT != null)
            {
                float w = _mpResizeStartSize.x + (_mpResizeStartMouse.x - mp.x);   // drag left  -> wider
                float h = _mpResizeStartSize.y + (_mpResizeStartMouse.y - mp.y);   // drag down  -> taller
                _mpWinRT.sizeDelta = new Vector2(Mathf.Clamp(w, 240f, 1100f), Mathf.Clamp(h, 150f, 1000f));
            }

            // Opacity slider drag.
            if (_mpOpacityDragging) ApplyOpacityFromMouse(mp);

            // Mouse-wheel scrollback over the chat area.
            if (RectHit(_mpChatPanelRT, mp))
            {
                float sw = Input.mouseScrollDelta.y;
                if (sw > 0f) _mpChatScroll += 3;
                else if (sw < 0f) _mpChatScroll -= 3;
            }

            // Mouse-wheel over the chips row scrolls it horizontally when many
            // (or long-named) players overflow the visible width.
            if (_mpChipsRowRT != null && RectHit(_mpChipsRowRT, mp))
            {
                float sw = Input.mouseScrollDelta.y;
                if (sw != 0f)
                {
                    float max = Mathf.Max(0f, _chipsTotalW - _mpChipsRowRT.rect.width);
                    _chipScroll = Mathf.Clamp(_chipScroll - sw * 40f, 0f, max);
                    if (_mpChipsContent != null)
                        _mpChipsContent.GetComponent<RectTransform>().anchoredPosition = new Vector2(-_chipScroll, 0f);
                }
            }

            if (_mpChatFocus && Input.GetKeyDown(KeyCode.Escape))
            {
                _mpChatInputField?.DeactivateInputField();
                _mpChatFocus = false;
            }

            // Player-movement block (typing WASD shouldn't walk the character) is owned by
            // LateUpdate now — it folds in both chat focus and the bug-report/crash popup.
        }

        /// <summary>Toggles the game's own navigation blocker so typing in chat
        /// doesn't drive the player.  Reuses PlayerController.SetNavigationBlocker —
        /// the same registry the game uses for Map/PurchaseUI/etc.  HelpSystem is
        /// chosen because it's unused in multiplayer (tutorial off), so it won't
        /// collide with the game's own blockers.</summary>
        private void SyncChatNavBlock(bool want)
        {
            if (want == _mpChatNavBlocked) return;
            try
            {
                var pc = PlayerHelper.PlayerController;
                if (pc == null) { _mpChatNavBlocked = false; return; }
                var m = pc.GetType().GetMethod(want ? "SetNavigationBlocker" : "UnsetNavigationBlocker",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (m == null) return;
                var enumType = m.GetParameters()[0].ParameterType;
                m.Invoke(pc, new[] { Enum.ToObject(enumType, 11) });   // NavigationBlocker.HelpSystem
                _mpChatNavBlocked = want;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] nav block: {ex.Message}"); }
        }

        private void ApplyOpacityFromMouse(Vector2 mp)
        {
            if (_mpOpacityTrackRT == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_mpOpacityTrackRT, mp, null, out var local);
            float w = _mpOpacityTrackRT.rect.width;
            float t = w > 0f ? Mathf.Clamp01(local.x / w) : _mpOpacity;
            SetMpOpacity(Mathf.Clamp(t, 0f, 1f));
        }

        /// <summary>Sets window opacity.  Fades ONLY the background + chrome images;
        /// the slider, roster text and chat stay fully visible (even at 0).</summary>
        private void SetMpOpacity(float o)
        {
            // Floor at 0.15 — at 0 the chrome is fully invisible and the window
            // looks broken (and a stray click on a not-yet-laid-out track could
            // land there without the user ever touching the slider).
            o = Mathf.Clamp(o, 0.15f, 1f);
            _mpOpacity = o;
            foreach (var (img, baseA) in _mpFade)
            {
                if (img == null) continue;
                var c = img.color; c.a = baseA * o; try { img.color = c; } catch { }
            }
            float w = _mpOpacityTrackRT != null ? _mpOpacityTrackRT.rect.width : 196f;
            if (_mpOpacityFillRT != null)
                _mpOpacityFillRT.sizeDelta = new Vector2(w * o, _mpOpacityFillRT.sizeDelta.y);
            if (_mpOpacityKnobRT != null)
                _mpOpacityKnobRT.anchoredPosition = new Vector2(w * o - 6f, _mpOpacityKnobRT.anchoredPosition.y);
        }

        private void SubmitMpChat()
        {
            if (_mpChatInputField != null) _mpChatInput = _mpChatInputField.text ?? "";
            string t = _mpChatInput.Trim();
            _mpChatInput = "";
            if (_mpChatInputField != null)
            {
                _mpChatInputField.SetTextWithoutNotify("");
                _mpChatInputField.ActivateInputField();
            }
            if (t.Length == 0) return;

            if (t.Equals("/bug", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("/bug ", StringComparison.OrdinalIgnoreCase))
            {
                string reason = t.Length > 4 ? t.Substring(4).Trim() : "";
                try
                {
                    var report = MPBugReport.Create(reason, discordTagIds: DefaultBugDiscordForumTagIds());
                    string msg = report.DiscordUploadQueued
                        ? "bug report saved and Discord upload queued"
                        : "bug report saved";
                    MPChat.AddNotice($"{msg}: {report.DirectoryPath}");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[BugReport] manual command failed: {ex.Message}");
                    MPChat.AddNotice("bug report failed: " + ex.Message);
                }
                return;
            }

#if BAMP_DEV
            // Dev builds ONLY (maintainer decision 2026-06-16): the intentional-crash test never ships in Release.
            if (t.Equals("/bugcrash", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("/bugcrash ", StringComparison.OrdinalIgnoreCase))
            {
                if (!MPConfig.AllowBugReportCrashTestLive())
                {
                    MPChat.AddNotice("crash test is disabled in config");
                    return;
                }
                string rest = t.Length > 9 ? t.Substring(9).Trim() : "";
                if (!rest.StartsWith("confirm", StringComparison.OrdinalIgnoreCase))
                {
                    MPChat.AddNotice("type /bugcrash confirm <reason> to intentionally close the game");
                    return;
                }
                string reason = rest.Length > 7 ? rest.Substring(7).Trim() : "manual crash test";
                MPBugReport.CrashForTest(reason);
                return;
            }
#endif

            // One-off whisper: "/w <name> <message>" (unique prefix match) —
            // sends private without changing the selected chip.
            if (t.StartsWith("/w ", StringComparison.OrdinalIgnoreCase))
            {
                string rest = t.Substring(3).TrimStart();
                int sp = rest.IndexOf(' ');
                if (sp > 0)
                {
                    string name = rest.Substring(0, sp);
                    string msg  = rest.Substring(sp + 1).Trim();
                    var lp = MPServer.IsRunning ? MPServer.LobbyPlayers : MPClient.LobbyPlayers;
                    string? match = null; int hits = 0;
                    foreach (var p in lp)
                        if (p != MPConfig.PlayerId && p.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { match = p; hits++; }
                    if (hits == 1 && msg.Length > 0) { try { MPChat.SendFromLocal(msg, match!); } catch { } return; }
                    MPChat.AddNotice(hits == 0 ? $"no player matching '{name}'" : $"'{name}' matches several players");
                    return;
                }
            }
            try { MPChat.SendFromLocal(t, _chatTarget); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] submit: {ex.Message}"); }
        }

        /// <summary>Rounds the window's corners with the menu's panel sprite + applies
        /// the game font.  Lazy: the sprite/font are captured when the native menu is
        /// injected (which is AFTER the window is built), so we apply them on first show.</summary>
        // ── Asset-lifetime hardening ──────────────────────────────────────────
        // The captured menu sprite/font are GAME-owned (Addressables) and get
        // destroyed on scene/view unloads — the client log showed the theme
        // capture completing TWICE at the menu (assets died between), and the
        // F9 window styled with a dead sprite renders blank (transparent bg, no
        // corners; a dead font blanks the glyphs too).  So: only ever use a
        // captured asset that is verifiably ALIVE, and fall back to a sprite WE
        // own (procedural rounded-rect, immortal) for the in-game window.

        /// <summary>True if a Unity object reference is non-null AND its native
        /// object still exists (interop == handles destroyed; the name probe
        /// covers any interop where it doesn't).</summary>
        private static bool IsAlive(UnityEngine.Object? o)
        {
            if (o is null || o == null) return false;
            try { _ = o.name; return true; } catch { return false; }
        }

        /// <summary>Mod-owned rounded-corner 9-slice sprite — generated once,
        /// flagged so Unity never unloads it.  White fill: Image.color tints it,
        /// so it drops in wherever the captured menu sprite was used.</summary>
        private static Sprite? _ownedRoundedSprite;
        private static Sprite? EnsureRoundedSprite()
        {
            if (_ownedRoundedSprite is not null && IsAlive(_ownedRoundedSprite)) return _ownedRoundedSprite;
            try
            {
                const int S = 24;       // texture size
                const float R = 8f;     // corner radius
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
                tex.hideFlags = HideFlags.HideAndDontSave;   // includes DontUnloadUnusedAsset
                for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    // Distance-based alpha: opaque except outside the rounded corners.
                    float cx = Mathf.Clamp(x + 0.5f, R, S - R);
                    float cy = Mathf.Clamp(y + 0.5f, R, S - R);
                    float d  = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float a  = Mathf.Clamp01(R - d + 0.5f);   // 1px soft edge
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
                tex.Apply();
                Sprite sp;
                try
                {
                    sp = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f),
                                       100f, 0, SpriteMeshType.FullRect,
                                       new Vector4(R + 1f, R + 1f, R + 1f, R + 1f));   // 9-slice border
                }
                catch
                {
                    sp = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f));
                }
                sp.hideFlags = HideFlags.HideAndDontSave;
                _ownedRoundedSprite = sp;
                Plugin.Logger.LogInfo("[MPWin] Generated owned rounded sprite (fallback theme).");
                return sp;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPWin] EnsureRoundedSprite: {ex.Message}"); return null; }
        }

        private Sprite? _mpStyledWith;   // sprite the window was last styled with — restyle if a better capture lands
        private void StyleMpWindow()
        {
            if (_mpWin == null) return;
            // Native menu sprite when it's still alive, else our own immortal one.
            var sprite = IsAlive(_panelSprite) ? _panelSprite : EnsureRoundedSprite();
            if (sprite == null) return;
            if (_mpStyled && ReferenceEquals(_mpStyledWith, sprite)) return;
            try
            {
                foreach (var img in _mpWin.GetComponentsInChildren<Image>(true))
                { try { img.sprite = sprite; img.type = Image.Type.Sliced; } catch { } }
                bool fontAlive = IsAlive(_gameFont);
                if (fontAlive)
                    foreach (var t in _mpWin.GetComponentsInChildren<TMP_Text>(true)) ApplyFont(t);
                _mpStyled = true;
                _mpStyledWith = sprite;
                Plugin.Logger.LogInfo($"[MPWin] styled (sprite={(ReferenceEquals(sprite, _panelSprite) ? "native" : "owned-fallback")}, font={(fontAlive ? "game" : "default")}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPWin] style: {ex.Message}"); }
        }

        /// <summary>
        /// Builds the full-screen "waiting for players" overlay shown during the
        /// startup pause hold.  Dims the game and names the players still loading.
        /// </summary>
        private void BuildStartupScreen(Transform canvasRoot)
        {
            _startupScreenGO = MakeGO("BAMP_StartupScreen", canvasRoot);
            var rt = _startupScreenGO.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;

            var bg = _startupScreenGO.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.82f);

            // Centred message — fills the screen, centre-aligned.
            var txtGO = MakeGO("Txt", _startupScreenGO.transform);
            var trt = txtGO.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            _startupScreenTxt = txtGO.AddComponent<TextMeshProUGUI>();
            _startupScreenTxt.fontSize  = 26;
            _startupScreenTxt.color     = new Color(1f, 0.95f, 0.7f, 1f);
            _startupScreenTxt.alignment = TextAlignmentOptions.Center;
            _startupScreenTxt.text      = "Multiplayer";

            _startupScreenGO.SetActive(false);
        }

        // ── Game settings editor panel ────────────────────────────────────────

        private const float SET_W = 470f;

        /// <summary>
        /// Builds the modal "Game Settings" overlay — every GameVariables setting
        /// shown and editable.  Bools toggle; numerics step with &lt; &gt; or are
        /// clicked and typed directly.  Hovering a name shows a description.
        /// Editing any value flips the difficulty to "Custom".
        /// </summary>
        private void BuildSettingsPanel(Transform canvasRoot)
        {
            _settingsHits.Clear();
            _settingsRefreshers.Clear();
            _settingsTips.Clear();
            _numFields.Clear();
            _editField = -1;

            _settingsPanelGO = MakeGO("BAMP_Settings", canvasRoot);
            var brt = _settingsPanelGO.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = brt.offsetMax = Vector2.zero;
            _settingsPanelGO.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);

            var content = MakeGO("Content", _settingsPanelGO.transform);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 1f);
            _settingsContentImg = content.AddComponent<Image>();
            _settingsContentImg.color = new Color(0.10f, 0.10f, 0.13f, 0.98f);
            var ct = content.transform;

            float y = -PAD;
            MakeLabel(ct, "Multiplayer Game Settings", SZ_HDR, C_WHITE,
                      PAD, y, SET_W - PAD * 2f, HDR, TextAlignmentOptions.Center);
            y -= HDR;
            MakeLabel(ct, "Hover a name for a description  •  click a value to type it",
                      SZ_STS, C_LBLGREY, PAD, y, SET_W - PAD * 2f, LH, TextAlignmentOptions.Center);
            y -= LADV + 2f;

            SettingsHeader(ct, ref y, "WORLD — applies to everyone");
            SettingsNumRow (ct, ref y, "Tax %", "Percent of profit paid as income tax each period.",
                            () => _hostSettings.TaxPercentage, v => _hostSettings.TaxPercentage = (int)v, 1f, 0f, 50f, "0");
            SettingsNumRow (ct, ref y, "Days per year", "In-game days per year. Affects aging and yearly events.",
                            () => _hostSettings.DaysPerYear, v => _hostSettings.DaysPerYear = (int)v, 5f, 10f, 365f, "0");
            SettingsNumRow (ct, ref y, "Market price x", "Global multiplier on product market prices.",
                            () => _hostSettings.MarketPriceMultiplier, v => _hostSettings.MarketPriceMultiplier = v, 0.05f, 0f, 5f, "0.00");
            SettingsNumRow (ct, ref y, "Employee salary x", "Multiplier on employee wages. Higher = staff cost more.",
                            () => _hostSettings.EmployeeHourlySalaryMultiplier, v => _hostSettings.EmployeeHourlySalaryMultiplier = v, 0.05f, 0f, 5f, "0.00");
            SettingsNumRow (ct, ref y, "Bank interest x", "Multiplier applied to bank interest amounts.",
                            () => _hostSettings.BankInterestMultiplier, v => _hostSettings.BankInterestMultiplier = v, 0.05f, 0f, 5f, "0.00");
            SettingsNumRow (ct, ref y, "Bank interest rate", "Base bank rate. Negative means you pay to hold a balance.",
                            () => _hostSettings.BankInterestRate, v => _hostSettings.BankInterestRate = v, 0.05f, -2f, 2f, "0.00");
            SettingsNumRow (ct, ref y, "Rival difficulty x", "Strength of AI rival companies. Higher = tougher rivals.",
                            () => _hostSettings.RivalsDifficultyMultiplier, v => _hostSettings.RivalsDifficultyMultiplier = v, 0.05f, 0f, 5f, "0.00");
            SettingsNumRow (ct, ref y, "Customer promotion x", "Effectiveness of marketing at attracting customers.",
                            () => _hostSettings.BaseCustomerPromotionMultiplier, v => _hostSettings.BaseCustomerPromotionMultiplier = v, 0.05f, 0f, 5f, "0.00");
            SettingsNumRow (ct, ref y, "Wholesale urgent fee x", "Surcharge multiplier for rush (urgent) wholesale orders.",
                            () => _hostSettings.WholesaleUrgentFeeMultiplier, v => _hostSettings.WholesaleUrgentFeeMultiplier = v, 0.05f, 0f, 5f, "0.00");
            SettingsNumRow (ct, ref y, "Importer urgent fee x", "Surcharge multiplier for rush (urgent) importer orders.",
                            () => _hostSettings.ImporterUrgentFeeMultiplier, v => _hostSettings.ImporterUrgentFeeMultiplier = v, 0.05f, 0f, 5f, "0.00");
            SettingsNumRow (ct, ref y, "Export x", "Multiplier on revenue earned from exporting goods.",
                            () => _hostSettings.ExportMultiplier, v => _hostSettings.ExportMultiplier = v, 0.05f, 0f, 5f, "0.00");
            SettingsBoolRow(ct, ref y, "No wholesale/import limits", "Removes per-order quantity caps on wholesale and imports.",
                            () => _hostSettings.DisableWholesaleAndImportLimits, v => _hostSettings.DisableWholesaleAndImportLimits = v);
            SettingsBoolRow(ct, ref y, "All importer products", "Every product is importable from the start of the game.",
                            () => _hostSettings.AllProductsAvailableFromImporters, v => _hostSettings.AllProductsAvailableFromImporters = v);

            // NOTE: Starting age + starting money are now set per-player in the
            // multiplayer lobby roster, not here (age is self-chosen, cash is
            // host-dictated), so they're intentionally omitted from this page.
            SettingsHeader(ct, ref y, "PLAYER — per character");
            SettingsBoolRow(ct, ref y, "Disable aging", "Your character never grows older.",
                            () => _hostSettings.DisableAging, v => _hostSettings.DisableAging = v);
            SettingsBoolRow(ct, ref y, "Disable energy need", "Removes the sleep/energy need. Required for multiplayer.",
                            () => _hostSettings.DisableEnergy, v => _hostSettings.DisableEnergy = v);
            SettingsBoolRow(ct, ref y, "Disable happiness need", "Removes the happiness need from your character.",
                            () => _hostSettings.DisableHappiness, v => _hostSettings.DisableHappiness = v);
            SettingsBoolRow(ct, ref y, "All courses unlocked", "Every education course is available immediately.",
                            () => _hostSettings.AllCoursesUnlocked, v => _hostSettings.AllCoursesUnlocked = v);
            SettingsBoolRow(ct, ref y, "All contacts unlocked", "Every business contact is available immediately.",
                            () => _hostSettings.AllContactsUnlocked, v => _hostSettings.AllContactsUnlocked = v);
            SettingsBoolRow(ct, ref y, "Disable vehicle damage", "Your vehicles never take damage.",
                            () => _hostSettings.DisableVehicleDamage, v => _hostSettings.DisableVehicleDamage = v);
            SettingsBoolRow(ct, ref y, "Disable vehicle fuel", "Your vehicles never consume fuel.",
                            () => _hostSettings.DisableVehicleFuel, v => _hostSettings.DisableVehicleFuel = v);
            // Tutorial is forced OFF in multiplayer (story quests desync), so it's not exposed here.

            y -= SGAP;
            var closeGO = MakeGO("Close", ct);
            _rtSettingsClose = closeGO.GetComponent<RectTransform>();
            SetAnchored(_rtSettingsClose, PAD, y, SET_W - PAD * 2f, BH);
            closeGO.AddComponent<Image>().color = C_BTNBLUE;
            MakeLabel(closeGO.transform, "Close", SZ_BTN, C_WHITE,
                      0f, 0f, SET_W - PAD * 2f, BH, TextAlignmentOptions.Center);
            y -= BADV;

            float h = -y + PAD;
            crt.sizeDelta        = new Vector2(SET_W, h);
            crt.anchoredPosition = new Vector2(0f, -Mathf.Round(Mathf.Max(0f, (Screen.height - h) / 2f)));

            // Tooltip — follows the cursor while hovering a setting name.
            _tooltipGO = MakeGO("Tooltip", _settingsPanelGO.transform);
            var ttrt = _tooltipGO.GetComponent<RectTransform>();
            ttrt.anchorMin = ttrt.anchorMax = ttrt.pivot = new Vector2(0f, 1f);
            ttrt.sizeDelta = new Vector2(340f, 62f);
            _tooltipGO.AddComponent<Image>().color = new Color(0.04f, 0.04f, 0.06f, 0.98f);
            _tooltipTxt = MakeLabel(_tooltipGO.transform, "", SZ_STS, C_WHITE,
                                    8f, -5f, 324f, 52f, TextAlignmentOptions.TopLeft);
            _tooltipGO.SetActive(false);

            _settingsPanelGO.SetActive(false);
        }

        private void SettingsHeader(Transform p, ref float y, string text)
        {
            y -= 4f;
            MakeLabel(p, text, SZ_LBL, C_YELLOW, PAD, y, SET_W - PAD * 2f, LH,
                      TextAlignmentOptions.Left);
            y -= LADV;
        }

        private void SettingsBoolRow(Transform p, ref float y, string label, string desc,
                                     System.Func<bool> get, System.Action<bool> set)
        {
            var nameLbl = MakeLabel(p, label, SZ_LBL, C_LBLGREY, PAD, y, 250f, LH,
                                    TextAlignmentOptions.Left);
            _settingsTips.Add((nameLbl.rectTransform, desc));

            var go  = MakeGO("t", p);
            var rt  = go.GetComponent<RectTransform>();
            SetAnchored(rt, 280f, y, 142f, LH);
            var img = go.AddComponent<Image>();
            var lbl = MakeLabel(go.transform, "", SZ_LBL, C_WHITE, 0f, 0f, 142f, LH,
                                TextAlignmentOptions.Center);

            void Render()
            {
                bool v = get();
                lbl.text  = v ? "ON" : "OFF";
                img.color = v ? new Color(0.20f, 0.45f, 0.25f, 1f)
                              : new Color(0.40f, 0.22f, 0.22f, 1f);
            }
            Render();
            _settingsHits.Add((rt, () => { set(!get()); MarkCustom(); Render(); }));
            _settingsRefreshers.Add(Render);
            y -= LADV;
        }

        private void SettingsNumRow(Transform p, ref float y, string label, string desc,
                                    System.Func<float> get, System.Action<float> set,
                                    float step, float min, float max, string fmt)
        {
            var nameLbl = MakeLabel(p, label, SZ_LBL, C_LBLGREY, PAD, y, 228f, LH,
                                    TextAlignmentOptions.Left);
            _settingsTips.Add((nameLbl.rectTransform, desc));

            var decGO = MakeGO("-", p);
            var decRt = decGO.GetComponent<RectTransform>();
            SetAnchored(decRt, 244f, y, 32f, LH);
            decGO.AddComponent<Image>().color = C_BTNBLUE;
            MakeLabel(decGO.transform, "<", SZ_BTN, C_WHITE, 0f, 0f, 32f, LH,
                      TextAlignmentOptions.Center);

            // Value field — click to type a value directly.
            var fieldGO = MakeGO("val", p);
            var fieldRt = fieldGO.GetComponent<RectTransform>();
            SetAnchored(fieldRt, 280f, y, 142f, LH);
            fieldGO.AddComponent<Image>().color = C_FIELD;
            var valLbl = MakeLabel(fieldGO.transform, "", SZ_LBL, C_YELLOW, 0f, 0f, 142f, LH,
                                   TextAlignmentOptions.Center);

            var incGO = MakeGO("+", p);
            var incRt = incGO.GetComponent<RectTransform>();
            SetAnchored(incRt, 426f, y, 32f, LH);
            incGO.AddComponent<Image>().color = C_BTNBLUE;
            MakeLabel(incGO.transform, ">", SZ_BTN, C_WHITE, 0f, 0f, 32f, LH,
                      TextAlignmentOptions.Center);

            void Render() { valLbl.text = get().ToString(fmt); }
            Render();
            _settingsHits.Add((decRt, () => { set(Mathf.Clamp(get() - step, min, max)); MarkCustom(); Render(); }));
            _settingsHits.Add((incRt, () => { set(Mathf.Clamp(get() + step, min, max)); MarkCustom(); Render(); }));
            _settingsRefreshers.Add(Render);
            _numFields.Add(new NumField
            {
                Rt = fieldRt, Lbl = valLbl, Get = get, Set = set, Min = min, Max = max, Fmt = fmt
            });
            y -= LADV;
        }

        // ── Settings panel — per-frame tooltip + click-to-type editing ────────

        private void TickSettingsPanel()
        {
            if (!_settingsOpen)
            {
                if (_tooltipGO != null && _tooltipGO.activeSelf) _tooltipGO.SetActive(false);
                return;
            }

            var ms = new Vector2(Input.mousePosition.x, Input.mousePosition.y);

            // Hover → tooltip following the cursor
            string? desc = null;
            foreach (var t in _settingsTips)
                if (RectHit(t.rt, ms)) { desc = t.desc; break; }

            if (_tooltipGO != null && _tooltipTxt != null)
            {
                if (desc != null)
                {
                    _tooltipTxt.text = desc;
                    var ttrt = (RectTransform)_tooltipGO.transform;
                    float tw = ttrt.sizeDelta.x, th = ttrt.sizeDelta.y;
                    // Place to the LEFT of the cursor — a cursor's body extends
                    // down-right from its hotspot, so its left side is always clear.
                    const float gap = 24f;
                    float cx = ms.x - gap - tw;
                    float cy = ms.y - 8f;
                    if (cx < 0f) cx = ms.x + gap + 56f;          // no room left — go well clear to the right
                    if (cx + tw > Screen.width) cx = Screen.width - tw;
                    if (cy - th < 0f) cy = th;                   // keep fully on-screen
                    ttrt.anchoredPosition = new Vector2(cx, cy - Screen.height);
                    if (!_tooltipGO.activeSelf) _tooltipGO.SetActive(true);
                }
                else if (_tooltipGO.activeSelf) _tooltipGO.SetActive(false);
            }

            // While typing into a field, show the buffer with a cursor.
            if (_editField >= 0 && _editField < _numFields.Count)
                _numFields[_editField].Lbl.text = _editBuffer + "|";
        }

        private void BeginSettingsEdit(int i)
        {
            CommitSettingsEdit();
            _editField  = i;
            _editBuffer = _numFields[i].Get()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void CommitSettingsEdit()
        {
            if (_editField < 0 || _editField >= _numFields.Count) { _editField = -1; return; }
            var f = _numFields[_editField];
            if (float.TryParse(_editBuffer, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float v))
            {
                f.Set(Mathf.Clamp(v, f.Min, f.Max));
                MarkCustom();
            }
            f.Lbl.text  = f.Get().ToString(f.Fmt);   // re-render the committed value
            _editField  = -1;
            _editBuffer = "";
        }

        /// <summary>
        /// Shows the full-screen startup overlay while the game is held waiting for
        /// players to finish loading, naming whoever is still loading.  Hides itself
        /// the moment the hold is released.
        /// </summary>
        private float _startupDiagTimer;
        private bool  _startupScreenWasShown;
        private float _startupHeldSince;

        private void TickStartupScreen()
        {
            if (_startupScreenGO == null) return;

            // Host-loss notice (set on involuntary in-game disconnect) reuses this
            // overlay — checked first so it wins over the startup-wait text.  The
            // game stays frozen (LateUpdate clamp) only while the notice is up;
            // dismissing it lets the player continue OFFLINE as a single-player
            // fork (allowed by design — the MP session is unaffected: the host's
            // copies are canonical and overwrite this world on the next rejoin).
            if (MPClient.SessionEnded)
            {
                _startupScreenGO.SetActive(true);
                _startupScreenWasShown = true;
                if (_startupScreenTxt != null)
                    _startupScreenTxt.text =
                        "<size=36><b>Multiplayer</b></size>\n\n" +
                        "<color=#FF7070>Connection to the host was lost.</color>\n\n" +
                        "The multiplayer session has ended — it resumes from the\n" +
                        "host's last save when the host returns.\n\n" +
                        "<size=17><color=#AAAAAA>You can keep playing this world offline as a single-player copy.\n" +
                        "Progress made here will NOT count toward the multiplayer session;\n" +
                        "saves from now on go to your single-player games.</color></size>\n\n" +
                        "<size=20><color=#FFD24A>Click (or press Enter) to continue offline</color></size>";

                if (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Return))
                {
                    MPClient.SessionEnded = false;                  // lifts the freeze clamp
                    MPClient.InMpGame     = false;                  // committed to the offline solo fork → native time allowed
                    GameStateReader.SetNativePause(false);          // lift the true pause too
                    MPSaveCoordinator.AllowNativeAutosave();        // the suppress flag is sticky — re-enable SP autosave
                    _startupScreenGO.SetActive(false);
                    _startupScreenWasShown = false;
                    Plugin.Logger.LogInfo("[UI] Host-loss notice dismissed — continuing OFFLINE (single-player fork; MP session unaffected).");
                }
                return;
            }

            // Disconnect-pause overlay (host): a player dropped (timeout-style)
            // and the session auto-paused — say WHO and how to proceed, instead
            // of the silent freeze the user mistook for a hang.  Cleared by the
            // player reconnecting (auto) or by clicking to keep playing.
            if (MPServer.IsRunning && MPServer.PausedByDisconnect)
            {
                _startupScreenGO.SetActive(true);
                _startupScreenWasShown = true;
                if (_startupScreenTxt != null)
                    _startupScreenTxt.text =
                        "<size=36><b>Multiplayer</b></size>\n\n" +
                        $"<color=#FFD24A>{MPServer.DisconnectPauseWho} lost connection.</color>\n\n" +
                        "Game paused.  They can rejoin from the multiplayer menu and will\n" +
                        "continue from their last save (money and properties stay current).\n\n" +
                        "<size=20><color=#FFD24A>Click (or press Enter) to keep playing without them</color></size>";
                if (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Return))
                {
                    MPServer.ResumeFromDisconnectPause();
                    _startupScreenGO.SetActive(false);
                    _startupScreenWasShown = false;
                }
                return;
            }

            // Suppress the one-frame flash on an INSTANT-release hold: hosting alone
            // (no one to wait for) begins AND ends the startup hold within ~1 frame,
            // so the full-screen "waiting for players" overlay would flash for that
            // single frame (the "~8s flicker").  Only show it once the hold has
            // actually persisted a beat — a genuine multi-player wait lasts seconds.
            // Unscaled time, because it must keep ticking through the timeScale=0 freeze.
            bool held = (MPServer.IsRunning || MPClient.IsConnected) && TimeSync.IsStartupHeld;
            if (!held) _startupHeldSince = 0f;
            else if (_startupHeldSince == 0f) _startupHeldSince = Time.unscaledTime;
            bool show = held && (Time.unscaledTime - _startupHeldSince) > 0.3f;
            _startupScreenGO.SetActive(show);

            // Diagnostics: log show transitions + sample the real timeScale/screen state
            // each second while held, so we can see if the freeze + wait screen actually
            // engaged (the "no freeze" report).
            if (show != _startupScreenWasShown)
            {
                _startupScreenWasShown = show;
                MPLoadProfiler.Mark($"WAIT SCREEN {(show ? "SHOWN" : "hidden")} (timeScale={Time.timeScale}, screenActive={_startupScreenGO.activeInHierarchy})");
            }
            if (show)
            {
                _startupDiagTimer -= Time.unscaledDeltaTime;
                if (_startupDiagTimer <= 0f)
                {
                    _startupDiagTimer = 1f;
                    MPLoadProfiler.Mark($"WAIT held: timeScale={Time.timeScale}, screenActive={_startupScreenGO.activeInHierarchy}, inGame={IsInGame()}, parkedGhosts={ParkedVehicleSync.ClientGhostCount}");
                }
            }

            if (!show || _startupScreenTxt == null) return;

            var waiting = MPServer.IsRunning
                ? MPServer.GetStartupWaitingFor()
                : MPClient.StartupWaitingFor;

            string body = (waiting != null && waiting.Count > 0)
                ? "Waiting for these players to finish loading:\n\n" +
                  string.Join("\n", waiting.Select(n => $"<color=#FFD24A>{MPNames.Resolve(n)}</color>"))
                : "Waiting for all players to finish loading…";

            // Countdown to the force-release (host knows the elapsed time;
            // clients show the host-side limit as an estimate).
            float remaining = Mathf.Max(0f, STARTUP_HOLD_TIMEOUT - _startupHoldElapsed);
            string countdown = MPServer.IsRunning
                ? $"\n\n<size=20><color=#FFD24A>Auto-start in {(int)remaining / 60}:{(int)remaining % 60:D2}</color></size>"
                : "";

            _startupScreenTxt.text =
                "<size=36><b>Multiplayer</b></size>\n\n" +
                body +
                countdown +
                "\n\n<size=17><color=#AAAAAA>The game starts automatically " +
                "once everyone is ready.</color></size>";
        }

        // (Legacy F8 panel pane-builders + state-panel refreshers EXCISED 2026-06-11 —
        //  the subsystem was unreachable dead code; the native menu/lobby is the real UI.)

        // ── Button handlers ───────────────────────────────────────────────────

        private void OnHost()
        {
            if (!int.TryParse(_port, out int p) || p < 1024 || p > 65535)
            { SetStatus("Invalid port.", true); return; }
            if (string.IsNullOrWhiteSpace(_name))
            { SetStatus("Enter a player name.", true); return; }
            MPConfig.SetRuntime(_name.Trim(), null, p);
            if (!MPServer.Start(p))
            { SetStatus($"Hosting FAILED on port {p} (port in use?).", true); return; }
            SetStatus($"Hosting on port {p} — waiting for players.", false);
        }

        private void OnJoin()
        {
            if (!int.TryParse(_port, out int p) || p < 1024 || p > 65535)
            { SetStatus("Invalid port.", true); return; }
            if (string.IsNullOrWhiteSpace(_ip))
            { SetStatus("Enter a host IP.", true); return; }
            if (string.IsNullOrWhiteSpace(_name))
            { SetStatus("Enter a player name.", true); return; }
            MPConfig.SetRuntime(_name.Trim(), _ip.Trim(), p);
            MPClient.Connect(_ip.Trim(), p);
            SetStatus("Connecting...", false);
        }

        private void OnStartNew()
        {
            MPServer.StartNewGame(_hostSettings);
            SetStatus($"Starting new game ({_hostSettings.Difficulty})...", false);
        }
        private void OnStartLoad() { MPServer.StartLoadGame(); SetStatus("Loading save...", false); }

        private void OnCycleDifficulty()
        {
            int i = System.Array.IndexOf(_difficulties, _selectedDifficulty);
            _selectedDifficulty = _difficulties[(i + 1) % _difficulties.Length];
            _hostSettings = MPServer.Preset(_selectedDifficulty);
            UpdateDifficultyLabel();
            RefreshSettingsPanel();
            Plugin.Logger.LogInfo($"[UI] Difficulty preset: {_selectedDifficulty}.");
        }

        private void UpdateDifficultyLabel()
        {
            // (legacy F8 difficulty label excised 2026-06-11 — the native
            //  lobby's HighlightDifficulty owns the live display)
        }

        /// <summary>Called when the host hand-edits a setting — difficulty becomes "Custom".</summary>
        private void MarkCustom()
        {
            _hostSettings.Difficulty = "Custom";
            UpdateDifficultyLabel();
        }

        private void RefreshSettingsPanel()
        {
            foreach (var r in _settingsRefreshers) r();
        }

        private void OnOpenSettings()
        {
            _settingsOpen = true;
            RefreshSettingsPanel();
            StyleSettingsPanel();   // native font + rounded panel (lazy — once assets are captured)
            if (_settingsPanelGO != null)
            {
                _settingsPanelGO.transform.SetAsLastSibling();   // draw ABOVE the lobby window
                _settingsPanelGO.SetActive(true);
            }
            // Hide the lobby behind the full-screen modal so its buttons don't take
            // clicks through the overlay (and so the panel isn't visually obscured).
            if (_lobbyWindow != null && _lobbyWindow.activeSelf) { _settingsHidLobby = true; _lobbyWindow.SetActive(false); }
        }

        private void OnCloseSettings()
        {
            CommitSettingsEdit();
            _settingsOpen = false;
            if (_settingsPanelGO != null) _settingsPanelGO.SetActive(false);
            // Return to the lobby if we came from it.
            if (_settingsHidLobby) { _settingsHidLobby = false; if (_lobbyWindow != null) _lobbyWindow.SetActive(true); }
        }

        /// <summary>Lazily restyle the settings panel to match the native menu: apply
        /// the captured game font to every label and round the content background
        /// with the menu's panel sprite.  These assets are captured when the native
        /// Multiplayer button is injected (at the main menu), which is AFTER the panel
        /// is built — so we apply them on first open, retrying until both are ready.</summary>
        private bool _settingsStyled;
        private void StyleSettingsPanel()
        {
            if (_settingsStyled || _settingsPanelGO == null) return;
            if (_gameFont == null && _panelSprite == null) return;   // not captured yet — retry next open
            try
            {
                foreach (var t in _settingsPanelGO.GetComponentsInChildren<TMP_Text>(true)) ApplyFont(t);
                // Round every panel element (content bg, buttons, value fields) with the
                // menu's sprite so the page matches the native menu — no sharp corners.
                if (_settingsContentImg != null && _panelSprite != null)
                    foreach (var img in _settingsContentImg.GetComponentsInChildren<Image>(true))
                    { try { img.sprite = _panelSprite; img.type = Image.Type.Sliced; } catch { } }
                if (_gameFont != null && _panelSprite != null) _settingsStyled = true;   // both applied → done
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[UI] StyleSettingsPanel: {ex.Message}"); }
        }
        private void OnStop()      { MPServer.Stop();          SetStatus("Stopped hosting.", false); }
        private void OnDisc()      { MPClient.Disconnect();    SetStatus("Disconnected.", false); }

        private void OnToggleEnforceCash()
        {
            MPServer.SetEnforceStartingCash(!MPServer.EnforceStartingCash);
            // (legacy enforce-cash label excised 2026-06-11)
        }


        // ── State machine ─────────────────────────────────────────────────────

        private static MPState State()
        {
            if (MPClient.IsConnecting)                        return MPState.Connecting;
            if (MPServer.IsRunning &&  MPServer.IsInLobby)   return MPState.LobbyHost;
            if (MPServer.IsRunning && !MPServer.IsInLobby)   return MPState.Hosting;
            if (MPClient.IsConnected &&  MPClient.IsInLobby) return MPState.LobbyClient;
            if (MPClient.IsConnected && !MPClient.IsInLobby) return MPState.Connected;
            return MPState.Idle;
        }

        private void SetStatus(string msg, bool err)
        {
            if (_txtStatus != null)
            {
                _txtStatus.text  = msg;
                _txtStatus.color = err ? C_RED : C_GREEN;
            }
            Plugin.Logger.LogInfo($"[UI] {msg}");
        }

        // ── Coordinate helpers ────────────────────────────────────────────────

        /// Convert raw Input.mousePosition (y=0 at bottom) to canvas anchoredPosition
        /// units (y=0 at top of screen, negative downward).
        /// With ConstantPixelSize/scaleFactor=1 this is: x unchanged, y -= Screen.height.
        private static Vector2 ScreenToCanvas(Vector2 screen) =>
            new Vector2(screen.x, screen.y - Screen.height);

        private static bool RectHit(RectTransform rt, Vector2 screenPos)
        {
            if (rt == null) return false;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rt, screenPos, null, out var local);
            return rt.rect.Contains(local);
        }

        // ── UI factory helpers ────────────────────────────────────────────────

        private static GameObject MakeGO(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        private static void SetAnchored(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot     = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta        = new Vector2(w, h);
        }


        private static TextMeshProUGUI MakeLabel(Transform parent, string text, int size,
            Color color, float x, float y, float w, float h, TextAlignmentOptions align)
        {
            var go  = MakeGO("Lbl", parent);
            SetAnchored(go.GetComponent<RectTransform>(), x, y, w, h);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text         = text;
            tmp.fontSize     = size;
            tmp.color        = color;
            tmp.alignment    = align;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        private (Image img, TextMeshProUGUI lbl, RectTransform rt) MakeField(
            Transform parent, string initial, float x, float y, float w, float h)
        {
            var go  = MakeGO("Field", parent);
            var rt  = go.GetComponent<RectTransform>();
            SetAnchored(rt, x, y, w, h);
            var img = go.AddComponent<Image>();
            img.color = C_FIELD;
            var lbl = MakeLabel(go.transform, initial, SZ_FLD, C_WHITE,
                                4f, 0f, w - 8f, h, TextAlignmentOptions.Left);
            return (img, lbl, rt);
        }

        private static (Image img, RectTransform rt) MakeButton(
            Transform parent, string label, Color color,
            float x, float y, float w, float h)
        {
            var go  = MakeGO("Btn", parent);
            var rt  = go.GetComponent<RectTransform>();
            SetAnchored(rt, x, y, w, h);
            var img = go.AddComponent<Image>();
            img.color = color;
            MakeLabel(go.transform, label, SZ_BTN, C_WHITE, 0f, 0f, w, h,
                      TextAlignmentOptions.Center);
            return (img, rt);
        }

        private static void MakeButton(Transform parent, string label, Color color,
            float x, float y, float w, float h, out RectTransform rt)
        {
            var (_, r) = MakeButton(parent, label, color, x, y, w, h);
            rt = r;
        }

        private enum MPState { Idle, LobbyHost, LobbyClient, Hosting, Connected, Connecting }
    }
}
