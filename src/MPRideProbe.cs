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
            }
            catch { }
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
