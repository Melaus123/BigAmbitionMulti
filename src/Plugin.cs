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

            // Apply Harmony patches (RentBuilding intercept, GameManager.Update drain)
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();

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
