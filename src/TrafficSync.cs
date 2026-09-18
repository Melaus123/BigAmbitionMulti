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
            // Velocity of the NEWEST state: the host's own rb.velocity when the packet carries one (S3),
            // else derived from two packet positions as before (an older host).  Used to interpolate
            // between the two buffered states and to dead-reckon when the buffer runs dry.
            public Vector3       Velocity;
            public float         TargetAt;            // CLIENT unscaled time TargetPos arrived
            public float         HostT;               // HOST sample time of TargetPos (packet stamp)
            // S4 (2026-09-12): the PREVIOUS received state. Rendering runs PlaybackDelay behind the newest
            // stamp, so the normal case is an interpolation between these two known poses — no chasing of
            // an extrapolated point, which is what made ghosts jerk when a 5 Hz packet landed late.
            public Vector3       PrevPos;
            public Quaternion    PrevRot = Quaternion.identity;
            public Vector3       PrevVel;
            public float         PrevHostT;
            public bool          HasPrev;
            public bool          HasVel;
            public bool          HasPrevVel;
            /// <summary>T3 P3: the newest packet said "stopped or braking" (TrafficCarDto.St). Such a car is
            /// never extrapolated, never given a Hermite tangent, and its rewind rate limit is lifted.</summary>
            public bool          Stopped;
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

        // ── S4: buffered interpolation (2026-09-12) ───────────────────────────
        /// <summary>How far BEHIND the newest host stamp ghosts are rendered. Fold d (re-check of fold c):
        /// the buffer holds exactly TWO states (previous, newest), so the delay must be AT MOST the 0.2 s
        /// (5 Hz) broadcast interval - at exactly the interval, render time sits on the previous stamp when a
        /// packet lands and reaches the newest stamp when the next one does, so u runs 0..1 over the whole
        /// cycle. A LONGER delay (fold c's 0.25) put render time BEFORE the previous stamp for the first
        /// 50 ms of every cycle: u clamped to 0 and the target jumped forward a quarter span on every
        /// arrival (a 5 Hz ripple). Arrival jitter around this value costs only a few ms of either a held
        /// pose or a smooth dead-reckon. A three-state buffer would allow a longer delay; not needed.</summary>
        private const float PlaybackDelay   = 0.20f;
        /// <summary>Chase rate for the host→client clock offset estimate (per applied snapshot).</summary>
        private const float ClockOffsetLerp = 0.05f;
        // ── TRAFFIC-CONSIST T3 (2026-09-18, design Q4 P1-P5): prediction that can never CLOSE a gap ──────────
        // I3 was dead reckoning up to 0.3 s plus an absolute ban on rewinding: a follower kept every overlap it
        // ever gained. Four numbers replace that, and this is why they are these numbers.
        //   PredictSpeedFloor 1.5 m/s - below a walking pace a car's own velocity vector is mostly noise, so
        //     extrapolating it invents motion the car never made, and that invented metre is exactly the one that
        //     lands inside the car in front. Below the floor nothing is predicted at all (P1).
        //   RewindRate 3 m/s - a correction backwards is the ghost GIVING BACK ground it should not have taken,
        //     so it has to read as motion, not as a snap. 0.2.3 jerked because it applied the whole correction in
        //     one frame; the rate limit is the entire difference (P4).
        //   RewindFastRate 8 m/s past RewindBacklogMetres 1.5 m - once the ghost is more than about a car's nose
        //     ahead of the truth, correct spacing matters more than smoothness: a 2 m backlog then closes in
        //     ~0.25 s instead of ~0.7 s (P4).
        // StoppedSpeed 0.5 m/s is the design's own "this car is not moving" test for the St flag (P3, F15).
        private const float PredictSpeedFloor   = 1.5f;
        private const float RewindRate          = 3f;
        private const float RewindFastRate      = 8f;
        private const float RewindBacklogMetres = 1.5f;
        private const float StoppedSpeed        = 0.5f;

        // T4 census counters for P1-P5's own evidence. Incremented in TickGhosts (no logging there), printed and
        // reset by the 30 s census line, so they always describe ONE window.
        private static int   _deadReckonFrames, _rewindFrames;
        private static float _maxDeadReckonMetres, _maxRewindMetres;
        private static float _clockOffset;          // clientUnscaledTime - hostT, smoothed
        private static bool  _haveClockOffset;

        // S1 client side: the stale/out-of-order guard for the unreliable lane.
        private static readonly object _seqLock = new();
        private static long _seqLast;
        private static long _seqDropped;
        private static bool _seqDropLogged;
        private static bool _sawOutOfOrder;

#if BAMP_DEV
        // S4 jitter (REDEFINED fold c): the CORRECTION an arriving packet implies at the CURRENT render
        // time — i.e. the jerk the player actually sees — for ghosts within 60 m, rolling 10 s window.
        private const float JitterWindowSeconds = 10f;
        private const float JitterNearSq        = 3600f;   // 60 m
        private static readonly List<(float t, float err)> _jitter = new();
