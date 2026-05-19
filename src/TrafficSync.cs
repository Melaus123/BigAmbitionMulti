using System.Reflection;
using UnityEngine;
using Helpers;
using GleyTrafficSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Phase 5 — host-authoritative AI-traffic sync.
    ///
    /// Host: enumerates the live GleyTrafficSystem traffic and broadcasts a full
    /// snapshot ~5x/sec.  Client: continuously suppresses its own local traffic
    /// and renders the host's traffic as lightweight visual ghosts.
    ///
    /// All methods must be called on the Unity main thread.
    /// </summary>
    public static class TrafficSync
    {
        private const float BroadcastInterval = 0.1f;   // host snapshot rate (~10 Hz)
        private const float GhostLerp         = 14f;    // client ghost chase rate
        private const float TaxiStopDuration  = 18f;    // how long a hailed taxi stays stopped
        private const int   CarsPerPlayer     = 24;     // traffic budget scaled by player count

        // Host: taxis stopped for a client hail → unscaled time to auto-resume them.
        private static readonly Dictionary<int, float> _taxiResumeAt = new();

        // ── Client ghost state ────────────────────────────────────────────────

        private sealed class TrafficGhost
        {
            public GameObject?   Go;
            public string        Model = "";
            public Vector3       TargetPos;
            public Quaternion    TargetRot = Quaternion.identity;
            public List<float>?  LastColors;          // last applied body colours
        }

        // Keyed by the host's Gley pool index.
        private static readonly Dictionary<int, TrafficGhost> _ghosts = new();

        // A networked position jump bigger than this is a reused pool slot or a
        // teleport — snap the ghost rather than sliding it across the screen.
        private const float SnapDistance = 12f;

        // One-time colour diagnostics (host reads / client applies).
        private static int _colorDiagHost;
        private static int _colorDiagClient;
        // Indices whose full colour state has been dumped (host / client).
        private static readonly HashSet<int> _colorDumpHost   = new();
        private static readonly HashSet<int> _colorDumpClient = new();

        private static float _hostBroadcastTimer;
        private static float _lightBroadcastTimer;
        private static bool  _clientTrafficKilled;

        // model name → traffic-car prefab, built once from Gley's VehiclePool.
        private static Dictionary<string, GameObject>? _trafficPrefabs;

        // Gley/AI/audio components destroyed on a traffic ghost — leaves a prop.
        private static readonly string[] _killTrafficComponents =
        {
            "VehicleComponent", "EngineSoundComponent", "AiCarRescueCheck",
            "AiCarHorn", "AiCarMusic", "VehicleLightsToggle", "TaxiController",
            "RandomVehicleColor", "RandomVehicleDirtiness", "VisibilityScript",
            "VehicleNavMeshObstacleToggler", "AudioSource",
        };

        /// <summary>Resets per-game state (call on game load / scene change).</summary>
        public static void Reset()
        {
            _hostBroadcastTimer  = 0f;
            _lightBroadcastTimer = 0f;
            _clientTrafficKilled = false;
            _anchorDiagLogged    = false;
            _trafficPrefabs      = null;
            _taxiResumeAt.Clear();
            _carRenderers.Clear();
            _colorDiagHost   = 0;
            _colorDiagClient = 0;
            _colorDumpHost.Clear();
            _colorDumpClient.Clear();
            _ghosts.Clear();          // ghost GameObjects die with the old scene
        }

        /// <summary>Role-based step — called each frame in-game.</summary>
        public static void Tick()
        {
            try
            {
                if (SaveGameManager.Current == null) return;

                if (MPServer.IsRunning)
                {
                    // Citywide: keep the traffic system spawning around every
                    // player, not just the host.
                    UpdateTrafficAnchors();
                    TickTaxiResumes();

                    _hostBroadcastTimer -= Time.unscaledDeltaTime;
                    if (_hostBroadcastTimer <= 0f)
                    {
                        _hostBroadcastTimer = BroadcastInterval;
                        MPServer.BroadcastTrafficSnapshot(BuildSnapshot());
                    }

                    _lightBroadcastTimer -= Time.unscaledDeltaTime;
                    if (_lightBroadcastTimer <= 0f)
                    {
                        _lightBroadcastTimer = 0.5f;     // lights change slowly
                        var lights = BuildLightSnapshot();
                        if (lights != null) MPServer.BroadcastTrafficLights(lights);
                    }
                }
                else if (MPClient.IsConnected)
                {
                    if (Time.timeSinceLevelLoad > 5f)
                        SuppressLocalTraffic();
                    TickGhosts();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[TrafficSync] Tick: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── Host: build the traffic snapshot ──────────────────────────────────

        private static TrafficSnapshotPayload BuildSnapshot()
        {
            var snap = new TrafficSnapshotPayload();
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<VehicleComponent>());
                if (arr == null) return snap;
                for (int i = 0; i < arr.Length; i++)
                {
                    var vc = arr[i].TryCast<VehicleComponent>();
                    if (vc == null) continue;
                    var go = vc.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;

                    var t = vc.transform;
                    var pos = t.position;
                    var rot = t.rotation;
                    int index = vc.GetIndex();
                    string model = StripCloneSuffix(go.name);

                    // Body colour — read FRESH every snapshot (Gley reuses pool
                    // indices, so a cache goes stale on recycle).  One colour
                    // group per SH_Vehicle renderer (collapsed if uniform).
                    var colors = ReadBodyColors(index, go);

                    if (_colorDiagHost < 8)
                    {
                        _colorDiagHost++;
                        Plugin.Logger.LogInfo(
                            $"[TrafficColor] host car[{index}] '{model}' {colors.Count / 6} group(s)");
                    }
                    if (_colorDumpHost.Count < 4 && _colorDumpHost.Add(index))
                        DumpFullCarColor(go, index, "HOST");

                    snap.Cars.Add(new TrafficCarDto
                    {
                        Index = index,
                        Model = model,
                        X = pos.x, Y = pos.y, Z = pos.z,
                        Qx = rot.x, Qy = rot.y, Qz = rot.z, Qw = rot.w,
                        Colors = colors,
                    });
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] BuildSnapshot: {ex.Message}");
            }
            return snap;
        }

        /// <summary>
        /// Diagnostic — logs the complete SH_Vehicle body-colour state of a car:
        /// every Color shader property, shared-material value AND property-block
        /// value.  Run on the host (real car) and client (ghost) for the same
        /// index; diffing the two reveals exactly what differs.
        /// </summary>
        private static void DumpFullCarColor(GameObject car, int index, string side)
        {
            try
            {
                var mpb = new MaterialPropertyBlock();
                var rends = car.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var mat = r.sharedMaterial;
                    if (mat == null || mat.shader == null) continue;
                    if (!mat.shader.name.Contains("SH_Vehicle")) continue;
                    if (r.gameObject.name.Contains("Wheel")) continue;   // want the body

                    r.GetPropertyBlock(mpb);
                    var sh = mat.shader;
                    int n = sh.GetPropertyCount();
                    Plugin.Logger.LogInfo(
                        $"[ColorDump] {side} idx={index} '{r.gameObject.name}' mat='{mat.name}' — {n} props:");
                    for (int p = 0; p < n; p++)
                    {
                        string pn = sh.GetPropertyName(p);
                        string val;
                        switch (sh.GetPropertyType(p))
                        {
                            case UnityEngine.Rendering.ShaderPropertyType.Color:
                                val = $"COL s={mat.GetColor(pn)} m={mpb.GetColor(pn)}"; break;
                            case UnityEngine.Rendering.ShaderPropertyType.Vector:
                                val = $"VEC s={mat.GetVector(pn)} m={mpb.GetVector(pn)}"; break;
                            case UnityEngine.Rendering.ShaderPropertyType.Float:
                            case UnityEngine.Rendering.ShaderPropertyType.Range:
                                val = $"FLT s={mat.GetFloat(pn):0.###} m={mpb.GetFloat(pn):0.###}"; break;
                            default:
                                continue;   // skip Texture / Int
                        }
                        Plugin.Logger.LogInfo($"[ColorDump] {side} idx={index}     {pn} : {val}");
                    }
                    return;   // one body renderer is representative
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ColorDump] {side} idx={index}: {ex.Message}");
            }
        }

        /// <summary>"VordTiaraVic(Clone)22" → "VordTiaraVic".</summary>
        private static string StripCloneSuffix(string name)
        {
            int idx = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return idx >= 0 ? name.Substring(0, idx) : name;
        }

        // ── Vehicle body colour ───────────────────────────────────────────────
        //
        // The car body uses shader "SH_Vehicle" with two custom (non-`_`-prefixed)
        // Color properties — the tint + fresnel from the car's VehicleColor.
        // Host: vehicle index → cached SH_Vehicle body renderers (the GameObject is
        // pooled, so the references stay valid even when a slot's car is recycled).
        private static readonly Dictionary<int, List<Renderer>> _carRenderers = new();

        /// <summary>All SH_Vehicle renderers of a car, in hierarchy order, cached per index.</summary>
        private static List<Renderer> GetCarRenderers(int index, GameObject car)
        {
            if (_carRenderers.TryGetValue(index, out var cached) &&
                cached.Count > 0 && cached[0] != null)
                return cached;

            var list = new List<Renderer>();
            try
            {
                var rends = car.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var mat = r.sharedMaterial;
                    if (mat != null && mat.shader != null && mat.shader.name.Contains("SH_Vehicle"))
                        list.Add(r);
                }
            }
            catch { }
            _carRenderers[index] = list;
            return list;
        }

        /// <summary>Reads one renderer's two SH_Vehicle tint colours from its MPB.</summary>
        private static (Color, Color) ReadRendererColors(Renderer r)
        {
            var mat = r.sharedMaterial;
            if (mat == null || mat.shader == null) return (Color.white, Color.white);
            var mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            Color c1 = Color.white, c2 = Color.white;
            int found = 0, n = mat.shader.GetPropertyCount();
            for (int p = 0; p < n && found < 2; p++)
            {
                if (mat.shader.GetPropertyType(p)
                    != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                string pn = mat.shader.GetPropertyName(p);
                if (pn.StartsWith("_")) continue;              // skip standard props
                var mpbCol = mpb.GetColor(pn);
                var col = mpbCol.a >= 0.5f ? mpbCol : mat.GetColor(pn);
                if (found == 0) c1 = col; else c2 = col;
                found++;
            }
            return (c1, c2);
        }

        /// <summary>
        /// Reads a car's body colours LIVE — one (tint,fresnel) pair per SH_Vehicle
        /// renderer.  Collapsed to a single pair when every renderer matches
        /// (regular car); kept per-renderer when they differ (box-truck cab, etc.).
        /// Returned flattened: 6 floats per group.
        /// </summary>
        private static List<float> ReadBodyColors(int index, GameObject car)
        {
            var groups = new List<(Color, Color)>();
            try
            {
                foreach (var r in GetCarRenderers(index, car))
                    if (r != null) groups.Add(ReadRendererColors(r));
            }
            catch { }
            if (groups.Count == 0) groups.Add((Color.white, Color.white));

            // Collapse when uniform.
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

        private static bool SameColors(List<float>? a, List<float>? b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Applies body colours to a ghost car's SH_Vehicle renderers.  One colour
        /// group → every renderer that colour; multiple groups → the i-th group to
        /// the i-th renderer (same prefab as the host, so renderer order matches).
        /// </summary>
        private static void ApplyVehicleBodyColors(GameObject ghost, string model, List<float> colors)
        {
            try
            {
                int groups = colors.Count / 6;
                if (groups < 1) return;

                var rends = ghost.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                int ri = 0, applied = 0;
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i].TryCast<Renderer>();
                    if (r == null) continue;
                    var mat = r.sharedMaterial;
                    if (mat == null || mat.shader == null) continue;
                    if (!mat.shader.name.Contains("SH_Vehicle")) continue;

                    int gi = groups == 1 ? 0 : Mathf.Min(ri, groups - 1);
                    int b  = gi * 6;
                    var c1 = new Color(colors[b],     colors[b + 1], colors[b + 2]);
                    var c2 = new Color(colors[b + 3], colors[b + 4], colors[b + 5]);

                    var mpb = new MaterialPropertyBlock();
                    r.GetPropertyBlock(mpb);                       // keep any existing block values
                    int idx = 0, n = mat.shader.GetPropertyCount();
                    for (int p = 0; p < n && idx < 2; p++)
                    {
                        if (mat.shader.GetPropertyType(p)
                            != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                        string pn = mat.shader.GetPropertyName(p);
                        if (pn.StartsWith("_")) continue;
                        mpb.SetColor(pn, idx == 0 ? c1 : c2);
                        idx++;
                    }
                    r.SetPropertyBlock(mpb);
                    ri++; applied++;
                }
                if (_colorDiagClient < 8)
                {
                    _colorDiagClient++;
                    Plugin.Logger.LogInfo(
                        $"[TrafficColor] ghost '{model}' apply {groups} group(s) → {applied} renderer(s)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] ApplyVehicleBodyColors: {ex.Message}");
            }
        }

        // ── Traffic-light sync ────────────────────────────────────────────────

        /// <summary>Host: reads every traffic-light intersection's current state.</summary>
        private static TrafficLightsPayload? BuildLightSnapshot()
        {
            try
            {
                var im = TrafficManager.Instance?.intersectionManager;
                var all = im?.allIntersections;
                if (all == null) return null;

                var payload = new TrafficLightsPayload();
                for (int i = 0; i < all.Length; i++)
                {
                    var el = all[i];
                    var ti = el != null ? el.TryCast<TrafficLightsIntersection>() : null;
                    if (ti == null) continue;          // PriorityIntersection — no lights
                    payload.Lights.Add(new LightStateDto
                    {
                        Index  = i,
                        Road   = ti.currentRoad,
                        Yellow = ti.yellowLight,
                    });
                }
                return payload;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] BuildLightSnapshot: {ex.Message}");
                return null;
            }
        }

        /// <summary>Client: forces each traffic-light intersection to the host's state.</summary>
        public static void ApplyTrafficLights(TrafficLightsPayload payload)
        {
            if (payload == null) return;
            if (SaveGameManager.Current == null) return;
            try
            {
                var im = TrafficManager.Instance?.intersectionManager;
                var all = im?.allIntersections;
                if (all == null) return;

                foreach (var s in payload.Lights)
                {
                    if (s.Index < 0 || s.Index >= all.Length) continue;
                    var el = all[s.Index];
                    var ti = el != null ? el.TryCast<TrafficLightsIntersection>() : null;
                    if (ti == null) continue;
                    ti.ChangeAllRoadsExceptSelectd(s.Road, TrafficLightsColor.Red);
                    ti.ChangeCurrentRoadColors(s.Road,
                        s.Yellow ? TrafficLightsColor.Yellow : TrafficLightsColor.Green);
                    ti.ApplyColorChanges();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] ApplyTrafficLights: {ex.Message}");
            }
        }

        // ── Client: apply the snapshot as ghost cars ──────────────────────────

        /// <summary>Applies a host traffic snapshot — spawns/moves/despawns ghosts.</summary>
        public static void ApplySnapshot(TrafficSnapshotPayload snap)
        {
            if (snap == null) return;
            if (SaveGameManager.Current == null) return;
            try
            {
                var seen = new HashSet<int>();
                foreach (var car in snap.Cars)
                {
                    seen.Add(car.Index);
                    var pos = new Vector3(car.X, car.Y, car.Z);
                    var rot = new Quaternion(car.Qx, car.Qy, car.Qz, car.Qw);

                    _ghosts.TryGetValue(car.Index, out var g);
                    // A pool slot reused by a DIFFERENT model = a different car —
                    // respawn fresh so the wrong mesh doesn't slide into view.
                    if (g != null && g.Go != null && g.Model != car.Model)
                    {
                        try { UnityEngine.Object.Destroy(g.Go); } catch { }
                        g = null;
                    }

                    if (g == null || g.Go == null)
                    {
                        var go = SpawnTrafficGhost(car.Model, pos, rot);
                        if (go == null) { _ghosts.Remove(car.Index); continue; }
                        g = new TrafficGhost { Go = go, Model = car.Model, TargetPos = pos, TargetRot = rot };
                        _ghosts[car.Index] = g;
                    }
                    else
                    {
                        // A jump bigger than SnapDistance = a reused slot of the
                        // same model, or a teleport — snap instead of sliding.
                        if (Vector3.Distance(g.Go.transform.position, pos) > SnapDistance)
                        {
                            g.Go.transform.position = pos;
                            g.Go.transform.rotation = rot;
                        }
                        g.TargetPos = pos;
                        g.TargetRot = rot;
                    }

                    // Apply body colours on spawn AND whenever they change (a car
                    // can recycle into this pool slot with a different colour).
                    if (g.Go != null && car.Colors != null && car.Colors.Count >= 6
                        && !SameColors(g.LastColors, car.Colors))
                    {
                        ApplyVehicleBodyColors(g.Go, car.Model, car.Colors);
                        g.LastColors = car.Colors;
                    }

                    // Diagnostic — full colour dump of the first few coloured ghosts.
                    if (g.Go != null && g.LastColors != null
                        && _colorDumpClient.Count < 4 && _colorDumpClient.Add(car.Index))
                        DumpFullCarColor(g.Go, car.Index, "CLIENT");
                }

                // Despawn ghosts whose host car is no longer in the snapshot.
                var stale = _ghosts.Where(kv => !seen.Contains(kv.Key))
                                   .Select(kv => kv.Key).ToList();
                foreach (var k in stale)
                {
                    if (_ghosts[k].Go != null)
                    {
                        try { UnityEngine.Object.Destroy(_ghosts[k].Go); } catch { }
                    }
                    _ghosts.Remove(k);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[TrafficSync] ApplySnapshot: {ex.Message}");
            }
        }

        // ── Client: traffic-ghost spawning (from Gley's own prefab pool) ──────

        private static bool _prefabMapDiagLogged;

        /// <summary>Builds the model→prefab map from Gley's VehiclePool (once).</summary>
        private static void BuildPrefabMap()
        {
            if (_trafficPrefabs != null) return;
            try
            {
                var tc = TrafficComponent.Instance;
                if (tc == null)
                {
                    if (!_prefabMapDiagLogged)
                    {
                        _prefabMapDiagLogged = true;
                        Plugin.Logger.LogWarning("[TrafficSync] BuildPrefabMap: TrafficComponent.Instance is null.");
                    }
                    return;                                 // retry next call
                }
                var pool = tc.vehiclePool;
                if (pool == null)
                {
                    Plugin.Logger.LogWarning("[TrafficSync] BuildPrefabMap: vehiclePool is null — using fallback spawn.");
                    _trafficPrefabs = new Dictionary<string, GameObject>();   // stop retrying
                    return;
                }
                var cars = pool.trafficCars;
                if (cars == null)
                {
                    Plugin.Logger.LogWarning("[TrafficSync] BuildPrefabMap: trafficCars is null — using fallback spawn.");
                    _trafficPrefabs = new Dictionary<string, GameObject>();
                    return;
                }

                var map = new Dictionary<string, GameObject>();
                for (int i = 0; i < cars.Length; i++)
                {
                    var el = cars[i];
                    if (el == null) continue;
                    if (i == 0)
                        Plugin.Logger.LogInfo(
                            "[TrafficSync] CarType members: " + string.Join(", ",
                                el.GetType().GetProperties(
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                  .Select(p => $"{p.Name}({p.PropertyType.Name})")));
                    var prefab = ExtractPrefab(el);
                    if (prefab == null) continue;
                    map[StripCloneSuffix(prefab.name)] = prefab;
                }
                _trafficPrefabs = map;                      // even if empty — fallback covers it
                Plugin.Logger.LogInfo(
                    $"[TrafficSync] Traffic prefab map: {map.Count} model(s) from {cars.Length} entries" +
                    (map.Count > 0 ? $" — {string.Join(", ", map.Keys)}" : " (EMPTY — using fallback spawn)"));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] BuildPrefabMap: {ex.Message}");
                _trafficPrefabs = new Dictionary<string, GameObject>();
            }
        }

        /// <summary>Pulls the prefab GameObject out of a Gley CarType wrapper by reflection.</summary>
        private static GameObject? ExtractPrefab(CarType el)
        {
            try
            {
                const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                GameObject? first = null;
                foreach (var p in el.GetType().GetProperties(f))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    object? v;
                    try { v = p.GetValue(el); } catch { continue; }
                    GameObject? go = v as GameObject;
                    if (go == null && v is Component c) go = c.gameObject;
                    if (go == null) continue;
                    string nm = p.Name.ToLowerInvariant();
                    if (nm.Contains("prefab") || nm.Contains("vehicle") || nm.Contains("car"))
                        return go;                          // preferred match
                    first ??= go;
                }
                return first;
            }
            catch { return null; }
        }

        /// <summary>
        /// Spawns a traffic ghost.  Prefers Gley's own prefab (correct models incl.
        /// Taxi); falls back to the player-vehicle ghost path so traffic still
        /// shows even if the Gley pool is unavailable.
        /// </summary>
        private static GameObject? SpawnTrafficGhost(string model, Vector3 pos, Quaternion rot)
        {
            BuildPrefabMap();

            GameObject? prefab = null;
            _trafficPrefabs?.TryGetValue(model, out prefab);
            if (prefab == null)
                return VehicleManager.SpawnVisualGhost(model, pos, rot);   // fallback

            GameObject go;
            try { go = UnityEngine.Object.Instantiate(prefab, pos, rot); }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] Instantiate '{model}': {ex.Message}");
                return VehicleManager.SpawnVisualGhost(model, pos, rot);    // fallback
            }
            go.SetActive(true);

            // Strip Gley AI / audio / LOD components — leaves a pure visual prop.
            // Taxis are special: keep TaxiController (the fast-travel interaction)
            // and keep VehicleComponent (TaxiController holds a reference to it)
            // but disable VehicleComponent so its dead AI fires no triggers/updates.
            bool isTaxi = model.Equals("Taxi", StringComparison.OrdinalIgnoreCase);
            try
            {
                var comps = go.GetComponents(Il2CppType.Of<Component>());
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    string cn = c.GetIl2CppType().Name;

                    if (isTaxi && cn == "TaxiController")
                        continue;                              // keep — the interaction
                    if (isTaxi && cn == "VehicleComponent")
                    {
                        var beh = c.TryCast<Behaviour>();       // keep ref, kill its logic
                        if (beh != null) beh.enabled = false;
                        continue;
                    }
                    if (System.Array.IndexOf(_killTrafficComponents, cn) >= 0)
                        UnityEngine.Object.Destroy(c);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] strip '{model}': {ex.Message}");
            }
            try
            {
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
            }
            catch { }
            return go;
        }

        /// <summary>Smooths each traffic ghost toward its networked transform.</summary>
        private static void TickGhosts()
        {
            if (_ghosts.Count == 0) return;
            float k = Time.deltaTime * GhostLerp;
            foreach (var g in _ghosts.Values)
            {
                if (g.Go == null) continue;
                var t = g.Go.transform;
                t.position = Vector3.Lerp(t.position, g.TargetPos, k);
                t.rotation = Quaternion.Slerp(t.rotation, g.TargetRot, k);
            }
        }

        /// <summary>Destroys all traffic ghosts (disconnect / scene unload).</summary>
        public static void DespawnAllGhosts()
        {
            foreach (var g in _ghosts.Values)
                if (g.Go != null) { try { UnityEngine.Object.Destroy(g.Go); } catch { } }
            _ghosts.Clear();
        }

        // ── Citywide: traffic spawns around every player ──────────────────────

        /// <summary>
        /// Host: feeds every player's position to Gley's density manager so
        /// traffic spawns around all players, not just the host.
        /// </summary>
        private static bool _anchorDiagLogged;

        private static void UpdateTrafficAnchors()
        {
            try
            {
                var tm = TrafficManager.Instance;
                if (tm == null) return;
                var dm = tm.densityManager;

                var anchors = new List<Transform>();
                var hostChar = PlayerHelper.PlayerController?.Character;
                if (hostChar != null) anchors.Add(hostChar.transform);
                foreach (var t in RemotePlayerManager.GetRemotePlayerTransforms())
                    if (t != null) anchors.Add(t);
                if (anchors.Count == 0) return;

                var arr = new Il2CppReferenceArray<Transform>(anchors.Count);
                for (int i = 0; i < anchors.Count; i++) arr[i] = anchors[i];

                // Feed every player to both anchor APIs — UpdateCamera drives the
                // active-grid squares (where traffic spawns), UpdateCameraPositions
                // the density manager.
                try { tm.UpdateCamera(arr); }
                catch (Exception e)
                { if (!_anchorDiagLogged) Plugin.Logger.LogWarning($"[TrafficSync] UpdateCamera: {e.Message}"); }
                if (dm != null)
                {
                    try { dm.UpdateCameraPositions(arr); }
                    catch (Exception e)
                    { if (!_anchorDiagLogged) Plugin.Logger.LogWarning($"[TrafficSync] UpdateCameraPositions: {e.Message}"); }
                    // Scale the traffic budget with player count — one player's
                    // worth of cars spread over N areas looks sparse.
                    try { dm.UpdateMaxCars(CarsPerPlayer * anchors.Count); } catch { }
                }

                if (!_anchorDiagLogged)
                {
                    _anchorDiagLogged = true;
                    Plugin.Logger.LogInfo(
                        $"[TrafficSync] Traffic anchors active: {anchors.Count} player(s); " +
                        $"densityManager={(dm != null ? "ok" : "NULL")}; " +
                        $"maxCars={CarsPerPlayer * anchors.Count}.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] UpdateTrafficAnchors: {ex.Message}");
            }
        }

        // ── Taxi hail (host-authoritative stop) ───────────────────────────────

        /// <summary>
        /// Called (via Harmony patch) when the local player hails a taxi.  The
        /// taxi's stop is host-authoritative: a client tells the host, the host
        /// stops its REAL taxi, and every ghost follows — so all players stay in
        /// sync.  The host's own click is already handled by the game's SP flow.
        /// </summary>
        public static void OnLocalTaxiHailed(GameObject taxiGo)
        {
            try
            {
                if (taxiGo == null) return;
                if (MPServer.IsRunning) return;          // host: game already stopped its real taxi
                if (!MPClient.IsConnected) return;
                int index = ResolveTaxiIndex(taxiGo);
                if (index >= 0) MPClient.SendTaxiHail(index);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] OnLocalTaxiHailed: {ex.Message}");
            }
        }

        /// <summary>Resolves a clicked taxi GameObject to its Gley pool index.</summary>
        private static int ResolveTaxiIndex(GameObject go)
        {
            // Client: the clicked taxi is one of our ghosts — keyed by pool index.
            foreach (var kv in _ghosts)
                if (kv.Value.Go == go) return kv.Key;
            // Host: a real traffic taxi — read its Gley pool index.
            var vcComp = VehicleManager.FindComponentByName(go, "VehicleComponent");
            var vc = vcComp != null ? vcComp.TryCast<VehicleComponent>() : null;
            return vc != null ? vc.GetIndex() : -1;
        }

        /// <summary>Finds a live traffic taxi's TaxiController by Gley pool index.</summary>
        private static TaxiController? FindTaxiByIndex(int index)
        {
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<VehicleComponent>());
            if (arr == null) return null;
            for (int i = 0; i < arr.Length; i++)
            {
                var vc = arr[i].TryCast<VehicleComponent>();
                if (vc == null || vc.GetIndex() != index) continue;
                var tcComp = VehicleManager.FindComponentByName(vc.gameObject, "TaxiController");
                return tcComp != null ? tcComp.TryCast<TaxiController>() : null;
            }
            return null;
        }

        /// <summary>
        /// Host: stops the real traffic taxi a client hailed by invoking the
        /// game's own TaxiController.RequestVehicleStop().  Every ghost follows
        /// the host's real taxi, so all players see the same thing.  An auto-
        /// resume is scheduled so the taxi doesn't become a permanent fixture.
        /// </summary>
        public static void HostStopTaxi(int index)
        {
            if (!MPServer.IsRunning) return;
            try
            {
                var taxi = FindTaxiByIndex(index);
                if (taxi == null)
                {
                    Plugin.Logger.LogWarning($"[TrafficSync] HostStopTaxi: no taxi with index {index}.");
                    return;
                }
                // RequestVehicleStop() is private — reflect on the real
                // TaxiController type so the instance matches the method.
                var m = typeof(TaxiController).GetMethod("RequestVehicleStop",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (m == null)
                {
                    Plugin.Logger.LogWarning("[TrafficSync] RequestVehicleStop not found on TaxiController.");
                    return;
                }
                m.Invoke(taxi, null);
                _taxiResumeAt[index] = Time.unscaledTime + TaxiStopDuration;   // schedule auto-resume
                Plugin.Logger.LogInfo($"[TrafficSync] Host stopped taxi index {index} (client hail).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] HostStopTaxi: {ex.Message}");
            }
        }

        /// <summary>Host: resumes hailed taxis whose stop duration has elapsed.</summary>
        private static void TickTaxiResumes()
        {
            if (_taxiResumeAt.Count == 0) return;
            float now = Time.unscaledTime;
            List<int>? due = null;
            foreach (var kv in _taxiResumeAt)
                if (now >= kv.Value) (due ??= new List<int>()).Add(kv.Key);
            if (due == null) return;

            foreach (var index in due)
            {
                _taxiResumeAt.Remove(index);
                HostResumeTaxi(index);
            }
        }

        /// <summary>
        /// Host: resumes a stopped taxi by writing the saved drive state back to
        /// the TrafficManager's job-level state (NativeArrays) via the same
        /// UpdateDrivingState path the stop used.  RequestVehicleStop saved both
        /// `_lastDriveAction` and `_lastActionValue` for exactly this.
        /// </summary>
        private static void HostResumeTaxi(int index)
        {
            try
            {
                var taxi = FindTaxiByIndex(index);
                if (taxi == null) return;                  // despawned — fine

                const BindingFlags f = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var tt = typeof(TaxiController);
                var lastAct = tt.GetProperty("_lastDriveAction",  f)?.GetValue(taxi);
                var lastVal = tt.GetProperty("_lastActionValue",  f)?.GetValue(taxi);
                if (lastAct == null || lastVal == null)
                {
                    Plugin.Logger.LogWarning(
                        $"[TrafficSync] HostResumeTaxi {index}: missing saved drive state.");
                    return;
                }

                var tm = TrafficManager.Instance;
                if (tm == null) return;
                var m = typeof(TrafficManager).GetMethod("UpdateDrivingState",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (m == null)
                {
                    Plugin.Logger.LogWarning("[TrafficSync] UpdateDrivingState not found on TrafficManager.");
                    return;
                }
                // UpdateDrivingState(int index, SpecialDriveActionTypes action, float value)
                m.Invoke(tm, new object[] { index, lastAct, lastVal });
                Plugin.Logger.LogInfo($"[TrafficSync] Resumed taxi index {index} (UpdateDrivingState).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] HostResumeTaxi {index}: {ex.Message}");
            }
        }

        // ── Client: suppress local traffic ────────────────────────────────────

        /// <summary>
        /// Kills the client's own Gley traffic by disabling the TrafficManager
        /// component outright — no simulation, no spawner, in any area.  Far more
        /// reliable than density-0 + clear, which lost the race when the client
        /// moved into freshly-activated grid areas.
        /// </summary>
        private static void SuppressLocalTraffic()
        {
            var tm = TrafficManager.Instance;
            if (tm == null) return;
            if (!tm.enabled) return;                 // already killed

            try { tm.ClearTraffic(); } catch { }
            tm.enabled = false;                      // stops Update/FixedUpdate → no sim, no spawn
            if (!_clientTrafficKilled)
            {
                _clientTrafficKilled = true;
                Plugin.Logger.LogInfo("[TrafficSync] Local traffic killed (TrafficManager disabled + cleared).");
            }
        }
    }
}
