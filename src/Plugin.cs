using System;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace BigAmbitionsMP
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        public static ManualLogSource Logger { get; private set; } = null!;
        public static Plugin Instance { get; private set; } = null!;

        private Harmony _harmony = null!;

        public override void Load()
        {
            Instance = this;
            Logger   = Log;

            Logger.LogInfo("BigAmbitionsMP loading...");

            // Read optional defaults from config file
            MPConfig.Init(Config);

            // Register our MonoBehaviours with IL2CPP so Unity can manage them
            ClassInjector.RegisterTypeInIl2Cpp<MPCanvasUI>();
            ClassInjector.RegisterTypeInIl2Cpp<RemotePlayerMover>();

            // Attach the UI component — it creates its own Canvas in Awake()
            AddComponent<MPCanvasUI>();

            // Apply Harmony patches per-class so a single bad class can't take
            // down the rest.  PatchAll() iterates internally and ABORTS on the
            // first throw (e.g. a TargetMethods returning empty → "Undefined
            // target method") — wrapping PatchAll in try/catch only catches
            // the outer throw, it does NOT cause the remaining classes to be
            // patched.  Iterating ourselves and try/catching each class gives
            // real isolation.
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            int okClasses = 0, failClasses = 0, totalPatched = 0;
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
            {
                if (!t.GetCustomAttributes(typeof(HarmonyPatch), true).Any()) continue;
                try
                {
                    var before = _harmony.GetPatchedMethods().Count();
                    _harmony.CreateClassProcessor(t).Patch();
                    var added = _harmony.GetPatchedMethods().Count() - before;
                    okClasses++;
                    totalPatched += added;
                    // Per-class patch count — keeps a single broken patch
                    // class visible at startup instead of silently dropped.
                    Logger.LogInfo($"[Plugin] Patched {t.Name}: {added} method(s)");
                }
                catch (Exception ex)
                {
                    failClasses++;
                    Logger.LogError($"[Plugin] Patch class {t.Name} FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Logger.LogInfo($"[Plugin] Patch summary: {okClasses} class(es) OK, {failClasses} failed, {totalPatched} method(s) patched total.");

            Logger.LogInfo("BigAmbitionsMP loaded. Canvas UI active — press F8 to toggle.");
        }

        public override bool Unload()
        {
            _harmony?.UnpatchSelf();
            MPServer.Stop();
            MPClient.Disconnect();
            return base.Unload();
        }
    }
}
