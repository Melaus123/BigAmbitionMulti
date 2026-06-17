using TMPro;
using PlayerActivity;
using UI.ItemPanel;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// The passenger's in-ride UI: the native in-car buttons (cloned from ItemPanelUI for the
    /// matching look) — Park relabelled "Exit Vehicle", plus Sleep — with no Sell and no Lock.
    /// Shown only once we're actually seated. Sleep is always available (you can sleep while
    /// someone else drives) and runs the vehicle's captured SleepEnvironment. The native panel
    /// itself can't be reused (it's bound to GameManager.selectedVehicle + a live VehicleController),
    /// so this is a stand-in that borrows its buttons. Ticked from MPCanvasUI.Update. A small
    /// transient toast doubles as the "Vehicle locked" feedback. See docs/PASSENGER-SYSTEM.md.
    /// </summary>
    internal static class PassengerHud
    {
        /// <summary>Live ItemPanelUI, cached by a Start postfix — source of the cloned buttons.</summary>
        internal static ItemPanelUI? NativePanel;

        private static GameObject? _canvas;
        private static GameObject? _buttonPanel;     // bare fallback container
        private static GameObject? _clonedPanel;     // preferred: a clone of the driver's ItemPanelUI panel
        private static GameObject? _toast;
        private static TextMeshProUGUI? _toastLabel;
        private static float _toastUntil;
        private static bool _baseBuilt;
        private static bool _buttonsBuilt;

        public static void Tick()
        {
            if (!_baseBuilt && (MPServer.IsRunning || MPClient.IsConnected)) BuildBase();
            if (_canvas == null) return;

            bool seated = PassengerRide.IsSeated;
            // Build the buttons the FIRST time we're seated — by then we're deep in-game and the
            // ItemPanelUI.Start postfix has cached NativePanel, so the native clone actually works.
            // (Building eagerly on the first MP frame ran before the HUD existed → ugly fallback.)
            if (seated && !_buttonsBuilt) BuildButtons();
            var panel = _clonedPanel != null ? _clonedPanel : _buttonPanel;   // cloned driver panel if we got one
            if (panel != null && panel.activeSelf != seated) panel.SetActive(seated);

            bool toastOn = Time.unscaledTime < _toastUntil;
            if (_toast != null && _toast.activeSelf != toastOn) _toast.SetActive(toastOn);
        }

        /// <summary>Toast a host board-rejection reason ("vehicle full" → "Vehicle full.").</summary>
        public static void ToastReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            Toast(char.ToUpper(reason[0]) + reason.Substring(1) + ".");
        }

        /// <summary>Briefly flash a centred message (e.g. "Vehicle locked.").</summary>
        public static void Toast(string msg)
        {
            if (!_baseBuilt) BuildBase();
            if (_toastLabel != null) _toastLabel.text = msg;
            _toastUntil = Time.unscaledTime + 2f;
            if (_toast != null) _toast.SetActive(true);
        }

        private static void OnSleep()
        {
            try
            {
                var env = VehicleManager.SleepEnvironmentFor(PassengerSync.LocalRidingVehicleId);
                if (env != null) PlayerActivityUI.Show(env);   // null entity: Car sleep is null-safe
                else { Plugin.Logger.LogWarning("[PassengerHud] no sleep environment for the ridden vehicle."); Toast("Can't sleep here."); }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PassengerHud] sleep: {ex.Message}"); }
        }

        private static void BuildBase()
        {
            _baseBuilt = true;   // even on failure — don't retry a broken build every frame
            try
            {
                var canvasGO = new GameObject("BAMP_PassengerHud");
                UnityEngine.Object.DontDestroyOnLoad(canvasGO);
                var canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 5000;
                var scaler = canvasGO.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                canvasGO.AddComponent<GraphicRaycaster>();
                _canvas = canvasGO;

                _buttonPanel = new GameObject("Buttons");
                _buttonPanel.transform.SetParent(canvasGO.transform, false);
                var prt = _buttonPanel.AddComponent<RectTransform>();
                prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0f);   // bottom-centre
                prt.anchoredPosition = new Vector2(0f, 24f);
                prt.sizeDelta = new Vector2(300f, 200f);
                // Panel background — so this reads like the driver's bottom panel, not just floating
                // buttons. Fallback tint here; the native ItemPanelUI sprite is copied over in
                // BuildButtons once NativePanel is cached (C: visual match to the driver's panel).
                var panelBg = _buttonPanel.AddComponent<Image>();
                panelBg.color = new Color(0f, 0f, 0f, 0.55f);
                _buttonPanel.SetActive(false);

                _toast = BuildToast(canvasGO.transform);
                _toast.SetActive(false);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PassengerHud] base build: {ex.Message}"); }
        }

        private static void BuildButtons()
        {
            _buttonsBuilt = true;
            try
            {
                if (_buttonPanel == null) return;
#if BAMP_DEV
                // DIAG: dump the native panel's immediate children so we can match its header/body/button
                //   proportions precisely if this still looks off.
                if (NativePanel != null && NativePanel.panel != null)
                {
                    var p = NativePanel.panel.transform;
                    var dbg = new System.Text.StringBuilder();
                    for (int i = 0; i < p.childCount; i++)
                    {
                        var ch = p.GetChild(i);
                        var crt = ch.GetComponent<RectTransform>();
                        dbg.Append($"{ch.name}(act={ch.gameObject.activeSelf}{(crt != null ? $",size={crt.sizeDelta}" : "")}) ");
                    }
                    Plugin.Logger.LogInfo($"[HudDiag] native panel children: {dbg}");
                }
#endif
                // 2026-06-16: build our OWN panel — do NOT clone the native ItemPanelUI (its
                // VerticalLayoutGroup/ContentSizeFitter fought every edit). But size + position it to MIRROR
                // the driver's: a sizeable panel sitting up off the screen bottom, with a titled HEADER at the
                // top and the buttons along the bottom. We borrow the native background sprite for the look.
                var prt = _buttonPanel.GetComponent<RectTransform>();
                if (prt != null)
                {
                    prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0f);   // bottom-centre
                    // Keep the TOP edge where it was (≈400 up from the bottom) but raise the BOTTOM: shrink
                    // height 300→150 and lift the anchor 100→250 (250 + 150 = 400, same top). Buttons move up
                    // under the header (FillRow) and the bottom edge comes up to nearly meet them.
                    prt.sizeDelta        = new Vector2(1000f, 150f);
                    prt.anchoredPosition = new Vector2(0f, 150f);   // lowered to sit just above the FPS / street-name HUD text
                }
                // Borrow the native panel's background sprite for an identical look (else a dark tint).
                var bg = _buttonPanel.GetComponent<Image>();
                if (bg != null)
                {
                    Image? srcImg = null;
                    if (NativePanel != null && NativePanel.panel != null)
                        srcImg = NativePanel.panel.GetComponent<Image>() ?? NativePanel.panel.GetComponentInChildren<Image>(true);
                    if (srcImg != null && srcImg.sprite != null) { bg.sprite = srcImg.sprite; bg.type = srcImg.type; bg.color = srcImg.color; }
                    else bg.color = new Color(0.10f, 0.10f, 0.12f, 0.92f);
                }

                BuildHeader(_buttonPanel.transform, "Vehicle");

                // Two buttons along the bottom, each filling its half — we place them ourselves.
                bool built = false;
                if (NativePanel != null && NativePanel.parkButton != null)
                {
                    var exit  = CloneNativeButton(_buttonPanel.transform, NativePanel.parkButton,  "Exit Vehicle",
                                                  new UnityEngine.Events.UnityAction(PassengerRide.RequestExit), Vector2.zero);
                    var sleep = CloneNativeButton(_buttonPanel.transform, NativePanel.sleepButton, "Sleep",
                                                  new UnityEngine.Events.UnityAction(OnSleep), Vector2.zero);
                    FillRow(sleep, 0.05f, 0.49f);   // Sleep on the LEFT
                    FillRow(exit,  0.51f, 0.95f);   // Exit Vehicle on the RIGHT (swapped 2026-06-16)
                    built = exit != null;
                }
                if (!built)
                {
                    BuildPlainExit(_buttonPanel.transform);
                    Plugin.Logger.LogWarning("[PassengerHud] native panel not cached at seat time — plain Exit button used.");
                }
                Plugin.Logger.LogInfo("[PassengerHud] built fresh passenger panel (header + Exit + Sleep).");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PassengerHud] button build: {ex.Message}"); }
        }

        // Place a button across aMinX..aMaxX of the width, in the BOTTOM band of the panel (our own
        // layout — no LayoutGroup involved).
        private static void FillRow(GameObject? go, float aMinX, float aMaxX)
        {
            if (go == null) return;
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = new Vector2(aMinX, 0.12f);   // sit just under the header, near the top of the shrunk bar
            rt.anchorMax = new Vector2(aMaxX, 0.58f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // A header strip across the TOP of the panel with a centred title — so the passenger panel reads
        // like the driver's (which has a titled header) rather than a bare button bar.
        private static void BuildHeader(Transform panel, string title)
        {
            var h = new GameObject("Header");
            h.transform.SetParent(panel, false);
            var rt = h.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, 60f);   // full width, 60 tall, pinned to the top edge
            rt.anchoredPosition = Vector2.zero;
            var img = h.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.35f);
            AddLabel(h.transform, title, 30f);
        }

        // C: clone the driver's actual ItemPanelUI panel so the passenger HUD has the SAME dark
        //   background, button styling and layout — then strip it to the two passenger buttons (Exit =
        //   the Park slot, Sleep), hiding Lock/Sell/etc. and the live-data row (which needs a live
        //   VehicleController we don't have on a ghost — it would show stale data or NRE). Returns null
        //   on any failure so BuildButtons falls back to bare buttons.
        private static GameObject? CloneDriverPanel()
        {
            try
            {
                var np = NativePanel;
                if (np == null || np.panel == null || _canvas == null)
                {
                    Plugin.Logger.LogWarning($"[PassengerHud] clone skipped: NativePanel={(np != null)} panel={(np != null && np.panel != null)} canvas={(_canvas != null)}");
                    return null;
                }
#if BAMP_DEV
                // DIAG: log the NATIVE panel's layout so we can match its position (it's sitting too low/
                //   overlapping text) and see whether a LayoutGroup controls the buttons (= why the stretch
                //   anchor change had no effect).
                try
                {
                    var nrt = np.panel.GetComponent<RectTransform>();
                    var nlg = np.panel.GetComponentInChildren<UnityEngine.UI.LayoutGroup>(true);
                    Plugin.Logger.LogInfo($"[HudDiag] native panel aMin={(nrt != null ? nrt.anchorMin.ToString() : "?")} aMax={(nrt != null ? nrt.anchorMax.ToString() : "?")} pivot={(nrt != null ? nrt.pivot.ToString() : "?")} pos={(nrt != null ? nrt.anchoredPosition.ToString() : "?")} size={(nrt != null ? nrt.sizeDelta.ToString() : "?")} layout={(nlg != null ? nlg.GetType().Name : "none")} parkParent={(np.parkButton != null ? np.parkButton.transform.parent.name : "?")}");
                }
                catch { }
#endif

                // Clone the ItemPanelUI's OWN GameObject so the clone definitely carries the ItemPanelUI
                // component (+ its button refs remapped to the cloned children). The previous attempt cloned
                // np.panel.gameObject — likely a CHILD with no ItemPanelUI → GetComponent null → silent fallback.
                var clone = UnityEngine.Object.Instantiate(np.gameObject);
                clone.SetActive(false);   // keep it inert while we strip it down
                var ip = clone.GetComponentInChildren<ItemPanelUI>(true);
                if (ip == null)
                {
                    Plugin.Logger.LogWarning("[PassengerHud] clone has no ItemPanelUI — falling back to bare buttons.");
                    UnityEngine.Object.Destroy(clone); return null;
                }
                Button? exitBtn = ip.parkButton, sleepBtn = ip.sleepButton;
                if (exitBtn == null)
                {
                    Plugin.Logger.LogWarning("[PassengerHud] cloned panel has no parkButton — falling back.");
                    UnityEngine.Object.Destroy(clone); return null;
                }

                // The visual container we keep (RectTransform + dark background + the buttons under it).
                var visual = ip.panel != null ? ip.panel.gameObject : clone;

                // Hide the live-data sub-views (need a real VehicleController → would show stale/NRE on a ghost).
                if (ip.vehicleInfo != null)     ip.vehicleInfo.gameObject.SetActive(false);
                if (ip.metaInfo != null)        ip.metaInfo.gameObject.SetActive(false);
                if (ip.maintenanceInfo != null) ip.maintenanceInfo.gameObject.SetActive(false);
                if (ip.cargoContainer != null)  ip.cargoContainer.SetActive(false);
                if (ip.itemNameLabel != null)   ip.itemNameLabel.gameObject.SetActive(false);

                // Hide every button except Exit (Park) + Sleep — drops Sell, Auto-park, Grab, Discard,
                // Leave, Place AND the mod's cloned Lock button in one pass.
                foreach (var b in clone.GetComponentsInChildren<Button>(true))
                {
                    if (b == null || b == exitBtn || b == sleepBtn) continue;
                    b.gameObject.SetActive(false);
                }
                // Disable the binding scripts so they don't drive/NRE off a vehicle we don't have.
                foreach (var comp in clone.GetComponentsInChildren(typeof(Component), true))
                {
                    if (comp == null) continue;
                    var n = comp.GetType().Name;
                    if ((n == "ItemPanelUI" || n == "VehicleInfoPanel") && comp is Behaviour beh) beh.enabled = false;
                }
                WireClonedButton(exitBtn,  "Exit Vehicle", new UnityEngine.Events.UnityAction(PassengerRide.RequestExit));
                WireClonedButton(sleepBtn, "Sleep",        new UnityEngine.Events.UnityAction(OnSleep));
                // TAKE CONTROL OF LAYOUT (2026-06-16): the native panel has a VerticalLayoutGroup (and may
                // have a ContentSizeFitter) that re-flows its children EVERY FRAME — that is why earlier
                // anchor/position edits "did nothing": the layout overwrote them. Disable both, then place
                // the panel and the two buttons ourselves.
                foreach (var lg  in visual.GetComponentsInChildren<UnityEngine.UI.LayoutGroup>(true))       lg.enabled  = false;
                foreach (var fit in visual.GetComponentsInChildren<UnityEngine.UI.ContentSizeFitter>(true)) fit.enabled = false;

                // Reparent onto our overlay canvas and size it to a COMPACT bottom bar. The native panel is
                // ~459 px tall (full of driver info we hide), which left a big empty dark box overlapping
                // other HUD text — that was the "too low / blocks text" problem.
                visual.transform.SetParent(_canvas.transform, false);
                var rt = visual.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f);   // bottom-centre
                    rt.sizeDelta        = new Vector2(900f, 130f);
                    rt.anchoredPosition = new Vector2(0f, 28f);
                }
                // Lay the two buttons out ourselves — reparented straight onto the panel, side by side,
                // each filling its half of the width (with a small gap), so they fill the bar.
                FillButton(sleepBtn, visual.transform, 0.05f, 0.49f);
                FillButton(exitBtn,  visual.transform, 0.51f, 0.95f);

                var cg = visual.GetComponent<CanvasGroup>();
                if (cg != null) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; }
                visual.SetActive(true);
                if (visual != clone) UnityEngine.Object.Destroy(clone);
                Plugin.Logger.LogInfo("[PassengerHud] cloned the driver panel (compact bar, Exit + Sleep).");
                return visual;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PassengerHud] clone driver panel: {ex.Message}"); return null; }
        }

        private static void WireClonedButton(Button? btn, string label, UnityEngine.Events.UnityAction onClick)
        {
            if (btn == null) return;
            btn.gameObject.SetActive(true);
            btn.onClick = new Button.ButtonClickedEvent();   // drop persistent + runtime listeners (ANTIPATTERNS class 6)
            btn.onClick.AddListener(onClick);
            btn.interactable = true; btn.enabled = true;
            foreach (var comp in btn.GetComponentsInChildren(typeof(Component), true))
            {
                if (comp == null) continue;
                var n = comp.GetType().Name;
                if (n.Contains("Localization") || n.Contains("LanguageChange")) UnityEngine.Object.Destroy(comp);
            }
            var tmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = label;
        }

        // Reparent a button onto `parent` and anchor-stretch it to fill aMinX..aMaxX of the width and a
        // centred vertical band — used to lay the two passenger buttons across the compact panel ourselves
        // (only works because we disabled the panel's LayoutGroup first).
        private static void FillButton(Button? b, Transform parent, float aMinX, float aMaxX)
        {
            if (b == null) return;
            b.transform.SetParent(parent, false);
            var rt = b.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = new Vector2(aMinX, 0.18f);
            rt.anchorMax = new Vector2(aMaxX, 0.82f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static GameObject? CloneNativeButton(Transform parent, Button src, string label,
                                                     UnityEngine.Events.UnityAction onClick, Vector2 anchoredPos)
        {
            if (src == null) return null;
            var clone = UnityEngine.Object.Instantiate(src.gameObject, parent);
            clone.SetActive(true);

            var rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f);   // bottom-centre
                rt.anchoredPosition = anchoredPos;
            }

            var btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick = new Button.ButtonClickedEvent();   // drop inherited persistent listeners (ANTIPATTERNS class 6)
                btn.onClick.AddListener(onClick);
                btn.interactable = true;
                btn.enabled = true;
            }

            // Strip any localization driver so our label sticks; then set it directly.
            foreach (var comp in clone.GetComponentsInChildren(typeof(Component), true))
            {
                if (comp == null) continue;
                var n = comp.GetType().Name;
                if (n.Contains("Localization") || n.Contains("LanguageChange")) UnityEngine.Object.Destroy(comp);
            }
            var tmp = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = label;
            return clone;
        }

        private static void BuildPlainExit(Transform parent)
        {
            var btnGO = new GameObject("ExitButton");
            btnGO.transform.SetParent(parent, false);
            var img = btnGO.AddComponent<Image>();
            img.color = new Color(0.12f, 0.12f, 0.14f, 0.92f);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f);   // bottom-centre
            rt.anchoredPosition = new Vector2(0f, 40f);
            rt.sizeDelta = new Vector2(240f, 56f);
            var btn = btnGO.AddComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(new UnityEngine.Events.UnityAction(PassengerRide.RequestExit));
            AddLabel(btnGO.transform, "Exit Vehicle", 24f);
        }

        private static GameObject BuildToast(Transform parent)
        {
            var toastGO = new GameObject("Toast");
            toastGO.transform.SetParent(parent, false);
            var img = toastGO.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.78f);
            var rt = toastGO.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, 160f);
            rt.sizeDelta = new Vector2(360f, 56f);
            _toastLabel = AddLabel(toastGO.transform, "", 26f);
            return toastGO;
        }

        private static TextMeshProUGUI AddLabel(Transform parent, string text, float size)
        {
            var txtGO = new GameObject("Label");
            txtGO.transform.SetParent(parent, false);
            var tmp = txtGO.AddComponent<TextMeshProUGUI>();
            tmp.text      = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize  = size;
            tmp.color     = Color.white;
            var lrt = txtGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            return tmp;
        }
    }
}
