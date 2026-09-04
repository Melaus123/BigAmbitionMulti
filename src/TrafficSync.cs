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
        private const float BroadcastInterval = 0.2f;   // host snapshot rate — T2: 5 Hz (the client dead-reckons
                                                        // up to 0.3 s, so 0.2 s packets sit inside its own tolerance)
        private const float HostSendRadius    = 195f;   // T2 + review M2: the parked system's 95/125/160 triple keeps a
                                                        // 35 m margin between the CLIENT cull ring and the host send ring
                                                        // (movement margin between beats; the two tests also use different
                                                        // anchors). 160==160 had zero margin → boundary cars churned
                                                        // identity re-sends and Destroy/Instantiate at the ring edge.

        // Wave-2 (measured 7.5 KB/s of ~95%-unchanged data): light DIFFS between full re-asserts.
        private const float LightsFullReassertSeconds = 10f;
        private static readonly Dictionary<int, (int road, bool yellow)> _lightLastSent = new();
        private static float _lightsFullSentAt = -999f;
        private const float GhostLerp         = 14f;    // client ghost chase rate
        private const float TaxiStopDuration  = 18f;    // how long a hailed taxi stays stopped
        // VANILLA TRAFFIC (user ruling 2026-09-02: "whatever traffic they are supposed to see, they see, regardless of
        // whether someone else is nearby"). The game sets its own count — TimeOfDayController.UpdateTrafficDensity:
        // round(curve(time, rain) × neighbourhood percentage) → Manager.SetTrafficDensity(n). The host records that
        // request (Patch_TM_SetTrafficDensity_ClientZero) and feeds Gley n × (distinct player AREAS): alone = exactly
        // the game's number; players within one despawn radius of each other share ONE area (the June "24 per player"
        // rule doubled the cars when two players stood together — F-2026-09-02-AD). Remote players in a different
        // neighbourhood get the host's neighbourhood value (the game has no position-based neighbourhood lookup).
        internal static int   GameDensityRequest = -1;   // last host-side SetTrafficDensity(n) seen; -1 = none yet
        private  static int   _lastBudgetLogged  = -1, _lastAreasLogged = -1;
        private const  float  AreaRadiusFallback = 150f;

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
            public Collider[]?   Solids;              // MINOR-7 (2026-09-02): non-trigger colliders cached at spawn (shove belt)
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

        // T2: last identity seen per pool slot — bridges the identity-less packets between the host's
        // identity re-sends (first sight / recycle / radius re-entry all carry Model+Colors).
        private static readonly Dictionary<int, (string model, List<float> colors)> _slotIdentity = new();

        /// <summary>Client-side count of spawned traffic ghosts — perf correlation.</summary>
        public static int ClientTrafficGhostCount => _ghosts.Count;

        /// <summary>Review BLOCKER-2 (2026-09-02): Unity raises no OnTriggerExit for a collider destroyed inside a
        /// trigger, so a Gley car braking for a ghost would keep that dead collider in its obstacle list and hold
        /// StopInDistance forever. The game calls Manager.TriggerColliderRemovedEvent before every such teardown
        /// (PedestrianPool, PlayerController, VehicleParkingHelper, …) — so does every ghost destroy path now.
        /// No-op for cars not sensing the collider.</summary>
        internal static void NotifyCollidersRemoved(GameObject? go, Collider[]? cachedSolids = null)
        {
            if (go == null) return;
            try
            {
                // Review #2 MINOR-2: TrafficManager.Instance lazily CREATES a manager — test without touching the getter.
                if (!TrafficManager.HasInstance || !TrafficManager.IsInitialized) return;
                // Review #2 MINOR-6: only solids can sit in an obstacle list (a trigger qualifies only with the
                // AiVehicleHalt tag) — use the spawn-time cache when the caller has one, else a solids-only walk.
                if (cachedSolids != null)
                {
                    foreach (var c in cachedSolids) if (c != null) { try { Manager.TriggerColliderRemovedEvent(c); } catch { } }
                    return;
                }
                foreach (var c in go.GetComponentsInChildren<Collider>(true))
                    if (c != null && (!c.isTrigger || c.CompareTag("AiVehicleHalt"))) { try { Manager.TriggerColliderRemovedEvent(c); } catch { } }
            }
            catch { }
        }

        /// <summary>Solid colliders of every traffic ghost (2026-09-02, ServiceCars shove belt): a client's locally
        /// spawned private-driver / arrival car must not be shoved by the host's kinematic traffic mirrors.</summary>
        public static List<Collider> AllTrafficGhostColliders()
        {
            var result = new List<Collider>();
            try
            {
                foreach (var g in _ghosts.Values)
                {
                    if (g?.Go == null || g.Solids == null) continue;   // MINOR-7: cached at spawn, no per-call hierarchy walk
                    foreach (var c in g.Solids) if (c != null) result.Add(c);
                }
            }
            catch { }
            return result;
        }

        // A networked position jump bigger than this is a reused pool slot or a
        // teleport — snap the ghost rather than sliding it across the screen.
        private const float SnapDistance = 12f;

        private static float _hostBroadcastTimer;
        private static float _lightBroadcastTimer;
        private static bool  _clientTrafficKilled;
        private static int   _nonGhostHailLogs;   // throttles the declined-hail line in OnLocalTaxiHailed

        // model name → traffic-car prefab, built once from Gley's VehiclePool.
        private static Dictionary<string, GameObject>? _trafficPrefabs;
        private static float _prefabWaitNextLog;   // round-188: throttles the pool-not-ready deferral line

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

            // Review B1/MIN-3: the wave-1/2 per-peer and per-slot state dies with the scene too — a
            // re-host in the same process otherwise inherits the previous world's identity maps
            // (previous world's MODELS painted onto new pool slots) and diffs against its lights.
            _peerSentIdentity.Clear();
            _slotIdentity.Clear();
            _lightLastSent.Clear();
            _lightsFullSentAt = -999f;

            // Ghost anchor (#7) — clear last-outside memory so a new game/save
            // doesn't keep spawning traffic at the previous session's location.
            _hasOutsidePos = false;
            if (_ghostAnchorGO != null) _ghostAnchorGO.transform.position = Vector3.zero;
            // Review #2 MINOR-4: the game's density request and the budget log memory are per world.
            GameDensityRequest = -1; _lastBudgetLogged = -1; _lastAreasLogged = -1;
            ClientGameDensityRequest = -1; SelfDensityCall = false; _pendingHandBack = false; _handBackWarned = false; _anchorIsGhost = false;
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
                        tb = MPPerf.Begin(); var master = BuildMaster(); MPPerf.End("Tr.Build", tb);
                        tb = MPPerf.Begin(); BroadcastPerPeer(master); MPPerf.End("Tr.Send", tb);
                    }

                    _lightBroadcastTimer -= Time.unscaledDeltaTime;
                    if (_lightBroadcastTimer <= 0f)
                    {
                        _lightBroadcastTimer = 0.5f;     // lights change slowly
                        tb = MPPerf.Begin();
                        var lights = BuildLightSnapshot();
                        if (lights != null)
                        {
                            // Wave-2: only intersections whose state MOVED ride the 0.5 s beat; a full
                            // re-assert every 10 s covers drops and late joiners (apply is per-index,
                            // so a partial list is naturally safe on the receiver).
                            bool full = Time.unscaledTime - _lightsFullSentAt >= LightsFullReassertSeconds;
                            var send = lights;
                            if (!full)
                            {
                                var diff = new TrafficLightsPayload();
                                foreach (var s in lights.Lights)
                                    if (!_lightLastSent.TryGetValue(s.Index, out var prev) || prev.road != s.Road || prev.yellow != s.Yellow)
                                        diff.Lights.Add(s);
                                send = diff;
                            }
                            else _lightsFullSentAt = Time.unscaledTime;
                            foreach (var s in lights.Lights) _lightLastSent[s.Index] = (s.Road, s.Yellow);
                            if (send.Lights.Count > 0) MPServer.BroadcastTrafficLights(send);
                        }
                        MPPerf.End("Tr.Light", tb);
                    }
                }
                else if (MPClient.IsConnected)
                {
                    // Client sim at zero density (field 2026-09-02): Gley recycles a car once it is far from EVERY
                    // anchor it knows (DriveJob.RemoveVehicle: readyToRemove = no camera within distanceToRemove);
                    // a client's traffic brain only knew the client's own camera, so the departing origin car was
                    // recycled the moment its owner teleported away — while the host stood next to it. Feed the same
                    // anchors the host feeds (local player, ride anchor, every remote avatar); the density budget line
                    // inside is host-only, the client's density stays 0 (SuppressLocalTraffic re-asserts it after).
                    if (ClientServiceSimEnabled && Time.timeSinceLevelLoad > 5f) UpdateTrafficAnchors();
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

        /// <summary>One live car, gathered once per beat; every per-peer snapshot filters THIS list.
        /// Identity is the cached Colors LIST REFERENCE: a recycle allocates a new entry (see
        /// _carColors), so reference inequality == "this peer has not seen this occupant".</summary>
        private sealed class MasterCar
        {
            public int Index; public string Model = ""; public List<float> Colors = new();
            public Vector3 Pos; public Quaternion Rot;
        }

        private static readonly List<MasterCar> _masterScratch = new();

        private static List<MasterCar> BuildMaster()
        {
            _masterScratch.Clear();
            try
            {
                var arr = GetVehiclePool();
                if (arr == null) return _masterScratch;
                for (int i = 0; i < arr.Length; i++)
                {
                    var vc = arr[i] as VehicleComponent;
                    if (vc == null) continue;
                    var go = vc.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    if (ServiceCars.IsLocalServiceCar(go)) continue;   // 2026-09-02: mirrored ONCE, as a service ghost — never also as traffic

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

                    _masterScratch.Add(new MasterCar { Index = index, Model = model, Colors = colors, Pos = pos, Rot = rot });
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] BuildMaster: {ex.Message}");
            }
            return _masterScratch;
        }

        // T2 per-peer state: PLAYER id → (pool slot → identity token = the Colors list ref last sent).
        // A slot leaving the peer's radius is FORGOTTEN, so re-entry re-sends identity — the client
        // culls its ghost at the same boundary and needs the model again to respawn it.
        // Review B1: keyed by PLAYER id, never link id — LiteNetLib RECYCLES peer ids, and a rejoining
        // client inheriting the old map would receive identity-less DTOs and spawn NO traffic at all
        // (the round-281 rule at MPServer.cs: per-peer state dies with the connection).
        private static readonly Dictionary<string, Dictionary<int, object>> _peerSentIdentity = new();

        /// <summary>Review B1/MIN-3: drop a departed (or teleported/reloaded) peer's traffic state, and
        /// force the next lights beat to a FULL re-assert so the newcomer starts from truth.</summary>
        public static void ForgetPeer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            _peerSentIdentity.Remove(playerId);
            _lightsFullSentAt = -999f;
        }

        private static void BroadcastPerPeer(List<MasterCar> master)
        {
            var peers = MPServer.ConnectedClientPeers();   // review MIN-9: named peers only (post-Hello)
            if (peers.Count == 0) return;
            float now = Time.unscaledTime;
            float r2 = HostSendRadius * HostSendRadius;
            var livePids = new HashSet<string>();
            foreach (var (link, pid) in peers)
            {
                if (link == null || string.IsNullOrEmpty(pid)) continue;
                livePids.Add(pid);
                bool havePos = RemotePlayerManager.TryGetRemotePosition(pid, out var anchor);
                // Review M3: a PASSENGER's avatar is parked at the boarding door — anchor on the ridden
                // car's ghost instead, else the rider crosses the city through empty streets. If the
                // ghost is not resolvable here, send UNCULLED rather than wrong.
                if (PassengerSync.TryGetRide(pid, out var rideVid))
                {
                    if (VehicleManager.TryGetGhostPosition(rideVid, out var ridePos)) { anchor = ridePos; havePos = true; }
                    else havePos = false;
                }
                if (!_peerSentIdentity.TryGetValue(pid, out var sent))
                    _peerSentIdentity[pid] = sent = new Dictionary<int, object>();
                var snap = new TrafficSnapshotPayload { T = now };
                foreach (var mc in master)
                {
                    // No known position (player still spawning) → uncullled full feed, identity-gated.
                    if (havePos && (mc.Pos - anchor).sqrMagnitude > r2) { sent.Remove(mc.Index); continue; }
                    bool needIdentity = !sent.TryGetValue(mc.Index, out var tok) || !ReferenceEquals(tok, mc.Colors);
                    var dto = new TrafficCarDto
                    {
                        Index = mc.Index,
                        X = Mathf.RoundToInt(mc.Pos.x * 100f), Y = Mathf.RoundToInt(mc.Pos.y * 100f), Z = Mathf.RoundToInt(mc.Pos.z * 100f),
                        Qx = Mathf.RoundToInt(mc.Rot.x * 10000f), Qy = Mathf.RoundToInt(mc.Rot.y * 10000f),
                        Qz = Mathf.RoundToInt(mc.Rot.z * 10000f), Qw = Mathf.RoundToInt(mc.Rot.w * 10000f),
                    };
                    if (needIdentity) { dto.Model = mc.Model; dto.Colors = mc.Colors; sent[mc.Index] = mc.Colors; }
                    snap.Cars.Add(dto);
                }
                MPServer.SendTrafficSnapshotTo(link, snap);
            }
            // Review B1: ALWAYS prune (the old "only when shrunk" gate skipped equal-count swaps, and
            // the empty-peers early-return above means the last disconnect is handled by ForgetPeer).
            if (_peerSentIdentity.Count != livePids.Count)
            {
                var stale = new List<string>();
                foreach (var k in _peerSentIdentity.Keys) if (!livePids.Contains(k)) stale.Add(k);
                foreach (var k in stale) _peerSentIdentity.Remove(k);
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
            if (!MPWorldReady.CanMaterialize) return; // round-188: 10 Hz stream — a drop is recurrence-covered
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
                    var pos = new Vector3(car.X * 0.01f, car.Y * 0.01f, car.Z * 0.01f);
                    var rot = new Quaternion(car.Qx * 0.0001f, car.Qy * 0.0001f, car.Qz * 0.0001f, car.Qw * 0.0001f);
                    if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f) rot = Quaternion.identity;

                    // T2: identity rides only when the HOST believes this peer needs it (first sight,
                    // recycle, radius re-entry). The slot cache bridges the packets in between.
                    if (car.Model != null) _slotIdentity[car.Index] = (car.Model, car.Colors);
                    else if (_slotIdentity.TryGetValue(car.Index, out var known)) { car.Model = known.model; car.Colors = known.colors; }

                    _ghosts.TryGetValue(car.Index, out var g);
                    if (car.Model == null)
                    {
                        // Identity not yet known here (should be rare: host resends on radius entry).
                        if (g == null || g.Go == null) continue;   // cannot spawn without a model
                        car.Model = g.Model;                       // same live car — keep going with what we have
                    }

                    if (haveMe)
                    {
                        float sq = (pos - me).sqrMagnitude;
                        bool isGhost = g != null && g.Go != null;
                        if (!isGhost && sq > GhostViewRadius * GhostViewRadius)
                            continue;                                  // out of view — don't spawn
                        if (isGhost && sq > GhostCullRadius * GhostCullRadius)
                        {
                            try { NotifyCollidersRemoved(g!.Go, g.Solids); UnityEngine.Object.Destroy(g!.Go); } catch { }
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
                        try { NotifyCollidersRemoved(g.Go, g.Solids); UnityEngine.Object.Destroy(g.Go); } catch { }
                        g = null;
                    }

                    if (g == null || g.Go == null)
                    {
                        var go = SpawnTrafficGhost(car.Model, pos, rot);
                        if (go == null) { _ghosts.Remove(car.Index); continue; }
                        g = new TrafficGhost { Go = go, Model = car.Model, TargetPos = pos, TargetRot = rot, TargetAt = Time.unscaledTime, HostT = snap.T };
                        g.Body = go.GetComponent<Rigidbody>();   // ROOT rb only (a child rb would teleport just that part)
                        try { var all = go.GetComponentsInChildren<Collider>(true); var sol = new List<Collider>(all.Length); foreach (var c in all) if (c != null && !c.isTrigger) sol.Add(c); g.Solids = sol.ToArray(); } catch { }
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
                        try { NotifyCollidersRemoved(_ghosts[k].Go, _ghosts[k].Solids); UnityEngine.Object.Destroy(_ghosts[k].Go); } catch { }
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

        /// <summary>Gives out a pooled traffic prefab by model name, for callers that need a
        /// REAL prefab reference a ghost does not carry. Returns null until the pool is built.
        /// Added 2026-08-29 for the 1.0 end-of-taxi-ride path (see TaxiRideEndFix).</summary>
        internal static GameObject? PooledPrefab(string model)
        {
            try
            {
                BuildPrefabMap();
                if (_trafficPrefabs == null || string.IsNullOrEmpty(model)) return null;
                if (_trafficPrefabs.TryGetValue(model, out var go)) return go;
                foreach (var kv in _trafficPrefabs)
                    if (string.Equals(kv.Key, model, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                return null;
            }
            catch { return null; }
        }

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

            // Round-188: while the Gley pool isn't up (TrafficComponent.Instance null during
            // load/fence) the map stays null — treating that as "use the fallback" routed EVERY
            // car of every 10 Hz packet into the unguarded ghost path (the 13,629-NRE storm).
            // Not-ready ≠ unknown-model: DEFER the car (the next snapshot retries in ~0.1s).
            if (_trafficPrefabs == null)
            {
                float now = UnityEngine.Time.unscaledTime;
                if (now >= _prefabWaitNextLog)
                {
                    _prefabWaitNextLog = now + 5f;
                    Plugin.Logger.LogInfo("[TrafficSync] ghost spawns deferred — traffic prefab pool not built yet. Will retry on the next snapshot.");
                }
                return null;
            }

            GameObject? prefab = null;
            _trafficPrefabs?.TryGetValue(model, out prefab);
            if (prefab == null)
                return VehicleManager.SpawnVisualGhost(model, pos, rot);   // fallback: UNKNOWN model with the pool up (e.g. Taxi)
            // A2 (2026-09-02): the clone routine is shared with the service-car look-alike (ServiceCars.TrySpawnLookalike).
            var body = CloneStrippedPrefab(prefab, model, pos, rot);
            if (body == null) return VehicleManager.SpawnVisualGhost(model, pos, rot);
            // Client sim at zero density (2026-09-02): a ghost's colliders move to the layer Gley brakes for
            // (ServiceColliderLayer — resolved from the traffic system's own LayerSetup; NOT PlayerVehicles, which
            // measured as neither sensed nor collidable, H-SVC-113). On the traffic layer a stripped ghost reaches the
            // unguarded VehicleComponent.cs:366 deref (the June NRE class); on playerLayers the car takes the safe
            // branch, brakes and waits, exactly as for a player's car. Same relayer the A2 look-alike uses.
            if (ClientServiceSimEnabled) RelayerCollidersToServiceLayer(body);
            return body;
        }

        // ── the layer Gley's sensors treat as "player" (H-SVC-113, field 2026-09-02) ─────────────────────
        // MEASURED on the rig: PlayerVehicles is NOT in LayerSetupData.playerLayers and never exchanges collision or
        // trigger events with AiVehicles — so bodies placed there were invisible to traffic (no braking, drive-through).
        // The layer traffic brakes for is project data; read it from Gley's own LayerSetup at first use: prefer
        // "Vehicles" when it is in playerLayers, else the lowest playerLayers bit that collides with AiVehicles.
        // -1 = nothing qualifies → callers leave prefab layers alone. Logged once with names.
        private static int _serviceLayer = -2;
        private static float _serviceLayerRetryAt;
        internal static int ServiceColliderLayer()
        {
            if (_serviceLayer != -2) return _serviceLayer;
            if (Time.unscaledTime < _serviceLayerRetryAt) return -1;   // review #2 MINOR-7: a failed load retries every 5 s, not per contact
            int chosen = -1;
            try
            {
                var ls = Resources.Load<LayerSetup>("LayerSetupData");
                if (ls == null) { _serviceLayerRetryAt = Time.unscaledTime + 5f; return -1; }   // not loadable yet
                int mask = (int)ls.playerLayers, ai = LayerHelper.AiVehiclesLayerIndex, veh = LayerHelper.VehiclesLayerIndex;
                bool Collides(int l) => ai < 0 || !Physics.GetIgnoreLayerCollision(ai, l);
                // Review MAJOR-1 (2026-09-02): the fallback is BOUNDED — never a character / UI / raycast-ignore
                // layer, never PlayerVehicles (measured: not sensed, no AiVehicles contact). Vehicles first; else the
                // lowest remaining playerLayers bit that collides with AiVehicles; else -1 and the service cars run
                // without sensing (logged as a warning; the look-alike then falls back to the player body).
                var excluded = new System.Collections.Generic.HashSet<int> { LayerHelper.PlayerLayerIndex, LayerHelper.HumanLayerIndex,
                    LayerHelper.PlayerVehiclesLayerIndex, LayerHelper.UiLayerIndex, LayerHelper.IgnoreRaycastLayerIndex, LayerHelper.DefaultLayerIndex };
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) names.Add($"{LayerMask.LayerToName(i)}({i}){(Collides(i) ? "" : "×noAi")}{(excluded.Contains(i) ? "×excluded" : "")}");
                if (veh >= 0 && (mask & (1 << veh)) != 0 && Collides(veh)) chosen = veh;
                else for (int i = 0; i < 32 && chosen < 0; i++) if ((mask & (1 << i)) != 0 && Collides(i) && !excluded.Contains(i)) chosen = i;
                _serviceLayer = chosen;
                int pv = LayerHelper.PlayerVehiclesLayerIndex;
                string hits = chosen >= 0
                    ? $"; chosen×Vehicles collide={(veh >= 0 && !Physics.GetIgnoreLayerCollision(chosen, veh))}, chosen×PlayerVehicles collide={(pv >= 0 && !Physics.GetIgnoreLayerCollision(chosen, pv))}"
                    : "";
                if (chosen >= 0)
                    Plugin.Logger.LogInfo($"[TrafficSync] service collider layer: {LayerMask.LayerToName(chosen)}({chosen}) — LayerSetup.playerLayers = [{string.Join(", ", names)}], AiVehicles={ai}, PlayerVehicles={pv}{hits}.");
                else
                    Plugin.Logger.LogWarning($"[TrafficSync] service collider layer: NONE qualifies — LayerSetup.playerLayers = [{string.Join(", ", names)}]; ghosts keep prefab layers, service cars drive WITHOUT sensing traffic, look-alikes fall back to the player body.");
            }
            catch (Exception ex) { _serviceLayer = -1; Plugin.Logger.LogWarning($"[TrafficSync] service collider layer: {ex.Message} — prefab layers kept."); }
            return _serviceLayer;
        }

        private static int _relayerLogged;
        internal static int RelayerCollidersToServiceLayer(GameObject go)
        {
            try
            {
                int layer = ServiceColliderLayer();
                if (layer < 0) return 0;
                go.layer = layer;
                int n = 0;
                foreach (var c in go.GetComponentsInChildren<Collider>(true))
                    if (c != null && c.gameObject.layer != layer) { c.gameObject.layer = layer; n++; }
                if (_relayerLogged++ < 2) Plugin.Logger.LogInfo($"[TrafficSync] traffic ghost '{go.name}': {n} collider object(s) moved to layer '{LayerMask.LayerToName(layer)}' (client sim: the branch Gley brakes for).");
                return n;
            }
            catch (Exception ex) { if (_relayerLogged++ < 2) Plugin.Logger.LogWarning($"[TrafficSync] ghost relayer: {ex.Message}"); return 0; }
        }

        /// <summary>Clones a Gley traffic prefab into a pure visual prop: instantiated INACTIVE, Gley AI / audio /
        /// LOD components stripped, cameras stripped, every rigidbody kinematic, then activated. Null when Unity's
        /// Instantiate itself fails (the caller chooses the fallback). Shared by the traffic ghosts and, since A2
        /// (2026-09-02), the service-car look-alike mirror (ServiceCars.TrySpawnLookalike).</summary>
        internal static GameObject? CloneStrippedPrefab(GameObject prefab, string model, Vector3 pos, Quaternion rot)
        {
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
                return null;    // the caller picks the fallback
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
                var audios = new System.Collections.Generic.List<Component>();
                var others = new System.Collections.Generic.List<Component>();
                // Field 2026-09-04 (host black screen on building enter/exit): AiCarMusic and its AudioSource
                // sit on a CHILD object, 'AiCarMusicPlayer', on every AI-drivable prefab. A root-only
                // GetComponents never saw them, so the clone kept a radio whose Start() subscribes to
                // GlobalEvents.onEnterBuilding/onExitBuilding and never unsubscribes; destroying the ghost
                // later left a dead subscriber that threw inside BuildingManager's enter/exit coroutine.
                // Walk the whole hierarchy so the kill list applies to children too.
                var comps = go.GetComponentsInChildren(typeof(Component), true);
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
                    if (cn == "AudioSource") { if (!isTaxi) audios.Add(c); continue; }
                    others.Add(c);
                }
                foreach (var c in others) if (c != null) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in audios) if (c != null) UnityEngine.Object.DestroyImmediate(c);
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
            try { go.AddComponent<ModGhostMarker>(); } catch { }   // review MAJOR-1(b): native layer-keyed triggers ignore mod ghosts
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
                if (g.Go != null) { try { NotifyCollidersRemoved(g.Go, g.Solids); UnityEngine.Object.Destroy(g.Go); } catch { } }
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

        // ── Round-199 anchor validity guard (field 20260730-213942, v0.1.15 host) ──
        // Vanilla feeds ONLY the local camera into the traffic grid, so Gley's cell
        // math never sees a position it can't handle.  We feed every player, and the
        // math (read from GetCellIndex IL) is
        //     row = FloorToInt(Abs((gridCorner.z - pos.z) / gridCellSize))
        // with NO bounds check: a position beyond the far grid edge (or NaN, which
        // floors to int.MinValue) overflows the cell array and TrafficManager.Update
        // throws EVERY FRAME — 3,679+ IndexOutOfRange filled a 20MB field log.
        // (Positions slightly outside the NEAR corner are silently Abs-folded back
        // into the grid by vanilla math — those don't crash and are left alone.)
        // The guard replays the exact same math against the live grid dimensions and
        // skips anchors that would overflow, naming the anchor and position so the
        // next field log identifies WHERE the off-grid player actually was.
        private static GleyUrbanAssets.CurrentSceneData? _gridScene;
        private static readonly HashSet<string> _badAnchorLogged = new();
        private static int _badAnchorSkips;

        private static bool AnchorFeedable(Transform t)
        {
            if (t == null) return false;
            Vector3 p = t.position;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
                || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                return LogBadAnchor(t, p, "non-finite position");
            try
            {
                if (_gridScene == null) _gridScene = GleyUrbanAssets.CurrentSceneData.GetSceneInstance();
                var sd = _gridScene;
                var grid = sd != null ? sd.grid : null;
                if (grid == null || grid.Length == 0 || sd!.gridCellSize <= 0)
                    return true;   // no grid to judge against → vanilla behavior (feed)
                int r = Mathf.FloorToInt(Mathf.Abs((sd.gridCorner.z - p.z) / sd.gridCellSize));
                int c = Mathf.FloorToInt(Mathf.Abs((sd.gridCorner.x - p.x) / sd.gridCellSize));
                if (r >= grid.Length || grid[r].row == null || c >= grid[r].row.Length)
                    return LogBadAnchor(t, p, $"off-grid cell [{r},{c}] vs {grid.Length} rows");
            }
            catch { }   // the guard must never break the feed itself
            return true;
        }

        private static bool LogBadAnchor(Transform t, Vector3 p, string why)
        {
            _badAnchorSkips++;
            string id = t != null ? t.name : "<null>";
            if (_badAnchorLogged.Add(id) || _badAnchorSkips % 600 == 0)
                Plugin.Logger.LogWarning(
                    $"[TrafficSync] anchor '{id}' NOT fed to the traffic grid: {why} at "
                    + $"({p.x:F1}, {p.y:F1}, {p.z:F1}) — would IndexOutOfRange TrafficManager.Update "
                    + $"every frame (round-199, skip #{_badAnchorSkips}).");
            return false;
        }

        /// <summary>The game's own despawn radius — two anchors closer than this share the cars around them, so
        /// they count as ONE area for the budget. Falls back to 150 m when the traffic component is not readable.</summary>
        private static float AreaRadius()
        {
            try { var tc = TrafficComponent.Instance; if (tc != null && tc.distanceToRemove > 1f) return Mathf.Clamp(tc.distanceToRemove, 80f, 400f); } catch { }
            return AreaRadiusFallback;
        }
        /// <summary>Greedy clustering of the fed anchors: an anchor within AreaRadius of an existing area joins it.</summary>
        private static int CountPlayerAreas(Transform[] anchors)
        {
            if (anchors == null || anchors.Length == 0) return 0;
            float r2 = AreaRadius(); r2 *= r2;
            var centres = new List<Vector3>(anchors.Length);
            foreach (var a in anchors)
            {
                if (a == null) continue;
                var p = a.position; bool joined = false;
                for (int i = 0; i < centres.Count && !joined; i++) if ((centres[i] - p).sqrMagnitude <= r2) joined = true;
                if (!joined) centres.Add(p);
            }
            return centres.Count;
        }

        // H-SVC-116 round 3: the reconnect-window belt and the offline hand-back retry. They cannot live in Tick():
        // MPCanvasUI.TickPositionSync returns BEFORE TrafficSync.Tick() whenever the machine is neither hosting nor
        // connected — exactly the window they exist for — so TickPositionSync calls THIS from its early-return block.
        private static bool _pendingHandBack;
        private static bool _handBackWarned;
        private static bool _anchorIsGhost;   // review r4 #2: the hand-back had to feed the parked ghost anchor — promote to the live character as soon as it is feedable

        /// <summary>Per frame while NOT hosting and NOT connected (MPCanvasUI.TickPositionSync early-return block).
        /// Reconnect window (InMpGame still true): keep Gley's camera list pointed at live transforms. Offline fork
        /// (InMpGame false) with a hand-back still pending: retry until a feed lands (design principle 4 — retries are
        /// recurrence-covered). Nothing else; the connected tick owns everything while a link is up.</summary>
        internal static void TickDisconnected()
        {
            try
            {
                if (MPServer.IsRunning || MPClient.IsConnected) return;
                if (Time.timeSinceLevelLoad <= 5f) return;
                if (!TrafficManager.HasInstance || !TrafficManager.IsInitialized) return;
                if (MPClient.InMpGame)
                {
                    if (ClientServiceSimEnabled) UpdateTrafficAnchors();      // the belt (was unreachable inside Tick)
                    return;
                }
                if (_pendingHandBack) { HandBackToVanilla("offline fork retry"); return; }   // clears _pendingHandBack once a feed lands
                if (_anchorIsGhost)
                {
                    // Review r4 #2: the offline fork was handed back on the parked ghost anchor; nothing else would ever move it.
                    // Promote to the live character the first frame it is feedable (vanilla parity: one camera that follows the player).
                    var live = PlayerHelper.PlayerController?.Character?.transform;
                    if (live != null && AnchorFeedable(live))
                    {
                        try { TrafficManager.Instance.UpdateCamera(new[] { live }); _anchorIsGhost = false; Plugin.Logger.LogInfo("[TrafficSync] offline fork: anchor promoted from the parked ghost to the live player."); }
                        catch (Exception e) { Plugin.Logger.LogWarning($"[TrafficSync] ghost → live promotion: {e.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] TickDisconnected: {ex.Message}"); }
        }

        /// <summary>H-SVC-116 (bundle 20260902-210235): the client's anchor feed lives in Tick's IsConnected branch, so it
        /// stops the instant the host is lost — while the same disconnect handler destroys every remote avatar. Gley
        /// (TrafficManager.activeCameras + PositionValidator.activeCameras) kept the destroyed transforms and, once the
        /// offline switch lifted the density clamp, threw two NullReferenceExceptions per frame. Called from the
        /// disconnect handler right after the avatars are removed: re-feed Gley from the live registry (now local-only).
        /// Cheap and idempotent — a repeated fire (drop + in-place reconnect) is a no-op. No-op without a live manager or
        /// a local character (menu / mid-load).</summary>
        internal static void RefeedAnchors(string why)
        {
            try
            {
                if (!TrafficManager.HasInstance || !TrafficManager.IsInitialized) return;   // review MINOR-2: never lazily create a manager
                if (PlayerHelper.PlayerController?.Character == null) { Plugin.Logger.LogInfo($"[TrafficSync] anchor re-feed skipped ({why}): no local character."); return; }
                if (UpdateTrafficAnchors()) Plugin.Logger.LogInfo($"[TrafficSync] anchors → local only ({why}).");
                else Plugin.Logger.LogWarning($"[TrafficSync] anchor re-feed not confirmed ({why}): no feedable local anchor and no parked outside position — Gley may still hold its previous camera list; TickDisconnected keeps trying while the world is loaded.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] RefeedAnchors({why}): {ex.Message}"); }
        }

        /// <summary>H-SVC-116: the player chose to continue OFFLINE after a host loss (MPClient.InMpGame is already false,
        /// so the zero-density clamp no longer applies). The offline copy is single player: give Gley the game's own last
        /// density request back and a local-only anchor list, in one step, so vanilla traffic resumes and no dead
        /// reference remains. Lights and sensors follow IsClientInWorld on their own patches.</summary>
        internal static void HandBackToVanilla(string why)
        {
            try
            {
                if (MPClient.IsConnected) return;                                   // review r2 #7: a live link means the clamp still rules
                if (!TrafficManager.HasInstance || !TrafficManager.IsInitialized) return;
                var tm = TrafficManager.Instance;
                bool insideNow = false; try { insideNow = BuildingManager.IsInsideBuilding; } catch { }
                if (!insideNow) tm.enabled = true;                                  // review r2 #5 / r4 #3: re-enable only outdoors — indoors (or fast-forward) the GAME pauses Gley itself once the link is gone (Patch_TM_SetPause blocks SetPause only while hosting/connected)
                // Review r2 #4: feed the LIVE character transform — vanilla parity (the game's single camera follows the
                // player indoors too) — never the pinned ghost anchor UpdateTrafficAnchors uses inside a building, because
                // nothing refreshes Gley's camera list in the offline fork (the game never calls UpdateCamera after start).
                bool fed = false;
                var t = PlayerHelper.PlayerController?.Character?.transform;
                if (t != null && AnchorFeedable(t))
                {
                    try { tm.UpdateCamera(new[] { t }); fed = true; }
                    catch (Exception e) { Plugin.Logger.LogWarning($"[TrafficSync] hand-back UpdateCamera: {e.Message}"); }
                }
                int density = ClientGameDensityRequest;
                // Review r2 #3: never raise the density while Gley may still hold the previous camera list.
                bool usedGhost = false;
                if (!fed && _hasOutsidePos)
                {
                    // Review r2 #5: the parked ghost anchor is live, mod-owned and on-grid — better than a dead list.
                    var ga = GetOrCreateGhostAnchor(); ga.position = _lastOutsidePos;
                    if (AnchorFeedable(ga)) { try { tm.UpdateCamera(new[] { ga }); fed = true; usedGhost = true; } catch { } }
                }
                if (!fed)
                {
                    // Design principle 4: a refused hand-back is retried every frame by TickDisconnected until a feed
                    // lands — the game re-requests density on its own once the clamp is lifted, so Gley must never be
                    // left holding a dead list. One warning per episode, not per frame.
                    _pendingHandBack = true;
                    if (!_handBackWarned) { _handBackWarned = true; Plugin.Logger.LogWarning($"[TrafficSync] traffic hand-back ({why}) not confirmed: no feedable local anchor yet — retrying every frame until one lands."); }
                    return;
                }
                _pendingHandBack = false; _handBackWarned = false; _anchorIsGhost = usedGhost;   // review r4 #2: TickDisconnected promotes a ghost anchor to the live player
                if (density >= 0)
                {
                    SelfDensityCall = true;                                         // review r2 #8: our own write, never recorded as the game's request
                    try { tm.SetTrafficDensity(density); } catch { } finally { SelfDensityCall = false; }
                }
                Plugin.Logger.LogInfo($"[TrafficSync] traffic handed back to vanilla ({why}): density {(density >= 0 ? density.ToString() : "unchanged — no game request recorded")}, anchor → local player.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] HandBackToVanilla({why}): {ex.Message}"); }
        }

        private static bool UpdateTrafficAnchors()
        {
            try
            {
                if (!TrafficManager.HasInstance || !TrafficManager.IsInitialized) return false;   // review r2 #1: never lazily create a manager; Gley's UpdateCamera no-ops when !initialized
                var tm = TrafficManager.Instance;
                if (tm == null) return false;
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
                if (anchors.Count == 0) return false;

                // Round-199: single choke point — every anchor (local, ghost, ride,
                // remote) is validated against the live grid before Gley sees it.
                var feed = new List<Transform>(anchors.Count);
                foreach (var a in anchors)
                    if (AnchorFeedable(a)) feed.Add(a);
                if (feed.Count == 0)
                {
                    // Review r2 #5: a refused feed must never leave Gley holding a DEAD list — fall back to the ghost anchor
                    // parked at the last known outside position (live, mod-owned, on-grid) when we have one.
                    if (_hasOutsidePos && !MPServer.IsRunning && !MPClient.IsConnected) { var ga = GetOrCreateGhostAnchor(); ga.position = _lastOutsidePos; if (AnchorFeedable(ga)) feed.Add(ga); }   // review r3 #5: disconnected only — a host's or a connected client's feed keeps its previous behaviour
                    if (feed.Count == 0) return false;
                }
                var arr = feed.ToArray();

                // Feed every player to both anchor APIs — UpdateCamera drives the
                // active-grid squares (where traffic spawns), UpdateCameraPositions
                // the density manager.
                bool fed = false;
                try { tm.UpdateCamera(arr); fed = true; }
                catch (Exception e)
                { if (!_anchorDiagLogged) Plugin.Logger.LogWarning($"[TrafficSync] UpdateCamera: {e.Message}"); }
                if (dm != null)
                {
                    try { dm.UpdateCameraPositions(arr); }   // review r3 #4: this leg refreshes only PositionValidator's list — TrafficManager.activeCameras (the array Update reads) is replaced by the UpdateCamera leg alone, so only that leg sets `fed`
                    catch (Exception e)
                    { if (!_anchorDiagLogged) Plugin.Logger.LogWarning($"[TrafficSync] UpdateCameraPositions: {e.Message}"); }
                    // HOST only (a client's ambient density is pinned to 0): budget = the game's own request ×
                    // distinct player areas — see the VANILLA TRAFFIC note at the top. Before the game's first
                    // request the Initialize value stands (clamped to the authored pool by ServiceCars).
                    if (MPServer.IsRunning && GameDensityRequest >= 0)
                    {
                        int areas = CountPlayerAreas(arr);
                        int budget = GameDensityRequest * Math.Max(1, areas);
                        // Review #2 MAJOR-2: never ask for more cars than the pool holds minus a reserve for service
                        // cars (a summon + an arrival car per player) — otherwise ambient traffic spends the very slots
                        // the pool bump reserves and a summon would yank an active car (Gley's by-name fallback).
                        int poolCount = 0; try { poolCount = tm.trafficVehicles?.GetVehicleList()?.Count ?? 0; } catch { }
                        int reserve = 2 * arr.Length;
                        int cap = poolCount > reserve ? poolCount - reserve : poolCount;
                        bool capped = poolCount > 0 && budget > cap;
                        if (capped) budget = cap;
                        try { dm.UpdateMaxCars(budget); } catch { }
                        if (budget != _lastBudgetLogged || areas != _lastAreasLogged)
                        {
                            _lastBudgetLogged = budget; _lastAreasLogged = areas;
                            Plugin.Logger.LogInfo($"[TrafficSync] traffic budget: the game asks {GameDensityRequest}; {areas} player area(s) (radius {AreaRadius():F0} m) → maxCars {budget}{(capped ? $" (capped by the pool: {poolCount} slots − {reserve} reserved)" : "")}.");
                        }
                    }
                }

                if (!_anchorDiagLogged)
                {
                    _anchorDiagLogged = true;
                    // The NATIVE spawn/despawn radii are scene-serialized (not in code) — printed once so
                    // the mod's send/view rings (160/130, hand-picked 2026-06-10) can be set from DATA:
                    // native despawn distance = how far the game itself keeps a car alive around a player.
                    string nativeR = "?";
                    try { var tc = TrafficComponent.Instance; if (tc != null) nativeR = $"spawn={tc.minDistanceToAdd:F0}m despawn={tc.distanceToRemove:F0}m"; } catch { }
                    Plugin.Logger.LogInfo(
                        $"[TrafficSync] Traffic anchors active: {arr.Length} fed of {anchors.Count} player(s); " +
                        $"densityManager={(dm != null ? "ok" : "NULL")}; " +
                        $"budget=game request × player areas (request so far {GameDensityRequest}); nativeRadii: {nativeR}.");
                }
                return fed;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] UpdateTrafficAnchors: {ex.Message}");
                return false;
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

                // GHOST-ONLY (2026-08-29, field-proven). This used to call ResolveTaxiIndex, which
                // falls back to the object's own VehicleComponent.GetIndex() when it is not a ghost.
                // That fallback is the HOST path - and this method has already returned on the host
                // two lines up, so on the only machine that reaches here it is always WRONG: it
                // yields an index into the CLIENT's local Gley pool, which names an unrelated slot
                // on the host.
                //
                // Field evidence (2026-08-29): a client hailed a 1.0 private driver - a car it had
                // summoned LOCALLY via TrafficManager.LoadVehicle, so not a ghost - and the host
                // logged "HostStopTaxi: no taxi with index 18." twice. Harmless only by luck: had a
                // real taxi occupied host slot 18, the host would have force-stopped a stranger's
                // cab for 18 seconds.
                //
                // A pool index is meaningful across machines ONLY for a ghost, because ghosts are
                // keyed by the host's index. Anything else is a locally-spawned vehicle the host
                // does not know about, and there is nothing for the host to stop. So: resolve from
                // _ghosts alone, and say so when we decline.
                int index = -1;
                foreach (var kv in _ghosts)
                    if (kv.Value.Go == taxiGo) { index = kv.Key; break; }

                if (index < 0)
                {
                    if (_nonGhostHailLogs++ < 6)
                        Plugin.Logger.LogInfo(
                            $"[TrafficSync] hail on '{taxiGo.name}' is NOT a host-mirrored ghost - "
                          + "not sending. It is a locally-spawned vehicle (e.g. a 1.0 private driver), "
                          + "so the host has nothing to stop and its pool index means nothing there.");
                    return;
                }
                MPClient.SendTaxiHail(index);
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
        // ResolveTaxiIndex DELETED 2026-08-29. It resolved a hail to a pool index by trying the
        // ghost table first and then falling back to the object's OWN VehicleComponent.GetIndex().
        // Its single caller (OnLocalTaxiHailed) returns on the host before reaching it, so that
        // fallback could only ever run on a CLIENT - where a self-reported index names a slot in the
        // client's own pool and means nothing to the host. Field-proven wrong on 2026-08-29
        // ("HostStopTaxi: no taxi with index 18" x2 after a client hailed a locally-summoned private
        // driver). OnLocalTaxiHailed now resolves from _ghosts inline. Deleted rather than left
        // unused: a helper that looks like the obvious way to answer "which vehicle is this?" is a
        // trap sitting in the file, and the next reader would reach for it.

        /// <summary>Finds a live traffic taxi's TaxiController by Gley pool index.
        /// HOST-SIDE ONLY. Note this returns null for anything that is not a TaxiController -
        /// a 1.0 PrivateDriverVehicle is a SIBLING of TaxiController (both EntityController+ITaxi,
        /// neither derives from the other), so a private driver never resolves here. That is
        /// correct: private drivers are summoned locally and are not host-mirrored.</summary>
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

            if (ClientServiceSimEnabled)
            {
                // Client sim at zero ambient density (2026-09-02): the traffic brain stays ON so the client's own
                // service cars drive natively. Ambient traffic never spawns: every density request is clamped to 0
                // (MPPatches.Patch_TM_SetTrafficDensity_ClientZero) and re-asserted here (a trivial assignment); the
                // per-tick clear below removes whatever spawned in the pre-zero window and is a no-op afterwards.
                // Lights: Patch_IM_UpdateIntersections_ClientSkip. Sensors: NREShield runs them for service cars only.
                if (!tm.enabled)
                {
                    tm.enabled = true;
                    Plugin.Logger.LogInfo("[TrafficSync] client traffic brain ON at zero ambient density — the client's own service cars drive natively; ambient traffic stays the host's.");
                }
                // MINOR-7 (review 2026-09-02): the density re-assert and the ambient sweep run on a 1 s beat
                // (first pass immediately), not every frame — the density clamp patch is the event-driven source.
                float nowSim = Time.unscaledTime;
                if (nowSim >= _nextClientSimBeat)
                {
                    _nextClientSimBeat = nowSim + 1f;
                    SelfDensityCall = true; try { tm.SetTrafficDensity(0); } catch { } finally { SelfDensityCall = false; }   // H-SVC-116: our own 0 is not the game's request
                    try { ClearClientTrafficExceptServiceCars(tm); } catch { }
                }
                return;
            }

            // Kill the client's local Gley traffic — the client renders host-synced ghosts instead.
            // ClearTraffic is host-scoped again (Patch_TM_ClearTraffic no longer no-ops the client), so
            // it actually runs here now (the previous direct-deactivation workaround is gone). Re-assert
            // whenever the manager is live OR any car is still active: the census (2026-06-16) showed the
            // game can re-enable the manager mid-session and a one-shot disable stranded ~20 cars. Cheap
            // pool scan, early-out once clean. Ghosts unaffected (cloned from the cached prefab map).
            // Client service cars (user-approved 2026-09-02): the mod's own private-driver / arrival cars
            // (ServiceCars registry, or any car carrying the game's PrivateDriverVehicle) are NOT ambient
            // traffic — they stay; ServiceCars retires them by distance because the dead sim never recycles
            // them. The clear below is Gley ClearTraffic's own rule (active + no preset path) minus those cars.
            bool anyActive = false;
            try
            {
                var list = tm.trafficVehicles?.GetVehicleList();
                if (list != null)
                    for (int i = 0; i < list.Count && !anyActive; i++)
                        if (list[i] is Component c && c.gameObject.activeSelf && !ServiceCars.IsClientKept(c.gameObject)) anyActive = true;
            }
            catch { }

            if (tm.enabled || anyActive)
            {
                try { ClearClientTrafficExceptServiceCars(tm); } catch { }
                tm.enabled = false;                  // stops Update/FixedUpdate → no sim, no spawn
                if (!_clientTrafficKilled)
                {
                    _clientTrafficKilled = true;
                    Plugin.Logger.LogInfo("[TrafficSync] Local traffic killed (ClearTraffic + manager disabled).");
                }
            }
        }

        private static int _clientKeptLogged = -1;
        /// <summary>Gley ClearTraffic's own rule (every ACTIVE pool car without a preset path), minus the mod's
        /// service cars — a client-summoned private driver, its destination car, a friend's arrival car
        /// (ServiceCars.IsClientKept). Same removal call Gley uses (RemoveVehicle → DisableVehicle).</summary>
        private static void ClearClientTrafficExceptServiceCars(TrafficManager tm)
        {
            var list = tm.trafficVehicles?.GetVehicleList();
            if (list == null) return;
            int kept = 0, removed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (v == null || !v.gameObject.activeSelf) continue;
                if (v.presetPath != null) continue;                       // native ClearTraffic spares a routed car
                if (ServiceCars.IsClientKept(v.gameObject)) { kept++; continue; }
                try { tm.RemoveVehicle(v.gameObject); removed++; } catch { }
            }
            if (kept != _clientKeptLogged)
            {
                _clientKeptLogged = kept;
                Plugin.Logger.LogInfo($"[TrafficSync] client traffic clear: {removed} ambient car(s) removed, {kept} service car(s) kept parked (the client's traffic brain is off — ServiceCars retires them by distance).");
            }
        }

        // CLAUDE-DIAGNOSTIC — F11 toggle for the entry-bug investigation.
        // Default ON.  Flipping OFF stops SuppressLocalTraffic from running
        // and re-enables TrafficManager so we can test whether disabling it
        // is what prevents BuildingManager.DelayedEnterBuildingActions from
        // firing on the client.
        public static bool ClientTrafficSuppressionEnabled { get; set; } = true;

        /// <summary>Client sim at ZERO ambient density (user-approved 2026-09-02; design: .modding/03-systems/
        /// private-driver-mp.md "Client sim at zero ambient density"). True = Gley keeps running on a pure client so
        /// the client's OWN service cars (private driver, arrival cars) drive natively — approach, stop, lights,
        /// braking for host traffic ghosts, drive-off, recycle — while ambient traffic can never spawn (density
        /// clamped to 0), local light phases never advance, sensors run only for service cars, and traffic ghosts
        /// sit on the layer Gley brakes for (ServiceColliderLayer — "Vehicles" on the tested data; NOT PlayerVehicles,
        /// H-SVC-113). False = the previous dead-sim behaviour (parked service cars).</summary>
        public static bool ClientServiceSimEnabled { get; set; } = true;
        /// <summary>H-SVC-116: the game's own last density request seen on a CLIENT (the number the clamp prefix zeroed),
        /// -1 = none yet. Restored when the player continues offline after a host loss (HandBackToVanilla).</summary>
        internal static int  ClientGameDensityRequest = -1;
        /// <summary>H-SVC-116: true only around the mod's OWN SetTrafficDensity(0) re-assert, so the clamp prefix never
        /// records that 0 as the game's request.</summary>
        internal static bool SelfDensityCall;
        private static float _nextClientSimBeat;

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
