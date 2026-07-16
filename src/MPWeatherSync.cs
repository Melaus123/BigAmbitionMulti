using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Weather sync (user-approved 2026-07-14): rain is driven by
    /// per-save state (gi.seed + gi.nextRainStartTime), so host and client skies
    /// diverge — one player in a downpour, the other in sun.  The host's rain
    /// state (dry/raining) rides the existing GameTimeSync heartbeat (~3s) as an
    /// additive JSON field (old builds ignore it — no protocol impact); the
    /// client forces its local RainHelper to match on mismatch.
    ///
    /// RainHelper is a DECOMPILE-DUMP GAP (class absent from the dump, like
    /// HandleEscapeClick) — everything here is reflection, and a total miss on
    /// the candidate transition methods dumps RainHelper's static surface ONCE
    /// so the log names the real API.  The Dev-build F7 key forces a transition
    /// locally, so the full sync loop is testable on one rig without waiting
    /// for natural rain.  Failure mode is purely cosmetic: skies stay divergent
    /// exactly as they always were.</summary>
    public static class MPWeatherSync
    {
        private static Type? _rainHelper;
        private static System.Reflection.PropertyInfo? _isRainingProp;
        private static System.Reflection.FieldInfo?    _isRainingField;
        private static bool _resolved, _surfaceDumped;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                // FindGameType needs the FULL name and RainHelper's namespace is a
                // dump gap (first F7 run confirmed the bare name misses) — scan all
                // loaded assemblies by simple name and log what resolved.
                _rainHelper = VehicleManager.FindGameType("RainHelper");
                if (_rainHelper == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type?[] types;
                        try { types = asm.GetTypes(); }
                        catch (System.Reflection.ReflectionTypeLoadException rtle) { types = rtle.Types; }
                        catch { continue; }
                        foreach (var t in types)
                            if (t != null && t.Name == "RainHelper") { _rainHelper = t; break; }
                        if (_rainHelper != null) break;
                    }
                }
                if (_rainHelper == null)
                { Plugin.Logger.LogWarning("[Weather] RainHelper type not found in any loaded assembly — weather sync off."); return; }
                Plugin.Logger.LogInfo($"[Weather] RainHelper resolved: '{_rainHelper.FullName}' in {_rainHelper.Assembly.GetName().Name}.");
                _isRainingProp  = AccessTools.Property(_rainHelper, "isRaining");
                _isRainingField = _isRainingProp == null ? AccessTools.Field(_rainHelper, "isRaining") : null;
                if (_isRainingProp == null && _isRainingField == null)
                    Plugin.Logger.LogWarning("[Weather] RainHelper.isRaining not found — weather sync off.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Weather] resolve: {ex.Message}"); }
        }

        /// <summary>-1 unknown, 0 dry, 1 raining.</summary>
        public static int CurrentRainState()
        {
            Resolve();
            try
            {
                if (_isRainingProp  != null) return (bool)_isRainingProp.GetValue(null)! ? 1 : 0;
                if (_isRainingField != null) return (bool)_isRainingField.GetValue(null)! ? 1 : 0;
            }
            catch { }
            return -1;
        }

        /// <summary>CLIENT, main thread: align local rain with the host's state.</summary>
        public static void ApplyRainState(int hostState)
        {
            if (hostState < 0) return;
            int local = CurrentRainState();
            if (local < 0 || local == hostState) return;
            Plugin.Logger.LogInfo($"[Weather] host={(hostState == 1 ? "raining" : "dry")} local={(local == 1 ? "raining" : "dry")} — forcing local to match.");
            TryForceRain(hostState == 1);
        }

        /// <summary>Invoke a RainHelper transition; on total candidate miss, dump
        /// its static method surface once so the field log names the real API.</summary>
        public static void TryForceRain(bool on)
        {
            Resolve();
            if (_rainHelper == null) return;
            string[] candidates = on
                ? new[] { "StartRain", "BeginRain", "RainStart", "StartRaining" }
                : new[] { "StopRain", "EndRain", "RainStop", "StopRaining" };
            foreach (var name in candidates)
            {
                try
                {
                    var m = AccessTools.Method(_rainHelper, name);
                    if (m != null && m.IsStatic && m.GetParameters().Length == 0)
                    {
                        m.Invoke(null, null);
                        Plugin.Logger.LogInfo($"[Weather] RainHelper.{name}() invoked (→ {(on ? "rain" : "dry")}).");
                        return;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Weather] {name}: {ex.InnerException?.Message ?? ex.Message}"); }
            }
            if (!_surfaceDumped)
            {
                _surfaceDumped = true;
                try
                {
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var m in _rainHelper.GetMethods(System.Reflection.BindingFlags.Public
                                                           | System.Reflection.BindingFlags.NonPublic
                                                           | System.Reflection.BindingFlags.Static))
                        if (m.DeclaringType == _rainHelper) names.Add($"{m.Name}/{m.GetParameters().Length}");
                    Plugin.Logger.LogWarning($"[Weather] no known transition method — RainHelper statics: {string.Join(", ", names)}");
                }
                catch { }
            }
        }
    }
}
