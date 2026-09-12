using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BigAmbitionsMP
{
    /// <summary>OPTIONS-GUARD (2026-09-05, F-2026-09-05-BI; SPEED-SHARED 2026-09-12): the game's Esc > Options > Others tab is usable
    /// mid-session. Its Difficulty dropdown / Customize button rewrite the economy LOCALLY (SaveGameManager.ApplyNewDifficulty — the mod
    /// syncs difficulty once at game start), so both stay greyed for the whole session. The Game speed slider is different: it works again,
    /// HOST-CONTROLLED. The host's slider is live and sets the WHOLE session's clock multiplier (GameManager.SetMinutesMultiplier); every
    /// client follows it over the 3 s clock heartbeat (GameTimeSyncPayload.ClockMult). A client's slider stays greyed but SHOWS the host's
    /// value, and a client's own saved preference (PlayerPrefSettings.GameSpeed) is never written — it is restored on the machine at the
    /// next scene reset once the session is over. Unstuck stays available (user decision). No on-screen text.</summary>
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

        // ── SPEED-SHARED: the session clock ────────────────────────────────────────────────────────────────
        /// <summary>Sane bounds for any multiplier we accept — a 0 would freeze the clock dead (the game's own pref wrapper
        /// defaults GameSpeed to 1f, PlayerPrefs.cs:68-69/:160-166, so only a corrupt value can be 0 - review r1 MINOR-2)
        /// and a huge one would outrun the drift correction.</summary>
        private const float MinMult = 0.05f;
        private const float MaxMult = 12f;

        /// <summary>CLIENTS: the multiplier this session runs at, as last received from the host's clock heartbeat.
        /// On the host the truth is the preference instead (HostValue) — see EffectiveSessionValue.</summary>
        public static float SessionMultiplier { get; private set; } = 1f;

        /// <summary>Did we ever push a multiplier of our own on this machine? Outside a session we restore the
        /// machine's own preference only if we did — single-player that never joined stays byte-identical.</summary>
        private static bool _multTouched;
        internal static void MarkMultiplierTouched() => _multTouched = true;

        /// <summary>Round-trip guard for the host's change broadcast (below); reset with the scene.</summary>
        internal static float LastBroadcastMult = float.MinValue;

        /// <summary>THE HOST'S OWN VALUE, read LIVE from the preference. The game's slider listener (Options.cs:1217-1223)
        /// writes PlayerPrefSettings.GameSpeed FIRST and only then calls SetMinutesMultiplier, so a prefix on the setter
        /// already sees the new value here. The preference is the source of truth on the host because the live field is
        /// whatever the last caller passed (and is 1f at process start, GameManager.cs:249).</summary>
        internal static float HostValue()
        {
            try { return Mathf.Clamp(PlayerPrefSettings.GameSpeed, MinMult, MaxMult); }
            catch (Exception ex) { Warn("host value", ex); return 1f; }
        }

        /// <summary>CLIENTS: adopt the host's value (from the clock heartbeat). Stores it first, so the Pin prefix below
        /// rewrites the setter call we make here to the very same number.</summary>
        internal static void SetSessionMultiplier(float v, string why)
        {
            try
            {
                v = Mathf.Clamp(v, MinMult, MaxMult);
                float old = SessionMultiplier;
                SessionMultiplier = v;
                if (Mathf.Abs(old - v) <= 0.001f) return;
                Plugin.Logger.LogInfo($"[OptionsGuard] session clock speed {v:0.00}x from the host ({why}; was {old:0.00}x).");
                _multTouched = true;
                GameManager.SetMinutesMultiplier(v);
                if (IsOptionsOpen()) ShowSessionValue(Instance);   // the greyed slider must not keep showing the old number
            }
            catch (Exception ex) { Warn("session multiplier (" + why + ")", ex); }
        }

        /// <summary>The multiplier this machine should be running at right now: the host reads its own preference live,
        /// a client uses the last value the host sent.</summary>
        internal static float EffectiveSessionValue()
        {
            try { return MPServer.IsRunning ? HostValue() : SessionMultiplier; }
            catch { return SessionMultiplier; }
        }

        /// <summary>Called from the game-scene-loaded reset (MPCanvasUI.cs) beside TimeSync.ResetClockState(). In a session
        /// the new world adopts the session value; out of one, the machine gets its OWN preference back — the only restore
        /// this design needs, since a client's preference is never written while it follows the host.</summary>
        internal static void OnSceneReset()
        {
            try
            {
                _rangeLogged = false;
                if (InSession) { SessionMultiplier = 1f; LastBroadcastMult = float.MinValue; PinClock("scene-reset"); return; }
                OnSessionLeft("scene-reset");
            }
            catch (Exception ex) { Warn("scene reset", ex); }
        }

        /// <summary>Fold b (review r1 MAJOR-1): the machine is no longer in a session - give it its OWN preference back.
        /// Called from the game-scene reset AND from the offline-fork dismiss (MPCanvasUI, the host gone for good and the
        /// player continuing alone), which does NOT reload the scene: before this fold that machine kept the host's last
        /// clock speed for the rest of the world while its un-greyed slider showed its own number.</summary>
        internal static void OnSessionLeft(string why)
        {
            try
            {
                SessionMultiplier = 1f;
                LastBroadcastMult = float.MinValue;
                if (!_multTouched) return;   // never touched it on this machine: leave single-player exactly as the game left it
                _multTouched = false;
                float pref = PlayerPrefSettings.GameSpeed;   // the same source the game itself loads from (GameManager.cs:266; the wrapper defaults to 1f)
                GameManager.SetMinutesMultiplier(pref);
                Plugin.Logger.LogInfo($"[OptionsGuard] own clock speed {pref:0.00}x restored ({why}).");
            }
            catch (Exception ex) { Warn("session left (" + why + ")", ex); }
        }

        /// <summary>CLIENT ONLY: show the host's value on the greyed slider. SetValueWithoutNotify, NEVER .value — the game's own
        /// listener (Options.cs:1217-1223) writes PlayerPrefSettings.GameSpeed as its first act, and this machine's saved preference
        /// must survive the session untouched.</summary>
        private static void ShowSessionValue(Scenes.MainMenu.Options o)
        {
            try
            {
                if (o == null) return;
                // The slider's units are pref x gameSpeedSliderMultiplier (Options.cs:1216; the constant is public static 10f at Options.cs:285).
                if (o.gameSpeedSlider != null)
                    o.gameSpeedSlider.SetValueWithoutNotify(SessionMultiplier * Scenes.MainMenu.Options.gameSpeedSliderMultiplier);
                // The game's own label format (Options.cs:1221-1222). The field is reachable as a plain member here because the mod builds
                // against the publicized game assembly (it is private in the shipped game) — the same direct access gameSpeedSlider uses.
                if (o.gameSpeedMultiplier != null)
                    o.gameSpeedMultiplier.text = $"{Mathf.RoundToInt(100f * SessionMultiplier)}%";
            }
            catch (Exception ex) { Warn("show session speed", ex); }
        }

        private static bool _rangeLogged;
        /// <summary>HOST, once per session: the slider's live range lives in the scene asset, not in the code (Options.cs:69-72 are
        /// [SerializeField] defaults), so the only way to learn it is to read the live component.</summary>
        private static void LogSliderRangeOnce(Scenes.MainMenu.Options o)
        {
            try
            {
                if (_rangeLogged || o.gameSpeedSlider == null) return;
                _rangeLogged = true;
                Plugin.Logger.LogInfo($"[OptionsGuard] game speed slider range {o.gameSpeedSlider.minValue:0.##}..{o.gameSpeedSlider.maxValue:0.##} (pref {PlayerPrefSettings.GameSpeed:0.00}).");
            }
            catch (Exception ex) { Warn("slider range", ex); }
        }

        /// <summary>Apply the greying to the three controls (idempotent; safe to call from Start and every OnEnable). Outside a session
        /// this touches nothing unless a previous call greyed the controls (then it restores them once) — review r1 MINOR-1.</summary>
        internal static void Apply(Scenes.MainMenu.Options o)
        {
            try
            {
                if (o == null) return;
                Instance = o;
                bool on = !InSession;
                bool host = MPServer.IsRunning;   // SPEED-SHARED: the host's Game speed slider stays live — it IS the session clock
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
                // SPEED-SHARED: the Game speed slider is live outside a session AND on the host (whose slider sets the session clock);
                // only a client is greyed — and a greyed client slider still SHOWS the host's value.
                try { if (o.gameSpeedSlider != null) o.gameSpeedSlider.interactable = on || host; } catch (Exception ex) { Warn("game speed slider", ex); }
                if (!on) { if (host) LogSliderRangeOnce(o); else ShowSessionValue(o); }
                _greyed = !on;
                if (!on && _logged++ < 5) Plugin.Logger.LogInfo(host
                    ? "[OptionsGuard] Difficulty greyed out for this session; Game speed stays live here and sets the session clock."
                    : "[OptionsGuard] Difficulty greyed out for this session; Game speed greyed and showing the host's value.");
            }
            catch (Exception ex) { Warn("apply", ex); }
        }

        private static int _warned;
        private static void Warn(string what, Exception ex) { try { if (_warned++ < 10) Plugin.Logger.LogWarning($"[OptionsGuard] {what}: {ex.Message}"); } catch { } }

        /// <summary>Put this machine's clock on the session value while in a session (world-ready, scene reset).</summary>
        internal static void PinClock(string why)
        {
            try { if (InSession) { _multTouched = true; GameManager.SetMinutesMultiplier(EffectiveSessionValue()); } }
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

    /// <summary>SPEED-SHARED. Every caller of the clock multiplier (the Options slider, load, PaidActivity, TimeHelper, a console cheat)
    /// gets the SESSION value in a session: the host's own preference on the host, the value the host sent on a client. That covers
    /// Options.cs:492, GameManager.cs:266, TimeHelper.cs:380 and — deliberately — PaidActivity.cs:150/184, which natively drops this
    /// machine to 1.0x for a fixed-duration activity and restores it afterwards. A paid activity therefore runs at the session speed on
    /// every machine: the same game-minutes as native, a different amount of real time, which is the price of one shared clock.
    /// The POSTFIX is the host's change detector: when the host moves its slider the new value goes out at once instead of waiting for
    /// the next 3 s heartbeat, so no client sits at the old speed long enough to cross the drift dead band.</summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.SetMinutesMultiplier))]
    public static class Patch_GameManager_SetMinutesMultiplier_Pin
    {
        private static int _logged;
        static void Prefix(ref float normalized)
        {
            try
            {
                if (!OptionsGuard.InSession) return;
                float v = OptionsGuard.EffectiveSessionValue();
                if (Mathf.Approximately(normalized, v)) return;   // the host's own slider lands here: the pref is already written
                if (_logged++ < 5) Plugin.Logger.LogInfo($"[OptionsGuard] clock multiplier {normalized:0.00} requested in-session — set to the session value {v:0.00}.");
                normalized = v;
                OptionsGuard.MarkMultiplierTouched();
            }
            catch { }
        }

        private static System.Reflection.FieldInfo? _minMultField;
        private static int _warned;
        static void Postfix()
        {
            try
            {
                if (!MPServer.IsRunning) return;
                _minMultField ??= AccessTools.Field(typeof(GameManager), "MinutesMultiplier");   // same reflection read as MPPatches.cs:516
                if (!(_minMultField?.GetValue(null) is float m)) return;
                if (Mathf.Abs(m - OptionsGuard.LastBroadcastMult) <= 0.001f) return;
                OptionsGuard.LastBroadcastMult = m;
                Plugin.Logger.LogInfo($"[OptionsGuard] host clock speed {m:0.00}x -> broadcast to the session.");
                MPServer.BroadcastGameTime();   // the 3 s heartbeat repeats it anyway; this only closes the gap
            }
            catch (Exception ex) { try { if (_warned++ < 5) Plugin.Logger.LogWarning($"[OptionsGuard] host speed broadcast: {ex.Message}"); } catch { } }
        }
    }
}
