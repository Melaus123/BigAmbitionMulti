using System;
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
        private static readonly System.Collections.Generic.HashSet<string> _pagesDumped = new();
        private static int _lines;
        private const int MaxLines = 320;

        // PERSIST the dump: round 1 left it in BepInEx's log, which is
        // overwritten every run — the data evaporated (2026-06-11).
        private const string DumpPath = @"C:\code\BigAmbitionsMP\.modding\ui-dumps\fullmenu.txt";

        private static void Out(string s)
        {
            Plugin.Logger.LogInfo(s);
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DumpPath)!);
                System.IO.File.AppendAllText(DumpPath, s + "\n");
            }
            catch { }
        }

        public static void Reset() { _menu = null; _dumped = false; _pagesDumped.Clear(); _lines = 0; }

        public static void Tick()
        {
            // After the shell dump: catch EVERY app page the player opens
            // (round 1 stopped at the first page — thin coverage).  Each page
            // dumps once per scene; click through all apps in one sitting.
            if (_dumped)
            {
                try
                {
                    var ac = _menu != null ? _menu.transform.Find("Canvas/AppsContainer") : null;
                    if (ac != null)
                        for (int i = 0; i < ac.childCount; i++)
                        {
                            var ch = ac.GetChild(i);
                            if (!ch.gameObject.activeSelf || ch.name.StartsWith("BAMP_")) continue;
                            if (!_pagesDumped.Add(ch.name)) continue;
                            _lines = 0;
                            Out($"[FullMenu] ── page interior '{ch.name}' (widget inventory) ──");
                            Dump(ch, 0, 5, true);
                            Out($"[FullMenu] ── page interior '{ch.name}' dump complete ──");
                        }
                }
                catch { }
                return;
            }
            try
            {
                if (_menu == null)
                {
                    if (Time.unscaledTime < _nextFindAt) return;
                    _nextFindAt = Time.unscaledTime + 2f;
                    _menuType ??= VehicleManager.FindGameType("UI.Smartphone.FullMenu")
                               ?? VehicleManager.FindGameType("FullMenu");
                    if (_menuType == null) return;
                    var arr = UnityEngine.Object.FindObjectsOfType(_menuType, true);
                    if (arr == null || arr.Length == 0) return;
                    _menu = arr[0] as Component;
                    if (_menu != null)
                        Out($"[FullMenu] instance found: '{_menu.gameObject.name}' (active={_menu.gameObject.activeInHierarchy}) — will dump on first open.");
                    return;
                }

                // Wait for the player to open it (shell + pages live then).
                if (!_menu.gameObject.activeInHierarchy) return;
                _dumped = true;
                Out("[FullMenu] ===== OPEN detected — dumping (passive, one-shot) =====");

                // 1. Shell, shallow.
                Dump(_menu.transform, 0, 3, false);

                // 2. App-button row in full + 3. active page, deeper.
                var t = _menu.transform;
                Transform? buttonRow = null, activePage = null;
                FindSpecials(t, ref buttonRow, ref activePage);
                if (buttonRow != null)
                {
                    _lines = 0;
                    Out($"[FullMenu] ── app-button row '{Path(buttonRow)}' ──");
                    Dump(buttonRow, 0, 4, true);
                }
                if (activePage != null)
                {
                    _lines = 0;
                    Out($"[FullMenu] ── active page '{Path(activePage)}' (widget inventory) ──");
                    Dump(activePage, 0, 5, true);
                }
                Out("[FullMenu] ===== dump complete =====");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[FullMenu] probe: {ex.Message}"); _dumped = true; }
        }

        /// <summary>The row containing FullMenuAppButton children, and the
        /// active (visible) app page — found by component scan.</summary>
        private static void FindSpecials(Transform root, ref Transform? buttonRow, ref Transform? activePage)
        {
            try
            {
                var comps = root.GetComponentsInChildren(typeof(Component), true);
                foreach (var c in comps)
                {
                    var cc = c as Component;
                    if (cc == null) continue;
                    string tn = cc.GetType().Name;
                    if (tn == "FullMenuAppButton" && buttonRow == null)
                        buttonRow = cc.transform.parent;
                    // App pages: each app is a class under UI.Smartphone.Apps.*
                    string full = cc.GetType().FullName ?? "";
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

                var rt = t as RectTransform;
                if (rt != null) sb.Append($" [{rt.rect.width:F0}x{rt.rect.height:F0}]");

                // Components of interest.
                foreach (var c in t.GetComponents(typeof(Component)))
                {
                    var cc = c as Component;
                    if (cc == null) continue;
                    string tn = cc.GetType().Name;
                    if (tn is "RectTransform" or "CanvasRenderer") continue;
                    if (tn == "Image" && detail)
                    {
                        var img = cc as Image;
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
                        var txt = cc as TextMeshProUGUI;
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
                Out(sb.ToString());   // the call the v2 persist-refactor MISSED — content went to the volatile log again
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
