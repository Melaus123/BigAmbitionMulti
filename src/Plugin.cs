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

            // Apply Harmony patches.  Wrap in try/catch so a single broken
            // patch (e.g. TargetMethod returning null) doesn't abort PatchAll
            // and silently drop ALL other patches — which would leave the
            // plugin in a half-loaded state that's hard to diagnose.
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            try
            {
                _harmony.PatchAll();
                Logger.LogInfo($"[Plugin] PatchAll completed: {_harmony.GetPatchedMethods().Count()} method(s) patched.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Plugin] PatchAll FAILED — some Harmony patches were not applied: {ex}");
            }

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
