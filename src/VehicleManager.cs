using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Helpers;
using Vehicles.VehicleTypes;
using GleyTrafficSystem;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Vehicle subsystem.  Currently a discovery probe — it gathers the data we
    /// need to design vehicle sync (the vehicle class landscape, how the player
    /// references / attaches to a vehicle, and the entry/exit API).  Will grow
    /// into the actual sync: transform sync + driver association + identity.
    ///
    /// All public methods must be called on the Unity main thread.
    /// </summary>
    public static class VehicleManager
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static bool _assembliesScanned;
        private static bool _playerMembersProbed;
        private static bool _parentInit;
        private static Transform? _lastCharParent;

        // ── 4. Vehicle-system class API dump ──────────────────────────────────
        //
        // Dumps the declared members of the classes that drive vehicle spawning,
        // control and identity — the data we need to design "recreate a remote
        // player's vehicle on my screen".


        public static Type? FindGameType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        /// <summary>Every method with the given simple name across all loaded
        /// assemblies.  Used by Harmony patches whose declaring type isn't known
        /// at compile time AND which may exist on multiple types (base +
        /// derived).  Logs each match so we can see what's being patched.</summary>
        public static IEnumerable<System.Reflection.MethodBase> FindAllMethodsByName(string methodName)
        {
            var bf = System.Reflection.BindingFlags.Public
                   | System.Reflection.BindingFlags.NonPublic
                   | System.Reflection.BindingFlags.Instance
                   | System.Reflection.BindingFlags.Static
                   | System.Reflection.BindingFlags.DeclaredOnly;   // skip inherited
            int hits = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    System.Reflection.MethodInfo? m = null;
                    try { m = t.GetMethod(methodName, bf); } catch { }
                    if (m != null)
                    {
                        hits++;
                        Plugin.Logger.LogInfo($"[FindMethod] '{methodName}' → {t.FullName}");
                        yield return m;
                    }
                }
            }
            if (hits == 0)
                Plugin.Logger.LogWarning($"[FindMethod] '{methodName}' not found in any loaded assembly.");
        }

        // ── 5. Driven-vehicle dump — model + colour identity ──────────────────
        //
        // While the player is driving, dumps the vehicle GameObject's renderers
        // and materials — the data needed to identify the vehicle model and its
        // colour so a remote copy can be recreated to match.

        private static bool _drivenDumped;

        public static Component? FindComponentByName(GameObject go, string typeName)
        {
            try
            {
                var comps = go.GetComponents(typeof(Component));
                for (int i = 0; i < comps.Length; i++)
                    if (comps[i] != null && comps[i].GetType().Name == typeName)
                        return comps[i];
            }
            catch { }
            return null;
        }

        // ── 1. Assembly scan — every vehicle-named type ───────────────────────

        // "car" alone matches noise (di-scar-dable, generi-cAr-ray…) so we don't
        // keyword on it — CarController/CarFeatures are already known by name.
        private static readonly string[] _typeKeywords =
        {
            "vehicle", "scooter", "motorcycle", "motorbike", "moped"
        };

        private static void ScanAssembliesForVehicleTypes()
        {
            if (_assembliesScanned) return;
            _assembliesScanned = true;

            Plugin.Logger.LogInfo("[Vehicle] === Assembly scan: vehicle-named types ===");
            int found = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.Name == null) continue;
                    string lower = t.Name.ToLowerInvariant();
                    bool hit = false;
                    foreach (var kw in _typeKeywords)
                        if (lower.Contains(kw)) { hit = true; break; }
                    if (!hit) continue;

                    Plugin.Logger.LogInfo($"[Vehicle]   {t.FullName}");
                    found++;
                }
            }
            Plugin.Logger.LogInfo($"[Vehicle] === Assembly scan done: {found} type(s) ===");
        }

        // ── Vehicle sync ──────────────────────────────────────────────────────
        //
        // Local side: enumerate every owned vehicle (VehicleHelper.AllPlayerVehicles)
        // and broadcast the whole fleet — parked and driven — at ~10 Hz.  The fleet
        // is the complete truth for that owner.
        // Remote side: each vehicle is a persistent "ghost" spawned with the real
        // game vehicle (CreateAndSpawnVehicle), kept out of our owned set, physics
        // frozen, interaction + ownership icon suppressed, an owner-name label
        // above it, driven purely by the synced transform.  A ghost is despawned
        // only when its vehicle drops out of the owner's fleet (sold) or the owner
        // disconnects.

        private sealed class RemoteVehicle
        {
            public string      OwnerId   = "";
            public string      TypeName  = "";   // vehicle type id — needed to run the game's own block-cull around this ghost
            public GameObject? Go;
            public Transform?  Label;
            public Vector3     TargetPos;
            public Quaternion  TargetRot = Quaternion.identity;
            // Dead reckoning (same as traffic ghosts): chase a target
            // extrapolated along measured velocity so a driven car never
            // stutters between network updates at low FPS.
            public Vector3     Velocity;
            public float       TargetAt;   // receiver clock at arrival (extrapolation base)
            public float       SenderT;    // sender's sample clock (velocity measurement)
            public string      AppliedCargo = "";   // manifest|carried sig the ghost was built with
            public Transform   LoadingPos;          // vehicleLoadingPosition (cargo-load spot), captured pre-strip
        }
        private const float MaxVehicleExtrapolateSeconds = 0.3f;
        private const float VehicleSnapDistance          = 15f;
        // Last-seen signature of the owners who grant the local player a key — when it changes, ghosts
        // are respawned so their drivability updates (see OnGrantsChanged).
        private static string _lastGrantorSig = "";

        // ── Shared-storage accessors (a ghost's owner + synced cargo, for the storage panel) ──
        /// <summary>The owner of a ghost vehicle (empty if unknown).</summary>
        public static string OwnerIdFor(string vid)
            => _remoteVehicles.TryGetValue(vid, out var rv) && rv != null ? rv.OwnerId : "";

        /// <summary>The vehicle-type id of a ghost (empty if unknown).</summary>
        public static string TypeNameFor(string vid)
            => _remoteVehicles.TryGetValue(vid, out var rv) && rv != null ? rv.TypeName : "";

        /// <summary>The ghost's synced loose cargo as (itemName, amount) rows, parsed from the manifest the
        /// ghost was built with (AppliedCargo = "item=amt;...|carried"). EA0.11 stores ALL loose vehicle
        /// cargo — car trunks AND flatbed/hand-trucks — in cargoInstances, which this manifest captures;
        /// the legacy cargoIds path is migrated away (UpdateItemInstancesToNewSystem). Manifest caps at 24
        /// stacks (ReadLocalFleet), so deeper storages list their first 24.</summary>
        public static System.Collections.Generic.List<(string item, int amount)> GhostCargoFor(string vid)
        {
            var rows = new System.Collections.Generic.List<(string, int)>();
            if (!_remoteVehicles.TryGetValue(vid, out var rv) || rv == null) return rows;
            string sig = rv.AppliedCargo ?? "";
            int bar = sig.IndexOf('|');
            string cargo = bar >= 0 ? sig.Substring(0, bar) : sig;
            foreach (var entry in cargo.Split(';'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                int eq = entry.LastIndexOf('=');   // '=' separator: EA 0.11 item ids contain ':'
                if (eq <= 0) continue;
                if (int.TryParse(entry.Substring(eq + 1), out int amt) && amt > 0)
                    rows.Add((entry.Substring(0, eq), amt));
            }
            return rows;
        }

        /// <summary>Vehicle cargo capacity (VehicleType.maxCargoCapacity) for the "Boxes: used/max" header;
        /// 0 if unknown. Resolved locally from the ghost's type — clients hold all VehicleType definitions.</summary>
        public static int MaxCargoFor(string vid)
        {
            try
            {
                var tn = TypeNameFor(vid);
                if (string.IsNullOrEmpty(tn)) return 0;
                var vt = Vehicles.VehicleTypes.VehicleTypeHelper.GetVehicleType(tn);
                return vt != null ? vt.maxCargoCapacity : 0;
            }
            catch { return 0; }
        }

        /// <summary>World spot the owner walks to when loading cargo (vehicleLoadingPosition); the ghost's own
        /// position if that wasn't captured; Vector3.zero if the ghost is unknown. Lets a non-owner deposit from
        /// the same place the owner would.</summary>
        public static Vector3 LoadingSpotFor(string vid)
        {
            try
            {
                if (_remoteVehicles.TryGetValue(vid, out var rv) && rv != null)
                {
                    if (rv.LoadingPos != null) return rv.LoadingPos.position;
                    if (rv.Go != null) return rv.Go.transform.position;
                }
            }
            catch { }
            return Vector3.zero;
        }

        // Gameplay components destroyed on a ghost so the game stops treating it
        // as a vehicle anyone owns — no map icon, no tickets, not enterable.
        // Visual components (renderers, CarFeatures which holds the colour) stay.
        private static readonly string[] _killVehicleComponents =
        {
            "VehicleController", "CarController", "DamageHandler",
            "VehicleDeformationController", "WheelControllerManager",
            "FuelModuleWrapper", "SpeedLimiterModuleWrapper",
            "FlipOverModuleWrapper", "VariableCenterOfMass",
            // Bike-only [RequireComponent(VehicleController)] wrappers — without these the
            // VehicleController couldn't be removed on motorcycle/scooter ghosts ("Can't remove
            // VehicleController because ArcadeModuleWrapper/MotorcycleModuleWrapper depends on it"),
            // leaving the ghost a LIVE native vehicle. StripVehicleComponents destroys these (in
            // "others") before the controller, so the controller removal now succeeds.
            "ArcadeModuleWrapper", "MotorcycleModuleWrapper",
            // Scooter drive controller. Its Start() launches a WaitForStationary coroutine that derefs
            // the (stripped) CarController/VehicleController on a scooter GHOST → an unhandled
            // NullReferenceException (~5×/session). Stripping it before its Start runs (the strip is
            // synchronous at spawn; Start fires next frame) stops the coroutine ever launching. Only
            // ghosts go through StripVehicleComponents, so real ridable scooters keep it.
            "ScooterController",
        };

        // Keyed by VehicleId (a player owns several vehicles).
        private static readonly Dictionary<string, RemoteVehicle> _remoteVehicles = new();

        private static int _lastAmbientCullFrame = -1;

        /// <summary>HOST-ONLY. For each PARKED (stationary) remote-vehicle ghost, run the game's OWN
        /// VehicleHelper.DestroyBlockingVehicles — the identical routine it already runs for the host's own
        /// cars (VehicleHelper.DestroyBlockingParkedVehicles over AllPlayerVehicles) — so an ambient parked
        /// car overlapping a CLIENT's parked car is released. The release goes through the game's
        /// ParkingSimulator.ReleaseParkedVehicle, so our parked-car sync propagates the removal to everyone
        /// (one consistent world; the spot repopulates via normal lane regeneration when the car leaves).
        ///
        /// SAFETY: DestroyBlockingVehicles(onlyParkedVehicles:true) releases ONLY colliders on
        /// ParkedVehiclesLayer (ambient decorations). A ghost is a CreateAndSpawnVehicle vehicle on
        /// PlayerVehiclesLayer (we never reassign its layer), so it is only the overlap box's REFERENCE
        /// POINT and can never itself be released — the same reason the host's own parked car is safe.
        /// Moving (being-driven) ghosts are skipped via the velocity gate, mirroring the game's own
        /// skipPlayerMountedVehicles.</summary>
        public static void CullBlockingAmbientAroundParkedGhosts()
        {
            if (!MPServer.IsRunning) return;                         // host is authoritative for ambient cars
            if (Time.frameCount == _lastAmbientCullFrame) return;    // once per frame even if many lanes regen at once
            _lastAmbientCullFrame = Time.frameCount;
            try
            {
                foreach (var rv in _remoteVehicles.Values)
                {
                    if (rv?.Go == null) continue;
                    if (rv.Velocity.sqrMagnitude > 0.25f) continue;  // ~>0.5 m/s = being driven → skip (like skipPlayerMountedVehicles)
                    try
                    {
                        var vt = Vehicles.VehicleTypes.VehicleTypeHelper.GetVehicleType(rv.TypeName);
                        Helpers.VehicleHelper.DestroyBlockingVehicles(rv.Go, vt, onlyParkedVehicles: true);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[ParkedSync] cull around ghost '{rv.TypeName}': {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[ParkedSync] CullBlockingAmbientAroundParkedGhosts: {ex.Message}"); }
        }

        /// <summary>Reads the local player's full vehicle fleet, or null if not ready.</summary>
        public static VehicleFleetPayload? ReadLocalFleet()
        {
            try
            {
                var list = VehicleHelper.AllPlayerVehicles;
                if (list == null) return null;

                Transform? openDriven = null;
                var fleet = new VehicleFleetPayload { OwnerId = MPConfig.PlayerId, T = Time.unscaledTime };
                for (int i = 0; i < list.Count; i++)
                {
                    var vc = list[i];
                    if (vc == null) continue;
                    var inst = vc.vehicleInstance;
                    if (inst == null || string.IsNullOrEmpty(inst.id)) continue;
                    // Ghost-leak guard: 'BAMP_BAMP_…' ids in a REAL fleet are
                    // ghosts that slipped into someone's VehicleInstances across
                    // a save/load (run-16 evidence: triple-prefixed rig id).
                    // Never re-broadcast them — that snowballs prefix-per-cycle.
                    // Never re-broadcast a ghost/proxy as our OWN vehicle. A GRANTED drivable proxy stays in
                    // AllPlayerVehicles (so the game can drive it) but must not be broadcast as ours; a
                    // 'BAMP_BAMP…' id is additionally a save-cycle leak (warn once).
                    if (inst.id.StartsWith("BAMP_"))
                    {
                        if (inst.id.Contains("BAMP_BAMP") && !_lastManifestLogged.ContainsKey(inst.id))
                        {
                            _lastManifestLogged[inst.id] = "LEAKED";
                            Plugin.Logger.LogWarning($"[Vehicle] leaked ghost in local fleet skipped: '{inst.id}' (save-cycle leak — root cause backlogged).");
                        }
                        continue;
                    }
                    var t = vc.transform;
                    string tn = inst.vehicleTypeName.ToString();
                    // Ground-truth capture: while WE drive/push, record the exact
                    // character-to-vehicle offset + animator state so the remote
                    // rendering can be modeled from data instead of guesses.
                    if (vc.controlledByPlayer)
                    {
                        if (IsOpenVehicle(tn)) openDriven = t;   // hand-IK sync anchor
                    }
                    // Cargo manifest → remote bed/handtruck boxes (visual only).
                    string cargo = "";
                    try
                    {
                        if (inst.cargoInstances != null && inst.cargoInstances.Count > 0)
                        {
                            var csb = new System.Text.StringBuilder();
                            for (int ci = 0; ci < inst.cargoInstances.Count && ci < 24; ci++)
                            {
                                var c = inst.cargoInstances[ci];
                                if (c == null) continue;
                                // '=' separator: EA 0.11 item ids CONTAIN colons
                                // ("ba:itemname_cheapgift") — ':' made the parser
                                // skip every entry (no boxes on remote beds).
                                csb.Append(c.itemName).Append('=').Append(c.amount).Append(';');
                            }
                            cargo = csb.ToString();
                        }
                    }
                    catch { }
                    // Carts transport ITEM INSTANCES (cargoIds), not loose cargo
                    // — run-16 evidence: HandTruck manifest "(empty)" with a box
                    // on it.  Ship the count; ghosts render generic boxes.
                    int carried = 0;
                    try { carried = inst.cargoIds?.Count ?? 0; } catch { }

                    string manifestSig = $"{cargo}|{carried}";
                    if (!_lastManifestLogged.TryGetValue(inst.id, out var prevCargo) || prevCargo != manifestSig)
                    {
                        _lastManifestLogged[inst.id] = manifestSig;
                        Plugin.Logger.LogInfo($"[Vehicle] manifest {tn} '{inst.id}': {(cargo.Length > 0 ? cargo : "(empty)")} carried={carried}");
                    }

                    // Cross-interior tag (v2 — replaces the owner-proximity
                    // heuristic): a vehicle near ME while I'm inside a building
                    // belongs to that interior; near me outside → outdoors; far
                    // from me → keep its last tag (left where it was).
                    string bldg = _lastVehicleBldg.TryGetValue(inst.id, out var prevB) ? prevB : "";
                    try
                    {
                        var me = Helpers.PlayerHelper.PlayerController?.Character?.transform;
                        if (me != null && UnityEngine.Vector3.Distance(me.position, t.position) < 30f)
                            bldg = MPRegisterSync.CurrentShopAddress;   // "" when outdoors
                    }
                    catch { }
                    _lastVehicleBldg[inst.id] = bldg;

                    // Live fuel (CarController.fuelModule) if it's a car, else the persisted instance value.
                    float fuel = inst.fuel;
                    try { var cc = vc as CarController; if (cc != null && cc.fuelModule != null) fuel = cc.fuelModule.amount; } catch { }

                    fleet.Vehicles.Add(new VehicleEntry
                    {
                        VehicleId = inst.id,
                        TypeName  = tn,
                        ColorName = inst.vehicleColorName ?? "",
                        Driving   = vc.controlledByPlayer,
                        Fuel      = fuel,
                        X = t.position.x, Y = t.position.y, Z = t.position.z,
                        Qx = t.rotation.x, Qy = t.rotation.y, Qz = t.rotation.z, Qw = t.rotation.w,
                        Cargo = cargo,
                        CarriedItems = carried,
                        Bldg  = bldg,
                    });
                }
                CurrentOpenDriven = openDriven;
                return fleet;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] ReadLocalFleet: {ex.Message}");
                return null;
            }
        }

        /// <summary>The MP vehicleId of the vehicle the LOCAL player is currently driving, or ""
        /// (used by the in-car lock toggle to know which vehicle the owner is in).</summary>
        public static string CurrentDrivenVehicleId()
        {
            try
            {
                var list = VehicleHelper.AllPlayerVehicles;
                if (list == null) return "";
                for (int i = 0; i < list.Count; i++)
                {
                    var vc = list[i];
                    if (vc != null && vc.controlledByPlayer && vc.vehicleInstance != null
                        && !string.IsNullOrEmpty(vc.vehicleInstance.id))
                        return vc.vehicleInstance.id;
                }
            }
            catch { }
            return "";
        }

        /// <summary>The open vehicle WE are currently pushing/riding, if any —
        /// the vehicle-local anchor for the hand-IK sync (refreshed each fleet
        /// read; null when on foot or in an enclosed vehicle).</summary>
        public static Transform? CurrentOpenDriven;

        /// <summary>Applies a remote player's full vehicle fleet (main thread).</summary>
        // CLAUDE-DIAGNOSTIC — kill-switch gate for owned-vehicle fleet sync.
        // When false, ApplyVehicleFleet returns early (no ghost vehicles
        // spawned for the host's owned cars).  Used by F4 master kill switch.
        public static bool ClientApplyFleetEnabled { get; set; } = true;

        // Sender-side per-vehicle state: last logged cargo manifest (handcart
        // evidence) and last interior tag (cross-interior mask v2).
        private static readonly Dictionary<string, string> _lastManifestLogged = new();
        private static readonly Dictionary<string, string> _lastVehicleBldg = new();

        public static void ApplyVehicleFleet(VehicleFleetPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.OwnerId)) return;
            if (SaveGameManager.Current == null) return;
            if (!ClientApplyFleetEnabled) return;     // CLAUDE-DIAGNOSTIC F4 gate
            try
            {
                var seen = new HashSet<string>();
                bool anyDriving = false;

                foreach (var e in p.Vehicles)
                {
                    if (string.IsNullOrEmpty(e.VehicleId)) continue;
                    seen.Add(e.VehicleId);
                    if (e.Driving) anyDriving = true;

                    var pos = new Vector3(e.X, e.Y, e.Z);
                    var rot = new Quaternion(e.Qx, e.Qy, e.Qz, e.Qw);

                    string cargoSig = $"{e.Cargo}|{e.CarriedItems}";
                    if (!_remoteVehicles.TryGetValue(e.VehicleId, out var rv) || rv.Go == null)
                    {
                        var spawned = SpawnRemoteVehicle(p.OwnerId, e, pos, rot);
                        if (spawned == null) continue;
                        rv = spawned;
                        rv.TargetAt = Time.unscaledTime;
                        rv.AppliedCargo = cargoSig;
                        _remoteVehicles[e.VehicleId] = rv;
                    }
                    else if (rv.AppliedCargo != cargoSig)
                    {
                        // Owner loaded/unloaded — ghost cargo visuals are baked
                        // at spawn, so rebuild the ghost (cheap; cargo changes
                        // are infrequent).  Fixes stale bed boxes (run-16).
                        DespawnByVehicleId(e.VehicleId);
                        var fresh = SpawnRemoteVehicle(p.OwnerId, e, pos, rot);
                        if (fresh == null) continue;
                        rv = fresh;
                        rv.TargetAt = Time.unscaledTime;
                        rv.AppliedCargo = cargoSig;
                        _remoteVehicles[e.VehicleId] = rv;
                        Plugin.Logger.LogInfo($"[Vehicle] ghost '{e.TypeName}' ({e.VehicleId}) rebuilt — cargo changed ({cargoSig}).");
                    }
                    if (rv.Go != null && Vector3.Distance(rv.Go.transform.position, pos) > VehicleSnapDistance)
                    {
                        // Teleport (ferry, recovery) — snap, don't slide.
                        rv.Go.transform.position = pos;
                        rv.Go.transform.rotation = rot;
                        rv.Velocity = Vector3.zero;
                    }
                    else
                    {
                        // Velocity from the SENDER's clock (packet stamp); tiny
                        // delta = two packets in one frame → keep prior velocity.
                        float vdt = p.T - rv.SenderT;
                        if (vdt > 0.005f)
                            rv.Velocity = (pos - rv.TargetPos) / vdt;
                    }
                    rv.TargetPos = pos;
                    rv.TargetRot = rot;
                    rv.TargetAt  = Time.unscaledTime;
                    rv.SenderT   = p.T;

                    // Keep a drivable (granted) proxy fueled from the owner's car so it isn't stuck at 0%.
                    // Skipped while WE drive it (controlledByPlayer) so local consumption isn't clobbered.
                    if (rv.Go != null) ApplyFuelToGhost(rv.Go, e.Fuel);

                    // Cross-interior mask v2 (per-vehicle building tag — fixes the
                    // vehicle-LEFT-inside case): show the ghost only when its tag
                    // matches MY current building ("" = outdoors, always shown).
                    if (rv.Go != null)
                    {
                        bool maskVeh = !string.IsNullOrEmpty(e.Bldg)
                                       && e.Bldg != MPRegisterSync.CurrentShopAddress;
                        if (rv.Go.activeSelf == maskVeh)
                        {
                            rv.Go.SetActive(!maskVeh);
                            Plugin.Logger.LogInfo(
                                $"[InteriorMask] ghost '{e.TypeName}' ({e.VehicleId}) {(maskVeh ? "hidden" : "shown")} — " +
                                $"tag='{e.Bldg}' mine='{MPRegisterSync.CurrentShopAddress}'.");
                        }
                    }
                }

                // Vehicles this owner no longer lists have been sold — despawn them.
                var stale = _remoteVehicles
                    .Where(kv => kv.Value.OwnerId == p.OwnerId && !seen.Contains(kv.Key))
                    .Select(kv => kv.Key).ToList();
                foreach (var id in stale) DespawnByVehicleId(id);

                // Hide the owner's walking model only when driving an ENCLOSED
                // vehicle.  On OPEN ones (scooters, push carts/dollies) the
                // rider/pusher must stay visible — hiding them made carts roll
                // around with nobody pushing (user report 2026-06-10).  The
                // avatar's own position sync keeps it at the vehicle.
                bool hideAvatar = false;
                Transform? ride = null;
                Vector3 rideOff = Vector3.zero;
                foreach (var e in p.Vehicles)
                {
                    if (!e.Driving) continue;
                    if (IsOpenVehicle(e.TypeName))
                    {
                        if (_openVehicleLogged.Add(e.TypeName))
                            Plugin.Logger.LogInfo($"[Vehicle] '{e.TypeName}' driven — OPEN vehicle, avatar stays visible.");
                        // Pin the avatar to the vehicle ghost (separately smoothed
                        // streams drift apart — the pusher floated off the cart).
                        if (_remoteVehicles.TryGetValue(e.VehicleId, out var rvm) && rvm.Go != null)
                        {
                            ride    = rvm.Go.transform;
                            rideOff = RideOffsetFor(e.TypeName);
                        }
                        else if (_openVehicleLogged.Add(e.TypeName + ":noghost"))
                        {
                            Plugin.Logger.LogWarning($"[RideDiag] open '{e.TypeName}' driven but its ghost is NOT in _remoteVehicles — ride pin INACTIVE (id={e.VehicleId}).");
                        }
                    }
                    else
                    {
                        hideAvatar = true;
                        if (_openVehicleLogged.Add(e.TypeName))
                            Plugin.Logger.LogInfo($"[Vehicle] '{e.TypeName}' driven — enclosed, avatar hidden (add to OpenVehicleNames if wrong).");
                    }
                }
                RemotePlayerManager.SetDriving(p.OwnerId, hideAvatar);
                RemotePlayerManager.SetRide(p.OwnerId, ride, rideOff);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] ApplyVehicleFleet '{p.OwnerId}': {ex.Message}");
            }
        }

        // Vehicle types the player rides ON / pushes (avatar stays visible).
        // Matched as substrings of VehicleTypeName — every driven type gets
        // logged once so this list can be refined from real data.
        private static readonly string[] OpenVehicleNames =
            { "scooter", "cart", "dolly", "dollie", "trolley", "bike", "moped", "pallet", "hand",
              "flatbed", "barrow", "wagon" };   // 'Flatbed' confirmed from the driven-type log 2026-06-10
        private static readonly HashSet<string> _openVehicleLogged = new();

        private static bool IsOpenVehicle(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            // The game's own flag (decompile sweep 2026-06-12): VehicleType.enclosed
            // is exactly what this substring list was guessing at.
            try
            {
                var vt = Vehicles.VehicleTypes.VehicleTypeHelper.GetVehicleType(typeName);
                if (vt != null) return !vt.enclosed;
            }
            catch { }
            // Fallback heuristic for unknown/mod types only.
            foreach (var k in OpenVehicleNames)
                if (typeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Root-cause fix for the ghost-vehicle save leak (decompile
        /// sweep 2026-06-12): CreateAndSpawnVehicle UNCONDITIONALLY adds the
        /// instance to SaveGameManager.Current.VehicleInstances — save data.
        /// Deregister our visual-only ghosts immediately at spawn; the save-time
        /// strip stays as backstop.</summary>
        private static void DeregisterGhostFromSave(VehicleInstance? inst)
        {
            try
            {
                var list = SaveGameManager.Current?.VehicleInstances;
                if (list != null && inst != null && list.Remove(inst))
                    Plugin.Logger.LogInfo($"[Vehicle] ghost '{inst.id}' deregistered from save data at spawn.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] ghost deregister: {ex.Message}"); }
        }

        /// <summary>Where the rider/pusher stands relative to the vehicle ghost.
        /// Geometry-probe-MEASURED (2026-06-10): the character TRANSFORM sits at
        /// the vehicle transform, but the game's pushing system RENDERS the
        /// body 1.00m behind it (sender bodyInVeh z=-1.08 vs observer -0.08,
        /// cart visuals identical).  That system is script-driven and absent on
        /// clones, so the pin reproduces it: pushables -1.0m; rideables stand
        /// on the deck at the root (scooter verified fine at zero).</summary>
        private static Vector3 RideOffsetFor(string typeName)
        {
            bool rideOn = typeName.IndexOf("scooter", StringComparison.OrdinalIgnoreCase) >= 0
                       || typeName.IndexOf("bike",    StringComparison.OrdinalIgnoreCase) >= 0
                       || typeName.IndexOf("moped",   StringComparison.OrdinalIgnoreCase) >= 0;
            return rideOn ? Vector3.zero : new Vector3(0f, 0f, -1.0f);
        }

        /// <summary>Fill a (ghost) VehicleInstance's cargo from the manifest
        /// string "itemId=amount;…" so the visual boxes appear remotely.
        /// ('=' separator — EA 0.11 item ids contain colons.)
        /// Unknown item names are skipped (version drift safe).</summary>
        private static void ApplyCargoManifest(VehicleInstance inst, string? manifest)
        {
            if (string.IsNullOrEmpty(manifest)) return;
            try
            {
                if (inst.cargoInstances == null) return;
                foreach (var part in manifest.Split(';'))
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    var bits = part.Split('=');
                    if (bits.Length != 2) continue;
                    if (string.IsNullOrEmpty(bits[0])) continue;
                    if (!int.TryParse(bits[1], out var amount) || amount <= 0) continue;
                    inst.cargoInstances.Add(new BigAmbitions.Items.CargoInstance(bits[0], amount, 0f, true));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] cargo manifest: {ex.Message}"); }
        }

        private static RemoteVehicle? SpawnRemoteVehicle(string ownerId, VehicleEntry e,
                                                         Vector3 pos, Quaternion rot)
        {
            VehicleInstance inst;
            try
            {
                inst = new VehicleInstance(e.TypeName);   // EA 0.11: string type id, ctor-required
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] new VehicleInstance('{e.TypeName}') failed: {ex.Message}");
                return null;
            }
            try
            {
                inst.id               = "BAMP_" + e.VehicleId;
                inst.vehicleColorName = e.ColorName;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] VehicleInstance setup ('{e.TypeName}'): {ex.Message}");
            }
            ApplyCargoManifest(inst, e.Cargo);   // bed/handtruck boxes derive from cargo
            // Carried ITEM instances (hand-truck channel): we can't replicate
            // another machine's loose items, so render generic stand-in cargo —
            // one unit per carried item (ghost-only; flatbed-proven renderer).
            try
            {
                for (int ci = 0; ci < e.CarriedItems && ci < 12; ci++)
                    inst.cargoInstances?.Add(new BigAmbitions.Items.CargoInstance(
                        "ba:itemname_cheapgift", 1, 0f, true));
            }
            catch { }

            int ownedBefore = SafeCount(() => VehicleHelper.AllPlayerVehicles?.Count ?? -1);

            VehicleController? vc;
            try
            {
                vc = VehicleHelper.CreateAndSpawnVehicle(inst, pos, rot);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] CreateAndSpawnVehicle failed: {ex.Message}");
                return null;
            }
            if (vc == null)
            {
                Plugin.Logger.LogWarning("[Vehicle] CreateAndSpawnVehicle returned null.");
                return null;
            }
            DeregisterGhostFromSave(inst);   // remote ghost: never save data → off the borrower's books (no tickets/tax)

            var go  = vc.gameObject;
            int ownedAfterSpawn = SafeCount(() => VehicleHelper.AllPlayerVehicles?.Count ?? -1);
            string poiName;
            try { poiName = vc.poi != null ? vc.poi.gameObject.name : "(null)"; }
            catch { poiName = "(error)"; }

            // A car whose owner has GRANTED the local player a key stays a real, DRIVABLE vehicle: keep the
            // controller AND keep it in AllPlayerVehicles, so VehicleHelper.GetCurrentVehicle() finds it and the
            // game's enter/drive flow works (GetCurrentVehicle scanning AllPlayerVehicles is exactly what NRE'd
            // when we unregistered it). It's still off the save (above) + off our fleet broadcast (ReadLocalFleet
            // skips BAMP_ ids), so the borrower takes no charges and never re-broadcasts it as their own.
            bool drivable = !string.IsNullOrEmpty(ownerId) && GrantSync.IsGranted(ownerId, MPConfig.PlayerId);

            // De-register from the player vehicle system — but NOT a drivable (granted) proxy.
            if (!drivable) { try { VehicleHelper.UnregisterPlayerVehicle(vc); } catch { } }
            // Destroy the point-of-interest (map/world "owned" icon) AND null the field. EnterVehicle calls
            // poi?.SetHidden(true); a DESTROYED-but-non-null Unity object slips past the C# null-conditional
            // and NREs inside SetHidden (this aborted entry when driving a granted proxy). Nulling makes the
            // poi?.  and  if (poi != null)  checks in EnterVehicle skip cleanly.
            try { if (vc.poi != null) { UnityEngine.Object.Destroy(vc.poi.gameObject); vc.poi = null; } } catch { }
            // Destroy every gameplay component — after this the game no longer
            // sees a vehicle here, just a prop: no ownership, no ticket, no entry.
            // Capture the vehicle's sleep environment BEFORE stripping the controller — a passenger
            // riding this ghost can't reach the real VehicleController, so this is their only handle
            // on it (PassengerHud's Sleep button uses it). Best-effort; null if not yet populated.
            try { if (vc.sleepEnvironment != null) _sleepEnvByVehicleId[e.VehicleId] = vc.sleepEnvironment; } catch { }
            // Capture the cargo-loading spot (where the owner walks to load cargo) BEFORE stripping the
            // controller, so a non-owner depositing into this ghost walks to the SAME place (PassengerRide).
            // Reflected up the type hierarchy (the field lives on the VehicleController base, vc may be CarController).
            Transform loadingPos = null;
            try
            {
                for (var t = vc.GetType(); t != null && loadingPos == null; t = t.BaseType)
                {
                    var f = t.GetField("vehicleLoadingPosition",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null) loadingPos = f.GetValue(vc) as Transform;
                }
            }
            catch { }
            int killed;
            if (drivable)
            {
                killed = 0;   // keep the controller → drivable (see the comment above).
                Plugin.Logger.LogInfo($"[Drive] ghost '{e.TypeName}' for '{ownerId}' spawned DRIVABLE (granted — controller kept, registered).");
            }
            else killed = StripVehicleComponents(go);
            // Freeze physics — we drive the ghost purely by the synced transform.
            // EVERY rigidbody in the hierarchy, same as SpawnVisualGhost: this
            // path froze only the ROOT, and the 0.11 prefabs carry dynamic
            // child rigidbodies — the client could shove the host's parked
            // vehicles by walking into them (resurfaced bug, 2026-06-12).
            try
            {
                var rbs = go.GetComponentsInChildren(typeof(Rigidbody), true);
                if (rbs != null)
                    for (int i = 0; i < rbs.Length; i++)
                    {
                        var rb = rbs[i] as Rigidbody;
                        if (rb != null) rb.isKinematic = true;
                    }
            }
            catch { }

            int ownedAfterStrip = SafeCount(() => VehicleHelper.AllPlayerVehicles?.Count ?? -1);
            Plugin.Logger.LogInfo(
                $"[Vehicle] Ghost '{e.TypeName}' for '{ownerId}': AllPlayerVehicles " +
                $"{ownedBefore}→{ownedAfterSpawn}→{ownedAfterStrip}, poi='{poiName}', " +
                $"{killed} gameplay component(s) destroyed.");

#if BAMP_DEV
            VehicleHierarchyProbe.DumpOnce(go, e.TypeName);   // DIAG:DEVTOOL — passenger door/seat discovery (once per type)
#endif
            var label = CreateOwnerLabel(go, ownerId);
            return new RemoteVehicle
            {
                OwnerId    = ownerId,
                TypeName   = e.TypeName,
                Go         = go,
                LoadingPos = loadingPos,
                Label      = label,
                TargetPos  = pos,
                TargetRot  = rot,
            };
        }

#if BAMP_DEV
        private static readonly System.Collections.Generic.List<string> _devProbeBatch = new();

        // DIAG:INVESTIGATION(passenger-doors) — spawn the next few not-yet-probed vehicle types
        //   in a ROW beside the local player so VehicleHierarchyProbe dumps their wheel/door
        //   data AND they stay VISIBLE for eyeballing 2-seater vs 4-seater. Each press first
        //   despawns the previous batch. Returns how many types remain uncollected. Works in SP.
        public static int DevProbeUncollected(int maxCount)
        {
            int spawned = 0, remaining = 0;
            try
            {
                // Clear the previous viewing batch.
                foreach (var id in _devProbeBatch) { try { DespawnByVehicleId(id); } catch { } }
                _devProbeBatch.Clear();

                Vector3 basePos = Vector3.zero, right = Vector3.right, fwd = Vector3.forward;
                try
                {
                    var ch = Helpers.PlayerHelper.PlayerController.Character.transform;
                    basePos = ch.position; right = ch.right; fwd = ch.forward;
                }
                catch { }
                var names = VehicleTypeHelper.GetVehicleTypeNames();
                if (names == null || names.Count == 0)
                {
                    Plugin.Logger.LogWarning("[VehProbe] no vehicle types available yet " +
                        "(GetVehicleTypeNames empty) — be in a loaded game and try again.");
                    return 0;
                }
                Plugin.Logger.LogInfo($"[VehProbe] {names.Count} vehicle type(s) total; finding uncollected…");
                foreach (var type in names)
                {
                    if (string.IsNullOrEmpty(type) || VehicleHierarchyProbe.HasDumped(type)) continue;
                    if (spawned >= maxCount) { remaining++; continue; }
                    // A row ahead-and-right of the player, ~4 m apart, so they don't overlap.
                    var pos = basePos + fwd * 6f + right * (spawned * 4f) + Vector3.up * 0.5f;
                    var entry = new VehicleEntry { VehicleId = "PROBE_" + type, TypeName = type,
                        X = pos.x, Y = pos.y, Z = pos.z, Qw = 1f };
                    var rv = SpawnRemoteVehicle(MPConfig.PlayerId, entry, pos, Quaternion.identity);
                    if (rv != null)
                    {
                        _devProbeBatch.Add(entry.VehicleId);   // leave visible; probe already fired
                        // Relabel the floating tag with the TYPE NAME so each car is identifiable
                        // on sight (you can't enter a stripped ghost to read its name).
                        try
                        {
                            var tag = rv.Label != null ? rv.Label.GetComponent<TextMeshProUGUI>() : null;
                            if (tag != null) { tag.text = type.Replace("ba:vehicletype_", ""); tag.color = new Color(0.4f, 0.9f, 1f); }
                        }
                        catch { }
                        spawned++;
                    }
                }
                Plugin.Logger.LogInfo($"[VehProbe] F5: spawned {spawned} car(s) beside you to view (data dumped); {remaining} type(s) still uncollected — F5 again for the next batch.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[VehProbe] DevProbeUncollected: {ex.Message}"); }
            return remaining;
        }
#endif

        /// <summary>
        /// Spawns a pure-visual vehicle ghost of the given model: a real game
        /// vehicle, immediately demoted to an inert prop (de-registered, POI
        /// destroyed, gameplay components stripped, physics frozen).  Used for
        /// both remote-player vehicles and AI-traffic ghosts.  Returns the
        /// GameObject, or null on failure.
        /// </summary>
        public static GameObject? SpawnVisualGhost(string typeName, Vector3 pos, Quaternion rot)
        {
            // EA 0.11: vehicleTypeName is a ctor-required string; validate via the
            // game's own lookup so unknown names (e.g. Taxi) fall back cleanly.
            bool known = false;
            try { known = Vehicles.VehicleTypes.VehicleTypeHelper.GetVehicleType(typeName) != null; }
            catch { }
            VehicleInstance inst;
            try { inst = new VehicleInstance(known ? typeName : "ba:vehicletype_vordv150"); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] ghost VehicleInstance failed: {ex.Message}");
                return null;
            }
            try { inst.id = "BAMP_ghost"; } catch { }

            VehicleController? vc;
            try { vc = VehicleHelper.CreateAndSpawnVehicle(inst, pos, rot); }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Vehicle] ghost CreateAndSpawnVehicle failed: {ex.Message}");
                return null;
            }
            if (vc == null) return null;
            DeregisterGhostFromSave(inst);   // visual ghost: never save data

            var go = vc.gameObject;
            try { VehicleHelper.UnregisterPlayerVehicle(vc); } catch { }
            try { if (vc.poi != null) UnityEngine.Object.Destroy(vc.poi.gameObject); } catch { }
            StripVehicleComponents(go);
            try
            {
                // EVERY rigidbody in the hierarchy, not just the root — a dynamic
                // child rb lets the local player physically shove the ghost.
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

        /// <summary>Destroy any Camera hiding in a cloned ghost's hierarchy — a
        /// stowaway ENABLED camera can hijack Camera.main, making the game's
        /// cursor pick-ray fire from the wrong eye (buildings highlight away
        /// from the cursor — 2026-06-10 investigation).</summary>
        public static int StripCameras(GameObject go)
        {
            int n = 0;
            try
            {
                var cams = go.GetComponentsInChildren(typeof(Camera), true);
                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i] as Camera;
                    if (c == null) continue;
                    UnityEngine.Object.Destroy(c);
                    n++;
                }
                if (n > 0)
                    Plugin.Logger.LogWarning($"[Ghost] STRIPPED {n} stowaway camera(s) from clone '{go.name}' — these hijack the cursor pick ray!");
            }
            catch { }
            return n;
        }

        /// <summary>Destroys the gameplay components that make a GameObject "a vehicle".</summary>
        private static int StripVehicleComponents(GameObject go)
        {
            int n = 0;
            StripCameras(go);
            try
            {
                // VehicleController has [RequireComponent] dependents (Fuel/SpeedLimiter/FlipOver wrappers +
                // DamageHandler). Deferred Destroy doesn't guarantee those go first, so the controller SURVIVED
                // ("Can't remove VehicleController …") — leaving the ghost a LIVE native vehicle whose own
                // click-interaction (walk-to-door to enter) fought our deposit-walk. Remove with DestroyImmediate
                // in dependency order: every other kill-listed component first, the controllers LAST.
                var controllers = new System.Collections.Generic.List<Component>();
                var others      = new System.Collections.Generic.List<Component>();
                var comps = go.GetComponents(typeof(Component));
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    var tn = c.GetType().Name;
                    if (System.Array.IndexOf(_killVehicleComponents, tn) < 0) continue;
                    if (tn == "VehicleController" || tn == "CarController") controllers.Add(c);
                    else                                                    others.Add(c);
                }
                foreach (var c in others)      if (c != null) { UnityEngine.Object.DestroyImmediate(c); n++; }
                foreach (var c in controllers) if (c != null) { UnityEngine.Object.DestroyImmediate(c); n++; }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] StripVehicleComponents: {ex.Message}");
            }
            return n;
        }

        private static int SafeCount(Func<int> f)
        {
            try { return f(); } catch { return -1; }
        }

        /// <summary>Creates a world-space owner-name label above a ghost vehicle.</summary>
        private static Transform? CreateOwnerLabel(GameObject vehicle, string ownerId)
        {
            try
            {
                var labelGO = new GameObject("BAMP_VehicleLabel");
                labelGO.transform.SetParent(vehicle.transform, false);
                labelGO.transform.localPosition = new Vector3(0f, 2.4f, 0f);

                var canvas = labelGO.AddComponent<Canvas>();
                canvas.renderMode  = RenderMode.WorldSpace;
                canvas.sortingOrder = 10;

                var rt = labelGO.GetComponent<RectTransform>();
                rt.sizeDelta  = new Vector2(300f, 60f);
                rt.localScale = new Vector3(0.01f, 0.01f, 0.01f);

                var tmp = labelGO.AddComponent<TextMeshProUGUI>();
                tmp.text      = MPNames.Resolve(ownerId) + "'s car";
                tmp.fontSize  = 42f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color     = new Color(1f, 0.85f, 0.4f);
                return labelGO.transform;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Vehicle] CreateOwnerLabel: {ex.Message}");
                return null;
            }
        }

        /// <summary>The transform of a spawned remote ghost vehicle, or — for the OWNER riding their own car
        /// while a borrower drives it — the owner's real followed car (so the passenger pin has a target).</summary>
        public static Transform? GhostTransform(string vehicleId)
        {
            if (string.IsNullOrEmpty(vehicleId)) return null;
            if (_remoteVehicles.TryGetValue(vehicleId, out var rv) && rv.Go != null) return rv.Go.transform;
            if (_ownedFollowing.ContainsKey(vehicleId)) { var go = FindOwnedGo(vehicleId); if (go != null) return go.transform; }
            return null;
        }

        /// <summary>Every spawned ghost vehicle as (vehicleId, transform).</summary>
        public static System.Collections.Generic.IEnumerable<(string, Transform)> AllGhosts()
        {
            foreach (var kv in _remoteVehicles)
                if (kv.Value != null && kv.Value.Go != null)
                    yield return (kv.Key, kv.Value.Go.transform);
        }

        /// <summary>The transform of one of the LOCAL player's OWN (real, non-ghost) vehicles by
        /// MP id, or null. Lets us pin a remote passenger to the car WE own — on our screen that
        /// car is the real native vehicle, not a ghost, so AllGhosts/GhostTransform won't find it.</summary>
        public static Transform? LocalVehicleTransform(string vehicleId)
        {
            if (string.IsNullOrEmpty(vehicleId)) return null;
            try
            {
                var list = VehicleHelper.AllPlayerVehicles;
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                {
                    var vc = list[i];
                    if (vc != null && vc.vehicleInstance != null && vc.vehicleInstance.id == vehicleId)
                        return vc.transform;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Count of player-vehicle ghosts on this client (census/diagnostics).</summary>
        public static int RemoteVehicleCount => _remoteVehicles.Count;

        /// <summary>Every collider on every vehicle on THIS machine — ghosts + the local player's
        /// real cars. Used to keep remote-avatar colliders from shoving them (IgnoreCollision).</summary>
        public static System.Collections.Generic.List<Collider> AllVehicleColliders()
        {
            var result = new System.Collections.Generic.List<Collider>();
            try
            {
                foreach (var rv in _remoteVehicles.Values)
                    if (rv?.Go != null) result.AddRange(rv.Go.GetComponentsInChildren<Collider>(true));
                var list = VehicleHelper.AllPlayerVehicles;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null) result.AddRange(list[i].GetComponentsInChildren<Collider>(true));
            }
            catch { }
            return result;
        }

        // vehicleId → the ghost's captured SleepEnvironment (read before the controller is stripped),
        // so a PASSENGER riding this ghost can sleep without the real VehicleController.
        private static readonly System.Collections.Generic.Dictionary<string, PlayerActivity.SleepEnvironment> _sleepEnvByVehicleId = new();

        /// <summary>The captured sleep environment for a ghost vehicle, or null.</summary>
        public static PlayerActivity.SleepEnvironment? SleepEnvironmentFor(string vehicleId)
            => (vehicleId != null && _sleepEnvByVehicleId.TryGetValue(vehicleId, out var s)) ? s : null;

        /// <summary>Colliders of GHOST vehicles only (not the local player's own cars) — used to
        /// stop the LOCAL player from shoving other players' cars without affecting their own.</summary>
        public static System.Collections.Generic.List<Collider> AllGhostColliders()
        {
            var result = new System.Collections.Generic.List<Collider>();
            try
            {
                foreach (var rv in _remoteVehicles.Values)
                    if (rv?.Go != null) result.AddRange(rv.Go.GetComponentsInChildren<Collider>(true));
            }
            catch { }
            return result;
        }

        // ===== FIX(flatbed-push 2026-06-22): neutralize the carving NavMeshObstacle on hand-cart GHOSTS =====
        // A flatbed/handtruck GHOST is a kinematic copy driven by the synced transform, and it keeps a
        // CARVING NavMeshObstacle (carve=true, en=true). On the non-owner's machine that obstacle displaces
        // the host's nav-driven traffic as the cart moves — so a player pushing a cart appears to shove cars.
        // The OWNER's own machine disables the cart's NavMeshObstacle while carrying it (v3 probe: en=False);
        // we replicate that on the ghost. The SOLID COLLIDER IS LEFT ENABLED — click-to-loot/board raycasts
        // it (PassengerRide pick + GhostIdFor, solid-only), so disabling it would break looting. Scooter is
        // excluded (ridden, not a carried cart).
        internal static bool IsHandCartType(string typeName) =>
            typeName != null && (typeName.IndexOf("flatbed", System.StringComparison.OrdinalIgnoreCase) >= 0
                              || typeName.IndexOf("handtruck", System.StringComparison.OrdinalIgnoreCase) >= 0);

        private static readonly Collider[] _cartIgnoreBuf = new Collider[256];
        internal static void NeutralizeHandCartGhostObstacle(UnityEngine.GameObject go)
        {
            if (go == null) return;
            try
            {
                // Match the owner's carried state: drop the carving NavMeshObstacle. It isn't the pusher, but
                // the owner disables it while carrying — left on, it would make traffic reroute around an
                // invisible carried cart.
                foreach (var o in go.GetComponentsInChildren<UnityEngine.AI.NavMeshObstacle>(true))
                    if (o != null && (o.enabled || o.carving)) { o.carving = false; o.enabled = false; }

                // THE PUSH (confirmed by test 2026-06-22): the ghost's SOLID collider displaces nearby
                // vehicles as the cart is carried. Keep the collider ENABLED so click-to-loot still raycasts
                // it (IgnoreCollision doesn't affect raycasts), and instead ignore collisions with every nearby
                // vehicle — anything carrying a Rigidbody (cars/traffic; walls/ground have none). Machine-
                // agnostic: host vs real Gley traffic and client vs ghost cars alike. Re-applied each tick so a
                // vehicle entering range is ignored before it reaches the cart.
                foreach (var cc in go.GetComponentsInChildren<Collider>(true))
                {
                    if (cc == null || cc.isTrigger || !cc.enabled) continue;
                    int n = UnityEngine.Physics.OverlapSphereNonAlloc(cc.bounds.center, 6f, _cartIgnoreBuf, ~0, UnityEngine.QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        var other = _cartIgnoreBuf[i];
                        if (other == null || other.attachedRigidbody == null) continue;   // vehicles only
                        if (other.transform.IsChildOf(go.transform)) continue;            // not the cart itself
                        UnityEngine.Physics.IgnoreCollision(cc, other, true);
                    }
                }
            }
            catch { }
        }

        /// <summary>Disable the carving NavMeshObstacle on every flatbed/handtruck ghost (host AND client —
        /// the ghost lives on whichever machine isn't the owner). Called every frame from the collision-ignore
        /// refresh, because the ghost keeps its HandTruck controller which can switch the obstacle back on.</summary>
        internal static void NeutralizeHandCartGhostObstacles()
        {
            try
            {
                foreach (var kv in _remoteVehicles)
                {
                    if (kv.Value?.Go == null) continue;
                    string tn = null; try { tn = TypeNameFor(kv.Key); } catch { }
                    if (IsHandCartType(tn)) NeutralizeHandCartGhostObstacle(kv.Value.Go);
                }
            }
            catch { }
        }

        /// <summary>The ghost vehicleId a (collider) transform belongs to, walking up parents,
        /// or "" if it isn't part of a ghost. Used by click-to-board cursor picking.</summary>
        public static string GhostIdFor(Transform t)
        {
            try
            {
                for (var cur = t; cur != null; cur = cur.parent)
                    foreach (var kv in _remoteVehicles)
                        if (kv.Value?.Go != null && kv.Value.Go.transform == cur) return kv.Key;
            }
            catch { }
            return "";
        }

        /// <summary>Despawns one ghost by vehicle id.</summary>
        public static void DespawnByVehicleId(string vehicleId)
        {
            if (_remoteVehicles.TryGetValue(vehicleId, out var rv))
            {
                if (rv.Go != null) { try { UnityEngine.Object.Destroy(rv.Go); } catch { } }
                _remoteVehicles.Remove(vehicleId);
                Plugin.Logger.LogInfo($"[Vehicle] Despawned ghost '{vehicleId}'");
            }
        }

        /// <summary>Despawns every ghost vehicle owned by a player (they disconnected).</summary>
        public static void DespawnAllOwnedBy(string ownerId)
        {
            var ids = _remoteVehicles.Where(kv => kv.Value.OwnerId == ownerId)
                                     .Select(kv => kv.Key).ToList();
            foreach (var id in ids) DespawnByVehicleId(id);
        }

        /// <summary>Despawns every ghost vehicle (disconnect / scene unload).</summary>
        public static void DespawnAll()
        {
            foreach (var rv in _remoteVehicles.Values)
                if (rv.Go != null) { try { UnityEngine.Object.Destroy(rv.Go); } catch { } }
            _remoteVehicles.Clear();
        }

        private static bool IsGhostBeingDriven(GameObject go)
        {
            try { var vc = go.GetComponentInChildren<VehicleController>(); return vc != null && vc.controlledByPlayer; }
            catch { return false; }
        }

        /// <summary>Write the owner's synced fuel onto a drivable proxy's FuelModule (so it's not stuck at 0%).
        /// No-op for a stripped ghost (no controller) or while the local player drives it (don't clobber).</summary>
        private static void ApplyFuelToGhost(GameObject go, float fuel)
        {
            try
            {
                var vc = go.GetComponentInChildren<VehicleController>();
                if (vc == null || vc.controlledByPlayer) return;
                var cc = vc as CarController;
                if (cc != null && cc.fuelModule != null) cc.fuelModule.amount = fuel;
                if (vc.vehicleInstance != null) vc.vehicleInstance.fuel = fuel;
            }
            catch { }
        }

        // ── Driving handoff (Phase 2 B) ───────────────────────────────────────
        // While a granted borrower drives owner O's car, the BORROWER broadcasts its pose; the OWNER's real
        // car becomes a kinematic follower of that (the owner's own fleet broadcast then carries the position
        // to everyone else). Reverts on exit (Released) or a ~1.5 s timeout (driver disconnect).
        private static string _drivingRealVid = "";   // REAL id of the proxy I'm currently driving ("" = none)
        private static string _drivingOwner   = "";
        private sealed class DrivenFollow { public Vector3 Pos; public Quaternion Rot = Quaternion.identity; public float Until; public float LastDamage = -1f; }
        private static readonly Dictionary<string, DrivenFollow> _ownedFollowing = new();   // MY cars driven remotely
        private const float DriveSyncTimeout = 1.5f;

        /// <summary>True if vid (one of MY cars) is currently driven remotely — used to redirect the owner's
        /// enter to a passenger seat (Phase 2 C).</summary>
        public static bool IsDrivenRemotely(string vid) => !string.IsNullOrEmpty(vid) && _ownedFollowing.ContainsKey(vid);

        /// <summary>Per-tick (TickPositionSync): broadcast the pose of a proxy I'm driving; on exit send a
        /// final Released; time out stale follows.</summary>
        public static void TickDriveSync()
        {
            try
            {
                string realVid = "", owner = ""; Transform pose = null; float fuel = 0f, dmg = 0f;
                var list = VehicleHelper.AllPlayerVehicles;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var vc = list[i]; if (vc == null) continue;
                        var inst = vc.vehicleInstance; if (inst == null || string.IsNullOrEmpty(inst.id)) continue;
                        if (vc.controlledByPlayer && inst.id.StartsWith("BAMP_"))
                        {
                            realVid = inst.id.Substring(5); owner = OwnerIdFor(realVid); pose = vc.transform;
                            try { var cc = vc as CarController; if (cc != null) { if (cc.fuelModule != null) fuel = cc.fuelModule.amount; if (cc.damageHandler != null) dmg = cc.damageHandler.Damage; } } catch { }
                            break;
                        }
                    }

                if (!string.IsNullOrEmpty(realVid) && pose != null && !string.IsNullOrEmpty(owner))
                {
                    _drivingRealVid = realVid; _drivingOwner = owner;
                    SendDrive(new VehicleDrivePayload {
                        VehicleId = realVid, OwnerId = owner, DriverId = MPConfig.PlayerId,
                        X = pose.position.x, Y = pose.position.y, Z = pose.position.z,
                        Qx = pose.rotation.x, Qy = pose.rotation.y, Qz = pose.rotation.z, Qw = pose.rotation.w,
                        Fuel = fuel, Damage = dmg, Released = false, T = Time.unscaledTime });
                }
                else if (!string.IsNullOrEmpty(_drivingRealVid))
                {
                    SendDrive(new VehicleDrivePayload { VehicleId = _drivingRealVid, OwnerId = _drivingOwner, DriverId = MPConfig.PlayerId, Released = true, T = Time.unscaledTime });
                    Plugin.Logger.LogInfo($"[Drive] exited borrowed '{_drivingRealVid}' — sent release.");
                    _drivingRealVid = ""; _drivingOwner = "";
                }

                if (_ownedFollowing.Count > 0)
                {
                    float now = Time.unscaledTime; List<string> expired = null;
                    foreach (var kv in _ownedFollowing) if (now > kv.Value.Until) (expired ??= new List<string>()).Add(kv.Key);
                    if (expired != null) foreach (var v in expired) ReleaseOwnedFollow(v);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Drive] TickDriveSync: {ex.Message}"); }
        }

        private static void SendDrive(VehicleDrivePayload p)
        {
            if (MPServer.IsRunning) MPServer.BroadcastVehicleDrive(p);
            else if (MPClient.IsConnected) MPClient.SendVehicleDrive(p);
        }

        /// <summary>Recipient: only the OWNER of the car acts — their real car follows the driver's pose.
        /// Everyone else sees it move via the owner's normal fleet broadcast. The driver itself ignores it.</summary>
        public static void ApplyDriveSync(VehicleDrivePayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.VehicleId) || p.DriverId == MPConfig.PlayerId) return;
                var ownedGo = FindOwnedGo(p.VehicleId);
                if (ownedGo == null) return;   // not my car — I'll see it move via the owner's normal broadcast
                if (p.Released) { ReleaseOwnedFollow(p.VehicleId); return; }
                if (!_ownedFollowing.TryGetValue(p.VehicleId, out var f))
                { f = new DrivenFollow(); _ownedFollowing[p.VehicleId] = f; BeginOwnedFollow(ownedGo, p.VehicleId); }
                f.Pos = new Vector3(p.X, p.Y, p.Z); f.Rot = new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw);
                f.Until = Time.unscaledTime + DriveSyncTimeout;
                // State back-prop (item D): the borrower's fuel + damage → my real car so my save reflects use.
                try
                {
                    var cc = ownedGo.GetComponentInChildren<VehicleController>() as CarController;
                    if (cc != null)
                    {
                        if (cc.fuelModule != null) cc.fuelModule.amount = p.Fuel;
                        if (cc.vehicleInstance != null) cc.vehicleInstance.fuel = p.Fuel;
                        if (System.Math.Abs(p.Damage - f.LastDamage) > 0.001f) { cc.SetDamage(p.Damage); f.LastDamage = p.Damage; }
                    }
                }
                catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Drive] ApplyDriveSync: {ex.Message}"); }
        }

        private static GameObject? FindOwnedGo(string vid)
        {
            var list = VehicleHelper.AllPlayerVehicles; if (list == null) return null;
            for (int i = 0; i < list.Count; i++) { var vc = list[i]; if (vc?.vehicleInstance != null && vc.vehicleInstance.id == vid) return vc.gameObject; }
            return null;
        }

        private static void BeginOwnedFollow(GameObject go, string vid)
        {
            try { foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) rb.isKinematic = true; } catch { }
            Plugin.Logger.LogInfo($"[Drive] my car '{vid}' is being driven remotely → following the driver.");
        }

        private static void ReleaseOwnedFollow(string vid)
        {
            if (!_ownedFollowing.Remove(vid)) return;
            var go = FindOwnedGo(vid);
            if (go != null) { try { foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) rb.isKinematic = false; } catch { } }
            Plugin.Logger.LogInfo($"[Drive] my car '{vid}' released → resuming local control.");
        }

        /// <summary>Lerp my cars that are being driven remotely toward the driver's synced pose (+ apply fuel).
        /// Called from TickSmoothing.</summary>
        private static void TickOwnedFollow(float k)
        {
            if (_ownedFollowing.Count == 0) return;
            foreach (var kv in _ownedFollowing)
            {
                var go = FindOwnedGo(kv.Key); if (go == null) continue;
                var t = go.transform;
                t.position = Vector3.Lerp(t.position, kv.Value.Pos, k);
                t.rotation = Quaternion.Slerp(t.rotation, kv.Value.Rot, k);
            }
        }

        /// <summary>A granted player clicked a ghost: if it's a DRIVABLE car they hold a key to and they're
        /// on foot, drop them into the driver's seat (the game's normal EnterVehicle). Returns true if it
        /// took the wheel, false to fall through to ride / cargo.</summary>
        public static bool TryDriveGhost(string vid)
        {
            try
            {
                if (string.IsNullOrEmpty(vid) || !_remoteVehicles.TryGetValue(vid, out var rv) || rv?.Go == null) return false;
                if (!GrantSync.IsGranted(rv.OwnerId, MPConfig.PlayerId)) return false;   // not my key
                if (VehicleHelper.GetCurrentVehicle() != null) return false;             // already in a vehicle
                var vc = rv.Go.GetComponentInChildren<VehicleController>();
                if (vc == null) return false;                                            // not drivable (stripped — granted after spawn; OnGrantsChanged respawns it)
                // EnterVehicle's HUD hookup calls VehicleHelper.GetCurrentVehicle(), which resolves the active
                // car from SaveGameManager.VehicleInstances — the proxy is kept OUT of that list to dodge
                // tickets/tax, so the lookup NRE'd. But GetCurrentVehicle checks VehiclesCache FIRST; seed the
                // proxy there so every GetCurrentVehicle() caller resolves it. Charges still skip it (they
                // iterate VehicleInstances, not this cache).
                try { if (vc.vehicleInstance != null) VehicleHelper.VehiclesCache[vc.vehicleInstance.id] = vc.vehicleInstance; } catch (Exception ce) { Plugin.Logger.LogWarning($"[Drive] cache seed: {ce.Message}"); }
                Plugin.Logger.LogInfo($"[Drive] driving granted vehicle '{vid}' (owner '{rv.OwnerId}') — DriveVehicle() (walk to door + enter, like the owner).");
                vc.DriveVehicle();   // native walk-to-door-then-EnterVehicle (not a teleport)
                return true;
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[Drive] TryDriveGhost '{vid}': {ex}"); return false; }
        }

        /// <summary>The grant table changed: if the set of owners who grant ME a key changed, respawn ghosts
        /// so a newly-granted car becomes drivable (or a revoked one inert) without a rejoin. MAIN THREAD.</summary>
        public static void OnGrantsChanged()
        {
            try
            {
                string sig = GrantSync.GrantorSig(MPConfig.PlayerId);
                if (sig == _lastGrantorSig) return;
                _lastGrantorSig = sig;
                Plugin.Logger.LogInfo($"[Drive] granted-by set changed → respawning ghosts (drivable owners: '{sig}').");
                DespawnAll();   // re-sync from the next fleet broadcast, drivable-or-not per current grants
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Drive] OnGrantsChanged: {ex.Message}"); }
        }

        /// <summary>Smooths ghost vehicles toward their networked transform; billboards labels.</summary>
        public static void TickSmoothing()
        {
            if (_remoteVehicles.Count == 0 && _ownedFollowing.Count == 0) return;   // still run the owned-follow lerp even with no ghosts
            // Capped so packet corrections blend over frames at low FPS.
            float k   = Mathf.Min(Time.deltaTime * 12f, 0.5f);
            float now = Time.unscaledTime;
            var cam = Camera.main;
            foreach (var rv in _remoteVehicles.Values)
            {
                if (rv.Go == null) continue;
                // A granted ghost the local player is DRIVING owns its own position — don't yank it back.
                if (IsGhostBeingDriven(rv.Go)) continue;
                var t = rv.Go.transform;
                // Chase the dead-reckoned point (target + velocity·elapsed) so
                // motion stays continuous between packets even at low FPS.
                float ahead = Mathf.Min(now - rv.TargetAt, MaxVehicleExtrapolateSeconds);
                var predicted = rv.TargetPos + rv.Velocity * ahead;
                t.position = Vector3.Lerp(t.position, predicted, k);
                t.rotation = Quaternion.Slerp(t.rotation, rv.TargetRot, k);
                if (rv.Label != null && cam != null)
                    rv.Label.rotation = cam.transform.rotation;
            }
            TickOwnedFollow(k);   // handoff: my cars driven remotely follow the driver's synced pose
        }

        // ── AI traffic discovery probe ────────────────────────────────────────
        //
        // Phase 5 prep.  Discovers the world-traffic system: which classes drive
        // AI traffic, how many traffic vehicles are live, what components they
        // carry, and the AI driver/spawner API — the data needed to design
        // host-authoritative traffic sync.  Logging only; zero side effects.

        private static bool _trafficTypesScanned;
        private static bool _trafficSceneScanned;

        private static readonly string[] _trafficKeywords =
        {
            "traffic", "aicar", "aivehicle", "aidriver", "npccar", "npcvehicle", "carspawn"
        };

        private static void ScanTrafficTypes()
        {
            if (_trafficTypesScanned) return;
            if (PlayerHelper.PlayerController == null) return;
            _trafficTypesScanned = true;

            Plugin.Logger.LogInfo("[Traffic] === Traffic-type assembly scan ===");
            int found = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name == null) continue;
                    string lo = t.Name.ToLowerInvariant();
                    bool hit = false;
                    foreach (var kw in _trafficKeywords)
                        if (lo.Contains(kw)) { hit = true; break; }
                    if (hit) { Plugin.Logger.LogInfo($"[Traffic]   {t.FullName}"); found++; }
                }
            }
            Plugin.Logger.LogInfo($"[Traffic] === {found} traffic-named type(s) ===");
        }
    }
}
