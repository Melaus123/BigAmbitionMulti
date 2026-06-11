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

        // ════════════════════════════════════════════════════════════════════
        // EXIT BANDAID — RETIRED 2026-06-10
        // ════════════════════════════════════════════════════════════════════
        // History: on clients, ExitZoneDespawner.OnTriggerEnter silently
        // short-circuited and a Harmony Postfix manually kicked
        // BuildingManager.ExitFromBuilding (21 investigation rounds, 2026-05).
        // Root cause turned out to be the client-side Player-layer colliders
        // on remote ghosts (the despawner's player-identification check saw
        // the wrong collider).  Those colliders were later removed (ghosts
        // are visual-only on clients) — and a live test with the kick
        // disabled confirmed native exit works again.  The kick, its F3
        // toggle, and all tracking state were deleted; building enter/exit
        // is fully vanilla now.
        //
        // Still present (real root-cause fix, NOT a bandaid):
        //   Patch_AiCarMusic_StartB_NoOpOnClient — an AiCarMusic lambda NREs
        //   on clients mid-exit-coroutine; prefix skips it client-side only.
        // ════════════════════════════════════════════════════════════════════

        // ── Diagnostic: Application.Quit ─────────────────────────────────────
        // Static method — reliably patchable on IL2CPP.
        // Logs a stack trace so we can see what triggers the second instance
        // to close unexpectedly.
        // TODO: remove once the second-instance shutdown cause is confirmed.


        // ── Patch: BuildingHelper.RentBuilding ────────────────────────────────
        // NESTED [HarmonyPatch] class — REQUIRED.  Plugin.cs only applies patch
        // classes that carry a CLASS-level [HarmonyPatch] (it skips method-level
        // patches on the bare MPPatches class).  This was previously written as
        // method-level patches on MPPatches → they were silently NEVER applied,
        // so a client's rent was never sent to the host (the bug being fixed).

        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.RentBuilding))]
        public static class Patch_RentBuilding
        {
            static bool Prefix(Building building, float dailyRent, float lastDeposit)
            {
                // Not in multiplayer session — let it run normally
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;

                // Server-confirmed execution dispatched by GameStatePatcher — allow it.
                if (SuppressNextRentRequest) { SuppressNextRentRequest = false; return true; }

                // Client — rent LOCALLY (so the UI flows to the start-business /
                // terminate-contract window and the client actually owns it), AND
                // notify the host so it records ownership + tells the other players.
                // (Optimistic: blocking the rent broke the in-game UI; on the common
                // no-conflict path local + host agree.  Host broadcasts the confirm to
                // OTHERS only, so we never double-rent ourselves.)
                if (MPClient.IsConnected)
                {
                    var key = GameStateReader.AddressKey(building);
                    MPClient.RequestRentBuilding(key, dailyRent, lastDeposit);
                    Plugin.Logger.LogInfo($"[Patch] Client renting {key} locally + notifying host.");
                    return true;
                }

                // Host — let it rent locally; the Postfix broadcasts the result.
                return true;
            }

            static void Postfix(Building building, float dailyRent, float lastDeposit)
            {
                if (!MPServer.IsRunning) return;
                if (SuppressNextRentRequest) return;   // already handled

                var key = GameStateReader.AddressKey(building);
                MPServer.BuildingOwners[key] = "host";
                MPServer.BroadcastRentConfirmToClients(key, dailyRent, lastDeposit);
                Plugin.Logger.LogInfo($"[Patch] Host rented {key}, broadcasted to clients.");
            }
        }

        // ── Patch: GameManager.Update ─────────────────────────────────────────
        // Postfix runs AFTER the game's own Update, so TickSuppression overrides
        // any timeScale the game set during its frame — we reliably win the race.
        // NESTED class — REQUIRED so Plugin.cs actually applies it (see the rent
        // patch note above; method-level patches on bare MPPatches are skipped).
        [HarmonyPatch(typeof(GameManager), "Update")]
        public static class Patch_GameManager_Update
        {
            static void Postfix()
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

            // SHIELD (MP only): GameManager.Update threw repeatedly during a
            // mid-join fresh start (intro loaded from a running world — a
            // transition the game never does natively) and the exception storm
            // stuck the loading screen (2026-06-11).  Log + swallow.
            private static int _swallowed;
            static Exception? Finalizer(Exception? __exception)
            {
                if (__exception == null) return null;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return __exception;
                _swallowed++;
                if (_swallowed <= 5 || _swallowed % 300 == 0)
                    Plugin.Logger.LogWarning($"[GMShield] swallowed {__exception.GetType().Name} in GameManager.Update (#{_swallowed}): {__exception.Message}");
                return null;
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

                // OUR OWN TogglePause invokes (SetNativePause / watchdog) are NOT
                // player pause presses — without this guard they re-enter the
                // manual-pause vote, which re-invokes TogglePause: the infinite
                // pause tug-of-war that froze the host (2026-06-10).
                if (GameStateReader.AllowNativePauseCall) return;

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

        // NESTED classes — REQUIRED so Plugin.cs applies them (method-level patches on
        // bare MPPatches are skipped — see the rent patch note).  These were silently
        // dead, so remote players' one-off action animations never synced.
        [HarmonyPatch(typeof(UnityEngine.Animator), nameof(UnityEngine.Animator.SetTrigger), typeof(string))]
        public static class Patch_Animator_SetTrigger_Name
        {
            static void Postfix(UnityEngine.Animator __instance, string __0)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                    if (__instance != RemotePlayerManager.GetLocalAnimator()) return;
                    RemotePlayerManager.SendLocalTrigger(RemotePlayerManager.ResolveTriggerIndex(__0));
                }
                catch { /* never let an animator trigger break the game */ }
            }
        }

        [HarmonyPatch(typeof(UnityEngine.Animator), nameof(UnityEngine.Animator.SetTrigger), typeof(int))]
        public static class Patch_Animator_SetTrigger_Hash
        {
            static void Postfix(UnityEngine.Animator __instance, int __0)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                    if (__instance != RemotePlayerManager.GetLocalAnimator()) return;
                    RemotePlayerManager.SendLocalTrigger(RemotePlayerManager.ResolveTriggerIndex(__0));
                }
                catch { /* never let an animator trigger break the game */ }
            }
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
        // (2026-06-10: the companion exit-kick bandaid was RETIRED — its root
        //  cause, client-side Player-layer ghost colliders, is long removed
        //  and vanilla exit was verified working with the kick disabled.
        //  This patch remains the only intervention in the exit chain.)
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
        // UpdateSign passes that bool from reg.RentedByPlayer — which is only
        // true when the LOCAL player owns the business.  For a business owned
        // by the OTHER player it's FALSE on BOTH sides: the client sees a
        // host-owned business as not-player, and the host sees a client-owned
        // business the same way.  Either side then queries Addressables, never
        // finds the synced JPGs, and renders the generic "?" sign.
        //
        // Fix: a Prefix on GetBusinessLogoTexture that — whenever we're in MP
        // (host OR client) and a player-business logo directory exists for the
        // requested name — flips the playerBusiness arg to true, so the sign
        // loads from the JPG files we synced to disk.  AI businesses (no logo
        // dir) are untouched; a local player business already passes true.
        // (Class name says "ForceClient" for history; it now serves the host too.)
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
                if (!MPClient.IsConnected && !MPServer.IsRunning) return;   // MP only — host OR client
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

        // ── Rivals leaderboard hooks (Phase 1d Wave 4: on-demand stats sync) ──

        /// <summary>
        /// Prefix on RivalLeaderboard.Load: when the user opens the rivals
        /// app on their phone, fire off a request for fresh stats from host.
        /// Original Load() runs immediately with whatever's in our cache
        /// (which may be empty on first open or stale on later opens).  When
        /// the snapshot arrives a few frames later, ApplyRivalsStatsSnapshot
        /// re-invokes Load() so the cache-populated values render.
        /// </summary>
        [HarmonyPatch]
        public static class Patch_RivalLeaderboard_Load_RequestStats
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("UI.Smartphone.Apps.Rivals.RivalLeaderboard")?.GetMethod("Load",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);

            // Throttle to avoid spamming the host if Load fires multiple times.
            private static float _lastRequestAt = -100f;
            static void Prefix()
            {
                try
                {
                    // Gate the GetAllRivalData append to THIS Load only (set here,
                    // before the body calls GetAllRivalData; cleared in the Load
                    // Postfix).  Fires on either role so the host injects clients
                    // and the client injects the host/other clients.
                    if (!MPClient.IsConnected && !MPServer.IsRunning) return;
                    GameStatePatcher.RivalsLeaderboardLoadRunning = true;
                    // Reset on EVERY Load (before the throttle) so the re-Load
                    // fired when the stats snapshot arrives logs fresh income
                    // samples — otherwise the diag cap is spent on the first
                    // (pre-stats) Load and the override is invisible.
                    Patch_RivalLeaderboard_GetRivalLeaderboardData_FromCache.ResetIncomeDiag();
                    if (!MPClient.IsConnected) return;   // stats request is client→host only
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (now - _lastRequestAt < 1f) return;
                    _lastRequestAt = now;
                    MPClient.SendRivalsStatsRequest();
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RivalLeaderboard_Load_RequestStats] {ex.Message}"); }
            }
        }

        /// <summary>
        /// Postfix on RivalLeaderboard.Load — clears the Load-context gate set
        /// by the Prefix.  Remote-player rows are no longer injected manually;
        /// the game itself creates + income-ranks them because the GetAllRivalData
        /// Postfix appends a synthetic RivalData per remote player (see
        /// Patch_RivalsHelper_GetAllRivalData).  This Postfix exists only to
        /// close the gate so GetAllRivalData's other callers don't see players.
        /// </summary>
        [HarmonyPatch]
        public static class Patch_RivalLeaderboard_Load_AddPlayers
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("UI.Smartphone.Apps.Rivals.RivalLeaderboard")?.GetMethod("Load",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);

            static void Postfix()
            {
                GameStatePatcher.RivalsLeaderboardLoadRunning = false;
            }
        }

        /// <summary>
        /// Postfix on RivalLeaderboard.GetRivalLeaderboardData(RivalData):
        /// after the game builds the row data locally (using its own — likely
        /// stale — RivalData), override the scalar fields with host's values
        /// from our cache.  Skips the building-list fields (ownedBusinesses /
        /// ownedBuildings) since those need real BuildingRegistration refs
        /// that we don't have on this side; their Count getter will still
        /// reflect whatever the local game has (better than nulled lists).
        /// </summary>
        [HarmonyPatch]
        public static class Patch_RivalLeaderboard_GetRivalLeaderboardData_FromCache
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("UI.Smartphone.Apps.Rivals.RivalLeaderboard");
                if (t == null) return null;
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "GetRivalLeaderboardData") continue;
                    var p = m.GetParameters();
                    if (p.Length != 1) continue;
                    return m;
                }
                return null;
            }

            private static int _diagSample = 0;
            /// <summary>Reset the income-diag sample cap so the next Load logs a
            /// fresh batch.  Called from the Load Prefix — without this, the
            /// static counter is exhausted on the FIRST Load (before the stats
            /// snapshot arrives) and we never see the post-stats override.</summary>
            private static int _hostCountDiag = 0;
            public static void ResetIncomeDiag() { _diagSample = 0; _hostCountDiag = 0; }
            static void Postfix(object __0, object __result)
            {
                try
                {
                    // First: capture rd refs on EITHER side.  Host uses these
                    // when building stats snapshots; client uses them for the
                    // populate-and-recompute path below.
                    var rd = __0 as BigAmbitions.Rivals.RivalData;
                    var lb = __result as UI.Smartphone.Apps.Rivals.RivalLeaderboardData;
                    if (rd == null || lb == null) return;
                    string id = rd.id?.ToString() ?? "";
                    if (string.IsNullOrEmpty(id)) return;

                    GameStatePatcher.AiRivalDataRefs[id] = rd;

                    // Host-side ground-truth diagnostic: the game just built lb
                    // for this rival.  Log which owned-list its business COUNT
                    // matches (lb.ownedBusinesses.Count vs rd.ownedBusinesses /
                    // rd.ownedRetailOfficeBusinesses) plus the total income — so
                    // we know exactly what the leaderboard counts and whether it
                    // includes the factory.  Throttled to a few rivals.
                    if (MPServer.IsRunning && _hostCountDiag < 6)
                    {
                        try
                        {
                            int lbBiz   = lb.ownedBusinesses?.Count ?? -1;
                            int rdBiz   = rd.ownedBusinesses?.Count ?? -1;
                            int rdRetOf = rd.ownedRetailOfficeBusinesses?.Count ?? -1;
                            int rdBldg  = rd.ownedBuildings?.Count ?? -1;
                            float wk    = 0f; try { wk = rd.WeeklyIncome; } catch { }
                            Plugin.Logger.LogInfo($"[HostCountDiag] {id.Substring(0, Math.Min(8, id.Length))} lb.ownedBiz={lbBiz} rd.ownedBiz={rdBiz} rd.retailOffice={rdRetOf} rd.ownedBldg={rdBldg} rd.WeeklyIncome=${wk:F0}");
                            _hostCountDiag++;
                        }
                        catch { }
                    }

                    // Remote-player rows (synthetic RivalData appended via the
                    // GetAllRivalData Postfix) flow through here too.  Handle them
                    // on BOTH roles so the game ranks them by income and the row
                    // shows the right income/businesses: income comes from
                    // ClientRivalStats (host fills it from each client's
                    // self-stats; client fills it from the host's snapshot), and
                    // owned lists from local registrations (the side that has the
                    // player's ownership synced).  The LOCAL player never reaches
                    // here — the game builds their row via GetPlayerLeaderboardData.
                    if (id != MPConfig.PlayerId && GameStatePatcher.ClientPlayerRoster.ContainsKey(id))
                    {
                        try
                        {
                            GameStatePatcher.PopulateRivalOwnedFromSync(rd, id);
                            try { lb.ownedBuildings  = rd.ownedBuildings;  } catch { }
                            try { lb.ownedBusinesses = rd.ownedRetailOfficeBusinesses; } catch { }   // row counts this set
                            // CRITICAL: players are not rivals — routing them through
                            // the RivalData path makes the game flag a no-business
                            // player "defeated" (and drop their rank).  Force it off
                            // so they rank normally by income.
                            try { lb.isDefeated = false; } catch { }
                            try { if (GameStatePatcher.ClientPlayerAges.TryGetValue(id, out var page) && page > 0) lb.ageInYears = page; } catch { }
                            if (GameStatePatcher.ClientRivalStats.TryGetValue(id, out var ps))
                            {
                                if (!string.IsNullOrEmpty(ps.Name)) lb.entryName = ps.Name;
                                lb.weeklyIncome = ps.WeeklyIncome;   // even 0 — drives income-sort ranking
                            }
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetRivalLeaderboardData] player row '{id}': {ex.Message}"); }
                        return;
                    }

                    // Only the client needs the rest of this Postfix's work
                    // (populating ownedBusinesses + overriding lb.weeklyIncome).
                    // On host, the natural game calc is authoritative.
                    if (!MPClient.IsConnected) return;

                    // Client AI rival: client's RivalData has EMPTY owned lists (we
                    // suppressed SetRivalBuildings).  Repopulate them to mirror the
                    // host's EXACT counted/displayed set (by address — includes
                    // cinema/theater, excludes factory), then mirror onto lb so the
                    // leaderboard row count + breakdown match the host.
                    try
                    {
                        GameStatePatcher.PopulateRivalOwnedFromSync(rd, id);
                        try { lb.weeklyIncome = rd.WeeklyIncome; } catch { }
                        try { lb.mostActiveNeighborhood = rd.MostActiveNeighborhood; } catch { }
                        try { lb.ownedBuildings  = rd.ownedBuildings;  } catch { }
                        try { lb.ownedBusinesses = rd.ownedRetailOfficeBusinesses; } catch { }

                        // Self-check vs host's authoritative leaderboard count.
                        if (GameStatePatcher.ClientRivalStats.TryGetValue(id, out var chk)
                            && rd.ownedRetailOfficeBusinesses != null
                            && rd.ownedRetailOfficeBusinesses.Count != chk.OwnedBusinessesCount)
                        {
                            Plugin.Logger.LogWarning($"[Patch_GetRivalLeaderboardData] count mismatch id={id.Substring(0, Math.Min(8, id.Length))} client={rd.ownedRetailOfficeBusinesses.Count} host={chk.OwnedBusinessesCount}.");
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetRivalLeaderboardData_FromCache] populate: {ex.Message}"); }

                    // If host's stats snapshot included this rival (most cases
                    // do once client has opened the rivals app once), prefer
                    // host's WeeklyIncome over the local recompute.  Host has
                    // the authoritative number (accurate per-business revenue
                    // including items/stock that we don't sync for AI
                    // businesses unless someone enters the building).
                    float localCalc = lb.weeklyIncome;
                    bool overrodeIncome = false;
                    if (GameStatePatcher.ClientRivalStats.TryGetValue(id, out var s))
                    {
                        if (!string.IsNullOrEmpty(s.Name))   lb.entryName    = s.Name;
                        if (s.WeeklyIncome != 0f)            { lb.weeklyIncome = s.WeeklyIncome; overrodeIncome = true; }
                    }
                    // Diagnostic — log a sample of incomes flowing through.
                    // Throttled by static counter to avoid spam.
                    if (_diagSample < 5)
                    {
                        Plugin.Logger.LogInfo($"[Patch_GetRivalLeaderboardData] id={id.Substring(0, Math.Min(8, id.Length))} natural=${localCalc:F0} fromStats=${(GameStatePatcher.ClientRivalStats.TryGetValue(id, out var s2) ? s2.WeeklyIncome : 0):F0} overrode={overrodeIncome} final=${lb.weeklyIncome:F0} ownedBiz={(rd.ownedBusinesses?.Count ?? 0)}");
                        _diagSample++;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetRivalLeaderboardData_FromCache] {ex.Message}"); }
            }
        }

        // ── RivalsHelper.GetRivalName override (Phase 1d Wave 2) ──────────────
        // The game's RivalDataCache (private static dict on RivalsHelper) is
        // unreachable from our C# code because IL2CPP-Interop doesn't expose
        // private fields.  So we intercept lookups at the public API instead:
        // Prefix on GetRivalName checks our own GameStatePatcher.ClientRivalNames
        // dict.  If the rivalId is one we received from host, we return that
        // name and skip the original (whose lookup would miss).  If unknown,
        // we fall through to the original — preserving normal SP behavior.
        [HarmonyPatch]
        public static class Patch_RivalsHelper_GetRivalName
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("BigAmbitions.Rivals.RivalsHelper")?.GetMethod("GetRivalName",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix(string __0, ref string __result)
            {
                try
                {
                    // Fire on EITHER role — host needs the same override so the
                    // host's UI shows client character names (not PlayerIds) when
                    // looking up the client's rival entry.
                    if (!MPClient.IsConnected && !MPServer.IsRunning) return true;
                    if (string.IsNullOrEmpty(__0))  return true;
                    if (GameStatePatcher.ClientRivalNames.TryGetValue(__0, out var name)
                        && !string.IsNullOrWhiteSpace(name))
                    {
                        __result = name;
                        return false;   // skip original
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetRivalName] {ex.Message}"); }
                return true;
            }
        }

        // ── Player profile detail-view fix ────────────────────────────────────
        // When the user clicks a rival row in the leaderboard, the chain is:
        //   Button.Select → RivalsApp.ShowRival(data) → SelectedRivalUI.ShowRival
        //   → SetChartWeeklyIncome / SetChartNumberOfBusinesses → RivalsHelper.GetRivalState(id)
        // For PLAYER rows, the id is a playerId not in gi.rivalStates (we
        // deliberately keep it out to avoid ghost leaderboard rows).
        // GetRivalState returns null → null deref → detail view never opens.
        //
        // Fix: Postfix GetRivalState — if result is null AND the id is a
        // known player (in ClientPlayerRoster) or matches a player-id pattern
        // we recognize, return a synthetic empty RivalState.  The chart will
        // render empty (we don't have history data yet) but the detail view
        // opens without erroring.
        [HarmonyPatch]
        public static class Patch_RivalsHelper_GetRivalState_PlayerFallback
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("BigAmbitions.Rivals.RivalsHelper")?.GetMethod("GetRivalState",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static void Postfix(string __0, ref BigAmbitions.Rivals.RivalState __result)
            {
                try
                {
                    if (__result != null) return;
                    if (string.IsNullOrEmpty(__0)) return;
                    // CRITICAL: only inject for REMOTE players, NOT for the
                    // local player.  The game expects null for the local
                    // player's id here (it goes through GetPlayerLeaderboardData
                    // for its own UI).  Returning non-null for the local id
                    // confuses downstream code → crashes minutes later when
                    // background ticks iterate the unexpected state.
                    if (__0 == MPConfig.PlayerId) return;
                    if (!GameStatePatcher.ClientPlayerRoster.ContainsKey(__0)) return;

                    // CRITICAL: populate the history Lists to be empty (not
                    // null).  RivalState's two history lists are typically
                    // iterated by chart-rendering and other game code; null
                    // would NRE natively (no catchable exception).
                    var rs = new BigAmbitions.Rivals.RivalState
                    {
                        rivalId = __0,
                        weeklyIncomeHistory      = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Tuple<int, float>>(),
                        numberOfBusinessesHistory = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Tuple<int, int>>(),
                    };
                    __result = rs;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetRivalState_PlayerFallback] {ex.Message}"); }
            }
        }

        // ── Player profile selectable-fix (GetRivalData fallback) ─────────────
        // SelectedRivalUI.ShowRival(data) — the detail view opened when a
        // leaderboard row is clicked — calls RivalsHelper.GetRivalData(data.rivalId)
        // near the TOP (for portrait / age / neighborhood / selectedRival) and
        // dereferences the result.  For a PLAYER row the id is a playerId that
        // is NOT in RivalDataCache → GetRivalData returns null → ShowRival NREs
        // before it ever reaches the chart code.  This is why the other
        // player's profile "isn't selectable" (clicking it silently fails).
        // The Wave-10 GetRivalState fallback does NOT help — GetRivalData is
        // called first.
        //
        // Fix: Postfix GetRivalData — when it returns null AND the id is a
        // known REMOTE player, return a synthetic non-null RivalData (owned*
        // lists populated from local registrations so the profile's business
        // count + income are real; never-null lists so downstream iteration is
        // safe).  Combined with the GetRivalState fallback (empty chart), the
        // profile now opens.  Fires on EITHER role so host can open the
        // client's row too.
        [HarmonyPatch]
        public static class Patch_RivalsHelper_GetRivalData_PlayerFallback
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("BigAmbitions.Rivals.RivalsHelper")?.GetMethod("GetRivalData",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static void Postfix(string __0, ref BigAmbitions.Rivals.RivalData __result)
            {
                try
                {
                    if (__result != null) return;
                    if (string.IsNullOrEmpty(__0)) return;
                    // REMOTE players only.  NEVER synthesize for the local
                    // player's id — the game expects null there (its own UI
                    // uses GetPlayerLeaderboardData); a non-null result for the
                    // local id confuses background ticks → delayed native crash
                    // (same caution as the GetRivalState fallback).
                    if (__0 == MPConfig.PlayerId) return;
                    if (!GameStatePatcher.ClientPlayerRoster.ContainsKey(__0)) return;

                    var rd = GameStatePatcher.BuildSyntheticPlayerRivalData(__0);
                    if (rd != null)
                    {
                        __result = rd;
                        Plugin.Logger.LogInfo($"[Patch_GetRivalData_PlayerFallback] synth RivalData for player '{__0}' biz={rd.ownedBusinesses?.Count ?? 0} bldg={rd.ownedBuildings?.Count ?? 0}.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetRivalData_PlayerFallback] {ex.Message}"); }
            }
        }

        // ── Income-relative leaderboard: feed remote players into the game's ──
        // ── own ranking via GetAllRivalData ───────────────────────────────────
        // RivalLeaderboard.Load builds its row list from
        // RivalsHelper.GetAllRivalData() + GetPlayerLeaderboardData() (local),
        // then List.Sort by income and assigns ranks 1..N.  By appending a
        // synthetic RivalData per REMOTE player here, the game ranks/orders/
        // numbers them by income exactly like AI rivals — so players climb as
        // they earn, AI shift accordingly, and N clients each get their own
        // income-ordered spot (#21, #22, …).  This replaces the old manual
        // row injection (which appended at a fixed rank and produced the #42
        // doubling).
        //
        // GATED to the Load context only (RivalsLeaderboardLoadRunning) so
        // GetAllRivalData's other callers — CityMapFilters.CreateSpecialRivalFilters
        // and RivalsHelper.GetPlayerRanking — never see the synthetic players.
        [HarmonyPatch]
        public static class Patch_RivalsHelper_GetAllRivalData
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("BigAmbitions.Rivals.RivalsHelper")?.GetMethod("GetAllRivalData",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static void Postfix(Il2CppSystem.Collections.Generic.IEnumerable<BigAmbitions.Rivals.RivalData> __result)
            {
                try
                {
                    if (!GameStatePatcher.RivalsLeaderboardLoadRunning) return;
                    if (!MPClient.IsConnected && !MPServer.IsRunning) return;
                    if (GameStatePatcher.ClientPlayerRoster.Count == 0) return;
                    if (__result == null) return;

                    // GetAllRivalData returns a freshly-built List<RivalData> each
                    // call (it does `new List<>(cache.Values.Where(...))`), so we
                    // can append our synthetic remote-player entries directly to
                    // that per-call list without polluting the cache or other
                    // callers.  (Gated to the Load context by the flag above.)
                    var list = __result.TryCast<Il2CppSystem.Collections.Generic.List<BigAmbitions.Rivals.RivalData>>();
                    if (list == null)
                    {
                        Plugin.Logger.LogWarning("[Patch_GetAllRivalData] result not a List<RivalData>; skipping player injection.");
                        return;
                    }

                    int appended = 0;
                    foreach (var kv in GameStatePatcher.ClientPlayerRoster)
                    {
                        string pid = kv.Key;
                        if (string.IsNullOrEmpty(pid)) continue;
                        if (pid == MPConfig.PlayerId) continue;   // local player: game's GetPlayerLeaderboardData path
                        var syn = GameStatePatcher.BuildSyntheticPlayerRivalData(pid);
                        if (syn != null) { list.Add(syn); appended++; }
                    }

                    if (appended > 0)
                        Plugin.Logger.LogInfo($"[Patch_GetAllRivalData] appended {appended} remote player(s) to the leaderboard list (now {list.Count}).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetAllRivalData] {ex.Message}"); }
            }
        }

        // ── Per-business income override (rival detail breakdown) ─────────────
        // RivalBusinessesTable.Load builds one RivalBusinessModel per rival
        // business and computes its WeeklyIncome from reg.GetAvgDailyIncome,
        // which is $0 on the client (AI business sales aren't simulated here).
        // The host ships the authoritative per-business weekly income keyed by
        // AddressKey (RivalStatsInfo.Businesses).  This Prefix overrides each
        // cell's model income before the cell renders it.
        [HarmonyPatch]
        public static class Patch_RivalBusinessesCellView_SetData
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("UI.Smartphone.Apps.Rivals.Tables.RivalBusinessesCellView")?.GetMethod("SetData",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);

            static void Prefix(UI.Smartphone.Apps.Rivals.Tables.RivalBusinessesCellView.RivalBusinessModel __0)
            {
                try
                {
                    // CLIENT only: override each breakdown cell's income with the
                    // host's authoritative value (synced from MPServer's exact
                    // WeeklyIncomeForBusiness calc).  Host computes it natively, so
                    // it needs no override here.
                    if (!MPClient.IsConnected || __0 == null) return;
                    string key = GameStateReader.AddressKey(__0.Address);
                    if (string.IsNullOrEmpty(key)) return;
                    if (GameStatePatcher.ClientBusinessIncomeByAddress.TryGetValue(key, out var inc))
                        __0.WeeklyIncome = inc;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RivalBusinessesCellView_SetData] {ex.Message}"); }
            }
        }

        // ── Remote player portrait (relayed PNG → Sprite) ─────────────────────
        // A player's real face is generated from their CharacterData appearance,
        // which we relay as a PNG and decode into GameStatePatcher.ClientPlayerPortraits.
        // The rivals profile portrait comes from RivalPortraitHelper; for player
        // ids we return our decoded sprite instead of a generated default.
        [HarmonyPatch]
        public static class Patch_RivalPortraitHelper_GetPortrait
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("BigAmbitions.Rivals.RivalPortraitHelper")?.GetMethod("GetPortrait",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix(string __0, ref UnityEngine.Sprite __result)
            {
                try
                {
                    if (!string.IsNullOrEmpty(__0)
                        && GameStatePatcher.ClientPlayerPortraits.TryGetValue(__0, out var sp) && sp != null)
                    { __result = sp; return false; }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RivalPortraitHelper_GetPortrait] {ex.Message}"); }
                return true;
            }
        }

        [HarmonyPatch]
        public static class Patch_RivalPortraitHelper_CreatePortrait
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("BigAmbitions.Rivals.RivalPortraitHelper")?.GetMethod("CreatePortrait",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix(BigAmbitions.Rivals.RivalData __0, int __1, Il2CppSystem.Action<UnityEngine.Sprite> __2)
            {
                try
                {
                    string id = __0?.id?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(id)
                        && GameStatePatcher.ClientPlayerPortraits.TryGetValue(id, out var sp) && sp != null)
                    {
                        try { __2?.Invoke(sp); } catch (Exception exi) { Plugin.Logger.LogWarning($"[Patch_RivalPortraitHelper_CreatePortrait] invoke: {exi.Message}"); }
                        return false;   // skip default generation
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RivalPortraitHelper_CreatePortrait] {ex.Message}"); }
                return true;
            }
        }

        // ── Suppression: RivalsHelper.GenerateRivals (Phase 1d Wave 2) ────────
        // GenerateRivals uses UuidHelper.GenerateBase64Uuid to make new IDs
        // per session — different on host vs client.  When host syncs
        // buildingOwnerRivalId to client, the client looks up that ID in its
        // own RivalDataCache and finds nothing → "undefined" name in the
        // building popup.  Suppressing GenerateRivals on client leaves
        // RivalDataCache empty until our RivalsSnapshot arrives and populates
        // it with host's (id, name) pairs.
        // ── UUID-queue strategy (Phase 1d Wave 6) ─────────────────────────────
        // The cleanest way to make client's RivalDataCache match host's: don't
        // suppress GenerateRivals — instead, INTERCEPT the random-ID generator
        // it calls (UuidHelper.GenerateBase64Uuid) so the client's own
        // GenerateRivals produces the SAME ids host produced.  The game's own
        // (private) code writes RivalDataCache normally — we never need
        // direct access to that private field.
        //
        // Two Harmony patches collaborate:
        //   * Patch_RivalsHelper_GenerateRivals — Prefix flips a "we're inside"
        //     flag, Postfix flips it back AND on HOST broadcasts the rival ids
        //     to clients as soon as they're generated (so client has them
        //     before its own GenerateRivals runs).
        //   * Patch_UuidHelper_GenerateBase64Uuid — Prefix checks the flag +
        //     dequeues from GameStatePatcher.PendingRivalIdQueue.  Outside
        //     GenerateRivals (and on host, where the queue is unused), the
        //     original runs normally — 38 other callers stay untouched.

        [HarmonyPatch]
        public static class Patch_RivalsHelper_GenerateRivals
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("BigAmbitions.Rivals.RivalsHelper")?.GetMethod("GenerateRivals",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            static bool Prefix()
            {
                if (MPClient.IsConnected)
                {
                    // CLIENT state machine:
                    //   (a) queue not ready yet → suppress (startup default-
                    //       load tries to fire GenerateRivals before we have
                    //       host's IDs; we can't let it generate random IDs).
                    //   (b) queue ready, not yet injected → ALLOW this single
                    //       run.  Mark Injected so future calls are blocked.
                    //   (c) already injected → suppress (prevents the natural
                    //       new-game GenerateRivals from clearing+rewriting our
                    //       host-sourced cache).
                    if (!GameStatePatcher.ClientRivalsReady)
                    {
                        Plugin.Logger.LogInfo("[Wave6] GenerateRivals suppressed on client (queue not yet populated).");
                        return false;
                    }
                    if (GameStatePatcher.ClientRivalsInjected)
                    {
                        Plugin.Logger.LogInfo("[Wave6] GenerateRivals suppressed on client (already injected once).");
                        return false;
                    }
                    GameStatePatcher.RivalsGenerateRunning = true;
                    GameStatePatcher.ClientRivalsInjected = true;
                    int n = GameStatePatcher.PendingRivalIdQueue.Count;
                    Plugin.Logger.LogInfo($"[Wave6] GenerateRivals starting on client; UUID queue has {n} id(s) ready to feed.");
                    return true;
                }

                // HOST — always run normally.
                GameStatePatcher.RivalsGenerateRunning = true;
                if (MPServer.IsRunning)
                    Plugin.Logger.LogInfo("[Wave6] GenerateRivals starting on host; will broadcast resulting rivals on Postfix.");

                // Clear stale RivalData refs — new GenerateRivals run means
                // the old RivalDataCache entries got replaced.  Holding refs
                // to destroyed objects causes native crashes on later access.
                GameStatePatcher.AiRivalDataRefs.Clear();
                return true;
            }

            static void Postfix()
            {
                // Only true if the Prefix let the original run.  Safe to clear.
                if (!GameStatePatcher.RivalsGenerateRunning) return;
                GameStatePatcher.RivalsGenerateRunning = false;

                // Host broadcasts rivals AS SOON as its GenerateRivals completes
                // (during SaveGameManager.New, in the load sequence) so clients
                // have the ids before their own GenerateRivals fires.  This is
                // much earlier than the previous broadcast point (ReleaseStartupHold).
                if (MPServer.IsRunning)
                {
                    try
                    {
                        var snap = MPServer.BuildRivalsSnapshot();
                        MPServer.BroadcastAny(MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", snap));
                        Plugin.Logger.LogInfo($"[Wave6] Host post-GenerateRivals broadcast: {snap.Rivals.Count} rival(s).");
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Wave6] Host post-GenerateRivals broadcast: {ex.Message}"); }
                }
            }
        }

        [HarmonyPatch]
        public static class Patch_UuidHelper_GenerateBase64Uuid
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("Extensions.UuidHelper")?.GetMethod("GenerateBase64Uuid",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly);

            private static int _drained = 0;
            static bool Prefix(ref string __result)
            {
                try
                {
                    // Only intercept when we're inside GenerateRivals on the
                    // CLIENT and our queue has IDs to feed.  All other callers
                    // (item IDs, employee IDs, etc.) see the original.
                    if (!MPClient.IsConnected) return true;
                    if (!GameStatePatcher.RivalsGenerateRunning) return true;
                    if (GameStatePatcher.PendingRivalIdQueue.Count == 0)
                    {
                        if (_drained < 3)
                            Plugin.Logger.LogWarning($"[Wave6] GenerateBase64Uuid called inside GenerateRivals but queue empty — falling back to original.  Host's roster may be smaller than client expects.");
                        _drained++;
                        return true;
                    }
                    __result = GameStatePatcher.PendingRivalIdQueue.Dequeue();
                    return false;   // skip original
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Wave6] UUID Prefix: {ex.Message}"); }
                return true;
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
            // (Taxi-crash bisect Postfix/Finalizer removed 2026-06-10 — solved.)
        }

        // ── FIX: spurious hover re-entry pulses (the "highlight leak") ────────
        // HiDiag showed the client's building highlights are its OWN cursor
        // system re-firing OnIoEnter: ghost vehicles popping in/out or driving
        // through the idle cursor's ray flip MouseController's target, and when
        // the ray lands back on the building it re-enters → highlight pulse
        // with no local cause.  A GENUINE hover always involves the mouse
        // moving, so: skip OnIoEnter when this same building was exited a
        // moment ago with the cursor in (almost) the same spot.  MP-gated —
        // single-player behaviour untouched.
        [HarmonyPatch(typeof(CityBuildingController), nameof(CityBuildingController.OnIoExit))]
        public static class Patch_CBC_OnIoExit_Record
        {
            internal static readonly Dictionary<int, (float t, UnityEngine.Vector2 mp)> LastExit = new();
            static void Postfix(CityBuildingController __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                    if (__instance == null) return;
                    LastExit[__instance.GetInstanceID()] = (
                        UnityEngine.Time.realtimeSinceStartup,
                        new UnityEngine.Vector2(UnityEngine.Input.mousePosition.x, UnityEngine.Input.mousePosition.y));
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(CityBuildingController), nameof(CityBuildingController.OnIoEnter))]
        public static class Patch_CBC_OnIoEnter_HoverDebounce
        {
            private const float ReEnterWindowSeconds = 0.6f;
            private const float CursorIdleSqPixels   = 9f;     // ≤3 px = cursor didn't move
            private static float _winStart; private static int _winCount;   // HiDiag (reinstated — highlight effort resumed)

            static bool Prefix(CityBuildingController __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                    if (__instance == null) return true;

                    // ★ THE root cause (2026-06-10, pick-trace-confirmed): on a
                    // single machine the UNFOCUSED instance still receives the
                    // system cursor position — as negative / out-of-bounds
                    // coordinates (the mouse is over the OTHER instance's
                    // window) — and the game picks buildings with them.  An
                    // unfocused window, or a cursor outside the window, must
                    // not hover-pick at all.
                    if (!UnityEngine.Application.isFocused) return false;
                    var mpos = UnityEngine.Input.mousePosition;
                    if (mpos.x < 0f || mpos.y < 0f ||
                        mpos.x >= UnityEngine.Screen.width || mpos.y >= UnityEngine.Screen.height)
                        return false;

                    float now = UnityEngine.Time.realtimeSinceStartup;

                    // Rule 1 (same building): exited a moment ago with an idle
                    // cursor → ghost re-pop, swallow.
                    bool suppress = false;
                    if (Patch_CBC_OnIoExit_Record.LastExit.TryGetValue(__instance.GetInstanceID(), out var rec))
                    {
                        float dt = now - rec.t;
                        var mp = new UnityEngine.Vector2(UnityEngine.Input.mousePosition.x, UnityEngine.Input.mousePosition.y);
                        suppress = dt < ReEnterWindowSeconds && (mp - rec.mp).sqrMagnitude < CursorIdleSqPixels;
                    }

                    // Rule 2 (any building): the mouse is idle AND the camera is
                    // still — the world under the cursor can't legitimately
                    // change, so ANY new hover is a ghost crossing/popping in
                    // the ray (HiDiag showed the residual flicker ALTERNATES
                    // between adjacent buildings, which Rule 1 can't catch).
                    bool idleScene = now - MPCanvasUI.InputUnstableAt > 0.25f;
                    if (idleScene) suppress = true;

                    if (now - _winStart > 1f) { _winStart = now; _winCount = 0; }
                    if (++_winCount <= 30)
                    {
                        string nm = "?"; try { nm = __instance.name; } catch { }
                        // Pick-trace: where does this building sit ON SCREEN vs the
                        // mouse?  Near-match ⇒ cursor-offset theory; far apart ⇒ the
                        // pick ray comes from a different camera than the render one.
                        string trace = "";
                        try
                        {
                            var cam = UnityEngine.Camera.main;
                            var mp2 = UnityEngine.Input.mousePosition;
                            if (cam != null)
                            {
                                var sp = cam.WorldToScreenPoint(__instance.transform.position);
                                trace = $" mouse=({mp2.x:F0},{mp2.y:F0}) bldg=({sp.x:F0},{sp.y:F0},z{sp.z:F0}) cam='{cam.name}'";
                            }
                        }
                        catch { }
                        Plugin.Logger.LogInfo($"[HiDiag/{(MPServer.IsRunning ? "HOST" : "CLIENT")}] OnIoEnter '{nm}'{(suppress ? (idleScene ? " SUPPRESSED-idle" : " SUPPRESSED") : "")}{trace}");
                    }
                    return !suppress;
                }
                catch { return true; }
            }
        }

        // (Patch_CBC_SetHighlight_Diag REMOVED 2026-06-10 — leftover highlight
        //  diagnostic.  Its detour sat on a method the taxi-map filter tail
        //  calls hundreds of times per frame: prime suspect for the silent
        //  native taxi crash, and timeline-consistent with its first report.)

        // ── Patches: TimeMachine — consensus time-skip (v2 architecture) ──────
        // v1 suppressed StartTimeMachine outright; that WEDGED the activity
        // flow (one press per session, then the skip button went inert) and
        // crashed the taxi.  v2: the machine STARTS natively (caller state
        // healthy, native overlay + working Cancel button) but its Update is
        // FROZEN in MP — it never advances time itself.  MPRestSync watches
        // isRunning for votes; the host's consensus executor moves the clock;
        // our code calls StopTimeMachine when the goal is reached.
        // Taxi bypasses BOTH patches (native behavior) until taxi v2.
        [HarmonyPatch]
        public static class Patch_TimeMachine_Start_Consensus
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("Timemachine.TimeMachine")
                     ?? VehicleManager.FindGameType("TimeMachine");
                int n = 0;
                if (t != null)
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "StartTimeMachine") { n++; }
                Plugin.Logger.LogInfo($"[Rest] TimeMachine patch: type={(t != null ? t.FullName : "NOT FOUND")} targets={n}");
                if (t == null) yield break;
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    if (m.Name == "StartTimeMachine") yield return m;
            }

            static void Postfix()
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                // Taxi ride machine → INSTANT ARRIVAL (overlay hidden, machine
                // stopped a beat later — its end handler teleports the player,
                // the clock never moves).
                if (TrafficSync.LocalInTaxi || MPRestSync.TaxiRidePending)
                {
                    try { MPRestSync.OnTaxiMachineStarted(); } catch { }
                    return;
                }
                // Rest-class skip press: immediate clean shutdown through the
                // engine's own off switch — the engine never stays alive in MP.
                try { MPRestSync.OnNativeSkipButtonPressed(); } catch { }
            }
        }

        // (Taxi breadcrumb probe pack REMOVED 2026-06-10 — crash solved:
        //  leftover SetHighlight diagnostic detour.  See context log.)

        // ── Patch: PlayerActivityUI.Update NRE shield ─────────────────────────
        // One taxi-crash flavor died on an NRE inside this Update (state
        // machine tripped mid-flow).  In MP, log + swallow: skipping one UI
        // tick is benign; killing the game is not.
        [HarmonyPatch]
        public static class Patch_ActivityUI_Update_Shield
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("PlayerActivity.PlayerActivityUI")
                     ?? VehicleManager.FindGameType("PlayerActivityUI");
                return t?.GetMethod("Update",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            }

            private static int _swallowed;
            static Exception? Finalizer(Exception? __exception)
            {
                if (__exception == null) return null;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return __exception;
                _swallowed++;
                if (_swallowed <= 5 || _swallowed % 200 == 0)
                    Plugin.Logger.LogWarning($"[ActivityUI] swallowed {__exception.GetType().Name} in Update (#{_swallowed}).");
                return null;
            }
        }

        // ── Patch: FullMenu.ShowApp/SelectApp — native app selected → our
        //    Business page steps aside (it lives as a sibling in the shell). ──
        [HarmonyPatch]
        public static class Patch_FullMenu_ShowApp_HideBusiness
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                // ShowApp/SelectApp have OVERLOADS — GetMethod(name) threw
                // AmbiguousMatch and the whole patch class failed (2026-06-10).
                // Enumerate and patch every overload.
                var t = VehicleManager.FindGameType("UI.Smartphone.FullMenu");
                int n = 0;
                if (t != null)
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                        if (m.Name == "ShowApp" || m.Name == "SelectApp") { n++; yield return m; }
                Plugin.Logger.LogInfo($"[HubApp] FullMenu show/select patches: {n} target(s)");
            }

            static void Postfix()
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                MPHubNativePage.HidePage();
            }
        }

        // ── Patch: PlayerActivityUI.HidePanel — native rest panel never shows ──
        // Rest v4 (user-designed): clicking a bench/bed must NOT open the
        // game's rest dialog; our own dock (MPCanvasUI.TickRestUI) replaces it.
        // The UI component keeps running (it drives the activity state machine)
        // — only its VISIBILITY is forced off: every HidePanel call becomes
        // HidePanel(true) in MP.
        [HarmonyPatch]
        public static class Patch_PlayerActivityUI_HidePanel
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("PlayerActivity.PlayerActivityUI")
                     ?? VehicleManager.FindGameType("PlayerActivityUI");
                var m = t?.GetMethod("HidePanel",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Plugin.Logger.LogInfo($"[RestDock] HidePanel patch: {(m != null ? "patched" : "NOT FOUND")}");
                return m;
            }

            static void Prefix(ref bool hide)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                // Only OUR rest-class activities lose their panel — foreign
                // activities (the taxi flow!) keep their native UI untouched.
                if (!MPRestSync.IsCurrentActivityRestClass()) return;
                hide = true;   // the native panel is replaced by our dock
            }
        }

        // ── Patches: blanket pause suppression in MP ──────────────────────────
        // "Nothing pauses outside the explicit vote system."  Every native pause
        // entry (rest dialogs, menus, the skip engine's time-control lock) is
        // suppressed unless OUR code holds the key (GameStateReader.
        // AllowNativePauseCall — set around the pause-vote system's own calls).
        [HarmonyPatch]
        public static class Patch_GSC_SetPause_Suppress
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("GameSpeedController");
                var m = t?.GetMethod("SetPause", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Plugin.Logger.LogInfo($"[Pause] SetPause suppression: {(m != null ? "patched" : "NOT FOUND")}");
                return m;
            }

            private static int _n;
            static bool Prefix(bool newPause)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                if (GameStateReader.AllowNativePauseCall) return true;
                if (!newPause) return true;                  // un-pausing is always fine
                if (_n++ < 5 || _n % 100 == 0)
                    Plugin.Logger.LogInfo($"[Pause] native pause suppressed (#{_n}).");
                return false;
            }
        }

        [HarmonyPatch]
        public static class Patch_GSC_DisableTimeControl_Suppress
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("GameSpeedController");
                var m = t?.GetMethod("DisableTimeControl", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Plugin.Logger.LogInfo($"[Pause] DisableTimeControl suppression: {(m != null ? "patched" : "NOT FOUND")}");
                return m;
            }

            private static int _n;
            static bool Prefix(bool disabled)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                if (GameStateReader.AllowNativePauseCall) return true;
                if (!disabled) return true;                  // re-enabling is always fine
                if (_n++ < 5 || _n % 100 == 0)
                    Plugin.Logger.LogInfo($"[Pause] time-control lock suppressed (#{_n}).");
                return false;
            }
        }

        // ── Patch: EntityController.UpdateNavMeshTargets NRE shield ───────────
        // Taxi boarding round 2 (2026-06-10): with the Gley storm silenced,
        // boarding still died — final exception before shutdown is an NRE in
        // EntityController.UpdateNavMeshTargets fired from a frame-delayed
        // coroutine (the boarding transition tears the agent down before the
        // delayed update runs).  Swallow it in MP: skipping one transient
        // navmesh-target refresh is harmless.
        [HarmonyPatch]
        public static class Patch_EntityNavMesh_NREShield
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("EntityController");
                var m = t?.GetMethod("UpdateNavMeshTargets",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Plugin.Logger.LogInfo($"[NavShield] EntityController.UpdateNavMeshTargets: {(m != null ? "patched" : "NOT FOUND")}");
                if (m != null) yield return m;
            }

            private static int _swallowed;
            static Exception? Finalizer(Exception __exception)
            {
                if (__exception == null) return null;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return __exception;
                _swallowed++;
                if (_swallowed <= 5 || _swallowed % 200 == 0)
                    Plugin.Logger.LogWarning($"[NavShield] swallowed {__exception.GetType().Name} in UpdateNavMeshTargets (#{_swallowed}).");
                return null;
            }
        }

        // ── Patch: Gley traffic collider NRE shield ───────────────────────────
        // Real traffic cars' collision sensors throw NullReferenceExceptions
        // when they touch OUR ghost objects (script-stripped clones lack the
        // Gley data the handler reads).  Mostly harmless spam — but a taxi the
        // player RIDES dies mid-coroutine on it (the 2026-06-10 "taxi crash":
        // ride hangs, game must be killed).  In MP, swallow those exceptions:
        // the traffic car simply ignores the ghost, which is correct.
        [HarmonyPatch]
        public static class Patch_GleyVehicle_NREShield
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("GleyTrafficSystem.VehicleComponent");
                int n = 0;
                if (t != null)
                    foreach (var name in new[] { "NewColliderHit", "OnTriggerEnter", "OnTriggerExit" })
                    {
                        var m = t.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (m != null) { n++; yield return m; }
                    }
                Plugin.Logger.LogInfo($"[Gley] NRE shield: type={(t != null ? "ok" : "NOT FOUND")} targets={n}");
            }

            private static int _swallowed;
            static Exception? Finalizer(Exception __exception)
            {
                if (__exception == null) return null;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return __exception;
                _swallowed++;
                if (_swallowed <= 5 || _swallowed % 500 == 0)
                    Plugin.Logger.LogWarning($"[Gley] swallowed {__exception.GetType().Name} in traffic collider handler (#{_swallowed}) — ghost contact.");
                return null;
            }
        }

        [HarmonyPatch]
        public static class Patch_TimeMachine_Update_Freeze
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("Timemachine.TimeMachine")
                     ?? VehicleManager.FindGameType("TimeMachine");
                return t?.GetMethod("Update",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            }

            static bool Prefix()
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                // No taxi bypass: the ride machine is stopped instantly (taxi
                // v2 = instant arrival), so it must never self-advance either.
                return false;                                  // machine never self-advances in MP
            }
        }

        // ── Patch: TaxiSystem.TravelTo — arms instant-arrival mode ────────────
        [HarmonyPatch]
        public static class Patch_TaxiSystem_TravelTo_Arm
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("TaxiSystem");
                var m = t?.GetMethod("TravelTo",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                Plugin.Logger.LogInfo($"[Taxi] TravelTo arm patch: {(m != null ? "patched" : "NOT FOUND")}");
                return m;
            }

            static void Prefix()
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                try { MPRestSync.OnTaxiRideStarting(); } catch { }
            }
        }

        // ── Patch: CollapsibleWindow.OnHover — phone only ─────────────────────
        // The phone's hover-peek slides toward the uncollapsed position; with
        // our taller phone (adjusted collapse slide) the peek travels far and
        // turns the uncollapse button into a moving target.  Field writes
        // didn't stick — skip the hover HANDLER itself for the phone instance
        // (other collapsible windows keep their peek; click-toggle unaffected).
        [HarmonyPatch]
        public static class Patch_CollapsibleWindow_OnHover_PhoneOff
        {
            static System.Reflection.MethodBase? TargetMethod()
                => VehicleManager.FindGameType("UI.CollapsibleWindow")?.GetMethod("OnHover",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            static bool Prefix(UnityEngine.Component __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                    if (__instance != null && __instance.gameObject.name == "Container"
                        && __instance.transform.parent != null
                        && __instance.transform.parent.name == "Smartphone")
                        return false;
                }
                catch { }
                return true;
            }
        }

        // ── Patch: SaveGamePathHelper.CurrentVersionFolderPath ────────────────
        // Durable MP load: the game's SaveGameManager.Load locates a save by
        // re-scanning CurrentVersionFolderPath() (the single-player version folder).
        // MP saves live under _BAMP_MP, which that scan never sees.  While an MP load
        // is in progress (MPSaveCoordinator.LoadRedirectFolder set, on the main thread,
        // only around the Load call) we redirect this to the MP session folder so the
        // game's own Load finds + loads our save natively — no staging, and MP saves
        // never touch the single-player folder (anti-cheat for free).
        [HarmonyPatch(typeof(SaveGamePathHelper), nameof(SaveGamePathHelper.CurrentVersionFolderPath))]
        public static class Patch_CurrentVersionFolderPath_MpRedirect
        {
            static void Postfix(ref string __result)
            {
                var redirect = MPSaveCoordinator.LoadRedirectFolder;
                if (!string.IsNullOrEmpty(redirect)) __result = redirect;
            }
        }

        // ── In-game pause menu (UI.MiniMenu.MiniMenu) ─────────────────────────
        // The Escape pause menu's "Save" and "Save and Exit to Desktop" buttons
        // call SaveGameManager into the SINGLE-PLAYER folder.  In an MP session
        // that would write a player's progress only to their solo save and lose
        // it to the host-centralized session.  These prefixes reroute both
        // buttons through the coordinated MP save when MP is active; in single
        // player they are inert (Prefix returns true → the game runs unchanged).
        //
        // SaveGame()              → MP save, then close the menu (Toggle(false)),
        //                           skip the SP save.
        // SaveAndExitToDesktop()  → MP save, then QuitToDesktop() (the game's own
        //                           NO-save exit path) so we exit cleanly WITHOUT
        //                           writing a single-player save.

        /// <summary>Resolves the game's MiniMenu type + caches its Toggle/Quit
        /// methods.  Uses the full interop name first, then a simple-name scan as
        /// a fallback in case the interop namespace differs from the dump.</summary>
        internal static class MiniMenuUtil
        {
            private static Type? _type;
            private static System.Reflection.MethodBase? _toggleBool;
            private static System.Reflection.MethodBase? _quit;

            internal static Type? Resolve()
            {
                if (_type != null) return _type;
                _type = VehicleManager.FindGameType("UI.MiniMenu.MiniMenu");
                if (_type == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var ty in types)
                        {
                            if (ty.Name != "MiniMenu") continue;
                            if (ty.GetMethod("SaveAndExitToDesktop",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) == null)
                                continue;
                            _type = ty; break;
                        }
                        if (_type != null) break;
                    }
                }
                return _type;
            }

            internal static System.Reflection.MethodBase? Method(string name)
                => Resolve()?.GetMethod(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            /// <summary>Close the pause menu — Toggle(bool show=false), the same
            /// call the original SaveGame() makes after a successful save.</summary>
            internal static void Close(object miniMenu)
            {
                try
                {
                    if (_toggleBool == null)
                    {
                        foreach (var m in (Resolve()?.GetMethods(
                                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                 ?? Array.Empty<System.Reflection.MethodInfo>()))
                            if (m.Name == "Toggle" && m.GetParameters().Length == 1) { _toggleBool = m; break; }
                    }
                    _toggleBool?.Invoke(miniMenu, new object[] { false });
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuSave] close menu: {ex.Message}"); }
            }

            /// <summary>Exit to desktop WITHOUT saving (the game's own no-save
            /// quit), so an MP Save-and-Exit doesn't also leave an SP save.</summary>
            internal static void QuitToDesktop(object miniMenu)
            {
                _quit ??= Method("QuitToDesktop");
                _quit?.Invoke(miniMenu, null);
            }

            /// <summary>Reads the name the player typed in the pause-menu save box
            /// (MiniMenu.saveGameName, a TMP_InputField) so the MP save can use it
            /// as the session name.  Empty if unavailable.</summary>
            internal static string GetSaveName(object miniMenu)
            {
                try
                {
                    var t = Resolve();
                    if (t == null) return "";
                    const System.Reflection.BindingFlags BF =
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    // Il2CppInterop exposes Unity-serialized fields as PROPERTIES, so try
                    // the property first; fall back to a real field just in case.
                    object? input = t.GetProperty("saveGameName", BF)?.GetValue(miniMenu)
                                 ?? t.GetField("saveGameName", BF)?.GetValue(miniMenu);
                    if (input == null) { Plugin.Logger.LogWarning("[MenuSave] saveGameName not found on MiniMenu."); return ""; }
                    var textProp = input.GetType().GetProperty("text",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    string name = (textProp?.GetValue(input) as string)?.Trim() ?? "";
                    Plugin.Logger.LogInfo($"[MenuSave] save box name = '{name}'.");
                    return name;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuSave] read save name: {ex.Message}"); return ""; }
            }
        }

        // ── In-game MP window input suppression ───────────────────────────────
        // While the F9 chat window is focused / hovered / being dragged, force the
        // game's own "a text field is selected" gate true so typing + clicking the
        // window doesn't drive the character (movement / camera / hotkeys / click-to-
        // move).  Inert otherwise (MPChat.SuppressGameInput is false).
        [HarmonyPatch(typeof(GameManager), "HasInputSelected")]
        public static class Patch_GM_HasInputSelected
        {
            static void Postfix(ref bool __result) { if (MPChat.SuppressGameInput) __result = true; }
        }

        [HarmonyPatch(typeof(GameManager), "ShouldBlockKeyboardShortcuts")]
        public static class Patch_GM_ShouldBlockKeyboardShortcuts
        {
            static void Postfix(ref bool __result) { if (MPChat.SuppressGameInput) __result = true; }
        }

        [HarmonyPatch]
        public static class Patch_MiniMenu_SaveGame
        {
            static System.Reflection.MethodBase? TargetMethod()
                => MiniMenuUtil.Method("SaveGame");

            static bool Prefix(object __instance)
            {
                // After a host loss the client is offline (forking into SP is
                // allowed by design) — the connection checks below already route
                // to the vanilla SP save.
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;   // SP → normal save
                try
                {
                    string name = MiniMenuUtil.GetSaveName(__instance);
                    Plugin.Logger.LogInfo($"[MenuSave] Save '{name}' → coordinated MP save (skipping SP save).");
                    MPSaveCoordinator.MenuSave(exiting: false, saveName: name);
                    MiniMenuUtil.Close(__instance);   // mirror the original's menu-close UX
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuSave] SaveGame: {ex.Message}"); }
                return false;   // skip the single-player save
            }
        }

        [HarmonyPatch]
        public static class Patch_MiniMenu_SaveAndExitToDesktop
        {
            static System.Reflection.MethodBase? TargetMethod()
                => MiniMenuUtil.Method("SaveAndExitToDesktop");

            static bool Prefix(object __instance)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;   // SP → normal
                try
                {
                    string name = MiniMenuUtil.GetSaveName(__instance);
                    Plugin.Logger.LogInfo($"[MenuSave] Save & Exit '{name}' → coordinated MP save, then quit (no SP save).");
                    MPSaveCoordinator.MenuSave(exiting: true, saveName: name);
                    MiniMenuUtil.QuitToDesktop(__instance);
                }
                catch (Exception ex)
                {
                    // If our path failed, don't strand the player at the menu —
                    // fall back to the game's own save+exit.
                    Plugin.Logger.LogWarning($"[MenuSave] SaveAndExit: {ex.Message} — falling back to game save+exit.");
                    return true;
                }
                return false;   // we handled both save + exit
            }
        }

    }
}
