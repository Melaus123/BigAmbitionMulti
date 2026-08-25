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
            public bool        OwnerUsing;          // fleet e.Driving — the OWNER is driving/pushing it RIGHT NOW (in-use arbitration)
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

        // (Round-243's GhostControllerFor was removed with its only caller — round-289, 2026-08-21.)

        /// <summary>The vehicle-type id of a ghost (empty if unknown).</summary>
        public static string TypeNameFor(string vid)
            => _remoteVehicles.TryGetValue(vid, out var rv) && rv != null ? rv.TypeName : "";

        /// <summary>Clear the hover OUTLINE on a ghost (round-12 #3). The game's only off-switch is
        /// EntityController.OnIoExit — the hover-exit resets the renderers back to the vehicles layer —
        /// but our redirected borrower flows (storage panel, deposit) end without a hover-exit, leaving
        /// the outline stuck on the ghost. Mirror OnIoExit's layer reset directly on the renderers.</summary>
        public static void ClearGhostHighlight(string vid)
        {
            try
            {
                var t = GhostTransform(vid);
                if (t == null) return;
                int outlined = LayerHelper.InteractiveItemsOutlinedLayerIndex;
                int normal   = LayerHelper.VehiclesLayerIndex;
                var rs = t.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                    if (rs[i] != null && rs[i].gameObject.layer == outlined) rs[i].gameObject.layer = normal;
            }
            catch { }
        }

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
            // Delivery-job mission logic on the delivery-van prefab (field 2026-07-20, NRE ×11): its
            // Start() caches GetComponent<VehicleController>() — null after the strip — and subscribes
            // CheckIfShouldDisappear to GlobalEvents.onNewHour, which then derefs the null EVERY GAME
            // HOUR a delivery ghost exists. Worse than noise: the throw aborts every onNewHour
            // subscriber combined after it for that hour. Same remedy as ScooterController — die
            // before Start ever subscribes. Real delivery vans (the local player's own job) keep it.
            "DeliveryJobVehicle",
        };

        // Keyed by VehicleId (a player owns several vehicles).
        private static readonly Dictionary<string, RemoteVehicle> _remoteVehicles = new();

        /// <summary>Round-109 — open "orphan-possessed" episodes: a ghost the local player is pushing or
        /// driving that its own owner has stopped listing in their fleet.  Keyed by vehicle id, so the
        /// per-tick warning becomes one line + a 30s rollup instead of ~10 lines a SECOND (a field report
        /// spent its whole ring buffer on 233 copies of that warning and overwrote the crash it was
        /// supposed to explain).  Cleared per session with the ghosts themselves.</summary>
        private static readonly Dictionary<string, (float firstAt, int hits, float lastRollupAt)> _orphanPossessed = new();

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
                        // Round-74c: a DRIVEN granted proxy must still anchor the hand-IK export —
                        // this skip ran BEFORE the openDriven capture, so CurrentOpenDriven stayed
                        // null for borrowed carts and the pusher's hands-on-handle pose never left
                        // this machine (user: "host didn't see the client's hands on the flatbed").
                        // The proxy still never enters the broadcast itself.
                        try { if (vc.controlledByPlayer && IsOpenVehicle(inst.vehicleTypeName ?? "")) openDriven = vc.transform; } catch { }
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
                                // 4-part since Option A (2026-07-07): paid + price ride along so the
                                // replica is checkout-faithful — the register reads unpaid stacks and
                                // prices OFF THE REPLICA when a borrower shops with a pushed cart.
                                csb.Append(c.itemName).Append('=').Append(c.amount)
                                   .Append('=').Append(c.paid ? '1' : '0')
                                   .Append('=').Append(c.pricePerUnit.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                                   .Append(';');
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

                    // Cross-interior tag v3 (round-74): the VEHICLE's own native street data is the
                    // truth — the game maintains it on every door transition (BuildingManager:779
                    // drive-in, HandTruck:184 / Scooter:199, "" outdoors) and it persists in the
                    // save. The old 30m owner-proximity heuristic mis-tagged carts whenever the
                    // owner stood in an ADJACENT interior box (interior volumes are spatially
                    // packed): a parked cart was hidden exactly where it really was and "shown"
                    // only where it wasn't (test #3 "teleported away", indoor AND outdoor).
                    // While a borrower drives it, their live building rides the drive stream
                    // instead — this machine's street data lags their door transitions.
                    string bldg;
                    if (_ownedFollowing.TryGetValue(inst.id, out var fTag)) bldg = fTag.Bldg ?? "";
                    // Round-88 (user-approved, field-test-confirmed): while *I* push my own open cart,
                    // the live truth is MY building context — a hand truck's native street data is
                    // written only at grab (HandTruck:184) and at release-INDOORS (VehicleController:
                    // 342-347), so a cart grabbed on the street stays tagged "outdoors" through the
                    // whole indoor push and renders in every same-layout interior on other machines
                    // ("carts from other people show", field 20260725-000353). Cars are excluded on
                    // purpose: native maintains their street data through door hooks + underground
                    // parking and must keep doing so.
                    else if (vc.controlledByPlayer && IsOpenVehicle(tn ?? ""))
                    {
                        bldg = MPRegisterSync.CurrentShopAddress ?? "";
                        // Round-200 Fix A (audit 2026-07-31): ALSO persist the live context into
                        // the cart's own record, not just the broadcast. Native writes street
                        // data only at grab + release-indoors — nothing during the push — so an
                        // abandonment mid-push (crash/quit/disconnect) used to SAVE a cart at
                        // interior coords still tagged '' (outdoors), which then rendered in
                        // every same-layout interior forever (mask fails open on ''). Converging
                        // the record on every fleet tick means abandonment at ANY moment
                        // persists the truth. Change-gated: writes only on door crossings.
                        try
                        {
                            string cur = string.IsNullOrEmpty(inst.streetName) ? "" : $"{inst.streetNumber} {inst.streetName}";
                            if (cur != bldg)
                            {
                                ApplyStreetData(inst, bldg);
                                Plugin.Logger.LogInfo($"[Vehicle] push tag converged: '{inst.id}' ({tn}) '{cur}' → '{bldg}' (round-200).");
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        try { bldg = string.IsNullOrEmpty(inst.streetName) ? "" : $"{inst.streetNumber} {inst.streetName}"; }
                        catch { bldg = ""; }
                    }

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

                // Round-74 data-level fleet: vehicles whose live object is UNLOADED (native interior
                // scoping — a cart parked in a building unloads with its interior; VehicleController.
                // OnDisable then deregisters it from AllPlayerVehicles) used to silently vanish from
                // this payload, indistinguishable from SOLD, so receivers despawned the ghost — the
                // whole stuck/vanishing-cart family. Emit them as DORMANT entries from save data:
                // parked at their recorded position, tagged by their own address. A vehicle ABSENT
                // from the payload now unambiguously means sold/removed.
                try
                {
                    var insts = SaveGameManager.Current?.VehicleInstances;
                    if (insts != null)
                    {
                        var live = new HashSet<string>();
                        foreach (var e in fleet.Vehicles) live.Add(e.VehicleId);
                        foreach (var inst in insts)
                        {
                            try
                            {
                                if (inst == null || string.IsNullOrEmpty(inst.id) || live.Contains(inst.id)) continue;
                                if (inst.id.StartsWith("BAMP_") && !inst.id.StartsWith("BAMP_TESTRIG")) continue;   // leaked ghosts: never re-broadcast
                                string dCargo = "";
                                if (inst.cargoInstances != null && inst.cargoInstances.Count > 0)
                                {
                                    var csb = new System.Text.StringBuilder();
                                    for (int ci = 0; ci < inst.cargoInstances.Count && ci < 24; ci++)
                                    {
                                        var c = inst.cargoInstances[ci];
                                        if (c == null) continue;
                                        csb.Append(c.itemName).Append('=').Append(c.amount)
                                           .Append('=').Append(c.paid ? '1' : '0')
                                           .Append('=').Append(c.pricePerUnit.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                                           .Append(';');
                                    }
                                    dCargo = csb.ToString();
                                }
                                int dCarried = 0; try { dCarried = inst.cargoIds?.Count ?? 0; } catch { }
                                string dBldg = string.IsNullOrEmpty(inst.streetName) ? "" : $"{inst.streetNumber} {inst.streetName}";
                                fleet.Vehicles.Add(new VehicleEntry
                                {
                                    VehicleId = inst.id,
                                    TypeName  = inst.vehicleTypeName ?? "",
                                    ColorName = inst.vehicleColorName ?? "",
                                    Driving   = false,
                                    Fuel      = inst.fuel,
                                    X = inst.position.x, Y = inst.position.y, Z = inst.position.z,
                                    Qx = inst.rotation.x, Qy = inst.rotation.y, Qz = inst.rotation.z, Qw = inst.rotation.w,
                                    Cargo = dCargo, CarriedItems = dCarried,
                                    Bldg = dBldg, Dormant = true,
                                });
                            }
                            catch { }   // one malformed instance must not gut the pass
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] dormant fleet pass: {ex.Message}"); }

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

        /// <summary>Review M3: live position of a remote-owned vehicle's ghost on THIS machine —
        /// the per-peer traffic cull anchors a passenger on the car they ride.</summary>
        public static bool TryGetGhostPosition(string vehicleId, out Vector3 pos)
        {
            pos = default;
            try
            {
                if (!string.IsNullOrEmpty(vehicleId) && _remoteVehicles.TryGetValue(vehicleId, out var rv) && rv?.Go != null)
                { pos = rv.Go.transform.position; return true; }
            }
            catch { }
            return false;
        }

        // ── Wave-2 (audit T4): the driven set streams at 10 Hz; the RESTING set travels only when
        // its signature changes, plus a 5 s full-truth heartbeat (absence = sold needs full packets).
        private static string _fleetFullSig = "";
        private static float  _fleetFullAt  = -999f;
        private const  float  FullFleetHeartbeatSeconds = 5f;

        public static void SplitAndSend(VehicleFleetPayload full)
        {
            if (full == null) return;
            float now = Time.unscaledTime;
            string sig = RestingFleetSig(full);
            if (sig != _fleetFullSig || now - _fleetFullAt >= FullFleetHeartbeatSeconds)
            {
                _fleetFullSig = sig; _fleetFullAt = now;
                DispatchFleet(full);
                return;
            }
            List<VehicleEntry>? driven = null;
            foreach (var e in full.Vehicles)
                if (e != null && e.Driving) (driven ??= new List<VehicleEntry>()).Add(e);
            if (driven == null) return;   // everything parked and unchanged — silence until the heartbeat
            DispatchFleet(new VehicleFleetPayload { OwnerId = full.OwnerId, T = full.T, Partial = true, Vehicles = driven });
        }

        private static void DispatchFleet(VehicleFleetPayload vp)
        {
            if (MPServer.IsRunning) MPServer.BroadcastVehicleSync(vp);
            else if (MPClient.IsConnected) MPClient.SendVehicleSync(vp);
        }

        /// <summary>Change signature over the NON-volatile fleet state. A driven vehicle contributes
        /// only its id (its pose is volatile by definition); a resting one contributes everything a
        /// receiver would need refreshed — so parking, refueling, cargo changes, sale/purchase and
        /// interior moves all trigger a full packet, while pure driving does not.</summary>
        private static string RestingFleetSig(VehicleFleetPayload p)
        {
            var sb = new System.Text.StringBuilder(256);
            foreach (var e in p.Vehicles)
            {
                if (e == null) continue;
                if (e.Driving) { sb.Append(e.VehicleId).Append("|D;"); continue; }
                sb.Append(e.VehicleId).Append('|').Append(e.TypeName).Append('|').Append(e.ColorName).Append('|').Append(e.Bldg)
                  .Append('|').Append(e.Dormant).Append('|').Append(e.Cargo).Append('|').Append(e.CarriedItems)
                  // Review MIN-5: round-to-nearest METRE, not a truncated half-metre — a parked car
                  // whose settle jitter straddled a truncation boundary flipped the whole-fleet sig
                  // every tick, silently reverting the split. ColorName added (repaint reaches ≤1 beat).
                  .Append('|').Append(Mathf.RoundToInt(e.X)).Append(',').Append(Mathf.RoundToInt(e.Y)).Append(',').Append(Mathf.RoundToInt(e.Z))
                  .Append('|').Append((int)(e.Fuel * 20f)).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Applies a remote player's full vehicle fleet (main thread).</summary>
        // CLAUDE-DIAGNOSTIC — kill-switch gate for owned-vehicle fleet sync.
        // When false, ApplyVehicleFleet returns early (no ghost vehicles
        // spawned for the host's owned cars).  Used by F4 master kill switch.
        public static bool ClientApplyFleetEnabled { get; set; } = true;

        // Sender-side per-vehicle state: last logged cargo manifest (handcart
        // evidence) and last interior tag (cross-interior mask v2).
        private static readonly Dictionary<string, string> _lastManifestLogged = new();
        // (_lastVehicleBldg removed round-74: cross-interior tags now come from the vehicle's own
        //  native street data — see the v3 tag block in ReadLocalFleet.)

        public static void ApplyVehicleFleet(VehicleFleetPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.OwnerId)) return;
            if (SaveGameManager.Current == null) return;
            if (!ClientApplyFleetEnabled) return;     // CLAUDE-DIAGNOSTIC F4 gate
            if (!MPWorldReady.CanMaterialize) return; // round-188: fleets re-send continuously — drop is covered
            try
            {
                var seen = new HashSet<string>();
                bool anyDriving = false;

                foreach (var e in p.Vehicles)
                {
                    if (string.IsNullOrEmpty(e.VehicleId)) continue;
                    seen.Add(e.VehicleId);
                    if (e.Driving) anyDriving = true;
                    // In-use arbitration (2026-07-07, run-9: dual possession — the partner grabbed the
                    // cart's ghost WHILE the owner was pushing the real one; the follow then dragged the
                    // owner's in-hands cart toward the borrower). Remember the owner's live use state;
                    // CanDriveGhost refuses the grab while it's set.

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
                        // DATA: GhostCargoFor reads AppliedCargo, so the trunk panel shows current contents to
                        // whoever opens it (driver or not) the instant cargo changes.
                        rv.AppliedCargo = cargoSig;

                        // VISUAL: refresh IN PLACE, exactly as single-player does — re-apply the cargo to the
                        // ghost's instance, then call the controller's own UpdateCargoCount (HandTruck →
                        // UpdateCardboardBoxes toggles its box placeholders by count; cars show no loose cargo
                        // → harmless no-op). NO despawn — despawning a ghost the borrower is driving/riding
                        // destroyed their parented character: the OOM crash (run-2026-06-29). Works while driven.
                        try
                        {
                            var vc = rv.Go.GetComponentInChildren<VehicleController>();
                            if (vc != null && vc.vehicleInstance != null)
                            {
                                RefreshGhostCargo(vc.vehicleInstance, e);
                                vc.UpdateCargoCount();
                            }
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] in-place cargo refresh '{e.VehicleId}': {ex.Message}"); }
                    }
                    if (rv.Go != null && !PossessedByLocal(rv.Go)
                        && Vector3.Distance(rv.Go.transform.position, pos) > VehicleSnapDistance)
                    {
                        // Teleport (ferry, recovery) — snap, don't slide. NEVER while WE possess the
                        // ghost (CartTrace-pinned root of the whole borrowed-cart cluster, 2026-07-07):
                        // this direct write teleported the cart out of the pusher's hands on every
                        // owner broadcast >15m away — native possession yanked it back, producing the
                        // packet-rate ping-pong, the corrupted stream→follow feedback with the owner,
                        // the cart pinned at the door after an interior warp (711m), and the eventual
                        // possession break. While possessed, THIS machine owns the ghost's transform —
                        // the owner's broadcast is derived from our own stream and must never re-apply.
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
                    rv.OwnerUsing = e.Driving;   // in-use arbitration (see the run-9 note above)

                    // Keep a drivable (granted) proxy fueled from the owner's car so it isn't stuck at 0%.
                    // Skipped while WE drive it (controlledByPlayer) so local consumption isn't clobbered.
                    if (rv.Go != null) ApplyFuelToGhost(rv.Go, e.Fuel);

                    // Cross-interior mask v2 (per-vehicle building tag — fixes the
                    // vehicle-LEFT-inside case): show the ghost only when its tag
                    // matches MY current building ("" = outdoors, always shown).
                    // v3 (2026-07-07, borrowed-flatbed field runs): a ghost WE are currently
                    // possessing (pushing/driving) is NEVER masked — the OWNER's tag for a
                    // remote-possessed vehicle is computed from ITS stale local position near the
                    // OWNER (the :401 proximity tagger), so it can claim a building the vehicle
                    // left long ago and hide the flatbed from its own pusher's hands.
                    if (rv.Go != null)
                    {
                        // Hand carts ARE tag-masked again (field 2026-07-19: a friend in a
                        // different same-model interior saw the owner's pushed flatbed).
                        // The 2026-07-07 blanket exemption existed because a wrong hide
                        // ejected a cart from its pusher's hands — but BOTH causes of a
                        // wrong hide have since been fixed structurally: iPossessIt below
                        // exempts anything in the LOCAL player's hands, and the [ShopCtx]
                        // context self-heal keeps CurrentShopAddress converged even when
                        // hand-vehicle entry skips the entry hook.  Residual worst case is
                        // a NON-possessed cart briefly hidden on a stale tag until its
                        // owner nears it — cosmetic and self-healing, vs. rendering
                        // through walls which is what the mask exists to stop.
                        bool iPossessIt = PossessedByLocal(rv.Go);   // flag (cars) OR parented-under-Player
                        // Round-200 Fix B (audit 2026-07-31): '' tags FAIL CLOSED for hand carts
                        // while the viewer is indoors. The tag is an unverifiable claim: if it's
                        // honest ('' = street) the cart is unviewable from inside anyway, and if
                        // it's stale (cart actually at interior coords — the abandonment hole Fix
                        // A closes going forward, plus legacy saves) showing it paints a phantom
                        // into every same-layout interior — 309 fail-open decisions in the
                        // 2026-07-30 field bundles. Matches the PLAYER-ghost mask's fail
                        // direction. GHOSTS ONLY — the owner-side mask (round-74b) deliberately
                        // keeps '' own vehicles visible: the owner's real cart is their ground
                        // truth AND the repair affordance (grabbing re-stamps the tag for
                        // everyone). Scoped to handtruck/flatbed — the types that can be pushed
                        // indoors. Stateless: re-evaluated every fleet packet, so any input
                        // change (step outside, tag repaired, possession) recovers in seconds.
                        string myBldg = MPRegisterSync.CurrentShopAddress ?? "";
                        bool maskVeh;
                        if (iPossessIt) maskVeh = false;
                        else if (!string.IsNullOrEmpty(e.Bldg)) maskVeh = e.Bldg != myBldg;                // tagged: unchanged rule
                        else maskVeh = myBldg.Length > 0 && IsHandCartType(e.TypeName ?? "");             // '' + viewer indoors → hide (carts only)
                        if (rv.Go.activeSelf == maskVeh)
                        {
                            rv.Go.SetActive(!maskVeh);
                            // Review 2026-08-24 (ROOT of field 20260821-180203, "cart stuck in hands"):
                            // SetActive(false) fires native VehicleController.OnDisable → Unregister-
                            // PlayerVehicle, and registration happens ONLY in Awake — so a hidden-then-
                            // shown DRIVABLE ghost stayed out of AllPlayerVehicles for the rest of the
                            // session, and GetCurrentVehicleBase() returned null while a player pushed
                            // it (the whole NRE family, plus every consumer in the class entry). Re-
                            // register on show: the native method dupe-guards, non-drivable ghosts have
                            // no controller to find, and BAMP_ ids are already excluded from fleet
                            // reads and the owned-interior mask, so nothing leaks anywhere else.
                            if (!maskVeh)
                            {
                                try
                                {
                                    var shownVc = rv.Go.GetComponentInChildren<VehicleController>(true);
                                    if (shownVc != null) VehicleHelper.RegisterPlayerVehicle(shownVc);
                                }
                                catch { }
                            }
                            Plugin.Logger.LogInfo(
                                $"[InteriorMask] ghost '{e.TypeName}' ({e.VehicleId}) {(maskVeh ? "hidden" : "shown")} — " +
                                $"tag='{e.Bldg}' mine='{MPRegisterSync.CurrentShopAddress}' possessed={iPossessIt}.");
                        }
                    }
                }

                // Vehicles this owner no longer lists have been sold — despawn them. NEVER a ghost
                // WE currently possess (CartTrace 2026-07-07: the owner's real cart briefly left its
                // fleet list — deregistered after the follow carried it into unloaded interior
                // coords — and this cleanup DESTROYED the cart out of the pusher's hands, stack:
                // ApplyVehicleFleet → DespawnByVehicleId → ExitIfLocallyDriven). A possessed ghost
                // rides out list flicker; a real sale despawns the moment it's released.
                // Wave-2 (audit T4): a PARTIAL packet lists only the driven set — absence means
                // nothing there, so sold-detection runs on full-truth packets only (≤5 s apart).
                var stale = p.Partial ? new List<string>() : _remoteVehicles
                    .Where(kv => kv.Value.OwnerId == p.OwnerId && !seen.Contains(kv.Key))
                    .Select(kv => kv.Key).ToList();
                // Round-109: an id that came BACK into the owner's fleet closes its orphan episode.
                if (_orphanPossessed.Count > 0)
                    foreach (var id in seen)
                        if (_orphanPossessed.TryGetValue(id, out var was))
                        {
                            _orphanPossessed.Remove(id);
                            Plugin.Logger.LogInfo($"[Vehicle] orphan-possessed '{id}' RE-LISTED by its owner after "
                                + $"{Time.unscaledTime - was.firstAt:F0}s ({was.hits} tick(s)) — list flicker, not a sale.");
                        }

                foreach (var id in stale)
                {
                    if (_remoteVehicles.TryGetValue(id, out var srv) && srv?.Go != null && PossessedByLocal(srv.Go))
                    {
                        // Round-109 THROTTLE (field 2026-07-26, host 'Dawnspear'): this fired EVERY fleet tick
                        // (~10/s, 233 copies) and filled the fixed-size ring buffer, leaving only the last 47
                        // seconds. The interaction-lock NREs it should have helped explain were ~2400 log lines
                        // earlier and had been overwritten, along with the pickup that started it. An unthrottled
                        // per-tick warning in a ring-buffer diagnostic erases the evidence it exists to preserve.
                        // Now: one full line per id, a rollup every 30s while it persists, and a closing line
                        // when the id is re-listed (above) or the ghost is finally released (DespawnByVehicleId).
                        if (!_orphanPossessed.TryGetValue(id, out var st))
                        {
                            string vt = "?"; try { vt = srv.TypeName ?? "?"; } catch { }
                            _orphanPossessed[id] = (Time.unscaledTime, 1, Time.unscaledTime);
                            Plugin.Logger.LogWarning($"[Vehicle] owner's fleet no longer lists '{id}' (type='{vt}', owner='{srv.OwnerId}') "
                                + "but WE are pushing/driving it — keeping (no despawn while possessed). Further ticks are "
                                + "rolled up every 30s so this cannot flood the ring buffer.");
                        }
                        else
                        {
                            st.hits++;
                            if (Time.unscaledTime - st.lastRollupAt >= 30f)
                            {
                                Plugin.Logger.LogWarning($"[Vehicle] orphan-possessed '{id}' STILL unlisted by its owner — "
                                    + $"{Time.unscaledTime - st.firstAt:F0}s, {st.hits} tick(s) so far.");
                                st.lastRollupAt = Time.unscaledTime;
                            }
                            _orphanPossessed[id] = st;
                        }
                        continue;
                    }
                    DespawnByVehicleId(id);
                }

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
            // Round-74e: ZERO for everything. Native parents the cart to the player at LOCAL ZERO
            // (HandTruck.EnterVehicle :156-158) — root and cart origin coincide; the pusher's whole
            // backward stance lives in the MODEL-local shift (:169-172), which now syncs as
            // PlayerPositionPayload.MlZ and is mirrored on the clone. The old -1m root offset here
            // was an unknowing compensation for that un-synced shift — keeping both stood the clone
            // 2m back (user-caught double offset).
            return Vector3.zero;
        }

        /// <summary>Fill a (ghost) VehicleInstance's cargo from the manifest
        /// string "itemId=amount=paid=price;…" (legacy 2-part accepted) so the
        /// visual boxes appear remotely AND the replica is checkout-faithful
        /// (Option A: a borrower's register reads unpaid stacks off the replica).
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
                    if (bits.Length != 2 && bits.Length != 4) continue;   // 2 = legacy wire, 4 = +paid+price (Option A)
                    if (string.IsNullOrEmpty(bits[0])) continue;
                    if (!int.TryParse(bits[1], out var amount) || amount <= 0) continue;
                    bool paid = true; float price = 0f;
                    if (bits.Length == 4)
                    {
                        paid = bits[2] != "0";
                        float.TryParse(bits[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out price);
                    }
                    inst.cargoInstances.Add(new BigAmbitions.Items.CargoInstance(bits[0], amount, price, paid));
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] cargo manifest: {ex.Message}"); }
        }

        /// <summary>(Re)build a ghost's cargo DATA from a fleet entry — the manifest stacks + one stand-in per
        /// carried item. Clears first, so it's safe to call IN-PLACE on a cargo change (no despawn); the caller
        /// then refreshes the visual via the controller's UpdateCargoCount (HandTruck.UpdateCardboardBoxes
        /// toggles the box placeholders by count — exactly how single-player updates a flatbed in place).</summary>
        private static void RefreshGhostCargo(VehicleInstance inst, VehicleEntry e)
        {
            try
            {
                if (inst?.cargoInstances == null) return;
                inst.cargoInstances.Clear();
                ApplyCargoManifest(inst, e.Cargo);
                for (int ci = 0; ci < e.CarriedItems && ci < 12; ci++)
                    inst.cargoInstances.Add(new BigAmbitions.Items.CargoInstance("ba:itemname_cheapgift", 1, 0f, true));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] RefreshGhostCargo: {ex.Message}"); }
        }

        // Round-56b: early-fleet-packet suppression (mirrors RemotePlayerManager's 2026-06-01 guard).
        private static float _earlyGhostNextLog;
        private static int   _earlyGhostCount;
        private static float _ghostFailNextLog;   // round-188: throttles the spawn-failure error
        private static int   _ghostFailCount;

        private static RemoteVehicle? SpawnRemoteVehicle(string ownerId, VehicleEntry e,
                                                         Vector3 pos, Quaternion rot)
        {
            // Round-56b (Westi 0.1.12 prev-log: 5,656 "CreateAndSpawnVehicle failed" NREs during a
            // stuck load): fleet packets arrive at 10 Hz while the local world is still loading —
            // the native spawn derefs scene systems that don't exist yet, and every packet retried
            // the NRE. Same guard the remote-player spawn has: world not ready → drop the packet
            // (the fleet re-sends continuously, so a drop costs nothing). Throttled log.
            try
            {
                if (InstanceBehavior<GameManager>.Instance?.playerController == null)
                {
                    _earlyGhostCount++;
                    float now = UnityEngine.Time.unscaledTime;
                    if (now >= _earlyGhostNextLog)
                    {
                        _earlyGhostNextLog = now + 2f;
                        Plugin.Logger.LogWarning($"[Vehicle] EARLY-DATA: ghost '{e.TypeName}' for '{ownerId}' before world ready — spawn suppressed. count={_earlyGhostCount}");
                    }
                    return null;
                }
            }
            catch { }
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
            RefreshGhostCargo(inst, e);   // manifest stacks + a stand-in per carried item (flatbed boxes derive from cargo)

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
                // Class 6 (serialized-state inheritance): a ghost cloned while its OWNER is pushing/
                // driving carries controlledByPlayer=TRUE — natively that means "already in someone's
                // hands", so the grab is refused forever (run-8: partner could only open the cart's
                // inventory). Merger testing surfaces it constantly: every grant change respawns
                // ghosts, so any respawn mid-push mints a poisoned clone. A fresh ghost is possessed
                // by NOBODY on this machine — clear the flag on every kept controller.
                try
                {
                    foreach (var kvc in go.GetComponentsInChildren<VehicleController>(true))
                        if (kvc != null && kvc.controlledByPlayer) { kvc.controlledByPlayer = false; Plugin.Logger.LogInfo($"[Drive] ghost '{e.TypeName}': cleared inherited controlledByPlayer (owner was using it at clone time)."); }
                    // User-approved 2026-07-07: a GHOST never runs the native abandoned-vehicle timer —
                    // only the OWNER's machine decides whether a vehicle still exists; the ghost follows
                    // the owner's broadcast. (The native timer on a ghost would locally destroy a parked
                    // borrowed cart that left the borrower's sight, then fleet-respawn it — flicker.)
                    foreach (var adv in go.GetComponentsInChildren<AutoDestroyVehicle>(true))
                        if (adv != null) UnityEngine.Object.Destroy(adv);
                }
                catch { }
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
            // Round-188 (field: 13,629 NREs in one log): the ONE spawn entry that lacked the
            // round-56b guard — traffic's 10 Hz snapshots routed every car here during the
            // startup fence and the native spawn NRE'd per car per packet.  Same semantics as
            // SpawnRemoteVehicle: world not ready → drop (streams re-deliver, a drop is free).
            try
            {
                if (InstanceBehavior<GameManager>.Instance?.playerController == null)
                {
                    _earlyGhostCount++;
                    float nowEg = UnityEngine.Time.unscaledTime;
                    if (nowEg >= _earlyGhostNextLog)
                    {
                        _earlyGhostNextLog = nowEg + 2f;
                        Plugin.Logger.LogInfo($"[Vehicle] visual-ghost spawn deferred — world not ready (playerController null); {_earlyGhostCount} drop(s). Streams re-deliver.");
                    }
                    return null;
                }
            }
            catch { return null; }
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
                // Round-188: throttled — this printed once per car per 0.1s packet in the field.
                _ghostFailCount++;
                float nowGf = UnityEngine.Time.unscaledTime;
                if (nowGf >= _ghostFailNextLog)
                {
                    _ghostFailNextLog = nowGf + 5f;
                    Plugin.Logger.LogError($"[Vehicle] ghost CreateAndSpawnVehicle failed ({_ghostFailCount} total this session): {ex.Message}");
                }
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

        /// <summary>Is this ghost possessed by the LOCAL player right now? Two signals, either
        /// suffices: controlledByPlayer (cars — the drive path sets it) OR the GO being PARENTED
        /// under the local Player (hand carts — run-1 CarryProbe showed pushing = 'Player/
        /// Flatbed(Clone)', and run-7 proved controlledByPlayer stays FALSE for pushed carts, which
        /// is why the mask ejected a cart from the pusher's hands: SetActive(false) on a possessed
        /// cart breaks the native possession, 2026-07-07 user report).</summary>
        // ── Field 20260821-180203 (the borrowed-cart class, two live NREs): SCOPED ghost
        // resolution for VehicleHelper.GetCurrentVehicleBase. Ghosts are deliberately outside
        // AllPlayerVehicles, so the native resolver returns null while a player possesses a
        // borrowed cart — and two native consumers deref it: ItemController.SecondaryInteract's
        // `.vehicleType.spawnInPlayerObject` gate (crashed on EVERY right-click) and
        // PlacementHelper.CancelPlacementMode → ItemPanelUI.SetVehicle → VehicleInfoPanel.
        // Initialize (crashed mid-cancel — "cart stuck in hands"). Round-289 (user decision)
        // removed a GLOBAL resolver answer as a landmine; this is the class-guidance narrow
        // form: the substitution happens ONLY inside the named consumer windows (the depth
        // brackets in HousingPatches), same call-scoped pattern as RaiseTenancy/LowerTenancy.
        // MAIN THREAD only.
        internal static int GhostResolveDepth;

        /// <summary>The ghost the LOCAL player is currently pushing/driving, if any — the
        /// drivable (granted) ghosts keep their VehicleController (possession detection and
        /// native enter/exit already use it).</summary>
        internal static VehicleController? LocalPossessedGhostController()
        {
            try
            {
                foreach (var rv in _remoteVehicles.Values)
                {
                    if (rv?.Go == null || !PossessedByLocal(rv.Go)) continue;
                    return rv.Go.GetComponentInChildren<VehicleController>(true);
                }
            }
            catch { }
            return null;
        }

        internal static bool PossessedByLocal(UnityEngine.GameObject go)
        {
            if (go == null) return false;
            try
            {
                var vc = go.GetComponentInChildren<VehicleController>(true);
                if (vc != null && vc.controlledByPlayer) return true;
                var pc = Helpers.PlayerHelper.PlayerController;
                return pc != null && go.transform.IsChildOf(pc.transform);
            }
            catch { return false; }
        }

        // ([CartProbe] sampled probe and its event-driven successor [CartTrace] both RETIRED —
        //  the borrowed-cart cluster closed 2026-07-07; see the context log for the full evidence trail.)

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

        /// <summary>Round-268: the exit-teardown remainder the native CarController.ExitVehicle
        /// lines past the throw point would have run — blocker release (:511), avatar visibility
        /// (:508), reparent (:510). Every call is a no-op when the state is already correct, so
        /// it is safe on any path. PASSENGER GUARD: a rider's Vehicle blocker + hidden avatar
        /// belong to the ride (PassengerRide sets both deliberately) — never touch them mid-ride.</summary>
        private static void CompleteExitTeardown()
        {
            if (PassengerRide.IsSeated) return;
            try { InstanceBehavior<GameManager>.Instance?.playerController?.UnsetNavigationBlocker(NavigationBlocker.Vehicle); } catch { }
            try { InstanceBehavior<GameManager>.Instance?.playerController?.Character?.ToggleVisibility(show: true); } catch { }
            try
            {
                var gm = InstanceBehavior<GameManager>.Instance;
                if (gm != null && gm.playerController != null && gm.playerController.transform.parent != gm.transform)
                    gm.playerController.transform.SetParent(gm.transform);
            }
            catch { }
        }

        /// <summary>Round-34, THE STALE-VEHICLE-STATE CLASS (user: "do we have a larger bug?" — yes, this):
        /// destroying a ghost/proxy the LOCAL player is currently driving or pushing skips the game's exit
        /// bookkeeping (VehicleController.ExitVehicle :327 clears ActiveVehicleId + controlledByPlayer +
        /// camera). The save then says "in a vehicle" that no longer exists — IsUsingVehicle true,
        /// GetCurrentVehicle() null — and every native path that trusts it breaks downstream (ItemPanel NRE
        /// on take-to-hands, "can't enter building while in a vehicle", on-foot false-blocks). Every despawn
        /// runs this first: native exit when possible, manual field-clear as fallback.</summary>
        private static void ExitIfLocallyDriven(GameObject? go, string vid)
        {
            try
            {
                var vc = go != null ? go.GetComponentInChildren<VehicleController>() : null;
                bool driven = vc != null && vc.controlledByPlayer;
                string active = "";
                try { active = SaveGameManager.Current?.ActiveVehicleId ?? ""; } catch { }
                bool activeHere = vc != null && !string.IsNullOrEmpty(active) && vc.vehicleInstance?.id == active;
                // Round-68: a ghost can die WITHOUT a live VehicleController (scene teardown, destroyed
                // GameObject) — the checks above both come up false then, and the stale ActiveVehicleId
                // survives into the save (the trapped-in-building / frozen-ghost poison).  Match the id
                // directly so the clear runs even when the controller is already gone.
                if (!activeHere && !string.IsNullOrEmpty(active)
                    && (active == "BAMP_" + vid || active == vid))
                {
                    Plugin.Logger.LogWarning($"[Vehicle] despawning '{vid}' whose ghost id matches ActiveVehicleId '{active}' with no live controller — clearing the stale active state (round-68).");
                    try { SaveGameManager.Current.ActiveVehicleId = null; } catch { }
                    // Round-268: a controller-less death can strand the blocker + hidden avatar
                    // the same way the throw path below can — same completion, same no-op safety.
                    CompleteExitTeardown();
                    return;
                }
                if (!driven && !activeHere) return;
                Plugin.Logger.LogWarning($"[Vehicle] despawning '{vid}' while the local player is USING it (driven={driven}, activeId={activeHere}) — running native exit first.");
                try { vc!.ExitVehicle(); }
                catch
                {
                    // Round-268 (approved 2026-08-18, found via the round-260 partial-rollback
                    // class audit): this fallback cleared 2 of the 6 states EnterVehicle set. A
                    // throw before CarController.ExitVehicle:511 left NavigationBlocker.Vehicle
                    // HELD (WASD frozen, building exits dead — the round-90 stuck shape) and
                    // could leave the avatar invisible + parented to the dying car. Complete
                    // the remainder the unreached native lines would have run.
                    try { SaveGameManager.Current.ActiveVehicleId = null; } catch { }
                    try { if (vc != null) vc.controlledByPlayer = false; } catch { }
                    CompleteExitTeardown();
                    Plugin.Logger.LogWarning("[Vehicle] native exit THREW on the dying vehicle — fallback completed the full teardown (blocker released, avatar shown, reparented; round-268).");
                }
                try
                {
                    var gm = InstanceBehavior<GameManager>.Instance;
                    if (gm != null && gm.selectedVehicle == vc) gm.selectedVehicle = null;
                }
                catch { }
            }
            catch { }
        }

        /// <summary>Despawns one ghost by vehicle id.</summary>
        public static void DespawnByVehicleId(string vehicleId)
        {
            // Round-109: close any open orphan-possessed episode for this id (released, then despawned).
            if (_orphanPossessed.TryGetValue(vehicleId, out var op))
            {
                _orphanPossessed.Remove(vehicleId);
                Plugin.Logger.LogInfo($"[Vehicle] orphan-possessed '{vehicleId}' episode ENDED at despawn after "
                    + $"{Time.unscaledTime - op.firstAt:F0}s ({op.hits} tick(s)) — no longer possessed, so the stale ghost was removed.");
            }
            if (_remoteVehicles.TryGetValue(vehicleId, out var rv))
            {
                ExitIfLocallyDriven(rv.Go, vehicleId);
                if (rv.Go != null) { try { UnityEngine.Object.Destroy(rv.Go); } catch { } }
                _remoteVehicles.Remove(vehicleId);
                Plugin.Logger.LogInfo($"[Vehicle] Despawned ghost '{vehicleId}'");
            }
        }

        /// <summary>Round-231: a despawn sweep must never DESTROY a ghost the local player is
        /// driving (field 20260802-125303: the owner toggled a permission mid-borrow and the
        /// respawn deleted the car — with its cargo — under the driver). Sweeps that merely
        /// REFRESH state (grant changes, owner disconnect) skip driven ghosts and set this
        /// flag; the drive-end funnel in the tick (the release sender — it always runs, on
        /// exit or timeout) applies the full despawn then, and the next fleet broadcast
        /// respawns whatever should still exist. Scene teardowns still despawn everything
        /// (ExitIfLocallyDriven keeps that crash-safe).</summary>
        private static bool _pendingGhostReconcile;

        /// <summary>Despawns every ghost vehicle owned by a player (they disconnected).
        /// A ghost the local player is DRIVING survives until their drive ends.</summary>
        public static void DespawnAllOwnedBy(string ownerId)
        {
            var ids = _remoteVehicles.Where(kv => kv.Value.OwnerId == ownerId)
                                     .Select(kv => kv.Key).ToList();
            foreach (var id in ids)
            {
                if (_remoteVehicles.TryGetValue(id, out var rv) && rv?.Go != null && IsGhostBeingDriven(rv.Go))
                {
                    _pendingGhostReconcile = true;
                    Plugin.Logger.LogInfo($"[Drive] ghost '{id}' kept alive through its owner's disconnect — local player is driving it; reconcile at drive end.");
                    continue;
                }
                DespawnByVehicleId(id);
            }
        }

        /// <summary>Despawns every ghost vehicle (disconnect / scene unload).</summary>
        public static void DespawnAll() => DespawnAll(skipDriven: false);

        public static void DespawnAll(bool skipDriven)
        {
            List<string> kept = null;
            foreach (var kv in _remoteVehicles)
                if (kv.Value.Go != null)
                {
                    if (skipDriven && IsGhostBeingDriven(kv.Value.Go))
                    {
                        (kept ??= new List<string>()).Add(kv.Key);
                        continue;
                    }
                    ExitIfLocallyDriven(kv.Value.Go, kv.Key);
                    try { UnityEngine.Object.Destroy(kv.Value.Go); } catch { }
                }
            if (kept == null)
            {
                _remoteVehicles.Clear();
                _orphanPossessed.Clear();   // round-109: episodes die with the ghosts (per-session, not per-process)
                _pendingGhostReconcile = false;
                GhostResolveDepth = 0;      // review M5: a leaked bracket must not outlive the session (round-106 statics lesson)
                return;
            }
            var survivors = kept.Where(_remoteVehicles.ContainsKey)
                                .ToDictionary(k => k, k => _remoteVehicles[k]);
            _remoteVehicles.Clear();
            foreach (var kv in survivors) _remoteVehicles[kv.Key] = kv.Value;
            _pendingGhostReconcile = true;
            Plugin.Logger.LogInfo($"[Drive] {survivors.Count} driven ghost(s) kept alive through the sweep — deferred reconcile at drive end.");
        }

        private static bool IsGhostBeingDriven(GameObject go)
        {
            // PossessedByLocal covers both signals: controlledByPlayer (cars) and parented-under-
            // Player (pushed hand carts, whose flag stays FALSE — run-7). Without the parent signal
            // the smoothing lerp fights the pusher and rubber-bands a freed cart to the owner's
            // stale pose ("parks back at the pickup spot", user 2026-07-07).
            return PossessedByLocal(go);
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
        private sealed class DrivenFollow { public Vector3 Pos; public Quaternion Rot = Quaternion.identity; public float Until; public float LastDamage = -1f; public string Driver = ""; public bool Hide; public Vector3 RideOff; public string Bldg = ""; }
        private static readonly Dictionary<string, DrivenFollow> _ownedFollowing = new();   // MY cars driven remotely
        // Round-74: cars a borrower RELEASED inside an interior WE haven't loaded stay kinematic
        // (parked-dormant) until we load that interior — resuming physics over an unloaded floor
        // dropped them into the void and the native OnDisable deregistration ate them.
        private static readonly Dictionary<string, string> _dormantReleases = new();   // vid → interior addressKey
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

                // Pushed hand carts: possession = PARENTED under the Player, controlledByPlayer stays
                // FALSE (run-7 proof) — the flag scan above never sees them, so the owner's real cart
                // never followed and its stale position poisoned the interior tag. Stream the pose of
                // a proxy cart in the local player's hands through the SAME driven channel.
                if (string.IsNullOrEmpty(realVid))
                {
                    var pc = Helpers.PlayerHelper.PlayerController;
                    if (pc != null)
                        foreach (var vc in pc.GetComponentsInChildren<VehicleController>(true))
                        {
                            var inst = vc?.vehicleInstance;
                            if (inst == null || string.IsNullOrEmpty(inst.id) || !inst.id.StartsWith("BAMP_")) continue;
                            realVid = inst.id.Substring(5); owner = OwnerIdFor(realVid); pose = vc.transform;
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
                        Fuel = fuel, Damage = dmg, Released = false, T = Time.unscaledTime,
                        Bldg = MPRegisterSync.CurrentShopAddress ?? "" });   // truthful-follow tag (door-hold removed round-74): visibility masks + dormant releases key on it
                }
                else if (!string.IsNullOrEmpty(_drivingRealVid))
                {
                    // Round-232: carry the borrower's native parking verdict for where the car now
                    // rests — the owner's copy otherwise keeps the PICKUP spot's legality forever
                    // (tickets in a legal spot / none in an illegal one). The proxy still exists at
                    // this tick (reconcile runs after the send), so read its instance directly.
                    int parkState = -1;
                    try
                    {
                        var pt = GhostTransform(_drivingRealVid)?.GetComponentInChildren<VehicleController>()?.vehicleInstance;
                        if (pt != null) parkState = (int)pt.parkingState;
                    }
                    catch { }
                    SendDrive(new VehicleDrivePayload { VehicleId = _drivingRealVid, OwnerId = _drivingOwner, DriverId = MPConfig.PlayerId, Released = true, ParkState = parkState, T = Time.unscaledTime });
                    Plugin.Logger.LogInfo($"[Drive] exited borrowed '{_drivingRealVid}' — sent release (parkState={parkState}).");
                    _drivingRealVid = ""; _drivingOwner = "";
                    // Round-231: a grant change / owner disconnect happened mid-drive and the sweep
                    // spared this ghost. The drive is over — apply the full despawn now; the next
                    // fleet broadcast respawns whatever should still exist, with current drivability.
                    if (_pendingGhostReconcile)
                    {
                        Plugin.Logger.LogInfo("[Drive] drive ended — applying the deferred ghost reconcile now.");
                        DespawnAll();
                    }
                }

                if (_ownedFollowing.Count > 0)
                {
                    float now = Time.unscaledTime; List<string> expired = null;
                    foreach (var kv in _ownedFollowing) if (now > kv.Value.Until) (expired ??= new List<string>()).Add(kv.Key);
                    if (expired != null) foreach (var v in expired) ReleaseOwnedFollow(v);
                }

                // Round-74b: OWNER-side interior mask for MY OWN live vehicles. The redesign keeps
                // my real cart alive at interior coords while a borrower pushes/parks it in a
                // building I'm not in — but native vehicles never needed interior masking (indoors-
                // elsewhere meant unloaded), so mine rendered across ADJACENT interior boxes (user:
                // "host saw it in his building while it was pushed in another"). Mirror the ghost
                // mask rule — hidden when its building is non-empty and not mine — via RENDERERS +
                // COLLIDERS only: SetActive(false) would fire VehicleController.OnDisable and
                // deregister it from the fleet (the round-74 root class).
                try { TickOwnedVehicleInteriorMask(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] owned interior mask: {ex.Message}"); }

                // Round-74: wake dormant-parked cars once WE load the interior they were released in.
                if (_dormantReleases.Count > 0)
                {
                    List<string> wake = null;
                    foreach (var kv in _dormantReleases)
                        if (kv.Value == MPRegisterSync.CurrentShopAddress) (wake ??= new List<string>()).Add(kv.Key);
                    if (wake != null)
                        foreach (var vid in wake)
                        {
                            _dormantReleases.Remove(vid);
                            var dgo = FindOwnedGo(vid);
                            if (dgo != null) { try { foreach (var rb in dgo.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) rb.isKinematic = false; } catch { } }
                            Plugin.Logger.LogInfo($"[Drive] dormant-parked '{vid}' woken — its interior is loaded here now.");
                        }
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
                if (ownedGo == null)
                {
                    // Round-74: a granted borrower can grab a DORMANT cart of mine (parked in an
                    // interior I don't have loaded — no live object on my side). Track the drive
                    // stream at the DATA level so the instance's position/address/fuel follow the
                    // borrower and the release parks it correctly in my save; the dormant fleet
                    // entry then broadcasts the new truth. Not my vehicle at all → no-op inside.
                    DataFollow(p);
                    return;
                }
                if (p.Released)
                {
                    // Round-232: adopt the borrower's parking verdict BEFORE the release persists
                    // the pose — the resting car then resumes vanilla fine behavior for where it
                    // actually is, not where it was picked up.
                    if (p.ParkState >= 0)
                        try
                        {
                            var ri = ownedGo.GetComponentInChildren<VehicleController>()?.vehicleInstance;
                            if (ri != null && (int)ri.parkingState != p.ParkState)
                            {
                                Plugin.Logger.LogInfo($"[Drive] '{p.VehicleId}' parking state {ri.parkingState} → {(Helpers.ParkingState)p.ParkState} (borrower's exit verdict).");
                                ri.parkingState = (Helpers.ParkingState)p.ParkState;
                            }
                        }
                        catch { }
                    ReleaseOwnedFollow(p.VehicleId);
                    return;
                }
                if (!_ownedFollowing.TryGetValue(p.VehicleId, out var f))
                {
                    f = new DrivenFollow(); _ownedFollowing[p.VehicleId] = f; BeginOwnedFollow(ownedGo, p.VehicleId);
                    // Round-74: SEED the follow target from the cart's CURRENT pose. f.Pos was born
                    // Vector3.zero, and a borrow that STARTED indoors (every packet Bldg-tagged under
                    // the old door-hold) never overwrote it — TickOwnedFollow lerped the real cart
                    // toward WORLD ORIGIN until it left valid space and the native OnDisable chain
                    // (VehicleController:184 → UnregisterPlayerVehicle) dropped it from the fleet,
                    // which despawned the borrower's possessed proxy mid-push (lauraskrobo1
                    // 20260723-221447; the CartTrace 2026-07-07 "lerped to 951" reads as this same
                    // hole caught mid-flight and misdiagnosed as an interior-coordinates problem).
                    f.Pos = ownedGo.transform.position; f.Rot = ownedGo.transform.rotation;
                    // Bug #1: depict the borrower in the seat the same way the fleet path depicts any remote driver
                    // (560-585) — enclosed car → hide the walk model; open vehicle (borrowed flatbed/cart) → keep it
                    // visible + pinned. The owner's own fleet broadcast never runs on the owner's machine, so the
                    // follow path is the ONLY place to set this for a car being driven by someone else.
                    string tn = ""; try { var vc0 = ownedGo.GetComponentInChildren<VehicleController>(); if (vc0?.vehicleInstance != null) tn = vc0.vehicleInstance.vehicleTypeName.ToString(); } catch { }
                    f.Hide = !IsOpenVehicle(tn);
                    f.RideOff = f.Hide ? Vector3.zero : RideOffsetFor(tn);
                }
                f.Driver = p.DriverId;
                // Round-74 (door-hold REMOVED — user-directed seamless-cart model): follow the borrower
                // TRUTHFULLY everywhere, interior coordinates included. The cart is where they push it,
                // on every machine, and parks wherever they leave it. Interior coords are survivable
                // because the cart is KINEMATIC for the whole follow (BeginOwnedFollow) — it cannot fall
                // through a floor this machine hasn't loaded. The RELEASE is the only physics-resume
                // point, and ReleaseOwnedFollow defers that until the interior is loaded here.
                f.Pos = new Vector3(p.X, p.Y, p.Z); f.Rot = new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw);
                f.Bldg = p.Bldg ?? "";
                f.Until = Time.unscaledTime + DriveSyncTimeout;
                // (Round-74d dynamic ride offset REVERTED same day: the per-type offset was already
                // placing the pusher correctly — the "posture" gap is the push POSE animation, not
                // the standing spot — and the computed offset made placement worse.)
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
            if (!_ownedFollowing.TryGetValue(vid, out var f)) return;
            _ownedFollowing.Remove(vid);
            // Bug #1: restore the borrower's walk model + release the ride pin now they've stopped driving my car.
            if (!string.IsNullOrEmpty(f.Driver))
                try { RemotePlayerManager.SetRide(f.Driver, null, Vector3.zero); RemotePlayerManager.SetDriving(f.Driver, false); } catch { }
            var go = FindOwnedGo(vid);
            if (go != null)
            {
                // Round-74: resume physics ONLY where this machine can simulate the ground under the
                // cart — outdoors, or an interior WE currently have loaded. Released inside an
                // interior we have NOT loaded, the cart stays kinematic (parked-dormant; position and
                // data stay correct, the fleet keeps listing it, the borrower's ghost stays put)
                // until we enter that building — un-kinematic over an unloaded floor dropped it into
                // the void and the native deregistration ate it (both stuck-trolley reports).
                bool safeHere = string.IsNullOrEmpty(f.Bldg) || f.Bldg == MPRegisterSync.CurrentShopAddress;
                if (safeHere)
                { try { foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) rb.isKinematic = false; } catch { } }
                else
                {
                    _dormantReleases[vid] = f.Bldg;
                    Plugin.Logger.LogInfo($"[Drive] my car '{vid}' released inside '{f.Bldg}' (not loaded here) — parked dormant until I enter that building.");
                }
                // Persist the borrower's parking NATIVELY: transform → instance (SavePosition) and
                // the borrower's building → the instance's street data. The v3 fleet tag and the
                // save both now record the cart exactly where it was left — same as if the owner
                // had parked it there themselves.
                try
                {
                    var vc = go.GetComponentInChildren<VehicleController>();
                    if (vc?.vehicleInstance != null) { ApplyStreetData(vc.vehicleInstance, f.Bldg ?? ""); vc.SavePosition(); }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Drive] release persist: {ex.Message}"); }
            }
            Plugin.Logger.LogInfo($"[Drive] my car '{vid}' released → resuming local control.");
        }

        // ── Round-74: DATA-level follow for dormant-borrowed vehicles (no live object here) ──
        private sealed class DataFollowState { public Vector3 Pos; public Quaternion Rot = Quaternion.identity; public string Bldg = ""; public bool Has; }
        private static readonly Dictionary<string, DataFollowState> _dataFollow = new();

        private static void DataFollow(VehicleDrivePayload p)
        {
            try
            {
                var insts = SaveGameManager.Current?.VehicleInstances;
                if (insts == null) return;
                VehicleInstance inst = null;
                foreach (var vi in insts) if (vi != null && vi.id == p.VehicleId) { inst = vi; break; }
                if (inst == null) { _dataFollow.Remove(p.VehicleId); return; }   // not my vehicle
                if (p.Released)
                {
                    if (p.ParkState >= 0) try { inst.parkingState = (Helpers.ParkingState)p.ParkState; } catch { }   // round-232: borrower's exit verdict
                    if (_dataFollow.TryGetValue(p.VehicleId, out var df) && df.Has)
                        Plugin.Logger.LogInfo($"[Drive] dormant-borrowed '{p.VehicleId}' released at '{(string.IsNullOrEmpty(df.Bldg) ? "outdoors" : df.Bldg)}' — save data holds the parked state.");
                    _dataFollow.Remove(p.VehicleId);
                    return;
                }
                if (!_dataFollow.TryGetValue(p.VehicleId, out var f))
                {
                    f = new DataFollowState(); _dataFollow[p.VehicleId] = f;
                    Plugin.Logger.LogInfo($"[Drive] my dormant car '{p.VehicleId}' is being driven remotely — data-follow (no live object here).");
                }
                f.Pos = new Vector3(p.X, p.Y, p.Z); f.Rot = new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw);
                f.Bldg = p.Bldg ?? ""; f.Has = true;
                // The instance IS the truth here — write it live so the dormant fleet entry keeps
                // broadcasting the borrower's current position/building to everyone else.
                inst.position = new SerializableVector3 { x = p.X, y = p.Y, z = p.Z };
                inst.rotation = new SerializableQuaternion { x = p.Qx, y = p.Qy, z = p.Qz, w = p.Qw };
                ApplyStreetData(inst, f.Bldg);
                try { inst.fuel = p.Fuel; } catch { }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Drive] data-follow: {ex.Message}"); }
        }

        // ── Round-74b: owner-side interior visibility for OWN vehicles ──
        private static readonly Dictionary<string, bool> _ownedMaskState = new();   // vid → currently hidden

        private static void TickOwnedVehicleInteriorMask()
        {
            var list = VehicleHelper.AllPlayerVehicles;
            if (list == null) return;
            string mine = MPRegisterSync.CurrentShopAddress ?? "";
            for (int i = 0; i < list.Count; i++)
            {
                var vc = list[i];
                var inst = vc?.vehicleInstance;
                if (vc == null || inst == null || string.IsNullOrEmpty(inst.id)) continue;
                if (vc.controlledByPlayer) continue;                     // with me — never masked
                if (inst.id.StartsWith("BAMP_") && !inst.id.StartsWith("BAMP_TESTRIG")) continue;   // ghosts have their own mask
                // The vehicle's building: live borrower building while followed (street data lags
                // until release), else its own native street data.
                string bldg;
                if (_ownedFollowing.TryGetValue(inst.id, out var f)) bldg = f.Bldg ?? "";
                else bldg = string.IsNullOrEmpty(inst.streetName) ? "" : $"{inst.streetNumber} {inst.streetName}";
                bool hide = bldg.Length > 0 && bldg != mine;             // same rule as the ghost mask
                _ownedMaskState.TryGetValue(inst.id, out bool wasHidden);
                if (hide == wasHidden) continue;
                _ownedMaskState[inst.id] = hide;
                try
                {
                    foreach (var r in vc.GetComponentsInChildren<Renderer>(true)) if (r != null) r.enabled = !hide;
                    foreach (var c in vc.GetComponentsInChildren<Collider>(true)) if (c != null) c.enabled = !hide;
                }
                catch { }
                Plugin.Logger.LogInfo($"[Vehicle] own '{inst.vehicleTypeName}' ({inst.id}) {(hide ? "hidden" : "shown")} — its bldg='{bldg}' mine='{mine}' (owner-side interior mask, round-74b).");
            }
        }

        /// <summary>Write a "N ba:street_x" address key (or "" = outdoors) into the instance's
        /// native street data — the same fields every native door transition maintains.</summary>
        private static void ApplyStreetData(VehicleInstance inst, string bldg)
        {
            try
            {
                if (string.IsNullOrEmpty(bldg)) { inst.SetStreetData(string.Empty, 0); return; }
                int sp = bldg.IndexOf(' ');
                if (sp > 0 && int.TryParse(bldg.Substring(0, sp), out int num)) inst.SetStreetData(bldg.Substring(sp + 1), num);
            }
            catch { }
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
                // Bug #1: keep the borrower depicted as the driver every frame (re-asserts against avatar respawn /
                // appearance reapply). Enclosed → hidden + unpinned (nametag rides their synced pose, like the fleet
                // path); open → visible + pinned to the cart so it doesn't drift off.
                var f = kv.Value;
                if (!string.IsNullOrEmpty(f.Driver))
                {
                    RemotePlayerManager.SetDriving(f.Driver, f.Hide);
                    RemotePlayerManager.SetRide(f.Driver, f.Hide ? null : t, f.RideOff);
                }
            }
        }

        /// <summary>Can a granted on-foot key-holder take the wheel of this ghost right now? (The check half of
        /// TryDriveGhost, so the click handler can offer a Drive/Storage choice without driving yet — bug #3.)</summary>
        public static bool CanDriveGhost(string vid)
        {
            string reason;
            return CanDriveGhostEx(vid, out reason);
        }

        private static bool CanDriveGhostEx(string vid, out string reason)
        {
            reason = "ok";
            try
            {
                if (string.IsNullOrEmpty(vid) || !_remoteVehicles.TryGetValue(vid, out var rv) || rv?.Go == null) { reason = "no-ghost"; return false; }
                if (!GrantSync.IsGranted(rv.OwnerId, MPConfig.PlayerId)) { reason = "no-key"; return false; }
                if (VehicleHelper.GetCurrentVehicle() != null) { reason = "already-in-vehicle"; return false; }
                if (rv.OwnerUsing) { reason = "owner-using"; return false; }   // no dual possession (run-9)
                if (rv.Go.GetComponentInChildren<VehicleController>() == null) { reason = "no-controller"; return false; }
                return true;
            }
            catch (Exception ex) { reason = "threw:" + ex.Message; return false; }
        }

        /// <summary>A granted player clicked a ghost: if it's a DRIVABLE car they hold a key to and they're
        /// on foot, drop them into the driver's seat (the game's normal EnterVehicle). Returns true if it
        /// took the wheel, false to fall through to ride / cargo.</summary>
        public static bool TryDriveGhost(string vid)
        {
            try
            {
                if (!CanDriveGhost(vid)) return false;
                var rv = _remoteVehicles[vid];
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
                DespawnAll(skipDriven: true);   // re-sync from the next fleet broadcast; a ghost being DRIVEN survives until drive end (round-231)
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
                // Keep the owner-name label facing the camera ALWAYS — even while the ghost is being driven.
                // (Must run before the position-skip below, or a driven/shared car's label freezes — bug 1,
                // run-2026-06-29.)
                if (rv.Label != null && cam != null) rv.Label.rotation = cam.transform.rotation;
                // A granted ghost the local player is DRIVING owns its own position — don't yank it back.
                if (IsGhostBeingDriven(rv.Go)) continue;
                var t = rv.Go.transform;
                // Chase the dead-reckoned point (target + velocity·elapsed) so
                // motion stays continuous between packets even at low FPS.
                float ahead = Mathf.Min(now - rv.TargetAt, MaxVehicleExtrapolateSeconds);
                var predicted = rv.TargetPos + rv.Velocity * ahead;
                t.position = Vector3.Lerp(t.position, predicted, k);
                t.rotation = Quaternion.Slerp(t.rotation, rv.TargetRot, k);
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
