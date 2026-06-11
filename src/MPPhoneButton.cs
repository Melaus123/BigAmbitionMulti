using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// The BizPhone "Chat" button — a native-looking 9th app icon on the game's
    /// bottom-left phone that opens the MP window (eventually replacing F9).
    ///
    /// Probe-established facts (2026-06-10): the app area
    /// `Canvases/Smartphone/Container/Phone/AppButtons` is a GridLayoutGroup
    /// (2 columns, 250x225 cells, no clipping mask) and contains the game's own
    /// INACTIVE `AppButtonTemplate` — so we instantiate that template, the grid
    /// reflows it in automatically, and we grow the phone body one cell row
    /// taller so the new row sits on the phone art.  The game's own
    /// SmartphoneAppButton logic is stripped (its AppName enum is full) and
    /// replaced with a plain Button → MP-window toggle.
    /// </summary>
    public static class MPPhoneButton
    {
        private const float CompactCellHeight = 180f;   // 5 rows × 180 = 4 rows × 225
        private const string ButtonName   = "BAMP_Chat";
        private const string IconFile     = "BAMP_ChatIcon.png";

        /// <summary>Set by the button's click; consumed by MPCanvasUI.Update.</summary>
        public static volatile bool OpenRequested;

        private static bool  _injected;
        private static int   _tries;
        private static float _nextTryAt;
        private static Sprite? _iconSprite;
        private static RectTransform? _iconRT;     // pulsed on unread chat
        private static Vector3 _iconBaseScale = Vector3.one;   // copied from the native sibling
        private static int _seenChatVersion;

        // Delayed icon-metrics re-copy (post-layout / post-native-refit).
        private static Transform? _recopyButtons;
        private static GameObject? _recopyGo;
        private static Image? _recopyIcon;
        private static float _recopyAt;

        // Native notification badge on the cloned button (unread chat count).
        private static GameObject? _badgeGO;
        private static TMPro.TextMeshProUGUI? _badgeText;

        // Height keeper: native phone heights the game re-applies on its own
        // state changes (radio hidden/visible).  Keeper holds current+RowGrowth.
        private static readonly float[] NativeHeights = { 1050f, 1230f };
        private static RectTransform? _keeperRT;
        private static float _keeperNext;
        private static int   _keeperLogs;

        public static void Reset()
        {
            _injected = false; _tries = 0; _nextTryAt = 0f;
            _iconRT = null; _iconBaseScale = Vector3.one;
            _recopyButtons = null; _recopyGo = null; _recopyIcon = null; _recopyAt = 0f;
            _badgeGO = null; _badgeText = null;
            _keeperRT = null; _keeperNext = 0f; _keeperLogs = 0;
            _hubIconRT = null; _hubIconBaseScale = Vector3.one;
            _hubRecopyGo = null; _hubRecopyIcon = null;
            _hubBadgeGO = null; _hubBadgeText = null;
        }

        /// <summary>Unread-message pulse: while the MP window is closed and chat
        /// lines have arrived since it was last open, the Chat icon breathes
        /// (gentle sine scale around its native base scale).  Main thread, per frame.</summary>
        public static void TickPulse(bool windowVisible)
        {
            if (_iconRT == null) return;
            try
            {
                if (windowVisible || MPChat.Version == _seenChatVersion)
                {
                    if (windowVisible) _seenChatVersion = MPChat.Version;   // window open = caught up
                    if (_iconRT.localScale != _iconBaseScale) _iconRT.localScale = _iconBaseScale;
                    if (_badgeGO != null && _badgeGO.activeSelf) _badgeGO.SetActive(false);
                    return;
                }
                // Unread: ~0.8 Hz breathe between 1.00× and 1.12× of base scale
                // (unscaled time — keeps pulsing while the game is paused) + the
                // game's own notification badge with the unread count.
                float s = 1f + 0.06f * (1f + Mathf.Sin(Time.unscaledTime * 5f));
                _iconRT.localScale = _iconBaseScale * s;
                if (_badgeGO != null)
                {
                    if (!_badgeGO.activeSelf) _badgeGO.SetActive(true);
                    if (_badgeText != null)
                    {
                        int unread = Mathf.Clamp(MPChat.Version - _seenChatVersion, 1, 9);
                        _badgeText.text = unread.ToString();
                    }
                }
            }
            catch { _iconRT = null; }   // icon died with its scene — re-arm on next inject
        }

        /// <summary>Main-thread, once per frame.  Injects once per game scene
        /// while MP is active; bounded retries (the phone UI may not exist for
        /// the first seconds of a scene).</summary>
        public static void Tick(bool inGame, bool mpActive)
        {
            // Pending post-layout icon re-copy (after inject) — both buttons:
            // the natives' own script re-fits their icons after the grid lays
            // out; our clones have no script, so we mirror a sibling late.
            if (_recopyAt > 0f && Time.unscaledTime >= _recopyAt)
            {
                _recopyAt = 0f;
                try
                {
                    if (_recopyButtons != null && _recopyGo != null) CopyIconMetricsFromSibling(_recopyButtons, _recopyGo, _recopyIcon);
                    if (_recopyButtons != null && _hubRecopyGo != null) CopyIconMetricsFromSibling(_recopyButtons, _hubRecopyGo, _hubRecopyIcon);
                    // Size diagnostic: both our icons after the late re-fit.
                    if (_recopyIcon != null && _hubRecopyIcon != null)
                        Plugin.Logger.LogInfo($"[PhoneBtn] icon metrics post-refit: chat={_recopyIcon.rectTransform.sizeDelta} hub={_hubRecopyIcon.rectTransform.sizeDelta}");
                }
                catch { }
            }

            // Height keeper: whenever the game re-applies a NATIVE phone height
            // (radio toggles etc.), re-add our one row on top of it.  Touches
            // nothing while collapsed/animating (those heights match neither
            // native value).
            if (_keeperRT != null && Time.unscaledTime >= _keeperNext)
            {
                _keeperNext = Time.unscaledTime + 0.5f;
                try
                {
                    float h = _keeperRT.rect.height;
                    foreach (float native in NativeHeights)
                    {
                        if (Mathf.Abs(h - native) < 6f)
                        {
                            var rt = _keeperRT;
                            rt.sizeDelta = new Vector2(rt.sizeDelta.x, rt.sizeDelta.y + (native + RowGrowth - h));
                            if (_keeperLogs++ < 6)
                                Plugin.Logger.LogInfo($"[PhoneBtn] keeper: native {native:F0} re-applied by game → held at {native + RowGrowth:F0}.");
                            break;
                        }
                    }
                }
                catch { _keeperRT = null; }   // died with scene — re-arm on next inject
            }

            if (!inGame || !mpActive || _injected || _tries >= 30) return;
            float now = Time.unscaledTime;
            if (now < _nextTryAt) return;
            _nextTryAt = now + 2f;
            _tries++;
            try { TryInject(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] inject: {ex.Message}"); }
        }

        private static void TryInject()
        {
            var t = VehicleManager.FindGameType("SmartphoneUI");
            if (t == null) return;
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(t), true);
            if (arr == null || arr.Length == 0) return;
            var ui = arr[0].TryCast<Component>();
            if (ui == null) return;

            var buttons = ui.transform.Find("Container/Phone/AppButtons");
            if (buttons == null) { Plugin.Logger.LogWarning("[PhoneBtn] AppButtons not found under Smartphone."); return; }
            if (buttons.Find(ButtonName) != null) { _injected = true; return; }   // already there (re-entry)

            var template = buttons.Find("AppButtonTemplate");
            var src = template != null ? template.gameObject : buttons.GetChild(0).gameObject;

            var go = UnityEngine.Object.Instantiate(src, buttons);
            go.name = ButtonName;

            // Strip the game's app-button logic (AppName-driven; the enum is
            // full) and any localization that would overwrite our label.
            int stripped = 0;
            var comps = go.GetComponents(Il2CppType.Of<Component>());
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                string cn = c.GetIl2CppType().Name;
                if (cn == "SmartphoneAppButton") { UnityEngine.Object.Destroy(c); stripped++; }
            }
            foreach (var c in go.GetComponentsInChildren(Il2CppType.Of<Component>(), true))
            {
                var cc = c.TryCast<Component>();
                if (cc == null) continue;
                if (cc.GetIl2CppType().Name.Contains("Localization"))
                { var b = cc.TryCast<Behaviour>(); if (b != null) b.enabled = false; }
            }

            // Label → "Chat"; log the child structure once for refinement.
            var texts = go.GetComponentsInChildren<TMPro.TMP_Text>(true);
            foreach (var txt in texts) { if (txt != null) txt.text = "Chat"; }

            // Icon: our sprite on the most icon-looking Image (named *Icon*),
            // else the largest child Image.  Falls back to a label-only button.
            var icon = LoadIcon();
            Image? target = null;
            var images = go.GetComponentsInChildren<Image>(true);
            foreach (var img in images)
            {
                if (img == null) continue;
                if (img.gameObject.name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0) { target = img; break; }
                if (target == null || img.rectTransform.rect.width > target.rectTransform.rect.width) target = img;
            }
            if (icon != null && target != null)
            {
                target.sprite = icon;
                target.color  = Color.white;
                target.type   = Image.Type.Simple;
                target.preserveAspect = true;
            }
            // The template carries a white background plate — the native buttons'
            // visible shape is just their icon sprite, so every non-icon Image on
            // our clone goes fully transparent (still raycastable for the click).
            foreach (var img in images)
            {
                if (img == null || ReferenceEquals(img, target)) continue;
                img.color = new Color(1f, 1f, 1f, 0f);
            }
            _iconRT = target != null ? target.rectTransform : null;
            Plugin.Logger.LogInfo($"[PhoneBtn] structure: imgs={images.Length} texts={texts.Length} stripped={stripped} icon={(icon != null ? "loaded" : "MISSING (label only)")} target='{target?.gameObject.name}'");

            // Click → MP window.  Plain uGUI Button (the grid is a real canvas).
            var btnComp = go.GetComponent(Il2CppType.Of<Button>());
            var btn = btnComp != null ? btnComp.TryCast<Button>() : null;
            if (btn == null) btn = go.AddComponent(Il2CppType.Of<Button>())!.TryCast<Button>();
            if (btn != null)
            {
                btn.onClick = new Button.ButtonClickedEvent();   // clear template listeners
                btn.onClick.AddListener((UnityAction)(() => { OpenRequested = true; }));
                if (target != null) btn.targetGraphic = target;
            }

            go.SetActive(true);

            // Layout — the dump (2026-06-10) finally located the art: it's a
            // 9-SLICED Image right on 'Phone' (stretch-anchored to Container),
            // and Container is managed by the game's CollapsibleWindow script —
            // which is why direct sizeDelta writes "didn't take" (the script
            // reasserts its own height).  So: raise the height value INSIDE
            // CollapsibleWindow (self-located: the float field equal to the
            // live height) + grow Container/AppButtons.  Fallback: compact grid.
            if (TryExpandPhone(ui.transform, buttons))
            {
                Plugin.Logger.LogInfo("[PhoneBtn] phone EXPANDED one row (CollapsibleWindow-aware); native cells kept.");
            }
            else
            {
                try
                {
                    var gridComp = buttons.GetComponent(Il2CppType.Of<GridLayoutGroup>());
                    var grid = gridComp != null ? gridComp.TryCast<GridLayoutGroup>() : null;
                    if (grid != null)
                    {
                        var old = grid.cellSize;
                        grid.cellSize = new Vector2(old.x, CompactCellHeight);
                        Plugin.Logger.LogInfo($"[PhoneBtn] FALLBACK compact grid: cellSize {old.x:F0}x{old.y:F0} → {old.x:F0}x{CompactCellHeight:F0}.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] cell resize: {ex.Message}"); }
            }

            // Native unread badge: the cloned button carries the game's own
            // notification Badge child — drive it directly for unread chat.
            try
            {
                var badge = go.transform.Find("Badge");
                if (badge != null)
                {
                    _badgeGO = badge.gameObject;
                    // The game's Badge script binds to its own data — disable it
                    // and drive the visuals ourselves.
                    foreach (var c in badge.GetComponents(Il2CppType.Of<Component>()))
                    {
                        var cc = c.TryCast<Behaviour>();
                        if (cc != null && cc.GetIl2CppType().Name == "Badge") cc.enabled = false;
                    }
                    var btx = badge.Find("BadgeText");
                    _badgeText = btx != null ? btx.GetComponent<TMPro.TextMeshProUGUI>() : null;
                    _badgeGO.SetActive(false);
                    Plugin.Logger.LogInfo($"[PhoneBtn] native badge wired (text={( _badgeText != null ? "yes" : "no")}).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] badge wire: {ex.Message}"); }

            // Size-match the icon to a LIVE sibling — AFTER the cell change and
            // a forced layout pass (the natives' own button script re-fits their
            // icons to the new cell; ours has no script, so we mirror theirs).
            // A delayed re-copy catches any late native adjustment.
            try { LayoutRebuilder.ForceRebuildLayoutImmediate(buttons.TryCast<RectTransform>()); } catch { }
            CopyIconMetricsFromSibling(buttons, go, target);
            _recopyButtons = buttons; _recopyGo = go; _recopyIcon = target;
            _recopyAt = Time.unscaledTime + 0.75f;

            _seenChatVersion = MPChat.Version;   // pre-existing lines aren't "unread"

            // ── Second app: Business Hub (fills the open row-5 slot). ────────
            try { InjectHubButton(buttons, src); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] hub inject: {ex.Message}"); }

            _injected = true;
            Plugin.Logger.LogInfo("[PhoneBtn] Chat button injected.");
        }

        // Hub button state (mirrors the chat button's).
        public static volatile bool HubOpenRequested;
        private static RectTransform? _hubIconRT;
        private static Vector3 _hubIconBaseScale = Vector3.one;
        private static GameObject? _hubBadgeGO;
        private static TMPro.TextMeshProUGUI? _hubBadgeText;
        private static int _seenHubVersion;

        private static void InjectHubButton(Transform buttons, GameObject src)
        {
            const string HubName = "BAMP_HubButton";
            if (buttons.Find(HubName) != null) return;
            var go = UnityEngine.Object.Instantiate(src, buttons);
            go.name = HubName;

            foreach (var c in go.GetComponents(Il2CppType.Of<Component>()))
            {
                if (c == null) continue;
                if (c.GetIl2CppType().Name == "SmartphoneAppButton") UnityEngine.Object.Destroy(c);
            }
            foreach (var c in go.GetComponentsInChildren(Il2CppType.Of<Component>(), true))
            {
                var cc = c.TryCast<Component>();
                if (cc == null) continue;
                if (cc.GetIl2CppType().Name.Contains("Localization"))
                { var b = cc.TryCast<Behaviour>(); if (b != null) b.enabled = false; }
            }
            foreach (var txt in go.GetComponentsInChildren<TMPro.TMP_Text>(true))
                if (txt != null) txt.text = "Business";

            var icon = LoadIconFile("BAMP_HubIcon.png");
            Image? target = null;
            var images = go.GetComponentsInChildren<Image>(true);
            foreach (var img in images)
            {
                if (img == null) continue;
                if (img.gameObject.name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0) { target = img; break; }
                if (target == null || img.rectTransform.rect.width > target.rectTransform.rect.width) target = img;
            }
            if (icon != null && target != null)
            {
                target.sprite = icon;
                target.color  = Color.white;
                target.type   = Image.Type.Simple;
                target.preserveAspect = true;
            }
            foreach (var img in images)
            {
                if (img == null || ReferenceEquals(img, target)) continue;
                img.color = new Color(1f, 1f, 1f, 0f);
            }
            _hubIconRT = target != null ? target.rectTransform : null;

            var btnComp = go.GetComponent(Il2CppType.Of<Button>());
            var btn = btnComp != null ? btnComp.TryCast<Button>() : null;
            if (btn == null) btn = go.AddComponent(Il2CppType.Of<Button>())!.TryCast<Button>();
            if (btn != null)
            {
                btn.onClick = new Button.ButtonClickedEvent();
                btn.onClick.AddListener((UnityAction)(() => { HubOpenRequested = true; }));
                if (target != null) btn.targetGraphic = target;
            }
            go.SetActive(true);

            try
            {
                var badge = go.transform.Find("Badge");
                if (badge != null)
                {
                    _hubBadgeGO = badge.gameObject;
                    foreach (var c in badge.GetComponents(Il2CppType.Of<Component>()))
                    {
                        var cc = c.TryCast<Behaviour>();
                        if (cc != null && cc.GetIl2CppType().Name == "Badge") cc.enabled = false;
                    }
                    var btx = badge.Find("BadgeText");
                    _hubBadgeText = btx != null ? btx.GetComponent<TMPro.TextMeshProUGUI>() : null;
                    _hubBadgeGO.SetActive(false);
                }
            }
            catch { }

            try { LayoutRebuilder.ForceRebuildLayoutImmediate(buttons.TryCast<RectTransform>()); } catch { }
            CopyIconMetricsFromSibling(buttons, go, target);
            _hubRecopyGo = go; _hubRecopyIcon = target;   // late re-fit with the chat one
            _seenHubVersion = MPHub.Version;
            Plugin.Logger.LogInfo("[PhoneBtn] Business Hub button injected.");
        }

        private static GameObject? _hubRecopyGo;
        private static Image? _hubRecopyIcon;

        /// <summary>Pulse + badge for the HUB button while offers are pending
        /// and the hub window is closed.</summary>
        public static void TickHubPulse(bool windowVisible)
        {
            if (_hubIconRT == null) return;
            try
            {
                bool pending = MPHub.PendingCount > 0;
                if (windowVisible) _seenHubVersion = MPHub.Version;
                if (!pending || windowVisible)
                {
                    if (_hubIconRT.localScale != _hubIconBaseScale) _hubIconRT.localScale = _hubIconBaseScale;
                    if (_hubBadgeGO != null && _hubBadgeGO.activeSelf) _hubBadgeGO.SetActive(false);
                    return;
                }
                float s = 1f + 0.06f * (1f + Mathf.Sin(Time.unscaledTime * 5f));
                _hubIconRT.localScale = _hubIconBaseScale * s;
                if (_hubBadgeGO != null)
                {
                    if (!_hubBadgeGO.activeSelf) _hubBadgeGO.SetActive(true);
                    if (_hubBadgeText != null) _hubBadgeText.text = Mathf.Clamp(MPHub.PendingCount, 1, 9).ToString();
                }
            }
            catch { _hubIconRT = null; }
        }

        // ── Phone expansion (CollapsibleWindow-aware) ─────────────────────────

        /// <summary>Grow the phone one cell row.  Container's height is OWNED by
        /// the game's CollapsibleWindow script, so writing the rect alone gets
        /// reverted — locate the script's height value (the float property whose
        /// value equals the live container height) and raise it too.</summary>
        private static bool TryExpandPhone(Transform smartphoneRoot, Transform buttons)
        {
            try
            {
                var container = smartphoneRoot.Find("Container");
                var crt = container != null ? container.TryCast<RectTransform>() : null;
                if (crt == null) return false;
                float liveH = crt.rect.height;

                // CollapsibleWindow stores POSITIONS, not sizes (dump-confirmed:
                // collapse = slide the window down).  Fix its collapsed slide
                // target for the taller body — diagnosed lookup (the first
                // attempt failed SILENTLY: log every property if it's missing).
                foreach (var c in container!.GetComponents(Il2CppType.Of<Component>()))
                {
                    if (c == null) continue;
                    var it = c.GetIl2CppType();
                    if (it.Name != "CollapsibleWindow") continue;
                    var cwType = VehicleManager.FindGameType(it.FullName ?? it.Name);
                    if (cwType == null) { Plugin.Logger.LogWarning("[PhoneBtn] CollapsibleWindow type unresolved."); break; }
                    var cw = Activator.CreateInstance(cwType, c.Pointer);

                    System.Reflection.PropertyInfo? pColl = null;
                    foreach (var p in cwType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (string.Equals(p.Name, "collapsedPosition", StringComparison.OrdinalIgnoreCase)) { pColl = p; break; }

                    if (pColl == null || !pColl.CanWrite)
                    {
                        var names = new System.Text.StringBuilder();
                        foreach (var p in cwType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            names.Append(p.Name).Append(' ');
                        Plugin.Logger.LogWarning($"[PhoneBtn] collapsedPosition NOT settable; available props: {names}");
                    }
                    else
                    {
                        var v = (Vector3)(pColl.GetValue(cw) ?? Vector3.zero);
                        pColl.SetValue(cw, new Vector3(v.x, v.y - RowGrowth, v.z));
                        var rb = (Vector3)(pColl.GetValue(cw) ?? Vector3.zero);
                        Plugin.Logger.LogInfo($"[PhoneBtn] collapsedPosition.y {v.y:F0} → {rb.y:F0} (readback; taller body slides further down).");
                    }

                    // Hover-peek OFF for the phone: with the adjusted collapse
                    // position the native peek-on-hover travels much further,
                    // turning the uncollapse button into a moving target.
                    // Click-to-toggle is unaffected.
                    foreach (var p in cwType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (!string.Equals(p.Name, "hoverFunction", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var cur = p.GetValue(cw);
                            if (p.CanWrite && p.PropertyType.IsEnum)
                            {
                                p.SetValue(cw, Enum.ToObject(p.PropertyType, 0));
                                Plugin.Logger.LogInfo($"[PhoneBtn] hoverFunction {cur} → {p.GetValue(cw)} (peek-on-hover disabled).");
                            }
                            else Plugin.Logger.LogWarning($"[PhoneBtn] hoverFunction not writable/enum (type={p.PropertyType.Name}, value={cur}).");
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] hoverFunction: {ex.Message}"); }
                        break;
                    }
                    break;
                }

                GrowKeepingBottom(container, RowGrowth);
                GrowKeepingBottom(buttons, RowGrowth);

                // Height keeper: the phone has MULTIPLE native heights (1050
                // radio-hidden / 1230 radio-visible) and the game re-applies
                // them on state changes — verified by the +2s probe seeing 1050
                // after we grew from 1230 (and a blind 1455 re-assert produced
                // the user's 180px overshoot).  Track whichever native height
                // the game currently wants and keep it exactly one row taller.
                _keeperRT   = crt;
                _keeperNext = Time.unscaledTime + 0.5f;
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] TryExpandPhone: {ex.Message}"); return false; }
        }

        private const float RowGrowth = 225f;   // one native cell row

        // ── Phone-art discovery (log-only, decides the future grow) ──────────

        /// <summary>Copy the icon child's layout from a live sibling button so
        /// our glyph renders at exactly the native size/position (the template's
        /// internal layout is pre-layout and differs).</summary>
        private static void CopyIconMetricsFromSibling(Transform buttons, GameObject ours, Image? ourIcon)
        {
            try
            {
                if (ourIcon == null) return;
                for (int i = 0; i < buttons.childCount; i++)
                {
                    var sib = buttons.GetChild(i);
                    if (sib == null || sib.gameObject == ours) continue;
                    if (!sib.gameObject.activeSelf || sib.name == "AppButtonTemplate") continue;
                    Transform? sibIconTr = null;
                    foreach (var img in sib.GetComponentsInChildren<Image>(true))
                        if (img != null && img.gameObject.name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0)
                        { sibIconTr = img.transform; break; }
                    if (sibIconTr == null) continue;
                    var srt = sibIconTr.TryCast<RectTransform>();
                    var ort = ourIcon.rectTransform;
                    if (srt == null || ort == null) continue;
                    ort.anchorMin        = srt.anchorMin;
                    ort.anchorMax        = srt.anchorMax;
                    ort.pivot            = srt.pivot;
                    ort.sizeDelta        = srt.sizeDelta;
                    ort.anchoredPosition = srt.anchoredPosition;
                    ort.localScale       = srt.localScale;
                    _iconBaseScale       = srt.localScale;
                    Plugin.Logger.LogInfo($"[PhoneBtn] icon metrics copied from '{sib.name}': sizeDelta=({srt.sizeDelta.x:F0},{srt.sizeDelta.y:F0}) pos=({srt.anchoredPosition.x:F0},{srt.anchoredPosition.y:F0}) scale={srt.localScale.x:F2} anchors=[{srt.anchorMin.x:F2},{srt.anchorMin.y:F2}]-[{srt.anchorMax.x:F2},{srt.anchorMax.y:F2}]");
                    return;
                }
                Plugin.Logger.LogWarning("[PhoneBtn] no live sibling icon found to copy metrics from.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] CopyIconMetrics: {ex.Message}"); }
        }

        /// <summary>Logs every graphic in the phone's screen region plus the
        /// Smartphone subtree's components — pins down where the visible phone
        /// body art actually lives (it is NOT in the Smartphone canvas: growing
        /// those rects moved buttons but never the art, and the geometry-match
        /// attempt found only look-alike city-map panels).</summary>
        private static void DeepDumpPhoneArt(Transform smartphoneRoot)
        {
            try
            {
                Plugin.Logger.LogInfo("[PhoneDump] ── Smartphone subtree (with components) ──");
                DumpGraphicsTree(smartphoneRoot, 0, 6);

                var container = smartphoneRoot.Find("Container");
                var crt = container != null ? container.TryCast<RectTransform>() : null;
                if (crt == null) return;
                GetWorldBounds(crt, out float cL, out float cB, out float cW, out float cH);
                float cR = cL + cW, cT = cB + cH, cArea = cW * cH;
                Plugin.Logger.LogInfo($"[PhoneDump] container world: L={cL:F0} B={cB:F0} {cW:F0}x{cH:F0}");

                // EVERY Graphic in the scene that covers ≥40% of the phone region.
                var graphics = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Graphic>(), true);
                int logged = 0;
                for (int i = 0; i < graphics.Length && logged < 25; i++)
                {
                    var gr = graphics[i].TryCast<Graphic>();
                    if (gr == null) continue;
                    var rt = gr.rectTransform;
                    if (rt == null || IsDescendantOf(rt, smartphoneRoot)) continue;
                    GetWorldBounds(rt, out float l, out float b, out float w, out float h);
                    float ix = Mathf.Min(cR, l + w) - Mathf.Max(cL, l);
                    float iy = Mathf.Min(cT, b + h) - Mathf.Max(cB, b);
                    if (ix <= 0f || iy <= 0f || ix * iy < cArea * 0.4f) continue;
                    string detail = gr.GetIl2CppType().Name;
                    var img = gr.TryCast<Image>();
                    if (img != null) detail += $" sprite='{img.sprite?.name}' type={img.type}";
                    var raw = gr.TryCast<RawImage>();
                    if (raw != null) detail += $" texture='{raw.texture?.name}'";
                    Plugin.Logger.LogInfo($"[PhoneDump] overlap: '{PathOf(rt)}' {detail} world={w:F0}x{h:F0} bottom={b:F0} active={gr.gameObject.activeInHierarchy}");
                    logged++;
                }
                Plugin.Logger.LogInfo($"[PhoneDump] ── end ({logged} overlapping graphic(s)) ──");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneDump] {ex.Message}"); }
        }

        private static void DumpGraphicsTree(Transform tr, int depth, int maxDepth)
        {
            if (tr == null || depth > maxDepth) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                var comps = tr.GetComponents(Il2CppType.Of<Component>());
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    string cn = c.GetIl2CppType().Name;
                    if (cn is "Transform" or "RectTransform" or "CanvasRenderer") continue;
                    var img = c.TryCast<Image>();
                    if (img != null) { sb.Append($"Image(sprite='{img.sprite?.name}',{img.type}) "); continue; }
                    var raw = c.TryCast<RawImage>();
                    if (raw != null) { sb.Append($"RawImage(tex='{raw.texture?.name}') "); continue; }
                    sb.Append(cn).Append(' ');
                }
                var rt = tr.TryCast<RectTransform>();
                string size = rt != null ? $"[{rt.rect.width:F0}x{rt.rect.height:F0}]" : "";
                Plugin.Logger.LogInfo($"[PhoneDump] {new string(' ', depth * 2)}{tr.name}{size} ({(tr.gameObject.activeSelf ? "on" : "OFF")}) {sb}");
                for (int i = 0; i < tr.childCount && i < 16; i++)
                    DumpGraphicsTree(tr.GetChild(i), depth + 1, maxDepth);
            }
            catch { }
        }

        /// <summary>Axis-aligned world bounds (UI canvases aren't rotated).</summary>
        private static void GetWorldBounds(RectTransform rt, out float left, out float bottom, out float w, out float h)
        {
            var r = rt.rect; var s = rt.lossyScale; var p = rt.position;
            w = r.width * s.x; h = r.height * s.y;
            left = p.x - rt.pivot.x * w;
            bottom = p.y - rt.pivot.y * h;
        }

        private static bool IsDescendantOf(Transform t, Transform ancestor)
        {
            int guard = 0;
            for (var p = t; p != null && guard++ < 24; p = p.parent)
                if (p == ancestor) return true;
            return false;
        }

        private static void GrowKeepingBottom(Transform? tr, float extra)
        {
            if (tr == null) return;
            try
            {
                var rt = tr.TryCast<RectTransform>();
                if (rt == null) return;
                Plugin.Logger.LogInfo($"[PhoneBtn] grow '{tr.name}' +{extra:F0}: size={rt.rect.width:F0}x{rt.rect.height:F0} sizeDelta=({rt.sizeDelta.x:F0},{rt.sizeDelta.y:F0}) pivot=({rt.pivot.x:F2},{rt.pivot.y:F2}) anchorsY=[{rt.anchorMin.y:F2},{rt.anchorMax.y:F2}]");
                rt.sizeDelta        = new Vector2(rt.sizeDelta.x, rt.sizeDelta.y + extra);
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, rt.anchoredPosition.y + extra * rt.pivot.y);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] grow '{tr.name}': {ex.Message}"); }
        }

        private static string PathOf(Transform tr)
        {
            try
            {
                var sb = new System.Text.StringBuilder(tr.name);
                var p = tr.parent; int guard = 0;
                while (p != null && guard++ < 12) { sb.Insert(0, p.name + "/"); p = p.parent; }
                return sb.ToString();
            }
            catch { return tr.name; }
        }

        /// <summary>Load any icon PNG from the plugins folder (owned texture).</summary>
        internal static Sprite? LoadIconFile(string file)
        {
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath, file);
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Logger.LogWarning($"[PhoneBtn] icon not found: {path}");
                    return null;
                }
                var bytes = System.IO.File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                tex.hideFlags = HideFlags.HideAndDontSave;
                if (!ImageConversion.LoadImage(tex, (Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>)bytes))
                { Plugin.Logger.LogWarning($"[PhoneBtn] icon decode failed: {file}"); return null; }
                var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                sp.hideFlags = HideFlags.HideAndDontSave;
                return sp;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] LoadIconFile({file}): {ex.Message}"); return null; }
        }

        private static Sprite? LoadIcon()
        {
            if (_iconSprite != null) return _iconSprite;
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath, IconFile);
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Logger.LogWarning($"[PhoneBtn] icon not found: {path}");
                    return null;
                }
                var bytes = System.IO.File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2);
                tex.hideFlags = HideFlags.HideAndDontSave;
                if (!ImageConversion.LoadImage(tex, (Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>)bytes))
                { Plugin.Logger.LogWarning("[PhoneBtn] icon decode failed."); return null; }
                _iconSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                _iconSprite.hideFlags = HideFlags.HideAndDontSave;
                Plugin.Logger.LogInfo($"[PhoneBtn] icon loaded ({tex.width}x{tex.height}, {bytes.Length}B).");
                return _iconSprite;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[PhoneBtn] LoadIcon: {ex.Message}"); return null; }
        }
    }
}
