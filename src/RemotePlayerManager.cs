using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Helpers;

namespace BigAmbitionsMP
{
    // ── Smooth-movement MonoBehaviour ─────────────────────────────────────────

    /// <summary>
    /// Attached to each remote-player capsule.  Lerps toward the latest received
    /// position/rotation every frame so movement looks smooth rather than teleporting.
    /// </summary>
    public class RemotePlayerMover : MonoBehaviour
    {
        public Vector3    TargetPosition;
        public Quaternion TargetRotation = Quaternion.identity;
        // Dead reckoning: measured velocity between packets + the time of the
        // last packet — Update() chases an extrapolated point so motion stays
        // continuous between 10 Hz updates even at low FPS (plain lerp made
        // remote players/vehicles step move-pause-move).
        public Vector3    Velocity;
        public float      TargetAt;   // receiver clock at arrival (extrapolation base)
        public float      SenderT;    // sender's sample clock (velocity measurement)

        /// <summary>The cloned character's Animator, if a model was spawned.</summary>
        public Animator?  Anim;

        /// <summary>While set, the avatar is PINNED to this transform (an open
        /// vehicle's ghost — scooter deck / cart handles) instead of chasing its
        /// own network targets: the avatar and vehicle are smoothed as separate
        /// streams and drift apart otherwise.</summary>
        public Transform? RideAttach;
        public Vector3    RideOffset;

        private const float LerpSpeed  = 20f;    // how quickly we chase the network target
        private const float AnimFLerp  = 14f;    // float-param chase rate (smooths 10 Hz steps)
        private const float SnapDist   = 10f;    // bigger jump = teleport, don't slide
        private const float MaxExtrapolate = 0.3f;

        /// <summary>Feed a new network target (computes velocity for dead reckoning).
        /// senderT = the sender's sample clock from the packet — velocity must be
        /// measured against THAT, not against frame-quantized arrival times.</summary>
        public void SetTarget(Vector3 pos, Quaternion rot, float senderT)
        {
            if (Vector3.Distance(transform.position, pos) > SnapDist)
            {
                transform.position = pos;
                transform.rotation = rot;
                Velocity = Vector3.zero;
            }
            else
            {
                float dt = senderT - SenderT;
                if (dt > 0.005f)                    // tiny = two packets one frame — keep prior velocity
                    Velocity = (pos - TargetPosition) / dt;
            }
            TargetPosition = pos;
            TargetRotation = rot;
            TargetAt       = Time.unscaledTime;
            SenderT        = senderT;
        }

        // Animator parameter map (index → name/type), built once from Anim.parameters.
        private string[]?                            _paramNames;
        private AnimatorControllerParameterType[]?   _paramTypes;

        // Latest networked animator state.
        private readonly Dictionary<int, float> _targetF  = new();
        private readonly Dictionary<int, float> _currentF = new();
        private readonly Dictionary<int, int>   _targetI  = new();
        private readonly HashSet<int>           _trueB    = new();
        private readonly List<int>              _pendingTriggers = new();

        private bool       _labelSearched;
        private Transform? _labelTransform;

        /// <summary>Stores the latest networked float/bool/int animator state.</summary>
        public void ApplyAnimState(Dictionary<int, float>? floats,
                                   List<int>? trueBools,
                                   Dictionary<int, int>? ints,
                                   List<float>? layerWeights = null,
                                   List<float>? ikTargets = null)
        {
            if (floats != null)
                foreach (var kv in floats) _targetF[kv.Key] = kv.Value;
            if (ints != null)
                foreach (var kv in ints) _targetI[kv.Key] = kv.Value;
            _trueB.Clear();
            if (trueBools != null)
                foreach (var b in trueBools) _trueB.Add(b);
            _layerW = layerWeights;
            // Hand-IK anchors with a grace window: one missed packet must not
            // release the hands (visible pop).  Cleared after 0.6s of silence.
            if (ikTargets != null && ikTargets.Count >= 8)
            {
                _ikT = ikTargets;
                _ikLastAt = Time.unscaledTime;
            }
            else if (_ikT != null && Time.unscaledTime - _ikLastAt > 0.6f)
            {
                _ikT = null;
                _ikSmoothInit = false;
            }
        }

        // Mirrored animator-layer weights.  The clone's game scripts are
        // stripped, so nothing raises layer weights locally — a state can be
        // ENTERED (transitions are parameter-driven) yet render at weight 0.
        // That was the cart-pusher bug: push state active, blend invisible.
        private List<float>? _layerW;

        // Mirrored hand-IK (pushing): vehicle-local hand anchors from the
        // sender; manual two-bone solve on the clone's arm bones AFTER the
        // animation pass (LateUpdate) so it overrides the frame's pose.
        private List<float>? _ikT;
        private float _ikLastAt;
        private bool _ikHumanChecked;
        private bool _ikHuman;
        private bool _ikSmoothInit;
        private Vector3 _ikL, _ikR;   // smoothed vehicle-local hand anchors

        /// <summary>The clone's HandContent bone — the anchor space for
        /// held-item hand mirroring (cart pushing uses the vehicle instead).</summary>
        private Transform? _handContent;
        private bool _handContentSearched;

        // Cached main camera for label billboarding.  Camera.main is a tagged
        // scene search — refresh at most 1x/s instead of every frame per remote.
        private static Camera? _billboardCam;
        private static float   _billboardCamAt = -999f;

        private void LateUpdate()
        {
            if (_ikT == null || Anim == null) return;
            // Anchor space: the pushed vehicle, else the HandContent bone while
            // a held prop's anchors are streaming (held-item carry grip).
            Transform? space = RideAttach;
            if (space == null)
            {
                if (!_handContentSearched)
                {
                    _handContentSearched = true;
                    try
                    {
                        foreach (var t in GetComponentsInChildren<Transform>(true))
                            if (t != null && t.name == "HandContent") { _handContent = t; break; }
                    }
                    catch { }
                }
                space = _handContent;
            }
            if (space == null) return;
            try
            {
                if (!_ikHumanChecked) { _ikHumanChecked = true; _ikHuman = Anim.isHuman; }
                if (!_ikHuman) return;
                long _pf = MPPerf.Begin();   // DIAG:FIELD — per-remote IK solve cost (RemLate bracket)

                // Smooth the anchors — packets step at 10 Hz and raw application
                // makes the hands pop between spots.
                var lRaw = new Vector3(_ikT[0], _ikT[1], _ikT[2]);
                var rRaw = new Vector3(_ikT[3], _ikT[4], _ikT[5]);
                if (!_ikSmoothInit) { _ikSmoothInit = true; _ikL = lRaw; _ikR = rRaw; }
                float k = Mathf.Min(Time.deltaTime * 10f, 1f);
                _ikL = Vector3.Lerp(_ikL, lRaw, k);
                _ikR = Vector3.Lerp(_ikR, rRaw, k);

                // Pole = world down: elbow solution stays stable however the
                // avatar/cart rotate (an avatar-relative pole flipped the
                // elbows on turns — the "arms coming off" wobble).
                MPHandIk.SolveArm(Anim, true,  space.TransformPoint(_ikL), Vector3.down);
                MPHandIk.SolveArm(Anim, false, space.TransformPoint(_ikR), Vector3.down);
                MPPerf.End("RemLate", _pf);
            }
            catch { }
        }

