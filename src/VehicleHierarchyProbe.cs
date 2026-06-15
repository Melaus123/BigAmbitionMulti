#if BAMP_DEV
using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigAmbitionsMP
{
    // DIAG:DEVTOOL — vehicle prefab-hierarchy probe (passenger-system authoring aid).
    //   The game has NO passenger seat/door data, so we need to learn whether the vehicle
    //   PREFABS carry named door/seat transforms (→ read door positions for free) or not
    //   (→ derive a passenger-side offset from the bounding box). On the FIRST spawn of each
    //   vehicleTypeName this logs every child transform's name + local position (relative to
    //   the car root) plus the renderer footprint, flagging door/seat/enter-named transforms
    //   with '**'. Passive (no keybind), once per type. #if BAMP_DEV only.
    //   See docs/DIAGNOSTICS.md and docs/PASSENGER-SYSTEM.md.
    internal static class VehicleHierarchyProbe
    {
        private static readonly HashSet<string> _dumped = new();

        internal static void DumpOnce(GameObject vehicle, string typeName)
        {
            if (vehicle == null) return;
            string key = string.IsNullOrEmpty(typeName) ? vehicle.name : typeName;
            if (!_dumped.Add(key)) return;
            try
            {
                var root = vehicle.transform;

                // Footprint from combined renderer bounds → the "derive from bounds" fallback
                // for a passenger-side offset uses half the width if no named doors exist.
                Bounds b = default; bool haveB = false;
                foreach (var r in vehicle.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    if (!haveB) { b = r.bounds; haveB = true; } else b.Encapsulate(r.bounds);
                }
                Vector3 sizeLocal = haveB ? root.InverseTransformVector(b.size) : Vector3.zero;

                var all = vehicle.GetComponentsInChildren<Transform>(true);
                Plugin.Logger.LogInfo(
                    $"[VehProbe] === '{key}' : {all.Length} transforms; footprint(local) ~ " +
                    $"W={Mathf.Abs(sizeLocal.x):F2} H={Mathf.Abs(sizeLocal.y):F2} L={Mathf.Abs(sizeLocal.z):F2} ===");

                int flagged = 0;
                foreach (var t in all)
                {
                    if (t == null) continue;
                    string nm = t.name;
                    bool interesting =
                        nm.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nm.IndexOf("seat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nm.IndexOf("enter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nm.IndexOf("passenger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nm.IndexOf("driver", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (interesting) flagged++;
                    Vector3 lp = root.InverseTransformPoint(t.position);
                    Plugin.Logger.LogInfo(
                        $"[VehProbe] {key} {(interesting ? "**" : "  ")} '{nm}' local=({lp.x:F2},{lp.y:F2},{lp.z:F2})");
                }
                Plugin.Logger.LogInfo(
                    $"[VehProbe] === '{key}' end — {flagged} door/seat/enter-named transform(s) " +
                    $"({(flagged > 0 ? "READ door positions" : "DERIVE from bounds")}) ===");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VehProbe] '{key}': {ex.Message}"); }
        }
    }
}
#endif
