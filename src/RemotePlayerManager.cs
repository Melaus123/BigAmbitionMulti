using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Helpers;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;

namespace BigAmbitionsMP
{
    // ── Smooth-movement MonoBehaviour ─────────────────────────────────────────

    /// <summary>
    /// Attached to each remote-player capsule.  Lerps toward the latest received
    /// position/rotation every frame so movement looks smooth rather than teleporting.
    /// Must be registered with ClassInjector before use.
    /// </summary>
    public class RemotePlayerMover : MonoBehaviour
    {
        public RemotePlayerMover(IntPtr ptr) : base(ptr) { }

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

        private void LateUpdate()
        {
            if (RideAttach == null || _ikT == null || Anim == null) return;
            try
            {
                if (!_ikHumanChecked) { _ikHumanChecked = true; _ikHuman = Anim.isHuman; }
                if (!_ikHuman) return;

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
                MPHandIk.SolveArm(Anim, true,  RideAttach.TransformPoint(_ikL), Vector3.down);
                MPHandIk.SolveArm(Anim, false, RideAttach.TransformPoint(_ikR), Vector3.down);
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
            if (_labelTransform != null && Camera.main != null)
                _labelTransform.rotation = Camera.main.transform.rotation;
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
        // CLAUDE-DIAGNOSTIC — master kill-switch flag.  When false, SpawnOrUpdate
        // skips spawn AND skips position updates (so existing destroyed players
        // don't get re-spawned).  Used by the F4 master toggle.
        public static bool ClientSpawnEnabled { get; set; } = true;

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
            if (!ClientSpawnEnabled) return;          // CLAUDE-DIAGNOSTIC kill-switch

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
        }

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
            _localAnim = null;          // re-fetched (with fresh trigger maps) next game
            _triggerHashToIndex = null;
            _triggerNameToIndex = null;
            VehicleManager.DespawnAll();
            TrafficSync.DespawnAllGhosts();
            Plugin.Logger.LogInfo("[RemotePlayer] All remote players removed.");
        }

        // ── Character discovery probe ─────────────────────────────────────────
        //
        // One-time inspection of the local player's character so we can later
        // clone its visual model (SkinnedMeshRenderers + Animator + skeleton) to
        // give remote players real animated bodies instead of capsules.
        // Logs: the GameObject hierarchy + components, the Animator's controller
        // and parameters, and every SkinnedMeshRenderer.

        private static bool _characterProbed;

