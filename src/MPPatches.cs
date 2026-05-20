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

            static void Prefix(object __instance)
            {
                try
                {
                    Plugin.Logger.LogInfo($"[Building] DelayedEnterBuildingActions on {__instance?.GetType().Name ?? "<static>"}");
                    // CLAUDE-DIAGNOSTIC — capture the call stack so we can see
                    // who calls DelayedEnterBuildingActions on the host.  Without
                    // this we don't know the call site (Invoke probe came back
                    // empty, so it isn't UnityEngine.MonoBehaviour.Invoke).
                    string st = Environment.StackTrace;
                    foreach (var line in st.Split('\n'))
                        Plugin.Logger.LogInfo($"[Building/Stack] {line.Trim()}");
                    TrafficSync.OnEnteredBuilding("DelayedEnterBuildingActions");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] DelayedEnterBuilding prefix: {ex.Message}"); }
            }
        }

        // CLAUDE-DIAGNOSTIC — DOTween DelayedCall probe.
        // Given DelayedEnterBuildingActions' stack trace shows IL2CPP→managed
        // with no managed caller frame, DOTween's DOVirtual.DelayedCall is the
        // top suspect.  Patch every method named "DelayedCall" across all
        // assemblies (FindAllMethodsByName) with a Postfix that logs call +
        // a short stack trace.  Whichever caller scheduled DelayedEnterBuildingActions
        // shows up just above.
        [HarmonyPatch]
        public static class Patch_DelayedCall_Diag
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("DelayedCall");

            static void Postfix(System.Reflection.MethodBase __originalMethod)
            {
                try
                {
                    string side = MPServer.IsRunning ? "HOST" : (MPClient.IsConnected ? "CLIENT" : "SP");
                    Plugin.Logger.LogInfo(
                        $"[InvokeProbe/{side}] {__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name} fired");
                    var lines = Environment.StackTrace.Split('\n');
                    for (int i = 0; i < lines.Length && i < 10; i++)
                        Plugin.Logger.LogInfo($"[InvokeProbe/{side}/Stack] {lines[i].Trim()}");
                }
                catch { }
            }
        }

        // CLAUDE-DIAGNOSTIC — GameObject.SendMessage probe.
        // SendMessage is the other native-dispatched method-by-name mechanism.
        // Only logs calls whose target method name suggests building/delay
        // semantics, to keep the log clean.
        [HarmonyPatch(typeof(UnityEngine.GameObject), nameof(UnityEngine.GameObject.SendMessage), new[] { typeof(string) })]
        public static class Patch_GO_SendMessage_Diag
        {
            static void Postfix(UnityEngine.GameObject __instance, string __0)
            {
                try
                {
                    if (__instance == null || __0 == null) return;
                    if (!__0.Contains("Delayed") && !__0.Contains("Enter") && !__0.Contains("Building")) return;
                    string side = MPServer.IsRunning ? "HOST" : (MPClient.IsConnected ? "CLIENT" : "SP");
                    Plugin.Logger.LogInfo($"[InvokeProbe/{side}] GameObject('{__instance.name}').SendMessage('{__0}')");
                }
                catch { }
            }
        }

        // CLAUDE-DIAGNOSTIC — StartCoroutine probe filtered to BuildingManager.
        // Since the Invoke probe came back empty, the most likely remaining
        // schedule mechanism is a coroutine.  Captures every coroutine started
        // by a BuildingManager so we can see what (if anything) it kicks off
        // that would eventually call DelayedEnterBuildingActions.
        [HarmonyPatch(typeof(UnityEngine.MonoBehaviour), nameof(UnityEngine.MonoBehaviour.StartCoroutine), new[] { typeof(System.Collections.IEnumerator) })]
        public static class Patch_MB_StartCoroutine_Diag
        {
            static void Postfix(UnityEngine.MonoBehaviour __instance, System.Collections.IEnumerator __0)
            {
                try
                {
                    if (__instance == null || __0 == null) return;
                    var t = __instance.GetIl2CppType();
                    if (t == null || t.Name != "BuildingManager") return;
                    string side = MPServer.IsRunning ? "HOST" : (MPClient.IsConnected ? "CLIENT" : "SP");
                    Plugin.Logger.LogInfo($"[InvokeProbe/{side}] BuildingManager.StartCoroutine({__0.GetType().Name})");
                }
                catch { }
            }
        }

        [HarmonyPatch]
        public static class Patch_EnterBuildingWithVehicle
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("EnterBuildingWithVehicle");
            static void Prefix(object __instance)
            {
                try
                {
                    Plugin.Logger.LogInfo($"[Building] EnterBuildingWithVehicle on {__instance?.GetType().Name ?? "<static>"}");
                    TrafficSync.OnEnteredBuilding("EnterBuildingWithVehicle");
                }
                catch { }
            }
        }

        [HarmonyPatch]
        public static class Patch_EnterParking
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("EnterParking");
            static void Prefix(object __instance)
            {
                try
                {
                    Plugin.Logger.LogInfo($"[Building] EnterParking on {__instance?.GetType().Name ?? "<static>"}");
                    TrafficSync.OnEnteredBuilding("EnterParking");
                }
                catch { }
            }
        }

        [HarmonyPatch]
        public static class Patch_ExitFromBuilding
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("ExitFromBuilding");

            static void Prefix(object __instance)
            {
                try
                {
                    var t = __instance?.GetType().Name ?? "<static>";
                    // CLAUDE-DIAGNOSTIC — log unconditionally so we can see if
                    // this fires on the client, whose LocalInBuilding flag
                    // never flipped true and so OnExitFromBuilding's own log
                    // would no-op.
                    Plugin.Logger.LogInfo($"[ExitDiag/PATCH] ExitFromBuilding on {t}");
                    TrafficSync.OnExitFromBuilding(t);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] ExitFromBuilding prefix: {ex.Message}"); }
            }
        }

        [HarmonyPatch]
        public static class Patch_ExitFromBuildingCoroutine
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("ExitFromBuildingCoroutine");

            // Defensive: also clear the flag here in case ExitFromBuilding's
            // patch missed an exit path.
            static void Prefix(object __instance)
            {
                try
                {
                    // CLAUDE-DIAGNOSTIC — log unconditionally (see Patch_ExitFromBuilding above).
                    Plugin.Logger.LogInfo($"[ExitDiag/PATCH] ExitFromBuildingCoroutine on {__instance?.GetType().Name ?? "<static>"}");
                    TrafficSync.OnExitFromBuilding(__instance?.GetType().Name);
                }
                catch { }
            }
        }

        // CLAUDE-DIAGNOSTIC — exit-trigger investigation (mirror of #6 entry probe).
        // Symptom: client walks into building exit trigger; nothing fires.
        // Host: ExitFromBuildingCoroutine fires fine.  Wider net of likely
        // entry points — whichever fires on the client identifies the real
        // exit signal, or none fires which means the trigger collider itself
        // isn't responding to the client's player.

        [HarmonyPatch]
        public static class Patch_OnExitBuilding_Diag
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("OnExitBuilding");
            static void Prefix(object __instance)
            { try { Plugin.Logger.LogInfo($"[ExitDiag/PATCH] OnExitBuilding on {__instance?.GetType().Name ?? "<static>"}"); } catch { } }
        }

        [HarmonyPatch]
        public static class Patch_GetExitPosition_Diag
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("GetExitPosition");
            static void Postfix(object __instance, object __result)
            { try { Plugin.Logger.LogInfo($"[ExitDiag/PATCH] GetExitPosition on {__instance?.GetType().Name ?? "<static>"} → {__result}"); } catch { } }
        }

        [HarmonyPatch]
        public static class Patch_ExitFloor_Diag
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("ExitFloor");
            static void Prefix(object __instance)
            { try { Plugin.Logger.LogInfo($"[ExitDiag/PATCH] ExitFloor on {__instance?.GetType().Name ?? "<static>"}"); } catch { } }
        }

        // CLAUDE-DIAGNOSTIC — Invoke / CancelInvoke probe for BuildingManager.
        // DelayedEnterBuildingActions fires on host but never on client.  Patch
        // MonoBehaviour.Invoke + CancelInvoke and filter to instances whose
        // runtime type is BuildingManager.  Two possible conclusions:
        //   (1) Host logs '[InvokeProbe/HOST] BuildingManager.Invoke("DelayedEnterBuildingActions", ...)'
        //       and client logs the same → the schedule is made on both, then
        //       something on the client cancels or never executes it.
        //   (2) Host logs it and client doesn't → the schedule itself is
        //       gated on something that's false on the client.

        [HarmonyPatch(typeof(UnityEngine.MonoBehaviour), nameof(UnityEngine.MonoBehaviour.Invoke), new[] { typeof(string), typeof(float) })]
        public static class Patch_MB_Invoke
        {
            static void Postfix(UnityEngine.MonoBehaviour __instance, string __0, float __1)
            {
                try
                {
                    if (__instance == null) return;
                    var t = __instance.GetIl2CppType();
                    if (t == null || t.Name != "BuildingManager") return;
                    string side = MPServer.IsRunning ? "HOST" : (MPClient.IsConnected ? "CLIENT" : "SP");
                    Plugin.Logger.LogInfo($"[InvokeProbe/{side}] BuildingManager.Invoke('{__0}', {__1})");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(UnityEngine.MonoBehaviour), nameof(UnityEngine.MonoBehaviour.CancelInvoke), new[] { typeof(string) })]
        public static class Patch_MB_CancelInvokeNamed
        {
            static void Postfix(UnityEngine.MonoBehaviour __instance, string __0)
            {
                try
                {
                    if (__instance == null) return;
                    var t = __instance.GetIl2CppType();
                    if (t == null || t.Name != "BuildingManager") return;
                    string side = MPServer.IsRunning ? "HOST" : (MPClient.IsConnected ? "CLIENT" : "SP");
                    Plugin.Logger.LogInfo($"[InvokeProbe/{side}] BuildingManager.CancelInvoke('{__0}')");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(UnityEngine.MonoBehaviour), nameof(UnityEngine.MonoBehaviour.CancelInvoke), new Type[0])]
        public static class Patch_MB_CancelInvokeAll
        {
            static void Postfix(UnityEngine.MonoBehaviour __instance)
            {
                try
                {
                    if (__instance == null) return;
                    var t = __instance.GetIl2CppType();
                    if (t == null || t.Name != "BuildingManager") return;
                    string side = MPServer.IsRunning ? "HOST" : (MPClient.IsConnected ? "CLIENT" : "SP");
                    Plugin.Logger.LogInfo($"[InvokeProbe/{side}] BuildingManager.CancelInvoke(all)");
                }
                catch { }
            }
        }

        // CLAUDE-DIAGNOSTIC — method-call tracer for the building entry path.
        // Patches every public method declared on BuildingManager with a
        // logging Prefix.  Run on both sides simultaneously and compare —
        // the first method that appears in HOST's log but not CLIENT's is
        // the next step after wherever the client coroutine bails (= one
        // hop from the first domino).
        //
        // Why class-wide on BuildingManager: we don't know exactly which
        // method the entry coroutine calls into next, and the coroutine's
        // own MoveNext is compiler-generated.  Patching every method gives
        // a complete call trace without us having to guess.
        [HarmonyPatch]
        public static class Patch_BuildingManager_AllMethods_Diag
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("BuildingManager");
                if (t == null) yield break;
                var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                       | System.Reflection.BindingFlags.DeclaredOnly;
                foreach (var m in t.GetMethods(bf))
                {
                    if (m.IsAbstract) continue;
                    if (m.ContainsGenericParameters) continue;
                    // Skip hot-path Unity messages and trivial accessors —
                    // they'd flood the log without helping us identify the
                    // entry sequence.
                    string n = m.Name;
                    if (n == "Update" || n == "FixedUpdate" || n == "LateUpdate"
                     || n == "OnGUI"  || n == "OnEnable"   || n == "OnDisable"
                     || n == "Awake"  || n == "Start"      || n == "OnDestroy"
                     || n.StartsWith("get_") || n.StartsWith("set_"))
                        continue;
                    // IL2CPP wrapped types expose lots of base.Object methods —
                    // skip the obvious ones.
                    if (n == "ToString" || n == "GetHashCode" || n == "Equals" || n == "Finalize")
                        continue;
                    yield return m;
                }
            }
            static void Prefix(System.Reflection.MethodBase __originalMethod, object __instance)
            {
                try
                {
                    Plugin.Logger.LogInfo(
                        $"[BMTrace/PATCH] BuildingManager.{__originalMethod?.Name} (static={__originalMethod?.IsStatic})");
                }
                catch { }
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

    }
}
