using System;
using System.Linq;
using System.Threading.Tasks;
using BAModAPI;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// EA 0.11+ entry point — loaded by the game's OFFICIAL mod loader
    /// (BigAmbitions.ModsInternal): ModsLocal\BigAmbitionsMP\BigAmbitionsMP.dll
    /// with Harmony + LiteNetLib in the Dependencies\ subfolder.
    /// (The BepInEx/IL2CPP entry for EA 0.10 lives on the 'main' branch.)
    /// </summary>
    public class ModEntry : ModBigAmbitionsBase
    {
        public static ModEntry Instance { get; private set; } = null!;

        private Harmony? _harmony;
        private GameObject? _uiHost;

        public override Task OnLoadAsync(ModContext context)
        {
            Instance = this;
            Plugin.Logger = new ModLog(context.Logger);

            Plugin.Logger.LogInfo($"BigAmbitionsMP loading (official loader, modId='{context.ModId}', root='{context.ModRootPath}')...");

            MPConfig.Init(context.ModRootPath);

            // Persistent host object for our UI component (Mono: custom
            // MonoBehaviours need no registration — AddComponent just works).
            _uiHost = new GameObject("BigAmbitionsMP");
            UnityEngine.Object.DontDestroyOnLoad(_uiHost);
            _uiHost.AddComponent<MPCanvasUI>();

            // Apply Harmony patches per-class so a single bad class can't take
            // down the rest (PatchAll aborts on the first throw).
            _harmony = new Harmony("com.bamp.bigambitionsmp");
            int okClasses = 0, failClasses = 0, totalPatched = 0;
            foreach (var t in typeof(ModEntry).Assembly.GetTypes())
            {
                if (!t.GetCustomAttributes(typeof(HarmonyPatch), true).Any()) continue;
                try
                {
                    var before = _harmony.GetPatchedMethods().Count();
                    _harmony.CreateClassProcessor(t).Patch();
                    var added = _harmony.GetPatchedMethods().Count() - before;
                    okClasses++;
                    totalPatched += added;
                    Plugin.Logger.LogInfo($"[Plugin] Patched {t.Name}: {added} method(s)");
                }
                catch (Exception ex)
                {
                    failClasses++;
                    Plugin.Logger.LogError($"[Plugin] Patch class {t.Name} FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Plugin.Logger.LogInfo($"[Plugin] Patch summary: {okClasses} class(es) OK, {failClasses} failed, {totalPatched} method(s) patched total.");

            // Optional testing-aid: drive new-game setup automatically when
            // BAMP_AUTOROLE env var is set.  No-op in normal play.
            MPAutopilot.Init();

            Plugin.Logger.LogInfo("BigAmbitionsMP loaded. Canvas UI active — press F8 to toggle.");
            return Task.CompletedTask;
        }

        public override Task OnUnloadAsync()
        {
            try { _harmony?.UnpatchAll(_harmony.Id); } catch { }
            try { MPServer.Stop(); } catch { }
            try { MPClient.Disconnect(); } catch { }
            if (_uiHost != null) UnityEngine.Object.Destroy(_uiHost);
            return Task.CompletedTask;
        }
    }

    /// <summary>Static façade kept so the rest of the codebase compiles
    /// unchanged: Plugin.Logger.Log* everywhere routes to the official
    /// loader's IModLogger and Unity's console.</summary>
    public static class Plugin
    {
        public static ModLog Logger { get; internal set; } = new ModLog(null);
    }

    /// <summary>BepInEx-ManualLogSource-shaped wrapper over IModLogger.</summary>
    public class ModLog
    {
        private readonly IModLogger? _inner;
        public ModLog(IModLogger? inner) { _inner = inner; }

        public void LogInfo(object msg)    { var s = $"[BAMP] {msg}";  if (_inner != null) _inner.Info(s);  else Debug.Log(s); }
        public void LogWarning(object msg) { var s = $"[BAMP] {msg}";  if (_inner != null) _inner.Warn(s);  else Debug.LogWarning(s); }
        public void LogError(object msg)   { var s = $"[BAMP] {msg}";  if (_inner != null) _inner.Error(s); else Debug.LogError(s); }
        public void LogDebug(object msg)   => LogInfo(msg);
    }
}