        public static void ProbeLocalCharacter()
        {
            if (_characterProbed) return;
            try
            {
                var pc = PlayerHelper.PlayerController;
                if (pc == null) return;                  // not ready — retry next call
                var character = pc.Character;
                if (character == null) return;
                _characterProbed = true;

                Transform root = character.transform;
                Plugin.Logger.LogInfo(
                    $"[RemotePlayer] === Character probe: root '{root.name}', " +
                    $"parent '{(root.parent != null ? root.parent.name : "(none)")}' ===");
                DumpHierarchy(root, 0);

                // ── Animator ─────────────────────────────────────────────────
                var animComp = root.GetComponentInChildren(Il2CppType.Of<Animator>());
                var animator = animComp != null ? animComp.TryCast<Animator>() : null;
                if (animator == null)
                {
                    Plugin.Logger.LogWarning("[RemotePlayer] No Animator found under the character.");
                }
                else
                {
                    var rac = animator.runtimeAnimatorController;
                    Plugin.Logger.LogInfo(
                        $"[RemotePlayer] Animator on '{animator.gameObject.name}' — " +
                        $"controller '{(rac != null ? rac.name : "(null)")}', " +
                        $"avatar '{(animator.avatar != null ? animator.avatar.name : "(null)")}', " +
                        $"isHuman={animator.isHuman}");
                    var ps = animator.parameters;
                    Plugin.Logger.LogInfo($"[RemotePlayer] Animator parameters ({ps.Length}):");
                    for (int i = 0; i < ps.Length; i++)
                        Plugin.Logger.LogInfo(
                            $"[RemotePlayer]   {ps[i].name}  ({ps[i].type})  " +
                            $"def f={ps[i].defaultFloat} b={ps[i].defaultBool} i={ps[i].defaultInt}");
                }

                // ── SkinnedMeshRenderers ─────────────────────────────────────
                var smrs = root.GetComponentsInChildren(Il2CppType.Of<SkinnedMeshRenderer>(), true);
                Plugin.Logger.LogInfo($"[RemotePlayer] SkinnedMeshRenderers ({smrs.Length}):");
                for (int i = 0; i < smrs.Length; i++)
                {
                    var smr = smrs[i].TryCast<SkinnedMeshRenderer>();
                    if (smr != null)
                        Plugin.Logger.LogInfo(
                            $"[RemotePlayer]   '{smr.gameObject.name}' mesh='" +
                            $"{(smr.sharedMesh != null ? smr.sharedMesh.name : "(null)")}'");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[RemotePlayer] ProbeLocalCharacter: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void DumpHierarchy(Transform t, int depth)
        {
            string indent = new string(' ', depth * 3);
            string comps;
            try
            {
                var cs = t.gameObject.GetComponents(Il2CppType.Of<Component>());
                var names = new List<string>();
                for (int i = 0; i < cs.Length; i++)
                    if (cs[i] != null) names.Add(cs[i].GetIl2CppType().Name);
                comps = string.Join(", ", names);
            }
            catch { comps = "(components unavailable)"; }

            Plugin.Logger.LogInfo($"[RemotePlayer]   {indent}{t.name}  [{comps}]");

            if (depth >= 4) return;          // cap depth — skeleton bones go very deep
            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(t.GetChild(i), depth + 1);
        }

        // ── Appearance discovery probe ────────────────────────────────────────
        //
        // The character's look = gender (Male/Female sub-tree) + which variant
        // GameObject is active per category (Hair, Torso, Legs…) + colours.
        // This probe logs the AppearanceSetter API, the per-category active
        // variant state, and the AppearanceElementVariant members — the data
        // needed to sync each remote player's real appearance.

        private static bool _appearanceProbed;

        public static void ProbeAppearance()
        {
            if (_appearanceProbed) return;
            try
            {
                var pc = PlayerHelper.PlayerController;
                if (pc == null) return;                  // not ready — retry next call
                var character = pc.Character;
                if (character == null) return;
                var modelT = character.transform.Find("Model");
                if (modelT == null) return;
                _appearanceProbed = true;

                Plugin.Logger.LogInfo("[Appearance] === Appearance probe ===");
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // 1. AppearanceSetter component (on the Player root)
                var setter = FindComponentByName(character.gameObject, "AppearanceSetter");
                if (setter != null)
                {
                    var t = setter.GetType();
                    Plugin.Logger.LogInfo($"[Appearance] AppearanceSetter type = {t.FullName}");
                    Plugin.Logger.LogInfo("[Appearance] AppearanceSetter props:   " +
                        string.Join(" | ", t.GetProperties(flags)
                            .Select(p => $"{p.Name}({p.PropertyType.Name})")));
                    Plugin.Logger.LogInfo("[Appearance] AppearanceSetter methods: " +
                        string.Join(" | ", t.GetMethods(flags).Where(m => !m.IsSpecialName)
                            .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name))})")));
                }
                else
                {
                    Plugin.Logger.LogWarning("[Appearance] AppearanceSetter not found on the player.");
                }

                // 2. Gender + per-category active-variant state
                Component? variantSample = null;
                foreach (var gender in new[] { "Male", "Female" })
                {
                    var g = modelT.Find(gender);
                    if (g == null) continue;
                    Plugin.Logger.LogInfo($"[Appearance] {gender}: activeSelf={g.gameObject.activeSelf}");
                    for (int ci = 0; ci < g.childCount; ci++)
                    {
                        var cat = g.GetChild(ci);
                        var on = new List<string>();
                        for (int vi = 0; vi < cat.childCount; vi++)
                        {
                            var v = cat.GetChild(vi);
                            if (v.gameObject.activeSelf) on.Add(v.name);
                            variantSample ??= FindComponentByName(v.gameObject, "AppearanceElementVariant");
                        }
                        Plugin.Logger.LogInfo(
                            $"[Appearance]   {cat.name} ({cat.childCount}) active=[{string.Join(",", on)}]");
                    }
                }

                // 3. AppearanceElementVariant members
                if (variantSample != null)
                {
                    var t = variantSample.GetType();
                    Plugin.Logger.LogInfo($"[Appearance] AppearanceElementVariant type = {t.FullName}");
                    Plugin.Logger.LogInfo("[Appearance] AppearanceElementVariant props: " +
                        string.Join(" | ", t.GetProperties(flags)
                            .Select(p => $"{p.Name}({p.PropertyType.Name})")));
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Appearance] ProbeAppearance: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static Component? FindComponentByName(GameObject go, string typeName)
        {
            try
            {
                var comps = go.GetComponents(Il2CppType.Of<Component>());
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c != null && c.GetIl2CppType().Name == typeName) return c;
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

        // ── Colour discovery probe ────────────────────────────────────────────
        //
        // Logs the materials + shader colour properties of each active body /
        // clothing / hair renderer, so we can find where skin tone, hair colour
        // and clothing tints live — the data needed to sync colours.

        private static bool _colorsProbed;

        public static void ProbeColors()
        {
            if (_colorsProbed) return;
            try
            {
                var model = PlayerHelper.PlayerController?.Character?.transform.Find("Model");
                if (model == null) return;
                var maleT = model.Find("Male");
                var femT  = model.Find("Female");
                if (maleT == null || femT == null) return;
                _colorsProbed = true;

                bool male   = maleT.gameObject.activeSelf;
                var genderT = male ? maleT : femT;
                Plugin.Logger.LogInfo($"[Colors] === Colour probe ({(male ? "Male" : "Female")}) ===");

                for (int ci = 0; ci < genderT.childCount; ci++)
                {
                    var cat = genderT.GetChild(ci);
                    for (int vi = 0; vi < cat.childCount; vi++)
                    {
                        var v = cat.GetChild(vi);
                        if (!v.gameObject.activeSelf) continue;
                        var smrComp = v.gameObject.GetComponent(Il2CppType.Of<SkinnedMeshRenderer>());
                        var smr = smrComp != null ? smrComp.TryCast<SkinnedMeshRenderer>() : null;
                        if (smr != null) DumpRendererColors($"{cat.name}/{v.name}", smr);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Colors] ProbeColors: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void DumpRendererColors(string label, SkinnedMeshRenderer smr)
        {
            try
            {
                var mats = smr.sharedMaterials;
                var mpb = new MaterialPropertyBlock();
                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null) continue;
                    var sh = mat.shader;
                    var colors = new List<string>();
                    int n = sh.GetPropertyCount();
                    // MPB + texture identity: is the tint a property, a property
                    // block, a swapped material, or a swapped texture?
                    bool hasMpb = false;
                    try { smr.GetPropertyBlock(mpb, m); hasMpb = !mpb.isEmpty; } catch { }
                    string texName = "";
                    try { texName = mat.mainTexture != null ? mat.mainTexture.name : "(none)"; } catch { }
                    for (int p = 0; p < n; p++)
                    {
                        if (sh.GetPropertyType(p) == UnityEngine.Rendering.ShaderPropertyType.Color)
                        {
                            string pn = sh.GetPropertyName(p);
                            string mpbPart = "";
                            try { if (hasMpb && mpb.HasColor(pn)) mpbPart = $" MPB={mpb.GetColor(pn)}"; } catch { }
                            colors.Add($"{pn}={mat.GetColor(pn)}{mpbPart}");
                        }
                    }
                    Plugin.Logger.LogInfo(
                        $"[Colors]   {label} mat[{m}]='{mat.name}' shader='{sh.name}' tex='{texName}' mpb={hasMpb} :: " +
                        (colors.Count > 0 ? string.Join(", ", colors) : "(no colour properties)"));
                }

                // (renderer-level MPB covered per-material above)
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Colors] {label}: {ex.Message}");
            }
        }

        // ── Body-shape / face-morph discovery probe ───────────────────────────
        //
        // Determines whether the character creator uses blendshape morphs (face
        // shape, body build, height, facial features) or bone scaling — neither
        // of which the variant + colour sync captures.  Logs every blendshape
        // with a non-zero weight on each active renderer, and every Armature bone
        // with a non-default localScale.

        private static bool _morphsProbed;

        public static void ProbeMorphs()
        {
            if (_morphsProbed) return;
            try
            {
                var model = PlayerHelper.PlayerController?.Character?.transform.Find("Model");
                if (model == null) return;
                var maleT = model.Find("Male");
                var femT  = model.Find("Female");
                if (maleT == null || femT == null) return;
                _morphsProbed = true;

                bool male   = maleT.gameObject.activeSelf;
                var genderT = male ? maleT : femT;
                Plugin.Logger.LogInfo($"[Morphs] === Body-shape / morph probe ({(male ? "Male" : "Female")}) ===");

                // 1. Blendshapes on each active variant's SkinnedMeshRenderer
                int totalNonZero = 0;
                for (int ci = 0; ci < genderT.childCount; ci++)
                {
                    var cat = genderT.GetChild(ci);
                    for (int vi = 0; vi < cat.childCount; vi++)
                    {
                        var v = cat.GetChild(vi);
                        if (!v.gameObject.activeSelf) continue;
                        var smrComp = v.gameObject.GetComponent(Il2CppType.Of<SkinnedMeshRenderer>());
                        var smr = smrComp != null ? smrComp.TryCast<SkinnedMeshRenderer>() : null;
                        if (smr == null || smr.sharedMesh == null) continue;
                        var mesh = smr.sharedMesh;
                        int bc = mesh.blendShapeCount;
                        var active = new List<string>();
                        for (int b = 0; b < bc; b++)
                        {
                            float w = smr.GetBlendShapeWeight(b);
                            if (Mathf.Abs(w) > 0.001f)
                            {
                                active.Add($"{mesh.GetBlendShapeName(b)}={w:0.##}");
                                totalNonZero++;
                            }
                        }
                        Plugin.Logger.LogInfo(
                            $"[Morphs]   {cat.name}/{v.name}: {bc} blendshapes, " +
                            (active.Count > 0 ? $"non-zero=[{string.Join(", ", active)}]" : "all zero"));
                    }
                }

                // 2. Armature bones with a non-default localScale
                var armature = model.Find("Armature");
                if (armature != null)
                {
                    int scaled = 0;
                    DumpScaledBones(armature, ref scaled);
                    Plugin.Logger.LogInfo($"[Morphs]   Armature: {scaled} bone(s) with non-default scale");
                }
                else
                {
                    Plugin.Logger.LogInfo("[Morphs]   Armature transform not found");
                }

                Plugin.Logger.LogInfo(
                    $"[Morphs] === Summary: {totalNonZero} non-zero blendshape weight(s) total — " +
                    (totalNonZero > 0 ? "morphs ARE used, sync needed" : "no blendshape morphs in use") + " ===");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Morphs] ProbeMorphs: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void DumpScaledBones(Transform t, ref int count)
        {
            var s = t.localScale;
            if (Mathf.Abs(s.x - 1f) > 0.001f || Mathf.Abs(s.y - 1f) > 0.001f || Mathf.Abs(s.z - 1f) > 0.001f)
            {
                Plugin.Logger.LogInfo($"[Morphs]   bone '{t.name}' localScale={s}");
                count++;
            }
            for (int i = 0; i < t.childCount; i++)
                DumpScaledBones(t.GetChild(i), ref count);
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
                var animComp = model.GetComponent(Il2CppType.Of<Animator>());
                _localAnim = animComp != null ? animComp.TryCast<Animator>() : null;
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
                            if (anim.GetBool(ps[i].name)) p.AnimB.Add(i);
                            break;
                    }
                }
                // Layer weights — the game's scripts drive these (e.g. the
                // upper-body hold layer fades in when pushing a cart); the
                // clone has no scripts, so they must be mirrored explicitly.
                int lc = anim.layerCount;
                for (int l = 0; l < lc && l < 8; l++)
                    p.LayerW.Add(anim.GetLayerWeight(l));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[AnimSync] ReadLocalAnimState: {ex.Message}");
            }
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

        // Discovery probe — reads the FULL animator parameter array through
        // interop EVERY FRAME (~110ms/frame while it ran, profiler-measured
        // 2026-06-09: most of the host's terrible first minute).  The action-
        // anim relay it informed shipped 2026-05-31; re-enable only for new
        // animator research.
        private static readonly bool AnimProbeEnabled = false;

        public static void ProbeAnimatorLive()
        {
            if (!AnimProbeEnabled) return;
            try
            {
                if (_animProbeAnim == null)
                {
                    var model = PlayerHelper.PlayerController?.Character?.transform.Find("Model");
                    if (model == null) return;
                    var animComp = model.GetComponent(Il2CppType.Of<Animator>());
                    _animProbeAnim = animComp != null ? animComp.TryCast<Animator>() : null;
                    if (_animProbeAnim == null)
                    {
                        if (!_animProbeMissing)
                        {
                            _animProbeMissing = true;
                            Plugin.Logger.LogWarning("[AnimProbe] No Animator on local Model.");
                        }
                        return;
                    }
                    Plugin.Logger.LogInfo("[AnimProbe] === Live animator probe started — play walk/run/idle/sit/car ===");
                }

                var ps = _animProbeAnim.parameters;
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    float val;
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            val = _animProbeAnim.GetFloat(p.name); break;
                        case AnimatorControllerParameterType.Bool:
                            val = _animProbeAnim.GetBool(p.name) ? 1f : 0f; break;
                        case AnimatorControllerParameterType.Int:
                            val = _animProbeAnim.GetInteger(p.name); break;
                        default:
                            continue;   // triggers — momentary, not sampled
                    }

                    if (!_animBaseline.TryGetValue(p.name, out var baseVal))
                    {
                        _animBaseline[p.name] = val;
                        continue;
                    }
                    if (!_animChanged.Contains(p.name) && Mathf.Abs(val - baseVal) > 0.02f)
                    {
                        _animChanged.Add(p.name);
                        Plugin.Logger.LogInfo(
                            $"[AnimProbe] '{p.name}' ({p.type}) changed {baseVal:0.###}→{val:0.###} " +
                            $"— now tracked ({_animChanged.Count} param(s) in use)");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_animProbeMissing)
                {
                    _animProbeMissing = true;
                    Plugin.Logger.LogError($"[AnimProbe] {ex.Message}");
                }
            }
        }

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
                var smrComp = variant.GetComponent(Il2CppType.Of<SkinnedMeshRenderer>());
                var smr = smrComp != null ? smrComp.TryCast<SkinnedMeshRenderer>() : null;
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
                var smrComp = variant.GetComponent(Il2CppType.Of<SkinnedMeshRenderer>());
                var smr = smrComp != null ? smrComp.TryCast<SkinnedMeshRenderer>() : null;
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

                    var smrComp = v.gameObject.GetComponent(Il2CppType.Of<SkinnedMeshRenderer>());
                    var smr = smrComp != null ? smrComp.TryCast<SkinnedMeshRenderer>() : null;
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

                    var smrComp = v.gameObject.GetComponent(Il2CppType.Of<SkinnedMeshRenderer>());
                    var smr = smrComp != null ? smrComp.TryCast<SkinnedMeshRenderer>() : null;
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
                    var smrComp = v.gameObject.GetComponent(Il2CppType.Of<SkinnedMeshRenderer>());
                    var smr = smrComp != null ? smrComp.TryCast<SkinnedMeshRenderer>() : null;
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
                    col.height   = 1.8f;
                    col.radius   = 0.4f;
                    col.center   = new Vector3(0f, 0.9f, 0f);
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
                var animComp = model.GetComponent(Il2CppType.Of<Animator>());
                mover.Anim = animComp != null ? animComp.TryCast<Animator>() : null;

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
                var comps = model.GetComponents(Il2CppType.Of<Component>());
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    if (System.Array.IndexOf(_killScripts, c.GetIl2CppType().Name) >= 0)
                        UnityEngine.Object.Destroy(c);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[RemotePlayer] StripModelScripts: {ex.Message}");
            }
        }
    }
}
