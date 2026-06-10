using System;
using Il2CppInterop.Runtime;
using Helpers;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Ground-truth capture for the open-vehicle rider rendering.  Runs on the
    /// LOCAL player only (works in single-player too): whenever we drive/push a
    /// vehicle, it records the character's offset and yaw RELATIVE TO THE
    /// VEHICLE (running mean per type) plus the animator's non-default
    /// parameters and active state hashes — i.e. exactly what a remote machine
    /// must reproduce.  One play session pushing/riding each vehicle type
    /// yields hardcodable offsets and tells us whether the pose is
    /// parameter-driven (already mirrored to remotes) or not.
    /// "[RideProbe]" lines; sampled at the fleet cadence, logged every ~3s.
    /// </summary>
    public static class MPRideProbe
    {
        private static string  _type = "";
        private static int     _n;
        private static Vector3 _sumOff;
        private static float   _sumYaw;
        private static float   _nextLogAt;
        private static string  _lastAnimSig = "";

        // (F7 spawn-test-row removed 2026-06-10 — capture complete: all three
        //  open types measured offset (0,0,0) / yaw 0; pose = animator bools
        //  HoldingBox/UsingHands/OnScooter, already mirrored.  The passive
        //  Sample() capture below stays for any future vehicle types.)

        public static void Sample(string typeName, Transform vehicle)
        {
            try
            {
                var ch = PlayerHelper.PlayerController?.Character?.transform;
                if (ch == null || vehicle == null) return;

                if (_type != typeName) { _type = typeName; _n = 0; _sumOff = Vector3.zero; _sumYaw = 0f; }
                _sumOff += vehicle.InverseTransformPoint(ch.position);
                _sumYaw += Mathf.DeltaAngle(vehicle.eulerAngles.y, ch.eulerAngles.y);
                _n++;

                float now = Time.unscaledTime;
                if (now < _nextLogAt) return;
                _nextLogAt = now + 3f;

                var m = _sumOff / _n;
                Plugin.Logger.LogInfo($"[RideProbe] '{typeName}' n={_n} meanLocalOffset=({m.x:F2},{m.y:F2},{m.z:F2}) meanYawDelta={_sumYaw / _n:F1}deg");
                LogAnimState(ch);
                LogGeometry(ch, vehicle);
            }
            catch { }
        }

        /// <summary>Where things actually RENDER (transforms lie): the visible
        /// body (skinned-mesh bounds) and visible cart (renderer bounds), both
        /// in vehicle-local space — directly comparable with the observer's
        /// RideDiag geom line.  Plus the rig inventory (Animation Rigging IK
        /// would explain a pose no animator state can produce on the clone).</summary>
        private static void LogGeometry(Transform ch, Transform vehicle)
        {
            try
            {
                var body = BoundsCenter(ch, skinnedOnly: true);
                var cart = BoundsCenter(vehicle, skinnedOnly: false);
                var bOff = body.HasValue ? vehicle.InverseTransformPoint(body.Value) : Vector3.zero;
                var cOff = cart.HasValue ? vehicle.InverseTransformPoint(cart.Value) : Vector3.zero;
                Plugin.Logger.LogInfo($"[RideProbe] geom: bodyInVeh=({bOff.x:F2},{bOff.y:F2},{bOff.z:F2}) cartVisualInVeh=({cOff.x:F2},{cOff.y:F2},{cOff.z:F2})");

                if (!_rigDumped)
                {
                    _rigDumped = true;
                    var sb = new System.Text.StringBuilder("[RideProbe] rig inventory:");
                    var comps = ch.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
                    int rigN = 0, animN = 0;
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        string tn = c.GetIl2CppType().FullName ?? "";
                        if (tn.EndsWith(".Animator")) animN++;
                        if ((tn.Contains("Rig") && !tn.Contains("Rigid")) || tn.Contains("Constraint") || tn.Contains("IK"))
                        {
                            rigN++;
                            if (rigN <= 14)
                            {
                                var cc = c.TryCast<Component>();
                                sb.Append($" {tn.Substring(tn.LastIndexOf('.') + 1)}@'{(cc != null ? cc.gameObject.name : "?")}'");
                            }
                        }
                    }
                    sb.Append($" | animators={animN} rigComps={rigN}");
                    Plugin.Logger.LogInfo(sb.ToString());
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[RideProbe] geom: {ex.Message}"); }
        }
        private static bool _rigDumped;

        internal static Vector3? BoundsCenter(Transform root, bool skinnedOnly)
        {
            try
            {
                var rs = root.GetComponentsInChildren(Il2CppType.Of<Renderer>(), false);
                bool any = false;
                var min = Vector3.positiveInfinity; var max = Vector3.negativeInfinity;
                foreach (var r in rs)
                {
                    var rr = r.TryCast<Renderer>();
                    if (rr == null || !rr.enabled) continue;
                    if (skinnedOnly && rr.TryCast<SkinnedMeshRenderer>() == null) continue;
                    var b = rr.bounds;
                    min = Vector3.Min(min, b.min); max = Vector3.Max(max, b.max);
                    any = true;
                }
                return any ? (min + max) * 0.5f : null;
            }
            catch { return null; }
        }

        private static void LogAnimState(Transform ch)
        {
            try
            {
                var model = ch.Find("Model");
                var animComp = model != null ? model.GetComponent(Il2CppType.Of<Animator>()) : null;
                var anim = animComp != null ? animComp.TryCast<Animator>() : null;
                if (anim == null) return;

                var sb = new System.Text.StringBuilder();
                var ps = anim.parameters;
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Bool:
                            if (anim.GetBool(p.name)) sb.Append(p.name).Append("=T ");
                            break;
                        case AnimatorControllerParameterType.Float:
                            float v = anim.GetFloat(p.name);
                            if (Mathf.Abs(v) > 0.01f) sb.Append(p.name).Append('=').Append(v.ToString("F2")).Append(' ');
                            break;
                        case AnimatorControllerParameterType.Int:
                            int vi = anim.GetInteger(p.name);
                            if (vi != 0) sb.Append(p.name).Append('=').Append(vi).Append(' ');
                            break;
                    }
                }
                for (int l = 0; l < anim.layerCount && l < 4; l++)
                {
                    var st = anim.GetCurrentAnimatorStateInfo(l);
                    sb.Append($"L{l}:0x{st.shortNameHash:X8} ");
                }
                string sig = sb.ToString();
                if (sig != _lastAnimSig)
                {
                    _lastAnimSig = sig;
                    Plugin.Logger.LogInfo($"[RideProbe] anim while driving: {sig}");
                }
            }
            catch { }
        }
    }
}
