using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using Localizor;   // .Localize (the tooltip header: TooltipSystem.AddHeader takes the game's text holder)

namespace BigAmbitionsMP
{
    /// <summary>
    /// The BUSINESS page inside the phone's full menu (REBUILD, user-approved 2026-09-28: mock-ups B1-B5 + wording;
    /// "plenty of room for long lists, and provides scroll bars when the list is still too long"; "make sure visually it
    /// matches more closely with the other screens from the same phone"). Spec: scratchpad hubart/gen_hub.py
    /// (left_pane, header_card, tabs, table). Laid out like the game's own Contacts / MyEmployees pages: a left pane
    /// ('Players' + a scrolled list of cards), a thin splitter, and a right pane with a white header card for the
    /// selected player, white tabs, a white table-header card and grey see-through rows with a thin scrollbar.
    ///
    /// PRESENTATION ONLY: every button makes the same MPHub / GrantSync / merger call as the page it replaces, under
    /// the same availability rules. Every number below is a mock-up pixel (a 1920x1057 in-game shot) converted to
    /// page units by BK/BX0/BY0 (measured from shot-hub-biz-transfers-fill.png: the page root spans screen x 21-1899,
    /// y 127-983 of that shot). Rows rebuild only when MPHub.Version or the grant / merger / roster signature moves
    /// (checked twice a second) - never per frame. Clicks stay on the hand-rolled hit-test dispatch: every clickable
    /// or typable part is registered below and tested only while active in the hierarchy (the known trap).
    /// Fallback when the phone page cannot be injected: the same page in a plain window titled 'Business'.
    /// </summary>
    public partial class MPCanvasUI
    {
        // ── state the MPCanvasUI hooks read ──
        private bool _hubVisible;
        private bool _hubUiHover;
        private GameObject? _hub;            // native: the page root; fallback: the whole window
        private RectTransform? _hubRT;       // click-inside area (click-away commits typing)
        private bool _hubNative;
        private Camera? _hubCam;             // FullMenu canvas camera (null = overlay)

        // ── look (approved mock-up; screen px -> page units) ──
        private const float BK = 1f / 0.9787f, BX0 = 21f, BY0 = 127f;
        private static readonly Color B_ROW = new Color(118f / 255f, 124f / 255f, 132f / 255f, 0.62f);
        private static readonly Color B_INK = LC(0x1E2226), B_GREY = LC(0x7A8088), B_SUB = new Color(1f, 1f, 1f, 0.72f), B_MUTED = new Color(1f, 1f, 1f, 0.78f);
        private static readonly Color B_TABOFF = LC(0xCFD4D9), B_TABOFFTXT = LC(0x7D838A), B_HEADTXT = LC(0x2A2F35), B_HELP = LC(0x6E757D);
        private static readonly Color B_BADGE = LC(0xE8474C), B_FIELD = LC(0xF1F4F7), B_FIELDLINE = LC(0xC9D0D8), B_FIELDFOC = LC(0x3B8CF0), B_DIV = LC(0xD5DADF);
        private static readonly Color B_TRACK = new Color(1f, 1f, 1f, 0.14f), B_THUMB = new Color(236f / 255f, 238f / 255f, 241f / 255f, 0.85f);
        private static readonly Color B_SPLIT = new Color(1f, 1f, 1f, 0.22f), B_AVTXT = LC(0x1B2025);
        private static readonly Color B_DIS = new Color(140f / 255f, 147f / 255f, 155f / 255f, 0.45f), B_DISTXT = new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color B_WINBG = LC(0x56606A);
        private static readonly Color B_DISW = LC(0xC9CED3), B_DISWTXT = LC(0x7A8088);
        private const string B_UNKNOWN = "Unknown player";   // any player whose name is not known (never an id)
        // Button kinds: 0 blue, 1 orange, 2 red, 3 grey, 4 disabled (flat grey 45 %, faded text), 5 disabled on the white
        // header card (flat #C9CED3, #7A8088 text - readable there).
        private static readonly int[,] B_GRAD = { { 0x1F63D6, 0x3B8CF0 }, { 0xC8743A, 0xE39A55 }, { 0xE2434B, 0xF26B6F }, { 0x8C939B, 0x8C939B } };
        private static readonly Sprite?[] _bGradSp = new Sprite?[4];

        private static readonly string[] B_TABS = { "Offers to you", "Your offers", "Loans", "Access", "Company" };
        private static readonly string[][] B_COLS =
        {
            new[] { "From", "Offer", "Amount", "Details" },
            new[] { "To", "Offer", "Amount", "Details", "Status" },
            new[] { "Player", "Loan", "Left to repay", "Per day", "Days left" },
            new[] { "Player", "Vehicles", "Homes", "Businesses" },
            new[] { "Member", "Vehicles", "Status" },
        };
        private static readonly float[][] B_COLW =   // -1 = takes what is left before the buttons
        {
            new[] { 300f, 150f, 140f, -1f },
            new[] { 300f, 150f, 130f, -1f, 100f },
            new[] { 280f, 130f, 160f, 150f, 110f },
            new[] { 380f, 190f, 190f, 190f },
            new[] { 420f, 200f, -1f },
        };
        private static readonly float[] B_ACTW = { 230f, 110f, 260f, 0f, 230f };
        private const float B_ROWW = 1163f, B_ROWH = 50f, B_PITCH = 58f, B_ROWSH = 456f, B_CARDPITCH = 88f, B_CARDSH = 712f;

        private sealed class BBtn { public GameObject go = null!; public RectTransform rt = null!; public Image img = null!; public TextMeshProUGUI lbl = null!; public int kind = -1; }
        private sealed class BField { public RectTransform rt = null!; public Image line = null!; public TextMeshProUGUI lbl = null!; }
        private sealed class BizPl
        {
            public string Key = "", Pid = "", Handle = "", Name = "";
            public bool Online, Co;
            public int Incoming;
            public OwnGrantEntry? Grant;
        }

        // ── page state ──
        private int _hubTab;                           // 0 Offers to you, 1 Your offers, 2 Loans, 3 Access, 4 Company
        private string _bizSel = "";                   // selected card key (pid, or "stable:<handle>" for an offline grantee)
        private int _hubSeenVersion = -1;
        private bool _bizDirty = true;
        private ulong _bizSig; private float _bizNextSig;
        private int _bizFieldKey = int.MinValue, _bizHelpKey = int.MinValue, _bizRowCount, _bizErrs;
        private readonly List<BizPl> _bizPls = new();
        private readonly HashSet<string> _bizOnline = new();
        // Typed inputs (click a box -> type digits; Enter / click-away commits; Esc leaves the box).
        // Defaults MATCH THE BANK's fixed deal: 20% total over 244 days.
        private int _bizFocus;                         // 0 none, 1 amount, 2 interest, 3 term, 4 part payment
        private bool _hubFreshFocus;                   // first keystroke replaces the value
        private double _hubAmount = 10000; private string _hubAmountStr = "10000";
        private string _hubRateStr = "20", _hubTermStr = "244";
        private double _hubPartialAmount = 1000; private string _hubPartialStr = "1000";
        private string _hubRepayArm = ""; private float _hubRepayArmAt; private float _hubRepayArmAmt;   // repay confirm: armed loan, time, amount (0 = full)

        // ── page parts ──
        private RectTransform? _bizRoot, _bizCloseRT;
        private TextMeshProUGUI? _bMeasure;
        private RectTransform? _bizCardsVp, _bizCardsContent;
        private readonly List<(RectTransform rt, string key)> _bizCardHits = new();
        private Image? _bizHdrAv; private TextMeshProUGUI? _bizHdrLetter, _bizHdrName, _bizHdrSub, _bizHelp;
        private BBtn? _bizMergeBtn, _bizGiftBtn, _bizLoanBtn, _bizLeaveBtn;
        private byte _bizMergeAct; private string _bizMergePid = "";
        private BField? _bizAmt, _bizRate, _bizTerm, _bizPart;
        private readonly RectTransform?[] _bizTabRT = new RectTransform?[5];
        private readonly Image?[] _bizTabImg = new Image?[5];
        private readonly TextMeshProUGUI?[] _bizTabLbl = new TextMeshProUGUI?[5];
        private GameObject? _bizTabBadge; private TextMeshProUGUI? _bizTabBadgeLbl;
        private GameObject? _bizToolLoans, _bizToolAccess, _bizToolCompany;
        private TextMeshProUGUI? _bizCompanyLbl;
        private RectTransform? _bizHeadCols, _bizRowsVp, _bizRowsContent;
        // Row / checkbox registry: act 0 accept, 1 decline, 2 cancel, 3 repay-confirm, 4 repay-part, 5 repay-all,
        // 6 key toggle (online pid), 7 key toggle (offline grantee), 9 merger accept, 10 merger decline.
        private readonly List<(RectTransform rt, byte act, string id, GrantKind kind)> _bizHits = new();
        // Merger confirmation popup (user 2026-07-07: proposing/accepting is a BIG decision - explain it).
        private GameObject? _mergerConfirmGO;
        private RectTransform? _mergerCardRT;
        private TextMeshProUGUI? _mergerConfirmLbl;
        private RectTransform? _rtMergerOk;
        private BBtn? _mergerOkBtn, _mergerCancelBtn;
        private string _mergerConfirmMode = "", _mergerConfirmPid = "";
        // MERGE-TOOLTIP-1 (user-approved 2026-09-28, decision 15): the GAME'S OWN tooltip on 'Propose merger', driven the
        // way its TooltipTarget drives it (0.1 s hover delay, then Show + header / splitter / label). Text is built only
        // when it opens; every other frame is a hit test.
        private float _bizTipSince = -1f;     // hover start (unscaled time); -1 = the pointer is not on the button
        private bool _bizTipShown;            // our tooltip is up in TooltipSystem
        private int _bizTipShowFrame = -10;   // frame of our last Show (review: the game fades a tooltip in one frame AFTER Show, unguarded)
        private int _bizTipRehideUntil = -1;  // re-hide window after a Hide that came within a frame of our Show
        private string _bizTipPid = "";       // the player it was built for (a selection change under the pointer rebuilds it)
        private bool _bizTipRaiseLogged;
#if BAMP_DEV
        private bool _bizTipDevHold;          // DEV lever: the pointer is treated as resting on the button's centre
#endif
        private Vector2 _bizFitFor = new Vector2(-1f, -1f);
        private float _bizToolW;
        private RectTransform? _bizLeftPane, _bizTabBadgeHost;
        private Vector2 _bizLeftFor = new Vector2(-1f, -1f);
        private GameObject? _bizSelGO; private int _bizEscFrame = -1;   // the focused box as the EventSystem's selection (Esc)
        private static int _bizUiLayer = -2;

        /// <summary>Hub hit-test honoring the host canvas's camera.</summary>
        private bool HubHit(RectTransform? rt, Vector2 screenPos)
        {
            if (rt == null) return false;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenPos, _hubCam, out var local);
            return rt.rect.Contains(local);
        }
        private bool BHit(RectTransform? rt, Vector2 mp) => rt != null && rt.gameObject.activeInHierarchy && HubHit(rt, mp);

        private void BizErr(string where, Exception ex)
        {
            if (_bizErrs++ < 6) Plugin.Logger.LogWarning($"[Hub] {where}: {ex.Message}");
        }

        // ══ entry points (hooks in MPCanvasUI) ══

        /// <summary>Open the Business page inside the native full menu (and the menu itself if closed).  Falls back to
        /// the standalone window when the injection isn't ready.</summary>
        private void ShowHubNative()
        {
            try
            {
                if (!MPHubNativePage.Ready || MPHubNativePage.ContentRoot == null)
                { _hubVisible = !_hubVisible; return; }   // fallback: standalone toggle
                // Rebuild if the page was last built for the other host.
                if (_hub != null && !_hubNative) { UnityEngine.Object.Destroy(_hub); _hub = null; }
                _hubCam = MPHubNativePage.UiCamera;
                if (_hub == null)
                {
                    _hubNative = true;
                    var root = BuildBizPage(MPHubNativePage.ContentRoot);
                    if (root != null) { _hub = root.gameObject; _hubRT = root; _hub.SetActive(false); }
                }
                MPHubNativePage.OpenMenuToBusiness();
                _hubVisible = _hub != null;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Hub] ShowHubNative: {ex.Message}"); }
        }

