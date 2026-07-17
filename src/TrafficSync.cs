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
            public Rigidbody?    Body;                // cached ROOT rigidbody — driven via MovePosition so the
                                                      //   kinematic ghost acts as a solid obstacle (2026-06-16)
#if BAMP_DEV
            public Vector3       LastMoveTarget;      // where TickGhosts last placed it; drift from this = the real push
            public bool          HasMoveTarget;
#endif
        }
#if BAMP_DEV
        // [PushDrift] worst per-frame displacement of a NEAR ghost AWAY from where TickGhosts placed it
        //   (= physics/the player shoving it). The old dev=0 missed this: it sampled the car at its target
        //   AFTER the per-frame correction. This catches the transient shove. Reported + reset by [Push].
        private static float _maxGhostDrift;
        private static bool  _maxGhostDriftKin = true;
#endif
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
#if BAMP_DEV
                    TickCensus();
                    TickPushProbe();
#endif
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
                // Passenger riding a ghost: cull around the RIDDEN car (the real character is
                // parked at the boarding door), else only entry-time traffic stays visible.
                var rideT = PassengerRide.RideAnchorTransform();
                if (rideT != null) { me = rideT.position; haveMe = true; }
                else { try { me = PlayerHelper.GetPosition(); haveMe = true; } catch { } }

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

                    // A pool slot reused for a DIFFERENT car = respawn fresh instead of sliding the old
                    // ghost across the map (the streak; the red [StreakMarker] confirmed these were the
                    // streaking ghosts). Two tells of reuse between 10 Hz packets: the MODEL changed, OR the
                    // position jumped further than any real car could travel in 100 ms (> SnapDistance ≈
                    // >120 m/s). Same-model reuse used to slip the model check and slide; catching the big
                    // jump here is a clean break — old ghost destroyed, a fresh one spawns at the new pos
                    // below. (ANTIPATTERNS class 7: a reused pool index is not a stable identity.)
                    if (g != null && g.Go != null
                        && (g.Model != car.Model || Vector3.Distance(g.Go.transform.position, pos) > SnapDistance))
                    {
                        try { UnityEngine.Object.Destroy(g.Go); } catch { }
                        g = null;
                    }

                    if (g == null || g.Go == null)
                    {
                        var go = SpawnTrafficGhost(car.Model, pos, rot);
                        if (go == null) { _ghosts.Remove(car.Index); continue; }
                        g = new TrafficGhost { Go = go, Model = car.Model, TargetPos = pos, TargetRot = rot, TargetAt = Time.unscaledTime, HostT = snap.T };
                        g.Body = go.GetComponent<Rigidbody>();   // ROOT rb only (a child rb would teleport just that part)
                        _ghosts[car.Index] = g;
                    }
                    else
                    {
                        // Same live car, small inter-packet move (a big jump = slot reuse, respawned above).
                        // Velocity from the HOST's packet stamp for smooth extrapolation between 10 Hz packets;
                        // if two packets land in one client frame (tiny dt) keep the previous velocity (zeroing
                        // it froze extrapolation = visible stutter).
                        float hdt = snap.T - g.HostT;
                        if (hdt > 0.005f)
                            g.Velocity = (pos - g.TargetPos) / hdt;
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
            // Instantiate INACTIVE (field NREs 2026-07-16: AiCarRescueCheck.OnEnable
            // threw ×30 inside Instantiate — clone components wake up BEFORE the
            // strip below removes them).  Deactivating the pool template for the
            // clone call makes the clone start inactive, so no OnEnable runs until
            // after the strip; the template's own active state is restored either way.
            bool prefabWasActive = prefab.activeSelf;
            try
            {
                if (prefabWasActive) prefab.SetActive(false);
                go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] Instantiate '{model}': {ex.Message}");
                return VehicleManager.SpawnVisualGhost(model, pos, rot);    // fallback
            }
            finally
            {
                try { if (prefabWasActive) prefab.SetActive(true); } catch { }
            }

            // Strip Gley AI / audio / LOD components — leaves a pure visual prop.
            // Taxis are special: keep TaxiController (the fast-travel interaction)
            // and keep VehicleComponent (TaxiController holds a reference to it)
            // but disable VehicleComponent so its dead AI fires no triggers/updates.
            bool isTaxi = model.Equals("Taxi", StringComparison.OrdinalIgnoreCase);
            try
            {
                // DestroyImmediate in dependency order — mirrors VehicleManager.StripVehicleComponents.
                // AudioSource is the target of EngineSoundComponent's [RequireComponent]; deferred Destroy
                // validates that dependency at the CALL (execution is end-of-frame), so destroying the
                // AudioSource while EngineSoundComponent was still attached was REFUSED every time
                // ("Can't remove AudioSource because EngineSoundComponent depends on it" — 5,855× in one
                // client session). Remove every other kill-listed component FIRST, the AudioSource LAST.
                Component? audio = null;
                var others = new System.Collections.Generic.List<Component>();
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
                    if (System.Array.IndexOf(_killTrafficComponents, cn) < 0) continue;

                    // Taxi keeps a (disabled) VehicleComponent that may require the AudioSource — leave it inert.
                    if (cn == "AudioSource") { if (!isTaxi) audio = c; continue; }
                    others.Add(c);
                }
                foreach (var c in others) if (c != null) UnityEngine.Object.DestroyImmediate(c);
                if (audio != null) UnityEngine.Object.DestroyImmediate(audio);
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
            go.SetActive(true);   // activate ONLY now — surviving components wake on a pure visual prop
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
#if BAMP_DEV
            var _pcDrift = PlayerHelper.PlayerController?.Character;
            Vector3 _ppDrift = _pcDrift != null ? _pcDrift.transform.position : new Vector3(1e9f, 1e9f, 1e9f);
