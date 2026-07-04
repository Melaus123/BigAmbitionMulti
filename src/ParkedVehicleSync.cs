using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Helpers;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Backlog #3 — parked-vehicle sync.
    ///
    /// Capture path: Harmony Postfix on Helpers.ParkingSimulator.RequestParkedVehicle
    /// / Prefix on ReleaseParkedVehicle add/remove the GameObject in
    /// `_hostTracked` keyed by GameObject.GetInstanceID().
    ///
    /// Broadcast policy (efficient):
    ///   - DIFF (Cars=adds, RemovedKeys=removes, IsFullSnapshot=false):
    ///     broadcast at most every 1s, ONLY if `_pendingAdds` or
    ///     `_pendingRemoves` is non-empty.  Steady-state when nothing changes
    ///     → no broadcast at all.
    ///   - FULL (IsFullSnapshot=true): broadcast every 30s for resync and to
    ///     cover any client that joined mid-session.  ~7KB/30s = ~230 B/s avg.
    ///
    /// Client state is split:
    ///   - `_clientKnown`: full metadata of every car the host has told us
    ///     about.  ~2k entries × ~80 bytes ≈ 160 KB.  Cheap.
    ///   - `_clientGhosts`: only the ghost GameObjects currently instantiated,
    ///     bounded by spatial culling (~30–50 at any moment).
    /// A 0.5s culling pass spawns ghosts that came within view-radius and
    /// releases ghosts that left (with a hysteresis buffer to avoid flicker).
    /// </summary>
    public static class ParkedVehicleSync
    {
        // ── Tuning ───────────────────────────────────────────────────────────
        private const float DiffInterval         = 1f;       // host: max 1 diff per second
        private const float FullSnapshotInterval = 30f;      // host: full resync cadence
        private const float CullInterval         = 0.5f;     // client: cull pass cadence

        // Client render radii.  A player only ever SEES the parked cars on the
        // street around them (~20-40 at once), so a 300 m view spawned hundreds of
        // pointless ghosts.  Tightened to roughly one block + margin.
        private const float ViewRadius = 95f;
        private const float CullRadius = 125f;  // hysteresis — drop only when farther
        private static readonly float ViewRadiusSq = ViewRadius * ViewRadius;
        private static readonly float CullRadiusSq = CullRadius * CullRadius;

        // Host send radius.  The host tracks EVERY parked car the game spawns
        // (thousands citywide); broadcasting them all is a huge, pointless burden.
        // Only cars within this radius of SOME player (host or a connected client)
        // can ever be rendered, so only those are sent.  A bit larger than the
        // client view radius for movement margin between diffs.
        private const float HostSendRadius = 160f;
        private static readonly float HostSendRadiusSq = HostSendRadius * HostSendRadius;

        // Unscaled "world settled" gate — replaces Time.timeSinceLevelLoad (which is
        // SCALED and stalls under the startup-freeze timeScale=0, delaying parked
        // broadcasts until after the unfreeze).  Counts real seconds since the scene
        // loaded so the host streams parked cars DURING the frozen hold.
        private static float _levelUnscaled;
        private const float SettleSeconds = 2.5f;

        // ── Host capture ─────────────────────────────────────────────────────
        private static readonly Dictionary<long, GameObject> _hostTracked = new();
        // Pending diff queues — populated by HostOnRequest/HostOnRelease and
        // drained when we broadcast a diff.  Adds vs removes are mutually
        // exclusive on flush.
        private static readonly HashSet<long> _pendingAdds    = new();
        private static readonly HashSet<long> _pendingRemoves = new();
        private static float _diffTimer;
        private static float _fullTimer;
        private static float _diagTimer;
        private static float _displacementTimer;
        // Per-peer instant resync: the position we last sent each client a parked
        // snapshot at.  When a client moves more than ResyncMoveDist from it
        // (teleport, fast-travel, or BA's large interior<->street teleport on
        // building enter/exit), resend immediately instead of making them wait up
        // to FullSnapshotInterval (30s) for the next full broadcast.  Concurrent:
        // written on the host tick (main) and entries dropped from network handlers.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Vector3> _lastSentPosByPlayer = new();
        private const float ResyncMoveDistSq = 60f * 60f;

        // ── Client state ─────────────────────────────────────────────────────
        // ALL metadata the host has told us about (regardless of distance).
        private static readonly Dictionary<long, ParkedVehicleDto> _clientKnown = new();
        // Only instantiated ghosts (subset, bounded by ViewRadius).
        private static readonly Dictionary<long, GameObject> _clientGhosts = new();
        private static float _cullTimer;

        // Reflection-cached pool entry points.
        private static MethodInfo? _miRequest;
        private static MethodInfo? _miRelease;

        // CLAUDE-DIAGNOSTIC — F6 toggle for the entry-bug investigation.
        // Default ON.  Flipping OFF lets the client's ParkingLaneGenerator
        // run as normal — used to test whether parked-vehicle suppression
        // is the upstream cause of DelayedEnterBuildingActions not firing.
        public static bool SpawnSuppressionEnabled { get; set; } = true;

        public static void ToggleSpawnSuppression()
        {
            SpawnSuppressionEnabled = !SpawnSuppressionEnabled;
            Plugin.Logger.LogInfo($"[ClientFix] Parked-vehicle spawn suppression → {SpawnSuppressionEnabled}");
        }

        // CLAUDE-DIAGNOSTIC — F5 toggle: disable the ENTIRE client-side
        // parked-vehicle sync.  When false, ApplySnapshot is a no-op AND
        // CullingPass is a no-op; toggling off ALSO releases all existing
        // ghosts back to the pool.  Lets us test whether the act of
        // renting from ParkingSimulator's pool (or having ghost vehicles
        // in the world) is what breaks the client's building-entry chain
        // after the host joins.
        public static bool ClientApplyEnabled { get; set; } = true;

        public static void ToggleClientApply()
        {
            ClientApplyEnabled = !ClientApplyEnabled;
            if (!ClientApplyEnabled) ReleaseAllGhosts();
            else Plugin.Logger.LogInfo("[ClientFix] Client parked-vehicle sync ENABLED — will rehydrate on next full host snapshot.");
        }

        public static void ReleaseAllGhosts()
        {
            int releasedCount = 0;
            foreach (var g in _clientGhosts.Values)
            {
                if (g != null)
                {
                    try
                    {
                        ClearAllPropertyBlocks(g);
                        var rr = g.GetComponent<RandomVehicleColor>();
                        if (rr != null) rr.enabled = true;   // restore the roller for native pool reuse (round-24)
                        CallPoolRelease(g);
                        releasedCount++;
                    }
                    catch { }
                }
            }
            _clientGhosts.Clear();
            _clientKnown.Clear();
            Plugin.Logger.LogInfo($"[ClientFix] Client parked-vehicle sync — released {releasedCount} ghost(s) back to pool, cleared known set.");
        }

        /// <summary>Host-side count of currently-tracked parked cars — for the startup
        /// "world is populated" gate (don't unpause onto empty streets).</summary>
        public static int HostTrackedCount => _hostTracked.Count;

        /// <summary>Client-side count of spawned parked ghosts — load diagnostics.</summary>
        public static int ClientGhostCount => _clientGhosts.Count;

        public static void Reset()
        {
            _hostTracked.Clear();
            _pendingAdds.Clear();
            _pendingRemoves.Clear();
            _diffTimer = 0f;
            _fullTimer = 0f;
            _diagTimer = 0f;
            _displacementTimer = 0f;
            _lastSentPosByPlayer.Clear();
            _cullTimer = 0f;
            _levelUnscaled = 0f;

            // Release any leftover client ghosts cleanly back to the pool.
            foreach (var g in _clientGhosts.Values)
            {
                if (g != null) { try { CallPoolRelease(g); } catch { } }
            }
            _clientGhosts.Clear();
            _clientKnown.Clear();
        }

        // Per-frame entry point — called from MPCanvasUI.TickPositionSync.
        public static void Tick()
        {
            try
            {
                if (SaveGameManager.Current == null) return;
                // Real-time settle gate (advances even while the world is frozen at
                // timeScale=0) so the host begins streaming parked cars during the
                // startup hold instead of only after the unfreeze.
                _levelUnscaled += Time.unscaledDeltaTime;
                if (_levelUnscaled < SettleSeconds) return;

                if (MPServer.IsRunning)
                {
                    TickHost();
                }
                else if (MPClient.IsConnected)
                {
                    TickClient();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] Tick: {ex.Message}");
            }
        }

        // ── HOST broadcast scheduling ────────────────────────────────────────

        private static void TickHost()
        {
            float dt = Time.unscaledDeltaTime;
            _diffTimer += dt;
            _fullTimer += dt;
            _diagTimer += dt;

            // Diff first: most frequent, smallest, only when something happened.
            if (_diffTimer >= DiffInterval &&
                (_pendingAdds.Count > 0 || _pendingRemoves.Count > 0))
            {
                _diffTimer = 0f;
                MPServer.BroadcastParkedSnapshot(BuildDiffSnapshot());
            }

            // Periodic full snapshot for resync (handles new joiners + drift).
            if (_fullTimer >= FullSnapshotInterval)
            {
                _fullTimer = 0f;
                MPServer.BroadcastParkedSnapshot(BuildFullSnapshot());
            }

            // 30s heartbeat — diagnostics only.
            if (_diagTimer >= 30f)
            {
                _diagTimer = 0f;
                Plugin.Logger.LogInfo(
                    $"[ParkedSync] HOST tracking {_hostTracked.Count} car(s); pending adds={_pendingAdds.Count} removes={_pendingRemoves.Count}.");
            }

            // Instant per-peer resync on large movement (twice a second is plenty
            // and avoids a per-frame allocation).
            _displacementTimer += dt;
            if (_displacementTimer >= 0.5f)
            {
                _displacementTimer = 0f;
                TickPeerDisplacement();
            }
        }

        private static ParkedSnapshotPayload BuildDiffSnapshot()
        {
            var snap = new ParkedSnapshotPayload { IsFullSnapshot = false };
            try
            {
                var centers = GetPlayerCenters();
                // Adds carry full per-car data so the client can spawn/place
                // them when they come into view.  Only send cars near a player —
                // far cars can never be rendered and just waste bandwidth.
                foreach (var key in _pendingAdds)
                {
                    if (!_hostTracked.TryGetValue(key, out var go) || go == null) continue;
                    if (!go.activeInHierarchy) continue;
                    if (!NearAnyPlayer(go.transform.position, centers)) continue;
                    var dto = MakeDto(key, go);
                    if (dto != null) snap.Cars.Add(dto);
                }
                _pendingAdds.Clear();

                // Removes are just keys — the client looks them up locally.
                foreach (var key in _pendingRemoves) snap.RemovedKeys.Add(key);
                _pendingRemoves.Clear();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] BuildDiffSnapshot: {ex.Message}");
            }
            return snap;
        }

        private static ParkedSnapshotPayload BuildFullSnapshot()
        {
            var snap = new ParkedSnapshotPayload { IsFullSnapshot = true };
            try
            {
                var centers = GetPlayerCenters();
                var dead = new List<long>();
                foreach (var kv in _hostTracked)
                {
                    if (kv.Value == null) { dead.Add(kv.Key); continue; }
                    if (!kv.Value.activeInHierarchy) continue;
                    // Cull: only stream cars within reach of SOME player.  The host
                    // tracks every car the game spawns citywide (thousands); a client
                    // can only ever render the handful around itself.
                    if (!NearAnyPlayer(kv.Value.transform.position, centers)) continue;
                    var dto = MakeDto(kv.Key, kv.Value);
                    if (dto != null) snap.Cars.Add(dto);
                }
                foreach (var k in dead) _hostTracked.Remove(k);

                // A full snapshot supersedes any pending diff — flush them.
                _pendingAdds.Clear();
                _pendingRemoves.Clear();
                _diffTimer = 0f;

                Plugin.Logger.LogInfo(
                    $"[ParkedSync] HOST full snapshot: sent {snap.Cars.Count}/{_hostTracked.Count} car(s) " +
                    $"within {HostSendRadius}m of {centers.Count} player(s).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] BuildFullSnapshot: {ex.Message}");
            }
            return snap;
        }

        // ── Per-peer instant resync (teleport / building-exit / fast-travel) ──

        /// <summary>Full parked snapshot of cars near a single point — no side
        /// effects on the broadcast timers/queues (unlike BuildFullSnapshot, which
        /// flushes diffs and resets the full-snapshot cadence).</summary>
        private static ParkedSnapshotPayload BuildSnapshotAround(Vector3 center)
        {
            var snap = new ParkedSnapshotPayload { IsFullSnapshot = true };
            try
            {
                var dead = new List<long>();
                foreach (var kv in _hostTracked)
                {
                    if (kv.Value == null) { dead.Add(kv.Key); continue; }
                    if (!kv.Value.activeInHierarchy) continue;
                    var pos = kv.Value.transform.position;
                    float dx = pos.x - center.x, dy = pos.y - center.y, dz = pos.z - center.z;
                    if (dx * dx + dy * dy + dz * dz > HostSendRadiusSq) continue;
                    var dto = MakeDto(kv.Key, kv.Value);
                    if (dto != null) snap.Cars.Add(dto);
                }
                foreach (var k in dead) _hostTracked.Remove(k);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ParkedSync] BuildSnapshotAround: {ex.Message}"); }
            return snap;
        }

        /// <summary>Drop a peer's last-sent position so the next displacement pass
        /// re-syncs them from scratch.  Called on (re)join and building-exit so a
        /// player who teleported/reloaded gets parked cars immediately rather than
        /// at the next 30s full snapshot.  Thread-safe.</summary>
        public static void ForgetPeer(string playerId)
        {
            if (!string.IsNullOrEmpty(playerId)) _lastSentPosByPlayer.TryRemove(playerId, out _);
        }

        /// <summary>Host: for each connected client, if they've moved far since we
        /// last sent them parked cars (or we've forgotten them on (re)join), resend
        /// a fresh full snapshot around their position now.  Main thread.</summary>
        private static void TickPeerDisplacement()
        {
            try
            {
                foreach (var (peer, pid) in MPServer.ConnectedClientPeers())
                {
                    var posN = RemotePlayerManager.GetPlayerPosition(pid);
                    if (posN == null) continue;
                    var pos = posN.Value;
                    if (_lastSentPosByPlayer.TryGetValue(pid, out var last))
                    {
                        float dx = pos.x - last.x, dy = pos.y - last.y, dz = pos.z - last.z;
                        if (dx * dx + dy * dy + dz * dz < ResyncMoveDistSq) continue;   // not far enough — wait
                    }
                    _lastSentPosByPlayer[pid] = pos;
                    var snap = BuildSnapshotAround(pos);
                    MPServer.SendParkedSnapshotTo(peer, snap);
                    Plugin.Logger.LogInfo($"[ParkedSync] instant resync to '{pid}' (moved/joined) — {snap.Cars.Count} car(s).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ParkedSync] TickPeerDisplacement: {ex.Message}"); }
        }

        // ── Host-side proximity cull ─────────────────────────────────────────
        private static readonly List<Vector3> _centerScratch = new();

        /// <summary>Positions to cull parked cars against: the host player plus every
        /// connected client's avatar (the host renders remote players, so their
        /// transforms give each client's world position).</summary>
        private static List<Vector3> GetPlayerCenters()
        {
            _centerScratch.Clear();
            try
            {
                // Passenger riding a ghost → use the ridden car as this player's center
                // (the real character is parked at the boarding door).
                var rideT = PassengerRide.RideAnchorTransform();
                if (rideT != null) _centerScratch.Add(rideT.position);
                else
                {
                    var localChar = PlayerHelper.PlayerController?.Character;
                    if (localChar != null) _centerScratch.Add(localChar.transform.position);
                }
            }
            catch { }
            try
            {
                var remotes = RemotePlayerManager.GetRemotePlayerTransforms();
                if (remotes != null)
                    foreach (var t in remotes)
                        if (t != null) _centerScratch.Add(t.position);
            }
            catch { }
            return _centerScratch;
        }

        private static bool NearAnyPlayer(Vector3 pos, List<Vector3> centers)
        {
            // No known player position → don't cull (sending is safer than starving).
            if (centers.Count == 0) return true;
            for (int i = 0; i < centers.Count; i++)
            {
                float dx = pos.x - centers[i].x;
                float dy = pos.y - centers[i].y;
                float dz = pos.z - centers[i].z;
                if (dx * dx + dy * dy + dz * dz <= HostSendRadiusSq) return true;
            }
            return false;
        }

        private static ParkedVehicleDto? MakeDto(long key, GameObject go)
        {
            try
            {
                var t = go.transform;
                var pos = t.position;
                var rot = t.rotation;
                return new ParkedVehicleDto
                {
                    Key   = key,
                    Model = StripCloneSuffix(go.name),
                    X = pos.x, Y = pos.y, Z = pos.z,
                    Qx = rot.x, Qy = rot.y, Qz = rot.z, Qw = rot.w,
                    Colors = ReadBodyColors(go),
                };
            }
            catch { return null; }
        }

        // ── HOST capture callbacks (invoked by Harmony patches) ──────────────

        public static void HostOnRequest(GameObject go, string model)
        {
            try
            {
                if (go == null) return;
                long key = go.GetInstanceID();
                _hostTracked[key] = go;
                _pendingAdds.Add(key);
                _pendingRemoves.Remove(key);    // request after release in same window
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ParkedSync] HostOnRequest: {ex.Message}"); }
        }

        public static void HostOnRelease(GameObject go)
        {
            try
            {
                if (go == null) return;
                long key = go.GetInstanceID();
                _hostTracked.Remove(key);
                _pendingAdds.Remove(key);       // release after request in same window
                _pendingRemoves.Add(key);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ParkedSync] HostOnRelease: {ex.Message}"); }
        }

        // ── CLIENT processing ────────────────────────────────────────────────

        private static void TickClient()
        {
            _cullTimer += Time.unscaledDeltaTime;
            _diagTimer += Time.unscaledDeltaTime;

            if (_cullTimer >= CullInterval)
            {
                _cullTimer = 0f;
                CullingPass();
            }

            if (_diagTimer >= 30f)
            {
                _diagTimer = 0f;
#if BAMP_DEV
                // Release: redundant with the Perf line's parkedGhosts=N.
                Plugin.Logger.LogInfo(
                    $"[ParkedSync] CLIENT known={_clientKnown.Count} active ghosts={_clientGhosts.Count}.");
#endif
            }
        }

        public static void ApplySnapshot(ParkedSnapshotPayload payload)
        {
            try
            {
                if (payload == null) return;
                if (!ClientApplyEnabled) return;     // CLAUDE-DIAGNOSTIC F5 gate

                if (payload.IsFullSnapshot)
                {
                    // Reset known set; despawn any ghost not in the new set.
                    _clientKnown.Clear();
                    foreach (var dto in payload.Cars)
                    {
                        if (dto == null) continue;
                        _clientKnown[dto.Key] = dto;
                    }
                    // Drop ghosts whose key is no longer known.
                    var stale = _clientGhosts.Where(kv => !_clientKnown.ContainsKey(kv.Key))
                                              .Select(kv => kv.Key).ToList();
                    foreach (var k in stale)
                    {
                        if (_clientGhosts[k] != null) CallPoolRelease(_clientGhosts[k]);
                        _clientGhosts.Remove(k);
                    }
                }
                else
                {
                    // Diff: add/update Cars, remove RemovedKeys.
                    if (payload.Cars != null)
                        foreach (var dto in payload.Cars)
                            if (dto != null) _clientKnown[dto.Key] = dto;

                    if (payload.RemovedKeys != null)
                    {
                        foreach (var key in payload.RemovedKeys)
                        {
                            _clientKnown.Remove(key);
                            if (_clientGhosts.TryGetValue(key, out var g) && g != null)
                                CallPoolRelease(g);
                            _clientGhosts.Remove(key);
                        }
                    }
                }

                // Re-run culling immediately so newly-added in-range cars
                // appear without waiting up to 0.5s for the next pass.
                CullingPass();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ParkedSync] ApplySnapshot: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void CullingPass()
        {
            if (!ClientApplyEnabled) return;     // CLAUDE-DIAGNOSTIC F5 gate
            try
            {
                // Passenger riding a ghost: cull parked cars around the RIDDEN car, not the
                // character parked at the boarding door (else parked cars stop updating while riding).
                Vector3 p;
                var rideT = PassengerRide.RideAnchorTransform();
                if (rideT != null) p = rideT.position;
                else
                {
                    var localChar = PlayerHelper.PlayerController?.Character;
                    if (localChar == null) return;
                    p = localChar.transform.position;
                }

                // Iterate _clientKnown — for each, decide whether it should be
                // an active ghost based on distance to local player.
                var toSpawn = new List<ParkedVehicleDto>();
                foreach (var kv in _clientKnown)
                {
                    var dto = kv.Value;
                    float dx = dto.X - p.x, dy = dto.Y - p.y, dz = dto.Z - p.z;
                    float sq = dx * dx + dy * dy + dz * dz;
                    bool isGhost = _clientGhosts.ContainsKey(kv.Key);

                    if (!isGhost && sq <= ViewRadiusSq)
                    {
                        // Came into view — spawn (collect now, instantiate after iteration
                        // to avoid mutating _clientGhosts mid-iteration of _clientKnown).
                        toSpawn.Add(dto);
                    }
                    else if (isGhost && sq > CullRadiusSq)
                    {
                        // Went out of range (with hysteresis) — release. Clear our paint blocks first so
                        // the game's own next rental of this pooled car doesn't inherit them (round-21),
                        // and restore the RandomVehicleColor roller we disabled for our rental (round-24).
                        var g = _clientGhosts[kv.Key];
                        if (g != null)
                        {
                            ClearAllPropertyBlocks(g);
                            var rr = g.GetComponent<RandomVehicleColor>();
                            if (rr != null) rr.enabled = true;
                            CallPoolRelease(g);
                        }
                        _clientGhosts.Remove(kv.Key);
                        _nearAudited.Remove(kv.Key);
                        _appliedG0.Remove(kv.Key);
                    }
                    else if (isGhost && sq < 144f)   // within 12 m — the band the user sees flips in
                    {
                        NearGhostColorAudit(kv.Key);   // DIAG:INVESTIGATION(parked-color), one-shot per rental
                    }
                }

                foreach (var dto in toSpawn)
                {
                    if (_clientGhosts.ContainsKey(dto.Key)) continue;
                    var go = SpawnGhost(dto.Model);
                    if (go == null) continue;
                    // Pool-reuse hygiene (round-21): a pooled car comes back with the PREVIOUS life's
                    // MaterialPropertyBlocks still set (the game's traffic/parked colors, or ours).
                    // Renderers whose material is NOT the SH_Vehicle paint shader are invisible to
                    // Read/ApplyBodyColors, so a stale block there survives re-rental and shows the OLD
                    // car's color on part of the new one (user 2026-07-03: one model flipping pink↔blue
                    // by LOD band). Reset every renderer to material defaults BEFORE painting.
                    ClearAllPropertyBlocks(go);
                    LogPaintCoverage(go, dto.Model);   // DIAG:INVESTIGATION(parked-color) — once per model

                    // ROOT (round-24, user-prompted re-check of the color system): vehicle prefabs carry
                    // RandomVehicleColor, whose OnEnable RE-ROLLS a random color on EVERY pool re-enable —
                    // and car GameObjects get enable-cycled by proximity systems mid-rental, repainting the
                    // ghost a random color AFTER our apply (the near/far flips). The game itself removes the
                    // component when assigning a real color (VehicleController.SetVehicleInstance :403);
                    // ours is a pooled object, so DISABLE it for our rental and re-enable on release.
                    var rvc = go.GetComponent<RandomVehicleColor>();
                    if (rvc != null) rvc.enabled = false;

                    var t = go.transform;
                    t.position = new Vector3(dto.X, dto.Y, dto.Z);
                    t.rotation = new Quaternion(dto.Qx, dto.Qy, dto.Qz, dto.Qw);
                    // Round-31 (user: the one-color delivery truck showed up GREEN, a color it can't be):
                    // RandomVehicleColor's presence is the game's own marker for "this model wears palette
                    // colors" — GetRandomVehicleColor has NO other caller, so prefabs without the component
                    // (delivery truck, police car, ambulance…) are never painted natively and every machine
                    // already shows their authored material look. Exact sync for them = paint NOTHING.
                    // Snapping their factory tint to the nearest palette entry is what greened the truck
                    // (probe: NEAR-AUDIT deliverytruck expected(0.11,0.10,0.08) body=(0.18,0.34,0.18)).
                    if (rvc != null && dto.Colors != null && dto.Colors.Count >= 6)
                    {
                        // Prefer the game's own paint API (CarFeatures.SetColor: tint + fresnel color +
                        // fresnel POWER on the curated bodyMeshes) with the nearest palette entry — the
                        // host's color IS a palette entry, so nearest = exact. Legacy MPB apply as fallback.
                        if (!ApplyNativeColor(go, dto))
                            ApplyBodyColors(go, dto.Colors);
                        _appliedG0[dto.Key] = new Color(dto.Colors[0], dto.Colors[1], dto.Colors[2]);   // audit baseline
                    }
                    _nearAudited.Remove(dto.Key);   // fresh rental → audit anew when approached
                    _clientGhosts[dto.Key] = go;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ParkedSync] CullingPass: {ex.Message}");
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
                return _miRequest.Invoke(null, new object[] { model }) as GameObject;
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

        // ── SH_Vehicle colour reader/applier ─────────────────────────────────

        // DIAG:INVESTIGATION(parked-color) — one-shot registries so the probes below log each car once.
        // REMOVE with the probes once the near/far color flip is settled (user 2026-07-03: far matches the
        // host, CLOSE flips to a wrong color — only a NON-uniform read/apply can do that, so only those log).
        private static readonly HashSet<int> _colorReadLogged  = new();
        private static readonly HashSet<int> _colorApplyLogged = new();
        private static string ColStr(Color c) => $"({c.r:F2},{c.g:F2},{c.b:F2})";

        /// <summary>Renderer-name LOD stem: "Zucchini_LOD2" → "Zucchini",
        /// "SM_GMC Yukon Inspired_Civilian_LOD3_2_hwQEK…" → "SM_GMC Yukon Inspired_Civilian".
        /// Names without a _LOD suffix (wheels, LOD0 bodies) come back unchanged.</summary>
        private static string LodStem(string n)
            => System.Text.RegularExpressions.Regex.Replace(n ?? "", @"_LOD\d+.*$", "");

        /// <summary>Apply the host's parked-car color through the game's OWN paint API (round-24):
        /// CarFeatures.SetColor(vehicleColor) — tint + fresnel color + fresnel power on the prefab's curated
        /// bodyMeshes. The wire carries the host's read tint+fresnel pair; the host's color always comes from
        /// the global VehicleColors palette (RandomVehicleColor → GetRandomVehicleColor), so the nearest
        /// palette entry IS the exact color. Returns false (→ legacy MPB fallback) if anything is missing.</summary>
        private static bool ApplyNativeColor(GameObject ghost, ParkedVehicleDto dto)
        {
            try
            {
                var cf = ghost.GetComponent<CarFeatures>();
                var table = InstanceBehavior<GlobalReferences>.Instance?.vehicleColors;
                if (cf == null || table == null) return false;
                var c1 = new Color(dto.Colors[0], dto.Colors[1], dto.Colors[2]);
                var c2 = new Color(dto.Colors[3], dto.Colors[4], dto.Colors[5]);
                Data.VehicleColors.VehicleColor? best = null;
                float bd = float.MaxValue;
                foreach (var vc in table)
                {
                    if (vc == null) continue;
                    // Property order in the wire pair isn't guaranteed (shader property iteration) — score
                    // both assignments and take the better one.
                    float dA = ColDist(c1, vc.tint) + ColDist(c2, vc.fresnelColor);
                    float dB = ColDist(c2, vc.tint) + ColDist(c1, vc.fresnelColor);
                    float d  = Mathf.Min(dA, dB);
                    if (d < bd) { bd = d; best = vc; }
                }
                if (best == null) return false;
                cf.SetColor(best);
                return true;
            }
            catch { return false; }
        }

        private static float ColDist(Color a, Color b)
            => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

        /// <summary>Pool-reuse hygiene (round-21): reset every renderer's MaterialPropertyBlock so a pooled
        /// car starts from material defaults — a stale block from its previous rental (any source) is the
        /// only way one car can wear TWO real paint jobs at once. Called on rent (before painting) and on
        /// our release (before handing back to the pool).</summary>
        private static void ClearAllPropertyBlocks(GameObject car)
        {
            try
            {
                var rends = car.GetComponentsInChildren(typeof(Renderer), true);
                for (int i = 0; i < rends.Length; i++)
                    (rends[i] as Renderer)?.SetPropertyBlock(null);
            }
            catch { }
        }

        // DIAG:INVESTIGATION(parked-color) — NEAR AUDIT: when the local player walks up to a synced parked
        // ghost (<12 m), compare what its paint-shader renderers are CURRENTLY wearing against what we applied
        // at spawn. A MISMATCH line = something repainted the car after our apply (the game's own systems, a
        // per-material-index block our whole-renderer write doesn't cover, …) — catches any post-apply repaint
        // regardless of source. One-shot per rental; quiet when everything matches.
        private static readonly Dictionary<long, Color> _appliedG0 = new();
        private static readonly HashSet<long> _nearAudited = new();
        private static void NearGhostColorAudit(long key)
        {
            try
            {
                if (_nearAudited.Contains(key)) return;
                if (!_clientGhosts.TryGetValue(key, out var go) || go == null) return;
                if (!_appliedG0.TryGetValue(key, out var expected)) return;
                _nearAudited.Add(key);

                var mism = new List<string>();
                var rends = go.GetComponentsInChildren(typeof(Renderer), true);
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i] as Renderer;
                    if (r == null) continue;
                    var mat = FindShVehicleMaterial(r);
                    if (mat == null) continue;
                    var g = ReadRendererColors(r, mat, out _);
                    // Only body-family renderers are expected to wear g0; wheels legitimately differ —
                    // flag a renderer only when it wore g0's EXPECTED color at apply time is unknowable
                    // here, so keep it simple: report every paint renderer whose current color is neither
                    // the expected body color nor "close" to it, with its name — the reader decides.
                    var c = g.Item1;
                    bool close = Mathf.Abs(c.r - expected.r) + Mathf.Abs(c.g - expected.g) + Mathf.Abs(c.b - expected.b) < 0.05f;
                    if (!close) mism.Add($"{r.name}={ColStr(c)}");
                }
                if (mism.Count > 0)
                {
                    var p = go.transform.position;
                    Plugin.Logger.LogInfo($"[ParkedColor] NEAR-AUDIT '{go.name}' @({p.x:F0},{p.z:F0}) expected{ColStr(expected)}: "
                        + string.Join("; ", mism.GetRange(0, Math.Min(8, mism.Count))) + (mism.Count > 8 ? " …" : ""));
                }
            }
            catch { }
        }

        // DIAG:INVESTIGATION(parked-color) — one line per MODEL naming the renderers our paint system CANNOT
        // see (non-SH_Vehicle materials). Those are exactly the meshes a stale pool block would survive on.
        private static readonly HashSet<string> _skipLoggedModels = new();
        private static void LogPaintCoverage(GameObject ghost, string model)
        {
            try
            {
                if (!_skipLoggedModels.Add(model)) return;
                var rends = ghost.GetComponentsInChildren(typeof(Renderer), true);
                var unseen = new List<string>();
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i] as Renderer;
                    if (r != null && FindShVehicleMaterial(r) == null) unseen.Add(r.name);
                }
                if (unseen.Count > 0)
                    Plugin.Logger.LogInfo($"[ParkedColor] COVERAGE '{model}': {unseen.Count} renderer(s) NOT paint-shader — "
                        + string.Join("; ", unseen.GetRange(0, Math.Min(10, unseen.Count))) + (unseen.Count > 10 ? " …" : ""));
            }
            catch { }
        }

        private static List<float> ReadBodyColors(GameObject car)
        {
            var groups = new List<(Color, Color)>();
            var dbg = new List<string>();   // DIAG:INVESTIGATION(parked-color)
            try
            {
                var rends = car.GetComponentsInChildren(typeof(Renderer), true);
                var entries = new List<((Color, Color) g, bool fromMpb, string matName, string rendName)>();
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i] as Renderer;
                    if (r == null) continue;
                    var mat = FindShVehicleMaterial(r);
                    if (mat == null) continue;
                    var g = ReadRendererColors(r, mat, out bool fromMpb);
                    entries.Add((g, fromMpb, mat.name ?? "", r.name ?? ""));
                }

                // READ-REPAIR (round-18/19, probe-confirmed): the game leaves SOME body-LOD renderers'
                // property block unset (every Zucchini's LOD2) — the fallback then read the SHARED material's
                // default (white) and shipped it, so clients painted that LOD band white while the host shows
                // the body color. An unset renderer inherits from a block-read sibling that is the SAME
                // painted surface: same material asset OR same renderer NAME-STEM (LOD families —
                // "Zucchini_LOD2" ↔ "Zucchini_LOD1", "…_Civilian_LOD2_<hash>" ↔ "…_Civilian"; round-19: the
                // material-name pairing alone missed, the wire still carried white → LOD variants evidently
                // use distinct material assets). Wheels keep their material defaults (distinct stems, no
                // block-read sibling).
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].fromMpb) continue;
                    string stemI = LodStem(entries[i].rendName);
                    for (int j = 0; j < entries.Count; j++)
                    {
                        if (j == i || !entries[j].fromMpb) continue;
                        if (entries[j].matName == entries[i].matName || LodStem(entries[j].rendName) == stemI)
                        { entries[i] = (entries[j].g, true, entries[i].matName, entries[i].rendName); break; }
                    }
                }

                // Probe log AFTER repair — shows what actually ships (round-19: the pre-repair log hid
                // whether the repair ran).
                for (int i = 0; i < entries.Count; i++)
                {
                    groups.Add(entries[i].g);
                    dbg.Add($"{entries[i].rendName}[{entries[i].matName}]={(entries[i].fromMpb ? "mpb" : "mat")}{ColStr(entries[i].g.Item1)}");
                }
            }
            catch { }
            if (groups.Count == 0) groups.Add((Color.white, Color.white));

            bool uniform = true;
            for (int i = 1; i < groups.Count && uniform; i++)
                if (groups[i] != groups[0]) uniform = false;

            // DIAG:INVESTIGATION(parked-color) — host-side read, only for flip-capable (non-uniform) cars.
            if (!uniform && _colorReadLogged.Add(car.GetInstanceID()))
            {
                var p = car.transform.position;
                Plugin.Logger.LogInfo($"[ParkedColor] READ '{car.name}' @({p.x:F0},{p.z:F0}) renderers={dbg.Count}: "
                    + string.Join("; ", dbg.GetRange(0, Math.Min(8, dbg.Count))) + (dbg.Count > 8 ? " …" : ""));
            }

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

        private static (Color, Color) ReadRendererColors(Renderer r, Material mat, out bool fromMpb)
        {
            var mpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            Color c1 = Color.white, c2 = Color.white;
            fromMpb = false;   // DIAG:INVESTIGATION(parked-color) — where the FIRST color came from
            int found = 0, n = mat.shader.GetPropertyCount();
            for (int p = 0; p < n && found < 2; p++)
            {
                if (mat.shader.GetPropertyType(p) != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                string pn = mat.shader.GetPropertyName(p);
                if (pn.StartsWith("_")) continue;
                var mpbCol = mpb.GetColor(pn);
                bool useMpb = mpbCol.a >= 0.5f;
                var col = useMpb ? mpbCol : mat.GetColor(pn);
                if (found == 0) { c1 = col; fromMpb = useMpb; } else c2 = col;
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

                // DIAG:INVESTIGATION(parked-color) — client-side apply, only for flip-capable (multi-group)
                // payloads: shows which renderer got which group by INDEX pairing (the suspected mismatch).
                var dbgA = (groups > 1 && _colorApplyLogged.Add(ghost.GetInstanceID()))
                           ? new List<string>() : null;

                var rends = ghost.GetComponentsInChildren(typeof(Renderer), true);
                int ri = 0;
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i] as Renderer;
                    if (r == null) continue;
                    var mat = FindShVehicleMaterial(r);
                    if (mat == null) continue;

                    int gi = groups == 1 ? 0 : Mathf.Min(ri, groups - 1);
                    int b  = gi * 6;
                    var c1 = new Color(colors[b],     colors[b + 1], colors[b + 2]);
                    var c2 = new Color(colors[b + 3], colors[b + 4], colors[b + 5]);
                    if (dbgA != null && dbgA.Count < 8) dbgA.Add($"{r.name}←g{gi}{ColStr(c1)}");

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

                if (dbgA != null)
                {
                    var p = ghost.transform.position;
                    Plugin.Logger.LogInfo($"[ParkedColor] APPLY '{ghost.name}' @({p.x:F0},{p.z:F0}) rends={ri} groups={groups}: "
                        + string.Join("; ", dbgA) + (ri > 8 ? " …" : ""));
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
