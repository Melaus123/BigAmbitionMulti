using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Helpers;

namespace BigAmbitionsMP
{
    // A vehicle ghost has no EntityController (we strip it), so MouseController sees currentTargetEntity==null
    // and treats a click on it as plain click-to-move (SetNewDestination, removeGoal:true) — walking the player
    // to the clicked car body and fighting our directed walk. An interactive entity click suppresses
    // click-to-move; this does the same for a ghost click. Our SetGoal uses removeGoal:false, so it's untouched.
    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SetNewDestination))]
    public static class Patch_PlayerController_SetNewDestination_GhostClick
    {
        static bool Prefix(bool removeGoal) => !(removeGoal && PassengerRide.IsHoveringGhost);
    }

    /// <summary>
    /// Passenger ("ride shotgun") gameplay layer. Consumes the host-authoritative occupancy +
    /// locks in <see cref="PassengerSync"/> and turns them into the visible ride: the LOCAL
    /// player walking to a ghost car's passenger door, pinning into the seat (own model hidden,
    /// the camera keeps following the pinned character), and stepping out beside the car; plus
    /// rendering OTHER players who are riding (their avatar pinned to the seat, nametag floating).
    /// The feature ships — only the F7 board/exit TEST trigger is dev-gated, a stand-in until the
    /// in-car "Ride" CTA lands. Driven once per frame from MPCanvasUI.Update. See
    /// docs/PASSENGER-SYSTEM.md.
    /// </summary>
    internal static class PassengerRide
    {
        // ── local ride ────────────────────────────────────────────────────────
        private static string _localVeh  = "";    // what we're currently rendering as ridden ("" = on foot)
        private static int    _localSeat = -1;
        private static bool   _pinned;            // arrived at the seat and locked to it
        private static float  _pinFallback;       // unscaled-time deadline to pin if the walk never arrives
        private static bool   _following;         // [PassFollow] mid building enter/exit — re-pin once co-located with the ghost
        // walk-to-deposit: pending deposit while the player walks to the vehicle's loading spot
        private static string _depVid = "", _depOwner = "";
        private static Vector3 _depSpot;
        private static float  _depDeadline;
        private static bool   _exitRequested;     // already asked the host to release us (car vanished)
        private static float  _ghostGoneSince = -1f;   // when our ridden ghost first went missing (-1 = present)

        /// <summary>True once we're actually pinned in the seat — drives the passenger HUD so the
        /// "Exit Vehicle" button only appears after we're really aboard, not on board-approval.</summary>
        internal static bool IsSeated => _pinned && _localVeh != "";

        /// <summary>While seated as a passenger the real character is parked invisibly at the
        /// boarding door, so world-streaming (traffic + parked-ghost culling) must follow the
        /// RIDDEN car instead — else only the cars seen at entry stay visible. Returns the
        /// ridden ghost's transform when seated, null otherwise.</summary>
        internal static Transform? RideAnchorTransform()
            => IsSeated ? VehicleManager.GhostTransform(_localVeh) : null;

        // Colliders + controller dropped while seated so the parked-at-door character can't be
        // a phantom obstacle (traffic braking for it, players bumping it); restored in EndLocalRide.
        private static readonly System.Collections.Generic.List<Collider> _seatedColliders = new();
        private static CharacterController? _seatedController;

        // ── remote riders we've pinned (pid → vehicleId) ────────────────────
        private static readonly Dictionary<string, string> _remotePinned = new();

        public static void Update()
        {
            if (!MPServer.IsRunning && !MPClient.IsConnected)
            {
                if (_localVeh != "") EndLocalRide(beside: false);
                if (_remotePinned.Count > 0) ClearRemotePins();
                return;
            }

            try
            {
                TickHoverHighlight();   // cursor over an unlocked ghost → outline + board on click
                TickLocalRide();
                TickDeposit();          // walk-to-deposit: deposit on arrival (proximity/timeout poll)
                TickRemoteRiders();
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] Update: {ex.Message}"); }
        }

        // ── click-to-board + hover outline (mirrors a driver entering: hover → click) ──
        private static string _hovered = "";
        /// <summary>Cursor is currently over one of our vehicle ghosts — suppresses native click-to-move so a
        /// ghost click behaves like an interactive-entity click (the mod handles it) instead of walking there.</summary>
        public static bool IsHoveringGhost => _hovered != "";
        private static readonly List<(GameObject go, int layer)> _hiLayers = new();
        private static int _outlineLayer = -2;   // -2 = unresolved
        private static int _pickMask;   // 0 = unresolved; the game's hover-layer mask (built once)
#if BAMP_DEV
        private static float _nextMissLog;
#endif

        // The game's own cursor-hover layer mask (= MouseController.DefaultObjectTypes), replicated so the
        // passenger pick is IDENTICAL to the owner's car-hover (same layers, solid-only). Built once.
        private static int PickMask()
        {
            if (_pickMask != 0) return _pickMask;
            _pickMask = LayerMask.GetMask("InteractiveItems", "InteractiveItemsOutlined", "Buildings",
                                          "BuildingsOutlined", "Vehicles", "PlayerVehicles", "Ground");
            if (_pickMask == 0) _pickMask = ~0;   // layer names not found — fall back to everything
            return _pickMask;
        }

        private static void TickHoverHighlight()
        {
            if (PassengerSync.IsRiding(MPConfig.PlayerId)) { ClearHighlight(); return; }
            if (VehicleStoragePanel.IsOpen) { ClearHighlight(); return; }   // storage panel up — don't world-click behind it

            var cam = Camera.main;
            string hit = "";
#if BAMP_DEV
            string missDbg = "";
#endif
            if (cam != null)
            {
                try
                {
                    // IDENTICAL to the native owner-highlight (MouseController.Run): the game's own hover
                    // layer mask, 600 m, SOLID-only (QueryTriggerInteraction.Ignore). No invented range or
                    // trigger params — the same query the game uses to outline a hovered car for the driver.
                    // The earlier ~0 mask hit ground/clutter first ("too small"); triggers made it "too big".
                    // GhostIdFor maps the hit to a ghost and returns "" for ground/buildings, so when the
                    // cursor is over a ghost car the first solid hit is the car body — exactly like native.
                    var ray = cam.ScreenPointToRay(Input.mousePosition);
                    if (Physics.Raycast(ray, out var rh, 600f, PickMask(), QueryTriggerInteraction.Ignore) && rh.transform != null)
                    {
                        hit = VehicleManager.GhostIdFor(rh.transform);
#if BAMP_DEV
                        if (hit == "") missDbg = $"{(rh.collider != null ? rh.collider.name : "?")}@{rh.distance:F1}m(L{rh.transform.gameObject.layer})";
#endif
                    }
                }
                catch { }
            }
#if BAMP_DEV
            // DIAG:INVESTIGATION(passenger-highlight) — if we're pointing near a car but mapping no
            //   ghost, log the nearest non-ghost hit (throttled) so a persistent miss is diagnosable
            //   without another test cycle. A successful hover logs below on change instead.
            if (hit == "" && missDbg != "" && Time.unscaledTime > _nextMissLog)
            {
                _nextMissLog = Time.unscaledTime + 1f;
                Plugin.Logger.LogInfo($"[RideHi] hover MISS — nearest non-ghost hit {missDbg} (no ghost mapped under cursor)");
            }
#endif
            // Locked cars STILL highlight (so you can tell they're interactable) — clicking one
            // tells you it's locked instead of boarding.
            if (hit != _hovered)
            {
#if BAMP_DEV
                // DIAG:INVESTIGATION(passenger-highlight) — is the cursor actually hitting a ghost,
                //   and did the outline layer resolve? Distinguishes "raycast misses" from "outline
                //   resolved but not rendering" (you reported not seeing the highlight this build).
                if (hit != "")
                    Plugin.Logger.LogInfo($"[RideHi] hover ghost '{hit}' locked={PassengerSync.IsLocked(hit)} outlineLayer={OutlineLayer()}");
#endif
                ClearHighlight(); if (hit != "") SetHighlight(hit); _hovered = hit;
            }
            if (_hovered != "" && Input.GetMouseButtonDown(0))
            {
                if (PassengerSync.IsLocked(_hovered)) PassengerHud.Toast("Vehicle locked.");
                else HandleUnlockedClick(_hovered);
            }
        }

        private static void SetHighlight(string vid)
        {
            int layer = OutlineLayer();
            if (layer < 0) return;   // unresolved — skip the visual; click-to-board still works
            var t = VehicleManager.GhostTransform(vid);
            if (t == null) return;
            int n = 0;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                _hiLayers.Add((r.gameObject, r.gameObject.layer));
                r.gameObject.layer = layer;
                n++;
            }
#if BAMP_DEV
            // DIAG:INVESTIGATION(passenger-highlight) — n=0 means the ghost has no renderers to
            //   outline; n>0 but no visible outline means the game's outline pass isn't drawing our
            //   ghosts on that layer (→ different fix needed).
            Plugin.Logger.LogInfo($"[RideHi] highlight '{vid}': {n} renderer(s) → layer {layer}");
#endif
        }

        private static void ClearHighlight()
        {
            for (int i = 0; i < _hiLayers.Count; i++)
                if (_hiLayers[i].go != null) _hiLayers[i].go.layer = _hiLayers[i].layer;
            _hiLayers.Clear();
            _hovered = "";
        }

        // Resolve the game's outline layer once (reflection — tolerates a renamed field, see
        // ANTIPATTERNS class 3; null-checked + logged, and the highlight is optional).
        private static int OutlineLayer()
        {
            if (_outlineLayer != -2) return _outlineLayer;
            _outlineLayer = -1;
            try
            {
                var ty = AccessTools.TypeByName("LayerHelper")
                      ?? AccessTools.TypeByName("Helpers.LayerHelper")
                      ?? AccessTools.TypeByName("BigAmbitions.LayerHelper");
                if (ty != null)
                {
                    var fi = ty.GetField("InteractiveItemsOutlinedLayerIndex", BindingFlags.Public | BindingFlags.Static);
                    if (fi != null) _outlineLayer = (int)fi.GetValue(null);
                    else
                    {
                        var pi = ty.GetProperty("InteractiveItemsOutlinedLayerIndex", BindingFlags.Public | BindingFlags.Static);
                        if (pi != null) _outlineLayer = (int)pi.GetValue(null);
                    }
                }
            }
            catch { }
            if (_outlineLayer < 0)
                Plugin.Logger.LogWarning("[Ride] outline layer unresolved — hover highlight off (click-to-board still works).");
            return _outlineLayer;
        }

        private static void RequestBoard(string vehicleId)
        {
            if (MPServer.IsRunning) MPServer.HostBoardRequest(vehicleId);
            else                    MPClient.SendBoardRequest(vehicleId);
        }

        // A non-owner clicked an UNLOCKED vehicle. Routing mirrors the native CTA:
        //   • Genuinely controlling a flatbed/hand-truck → LOOT the target into it.
        //   • Genuinely driving a car / on a scooter → no interaction (can't board or loot from there).
        //   • On foot, flatbed/hand-truck (0 passenger seats) → its storage list.
        //   • On foot, CAR with trunk items → the host-style menu (Enter Vehicle / Manage Storage).
        //   • On foot, empty car → board straight away.
        // We key off GetCurrentVehicle() (resolves ActiveVehicleId against the LOCAL vehicle list, so it's
        // null on foot AND for a stale/unresolvable id) rather than raw IsUsingVehicle — the latter is just
        // "ActiveVehicleId set", which a stale ghost id makes wrongly true and false-blocked an on-foot
        // player with "Not while driving". Seat count is from the ghost's type + authored table (client-safe).
        private static void HandleUnlockedClick(string vid)
        {
            string owner = VehicleManager.OwnerIdFor(vid);

            // Holding an item on foot → walk to the vehicle's cargo-loading spot (the same place the owner
            // goes) and deposit on arrival — mirrors VehicleController.MoveAndAddHeldItemToStorage. Cars AND carts.
            if (VehicleHelper.GetCurrentVehicle() == null && PlayerHelper.ItemInstanceInHands != null)
            {
                WalkAndDeposit(vid, owner);
                return;
            }

            bool hasCargo = VehicleManager.GhostCargoFor(vid).Count > 0;

            var cur = VehicleHelper.GetCurrentVehicle();
            if (cur != null)
            {
                bool canStore = cur.VehicleType != null && cur.VehicleType.spawnInPlayerObject && cur.VehicleType.maxCargoCapacity > 0;
                if (canStore)   // pushing a flatbed/hand-truck → loot the target into it
                {
                    if (hasCargo) VehicleStoragePanel.Open(vid, owner);
                    else          PassengerHud.Toast("Nothing to take.");
                }
                // else: driving a car / on a scooter — nothing to do here; stay silent rather than nag.
                return;
            }

            // On foot.
            int seats = PassengerSync.PassengerSeatsForType(VehicleManager.TypeNameFor(vid));
            if (seats <= 0)
            {
                if (hasCargo) VehicleStoragePanel.Open(vid, owner);
                else          PassengerHud.Toast("Nothing to take.");
                return;
            }
            if (hasCargo) VehicleStoragePanel.OpenChoice(vid, owner, () => RequestBoard(vid));
            else          RequestBoard(vid);
        }

        // Walk to the vehicle's cargo-loading spot (the same place the owner would) and deposit the held item
        // on arrival — mirrors VehicleController.MoveAndAddHeldItemToStorage. Falls back to depositing in place
        // if we can't resolve a spot or the walk can't be issued.
        private static void WalkAndDeposit(string vid, string owner)
        {
            try
            {
                var pc = PlayerHelper.PlayerController;
                var ghost = VehicleManager.GhostTransform(vid);
                Vector3 spot = VehicleManager.LoadingSpotFor(vid);
                if (pc == null || ghost == null || spot == Vector3.zero)
                {
                    VehicleStorageSync.RequestDeposit(vid, owner);   // can't walk → deposit in place
                    return;
                }
                // Walk to the loading spot; deposit when we ARRIVE — polled by proximity/timeout in TickDeposit.
                // (SetGoal's arrival callback is unreliable for ghosts; boarding uses the same atCar/timeout poll.)
                _depVid = vid; _depOwner = owner; _depSpot = spot; _depDeadline = Time.unscaledTime + 10f;
                pc.SetGoal(spot, new UnityEngine.Events.UnityAction(() => { }));
            }
            catch { VehicleStorageSync.RequestDeposit(vid, owner); }
        }

        // Deposit when the player reaches the loading spot (proximity) or after a timeout backstop — mirrors the
        // boarding atCar/_pinFallback poll instead of SetGoal's unreliable arrival callback.
        private static void TickDeposit()
        {
            if (_depVid == "") return;
            bool arrived = false;
            try
            {
                var ch = PlayerHelper.PlayerController?.Character;
                if (ch != null) arrived = Vector3.Distance(ch.transform.position, _depSpot) < 1.5f;
            }
            catch { }
            if (arrived || Time.unscaledTime >= _depDeadline)
            {
                string vid = _depVid, owner = _depOwner;
                _depVid = ""; _depOwner = "";   // clear first so the deposit fires exactly once
                VehicleStorageSync.RequestDeposit(vid, owner);
            }
        }

        /// <summary>Leave the current ride (called by the passenger HUD's Exit Vehicle button).</summary>
        internal static void RequestExit()
        {
            string vid = PassengerSync.LocalRidingVehicleId;
            if (string.IsNullOrEmpty(vid)) return;
            if (MPServer.IsRunning) MPServer.HostExit(vid);
            else                    MPClient.SendPassengerExit(vid);
        }

        // ── local ride state machine (driven by PassengerSync.LocalRidingVehicleId) ──
        private static void TickLocalRide()
        {
            string target = PassengerSync.LocalRidingVehicleId;

            if (target != _localVeh)
            {
                if (string.IsNullOrEmpty(target)) EndLocalRide(beside: true);
                else BeginLocalRide(target, PassengerSync.LocalSeat);
            }

            if (_localVeh == "") return;

            var ghost = VehicleManager.GhostTransform(_localVeh);
            if (ghost == null)
            {
                // Our ridden car isn't here — either still spawning (board just approved) or it drove
                // off / the owner left. Wait briefly; if it's truly gone, ask the host to release our
                // seat so we don't hold a phantom occupancy. NEVER pin without a car — that used to
                // freeze + hide the player in place ("got the Exit button but never got in").
                if (_ghostGoneSince < 0f) _ghostGoneSince = Time.unscaledTime;
                else if (Time.unscaledTime - _ghostGoneSince > 3f)
                {
                    // Car stayed gone — owner drove far / desynced / DISCONNECTED. Disembark LOCALLY:
                    // revert to our normal avatar + restore camera/movement even if the host is gone
                    // and can never confirm the exit. Never leave the passenger stuck hidden + frozen.
                    RequestExit();                                 // best-effort host notify (no-op if host gone)
                    PassengerSync.ApplyExit(MPConfig.PlayerId);    // clear our local ride mirror (prevents re-begin)
                    EndLocalRide(beside: false);                   // ToggleVisibility(true) + unlock + restore camera
                }
                return;
            }
            _ghostGoneSince = -1f;

            // Become "seated" (which is what shows the Exit HUD) only once we've actually REACHED the
            // car — not on a short timer while still walking up, and not if we never make it. The
            // timeout is just a backstop for a failed walk path (then we warp into the seat).
            if (!_pinned)
            {
                var ch = PlayerHelper.PlayerController?.Character;
                bool atCar = ch != null &&
                             Vector3.Distance(ch.transform.position, ghost.TransformPoint(DoorLocal(_localSeat))) < 2.5f;
                // [PassFollow] after following the driver through a building transition, re-pin as soon as
                // we and the ghost are co-located again (same scene — the ghost teleports to the new scene
                // a beat after we transition), without waiting for the 2.5 m walk or the fallback timeout.
                bool followReady = _following && ch != null && Vector3.Distance(ch.transform.position, ghost.position) < 30f;
                if (atCar || followReady || Time.unscaledTime >= _pinFallback) { _following = false; StartPin(); }
            }
            else
            {
                // [PassFollow] Host-modeled camera, re-asserted every frame while pinned. The host's live
                // cam stays IndoorVehicleCam (inside) / VehicleCam (outside) following the driven car; we
                // mirror that, pointing Follow at the ghost. The building-entry transition switches the live
                // cam to the on-foot IndoorCam (one-shot), so a single assignment at StartPin gets stranded
                // — re-asserting wins it back the next frame. Idempotent (see EnsureRideCamera), so no flicker.
                EnsureRideCamera(ghost);
            }
        }

        private static void BeginLocalRide(string vehicleId, int seat)
        {
            _localVeh = vehicleId; _localSeat = seat; _pinned = false; _following = false;
            _exitRequested = false; _ghostGoneSince = -1f; _camSaved = false;
            _pinFallback = Time.unscaledTime + 10f;   // backstop only: warp into the seat if the walk fails

            var pc = PlayerHelper.PlayerController;
            var ghost = VehicleManager.GhostTransform(vehicleId);
            if (pc == null || ghost == null) return;   // no car yet — TickLocalRide waits / pins later
            try
            {
                Vector3 door = ghost.TransformPoint(DoorLocal(seat));
                pc.SetGoal(door, new UnityEngine.Events.UnityAction(StartPin));   // walk; pin on arrival
            }
            catch { /* walk failed — the pin fallback in TickLocalRide still seats us */ }
        }

        // ── the "in the car" transition — the EXACT native enter calls (minus ownership/drive) ──
        private static Transform? _savedVcFollow,  _savedVcLook;    // [PassFollow] vehicleCamera originals (restored on exit)
        private static Transform? _savedIvcFollow, _savedIvcLook;   // [PassFollow] indoorVehicleCamera originals (restored on exit)
        private static bool       _camSaved;         // [PassFollow] captured both vehicle cams' pre-ride Follow yet?
        private static bool       _camIndoor;        // [PassFollow] last asserted vehicle cam (true=indoorVehicleCamera)

        private static void StartPin()
        {
            if (_pinned || _localVeh == "") return;
            _pinned = true;
            try
            {
                var pc = PlayerHelper.PlayerController;
                var ghost = VehicleManager.GhostTransform(_localVeh);
                if (pc == null) return;
                // Same thing entering a car does (VehicleController/CarController.EnterVehicle): hide
                // the avatar, lock independent movement, switch to the vehicle camera — minus the two
                // driver-only lines (ActiveVehicleId + controlledByPlayer), which we must NOT set on
                // another player's car. The camera is pointed at the ghost since it isn't our own.
                try { pc.Character?.ToggleVisibility(false); } catch { }   // avatar disappears (native)
                SetVehicleNavBlocker(true);                                // movement locked (native)
                EnsureRideCamera(ghost);                                   // camera → car view (host-modeled; re-asserted each frame by TickLocalRide)
                // The real character stays parked invisibly at the boarding door; drop its
                // colliders + controller so it isn't a phantom obstacle there (passing traffic
                // braking for it, players bumping it). Restored in EndLocalRide.
                try
                {
                    var ch = pc.Character;
                    if (ch != null)
                    {
                        _seatedColliders.Clear();
                        foreach (var col in ch.GetComponentsInChildren<Collider>(true))
                            if (col != null && col.enabled) { _seatedColliders.Add(col); col.enabled = false; }
                        _seatedController = ch.GetComponentInChildren<CharacterController>(true);
                        if (_seatedController != null && _seatedController.enabled) _seatedController.enabled = false;
                        else _seatedController = null;
                    }
                }
                catch { }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] StartPin: {ex.Message}"); }
        }

        /// <summary>The game's own movement lock (PlayerController.SetNavigationBlocker) via reflection
        /// — same registry as Map/Sleep/etc.; the enum isn't directly referenceable. Vehicle = 26.</summary>
        private static void SetVehicleNavBlocker(bool blocked)
        {
            try
            {
                var pc = PlayerHelper.PlayerController;
                if (pc == null) return;
                var m = pc.GetType().GetMethod(blocked ? "SetNavigationBlocker" : "UnsetNavigationBlocker",
                    BindingFlags.Public | BindingFlags.Instance);
                if (m == null) return;
                var enumType = m.GetParameters()[0].ParameterType;
                m.Invoke(pc, new[] { System.Enum.ToObject(enumType, 26) });   // NavigationBlocker.Vehicle
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] navblock: {ex.Message}"); }
        }

        // Capture both vehicle cams' pre-ride Follow/LookAt once, so EndLocalRide can fully restore them.
        // The game leaves these bound to the local player; if we leave one chasing our ghost, the
        // passenger's OWN future driving camera would track the wrong (possibly destroyed) transform.
        private static void SaveCamsOnce(GameManager gm)
        {
            if (_camSaved) return;
            _camSaved = true;
            if (gm.vehicleCamera != null)       { _savedVcFollow  = gm.vehicleCamera.Follow;       _savedVcLook  = gm.vehicleCamera.LookAt; }
            if (gm.indoorVehicleCamera != null) { _savedIvcFollow = gm.indoorVehicleCamera.Follow; _savedIvcLook = gm.indoorVehicleCamera.LookAt; }
        }

        // Host-modeled live camera while riding. Confirmed by probe: the HOST's live cam is IndoorVehicleCam
        // (inside) / VehicleCam (outside), prio=1, following the driven car. We mirror that exactly, but
        // point Follow at the synced ghost (we're not the driver) and pick indoor vs outdoor by
        // BuildingManager.IsInsideBuilding (CarController.UpdateCamera does the same). Idempotent and
        // re-asserted every frame by TickLocalRide: the building-entry transition switches the live cam to
        // the on-foot IndoorCam (BuildingManager.cs:517), so we just re-claim Priority the next frame. The
        // GetCurrentCamera()!=cam guard means we only call SetCamera when not already live → no flicker.
        private static void EnsureRideCamera(Transform? ghost)
        {
            if (ghost == null) return;
            try
            {
                var gm = InstanceBehavior<GameManager>.Instance;
                if (gm == null) return;
                SaveCamsOnce(gm);
                _camIndoor = BuildingManager.IsInsideBuilding;
                var cam = _camIndoor ? gm.indoorVehicleCamera : gm.vehicleCamera;
                if (cam == null) return;
                if (cam.Follow != ghost) { cam.Follow = ghost; cam.LookAt = ghost; }
                if (CameraHelper.GetCurrentCamera() != cam) CameraHelper.SetCamera(cam);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] camera assert: {ex.Message}"); }
        }

        private static void RestoreCamera()
        {
            try
            {
                var gm = InstanceBehavior<GameManager>.Instance;
                if (gm == null) return;
                // Restore BOTH vehicle cams to their pre-ride Follow (so the player's own future driving
                // camera doesn't chase our ghost), then hand back to the matching on-foot cam — indoorCamera
                // inside, pedestrianCamera outside (mirrors BuildingManager.cs:517 / CarController.cs:502).
                if (_camSaved)
                {
                    if (gm.vehicleCamera != null)       { gm.vehicleCamera.Follow = _savedVcFollow;       gm.vehicleCamera.LookAt = _savedVcLook; }
                    if (gm.indoorVehicleCamera != null) { gm.indoorVehicleCamera.Follow = _savedIvcFollow; gm.indoorVehicleCamera.LookAt = _savedIvcLook; }
                    _camSaved = false;
                }
                var footCam = BuildingManager.IsInsideBuilding ? gm.indoorCamera : gm.pedestrianCamera;
                if (footCam != null) CameraHelper.SetCamera(footCam);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] camera exit: {ex.Message}"); }
        }

        private static void EndLocalRide(bool beside)
        {
            if (_localVeh == "") return;
            try
            {
                var pc = PlayerHelper.PlayerController;
                var ch = pc?.Character;
                var ghost = VehicleManager.GhostTransform(_localVeh);

                // Reverse the native enter (only if we actually got in): avatar back, movement
                // unlocked, camera restored — then teleport to the exit door (current car position).
                if (_pinned)
                {
                    try { ch?.ToggleVisibility(true); } catch { }   // re-activates the character
                    SetVehicleNavBlocker(false);
                    RestoreCamera();
                }

                // Restore the colliders/controller we dropped while seated — UNCONDITIONAL so we
                // can never leave the player non-physical (falling through the world) even if the
                // pin state got confused. No-op when nothing was dropped.
                if (_seatedColliders.Count > 0 || _seatedController != null)
                {
                    try { foreach (var col in _seatedColliders) if (col != null) col.enabled = true; } catch { }
                    try { if (_seatedController != null) _seatedController.enabled = true; } catch { }
                    _seatedColliders.Clear(); _seatedController = null;
                }

                if (ch != null)
                {
                    Vector3 outPos = (beside && ghost != null) ? ghost.TransformPoint(DoorLocal(_localSeat)) : ch.transform.position;
                    try { ch.navmeshAgent?.Warp(outPos); } catch { try { ch.transform.position = outPos; } catch { } }
                }
                try { pc?.ResetNavigation(); } catch { }
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] EndLocalRide: {ex.Message}"); }
            _localVeh = ""; _localSeat = -1; _pinned = false; _following = false;
            _exitRequested = false; _ghostGoneSince = -1f;
        }

        /// <summary>[PassFollow] Reverse StartPin WITHOUT ending the ride, so we stay "riding" across a
        /// building transition: re-activate the avatar, unlock movement, restore the camera, re-enable the
        /// colliders we dropped while seated. Leaves _localVeh intact + arms _following so TickLocalRide
        /// re-pins to the ghost once we're co-located again in the new scene.</summary>
        private static void SoftUnpin()
        {
            if (_pinned)
            {
                try { PlayerHelper.PlayerController?.Character?.ToggleVisibility(true); } catch { }
                SetVehicleNavBlocker(false);
                RestoreCamera();
            }
            if (_seatedColliders.Count > 0 || _seatedController != null)
            {
                try { foreach (var col in _seatedColliders) if (col != null) col.enabled = true; } catch { }
                try { if (_seatedController != null) _seatedController.enabled = true; } catch { }
                _seatedColliders.Clear(); _seatedController = null;
            }
            _pinned = false;
            _following = true;                       // re-pin promptly once co-located with the ghost
            _pinFallback = Time.unscaledTime + 10f;  // backstop only
        }

        /// <summary>[PassFollow] Resolve a building by its AddressKey (CityManager lookup) then follow the
        /// driver into it. Shared by the client rider (FollowEnter message) and the host rider (relay route)
        /// so both resolve the building identically.</summary>
        public static void FollowDriverIntoByAddress(string addressKey)
        {
            if (_localVeh == "" || string.IsNullOrEmpty(addressKey)) return;
            try
            {
                var cm = InstanceBehavior<CityManager>.Instance;
                CityBuildingController? target = null;
                if (cm != null && cm.cityBuildingControllers != null)
                    foreach (var c in cm.cityBuildingControllers)
                        if (c != null && c.building != null && GameStateReader.AddressKey(c.building) == addressKey) { target = c; break; }
                if (target == null) { Plugin.Logger.LogWarning($"[PassFollow] FollowEnter: could not resolve building '{addressKey}'."); return; }
                FollowDriverInto(target.building);
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] FollowDriverIntoByAddress: {ex.Message}"); }
        }

        /// <summary>[PassFollow] The driver drove our ridden vehicle through a building entrance — follow
        /// them in: soft un-pin, enter the building so its interior loads on our client, and let
        /// TickLocalRide re-pin to the ghost (already at the right world coords inside) — so we ride in the
        /// interior instead of staring at unloaded space.</summary>
        public static void FollowDriverInto(Buildings.Building building)
        {
            if (_localVeh == "" || building == null) return;   // not riding / unresolved → ignore
            // Defensive backstop: if we're somehow already inside a building (a FollowExit was missed or
            // arrived late), DON'T enter again — a second EnterBuilding loads an interior on top of the
            // first → "grey void" (user 2026-06-23). Skip; the next exit/enter cycle recovers. With slice 2
            // (FollowExit) working we should always be outside when this fires.
            if (BuildingManager.IsInsideBuilding)
            {
                Plugin.Logger.LogInfo($"[PassFollow] FollowEnter ignored — already inside a building (riding '{_localVeh}'); avoiding double-enter.");
                return;
            }
            try
            {
                SoftUnpin();
                InstanceBehavior<BuildingManager>.Instance?.EnterBuilding(building, false, false, 0, -1, true);
                Plugin.Logger.LogInfo($"[PassFollow] following driver into building (riding '{_localVeh}') — entering; will re-pin inside.");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] FollowDriverInto: {ex.Message}"); }
        }

        /// <summary>[PassFollow] The driver drove our ridden vehicle back OUT of a building — follow them
        /// out: soft un-pin, exit the building on our client (back to the street), and let TickLocalRide
        /// re-pin to the ghost once we're co-located outside. Mirror of FollowDriverInto.</summary>
        public static void FollowDriverOut(int targetExitId)
        {
            if (_localVeh == "") return;                       // not riding → ignore
            if (!BuildingManager.IsInsideBuilding)
            {
                Plugin.Logger.LogInfo($"[PassFollow] FollowExit ignored — not inside a building (riding '{_localVeh}').");
                return;
            }
            try
            {
                SoftUnpin();
                InstanceBehavior<BuildingManager>.Instance?.ExitFromBuilding(targetExitId);
                Plugin.Logger.LogInfo($"[PassFollow] following driver out of building (riding '{_localVeh}', exit {targetExitId}) — exiting; will re-pin outside.");
            }
            catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Ride] FollowDriverOut: {ex.Message}"); }
        }

        // ── remote riders rendering ──────────────────────────────────────────
        private static void TickRemoteRiders()
        {
            var want = new Dictionary<string, string>();   // pid → vid (remote riders this frame)
            foreach (var vid in PassengerSync.OccupiedVehicleIds())
            {
                // Resolve the vehicle's transform on THIS machine: another player's car is a ghost,
                // but the OWNER's own car is the REAL native vehicle (not a ghost) — riders of it
                // must be pinned to it too, else the passenger never sits on the owner's screen
                // (and, pre-trigger-fix, kept shoving the car).
                Transform? veh = VehicleManager.GhostTransform(vid) ?? VehicleManager.LocalVehicleTransform(vid);
                if (veh == null) continue;
                var riders = PassengerSync.RidersOf(vid);
                if (riders == null) continue;
                foreach (var kv in riders)
                {
                    string pid = kv.Value;
                    if (string.IsNullOrEmpty(pid) || pid == MPConfig.PlayerId) continue;   // local ride handles us
                    want[pid] = vid;
                    try
                    {
                        RemotePlayerManager.SetDriving(pid, true);                // hide their walk model
                        RemotePlayerManager.SetRide(pid, veh, SeatLocal(kv.Key)); // pin (nametag) to the seat
                    }
                    catch { }
                }
            }
            // Release anyone who stopped riding since last frame.
            if (_remotePinned.Count > 0)
            {
                List<string>? stale = null;
                foreach (var kv in _remotePinned)
                    if (!want.ContainsKey(kv.Key)) (stale ??= new List<string>()).Add(kv.Key);
                if (stale != null)
                    foreach (var pid in stale)
                    {
                        try { RemotePlayerManager.SetRide(pid, null, Vector3.zero); RemotePlayerManager.SetDriving(pid, false); } catch { }
                        _remotePinned.Remove(pid);
                    }
            }
            foreach (var kv in want) _remotePinned[kv.Key] = kv.Value;
        }

        private static void ClearRemotePins()
        {
            foreach (var kv in _remotePinned)
                try { RemotePlayerManager.SetRide(kv.Key, null, Vector3.zero); RemotePlayerManager.SetDriving(kv.Key, false); } catch { }
            _remotePinned.Clear();
        }

        // ── seat / door geometry (generic local-space; refine per car later) ─
        // Car frame: +x right, +z forward. Seat 1 = front passenger (shotgun, right-front);
        // 2 = rear-left, 3 = rear-right. Doors sit just outside the body on the matching side.
        private static Vector3 SeatLocal(int seat) => seat switch
        {
            2 => new Vector3(-0.45f, 0.7f, -0.85f),
            3 => new Vector3( 0.45f, 0.7f, -0.85f),
            _ => new Vector3( 0.45f, 0.7f,  0.15f),   // 1 / default = shotgun
        };
        private static Vector3 DoorLocal(int seat) => seat switch
        {
            2 => new Vector3(-1.15f, 0f, -0.85f),
            3 => new Vector3( 1.15f, 0f, -0.85f),
            _ => new Vector3( 1.15f, 0f,  0.15f),
        };
    }
}
