using System;
using System.Collections.Generic;
using GleyTrafficSystem;
using HarmonyLib;
using UnityEngine;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Private driver in multiplayer (2026-09-02, user-approved; design: .modding/03-systems/private-driver-mp.md).
    ///
    /// A "service car" is a Gley car THIS machine spawned for a ride: the summoned private driver, the destination
    /// driver the game loads at arrival, and a friend's arrival car (GhostTaxi). The game never puts these in
    /// AllPlayerVehicles, so nobody else saw them and the HOST's traffic drove straight through a client's one.
    /// The registry appends them to the normal fleet broadcast (VehicleEntry.Service) so every machine spawns a
    /// mirror — since A2 (2026-09-02) a look-alike clone of the owner's traffic prefab (TrySpawnLookalike: same
    /// model, the owner's paint, the prefab's driver figure) with its colliders on the player-vehicles layer the
    /// host's traffic sensors brake for; the field-proven player-vehicle body is the fallback (MirrorStyle) — and
    /// prunes itself when Gley recycles the car (the ordinary traffic despawn), which retires the mirror everywhere.
    /// </summary>
    internal static class ServiceCars
    {
        private sealed class Entry
        {
            public string     Vid = "";
            public GameObject Go = null!;
            public string     TypeName = "";
            public string     ColorName = "";
            public float      StopUntil;                            // owner side: auto-resume deadline (0 = not stopped by a rider)
            public bool       SavedValid; public float SavedValue; public SpecialDriveActionTypes SavedAction;
            public Waypoint?  SavedTarget; public List<int>? SavedPath;   // owner side: an APPROACHING driver's route, restored after a rider's stop (user, 2026-09-02)
        }
        private static readonly Dictionary<string, Entry> _local = new();
        private static int _seq;
        private static float _nextPrune;

        // ── registry ────────────────────────────────────────────────────────────────────────────────
        internal static void RegisterLocal(GameObject go, string typeName, string colorName, string why)
        {
            try
            {
                if (go == null) return;
                foreach (var e in _local.Values) if (e.Go == go) return;   // already mirrored (re-summon of a waiting car)
                string vid = $"SVC_{MPConfig.PlayerId}_{++_seq}";
                _local[vid] = new Entry { Vid = vid, Go = go, TypeName = typeName ?? "", ColorName = colorName ?? "" };
                VehicleManager.MarkFleetDirty();
                Plugin.Logger.LogInfo($"[Service] registered '{vid}' ({typeName}) — {why}; mirrored to everyone until the traffic system recycles it.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] register: {ex.Message}"); }
        }

        internal static bool IsLocalServiceCar(GameObject go)
        {
            if (go == null || _local.Count == 0) return false;
            foreach (var e in _local.Values) if (e.Go == go) return true;
            return false;
        }
        /// <summary>Client side: a pool car the per-tick traffic clear must leave alone — one of ours, or any car
        /// carrying the game's PrivateDriverVehicle (the summoned / destination driver before it is registered).</summary>
        internal static bool IsClientKept(GameObject go)
        {
            if (go == null) return false;
            if (IsLocalServiceCar(go)) return true;
            try { return go.GetComponent<Helpers.PrivateDriverVehicle>() != null; } catch { return false; }
        }

        // ── world-load traffic batch = vanilla (review MAJOR-2, user ruling "vanilla traffic" 2026-09-02) ─────────
        // DensityManager.Initialize runs LoadInitialVehicles for maxNrOfVehicles attempts — the Initialize CAP, which
        // the bump raised (30 → 59). Clamp that first batch to the authored pool total so a world starts with exactly
        // the cars vanilla starts with; the game's own SetTrafficDensity takes over from there. MP only.
        [HarmonyPatch(typeof(DensityManager), nameof(DensityManager.Initialize))]
        public static class Patch_DensityManager_Initialize_VanillaBatch
        {
            static void Prefix(ref int maxNrOfVehicles)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                    if (AuthoredPoolTotal > 0 && maxNrOfVehicles > AuthoredPoolTotal)
                    {
                        Plugin.Logger.LogInfo($"[Service] world-load traffic batch clamped {maxNrOfVehicles} → {AuthoredPoolTotal} (the authored pool; vanilla start).");
                        maxNrOfVehicles = AuthoredPoolTotal;
                    }
                }
                catch { }
            }
        }

        // ── stuck-car rescue exemption (verified 2026-09-02, F-2026-09-02-V; user-approved) ────────────────────
        // The game's AiCarRescueCheck (a component on every pool car, ticked by a plain MonoBehaviour every 0.1 s —
        // so alive on clients too) removes any car that is not the phone's CurrentVehicle and sits still > 20 s
        // off-screen / > 40 s on-screen. A service car stands still ON PURPOSE: parked on a client (the sim is off;
        // PruneParkedClientCars retires it by distance) or hard-stopped on the host while a friend walks up
        // (StopUntil). Skip the check for exactly those; every other car — including a handed-off host car that is
        // genuinely stuck in traffic — keeps the native rescue.
        internal static bool RescueExempt(GameObject go)
        {
            if (go == null || (!MPServer.IsRunning && !MPClient.InMpGame)) return false;
            // Pure client: with the sim OFF every service car is parked by design (exempt); with the sim ON the
            // native rule applies as on the host — only a car stopped for a rider (review MINOR-5: parity, and a
            // permanently-braking car must not read as "parked on purpose").
            if (!MPServer.IsRunning && !TrafficSync.ClientServiceSimEnabled) return IsClientKept(go);
            foreach (var e in _local.Values)
                if (e.Go == go) return e.StopUntil > Time.unscaledTime;    // host: only while a rider is walking up
            return false;
        }
        [HarmonyPatch(typeof(AiCarRescueCheck), nameof(AiCarRescueCheck.DoUpdate))]
        public static class Patch_AiCarRescueCheck_ServiceExempt
        {
            private static int _logs;
            static bool Prefix(AiCarRescueCheck __instance)
            {
                try
                {
                    if (__instance == null || !RescueExempt(__instance.gameObject)) return true;
                    if (_logs++ < 3) Plugin.Logger.LogInfo("[Service] stuck-car rescue skipped for a service car standing still on purpose (parked client car / waiting for a rider).");
                    return false;
                }
                catch { return true; }
            }
        }

        // ── client side: parked service cars (user-approved 2026-09-02) ─────────────────────────────────────
        // With TrafficSync.ClientServiceSimEnabled OFF a pure client's traffic brain is off, so a service car the
        // game released — the origin after the owner's ride, a friend's arrival car — never drives away and Gley
        // never recycles it. It survives the client's traffic clear as a PARKED car (no vanish in front of anyone)
        // and is removed here once no player, local or remote, is within ClientParkedKeepRadius. With the flag ON
        // (default since 2026-09-02) such cars drive away and Gley recycles them; this prune is then the belt. Never the waiting driver (SmartphonePrivateDriverUI.CurrentVehicle), never a car
        // a rider is walking to (StopUntil). Positions are read live at the moment of the decision. Parity
        // (approach drive, drive-off) on clients is a separate design — see the 2026-09-02 brainstorm.
        internal const float ClientParkedKeepRadius = 150f;
        private static float _nextClientPrune;
        private static void PruneParkedClientCars(float now)
        {
            if (_local.Count == 0) return;
            GameObject? current = null;
            try { var cv = Player.HUD.SmartphoneUI.SmartphonePrivateDriverUI.CurrentVehicle; if (cv != null) current = cv.gameObject; } catch { }
            List<Entry>? gone = null;
            foreach (var e in _local.Values)
            {
                if (e.Go == null || !e.Go.activeInHierarchy) continue;
                if (e.Go == current) continue;
                if (e.StopUntil > now) continue;
                if (RemotePlayerManager.AnyPlayerWithin(e.Go.transform.position, ClientParkedKeepRadius)) continue;
                (gone ??= new List<Entry>()).Add(e);
            }
            if (gone == null) return;
            foreach (var e in gone)
            {
                try { TrafficManager.Instance?.RemoveVehicle(e.Go); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] parked-car removal '{e.Vid}': {ex.Message}"); }
                Plugin.Logger.LogInfo($"[Service] '{e.Vid}' parked service car removed — no player within {ClientParkedKeepRadius:F0} m (client: the traffic system cannot recycle it).");
            }
            Prune("parked car removed");
        }

        // ── receiver side: what a service mirror looks like (A2, user-approved 2026-09-02) ─────────────────
        internal enum GhostStyle { TrafficLookalike, PlayerBody }
        /// <summary>The one-line fallback switch. TrafficLookalike = a clone of the traffic prefab the owner's machine
        /// actually drives (driver figure included when the prefab carries one); PlayerBody = the field-proven
        /// player-vehicle ghost. Either way a failed look-alike falls through to the player body at spawn time.</summary>
        internal static GhostStyle MirrorStyle = GhostStyle.TrafficLookalike;
        private static readonly HashSet<string> _lookalikeLogged = new();

        /// <summary>Spawns the look-alike body for a service entry, or null (the caller then spawns the player body).
        /// The prefab is resolved exactly as the summoner's machine resolved it (PrivateDriverHelpers.GetAiVehiclePrefab
        /// from the vehicle type), cloned through TrafficSync's traffic-ghost routine (inactive instantiate, Gley
        /// components stripped, kinematic), painted with the two native calls SetupVehicle makes, and its colliders
        /// moved to the layer Gley brakes for (TrafficSync.ServiceColliderLayer, resolved from LayerSetupData.playerLayers;
        /// H-SVC-113 measured 2026-09-02 that PlayerVehicles is NOT that layer): Gley classifies a sensed collider by
        /// LAYER (VehicleComponent.OnTriggerEnter → playerLayers branch, no traffic index involved) and the mod's click
        /// ray includes that layer, so braking and clicking work for the swapped body.</summary>
        internal static GameObject? TrySpawnLookalike(string ownerId, VehicleEntry e, Vector3 pos, Quaternion rot)
        {
            try
            {
                var vt = Vehicles.VehicleTypes.VehicleTypeHelper.GetVehicleType(e.TypeName);
                if (vt == null) { Fallback(e, "unknown vehicle type"); return null; }
                if (TrafficSync.ServiceColliderLayer() < 0) { Fallback(e, "no layer the traffic system brakes for (review MAJOR-1)"); return null; }
                var ai = Helpers.PrivateDriverHelpers.GetAiVehiclePrefab(vt);   // the very asset the owner's car was loaded from
                if (ai == null) { Fallback(e, "no AI prefab for the type"); return null; }
                var go = TrafficSync.CloneStrippedPrefab(ai.gameObject, ai.gameObject.name, pos, rot);
                if (go == null) { Fallback(e, "clone failed"); return null; }
                // The owner's paint — the same two native calls the summoner's machine makes (PrivateDriverHelpers.SetupVehicle).
                // CarFeatures is not on the strip list and SetColor writes serialized bodyMeshes, so it works on the clone.
                bool driver = false; string paint = "none";
                try
                {
                    var cf = go.GetComponent<CarFeatures>();
                    if (cf != null)
                    {
                        cf.SetDirtiness(0f);
                        if (!string.IsNullOrEmpty(e.ColorName) && Helpers.VehicleHelper.TryGetVehicleColor(e.ColorName, out var col)) { cf.SetColor(col); paint = e.ColorName; }
                        driver = cf.driverRenderer != null && cf.driverRenderer.enabled && cf.driverRenderer.gameObject.activeInHierarchy;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] look-alike paint '{e.VehicleId}': {ex.Message}"); }
                int layer = TrafficSync.ServiceColliderLayer();   // H-SVC-113: the layer Gley's sensors treat as "player" (NOT PlayerVehicles)
                int relayered = TrafficSync.RelayerCollidersToServiceLayer(go);
                if (_lookalikeLogged.Add(e.TypeName))
                    Plugin.Logger.LogInfo($"[Service] mirror style: traffic look-alike '{ai.gameObject.name}' for {e.TypeName} (paint={paint}, driver figure={(driver ? "yes" : "no")}, {relayered} collider object(s) moved to layer '{(layer >= 0 ? LayerMask.LayerToName(layer) : "unchanged")}').");
                return go;
            }
            catch (Exception ex) { Fallback(e, $"{ex.GetType().Name}: {ex.Message}"); return null; }
        }
        private static void Fallback(VehicleEntry e, string why)
        {
            if (_lookalikeLogged.Add("fallback:" + e.TypeName))
                Plugin.Logger.LogWarning($"[Service] look-alike for {e.TypeName} unavailable ({why}) — this machine mirrors it with the player-vehicle body instead.");
        }

        private static void Prune(string why)
        {
            List<string>? dead = null;
            foreach (var kv in _local)
                if (kv.Value.Go == null || !kv.Value.Go.activeInHierarchy) (dead ??= new List<string>()).Add(kv.Key);
            if (dead == null) return;
            foreach (var k in dead) { _local.Remove(k); Plugin.Logger.LogInfo($"[Service] '{k}' retired ({why}) — mirror drops with the next fleet packet."); }
            VehicleManager.MarkFleetDirty();
        }

        /// <summary>Called by VehicleManager.ReadLocalFleet — one Driving+Service entry per live service car.</summary>
        internal static void AppendFleetEntries(VehicleFleetPayload fleet)
        {
            try
            {
                if (_local.Count == 0 || fleet == null) return;
                Prune("pool recycled");
                foreach (var e in _local.Values)
                {
                    var t = e.Go.transform;
                    fleet.Vehicles.Add(new VehicleEntry
                    {
                        VehicleId = e.Vid, TypeName = e.TypeName, ColorName = e.ColorName,
                        Driving = true, Service = true, Fuel = 1f,
                        X = t.position.x, Y = t.position.y, Z = t.position.z,
                        Qx = t.rotation.x, Qy = t.rotation.y, Qz = t.rotation.z, Qw = t.rotation.w,
                        Cargo = "", CarriedItems = 0, Bldg = "",
                    });
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] fleet append: {ex.Message}"); }
        }

        // ── native hooks: register on summon / arrival; drive off instead of instant removal ──────────
        [HarmonyPatch(typeof(Helpers.PrivateDriverHelpers), nameof(Helpers.PrivateDriverHelpers.SummonPrivateDriverVehicle))]
        public static class Patch_Summon_Register
        {
            static void Postfix(Helpers.PrivateDriverVehicle __result)
            {
                try
                {
                    if (__result == null) return;
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                    var inst = __result.vehicleInstance;
                    RegisterLocal(__result.gameObject, inst?.vehicleTypeName?.ToString() ?? "", inst?.vehicleColorName ?? "", "private driver summoned");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] summon hook: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Helpers.PrivateDriverVehicle), nameof(Helpers.PrivateDriverVehicle.InstantiateVehicle))]
        public static class Patch_Arrival_Register
        {
            static void Postfix(Helpers.PrivateDriverVehicle __instance, VehicleComponent __result)
            {
                try
                {
                    if (__result == null) return;
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                    var inst = __instance?.vehicleInstance;
                    RegisterLocal(__result.gameObject, inst?.vehicleTypeName?.ToString() ?? "", inst?.vehicleColorName ?? "", "destination driver loaded");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] arrival hook: {ex.Message}"); }
            }
        }

        // DRIVE-OFF (user ruling 2026-09-02): at arrival the game removes the ORIGIN car instantly wherever it is
        // (InstantiateVehicle → DismissPrivateDriver(force, instantRemove:true)). In MP rides are instant, so a
        // bystander saw the car pull away and blink out a second later. Turn that one call into DriveAway: the car
        // stays plain traffic, stays mirrored (registry by GameObject) and stays ridable until Gley recycles it.
        // GUARD (user-approved 2026-09-02): the destination car comes from the SAME pool. Gley LoadVehicle picks a
        // FREE slot with the same prefab reference, else the FIRST slot whose prefab NAME matches — free or not — and
        // AddVehicleAtPositionNow removes an active pick first. If that pick would be the origin car, the drive-off
        // is impossible (the origin would be yanked to the destination mid-drive-off): the host first tries to free a
        // slot by removing an out-of-view AMBIENT car of that model (Gley's own CanBeRemoved test — the density
        // manager does the same every frame), and if the pick is still the origin the native instant removal stands,
        // so the destination driver always appears. The prediction runs LIVE at this call, on the same list and the
        // same prefab the native code is about to use.
        [HarmonyPatch(typeof(Player.HUD.SmartphoneUI.SmartphonePrivateDriverUI), "DismissPrivateDriver",
                      new[] { typeof(bool), typeof(VehicleInstance), typeof(bool) })]
        public static class Patch_Dismiss_DriveOffAtArrival
        {
            private static int _logs, _skips;
            static void Prefix(ref bool instantRemove)
            {
                try
                {
                    if (!instantRemove) return;
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                    if (!TaxiSystem.IsTraveling) return;   // only the arrival hand-off; an explicit "remove" elsewhere stays native
                    // Review MINOR-6 / #2 MINOR-1: during a FRIEND's ride the game's no-waypoint fallback dismisses the
                    // RIDER's own driver — not the arrival hand-off; stay native. cityMap.Taxi is already null by then
                    // (TravelCoroutine closes the map right after DriveAway), so the test is the mod's own flag, set at
                    // boarding by GhostTaxi.DriveAway and cleared once the travel ends.
                    if (FriendRideActive) return;
                    var pdv = Player.HUD.SmartphoneUI.SmartphonePrivateDriverUI.CurrentVehicle;
                    var origin = pdv != null ? pdv.GetComponent<VehicleComponent>() : null;
                    if (origin == null)
                    {
                        if (_skips++ < 5) Plugin.Logger.LogWarning("[Service] arrival hand-off skipped: the current driver has no pool identity — native removal.");
                        return;
                    }
                    GameObject? aiPrefab = null;
                    try { var vt = pdv!.vehicleInstance?.VehicleType; if (vt != null) aiPrefab = Helpers.PrivateDriverHelpers.GetAiVehiclePrefab(vt)?.gameObject; } catch { }
                    if (aiPrefab == null) aiPrefab = origin.prefab;
                    string model = aiPrefab != null ? aiPrefab.name : "?";
                    var pick = PredictDestinationSlot(aiPrefab, out string how);
                    if (pick == null)
                    {
                        if (_skips++ < 5) Plugin.Logger.LogWarning($"[Service] arrival hand-off skipped for {model}: the pool has no slot of that model at all — native removal (the destination spawn will fail natively too).");
                        return;
                    }
                    if (pick == origin && MPServer.IsRunning && TryFreeHostSlot(aiPrefab!, origin))
                        pick = PredictDestinationSlot(aiPrefab, out how);
                    PoolSlots(aiPrefab, out int total, out int free);
                    if (pick == null || pick == origin)
                    {
                        if (_skips++ < 20) Plugin.Logger.LogInfo($"[Service] arrival hand-off SKIPPED for {model}: the destination would reuse the origin car (pool {model}: {total} slot(s), {free} free) — native instant removal keeps the destination driver.");
                        return;   // instantRemove stays true
                    }
                    string pickState = !pick.gameObject.activeInHierarchy ? "free" : IsLocalServiceCar(pick.gameObject) || pick.GetComponent<Helpers.PrivateDriverVehicle>() != null ? "ACTIVE service car (will be yanked)" : "active ambient car (yanked, as native)";
                    instantRemove = false;
                    if (_logs++ < 20) Plugin.Logger.LogInfo($"[Service] arrival hand-off: origin drives off; destination slot '{pick.name}' ({pickState}, matched {how}); pool {model}: {total} slot(s), {free} free.");
                    return;
                }
                catch (Exception ex) { if (_skips++ < 5) Plugin.Logger.LogWarning($"[Service] arrival hand-off guard: {ex.GetType().Name}: {ex.Message} — native removal."); }
            }
        }

        // ── pool helpers (2026-09-02) ─────────────────────────────────────────────────────────────────────
        // ── friends ride free (review BLOCKER-1, user ruling 2026-09-02) ──────────────────────────────────────
        // TaxiSystem.GetPrice waives the fare for exactly one type (PrivateDriverVehicle); a GhostTaxi ride would be
        // billed distance × 0.15, shown on the map button, and silently refused when the rider is short. MP only —
        // single player never has a GhostTaxi, so the guard passes through by construction.
        [HarmonyPatch(typeof(TaxiSystem), nameof(TaxiSystem.GetPrice))]
        public static class Patch_TaxiSystem_GetPrice_ServiceFree
        {
            private static int _logs;
            static bool Prefix(ref float __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return true;
                    if (!(InstanceBehavior<CityManager>.Instance?.cityMap?.Taxi is GhostTaxi)) return true;
                    __result = 0f;
                    if (_logs++ < 3) Plugin.Logger.LogInfo("[Service] friend ride: fare waived (the owner's contract already paid for the driver).");
                    return false;
                }
                catch { return true; }
            }
        }

        // ── pool identity (H-SVC-114, field 2026-09-02) ───────────────────────────────────────────────────
        // Gley LoadVehicle matches a FREE slot by prefab REFERENCE first and only then falls back to the FIRST slot of
        // that prefab NAME regardless of state. The private-driver prefab comes from PrefabHelper.LoadPrefab — a
        // separate object from the VehiclePool's serialized reference — so the reference search never matches and the
        // by-name fallback always returns the lowest slot: the departing origin at the arrival hand-off (client log:
        // 3 slots, 2 free, origin picked). Substitute the pool's own reference so Gley's first search takes any FREE
        // slot of the model; the by-name fallback still applies only when every slot is busy. MP only.
        internal static GameObject? PoolReferenceFor(GameObject? prefab)
        {
            if (prefab == null) return null;
            var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
            if (list == null) return null;
            GameObject? byName = null;
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (v == null || v.prefab == null) continue;
                if (v.prefab == prefab) return null;                       // exact reference present — nothing to do
                if (byName == null && v.prefab.name == prefab.name) byName = v.prefab;
            }
            return byName;
        }
        [HarmonyPatch(typeof(TrafficManager), nameof(TrafficManager.LoadVehicle))]
        public static class Patch_LoadVehicle_PoolReference
        {
            private static int _logs;
            static void Prefix(ref GameObject vehiclePrefab)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                    var sub = PoolReferenceFor(vehiclePrefab);
                    if (sub == null) return;
                    if (_logs++ < 5) Plugin.Logger.LogInfo($"[Service] LoadVehicle('{vehiclePrefab.name}'): the requested prefab is not the pool's own reference — substituting it so a FREE slot of the model is taken (H-SVC-114).");
                    vehiclePrefab = sub;
                }
                catch { }
            }
        }

        /// <summary>Exactly Gley TrafficManager.LoadVehicle's choice, read live — after the same reference substitution
        /// Patch_LoadVehicle_PoolReference applies: a FREE slot with the same prefab reference, else the FIRST slot
        /// whose prefab NAME matches (free or not); null when the model has no slot.</summary>
        private static VehicleComponent? PredictDestinationSlot(GameObject? prefab, out string how)
        {
            how = "none";
            if (prefab == null) return null;
            var sub = PoolReferenceFor(prefab); if (sub != null) prefab = sub;
            var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (v != null && v.prefab == prefab && !v.gameObject.activeInHierarchy) { how = "by reference (free)"; return v; }
            }
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (v != null && v.prefab != null && v.prefab.name == prefab.name) { how = "by name (first slot)"; return v; }
            }
            return null;
        }
        private static void PoolSlots(GameObject? prefab, out int total, out int free)
        {
            total = 0; free = 0;
            try
            {
                var list = TrafficManager.Instance?.trafficVehicles?.GetVehicleList();
                if (list == null || prefab == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null || v.prefab == null || v.prefab.name != prefab.name) continue;
                    total++;
                    if (!v.gameObject.activeInHierarchy) free++;
                }
            }
            catch { }
        }
        /// <summary>HOST only: free one pool slot of the model by removing the farthest active AMBIENT car of it that
        /// Gley itself says may go (CanBeRemoved = out of view, no preset path). Never a service car, never a car
        /// carrying a PrivateDriverVehicle, never the origin. Returns true when a slot was freed.</summary>
        private static bool TryFreeHostSlot(GameObject prefab, VehicleComponent origin)
        {
            try
            {
                var tm = TrafficManager.Instance; var tv = tm?.trafficVehicles; var list = tv?.GetVehicleList();
                if (tm == null || tv == null || list == null) return false;
                Vector3 me = Vector3.zero; try { me = Helpers.PlayerHelper.GetPosition(); } catch { }
                VehicleComponent? best = null; float bestD = -1f;
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null || v == origin || v.prefab == null || v.prefab.name != prefab.name) continue;
                    if (!v.gameObject.activeInHierarchy) continue;
                    if (IsLocalServiceCar(v.gameObject) || v.GetComponent<Helpers.PrivateDriverVehicle>() != null) continue;
                    bool may; try { may = tv.CanBeRemoved(v.GetIndex()); } catch { may = false; }
                    if (!may) continue;
                    float d = (v.transform.position - me).sqrMagnitude;
                    if (d > bestD) { bestD = d; best = v; }
                }
                if (best == null) return false;
                tm.RemoveVehicle(best.gameObject);
                Plugin.Logger.LogInfo($"[Service] freed a pool slot of {prefab.name} for the arrival hand-off: removed out-of-view ambient car '{best.name}' at {Mathf.Sqrt(Mathf.Max(bestD, 0f)):F0} m (the traffic system recycles such cars every frame).");
                return !best.gameObject.activeInHierarchy;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] free host slot: {ex.Message}"); return false; }
        }

        // ── pool census + slot bump (user-approved 2026-09-02) ──────────────────────────────────────────────
        // Slots per model are authored in the VehiclePool asset (CarType.nrOfVehicles); the game sizes the traffic
        // cap as their SUM (CityManager: Manager.Initialize(..., vehiclePool.GetNumberOfVehicles(), vehiclePool, ...))
        // and Gley's cap-sized arrays are indexed by POOL index, so the pool must never exceed the cap. This prefix
        // raises both together, before anything is allocated: every AI-drivable model gets at least PoolMinSlots
        // slots (origin driving off + destination + one lingering previous car), and the cap grows by the same
        // delta. Visible traffic is unchanged — the game sets density as an ABSOLUTE count (TimeOfDayController →
        // SetTrafficDensity(numberOfAiVehicles)); the cap is only an upper bound. The asset object is edited in
        // memory for the process lifetime, so a world reload sees the bumped counts and a zero delta (idempotent).
        // The postfix logs the census — authored → effective per model — one line per world load, MP only.
        internal const int PoolMinSlots = 3;
        private static readonly Dictionary<string, int> _authoredSlots = new();
        private static readonly Dictionary<CarType, int> _authoredByType = new();   // to restore the asset after the pool is built
        internal static int AuthoredPoolTotal;                                       // vanilla cap = sum of authored slots
        [HarmonyPatch(typeof(TrafficManager), nameof(TrafficManager.Initialize))]
        public static class Patch_TrafficPool_CensusAndBump
        {
            static void Prefix(ref int nrOfVehicles, VehiclePool vehiclePool)
            {
                try
                {
                    _authoredSlots.Clear(); _authoredByType.Clear(); AuthoredPoolTotal = 0;
                    if (vehiclePool == null || vehiclePool.trafficCars == null) return;
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;   // single player: authored pool, untouched
                    int delta = 0; var bumped = new List<string>();
                    foreach (var ct in vehiclePool.trafficCars)
                    {
                        if (ct == null || !ct.canBeAiDriven || ct.nrOfVehicles <= 0) continue;
                        string name = ct.vehiclePrefab != null ? ct.vehiclePrefab.name : (ct.name ?? "?");
                        if (!_authoredSlots.ContainsKey(name)) _authoredSlots[name] = ct.nrOfVehicles;
                        _authoredByType[ct] = ct.nrOfVehicles; AuthoredPoolTotal += ct.nrOfVehicles;
                        if (ct.nrOfVehicles < PoolMinSlots)
                        {
                            bumped.Add($"{name} {ct.nrOfVehicles}→{PoolMinSlots}");
                            delta += PoolMinSlots - ct.nrOfVehicles;
                            ct.nrOfVehicles = PoolMinSlots;
                        }
                    }
                    if (delta > 0)
                    {
                        int before = nrOfVehicles; nrOfVehicles += delta;
                        Plugin.Logger.LogInfo($"[Service] traffic pool bump: +{delta} slot(s), cap {before}→{nrOfVehicles} — {string.Join(", ", bumped)}.");
                    }
                    else Plugin.Logger.LogInfo($"[Service] traffic pool bump: nothing below {PoolMinSlots} slots (cap {nrOfVehicles}).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] pool bump: {ex.Message}"); }
            }
            static void Postfix(TrafficManager __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                    // Review MAJOR-2 (2026-09-02): the VehiclePool is a process-lifetime asset and the game re-reads
                    // GetNumberOfVehicles() at every world load — restore the authored counts now that the pool is
                    // built, so a later single-player world (and ParkingSimulator's pool sizing) sees vanilla data.
                    // The running world keeps its bumped slots; the next MP load bumps again from the authored values.
                    int restored = 0;
                    foreach (var kv in _authoredByType) { if (kv.Key != null && kv.Key.nrOfVehicles != kv.Value) { kv.Key.nrOfVehicles = kv.Value; restored++; } }
                    if (restored > 0) Plugin.Logger.LogInfo($"[Service] traffic pool asset restored to authored counts ({restored} model(s)) — the running world keeps its bumped slots.");
                    var list = __instance?.trafficVehicles?.GetVehicleList();
                    if (list == null) return;
                    var now = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var v = list[i]; if (v == null) continue;
                        string name = v.prefab != null ? v.prefab.name : "?";
                        now[name] = now.TryGetValue(name, out int c) ? c + 1 : 1;
                    }
                    var parts = new List<string>();
                    foreach (var kv in now)
                        parts.Add(_authoredSlots.TryGetValue(kv.Key, out int a) && a != kv.Value ? $"{kv.Key} {a}→{kv.Value}" : $"{kv.Key} {kv.Value}");
                    Plugin.Logger.LogInfo($"[Service] traffic pool census: {now.Count} model(s), {list.Count} slot(s) — {string.Join(", ", parts)}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] pool census: {ex.Message}"); }
            }
        }

        // ── owner side: stop / resume for a rider ────────────────────────────────────────────────────
        private static readonly System.Reflection.FieldInfo? _fTargetWaypoint  = AccessTools.Field(typeof(Helpers.PrivateDriverVehicle), "_targetWaypoint");
        private static readonly System.Reflection.FieldInfo? _fLastDriveAction = AccessTools.Field(typeof(Helpers.PrivateDriverVehicle), "_lastDriveAction");
        private static readonly System.Reflection.FieldInfo? _fLastActionValue = AccessTools.Field(typeof(Helpers.PrivateDriverVehicle), "_lastActionValue");
        internal static void OwnerStop(ServiceCarPayload p)
        {
            try
            {
                if (p == null || !_local.TryGetValue(p.VehicleId, out var e) || e.Go == null) return;
                var pdv = e.Go.GetComponent<Helpers.PrivateDriverVehicle>();
                if (pdv != null)
                {
                    // User ruling 2026-09-02: a rider's hail must not cost the owner the approach. The native stop
                    // (RequestVehicleStop) forgets the destination and CLEARS the route list in place, so snapshot
                    // both first — only while the driver is actually approaching (a destination is set).
                    try
                    {
                        var vcA = e.Go.GetComponent<VehicleComponent>();
                        var target = _fTargetWaypoint?.GetValue(pdv) as Waypoint;
                        // Review #2 MAJOR-3: STICKY — a second stop on an already-stopped car finds no target and must
                        // not erase the snapshot the first stop took; Resume is the only consumer and clearer.
                        if (target != null && vcA != null && vcA.presetPath != null && vcA.presetPath.Count > 0)
                        { e.SavedTarget = target; e.SavedPath = new List<int>(vcA.presetPath); }
                    }
                    catch { }
                    pdv.RequestVehicleStop(hail: true, hardStop: true);
                }
                else
                {
                    var vc = e.Go.GetComponent<VehicleComponent>();
                    if (vc == null) return;
                    int idx = vc.GetIndex();
                    if (!e.SavedValid) { (e.SavedValue, e.SavedAction) = TrafficManager.Instance.GetCurrentDrivingState(idx); e.SavedValid = true; }
                    AIEvents.TriggerChangeDrivingStateEvent(idx, SpecialDriveActionTypes.StopNow, 60f);
                }
                e.StopUntil = Time.unscaledTime + 60f;
                Plugin.Logger.LogInfo($"[Service] '{e.Vid}' stopped for '{p.PlayerId}' (auto-resume in 60 s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] owner stop: {ex.Message}"); }
        }

        internal static void OwnerResume(ServiceCarPayload p)
        {
            try
            {
                if (p == null || !_local.TryGetValue(p.VehicleId, out var e)) return;
                Resume(e, $"rider '{p.PlayerId}' boarded or cancelled");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] owner resume: {ex.Message}"); }
        }

        private static void Resume(Entry e, string why)
        {
            if (e.StopUntil <= 0f) return;
            e.StopUntil = 0f;
            if (e.Go == null) return;
            var pdv = e.Go.GetComponent<Helpers.PrivateDriverVehicle>();
            if (pdv != null)
            {
                // User ruling 2026-09-02 (field run 3): a rider who did NOT call the car must never send it away. A
                // driver still on duty for its owner — waiting (empty preset path) or approaching (routed) — keeps
                // waiting; only the owner's own boarding releases it (native DriveAway). Live read of the game's own
                // state: presetPath == null means the car was already released and is plain traffic with a leftover
                // component (the origin after a hand-off) — that one resumes. Calling DriveAway on an on-duty driver
                // also fought PrivateDriverVehicle.Update, which re-applies StopInPoint to the CurrentVehicle every
                // frame — the "moved a little, then stopped again" the host saw.
                var vc = e.Go.GetComponent<VehicleComponent>();
                bool onDuty = vc != null && vc.presetPath != null;
                if (onDuty)
                {
                    // An APPROACHING driver gets its route and destination back (user, 2026-09-02): the car has not
                    // moved since the stop, so the remaining route is valid from where it stands; Gley's next waypoint
                    // request pops already-passed entries and targets presetPath[0] (DrivingAI.WaypointRequested).
                    // The drive action/value the native stop captured (_lastDriveAction/_lastActionValue) is what
                    // DriveAway itself replays — the same three calls, minus nulling the route.
                    if (e.SavedTarget != null && e.SavedPath != null && e.SavedPath.Count > 0)
                    {
                        try
                        {
                            vc!.presetPath = new List<int>(e.SavedPath);
                            pdv.SetTargetWaypoint(e.SavedTarget);
                            var action = _fLastDriveAction != null ? (SpecialDriveActionTypes)_fLastDriveAction.GetValue(pdv) : SpecialDriveActionTypes.Continue;
                            float value = _fLastActionValue != null ? (float)_fLastActionValue.GetValue(pdv) : 0f;
                            TrafficManager.Instance.ForceSetVehicleActiveAction(vc, action);
                            AIEvents.TriggerChangeDrivingStateEvent(vc.GetIndex(), action, value);
                            TrafficManager.Instance.VehicleUpdateWaypoint(vc);
                            Plugin.Logger.LogInfo($"[Service] '{e.Vid}' resumes its approach to its owner ({e.SavedPath.Count} waypoint(s) left) — {why}.");
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] '{e.Vid}' approach restore: {ex.Message} — it waits where it stands."); }
                        e.SavedTarget = null; e.SavedPath = null;
                        return;
                    }
                    Plugin.Logger.LogInfo($"[Service] '{e.Vid}' stays waiting for its owner — {why} (a rider does not release the driver).");
                    return;
                }
                pdv.DriveAway();
            }
            else if (e.SavedValid)
            {
                var vc = e.Go.GetComponent<VehicleComponent>();
                if (vc != null) AIEvents.TriggerChangeDrivingStateEvent(vc.GetIndex(), e.SavedAction, e.SavedValue);
                e.SavedValid = false;
            }
            Plugin.Logger.LogInfo($"[Service] '{e.Vid}' resumed — {why}.");
        }

        /// <summary>Host: a rider's stop/resume arrived — handle it if the host owns the car, else forward to the owner.</summary>
        /// <summary>SVC_&lt;owner&gt;_&lt;n&gt; → owner ("" when the id is not in that shape).</summary>
        internal static string OwnerFromServiceId(string? vid)
        {
            if (string.IsNullOrEmpty(vid) || !vid!.StartsWith("SVC_")) return "";
            int last = vid.LastIndexOf('_');
            return last > 4 ? vid.Substring(4, last - 4) : "";
        }
        internal static void HostRoute(MessageType type, ServiceCarPayload p, string senderPid)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.VehicleId)) return;
                if (string.IsNullOrEmpty(p.PlayerId)) p.PlayerId = senderPid ?? "";
                bool stop = type == MessageType.ServiceCarStop;
                // Review MINOR-1: this runs on the network poll thread — no dictionary reads here. The owner is in
                // the id by construction (SVC_<owner>_<n>); everything else happens on the main thread.
                string owner = OwnerFromServiceId(p.VehicleId);
                string vid = p.VehicleId, from = senderPid ?? "";
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    try
                    {
                        if (_local.ContainsKey(vid)) { if (stop) OwnerStop(p); else OwnerResume(p); return; }
                        if (string.IsNullOrEmpty(owner)) owner = VehicleManager.OwnerIdFor(vid);
                        if (string.IsNullOrEmpty(owner)) { Plugin.Logger.LogInfo($"[Service] {type} for unknown car '{vid}' from '{from}' — dropped."); return; }
                        MPServer.SendToPlayer(owner, MessageEnvelope.Create(type, from, p));
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] host route (main): {ex.Message}"); }
                });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] host route: {ex.Message}"); }
        }

        private static void Send(MessageType type, string vid, string owner)
        {
            var p = new ServiceCarPayload { VehicleId = vid, PlayerId = MPConfig.PlayerId };
            if (MPServer.IsRunning) HostRoute(type, p, MPConfig.PlayerId);
            else MPClient.SendEnvelope(MessageEnvelope.Create(type, MPConfig.PlayerId, p));
        }
        internal static void SendStop(string vid, string owner)   => Send(MessageType.ServiceCarStop, vid, owner);
        internal static void SendResume(string vid, string owner) => Send(MessageType.ServiceCarResume, vid, owner);

        // ── rider side: click → stop → walk → the game's destination map ─────────────────────────────
        private static string _rideVid = "", _rideOwner = "";
        private static float  _rideDeadline;
        private static bool   _mapWasOpen;
        private static GhostTaxi? _rideTaxi;

        private static Vector3 _rideGoal, _rideGoalCarPos;   // review MINOR-4: re-aim the walk when the car moves
        internal static void RideFromGhost(string vid, string owner)
        {
            try
            {
                var t = VehicleManager.GhostTransform(vid);
                var pc = Helpers.PlayerHelper.PlayerController;
                if (t == null || pc == null) return;
                var taxi = t.GetComponent<GhostTaxi>();
                if (taxi == null) { Plugin.Logger.LogWarning($"[Service] ghost '{vid}' has no GhostTaxi — ride not offered."); return; }
                if (_rideVid == vid) return;                                        // review #2 MAJOR-3: a repeat click on the same car is a no-op
                if (_rideVid != "" && _rideVid != vid) EndRide("switched cars");   // review MINOR-4: the first car resumes now, not after 60 s
                _rideTaxi = taxi;
                SendStop(vid, owner);
                _rideVid = vid; _rideOwner = owner; _rideDeadline = Time.unscaledTime + 15f; _mapWasOpen = false;
                Vector3 spot = VehicleManager.LoadingSpotFor(vid);
                if (spot == Vector3.zero) spot = t.position + t.right * 2.5f;
                _rideGoal = spot; _rideGoalCarPos = t.position;
                pc.SetGoal(spot, new UnityEngine.Events.UnityAction(() => { }));
                VehicleManager.ClearGhostHighlight(vid);
                Plugin.Logger.LogInfo($"[Service] riding '{vid}' ({owner}'s driver): stop requested, walking over.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] ride: {ex.Message}"); _rideVid = ""; }
        }

        private static void TickRide()
        {
            if (_rideVid == "") return;
            try
            {
                var t = VehicleManager.GhostTransform(_rideVid);
                var ch = Helpers.PlayerHelper.PlayerController?.Character;
                if (t == null || ch == null || _rideTaxi == null) { EndRide("car gone"); return; }
                if (TaxiSystem.IsTraveling) { _rideVid = ""; return; }   // boarded: GhostTaxi.DriveAway already resumed the owner's car
                var cityMap = InstanceBehavior<CityManager>.Instance?.cityMap;
                bool mapOpen = CityMap.IsOpen && cityMap != null && ReferenceEquals(cityMap.Taxi, _rideTaxi);
                if (mapOpen) { _mapWasOpen = true; return; }
                if (_mapWasOpen) { EndRide("map closed without travelling"); return; }   // cancelled → resume the owner's car
                bool arrived = Vector3.Distance(ch.transform.position, t.position) < 4f;
                // Review MINOR-4: the car may still be rolling while the stop request makes its round trip — re-aim
                // the walk whenever the car has moved more than 2 m from where the goal was set (live read).
                if (!arrived && (t.position - _rideGoalCarPos).sqrMagnitude > 4f)
                {
                    var pc2 = Helpers.PlayerHelper.PlayerController;
                    Vector3 spot2 = VehicleManager.LoadingSpotFor(_rideVid);
                    if (spot2 == Vector3.zero) spot2 = t.position + t.right * 2.5f;
                    _rideGoal = spot2; _rideGoalCarPos = t.position;
                    try { pc2?.SetGoal(spot2, new UnityEngine.Events.UnityAction(() => { })); } catch { }
                }
                if (!arrived && Time.unscaledTime < _rideDeadline) return;
                if (!arrived) { EndRide("could not reach the car"); return; }
                cityMap?.SetTaxiMode(_rideTaxi);   // the game's own taxi map; TravelTo reads cityMap.Taxi
                if (!CityMap.IsOpen) { EndRide("map did not open"); return; }
                _mapWasOpen = true;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] ride tick: {ex.Message}"); EndRide("error"); }
        }

        private static void EndRide(string why)
        {
            if (_rideVid != "") { SendResume(_rideVid, _rideOwner); Plugin.Logger.LogInfo($"[Service] ride of '{_rideVid}' ended — {why}; owner's car resumes."); }
            _rideVid = ""; _rideOwner = ""; _rideTaxi = null; _mapWasOpen = false;
        }

        // ── shove belt (B): clients only ─────────────────────────────────────────────────────────────
        internal static void IgnoreTrafficGhostCollisions()
        {
            try
            {
                if (_local.Count == 0) return;
                var ghostCols = TrafficSync.AllTrafficGhostColliders();
                if (ghostCols.Count == 0) return;
                foreach (var e in _local.Values)
                {
                    if (e.Go == null) continue;
                    foreach (var c in e.Go.GetComponentsInChildren<Collider>(true))
                    {
                        if (c == null) continue;
                        // Client sim (2026-09-02): the car's SENSOR trigger must still see ghosts — IgnoreCollision
                        // mutes trigger events for the pair too — so only the solid colliders are paired (no shove).
                        if (TrafficSync.ClientServiceSimEnabled && c.isTrigger) continue;
                        for (int i = 0; i < ghostCols.Count; i++)
                            if (ghostCols[i] != null) Physics.IgnoreCollision(c, ghostCols[i], true);
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] shove belt: {ex.Message}"); }
        }

        // ── tick ────────────────────────────────────────────────────────────────────────────────────
        // Review #2 MINOR-1: true from a friend-ride boarding (GhostTaxi.DriveAway, before the map closes) until the
        // travel coroutine has ended; read by the arrival guard. Frame-stamped so the same-frame tick cannot clear it
        // before TaxiSystem has stored its coroutine.
        internal static bool FriendRideActive { get; private set; }
        private static int _friendRideFrame;
        internal static void MarkFriendRideBoarded() { FriendRideActive = true; _friendRideFrame = Time.frameCount; }

        internal static void Tick()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.InMpGame) { if (_local.Count > 0) _local.Clear(); _rideVid = ""; FriendRideActive = false; return; }
                if (FriendRideActive && !TaxiSystem.IsTraveling && Time.frameCount > _friendRideFrame + 1) FriendRideActive = false;
                float now = Time.unscaledTime;
                if (now >= _nextPrune) { _nextPrune = now + 1f; if (_local.Count > 0) Prune("pool recycled"); }
                if (now >= _nextClientPrune) { _nextClientPrune = now + 1f; if (!MPServer.IsRunning) PruneParkedClientCars(now); }
                foreach (var e in _local.Values)
                    if (e.StopUntil > 0f && now >= e.StopUntil) Resume(e, "60 s stop expired");
                TickRide();
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] tick: {ex.Message}"); }
        }
    }

    /// <summary>Marks every mod ghost body (traffic ghost, service look-alike, fleet ghost) so native layer-keyed
    /// triggers can ignore it (review MAJOR-1(b), 2026-09-02; MPPatches.IsModGhostContact).</summary>
    public sealed class ModGhostMarker : MonoBehaviour { }

    /// <summary>The ride hook on a mirrored service ghost: the game's own ITaxi contract, minus the fare.
    /// DriveAway (fired natively at boarding) resumes the owner's car; arrival loads a plain pool car of the same
    /// model at the rider's destination and registers it as the rider's own service car (mirrored, drives off).</summary>
    public sealed class GhostTaxi : MonoBehaviour, Vehicles.Taxis.ITaxi
    {
        public string Vid = "", OwnerId = "", TypeName = "";

        public void DriveAway() { try { ServiceCars.MarkFriendRideBoarded(); ServiceCars.SendResume(Vid, OwnerId); } catch { } }

        public VehicleComponent GetVehiclePrefab()
        {
            try
            {
                var vt = Vehicles.VehicleTypes.VehicleTypeHelper.GetVehicleType(TypeName);
                var ai = vt != null ? Helpers.PrivateDriverHelpers.GetAiVehiclePrefab(vt) : null;
                if (ai != null) return ai;
            }
            catch { }
            try { var taxi = TrafficSync.PooledPrefab("Taxi"); return taxi != null ? taxi.GetComponent<VehicleComponent>() : null!; } catch { return null!; }
        }

        public VehicleComponent InstantiateVehicle(Waypoint waypoint)
        {
            try
            {
                var prefab = GetVehiclePrefab();
                if (prefab == null || waypoint == null) return null!;
                var vc = TrafficManager.Instance.LoadVehicle(prefab.gameObject, waypoint);
                if (vc == null) { Plugin.Logger.LogInfo($"[Service] arrival car for '{Vid}' not loaded (spot taken or pool empty) — ride still completes."); return null!; }
                ServiceCars.RegisterLocal(vc.gameObject, TypeName, "", "friend's arrival car");
                return vc;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Service] arrival car: {ex.Message}"); return null!; }
        }

        public float GetTimeMultiplier() => 0.85f;
        public void OnTravelFinished() { }
        public string GetHappinessModifierName() => "ba:happinessmodifier_privatedriver";
    }
}
