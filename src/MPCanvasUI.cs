using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Helpers;
using Intro;

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
        public MPCanvasUI(IntPtr ptr) : base(ptr) { }

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
        private bool   _visible = true;
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
        private const float STARTUP_HOLD_TIMEOUT = 90f;

        // Sends the local player's appearance once, after the character is ready.
        private bool _localAppearanceSent;

        // ── periodic host-side sync timers ────────────────────────────────────
        /// <summary>Counts down to next game-time/speed heartbeat (host, every 3 s).</summary>
        private float _timeSyncTimer = 3f;
        /// <summary>Counts down to next market broadcast (host, ~60 s).</summary>
        private float _marketSyncTimer = 60f;

        // ── in-game player HUD (F9 toggle) ────────────────────────────────────
        private bool        _hudVisible;
        private GameObject? _hudGO;
        private TextMeshProUGUI?   _hudHeader;
        private TextMeshProUGUI[]  _hudRows = new TextMeshProUGUI[8];

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
        private GameObject _paneIdle       = null!;
        private GameObject _paneLobbyHost  = null!;
        private GameObject _paneLobbyClient= null!;
        private GameObject _paneHosting    = null!;
        private GameObject _paneConnected  = null!;
        private GameObject _paneConnecting = null!;

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
        private TextMeshProUGUI _txtLHInfo      = null!;
        private TextMeshProUGUI[] _txtLHSlots   = new TextMeshProUGUI[4];
        private TextMeshProUGUI _txtLHDifficulty = null!;
        private RectTransform   _rtLHDifficulty  = null!;
        private TextMeshProUGUI _txtLHEnforceCash = null!;
        private RectTransform   _rtLHEnforceCash  = null!;
        private Image           _imgClientCash    = null!;
        private TextMeshProUGUI _txtClientCash    = null!;
        private RectTransform   _rtClientCash     = null!;
        private string          _clientCash       = "4200";
        private string _selectedDifficulty = "Normal";
        private static readonly string[] _difficulties = { "Easy", "Normal", "Hard" };

        // ── Game settings editor ──────────────────────────────────────────────
        private GameVariablesDto _hostSettings = MPServer.Preset("Normal");
        private GameObject?   _settingsPanelGO;
        private RectTransform _rtLHSettings    = null!;
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
        private RectTransform   _rtLHStartNew   = null!;
        private RectTransform   _rtLHStartLoad  = null!;
        private RectTransform   _rtLHStop       = null!;

        // ── Lobby-client live labels ──────────────────────────────────────────
        private TextMeshProUGUI _txtLCInfo      = null!;
        private TextMeshProUGUI[] _txtLCSlots   = new TextMeshProUGUI[4];
        private RectTransform   _rtLCDisc       = null!;

        // ── In-game host labels ───────────────────────────────────────────────
        private TextMeshProUGUI _txtHostAs      = null!;
        private TextMeshProUGUI _txtHostPort    = null!;
        private TextMeshProUGUI _txtHostPlayers = null!;
        private RectTransform   _rtStopBtn      = null!;

        // ── In-game client labels ─────────────────────────────────────────────
        private TextMeshProUGUI _txtConnAs      = null!;
        private TextMeshProUGUI _txtConnHost    = null!;
        private RectTransform   _rtDiscBtn      = null!;

        // ── Connecting labels ─────────────────────────────────────────────────
        private TextMeshProUGUI _txtConnecting  = null!;
        private RectTransform   _rtCancelBtn    = null!;

        // ── Status bar ────────────────────────────────────────────────────────
        private TextMeshProUGUI _txtStatus      = null!;

        // ── hit-test rect for title bar ───────────────────────────────────────
        private RectTransform _rtHdr = null!;

        // ── deferred init ─────────────────────────────────────────────────────
        private bool _built;
        private int  _initDelay;

        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
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
        }

        // ── Update ────────────────────────────────────────────────────────────

        private void Update()
        {
            // Startup pause screen — full-screen "waiting for players" overlay
            TickStartupScreen();

            // Settings panel — hover tooltips + click-to-type field cursor
            TickSettingsPanel();

            // Drain the main-thread action queue
            // NOTE: TickTimeScaleMonitor was moved to LateUpdate so it runs AFTER Unity's
            // EventSystem processes button clicks (speed buttons, pause, sleep, rest).
            // EventSystem fires after ALL Update() calls, so Update-phase monitoring is
            // always 1 frame behind any button-click-driven timeScale change. — runs in ALL scenes (GameManager.Update
            // only exists in-game; this canvas is DontDestroyOnLoad so it's always active).
            GameStatePatcher.DrainQueue();

            // Per-frame clock drift correction (drips small adjustments in if scheduled)
            TimeSync.TickClockCorrection();

            // Player sync ticks — run regardless of panel visibility or build state
            TickGameLoadDetect();
            TickStartupTimeout();
            TickWorldSnapshot();
            TickPositionSync();
            TickTimeSync();
            TickMarketSync();
            TickIntroNamePrefill();

            // Auto-hide when the game starts so our canvas doesn't block the intro
            // scene's "Start Game" button or any in-game UI.
            // The user can still press F8 to bring the panel back at any time.
            if (_built && _canvasGO != null)
            {
                var cur = State();
                bool justStarted = (_prevState == MPState.LobbyHost || _prevState == MPState.LobbyClient)
                                && (cur == MPState.Hosting     || cur == MPState.Connected);
                if (justStarted && _visible)
                {
                    _visible = false;
                    // Hide only the draggable panel — NOT the whole canvas.  The HUD,
                    // skip indicator and startup screen are siblings of the panel under
                    // _canvasGO; deactivating the canvas would kill them too.
                    _panelRT.gameObject.SetActive(false);
                    Plugin.Logger.LogInfo("[UI] Panel auto-hidden — game starting. Press F8 to show.");
                }
                _prevState = cur;
            }

            if (!_built)
            {
                if (++_initDelay < 30) return;
                try   { BuildCanvas(); _built = true; Plugin.Logger.LogInfo("[UI] Canvas built OK."); }
                catch (Exception ex) { _built = true; Plugin.Logger.LogError($"[UI] Build failed: {ex}"); }
                return;
            }

            if (_canvasGO == null) return;

            if (Input.GetKeyDown(KeyCode.F8))
            {
                _visible = !_visible;
                _panelRT.gameObject.SetActive(_visible);
            }

            // F9: toggle in-game player HUD (works independently of F8 panel)
            if (Input.GetKeyDown(KeyCode.F9) && _hudGO != null)
            {
                _hudVisible = !_hudVisible;
                _hudGO.SetActive(_hudVisible);
            }

            // Refresh HUD content when it's visible
            if (_hudGO != null && _hudVisible)
                RefreshHUD();

            if (!_visible) return;

            RefreshStatePanels();

            var mpScreen = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            var mpCanvas = ScreenToCanvas(mpScreen);

            // Drag
            if (Input.GetMouseButtonDown(0) && RectHit(_rtHdr, mpScreen))
            {
                _dragging = true;
                _dragOff  = _panelRT.anchoredPosition - mpCanvas;
            }
            if (!Input.GetMouseButton(0)) _dragging = false;
            if (_dragging) _panelRT.anchoredPosition = mpCanvas + _dragOff;

            // Field focus (only when idle pane is active)
            if (Input.GetMouseButtonDown(0))
            {
                if      (!MPClient.EnforceStartingCash && RectHit(_rtClientCash, mpScreen)) _focus = 5;
                else if (RectHit(_rtName,     mpScreen)) _focus = 1;
                else if (RectHit(_rtPort,     mpScreen)) _focus = 2;
                else if (RectHit(_rtIp,       mpScreen)) _focus = 3;
                else if (RectHit(_rtJoinPort, mpScreen)) _focus = 4;
                else                                      _focus = 0;
            }

            // Settings value field — type a number directly into the focused field.
            if (_editField >= 0)
            {
                foreach (char c in Input.inputString)
                {
                    if (c == '\b')
                    {
                        if (_editBuffer.Length > 0) _editBuffer = _editBuffer[..^1];
                    }
                    else if (c == '\n' || c == '\r')
                    {
                        CommitSettingsEdit();
                        break;
                    }
                    else if ((c >= '0' && c <= '9') || c == '.' || c == '-')
                    {
                        if (_editBuffer.Length < 12) _editBuffer += c;
                    }
                }
            }

            // Keyboard input
            if (_focus > 0)
            {
                foreach (char c in Input.inputString)
                {
                    if (c == '\b')
                    {
                        if      (_focus == 1 && _name.Length > 0) _name = _name[..^1];
                        else if ((_focus == 2 || _focus == 4) && _port.Length > 0) _port = _port[..^1];
                        else if (_focus == 3 && _ip.Length > 0) _ip = _ip[..^1];
                        else if (_focus == 5 && _clientCash.Length > 0) _clientCash = _clientCash[..^1];
                    }
                    else if (c != '\n' && c != '\r' && c != '\0')
                    {
                        if      (_focus == 1)                _name += c;
                        else if (_focus == 2 || _focus == 4) _port += c;
                        else if (_focus == 3)                _ip   += c;
                        else if (_focus == 5 && c >= '0' && c <= '9' && _clientCash.Length < 7) _clientCash += c;
                    }
                }
            }

            UpdateFields();

            // Button clicks
            if (Input.GetMouseButtonDown(0))
            {
                var st = State();
                if (_settingsOpen)
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
                else switch (st)
                {
                    case MPState.Idle:
                        if (RectHit(_rtHostBtn, mpScreen)) OnHost();
                        if (RectHit(_rtJoinBtn, mpScreen)) OnJoin();
                        break;
                    case MPState.LobbyHost:
                        if (RectHit(_rtLHDifficulty,  mpScreen)) OnCycleDifficulty();
                        if (RectHit(_rtLHEnforceCash, mpScreen)) OnToggleEnforceCash();
                        if (RectHit(_rtLHSettings,    mpScreen)) OnOpenSettings();
                        if (RectHit(_rtLHStartNew,  mpScreen)) OnStartNew();
                        if (RectHit(_rtLHStartLoad, mpScreen)) OnStartLoad();
                        if (RectHit(_rtLHStop,      mpScreen)) OnStop();
                        break;
                    case MPState.LobbyClient:
                        if (RectHit(_rtLCDisc, mpScreen)) OnDisc();
                        break;
                    case MPState.Hosting:
                        if (RectHit(_rtStopBtn, mpScreen)) OnStop();
                        break;
                    case MPState.Connected:
                        if (RectHit(_rtDiscBtn, mpScreen)) OnDisc();
                        break;
                    case MPState.Connecting:
                        if (RectHit(_rtCancelBtn, mpScreen)) OnDisc();
                        break;
                }
            }

            // Hover tint on idle-pane buttons
            if (_imgHostBtn != null) _imgHostBtn.color = RectHit(_rtHostBtn, mpScreen) ? C_BTNHOV : C_BTN;
            if (_imgJoinBtn != null) _imgJoinBtn.color = RectHit(_rtJoinBtn, mpScreen) ? C_BTNHOV : C_BTN;

            // Keep in-game host player count live
            if (State() == MPState.Hosting && _txtHostPlayers != null)
                _txtHostPlayers.text = $"Players connected: {MPServer.ConnectedCount}";
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
            if (!IsInGame()) return;
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;

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

                // Reject any time skip (bench / bed / sleep fast-forward) so the
                // world clock can only advance at real-time pace.
                if (!frozen)
                    TickWorldClock();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[UI] LateUpdate error: {ex.Message}");
            }
        }

        // ── World clock — skip rejection ──────────────────────────────────────
        //
        // With Time.timeScale forced to 1, the game clock advances at exactly 1×
        // real-time on its own.  A skip/fast-forward (bench, bed, sleep) instead
        // races the clock forward far faster than real time.  We measure the rate
        // over a short window and, on a skip, pin the clock back to the pre-skip
        // value until the skip mechanism gives up — so skips simply never work.

        private const float  WC_SAMPLE_WINDOW = 0.15f;  // s — rate measurement window
        private const double WC_SKIP_H_PER_S  = 0.20;   // game-h/real-s above this = a skip
        private const double WC_SETTLE_SLACK  = 0.02;   // game-h — skip considered over below this

        private static double _wcWindowStartHours = -1;
        private static float  _wcWindowStartReal;
        private static bool   _wcRejecting;
        private static double _wcLockHours;

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

            // Backlog #5 — exempt taxi travel.  The game's TaxiTravel coroutine
            // genuinely needs to fast-forward the clock to complete the ride;
            // pinning the clock would lock the player in the cab.  While the
            // local player is in a taxi we stop measuring rate and stay out of
            // the way; the moment the ride ends we re-arm the detector at the
            // post-ride clock (so the new time isn't seen as a skip retroactively).
            if (TrafficSync.LocalInTaxi)
            {
                _wcWindowStartHours = nowHours;        // keep window glued to live time
                _wcWindowStartReal  = realNow;
                _wcRejecting        = false;
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
                // Skip detected — lock at the pre-skip (window-start) value.
                _wcRejecting = true;
                _wcLockHours = _wcWindowStartHours;
                WriteWorldClock(_wcLockHours);
                Plugin.Logger.LogInfo(
                    $"[UI] Time skip rejected ({rate * 60:F0} game-min/s) — " +
                    $"clock held at day {(int)(_wcLockHours / 24.0)} hour {_wcLockHours % 24.0:F2}.");
            }
            else
            {
                // Normal real-time progression — slide the measurement window forward.
                _wcWindowStartHours = nowHours;
                _wcWindowStartReal  = realNow;
            }
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

        private void TickGameLoadDetect()
        {
            bool inGame = IsInGame();
            if (inGame && !_wasInGame)
            {
                Plugin.Logger.LogInfo("[UI] Game scene loaded — player sync active.");

                // Discovery probe — logs GameInstance time/skip method names so we
                // can identify what the bench/bed/car rest uses to fast-forward time.
                GameStateReader.ProbeGameInstance();

                // Fresh world-clock skip detector + appearance state for this session.
                ResetWorldClock();
                TrafficSync.Reset();
                _localAppearanceSent = false;
                _introNameFilled = false;       // re-arm intro-name prefill on next char-gen

                // Startup pause hold — freeze the game the moment our scene loads
                // and report in-game.  Stays frozen until ALL players have loaded,
                // then the host auto-releases it (no player interaction needed).
                if (MPServer.IsRunning || MPClient.IsConnected)
                {
                    TimeSync.BeginStartupHold();
                    _startupHoldElapsed = 0f;
                    if (MPServer.IsRunning)
                        MPServer.MarkPlayerInGame(MPConfig.PlayerId);
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
            }
            _wasInGame = inGame;
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

        /// <summary>Reads the local player's appearance once ready and sends it to peers.</summary>
        private void TrySendLocalAppearance()
        {
            if (_localAppearanceSent) return;
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;
            var dto = RemotePlayerManager.ReadLocalAppearance();
            if (dto == null) return;              // character not ready — retry next tick
            _localAppearanceSent = true;
            Plugin.Logger.LogInfo($"[Appearance] Local appearance: {RemotePlayerManager.Summary(dto)}");
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
        // One-shot: when an IntroCharacterCustomizer appears (host or client just
        // entered char-gen), pre-fill the first TMP_InputField with MPConfig.PlayerId
        // so the user doesn't have to retype the same name they put in the F8 panel.
        // Re-armed by Reset() (called on game load).
        private bool _introNameFilled;

        private void TickIntroNamePrefill()
        {
            if (_introNameFilled) return;
            try
            {
                var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<IntroCharacterCustomizer>());
                if (found == null || found.Length == 0) return;
                var customizer = found[0].TryCast<IntroCharacterCustomizer>();
                if (customizer == null) return;

                var fields = customizer.GetComponentsInChildren(Il2CppType.Of<TMP_InputField>(), true);
                if (fields == null || fields.Length == 0) return;

                Plugin.Logger.LogInfo(
                    $"[IntroName] IntroCharacterCustomizer detected — {fields.Length} TMP_InputField(s):");
                for (int i = 0; i < fields.Length; i++)
                {
                    var f = fields[i].TryCast<TMP_InputField>();
                    if (f == null) continue;
                    Plugin.Logger.LogInfo($"[IntroName]   [{i}] '{f.gameObject.name}' text='{f.text}'");
                }

                var preferred = MPConfig.PlayerId;
                if (string.IsNullOrWhiteSpace(preferred)) { _introNameFilled = true; return; }

                // Split on first space for first/last-name dual-field forms.
                string first = preferred, last = "";
                int sp = preferred.IndexOf(' ');
                if (sp > 0) { first = preferred.Substring(0, sp); last = preferred.Substring(sp + 1); }

                int filled = 0;
                for (int i = 0; i < fields.Length && filled < 2; i++)
                {
                    var f = fields[i].TryCast<TMP_InputField>();
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
                _introNameFilled = true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[IntroName] {ex.Message}");
                _introNameFilled = true;        // don't spam — try once, give up on error
            }
        }

        private void TickPositionSync()
        {
            if (!IsInGame()) return;

            // One-time discovery probes of the local character (self-guard once done).
            RemotePlayerManager.ProbeLocalCharacter();
            RemotePlayerManager.ProbeAppearance();
            RemotePlayerManager.ProbeColors();
            RemotePlayerManager.ProbeMorphs();
            RemotePlayerManager.ProbeAnimatorLive();
            VehicleManager.ProbeTraffic();
            VehicleManager.ProbeTaxi();
            VehicleManager.ProbeTrafficExtras();
            VehicleManager.ProbeCarColor();

            // Send our character appearance once the character is ready.
            TrySendLocalAppearance();

            if (!MPServer.IsRunning && !MPClient.IsConnected) return;

            // Smooth remote vehicles toward their networked transform (every frame).
            VehicleManager.TickSmoothing();

            // Traffic sync foundation (host enumerates / client disables local traffic).
            TrafficSync.Tick();

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
                    RotY = rotY
                };
                RemotePlayerManager.ReadLocalAnimState(payload);

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

        // ── Player HUD ────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the HUD row text each frame it's visible.
        /// Row 0 = local player, rows 1+ = remote players from RemotePlayerManager.
        /// </summary>
        private void RefreshHUD()
        {
            var st = State();
            bool connected = st == MPState.Hosting || st == MPState.Connected ||
                             st == MPState.LobbyHost || st == MPState.LobbyClient;

            if (!connected)
            {
                if (_hudGO != null) _hudGO.SetActive(false);
                _hudVisible = false;
                return;
            }

            // Row 0: local player (always present)
            if (_hudRows[0] != null)
                _hudRows[0].text = $"● {MPConfig.PlayerId}  (you)";

            // Rows 1+: remote players
            var remotes = RemotePlayerManager.GetRemotePlayerIds();
            for (int i = 1; i < _hudRows.Length; i++)
            {
                if (_hudRows[i] == null) continue;
                int ri = i - 1;
                _hudRows[i].text = ri < remotes.Count ? $"● {remotes[ri]}" : "";
            }

            // Update header with mode label
            if (_hudHeader != null)
            {
                string mode = st == MPState.Hosting   ? "HOST"
                            : st == MPState.LobbyHost ? "LOBBY-HOST"
                            : st == MPState.LobbyClient ? "LOBBY"
                            : "CLIENT";
                _hudHeader.text = $"[MP: {mode}]  F9";
            }
        }

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

            // Panel
            var panelGO  = MakeGO("Panel", _canvasGO.transform);
            var panelImg = panelGO.AddComponent<Image>();
            panelImg.color = C_BG;

            _panelRT = panelGO.GetComponent<RectTransform>();
            _panelRT.anchorMin = _panelRT.anchorMax = _panelRT.pivot = new Vector2(0f, 1f);
            _panelRT.sizeDelta = new Vector2(W, 0f); // finalized below

            // Header bar
            var hdrGO = MakeGO("Header", panelGO.transform);
            hdrGO.AddComponent<Image>().color = C_HDR;
            _rtHdr = hdrGO.GetComponent<RectTransform>();
            SetAnchored(_rtHdr, 0f, 0f, W, HDR);
            MakeLabel(hdrGO.transform, "Big Ambitions Multiplayer  [F8]",
                      SZ_HDR, C_WHITE, 0f, 0f, W, HDR, TextAlignmentOptions.Center);

            // State panes (fill the panel; only one active at a time)
            _paneIdle        = MakePaneFill("PaneIdle",        panelGO);
            _paneLobbyHost   = MakePaneFill("PaneLobbyHost",   panelGO);
            _paneLobbyClient = MakePaneFill("PaneLobbyClient", panelGO);
            _paneHosting     = MakePaneFill("PaneHosting",     panelGO);
            _paneConnected   = MakePaneFill("PaneConnected",   panelGO);
            _paneConnecting  = MakePaneFill("PaneConnecting",  panelGO);

            float iy  = CSTART; BuildIdlePane       (_paneIdle.transform,        ref iy);
            float lhy = CSTART; BuildLobbyHostPane  (_paneLobbyHost.transform,   ref lhy);
            float lcy = CSTART; BuildLobbyClientPane(_paneLobbyClient.transform, ref lcy);
            float hy  = CSTART; BuildHostingPane    (_paneHosting.transform,     ref hy);
            float cy  = CSTART; BuildConnectedPane  (_paneConnected.transform,   ref cy);
            float ny  = CSTART; BuildConnectingPane (_paneConnecting.transform,  ref ny);

            // Status bar — always on panel, never hidden
            float contentBottom = Mathf.Min(iy, lhy, lcy, hy, cy, ny);
            float statusTop     = contentBottom - 8f;
            var   statusGO      = MakeGO("Status", panelGO.transform);
            SetAnchored(statusGO.GetComponent<RectTransform>(), PAD, statusTop, FW, 24f);
            _txtStatus = MakeLabel(statusGO.transform, "", SZ_STS, C_GREEN,
                                   0f, 0f, FW, 24f, TextAlignmentOptions.Center);

            // Panel height: status element bottom + bottom padding
            float panelHeight = -statusTop + 24f + 12f;
            _panelRT.sizeDelta = new Vector2(W, panelHeight);

            // Centre on screen
            _panelRT.anchoredPosition = new Vector2(
                Mathf.Round((Screen.width  - W)           / 2f),
                Mathf.Round(-((Screen.height - panelHeight) / 2f)));

            // Build the in-game player HUD (starts hidden; F9 to toggle)
            BuildHUD(_canvasGO.transform);

            BuildStartupScreen(_canvasGO.transform);
            try { BuildSettingsPanel(_canvasGO.transform); }
            catch (Exception ex) { Plugin.Logger.LogError($"[UI] BuildSettingsPanel failed: {ex}"); }

            RefreshStatePanels();
            UpdateFields();
        }

        // ── HUD builder ───────────────────────────────────────────────────────

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
            content.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.13f, 0.98f);
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

            SettingsHeader(ct, ref y, "PLAYER — per character");
            SettingsNumRow (ct, ref y, "Starting age", "Your character's age when the game begins.",
                            () => _hostSettings.StartingAge, v => _hostSettings.StartingAge = (int)v, 1f, 16f, 80f, "0");
            SettingsNumRow (ct, ref y, "Starting money", "Cash your character starts the game with.",
                            () => _hostSettings.StartingMoney, v => _hostSettings.StartingMoney = (int)v, 500f, 0f, 100000f, "0");
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
            SettingsBoolRow(ct, ref y, "Tutorial enabled", "Story tutorial & quests. Off in multiplayer to avoid desync.",
                            () => _hostSettings.TutorialEnabled, v => _hostSettings.TutorialEnabled = v);

            y -= SGAP;
            var closeGO = MakeGO("Close", ct);
            _rtSettingsClose = closeGO.GetComponent<RectTransform>();
            SetAnchored(_rtSettingsClose, PAD, y, SET_W - PAD * 2f, BH);
            closeGO.AddComponent<Image>().color = C_BTN;
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
            decGO.AddComponent<Image>().color = C_BTN;
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
            incGO.AddComponent<Image>().color = C_BTN;
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
        private void TickStartupScreen()
        {
            if (_startupScreenGO == null) return;

            bool show = (MPServer.IsRunning || MPClient.IsConnected) && TimeSync.IsStartupHeld;
            _startupScreenGO.SetActive(show);
            if (!show || _startupScreenTxt == null) return;

            var waiting = MPServer.IsRunning
                ? MPServer.GetStartupWaitingFor()
                : MPClient.StartupWaitingFor;

            string body = (waiting != null && waiting.Count > 0)
                ? "Waiting for these players to finish loading:\n\n" +
                  string.Join("\n", waiting.Select(n => $"<color=#FFD24A>{n}</color>"))
                : "Waiting for all players to finish loading…";

            _startupScreenTxt.text =
                "<size=36><b>Multiplayer</b></size>\n\n" +
                body +
                "\n\n<size=17><color=#AAAAAA>The game starts automatically " +
                "once everyone is ready.</color></size>";
        }

        // ── Pane builders ─────────────────────────────────────────────────────

        private void BuildIdlePane(Transform p, ref float y)
        {
            MakeLabel(p, "Your name:", SZ_LBL, C_LBLGREY, PAD, y, FW, LH,
                      TextAlignmentOptions.Left); y -= LADV;
            (_imgName, _txtName, _rtName) = MakeField(p, _name, PAD, y, FW, FH); y -= FADV;

            y -= SGAP;
            Sep(p, y, "HOST"); y -= LADV;
            MakeLabel(p, "Port:", SZ_LBL, C_LBLGREY, PAD, y, 52f, FH, TextAlignmentOptions.Left);
            (_imgPort, _txtPort, _rtPort) = MakeField(p, _port, PAD + 56f, y, FW - 56f, FH);
            y -= FADV;
            (_imgHostBtn, _rtHostBtn) = MakeButton(p, "Host Game", C_BTN, PAD, y, FW, BH);
            y -= BADV;

            y -= SGAP;
            Sep(p, y, "JOIN"); y -= LADV;
            MakeLabel(p, "IP:", SZ_LBL, C_LBLGREY, PAD, y, 32f, FH, TextAlignmentOptions.Left);
            (_imgIp, _txtIp, _rtIp) = MakeField(p, _ip, PAD + 36f, y, FW - 36f, FH);
            y -= FADV;
            MakeLabel(p, "Port:", SZ_LBL, C_LBLGREY, PAD, y, 52f, FH, TextAlignmentOptions.Left);
            (_imgJoinPort, _txtJoinPort, _rtJoinPort) = MakeField(p, _port, PAD + 56f, y, FW - 56f, FH);
            y -= FADV;
            (_imgJoinBtn, _rtJoinBtn) = MakeButton(p, "Join Game", C_BTN, PAD, y, FW, BH);
            y -= BADV;
        }

        private void BuildLobbyHostPane(Transform p, ref float y)
        {
            _txtLHInfo = MakeLabel(p, "", SZ_LBL, C_WHITE, PAD, y, FW, LH,
                                   TextAlignmentOptions.Left); y -= LADV;

            y -= SGAP;
            MakeLabel(p, "Players in lobby:", SZ_LBL, C_LBLGREY, PAD, y, FW, LH,
                      TextAlignmentOptions.Left); y -= LADV;
            for (int i = 0; i < 4; i++)
            {
                _txtLHSlots[i] = MakeLabel(p, "", SZ_LBL, C_YELLOW, PAD + 12f, y, FW - 12f, LH,
                                           TextAlignmentOptions.Left); y -= LADV;
            }

            // Difficulty selector — click to cycle Easy / Normal / Hard.
            // Applies to "Start New Game"; networked to all clients.
            y -= SGAP;
            var diffGO = MakeGO("LHDifficulty", p);
            SetAnchored(diffGO.GetComponent<RectTransform>(), PAD, y, FW, BH);
            diffGO.AddComponent<Image>().color = C_BTN;
            _rtLHDifficulty  = diffGO.GetComponent<RectTransform>();
            _txtLHDifficulty = MakeLabel(diffGO.transform, "", SZ_BTN, C_WHITE,
                                         0f, 0f, FW, BH, TextAlignmentOptions.Center);
            _txtLHDifficulty.text = $"◄   Difficulty:  {_hostSettings.Difficulty}   ►";
            y -= BADV;

            // Starting-cash enforcement toggle — click to switch enforced vs per-player.
            var ecGO = MakeGO("LHEnforceCash", p);
            SetAnchored(ecGO.GetComponent<RectTransform>(), PAD, y, FW, BH);
            ecGO.AddComponent<Image>().color = C_BTN;
            _rtLHEnforceCash  = ecGO.GetComponent<RectTransform>();
            _txtLHEnforceCash = MakeLabel(ecGO.transform, "", SZ_BTN, C_WHITE,
                                          0f, 0f, FW, BH, TextAlignmentOptions.Center);
            UpdateEnforceCashLabel();
            y -= BADV;

            MakeButton(p, "⚙  Game Settings…",          C_BTNBLUE, PAD, y, FW, BH, out _rtLHSettings);
            y -= BADV;

            y -= SGAP;
            MakeButton(p, "▶  Start New Game",          C_BTN,     PAD, y, FW, BH, out _rtLHStartNew);
            y -= BADV;
            MakeButton(p, "▶  Load Multiplayer Save",   C_BTNBLUE, PAD, y, FW, BH, out _rtLHStartLoad);
            y -= BADV;
            y -= SGAP;
            MakeButton(p, "■  Stop Hosting",            C_STOP,    PAD, y, FW, BH, out _rtLHStop);
            y -= BADV;
        }

        private void BuildLobbyClientPane(Transform p, ref float y)
        {
            _txtLCInfo = MakeLabel(p, "", SZ_LBL, C_WHITE, PAD, y, FW, LH,
                                   TextAlignmentOptions.Left); y -= LADV;

            MakeLabel(p, "Waiting for host to start the game...", SZ_LBL, C_LBLGREY,
                      PAD, y, FW, LH, TextAlignmentOptions.Left); y -= LADV;

            y -= SGAP;
            MakeLabel(p, "Players in lobby:", SZ_LBL, C_LBLGREY, PAD, y, FW, LH,
                      TextAlignmentOptions.Left); y -= LADV;
            for (int i = 0; i < 4; i++)
            {
                _txtLCSlots[i] = MakeLabel(p, "", SZ_LBL, C_YELLOW, PAD + 12f, y, FW - 12f, LH,
                                           TextAlignmentOptions.Left); y -= LADV;
            }

            // Starting cash — editable when the host has not enforced a fixed amount.
            y -= SGAP;
            MakeLabel(p, "Your starting cash:", SZ_LBL, C_LBLGREY, PAD, y, 150f, FH,
                      TextAlignmentOptions.Left);
            (_imgClientCash, _txtClientCash, _rtClientCash) =
                MakeField(p, _clientCash, PAD + 156f, y, FW - 156f, FH);
            y -= FADV;

            y -= SGAP;
            MakeButton(p, "Disconnect", C_STOP, PAD, y, FW, BH, out _rtLCDisc);
            y -= BADV;
        }

        private void BuildHostingPane(Transform p, ref float y)
        {
            _txtHostAs      = InfoLbl(p, y); y -= LADV;
            _txtHostPort    = InfoLbl(p, y); y -= LADV;
            _txtHostPlayers = InfoLbl(p, y); y -= LADV;
            y -= SGAP;
            MakeButton(p, "■  Stop Hosting", C_STOP, PAD, y, FW, BH, out _rtStopBtn);
            y -= BADV;
        }

        private void BuildConnectedPane(Transform p, ref float y)
        {
            _txtConnAs   = InfoLbl(p, y); y -= LADV;
            _txtConnHost = InfoLbl(p, y); y -= LADV;
            y -= SGAP;
            MakeButton(p, "Disconnect", C_STOP, PAD, y, FW, BH, out _rtDiscBtn);
            y -= BADV;
        }

        private void BuildConnectingPane(Transform p, ref float y)
        {
            _txtConnecting = InfoLbl(p, y); y -= LADV;
            y -= SGAP;
            MakeButton(p, "Cancel", C_STOP, PAD, y, FW, BH, out _rtCancelBtn);
            y -= BADV;
        }

        private TextMeshProUGUI InfoLbl(Transform p, float y) =>
            MakeLabel(p, "", SZ_LBL, C_WHITE, PAD, y, FW, LH, TextAlignmentOptions.Left);

        private void Sep(Transform p, float y, string section = "HOST") =>
            MakeLabel(p, $"── {section} {'─'.ToString().PadRight(30, '─')}", SZ_SEP, C_LBLGREY,
                      PAD, y, FW, LH, TextAlignmentOptions.Left);

        // ── State refresh ─────────────────────────────────────────────────────

        private void RefreshStatePanels()
        {
            var st = State();
            _paneIdle.SetActive(st == MPState.Idle);
            _paneLobbyHost.SetActive(st == MPState.LobbyHost);
            _paneLobbyClient.SetActive(st == MPState.LobbyClient);
            _paneHosting.SetActive(st == MPState.Hosting);
            _paneConnected.SetActive(st == MPState.Connected);
            _paneConnecting.SetActive(st == MPState.Connecting);

            switch (st)
            {
                case MPState.LobbyHost:
                    _txtLHInfo.text = $"Hosting as: {MPConfig.PlayerId}   Port: {MPConfig.Port}";
                    RefreshPlayerSlots(_txtLHSlots, MPServer.LobbyPlayers, 0);
                    UpdateEnforceCashLabel();
                    break;

                case MPState.LobbyClient:
                    _txtLCInfo.text = $"Connected as: {MPConfig.PlayerId}   Host: {MPConfig.HostIP}:{MPConfig.Port}";
                    RefreshPlayerSlots(_txtLCSlots, MPClient.LobbyPlayers, -1);
                    break;

                case MPState.Hosting:
                    _txtHostAs.text      = $"Hosting as: {MPConfig.PlayerId}";
                    _txtHostPort.text    = $"Port: {MPConfig.Port}";
                    _txtHostPlayers.text = $"Players connected: {MPServer.ConnectedCount}";
                    break;

                case MPState.Connected:
                    _txtConnAs.text   = $"Connected as: {MPConfig.PlayerId}";
                    _txtConnHost.text = $"Host: {MPConfig.HostIP}:{MPConfig.Port}";
                    break;

                case MPState.Connecting:
                    _txtConnecting.text = $"Connecting to {MPConfig.HostIP}:{MPConfig.Port}...";
                    break;
            }
        }

        private static void RefreshPlayerSlots(TextMeshProUGUI[] slots,
                                               System.Collections.Generic.List<string> players,
                                               int hostIndex)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                if (i < players.Count)
                    slots[i].text = $"• {players[i]}{(i == 0 && hostIndex == 0 ? "  (host)" : "")}";
                else
                    slots[i].text = "";
            }
        }

        private void UpdateFields()
        {
            SetField(_imgName,     _txtName,     _name, _focus == 1);
            SetField(_imgPort,     _txtPort,     _port, _focus == 2);
            SetField(_imgIp,       _txtIp,       _ip,   _focus == 3);
            SetField(_imgJoinPort, _txtJoinPort, _port, _focus == 4);

            // Client starting-cash field — editable only when the host allows it.
            string cashDisp = MPClient.EnforceStartingCash ? "(set by host)" : _clientCash;
            SetField(_imgClientCash, _txtClientCash, cashDisp, _focus == 5);
            if (int.TryParse(_clientCash, out var cc))
                MPClient.ChosenStartingCash = cc < 0 ? 0 : (cc > 100000 ? 100000 : cc);
        }

        private static void SetField(Image img, TextMeshProUGUI lbl, string val, bool focused)
        {
            if (img != null) img.color = focused ? C_FIELDFOC : C_FIELD;
            if (lbl != null) lbl.text  = val + (focused ? "|" : "");
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private void OnHost()
        {
            if (!int.TryParse(_port, out int p) || p < 1024 || p > 65535)
            { SetStatus("Invalid port.", true); return; }
            if (string.IsNullOrWhiteSpace(_name))
            { SetStatus("Enter a player name.", true); return; }
            MPConfig.SetRuntime(_name.Trim(), null, p);
            MPServer.Start(p);
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
            if (_txtLHDifficulty != null)
                _txtLHDifficulty.text = $"◄   Difficulty:  {_hostSettings.Difficulty}   ►";
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
            if (_settingsPanelGO != null) _settingsPanelGO.SetActive(true);
        }

        private void OnCloseSettings()
        {
            CommitSettingsEdit();
            _settingsOpen = false;
            if (_settingsPanelGO != null) _settingsPanelGO.SetActive(false);
        }
        private void OnStop()      { MPServer.Stop();          SetStatus("Stopped hosting.", false); }
        private void OnDisc()      { MPClient.Disconnect();    SetStatus("Disconnected.", false); }

        private void OnToggleEnforceCash()
        {
            MPServer.SetEnforceStartingCash(!MPServer.EnforceStartingCash);
            UpdateEnforceCashLabel();
        }

        private void UpdateEnforceCashLabel()
        {
            if (_txtLHEnforceCash != null)
                _txtLHEnforceCash.text = MPServer.EnforceStartingCash
                    ? "Starting cash:  ENFORCED — same for all players"
                    : "Starting cash:  per-player — each sets their own";
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

        private static GameObject MakePaneFill(string name, GameObject panelGO)
        {
            var go = MakeGO(name, panelGO.transform);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return go;
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