        /// <summary>Scene reset: the native page died with the scene; a fallback window lives on our own canvas, so it
        /// goes too (otherwise it would outlive the session).</summary>
        private void BizOnSceneReset()
        {
            try
            {
                if (_hub != null && !_hubNative) UnityEngine.Object.Destroy(_hub);
            }
            catch { }
            BizHideMergeTip();   // the tooltip system outlives the scene (DontDestroyOnLoad)
#if BAMP_DEV
            _bizTipDevHold = false;
#endif
            _hub = null; _hubRT = null; _hubNative = false; _bizRoot = null; _mergerConfirmGO = null;
            _bizFocus = 0; _hubUiHover = false; _bizSel = ""; _hubRepayArm = "";
            _bizSelGO = null; _bizLeftPane = null; _bizTabBadgeHost = null; _bizLeftFor = new Vector2(-1f, -1f);
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
                // Native-hosted: the page's active state is the master (a native app click hides us via the ShowApp patch).
                if (_hubNative && _hub != null)
                    _hubVisible = MPHubNativePage.PageActive;

                if (_hub == null)
                {
                    BizHideMergeTip();
                    if (!_hubVisible || _canvasGO == null) return;
                    BuildBizWindow();
                    if (_hub == null) return;
                }
                if (_hub!.activeSelf != _hubVisible) _hub.SetActive(_hubVisible);
                _hubUiHover = false;
                if (!_hubVisible)
                {
                    BizHideMergeTip();   // page / menu closed
                    if (_bizFocus != 0) CommitHubInputs();
                    BizSyncSelection();
                    if (_mergerConfirmGO != null && _mergerConfirmGO.activeSelf) _mergerConfirmGO.SetActive(false);
                    return;
                }
                if (!_hubNative) BizFitWindow();

                HandleHubTyping();
                BizTickFields();
                BizSyncSelection();

                bool rebuild = _bizDirty;
                if (MPHub.Version != _hubSeenVersion) { _hubSeenVersion = MPHub.Version; rebuild = true; }
                if (Time.unscaledTime >= _bizNextSig)
                {
                    _bizNextSig = Time.unscaledTime + 0.5f;
                    // The 6 s repay-confirm window ran out: the row redraws back to 'Pay all' by itself.
                    if (_hubRepayArm != "" && Time.unscaledTime - _hubRepayArmAt > 6f) { _hubRepayArm = ""; rebuild = true; }
                    BizFitLeft();
                    ulong s = BizSignature();
                    if (s != _bizSig) { _bizSig = s; rebuild = true; }
                    else BizRefreshHeaderButtons();
                }
                if (rebuild) { _bizDirty = false; BizRebuild(); }

                // In the NATIVE page the menu itself already blocks gameplay input; there only a FOCUSED box holds the
                // game's text-field gate (so the menu's hotkeys don't fire while typing and Esc leaves the box first) -
                // hover-based suppression fought the menu's own shortcuts (ESC took ~6 presses, 2026-06-10).
                var mp = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
                _hubUiHover = _hubNative ? _bizFocus != 0 : (HubHit(_hubRT, mp) || _bizFocus != 0);
                BizTickMergeTip(mp);
                if (!Input.GetMouseButtonDown(0)) return;
                BizClick(mp);
            }
            catch (Exception ex) { BizErr("TickHubWindow", ex); }
        }

        // ══ clicks ══

        private void BizClick(Vector2 mp)
        {
            // Merger confirm popup swallows every click while open: Confirm executes, anything else closes without acting.
            if (_mergerConfirmGO != null && _mergerConfirmGO.activeSelf)
            {
                if (BHit(_rtMergerOk, mp))
                {
                    if (_mergerConfirmMode == "propose" && !string.IsNullOrEmpty(_mergerConfirmPid))
                    {
                        // r4: ask and nothing else. The chip appears when the host's offer table says the offer exists.
                        if (MPServer.IsRunning) MPServer.HostMergerAction("propose", _mergerConfirmPid, MPConfig.PlayerId);
                        else                    MPClient.SendMergerAction("propose", _mergerConfirmPid);
                    }
                    else if (_mergerConfirmMode == "accept")
                    {
                        if (MPServer.IsRunning) MPServer.HostMergerAction("accept", "", MPConfig.PlayerId);
                        else                    MPClient.SendMergerAction("accept");
                    }
                    _bizDirty = true;
                }
                _mergerConfirmGO.SetActive(false);
                return;
            }

            if (!HubHit(_hubRT, mp)) { CommitHubInputs(); return; }   // click-away commits typing

            if (_bizAmt != null && BHit(_bizAmt.rt, mp)) { BizFocus(1); _hubAmountStr = ((long)_hubAmount).ToString(); return; }
            if (_bizRate != null && BHit(_bizRate.rt, mp)) { BizFocus(2); _hubRateStr = HubRate().ToString("F0"); return; }
            if (_bizTerm != null && BHit(_bizTerm.rt, mp)) { BizFocus(3); _hubTermStr = HubTerm().ToString(); return; }
            if (_bizPart != null && BHit(_bizPart.rt, mp)) { BizFocus(4); _hubPartialStr = ((long)_hubPartialAmount).ToString(); return; }
            CommitHubInputs();

            if (!_hubNative && BHit(_bizCloseRT, mp)) { _hubVisible = false; return; }
            for (int i = 0; i < 5; i++)
                if (BHit(_bizTabRT[i], mp)) { if (_hubTab != i) { _hubTab = i; _bizDirty = true; } return; }
            foreach (var (rt, key) in _bizCardHits)
            {
                if (!BHit(_bizCardsVp, mp) || !BHit(rt, mp)) continue;
                if (_bizSel != key) { _bizSel = key; _bizDirty = true; }
                return;
            }

            // Header card: the selected player.
            if (_bizMergeBtn != null && _bizMergeAct != 0 && BHit(_bizMergeBtn.rt, mp))
            {
                if (_bizMergeAct == 8) ShowMergerConfirm("propose", _bizMergePid);   // propose -> confirm popup first
                else if (_bizMergeAct == 12)                                       // withdraw my pending proposal (no confirm)
                {
                    if (MPServer.IsRunning) MPServer.HostMergerAction("unpropose", "", MPConfig.PlayerId);
                    else                    MPClient.SendMergerAction("unpropose");
                    _bizDirty = true;
                }
                return;
            }
            string target = BizTargetPid();
            if (_bizGiftBtn != null && BHit(_bizGiftBtn.rt, mp))
            {
                if (target != "") MPHub.OfferGift(target, (float)_hubAmount);
                BizRefreshHeaderButtons();
                return;
            }
            if (_bizLoanBtn != null && BHit(_bizLoanBtn.rt, mp))
            {
                if (target != "")
                {
                    // EXACT dailies (no ceiling) - the typed rate IS the effective rate (20% stays 20%, not 22%).
                    double pct = HubRate();
                    int term = HubTerm();
                    double di = Math.Max(0, _hubAmount * pct / 100.0 / term);
                    double dp = Math.Max(1, _hubAmount / term);
                    MPHub.OfferLoan(target, (float)_hubAmount, (float)di, (float)dp);
                }
                BizRefreshHeaderButtons();
                return;
            }
            if (_bizLeaveBtn != null && BHit(_bizLeaveBtn.rt, mp))   // leave the merger (a last pair dissolves it)
            {
                if (MPServer.IsRunning) MPServer.HostMergerAction("leave", "", MPConfig.PlayerId);
                else                    MPClient.SendMergerAction("leave");
                _bizDirty = true;
                return;
            }

            // Rows and checkboxes: the viewport gate keeps scrolled-off rows inert.
            if (!BHit(_bizRowsVp, mp)) return;
            foreach (var (rt, act, id, kind) in _bizHits)
            {
                if (id == "" || !BHit(rt, mp)) continue;
                switch (act)
                {
                    case 0: MPHub.AnswerOffer(id, true); break;
                    case 1: MPHub.AnswerOffer(id, false); break;
                    case 2: MPHub.CancelOffer(id); break;
                    case 3: BizRepayConfirm(id); break;   // confirm (second click) -> execute the armed repayment
                    case 4: _hubRepayArm = id; _hubRepayArmAmt = (float)_hubPartialAmount; _hubRepayArmAt = Time.unscaledTime; _bizDirty = true; break;
                    case 5: _hubRepayArm = id; _hubRepayArmAmt = 0f; _hubRepayArmAt = Time.unscaledTime; _bizDirty = true; break;
                    case 6:
                        if (id.StartsWith("pid:"))
                        {
                            string pid = id.Substring(4);
                            // Merger: the merger overrides the three permissions while it stands, so a co-member's box is
                            // inert (drawn locked). No store write, no toast -- the stored grant survives an unmerge.
                            if (MergerSync.IAmMember && MergerSync.IsMemberPid(pid))
                            {
                                Plugin.Logger.LogInfo($"[Merger] permission toggle ignored for co-member '{pid}' (merger overrides the three permissions)");
                                return;
                            }
                            bool now = !GrantSync.IsGranted(kind, MPConfig.PlayerId, pid);
                            if (MPServer.IsRunning) MPServer.HostSetGrant(kind, pid, now);
                            else                    MPClient.SendPermissionGrant(kind, pid, now);
                            _bizDirty = true;
                        }
                        break;
                    case 7:
                        if (id.StartsWith("stable:"))
                        {
                            string handle = id.Substring(7);
                            bool cur = false;
                            foreach (var g in GrantSync.MyGrantees()) if (g.Handle == handle) { cur = g.Kinds.Contains(kind); break; }
                            if (MPServer.IsRunning) MPServer.HostSetGrantOffline(kind, handle, !cur);
                            else                    MPClient.SendPermissionSetOffline(kind, handle, !cur);
                            _bizDirty = true;
                        }
                        break;
                    case 9: ShowMergerConfirm("accept", MergerSync.IncomingFromPid); break;   // accept -> confirm popup
                    case 10:                                                                     // decline (safe, no confirm)
                        if (MPServer.IsRunning) MPServer.HostMergerAction("decline", "", MPConfig.PlayerId);
                        else                    MPClient.SendMergerAction("decline");
                        _bizDirty = true;
                        break;
                }
                return;
            }
        }

        // ══ typing (today's input model) ══

        private void BizFocus(int f) { _bizFocus = f; _hubFreshFocus = true; }

        private double HubRate()
            => double.TryParse(_hubRateStr, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var r)
               ? Math.Min(Math.Max(r, 0.0), 1000.0) : 20.0;

        private int HubTerm()
            => int.TryParse(_hubTermStr, out var t) ? Math.Min(Math.Max(t, 1), 999) : 244;

        private void CommitHubInputs()
        {
            try
            {
                int f = _bizFocus;
                if (f == 0) return;
                _bizFocus = 0;
                if (f == 1) { if (long.TryParse(_hubAmountStr, out var v) && v > 0) _hubAmount = Math.Max(100, v); }
                else if (f == 2) _hubRateStr = HubRate().ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                else if (f == 3) _hubTermStr = HubTerm().ToString();
                else if (f == 4)
                {
                    if (long.TryParse(_hubPartialStr, out var pv) && pv > 0) _hubPartialAmount = Math.Max(1, pv);
                    _bizDirty = true;   // re-render rows so the 'Pay $x' buttons reflect the committed amount
                }
                BizRefreshHeaderButtons();
            }
            catch (Exception ex) { BizErr("CommitHubInputs", ex); }
        }

        private void HandleHubTyping()
        {
            if (_bizFocus == 0) return;
            string s = Input.inputString;
            foreach (char c in s)
            {
                // First keystroke after focusing REPLACES the value (2026-06-10).
                if (_hubFreshFocus && (char.IsDigit(c) || c == '.'))
                {
                    if (_bizFocus == 1) _hubAmountStr = "";
                    else if (_bizFocus == 2) _hubRateStr = "";
                    else if (_bizFocus == 3) _hubTermStr = "";
                    else if (_bizFocus == 4) _hubPartialStr = "";
                }
                if (c != '\n' && c != '\r') _hubFreshFocus = false;

                if (c == '\b')
                {
                    if (_bizFocus == 1 && _hubAmountStr.Length > 0) _hubAmountStr = _hubAmountStr.Substring(0, _hubAmountStr.Length - 1);
                    else if (_bizFocus == 2 && _hubRateStr.Length > 0) _hubRateStr = _hubRateStr.Substring(0, _hubRateStr.Length - 1);
                    else if (_bizFocus == 3 && _hubTermStr.Length > 0) _hubTermStr = _hubTermStr.Substring(0, _hubTermStr.Length - 1);
                    else if (_bizFocus == 4 && _hubPartialStr.Length > 0) _hubPartialStr = _hubPartialStr.Substring(0, _hubPartialStr.Length - 1);
                }
                else if (c == '\n' || c == '\r') { CommitHubInputs(); break; }
                else if (_bizFocus == 1 && char.IsDigit(c) && _hubAmountStr.Length < 9) _hubAmountStr += c;
                else if (_bizFocus == 2 && (char.IsDigit(c) || (c == '.' && !_hubRateStr.Contains('.'))) && _hubRateStr.Length < 5) _hubRateStr += c;
                else if (_bizFocus == 3 && char.IsDigit(c) && _hubTermStr.Length < 3) _hubTermStr += c;
                else if (_bizFocus == 4 && char.IsDigit(c) && _hubPartialStr.Length < 9) _hubPartialStr += c;
            }
            if (_bizFocus == 4 && s.Length > 0) _bizDirty = true;   // live-update the 'Pay $x' buttons as you type
            if (_bizFocus != 0 && Input.GetKeyDown(KeyCode.Escape)) BizEscLeave();
        }

        /// <summary>'Confirm $x' click. A click within 0.5 s of arming is the second half of a double-click on 'Pay all'
        /// (the Confirm takes that button's spot): ignored, the row stays armed. Returns true when the repayment was asked.</summary>
        private bool BizRepayConfirm(string id)
        {
            if (BizRepayTooSoon(id)) return false;
            bool paid = false;
            if (_hubRepayArm == id && Time.unscaledTime - _hubRepayArmAt < 6f) { MPHub.RequestRepay(id, _hubRepayArmAmt); paid = true; }
            _hubRepayArm = ""; _bizDirty = true;
            return paid;
        }

        private bool BizRepayTooSoon(string id) => _hubRepayArm == id && Time.unscaledTime - _hubRepayArmAt < 0.5f;

        /// <summary>Esc while a box is focused: leave the box KEEPING what was typed (committed as a click-away does). The box
        /// stays the EventSystem's selected object until a later frame, so the game's own Esc handler (GameManager
        /// GlobalKeyEvents -> CancelButtonHandler.HandleEscapeClick -> HasInputSelectedConsumedClick) swallows THIS press
        /// and only the next Esc closes the menu.</summary>
        private void BizEscLeave()
        {
            _bizEscFrame = Time.frameCount;
            CommitHubInputs();
        }

        /// <summary>A focused box is the EventSystem's selected object, on the UI layer - the game's own test for "typing in a
        /// field" (GameManager.HasInputSelected). Released once focus leaves, never on the frame Esc left the box. No allocation.</summary>
        /// <summary>Re-check MEDIUM/LOW: the page is not ticked once the link is gone (MPCanvasUI's no-link return), so a
        /// focused box (holding the EventSystem selection = game hotkeys and Esc blocked) or an open merger popup (a
        /// full-screen click catcher) would stay. Called every frame on that early-return path; cheap no-op when clean.</summary>
        internal void BizOnLinkLost()
        {
            try
            {
                if (_bizFocus != 0) _bizFocus = 0;
                if (_bizSelGO != null)
                {
                    var es = EventSystem.current;
                    if (es != null && es.currentSelectedGameObject == _bizSelGO) es.SetSelectedGameObject(null);
                    _bizSelGO = null;
                }
                if (_mergerConfirmGO != null && _mergerConfirmGO.activeSelf) _mergerConfirmGO.SetActive(false);
                BizHideMergeTip();
            }
            catch (Exception ex) { BizErr("link lost", ex); }
        }

        private void BizSyncSelection()
        {
            try
            {
                var es = EventSystem.current;
                if (es == null) return;
                BField? f = _bizFocus == 1 ? _bizAmt : _bizFocus == 2 ? _bizRate : _bizFocus == 3 ? _bizTerm : _bizFocus == 4 ? _bizPart : null;
                GameObject? want = f != null && _hubVisible ? f.rt.gameObject : null;
                if (want != null && want.activeInHierarchy)
                {
                    // Re-check LOW: another TYPING box took the selection (the chat's input) - hand it over instead of
                    // stealing it back every frame: our box commits and loses focus. Only typing boxes count: a native
                    // button the game left selected must not cancel our focus (rig T-HUBSHOTS-20260928-160014 step 102).
                    var cur = es.currentSelectedGameObject;
                    if (cur != null && cur != want && cur != _bizSelGO
                        && (cur.GetComponent<TMPro.TMP_InputField>() != null || cur.GetComponent<UnityEngine.UI.InputField>() != null))
                    {
                        CommitHubInputs(); _bizFocus = 0; _bizSelGO = null;
                        return;
                    }
                    if (_bizUiLayer == -2) { _bizUiLayer = LayerMask.NameToLayer("UI"); if (_bizUiLayer < 0) _bizUiLayer = 5; }
                    if (want.layer != _bizUiLayer) want.layer = _bizUiLayer;
                    if (es.currentSelectedGameObject != want) es.SetSelectedGameObject(want);
                    _bizSelGO = want;
                    return;
                }
                if (_bizSelGO == null) return;
                if (Time.frameCount == _bizEscFrame) return;   // the game's Esc handler may still read it this frame
                if (es.currentSelectedGameObject == _bizSelGO) es.SetSelectedGameObject(null);
                _bizSelGO = null;
            }
            catch (Exception ex) { BizErr("selection", ex); }
        }

        /// <summary>Box texts + help line: rewritten only when a value, the focus or the caret blink changes.</summary>
        private void BizTickFields()
        {
            try
            {
                bool caret = _bizFocus != 0 && Mathf.FloorToInt(Time.unscaledTime * 2f) % 2 == 0;
                int k = _bizFocus * 31 + (caret ? 7 : 0);
                k = k * 31 + _hubAmountStr.GetHashCode(); k = k * 31 + _hubRateStr.GetHashCode(); k = k * 31 + _hubTermStr.GetHashCode();
                k = k * 31 + _hubPartialStr.GetHashCode(); k = k * 31 + _hubAmount.GetHashCode(); k = k * 31 + _hubPartialAmount.GetHashCode();
                if (k != _bizFieldKey)
                {
                    _bizFieldKey = k;
                    string cur = caret ? "|" : "";
                    BSetField(_bizAmt, _bizFocus == 1 ? "$" + _hubAmountStr + cur : "$" + _hubAmount.ToString("N0"), _bizFocus == 1);
                    BSetField(_bizRate, _bizFocus == 2 ? _hubRateStr + cur + " %" : HubRate().ToString("F0") + " %", _bizFocus == 2);
                    BSetField(_bizTerm, _bizFocus == 3 ? _hubTermStr + cur + " days" : HubTerm() + " days", _bizFocus == 3);
                    BSetField(_bizPart, _bizFocus == 4 ? "$" + _hubPartialStr + cur : "$" + _hubPartialAmount.ToString("N0"), _bizFocus == 4);
                }
                if (_bizHelp == null) return;
                var sel = BizSelected();
                bool helpOn = sel != null && BizTargetPid() != "";   // offline: the offers are greyed, no loan help
                string name = sel?.Name ?? "";
                int h = name.GetHashCode() * 31 + _hubAmount.GetHashCode();
                h = h * 31 + HubRate().GetHashCode(); h = h * 31 + HubTerm(); h = h * 31 + (helpOn ? 1 : 0);
                if (h == _bizHelpKey) return;
                _bizHelpKey = h;
                if (!helpOn) { _bizHelp.text = ""; return; }
                // Bank convention: rate = TOTAL premium over the term. EXACT dailies (no ceiling, 2026-06-10).
                double pct = HubRate();
                int term = HubTerm();
                double di = Math.Max(0, _hubAmount * pct / 100.0 / term);
                double dp = Math.Max(1, _hubAmount / term);
                _bizHelp.text = $"{name} must accept before any money moves. A loan of ${_hubAmount:N0} costs them ${di:N0} a day in interest plus " +
                                $"${dp:N0} a day back, for {term} days: ${_hubAmount * pct / 100.0:N0} interest in total ({pct:F0}%). The bank charges 20% for 244 days.";
            }
            catch (Exception ex) { BizErr("fields", ex); }
        }

        private static void BSetField(BField? f, string text, bool focus)
        {
            if (f == null) return;
            f.lbl.text = text;
            f.line.color = focus ? B_FIELDFOC : B_FIELDLINE;
        }

        // ══ data ══

        private BizPl? BizSelected()
        {
            foreach (var p in _bizPls) if (p.Key == _bizSel) return p;
            return null;
        }

        /// <summary>The selected player IF today's page could have targeted them (an online player - today's chips only
        /// ever listed the online roster); "" otherwise.</summary>
        private string BizTargetPid()
        {
            var p = BizSelected();
            return p != null && p.Online && p.Pid != "" ? p.Pid : "";
        }

        private static ulong BMix(ulong h, int v) => (h ^ (uint)v) * 1099511628211UL;
        private static ulong BMix(ulong h, string? s) => BMix(h, s == null ? 0 : s.GetHashCode());

        /// <summary>Everything the cards and tables show that MPHub.Version does not cover: the roster and names,
        /// grants, merger state and the company's car counts. Twice a second.</summary>
        private ulong BizSignature()
        {
            ulong h = 1469598103934665603UL;
            try
            {
                string me = MPConfig.PlayerId ?? "";
                foreach (var pl in MPRestSync.AllPlayers())
                {
                    if (pl == me) continue;
                    h = BMix(h, pl); h = BMix(h, CwName(pl));
                    h = BMix(h, (GrantSync.IsGranted(GrantKind.Vehicle, me, pl) ? 1 : 0) | (GrantSync.IsGranted(GrantKind.Housing, me, pl) ? 2 : 0)
                              | (GrantSync.IsGranted(GrantKind.Business, me, pl) ? 4 : 0) | (MergerSync.IsMemberPid(pl) ? 8 : 0) | (MergerSync.InAnyGroup(pl) ? 16 : 0));
                }
                h = BMix(h, MergerSync.IAmMember ? 1 : 0);
                h = BMix(h, MergerSync.IncomingFromPid); h = BMix(h, MergerSync.OutgoingToPid);
                if (MergerSync.IAmMember)
                {
                    h = BMix(h, MergerSync.MyGroupDisplayName);
                    foreach (var mp in MergerSync.MyMemberPidsOrdered)
                    { h = BMix(h, mp); h = BMix(h, mp == me ? VehicleManager.LocalVehicleCount() : VehicleManager.GhostCountFor(mp)); }
                }
                foreach (var g in GrantSync.MyGrantees())
                {
                    if (g == null) continue;
                    h = BMix(h, g.Handle); h = BMix(h, g.Name); h = BMix(h, g.Online ? 1 : 0);
                    foreach (var k in g.Kinds) h = BMix(h, (int)k + 3);
                }
            }
            catch (Exception ex) { BizErr("signature", ex); }
            return h;
        }

        /// <summary>Every other ONLINE player, plus anyone offline I have an offer, loan, grant or company link with;
        /// online first, then by name.</summary>
        private void BizCollectPlayers()
        {
            _bizPls.Clear(); _bizOnline.Clear();
            string me = MPConfig.PlayerId ?? "";
            var seen = new HashSet<string>();
            foreach (var pl in MPRestSync.AllPlayers())
            {
                if (string.IsNullOrEmpty(pl) || pl == me) continue;
                _bizOnline.Add(pl);
                if (seen.Add(pl)) _bizPls.Add(new BizPl { Key = pl, Pid = pl, Name = CwName(pl), Online = true });
            }
            void Off(string pid)
            {
                if (string.IsNullOrEmpty(pid) || pid == me || !seen.Add(pid)) return;
                _bizPls.Add(new BizPl { Key = pid, Pid = pid, Name = CwName(pid) });
            }
            foreach (var o in MPHub.IncomingOffers) Off(o.From);
            foreach (var o in MPHub.OutgoingOffers) Off(o.To);
            foreach (var l in MPHub.Loans) { if (l.Borrower == me) Off(l.Lender); else if (l.Lender == me) Off(l.Borrower); }
            bool member = MergerSync.IAmMember;
            if (member) foreach (var mp in MergerSync.MyMemberPidsOrdered) Off(mp);
            foreach (var p in _bizPls) p.Co = member && (p.Online ? MergerSync.IsMemberPid(p.Pid) : Contains(MergerSync.MyMemberPidsOrdered, p.Pid));
            // Offline grantees (known by StableId handle). Matched to a card by pid / handle ONLY - never by display name, so
            // two grantees with the same name each keep their own card and Access row. An unknown name reads 'Unknown player'.
            foreach (var g in GrantSync.MyGrantees())
            {
                if (g == null || g.Online || string.IsNullOrEmpty(g.Handle)) continue;
                BizPl? same = null;
                foreach (var p in _bizPls) if (p.Handle == g.Handle || (p.Pid != "" && p.Pid == g.Handle)) { same = p; break; }
                if (same != null) { if (!same.Online && same.Grant == null) { same.Handle = g.Handle; same.Grant = g; } continue; }
                string key = "stable:" + g.Handle;
                if (!seen.Add(key)) continue;
                _bizPls.Add(new BizPl { Key = key, Handle = g.Handle, Name = string.IsNullOrEmpty(g.Name) ? B_UNKNOWN : g.Name, Grant = g });
            }
            foreach (var o in MPHub.IncomingOffers)
                foreach (var p in _bizPls) if (p.Pid != "" && p.Pid == o.From) { p.Incoming++; break; }
            _bizPls.Sort((a, b) =>   // online first, then by name; ties by pid (then key) so the order never shuffles
            {
                if (a.Online != b.Online) return a.Online ? -1 : 1;
                int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.CompareOrdinal(a.Pid, b.Pid);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            // Nothing selected (or the selection left) -> the first online player, else the first card.
            if (BizSelected() == null) _bizSel = _bizPls.Count > 0 ? _bizPls[0].Key : "";
        }

        private static bool Contains(IReadOnlyList<string> l, string s)
        {
            for (int i = 0; i < l.Count; i++) if (l[i] == s) return true;
            return false;
        }

        private static string BizStatus(BizPl p) => (p.Online ? "Online" : "Offline") + (p.Co ? " · Your company" : "");

        // ══ rebuild (on a data change only) ══

        private void BizRebuild()
        {
            if (_bizRoot == null) return;
            try
            {
                BizLayoutStatic();
                BizCollectPlayers();
                BizBuildCards();
                BizBuildHeader();
                BizBuildTabs();
                BizBuildTable();
                BizRefreshHeaderButtons();
                _bizHelpKey = int.MinValue; _bizFieldKey = int.MinValue;
            }
            catch (Exception ex) { BizErr("rebuild", ex); }
        }

        /// <summary>Widths that come from measured text (tabs, Offer buttons, Leave merger). TMP measures nothing useful
        /// while the page is still hidden (the first build happens then), so this runs on every rebuild while it is live.</summary>
        private void BizLayoutStatic()
        {
            if (_bizRoot == null || !_bizRoot.gameObject.activeInHierarchy) return;
            float tx = PX(669f);
            for (int i = 0; i < 5; i++)
            {
                if (_bizTabRT[i] == null) continue;
                float w = BMeasure(B_TABS[i], 13f, true, 6f) + 40f;
                BPos(_bizTabRT[i]!, tx, PY(392f), w, 46f + 4f);
                if (_bizTabLbl[i] != null) _bizTabLbl[i]!.rectTransform.sizeDelta = new Vector2(w * BK, 46f * BK);
                if (i == 0 && _bizTabBadgeHost != null)
                {
                    BPos(_bizTabBadgeHost, tx, PY(392f), w, 46f + 4f);   // tab 0's rect, drawn after every tab
                    if (_bizTabBadge != null) _bizTabBadge.GetComponent<RectTransform>().anchoredPosition = new Vector2((w + 8f) * BK, -3f * BK);
                }
                tx += w + 2f;
            }
            float wl = BMeasure("Offer loan", 13f, true) + 32f, wg = BMeasure("Offer gift", 13f, true) + 32f;
            if (_bizLoanBtn != null) BPos(_bizLoanBtn.rt, 1195f - wl, 127f, wl, 38f);
            if (_bizGiftBtn != null) BPos(_bizGiftBtn.rt, 1195f - wl - 12f - wg, 127f, wg, 38f);
            float wlv = BMeasure("Leave merger", 13f, true) + 32f;
            if (_bizLeaveBtn != null) BPos(_bizLeaveBtn.rt, _bizToolW - wlv, 7f, wlv, 32f);
            if (_bizCompanyLbl != null) BPos(_bizCompanyLbl.rectTransform, 0f, 0f, _bizToolW - wlv - 12f, 46f);
        }

        private void BizBuildCards()
        {
            if (_bizCardsContent == null) return;
            BClear(_bizCardsContent);
            _bizCardHits.Clear();
            for (int i = 0; i < _bizPls.Count; i++)
            {
                var p = _bizPls[i];
                bool on = p.Key == _bizSel;
                var card = BPanel(_bizCardsContent, "Card", on ? Color.white : B_ROW, 4f, on ? 14f : 6f, on ? 0.22f : 0.18f, on ? 4f : 2f);
                BPos(card, 16f, 10f + i * B_CARDPITCH, 470f, 78f);   // 12 px in: the viewport is padded so shadows show
                BAvatar(card, p.Pid, p.Name, 16f, 17f, 44f, 18f);
                float tw = p.Incoming > 0 ? 340f : 370f;
                BText(card, p.Name, 16f, on ? B_INK : Color.white, 76f, 17f, tw, 22f, TextAlignmentOptions.Left, true);
                BText(card, BizStatus(p), 14f, on ? B_GREY : B_SUB, 76f, 42f, tw, 20f, TextAlignmentOptions.Left);
                // The count sits INSIDE the card (right, centred), so the list viewport clips it with its card.
                if (p.Incoming > 0) BBadge(card, p.Incoming, 470f - 16f, 39f - 11f);
                _bizCardHits.Add((card, p.Key));
            }
            _bizCardsContent.sizeDelta = new Vector2(0f, Mathf.Max(B_CARDSH, _bizPls.Count * B_CARDPITCH) * BK);
        }

        private void BizBuildHeader()
        {
            var p = BizSelected();
            bool any = p != null;
            if (_bizHdrAv != null) _bizHdrAv.gameObject.SetActive(any);
            if (p != null)
            {
                if (_bizHdrAv != null) _bizHdrAv.color = CwColour(p.Pid);
                if (_bizHdrLetter != null) _bizHdrLetter.text = BLetter(p.Name);
            }
            if (_bizHdrName != null) _bizHdrName.text = p?.Name ?? "";
            if (_bizHdrSub != null) _bizHdrSub.text = p != null ? BizStatus(p) : "";

            // Merger button, exactly where today's chip offered one: my proposal waiting on them -> 'Cancel merger
            // offer'; a co-member ('Merged') or their company already holds my offer ('In a company') -> none;
            // otherwise 'Propose merger'. Online players only (today's chips never listed anyone offline).
            _bizMergeAct = 0; _bizMergePid = "";
            string label = "";
            if (p != null && p.Online && p.Pid != "")
            {
                string pl = p.Pid;
                if (MergerSync.IAmMember && MergerSync.IsMemberPid(pl)) { }
                else if (MergerSync.OutgoingToPid == pl) { label = "Cancel merger offer"; _bizMergeAct = 12; }
                else if (MergerSync.InAnyGroup(pl) && MergerSync.MergedRuntime(MergerSync.OutgoingToPid, pl)) { }
                else { label = "Propose merger"; _bizMergeAct = 8; }
                _bizMergePid = pl;
            }
            if (_bizMergeBtn != null)
            {
                _bizMergeBtn.go.SetActive(label != "");
                if (label != "")
                {
                    _bizMergeBtn.lbl.text = label;
                    float w = BMeasure(label, 13f, true) + 32f;
                    BPos(_bizMergeBtn.rt, 1221f - 26f - w, 31f, w, 32f);
                }
            }
        }

        /// <summary>Offer buttons greyed exactly where today's MPHub would refuse (no target, an offer of that kind
        /// already waiting on them, or not enough uncommitted money). A greyed button on a real target still makes
        /// today's call, so today's notice explains the refusal.</summary>
        private void BizRefreshHeaderButtons()
        {
            try
            {
                string t = BizTargetPid();
                bool gift = t != "", loan = t != "";
                if (t != "")
                {
                    foreach (var o in MPHub.OutgoingOffers)
                    {
                        if (o.To != t) continue;
                        if (o.Kind == "gift") gift = false;
                        else if (o.Kind == "loan") loan = false;
                    }
                    if (MPHub.AvailableMoney() < _hubAmount) { gift = false; loan = false; }
                }
                BSetKind(_bizGiftBtn, gift ? 0 : 5);
                BSetKind(_bizLoanBtn, loan ? 1 : 5);
            }
            catch (Exception ex) { BizErr("header buttons", ex); }
        }

        private void BizBuildTabs()
        {
            for (int i = 0; i < 5; i++)
            {
                bool on = i == _hubTab;
                if (_bizTabImg[i] != null) _bizTabImg[i]!.color = on ? Color.white : B_TABOFF;
                if (_bizTabLbl[i] != null) _bizTabLbl[i]!.color = on ? B_INK : B_TABOFFTXT;
            }
            int n = MPHub.IncomingOffers.Count;
            if (_bizTabBadge != null)
            {
                _bizTabBadge.SetActive(n > 0);
                if (n > 0 && _bizTabBadgeLbl != null)
                {
                    _bizTabBadgeLbl.text = n.ToString();
                    var brt = _bizTabBadge.GetComponent<RectTransform>();
                    float w = Mathf.Max(22f, BMeasure(_bizTabBadgeLbl.text, 12f, true) + 10f);
                    brt.sizeDelta = new Vector2(w * BK, 22f * BK);
                }
            }
        }

        private float BizColX(int tab, int col)
        {
            float x = 24f;
            for (int i = 0; i < col; i++) x += BizColW(tab, i);
            return x;
        }

        private float BizColW(int tab, int col)
        {
            var w = B_COLW[tab];
            if (w[col] >= 0f) return w[col];
            float fixedW = 0f;
            foreach (var v in w) if (v >= 0f) fixedW += v;
            return B_ROWW - 24f - 16f - fixedW - B_ACTW[tab] - 12f;
        }

        private void BizBuildTable()
        {
            if (_bizRowsContent == null || _bizHeadCols == null) return;
            int tab = Mathf.Clamp(_hubTab, 0, 4);
            // Column labels line up with the cells below (the rows start 20 px inside the header card).
            BClear(_bizHeadCols);
            for (int c = 0; c < B_COLS[tab].Length; c++)
                BText(_bizHeadCols, B_COLS[tab][c], 15f, B_HEADTXT, 20f + BizColX(tab, c), 0f, BizColW(tab, c) - 8f, 58f, TextAlignmentOptions.Left, true, false, 1f);

            BClear(_bizRowsContent);
            _bizHits.Clear();
            int n = 0;
            string empty = "";
            bool repayable = false;
            string me = MPConfig.PlayerId ?? "";
            if (tab == 0)
            {
                foreach (var o in MPHub.IncomingOffers)
                {
                    var row = BRow(n++);
                    BNameCell(row, tab, 0, o.From, _bizOnline.Contains(o.From) ? "" : "Offline");
                    BCell(row, tab, 1, BizKind(o.Kind), Color.white, true);
                    BCell(row, tab, 2, $"${o.Principal:N0}", Color.white, true);
                    BCell(row, tab, 3, o.Kind == "gift" ? "" : o.Kind == "business"
                        ? $"{o.BusinessName}: the business, its furniture and staff"
                        : $"{MPHub.OfferTotalPct(o):F0}% for {MPHub.OfferTermDays(o)} days: you pay ${o.DailyInterest:N0} + ${o.DailyPayment:N0} a day", B_MUTED, false, true);
                    BRowButtons(row, ("Accept", 0, (byte)0, o.Id, GrantKind.Vehicle), ("Decline", 2, (byte)1, o.Id, GrantKind.Vehicle));
                }
                empty = "No offers right now.";
            }
            else if (tab == 1)
            {
                foreach (var o in MPHub.OutgoingOffers)
                {
                    var row = BRow(n++);
                    BNameCell(row, tab, 0, o.To, _bizOnline.Contains(o.To) ? "" : "Offline");
                    BCell(row, tab, 1, BizKind(o.Kind), Color.white, true);
                    BCell(row, tab, 2, $"${o.Principal:N0}", Color.white, true);
                    BCell(row, tab, 3, o.Kind == "gift" ? "" : o.Kind == "business"
                        ? $"{o.BusinessName}: the business, its furniture and staff"
                        : $"{MPHub.OfferTotalPct(o):F0}% for {MPHub.OfferTermDays(o)} days: they pay ${o.DailyInterest:N0} + ${o.DailyPayment:N0} a day", B_MUTED, false, true);
                    BCell(row, tab, 4, "Waiting", B_MUTED, false);
                    BRowButtons(row, ("Cancel", 3, (byte)2, o.Id, GrantKind.Vehicle));
                }
                empty = "You have no offers waiting.";
            }
            else if (tab == 2)
            {
                // Loans you OWE get Pay buttons (two-click confirm) unless the lender is offline (the host can't credit
                // them - greyed); loans owed TO you are display-only.
                if (_hubRepayArm != "" && Time.unscaledTime - _hubRepayArmAt > 6f) _hubRepayArm = "";
                int mine = 0;
                // While the part-payment box is focused, the buttons show the LIVE typed value; else the committed one.
                float pShow = (_bizFocus == 4 && long.TryParse(_hubPartialStr, out var pParse) && pParse > 0) ? pParse : (float)_hubPartialAmount;
                foreach (var ln in MPHub.Loans)
                {
                    bool owe = ln.Borrower == me;
                    if (!owe && ln.Lender != me) continue;
                    mine++;
                    string other = owe ? ln.Lender : ln.Borrower;
                    int dleft = (int)Math.Ceiling(ln.Remaining / Math.Max(1f, ln.DailyPayment));
                    var row = BRow(n++);
                    BNameCell(row, tab, 0, other, _bizOnline.Contains(other) ? "" : "Offline");
                    BCell(row, tab, 1, owe ? "You owe" : "Owes you", Color.white, true);
                    BCell(row, tab, 2, $"${ln.Remaining:N0}", Color.white, true);
                    BCell(row, tab, 3, $"${ln.DailyInterest:N0} + ${ln.DailyPayment:N0}", B_MUTED, false);
                    BCell(row, tab, 4, dleft.ToString(), Color.white, true);
                    if (!owe) continue;
                    float pAmt = Math.Min(pShow, ln.Remaining);
                    // 'Pay all' and its 'Confirm $x' share ONE slot (same spot, same width): the Confirm never reaches over
                    // the 'Pay $x' spot, and a double-click on 'Pay all' meets the 0.5 s guard (BizRepayConfirm).
                    float slot = Mathf.Max(BMeasure("Pay all", 13f, true), BMeasure($"Confirm ${ln.Remaining:N0}", 13f, true) + 6f) + 32f;
                    if (!_bizOnline.Contains(ln.Lender))
                        BRowButtonsW(row, slot, ("Pay all", 4, (byte)0, "", GrantKind.Vehicle), ($"Pay ${pAmt:N0}", 4, (byte)0, "", GrantKind.Vehicle));
                    else if (_hubRepayArm == ln.Id)
                    {
                        repayable = true;
                        float amt = _hubRepayArmAmt <= 0f ? ln.Remaining : Math.Min(_hubRepayArmAmt, ln.Remaining);
                        BRowButtonsW(row, slot, ($"Confirm ${amt:N0}", 1, (byte)3, ln.Id, GrantKind.Vehicle));
                    }
                    else
                    {
                        repayable = true;
                        BRowButtonsW(row, slot, ("Pay all", 0, (byte)5, ln.Id, GrantKind.Vehicle), ($"Pay ${pAmt:N0}", 1, (byte)4, ln.Id, GrantKind.Vehicle));
                    }
                }
                if (MPHub.Loans.Count > 0 && mine == 0)
                    Plugin.Logger.LogWarning($"[Hub] {MPHub.Loans.Count} active loan(s) but none match my id '{MPConfig.PlayerId}'");
                empty = "No active loans.";
            }
            else if (tab == 3)
            {
                foreach (var p in _bizPls)
                {
                    if (p.Online)
                    {
                        // A merger is a SUPERSET of the three permissions and overrides them while it stands: a
                        // co-member's boxes are drawn locked (ticked) and their click is ignored as today.
                        var row = BRow(n++);
                        BNameCell(row, tab, 0, p.Pid, p.Co ? "Your company: shares everything" : "");
                        for (int k = 0; k < 3; k++)
                        {
                            var kind = (GrantKind)k;
                            BCheckCell(row, tab, k + 1, p.Co || GrantSync.IsGranted(kind, me, p.Pid), p.Co, (byte)6, "pid:" + p.Pid, kind);
                        }
                    }
                    else if (p.Co)
                    {
                        var row = BRow(n++);
                        BNameCell(row, tab, 0, p.Pid, "Offline · Your company: shares everything");
                        for (int k = 0; k < 3; k++) BCheckCell(row, tab, k + 1, true, true, 0, "", GrantKind.Vehicle);
                    }
                    else if (p.Grant != null && p.Handle != "")
                    {
                        var row = BRow(n++);
                        BNameCell(row, tab, 0, p.Pid, "Offline", p.Name);
                        for (int k = 0; k < 3; k++)
                        {
                            var kind = (GrantKind)k;
                            BCheckCell(row, tab, k + 1, p.Grant.Kinds.Contains(kind), false, (byte)7, "stable:" + p.Handle, kind);
                        }
                    }
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(MergerSync.IncomingFromPid))
                {
                    var row = BRow(n++);
                    BNameCell(row, tab, 0, MergerSync.IncomingFromPid, "");
                    BCell(row, tab, 2, "Proposes a company merger", B_MUTED, false);
                    BRowButtons(row, ("Accept", 0, (byte)9, "merger", GrantKind.Vehicle), ("Decline", 2, (byte)10, "merger", GrantKind.Vehicle));
                }
                if (MergerSync.IAmMember)
                {
                    foreach (var mp in MergerSync.MyMemberPidsOrdered)
                    {
                        if (string.IsNullOrEmpty(mp)) continue;
                        bool isMe = mp == me;
                        var row = BRow(n++);
                        BNameCell(row, tab, 0, mp, isMe ? "You" : "");
                        BCell(row, tab, 1, (isMe ? VehicleManager.LocalVehicleCount() : VehicleManager.GhostCountFor(mp)).ToString(), Color.white, true);
                        BCell(row, tab, 2, isMe || _bizOnline.Contains(mp) ? "Online" : "Offline", B_MUTED, false);
                    }
                    // Phase 1-A (D3): the company's OWN name; a host that sends no name falls back to the roster.
                    string co = MergerSync.MyGroupDisplayName;
                    if (string.IsNullOrEmpty(co))   // display names (a member whose name has not arrived yet shows its account name)
                    {
                        var nm = new List<string>();
                        foreach (var mp in MergerSync.MyMemberPidsOrdered) if (!string.IsNullOrEmpty(mp)) nm.Add(CwName(mp));
                        co = string.Join(", ", nm);
                    }
                    if (_bizCompanyLbl != null) _bizCompanyLbl.text = "Your company: " + co;
                }
                else
                {
                    var t = BText(_bizRowsContent, "You're not in a company. Pick a player on the left and choose Propose merger.", 15f, B_MUTED,
                                  24f, n * B_PITCH, B_ROWW - 48f, B_ROWH, TextAlignmentOptions.Left);
                    t.fontStyle = FontStyles.Normal;
                }
            }
            if (n == 0 && empty != "")
                BText(_bizRowsContent, empty, 15f, B_MUTED, 24f, 0f, B_ROWW - 48f, B_ROWH, TextAlignmentOptions.Left);
            _bizRowCount = n;
            _bizRowsContent.sizeDelta = new Vector2(0f, Mathf.Max(B_ROWSH, n * B_PITCH - (B_PITCH - B_ROWH)) * BK);

            if (_bizToolLoans != null) _bizToolLoans.SetActive(tab == 2 && repayable);
            if (_bizToolAccess != null) _bizToolAccess.SetActive(tab == 3);
            if (_bizToolCompany != null) _bizToolCompany.SetActive(tab == 4 && MergerSync.IAmMember);
        }

        private static string BizKind(string kind) => kind == "gift" ? "Gift" : kind == "business" ? "Business sale" : "Loan";

        // ══ row builders ══

        private RectTransform BRow(int idx)
        {
            var row = BPanel(_bizRowsContent!, "Row", B_ROW, 4f, 6f, 0.18f, 2f);
            BPos(row, 0f, idx * B_PITCH, B_ROWW, B_ROWH);
            return row;
        }

        private void BCell(RectTransform row, int tab, int col, string text, Color c, bool bold, bool wrap = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            var t = BText(row, text, wrap ? 14f : 15f, c, BizColX(tab, col), 0f, BizColW(tab, col) - 12f, B_ROWH, TextAlignmentOptions.Left, bold, false, 0f, wrap);
            if (wrap) t.lineSpacing = -8f;
        }

        private void BNameCell(RectTransform row, int tab, int col, string pid, string sub, string? name = null)
        {
            name ??= CwName(pid);
            float x = BizColX(tab, col), w = BizColW(tab, col) - 38f - 12f;
            BAvatar(row, pid, name, x, 11f, 28f, 12f);
            if (sub == "") { BText(row, name, 15f, Color.white, x + 38f, 0f, w, B_ROWH, TextAlignmentOptions.Left, true); return; }
            BText(row, name, 15f, Color.white, x + 38f, 6f, w, 20f, TextAlignmentOptions.Left, true);
            BText(row, sub, 13f, new Color(1f, 1f, 1f, 0.7f), x + 38f, 26f, w, 18f, TextAlignmentOptions.Left);
        }

        private void BRowButtons(RectTransform row, params (string label, int kind, byte act, string id, GrantKind k)[] btns)
            => BRowButtonsW(row, 0f, btns);

        /// <summary>Row buttons, first = rightmost; the first takes <paramref name="firstW"/> mock px when that is > 0.</summary>
        private void BRowButtonsW(RectTransform row, float firstW, params (string label, int kind, byte act, string id, GrantKind k)[] btns)
        {
            float right = B_ROWW - 16f;
            for (int i = 0; i < btns.Length; i++)
            {
                var b = btns[i];
                float w = i == 0 && firstW > 0f ? firstW : BMeasure(b.label, 13f, true) + 32f;
                right -= w;
                var btn = BButton(row, b.label, b.kind, right, 9f, 32f, w);
                right -= 10f;
                if (b.kind != 4 && b.id != "") _bizHits.Add((btn.rt, b.act, b.id, b.k));
            }
        }

        private void BCheckCell(RectTransform row, int tab, int col, bool on, bool locked, byte act, string id, GrantKind kind)
        {
            float x = BizColX(tab, col);
            var box = LImg(row, "Check", locked ? new Color(1f, 1f, 1f, 0.45f) : Color.white, LRoundSprite(false), 4f * BK);
            BPos(box.rectTransform, x, 13f, 24f, 24f);
            if (on)
            {
                var ink = locked ? new Color(B_INK.r, B_INK.g, B_INK.b, 0.6f) : B_INK;
                CwBar(box.transform, ink, -4.4f * BK, -1.6f * BK, 7.2f * BK, 3f * BK, -45f);
                CwBar(box.transform, ink, 1.8f * BK, 0.8f * BK, 13.5f * BK, 3f * BK, 45f);
            }
            if (act == 0 || id == "") return;
            var hit = MakeGO("Hit", row);   // a finger-sized target around the box
            var hrt = hit.GetComponent<RectTransform>();
            BPos(hrt, x - 10f, 3f, 44f, 44f);
            _bizHits.Add((hrt, act, id, kind));
        }

        // ══ build (once per host) ══

        /// <summary>Fallback when the phone page cannot be injected: the same page in a plain window titled 'Business'
        /// with a red close button, fitted to the screen.</summary>
        private void BuildBizWindow()
        {
            try
            {
                _hubNative = false; _hubCam = null;
                var win = MakeGO("BAMP_Hub", _canvasGO!.transform);
                var wrt = win.GetComponent<RectTransform>();
                wrt.anchorMin = wrt.anchorMax = wrt.pivot = new Vector2(0.5f, 0.5f);
                wrt.sizeDelta = new Vector2(1920f, 915f); wrt.anchoredPosition = Vector2.zero;
                var sh = LImg(win.transform, "Shadow", new Color(0f, 0f, 0f, 0.45f), LShadowSprite(), 0f);
                sh.pixelsPerUnitMultiplier = 1f;
                LStretch(sh.rectTransform, -24f, -30f, -24f, -18f);
                var body = LImg(win.transform, "Body", B_WINBG, LRoundSprite(false), 4f);
                body.raycastTarget = true;   // the window swallows clicks (they never reach the world behind)
                LStretch(body.rectTransform, 0f, 0f, 0f, 0f);
                var bar = LImg(win.transform, "Bar", L_BAR, LRoundSprite(false), 4f).rectTransform;
                bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = Vector2.one; bar.pivot = new Vector2(0.5f, 1f);
                bar.sizeDelta = new Vector2(0f, 40f); bar.anchoredPosition = Vector2.zero;
                var title = MakeLabel(win.transform, "Business", 10, L_BARTXT, 18f, 0f, 600f, 40f, TextAlignmentOptions.Left);
                ApplyFont(title); title.richText = false; title.fontSize = 18f; title.fontStyle = FontStyles.Bold; title.raycastTarget = false;
                var close = LImg(win.transform, "Close", CW_CLOSE, LRoundSprite(false), 3f);
                var crt = close.rectTransform;
                crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1f, 1f);
                crt.sizeDelta = new Vector2(28f, 28f); crt.anchoredPosition = new Vector2(-6f, -6f);
                CwBar(close.transform, Color.white, 0f, 0f, 15f, 2.6f, 45f);
                CwBar(close.transform, Color.white, 0f, 0f, 15f, 2.6f, -45f);
                _bizCloseRT = crt;
                var page = MakeGO("Page", win.transform);
                SetAnchored(page.GetComponent<RectTransform>(), 0f, -40f, 1920f, 875f);
                var root = BuildBizPage(page.transform);
                if (root == null) { UnityEngine.Object.Destroy(win); return; }
                _hub = win; _hubRT = wrt;
                _bizFitFor = new Vector2(-1f, -1f);
                _hub.SetActive(false);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Hub] BuildBizWindow FAILED: {ex}");
                _hub = null; _hubVisible = false;
            }
        }

        private void BizFitWindow()
        {
            try
            {
                if (_hubRT == null || _canvasGO == null) return;
                var size = _canvasGO.GetComponent<RectTransform>().rect.size;
                if (size == _bizFitFor) return;
                _bizFitFor = size;
                float s = Mathf.Min(size.x * 0.96f / 1920f, size.y * 0.94f / 915f);
                _hubRT.localScale = new Vector3(s, s, 1f);
            }
            catch (Exception ex) { BizErr("fit", ex); }
        }

        /// <summary>Left margin (2026-09-28): the native page root is a centred 1920-unit box, so on a screen narrower than the
        /// approved 1920x1057 shot (true 16:9) its left edge sits AT the screen edge. The left pane shifts so 'Players' and the
        /// cards keep the shot's distance from the VISIBLE left edge (there the root started 21 px in). Checked twice a
        /// second; recomputed only when the screen size changes. The fallback window's own edge is its visible edge.</summary>
        private void BizFitLeft()
        {
            try
            {
                if (_bizLeftPane == null || _bizRoot == null || !_bizRoot.gameObject.activeInHierarchy) return;
                var scr = new Vector2(Screen.width, Screen.height);
                if (scr == _bizLeftFor) return;
                float shift = BX0;
                if (_hubNative)
                {
                    if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_bizRoot, new Vector2(0f, Screen.height * 0.5f), _hubCam, out var lp)) return;
                    float gap = (_bizRoot.rect.xMin - lp.x) / BK;   // mock px from the visible left edge to the root's left edge
                    shift = Mathf.Clamp(BX0 - gap, 0f, 60f);
                }
                _bizLeftPane.anchoredPosition = new Vector2(shift * BK, 0f);
                _bizLeftFor = scr;
            }
            catch (Exception ex) { BizErr("fit left", ex); }
        }

        /// <summary>The page itself (native full-menu page or the fallback window's body). Returns its root.</summary>
        private RectTransform? BuildBizPage(Transform host)
        {
            try
            {
                _bizCardHits.Clear(); _bizHits.Clear(); _bizPls.Clear();
                _mergerConfirmGO = null; _rtMergerOk = null; _mergerOkBtn = null; _mergerCancelBtn = null;
                var rootGO = MakeGO("BAMP_BusinessPage", host);
                var root = rootGO.GetComponent<RectTransform>();
                LStretch(root, 0f, 0f, 0f, 0f);
                _bizRoot = root;
                _bMeasure = MakeLabel(root, "", 10, Color.clear, 0f, 0f, 10f, 10f, TextAlignmentOptions.Left);
                ApplyFont(_bMeasure); _bMeasure.richText = false; _bMeasure.raycastTarget = false;
                _bMeasure.textWrappingMode = TextWrappingModes.NoWrap;

                // ── left pane: 'Players' + scrolled cards ──
                // Its own layer, shifted by BizFitLeft so the margin from the VISIBLE left edge matches the approved shot.
                _bizLeftPane = MakeGO("LeftPane", root).GetComponent<RectTransform>();
                LStretch(_bizLeftPane, 0f, 0f, 0f, 0f);
                _bizLeftFor = new Vector2(-1f, -1f);
                BText(_bizLeftPane, "Players", 26f, Color.white, PX(35f), PY(164f), 420f, 40f, TextAlignmentOptions.Left);
                // The list viewport is padded 12 px left / 8 px right around the cards (which keep their place): shadows show.
                (_bizCardsVp, _bizCardsContent) = BScroll(_bizLeftPane, PX(21f) - 12f, PY(228f), 486f + 20f, B_CARDSH, PX(515f), PY(238f), B_CARDSH - 18f, 30f);
                var split = LImg(root, "Splitter", B_SPLIT, null, 0f);
                BPos(split.rectTransform, PX(587f), PY(128f), 2f, 855f);

                // ── header card: the selected player ──
                var hc = BPanel(root, "HeaderCard", Color.white, 3f, 14f, 0.22f, 4f);
                BPos(hc, PX(669f), PY(140f), 1221f, 232f);
                _bizHdrAv = LImg(hc, "Avatar", Color.white, LRoundSprite(false), 25f * BK);
                BPos(_bizHdrAv.rectTransform, 26f, 22f, 50f, 50f);
                _bizHdrLetter = BText(_bizHdrAv.transform, "", 20f, B_AVTXT, 0f, 0f, 50f, 50f, TextAlignmentOptions.Center, true);
                _bizHdrName = BText(hc, "", 20f, B_INK, 92f, 22f, 760f, 28f, TextAlignmentOptions.Left, true);
                _bizHdrSub = BText(hc, "", 14f, B_GREY, 92f, 51f, 760f, 20f, TextAlignmentOptions.Left);
                _bizMergeBtn = BButton(hc, "Propose merger", 3, 1221f - 26f - 150f, 31f, 32f, 150f);
                _bizMergeBtn.go.SetActive(false);
                BPos(LImg(hc, "Divider", B_DIV, null, 0f).rectTransform, 26f, 88f, 1169f, 1f);
                BText(hc, "Amount", 12f, B_GREY, 26f, 104f, 190f, 18f, TextAlignmentOptions.Left, true, true, 6f);
                BText(hc, "Interest", 12f, B_GREY, 234f, 104f, 100f, 18f, TextAlignmentOptions.Left, true, true, 6f);
                BText(hc, "Term", 12f, B_GREY, 352f, 104f, 140f, 18f, TextAlignmentOptions.Left, true, true, 6f);
                _bizAmt = BMakeField(hc, 26f, 127f, 190f, 38f);
                _bizRate = BMakeField(hc, 234f, 127f, 100f, 38f);
                _bizTerm = BMakeField(hc, 352f, 127f, 140f, 38f);
                float wl = BMeasure("Offer loan", 13f, true) + 32f, wg = BMeasure("Offer gift", 13f, true) + 32f;
                _bizLoanBtn = BButton(hc, "Offer loan", 1, 1195f - wl, 127f, 38f, wl);
                _bizGiftBtn = BButton(hc, "Offer gift", 0, 1195f - wl - 12f - wg, 127f, 38f, wg);
                _bizHelp = BText(hc, "", 13f, B_HELP, 26f, 176f, 1169f, 48f, TextAlignmentOptions.TopLeft, false, false, 0f, true);

                // ── tabs + the tab's tool area on the same line ──
                float tx = PX(669f);
                for (int i = 0; i < 5; i++)
                {
                    float w = BMeasure(B_TABS[i], 13f, true, 6f) + 40f;
                    var img = LImg(root, "Tab", B_TABOFF, LRoundSprite(false), 3f * BK);
                    BPos(img.rectTransform, tx, PY(392f), w, 46f + 4f);   // the bottom 4 px tuck under the table header
                    _bizTabImg[i] = img; _bizTabRT[i] = img.rectTransform;
                    _bizTabLbl[i] = BText(img.transform, B_TABS[i], 13f, B_TABOFFTXT, 0f, 0f, w, 46f, TextAlignmentOptions.Center, true, true, 6f);
                    tx += w + 2f;
                }
                // 'Offers to you' count: on a host with tab 0's rect, made AFTER every tab so it draws above the neighbour.
                float w0 = BMeasure(B_TABS[0], 13f, true, 6f) + 40f;
                _bizTabBadgeHost = MakeGO("TabBadgeHost", root).GetComponent<RectTransform>();
                BPos(_bizTabBadgeHost, PX(669f), PY(392f), w0, 46f + 4f);
                _bizTabBadge = BBadge(_bizTabBadgeHost, 1, w0 + 8f, -8f).gameObject;
                _bizTabBadgeLbl = _bizTabBadge.GetComponentInChildren<TextMeshProUGUI>();
                _bizTabBadge.SetActive(false);
                float toolX = PX(1320f), toolW = PX(1890f) - toolX;   // the tool's own content is right-aligned
                _bizToolW = toolW;
                _bizToolLoans = MakeGO("ToolLoans", root);
                SetAnchored(_bizToolLoans.GetComponent<RectTransform>(), toolX * BK, -PY(392f) * BK, toolW * BK, 46f * BK);
                _bizPart = BMakeField(_bizToolLoans.transform, toolW - 150f, 4f, 150f, 38f);
                BText(_bizToolLoans.transform, "Part payment", 13f, Color.white, 0f, 0f, toolW - 150f - 12f, 46f, TextAlignmentOptions.Right, true, true, 6f);
                _bizToolAccess = MakeGO("ToolAccess", root);
                SetAnchored(_bizToolAccess.GetComponent<RectTransform>(), toolX * BK, -PY(392f) * BK, toolW * BK, 46f * BK);
                BText(_bizToolAccess.transform, "Ticked: that player can use yours as if they were their own.", 14f, Color.white, 0f, 0f, toolW, 46f, TextAlignmentOptions.Right);
                _bizToolCompany = MakeGO("ToolCompany", root);
                SetAnchored(_bizToolCompany.GetComponent<RectTransform>(), toolX * BK, -PY(392f) * BK, toolW * BK, 46f * BK);
                float wlv = BMeasure("Leave merger", 13f, true) + 32f;
                _bizLeaveBtn = BButton(_bizToolCompany.transform, "Leave merger", 2, toolW - wlv, 7f, 32f, wlv);
                _bizCompanyLbl = BText(_bizToolCompany.transform, "", 14f, Color.white, 0f, 0f, toolW - wlv - 12f, 46f, TextAlignmentOptions.Right);
                _bizToolLoans.SetActive(false); _bizToolAccess.SetActive(false); _bizToolCompany.SetActive(false);

                // ── table header card + scrolled rows ──
                var th = BPanel(root, "TableHeader", Color.white, 3f, 14f, 0.16f, 3f);
                BPos(th, PX(669f), PY(438f), 1221f, 58f);
                var colsGO = MakeGO("Cols", th);
                _bizHeadCols = colsGO.GetComponent<RectTransform>();
                LStretch(_bizHeadCols, 0f, 0f, 0f, 0f);
                (_bizRowsVp, _bizRowsContent) = BScroll(root, PX(689f), PY(512f), B_ROWW, B_ROWSH, PX(1866f), PY(512f), B_ROWSH, 30f);

                _bizDirty = true; _hubSeenVersion = -1; _bizSig = 0; _bizNextSig = 0f;
                _bizFieldKey = int.MinValue; _bizHelpKey = int.MinValue;
                Plugin.Logger.LogInfo($"[Hub] Business page built OK (native={_hubNative}).");
                return root;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Hub] BuildBizPage FAILED: {ex}");
                try { if (_bizRoot != null) UnityEngine.Object.Destroy(_bizRoot.gameObject); } catch { }
                _bizRoot = null; _hubVisible = false;
                return null;
            }
        }

        // ══ merger confirmation popup ══

        // ══ MERGE-TOOLTIP-1: 'Propose merger' hover tooltip (the game's TooltipSystem) ══

        /// <summary>Every page tick: pointer on a visible 'Propose merger' (never 'Cancel merger offer', never with the merger
        /// popup open) for TooltipSystem.Delay -> the tooltip opens; anything else closes it. No allocation per frame.</summary>
        private void BizTickMergeTip(Vector2 mp)
        {
            try
            {
                // Review LOW: TooltipSystem.Show fades the box in one frame later without checking a Hide in between, so a
                // Hide within a frame of our Show (e.g. clicking the button as the delay completes -> popup) would leave an
                // empty box above the popup. For a few frames re-hide - only while the system says nothing is showing, so a
                // real game tooltip is never closed.
                if (_bizTipRehideUntil >= Time.frameCount && !_bizTipShown && !Tooltip.TooltipSystem.IsVisible) Tooltip.TooltipSystem.Hide();
                bool popup = _mergerConfirmGO != null && _mergerConfirmGO.activeSelf;
                bool eligible = !popup && _bizMergeBtn != null && _bizMergeAct == 8 && _bizMergePid != "" && _bizMergeBtn.go.activeInHierarchy;
#if BAMP_DEV
                if (eligible && _bizTipDevHold) mp = BizTipDevPoint();
#endif
                if (!eligible || !BHit(_bizMergeBtn!.rt, mp)) { BizHideMergeTip(); return; }
                if (_bizTipShown && _bizTipPid != _bizMergePid) BizHideMergeTip();   // another player selected under the pointer
                if (_bizTipSince < 0f) { _bizTipSince = Time.unscaledTime; return; }
                if (!_bizTipShown && Time.unscaledTime - _bizTipSince >= Tooltip.TooltipSystem.Delay) BizShowMergeTip();
#if BAMP_DEV
                if (_bizTipShown && _bizTipDevHold) BizTipDevPlace(mp);
#endif
            }
            catch (Exception ex) { BizErr("merge tooltip", ex); }
        }

        /// <summary>Opens it: header -> splitter -> label, the pattern of the game's BasicTooltip (approved text, verbatim).</summary>
        private void BizShowMergeTip()
        {
            _bizTipShown = true; _bizTipPid = _bizMergePid;   // set first: a throw below must not retry every frame
            _bizTipShowFrame = Time.frameCount;
            try
            {
                var p = BizSelected();
                string name = p != null && p.Pid == _bizMergePid ? p.Name : CwName(_bizMergePid);
                if (string.IsNullOrEmpty(name)) name = B_UNKNOWN;
                BizTipAboveUs();
                Tooltip.TooltipSystem.Show();
                Tooltip.TooltipSystem.AddHeader("Co-op feature".Localize());
                Tooltip.TooltipSystem.AddSplitter();
                Tooltip.TooltipSystem.AddLabel("Merge your company with " + name + "'s and run them as one: you both get full use of " +
                    "everything the other owns, including businesses, homes and vehicles. Either of you can leave the merger at any time.",
                    Color.white);
            }
            catch (Exception ex) { BizErr("merge tooltip show", ex); }
        }

        private void BizHideMergeTip()
        {
            _bizTipSince = -1f;
            if (!_bizTipShown) return;
            _bizTipShown = false; _bizTipPid = "";
            try { if (Tooltip.TooltipSystem.IsVisible) Tooltip.TooltipSystem.Hide(); }
            catch (Exception ex) { BizErr("merge tooltip hide", ex); }
            if (Time.frameCount - _bizTipShowFrame <= 1) _bizTipRehideUntil = Time.frameCount + 3;
        }

        /// <summary>KNOWN TRAP (VehicleStoragePanel.EnsureTooltipsAboveUs precedent): the game's tooltip canvas can sort BELOW
        /// the canvas hosting this page (the phone's full-menu canvas when native, our overlay canvas in the fallback
        /// window) and below the merger popup's own sorted canvas (+100). Tooltips are topmost by design: RAISE the tooltip
        /// canvas just above all of them (never lower it). Idempotent; logged once; the singleton is DontDestroyOnLoad.</summary>
        private void BizTipAboveUs()
        {
            try
            {
                var ts = global::InstanceBehavior<Tooltip.TooltipSystem>.Instance;
                var tc = ts != null ? HarmonyLib.AccessTools.Field(typeof(Tooltip.TooltipSystem), "canvas")?.GetValue(ts) as Canvas : null;
                var hc = _hubRT != null ? _hubRT.GetComponentInParent<Canvas>() : null;
                if (tc == null || hc == null) return;
                var root = hc.rootCanvas;
                int pop = BizTipPopupOrder(root);
                int want = Mathf.Min(Mathf.Max(root.sortingOrder, pop) + 1, 32767);
                int was = tc.sortingOrder;
                string wasLayer = SortingLayer.IDToName(tc.sortingLayerID);
                if (tc.sortingLayerID != root.sortingLayerID &&
                    SortingLayer.GetLayerValueFromID(tc.sortingLayerID) < SortingLayer.GetLayerValueFromID(root.sortingLayerID))
                    tc.sortingLayerID = root.sortingLayerID;
                if (tc.sortingOrder < want) tc.sortingOrder = want;
                if (!_bizTipRaiseLogged)
                {
                    _bizTipRaiseLogged = true;
                    Plugin.Logger.LogInfo($"[Hub] merger tooltip: tooltip canvas {was} ({wasLayer}) -> {tc.sortingOrder} ({SortingLayer.IDToName(tc.sortingLayerID)}, {tc.renderMode}); " +
                                          $"page canvas '{root.name}' {root.sortingOrder} ({SortingLayer.IDToName(root.sortingLayerID)}, {root.renderMode}); merger popup {pop} (tooltips are topmost by design).");
                }
            }
            catch (Exception ex) { BizErr("merge tooltip sort", ex); }
        }

        /// <summary>The merger popup's sorted canvas order: its live value when built, else what ShowMergerConfirm will give it.</summary>
        private int BizTipPopupOrder(Canvas root)
        {
            var ov = _mergerConfirmGO != null ? _mergerConfirmGO.GetComponent<Canvas>() : null;
            int planned = Mathf.Min(root.sortingOrder + 100, 32767);
            return ov != null && ov.overrideSorting ? Mathf.Max(ov.sortingOrder, planned) : planned;
        }

        /// <summary>Merger confirm popup (build-once): the game's confirm-popup look (as the lobby's Hosting port popup:
        /// light title bar, slate body, no dimming, blocks clicks behind); its approved text is unchanged.</summary>
        private void ShowMergerConfirm(string mode, string pid)
        {
            try
            {
                BizHideMergeTip();   // the popup opening closes the tooltip
                if (string.IsNullOrEmpty(pid) || _hub == null) return;
                if (_mergerConfirmGO == null)
                {
                    _mergerConfirmGO = MakeGO("BAMP_MergerConfirm", _hub.transform);
                    var brt = _mergerConfirmGO.GetComponent<RectTransform>();
                    LStretch(brt, -8000f, -8000f, -8000f, -8000f);   // a transparent blocker over the whole menu
                    var blk = _mergerConfirmGO.AddComponent<Image>(); blk.color = new Color(0f, 0f, 0f, 0f);
                    // Its own sorted canvas + raycaster: the blocker sits above the menu's app row and top bar too.
                    _mergerConfirmGO.AddComponent<Canvas>();
                    _mergerConfirmGO.AddComponent<GraphicRaycaster>();
                    var card = MakeGO("Card", _mergerConfirmGO.transform);
                    _mergerCardRT = card.GetComponent<RectTransform>();
                    _mergerCardRT.anchorMin = _mergerCardRT.anchorMax = _mergerCardRT.pivot = new Vector2(0.5f, 0.5f);
                    _mergerCardRT.anchoredPosition = Vector2.zero;
                    var sh = LImg(card.transform, "Shadow", new Color(0f, 0f, 0f, 0.45f), LShadowSprite(), 0f);
                    sh.pixelsPerUnitMultiplier = 1f;
                    LStretch(sh.rectTransform, -24f, -30f, -24f, -18f);
                    LStretch(LImg(card.transform, "Body", L_BODY, LRoundSprite(false), 4f).rectTransform, 0f, 0f, 0f, 0f);
                    var bar = LImg(card.transform, "Bar", L_BAR, LRoundSprite(false), 4f).rectTransform;
                    bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = Vector2.one; bar.pivot = new Vector2(0.5f, 1f);
                    bar.sizeDelta = new Vector2(0f, 44f * BK); bar.anchoredPosition = Vector2.zero;
                    var low = LImg(bar, "Low", L_BAR, null, 0f).rectTransform;   // squares the bar's bottom corners
                    low.anchorMin = Vector2.zero; low.anchorMax = new Vector2(1f, 0f); low.pivot = new Vector2(0.5f, 0f);
                    low.sizeDelta = new Vector2(0f, 8f); low.anchoredPosition = Vector2.zero;
                    // The lobby popups' title: dark bold UPPERCASE letter-spaced on the light bar (user-approved 2026-09-28).
                    BText(card.transform, "Company merger", 17f, L_BARTXT, 18f, 0f, 620f - 36f, 44f, TextAlignmentOptions.Left, true, true, 8f);
                    _mergerConfirmLbl = BText(card.transform, "", 15f, L_PARA, 26f, 64f, 568f, 200f, TextAlignmentOptions.TopLeft, false, false, 0f, true);
                    _mergerConfirmLbl.richText = true; _mergerConfirmLbl.overflowMode = TextOverflowModes.Overflow;
                    _mergerCancelBtn = BButton(card.transform, "Cancel", 3, 620f - 26f - 130f - 12f - 130f, 0f, 40f, 130f, 15f);
                    _mergerOkBtn = BButton(card.transform, "Confirm", 0, 620f - 26f - 130f, 0f, 40f, 130f, 15f);
                    _rtMergerOk = _mergerOkBtn.rt;
                }
                _mergerConfirmMode = mode; _mergerConfirmPid = pid;
                // MERGE-NOTICE-1 (user's exact approved wording, 2026-09-17; in-game route = the top-bar Report button):
                // shown at the top of both modes until the feature is judged stable; the user will ask for its removal.
                string warn = "<b>Experimental feature.</b> Company mergers are new and may still have bugs. If something " +
                              "looks wrong, press the purple Report button at the top right of the screen and say what you " +
                              "were doing when it happened.";
                string terms = "A merger runs your companies as <b>one</b>: every member gets full access to " +
                               "everything the others own — businesses, registers and storage, homes, and " +
                               "vehicles — including selling and spending on the company's behalf. Any member " +
                               "can leave the merger at any time.";
                string text = mode == "propose"
                    ? $"{warn}\n\n<b>Propose merging companies with {CwName(pid)}?</b>\n\n{terms}\n\nThey will be asked to accept."
                    : $"{warn}\n\n<b>Merge companies with {CwName(pid)}?</b>\n\n{terms}";
                if (_mergerConfirmLbl != null && _mergerCardRT != null)
                {
                    _mergerConfirmLbl.text = text;
                    float th = _mergerConfirmLbl.GetPreferredValues(text, 568f * BK, 0f).y / BK;
                    BPos(_mergerConfirmLbl.rectTransform, 26f, 64f, 568f, th + 4f);
                    float by = 64f + th + 24f, h = by + 40f + 22f;
                    _mergerCardRT.sizeDelta = new Vector2(620f * BK, h * BK);
                    if (_mergerCancelBtn != null) BPos(_mergerCancelBtn.rt, 620f - 26f - 130f - 12f - 130f, by, 130f, 40f);
                    if (_mergerOkBtn != null) BPos(_mergerOkBtn.rt, 620f - 26f - 130f, by, 130f, 40f);
                }
                _mergerConfirmGO.SetActive(true);
                _mergerConfirmGO.transform.SetAsLastSibling();   // above the page content
                var ov = _mergerConfirmGO.GetComponent<Canvas>();   // ...and above the whole menu (set each time the popup opens)
                var pc = _mergerConfirmGO.transform.parent != null ? _mergerConfirmGO.transform.parent.GetComponentInParent<Canvas>() : null;
                if (ov != null)
                {
                    ov.overrideSorting = true;
                    if (pc != null) { ov.sortingLayerID = pc.rootCanvas.sortingLayerID; ov.sortingOrder = Mathf.Min(pc.rootCanvas.sortingOrder + 100, 32767); }
                }
            }
            catch (Exception ex) { BizErr("ShowMergerConfirm", ex); }
        }

        // ══ kit (mock-up px in, page units out) ══

        private static float PX(float sx) => sx - BX0;
        private static float PY(float sy) => sy - BY0;
        private static void BPos(RectTransform rt, float x, float y, float w, float h) => SetAnchored(rt, x * BK, -y * BK, w * BK, h * BK);
        private static string BLetter(string name) => string.IsNullOrEmpty(name) ? "?" : char.ToUpperInvariant(name[0]).ToString();

        private static void BClear(RectTransform? content)
        {
            if (content == null) return;
            for (int i = content.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(content.GetChild(i).gameObject);
        }

        private TextMeshProUGUI BText(Transform p, string text, float px, Color c, float x, float y, float w, float h,
            TextAlignmentOptions al, bool bold = false, bool caps = false, float spacing = 0f, bool wrap = false)
        {
            var t = MakeLabel(p, "", 10, c, x * BK, -y * BK, w * BK, h * BK, al);
            ApplyFont(t);
            t.richText = false;
            t.fontSize = px * BK;
            t.fontStyle = (bold ? FontStyles.Bold : FontStyles.Normal) | (caps ? FontStyles.UpperCase : FontStyles.Normal);
            t.characterSpacing = spacing;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.text = text;
            return t;
        }

        /// <summary>Width (mock px) of a one-line text at a size (UPPERCASE bold when caps).</summary>
        private float BMeasure(string text, float px, bool caps, float spacing = 4f)
        {
            if (_bMeasure == null) return text.Length * px * 0.7f;
            _bMeasure.fontSize = px * BK;
            _bMeasure.fontStyle = FontStyles.Bold;
            _bMeasure.characterSpacing = caps ? spacing : 0f;
            return _bMeasure.GetPreferredValues(caps ? text.ToUpperInvariant() : text).x / BK;
        }

        /// <summary>A panel: soft shadow behind a rounded body (the shadow is a sibling BEFORE the body, so it draws under it).</summary>
        private static RectTransform BPanel(Transform p, string name, Color c, float radiusPx, float fallPx, float alpha, float dyPx)
        {
            var go = MakeGO(name, p);
            var rt = go.GetComponent<RectTransform>();
            var sh = LImg(go.transform, "Shadow", new Color(0f, 0f, 0f, alpha), LShadowSprite(), 0f);
            float f = fallPx * BK, dy = dyPx * BK;
            sh.pixelsPerUnitMultiplier = 24f / f;
            LStretch(sh.rectTransform, -f, -f - dy, -f, -f + dy);
            LStretch(LImg(go.transform, "Body", c, LRoundSprite(false), radiusPx * BK).rectTransform, 0f, 0f, 0f, 0f);
            return rt;
        }

        private void BAvatar(Transform p, string pid, string name, float x, float y, float size, float fontPx)
        {
            var img = LImg(p, "Avatar", (Color)CwColour(pid), LRoundSprite(false), size * BK / 2f);
            BPos(img.rectTransform, x, y, size, size);
            BText(img.transform, BLetter(name), fontPx, B_AVTXT, 0f, 0f, size, size, TextAlignmentOptions.Center, true);
        }

        /// <summary>Red count badge, centred on (cx, cy) of the parent's top-left (mock px, y down).</summary>
        private RectTransform BBadge(Transform p, int n, float cx, float cy)
        {
            string s = n.ToString();
            float w = Mathf.Max(22f, BMeasure(s, 12f, true) + 10f);
            var img = LImg(p, "Badge", B_BADGE, LRoundSprite(false), 11f * BK);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(cx * BK, -(cy + 11f) * BK);
            rt.sizeDelta = new Vector2(w * BK, 22f * BK);
            var t = BText(img.transform, s, 12f, Color.white, 0f, 0f, w, 22f, TextAlignmentOptions.Center, true);
            var trt = t.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
            return rt;
        }

        private BBtn BButton(Transform p, string label, int kind, float x, float y, float h, float w, float fontPx = 13f)
        {
            var go = MakeGO("BAMP_BizBtn", p);
            var rt = go.GetComponent<RectTransform>();
            BPos(rt, x, y, w, h);
            var img = go.AddComponent<Image>(); img.raycastTarget = false;
            var t = BText(go.transform, label, fontPx, Color.white, 0f, 0f, w, h, TextAlignmentOptions.Center, true, true, 4f);
            var trt = t.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
            var b = new BBtn { go = go, rt = rt, img = img, lbl = t };
            BSetKind(b, kind);
            return b;
        }

        private static void BSetKind(BBtn? b, int kind)
        {
            if (b == null || b.kind == kind) return;
            b.kind = kind;
            try
            {
                b.img.sprite = kind >= 4 ? LRoundSprite(false) : BGradSprite(kind);
                b.img.type = Image.Type.Sliced;
                b.img.pixelsPerUnitMultiplier = 10f / (3f * BK);
                b.img.color = kind == 5 ? B_DISW : kind == 4 ? B_DIS : Color.white;
                b.lbl.color = kind == 5 ? B_DISWTXT : kind == 4 ? B_DISTXT : Color.white;
            }
            catch { }
        }

        /// <summary>40 px rounded rect (radius 10, 9-slice border 10) with a left-to-right gradient baked in; the stretched
        /// centre keeps the gradient running across any button width.</summary>
        private static Sprite BGradSprite(int kind)
        {
            var cached = _bGradSp[kind];
            if (cached != null && IsAlive(cached)) return cached;
            const int S = 40; const float R = 10f, H = S / 2f;
            Color c0 = LC(B_GRAD[kind, 0]), c1 = LC(B_GRAD[kind, 1]);
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - H) - (H - R), qy = Mathf.Abs(y + 0.5f - H) - (H - R);
                    float sd = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - R;
                    float a = Mathf.Clamp01(0.5f - sd);
                    var c = Color.Lerp(c0, c1, (x + 0.5f) / S);
                    px[y * S + x] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)(a * 255f));
                }
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixels32(px); tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(R, R, R, R));
            sp.hideFlags = HideFlags.HideAndDontSave;
            _bGradSp[kind] = sp;
            return sp;
        }

        private BField BMakeField(Transform p, float x, float y, float w, float h)
        {
            var go = MakeGO("BAMP_BizField", p);
            var rt = go.GetComponent<RectTransform>();
            BPos(rt, x, y, w, h);
            var line = go.AddComponent<Image>();
            line.sprite = LRoundSprite(false); line.type = Image.Type.Sliced; line.pixelsPerUnitMultiplier = 10f / (3f * BK);
            line.color = B_FIELDLINE; line.raycastTarget = false;
            var fill = LImg(go.transform, "Fill", B_FIELD, LRoundSprite(false), 2.5f * BK);
            LStretch(fill.rectTransform, BK, BK, BK, BK);   // the 1 px border
            var t = BText(go.transform, "", 16f, B_INK, 12f, 0f, w - 24f, h, TextAlignmentOptions.Left, true);
            return new BField { rt = rt, line = line, lbl = t };
        }

        /// <summary>A vertical scroll list: masked viewport (x, y, w, h mock px) + a thin scrollbar at (bx, by, 5 x bh)
        /// that shows only when the list is longer than the view.</summary>
        private (RectTransform vp, RectTransform content) BScroll(Transform parent, float x, float y, float w, float h, float bx, float by, float bh, float sens)
        {
            var vpGO = MakeGO("BAMP_Vp", parent);
            var vpRT = vpGO.GetComponent<RectTransform>();
            BPos(vpRT, x, y, w, h);
            vpGO.AddComponent<RectMask2D>();
            vpGO.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.004f);   // raycast target for the wheel

            var contentGO = MakeGO("Content", vpGO.transform);
            var cRT = contentGO.GetComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0f, 1f); cRT.anchorMax = new Vector2(1f, 1f); cRT.pivot = new Vector2(0.5f, 1f);
            cRT.anchoredPosition = Vector2.zero; cRT.sizeDelta = new Vector2(0f, h * BK);

            var barGO = MakeGO("Bar", parent);
            var barRT = barGO.GetComponent<RectTransform>();
            BPos(barRT, bx, by, 5f, bh);
            var track = barGO.AddComponent<Image>();
            track.color = B_TRACK; track.sprite = LRoundSprite(false); track.type = Image.Type.Sliced; track.pixelsPerUnitMultiplier = 10f / (2.5f * BK);
            var handleGO = MakeGO("Handle", barGO.transform);
            var handleRT = handleGO.GetComponent<RectTransform>();
            handleRT.anchorMin = Vector2.zero; handleRT.anchorMax = Vector2.one; handleRT.offsetMin = handleRT.offsetMax = Vector2.zero;
            var handle = handleGO.AddComponent<Image>();
            handle.color = B_THUMB; handle.sprite = LRoundSprite(false); handle.type = Image.Type.Sliced; handle.pixelsPerUnitMultiplier = 10f / (2.5f * BK);
            var bar = barGO.AddComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.handleRect = handleRT;
            bar.targetGraphic = handle;

            var sr = vpGO.AddComponent<ScrollRect>();
            sr.content = cRT; sr.viewport = vpRT;
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = sens;
            sr.verticalScrollbar = bar;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            return (vpRT, cRT);
        }

