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
                    try { CallPoolRelease(g); releasedCount++; } catch { }
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
                var localChar = PlayerHelper.PlayerController?.Character;
                if (localChar != null) _centerScratch.Add(localChar.transform.position);
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
                Plugin.Logger.LogInfo(
                    $"[ParkedSync] CLIENT known={_clientKnown.Count} active ghosts={_clientGhosts.Count}.");
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
                var localChar = PlayerHelper.PlayerController?.Character;
                if (localChar == null) return;
                var p = localChar.transform.position;

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
                        // Went out of range (with hysteresis) — release.
                        var g = _clientGhosts[kv.Key];
                        if (g != null) CallPoolRelease(g);
                        _clientGhosts.Remove(kv.Key);
                    }
                }

                foreach (var dto in toSpawn)
                {
                    if (_clientGhosts.ContainsKey(dto.Key)) continue;
                    var go = SpawnGhost(dto.Model);
                    if (go == null) continue;
                    var t = go.transform;
                    t.position = new Vector3(dto.X, dto.Y, dto.Z);
                    t.rotation = new Quaternion(dto.Qx, dto.Qy, dto.Qz, dto.Qw);
                    if (dto.Colors != null && dto.Colors.Count >= 6)
                        ApplyBodyColors(go, dto.Colors);
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

        private static List<float> ReadBodyColors(GameObject car)
        {
            var groups = new List<(Color, Color)>();
            try
            {
                var rends = car.GetComponentsInChildren(typeof(Renderer), true);
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i] as Renderer;
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
