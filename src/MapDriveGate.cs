using System;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>Map-open drive gate (user-approved 2026-08-18; report 20260818-114209
    /// "going into map while in a car ... moves and usually crashes the car").
    ///
    /// Vanilla's map-open pauses the whole game (CityMap:187) — that pause is the ONLY
    /// thing that ever stopped the map's pan keys from ALSO driving the car: the map
    /// camera pans with PlayerAction.Move (CityMapCam:138) while the vehicle input
    /// provider reads the same physical keys, and MP must suppress the pause (shared
    /// clock).  On-foot movement needs no gate — the map's own NavigationBlocker.Map
    /// already freezes WASD (PlayerController:241-247); only the VEHICLE channel
    /// (a separate blocker set the map never touches) was open.
    ///
    /// Gate: Throttle(), Brakes() and Steering() read 0 while the city map is open
    /// in MP.  Brakes is gated because the brake key doubles as REVERSE — with the
    /// gate off, S panned the map AND drove the car backwards (user rig test
    /// 2026-08-18).  Consequence: no key affects the car at all while the map is
    /// open, including braking; momentum stays the player's own responsibility
    /// (user ruling) and live traffic is untouched.  The map camera reads the Move
    /// action directly, so panning is unaffected by this gate.
    ///
    /// 5-point checklist (2026-08-18): no other mod patch targets this type (grep);
    /// our code never calls it; loader annotation checked on next load; MP-gated
    /// live per call; postfixes, no exception absorption.  The type lives in
    /// ExternalPlugins.dll (byte-scan of Managed) — resolved by scan with an
    /// Assembly.Load fallback; a miss = the loader's BOUND-NOTHING warn plus the
    /// warns below, and the gate is simply off (pre-round behavior).</summary>
    public static class MapDriveGate
    {
        private const string TypeName = "NWH.VehiclePhysics2.Input.InputSystemVehicleInputProvider";

        private static bool Gated => CityMap.IsOpen && (MPServer.IsRunning || MPClient.IsClientInWorld);

        private static Type? _resolved;
        private static Type? Resolve()
        {
            if (_resolved != null) return _resolved;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { _resolved = asm.GetType(TypeName); } catch { }
                if (_resolved != null) return _resolved;
            }
            try { _resolved = System.Reflection.Assembly.Load("ExternalPlugins")?.GetType(TypeName); } catch { }
            return _resolved;
        }

        [HarmonyPatch]
        public static class Patch_VehicleThrottle_MapGate
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var m = Resolve() is Type t ? AccessTools.Method(t, "Throttle", Type.EmptyTypes) : null;
                if (m == null) { Plugin.Logger.LogWarning("[MapGate] Throttle not resolvable — map drive gate OFF (keys can steer the car while the map is open)."); yield break; }
                yield return m;
            }
            static void Postfix(ref float __result) { if (Gated) __result = 0f; }
        }

        [HarmonyPatch]
        public static class Patch_VehicleBrakes_MapGate
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var m = Resolve() is Type t ? AccessTools.Method(t, "Brakes", Type.EmptyTypes) : null;
                if (m == null) { Plugin.Logger.LogWarning("[MapGate] Brakes not resolvable — map drive gate OFF for the brake/reverse key (S can reverse the car while the map is open)."); yield break; }
                yield return m;
            }
            static void Postfix(ref float __result) { if (Gated) __result = 0f; }
        }

        [HarmonyPatch]
        public static class Patch_VehicleSteering_MapGate
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var m = Resolve() is Type t ? AccessTools.Method(t, "Steering", Type.EmptyTypes) : null;
                if (m == null) { Plugin.Logger.LogWarning("[MapGate] Steering not resolvable — map drive gate OFF (keys can steer the car while the map is open)."); yield break; }
                yield return m;
            }
            static void Postfix(ref float __result) { if (Gated) __result = 0f; }
        }
    }
}
