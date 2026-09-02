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

        // ── Bug 235855 (2026-09-01, user-approved legs A+B): the speaker manager's stuck pause flag ──
        // Line numbers: 1.0 update of 2026-09-01 (decompile mono-1.0-update0901, LoudSpeakersManager.cs;
        // the older hotfix tree is STALE for this class). SetPause (:164) computes `_isPaused = pause ||
        // IsInPlacementMode || FullMenu.IsOpen || CityMap.IsOpen` (:171) and is re-run only by
        // GlobalEvents.onPause and by the placement start/end handler — which feeds the flag back into
        // itself (SetPause(_isPaused), :189), so once TRUE it can never fall on its own. Vanilla clears it
        // because leaving placement mode un-pauses the game and that un-pause dispatches onPause(false).
        // In MP the entry-side pause is suppressed (Patch_GSC_SetPause_Suppress), so GameSpeedController.Set
        // sees paused false→false and never dispatches: the flag sticks, PlayNextStation (:330) refuses
        // every press, and since the update LateUpdate (:93) also refuses to start the next song — the shop
        // radio goes silent for everyone in the building when the current track ends. Field 235855: host
        // 7/7 and client 25/25 presses dead after a placement, recovered only by a manual pause cycle or a
        // city-map close — the two paths that still fire onPause(false). This delivers the ONE call
        // vanilla's un-pause delivered, at the points MP suppresses it: placement end (leg A, below; the
        // throw path is covered from PlacementBlockerGuard's repair finalizer) and menu close (leg B,
        // Patch_Sfx_MenuCloseRestore). SetPause(false) on the update arms _pendingPlay (no direct UnPause).
        // A genuinely paused game — manual pause, startup hold, or the game's own flag read live — is
        // left alone, exactly like vanilla's wasPausedBeforePlacementMode branch; its eventual un-pause
        // runs Set→SetPauseState→onPause(false) (paused=false is never suppressed) and clears the flag.
        private static System.Reflection.FieldInfo? _fSpeakerPaused, _fSpeakerInit, _fSpeakerAudio;
        private static System.Reflection.MethodInfo? _mSpeakerSetPause;
        private static bool _speakerReflectWarned;
        private static int _speakerClears, _speakerRefusals;   // separate: a refusal streak must not eat the success window

        private static void EnsureSpeakerReflection()
        {
            var t = typeof(Player.Sound.Radio.LoudSpeakersManager);
            _fSpeakerPaused   ??= AccessTools.Field(t, "_isPaused");
            _fSpeakerInit     ??= AccessTools.Field(t, "_speakersInitialized");
            _fSpeakerAudio    ??= AccessTools.Field(t, "audioSource");
            _mSpeakerSetPause ??= AccessTools.Method(t, "SetPause", new[] { typeof(bool) });
        }

        /// <summary>Diagnostic read for the RadioDiag press line — the two gates PlayNextStation checks.
        /// A press that changes nothing now names its own reason in the log.</summary>
        internal static string SpeakerFlagsForDiag()
        {
            try
            {
                EnsureSpeakerReflection();
                var mgr = InstanceBehavior<Player.Sound.Radio.LoudSpeakersManager>.Instance;
                if (mgr == null) return "speakers=none";
                string p = _fSpeakerPaused?.GetValue(mgr)?.ToString() ?? "?";
                string i = _fSpeakerInit?.GetValue(mgr)?.ToString() ?? "?";
                return $"speakerPaused={p} speakersInit={i}";
            }
            catch (Exception ex) { return $"speakers=ERR({ex.GetType().Name})"; }
        }

        /// <summary>Deliver the speaker un-pause vanilla's game un-pause would have delivered. No-op
        /// outside MP, while genuinely paused, before the manager has an audio source, or when the
        /// flag is already clear. SetPause(false) itself re-reads placement/menu/map state live, so it
        /// can only clear the flag when none of them is open.</summary>
        internal static void ReconcileSpeakerPause(string reason)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.InMpGame) return;        // vanilla gets its own onPause(false)
                if (TimeSync.ManualPaused || TimeSync.IsStartupHeld) return;   // genuinely paused (mod intent)
                bool nativePaused;
                try { nativePaused = UI.UIs.Instance?.gameSpeed?.Paused ?? false; }
                catch { nativePaused = true; }                                  // unreadable (pre-load) → fail CLOSED
                if (nativePaused) return;                                       // genuinely paused (game's flag)
                var mgr = InstanceBehavior<Player.Sound.Radio.LoudSpeakersManager>.Instance;
                if (mgr == null) return;
                EnsureSpeakerReflection();
                if (_mSpeakerSetPause == null || _fSpeakerPaused == null)
                {
                    if (!_speakerReflectWarned)
                    {
                        _speakerReflectWarned = true;
                        Plugin.Logger.LogWarning("[Radio] LoudSpeakersManager.SetPause/_isPaused did not resolve — speaker un-pause mirror stands down (next-station stays dead after a placement in MP).");
                    }
                    return;
                }
                // SetPause logs a Unity error when its audio source is null (pre-init) — skip, don't spam.
                if (_fSpeakerAudio != null)
                {
                    var ao = _fSpeakerAudio.GetValue(mgr);
                    if (ao == null || (ao is UnityEngine.Object uo && uo == null)) return;
                }
                bool before = (bool)_fSpeakerPaused.GetValue(mgr);
                if (!before) return;                                            // nothing to reconcile
                _mSpeakerSetPause.Invoke(mgr, new object[] { false });
                bool after = (bool)_fSpeakerPaused.GetValue(mgr);
                if (!after)
                {
                    _speakerClears++;
                    if (_speakerClears <= 5 || _speakerClears % 25 == 0)
                        Plugin.Logger.LogInfo($"[Radio] speaker pause flag cleared on {reason} (#{_speakerClears}) — MP suppresses the native un-pause vanilla used for this.");
                }
                else
                {
                    // A refusal repeats on every placement end / menu close until the standing gate drops — same limiter.
                    _speakerRefusals++;
                    if (_speakerRefusals <= 5 || _speakerRefusals % 25 == 0)
                        Plugin.Logger.LogInfo($"[Radio] speaker pause flag STILL set after {reason} (#{_speakerRefusals}) — placement={BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode} fullMenu={UI.Smartphone.FullMenu.IsOpen} cityMap={CityMap.IsOpen}.");
                }
            }
            catch (Exception ex)
            {
                // A reflection invoke wraps the real error (round-39 lesson, GameStateReader) — surface the inner one.
                var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
                Plugin.Logger.LogWarning($"[Radio] speaker pause reconcile ({reason}): {inner.GetType().Name}: {inner.Message}");
            }
        }

        /// <summary>Leg A — placement end. A postfix runs after StopPlacingItem (IsInPlacementMode is
        /// false again), after the native placement-end handler re-armed the flag, and after the
        /// un-pause MP turned into a no-op — the exact point vanilla's onPause(false) arrived. A postfix
        /// is skipped when the original throws; that path is covered from the repair finalizer in
        /// PlacementBlockerGuard (review MAJOR-2), which runs the same reconcile after its teardown.</summary>
        [HarmonyPatch(typeof(Buildings.Indoors.InteriorDesign.PlacementHelper),
                      nameof(Buildings.Indoors.InteriorDesign.PlacementHelper.CancelPlacementMode))]
        public static class Patch_PlacementEnd_SpeakerUnpause
        {
            static void Postfix() => ReconcileSpeakerPause("placement end");
        }
    }
}
