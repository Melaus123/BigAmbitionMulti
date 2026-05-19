using UnityEngine;
using Il2CppInterop.Runtime.Injection;

namespace BigAmbitionsMP
{
    /// <summary>
    /// In-game overlay. Toggle with F8. Draggable via title bar.
    ///
    /// This game's IL2CPP binary strips GUILayout.BeginArea, GUI.Button, and
    /// GUI.TextField.  Only GUI.Box is reliably available, so ALL rendering is
    /// done with GUI.Box and ALL interactivity (text input, button clicks) is
    /// handled in Update() via Input.* which is confirmed unstripped.
    /// </summary>
    public class MPMenuUI : MonoBehaviour
    {
        public MPMenuUI(IntPtr ptr) : base(ptr) { }

        // ── Model ─────────────────────────────────────────────────────────────
        private bool   _visible  = true;
        private string _name     = "Player1";
        private string _port     = "7777";
        private string _ip       = "127.0.0.1";
        private string _status   = "";
        private bool   _statusErr;

        // ── Window / drag ─────────────────────────────────────────────────────
        private Rect    _win     = new Rect(20, 20, 340, 348);
        private bool    _dragging;
        private Vector2 _dragOff;

        // ── Text-field focus  (0=none 1=name 2=port 3=ip 4=joinPort) ──────────
        private int _focus;

        // Interactive rects written each OnGUI, read each Update
        private Rect _rName, _rPort, _rJoinPort, _rIp;
        private Rect _rHostBtn, _rJoinBtn, _rStopBtn, _rDiscBtn, _rCancelBtn;

        // ── Layout constants ──────────────────────────────────────────────────
        private const float PAD = 8f;
        private const float H   = 22f;
        private const float G   = 5f;
        private const float HDR = 24f;

        // ── Update: drag + focus + text input + button clicks ─────────────────

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) _visible = !_visible;
            if (!_visible) return;

            // y=0 at bottom in Input, y=0 at top in IMGUI → flip
            var m = new Vector2(Input.mousePosition.x,
                                Screen.height - Input.mousePosition.y);

            // Drag
            var hdrR = new Rect(_win.x, _win.y, _win.width, HDR);
            if (Input.GetMouseButtonDown(0) && hdrR.Contains(m))
            {
                _dragging = true;
                _dragOff  = new Vector2(_win.x - m.x, _win.y - m.y);
            }
            if (!Input.GetMouseButton(0)) _dragging = false;
            if (_dragging) { _win.x = m.x + _dragOff.x; _win.y = m.y + _dragOff.y; }

            // Field focus — click to select a field
            if (Input.GetMouseButtonDown(0))
            {
                if      (_rName.Contains(m))     _focus = 1;
                else if (_rPort.Contains(m))     _focus = 2;
                else if (_rIp.Contains(m))       _focus = 3;
                else if (_rJoinPort.Contains(m)) _focus = 4;
                else                              _focus = 0;
            }

            // Keyboard input → write into focused field
            if (_focus > 0)
            {
                string raw = Input.inputString;
                for (int i = 0; i < raw.Length; i++)
                {
                    char c = raw[i];
                    if (c == '\b')              // backspace
                    {
                        if (_focus == 1 && _name.Length > 0)
                            _name = _name.Substring(0, _name.Length - 1);
                        else if ((_focus == 2 || _focus == 4) && _port.Length > 0)
                            _port = _port.Substring(0, _port.Length - 1);
                        else if (_focus == 3 && _ip.Length > 0)
                            _ip = _ip.Substring(0, _ip.Length - 1);
                    }
                    else if (c != '\n' && c != '\r' && c != '\0')
                    {
                        if      (_focus == 1)             _name += c;
                        else if (_focus == 2 || _focus == 4) _port += c;
                        else if (_focus == 3)             _ip   += c;
                    }
                }
            }

