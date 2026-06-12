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
        }
        private const float MaxVehicleExtrapolateSeconds = 0.3f;
        private const float VehicleSnapDistance          = 15f;

        // Gameplay components destroyed on a ghost so the game stops treating it
        // as a vehicle anyone owns — no map icon, no tickets, not enterable.
        // Visual components (renderers, CarFeatures which holds the colour) stay.
        private static readonly string[] _killVehicleComponents =
        {
            "VehicleController", "CarController", "DamageHandler",
            "VehicleDeformationController", "WheelControllerManager",
            "FuelModuleWrapper", "SpeedLimiterModuleWrapper",
            "FlipOverModuleWrapper", "VariableCenterOfMass",
        };

        // Keyed by VehicleId (a player owns several vehicles).
        private static readonly Dictionary<string, RemoteVehicle> _remoteVehicles = new();

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
                    if (inst.id.Contains("BAMP_BAMP"))
                    {
                        if (!_lastManifestLogged.ContainsKey(inst.id))
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

                    fleet.Vehicles.Add(new VehicleEntry
                    {
                        VehicleId = inst.id,
                        TypeName  = tn,
                        ColorName = inst.vehicleColorName ?? "",
                        Driving   = vc.controlledByPlayer,
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
            DeregisterGhostFromSave(inst);   // remote ghost: visual-only, never save data

            var go  = vc.gameObject;
            int ownedAfterSpawn = SafeCount(() => VehicleHelper.AllPlayerVehicles?.Count ?? -1);
            string poiName;
            try { poiName = vc.poi != null ? vc.poi.gameObject.name : "(null)"; }
            catch { poiName = "(error)"; }

            // De-register from the player vehicle system.
            try { VehicleHelper.UnregisterPlayerVehicle(vc); } catch { }
            // Destroy the point-of-interest (map/world "owned" icon).
            try { if (vc.poi != null) UnityEngine.Object.Destroy(vc.poi.gameObject); } catch { }
            // Destroy every gameplay component — after this the game no longer
            // sees a vehicle here, just a prop: no ownership, no ticket, no entry.
            int killed = StripVehicleComponents(go);
            // Freeze physics — we drive the ghost purely by the synced transform.
            try
            {
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
            }
            catch { }

            int ownedAfterStrip = SafeCount(() => VehicleHelper.AllPlayerVehicles?.Count ?? -1);
            Plugin.Logger.LogInfo(
                $"[Vehicle] Ghost '{e.TypeName}' for '{ownerId}': AllPlayerVehicles " +
                $"{ownedBefore}→{ownedAfterSpawn}→{ownedAfterStrip}, poi='{poiName}', " +
                $"{killed} gameplay component(s) destroyed.");

            var label = CreateOwnerLabel(go, ownerId);
            return new RemoteVehicle
            {
                OwnerId   = ownerId,
                Go        = go,
                Label     = label,
                TargetPos = pos,
                TargetRot = rot,
            };
        }

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
                var comps = go.GetComponents(typeof(Component));
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    if (System.Array.IndexOf(_killVehicleComponents, c.GetType().Name) >= 0)
                    {
                        UnityEngine.Object.Destroy(c);
                        n++;
                    }
                }
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
                tmp.text      = ownerId + "'s car";
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

        /// <summary>Smooths ghost vehicles toward their networked transform; billboards labels.</summary>
        public static void TickSmoothing()
        {
            if (_remoteVehicles.Count == 0) return;
            // Capped so packet corrections blend over frames at low FPS.
            float k   = Mathf.Min(Time.deltaTime * 12f, 0.5f);
            float now = Time.unscaledTime;
            var cam = Camera.main;
            foreach (var rv in _remoteVehicles.Values)
            {
                if (rv.Go == null) continue;
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