        /// <summary>Queues a one-off trigger to fire on the next frame.</summary>
        public void FireTrigger(int paramIndex)
        {
            if (paramIndex >= 0) _pendingTriggers.Add(paramIndex);
        }


        private void EnsureParamMap()
        {
            if (_paramNames != null || Anim == null) return;
            var ps = Anim.parameters;
            _paramNames = new string[ps.Length];
            _paramTypes = new AnimatorControllerParameterType[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                _paramNames[i] = ps[i].name;
                _paramTypes[i] = ps[i].type;
            }
        }

        private void Update()
        {
            long _pf = MPPerf.Begin();   // DIAG:FIELD — per-remote avatar mirror cost (RemUpd bracket)
            // Riding/pushing an open vehicle: follow the vehicle ghost rigidly
            // (position + facing) — its own dead reckoning does the smoothing.
            if (RideAttach != null)
            {
                try
                {
                    transform.position = RideAttach.TransformPoint(RideOffset);
                    transform.rotation = Quaternion.Euler(0f, RideAttach.eulerAngles.y, 0f);
                }
                catch { RideAttach = null; }   // ghost despawned — resume normal sync
            }
            else
            {
                // Smooth position/rotation toward the dead-reckoned network target.
                // Blend capped so packet corrections spread over frames at low FPS.
                float k = Mathf.Min(Time.deltaTime * LerpSpeed, 0.5f);
                float ahead = Mathf.Min(Time.unscaledTime - TargetAt, MaxExtrapolate);
                var predicted = TargetPosition + Velocity * ahead;
                transform.position = Vector3.Lerp(transform.position, predicted, k);
                transform.rotation = Quaternion.Slerp(transform.rotation, TargetRotation, k);
            }


            // Mirror the owner's real animator state (no local guessing).
            if (Anim != null)
            {
                EnsureParamMap();
                try
                {
                    // Floats — chase the networked value so 10 Hz updates look smooth.
                    foreach (var kv in _targetF)
                    {
                        if (kv.Key < 0 || kv.Key >= _paramNames!.Length) continue;
                        float cur = _currentF.TryGetValue(kv.Key, out var c) ? c : kv.Value;
                        cur = Mathf.Lerp(cur, kv.Value, Time.deltaTime * AnimFLerp);
                        _currentF[kv.Key] = cur;
                        Anim.SetFloat(_paramNames[kv.Key], cur);
                    }
                    // Ints — applied directly.
                    foreach (var kv in _targetI)
                    {
                        if (kv.Key < 0 || kv.Key >= _paramNames!.Length) continue;
                        Anim.SetInteger(_paramNames[kv.Key], kv.Value);
                    }
                    // Bools — every bool param set explicitly (true iff in the synced set).
                    if (_paramTypes != null)
                        for (int i = 0; i < _paramTypes.Length; i++)
                            if (_paramTypes[i] == AnimatorControllerParameterType.Bool)
                                Anim.SetBool(_paramNames![i], _trueB.Contains(i));
                    // Triggers — fire-and-forget one-off actions.
                    if (_pendingTriggers.Count > 0)
                    {
                        foreach (var t in _pendingTriggers)
                            if (t >= 0 && t < _paramNames!.Length)
                                Anim.SetTrigger(_paramNames[t]);
                        _pendingTriggers.Clear();
                    }
                    // Layer weights — mirrored from the sender (layer 0 is
                    // always weight 1 in Unity; start at 1).
                    if (_layerW != null)
                        for (int l = 1; l < _layerW.Count && l < Anim.layerCount; l++)
                            Anim.SetLayerWeight(l, _layerW[l]);
                }
                catch { /* parameter mismatch — ignore */ }
            }

            // Billboard: make the name label always face the camera
            if (!_labelSearched)
            {
                _labelTransform = transform.Find("BAMP_Label");
                _labelSearched  = true;
            }
            if (_labelTransform != null)
            {
                if (_billboardCam == null || Time.unscaledTime - _billboardCamAt > 1f)
                { _billboardCam = Camera.main; _billboardCamAt = Time.unscaledTime; }
                if (_billboardCam != null)
                    _labelTransform.rotation = _billboardCam.transform.rotation;
            }
            MPPerf.End("RemUpd", _pf);
        }
    }

    // ── Manager ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and manages a capsule + name-label for each remote player.
    /// All public methods must be called on the Unity main thread.
    /// </summary>
    public static class RemotePlayerManager
    {
        private static readonly Dictionary<string, GameObject> _players = new();

        /// <summary>Returns a snapshot of currently tracked remote player IDs.</summary>
        public static IReadOnlyList<string> GetRemotePlayerIds() =>
            new List<string>(_players.Keys);

        /// <summary>Transforms of every tracked remote player (for traffic anchoring).</summary>
        public static List<Transform> GetRemotePlayerTransforms()
        {
            var list = new List<Transform>();
            foreach (var go in _players.Values)
                if (go != null) list.Add(go.transform);
            return list;
        }

        /// <summary>
        /// Moves an existing remote-player capsule, or spawns one on first call.
        /// No-ops when not in-game.
        /// </summary>
        public static void DestroyAllRemotePlayers()
        {
            foreach (var kv in _players)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _players.Clear();
            Plugin.Logger.LogInfo("[RemotePlayer] Destroyed all remote players (kill-switch).");
        }

        public static void SpawnOrUpdate(PlayerPositionPayload p)
        {
            if (SaveGameManager.Current == null) return;

            // Don't spawn/update remote players until WE are actually standing in the
            // game world.  SaveGameManager.Current goes non-null at SaveGameManager.New()
            // — which runs BEFORE character creation — so the check above is true while
            // we're still in the customizer with no PlayerController.  Spawning a physics
            // capsule into the intro/char-creation scene (then tearing it down on the
            // scene transition) is the suspected host crash when a client finishes
            // customization long before we do.  Gate on the real in-world indicator.
            if (PlayerHelper.PlayerController == null)
            {
                DiagEarlyRemoteData(p.PlayerId);
                return;
            }

            if (!_players.TryGetValue(p.PlayerId, out var go) || go == null)
                go = SpawnRemotePlayer(p.PlayerId);

            // Give the target to the mover component; it dead-reckons every frame
            var mover = go.GetComponent<RemotePlayerMover>();
            if (mover != null)
            {
                mover.SetTarget(new Vector3(p.X, p.Y, p.Z), Quaternion.Euler(0f, p.RotY, 0f), p.T);
                mover.ApplyAnimState(p.AnimF, p.AnimB, p.AnimI, p.LayerW, p.IkT);
            }
            else
            {
                // Fallback if mover somehow missing
                go.transform.position = new Vector3(p.X, p.Y, p.Z);
                go.transform.rotation = Quaternion.Euler(0f, p.RotY, 0f);
            }

            // ── Cross-interior mask (2026-06-11): interiors of the same building
            // TYPE share one detached coordinate space (~x900), so a player inside
            // building A otherwise renders inside building B for anyone standing
            // there.  Show the avatar only when sender and local player are in the
            // SAME building (or both outdoors).  Root-level SetActive — SetDriving
            // toggles the Model/Capsule CHILDREN, so the two never fight.  Local
            // building changes re-evaluate within one packet (~100 ms).
            ApplyHeldProp(go, p.PlayerId, p.Held ?? "");
            // Mirror the holder's exact prop placement (baskets hang off-axis).
            if (p.HeldT != null && p.HeldT.Count >= 6
                && _heldProps.TryGetValue(p.PlayerId, out var heldGo) && heldGo != null)
            {
                heldGo.transform.localPosition    = new Vector3(p.HeldT[0], p.HeldT[1], p.HeldT[2]);
                heldGo.transform.localEulerAngles = new Vector3(p.HeldT[3], p.HeldT[4], p.HeldT[5]);
            }

            _remoteBuildings[p.PlayerId] = p.Bldg ?? "";
            bool sameRoom = (p.Bldg ?? "") == (MPRegisterSync.CurrentShopAddress ?? "");
            if (go.activeSelf != sameRoom)
            {
                go.SetActive(sameRoom);
                if (sameRoom && mover != null)
                {
                    // While hidden the mover doesn't run — snap to the live
                    // target so the avatar doesn't glide from its stale spot
                    // (user saw the worker "teleport a bit" on shop entry).
                    go.transform.position = mover.TargetPosition;
                    go.transform.rotation = mover.TargetRotation;
                    mover.Velocity = Vector3.zero;
                }
                Plugin.Logger.LogInfo(
                    $"[InteriorMask] '{p.PlayerId}' {(sameRoom ? "shown" : "hidden")} — " +
                    $"their bldg='{p.Bldg}' mine='{MPRegisterSync.CurrentShopAddress}'.");
            }
        }