#if BAMP_DEV
        // DEV screenshot levers (2026-09-28, Business redesign): TestDrive `hubview`. Opening and closing go through the
        // phone icons' own request flags; these read state, pick the tab the way a tab click does, and open / close the
        // merger popup WITHOUT sending anything (its Confirm is never pressed by a lever).
        internal bool DevHubVisible => _hubVisible;

        internal void DevSetHubTab(int tab)
        {
            try { _hubTab = Mathf.Clamp(tab, 0, 4); _bizDirty = true; }   // = the tab click in BizClick
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TestDrive] DevSetHubTab: {ex.Message}"); }
        }

        internal string DevHubState()
        {
            try
            {
                return $"ready={MPHubNativePage.Ready} visible={_hubVisible} native={_hubNative} pageActive={MPHubNativePage.PageActive} " +
                       $"tab={_hubTab} in={MPHub.IncomingOffers.Count} out={MPHub.OutgoingOffers.Count} loans={MPHub.Loans.Count} " +
                       $"cards={_bizPls.Count} rows={_bizRowCount} sel='{_bizSel}' incomingMerger='{MergerSync.IncomingFromPid}' " +
                       $"popup={_mergerConfirmGO != null && _mergerConfirmGO.activeSelf} menuOpen={UI.Smartphone.FullMenu.IsOpen} " +
                       $"focus={_bizFocus} amount={_hubAmount:F0} arm='{_hubRepayArm}' unknownCards={DevUnknownCards()} " +
                       $"leftShift={(_bizLeftPane != null ? _bizLeftPane.anchoredPosition.x / BK : -1f):F1}";
            }
            catch (Exception ex) { return "stateErr=" + ex.Message; }
        }

        private int DevUnknownCards()
        {
            int n = 0;
            foreach (var p in _bizPls) if (p.Name == B_UNKNOWN) n++;
            return n;
        }

        /// <summary>DEV `hubview escbox|escmenu`: one Esc press as the real key runs it - our HandleHubTyping half (BizEscLeave)
        /// then the game's own CancelButtonHandler.HandleEscapeClick (GameManager.GlobalKeyEvents' Cancel branch) - in one
        /// frame. escbox first focuses the Amount box and leaves the draft '25000' in it as keystrokes would. Sends nothing.</summary>
        internal string DevBizEsc(bool box)
        {
            try
            {
                if (_hub == null || !_hubVisible) return "ERR page not open";
                if (box)
                {
                    BizFocus(1); _hubAmountStr = "25000"; _hubFreshFocus = false;
                    BizSyncSelection();
                }
                var es = EventSystem.current;
                bool selBefore = es != null && _bizSelGO != null && es.currentSelectedGameObject == _bizSelGO;
                if (_bizFocus != 0) BizEscLeave();
                global::CancelButtonHandler.HandleEscapeClick();
                return $"selBefore={selBefore} focus={_bizFocus} amount={_hubAmount:F0} menuOpen={UI.Smartphone.FullMenu.IsOpen}";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        /// <summary>DEV `hubview paydouble`: the 'Pay all' click (act 5) on the first loan I owe to an online lender, then
        /// the double-click's second half in the same instant through the Confirm's own guard. NEVER requests a repayment:
        /// it reports what the guard says.</summary>
        internal string DevBizPayDouble()
        {
            try
            {
                string me = MPConfig.PlayerId ?? "";
                foreach (var ln in MPHub.Loans)
                {
                    if (ln.Borrower != me || !_bizOnline.Contains(ln.Lender)) continue;
                    _hubRepayArm = ln.Id; _hubRepayArmAmt = 0f; _hubRepayArmAt = Time.unscaledTime; _bizDirty = true;
                    bool tooSoon = BizRepayTooSoon(ln.Id);
                    return $"armed='{ln.Id}' tooSoon={tooSoon} paid=False";
                }
                return "ERR no loan I owe to an online lender";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        internal string DevBizMergerPopup(bool open)
        {
            try
            {
                if (!open)
                {
                    if (_mergerConfirmGO != null) _mergerConfirmGO.SetActive(false);
                    return "open=False";
                }
                if (_hub == null || !_hubVisible) return "ERR page not open";
                string pid = BizTargetPid();
                if (pid == "") foreach (var p in _bizPls) if (p.Online && p.Pid != "") { pid = p.Pid; break; }
                if (pid == "") return "ERR no online player";
                ShowMergerConfirm("propose", pid);
                return $"open={_mergerConfirmGO != null && _mergerConfirmGO.activeSelf} target='{pid}'";
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        // ── MERGE-TOOLTIP-1 DEV lever (hubview mergetip|mergetipstate|mergetipoff): the page's own hover path with a virtual
        // pointer resting on the button's centre (the rig's real pointer is wherever the desktop left it). Sends nothing.
        private static System.Reflection.FieldInfo? _tsFCanvas, _tsFCanvasRT, _tsFTipRT, _tsFDataRT;

        private Vector2 BizTipDevPoint()
        {
            var rt = _bizMergeBtn!.rt;
            return RectTransformUtility.WorldToScreenPoint(_hubCam, rt.TransformPoint(rt.rect.center));
        }

        private void BizTipDevFields(out Canvas? canvas, out RectTransform? crt, out RectTransform? tip, out RectTransform? data)
        {
            canvas = null; crt = null; tip = null; data = null;
            var ts = global::InstanceBehavior<Tooltip.TooltipSystem>.Instance;
            if (ts == null) return;
            var ty = typeof(Tooltip.TooltipSystem);
            _tsFCanvas ??= HarmonyLib.AccessTools.Field(ty, "canvas");
            _tsFCanvasRT ??= HarmonyLib.AccessTools.Field(ty, "canvasRectTransform");
            _tsFTipRT ??= HarmonyLib.AccessTools.Field(ty, "tooltipRect");
            _tsFDataRT ??= HarmonyLib.AccessTools.Field(ty, "dataRectTransform");
            canvas = _tsFCanvas?.GetValue(ts) as Canvas;
            crt = _tsFCanvasRT?.GetValue(ts) as RectTransform;
            tip = _tsFTipRT?.GetValue(ts) as RectTransform;
            data = _tsFDataRT?.GetValue(ts) as RectTransform;
        }

        /// <summary>= TooltipSystem.SetPosition (decompile Tooltip/TooltipSystem.cs) fed the virtual pointer.</summary>
        private void BizTipDevPlace(Vector2 sp)
        {
            BizTipDevFields(out var canvas, out var crt, out var tip, out var data);
            if (canvas == null || crt == null || tip == null || data == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(crt, sp, canvas.worldCamera, out var lp);
            Vector2 a = lp; Rect r = data.rect, r2 = crt.rect;
            if (a.x + r2.width / 2f < r.width / 2f) a.x = -r2.width / 2f + r.width / 2f;
            else if (a.x + r2.width / 2f + r.width / 2f > r2.width) a.x = r2.width / 2f - r.width / 2f;
            if (a.y + r2.height / 2f + r.height > r2.height) a.y = r2.height / 2f - r.height;
            if (lp.y > r2.height / 2f - r.height + 40f) a.y = lp.y - r.height - 40f;
            tip.anchoredPosition = a;
        }

        internal string DevBizMergeTip(string what)
        {
            try
            {
                if (what == "off") { _bizTipDevHold = false; BizHideMergeTip(); return DevBizMergeTipState(); }
                if (what == "on")
                {
                    if (_hub == null || !_hubVisible) return "ERR page not open";
                    if (_bizMergeAct != 8)   // pick a player the button would offer 'Propose merger' for (= a card click)
                        foreach (var p in _bizPls)
                            if (p.Online && p.Pid != "" && !(MergerSync.IAmMember && MergerSync.IsMemberPid(p.Pid)) &&
                                MergerSync.OutgoingToPid != p.Pid && !MergerSync.InAnyGroup(p.Pid))
                            { _bizSel = p.Key; _bizDirty = true; break; }
                    _bizTipDevHold = true;
                    return $"hold=True sel='{_bizSel}' act={_bizMergeAct}";
                }
                return DevBizMergeTipState();
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }

        private string DevBizMergeTipState()
        {
            try
            {
                BizTipDevFields(out var canvas, out _, out _, out var data);
                var hc = _hubRT != null ? _hubRT.GetComponentInParent<Canvas>() : null;
                var root = hc != null ? hc.rootCanvas : null;
                int tipO = canvas != null ? canvas.sortingOrder : int.MinValue, pageO = root != null ? root.sortingOrder : int.MinValue;
                int popO = root != null ? BizTipPopupOrder(root) : int.MinValue;
                bool inScreen = false; string box = "?";
                if (canvas != null && data != null)
                {
                    var wc = new Vector3[4]; data.GetWorldCorners(wc);
                    var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    Vector2 lo = RectTransformUtility.WorldToScreenPoint(cam, wc[0]), hi = RectTransformUtility.WorldToScreenPoint(cam, wc[2]);
                    inScreen = lo.x >= 0f && lo.y >= 0f && hi.x <= Screen.width && hi.y <= Screen.height && hi.x > lo.x && hi.y > lo.y;
                    box = $"{lo.x:F0},{lo.y:F0}-{hi.x:F0},{hi.y:F0}/{Screen.width}x{Screen.height}";
                }
                return $"visible={Tooltip.TooltipSystem.IsVisible} shown={_bizTipShown} hold={_bizTipDevHold} btn={(_bizMergeBtn != null && _bizMergeBtn.go.activeInHierarchy)} " +
                       $"label='{(_bizMergeBtn != null ? _bizMergeBtn.lbl.text : "")}' target='{_bizTipPid}' " +
                       $"tipCanvas={tipO}/{(canvas != null ? canvas.renderMode.ToString() : "?")} pageCanvas={pageO}/{(root != null ? root.renderMode.ToString() : "?")} popupCanvas={popO} " +
                       $"above={(tipO > pageO && tipO > popO)} inScreen={inScreen} box={box}";
            }
            catch (Exception ex) { return "stateErr=" + ex.Message; }
        }
#endif
    }
}
