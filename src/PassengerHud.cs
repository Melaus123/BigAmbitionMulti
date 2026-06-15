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
        private static GameObject? _buttonPanel;
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
            if (_buttonPanel != null && _buttonPanel.activeSelf != seated) _buttonPanel.SetActive(seated);

            bool toastOn = Time.unscaledTime < _toastUntil;
            if (_toast != null && _toast.activeSelf != toastOn) _toast.SetActive(toastOn);
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
                prt.anchoredPosition = Vector2.zero;
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
                bool cloned = false;
                if (NativePanel != null)
                {
                    var exit  = CloneNativeButton(_buttonPanel.transform, NativePanel.parkButton,  "Exit Vehicle",
                                                  new UnityEngine.Events.UnityAction(PassengerRide.RequestExit), new Vector2(0f, 40f));
                    var sleep = CloneNativeButton(_buttonPanel.transform, NativePanel.sleepButton, "Sleep",
                                                  new UnityEngine.Events.UnityAction(OnSleep), new Vector2(0f, 112f));
                    cloned = exit != null;
                }
                if (!cloned)
                {
                    BuildPlainExit(_buttonPanel.transform);
                    Plugin.Logger.LogWarning("[PassengerHud] native panel not cached at seat time — plain Exit button used.");
                }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PassengerHud] button build: {ex.Message}"); }
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