        // ── Cross-interior mask state (see SpawnOrUpdate) ─────────────────────
        private static readonly Dictionary<string, string> _remoteBuildings = new();

        /// <summary>True when a remote player's avatar is hidden by the
        /// cross-interior mask (used to hide their nearby vehicle ghosts too).</summary>
        public static bool IsMasked(string playerId) =>
            _players.TryGetValue(playerId, out var go) && go != null && !go.activeSelf;

        /// <summary>Remote avatar's current world position (valid even while masked).</summary>
        public static Vector3? GetPlayerPosition(string playerId) =>
            _players.TryGetValue(playerId, out var go) && go != null
                ? go.transform.position : (Vector3?)null;

        /// <summary>Applies a networked one-off trigger to a remote player's model.</summary>
        public static void ApplyTrigger(string playerId, int paramIndex)
        {
            if (_players.TryGetValue(playerId, out var go) && go != null)
                go.GetComponent<RemotePlayerMover>()?.FireTrigger(paramIndex);
        }

        // ── Crash diagnostic (2026-06-01) ────────────────────────────────────
        // Fires when remote-player data arrives while OUR own world isn't loaded
        // yet (PlayerController null but a save exists = we're in character
        // creation).  This is the suspected host-CTD window when a client loads
        // far ahead of us.  The spawn is now suppressed; this log confirms whether
        // the path was actually being hit, so if the crash persists we know to look
        // elsewhere.  Throttled + unscaled-time so it can't spam at 10 Hz.
        private static float _earlyDataNextLog;
        private static int   _earlyDataCount;

        private static void DiagEarlyRemoteData(string playerId)
        {
            _earlyDataCount++;
            float now = Time.unscaledTime;
            if (now < _earlyDataNextLog) return;
            _earlyDataNextLog = now + 2f;
            Plugin.Logger.LogWarning(
                $"[RemotePlayer] EARLY-DATA: remote-player data for '{playerId}' arrived while " +
                $"local world not ready (PlayerController null, save loaded) — spawn suppressed. " +
                $"count={_earlyDataCount}");
        }

        /// <summary>Remove one remote player (they left the session).</summary>
        /// <summary>
        /// Looks up the in-character name for a remote PlayerId.  Falls back
        /// to PlayerId if no profile has arrived yet for this player.
        /// </summary>
        public static string ResolveDisplayName(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return "";
            if (GameStatePatcher.ClientRivalNames.TryGetValue(playerId, out var n)
                && !string.IsNullOrWhiteSpace(n)) return n;
            return playerId;
        }

        /// <summary>
        /// Called when a PlayerProfile message arrives — refreshes the
        /// floating name tag above the matching remote-player avatar.
        /// No-op if the player isn't currently spawned.
        /// </summary>
        public static void UpdateLabel(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            if (!_players.TryGetValue(playerId, out var go) || go == null) return;
            try
            {
                var t = go.transform.Find("BAMP_Label");
                if (t == null) return;
                var tmp = t.GetComponent<TextMeshProUGUI>();
                if (tmp == null) return;
                tmp.text = ResolveDisplayName(playerId);
                Plugin.Logger.LogInfo($"[RemotePlayer] Label refreshed for '{playerId}' → '{tmp.text}'");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[RemotePlayer] UpdateLabel '{playerId}': {ex.Message}"); }
        }

        public static void Remove(string playerId)
        {
            if (_players.TryGetValue(playerId, out var go) && go != null)
                UnityEngine.Object.Destroy(go);
            _players.Remove(playerId);
            _appearances.Remove(playerId);
            VehicleManager.DespawnAllOwnedBy(playerId);
            Plugin.Logger.LogInfo($"[RemotePlayer] Removed '{playerId}'");
        }

        /// <summary>Remove all remote players (disconnect / scene unload).</summary>
        public static void RemoveAll()
        {
            foreach (var kv in _players)
                if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            _players.Clear();
            _appearances.Clear();
            _remoteBuildings.Clear();   // interior-mask state dies with the avatars
            _heldApplied.Clear();       // held-prop state too
            _heldProps.Clear();
            _heldTemplates.Clear();     // scene templates died with the scene
            _localHandContent = null;
            _localAnim = null;          // re-fetched (with fresh trigger maps) next game
            _triggerHashToIndex = null;
            _triggerNameToIndex = null;
            VehicleManager.DespawnAll();
            TrafficSync.DespawnAllGhosts();
            Plugin.Logger.LogInfo("[RemotePlayer] All remote players removed.");
        }

        private static void DumpHierarchy(Transform t, int depth)
        {
            string indent = new string(' ', depth * 3);
            string comps;
            try
            {
                var cs = t.gameObject.GetComponents(typeof(Component));
                var names = new List<string>();
                for (int i = 0; i < cs.Length; i++)
                    if (cs[i] != null) names.Add(cs[i].GetType().Name);
                comps = string.Join(", ", names);
            }
            catch { comps = "(components unavailable)"; }

            Plugin.Logger.LogInfo($"[RemotePlayer]   {indent}{t.name}  [{comps}]");

            if (depth >= 4) return;          // cap depth — skeleton bones go very deep
            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(t.GetChild(i), depth + 1);
        }

        private static Component? FindComponentByName(GameObject go, string typeName)
        {
            try
            {
                var comps = go.GetComponents(typeof(Component));
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c != null && c.GetType().Name == typeName) return c;
                }
            }
            catch { }
            return null;
        }

        // ── Appearance sync ───────────────────────────────────────────────────
        //
        // Appearance = gender (Male/Female sub-tree active) + the one active
        // variant GameObject per category.  Read off the local player, networked,
        // and re-applied to each remote player's cloned model with SetActive.

        private static readonly Dictionary<string, PlayerAppearancePayload> _appearances = new();

