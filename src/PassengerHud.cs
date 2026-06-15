using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// The passenger's in-ride UI — the equivalent of the driver's in-car menu, minus the actions
    /// a passenger can't take (no Sell, no driving). For now it's the core "Exit Vehicle" action
    /// (Park's passenger analogue); Sleep can be added once CanSleep() is resolvable for a ghost.
    /// Shown only while the LOCAL player is riding. Built once, then toggled. Ticked each frame
    /// from MPCanvasUI.Update. See docs/PASSENGER-SYSTEM.md.
    /// </summary>
    internal static class PassengerHud
    {
        private static GameObject? _canvas;   // whole screen-space canvas (toggled)
        private static bool _built;

        public static void Tick()
        {
            bool riding = !string.IsNullOrEmpty(PassengerSync.LocalRidingVehicleId);
            if (!riding)
            {
                if (_canvas != null) _canvas.SetActive(false);
                return;
            }
            if (!_built) Build();
            if (_canvas != null) _canvas.SetActive(true);
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

                // "Exit Vehicle" button, bottom-centre.
                var btnGO = new GameObject("ExitButton");
                btnGO.transform.SetParent(canvasGO.transform, false);
                var img = btnGO.AddComponent<Image>();
                img.color = new Color(0.12f, 0.12f, 0.14f, 0.92f);
                var rt = btnGO.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, 90f);
                rt.sizeDelta = new Vector2(240f, 60f);

                var btn = btnGO.AddComponent<Button>();
                btn.onClick = new Button.ButtonClickedEvent();
                btn.onClick.AddListener(new UnityEngine.Events.UnityAction(PassengerRide.RequestExit));

                var txtGO = new GameObject("Label");
                txtGO.transform.SetParent(btnGO.transform, false);
                var tmp = txtGO.AddComponent<TextMeshProUGUI>();
                tmp.text      = "Exit Vehicle";
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize  = 24f;
                tmp.color     = Color.white;
                var lrt = txtGO.GetComponent<RectTransform>();
                lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
                lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[PassengerHud] build: {ex.Message}"); }
        }
    }
}
