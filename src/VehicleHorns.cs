using System;
using System.Collections.Generic;
using HarmonyLib;
using NWH.VehiclePhysics2.Sound.SoundComponents;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace BigAmbitionsMP
{
    /// <summary>T-31 (2026-11-13): other players hear a player's car horn.
    /// Natively the horn is CarController.Update (:322) writing input.Horn from the held key, and NWH's
    /// HornComponent.VC_Update (:18-33) playing a LOOPING AudioSource while it is set. Remote copies are stripped
    /// of the NWH controller (StripVehicleComponents), so they have no horn source of their own.
    /// Design (NWH's own multiplayer hook carries the horn as a state flag - VehicleController.cs:409/430):
    ///  - SENDER: the horn flag rides the regular vehicle streams (VehicleEntry.Horn in the fleet, VehicleDrivePayload.Horn
    ///    from a borrower), decided from the REAL fact - the native horn AudioSource playing - plus a latch ("sounded
    ///    since the last send", fed by a HornComponent.VC_Update postfix) so a tap shorter than one send still goes out.
    ///    The owner sends it only while the car is in use (driven by the owner, or followed for a borrower).
    ///  - CAPTURE: a remote copy gets, BEFORE the strip, a child "BAMP_Horn" with its own looping AudioSource carrying the
    ///    native horn's clip, mixer group, spatial blend, doppler, volume, rolloff/min/max distance and priority.
    ///  - APPLIER: the fleet apply plays/stops it from the flag; the owner applies a borrower's flag on its REAL car through
    ///    a captured source too - the native horn cannot sound there: a car its owner does not drive has its NWH controller
    ///    disabled (CarController.cs:464/606), so HornComponent never updates and its source is disabled (SoundComponent.cs:133-138).
    ///    While that source plays, the owner's fleet entry carries Horn=true, so third players hear a borrowed car too.
    ///  - FORCE OFF: no flag-true packet for &gt; 1 s, the copy disabled/despawned, a borrow released or timed out, scene unload.</summary>
    internal static class VehicleHorns
    {
        internal const float StallSeconds = 1f;
        private const int MaxEdgeLines = 400;

        private static readonly HashSet<int> _soundedSinceSend = new();            // NWH controller instance ids
        private static readonly Dictionary<string, bool> _lastSent = new();          // vid -> last flag sent (edge log)
        private static readonly Dictionary<string, RemoteHorn> _copies = new();      // remote copies, by real vehicle id
        private static readonly Dictionary<string, RemoteHorn> _owned = new();       // MY cars a borrower honks, by vehicle id
        private static readonly Dictionary<string, (GameObject go, string why)> _silentCopies = new();   // copies with no horn (DEV readout)
        private static readonly HashSet<string> _noHornLogged = new();               // per model
        private static int _edgeLines;
        private static bool _sceneHooked;

        // ── SENDER ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>The native horn component of a game vehicle (null when it is not an NWH car).</summary>
        internal static HornComponent? NativeHorn(VehicleController? vc, out NWH.VehiclePhysics2.VehicleController? nwh)
        {
            nwh = null;
            var cc = vc as CarController;
            if (cc == null || cc.vehicleController == null) return null;
            nwh = cc.vehicleController;
            var sm = nwh.soundManager;
            return sm != null ? sm.hornComponent : null;
        }

        /// <summary>HornComponent.VC_Update postfix: remember that this car's horn sounded since the last send.</summary>
        internal static void NoteSounded(HornComponent h, NWH.VehiclePhysics2.VehicleController nwh)
        {
            if (h == null || nwh == null || h.source == null || !h.source.isPlaying) return;
            _soundedSinceSend.Add(nwh.GetInstanceID());
        }

        /// <summary>The horn flag to send for this vehicle: its native horn source playing now, or sounded since the
        /// last send. Always consumes the latch; false while the car is not in use (inUse=false).</summary>
        internal static bool SenderHorn(VehicleController? vc, string vid, bool inUse, string role)
        {
            bool on = false;
            try
            {
                var hc = NativeHorn(vc, out var nwh);
                if (nwh == null) return false;
                bool latched = _soundedSinceSend.Remove(nwh.GetInstanceID());
                bool playing = hc != null && hc.source != null && hc.source.isPlaying;
                bool applied = !string.IsNullOrEmpty(vid) && _owned.TryGetValue(vid, out var ow) && ow != null && ow.Playing;   // a borrower's horn on MY car
                on = inUse && (playing || latched || applied);
            }
            catch { on = false; }
            if (!string.IsNullOrEmpty(vid))
            {
                bool was = _lastSent.TryGetValue(vid, out var w) && w;
                if (was != on)
                {
                    _lastSent[vid] = on;
                    Edge($"[Horn] send '{vid}' {(on ? "on" : "off")} role={role}");
                }
            }
            return on;
        }

        /// <summary>Borrower side (TickDriveSync): the horn flag of the proxy at this pose.</summary>
        internal static bool SenderHornOf(Transform? pose, string vid)
        {
            if (pose == null) return false;
            VehicleController? vc = null;
            try { vc = pose.GetComponent<VehicleController>(); } catch { }
            return SenderHorn(vc, vid, true, "borrower");
        }

        /// <summary>Borrower release: drop the proxy's latch, so a honk in the last send window does not ride the next borrow.</summary>
        internal static void ClearLatchOf(Transform? ghost)
        {
            if (ghost == null) return;
            try { NativeHorn(ghost.GetComponentInChildren<VehicleController>(), out var nwh); if (nwh != null) _soundedSinceSend.Remove(nwh.GetInstanceID()); } catch { }
        }

        // ── CAPTURE ────────────────────────────────────────────────────────────────────────────────

        /// <summary>SpawnRemoteVehicle, BEFORE StripVehicleComponents: give the copy its own horn source built from the
        /// native horn's settings. No clip on the model = no horn (logged once per model; never an invented sound).</summary>
        internal static void CaptureOnCopy(GameObject go, VehicleController vc, string vid, string ownerId, string typeName)
        {
            if (go == null || string.IsNullOrEmpty(vid)) return;
            try
            {
                var rh = BuildSource(go, vc, vid, ownerId, "copy", typeName, out var why);
                if (rh == null) { _silentCopies[vid] = (go, why); return; }
                _silentCopies.Remove(vid);
                _copies[vid] = rh;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Horn] capture '{vid}': {ex.Message}"); }
        }

        private static RemoteHorn? BuildSource(GameObject host, VehicleController vc, string vid, string who, string role, string typeName, out string why)
        {
            why = "";
            var hc = NativeHorn(vc, out var nwh);
            var sm = nwh != null ? nwh.soundManager : null;
            AudioClip? clip = null;
            try { if (hc != null) clip = hc.Clip; } catch { }
            if (hc == null || sm == null || clip == null)
            {
                why = vc is CarController ? (hc == null ? "no NWH horn component" : "no horn clip") : "not a car";
                if (_noHornLogged.Add(typeName ?? ""))
                    Plugin.Logger.LogInfo($"[Horn] model '{typeName}' has no horn ({why}) - its copies stay silent.");
                return null;
            }
            var child = new GameObject("BAMP_Horn");
            child.transform.SetParent(host.transform, false);
            // Where the native source sits: SoundManager's "OtherAudioSources" GO, created at (0, 0.2, 0) on the NWH controller.
            Vector3 world = sm.otherSourceGO != null ? sm.otherSourceGO.transform.position : nwh!.transform.TransformPoint(new Vector3(0f, 0.2f, 0f));
            child.transform.position = world;
            var src = child.AddComponent<AudioSource>();
            var ns = hc.source;   // null until the native component initialised
            src.playOnAwake = false;
            src.loop = true;                                                    // HornComponent.InitLoop
            src.clip = clip;
            src.outputAudioMixerGroup = ns != null ? ns.outputAudioMixerGroup : OtherGroup(sm);   // HornComponent.AudioMixerGroup
            src.spatialBlend = ns != null ? ns.spatialBlend : sm.spatialBlend;
            src.dopplerLevel = ns != null ? ns.dopplerLevel : sm.dopplerLevel;
            src.volume = hc.baseVolume * sm.masterVolume;                       // what VC_Update's SetVolume(baseVolume) sets while honking
            src.priority = hc.Priority;                                         // 200
            if (ns != null)
            {
                src.pitch = ns.pitch;
                src.rolloffMode = ns.rolloffMode;
                src.minDistance = ns.minDistance;
                src.maxDistance = ns.maxDistance;
                src.spread = ns.spread;
                if (ns.rolloffMode == AudioRolloffMode.Custom)
                    src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, ns.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            }
            // ns == null: the native source would be a fresh AddComponent<AudioSource> too - Unity's defaults are its defaults.
            var rh = child.AddComponent<RemoteHorn>();
            rh.Vid = vid; rh.Who = who; rh.Role = role; rh.Src = src; rh.ClipName = clip.name;
            HookScene();
            return rh;
        }

        /// <summary>The group NWH routes the horn to when it initialises (SoundManager.cs:157-167: "Other" of its mixer), with the
        /// mixer the game assigns at runtime (CarController.cs:141, GlobalReferences.vehicleAudioMixer - the one the vehicle volume
        /// option drives, Options.cs:463). Used when the copy was built before NWH initialised its own source.</summary>
        private static AudioMixerGroup? OtherGroup(NWH.VehiclePhysics2.Sound.SoundManager sm)
        {
            AudioMixer? m = null;
            try { var gr = InstanceBehavior<GlobalReferences>.Instance; if (gr != null) m = gr.vehicleAudioMixer; } catch { }
            if (m == null) m = sm.mixer;
            try { if (m != null) { var g = m.FindMatchingGroups("Other"); if (g != null && g.Length > 0) return g[0]; } } catch { }
            return sm.otherMixerGroup;
        }

        // ── APPLIER ────────────────────────────────────────────────────────────────────────────────

        /// <summary>Fleet apply: play/stop a remote copy's horn from the owner's flag.</summary>
        internal static void ApplyCopy(string vid, bool on, string why)
        {
            if (string.IsNullOrEmpty(vid) || !_copies.TryGetValue(vid, out var rh) || rh == null) return;
            rh.Want(on, why);
        }

        /// <summary>Owner side (ApplyDriveSync): a borrower's horn on MY real car, through a captured source (the native horn
        /// is disabled on a car its owner does not drive - see the class summary).</summary>
        internal static void ApplyOwned(GameObject ownedGo, string vid, string driverId, bool on)
        {
            if (ownedGo == null || string.IsNullOrEmpty(vid)) return;
            try
            {
                if (!_owned.TryGetValue(vid, out var rh) || rh == null)
                {
                    if (!on) return;
                    var vc = ownedGo.GetComponentInChildren<VehicleController>();
                    string tn = ""; try { tn = vc != null && vc.vehicleInstance != null ? vc.vehicleInstance.vehicleTypeName.ToString() : ""; } catch { }
                    rh = vc != null ? BuildSource(ownedGo, vc, vid, driverId, "own car", tn, out _) : null;
                    if (rh == null) return;
                    _owned[vid] = rh;
                }
                rh.Who = driverId;
                rh.Want(on, "packet");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Horn] owned apply '{vid}': {ex.Message}"); }
        }

        /// <summary>ReleaseOwnedFollow (release or borrow timeout): silence MY car and drop its horn driver.</summary>
        internal static void ReleaseOwned(string vid, string why)
        {
            if (string.IsNullOrEmpty(vid) || !_owned.TryGetValue(vid, out var rh)) return;
            _owned.Remove(vid);
            if (rh == null) return;
            try { rh.Want(false, why); } catch { }
            try { UnityEngine.Object.Destroy(rh.gameObject); } catch { }
        }

        internal static void Forget(RemoteHorn rh)
        {
            if (rh == null) return;
            if (_copies.TryGetValue(rh.Vid, out var c) && ReferenceEquals(c, rh)) _copies.Remove(rh.Vid);
            if (_owned.TryGetValue(rh.Vid, out var o) && ReferenceEquals(o, rh)) _owned.Remove(rh.Vid);
        }

        private static void HookScene()
        {
            if (_sceneHooked) return;
            _sceneHooked = true;
            SceneManager.sceneUnloaded += _ =>
            {
                foreach (var rh in new List<RemoteHorn>(_copies.Values)) try { if (rh != null) rh.Want(false, "scene unload"); } catch { }
                foreach (var rh in new List<RemoteHorn>(_owned.Values)) try { if (rh != null) rh.Want(false, "scene unload"); } catch { }
            };
        }

        internal static void Edge(string line)
        {
            if (_edgeLines >= MaxEdgeLines) return;
            _edgeLines++;
            Plugin.Logger.LogInfo(_edgeLines < MaxEdgeLines ? line : line + " (further [Horn] edge lines suppressed this session)");
        }

#if BAMP_DEV
        /// <summary>DEV readout (hornstate): every remote copy as vid:src=..:playing=..:clip=..</summary>
        internal static string DevCopies()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _copies)
            {
                var rh = kv.Value; if (rh == null) continue;
                bool playing = false; try { playing = rh.Src != null && rh.Src.isPlaying; } catch { }
                sb.Append(sb.Length > 0 ? "," : "").Append(kv.Key).Append(":src=True:playing=").Append(playing).Append(":clip=").Append(rh.ClipName);
            }
            foreach (var kv in _silentCopies)
            {
                if (kv.Value.go == null) continue;   // despawned
                sb.Append(sb.Length > 0 ? "," : "").Append(kv.Key).Append(":src=False:playing=False:clip=(").Append(kv.Value.why.Replace(' ', '_')).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>DEV readout: the captured horn source on MY car (a borrower's horn) is actually playing.</summary>
        internal static bool DevOwnedPlaying(string vid)
            => _owned.TryGetValue(vid, out var rh) && rh != null && rh.Src != null && rh.Src.isPlaying;
#endif
    }

    /// <summary>T-31: one car's remote horn - a captured looping source on a copy, or on MY real car for a borrower.</summary>
    internal sealed class RemoteHorn : MonoBehaviour
    {
        internal string Vid = "", Who = "", Role = "", ClipName = "";
        internal AudioSource? Src;
        internal bool Playing;
        private float _lastOnAt;

        internal void Want(bool on, string why)
        {
            if (on)
            {
                if (!isActiveAndEnabled) return;   // a hidden copy stays silent; the next packet after it shows starts it
                _lastOnAt = Time.unscaledTime;
                if (!Playing) Begin();
            }
            else if (Playing) End(why);
        }

        private void Update()
        {
            if (Playing && Time.unscaledTime - _lastOnAt > VehicleHorns.StallSeconds) End("no packet for > 1 s");
        }

        private void OnDisable() { if (Playing) End("disabled/despawned"); }

        private void OnDestroy() { if (Playing) End("destroyed"); VehicleHorns.Forget(this); }

        private void Begin()
        {
            Playing = true;
            try { if (Src != null) Src.Play(); } catch { }
            VehicleHorns.Edge($"[Horn] start '{Vid}' {Role} ({(Role == "copy" ? "owner" : "driver")} '{Who}') clip='{ClipName}'");
        }

        private void End(string why)
        {
            Playing = false;
            try { if (Src != null) Src.Stop(); } catch { }
            VehicleHorns.Edge($"[Horn] stop '{Vid}' {Role} ({why})");
        }
    }

    /// <summary>T-31 sender latch: after the native horn's own update, note a car whose horn sounded this frame.</summary>
    [HarmonyPatch(typeof(HornComponent), nameof(HornComponent.VC_Update))]
    internal static class Patch_HornComponent_VC_Update_Latch
    {
        static void Postfix(HornComponent __instance)
        {
            try { if (__instance != null) VehicleHorns.NoteSounded(__instance, __instance.vehicleController); } catch { }
        }
    }
}
