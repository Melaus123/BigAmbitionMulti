using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Helpers;
using Il2CppInterop.Runtime;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Backlog #3 — parked-vehicle sync (street parking + parking lots).
    ///
    /// Architecture (per ProbeParkedVehicles dump 2026-05-19):
    /// - 558 ParkingLaneGenerator MonoBehaviour instances spread across the
    ///   city, each generates vehicles into its own spots via RNG.
    /// - Vehicles come from a shared static pool: Helpers.ParkingSimulator,
    ///   keyed by vehicle name (string).  Methods RequestParkedVehicle and
    ///   ReleaseParkedVehicle are the ONLY entry/exit points — Harmony
    ///   Postfixes on those capture every spawn/release without per-lane
    ///   subscription gymnastics.
    /// - Lane positions are deterministic from world geometry; which CAR
    ///   ends up in which spot is per-session random, which is why host and
    ///   client previously didn't match.
    ///
    /// Sync model (host-authoritative, like TrafficSync):
    /// - HOST: Harmony Postfix on RequestParkedVehicle adds the spawned
    ///   GameObject to `_hostTracked`; Prefix on ReleaseParkedVehicle removes
    ///   it.  Every ~3 seconds the host re-reads live transform + colours
    ///   from every tracked GameObject, builds a ParkedSnapshotPayload, and
    ///   broadcasts it (reliable-ordered).
    /// - CLIENT: receives the snapshot, spawns one ghost per Key by calling
    ///   ParkingSimulator.RequestParkedVehicle(model) itself (using the
    ///   client's own pool), positions/rotates/colors it.  Ghosts not in a
    ///   later snapshot are released back to the client's pool.
    /// - CLIENT: ParkingLaneGenerator.GenerateParkedVehicles is Prefix-skipped
    ///   while MPClient.IsConnected so the client's own lanes don't generate
    ///   competing cars.
    /// </summary>
    public static class ParkedVehicleSync
    {
        // ── Host-side capture ────────────────────────────────────────────────
        // Keyed by GameObject.GetInstanceID() (stable for that GO's lifetime).
        private static readonly Dictionary<long, GameObject> _hostTracked = new();
        private static float _hostBroadcastTimer;
        private const float BroadcastInterval = 3f;

        // ── Client-side ghosts ───────────────────────────────────────────────
        // Keyed by the HOST's instance id (transmitted in the DTO).
        private static readonly Dictionary<long, GameObject> _clientGhosts = new();
        private static float _lastDiagTime;

        // Reflection-cached pool entry points (resolved lazily).
        private static MethodInfo? _miRequest;
        private static MethodInfo? _miRelease;

        public static void Reset()
        {
            _hostTracked.Clear();
            _hostBroadcastTimer = 0f;

            // Release any client ghosts cleanly back to the pool before
            // dropping references; a fresh game / scene change has its own
            // pool that we shouldn't pollute.
            foreach (var g in _clientGhosts.Values)
            {
                if (g != null) { try { CallPoolRelease(g); } catch { } }
            }
            _clientGhosts.Clear();
            _lastDiagTime = 0f;
        }

        // Per-frame entry point — called from MPCanvasUI.TickPositionSync.
        public static void Tick()
        {
            try
            {
                if (SaveGameManager.Current == null) return;
                if (Time.timeSinceLevelLoad < 5f) return;       // let lanes settle

                if (MPServer.IsRunning)
                {
                    _hostBroadcastTimer -= Time.unscaledDeltaTime;
                    if (_hostBroadcastTimer <= 0f)
                    {
                        _hostBroadcastTimer = BroadcastInterval;
                        MPServer.BroadcastParkedSnapshot(BuildSnapshot());
                    }

                    // 30s heartbeat — verifies capture is alive during play.
                    if (Time.unscaledTime - _lastDiagTime > 30f)
                    {
                        _lastDiagTime = Time.unscaledTime;
                        Plugin.Logger.LogInfo($"[ParkedSync] HOST tracking {_hostTracked.Count} parked vehicle(s).");
                    }
                }
                else if (MPClient.IsConnected)
                {
                    if (Time.unscaledTime - _lastDiagTime > 30f)
                    {
                        _lastDiagTime = Time.unscaledTime;
                        Plugin.Logger.LogInfo($"[ParkedSync] CLIENT showing {_clientGhosts.Count} ghost(s).");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] Tick: {ex.Message}");
            }
        }

        // ── HOST capture callbacks (invoked by Harmony patches) ──────────────

        public static void HostOnRequest(GameObject go, string model)
        {
            try
            {
                if (go == null) return;
                _hostTracked[go.GetInstanceID()] = go;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ParkedSync] HostOnRequest: {ex.Message}"); }
        }

        public static void HostOnRelease(GameObject go)
        {
            try
            {
                if (go == null) return;
                _hostTracked.Remove(go.GetInstanceID());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ParkedSync] HostOnRelease: {ex.Message}"); }
        }

        // ── HOST snapshot builder ────────────────────────────────────────────

        private static ParkedSnapshotPayload BuildSnapshot()
        {
            var snap = new ParkedSnapshotPayload();
            try
            {
                var dead = new List<long>();
                foreach (var kv in _hostTracked)
                {
                    var go = kv.Value;
                    if (go == null)              { dead.Add(kv.Key); continue; }
                    if (!go.activeInHierarchy)   continue;     // pooled-out, skip
                    var t = go.transform;
                    var pos = t.position;
                    var rot = t.rotation;
                    snap.Cars.Add(new ParkedVehicleDto
                    {
                        Key   = kv.Key,
                        Model = StripCloneSuffix(go.name),
                        X = pos.x, Y = pos.y, Z = pos.z,
                        Qx = rot.x, Qy = rot.y, Qz = rot.z, Qw = rot.w,
                        Colors = ReadBodyColors(go),
                    });
                }
                foreach (var k in dead) _hostTracked.Remove(k);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] BuildSnapshot: {ex.Message}");
            }
            return snap;
        }

        // ── CLIENT apply ─────────────────────────────────────────────────────

        public static void ApplySnapshot(ParkedSnapshotPayload payload)
        {
            try
            {
                if (payload?.Cars == null) return;

                var seen = new HashSet<long>(payload.Cars.Count);
                foreach (var car in payload.Cars)
                {
                    seen.Add(car.Key);
                    if (!_clientGhosts.TryGetValue(car.Key, out var ghost) || ghost == null)
                    {
                        ghost = SpawnGhost(car.Model);
                        if (ghost == null) continue;
                        _clientGhosts[car.Key] = ghost;
                    }
                    var t = ghost.transform;
                    t.position = new Vector3(car.X, car.Y, car.Z);
                    t.rotation = new Quaternion(car.Qx, car.Qy, car.Qz, car.Qw);
                    if (car.Colors != null && car.Colors.Count >= 6)
                        ApplyBodyColors(ghost, car.Colors);
                }

                // Despawn ghosts the host's snapshot no longer contains.
                var stale = _clientGhosts.Where(kv => !seen.Contains(kv.Key))
                                          .Select(kv => kv.Key).ToList();
                foreach (var k in stale)
                {
                    if (_clientGhosts[k] != null) CallPoolRelease(_clientGhosts[k]);
                    _clientGhosts.Remove(k);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ParkedSync] ApplySnapshot: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── Pool access via reflection on Helpers.ParkingSimulator ───────────

        private static GameObject? SpawnGhost(string model)
        {
            try
            {
                if (_miRequest == null)
                {
                    var simT = FindType("Helpers.ParkingSimulator");
                    _miRequest = simT?.GetMethod("RequestParkedVehicle",
                        BindingFlags.Public | BindingFlags.Static);
                }
                if (_miRequest == null) return null;
                var go = _miRequest.Invoke(null, new object[] { model }) as GameObject;
                return go;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] SpawnGhost '{model}': {ex.Message}");
                return null;
            }
        }

        private static void CallPoolRelease(GameObject go)
        {
            try
            {
                if (_miRelease == null)
                {
                    var simT = FindType("Helpers.ParkingSimulator");
                    _miRelease = simT?.GetMethod("ReleaseParkedVehicle",
                        BindingFlags.Public | BindingFlags.Static);
                }
                _miRelease?.Invoke(null, new object[] { go });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] CallPoolRelease: {ex.Message}");
            }
        }

        // ── SH_Vehicle color reader/applier ──────────────────────────────────
        // Same shader as traffic cars; duplicate of TrafficSync's logic but
        // independent to avoid coupling.  Two non-`_`-prefixed Color shader
        // properties (tint + fresnel), per renderer; MaterialPropertyBlock
        // is per-renderer.

        private static List<float> ReadBodyColors(GameObject car)
        {
            var groups = new List<(Color, Color)>();
            try
            {
                var rends = car.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var mat = FindShVehicleMaterial(r);
                    if (mat == null) continue;
                    groups.Add(ReadRendererColors(r, mat));
                }
            }
            catch { }
            if (groups.Count == 0) groups.Add((Color.white, Color.white));

            bool uniform = true;
            for (int i = 1; i < groups.Count && uniform; i++)
                if (groups[i] != groups[0]) uniform = false;

            var flat = new List<float>();
            int count = uniform ? 1 : groups.Count;
            for (int i = 0; i < count; i++)
            {
                var (a, b) = groups[i];
                flat.Add(a.r); flat.Add(a.g); flat.Add(a.b);
                flat.Add(b.r); flat.Add(b.g); flat.Add(b.b);
            }
            return flat;
        }

        private static (Color, Color) ReadRendererColors(Renderer r, Material mat)
        {
            var mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            Color c1 = Color.white, c2 = Color.white;
            int found = 0, n = mat.shader.GetPropertyCount();
            for (int p = 0; p < n && found < 2; p++)
            {
                if (mat.shader.GetPropertyType(p) != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                string pn = mat.shader.GetPropertyName(p);
                if (pn.StartsWith("_")) continue;
                var mpbCol = mpb.GetColor(pn);
                var col = mpbCol.a >= 0.5f ? mpbCol : mat.GetColor(pn);
                if (found == 0) c1 = col; else c2 = col;
                found++;
            }
            return (c1, c2);
        }

        private static Material? FindShVehicleMaterial(Renderer r)
        {
            var mats = r.sharedMaterials;
            if (mats == null) return null;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m != null && m.shader != null && m.shader.name.Contains("SH_Vehicle"))
                    return m;
            }
            return null;
        }

        private static void ApplyBodyColors(GameObject ghost, List<float> colors)
        {
            try
            {
                int groups = colors.Count / 6;
                if (groups < 1) return;

                var rends = ghost.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                int ri = 0;
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var mat = FindShVehicleMaterial(r);
                    if (mat == null) continue;

                    int gi = groups == 1 ? 0 : Mathf.Min(ri, groups - 1);
                    int b  = gi * 6;
                    var c1 = new Color(colors[b],     colors[b + 1], colors[b + 2]);
                    var c2 = new Color(colors[b + 3], colors[b + 4], colors[b + 5]);

                    var mpb = new MaterialPropertyBlock();
                    r.GetPropertyBlock(mpb);
                    int idx = 0, n = mat.shader.GetPropertyCount();
                    for (int p = 0; p < n && idx < 2; p++)
                    {
                        if (mat.shader.GetPropertyType(p) != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                        string pn = mat.shader.GetPropertyName(p);
                        if (pn.StartsWith("_")) continue;
                        mpb.SetColor(pn, idx == 0 ? c1 : c2);
                        idx++;
                    }
                    r.SetPropertyBlock(mpb);
                    ri++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] ApplyBodyColors: {ex.Message}");
            }
        }

        // ── Utility ──────────────────────────────────────────────────────────

        private static Type? FindType(string nameOrFullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.FullName == nameOrFullName || t.Name == nameOrFullName) return t;
                }
            }
            return null;
        }

        private static string StripCloneSuffix(string name)
        {
            int idx = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx >= 0 ? name.Substring(0, idx) : name;
        }
    }
}
