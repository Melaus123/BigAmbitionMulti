using System;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    // PROBE-START: P-CARCLICK (the whole file is the probe; delete the file to remove)
    /// <summary>P-CARCLICK — four log-only fields riding the native [CarClickBlocked] diagnostic
    /// (field 20260830-173306, 3rd field recurrence of the class, zero fix attempts;
    /// user-approved 2026-09-01). The native instrument cannot separate a NEAR-MISS (ray clipped
    /// the bounding box but missed the body) from the VERIFIED native selection gap: car
    /// colliders live on CHILD objects while the real click path (MouseController.Run :168) is
    /// TryGetComponent&lt;EntityController&gt; on the EXACT object the ray hit — no parent walk —
    /// and the diagnostic's own success test (VehicleClickBlockedDiagnostic :39) is
    /// GetComponentInParent, a parent walk, so it scores exactly those failed clicks as
    /// successes (F-2026-09-01-B, all four legs read-verified). Per failure event this adds:
    ///  1. identity + aim quality — vehicle instance id ("BAMP_" prefix = another player's
    ///     ghost) + world position + the ray's closest approach to the bounds centre as a
    ///     fraction of the half-extent (Chebyshev, so ~0% = dead-centre aim that STILL failed =
    ///     pathological; ~100% = edge clip = ordinary near-miss);
    ///  2. a collider census of the aimed vehicle — per collider: path, type,
    ///     enabled/isTrigger/layer, and whether an EntityController sits on the SAME GameObject
    ///     (the only arrangement the real click path can select);
    ///  3. the same ray re-run WITH triggers — the real click honours the physics default
    ///     queriesHitTriggers=true while the diagnostic passes Ignore, so this names what the
    ///     REAL click actually saw first on the ray;
    ///  4. companion, NEXT FRAME — MouseController.currentTargetEntity: converts ray geometry
    ///     into the player-visible truth (did the click select the car or not) — the one thing
    ///     the native instrument never records.
    /// Log-only; rides the native 2-second throttle (the postfix only runs when the native
    /// failure line prints); capped per launch. Deliberately NOT MP-gated — the pending
    /// single-player rig census (queued test) must log too, and the native diagnostic itself
    /// is not MP-gated. Retire when the class is root-caused (or closed as vanilla with
    /// evidence).</summary>
    public static class CarClickProbe
    {
        private const int MaxEventsPerLaunch = 40;   // native 2s throttle bounds the rate; this bounds a marathon
        private static int _events;
        private static LayerMask _mask = -1;         // stashed by the OnInteractionAttempt prefix (same call stack)
        private static int _pendingFrame = -1;       // companion arm: log currentTargetEntity on a LATER frame
        private static string _pendingVehicle = "";

        /// <summary>Stash the interaction mask — LogFailure does not receive it, and the field
        /// it comes from (MouseController.RaycastLayerMark) is private. One assignment per
        /// mouse-down; the prefix and LogFailure share the same call stack, so the stash is
        /// always fresh when the postfix reads it.</summary>
        [HarmonyPatch(typeof(Helpers.VehicleClickBlockedDiagnostic),
                      nameof(Helpers.VehicleClickBlockedDiagnostic.OnInteractionAttempt))]
        public static class Patch_CarClickProbe_StashMask
        {
            static void Prefix(LayerMask interactionMask) { _mask = interactionMask; }
        }

        /// <summary>The three per-event fields, appended right after the native warning.
        /// LogFailure is PRIVATE and its aim parameter is a PRIVATE struct — resolved via
        /// TargetMethods (yield nothing on a miss: the probe goes dead, it never trips the
        /// player-visible patch-degraded notice) and read via a boxed object + Traverse.</summary>
        [HarmonyPatch]
        public static class Patch_CarClickProbe_OnFailure
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var m = AccessTools.Method(typeof(Helpers.VehicleClickBlockedDiagnostic), "LogFailure");
                if (m == null)
                    Plugin.Logger.LogWarning("[PROBE] CarClick: VehicleClickBlockedDiagnostic.LogFailure did not resolve — probe dead (game update renamed it?).");
                else
                    yield return m;
            }

            static void Postfix(Ray __0, object __1, bool __2, RaycastHit __3)
            {
                try
                {
                    if (_events >= MaxEventsPerLaunch) return;
                    _events++;
                    var vc = Traverse.Create(__1).Property("Vehicle").GetValue<VehicleController>();
                    if (vc == null) { Plugin.Logger.LogInfo("[PROBE] CarClick: event with null vehicle (aim struct read failed?)."); return; }

                    var sb = new System.Text.StringBuilder();

                    // ── Field 1: identity + aim quality ─────────────────────────────────────
                    string vid = "?";
                    try { vid = vc.vehicleInstance?.id ?? "(no instance — ghost?)"; } catch { }
                    var col = vc.vehicleCollider;
                    string aimFrac = "n/a";
                    if (col != null)
                    {
                        var b = col.bounds;
                        var dir = __0.direction.normalized;
                        float t = Mathf.Max(0f, Vector3.Dot(b.center - __0.origin, dir));
                        var off = __0.origin + dir * t - b.center;
                        float fx = b.extents.x > 0.001f ? Mathf.Abs(off.x) / b.extents.x : 0f;
                        float fy = b.extents.y > 0.001f ? Mathf.Abs(off.y) / b.extents.y : 0f;
                        float fz = b.extents.z > 0.001f ? Mathf.Abs(off.z) / b.extents.z : 0f;
                        aimFrac = $"{Mathf.Max(fx, Mathf.Max(fy, fz)) * 100f:F0}%";
                    }
                    var p = vc.transform.position;
                    sb.AppendLine($"[PROBE] CarClick #{_events} '{vc.name}' id='{vid}' pos=({p.x:F1},{p.y:F1},{p.z:F1}) aimFrac={aimFrac} (closest approach / half-extent; <20% dead-centre, ~100% edge clip)");

                    // ── Field 2: collider census of the aimed vehicle ───────────────────────
                    var cols = vc.GetComponentsInChildren<Collider>(true);
                    int shown = 0;
                    foreach (var c in cols)
                    {
                        if (c == null) continue;
                        if (shown++ >= 16) { sb.AppendLine($"  census: ... {cols.Length - 16} more collider(s) elided"); break; }
                        bool sameGoEc = c.GetComponent<EntityController>() != null;
                        sb.AppendLine($"  census: '{RelPath(vc.transform, c.transform)}' {c.GetType().Name} enabled={c.enabled} isTrigger={c.isTrigger} layer={LayerMask.LayerToName(c.gameObject.layer)} ecOnSameGO={sameGoEc}{(ReferenceEquals(c, col) ? " [diagnostic's vehicleCollider]" : "")}");
                    }
                    if (shown == 0) sb.AppendLine("  census: NO colliders under the vehicle at all");

                    // ── Field 3: the same ray, but seeing triggers like the real click does ──
                    if (Physics.Raycast(__0, out var hitT, 600f, _mask, QueryTriggerInteraction.Collide))
                    {
                        bool ecExact = hitT.transform.GetComponent<EntityController>() != null;
                        sb.AppendLine($"  withTriggers: {hitT.distance:F2}m '{FullPath(hitT.collider.gameObject)}' layer={LayerMask.LayerToName(hitT.collider.gameObject.layer)} isTrigger={hitT.collider.isTrigger} ecOnHitGO={ecExact} (this is what the REAL click saw)");
                    }
                    else sb.AppendLine("  withTriggers: nothing (real click saw nothing either)");

                    Plugin.Logger.LogInfo(sb.ToString().TrimEnd());

                    // ── Field 4: arm the next-frame selection read ───────────────────────────
                    _pendingFrame = Time.frameCount;
                    _pendingVehicle = vc.name;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PROBE] CarClick postfix: {ex.Message}"); }
            }
        }

        /// <summary>Field 4: on the first MouseController.Run AFTER the event's frame, report
        /// what the click system actually selected — the player-visible outcome.</summary>
        [HarmonyPatch(typeof(MouseController), nameof(MouseController.Run))]
        public static class Patch_CarClickProbe_NextFrame
        {
            static void Postfix()
            {
                if (_pendingFrame < 0 || Time.frameCount <= _pendingFrame) return;
                try
                {
                    var e = MouseController.currentTargetEntity;
                    Plugin.Logger.LogInfo($"[PROBE] CarClick next-frame after '{_pendingVehicle}': currentTargetEntity={(e == null ? "NULL (click selected nothing)" : $"{e.GetType().Name} '{e.name}'")}");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PROBE] CarClick next-frame: {ex.Message}"); }
                _pendingFrame = -1; _pendingVehicle = "";
            }
        }

        private static string RelPath(Transform root, Transform t)
        {
            if (t == root) return "(root)";
            string s = t.name;
            var cur = t.parent;
            while (cur != null && cur != root) { s = cur.name + "/" + s; cur = cur.parent; }
            return s;
        }

        private static string FullPath(GameObject go)
        {
            string s = go.name;
            var cur = go.transform.parent;
            while (cur != null) { s = cur.name + "/" + s; cur = cur.parent; }
            return s;
        }
    }
    // PROBE-END: P-CARCLICK
}
