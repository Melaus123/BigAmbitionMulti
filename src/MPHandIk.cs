using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Hand-IK mirroring for open-vehicle pushing.  The game glues the pusher's
    /// hands to the cart handles with Animation Rigging TwoBoneIKConstraints
    /// (LHandConstraint/RHandConstraint under the character Model) — component
    /// state, invisible to the animator sync.  The clone carries the SAME rig
    /// (Instantiate keeps Unity package components; script-stripping only kills
    /// game scripts), so: the sender ships its IK target positions in
    /// VEHICLE-LOCAL space + rig weights each tick, and the observer drives the
    /// clone's identical constraints to the same spots on the ghost cart — the
    /// clone's own RigBuilder solves the arms.
    /// PlayerPositionPayload.IkT packing: [Lx,Ly,Lz, Rx,Ry,Rz, Lweight,Rweight].
    /// </summary>
    public static class MPHandIk
    {
        public sealed class Refs
        {
            public Transform?    LTarget, RTarget;
            public object?       LRig, RRig;
            public PropertyInfo? WeightProp;
        }

        private static Refs? _local;          // local player's rig (sender side)
        private static bool  _localSearched;
        private static bool  _loggedFill;

        public static void Reset() { _local = null; _localSearched = false; _loggedFill = false; }

        /// <summary>Discover the hand-IK pieces under a character root — works
        /// for the local player and for clones (identical hierarchy).</summary>
        public static Refs? Discover(Transform root, string tag)
        {
            try
            {
                var refs = new Refs();
                foreach (var c in root.GetComponentsInChildren(Il2CppType.Of<Component>(), true))
                {
                    if (c == null) continue;
                    var it = c.GetIl2CppType();
                    if (it.Name == "TwoBoneIKConstraint")
                    {
                        var mt = VehicleManager.FindGameType(it.FullName ?? "");
                        if (mt == null) continue;
                        var wrap   = Activator.CreateInstance(mt, c.Pointer);
                        var data   = mt.GetProperty("data", BindingFlags.Public | BindingFlags.Instance)?.GetValue(wrap);
                        var target = data?.GetType().GetProperty("target", BindingFlags.Public | BindingFlags.Instance)?.GetValue(data) as Transform;
                        if (target == null) continue;
                        string go = c.TryCast<Component>()?.gameObject?.name ?? "";
                        if (go.StartsWith("L")) refs.LTarget = target;
                        else if (go.StartsWith("R")) refs.RTarget = target;
                    }
                    else if (it.Name == "Rig")
                    {
                        string go = c.TryCast<Component>()?.gameObject?.name ?? "";
                        if (!go.Contains("HandIKRig")) continue;
                        var mt = VehicleManager.FindGameType(it.FullName ?? "");
                        if (mt == null) continue;
                        var wrap = Activator.CreateInstance(mt, c.Pointer);
                        refs.WeightProp ??= mt.GetProperty("weight", BindingFlags.Public | BindingFlags.Instance);
                        if (go.StartsWith("L")) refs.LRig = wrap;
                        else if (go.StartsWith("R")) refs.RRig = wrap;
                    }
                }
                bool ok = refs.LTarget != null && refs.RTarget != null && refs.WeightProp != null;
                Plugin.Logger.LogInfo($"[HandIK] discover({tag}): Ltarget={(refs.LTarget != null ? refs.LTarget.name : "MISS")} Rtarget={(refs.RTarget != null ? refs.RTarget.name : "MISS")} rigs={(refs.LRig != null ? 1 : 0)}+{(refs.RRig != null ? 1 : 0)} weightProp={refs.WeightProp != null} → {(ok ? "OK" : "INCOMPLETE")}");
                return ok ? refs : null;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[HandIK] discover({tag}): {ex.Message}");
                return null;
            }
        }

        /// <summary>Sender: append vehicle-local IK targets + rig weights to the
        /// outgoing position payload (only while driving an open vehicle).</summary>
        public static void FillPayload(PlayerPositionPayload p)
        {
            try
            {
                var veh = VehicleManager.CurrentOpenDriven;
                if (veh == null) return;
                if (!_localSearched)
                {
                    _localSearched = true;
                    var ch = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                    if (ch != null) _local = Discover(ch, "local");
                }
                var r = _local;
                if (r == null || r.LTarget == null || r.RTarget == null) return;

                var l  = veh.InverseTransformPoint(r.LTarget.position);
                var rr = veh.InverseTransformPoint(r.RTarget.position);
                float lw = ReadWeight(r, r.LRig), rw = ReadWeight(r, r.RRig);
                p.IkT.Add(l.x);  p.IkT.Add(l.y);  p.IkT.Add(l.z);
                p.IkT.Add(rr.x); p.IkT.Add(rr.y); p.IkT.Add(rr.z);
                p.IkT.Add(lw);   p.IkT.Add(rw);

                if (!_loggedFill)
                {
                    _loggedFill = true;
                    Plugin.Logger.LogInfo($"[HandIK] sending: L=({l.x:F2},{l.y:F2},{l.z:F2}) R=({rr.x:F2},{rr.y:F2},{rr.z:F2}) w=({lw:F2},{rw:F2})");
                }
            }
            catch { }
        }

        private static float ReadWeight(Refs r, object? rig)
        {
            try { return rig != null && r.WeightProp != null ? (float)(r.WeightProp.GetValue(rig) ?? 0f) : 0f; }
            catch { return 0f; }
        }

        public static void WriteWeight(Refs r, object? rig, float w)
        {
            try { if (rig != null && r.WeightProp != null && r.WeightProp.CanWrite) r.WeightProp.SetValue(rig, w); }
            catch { }
        }
    }
}
