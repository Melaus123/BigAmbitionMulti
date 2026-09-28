using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>H-CARBUYSTACK-1 (user-approved 2026-09-27, option B + amendments 1/2). A car bought at a dealership is
    /// spawned natively on the store's one delivery spot (CityBuildingController.customPositions[0]); the native
    /// "something is on the spot" test (a 6 m cube) reads only the buyer's OWN VehicleInstances. A partner's car is a ghost
    /// that is taken out of the save list (VehicleManager DeregisterGhostFromSave), so a second player's purchase landed
    /// INSIDE the first player's car. Here, only while a multiplayer session is live:
    ///  - own car on the spot  -> nothing changes (the native refusal = single-player parity);
    ///  - partner ghost on it  -> a free spot next to it is chosen (Tier A: back 1, fwd 1, right 1, left 1, back 2, fwd 2
    ///    along the spot's own axes, same facing; side candidates never in a Gley traffic lane; Tier B: the game's own
    ///    AutoParkSpots within 10 m; hard cap 12 m) and the native spawn is moved there through an ARMED one-shot prefix on
    ///    VehicleHelper.CreateAndSpawnVehicle (ghost spawns use that method too, so it acts only while armed and never on
    ///    a BAMP_ instance); no spot -> the game's own blocking notice, the purchase is skipped before any money moves.
    /// Settings key CarBuyFreeSpot (default on). Everything else is the native purchase, untouched.</summary>
    internal static class CarBuySpot
    {
        internal const string BlockKey = "purchasevehicleui_notification_recently_purchase_vehicle_is_blocking";
        private const float SpotCube = 6f;      // native Bounds(spot, Vector3.one * 6f)
        private const float TierBRadius = 10f;
        private const float HardCap = 12f;
        private const int MaxTries = 12;

        [System.ThreadStatic] private static int _depth;   // the Contract -> Showcase delegate hop decides once
        private static bool _armed;
        private static Vector3 _pos;
        private static Quaternion _rot;

        internal static bool SessionLive()
        {
            try { return MPServer.IsRunning || MPClient.IsConnected; } catch { return false; }
        }

        /// <summary>Shared prefix body. Returns false (and sets result) only for the "no free spot" refusal.</summary>
        internal static bool Enter(ref bool result, out bool counted)
        {
            counted = false;
            try
            {
                if (_depth > 0) return true;           // inner hop of the same purchase - already decided
                _depth++; counted = true;
                _armed = false;
                if (!SessionLive() || !MPConfig.CarBuyFreeSpotLive()) return true;
                var cbc = InstanceBehavior<BuildingManager>.Instance?.cityBuildingController;
                var cps = cbc?.customPositions;
                if (cps == null || cps.Count == 0 || cps[0] == null) return true;
                Transform t = cps[0];
                var cube = new Bounds(t.position, Vector3.one * SpotCube);
                var gi = SaveGameManager.Current;
                if (gi?.VehicleInstances != null)
                    foreach (var vi in gi.VehicleInstances)
                    {
                        if (vi == null || (vi.id != null && vi.id.StartsWith("BAMP_"))) continue;
                        if (cube.Contains((Vector3)vi.position))
                        {
                            Plugin.Logger.LogInfo($"[CarBuy] own car {vi.id} on the spot - native refusal (parity)");
                            return true;
                        }
                    }
                string? blocker = null, blockerOwner = "";
                var ghosts = new List<(string vid, string owner, string type, Vector3 pos, Quaternion rot)>();
                foreach (var g in VehicleManager.GhostFootprints())
                {
                    ghosts.Add(g);
                    if (blocker == null && cube.Contains(g.pos)) { blocker = g.vid; blockerOwner = g.owner; }
                }
                if (blocker == null) return true;       // spot free for everyone - native
                string addr = "?";
                try { var bld = cbc?.building; addr = bld != null ? GameStateReader.AddressKey(bld) : "?"; } catch { }
                string buyType = CurrentPurchaseType ?? "";
                if (TryFindSpot(t, buyType, gi?.VehicleInstances, ghosts, out var pos, out var rot, out string label, out int tried))
                {
                    _pos = pos; _rot = rot; _armed = true;
                    Plugin.Logger.LogInfo($"[CarBuy] spot blocked by partner ghost {blocker} ({blockerOwner}) at {addr}; chose {label} at {Fmt(pos)} d={Vector3.Distance(pos, t.position):F2} tried={tried}");
                    return true;
                }
                Plugin.Logger.LogInfo($"[CarBuy] spot blocked by partner ghost {blocker}; no free safe spot in {tried} tries - native refusal");
                try { UI.Notification.Notifications.ShowError(BlockKey, BlockKey); } catch { }
                result = false;
                return false;
            }
            catch (System.Exception ex)
            {
                _armed = false;
                Plugin.Logger.LogWarning($"[CarBuy] decide error: {ex.Message} - native purchase runs");
                return true;
            }
        }

        internal static void Exit(bool counted)
        {
            if (!counted) return;
            try { _depth = System.Math.Max(0, _depth - 1); _armed = false; CurrentPurchaseType = null; } catch { }
        }

        /// <summary>Vehicle type id of the purchase being decided (set by the prefixes; used for the size lookup).</summary>
        internal static string? CurrentPurchaseType;
        internal static bool InPurchase => _depth > 0;

        internal static bool TakeOverride(out Vector3 pos, out Quaternion rot)
        {
            pos = _pos; rot = _rot;
            if (!_armed) return false;
            _armed = false;
            return true;
        }

        private static string Fmt(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F2}", v.x, v.y, v.z);

        // ── spot search ──────────────────────────────────────────────────────────────────────────
        private static bool TryFindSpot(Transform t, string type, List<VehicleInstance>? own, List<(string vid, string owner, string type, Vector3 pos, Quaternion rot)> ghosts,
                                        out Vector3 pos, out Quaternion rot, out string label, out int tried)
        {
            pos = default; rot = t.rotation; label = ""; tried = 0;
            Vector3 size = new Vector3(2f, 1.5f, 4.5f), center = Vector3.zero;
            try
            {
                if (!string.IsNullOrEmpty(type))
                {
                    var cs = Helpers.VehicleHelper.GetVehicleColliderCenterAndSize(type);
                    if (cs.Item2.x > 0.1f && cs.Item2.z > 0.1f) { center = cs.Item1; size = cs.Item2; }
                }
            }
            catch { }
            float W = size.x, L = size.z;
            // Obstacles in world space: own cars (save positions) + every partner ghost footprint, with their own sizes.
            var obst = new List<(Vector3 p, float w, float l)>();
            try
            {
                if (own != null)
                    foreach (var vi in own)
                    {
                        if (vi == null || (vi.id != null && vi.id.StartsWith("BAMP_"))) continue;
                        var (w, l) = SizeOf(vi.vehicleTypeName, W, L);
                        obst.Add(((Vector3)vi.position, w, l));
                    }
            }
            catch { }
            foreach (var g in ghosts) { var (w, l) = SizeOf(g.type, W, L); obst.Add((g.pos, w, l)); }

            // Ground at the spot itself: the reference height every candidate must match.
            bool haveGround = Physics.Raycast(t.position + Vector3.up * 2f, Vector3.down, out var gHit, 4f, Helpers.LayerHelper.groundLayerMask, QueryTriggerInteraction.Ignore);
            float spotLift = haveGround ? t.position.y - gHit.point.y : 0f;
            List<(Vector3 a, Vector3 b)> lanes = LaneSegmentsNear(t.position, 30f, out bool laneTable);

            var cands = new List<(Vector3 c, Quaternion r, string label, bool side)>();
            Vector3 fwd = t.rotation * Vector3.forward, right = t.rotation * Vector3.right;
            float sL = L + 1.0f, sW = W + 0.8f;
            cands.Add((t.position - fwd * sL, t.rotation, "tierA back 1", false));
            cands.Add((t.position + fwd * sL, t.rotation, "tierA fwd 1", false));
            cands.Add((t.position + right * sW, t.rotation, "tierA right 1", true));
            cands.Add((t.position - right * sW, t.rotation, "tierA left 1", true));
            cands.Add((t.position - fwd * sL * 2f, t.rotation, "tierA back 2", false));
            cands.Add((t.position + fwd * sL * 2f, t.rotation, "tierA fwd 2", false));
            // Tier B: the game's own street parking spots near the delivery spot (VehicleParkingHelper fields).
            try
            {
                float pad = 0f;
                try { pad = InstanceBehavior<GlobalReferences>.Instance.autoParkSettings.VehiclePadding * 2f; } catch { }
                var hits = Physics.OverlapSphere(t.position, TierBRadius, 1 << Helpers.LayerHelper.AutoParkSpotsLayerIndex, QueryTriggerInteraction.Collide);
                var tb = new List<(Vector3 c, Quaternion r, float d)>();
                foreach (var h in hits)
                {
                    if (h == null || !h.TryGetComponent<AutoParkSpot>(out var aps)) continue;
                    if (aps.maxVehicleLength < L + pad) continue;
                    Quaternion r = aps.transform.parent != null ? aps.transform.parent.rotation : aps.transform.rotation;
                    Quaternion flip = r * Quaternion.Euler(0f, 180f, 0f);
                    if (Vector3.Dot(flip * Vector3.forward, fwd) > Vector3.Dot(r * Vector3.forward, fwd)) r = flip;
                    tb.Add((aps.transform.position, r, Vector3.Distance(aps.transform.position, t.position)));
                }
                tb.Sort((x, y) => x.d.CompareTo(y.d));
                for (int i = 0; i < tb.Count; i++) cands.Add((tb[i].c, tb[i].r, "tierB parkspot " + (i + 1), false));
            }
            catch { }

            int sideSkipped = 0;
            foreach (var cd in cands)
            {
                if (tried >= MaxTries) break;
                if (Vector3.Distance(cd.c, t.position) > HardCap) continue;
                if (cd.side && !laneTable) { sideSkipped++; continue; }
                tried++;
                if (cd.side && InLane(cd.c, lanes, W)) continue;
                if (Occupied(cd.c, cd.r, W, L, obst)) continue;
                Vector3 c = cd.c;
                if (haveGround)
                {
                    if (!Physics.Raycast(c + Vector3.up * 2f, Vector3.down, out var h, 4f, Helpers.LayerHelper.groundLayerMask, QueryTriggerInteraction.Ignore)) continue;
                    if (Mathf.Abs(h.point.y - gHit.point.y) >= 0.4f) continue;
                    c.y = h.point.y + spotLift;
                }
                int statics = Physics.OverlapBox(c + cd.r * center + Vector3.up * 0.3f, size * 0.5f, cd.r,
                                                 Helpers.LayerHelper.buildingsLayerMask | Helpers.LayerHelper.wallsLayerMask,
                                                 QueryTriggerInteraction.Ignore).Length;
                if (statics > 0) continue;
                if (Vector3.Distance(c, t.position) > HardCap) continue;
                pos = c; rot = cd.r; label = cd.label;
                if (!haveGround) label += " (no ground at spot - height check off)";
                return true;
            }
            if (sideSkipped > 0)
                Plugin.Logger.LogInfo($"[CarBuy] no Gley lane table - {sideSkipped} side candidate(s) skipped");
            return false;
        }

        private static (float w, float l) SizeOf(string type, float dw, float dl)
        {
            try
            {
                if (!string.IsNullOrEmpty(type))
                {
                    var cs = Helpers.VehicleHelper.GetVehicleColliderCenterAndSize(type);
                    if (cs.Item2.x > 0.1f && cs.Item2.z > 0.1f) return (cs.Item2.x, cs.Item2.z);
                }
            }
            catch { }
            return (dw, dl);
        }

        /// <summary>Another car's centre within the candidate's footprint plus padding, in the candidate's frame
        /// (half-sum of both sizes + 0.6 m sideways / + 0.8 m lengthways; equal cars = the design's W+0.6 / L+0.8).</summary>
        private static bool Occupied(Vector3 c, Quaternion r, float W, float L, List<(Vector3 p, float w, float l)> obst)
        {
            Quaternion inv = Quaternion.Inverse(r);
            foreach (var o in obst)
            {
                Vector3 d = inv * (o.p - c);
                if (Mathf.Abs(d.x) < (W + o.w) * 0.5f + 0.6f && Mathf.Abs(d.z) < (L + o.l) * 0.5f + 0.8f && Mathf.Abs(d.y) < 4f) return true;
            }
            return false;
        }

        /// <summary>Gley traffic lane segments (waypoint -> neighbour) with an end within <paramref name="radius"/> of p,
        /// from the running manager's own table (the one TestDrive tproad / TrafficSync read). laneTable=false when absent.</summary>
        private static List<(Vector3 a, Vector3 b)> LaneSegmentsNear(Vector3 p, float radius, out bool laneTable)
        {
            var segs = new List<(Vector3, Vector3)>();
            laneTable = false;
            try
            {
                var tm = GleyTrafficSystem.TrafficManager.HasInstance ? GleyTrafficSystem.TrafficManager.Instance : null;
                var wps = tm?.densityManager?.gridManager?.currentSceneData?.allWaypoints;
                if (wps == null || wps.Length == 0) return segs;
                laneTable = true;
                float r2 = radius * radius;
                for (int i = 0; i < wps.Length; i++)
                {
                    var w = wps[i];
                    if (w == null) continue;
                    float dx = w.position.x - p.x, dz = w.position.z - p.z;
                    bool near = dx * dx + dz * dz <= r2;
                    var nb = w.neighbors;
                    if (nb == null || nb.Count == 0) { if (near) segs.Add((w.position, w.position)); continue; }
                    foreach (int n in nb)
                    {
                        if (n < 0 || n >= wps.Length || wps[n] == null) continue;
                        Vector3 b = wps[n].position;
                        float bx = b.x - p.x, bz = b.z - p.z;
                        if (near || bx * bx + bz * bz <= r2) segs.Add((w.position, b));
                    }
                }
            }
            catch { laneTable = false; segs.Clear(); }
            return segs;
        }

        private static bool InLane(Vector3 c, List<(Vector3 a, Vector3 b)> segs, float W)
        {
            foreach (var s in segs)
            {
                Vector2 a = new Vector2(s.a.x, s.a.z), b = new Vector2(s.b.x, s.b.z), q = new Vector2(c.x, c.z);
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                float u = len2 < 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(q - a, ab) / len2);
                if ((a + ab * u - q).magnitude < W) return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Buildings.ContractVehicleForSale), nameof(Buildings.ContractVehicleForSale.Purchase))]
    public static class Patch_ContractVehicleForSale_Purchase_CarBuySpot
    {
        static bool Prefix(Buildings.ContractVehicleForSale __instance, ref bool __result, out bool __state)
        {
            __state = false;
            try { if (!CarBuySpot.InPurchase) CarBuySpot.CurrentPurchaseType = __instance?.ShowcaseVehicleController != null ? __instance.ShowcaseVehicleController.vehicleName : Traverse.Create(__instance).Field("_vehicleType").GetValue<Vehicles.VehicleTypes.VehicleType>()?.vehicleTypeName; } catch { }
            return CarBuySpot.Enter(ref __result, out __state);
        }
        static System.Exception Finalizer(System.Exception __exception, bool __state) { CarBuySpot.Exit(__state); return __exception; }
    }

    [HarmonyPatch(typeof(Controllers.ShowcaseVehicleController), nameof(Controllers.ShowcaseVehicleController.Purchase))]
    public static class Patch_ShowcaseVehicleController_Purchase_CarBuySpot
    {
        static bool Prefix(Controllers.ShowcaseVehicleController __instance, ref bool __result, out bool __state)
        {
            __state = false;
            try { if (!CarBuySpot.InPurchase) CarBuySpot.CurrentPurchaseType = __instance?.vehicleName; } catch { }
            return CarBuySpot.Enter(ref __result, out __state);
        }
        static System.Exception Finalizer(System.Exception __exception, bool __state) { CarBuySpot.Exit(__state); return __exception; }
    }

    [HarmonyPatch(typeof(Helpers.VehicleHelper), nameof(Helpers.VehicleHelper.CreateAndSpawnVehicle))]
    public static class Patch_VehicleHelper_CreateAndSpawnVehicle_CarBuySpot
    {
        static void Prefix(VehicleInstance vehicleInstance, ref Vector3 spawnPosition, ref Quaternion spawnRotation)
        {
            try
            {
                if (!CarBuySpot.InPurchase) return;
                if (vehicleInstance?.id != null && vehicleInstance.id.StartsWith("BAMP_")) return;
                if (CarBuySpot.TakeOverride(out var p, out var r)) { spawnPosition = p; spawnRotation = r; }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[CarBuy] spawn override error: {ex.Message}"); }
        }

        static void Postfix(VehicleInstance vehicleInstance)
        {
            try
            {
                // Partners learn of the new car on the next fleet send instead of the 5 s heartbeat (shortens the
                // same-spot race between two near-simultaneous purchases).
                if (CarBuySpot.InPurchase && CarBuySpot.SessionLive() && !(vehicleInstance?.id?.StartsWith("BAMP_") ?? false))
                    VehicleManager.MarkFleetDirty();
            }
            catch { }
        }
    }
}
