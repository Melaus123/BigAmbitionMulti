#if BAMP_DEV
using System;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>T-31 DEV levers (t-horn).
    /// `horn &lt;seconds&gt;` holds the GAME'S OWN horn on the car this player drives: a CarController.Update postfix sets
    /// input.Horn = true after the game read the key (CarController.cs:322), so the native HornComponent plays the real
    /// sound and every T-31 sender path runs exactly as for a held key. At expiry it writes input.Horn = false once - the
    /// key release - on the car it forced (also when the player left that car meanwhile).
    /// `hornstate` reads back: pick=&lt;first own live car&gt;, own=[vid:ctl=..:src=..:playing=..] (MY live cars: driven by me,
    /// native horn source present, that source actually playing, mod= the captured source a borrower's horn plays on it) and copies=[vid:src=..:playing=..:clip=..] (remote copies).</summary>
    internal static class DevT31
    {
        internal static float HornUntil;
        internal static NWH.VehiclePhysics2.VehicleController? Forced;
        private static string _forcedVid = "";

        internal static string Horn(string arg)
        {
            if (!float.TryParse((arg ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) || s <= 0f || s > 30f)
                return "ERR usage: horn <seconds 0-30>";
            CarController? cc = null;
            try
            {
                var list = Helpers.VehicleHelper.AllPlayerVehicles;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i] as CarController;
                        if (c != null && c.controlledByPlayer && c.vehicleController != null) { cc = c; break; }
                    }
            }
            catch (Exception ex) { return $"ERR horn: {ex.Message}"; }
            if (cc == null) return "ERR not driving a car";
            var hc = VehicleHorns.NativeHorn(cc, out _);
            Forced = cc.vehicleController;
            HornUntil = Time.unscaledTime + s;
            _forcedVid = cc.vehicleInstance != null ? cc.vehicleInstance.id ?? "" : "";
            Plugin.Logger.LogInfo($"[TestDrive] horn: holding the native horn on '{_forcedVid}' for {s:F1}s.");
            return $"OK horn vid={_forcedVid} seconds={s:F1} nativeHorn={(hc != null && hc.source != null)}";
        }

        internal static void Tick(CarController cc)
        {
            if (Forced == null || cc == null || cc.vehicleController != Forced) return;
            if (Time.unscaledTime < HornUntil)
            {
                if (cc.controlledByPlayer) Forced.input.Horn = true;
                return;
            }
            Forced.input.Horn = false;
            Forced = null;
            Plugin.Logger.LogInfo($"[TestDrive] horn released on '{_forcedVid}'.");
        }

        internal static string HornState(string arg)
        {
            var own = new System.Text.StringBuilder();
            string pick = "";
            try
            {
                var list = Helpers.VehicleHelper.AllPlayerVehicles;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var vc = list[i]; var inst = vc != null ? vc.vehicleInstance : null;
                        if (inst == null || string.IsNullOrEmpty(inst.id) || inst.id.StartsWith("BAMP_", StringComparison.Ordinal)) continue;
                        var hc = VehicleHorns.NativeHorn(vc, out _);
                        bool src = hc != null && hc.source != null, playing = src && hc!.source.isPlaying;
                        own.Append(own.Length > 0 ? "," : "").Append(inst.id).Append(":ctl=").Append(vc!.controlledByPlayer)
                           .Append(":src=").Append(src).Append(":playing=").Append(playing).Append(":mod=").Append(VehicleHorns.DevOwnedPlaying(inst.id));
                        if (pick.Length == 0 && vc is CarController && vc.gameObject.activeInHierarchy
                            && !VehicleManager.IsHandCartType(inst.vehicleTypeName.ToString()))
                            pick = inst.id;
                    }
            }
            catch (Exception ex) { return $"ERR hornstate: {ex.Message}"; }
            return $"OK hornstate pick={(pick.Length > 0 ? pick : "-")} own=[{own}] copies=[{VehicleHorns.DevCopies()}]";
        }
    }

    [HarmonyPatch(typeof(CarController), "Update")]
    internal static class Patch_CarController_Update_DevHorn
    {
        static void Postfix(CarController __instance)
        {
            try { DevT31.Tick(__instance); } catch { }
        }
    }
}
#endif
