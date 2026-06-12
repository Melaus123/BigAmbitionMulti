using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Hand-IK mirroring for open-vehicle pushing — SAFE implementation.
    ///
    /// v1 reflected into Animation Rigging's TwoBoneIKConstraint.data — that
    /// property returns a GENERIC IL2CPP STRUCT, and boxing one through
    /// reflection hard-faults the process (host crash on flatbed mount,
    /// 2026-06-10).  Never reflect generic interop struct properties.
    ///
    /// v2 uses only standard humanoid-Animator APIs:
    ///   * Sender: reads its HAND BONE world positions (while pushing, the
    ///     game's own IK has glued them to the cart handles) in VEHICLE-local
    ///     space → PlayerPositionPayload.IkT = [Lxyz, Rxyz, 1, 1].
    ///   * Observer: manual two-bone IK on the clone's arm bones in
    ///     LateUpdate (after animation), aiming the hands at the same
    ///     cart-relative points on the ghost.
    /// </summary>
    public static class MPHandIk
    {
        private static Animator? _localAnim;
        private static bool _searched;
        private static bool _loggedFill;

        public static void Reset() { _localAnim = null; _searched = false; _loggedFill = false; }

        /// <summary>Sender: append anchor-local hand-bone positions to the
        /// outgoing position payload.  Anchor space = the driven open vehicle
        /// (cart pushing), else the HandContent bone while a prop is held
        /// (box/basket carry — 2026-06-12).  Either way the observer's manual
        /// solve reproduces the holder's REAL arm pose, whatever mechanism
        /// produced it locally.</summary>
        public static void FillPayload(PlayerPositionPayload p)
        {
            try
            {
                Transform? veh = VehicleManager.CurrentOpenDriven;
                if (veh == null && !string.IsNullOrEmpty(p.Held))
                    veh = RemotePlayerManager.LocalHandContent;   // held-item anchor space
                if (veh == null) return;
                if (!_searched)
                {
                    _searched = true;
                    var ch = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                    var model = ch != null ? ch.Find("Model") : null;
                    var ac = model != null ? model.GetComponent(Il2CppType.Of<Animator>()) : null;
                    _localAnim = ac != null ? ac.TryCast<Animator>() : null;
                    Plugin.Logger.LogInfo($"[HandIK] local animator: {(_localAnim != null ? $"ok, human={_localAnim.isHuman}" : "MISS")}");
                }
                var anim = _localAnim;
                if (anim == null || !anim.isHuman) return;

                var lh = anim.GetBoneTransform(HumanBodyBones.LeftHand);
                var rh = anim.GetBoneTransform(HumanBodyBones.RightHand);
                if (lh == null || rh == null) return;
                var l = veh.InverseTransformPoint(lh.position);
                var r = veh.InverseTransformPoint(rh.position);
                p.IkT.Add(l.x); p.IkT.Add(l.y); p.IkT.Add(l.z);
                p.IkT.Add(r.x); p.IkT.Add(r.y); p.IkT.Add(r.z);
                p.IkT.Add(1f);  p.IkT.Add(1f);

                if (!_loggedFill)
                {
                    _loggedFill = true;
                    Plugin.Logger.LogInfo($"[HandIK] sending hand anchors: L=({l.x:F2},{l.y:F2},{l.z:F2}) R=({r.x:F2},{r.y:F2},{r.z:F2})");
                }
            }
            catch { }
        }

        /// <summary>Observer: classic two-bone IK — rotate upper arm and forearm
        /// so the hand lands on the target; elbow bends along the pole side.
        /// Call in LateUpdate so it overrides the frame's animation pose.</summary>
        public static void SolveArm(Animator anim, bool left, Vector3 target, Vector3 poleDir)
        {
            var upper = anim.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var lower = anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var hand  = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand    : HumanBodyBones.RightHand);
            if (upper == null || lower == null || hand == null) return;

            float a = (lower.position - upper.position).magnitude;
            float b = (hand.position  - lower.position).magnitude;
            Vector3 toT = target - upper.position;
            if (toT.sqrMagnitude < 1e-6f || a < 1e-4f || b < 1e-4f) return;
            float d = Mathf.Clamp(toT.magnitude, 0.05f, a + b - 0.01f);
            Vector3 dir = toT.normalized;

            Vector3 bendNormal = Vector3.Cross(dir, poleDir);
            if (bendNormal.sqrMagnitude < 1e-6f) bendNormal = Vector3.Cross(dir, Vector3.up);
            bendNormal.Normalize();

            // Law of cosines: angle at the shoulder between target-line and upper arm.
            float cosU = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float angU = Mathf.Acos(cosU) * Mathf.Rad2Deg;

            Vector3 wantUpperDir = Quaternion.AngleAxis(angU, bendNormal) * dir;
            upper.rotation = Quaternion.FromToRotation(lower.position - upper.position, wantUpperDir) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, target - lower.position) * lower.rotation;
        }
    }
}