            // Button clicks — check rects set by last OnGUI
            if (Input.GetMouseButtonDown(0))
            {
                var st = State();
                if (st == MPState.Idle)
                {
                    if (_rHostBtn.Contains(m))   OnHost();
                    if (_rJoinBtn.Contains(m))   OnJoin();
                }
                else if (st == MPState.Hosting    && _rStopBtn.Contains(m))   OnStop();
                else if (st == MPState.Connected  && _rDiscBtn.Contains(m))   OnDisc();
                else if (st == MPState.Connecting && _rCancelBtn.Contains(m)) OnDisc();
            }
        }

        // ── OnGUI: GUI.Box only (all other GUI.* are stripped) ────────────────

        private void OnGUI()
        {
            if (!_visible) return;

            var pb = GUI.backgroundColor;

            // ── Main body: 8 near-black layers ──────────────────────────────
            GUI.backgroundColor = new Color(0.13f, 0.13f, 0.13f, 1f);
            for (int i = 0; i < 8; i++) GUI.Box(_win, "");

            // ── Header: 12 medium-grey layers → clearly lighter drag target ──
            var hdr = new Rect(_win.x, _win.y, _win.width, HDR);
            GUI.backgroundColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            for (int i = 0; i < 12; i++) GUI.Box(hdr, "");
            GUI.backgroundColor = pb;
            GUI.Box(hdr, "Big Ambitions Multiplayer  [F8]");

            // ── Content ──────────────────────────────────────────────────────
            float x = _win.x + PAD;
            float y = _win.y + HDR + G;
            float w = _win.width - PAD * 2;

            var st = State();
            if      (st == MPState.Idle)       DrawIdle(x, ref y, w);
            else if (st == MPState.Hosting)    DrawHosting(x, ref y, w);
            else if (st == MPState.Connected)  DrawConnected(x, ref y, w);
            else if (st == MPState.Connecting) DrawConnecting(x, ref y, w);

            if (!string.IsNullOrEmpty(_status))
            {
                var pc = GUI.contentColor;
                GUI.contentColor = _statusErr ? Color.red : Color.green;
                GUI.Box(new Rect(x, y + G, w, H), _status);
                GUI.contentColor = pc;
            }
        }

        // ── Panel drawers ─────────────────────────────────────────────────────

        private void DrawIdle(float x, ref float y, float w)
        {
            // Name
            Lbl(x, ref y, w, "Your name (click then type):");
            Field(x, ref y, w, _name, _focus == 1, ref _rName);
            y += G;

            // ── Host ──────────────────────────────────────────────────────────
            Lbl(x, ref y, w, "--- HOST ---");
            Lbl(x, y, 50, "Port:");
            Field(x + 52, ref y, w - 52, _port, _focus == 2, ref _rPort);
            Btn(x, ref y, w, ">> Host Game <<", ref _rHostBtn);
            y += G;

            // ── Join ──────────────────────────────────────────────────────────
            Lbl(x, ref y, w, "--- JOIN ---");
            Lbl(x, y, 28, "IP:");
            Field(x + 30, ref y, w - 30, _ip, _focus == 3, ref _rIp);
            Lbl(x, y, 50, "Port:");
            Field(x + 52, ref y, w - 52, _port, _focus == 4, ref _rJoinPort);
            Btn(x, ref y, w, ">> Join Game <<", ref _rJoinBtn);
        }

        private void DrawHosting(float x, ref float y, float w)
        {
            Lbl(x, ref y, w, $"Hosting as: {MPConfig.PlayerId}");
            Lbl(x, ref y, w, $"Port: {MPConfig.Port}");
            Lbl(x, ref y, w, $"Players connected: {MPServer.ConnectedCount}");
            y += G;
            Btn(x, ref y, w, ">> Stop Hosting <<", ref _rStopBtn);
        }

        private void DrawConnected(float x, ref float y, float w)
        {
            Lbl(x, ref y, w, $"Connected as: {MPConfig.PlayerId}");
            Lbl(x, ref y, w, $"Host: {MPConfig.HostIP}:{MPConfig.Port}");
            y += G;
            Btn(x, ref y, w, ">> Disconnect <<", ref _rDiscBtn);
        }

        private void DrawConnecting(float x, ref float y, float w)
        {
            Lbl(x, ref y, w, $"Connecting to {MPConfig.HostIP}:{MPConfig.Port}...");
            y += G;
            Btn(x, ref y, w, ">> Cancel <<", ref _rCancelBtn);
        }

        // ── GUI.Box helpers ───────────────────────────────────────────────────

        // Label — plain box, advances y
        private static void Lbl(float x, ref float y, float w, string t)
        { GUI.Box(new Rect(x, y, w, H), t); y += H + G; }

        // Label at fixed y — does NOT advance y (for side-by-side with Field)
        private static void Lbl(float x, float y, float w, string t)
        { GUI.Box(new Rect(x, y, w, H), t); }

        // Field — darker box to distinguish from labels; cursor "|" when focused
        private void Field(float x, ref float y, float w, string val, bool foc, ref Rect r)
        {
            r = new Rect(x, y, w, H);
            var pb = GUI.backgroundColor;
            GUI.backgroundColor = foc
                ? new Color(0.42f, 0.42f, 0.42f, 1f)
                : new Color(0.22f, 0.22f, 0.22f, 1f);
            for (int i = 0; i < 5; i++) GUI.Box(r, "");   // solid field bg
            GUI.backgroundColor = pb;
            GUI.Box(r, val + (foc ? "|" : ""));            // value text
            y += H + G;
        }

        // Button — visibly lighter box with >> << markers
        private static void Btn(float x, ref float y, float w, string t, ref Rect r)
        {
            r = new Rect(x, y, w, H);
            var pb = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.50f, 0.50f, 0.50f, 1f);
            for (int i = 0; i < 8; i++) GUI.Box(r, "");   // solid button bg
            GUI.backgroundColor = pb;
            GUI.Box(r, t);
            y += H + G;
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
            SetStatus($"Hosting on port {p}.", false);
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

        private void OnStop() { MPServer.Stop();      SetStatus("Stopped hosting.", false); }
        private void OnDisc() { MPClient.Disconnect(); SetStatus("Disconnected.",    false); }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static MPState State()
        {
            if (MPServer.IsRunning)    return MPState.Hosting;
            if (MPClient.IsConnected)  return MPState.Connected;
            if (MPClient.IsConnecting) return MPState.Connecting;
            return MPState.Idle;
        }

        private void SetStatus(string msg, bool err)
        {
            _status    = msg;
            _statusErr = err;
            Plugin.Logger.LogInfo($"[UI] {msg}");
        }

        private enum MPState { Idle, Hosting, Connected, Connecting }
    }
}
