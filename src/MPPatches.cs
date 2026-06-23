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

        // ── Patch: BizManPresentation.OnTerminateContractConfirm ──────────────
        // The native "terminate lease / move out" runs entirely on the local
        // SaveGameManager.Current (RentedByPlayer=false, business emptied, deposit
        // refunded) and — unlike renting (Patch_RentBuilding above) — was NEVER
        // routed to the host.  A CLIENT's unrent therefore stayed purely local while
        // the host kept the building owned by that client, so the host's
        // authoritative ownership/business sync re-asserted the rental → the reported
        // "client can't unrent".  This postfix mirrors the rent path: the unrent has
        // already run locally, so we just release ownership — the client asks the
        // host (VacateRequest), the host releases directly.
        [HarmonyPatch(typeof(BizManPresentation), "OnTerminateContractConfirm")]
        public static class Patch_TerminateContract
        {
            static void Postfix(BizManPresentation __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;   // single player — nothing to do

                    var reg = RegOf(__instance);
                    if (reg == null) { Plugin.Logger.LogWarning("[Patch] Unrent: could not resolve the building registration."); return; }
                    string key = GameStateReader.AddressKey(reg);

                    if (MPClient.IsConnected)
                    {
                        MPClient.RequestVacateBuilding(key);
                        Plugin.Logger.LogInfo($"[Patch] Client unrented {key} locally + notifying host.");
                        return;
                    }
                    // Host — released locally already; clear ownership + tell clients.
                    MPServer.BuildingOwners.TryRemove(key, out _);
                    MPServer.BroadcastVacate(key);
                    Plugin.Logger.LogInfo($"[Patch] Host unrented {key}, broadcasted vacate to clients.");
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Patch] Patch_TerminateContract: {ex.Message}"); }
            }

            // bizManBusiness is a private field; buildingRegistration may be a field
            // or property — resolve both reflectively (BuildingRegistration is a
            // class, so reading it is safe on this runtime).
            static BuildingRegistration? RegOf(BizManPresentation pres)
            {
                object? biz = ReadMember(pres, "bizManBusiness");
                return biz == null ? null : ReadMember(biz, "buildingRegistration") as BuildingRegistration;
            }
            static object? ReadMember(object obj, string name)
            {
                var t = obj.GetType();
                var f = AccessTools.Field(t, name);
                if (f != null) return f.GetValue(obj);
                var p = AccessTools.Property(t, name);
                return p != null ? p.GetValue(obj) : null;
            }
        }

        // ── Patch: BizManPresentation.SendBuyBuildingOffer ────────────────────
        // Buying real estate adds the building to the LOCAL gi.realEstate and removes it
        // from the LOCAL gi.buildingsForSale — but the host owns the authoritative
        // for-sale market + a real-estate ownership registry.  Unrouted, a client's buy
        // never reaches the host, so the building stays for-sale for everyone else and a
        // second player can buy the SAME building (two owners).  This postfix (the buy
        // already ran locally) ratifies it: client → BuyRequest (host arbitrates + removes
        // it from the market, or denies → we roll back); host → record ownership directly
        // (its own native buy already updated its market, which the poll propagates).
        [HarmonyPatch(typeof(BizManPresentation), "SendBuyBuildingOffer")]
        public static class Patch_SendBuyBuildingOffer
        {
            static void Postfix(BizManPresentation __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;   // single player — nothing to do

                    var reg = RegOf(__instance);
                    if (reg == null) return;
                    // Only act if the purchase actually went through (enough money / valid offer).
                    bool bought; try { bought = reg.BuildingOwnedByPlayer; } catch { bought = false; }
                    if (!bought) return;
                    string key = GameStateReader.AddressKey(reg);

                    if (MPClient.IsConnected)
                    {
                        MPClient.RequestBuyBuilding(key);
                        Plugin.Logger.LogInfo($"[Patch] Client bought {key} locally + notifying host.");
                        return;
                    }
                    MPServer.BuildingRealEstateOwners[key] = "host";
                    Plugin.Logger.LogInfo($"[Patch] Host bought {key} (recorded in ownership registry).");
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Patch] Patch_SendBuyBuildingOffer: {ex.Message}"); }
            }

            static BuildingRegistration? RegOf(BizManPresentation pres)
            {
                object? biz = ReadMember(pres, "bizManBusiness");
                return biz == null ? null : ReadMember(biz, "buildingRegistration") as BuildingRegistration;
            }
            static object? ReadMember(object obj, string name)
            {
                var t = obj.GetType();
                var f = AccessTools.Field(t, name);
                if (f != null) return f.GetValue(obj);
                var p = AccessTools.Property(t, name);
                return p != null ? p.GetValue(obj) : null;
            }
        }

        // Shared reflection helper (enclosing-class scope) for patches that don't carry
        // their own copy.  BuildingRegistration is a class, so reading it is safe.
        private static object? ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var f = AccessTools.Field(t, name);
            if (f != null) return f.GetValue(obj);
            var p = AccessTools.Property(t, name);
            return p != null ? p.GetValue(obj) : null;
        }

        /// <summary>RealEstateSettings → its building's address key, via the private
        /// _bizMan.business.buildingRegistration chain.</summary>
        private static string? RealEstateSettingsKey(object settings)
        {
            var biz      = ReadMember(settings, "_bizMan");
            var business = biz == null ? null : ReadMember(biz, "business");
            var reg      = business == null ? null : ReadMember(business, "buildingRegistration") as BuildingRegistration;
            return reg == null ? null : GameStateReader.AddressKey(reg);
        }

        // ── Patch: RealEstateSettings.SetForSale / CancelForSale ──────────────
        // Listing/canceling a building for sale mutates only the LOCAL gi.buildingsForSale,
        // which the host's authoritative for-sale broadcast then overwrites — so a client's
        // listing or cancel never sticks.  Route both through the host so the authoritative
        // market reflects them (and thus every player sees the change).  Host actions need
        // no routing: the native change + the for-sale poll already propagate them.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.RealEstateSettings), "SetForSale")]
        public static class Patch_RealEstateSettings_SetForSale
        {
            static void Postfix(UI.Smartphone.Apps.BizMan.RealEstateSettings __instance)
            {
                try
                {
                    if (!MPClient.IsConnected) return;   // host/SP: native + for-sale poll handle it
                    string? key = RealEstateSettingsKey(__instance);
                    if (string.IsNullOrEmpty(key)) return;
                    var info = GameStatePatcher.GetForSaleInfo(key);
                    if (info == null) return;            // no price entered → native didn't list it
                    MPClient.RequestListForSale(info);
                    Plugin.Logger.LogInfo($"[Patch] Client listed {key} for sale + notifying host.");
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Patch] SetForSale: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.RealEstateSettings), "CancelForSale")]
        public static class Patch_RealEstateSettings_CancelForSale
        {
            static void Postfix(UI.Smartphone.Apps.BizMan.RealEstateSettings __instance)
            {
                try
                {
                    if (!MPClient.IsConnected) return;   // host/SP: native + for-sale poll handle it
                    string? key = RealEstateSettingsKey(__instance);
                    if (string.IsNullOrEmpty(key)) return;
                    MPClient.RequestCancelSale(key);
                    Plugin.Logger.LogInfo($"[Patch] Client canceled sale of {key} + notifying host.");
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Patch] CancelForSale: {ex.Message}"); }
            }
        }

        // ── Patch: RealEstateHelper.SimulateCompetitorBuyingPlayerBuildings ───
        // Runs in EVERY sim but only ever sells the LOCAL owner's listed buildings (the AI
        // pays the owner natively in their own sim — correct + per-player).  We don't gate
        // it; we route the RESULT: a prefix snapshots the owner's for-sale buildings, the
        // postfix sees which the AI bought, and tells the host to drop them from the
        // authoritative market + ownership registry (host clears its own directly).
        [HarmonyPatch(typeof(Helpers.RealEstateHelper), "SimulateCompetitorBuyingPlayerBuildings")]
        public static class Patch_SimulateCompetitorBuyingPlayerBuildings
        {
            static void Prefix(out System.Collections.Generic.List<string> __state)
            {
                __state = (MPServer.IsRunning || MPClient.IsConnected) ? GameStatePatcher.SnapshotOwnedForSale() : null;
            }
            static void Postfix(System.Collections.Generic.List<string> __state)
            {
                try
                {
                    if (__state == null || __state.Count == 0) return;
                    foreach (var addr in GameStatePatcher.DetectSold(__state))
                    {
                        if (MPServer.IsRunning) MPServer.BuildingRealEstateOwners.TryRemove(addr, out _);
                        else if (MPClient.IsConnected) MPClient.SendSaleCompleted(addr);
                    }
                }
                catch (System.Exception ex) { Plugin.Logger.LogWarning($"[Patch] SimulateCompetitorBuyingPlayerBuildings: {ex.Message}"); }
            }
        }

        // ── Patches: AI for-sale market generation → HOST-ONLY ────────────────
        // UpdateBuildingsForSale (lists new AI buildings) and SimulateCompetitorBuyingAIBuildings
        // (AI buys AI buildings) mutate the GLOBAL for-sale market with their own RNG.  The
        // host's market is authoritative and replace-synced to clients, so running these on
        // clients only causes transient divergence + wasted work — skip them client-side.
        [HarmonyPatch(typeof(Helpers.RealEstateHelper), "UpdateBuildingsForSale")]
        public static class Patch_UpdateBuildingsForSale_HostOnly
        {
            static bool Prefix() => !MPClient.IsConnected;   // host + single player run it; clients get the market via sync
        }

        [HarmonyPatch(typeof(Helpers.RealEstateHelper), "SimulateCompetitorBuyingAIBuildings")]
        public static class Patch_SimulateCompetitorBuyingAIBuildings_HostOnly
        {
            static bool Prefix() => !MPClient.IsConnected;
        }

        // ── Patch: GameManager.Update ─────────────────────────────────────────
        // Postfix runs AFTER the game's own Update — used to drain our main-thread action queue each frame.
        // NESTED class — REQUIRED so Plugin.cs actually applies it (see the rent
        // patch note above; method-level patches on bare MPPatches are skipped).
        [HarmonyPatch(typeof(GameManager), "Update")]
        public static class Patch_GameManager_Update
        {
            static void Postfix()
            {
                GameStatePatcher.DrainQueue();
                // (Removed the second timeScale-enforcement point 2026-06-18: it was speculative defense-in-depth
                //  with no specific issue behind it. MPCanvasUI.LateUpdate already re-applies timeScale every
                //  frame, and this Postfix runs AFTER the game's Update — so it never even protected
                //  RunMainGameTick's delta, which already ran in Update. The per-frame LateUpdate set suffices.)
            }

            // (GMShield Finalizer RETIRED 2026-06-12 dead-code sweep: zero
            //  swallows post-port — the mid-join NRE storm it bandaged was
            //  properly fixed by the fresh-start menu detour.)
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

        // ── Patch: IntroCharacterCustomizer.Start — name pre-fill (event-driven) ─
        // Fires ONCE when the character-creation screen is created; pre-fills the
        // name field with the F8-lobby name.  Replaced a per-frame FindObjectsOfType
        // poll that drained single-player (90->12fps) — see MPCanvasUI.PrefillIntroName.
        [HarmonyPatch(typeof(Intro.IntroCharacterCustomizer), "Start")]
        public static class Patch_IntroCharacterCustomizer_Prefill
        {
            static void Postfix(Intro.IntroCharacterCustomizer __instance)
            {
                try { MPCanvasUI.PrefillIntroName(__instance); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[IntroName] Start postfix: {ex.Message}"); }
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

                // Arm the client-side BlackOverlay suppressor: the overlay can only get stuck-on right after a
                // building-entry transition, so scan for it in a short window after entry (not forever).
                try { MPCanvasUI.ArmBlackOverlayScan(); } catch { }

                // Shop context for RemoteSale: whose shop is the player inside?
                // ([ShopGate] classification probe retired 2026-06-12 sweep —
                //  its discriminator shipped as the purchaser-enable fix.)
                try
                {
                    if (MPServer.IsRunning || MPClient.IsConnected)
                    {
                        var reg = __instance.buildingRegistration;
                        MPRegisterSync.SetCurrentShop(
                            reg != null ? reg.businessOwnerRivalId : "",
                            reg != null ? GameStateReader.AddressKey(reg) : "");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] shop context: {ex.Message}"); }

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
                    if (InteriorSync.TrySendOwnerSnapshotOnEntry(reg, addr)) return;
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
                    MPRegisterSync.SetCurrentShop("", "");   // left the building — RemoteSale context gone
                    if (!MPClient.IsConnected) return;
                    if (__instance == null) return;
                    var reg = __instance.buildingRegistration;
                    if (reg == null) return;
                    var addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr)) return;
                    InteriorSync.NotifyLocalBuildingExit(addr);
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
                // HOST ONLY (Backlog #7): the game clears traffic on building entry; on the host that
                // would wipe the street for every client still standing outside, so we no-op it.
                // The CLIENT must NOT block ClearTraffic — TrafficSync.SuppressLocalTraffic RELIES on it
                // to kill local Gley cars. The 2026-05-20 "Path X" client-side block was a diagnostic
                // that silently neutralised that suppression (→ leftover pushable cars, confirmed
                // 2026-06-16 [TrafCensus]); reverted to host-only 2026-06-16. SetPause/ClearTrafficOnArea
                // keep their client block for now (SetPause is the one tied to client building entry).
                if (MPServer.IsRunning)
                {
                    Plugin.Logger.LogInfo("[TMBlock] ClearTraffic() — SKIPPED (host MP active).");
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

        // ── Parked-car overlap: extend the game's ambient cleanup to CLIENT cars ──
        // The game clears ambient parked cars overlapping the LOCAL player's cars
        // (DestroyBlockingParkedVehicles → DestroyBlockingVehicles, iterating AllPlayerVehicles).
        // In MP that only covers the HOST's own cars, so an ambient car (re)generated onto a
        // CLIENT's parked car is never cleared → it syncs to the client as an overlap. Host-only
        // Postfix: after the native cleanup, run the SAME routine for each parked client-car ghost,
        // so the removal happens host-side and propagates to everyone via the parked-car sync.
        // Only ambient cars (ParkedVehiclesLayer) are ever released; a ghost (PlayerVehiclesLayer)
        // is just the reference point — see VehicleManager.CullBlockingAmbientAroundParkedGhosts.
        [HarmonyPatch(typeof(Helpers.VehicleHelper), "DestroyBlockingParkedVehicles")]
        public static class Patch_DestroyBlockingParkedVehicles_RemotePlayers
        {
            static void Postfix()
            {
                if (!MPServer.IsRunning) return;
                VehicleManager.CullBlockingAmbientAroundParkedGhosts();
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
                        weeklyIncomeHistory      = new System.Collections.Generic.List<System.Tuple<int, float>>(),
                        numberOfBusinessesHistory = new System.Collections.Generic.List<System.Tuple<int, int>>(),
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

            static void Postfix(System.Collections.Generic.IEnumerable<BigAmbitions.Rivals.RivalData> __result)
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
                    var list = __result as System.Collections.Generic.List<BigAmbitions.Rivals.RivalData>;
                    if (list == null)
                    {
                        Plugin.Logger.LogWarning("[Patch_GetAllRivalData] result not a List<RivalData>; skipping player injection.");
                        return;
                    }

                    // Defensive dedupe: ANY pre-existing entry with a player id
                    // is a stub from some other population path (the resurrected
                    // RivalDataCache leak grew one per player) — remove before
                    // appending our full synthetic rows so each player renders
                    // exactly once.
                    int purged = list.RemoveAll(rd =>
                    {
                        try
                        {
                            string rid = rd?.id ?? "";
                            return !string.IsNullOrEmpty(rid)
                                && (rid == MPConfig.PlayerId || GameStatePatcher.ClientPlayerRoster.ContainsKey(rid));
                        }
                        catch { return false; }
                    });
                    if (purged > 0)
                        Plugin.Logger.LogInfo($"[Patch_GetAllRivalData] purged {purged} stale player stub row(s).");

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

            static bool Prefix(BigAmbitions.Rivals.RivalData __0, int __1, System.Action<UnityEngine.Sprite> __2)
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

        // Host-authoritative AI economy (2026-06-19, bucket 2): the daily AI city-economy — new/closed AI
        // businesses, residential/warehouse rent-availability swaps, and per-neighborhood market demand — must
        // run on ONE machine, or each client evolves a different city. Suppress it on clients; the host's results
        // arrive via BusinessSync (AI business identity/prices + AvailableForRent) and the market snapshot
        // (importPriceIndex + demandValues, the latter added in Phase A1). NeighbourhoodStats need not sync —
        // only RunDaily (now skipped on clients) reads it. Host + single-player run it normally.
        [HarmonyPatch(typeof(Helpers.CompetitionHelper), "RunDaily")]
        public static class Patch_CompetitionHelper_RunDaily_SkipOnClient
        {
            static bool Prefix()
            {
                if (!MPClient.IsConnected) return true;
                Plugin.Logger.LogInfo("[Suppress] CompetitionHelper.RunDaily skipped on client (host-authoritative AI economy).");
                return false;
            }
        }

        // Host-authoritative market events (2026-06-19, bucket 2 follow-up — audit catch): NewDay also calls
        // ProductMarketHelper.RunDaily (SEPARATE from CompetitionHelper.RunDaily), which CREATES market events
        // (hype / shortage / max-providers) via RNG. Clients must NOT generate their own — they receive the
        // host's authoritative gi.marketEvents via the MarketEvents sync (Protocol MessageType.MarketEvents,
        // whose comment already assumes "clients suppress the generating sim"). Still clear the lowest-market-
        // price cache daily (the only other place it was cleared) so synced competitor prices are reflected in
        // price-acceptability. Host + single-player generate events normally.
        [HarmonyPatch(typeof(Helpers.ProductMarketHelper), "RunDaily")]
        public static class Patch_ProductMarketHelper_RunDaily_SkipOnClient
        {
            static bool Prefix()
            {
                if (!MPClient.IsConnected) return true;
                ItemHelper.ClearLmpCache();
                Plugin.Logger.LogInfo("[Suppress] ProductMarketHelper.RunDaily skipped on client (host-authoritative market events; LMP cache cleared).");
                return false;
            }
        }

        // MP season consistency (2026-06-19): SeasonalDecorations is a PER-MACHINE PlayerPref that gates which
        // seasonal item variants are sold (RemoveSeasonalItems + BuildingRegistration.GetItemNameBySeason).
        // Differing prefs would make players sell different seasonal items. Force it consistent (= on, the
        // normal seasonal behavior) for everyone in an MP session so the economy matches. Side effect: seasonal
        // decorations show in MP regardless of the local toggle; the toggle effectively no-ops in a session.
        [HarmonyPatch(typeof(PlayerPrefSettings), "SeasonalDecorations", MethodType.Getter)]
        public static class Patch_PlayerPrefSettings_SeasonalDecorations_ForceConsistent
        {
            static void Postfix(ref bool __result)
            {
                if (MPServer.IsRunning || MPClient.IsConnected) __result = true;
            }
        }

        // Anti-exploit (2026-06-19): a player could keep an item ASSIGNED to a shelf (so its retail price isn't
        // pruned by RemoveUnusedRetailPrices) while never actually stocking it, then set a phantom-low price. On
        // OTHER clients GetShelfFillState defaults to "full" for a player-owned shop (no AI goods-source), so
        // that price counted in GetLowestMarketPrice and dragged the neighbourhood price floor down for rivals
        // at zero inventory cost. Fix: for another player's shop, report the REAL shelf fill from the synced item
        // stock (InteriorSync Phase 2b rebuilds reg.itemInstances + cargo amounts). A priced-but-empty shelf now
        // reads 0 → its price is skipped in GetLowestMarketPrice → moving the floor requires actually stocking +
        // selling (legitimate competition). MP-only; own shops (return 1f earlier) + AI rivals are untouched.
        [HarmonyPatch(typeof(Controllers.PlayerItemPurchaser), "GetShelfFillState")]
        public static class Patch_PlayerItemPurchaser_GetShelfFillState_RealStockForPlayers
        {
            static void Postfix(string itemName, BuildingRegistration registration, ref float __result)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (registration == null || !GameStatePatcher.IsSessionPlayerBusiness(registration)) return;
                try
                {
                    bool stocked = false;
                    var items = registration.itemInstances;
                    if (items != null)
                        foreach (var ii in items.Values)
                        {
                            if (ii?.ItemCached == null) continue;
                            if ((ii.ItemCached.type & (BigAmbitions.Items.ItemType.PointOfSale | BigAmbitions.Items.ItemType.ShowcaseShelf)) == 0) continue;
                            var stock = ItemHelper.GetStockInstance(ii);
                            if (stock != null && stock.itemName == itemName && stock.amount > 0) { stocked = true; break; }
                        }
                    __result = stocked ? 1f : 0f;
                }
                catch { /* on any doubt leave the game's value — fail open, never over-suppress competition */ }
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

        // ── Building-hover pick guard — DEV-ONLY (two-instance testing) ───────
        // History: the "highlight leak" (the client showed building highlights
        // that tracked the HOST's mouse but not its target) was a SINGLE-MACHINE,
        // TWO-INSTANCE artifact — the unfocused instance still receives the OS
        // cursor position (as out-of-bounds coords, the mouse being over the other
        // window) and the game picks buildings with it.  The fix that actually
        // worked (user-confirmed) was THIS focus/out-of-bounds guard.  A separate
        // idle/ghost-vehicle "debounce" theorised for residual flicker barely
        // fired, was descoped, and broke the HOST's own highlighting in real play,
        // so it has been removed entirely.
        //
        // On SEPARATE machines each player has their own cursor, so the bleed is
        // impossible and building highlighting is fully local (the game's OnIoEnter
        // is local-visual; we never broadcast it).  So the RELEASE build carries NO
        // hover patch at all — native highlighting on host and client.  This guard
        // compiles ONLY into the DEV build, where two instances share one machine
        // and one OS cursor.
#if BAMP_DEV
        [HarmonyPatch(typeof(CityBuildingController), nameof(CityBuildingController.OnIoEnter))]
        public static class Patch_CBC_OnIoEnter_DevFocusGuard
        {
            static bool Prefix()
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                    // The UNFOCUSED instance (mouse is over the OTHER window) must
                    // not hover-pick — its cursor coords belong to the other game.
                    if (!UnityEngine.Application.isFocused) return false;
                    var m = UnityEngine.Input.mousePosition;
                    if (m.x < 0f || m.y < 0f || m.x >= UnityEngine.Screen.width || m.y >= UnityEngine.Screen.height)
                        return false;
                }
                catch { }
                return true;
            }
        }
#endif

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
                if (!MPServer.IsRunning && !MPClient.InMpGame) return;   // sticky gate: survives reconnect
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

        // ── Patch: PlayerActivityUI.HidePanel — the vanilla time-skip panel NEVER shows ──
        // Rest v5 (user-designed 2026-06-22): the vanilla PlayerActivityUI is a DEAD UI in MP — it must
        // never be visible under ANY condition; our dock (MPCanvasUI.TickRestUI) replaces it for EVERY
        // activity. The component keeps running (it drives the activity state machine) — only its
        // VISIBILITY is forced off: every HidePanel call becomes HidePanel(true) in MP, unconditionally.
        // This panel is used ONLY by IPlayerActivity types (Rest/Sleep/Work/Workout/Hygiene/Entertain/
        // Study/Swimming/Paid) — all time-skips our dock handles. The TAXI is a SEPARATE system
        // (TaxiSystem, not an IPlayerActivity) and never touches this panel, so there is nothing to
        // exempt. The old per-activity gate is exactly what let Paid + stale-detection states leak it.
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
                if (!MPServer.IsRunning && !MPClient.InMpGame) return;   // sticky gate: survives reconnect
                hide = true;   // vanilla time-skip panel is dead in MP — always hidden; our dock replaces it
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
                if (!MPServer.IsRunning && !MPClient.InMpGame) return true;   // sticky gate: survives reconnect
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
                if (!MPServer.IsRunning && !MPClient.InMpGame) return true;   // sticky gate: survives reconnect
                if (GameStateReader.AllowNativePauseCall) return true;
                if (!disabled) return true;                  // re-enabling is always fine
                if (_n++ < 5 || _n % 100 == 0)
                    Plugin.Logger.LogInfo($"[Pause] time-control lock suppressed (#{_n}).");
                return false;
            }
        }

        // ── Patch: BusinessHelper.IsBusinessOpen — owner-authoritative open truth ──
        // Open/closed used to be re-derived on every machine from a replicated schedule, so another player's
        // shop could read "closed" on your client even while its owner had it open. (The owner never notices:
        // CanEnterBuilding lets the OWNER in via RentedByPlayer, bypassing this check entirely.) Fix: the
        // machine that RUNS a business reports its own computed IsBusinessOpen (BusinessSync.OwnerOpenByAddress),
        // and here every OTHER machine returns that single synced truth instead of re-deriving. Scoped to shops
        // you don't run that have a KNOWN synced state; your own shops + AI shops fall through to the real
        // check. (2026-06-19, "can't enter friend's shop" bug)
        [HarmonyPatch(typeof(BusinessHelper), nameof(BusinessHelper.IsBusinessOpen), new Type[] { typeof(BuildingRegistration), typeof(int) })]
        public static class Patch_IsBusinessOpen_OwnerTruth
        {
            static bool Prefix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return true;   // SP — real check
                    if (buildingRegistration == null) return true;
                    if (buildingRegistration.RentedByPlayer) return true;          // my own shop — I'm the authority
                    string addr = GameStateReader.AddressKey(buildingRegistration);
                    if (string.IsNullOrEmpty(addr)) return true;
                    if (BusinessSync.OwnerOpenByAddress.TryGetValue(addr, out var st) && st != 0)
                    {
                        __result = (st == 1);   // 1 = open, 2 = closed — the owner's synced truth
                        return false;           // skip the local re-derivation
                    }
                }
                catch { }
                return true;   // unknown (AI shop / not yet synced) — fall through to the real check
            }
        }

        // ── Patch: BuildingHelper.CanEnterBuilding — DIAGNOSTIC for the shop-closed bug ──
        // Release-safe: fires ONLY when entry is DENIED for a business you don't run, logging the exact
        // open-check inputs on THIS client so an affected player's log pinpoints the cause (synced truth vs
        // raw schedule). Throttled per shop. Remove once the bug is confirmed fixed in the wild.
        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.CanEnterBuilding))]
        public static class Patch_CanEnterBuilding_ClosedDiag
        {
            private static readonly System.Collections.Generic.Dictionary<string, float> _nextLogAt = new();
            static void Postfix(Address address, bool __result)
            {
                try
                {
                    if (__result) return;                                   // entry allowed — nothing to diagnose
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;  // SP
                    var reg = BuildingHelper.GetBuildingRegistration(address);
                    if (reg == null || reg.RentedByPlayer) return;          // my own shop shouldn't read closed
                    string addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr)) return;
                    float now = UnityEngine.Time.unscaledTime;
                    if (_nextLogAt.TryGetValue(addr, out var due) && now < due) return;
                    _nextLogAt[addr] = now + 10f;                           // throttle per shop

                    int ownerState = BusinessSync.OwnerOpenByAddress.TryGetValue(addr, out var os) ? os : 0;
                    int dayCount   = reg.scheduleDays?.Count ?? -1;
                    var today      = BuildingHelper.GetTodaySchedule(reg);
                    string todayDesc;
                    if (today == null) todayDesc = "today=NULL";
                    else
                    {
                        string slots = "";
                        if (today.openingHourSlots != null)
                            foreach (var s in today.openingHourSlots) slots += $"[{s.startingHour}-{s.endingHour}]";
                        todayDesc = $"today.isOpen={today.isOpen} slots={(slots.Length == 0 ? "none" : slots)}";
                    }
                    Plugin.Logger.LogWarning($"[ShopClosedDiag] DENIED '{addr}' biz='{reg.BusinessName}' ownerState={ownerState}(0=unk/1=open/2=closed) temporarilyClosed={reg.temporarilyClosed} scheduleDays={dayCount} {todayDesc} now=h{SaveGameManager.Current.Hour}/{TimeHelper.GetDayOfWeek()} bizOwnerRival='{reg.businessOwnerRivalId}'");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[ShopClosedDiag] {ex.Message}"); }
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
                if (!MPServer.IsRunning && !MPClient.InMpGame) return true;   // sticky gate: survives reconnect
                // No taxi bypass: the ride machine is stopped instantly (taxi
                // v2 = instant arrival), so it must never self-advance either.
                return false;                                  // machine never self-advances in MP
            }
        }

        // ── Time-skip economy: simulate the shop the player is STANDING IN ────
        // During our consensus skip the TimeMachine is stopped (isRunning=false), so the game's
        // per-hour business sim (BusinessSimulatorHelper.RunHourly) EXCLUDES the customer-spawning
        // shop the player is physically inside — natively it relies on live customers for that one,
        // which can't keep pace with the fast clock, so it under-earns. Native fast-forward avoids
        // this because isRunning=true. We restore that view of isRunning ONLY for the duration of
        // RunHourly while a skip is active — tightly scoped so the ~two dozen OTHER isRunning
        // consumers (activity Cancel, hospital faint, energy/UI/tutorials) keep seeing the REAL
        // value (a global override would re-fire onTimeMachineEnded on the skip's abort path).
        // Outside a skip the flag is never set, so there is zero effect on normal play.
        public static bool ForceSimMachineRunning;

        [HarmonyPatch]
        public static class Patch_BusinessSimulator_RunHourly_SimOwnShop
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("BusinessSimulatorHelper");
                return t?.GetMethod("RunHourly",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }

            static void Prefix()
            {
                if (MPRestSync.SkipActive) ForceSimMachineRunning = true;
            }

            // Finalizer (not Postfix) so the flag is cleared even if the sim throws.
            static void Finalizer() { ForceSimMachineRunning = false; }
        }

        [HarmonyPatch]
        public static class Patch_TimeMachine_isRunning_SimScope
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("Timemachine.TimeMachine")
                     ?? VehicleManager.FindGameType("TimeMachine");
                return t?.GetMethod("get_isRunning",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            }

            static void Postfix(ref bool __result)
            {
                if (ForceSimMachineRunning) __result = true;
            }
        }

        // ── MP economy shields (2026-06-19) ───────────────────────────────────
        // Other players' shops carry a businessOwnerRivalId locally, so the game's AI city-economy treats them
        // as AI rivals — and it only spares RentedByPlayer (the LOCAL player's own). These guards stop the AI
        // from CLOSING another player's shop and stop one player from BUYING OUT another's. (Re-pricing and
        // re-valuation of player shops are already handled by Patch_NoRivalRepriceOnPlayerShops /
        // Patch_NoRivalValuationOnPlayerShops further below.) Competition (customer split) is untouched. MP-gated.

        // (1) Never let the AI economy shut down another player's business.
        [HarmonyPatch(typeof(BuildingRegistration), "ShutDownAIBusiness")]
        public static class Patch_BuildingRegistration_ShutDownAIBusiness_ShieldPlayers
        {
            static bool Prefix(BuildingRegistration __instance)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                if (GameStatePatcher.IsSessionPlayerBusiness(__instance))
                {
                    Plugin.Logger.LogInfo("[EconShield] blocked AI shutdown of another player's business.");
                    return false;
                }
                return true;
            }
        }

        // (2) Block buying out (overtaking) another PLAYER's shop — real AI rivals stay overtakeable.
        // A buyout is accepted only if the offer >= valuation * acceptRate; forcing acceptRate huge for a
        // player-owned shop makes every offer fall short BEFORE any money moves, so it can't be taken over.
        [HarmonyPatch(typeof(BigAmbitions.Rivals.RivalsHelper), "GetOvertakeBusinessAcceptRate")]
        public static class Patch_RivalsHelper_GetOvertakeBusinessAcceptRate_BlockVsPlayers
        {
            static void Postfix(string rivalId, ref float __result)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                if (GameStatePatcher.IsSessionPlayerRivalId(rivalId)) __result = float.MaxValue;
            }
        }

        // (5) Our synthetic on-duty staff has zero "demands", so the game's hourly satisfaction calc does
        // 0/0 → NaN. Skip it for the synthetic; it keeps its mod-set satisfaction (100). (2026-06-19.)
        [HarmonyPatch(typeof(Entities.EmployeeInstance), "UpdateSatisfaction")]
        public static class Patch_EmployeeInstance_UpdateSatisfaction_SkipSynthetic
        {
            static bool Prefix(Entities.EmployeeInstance __instance)
            {
                if (__instance?.id != null && __instance.id.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix))
                    return false;   // synthetic has no demands → 0/0 NaN; leave its satisfaction untouched
                return true;
            }
        }

        // ── Patch: EmployeeInstance.RunComplaintsHourly — guard + probe orphaned employees ──
        // An employee whose assignedAddress doesn't resolve to a BuildingRegistration makes the
        // native complaint message-build (UnfulfilledDemandsComplaint.GetComplaintMessageData →
        // GetBuildingRegistration → deref) throw an NRE.  That NRE escapes EmployeeHelper.RunHourly
        // and aborts the REST of the hourly economy tick — every employee AND every delivery step
        // that runs after it — i.e. "everyone stopped working, no deliveries" (bug-20260621-181535).
        // This is MP-ONLY (in single player an employee's workplace always exists), so it's a real
        // multiplayer data gap: an employee is pointing at a building this machine can't resolve.
        //
        // This is a BANDAID, by design: we skip that one employee's complaint processing (it can't
        // meaningfully complain about a missing workplace) so the tick survives — but we LOG IT LOUDLY
        // as a release-safe WARNING, because the guard hides the player-facing symptom and the ROOT
        // (why the building is unresolvable on the client) still needs fixing.  The [OrphanEmployee]
        // tag is meant to jump out of any future log so we notice this is still happening.
        [HarmonyPatch(typeof(Entities.EmployeeInstance), "RunComplaintsHourly")]
        public static class Patch_EmployeeInstance_RunComplaintsHourly_GuardOrphanBuilding
        {
            // per-employee-id real-time throttle so it stays visible without flooding the log
            private static readonly System.Collections.Generic.Dictionary<string, float> _lastLogged = new();

            static bool Prefix(Entities.EmployeeInstance __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return true;   // single player — run native
                    if (__instance == null) return true;

                    BuildingRegistration? reg = null;
                    try { reg = Helpers.BuildingHelper.GetBuildingRegistration(__instance.assignedAddress); } catch { reg = null; }
                    if (reg != null) return true;   // normal employee with a resolvable workplace — native runs

                    LogOrphan(__instance);
                    return false;   // skip this orphan's complaint processing → no NRE → the hourly tick lives
                }
                catch { return true; }   // the guard must never be the thing that breaks the tick
            }

            private static void LogOrphan(Entities.EmployeeInstance emp)
            {
                try
                {
                    string id = "?"; try { id = emp.id ?? "?"; } catch { }
                    float now = UnityEngine.Time.unscaledTime;
                    if (_lastLogged.TryGetValue(id, out var last) && now - last < 120f) return;   // ≤ once / 2 min / id
                    _lastLogged[id] = now;

                    string name = "?"; try { name = emp.characterData?.name ?? "?"; } catch { }
                    string addr = "?";
                    try { addr = $"{emp.assignedAddress.streetNumber} {emp.assignedAddress.streetName}"; } catch { }
                    bool synth = false; try { synth = id.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix); } catch { }
                    string role = MPServer.IsRunning ? "host" : "client";
                    Plugin.Logger.LogWarning(
                        $"[OrphanEmployee] GUARDED a complaint NRE on {role}: employee '{name}' (id={id}, synthetic={synth}) " +
                        $"is assigned to '{addr}' which has NO building registration here — skipped its complaints to keep the " +
                        $"hourly economy tick alive. ROOT DATA GAP TO FIX (not a real fix): an MP employee→building assignment is unresolvable.");
                }
                catch { }
            }
        }

        // (6) A disconnected player's RivalState lingers in gi.rivalStates, but GetRivalData refuses an
        // off-roster id → returns null → RivalsHelper.RunDaily dereferences it on the next day-roll (crash on
        // every machine). Pre-drop any rivalState whose RivalData no longer resolves: connected players resolve
        // via the GetRivalData fallback and AI rivals via the cache (both kept); only stale/orphan entries go.
        // MP-only. (2026-06-19.)
        [HarmonyPatch(typeof(BigAmbitions.Rivals.RivalsHelper), "RunDaily")]
        public static class Patch_RivalsHelper_RunDaily_DropOrphanRivalStates
        {
            static void Prefix()
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                var states = SaveGameManager.Current?.rivalStates;
                if (states == null) return;
                int removed = states.RemoveAll(rs => rs == null
                    || BigAmbitions.Rivals.RivalsHelper.GetRivalData(rs.rivalId) == null);
                if (removed > 0)
                    Plugin.Logger.LogInfo($"[RivalGuard] dropped {removed} stale/orphan rivalState(s) before RunDaily.");
            }
        }

        // Phase 2 — AHEAD-of-host freeze (clock-only). While TimeSync.AheadHeld (and not mid-skip), zero the
        // game-time tick's delta so the clock + economy HOLD until the host catches up. timeScale is untouched,
        // so the player + NPCs + traffic keep moving — only the clock/accrual pause. Never rewinds.
        [HarmonyPatch(typeof(GameManager), "RunMainGameTick")]
        public static class Patch_GameManager_RunMainGameTick_AheadFreeze
        {
            static void Prefix(ref float deltaTimeWithMultiplier)
            {
                // Gate on an active MP game: a stale AheadHeld must never freeze the clock/economy
                // in single-player after a disconnect.
                if ((MPServer.IsRunning || MPClient.InMpGame) && TimeSync.AheadHeld && !MPRestSync.SkipActive)
                    deltaTimeWithMultiplier = 0f;
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

        // ── Patch: GameManager.RunMidNightAutoSave ────────────────────────────
        // The game's midnight autosave (fired at in-game 23:00 from RunMainGameTick)
        // calls SaveGameManager.Save(MidnightSave) and — unlike the periodic
        // CheckAutoSave — is NOT gated by GameManager.preventAutoSave, so the mod's
        // SuppressNativeAutosave does not cover it.  Left alone, in an MP session it
        // writes a vanilla "Recover Midnight.hsg" into the SINGLE-PLAYER folder every
        // in-game day (the path redirect above is only live around a Load, never a
        // Save).  We don't SUPPRESS it — we RELOCATE it: skip the native save and
        // enqueue MidnightRecoverSave, which writes the same recover save into the MP
        // area (a manifest-less '<session>-recover' sibling), stripped + tracked like
        // every other MP save.  The gate matches the SuppressNativeAutosave lifecycle
        // (IsRunning || IsConnected), so a host-loss OFFLINE FORK (both false) lets
        // the native midnight save run unchanged.  Single player: prefix returns true.
        [HarmonyPatch(typeof(GameManager), "RunMidNightAutoSave")]
        public static class Patch_GameManager_RunMidNightAutoSave_RedirectInMp
        {
            static bool Prefix()
            {
                try
                {
                    if (MPServer.IsRunning || MPClient.IsConnected)
                    {
                        // Relocate the recover save into the MP area; defer to a clean
                        // frame so the strip+save+join doesn't run mid-hourly-tick.
                        GameStatePatcher.EnqueueOnMainThread(() => MPSaveCoordinator.MidnightRecoverSave());
                        return false;   // skip the native SP-folder midnight save
                    }
                }
                catch { }
                return true;             // single player / offline fork — native save runs
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

        // Escape-safety guard for our HasInputSelected override.  The patch above
        // can make GameManager.HasInputSelected() return true while NOTHING is
        // EventSystem-selected (our chat is a custom canvas input, not a Unity-
        // selected object).  The game's HandleEscapeClick then derefs
        // EventSystem.current.currentSelectedGameObject with no null check
        // (GameManager.cs:968) → an NRE that aborts Update on every Escape press —
        // the "stuck after exiting a car, Escape does nothing" field bug.  When we
        // detect that exact state, clear our chat focus/suppression and skip the
        // native body so it can't throw; Escape now ESCAPES our block instead of
        // crashing.  Inert when SuppressGameInput is false (returns true = run native).
        [HarmonyPatch(typeof(GameManager), "HandleEscapeClick")]
        public static class Patch_GM_HandleEscapeClick_NullGuard
        {
            static bool Prefix()
            {
                try
                {
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (MPChat.SuppressGameInput && (es == null || es.currentSelectedGameObject == null))
                    {
                        MPCanvasUI.ClearChatFocus();
                        return false;   // skip native — it would NRE on the null selection
                    }
                }
                catch { }
                return true;
            }
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


        // ── [ShelfGate] Wave-2 fix attempt #1 (evidence: ShopGate 2026-06-11).
        // Working shops carry a REAL rival GUID in businessOwnerRivalId; a
        // player-owned shop carries the PLAYER id, which has no RivalData
        // record (players are deliberately kept out of the rivals cache —
        // Wave-5 rollback guard), so the game's customer CTA never shows.
        // Narrow override: inside another LOBBY PLAYER's shop, force the shelf
        // CTA visible.  Instrumented: if the downstream pickup/basket path
        // ALSO resolves the rival record, the next run's log + user report
        // localize it — evidence either way in one run. ──────────────────────
        [HarmonyPatch]
        public static class Patch_ShelfGate_ShouldShow
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(Player.HUD.ItemInfoOverlays.ShelfCtaBehavior).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "ShouldShow") return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[ShelfGate] target: {ex.Message}"); }
                return null;
            }

            private static float _nextLog;

            static void Postfix(ref bool __result)
            {
                if (__result) return;                                     // native already allows
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                try
                {
                    string owner = MPRegisterSync.CurrentShopOwner;
                    if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) return;
                    if (!MPRestSync.AllPlayers().Contains(owner)) return; // AI shops keep native gates
                    __result = true;
                    if (UnityEngine.Time.unscaledTime >= _nextLog)
                    {
                        _nextLog = UnityEngine.Time.unscaledTime + 5f;
                        Plugin.Logger.LogInfo($"[ShelfGate] shelf CTA forced ON in '{owner}' shop (native gate said no).");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[ShelfGate] {ex.Message}"); }
            }
        }

        // ── [RegShield] register-order probe + hard-lock shield (2026-06-11).
        // Run evidence: in another player's shop the native queue ACCEPTED the
        // customer, then CashRegisterController.OnPlaceOrder threw an Il2Cpp
        // NRE mid-placement → "Waiting in queue" forever, cancel dead,
        // movement locked.  Probe half: log instance state on entry (which
        // reference is null — missing serving employee vs unresolvable player
        // rival record).  Shield half: FINALIZER swallows the NRE in
        // lobby-player shops ONLY and runs native OnOrderCancel so the buyer
        // dequeues cleanly instead of hard-locking.  AI shops untouched. ─────
        [HarmonyPatch]
        public static class Patch_RegisterOrder_Shield
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(Controllers.CashRegisterController).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "OnPlaceOrder") return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RegShield] target: {ex.Message}"); }
                return null;
            }

            static bool InPlayerShop()
            {
                string owner = MPRegisterSync.CurrentShopOwner;
                return !string.IsNullOrEmpty(owner) && owner != MPConfig.PlayerId
                       && MPRestSync.AllPlayers().Contains(owner);
            }

            // (Probe prefix REMOVED 2026-06-12 — purchase path verified; the
            //  finalizer below stays as a last-line shield should any native
            //  OnPlaceOrder run slip past the MP finalizer's conditions.)

            static Exception? Finalizer(Exception __exception, Controllers.CashRegisterController __instance)
            {
                if (__exception == null) return null;
                try
                {
                    if ((MPServer.IsRunning || MPClient.IsConnected) && InPlayerShop())
                    {
                        Plugin.Logger.LogWarning(
                            $"[RegShield] OnPlaceOrder threw in '{MPRegisterSync.CurrentShopOwner}' shop — " +
                            $"swallowed + cancelling the order to avoid the queue hard-lock: {__exception.Message}");
                        try { __instance.OnOrderCancel(); }
                        catch (Exception cx) { Plugin.Logger.LogWarning($"[RegShield] OnOrderCancel also failed: {cx.Message}"); }
                        return null;   // suppress — buyer dequeues instead of wedging
                    }
                }
                catch { }
                return __exception;    // anywhere else: native behavior untouched
            }
        }

        // ── [StaffEval] gate override (RESURRECTED 2026-06-12 for VISIBLE
        // STAFF NPCs at employee-duty stations — user ruling: invisible-NPC-as-
        // owner was wrong, visible-NPC-as-staff is right).  The staffing
        // evaluator's first gate refuses rival-translated shops (run-10
        // evidence); force it TRUE only for unstaffed EMPLOYEE-duty stations.
        // Everything downstream is native game code (shift lookup → spawn). ──
        [HarmonyPatch]
        public static class Patch_StaffEval_GateOverride
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(EmployeeStationController).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "ShouldUpdateEmployee" && m.DeclaringType == typeof(EmployeeStationController))
                            return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffEval] gate target: {ex.Message}"); }
                return null;
            }

            static void Postfix(EmployeeStationController __instance, ref bool __result)
            {
                if (__result) return;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                try
                {
                    if (!MPRegisterSync.IsEmployeeDutyStation(__instance.transform.position)) return;
                    bool unstaffed = false;
                    try { unstaffed = __instance.employeeInstance == null; } catch { }
                    if (!unstaffed) return;
                    __result = true;
                    Plugin.Logger.LogInfo("[StaffEval] ShouldUpdateEmployee FORCED TRUE (employee-duty station, unstaffed).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffEval] gate: {ex.Message}"); }
            }
        }

        // DIAG:INVESTIGATION(staff-spawn) — observe-only probe; logs the work-shift lookup to
        //   name the next gate if the employee spawn chain refuses. Remove when concluded.
