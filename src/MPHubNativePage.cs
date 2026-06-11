using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// "Business" as a NATIVE full-menu app (2026-06-10, probe-grounded):
    /// the game's full-screen interface is UI.Smartphone.FullMenu —
    ///   Canvas/Topbar/AppButtons   ← FullMenuAppButton row (has an INACTIVE
    ///                                 AppButtonTemplate, same trick as the phone)
    ///   Canvas/Topbar/AppName      ← big page title (localized)
    ///   Canvas/AppsContainer      ← one child per app page (3840x1750)
    /// We clone the template into the row ("Business" + briefcase logo) and add
    /// our own page as an AppsContainer sibling — the shell (background, top
    /// bar, bottom bar) is the native one BY CONSTRUCTION.  A Harmony postfix
    /// on FullMenu.ShowApp hides our page when a native app is selected.
    /// </summary>
    public static class MPHubNativePage
    {
        public static bool Ready { get; private set; }
        public static bool PageActive => _page != null && _page.activeSelf;
        /// <summary>Content host for MPCanvasUI's hub builder: 1920x875 at
        /// scale 2 → fills the 3840x1750 page area with native-shell margins.</summary>
        public static Transform? ContentRoot { get; private set; }
        /// <summary>Camera for hit-testing inside the FullMenu canvas (null =
        /// overlay).</summary>
        public static Camera? UiCamera { get; private set; }
        /// <summary>Set by our top-row button's click; consumed by MPCanvasUI.</summary>
        public static volatile bool OpenRequested;

        private static Component? _menu;
        private static Type? _menuType;
        private static GameObject? _page;
        private static Transform? _appsContainer;
        private static GameObject? _button;
        private static GameObject? _selectedIcon;
        private static TextMeshProUGUI? _appNameLbl;
        private static Behaviour? _appNameLoc;        // native localization on AppName
        private static float _nextTryAt;
        private static int _tries;

        public static void Reset()
        {
            Ready = false;
            _menu = null; _page = null; _appsContainer = null; _button = null;
            _selectedIcon = null; _appNameLbl = null; _appNameLoc = null;
            ContentRoot = null; UiCamera = null;
            _tries = 0; _nextTryAt = 0f;
            OpenRequested = false;
        }

        public static void Tick()
        {
            try
            {
                if (!Ready) { TryInject(); return; }
                if (_page == null || _appsContainer == null) { Reset(); return; }   // scene died

                if (PageActive)
                {
                    // Hold the top title while our page is up (the native
                    // localization would overwrite it).
                    if (_appNameLoc != null && _appNameLoc.enabled) _appNameLoc.enabled = false;
                    if (_appNameLbl != null && _appNameLbl.text != "Business") _appNameLbl.text = "Business";
                    if (_selectedIcon != null && !_selectedIcon.activeSelf) _selectedIcon.SetActive(true);
                }
            }
            catch { }
        }

        private static void TryInject()
        {
            if (_tries >= 30 || Time.unscaledTime < _nextTryAt) return;
            _nextTryAt = Time.unscaledTime + 2f;
            _tries++;
            try
            {
                _menuType ??= VehicleManager.FindGameType("UI.Smartphone.FullMenu")
                           ?? VehicleManager.FindGameType("FullMenu");
                if (_menuType == null) return;
                if (_menu == null)
                {
                    var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_menuType), true);
                    if (arr == null || arr.Length == 0) return;
                    _menu = arr[0].TryCast<Component>();
                    if (_menu == null) return;
                }

                var canvasT = _menu.transform.Find("Canvas");
                var row = canvasT?.Find("Topbar/AppButtons");
                _appsContainer = canvasT?.Find("AppsContainer");
                var template = row?.Find("AppButtonTemplate");
                if (canvasT == null || row == null || _appsContainer == null || template == null)
                {
                    Plugin.Logger.LogWarning("[HubApp] FullMenu paths not found (canvas/row/container/template).");
                    return;
                }

                // Hit-test camera (the canvas may be camera-space).
                var canvas = canvasT.GetComponent<Canvas>();
                UiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

                // Top title label + its localization component.
                var appName = canvasT.Find("Topbar/AppName");
                _appNameLbl = appName != null ? appName.GetComponent<TextMeshProUGUI>() : null;
                if (appName != null)
                    foreach (var c in appName.GetComponents(Il2CppType.Of<Component>()))
                    {
                        var cc = c.TryCast<Behaviour>();
                        if (cc != null && cc.GetIl2CppType().Name.Contains("Localization")) { _appNameLoc = cc; break; }
                    }

                // ── Top-row button: clone the game's own template. ────────────
                _button = UnityEngine.Object.Instantiate(template.gameObject, row);
                _button.name = "BAMP_Business";
                foreach (var c in _button.GetComponents(Il2CppType.Of<Component>()))
                {
                    if (c == null) continue;
                    if (c.GetIl2CppType().Name == "FullMenuAppButton") UnityEngine.Object.Destroy(c);
                }
                foreach (var c in _button.GetComponentsInChildren(Il2CppType.Of<Component>(), true))
                {
                    var cc = c.TryCast<Behaviour>();
                    if (cc != null && cc.GetIl2CppType().Name.Contains("Localization")) cc.enabled = false;
                }
                var titleT = _button.transform.Find("Title");
                var titleLbl = titleT != null ? titleT.GetComponent<TextMeshProUGUI>() : null;
                if (titleLbl != null) titleLbl.text = "Business";
                var logoT = _button.transform.Find("Logo");
                var logoImg = logoT != null ? logoT.GetComponent<Image>() : null;
                var icon = MPPhoneButton.LoadIconFile("BAMP_HubIcon.png");
                if (logoImg != null && icon != null)
                {
                    logoImg.sprite = icon;
                    logoImg.color = Color.white;
                    logoImg.preserveAspect = true;
                }
                _selectedIcon = _button.transform.Find("SelectedIcon")?.gameObject;
                _selectedIcon?.SetActive(false);

                var btnComp = _button.GetComponent(Il2CppType.Of<Button>());
                var btn = btnComp != null ? btnComp.TryCast<Button>() : null;
                if (btn != null)
                {
                    btn.onClick = new Button.ButtonClickedEvent();
                    btn.onClick.AddListener((UnityAction)(() => { OpenRequested = true; }));
                }
                _button.SetActive(true);

                // ── Our page: a native AppsContainer sibling. ────────────────
                _page = new GameObject("BAMP_BusinessApp");
                _page.transform.SetParent(_appsContainer, false);
                var prt = _page.AddComponent<RectTransform>();
                prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
                _page.AddComponent<CanvasGroup>();

                var root = new GameObject("Root");
                root.transform.SetParent(_page.transform, false);
                var rrt = root.AddComponent<RectTransform>();
                rrt.anchorMin = rrt.anchorMax = rrt.pivot = new Vector2(0.5f, 0.5f);
                rrt.sizeDelta = new Vector2(1920f, 875f);
                rrt.localScale = new Vector3(2f, 2f, 1f);
                ContentRoot = root.transform;
                _page.SetActive(false);

                Ready = true;
                Plugin.Logger.LogInfo($"[HubApp] injected: top-row button + page (canvas={(canvas != null ? canvas.renderMode.ToString() : "?")}, cam={(UiCamera != null ? "yes" : "null")}).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[HubApp] inject: {ex.Message}"); }
        }

        /// <summary>Show our page (hides native pages + their selected dots).</summary>
        public static void ShowPage()
        {
            try
            {
                if (!Ready || _page == null || _appsContainer == null) return;
                for (int i = 0; i < _appsContainer.childCount; i++)
                {
                    var ch = _appsContainer.GetChild(i).gameObject;
                    if (ch != _page && ch.activeSelf) ch.SetActive(false);
                }
                SetNativeSelectedDots(false);
                _page.SetActive(true);
            }
            catch { }
        }

        /// <summary>Hide our page (a native app was selected, or closing).</summary>
        public static void HidePage()
        {
            try
            {
                if (_page != null && _page.activeSelf) _page.SetActive(false);
                _selectedIcon?.SetActive(false);
                if (_appNameLoc != null) _appNameLoc.enabled = true;   // native owns the title again
            }
            catch { }
        }

        private static void SetNativeSelectedDots(bool on)
        {
            try
            {
                var row = _button != null ? _button.transform.parent : null;
                if (row == null) return;
                for (int i = 0; i < row.childCount; i++)
                {
                    var ch = row.GetChild(i);
                    if (ch.gameObject == _button) continue;
                    var dot = ch.Find("SelectedIcon");
                    if (dot != null && dot.gameObject.activeSelf != on) dot.gameObject.SetActive(on);
                }
            }
            catch { }
        }

        /// <summary>Open the full menu (if closed) and land on Business —
        /// used by the phone's Business app icon.  Opens through the SAME
        /// pipeline the native phone icons use (SmartphoneUI.OpenApp) — the
        /// canvas child is inactive while closed, so a bare Toggle invoke was
        /// unreliable (phone button "did nothing", 2026-06-10).</summary>
        public static void OpenMenuToBusiness()
        {
            try
            {
                if (!Ready || _menu == null) return;
                var canvasT = _menu.transform.Find("Canvas");
                bool open = canvasT != null && canvasT.gameObject.activeInHierarchy;
                if (!open)
                {
                    bool opened = false;
                    // Native pipeline: SmartphoneUI.OpenApp(anyApp) — our
                    // ShowPage immediately replaces the page it selected.
                    try
                    {
                        var suiType = VehicleManager.FindGameType("SmartphoneUI");
                        var openApp = suiType?.GetMethod("OpenApp", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (suiType != null && openApp != null)
                        {
                            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(suiType), true);
                            var sui = arr != null && arr.Length > 0 ? arr[0].TryCast<Component>() : null;
                            var ps = openApp.GetParameters();
                            if (sui != null && ps.Length == 1 && ps[0].ParameterType.IsEnum)
                            {
                                var first = Enum.GetValues(ps[0].ParameterType).GetValue(0);
                                openApp.Invoke(sui, new object[] { first! });
                                opened = true;
                            }
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[HubApp] OpenApp path: {ex.Message}"); }
                    if (!opened)
                    {
                        var toggle = _menuType?.GetMethod("Toggle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        toggle?.Invoke(_menu, null);
                    }
                    Plugin.Logger.LogInfo($"[HubApp] menu open requested (viaOpenApp={opened}); canvas active={canvasT != null && canvasT.gameObject.activeInHierarchy}.");
                }
                ShowPage();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[HubApp] OpenMenuToBusiness: {ex.Message}"); }
        }

        /// <summary>Close the whole menu (our page's X).</summary>
        public static void CloseMenu()
        {
            try
            {
                HidePage();
                var close = _menuType?.GetMethod("CloseFullMenu", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (_menu != null && close != null) close.Invoke(_menu, null);
            }
            catch { }
        }
    }
}