#endif
            foreach (var g in _ghosts.Values)
            {
                if (g.Go == null) continue;
                var t = g.Go.transform;
#if BAMP_DEV
                // Measure how far this ghost moved AWAY from where we last placed it — only for ghosts near
                //   the player (= the actual push). Tracked here every frame; the [Push] probe reports it.
                if (g.HasMoveTarget && (t.position - _ppDrift).sqrMagnitude < 25f)
                {
                    float _drift = (t.position - g.LastMoveTarget).magnitude;
                    if (_drift > _maxGhostDrift) { _maxGhostDrift = _drift; _maxGhostDriftKin = g.Body == null || g.Body.isKinematic; }
                }
#endif
                float ahead = Mathf.Min(now - g.TargetAt, MaxExtrapolateSeconds);
                var predicted = g.TargetPos + g.Velocity * ahead;
                Vector3    smoothedPos = Vector3.Lerp(t.position, predicted, k);
                Quaternion smoothedRot = Quaternion.Slerp(t.rotation, g.TargetRot, k);
#if BAMP_DEV
                Vector3 _pre = t.position;
#endif
                // 2026-06-16 (user-approved): drive the kinematic ghost with MovePosition/MoveRotation
                //   (a physics-correct swept move) instead of transform.position, so it acts as a SOLID
                //   obstacle that BLOCKS the local player and can't be shoved — like the host's real cars.
                //   Falls back to the transform if a ghost has no root rigidbody. (Evaluating — may need a
                //   FixedUpdate pass if it stutters.)
                if (g.Body != null)
                {
                    g.Body.MovePosition(smoothedPos);
                    g.Body.MoveRotation(smoothedRot);
                }
                else
                {
                    t.position = smoothedPos;
                    t.rotation = smoothedRot;
                }
#if BAMP_DEV
                g.LastMoveTarget = smoothedPos;   // record where we placed it; next frame's drift from this = the push
                g.HasMoveTarget  = true;
#endif
#if BAMP_DEV
                // DIAG:INVESTIGATION(traffic-streak) — a ghost moving a big distance in ONE frame, esp.
                //   SIDEWAYS. MovePosition defers the transform update, so measure the INTENDED move.
                {
                    Vector3 mv = smoothedPos - _pre;
                    float d = mv.magnitude;
                    if (d > 3f)
                    {
                        float offAxis = mv.sqrMagnitude > 0.0001f ? Vector3.Angle(t.forward, mv) : 0f;
                        Plugin.Logger.LogWarning(
                            $"[TrafStreak] {d:F1}m/frame offAxis={offAxis:F0}° vel={g.Velocity.magnitude:F1} ahead={ahead:F2} " +
                            $"from=({_pre.x:F0},{_pre.z:F0}) to=({smoothedPos.x:F0},{smoothedPos.z:F0}) tgt=({g.TargetPos.x:F0},{g.TargetPos.z:F0})");
                    }
                }
#endif
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
                // Passenger riding a ghost: anchor traffic spawning on the RIDDEN car, and skip
                // the frozen door-character anchor (so we don't also stream traffic back at the door).
                var rideAnchor = PassengerRide.RideAnchorTransform();
                if (rideAnchor != null) anchors.Add(rideAnchor);
                var hostChar = PlayerHelper.PlayerController?.Character;
                if (rideAnchor == null && hostChar != null)
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
#if BAMP_DEV
        // DIAG:INVESTIGATION(client-traffic) — is the client LEAKING local Gley cars despite
        //   SuppressLocalTraffic? ClearTraffic() is no-op'd by Patch_TM_ClearTraffic while MP-active,
        //   so suppression may only DISABLE the manager and leave already-spawned cars in the scene
        //   (dynamic = pushable [E]; mis-simulated = "streak" [D]). Census the survivors + the nearest
        //   one's kinematic state, 1 Hz. Pair with [TrafStreak]: streak + activeGley>0 ⇒ local Gley.
        private static float _nextCensus;
        private static void TickCensus()
        {
            if (Time.unscaledTime < _nextCensus) return;
            _nextCensus = Time.unscaledTime + 1f;
            try
            {
                var tm = TrafficManager.Instance;
                int gley = 0, active = 0; float nd = 999f; bool nk = false; string nn = "";
                var pc = PlayerHelper.PlayerController?.Character;
                Vector3 pp = pc != null ? pc.transform.position : Vector3.zero;
                var list = tm?.trafficVehicles?.GetVehicleList();
                if (list != null)
                {
                    gley = list.Count;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var comp = list[i] as Component;
                        if (comp == null || !comp.gameObject.activeInHierarchy) continue;
                        active++;
                        if (pc != null)
                        {
                            float d = Vector3.Distance(comp.transform.position, pp);
                            if (d < nd) { nd = d; nn = comp.gameObject.name; var rb = comp.GetComponentInChildren<Rigidbody>(); nk = rb == null || rb.isKinematic; }
                        }
                    }
                }
                Plugin.Logger.LogInfo(
                    $"[TrafCensus] gleyTM.enabled={(tm != null ? tm.enabled.ToString() : "<null>")} killed={_clientTrafficKilled} " +
                    $"gleyCars={gley} activeGley={active} ghosts={_ghosts.Count} pvGhosts={VehicleManager.RemoteVehicleCount} " +
                    $"nearestGley={(nn == "" ? "none" : $"'{nn}' d={nd:F1} kinematic={nk}")}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafCensus] {ex.Message}"); }
        }

        // DIAG:INVESTIGATION(push) — the client can push only DRIVING traffic cars, NOT parked ones
        //   (user-verified 2026-06-16). Both parked + driving ghosts are frozen kinematic + Gley-stripped in
        //   code; the ONLY differences are (1) TickGhosts sets driving ghosts' transform.position every frame,
        //   and (2) they're NOT in the local-player IgnoreCollision set (only player-vehicle ghosts are).
        //   Reports the nearest TRAFFIC ghost's RUNTIME state: rigidbodies really kinematic? solid collider?
        //   ignored vs the player? displaced from its synced target (= being pushed)?
        private static float _nextPushProbe;
        private static void TickPushProbe()
        {
            if (Time.unscaledTime < _nextPushProbe) return;
            _nextPushProbe = Time.unscaledTime + 0.5f;
            try
            {
                var pc = PlayerHelper.PlayerController?.Character;
                if (pc == null) return;
                Vector3 pp = pc.transform.position;

                var cc  = pc.GetComponentInChildren<CharacterController>(true);
                var prb = pc.GetComponentInChildren<Rigidbody>(true);
                string rbInfo = prb == null ? "none" : $"{(prb.isKinematic ? "kin" : "DYN")}/{prb.collisionDetectionMode}";

                // Nearest traffic ghost + its main SOLID collider (the body the player would contact).
                TrafficGhost? near = null; float ndSq = 8f * 8f;
                foreach (var g in _ghosts.Values)
                {
                    if (g.Go == null) continue;
                    float sq = (g.Go.transform.position - pp).sqrMagnitude;
                    if (sq < ndSq) { ndSq = sq; near = g; }
                }
                Collider? ghostSolid = null;
                if (near != null && near.Go != null)
                    foreach (var col in near.Go.GetComponentsInChildren<Collider>(true))
                        if (col != null && !col.isTrigger) { ghostSolid = col; break; }

                // DIAG(bubble): the PLAYER's OWN colliders (Character subtree ONLY — the earlier dump wrongly
                //   walked the shared scene root and swept in a parked car's parking sphere + every pedestrian).
                //   For each SOLID one, ",HITS"/",noHit" = does it actually collide with the ghost body (layer
                //   matrix ON and pair not ignored)? That pins the "pusher" vs the movement "blocker".
                var sbp = new System.Text.StringBuilder();
                foreach (var c in pc.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null) continue;
                    string dim = c is CapsuleCollider cap ? $"r{cap.radius:F2}h{cap.height:F2}"
                               : c is SphereCollider sph  ? $"r{sph.radius:F2}"
                               : c is BoxCollider box      ? $"{box.size.x:F1}x{box.size.z:F1}"
                               : "mesh";
                    string hit = "";
                    if (!c.isTrigger && ghostSolid != null)
                    {
                        bool layerOn = !Physics.GetIgnoreLayerCollision(c.gameObject.layer, ghostSolid.gameObject.layer);
                        bool pairOn  = !Physics.GetIgnoreCollision(c, ghostSolid);
                        hit = layerOn && pairOn ? ",HITS" : ",noHit";
                    }
                    sbp.Append($"{c.name}({c.GetType().Name.Replace("Collider", "")},{dim},{(c.isTrigger ? "trig" : "solid")},L{c.gameObject.layer}{hit}) ");
                }
                string playerInfo = $"CC={(cc != null)} rb={rbInfo} cols=[{sbp}]";

                if (near == null || near.Go == null) { Plugin.Logger.LogInfo($"[Push] player {playerInfo}; no traffic ghost within 8 m"); return; }

                var sb = new System.Text.StringBuilder();
                foreach (var col in near.Go.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null) continue;
                    var rb = col.attachedRigidbody;
                    sb.Append($"{col.name}[{(col.isTrigger ? "trig" : "solid")},{(rb == null ? "noRb" : rb.isKinematic ? "kin" : "DYN")},L{col.gameObject.layer}] ");
                }
                float dev = Vector3.Distance(near.Go.transform.position, near.TargetPos);
                Plugin.Logger.LogInfo($"[Push] player {playerInfo} | nearestTraffic d={Mathf.Sqrt(ndSq):F1} dev={dev:F1} maxDrift={_maxGhostDrift:F2}m(kin={_maxGhostDriftKin}) body={(near.Body != null ? "root(MovePosition)" : "none(transform)")} | ghostCols: {sb}");
                _maxGhostDrift = 0f;   // reset the measurement window after reporting
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Push] {ex.Message}"); }
        }