#if BAMP_DEV
        // ── [StaffEval] shift-lookup probe (resurrected instrumentation: names
        // the next gate if the spawn chain still refuses). ────────────────────
        [HarmonyPatch]
        public static class Patch_StaffEval_ShiftProbe
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(EmployeeStationController).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "GetEmployeeWorkShift" && m.DeclaringType == typeof(EmployeeStationController))
                            return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffEval] shift target: {ex.Message}"); }
                return null;
            }

            static void Postfix(EmployeeStationController __instance, WorkShift? __result)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                try
                {
                    if (!MPRegisterSync.IsEmployeeDutyStation(__instance.transform.position)) return;
                    Plugin.Logger.LogInfo(__result == null
                        ? "[StaffEval] GetEmployeeWorkShift → null"
                        : $"[StaffEval] GetEmployeeWorkShift → shift(emp='{__result.employeeId}' station='{__result.itemInstanceId}' {__result.startingHour}-{__result.endingHour})");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffEval] shift probe: {ex.Message}"); }
            }
        }
#endif

#if BAMP_DEV
        // DIAG:INVESTIGATION(checkout-freeze 2026-06-19) — AI-shop "stuck in the register queue".
        //   [CheckoutProbe] OnPlaceOrder snapshots the register the instant the client places an order:
        //   is the shop AI or a player's? is a cashier present? an employee instance? did the player join
        //   the served queue (AI shops have NO self-purchase fallback, so joining = committed to a cashier).
        //   The paired shiftLookup probe logs whether the synced schedule even carries a work-shift for the
        //   register — distinguishing "no work-shift synced" from "no employee instance". Remove when resolved.
        [HarmonyPatch]
        public static class Patch_CashRegister_OnPlaceOrder_CheckoutProbe
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(Controllers.CashRegisterController).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "OnPlaceOrder" && m.DeclaringType == typeof(Controllers.CashRegisterController)) return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CheckoutProbe] order target: {ex.Message}"); }
                return null;
            }

            static void Postfix(Controllers.CashRegisterController __instance)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                try
                {
                    string owner = MPRegisterSync.CurrentShopOwner;
                    bool ownerIsPlayer = !string.IsNullOrEmpty(owner) && MPRestSync.AllPlayers().Contains(owner);
                    bool playerOwned = false;
                    try { playerOwned = InstanceBehavior<BuildingManager>.Instance.IsPlayerOwnedBusiness; } catch { }
                    bool cashier = false;     try { cashier = __instance.employee != null; } catch { }
                    bool empInst = false;     try { empInst = __instance.employeeInstance != null; } catch { }
                    bool joinedQueue = false; try { joinedQueue = __instance.playerCustomer != null; } catch { }
                    Plugin.Logger.LogInfo(
                        $"[CheckoutProbe] OnPlaceOrder shop='{MPRegisterSync.CurrentShopAddress}' owner='{owner}' ownerIsPlayer={ownerIsPlayer} " +
                        $"playerOwnedBiz={playerOwned} cashierPresent={cashier} employeeInstance={empInst} joinedServedQueue={joinedQueue}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CheckoutProbe] order: {ex.Message}"); }
            }
        }

        // [CheckoutProbe] shiftLookup — while inside a non-player-duty shop, log the register's synced
        //   work-shift lookup (does scheduleDays carry a shift+employeeId for now?) + employee-instance /
        //   cashier presence. Throttled 2s. Tells "no shift synced" apart from "no employee instance".
        [HarmonyPatch]
        public static class Patch_CashRegister_ShiftLookup_CheckoutProbe
        {
            private static float _next;

            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(EmployeeStationController).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "GetEmployeeWorkShift" && m.DeclaringType == typeof(EmployeeStationController)) return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CheckoutProbe] shift target: {ex.Message}"); }
                return null;
            }

            static void Postfix(EmployeeStationController __instance, WorkShift? __result)
            {
                if (!MPClient.IsConnected) return;
                try
                {
                    if (MPRegisterSync.IsEmployeeDutyStation(__instance.transform.position)) return;   // player-duty: covered by [StaffEval]
                    if (string.IsNullOrEmpty(MPRegisterSync.CurrentShopAddress)) return;                // only while inside a shop
                    if (UnityEngine.Time.unscaledTime < _next) return;
                    _next = UnityEngine.Time.unscaledTime + 2f;
                    bool empInst = false; try { empInst = __instance.employeeInstance != null; } catch { }
                    bool cashier = false; try { cashier = __instance.employee != null; } catch { }
                    Plugin.Logger.LogInfo(
                        $"[CheckoutProbe] shiftLookup shop='{MPRegisterSync.CurrentShopAddress}' owner='{MPRegisterSync.CurrentShopOwner}' " +
                        $"workShift={(__result == null ? "null" : $"emp='{__result.employeeId}' {__result.startingHour}-{__result.endingHour}")} employeeInstance={empInst} cashierPresent={cashier}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CheckoutProbe] shift: {ex.Message}"); }
            }
        }
