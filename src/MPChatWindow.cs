using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace BigAmbitionsMP
{
    /// <summary>
    /// The floating CHAT window (RESTYLE, user-approved 2026-09-28: mock-ups C1-C4 + wording; "for chat i want a
    /// floating window due to its unique nature"; "make sure visually it matches more closely with the other screens
    /// from the same phone"). Spec: scratchpad hubart/gen_hub.py chat(); every number below is a 1080p canvas unit
    /// (= a mock-up pixel). Styled like the game's own HUD cards: light title bar, dark bold UPPERCASE title, red
    /// square close button, slate body, 3 px corners, soft shadow (the rounded/shadow sprites are the mod's own kit,
    /// so they never die with a game asset).
    ///
    /// The message list is a real list of WRAPPING rows: each row is measured (TMP preferred height at the list
    /// width) and stacked by hand under a RectMask2D viewport; scrolling is by rendered HEIGHT (px from the bottom),
    /// never by message count, so the newest row is always fully visible at the bottom. Rows are added
    /// incrementally when MPChat.Version moves (trimmed rows are dropped from the top) - never rebuilt per frame.
    /// Clicks stay on the hand-rolled RectHit dispatch (every clickable part is hit-tested here, and only while
    /// active in the hierarchy - the known trap).
    /// </summary>
    public partial class MPCanvasUI
    {
        // ── look (approved mock-up) ──
        // Title bar + body match the lobby cards (manager 2026-09-28): #C6CFD6 bar, #3C454F body FULLY solid at the default
        // opacity (a 94% body let the text behind show through - Confirmed in shot-chat-drop.png); the slider still fades both.
        private static readonly Color CW_BAR = LC(0xC6CFD6), CW_TITLE = LC(0x1B2328), CW_CLOSE = LC(0xE5484D);
        private static readonly Color CW_BODY = LC(0x3C454F);
        private static readonly Color CW_LBL = LC(0xCDD4DA), CW_BOX = LC(0x2B3238), CW_BLUE = LC(0x3A5C8F), CW_TIME = LC(0xAEB7BF);
        private static readonly Color CW_BOXLINE = new Color(1f, 1f, 1f, 0.14f), CW_INLINE = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color CW_SLIDER = LC(0x9AA4AD), CW_DDTXT = LC(0x1E2226), CW_DDSEL = LC(0xDBE2E8);
        private static readonly Color CW_GRIP = new Color(1f, 1f, 1f, 0.35f);
        private const string CW_PRIV_LBL = "#C89BFF", CW_PRIV_TXT = "#E9DCFF";
        private const float CW_TITLE_H = 32f, CW_TO_TOP = 40f, CW_TO_H = 28f, CW_TO_X = 38f;
        private const float CW_LIST_TOP = 78f, CW_LIST_BOT = 52f, CW_LIST_L = 12f, CW_LIST_R = 16f;
        private const float CW_IN_H = 30f, CW_SEND_W = 62f, CW_ROW_GAP = 3f, CW_DD_ROW = 30f, CW_WHEEL = 40f;
        private const int CW_DD_MAXROWS = 6;
        // Size limits (review R5: the list always holds ~4 lines: 220 - 78 - 52 = 90 px).
        private const float CW_MIN_W = 240f, CW_MIN_H = 220f, CW_MAX_W = 1100f, CW_MAX_H = 1000f;
        // Default place (user U1 2026-09-28): BOTTOM RIGHT, just LEFT of the BizPhone - bottom level with the phone's bottom,
        // right edge CW_PHONE_GAP left of the phone's left edge (the old top-right spot sat on the game's own pop-up
        // notification cards). The window is anchored bottom-right, so a position is (right-edge x <= 0, bottom y >= 0) in
        // canvas units. If that would reach the bottom-centre street name, the window is lifted above it.
        private const float CW_PHONE_GAP = 8f, CW_STREET_HALF = 170f, CW_STREET_TOP = 96f;
        private static readonly Vector2 CW_DEF_FALLBACK = new Vector2(-325f, 8f);   // 1080p measure (phone unreadable)

        private sealed class CwRow
        {
            public ChatLine? Line;      // message row: its line (null = a time row)
            public ChatLine? TimeOf;    // time row: the message it heads
            public RectTransform RT = null!;
            public TextMeshProUGUI T = null!;
            public float H = -1f;       // measured height (-1 = not yet)
        }

        private RectTransform?    _mpCloseRT;
        private string            _chatTarget = "";   // "" = everyone
        private readonly List<CwRow> _cwRows = new();
        private readonly List<ChatLine> _cwSnap = new();
        private readonly List<string> _cwRoster = new();                       // the OTHER online players (ids)
        private readonly Dictionary<string, string> _cwNames = new();          // id -> last resolved name (never show a raw id once known)
        private readonly System.Text.StringBuilder _cwSigSb = new();
        private readonly List<(RectTransform rt, string who)> _cwDropItems = new();
        private RectTransform? _cwViewRT, _cwContentRT, _cwListHitRT, _cwTrackRT, _cwThumbRT, _cwPillRT, _cwToBoxRT;
        private RectTransform? _cwDropRootRT, _cwDropPanelRT, _cwDropContentRT;
        private GameObject?    _cwEmptyGO, _cwTrackGO, _cwPillGO, _cwDropGO;
        private TextMeshProUGUI? _cwToTxt, _cwPlaceholder, _cwPillTxt;
        private int    _cwVersion = -1;
        private float  _cwScroll, _cwContentH, _cwLaidW = -1f;
        private float  _cwAppScroll = -1f, _cwAppMax = -1f, _cwAppViewH = -1f, _cwAppTrackH = -1f;
        private bool   _cwUnseen, _cwThumbDrag, _cwDropOpen;
        private float  _cwThumbStartY, _cwThumbStartScroll, _cwDropScroll, _cwDropContentH;
        private float  _cwNextRoster, _cwNextClock;
        private string _cwRosterSig = "\0", _cwTargetShown = "\0";
        private bool   _cwTickErrLogged, _cwRefreshErrLogged;
        private TextMeshProUGUI? _cwTitleTxt;                                   // fades with the title bar (SetMpOpacity)
        private float  _cwNextRelayout;                                         // R4: re-measure at most every 100 ms while resizing
        private Vector2 _cwResizeStartPos;
        private int    _cwPlaceScrW = -1, _cwPlaceScrH = -1;
        private float  _cwPlaceScale = -1f;
        private Vector2 _cwPhonePos;
        private bool   _cwPhoneKnown, _cwPhoneLogged, _cwPhoneErrLogged;
        private readonly Vector3[] _cwCorners = new Vector3[4];
        private readonly Dictionary<int, string> _cwNameHex = new();           // player colour -> readable name colour
        private UnityEngine.Object? _mpStyledFont;   // font the window was last styled with - restyle if a new capture lands

        // ── small builders ──

        private TextMeshProUGUI CwText(Transform p, string name, string text, float size, Color c, TextAlignmentOptions al,
                                       FontStyles st = FontStyles.Normal, float spacing = 0f)
        {
            var go = MakeGO(name, p);
            var t = go.AddComponent<TextMeshProUGUI>();
            ApplyFont(t);
            t.richText = false; t.text = text; t.fontSize = size; t.color = c; t.alignment = al;
            t.fontStyle = st; t.characterSpacing = spacing;
            t.textWrappingMode = TextWrappingModes.NoWrap; t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            return t;
        }

        private static Image CwBar(Transform p, Color c, float cx, float cy, float w, float h, float rot)
        {
            var go = MakeGO("Stroke", p);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(cx, cy); rt.sizeDelta = new Vector2(w, h);
            rt.localRotation = Quaternion.Euler(0f, 0f, rot);
            var img = go.AddComponent<Image>(); img.color = c; img.raycastTarget = false;
            return img;
        }

        private static void CwTopStretch(RectTransform rt, float l, float r, float top, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(l, -(top + h)); rt.offsetMax = new Vector2(-r, -top);
        }

        private static bool CwHit(RectTransform? rt, Vector2 mp)
            => rt != null && rt.gameObject.activeInHierarchy && RectHit(rt, mp);

        /// <summary>A player's colour (PlayerColours - the per-player colour every other surface uses); mine for myself.</summary>
        private static Color32 CwColour(string pid)
        {
            try
            {
                if (pid == MPConfig.PlayerId)
                {
                    int s = PlayerColours.SlotOf(pid);
                    if (s > 0) return PlayerColours.ColourForSlot(s);
                    PlayerColours.TryColourFor("", out var c0);   // the shared fallback colour
                    return c0;
                }
                PlayerColours.TryColourFor(pid, out var c);
                return c;
            }
            catch { return new Color32(0x00, 0x7F, 0x99, 0xFF); }
        }

        /// <summary>Display name (MPNames, the same name everywhere); a name once resolved sticks, so a player who
        /// left never turns back into a raw id.</summary>
        private string CwName(string pid)
        {
            try
            {
                if (string.IsNullOrEmpty(pid)) return "";
                string n = MPNames.Resolve(pid);
                if (!string.IsNullOrEmpty(n) && n != pid) { _cwNames[pid] = n; return n; }
                return _cwNames.TryGetValue(pid, out var c) ? c : n;
            }
            catch { return pid; }
        }

        // ── build ──

        private void BuildMpWindow(Transform canvasRoot)
        {
            _mpFade.Clear(); _cwRows.Clear(); _cwDropItems.Clear();
            _cwVersion = -1; _cwLaidW = -1f; _cwScroll = 0f; _cwUnseen = false; _cwDropOpen = false;
            _cwRosterSig = "\0"; _cwTargetShown = "\0"; _cwAppScroll = -1f;
            _mpWin   = MakeGO("BAMP_MpWindow", canvasRoot);
            _mpWinRT = _mpWin.GetComponent<RectTransform>();
            _mpWinRT.anchorMin = _mpWinRT.anchorMax = _mpWinRT.pivot = new Vector2(1f, 0f);   // bottom-right (U1)
            _mpWinRT.sizeDelta        = new Vector2(MPW_W, MPW_H);
            _mpWinRT.anchoredPosition = CW_DEF_FALLBACK;                                      // placed on open (CwPlaceWindow)
            _mpWin.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);   // transparent raycast target: world clicks stop here
            var win = _mpWin.transform;

            var sh = LImg(win, "Shadow", new Color(0f, 0f, 0f, 0.35f), LShadowSprite(), 0f);
            sh.pixelsPerUnitMultiplier = 2f;                                   // 12 px falloff
            LStretch(sh.rectTransform, -12f, -16f, -12f, -8f);
            AddFade(sh);
            var body = LImg(win, "Body", CW_BODY, LRoundSprite(false), 3f);
            LStretch(body.rectTransform, 0f, 0f, 0f, 0f);
            AddFade(body);

            // Title bar (drag handle): light bar, dark bold letter-spaced 'CHAT', opacity slider, red close.
            var bar = LImg(win, "Title", CW_BAR, LRoundSprite(false), 3f);
            _mpTitleRT = bar.rectTransform;
            CwTopStretch(_mpTitleRT, 0f, 0f, 0f, CW_TITLE_H);
            AddFade(bar);
            var low = LImg(bar.transform, "Low", CW_BAR, null, 0f).rectTransform;   // squares the bar's bottom corners
            low.anchorMin = Vector2.zero; low.anchorMax = new Vector2(1f, 0f); low.pivot = new Vector2(0.5f, 0f);
            low.sizeDelta = new Vector2(0f, 4f); low.anchoredPosition = Vector2.zero;
            AddFade(low.GetComponent<Image>());
            var title = CwText(bar.transform, "TitleText", "CHAT", 13f, CW_TITLE, TextAlignmentOptions.Left, FontStyles.Bold, 10f);
            LStretch(title.rectTransform, 12f, 0f, 110f, 0f);
            _cwTitleTxt = title;

            var close = LImg(bar.transform, "Close", CW_CLOSE, LRoundSprite(false), 3f);
            _mpCloseRT = close.rectTransform;
            _mpCloseRT.anchorMin = _mpCloseRT.anchorMax = _mpCloseRT.pivot = new Vector2(1f, 0.5f);
            _mpCloseRT.anchoredPosition = new Vector2(-6f, 0f); _mpCloseRT.sizeDelta = new Vector2(22f, 22f);
            CwBar(close.transform, Color.white, 0f, 0f, 12f, 2f, 45f);
            CwBar(close.transform, Color.white, 0f, 0f, 12f, 2f, -45f);

            // Opacity slider (today's function: fades the chrome, floor 0.15). The hit rect is 14 tall around a 4 px track.
            var trackGO = MakeGO("OpacityTrack", bar.transform);
            _mpOpacityTrackRT = trackGO.GetComponent<RectTransform>();
            _mpOpacityTrackRT.anchorMin = _mpOpacityTrackRT.anchorMax = _mpOpacityTrackRT.pivot = new Vector2(1f, 0.5f);
            _mpOpacityTrackRT.anchoredPosition = new Vector2(-38f, 0f); _mpOpacityTrackRT.sizeDelta = new Vector2(56f, 14f);
            var line = LImg(trackGO.transform, "Line", CW_SLIDER, LRoundSprite(false), 2f).rectTransform;
            line.anchorMin = new Vector2(0f, 0.5f); line.anchorMax = new Vector2(1f, 0.5f); line.pivot = new Vector2(0.5f, 0.5f);
            line.sizeDelta = new Vector2(0f, 4f); line.anchoredPosition = Vector2.zero;
            var knob = LImg(trackGO.transform, "Knob", CW_BLUE, LRoundSprite(false), 6f);
            _mpOpacityKnobRT = knob.rectTransform;
            _mpOpacityKnobRT.anchorMin = _mpOpacityKnobRT.anchorMax = new Vector2(0f, 0.5f); _mpOpacityKnobRT.pivot = new Vector2(0f, 0.5f);
            _mpOpacityKnobRT.sizeDelta = new Vector2(12f, 12f); _mpOpacityKnobRT.anchoredPosition = new Vector2(56f * _mpOpacity - 6f, 0f);

            // 'TO' row: label + dropdown box (white text + chevron).
            var toLbl = CwText(win, "ToLbl", "TO", 12f, CW_LBL, TextAlignmentOptions.Left, FontStyles.Bold, 6f);
            SetAnchored(toLbl.rectTransform, 10f, -CW_TO_TOP, 26f, CW_TO_H);
            var box = LImg(win, "ToBox", CW_BOX, LRoundSprite(false), 3f);
            _cwToBoxRT = box.rectTransform;
            CwTopStretch(_cwToBoxRT, CW_TO_X, 10f, CW_TO_TOP, CW_TO_H);
            LStretch(LImg(box.transform, "Line", CW_BOXLINE, LRoundSprite(true), 3f).rectTransform, 0f, 0f, 0f, 0f);
            _cwToTxt = CwText(box.transform, "Name", "Everyone", 14f, Color.white, TextAlignmentOptions.Left);
            LStretch(_cwToTxt.rectTransform, 10f, 0f, 28f, 0f);
            var chev = MakeGO("Chevron", box.transform).GetComponent<RectTransform>();
            chev.anchorMin = chev.anchorMax = chev.pivot = new Vector2(1f, 0.5f);
            chev.anchoredPosition = new Vector2(-10f, 0f); chev.sizeDelta = new Vector2(12f, 12f);
            CwBar(chev, CW_LBL, -2f, 0f, 6.2f, 2f, -45f);
            CwBar(chev, CW_LBL, 2f, 0f, 6.2f, 2f, 45f);

            // Message list: wheel/hit area, masked viewport, bottom-anchored content of hand-stacked rows.
            _cwListHitRT = MakeGO("ListHit", win).GetComponent<RectTransform>();
            LStretch(_cwListHitRT, 0f, CW_LIST_BOT - 6f, 0f, CW_LIST_TOP - 4f);
            var view = MakeGO("View", win);
            _cwViewRT = view.GetComponent<RectTransform>();
            LStretch(_cwViewRT, CW_LIST_L, CW_LIST_BOT, CW_LIST_R, CW_LIST_TOP);
            view.AddComponent<RectMask2D>();
            _cwContentRT = MakeGO("Content", view.transform).GetComponent<RectTransform>();
            _cwContentRT.anchorMin = Vector2.zero; _cwContentRT.anchorMax = new Vector2(1f, 0f); _cwContentRT.pivot = new Vector2(0.5f, 0f);
            _cwContentRT.sizeDelta = Vector2.zero; _cwContentRT.anchoredPosition = Vector2.zero;
            var empty = CwText(view.transform, "Empty", "No messages yet. Type below and press Enter.", 13f, CW_LBL,
                               TextAlignmentOptions.BottomLeft, FontStyles.Italic);
            empty.textWrappingMode = TextWrappingModes.Normal;
            LStretch(empty.rectTransform, 0f, 4f, 0f, 0f);
            _cwEmptyGO = empty.gameObject;

            // Scroll bar: 10-wide hit rect, 4 px track (white 12%) + #A7A7A8 thumb at its right.
            _cwTrackGO = MakeGO("ScrollBar", win);
            _cwTrackRT = _cwTrackGO.GetComponent<RectTransform>();
            _cwTrackRT.anchorMin = new Vector2(1f, 0f); _cwTrackRT.anchorMax = Vector2.one; _cwTrackRT.pivot = new Vector2(1f, 0.5f);
            _cwTrackRT.offsetMin = new Vector2(-CW_LIST_R, CW_LIST_BOT); _cwTrackRT.offsetMax = new Vector2(-6f, -CW_LIST_TOP);
            var trk = LImg(_cwTrackGO.transform, "Track", L_TRACK, LRoundSprite(false), 2f).rectTransform;
            trk.anchorMin = new Vector2(1f, 0f); trk.anchorMax = Vector2.one; trk.pivot = new Vector2(1f, 0.5f);
            trk.offsetMin = new Vector2(-4f, 0f); trk.offsetMax = Vector2.zero;
            _cwThumbRT = LImg(trk, "Thumb", L_THUMB, LRoundSprite(false), 2f).rectTransform;
            _cwThumbRT.anchorMin = new Vector2(0f, 1f); _cwThumbRT.anchorMax = Vector2.one; _cwThumbRT.pivot = new Vector2(0.5f, 1f);
            _cwThumbRT.sizeDelta = new Vector2(0f, 20f); _cwThumbRT.anchoredPosition = Vector2.zero;
            _cwTrackGO.SetActive(false);

            // 'New messages' pill (click = jump to the bottom), centred over the list's bottom edge.
            var pillArea = MakeGO("PillArea", win).GetComponent<RectTransform>();
            LStretch(pillArea, CW_LIST_L, CW_LIST_BOT, CW_LIST_R, CW_LIST_TOP);
            var pill = LImg(pillArea, "Pill", CW_BLUE, LRoundSprite(false), 12f);
            _cwPillRT = pill.rectTransform;
            _cwPillRT.anchorMin = _cwPillRT.anchorMax = new Vector2(0.5f, 0f); _cwPillRT.pivot = new Vector2(0.5f, 0f);
            _cwPillRT.anchoredPosition = new Vector2(0f, 8f); _cwPillRT.sizeDelta = new Vector2(126f, 24f);
            var arrow = MakeGO("Arrow", pill.transform).GetComponent<RectTransform>();
            arrow.anchorMin = arrow.anchorMax = new Vector2(0f, 0.5f); arrow.pivot = new Vector2(0f, 0.5f);
            arrow.anchoredPosition = new Vector2(12f, 0f); arrow.sizeDelta = new Vector2(10f, 10f);
            CwBar(arrow, Color.white, 0f, 0.25f, 1.8f, 8.5f, 0f);
            CwBar(arrow, Color.white, -1.75f, -2.25f, 5.4f, 1.8f, -45f);
            CwBar(arrow, Color.white, 1.75f, -2.25f, 5.4f, 1.8f, 45f);
            _cwPillTxt = CwText(pill.transform, "Text", "New messages", 12f, Color.white, TextAlignmentOptions.Left, FontStyles.Bold);
            LStretch(_cwPillTxt.rectTransform, 28f, 0f, 8f, 0f);
            _cwPillGO = pill.gameObject;
            _cwPillGO.SetActive(false);

            // Input row: box (#2B3238, white-16% border) + 'SEND'.
            var inGO = MakeGO("ChatInput", win);
            _mpChatInputRT = inGO.GetComponent<RectTransform>();
            _mpChatInputRT.anchorMin = Vector2.zero; _mpChatInputRT.anchorMax = new Vector2(1f, 0f); _mpChatInputRT.pivot = Vector2.zero;
            _mpChatInputRT.offsetMin = new Vector2(10f, 10f);
            _mpChatInputRT.offsetMax = new Vector2(-(10f + CW_SEND_W + 6f), 10f + CW_IN_H);
            var inImg = inGO.AddComponent<Image>();
            inImg.color = CW_BOX; inImg.sprite = LRoundSprite(false); inImg.type = Image.Type.Sliced; inImg.pixelsPerUnitMultiplier = 10f / 3f;
            LStretch(LImg(inGO.transform, "Line", CW_INLINE, LRoundSprite(true), 3f).rectTransform, 0f, 0f, 0f, 0f);
            var area = MakeGO("TextArea", inGO.transform);
            var art = area.GetComponent<RectTransform>();
            LStretch(art, 10f, 0f, 10f, 0f);
            area.AddComponent<RectMask2D>();
            _cwPlaceholder = CwText(area.transform, "Placeholder", "Message everyone", 13f, L_DISTXT, TextAlignmentOptions.Left);
            LStretch(_cwPlaceholder.rectTransform, 0f, 0f, 0f, 0f);
            var inTxt = CwText(area.transform, "Text", "", 13f, Color.white, TextAlignmentOptions.Left);
            inTxt.overflowMode = TextOverflowModes.ScrollRect;
            LStretch(inTxt.rectTransform, 0f, 0f, 0f, 0f);

            _mpChatInputField = inGO.AddComponent<TMP_InputField>();
            _mpChatInputField.textViewport = art;
            _mpChatInputField.textComponent = inTxt;
            _mpChatInputField.placeholder = _cwPlaceholder;
            _mpChatInputField.targetGraphic = inImg;
            _mpChatInputField.lineType = TMP_InputField.LineType.SingleLine;
            _mpChatInputField.characterLimit = 120;
            _mpChatInputField.restoreOriginalTextOnEscape = false;   // R10: Esc keeps the draft
            _mpChatInputField.onFocusSelectAll = false;               // re-check LOW: a restored draft is not selected, so the next key does not erase it
            _mpChatInputField.caretWidth = 2;
            _mpChatInputField.customCaretColor = true;
            _mpChatInputField.caretColor = Color.white;
            _mpChatInputField.selectionColor = new Color(0.30f, 0.50f, 0.85f, 0.55f);
            _mpChatInputField.onValueChanged.AddListener(v => _mpChatInput = v ?? "");
            _mpChatInputField.onSelect.AddListener(_ => _mpChatFocus = true);
            _mpChatInputField.onDeselect.AddListener(_ => _mpChatFocus = false);
            _mpChatInputField.onSubmit.AddListener(_ => SubmitMpChat());

            var send = LImg(win, "Send", CW_BLUE, LRoundSprite(false), 3f);
            _mpSendRT = send.rectTransform;
            _mpSendRT.anchorMin = _mpSendRT.anchorMax = _mpSendRT.pivot = new Vector2(1f, 0f);
            _mpSendRT.anchoredPosition = new Vector2(-10f, 10f); _mpSendRT.sizeDelta = new Vector2(CW_SEND_W, CW_IN_H);
            LStretch(CwText(send.transform, "Text", "SEND", 12f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold, 6f).rectTransform, 0f, 0f, 0f, 0f);

            // Resize grip - bottom-left corner (today's behaviour): two short diagonal strokes, inside the 10 px margin
            // left of / below the typing box (R11: it no longer overlaps the box's corner).
            var gripGO = MakeGO("ResizeGrip", win);
            _mpGripRT = gripGO.GetComponent<RectTransform>();
            _mpGripRT.anchorMin = _mpGripRT.anchorMax = _mpGripRT.pivot = Vector2.zero;
            _mpGripRT.anchoredPosition = Vector2.zero; _mpGripRT.sizeDelta = new Vector2(10f, 10f);
            var gs = MakeGO("Strokes", gripGO.transform).GetComponent<RectTransform>();
            gs.anchorMin = gs.anchorMax = gs.pivot = Vector2.zero; gs.anchoredPosition = new Vector2(1f, 1f); gs.sizeDelta = new Vector2(8f, 8f);
            AddFade(CwBar(gs, CW_GRIP, -0.5f, -0.5f, 7f, 1.2f, -45f));
            AddFade(CwBar(gs, CW_GRIP, -2f, -2f, 3.4f, 1.2f, -45f));

            // TO dropdown (last child: draws over everything; it may overlay the list, never leaves the window - R5).
            _cwDropGO = MakeGO("ToDrop", win);
            _cwDropRootRT = _cwDropGO.GetComponent<RectTransform>();
            SetAnchored(_cwDropRootRT, CW_TO_X, -(CW_TO_TOP + CW_TO_H + 2f), 200f, CW_DD_ROW);
            var dsh = LImg(_cwDropGO.transform, "Shadow", new Color(0f, 0f, 0f, 0.40f), LShadowSprite(), 0f);
            dsh.pixelsPerUnitMultiplier = 2f;
            LStretch(dsh.rectTransform, -12f, -18f, -12f, -6f);
            var panel = LImg(_cwDropGO.transform, "Panel", Color.white, LRoundSprite(false), 3f);
            panel.raycastTarget = true;
            _cwDropPanelRT = panel.rectTransform;
            LStretch(_cwDropPanelRT, 0f, 0f, 0f, 0f);
            panel.gameObject.AddComponent<RectMask2D>();
            _cwDropContentRT = MakeGO("Content", panel.transform).GetComponent<RectTransform>();
            _cwDropContentRT.anchorMin = new Vector2(0f, 1f); _cwDropContentRT.anchorMax = Vector2.one; _cwDropContentRT.pivot = new Vector2(0.5f, 1f);
            _cwDropContentRT.sizeDelta = Vector2.zero; _cwDropContentRT.anchoredPosition = Vector2.zero;
            _cwDropGO.SetActive(false);

            SetMpOpacity(_mpOpacity);
            _mpWin.SetActive(false);   // hidden until the phone's Chat icon
        }

        // ── rows ──

        /// <summary>Typed text shown literally (R9): wrapped in noparse (as the lobby name rows), so '<3' reads '<3' and no
        /// rich-text tag takes effect. Re-check MEDIUM: TMP ends a tag name at '=' or a space, so '</noparse=1>' would also
        /// close the block - every '<' in typed text gets an invisible zero-width space after it, so no tag can form at all.
        /// Display only: the text sent over the network is the raw typed text.</summary>
        private static string CwEsc(string s)
        {
            s ??= "";
            if (s.IndexOf('<') >= 0) s = s.Replace("<", "<\u200B");
            return "<noparse>" + s + "</noparse>";
        }

        /// <summary>'Day {n} · ' + the clock in the game's own 12/24 h setting (TimeHelper, what the HUD uses - R8).</summary>
        private static string CwTimeText(ChatLine l)
        {
            string clock;
            try { clock = TimeHelper.GetFormattedTime(l.Hour, l.Minute); }
            catch { clock = l.Hour.ToString("00") + ":" + l.Minute.ToString("00"); }
            return "Day " + l.Day + " · " + clock;
        }

        private static float CwLin(float v) => v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
        private static float CwLum(Color c) => 0.2126f * CwLin(c.r) + 0.7152f * CwLin(c.g) + 0.0722f * CwLin(c.b);

        /// <summary>A player's colour for a chat NAME label: lightened toward white (hue kept) until it reaches 4.5:1 against
        /// the body #3C454F (manager 2026-09-28: raw player colours are only ~1.6-2.2:1 there). Cached; each mapping logged once.</summary>
        private string CwNameHex(string pid)
        {
            Color32 c32 = CwColour(pid);
            int key = (c32.r << 16) | (c32.g << 8) | c32.b;
            if (_cwNameHex.TryGetValue(key, out var hex)) return hex;
            try
            {
                Color c = c32, o = c;
                float body = CwLum(CW_BODY) + 0.05f, ratio = 0f;
                for (int i = 0; i <= 20; i++)
                {
                    o = Color.Lerp(c, Color.white, i * 0.05f);
                    ratio = (CwLum(o) + 0.05f) / body;
                    if (ratio >= 4.5f) break;
                }
                hex = "#" + ColorUtility.ToHtmlStringRGB(o);
                Plugin.Logger.LogInfo($"[Chat] name colour #{ColorUtility.ToHtmlStringRGB(c)} -> {hex} (contrast {ratio:F2}:1 on #3C454F)");
            }
            catch { hex = "#FFFFFF"; }
            _cwNameHex[key] = hex;
            return hex;
        }

        private string CwRender(ChatLine l)
        {
            try
            {
                bool mine = l.From == MPConfig.PlayerId;
                if (!string.IsNullOrEmpty(l.To))
                {
                    // U3: the NAME keeps the player's colour; only the word '(private)' is purple; the text stays #E9DCFF.
                    string col = CwNameHex(mine ? l.To : l.From);
                    string nm = mine ? "To " + CwEsc(CwName(l.To)) : CwEsc(CwName(l.From));
                    return "<b><color=" + col + ">" + nm + "</color> <color=" + CW_PRIV_LBL + ">(private)</color><color=" + col + ">:</color></b> <color="
                           + CW_PRIV_TXT + ">" + CwEsc(l.Text) + "</color>";
                }
                string who = mine ? "You:" : CwEsc(CwName(l.From)) + ":";
                return "<color=" + CwNameHex(l.From) + "><b>" + who + "</b></color> " + CwEsc(l.Text);
            }
            catch { return CwEsc(l.Text); }
        }

        private CwRow CwNewRow(ChatLine l, bool time)
        {
            var go = MakeGO(time ? "Time" : "Msg", _cwContentRT!);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 1f);
            var t = go.AddComponent<TextMeshProUGUI>();
            ApplyFont(t);
            t.raycastTarget = false; t.richText = true;
            t.textWrappingMode = TextWrappingModes.Normal; t.overflowMode = TextOverflowModes.Overflow;
            if (time) { t.fontSize = 11f; t.color = CW_TIME; t.alignment = TextAlignmentOptions.Center; t.text = CwTimeText(l); }
            else      { t.fontSize = 13f; t.color = Color.white; t.alignment = TextAlignmentOptions.TopLeft; t.text = CwRender(l); }
            return new CwRow { Line = time ? null : l, TimeOf = time ? l : null, RT = rt, T = t };
        }

        private static void CwDestroy(CwRow r) { try { UnityEngine.Object.Destroy(r.RT.gameObject); } catch { } }

        private static bool CwNeedsTime(ChatLine? prev, ChatLine l)
            => l.Day > 0 && (prev == null || prev.Day != l.Day || prev.Hour != l.Hour);

        /// <summary>Bring the rows in line with MPChat (on a Version change only): drop trimmed rows from the top, append
        /// new ones at the bottom. A full rebuild only when the log changed in the middle (a DEV removal).</summary>
        private void CwSyncRows()
        {
            try
            {
                if (_cwContentRT == null) return;
                MPChat.CopyAll(_cwSnap);
                int n = _cwSnap.Count;
                if (_cwEmptyGO != null && _cwEmptyGO.activeSelf != (n == 0)) _cwEmptyGO.SetActive(n == 0);

                int firstRow = -1;
                if (n > 0)
                    for (int i = 0; i < _cwRows.Count; i++)
                        if (ReferenceEquals(_cwRows[i].Line, _cwSnap[0])) { firstRow = i; break; }
                bool rebuild = false;
                int shown = 0;
                if (_cwRows.Count > 0)
                {
                    if (firstRow < 0) rebuild = true;
                    else
                        for (int i = firstRow; i < _cwRows.Count; i++)
                        {
                            var ln = _cwRows[i].Line;
                            if (ln == null) continue;
                            if (shown >= n || !ReferenceEquals(ln, _cwSnap[shown])) { rebuild = true; break; }
                            shown++;
                        }
                }
                if (rebuild)
                {
                    foreach (var r in _cwRows) CwDestroy(r);
                    _cwRows.Clear(); shown = 0; _cwScroll = 0f; _cwUnseen = false;
                }
                else if (firstRow > 0)
                {
                    int cut = firstRow;
                    if (_cwRows[cut - 1].Line == null && ReferenceEquals(_cwRows[cut - 1].TimeOf, _cwSnap[0])) cut--;   // keep snap[0]'s own time line
                    for (int i = 0; i < cut; i++) CwDestroy(_cwRows[i]);
                    _cwRows.RemoveRange(0, cut);
                    if (_cwRows.Count > 0 && _cwRows[0].Line != null && CwNeedsTime(null, _cwRows[0].Line!))
                        _cwRows.Insert(0, CwNewRow(_cwRows[0].Line!, true));
                }

                int appendFrom = _cwRows.Count;
                ChatLine? prev = shown > 0 ? _cwSnap[shown - 1] : null;
                for (int i = shown; i < n; i++)
                {
                    var l = _cwSnap[i];
                    if (CwNeedsTime(prev, l)) _cwRows.Add(CwNewRow(l, true));
                    _cwRows.Add(CwNewRow(l, false));
                    prev = l;
                }
                CwLayout(false);
                // Scrolled up and something arrived: keep the view where it is and show 'New messages'.
                if (!rebuild && appendFrom > 0 && _cwRows.Count > appendFrom && _cwScroll > 0.5f)
                {
                    float h = 0f;
                    for (int i = appendFrom; i < _cwRows.Count; i++) h += Mathf.Max(0f, _cwRows[i].H) + CW_ROW_GAP;
                    _cwScroll += h; _cwUnseen = true;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] rows: {ex.Message}"); }
        }

        /// <summary>Measure (new rows, or all on a width/font change) and stack the rows top-down; content height = the sum.</summary>
        private void CwLayout(bool remeasure)
        {
            try
            {
                if (_cwViewRT == null || _cwContentRT == null) return;
                float w = _cwViewRT.rect.width;
                if (w <= 1f) return;
                float y = 0f; int cnt = _cwRows.Count;
                for (int i = 0; i < cnt; i++)
                {
                    var r = _cwRows[i];
                    if (remeasure || r.H < 0f)
                    {
                        float ph = 0f;
                        try { ph = r.T.GetPreferredValues(r.T.text, w, 0f).y; } catch { }
                        r.H = Mathf.Ceil(Mathf.Max(ph, 1f)) + (r.Line == null ? 6f : 0f);
                    }
                    r.RT.offsetMin = new Vector2(0f, -(y + r.H));
                    r.RT.offsetMax = new Vector2(0f, -y);
                    y += r.H;
                    if (i < cnt - 1) y += CW_ROW_GAP;
                }
                _cwContentH = y;
                _cwContentRT.sizeDelta = new Vector2(0f, y);
                _cwLaidW = w;
                _cwAppScroll = -1f;   // re-apply position + thumb
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] layout: {ex.Message}"); }
        }

        private float CwThumbH(float trackH, float viewH)
            => _cwContentH > 0f ? Mathf.Clamp(trackH * viewH / _cwContentH, 18f, trackH) : trackH;

        /// <summary>Clamp the scroll, place the content and the thumb (only when something moved), pill + bar visibility.</summary>
        private void CwApplyScroll()
        {
            if (_cwViewRT == null || _cwContentRT == null) return;
            float viewH = _cwViewRT.rect.height;
            float max = Mathf.Max(0f, _cwContentH - viewH);
            _cwScroll = Mathf.Clamp(_cwScroll, 0f, max);
            if (_cwScroll <= 0.5f) _cwUnseen = false;
            float trackH = _cwTrackRT != null ? _cwTrackRT.rect.height : 0f;
            if (_cwScroll != _cwAppScroll || max != _cwAppMax || viewH != _cwAppViewH || trackH != _cwAppTrackH)
            {
                _cwAppScroll = _cwScroll; _cwAppMax = max; _cwAppViewH = viewH; _cwAppTrackH = trackH;
                _cwContentRT.anchoredPosition = new Vector2(0f, -_cwScroll);
                if (_cwThumbRT != null && trackH > 0f)
                {
                    float th = CwThumbH(trackH, viewH);
                    float top = (trackH - th) * (1f - (max > 0f ? _cwScroll / max : 0f));
                    _cwThumbRT.sizeDelta = new Vector2(0f, th);
                    _cwThumbRT.anchoredPosition = new Vector2(0f, -top);
                }
                bool bar = max > 0.5f;
                if (_cwTrackGO != null && _cwTrackGO.activeSelf != bar) _cwTrackGO.SetActive(bar);
            }
            bool pill = _cwUnseen && _cwScroll > 0.5f;
            if (_cwPillGO != null && _cwPillGO.activeSelf != pill)
            {
                if (pill && _cwPillRT != null && _cwPillTxt != null)
                {
                    float tw = 80f;
                    try { tw = _cwPillTxt.GetPreferredValues(_cwPillTxt.text).x; } catch { }
                    _cwPillRT.sizeDelta = new Vector2(28f + Mathf.Ceil(tw) + 12f, 24f);
                }
                _cwPillGO.SetActive(pill);
            }
        }

        // ── recipient: roster, TO box, dropdown ──

        private void CwCheckRoster()
        {
            try
            {
                // R14: LobbyPlayers is an immutable copy-on-write snapshot (MPServer/MPClient publish a new list under their
                // writer lock and never mutate a published one) - taking ONE reference below is the consistent snapshot.
                IReadOnlyList<string>? players = MPServer.IsRunning ? MPServer.LobbyPlayers
                                              : MPClient.IsConnected ? MPClient.LobbyPlayers : null;
                string me = MPConfig.PlayerId;
                _cwSigSb.Clear();
                if (players != null)
                    foreach (var p in players)
                        if (!string.IsNullOrEmpty(p) && p != me) _cwSigSb.Append(p).Append('=').Append(CwName(p)).Append('|');
                string sig = _cwSigSb.ToString();
                if (sig != _cwRosterSig)
                {
                    _cwRosterSig = sig;
                    _cwRoster.Clear();
                    if (players != null)
                        foreach (var p in players)
                            if (!string.IsNullOrEmpty(p) && p != me) _cwRoster.Add(p);
                    _cwTargetShown = "\0";
                    foreach (var r in _cwRows) if (r.Line != null) r.T.text = CwRender(r.Line);   // names may have resolved
                    _cwLaidW = -1f;                                                                // re-measure next frame
                    if (_cwDropOpen) CwBuildDrop();
                }
                // R3: the chosen player left -> Everyone, but ONLY while the typing box is empty: a private draft keeps its
                // target (SubmitMpChat refuses to send it to a player who is gone - it never turns public by itself).
                if (_chatTarget != "" && string.IsNullOrWhiteSpace(_mpChatInput) && !_cwRoster.Contains(_chatTarget)) _chatTarget = "";
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] roster: {ex.Message}"); }
        }

        private void CwShowTarget()
        {
            _cwTargetShown = _chatTarget;
            string name = _chatTarget == "" ? "Everyone" : CwName(_chatTarget);
            if (_cwToTxt != null) _cwToTxt.text = name;
            if (_cwPlaceholder != null) _cwPlaceholder.text = _chatTarget == "" ? "Message everyone" : "Message " + name;
        }

        private void CwOpenDrop()
        {
            try
            {
                if (_cwDropGO == null) return;
                _cwDropOpen = true;
                CwBuildDrop();
                _cwDropGO.SetActive(true);
                _cwDropGO.transform.SetAsLastSibling();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] dropdown: {ex.Message}"); }
        }

        private void CwCloseDrop()
        {
            _cwDropOpen = false;
            try { if (_cwDropGO != null && _cwDropGO.activeSelf) _cwDropGO.SetActive(false); } catch { }
        }

        /// <summary>Everyone + every other online player (colour dot + name); scrolls (wheel) past six rows.</summary>
        private void CwBuildDrop()
        {
            try
            {
                if (_cwDropContentRT == null || _cwDropRootRT == null || _cwToBoxRT == null) return;
                foreach (var it in _cwDropItems) { try { UnityEngine.Object.Destroy(it.rt.gameObject); } catch { } }
                _cwDropItems.Clear();
                int n = 1 + _cwRoster.Count;
                for (int i = 0; i < n; i++)
                {
                    string who = i == 0 ? "" : _cwRoster[i - 1];
                    var rt = MakeGO("Opt", _cwDropContentRT).GetComponent<RectTransform>();
                    CwTopStretch(rt, 0f, 0f, i * CW_DD_ROW, CW_DD_ROW);
                    if (who == _chatTarget) { var sel = rt.gameObject.AddComponent<Image>(); sel.color = CW_DDSEL; sel.raycastTarget = false; }
                    if (who != "")
                    {
                        var dot = LImg(rt, "Dot", CwColour(who), LRoundSprite(false), 5f).rectTransform;
                        dot.anchorMin = dot.anchorMax = new Vector2(0f, 0.5f); dot.pivot = new Vector2(0f, 0.5f);
                        dot.anchoredPosition = new Vector2(10f, 0f); dot.sizeDelta = new Vector2(10f, 10f);
                    }
                    var t = CwText(rt, "Name", who == "" ? "Everyone" : CwName(who), 13f, CW_DDTXT, TextAlignmentOptions.Left);
                    LStretch(t.rectTransform, 28f, 0f, 10f, 0f);
                    _cwDropItems.Add((rt, who));
                }
                _cwDropContentH = n * CW_DD_ROW;
                _cwDropContentRT.sizeDelta = new Vector2(0f, _cwDropContentH);
                _cwDropScroll = 0f; _cwDropContentRT.anchoredPosition = Vector2.zero;
                // R5: as many rows as fit inside the window below the TO box (the rest scroll) - it never hangs off-screen.
                int fit = CW_DD_MAXROWS;
                if (_mpWinRT != null)
                    fit = Mathf.Clamp(Mathf.FloorToInt((_mpWinRT.sizeDelta.y - (CW_TO_TOP + CW_TO_H + 2f) - 8f) / CW_DD_ROW), 1, CW_DD_MAXROWS);
                _cwDropRootRT.sizeDelta = new Vector2(_cwToBoxRT.rect.width, Mathf.Min(n, fit) * CW_DD_ROW);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] dropdown build: {ex.Message}"); }
        }

        // ── per-frame (visible only) ──

        /// <summary>Main thread, every frame in game (cheap: twice a second): hand MPChat the game's own day + clock
        /// (SaveGameManager.Current Day/Hour/Minute - what the HUD's 'Sunday (Day 133)' / '12:48' show) so a line
        /// added on the network thread is stamped with the in-game time.</summary>
        private void CwTickClock()
        {
            float now = Time.unscaledTime;
            if (now < _cwNextClock) return;
            _cwNextClock = now + 0.5f;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi != null) MPChat.SetClock(gi.Day, gi.Hour, Mathf.FloorToInt(gi.Minute));
                else MPChat.SetClock(0, 0, 0);   // R7: main menu / lobby - no stale clock; a line added now gets no time line
            }
            catch { }
        }

        private void RefreshMpWindow()
        {
            if (_mpWin == null) return;
            try
            {
                StyleMpWindow();   // self-gated: restyles only when a new game font is captured
                float now = Time.unscaledTime;
                if (now >= _cwNextRoster) { _cwNextRoster = now + 0.5f; CwCheckRoster(); }
                if (_cwTargetShown != _chatTarget) CwShowTarget();
                int v = MPChat.Version;
                if (v != _cwVersion) { _cwVersion = v; CwSyncRows(); }
                // R4: while the grip is held, re-measure at most every 100 ms (the release re-measures at the final width).
                if (_cwViewRT != null && Mathf.Abs(_cwViewRT.rect.width - _cwLaidW) > 0.5f && (!_mpResizing || now >= _cwNextRelayout))
                { _cwNextRelayout = now + 0.1f; CwLayout(true); }
                if (Screen.width != _cwPlaceScrW || Screen.height != _cwPlaceScrH || UiScale != _cwPlaceScale) CwPlaceWindow();
                CwApplyScroll();
                if (_mpChatInputField != null && _mpChatInputField.text != _mpChatInput)
                    _mpChatInputField.SetTextWithoutNotify(_mpChatInput);
            }
            catch (Exception ex)
            {
                if (!_cwRefreshErrLogged) { _cwRefreshErrLogged = true; Plugin.Logger.LogWarning($"[Chat] refresh: {ex}"); }
            }
        }

        private void CwClick(Vector2 mp)
        {
            if (_cwDropOpen)
            {
                if (CwHit(_cwDropPanelRT, mp))
                {
                    foreach (var it in _cwDropItems)
                        if (RectHit(it.rt, mp)) { _chatTarget = it.who; break; }
                    CwCloseDrop();
                    return;
                }
                CwCloseDrop();                              // a click anywhere else closes it
                if (CwHit(_cwToBoxRT, mp)) return;          // ...and the box itself toggles it shut
            }
            if (CwHit(_mpCloseRT, mp)) { ToggleMpWindow(); return; }
            if (CwHit(_mpGripRT, mp))
            {
                _mpResizing = true; _mpResizeStartMouse = mp;
                _mpResizeStartSize = _mpWinRT != null ? _mpWinRT.sizeDelta : new Vector2(MPW_W, MPW_H);
                _cwResizeStartPos  = _mpWinRT != null ? _mpWinRT.anchoredPosition : Vector2.zero;
                return;
            }
            if (CwHit(_mpOpacityTrackRT, mp)) { _mpOpacityDragging = true; ApplyOpacityFromMouse(mp); return; }   // before the title: it lives IN the bar
            if (CwHit(_cwToBoxRT, mp)) { CwOpenDrop(); return; }
            if (CwHit(_cwPillRT, mp)) { _cwScroll = 0f; _cwUnseen = false; return; }
            if (CwHit(_cwTrackRT, mp) && _cwTrackRT != null && _cwViewRT != null)
            {
                float trackH = _cwTrackRT.rect.height, viewH = _cwViewRT.rect.height;
                float max = Mathf.Max(0f, _cwContentH - viewH), th = CwThumbH(trackH, viewH);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_cwTrackRT, mp, null, out var loc);
                float fromTop = _cwTrackRT.rect.yMax - loc.y;
                float thumbTop = (trackH - th) * (1f - (max > 0f ? _cwScroll / max : 0f));
                if (fromTop >= thumbTop && fromTop <= thumbTop + th)
                { _cwThumbDrag = true; _cwThumbStartY = mp.y; _cwThumbStartScroll = _cwScroll; }
                else
                {
                    float range = trackH - th;
                    float f = range > 0f ? Mathf.Clamp01((fromTop - th * 0.5f) / range) : 1f;
                    _cwScroll = max * (1f - f);
                }
                return;
            }
            if (CwHit(_mpTitleRT, mp)) { _mpDragging = true; _mpDragLast = mp; return; }
            if (CwHit(_mpSendRT, mp)) SubmitMpChat();
        }

        private void TickMpWindow()
        {
            if (_mpWin == null || !_mpWin.activeSelf) { _chatSuppress = false; return; }
            try
            {
                Vector2 mp = Input.mousePosition;

                // Chat's contribution to the input-suppression flag: FOCUS-ONLY (see the history in LateUpdate) - the
                // game's keyboard/shortcut gate is forced only while the box is focused for typing.
                _mpChatFocus = _mpChatInputField != null && _mpChatInputField.isFocused;
                _chatSuppress = _mpChatFocus;

                if (Input.GetMouseButtonDown(0)) CwClick(mp);
                if (Input.GetMouseButtonUp(0))
                {
                    bool placed = _mpDragging || _mpResizing;
                    _mpDragging = false; _mpOpacityDragging = false; _mpResizing = false; _cwThumbDrag = false;
                    if (placed) CwSavePlace();   // U2: remembered on this machine
                }

                float us = Mathf.Max(0.01f, UiScale);   // R6: mouse pixels -> canvas units
                if (_mpDragging && _mpWinRT != null)
                {
                    Vector2 d = (mp - _mpDragLast) / us;
                    _mpDragLast = mp;
                    _mpWinRT.anchoredPosition = CwClampPos(_mpWinRT.anchoredPosition + d, _mpWinRT.sizeDelta);
                }
                if (_mpResizing && _mpWinRT != null)   // today's grip: drag the bottom-left corner out; the top-right corner stays put
                {
                    float cw = Screen.width / us;
                    float top = _cwResizeStartPos.y + _mpResizeStartSize.y;
                    float w = _mpResizeStartSize.x + (_mpResizeStartMouse.x - mp.x) / us;
                    float h = _mpResizeStartSize.y + (_mpResizeStartMouse.y - mp.y) / us;
                    w = Mathf.Clamp(w, CW_MIN_W, Mathf.Max(CW_MIN_W, Mathf.Min(CW_MAX_W, cw + _cwResizeStartPos.x)));
                    h = Mathf.Clamp(h, CW_MIN_H, Mathf.Max(CW_MIN_H, Mathf.Min(CW_MAX_H, top)));
                    _mpWinRT.sizeDelta = new Vector2(w, h);
                    _mpWinRT.anchoredPosition = new Vector2(_cwResizeStartPos.x, Mathf.Max(0f, top - h));
                }
                if (_mpOpacityDragging) ApplyOpacityFromMouse(mp);

                if (_cwThumbDrag && _cwTrackRT != null && _cwViewRT != null)
                {
                    float trackH = _cwTrackRT.rect.height, viewH = _cwViewRT.rect.height;
                    float max = Mathf.Max(0f, _cwContentH - viewH), range = trackH - CwThumbH(trackH, viewH);
                    if (range > 1f)
                        _cwScroll = Mathf.Clamp(_cwThumbStartScroll + (mp.y - _cwThumbStartY) / Mathf.Max(0.01f, UiScale) * max / range, 0f, max);
                }

                float sw = Input.mouseScrollDelta.y;
                if (sw != 0f)
                {
                    if (_cwDropOpen && CwHit(_cwDropPanelRT, mp) && _cwDropPanelRT != null && _cwDropContentRT != null)
                    {
                        float dmax = Mathf.Max(0f, _cwDropContentH - _cwDropPanelRT.rect.height);
                        _cwDropScroll = Mathf.Clamp(_cwDropScroll - sw * CW_DD_ROW, 0f, dmax);
                        _cwDropContentRT.anchoredPosition = new Vector2(0f, _cwDropScroll);
                    }
                    else if (CwHit(_cwListHitRT, mp)) _cwScroll += sw * CW_WHEEL;   // wheel up = older
                }

                if (Input.GetKeyDown(KeyCode.Escape))   // R10: Esc closes the TO dropdown too; the draft stays
                {
                    if (_cwDropOpen) CwCloseDrop();
                    if (_mpChatFocus) { _mpChatInputField?.DeactivateInputField(); _mpChatFocus = false; }
                }
            }
            catch (Exception ex)
            {
                if (!_cwTickErrLogged) { _cwTickErrLogged = true; Plugin.Logger.LogWarning($"[Chat] tick: {ex}"); }
            }
            // Player-movement block (typing WASD shouldn't walk the character) is owned by LateUpdate.
        }

        // ── place: default next to the phone (U1), remembered per machine (U2) ──

        /// <summary>Is this player still in the session (a live, lock-free snapshot - see CwCheckRoster)?</summary>
        private static bool CwTargetOnline(string pid)
        {
            try
            {
                var players = MPServer.IsRunning ? MPServer.LobbyPlayers : MPClient.IsConnected ? MPClient.LobbyPlayers : null;
                if (players == null) return false;
                foreach (var p in players) if (p == pid) return true;
                return false;
            }
            catch { return true; }
        }

        /// <summary>Keep the whole window on screen (canvas units; anchored bottom-right).</summary>
        private static Vector2 CwClampPos(Vector2 p, Vector2 size)
        {
            float us = Mathf.Max(0.01f, UiScale);
            float cw = Screen.width / us, ch = Screen.height / us;
            p.x = Mathf.Clamp(p.x, Mathf.Min(0f, -(cw - size.x)), 0f);
            p.y = Mathf.Clamp(p.y, 0f, Mathf.Max(0f, ch - size.y));
            return p;
        }

        /// <summary>The default place: right edge CW_PHONE_GAP left of the BizPhone (UIs.smartphoneCollapsibleWindow, the
        /// phone body the game itself collapses), bottom level with its bottom; lifted above the bottom-centre street name
        /// if it would reach it. A collapsed/closed phone reuses the last measured place (U1: 'the same place is fine').</summary>
        private Vector2 CwDefaultPos()
        {
            Vector2 p = _cwPhoneKnown ? _cwPhonePos : CW_DEF_FALLBACK;
            try
            {
                var cwin = InstanceBehavior<global::UI.UIs>.Instance?.smartphoneCollapsibleWindow;
                if (cwin != null && cwin.gameObject.activeInHierarchy && !cwin.IsCollapsed && cwin.transform is RectTransform prt)
                {
                    var canvas = prt.GetComponentInParent<Canvas>();
                    if (canvas != null) canvas = canvas.rootCanvas;
                    Camera? cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                    prt.GetWorldCorners(_cwCorners);
                    float left = float.MaxValue, bottom = float.MaxValue;
                    for (int i = 0; i < 4; i++)
                    {
                        var sp = RectTransformUtility.WorldToScreenPoint(cam, _cwCorners[i]);
                        left = Mathf.Min(left, sp.x); bottom = Mathf.Min(bottom, sp.y);
                    }
                    float us = Mathf.Max(0.01f, UiScale);
                    if (left > 0f && left < Screen.width)
                    {
                        p = new Vector2(-((Screen.width - left) / us + CW_PHONE_GAP), Mathf.Max(0f, bottom / us));
                        float cw = Screen.width / us;
                        if (cw + p.x - MPW_W < cw * 0.5f + CW_STREET_HALF) p.y = Mathf.Max(p.y, CW_STREET_TOP);   // keep the street name clear
                        _cwPhonePos = p; _cwPhoneKnown = true;
                        if (!_cwPhoneLogged)
                        {
                            _cwPhoneLogged = true;
                            Plugin.Logger.LogInfo($"[Chat] default place: phone left={left:F0}px bottom={bottom:F0}px screen={Screen.width}x{Screen.height} scale={us:F2} -> pos={p.x:F0},{p.y:F0}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_cwPhoneErrLogged) { _cwPhoneErrLogged = true; Plugin.Logger.LogWarning($"[Chat] default place: {ex.Message}"); }
            }
            return p;
        }

        private static bool CwTryReadPlace(out Vector2 pos, out Vector2 size)
        {
            pos = size = Vector2.zero;
            try
            {
                var parts = (MPConfig.ChatWindowPlace ?? "").Split(',');
                if (parts.Length != 4) return false;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var fs = System.Globalization.NumberStyles.Float;
                if (!float.TryParse(parts[0], fs, inv, out float x) || !float.TryParse(parts[1], fs, inv, out float y)
                    || !float.TryParse(parts[2], fs, inv, out float w) || !float.TryParse(parts[3], fs, inv, out float h)) return false;
                // Re-check LOW: 'NaN' parses and survives Mathf.Clamp - a hand-edited or damaged value falls back to the default.
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(w) || float.IsNaN(h)
                    || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(w) || float.IsInfinity(h)) return false;
                pos = new Vector2(x, y);
                size = new Vector2(Mathf.Clamp(w, CW_MIN_W, CW_MAX_W), Mathf.Clamp(h, CW_MIN_H, CW_MAX_H));
                return true;
            }
            catch { return false; }
        }

        /// <summary>On open and on a resolution / UI-scale change: the remembered place (U2) or the default, clamped on screen.</summary>
        private void CwPlaceWindow()
        {
            _cwPlaceScrW = Screen.width; _cwPlaceScrH = Screen.height; _cwPlaceScale = UiScale;
            if (_mpWinRT == null) return;
            try
            {
                Vector2 pos, size;
                if (CwTryReadPlace(out pos, out size))
                    Plugin.Logger.LogInfo($"[Chat] place restored: {MPConfig.ChatWindowPlace}");
                else { size = new Vector2(MPW_W, MPW_H); pos = CwDefaultPos(); }
                float us = Mathf.Max(0.01f, UiScale);
                size.x = Mathf.Min(size.x, Mathf.Max(CW_MIN_W, Screen.width / us));
                size.y = Mathf.Min(size.y, Mathf.Max(CW_MIN_H, Screen.height / us));
                _mpWinRT.sizeDelta = size;
                _mpWinRT.anchoredPosition = CwClampPos(pos, size);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] place: {ex.Message}"); }
        }

        /// <summary>Remember the place on this machine (MPConfig, per install) - on the drag / grip release only.</summary>
        private void CwSavePlace()
        {
            if (_mpWinRT == null) return;
            try
            {
                var p = _mpWinRT.anchoredPosition; var sz = _mpWinRT.sizeDelta;
                string v = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F0},{1:F0},{2:F0},{3:F0}", p.x, p.y, sz.x, sz.y);
                if (v == MPConfig.ChatWindowPlace) return;
                MPConfig.SetChatWindowPlace(v);
                Plugin.Logger.LogInfo($"[Chat] place saved: {v}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Chat] place save: {ex.Message}"); }
        }

#if BAMP_DEV
        // DEV levers (TestDrive `chatview`): read the state, size / scroll / reset the window the way the grip / wheel do.
        private string CwNewestVisible()
        {
            if (_cwRows.Count == 0 || _cwViewRT == null) return "none";
            var a = new Vector3[4]; var b = new Vector3[4];
            _cwRows[_cwRows.Count - 1].RT.GetWorldCorners(a); _cwViewRT.GetWorldCorners(b);
            return (a[0].y >= b[0].y - 0.5f && a[1].y <= b[1].y + 0.5f) ? "True" : "False";
        }

        internal string DevChatState()
        {
            try
            {
                var sz = _mpWinRT != null ? _mpWinRT.sizeDelta : Vector2.zero;
                var pos = _mpWinRT != null ? _mpWinRT.anchoredPosition : Vector2.zero;
                float viewH = _cwViewRT != null ? _cwViewRT.rect.height : -1f;
                int msgs = 0, times = 0;
                foreach (var r in _cwRows) { if (r.Line != null) msgs++; else times++; }
                return $"visible={_mpWinVisible} active={(_mpWin != null && _mpWin.activeSelf)} size={sz.x:F0}x{sz.y:F0} pos={pos.x:F0},{pos.y:F0} " +
                       $"listH={viewH:F0} contentH={_cwContentH:F0} scroll={_cwScroll:F0} maxScroll={Mathf.Max(0f, _cwContentH - viewH):F0} " +
                       $"rows={msgs} timeRows={times} lines={MPChat.Snapshot(500).Count} pill={(_cwPillGO != null && _cwPillGO.activeSelf)} " +
                       $"drop={_cwDropOpen} dropItems={_cwDropItems.Count} to={(_chatTarget == "" ? "everyone" : _chatTarget)} " +
                       $"saved={(string.IsNullOrEmpty(MPConfig.ChatWindowPlace) ? "none" : MPConfig.ChatWindowPlace)} newestVisible={CwNewestVisible()}";
            }
            catch (Exception ex) { return "stateErr=" + ex.Message; }
        }

        internal string DevChatSize(float w, float h)
        {
            try
            {
                if (_mpWinRT == null) return "noWindow";
                _mpWinRT.sizeDelta = new Vector2(Mathf.Clamp(w, CW_MIN_W, CW_MAX_W), Mathf.Clamp(h, CW_MIN_H, CW_MAX_H));   // the grip's own limits
                _mpWinRT.anchoredPosition = CwClampPos(_mpWinRT.anchoredPosition, _mpWinRT.sizeDelta);
                return DevChatState();
            }
            catch (Exception ex) { return "sizeErr=" + ex.Message; }
        }

        internal string DevChatScroll(float px)
        {
            try { _cwScroll = Mathf.Max(0f, px); return DevChatState(); }   // px back from the bottom; clamped next frame as a wheel scroll is
            catch (Exception ex) { return "scrollErr=" + ex.Message; }
        }

        internal string DevChatDrop(bool open)
        {
            try { if (open) CwOpenDrop(); else CwCloseDrop(); return DevChatState(); }   // = a click on the TO box / outside it
            catch (Exception ex) { return "dropErr=" + ex.Message; }
        }

        internal string DevChatReset()
        {
            try
            {
                if (_mpWinRT == null) return "noWindow";
                MPConfig.SetChatWindowPlace("");   // forget the remembered place: the next open uses the default again
                _mpWinRT.sizeDelta = new Vector2(MPW_W, MPW_H);
                _mpWinRT.anchoredPosition = CwClampPos(CwDefaultPos(), _mpWinRT.sizeDelta); _cwScroll = 0f;
                return DevChatState();
            }
            catch (Exception ex) { return "resetErr=" + ex.Message; }
        }

        /// <summary>= a drag + grip release at (x, y) W x H: placed, clamped and REMEMBERED (U2).</summary>
        internal string DevChatPlace(float x, float y, float w, float h)
        {
            try
            {
                if (_mpWinRT == null) return "noWindow";
                _mpWinRT.sizeDelta = new Vector2(Mathf.Clamp(w, CW_MIN_W, CW_MAX_W), Mathf.Clamp(h, CW_MIN_H, CW_MAX_H));
                _mpWinRT.anchoredPosition = CwClampPos(new Vector2(x, y), _mpWinRT.sizeDelta);
                CwSavePlace();
                return DevChatState();
            }
            catch (Exception ex) { return "placeErr=" + ex.Message; }
        }
#endif
    }
}