#endif

        private static void SuppressLocalTraffic()
        {
            if (!ClientTrafficSuppressionEnabled) return;

            var tm = TrafficManager.Instance;
            if (tm == null) return;

            // Kill the client's local Gley traffic — the client renders host-synced ghosts instead.
            // ClearTraffic is host-scoped again (Patch_TM_ClearTraffic no longer no-ops the client), so
            // it actually runs here now (the previous direct-deactivation workaround is gone). Re-assert
            // whenever the manager is live OR any car is still active: the census (2026-06-16) showed the
            // game can re-enable the manager mid-session and a one-shot disable stranded ~20 cars. Cheap
            // pool scan, early-out once clean. Ghosts unaffected (cloned from the cached prefab map).
            bool anyActive = false;
            try
            {
                var list = tm.trafficVehicles?.GetVehicleList();
                if (list != null)
                    for (int i = 0; i < list.Count && !anyActive; i++)
                        if (list[i] is Component c && c.gameObject.activeSelf) anyActive = true;
            }
            catch { }

            if (tm.enabled || anyActive)
            {
                try { tm.ClearTraffic(); } catch { }
                tm.enabled = false;                  // stops Update/FixedUpdate → no sim, no spawn
                if (!_clientTrafficKilled)
                {
                    _clientTrafficKilled = true;
                    Plugin.Logger.LogInfo("[TrafficSync] Local traffic killed (ClearTraffic + manager disabled).");
                }
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