#endif

        // ── Patch: VehicleParkingHelper.Start — neutralize on vehicle GHOSTS (2026-06-19).
        // A synced/remote vehicle GHOST is a stripped clone: StripVehicleComponents removes its
        // CarController (+ VariableCenterOfMass), but VehicleParkingHelper was left on it.  Native
        // Start() then derefs the now-missing CarController, and Update() derefs the resulting null
        // _carController EVERY FRAME the ghost is visible → a NullReferenceException per frame (heavy
        // log spam + per-frame cost; Unity isolates it so no gameplay break, but it buries real events
        // in the logs).  A real player vehicle ALWAYS has a CarController in its parents, so this fires
        // ONLY on ghosts: disable the (vestigial — player auto-park only; mod references it nowhere)
        // component so none of its methods run.  Real vehicles pass the check and Start runs unchanged.
        [HarmonyPatch(typeof(VehicleParkingHelper), "Start")]
        public static class Patch_VehicleParkingHelper_SkipOnGhost
        {
            static bool Prefix(VehicleParkingHelper __instance)
            {
                try
                {
                    if (__instance.GetComponentInParent<CarController>() == null)
                    {
                        __instance.enabled = false;
                        return false;   // ghost — skip native Start (it would NRE on the stripped CarController)
                    }
                }
                catch { }
                return true;            // real vehicle — run native Start unchanged
            }
        }

        // ── [MPSale] MP order finalizer (2026-06-12, user-approved design).
        // The native OnPlaceOrder decrements LOCAL replica stock and books
        // revenue into a LOCAL ledger — and NREs on the replica's missing
        // stock graph anyway (disasm: GetStockInstance /
        // CheckStockAvailableForEntries).  A player-shop sale is a
        // cross-machine transaction: charge the buyer HERE from the synced
        // store price table; stock + revenue land on the OWNER's machine via
        // RemoteSale.  Native code never runs in player shops; everything the
        // buyer sees (paid-marking, success close, callbacks) is the game's
        // own PurchaseUI machinery. ───────────────────────────────────────────
        [HarmonyPatch]
        public static class Patch_MPOrderFinalizer
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(Controllers.CashRegisterController).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "OnPlaceOrder" && m.DeclaringType == typeof(Controllers.CashRegisterController))
                            return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSale] target: {ex.Message}"); }
                return null;
            }

            // ── Service moment (user, 2026-06-12): native purchases have a
            // beat — you WALK UP to the register, the cashier rings you up,
            // THEN it's yours.  Order taken in the prefix; then WalkUp phase
            // (native first-customer spot, driven by the same NavMeshAgent the
            // activity system's MovingTowardsActivity uses — the genuine
            // full-service walk is welded to the employee queue we can't run),
            // then the ~2s Beat, then completion.  Cancelling the UI at any
            // point charges NOTHING.
            private const float SERVICE_SECONDS = 2.0f;
            private const float WALKUP_TIMEOUT  = 8.0f;
            private const float ARRIVE_DIST     = 1.1f;
            private enum Phase { None, WalkUp, Beat }
            private static Phase  _phase = Phase.None;
            private static float  _pendingAt;        // beat completion time
            private static float  _walkDeadline;
            private static float  _pendingTotal;
            private static string _pendingDesc = "";
            private static string _pendingOwner = "";
            private static string _pendingAddress = "";
            private static string _pendingAct0 = "none";
            private static UnityEngine.Vector3 _walkSpot;
            private static UnityEngine.Vector3 _registerPos;
            private static UnityEngine.AI.NavMeshAgent? _walkAgent;
            private static bool _agentWasEnabled;
            private static readonly System.Collections.Generic.List<SaleItem> _pendingItems = new();

            static bool Prefix(Controllers.CashRegisterController __instance,
                               System.Collections.Generic.List<BigAmbitions.Items.CargoInstance> orderedCargoInstances)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                string owner = MPRegisterSync.CurrentShopOwner;
                if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) return true;
                if (!MPRestSync.AllPlayers().Contains(owner)) return true;   // AI shops fully native
                try
                {
                    string act0 = "none";
                    try { act0 = MPRestSync.CurrentActivityName() ?? "none"; } catch { }

                    float total = 0f;
                    var desc = new System.Text.StringBuilder();
                    _pendingItems.Clear();
                    if (orderedCargoInstances != null)
                        for (int i = 0; i < orderedCargoInstances.Count; i++)
                        {
                            var c = orderedCargoInstances[i];
                            if (c == null) continue;
                            float price = MPRegisterSync.GetShopPrice(c.itemName);
                            if (price < 0f) price = (float)c.pricePerUnit;   // table miss → cargo stamp
                            total += c.amount * price;
                            if (desc.Length < 160) desc.Append($"{c.itemName} x{c.amount}, ");
                            _pendingItems.Add(new SaleItem { ItemName = c.itemName ?? "", Amount = c.amount });
                        }

                    _pendingTotal   = total;
                    _pendingDesc    = desc.ToString().TrimEnd(' ', ',');
                    _pendingOwner   = owner;
                    _pendingAddress = MPRegisterSync.CurrentShopAddress;
                    _pendingAct0    = act0;
                    _registerPos    = __instance.transform.position;

                    // Walk-up: the station's own first-customer spot, reached
                    // with the player's NavMeshAgent (the activity system's own
                    // mover).  Falls straight to the Beat on any failure.
                    _walkSpot = _registerPos;
                    bool haveSpot = false;
                    try
                    {
                        var spotT = __instance.GetFirstCustomerSpot();
                        if (spotT != null) { _walkSpot = spotT.position; haveSpot = true; }
                    }
                    catch { }
                    _walkAgent = null;
                    var ch = Helpers.PlayerHelper.PlayerController?.Character;
                    if (haveSpot && ch != null
                        && UnityEngine.Vector3.Distance(ch.transform.position, _walkSpot) > ARRIVE_DIST + 0.2f)
                    {
                        try
                        {
                            var agent = ch.GetComponent<UnityEngine.AI.NavMeshAgent>();
                            if (agent != null)
                            {
                                _agentWasEnabled = agent.enabled;
                                agent.enabled = true;
                                if (agent.isOnNavMesh && agent.SetDestination(_walkSpot)) _walkAgent = agent;
                                else agent.enabled = _agentWasEnabled;
                            }
                        }
                        catch (Exception wx) { Plugin.Logger.LogWarning($"[MPSale] walk-up agent: {wx.Message}"); }
                    }

                    if (_walkAgent != null)
                    {
                        _phase = Phase.WalkUp;
                        _walkDeadline = UnityEngine.Time.unscaledTime + WALKUP_TIMEOUT;
                        Plugin.Logger.LogInfo($"[MPSale] order taken (${total:F2}) — walking up to the register...");
                    }
                    else
                    {
                        BeginBeat();
                        Plugin.Logger.LogInfo($"[MPSale] order taken (${total:F2}) — ringing up...");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSale] order intake: {ex}"); }
                return false;   // the native finalizer NEVER runs in player shops
            }

            /// <summary>Walk-up arrived (or skipped) — face the counter, play
            /// the worker's ring-up on their avatar, start the beat clock.</summary>
            private static void BeginBeat()
            {
                _phase = Phase.Beat;
                _pendingAt = UnityEngine.Time.unscaledTime + SERVICE_SECONDS;
                try
                {
                    var ch = Helpers.PlayerHelper.PlayerController?.Character;
                    if (ch != null)
                    {
                        var look = _registerPos - ch.transform.position; look.y = 0f;
                        if (look.sqrMagnitude > 0.01f)
                            ch.transform.rotation = UnityEngine.Quaternion.LookRotation(look, UnityEngine.Vector3.up);
                    }
                }
                catch { }
                try
                {
                    int idx = RemotePlayerManager.ResolveTriggerIndex("UsingCashRegister");
                    if (idx >= 0) RemotePlayerManager.ApplyTrigger(_pendingOwner, idx);
                }
                catch { }
            }

            private static void ReleaseWalkAgent()
            {
                try
                {
                    if (_walkAgent != null)
                    {
                        if (_walkAgent.enabled && _walkAgent.isOnNavMesh) _walkAgent.ResetPath();
                        _walkAgent.enabled = _agentWasEnabled;
                    }
                }
                catch { }
                _walkAgent = null;
            }

            private static bool PanelStillOpen()
            {
                try { return UI.Purchase.PurchaseUI.IsPanelOpen; } catch { return false; }
            }

            /// <summary>Advances the pending sale (walk-up → beat → completion
            /// or abort).  Called ~1 Hz from MPRegisterSync.TickDuty — main
            /// thread.</summary>
            public static void TickPending()
            {
                if (_phase == Phase.None) return;

                if (_phase == Phase.WalkUp)
                {
                    if (!PanelStillOpen())
                    {
                        ReleaseWalkAgent();
                        _phase = Phase.None;
                        Plugin.Logger.LogInfo("[MPSale] buyer cancelled during the walk-up — nothing charged.");
                        return;
                    }
                    var ch = Helpers.PlayerHelper.PlayerController?.Character;
                    bool arrived = ch != null
                        && UnityEngine.Vector3.Distance(ch.transform.position, _walkSpot) <= ARRIVE_DIST;
                    if (arrived || UnityEngine.Time.unscaledTime >= _walkDeadline)
                    {
                        ReleaseWalkAgent();
                        BeginBeat();
                        Plugin.Logger.LogInfo(arrived
                            ? "[MPSale] at the counter — ringing up..."
                            : "[MPSale] walk-up timed out — ringing up anyway.");
                    }
                    return;
                }

                // Phase.Beat
                if (UnityEngine.Time.unscaledTime < _pendingAt) return;
                _phase = Phase.None;
                try
                {
                    bool open = false;
                    try { open = UI.Purchase.PurchaseUI.IsPanelOpen; } catch { }
                    if (!open)
                    {
                        Plugin.Logger.LogInfo("[MPSale] buyer cancelled during the service moment — nothing charged.");
                        return;
                    }

                    MPHub.ApplyMoneyDelta(-_pendingTotal, $"Purchase at {_pendingAddress}");

                    var sale = new RemoteSalePayload
                    {
                        BuyerId = MPConfig.PlayerId,
                        OwnerId = _pendingOwner,
                        Address = _pendingAddress,
                        Total   = _pendingTotal,
                        Desc    = _pendingDesc,
                    };
                    sale.Items.AddRange(_pendingItems);
                    if (MPServer.IsRunning) MPServer.HandleRemoteSale(sale, MPConfig.PlayerId);
                    else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.RemoteSale, MPConfig.PlayerId, sale));

                    // Native paid-marking + native SUCCESS close — fires the
                    // opener's completion callbacks (the state release).
                    try
                    {
                        var uiObj = UnityEngine.Object.FindObjectOfType(typeof(UI.Purchase.PurchaseUI));
                        var ui = (uiObj as UI.Purchase.PurchaseUI);
                        if (ui != null)
                        {
                            ui.SetCargoInstancesToPaid();
                            ui.Close((bool?)true);
                        }
                        else Plugin.Logger.LogWarning("[MPSale] PurchaseUI not found — paid, but UI not closed.");
                    }
                    catch (Exception ux) { Plugin.Logger.LogWarning($"[MPSale] UI close: {ux.Message}"); }

                    string act1 = "none";
                    try { act1 = MPRestSync.CurrentActivityName() ?? "none"; } catch { }
                    Plugin.Logger.LogInfo(
                        $"[MPSale] finalized: total=${_pendingTotal:F2} owner='{_pendingOwner}' items='{_pendingDesc}' activity '{_pendingAct0}'→'{act1}'.");

                    if (act1 != "none" && act1 == _pendingAct0)
                    {
                        Plugin.Logger.LogWarning($"[MPSale] activity '{act1}' persisted after close — invoking StandUp fallback.");
                        try { MPRestSync.StandUp(); } catch (Exception sx) { Plugin.Logger.LogWarning($"[MPSale] StandUp: {sx.Message}"); }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSale] completion: {ex}"); }
            }
        }

        // ── [SelfCheckout] (2026-06-12, user direction: "do the act identical
        // to the normal buying process and ring it up").  In another lobby
        // player's shop, clicking a register THAT PLAYER IS WORKING routes to
        // the game's own SELF-CHECKOUT flow (InteractAsSelfService →
        // MakeFullServiceSelfPurchase) instead of the employee-service queue
        // that cannot be served locally.  Native UI, native payment, native
        // bagging — the worker's avatar stands at the counter throughout. ─────
        [HarmonyPatch]
        public static class Patch_RegisterInteract_SelfCheckout
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(Controllers.CashRegisterController).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "Interact" && m.DeclaringType == typeof(Controllers.CashRegisterController))
                            return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[SelfCheckout] target: {ex.Message}"); }
                return null;
            }

            private static System.Reflection.MethodInfo? _selfService;
            private static float _lastDeclineLogAt;

            static bool Prefix(Controllers.CashRegisterController __instance, ref bool __result)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                try
                {
                    string owner = MPRegisterSync.CurrentShopOwner;
                    if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) return true;
                    if (!MPRestSync.AllPlayers().Contains(owner)) return true;          // AI shops native
                    if (!MPRegisterSync.IsStaffedByOtherPlayer(__instance.transform.position))
                    {
                        // Decline path (the "no employees" field bug): a real
                        // player's shop, but no one on duty at THIS register — fall
                        // through to native.  Logged (throttled) so the failure is
                        // VISIBLE instead of inferred from a missing success line.
                        if (UnityEngine.Time.unscaledTime - _lastDeclineLogAt > 1.5f)
                        {
                            _lastDeclineLogAt = UnityEngine.Time.unscaledTime;
                            Plugin.Logger.LogInfo($"[SelfCheckout] DECLINED in '{owner}' shop — no one on duty at this register (native 'no employees' path). pos={__instance.transform.position}");
                        }
                        return true;
                    }

                    if (_selfService == null)
                        foreach (var m in typeof(Controllers.CashRegisterController).GetMethods(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                            if (m.Name == "InteractAsSelfService") { _selfService = m; break; }
                    if (_selfService == null)
                    {
                        Plugin.Logger.LogWarning("[SelfCheckout] InteractAsSelfService not found — falling through native.");
                        return true;
                    }
                    object? r = _selfService.Invoke(__instance, null);
                    __result = r is bool rb && rb;
                    Plugin.Logger.LogInfo($"[SelfCheckout] routed register click to self-checkout in '{owner}' shop → {__result}.");
                    return false;   // skip the native employee-service path entirely
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[SelfCheckout] {ex.Message} — falling through native.");
                    return true;
                }
            }
        }

        // ── [RegGuard] queue-entry guard (2026-06-11; extended to AI shops 2026-06-19).  In another player's
        // shop the native queue happily accepts a customer even when NO serving
        // entity exists locally → OnPlaceOrder NREs → hard lock (two runs).
        // Block CanOrder until the register actually has an employeeInstance
        // (the synthetic-staffing NPC once it spawns and assigns itself).
        // Safe failure mode: buyer simply can't queue yet — no lock. ──────────
        [HarmonyPatch]
        public static class Patch_RegisterQueue_Guard
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                try
                {
                    foreach (var m in typeof(Controllers.CashRegisterController).GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        if (m.Name == "CanOrder") return m;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RegGuard] target: {ex.Message}"); }
                return null;
            }

            private static float _nextLog;

            static void Postfix(Controllers.CashRegisterController __instance, ref bool __result)
            {
                if (!__result) return;
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                try
                {
                    // Skip the receiver's OWN shop — guard EVERYTHING else (AI shops AND other
                    // players' shops) the same way.  Key on IsPlayerOwnedBusiness, NOT the owner id:
                    // an AI shop can carry an EMPTY businessOwnerRivalId on the client, so the old
                    // owner-string gate let unstaffed AI registers through and the buyer hard-locked
                    // (no self-purchase fallback exists for AI shops — bug 2026-06-18).
                    var bm = InstanceBehavior<BuildingManager>.Instance;
                    if (bm == null || bm.buildingRegistration == null) return;   // not inside a shop → native
                    bool myShop = false;
                    try { myShop = bm.IsPlayerOwnedBusiness; } catch { }
                    if (myShop) return;                                          // my own shop → native
                    // Another player actively working THIS register → the Interact prefix routes to
                    // self-checkout; CanOrder must stay native so the customer path engages.
                    if (MPRegisterSync.IsStaffedByOtherPlayer(__instance.transform.position)) return;
                    bool staffed = false;
                    try { staffed = __instance.employeeInstance != null; } catch { }
                    if (staffed) return;                                         // a real cashier mans it (AI via synced shift, or synthetic) — allow
                    __result = false;
                    if (UnityEngine.Time.unscaledTime >= _nextLog)
                    {
                        _nextLog = UnityEngine.Time.unscaledTime + 5f;
                        // Native "there's no one at the register" toast — the
                        // same key the game shows for an unstaffed hairdresser
                        // till.  Accurate model (user 2026-06-12): no cashier =
                        // no checkout, clearly said, nothing locks.
                        try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Error, "notification_cashregister_no_employee"); }
                        catch { }
                        Plugin.Logger.LogInfo($"[RegGuard] blocked queue at '{MPRegisterSync.CurrentShopAddress}' (owner='{MPRegisterSync.CurrentShopOwner}') — no cashier (notified buyer).");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RegGuard] {ex.Message}"); }
            }
        }

        // ── [StationLock] IRS tax counter — never hard-lock at an unstaffed counter (2026-06-23) ──
        // IRSStationController.OnOrderCancel does `if (employee.customer == playerCustomer)`; on a client
        // where the counter is unstaffed (employee == null) that NREs → the cancel fails → the player is
        // stuck (can't escape, ALT+F4 — the reported bug).  PRIMARY fix is reliable staffing (GameStatePatcher
        // re-runs AI staffing after the interior re-load).  This is the BACKSTOP: if a counter is somehow
        // still unstaffed, do the safe cleanup ourselves (dequeue + close the modal) so the player always
        // escapes.  Staffed (employee != null) → native runs unchanged.  (Hairdresser/coat-check/cinema
        // cancels are already null-safe; the cash register is covered by RegGuard.  IRS is the gap.)
        [HarmonyPatch(typeof(Controllers.IRSStationController), "OnOrderCancel")]
        public static class Patch_IRSCancel_NeverHardLock
        {
            static bool Prefix(Controllers.IRSStationController __instance)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;   // single player / offline → native
                try
                {
                    if (__instance.employee != null) return true;               // staffed → native cancel is safe
                    // Unstaffed: native would NRE on employee.customer.  Replicate the safe tail ourselves.
                    if (__instance.playerCustomer != null)
                    {
                        try { __instance.GetWaitingLine().customersManagement.RemoveCustomer(__instance.playerCustomer); } catch { }
                        try { __instance.playerCustomer.UnsubscribeToGlobalEvents(); } catch { }
                        try { UnityEngine.Object.Destroy(__instance.playerCustomer); } catch { }
                    }
                    try { InstanceBehavior<UI.UIs>.Instance.playerHUD.purchaseUI.Close(); } catch { }
                    Plugin.Logger.LogInfo("[StationLock] IRS cancel at an UNSTAFFED counter — safe-dequeued + closed UI (prevented hard-lock).");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StationLock] IRS cancel guard: {ex.Message}"); return true; }
            }
        }

        // ── Honorary-degree training (user spec 2026-06-12) ───────────────────
        // In MP nobody sits at a desk for tens of game-hours: the school door
        // opens OUR confirm dialog (course + FULL cost); Accept charges the
        // whole remaining course through the game's own tuition transaction
        // and credits completion via the native diploma fields + event.  The
        // StudyActivity never starts — this Prefix swallows it at the single
        // funnel every activity passes through.
        [HarmonyPatch(typeof(PlayerActivity.PlayerActivityUI), "Show",
            new Type[] { typeof(PlayerActivity.IPlayerActivity), typeof(EntityController) })]
        public static class Patch_TrainingDoor_HonoraryDegree
        {
            static bool Prefix(PlayerActivity.IPlayerActivity playerActivity)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                try
                {
                    if (playerActivity is PlayerActivity.StudyActivity)
                    {
                        MPCanvasUI.RequestTrainingDialog(playerActivity);
                        return false;
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Train] door intercept: {ex.Message}"); }
                return true;
            }
        }

        // ── Replicated shops are NOT rival businesses (2026-06-12) ────────────
        // The game auto-adopts every non-player-owned business: entering one
        // spawns AI cashiers at every station (SetupAiEmployeeStations — the
        // "employee at the counter though no one was hired" user bug) and the
        // daily rival sim re-prices it (CompetitionHelper — caught by the
        // auditor as persistent biz divergence).  Neither path validates the
        // owner id, so SESSION-PLAYER shops fall through.  Gate both.

        [HarmonyPatch(typeof(BuildingManager), "SetupAiEmployeeStations")]
        public static class Patch_NoAiStaffInPlayerShops
        {
            static bool Prefix(BuildingManager __instance)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;
                try
                {
                    var reg = __instance.buildingRegistration;
                    if (!GameStatePatcher.IsSessionPlayerBusiness(reg)) return true;
                    Plugin.Logger.LogInfo($"[Patcher] AI-staff spawn suppressed in session player's shop '{GameStateReader.AddressKey(reg)}' (owner='{reg.businessOwnerRivalId}').");
                    return false;   // skip native AI staffing — MPRegisterSync owns staffing visuals
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(Helpers.CompetitionHelper), "ShouldRecalculateRetailPrices")]
        public static class Patch_NoRivalRepriceOnPlayerShops
        {
            static void Postfix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                if (!__result || (!MPServer.IsRunning && !MPClient.IsConnected)) return;
                try
                {
                    if (GameStatePatcher.IsSessionPlayerBusiness(buildingRegistration))
                    {
                        __result = false;   // owner's synced price table is the only truth
                        Plugin.Logger.LogInfo($"[Patcher] rival re-price suppressed for session player's shop '{GameStateReader.AddressKey(buildingRegistration)}'.");
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Helpers.CompetitionHelper), "ShouldUpdateDailyValuation")]
        public static class Patch_NoRivalValuationOnPlayerShops
        {
            static void Postfix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                if (!__result || (!MPServer.IsRunning && !MPClient.IsConnected)) return;
                try
                {
                    if (GameStatePatcher.IsSessionPlayerBusiness(buildingRegistration))
                        __result = false;
                }
                catch { }
            }
        }

        // ── Replicated-shop shelf fill (2026-06-12) ───────────────────────────
        // Our buyer-side purchaser enable gives shelves native hover/take, but
        // PlayerItemPurchaser.UpdatePriceInfo then pins NON-rented shelves to
        // the AI fill model (GetShelfFillState = 1f for non-AI businesses) —
        // the user saw a FULL shelf while the owner's was half stocked.  In
        // session-player shops the cargo is REAL, so restore the cargo-driven
        // fill (ShowcaseShelfController.UpdateVisuals: amount / capacity)
        // after every UpdatePriceInfo.  ShowItemVisuals early-returns for an
        // unchanged item name, so this never re-rolls the stock-look shuffle.
        [HarmonyPatch(typeof(Controllers.PlayerItemPurchaser), "UpdatePriceInfo")]
        public static class Patch_ReplicatedShelfFill
        {
            static void Postfix(Controllers.PlayerItemPurchaser __instance)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                try
                {
                    var ic = __instance.itemController;
                    var ssc = ic as Items.SpecialItems.ShowcaseShelfController;
                    if (ssc == null) return;
                    var reg = ItemHelper.GetBuildingRegistration(ic.ItemInstance);   // global-ns helper
                    if (reg == null) return;
                    if (!GameStatePatcher.IsReplicatedInterior(GameStateReader.AddressKey(reg))) return;
                    ssc.UpdateVisuals();
                }
                catch { }
            }
        }

    }
}
