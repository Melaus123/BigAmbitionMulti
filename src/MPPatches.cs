using System;
using HarmonyLib;
using Buildings;
using Helpers;
using GleyTrafficSystem;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Harmony patches that intercept key game methods.
    /// </summary>
    public static class MPPatches
    {
        /// <summary>
        /// Set to true before calling BuildingHelper.RentBuilding from GameStatePatcher
        /// so the patch knows to allow it through without sending another network request.
        /// </summary>
        public static bool SuppressNextRentRequest = false;

        // Re-entrancy depth counter for the despawner OnTriggerEnter
        // bandaid.  Incremented on Prefix, decremented on Postfix.  Used by
        // Patch_ExitFromBuilding to determine "did the game's natural code
        // already kick the exit while we were inside the despawner trigger?"
        // — if so, the bandaid Postfix won't double-kick.
        internal static int _despawnerDepth = 0;

        // ════════════════════════════════════════════════════════════════════
        // BANDAID REGISTRY (post-2026-05-21 cleanup)
        // ════════════════════════════════════════════════════════════════════
        // After 21 rounds of investigation the bandaid has shrunk to two
        // small pieces:
        //
        //   1) ExitFromBuilding kick — in Patch_ExitZoneDespawner_OnTriggerEnter_Diag.Postfix.
        //      When MPClient.IsConnected and the despawner silently short-
        //      circuited (a different upstream bug that lives in cpp2il's
        //      CallsUnknownMethods, which we never decompiled), we call
        //      BuildingManager.ExitFromBuilding(exitToZoneId, true) ourselves.
        //      Gated on ExitBandaidKickEnabled (below).
        //
        //   2) AiCarMusic suppression — Patch_AiCarMusic_StartB_NoOpOnClient.
        //      The lambda <Start>b__1_* dereferences something null on the
        //      client and crashes the exit coroutine mid-execution.  Prefix
        //      returns false on client, host runs unchanged.  This is the
        //      surgical root-cause fix.  See "REFERENCE — Key discoveries"
        //      in the context log for details.
        //
        // To disable: set ExitBandaidKickEnabled=false (returns original
        // can't-exit bug) and/or remove the AiCarMusic patch class (returns
        // the NRE).  Search marker:  CLAUDE-DIAGNOSTIC[BANDAID]
        // ════════════════════════════════════════════════════════════════════
        public static bool ExitBandaidKickEnabled = true;

        // CLAUDE-DIAGNOSTIC — bandaid fix tracking.  Set to true in
        // Patch_ExitFromBuilding Prefix when _despawnerDepth > 0.  If after
        // the despawner Postfix this is still false on the client, the
        // despawner silently short-circuited and the Postfix kicks the exit
        // manually.  Diagnostic logs from rounds 1-6 narrowed the bug to a
        // check we can't intercept via Harmony — the bandaid bypasses it.
        internal static bool _exitFromBuildingSeenDuringDespawn = false;
        // Snapshot of the active despawner instance for the Postfix's manual
        // ExitFromBuilding call (it needs exitToZoneId).
        internal static object? _currentDespawnerInstance = null;
        // De-double the bandaid.  Despawner trigger fires twice in rapid
        // succession (once per Player collider, once per PlayerTriggerCollider)
        // — two parallel ExitFromBuildingCoroutine state machines will trip
        // over each other and the visual teleport never completes.  Track the
        // realtime of the last successful bandaid kick and reject duplicates
        // within a 3s cooldown.
        internal static float _lastBandaidKickTime = -100f;

        // ── Diagnostic: Application.Quit ─────────────────────────────────────
        // Static method — reliably patchable on IL2CPP.
        // Logs a stack trace so we can see what triggers the second instance
        // to close unexpectedly.
        // TODO: remove once the second-instance shutdown cause is confirmed.


        // ── Patch: BuildingHelper.RentBuilding ────────────────────────────────

        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.RentBuilding))]
        [HarmonyPrefix]
        public static bool Prefix_RentBuilding(Building building, float dailyRent, float lastDeposit)
        {
            // Not in multiplayer session — let it run normally
            if (!MPServer.IsRunning && !MPClient.IsConnected)
                return true;

            // This is a server-confirmed execution dispatched by GameStatePatcher — allow it
            if (SuppressNextRentRequest)
            {
                SuppressNextRentRequest = false;
                return true;
            }

            // We're a client — send request to host and block local execution
            if (MPClient.IsConnected)
            {
                var key = GameStateReader.AddressKey(building);
                MPClient.RequestRentBuilding(key, dailyRent, lastDeposit);
                Plugin.Logger.LogInfo($"[Patch] RentBuilding intercepted for client: {key}");
                return false; // Skip original — wait for host confirmation
            }

            // We're the host — let it run locally, then broadcast the result
            // (the result broadcast happens in the Postfix)
            return true;
        }

        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.RentBuilding))]
        [HarmonyPostfix]
        public static void Postfix_RentBuilding(Building building, float dailyRent, float lastDeposit)
        {
            // Only the host needs to broadcast after a local rent
            if (!MPServer.IsRunning) return;
            if (SuppressNextRentRequest) return; // already handled

            var key = GameStateReader.AddressKey(building);
            MPServer.BuildingOwners[key] = "host";

            var payload = new BuildingOwnershipPayload
            {
                AddressKey    = key,
                OwnerPlayerId = "host",
                DailyRent     = dailyRent,
                LastDeposit   = lastDeposit
            };
            // Broadcast to clients so they mark the building as taken
            // (Re-using the existing broadcast path)
            MPServer.BroadcastRentConfirmToClients(key, dailyRent, lastDeposit);
            Plugin.Logger.LogInfo($"[Patch] Host rented {key}, broadcasted to clients.");
        }

        // ── Patch: GameManager.Update ─────────────────────────────────────────
        // Postfix runs AFTER the game's own Update, so TickSuppression overrides
        // any timeScale the game set during its frame — we reliably win the race.

        [HarmonyPatch(typeof(GameManager), "Update")]
        [HarmonyPostfix]
        public static void Postfix_GameManagerUpdate()
        {
            GameStatePatcher.DrainQueue();

            // Second timeScale enforcement point — runs after the game's own Update,
            // so nothing the game does mid-frame can let the world pause or skip.
            // (MPCanvasUI.LateUpdate is the primary point; this backs it up.)
            if (MPServer.IsRunning || MPClient.IsConnected)
            {
                bool frozen = TimeSync.ManualPaused || TimeSync.IsStartupHeld;
                UnityEngine.Time.timeScale = frozen ? 0f : 1f;
            }
        }

        // ── Patch: GameSpeedController.TogglePause ────────────────────────────
        // Dynamic patch — uses TargetMethod() so we don't need the type's namespace
        // at compile time.  Two purposes:
        //   1. Cache the GSC instance in GameStateReader for diagnostic reads.
        //   2. (Future) intercept pause/unpause to use GSC.paused as authoritative
        //      pause-intent signal instead of inferring from timeScale deltas.

        [HarmonyPatch]
        public static class Patch_GSC_TogglePause
        {
            static System.Reflection.MethodBase? TargetMethod()
                => GameStateReader.FindGSCMethod("TogglePause");

            static void Postfix(object __instance)
            {
                GameStateReader.CacheGSCInstance(__instance);

                // Not in a multiplayer session — leave pause handling to the game.
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;

                // TogglePause fires ONLY for the deliberate pause button — menus and
                // benches pause through a different path.  Read the resulting absolute
                // pause state and share it so the world pauses/resumes for everyone.
                bool paused = GameStateReader.GetGSCPaused();
                TimeSync.SetManualPause(paused);
                if (MPServer.IsRunning)        MPServer.BroadcastManualPause(paused);
                else if (MPClient.IsConnected) MPClient.SendManualPause(paused);
                Plugin.Logger.LogInfo($"[Patch] TogglePause → manual pause = {paused}");
            }
        }

        // ── Patch: GameSpeedController.Set(GameSpeed) ─────────────────────────
        // Set() is the general "change to this speed" entry point — called by sleep,
        // bench rest skip, speed buttons, and anything else that changes game speed.
        // Patching it ensures we cache the GSC instance as soon as ANY speed change
        // fires, not just when the pause button is clicked.

        [HarmonyPatch]
        public static class Patch_GSC_Set
        {
            static System.Reflection.MethodBase? TargetMethod()
                => GameStateReader.FindGSCMethod("Set");

            static void Postfix(object __instance)
            {
                GameStateReader.CacheGSCInstance(__instance);
                // Log only when isFastForwarding is true — avoids spam on normal speed changes
                if (GameStateReader.GetGSCIsFastForwarding())
                    Plugin.Logger.LogInfo(
                        $"[Patch] GSC.Set fired with isFastForwarding=true — GSC: {GameStateReader.GetGSCDiagnostic()}");
            }
        }

        // ── Patch: Animator.SetTrigger ────────────────────────────────────────
        // Triggers are momentary (fire-and-forget) so they can't be polled like
        // float/bool params.  This catches them at the source.  It fires for
        // EVERY animator in the game (all NPCs) — the instance check against the
        // local player's animator keeps the body free for everything else.

        [HarmonyPatch(typeof(UnityEngine.Animator), nameof(UnityEngine.Animator.SetTrigger), typeof(string))]
        [HarmonyPostfix]
        public static void Postfix_Animator_SetTrigger_Name(UnityEngine.Animator __instance, string __0)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (__instance != RemotePlayerManager.GetLocalAnimator()) return;
                RemotePlayerManager.SendLocalTrigger(RemotePlayerManager.ResolveTriggerIndex(__0));
            }
            catch { /* never let an animator trigger break the game */ }
        }

        [HarmonyPatch(typeof(UnityEngine.Animator), nameof(UnityEngine.Animator.SetTrigger), typeof(int))]
        [HarmonyPostfix]
        public static void Postfix_Animator_SetTrigger_Hash(UnityEngine.Animator __instance, int __0)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (__instance != RemotePlayerManager.GetLocalAnimator()) return;
                RemotePlayerManager.SendLocalTrigger(RemotePlayerManager.ResolveTriggerIndex(__0));
            }
            catch { /* never let an animator trigger break the game */ }
        }

        // ── Patch: TaxiController.OnClickToUseTaxi ────────────────────────────
        // Fires when a player hails a taxi.  On a client we forward it to the
        // host so the host stops its real taxi — the ghost then stops too, so
        // the taxi is reachable (SP behaviour).  Dynamic patch — TaxiController's
        // namespace isn't needed at compile time.

        [HarmonyPatch]
        public static class Patch_TaxiController_OnClick
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("TaxiController");
                return t?.GetMethod("OnClickToUseTaxi",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            }

            static void Postfix(UnityEngine.Component __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                    if (__instance != null)
                        TrafficSync.OnLocalTaxiHailed(__instance.gameObject);
                }
                catch { /* never let a taxi click break the game */ }
            }
        }

        // ── Patch: *.TaxiTravel (backlog #5) ──────────────────────────────────
        // Fires when the player picks a destination from the taxi menu and the
        // ride begins.  The game then sets isFastForwarding=true and advances
        // the world clock through the trip — which our world-clock pinner
        // normally reverts every frame, locking the player in the cab.
        //
        // IMPORTANT: previous version used TargetMethod() returning null when
        // TaxiTravel wasn't on TaxiController — Harmony treats null as an
        // error and aborts PatchAll, which then silently drops EVERY other
        // patch in the assembly (including building entry/exit).  Use
        // TargetMethods (plural) with FindAllMethodsByName so a missing
        // method just produces an empty enumerable and the rest of PatchAll
        // continues unaffected.

        [HarmonyPatch]
        public static class Patch_TaxiTravel
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("TaxiTravel");

            static void Prefix()
            {
                try { TrafficSync.OnTaxiTravelStart(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] TaxiTravel prefix: {ex.Message}"); }
            }

            // OBSERVED 2026-05-19: TaxiTravel lives on UI.InGameUI.BuildingResume
            // and there is no separate CompletedTaxiRide method anywhere in the
            // loaded assemblies (log: "[FindMethod] 'CompletedTaxiRide' not
            // found").  TaxiTravel is the ride coroutine itself — postfix fires
            // when the ride completes, which is our "ride ended" event.
            static void Postfix()
            {
                try { TrafficSync.OnTaxiTravelEnd(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] TaxiTravel postfix: {ex.Message}"); }
            }
        }

        // ── Patches: building entry / exit (backlog #6 + #7) ───────────────────
        // Diagnostic logs + flip TrafficSync.LocalInBuilding so the traffic
        // anchor logic can drop the host's anchor while they're inside (#7).
        //
        // Method names confirmed present in BigAmbitions.dll:
        //   EnterBuildingCoroutine, EnteredBuilding, ExitFromBuilding,
        //   ExitFromBuildingCoroutine.
        // These exist on multiple controller types (CityBuildingController,
        // CasinoBuildingController, etc.) so we patch ALL methods with each
        // name across all assemblies via FindAllMethodsByName.

        // Building entry / exit — flips TrafficSync.LocalInBuilding so anchor
        // logic and other in-building behaviour can hook off it.  The actual
        // method that fires on foot entry is BuildingManager.DelayedEnterBuildingActions
        // (confirmed by observation 2026-05-19 — EnterBuildingCoroutine is
        // patched but never fires for this path).  EnterBuildingWithVehicle /
        // EnterParking are defensive secondary handlers for the vehicle and
        // parking-entry paths.

        [HarmonyPatch]
        public static class Patch_DelayedEnterBuilding
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("DelayedEnterBuildingActions");

            static void Prefix(BuildingManager __instance)
            {
                try { TrafficSync.OnEnteredBuilding("DelayedEnterBuildingActions"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] DelayedEnterBuilding prefix: {ex.Message}"); }

                // Interior sync (Phase 2): tell the host we've entered so we
                // can subscribe to its authoritative interior state.  Host-side
                // path is symmetric: the host enters its own buildings without
                // sending anything (it IS the source of truth).
                try
                {
                    if (!MPClient.IsConnected) return;
                    if (__instance == null) return;
                    var reg = __instance.buildingRegistration;
                    if (reg == null) return;
                    var addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr)) return;
                    MPClient.SendInteriorRequest(addr);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] DelayedEnterBuilding interior-req: {ex.Message}"); }
            }
        }

        // ── Patch: BuildingManager.ExitFromBuilding (Phase 2 unsubscribe) ─────
        // Fires when the player leaves a building (either on foot via the exit
        // zone or via "exit to street").  We capture the registration BEFORE
        // the method runs so the address is still valid, then send
        // PlayerExitedBuilding to the host so it drops us from the building's
        // subscriber set.
        [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.ExitFromBuilding))]
        public static class Patch_ExitFromBuilding_InteriorSync
        {
            static void Prefix(BuildingManager __instance)
            {
                try
                {
                    if (!MPClient.IsConnected) return;
                    if (__instance == null) return;
                    var reg = __instance.buildingRegistration;
                    if (reg == null) return;
                    var addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr)) return;
                    MPClient.SendPlayerExitedBuilding(addr);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] ExitFromBuilding_InteriorSync prefix: {ex.Message}"); }
            }
        }
        // ── Backlog #7 traffic-kill blockers ─────────────────────────────────
        // Gley's TrafficManager exposes ClearTraffic / ClearTrafficOnArea /
        // SetPause.  On building entry the game calls SetPause(true) +
        // ClearTraffic, which wipes the world's cars for everyone — including
        // remote clients still standing outside.  In MP we want traffic
        // persistent across the host's interior visits, so we no-op these
        // whenever the host is MP-active.  SetPause(false) (unpause) still
        // passes through.

        [HarmonyPatch(typeof(TrafficManager), nameof(TrafficManager.ClearTraffic))]
        public static class Patch_TM_ClearTraffic
        {
            static bool Prefix()
            {
                // CLAUDE-DIAGNOSTIC — symmetrize on host AND client.  Path X test:
                // if blocking on client also lets DelayedEnterBuildingActions fire,
                // ClearTraffic was the gate; if not, this was a dead end.
                if (MPServer.IsRunning || MPClient.IsConnected)
                {
                    string side = MPServer.IsRunning ? "host" : "client";
                    Plugin.Logger.LogInfo($"[TMBlock] ClearTraffic() — SKIPPED ({side} MP active).");
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(TrafficManager), nameof(TrafficManager.ClearTrafficOnArea))]
        public static class Patch_TM_ClearTrafficOnArea
        {
            static bool Prefix(UnityEngine.Vector3 __0, float __1)
            {
                // CLAUDE-DIAGNOSTIC — same Path X symmetrize.
                if (MPServer.IsRunning || MPClient.IsConnected)
                {
                    string side = MPServer.IsRunning ? "host" : "client";
                    Plugin.Logger.LogInfo($"[TMBlock] ClearTrafficOnArea(pos={__0}, r={__1}) — SKIPPED ({side} MP active).");
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(TrafficManager), nameof(TrafficManager.SetPause))]
        public static class Patch_TM_SetPause
        {
            static bool Prefix(bool __0)
            {
                // CLAUDE-DIAGNOSTIC — first-domino test: symmetrize on host AND
                // client.  Observed sequence on host:
                //   SetWorkingState → SetPause(true) [BLOCKED] → DelayedEnterBuildingActions
                // On client SetPause(true) wasn't blocked and DelayedEnterBuildingActions
                // never fired.  Testing the hypothesis that SetPause(true) side effects
                // are what prevents the entry chain from progressing.
                if ((MPServer.IsRunning || MPClient.IsConnected) && __0)
                {
                    string side = MPServer.IsRunning ? "host" : "client";
                    Plugin.Logger.LogInfo($"[TMBlock] SetPause(true) — SKIPPED ({side} MP active).");
                    return false;
                }
                return true;
            }
        }

        // ── Patch: GameManager.ClickSleep ─────────────────────────────────────
        // ClickSleep is the public handler for the in-game "sleep" button.
        // We allow the sleep to START (character enters bed) but suppress the
        // fast-forward until all players have also clicked sleep.

        [HarmonyPatch(typeof(GameManager), "ClickSleep")]
        [HarmonyPrefix]
        public static bool Prefix_ClickSleep()
        {
            // In the multiplayer time model there are no time skips.  Sleeping is
            // still allowed (the character can lie down) but the fast-forward is
            // rejected by the world-clock monitor.  Nothing to intercept here.
            return true;
        }

        // ── Backlog #3 — parked-vehicle sync ──────────────────────────────────
        // Helpers.ParkingSimulator is the static pool for all world parked
        // vehicles.  RequestParkedVehicle / ReleaseParkedVehicle are the only
        // entry/exit points and they're static — Postfixes here capture every
        // spawn/release across the entire game.  On the host these feed
        // ParkedVehicleSync's tracked set; the client's own client-driven
        // ghost Requests/Releases are filtered out by the MPServer.IsRunning
        // check.

        [HarmonyPatch(typeof(Helpers.ParkingSimulator), nameof(Helpers.ParkingSimulator.RequestParkedVehicle))]
        public static class Patch_ParkingSim_Request
        {
            static void Postfix(string __0, UnityEngine.GameObject __result)
            {
                try
                {
                    if (!MPServer.IsRunning) return;
                    if (__result == null) return;
                    ParkedVehicleSync.HostOnRequest(__result, __0);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] RequestParkedVehicle postfix: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Helpers.ParkingSimulator), nameof(Helpers.ParkingSimulator.ReleaseParkedVehicle))]
        public static class Patch_ParkingSim_Release
        {
            static void Prefix(UnityEngine.GameObject __0)
            {
                try
                {
                    if (!MPServer.IsRunning) return;
                    if (__0 == null) return;
                    ParkedVehicleSync.HostOnRelease(__0);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] ReleaseParkedVehicle prefix: {ex.Message}"); }
            }
        }

        // Client suppresses its own ParkingLaneGenerator.GenerateParkedVehicles
        // so the local RNG can't create cars that conflict with host snapshots.
        // The host snapshot is the only source of parked cars while connected.
        //
        // OBSERVED 2026-05-19: the previous TargetMethod() using FindGameType
        // returned null and aborted the patch class (1 of "2 failed" in the
        // patch summary).  Result: client kept generating its own cars on top
        // of host ghosts → 2 cars per spot, visible overlap.  Switch to the
        // TargetMethods + FindAllMethodsByName pattern (same as building
        // patches) — survives a name/namespace mismatch by yielding nothing
        // rather than throwing, and catches every overload across assemblies.
        [HarmonyPatch]
        public static class Patch_GenerateParkedVehicles
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("GenerateParkedVehicles");

            static bool Prefix()
            {
                // CLAUDE-DIAGNOSTIC — gated on ParkedVehicleSync.SpawnSuppressionEnabled
                // so F6 can toggle the suppression at runtime for the entry-bug A/B test.
                if (MPClient.IsConnected && ParkedVehicleSync.SpawnSuppressionEnabled) return false;
                return true;
            }
        }

        // CLAUDE-DIAGNOSTIC — client-can't-exit-building investigation (2026-05-20).
        // Cpp2IL decompile revealed:
        //   * ExitZone.OnTriggerEnter only opens the door (animation) — NOT the
        //     building-exit trigger.  Our earlier exit-trigger probe was watching
        //     the wrong class.
        //   * The REAL exit handler is ExitZoneDespawner.OnTriggerEnter, which
        //     guards on CompareTag / NavigationDisabled / IsInsideMotorVehicle /
        //     BuildingManager.IsInsideBuilding / IsInsideParking / HasPaidForAllItems
        //     / CanLeaveHome before calling StartCoroutine(ExitFromBuildingCoroutine).
        //
        // These two prefixes let us tell whether (a) the client's player collider
        // never enters the despawner trigger volume (collider/layer problem) or
        // (b) it enters but a guard rejects (state-mismatch problem).
        //
        // Tag/layer fetch is wrapped in try/catch because IL2CPP wrappers can
        // throw NullRef on transient destroyed objects.
        [HarmonyPatch]
        public static class Patch_ExitZoneDespawner_OnTriggerEnter_Diag
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("ExitZoneDespawner");
                if (t == null) yield break;
                var bf = System.Reflection.BindingFlags.Instance
                       | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Public
                       | System.Reflection.BindingFlags.DeclaredOnly;
                var m = t.GetMethod("OnTriggerEnter", bf);
                if (m != null) yield return m;
            }

            static void Prefix(object __instance, UnityEngine.Collider other)
            {
                try
                {
                    _despawnerDepth++;
                    // Reset bandaid tracking for THIS trigger fire and snapshot
                    // the instance so the Postfix can call ExitFromBuilding
                    // with its exitToZoneId field.
                    _exitFromBuildingSeenDuringDespawn = false;
                    _currentDespawnerInstance = __instance;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] ExitDespawner OnTriggerEnter prefix: {ex.Message}"); }
            }

            static void Postfix()
            {
                try
                {
                    if (!ExitBandaidKickEnabled) return;
                    if (!MPClient.IsConnected)   return;
                    if (_exitFromBuildingSeenDuringDespawn) return;   // game already kicked it
                    if (_currentDespawnerInstance == null)  return;

                    var di = _currentDespawnerInstance;
                    var dt = di.GetType();
                    bool casino = ReadBool(dt, di, "isCasinoExit");
                    bool parking = ReadBool(dt, di, "isParkingExit");
                    int exitToZoneId = ReadInt(dt, di, "exitToZoneId");
                    if (casino || parking) return;   // game's casino/parking paths still work fine

                    // De-double: the despawner trigger fires twice in a row
                    // (Player collider + PlayerTriggerCollider).  Two parallel
                    // ExitFromBuildingCoroutine starts garble the visual teleport.
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (now - _lastBandaidKickTime < 3f) return;
                    _lastBandaidKickTime = now;

                    string side = MPServer.IsRunning ? "HOST" : "CLIENT";
                    Plugin.Logger.LogInfo($"[ExitBandaid/{side}] kicking ExitFromBuilding({exitToZoneId})");

                    var bmType = VehicleManager.FindGameType("BuildingManager");
                    if (bmType == null) return;
                    var bmInst = GetBuildingManagerInstance(bmType);
                    if (bmInst == null) { Plugin.Logger.LogWarning($"[ExitBandaid/{side}] BuildingManager singleton null."); return; }
                    var exitMethod = bmType.GetMethod("ExitFromBuilding",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (exitMethod == null) { Plugin.Logger.LogWarning($"[ExitBandaid/{side}] ExitFromBuilding not found."); return; }
                    exitMethod.Invoke(bmInst, new object[] { exitToZoneId, true });
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[ExitBandaid] Postfix: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _currentDespawnerInstance = null;
                    _despawnerDepth--;
                    if (_despawnerDepth < 0) _despawnerDepth = 0;
                }
            }

            // Helpers — small reflection wrappers used by the bandaid Postfix.
            private static bool ReadBool(Type t, object inst, string field)
            {
                try
                {
                    var f = t.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    return f != null && (bool)(f.GetValue(inst) ?? false);
                }
                catch { return false; }
            }
            private static int ReadInt(Type t, object inst, string field)
            {
                try
                {
                    var f = t.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    return f == null ? -1 : (int)(f.GetValue(inst) ?? -1);
                }
                catch { return -1; }
            }
            private static object? GetBuildingManagerInstance(Type bmType)
            {
                try
                {
                    // Walk BaseType chain to InstanceBehavior<BuildingManager>.GetInstance(bool).
                    var bt = bmType.BaseType;
                    while (bt != null)
                    {
                        var get = bt.GetMethod("GetInstance",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (get != null)
                        {
                            try { return get.Invoke(null, new object[] { true }); }
                            catch { try { return get.Invoke(null, new object[] { false }); } catch { return null; } }
                        }
                        bt = bt.BaseType;
                    }
                }
                catch { }
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // CLAUDE-DIAGNOSTIC[BANDAID] — Round 20: REAL ROOT-CAUSE FIX.
        // ════════════════════════════════════════════════════════════════
        // The IL2CPP NRE stack trace finally showed two frames:
        //   at AiCarMusic.<Start>b__1_1 (Address _) [0x00000]
        //   at BuildingManager+<ExitFromBuildingCoroutine>d__87.MoveNext ()
        //
        // AiCarMusic is the NPC-car-music subsystem.  In Start() it registers
        // two lambdas (<Start>b__1_0 and <Start>b__1_1) on a building-Address
        // event (per cpp2il's Delegate.Combine call).  The exit coroutine
        // fires that event during shutdown of the building scene.  The
        // _1_1 lambda calls DoPlay() (per its CalledBy attribute) — DoPlay
        // dereferences something null on the client and throws.
        //
        // Patch_AiCarMusic_StartB_NoOpOnClient prefixes the lambda and
        // skips it entirely when MPClient.IsConnected.  On the host the
        // lambda runs normally so AI-car music keeps working.
        //
        // Effect on the bandaid:
        //   - Coroutine no longer NREs → can reach its terminal state-reset.
        //   - Bandaid's force-reset (set_enteringBuilding/exitingBuilding(false))
        //     is hopefully no longer needed.  Test by toggling
        //     `MPPatches.ExitBandaidStateResetEnabled = false`.
        //   - The kick (manual ExitFromBuilding) is still needed because
        //     the despawner's silent short-circuit is a separate bug we
        //     haven't fixed.
        // ════════════════════════════════════════════════════════════════
        [HarmonyPatch]
        public static class Patch_AiCarMusic_StartB_NoOpOnClient
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("AiCarMusic");
                if (t == null)
                {
                    Plugin.Logger.LogWarning("[Patch] AiCarMusic type not found.");
                    yield break;
                }
                // Compiler-generated lambdas — diffable-cs shows them as
                // `<Start>b__1_0` / `<Start>b__1_1`.  IL2CPP-Interop renames
                // `<>` to `_` in identifier names (verified with d__87 earlier).
                // Search broadly across all flag combinations and log every
                // method we find so we can see what's actually exposed.
                var allFlags = System.Reflection.BindingFlags.Instance
                             | System.Reflection.BindingFlags.Static
                             | System.Reflection.BindingFlags.Public
                             | System.Reflection.BindingFlags.NonPublic
                             | System.Reflection.BindingFlags.DeclaredOnly;
                int hits = 0;
                foreach (var m in t.GetMethods(allFlags))
                {
                    // Match by `b__1` substring — that's the only stable
                    // marker of the compiler-generated lambda block 1.
                    // Avoid matching anything else (DoPlay, Start, etc.).
                    if (m.Name.Contains("b__1"))
                    {
                        Plugin.Logger.LogInfo($"[Patch] AiCarMusic candidate: '{m.Name}' params=[{string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))}]");
                        hits++;
                        yield return m;
                    }
                }
                if (hits == 0)
                {
                    // Dump all methods to help diagnose.
                    Plugin.Logger.LogWarning("[Patch] No AiCarMusic b__1 lambda found.  Methods on type:");
                    foreach (var m in t.GetMethods(allFlags))
                        Plugin.Logger.LogWarning($"[Patch]   {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                }
            }
            // Fires hundreds of times per session (every AI car triggers it).
            // Log only the first suppression + every 500th to keep the log
            // readable.  The first one proves the patch is live.
            private static int _aiCarSuppressCount = 0;
            static bool Prefix(System.Reflection.MethodBase __originalMethod)
            {
                if (!MPClient.IsConnected) return true;   // host: run as normal
                _aiCarSuppressCount++;
                if (_aiCarSuppressCount == 1 || _aiCarSuppressCount % 500 == 0)
                {
                    Plugin.Logger.LogInfo($"[AiCarMusic/CLIENT] {__originalMethod?.Name} suppressed (#{_aiCarSuppressCount}).");
                }
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Business sync helper: force player-business logo lookup on client.
        // ════════════════════════════════════════════════════════════════
        // LogoHelper.GetBusinessLogoTexture(name, size, playerBusiness) takes
        // a bool that decides which path it queries: false → Addressables
        // (AI businesses), true → on-disk files (player businesses).
        // UpdateSign passes that bool from reg.RentedByPlayer, but on the
        // client reg.RentedByPlayer is FALSE even for host-owned businesses
        // (RentedByPlayer means "the LOCAL player owns it" — host owns it
        // from their perspective, not from the client's).  So client always
        // queries Addressables, never finds host's customized JPGs, and
        // shows the generic fallback.
        //
        // Fix: a Prefix on GetBusinessLogoTexture that, when MPClient is
        // connected and a player-business directory exists for the requested
        // name, flips the playerBusiness arg to true.  Sign now loads from
        // the JPG files we synced to disk.
        [HarmonyPatch]
        public static class Patch_GetBusinessLogoTexture_ForceClient
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("LogoHelper");
                if (t == null) return null;
                foreach (var m in t.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "GetBusinessLogoTexture") continue;
                    var p = m.GetParameters();
                    if (p.Length != 3) continue;
                    return m;
                }
                return null;
            }

            private static int _flipCount;
            static void Prefix(string __0, object __1, ref bool __2)
            {
                if (!MPClient.IsConnected) return;
                if (__2) return;   // already true
                if (string.IsNullOrEmpty(__0)) return;
                try
                {
                    var dir = LogoHelper.GetPlayerBusinessLogoPath(__0);
                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    {
                        __2 = true;
                        _flipCount++;
                        if (_flipCount <= 3 || _flipCount % 50 == 0)
                            Plugin.Logger.LogInfo($"[GetLogoTex] forced playerBusiness=true for '{__0}' (#{_flipCount})");
                    }
                }
                catch { }
            }
        }

        // ── Suppression: CityGenerator AI/rival business assignment ───────────
        // The client's CityGenerator.InitializeCity runs on game load and assigns
        // AI rivals/businesses to residential, warehouse, retail, and office
        // buildings — using the client's LOCAL random seed.  Even after our
        // BusinessSnapshot writes the host's authoritative state on top, a few
        // residential/warehouse assignments survive (confirmed by the user's
        // screenshot: the "extras" on client match the pre-sync state exactly).
        //
        // Strategy: skip the business-assigning generators on client.  Keep
        // PopulateBuildings (creates the registrations in gi.BuildingRegistrations)
        // and EnsureTutorialBuildingsAreAvailable (just ensures specific addresses
        // are unassigned) since those are needed for the registration skeleton.
        //
        // Each patch is gated on MPClient.IsConnected so single-player and host
        // behavior is unchanged.

        [HarmonyPatch]
        public static class Patch_CityGenerator_SetupResidentialBuildings_SkipOnClient
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("Helpers.CityGenerator")?.GetMethod("SetupResidentialBuildings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix()
            {
                if (!MPClient.IsConnected) return true;
                Plugin.Logger.LogInfo("[Suppress] SetupResidentialBuildings skipped on client.");
                return false;
            }
        }

        [HarmonyPatch]
        public static class Patch_CityGenerator_SetupWarehouseBuildings_SkipOnClient
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("Helpers.CityGenerator")?.GetMethod("SetupWarehouseBuildings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix()
            {
                if (!MPClient.IsConnected) return true;
                Plugin.Logger.LogInfo("[Suppress] SetupWarehouseBuildings skipped on client.");
                return false;
            }
        }

        [HarmonyPatch]
        public static class Patch_CityGenerator_DistributeBuildingsToRivals_SkipOnClient
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("Helpers.CityGenerator")?.GetMethod("DistributeBuildingsToRivals",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix()
            {
                if (!MPClient.IsConnected) return true;
                Plugin.Logger.LogInfo("[Suppress] DistributeBuildingsToRivals skipped on client.");
                return false;
            }
        }

        [HarmonyPatch]
        public static class Patch_CityGenerator_SetupRivalFactories_SkipOnClient
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("Helpers.CityGenerator")?.GetMethod("SetupRivalFactories",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix()
            {
                if (!MPClient.IsConnected) return true;
                Plugin.Logger.LogInfo("[Suppress] SetupRivalFactories skipped on client.");
                return false;
            }
        }

        [HarmonyPatch]
        public static class Patch_CityGenerator_SetRivalBuildings_SkipOnClient
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("Helpers.CityGenerator")?.GetMethod("SetRivalBuildings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix()
            {
                if (!MPClient.IsConnected) return true;
                Plugin.Logger.LogInfo("[Suppress] SetRivalBuildings skipped on client.");
                return false;
            }
        }

        // ── Suppression: RealEstateHelper.RunDaily (buy marketplace) ──────────
        // RealEstateHelper.RunDaily is the orchestrator for the daily real-estate
        // tick: UpdateBuildingsForSale (picks ~3 new buildings per neighborhood
        // for the buy marketplace), UpdatePlayerRealEstate, and the two
        // SimulateCompetitorBuying* methods.  All four make RNG-based choices —
        // running them on the client diverges from host's choices.
        //
        // Suppressing the whole RunDaily on client means: client never mutates
        // gi.buildingsForSale or gi.realEstate locally; host's snapshot is the
        // sole authority.  BusinessSync.CheckBuildingsForSaleChange polls the
        // host's list each Tick and re-broadcasts on change.
        [HarmonyPatch]
        public static class Patch_RealEstateHelper_RunDaily_SkipOnClient
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("Helpers.RealEstateHelper")?.GetMethod("RunDaily",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix()
            {
                if (!MPClient.IsConnected) return true;
                Plugin.Logger.LogInfo("[Suppress] RealEstateHelper.RunDaily skipped on client.");
                return false;
            }
        }

        // ── Diagnostic: CityMapFilters.ApplyFilters ───────────────────────────
        // The map's "for rent" highlight discrepancy investigation.  Our snapshot
        // apply runs once at sync time and our diagnostic shows host/client state
        // identical at that moment.  But the user reports a visual difference
        // when they later open the map.  This patch fires our diagnostic at the
        // EXACT moment the filter computes highlights — so we capture the state
        // the filter algorithm actually sees, eliminating any timing ambiguity.
        [HarmonyPatch]
        public static class Patch_CityMapFilters_ApplyFilters_Diag
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("CityMapFilters");
                if (t == null) return null;
                return t.GetMethod("ApplyFilters",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            }

            // Throttle: ApplyFilters fires once per toggle click, but the user
            // may toggle several quickly.  Cap to one dump per 2s so we don't
            // spam the log.
            private static float _lastDumpAt = -100f;
            static void Prefix()
            {
                try
                {
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (now - _lastDumpAt < 2f) return;
                    _lastDumpAt = now;

                    string role = MPServer.IsRunning ? "HOST" : (MPClient.IsConnected ? "CLIENT" : "SP");
                    Plugin.Logger.LogInfo($"[Patch_ApplyFilters] === ApplyFilters about to run on {role} ===");

                    var gi = SaveGameManager.Current;
                    if (gi == null) return;

                    var stats = new BusinessSync.TypeStats();
                    int total = 0;
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        total++;
                        stats.AccumulateFromReg(reg);
                    }
                    stats.Log($"ApplyFilters.{role}", total);
                    BusinessSync.LogForSaleAndRealEstate($"ApplyFilters.{role}", gi);
                    BusinessSync.LogSceneCBCCounts($"ApplyFilters.{role}");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_ApplyFilters] {ex.Message}"); }
            }
        }

    }
}
