using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// The passenger's in-ride UI. For now the core "Exit Vehicle" action (shown only once we're
    /// actually seated, not on board-approval) plus a transient toast used for "Vehicle locked".
    /// The driver's native ItemPanelUI can't be reused for a passenger — it's hard-wired to
    /// GameManager.selectedVehicle + a live VehicleController, neither of which a passenger riding
    /// a ghost has — so this is a lightweight stand-in. Built once, lazily; ticked from
    /// MPCanvasUI.Update. See docs/PASSENGER-SYSTEM.md.
    /// </summary>
    internal static class PassengerHud
    {
        private static GameObject? _canvas;
        private static GameObject? _exitBtn;
        private static GameObject? _toast;
        private static TextMeshProUGUI? _toastLabel;
        private static float _toastUntil;
        private static bool _built;

        public static void Tick()
        {
            if (!_built && (MPServer.IsRunning || MPClient.IsConnected)) Build();
            if (_canvas == null) return;

            bool seated = PassengerRide.IsSeated;
            if (_exitBtn != null && _exitBtn.activeSelf != seated) _exitBtn.SetActive(seated);

            bool toastOn = Time.unscaledTime < _toastUntil;
            if (_toast != null && _toast.activeSelf != toastOn) _toast.SetActive(toastOn);
        }

        /// <summary>Briefly flash a centred message (e.g. "Vehicle locked.").</summary>
        public static void Toast(string msg)
        {
            if (!_built) Build();
            if (_toastLabel != null) _toastLabel.text = msg;
            _toastUntil = Time.unscaledTime + 2f;
            if (_toast != null) _toast.SetActive(true);
        }

        private static void Build()
        {
            _built = true;   // even on failure — don't retry a broken build every frame
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

                _exitBtn = BuildExitButton(canvasGO.transform);
                _exitBtn.SetActive(false);

                _toast = BuildToast(canvasGO.transform);
                _toast.SetActive(false);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PassengerHud] build: {ex.Message}"); }
        }

        private static GameObject BuildExitButton(Transform parent)
        {
            var btnGO = new GameObject("ExitButton");
            btnGO.transform.SetParent(parent, false);
            var img = btnGO.AddComponent<Image>();
            img.color = new Color(0.12f, 0.12f, 0.14f, 0.92f);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 90f);
            rt.sizeDelta = new Vector2(240f, 60f);

            var btn = btnGO.AddComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(new UnityEngine.Events.UnityAction(PassengerRide.RequestExit));

            AddLabel(btnGO.transform, "Exit Vehicle", 24f);
            return btnGO;
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