#endif

        /// <summary>S1: called on the NETWORK thread as each snapshot arrives (MPClient.HandleTrafficSnapshot)
        /// — before the coalescing enqueue, so a stale packet can never win newest-wins. Seq 0 = an older host
        /// that stamps none: nothing to guard, always accept.</summary>
        internal static bool AcceptSnapshotSeq(long seq)
        {
            if (seq <= 0) return true;
            lock (_seqLock)
            {
                if (seq <= _seqLast)
                {
                    // A far-lower seq is a NEW host stream (rejoin / host restart), not a stale packet.
                    if (_seqLast - seq > 100) { _seqLast = seq; return true; }
                    _seqDropped++;
                    _sawOutOfOrder = true;
                    if (!_seqDropLogged)
                    {
                        _seqDropLogged = true;
                        Plugin.Logger.LogInfo($"[TrafficSync] dropped a stale traffic snapshot (seq {seq} <= {_seqLast}). " +
                                              "Further drops are counted only (TestDrive 'traffic').");
                    }
                    return false;
                }
                _seqLast = seq;
                return true;
            }
        }

        /// <summary>S5 lever data: last seq (sent on the host, accepted on the client), stale drops, lane.</summary>
        public static long LastTrafficSeq        => MPServer.IsRunning ? _trafficSeq : _seqLast;
        public static long StaleSnapshotsDropped => _seqDropped;
        /// <summary>The host knows what its transport did; a client can only say "unreliable" once it has
        /// actually seen an out-of-order packet (a reliable-ordered lane never delivers one).</summary>
        public static string TrafficLane => MPServer.IsRunning
            ? (_laneUnreliable ? "unreliable" : "reliable")
            : (_sawOutOfOrder ? "unreliable" : "unknown");

        /// <summary>S5: rolling render-vs-truth error. False = this build keeps no window (release).</summary>
        public static bool GhostJitterStats(out int samples, out float mean, out float max)
        {
            samples = 0; mean = 0f; max = 0f;
#if BAMP_DEV
            float now = Time.unscaledTime;
            _jitter.RemoveAll(s => now - s.t > JitterWindowSeconds);
            foreach (var s in _jitter) { samples++; mean += s.err; if (s.err > max) max = s.err; }
            if (samples > 0) mean /= samples;
            return true;
#else
            return false;
#endif
        }

        /// <summary>S5: clears the jitter window.</summary>
        public static void GhostJitterReset()
        {
#if BAMP_DEV
            _jitter.Clear();
#endif
        }

        /// <summary>Cubic Hermite between two states with their velocities (tangents already scaled by the
        /// span), so a turning car follows its arc instead of cutting the corner a straight lerp would.</summary>
        private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p0
                 + (u3 - 2f * u2 + u) * m0
                 + (-2f * u3 + 3f * u2) * p1
                 + (u3 - u2) * m1;
        }

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
            _peerTraffic.Clear(); _peerTrafficPin.Clear();   // TRAFFIC-APART P7: the per-peer traffic mode is per world too
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
            // TRAFFIC-APART P7: a new world starts in GHOST mode with no handover running, whatever the last one ended in.
            ClientTrafficMode = ModeGhost; _handover = HandoverNone; _modeSeq = 0;
            _modeDeferLogged = false; _localDensityIssued = false; _localDensityWaitLogged = false; _localDensityUninitLogged = false;
            // TRAFFIC-GRID-1 fold b: the once-per-key warning memory and the skip counter are per world. This is the
            // reset that runs on EVERY disconnect (game load / scene change); HandBackToVanilla only runs on the
            // offline fork, so the counters would otherwise carry a previous world's numbers into the next log.
            _badDensityCameraLogged.Clear(); _offGridDensitySkips = 0;
            // TRAFFIC-CONSIST T1/T3: the published-leftover state, the stand-ins (their objects die with the scene)
            // and the clock estimate are all per world. _haveClockOffset matters here: the HOST stamps its stand-ins
            // with its own clock (offset 0), and that must never be carried into a session where this machine is a
            // client reading a real host's stamps.
            foreach (var f in _foreign.Values) { f.Rows.Clear(); f.StandIns.Clear(); }
            _foreign.Clear(); _foreignNextOrdinal = 0; _foreignDropLogs = 0;
            _publishTimer = 0f; _publishSeq = 0; _publishedLast = 0; _publishLogged = false;
            _publishedIds.Clear(); _sensorSkips = 0;
            _haveClockOffset = false; _clockOffset = 0f;
            _senseProxyPending.Clear(); _senseProxyDeferLogged = false;
            _nextCensusAt = 0f; _gleyTriggerHits = 0;
            _deadReckonFrames = 0; _rewindFrames = 0; _maxDeadReckonMetres = 0f; _maxRewindMetres = 0f;
            // H-TAXISTRAND-1: a scene reset during a ride would otherwise leave the world-clock pinner suppressed
            // for good (the ride's end event never arrives for the world that went away).
            LocalInTaxi = false; _taxiTarget = null; _taxiInstantArmed = false;
        }

        /// <summary>Role-based step — called each frame in-game.</summary>
        public static void Tick()
        {
            try
            {
                if (SaveGameManager.Current == null) return;

                TickTaxiLiveCheck();   // F5: the ride's completion event can be missed — a live read closes the ride

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

                    // TRAFFIC-CONSIST T1 step 2: the host's stand-ins for other players' leftovers are ordinary
                    // entries in the SAME ghost dictionary, so the existing interpolation moves them - there is no
                    // second mover. A no-op (one count test) while nobody is publishing.
                    TickGhosts();
                    TickSenseProxyRetry();
                    TickTrafficCensus();
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
                    // TRAFFIC-CONSIST T1 step 1: the publish rides the HOST'S OWN beat - 0.2 s (open question 1,
                    // decided 2026-09-18) - and PublishLeftovers itself holds the rule about when to send at all.
                    _publishTimer -= Time.unscaledDeltaTime;
                    if (_publishTimer <= 0f) { _publishTimer = PublishInterval; PublishLeftovers(); }
                    TickSenseProxyRetry();
                    TickTrafficCensus();
#if BAMP_DEV
                    TickCensus();
                    TickPushProbe();
#endif
                }
                else if (_foreign.Count > 0)
                {
                    // Review 2026-09-18: the host stopped its server but stayed in the world - nobody publishes to
                    // it any more and the host branch above (the only sweeper) no longer runs. Retire every
                    // stand-in through the ordinary path now; without this they stay as solid props for good.
                    SweepForeignTraffic(Time.unscaledTime, _noPeers);
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
            /// <summary>S3 (2026-09-12): the car's TRUE velocity, straight off the same VehicleComponent
            /// this row was built from (GetVelocity() = its rigidbody velocity, m/s).</summary>
            public Vector3 Vel;
            /// <summary>T3 P3: 1 = stopped or braking, read live from Gley at the moment this row was built.</summary>
            public byte    St;
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

                    // S3: the velocity comes from the SAME VehicleComponent whose transform this row
                    // sampled (vc above) — rb.velocity, so a braking or turning car is honest on the wire
                    // instead of being back-derived by the client from two quantized positions.
                    Vector3 vel = default;
                    try { vel = vc.GetVelocity(); } catch { }
                    // T3 P3: "stopped or braking" travels WITH the row, so the client never has to infer it from a
                    // velocity that is already a beat old. Live read at the moment of commitment.
                    _masterScratch.Add(new MasterCar { Index = index, Model = model, Colors = colors, Pos = pos, Rot = rot, Vel = vel, St = StoppedFlag(index, vel) });
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrafficSync] BuildMaster: {ex.Message}");
            }
            return _masterScratch;
        }

        /// <summary>T3 P3 (design Q4, F15): 1 when Gley's own driving action for this car is one of the stop-ish
        /// ones - GiveWay 15, StopInPoint 20, TempStop 60, StopInDistance 70, StopNow 90
        /// (GleyTrafficSystem/SpecialDriveActionTypes.cs) - or the car is simply slower than StoppedSpeed. Read
        /// live, per row. TrafficManager.Instance lazily CREATES a manager, so the state is tested first (review
        /// #2 MINOR-2); any read failure yields 0 = "moving or unknown", which is exactly today's behaviour.</summary>
        private static byte StoppedFlag(int index, Vector3 vel)
        {
            try
            {
                if (vel.sqrMagnitude < StoppedSpeed * StoppedSpeed) return 1;
                if (!TrafficManager.HasInstance || !TrafficManager.IsInitialized) return 0;
                var tm = TrafficManager.Instance;
                if (tm == null) return 0;
                var state = tm.GetCurrentDrivingState(index);
                switch (state.Item2)
                {
                    case SpecialDriveActionTypes.GiveWay:
                    case SpecialDriveActionTypes.StopInPoint:
                    case SpecialDriveActionTypes.TempStop:
                    case SpecialDriveActionTypes.StopInDistance:
                    case SpecialDriveActionTypes.StopNow:
                        return 1;
                }
            }
            catch { }
            return 0;
        }

        // T2 per-peer state: PLAYER id → (pool slot → identity token = the Colors list ref last sent).
        // A slot leaving the peer's radius is FORGOTTEN, so re-entry re-sends identity — the client
        // culls its ghost at the same boundary and needs the model again to respawn it.
        // Review B1: keyed by PLAYER id, never link id — LiteNetLib RECYCLES peer ids, and a rejoining
        // client inheriting the old map would receive identity-less DTOs and spawn NO traffic at all
        // (the round-281 rule at MPServer.cs: per-peer state dies with the connection).
        private static readonly Dictionary<string, Dictionary<int, object>> _peerSentIdentity = new();

        // S1 (2026-09-12): the traffic stream left the shared reliable-ordered channel.
        private static long  _trafficSeq;                // monotonic stamp on every snapshot the host sends
        private static bool  _laneUnreliable;            // what the transport actually did with the last send
        // c3 (2026-09-12): the lane line is EDGE-TRIGGERED, not once-per-session. The first snapshot of a
        // session is small enough to fit under the MTU, so a once-only line always read "unreliable" while
        // every later over-MTU send falls back to the reliable stream. Capped so a flapping link can't spam.
        private static int   _laneLogState;              // 0 = none yet, 1 = unreliable, 2 = reliable
        private static int   _laneLogLines;
        private const  int   LaneLogMaxLines = 6;
        private const  float IdentityReassertSeconds = 5f;
        private static float _identityReassertAt = -999f;

        /// <summary>Review B1/MIN-3: drop a departed (or teleported/reloaded) peer's traffic state, and
        /// force the next lights beat to a FULL re-assert so the newcomer starts from truth.</summary>
        public static void ForgetPeer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            _peerSentIdentity.Remove(playerId);
            _peerTraffic.Remove(playerId); _peerTrafficPin.Remove(playerId);   // TRAFFIC-APART P7: its mode state goes with it
            // TRAFFIC-CONSIST T1 step 4: a departing owner's published leftovers and their stand-ins go AT ONCE -
            // nobody is left to update them, and a rejoin takes a new ordinal (design failure case 1).
            if (_foreign.TryGetValue(playerId, out var goneF))
            {
                int n = goneF.StandIns.Count, rows = goneF.Rows.Count;
                DropStandIns(goneF, null);
                _foreign.Remove(playerId);
                if (rows > 0 || n > 0) Plugin.Logger.LogInfo($"[TrafficSync] '{playerId}' left: {rows} relayed leftover row(s) and {n} stand-in(s) dropped.");
            }
            _lightsFullSentAt = -999f;
        }

        // ── TRAFFIC-APART (user ruling 2026-09-12): a client far from everyone runs its OWN traffic ─────
        //
        // While a client is beyond 350 m from EVERY other player it runs its own local Gley ambient traffic; within
        // 250 m of anyone the host's traffic rules (the ghosts). The HOST decides, per peer, on this same 0.2 s beat
        // and says so with an explicit message; the client obeys and acks. No leader election: the host is the
        // authority for any pair of players in range, two clients far from the host included.
        internal const string ModeGhost = "ghost";
        internal const string ModeLocal = "local";
        private const float ModeFarMeters       = 350f;   // ghost -> local: the nearest other player is further than this
        private const float ModeNearMeters      = 250f;   // local -> ghost: anyone is nearer than this (100 m hysteresis band)
        private const float ModeReassertSeconds = 5f;     // the CURRENT mode is re-sent to every peer this often
        private static float _modeReassertAt = -999f;

        private sealed class PeerTraffic
        {
            public string Mode  = ModeGhost;   // new peers start in GHOST — today's behaviour
            public int    Seq;                 // per-peer flip counter; an ack for an older Seq is a late ack, ignored
            public bool   Acked = true;        // has the client confirmed THIS Seq?  Gates the local-mode cut-off below
            public float  FlipAt;              // host unscaled time of the flip
        }
        private static readonly Dictionary<string, PeerTraffic> _peerTraffic = new();
        /// <summary>P9 test seam: pid -> "local"/"ghost" pinned by `trafficmode force`. Absent = the distance rule.</summary>
        private static readonly Dictionary<string, string> _peerTrafficPin = new();

        /// <summary>The position the traffic rules judge a REMOTE player by: the ridden car when they are a passenger
        /// (review M3 — their avatar is parked at the boarding door, which is not where they are), else the avatar
        /// itself. A player INDOORS keeps their avatar at the building (RemotePlayerManager.GetPlayerPosition stays
        /// valid while masked), and that is exactly the outside position to measure from. An unresolvable ride yields
        /// no position at all rather than a wrong one.</summary>
        private static bool TryGetPlayerAnchorPosition(string pid, out Vector3 pos)
        {
            pos = default;
            if (PassengerSync.TryGetRide(pid, out var rideVid))
                return VehicleManager.TryGetGhostPosition(rideVid, out pos);
            return RemotePlayerManager.TryGetRemotePosition(pid, out pos);
        }

        /// <summary>The position the traffic rules judge the LOCAL player by — the same one UpdateTrafficAnchors feeds
        /// Gley: the ridden car, else the live character outdoors, else the anchor pinned at the last outside position
        /// while indoors.</summary>
        private static bool LocalAnchorPosition(out Vector3 pos)
        {
            pos = default;
            try
            {
                var rideAnchor = PassengerRide.RideAnchorTransform();
                if (rideAnchor != null) { pos = rideAnchor.position; return true; }
                var ch = PlayerHelper.PlayerController?.Character;
                if (ch == null) return false;
                bool inside = LocalInBuilding;
                try { inside = BuildingManager.IsInsideBuilding; } catch { }
                pos = (inside && _hasOutsidePos) ? _lastOutsidePos : ch.transform.position;
                return true;
            }
            catch { return false; }
        }

        /// <summary>TRAFFIC-APART P2: one verdict per connected peer, on the 0.2 s snapshot beat. The distance that
        /// decides is to the NEAREST other player — the host's own anchor and every other client — with a 250/350 m
        /// hysteresis band so a player walking the boundary cannot flap. A peer whose position (or everyone else's)
        /// is unknown this beat keeps the mode it has: no verdict without evidence.</summary>
        private static void EvaluatePeerTrafficModes(List<(MPLink peer, string playerId)> peers, Dictionary<string, Vector3> posByPid, float now)
        {
            bool reassert = now - _modeReassertAt >= ModeReassertSeconds;
            if (reassert) _modeReassertAt = now;
            bool haveHost = LocalAnchorPosition(out var hostPos);
            foreach (var (link, pid) in peers)
            {
                if (link == null || string.IsNullOrEmpty(pid)) continue;
                if (!_peerTraffic.TryGetValue(pid, out var pt)) _peerTraffic[pid] = pt = new PeerTraffic();

                float nearest = float.PositiveInfinity;
                if (posByPid.TryGetValue(pid, out var me))
                {
                    if (haveHost) nearest = Vector3.Distance(me, hostPos);
                    foreach (var other in peers)
                    {
                        if (string.IsNullOrEmpty(other.playerId) || other.playerId == pid) continue;
                        if (posByPid.TryGetValue(other.playerId, out var op)) nearest = Mathf.Min(nearest, Vector3.Distance(me, op));
                    }
                }

                string want = pt.Mode;
                if (_peerTrafficPin.TryGetValue(pid, out var pin) && !string.IsNullOrEmpty(pin)) want = pin;
                else if (float.IsPositiveInfinity(nearest)) { }                                  // nothing to measure against this beat
                else if (pt.Mode == ModeGhost) { if (nearest > ModeFarMeters) want = ModeLocal; }
                else if (nearest <= ModeNearMeters) want = ModeGhost;

                if (want != pt.Mode)
                {
                    string old = pt.Mode;
                    pt.Mode = want; pt.Seq++; pt.Acked = false; pt.FlipAt = now;
                    // P8: this peer is about to paint the host's lights again — make the next lights beat a FULL
                    // broadcast so it starts from truth instead of waiting up to 10 s for the periodic re-assert.
                    if (want == ModeGhost) _lightsFullSentAt = -999f;
                    string d = float.IsPositiveInfinity(nearest) ? "unknown" : $"{nearest:F0} m";
                    Plugin.Logger.LogInfo($"[TrafficSync] traffic mode for {pid}: {old} -> {want} (nearest other player {d}).");
                    SendTrafficMode(link, pid, pt);
                }
                else if (reassert) SendTrafficMode(link, pid, pt);   // recurrence: covers the join race, a reconnect, a host restart, a lost message
            }
        }

        private static void SendTrafficMode(MPLink link, string pid, PeerTraffic pt)
        {
            try { MPServer.SendTrafficModeTo(link, new TrafficModePayload { Mode = pt.Mode, Seq = pt.Seq }); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] traffic mode -> {pid}: {ex.Message}"); }
        }

        /// <summary>P4: the client confirms the mode named by Seq. An ack for a superseded flip is dropped.</summary>
        public static void HostOnModeAck(string pid, TrafficModeAckPayload p)
        {
            if (p == null || string.IsNullOrEmpty(pid)) return;
            if (!_peerTraffic.TryGetValue(pid, out var pt)) return;
            if (pt.Acked || p.Seq != pt.Seq || p.Mode != pt.Mode) return;
            pt.Acked = true;
            Plugin.Logger.LogInfo($"[TrafficSync] traffic mode for {pid}: {pt.Mode} in force after {Time.unscaledTime - pt.FlipAt:F1} s"
                                + (pt.Mode == ModeLocal ? " - its snapshot stream and its traffic anchor stop now." : "."));
        }

        /// <summary>P9 test seam (`trafficmode force`): pin a peer's verdict, or hand it back to the distance rule
        /// ("auto"). Returns how many connected peers matched. Nothing flips here — the next evaluator beat does it.</summary>
        public static int HostForceTrafficMode(string who, string mode)
        {
            if (!MPServer.IsRunning || string.IsNullOrEmpty(who)) return 0;
            bool all = string.Equals(who, "all", StringComparison.OrdinalIgnoreCase);
            int n = 0;
            foreach (var (link, pid) in MPServer.ConnectedClientPeers())
            {
                if (string.IsNullOrEmpty(pid)) continue;
                if (!all && pid != who) continue;
                n++;
                if (mode == "auto") _peerTrafficPin.Remove(pid); else _peerTrafficPin[pid] = mode;
            }
            return n;
        }

        private static void BroadcastPerPeer(List<MasterCar> master)
        {
            var peers = MPServer.ConnectedClientPeers();   // review MIN-9: named peers only (post-Hello)
            if (peers.Count == 0) return;
            float now = Time.unscaledTime;
            float r2 = HostSendRadius * HostSendRadius;
            // S1: identity (Model+Colors) rides only when this peer has not seen the slot's occupant — and on
            // the unreliable lane THAT packet can be lost, leaving the client unable to spawn the slot at all
            // (ApplySnapshot skips a car with no known identity). Cheapest cover, and smaller than a client
            // "unknown slot" request (no new message type, no client→host traffic, no per-slot bookkeeping):
            // re-assert identity for every in-range slot once every IdentityReassertSeconds, so a lost
            // identity packet costs at most that long — one identity burst per peer per 5 s.
            bool idReassert = now - _identityReassertAt >= IdentityReassertSeconds;
            if (idReassert) _identityReassertAt = now;
            var livePids = new HashSet<string>();
            // TRAFFIC-APART P2: every player's position FIRST. The verdict for ONE peer needs the distance to EVERY
            // other player, so the positions can no longer be resolved inside the send loop below.
            var posByPid = new Dictionary<string, Vector3>();
            foreach (var (link, pid) in peers)
            {
                if (link == null || string.IsNullOrEmpty(pid)) continue;
                livePids.Add(pid);
                if (TryGetPlayerAnchorPosition(pid, out var ppos)) posByPid[pid] = ppos;
            }
            EvaluatePeerTrafficModes(peers, posByPid, now);

            foreach (var (link, pid) in peers)
            {
                if (link == null || string.IsNullOrEmpty(pid)) continue;
                // TRAFFIC-APART P3 — THE LOAD-BEARING GATE. A peer in LOCAL mode leaves the stream only once it has
                // ACKED that mode. WHY THE ACK COMES FIRST: ApplySnapshot DESTROYS every ghost absent from a snapshot
                // (:1021-1031, the absent-sweep). So the instant the host stops sending, that client's next apply
                // — or its last one — removes every ghost it still holds at once: the total pop-out this whole design
                // exists to avoid. The host therefore keeps streaming, AND keeps feeding that peer's traffic anchor
                // (UpdateTrafficAnchors), while the client fades its ghosts out one by one off-screen; the ack ("my
                // last ghost is gone") is what closes the tap. Nothing empty is sent in its place — an empty snapshot
                // IS the pop-out. A flip back to GHOST re-opens stream and anchor IMMEDIATELY, no ack needed: in that
                // direction the client has nothing to lose, it is only gaining cars back.
                if (_peerTraffic.TryGetValue(pid, out var pmode) && pmode.Mode == ModeLocal && pmode.Acked) continue;
                bool havePos = posByPid.TryGetValue(pid, out var anchor);
                if (!_peerSentIdentity.TryGetValue(pid, out var sent))
                    _peerSentIdentity[pid] = sent = new Dictionary<int, object>();
                var snap = new TrafficSnapshotPayload { T = now, Seq = ++_trafficSeq };
                foreach (var mc in master)
                {
                    // No known position (player still spawning) → uncullled full feed, identity-gated.
                    if (havePos && (mc.Pos - anchor).sqrMagnitude > r2) { sent.Remove(mc.Index); continue; }
                    bool needIdentity = idReassert || !sent.TryGetValue(mc.Index, out var tok) || !ReferenceEquals(tok, mc.Colors);
                    var dto = new TrafficCarDto
                    {
                        Index = mc.Index,
                        X = Mathf.RoundToInt(mc.Pos.x * 100f), Y = Mathf.RoundToInt(mc.Pos.y * 100f), Z = Mathf.RoundToInt(mc.Pos.z * 100f),
                        Qx = Mathf.RoundToInt(mc.Rot.x * 10000f), Qy = Mathf.RoundToInt(mc.Rot.y * 10000f),
                        Qz = Mathf.RoundToInt(mc.Rot.z * 10000f), Qw = Mathf.RoundToInt(mc.Rot.w * 10000f),
                        // S3: velocity in cm/s (0 = standing still / unknown — the client derives then).
                        Vx = Mathf.RoundToInt(mc.Vel.x * 100f), Vy = Mathf.RoundToInt(mc.Vel.y * 100f),
                        Vz = Mathf.RoundToInt(mc.Vel.z * 100f),
                        // T3 P3: additive - a 0.3.0 client ignores an unknown JSON member and behaves as today.
                        St = mc.St,
                    };
                    if (needIdentity) { dto.Model = mc.Model; dto.Colors = mc.Colors; sent[mc.Index] = mc.Colors; }
                    snap.Cars.Add(dto);
                }
                // TRAFFIC-CONSIST T1 step 3: FOLD every OTHER client's published leftovers into this peer's
                // ordinary snapshot, under their reserved ids. The OWNER is skipped - it still holds those cars
                // itself, and a second copy of its own car would be the "two truths for one car" the design rules
                // out structurally. Same send radius as the host's own rows, and identity always rides (the rows
                // are few and only exist during a fade), so a receiving client can spawn them on first sight.
                // Nothing on the receiving side is new: they are ordinary TrafficSnapshot rows, so a 0.3.0 client
                // renders them exactly like the host's own cars.
                foreach (var kvF in _foreign)
                {
                    if (kvF.Key == pid) continue;
                    foreach (var row in kvF.Value.Rows)
                    {
                        if (havePos)
                        {
                            var rp = new Vector3(row.X * 0.01f, row.Y * 0.01f, row.Z * 0.01f);
                            if ((rp - anchor).sqrMagnitude > r2) continue;
                        }
                        snap.Cars.Add(row);
                    }
                }
                bool laneOk = MPServer.SendTrafficSnapshotTo(link, snap);
                _laneUnreliable = laneOk;
                int laneState = laneOk ? 1 : 2;
                if (laneState != _laneLogState)
                {
                    _laneLogState = laneState;
                    if (_laneLogLines < LaneLogMaxLines)
                    {
                        _laneLogLines++;
                        Plugin.Logger.LogInfo(laneOk
                            ? $"[TrafficSync] snapshots on the unreliable lane (seq-guarded); " +
                              $"identity re-send every {IdentityReassertSeconds:0}s."
                            : $"[TrafficSync] snapshots on the reliable lane (over-MTU on this link, " +
                              $"ReliableUnordered stream; seq-guarded); identity re-send every {IdentityReassertSeconds:0}s.");
                    }
                }
            }
            // Review B1: ALWAYS prune (the old "only when shrunk" gate skipped equal-count swaps, and
            // the empty-peers early-return above means the last disconnect is handled by ForgetPeer).
            if (_peerSentIdentity.Count != livePids.Count)
            {
                var stale = new List<string>();
                foreach (var k in _peerSentIdentity.Keys) if (!livePids.Contains(k)) stale.Add(k);
                foreach (var k in stale) _peerSentIdentity.Remove(k);
            }
            // TRAFFIC-APART P2: the per-peer mode state rides the SAME sweep as the identity map above.
            // Review r1 MINOR-5: a count test misses a same-beat leave+join (one out, one in), which would hand the
            // rejoined pid the departed one's stale Mode=local/Acked=true. The dictionary is tiny - scan it every beat.
            {
                List<string>? staleMode = null;
                foreach (var k in _peerTraffic.Keys) if (!livePids.Contains(k)) (staleMode ??= new List<string>()).Add(k);
                if (staleMode != null) foreach (var k in staleMode) { _peerTraffic.Remove(k); _peerTrafficPin.Remove(k); }
            }
            // TRAFFIC-CONSIST T1 step 4: published rows are perishable - they die after 2 s of silence and with
            // the peer. Rides this same 0.2 s beat (an event, not a timer of its own).
            SweepForeignTraffic(now, livePids);
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
            // TRAFFIC-APART P5(v): in LOCAL mode this client advances its OWN light phases
            // (Patch_IM_UpdateIntersections_ClientSkip lets UpdateIntersections run again), so painting the host's
            // states on top would fight its own timer. Dropped HERE, beside the reason, not at the MPClient dispatch.
            if (ClientRunsLocalTraffic) return;
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
            if (!MPWorldReady.CanMaterialize) return; // round-188: 5 Hz stream — a drop is recurrence-covered
            // TRAFFIC-APART P5(vi): in LOCAL mode a snapshot arriving BEFORE the ack still matters — the ghosts still
            // here must keep MOVING (and the absent-sweep must keep working) while they fade out off-screen. Once the
            // ack has gone the handover is over and the host's stream is closed, so a late snapshot is ignored whole.
            if (ClientRunsLocalTraffic && _handover != HandoverToLocal) return;
            try
            {
                // S4: keep one smoothed estimate of "what is the host's clock here, now", since render time
                // is expressed in HOST stamps. A big step (host restart, a long stall) re-seeds it rather
                // than crawling there over dozens of packets.
                float off = Time.unscaledTime - snap.T;
                if (!_haveClockOffset || Mathf.Abs(off - _clockOffset) > 1f) { _clockOffset = off; _haveClockOffset = true; }
                else _clockOffset = Mathf.Lerp(_clockOffset, off, ClockOffsetLerp);

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
                    // Review MINOR-4: a degenerate quaternion KEEPS THE LAST ROTATION for this slot (the
                    // packet no longer carries a separate yaw); a slot with no ghost yet has none — identity.
                    if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f)
                        rot = _ghosts.TryGetValue(car.Index, out var lastRotG) ? lastRotG.TargetRot : Quaternion.identity;

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
                        // TRAFFIC-APART P5(vi): never SPAWN a ghost while this client runs its own traffic — during
                        // the handover the ghost population may only shrink. Existing ghosts keep updating below.
                        if (ClientRunsLocalTraffic) { _ghosts.Remove(car.Index); continue; }
                        var go = SpawnTrafficGhost(car.Model, pos, rot);
                        if (go == null) { _ghosts.Remove(car.Index); continue; }
                        g = new TrafficGhost { Go = go, Model = car.Model, TargetPos = pos, TargetRot = rot, TargetAt = Time.unscaledTime, HostT = snap.T };
                        g.Body = go.GetComponent<Rigidbody>();   // ROOT rb only (a child rb would teleport just that part)
                        try { var all = go.GetComponentsInChildren<Collider>(true); var sol = new List<Collider>(all.Length); foreach (var c in all) if (c != null && !c.isTrigger) sol.Add(c); g.Solids = sol.ToArray(); } catch { }
                        // S3: a brand-new ghost already knows its speed when the host sent one — no need to
                        // wait for a second packet before it can move between snapshots.
                        if (car.Vx != 0 || car.Vy != 0 || car.Vz != 0)
                        { g.Velocity = new Vector3(car.Vx * 0.01f, car.Vy * 0.01f, car.Vz * 0.01f); g.HasVel = true; }
                        g.Stopped = car.St != 0;   // T3 P3
                        _ghosts[car.Index] = g;
                    }
                    else
                    {
                        // Same live car, small inter-packet move (a big jump = slot reuse, respawned above).
                        // S4: the state that WAS the newest becomes the previous one — rendering interpolates
                        // between the two. S3: use the host's own velocity when the packet carries one; an
                        // older host sends none, so derive it from the packet stamp delta exactly as before
                        // (if two packets land in one client frame, tiny dt, keep the previous velocity —
                        // zeroing it froze the motion = visible stutter).
                        float hdt = snap.T - g.HostT;
                        g.PrevPos    = g.TargetPos;
                        g.PrevRot    = g.TargetRot;
                        g.PrevVel    = g.Velocity;
                        g.PrevHostT  = g.HostT;
                        g.HasPrevVel = g.HasVel;
                        g.HasPrev    = true;
                        if (car.Vx != 0 || car.Vy != 0 || car.Vz != 0)
                        { g.Velocity = new Vector3(car.Vx * 0.01f, car.Vy * 0.01f, car.Vz * 0.01f); g.HasVel = true; }
                        else if (hdt > 0.005f)
                        { g.Velocity = (pos - g.TargetPos) / hdt; g.HasVel = true; }
                        g.TargetPos = pos;
                        g.TargetRot = rot;
                        g.TargetAt  = Time.unscaledTime;
                        g.HostT     = snap.T;
                        g.Stopped   = car.St != 0;   // T3 P3: the newest word on whether this car moves at all
#if BAMP_DEV
                        // S4 jitter, REDEFINED (fold c): the visible CORRECTION this arrival implies. The
                        // ghost is drawn PlaybackDelay behind the host clock, so nothing is ever rendered as
                        // recently as snap.T — the old "truth at snap.T vs a render record within 50 ms"
                        // definition could never sample anything (rig run 2: samples=0). Instead: the ghost
                        // stands at P now, for render time rt; the NEW state implies a different point for
                        // that SAME rt, and the gap is the jerk the player sees when the ghost is corrected.
                        if (g.Go != null && haveMe && (pos - me).sqrMagnitude < JitterNearSq)
                        {
                            float rt = Time.unscaledTime - _clockOffset - PlaybackDelay;
                            Vector3 implied = pos + g.Velocity * Mathf.Clamp(rt - snap.T, -MaxExtrapolateSeconds, MaxExtrapolateSeconds);
                            _jitter.Add((Time.unscaledTime, (g.Go.transform.position - implied).magnitude));
                            if (_jitter.Count > 4096) _jitter.RemoveRange(0, _jitter.Count - 4096);
                        }
#endif
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
            // unguarded VehicleComponent.OnTriggerEnter deref (decompile :428, other.attachedRigidbody - the June NRE
            // class); on playerLayers the car takes the safe branch, brakes and waits, exactly as for a player's car.
            // Same relayer the A2 look-alike uses. UNCONDITIONAL since TRAFFIC-CONSIST T5 (review HIGH-1): a client's
            // local-mode cars and fade leftovers run the real sensor handler whatever ClientServiceSimEnabled says,
            // so no ghost may stay on AiVehicles once a sensed layer exists (the routine is a no-op without one).
            RelayerCollidersToServiceLayer(body);
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
                if (ai >= 0) excluded.Add(ai);   // review 2026-09-18: never the traffic layer itself - a proxy there takes the same-layer branch and its null-rigidbody deref
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

        /// <summary>Smooths each traffic ghost toward its networked transform. S4 (2026-09-12): the pose is
        /// taken from BUFFERED INTERPOLATION — each ghost is drawn PlaybackDelay behind the newest host stamp,
        /// between the two states it has received, so motion stays continuous between 5 Hz packets even at low
        /// FPS and a late packet no longer lands as a jerk. Only a dry buffer dead-reckons.</summary>
        private static void TickGhosts()
        {
            if (_ghosts.Count == 0) return;
            // Cap the blend below 1 so packet corrections spread over a couple
            // of frames instead of landing as a visible pop at low FPS (the
            // uncapped factor saturates past ~70ms frames).
            float k   = Mathf.Min(Time.deltaTime * GhostLerp, 0.5f);
            float now = Time.unscaledTime;
            // S4: the moment, ON THE HOST'S CLOCK, that this frame draws.
            float renderT = now - _clockOffset - PlaybackDelay;
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
                // S4: normal case — render time lies between the two received states, so INTERPOLATE
                // (Hermite on the two velocities when both are known, else linear) and Slerp the rotation on
                // exactly the same schedule, instead of chasing an extrapolated point.
                float ahead = 0f;
                Vector3 desiredPos; Quaternion desiredRot;
                // T3 P1/P3: "this car is not really moving" - either the newest speed is below the floor or the
                // packet says so outright. Everything predictive is off for such a car.
                float speed = g.Velocity.magnitude;
                bool  still = g.Stopped || speed < PredictSpeedFloor;
                if (_haveClockOffset && g.HasPrev && renderT < g.HostT && g.HostT - g.PrevHostT > 0.001f)
                {
                    float span = g.HostT - g.PrevHostT;
                    float u    = Mathf.Clamp01((renderT - g.PrevHostT) / span);
                    // T3 P5: the Hermite needs BOTH tangents to be real. A long tangent at one end against a
                    // near-zero one at the other overshoots past its own endpoint - a car easing to a stop is
                    // exactly that case - so a slow or stopped endpoint takes the plain lerp instead.
                    bool hermite = g.HasVel && g.HasPrevVel && !still && g.PrevVel.magnitude >= PredictSpeedFloor;
                    desiredPos = hermite
                        ? Hermite(g.PrevPos, g.PrevVel * span, g.TargetPos, g.Velocity * span, u)
                        : Vector3.Lerp(g.PrevPos, g.TargetPos, u);
                    desiredRot = Quaternion.Slerp(g.PrevRot, g.TargetRot, u);
                }
                else
                {
                    // Buffer dry (a lost or late packet), only one state so far, or no clock estimate yet:
                    // dead-reckon from the newest state along its velocity, capped, rotation held at the
                    // newest received one (never extrapolated).
                    ahead = _haveClockOffset
                        ? Mathf.Clamp(renderT - g.HostT, 0f, MaxExtrapolateSeconds)
                        : Mathf.Min(now - g.TargetAt, MaxExtrapolateSeconds);
                    if (still)
                    {
                        // T3 P1/P3: no extrapolation AT ALL below the floor or under St. The gap a stopped car
                        // leaves is real; inventing motion into it is what pushes a ghost into the car ahead.
                        ahead = 0f;
                        desiredPos = g.TargetPos;
                    }
                    else
                    {
                        // T3 P2 - BRAKING DAMP. When the newest speed is below the previous one the car is
                        // slowing, so predict with the deceleration actually measured between the two stamps and
                        // never travel further than its own stopping distance v^2/2a: a constant-velocity guess
                        // would sail it into the back of the queue it is joining.
                        float dist = speed * ahead;
                        if (g.HasPrev && g.HasPrevVel && g.HostT - g.PrevHostT > 0.001f)
                        {
                            float prevSpeed = g.PrevVel.magnitude;
                            if (prevSpeed > speed)
                            {
                                float decel = (prevSpeed - speed) / (g.HostT - g.PrevHostT);
                                if (decel > 0.01f)
                                    dist = Mathf.Min(dist - 0.5f * decel * ahead * ahead, speed * speed / (2f * decel));
                            }
                        }
                        if (dist < 0f) dist = 0f;
                        desiredPos = g.TargetPos + g.Velocity.normalized * dist;
                    }
                    desiredRot = g.TargetRot;
                    if (ahead > 0f)
                    {
                        _deadReckonFrames++;
                        float dr = (desiredPos - g.TargetPos).magnitude;
                        if (dr > _maxDeadReckonMetres) _maxDeadReckonMetres = dr;
                    }
                }
                Vector3    smoothedPos = Vector3.Lerp(t.position, desiredPos, k);
                Quaternion smoothedRot = Quaternion.Slerp(t.rotation, desiredRot, k);
                // T3 P4 - THE RATE-LIMITED REWIND, in place of the old absolute no-rewind clamp (I3: a follower
                // that may never move backwards can never give an overlap back, so a wrong gap became permanent).
                // Backward motion along the heading is now limited, not forbidden: RewindRate normally, and
                // RewindFastRate once the backlog passes RewindBacklogMetres, so the correction reads as the car
                // easing back rather than as the 0.2.3 snap. A car the packet calls stopped (P3) has the limit
                // lifted altogether - it is not driving away from the correction, and the chase rate k still
                // spreads it over a few frames.
                {
                    Vector3 mv  = smoothedPos - t.position;
                    Vector3 dir = g.Velocity.sqrMagnitude > 0.01f ? g.Velocity.normalized : t.forward;
                    float along = Vector3.Dot(mv, dir);
                    if (along < 0f)
                    {
                        float backlog = Mathf.Max(0f, Vector3.Dot(t.position - desiredPos, dir));
                        if (backlog > _maxRewindMetres) _maxRewindMetres = backlog;
                        _rewindFrames++;
                        if (!g.Stopped)
                        {
                            float cap = (backlog > RewindBacklogMetres ? RewindFastRate : RewindRate) * Time.deltaTime;
                            if (along < -cap) smoothedPos -= dir * (along + cap);
                        }
                    }
                }
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
            // S1/S4: the next session is a new stream — a fresh host starts its Seq at 1 and its clock is
            // unrelated to this one's, so neither may be carried across a disconnect.
            lock (_seqLock) { _seqLast = 0; _seqDropped = 0; _seqDropLogged = false; _sawOutOfOrder = false; }
            _haveClockOffset = false;
#if BAMP_DEV
            _jitter.Clear();
#endif
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
        // Fold b: the grid dimensions are read from the GridManager Gley is actually
        // using (its own `currentSceneData`, decompile GridManager.cs:13), never from
        // CurrentSceneData.GetSceneInstance() — see PositionInGrid below for why.
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
                // Fold b: judge against the grid of the manager Gley is really running (see PositionInGrid).
                var gm = TrafficManager.HasInstance ? TrafficManager.Instance.densityManager?.gridManager : null;
                if (!PositionInGrid(gm, p, out int r, out int c, out int rows))
                    return LogBadAnchor(t, p, $"off-grid cell [{r},{c}] vs {rows} rows");
            }
            catch { }   // the guard must never break the feed itself
            return true;
        }

        /// <summary>Replays Gley's own cell math against the LIVE grid and says whether this position can be indexed
        /// at all. Returns true with rows=0 when there is no grid to judge against — nothing to overflow, so vanilla
        /// behaviour. Callers must wrap this in a try/catch: reading the scene data can throw.
        /// <para>The scene data comes from the passed GridManager's OWN <c>currentSceneData</c> (decompile
        /// GridManager.cs:13, protected, publicized) — the very grid whose <c>GetCell</c> would throw. It must NEVER
        /// come from <c>CurrentSceneData.GetSceneInstance()</c>: when no grid component is in the scene that method
        /// CREATES a dummy <c>GameObject("GleyTrafficSystem")</c> and puts a CurrentSceneData on it
        /// (CurrentSceneData.cs:34-37); the extra component then trips Gley's "Multiple Grid components" path, which
        /// aborts TrafficManager.Initialize (TrafficManager.cs:240-246) — the guard would break traffic outright.
        /// <c>_gridScene</c> is just a cache of the manager's reference, re-seated whenever the manager's differs.</para></summary>
        internal static bool PositionInGrid(GleyUrbanAssets.GridManager? gm, Vector3 p, out int r, out int c, out int rows)
        {
            r = 0; c = 0; rows = 0;
            var sd = gm != null ? gm.currentSceneData : null;
            if (sd == null) return true;                          // no manager / no scene data to judge against → vanilla behavior
            if (!ReferenceEquals(_gridScene, sd)) _gridScene = sd;
            var grid = sd.grid;
            if (grid == null || grid.Length == 0 || sd.gridCellSize <= 0)
                return true;   // no grid to judge against → vanilla behavior
            rows = grid.Length;
            r = Mathf.FloorToInt(Mathf.Abs((sd.gridCorner.z - p.z) / sd.gridCellSize));
            c = Mathf.FloorToInt(Mathf.Abs((sd.gridCorner.x - p.x) / sd.gridCellSize));
            return r < rows && grid[r].row != null && c < grid[r].row.Length;
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

        // ── TRAFFIC-GRID-1 (3 host-log sightings, none on a mod frame, all right after "N player area(s)" grew) ──
        // The real defect is a STALE ARRAY, not an off-grid camera. Two objects hold the camera-position
        // array and only one of them is re-seated when it is replaced:
        //   * TrafficManager.UpdateCamera (decompile TrafficManager.cs:655-666) allocates a BRAND NEW
        //     NativeArray<float3> whenever the fed camera COUNT changes (:659-661) — which is exactly what
        //     UpdateTrafficAnchors does when a player joins/leaves an area.
        //   * GridManager keeps its OWN reference to that array (GridManager.cs:19) and is re-seated only by
        //     UpdateActiveCells (:131-133), reached through UpdateGrid (:52) — which TrafficManager.Update
        //     calls LAST (:1018).
        // So for one frame after a re-feed the two disagree. Update fills the manager's NEW array
        // (:1001-1004), draws activeCameraIndex = Random.Range(0, <new, longer length>) (:1010) and passes it
        // to the density pass (:1011) → GridManager.GetCell(int) (:115-118) indexes the GridManager's STALE,
        // SHORTER array. A NativeArray read is unchecked in the release player, so that is not an exception
        // but garbage floats, which then blow up as `grid[num]` in CurrentSceneData.GetCell (:47-52). And
        // because the throw aborts Update BEFORE :1018, the stale array is never re-seated — it recurs every
        // frame until something else re-feeds the cameras.
        // Hence the test below is a LENGTH test against the GridManager's own array, not a position test on a
        // live camera transform (that transform is on-grid; the old predicate could never fire). Returning
        // false skips just the density add, which lets Update run on to :1018 and re-seat the array — a
        // one-frame self-heal. Skipping one add is harmless anyway: the pass runs every frame.
        // The position/off-grid test is kept as the second line of defence for the round-199 case.
        private static readonly HashSet<string> _badDensityCameraLogged = new();
        private static int _offGridDensitySkips;

        internal static bool DensityCameraFeedable(DensityManager dm, int idx)
        {
            try
            {
                if (dm == null) return true;
                var gm = dm.gridManager;                       // DensityManager.cs:18 — the GridManager whose GetCell(int) the pass calls
                if (gm == null) return true;
                var tm = TrafficManager.HasInstance ? TrafficManager.Instance : null;
                var arr = gm.activeCameraPositions;            // GridManager.cs:19 — the array GetCell(int) indexes
                if (!arr.IsCreated) return true;
                string reason, kind;
                if (idx < 0 || idx >= arr.Length)
                {
                    kind   = "stale";
                    reason = $"stale camera array: index {idx} of {arr.Length} (the manager now feeds {tm?.activeCameraPositions.Length})";
                }
                else
                {
                    UnityEngine.Vector3 p = arr[idx];
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
                        || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                    {
                        kind   = "nonfinite";
                        reason = $"non-finite position ({p.x:F1}, {p.y:F1}, {p.z:F1})";
                    }
                    else if (!PositionInGrid(gm, p, out int r, out int c, out int rows))
                    {
                        kind   = "offgrid";
                        reason = $"off-grid cell [{r},{c}] vs {rows} rows at ({p.x:F1}, {p.y:F1}, {p.z:F1})";
                    }
                    else return true;
                }
                _offGridDensitySkips++;
                if (_badDensityCameraLogged.Add($"{kind}|{idx}") || _offGridDensitySkips % 600 == 0)
                    Plugin.Logger.LogWarning(
                        $"[TrafficSync] density pass skipped: camera {idx} {reason} - Gley's GetCell would "
                        + $"IndexOutOfRange (TRAFFIC-GRID-1, skip #{_offGridDensitySkips}).");
                return false;
            }
            catch { return true; }   // the guard must never break the density pass itself
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
                if (_pendingHandBack)
                {
                    // Clears _pendingHandBack once a feed lands. Review r6 #3 / r7 #5: retry only when something CAN be fed — the
                    // parked ghost (an outside position on record), the live character outdoors, or the live character inside an
                    // on-grid interior (UpdateTrafficAnchors feeds those too); an off-grid interior with nothing on record waits for
                    // the door instead of re-running the hand-back every frame for nothing.
                    var live = PlayerHelper.PlayerController?.Character?.transform;
                    if (_hasOutsidePos || !BuildingManager.IsInsideBuilding || (live != null && AnchorFeedable(live))) HandBackToVanilla("offline fork retry");
                    return;
                }
                if (_anchorIsGhost && !BuildingManager.IsInsideBuilding)   // review r5 #5: no promotion attempts indoors (an off-grid interior would refuse every frame and log every ~10 s)
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
                // TRAFFIC-APART P7: the offline fork is single player — leave no mode or half-finished handover
                // behind, so a later session starts clean in ghost mode.
                ClientTrafficMode = ModeGhost; _handover = HandoverNone; _modeSeq = 0;
                _modeDeferLogged = false; _localDensityIssued = false; _localDensityWaitLogged = false; _localDensityUninitLogged = false;
                if (!TrafficManager.HasInstance || !TrafficManager.IsInitialized) return;
                var tm = TrafficManager.Instance;
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
                // Review r5 #2/#3: re-enable the manager only AFTER a feed is confirmed (never let it run on the previous camera
                // list), and only outdoors: while the link was up Patch_TM_SetPause blocked the game's entry-time pause, so indoors the
                // manager is normally already enabled; if the player entered a building AFTER the drop the game paused Gley itself
                // and its exit event (VehicleHelper.OnExitBuilding → SetPause(false)) re-enables it — respect that.
                bool insideNow = false; try { insideNow = BuildingManager.IsInsideBuilding; } catch { }
                bool fastForward = false; try { fastForward = UnityEngine.Time.timeScale > 1.01f; } catch { }
                if (!insideNow && !fastForward) tm.enabled = true;   // review r6 #4: the game pauses Gley during fast-forward too (GameSpeedController.ChangeTimeScale → SetPause(FastForward || inside)); its next speed change re-enables
                if (density >= 0)
                {
                    SelfDensityCall = true;                                         // review r2 #8: our own write, never recorded as the game's request
                    try { tm.SetTrafficDensity(density); } catch (System.Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] hand-back ({why}): density restore failed: {ex.Message}"); } finally { SelfDensityCall = false; }   // review r6 #5: never silent
                }
                Plugin.Logger.LogInfo($"[TrafficSync] traffic handed back to vanilla ({why}): density {(density >= 0 ? density.ToString() : "unchanged — no game request recorded")}, anchor → {(usedGhost ? "parked ghost (promoted to the player once outdoors)" : "local player")}.");   // review r6 #5: say which anchor
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
                // TRAFFIC-APART P3 / P5(iii): whose avatars still anchor traffic on THIS machine.
                //  - HOST: a peer that has ACKED local mode is dropped, so CountPlayerAreas loses that area and the
                //    budget below falls — Gley then RECYCLES the cars around that player at its own pace (one per
                //    frame, DriveJob's own readiness test) instead of anything despawning them in a batch.
                //    RemotePlayerManager hands out an ANONYMOUS transform list (:414) and has no pid->transform
                //    accessor, and that file is not ours to edit, so the gated peers are matched by POSITION: both
                //    accessors read the very same go.transform.position inside this one frame, so the float triples
                //    are identical, not merely close.
                //  - CLIENT in LOCAL mode: no remote avatar anchors anything here — this machine's traffic is its own
                //    and follows its own player alone.
                if (MPServer.IsRunning || !ClientRunsLocalTraffic)
                {
                    HashSet<Vector3>? gatedOut = null;
                    if (MPServer.IsRunning)
                        foreach (var kv in _peerTraffic)
                            if (kv.Value.Mode == ModeLocal && kv.Value.Acked
                                && RemotePlayerManager.TryGetRemotePosition(kv.Key, out var gpos))
                                (gatedOut ??= new HashSet<Vector3>()).Add(gpos);
                    foreach (var t in RemotePlayerManager.GetRemotePlayerTransforms())
                        if (t != null && (gatedOut == null || !gatedOut.Contains(t.position))) anchors.Add(t);
                }
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
                    // TRAFFIC-APART P10: no behaviour change — only the reason is now told apart. In LOCAL mode the
                    // hailed car IS this machine's own traffic and the game has already stopped it here, which is the
                    // whole of it: nobody else can see that car, so there is nothing for the host to mirror.
                    if (_nonGhostHailLogs++ < 6)
                        Plugin.Logger.LogInfo(ClientRunsLocalTraffic
                            ? $"[TrafficSync] hail on '{taxiGo.name}' is this machine's OWN traffic (local mode) - "
                            + "not sending. The game already stopped the car here, and no other player can see it, "
                            + "so the host has nothing to mirror."
                            : $"[TrafficSync] hail on '{taxiGo.name}' is NOT a host-mirrored ghost - "
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
        // The game's taxi ride is TaxiSystem.TravelCoroutine: it runs the time
        // machine forward by the trip duration (isFastForwarding), then warps
        // the player.  Our world-clock pinner (MPCanvasUI.TickWorldClock) would
        // normally revert each advance every frame — result: the ride coroutine
        // waits forever for time to move forward and the player is "locked up"
        // in the cab.
        //
        // While LocalInTaxi is set the world-clock pinner gets out of the way.
        // Once the ride completes (the game's own "ba:gameevent_completedtaxiride"
        // announcement) we reset the pinner's window to the new clock so it
        // doesn't see the advance retroactively as a skip and roll it back.
        //
        // 2026-09-18 (H-TAXISTRAND-1): both ends are bound to the ride itself now
        // — MPPatches.Patch_TaxiSystem_TravelCoroutine_RideStart and
        // Patch_GameEvent_CompletedTaxiRide_RideEnd.  The previous binding sat on
        // the BuildingResume.TaxiTravel wrapper, which ends ~1.3 s BEFORE the ride
        // and runs even when TravelTo refuses the trip.
        public static bool LocalInTaxi { get; private set; }

        // X2 arrival trace (H-TAXISTRAND-1, log-only): the destination captured at the start of the ride.
        private static EntityController? _taxiTarget;
        private static bool _taxiInstantArmed;
        private static int  _taxiTraceLines;
        private const int   TaxiTraceBudget = 60;   // arrival lines per session

        public static void OnTaxiTravelStart(EntityController? target = null)
        {
            _taxiTarget = target;
            try { _taxiInstantArmed = MPRestSync.TaxiRidePending; } catch { _taxiInstantArmed = false; }
            if (LocalInTaxi) return;
            LocalInTaxi = true;
            Plugin.Logger.LogInfo("[Taxi] ride start (TaxiSystem.TravelCoroutine) — world-clock suppression OFF until the ride ends.");
        }

        public static void OnTaxiTravelEnd()
        {
            if (!LocalInTaxi) return;
            LocalInTaxi = false;
            Plugin.Logger.LogInfo("[Taxi] ride end (ba:gameevent_completedtaxiride) — world-clock suppression re-armed at post-ride time.");
            // World-clock detector resets itself on the next TickWorldClock pass
            // because `LocalInTaxi` was true the previous frame; the detector
            // already short-circuits in that case.  No explicit reset needed —
            // see MPCanvasUI.TickWorldClock.
            TraceTaxiArrival();
            _taxiTarget = null;
        }

        private static System.Reflection.PropertyInfo? _pTaxiIsTraveling;
        private static bool _taxiProbeResolved;
        private static bool _taxiProbeMissLogged;

        /// <summary>F5 (2026-09-18): the ride's completion event can simply never arrive — the coroutine is cancelled,
        /// the warp throws on a destroyed destination (TaxiSystem.cs:92), or the session drops. LocalInTaxi would then
        /// stay true for the rest of the world: MPCanvasUI's LateUpdate steps out of the way entirely while it is set,
        /// so Time.timeScale is owned by nobody, and the world clock stays on the taxi clamp. So the ride's own live
        /// state decides instead of a timer. TaxiSystem.IsTraveling is `_travelCoroutine != null` (TaxiSystem.cs:20);
        /// TravelTo assigns that field on the same frame our START prefix fires (TaxiSystem.cs:65 — the prefix runs
        /// while the StartCoroutine argument is being evaluated, and the assignment completes before the frame ends),
        /// and TravelCoroutine clears it only on its very last line, after the completion event. So it is already true
        /// by the first tick after the start, and false means the ride is over however it ended.</summary>
        private static void TickTaxiLiveCheck()
        {
            if (!LocalInTaxi) return;
            try
            {
                if (!_taxiProbeResolved)
                {
                    _taxiProbeResolved = true;
                    _pTaxiIsTraveling = VehicleManager.FindGameType("TaxiSystem")?
                        .GetProperty("IsTraveling", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (_pTaxiIsTraveling == null)
                {
                    if (!_taxiProbeMissLogged)
                    {
                        _taxiProbeMissLogged = true;
                        Plugin.Logger.LogWarning("[Taxi] TaxiSystem.IsTraveling not found — a ride that loses its completion event can no longer be closed by the live check.");
                    }
                    return;
                }
                if (_pTaxiIsTraveling.GetValue(null) is not bool traveling) return;   // no TaxiSystem instance yet: decide nothing
                if (traveling) return;
            }
            catch { return; }   // the property throws while the instance is gone — that is not a verdict
            Plugin.Logger.LogWarning("[Taxi] ride ended without its completion event — cleared by the live check.");
            OnTaxiTravelEnd();
        }

        /// <summary>X2 (log-only, H-TAXISTRAND-1): once per ride, everything that decides whether the player can
        /// walk after the warp — where the game aimed them, whether that point is ON the navmesh, where they
        /// actually ended up, and the state of their agent. Budget TaxiTraceBudget lines per session.</summary>
        private static void TraceTaxiArrival()
        {
            try
            {
                if (_taxiTraceLines >= TaxiTraceBudget) return;
                _taxiTraceLines++;
                string name = "?", type = "?";
                var tgt = _taxiTarget;
                try { if (tgt != null) { name = tgt.name; type = tgt.GetType().Name; } } catch { }
                bool haveNav = false; Vector3 nav = Vector3.zero;
                try { if (tgt != null) { nav = tgt.GetNavMeshTargetPosition(); haveNav = true; } } catch { }
                string sample = "no target";
                if (haveNav)
                {
                    try
                    {
                        sample = UnityEngine.AI.NavMesh.SamplePosition(nav, out var hit, 3f, -1)
                            ? $"{hit.position} mask={hit.mask}"
                            : "NO HIT within 3m";
                    }
                    catch (Exception ex) { sample = "threw " + ex.GetType().Name; }
                }
                Vector3 ppos = Vector3.zero; bool agentEnabled = false, onNav = false;
                try
                {
                    var ch = PlayerHelper.PlayerController?.Character;
                    if (ch != null)
                    {
                        ppos = ch.transform.position;
                        var agent = ch.GetComponentInChildren<UnityEngine.AI.NavMeshAgent>(true);
                        if (agent != null) { agentEnabled = agent.enabled; onNav = agent.isOnNavMesh; }
                    }
                }
                catch { }
                Plugin.Logger.LogWarning($"[Taxi] arrival target='{name}'/{type} navTarget={(haveNav ? nav.ToString() : "n/a")} sample={sample}"
                    + $" player={ppos} agentEnabled={agentEnabled} onNavMesh={onNav} instant={_taxiInstantArmed} ({_taxiTraceLines}/{TaxiTraceBudget})");
            }
            catch { }
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

        // ── TRAFFIC-APART: the client half ───────────────────────────────────────────
        //
        // This machine runs EITHER the host's traffic (ghosts) or its own Gley ambient traffic — never both — and the
        // host decides which (P2). The switch is a HANDOVER, not a cut: in each direction the outgoing traffic is
        // kept until the incoming traffic is actually here, and every car that goes, goes off-screen. That is the
        // whole point: crossing the boundary must never pop a street empty.
        internal const string HandoverNone    = "none";
        internal const string HandoverToLocal = "toLocal";
        internal const string HandoverToGhost = "toGhost";
        // TRAFFIC-APART-2 (user 2026-09-16): a hand-over car must not vanish in plain view, so the two deadlines a
        // fade has are NOT the same number any more. Review r1 MAJOR-2 + rig run 1: a car 70 m away is a few pixels,
        // but a car STOPPED in view - queued behind the player, at a red light - never goes off-screen at all (run 1:
        // 18.1 s for the last ghost, and one local car that hit the old 20 s ceiling). Distance still retires those
        // stopped cars; the 90 s in-view cut is the last resort, by which time the player has long since moved on.
        private  const float  HandoverArrivalCeilingSeconds = 20f;   // ghost mode only: stop waiting for the host's ghosts to arrive
        private  const float  HandoverInViewCeilingSeconds  = 90f;   // the residual: a car STILL on screen this long is cut and logged
        private  const float  HandoverRetireDistance = 70f;

        /// <summary>Which traffic this machine runs: "ghost" (the host's) or "local" (its own). Ghost by default
        /// — today's behaviour — and reset to it on leave (P7).</summary>
        public  static string ClientTrafficMode { get; private set; } = ModeGhost;
        /// <summary>True on a CLIENT that is running its OWN ambient traffic. Never true on the host (its traffic is
        /// its own by definition, and every rule this gates is client-side).</summary>
        public  static bool   ClientRunsLocalTraffic => !MPServer.IsRunning && ClientTrafficMode == ModeLocal;
        /// <summary>P9: "none" / "toLocal" / "toGhost" — a fade is running while this is not "none".</summary>
        public  static string ClientHandover => _handover;
        private static int    _modeSeq;
        private static string _handover = HandoverNone;
        private static float  _handoverAt;
        private static bool   _modeDeferLogged;
        private static bool   _localDensityIssued;
        private static bool   _localDensityWaitLogged;
        private static bool   _localDensityUninitLogged;   // MINOR-3: "Gley not initialised yet" is said once per mode entry

        /// <summary>P4: the host names the traffic this client is to run. A repeat (the host's 5 s re-assert) is a
        /// no-op; a verdict that arrives before the world can materialize is NOT recorded, because that re-assert
        /// brings it straight back and there is no traffic to hand over yet.</summary>
        public static void ApplyTrafficMode(TrafficModePayload p)
        {
            try
            {
                if (p == null || MPServer.IsRunning) return;
                string mode = p.Mode == ModeLocal ? ModeLocal : ModeGhost;
                if (mode == ClientTrafficMode && p.Seq == _modeSeq) return;
                if (!MPWorldReady.CanMaterialize)
                {
                    if (!_modeDeferLogged) { _modeDeferLogged = true; Plugin.Logger.LogInfo("[TrafficSync] traffic mode deferred: world not ready"); }
                    return;
                }
                ClientTrafficMode = mode; _modeSeq = p.Seq; _handoverAt = Time.unscaledTime;
                _handover = mode == ModeLocal ? HandoverToLocal : HandoverToGhost;
                _nextClientSimBeat = 0f;                  // the handover starts on this frame's beat, not up to 1 s late
                if (mode == ModeLocal) EnterLocalMode(); else EnterGhostMode();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] ApplyTrafficMode: {ex.Message}"); }
        }

        /// <summary>P5: far from everyone — wake this machine's own traffic brain. The ghosts still here are faded out
        /// by the 1 s beat and only THEN acked, so the host keeps streaming them in the meantime.</summary>
        private static void EnterLocalMode()
        {
            _localDensityIssued = false; _localDensityWaitLogged = false; _localDensityUninitLogged = false;
            Plugin.Logger.LogInfo($"[TrafficSync] traffic mode: local (seq {_modeSeq}) - this client is far from every other player and takes over its own traffic; {_ghosts.Count} host ghost(s) fade out first. Measured here: nearest other player {MeasuredNearestOtherPlayer()}.");
        }

        /// <summary>P6: someone is near again — the host's cars rule here. Acked IMMEDIATELY: in this direction there
        /// is nothing on the host to protect, it simply resumes the stream (and a full lights broadcast). The local
        /// ambient cars are kept until enough ghosts have arrived to replace them.</summary>
        private static void EnterGhostMode()
        {
            var tm = TrafficManager.Instance;
            if (tm != null) { SelfDensityCall = true; try { tm.SetTrafficDensity(0); } catch { } finally { SelfDensityCall = false; } }
            _localDensityIssued = false; _localDensityWaitLogged = false; _localDensityUninitLogged = false;
            Plugin.Logger.LogInfo($"[TrafficSync] traffic mode: ghost (seq {_modeSeq}) - another player is near; the host's traffic takes over and the local cars fade out as its ghosts arrive. Measured here: nearest other player {MeasuredNearestOtherPlayer()}.");
            MPClient.SendTrafficModeAck(ModeGhost, _modeSeq);
        }

        /// <summary>The client's 1 s traffic beat, as a MODE SWITCH (P5/P6): local mode runs this machine's own
        /// traffic, ghost mode holds it at zero density and renders the host's. Order matters: local mode and the
        /// fade back to ghost mode are both handled ABOVE the ClientServiceSimEnabled branch, so a flip fades in
        /// either configuration and neither the zero re-assert nor the legacy kill ever sees an unfinished fade.</summary>
        private static void SuppressLocalTraffic()
        {
            if (!ClientTrafficSuppressionEnabled) return;

            var tm = TrafficManager.Instance;
            if (tm == null) return;

            // MINOR-7 (review 2026-09-02): the density re-assert and the ambient sweep run on a 1 s beat (first pass
            // immediately), not every frame — the density clamp patch is the event-driven source. Both modes share it.
            float now = Time.unscaledTime;
            bool beat = now >= _nextClientSimBeat;
            if (beat) _nextClientSimBeat = now + 1f;

            if (ClientRunsLocalTraffic) { TickLocalMode(tm, now, beat); return; }

            // TRAFFIC-APART P6 / review r1 MAJOR-1: while the handover back to ghost mode runs, this beat FADES the
            // local cars out instead. It sat inside the ClientServiceSimEnabled block, so with that kill-switch off
            // the flip fell straight through to the legacy path below - every local car vanished at once and
            // _handover was never cleared. Both configurations wait behind it now, so the street is never emptied
            // before the host's ghosts have arrived to fill it.
            if (_handover == HandoverToGhost)
            {
                if (!beat) return;
                if (!TickGhostHandover(tm, now)) return;
            }

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
                if (!beat) return;
                SelfDensityCall = true; try { tm.SetTrafficDensity(0); } catch { } finally { SelfDensityCall = false; }   // H-SVC-116: our own 0 is not the game's request
                try { ClearClientTrafficExceptServiceCars(tm); } catch { }
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

        /// <summary>P5: this machine's own traffic runs here. The density is the game's OWN last request (the clamp
        /// patch passes it straight through in this mode and still records it); the budget block in
        /// UpdateTrafficAnchors is host-only, so the density manager's max is set here as well.</summary>
        private static void TickLocalMode(TrafficManager tm, float now, bool beat)
        {
            if (!tm.enabled)
            {
                tm.enabled = true; _clientTrafficKilled = false;
                Plugin.Logger.LogInfo("[TrafficSync] client traffic brain ON at the game's own density - this client runs its OWN ambient traffic (local mode).");
            }
            if (!beat) return;

            if (!_localDensityIssued)
            {
                if (ClientGameDensityRequest >= 0)
                {
                    // NOT under SelfDensityCall: this IS the game's own number going back in, so the clamp patch
                    // recording it again is exactly right.
                    try { tm.SetTrafficDensity(ClientGameDensityRequest); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] local mode: density {ClientGameDensityRequest} refused: {ex.Message}"); }
                    // Review r1 MINOR-4: the host's budget block keeps a pool reserve for service cars (a summon +
                    // an arrival car per player, :1963-1970); this machine carries ONE player, so the reserve is 2.
                    // Same shape as UpdateTrafficAnchors - with no pool read (poolCount 0) the request stands.
                    int poolCount = 0; try { poolCount = tm.trafficVehicles?.GetVehicleList()?.Count ?? 0; } catch { }
                    const int reserve = 2;
                    int cap = poolCount > reserve ? poolCount - reserve : poolCount;
                    bool capped = poolCount > 0 && ClientGameDensityRequest > cap;
                    int issue = capped ? Math.Min(ClientGameDensityRequest, cap) : ClientGameDensityRequest;
                    try { tm.densityManager?.UpdateMaxCars(issue); } catch { }
                    // Review r1 MINOR-3: TrafficManager.SetTrafficDensity is `if (initialized) densityManager
                    // .UpdateMaxCars(...)` (decompile :631-637), so before Gley is initialised the number is dropped
                    // on the floor. The latch waits for that; the next beat re-issues it.
                    if (TrafficManager.IsInitialized)
                    {
                        _localDensityIssued = true;
                        Plugin.Logger.LogInfo($"[TrafficSync] local mode: traffic density {ClientGameDensityRequest} (the game's own last request) is in force here{(capped ? $" (capped by the pool to {issue}: {poolCount} slots − {reserve} reserved)" : "")}.");
                    }
                    else if (!_localDensityUninitLogged)
                    {
                        _localDensityUninitLogged = true;
                        Plugin.Logger.LogInfo($"[TrafficSync] local mode: Gley not initialised yet - density {ClientGameDensityRequest} re-issued on the next beat.");
                    }
                }
                else if (!_localDensityWaitLogged)
                {
                    _localDensityWaitLogged = true;
                    Plugin.Logger.LogInfo("[TrafficSync] local mode: the game has asked for no density yet - retrying on each beat until it does.");
                }
            }

            if (_handover != HandoverToLocal) return;

            // The fade: a ghost goes once it is OFF SCREEN, past the cull ring, or simply farther than
            // HandoverRetireDistance - a car stopped in full view never goes off-screen and held run 1's last ack
            // for 18.1 s - so nothing the player can really see is removed. The ack waits for the last one — that
            // is what stops the host's stream.
            Vector3 me = default; bool haveMe = false;
            var rideT = PassengerRide.RideAnchorTransform();
            if (rideT != null) { me = rideT.position; haveMe = true; }
            else { try { me = PlayerHelper.GetPosition(); haveMe = true; } catch { } }
            bool hard = now - _handoverAt >= HandoverInViewCeilingSeconds;
            var gone = new List<int>(); int forced = 0;
            foreach (var kv in _ghosts)
            {
                var g = kv.Value;
                if (g.Go == null) { gone.Add(kv.Key); continue; }
                float d2   = haveMe ? (g.Go.transform.position - me).sqrMagnitude : 0f;
                bool  far  = haveMe && d2 > GhostCullRadius * GhostCullRadius;
                bool  away = haveMe && d2 > HandoverRetireDistance * HandoverRetireDistance;
                // A ghost keeps the RENDERER test (not the game's per-car visibility flag): a ghost is a clone whose
                // VisibilityScript is never Reset - the one Reset call sits in VehicleComponent.DeactivateVehicle
                // (VehicleComponent.cs:221), i.e. Gley clears the flag as it PUTS A CAR AWAY, so a pooled car comes
                // back out with neverBeenVisible=true, and a mod clone never goes through that path at all - so the
                // flag would claim "in view" before the clone is ever drawn. The renderer flag is the truthful one
                // for a clone.
                if (far || away || !AnyRendererVisible(g.Go)) { gone.Add(kv.Key); continue; }
                if (hard) { gone.Add(kv.Key); forced++; }
            }
            foreach (var k in gone) RetireGhost(k);
            if (_ghosts.Count > 0) return;

            if (forced > 0) Plugin.Logger.LogInfo($"[TrafficSync] traffic mode: local - {forced} ghost(s) still on screen after {HandoverInViewCeilingSeconds:F0} s, removed.");
            else             Plugin.Logger.LogInfo($"[TrafficSync] traffic mode: local (ghosts handed over in {now - _handoverAt:F1} s).");
            _handover = HandoverNone;
            MPClient.SendTrafficModeAck(ModeLocal, _modeSeq);
        }

        /// <summary>P6: the fade back. True once the local ambient cars are gone and the ordinary ghost-mode beat
        /// (density 0 + the sweep) may resume. The local cars are KEPT until the host's ghosts are really here — half
        /// the number the game asks for, capped — and then go as they leave the screen or fall farther than
        /// HandoverRetireDistance away. Same spare list as
        /// ClearClientTrafficExceptServiceCars: a routed car (presetPath) and the mod's service cars stay.</summary>
        private static bool TickGhostHandover(TrafficManager tm, float now)
        {
            bool arrival = now - _handoverAt >= HandoverArrivalCeilingSeconds;
            bool hard    = now - _handoverAt >= HandoverInViewCeilingSeconds;
            // Review r1 MAJOR-2: _ghosts counts what is inside the 160 m cull ring, while the game's density request
            // is a whole-neighbourhood number, so half of it can never arrive and the gate plateaus below it (rig run
            // 1: 13 ghosts for a request of 18). Four ghosts in the ring already means "the host's cars are here".
            int want = ClientGameDensityRequest > 0 ? Math.Min((ClientGameDensityRequest + 1) / 2, 4) : 0;
            if (want > 0 && _ghosts.Count < want && !arrival) return false;

            // The same anchor ApplySnapshot uses: the ride anchor while riding, else the player body.
            Vector3 me = default; bool haveMe = false;
            var rideT = PassengerRide.RideAnchorTransform();
            if (rideT != null) { me = rideT.position; haveMe = true; }
            else { try { me = PlayerHelper.GetPosition(); haveMe = true; } catch { } }

            // Two deadlines, two jobs (TRAFFIC-APART-2). The ARRIVAL gate above still gives up after 20 s of
            // waiting for the host's ghosts. The in-view cut below is now 90 s: before it, a car the player can
            // actually see is KEPT, and only distance or leaving the screen retires it - nothing vanishes in
            // plain view. Fold c (re-check of fold b): the hard cut is still the escape hatch, so past it the
            // handover FINISHES no matter what - a car whose RemoveVehicle throws is counted and logged, never
            // allowed to hold the handover open forever (local traffic beside the host's ghosts, no ack ever
            // sent). Before it a failed removal is simply retried on the next beat. 'forced' counts only the
            // cars that were still in view - a car already off-screen or far away would have gone regardless.
            int left = 0, forced = 0, failed = 0;
            var list = tm.trafficVehicles?.GetVehicleList();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null || !v.gameObject.activeSelf) continue;
                    if (v.presetPath != null) continue;
                    if (ServiceCars.IsClientKept(v.gameObject)) continue;
                    bool away = haveMe && (v.gameObject.transform.position - me).sqrMagnitude > HandoverRetireDistance * HandoverRetireDistance;
                    bool inView = !away && LocalCarInView(v);
                    if (!hard && inView) { left++; continue; }
                    try { tm.RemoveVehicle(v.gameObject); if (hard && inView) forced++; }
                    catch { if (hard) failed++; else left++; }
                }
            if (left > 0) return false;

            if (failed > 0) Plugin.Logger.LogWarning($"[TrafficSync] traffic mode: ghost - {failed} local car(s) could not be removed at the {HandoverInViewCeilingSeconds:F0} s ceiling; handing over anyway.");
            if (forced > 0) Plugin.Logger.LogInfo($"[TrafficSync] traffic mode: ghost - {forced} local car(s) still on screen after {HandoverInViewCeilingSeconds:F0} s, removed.");
            else             Plugin.Logger.LogInfo($"[TrafficSync] traffic mode: ghost (local cars handed over in {now - _handoverAt:F1} s).");
            _handover = HandoverNone;
            return true;
        }

        /// <summary>Is this LOCAL (Gley-owned) car on screen? Uses the game's own per-car flag — the very one Gley's
        /// TrafficManager.Update consults before it may remove a car (decompile TrafficManager.cs:1012 CanBeRemoved →
        /// VehicleComponent.cs:344-348 → VisibilityScript.cs:11-17, driven by OnBecameVisible/OnBecameInvisible) — so
        /// the mod and the game agree on what "in view" means. Falls back to the renderer test when the car has no
        /// visibility script or reading it throws. Ghost clones must NOT use this: Reset is called only from
        /// VehicleComponent.DeactivateVehicle (VehicleComponent.cs:221) - the de-activation path a pooled Gley car
        /// takes on its way back to the pool (so it re-activates with neverBeenVisible=true) and one a mod clone
        /// never takes, leaving its flag meaningless.</summary>
        private static bool LocalCarInView(VehicleComponent v)
        {
            try { return v.visibilityScript != null ? !v.visibilityScript.IsNotInView() : AnyRendererVisible(v.gameObject); }
            catch { return AnyRendererVisible(v.gameObject); }
        }

        /// <summary>True while any renderer of this body is on screen. A body with NO renderers has nothing that can
        /// pop out of view, so it counts as invisible and may go at once. Review r1 MINOR-7: Renderer.isVisible is
        /// true for ANY camera - a reflection probe or a cutscene camera keeps a body "visible" - so this test alone
        /// can pin a car forever; both callers OR it with the HandoverRetireDistance test, which is the cover.</summary>
        private static bool AnyRendererVisible(GameObject? go)
        {
            try
            {
                if (go == null) return false;
                var rs = go.GetComponentsInChildren<Renderer>(true);
                if (rs == null || rs.Length == 0) return false;
                foreach (var r in rs) if (r != null && r.isVisible) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Destroys one traffic ghost through the same release path ApplySnapshot's own culling uses.</summary>
        private static void RetireGhost(int index)
        {
            if (!_ghosts.TryGetValue(index, out var g)) return;
            if (g.Go != null) { try { NotifyCollidersRemoved(g.Go, g.Solids); UnityEngine.Object.Destroy(g.Go); } catch { } }
            _ghosts.Remove(index);
        }

        /// <summary>P9: active AMBIENT Gley cars on this machine — the same filter the client sweep uses below
        /// (active, no preset path, not one of the mod's service cars).</summary>
        public static int LocalAmbientCount()
        {
            int n = 0;
            try
            {
                var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var v = list[i];
                        if (v == null || !v.gameObject.activeSelf) continue;
                        if (v.presetPath != null) continue;
                        if (ServiceCars.IsClientKept(v.gameObject)) continue;
                        n++;
                    }
            }
            catch { }
            return n;
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

        // ══ TRAFFIC-CONSIST T1 (2026-09-18, design Q1 option A) - a client PUBLISHES its leftover cars ══════
        //
        // WHY AT ALL (user ruling): everyone has to exist in the same world, consistent with itself. During the
        // switch-over to ghost mode a client still holds ambient cars of its OWN - cars another player standing
        // next to him cannot see and, worse, that the other machine's thinking traffic drives straight through.
        // Option B ("place the boundary so no leftover is ever visible") is arithmetically impossible (leftovers
        // stay private only past 230 m while the flip fires at 250 m, and a fade may run to 90 s), and option C
        // ("the host adopts the cars") needs a respawn that can fail and a pose jump. So: publish them.
        //
        // THE RULE (open question 2, decided 2026-09-18): ONLY while this client's mode is ghost AND it still
        // holds ambient local cars. A client running local traffic alone is beyond 350 m from everyone - there is
        // nobody to be inconsistent with, and nothing to pay for.
        private const float PublishInterval = BroadcastInterval;   // 0.2 s - the host's own beat (open question 1)
        private static float _publishTimer;
        private static long  _publishSeq;
        private static int   _publishedLast;
        private static bool  _publishLogged;
        /// <summary>T5: the pool indices of the cars the LAST publish beat carried - i.e. this client's current fade
        /// leftovers. Kept as a set so the Gley sensor shield (MPPatches.Patch_GleyVehicle_NREShield, a PHYSICS-path
        /// prefix) can answer "is this one of my own real cars?" with one hash lookup and no allocation, from the
        /// very same list T1 publishes from.</summary>
        private static readonly HashSet<int> _publishedIds = new();
        private static int   _sensorSkips;

        /// <summary>T5: is this Gley car one of this client's fade leftovers (the newest publish beat)? False on the
        /// host, false when nothing is being published.</summary>
        internal static bool IsPublishedLeftover(VehicleComponent? v)
        {
            try { return v != null && _publishedIds.Count > 0 && _publishedIds.Contains(v.GetIndex()); }
            catch { return false; }
        }

        /// <summary>T5: the sensor shield skipped one real handler call. Counter only - it runs on the physics path
        /// and must never log; the 30 s census prints and resets it.</summary>
        internal static void CountSensorSkip() { _sensorSkips++; }
        /// <summary>T4 lever: how many leftover cars this client put on the wire on its last publish beat.</summary>
        public static int PublishedLeftoverCount => _publishedLast;

        /// <summary>T1 step 1: the client's publish beat. The filter is the FADE'S OWN leftover filter (active, no
        /// preset path, not one of the mod's service cars - the same three tests TickGhostHandover and
        /// ClearClientTrafficExceptServiceCars use), so the rows are exactly the cars that are fading out. Identity
        /// (model + colours) rides on every row: there are at most a couple of dozen and only for the length of a
        /// fade, and it lets the host - and every peer the host relays to - spawn one on first sight.</summary>
        private static void PublishLeftovers()
        {
            try
            {
                if (MPServer.IsRunning || !MPClient.IsConnected) { _publishedLast = 0; _publishedIds.Clear(); return; }
                if (ClientTrafficMode != ModeGhost) { _publishedLast = 0; _publishedIds.Clear(); return; }
                var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
                if (list == null) { _publishedLast = 0; _publishedIds.Clear(); return; }
                _publishedIds.Clear();   // T5: rebuilt every beat, so the shield's exemption can never outlive a car
                var payload = new ClientTrafficSnapshotPayload { T = Time.unscaledTime, Seq = ++_publishSeq };
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null) continue;
                    var go = v.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    if (v.presetPath != null) continue;
                    if (ServiceCars.IsClientKept(go)) continue;
                    int index = v.GetIndex();
                    _publishedIds.Add(index);
                    var t = v.transform;
                    var pos = t.position; var rot = t.rotation;
                    Vector3 vel = default;
                    try { vel = v.GetVelocity(); } catch { }
                    // Same identity cache the host's BuildMaster uses: a recycle always teleports, so a small move
                    // means the same live car and the model/paint read (an IL2CPP string + a material scan) is skipped.
                    string model; List<float> colors;
                    if (_carColors.TryGetValue(index, out var cc) && (pos - cc.Pos).sqrMagnitude < SnapDistance * SnapDistance)
                    { model = cc.Model; colors = cc.Colors; cc.Pos = pos; }
                    else
                    {
                        model = StripCloneSuffix(go.name);
                        colors = ReadBodyColors(index, go);
                        _carColors[index] = new CarColorEntry { Model = model, Pos = pos, Colors = colors };
                    }
                    payload.Cars.Add(new TrafficCarDto
                    {
                        Index = index, Model = model, Colors = colors,
                        X = Mathf.RoundToInt(pos.x * 100f), Y = Mathf.RoundToInt(pos.y * 100f), Z = Mathf.RoundToInt(pos.z * 100f),
                        Qx = Mathf.RoundToInt(rot.x * 10000f), Qy = Mathf.RoundToInt(rot.y * 10000f),
                        Qz = Mathf.RoundToInt(rot.z * 10000f), Qw = Mathf.RoundToInt(rot.w * 10000f),
                        Vx = Mathf.RoundToInt(vel.x * 100f), Vy = Mathf.RoundToInt(vel.y * 100f), Vz = Mathf.RoundToInt(vel.z * 100f),
                        // T3 P3: the machine that RUNS the car is the one that can read its driving action, and for
                        // a published leftover that machine is this one - so the flag is set here, by the same test
                        // the host applies to its own rows.
                        St = StoppedFlag(index, vel),
                    });
                }
                _publishedLast = payload.Cars.Count;
                if (_publishedLast == 0) return;
                MPClient.SendClientTrafficSnapshot(payload);
                if (!_publishLogged)
                {
                    _publishLogged = true;
                    Plugin.Logger.LogInfo($"[TrafficSync] publishing {_publishedLast} leftover local car(s) to the host at {1f / PublishInterval:0} Hz while this client is in ghost mode - the host stands them in for its own traffic to brake for and relays them to every other player. Counted from here on by the 30 s [TrafficCensus] line ('published').");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] PublishLeftovers: {ex.Message}"); }
        }

        // ── T1 steps 2-4: the HOST's side of the published leftovers ──────────────────────────────────────
        /// <summary>Reserved relay id base (design Q1 step 3). The host's own ids are Gley POOL INDICES, bounded by
        /// the pool size (a few hundred), so ids from 1,000,000 up can never collide with one; each owner gets a
        /// 10,000-wide block, wider than any pool.</summary>
        private const int   ForeignIdBase   = 1000000;
        private const int   ForeignIdStride = 10000;
        /// <summary>Design failure case 1: all of an owner's rows drop after this long without a publish (and at
        /// once on ForgetPeer), so a client wedged mid-fade - or simply gone - cannot leave cars standing.</summary>
        private const float ForeignSilenceSeconds = 2f;

        private sealed class ForeignTraffic
        {
            public int   Ordinal;
            public long  Seq;
            public float LastAt;
            public float SinceAt;                                  // first publish of the current episode (census age)
            public readonly List<TrafficCarDto> Rows = new();      // the NEWEST publish, already carrying relay ids
            public readonly HashSet<int> StandIns = new();         // relay ids that have a stand-in in _ghosts
        }
        private static readonly Dictionary<string, ForeignTraffic> _foreign = new();
        private static readonly HashSet<string> _noPeers = new();   // the empty live-set for the server-stopped sweep
        private static int _foreignNextOrdinal;
        private static int _foreignDropLogs;

        /// <summary>T4 lever: stand-ins this host keeps for other players' published cars.</summary>
        public static int HostStandInCount { get { int n = 0; foreach (var f in _foreign.Values) n += f.StandIns.Count; return n; } }
        /// <summary>T4 lever: rows of other players' leftovers this host is relaying right now.</summary>
        public static int HostReceivedLeftoverCount { get { int n = 0; foreach (var f in _foreign.Values) n += f.Rows.Count; return n; } }
        /// <summary>T4 lever: cars in this machine's ghost table that are somebody ELSE'S leftovers (reserved ids) -
        /// the host's stand-ins, or, on a client, the rows the host relayed here.</summary>
        public static int ForeignGhostCount { get { int n = 0; foreach (var k in _ghosts.Keys) if (k >= ForeignIdBase) n++; return n; } }

        /// <summary>T1 steps 2-4 (host): take one client's published leftovers, keep a SENSED stand-in per car so
        /// this machine's own thinking traffic brakes for them, and hold the rows for the relay in BroadcastPerPeer.
        /// Seq-guarded exactly like the host's own stream (the publish rides the unreliable lane, so a late packet
        /// must never win newest-wins; a far-lower Seq is a new stream after a rejoin, not a stale packet). Main
        /// thread only - it spawns and moves objects.</summary>
        public static void HostOnClientTrafficSnapshot(string pid, ClientTrafficSnapshotPayload p)
        {
            if (!MPServer.IsRunning || p == null || string.IsNullOrEmpty(pid)) return;
            try
            {
                if (!_foreign.TryGetValue(pid, out var ft))
                {
                    _foreign[pid] = ft = new ForeignTraffic { Ordinal = ++_foreignNextOrdinal, SinceAt = Time.unscaledTime };
                    Plugin.Logger.LogInfo($"[TrafficSync] '{pid}' is publishing its leftover local cars (ordinal {ft.Ordinal}): they get stand-ins here and ride this host's snapshots to every OTHER player under ids from {ForeignIdBase + ForeignIdStride * ft.Ordinal}.");
                }
                if (p.Seq > 0 && p.Seq <= ft.Seq && ft.Seq - p.Seq <= 100) return;
                if (ft.Rows.Count == 0 && ft.StandIns.Count == 0) ft.SinceAt = Time.unscaledTime;
                ft.Seq = p.Seq; ft.LastAt = Time.unscaledTime;
                ft.Rows.Clear();
                var live = new HashSet<int>();
                if (p.Cars != null)
                    foreach (var c in p.Cars)
                    {
                        if (c == null) continue;
                        int id = ForeignIdBase + ForeignIdStride * ft.Ordinal + c.Index;
                        var row = new TrafficCarDto
                        {
                            Index = id, Model = c.Model, Colors = c.Colors,
                            X = c.X, Y = c.Y, Z = c.Z, Qx = c.Qx, Qy = c.Qy, Qz = c.Qz, Qw = c.Qw,
                            Vx = c.Vx, Vy = c.Vy, Vz = c.Vz, St = c.St,
                        };
                        ft.Rows.Add(row);
                        live.Add(id);
                        ApplyStandIn(ft, row);
                    }
                // A car ABSENT from the newest publish is gone on its owner's machine - the same rule ApplySnapshot's
                // absent sweep applies to the host's own cars, so a leftover dies here the moment it dies there.
                DropStandIns(ft, live);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] client traffic from '{pid}': {ex.Message}"); }
        }

        /// <summary>T1 step 2: one stand-in per published car - the SAME stripped clone the traffic ghosts and the
        /// service look-alikes use (CloneStrippedPrefab + RelayerCollidersToServiceLayer), stored as an ordinary
        /// entry in the ghost table so the SAME interpolation drives it. It is a visible PROP, never a Gley car:
        /// the host's density budget and its pool are untouched (design failure case 3), and because it is visible
        /// the host's own player - who is just "another player" to the client in fade - sees those cars at all.
        /// An unknown model gets NO stand-in, and its row is still relayed (design failure case 2).
        /// The stamps are the host's OWN arrival times, not the publisher's clock: the two machines share no clock,
        /// and the arrival beat IS the sample cadence, so the buffered interpolation works with offset 0.</summary>
        private static void ApplyStandIn(ForeignTraffic ft, TrafficCarDto row)
        {
            try
            {
                string model = row.Model ?? "";
                var pos = new Vector3(row.X * 0.01f, row.Y * 0.01f, row.Z * 0.01f);
                var rot = new Quaternion(row.Qx * 0.0001f, row.Qy * 0.0001f, row.Qz * 0.0001f, row.Qw * 0.0001f);
                if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f)
                    rot = _ghosts.TryGetValue(row.Index, out var lastRot) ? lastRot.TargetRot : Quaternion.identity;
                float now = Time.unscaledTime;
                _ghosts.TryGetValue(row.Index, out var g);
                // A pool slot reused for a different car on the owner's machine: same two tells as ApplySnapshot.
                if (g != null && g.Go != null && (g.Model != model || Vector3.Distance(g.Go.transform.position, pos) > SnapDistance))
                {
                    try { NotifyCollidersRemoved(g.Go, g.Solids); UnityEngine.Object.Destroy(g.Go); } catch { }
                    _ghosts.Remove(row.Index); ft.StandIns.Remove(row.Index); g = null;
                }
                if (g == null || g.Go == null)
                {
                    if (model.Length == 0) return;
                    BuildPrefabMap();
                    GameObject? prefab = null;
                    _trafficPrefabs?.TryGetValue(model, out prefab);
                    if (prefab == null) return;                       // failure case 2: no stand-in, row still relayed
                    var body = CloneStrippedPrefab(prefab, model, pos, rot);
                    if (body == null) return;
                    RelayerCollidersToServiceLayer(body);             // unconditional here: this host really runs Gley
                    g = new TrafficGhost { Go = body, Model = model, TargetPos = pos, TargetRot = rot, TargetAt = now, HostT = now };
                    g.Body = body.GetComponent<Rigidbody>();
                    try { var all = body.GetComponentsInChildren<Collider>(true); var sol = new List<Collider>(all.Length); foreach (var c in all) if (c != null && !c.isTrigger) sol.Add(c); g.Solids = sol.ToArray(); } catch { }
                    _ghosts[row.Index] = g;
                    ft.StandIns.Add(row.Index);
                    // The host's own clock IS the stamp clock for stand-ins, so the buffered interpolation needs no
                    // estimate (offset 0). The host never runs ApplySnapshot, which is the only other writer.
                    _clockOffset = 0f; _haveClockOffset = true;
                }
                else
                {
                    g.PrevPos = g.TargetPos; g.PrevRot = g.TargetRot; g.PrevVel = g.Velocity;
                    g.PrevHostT = g.HostT; g.HasPrevVel = g.HasVel; g.HasPrev = true;
                    float hdt = now - g.HostT;
                    if (row.Vx != 0 || row.Vy != 0 || row.Vz != 0)
                    { g.Velocity = new Vector3(row.Vx * 0.01f, row.Vy * 0.01f, row.Vz * 0.01f); g.HasVel = true; }
                    else if (hdt > 0.005f) { g.Velocity = (pos - g.TargetPos) / hdt; g.HasVel = true; }
                    g.TargetPos = pos; g.TargetRot = rot; g.TargetAt = now; g.HostT = now;
                }
                g.Stopped = row.St != 0;
                if (g.Go != null && row.Colors != null && row.Colors.Count >= 6 && !SameColors(g.LastColors, row.Colors))
                {
                    ApplyVehicleBodyColors(g.Go, model, row.Colors);
                    g.LastColors = row.Colors;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] stand-in for a published car: {ex.Message}"); }
        }

        /// <summary>Retires an owner's stand-ins: all of them (live == null) or every one the newest publish no
        /// longer names. Goes through the same release path every other ghost teardown uses, so a host car braking
        /// for one never keeps a dead collider (review BLOCKER-2 / design failure case 4).</summary>
        private static void DropStandIns(ForeignTraffic ft, HashSet<int>? live)
        {
            List<int>? gone = null;
            foreach (var id in ft.StandIns) if (live == null || !live.Contains(id)) (gone ??= new List<int>()).Add(id);
            if (live == null) ft.Rows.Clear();
            if (gone == null) return;
            foreach (var id in gone) { RetireGhost(id); ft.StandIns.Remove(id); }
        }

        /// <summary>T1 step 4: an owner that has gone quiet for ForeignSilenceSeconds - or that is no longer a
        /// connected peer - loses every row and every stand-in at once. Runs on the host's own 0.2 s beat.</summary>
        private static void SweepForeignTraffic(float now, HashSet<string> livePids)
        {
            if (_foreign.Count == 0) return;
            List<string>? drop = null;
            foreach (var kv in _foreign)
                if (!livePids.Contains(kv.Key) || now - kv.Value.LastAt > ForeignSilenceSeconds)
                    (drop ??= new List<string>()).Add(kv.Key);
            if (drop == null) return;
            foreach (var pid in drop)
            {
                if (!_foreign.TryGetValue(pid, out var ft)) continue;
                bool live = livePids.Contains(pid);
                int rows = ft.Rows.Count, n = ft.StandIns.Count;
                DropStandIns(ft, null);
                _foreign.Remove(pid);
                if ((rows > 0 || n > 0) && _foreignDropLogs++ < 10)
                    Plugin.Logger.LogInfo($"[TrafficSync] '{pid}' stopped publishing leftovers ({(live ? $"{ForeignSilenceSeconds:0} s of silence" : "peer gone")}): {rows} relayed row(s) and {n} stand-in(s) dropped.");
            }
        }

        // ══ TRAFFIC-CONSIST T2 (design Q3) - player-vehicle ghosts are SENSED ══════════════════════════════
        internal const string SenseProxyName = "BAMP_TrafficSense";
        private  const string SenseProxyTag  = "AiVehicleHalt";
        private static readonly List<GameObject> _senseProxyPending = new();
        private static float _senseProxyRetryAt;
        private static int   _senseProxyLogs;
        private static bool  _senseProxyDeferLogged;

        /// <summary>The ghost's sense proxy, wherever it hangs (it is parented to the body collider when there is
        /// one, so a root-level Find would miss it).</summary>
        private static Transform? FindSenseProxy(GameObject go)
        {
            try
            {
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.gameObject.name == SenseProxyName) return t;
            }
            catch { }
            return null;
        }

        /// <summary>T2 (design Q3, I2): gives one player-vehicle ghost the child that Gley's sensors see. ONE
        /// mechanism on EVERY machine, unconditionally - the host always runs thinking traffic and a client runs
        /// one in either mode (its own service cars drive natively at zero ambient density) - and doing it once at
        /// spawn avoids re-layering churn at every mode flip.
        /// WHY A CHILD, never the root: EntityController re-asserts the ROOT layer in Awake and Show()
        /// (EntityController.cs:83-85, :162) and VehicleController rewrites renderer layers on enter/exit (:338,
        /// :345, :389), so a relayered root is undone on any ghost that KEEPS its controller (a granted, drivable
        /// one). Nothing native touches a child's layer.
        /// WHY A TRIGGER: Gley ignores a trigger collider UNLESS it carries the tag AiVehicleHalt
        /// (VehicleComponent.cs:419, :471) - the game's own tag (DriveInEntranceEnterTrigger.cs:16) - and the
        /// playerLayers branch it then takes never dereferences attachedRigidbody (:451-457), so the proxy needs no
        /// rigidbody and adds no solid physics against the local player's own car.
        /// TEARDOWN: every ghost destroy path goes through NotifyCollidersRemoved, which already walks triggers
        /// carrying this tag - without that a braking car would hold a dead collider forever (failure case 4).</summary>
        internal static void AttachTrafficSenseProxy(GameObject? go)
        {
            if (go == null) return;
            try
            {
                if (FindSenseProxy(go) != null) return;              // a rebuilt ghost keeps the one it has
                int layer = ServiceColliderLayer();
                if (layer < 0)
                {
                    // A gated action that defers RETRIES on the next beat, and says so.
                    if (!_senseProxyPending.Contains(go)) _senseProxyPending.Add(go);
                    if (!_senseProxyDeferLogged)
                    {
                        _senseProxyDeferLogged = true;
                        Plugin.Logger.LogInfo("[TrafficSync] traffic-sense proxy deferred: the sensed layer is not resolvable yet (LayerSetupData not loaded). Retried on every traffic beat.");
                    }
                    return;
                }

                Transform parent = go.transform;
                Vector3 center, size;
                if (VehicleHelper.TryGetBodyColliderBounds(go.transform, out var lb, out var bodyCol) && bodyCol != null)
                {
                    parent = bodyCol.transform;     // the bounds the game hands back are in THAT transform's space
                    center = lb.center; size = lb.size;
                }
                else
                {
                    // Fallback for a body this ghost does not have in the native shape: the union of its own solid
                    // colliders, brought into the root's space. Axis-aligned, so approximate - but a sense volume
                    // only has to be about the size of the car.
                    Bounds? wb = null;
                    foreach (var c in go.GetComponentsInChildren<Collider>(true))
                    {
                        if (c == null || c.isTrigger) continue;
                        if (wb == null) wb = c.bounds;
                        else { var b = wb.Value; b.Encapsulate(c.bounds); wb = b; }
                    }
                    if (wb == null) return;         // nothing to size a proxy from
                    var w = wb.Value;
                    var ls = go.transform.lossyScale;
                    center = go.transform.InverseTransformPoint(w.center);
                    size = new Vector3(w.size.x / Mathf.Max(0.0001f, Mathf.Abs(ls.x)),
                                       w.size.y / Mathf.Max(0.0001f, Mathf.Abs(ls.y)),
                                       w.size.z / Mathf.Max(0.0001f, Mathf.Abs(ls.z)));
                }

                var child = new GameObject(SenseProxyName);
                child.transform.SetParent(parent, false);
                child.transform.localPosition = center;
                child.transform.localRotation = Quaternion.identity;
                child.transform.localScale    = Vector3.one;
                child.layer = layer;
                try { child.tag = SenseProxyTag; }
                catch (Exception ex)
                {
                    try { UnityEngine.Object.Destroy(child); } catch { }
                    Plugin.Logger.LogWarning($"[TrafficSync] traffic-sense proxy: the tag '{SenseProxyTag}' is not defined in this project, so player-vehicle ghosts stay invisible to traffic ({ex.Message}).");
                    return;
                }
                var box = child.AddComponent<BoxCollider>();
                box.size = size;
                box.isTrigger = true;
                if (_senseProxyLogs++ < 2)
                    Plugin.Logger.LogInfo($"[TrafficSync] player-vehicle ghost '{go.name}': sense proxy '{SenseProxyName}' added ({size.x:F1}x{size.z:F1} m, trigger tagged '{SenseProxyTag}', layer '{LayerMask.LayerToName(layer)}') - thinking traffic brakes for this ghost now.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficSync] traffic-sense proxy: {ex.Message}"); }
        }

        /// <summary>T4 census: does this ghost carry a live proxy on the sensed layer?</summary>
        internal static bool HasTrafficSenseProxy(GameObject? go)
        {
            try
            {
                if (go == null) return false;
                var t = FindSenseProxy(go);
                if (t == null) return false;
                int layer = ServiceColliderLayer();
                return layer >= 0 && t.gameObject.layer == layer;
            }
            catch { return false; }
        }

        /// <summary>T2: ghosts whose proxy could not be built yet (the sensed layer was unreadable) retry on the
        /// traffic beat and say when they land. Dead entries fall out; a still-unready one re-queues itself.</summary>
        private static void TickSenseProxyRetry()
        {
            if (_senseProxyPending.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _senseProxyRetryAt) return;
            _senseProxyRetryAt = now + 1f;
            var pending = _senseProxyPending.ToArray();
            _senseProxyPending.Clear();
            int done = 0;
            foreach (var go in pending)
            {
                if (go == null) continue;
                AttachTrafficSenseProxy(go);
                if (FindSenseProxy(go) != null) done++;
            }
            if (done > 0) Plugin.Logger.LogInfo($"[TrafficSync] traffic-sense proxy: {done} deferred player-vehicle ghost(s) are sensed now.");
        }

        // ══ TRAFFIC-CONSIST T4 - the census (design section 6; LOG ONLY) ══════════════════════════════════
        private const float CensusInterval = 30f;
        private static float _nextCensusAt;
        private static int   _gleyTriggerHits;
        private static bool  _triggerHitWarned;
        private static readonly List<Vector3> _censusScratch = new();

        /// <summary>T4: counter only, called from the VehicleComponent.OnTriggerEnter postfix. Counts a sensor hit
        /// whose other collider sits under a ModGhostMarker parent - i.e. one of the mod's ghost bodies. Runs per
        /// trigger event, so it never logs; the 30 s census prints and resets it.</summary>
        internal static void CountGhostTriggerHit(Component? other)
        {
            try { if (other != null && other.GetComponentInParent<ModGhostMarker>() != null) _gleyTriggerHits++; }
            catch { }
        }

        /// <summary>A once-only warning for a per-event patch body, so a broken hook is still named without
        /// spamming a line per frame.</summary>
        internal static void WarnOnce(string patch, Exception ex)
        {
            if (_triggerHitWarned) return;
            _triggerHitWarned = true;
            Plugin.Logger.LogWarning($"[TrafficSync] {patch}: {ex.Message} (further occurrences are silent).");
        }

        /// <summary>T4 (design section 6): ONE line every 30 s on BOTH sides, log-only. It answers I1/I2/I3 in
        /// order: how many leftovers are published/relayed and stood in for, whether every player-vehicle ghost is
        /// really sensed (pvGhostsSensed == pvGhosts), and what the prediction did (dead-reckon and rewind extremes
        /// over the window, and how many ghost pairs are overlapping). The pair count is computed ONCE here, never
        /// per frame. The counters describe one window and are reset as it is printed.</summary>
        private static void TickTrafficCensus()
        {
            float now = Time.unscaledTime;
            if (_nextCensusAt <= 0f) { _nextCensusAt = now + CensusInterval; return; }
            if (now < _nextCensusAt) return;
            _nextCensusAt = now + CensusInterval;
            try
            {
                bool host = MPServer.IsRunning;
                int sensedLayer = ServiceColliderLayer();
                int onLayer = 0;
                _censusScratch.Clear();
                foreach (var g in _ghosts.Values)
                {
                    if (g?.Go == null) continue;
                    if (sensedLayer >= 0 && g.Go.layer == sensedLayer) onLayer++;
                    _censusScratch.Add(g.Go.transform.position);
                }
                // Ghost pairs closer than 2.5 m - the shape I3 leaves behind. <= ~40 ghosts, so <= ~800 pairs, once
                // every 30 s.
                int pairs = 0;
                for (int i = 0; i < _censusScratch.Count; i++)
                    for (int j = i + 1; j < _censusScratch.Count; j++)
                        if ((_censusScratch[i] - _censusScratch[j]).sqrMagnitude < 6.25f) pairs++;
                _censusScratch.Clear();

                int localAmbient = LocalAmbientCount();
                float leftoverAge = 0f;
                if (host) { foreach (var f in _foreign.Values) if (f.Rows.Count > 0) leftoverAge = Mathf.Max(leftoverAge, now - f.SinceAt); }
                else if (_handover == HandoverToGhost && localAmbient > 0) leftoverAge = now - _handoverAt;

                string side = host
                    ? $"receivedLeftovers={HostReceivedLeftoverCount} standIns={HostStandInCount}"
                    : $"published={_publishedLast} sensorSkipped={_sensorSkips}";
                Plugin.Logger.LogInfo(
                    $"[TrafficCensus] role={(host ? "host" : "client")} mode={(host ? "host" : ClientTrafficMode)} handover={(host ? HandoverNone : _handover)} " +
                    $"ghosts={_ghosts.Count} ghostsOnSensedLayer={onLayer} pvGhosts={VehicleManager.RemoteVehicleCount} " +
                    $"pvGhostsSensed={VehicleManager.SensedGhostCount()} localAmbient={localAmbient} oldestLeftoverAge={leftoverAge:F1} " +
                    $"{side} gleyTriggerHitsFromPlayerVehicleGhosts={_gleyTriggerHits} overlappingReplicaPairs={pairs} " +
                    $"deadReckonFrames={_deadReckonFrames} maxDeadReckonMetres={_maxDeadReckonMetres:F2} " +
                    $"rewindFrames={_rewindFrames} maxRewindMetres={_maxRewindMetres:F2}");
                _gleyTriggerHits = 0; _sensorSkips = 0; _deadReckonFrames = 0; _rewindFrames = 0;
                _maxDeadReckonMetres = 0f; _maxRewindMetres = 0f;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TrafficCensus] {ex.Message}"); }
        }

        /// <summary>T4: what THIS machine measures to the nearest other player, printed on the flip lines beside the
        /// host's own number - a divergence between the two is what a wrong flip would look like. Every other player
        /// is remote from a client, so the same anchor rules apply (a rider is judged by the car it rides).</summary>
        private static string MeasuredNearestOtherPlayer()
        {
            try
            {
                if (!LocalAnchorPosition(out var me)) return "unknown";
                float best = float.PositiveInfinity;
                foreach (var pid in RemotePlayerManager.GetRemotePlayerIds())
                {
                    if (string.IsNullOrEmpty(pid)) continue;
                    if (TryGetPlayerAnchorPosition(pid, out var op)) best = Mathf.Min(best, Vector3.Distance(me, op));
                }
                return float.IsPositiveInfinity(best) ? "unknown" : $"{best:F0} m";
            }
            catch { return "unknown"; }
        }

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