        /// <summary>Reads the local player's appearance, or null if the character isn't ready.</summary>
        public static PlayerAppearancePayload? ReadLocalAppearance()
        {
            try
            {
                var model = PlayerHelper.PlayerController?.Character?.transform.Find("Model");
                if (model == null) return null;
                var maleT = model.Find("Male");
                var femT  = model.Find("Female");
                if (maleT == null || femT == null) return null;

                bool male   = maleT.gameObject.activeSelf;
                var genderT = male ? maleT : femT;
                var dto = new PlayerAppearancePayload
                {
                    PlayerId = MPConfig.PlayerId,
                    Gender   = male ? "Male" : "Female",
                };
                for (int ci = 0; ci < genderT.childCount; ci++)
                {
                    var cat = genderT.GetChild(ci);
                    for (int vi = 0; vi < cat.childCount; vi++)
                    {
                        var v = cat.GetChild(vi);
                        if (!v.gameObject.activeSelf) continue;
                        dto.Variants[cat.name] = v.name;
                        CaptureColors(dto, cat.name, v.gameObject);
                        CaptureBlendShapes(dto, cat.name, v.gameObject);
                        break;
                    }
                }
                return dto;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Appearance] ReadLocalAppearance: {ex.Message}");
                return null;
            }
        }

        /// <summary>Stores a player's appearance; applies it now if their model exists.</summary>
        public static void SetAppearance(PlayerAppearancePayload? dto)
        {
            if (dto == null || string.IsNullOrEmpty(dto.PlayerId)) return;
            _appearances[dto.PlayerId] = dto;
            if (_players.TryGetValue(dto.PlayerId, out var root) && root != null)
                ApplyAppearance(root, dto);
        }

        /// <summary>Pin (or release: vehicle=null) a remote player's avatar to an
        /// open vehicle's ghost — scooters/carts, where the rider must stay
        /// visible AND glued to the vehicle.</summary>
        public static void SetRide(string playerId, Transform? vehicle, Vector3 offset)
        {
            try
            {
                if (!_players.TryGetValue(playerId, out var go) || go == null) return;
                var mover = go.GetComponent<RemotePlayerMover>();
                if (mover == null) return;
                mover.RideAttach = vehicle;
                mover.RideOffset = offset;
            }
            catch { }
        }

        /// <summary>Hides/shows a remote player's walking model while they're driving a vehicle.</summary>
        public static void SetDriving(string playerId, bool driving)
        {
            if (!_players.TryGetValue(playerId, out var go) || go == null) return;
            var model = go.transform.Find("Model");
            if (model != null) model.gameObject.SetActive(!driving);
            var cap = go.transform.Find("Capsule");      // capsule-fallback case
            if (cap != null) cap.gameObject.SetActive(!driving);
        }

        private static float _nextIgnoreRefresh;
        /// <summary>HOST: keep remote-avatar colliders from physically shoving vehicles WITHOUT
        /// removing them (Gley needs a solid collider on the player layer to detect players by
        /// raycast).  Because avatars share the local player's layer we can't exclude the pair via
        /// the layer matrix, so we ignore collisions per collider-pair (each avatar × every vehicle
        /// collider), re-applied on a short interval to cover freshly-spawned avatars/vehicles and
        /// post-load rebuilds (the ignore resets when a collider is destroyed/recreated).</summary>
        public static void TickVehicleCollisionIgnores()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected) return;
#if BAMP_DEV
            TickAvatarSenseProbe();   // host-side Option-B diagnostic (self-gated host-only + 1 Hz)
