using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Extensions;
using Localizor;
using Localizor.LanguageChangeEvent;
using Scenes.MainMenu;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Round-225 (user request): the MP load screen becomes the NATIVE single-player
    /// load window — cloned, stripped, and re-fed with our playthrough data — so the
    /// layout matches vanilla exactly and saves can be DELETED like vanilla.
    ///
    /// Clone → STRIP → rewire (the user's explicit caveat: inherit nothing by accident):
    ///  * Only the WINDOW PANEL is cloned — never the flow that loads SP saves.
    ///  * The clone's LoadGame controller component is DESTROYED outright: its Update
    ///    (F5 rescan, Escape close), its SP-folder scans, and every serialized button
    ///    handler pointing at it die with it.
    ///  * SP-only chrome is removed by field reference (upgrade-saves button, broken-
    ///    saves popup, mod-backup warning — when they live inside the cloned subtree).
    ///  * Every button we keep gets a FRESH event object (kills serialized listeners,
    ///    not just runtime ones) before our handler is added.
    ///  * Card/row components are safe by construction: rows replace their own
    ///    listeners in Setup(), and the mod-compat tooltip deactivates itself for
    ///    saves with hasEverUsedMods=false (ours always are).
    ///  * A one-time inventory of the clone's buttons/labels is logged on first build
    ///    so anything unexpected is visible on the rig instead of silently inherited.
    ///
    /// Deletion (vanilla parity, MP honesty): per-save and per-world, both behind the
    /// native HudConfirm dialog with the native confirm texts. Deletes are performed
    /// on the EXACT directories the list walk yielded (never name resolution — the
    /// name-as-identity rule), and remove only THIS machine's copy; mirrored copies on
    /// other members' machines are theirs.
    /// </summary>
    internal static class MPLoadWindow
    {
        private static GameObject _win;
        private static LoadGame  _sceneLg;      // the ORIGINAL (template + GetIcon source) — never modified
        private static Transform _template;     // clone's character-card template
        private static Toggle    _recoverToggle;
        private static bool      _showRecover;
        private static bool      _inventoryLogged;
        private static readonly List<LoadGameCharacterEntryView> _cards = new();
        private static readonly Dictionary<SaveGameManager.SaveGameStruct, MPSaveManager.MpVariant> _rowMap = new();

        public static bool IsOpen => _win != null && _win.activeSelf;

        /// <summary>Open the native-style window; false = build failed (caller falls
        /// back to the classic picker, loudly).</summary>
        public static bool Open()
        {
            try
            {
                if (_win == null && !Build()) return false;
                Refresh();
                // Round-225f: as a sibling of the real window the clone keeps its
                // authored geometry — do NOT touch the rect. Animators still must die
                // (they hold the faded/closed pose) and canvas groups must be opaque.
                var rt = _win.GetComponent<RectTransform>();
                foreach (var an in _win.GetComponentsInChildren<Animator>(true)) { try { an.enabled = false; } catch { } }
                foreach (var cg in _win.GetComponentsInChildren<CanvasGroup>(true)) { try { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; } catch { } }
                _win.transform.SetAsLastSibling();
                _win.SetActive(true);
                MPCanvasUI.Instance?.SetMpMenuHiddenForLoadWindow(true);   // round-225e
                if (rt != null)
                    Plugin.Logger.LogInfo($"[LoadWin] shown: active={_win.activeInHierarchy} rect={rt.rect.width:F0}x{rt.rect.height:F0} scale={rt.lossyScale.x:F2} pos={rt.position} canvas={( _win.GetComponentInParent<Canvas>()?.name ?? "none")}");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[LoadWin] open failed — classic picker fallback. {ex.Message}");
                return false;
            }
        }

        public static void Close()
        {
            try { if (_win != null) _win.SetActive(false); } catch { }
            try { MPCanvasUI.Instance?.SetMpMenuHiddenForLoadWindow(false); } catch { }   // round-225e
        }

        /// <summary>Scene teardown: the clone borrows menu-scene assets — drop it and
        /// rebuild fresh next time the menu opens.</summary>
        public static void ResetForScene()
        {
            try { if (_win != null) UnityEngine.Object.Destroy(_win); } catch { }
            _win = null; _sceneLg = null; _template = null; _recoverToggle = null;
            _cards.Clear(); _rowMap.Clear();
        }

        // ── build: clone + strip + rewire ────────────────────────────────────

        private static bool Build()
        {
            _sceneLg = FindSceneLoadGame();
            if (_sceneLg == null) { Plugin.Logger.LogWarning("[LoadWin] native LoadGame window not found in this scene."); return false; }

            // Rounds 225b-f (rig-iterated): our own canvases fought the native ones —
            // wrong scaler (giant), wrong sort (invisible, or above HudConfirm). The
            // robust home is the NATIVE canvas itself: the clone becomes a SIBLING of
            // the real load window, so position, scale, and dialog stacking are all
            // correct by construction, with nothing to tune.
            var parent = _sceneLg.transform.parent;
            if (parent == null) { Plugin.Logger.LogWarning("[LoadWin] native window has no parent — aborting."); return false; }

            _win = UnityEngine.Object.Instantiate(_sceneLg.gameObject, parent, worldPositionStays: false);
            _win.name = "BAMP_LoadWindow";
            _win.SetActive(false);

            var lg = _win.GetComponent<LoadGame>();
            // Capture the clone's OWN references before the controller dies.
            _template      = FieldOf<Transform>(lg, "characterTemplate");
            _recoverToggle = FieldOf<Toggle>(lg, "toggleRecoverSaves");
            DestroyIfOurs(FieldOf<GameObject>(lg, "upgradeSaveGamesButton"));
            DestroyIfOurs(FieldOf<GameObject>(lg, "brokenSaveGamesPopup"));
            var backupUi = FieldOf<Component>(lg, "modBackupWarningUi");
            if (backupUi != null) DestroyIfOurs(backupUi.gameObject);
            // Kill the controller: Update (F5/Escape), SP scans, and every serialized
            // handler targeting it become inert. Immediate, so nothing runs a frame.
            UnityEngine.Object.DestroyImmediate(lg);
            if (_template == null) { Plugin.Logger.LogWarning("[LoadWin] clone had no character template — aborting."); ResetForScene(); return false; }

            // Rewire surviving chrome by name; log the full inventory once so any
            // unexpected inheritance is SEEN, not suffered.
            foreach (var b in _win.GetComponentsInChildren<Button>(true))
            {
                string n = b.gameObject.name.ToLowerInvariant();
                string path = PathOf(b.transform);
                if (!_inventoryLogged) Plugin.Logger.LogInfo($"[LoadWin] clone button: {path}");
                if (b.GetComponentInParent<LoadGameCharacterEntryView>() != null) continue;   // card internals — handled per card
                if (n.Contains("close") || n.Contains("back") || n.Contains("exit"))
                { Rewire(b, () => { Close(); MPCanvasUI.Instance?.OnLoadWindowClosed(); }); }
                else if (n.Contains("browse") || n.Contains("folder"))
                { Rewire(b, () => { try { System.Diagnostics.Process.Start("explorer.exe", Path.GetFullPath(MPSaveManager.MpVersionFolder())); } catch (Exception ex) { Plugin.Logger.LogWarning($"[LoadWin] browse: {ex.Message}"); } }); }
                else if (n.Contains("upgrade"))
                { UnityEngine.Object.Destroy(b.gameObject); }
            }
            if (_recoverToggle != null)
            {
                _recoverToggle.onValueChanged = new Toggle.ToggleEvent();   // fresh event: serialized handlers die
                _recoverToggle.SetIsOnWithoutNotify(false);
                _showRecover = false;
                _recoverToggle.onValueChanged.AddListener(v =>
                {
                    _showRecover = v;
                    foreach (var c in _cards) { try { if (c != null && c.HasRecoverSaveEntries()) c.SetRecoverSavesVisible(v); } catch { } }
                });
            }
            if (!_inventoryLogged)
            {
                foreach (var t in _win.GetComponentsInChildren<TMP_Text>(true).Take(12))
                    Plugin.Logger.LogInfo($"[LoadWin] clone label: {PathOf(t.transform)} = '{Trunc(t.text)}'");
                _inventoryLogged = true;
            }
            return true;
        }

        // ── refresh: feed OUR playthroughs into the native cards ─────────────

        private static void Refresh()
        {
            _cards.Clear(); _rowMap.Clear();
            try { _template.ResetTemplate(); } catch { ResetTemplateManual(_template); }

            var runs = MPSaveManager.ListPlaythroughs();
            foreach (var run in runs)
            {
                Transform entry;
                try { entry = _template.CreateElement(); } catch { entry = CreateElementManual(_template); }
                var card = entry.GetComponent<LoadGameCharacterEntryView>();
                if (card == null) continue;
                _cards.Add(card);

                card.SetTitle(CardTitle(run));
                TrySetPortrait(card, run);
                if (card.LoadLastGameModTooltip != null) { try { card.LoadLastGameModTooltip.gameObject.SetActive(false); } catch { } }

                var newestMain = run.Variants.Where(v => v.Kind == "Main").OrderByDescending(v => v.SavedAtUnix).FirstOrDefault()
                              ?? run.Variants.OrderByDescending(v => v.SavedAtUnix).FirstOrDefault();
                var runRef = run;
                Rewire(card.LoadLastGameButton, () => { if (newestMain != null) LoadVariant(newestMain); });
                Rewire(card.RemoveCharacterButton, () => ConfirmDeleteWorld(runRef));

                var structs = new List<SaveGameManager.SaveGameStruct>();
                foreach (var v in run.Variants)
                {
                    var s = new SaveGameManager.SaveGameStruct
                    {
                        name           = v.SessionName,
                        day            = v.Day < 0 ? 0 : v.Day,
                        lastPlayedDate = v.SavedAtUnix > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(v.SavedAtUnix).LocalDateTime
                            : SafeDirTime(v.SessionDir),
                        isRecoverSave  = v.Kind != "Main",          // vanilla's toggle hides these
                        CharacterPath  = v.SessionDir ?? "",        // GetIcon probes <dir>/<name>.jpg → default sprite
                        hasEverUsedMods = false,                    // mod tooltip deactivates itself
                    };
                    structs.Add(s);
                    _rowMap[s] = v;
                }
                card.Setup(structs, _showRecover, GetIconSafe, OnRowLoad, OnRowDelete);
                entry.gameObject.SetActive(true);
            }
            Plugin.Logger.LogInfo($"[LoadWin] listed {runs.Count} playthrough(s).");
        }

        private static string CardTitle(MPSaveManager.MpPlaythrough run)
        {
            string players = (run.Players ?? "").Trim();
            return string.IsNullOrEmpty(players) || players == "—" ? run.Base : $"{run.Base}  ({players})";
        }

        private static void TrySetPortrait(LoadGameCharacterEntryView card, MPSaveManager.MpPlaythrough run)
        {
            try
            {
                foreach (var v in run.Variants.OrderByDescending(x => x.SavedAtUnix))
                {
                    if (string.IsNullOrEmpty(v.SessionDir) || !Directory.Exists(v.SessionDir)) continue;
                    var jpg = Directory.GetFiles(v.SessionDir, "*portrait*.jpg", SearchOption.AllDirectories).FirstOrDefault();
                    if (jpg == null) continue;
                    var tex = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                    if (!tex.LoadImage(File.ReadAllBytes(jpg))) { UnityEngine.Object.Destroy(tex); continue; }
                    card.PortraitImage.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    return;
                }
            }
            catch { }
        }

        private static Sprite GetIconSafe(SaveGameManager.SaveGameStruct s)
        {
            try { if (_sceneLg != null) return _sceneLg.GetIcon(s); } catch { }
            return null;
        }

        // ── load + delete actions ────────────────────────────────────────────

        private static void LoadVariant(MPSaveManager.MpVariant v)
        {
            Close();
            MPCanvasUI.Instance?.LoadPickedSession(v.SessionName, v.PlaythroughId ?? "");
        }

        private static void OnRowLoad(SaveGameManager.SaveGameStruct s)
        {
            if (_rowMap.TryGetValue(s, out var v)) LoadVariant(v);
        }

        private static void OnRowDelete(SaveGameManager.SaveGameStruct s, Transform row)
        {
            if (!_rowMap.TryGetValue(s, out var v)) return;
            HudConfirm.Show(default, "main_menu_delete_save_game_confirm".Localize(new { saveGameName = v.SessionName }), () =>
            {
                DeleteDirs(new[] { v.SessionDir }, $"save '{v.SessionName}'");
                Refresh();
            });
        }

        private static void ConfirmDeleteWorld(MPSaveManager.MpPlaythrough run)
        {
            HudConfirm.Show(default, "main_menu_delete_character_confirm".Localize(new { characterName = run.Base }), () =>
            {
                DeleteDirs(run.Variants.Select(v => v.SessionDir), $"world '{run.Base}' ({run.Variants.Count} save(s))");
                Refresh();
            });
        }

        /// <summary>Delete the EXACT directories the walk yielded (no name resolution),
        /// then clean any playthrough folder left empty. This machine's copy only.</summary>
        private static void DeleteDirs(IEnumerable<string> dirs, string what)
        {
            int done = 0;
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    parents.Add(Path.GetDirectoryName(d));
                    Directory.Delete(d, recursive: true);
                    done++;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[LoadWin] delete '{d}': {ex.Message}"); }
            }
            string root = MPSaveManager.MpVersionFolder();
            foreach (var p in parents)
            {
                try
                {
                    // An emptied playthrough folder (v2) goes too — but never the store root.
                    if (!string.IsNullOrEmpty(p) && !string.Equals(p.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(p) && Directory.GetFileSystemEntries(p).Length == 0)
                        Directory.Delete(p);
                }
                catch { }
            }
            MPSaveManager.ResetPidCache();
            Plugin.Logger.LogInfo($"[LoadWin] deleted {what} — {done} folder(s) removed (this machine's copy only).");
        }

        // ── small helpers ────────────────────────────────────────────────────

        private static LoadGame FindSceneLoadGame()
        {
            try
            {
                foreach (var lg in Resources.FindObjectsOfTypeAll<LoadGame>())
                    if (lg != null && lg.gameObject.scene.IsValid()) return lg;   // scene object, not a prefab asset
            }
            catch { }
            return null;
        }

        private static T FieldOf<T>(object obj, string field) where T : class
        {
            try { return HarmonyLib.AccessTools.Field(obj.GetType(), field)?.GetValue(obj) as T; } catch { return null; }
        }

        private static void DestroyIfOurs(GameObject go)
        {
            try { if (go != null && _win != null && go.transform.IsChildOf(_win.transform)) UnityEngine.Object.Destroy(go); } catch { }
        }

        private static void Rewire(Button b, Action onClick)
        {
            if (b == null) return;
            b.onClick = new Button.ButtonClickedEvent();   // fresh event: serialized handlers die with it
            b.onClick.AddListener(() => { try { onClick(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[LoadWin] click: {ex.Message}"); } });
        }

        private static DateTime SafeDirTime(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return Directory.GetLastWriteTime(dir); } catch { }
            return DateTime.MinValue;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            while (t != null && parts.Count < 8) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 40 ? s.Substring(0, 40) : s).Replace("\n", " ");

        // Fallbacks if the game's template extension helpers are unreachable.
        private static void ResetTemplateManual(Transform template)
        {
            if (template == null) return;
            foreach (Transform sib in template.parent)
                if (sib != template && sib.gameObject.activeSelf) UnityEngine.Object.Destroy(sib.gameObject);
            template.gameObject.SetActive(false);
        }

        private static Transform CreateElementManual(Transform template)
        {
            var t = UnityEngine.Object.Instantiate(template.gameObject, template.parent).transform;
            t.gameObject.SetActive(true);
            return t;
        }
    }
}
