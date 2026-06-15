using System.Reflection;
using UnityEngine;
using Helpers;
using GleyTrafficSystem;

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
            // Dead reckoning: chase a target EXTRAPOLATED along the car's
            // measured velocity, so the ghost never sits still between 10 Hz
            // snapshots.  (Plain lerp-to-last-target made cars move in stints
            // at low client FPS — reach target, freeze, jump on next packet.)
            public Vector3       Velocity;
            public float         TargetAt;            // CLIENT unscaled time TargetPos arrived
            public float         HostT;               // HOST sample time of TargetPos (packet stamp)
        }
        // Don't predict further than this past the last packet — a stopped or
        // turning car otherwise overshoots while we wait for fresh data.
        private const float MaxExtrapolateSeconds = 0.3f;

        // Client view culling for traffic ghosts: only embody cars near OUR
        // player (the stream covers cars around every player).  Spawn inside
        // ViewRadius, release beyond CullRadius (hysteresis).
        private const float GhostViewRadius = 130f;
        private const float GhostCullRadius = 160f;

        // Keyed by the host's Gley pool index.
        private static readonly Dictionary<int, TrafficGhost> _ghosts = new();

        /// <summary>Client-side count of spawned traffic ghosts — perf correlation.</summary>
        public static int ClientTrafficGhostCount => _ghosts.Count;

        // A networked position jump bigger than this is a reused pool slot or a
        // teleport — snap the ghost rather than sliding it across the screen.
        private const float SnapDistance = 12f;

        // One-time colour diagnostics (host reads / client applies).
        // ColorDumpEnabled gates the FULL per-vehicle material dump (~360 log
        // lines + a renderer/property sweep per dumped car).  It produced 79k
        // log lines in one session — re-enable only when actively debugging
        // vehicle colours.
        private static readonly bool ColorDumpEnabled = false;
        private static int _colorDiagHost;
        private static int _colorDiagClient;
        // Indices whose full colour state has been dumped (host / client).
        // Separate caps for trucks vs regular cars so we're guaranteed to see
        // truck data even if non-trucks fill the first 4 slots.
        private static readonly HashSet<int> _colorDumpHost   = new();
        private static readonly HashSet<int> _colorDumpClient = new();
        private static readonly HashSet<int> _truckDumpHost   = new();
        private static readonly HashSet<int> _truckDumpClient = new();

        /// <summary>Heuristic: does this prefab name look like a delivery/box truck?</summary>
        private static bool LooksLikeTruck(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string m = model.ToLowerInvariant();
            return m.Contains("truck") || m.Contains("box")
                || m.Contains("delivery") || m.Contains("van")
                || m.Contains("lorry") || m.Contains("cargo");
        }

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
            _truckDumpHost.Clear();
            _truckDumpClient.Clear();
            _ghosts.Clear();          // ghost GameObjects die with the old scene

            // Ghost anchor (#7) — clear last-outside memory so a new game/save
            // doesn't keep spawning traffic at the previous session's location.
            _hasOutsidePos = false;
            if (_ghostAnchorGO != null) _ghostAnchorGO.transform.position = Vector3.zero;
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
                    long tb = MPPerf.Begin(); UpdateTrafficAnchors(); MPPerf.End("Tr.Anchor", tb);
                    TickTaxiResumes();

                    _hostBroadcastTimer -= Time.unscaledDeltaTime;
                    if (_hostBroadcastTimer <= 0f)
                    {
                        _hostBroadcastTimer = BroadcastInterval;
                        tb = MPPerf.Begin(); var s = BuildSnapshot(); MPPerf.End("Tr.Build", tb);
                        tb = MPPerf.Begin(); MPServer.BroadcastTrafficSnapshot(s); MPPerf.End("Tr.Send", tb);
                    }

                    _lightBroadcastTimer -= Time.unscaledDeltaTime;
                    if (_lightBroadcastTimer <= 0f)
                    {
                        _lightBroadcastTimer = 0.5f;     // lights change slowly
                        tb = MPPerf.Begin();
                        var lights = BuildLightSnapshot();
                        if (lights != null) MPServer.BroadcastTrafficLights(lights);
                        MPPerf.End("Tr.Light", tb);
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

        // ── Cached vehicle-pool enumeration ───────────────────────────────────
        // FindObjectsOfType walks the ENTIRE scene (tens of thousands of
        // objects) — at the 10 Hz broadcast rate it alone made TrafficSync cost
        // ~59ms per frame on the host (profiler-measured 2026-06-09, the host
        // choppiness).  Gley pre-instantiates its vehicle pool, so the
        // VehicleComponent set is stable: enumerate ONCE including inactive
        // pool members, refresh rarely, and filter activeInHierarchy per use.
        private static UnityEngine.Object[]? _vcPool;
        private static float _vcPoolAt = -999f;
        // 10s: one scene scan per 10s is ~free (vs 10/sec before) and picks up
        // any pool growth (UpdateMaxCars raises the budget per player) quickly.
        private const float VcPoolRefreshSeconds = 10f;

        private static UnityEngine.Object[]? GetVehiclePool()
        {
            float now = Time.unscaledTime;
            if (_vcPool != null && now - _vcPoolAt < VcPoolRefreshSeconds) return _vcPool;
            // Gley's own registry first (perf pass 2026-06-12): the old
            // FindObjectsOfType(includeInactive) walk cost 60-80ms per refresh
            // — a visible rhythmic hitch on the host.  TrafficVehicles holds
            // the complete pool; the walk remains only as fallback.
            try
            {
                var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
                if (list != null && list.Count > 0)
                {
                    var arr = new UnityEngine.Object[list.Count];
                    for (int i = 0; i < list.Count; i++) arr[i] = list[i];
                    _vcPool   = arr;
                    _vcPoolAt = now;
                    return _vcPool;
                }
            }
            catch { }
            try
            {
                _vcPool   = UnityEngine.Object.FindObjectsOfType(typeof(VehicleComponent), true);
                _vcPoolAt = now;
            }
            catch { _vcPool = null; }
            return _vcPool;
        }

        /// <summary>Drop the cached pool (scene unload / session end).</summary>
        public static void InvalidateVehiclePool() { _vcPool = null; _vcPoolAt = -999f; _carColors.Clear(); }

        /// <summary>Host-side count of active driving vehicles in the world — for the
        /// startup "world is populated" gate.</summary>
        public static int HostTrafficCount()
        {
            try
            {
                var arr = GetVehiclePool();
                if (arr == null) return 0;
                int n = 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    var vc = arr[i] as VehicleComponent;
                    if (vc == null) continue;
                    var go = vc.gameObject;
                    if (go != null && go.activeInHierarchy) n++;
                }
                return n;
            }
            catch { return 0; }
        }

        // ── Host: build the traffic snapshot ──────────────────────────────────

        // Body colours cached per pool index.  Gley repaints a slot only when it
        // recycles it for a new spawn — which teleports the car — so re-read on
        // model change or a >SnapDistance jump instead of every snapshot (the
        // per-renderer material reads were the other half of the 59ms).
        private sealed class CarColorEntry { public string Model = ""; public Vector3 Pos; public List<float> Colors = new(); }
        private static readonly Dictionary<int, CarColorEntry> _carColors = new();

        private static TrafficSnapshotPayload BuildSnapshot()
        {
            var snap = new TrafficSnapshotPayload { T = Time.unscaledTime };
            try
            {
                var arr = GetVehiclePool();
                if (arr == null) return snap;
                for (int i = 0; i < arr.Length; i++)
                {
                    var vc = arr[i] as VehicleComponent;
                    if (vc == null) continue;
                    var go = vc.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;

                    var t = vc.transform;
                    var pos = t.position;
                    var rot = t.rotation;
                    int index = vc.GetIndex();

                    // Model name + paint cached per pool slot.  A recycle (new
                    // car in this slot) ALWAYS teleports, so a small move means
                    // it's the same live car — skip the go.name read too (it
                    // allocated an IL2CPP string per car per broadcast: ~480
                    // allocs/sec of collector pressure = rhythmic GC hitches).
                    string model;
                    List<float> colors;
                    if (_carColors.TryGetValue(index, out var cc)
                        && (pos - cc.Pos).sqrMagnitude < SnapDistance * SnapDistance)
                    {
                        model  = cc.Model;
                        colors = cc.Colors;
                        cc.Pos = pos;
                    }
                    else
                    {
                        model  = StripCloneSuffix(go.name);
                        colors = ReadBodyColors(index, go);
                        _carColors[index] = new CarColorEntry { Model = model, Pos = pos, Colors = colors };
                    }

                    if (_colorDiagHost < 8)
                    {
                        _colorDiagHost++;
                        Plugin.Logger.LogInfo(
                            $"[TrafficColor] host car[{index}] '{model}' {colors.Count / 6} group(s)");
                    }
                    bool truckH = LooksLikeTruck(model);
                    bool dumpH  = ColorDumpEnabled && (truckH
                        ? (_truckDumpHost.Count < 4 && _truckDumpHost.Add(index))
                        : (_colorDumpHost.Count < 2 && _colorDumpHost.Add(index)));
                    if (dumpH) DumpFullCarColor(go, index, "HOST", model);

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
        /// Diagnostic — logs the complete colour/material state of a car.
        /// For every non-wheel renderer dumps EACH material in `sharedMaterials`
        /// (sub-meshes — a single MeshRenderer often has multiple slots, e.g. a
        /// truck cab + box on the same mesh) with shader, name, all Color/
        /// Vector/Float/Texture properties (shared + MPB).  Run on the host
        /// (real car) and client (ghost) for the same index; diffing the two
        /// pinpoints exactly what differs.
        /// </summary>
        private static void DumpFullCarColor(GameObject car, int index, string side, string model)
        {
            try
            {
                var rootT = car.transform;
                var mpb = new MaterialPropertyBlock();
                var rends = car.GetComponentsInChildren(typeof(Renderer), true);
                int dumped = 0;
                Plugin.Logger.LogInfo(
                    $"[ColorDump] === {side} idx={index} model='{model}' renderers={rends.Length} ===");

                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i] as Renderer;
                    if (r == null) continue;
                    if (r.gameObject.name.Contains("Wheel")) continue;   // noise

                    string path = BuildHierarchyPath(r.transform, rootT);
                    string rType = r.GetType().Name;

                    // sharedMaterials = all sub-mesh slots (cab vs box can be
                    // separate slots on the same MeshRenderer).
                    var mats = r.sharedMaterials;
                    int matCount = mats != null ? mats.Length : 0;

                    if (matCount == 0)
                    {
                        Plugin.Logger.LogInfo(
                            $"[ColorDump] {side} idx={index} [{dumped}] '{path}' ({rType}) NO_MAT");
                        dumped++;
                        continue;
                    }

                    r.GetPropertyBlock(mpb);

                    for (int m = 0; m < matCount; m++)
                    {
                        var mat = mats[m];
                        if (mat == null || mat.shader == null)
                        {
                            Plugin.Logger.LogInfo(
                                $"[ColorDump] {side} idx={index} [{dumped}.{m}] '{path}' ({rType}) NULL_MAT");
                            continue;
                        }

                        var sh = mat.shader;
                        int n = sh.GetPropertyCount();
                        Plugin.Logger.LogInfo(
                            $"[ColorDump] {side} idx={index} [{dumped}.{m}] '{path}' ({rType}) shader='{sh.name}' mat='{mat.name}' props={n}");

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
                                case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                {
                                    var st = mat.GetTexture(pn);
                                    var mt = mpb.GetTexture(pn);
                                    val = $"TEX s='{(st != null ? st.name : "<null>")}'(id={(st != null ? st.GetInstanceID() : 0)}) m='{(mt != null ? mt.name : "<null>")}'(id={(mt != null ? mt.GetInstanceID() : 0)})";
                                    break;
                                }
                                default:
                                    continue;   // skip Int / others
                            }
                            Plugin.Logger.LogInfo($"[ColorDump] {side} idx={index} [{dumped}.{m}]   {pn} : {val}");
                        }
                    }
                    dumped++;
                }
                Plugin.Logger.LogInfo(
                    $"[ColorDump] === {side} idx={index} end ({dumped} non-wheel renderers) ===");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ColorDump] {side} idx={index}: {ex.Message}");
            }
        }

        /// <summary>"Body/Cab/RoofMesh" — hierarchy path relative to the car root.</summary>
        private static string BuildHierarchyPath(Transform t, Transform root)
        {
            if (t == null) return "<null>";
            if (t == root) return t.name;
            var sb = new System.Text.StringBuilder(t.name);
            var cur = t.parent;
            while (cur != null && cur != root)
            {
                sb.Insert(0, "/");
                sb.Insert(0, cur.name);
                cur = cur.parent;
            }
            return sb.ToString();
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
        // CRITICAL: a single Renderer can carry multiple materials via sub-meshes
        // (Renderer.sharedMaterials).  The Freightliner truck's body renderer has
        // 3 slots: [0] M_Freightliner Truck_Back (HDRP/Lit, the trailer),
        // [1] M_Freightliner Truck_Cabin (SH_Vehicle, the recolored cab),
        // [2] M_GlassTransCars (HDRP/Lit, windows).  Reading only sharedMaterial
        // (= slot 0) misses the cab entirely.  Scan ALL slots to find SH_Vehicle.
        //
        // MaterialPropertyBlock is per-RENDERER (not per-slot), so all SH_Vehicle
        // materials on the same renderer share a single MPB colour.  We only
        // need to find ONE SH_Vehicle material on each renderer to discover the
        // shader's property names; the MPB write then affects every SH_Vehicle
        // slot on that renderer.
        //
        // Host: vehicle index → cached body renderers (pooled GameObjects keep
        // their refs valid even when Gley recycles the slot to a different car).
        private static readonly Dictionary<int, List<Renderer>> _carRenderers = new();

        /// <summary>First SH_Vehicle material in any sharedMaterials slot, or null.</summary>
        private static Material? FindShVehicleMaterial(Renderer r)
        {
            if (r == null) return null;
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

        /// <summary>All renderers with at least one SH_Vehicle material slot, cached per index.</summary>
        private static List<Renderer> GetCarRenderers(int index, GameObject car)
        {
            if (_carRenderers.TryGetValue(index, out var cached) &&
                cached.Count > 0 && cached[0] != null)
                return cached;

            var list = new List<Renderer>();
            try
            {
                var rends = car.GetComponentsInChildren(typeof(Renderer), true);
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i] as Renderer;
                    if (r == null) continue;
                    if (FindShVehicleMaterial(r) != null)       // any sub-mesh slot
                        list.Add(r);
                }
            }
            catch { }
            _carRenderers[index] = list;
            return list;
        }

        /// <summary>Reads a renderer's two SH_Vehicle tint colours from its MPB.</summary>
        private static (Color, Color) ReadRendererColors(Renderer r)
        {
            var mat = FindShVehicleMaterial(r);                 // any slot, not just [0]
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

                var rends = ghost.GetComponentsInChildren(typeof(Renderer), true);
                int ri = 0, applied = 0;
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i] as Renderer;
                    if (r == null) continue;
                    var mat = FindShVehicleMaterial(r);             // scan ALL slots, not just [0]
                    if (mat == null || mat.shader == null) continue;

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
                    var ti = el != null ? el as TrafficLightsIntersection : null;
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
                    var ti = el != null ? el as TrafficLightsIntersection : null;
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
        // CLAUDE-DIAGNOSTIC — master kill-switch flag for client traffic ghosts.
        // ApplySnapshot returns early when false.  Used by the F4 master toggle
        // to find which client-side sync subsystem breaks the building-entry chain.
        public static bool ClientGhostApplyEnabled { get; set; } = true;

        public static void ApplySnapshot(TrafficSnapshotPayload snap)
        {
            if (snap == null) return;
            if (SaveGameManager.Current == null) return;
            if (!ClientGhostApplyEnabled) return;     // CLAUDE-DIAGNOSTIC kill-switch
            try
            {
                // View culling: ghosts only need to exist near OUR player — the
                // host streams cars simulated around EVERY player, and the ~half
                // near the other player are invisible from here.  Mirrors the
                // parked-ghost culling (spawn inside ViewRadius, release beyond
                // CullRadius — hysteresis so boundary cars don't flap).  Targets
                // keep streaming, so a car pops back in the moment it's near.
                Vector3 me = default; bool haveMe = false;
                try { me = PlayerHelper.GetPosition(); haveMe = true; } catch { }

                var seen = new HashSet<int>();
                foreach (var car in snap.Cars)
                {
                    seen.Add(car.Index);
                    var pos = new Vector3(car.X, car.Y, car.Z);
                    var rot = new Quaternion(car.Qx, car.Qy, car.Qz, car.Qw);

                    _ghosts.TryGetValue(car.Index, out var g);

                    if (haveMe)
                    {
                        float sq = (pos - me).sqrMagnitude;
                        bool isGhost = g != null && g.Go != null;
                        if (!isGhost && sq > GhostViewRadius * GhostViewRadius)
                            continue;                                  // out of view — don't spawn
                        if (isGhost && sq > GhostCullRadius * GhostCullRadius)
                        {
                            try { UnityEngine.Object.Destroy(g!.Go); } catch { }
                            _ghosts.Remove(car.Index);
                            continue;                                  // left view — release
                        }
                    }

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
                        g = new TrafficGhost { Go = go, Model = car.Model, TargetPos = pos, TargetRot = rot, TargetAt = Time.unscaledTime, HostT = snap.T };
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
                            g.Velocity = Vector3.zero;
                        }
                        else
                        {
                            // Velocity from the HOST's sample clock (packet stamp)
                            // — client arrival times are frame-quantized and made
                            // the velocity estimate jitter.  If two packets land
                            // in one frame (tiny dt), KEEP the previous velocity
                            // (zeroing it froze extrapolation = visible stutter).
                            float hdt = snap.T - g.HostT;
                            if (hdt > 0.005f)
                                g.Velocity = (pos - g.TargetPos) / hdt;
                        }
                        g.TargetPos = pos;
                        g.TargetRot = rot;
                        g.TargetAt  = Time.unscaledTime;
                        g.HostT     = snap.T;
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
                    if (ColorDumpEnabled && g.Go != null && g.LastColors != null)
                    {
                        bool truckC = LooksLikeTruck(car.Model);
                        bool dumpC  = truckC
                            ? (_truckDumpClient.Count < 4 && _truckDumpClient.Add(car.Index))
                            : (_colorDumpClient.Count < 2 && _colorDumpClient.Add(car.Index));
                        if (dumpC) DumpFullCarColor(g.Go, car.Index, "CLIENT", car.Model);
                    }
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

        /// <summary>The prefab GameObject of a Gley CarType.  EA 0.11 (Mono):
        /// vehiclePrefab is a plain public FIELD — the old property-reflection
        /// scan found nothing and the prefab map silently went empty (every
        /// traffic ghost fell back to the generic spawn).</summary>
        private static GameObject? ExtractPrefab(CarType el)
        {
            try { return el.vehiclePrefab; }
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
                var comps = go.GetComponents(typeof(Component));
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    string cn = c.GetType().Name;

                    if (isTaxi && cn == "TaxiController")
                        continue;                              // keep — the interaction
                    if (isTaxi && cn == "VehicleComponent")
                    {
                        var beh = c as Behaviour;       // keep ref, kill its logic
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
            VehicleManager.StripCameras(go);   // stowaway cameras hijack the cursor pick ray
            try
            {
                // EVERY rigidbody in the hierarchy, not just the root — vehicle
                // prefabs carry rbs on children (carHolder/wheels), and a dynamic
                // one lets the local player physically shove the ghost around.
                // Kinematic = transform-driven immovable obstacle, like a wall.
                var rbs = go.GetComponentsInChildren(typeof(Rigidbody), true);
                for (int i = 0; i < rbs.Length; i++)
                {
                    var rb = rbs[i] as Rigidbody;
                    if (rb == null) continue;
                    rb.isKinematic = true;
                    rb.useGravity  = false;
                }
            }
            catch { }
            return go;
        }

        /// <summary>Smooths each traffic ghost toward its networked transform —
        /// chasing a target extrapolated along the car's measured velocity so
        /// motion stays continuous between 10 Hz packets even at low FPS.</summary>
        private static void TickGhosts()
        {
            if (_ghosts.Count == 0) return;
            // Cap the blend below 1 so packet corrections spread over a couple
            // of frames instead of landing as a visible pop at low FPS (the
            // uncapped factor saturates past ~70ms frames).
            float k   = Mathf.Min(Time.deltaTime * GhostLerp, 0.5f);
            float now = Time.unscaledTime;
            foreach (var g in _ghosts.Values)
            {
                if (g.Go == null) continue;
                var t = g.Go.transform;
                float ahead = Mathf.Min(now - g.TargetAt, MaxExtrapolateSeconds);
                var predicted = g.TargetPos + g.Velocity * ahead;
                t.position = Vector3.Lerp(t.position, predicted, k);
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

        // #7 — when host enters a building we need to KEEP an exterior anchor
        // so Gley keeps spawning traffic.  Removing the host's anchor only
        // works if a client is outside; in solo / both-inside cases, anchors
        // hit zero and traffic stops.  Persistent fix: a "ghost anchor" pinned
        // at the host's LAST outside position.  As long as the host has been
        // outside once this session, the traffic system continues to simulate
        // around that position while they're indoors.
        private static GameObject? _ghostAnchorGO;
        private static Vector3 _lastOutsidePos;
        private static bool _hasOutsidePos;

        private static Transform GetOrCreateGhostAnchor()
        {
            if (_ghostAnchorGO == null)
            {
                _ghostAnchorGO = new GameObject("BAMP_TrafficGhostAnchor");
                UnityEngine.Object.DontDestroyOnLoad(_ghostAnchorGO);
            }
            return _ghostAnchorGO.transform;
        }

        private static void UpdateTrafficAnchors()
        {
            try
            {
                var tm = TrafficManager.Instance;
                if (tm == null) return;
                var dm = tm.densityManager;

                var anchors = new List<Transform>();
                var hostChar = PlayerHelper.PlayerController?.Character;
                if (hostChar != null)
                {
                    // Inside/outside from the GAME's authoritative static, not
                    // just our enter/exit event flag — a session LOAD skips the
                    // exit event, leaving the flag stuck TRUE while the player
                    // stands outside with no outside-pos memory → zero anchors
                    // → no traffic at all (user, 2026-06-12).
                    bool inside = LocalInBuilding;
                    try { inside = BuildingManager.IsInsideBuilding; } catch { }
                    LocalInBuilding = inside;   // resync the event flag
                    if (!inside)
                    {
                        // Outside — use the live transform AND remember it so
                        // we can pin the ghost anchor here if we go inside.
                        anchors.Add(hostChar.transform);
                        _lastOutsidePos = hostChar.transform.position;
                        _hasOutsidePos  = true;
                    }
                    else if (_hasOutsidePos)
                    {
                        // Inside — use the persistent ghost anchor parked at
                        // the last outside position, so traffic keeps simulating
                        // exactly where we left off.
                        var ga = GetOrCreateGhostAnchor();
                        ga.position = _lastOutsidePos;
                        anchors.Add(ga);
                    }
                    else
                    {
                        // Inside with NO outside memory (fresh load straight
                        // into a building) — feed the player transform anyway;
                        // traffic around the building beats a dead feed.
                        anchors.Add(hostChar.transform);
                    }
                }
                foreach (var t in RemotePlayerManager.GetRemotePlayerTransforms())
                    if (t != null) anchors.Add(t);
                if (anchors.Count == 0) return;

                var arr = new Transform[anchors.Count];
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

        // ── Taxi travel fast-forward exemption (backlog #5) ───────────────────
        // The game's taxi travel uses TaxiController.TaxiTravel which calls
        // GameSpeedController.Set with isFastForwarding=true and advances the
        // world clock by the trip duration.  Our world-clock pinner (MPCanvasUI.
        // TickWorldClock) would normally revert each advance every frame —
        // result: the taxi ride coroutine waits forever for time to move
        // forward and the player is "locked up" in the cab.
        //
        // While LocalInTaxi is set the world-clock pinner gets out of the way.
        // Once the ride completes (`CompletedTaxiRide`) we reset the pinner's
        // window to the new clock so it doesn't see the advance retroactively
        // as a skip and roll it back.
        public static bool LocalInTaxi { get; private set; }

        public static void OnTaxiTravelStart()
        {
            if (LocalInTaxi) return;
            LocalInTaxi = true;
            Plugin.Logger.LogInfo("[Taxi] TaxiTravel start — world-clock suppression OFF until ride ends.");
        }

        public static void OnTaxiTravelEnd()
        {
            if (!LocalInTaxi) return;
            LocalInTaxi = false;
            Plugin.Logger.LogInfo("[Taxi] CompletedTaxiRide — world-clock suppression re-armed at post-ride time.");
            // World-clock detector resets itself on the next TickWorldClock pass
            // because `LocalInTaxi` was true the previous frame; the detector
            // already short-circuits in that case.  No explicit reset needed —
            // see MPCanvasUI.TickWorldClock.
        }

        // ── Building entry / exit (backlog #6 + #7) ───────────────────────────
        // When the local player enters a building, Big Ambitions teleports them
        // to an interior position (often far from the outside world).  For the
        // host this means our traffic anchors include a "host" point inside the
        // building — Gley then spawns traffic in the interior, which has no
        // roads, so the outside world (where the client is) goes empty (#7).
        //
        // For the client, entering a building has been observed to freeze on a
        // black screen (#6).  Root cause TBD — Harmony patches added below give
        // us [Building] entry/exit logs so we can diagnose where the flow
        // stalls.
        public static bool LocalInBuilding { get; private set; }

        public static void OnEnteredBuilding(string? where = null)
        {
            if (LocalInBuilding) return;
            LocalInBuilding = true;
            Plugin.Logger.LogInfo($"[Building] EnteredBuilding{(where != null ? " (" + where + ")" : "")} — local player inside.");
            // Re-arm anchor diag so we re-log the active set after the host
            // moves between exterior / interior.
            _anchorDiagLogged = false;
        }

        public static void OnExitFromBuilding(string? where = null)
        {
            if (!LocalInBuilding) return;
            LocalInBuilding = false;
            Plugin.Logger.LogInfo($"[Building] ExitFromBuilding{(where != null ? " (" + where + ")" : "")} — local player outside.");
            _anchorDiagLogged = false;
        }

        /// <summary>Resolves a clicked taxi GameObject to its Gley pool index.</summary>
        private static int ResolveTaxiIndex(GameObject go)
        {
            // Client: the clicked taxi is one of our ghosts — keyed by pool index.
            foreach (var kv in _ghosts)
                if (kv.Value.Go == go) return kv.Key;
            // Host: a real traffic taxi — read its Gley pool index.
            var vcComp = VehicleManager.FindComponentByName(go, "VehicleComponent");
            var vc = vcComp != null ? vcComp as VehicleComponent : null;
            return vc != null ? vc.GetIndex() : -1;
        }

        /// <summary>Finds a live traffic taxi's TaxiController by Gley pool index.</summary>
        private static TaxiController? FindTaxiByIndex(int index)
        {
            var arr = GetVehiclePool();
            if (arr == null) return null;
            for (int i = 0; i < arr.Length; i++)
            {
                var vc = arr[i] as VehicleComponent;
                if (vc == null || vc.GetIndex() != index) continue;
                var tcComp = VehicleManager.FindComponentByName(vc.gameObject, "TaxiController");
                return tcComp != null ? tcComp as TaxiController : null;
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
                var lastAct = MPReflect.Get(tt, taxi, "_lastDriveAction");
                var lastVal = MPReflect.Get(tt, taxi, "_lastActionValue");
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
            // CLAUDE-DIAGNOSTIC — toggle flag for backlog #6 first-domino test.
            if (!ClientTrafficSuppressionEnabled) return;

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

        // CLAUDE-DIAGNOSTIC — F11 toggle for the entry-bug investigation.
        // Default ON.  Flipping OFF stops SuppressLocalTraffic from running
        // and re-enables TrafficManager so we can test whether disabling it
        // is what prevents BuildingManager.DelayedEnterBuildingActions from
        // firing on the client.
        public static bool ClientTrafficSuppressionEnabled { get; set; } = true;

        public static void ToggleClientTrafficSuppression()
        {
            ClientTrafficSuppressionEnabled = !ClientTrafficSuppressionEnabled;
            try
            {
                var tm = TrafficManager.Instance;
                if (tm != null && !ClientTrafficSuppressionEnabled)
                {
                    tm.enabled = true;     // un-suppress immediately
                }
                Plugin.Logger.LogInfo(
                    $"[ClientFix] Client traffic suppression → {ClientTrafficSuppressionEnabled} (TM.enabled={(tm != null ? tm.enabled.ToString() : "<null>")})");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ClientFix] traffic toggle: {ex.Message}"); }
        }
    }
}