#endif
            if (UnityEngine.Time.unscaledTime < _nextIgnoreRefresh) return;
            _nextIgnoreRefresh = UnityEngine.Time.unscaledTime + 1.5f;
            try
            {
                // Nobody should physically push a car they don't authoritatively control. The avatar
                // shares the local player's layer (needed for Gley raycast detection), so a layer-
                // matrix exclusion can't single it out — we ignore each relevant collider PAIR.

                // (1) The LOCAL player must not shove GHOSTS (other players' cars). BOTH roles — this
                //     was the missing case: a client could walk into a ghost and the snap-back made
                //     it "zoom". Their OWN real cars are deliberately left collidable.
                var ghostCols = VehicleManager.AllGhostColliders();
                var lc = PlayerHelper.PlayerController?.Character;
                if (lc != null && ghostCols.Count > 0)
                {
                    var playerCols = lc.GetComponentsInChildren<Collider>(true);
                    for (int pi = 0; pi < playerCols.Length; pi++)
                    {
                        if (playerCols[pi] == null) continue;
                        for (int i = 0; i < ghostCols.Count; i++)
                            if (ghostCols[i] != null) UnityEngine.Physics.IgnoreCollision(playerCols[pi], ghostCols[i], true);
                    }
                }

                // (2) HOST only: remote avatars must not shove ANY vehicle (the host's real cars are
                //     dynamic + drivable; avatars carry a SOLID collider for Gley detection).
                if (MPServer.IsRunning && _players.Count > 0)
                {
                    var vehCols = VehicleManager.AllVehicleColliders();
                    // Gley TRAFFIC cars are NOT in AllVehicleColliders — gather their solid colliders too, so the
                    //   avatar (kept SOLID so Gley's raycast still senses it and brakes — IgnoreCollision doesn't
                    //   affect raycasts) doesn't physically SHOVE live traffic. THIS was the real push: the
                    //   host-side avatar shoving traffic, then the host syncing the shoved positions down.
                    var trafCols = new List<Collider>();
                    try
                    {
                        var tlist = GleyTrafficSystem.TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
                        if (tlist != null)
                            for (int i = 0; i < tlist.Count; i++)
                            {
                                var tc = tlist[i] as Component;
                                if (tc == null || !tc.gameObject.activeInHierarchy) continue;
                                foreach (var col in tc.GetComponentsInChildren<Collider>(true))
                                    if (col != null && !col.isTrigger) trafCols.Add(col);
                            }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[RemotePlayer] traffic-ignore gather: {ex.Message}"); }

                    foreach (var kv in _players)
                    {
                        if (kv.Value == null) continue;
                        var ac = kv.Value.GetComponentInChildren<Collider>();   // the root capsule
                        if (ac == null) continue;
                        for (int i = 0; i < vehCols.Count; i++)
                            if (vehCols[i] != null) UnityEngine.Physics.IgnoreCollision(ac, vehCols[i], true);
                        for (int i = 0; i < trafCols.Count; i++)
                            UnityEngine.Physics.IgnoreCollision(ac, trafCols[i], true);   // don't shove live Gley traffic
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[RemotePlayer] collision-ignore refresh: {ex.Message}"); }
        }

#if BAMP_DEV
        // DIAG:INVESTIGATION(traffic-stop, Option B) — does the HOST's Gley traffic SENSE + slow near each
        //   remote avatar? (User: cars drive into the client instead of stopping.) The avatar carries a
        //   SOLID collider on the host-player layer (RemotePlayerManager spawn), so Gley's raycast SHOULD
        //   detect it. Logs the avatar's layer + solid state + the nearest active traffic car's distance and
        //   speed. Cars that keep approaching to d≈0 = host NOT sensing the avatar (fix detectability);
        //   cars that hold off / slow = host IS braking (fix the client sync that isn't reflecting it).
        private static float _nextAvSense;
        private static void TickAvatarSenseProbe()
        {
            if (!MPServer.IsRunning) return;
            if (UnityEngine.Time.unscaledTime < _nextAvSense) return;
            _nextAvSense = UnityEngine.Time.unscaledTime + 1f;
            try
            {
                var list = GleyTrafficSystem.TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
                foreach (var kv in _players)
                {
                    var av = kv.Value;
                    if (av == null) continue;
                    var ac = av.GetComponentInChildren<Collider>();
                    UnityEngine.Vector3 ap = av.transform.position;
                    float nd = 999f, nspeed = -1f;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        {
                            var c = list[i] as Component;
                            if (c == null || !c.gameObject.activeInHierarchy) continue;
                            float d = (c.transform.position - ap).magnitude;
                            if (d < nd) { nd = d; var rb = c.GetComponentInChildren<Rigidbody>(); nspeed = rb != null ? rb.velocity.magnitude : -1f; }
                        }
                    Plugin.Logger.LogInfo($"[AvSense] avatar '{kv.Key}' layer={av.layer}({UnityEngine.LayerMask.LayerToName(av.layer)}) colSolid={(ac != null && !ac.isTrigger)} nearestCar d={nd:F1} speed={nspeed:F1}");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[AvSense] {ex.Message}"); }
        }
#endif

        /// <summary>Every known appearance — used by the host to broadcast the full set.</summary>
        public static List<PlayerAppearancePayload> GetAllAppearances() =>
            new List<PlayerAppearancePayload>(_appearances.Values);

        public static string Summary(PlayerAppearancePayload dto) =>
            $"{dto.Gender} — " + string.Join(", ", dto.Variants.Select(kv => $"{kv.Key}={kv.Value}"));

        /// <summary>FULL change-detection signature: variants + colors + blends.
        /// Summary() alone missed color/blend changes — the corrected tints
        /// after load (and re-colored outfits) never re-sent (2026-06-11).</summary>
        public static string FullSig(PlayerAppearancePayload dto)
        {
            var sb = new System.Text.StringBuilder(dto.Gender);
            foreach (var kv in dto.Variants.OrderBy(k => k.Key))
                sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
            foreach (var c in dto.Colors.OrderBy(c => c.Cat).ThenBy(c => c.Mat).ThenBy(c => c.Prop))
                sb.Append('|').Append(c.Cat).Append(c.Mat).Append(c.Prop)
                  .Append((int)(c.R * 255f)).Append(',').Append((int)(c.G * 255f)).Append(',')
                  .Append((int)(c.B * 255f)).Append(',').Append((int)(c.A * 255f));
            foreach (var b in dto.Blends.OrderBy(b => b.Cat).ThenBy(b => b.Shape))
                sb.Append('|').Append(b.Cat).Append(b.Shape).Append((int)b.Weight);
            foreach (var f in dto.Floats.OrderBy(f => f.Cat).ThenBy(f => f.Mat).ThenBy(f => f.Prop))
                sb.Append('|').Append(f.Cat).Append(f.Mat).Append(f.Prop).Append((int)(f.V * 1000f));
            return sb.ToString();
        }

        private static void ApplyAppearance(GameObject root, PlayerAppearancePayload dto)
        {
            try
            {
                var model = root.transform.Find("Model");
                if (model == null) return;          // capsule fallback — nothing to dress
                var maleT = model.Find("Male");
                var femT  = model.Find("Female");
                if (maleT == null || femT == null) return;

                bool male = dto.Gender == "Male";
                maleT.gameObject.SetActive(male);
                femT.gameObject.SetActive(!male);

                var genderT = male ? maleT : femT;
                for (int ci = 0; ci < genderT.childCount; ci++)
                {
                    var cat = genderT.GetChild(ci);
                    if (!dto.Variants.TryGetValue(cat.name, out var wanted)) continue;
                    for (int vi = 0; vi < cat.childCount; vi++)
                    {
                        var v = cat.GetChild(vi);
                        v.gameObject.SetActive(v.name == wanted);
                    }
                }
                ApplyColors(genderT, dto);
                ApplyBlendShapes(genderT, dto);
                Plugin.Logger.LogInfo(
                    $"[Appearance] Applied to '{dto.PlayerId}': {Summary(dto)} " +
                    $"(+{dto.Colors.Count} colours, +{dto.Blends.Count} morphs)");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Appearance] ApplyAppearance '{dto.PlayerId}': {ex.Message}");
            }
        }
        // ── Animator state sync ───────────────────────────────────────────────
        //
        // Generic full-animator mirror: instead of enumerating animations we
        // network the animator's INPUT state.  Floats/ints are read in full each
        // tick; bools as the list of true indices; triggers are caught by a
        // Harmony patch on Animator.SetTrigger and sent as one-off events.

        private static Animator? _localAnim;
        private static Dictionary<int, int>?    _triggerHashToIndex;  // nameHash → index
        private static Dictionary<string, int>? _triggerNameToIndex;  // name     → index

        /// <summary>The local player's character Animator; null until the character exists.</summary>
        public static Animator? GetLocalAnimator()
        {
            if (_localAnim != null) return _localAnim;
            try
            {
                var model = PlayerHelper.PlayerController?.Character?.transform.Find("Model");
                if (model == null) return null;
                var animComp = model.GetComponent(typeof(Animator));
                _localAnim = animComp != null ? animComp as Animator : null;
                if (_localAnim != null) BuildTriggerMaps(_localAnim);
            }
            catch { }
            return _localAnim;
        }

        private static void BuildTriggerMaps(Animator anim)
        {
            _triggerHashToIndex = new Dictionary<int, int>();
            _triggerNameToIndex = new Dictionary<string, int>();
            var ps = anim.parameters;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].type == AnimatorControllerParameterType.Trigger)
                {
                    _triggerHashToIndex[ps[i].nameHash] = i;
                    _triggerNameToIndex[ps[i].name]     = i;
                }
            }
        }

        /// <summary>Reads the local animator's float/bool/int state into a position payload.</summary>
        public static void ReadLocalAnimState(PlayerPositionPayload p)
        {
            try
            {
                var anim = GetLocalAnimator();
                if (anim == null) return;
                var ps = anim.parameters;
                for (int i = 0; i < ps.Length; i++)
                {
                    switch (ps[i].type)
                    {
                        case AnimatorControllerParameterType.Float:
                            p.AnimF[i] = anim.GetFloat(ps[i].name);
                            break;
                        case AnimatorControllerParameterType.Int:
                            p.AnimI[i] = anim.GetInteger(ps[i].name);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            bool bv = anim.GetBool(ps[i].name);
                            if (bv) p.AnimB.Add(i);
                            // [CarryProbe] 2026-06-12: box-carry pose/prop never
                            // shows remotely — log every Holding* transition +
                            // scan for the held prop so one carry run names the
                            // mechanism (bool vs layer vs IK vs prop-attach).
                            if (ps[i].name.IndexOf("olding", StringComparison.Ordinal) >= 0)
                                ProbeHoldingChange(anim, ps[i].name, bv);
                            break;
                    }
                }
                // Layer weights — the game's scripts drive these (e.g. the
                // upper-body hold layer fades in when pushing a cart); the
                // clone has no scripts, so they must be mirrored explicitly.
                int lc = anim.layerCount;
                for (int l = 0; l < lc && l < 8; l++)
                    p.LayerW.Add(anim.GetLayerWeight(l));

                // Held prop (HandContent skeleton node) — first active child's
                // cleaned name rides the payload; "" = empty hands.
                try
                {
                    if (_localHandContent == null)
                        _localHandContent = FindDeep(anim.transform.root, "HandContent");
                    if (_localHandContent != null)
                    {
                        for (int c = 0; c < _localHandContent.childCount; c++)
                        {
                            var ch = _localHandContent.GetChild(c);
                            // activeInHierarchy, NOT activeSelf: 0.11 parks a prop
                            // under an inactive parent — activeSelf stayed true and
                            // we broadcast a box the player wasn't holding.
                            if (ch == null || !ch.gameObject.activeInHierarchy) continue;
                            p.Held = CleanPropName(ch.name);
                            // Exact local placement — baskets hang off-axis.
                            var lp = ch.localPosition; var le = ch.localEulerAngles;
                            p.HeldT.Add(lp.x); p.HeldT.Add(lp.y); p.HeldT.Add(lp.z);
                            p.HeldT.Add(le.x); p.HeldT.Add(le.y); p.HeldT.Add(le.z);
                            break;
                        }
                    }
                }
                catch { _localHandContent = null; }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[AnimSync] ReadLocalAnimState: {ex.Message}");
            }
        }

        // ── Held-prop sync (CarryProbe verdict 2026-06-12) ────────────────────
        private static Transform? _localHandContent;

        /// <summary>The LOCAL player's HandContent bone (hand-IK anchor space
        /// for held items — see MPHandIk.FillPayload).</summary>
        internal static Transform? LocalHandContent => _localHandContent;
        private static readonly Dictionary<string, GameObject> _heldTemplates = new();
        private static readonly Dictionary<string, string> _heldApplied = new();   // playerId → prop name
        private static readonly Dictionary<string, GameObject> _heldProps = new(); // playerId → live prop clone

        private static string CleanPropName(string n)
        {
            int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            if (i >= 0) n = n.Substring(0, i);
            return n.Trim();
        }

        private static Transform? FindDeep(Transform root, string name)
        {
            try
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == name) return t;
            }
            catch { }
            return null;
        }

        /// <summary>Mirror the sender's held prop into the remote avatar's
        /// HandContent node.  Templates are inactive scene instances of the
        /// same prefab (e.g. flatbed placeholder boxes), cloned + stripped to
        /// visuals.  Keyed on the prop NAME, not animator flags — covers boxes,
        /// baskets and anything else the game ever puts in hands.</summary>
        private static void ApplyHeldProp(GameObject avatar, string playerId, string held)
        {
            held ??= "";
            if (_heldApplied.TryGetValue(playerId, out var cur) && cur == held) return;
            _heldApplied[playerId] = held;
            try
            {
                var hand = FindDeep(avatar.transform, "HandContent");
                if (hand == null) return;                       // capsule-fallback clone
                for (int i = hand.childCount - 1; i >= 0; i--)
                {
                    var c = hand.GetChild(i);
                    if (c != null && c.name.StartsWith("BAMP_Held_"))
                        UnityEngine.Object.Destroy(c.gameObject);
                }
                _heldProps.Remove(playerId);
                if (held.Length == 0)
                {
                    Plugin.Logger.LogInfo($"[Carry] '{playerId}' hands empty.");
                    return;
                }
                var template = FindHeldTemplate(held);
                if (template == null)
                {
                    Plugin.Logger.LogWarning($"[Carry] no scene template for held prop '{held}' — prop not shown.");
                    return;
                }
                var prop = UnityEngine.Object.Instantiate(template);
                prop.name = "BAMP_Held_" + held;
                StripToVisual(prop);
                prop.transform.SetParent(hand, false);
                prop.transform.localPosition = Vector3.zero;
                prop.transform.localRotation = Quaternion.identity;
                prop.SetActive(true);
                _heldProps[playerId] = prop;   // local transform mirrored per packet
                Plugin.Logger.LogInfo($"[Carry] '{playerId}' holding '{held}' — prop attached.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Carry] {ex.Message}"); }
        }

        private static GameObject? FindHeldTemplate(string name)
        {
            if (_heldTemplates.TryGetValue(name, out var t) && t != null) return t;
            try
            {
                // Inactive instances included — the scene keeps placeholder
                // copies (e.g. vehicle-bed boxes) of the same prefabs.
                var all = Resources.FindObjectsOfTypeAll(typeof(Transform));
                if (all != null)
                    foreach (var o in all)
                    {
                        var tr = o as Transform;
                        if (tr == null) continue;
                        if (CleanPropName(tr.name) != name) continue;
                        if (tr.name.StartsWith("BAMP_Held_")) continue;   // never template off our own clones
                        _heldTemplates[name] = tr.gameObject;
                        return tr.gameObject;
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Carry] template scan: {ex.Message}"); }
            return null;
        }

        /// <summary>Strip a cloned prop to pure visuals (no behaviours,
        /// colliders or physics — same principle as ghost vehicles).</summary>
        private static void StripToVisual(GameObject go)
        {
            try
            {
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    string tn = "";
                    try { tn = c.GetType().Name; } catch { continue; }
                    if (tn == "Transform" || tn == "RectTransform"
                        || tn.Contains("MeshFilter") || tn.Contains("MeshRenderer")
                        || tn.Contains("SkinnedMeshRenderer")) continue;
                    try { UnityEngine.Object.Destroy(c); } catch { }
                }
            }
            catch { }
        }

        // ── [CarryProbe] held-prop investigation (2026-06-12) ─────────────────
        private static readonly HashSet<string> _holdingOn = new();

        private static void ProbeHoldingChange(Animator anim, string name, bool on)
        {
            bool was = _holdingOn.Contains(name);
            if (on == was) return;
            if (on) _holdingOn.Add(name); else _holdingOn.Remove(name);
            Plugin.Logger.LogInfo($"[CarryProbe] local '{name}' → {on}");
            if (!on) return;
            try
            {
                // Name the held prop: anything box/crate/holding-ish under the
                // character root, with its full path + active layer weights.
                var root = anim.transform.root;
                var sb = new System.Text.StringBuilder("[CarryProbe] prop scan:");
                int found = 0;
                foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                {
                    var n = tr.name;
                    if (n.IndexOf("box", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("crate", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("hold", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string path = n;
                    var pT = tr.parent;
                    int depth = 0;
                    while (pT != null && pT != root && depth++ < 8) { path = pT.name + "/" + path; pT = pT.parent; }
                    sb.Append($" '{path}'(active={tr.gameObject.activeInHierarchy})");
                    if (++found >= 10) break;
                }
                if (found == 0) sb.Append(" (none matched)");
                var lw = new System.Text.StringBuilder(" layers:");
                for (int l = 0; l < anim.layerCount && l < 8; l++) lw.Append($" {l}={anim.GetLayerWeight(l):F2}");
                Plugin.Logger.LogInfo(sb.ToString() + lw);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[CarryProbe] {ex.Message}"); }
        }

        /// <summary>Resolves an Animator.SetTrigger(int hash) argument to a parameter index, or -1.</summary>
        public static int ResolveTriggerIndex(int nameHash)
            => _triggerHashToIndex != null && _triggerHashToIndex.TryGetValue(nameHash, out var i) ? i : -1;

        /// <summary>Resolves an Animator.SetTrigger(string name) argument to a parameter index, or -1.</summary>
        public static int ResolveTriggerIndex(string name)
            => _triggerNameToIndex != null && _triggerNameToIndex.TryGetValue(name, out var i) ? i : -1;

        /// <summary>Routes a local trigger fire onto the network (host broadcasts, client sends).</summary>
        public static void SendLocalTrigger(int paramIndex)
        {
            if (paramIndex < 0) return;
            if (MPServer.IsRunning)        MPServer.BroadcastAnimTrigger(paramIndex);
            else if (MPClient.IsConnected) MPClient.SendAnimTrigger(paramIndex);
        }

        // ── Live animation-parameter probe ────────────────────────────────────
        //
        // Samples the local player's Animator every frame and logs each
        // parameter the FIRST time it changes from its baseline.  Play a varied
        // session (walk, run, idle, sit on a bench, enter a car…) to discover
        // the MINIMAL set of parameters that actually drive animation — that set
        // is what we network for exact remote-player animation (instead of the
        // current derive-speed-from-position estimate, which causes the slight
        // deviation).  Logging only; zero network cost.

        private static Animator? _animProbeAnim;
        private static bool _animProbeMissing;
        private static readonly Dictionary<string, float> _animBaseline = new();
        private static readonly HashSet<string> _animChanged = new();

        // ── Colour capture / apply ────────────────────────────────────────────
        //
        // Generic: capture every Color shader property on every material of a
        // variant's SkinnedMeshRenderer, network them, and re-apply with SetColor.
        // This reproduces any look the player chose (skin tone, hair, clothing
        // tints) without needing to know which shader property means what.

        private static void CaptureColors(PlayerAppearancePayload dto, string cat, GameObject variant)
        {
            try
            {
                var smrComp = variant.GetComponent(typeof(SkinnedMeshRenderer));
                var smr = smrComp != null ? smrComp as SkinnedMeshRenderer : null;
                if (smr == null) return;
                var mats = smr.sharedMaterials;
                var mpb = new MaterialPropertyBlock();
                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null) continue;
                    // The VISIBLE tint may live in a MaterialPropertyBlock —
                    // mat.GetColor alone returns the base/default color, which
                    // is why ghost outfits had the right garments but wrong
                    // colors (2026-06-11).
                    bool hasMpb = false;
                    try { smr.GetPropertyBlock(mpb, m); hasMpb = !mpb.isEmpty; } catch { }
                    var sh = mat.shader;
                    int n = sh.GetPropertyCount();
                    for (int p = 0; p < n; p++)
                    {
                        var pt = sh.GetPropertyType(p);
                        string pn = sh.GetPropertyName(p);
                        if (pt == UnityEngine.Rendering.ShaderPropertyType.Color)
                        {
                            var c = mat.GetColor(pn);
                            try { if (hasMpb && mpb.HasColor(pn)) c = mpb.GetColor(pn); } catch { }
                            dto.Colors.Add(new ColorEntry
                            {
                                Cat = cat, Mat = m, Prop = pn,
                                R = c.r, G = c.g, B = c.b, A = c.a,
                            });
                        }
                        else if (pt == UnityEngine.Rendering.ShaderPropertyType.Float
                              || pt == UnityEngine.Rendering.ShaderPropertyType.Range)
                        {
                            // The clothes DYE is a float (texture-array slice
                            // index on SH_CharacterClothes*), not a color.
                            float v = mat.GetFloat(pn);
                            try { if (hasMpb && mpb.HasFloat(pn)) v = mpb.GetFloat(pn); } catch { }
                            dto.Floats.Add(new FloatEntry { Cat = cat, Mat = m, Prop = pn, V = v });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Colors] CaptureColors '{cat}': {ex.Message}");
            }
        }

        private static void CaptureBlendShapes(PlayerAppearancePayload dto, string cat, GameObject variant)
        {
            try
            {
                var smrComp = variant.GetComponent(typeof(SkinnedMeshRenderer));
                var smr = smrComp != null ? smrComp as SkinnedMeshRenderer : null;
                if (smr == null || smr.sharedMesh == null) return;
                var mesh = smr.sharedMesh;
                int bc = mesh.blendShapeCount;
                for (int b = 0; b < bc; b++)
                {
                    dto.Blends.Add(new BlendEntry
                    {
                        Cat = cat,
                        Shape = mesh.GetBlendShapeName(b),
                        Weight = smr.GetBlendShapeWeight(b),
                    });
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Morphs] CaptureBlendShapes '{cat}': {ex.Message}");
            }
        }

        /// <summary>Re-applies captured blendshape morphs onto a remote player's cloned model.</summary>
        private static void ApplyBlendShapes(Transform genderT, PlayerAppearancePayload dto)
        {
            if (dto.Blends.Count == 0) return;
            try
            {
                foreach (var grp in dto.Blends.GroupBy(e => e.Cat))
                {
                    if (!dto.Variants.TryGetValue(grp.Key, out var wanted)) continue;
                    var cat = genderT.Find(grp.Key);
                    if (cat == null) continue;
                    var v = cat.Find(wanted);
                    if (v == null) continue;

                    var smrComp = v.gameObject.GetComponent(typeof(SkinnedMeshRenderer));
                    var smr = smrComp != null ? smrComp as SkinnedMeshRenderer : null;
                    if (smr == null || smr.sharedMesh == null) continue;
                    var mesh = smr.sharedMesh;
                    foreach (var e in grp)
                    {
                        int idx = mesh.GetBlendShapeIndex(e.Shape);
                        if (idx >= 0) smr.SetBlendShapeWeight(idx, e.Weight);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Morphs] ApplyBlendShapes '{dto.PlayerId}': {ex.Message}");
            }
        }

        /// <summary>Re-applies captured colours onto a remote player's cloned model.</summary>
        private static void ApplyColors(Transform genderT, PlayerAppearancePayload dto)
        {
            if (dto.Colors.Count == 0) return;
            try
            {
                // Group entries by category so we touch each variant's materials once.
                foreach (var grp in dto.Colors.GroupBy(e => e.Cat))
                {
                    if (!dto.Variants.TryGetValue(grp.Key, out var wanted)) continue;
                    var cat = genderT.Find(grp.Key);
                    if (cat == null) continue;
                    var v = cat.Find(wanted);
                    if (v == null) continue;

                    var smrComp = v.gameObject.GetComponent(typeof(SkinnedMeshRenderer));
                    var smr = smrComp != null ? smrComp as SkinnedMeshRenderer : null;
                    if (smr == null) continue;

                    // .materials forces per-renderer instanced copies so we don't
                    // tint the shared asset (which would recolour the local player).
                    var mats = smr.materials;
                    foreach (var e in grp)
                    {
                        if (e.Mat < 0 || e.Mat >= mats.Length) continue;
                        var mat = mats[e.Mat];
                        if (mat == null) continue;
                        mat.SetColor(e.Prop, new Color(e.R, e.G, e.B, e.A));
                    }
                }

                // Float properties — the dye index travels here.
                foreach (var grp in dto.Floats.GroupBy(e => e.Cat))
                {
                    if (!dto.Variants.TryGetValue(grp.Key, out var wanted)) continue;
                    var cat = genderT.Find(grp.Key);
                    var v = cat != null ? cat.Find(wanted) : null;
                    if (v == null) continue;
                    var smrComp = v.gameObject.GetComponent(typeof(SkinnedMeshRenderer));
                    var smr = smrComp != null ? smrComp as SkinnedMeshRenderer : null;
                    if (smr == null) continue;
                    var mats = smr.materials;
                    foreach (var e in grp)
                    {
                        if (e.Mat < 0 || e.Mat >= mats.Length) continue;
                        var mat = mats[e.Mat];
                        if (mat == null) continue;
                        try { mat.SetFloat(e.Prop, e.V); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Colors] ApplyColors '{dto.PlayerId}': {ex.Message}");
            }
        }

        // ── Private ───────────────────────────────────────────────────────────

        private static readonly string[] _killScripts =
        {
            "RigBuilder", "BoneRenderer", "StepTrigger", "AnimationObjectSpawner",
            "AnimationTriggerEvents", "SkinnedMeshCombiner"
        };

        private static GameObject SpawnRemotePlayer(string playerId)
        {
            // Root GO — the position-sync mover drives this; the model is a child.
            var root = new GameObject($"BAMP_Player_{playerId}");
            _players[playerId] = root;          // register early — re-spawn guard

            // #4 — make remote players visible to AI traffic so cars stop for
            // them just like they do for the host's local player.  Gley
            // raycasts against `playerLayer`; if a remote player is on the same
            // layer as the local character (with a real collider on it), every
            // existing "stop for player" rule applies for free.
            //
            // CRITICAL: only do this on the HOST.  Traffic only runs on the
            // host, so only the host needs to detect remote players as
            // obstacles.  Doing this on the CLIENT puts an extra collider on
            // the Player layer, which (per backlog observation 2026-05-20)
            // breaks the client's building-entry chain — its physics query
            // for "the player at this entrance" returns more than expected
            // and the entry coroutine bails on the player-identification
            // check, never invoking DelayedEnterBuildingActions.
            //
            // On the client, the remote player is visual-only: position +
            // animations, no physics presence.
            if (MPServer.IsRunning)
            {
                try
                {
                    int playerLayer = 0;
                    var localChar = PlayerHelper.PlayerController?.Character?.gameObject;
                    if (localChar != null) playerLayer = localChar.layer;
                    root.layer = playerLayer;

                    var col = root.AddComponent<CapsuleCollider>();
                    col.height   = 1.65f;                       // match the real client capsule (was 1.8)
                    col.radius   = 0.25f;                       // match the real client capsule (was 0.4 — needlessly large)
                    col.center   = new Vector3(0f, 0.825f, 0f); // half-height (was 0.9 for the taller capsule)
                    // SOLID, not a trigger.  Gley detects players by RAYCAST and its query IGNORES
                    // triggers, so a trigger makes traffic run remote players over (observed
                    // 2026-06-15).  Keep the collider solid (traffic still stops) and instead stop
                    // it SHOVING vehicles with per-pair Physics.IgnoreCollision against every vehicle
                    // collider — a layer-matrix exclusion can't be used because the avatar shares
                    // the LOCAL player's layer, which must keep colliding with vehicles.  See
                    // TickVehicleCollisionIgnores() (refreshed on a short interval, host only).
                    col.isTrigger = false;

                    var rb = root.AddComponent<Rigidbody>();
                    rb.isKinematic = true;
                    rb.useGravity  = false;
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

                    Plugin.Logger.LogInfo(
                        $"[RemotePlayer] '{playerId}' collider on layer {playerLayer} ({LayerMask.LayerToName(playerLayer)}) [host].");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[RemotePlayer] Collider setup failed for '{playerId}': {ex.Message}");
                }
            }
            else
            {
                Plugin.Logger.LogInfo($"[RemotePlayer] '{playerId}' visual-only (no collider) — client side.");
            }

            // Clone the local player's character model so remote players get a real
            // animated body.  Falls back to a capsule if the clone fails.
            GameObject? model = null;
            try
            {
                var src = PlayerHelper.PlayerController?.Character?.transform.Find("Model");
                if (src != null)
                {
                    model = UnityEngine.Object.Instantiate(src.gameObject, root.transform);
                    model.name = "Model";
                    model.transform.localPosition = Vector3.zero;
                    model.transform.localRotation = Quaternion.identity;
                    StripModelScripts(model);
                    VehicleManager.StripCameras(model);   // stowaway cameras hijack the cursor pick ray
                }
                else
                {
                    Plugin.Logger.LogWarning("[RemotePlayer] Local character 'Model' not found — using capsule.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[RemotePlayer] Model clone failed for '{playerId}': {ex.Message}");
                if (model != null) { UnityEngine.Object.Destroy(model); model = null; }
            }

            if (model == null)
            {
                var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                cap.name = "Capsule";
                cap.transform.SetParent(root.transform, false);
                var col = cap.GetComponent<CapsuleCollider>();
                if (col != null) UnityEngine.Object.Destroy(col);
                var rend = cap.GetComponent<Renderer>();
                if (rend != null) rend.material.color = new Color(0.3f, 0.6f, 1f);
            }

            // Smooth-movement + animation driver
            var mover = root.AddComponent<RemotePlayerMover>();
            if (model != null)
            {
                var animComp = model.GetComponent(typeof(Animator));
                mover.Anim = animComp != null ? animComp as Animator : null;

                // Dress the model if we already received this player's appearance.
                if (_appearances.TryGetValue(playerId, out var ap))
                    ApplyAppearance(root, ap);
            }

            // Name label via world-space Canvas + TextMeshProUGUI (above the head)
            try
            {
                var labelGO = new GameObject("BAMP_Label");
                labelGO.transform.SetParent(root.transform, false);
                labelGO.transform.localPosition = new Vector3(0f, 2.0f, 0f);

                var canvas = labelGO.AddComponent<Canvas>();
                canvas.renderMode  = RenderMode.WorldSpace;
                canvas.sortingOrder = 10;

                var rt = labelGO.GetComponent<RectTransform>();
                rt.sizeDelta  = new Vector2(300f, 60f);
                rt.localScale = new Vector3(0.01f, 0.01f, 0.01f);

                var tmp = labelGO.AddComponent<TextMeshProUGUI>();
                tmp.text      = ResolveDisplayName(playerId);
                tmp.fontSize  = 48f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color     = Color.white;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[RemotePlayer] Label creation failed for '{playerId}': {ex.Message}");
            }

            Plugin.Logger.LogInfo(
                $"[RemotePlayer] Spawned '{playerId}' ({(model != null ? "character model" : "capsule fallback")})");
            return root;
        }

        /// <summary>Removes gameplay scripts from a cloned model — keeps Animator + meshes + skeleton.</summary>
        private static void StripModelScripts(GameObject model)
        {
            try
            {
                var comps = model.GetComponents(typeof(Component));
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    if (System.Array.IndexOf(_killScripts, c.GetType().Name) >= 0)
                        UnityEngine.Object.Destroy(c);
                }
                // The cloned model is VISUAL-ONLY: strip every collider + rigidbody it carries
                // (bone / ragdoll physics) so it can NEVER impart force to vehicles or the world.
                // The host-only root capsule is handled separately (TickVehicleCollisionIgnores);
                // this catches any physics hiding deeper in the cloned character hierarchy.
                foreach (var rb in model.GetComponentsInChildren<Rigidbody>(true))
                    if (rb != null) UnityEngine.Object.Destroy(rb);
                foreach (var col in model.GetComponentsInChildren<Collider>(true))
                    if (col != null) UnityEngine.Object.Destroy(col);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[RemotePlayer] StripModelScripts: {ex.Message}");
            }
        }
    }
}
