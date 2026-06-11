using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// PASSIVE recon for the Business-app full-menu integration (2026-06-10).
    /// The game's full-screen interface (Persona | Contacts | Rivals | …) is
    /// UI.Smartphone.FullMenu: one shared shell + FullMenuAppButton row + app
    /// pages.  When the player first opens it, this probe dumps:
    ///   1. the shell hierarchy (depth-limited),
    ///   2. the app-button row in full (our "Business" button joins it),
    ///   3. the ACTIVE app page's widget inventory (white boxes, headers,
    ///      dropdowns, tables — prototypes for the native widget kit).
    /// No behavior changes.  "[FullMenu]" lines.  REMOVE after integration.
    /// </summary>
    public static class MPFullMenuProbe
    {
        private static Component? _menu;
        private static Type? _menuType;
        private static float _nextFindAt;
        private static bool _dumped;
        private static bool _pageDumped;
        private static int _lines;
        private const int MaxLines = 320;

        public static void Reset() { _menu = null; _dumped = false; _pageDumped = false; _lines = 0; }

        public static void Tick()
        {
            // After the shell dump: catch the FIRST app page the player opens
            // (the white-box widget inventory the shell dump missed — pages
            // were all inactive the frame the menu opened).
            if (_dumped && !_pageDumped)
            {
                try
                {
                    var ac = _menu != null ? _menu.transform.Find("Canvas/AppsContainer") : null;
                    if (ac != null)
                        for (int i = 0; i < ac.childCount; i++)
                        {
                            var ch = ac.GetChild(i);
                            if (!ch.gameObject.activeSelf || ch.name.StartsWith("BAMP_")) continue;
                            _pageDumped = true;
                            _lines = 0;
                            Plugin.Logger.LogInfo($"[FullMenu] ── page interior '{ch.name}' (widget inventory) ──");
                            Dump(ch, 0, 5, true);
                            Plugin.Logger.LogInfo("[FullMenu] ── page interior dump complete ──");
                            break;
                        }
                }
                catch { _pageDumped = true; }
                return;
            }
            if (_dumped) return;
            try
            {
                if (_menu == null)
                {
                    if (Time.unscaledTime < _nextFindAt) return;
                    _nextFindAt = Time.unscaledTime + 2f;
                    _menuType ??= VehicleManager.FindGameType("UI.Smartphone.FullMenu")
                               ?? VehicleManager.FindGameType("FullMenu");
                    if (_menuType == null) return;
                    var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_menuType), true);
                    if (arr == null || arr.Length == 0) return;
                    _menu = arr[0].TryCast<Component>();
                    if (_menu != null)
                        Plugin.Logger.LogInfo($"[FullMenu] instance found: '{_menu.gameObject.name}' (active={_menu.gameObject.activeInHierarchy}) — will dump on first open.");
                    return;
                }

                // Wait for the player to open it (shell + pages live then).
                if (!_menu.gameObject.activeInHierarchy) return;
                _dumped = true;
                Plugin.Logger.LogInfo("[FullMenu] ===== OPEN detected — dumping (passive, one-shot) =====");

                // 1. Shell, shallow.
                Dump(_menu.transform, 0, 3, false);

                // 2. App-button row in full + 3. active page, deeper.
                var t = _menu.transform;
                Transform? buttonRow = null, activePage = null;
                FindSpecials(t, ref buttonRow, ref activePage);
                if (buttonRow != null)
                {
                    _lines = 0;
                    Plugin.Logger.LogInfo($"[FullMenu] ── app-button row '{Path(buttonRow)}' ──");
                    Dump(buttonRow, 0, 4, true);
                }
                if (activePage != null)
                {
                    _lines = 0;
                    Plugin.Logger.LogInfo($"[FullMenu] ── active page '{Path(activePage)}' (widget inventory) ──");
                    Dump(activePage, 0, 5, true);
                }
                Plugin.Logger.LogInfo("[FullMenu] ===== dump complete =====");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[FullMenu] probe: {ex.Message}"); _dumped = true; }
        }

        /// <summary>The row containing FullMenuAppButton children, and the
        /// active (visible) app page — found by component scan.</summary>
        private static void FindSpecials(Transform root, ref Transform? buttonRow, ref Transform? activePage)
        {
            try
            {
                var comps = root.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
                foreach (var c in comps)
                {
                    var cc = c.TryCast<Component>();
                    if (cc == null) continue;
                    string tn = cc.GetIl2CppType().Name;
                    if (tn == "FullMenuAppButton" && buttonRow == null)
                        buttonRow = cc.transform.parent;
                    // App pages: each app is a class under UI.Smartphone.Apps.*
                    string full = cc.GetIl2CppType().FullName ?? "";
                    if (full.StartsWith("UI.Smartphone.Apps.") && cc.gameObject.activeInHierarchy && activePage == null)
                        activePage = cc.transform;
                }
            }
            catch { }
        }

        private static void Dump(Transform t, int depth, int maxDepth, bool detail)
        {
            if (depth > maxDepth || _lines >= MaxLines) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[FullMenu] ").Append(new string(' ', depth * 2)).Append(t.gameObject.activeSelf ? "+" : "-").Append(' ').Append(t.name);

                var rt = t.TryCast<RectTransform>();
                if (rt != null) sb.Append($" [{rt.rect.width:F0}x{rt.rect.height:F0}]");

                // Components of interest.
                foreach (var c in t.GetComponents(Il2CppType.Of<Component>()))
                {
                    var cc = c.TryCast<Component>();
                    if (cc == null) continue;
                    string tn = cc.GetIl2CppType().Name;
                    if (tn is "RectTransform" or "CanvasRenderer") continue;
                    if (tn == "Image" && detail)
                    {
                        var img = cc.TryCast<Image>();
                        if (img != null)
                        {
                            var col = img.color;
                            sb.Append($" Image(#{(int)(col.r*255):X2}{(int)(col.g*255):X2}{(int)(col.b*255):X2}@{col.a:F2}");
                            try { if (img.sprite != null) sb.Append($" '{img.sprite.name}'"); } catch { }
                            sb.Append(')');
                        }
                    }
                    else if (tn == "TextMeshProUGUI" && detail)
                    {
                        var txt = cc.TryCast<TextMeshProUGUI>();
                        if (txt != null)
                        {
                            var col = txt.color;
                            string raw = txt.text ?? "";
                            if (raw.Length > 24) raw = raw.Substring(0, 24) + "…";
                            sb.Append($" TMP({txt.fontSize:F0}pt #{(int)(col.r*255):X2}{(int)(col.g*255):X2}{(int)(col.b*255):X2} '{raw.Replace('\n', ' ')}')");
                        }
                    }
                    else if (tn != "Image" && tn != "TextMeshProUGUI")
                        sb.Append(' ').Append(tn);
                }
                Plugin.Logger.LogInfo(sb.ToString());
                _lines++;

                for (int i = 0; i < t.childCount && _lines < MaxLines; i++)
                    Dump(t.GetChild(i), depth + 1, maxDepth, detail);
            }
            catch { }
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            var cur = t.parent;
            int guard = 0;
            while (cur != null && guard++ < 10) { p = cur.name + "/" + p; cur = cur.parent; }
            return p;
        }
    }
}
