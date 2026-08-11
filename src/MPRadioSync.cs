using System;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Round-227 — building speaker radio: crash fix + full propagation.
    ///
    /// CRASH (rig-reproduced, dump-proven): a business HELPER pressing next-station
    /// while the building sat on the LAST real station spiraled the native
    /// station-cycling mesh to a StackOverflow. Root cause (BuildingRegistration
    /// :595/:604): GetBusinessRadioStation/-Volume return the BUSINESS-TYPE DEFAULT
    /// whenever !RentedByPlayer — so on a helper's machine the mesh's own
    /// "skip the empty station" write was invisible to its next read, forever.
    /// Vanilla can't reach this (only owners see the button); our permission work
    /// exposed it. Fix: the two getters honor our grants — a permitted player reads
    /// the stored values exactly like the owner, on every consumer (playback,
    /// overlay UI, street-side music) at once.
    ///
    /// PROPAGATION (user-directed): station changes and on/off toggles broadcast to
    /// everyone (host-relayed, light dedicated message — never the whole-interior
    /// forward, which could clobber concurrent furniture edits), and the state rides
    /// the interior snapshot so anyone ENTERING the building — or joining later —
    /// inherits it. On/off travels as the SIGN of radioVolume (native convention).
    /// Volume drags debounce 0.8s so a slider pull is one message, not fifty.
    /// </summary>
    internal static class MPRadioSync
    {
        // ── root fix: grant-aware getters ────────────────────────────────────

        [HarmonyPatch(typeof(BuildingRegistration), "GetBusinessRadioStation")]
        public static class Patch_RadioStation_GrantAware
        {
            static void Postfix(BuildingRegistration __instance, ref RadioStation __result)
            {
                try
                {
                    if (__instance.RentedByPlayer) return;
                    string key = GameStateReader.AddressKey(__instance);
                    if (GrantSync.CanEnterGranted(key) || GrantSync.IsHelperBusiness(key))
                        __result = __instance.radioStation;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(BuildingRegistration), "GetBusinessRadioVolume")]
        public static class Patch_RadioVolume_GrantAware
        {
            static void Postfix(BuildingRegistration __instance, ref float __result)
            {
                try
                {
                    if (__instance.RentedByPlayer) return;
                    string key = GameStateReader.AddressKey(__instance);
                    if (GrantSync.CanEnterGranted(key) || GrantSync.IsHelperBusiness(key))
                        __result = __instance.radioVolume;
                }
                catch { }
            }
        }

        // ── change capture: any local press propagates ───────────────────────

        [HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.RadioOverlay), "NextStation")]
        public static class Patch_RadioNext_Propagate { static void Postfix() => SendCurrent("station"); }

        [HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.RadioOverlay), "ToggleRadio")]
        public static class Patch_RadioToggle_Propagate { static void Postfix() => SendCurrent("toggle"); }

        [HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.RadioOverlay), "OnVolumeChanged")]
        public static class Patch_RadioVolume_Propagate
        {
            static void Postfix() { _volDirty = true; _volQuietAt = Time.unscaledTime + 0.8f; }
        }

        private static bool _volDirty; private static float _volQuietAt;

        /// <summary>Main thread, every frame (MPCanvasUI.Update): flush a debounced
        /// volume drag once the slider has been quiet for 0.8s.</summary>
        public static void Tick()
        {
            if (_volDirty && Time.unscaledTime >= _volQuietAt) { _volDirty = false; SendCurrent("volume"); }
        }

        private static void SendCurrent(string why)
        {
            try
            {
                var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                if (reg == null) return;
                var p = new RadioStatePayload
                {
                    AddressKey = GameStateReader.AddressKey(reg),
                    Station    = (int)reg.radioStation,
                    Volume     = reg.radioVolume,
                };
                Plugin.Logger.LogInfo($"[Radio] local {why} → '{p.AddressKey}' station={(RadioStation)p.Station} vol={p.Volume:F2} — propagating.");
                if (MPServer.IsRunning) HostHandle(p, MPConfig.PlayerId);
                else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.RadioState, MPConfig.PlayerId, p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Radio] send: {ex.Message}"); }
        }

        /// <summary>HOST: sanity-check, apply, rebroadcast. Validation is deliberately
        /// shallow — the stakes are which song plays — and the audit line names the
        /// sender for any future dispute.</summary>
        public static void HostHandle(RadioStatePayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                if (p.Station < 0 || p.Station > 32 || float.IsNaN(p.Volume) || Mathf.Abs(p.Volume) > 1.001f)
                {
                    Plugin.Logger.LogWarning($"[Radio] implausible state from '{senderPid}' (station={p.Station} vol={p.Volume}) — dropped.");
                    return;
                }
                Apply(p, $"from '{senderPid}'");
                MPServer.BroadcastAny(MessageEnvelope.Create(MessageType.RadioState, senderPid, p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Radio] host handle: {ex.Message}"); }
        }

        /// <summary>Any machine: land the state on the reg; if the local player is
        /// STANDING in that building, poke the speaker system the way a local press
        /// would (echo of our own change is a no-op re-apply).</summary>
        public static void Apply(RadioStatePayload p, string src)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.AddressKey)) return;
                var reg = GameStatePatcher.FindRegistration(p.AddressKey);
                if (reg == null) return;
                bool stationChanged = (int)reg.radioStation != p.Station;
                bool volumeChanged  = Math.Abs(reg.radioVolume - p.Volume) > 0.001f;
                if (!stationChanged && !volumeChanged) return;
                reg.radioStation = (RadioStation)p.Station;
                reg.radioVolume  = p.Volume;
                Plugin.Logger.LogInfo($"[Radio] applied {src}: '{p.AddressKey}' station={(RadioStation)p.Station} vol={p.Volume:F2}.");
                PokeIfInside(reg, stationChanged);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Radio] apply: {ex.Message}"); }
        }

        /// <summary>Make live audio/UI follow an applied change when we're inside the
        /// building: PlayStation for a station switch (what a local press runs), and
        /// the native volume-changed event for volume/mute (LoudSpeakersManager's
        /// own OnVolumeUpdate is subscribed to it).</summary>
        internal static void PokeIfInside(BuildingRegistration reg, bool stationChanged)
        {
            try
            {
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || bm.buildingRegistration != reg) return;
                if (stationChanged)
                {
                    var mgr = InstanceBehavior<Player.Sound.Radio.LoudSpeakersManager>.Instance;
                    if (mgr != null)
                        AccessTools.Method(typeof(Player.Sound.Radio.LoudSpeakersManager), "PlayStation", new[] { typeof(RadioStation) })
                            ?.Invoke(mgr, new object[] { reg.GetBusinessRadioStation() });
                }
                LoudspeakerController.onBuildingRadioVolumeChanged?.Invoke();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Radio] poke: {ex.Message}"); }
        }

        /// <summary>Interior-snapshot integration: apply the piggybacked radio state
        /// (absent on old-format snapshots: Station -1 / Volume -999).</summary>
        public static void ApplyFromSnapshot(string addressKey, int station, float volume)
        {
            if (station < 0 || volume < -900f) return;
            Apply(new RadioStatePayload { AddressKey = addressKey, Station = station, Volume = volume }, "interior snapshot");
        }
    }
}
