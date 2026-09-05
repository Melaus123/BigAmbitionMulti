using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>OPTIONS-GUARD (2026-09-05, F-2026-09-05-BI): the game's Esc > Options > Others tab is usable mid-session. Its Difficulty
    /// dropdown / Customize button rewrite the economy LOCALLY (SaveGameManager.ApplyNewDifficulty — the mod syncs difficulty once at game
    /// start), and its Game speed slider rescales this machine's clock tick against the host clock (GameManager.SetMinutesMultiplier; the
    /// value persists across restarts). While a session is running the three controls are greyed (non-interactable) and the clock
    /// multiplier is pinned to 1.0. Unstuck stays available (user decision). No on-screen text.</summary>
    public static class OptionsGuard
    {
        internal static bool InSession => MPServer.IsRunning || MPClient.IsClientInWorld;

        private static int _logged;

        /// <summary>The game's persistent Options screen (set on every Start/OnEnable postfix). Its gameObject.activeInHierarchy is the
        /// truthful "Options is open" state — Options.IsVisible is a sticky static (review NEEDS-MENU-FREEZE r1 MINOR-8).</summary>
        internal static Scenes.MainMenu.Options Instance { get; private set; }
        internal static bool IsOptionsOpen()
        {
            try { var i = Instance; return i != null && i.gameObject != null && i.gameObject.activeInHierarchy; } catch { return false; }
        }

        private static bool _greyed;   // we changed the controls; restore them once the session is over

        /// <summary>Apply the greying to the three controls (idempotent; safe to call from Start and every OnEnable). Outside a session
        /// this touches nothing unless a previous call greyed the controls (then it restores them once) — review r1 MINOR-1.</summary>
        internal static void Apply(Scenes.MainMenu.Options o)
        {
            try
            {
                if (o == null) return;
                Instance = o;
                bool on = !InSession;
                if (on && !_greyed) return;   // single-player: leave the screen alone
                var dd = o.difficultyDropdown;
                if (dd != null && dd.gameObject.activeInHierarchy)   // populated only when active (Options.cs:1026-1034; Dropdown._hoverEffects is set by SetOptions)
                {
                    try { dd.SetInteractable(on); } catch (Exception ex) { Warn("difficulty dropdown", ex); }
                    try { if (dd.canvasGroup != null) dd.canvasGroup.alpha = on ? 1f : 0.5f; } catch (Exception ex) { Warn("difficulty dropdown alpha", ex); }
                }
                try
                {
                    var go = o.difficultyCustomizeButton;
                    if (go != null)
                    {
                        var b = go.GetComponent<Button>();
                        if (b != null) b.interactable = on;
                        var cg = go.GetComponent<CanvasGroup>();
                        if (cg == null && !on) cg = go.AddComponent<CanvasGroup>();
                        if (cg != null) { cg.interactable = on; cg.alpha = on ? 1f : 0.5f; }
                    }
                }
                catch (Exception ex) { Warn("difficulty customize", ex); }
                try { if (o.gameSpeedSlider != null) o.gameSpeedSlider.interactable = on; } catch (Exception ex) { Warn("game speed slider", ex); }
                _greyed = !on;
                if (!on && _logged++ < 5) Plugin.Logger.LogInfo("[OptionsGuard] Difficulty and Game speed greyed out for this session.");
            }
            catch (Exception ex) { Warn("apply", ex); }
        }

        private static int _warned;
        private static void Warn(string what, Exception ex) { try { if (_warned++ < 10) Plugin.Logger.LogWarning($"[OptionsGuard] {what}: {ex.Message}"); } catch { } }

        /// <summary>Pin the clock multiplier to 1.0 while in a session (world-ready and every setter call).</summary>
        internal static void PinClock(string why)
        {
            try { if (InSession) GameManager.SetMinutesMultiplier(1f); }
            catch (Exception ex) { Warn("pin clock (" + why + ")", ex); }
        }
    }

    [HarmonyPatch(typeof(Scenes.MainMenu.Options), "Start")]
    public static class Patch_Options_Start_Guard
    {
        static void Postfix(Scenes.MainMenu.Options __instance) => OptionsGuard.Apply(__instance);
    }

    [HarmonyPatch(typeof(Scenes.MainMenu.Options), "OnEnable")]
    public static class Patch_Options_OnEnable_Guard
    {
        static void Postfix(Scenes.MainMenu.Options __instance) => OptionsGuard.Apply(__instance);
    }

    /// <summary>Difficulty setup re-creates the dropdown options each time it runs (Options.cs:1024-1046); re-apply after it.</summary>
    [HarmonyPatch(typeof(Scenes.MainMenu.Options), "SetUpDifficultySetting")]
    public static class Patch_Options_SetUpDifficultySetting_Guard
    {
        static void Postfix(Scenes.MainMenu.Options __instance) => OptionsGuard.Apply(__instance);
    }

    /// <summary>Every caller of the clock multiplier (Options slider, load, PaidActivity restore, console cheat) gets 1.0 in-session.</summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.SetMinutesMultiplier))]
    public static class Patch_GameManager_SetMinutesMultiplier_Pin
    {
        private static int _logged;
        static void Prefix(ref float normalized)
        {
            try
            {
                if (!OptionsGuard.InSession || Mathf.Approximately(normalized, 1f)) return;
                if (_logged++ < 5) Plugin.Logger.LogInfo($"[OptionsGuard] clock multiplier {normalized:0.00} requested in-session — pinned to 1.00.");
                normalized = 1f;
            }
            catch { }
        }
    }
}
