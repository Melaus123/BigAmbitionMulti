using System;
using HarmonyLib;
using Buildings;
using Helpers;
using GleyTrafficSystem;
using Extensions;   // phase 4a: the game's own ToShortCurrencyFormat for the company figures
using Localizor;    // U2: the string .GetLocalization() the four dropdown builders use

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

        // RENT-DIAG: addresses already reported this session.
        private static readonly System.Collections.Generic.HashSet<string> _rentDiagSeen = new System.Collections.Generic.HashSet<string>();

        private static void RentDiag(Building building, float dailyRent, float lastDeposit)
        {
            try
            {
                if (building == null) return;
                string addr = GameStateReader.AddressKey(building);
                if (!_rentDiagSeen.Add(addr)) return;
                string native = "-", sqm = "-", nbh = "-";
                try { native = building.GetBuildingDailyMarketRent().ToString(); } catch { }
                try { sqm = Buildings.BuildingSizeHelper.GetData(building.BuildingSize).squareMeters.ToString(); } catch { }
                try { nbh = building.Neighbourhood ?? "-"; } catch { }
                Plugin.Logger.LogInfo($"[RentDiag] {addr} native={native} quoted={dailyRent:F0} deposit={lastDeposit:F0} sqm={sqm} nbh={nbh}");
            }
            catch (Exception ex) { try { Plugin.Logger.LogWarning($"[RentDiag] {ex.Message}"); } catch { } }
        }

        [HarmonyPatch(typeof(BuildingHelper), nameof(BuildingHelper.RentBuilding))]
        public static class Patch_RentBuilding
        {
            static bool Prefix(Building building, float dailyRent, float lastDeposit)
            {
                // Not in multiplayer session — let it run normally
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;

                // Server-confirmed execution dispatched by GameStatePatcher — allow it.
                if (SuppressNextRentRequest) { SuppressNextRentRequest = false; return true; }

                // RENT-DIAG (2026-09-17): one line per address per session. A native != quoted gap names a
                // LOCAL rent mod on this machine — the two players are then renting at different prices and
                // every downstream rent figure diverges. Anything unreachable prints '-'.
                RentDiag(building, dailyRent, lastDeposit);

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
                // Round-267 (field 20260815-134731): the owner-interior publisher enrolls on
                // BUILDING ENTRY — a client renting the building they were ALREADY INSIDE fired
                // the entry edge before ownership, so nothing they placed ever published and the
                // host copy froze at the starter set ("23/0" audit) until a leave+re-enter.
                // Live-read the current building at the commitment instead of trusting the
                // stale entry edge; a rent from outside changes nothing (next entry enrolls).
                if (MPClient.IsConnected && !MPServer.IsRunning)
                {
                    try
                    {
                        var curReg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                        if (curReg != null)
                        {
                            string curAddr = GameStateReader.AddressKey(curReg);
                            if (!string.IsNullOrEmpty(curAddr) && curAddr == GameStateReader.AddressKey(building)
                                && InteriorSync.TrySendOwnerSnapshotOnEntry(curReg, curAddr))
                                Plugin.Logger.LogInfo($"[InteriorSync] owner publisher enrolled at RENT for '{curAddr}' — rented while inside (round-267).");
                        }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[InteriorSync] rent-time enroll: {ex.Message}"); }
                }

                // D4 (fold V6): this machine just RENTED something — its own access answer changed, and
                // no grant/ownership refresh is coming to say so. Drop the cached icon verdicts.
                try { HamptonsAccess.InvalidateIconVerdicts(); } catch { }

                if (!MPServer.IsRunning) return;
                if (SuppressNextRentRequest) return;   // already handled

                var key = GameStateReader.AddressKey(building);
                MPServer.BuildingOwners[key] = "host";
                MPServer.BroadcastRentConfirmToClients(key, dailyRent, lastDeposit);
                MPServer.RefreshBuildingAccess();   // housing: guests granted housing can now enter this newly-rented building
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
                    // D4 (fold V6): the mirror of the rent site — a local terminate changes our own
                    // access answer, on either role, before any ledger event reaches us.
                    try { HamptonsAccess.InvalidateIconVerdicts(); } catch { }

                    if (MPClient.IsConnected)
                    {
                        MPClient.RequestVacateBuilding(key);
                        Plugin.Logger.LogInfo($"[Patch] Client unrented {key} locally + notifying host.");
                        return;
                    }
                    // Host — released locally already; clear ownership + tell clients.
                    MPServer.BuildingOwners.TryRemove(key, out _);
                    MPServer.BroadcastVacate(key);
                    MPServer.RefreshBuildingAccess();   // housing: drop guests' access to this now-vacated building
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
                    // Round-159: record the REAL id, never the literal "host" — after a host handoff
                    // "host" means whoever hosts NOW (a previous host's deed would silently transfer),
                    // and the ledger shield explicitly ignores "host" entries.
                    MPServer.BuildingRealEstateOwners[key] = MPConfig.PlayerId;
                    MPServer.RefreshBuildingAccess();   // housing: guests granted housing can now enter this newly-bought building
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
            static bool Prefix() => !MPClient.IsClientInWorld;   // host + single player run it; clients get the market via sync
        }

        [HarmonyPatch(typeof(Helpers.RealEstateHelper), "SimulateCompetitorBuyingAIBuildings")]
        public static class Patch_SimulateCompetitorBuyingAIBuildings_HostOnly
        {
            static bool Prefix() => !MPClient.IsClientInWorld;
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
                // 284b (verifier F-4): note the pending press BEFORE applying it locally — a
                // heartbeat draining between SetManualPause and SendManualPause's own note
                // could converge the just-set state away.  SendManualPause re-notes the same
                // value (refreshing the stamp), which is harmless.
                if (!MPServer.IsRunning && MPClient.IsConnected) TimeSync.NotePendingLocalPause(paused);
                TimeSync.SetManualPause(paused);
                if (MPServer.IsRunning)        MPServer.SetDeliberatePause(paused);
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

        // ── Round-60: register income during a consensus skip ────────────────
        // (Round-52 REWRITTEN 2026-07-23 — the original two-patch design was INERT:
        // the SimScope isRunning-getter fake was INLINED out of the native RunHourly
        // body by the Mono JIT, and this postfix's own gate read the same fake and
        // stood down. Field symptom: the shop the register worker occupies earned $0
        // during every MP skip while vanilla's time machine paid normally — KuyuSuyu
        // x3; localized by probe runs 1-7, fixed and probe-verified same day: MP skip
        // income now matches the SP control, ~$2.5-2.8k/day on the test shop.)
        //
        // The game's hourly abstract simulator (BusinessSimulatorHelper.RunHourly)
        // skips the customer-spawning shop the LOCAL player is standing in unless the
        // native time machine runs — that bypass is how SP register fast-forward pays.
        // Our consensus skip drives RunMainGameTick directly with the machine stopped,
        // so this postfix simulates exactly the shop native exempted, per machine for
        // its own player, while OUR skip is active. Staffing is native-correct (ad-hoc
        // register worker via IsPlayerWorkingInEmployeeStation; a remote helper's
        // register via the synthetic duty employee's blanket 0-24 shift). No double
        // income: the sim and the live spawner share the CustomerEntry list and
        // per-order-entry processed flags. The real-machine gate reads the isRunning
        // BACKING FIELD, never the getter — patching or reading a trivial getter is
        // unreliable on Mono (inlining), the exact trap that broke the original.
        [HarmonyPatch(typeof(BusinessSimulatorHelper), nameof(BusinessSimulatorHelper.RunHourly))]
        public static class Patch_RunHourly_SimulateOccupiedShopDuringSkip
        {
            private static System.Reflection.FieldInfo _tmBackingField;
            private static bool TimeMachineReallyRunning()
            {
                try
                {
                    var tm = InstanceBehavior<UI.UIs>.Instance?.timeMachine;
                    if (tm == null) return false;
                    _tmBackingField ??= HarmonyLib.AccessTools.Field(tm.GetType(), "<isRunning>k__BackingField");
                    if (_tmBackingField == null) return tm.isRunning;   // fallback: the getter
                    return _tmBackingField.GetValue(tm) is bool b && b;
                }
                catch { return false; }
            }

            private static int _simmed;
            static void Postfix()
            {
                try
                {
                    if (!MPRestSync.SkipActive) return;
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                    var current = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    if (current == null || !current.RentedByPlayer) return;
                    if (!MergerFlip.TrulyMine(current)) return;          // only MY shop — replicas sim on their owner's machine
                    var data = BusinessTypeHelper.GetData(current);
                    if (data?.simulator == null || !data.spawnCustomers) return;   // !spawnCustomers shops already simulated natively
                    if (TimeMachineReallyRunning()) return;              // a GENUINE machine covers it natively
                    if (!BusinessHelper.IsBusinessOpen(current)) return;
                    data.simulator.SetUp(current, SaveGameManager.Current.Hour);
                    data.simulator.SimulateCurrentHour();
                    _simmed++;
                    if (_simmed == 1 || _simmed % 12 == 0)
                        Plugin.Logger.LogInfo($"[Rest] occupied-shop hourly sim during skip: '{current.BusinessName}' h{SaveGameManager.Current.Hour} (#{_simmed}) — SP time-machine parity (round-60).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Rest] occupied-shop sim: {ex.Message}"); }
            }
        }

        // ── Round-60b: normal-speed customer VISUALS during a consensus skip ─────
        // During a skip the world stays visible (no native TM blur) while the clock
        // runs at 25 game-min/s — the interior spawner's 1s check finds every entry
        // that came due in the last ~25 game-minutes and spawns them ALL, flooding
        // the shop with bodies that real-time serving can never clear (user report
        // 2026-07-23). The player moves at normal speed during a skip, so the world
        // should LOOK like normal speed (user design): bodies arrive at the rate
        // this shop, this hour, would produce at the NORMAL clock — the hour's
        // demand spread over the hour, converted through the game's own
        // MinutesMultiplier. Busy hours look busy, dead hours look dead. The
        // economy is unaffected: entries denied a body are still consumed by the
        // native loop and billed by the round-60 hourly sim (per-order-entry flags
        // dedup the two paths) — income identical, visuals consistent.
        [HarmonyPatch(typeof(IndoorCustomerSpawner), "CanSpawnCustomer")]
        public static class Patch_IndoorSpawner_SkipVisualPace
        {
            private const float MinSecondsBetween = 1.5f;   // burst guard (rush hours)
            private const float MaxSecondsBetween = 90f;    // dead-hour ceiling

            private static float _nextAllowed;
            private static int _cachedHour = -1;
            private static string _cachedAddr = "";
            private static float _cachedInterval = 8f;
            private static System.Reflection.FieldInfo _minMultField;

            /// <summary>The game's normal clock rate (game-minutes per real second) —
            /// GameManager.MinutesMultiplier, the exact factor normal play runs at.</summary>
            private static float NormalMinutesPerRealSecond()
            {
                try
                {
                    _minMultField ??= HarmonyLib.AccessTools.Field(typeof(GameManager), "MinutesMultiplier");
                    if (_minMultField?.GetValue(null) is float m && m > 0.05f) return m;
                }
                catch { }
                return 1f;
            }

            /// <summary>Real seconds between arrivals THIS shop/hour would show at normal
            /// speed: (60 / entries-this-hour) game-minutes between customers, divided by
            /// the normal game-min-per-real-second rate. Recomputed on shop/hour change.</summary>
            private static float CurrentNormalPaceInterval()
            {
                try
                {
                    var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    if (reg == null) return 8f;
                    int hour = SaveGameManager.Current.Hour;
                    string addr = "";
                    try { addr = GameStateReader.AddressKey(reg); } catch { }
                    if (hour != _cachedHour || addr != _cachedAddr)
                    {
                        _cachedHour = hour; _cachedAddr = addr;
                        int n = 0;
                        try
                        {
                            foreach (var e in AI.Customers.CustomerEntries.CustomerEntriesHelper.GetEntriesByAddress(reg.Address))
                                if (e != null && e.spawnTime.Hour == hour) n++;
                        }
                        catch { }
                        float gameMinBetween = 60f / UnityEngine.Mathf.Max(1, n);
                        _cachedInterval = UnityEngine.Mathf.Clamp(gameMinBetween / NormalMinutesPerRealSecond(), MinSecondsBetween, MaxSecondsBetween);
                    }
                    return _cachedInterval;
                }
                catch { return 8f; }
            }

            static void Postfix(ref bool __result)
            {
                try
                {
                    if (!__result) return;
                    // STATELESS by design (suppression-lapse class, round-56): the throttle
                    // exists only inside this call while SkipActive reads true — there is no
                    // latch to un-stick when a skip ends. The residual timestamp is zeroed
                    // whenever we're NOT skipping so the next skip starts with a clean slate.
                    if (!MPRestSync.SkipActive) { _nextAllowed = 0f; return; }
                    if (!MPServer.IsRunning && !MPClient.IsConnected) { _nextAllowed = 0f; return; }
                    if (UnityEngine.Time.unscaledTime < _nextAllowed) { __result = false; return; }
                    _nextAllowed = UnityEngine.Time.unscaledTime + CurrentNormalPaceInterval();
                }
                catch { }
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

        // ── Private driver hail (NEW IN GAME 1.0; comment rewritten 2026-08-29 after field proof) ──
        // 1.0's Onyx private driver is a SECOND door onto the shared TaxiSystem machinery:
        // PrivateDriverVehicle implements ITaxi and rides TravelTo/TravelCoroutine, so the ride, the
        // time skip and the arrival were already covered. Its click entry point is
        // OnClickToSetDestination (Helpers/PrivateDriverVehicle.cs:228), which this patch hooks.
        //
        // WHAT THIS PATCH ACTUALLY DOES TODAY: nothing, by design, and that is the correct outcome.
        // TrafficSync.OnLocalTaxiHailed now resolves the hail from the GHOST TABLE ONLY, and a
        // private driver can never be a ghost: ghosts are cloned from Gley pool prefabs, whereas
        // PrivateDriverVehicle is AddComponent'd onto a car the CLIENT summons locally
        // (PrivateDriverHelpers.cs:126/168). So every private-driver click takes the "not a
        // host-mirrored ghost - not sending" branch and logs there.
        //
        // THE ORIGINAL COMMENT HERE WAS WRONG and is recorded rather than deleted. It claimed the
        // existing VehicleComponent lookup "resolves the pool index unchanged" and that without this
        // patch "the host keeps simulating that vehicle". Both false: the index resolved was the
        // CLIENT's own pool slot, meaningless on the host (field: "HostStopTaxi: no taxi with index
        // 18" x2), and the host never simulated the car because the client created it.
        //
        // KEPT, not deleted: this is the correct door if private drivers ever become host-mirrored,
        // and until then it is a cheap probe that names the declined hail. It cannot misfire - the
        // decision now lives in one place, in TrafficSync, for every door onto that path.
        [HarmonyPatch]
        public static class Patch_PrivateDriverVehicle_OnClickToSetDestination
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("Helpers.PrivateDriverVehicle");
                return t?.GetMethod("OnClickToSetDestination",
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
                catch { /* never let a driver click break the game */ }
            }
        }

        // ── Patches: the taxi ride's real START and END (backlog #5; re-bound 2026-09-18, H-TAXISTRAND-1) ────
        // What the player lives through as "the ride" is TaxiSystem.TravelCoroutine: it hides the character, runs the
        // time machine forward by the trip duration, then warps them to the destination.  The game advances the world
        // clock through the trip — which our world-clock pinner would otherwise revert every frame, locking the
        // player in the cab — so the ride has to be bracketed exactly.
        //
        // The OLD binding (2026-05-19) patched whatever was named "TaxiTravel", believing it WAS the ride coroutine.
        // It is not: TaxiTravel lives on UI.InGameUI.BuildingResume and is a one-line wrapper around
        // TaxiSystem.TravelTo, which returns without starting anything when a private fence is locked or the fare
        // cannot be paid (decompile TaxiSystem.cs:37-66).  So its postfix fired about 1.3 s BEFORE the ride, and
        // fired even when no ride had begun.  The new binding:
        //   START = a prefix on TaxiSystem.TravelCoroutine(EntityController, float).  Patching an iterator method's
        //           stub fires exactly when StartCoroutine(TravelCoroutine(..)) is evaluated — the last line of
        //           TravelTo, past every early return — and hands us the destination for the arrival trace.
        //   END   = the game's own completion announcement, GameEvent.Invoke("ba:gameevent_completedtaxiride"),
        //           raised inside TravelCoroutine right after Character.Reset() (TaxiSystem.cs:96).  GameEvent is a
        //           plain static holding one Action<string>, and the engine clears that delegate on subsystem
        //           registration, so a prefix on Invoke is both the smallest hook and the one that cannot be
        //           unsubscribed out from under us.  TravelCoroutine raises an EMPTY event id five lines earlier,
        //           hence the exact-id test.
        // Both use TargetMethods (plural): a TargetMethod returning null aborts PatchAll and would silently drop
        // EVERY other patch in the assembly.
        // NOT TOUCHED here (out of scope): the MP instant-arrival timers (MPRestSync.OnTaxiRideStarting's 8 s pending
        // window and the 0.3 s machine-stop), which are what makes the ride finish at once in multiplayer.

        [HarmonyPatch]
        public static class Patch_TaxiSystem_TravelCoroutine_RideStart
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("TaxiSystem");
                // F6: named with its parameter types (decompile TaxiSystem.cs:68) — a name-only lookup would throw
                // AmbiguousMatchException inside TargetMethods if the game ever adds an overload, and a throw here
                // aborts PatchAll for the whole assembly.
                var m = t?.GetMethod("TravelCoroutine",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(EntityController), typeof(float) }, null);
                Plugin.Logger.LogInfo($"[Taxi] ride-start patch (TaxiSystem.TravelCoroutine): {(m != null ? "patched" : "NOT FOUND")}");
                if (m != null) yield return m;
            }

            static void Prefix(EntityController target)
            {
                try { TrafficSync.OnTaxiTravelStart(target); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] WARNING Patch_TaxiSystem_TravelCoroutine_RideStart: {ex.Message}"); }
            }
        }

        [HarmonyPatch]
        public static class Patch_GameEvent_CompletedTaxiRide_RideEnd
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var m = AccessTools.Method(typeof(GameEvent), nameof(GameEvent.Invoke), new[] { typeof(string) });
                Plugin.Logger.LogInfo($"[Taxi] ride-end patch (GameEvent.Invoke): {(m != null ? "patched" : "NOT FOUND")}");
                if (m != null) yield return m;
            }

            static void Prefix(string gameEvent)
            {
                try
                {
                    if (gameEvent != "ba:gameevent_completedtaxiride") return;   // the ride also raises an empty id a few lines earlier
                    TrafficSync.OnTaxiTravelEnd();
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] WARNING Patch_GameEvent_CompletedTaxiRide_RideEnd: {ex.Message}"); }
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
                    if (MPServer.IsRunning || MPClient.IsConnected || MPClient.OfflineFork)   // H-FORK-1 r2 (review #1): set the shop context in the offline fork too
                    {
                        var reg = __instance.buildingRegistration;
                        MPRegisterSync.SetCurrentShop(
                            reg != null ? reg.businessOwnerRivalId : "",
                            reg != null ? GameStateReader.AddressKey(reg) : "");
                        try { if (reg != null) MPRegisterSync.LogStaffDiagOnEntry(reg, GameStateReader.AddressKey(reg)); } catch { }   // [StaffDiag] visitor-side dump (removable)
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
                    // A GUEST of a shared residence must ALWAYS request the interior (subscribe to VIEW it), never be
                    // treated as its owner: if their RentedByPlayer momentarily reads true (e.g. right after a design
                    // edit), the owner-snapshot path fired and NO InteriorRequest was sent → re-entry showed no interior
                    // (user 2026-06-30 "couldn't re-enter"). Diagnostic logs the entry state — remove once settled.
                    bool guestHere = GrantSync.CanEnterGranted(addr);
                    Plugin.Logger.LogInfo($"[Housing] enter-hook addr='{addr}' rented={reg.RentedByPlayer} guest={guestHere}.");
                    if (!guestHere && InteriorSync.TrySendOwnerSnapshotOnEntry(reg, addr)) return;
                    MPClient.SendInteriorRequest(addr);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] DelayedEnterBuilding interior-req: {ex.Message}"); }
            }
        }

        // ── [PassFollow] passenger follows the driver through a building entrance (2026-06-23) ──
        // When the HOST drives a vehicle that has passengers through a building entrance, each rider
        // otherwise ends up staring at unloaded interior coords ("grey nothingness", user 2026-06-23):
        // the ghost is at the right WORLD spot but the rider's client never entered the building.  Tell
        // each rider to follow the driver inside.  SLICE 1a: detect + signal only (the rider logs +
        // resolves the building; the actual EnterBuilding lands in 1b).  Host-as-driver for now.
        [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.EnterBuildingWithVehicle))]
        public static class Patch_EnterBuildingWithVehicle_PassengerFollow
        {
            static void Postfix(CityBuildingController cbc, bool __result)
            {
                if (!__result) return;                                    // only when we actually drove in
                if (!MPServer.IsRunning && !MPClient.IsConnected) return; // only in an MP game
                try
                {
                    string vehicleId = VehicleManager.CurrentDrivenVehicleId();
                    if (string.IsNullOrEmpty(vehicleId)) return;          // we're not the one driving
                    string addressKey = GameStateReader.AddressKey(cbc.building);
                    if (MPServer.IsRunning)
                    {   // host is the driver → resolve riders + notify directly
                        MPServer.RouteFollowEnterToRiders(vehicleId, addressKey, MPConfig.PlayerId);
                        Plugin.Logger.LogInfo($"[PassFollow] host drove into '{addressKey}' aboard '{vehicleId}' → routing FollowEnter to riders.");
                    }
                    else
                    {   // client is the driver → can't reach the riders directly; let the host fan out
                        MPClient.SendPassengerFollowRelayEnter(vehicleId, addressKey);
                        Plugin.Logger.LogInfo($"[PassFollow] client drove into '{addressKey}' aboard '{vehicleId}' → relayed to host.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PassFollow] EnterBuildingWithVehicle: {ex.Message}"); }
            }
        }

        // ── [PassFollow] passenger follows the driver back OUT (2026-06-23) ──
        // Mirror of the enter hook. When the HOST drives a vehicle with riders out of a building, tell each
        // rider to exit too. Without this the rider keeps stale "inside" state, so the host's NEXT entrance
        // calls EnterBuilding while the rider is still inside → a second interior loads on top → "grey void"
        // (user 2026-06-23). targetExitId is forwarded so the rider leaves the same door (re-pin to the
        // ghost outside fixes the exact spot regardless). Host-as-driver; selectedVehicle is still set here.
        [HarmonyPatch(typeof(BuildingManager), nameof(BuildingManager.ExitFromBuilding))]
        public static class Patch_ExitFromBuilding_PassengerFollow
        {
            static void Postfix(int targetExitId)
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return; // only in an MP game
                try
                {
                    string vehicleId = VehicleManager.CurrentDrivenVehicleId();
                    if (string.IsNullOrEmpty(vehicleId)) return;   // on foot / not the driver (incl. a rider's own FollowDriverOut)
                    if (MPServer.IsRunning)
                    {   // host is the driver → resolve riders + notify directly
                        MPServer.RouteFollowExitToRiders(vehicleId, targetExitId, MPConfig.PlayerId);
                        Plugin.Logger.LogInfo($"[PassFollow] host drove out (exit {targetExitId}) aboard '{vehicleId}' → routing FollowExit to riders.");
                    }
                    else
                    {   // client is the driver → let the host fan out
                        MPClient.SendPassengerFollowRelayExit(vehicleId, targetExitId);
                        Plugin.Logger.LogInfo($"[PassFollow] client drove out (exit {targetExitId}) aboard '{vehicleId}' → relayed to host.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PassFollow] ExitFromBuilding: {ex.Message}"); }
            }
        }

        // ── Exit notification via the game's own completion event (field 20260830-170644) ──
        // This replaced a PREFIX on BuildingManager.ExitFromBuilding, which announced the exit
        // at exit START. Two defects: (a) a REFUSED exit ("warehouse gate is blocked" →
        // yield break) had already cleared the shop context and told the host we left — while
        // we never did; (b) building→building transitions (the enter coroutine calls
        // ExitFromBuildingCoroutine DIRECTLY, bypassing ExitFromBuilding) never fired the
        // prefix at all, leaving the host's interior-subscriber set stale on A→B moves.
        // GlobalEvents.onExitBuilding is the game's own completion signal: it fires AFTER the
        // street teleport + ResetIndoors, on every real exit path — door exits, drive-outs,
        // Hamptons, and A→B transitions (BuildingManager.cs:1458 and :1517). Subscribed once
        // at mod load (Plugin.OnLoadAsync), removed at unload.
        public static class ExitBuildingNotifier
        {
            private static bool _installed;
            private static readonly Action<Address> _handler = OnExitedBuilding;

            public static void Install()
            {
                if (_installed) return;
                _installed = true;
                GlobalEvents.onExitBuilding = (Action<Address>)Delegate.Combine(GlobalEvents.onExitBuilding, _handler);
                Plugin.Logger.LogInfo("[Patch] exit notifier subscribed to GlobalEvents.onExitBuilding.");
            }

            public static void Uninstall()
            {
                if (!_installed) return;
                _installed = false;
                GlobalEvents.onExitBuilding = (Action<Address>?)Delegate.Remove(GlobalEvents.onExitBuilding, _handler);
            }

            private static void OnExitedBuilding(Address address)
            {
                try
                {
                    MPRegisterSync.SetCurrentShop("", "");   // left the building — RemoteSale context gone
                    if (!MPClient.IsConnected) return;
                    var addr = GameStateReader.AddressKey(address);
                    if (string.IsNullOrEmpty(addr)) return;
                    InteriorSync.NotifyLocalBuildingExit(addr);
                    MPClient.SendPlayerExitedBuilding(addr);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] exit notify: {ex.Message}"); }
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

        // ── Patch: stop the CLIENT's Gley traffic from re-spawning ────────────
        // TrafficSync.SuppressLocalTraffic disables TrafficManager, but the GAME re-enables it whenever the
        // client crosses into a freshly-activated grid area — Gley then spawns a batch of local cars whose
        // sensors hit our ghosts (the swallowed-NRE storm) before suppression clears them on the next tick.
        // Disabling the manager is reactive and loses that race; this is the source fix: no-op the manager's
        // own Update/FixedUpdate on a pure client so the sim/spawner never runs no matter how often the game
        // flips it back on. In MP-client steady state the manager is already disabled (Update already not
        // running), so this only makes that known-good state permanent — behaviour-neutral. The <5s grace
        // lets Gley finish pre-instantiating its vehicle pool (which TrafficSync clones ghosts from) before
        // we freeze it. Host runs normally; honors the F11 suppression toggle for diagnostics.
        [HarmonyPatch]
        public static class Patch_TM_UpdateSkip
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = typeof(TrafficManager);
                int n = 0;
                foreach (var name in new[] { "Update", "FixedUpdate" })
                {
                    var m = t.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (m != null) { n++; yield return m; }
                }
                Plugin.Logger.LogInfo($"[TrafficSync] client traffic-update skip: targets={n}");
            }

            static bool Prefix()
            {
                if (UnityEngine.Time.timeSinceLevelLoad <= 5f) return true;     // let the pool init first
                // false = skip Gley's update (no spawn, no sim) on a pure client while suppression is on — UNLESS
                // the client sim runs at zero ambient density (2026-09-02: ClientServiceSimEnabled), where Gley must
                // tick so the client's own service cars drive; ambient spawning is prevented by the density clamp.
                // TRAFFIC-APART P5: in LOCAL mode this client is far from every other player and runs its OWN
                // ambient traffic, so Gley's update must tick here whatever the service-sim flag says.
                return !(MPClient.IsClientInWorld && !MPServer.IsRunning && TrafficSync.ClientTrafficSuppressionEnabled
                         && !TrafficSync.ClientServiceSimEnabled && !TrafficSync.ClientRunsLocalTraffic);
            }
        }

        /// <summary>TRAFFIC-GRID-1: skip Gley's per-frame density add for the one frame after a re-feed, while the
        /// GridManager still holds the SHORTER camera-position array the TrafficManager has already replaced - its
        /// GetCell(int) would index past the end (3 host-log sightings, all just after the player-area count grew).
        /// Skipping lets Update reach the line that re-seats the array, so it heals itself. See TrafficSync.</summary>
        [HarmonyPatch(typeof(DensityManager), nameof(DensityManager.UpdateVehicleDensity), new[] { typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3), typeof(int) })]
        public static class Patch_DensityManager_CameraCellGuard { static bool Prefix(DensityManager __instance, int activeCameraIndex) => TrafficSync.DensityCameraFeedable(__instance, activeCameraIndex); }

        /// <summary>Client sim at zero ambient density (2026-09-02): every density request on a pure client becomes 0.
        /// Gley's DensityManager adds cars only while current &lt; max and its initial load loops to max, so 0 means
        /// no ambient car ever spawns locally; the client's service cars are loaded explicitly (LoadVehicle) and
        /// are unaffected. The game re-requests density on time-of-day / neighbourhood changes — this is the source.</summary>
        [HarmonyPatch(typeof(TrafficManager), nameof(TrafficManager.SetTrafficDensity))]
        public static class Patch_TM_SetTrafficDensity_ClientZero
        {
            private static int _logs;
            static void Prefix(ref int nrOfCars)
            {
                try
                {
                    // HOST: remember the game's own number — the anchor feed multiplies it by distinct player areas
                    // (vanilla traffic, user ruling 2026-09-02). The mod's own re-assert calls pass 0 on clients only.
                    if (MPServer.IsRunning) { TrafficSync.GameDensityRequest = Math.Max(0, nrOfCars); return; }
                    if (!(MPClient.IsClientInWorld && !MPServer.IsRunning) || !TrafficSync.ClientServiceSimEnabled) return;
                    if (!TrafficSync.SelfDensityCall) TrafficSync.ClientGameDensityRequest = Math.Max(0, nrOfCars);   // H-SVC-116: the game's own number, kept for the offline hand-back
                    // TRAFFIC-APART P5(i): in LOCAL mode the ambient traffic here is this machine's OWN, so the
                    // game's number passes straight through. It is still recorded above - for the flip back to
                    // ghost mode and for the offline hand-back.
                    if (TrafficSync.ClientRunsLocalTraffic) return;
                    if (nrOfCars != 0 && _logs++ < 3) Plugin.Logger.LogInfo($"[TrafficSync] client density request {nrOfCars} → 0 (ambient traffic is the host's; only service cars drive here).");
                    nrOfCars = 0;
                }
                catch { }
            }
        }

        // ── Review MAJOR-1(b) (2026-09-02): native triggers keyed on the "Vehicles" layer ignore mod ghosts ──
        // UndergroundParkingEntrance / ParkingElevator / CityHamptonsHouseController / TurnstileBarrier react to any
        // collider on LayerHelper.VehiclesLayerIndex and then compare its VehicleController with the player's
        // selectedVehicle — for a ghost (no controller) the comparison degenerates and, with the local player on foot,
        // fires EnterParking / the elevator overlay / the Business Management screen. Every mod ghost body (traffic
        // ghost, service look-alike, fleet ghost) carries ModGhostMarker; these prefixes drop such contacts. MP only.
        internal static bool IsModGhostContact(UnityEngine.Component? c)
        {
            try
            {
                if (c == null || (!MPServer.IsRunning && !MPClient.InMpGame)) return false;
                if (c.GetComponentInParent<ModGhostMarker>() == null) return false;
                // Review #2 MAJOR-1 (2026-09-02): a granted proxy (a friend's car the LOCAL player holds a key to and is
                // driving) is a real car with the marker on its root; while driven its root sits on the Vehicles layer
                // (VehicleController.EnterVehicle :338) — exactly what these triggers key on. The local player's own
                // selected vehicle is never a "ghost contact": the native comparison against selectedVehicle must run.
                var vc = c.GetComponentInParent<VehicleController>();
                if (vc != null && InstanceBehavior<GameManager>.Instance != null && vc == InstanceBehavior<GameManager>.Instance.selectedVehicle) return false;
                return true;
            }
            catch { return false; }
        }
        [HarmonyPatch(typeof(Parking.UndergroundParking.UndergroundParkingEntrance), "OnTriggerEnter")]
        public static class Patch_UndergroundParkingEntrance_IgnoreGhosts { static bool Prefix(UnityEngine.Collider other) => !IsModGhostContact(other); }
        [HarmonyPatch(typeof(Parking.UndergroundParking.ParkingElevator), "OnTriggerEnter")]
        public static class Patch_ParkingElevator_IgnoreGhosts { static bool Prefix(UnityEngine.Collider other) => !IsModGhostContact(other); }
        [HarmonyPatch(typeof(Parking.UndergroundParking.ParkingElevator), "OnTriggerExit")]   // review #2 MINOR-3: the exit hides the overlay for a player on foot
        public static class Patch_ParkingElevator_Exit_IgnoreGhosts { static bool Prefix(UnityEngine.Collider other) => !IsModGhostContact(other); }
        [HarmonyPatch(typeof(CityHamptonsHouseController), "OnCollisionEnter")]
        public static class Patch_HamptonsHouse_IgnoreGhosts { static bool Prefix(UnityEngine.Collision other) => !IsModGhostContact(other?.collider); }
        [HarmonyPatch(typeof(Controllers.TurnstileBarrier), "OnTriggerEnter")]
        public static class Patch_Turnstile_Enter_IgnoreGhosts { static bool Prefix(UnityEngine.Collider other) => !IsModGhostContact(other); }
        [HarmonyPatch(typeof(Controllers.TurnstileBarrier), "OnTriggerExit")]
        public static class Patch_Turnstile_Exit_IgnoreGhosts { static bool Prefix(UnityEngine.Collider other) => !IsModGhostContact(other); }

        /// <summary>TRAFFIC-CONSIST T4 (2026-09-18, design section 6): the trigger-hit counter. Gley's own sensor
        /// entry point, counted ONLY when the other collider sits under a ModGhostMarker parent - i.e. it is one of
        /// the mod's ghost bodies. Expected 0 before T2's sense proxy exists and non-zero after, which is the
        /// measurement I2 needs and, like the symptom itself, it needs no proximity to anything. Counter only: this
        /// runs per trigger event and must never log per frame (the census prints it every 30 s).</summary>
        [HarmonyPatch(typeof(VehicleComponent), nameof(VehicleComponent.OnTriggerEnter))]
        public static class Patch_VC_OnTriggerEnter_CountGhostHits
        {
            static void Postfix(UnityEngine.Collider other)
            {
                try { TrafficSync.CountGhostTriggerHit(other); }
                catch (Exception ex) { TrafficSync.WarnOnce("Patch_VC_OnTriggerEnter_CountGhostHits", ex); }
            }
        }

        /// <summary>Client sim at zero ambient density (2026-09-02): while the client paints the HOST's light states
        /// (TrafficSync.ApplyTrafficLights → Waypoint.stop), Gley's local phase timer must never advance there.
        /// TRAFFIC-APART P5(iv): in LOCAL mode nothing paints them - this client runs its own traffic, so it runs
        /// its own phases too.</summary>
        [HarmonyPatch(typeof(IntersectionManager), nameof(IntersectionManager.UpdateIntersections))]
        public static class Patch_IM_UpdateIntersections_ClientSkip
        {
            static bool Prefix() => !(MPClient.IsClientInWorld && !MPServer.IsRunning && TrafficSync.ClientTrafficSuppressionEnabled
                                      && !TrafficSync.ClientRunsLocalTraffic);   // false = skip on a pure client in GHOST mode (MINOR-2: honours the F11 vanilla toggle)
        }
        // ── (removed 2026-08-26) Patch_ClickSleep ─────────────────────────
        // Deleted by the patch-target audit. Broken two ways and inert a third: GameManager has NO
        // ClickSleep (the real member is UI.ItemPanel.ItemPanelUI.ClickSleep); the attribute sat on a
        // method of the OUTER MPPatches class, which carries no class-level [HarmonyPatch], so
        // Plugin.cs:292 skipped it and the patch never applied; and the body was `return true;`, a
        // no-op. It was a trap: adding a class-level attribute here — or moving to PatchAll — would
        // have made Harmony throw on the missing target and count toward MpDisabledByPatchFailure.
        // Sleeping needs no patch: the MP time model has no time skips and the fast-forward is
        // refused by the world-clock monitor.

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
                    if (__0 == null) return;
                    if (MPServer.IsRunning) { ParkedVehicleSync.HostOnRelease(__0); return; }
                    // AUTOPARK-1 H1 (client): a NATIVE caller is releasing a car this client
                    // rented for a host-placed parked ghost — e.g. VehicleHelper
                    // .DestroyBlockingVehicles clearing room for a delivered vehicle (1.0 :298).
                    // Native intent stands (the release runs); we only fix our own books, so the
                    // object can never be pushed into the collectionCheck:false pool twice.
                    // The lane's own periodic sweep never reaches this point — its children are
                    // detached by the CleanupParkedVehicles prefix below.
                    if (ParkedVehicleSync.IsForeignReleaseOfRented(__0))
                        ParkedVehicleSync.OnForeignRelease(__0);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] ReleaseParkedVehicle prefix: {ex.Message}"); }
            }
        }

        // AUTOPARK-1 H1 — ParkingLaneGenerator.CleanupParkedVehicles (1.0 :938-956) walks the
        // lane's DIRECT children and releases every one on the ParkedVehicles layer.  It runs
        // daily (RunDaily -> ParkingLaneRegeneration), on every business open/close flip, and is
        // forced at :393/:693/:1208 — and its distance guard is bypassed while the player is
        // indoors.  Nothing suppresses it on a client, so this local timer would evict
        // HOST-AUTHORITATIVE parked cars; the host's snapshot is the only thing allowed to decide
        // when one leaves.  Detach our rented children for the duration of the sweep (the method
        // snapshots GetChildren() itself, so a prefix detach is invisible to it) and put them back
        // in the Finalizer.  Chosen over refusing the release inside ReleaseParkedVehicle because
        // the sweep fires onReleaseVehicle FIRST and its subscriber mutates the car — see
        // ParkedVehicleSync.DetachRentedLaneChildren.
        [HarmonyPatch(typeof(ParkingLaneGenerator), nameof(ParkingLaneGenerator.CleanupParkedVehicles))]
        public static class Patch_ParkingLane_CleanupKeepsHostCars
        {
            static void Prefix(ParkingLaneGenerator __instance,
                               out System.Collections.Generic.List<UnityEngine.GameObject>? __state)
            {
                __state = null;
                try { __state = ParkedVehicleSync.DetachRentedLaneChildren(__instance); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] CleanupParkedVehicles prefix (AUTOPARK-1 H1): {ex.Message}"); }
            }

            static void Finalizer(ParkingLaneGenerator __instance,
                                  System.Collections.Generic.List<UnityEngine.GameObject>? __state)
            {
                try { ParkedVehicleSync.ReattachRentedLaneChildren(__instance, __state); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch] CleanupParkedVehicles finalizer (AUTOPARK-1 H1): {ex.Message}"); }
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
            // TICK AUDIT: ParkingLaneGenerator.OnNewHour (1.0 :627) subscribes to GlobalEvents.onNewHour
            // and, when a business's open/closed state FLIPS, queues itself onto
            // ParkingSimulator.parkingQueueWorker.
            // CORRECTION (2026-08-29): an earlier version of this note called it "1.0-new, no such
            // declaration in 0.11". That was true of the METHOD NAME ONLY. 0.11 did exactly the same
            // thing as an ANONYMOUS DELEGATE (0.11 ParkingLaneGenerator.cs:233-240, same
            // IsBusinessOpen / _lastOpeningState / AddWork(this) body), and its queue body already
            // ran both bracketing calls (CleanupParkedVehicles :404, DestroyBlockingParkedVehicles
            // :412). So none of this is new; it is a PRE-EXISTING reliance found during the 1.0 audit.
            // Note the irony: the sibling comment on the parking shield below warns that "a name
            // surviving a version tells you nothing about what its BODY still does" - and here the
            // inverse fired, a name APPEARING was read as behaviour appearing. That work regenerates
            // the lane via GenerateParkedVehicles(chanceOfFreeSpot, ...) - which is RNG-driven and
            // would therefore diverge if it ran independently on each machine.
            // CLASSIFIED: the RNG-DIVERGENCE half needs no action, and the reason is this very
            // patch — OnNewHour only ENQUEUES, and the queue's work reaches GenerateParkedVehicles,
            // which the prefix below already suppresses on a client (outside job-truck lanes).
            // NARROWED 2026-08-29 (review): that is NOT the whole work item. The same queue body
            // also runs CleanupParkedVehicles() before it and DestroyBlockingParkedVehicles() after
            // it (ParkingLaneGenerator.cs:888-903), and NEITHER is covered by this prefix. So on a
            // client the lane still EMPTIES on an open/closed flip and relies on ParkedVehicleSync
            // to refill it from the host's snapshot. That is believed fine — the host is the source
            // of parked cars anyway — but it is a reliance, not a no-op, and saying "no action
            // needed" flatly would have hidden it.
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
                => VehicleManager.FindAllMethodsByName("GenerateParkedVehicles");

            // Round-82: the delivery JOB TRUCK is spawned INSIDE this method (first lane slot,
            // ParkingLaneGenerator:517) — the blanket client skip removed every job truck from
            // every client since parked-suppression shipped (probe-confirmed 2026-07-24: zero
            // DeliveryJobStartController instances client-side while the host logged 10 VISIBLE).
            private static readonly System.Reflection.MethodInfo? MiVanLane =
                AccessTools.Method(typeof(ParkingLaneGenerator), "ContainsDeliveryVehicle");

            static bool Prefix(object __instance, ref int freeSpotChance)
            {
                // CLAUDE-DIAGNOSTIC — gated on ParkedVehicleSync.SpawnSuppressionEnabled
                // so F6 can toggle the suppression at runtime for the entry-bug A/B test.
                if (!MPClient.IsClientInWorld || !ParkedVehicleSync.SpawnSuppressionEnabled) return true;

                // Round-82 (user-approved): job-truck lanes run NATIVE with every regular spot
                // forced FREE — Chance(100) is always true (Random.Range(0,100) <= 100), so the
                // ambient arm provably spawns nothing (no doubling with host-mirror ghosts) while
                // the van slot BYPASSES the chance roll and spawns normally: native placement,
                // building link, DeactivateIfNeeded, DeliveryVanSpots bookkeeping. Non-owner van
                // lanes (another lane already holds this building's van) degrade to all-free too.
                try
                {
                    if (MiVanLane != null && __instance is ParkingLaneGenerator
                        && (bool)MiVanLane.Invoke(__instance, null))
                    {
                        freeSpotChance = 100;
                        return true;
                    }
                }
                catch { }
                return false;   // every other lane: host snapshot stays the only source of parked cars
            }
        }

        // ── Round-94 (user-approved, by-census closure of the injected-staff leak) ───────────
        // EmployeeHelper.GetEmployeeInstances() (no-arg) returns the RAW gi.EmployeeInstances —
        // including the round-30 injected partner-staff mirrors — and it's a trivial getter the
        // Mono-inlining rule forbids patching. Round-80 filtered the QUERY overload; every other
        // consumer of the raw list kept leaking (field 20260726-003417: "workers list of rivals
        // shops" = EmployeesScrollerController). Census of ALL raw-list callers → the five below
        // get a reentrant STASH: injected records are lifted out for the duration of the native
        // call and restored in a Finalizer (exception-safe; per-call local stash, nesting-safe).
        // Sim internals (BusinessEmployeeGenerator, RecruitmentHelper, HeadhunterPlan removal,
        // EmployeeHelper internals) deliberately keep seeing the injection — that's its purpose.
        internal static object? StashInjected(bool alsoMergedPartners)
        {
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return null;
            long _pc = MPPerf.Begin();   // round-97: roster walk inside native calls — patch-cost bracketed
            try
            {
                var list = Helpers.EmployeeHelper.GetEmployeeInstances();
                if (list == null) return null;
                List<Entities.EmployeeInstance>? stash = null;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var e = list[i];
                    if (e?.id == null) continue;
                    // Round-100: synthetic duty stand-ins strip UNCONDITIONALLY — never real staff,
                    // no merged exemption. Injected partner records keep the round-94 rules.
                    bool synthetic = MPRegisterSync.IsSyntheticDuty(e.id);
                    if (!synthetic && !MPRegisterSync.IsInjectedStaff(e.id)) continue;
                    if (!synthetic && !alsoMergedPartners && MPRegisterSync.IsInjectedFromMergedPartner(e.id)) continue;
                    (stash ??= new System.Collections.Generic.List<Entities.EmployeeInstance>()).Add(e);
                    list.RemoveAt(i);
                }
                return stash;
            }
            catch { return null; }
            finally { MPPerf.PatchEnd("InjectedStash", _pc); }
        }

        internal static void RestoreInjected(object? state)
        {
            long _pc = MPPerf.Begin();   // round-97: both halves of the stash/restore cycle report as one site
            try
            {
                if (state is not System.Collections.Generic.List<Entities.EmployeeInstance> stash || stash.Count == 0) return;
                var list = Helpers.EmployeeHelper.GetEmployeeInstances();
                if (list == null) return;
                for (int i = 0; i < stash.Count; i++) list.Add(stash[i]);
            }
            catch { }
            finally { MPPerf.PatchEnd("InjectedStash", _pc); }
        }

        // My Employees / HR-manager candidate list (the reported surface). Merged-partner staff
        // stay visible (round-80/62 semantics: a merged company shares its pool).
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.EmployeesScrollerController),
                      nameof(UI.Smartphone.Apps.BizMan.HRManagers.EmployeesScrollerController.Load))]
        public static class Patch_EmployeesScroller_HideInjected
        {
            static void Prefix(out object? __state) => __state = StashInjected(alsoMergedPartners: false);
            static void Finalizer(object? __state) => RestoreInjected(__state);
        }

        // HR-plan assign list — same shape, same rule.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI),
                      nameof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI.Fill))]
        public static class Patch_HrManagerPlan_HideInjected
        {
            static void Prefix(out object? __state) => __state = StashInjected(alsoMergedPartners: false);
            static void Finalizer(object? __state) => RestoreInjected(__state);
        }

        // "Has hired an employee" tutorial/goal counter — injected mirrors must not satisfy it
        // (merged-partner staff still count, matching round-80's EmployeeMaxLevelGoal semantics).
        [HarmonyPatch(typeof(Tutorial.HasHiredEmployee), nameof(Tutorial.HasHiredEmployee.CheckIfCompleted))]
        public static class Patch_HasHiredEmployee_HideInjected
        {
            static void Prefix(out object? __state) => __state = StashInjected(alsoMergedPartners: false);
            static void Finalizer(object? __state) => RestoreInjected(__state);
        }

        // Business transfer (moving company): MoveEmployees reassigns everything at the origin
        // address — on a merger-flipped replica that would mutate partner MIRROR records. ALL
        // injected records excluded (their real owner's machine is the authority).
        [HarmonyPatch(typeof(Buildings.BuildingTypes.Special.MovingCompany.BizManTransfer),
                      nameof(Buildings.BuildingTypes.Special.MovingCompany.BizManTransfer.Transfer))]
        public static class Patch_BizManTransfer_HideInjected
        {
            static void Prefix(out object? __state) => __state = StashInjected(alsoMergedPartners: true);
            static void Finalizer(object? __state) => RestoreInjected(__state);
        }

        // ── CROSS-HR-1 S3: NOTHING RUNS THROUGH A SHADOW ───────────────────────────────────────────
        // A partner's HR plans are now INSTALLED on a plain member as tagged DISPLAY COPIES
        // (CompanyLists.Apply -> MergerAbsence.InstallListsForDisplay, "display:<pid>"), so the member's
        // own lookups resolve them - which is the whole point - and so do the game's own passes, which
        // must not.  StripModEmployeeRecords keeps injected partner records out of EmployeeHelper.RunDaily
        // / RunHourly / the complaints, but NOT out of EmployeeHelper.WorkDaily (decompile
        // Helpers/EmployeeHelper.cs:97-105, its OWN NewDay step at GameManager.cs:649, where the only mod
        // patch is the throw wrapper), and MergerEmployeeSync builds the partner's HR manager as a real
        // `new HRManager()` - so HRManager.WorkDaily (Entities/HRManager.cs:30-38) would find the shadow
        // through HrManagerHelper.GetAssignedPlanForHrManager(id) and TRAIN + PAY a second time out of the
        // shared wallet.  Each guard below is a prefix that skips on a DISPLAY INSTALL ONLY: an absence
        // stand-in's items are tagged with a REAL pid, IsDisplayInstall is false for them and they still
        // run, which is exactly what standing in means.  No new on-screen text - the skips only log.
        private static bool IsShadowPlan(object plan)
        { try { return plan != null && MergerAbsence.IsDisplayInstall(plan); } catch { return false; } }

        /// <summary>One log line per plan per guard (not per day: GameInstance exposes no plain day counter
        /// and a shadow set is small and bounded, so this is strictly quieter than the brief's cadence).</summary>
        private static readonly System.Collections.Generic.HashSet<string> _shadowSkipLogged
            = new System.Collections.Generic.HashSet<string>();

        private static void LogShadowSkip(string what, string planId)
        {
            try
            {
                if (_shadowSkipLogged.Add(what + "|" + (planId ?? "")))
                    Plugin.Logger.LogInfo($"[Shadow] {what} skipped for HR plan '{planId}' - it is a partner's "
                                        + "display copy; the machine that owns it is the one that runs it.");
            }
            catch { }
        }

        // (a) THE DECIDING ONE: the daily HR pass itself (train + charge the insurance).
        [HarmonyPatch(typeof(Entities.HRManager), nameof(Entities.HRManager.WorkDaily))]
        public static class Patch_HRManagerWorkDaily_SkipShadow
        {
            static bool Prefix(Entities.HRManager __instance)
            {
                try
                {
                    string id = __instance?.id ?? "";
                    if (id.Length == 0) return true;
                    // Resolved exactly the way native does it (HRManager.cs:32).
                    var plan = Buildings.Office.Headquarters.HrManagerHelper.GetAssignedPlanForHrManager(id);
                    if (!IsShadowPlan(plan)) return true;
                    LogShadowSkip("the daily HR pass (TrainEmployees + PayHealthInsurance)", plan.id);
                    return false;
                }
                catch { return true; }   // fail OPEN only for my own plans: a shadow always answers above
            }
        }

        // (b) ScheduleHelper.UpdateHQPlans (decompile :425-431) unassigns the manager of every plan at a
        // headquarters whose manager has no work shift - on a shadow that manager is the OWNER's, and the
        // owner's machine decides.  The guard sits on the WRITE, so HRManager.UnAssignWork (:40-43) and
        // any other route to it are covered by the same prefix.
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HrManagerPlan),
                      nameof(Buildings.Office.Headquarters.HrManagerPlan.UnAssignEmployee))]
        public static class Patch_HrPlanUnAssignEmployee_SkipShadow
        {
            static bool Prefix(Buildings.Office.Headquarters.HrManagerPlan __instance)
            {
                if (!IsShadowPlan(__instance)) return true;
                LogShadowSkip("UnAssignEmployee", __instance.id);
                return false;
            }
        }

        // (c) The deregistration sweep BusinessHelper.DeleteHQPlans (:1002-1005) deletes every HR plan of
        // the address through HrManagerHelper.DeletePlan -> HrManagerPlan.Delete (:223-236, a RemoveAll by
        // id that also closes the insurance offers).  On a member that would drop a partner's plan out of
        // the list behind MergerAbsence's install record and orphan it.  Guarding Delete covers every route
        // (the pane's own delete is already ROUTED by Patch_HrPaneDelete_MergerGate).
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HrManagerPlan),
                      nameof(Buildings.Office.Headquarters.HrManagerPlan.Delete))]
        public static class Patch_HrPlanDelete_SkipShadow
        {
            static bool Prefix(Buildings.Office.Headquarters.HrManagerPlan __instance)
            {
                if (!IsShadowPlan(__instance)) return true;
                LogShadowSkip("Delete", __instance.id);
                return false;
            }
        }

        // (e) HrManagerPlan.UpgradeHealthInsurancePlan (:173-195) is ONE of the writes that BUY insurance -
        // it charges through GameManager.ChangeMoneySafe and then assigns healthInsurancePlan.  It is
        // reachable from the HR pane (already routed) AND from UI.Dialog/HealthInsurancePartnershipSettings
        // :41, which offers EVERY plan in gi.hrManagerPlans with CanHaveHealthInsurancePlan - a shadow
        // included.  The guard is on the write, so both PANE routes are covered - but it is NOT the only
        // write that buys: the NEGOTIATED route commits in Entities/HealthInsurancePlanOffer.AcceptOffer
        // (:86-97), which assigns healthInsurancePlan directly and never passes through here (CROSS-HR-1b
        // K1 - the comment that used to stand here said "the only write", and it was wrong).  (f) and (g)
        // below close that route at both ends: the offer is never made, and an offer in flight never lands.
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HrManagerPlan),
                      nameof(Buildings.Office.Headquarters.HrManagerPlan.UpgradeHealthInsurancePlan))]
        public static class Patch_HrPlanUpgradeInsurance_SkipShadow
        {
            static bool Prefix(Buildings.Office.Headquarters.HrManagerPlan __instance)
            {
                if (!IsShadowPlan(__instance)) return true;
                LogShadowSkip("UpgradeHealthInsurancePlan", __instance.id);
                return false;
            }
        }

        // (f) CROSS-HR-1b K1(a): THE PARTNERSHIP DIALOG MUST NOT OFFER A SHADOW.  The collection at
        // UI.Dialog/HealthInsurancePartnershipSettings.cs:41 is a LOCAL (`availablePlans`, captured by the
        // two dropdown closures), so there is no member for a postfix to filter - THE FILTER ITSELF is the
        // seam: `.Where(x => x.CanHaveHealthInsurancePlan)`, and :42 builds the dropdown's options from that
        // same list, so one postfix on the property removes a shadow from BOTH the list and the options.
        // A shadow arrives with healthInsurancePlan == null (MergerAbsence :850 sets it only when
        // HealthInsurancePlanType >= 0) and resolves its manager through the injected copy, so native
        // answers TRUE for it (decompile HrManagerPlan.cs:44-47).  A shadow is READ-ONLY on this machine
        // whatever asks, so false is the right answer everywhere this property is read.
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HrManagerPlan),
                      nameof(Buildings.Office.Headquarters.HrManagerPlan.CanHaveHealthInsurancePlan),
                      HarmonyLib.MethodType.Getter)]
        public static class Patch_HrPlanCanHaveInsurance_SkipShadow
        {
            static void Postfix(Buildings.Office.Headquarters.HrManagerPlan __instance, ref bool __result)
            {
                try
                {
                    if (!__result || !IsShadowPlan(__instance)) return;
                    __result = false;
                    LogShadowSkip("the insurance partnership offer list", __instance.id);
                }
                catch { }
            }
        }

        // (g) CROSS-HR-1b K1(b): AND AN OFFER ALREADY IN FLIGHT MUST NOT COMMIT ONTO ONE.
        // Entities/HealthInsurancePlanOffer.AcceptOffer (:86-97) writes `HrManagerPlan.healthInsurancePlan`
        // straight onto the plan the offer names (the property resolves through HrManagerHelper
        // .GetPlanFromId, which finds a shadow like any other element), so it never meets (e)'s guard.
        // CompanyMessages.RouteOfferCommit gates the RELAYED offers only (CompanyMessages.cs :951), so an
        // offer minted HERE - by an older build, or by any route this one does not own - would still land.
        // Refused in place: the offer object is left exactly as it was, not finished and not accepted,
        // which is the game's own "nothing happened" state.  No new text: the skip only logs.
        [HarmonyPatch(typeof(Entities.HealthInsurancePlanOffer),
                      nameof(Entities.HealthInsurancePlanOffer.AcceptOffer))]
        public static class Patch_InsuranceOfferAccept_SkipShadow
        {
            // CROSS-HR-1c (re-check MAJOR-1): CompanyCandidates.Patch_HealthInsuranceOffer_Accept_CompanyRoute
            // prefixes the SAME method to route a RELAYED partner offer (D23), whose plan id is the owner's id =
            // this machine's shadow id.  Without an order the shadow refusal could win the tie and swallow that
            // accept.  Priority.Last runs this prefix AFTER the route: a routed offer skips it (the route
            // returns false), an own offer on a shadow still reaches it and is refused here.
            [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.Last)]
            static bool Prefix(Entities.HealthInsurancePlanOffer __instance)
            {
                try
                {
                    string id = __instance?.hrManagerPlanId ?? "";
                    if (id.Length == 0 || !CompanyPlans.IsShadowHrPlanId(id)) return true;
                    LogShadowSkip("AcceptOffer (the insurance commit)", id);
                    return false;
                }
                catch { return true; }   // fail OPEN only for my own plans: a shadow always answers above
            }
        }

        // (d) BizManTransfer (:174-180) rewrites headquartersAddress on EVERY HR plan at the origin
        // address - it is a bare foreach over the list, so there is no per-plan seam to prefix.  The whole
        // transfer therefore runs with the shadows OUT of the list (the same shape as the injected-record
        // strip directly above), and they go back untouched afterwards.
        [HarmonyPatch(typeof(Buildings.BuildingTypes.Special.MovingCompany.BizManTransfer),
                      nameof(Buildings.BuildingTypes.Special.MovingCompany.BizManTransfer.Transfer))]
        public static class Patch_BizManTransfer_HideShadowPlans
        {
            static void Prefix(out System.Collections.Generic.List<Buildings.Office.Headquarters.HrManagerPlan>? __state)
            {
                __state = null;
                try
                {
                    var list = SaveGameManager.Current?.hrManagerPlans;
                    if (list == null || list.Count == 0) return;
                    System.Collections.Generic.List<Buildings.Office.Headquarters.HrManagerPlan>? taken = null;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (!IsShadowPlan(list[i])) continue;
                        (taken ??= new System.Collections.Generic.List<Buildings.Office.Headquarters.HrManagerPlan>()).Add(list[i]);
                        list.RemoveAt(i);
                    }
                    __state = taken;
                    if (taken != null)
                        Plugin.Logger.LogInfo($"[Shadow] {taken.Count} partner HR plan(s) sit out a BizMan business transfer.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Shadow] transfer strip: {ex.Message}"); }
            }

            static void Finalizer(System.Collections.Generic.List<Buildings.Office.Headquarters.HrManagerPlan>? __state)
            {
                try
                {
                    if (__state == null) return;
                    var list = SaveGameManager.Current?.hrManagerPlans;
                    if (list == null) return;
                    for (int i = __state.Count - 1; i >= 0; i--)
                        if (!list.Contains(__state[i])) list.Add(__state[i]);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Shadow] transfer restore: {ex.Message}"); }
            }
        }

        // ===== CROSS-HR-2 (2026-09-12, plan D38) T1: THE TRAINING LEG ==============================
        // A partner's HR plan may train a worker who STAYS at my shop. The plan runs HERE (this machine
        // owns it); that worker's REAL record lives on the machine that employs him and what sits here is
        // an INJECTED COPY (MPRegisterSync.IsInjectedStaff). Native's writes in HrManagerPlan.TrainEmployees
        // (decompile Buildings.Office.Headquarters/HrManagerPlan.cs:66-117) land on that copy and the
        // owner's next roster push overwrites them - so the day's training is paid for and never happens.
        //
        // The copy is LEFT exactly as native left it; what travels is the DIFF. The prefix snapshots each
        // injected assignee's skill values and hourlyWage, the postfix reads them again and sends ONE
        // "hrtrain" leg per employee on the existing employee-edit carrier; the owner mirrors the same
        // writes onto the real record and republishes (MergerEmployeeSync.ApplyOnOwner).
        // MONEY IS NOT SENT: the plan's single ChangeMoneySafe (:115) is native's, on this machine, once.
        // Native runs TrainEmployees once a day (HRManager.WorkDaily, Entities/HRManager.cs:30-38), so one
        // leg per employee per game day is native's own cadence - and the Stamp makes a repeat inert.
        // INERT with no injected assignee: the snapshot stays null and the postfix returns at once.
        private sealed class HrTrainSnap
        {
            internal float Wage;
            internal readonly System.Collections.Generic.Dictionary<string, float> Skills
                = new System.Collections.Generic.Dictionary<string, float>(System.StringComparer.Ordinal);
        }

        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HrManagerPlan),
                      nameof(Buildings.Office.Headquarters.HrManagerPlan.TrainEmployees))]
        public static class Patch_HrPlanTrainEmployees_RouteInjected
        {
            static void Prefix(Buildings.Office.Headquarters.HrManagerPlan __instance,
                               out System.Collections.Generic.Dictionary<string, HrTrainSnap>? __state)
            {
                __state = null;
                try
                {
                    if (__instance == null || IsShadowPlan(__instance)) return;   // a shadow never trains here
                    var ids = __instance.assignedEmployees;
                    if (ids == null) return;
                    foreach (var id in ids)
                    {
                        if (string.IsNullOrEmpty(id) || !MPRegisterSync.IsInjectedStaff(id)) continue;
                        Entities.EmployeeInstance? e = null;
                        try { e = Helpers.EmployeeHelper.GetEmployeeById(id); } catch { }
                        if (e == null) continue;
                        var snap = new HrTrainSnap();
                        try { snap.Wage = e.hourlyWage; } catch { }
                        try
                        {
                            var sk = e.characterData?.skills;
                            if (sk != null)
                                foreach (var s in sk)
                                    if (s != null && !string.IsNullOrEmpty(s.name)) snap.Skills[s.name] = s.value;
                        }
                        catch { }
                        (__state ??= new System.Collections.Generic.Dictionary<string, HrTrainSnap>(System.StringComparer.Ordinal))[id] = snap;
                    }
                }
                catch { __state = null; }
            }

            static void Postfix(Buildings.Office.Headquarters.HrManagerPlan __instance,
                                System.Collections.Generic.Dictionary<string, HrTrainSnap>? __state)
            {
                string planId0 = ""; try { planId0 = __instance?.id ?? ""; } catch { }
                if (__state == null || __state.Count == 0) { MergerEmployeeSync.NoteTrainPass(planId0, 0, 0, 0); return; }   // inert: no injected assignee, no leg, no log
                int legs = 0, unchanged = 0;
                try
                {
                    int day = 0; try { day = SaveGameManager.Current?.Day ?? 0; } catch { }
                    string planId = planId0;
                    int target = 0; try { target = __instance?.trainingTarget ?? 0; } catch { }
                    foreach (var kv in __state)
                    {
                        Entities.EmployeeInstance? e = null;
                        try { e = Helpers.EmployeeHelper.GetEmployeeById(kv.Key); } catch { }
                        if (e == null) { unchanged++; continue; }   // r2 MINOR-3: gone between prefix and postfix - counted, so the summary adds up
                        var deltas = new System.Collections.Generic.List<string>();
                        try
                        {
                            var sk = e.characterData?.skills;
                            if (sk != null)
                                foreach (var s in sk)
                                {
                                    if (s == null || string.IsNullOrEmpty(s.name)) continue;
                                    float was = kv.Value.Skills.TryGetValue(s.name, out var w) ? w : s.value;
                                    float d = s.value - was;
                                    if (d > 0.0001f)
                                        deltas.Add(s.name + "=+" + d.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                                }
                        }
                        catch { }
                        float wd = 0f; try { wd = e.hourlyWage - kv.Value.Wage; } catch { }
                        float wr = 0f; try { wr = kv.Value.Wage > 0f ? e.hourlyWage / kv.Value.Wage : 0f; } catch { }
                        if (deltas.Count == 0 && wd <= 0.0001f) { unchanged++; continue; }   // native did nothing to this copy (at target, or past it)
                        string addr = ""; try { addr = GameStateReader.AddressKey(e.assignedAddress) ?? ""; } catch { }
                        string owner = ""; try { owner = MPRegisterSync.OwnerOfInjected(kv.Key) ?? ""; } catch { }
                        MergerEmployeeSync.SendHrTrain(new EmployeeEditPayload
                        {
                            PlayerId   = MPConfig.PlayerId,
                            Action     = "hrtrain",
                            AddressKey = addr,
                            EmployeeId = kv.Key,
                            OwnerPid   = owner,
                            AssignedHrManagerPlanId = planId,
                            Stamp      = planId + "|" + day + "|" + kv.Key,
                            SkillDeltas = deltas,
                            WageDelta  = wd > 0f ? wd : 0f,
                            WageRatio  = wr > 1f ? wr : 0f,
                            TrainingTarget = target,
                        });
                        legs++;
                        Plugin.Logger.LogInfo($"[CrossHR] training leg for '{kv.Key}' (plan '{planId}', day {day}) -> owner "
                                            + $"'{(owner.Length > 0 ? owner : "?")}': {(deltas.Count > 0 ? string.Join(", ", deltas) : "no skill change")}, "
                                            + $"wage +{wd.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} (x{wr.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}). The copy here stays as native left it.");
                    }
                    // One summary per pass that had injected assignees (rig T-CROSSHR2 2026-09-12: a pass that sends nothing must say why).
                    Plugin.Logger.LogInfo($"[CrossHR] training pass on plan '{planId}' (day {day}): {__state.Count} injected assignee(s), {legs} leg(s) sent, {unchanged} unchanged (native left those copies as they were).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] training legs: {ex.Message}"); }
                MergerEmployeeSync.NoteTrainPass(planId0, __state.Count, legs, unchanged);
            }
        }

        // ===== CROSS-HR-2 T3b: A SHADOW STAYS READ-ONLY ONCE A WORKER CARRIES ITS ID ================
        // EmployeeInstance.OnRemove (decompile Entities/EmployeeInstance.cs:841-846, reached from Resign()
        // :760-762, RemoveEmployee() :828-831 and HeadhunterPlan.cs:405) ends with
        //     HrManagerHelper.GetPlanFromId(assignedHrManagerPlanId)?.assignedEmployees.Remove(id);
        // and runs on the WORKER's machine. Once a member's own worker carries a PARTNER's plan id, that
        // lookup resolves the SHADOW installed here and the removal WRITES it: harmless to the runner (the
        // next feed replaces the shadow) but it breaks "nothing writes a shadow" and desyncs the copy until
        // then. The prefix hides the id for the duration of the native body - GetPlanFromId("") is null
        // (decompile HrManagerHelper.cs:18-28) and `?.` makes the removal a no-op - restores it after, and
        // sends the removal where it belongs: the routed HR "assign" with BoolValue=false on the
        // mergerplanedit carrier (CompanyPlans.RoutePaneEdit:1364 -> Send:1471; a shadow row is registered in
        // _rowInfo by RegisterShadowRows:1213-1237, so IsOverlayPlan is true for it), which the RUNNER applies
        // to its REAL plan through the never-refused unassign branch (CompanyPlans.cs:1901 "assign").
        [HarmonyPatch(typeof(Entities.EmployeeInstance), nameof(Entities.EmployeeInstance.OnRemove))]
        public static class Patch_EmployeeOnRemove_ShadowStaysReadOnly
        {
            static void Prefix(Entities.EmployeeInstance __instance, out string __state)
            {
                __state = "";
                try
                {
                    string pid = __instance?.assignedHrManagerPlanId ?? "";
                    if (pid.Length == 0) return;
                    if (!IsShadowPlan(Buildings.Office.Headquarters.HrManagerHelper.GetPlanFromId(pid))) return;
                    __state = pid;
                    __instance!.assignedHrManagerPlanId = "";
                }
                catch { __state = ""; }
            }

            static void Postfix(Entities.EmployeeInstance __instance, string __state)
            {
                if (string.IsNullOrEmpty(__state)) return;
                try
                {
                    __instance.assignedHrManagerPlanId = __state;   // the record leaves with the tag it had
                    var plan = Buildings.Office.Headquarters.HrManagerHelper.GetPlanFromId(__state);
                    if (plan == null) return;
                    bool routed = CompanyPlans.RoutePaneEdit("hr", "the worker left - take him off the plan",
                                                            plan, "assign", __instance.id, 0, 0f, false);
                    Plugin.Logger.LogInfo($"[CrossHR] '{__instance.id}' left this save carrying partner plan '{__state}' - "
                                        + $"the shadow was NOT written; the drop routed to its owner (routed={routed}).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] shadow-safe OnRemove: {ex.Message}"); }
            }
        }

        // AI rival poach-defense (the long-standing phantom-poach edge, backlog → closed): under a
        // merger flip the partner's regs read RentedByPlayer=true, so the AI could select an
        // injected MIRROR as its poach target — a poach of an employee that only exists as a copy.
        // ALL injected records excluded from the AI's view.
        [HarmonyPatch(typeof(BigAmbitions.Rivals.RivalDefenseHelper),
                      nameof(BigAmbitions.Rivals.RivalDefenseHelper.ActivateHireEmployees))]
        public static class Patch_RivalDefense_HideInjected
        {
            static void Prefix(out object? __state) => __state = StashInjected(alsoMergedPartners: true);
            static void Finalizer(object? __state) => RestoreInjected(__state);
        }

        // Round-94 SECOND LOOK (user-mandated "don't revisit"): the census covered helper CALLERS;
        // a direct-field sweep found two GLOBAL raw accesses bypassing the helper entirely. The
        // five other direct sites (HeadquartersList, the four plan lists) are address-scoped and
        // stay per the round-80 rule. The contacts/dictionary family is the SEPARATE round-63
        // stale-contact class — tracked in the backlog, not silently folded in here.

        // Hourly prompt variables — EMPLOYEE_COUNT was the raw list count.
        [HarmonyPatch(typeof(Extensions.GamePromptHelper), nameof(Extensions.GamePromptHelper.RunHourly))]
        public static class Patch_GamePrompt_HideInjected
        {
            static void Prefix(out object? __state) => __state = StashInjected(alsoMergedPartners: false);
            static void Finalizer(object? __state) => RestoreInjected(__state);
        }

        // Tutorial pointer hide-condition — "any employee training" read the raw list.
        [HarmonyPatch(typeof(Tutorial.TutorialPointerHideConditionIfPlayerHasEmployeesTraining), "ConditionMetInternal")]
        public static class Patch_TutorialTrainingPointer_HideInjected
        {
            static void Prefix(out object? __state) => __state = StashInjected(alsoMergedPartners: false);
            static void Finalizer(object? __state) => RestoreInjected(__state);
        }

        // ── Round-88 (user-approved): street releases clear a hand truck's street data ────────
        // Native writes street data at grab (HandTruck:184) and at release-INDOORS
        // (VehicleController:342-347) — but a release on the STREET writes nothing, so a cart
        // grabbed indoors keeps the building tag while genuinely sitting outdoors, and the
        // cross-interior mask hides it from every other machine (invisible street cart) until
        // the next grab re-stamps it. Clear the tag exactly where native would have written it.
        // MP-gated — single-player keeps vanilla data untouched. Covers Flatbed (a HandTruck
        // subclass) via the base-type patch.
        [HarmonyPatch(typeof(HandTruck), nameof(HandTruck.ExitVehicle))]
        public static class Patch_HandTruckExit_StreetDataClear
        {
            static void Postfix(HandTruck __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                    if (BuildingManager.IsInsideBuilding || Parking.UndergroundParking.UndergroundParkingManager.IsInsideParking) return;   // native wrote these cases
                    var inst = __instance?.vehicleInstance;
                    if (inst == null || string.IsNullOrEmpty(inst.streetName)) return;   // already street-tagged
                    inst.SetStreetData(string.Empty, 0);
                    Plugin.Logger.LogInfo($"[Vehicle] street release: cleared stale building tag on '{inst.id}' (round-88 — native only writes on indoor releases).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Vehicle] street-release tag clear: {ex.Message}"); }
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
            private static int _deadAiCarSkipCount = 0;   // H-EXIT-1
            static bool Prefix(UnityEngine.MonoBehaviour __instance, System.Reflection.MethodBase __originalMethod)
            {
                // H-EXIT-1 (bundle 20260904-024801, PR #5): AiCarMusic never unsubscribes its two lambdas and vanilla never destroys a
                // traffic car — the mod does (stripped look-alikes). A DESTROYED instance's handler would dereference its dead AudioSource /
                // isActiveAndEnabled inside the game's enter/exit coroutine and kill it before the fade-in (host black screen). Unity's
                // overloaded == is true for a destroyed component: skip exactly those, on any role; live cars run as vanilla.
                if (__instance == null)
                {
                    if (_deadAiCarSkipCount++ < 20 || _deadAiCarSkipCount % 500 == 0)
                        Plugin.Logger.LogWarning($"[AiCarMusic] {__originalMethod?.Name} skipped — its car was destroyed (dead subscriber, #{_deadAiCarSkipCount}) (H-EXIT-1).");
                    return false;
                }
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
                if (!MPClient.IsClientInWorld) return true;
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
                if (!MPClient.IsClientInWorld) return true;
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
                if (!MPClient.IsClientInWorld) return true;
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
                if (!MPClient.IsClientInWorld) return true;
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
                if (!MPClient.IsClientInWorld) return true;
                Plugin.Logger.LogInfo("[Suppress] SetRivalBuildings skipped on client.");
                return false;
            }
        }

        // Round-39b (client joins a HOST'S NEW GAME, log-proven): the suppressions above silence the rival
        // data PRODUCERS on clients, but the CONSUMER — RivalsHelper.CheckRivalTimelines — was left running.
        // On an existing-save join the rival states load from the synced save, so it never threw; on a NEW
        // game there is nothing, and RivalTimeline.Check NREs on the missing state. That single NRE has two
        // separate blast radii, both observed: (1) it fires INSIDE CityGenerator.InitializeCity
        // (MarkBuildingsForRentAndGetAvailableOnes → ShutdownBusiness(updateRivals:true)) and aborts
        // CityManager.OnScenesLoaded — everything after city init (player spawn placement, world streaming
        // setup) never runs → the client spawns in the staging area and gets a grey unstreamed world on
        // building exit; (2) it fires from RivalsHelper.RunHourly and aborts the client's main game tick
        // every hour (the same tick-abort class as the June "HQ stopped working" bug). Rival timeline
        // simulation is HOST domain (messages, activation, defenses — results reach clients via sync);
        // skip the whole check on clients.
        [HarmonyPatch]
        public static class Patch_RivalsHelper_CheckRivalTimelines_SkipOnClient
        {
            // BOTH variants (field NRE 2026-07-16): the hourly sweep
            // (CheckRivalTimelines, plural) AND the per-neighborhood check
            // (CheckRivalTimeline(string), singular — fires on neighborhood
            // entry) — the singular slipped through the original patch and
            // NRE'd RivalTimeline.Check on a client.  Same rationale: rival
            // machinery is host-authoritative.
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("BigAmbitions.Rivals.RivalsHelper");
                if (t == null) yield break;
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly))
                    if (m.Name == "CheckRivalTimelines" || m.Name == "CheckRivalTimeline")
                        yield return m;
            }

            static bool Prefix()
            {
                if (!MPClient.IsClientInWorld) return true;
                return false;   // silent — the sweep runs hourly; one log line per session would still be noise on fast-forward
            }
        }

        /// <summary>RIVAL-FAIR-2 R3b (2026-09-12): RivalDefenseHelper.RunHourly is the rival WAR clock —
        /// it expires defenseStates whose timestamp is past and re-prices the rival's shops
        /// (decompile :37-63). A client ran it locally, so a client could END a war the host is still
        /// fighting. The host's expiry now reaches every machine through R3 (the rival-state publish)
        /// and the rival's own retail prices through BusinessSync / MPPriceSync, so the client has
        /// nothing to compute: skip it, exactly as the timeline sweep beside it is skipped.</summary>
        [HarmonyPatch]
        public static class Patch_RivalDefenseHelper_RunHourly_SkipOnClient
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = VehicleManager.FindGameType("BigAmbitions.Rivals.RivalDefenseHelper");
                if (t == null) yield break;
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Static  | System.Reflection.BindingFlags.DeclaredOnly))
                    if (m.Name == "RunHourly")
                        yield return m;
            }

            static bool Prefix()
            {
                if (!MPClient.IsClientInWorld) return true;
                return false;   // silent, like the timeline skip above: hourly
            }
        }

        /// <summary>RIVAL-FAIR-2 R3 (2026-09-12): the HOST recomputes its rival-state signature after
        /// every method that can move it — the hourly timeline sweep and the per-neighbourhood check
        /// (RivalsHelper.CheckRivalTimelines / CheckRivalTimeline, the same pair the skip patch above
        /// binds), the defense clock (RivalDefenseHelper.RunHourly) and the two activations
        /// (ActivatePriceReduction / ActivateLowDemand) — and publishes to every client when it differs
        /// from the last sent. Host-only; a postfix, so it sees the state the call left behind (and it
        /// still runs on a client whose prefix skipped the body, where IsRunning refuses it).</summary>
        [HarmonyPatch]
        public static class Patch_RivalState_PublishOnChange
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                       | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
                var rh = VehicleManager.FindGameType("BigAmbitions.Rivals.RivalsHelper");
                if (rh != null)
                    foreach (var m in rh.GetMethods(F))
                        if (m.Name == "CheckRivalTimelines" || m.Name == "CheckRivalTimeline")
                            yield return m;
                var dh = VehicleManager.FindGameType("BigAmbitions.Rivals.RivalDefenseHelper");
                if (dh != null)
                    foreach (var m in dh.GetMethods(F))
                        if (m.Name == "RunHourly" || m.Name == "ActivatePriceReduction" || m.Name == "ActivateLowDemand")
                            yield return m;
            }

            static void Postfix(System.Reflection.MethodBase __originalMethod)
            {
                try
                {
                    if (!MPServer.IsRunning) return;
                    MPServer.PublishRivalStateIfChanged(__originalMethod?.Name ?? "?");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[RivalSync] publish hook: {ex.Message}"); }
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
                    if (!MPClient.IsClientInWorld && !MPServer.IsRunning) return;
                    GameStatePatcher.RivalsLeaderboardLoadRunning = true;
                    MergerRivalsFold.BeginLoad();   // merger fold: this Load's folded-company cache
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
                                // Round-25 parity: primary neighborhood is now published for players —
                                // render it (was blank/arbitrary: the native calc groups the synthetic
                                // rd's replica regs whose dailyIncomes are empty here).
                                try { if (!string.IsNullOrEmpty(ps.MostActiveNeighborhood)) lb.mostActiveNeighborhood = ps.MostActiveNeighborhood; } catch { }
                            }
                            // MERGER Phase 1-B (B2): when this pid anchors a foreign merged company, the
                            // row IS that company - name, income and lists are the members' sum/union,
                            // not the anchor's own (which the populate above just restored).
                            MergerRivalsFold.ApplyToLeaderboardRow(id, rd, lb);
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetRivalLeaderboardData] player row '{id}': {ex.Message}"); }
                        return;
                    }

                    // Only the client needs the rest of this Postfix's work
                    // (populating ownedBusinesses + overriding lb.weeklyIncome).
                    // On host, the natural game calc is authoritative.
                    if (!MPClient.IsClientInWorld) return;

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

            /// <summary>Backstop (field 2026-07-16: Load died ×3 mid-build and the
            /// Rivals page never rendered): if the native row build throws for ANY
            /// entry, substitute a minimal safe row instead of letting one bad
            /// entry take down the whole page.  The primary fix is the
            /// OnRivalDefeat player guard below; this catches whatever else the
            /// native path may throw on.  Skipped-postfix caveat: a substituted
            /// row misses the income/name overrides above — acceptable, it only
            /// exists so the page loads.</summary>
            static Exception? Finalizer(Exception? __exception, object __0, ref object? __result)
            {
                if (__exception == null) return null;
                try
                {
                    var rd = __0 as BigAmbitions.Rivals.RivalData;
                    string id   = rd?.id ?? "";
                    string name = rd?.rivalName ?? "";
                    try { if (GameStatePatcher.ClientRivalStats.TryGetValue(id, out var ps) && !string.IsNullOrEmpty(ps.Name)) name = ps.Name; } catch { }
                    __result = new UI.Smartphone.Apps.Rivals.RivalLeaderboardData
                    {
                        rivalId         = id,
                        entryName       = string.IsNullOrEmpty(name) ? "Unknown" : name,
                        ageInYears      = 0,
                        weeklyIncome    = 0f,
                        isDefeated      = false,
                        ownedBusinesses = rd?.ownedRetailOfficeBusinesses ?? new(),
                        ownedBuildings  = rd?.ownedBuildings ?? new(),
                        mostActiveNeighborhood = "",
                    };
                    Plugin.Logger.LogWarning($"[Patch_GetRivalLeaderboardData_FromCache] row build threw for '{id}' ({__exception.GetType().Name}: {__exception.Message}) — safe row substituted so the page loads.");
                    return null;   // swallow: the page keeps loading
                }
                catch { return __exception; }   // backstop itself failed — surface the original
            }
        }

        /// <summary>Session players are NOT defeatable rivals.  The leaderboard's
        /// row builder flags any entry with negative weekly income or zero
        /// retail/office businesses as defeated and calls
        /// RivalsHelper.OnRivalDefeat on it — for a remote player's synthetic
        /// stub (fresh player: 0 businesses) that runs the game's rival-shutdown
        /// machinery (shutdown businesses / sell real estate / clear state)
        /// against a PLAYER id, and NREs on the rival-state we deliberately
        /// strip for players (field 2026-07-16: Rivals page died ×3 on load).
        /// Skip the whole method for session-player ids.</summary>
        [HarmonyPatch(typeof(BigAmbitions.Rivals.RivalsHelper), "OnRivalDefeat")]
        public static class Patch_RivalsHelper_OnRivalDefeat_PlayerGuard
        {
            private static readonly HashSet<string> _logged = new();
            static bool Prefix(BigAmbitions.Rivals.RivalData rival)
            {
                try
                {
                    string id = rival?.id ?? "";
                    if (string.IsNullOrEmpty(id) || !GameStatePatcher.IsSessionPlayerId(id)) return true;
                    if (_logged.Add(id))
                        Plugin.Logger.LogInfo($"[Patch_OnRivalDefeat] skipped for session player '{id}' — players can't be defeated as rivals.");
                    return false;
                }
                catch { return true; }   // never block the real rival path on a guard error
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
                    if (!MPClient.IsClientInWorld && !MPServer.IsRunning && !MPClient.OfflineFork) return true;   // H-FORK-1: holds in the offline fork
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
        // ===== MERGER Phase 1-B: one rivals row per merged company (map SS25) =====
        /// <summary>
        /// Folds a merged company's members into ONE rivals-list entry (user requirement 2026-09-10:
        /// a merged company is ONE entry, every figure the SUM of the members', separate groups stay
        /// separate). Everything here is computed locally from state this machine already holds - no
        /// protocol change, and with no merger in the session every branch is a no-op.
        ///
        /// INCOME IS SUMMED OVER DE-DUPLICATED PER-BUSINESS ROWS, NOT PER-MEMBER TOTALS. A member's
        /// published WeeklyIncome is FinancialSummaryHelper.GetLastFinancialSummaries(7).totalProfit
        /// (RivalSelfStats.Build). Patch_FinancialSummary_OwnBusinessesOnly EXCLUDES merger-FLIPPED
        /// partner shops from each member's daily summary: CreateFinancialSummary is a step of
        /// Patch_MergerAuthorityVeil (:3506) and the veil un-flips every flipped registration for the
        /// duration of that pass, so a member's own books hold only what they themselves operate.
        /// Address-keyed de-duplication stands on its own merit - it is what makes the company total
        /// independent of WHICH member is operating an address on any given day.
        ///
        /// ONE BUSINESS DEFINITION for every member including the local one: the NATIVE leaderboard
        /// test (RivalLeaderboard.GetPlayerLeaderboardData decompile :73 - generatesrevenue-tagged, or
        /// the factory) applied to the local BuildingRegistration behind each address. The published
        /// address sets (RivalSelfStats: every non-residential rented reg) only say WHICH addresses
        /// belong to the company; they never decide what counts as a business.
        /// </summary>
        internal static class MergerRivalsFold
        {
            /// <summary>One company's folded totals.</summary>
            internal sealed class Company
            {
                internal float  WeeklyIncome;
                internal int    KnownMembers;      // r2/R2: members whose stats row was present
                internal int    NoFigures;         // r2/R2: members with no stats row (offline)
                internal string Neighborhood = "";
                internal readonly System.Collections.Generic.List<BuildingRegistration> Businesses = new();
                internal readonly System.Collections.Generic.List<BuildingRegistration> Buildings  = new();
            }

            /// <summary>Foreign companies folded during THIS leaderboard Load, keyed by the anchor pid
            /// whose row carries them. Filled in the GetAllRivalData Postfix (which runs first), read in
            /// the GetRivalLeaderboardData Postfix - so the pass over registrations happens once.</summary>
            private static readonly System.Collections.Generic.Dictionary<string, Company> _anchorRows = new();

            internal static void BeginLoad() { try { _anchorRows.Clear(); } catch { } }

            /// <summary>The native leaderboard's business test (GetPlayerLeaderboardData :73).</summary>
            private static bool NativeRevenueBusiness(BuildingRegistration reg)
            {
                try
                {
                    if (reg.businessTypeName == "ba:businesstype_factory") return true;
                    return BusinessTypeHelper.GetData(reg).HasTag(BigAmbitions.Tags.TagRef.Businesstag.generatesrevenue);
                }
                catch { return false; }
            }

            /// <summary>Weekly income for one company address: the host-authoritative figure when the
            /// stats snapshot carries it, else the local registration's own average.</summary>
            private static float IncomeOf(string key, BuildingRegistration reg)
            {
                if (GameStatePatcher.ClientBusinessIncomeByAddress.TryGetValue(key, out var inc)) return inc;
                try { return reg.GetAvgWeeklyIncome(); } catch { return 0f; }
            }

            /// <summary>Sum/union one company. <paramref name="memberPids"/> are its ONLINE members;
            /// <paramref name="group"/> is the company itself - its BuildingKeys cover members who are
            /// OFFLINE; <paramref name="includeLocalPlayer"/> adds THIS machine's own operations (true
            /// only for MY company - the local player is the one member no snapshot row describes).
            /// Null when nothing could be resolved.
            ///
            /// r2/R1 ADDRESS SOURCE: the union over the members of ClientRivalStats[pid].Businesses[]
            /// .AddressKey (host-authoritative; present on the HOST and on clients alike) UNION the
            /// group's BuildingKeys (Protocol.cs:599 - every building operated by ANY member, offline
            /// included). ClientRivalBusinessAddrs is NEVER read here: it is written only on the client
            /// apply path (GameStatePatcher.cs:808), so on the HOST every foreign company folded to
            /// $0 / 0 businesses.
            /// r2/R2 INCOME SCALE: WeeklyIncome is the SUM of the members' self-reported NET figures
            /// (RivalStatsInfo.WeeklyIncome - the same FinancialSummaryHelper totalProfit the native
            /// local row and every remote player row use), NOT the per-business GROSS sum: one sort
            /// field, one scale. The LOCAL player's own contribution is left to the native row (see the
            /// GetPlayerLeaderboardData Postfix). No double count - a partner's flipped replica
            /// registrations report $0 per-business and MergerWallet mirrors the balance without a
            /// transaction, so neither touches a member's summary. Per-business figures now weight
            /// mostActiveNeighborhood only.</summary>
            internal static Company? Build(MergerGroupInfo? group,
                                           System.Collections.Generic.IEnumerable<string> memberPids,
                                           bool includeLocalPlayer)
            {
                try
                {
                    var owners   = new System.Collections.Generic.HashSet<string>();
                    var bizAddrs = new System.Collections.Generic.HashSet<string>();
                    float netIncome = 0f; int known = 0, noFigures = 0;
                    foreach (var pid in memberPids)
                    {
                        if (string.IsNullOrEmpty(pid) || pid == MPConfig.PlayerId) continue;
                        owners.Add(pid);
                        if (GameStatePatcher.ClientRivalStats.TryGetValue(pid, out var st) && st != null)
                        {
                            known++;
                            netIncome += st.WeeklyIncome;
                            if (st.Businesses != null)
                                foreach (var b in st.Businesses)
                                    if (b != null && !string.IsNullOrEmpty(b.AddressKey)) bizAddrs.Add(b.AddressKey);
                        }
                        else noFigures++;      // B4: an ONLINE member who has not self-reported yet - counted, logged once per Load
                    }
                    if (group?.BuildingKeys != null)
                        foreach (var a in group.BuildingKeys) if (!string.IsNullOrEmpty(a)) bizAddrs.Add(a);
                    if (!includeLocalPlayer && owners.Count == 0 && bizAddrs.Count == 0) return null;

                    var gi = SaveGameManager.Current;
                    if (gi == null || gi.BuildingRegistrations == null) return null;

                    var biz  = new System.Collections.Generic.Dictionary<string, BuildingRegistration>();
                    var bldg = new System.Collections.Generic.Dictionary<string, BuildingRegistration>();
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        try
                        {
                            string key = GameStateReader.AddressKey(reg);
                            if (string.IsNullOrEmpty(key)) continue;
                            // Businesses: a member's published address set, plus - for MY company - every
                            // shop this machine itself rents, which under the slice-3 ownership flip
                            // already includes partners' shops. The dictionary de-duplicates the overlap.
                            bool localRented = includeLocalPlayer && reg.RentedByPlayer;
                            if ((localRented || bizAddrs.Contains(key)) && NativeRevenueBusiness(reg)) biz[key] = reg;
                            // Real estate: bought by this machine, or ledgered to a member.
                            bool ownsBuilding = includeLocalPlayer && reg.BuildingOwnedByPlayer;
                            if (!ownsBuilding)
                            {
                                string owner = reg.buildingOwnerRivalId?.ToString() ?? "";
                                ownsBuilding = owner.Length > 0 && owners.Contains(owner);
                            }
                            if (ownsBuilding) bldg[key] = reg;
                        }
                        catch { }
                    }

                    var co   = new Company();
                    co.WeeklyIncome = netIncome;                 // r2/R2: members' NET figures, not per-business gross
                    co.KnownMembers = known; co.NoFigures = noFigures;
                    var hood = new System.Collections.Generic.Dictionary<string, float>();
                    foreach (var kv in biz)
                    {
                        float wk = IncomeOf(kv.Key, kv.Value);   // weights the neighborhood pick ONLY
                        co.Businesses.Add(kv.Value);
                        string h = ""; try { h = kv.Value.Neighborhood ?? ""; } catch { }
                        if (h.Length > 0) { hood.TryGetValue(h, out var s); hood[h] = s + wk; }
                    }
                    foreach (var kv in bldg) co.Buildings.Add(kv.Value);
                    float best = float.MinValue;
                    foreach (var kv in hood) if (kv.Value > best) { best = kv.Value; co.Neighborhood = kv.Key; }
                    return co;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] rivals fold build: {ex.Message}"); return null; }
            }

            /// <summary>The pid a FOREIGN company's single row is keyed on: its founder when that member
            /// is online, else the first online member in join order. ALWAYS a real session pid - a row
            /// keyed by a GroupId would slip past the OnRivalDefeat player guard and the save-time
            /// synthetic-state strip, both of which recognise player ids only.</summary>
            internal static string AnchorPid(MergerGroupInfo g)
            {
                if (g == null) return "";
                try
                {
                    string f = g.FounderPid ?? "";
                    if (f.Length > 0 && GameStatePatcher.ClientPlayerRoster.ContainsKey(f)) return f;
                    foreach (var pid in g.MemberPidsOrdered ?? new System.Collections.Generic.List<string>())
                        if (!string.IsNullOrEmpty(pid) && GameStatePatcher.ClientPlayerRoster.ContainsKey(pid)) return pid;
                }
                catch { }
                return "";
            }

            /// <summary>Is this remote pid folded away - a member of MY company, or a non-anchor member of
            /// a foreign one? Such a pid must never become a leaderboard row of its own.</summary>
            internal static bool IsFoldedAway(string pid)
            {
                if (string.IsNullOrEmpty(pid) || !MergerSync.AnyGroup) return false;
                if (MergerSync.IsMemberPid(pid)) return true;              // my company: folded onto MY row
                try
                {
                    foreach (var g in MergerSync.ForeignGroups)
                    {
                        var pids = g.MemberPidsOrdered;
                        if (pids == null || !pids.Contains(pid)) continue;
                        return pid != AnchorPid(g);                        // only the anchor survives
                    }
                }
                catch { }
                return false;
            }

            /// <summary>The foreign company whose single row this pid anchors, or null.</summary>
            internal static MergerGroupInfo? ForeignCompanyAnchoredBy(string pid)
            {
                if (string.IsNullOrEmpty(pid) || !MergerSync.AnyGroup) return null;
                try { foreach (var g in MergerSync.ForeignGroups) if (AnchorPid(g) == pid) return g; }
                catch { }
                return null;
            }

            private static void Fill(System.Collections.Generic.List<BuildingRegistration>? dst,
                                    System.Collections.Generic.List<BuildingRegistration> src)
            {
                if (dst == null) return;
                dst.Clear();
                foreach (var r in src) dst.Add(r);
            }

            /// <summary>B2, step 1 - before the synthetic anchor row reaches the native row builder,
            /// give it the company's NAME and the members' UNIONED lists. Doing it here (not only on the
            /// finished row) also keeps GetRivalLeaderboardData :104 from reading a zero-business anchor
            /// and calling DefeatRival on a company that plainly owns shops.</summary>
            internal static void ApplyToRivalData(BigAmbitions.Rivals.RivalData rd, MergerGroupInfo g)
            {
                if (rd == null || g == null) return;
                try
                {
                    var co = Build(g, g.MemberPidsOrdered ?? new System.Collections.Generic.List<string>(), false);
                    if (co == null) return;
                    _anchorRows[rd.id ?? ""] = co;
                    string name = MergerSync.GroupDisplayName(g.GroupId);
                    if (!string.IsNullOrEmpty(name)) rd.rivalName = name;
                    Fill(rd.ownedBuildings, co.Buildings);
                    Fill(rd.ownedBusinesses, co.Businesses);
                    Fill(rd.ownedRetailOfficeBusinesses, co.Businesses);
                    Log(g.MemberPidsOrdered?.Count ?? 0, string.IsNullOrEmpty(name) ? (rd.rivalName ?? "") : name, co);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] foreign fold: {ex.Message}"); }
            }

            /// <summary>B2, step 2 - the finished row. The player-row branch above rebuilt rd's lists
            /// from the ANCHOR's own ownership and copied them onto lb; put the company back on both.</summary>
            internal static void ApplyToLeaderboardRow(string anchorPid, BigAmbitions.Rivals.RivalData rd,
                                                      UI.Smartphone.Apps.Rivals.RivalLeaderboardData lb)
            {
                if (lb == null || string.IsNullOrEmpty(anchorPid) || !MergerSync.AnyGroup) return;
                if (!_anchorRows.TryGetValue(anchorPid, out var co) || co == null) return;
                var g = ForeignCompanyAnchoredBy(anchorPid);
                if (g == null) return;
                try
                {
                    string name = MergerSync.GroupDisplayName(g.GroupId);
                    if (!string.IsNullOrEmpty(name)) lb.entryName = name;
                    lb.weeklyIncome = co.WeeklyIncome;
                    lb.ownedBusinesses = co.Businesses;
                    lb.ownedBuildings  = co.Buildings;
                    lb.mostActiveNeighborhood = co.Neighborhood ?? "";
                    lb.isDefeated = false;
                    if (rd != null)
                    {
                        Fill(rd.ownedBuildings, co.Buildings);
                        Fill(rd.ownedBusinesses, co.Businesses);
                        Fill(rd.ownedRetailOfficeBusinesses, co.Businesses);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] foreign row: {ex.Message}"); }
            }

            /// <summary>B5 - one INFO line per company folded on a leaderboard load.</summary>
            internal static void Log(int members, string name, Company co)
            {
                string offline = co.NoFigures > 0 ? $", {co.NoFigures} member(s) without figures" : "";   // B4: members push their figures (B1), so a gap is 'not yet', not 'offline'
                Plugin.Logger.LogInfo(
                    $"[Merger] rivals list: folded {members} member(s) into '{name}' " +
                    $"(income ${co.WeeklyIncome:N0}, businesses {co.Businesses.Count}, buildings {co.Buildings.Count}{offline})");
            }
        }

        // ===== MERGER Phase 1-B (B1b): the LOCAL player's row IS the company row =====
        // RivalLeaderboard.GetPlayerLeaderboardData (decompile :68-99) builds the local player's row
        // natively from local data with rivalId == null, and Load appends it at :28 - the host stats
        // snapshot never touches it. When I am merged, rewrite that ONE row into the COMPANY row:
        // company name, summed income, unioned business / real-estate lists, recomputed primary
        // neighborhood. rivalId STAYS null - that is the native player-row contract the detail view and
        // its charts read. Not merged => untouched, so single-player and unmerged sessions see no change.
        [HarmonyPatch]
        public static class Patch_RivalLeaderboard_GetPlayerLeaderboardData_MergerFold
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("UI.Smartphone.Apps.Rivals.RivalLeaderboard")?.GetMethod("GetPlayerLeaderboardData",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);

            static void Postfix(UI.Smartphone.Apps.Rivals.RivalLeaderboardData __result)
            {
                try
                {
                    if (__result == null || !MergerSync.IAmMember) return;
                    var members = MergerSync.MyMemberPidsOrdered;
                    var co = MergerRivalsFold.Build(MergerSync.MyGroup, members, true);
                    if (co == null) return;
                    string name = MergerSync.MyGroupDisplayName;
                    if (!string.IsNullOrEmpty(name)) __result.entryName = name;
                    // r2/R2: the native value IS the local member's NET contribution (and already
                    // includes the $0 replica rows), so ADD the other members' NET figures to it.
                    __result.weeklyIncome += co.WeeklyIncome;
                    __result.ownedBusinesses = co.Businesses;
                    __result.ownedBuildings  = co.Buildings;
                    __result.mostActiveNeighborhood = co.Neighborhood ?? "";
                    int folded = 0;
                    foreach (var pid in members) if (!string.IsNullOrEmpty(pid) && pid != MPConfig.PlayerId) folded++;
                    MergerRivalsFold.Log(folded, string.IsNullOrEmpty(name) ? (__result.entryName ?? "") : name, co);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_GetPlayerLeaderboardData_MergerFold] {ex.Message}"); }
            }
        }

        // ===== MERGER Phase 1-B (B1c): a company name is longer than one player's name =====
        // RivalLeaderboardButton.SetUp (decompile :54-71) pushes data.entryName into a private
        // TextMeshProUGUI laid out for a single character name. Turn word wrapping on and cap the
        // overflow with an ellipsis so a multi-member company name degrades gracefully instead of
        // clipping mid-glyph. The prefab's row height is NOT touched - the leaderboard instantiates its
        // rows into a parent that sizes them, and growing one row there would desynchronise the list.
        [HarmonyPatch]
        public static class Patch_RivalLeaderboardButton_SetUp_MergerWrap
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("UI.Smartphone.Apps.Rivals.RivalLeaderboardButton")?.GetMethod("SetUp",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);

            static void Postfix(TMPro.TextMeshProUGUI ___nameText)
            {
                try
                {
                    if (___nameText == null || !MergerSync.AnyGroup) return;   // inert with no merger anywhere
                    ___nameText.enableWordWrapping = true;
                    ___nameText.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RivalLeaderboardButton_SetUp_MergerWrap] {ex.Message}"); }
            }
        }

        // ===== MERGER PHASE 4d (R4): the RIVALS DETAIL PANE =====
        // The leaderboard row already wraps a company name (above). The DETAIL pane's own name field does not -
        // SelectedRivalUI.ShowRival pushes data.entryName into the private `rivalNameField` (decompile
        // UI.Smartphone.Apps.Rivals/SelectedRivalUI.cs:29 declares it, :117 sets it), so "A & B & C" clipped
        // there exactly as the row did before 1-B. Same two-line fix, different field.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.Rivals.SelectedRivalUI), nameof(UI.Smartphone.Apps.Rivals.SelectedRivalUI.ShowRival))]
        public static class Patch_SelectedRivalUI_ShowRival_MergerWrap
        {
            static void Postfix(TMPro.TextMeshProUGUI ___rivalNameField)
            {
                try
                {
                    if (___rivalNameField == null || !MergerSync.AnyGroup) return;   // inert with no merger anywhere
                    ___rivalNameField.enableWordWrapping = true;
                    ___rivalNameField.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_SelectedRivalUI_ShowRival_MergerWrap] {ex.Message}"); }
            }
        }

        // The detail pane's two tables list the company's buildings with no hint of WHOSE they are. D18: rows that
        // clearly belong to another member are coloured; the two summary fields above them (business count, weekly
        // income - SelectedRivalUI.cs:122-123) are SUMS and stay plain, as does each row's own money column.
        // The owner comes from the model's Address through the flip-proof lookup, so a company building whose
        // rival id the merger blanked still names its owner. Cells are RECYCLED, so every cell's own colours are
        // captured once and restored on any row that is not another player's (SharedShopVisibility.cs:465-471).
        private sealed class RivalRowColors { public UnityEngine.Color A, B, C; public bool Captured; }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            UI.Smartphone.Apps.Rivals.Tables.RivalBusinessesCellView, RivalRowColors> _rivalBizDefaults = new();

        private static bool TryRowOwnerColour(Address address, out UnityEngine.Color c)   // Address lives in the GLOBAL namespace
        {
            c = UnityEngine.Color.white;
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return false;
                string key = GameStateReader.AddressKey(address);
                if (string.IsNullOrEmpty(key)) return false;
                // 4d r2 (review MINOR-2): RealEstateCellView also draws the Market Insider list, and this helper is
                // the one gate both tables share — so the tint is CO-MEMBER ONLY (D18: colour on the listed rows of
                // another MEMBER). A non-merged session, and a non-member's building in any session, keep the game's
                // own colours exactly as before 4d.
                string owner = PlayerColours.FlipProofOwner(key);
                if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) return false;
                if (!MergerSync.MergedRuntime(owner, MPConfig.PlayerId)) return false;
                if (!PlayerColours.TryColourFor(owner, out var c32)) return false;
                c = (UnityEngine.Color)c32;
                return true;
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.Rivals.Tables.RivalBusinessesCellView),
                      nameof(UI.Smartphone.Apps.Rivals.Tables.RivalBusinessesCellView.SetData))]
        public static class Patch_RivalBusinessesCellView_SetData_OwnerTint
        {
            static void Postfix(UI.Smartphone.Apps.Rivals.Tables.RivalBusinessesCellView __instance,
                                UI.Smartphone.Apps.Rivals.Tables.RivalBusinessesCellView.RivalBusinessModel data)
            {
                try
                {
                    if (__instance == null || data == null) return;
                    var typeText = HousingMapCues.GetMember(__instance.businessType, "TextContainer") as TMPro.TMP_Text;
                    var addrText = HousingMapCues.GetMember(__instance.address, "TextContainer") as TMPro.TMP_Text;
                    var def = _rivalBizDefaults.GetOrCreateValue(__instance);
                    if (!def.Captured)
                    {
                        def.A = __instance.businessName != null ? __instance.businessName.color : UnityEngine.Color.white;
                        def.B = typeText != null ? typeText.color : UnityEngine.Color.white;
                        def.C = addrText != null ? addrText.color : UnityEngine.Color.white;
                        def.Captured = true;
                    }
                    bool paint = TryRowOwnerColour(data.Address, out var oc);
                    if (__instance.businessName != null) __instance.businessName.color = paint ? oc : def.A;
                    if (typeText != null) typeText.color = paint ? oc : def.B;
                    if (addrText != null) addrText.color = paint ? oc : def.C;
                    // weeklyIncome keeps the game's own red/white money colouring (D18: sums and money stay plain).
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RivalBusinessesCellView_SetData_OwnerTint] {ex.Message}"); }
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            UI.Smartphone.Apps.MarketInsider.RealEstateCellView, RivalRowColors> _realEstateDefaults = new();

        // The rivals REAL-ESTATE table is the market-insider cell view (decompile
        // UI.Smartphone.Apps.Rivals.Tables/RivalRealEstateTable.cs:10 - BaTable<RealEstateCellView, ...>), so this
        // one patch covers both screens: the tint is CO-MEMBER ONLY: TryRowOwnerColour one method up answers for a company
        // co-member and for nobody else, so a NON-MEMBER's building and a non-merged session both fall
        // through to the cell's own colours, restored below. Within that, a building nobody in the session owns resolves to no owner and the cell
        // is restored to the colours it shipped with.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.MarketInsider.RealEstateCellView),
                      nameof(UI.Smartphone.Apps.MarketInsider.RealEstateCellView.SetData))]
        public static class Patch_RealEstateCellView_SetData_OwnerTint
        {
            static void Postfix(UI.Smartphone.Apps.MarketInsider.RealEstateCellView __instance,
                                UI.Smartphone.Apps.MarketInsider.RealEstateCellView.RealEstateModel data)
            {
                try
                {
                    if (__instance == null || data == null) return;
                    var addrText = HousingMapCues.GetMember(__instance.address, "TextContainer") as TMPro.TMP_Text;
                    var typeText = HousingMapCues.GetMember(__instance.buildingType, "TextContainer") as TMPro.TMP_Text;
                    var def = _realEstateDefaults.GetOrCreateValue(__instance);
                    if (!def.Captured)
                    {
                        def.A = addrText != null ? addrText.color : UnityEngine.Color.white;
                        def.B = typeText != null ? typeText.color : UnityEngine.Color.white;
                        def.C = __instance.totalSize != null ? __instance.totalSize.color : UnityEngine.Color.white;
                        def.Captured = true;
                    }
                    bool paint = TryRowOwnerColour(data.Address, out var oc);
                    if (addrText != null) addrText.color = paint ? oc : def.A;
                    if (typeText != null) typeText.color = paint ? oc : def.B;
                    if (__instance.totalSize != null) __instance.totalSize.color = paint ? oc : def.C;
                    // estimatedValue and price keep the game's own colours (money stays plain).
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RealEstateCellView_SetData_OwnerTint] {ex.Message}"); }
            }
        }
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
                    if (!MPClient.IsClientInWorld && !MPServer.IsRunning) return;
                    if (GameStatePatcher.ClientPlayerRoster.Count == 0) return;
                    if (__result == null) return;

                    // GetAllRivalData returns a freshly-built List<RivalData> each
                    // call (decompile RivalsHelper.cs:788 - new List<RivalData>(
                    // RivalDataCache.Values); there is NO filter), so we
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

                    int appended = 0, suppressed = 0;
                    foreach (var kv in GameStatePatcher.ClientPlayerRoster)
                    {
                        string pid = kv.Key;
                        if (string.IsNullOrEmpty(pid)) continue;
                        if (pid == MPConfig.PlayerId) continue;   // local player: game's GetPlayerLeaderboardData path
                        // MERGER Phase 1-B: a merged company is ONE entry. (B1a) the other members of MY
                        // company fold onto my own native row; (B2) a foreign company folds onto its
                        // FOUNDER's row - a real session pid, so the OnRivalDefeat player guard and the
                        // save-time synthetic-state strip still recognise it. Either way the folded-away
                        // members never appear as rivals of their own.
                        if (MergerRivalsFold.IsFoldedAway(pid)) { suppressed++; continue; }
                        var syn = GameStatePatcher.BuildSyntheticPlayerRivalData(pid);
                        if (syn == null) continue;
                        var foreign = MergerRivalsFold.ForeignCompanyAnchoredBy(pid);
                        if (foreign != null) MergerRivalsFold.ApplyToRivalData(syn, foreign);
                        list.Add(syn); appended++;
                    }
                    if (suppressed > 0)
                        Plugin.Logger.LogInfo($"[Patch_GetAllRivalData] merger: suppressed {suppressed} member row(s) - their company shows as one entry.");

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
                    // BOTH roles (round-25): the map holds synced authoritative incomes — on the CLIENT
                    // that's AI + other players (from the host snapshot); on the HOST that's other players'
                    // self-reported rows (its replicas have no order history → native calc showed $0 for a
                    // client's businesses). Addresses NOT in the map (own businesses; AI on the host)
                    // render natively as before.
                    if ((!MPClient.IsClientInWorld && !MPServer.IsRunning) || __0 == null) return;
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

        // ── Patch: EmployeeHelper.RunDaily — mod records sit out the daily pass ──
        // Field report 2026-07-16: "my colleague keeps getting messages that MY
        // employees are calling in sick / want to switch shifts" (+ 'On Duty
        // Staff' messages).  RunDaily iterates EVERY employee record on the
        // machine — including the partner-staff copies and duty synthetics we
        // inject for station staffing — and rolls sick days ("called in sick"
        // notifications), demands (shift-change requests), retirement notices,
        // and HR substitutions.  The injected copies' STATE drift was accepted
        // (roster re-sync overwrites it) but the MESSAGES had already reached
        // the wrong player.  Same remedy as the save path: strip the mod's
        // records for the duration of the native pass, restore right after
        // (Finalizer, so a native throw can't leave the roster stripped).
        // The owner's machine still generates everything for their own staff.
        // NOTE merger (on hold): merged-partner staff are also stripped here;
        // revisit if the merger ships message-sharing semantics.
        /// <summary>Shared strip/restore for native passes that iterate EVERY employee
        /// record (RunDaily, employee complaints, …): remove the mod's injected +
        /// synthetic records for the duration, restore after.  The owner's machine
        /// still generates everything for their own staff.</summary>
        internal static System.Collections.Generic.List<Entities.EmployeeInstance> StripModEmployeeRecords(string context)
        {
            var stripped = new System.Collections.Generic.List<Entities.EmployeeInstance>();
            long _pc = MPPerf.Begin();   // round-97: full-roster walk inside native passes — patch-cost bracketed
            // H-SCHEDWIPE-1: this pair also brackets the pass for the schedule scan, which must never read a shift the
            // SIMULATION removes in here (retirement calls UnassignEmployeeFromAllWorkshifts) as the player's own edit.
            SharedShopSchedule.InEmployeePass++;
            SharedShopSchedule.LastStripFrame   = UnityEngine.Time.frameCount;
            SharedShopSchedule.LastStripContext = context;
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return stripped;   // records only exist in MP anyway — and they SURVIVE the offline fork (H-FORK-1)
                var list = SaveGameManager.Current?.EmployeeInstances;
                if (list == null) return stripped;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    string id = list[i]?.id ?? "";
                    if (!id.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix) && !MPRegisterSync.IsInjectedStaff(id)) continue;
                    stripped.Add(list[i]!);
                    list.RemoveAt(i);
                    try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.Remove(id); } catch { }
                }
                if (stripped.Count > 0)
                    Plugin.Logger.LogInfo($"[StaffRoster] {stripped.Count} injected/synthetic record(s) sit out {context} (their messages stay on the owner's machine).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] strip ({context}): {ex.Message}"); }
            finally { MPPerf.PatchEnd("EmployeeStrip", _pc); }
            return stripped;
        }

        internal static void RestoreModEmployeeRecords(System.Collections.Generic.List<Entities.EmployeeInstance>? stripped, string context)
        {
            long _pc = MPPerf.Begin();   // round-97: both halves of the strip/restore cycle report as one site
            if (SharedShopSchedule.InEmployeePass > 0) SharedShopSchedule.InEmployeePass--;   // H-SCHEDWIPE-1 (see the strip half)
            SharedShopSchedule.LastRestoreFrame = UnityEngine.Time.frameCount;
            try
            {
                var list = SaveGameManager.Current?.EmployeeInstances;
                if (list != null && stripped != null)
                    foreach (var emp in stripped)
                    {
                        if (emp == null || string.IsNullOrEmpty(emp.id)) continue;
                        bool exists = false;
                        for (int i = 0; i < list.Count; i++)
                            if (list[i]?.id == emp.id) { exists = true; break; }
                        if (!exists) list.Add(emp);
                        try { Helpers.EmployeeHelper.EmployeeInstancesDictionary[emp.id] = emp; } catch { }
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] restore ({context}): {ex.Message}"); }
            finally { MPPerf.PatchEnd("EmployeeStrip", _pc); }
        }

        [HarmonyPatch(typeof(Helpers.EmployeeHelper), nameof(Helpers.EmployeeHelper.RunDaily))]
        public static class Patch_EmployeeHelper_RunDaily_SkipModRecords
        {
            static void Prefix(out System.Collections.Generic.List<Entities.EmployeeInstance> __state)
                => __state = StripModEmployeeRecords("the daily employee pass");

            static Exception? Finalizer(System.Collections.Generic.List<Entities.EmployeeInstance> __state, Exception __exception)
            {
                RestoreModEmployeeRecords(__state, "RunDaily");
                return __exception;   // never swallow the native pass's own failure
            }
        }

        // ── Round-95 (user-approved, closes the full message-pipeline audit) ─────────────────
        // H-MERGERTRAIN-1 (2026-09-19) leans on this patch and adds nothing of its own. EmployeeInstance
        // .RunHourly - which ends a training session and calls FinishTraining (+10 skill, a wage rise, a
        // to-do, the 'trainingfinished' GameEvent, FinishedTrainingEmployees) - has exactly ONE caller in
        // the whole decompile: Helpers/EmployeeHelper.cs:256, inside the roster walk below. The strip empties
        // SaveGameManager.Current.EmployeeInstances of every injected id for the duration of that pass, so a
        // COPY is never walked and can never finish a session. The owner's republish is what ends it on the
        // copy. (Native also keeps a training record out of the work offers on its own: TasksUI.cs:692 and
        // :707 skip trainingSession != null for both the assign-business and assign-shift to-dos, and
        // BusinessHelper.cs:1002 skips them in auto-fill - so no bogus assign can be routed from one.)
        // The HOURLY employee pass was the ONE remaining pipeline that could message FROM an
        // injected mirror (quit + low-satisfaction live in EmployeeInstance.RunHourly, driven by
        // EmployeeHelper.RunHourly's full-roster walk; the daily pass and the complaint pass were
        // stripped in earlier rounds). Same strip/restore as RunDaily. The Finalizer also purges
        // the caveat-1 RESIDUE: Employees-category contacts whose id (the contact key IS the
        // character name) matches a currently-injected mirror — entries minted on this machine
        // before the pipelines were closed. Native-legal by PRECEDENT (RemoveContactsWithNullId +
        // RegenerateWronglyGeneratedAIBusinessEmployees both RemoveAll on gi.Contacts). Orphan
        // husks from long-departed partners are indistinguishable from legitimately-departed own
        // staff (native deliberately KEEPS ex-employee contacts) and are left alone.
        [HarmonyPatch(typeof(Helpers.EmployeeHelper), nameof(Helpers.EmployeeHelper.RunHourly))]
        public static class Patch_EmployeeHelper_RunHourly_SkipModRecords
        {
            // MERGER PHASE 4b (PEOPLE) part 1: this same pass AGES the candidate list and DELETES at zero
            // (decompile Helpers/EmployeeHelper.cs:242-246). A partner's candidate copy must never be aged or
            // deleted here - its clock is the ORIGIN's, carried by the pool republish - so the copies sit the
            // pass out too and go straight back in the finalizer below (CompanyCandidates holds the stash:
            // Harmony allows only one __state, and this pass is main-thread and non-reentrant).
            static void Prefix(out System.Collections.Generic.List<Entities.EmployeeInstance> __state)
            {
                __state = StripModEmployeeRecords("the hourly employee pass");
                CompanyCandidates.StripForHourlyPass();
            }

            static Exception? Finalizer(System.Collections.Generic.List<Entities.EmployeeInstance> __state, Exception __exception)
            {
                RestoreModEmployeeRecords(__state, "RunHourly");
                CompanyCandidates.RestoreAfterHourlyPass();
                try
                {
                    if ((MPServer.IsRunning || MPClient.IsClientInWorld || MPClient.OfflineFork) && __state != null && __state.Count > 0)   // H-FORK-1: holds in the offline fork
                    {
                        var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                        foreach (var e in __state)
                            try { if (e != null && MPRegisterSync.IsInjectedStaff(e.id) && !MPRegisterSync.IsInjectedFromMergedPartner(e.id) && !string.IsNullOrEmpty(e.characterData?.name)) names.Add(e.characterData.name); } catch { }
                        // Round-95b (user-approved) NAME-COLLISION guard: contacts are keyed by character
                        // NAME, so a contact whose name also belongs to a NON-injected (own) employee holds
                        // the player's real message history — never purge those. Failure direction flips to
                        // "kept a phantom half", never "deleted yours".
                        // 2026-09-12: merged partners' staff are EXEMPT from this purge entirely
                        // (MPRegisterSync.IsInjectedFromMergedPartner, :1794). Their Employees contacts are the
                        // message relay's OWN, minted on purpose with the SENDER's category since phase 4b build B
                        // (CompanyMessages.cs:441-451) - purging them would delete a live relayed message and its
                        // buttons at the next game hour. No phantom is left behind: the exemption lapses with the
                        // membership (IsInjectedFromMergedPartner ends at MergerSync.IsMemberPid), so after a
                        // dissolve the purge collects those names again at the next game hour.
                        try
                        {
                            var all = Helpers.EmployeeHelper.GetEmployeeInstances();
                            if (all != null)
                                foreach (var e in all)
                                    try { if (e != null && !MPRegisterSync.IsInjectedStaff(e.id) && !string.IsNullOrEmpty(e.characterData?.name)) names.Remove(e.characterData.name); } catch { }
                        }
                        catch { }
                        var contacts = SaveGameManager.Current?.Contacts;
                        if (names.Count > 0 && contacts != null)
                        {
                            int removed = contacts.RemoveAll(c => c != null && c.IsEmployeeContact && c.id != null && names.Contains(c.id));
                            if (removed > 0)
                                Plugin.Logger.LogWarning($"[Patcher] purged {removed} stale partner-staff contact(s) (round-95 — minted before the message pipelines were closed; persists at next save).");
                        }
                    }
                }
                catch { }
                return __exception;   // never swallow the native pass's own failure
            }
        }

        /// <summary>Guard family member 8 (field 2026-07-20: "notice from on duty
        /// staff from another player's staff"): employee complaints enumerate the
        /// FULL roster OUTSIDE RunDaily, so the RunDaily strip never covered them —
        /// an injected partner record could be picked to complain, and the message
        /// (delivered via the staff contact) created the Contacts→Employees entry
        /// on the wrong machine.  Same strip/restore for the complaint pass.</summary>
        [HarmonyPatch(typeof(AI.Employees.ComplaintHelper), nameof(AI.Employees.ComplaintHelper.RunEmployeeComplaints))]
        public static class Patch_ComplaintHelper_RunEmployeeComplaints_SkipModRecords
        {
            static void Prefix(out System.Collections.Generic.List<Entities.EmployeeInstance> __state)
                => __state = StripModEmployeeRecords("the employee-complaint pass");

            static Exception? Finalizer(System.Collections.Generic.List<Entities.EmployeeInstance> __state, Exception __exception)
            {
                RestoreModEmployeeRecords(__state, "RunEmployeeComplaints");
                return __exception;
            }
        }

        /// <summary>Field 2026-07-20 ("employee hit skill level — but it's not my
        /// employee"): training completion adds the record to the STATIC
        /// FinishedTrainingEmployees dictionary AT COMPLETION TIME — outside the
        /// RunDaily strip window — and the notification at RunDaily's tail reads
        /// that dictionary directly, so the roster strip never protected it.
        /// Purge injected/synthetic entries just before the reader runs.</summary>
        [HarmonyPatch(typeof(Helpers.EmployeeHelper), "ShowFinishedTrainingEmployeeNotifications")]
        public static class Patch_TrainingNotifications_SkipModRecords
        {
            static void Prefix()
            {
                try
                {
                    var dict = Helpers.EmployeeHelper.FinishedTrainingEmployees;
                    if (dict == null || dict.Count == 0) return;
                    var dead = new System.Collections.Generic.List<Entities.EmployeeInstance>();
                    foreach (var kv in dict)
                    {
                        string id = kv.Key?.id ?? "";
                        if (id.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix) || MPRegisterSync.IsInjectedStaff(id))
                            dead.Add(kv.Key!);
                    }
                    foreach (var k in dead) dict.Remove(k);
                    if (dead.Count > 0)
                        Plugin.Logger.LogInfo($"[StaffRoster] {dead.Count} injected/synthetic record(s) removed from the finished-training notification list (announced on the owner's machine, not here).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_TrainingNotifications] purge: {ex.Message}"); }
            }
        }

        // ── Patch: BizManSettings.RenameLogoSpriteFolder — stale-target shield ──
        // RED ROC log 2026-07-13: renaming a business to a name it previously
        // held throws IOException in the native rename (Directory.Move onto an
        // existing folder from the earlier round-trip) — which aborts the REST
        // of SaveBusinessInformation for that click AND skips our sync postfix,
        // so the rename neither fully applies nor propagates.  Native already
        // deletes a stale TempSpriteFolder the same way — this extends that
        // exact pattern to a stale DESTINATION.  Vanilla-reproducible bug;
        // shield not MP-gated.
        [HarmonyPatch(typeof(BizManSettings), nameof(BizManSettings.RenameLogoSpriteFolder))]
        public static class Patch_RenameLogoSpriteFolder_StaleTargetShield
        {
            static void Prefix(string oldName, string newName)
            {
                try
                {
                    string src = LogoHelper.GetPlayerBusinessLogoPath(oldName);
                    string dst = LogoHelper.GetPlayerBusinessLogoPath(newName);
                    if (!System.IO.Directory.Exists(src)
                        || string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return;
                    if (System.IO.Directory.Exists(dst))
                    {
                        System.IO.Directory.Delete(dst, true);
                        Plugin.Logger.LogWarning($"[ModCompat] logo rename '{oldName}' → '{newName}': stale destination folder removed (rename round-trip) so the native move can't abort the settings save.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RenameLogoSpriteFolder] {ex.Message}"); }
            }
        }

        // ── Patch: CustomerEntriesHelper.RemoveSeasonalItems — stale-mod-item shield ──
        // Field report 2026-07-12 (offline player, no comment): a store's sale
        // list carried 'it-services:itemname_bladeserver' from a removed CONTENT
        // MOD; the seasonal filter's ItemsGetter.GetByName(x).season deref NREd,
        // and IndoorCustomerSpawner.Update aborted EVERY FRAME (3,467 in one
        // log) — the store silently never spawned customers.  Same class as the
        // 0.1.9 payroll-runaway fix (stale content-mod item aborts a native
        // loop).  Prefix strips names that no longer resolve BEFORE the native
        // filter runs: an unresolvable item cannot be sold, and this method's
        // whole job is removing unsellable items, so the repair is native-shaped
        // — and it cleans the CACHED list, so the spawner recovers for good.
        // NOT MP-gated: the report shows it biting in offline play.
        [HarmonyPatch(typeof(AI.Customers.CustomerEntries.CustomerEntriesHelper), "RemoveSeasonalItems")]
        public static class Patch_RemoveSeasonalItems_StaleModItemShield
        {
            private static readonly System.Collections.Generic.HashSet<string> _reported = new();
            static void Prefix(System.Collections.Generic.List<string> itemsForSale)
            {
                try
                {
                    if (itemsForSale == null) return;
                    itemsForSale.RemoveAll(x =>
                    {
                        bool dead;
                        try { dead = string.IsNullOrEmpty(x) || BigAmbitions.Items.ItemsGetter.GetByName(x) == null; }
                        catch { dead = true; }
                        if (dead && !string.IsNullOrEmpty(x) && _reported.Add(x))
                            Plugin.Logger.LogWarning($"[ModCompat] sale list references '{x}' but no such item exists (content mod removed?) — dropped so customer generation can run.");
                        return dead;
                    });
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_RemoveSeasonalItems_StaleModItemShield] {ex.Message}"); }
            }
        }

        // ── Patch: PortraitGenerator.GetCharacterPortraitPath(GameInstance) ──
        // MP self-portrait blank (Rivals page, host + client): the game's Save
        // regenerates the player portrait NEXT TO THE .HSG — in MP that's
        // _BAMP_MP\<session>\<stableId>\save portrait.jpg, because
        // PerformLocalSave passes an explicit characterFolder.  But the 1-arg
        // path getter (used by LoadPlayerPortrait — the Rivals self row at
        // RivalLeaderboard :86 and the topbar at Topbar :174/:181 — and by our
        // ReadLocalPortraitBase64 relay) builds on CurrentVersionFolderPath(),
        // the NATIVE version folder (EA 0.11\guid-<stableId>\), which never
        // receives a write in MP.  Read and write never meet, LoadPlayerPortrait
        // returns null, and the player's own picture is blank.  While an MP
        // session has a known portrait folder, resolve the 1-arg getter there
        // so every caller agrees on the folder the save flow actually writes —
        // including the topbar's regenerate-fallback WRITE, which then
        // self-heals a fresh session that hasn't saved yet.  The 2-arg overload
        // (Save's write, explicit folder) and the SaveGameStruct overload
        // (main-menu save list) are separate methods — untouched.  Gated on a
        // live MP session so single player and offline forks keep the native
        // path even if the folder pointer is stale.
        //
        // DISK-JUNK r3 (2026-09-05, review F-2026-09-05-CY MINOR-1): the file NAME this getter
        // returns is FIXED — "save portrait.jpg" — and is NOT derived from saveGame.SaveGameName.
        // A fresh character's SaveGameName is "New <name> Save Game", so the derived name dropped a
        // SECOND portrait into the store member folder beside the "save portrait.jpg" the mod's own
        // saves write (their .hsg base name is MPSaveCoordinator.SaveFileName = "save"; kept private
        // there, so the literal is repeated here rather than widening it), and the load window's
        // *portrait*.jpg glob then showed the stale first face.  The store member folder holds exactly
        // save.hsg / save.hsg.meta / save portrait.jpg: the first portrait IS that file, and every
        // later save overwrites it in place.  Both READ paths (PortraitGenerator.LoadPlayerPortrait
        // and GameStatePatcher.cs:430) go through this same 1-arg getter, so they resolve the same
        // fixed name as the write.
        [HarmonyPatch(typeof(Character.Customization.PortraitGenerator),
                      nameof(Character.Customization.PortraitGenerator.GetCharacterPortraitPath),
                      typeof(GameInstance))]
        public static class Patch_PortraitGenerator_GetCharacterPortraitPath_MpFolder
        {
            static bool Prefix(GameInstance saveGame, ref string __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                    string? folder = MPSaveCoordinator.PortraitFolder;
                    if (string.IsNullOrEmpty(folder) || saveGame == null) return true;
                    __result = System.IO.Path.Combine(folder, "save portrait.jpg");   // DISK-JUNK r3: the store folder holds exactly save.hsg / save.hsg.meta / save portrait.jpg — the first portrait IS that file and every later save overwrites it in place (review F-2026-09-05-CY MINOR-1)
                    return false;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[Patch_PortraitPath_MpFolder] {ex.Message}");
                    return true;
                }
            }
        }

        // ── Patch: ScheduleAutoFiller ctor — exclude mod-injected records ────
        // "New Text" field report (2026-07-09): the BizMan auto-fill builds its
        // employee list by assignedAddress, which INCLUDES our duty synthetics
        // (BAMP_DUTY_*, assignedAddress = the shop) and injected partner
        // records.  The fill then schedules them like real staff — and because
        // it runs on a background thread, our duty-off/roster-drop sweeps can
        // race it, leaving shifts for ids whose record is gone (permanently
        // orphaned; rendered as "New Text", unremovable).  The ctor is the one
        // funnel every fill path passes through (BizMan button, per-day fill,
        // EA010 compat fix) AFTER the null-employees case is resolved — filter
        // the list in place before the filler snapshots it.  Runs on the main
        // thread (the worker thread starts later in AutoFillSchedule).
        [HarmonyPatch(typeof(Buildings.Schedule.ScheduleAutoFiller), MethodType.Constructor,
                      new Type[] { typeof(System.Collections.Generic.List<Entities.EmployeeInstance>), typeof(BuildingRegistration), typeof(ScheduleDay) })]
        public static class Patch_ScheduleAutoFiller_ExcludeModRecords
        {
            static void Prefix(System.Collections.Generic.List<Entities.EmployeeInstance> __0, BuildingRegistration __1)
            {
                try
                {
                    if (__0 == null) return;
                    int n = __0.RemoveAll(e =>
                    {
                        string id = e?.id ?? "";
                        if (id.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix)) return true;
                        // Shared-shop slice 3: on a shop shared with me, the OWNER's copied staff are the right staff.
                        return MPRegisterSync.IsInjectedStaff(id) && !SharedShopStaff.AllowedInAutoFill(id, __1);
                    });
                    if (n > 0)
                        Plugin.Logger.LogInfo($"[ScheduleGuard] excluded {n} synthetic/injected record(s) from auto-fill for '{__1?.BusinessName}'.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Patch_ScheduleAutoFiller_ExcludeModRecords] {ex.Message}"); }
            }
        }

        // ── Patch: WorkShiftSlider.SetUp — orphan-shift colour guard ────────
        // SHIFT-SETUP-1: native SetUp ENDS with
        //   background.color = ScheduleHelper.GetEmployeeColor(_workShift.employeeId)
        // and GetEmployeeColor uses the dictionary INDEXER (ScheduleHelper.cs:142-145),
        // so a shift naming an employee id with no record throws KeyNotFoundException
        // in SetUp — which runs BEFORE the two guards below.  The cell then dies and
        // the day's REMAINING shifts are never drawn.  The mod deliberately leaves
        // real-id orphan shifts in place (MPSaveIntegrity: an id can be legitimately
        // absent for a while — a partner's staff not yet delivered), so the shift is
        // not ours to delete or rewrite.  The colour is the last statement in SetUp:
        // swallowing ONLY the missing-key case loses nothing but the tint.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.WorkShiftSlider), "SetUp")]
        public static class Patch_WorkShiftSlider_SetUp_OrphanColourGuard
        {
            private static readonly System.Collections.Generic.HashSet<string> _loggedIds = new();
            static Exception? Finalizer(Exception __exception, WorkShift __2)
            {
                if (__exception is not System.Collections.Generic.KeyNotFoundException) return __exception;
                try
                {
                    string id = __2?.employeeId ?? "";
                    Entities.EmployeeInstance? emp = null;
                    try { emp = Helpers.EmployeeHelper.GetEmployeeById(id); } catch { }
                    if (emp != null) return __exception;   // resolvable employee — some other missing key
                    if (_loggedIds.Add($"{__2?.itemInstanceId}|{id}"))
                        Plugin.Logger.LogWarning($"[ScheduleDiag] SetUp: shift names unknown employee '{id}' — colour skipped (SHIFT-SETUP-1).");
                    return null;
                }
                catch { return __exception; }
            }
        }

        // ── Patch: WorkShiftSlider.UpdateState — orphan-shift render guard ───
        // A WorkShift whose employeeId resolves to no record makes UpdateState
        // NRE at employeeById.characterData.name BEFORE the name label is set —
        // the TMP component keeps its prefab default, which is literally
        // "New Text".  Swallow ONLY that case: label the slider "(missing
        // staff)", hide the warning icon, and log (throttled per id).  Any
        // exception with a RESOLVABLE employee is rethrown — not our case.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.WorkShiftSlider), "UpdateState")]
        public static class Patch_WorkShiftSlider_UpdateState_OrphanGuard
        {
            private static readonly System.Collections.Generic.HashSet<string> _loggedIds = new();
            static Exception? Finalizer(Exception __exception,
                                        UI.Smartphone.Apps.BizMan.Schedule.WorkShiftSlider __instance,
                                        WorkShift __0)
            {
                if (__exception == null) return null;
                try
                {
                    string id = __0?.employeeId ?? "";
                    Entities.EmployeeInstance? emp = null;
                    try { emp = Helpers.EmployeeHelper.GetEmployeeById(id); } catch { }
                    if (emp != null) return __exception;   // some other failure — surface it
                    var t = typeof(UI.Smartphone.Apps.BizMan.Schedule.WorkShiftSlider);
                    if (HarmonyLib.AccessTools.Field(t, "employeeNameLabel")?.GetValue(__instance) is TMPro.TMP_Text label)
                        label.text = "(missing staff)";
                    if (HarmonyLib.AccessTools.Field(t, "warningObj")?.GetValue(__instance) is UnityEngine.GameObject warn)
                        warn.SetActive(false);
                    if (_loggedIds.Add(id))
                        Plugin.Logger.LogWarning($"[ScheduleDiag] slider rendered orphan shift (emp='{id}') — labeled '(missing staff)'.");
                    return null;
                }
                catch { return __exception; }
            }
        }

        // ── Patch: WorkShiftSlider.UpdateWarning — null-employee guard ───────
        // The drag/edit path (OnWorkShiftChanged) calls UpdateWarning with the
        // GetEmployeeById result unchecked; for an orphan shift that is null
        // and the demands loop NREs.  Nothing to warn about for a nonexistent
        // employee — hide the icon and skip.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.WorkShiftSlider), "UpdateWarning")]
        public static class Patch_WorkShiftSlider_UpdateWarning_NullGuard
        {
            static bool Prefix(UI.Smartphone.Apps.BizMan.Schedule.WorkShiftSlider __instance, Entities.EmployeeInstance __0)
            {
                if (__0 != null) return true;
                try
                {
                    if (HarmonyLib.AccessTools.Field(typeof(UI.Smartphone.Apps.BizMan.Schedule.WorkShiftSlider), "warningObj")
                            ?.GetValue(__instance) is UnityEngine.GameObject warn)
                        warn.SetActive(false);
                }
                catch { }
                return false;
            }
        }

        // ── Patch: JobDemand.Fulfilled(string, List<ScheduleDay>) ─ orphan guard ─
        // INFOBOX-GUARD-1: the STRING overload (decompile
        // Entities.Employee.JobDemands/JobDemand.cs:58-62) resolves the id with
        // EmployeeHelper.GetEmployeeById and hands the result to the abstract
        // EmployeeInstance overload UNCHECKED.  For a shift naming an id this save
        // does not hold, HoursWorkingPerWeek.Fulfilled derefs null
        // (instance.assignedWeeklyHours, Requirements/HoursWorkingPerWeek.cs:24-26)
        // and ScheduleEmployeeInfoBox.SetUpDemand throws on EVERY redraw — the
        // employee info box stays blank (error census B4: 190 throws in one bundle).
        // The ONLY caller of the string overload is that SetUpDemand
        // (UI.Smartphone.Apps.BizMan.Schedule/ScheduleEmployeeInfoBox.cs:34, reached
        // from ScheduleEmployeeCellView.cs:188); it uses the bool for
        // notFulfilledBackground.SetActive(!flag) and nothing else, so `false`
        // paints the "not fulfilled" backing and writes nothing anywhere.
        // JobDemand is abstract but THIS overload is concrete, so the patch is
        // pinned by argument types and can only bind to it.
        [HarmonyPatch(typeof(Entities.Employee.JobDemands.JobDemand), "Fulfilled",
                      new Type[] { typeof(string), typeof(List<ScheduleDay>) })]
        public static class Patch_JobDemand_Fulfilled_OrphanGuard
        {
            private static readonly System.Collections.Generic.HashSet<string> _loggedIds = new();
            static bool Prefix(string __0, ref bool __result)
            {
                try
                {
                    Entities.EmployeeInstance? emp = null;
                    try { emp = Helpers.EmployeeHelper.GetEmployeeById(__0 ?? ""); } catch { }
                    if (emp != null) return true;   // resolvable — native runs untouched
                    if (_loggedIds.Count < 40 && _loggedIds.Add(__0 ?? ""))
                        Plugin.Logger.LogWarning($"[ScheduleDiag] JobDemand.Fulfilled: shift names unknown employee '{__0}' — demand reported unfulfilled, nothing changed (INFOBOX-GUARD-1).");
                    __result = false;
                    return false;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[Patch_JobDemand_Fulfilled_OrphanGuard] {ex.Message}");
                    return true;
                }
            }
        }

        // ── Patch: ScheduleHelper.UpdateEmployeeAfterWorkShiftChange(string) ──
        // The removal path (right-click → RemoveWorkShift) indexes
        // EmployeesById[employeeId] AFTER removing the shift but BEFORE the two
        // UI-refresh calls; for an orphan id the indexer throws and the slider
        // never leaves the screen — the field report's "unable to remove".
        // Skipping the per-employee UI update for an id with no record is a
        // semantic no-op; the refresh calls then complete normally.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper), "UpdateEmployeeAfterWorkShiftChange",
                      new Type[] { typeof(string) })]
        public static class Patch_ScheduleHelper_UpdateEmployee_OrphanGuard
        {
            static bool Prefix(string __0)
            {
                try
                {
                    var dict = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.EmployeesById;
                    return dict != null && dict.ContainsKey(__0 ?? "");
                }
                catch { return true; }
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
                        // Round-256 (field 20260809-132321): this same suppression also fires
                        // for the routine startup default-load call, so no WARN here — but when
                        // it swallows the natural NEW-GAME call, city generation proceeds
                        // RIVAL-LESS (a save written from that state has gi.rivalStates EMPTY
                        // forever: rivals=0 world, rival-refs×1093 dangling). The snapshot
                        // handler repairs that ordering when the ids arrive: late
                        // GenerateRivals + cache refill, discriminated there by regs>0.
                        Plugin.Logger.LogInfo("[Wave6] GenerateRivals suppressed on client (host ids not yet arrived; round-256: a new-game generation in this state builds rival-less until the snapshot lands).");
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

                // Round-257: after an injected client run the queue must be EMPTY —
                // every seeded id dequeued into its slot. Leftovers mean the game's
                // mint count no longer matches what the host shipped (e.g. a game
                // update changed the wholesale/import block sizes, or specials
                // started minting): name them so the drift diagnoses itself.
                if (MPClient.IsConnected && GameStatePatcher.PendingRivalIdQueue.Count > 0)
                {
                    try
                    {
                        var left = new System.Collections.Generic.List<string>();
                        foreach (var id in GameStatePatcher.PendingRivalIdQueue) { left.Add(id); if (left.Count >= 6) break; }
                        Plugin.Logger.LogWarning($"[Wave6] UUID queue has {GameStatePatcher.PendingRivalIdQueue.Count} LEFTOVER id(s) after GenerateRivals — mint count vs host id count drifted (round-257 alignment broken by a game change?): {string.Join(", ", left)}");
                    }
                    catch { }
                }

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
                    if (!MPClient.IsClientInWorld) return true;
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
                if (!MPClient.IsClientInWorld) return true;
                Plugin.Logger.LogInfo("[Suppress] RealEstateHelper.RunDaily skipped on client.");
                return false;
            }
        }

        // NOTE (2026-08-29): 1.0's new RestockCurrentBusinessIfNeeded is handled by the MERGER
        // AUTHORITY VEIL above, not by a client-side skip. See the Steps entry for the reasoning —
        // the veil already expresses "run on the real owner's machine only", which is exactly what
        // this pass needs, and a client-side skip got it wrong in both directions.

        // ── REMOVED 2026-08-29: the three "new 1.0 hourly tick" client suppressions ───────────
        // Added, deployed and field-run 2026-08-29, then REVERTED the same day after an Opus review
        // plus a confirming grep. The reasoning that put them here was wrong, so it is recorded here
        // rather than deleted — the next person to see these three ticks will have the same idea.
        //
        // THE CLAIM WAS: FoodDeliveryHelper.RunHourly, PricingManagerHelper.RunHourly and
        // FoodDeliveryJobHelper.RunHourly mutate HOST-OWNED save state, so a client running them
        // would double-charge, double-deliver, and roll its own world into the shared save.
        //
        // WHAT IS ACTUALLY TRUE: all three operate on PER-MACHINE save state this mod never syncs.
        //   FoodDeliveryHelper    → SaveGameManager.Current.FoodDeliveryContracts  (0 refs in src/)
        //   PricingManagerHelper  → SaveGameManager.Current.pricingManagerPlans    (0 refs in src/)
        //   FoodDeliveryJobHelper → SaveGameManager.Current.foodDeliveryOffers      (0 refs in src/)
        // Saves are PER-PLAYER (MPSaveCoordinator.cs:29 — "<stableId>/save.hsg per player"), so the
        // host’s lists CANNOT contain a client’s contract, plan or offer. Suppressing prevented no
        // double-run whatsoever; it stranded the CLIENT’s own feature:
        //   • a client’s food delivery never arrives and is never charged — and because
        //     FoodDeliveryDialog.cs:44 gates on GetContracts().Count > 0, the stuck contract then
        //     BLOCKS every later order that client tries to place;
        //   • the client’s delivery gig board decays to empty and never refills (offers expire in
        //     GetOffer, refill only happens in the suppressed RunHourly);
        //   • a client’s pricing-manager employee silently does nothing — prices never reapply.
        //
        // THE TEST THAT SHOULD HAVE BEEN APPLIED, AND NOW IS: before suppressing a tick on clients,
        // NAME THE SYNC CHANNEL THAT CARRIES ITS RESULT TO THEM. Every other suppression in this
        // file has one. These three had none — and "no sync channel" is the signature of per-player
        // state, not of a host-authoritative pass. Structurally identical ticks are deliberately
        // NOT suppressed for exactly this reason: RecruitmentHelper.RunHourly (RNG candidates into
        // saved state), ContactsHelper.RunHourly, GamePromptHelper.RunHourly.
        //
        // THE REAL HAZARD IS THE OPPOSITE ONE, and it is handled elsewhere: a HOST whose pricing
        // plan sees a merger-flipped partner shop as its own (PricingManagerHelper.IsManageableBusiness
        // gates on registration.RentedByPlayer — precisely the flag the merger flip falsifies) and
        // rewrites that shop’s retailPrices. That is an AUTHORITY problem, not a client problem, so
        // PricingManagerHelper.RunHourly is now an entry in Patch_MergerAuthorityVeil.Steps above.
        // The other two move no partner-owned state and need nothing.
        //
        // All three remain in Patch_GameTickChain_ContainThrows.Steps — crash containment is
        // orthogonal to authority and is still wanted.

        // -- Pricing manager: never manage ANOTHER PLAYER'S shop (2026-08-29) -----------------
        // The merger veil entry above covers the MERGER case. It is not enough, because the merger
        // was the wrong frame: a pricing plan does not manage buildings anyone picked. Read it:
        //   GetSupervisedStores()  (PricingManagerPlan.cs:384-400) sweeps ALL of
        //     SaveGameManager.Current.BuildingRegistrations and keeps every reg where
        //   IsSupervisedStore -> PricingManagerHelper.IsManageableBusiness(reg)
        //     (PricingManagerHelper.cs:54-61 -- RentedByPlayer && retailPrices != null
        //      && HasTag(generatesrevenue))   AND   reg.Neighborhood == supervisedNeighborhood.
        // A plan supervises a whole NEIGHBOURHOOD and takes everything in it that looks like mine.
        //
        // And on EVERY machine another player's business sits in the local list with
        // RentedByPlayer == true -- that is exactly what GameStatePatcher.IsForeignPlayerBusiness
        // keys on (:2306-2316), and why HideFromOwnAssetLists has to exist at all for native
        // "my assets" sweeps. So WITHOUT a merger -- ordinary multiplayer, with or without any
        // permission grant -- a partner's shop in my neighbourhood is RentedByPlayer=true and is
        // NOT in MergerFlip._flipped, so the veil never touches it and my pricing manager rewrites
        // their prices. The hazard needs no grant; it exists with none.
        //
        // This is the same class of native own-assets sweep the mod already filters, so it gets the
        // same mechanism: GameStatePatcher.HideFromOwnAssetLists, which is the MERGER-AWARE variant
        // (GameStatePatcher.cs:2318-2329, "Use for DISPLAY/PICKER filtering only").
        //
        // WHY THE MERGER-AWARE ONE AND NOT IsForeignPlayerBusiness (which this used at first):
        // IsManageableBusiness has THREE callers in 1.0 and only ONE of them is veiled -
        //   PricingManagerPlan.cs:404   IsSupervisedStore  -> reached from RunHourly  (VEILED)
        //   PricingManagerPlanUI.cs:81  GetSelectableNeighborhoods                    (NOT veiled)
        //   PricingManagersPlanList.cs:277 GetOwnedBusinessCount                      (NOT veiled)
        // The two UI callers run with the merger flip ACTIVE, where RentedByPlayer is deliberately
        // true so partners can co-manage. IsForeignPlayerBusiness ignores the flip and would have
        // stripped exactly the shops a merger exists to share - a feature-breaking false positive in
        // the UI. HideFromOwnAssetLists returns false for a flipped shop, so the merger keeps working
        // and a plain foreign shop is still excluded.
        //
        // Inside the veiled RunHourly path this postfix is a genuine no-op: the veil has already set
        // RentedByPlayer false, so IsManageableBusiness short-circuits (PricingManagerHelper.cs:56)
        // and the `if (!__result) return;` below fires first. The two do not fight.
        [HarmonyPatch]
        public static class Patch_PricingManager_IsManageableBusiness_NotAnotherPlayers
        {
            static System.Reflection.MethodBase? TargetMethod() =>
                VehicleManager.FindGameType("Buildings.Office.Headquarters.PricingManagerHelper")
                    ?.GetMethod("IsManageableBusiness",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);

            static int _logs;

            static void Postfix(BuildingRegistration registration, ref bool __result)
            {
                if (!__result) return;                                   // already excluded
                // IsClientInWorld, NOT IsConnected (2026-08-29). MPClient.cs:93-99 states the rule: "Suppressions of native world-mutating passes, SHIELDS OVER MOD-CREATED STATE, and replica protections must gate on THIS instead of IsConnected." IsConnected goes false the instant a link drops, while the MP world - and every ghost in it - is still loaded.
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return; // single-player: native
                try
                {
                    if (!GameStatePatcher.HideFromOwnAssetLists(registration)) return;
                    __result = false;
                    if (_logs++ < 8)
                        Plugin.Logger.LogInfo(
                            $"[PricingMgr] excluded '{registration.BusinessName}' @ "
                          + $"{GameStateReader.AddressKey(registration)} -- it belongs to player "
                          + $"'{registration.businessOwnerRivalId}', not to this machine. A pricing plan "
                          + "sweeps its whole neighbourhood, so without this it would reprice their shop.");
                }
                catch { /* a diagnostic must never change the answer it is reporting on */ }
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
                if (!MPClient.IsClientInWorld) return true;
                Plugin.Logger.LogInfo("[Suppress] CompetitionHelper.RunDaily skipped on client (host-authoritative AI economy).");
                return false;
            }
        }

        // Payroll-runaway guard (2026-07-07, field bug report): GameManager.NewDay runs
        // EmployeeHelper.PayDailyWages (:636), CompetitionHelper.RunDaily (:640), then
        // EmployeeHelper.RunDaily (:651) — the call that zeroes workedHoursToday daily and resets
        // workedDays/workedHoursThisWeek on Mondays. An exception in the AI-economy step aborts
        // NewDay BEFORE the reset (and before TaxHelper/ProductMarketHelper/daily summary), so
        // every midnight charges wages on an ever-growing hours total and never resets it —
        // host-only, because clients skip CompetitionHelper.RunDaily (patch above). Confirmed
        // trigger: stale item names in save-persisted productMarketEntries (leftovers of a removed
        // content mod) — ItemsGetter.GetByName returns null for them, and TryCreateCompetitorBusiness
        // (CompetitionHelper.cs:292) feeds that null through TryGetBusinessForItem into
        // GetSuitableBuildingTypeForItem, which derefs item.itemName on its first line (:496).
        //
        // Guard at the source: skip a market entry whose item no longer resolves — the day's
        // competitor-business loop (StartNewBusiness :280) simply tries the next demanded item,
        // exactly as if this one weren't demanded. Guarding deeper (e.g. defaulting
        // GetSuitableBuildingTypeForItem) would only move the NRE — IsPrimaryProduct(:507) and
        // FindSuitableBusinessDefaultForItem(:334) deref the same item.
        [HarmonyPatch(typeof(Helpers.CompetitionHelper), "TryCreateCompetitorBusiness")]
        public static class Patch_Competition_TryCreateBusiness_StaleItemGuard
        {
            private static readonly HashSet<string> _warned = new HashSet<string>();
            static bool Prefix(string itemName, ref bool __result)
            {
                try { if (BigAmbitions.Items.ItemsGetter.GetByName(itemName, suppressError: true) != null) return true; }
                catch { return true; }   // guard must never become its own crash — let native run
                if (_warned.Add(itemName ?? ""))
                    Plugin.Logger.LogWarning($"[Guard] CompetitionHelper: market entry '{itemName}' resolves to no item " +
                                             "(stale entry, e.g. from a removed content mod) — skipped as competitor-business candidate.");
                __result = false;
                return false;
            }
        }

        // Merger slice 3 — DEED GUARD, retired by PHASE 5 / D27 (user 2026-09-12): terminating the rental
        // contract of a FLIPPED (partner-owned) business used to be blocked, because the native path sells
        // the interior and refunds the deposit on the local REPLICA while the real owner keeps the building
        // (Class 10 divergence). It now ROUTES instead: the member's confirm sends a `mergerterminate` op on
        // the shared-work-edit carrier to the machine that RUNS the building, and the sale and the refund
        // happen THERE, on the owner's world, with the shared wallet absorbing the credit (D6). Nothing is
        // written on the replica here. Inert without a merger (FlippedCount == 0 short-circuit).
        [HarmonyPatch(typeof(BizManPresentation), "OnTerminateContractConfirm")]
        public static class Patch_BizMan_TerminateContract_MergerDeedGuard
        {
            static bool Prefix(BizManPresentation __instance)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0) return true;
                    var bm  = AccessTools.Field(typeof(BizManPresentation), "bizManBusiness")?.GetValue(__instance) as BizManBusiness;
                    var reg = bm?.buildingRegistration;
                    if (reg == null) return true;
                    string key = GameStateReader.AddressKey(reg);
                    if (!MergerFlip.IsFlipped(key)) return true;   // genuinely mine — native flow
                    // D27: a STAND-IN MAY run it. On the machine simulating an absent owner's businesses the
                    // address is still flipped, but the lifted copy IS the live state and this machine is the
                    // route target — the native path here is the owner's own path, so it runs unchanged.
                    bool standIn = false; try { standIn = MergerAbsence.SimulatesHere(key); } catch { }
                    if (standIn) return true;
                    if (!MergerSync.IAmMember)
                    {
                        Plugin.Logger.LogInfo($"[Merger] terminate-rental refused for '{key}': this machine is not a company member.");
                        return false;
                    }
                    if (!SharedShopWorkTabs.RunnerReachable(key))
                    {
                        Plugin.Logger.LogWarning($"[Merger] terminate-rental refused for '{key}': nobody is running that building right now.");
                        return false;
                    }
                    SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                    { PlayerId = MPConfig.PlayerId, AddressKey = key, Op = "mergerterminate" });
                    Plugin.Logger.LogInfo($"[Merger] terminate-rental routed for '{key}' — the machine that runs it sells the interior and returns the deposit there.");
                    return false;   // the local screen closes with no native write; the owner's pushes carry the result back
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] deed guard: {ex.Message}"); return true; }
            }
        }

        // Merger slice 3 — settings-save guard, narrowed by PHASE 5 / D28 (user 2026-09-12).
        // BizManSettings.SaveBusinessInformation bundles rename + BUSINESS TYPE change (with employee
        // unassignment) as inline UI logic. The RENAME and the LOGO are routed for a company member by
        // SharedShopWorkTabs.Patch_BizManSettings_Save_Routed (ops `rename`/`logo`, the helper route reused),
        // so where that body covers the address this guard steps aside — one owner for the method, instead of
        // two prefixes racing to return false on it. The business TYPE stays refused: it has no routable
        // data-level native method, the routed body drops it on the floor exactly as it does for a permission
        // helper, and the dropdown itself is greyed (D30). Inert without a merger.
        [HarmonyPatch(typeof(BizManSettings), nameof(BizManSettings.SaveBusinessInformation))]
        public static class Patch_BizManSettings_Save_MergerGuard
        {
            static bool Prefix(BizManSettings __instance)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0) return true;
                    var bm  = AccessTools.Field(typeof(BizManSettings), "_bizManBusiness")?.GetValue(__instance) as BizManBusiness;
                    var reg = bm?.buildingRegistration;
                    if (reg == null) return true;
                    if (!MergerFlip.IsFlipped(GameStateReader.AddressKey(reg))) return true;
                    if (SharedShopWorkTabs.RoutesIdentityFor(reg, out _, out _)) return true;   // D28: the routed body owns this save
                    PassengerHud.Toast("Only the deed holder can change business settings (for now).");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] settings guard: {ex.Message}"); return true; }
            }
        }

        // Borrowed-vehicle building transitions (2026-07-07, run-10 field narrative): the native entry
        // coroutine moves an accompanying vehicle via ITS OWN bookkeeping (selectedVehicle /
        // VehicleInstances) — a borrowed PROXY is deliberately absent from those records (tax/ticket
        // dodge), so the transition breaks possession HALFWAY: the pusher keeps a distorted pushing
        // pose with no cart, the cart strands outside the door, and the follow + physics release then
        // lose the owner's real cart entirely (flung/under-world). AUTO-PARK instead: the native
        // ExitVehicle runs at the door (clean pose, clean release to the owner, cart rests where it
        // was left), then the entry proceeds without it. Borrowed carts simply wait outside — the
        // honest limitation until proxy transitions are fully supported. Inert without proxies.
        // (Auto-park REVERTED same day — user, correctly: bringing a cart indoors is a CORE mechanic.
        //  Run-10's own probes then proved the STATE survives the transition fine — possessed +
        //  parented for minutes inside; the failure is the cart's RENDERING after the interior
        //  loads. Fix targets the visual layer instead; see MergerFlip/VehicleManager cart work.)

        // Merger slice 3 — routed open/close toggle: the BizMan toggle calls BuildingRegistration.
        // TemporarilyClose, which on a FLIPPED replica would only mutate the local copy (the owner's
        // synced truth then overwrites it — the toggle "doesn't stick"). Route it to the true owner,
        // who runs the native method (licensing/customer-entry/todo side effects fire on the
        // authoritative machine) and whose sync republishes the state to everyone. Inert without a
        // merger (IsFlipped false for everything).
        [HarmonyPatch(typeof(BuildingRegistration), nameof(BuildingRegistration.TemporarilyClose))]
        public static class Patch_TemporarilyClose_MergerRoute
        {
            static bool Prefix(BuildingRegistration __instance, bool closed)
            {
                try
                {
                    if (__instance == null) return true;
                    string key = GameStateReader.AddressKey(__instance);
                    // ROUND-124: route for a GRANTED HELPER too, not only a merger-flipped replica.
                    // Field report: "the host's open/close toggle transmitted instantly, the client's did not."
                    // Cause: this interception only fired on MergerFlip.IsFlipped, so an ordinary helper
                    // toggling the owner's shop ran the NATIVE method and mutated their own replica — the owner
                    // never heard about it, and the round-122 immediate push skips it because TrulyMine is false
                    // on that machine. Everything needed already existed: RouteTemporarilyClose sends it, the
                    // host grant-gates it in HostRouteBusinessEdit and applies or relays to the owner, and the
                    // owner's own TemporarilyClose then runs natively (licensing, customer entries, todo) and
                    // republishes. So this just widens WHO gets routed.
                    bool routeAsHelper = false;
                    try { routeAsHelper = !MergerFlip.TrulyMine(__instance) && GrantSync.IsHelperBusiness(key); }
                    catch { }
                    if (!MergerFlip.IsFlipped(key) && !routeAsHelper) return true;   // genuinely mine → native
                    BusinessSync.RouteTemporarilyClose(key, closed);
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] toggle route: {ex.Message}"); return true; }
            }
        }

        // Merger slice 3 — AUTHORITY VEIL (merger map §13-A): the ownership flip makes partner
        // businesses read RentedByPlayer=true for MENUS, but these audited native passes SIMULATE
        // off that same flag (charging rent/taxes/marketing/licensing, business + summary/net-worth
        // accumulation, customer-entry generation, dirt sim, todo/job-board state). Around each
        // pass, every flipped reg reverts to native truth (VeilPush) and re-flips after (VeilPop in
        // a Finalizer — guaranteed re-flip even when the pass throws; the pass's own throw is then
        // contained by the tick wall below). Nesting-counted: veiled passes call each other
        // (PayLicensingFeesForAllBusinesses → PayLicensingFees). Result: every cost and credit
        // fires exactly once, on the real owner's machine — un-merged behavior, merged presentation.
        // INERT without a merger: push/pop over an empty flip table touches nothing.
        [HarmonyPatch]
        public static class Patch_MergerAuthorityVeil
        {
            // Every name below is DECOMPILE-VERIFIED at its declaration (2026-07-07 field run 3:
            // 9 of the original entries were agent-FABRICATED names — PayRentToNpc etc. — that
            // resolved to nothing on any build; the real code lives in the RunDaily/RunHourly
            // bodies. Third agent-fabrication catch this campaign: never trust an audit-table
            // name that wasn't read at its line.)
            private static readonly (string type, string method)[] Steps =
            {
                ("Helpers.MarketingHelper",         "RunDaily"),                // charges daily marketing spend
                ("Helpers.BusinessHelper",          "RunDaily"),                // rent (:43), licensing (:76), daily orders (:107; the NetWorth accumulator was removed by the 1.0 update 2026-09-01)
                ("Helpers.BusinessHelper",          "RunHourly"),               // job board (:308), security (:330), todo gen (:499)
                ("Buildings.Retail.Businesses.CinemaTheater.LicensingFeesHelper", "PayLicensingFees"),
                ("Buildings.Retail.Businesses.CinemaTheater.LicensingFeesHelper", "ShowUnpaidFeesNotifications"),   // (:106)
                // Tax CHARGE accrues per-transaction (TrackTransaction — per-machine-correct
                // unveiled); the flag sites are the IRS REPOSSESSION sweep (must never seize a
                // partner's flipped buildings' items) and the tax-report display builder.
                ("Helpers.TaxHelper",               "ForcePayCurrentTaxes"),
                ("Helpers.TaxHelper",               "GenerateTaxes"),
                ("Helpers.FinancialSummaryHelper",  "CreateFinancialSummary"),  // (:24) drives NetWorth accumulation
                ("BusinessSimulatorHelper",         "RunHourly"),               // away-from-shop revenue engine
                // Merger phase 0 (2026-09-10, F-2026-09-10-B): two more RentedByPlayer-gated passes. FillProvidersDictionary
                // counts a player shop as a provider unconditionally (an empty-shelf flipped partner shop would inflate the
                // counts behind UpdateMarketDemands); RefreshRivals clears businessOwnerRivalId on RentedByPlayer regs and
                // rebuilds the rival tables (a flipped shop would drop out of rival bookkeeping). The CompetitionHelper cluster
                // and CustomerEntriesHelper.UpdateCachedProductsForAiBusiness are deliberately NOT here: the flip is what
                // shields a partner's shop from the AI shutting it down, re-tenanting it or rewriting its prices/products.
                ("Helpers.ProductMarketHelper",     "FillProvidersDictionary"),
                ("BigAmbitions.Rivals.RivalsHelper", "RefreshRivals"),
                // NEW IN GAME 1.0 (2026-08-29): BuildingManager.RunCurrentBuildingHourly ->
                // BusinessHelper.RestockCurrentBusinessIfNeeded restocks the shop the local player is
                // STANDING IN — the one building BusinessSimulatorHelper.RunHourly deliberately skips
                // (BusinessSimulatorHelper.cs:32). It gates on registration.RentedByPlayer, which is
                // precisely the flag the flip falsifies, so it belongs here: veiled, it fires on the
                // real owner's machine and nowhere else. (A client-side skip was tried first and was
                // WRONG in both directions — it left the HOST free to restock a partner's shop, and it
                // stopped a client's OWN shop restocking while they stood in it, which no other pass
                // covers. The veil gets both right with no new gate.)
                ("Helpers.BusinessHelper",          "RestockCurrentBusinessIfNeeded"),
                // NEW IN GAME 1.0 (added to the veil 2026-08-29, replacing a wrong client skip):
                // the pricing-manager employee reapplies managed retail prices hourly and gates on
                // registration.RentedByPlayer (PricingManagerHelper.IsManageableBusiness) — the exact
                // flag the merger flip falsifies. Unveiled, a HOST’s plan would rewrite a partner’s
                // flipped shop’s prices. Veiled, it sees native ownership and touches only its own.
                // NOTE: this does NOT stop a client running it for their own shops — the plan list
                // (SaveGameManager.Current.pricingManagerPlans) is per-player and unsynced, so each
                // machine must run its own. Authority, not suppression.
                ("Buildings.Office.Headquarters.PricingManagerHelper", "RunHourly"),
                ("BusinessSimulatorHelper",         "OnTimeMachineEnd"),
                ("Entities.ImportPartnership",      "DoDeliveries"),            // (:175/:249) import purchases + deliveries
                ("Buildings.BuildingTypes.Shared.Dirtiness.BuildingCleanlinessHelper", "RunHourly"),   // dirt sim (:34)
                ("AI.Customers.CustomerEntries.CustomerEntriesHelper", "UpdateCustomerEntriesForPlayerBusiness"),   // entry GENERATION stays owner-only (namespace is AI.*, not Helpers)
                ("BigAmbitions.Rivals.RivalsHelper","RunDaily"),                // rival sim sees partner shops as it does today
                ("BigAmbitions.Rivals.RivalsHelper","RunHourly"),
            };

            static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                // ALL overloads by name — AccessTools.Method(name) returns null on an ambiguous
                // (overloaded) name, which left 9 passes UNVEILED in the 2026-07-07 field run
                // (the loud patch-time error caught it). Veiling every overload is correct: the
                // un-flip must hold for whichever variant the game calls.
                foreach (var (type, method) in Steps)
                    foreach (var m in Patch_GameTickChain_ContainThrows.ResolveAllByName(type, method, "Merger] authority veil", "UNVEILED (double-run risk in a merger)"))
                        yield return m;
            }

            static void Prefix() => MergerFlip.VeilPush();
            static Exception Finalizer(Exception __exception) { MergerFlip.VeilPop(); return __exception; }
        }

        // ─────────── H-SALEHOLE-1 (H2): THE SERVE SCOPE ───────────
        /// <summary>The LIVE SERVE CHAIN, un-flipped for the duration of each native serve step (2026-09-19).
        /// Shaped like <see cref="Patch_MergerAuthorityVeil"/>: a name table, TargetMethods, a Prefix that
        /// pushes and a Finalizer that always pops. What it buys: on a MEMBER standing in a merger-FLIPPED
        /// partner shop, every owner-GATED native step below sees the shop as someone else's and skips itself
        /// (the two ungated order records and the player-as-customer paths are outside it - MergerFlip header)
        /// — no bag taken from the replica register, no shelf drained, no local pricing, no order recorded —
        /// which is precisely the state the PERMISSION HELPER is already in, so Patch_Order_Pay_HelperForward
        /// forwards each paid NPC order and the owner adopts it once. On the TRUE owner (and on a stand-in for
        /// an absent owner) the scope is a no-op, so nothing changes there.
        /// EVERY NAME BELOW IS DECOMPILE-VERIFIED at its owner-gate line; a name that resolves to nothing logs
        /// LOUD at patch time (the Class-3 rule).
        /// COROUTINES are wrapped at the compiler-generated enumerator's MoveNext, so the scope is pushed and
        /// popped inside EACH SLICE and can never stay up across a frame.</summary>
        [HarmonyPatch]
        public static class Patch_MergerServeScope
        {
            // SYNCHRONOUS serve steps — patched directly.
            private static readonly (string type, string method)[] Direct =
            {
                ("FullServiceEmployee",    "CheckConditions"),          // :179 prices + judges the order, owner-gated
                ("BarmanEmployee",         "IsOrderEntryPurchasable"),  // :225
                ("Customer",               "CompleteOrder"),            // :393-395 records the order on THIS machine
                ("Customer",               "ReturnItemsToShelf"),       // :318 puts an abandoned basket back on the replica's shelves
                ("Customer",               "GrabItem"),                 // :511 the NPC shelf/producer drain
                ("ProcessSelfServiceOrder","OnStart"),                  // :21/:64 the self-service NPC drain + pricing
            };

            // COROUTINES — patched at MoveNext (AccessTools.EnumeratorMoveNext).
            private static readonly (string type, string method)[] Coroutines =
            {
                ("FullServiceEmployee",        "ServeCustomer"),        // :66 :95 :111 :135 :154 (bag, stock, pricing, the Add)
                ("FullServiceEmployee",        "GrabOrderEntryItems"),  // :243 shelf drain + cost basis
                ("SelfServiceEmployee",        "ServeCustomer"),        // :64 bag subtract
                ("BarmanEmployee",             "ServeCustomer"),        // :78 :105 :106
                ("BarmanEmployee",             "GrabOrderEntryItems"),  // :203 :204
                ("HairdresserStylistEmployee", "ServeCustomer"),        // :73 :100 :136 :140
                ("CoatCheckEmployee",          "ServeCustomer"),        // :66
                ("TicketBoothEmployee",        "ServeCustomer"),        // :23 (Order.Pay for the NPC ticket sale)
                ("Controllers.TicketKioskController", "ServeCustomer"), // :144 the kiosk checkout
            };

            static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                const string tag = "Merger] serve scope";
                const string why = "UNSCOPED (a member would serve a partner's shop against its own replica)";
                foreach (var (type, method) in Direct)
                    foreach (var m in Patch_GameTickChain_ContainThrows.ResolveAllByName(type, method, tag, why))
                        yield return m;
                foreach (var (type, method) in Coroutines)
                    foreach (var m in Patch_GameTickChain_ContainThrows.ResolveAllByName(type, method, tag, why))
                    {
                        System.Reflection.MethodBase? mv = null;
                        try { mv = AccessTools.EnumeratorMoveNext(m); } catch { }
                        if (mv == null) Plugin.Logger.LogError($"[{tag}: {type}.{method} MoveNext did not resolve — that step is {why}.");
                        else yield return mv;
                    }
            }

            static void Prefix()
            {
                try { MergerFlip.ServeScopePush(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] Patch_MergerServeScope push: {ex.Message}"); }
            }

            static Exception Finalizer(Exception __exception)
            {
                try { MergerFlip.ServeScopePop(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] Patch_MergerServeScope pop: {ex.Message}"); }
                return __exception;
            }
        }

        // ─────────── MERGER PHASE 2 STOP-GAPS (2026-09-11) ───────────
        // Three NATIVE writes on a merger-FLIPPED partner building spend or move REAL company value
        // against a LOCAL REPLICA: the goods land (or vanish) on the replica, the owner's next push
        // overwrites them, and the money movement is real for everyone (the shared wallet mirrors
        // company cash). Each is REFUSED at its point of commitment until it has an owner route.
        // SILENT to the player (no new on-screen text) + one INFO line saying why. INERT without a
        // merger: the flip table is empty, so every gate returns immediately. S1 (warehouse Sell All)
        // lives in SharedShopWorkTabs.cs next to the shared-session block it widens.

        /// <summary>True when this address is a PARTNER building the merger flipped to look rented on
        /// this machine. Reads the flip table directly (the game's own state, no timers); allocation-free
        /// while un-merged.</summary>
        private static bool FlippedAddr(Address address)
        {
            if (MergerFlip.FlippedCount == 0) return false;
            try { return MergerFlip.IsFlipped(GameStateReader.AddressKey(address)); } catch { return false; }
        }

        /// <summary>STOP-GAP S2 — DELIVERY CONTRACT / WHOLESALE ORDER CREATION.
        /// WholesaleStoreManagerDialog.cs:65 `private DialogEntry OnDeliveryContractSettingsSet()` is the
        /// game's ONLY point where a DeliveryContract enters SaveGameManager.Current.DeliveryContracts
        /// (:97). On a member's machine the business picker offers the partner's FLIPPED shops, and the
        /// pass that fills the contract (BusinessHelper.HandleWholesaleDeliveries, GameManager.cs:603)
        /// has NO ownership gate and no veil entry — the goods would land on the replica and vanish on
        /// the owner's next push while the delivery fee and the goods cost are real for everyone.
        /// CREATION is gated, never the pass: veiling HandleWholesaleDeliveries would also disturb
        /// contracts already in flight. Refusal = __result null, exactly what this method's own
        /// invalid-input paths return (:71, :76, :86) — the dialog entry simply does not advance, so
        /// there is no new on-screen text. The origin (the wholesale store NPC) is never a player
        /// building and so can never be flipped; only the DESTINATION business is tested.</summary>
        [HarmonyPatch(typeof(Dialogs.WholesaleStoreManagerDialog), "OnDeliveryContractSettingsSet")]
        public static class Patch_WholesaleContractCreate_MergerGate
        {
            static bool Prefix(ref Entities.DialogEntry __result)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0) return true;   // inert without a merger
                    var settings = DialogController.current?.GetInputComponent<UI.Dialog.DeliveryContractSettings>();
                    var reg = settings?.selectedBusiness;
                    if (reg == null) return true;                    // native handles the empty pick (:69)
                    bool rented; try { rented = reg.RentedByPlayer; } catch { return true; }
                    if (!rented || MergerFlip.TrulyMine(reg)) return true;
                    // WAVE 4 (V2a): the refusal becomes a ROUTE. The member's dialog adds NOTHING locally
                    // (__result null is the method's own invalid-input answer, :71/:76/:86, so the dialog
                    // simply does not advance and there is no new on-screen text); the operator replays the
                    // creation through the game's own construction and its publish brings the contract back
                    // as a display copy (V1). The wholesale store is the dialog's own contact (:87).
                    string bizKey = GameStateReader.AddressKey(reg);
                    string whKey = ""; try { whKey = GameStateReader.AddressKey(DialogController.current.contact.Address); } catch { }
                    if (whKey.Length == 0)
                    { Plugin.Logger.LogWarning($"[Merger] contract create REFUSED for '{bizKey}': the dialog's wholesale contact has no address."); __result = null; return false; }
                    if (!MPServer.IsRunning && !MPClient.IsConnected)
                    { Plugin.Logger.LogWarning($"[Merger] contract create REFUSED for '{bizKey}': no session to route it over."); __result = null; return false; }
                    SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                    { PlayerId = MPConfig.PlayerId, AddressKey = bizKey, Op = "mergercontract", StrValue = whKey });
                    __result = null;
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] contract-creation gate: {ex.Message}"); return true; }
            }
        }

        /// <summary>H-MERGERHIRE-1 — RECRUITMENT CAMPAIGN BOOKING FOR A MERGED COMPANY (2026-09-19,
        /// user-approved). RecruitmentAgencyDialog.cs:145 `private DialogEntry OnRecruitmentSettingsSet()` is
        /// the game's only booking point: it charges the price and adds the campaign to
        /// SaveGameManager.Current.RecruitmentCampaigns (:199), whose hourly tick then lands the candidates
        /// (RecruitmentHelper.RunHourly). On a member's machine both halves would be wrong - the money is real
        /// but the campaign would tick against a replica the owner never hears of - so the booking is ROUTED to
        /// the shop's real owner (op "mergercampaign") and NOTHING is charged or added here. The shop only
        /// reaches the picker at all when that owner can take it (AccessGates.RecruitmentRoutable).
        /// The member's screen is the game's own: the three native validations are re-run here with the game's
        /// OWN error keys and the method's own `null` answer, and on a successful route the native conversation
        /// tail is produced exactly (the two TextMessages and the same follow-up entry). NO NEW ON-SCREEN TEXT.
        /// The FIRST validation (no business picked, :148-152) is left to native: this prefix bows out before it.
        /// INERT without a merger, and on a shop that is truly mine (MergerFlip.TrulyMine) - the same test the
        /// wholesale contract gate above uses.</summary>
        [HarmonyPatch(typeof(Dialogs.RecruitmentAgencyDialog), "OnRecruitmentSettingsSet")]
        public static class Patch_RecruitmentCampaignCreate_MergerGate
        {
            static bool Prefix(Dialogs.RecruitmentAgencyDialog __instance, ref Entities.DialogEntry? __result)
            {
                bool sent = false;   // review HIGH-1: true once the routed leg may be on the wire
                try
                {
                    if (MergerFlip.FlippedCount == 0) return true;   // inert without a merger
                    var ctrl = DialogController.current;
                    var settings = ctrl?.GetInputComponent<UI.Dialog.RecruitmentSettings>();
                    var reg = settings?.selectedBusiness;
                    if (ctrl == null || settings == null || reg == null) return true;   // native raises its own "select a business" (:148-152)
                    bool rented; try { rented = reg.RentedByPlayer; } catch { return true; }
                    if (!rented || MergerFlip.TrulyMine(reg)) return true;

                    // The remaining two native validations, with the game's own keys and its own null answer.
                    if (!settings.hasSelectedSkill)
                    { UI.Notification.Notifications.ShowError("recruitmentagencydialog_notification_select_skill"); __result = null; return false; }
                    string skill = settings.selectedSkill ?? "";
                    bool full = false, part = false;
                    try { full = settings.fullTimeToggle.isOn; part = settings.partTimeToggle.isOn; } catch { }
                    bool wantsSchedule = false;
                    try
                    {
                        wantsSchedule = skill.Length > 0
                            && BigAmbitions.Characters.Skills.SkillHelper.GetData(skill).HasTag(BigAmbitions.Tags.TagRef.Skilltag.hashoursperweekdemand);
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] recruitment schedule test for '{skill}': {ex.Message}"); }
                    if (wantsSchedule && !part && !full)
                    { UI.Notification.Notifications.ShowError("recruitmentagencydialog_notification_select_schedule"); __result = null; return false; }

                    string bizKey = GameStateReader.AddressKey(reg);
                    string agencyKey = ""; try { agencyKey = GameStateReader.AddressKey(ctrl.contact.Address); } catch { }
                    if (agencyKey.Length == 0)
                    { Plugin.Logger.LogWarning($"[Merger] campaign booking REFUSED for '{bizKey}': the dialog's agency contact has no address."); __result = null; return false; }
                    if (!MPServer.IsRunning && !MPClient.IsConnected)
                    { Plugin.Logger.LogWarning($"[Merger] campaign booking REFUSED for '{bizKey}': no session to route it over."); __result = null; return false; }

                    int candidates = 0, days = 0; float quote = 0f;
                    try
                    {
                        candidates = (int)settings.candidatesAmountSlider.value;          // native cast (:174)
                        days = UnityEngine.Mathf.RoundToInt(settings.deadlineSlider.value);   // native rounding (:192)
                        quote = settings.totalPrice;
                    }
                    catch (Exception ex)
                    { Plugin.Logger.LogWarning($"[Merger] campaign booking REFUSED for '{bizKey}': the dialog's sliders could not be read ({ex.Message})."); __result = null; return false; }

                    sent = true;   // review HIGH-1: from here on native must NEVER run (see the catch below)
                    SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                    {
                        PlayerId = MPConfig.PlayerId, AddressKey = bizKey, Op = "mergercampaign", AgencyKey = agencyKey,
                        SkillName = skill, IntValue = candidates, BoolValue = full, PartTime = part, Days = days, Estimate = quote,
                    });
                    Plugin.Logger.LogInfo($"[Merger] recruitment campaign routed for '{bizKey}' ({skill}, {candidates} candidates, {days} days, quote {quote})");

                    // The native conversation tail (:201-231), unchanged. `text` comes from a THROWAWAY campaign
                    // carrying this booking's two toggles, so the wording is the game's own GetScheduleTypesInfo().
                    try
                    {
                        var shape = new Entities.RecruitmentCampaign { fullTime = full, partTime = part };
                        var messageData = new Dictionary<string, string>
                        {
                            { "businessName", reg.BusinessName ?? "" },
                            { "amountOfCandidates", candidates.ToString() },
                            { "skillKey", skill },
                            { "days", days.ToString() },
                            { "text", shape.GetScheduleTypesInfo() },
                        };
                        ctrl.contact.ReceivePlayerMessage(
                            new Entities.TextMessage("ba:messagetype_dialog_recruitment_agency_on_recruitment_settings_set_player", messageData, read: true));
                        ctrl.contact.SendMessage(
                            new Entities.TextMessage("ba:messagetype_dialog_recruitment_agency_on_recruitment_settings_set_recruiter", read: true));
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] recruitment conversation tail: {ex.Message}"); }

                    // The native follow-up entry. AlreadyHasCampaignActive is private, so it is called by
                    // reflection on the dialog instance. It is kept because it reads NOTHING about the local
                    // campaign list - it is the "anything else?" entry, and it is also the ONLY thing that gives
                    // this last entry any buttons at all (DialogController.SetCurrentEntryButtons:102-131 draws
                    // buttons only for OnCancel/OnConfirm/OnSecondOption, and this entry has none); dropping it
                    // would leave the member in a dialog with no way out. Its "manage campaigns" option lists the
                    // LOCAL save, which holds no copy of a routed campaign - see the build report.
                    var dlg = __instance;
                    __result = new Entities.DialogEntry
                    {
                        messageData = "dialog_recruitment_agency_on_recruitment_settings_set_recruiter".Localize(),
                        InputTemplate = Entities.DialogEntry.InputTemplateName.None,
                        OnVisible = delegate
                        {
                            try
                            {
                                var mi = AccessTools.Method(typeof(Dialogs.RecruitmentAgencyDialog), "AlreadyHasCampaignActive", new[] { typeof(bool) });
                                if (mi == null)
                                { Plugin.Logger.LogWarning("[Merger] recruitment tail: AlreadyHasCampaignActive not found — the conversation ends on the recruiter's line."); return; }
                                (mi.Invoke(dlg, new object[] { true }) as Entities.DialogEntry)?.ShowEntry();
                            }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] recruitment tail: {ex.Message}"); }
                        },
                    };
                    return false;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[Merger] campaign-booking gate: {ex.Message}");
                    // Review HIGH-1: once the routed leg may be on the wire, falling back to the native method would
                    // charge THIS machine and add a local campaign on top of the owner's - a double booking. The
                    // dialog simply does not advance (the method's own invalid-input answer).
                    if (sent) { __result = null; return false; }
                    return true;
                }
            }
        }

        /// <summary>THE CROSS-MEMBER DELIVERY ENTRY (4c part 2, D20-7) — was STOP-GAP S3, HQ LOGISTICS PLAN LEG.
        /// LogisticsManagerPlan.cs:74 `public void DeliverDestination(LogisticsManagerPlanDestination
        /// destination)` moves the goods for ONE leg. It checks only RAW RentedByPlayer on the plan's own
        /// warehouse (:79) — which the flip satisfies — and does not test the DESTINATION at all, and the
        /// logistics pass has no veil entry, so a leg would move goods into or out of a partner's flipped
        /// replica. A leg runs only when BOTH ends sit in the SAME operating set (W3-0 r3, F1): both mine, or
        /// both simulated here for an absent owner — on the machine standing in for one, its lifted copy is the
        /// live state, so that owner's own daily legs keep running while they are away. Every mixed leg is
        /// TAKEN OVER: one end mine and the other run here for an absent partner is a goods movement between
        /// two MEMBERS' buildings, which no single machine can perform — 4c part 2 hands it to CargoTransfer,
        /// the two-phase routed transfer (need → withdraw → deliver → ack → return), and the native method
        /// still does not run. Only a leg the transfer cannot even start (no plan id, no warehouse, no
        /// destination registration) falls back to the old refusal. The HQ owner's own legs are untouched
        /// (which is why this is a per-leg gate and not a veil entry). The plan's UI creation is not
        /// touched. LogisticsManagerPlan has NO name field (its id is a base64 uuid), so the plan is
        /// named in the log by its warehouse address key. One INFO per plan per game day.</summary>
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.LogisticsManagerPlan), "DeliverDestination")]
        public static class Patch_LogisticsPlanLeg_MergerGate
        {
            private static readonly System.Collections.Generic.Dictionary<string, int> _loggedDay = new System.Collections.Generic.Dictionary<string, int>();

            static bool Prefix(Buildings.Office.Headquarters.LogisticsManagerPlan __instance,
                               Entities.LogisticsManagerPlanDestination destination)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || __instance == null) return true;   // inert without a merger
                    // WAVE 4 (V1 execution guard): a DISPLAY COPY is a partner's plan installed here only so
                    // the member can SEE and EDIT it. It must never move goods on this machine. A plan this
                    // machine SIMULATES is not tagged (the absence installer tags under the owner's pid, the
                    // display installer under "display:<pid>"), so simulated legs still run.
                    if (CompanyLists.IsDisplayPlan(__instance))
                    {
                        int dday = 0; try { dday = SaveGameManager.Current != null ? SaveGameManager.Current.Day : 0; } catch { }
                        string dkey = __instance.id ?? "?";
                        if (!_loggedDay.TryGetValue(dkey, out var dseen) || dseen != dday)
                        {
                            _loggedDay[dkey] = dday;
                            Plugin.Logger.LogInfo($"[Merger] logistics plan '{dkey}' skipped - a display copy of a partner's plan, run by its operator.");
                        }
                        return false;
                    }
                    bool src = FlippedAddr(__instance.targetAddress);
                    bool dst = destination != null && FlippedAddr(destination.deliveryTargetAddress);
                    string srcKey = "", dstKey = "";
                    try { srcKey = GameStateReader.AddressKey(__instance.targetAddress); } catch { }
                    try { if (destination != null) dstKey = GameStateReader.AddressKey(destination.deliveryTargetAddress); } catch { }
                    // W3-0 r3 (F1): an end this machine SIMULATES is not "operated elsewhere" — it is operated
                    // HERE, for an absent owner. The two ends must sit in the SAME operating set: both mine, or
                    // both simulated here. Every mixed leg (one end mine + one simulated, or any end flipped and
                    // NOT simulated) is a cross-owner goods movement — 4c part 2's ROUTED TRANSFER, never
                    // something one machine performs. An end with no address does not vote: an unset destination
                    // row cannot cross an owner boundary.
                    if (!src && !dst) return true;                                        // both ends mine
                    bool srcSim = srcKey.Length == 0 || MergerAbsence.SimulatesHere(srcKey);
                    bool dstSim = dstKey.Length == 0 || MergerAbsence.SimulatesHere(dstKey);
                    if (srcSim && dstSim) return true;                                    // both ends simulated here
                    // 4c PART 2 (C1 TAKE-OVER): this is the mixed leg, and it is now a ROUTED CARGO TRANSFER
                    // rather than a refusal. The native method never runs either way — the transfer performs
                    // every native effect once, on the machine that holds the object (CargoTransfer.cs).
                    if (CargoTransfer.TakeOver(__instance, destination)) return false;
                    string plan = srcKey.Length > 0 ? srcKey : "?";
                    int day = 0; try { day = SaveGameManager.Current != null ? SaveGameManager.Current.Day : 0; } catch { }
                    string key = __instance.id ?? plan;
                    if (!_loggedDay.TryGetValue(key, out var seen) || seen != day)
                    {
                        _loggedDay[key] = day;
                        Plugin.Logger.LogInfo($"[Merger] logistics plan '{plan}' skipped - source/destination is a company building operated elsewhere and the routed cargo transfer could not be started for this leg");
                    }
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics leg gate: {ex.Message}"); return true; }
            }
        }

        /// <summary>WAVE 4 r2 (review MAJOR-4) — THE PLAN PASS ITSELF.
        /// `public IEnumerable&lt;LogisticsManagerPlanDestination&gt; GetPlannedDeliveries()`
        /// (LogisticsManagerPlan.cs:52) is what the hourly logistics pass walks for EVERY plan in
        /// gi.logisticsManagerPlans — display copies included, because that list is walked whole. Two things
        /// happen there that a partner's plan must never do on a member's machine: :56 calls
        /// `LogisticsManagerInstance.IsEmployeeAvailable()` on a manager this machine may not hold at all (a
        /// null instance — an NRE the hourly tick wall then eats, costing the member its own pass for that
        /// hour), and :58-60 NULLS `targetAddress` whenever the plan's warehouse is not RentedByPlayer here,
        /// which for a partner's warehouse outside the merged set is simply the truth — silently deleting the
        /// OWNER's warehouse from the OWNER's plan. A display copy is skipped whole: it is run by its
        /// operator, exactly like its legs (S3) and its wholesale contracts. A plan this machine SIMULATES is
        /// not a display copy (r2 MAJOR-1: the tag string decides) and still runs. Inert without a merger.</summary>
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.LogisticsManagerPlan), "GetPlannedDeliveries")]
        public static class Patch_LogisticsPlanPlanned_DisplayCopyGuard
        {
            private static readonly System.Collections.Generic.Dictionary<string, int> _loggedDay = new System.Collections.Generic.Dictionary<string, int>();

            static bool Prefix(Buildings.Office.Headquarters.LogisticsManagerPlan __instance,
                               ref System.Collections.Generic.IEnumerable<Entities.LogisticsManagerPlanDestination> __result)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || __instance == null) return true;   // inert without a merger
                    if (!CompanyLists.IsDisplayPlan(__instance)) return true;
                    __result = System.Linq.Enumerable.Empty<Entities.LogisticsManagerPlanDestination>();
                    int dday = 0; try { dday = SaveGameManager.Current != null ? SaveGameManager.Current.Day : 0; } catch { }
                    string dkey = __instance.id ?? "?";
                    if (!_loggedDay.TryGetValue(dkey, out var dseen) || dseen != dday)
                    {
                        _loggedDay[dkey] = dday;
                        Plugin.Logger.LogInfo($"[Merger] logistics plan '{dkey}' not planned here - a display copy of a partner's plan, run by its operator.");
                    }
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics plan-pass gate: {ex.Message}"); return true; }
            }
        }

        /// <summary>STOP-GAP S4 — HQ LOGISTICS PLAN EDIT (wave 3, W3-5b).
        /// S3 above gates the per-tick DELIVERY pass; the plan's own commits are un-gated. Decompile pass
        /// 2026-09-11: the EDIT UIs mutate a STORED plan object in place (LogisticsManagerPlanUI.cs :107/:227/
        /// :566, LogisticsManagerDestinationUI.cs :60, each followed by SaveGameManager.MarkChange; the only
        /// list reference in PlanUI is the READ at :178). The single place a plan enters the list is
        /// LogisticsManagersPlanList.cs:236 AddPlan → :243, stamping headquartersAddress from the BizMan page
        /// being viewed (:240). A plans list is per-save, so a plain co-member holds NO plan object for a
        /// partner's HQ — the in-place mutations are unreachable there and only CREATION needs the gate.
        /// The two dropdowns are gated as well, because they are reachable on a member's OWN plan and can
        /// name a partner's flipped building as source or destination — a leg S3 then refuses every day.
        /// W3-0 r3 (F2): what decides is the PLAN's own operating set, read off its headquartersAddress — a
        /// plan whose HQ this machine SIMULATES may name only ends it simulates, a plan whose HQ is mine may
        /// name only mine, and a plan belonging to a partner who is here to run it is not editable at all.
        /// That is the line S3 draws on the leg, drawn one step earlier, so the mixed plan cannot be built.
        /// Refusal = the
        /// native method does not run: silent to the player, one INFO line in the existing merger wording,
        /// inert without a merger. Wave 4 replaces these with routes.</summary>
        private static bool PartnerOperated(BuildingRegistration reg)
        {
            if (reg == null) return false;
            bool rented; try { rented = reg.RentedByPlayer; } catch { return false; }
            if (!rented || MergerFlip.TrulyMine(reg)) return false;
            try { if (MergerAbsence.SimulatesHere(GameStateReader.AddressKey(reg))) return false; } catch { }
            return true;
        }

        // W3-0 r3 (F2): which operating set a plan or an end belongs to. SetOwn = this player's own building;
        // SetSim = a building this machine runs for an ABSENT owner; SetElsewhere = a partner's building run on
        // another machine; SetNone = no address at all (an unset dropdown row — it does not vote).
        private const int SetOwn = 0, SetSim = 1, SetElsewhere = -1, SetNone = 2;

        private static int SetOf(string key, bool flipped)
        {
            if (key == null || key.Length == 0) return SetNone;
            if (!flipped) return SetOwn;
            try { return MergerAbsence.SimulatesHere(key) ? SetSim : SetElsewhere; } catch { return SetElsewhere; }
        }

        private static int OperatingSet(Address address)
        {
            string k = ""; try { k = GameStateReader.AddressKey(address); } catch { }
            return SetOf(k, FlippedAddr(address));
        }

        private static int OperatingSet(BuildingRegistration reg)
        {
            if (reg == null) return SetNone;
            string k = ""; try { k = GameStateReader.AddressKey(reg); } catch { }
            bool rented; try { rented = reg.RentedByPlayer; } catch { return SetNone; }
            return SetOf(k, rented && !MergerFlip.TrulyMine(reg));
        }

        /// <summary>The PLAN's set, read off its headquartersAddress (LogisticsManagerPlan.cs:36). A plan the
        /// UI has not loaded (null) counts as this player's own: both native commits dereference _currentPlan
        /// unconditionally (LogisticsManagerPlanUI.cs:214/:568), so a null there is native's own crash and not
        /// a case this gate should widen for.</summary>
        private static int PlanSet(Buildings.Office.Headquarters.LogisticsManagerPlan plan)
        {
            if (plan == null) return SetOwn;
            int s = OperatingSet(plan.headquartersAddress);
            return s == SetNone ? SetOwn : s;
        }

        /// <summary>May this end go on that plan? The PLAN must still be one this machine may edit at all -
        /// mine, or one I run for an absent owner. PHASE 5 r2 (J2): the END no longer has to sit in the
        /// same operating set. Since 4c part 2b the leg gate hands a mixed-owner leg to the ROUTED CARGO
        /// TRANSFER at delivery time, so a CO-MEMBER's building is a legal end; only an end outside the
        /// company stays refused. SetNone (clearing a row) is allowed on any plan we may edit at all.</summary>
        private static bool EndFitsPlan(int planSet, int endSet, string endKey)
            => (planSet == SetOwn || planSet == SetSim)
               && (endSet == SetNone || endSet == planSet || EndInCompany(endKey));

        /// <summary>PHASE 5 r2 (J2): is this end a building of MY COMPANY? The company's address->owner map
        /// (CompanyLists) answers for a PARTNER's building; MY OWN building is not in that map, so it is
        /// checked against the registry instead - rented here and truly mine, while I am in a company.
        /// Everything else - a non-member's building, an address this machine cannot place - is false, so
        /// it stays refused exactly as before.</summary>
        private static bool EndInCompany(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            try
            {
                if (!MergerSync.IAmMember) return false;
                if (CompanyLists.TryOwnerOfAddress(key, out var owner) && !string.IsNullOrEmpty(owner))
                    return MergerSync.MergedRuntime(owner, MPConfig.PlayerId);
                var reg = GameStatePatcher.FindRegistration(key);
                return reg != null && reg.RentedByPlayer && MergerFlip.TrulyMine(reg);
            }
            catch { return false; }
        }

        /// <summary>WAVE 4 (V2c): a TAGGED DISPLAY COPY of a partner's plan IS editable here — the native UI
        /// mutates it in place and the mutation is then routed to the operator. PHASE 5 r2 (J2): an end that
        /// belongs to ANOTHER MEMBER of the same company - my own building included - is allowed too, because
        /// the routed cargo transfer of 4c part 2b moves the goods between the two machines at delivery time.
        /// What stays refused is an end OUTSIDE the company, or one no map can place. `key` empty = clearing
        /// a row, always allowed.</summary>
        private static bool RoutedEndFits(Buildings.Office.Headquarters.LogisticsManagerPlan plan, string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            string hq = ""; try { hq = GameStateReader.AddressKey(plan.headquartersAddress); } catch { }
            if (!CompanyLists.TryOwnerOfAddress(hq, out var hqOwner) || string.IsNullOrEmpty(hqOwner)) return false;
            if (CompanyLists.TryOwnerOfAddress(key, out var endOwner) && endOwner == hqOwner) return true;
            // The plan's company must be MINE before a co-member's building counts as one of its ends.
            return MergerSync.MergedRuntime(hqOwner, MPConfig.PlayerId) && EndInCompany(key);
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList), "AddPlan")]
        public static class Patch_LogisticsPlanCreate_MergerGate
        {
            static bool Prefix(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList __instance)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0) return true;   // inert without a merger
                    var ui = InstanceBehavior<UI.UIs>.Instance;
                    var biz = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                    var reg = biz != null ? biz.buildingRegistration : null;
                    if (!PartnerOperated(reg)) return true;
                    // WAVE 4 (V2c): creation becomes a ROUTE. Nothing is added to this machine's list - the
                    // operator creates the plan and its publish brings the display copy back (V1).
                    string hq = GameStateReader.AddressKey(reg);
                    bool isFactory = false;
                    try { isFactory = (__instance.currentTab ?? "").Equals("factory", StringComparison.OrdinalIgnoreCase); } catch { }
                    if (!CompanyLists.RoutePlanCreate(hq, isFactory))
                        Plugin.Logger.LogWarning($"[Merger] logistics plan at '{hq}' refused - company building operated elsewhere and the route could not be sent.");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics plan-create gate: {ex.Message}"); return true; }
            }
        }

        /// <summary>HQ-PARITY-8 G1 — THE PANE'S DELETE BUTTON.  `LogisticsManagerPlanUI.DeletePlan`
        /// (decompile :239-247) deletes through `LogisticsManagerHelper.DeletePlan(_currentPlan.id)` and
        /// mutates no plan, so the MarkChange seam above has nothing to diff: a member's delete of a
        /// partner's plan vanished locally and came back at the owner's next publish.  Gated like the HR /
        /// pricing / headhunter delete buttons (:11408 / :11454 / :11817), and applied at the owner by
        /// ApplyLogistics' `delete`.
        /// ____currentPlan = '___' + '_currentPlan' (LogisticsManagerPlanUI.cs:71).</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI), "DeletePlan")]
        public static class Patch_LogisticsPaneDelete_MergerGate
        {
            static bool Prefix(Buildings.Office.Headquarters.LogisticsManagerPlan ____currentPlan)
            { try { return !CompanyPlans.RoutePaneEdit("logistics", "DeletePlan", ____currentPlan, "delete"); } catch { return true; } }
        }

        /// <summary>HQ-PARITY-8 G2 — THE PLAN-ROW DRAG.  `LogisticsManagersPlanList.OnPlanReordered` (:75-85)
        /// is the fifth family's drop handler and was the only one unhooked; same refusal as the other four
        /// (:10904-10931).  Row order is a display preference, so there is nothing for a route to carry.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList), "OnPlanReordered")]
        public static class Patch_LogisticsPlanReorder_MergerGate
        {
            static bool Prefix(int fromIndex, int toIndex)
            { try { return !CompanyPlans.RefuseReorder("logistics", fromIndex, toIndex); } catch { return true; } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI), "UpdateSelectedBusiness")]
        public static class Patch_LogisticsPlanDestination_MergerGate
        {
            // ____currentPlan = ___ + the field name '_currentPlan' (LogisticsManagerPlanUI.cs:71
            // `private LogisticsManagerPlan _currentPlan;`) = four underscores.
            static bool Prefix(Address businessAddress, Buildings.Office.Headquarters.LogisticsManagerPlan ____currentPlan)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0) return true;
                    string a = ""; try { a = GameStateReader.AddressKey(businessAddress); } catch { }
                    // WAVE 4 (V2c): on a tagged DISPLAY COPY the native mutation is allowed and the Postfix
                    // routes the whole plan; only an end outside the company is still refused (r2 J2).
                    if (CompanyLists.IsDisplayPlan(____currentPlan))
                    {
                        if (RoutedEndFits(____currentPlan, a)) return true;
                        Plugin.Logger.LogWarning($"[Merger] plan REFUSED cross-owner for '{a}' - that building is not part of this company.");
                        return false;
                    }
                    if (EndFitsPlan(PlanSet(____currentPlan), OperatingSet(businessAddress), a)) return true;
                    Plugin.Logger.LogWarning($"[Merger] logistics plan destination '{a}' refused - that building is not part of this company.");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics plan-destination gate: {ex.Message}"); return true; }
            }

            static void Postfix(Buildings.Office.Headquarters.LogisticsManagerPlan ____currentPlan)
            {
                try { CompanyLists.RoutePlanEdit(____currentPlan, "destination changed"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] plan-destination route: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI), "OnChangedWarehouse")]
        public static class Patch_LogisticsPlanWarehouse_MergerGate
        {
            // ___ + the field names '_warehouses' (LogisticsManagerPlanUI.cs:73) and '_currentPlan' (:71
            // `private LogisticsManagerPlan _currentPlan;`) = four underscores each.
            static bool Prefix(int warehouseIndex, System.Collections.Generic.List<BuildingRegistration> ____warehouses,
                               Buildings.Office.Headquarters.LogisticsManagerPlan ____currentPlan)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || warehouseIndex <= 0 || ____warehouses == null
                        || warehouseIndex - 1 >= ____warehouses.Count) return true;   // index 0 = clearing the warehouse
                    var reg = ____warehouses[warehouseIndex - 1];
                    string wk = ""; try { wk = GameStateReader.AddressKey(reg); } catch { }
                    if (CompanyLists.IsDisplayPlan(____currentPlan))
                    {
                        if (RoutedEndFits(____currentPlan, wk)) return true;   // the Postfix on LoadPlan routes it
                        Plugin.Logger.LogWarning($"[Merger] plan REFUSED cross-owner for '{wk}' - that building is not part of this company.");
                        return false;
                    }
                    if (EndFitsPlan(PlanSet(____currentPlan), OperatingSet(reg), wk)) return true;
                    Plugin.Logger.LogWarning($"[Merger] logistics plan warehouse '{wk}' refused - that building is not part of this company.");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics plan-warehouse gate: {ex.Message}"); return true; }
            }
        }

        /// <summary>WAVE 4 (V2c) — ONE OF THE TWO ROUTE SEAMS for an in-place mutation of a tagged
        /// display plan.  `public void LoadPlan(LogisticsManagerPlan plan)` is re-run by the warehouse
        /// dropdown (LogisticsManagerPlanUI.cs:569), a destination reorder (:113) and the destination REMOVE
        /// button (LogisticsManagerDestinationUI.cs:60 via `_logisticsManagerPlanUI.LoadPlan(_currentPlan)`),
        /// each mutating the STORED plan object in place.  FOLD b (review MAJOR-1/2): it is NOT the catch-all
        /// it was described as - the per-item target field (:387/:406/:424), AddDestination (:227-237) and
        /// UpdateSelectedBusiness (:566-575) never come back through here; they end at
        /// SaveGameManager.MarkChange, which is the OTHER seam (the postfix below). Both seams call the one
        /// method CompanyLists.RouteDisplayPlanIfChanged. CompanyLists dedupes by the plan's
        /// serialised shape, so a plain re-selection sends nothing - and HQ-PARITY-1 c3 gives the OWNER's leg
        /// the SAME dedupe (CompanyLists.OwnPlanChanged), because LoadPlan also runs on every plain click
        /// (LogisticsManagersPlanList.SelectPlan :228) and a pure read must never publish.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI), "LoadPlan")]
        public static class Patch_LogisticsPlanLoad_MergerRoute
        {
            /// <summary>HQ-PARITY-2 FOLD b3, THE PANE REGISTER (review MAJOR-4/5).  The call-window scope
            /// is gone: it was open only for the length of one LoadPlan, so it covered the FIRST draw and
            /// nothing else - AddDestination (:227-237, which re-reads MaxDestinations at :235 and through
            /// AddDestinationEntry at :265) and UpdateSelectedBusiness (:566-575, which calls LoadProducts
            /// and so CountResourcesInPallets at :296) both run from a CLICK handler with no LoadPlan around
            /// them, and drew a new row at half alpha with the add button dead and stale stock beside it.
            /// This postfix now only REMEMBERS the pane object; the two prefixes below answer off the PLAN.</summary>
            /// <summary>HQ-PARITY-3 A1 — THE DELETED MECHANISM: a LOAD no longer commits anything.  This
            /// postfix used to call RouteDisplayPlanIfChanged, which made a pure READ a commit point protected
            /// by nothing but a byte-exact JSON dedupe between a baseline seeded one way and a shape built
            /// another; the hands-on run caught it sending a WHOLE-PLAN replace on a plain open and wrecking
            /// the owner's real plan.  LoadPlan writes nothing to the plan (decompile :127-155): SetOptions
            /// never raises onOptionSelected (Dropdown.cs:304-322) and the stock targets are only ever added
            /// inside the click/onEndEdit delegates (:378-383/:399-402).  So the load SHUTS the seam (Prefix),
            /// and when it is the OPENING of a plan - the list's own SelectPlan is on the stack - it CAPTURES
            /// the copy exactly as it now stands as the baseline every later edit is diffed against.  The
            /// commit points that remain are UpdateSelectedBusiness, OnChangedWarehouse, ChangeLogisticsManager
            /// and SaveGameManager.MarkChange outside a load.</summary>
            /// <summary>HQ-PARITY-7 R2: one number per LoadPlan.  The pallet-count prefix says ONCE per load
            /// what the stock substitution decided; raised here in the Prefix so it is already this load's
            /// number by the time the pane draws its product rows.</summary>
            internal static int LoadPlanSerial;

            /// <summary>HQ-PARITY-9 (2026-09-17): THE PANE IS NOTED BEFORE THE DRAW, NOT AFTER.  The list hides
            /// the pane on every tab switch (LogisticsManagersPlanList.cs:94, RefreshManagersList) and LoadPlan
            /// draws the product rows (:139-142, AddDestinationEntry -> LoadProducts -> CountResourcesInPallets)
            /// BEFORE it re-activates the pane (:154).  With the pane noted only in the Postfix, the first
            /// LoadPlan of a session asked the stock prefix with no pane at all; and CompanyPlans.PaneDisplayPlan
            /// now treats "inside LoadPlan" as showing (CompanyLists.LoadingPlan), which needs the pane that is
            /// loading to be the one on record.  `_currentPlan` is the first line of LoadPlan (:129), so a
            /// live read during the draw already sees the new plan.</summary>
            static void Prefix(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI __instance)
            {
                LoadPlanSerial++; CompanyLists.LoadingPlan = true;
                try { CompanyPlans.NoteLogisticsPane(__instance); CompanyPlans.RegisterPane("logistics", __instance); } catch { }
            }

            static void Finalizer() { CompanyLists.LoadingPlan = false; }

            static void Postfix(Buildings.Office.Headquarters.LogisticsManagerPlan plan,
                                UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI __instance)
            {
                try
                {
                    CompanyPlans.NoteLogisticsPane(__instance);
                    CompanyPlans.RegisterPane("logistics", __instance);          // HQ-PARITY-4 P1
                    if (MergerFlip.FlippedCount == 0 || plan == null) return;   // inert without a merger
                    if (!CompanyLists.IsDisplayPlan(plan))
                    {
                        // HQ-PARITY-1 c3: only a CHANGED own plan commits; opening one is a read.
                        if (CompanyLists.OwnPlanChanged(plan)) CompanyPlans.OwnEditCommitted("logistics plan edited");
                        return;
                    }
                    // HQ-PARITY-3 A3: A REORDER IS IMPOSSIBLE ON A PARTNER'S PLAN.  The pane's destination
                    // rows are the one set this mod never stripped - the drag handle strip was applied only to
                    // plan-LIST rows - and a drag there rewrites the owner's order through a diff the runner
                    // cannot express (its destchange Resets every row it rewrites).  The handle comes off each
                    // row the instant the pane has drawn it, exactly as it does for a partner's list rows.
                    StripDestinationDrag(__instance);
                    if (Patch_LogisticsSelectPlan_Opening.Opening) CompanyLists.CaptureLogisticsBaseline(plan);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] plan-load route: {ex.Message}"); }
            }

            private static System.Reflection.FieldInfo? _fEntries;

            private static void StripDestinationDrag(object ui)
            {
                try
                {
                    if (ui == null) return;
                    if (_fEntries == null)
                        _fEntries = ui.GetType().GetField("_destinationEntries",
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var rows = _fEntries != null ? _fEntries.GetValue(ui) as System.Collections.IEnumerable : null;
                    if (rows == null) return;
                    foreach (var r in rows)
                    {
                        var c = r as UnityEngine.Component;
                        if (c != null) CompanyPlans.StripDragHandle(c.transform);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] destination drag strip: {ex.Message}"); }
            }
        }

        /// <summary>HQ-PARITY-3 A1: WHICH LoadPlan is an OPENING.  `LogisticsManagersPlanList.SelectPlan
        /// (Transform, LogisticsManagerPlan)` (decompile :207) is the one path a click on a plan row takes,
        /// and it ends at LoadPlan; every OTHER LoadPlan is a redraw that FOLLOWS a mutation (the warehouse
        /// dropdown :223, the destination remove button LogisticsManagerDestinationUI.cs:60, a reorder :115),
        /// and re-taking the baseline there would swallow the very edit the following MarkChange is about to
        /// route.  So the capture is gated on the opening, and on nothing else.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList), "SelectPlan",
                      new[] { typeof(UnityEngine.Transform), typeof(Buildings.Office.Headquarters.LogisticsManagerPlan) })]
        public static class Patch_LogisticsSelectPlan_Opening
        {
            internal static bool Opening;
            static void Prefix() { Opening = true; }
            static void Finalizer() { Opening = false; }
        }

        /// <summary>HQ-PARITY-3 A3: the drop itself.  `OnDestinationReordered` (decompile
        /// LogisticsManagerPlanUI.cs:107-117) moves a destination in the list and re-loads the pane; on a
        /// PARTNER's plan the change cannot travel (a permutation is refused by the op diff, because the
        /// runner's destchange Resets the runtime state of every row it rewrites) and under A2 it is now
        /// sent nowhere at all - so the drop must not happen on the copy either, or the pane would show an
        /// order the owner does not have.  Refusing in the prefix lets the list finish its own drag cleanup.
        /// FOLD b B5 (review F7): AND THE ROWS GO BACK.  The refusal stopped the DATA move but left the
        /// ReorderableList's own visual move standing, so the rows sat in an order the plan did not have.  The
        /// prefix now re-loads the pane from the unchanged plan - exactly what the native handler does after
        /// its own data move (decompile LogisticsManagerPlanUI.cs:113-116) - and that load is inert under A1's
        /// loading flag, so it routes nothing.
        /// Belt and braces with the handle strip above; silent to the player, one INFO line per plan.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI), "OnDestinationReordered")]
        public static class Patch_LogisticsReorder_DisplayRefuse
        {
            private static readonly System.Collections.Generic.HashSet<string> _logged = new System.Collections.Generic.HashSet<string>();

            /// <summary>B6: the plan ids of a dissolved owner leave the once-per-plan set with them.</summary>
            internal static void Forget(System.Collections.Generic.IEnumerable<string> ids)
            {
                if (ids == null) return;
                foreach (var id in ids) if (!string.IsNullOrEmpty(id)) _logged.Remove(id);
            }

            static bool Prefix(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI __instance,
                               Buildings.Office.Headquarters.LogisticsManagerPlan ____currentPlan)
            {
                try
                {
                    if (____currentPlan == null || !CompanyLists.IsDisplayPlan(____currentPlan)) return true;
                    if (_logged.Add(____currentPlan.id ?? "?"))
                        Plugin.Logger.LogInfo($"[Merger] logistics plan '{____currentPlan.id}': a destination reorder is not made on a partner's plan - the order is the owner's and no single op can carry it.");
                    if (__instance != null) __instance.LoadPlan(____currentPlan);   // B5: the rows go back to the owner's order
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] destination reorder gate: {ex.Message}"); return true; }
            }
        }

        /// <summary>HQ-PARITY-3 A5 — RUNS OUT IN.  `LogisticsManagerPlan.GetRunsOutIn(string, int)` (decompile
        /// :180-195) divides the stock by the LOCAL seven-day sales of every BuildingRegistration on this
        /// machine.  A partner's sales are in none of them, so the pane drew "never" beside a stock figure
        /// that was already the owner's.  Gated exactly as the pallet count is - the plan asked about must be
        /// the display copy the pane is showing - and answering with the decompile's own outcomes off the
        /// weekly sales the owner published.</summary>
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.LogisticsManagerPlan), "GetRunsOutIn")]
        public static class Patch_LogisticsRunsOutIn_DisplayScope
        {
            static bool Prefix(Buildings.Office.Headquarters.LogisticsManagerPlan __instance,
                               string product, int currentStock, ref int __result)
            {
                try
                {
                    int days;
                    if (!CompanyPlans.ScopedRunsOutIn(__instance, product, currentStock, out days)) return true;
                    __result = days;
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics runs-out-in: {ex.Message}"); return true; }
            }
        }

        /// <summary>HQ-PARITY-2 P2 PREFIX (a) — THE CAPACITY.  `LogisticsManagerPlan.MaxDestinations`
        /// (decompile LogisticsManagerPlan.cs:43) is `CalculateMaxDestinations(targetAddress,
        /// assignedEmployeeId)`, whose first term is the warehouse's `vehicleSlots.Sum(s =&gt;
        /// s.DestinationsThatCanDeliver)` (:151) and that needs `SaveGameManager.Current.VehicleInstances
        /// .Find(vehicleInstanceId)` (Entities/VehicleSlot.cs:13-22).  A partner's VehicleInstances are
        /// deliberately NOT in this machine's save list, so the sum is 0, the method short-circuits at :152-154
        /// and the pane greys every destination row and kills the add-destination button.  FOLD b3: the
        /// answer follows THE PLAN, not a call window - whenever `__instance` is a tagged display copy whose
        /// owner has published numbers the OWNER's figure answers, so the reads AddDestination (:235) and
        /// AddDestinationEntry (:265) make from a CLICK handler are answered too.  Outside that - my own
        /// plans, the delivery pass's own reads (GetPlannedDeliveries :54/:63), every machine off a merger -
        /// there is no display copy at all and the prefix returns true on its first test.</summary>
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.LogisticsManagerPlan), "MaxDestinations", MethodType.Getter)]
        public static class Patch_LogisticsMaxDestinations_DisplayScope
        {
            static bool Prefix(Buildings.Office.Headquarters.LogisticsManagerPlan __instance, ref int __result)
            {
                try
                {
                    int max;
                    if (!CompanyPlans.ScopedMaxDestinations(__instance, out max)) return true;
                    __result = max;
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics capacity: {ex.Message}"); return true; }
            }
        }

        /// <summary>HQ-PARITY-2 P2 PREFIX (b) — THE STOCK.  `BuildingHelper.CountResourcesInPallets`
        /// (Helpers/BuildingHelper.cs:391-413) sums this machine's item instances for the address; a partner
        /// warehouse's replica is seeded once at world-live and refreshed only while somebody is inside it,
        /// so the pane's product rows (LogisticsManagerPlanUI.cs:296-298, and GetRunsOutIn off the same
        /// number) draw stale or empty.  FOLD b3: the condition is THE PANE, not a call window - the pane is
        /// active in the hierarchy, its `_currentPlan` is a display copy, and `address` is that plan's own
        /// source warehouse (key compare); every other address and every other caller falls through.  That
        /// also covers LoadProducts called straight from UpdateSelectedBusiness (:566-575), which the window
        /// missed.  INERT OUTSIDE: off a session and on my own plans there are no display copies at all; the
        /// delivery pass never runs on one (the plan-pass gate above lifts them out); and a co-member's own
        /// delivery arithmetic never counts pallets in a PARTNER's warehouse.
        /// FOLD b B4 (review F5): THE OUTER GATE IS THE PANE AGAIN.  It had become `MergerFlip.FlippedCount
        /// == 0`, which is up for the whole session, so the purchasing answer below was consulted for EVERY
        /// caller of this method while a partner's purchasing plan happened to be on screen - the simulation's
        /// own (Entities/ImportProduct.cs:45, Entities/Warehouse.cs:121) included, and a co-member's local
        /// ordering maths would then have been done with the OWNER's pallet count.  The comment that used to
        /// stand here said that was "the better answer anyway": it was not, it was the defect.  Nothing is
        /// scoped now unless a logistics pane is tracked or the purchasing pane's own product model is on the
        /// stack.  An item the owner does not hold is 0 there and is answered 0 here.</summary>
        [HarmonyPatch(typeof(Helpers.BuildingHelper), "CountResourcesInPallets")]
        public static class Patch_CountResourcesInPallets_DisplayScope
        {
            static bool Prefix(Address address, string resourceName, ref int __result)
            {
                try
                {
                    if (address == null) return true;
                    if (!(CompanyPlans.LogisticsPaneTracked || CompanyPlans.PurchasingModelScope))
                    { NoteStockNotSubstituted(address, resourceName, null); return true; }   // HQ-PARITY-6 P3
                    string key = ""; try { key = GameStateReader.AddressKey(address); } catch { }
                    if (key.Length == 0) return true;
                    int count;
                    if (CompanyPlans.LogisticsPaneTracked && CompanyPlans.ScopedStock(key, resourceName, out count))
                    { NoteStockDuringLoadPlan(key, resourceName, count); __result = count; return false; }   // R2
                    // HQ-PARITY-3 A7 — THE SECOND GATE, PURCHASING.  PurchasingAgentProductModel.UpdateWarehouse
                    // (decompile :66-70) counts LOCAL pallets in the product's assigned warehouse, so a
                    // partner's purchasing rows drew this machine's stale replica.  The gate is the same shape
                    // as the logistics one: the purchasing pane is active, its `_currentImportPartnership`
                    // (read live) is a partner's display row, and the address is one of that partnership's own
                    // assigned warehouses.  Everything else falls through to the native count.  FOLD b B4: and
                    // only for the pane's OWN caller, PurchasingAgentProductModel.UpdateWarehouse.
                    if (CompanyPlans.PurchasingModelScope && CompanyPlans.ScopedPurchasingStock(key, resourceName, out count))
                    { __result = count; return false; }
                    if (CompanyPlans.LogisticsPaneTracked) NoteStockDuringLoadPlan(key, resourceName, null);   // R2
                    NoteStockNotSubstituted(address, resourceName, key);   // HQ-PARITY-6 P3
                    return true;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics stock: {ex.Message}"); return true; }
            }

            /// <summary>HQ-PARITY-7 R2 - ONE LINE PER LOADPLAN SAYS WHAT THE SUBSTITUTION DECIDED.  The pane
            /// asks this counter once per listed product (LogisticsManagerPlanUI.cs:296), so a line per call
            /// is a flood, and the once-per-pair miss line below stays silent whenever the figures are wrong
            /// for a reason its own test does not cover - which is how the R1 failure went unrecorded.  This
            /// says, once for each LoadPlan, which plan the pane was holding, whether it was still TAGGED,
            /// whether the owner's DTO was found for its id, how many stock lines that DTO carried and what
            /// the first product's figure came out as.  The serial compare is the FIRST thing tested after
            /// the cheap pane flag, so on every other call nothing is asked and no string is built.</summary>
            private static int _lastStockLogSerial = -1;

            private static void NoteStockDuringLoadPlan(string key, string itemName, int? count)
            {
                try
                {
                    if (!CompanyPlans.LogisticsPaneTracked) return;
                    int serial = Patch_LogisticsPlanLoad_MergerRoute.LoadPlanSerial;
                    if (_lastStockLogSerial == serial) return;
                    _lastStockLogSerial = serial;           // fold b: claim the serial first, so a pane that is
                    var pl = CompanyPlans.PaneDisplayPlan(); // not a partner's copy costs ONE read per LoadPlan
                    if (pl == null) return;                 // not a partner's copy even by id - the miss line speaks
                    string id = pl.id ?? "";
                    var dto = string.IsNullOrEmpty(id) ? null : CompanyPlans.LogisticsDtoOf(id);
                    Plugin.Logger.LogInfo($"[Plans] stock during LoadPlan #{serial} of {id} at {key}: "
                        + $"display={CompanyLists.IsDisplayPlan(pl)} dto={(dto == null ? "missing" : "found")} "
                        + $"lines={dto?.Stock?.Count ?? 0} '{itemName}' -> {(count.HasValue ? count.Value.ToString() : "vanilla")}");
                }
                catch { }
            }
        }

        /// <summary>HQ-PARITY-6 P3 — THE SILENT MISS BECOMES A LINE.  When the pane draws a partner's plan it
        /// asks this counter once per listed product (LogisticsManagerPlanUI.cs:296); if the substitution
        /// above does not fire, the native count of a replica that holds no pallets draws 0 and NOTHING said
        /// why - the factory tab showed 0 for every product from the start, and the warehouse tab fell to 0
        /// after a destination removal, with no evidence either way. This says why, ONCE per plan-and-address
        /// pair. Cost: nothing while no partner plan is on screen (PaneDisplayPlan answers null on a
        /// registered-pane miss or a hidden pane before anything is allocated), and the message strings are
        /// built only on a genuine miss.  HQ-PARITY-7 R2: a null answer from a pane that IS up is no longer
        /// silence either - it is the R1 failure itself, and it gets its own once-per-address line.
        /// FOLD b H3: the pair is the KEY, not a concatenated string - the dedupe test now runs before any
        /// allocation at all, and before ScopedStockReason is asked (that call does real work to word the
        /// reason, and on a pane the player leaves open it was being paid on every product of every frame
        /// only to have its answer thrown away). The pair is recorded ONLY once a reason actually came back,
        /// so a product that was in order the first time is still free to speak later. Cleared with the rest
        /// of the merger state (CompanyPlans.ClearAll), so a new merger starts silent again.</summary>
        private static readonly System.Collections.Generic.HashSet<(string id, string key)> _stockMissLogged = new();

        /// <summary>FOLD b H3: called from CompanyPlans.ClearAll - every dissolve, disconnect and reload runs
        /// through it, so the once-per-pair promise is per merger rather than per process.</summary>
        internal static void ClearStockMissLog() { try { _stockMissLogged.Clear(); } catch { } }

        private static void NoteStockNotSubstituted(Address address, string resourceName, string key)
        {
            try
            {
                if (MergerFlip.FlippedCount == 0) return;
                var pl = CompanyPlans.PaneDisplayPlan();
                if (key == null) { try { key = GameStateReader.AddressKey(address); } catch { return; } }
                if (string.IsNullOrEmpty(key)) return;
                if (pl == null)
                {
                    // HQ-PARITY-7 R2: THE MISS THAT SAID NOTHING.  A logistics pane that is up while the
                    // merger is flipped, holding a plan this mod does not recognise as a partner's copy, took
                    // the early return here - which is exactly the R1 failure, drawn from the empty local
                    // replica in silence.  Same once-per-pair budget, with an empty id as the pair's id half.
                    if (!CompanyPlans.LogisticsPaneTracked) return;
                    if (_stockMissLogged.Contains(("?", key))) return;   // fold b: its own slot, not an empty-id plan's
                    _stockMissLogged.Add(("?", key));
                    Plugin.Logger.LogInfo($"[Plans] logistics stock NOT substituted at {key}: pane plan not recognised as a partner copy");
                    return;
                }
                if (pl.targetAddress == null) return;
                string tkey = ""; try { tkey = GameStateReader.AddressKey(pl.targetAddress); } catch { }
                if (!string.Equals(tkey, key, StringComparison.OrdinalIgnoreCase)) return;   // some other building's count
                string id = pl.id ?? "";
                if (_stockMissLogged.Contains((id, key))) return;   // H3: said once already - ask nothing, build nothing
                string? reason = CompanyPlans.ScopedStockReason(id, key, resourceName);
                if (reason == null) return;   // the substitution was in order after all
                _stockMissLogged.Add((id, key));
                Plugin.Logger.LogInfo($"[Plans] logistics stock NOT substituted for display plan {id} at {key}: {reason}");
            }
            catch { }
        }

        /// <summary>FOLD b B4 — THE PURCHASING PANE'S OWN PALLET READ, AND NO OTHER.
        /// `PurchasingAgentProductModel.UpdateWarehouse()` (decompile
        /// UI.Smartphone.Apps.BizMan.PurchasingAgent/PurchasingAgentProductModel.cs:63-67) is the one place the
        /// purchasing product rows get their `WarehouseStock` number, and it is the only caller of
        /// CountResourcesInPallets that should ever be answered with a PARTNER's count.  The flag is raised for
        /// the length of that call and dropped in a FINALIZER, which Harmony 2.3.3 runs whether the body
        /// returned or threw - a postfix would leak the flag on a throw and hand the owner's numbers to the
        /// simulation.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductModel), "UpdateWarehouse")]
        public static class Patch_PurchasingProductModel_StockScope
        {
            static void Prefix() { CompanyPlans.PurchasingModelScope = true; }
            static void Finalizer() { CompanyPlans.PurchasingModelScope = false; }
        }

        /// <summary>FOLD b B5 (review F7) — A DESTINATION ROW ADDED AFTER THE LOAD.  `AddDestinationEntry(int)`
        /// (decompile LogisticsManagerPlanUI.cs:262-269) instantiates the row, adds it to `_destinationEntries`
        /// and calls LoadProducts - it never goes through LoadPlan, so the postfix that strips the drag handles
        /// off a partner's rows never saw it and the new row stayed draggable.  Its handle comes off here, the
        /// same way and with the same helper.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI), "AddDestinationEntry")]
        public static class Patch_LogisticsAddDestEntry_StripDrag
        {
            private static System.Reflection.FieldInfo? _fEntries;

            static void Postfix(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI __instance,
                                Buildings.Office.Headquarters.LogisticsManagerPlan ____currentPlan)
            {
                try
                {
                    if (__instance == null || ____currentPlan == null) return;
                    if (!CompanyLists.IsDisplayPlan(____currentPlan)) return;
                    if (_fEntries == null)
                        _fEntries = __instance.GetType().GetField("_destinationEntries",
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    var rows = _fEntries != null ? _fEntries.GetValue(__instance) as System.Collections.IList : null;
                    if (rows == null || rows.Count == 0) return;
                    var last = rows[rows.Count - 1] as UnityEngine.Component;
                    if (last != null) CompanyPlans.StripDragHandle(last.transform);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] destination row drag strip: {ex.Message}"); }
            }
        }

        /// <summary>HQ-PARITY-2 FOLD b1 — THE SECOND SEAM: ONE COMMIT POINT FOR EVERY PANE MUTATION.
        /// `SaveGameManager.MarkChange()` (SaveGameManager.cs:586) is what EVERY logistics control ends at,
        /// including the three that reach nothing else: the per-item target field's onEndEdit / plus / minus
        /// delegates (LogisticsManagerPlanUI.cs:387/:406/:424), AddDestination (:227-237, which calls
        /// AddDestinationEntry + Reinitialize + MarkChange and never LoadPlan) and UpdateSelectedBusiness
        /// (:566-575).  Before this fold a target typed on a partner's plan was never sent and reverted at the
        /// next fan-out, and a destination added but left unpicked simply vanished (review MAJOR-1/2).
        /// MarkChange fires on everything the game saves, so the gates are ordered cheapest first:
        /// (a) a merger is up and this machine is a member - one int compare, which is the whole cost off a
        /// session; (b) the logistics pane object this mod registered in the LoadPlan postfix above is alive
        /// and active in the hierarchy; (c) its `_currentPlan` (read live, LogisticsManagerPlanUI.cs:71) is a
        /// partner's copy - tagged, or (HQ-PARITY-7 R1) its id has a DTO from an owner whose overlay is not
        /// suspended here.  Only then does it cost a dictionary lookup and one JSON signature, and only
        /// while a PARTNER's logistics pane is open - and RouteDisplayPlanIfChanged is a no-op when the shape
        /// is unchanged (the `_lastSentPlan` early return), so a plain redraw sends nothing.</summary>
        [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.MarkChange))]
        public static class Patch_SaveGameMarkChange_LogisticsSeam
        {
            static void Postfix()
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || !MergerSync.IAmMember) return;   // (a)
                    var plan = CompanyPlans.PaneDisplayPlan();                           // (b) + (c)
                    if (plan != null) CompanyLists.RouteDisplayPlanIfChanged(plan, "logistics pane edit");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics pane commit: {ex.Message}"); }
            }
        }

        /// <summary>HQ-PARITY-2 P3 — THE ONE CONTROL THAT DOES NOT REACH LoadPlan.  `ChangeLogisticsManager`
        /// (LogisticsManagersPlanList.cs:193-200) writes `plan.assignedEmployeeId` and calls MarkChange, but
        /// never LoadPlan - so a manager changed on a PARTNER's row reached nobody until some later click
        /// re-routed the whole plan, and a manager changed on MY OWN row waited out the 30 s dirty cadence.
        /// Both now leave from here, on the same two paths every other logistics control takes.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList), "ChangeLogisticsManager")]
        public static class Patch_LogisticsManagerChange_MergerRoute
        {
            static void Postfix(Buildings.Office.Headquarters.LogisticsManagerPlan plan)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0) return;   // inert without a merger
                    if (CompanyLists.IsDisplayPlan(plan)) CompanyLists.RoutePlanEdit(plan, "logistics manager changed");
                    else if (CompanyLists.OwnPlanChanged(plan)) CompanyPlans.OwnEditCommitted("logistics manager changed");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] logistics manager route: {ex.Message}"); }
            }
        }

        /// <summary>WAVE 4 (V1 execution guard) — THE WHOLESALE DELIVERY PASS.
        /// `BusinessHelper.HandleWholesaleDeliveries()` (Helpers/BusinessHelper.cs:422, called from
        /// GameManager.cs:603) walks `SaveGameManager.Current.DeliveryContracts` whole
        /// (`foreach (DeliveryContract deliveryContract in SaveGameManager.Current.DeliveryContracts)`,
        /// :429) and has no ownership test of any kind — a tagged DISPLAY COPY would order goods, pay a
        /// delivery fee out of the shared wallet and land stock on a replica. There is no per-item hook in
        /// that loop, so the guard LIFTS the display copies out for the duration of the pass and puts them
        /// back in a Finalizer (which also runs when the native method throws).
        /// PREDICATE (r2, review MAJOR-1): lift a contract when `MergerAbsence.IsDisplayInstall(contract)` —
        /// the TAG STRING, not "did this machine install it" and not the address. IsTaggedInstall also
        /// answers true for the absence hand-over's installs, which are an absent owner's REAL contracts
        /// and are meant to run here; and the address test it used to be paired with let a display copy
        /// survive on an address that later became simulated (r2 MAJOR-2) and order the same goods twice.
        /// A display copy never runs, on any address, ever.
        /// Inert without a merger and whenever nothing is installed.</summary>
        [HarmonyPatch(typeof(BusinessHelper), nameof(BusinessHelper.HandleWholesaleDeliveries))]
        public static class Patch_WholesaleDeliveries_DisplayCopyGuard
        {
            private static readonly List<(int Index, Entities.DeliveryContract Item)> _lifted = new();

            static void Prefix()
            {
                _lifted.Clear();
                try
                {
                    var gi = SaveGameManager.Current;
                    if (gi == null || MergerAbsence.InstalledListItems.Count == 0) return;
                    var list = gi.DeliveryContracts;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var c = list[i];
                        if (c == null || !MergerAbsence.IsDisplayInstall(c)) continue;   // r2 MAJOR-1: the tag decides
                        _lifted.Add((i, c));
                        list.RemoveAt(i);
                    }
                    if (_lifted.Count > 0)
                        Plugin.Logger.LogInfo($"[CompanyLists] wholesale pass: {_lifted.Count} display copy contract(s) held out (they run on their operator).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] wholesale guard lift: {ex.Message}"); }
            }

            static void Finalizer()
            {
                try
                {
                    var gi = SaveGameManager.Current;
                    if (gi == null) { _lifted.Clear(); return; }
                    for (int i = _lifted.Count - 1; i >= 0; i--)
                    {
                        var (idx, item) = _lifted[i];
                        if (idx > gi.DeliveryContracts.Count) idx = gi.DeliveryContracts.Count;
                        gi.DeliveryContracts.Insert(idx, item);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] wholesale guard restore: {ex.Message}"); }
                finally { _lifted.Clear(); }
            }
        }

        /// <summary>WAVE 4 (V3) — ATTRIBUTION, BizMan Deliveries. `DeliveryContractEntry.Initialize(
        /// DeliveryContract contract, Action&lt;DeliveryContractEntry, DeliveryContract&gt; selectContract)`
        /// (:36) builds one contract row. `___businessNameText` = three underscores + the exact field name
        /// `businessNameText` (DeliveryContractEntry.cs:24 `private TMP_Text businessNameText;` — no leading
        /// underscore of its own). ONLY that label's colour is touched; the fee column keeps the game's own.
        /// Rows are pooled/instantiated per refresh, so the label's original colour is cached by instance id
        /// and restored for every row that is not a partner's.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.DeliveryContractEntry), "Initialize")]
        public static class Patch_DeliveryContractEntry_PartnerTint
        {
            private static readonly Dictionary<int, UnityEngine.Color> _home = new();

            /// <summary>r2 minor (e): the partner colour of each pooled row label, by label instance id. The
            /// row REPAINTS itself on selection (DeliveryContractEntry.SetSelected, :84/:90, black when
            /// selected and white when not), which wiped the attribution the moment a row was clicked.</summary>
            internal static readonly Dictionary<int, UnityEngine.Color> Tinted = new();

            static void Postfix(Entities.DeliveryContract contract, TMPro.TMP_Text ___businessNameText)
            {
                try
                {
                    if (contract == null || ___businessNameText == null) return;
                    int id = ___businessNameText.GetInstanceID();
                    if (!_home.TryGetValue(id, out var home)) { home = ___businessNameText.color; _home[id] = home; }
                    string key = ""; try { key = GameStateReader.AddressKey(contract.businessAddress); } catch { }
                    if (!MergerSync.IAmMember
                        || !CompanyLists.TryOwnerOfAddress(key, out var pid)
                        || !PlayerColours.TryColourFor(pid, out var c))
                    { Tinted.Remove(id); ___businessNameText.color = home; return; }
                    Tinted[id] = c;
                    ___businessNameText.color = c;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] contract row tint: {ex.Message}"); }
            }
        }

        /// <summary>WAVE 4 r2 (minor e) — THE SELECTION REPAINT. `public void SetSelected(bool isSelected)`
        /// (DeliveryContractEntry.cs:81) rewrites `businessNameText.color` on every selection change (:84
        /// black, :90 white), so a partner row lost its member colour as soon as it was clicked. The row's
        /// own colour is re-applied afterwards; the fee column keeps the game's own, exactly as at
        /// Initialize. Only rows the Initialize postfix actually tinted are touched.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.DeliveryContractEntry), "SetSelected")]
        public static class Patch_DeliveryContractEntry_TintOnSelect
        {
            static void Postfix(TMPro.TMP_Text ___businessNameText)
            {
                try
                {
                    if (___businessNameText == null || Patch_DeliveryContractEntry_PartnerTint.Tinted.Count == 0) return;
                    if (Patch_DeliveryContractEntry_PartnerTint.Tinted.TryGetValue(___businessNameText.GetInstanceID(), out var c))
                        ___businessNameText.color = c;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] contract row tint (selection): {ex.Message}"); }
            }
        }

        /// <summary>WAVE 4 (V3) — ATTRIBUTION, BizMan logistics plan list.
        /// `LogisticsManagersPlanListEntry.Initialize(LogisticsManagerPlan plan, Action&lt;...&gt; onSelected)`
        /// builds one plan row; `___locationNameNotSelectedLabel` / `___locationNameSelectedLabel` are three
        /// underscores + the exact field names (LogisticsManagersPlanListEntry.cs:25 and :31, neither has a
        /// leading underscore). The manager-name label is left alone. The row is a partner's when its plan's
        /// HEADQUARTERS belongs to a partner — which is exactly what a display copy is.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanListEntry), "Initialize")]
        public static class Patch_LogisticsPlanEntry_PartnerTint
        {
            private static readonly Dictionary<int, UnityEngine.Color> _home = new();

            static void Postfix(Buildings.Office.Headquarters.LogisticsManagerPlan plan,
                                TMPro.TMP_Text ___locationNameNotSelectedLabel,
                                TMPro.TMP_Text ___locationNameSelectedLabel)
            {
                try
                {
                    if (plan == null) return;
                    string key = ""; try { key = GameStateReader.AddressKey(plan.headquartersAddress); } catch { }
                    bool partner = MergerSync.IAmMember
                                && CompanyLists.TryOwnerOfAddress(key, out var pid)
                                && PlayerColours.TryColourFor(pid, out var c0);
                    UnityEngine.Color tint = default;
                    if (partner) { CompanyLists.TryOwnerOfAddress(key, out var pid2); PlayerColours.TryColourFor(pid2, out var c1); tint = c1; }
                    Paint(___locationNameNotSelectedLabel, partner, tint);
                    Paint(___locationNameSelectedLabel, partner, tint);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CompanyLists] plan row tint: {ex.Message}"); }
            }

            private static void Paint(TMPro.TMP_Text label, bool partner, UnityEngine.Color tint)
            {
                if (label == null) return;
                int id = label.GetInstanceID();
                if (!_home.TryGetValue(id, out var home)) { home = label.color; _home[id] = home; }
                label.color = partner ? tint : home;
            }
        }

        // Tick-chain containment wall (2026-07-07, ANTIPATTERNS Class 11): GameManager.NewDay and the
        // hourly section of RunMainGameTick each run a long ORDERED list of subsystem calls with no
        // per-step isolation — one throw silently kills every step after it (the payroll-runaway bug:
        // a CompetitionHelper NRE aborted the worked-hours reset, taxes and the market day, every
        // midnight). Native behavior on a throw is "abort the rest of the chain", so containing a step
        // is never worse for world state and always better for the chain. Every exception is logged IN
        // FULL — this is a loud containment wall, not a silent catch (the silent-catch rule stands).
        // Each step below is decompile-verified for EA 0.11 build 3540 (GameManager.cs :576-608 hourly,
        // :628-659 daily); an unresolvable step logs an error at patch time instead of failing silently
        // (ANTIPATTERNS Class 3). Event multicasts (GlobalEvents.onNewDay/onNewHour, GameEvent.Invoke)
        // are NOT wrappable this way and remain the chain's residual exposure.
        [HarmonyPatch]
        public static class Patch_GameTickChain_ContainThrows
        {
            private static readonly (string type, string method)[] Steps =
            {
                // ── NewDay chain, in call order ──
                ("Helpers.PlayerHelper",            "IncreasePlayerAge"),
                // ("Helpers.FinancialSummaryHelper", "SetMoneyBeforeMidnight") — DELETED in game 1.0
                //   (method gone AND NewDay no longer calls it). Nothing left to contain; the entry
                //   only produced a boot-time "UNPROTECTED" LogError. Removed 2026-08-29.
                ("UI.Notification.NotificationsListUI", "CleanOldNotifications"),
                ("Helpers.RealEstateHelper",        "RunDaily"),
                ("Helpers.EmployeeHelper",          "PayDailyWages"),
                ("Helpers.EmployeeHelper",          "WorkDaily"),
                ("Helpers.ParkingSimulator",        "RunDaily"),
                ("Helpers.BusinessHelper",          "RunDaily"),
                // NOT part of the NewDay chain above — listed here for proximity to its sibling.
                // 1.0-new. Veiled above for AUTHORITY; contained here for CRASHES - the two are
                // orthogonal and it wants both. It runs inside the GlobalEvents.onNewHour multicast
                // (BuildingManager.cs:285-291), which this wall cannot wrap as a whole, so an
                // uncontained throw here kills every onNewHour subscriber after it.
                ("Helpers.BusinessHelper",          "RestockCurrentBusinessIfNeeded"),
                ("Helpers.CompetitionHelper",       "RunDaily"),
                ("Helpers.ProductMarketHelper",     "RunDaily"),
                ("AdManager",                       "RunDaily"),
                ("Helpers.TaxHelper",               "RunDaily"),
                ("UI.DailySummary.DailySummary",    "Run"),
                ("Helpers.InvestmentFundHelper",    "RunDaily"),
                ("Helpers.EmployeeHelper",          "RunDaily"),
                ("BigAmbitions.Rivals.RivalsHelper","RunDaily"),
                // ── hourly chain (RunMainGameTick), in call order ──
                ("JobHelper",                       "RunHourly"),
                ("Helpers.ParkingSimulator",        "RunHourly"),
                ("Helpers.RecruitmentHelper",       "RunHourly"),
                ("Helpers.HappinessHelper",         "RunHourly"),
                ("Helpers.EmployeeHelper",          "RunHourly"),
                ("Helpers.ProductMarketHelper",     "GenerateShortagesAndBackorders"),
                ("Buildings.Office.Headquarters.LogisticsManagerHelper", "DoAllFactoryDeliveries"),
                ("Helpers.BusinessHelper",          "HandleWholesaleDeliveries"),
                ("Entities.ImportPartnership",      "DoAllDeliveries"),
                ("Buildings.Office.Headquarters.LogisticsManagerHelper", "DoAllWarehouseDeliveries"),
                ("Helpers.BusinessHelper",          "RunHourly"),
                ("Buildings.BuildingTypes.Shared.Dirtiness.BuildingCleanlinessHelper", "RunHourly"),
                ("Buildings.BuildingTypes.Special.FurnitureStore.FurnitureDeliveryHelper", "RunHourly"),
                ("Vehicles.VehicleDeliveryHelper",  "RunHourly"),
                // NEW IN GAME 1.0 (2026-08-29) — GameManager.cs :617, :641, :644. The wall silently
                // shrank relative to the chain when 1.0 added these; an unwrapped throw in any of
                // them aborts every hourly step after it.
                ("Buildings.Office.Headquarters.PricingManagerHelper",          "RunHourly"),
                ("Buildings.BuildingTypes.Special.FoodDelivery.FoodDeliveryHelper", "RunHourly"),
                ("Player.FoodDeliveryJob.FoodDeliveryJobHelper",                "RunHourly"),
                ("BigAmbitions.Rivals.RivalsHelper","RunHourly"),
                ("Extensions.GamePromptHelper",     "RunHourly"),
                // ("Helpers.BankHelper", "RunHourly") — the whole TYPE is gone in game 1.0 (banking
                //   overhaul). Nothing left to contain; removed 2026-08-29 (was a boot LogError).
                ("Entities.ContactsHelper",         "RunHourly"),
            };

            static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                // ALL overloads by name (see the veil's TargetMethods note — Method(name) nulls out
                // on overloaded names). Covers PortraitGenerator.Create's two overloads too.
                foreach (var (type, method) in Steps)
                    foreach (var m in ResolveAllByName(type, method, "Guard] tick-chain wall", "UNPROTECTED"))
                        yield return m;
                foreach (var m in ResolveAllByName("Character.Customization.PortraitGenerator", "Create", "Guard] tick-chain wall", "UNPROTECTED"))
                    yield return m;
            }

            /// <summary>Every non-abstract, non-generic declared method with this name — shared by the
            /// tick wall and the authority veil. Logs LOUD when a name resolves to nothing (Class 3).</summary>
            internal static IEnumerable<System.Reflection.MethodBase> ResolveAllByName(string type, string method, string tag, string consequence)
            {
                var t = AccessTools.TypeByName(type);
                int n = 0;
                if (t != null)
                    foreach (var m in AccessTools.GetDeclaredMethods(t))
                        if (m != null && m.Name == method && !m.IsAbstract && !m.ContainsGenericParameters) { n++; yield return m; }
                if (n == 0)
                    Plugin.Logger.LogError($"[{tag}: {type}.{method} not found (game update renamed it?) — that step is {consequence}.");
            }

            static Exception Finalizer(System.Reflection.MethodBase __originalMethod, Exception __exception)
            {
                if (__exception != null)
                    Plugin.Logger.LogError($"[Guard] {__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} threw during the " +
                                           $"game tick — contained so the rest of the hour/day chain still runs: {__exception}");
                return null;   // suppress — the chain continues
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
                if (!MPClient.IsClientInWorld) return true;
                ItemHelper.ClearPriceCaches();   // 1.0 renamed ClearLmpCache
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
                if (MPServer.IsRunning || MPClient.IsClientInWorld) __result = true;
            }
        }

        /// <summary>The set of goods item-names actually STOCKED on a shop's priced shelves — the single
        /// source of truth for "is this shelf stocked" (a PointOfSale/ShowcaseShelf item whose stock
        /// instance has amount &gt; 0). Used by GetShelfFillState (live, entered shops) AND the MPStockSync
        /// digest (un-entered shops), so the two can't drift.</summary>
        public static System.Collections.Generic.HashSet<string> StockedGoodsOf(BuildingRegistration reg)
        {
            var set = new System.Collections.Generic.HashSet<string>();
            try
            {
                var items = reg?.itemInstances;
                if (items == null) return set;
                foreach (var ii in items.Values)
                {
                    if (ii?.ItemCached == null) continue;
                    if ((ii.ItemCached.type & (BigAmbitions.Items.ItemType.PointOfSale | BigAmbitions.Items.ItemType.ShowcaseShelf)) == 0) continue;
                    var stock = ItemHelper.GetStockInstance(ii);
                    if (stock != null && !string.IsNullOrEmpty(stock.itemName) && stock.amount > 0) set.Add(stock.itemName);
                }
            }
            catch { }
            return set;
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                if (registration == null || !GameStatePatcher.IsAnyPlayerBusiness(registration)) return;
                try
                {
                    string addr = GameStateReader.AddressKey(registration);
                    if (GameStatePatcher.IsReplicatedInterior(addr))
                        __result = StockedGoodsOf(registration).Contains(itemName) ? 1f : 0f;   // live: this shop's interior is loaded here
                    else if (MPStockSync.HasDigest(addr))
                        __result = MPStockSync.IsStocked(addr, itemName) ? 1f : 0f;              // un-entered: owner's last-known stock digest
                    // else: no live data AND no digest → leave the game's native value (fail open)
                }
                catch { /* on any doubt leave the game's value — fail open, never over-suppress competition */ }
            }
        }

        // ── Round-102i: clients never compute product demand themselves ───────
        // A client's provider counts are empty (it skips the AI-economy pass that fills them),
        // so the native formula — 100 minus a share based on competitors — collapses to 99-100%
        // for EVERY product. Demand is not cosmetic: it feeds the customer-traffic calculators,
        // so a client running on its own numbers gets inflated traffic for its own shops on top
        // of a wrong Market Insider. The host's values are authoritative and now arrive reliably
        // (real-time cadence, round-102h) and completely (add-if-missing, round-102i).
        // Gated on HaveAuthoritativeDemand so the FIRST computation still runs and creates the
        // rows — suppressing before any host payload has landed would leave the client with no
        // demand rows at all (the native getter would log an error and substitute 50% per call).
        [HarmonyPatch(typeof(Helpers.ProductMarketHelper), nameof(Helpers.ProductMarketHelper.UpdateMarketDemands))]
        public static class Patch_UpdateMarketDemands_HostAuthoritativeOnClient
        {
            static bool Prefix()
            {
                if (MPServer.IsRunning || !MPClient.IsConnected) return true;    // host / SP → native
                return !GameStatePatcher.HaveAuthoritativeDemand;                // client → only before host data exists
            }
        }

        [HarmonyPatch(typeof(Helpers.ProductMarketHelper), nameof(Helpers.ProductMarketHelper.UpdateMarketDemand))]
        public static class Patch_UpdateMarketDemand_HostAuthoritativeOnClient
        {
            static bool Prefix()
            {
                if (MPServer.IsRunning || !MPClient.IsConnected) return true;
                return !GameStatePatcher.HaveAuthoritativeDemand;
            }
        }

        // ── Round-102: the monopoly flag is HOST-STAMPED, per player ──────────
        // NeighborhoodDemand.RecalculateIfPlayerHasMonopoly decides "am I the only seller here?"
        // by walking registrations and reading each one's cachedAvailableProducts. In MP that
        // input is unreliable on BOTH machines — clients hold no product cache for AI shops
        // (measured 242/242, round-101) and the host holds none for PARTNER shops — so the walk
        // can find no rival seller and OVER-GRANT the monopoly, worth +30% customer price
        // tolerance and +30% on the optimal-price index. The authoritative value now arrives
        // stamped with its OWNER in the market snapshot (GameStateReader.BuildMonopolyOwners),
        // so in an MP session the native recompute must not overwrite it. Single-player and the
        // menu are untouched — this is a pass-through there.
        [HarmonyPatch(typeof(Entities.NeighborhoodDemand), nameof(Entities.NeighborhoodDemand.RecalculateIfPlayerHasMonopoly))]
        public static class Patch_NeighborhoodDemand_MonopolyIsHostStamped
        {
            static bool Prefix()
            {
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;   // SP → native
                return false;                                                   // MP → keep the stamped value
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
                // GAME-PATCH-0916: ApplyFilters has TWO overloads now — the public no-arg pass starter
                // (decompile CityMapFilters.cs:499) and a private per-building ApplyFilters(CityBuildingController)
                // (:567) the deferred work queue pumps. A name-only lookup threw AmbiguousMatchException and this
                // whole patch class failed at startup (host log 2026-09-16 12:49). The dump wants the pass
                // starter, so the empty argument list pins it.
                return t.GetMethod("ApplyFilters",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
            }

            // Round-98 gates (user-approved; the dump measured ~53ms/call): only when a human
            // is actually LOOKING at the map — our own sync code re-runs ApplyFilters with the
            // map CLOSED (GameStatePatcher/MergerFlip pin refreshes) and the dump landed as a
            // mid-drive hitch, in SP too. MP-only (it diagnoses cross-machine state) and at
            // most one dump per 60s (was 2s).
            private static float _lastDumpAt = -100f;
            static void Prefix(object __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return;
                    var comp = __instance as UnityEngine.Component;
                    if (comp == null || !comp.gameObject.activeInHierarchy) return;   // map UI not visible
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (now - _lastDumpAt < 60f) return;
                    _lastDumpAt = now;

                    long _pc = MPPerf.Begin();   // round-97: 826-reg ×3-pass dump inside a native call — patch-cost bracketed
                    try
                    {
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
                    finally { MPPerf.PatchEnd("ApplyFiltersDump", _pc); }
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return __exception;
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                MPHubNativePage.HidePage();
            }
        }

        // ── Patch: FullMenu.Update — keep the phone's own clock/date live while the world runs (field 20260901-202027) ──
        // FullMenu paints timeLabel/dateLabel ONCE in Toggle(show:true) (:187) — vanilla pauses the world while the
        // menu is open, so a snapshot is exact there. MP keeps the world running ("nothing pauses outside the vote"),
        // so the menu clock froze at the moment it opened — glaring behind a running skip. Same family as the
        // round-27 ambience restore: a native screen built around the menu pause, left stale by our suppression.
        // Refresh the time label when the game minute changes and the date label when the day changes, with the
        // game's own calls (GetCurrentFormattedTime exists on both builds; SetCurrentFormattedTime is update-only).
        // dateLabel is a Localizor TextLocalizationComponent (HGPlugins IS referenced — review #3 MAJOR-4 corrected
        // an earlier "not referenced" comment); its Arguments property is set through the TYPED component with the
        // same anonymous shape the game builds at open, so a future rename breaks the build instead of silently
        // no-op'ing. User-approved 2026-09-01.
        [HarmonyPatch(typeof(UI.Smartphone.FullMenu), "Update")]
        public static class Patch_FullMenu_Update_LiveClock
        {
            private static System.Reflection.FieldInfo? _fTime, _fDate;
            private static bool _resolved, _warned;
            private static int _lastMinute = -1, _lastDay = -1;

            static void Postfix(UI.Smartphone.FullMenu __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;
                    if (!UI.Smartphone.FullMenu.IsOpen) { _lastMinute = -1; _lastDay = -1; return; }   // re-paint on the next open
                    var gi = SaveGameManager.Current;
                    if (gi == null) return;
                    if (!_resolved)
                    {
                        _resolved = true;
                        _fTime = AccessTools.Field(typeof(UI.Smartphone.FullMenu), "timeLabel");
                        _fDate = AccessTools.Field(typeof(UI.Smartphone.FullMenu), "dateLabel");
                        Plugin.Logger.LogInfo($"[Clock] FullMenu live clock: timeLabel={(_fTime != null ? "ok" : "NOT FOUND")} dateLabel={(_fDate != null ? "ok" : "NOT FOUND")}.");
                    }
                    int minute = gi.Hour * 60 + (int)gi.Minute;
                    if (minute != _lastMinute && _fTime?.GetValue(__instance) is TMPro.TMP_Text tl)
                    {
                        tl.text = TimeHelper.GetCurrentFormattedTime();
                        _lastMinute = minute;
                    }
                    if (gi.Day != _lastDay)
                    {
                        if (_fDate?.GetValue(__instance) is Localizor.LanguageChangeEvent.TextLocalizationComponent dl)
                            dl.Arguments = new { DayOfWeek = "common_" + TimeHelper.GetDayOfWeek(), CurrentNumberDay = gi.Day };
                        _lastDay = gi.Day;
                    }
                }
                catch (Exception ex)
                {
                    if (!_warned) { _warned = true; Plugin.Logger.LogWarning($"[Clock] FullMenu live clock: {ex.GetType().Name}: {ex.Message}"); }
                }
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
                // Round-199 diagnostic: isTimeControlDisabled has exactly ONE writer
                // (this method — read from GameSpeedController.cs), yet a field client
                // (20260730-235612) had the lock set 13× with the watchdog cleaning up.
                // Both ALLOWED paths below can arm it: before the MP gate flips during
                // load, and inside our own sanctioned-pause window (native onPause
                // handlers run while we hold the key).  Record WHO on every set that
                // goes through, so the watchdog's clear line names the grabber.
                if (!MPServer.IsRunning && !MPClient.InMpGame)
                {
                    if (disabled) GameStateReader.LastTimeLockTag = $"{Caller()} [outside MP gate]";
                    return true;   // sticky gate: survives reconnect
                }
                if (GameStateReader.AllowNativePauseCall)
                {
                    if (disabled) GameStateReader.LastTimeLockTag = $"{Caller()} [inside sanctioned-pause window]";
                    return true;
                }
                if (!disabled) return true;                  // re-enabling is always fine
                if (_n++ < 5 || _n % 100 == 0)
                    Plugin.Logger.LogInfo($"[Pause] time-control lock suppressed (#{_n}) — caller {Caller()}.");
                return false;
            }

            /// <summary>Nearest native caller: skips our own frames, Harmony plumbing
            /// (dynamic-method wrappers have a null DeclaringType), and the patched
            /// method itself.</summary>
            private static string Caller()
            {
                try
                {
                    var st = new System.Diagnostics.StackTrace(1, false);
                    for (int i = 0; i < st.FrameCount; i++)
                    {
                        var m = st.GetFrame(i)?.GetMethod();
                        var dt = m?.DeclaringType;
                        if (dt == null) continue;
                        if (dt.Assembly == typeof(Patch_GSC_DisableTimeControl_Suppress).Assembly) continue;
                        string full = dt.FullName ?? "";
                        if (full.StartsWith("HarmonyLib") || full.StartsWith("MonoMod")) continue;
                        if (m!.Name == "DisableTimeControl") continue;
                        return $"{dt.Name}.{m.Name}";
                    }
                }
                catch { }
                return "<unknown>";
            }
        }

        // ── Patch: menu-close ambience restore (round-27 — THE ambient-sound fix) ─────────────────────
        // Menus (FullMenu:208, MiniMenu:259, Feedback:88) duck city ambience DIRECTLY on open via
        // SfxManager.SetSoundSnapshotCityMap(true) — unconditional (:344). On close they call it with
        // false, whose restore branch only runs when the CITY MAP is open (:346-349, the back-to-map
        // case); closing a menu anywhere else is a native NO-OP. Vanilla still restores because those
        // menus also pause/unpause, and the close-side unpause fires onPause(false) →
        // SetSoundSnapshot(false) — the exact pipeline our MP design suppresses ("nothing pauses outside
        // the vote"). So in MP: duck always ran, restore never did — city ambience dead until a manual
        // pause+unpause (TogglePause's own direct restore, GameSpeedController:81). Round-26 guarded the
        // event pipeline; this supplies the missing HALF of the direct pair: on a no-map menu close, run
        // the restore vanilla's unpause would have done (SetSoundSnapshot picks indoor/outdoor itself).
        [HarmonyPatch(typeof(SfxManager), nameof(SfxManager.SetSoundSnapshotCityMap))]
        public static class Patch_Sfx_MenuCloseRestore
        {
            static void Postfix(SfxManager __instance, bool onlyUISound, float transitionTime)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return;   // sticky gate: survives reconnect
                    if (onlyUISound) return;                                 // menu OPEN duck — native, correct
                    if (CityMap.IsOpen && !UI.BuildingPreview.isPreviewing) return;   // native branch restored map ambience
                    // A REAL sanctioned pause keeps the duck, exactly like vanilla's paused state would.
                    if (TimeSync.ManualPaused || TimeSync.IsStartupHeld) return;
                    __instance.SetSoundSnapshot(false, transitionTime);
                    Plugin.Logger.LogInfo("[Sfx] menu-close ambience restore (MP suppresses the pause pipeline vanilla used for this).");
                    // Bug 235855 leg B (user-approved 2026-09-01). Two roles: (1) a second-chance clear of the
                    // speaker pause flag a placement armed (also covers a leg-A miss when CancelPlacementMode
                    // threw); (2) the one menu arm that still exists in MP — an un-pause evaluated while
                    // FullMenu.IsOpen (a sanctioned unpause or a LocalFiles song reload with the menu up).
                    // Opening a menu does NOT arm it in MP: FullMenu's own gameSpeed.SetPause(true)
                    // (FullMenu:240) is suppressed above. Callers of this hook (1.0 update 2026-09-01):
                    // FullMenu :218 IsOpen=show → :232, MiniMenu :251 → :252, Feedback :87-89 — each writes
                    // IsOpen BEFORE calling, so the live re-read inside SetPause sees the menu closed. Runs
                    // last so it can never cost the restore line above.
                    MPRadioSync.ReconcileSpeakerPause("menu close");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Sfx] menu-close restore: {ex.Message}"); }
            }
        }

        // ── Patch: SetPauseState-level suppression (round-26 — the ambient-sound fix) ─────────────────
        // The SetPause/DisableTimeControl suppressions above stop the MENU pause calls, but the game has
        // callers that BYPASS SetPause entirely: TimeMachine starts a skip via gameSpeed.Set(paused:true)
        // directly (TimeMachine.cs:126), and Reset() re-dispatches whenever its state compare sees a
        // mismatch — which our suppression itself desyncs. Any such path reaching the private
        // SetPauseState fires GlobalEvents.onPause(true) → SfxManager transitions to the UI-only mixer
        // snapshot (ambience MUTED, SfxManager:176→322) + AudioZones Pause() their sources — while our
        // timescale monitor keeps the game visibly running. The balanced onPause(false) then never comes
        // (the close-side Reset sees "no change"), so ambience stays dead until a manual pause+unpause —
        // whose TogglePause also restores the snapshot DIRECTLY (GameSpeedController:81), which is exactly
        // the user's observed workaround (2026-07-04). Gate the dispatch itself: in MP, a paused=true
        // SetPauseState runs ONLY for our sanctioned pauses (AllowNativePauseCall — the vote system and
        // the startup hold both drive TogglePause under that key). paused=false ALWAYS passes, so an
        // already-stuck duck self-heals at the next unpause opportunity.
        [HarmonyPatch]
        public static class Patch_GSC_SetPauseState_Suppress
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var t = VehicleManager.FindGameType("GameSpeedController");
                var m = t?.GetMethod("SetPauseState", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Plugin.Logger.LogInfo($"[Pause] SetPauseState suppression: {(m != null ? "patched" : "NOT FOUND")}");
                return m;
            }

            private static int _n;
            static bool Prefix(GameSpeed newGameSpeed)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.InMpGame) return true;   // sticky gate: survives reconnect
                    if (GameStateReader.AllowNativePauseCall) return true;        // sanctioned pause → real duck, like vanilla
                    if (newGameSpeed == null || !newGameSpeed.paused) return true; // the un-pause dispatch (audio RESTORE) always runs
                    if (_n++ < 8 || _n % 100 == 0)
                    {
                        // DIAG: name the caller so the log converts "which menu path pulls the trigger"
                        // from inference to fact (skip Harmony trampoline frames — best-effort).
                        string trig = "?";
                        try
                        {
                            var st = new System.Diagnostics.StackTrace(false);
                            for (int i = 2; i < st.FrameCount && i < 8; i++)
                            {
                                var mth = st.GetFrame(i)?.GetMethod();
                                var tn  = mth?.DeclaringType?.Name ?? "";
                                if (tn.Length == 0 || tn.Contains("Patch") || tn.Contains("Harmony") || tn.Contains("MonoMethod")) continue;
                                trig = $"{tn}.{mth?.Name}";
                                break;
                            }
                        }
                        catch { }
                        Plugin.Logger.LogInfo($"[Pause] SetPauseState(paused) suppressed (#{_n}) — ambience duck blocked; trigger≈{trig}.");
                    }
                    return false;
                }
                catch { return true; }
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
                    // TrulyMine, not the raw flag: a merger-flipped partner shop must CONSUME the
                    // owner's synced open truth, not locally re-derive it (run-4: P1's local "close"
                    // of P2's shop diverged silently — the owner stays the open-state authority).
                    if (MergerFlip.TrulyMine(buildingRegistration)) return true;   // my own shop — I'm the authority
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

        // ── Round-51: clients never manufacture market/AI-shop state ──────────
        // PROBE-RESOLVED (dual-instance run 2026-07-21, 50 hits, stack-confirmed): the Phase-1b
        // suppression kept CityGenerator.PopulateBuildings for the registration skeleton — but
        // PopulateBuildings ALSO rolls the "ideal empty percentage" rental market
        // (MarkBuildingsForRentAndGetAvailableOnes → SetBuildingForRent) with the CLIENT'S OWN
        // dice at every scene load (CityManager.OnScenesLoaded → InitializeCity). A client thus
        // marks buildings empty+rentable that the host filled with AI shops — the FORMATION
        // MECHANISM of the round-50 zombie/wrongful-rent class (field: 18 retail + 11 office
        // rival shops flipped within minutes of a 0.1.11 join). The client's market is fully
        // host-derived (BusinessSync carries AvailableForRent; the apply writes it), so both
        // native "empty+rentable" producers are simply dead code on a client. Counted, quiet.
        internal static class ShopClearSuppress
        {
            private static int _n;
            internal static bool Skip(string via)
            {
                if (!MPClient.IsClientInWorld || MPServer.IsRunning) return false;   // SP + host: native
                _n++;
                if (_n == 1 || _n % 25 == 0)
                    Plugin.Logger.LogInfo($"[Suppress] {via} skipped on client (#{_n}) — market/AI-shop state is host-derived (round-51).");
                return true;
            }
        }

        [HarmonyPatch(typeof(BusinessHelper), nameof(BusinessHelper.SetBuildingForRent))]
        public static class Patch_SetBuildingForRent_SkipOnClient
        {
            static bool Prefix() => !ShopClearSuppress.Skip("SetBuildingForRent");
        }

        [HarmonyPatch(typeof(BuildingRegistration), nameof(BuildingRegistration.ShutDownAIBusiness))]
        public static class Patch_ShutDownAIBusiness_SkipOnClient
        {
            static bool Prefix() => !ShopClearSuppress.Skip("ShutDownAIBusiness");
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
                    if (reg == null || MergerFlip.TrulyMine(reg)) return;   // my own shop shouldn't read closed
                    string addr = GameStateReader.AddressKey(reg);
                    if (string.IsNullOrEmpty(addr)) return;
                    float now = UnityEngine.Time.unscaledTime;
                    if (_nextLogAt.TryGetValue(addr, out var due) && now < due) return;
                    _nextLogAt[addr] = now + 10f;                           // throttle per shop

                    int ownerState = BusinessSync.OwnerOpenByAddress.TryGetValue(addr, out var os) ? os : 0;
                    int dayCount   = reg.scheduleDays?.Count ?? -1;
                    var today      = BuildingHelper.GetTodaySchedule(reg);
                    int hourNow    = SaveGameManager.Current.Hour;
                    string todayDesc;
                    bool explained = ownerState == 2 || reg.temporarilyClosed;   // positively closed
                    // Vacant shells deny naturally (field 2026-07-16: ~30 lines of
                    // door-trying on no-name/no-schedule buildings) — explained.
                    if (string.IsNullOrEmpty(reg.BusinessName)) explained = true;
                    if (today == null) todayDesc = "today=NULL";
                    else
                    {
                        string slots = "";
                        bool inSlot = false;
                        if (today.openingHourSlots != null)
                            foreach (var s in today.openingHourSlots)
                            {
                                slots += $"[{s.startingHour}-{s.endingHour}]";
                                if (hourNow >= s.startingHour && hourNow < s.endingHour) inSlot = true;
                            }
                        if (!today.isOpen || !inSlot) explained = true;          // schedule says closed right now
                        todayDesc = $"today.isOpen={today.isOpen} slots={(slots.Length == 0 ? "none" : slots)}";
                    }
                    // Anomaly gate (2026-07-09, RED ROC report review): ~170 lines in one report were all
                    // legitimate after-hours denials — the diag's own fields contained the explanation.
                    // A denial the local evidence EXPLAINS is native behavior; only a denial that
                    // contradicts the evidence (apparently open, yet refused) is the June-19 sync class
                    // this diagnostic exists to catch. Log ONLY the contradiction.
                    if (explained) return;
                    Plugin.Logger.LogWarning($"[ShopClosedDiag] DENIED-WHILE-APPARENTLY-OPEN '{addr}' biz='{reg.BusinessName}' ownerState={ownerState}(0=unk/1=open/2=closed) temporarilyClosed={reg.temporarilyClosed} scheduleDays={dayCount} {todayDesc} now=h{hourNow}/{TimeHelper.GetDayOfWeek()} bizOwnerRival='{reg.businessOwnerRivalId}'");
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return __exception;
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
        //
        // T5 (2026-09-18, H-LOCALSTOP-1): the old rule "on a pure CLIENT all local Gley traffic is suppressed, so
        // every sensor call here belongs to a pre-clear transient" has been FALSE since TRAFFIC-APART. A client now
        // runs REAL Gley cars in two ordinary situations — its own ambient traffic in LOCAL mode, and the fade
        // leftovers it still holds while already in ghost mode — and a brain whose OnTriggerEnter / OnTriggerExit /
        // NewColliderHit never runs brakes for nothing at all. Those two cases are exempt now, next to the client's
        // own service cars, in the same shape the sibling client patches use (Patch_TM_UpdateSkip, the density
        // clamp, Patch_IM_UpdateIntersections_ClientSkip all carry the ClientRunsLocalTraffic exemption).
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

            // THE RULE. A car whose brain this machine really runs keeps its sensors; anything else on a pure
            // client is a pre-clear transient touching one of our ghosts, and the handler is skipped so the NRE is
            // never THROWN (the Finalizer below only catches it after the full stack unwind — ~7,500×/session on
            // the physics path). The HOST always keeps the real handler: its live traffic needs the sensors, and
            // the Finalizer covers the low-volume real-traffic-vs-ghost contacts.
            //   (a) LOCAL mode — this client runs its OWN ambient traffic (TRAFFIC-APART P5);
            //   (b) fade LEFTOVERS — real Gley cars it still holds while already in ghost mode, taken from the very
            //       registry T1 publishes from (one hash lookup, rebuilt every 0.2 s beat, no scene sweep);
            //   (c) its own SERVICE cars (2026-09-02) — they brake for traffic ghosts through the playerLayers
            //       branch, and that arm alone stays gated by ClientServiceSimEnabled, because that flag is what
            //       turns the service-car sim on; (a) and (b) are ordinary Gley ambient traffic whose existence
            //       does not depend on it, exactly as Patch_TM_UpdateSkip lets Gley tick in local mode "whatever
            //       the service-sim flag says".
            // The sensed-layer guard covers all three: with no sensed layer nothing here is sensed anyway
            // (ghosts stay on AiVehicles), so the shield stays closed. With one, every CLONED traffic ghost,
            // look-alike and stand-in is relayered onto it unconditionally (TrafficSync.SpawnTrafficGhost /
            // ApplyStandIn - review HIGH-1); the two SpawnVisualGhost fallbacks in TrafficSync.SpawnTrafficGhost are
            // native player-vehicle bodies (never on AiVehicles), so they too stay off the same-layer branch. So an
            // open arm meets ghosts only on the playerLayers branch, which never dereferences attachedRigidbody;
            // the Finalizer stays as the net for anything that slips through.
            static bool Prefix(VehicleComponent __instance)
            {
                if (!(MPClient.IsClientInWorld && !MPServer.IsRunning)) return true;   // host / single player: real handler
                if (TrafficSync.ServiceColliderLayer() < 0) { TrafficSync.CountSensorSkip(); return false; }
                if (TrafficSync.ClientRunsLocalTraffic) return true;                                        // (a)
                if (TrafficSync.IsPublishedLeftover(__instance)) return true;                               // (b)
                if (TrafficSync.ClientServiceSimEnabled && __instance != null
                    && ServiceCars.IsClientKept(__instance.gameObject)) return true;                        // (c)
                TrafficSync.CountSensorSkip();
                return false;
            }

            private static int _swallowed;
            private static int _detailed;

            // N1 (2026-09-18, H-LEFTOVERNRE-1, LOG ONLY): the line used to end "— ghost contact", which was a
            // GUESS — rig T-TRAFFIC-APART-20260918-190014 caught 2 swallows on a client during the ghost-mode
            // fade with every traffic ghost already on the sensed layer, so nothing proved the other side was a
            // ghost at all. The words are gone and the first 10 swallows per session now say WHAT was touched.
            // All three patched methods take the collider as `other` (GleyTrafficSystem/VehicleComponent.cs:417
            // OnTriggerEnter, :460 NewColliderHit, :469 OnTriggerExit), so Harmony injects it by name.
            // Counter cadence unchanged; budget 10 detail lines per session; nothing here runs per frame.
            static Exception? Finalizer(Exception __exception, VehicleComponent __instance, UnityEngine.Collider other)
            {
                if (__exception == null) return null;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return __exception;
                _swallowed++;
                string detail = "";
                if (_detailed < 10)
                {
                    _detailed++;
                    try { detail = SwallowDetail(__exception, __instance, other); }
                    catch (Exception ex2) { detail = " | detail unavailable: " + ex2.Message; }
                }
                if (_swallowed <= 5 || _swallowed % 500 == 0)
                    Plugin.Logger.LogWarning($"[Gley] swallowed {__exception.GetType().Name} in traffic collider handler (#{_swallowed}).{detail}");
                else if (detail.Length > 0)
                    Plugin.Logger.LogWarning($"[Gley] swallowed {__exception.GetType().Name} in traffic collider handler (#{_swallowed}) — detail only.{detail}");
                return null;
            }

            /// <summary>N1 probe text: the other collider (name, layer, flags, root, whether it is one of OUR
            /// ghost bodies or another Gley car), what the receiving car is to THIS machine, and the exception's
            /// first stack frame. Read-only — no scene sweep, only component lookups on the two objects in hand.</summary>
            private static string SwallowDetail(Exception ex, VehicleComponent inst, UnityEngine.Collider other)
            {
                string o;
                if (other == null) o = "other=NULL (no collider argument reached the handler)";
                else
                {
                    var go = other.gameObject;
                    int layer = go != null ? go.layer : -1;
                    string layerName = "";
                    try { layerName = layer >= 0 ? UnityEngine.LayerMask.LayerToName(layer) : "?"; } catch { layerName = "?"; }
                    UnityEngine.Transform? root = go != null ? go.transform.root : null;
                    bool modGhost = go != null && go.GetComponentInParent<ModGhostMarker>() != null;
                    VehicleComponent? vc = go != null ? go.GetComponentInParent<VehicleComponent>() : null;
                    o = $"other='{other.name}' layer='{layerName}'({layer}) isTrigger={other.isTrigger} "
                      + $"enabled={other.enabled} activeInHierarchy={(go != null && go.activeInHierarchy)} "
                      + $"rigidbody={(other.attachedRigidbody == null ? "null" : "present")} "
                      + $"root='{(root != null ? root.name : "?")}' modGhost={modGhost} "
                      + $"gleyCar={(vc == null ? "no" : "yes(active=" + vc.gameObject.activeInHierarchy + ")")}";
                }
                string frame = "";
                try
                {
                    string st = ex.StackTrace ?? "";
                    int nl = st.IndexOf('\n');
                    frame = (nl > 0 ? st.Substring(0, nl) : st).Trim();
                }
                catch { }
                bool leftover = false;
                try { leftover = TrafficSync.IsPublishedLeftover(inst); } catch { }
                return $" | inst='{(inst == null ? "null" : inst.name)}' publishedLeftover={leftover} "
                     + $"clientLocalTraffic={TrafficSync.ClientRunsLocalTraffic} | {o} | first frame: {frame}";
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

        // ── Complaint message builder NRE shield — RETIRED 2026-09-02 (game Build 3674) ─────────────
        // Patch_Complaint_MessageData_RivalPoorShield (2026-07-09) swallowed the NRE from
        // Complaint.GetComplaintMessageData dereferencing a null rival in rival-poor worlds. The 2026-09-02 game
        // update removed that method: Complaint.GetComplaintMessage now adds "rivalName" only when a rival was
        // found and returns a dedicated "_no_rival" message type + hasRival flag (AI.Employees/Complaint.cs,
        // Build 3674). The native bug is fixed at its source, so the shield is gone rather than retargeted —
        // it would otherwise fail to bind on every launch and raise the player-visible "hook failed" notice.

        // ── Rival-poor shield #2 (2026-07-09 audit): GetRandomRivalForBuilding ────────────────────
        // Same class as the complaint NRE: `GetNonSpecialRivals().GetRandom().id` with no empty-list
        // protection — called at RUNTIME by the host's daily real-estate rotation (RealEstateHelper
        // :108/:126 assign a rival owner) and by load-time compatibility fixes. In a rival-poor world
        // (degraded saves; players register as SPECIAL rivals) the pick NREs and the daily rotation
        // dies. An empty rival id is the benign native state ("no landlord"), so fail to that.
        [HarmonyPatch(typeof(BigAmbitions.Rivals.RivalsHelper), nameof(BigAmbitions.Rivals.RivalsHelper.GetRandomRivalForBuilding))]
        public static class Patch_RivalsHelper_GetRandomRivalForBuilding_RivalPoorShield
        {
            private static int _shielded;

            static Exception? Finalizer(Exception __exception, ref string __result)
            {
                if (__exception == null) return null;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return __exception;   // SP: vanilla behavior; H-FORK-1: holds in the offline fork
                __result = "";
                _shielded++;
                if (_shielded <= 3 || _shielded % 100 == 0)
                    Plugin.Logger.LogWarning($"[Guard] GetRandomRivalForBuilding threw ({__exception.GetType().Name}) — rival-poor world? Returned no-owner (#{_shielded}).");
                return null;
            }
        }

        // ── MyEmployees: hide injected partner-staff records (RED ROC report, 2026-07-09) ─────────
        // The roster sync injects OTHER players' employees into gi.EmployeeInstances so the game's own
        // staffing engine can man their stations in visited shops — but the MyEmployees app lists that
        // same collection, so a partner's whole workforce appeared under "My Employees" ("many of my
        // friend's customer service employees are showing"). Filter injected records out of the APP's
        // list only (the staffing engine keeps seeing them) — unless a MERGER makes them ours (slice 5).
        [HarmonyPatch(typeof(UI.Smartphone.Apps.MyEmployees.EmployeeScrollerController), "PopulateAllModels")]
        public static class Patch_MyEmployees_HideInjectedStaff
        {
            static void Postfix(System.Collections.Generic.List<UI.Smartphone.Apps.MyEmployees.EmployeeModel> allModels)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // H-FORK-1: holds in the offline fork
                    int removed = allModels.RemoveAll(m =>
                        m != null && MPRegisterSync.IsInjectedStaff(m.employeeId)
                                  && !MPRegisterSync.IsInjectedFromMergedPartner(m.employeeId)
                                  && !SharedShopStaff.IsFromGrantOwner(m.employeeId));   // shared-shop slice 3: the owner's people stay (tinted)
                    if (removed > 0)
                        Plugin.Logger.LogInfo($"[StaffRoster] MyEmployees: hid {removed} injected partner-staff record(s) (display only).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] MyEmployees filter: {ex.Message}"); }
            }
        }

        // ── Round-263: hide duty synthetics from the SCHEDULE surface ─────────
        // Field 20260811-224028: the BizMan schedule was the one staff surface
        // never filtered (MyEmployees, global queries, asset lists all are) — the
        // owner saw an editable "On-Duty Staff" row, fought our inject/remove
        // cycle, and the editor's drag state desynced. Three chokepoints, duty
        // synthetics (BAMP_DUTY_) ONLY — a merged partner's REAL staff must stay
        // visible and schedulable (merger map §20):
        //   (1) FetchEmployees      — strip the row LIST; EmployeesById KEEPS the
        //       synthetic (GetEmployeeColor etc. index the dict — removal = KeyNotFound).
        //   (2) GetEmployeesForDay  — strip day rows (rows derive from SHIFTS, not
        //       the list, so (1) alone leaves the row).
        //   (3) GetWorkShiftsByWorkstationId — return a FILTERED COPY (the dict
        //       holds the live list — never mutate it): the placement math's
        //       overlap walk (CalculateEndingHour :536) then ignores the
        //       synthetic's 0-24 till shift, so the owner can schedule real staff
        //       onto a till a player currently mans — WITHOUT this half, hiding
        //       the row makes that till an invisibly-blocked trap (user concern,
        //       confirmed in code).
        private static bool IsDutySyntheticId(string? id) =>
            !string.IsNullOrEmpty(id) && id!.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal);

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper),
                      nameof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.FetchEmployees))]
        public static class Patch_ScheduleFetchEmployees_HideDutySynthetics
        {
            private static int _n;
            static void Postfix()
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // H-FORK-1: holds in the offline fork
                    int removed = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.Employees?.RemoveAll(e => IsDutySyntheticId(e?.id)) ?? 0;
                    if (removed > 0 && _n++ < 4)
                        Plugin.Logger.LogInfo($"[StaffRoster] schedule: hid {removed} duty synthetic(s) from the employee list (round-263).");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper),
                      nameof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.GetEmployeesForDay))]
        public static class Patch_ScheduleEmployeesForDay_HideDutySynthetics
        {
            static void Postfix(System.Collections.Generic.List<Entities.EmployeeInstance> __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // H-FORK-1: holds in the offline fork
                    __result?.RemoveAll(e => IsDutySyntheticId(e?.id));   // fresh LINQ list — safe to mutate
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper),
                      nameof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.GetWorkShiftsByWorkstationId))]
        public static class Patch_ScheduleWorkstationShifts_IgnoreDutySynthetics
        {
            static void Postfix(ref System.Collections.Generic.List<WorkShift> __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // H-FORK-1: holds in the offline fork
                    if (__result == null || __result.Count == 0) return;
                    bool any = false;
                    foreach (var s in __result) if (IsDutySyntheticId(s?.employeeId)) { any = true; break; }
                    if (!any) return;
                    var filtered = new System.Collections.Generic.List<WorkShift>(__result.Count);
                    foreach (var s in __result) if (!IsDutySyntheticId(s?.employeeId)) filtered.Add(s);
                    __result = filtered;   // COPY — the dict's live list stays intact
                }
                catch { }
            }
        }

        // ── Global employee queries hide injected partner staff (round-80) ────
        // cdawg 20260724-165131: the partner's PURCHASING AGENTS appeared in the
        // import dialog / HQ specialist pages. Full survey of the query API
        // (EmployeeHelper.GetEmployeeInstances(queryInfo)):
        //   ADDRESS-SCOPED queries — security-guard strength, per-shop staffing/
        //   schedules — are exactly what the round-30 injection exists to serve:
        //   NEVER filtered (and your own shop's address can't match a partner
        //   record anyway).
        //   GLOBAL queries (withAssignedAddress null) are "MY workforce"
        //   semantics: the six specialist surfaces (ImportManagerDialog,
        //   ImportPartnershipSettings, HeadquartersList ×3 [purchasing/HR/
        //   headhunter], ScheduleDaySelection's HR check) and EmploymentGoal's
        //   counts — injected records filtered here, one choke point.
        // Merged-company partner staff stay visible (same exemption as the
        // MyEmployees filter above).
        [HarmonyPatch(typeof(Helpers.EmployeeHelper), nameof(Helpers.EmployeeHelper.GetEmployeeInstances),
                      typeof(EmployeeInstancesQueryInfo), typeof(System.Collections.Generic.List<Entities.EmployeeInstance>))]
        public static class Patch_EmployeeQuery_GlobalHidesInjected
        {
            private static float _nextLog;
            static void Postfix(EmployeeInstancesQueryInfo queryInfo, System.Collections.Generic.List<Entities.EmployeeInstance> __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // H-FORK-1: holds in the offline fork
                    // QueryInfo is a STRUCT; Address is a class with overloaded operators — use a
                    // pattern match so the null test can't route through an operator surprise.
                    if (__result == null || queryInfo.withAssignedAddress is not null) return;
                    int removed = __result.RemoveAll(e =>
                        e != null && (MPRegisterSync.IsSyntheticDuty(e.id)   // round-100: stand-ins never answer queries
                                      || (MPRegisterSync.IsInjectedStaff(e.id)
                                          && !MPRegisterSync.IsInjectedFromMergedPartner(e.id)
                                          && !SharedShopStaff.ShowInMyEmployees(e.id))));   // shared-shop slice 3: ONLY the My Employees list may show the owner's people; goals/counts/specialists still hide them
                    if (removed > 0 && UnityEngine.Time.unscaledTime >= _nextLog)
                    {
                        _nextLog = UnityEngine.Time.unscaledTime + 5f;
                        Plugin.Logger.LogInfo($"[StaffRoster] global employee query: hid {removed} injected partner record(s) (specialist/goal surfaces, round-80).");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] employee query filter: {ex.Message}"); }
            }
        }

        // Round-80 survey, raw-list consumer: EmployeeMaxLevelGoal takes a GLOBAL
        // max over every employee's skills — a partner's level-10 worker completed
        // YOUR goal. Recompute minus injected (merged partners exempt, as above).
        [HarmonyPatch(typeof(EmployeeMaxLevelGoal), "GetValue")]
        public static class Patch_EmployeeMaxLevelGoal_SkipInjected
        {
            static bool Prefix(ref float __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;   // H-FORK-1: holds in the offline fork
                    var list = Helpers.EmployeeHelper.GetEmployeeInstances();
                    if (list == null) return true;
                    __result = list
                        .Where(e => e?.characterData?.skills != null
                                    && !MPRegisterSync.IsSyntheticDuty(e.id)   // round-100: stand-in skill can't be "your best employee"
                                    && !(MPRegisterSync.IsInjectedStaff(e.id) && !MPRegisterSync.IsInjectedFromMergedPartner(e.id)))
                        .SelectMany(e => e.characterData.skills)
                        .Select(x => x.value)
                        .DefaultIfEmpty(0f)
                        .Max();
                    return false;
                }
                catch { return true; }   // any surprise → native behavior
            }
        }

        // ── Assign-to-business dropdowns: hide partner businesses (2026-07-16) ─────
        // The game's two "assign employee to business" dropdowns enumerate every
        // player-rented registration — which in MP includes the PARTNER's shops, so
        // players could "swap" employees into each other's businesses.  A cross-
        // assigned record exists only on the assigning machine (roster sync
        // publishes own-shop staff only): the partner sees ghost workers they never
        // hired, and the record keeps generating demands here.  Filter both
        // dropdowns; MPSaveIntegrity's cross-owner-staff class repairs saves that
        // already mixed.

        /// <summary>MyEmployees detail page: PlayerBuildingFilter is the dropdown's
        /// own per-registration filter, so vetoing here keeps the option list and
        /// the _businessRegistrations index mapping aligned for free.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.MyEmployees.MyEmployees), "PlayerBuildingFilter")]
        public static class Patch_MyEmployees_AssignDropdown_HidePartnerBusinesses
        {
            static void Postfix(UI.Smartphone.Apps.MyEmployees.MyEmployees __instance,
                                BuildingRegistration buildingRegistration, ref bool __result)
            {
                try
                {
                    if (!__result) return;
                    if (SharedShopStaff.DropdownForGrantRecord) return;   // shared-shop slice 3: the dropdown for an OWNER's employee lists that owner's shared shops
                    if (!GameStatePatcher.IsForeignPlayerBusiness(buildingRegistration)) return;
                    // Native exception preserved: stay visible while the employee is
                    // STILL assigned there (pre-repair save) so the dropdown can show
                    // the current assignment; the integrity sweep unassigns on load.
                    var emp = AccessTools.Field(typeof(UI.Smartphone.Apps.MyEmployees.MyEmployees), "_selectedEmployeeInstance")
                                         ?.GetValue(__instance) as Entities.EmployeeInstance;
                    if (emp != null && buildingRegistration.Address == emp.assignedAddress) return;
                    __result = false;
                }
                catch { }   // never break the dropdown over a guard error
            }
        }

        /// <summary>HeadHunter candidate rows build their assign dropdown from an
        /// inline LINQ over ALL rented registrations — no filter seam, so re-filter
        /// the private list after SetData and rebuild the options the same way the
        /// native code does (option i+1 must map to _buildingRegistrations[i]).</summary>
        [HarmonyPatch(typeof(CandidateCellView), "SetData")]
        public static class Patch_CandidateCell_AssignDropdown_HidePartnerBusinesses
        {
            private static int _logged;
            static void Postfix(CandidateCellView __instance)
            {
                try
                {
                    // 1.0 PORT (sweep-2 #2): SetData now assigns _buildingRegistrations from a STATIC
                    // cache (BusinessOptionsByEmployeeSetup) that hands every row the SAME list
                    // instance.  RemoveAll on that shared list poisoned the cache: the next cache hit
                    // saw removed==0 (this postfix early-returns) while the native options still held
                    // the partner names — a misaligned dropdown where a pick lands on the WRONG
                    // business (AssignBusiness indexes _buildingRegistrations[index-1]).  Filter a
                    // COPY and point the row's field at the copy; the cache is never touched.
                    var shared = AccessTools.Field(typeof(CandidateCellView), "_buildingRegistrations")
                                            ?.GetValue(__instance) as System.Collections.Generic.List<BuildingRegistration>;
                    if (shared == null || shared.Count == 0) return;
                    var emp = AccessTools.Field(typeof(CandidateCellView), "_employeeInstance")
                                         ?.GetValue(__instance) as Entities.EmployeeInstance;
                    var regs = new System.Collections.Generic.List<BuildingRegistration>(shared);
                    int removed = regs.RemoveAll(r => GameStatePatcher.IsForeignPlayerBusiness(r)
                                                   && (emp == null || r.Address != emp.assignedAddress));
                    if (removed == 0) return;
                    AccessTools.Field(typeof(CandidateCellView), "_buildingRegistrations")
                              ?.SetValue(__instance, regs);   // the row now reads OUR filtered copy
                    var opts = new System.Collections.Generic.List<string> { VehicleStoragePanel.Localize("common_unassigned") };
                    foreach (var r in regs)
                    {
                        string dn; try { dn = r.GetDisplayName(); } catch { dn = r.BusinessName ?? "?"; }
                        opts.Add(dn);
                    }
                    int sel = 0;
                    try { if (emp != null && emp.IsAssignedToAnyBusiness()) sel = regs.FindIndex(r => r.Address == emp.assignedAddress) + 1; } catch { }
                    __instance.assignBusiness.SetOptions(opts, false, sel);
                    if (_logged++ < 3)
                        Plugin.Logger.LogInfo($"[StaffRoster] candidate assign-dropdown: hid {removed} partner business(es).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] candidate dropdown filter: {ex.Message}"); }
            }
        }

        /// <summary>Mass-action variants of the same hole (Tier-1 ownership sweep,
        /// 2026-07-16): AssignBusinessMassAction and AssignToBusinessAndHireMassAction
        /// each carry their OWN private PlayerBuildingFilter and bulk-write
        /// assignedAddress.  Both the option list and the confirm handler re-query
        /// through that same filter, so a filter veto stays index-consistent.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.MyEmployees.Types.AssignBusinessMassAction), "PlayerBuildingFilter")]
        public static class Patch_MassAssign_HidePartnerBusinesses
        {
            static void Postfix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                try
                {
                    if (!__result) return;
                    // Shared-shop ruling 23: while the BULK panel is scoped to a granting owner, THAT owner's shared
                    // shops are the whole point of the list — the same stand-down the single-employee dropdown above
                    // has carried since slice 3. Without it this veto silently emptied the panel (field 2026-08-22):
                    // the game's own filter answered yes and this line overrode it to no.
                    if (SharedShopStaff.AllowsInMassAssign(buildingRegistration)) return;
                    if (GameStatePatcher.IsForeignPlayerBusiness(buildingRegistration)) __result = false;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.MyEmployees.Types.AssignToBusinessAndHireMassAction), "PlayerBuildingFilter")]
        public static class Patch_MassAssignHire_HidePartnerBusinesses
        {
            static void Postfix(BuildingRegistration buildingRegistration, ref bool __result)
            {
                try { if (__result && GameStatePatcher.IsForeignPlayerBusiness(buildingRegistration)) __result = false; } catch { }
            }
        }

        // ── Ownership choke point + BizMan visibility block (approved 2026-07-16) ──
        // Tier-2 audit verdict: GetPlayerBuildingRegistrations' only base predicate
        // is RentedByPlayer, and in MP the partner's shops are RentedByPlayer
        // replicas — so EVERY native "pick one of your businesses" surface offered
        // them (worst: Moving Service can SHUT DOWN the origin business; Interior
        // Firm installs a new interior).  One postfix here closes: MovingService
        // origin+destination, Delivery/FurnitureDelivery/Marketing/Recruitment/
        // InteriorFirm settings, LogisticsManagerPlanUI, PurchasingAgent, and the
        // BizManBusiness page-nav dropdown.  Merger-aware via HideFromOwnAssetLists
        // (flipped regs stay visible — shared management is the merger's job).
        // Full audit: .modding/03-systems/ownership-exposure-map.md.
        // 2026-09-12: the five SERVICE pickers (Moving origin+destination, Interior Installation Firm,
        // Recruitment campaign, Furniture/Food delivery destinations) drop a partner's shops AGAIN in
        // AccessGates, because their bookings are PER-MACHINE contracts that execute on the booking machine.
        [HarmonyPatch(typeof(Helpers.BuildingHelper), nameof(Helpers.BuildingHelper.GetPlayerBuildingRegistrations))]
        public static class Patch_GetPlayerBuildingRegistrations_HidePartnerBusinesses
        {
            static void Postfix(System.Collections.Generic.List<BuildingRegistration> __result)
            {
                try { __result?.RemoveAll(r => GameStatePatcher.HideFromOwnAssetLists(r)); } catch { }
            }
        }

        /// <summary>BizMan main business list: enumerates registrations directly
        /// (not via the helper), so filter its models.  Rented rows hide via the
        /// business-owner stamp; real-estate rows (isRealEstate branch) via the
        /// BUILDING-owner stamp — defensive, AI landlords can't match because
        /// their buildings are never BuildingOwnedByPlayer.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.BusinessScrollerController), "PopulateAllModels")]
        public static class Patch_BizManBusinessList_HidePartnerBusinesses
        {
            private static int _logged;
            static void Postfix(System.Collections.Generic.List<BusinessCellView.BusinessModel> allModels)
            {
                try
                {
                    if (allModels == null || allModels.Count == 0) return;
                    // No connection gate: offline MP saves still carry ownership
                    // stamps and the partner's shops must stay unmanageable there too.
                    int removed = allModels.RemoveAll(m =>
                    {
                        if (m == null) return false;
                        BuildingRegistration? reg = null;
                        try { reg = Helpers.BuildingHelper.GetBuildingRegistration(m.Address); } catch { }
                        if (reg == null) return false;
                        if (GameStatePatcher.HideFromOwnAssetLists(reg)) return true;
                        try
                        {
                            string bOwner = reg.buildingOwnerRivalId?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(bOwner) && bOwner != MPConfig.PlayerId && reg.BuildingOwnedByPlayer
                                && !MergerFlip.IsFlipped(GameStateReader.AddressKey(reg)))
                                return true;
                        }
                        catch { }
                        return false;
                    });
                    if (removed > 0 && _logged++ < 3)
                        Plugin.Logger.LogInfo($"[BizMan] business list: hid {removed} partner entr(ies) — rivals don't manage each other's businesses.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[BizMan] list filter: {ex.Message}"); }
            }
        }

        /// <summary>BizMan HQ and Warehouse tabs enumerate directly per entry —
        /// prefix-skip the entry builder for partner buildings.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HeadquartersList), "SetUpEntry")]
        public static class Patch_BizManHQList_HidePartnerBusinesses
        {
            static bool Prefix(BuildingRegistration headquarters)
            {
                try
                {
                    if (GameStatePatcher.HideFromOwnAssetLists(headquarters)) return false;
                    // HQ-PARITY-1 P7: the flip makes every partner headquarters RentedByPlayer here, and the
                    // hub lists every such registration (decompile HeadquartersList.cs:26) - so a merged
                    // company was drawing one card per member.  ONE card now: the backing headquarters
                    // (CompanyPlans.BackingHq), which is my own where I have one.
                    if (CompanyPlans.HideExtraHqCard(headquarters)) return false;
                    CompanyPlans.NoteHqCardDrawn(GameStateReader.AddressKey(headquarters));
                    return true;
                }
                catch { return true; }
            }
        }

        /// <summary>HQ-PARITY-1 P7.  The company's ONE card must count the company's PEOPLE: the native
        /// counter queries one headquarters address (HeadquartersList.SetUpEmployeeCounter :69-83), so the
        /// five labels on the backing card are re-summed over every company headquarters - my own and every
        /// flipped partner one - with that same native helper.  Only the backing card is touched; an own
        /// second headquarters, a non-member and an off-session machine keep the native number.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HeadquartersList), "SetUpEmployeeCounter")]
        public static class Patch_BizManHQList_UnionCounters
        {
            static void Postfix(UnityEngine.Transform entry, string tabName, string skill)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || entry == null) return;   // inert without a merger
                    if (!CompanyPlans.DrawingCompanyCard()) return;
                    var button = entry.GetButtonByName("EmployeeCounter/" + tabName);
                    if (button == null) return;
                    button.transform.GetLabelByName("Count").text = CompanyPlans.UnionCounterFor(skill).ToString();
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[BizMan] headquarters counter union: {ex.Message}"); }
            }
        }

        /// <summary>HQ-PARITY-1 P7: the hub's list pass, which is where the card state is logged - once, and
        /// again whenever it changes (a member joins or leaves, a headquarters is bought or sold; the hub
        /// rebuilds on all of them).</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HeadquartersList), "Load")]
        public static class Patch_BizManHQList_CardPass
        {
            static void Prefix() { try { CompanyPlans.HqCardPassBegin(); } catch { } }
            static void Postfix() { try { CompanyPlans.HqCardPassDone(); } catch { } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.WarehouseList), "SetUpEntry")]
        public static class Patch_BizManWarehouseList_HidePartnerBusinesses
        {
            static bool Prefix(Entities.Warehouse warehouse)
            {
                try
                {
                    // Shared-shop ruling 26 (2026-08-22): a helper SEES the owner's warehouses and factories in this
                    // list (teal, Presentation-only page). One predicate for "listed" and "not vetoed" so the two can
                    // never disagree — the bug class of 2026-08-22.
                    if (SharedShopVisibility.IsSharedWarehouseEntry(warehouse)) return true;
                    return !GameStatePatcher.HideFromOwnAssetLists(warehouse);
                }
                catch { return true; }
            }
        }

        // (Round-96: the signature-scanned Patch_HrManagerList_HideInjectedStaff was RETIRED here —
        //  superseded by the typed Patch_EmployeesScroller_HideInjected stash (round-94) on the same
        //  Load(List<EmployeeInstance>, bool, string) target; the scan ALSO threw a HarmonyException
        //  outright on some installs (field 20260726-034343, Evi) — one FAILED line every launch.)


        // ── Freeze guard: stuck selection/overlay after entity-destroy NRE ────
        // Field 2026-07-16 ("suddenly couldn't move" in the partner's shop) —
        // HYPOTHESIS (attempt 1/2): EntityController.OnDestroy →
        // ResetCurrentEntitySelected(entity) calls entity.OnIoExit() BEFORE
        // clearing the selection, and OnIoExit NREs on half-destroyed state (×5
        // that session), aborting BEFORE HideSimpleOverlayAndClearCta.  Result:
        // currentTargetEntity/CurrentTarget stay set and the interaction CTA
        // overlay stays up over a dead entity — a stuck overlay eating clicks is
        // a movement lock in a click-to-move game.  The Finalizer completes the
        // native method's own intent (clear selection, hide overlay), swallows
        // the teardown NRE, and timestamps every occurrence so field logs can
        // correlate with freeze reports (the [SynthStaff] INSIDE line is the
        // trigger side).  Covers native-caused destroys (despawning vehicles
        // etc.) too, not just our synthetic-staff removal.
        [HarmonyPatch(typeof(MouseController), nameof(MouseController.ResetCurrentEntitySelected))]
        public static class Patch_MouseController_ResetSelected_FreezeGuard
        {
            static Exception? Finalizer(Exception? __exception, EntityController __0)
            {
                if (__exception == null) return null;
                try
                {
                    string what = "?";
                    try { what = __0 == null ? "null" : __0.GetType().Name; } catch { }
                    // Clear when it's the same entity (native intent) or the field
                    // holds a destroyed leftover (Unity fake-null) — never a live
                    // different entity the cursor has since moved to.
                    if (ReferenceEquals(MouseController.currentTargetEntity, __0) || MouseController.currentTargetEntity == null)
                    {
                        MouseController.currentTargetEntity = null;
                        try { AccessTools.Field(typeof(MouseController), "CurrentTarget")?.SetValue(null, null); } catch { }
                    }
                    try
                    {
                        var om = InstanceBehavior<Player.HUD.ItemInfoOverlays.OverlayManager>.Instance;
                        if (om != null)
                        {
                            bool showing;
                            try { showing = om.IsShowingOverlayOverItem(__0); } catch { showing = true; }   // the check itself can die on the dead entity
                            if (showing) om.HideSimpleOverlayAndClearCta();
                        }
                    }
                    catch { }
                    Plugin.Logger.LogWarning($"[IoGuard] ResetCurrentEntitySelected({what}) threw {__exception.GetType().Name} during entity teardown — cleared stuck selection/overlay (freeze guard).");
                }
                catch { return __exception; }   // guard itself failed — surface the original
                return null;   // swallow: the entity is dying anyway; native cleanup intent completed above
            }
        }

        // ── Own-only economics displays (approved 2026-07-16) ─────────────────
        // The daily financial summary builder iterates every RentedByPlayer reg —
        // partner replicas included — so DailySummary income, CharacterInfo weekly
        // profit, and the finances history all report COMBINED economics.  Veil the
        // foreign regs for the duration of the build (RunDaily-strip pattern:
        // Prefix hides, Finalizer restores and rethrows) so the native loop skips
        // them naturally.  Forward-looking only: summaries already persisted stay
        // polluted (not provably repairable).  Merger: CreateFinancialSummary is a
        // step of Patch_MergerAuthorityVeil (:3506), and that veil un-flips every
        // flipped registration for the pass — so a partner's flipped shops are
        // EXCLUDED here too, and each member's summary is only what they operate.
        [HarmonyPatch(typeof(Helpers.FinancialSummaryHelper), nameof(Helpers.FinancialSummaryHelper.CreateFinancialSummary))]
        public static class Patch_FinancialSummary_OwnBusinessesOnly
        {
            static void Prefix(out System.Collections.Generic.List<BuildingRegistration>? __state)
            {
                __state = null;
                try
                {
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null) return;
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null || !reg.RentedByPlayer) continue;
                        if (!GameStatePatcher.HideFromOwnAssetLists(reg)) continue;
                        reg.RentedByPlayer = false;
                        (__state ??= new()).Add(reg);
                    }
                    // Round-105 diagnostic (field 2026-07-27, RED ROC): a client's weekly income went
                    // $1,362,643 -> $0 with all 15 businesses still present, no reload and no interior
                    // loss. BOTH zeroed figures read gi.financialSummaries (RivalSelfStats weekly sum
                    // and BuildingRegistration.GetAvgDailyIncome), so one cause empties both — and this
                    // veil is the only thing that decides which businesses enter the day's summary.
                    // It previously logged NOTHING on the success path, so a misclassification here was
                    // invisible. Now every daily run says what it excluded: if a player's OWN shops
                    // appear in this list, their own income is being kept out of their own books.
                    if (__state != null && __state.Count > 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < __state.Count && i < 4; i++)
                            sb.Append(' ').Append(GameStateReader.AddressKey(__state[i]));
                        Plugin.Logger.LogInfo(
                            $"[Economics] daily summary excludes {__state.Count} business(es) attributed to ANOTHER player —{sb}" +
                            (__state.Count > 4 ? $" (+{__state.Count - 4} more)" : "") +
                            ". These are not counted in this machine's own income.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Economics] summary veil: {ex.Message}"); }
            }

            static Exception? Finalizer(Exception? __exception, System.Collections.Generic.List<BuildingRegistration>? __state)
            {
                if (__state != null)
                    foreach (var reg in __state)
                        try { reg.RentedByPlayer = true; } catch { }
                return __exception;   // pass through — the veil never swallows native failures
            }
        }

        /// <summary>Character page: the business count enumerates all rented regs.
        /// Recompute own-only and re-stamp the label.  The weekly-profit figure
        /// self-heals as veiled summaries accumulate. The wealth figures on THIS page come from
        /// PlayerHelper.GetPersonalWealth (unchanged by the 1.0 update): own Money, investments, loans and this
        /// save's VehicleInstances / playerBoats / realEstate — lists the mod never writes (a ghost vehicle is a
        /// GameObject, never a VehicleInstance record). The old NetWorth field fed the BANK LOAN CAP, not this page;
        /// the update removed it (review #3 MAJOR-2 corrected an earlier comment that conflated the two).</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.Persona.CharacterInfo), "OnEnable")]
        public static class Patch_CharacterInfo_OwnBusinessCount
        {
            static void Postfix(UI.Smartphone.Apps.Persona.CharacterInfo __instance)
            {
                try
                {
                    var gi = SaveGameManager.Current;
                    if (gi?.BuildingRegistrations == null) return;
                    int own = 0, foreign = 0;
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null || !reg.RentedByPlayer) continue;
                        bool revenue; try { revenue = BusinessTypeHelper.GetData(reg).HasTag(BigAmbitions.Tags.TagRef.Businesstag.generatesrevenue); } catch { continue; }
                        if (!revenue) continue;
                        if (GameStatePatcher.HideFromOwnAssetLists(reg)) foreign++; else own++;
                    }
                    if (foreign == 0) return;   // native count was already correct
                    // 1.0 rebuilt this screen: the label is a plain TMP_Text named 'totalBusinessesLabel'
                    // (was a TextLocalizationComponent 'totalBusinesses' with an Arguments property).
                    var lbl = AccessTools.Field(typeof(UI.Smartphone.Apps.Persona.CharacterInfo), "totalBusinessesLabel")?.GetValue(__instance) as TMPro.TMP_Text;
                    if (lbl != null) lbl.SetText(own.ToString());
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Economics] character count: {ex.Message}"); }
            }
        }

        /// <summary>EconoView: its selector listed partner businesses (their charts
        /// rendered as yours) AND its "Open in BizMan" button opened the selection
        /// by address — a path around the BizMan list block.  Filtering the private
        /// list closes both; options rebuilt exactly as OnEnable does.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.EconoView.EconoView), "OnEnable")]
        public static class Patch_EconoView_HidePartnerBusinesses
        {
            static void Postfix(UI.Smartphone.Apps.EconoView.EconoView __instance)
            {
                try
                {
                    var regs = AccessTools.Field(typeof(UI.Smartphone.Apps.EconoView.EconoView), "_businesses")
                                          ?.GetValue(__instance) as System.Collections.Generic.List<BuildingRegistration>;
                    if (regs == null || regs.Count == 0) return;
                    int removed = regs.RemoveAll(r => GameStatePatcher.HideFromOwnAssetLists(r));
                    if (removed == 0) return;
                    var opts = new System.Collections.Generic.List<string>();
                    foreach (var r in regs)
                    {
                        string n; try { n = !string.IsNullOrEmpty(r.BusinessName) ? r.BusinessName : Streets.AddressHelper.ToFormattedString(r.Address); } catch { n = "?"; }
                        opts.Add(n);
                    }
                    __instance.businessNameDropdown.SetOptions(opts, false);
                    Plugin.Logger.LogInfo($"[Economics] EconoView: hid {removed} partner business(es).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Economics] EconoView filter: {ex.Message}"); }
            }
        }

        // ── Pickup gate in foreign businesses (field 2026-07-19) ──────────────
        // A visitor grabbed a floor box in a partner's shop: native pickup has
        // no concept of "someone else's shop" (every reachable box is yours in
        // vanilla), so the take applied LOCALLY only — the visitor conjured a
        // sellable box that exists on no other machine (dup exploit) while the
        // owner's real box never moved.  Gate ItemController.TryToGrabItem: in
        // another player's business without a Business grant, refuse like a
        // rival store would.  Granted guests keep the owner-confirmed storage
        // flows; merger-flipped shops are shared and stay grabbable.
        [HarmonyPatch(typeof(ItemController), nameof(ItemController.TryToGrabItem))]
        public static class Patch_ItemController_TryToGrabItem_ForeignShopGate
        {
            private static int _logged;
            private static int _sessionOwnerLogged;

            /// <summary>The one foreign-grab refusal decision (grab audit P3 extracted it so the
            /// in-vehicle gate below cannot drift from this one): refuse when the current building
            /// is another player's business and the local player holds neither a Business grant
            /// from its owner nor a merger flip on it.  FOLD F1 (review 2026-09-18): ownership is
            /// decided LOCALLY here — the stamped owner id is non-empty, not ours, and is either a
            /// session player (IsSessionPlayerRivalId) or the registration carries the inherited
            /// rented flag — so the gate now holds in EVERY direction (host standing in a client's
            /// shop, client standing in another client's shop), not only where RentedByPlayer
            /// happens to be set on this machine.</summary>
            internal static bool RefusedForeignGrab(out string owner)
            {
                owner = "";
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return false;   // H-FORK-1: holds in the offline fork
                    var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    if (reg == null) return false;
                    // F1 (H1, review 2026-09-18): NOT IsForeignPlayerBusiness — its tail is
                    // `return reg.RentedByPlayer`, which is MACHINE-LOCAL: on the host a client's
                    // shop reads false and on client B a client-A mid-session rental reads false,
                    // so the gate never fired in those directions. IsForeignPlayerBusiness itself
                    // stays as is (the employee-dropdown filter wants its rented meaning).
                    string o = "";
                    try { o = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                    if (string.IsNullOrEmpty(o) || o == MPConfig.PlayerId) return false;
                    bool rented = false;
                    try { rented = reg.RentedByPlayer; } catch { }
                    bool sessionOwner = !rented && GameStatePatcher.IsSessionPlayerRivalId(o);
                    if (!rented && !sessionOwner) return false;
                    if (sessionOwner && _sessionOwnerLogged++ == 0)
                        Plugin.Logger.LogInfo("[PickupGate] foreign-shop gate: session-player ownership test active (PUT-PERM-1 H1)");
                    owner = MPRegisterSync.CurrentShopOwner;
                    if (string.IsNullOrEmpty(owner)) owner = o;
                    bool granted = !string.IsNullOrEmpty(owner) && GrantSync.IsGranted(GrantKind.Business, owner, MPConfig.PlayerId);
                    bool flipped = false;
                    try { flipped = MergerFlip.IsFlipped(GameStateReader.AddressKey(reg)); } catch { }
                    return !(granted || flipped);
                }
                catch { return false; }   // never break native pickup on a guard error
            }

            internal static void RefusalToast(string owner, string where)
            {
                PassengerHud.Toast($"This belongs to {(string.IsNullOrEmpty(owner) ? "another player" : owner)} — you need permission to take items here.");
                if (_logged++ < 10)
                    Plugin.Logger.LogInfo($"[PickupGate] grab refused at {where} (owner '{owner}', no Business grant).");
            }

            // PUT-PERM-1 (user ruling 2026-09-18) — the MIRROR of the take refusal above. A visitor
            // WITHOUT permission may not put goods into a partner's shelf/fridge/till/station either:
            // bundle 20260918-195543 showed six items merged into the host's shelf on the visitor's
            // replica only, and the owner's next absolute cargo statement erased them for good. Every
            // put entry point refuses BEFORE anything leaves the hands, so the goods stay in hand.
            // Same ownership decision (RefusedForeignGrab), same owner fallback, same log budget; the
            // toast gets its own 2 s throttle because one drag fires several hooks (merge + add, the
            // housing route and the deposit guard) and must not stack three identical toasts.
            private static int   _putLogged;
            private static float _nextPutToast;

            internal static void PutRefusalToast(string owner, string where)
            {
                float now = 0f;
                try { now = UnityEngine.Time.unscaledTime; } catch { }
                if (now >= _nextPutToast)
                {
                    _nextPutToast = now + 2f;
                    PassengerHud.Toast($"This belongs to {(string.IsNullOrEmpty(owner) ? "another player" : owner)} — you need permission to put items here.");
                }
                if (_putLogged++ < 10)
                    Plugin.Logger.LogInfo($"[PutGate] put refused at {where} (owner '{owner}', no Business grant).");
            }

            static bool Prefix(ItemController __instance, ref bool __result)
            {
                try
                {
                    if (!RefusedForeignGrab(out string owner)) return true;
                    __result = false;
                    RefusalToast(owner, "'" + (InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.BusinessName ?? "?") + "'");
                    return false;
                }
                catch { return true; }   // never break native pickup on a guard error
            }

            // GRAB AUDIT P2 (ruling 37, 2026-08-25): the round-269 GuestCargoGrab SENDER is RETIRED.
            // The removal chokepoint's delta forward now carries a chokepoint-NAMED remove that
            // survives the no-baseline and cap-suppression windows (BuildLocalEditDelta
            // knownRemovedId) — the one job this second channel actually performed. Audit findings
            // that sealed it: ordering-equivalent to the delta (same lane, same FIFO apply queue —
            // never an ordering guarantee); applier strictly weaker (no parent stackedItems fix-up,
            // no lineage guard, no mid-drag defer); its StockOnly branch was UNREACHABLE (every
            // TryToGrabItem success passes the removal first, so stillThere was always false). The
            // message type and both receive-side appliers stay for older-peer compatibility.
        }

        // GRAB AUDIT P3 (2026-08-25): the round-269 gate covered TryToGrabItem, but
        // ItemController.Interact has an UNGATED in-vehicle branch (:509-528, EA 0.11): sitting in
        // a vehicle inside a foreign business, a click on an alwaysinteractable item moves its
        // cargo STRAIGHT into the vehicle and removes the item — no grant check anywhere on the
        // path, the same local-only mint the gate exists to block (live dup exploit at drive-in
        // buildings). Same refusal decision, same toast, extracted from the gate so the two can
        // never drift.
        [HarmonyPatch(typeof(ItemController), nameof(ItemController.Interact))]
        public static class Patch_ItemController_Interact_ForeignVehicleGate
        {
            static bool Prefix(ItemController __instance, ref bool __result)
            {
                try
                {
                    if (!PlayerHelper.IsUsingVehicle) return true;               // the ungated branch needs a vehicle
                    if (!(__instance?.Item?.HasTag(BigAmbitions.Tags.TagRef.Itemtag.alwaysinteractable) ?? false)) return true;
                    if (!Patch_ItemController_TryToGrabItem_ForeignShopGate.RefusedForeignGrab(out string owner)) return true;
                    __result = true;   // consumed — nothing moves
                    Patch_ItemController_TryToGrabItem_ForeignShopGate.RefusalToast(owner, "the in-vehicle pickup");
                    return false;
                }
                catch { return true; }   // never break native interaction on a guard error
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;   // review r7 #2: holds in the offline fork
                if (GameStatePatcher.IsAnyPlayerBusiness(__instance))
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // review r6 #1: the offline fork keeps this shield (as the valuation Prefix does) — with the valuation at $0 and no shield, any $1 offer took a friend's replica
                if (GameStatePatcher.IsSessionPlayerRivalId(rivalId))
                {
                    __result = float.MaxValue;
                    // Round-50: say so — a silently-impossible offer reads as "overtake is broken"
                    // in reports (2026-07-21-204010, where a CONTAMINATED player stamp on an AI shop
                    // sent every offer down this branch).
                    Plugin.Logger.LogInfo($"[EconShield] overtake offer vs player-rival '{rivalId}' blocked (players can't buy each other out; mergers are the sharing path).");
                }
            }
        }

        // ── Round-196 (field 20260730-213942): the acceptRate=MaxValue shield FAILS OPEN when
        // CompetitionHelper.CalculateAiOwnedValuation returns 0 for the target (untagged building
        // type → flat 0f; or a mirrored partner business with no local financial/layout data).
        // The native gate is `offer < valuation × acceptRate` and 0 × MaxValue = 0, so ANY offer
        // was accepted: $50 claimed a partner's shop locally and the money was deducted. Two
        // layers, per the design principles (guard the MUTATION, never just a multiplier feeding
        // a downstream comparison):
        //   (1) the offer SUBMIT refuses player-owned targets before any money moves, with
        //       player-visible feedback (round-50 lesson: silent blocks read as "broken");
        //   (2) OvertakeBusiness itself — the single transfer choke point — refuses player-owned
        //       targets unless the accepted-offer flow (round-196 Phase 1) has authorized this
        //       exact address. The guard is the feature's door, not a wall to tear down later.
        /// <summary>Address key authorized for ONE player-business transfer by the accepted-offer
        /// flow. Set immediately before the flow invokes OvertakeBusiness on the same call stack;
        /// consumed by the guard. Null = no transfer authorized.</summary>
        internal static string? AuthorizedPlayerBusinessTransfer;

        [HarmonyPatch(typeof(BizManPresentation), nameof(BizManPresentation.SendOvertakeOffer))]
        public static class Patch_SendOvertakeOffer_RouteToPlayerOffer
        {
            // ── B1 (log-only, 2026-09-18): the HOST keeps the native instant buyout for an AI-run business. These
            // two lines say what that flow did to the business's staff. The Postfix runs when SendOvertakeOffer
            // RETURNS, which is not necessarily when the transfer completes — the native flow can finish later, so
            // the line says which of the two it was rather than pretending the numbers are final.
            private static string _b1Addr = "";
            private static int    _b1Before;

            private static int OwnEmployeesAt(string addr)
            {
                int n = 0;
                try
                {
                    var list = SaveGameManager.Current?.EmployeeInstances;
                    if (list != null)
                        foreach (var e in list)
                        {
                            if (e == null || e.assignedAddress == null) continue;
                            if (GameStateReader.AddressKey(e.assignedAddress) == addr) n++;
                        }
                }
                catch { }
                return n;
            }

            private static void TraceHostBuyout(BuildingRegistration reg)
            {
                try
                {
                    _b1Addr   = GameStateReader.AddressKey(reg);
                    _b1Before = OwnEmployeesAt(_b1Addr);
                    int ai = 0;   try { ai   = reg.aiEmployees != null ? reg.aiEmployees.Count : 0; } catch { }
                    string type = ""; try { type = reg.businessTypeName ?? ""; } catch { }
                    Plugin.Logger.LogWarning($"[Takeover/Host] native buyout '{_b1Addr}' type={type} aiEmployees={ai} ownEmployeesBefore={_b1Before}");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover/Host] WARNING B1 buyout trace: {ex.Message}"); }
            }

            static void Postfix()
            {
                try
                {
                    if (_b1Addr.Length == 0) return;
                    string addr = _b1Addr; _b1Addr = "";
                    bool rented = false; bool found = false;
                    try
                    {
                        var gi = SaveGameManager.Current;
                        if (gi?.BuildingRegistrations != null)
                            foreach (var r in gi.BuildingRegistrations)
                                if (r != null && GameStateReader.AddressKey(r) == addr) { rented = r.RentedByPlayer; found = true; break; }
                    }
                    catch { }
                    int after = OwnEmployeesAt(addr);
                    Plugin.Logger.LogWarning($"[Takeover/Host] native buyout '{addr}' returned: ownEmployeesAfter={after} (before {_b1Before}) RentedByPlayer={(found ? rented.ToString() : "reg gone")}"
                        + (rented ? " — the transfer had completed by the time SendOvertakeOffer returned." : " — NOT transferred yet on return; the native flow completes later, so these numbers are the pre-transfer state."));
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Takeover/Host] WARNING B1 buyout postfix: {ex.Message}"); }
            }

            static bool Prefix(BizManBusiness ___bizManBusiness, TMPro.TMP_InputField ___offerAmountInputField)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;   // review r6 #1: the offline fork keeps this shield
                    var reg = ___bizManBusiness?.buildingRegistration;
                    if (reg == null) return true;
                    if (!GameStatePatcher.IsAnyPlayerBusiness(reg))
                    {
                        // AI-run business. HOST keeps the native instant flow (it owns
                        // the authoritative data). CLIENTS go through host arbitration
                        // (round-204b, MPTakeover) — the client-local native flow is the
                        // replica-mutation bug ("coop partner's taken-over business
                        // reset") plus the $0-valuation exploit.
                        if (MPClient.IsConnected)
                            return MPTakeover.ClientOfferPrefix(reg, ___offerAmountInputField);
                        TraceHostBuyout(reg);   // B1 (log-only, 2026-09-18): what the native instant flow does to the staff
                        return true;
                    }

                    // Round-196 Phase 1: the native offer box, aimed at a PLAYER's shop,
                    // becomes a hub purchase offer — no money moves unless they accept.
                    string owner = reg.businessOwnerRivalId?.ToString() ?? "";
                    string addr  = GameStateReader.AddressKey(reg);
                    string biz   = ""; try { biz = reg.BusinessName?.ToString() ?? ""; } catch { }
                    if (!GameStatePatcher.IsSessionPlayerRivalId(owner))
                    {
                        // Player-owned per the ledger but no owner stamp to route to — refuse
                        // rather than let the native flow claim it (the round-196 hole).
                        Plugin.Logger.LogWarning($"[Offers] offer for '{addr}' refused — player-owned but no resolvable owner id (stamp='{owner}').");
                        try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Error, "You can't buy out another player's business."); } catch { }
                        return false;
                    }
                    if (!float.TryParse(___offerAmountInputField?.text, out var amount) || amount <= 0f)
                    {
                        try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Error, "common_notification_invalid_amount"); } catch { }
                        return false;
                    }
                    if (!MPServer.IsRunning && !MPClient.IsConnected)
                    {
                        // Review r7 #1: in the offline fork MPHub.OfferBusiness would book the offer locally (reserving the money)
                        // and SendHub would drop it silently — the "Offer sent" notice below would be a lie. Same refusal as above.
                        Plugin.Logger.LogWarning($"[Offers] offer for '{addr}' refused — no host link (offline fork); nothing can be sent.");
                        try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Error, "The host connection is gone, so offers can't be sent from this offline copy."); } catch { }   // wording approved by the user 2026-09-05 (review r8 #4: the reused line named the wrong reason)
                        return false;
                    }
                    if (MPHub.OfferBusiness(owner, amount, addr, biz))
                    {
                        Plugin.Logger.LogInfo($"[Offers] purchase offer sent: '{biz}' at {addr} to '{owner}' for ${amount:N0}.");
                        try { UI.Notification.Notifications.Show(UI.Notification.NotificationType.Info, $"Offer sent to {owner} — no money moves unless they accept. Track it in the Business app."); } catch { }
                        try { ___offerAmountInputField.text = ""; } catch { }
                    }
                    return false;
                }
                catch { }
                return true;
            }
        }

        // Round-196: while our accepted-offer transfer drives the native takeover on the
        // buyer's machine, the game must neither fabricate fresh staff (the seller's real
        // workers transfer instead) nor pop BizMan open mid-play.
        [HarmonyPatch(typeof(Helpers.BusinessEmployeeGenerator), nameof(Helpers.BusinessEmployeeGenerator.GenerateEmployees))]
        public static class Patch_GenerateEmployees_SkipDuringTransfer
        {
            static bool Prefix()
            {
                if (!MPOffers.TransferInProgress) return true;
                Plugin.Logger.LogInfo("[Offers] GenerateEmployees skipped — transferred staff arrive with the sale.");
                return false;
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.BizMan), nameof(UI.Smartphone.Apps.BizMan.BizMan.Open))]
        public static class Patch_BizManOpen_SkipDuringTransfer
        {
            static bool Prefix() => !MPOffers.TransferInProgress;
        }

        // OvertakeBusiness ends with AutoFillSchedule(bizManBusiness) — null on our path, and
        // the method dereferences it unconditionally (rig 2026-07-30: the NRE aborted the whole
        // post-claim restoration, losing the transferred schedule). We restore the seller's REAL
        // schedule afterwards anyway — auto-fill must not run at all during a transfer.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule), nameof(UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule.AutoFillSchedule))]
        public static class Patch_AutoFillSchedule_SkipDuringTransfer
        {
            // Round-204c: also skipped during an arbitrated AI-takeover claim — same
            // AutoFillSchedule(null) NRE; MPTakeover runs the registration-level
            // auto-filler itself right after the native claim.
            static bool Prefix() => !MPOffers.TransferInProgress && !MPTakeover.ClaimInProgress;
        }

        [HarmonyPatch(typeof(BizManPresentation), nameof(BizManPresentation.OvertakeBusiness))]
        public static class Patch_OvertakeBusiness_GuardPlayerTargets
        {
            static bool Prefix(BuildingRegistration buildingRegistration)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;   // review r6 #1: the offline fork keeps this shield
                    if (buildingRegistration == null || !GameStatePatcher.IsAnyPlayerBusiness(buildingRegistration)) return true;
                    string key = GameStateReader.AddressKey(buildingRegistration);
                    if (AuthorizedPlayerBusinessTransfer == key)
                    {
                        AuthorizedPlayerBusinessTransfer = null;
                        Plugin.Logger.LogInfo($"[EconShield] player-business transfer authorized at '{key}' (accepted offer) — native takeover proceeding.");
                        return true;
                    }
                    Plugin.Logger.LogWarning($"[EconShield] OvertakeBusiness BLOCKED at '{key}' — session player's business, no accepted-offer authorization (round-196).");
                    return false;
                }
                catch { return true; }
            }
        }

        // Round-55 (Westi report 2026-07-22, screenshot-confirmed): the rival "hire your best
        // employees" defense (RivalDefenseHelper.ActivateHireEmployees) ranks poach targets by
        // skill+satisfaction — our synthetic's pinned satisfaction 100 made it the FIRST pick, so
        // the rival "poached" the fake employee and the owner got the training-ultimatum message
        // from contact "On-Duty Staff" ("...or I'll leave"). IsPoachable is the game's own
        // can-this-employee-be-poached predicate and the single chokepoint every selector honors —
        // a synthetic is never poachable. (The stale contact thread in affected saves is purged by
        // MPRegisterSync.StripOrphanSyntheticEmployees at world-ready.)
        [HarmonyPatch(typeof(Entities.EmployeeInstance), nameof(Entities.EmployeeInstance.IsPoachable), MethodType.Getter)]
        public static class Patch_EmployeeInstance_IsPoachable_NeverSynthetic
        {
            static void Postfix(Entities.EmployeeInstance __instance, ref bool __result)
            {
                try
                {
                    if (__result && __instance?.id != null && __instance.id.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix))
                        __result = false;
                }
                catch { }
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
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;   // single player — run native; H-FORK-1: holds in the offline fork
                    if (__instance == null) return true;

                    // Round-62 (field: "I see other players' staff issues but not my own,
                    // and no names" — Just JP group, 2026-07-22): an INJECTED partner-staff
                    // display copy must never complain here — the OWNER's machine holds the
                    // real record and delivers the complaint to the right player. The copies
                    // were prime complainers (no native task assigned) and, when the roster
                    // carried no name, messaged from a contact literally called "Staff".
                    try { if (MPRegisterSync.IsInjectedStaff(__instance.id)) return false; } catch { }
                    // NOTIFY-2 fold G (2026-09-17): a SYNTHETIC DUTY stand-in has no life of its own
                    // either. Without this it could complain - and, with a deadline expiring, RESIGN -
                    // into the very batch NotificationRelay.InOwnStaffBatch treats as proof that the
                    // person is one of MY staff. The training purge (:3371-3387) already tests this
                    // prefix; the two hourly prefixes now agree with it.
                    try { if ((__instance.id ?? "").StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) return false; } catch { }

                    // Round-62: a NULL/undefined address is the NATIVE state of a hired-but-
                    // unassigned employee — vanilla lets them complain ("the company" message;
                    // e.g. new hires demanding training). The old blanket skip silenced exactly
                    // those. The rival-poor NRE this guard originally masked was shielded at
                    // Complaint.GetComplaintMessageData (2026-07-09) until game Build 3674 (2026-09-02)
                    // fixed it natively (no-rival message types); the shield is retired.
                    var addr = __instance.assignedAddress;
                    if (addr == null || string.IsNullOrEmpty(addr.streetName)) return true;   // idle/unassigned — native handles

                    BuildingRegistration? reg = null;
                    try { reg = Helpers.BuildingHelper.GetBuildingRegistration(addr); } catch { reg = null; }
                    if (reg != null) return true;   // normal employee with a resolvable workplace — native runs

                    // The TRUE MP data gap: a real address this machine can't resolve
                    // (registration not synced) — skip so the hourly tick survives.
                    LogOrphan(__instance);
                    return false;
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // H-FORK-1: holds in the offline fork
                var states = SaveGameManager.Current?.rivalStates;
                if (states == null) return;
                int removed = states.RemoveAll(rs =>
                {
                    if (rs == null) return true;
                    // Guard the lookup like the sibling at Patch_GetAllRivalData: a throw here would abort the
                    // whole RemoveAll and escape the Prefix, disrupting the day-roll economy step. On error,
                    // KEEP the entry (never delete a rival on a transient lookup failure).
                    try { return BigAmbitions.Rivals.RivalsHelper.GetRivalData(rs.rivalId) == null; }
                    catch { return false; }
                });
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
            private static bool _boatLogged;
            private static int  _boatWarned;

            /// <summary>TICK-ISO-1 fold (review H1): the clock BEFORE this tick. RunMainGameTick adds the delta to
            /// SaveGameManager.Current.Minute at GameManager.cs:568 and the hourly loop subtracts 60 per hour after
            /// RunHourly; a throw inside RunHourly left Minute ACCUMULATING every swallowed frame, and the first clean
            /// tick would have run the whole backlog of hours in one burst. On a swallow the Finalizer puts the clock
            /// back where this tick found it: the clock holds while the fault persists, and never bursts.</summary>
            [ThreadStatic] private static float _minuteBefore;

            static void Prefix(ref float deltaTimeWithMultiplier)
            {
                try { _minuteBefore = SaveGameManager.Current != null ? SaveGameManager.Current.Minute : -1f; } catch { _minuteBefore = -1f; }
                // Gate on an active MP game: a stale AheadHeld must never freeze the clock/economy
                // in single-player after a disconnect.
                bool inSession = MPServer.IsRunning || MPClient.InMpGame;

                // SPEED-SHARED (deliberate deviation from native, required by the shared clock): on the casino boat the game runs
                // the clock at HALF speed on this machine and ignores the multiplier entirely — GameManager.cs:422-424 calls
                // RunMainGameTick(Time.deltaTime * 0.5f) instead of the normal * MinutesMultiplier branch (:428). A shared clock
                // cannot slow one machine: before this build a boat visit put that player behind the session and the drift
                // correction papered over it with catch-up bursts every heartbeat. So in a session the boat branch is rescaled to
                // the session speed. The equality test is what keeps this surgical: only the boat branch's EXACT product is
                // rewritten (the same float computation from the same frame), so the mod's own catch-up and skip calls, which pass
                // other magnitudes, are never rescaled.
                if (inSession)
                {
                    try
                    {
                        if (CasinoBoatManager.IsOnCasinoBoat)
                        {
                            if (deltaTimeWithMultiplier == UnityEngine.Time.deltaTime * 0.5f)
                            {
                                float v = OptionsGuard.EffectiveSessionValue();
                                deltaTimeWithMultiplier = UnityEngine.Time.deltaTime * v;
                                if (!_boatLogged)
                                {
                                    _boatLogged = true;   // one line per boarding
                                    Plugin.Logger.LogInfo($"[OptionsGuard] casino boat: the clock keeps the session speed {v:0.00}x here (the game's own boat rate is 0.5x; a shared clock cannot slow one machine).");
                                }
                            }
                        }
                        else _boatLogged = false;   // stepped off — arm the next boarding's line
                    }
                    catch (Exception ex) { try { if (_boatWarned++ < 5) Plugin.Logger.LogWarning($"[OptionsGuard] casino boat clock: {ex.Message}"); } catch { } }
                }

                if (inSession && TimeSync.AheadHeld && !MPRestSync.SkipActive)
                    deltaTimeWithMultiplier = 0f;
            }

            // TICK-ISO-1 (2026-09-17).  GameStatePatcher.DrainQueue rides Patch_GameManager_Update.Postfix (:344-352)
            // and a Postfix does NOT run when the original throws.  A native throw inside RunMainGameTick therefore
            // escaped both and stalled replication on BOTH machines for every frame it kept throwing.  In an MP world
            // the tick's exception is swallowed here so the rest of the frame - and the queue drain - still runs;
            // outside one the exception is handed back and vanilla behaves exactly as before.
            private static readonly System.Collections.Generic.HashSet<string> _isoShapes =
                new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            private static readonly System.Collections.Generic.Dictionary<string, int> _isoCounts =
                new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

            static Exception? Finalizer(Exception? __exception)
            {
                if (__exception == null) return null;
                bool inMpWorld;
                try { inMpWorld = MPServer.IsRunning || MPClient.IsClientInWorld || MPClient.OfflineFork; }
                catch { inMpWorld = false; }
                if (!inMpWorld) return __exception;                       // vanilla: hand the game's own throw back
                bool clockHeld = false;
                try
                {
                    // Review H1: never bank time a swallowed tick did not spend (see _minuteBefore).
                    var gi = SaveGameManager.Current;
                    if (gi != null && _minuteBefore >= 0f && gi.Minute > _minuteBefore) { gi.Minute = _minuteBefore; clockHeld = true; }
                }
                catch { }
                try
                {
                    string shape = IsoShapeOf(__exception);
                    if (_isoCounts.Count >= 64 && !_isoCounts.ContainsKey(shape)) shape = "(other shapes)";   // review L4: bounded
                    _isoCounts.TryGetValue(shape, out int seen);
                    seen++;
                    _isoCounts[shape] = seen;
                    string held = clockHeld ? " - the clock is HELD until the fault clears" : "";
                    if (_isoShapes.Add(shape))
                        Plugin.Logger.LogWarning($"[TickIso] the game's main tick threw {shape} - swallowed so replication keeps running{held} (TICK-ISO-1).\n{__exception}");
                    else if (seen % 600 == 0)
                        Plugin.Logger.LogWarning($"[TickIso] the game's main tick threw {shape} - swallowed so replication keeps running{held} (TICK-ISO-1); {seen} times so far.");
                }
                catch { }
                return null;
            }

            /// <summary>Exception type plus its first two stack frames: one key per distinct failure site, so a storm
            /// of the same throw costs one line and a NEW site is never hidden behind it.</summary>
            private static string IsoShapeOf(Exception ex)
            {
                try
                {
                    string frames = "";
                    string st = ex.StackTrace ?? "";
                    int taken = 0;
                    foreach (var rawLine in st.Split('\n'))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0) continue;
                        frames += (taken == 0 ? " at " : " <- ") + line;
                        if (++taken == 2) break;
                    }
                    return ex.GetType().Name + frames;
                }
                catch { return ex.GetType().Name; }
            }
        }

        // SCHEDULE-2 guard (2026-09-17).  ScheduleHelper.RegenerateSimulatedScheduleDays (decompile :166-182) takes
        // CurrentSimulatedDayIndex = ScheduleDays.FindIndex(x => x == CurrentScheduleDay) - a REFERENCE match - and
        // GetSimulatedScheduleDays then indexes SimulatedScheduleDays with that value (:186), so a selected day object
        // that is no longer in the list gives -1 and throws ArgumentOutOfRangeException.  Replica refreshes now
        // reconcile the day objects IN PLACE (GameStatePatcher, SharedShopSchedule), but any other path that rebuilds
        // them reopens the same hole: re-point the selection at the list entry for the SAME weekday first.
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper),
                      nameof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.RegenerateSimulatedScheduleDays))]
        public static class Patch_ScheduleHelper_Regenerate_ReAnchor
        {
            private static readonly System.Collections.Generic.HashSet<string> _reAnchorLogged =
                new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            static void Prefix()
            {
                try
                {
                    var cur = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay;
                    var days = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.ScheduleDays;
                    if (days == null || days.Count == 0) return;
                    if (cur != null) foreach (var d in days) if (ReferenceEquals(d, cur)) return;   // the page is still anchored
                    var replacement = days[0];   // review L6: a NULL selection indexes [-1] just the same
                    if (cur != null) foreach (var d in days) if (d != null && d.day == cur.day) { replacement = d; break; }
                    UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay = replacement;
                    string addr = "";
                    try
                    {
                        var biz = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.Business;
                        if (biz != null && biz.buildingRegistration != null) addr = GameStateReader.AddressKey(biz.buildingRegistration);
                    }
                    catch { }
                    if (_reAnchorLogged.Add(addr))
                        Plugin.Logger.LogInfo($"[Schedule] selected day re-anchored after a replica refresh (SCHEDULE-2){(addr.Length > 0 ? $" at {addr}" : "")}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Schedule] re-anchor guard (SCHEDULE-2): {ex.Message}"); }
            }
        }

        // TICK-ISO-1 diagnostic (2026-09-17).  WorkstationController.FindChair (decompile :31-54) walks to the stack
        // root and recurses through childItemControllers testing ItemController.Occupied, whose seat branch (:262)
        // runs sittingPositions.All(x => x.childCount > 0) and throws NullReferenceException when a seat's
        // sittingPositions array holds a null slot.  That is the throw that escaped RunMainGameTick and stalled
        // replication (the "can't enter a friend's shop" family).  In an MP world answer "no chair found" instead of
        // throwing, and name the seat with the null slot - the permanent diagnostic for that family.
        [HarmonyPatch(typeof(Controllers.WorkstationController), nameof(Controllers.WorkstationController.FindChair))]
        public static class Patch_WorkstationController_FindChair_Iso
        {
            private static readonly System.Collections.Generic.HashSet<string> _chairLogged =
                new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            // sittingPositions is declared `internal` on ItemController: read it by reflection so a visibility or
            // rename change in a game update costs a missing detail, never a compile break or a throw in the guard.
            private static readonly System.Reflection.FieldInfo _fSittingPositions =
                AccessTools.Field(typeof(ItemController), "sittingPositions");

            static Exception? Finalizer(Exception? __exception, Controllers.WorkstationController __instance, ref ItemController? __result)
            {
                if (__exception == null) return null;
                bool inMpWorld;
                try { inMpWorld = MPServer.IsRunning || MPClient.IsClientInWorld || MPClient.OfflineFork; }
                catch { inMpWorld = false; }
                if (!inMpWorld) return __exception;
                __result = null;                                          // the caller's own "no chair" answer
                try
                {
                    string item = "?", id = "";
                    try { if (__instance != null) item = __instance.itemName; } catch { }
                    try
                    {
                        var ii = __instance != null ? __instance.ItemInstance : null;
                        if (ii != null && ii.id != null) id = ii.id.ToString();
                    }
                    catch { }
                    if (_chairLogged.Add(item + "|" + id))
                        Plugin.Logger.LogWarning($"[TickIso] FindChair threw at '{item}'{(id.Length > 0 ? $" ({id})" : "")} - {DescribeSeats(__instance)} (TICK-ISO-1 diagnostic): {__exception.GetType().Name}: {__exception.Message}");
                }
                catch { }
                return null;
            }

            /// <summary>Every seat under the workstation's stack root (the same walk FindChair does: up through
            /// parentItemController, then down through childItemControllers), with its sitting-slot count and the
            /// indices that are null - the slot the game's Occupied lambda dereferences.</summary>
            private static string DescribeSeats(Controllers.WorkstationController? ws)
            {
                try
                {
                    ItemController? root = ws;
                    int hops = 0;
                    while (root != null && root.parentItemController != null && hops++ < 32) root = root.parentItemController;
                    if (root == null) return "no stack root to inspect";
                    var found = new System.Collections.Generic.List<string>();
                    DescribeSeat(root, found, 0);
                    if (found.Count == 0) return "no seat found under the stack";
                    return string.Join("; ", found.ToArray());
                }
                catch { return "seat inspection failed"; }
            }

            private static void DescribeSeat(ItemController? ic, System.Collections.Generic.List<string> found, int depth)
            {
                if (ic == null || depth > 16 || found.Count >= 12) return;
                try
                {
                    var slots = _fSittingPositions != null ? _fSittingPositions.GetValue(ic) as UnityEngine.Transform[] : null;
                    if (slots != null && slots.Length > 0)
                    {
                        string nulls = "";
                        for (int i = 0; i < slots.Length; i++)
                            if (slots[i] == null) nulls += (nulls.Length > 0 ? "," : "") + i;
                        string sid = "";
                        try { var ii = ic.ItemInstance; if (ii != null && ii.id != null) sid = ii.id.ToString(); } catch { }
                        found.Add($"seat '{ic.itemName}'{(sid.Length > 0 ? $" ({sid})" : "")} has {slots.Length} sitting slot(s), "
                                  + (nulls.Length > 0 ? $"null sitting slot(s) [{nulls}]" : "none null"));
                    }
                }
                catch { }
                try
                {
                    if (ic.childItemControllers != null)
                        foreach (var child in ic.childItemControllers) DescribeSeat(child, found, depth + 1);
                }
                catch { }
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
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
            // PROTON-1 (bundle bamp-bug-20260907-115848, Linux/Proton): after this patch was
            // attached, the NATIVE method began throwing NullReferenceException on every call --
            // including the game's own callers -- 27,899 times in one session.  The cause is still
            // unknown (the old log printed ex.Message only).  A Finalizer swallows the throw and
            // serves the folder we cached before patching, so the mod's store paths never silently
            // go relative and the game's own save scan keeps working.
            private static bool _threwLogged;
            private static int  _threwSince;
            private static DateTime _threwNextAt = DateTime.MinValue;

            static void Postfix(ref string __result)
            {
                try
                {
                    var redirect = MPSaveCoordinator.LoadRedirectFolder;
                    if (!string.IsNullOrEmpty(redirect)) __result = redirect;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[MPSave] CurrentVersionFolderPath postfix (PROTON-1): {ex.Message}");
                }
            }

            static Exception? Finalizer(Exception __exception, ref string __result)
            {
                if (__exception == null) return null;
                string cached = MPSaveManager.CachedVersionFolderOrEmpty;
                if (cached.Length == 0) return __exception;   // nothing better to serve -- let it throw

                // Explicit null checks, not string.IsNullOrEmpty: on net48 the reference
                // assemblies carry no nullable annotations, so IsNullOrEmpty does not narrow
                // and every later use would flag CS8600/CS8601.
                string serve = cached;
                try
                {
                    var r = MPSaveCoordinator.LoadRedirectFolder;
                    if (r != null && r.Length > 0) serve = r;
                }
                catch { }
                __result = serve;

                if (!_threwLogged)
                {
                    _threwLogged = true;
                    _threwNextAt = DateTime.UtcNow.AddSeconds(30);
                    Plugin.Logger.LogWarning($"[MPSave] CurrentVersionFolderPath threw after patching - serving the cached folder (PROTON-1): {__exception}");
                }
                else
                {
                    _threwSince++;
                    if (DateTime.UtcNow >= _threwNextAt)
                    {
                        _threwNextAt = DateTime.UtcNow.AddSeconds(30);
                        Plugin.Logger.LogWarning($"[MPSave] CurrentVersionFolderPath still throwing: {_threwSince} more (PROTON-1)");
                        _threwSince = 0;
                    }
                }
                return null;
            }
        }

        // ── Patch: GameManager.RunMidNightAutoSave ────────────────────────────
        // The game's midnight autosave (fired at in-game 23:00 from RunMainGameTick).
        // 0.11 saved directly, ungated; 1.0 CHANGED THIS (sweep-2 S7, 2026-08-29): the
        // body now honours GameManager.preventAutoSave, deferring via
        // PendingMidnightAutoSave, which SetPreventAutoSave(false) drains later. None
        // of that machinery runs in MP — this Prefix returns false before the native
        // body, so no pending flag is ever set; single player runs the new native flow
        // untouched. Left alone in an MP session, the native save would still write a
        // vanilla "Recover Midnight.hsg" into the SINGLE-PLAYER folder every
        // in-game day.  In MP we replace it with a HOST-COORDINATED recover checkpoint
        // so it behaves like every other save (paired across machines, loadable):
        //   HOST   → MidnightRecoverSave() → HostSaveNow("midnight") → '<base>-recover'
        //            (broadcasts SaveNow so every client uploads its own .hsg, writes a
        //            manifest with all slots + carries forward absent members), deduped
        //            to once per in-game day.  Loadable on a separate host PC.
        //   CLIENT → do NOTHING; it saves only on the host's SaveNow, so a client can
        //            never make an orphan recover save the host didn't coordinate (the
        //            old per-machine path produced 505 client vs 72 host).
        //   OFFLINE FORK / single player (not hosting, not in an MP world — review S3: the client
        //            leg gates on IsClientInWorld so a 23:00 reconnect blip cannot leak a native
        //            Recover Midnight.hsg into the SP folder) → native midnight save runs unchanged.
        [HarmonyPatch(typeof(GameManager), "RunMidNightAutoSave")]
        public static class Patch_GameManager_RunMidNightAutoSave_RedirectInMp
        {
            static bool Prefix()
            {
                try
                {
                    if (MPServer.IsRunning)
                    {
                        // Host's authoritative midnight → one coordinated recover checkpoint; defer to a
                        // clean frame so the broadcast+save doesn't run mid-hourly-tick.
                        GameStatePatcher.EnqueueOnMainThread(() => MPSaveCoordinator.MidnightRecoverSave());
                        return false;
                    }
                    // Review S3 (2026-08-29): IsClientInWorld, not IsConnected — this suppresses a
                    // NATIVE pass over the loaded MP world (a stray Recover Midnight.hsg in the SP
                    // folder), so a reconnect blip at 23:00 must not hand it back to native
                    // (MPClient.cs:93-99 doctrine). Classified from the 48-gate list.
                    if (MPClient.IsClientInWorld)
                        return false;   // pure client: never self-saves the recover point (the host coordinates it)
                }
                catch { }
                return true;             // single player / offline fork — native save runs
            }
        }

        // ── Patch: GameManager.CheckAutoSave ──────────────────────────────────
        // Native SP-folder autosave suppression, moved OFF the preventAutoSave flag
        // (field 20260830-205553, bug-class member 5): the mod used to hold
        // GameManager.preventAutoSave=true every frame of an MP session, which was
        // safe in 0.11 — but 1.0 gave that SAME FLAG a new consumer, the quicksave
        // hotkey (GameManager.cs:911 `if (preventAutoSave) ShowError("notification_
        // cant_save")`), so every MP quicksave press showed "cannot save at this
        // time" and never reached MiniMenu.SaveGame (where our MenuSave reroute
        // lives). This prefix suppresses the native autosave DIRECTLY and gives the
        // flag back to its native owners (placement mode, blocking activities) —
        // quicksave now flows: key → MiniMenu.SaveGame → MenuSave reroute →
        // coordinated MP save, and during placement/activities it shows the native
        // refusal exactly as single player does.
        // GATE (review F2, corrected same day): IsClientInWorld, NOT IsConnected —
        // this suppresses a native pass over the loaded MP world (MPClient.cs:93-99
        // doctrine). The poll thread clears _connected a frame BEFORE the host-loss
        // notice arms SessionEnded (and a voluntary leave never arms it), so an
        // IsConnected gate left windows where native autosave could write the MP
        // character into the single-player folder. IsClientInWorld stays true
        // through drops (InMpGame is sticky) and goes false at the offline-fork
        // commit (InMpGame=false), so suppression still self-lifts there —
        // replacing the old sticky-flag AllowNativeAutosave call.
        // The midnight autosave needs nothing here: RunMidNightAutoSave is already
        // fully replaced in MP by its own prefix above, so its preventAutoSave
        // deferral latch can never set in a session.
        [HarmonyPatch(typeof(GameManager), "CheckAutoSave")]
        public static class Patch_GameManager_CheckAutoSave_SuppressInMp
        {
            static bool Prefix()
            {
                try
                {
                    if (MPServer.IsRunning || MPClient.IsClientInWorld)
                        return false;   // MP world loaded (incl. dropped-link + host-loss limbo): the coordinated save replaces native autosave
                }
                catch { }
                return true;            // single player / committed offline fork — native autosave runs
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

            /// <summary>The exact text our menu-open refresh last wrote into the save box
            /// (null = we haven't seeded it). At save-click, a box that still MATCHES this
            /// is UNEDITED — the player's intent is "save", not "rename" — so the save
            /// runs under the CURRENT session even if the base changed between open and
            /// click (review F2: the box is a display cache; commitments read live).</summary>
            internal static string? LastSeededBoxText;

            private static bool _panelMissLogged;

            /// <summary>TRUE when the pause-menu panel is on screen (public field
            /// `panel`, a UI Image — MiniMenu.cs:29). Fails CLOSED (false): wrongly
            /// closing runs UIs.CloseActiveUis (placement/time-machine cancel), while
            /// wrongly not-closing just leaves the menu open after a save. A resolve
            /// MISS is logged once (review F4): fail-closed here also means every
            /// menu save is treated as a quicksave — renames silently stop working —
            /// so a future game update renaming the field must be visible in the log.</summary>
            internal static bool IsPanelOpen(object miniMenu)
            {
                try
                {
                    var f = Resolve()?.GetField("panel",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (f == null)
                    {
                        if (!_panelMissLogged) { _panelMissLogged = true; Plugin.Logger.LogWarning("[MenuSave] MiniMenu.panel not found — panel-open checks fail closed (menu saves will behave like quicksaves; renames unavailable). Game update likely moved the field."); }
                        return false;
                    }
                    return f.GetValue(miniMenu) is UnityEngine.Component comp
                           && comp != null && comp.gameObject.activeSelf;
                }
                catch { return false; }
            }

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

            /// <summary>Write the save box (mirror of GetSaveName: property-first, field
            /// fallback, then the TMP text property). Truthful pre-fill design — the box
            /// shows the CURRENT session base when the menu opens.</summary>
            internal static void SetSaveName(object miniMenu, string text)
            {
                try
                {
                    var t = Resolve();
                    if (t == null) return;
                    const System.Reflection.BindingFlags BF =
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    object? input = t.GetProperty("saveGameName", BF)?.GetValue(miniMenu)
                                 ?? t.GetField("saveGameName", BF)?.GetValue(miniMenu);
                    if (input == null) { Plugin.Logger.LogWarning("[MenuSave] saveGameName not found on MiniMenu (box refresh skipped)."); return; }
                    var textProp = input.GetType().GetProperty("text",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    textProp?.SetValue(input, text ?? "");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuSave] write save name: {ex.Message}"); }
            }
        }

        // ── Truthful save-box pre-fill (user-approved design 2026-08-31) ──────
        // The box natively seeds ONCE at world load from the machine's LOCAL
        // remembered name (MiniMenu.Start) — a per-machine fossil in MP, and the
        // root of field 20260830-205553's accidental rename: a routine save click
        // carried a stale name and renamed the shared session for everyone.
        // Refresh the box with the CURRENT session base every time the menu
        // OPENS: an unedited box = a plain save of the current session; an edited
        // box = a deliberate rename, legitimate from ANY player (user ruling).
        // A never-saved host world shows a BLANK box until its first save mints
        // the date-stamp default (which then shows; user-allowed); a client
        // before its first pin is blank too — an empty name saves the current
        // session. Single-player: gate false, box untouched.
        [HarmonyPatch]
        public static class Patch_MiniMenu_Toggle_SeedSessionName
        {
            static System.Reflection.MethodBase? TargetMethod()
                => MiniMenuUtil.Resolve()?.GetMethod("Toggle",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, new Type[] { typeof(bool) }, null);   // Toggle() no-arg overload exists too — bind the bool one

            static void Postfix(object __instance, bool show)
            {
                try
                {
                    if (!show) return;
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                    string seed = MPSaveCoordinator.CurrentSessionBaseForDisplay();
                    MiniMenuUtil.SetSaveName(__instance, seed);
                    MiniMenuUtil.LastSeededBoxText = seed;   // the unedited-box reference for the save prefixes (review F2)
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MenuSave] box refresh: {ex.Message}"); }
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

        // RETIRED 2026-07-16: the escape-click null guard (protected the June
        // "Escape does nothing after exiting a car" NRE inside GameManager.
        // HandleEscapeClick).  Three generations never bound in the field: the
        // live build removed/restructured the method beyond both name lookup and
        // a void/no-param *Escape* scan (our decompile dump predates the change),
        // and every failed bind stamped PatchIssues noise into every report.
        // Field evidence for retirement: the guard was effectively OFF on all
        // installs for five weeks with zero recurrence of the original symptom.
        // TRIPWIRE: if "Escape does nothing while/after chat" reports return,
        // re-dump the CURRENT game build and rebuild the guard against it.

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
                    if (!MiniMenuUtil.IsPanelOpen(__instance))
                    {
                        // QUICKSAVE (the 1.0 hotkey calls SaveGame() with the menu never
                        // shown): no box was presented, so no rename intent can exist —
                        // ignore the fossil box text; an empty name saves the CURRENT
                        // session (truthful-name design, user-approved 2026-08-31).
                        name = "";
                        Plugin.Logger.LogInfo("[MenuSave] quicksave (panel closed) → saving the current session (box text ignored).");
                    }
                    else if (MiniMenuUtil.LastSeededBoxText != null && name == MiniMenuUtil.LastSeededBoxText)
                    {
                        // UNEDITED box (still exactly what our menu-open refresh wrote): intent is
                        // "save", not "rename" — empty ⇒ the CURRENT session, even if the base
                        // changed between open and click (review F2). Any edit falls through and
                        // is honored as a deliberate rename (user ruling 2026-08-31).
                        name = "";
                    }
                    Plugin.Logger.LogInfo($"[MenuSave] Save '{name}' → coordinated MP save (skipping SP save).");
                    MPSaveCoordinator.MenuSave(exiting: false, saveName: name);
                    // Review F3 (field 20260830-205553): close ONLY when the panel is
                    // actually open — native ExecuteSave gates its Toggle(false) on
                    // panel.gameObject.activeSelf (MiniMenu.cs:90). The QUICKSAVE key
                    // calls SaveGame() with the menu CLOSED, and an unconditional
                    // Toggle(false) runs UIs.CloseActiveUis: cancels placement mode,
                    // the time machine, and open cargo/activity panels on every press.
                    if (MiniMenuUtil.IsPanelOpen(__instance))
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
                if (MPSaveCoordinator.ExitWaitActive) { Plugin.Logger.LogInfo("[MPSave] Save & Exit already in progress — second click ignored (H-SERVE-1 r2)."); return false; }
                if (!MPServer.IsRunning && !MPClient.IsConnected) return true;   // SP → normal
                try
                {
                    string name = MiniMenuUtil.GetSaveName(__instance);
                    if (MiniMenuUtil.LastSeededBoxText != null && name == MiniMenuUtil.LastSeededBoxText)
                        name = "";   // unedited box ⇒ save-and-exit under the CURRENT session (review F2, same rule as Save)
                    Plugin.Logger.LogInfo($"[MenuSave] Save & Exit '{name}' → coordinated MP save, then quit (no SP save).");
                    MPSaveCoordinator.MenuSave(exiting: true, saveName: name);
                    // H-SERVE-1 (B): give connected members' SaveNow uploads a bounded chance to land before the process dies —
                    // poll the authoritative "landed" probe every frame, quit when all landed or after 10 s (events over timers:
                    // the state is re-read live; the deadline only bounds it). The quit-time flush then carries anyone still missing.
                    if (MPServer.IsRunning && MPServer.ConnectedStableIds().Count > 1 && MPCanvasUI.Instance != null)
                    {
                        var mm = __instance;
                        MPCanvasUI.Instance.StartCoroutine(MPSaveCoordinator.WaitForUploadsThen(10f, () => { try { MiniMenuUtil.QuitToDesktop(mm); } catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] deferred quit: {ex.Message}"); } }));
                    }
                    else MiniMenuUtil.QuitToDesktop(__instance);
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
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
                       && GameStatePatcher.IsSessionPlayerRivalId(owner);   // H-FORK-1: the live player list is empty offline; the roster survives
            }

            // (Probe prefix REMOVED 2026-06-12 — purchase path verified; the
            //  finalizer below stays as a last-line shield should any native
            //  OnPlaceOrder run slip past the MP finalizer's conditions.)

            static Exception? Finalizer(Exception __exception, Controllers.CashRegisterController __instance)
            {
                if (__exception == null) return null;
                try
                {
                    if ((MPServer.IsRunning || MPClient.IsClientInWorld || MPClient.OfflineFork) && InPlayerShop())   // H-FORK-1: holds in the offline fork
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

            private static float _nextGateLogAt;
            static void Postfix(EmployeeStationController __instance, ref bool __result)
            {
                if (__result) return;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // H-FORK-1 r2 (review #3): same gate as its sibling on this method
                try
                {
                    // Round-30 (WS3): the override now also admits ANY station inside a shop we hold a synced
                    // staff roster for — the injected real-id records + the synced shifts let the game's own
                    // engine staff every station, not just duty-broadcast tills.
                    bool duty = MPRegisterSync.IsEmployeeDutyStation(__instance);   // field 150521: identity-matched
                    bool roster = !duty && MPRegisterSync.HasRosterFor(MPRegisterSync.CurrentShopAddress);
                    if (!duty && !roster) return;
                    bool unstaffed = false;
                    try { unstaffed = __instance.employeeInstance == null; } catch { }
                    if (!unstaffed) return;
                    __result = true;
                    if (UnityEngine.Time.unscaledTime >= _nextGateLogAt)
                    {
                        _nextGateLogAt = UnityEngine.Time.unscaledTime + 5f;   // roster shops re-evaluate often — keep the log sane
                        Plugin.Logger.LogInfo($"[StaffEval] ShouldUpdateEmployee FORCED TRUE ({(duty ? "employee-duty station" : "roster shop")}, unstaffed).");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffEval] gate: {ex.Message}"); }
            }
        }

        /// <summary>Round-32 belt-and-braces: roster-injected staff records (another player's employees,
        /// mirrored locally so visitors see them work) must NEVER enter the local payroll. They carry wage 0,
        /// so the charge would be $0 anyway — but a $0 salary line for someone else's cashier in the local
        /// player's finances is wrong on its face. PayWage is the single native charge point.
        /// (Slice 5 note: injected records now carry the REAL wage for display — this skip is what makes
        /// that safe.)</summary>
        [HarmonyPatch(typeof(Entities.EmployeeInstance), nameof(Entities.EmployeeInstance.PayWage))]
        public static class Patch_EmployeeInstance_PayWage_SkipInjected
        {
            static bool Prefix(Entities.EmployeeInstance __instance)
            {
                try
                {
                    if ((MPServer.IsRunning || MPClient.IsClientInWorld || MPClient.OfflineFork)   // H-FORK-1: holds in the offline fork
                        && __instance != null && MPRegisterSync.IsInjectedStaff(__instance.id))
                        return false;
                }
                catch { }
                return true;
            }
        }

        /// <summary>Injected-record leak audit (user probe, 2026-07-09): an injected partner-staff record
        /// is a DISPLAY/STAFFING puppet — the real employee's life (satisfaction, complaints, worked
        /// hours) runs on the OWNER's machine. The native per-employee hourly was running ALL of it on
        /// the local copies too; the worst path: a complaint deadline expiring LOCALLY calls
        /// ComplaintResign() → Resign() — bogus "your employee is unhappy" messages about someone else's
        /// staff, and (with the slice-5 fire routing) a visitor's local complaint sim could genuinely
        /// fire the partner's REAL employee. One chokepoint kills the whole family.</summary>
        [HarmonyPatch(typeof(Entities.EmployeeInstance), nameof(Entities.EmployeeInstance.RunHourly))]
        public static class Patch_EmployeeInstance_RunHourly_SkipInjected
        {
            static bool Prefix(Entities.EmployeeInstance __instance)
            {
                try
                {
                    if ((MPServer.IsRunning || MPClient.IsClientInWorld || MPClient.OfflineFork)   // H-FORK-1: holds in the offline fork
                        && __instance != null
                        // NOTIFY-2 fold G (2026-09-17): a synthetic duty stand-in is a staffing puppet with
                        // no life either - and a resignation from one would land in the batch
                        // NotificationRelay.InOwnStaffBatch reads as "this person is mine".
                        && (MPRegisterSync.IsInjectedStaff(__instance.id)
                            || (__instance.id ?? "").StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)))
                        return false;
                }
                catch { }
                return true;
            }
        }

        /// <summary>Injected-record leak audit (2026-07-09): "employ N people" personal goals count the
        /// unfiltered employee query — 113 injected partner records on the reporter's client would
        /// complete employment goals instantly. Re-run the goal's own query and subtract the injected.</summary>
        [HarmonyPatch(typeof(EmploymentGoal), "GetValue")]
        public static class Patch_EmploymentGoal_ExcludeInjected
        {
            static void Postfix(EmploymentGoal __instance, ref int __result)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return;   // H-FORK-1: holds in the offline fork
                    if (__result <= 0) return;
                    var matches = Helpers.EmployeeHelper.GetEmployeeInstances(new EmployeeInstancesQueryInfo
                    {
                        excludeBeingReplaced = true,
                        isAssignedToAnyWorkShift = __instance.requiresToBeScheduled,
                        withSkills = (!__instance.requireSkill) ? null : new string[1] { __instance.skill }
                    });
                    int injected = 0;
                    foreach (var e in matches)
                        if (e != null && MPRegisterSync.IsInjectedStaff(e.id)) injected++;
                    if (injected > 0) __result = Math.Max(0, __result - injected);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffRoster] employment-goal filter: {ex.Message}"); }
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
                    if (!MPRegisterSync.IsEmployeeDutyStation(__instance)) return;
                    Plugin.Logger.LogInfo(__result == null
                        ? "[StaffEval] GetEmployeeWorkShift → null"
                        : $"[StaffEval] GetEmployeeWorkShift → shift(emp='{__result.employeeId}' station='{__result.itemInstanceId}' {__result.startingHour}-{__result.endingHour})");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[StaffEval] shift probe: {ex.Message}"); }
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

        // ── Patch: VehicleParkingHelper ghost shield (2026-06-25; list trimmed to Update 2026-08-29).
        // NOTE ON THE HEADER BELOW: it was written when this class bound the TRIGGER callbacks, and
        // that premise no longer applies to anything it binds — after the trim it patches Update
        // alone, which IS gated by Behaviour.enabled. Kept anyway, because the sibling Start-skip's
        // `enabled = false` only covers a ghost that was ALREADY stripped when Start ran; a vehicle
        // stripped AFTER Start keeps running Update with a live `enabled`. That is the case Update
        // still guards. (Also: Update dereferencing _carController on its first line is NOT a 1.0
        // change — 0.11's VehicleParkingHelper.cs:93-95 did the same.)
        // The Start-skip above disables the component on ghosts, but Unity still delivers collider
        // TRIGGER callbacks regardless of Behaviour.enabled, and they deref the same stripped
        // CarController → an UNHANDLED NullReferenceException per ghost-vs-parking-trigger contact
        // (~28× in one client session, raw 2-line stack each, buries real events). Same ghost test as
        // the Start patch: no CarController in parents ⇒ ghost ⇒ skip the handler so the NRE is never
        // thrown. Real vehicles (CarController present) run unchanged.
        [HarmonyPatch]
        public static class Patch_VehicleParkingHelper_TriggerSkipOnGhost
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                // 1.0 REWROTE auto-parking (2026-08-29): OnTriggerEnter and CarOverlapsInSpot are
                // gone; spot selection moved to a POLLED path whose Update() derefs _carController on
                // its very first line (`if (_carController.controlledByPlayer)`) — unguarded, so a
                // ghost NREs every frame there instead. "Update" is therefore in the list now.
                // The loop skips names that do not resolve. (It no longer buys 0.11 compatibility —
                // this assembly now has hard 1.0-only bindings, e.g. ItemHelper.ClearPriceCaches and the
                // 5-arg EnterBuilding. The tolerant style is still worth keeping for the NEXT update.)
                // The list is written out EXPLICITLY because when OnTriggerEnter vanished in 1.0 the
                // class still bound OnTriggerExit, so the gap reported as neither "failed" nor "dead":
                // it degraded in total silence. That is why the list is explicit.
                // LIST TRIMMED 2026-08-29 after reading 1.0's VehicleParkingHelper end to end
                // instead of guessing from method NAMES. What the four-name list actually was:
                //   "OnTriggerStay"  - never existed in EITHER version. Pure fiction.
                //   "OnTriggerEnter" - existed in 0.11 (:33), deleted in 1.0.
                //   "OnTriggerExit"  - exists (1.0 :99) but its body now touches only `other` and
                //                      `availableAutoParkSpot`. It no longer dereferences
                //                      _carController, so patching it protected NOTHING.
                //   "Update"         - the only real one: 1.0 :176 opens with
                //                      `if (_carController.controlledByPlayer)`, unguarded.
                // A name surviving a version tells you nothing about what its BODY still does.
                var t = typeof(VehicleParkingHelper);
                foreach (var name in new[] { "Update" })
                {
                    var m = t.GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (m != null) yield return m;
                }
            }

            static bool Prefix(VehicleParkingHelper __instance)
            {
                // MP GATE: this is a PER-FRAME prefix and GetComponentInParent walks the hierarchy
                // every call, so single-player - which has no ghosts to shield - should pay nothing.
                // IsClientInWorld, NOT IsConnected (2026-08-29). MPClient.cs:93-99 states the rule: "Suppressions of native world-mutating passes, SHIELDS OVER MOD-CREATED STATE, and replica protections must gate on THIS instead of IsConnected." IsConnected goes false the instant a link drops, while the MP world - and every ghost in it - is still loaded.
                // Concretely here: MPServer.Stop() ("Stop hosting" while staying in the world) despawns
                // NO ghosts, so a host who stops hosting keeps every remote-vehicle ghost with
                // IsRunning false and IsConnected false. IsClientInWorld covers that and the drop window.
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                try { return __instance.GetComponentInParent<CarController>() != null; }   // ghost (no CarController) → skip
                catch { return true; }
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
            // Family sweep 2026-08-31 (07-bug-classes): OnPlaceOrder OVERRIDES dispatch PAST a
            // patch on the CashRegister-declared method — tickets (TicketBoothController) and
            // haircuts (HairdresserChairController) ran NATIVE on the replica: the buyer paid
            // locally and the revenue booked into the buyer's COPY of the business (owner never
            // credited). All three declared methods are intercepted now; the delivery half is
            // per purchase SHAPE (see the Beat completion + the Service branch in the prefix).
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var owners = new[] { typeof(Controllers.CashRegisterController),
                                     typeof(Controllers.TicketBoothController),
                                     typeof(Controllers.HairdresserChairController) };
                foreach (var t in owners)
                {
                    System.Reflection.MethodBase? m = null;
                    try
                    {
                        foreach (var mm in t.GetMethods(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                            if (mm.Name == "OnPlaceOrder" && mm.DeclaringType == t) { m = mm; break; }
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSale] target {t.Name}: {ex.Message}"); }
                    if (m != null) yield return m;
                    else Plugin.Logger.LogWarning($"[MPSale] no declared OnPlaceOrder on {t.Name} — that shape stays native (watch for family recurrence).");
                }
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
            // Family sweep 2026-08-31: delivery is per-SHAPE — the pending order also carries
            // resolved per-unit prices (SaleItem has none) for the shape-A bag mint, and a
            // ticket marker for shape B.
            private static readonly System.Collections.Generic.List<(string name, int amount, float price)> _pendingPriced = new();
            private static bool _pendingTicket;

            /// <summary>Review-family #3: release the hairdresser queue panel + its navigation
            /// blocker (only PurchaseUI.Close clears both; the null playerCustomer makes the
            /// resulting OnOrderCancel a safe no-op).</summary>
            private static void CloseHairPanel()
            {
                try { InstanceBehavior<UI.UIs>.Instance.playerHUD.purchaseUI.Close(); } catch { }
            }

            /// <summary>One-frame deferral for the shape-C appearance commit — ChangeHair assigns
            /// _newCharacterAppearance on the line AFTER the invoke that reached our prefix, so a
            /// synchronous commit would apply the PREVIOUS haircut (null on a first cut). Hosted on
            /// MPCanvasUI.Instance (persistent, DontDestroyOnLoad) — a chair-hosted coroutine dies
            /// silently if the interior apply respawns the chair in that one frame (recheck A-3).</summary>
            private static System.Collections.IEnumerator InvokeHairCommitNextFrame(Action commit)
            {
                yield return null;
                try { commit(); } catch (Exception cex) { Plugin.Logger.LogWarning($"[MPSale] hair commit: {cex.Message}"); }
            }

            /// <summary>Review-family #4: intake for the cinema/theater TICKET KIOSK — a
            /// PurchaseUI-driven Producer (not a station) whose private OnPlaceOrder would Pay
            /// into the replica. Interacted with in place, so no walk-up — straight to the
            /// service beat; the Beat's PurchaseUI-open cancel check applies as at a booth.</summary>
            internal static void IntakeExternalTicketOrder(UnityEngine.Vector3 pos,
                System.Collections.Generic.List<BigAmbitions.Items.CargoInstance> cargo, string owner)
            {
                try
                {
                    string act0 = "none";
                    try { act0 = MPRestSync.CurrentActivityName() ?? "none"; } catch { }
                    float total = 0f;
                    var desc = new System.Text.StringBuilder();
                    _pendingItems.Clear(); _pendingPriced.Clear();
                    _pendingTicket = true;
                    if (cargo != null)
                        foreach (var c in cargo)
                        {
                            if (c == null) continue;
                            float price = MPRegisterSync.GetShopPrice(c.itemName);
                            if (price < 0f) price = (float)c.pricePerUnit;
                            total += c.amount * price;
                            if (desc.Length < 160) desc.Append($"{c.itemName} x{c.amount}, ");
                            _pendingItems.Add(new SaleItem { ItemName = c.itemName ?? "", Amount = c.amount });
                            _pendingPriced.Add((c.itemName ?? "", c.amount, price));
                        }
                    _pendingTotal = total;
                    _pendingDesc = desc.ToString().TrimEnd(' ', ',');
                    _pendingOwner = owner;
                    _pendingAddress = MPRegisterSync.CurrentShopAddress;
                    _pendingAct0 = act0;
                    _registerPos = pos;
                    _walkAgent = null;
                    BeginBeat();
                    Plugin.Logger.LogInfo($"[MPSale] kiosk ticket order taken (${total:F2}) — ringing up...");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSale] kiosk intake: {ex.Message}"); }
            }

            // GUEST-PARITY-2b: one line per address where a permitted guest's services were priced free.
            private static readonly System.Collections.Generic.HashSet<string> _freeSvcLogged =
                new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            static bool Prefix(EmployeeStationController __instance,
                               System.Collections.Generic.List<BigAmbitions.Items.CargoInstance> orderedCargoInstances)
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                string owner = MPRegisterSync.CurrentShopOwner;
                if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) return true;
                if (!MPRestSync.AllPlayers().Contains(owner)) return true;   // AI shops fully native

                // ── Family shape C: SERVICES (hairdresser) — no PurchaseUI walk-up (the
                // buyer sits in the chair), no goods. The services are computed INSIDE the
                // method being swallowed, from the chair's public changed-flags — so compute
                // them here, charge, credit the owner, and skip the native queue (whose serve
                // would Pay into the buyer's REPLICA registration). The chair UI only PREVIEWS
                // on a mannequin — the real look is committed by the deferred onHairChangeAction
                // below; the appearance sync then carries it to other machines.
                // Recheck A-2: the panel close lives in a FINALLY — every exit (no changes, no
                // product, success, throw) must release the queue panel or the buyer is stuck
                // charged-and-wedged behind it.
                if (__instance is Controllers.HairdresserChairController hc)
                {
                    try
                    {
                        // GUEST-PARITY-2b (M3): native OnPlaceOrder prices BOTH fees with the owner's own rule,
                        // IsPlayerOwnedBusiness ? 0f : price (HairdresserChairController.cs:191/:246). This routed
                        // MP path never read ownership: a permitted guest saw the native flip's $0 preview in the
                        // chair UI and was then charged the FULL price through RemoteSalePayload. Apply the same
                        // rule here — permitted here means the shop is one this player may run, so it is free.
                        bool svcFree = false;
                        try { svcFree = CinemaGuestParity.PermittedHere(out _); } catch { }
                        var fees = new System.Collections.Generic.List<(string name, int amount, float price)>();
                        if (hc.hasPlayerHairVariantChanged || hc.hasPlayerEyebrowVariantChanged || hc.hasPlayerBeardVariantChanged)
                            fees.Add(("ba:itemname_haircuttingfee", 1, svcFree ? 0f : ItemHelper.GetPriceOnCurrentBusiness("ba:itemname_haircuttingfee")));
                        if (hc.hasPlayerHairColorChanged || hc.hasPlayerEyebrowColorChanged || hc.hasPlayerBeardColorChanged)
                            fees.Add(("ba:itemname_hairchemicalfee", 1, svcFree ? 0f : ItemHelper.GetPriceOnCurrentBusiness("ba:itemname_hairchemicalfee")));
                        if (fees.Count == 0) return false;   // nothing changed — charge nothing (finally releases the panel)
                        if (svcFree && _freeSvcLogged.Add(MPRegisterSync.CurrentShopAddress))   // never null (MPRegisterSync.cs:79); a ?? here re-flags the property maybe-null for :8428
                            Plugin.Logger.LogInfo("[Order] haircut fees 0 for a permitted guest (GUEST-PARITY-2b)");

                        // Review-family #7a: native consumes one hair-care product per service and
                        // ABORTS UNPAID when the shop has none (HairdresserStylistEmployee:171-175 →
                        // StopServingPlayer:258-264, its own notification). Mirror: the replica's
                        // item cargo is synced, so check it; on none — native's own notification,
                        // panel closed, nothing charged, and the preview simply never commits.
                        bool haveProduct = false;
                        try
                        {
                            var regH = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                            if (regH?.itemInstances != null)
                                foreach (var kvH in regH.itemInstances)
                                {
                                    var cargoH = kvH.Value?.cargoInstances;
                                    if (cargoH == null) continue;
                                    foreach (var cH in cargoH)
                                        if (cH != null && cH.itemName == "ba:itemname_haircareproduct" && cH.amount > 0) { haveProduct = true; break; }
                                    if (haveProduct) break;
                                }
                        }
                        catch { }
                        if (!haveProduct)
                        {
                            try
                            {
                                UI.Notification.Notifications.Show(UI.Notification.NotificationType.Error, "notification_no_items_available",
                                    new System.Collections.Generic.Dictionary<string, string> { { "itemname", "ba:itemname_haircareproduct" } });
                            }
                            catch { }
                            Plugin.Logger.LogInfo("[MPSale] service refused — no hair-care product in the shop (mirrors native StopServingPlayer; nothing charged).");
                            return false;   // finally releases the panel
                        }

                        float svcTotal = 0f; var svcSale = new RemoteSalePayload
                        { BuyerId = MPConfig.PlayerId, OwnerId = owner, Address = MPRegisterSync.CurrentShopAddress };
                        var svcDesc = new System.Text.StringBuilder();
                        foreach (var f in fees)
                        {
                            svcTotal += f.amount * f.price;
                            svcSale.Items.Add(new SaleItem { ItemName = f.name, Amount = f.amount });
                            svcDesc.Append($"{f.name} x{f.amount}, ");
                        }
                        // #7a: ship the consumed product too — the OWNER's machine (stock truth)
                        // decrements it via the normal RemoteSale stock path.
                        svcSale.Items.Add(new SaleItem { ItemName = "ba:itemname_haircareproduct", Amount = 1 });
                        svcSale.Total = svcTotal;
                        svcSale.Desc  = svcDesc.ToString().TrimEnd(' ', ',');
                        MPHub.ApplyMoneyDelta(-svcTotal, $"Services at {MPRegisterSync.CurrentShopAddress}");
                        if (MPServer.IsRunning) MPServer.HandleRemoteSale(svcSale, MPConfig.PlayerId);
                        else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.RemoteSale, MPConfig.PlayerId, svcSale));

                        // Review-family #2 (gating): the chair UI only PREVIEWS on a mannequin; the
                        // real appearance commit is onHairChangeAction — handed to the chair in
                        // ChangeHair, fired natively ONLY at the stylist's serve-finish
                        // (HairdresserStylistEmployee:210), which we skip. Fire it ourselves,
                        // DEFERRED ONE FRAME: ChangeHair assigns _newCharacterAppearance on the line
                        // AFTER the invoke that reached us, so a synchronous call here would commit
                        // the PREVIOUS haircut (null on a first-ever cut — appearance wipe).
                        var commit = hc.onHairChangeAction;
                        if (commit != null)
                        {
                            // Recheck A-3: host the one-frame deferral on the mod's PERSISTENT
                            // UI object, not the chair — the interior apply destroys/respawns
                            // ItemControllers on any diff, and a coroutine dies with its host
                            // WITHOUT raising: charged, look silently never applied.
                            var host = (UnityEngine.MonoBehaviour?)MPCanvasUI.Instance ?? hc;
                            host.StartCoroutine(InvokeHairCommitNextFrame(commit));
                        }
                        else Plugin.Logger.LogWarning("[MPSale] no hair-commit callback on the chair — charged, but the look may not apply (report this).");

                        Plugin.Logger.LogInfo($"[MPSale] service purchase finalized: total=${svcTotal:F2} owner='{owner}' items='{svcSale.Desc}' + haircareproduct x1 (family shape C — commit deferred one frame, panel closed).");
                    }
                    catch (Exception hx) { Plugin.Logger.LogWarning($"[MPSale] service checkout: {hx.Message}"); }
                    finally
                    {
                        // Review-family #3 + recheck A-2: the queue label sets PurchaseUI.IsPanelOpen
                        // and a navigation blocker only PurchaseUI.Close clears — EVERY exit path
                        // must close it, including a throw between the debit and the finalize.
                        CloseHairPanel();
                    }
                    return false;
                }

                try
                {
                    string act0 = "none";
                    try { act0 = MPRestSync.CurrentActivityName() ?? "none"; } catch { }

                    float total = 0f;
                    var desc = new System.Text.StringBuilder();
                    _pendingItems.Clear();
                    _pendingPriced.Clear();
                    _pendingTicket = __instance is Controllers.TicketBoothController;
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
                            _pendingPriced.Add((c.itemName ?? "", c.amount, price));
                            // Belt for shape B: a ticket item sold anywhere is ticket-shaped.
                            try
                            {
                                var itT = BigAmbitions.Items.ItemsGetter.GetByName(c.itemName);
                                if (itT != null && itT.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isticket)) _pendingTicket = true;
                            }
                            catch { }
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

                    // If we're a CLIENT and the host link is down, the RemoteSale below would no-op on a dead
                    // socket — the owner would never be credited nor the stock decremented, yet the local debit
                    // would still run (cash destroyed, no rollback). Abort and charge nothing; the buyer retries
                    // once reconnected. (Mirrors the "buyer cancelled" branch above.)
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld)
                    {
                        Plugin.Logger.LogWarning("[MPSale] host link down at ring-up — sale aborted, nothing charged (retry after reconnect).");
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

                    // Round-211b (field 20260731-215117 + rig repro): THE bug — this
                    // finalize marked the basket's cargo PAID but never converted the
                    // container, so the buyer walked off with a paid basket instead of
                    // a bag. (Both field cases were THIS path: the [Hub] money line is
                    // ApplyMoneyDelta's signature — the native serve branches never
                    // charged at all.) Native's own conversion, gated on the held-item
                    // DATA carrying the shopping-container tag.
                    try
                    {
                        var hands = Helpers.PlayerHelper.ItemInstanceInHands;
                        var it = hands?.ItemCached;
                        if (it != null && it.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isshoppingcontainer)
                            && (hands.cargoInstances?.Count ?? 0) > 0)
                        {
                            SelfServiceEmployee.ConvertPlayerShoppingContainerToPaperBag();
                            Plugin.Logger.LogInfo("[MPSale] held container converted to a paper bag (round-211b).");
                        }
                    }
                    catch (Exception cx) { Plugin.Logger.LogWarning($"[MPSale] bag conversion: {cx.Message}"); }

                    // ── Family delivery, per shape (07-bug-classes sweep 2026-08-31) ──
                    // Shape B (tickets): native Order.Pay's buyer-side delivery is a save flag +
                    // blocker refresh (Order.cs:56) — mirror exactly that, never Pay itself (Pay
                    // books revenue into the local REPLICA registration).
                    // Shape A (orders — restaurants/fast food/bar drinks): the buyer's hands are
                    // EMPTY by native requirement and the goods exist only in the order — mirror
                    // native MakeFullServiceSelfPurchase's delivery half: mint the paper bag and
                    // fill it with the ordered items, already PAID (native bags drinks too).
                    // Vehicle checkouts need nothing (cargo already possessed); basket checkouts
                    // were converted above (round-211b).
                    try
                    {
                        // Review-family #10: delivery is ADDITIVE — a mixed order (ticket + goods)
                        // delivers both halves; each line routes by its own isticket tag.
                        var goodsLines = new System.Collections.Generic.List<(string name, int amount, float price)>();
                        bool anyTicket = false;
                        foreach (var itD in _pendingPriced)
                        {
                            bool isTicket = false;
                            try
                            {
                                var itT2 = BigAmbitions.Items.ItemsGetter.GetByName(itD.name);
                                isTicket = itT2 != null && itT2.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isticket);
                            }
                            catch { }
                            if (isTicket) anyTicket = true; else goodsLines.Add(itD);
                        }
                        if (anyTicket || _pendingTicket)
                        {
                            // Shape B: mirror native Order.Pay's buyer half exactly (Order.cs:54-58) —
                            // the flag + blocker refresh, never Pay itself (Pay books revenue into
                            // the local REPLICA registration).
                            SaveGameManager.Current.hasCinemaTheaterTicket = true;
                            try { Buildings.Retail.Businesses.CinemaTheater.TicketEntryBlocker.UpdateBlockers(); } catch { }
                            Plugin.Logger.LogInfo("[MPSale] ticket delivered — entry flag set (family shape B).");
                        }
                        bool usingVeh = false; try { usingVeh = Helpers.PlayerHelper.IsUsingVehicle; } catch { }
                        if (!usingVeh && Helpers.PlayerHelper.ItemInstanceInHands == null && goodsLines.Count > 0)
                        {
                            var chD = Helpers.PlayerHelper.PlayerController?.Character;
                            if (chD != null)
                            {
                                // Review-family #1: SetPaperbag is the VISUAL half only (prefab +
                                // SetHandContent — it never writes CharacterData.itemInHands). Native
                                // always pairs it with the DATA half (FullServiceEmployee
                                // .UpdatePlayerPurchase:274-285): a fresh bag ItemInstance, cargo
                                // added, assigned through PlayerHelper.ItemInstanceInHands (whose
                                // setter wires the item panel + cargo callbacks). Mirror BOTH halves.
                                FullServiceEmployee.SetPaperbag(chD);
                                var bagInst = new BigAmbitions.Items.ItemInstance(BigAmbitions.Items.ItemsGetter.GetRandomBag());
                                foreach (var itD in goodsLines)
                                    bagInst.AddToCargo(new BigAmbitions.Items.CargoInstance(itD.name, itD.amount, itD.price, paid: true));
                                Helpers.PlayerHelper.ItemInstanceInHands = bagInst;
                                Plugin.Logger.LogInfo($"[MPSale] order delivered: paper bag with {goodsLines.Count} item line(s) (family shape A).");
                            }
                        }
                    }
                    catch (Exception dvx) { Plugin.Logger.LogWarning($"[MPSale] delivery: {dvx.Message}"); }

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

        // ── Review-family #4: fifth family member — the cinema/theater TICKET KIOSK.
        // Its OnPlaceOrder is a PRIVATE method on a Producer (not a station), wired directly
        // as the PurchaseUI callback, ending in order.Pay(replica) — buyer charged, revenue
        // into the buyer's copy, owner uncredited. Route it through the same pending machine
        // as the booth (shape B: ticket flag + blocker refresh at the beat).
        [HarmonyPatch(typeof(Controllers.TicketKioskController), "OnPlaceOrder")]
        public static class Patch_TicketKiosk_MPOrder
        {
            static bool Prefix(Controllers.TicketKioskController __instance,
                               System.Collections.Generic.List<BigAmbitions.Items.CargoInstance> orderedCargoInstances)
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                string owner = MPRegisterSync.CurrentShopOwner;
                if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) return true;
                if (!MPRestSync.AllPlayers().Contains(owner)) return true;   // AI cinemas fully native
                Patch_MPOrderFinalizer.IntakeExternalTicketOrder(__instance.transform.position, orderedCargoInstances, owner);
                return false;
            }
        }

        // ── Family shape D (07-bug-classes sweep 2026-08-31): GYM shelf grabs ──
        // PlayerItemPurchaser.GrabItem's Gym branch charges the buyer IMMEDIATELY at grab
        // (ChangeMoneySafe, cargo paid:true) — no register visit, so the MPSale finalizer
        // never fires. In another SESSION PLAYER's gym (replicas DO get purchaser-enabled
        // display shelves — GameStatePatcher:1488 is not business-type-gated) the buyer paid
        // and the owner was never credited. Mirror the OWNER's half only: diff the paid
        // cargo the grab added into the buyer's hands and send RemoteSale (the buyer's
        // charge and the goods are already native and correct).
        [HarmonyPatch(typeof(Controllers.PlayerItemPurchaser), nameof(Controllers.PlayerItemPurchaser.GrabItem))]
        public static class Patch_GymGrab_OwnerCredit
        {
            private static bool _armed;
            private static readonly System.Collections.Generic.Dictionary<string, (int amount, float price)> _before = new();

            private static void SnapshotPaidHands(System.Collections.Generic.Dictionary<string, (int amount, float price)> into)
            {
                into.Clear();
                try
                {
                    var cargo = Helpers.PlayerHelper.ItemInstanceInHands?.cargoInstances;
                    if (cargo == null) return;
                    foreach (var c in cargo)
                    {
                        if (c == null || !c.paid || string.IsNullOrEmpty(c.itemName)) continue;
                        into.TryGetValue(c.itemName, out var cur);
                        into[c.itemName] = (cur.amount + c.amount, (float)c.pricePerUnit);
                    }
                }
                catch { }
            }

            static void Prefix()
            {
                _armed = false;
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                    string owner = MPRegisterSync.CurrentShopOwner;
                    if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) return;
                    if (!MPRestSync.AllPlayers().Contains(owner)) return;
                    var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    if (Helpers.BusinessTypeHelper.GetData(reg)?.customerType != CustomerType.Gym) return;
                    SnapshotPaidHands(_before);
                    _armed = true;
                }
                catch { }
            }

            static void Postfix(Controllers.PlayerItemPurchaser __instance)
            {
                if (!_armed) return;
                _armed = false;
                try
                {
                    var after = new System.Collections.Generic.Dictionary<string, (int amount, float price)>();
                    SnapshotPaidHands(after);
                    var sale = new RemoteSalePayload
                    {
                        BuyerId = MPConfig.PlayerId,
                        OwnerId = MPRegisterSync.CurrentShopOwner,
                        Address = MPRegisterSync.CurrentShopAddress,
                    };
                    float total = 0f; var desc = new System.Text.StringBuilder();
                    foreach (var kv in after)
                    {
                        _before.TryGetValue(kv.Key, out var was);
                        int added = kv.Value.amount - was.amount;
                        if (added <= 0) continue;
                        sale.Items.Add(new SaleItem { ItemName = kv.Key, Amount = added });
                        total += added * kv.Value.price;
                        desc.Append($"{kv.Key} x{added}, ");
                    }
                    if (sale.Items.Count == 0) return;   // grab refused / nothing paid — nothing to mirror
                    // Review-family #7b: a merge into an existing paid line rewrites pricePerUnit
                    // to a weighted AVERAGE — the post-merge diff price ≠ what the buyer was
                    // actually charged. The purchaser's own TotalPrice IS the exact charge for
                    // this grab (one item line per grab); use it when the diff is a single line.
                    if (sale.Items.Count == 1)
                    {
                        try { float tp = __instance.TotalPrice; if (tp > 0f) total = tp; } catch { }
                    }
                    sale.Total = total;
                    sale.Desc  = desc.ToString().TrimEnd(' ', ',');
                    if (MPServer.IsRunning) MPServer.HandleRemoteSale(sale, MPConfig.PlayerId);
                    else MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.RemoteSale, MPConfig.PlayerId, sale));
                    Plugin.Logger.LogInfo($"[MPSale] gym grab mirrored to the owner: total=${total:F2} items='{sale.Desc}' (family shape D — buyer charge was native).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSale] gym grab mirror: {ex.Message}"); }
            }
        }

        // ── [SelfCheckout] (2026-06-12, user direction: "do the act identical
        // to the normal buying process and ring it up").  In another lobby
        // player's shop, clicking a register THAT PLAYER IS WORKING routes to
        // the game's own SELF-CHECKOUT flow (InteractAsSelfService →
        // MakeFullServiceSelfPurchase) instead of the employee-service queue
        // that cannot be served locally.  Native UI, native payment, native
        // bagging — the worker's avatar stands at the counter throughout. ─────
        // ── Round-211: charged-but-unbagged backstop (field 20260731-215117) ─────
        // Tali paid $15,500 in Just JP's staffed shop and kept the full basket — no
        // bag, no exception. The only branch fitting all evidence: the staffed-serve
        // routine's "holding a shopping container?" check reads the VISUAL hand
        // transform for a tagged ItemController at serve start; when it misses
        // (intermittent, trigger unnamed — the carry-template suspect was read and
        // exonerated), Pay still runs and the bag conversion is silently skipped.
        // UpdatePlayerPurchase executes on EVERY successful player charge, in both
        // flag outcomes — so this postfix checks the DATA (the held ItemInstance's
        // container tag, the check native should have used): container still in
        // hands after a completed purchase ⇒ run native's own conversion, and log
        // the visual-hand state loudly so the next occurrence names the trigger.
        // Healthy purchases hold a BAG by now → early return, zero behavior change.
        [HarmonyPatch(typeof(SelfServiceEmployee), nameof(SelfServiceEmployee.UpdatePlayerPurchase))]
        public static class Patch_PurchaseBagBackstop
        {
            static void Postfix()
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;   // SP: native's own ground
                    var hands = PlayerHelper.ItemInstanceInHands;
                    if (hands == null) return;
                    BigAmbitions.Items.Item? item = null;
                    try { item = hands.ItemCached; } catch { }
                    if (item == null || !item.HasTag(BigAmbitions.Tags.TagRef.Itemtag.isshoppingcontainer)) return;   // bag/nothing — healthy

                    int cargo = 0, unpaid = 0;
                    try
                    {
                        cargo = hands.cargoInstances?.Count ?? 0;
                        if (hands.cargoInstances != null)
                            foreach (var c in hands.cargoInstances) if (c != null && !c.paid) unpaid++;
                    }
                    catch { }
                    // The exact input the native serve check consumed — captured at the
                    // failure moment so the intermittent trigger names itself.
                    string handGo = "<null>"; bool hasIc = false;
                    try
                    {
                        var hc = InstanceBehavior<GameManager>.Instance?.playerController?.Character?.GetHandContent();
                        if (hc != null) { handGo = hc.name; hasIc = hc.TryGetComponent<ItemController>(out _); }
                    }
                    catch { }
                    Plugin.Logger.LogWarning(
                        $"[SelfCheckout] BAG BACKSTOP (round-211): purchase completed but hands still hold container "
                        + $"'{hands.itemName}' (cargo={cargo}, unpaid={unpaid}; visual hand GO='{handGo}' itemController={hasIc}) "
                        + $"— running the native conversion.");
                    if (cargo > 0) SelfServiceEmployee.ConvertPlayerShoppingContainerToPaperBag();   // native semantics: whole container bags
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[SelfCheckout] bag backstop: {ex.Message}"); }
            }
        }

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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
                // Review-family #5: a TICKET BOOTH inherits this Interact but must NEVER be
                // rerouted to self-checkout — with someone on duty at the booth the reroute
                // called InteractAsSelfService, which is a no-op with empty hands = silent
                // dead click; the booth's own CanOrder → ticket UI is the real flow.
                if (__instance is Controllers.TicketBoothController) return true;
                try
                {
                    string owner = MPRegisterSync.CurrentShopOwner;
                    if (string.IsNullOrEmpty(owner) || owner == MPConfig.PlayerId) return true;
                    if (!MPRestSync.AllPlayers().Contains(owner)) return true;          // AI shops native
                    if (!MPRegisterSync.IsStaffedByOtherPlayer(__instance))   // field 150521: identity-matched
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
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
                    if (MPRegisterSync.IsStaffedByOtherPlayer(__instance)) return;   // field 150521: identity-matched
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;   // single player / offline → native
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
        //
        // 1.0 PORT (sweep-2 #1, 2026-08-29): THE FUNNEL MOVED.  0.11's type
        // overload forwarded to Show(IPlayerActivity, EntityController) — the
        // overload this used to patch.  1.0 added a PRIVATE
        // Show(IPlayerActivity, bool allowUsingVehicle) and points BOTH public
        // overloads at it (PlayerActivityUI.cs:193-205), so the school door
        // (an IPlayerActivityType call) bypassed the old target entirely —
        // while the patch still bound and 390/390 stayed green.  Retargeted to
        // the private overload, which is the single funnel again.
        [HarmonyPatch(typeof(PlayerActivity.PlayerActivityUI), "Show",
            new Type[] { typeof(PlayerActivity.IPlayerActivity), typeof(bool) })]
        public static class Patch_TrainingDoor_HonoraryDegree
        {
            static bool Prefix(PlayerActivity.IPlayerActivity playerActivity)
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return true;
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;   // review r7 #2: holds in the offline fork
                try
                {
                    var reg = __instance.buildingRegistration;
                    if (!GameStatePatcher.IsAnyPlayerBusiness(reg)) return true;
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
                if (!__result || (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork)) return;   // review r7 #2: holds in the offline fork
                try
                {
                    if (GameStatePatcher.IsAnyPlayerBusiness(buildingRegistration))
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
                if (!__result || (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork)) return;   // review r7 #2: holds in the offline fork
                try
                {
                    if (GameStatePatcher.IsAnyPlayerBusiness(buildingRegistration))
                        __result = false;
                }
                catch { }
            }
        }

        // Round-198b (field 20260730-221621, 2× NRE): the ShouldUpdate gate above can be
        // bypassed by the PENDING queue — a registration enqueued before its player stamp
        // arrived is processed later regardless (stale-snapshot shape, ANTIPATTERNS Class 15).
        // The math itself then asks the AI-archetype table for a PLAYER-NAMED business
        // (GetBusinessDefault by BusinessName) — which cannot exist — and NREs. Guard the
        // MUTATION: valuation math never runs for a session player's shop. Verified consumers
        // all have synced/shielded sources: rival detail incomes are host-synced (the client
        // can't compute them), leaderboard uses host stats, takeover pricing is intercepted
        // (round-196), AI shutdown/reprice already shielded.
        [HarmonyPatch(typeof(Helpers.CompetitionHelper), nameof(Helpers.CompetitionHelper.UpdateDailyValuation))]
        public static class Patch_NoDailyValuationMathOnPlayerShops
        {
            private static float _nextLog;
            static bool Prefix(BuildingRegistration buildingRegistration)
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;   // review r7 #2: the offline fork keeps this shield (the NRE it exists for is reachable there too)
                try
                {
                    if (buildingRegistration != null && GameStatePatcher.IsAnyPlayerBusiness(buildingRegistration))
                    {
                        if (UnityEngine.Time.unscaledTime >= _nextLog)
                        {
                            _nextLog = UnityEngine.Time.unscaledTime + 60f;
                            Plugin.Logger.LogInfo($"[EconShield] daily-valuation math skipped for session player's shop '{GameStateReader.AddressKey(buildingRegistration)}' (no AI archetype exists for player-named businesses; round-198b).");
                        }
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        // ── Round-204: client-side AI-business valuation fallback ────────────────
        // CONFIRMED on the rig (10× in one session): CompetitionHelper.
        // CalculateAiOwnedValuation does dailyIncomes.Average() — LINQ Average() on an
        // EMPTY list throws InvalidOperationException. AI business incomes are
        // host-simulated and never reach clients, so on a CLIENT every rival shop's
        // BizMan page died inside SetAiOwned BEFORE its final SetActiveView("Info") —
        // the overtake panel (Info-view-only) never appeared: clients simply could not
        // take over AI businesses. Fallback replicates native's own zero-income shape
        // (Mathf.Max clamps to 0) : income term 0 + the layout valuation, exactly what
        // native computes for a business with no recorded income. The host's accept
        // check still uses ITS full valuation. InvalidOperationException ONLY; other
        // failures keep vanilla behavior. MP-gated.
        // Round-255 (field 20260808-111258): the exception shield above is NOT enough —
        // a client who joined an ESTABLISHED save carries stale-but-non-empty dailyIncomes
        // (frozen at join; the host's keep rolling daily), so the local computation
        // succeeds and quotes a price that drifts further from the host's every game day.
        // The player-facing symptom: BizMan shows $X, the host arbitrates at $Y — "prices
        // severely desync, can't buy unless it matches what the host sees." The Postfix
        // makes the HOST-SYNCED valuation the primary source on clients whenever one is
        // present (republished on every daily roll), so the display and the referee agree.
        // Player-run businesses never appear in the dict (host publishes 0 → round-255
        // removal at apply), and the host itself is never overridden.
        [HarmonyPatch(typeof(Helpers.CompetitionHelper), nameof(Helpers.CompetitionHelper.CalculateAiOwnedValuation))]
        public static class Patch_AiValuation_ClientEmptyIncomeFallback
        {
            private static int _n;
            private static int _o;
            private static float _nextProbeLog;

            // H-BIZ-1 (bundle 20260903-130650, user option A, 2026-09-03) — RESTRUCTURE per the bandaid ceiling: in MP the
            // native body is replaced by the same formula written null-safe. Native never meets a registration without a
            // Layout (every AI business gets one at creation; a player's own shop never reaches SetAiOwned), but in MP
            // another PLAYER's shop is shown through the AI-owned page (design-based, no Layout) and on a client every
            // unvisited AI business is a hollow mirror with seeded dailyIncomes — the native path then dies in
            // GetLayoutPath(null) (NullReferenceException; round-204's InvalidOperationException shield never saw it).
            //   • another player's shop → the OWNER's on-demand closure figure (ShopValuation), 0 until it arrives;
            //   • income term = 0 when dailyIncomes is null/empty (native's own zero-income shape);
            //   • layout term = 0 when Layout is null/empty or the building is unresolvable, else the native lookup.
            // A host's AI shop with complete data computes exactly the native number. Single player: native, untouched.
            static bool Prefix(BuildingRegistration registration, ref float __result)
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return true;   // review r4 #1: the offline fork keeps the shield
                try
                {
                    __result = 0f;
                    if (registration == null) return false;
                    string addr = GameStateReader.AddressKey(registration);
                    if (GameStatePatcher.IsSessionPlayerRivalId(registration.businessOwnerRivalId))   // review #1: the RUNNER stamp only — IsAnyPlayerBusiness also matches the DEED ledger, which priced an AI-run business in a player-bought building at $0 (a $1 takeover on the host)
                    {
                        __result = ShopValuation.KnownValue(addr);
                        return false;
                    }
                    var data = BuildingTypeHelper.GetData(registration);
                    if (data == null || !data.HasTag(BigAmbitions.Tags.TagRef.Buildingtypetag.calculateaiownedvaluation)) return false;
                    float income = 0f;
                    var inc = registration.dailyIncomes;
                    if (inc != null && inc.Count > 0)
                    {
                        double sum = 0; foreach (var f in inc) sum += f;
                        float avg = (float)(sum / inc.Count);
                        income = UnityEngine.Mathf.Max(avg * (float)SaveGameManager.Current.gameVariables.daysPerYear * 3f, 0f);
                    }
                    float layout = 0f;
                    string layoutName = ""; try { layoutName = registration.Layout ?? ""; } catch { }
                    if (layoutName.Length > 0)
                    {
                        var building = BuildingHelper.GetBuilding(registration.Address);
                        if (building != null)
                            layout = BusinessLayoutSets.BusinessLayoutSetHelper.GetOrLoadBusinessLayoutSet(
                                         registration.businessTypeName, new Blueprints.BuildingSizeInfo(building), layoutName, warnIfNotFound: false)
                                     ?.GetValuation() ?? 0f;
                    }
                    __result = income + layout;
                    if (layoutName.Length == 0 && UnityEngine.Time.unscaledTime >= _nextProbeLog)
                    {
                        _nextProbeLog = UnityEngine.Time.unscaledTime + 30f;
                        Plugin.Logger.LogInfo($"[EconShield] valuation fallback '{addr}': owner='{registration.businessOwnerRivalId}' incomes={inc?.Count ?? 0} layout=<null> → income-only ${__result:F0} (H-BIZ-1 probe; on clients the host-synced figure replaces it).");
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    __result = 0f;
                    if (_n++ < 3) Plugin.Logger.LogWarning($"[EconShield] valuation prefix failed for '{GameStateReader.AddressKey(registration)}': {ex.Message} — showing $0.");
                    return false;
                }
            }
            // Round-255: on CLIENTS the host-synced valuation is the primary source whenever one is present (republished
            // on every daily roll) — display and referee agree. Player-run businesses never appear in the dict.
            static void Postfix(BuildingRegistration registration, ref float __result)
            {
                try
                {
                    if (MPServer.IsRunning || !MPClient.IsClientInWorld) return;   // clients only; host stays authoritative
                    string addr = GameStateReader.AddressKey(registration);
                    if (string.IsNullOrEmpty(addr)) return;
                    if (GameStatePatcher.IsSessionPlayerRivalId(registration.businessOwnerRivalId)) return;   // review r4 #5: never let a stale synced number override the player-shop leg
                    if (!BusinessSync.AiValuationByAddress.TryGetValue(addr, out float synced) || synced <= 0f) return;
                    if (_o++ < 3 || _o % 50 == 0)
                        Plugin.Logger.LogInfo($"[EconShield] AI valuation for '{addr}': host-synced ${synced:F0} replaces local ${__result:F0} (stale-income display fix, round-255).");
                    __result = synced;
                }
                catch { }
            }
            // Last resort only: with the Prefix replacing the body in MP nothing native can throw here; a throw from the
            // Prefix/Postfix themselves is contained to $0 instead of aborting the page.
            static Exception? Finalizer(BuildingRegistration registration, ref float __result, Exception __exception)
            {
                if (__exception == null) return null;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld && !MPClient.OfflineFork) return __exception;
                try { __result = 0f; Plugin.Logger.LogWarning($"[EconShield] valuation threw for '{GameStateReader.AddressKey(registration)}': {__exception.GetType().Name}: {__exception.Message} — contained to $0."); } catch { }
                return null;
            }
        }

        // (Round-204's optimistic OvertakeBusiness→RentRequest relay REMOVED same day:
        //  rig-refuted — HandleRentRequest correctly refuses occupied buildings, and the
        //  denial rollback vacated the natively-taken-over business. Replaced by the
        //  host-ARBITRATED flow in MPTakeover.cs, round-204b.)

        // ── Round-256: vehicle-store empty-picker diagnostic (DETECT-ONLY) ───────
        // Field 20260809-132321: "when i go into van dealership nothing is showing."
        // Native SetListOfVehiclesForSale has FOUR silent-empty exits (no reg / empty
        // businessTypeName / empty Layout / vehicle types unresolved) and logs none of
        // them. This names the failing stage the moment the picker builds empty, so
        // any future report carries its own diagnosis. Inert on healthy stores (only
        // fires when the list is empty). MP-gated; capped ×4 per session.
        [HarmonyPatch(typeof(Buildings.VehicleContractSettings), "SetVehicleDropdown")]
        public static class Patch_VehicleStorePicker_EmptyDiag
        {
            private static int _n;
            static void Postfix(Buildings.VehicleContractSettings __instance)
            {
                try
                {
                    if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                    var vehicles = AccessTools.Field(typeof(Buildings.VehicleContractSettings), "_vehicles")
                                       ?.GetValue(__instance) as System.Collections.ICollection;
                    if (vehicles == null || vehicles.Count > 0) return;
                    if (_n++ >= 4) return;

                    string why = "unknown", addrKey = "?";
                    try
                    {
                        var addr = DialogController.current?.contact?.Address;
                        if (addr == null) why = "no contact address";
                        else
                        {
                            var reg = BuildingHelper.GetBuildingRegistration(addr);
                            if (reg == null) { why = "no building registration"; addrKey = addr.ToString() ?? "?"; }
                            else
                            {
                                addrKey = GameStateReader.AddressKey(reg);
                                string btn = ""; try { btn = reg.businessTypeName?.ToString() ?? ""; } catch { }
                                string lay = ""; try { lay = reg.Layout?.ToString() ?? ""; } catch { }
                                if (string.IsNullOrEmpty(btn)) why = "reg.businessTypeName EMPTY (half-generated/damaged reg)";
                                else if (string.IsNullOrEmpty(lay)) why = "reg.Layout EMPTY (half-generated/damaged reg)";
                                else
                                {
                                    var building = BuildingHelper.GetBuilding(reg.Address);
                                    var set = BusinessLayoutSets.BusinessLayoutSetHelper.GetOrLoadBusinessLayoutSet(
                                                  btn, new Blueprints.BuildingSizeInfo(building), lay.ToLower(), false);
                                    if (set?.Items == null) why = $"layout set missing for '{btn}'/'{lay}' (asset/content problem)";
                                    else
                                    {
                                        int purchaser = 0, resolved = 0;
                                        foreach (var item in set.Items)
                                        {
                                            var pip = item?.playerItemPurchaserSettings;
                                            if (pip == null || !pip.enabled) continue;
                                            purchaser++;
                                            try
                                            {
                                                var it = BigAmbitions.Items.ItemsGetter.GetByName(pip.itemName);
                                                if (it != null && Vehicles.VehicleTypes.VehicleTypeHelper.GetVehicleType(it.vehicleType) != null)
                                                    resolved++;
                                            }
                                            catch { }
                                        }
                                        why = $"layout OK but purchaser items={purchaser}, vehicle types resolved={resolved} (vehicle-type assets missing/broken)";
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception exIn) { why = $"diag itself failed: {exIn.Message}"; }
                    Plugin.Logger.LogWarning($"[VehStoreDiag] vehicle picker built EMPTY at '{addrKey}' — {why} (round-256).");
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
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
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

        // ── Round-249 HARDENING: a PARTIAL radio-station table self-heals (field 20260807-200348) ──
        // A content mod's asset fired RadioStationSource.OnEnable during GameManager.Awake's
        // synchronous bundle load — BEFORE GlobalReferences existed — so the native fill
        // (RadioPlayer.FillRadioStationsData) NRE'd mid-loop AFTER latching _isDataLoaded=true:
        // the station table stayed PARTIAL for the whole session.  Every later lookup of a
        // missing station threw KeyNotFoundException through native code with no safety net —
        // VehicleController invokes onEnterVehicle BARE, so ENTERING A VEHICLE aborted ("can't
        // access the vehicle I buy"); pause ducks and station cycling (the round-227 family
        // below) die the same way.  The latch exists for performance, not semantics: when a
        // lookup is about to miss and the fill's precondition (GlobalReferences.
        // radioStationClips) now holds, unlatch it and let the game's own fill run again
        // inside the SAME call — the table repairs in place and the lookup proceeds normally.
        // Mod-compat shield: on healthy installs the dictionary always contains every enum
        // key once filled, so this prefix never acts.
        [HarmonyLib.HarmonyPatch(typeof(RadioPlayer), nameof(RadioPlayer.GetRadioStationData))]
        public static class Patch_RadioPlayer_GetRadioStationData_HealPartialTable
        {
            private static readonly HarmonyLib.AccessTools.FieldRef<RadioPlayer, System.Collections.Generic.Dictionary<RadioStation, RadioStationData>>
                _data = HarmonyLib.AccessTools.FieldRefAccess<RadioPlayer, System.Collections.Generic.Dictionary<RadioStation, RadioStationData>>("_radioStationsData");
            private static readonly HarmonyLib.AccessTools.FieldRef<RadioPlayer, bool>
                _loaded = HarmonyLib.AccessTools.FieldRefAccess<RadioPlayer, bool>("_isDataLoaded");
            private static int _logged;

            static void Prefix(RadioPlayer __instance, RadioStation radioStation)
            {
                try
                {
                    if (!_loaded(__instance)) return;                    // native fills anyway — nothing to heal
                    var dict = _data(__instance);
                    if (dict == null || dict.ContainsKey(radioStation)) return;
                    // Partial table (an Awake-time fill died mid-loop).  Unlatch ONLY when the
                    // refill's precondition now holds — otherwise native behavior stays untouched
                    // (an unlatch that refails would just re-latch partial, same as today).
                    var gr = InstanceBehavior<GlobalReferences>.Instance;
                    if (gr == null || gr.radioStationClips == null) return;
                    _loaded(__instance) = false;
                    if (_logged++ < 4)
                        Plugin.Logger.LogWarning($"[Radio] station table PARTIAL ({dict.Count} filled, '{radioStation}' missing) — fill latch reset; the game's own refill repairs it (round-249, mod-compat shield).");
                }
                catch { }   // the shield must never make a lookup worse
            }
        }

        // ── Round-227 DIAGNOSTICS: music-station change crash (field 20260801-012525) ──
        // "Players with permission attempting to change music station crashes game."
        // The native station-cycling mesh (static PlayNextStation / instance
        // PlayNextStation / instance PlayStation) retries with NO exhaustion guard —
        // on installs with broken station data it may spiral to a StackOverflow,
        // which kills the process before normal logs flush. PROBES ONLY (no behavior
        // change): a shared depth counter across all three methods logs milestones
        // through MPSaveCoordinator.DiagWrite (per-line flushed file, BAMP_DEV) so a
        // hard crash still leaves the spiral on disk; the overlay buttons log who
        // pressed and every station's data state at that moment.
        internal static class RadioMeshDiag
        {
            internal static int Depth;
            internal static void Milestone(string where)
            {
                if (Depth == 8 || Depth == 16 || Depth == 32 || Depth == 64 || Depth == 128 || Depth == 256)
                {
                    string msg = $"[RadioDiag] mesh depth {Depth} at {where} — {Snapshot()}";
                    Plugin.Logger.LogWarning(msg);
                    MPSaveCoordinator.DiagWrite(msg);
                }
            }
            internal static string Snapshot()
            {
                try
                {
                    var rp = InstanceBehavior<GameManager>.Instance?.radioPlayer;
                    if (rp == null) return "radioPlayer=null";
                    var sb = new System.Text.StringBuilder();
                    try
                    {
                        var reg = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                        sb.Append($"bldgStation={(reg != null ? reg.GetBusinessRadioStation().ToString() : "?")} ");
                    }
                    catch { sb.Append("bldgStation=ERR "); }
                    sb.Append(MPRadioSync.SpeakerFlagsForDiag()).Append(' ');   // 235855: the two press gates
                    foreach (RadioStation st in Enum.GetValues(typeof(RadioStation)))
                    {
                        try
                        {
                            var d = rp.GetRadioStationData(st);
                            sb.Append(d == null ? $"{st}=NULL " : $"{st}={(d.IsLoading ? "LOADING" : "ok")}/{d.radioClips?.Length ?? -1} ");
                        }
                        catch (Exception ex) { sb.Append($"{st}=ERR({ex.GetType().Name}) "); }
                    }
                    return sb.ToString();
                }
                catch (Exception ex) { return "snapshot failed: " + ex.Message; }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(Player.Sound.Radio.LoudSpeakersManager), "PlayNextStation", new Type[0])]
        public static class Patch_Radio227_StaticPNS
        {
            static bool Prefix(out bool __state)
            {
                // Round-227 belt: healthy cycling never exceeds a handful — 64 means a
                // runaway (the rig crash spiraled here). Abort loudly instead of dying.
                if (RadioMeshDiag.Depth >= 64)
                {
                    __state = false;
                    Plugin.Logger.LogError($"[RadioDiag] runaway station cycle ABORTED at depth {RadioMeshDiag.Depth} — {RadioMeshDiag.Snapshot()}");
                    return false;
                }
                __state = true; RadioMeshDiag.Depth++; RadioMeshDiag.Milestone("static PlayNextStation"); return true;
            }
            static Exception? Finalizer(Exception __exception, bool __state)
            {
                if (__state) RadioMeshDiag.Depth--;
                if (__exception != null)
                {
                    Plugin.Logger.LogError($"[RadioDiag] static PlayNextStation threw {__exception.GetType().Name}: {__exception.Message} — {RadioMeshDiag.Snapshot()}");
                    MPSaveCoordinator.DiagWrite($"[RadioDiag] static PNS threw {__exception.GetType().Name}");
                }
                return __exception;   // probes only — rethrow unchanged
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(Player.Sound.Radio.LoudSpeakersManager), "PlayNextStation", new[] { typeof(RadioStation) })]
        public static class Patch_Radio227_InstPNS
        {
            static bool Prefix(out bool __state)
            {
                if (RadioMeshDiag.Depth >= 64) { __state = false; return false; }   // round-227 belt
                __state = true; RadioMeshDiag.Depth++; RadioMeshDiag.Milestone("instance PlayNextStation"); return true;
            }
            static Exception? Finalizer(Exception __exception, bool __state) { if (__state) RadioMeshDiag.Depth--; return __exception; }
        }

        [HarmonyLib.HarmonyPatch(typeof(Player.Sound.Radio.LoudSpeakersManager), "PlayStation", new[] { typeof(RadioStation) })]
        public static class Patch_Radio227_InstPlay
        {
            static bool Prefix(out bool __state)
            {
                if (RadioMeshDiag.Depth >= 64) { __state = false; return false; }   // round-227 belt
                __state = true; RadioMeshDiag.Depth++; RadioMeshDiag.Milestone("instance PlayStation"); return true;
            }
            static Exception? Finalizer(Exception __exception, bool __state) { if (__state) RadioMeshDiag.Depth--; return __exception; }
        }

        [HarmonyLib.HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.RadioOverlay), "NextStation")]
        public static class Patch_Radio227_OverlayNext
        {
            static void Prefix()
            {
                string msg = $"[RadioDiag] NextStation PRESSED — guest={HousingFurniture.LocalGuestHere()} helper={HousingFurniture.LocalHelperHere()}; {RadioMeshDiag.Snapshot()}";
                Plugin.Logger.LogInfo(msg);
                MPSaveCoordinator.DiagWrite(msg);
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(Player.HUD.ItemInfoOverlays.RadioOverlay), "ToggleRadio")]
        public static class Patch_Radio227_OverlayToggle
        {
            static void Prefix()
            {
                Plugin.Logger.LogInfo($"[RadioDiag] ToggleRadio PRESSED — guest={HousingFurniture.LocalGuestHere()} helper={HousingFurniture.LocalHelperHere()}");
            }
        }

        // ── Round-192: LoudSpeakersManager failure latch ──────────────────────────
        // TEMPORARY SHIELD (user directive): exists only so a NATIVE bug stops generating
        // misdirected reports against this mod — REMOVE when Hovgaard fixes the manager's
        // retry loop (watch the game's changelogs for a LoudSpeakers/radio fix).
        // SECOND field case of the native speaker retry-storm (first: French SP NRE report;
        // now bamp-bug-20260729-205106 — KeyNotFoundException 'Dance' ×4,811, a ~350ms frame
        // every 2s on a streamed session).  LoudSpeakersManager.LateUpdate retries
        // InitSpeakers forever with NO failure latch; when the station data never loaded
        // (usually a content-mod conflict breaking the asset catalog) every retry throws,
        // and Unity's stack capture + logging IS the rhythmic lag.  The latch: after 5
        // consecutive failures IN AN MP SESSION, disable the manager with one loud line —
        // speaker radio goes silent (it was broken anyway), the framerate comes back.
        // Single-player passes the exception through untouched (round-187 directive: the
        // mod acts only in multiplayer).
        [HarmonyLib.HarmonyPatch(typeof(Player.Sound.Radio.LoudSpeakersManager), "LateUpdate")]
        public static class Patch_LoudSpeakers_FailureLatch
        {
            /// <summary>Audit 2026-08-26: both fields are per-PROCESS but the thing they protect — a
            /// disabled native component — dies with the scene. So in a second session the retry storm
            /// returned with no latch to stop it, and worse, the `else if (!_latched)` below suppressed
            /// even the warning, making the second occurrence SILENT. Re-armed per scene.</summary>
            internal static void ResetForScene() { _fails = 0; _latched = false; }

            private static int _fails;
            private static bool _latched;

            static Exception? Finalizer(Exception __exception, Player.Sound.Radio.LoudSpeakersManager __instance)
            {
                if (__exception == null) { _fails = 0; return null; }
                if (!MPServer.IsRunning && !MPClient.InMpGame) return __exception;   // SP: native behavior untouched
                _fails++;
                if (_fails >= 5 && !_latched)
                {
                    _latched = true;
                    try { __instance.enabled = false; } catch { }
                    Plugin.Logger.LogError($"[Shield] LoudSpeakersManager DISABLED after {_fails} consecutive LateUpdate failures ({__exception.GetType().Name}: {__exception.Message}) — speaker radio is off for this session. Known native retry-storm (station data failed to load; on modded installs usually a content-mod conflict). Each retry was costing a ~350ms frame.");
                }
                else if (!_latched)
                    Plugin.Logger.LogWarning($"[Shield] LoudSpeakersManager LateUpdate threw ({_fails}/5): {__exception.Message}");
                return null;   // suppress — the native code would rethrow every cycle forever
            }
        }

        // ── Round-199: stationary-NPC stuck combined-animation flag ──────────────
        // TEMPORARY SHIELD for a NATIVE bug (remove when Hovgaard fixes it — same
        // tracking as the LoudSpeakers latch above).  Field 20260730-194909:
        // KeyNotFoundException 'TalkingPhone' ×8,170 filled the client's log
        // (log-erasure class).  Mechanism (StationaryAiBehavior.cs, read):
        // GetRandomPermanentAnimation sets _isPlayingCombinedAnimation=true when it
        // rolls a combined animation but NEVER clears it when a later re-roll lands
        // on a plain one — re-rolls fire on umbrella add/remove, i.e. rain
        // transitions, which MP weather sync makes more frequent.  Update() then
        // looks the plain anim up in the combined table every frame forever.
        // Heal = clear the stuck flag on the throwing NPC: exactly the state a
        // plain roll was meant to leave (the pose keeps playing; only the bogus
        // combined lookup stops).  KeyNotFoundException ONLY — anything else keeps
        // vanilla behavior.  MP-gated; single-player untouched.
        [HarmonyLib.HarmonyPatch(typeof(Entities.StationaryAiBehavior), "Update")]
        public static class Patch_StationaryAi_HealStuckCombinedFlag
        {
            private static System.Reflection.FieldInfo? _flag;
            private static int _n;
            private static float _nextLog;

            static Exception? Finalizer(Entities.StationaryAiBehavior __instance, Exception __exception)
            {
                if (__exception == null) return null;
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return __exception;   // SP: vanilla
                if (!(__exception is KeyNotFoundException)) return __exception;             // unknown failure: vanilla
                try
                {
                    _flag ??= AccessTools.Field(typeof(Entities.StationaryAiBehavior), "_isPlayingCombinedAnimation");
                    _flag?.SetValue(__instance, false);
                    _n++;
                    if (_n <= 5 || UnityEngine.Time.unscaledTime >= _nextLog)
                    {
                        _nextLog = UnityEngine.Time.unscaledTime + 60f;
                        Plugin.Logger.LogWarning(
                            $"[Shield] StationaryAiBehavior stuck combined-animation flag cleared "
                            + $"(vanilla one-way flag bug, event #{_n}) — per-frame KeyNotFound spam stopped.");
                    }
                }
                catch { }
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // MERGER PHASE 4a — COMPANY BOOKS (2026-09-11, D14/D18/D19 + B9)
        // ═══════════════════════════════════════════════════════════════════════════════════════
        // The overlay itself lives in src/CompanyBooks.cs. These are its hooks: the day-change
        // re-apply, the two DISPLAY-TIME surfaces that read at an event rather than on open, the
        // own-only loan cap, the tax inertness + pay-all, the row tint and the company net worth.
        // Every one of them is inert without a merger.

        /// <summary>B4/C2: the top-bar arrow + money tooltip are EVENT-driven (Topbar.cs:117-126), so
        /// late books never reach them. SetMoneyChangeValue is private, side-effect-free and
        /// re-entrant — the postfix below parks the live instance so a books receipt can re-invoke it.
        /// </summary>
        private static object? _topbarInstance;
        private static System.Reflection.MethodInfo? _topbarSetMoneyChange;

        public static void RefreshTopbarMoneyChange()
        {
            try
            {
                if (_topbarInstance == null) return;                         // the bar has not drawn yet
                _topbarSetMoneyChange ??= AccessTools.Method(typeof(UI.Topbar.Topbar), "SetMoneyChangeValue");
                if (_topbarSetMoneyChange == null) { Plugin.Logger.LogWarning("[Books] top-bar refresh refused: SetMoneyChangeValue not found."); return; }
                _topbarSetMoneyChange.Invoke(_topbarInstance, null);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] top-bar refresh: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(UI.Topbar.Topbar), "SetMoneyChangeValue")]
        public static class Patch_Topbar_MoneyChange_Books
        {
            static void Postfix(UI.Topbar.Topbar __instance) { _topbarInstance = __instance; }
        }

        /// <summary>B2: RunDaily's POSTFIX — after the whole body, therefore AFTER the persisted
        /// playerWeeklyIncomeHistory write (BusinessHelper.cs:139-140), so that history stays
        /// personal, and after CreateFinancialSummary REPLACED the day record (:100-101), which is
        /// what voids the previous overlay. RunDaily is itself a veil Step, so this runs with the veil
        /// still up (VeilPop is the Finalizer): the apply defers and the veil's pop re-applies.</summary>
        [HarmonyPatch(typeof(Helpers.BusinessHelper), nameof(Helpers.BusinessHelper.RunDaily))]
        public static class Patch_BusinessHelper_RunDaily_Books
        {
            static void Postfix()
            {
                try { CompanyBooks.OnDayChanged(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] day-change hook: {ex.Message}"); }
            }
        }

        /// <summary>B4/C1: the end-of-day popup reads the stored Day-1 record inside its coroutine at
        /// DISPLAY time (DailySummary.cs:70). A prefix on the public Run() applies anything pending
        /// before the coroutine starts.</summary>
        [HarmonyPatch]
        public static class Patch_DailySummary_Run_Books
        {
            static System.Reflection.MethodBase? TargetMethod()
            {
                var m = AccessTools.Method("UI.DailySummary.DailySummary:Run");
                if (m == null) Plugin.Logger.LogError("[Books] UI.DailySummary.DailySummary:Run NOT FOUND — the daily popup will not wait for late books.");
                return m;
            }
            static void Prefix()
            {
                try { CompanyBooks.ApplyPending("daily popup"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] daily popup hook: {ex.Message}"); }
                // DAYTABLE-1 C3(i): AFTER the apply above — this is the Day-1 figure the player is about
                // to see (DailySummary.ExecuteSequence reads financialSummaries[dayNumber == Day-1]).
                try { CompanyBooks.LogDayTable((SaveGameManager.Current?.Day ?? 0) - 1, "popup"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] daily popup daytable: {ex.Message}"); }
            }
        }

        /// <summary>B4/C5: the own 7-day income chart. Points 1-6 come from the PERSISTED
        /// playerWeeklyIncomeHistory (written unveiled-personal at BusinessHelper.cs:139-140) and only
        /// point 7 is re-summed live, so late books never reach the earlier points. When merged, every
        /// point is recomputed from the OVERLAID summaries so the whole line is a company line.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.Rivals.SelectedRivalUI), "GetPlayerWeeklyIncomeHistory")]
        public static class Patch_SelectedRivalUI_WeeklyHistory_Books
        {
            static void Postfix(List<Tuple<int, float>> __result)
            {
                try
                {
                    if (__result == null || !MergerSync.IAmMember) return;
                    CompanyBooks.ApplyPending("weekly income chart");
                    if (CompanyBooks.Inert) { Plugin.Logger.LogInfo("[Books] weekly chart left personal: the overlay is lifted right now."); return; }
                    var sums = SaveGameManager.Current?.financialSummaries;
                    if (sums == null) return;
                    for (int i = 0; i < __result.Count; i++)
                    {
                        int day = __result[i].Item1;
                        float v = 0f;
                        foreach (var s in sums) if (s != null && s.dayNumber <= day && s.dayNumber > day - 7) v += s.totalProfit;
                        __result[i] = new Tuple<int, float>(day, v);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] weekly chart: {ex.Message}"); }
            }
        }

        /// <summary>D19-3: the BANK LOAN CAP reads own income only. BankDialog.cs:325 calls
        /// PlayerHelper.CalculateDailyIncome, which sums the last seven records' totalProfit — the very
        /// field the overlay folds partner money into. Answering from the LEDGER (the exact figure the
        /// overlay added to each of those days, subtracted back out) keeps the cap personal without
        /// mutating the list for anyone else, so no push/pop window exists for a reader to fall into.
        /// </summary>
        [HarmonyPatch(typeof(Helpers.PlayerHelper), nameof(Helpers.PlayerHelper.CalculateDailyIncome))]
        public static class Patch_PlayerHelper_DailyIncome_OwnOnly
        {
            static bool Prefix(ref float __result)
            {
                try
                {
                    if (!MergerSync.IAmMember || CompanyBooks.OverlaidDays == 0) return true;   // vanilla
                    __result = CompanyBooks.OwnOnlyDailyIncome();
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] loan-cap income: {ex.Message} — vanilla figure used."); return true; }
            }
        }

        /// <summary>D19-2: TaxHelper.RunDaily is NOT one of the authority-veil Steps, yet the liability
        /// trigger it calls (PlayerShouldDoTaxes, TaxHelper.cs:152-170) sums BusinessIncomeStatement
        /// TotalSales over a whole tax year — with an overlay live every member would cross the
        /// $150,000 threshold on the company's sales. The overlay is lifted for the whole pass, exactly
        /// as a veiled Step lifts it.  Review r2: BusinessHelper.RunDaily IS a veil Step, and the veil
        /// already lifts the overlay for that whole pass, so on the day-change path this patch is
        /// redundant - it is KEPT as belt and braces because TaxHelper.RunDaily is a separate entry point
        /// with its own callers, and a lift that nests correctly costs nothing when it is already up.</summary>
        [HarmonyPatch(typeof(Helpers.TaxHelper), nameof(Helpers.TaxHelper.RunDaily))]
        public static class Patch_TaxHelper_RunDaily_BooksInert
        {
            static void Prefix() { try { CompanyBooks.SuspendPush(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] tax-pass lift refused: {ex.Message}"); } }
            static Exception? Finalizer(Exception? __exception)
            {
                try { CompanyBooks.SuspendPop(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] tax-pass restore refused: {ex.Message}"); }
                return __exception;
            }
        }

        /// <summary>B9-iii: the taxes tab shows the COMPANY bill. The page has ONE amount label per
        /// row (EconoViewTaxes.cs:34 taxesOwedAmountLabel) and no per-member row container, so the
        /// company figure goes into the page's own label with the page's own currency formatting —
        /// no new wording, no new rows.</summary>
        [HarmonyPatch(typeof(EconoViewTaxes), nameof(EconoViewTaxes.Init))]
        public static class Patch_EconoViewTaxes_CompanyTotal
        {
            static void Postfix(TMPro.TMP_Text ___taxesOwedAmountLabel)
            {
                try
                {
                    if (!MergerSync.IAmMember || ___taxesOwedAmountLabel == null) return;
                    // MINOR (b, review r2): this label natively shows the CURRENT bill only - back taxes
                    // have their own label (EconoViewTaxes.cs:93) - so the OWN half is current-only too.
                    // T9 (phase 4b): and so is the PARTNERS' half now - CompanyBooksPayload carries an
                    // additive TaxCurrentDue beside its whole-outstanding TaxDue, and this label sums
                    // that one. A partner still on the older build publishes 0 there and adds nothing.
                    float own = 0f;
                    try { own = Helpers.TaxHelper.GetCurrentTaxesToPay(); } catch { }
                    float partners = CompanyBooks.PartnerTaxCurrentDue();
                    if (partners <= 0f) return;                          // nothing to add: leave the page alone
                    // GAME-PATCH-0916 F5: the EconoView taxes page writes every figure with its own
                    // TaxCalculationHelper.ToCurrencyFormat (decompile EconoViewTaxes.cs:157/165/174 - full
                    // "$1,234.56"), so a short-form company total sat next to full-form native figures. Use the
                    // page's own formatter. It takes a decimal (TaxCalculationHelper.cs:133) and our running
                    // totals are floats, so the game's own float->decimal rounding does the conversion
                    // (RoundCurrency, :118 - exactly what ToRoundedCurrencyFormat :128 does).
                    ___taxesOwedAmountLabel.text = Helpers.TaxCalculationHelper.ToCurrencyFormat(
                        Helpers.TaxCalculationHelper.RoundCurrency(own + partners));
                    // m-e (review r3): with no own bill the page had just written its own "no taxes
                    // due" / "taxes paid" wording in darkGreen (EconoViewTaxes.cs:112/113 and
                    // 122/123).  Overwriting the wording with a figure while leaving the green read as
                    // SETTLED when the company still owes.  Give it the colour that same page uses for
                    // a bill that IS due (EconoViewTaxes.cs:103) - the page's own colour, no new text.
                    if (own <= 0f)
                        try
                        {
                            var gr = InstanceBehavior<GlobalReferences>.Instance;
                            if (gr?.colors != null) ___taxesOwedAmountLabel.color = gr.colors.red;
                        }
                        catch { }
                    Plugin.Logger.LogInfo($"[Tax] company total shown: own {own:F2} + partners {partners:F2}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] company total: {ex.Message}"); }
            }
        }

        /// <summary>BUILD POPUPS-1 P1.  The IRS unpaid-tax warning (decompile Helpers/TaxHelper.cs:181-200)
        /// is built on each member from its OWN shops under the merger veil, so a four-member company gets
        /// four warnings each naming a quarter of the bill - while the tax PAGE already shows the company
        /// total (Patch_EconoViewTaxes_CompanyTotal, :8634) and D19-2 lets any member pay the sum.  This
        /// prefix runs the native body's EXACT sequence - same contact, same key, same three data entries -
        /// with `amount` = own + partners, and returns false so the native body does not also send.  Off a
        /// merger, or with no partner bill, it returns true and the native body runs untouched.
        /// The tax BILL itself is a company bill too now (TAXBILL-ONE, user ruling 2026-09-12): the
        /// payload carries each member's WHOLE filed return rather than one scalar, so
        /// Patch_TaxesMessage_CompanyBill below hands the game's own renderer a company Taxes object
        /// whose labelled lines and whose grand total agree with each other.</summary>
        [HarmonyPatch(typeof(Helpers.TaxHelper), "SendUnpaidWarning")]
        public static class Patch_TaxHelper_UnpaidWarning_CompanyTotal
        {
            static bool Prefix()
            {
                bool sent = false;   // POPUPS-1 review MINOR-4: true once the company warning went out
                try
                {
                    if (!MergerSync.IAmMember) return true;
                    float partners = CompanyBooks.PartnerTaxCurrentDue();
                    if (partners <= 0f) return true;                      // nothing to add: leave the game alone
                    sent = false;
                    float own;
                    try { own = Helpers.TaxHelper.GetCurrentTaxesToPay(); } catch { return true; }
                    var irs = Helpers.TaxHelper.GetIRSAddress();
                    var contact = Entities.Contact.GetContact("internal_revenue_service", UI.Smartphone.Apps.Contacts.ContactCategoryName.Finance, "government", irs);
                    if (contact == null) return true;
                    var data = new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "day",     Helpers.TaxHelper.GetCurrentTaxesDueDay().ToString() },
                        { "address", Streets.AddressHelper.ToFormattedString(irs) },
                        // GAME-PATCH-0916 F5: the native body formats this entry with
                        // TaxCalculationHelper.ToRoundedCurrencyFormat (decompile Helpers/TaxHelper.cs:203), not
                        // the float extension - match it so the company figure reads like the native warning.
                        { "amount",  Helpers.TaxCalculationHelper.ToRoundedCurrencyFormat(own + partners) },
                    };
                    GameManager.SendTextMessage(contact, "ba:messagetype_contacts_taxes_message_warning", data);
                    sent = true;   // POPUPS-1 review MINOR-4: from here on native must NOT send a second warning
                    Plugin.Logger.LogInfo($"[Tax] the unpaid-tax warning names the COMPANY bill: own {own:F2} + partners {partners:F2}.");
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] company warning: {ex.Message}"); return !sent; }
            }
        }

        /// <summary>B9-iv: the game's OWN pay action fired. The member's own bill was paid natively;
        /// ask every partner to settle theirs so the whole company is clear. PayingForCompany guards
        /// the relayed payment from firing a second pay-all.</summary>
        [HarmonyPatch(typeof(Helpers.TaxHelper), nameof(Helpers.TaxHelper.PayCurrentTaxes), new Type[] { typeof(float) })]
        public static class Patch_TaxHelper_PayCurrent_PayAll
        {
            static void Prefix(out int __state)
            {
                __state = 0;
                try { __state = SaveGameManager.Current?.currentUnpaidTaxes?.day ?? 0; } catch { }
                // TAXBILL-ONE T3 (PATCH F): with NO own bill the native body returns true at once
                // (decompile Helpers/TaxHelper.cs:377-379) and the postfix's "own record null
                // afterwards" test still holds - but the period would be 0 and the pay-all would name
                // no bill.  Name the co-members' period instead, so a member who owes nothing itself
                // can still settle the COMPANY's bill at the counter.
                try { if (__state == 0 && MergerSync.IAmMember) __state = CompanyBooks.PartnerTopPaidPeriod(); } catch { }
            }
            static void Postfix(bool __result, int __state)
            {
                try
                {
                    if (!__result || !MergerSync.IAmMember) return;
                    if (CompanyBooks.PayingForCompany) return;           // this IS the relayed payment
                    // TAXBILL-ONE fold c (rig run 2): the payer's partners must see THIS machine's due drop at
                    // once - the game's own pay path publishes nothing, so every other machine's company
                    // counter kept showing this member's old bill until the next day change.  One publish per
                    // pay action, partial or full (the relayed leg publishes in PayOwnForCompany already).
                    CompanyBooks.Publish("tax paid");
                    // M5 (review r2): this method returns TRUE for a PARTIAL payment too - the game's own
                    // UI pays whatever the player typed through it (IRSEmployee.cs:51 passes
                    // purchaseUI.CurrentTaxPaymentAmount; TaxHelper.cs:376-419 pays min(amount, due) and
                    // returns true). Only a payment that SETTLES the bill clears the record
                    // (TaxHelper.cs:400-407: remaining <= 0 -> currentUnpaidTaxes = null), so that is the
                    // test. A partial payment is a personal one and must not make the whole company pay.
                    if (SaveGameManager.Current?.currentUnpaidTaxes != null)
                    { Plugin.Logger.LogInfo("[Tax] partial own payment - company pay-all not triggered."); return; }
                    CompanyBooks.SendPayAll(__state);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] pay-all hook: {ex.Message}"); }
            }
        }

        /// <summary>TAXBILL-ONE T1: capture the return at FILING - and ONLY at filing (fold b, review r1
        /// MAJOR-1).  A postfix on ExecutePlayerTaxesEvent cannot reach the figure it needs - the Taxes
        /// object is a LOCAL there (decompile Helpers/TaxHelper.cs:106-148) and a ZERO bill is never stored
        /// on the game instance at all (:117-125) - so the snapshot is taken where the object is a return
        /// value: GenerateTaxes (:203-274).  GenerateTaxes has TWO callers: ExecutePlayerTaxesEvent (:108)
        /// and the game's console command IRSPrintTaxesOwedSoFar (:555), which only PRINTS what would be
        /// owed and files nothing - a snapshot taken there would be published to the partners as a return
        /// this member never filed.  So the snapshot is taken only while the filing pass is up:
        /// Patch_TaxHelper_ExecuteTaxes_Publish raises FilingPass in its prefix and drops it in its
        /// finalizer (an exception inside the pass cannot leave it up).  The snapshot is a DEEP copy
        /// because the live record keeps being mutated by later payments (:409-411).</summary>
        [HarmonyPatch(typeof(Helpers.TaxHelper), "GenerateTaxes")]
        public static class Patch_TaxHelper_GenerateTaxes_Capture
        {
            /// <summary>TRUE only between ExecutePlayerTaxesEvent's entry and its exit (fold b).</summary>
            public static bool FilingPass;
            static void Postfix(Entities.Taxes __result)
            {
                try
                {
                    if (!FilingPass || !MergerSync.IAmMember || __result == null) return;
                    CompanyBooks.LastFiledReturn = CompanyBooks.CloneReturn(__result);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] filed-return capture: {ex.Message} — this member's rows may be missing from the company bill."); }
            }
        }

        /// <summary>TAXBILL-ONE T1: publish AT FILING, so the other members see this member's parts within
        /// seconds rather than at the next day change.  This runs INSIDE the RunDaily lift
        /// (Patch_TaxHelper_RunDaily_BooksInert above has SuspendPush up), and that is safe: Publish does
        /// not test the suspend counter at all - it lifts the overlay itself and reads the live records
        /// (CompanyBooks.cs:231, "lift the overlay HERE") - so a suspended publish is neither dropped nor
        /// deferred, it goes out immediately with own-only figures, which is exactly what a filed return
        /// must carry.  No PublishAfterPop is needed.
        /// Fold b: the prefix raises Patch_TaxHelper_GenerateTaxes_Capture.FilingPass and the finalizer drops
        /// it, so GenerateTaxes is snapshotted only when this pass called it; the postfix publishes only a
        /// return dated TODAY - the one this very pass produced - never an older snapshot.</summary>
        [HarmonyPatch(typeof(Helpers.TaxHelper), "ExecutePlayerTaxesEvent")]
        public static class Patch_TaxHelper_ExecuteTaxes_Publish
        {
            static void Prefix() { Patch_TaxHelper_GenerateTaxes_Capture.FilingPass = true; }
            static Exception? Finalizer(Exception? __exception)
            {
                Patch_TaxHelper_GenerateTaxes_Capture.FilingPass = false;
                return __exception;
            }
            static void Postfix()
            {
                try
                {
                    if (!MergerSync.IAmMember) return;
                    var t = CompanyBooks.LastFiledReturn;
                    if (t == null || t.day != (SaveGameManager.Current?.Day ?? -1)) return;   // fold b: only THIS pass's return
                    CompanyBooks.Publish("tax filed");
                    Plugin.Logger.LogInfo($"[Tax] filed this member's return for period day {t.day}: total {t.totalToPay:F2} ({t.businessesIncome?.Count ?? 0} business row(s), {t.deductibleExpenses?.Count ?? 0} deduction row(s), {t.estateTaxes?.Count ?? 0} property row(s)); published to the company.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] filing publish: {ex.Message} — the company sees this member's parts at its next publish."); }
            }
        }

        /// <summary>TAXBILL-ONE T2 (user ruling 2026-09-12): under a merger the bill the IRS sends IS the
        /// company's return - every member's businesses, deductions and properties on one sheet, one
        /// total, equal to what the shared wallet will pay.  Nothing new is drawn: the renderer is the
        /// game's own (UI.Smartphone.Apps.Contacts/TaxesMessage.SetData, decompile :107-118) and it is
        /// simply handed a DIFFERENT Taxes object.  ContactsApp.cs:495-499 re-runs SetData from the
        /// STORED message on every open of the conversation, so the swap is never persisted - and a
        /// member who files later completes the bill by itself at the next open.
        /// The only label this build adds is a member's DISPLAY NAME, which the repossession variant
        /// already draws as a plain row with an empty value (TaxesMessage.cs:221).</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.Contacts.TaxesMessage),
                      nameof(UI.Smartphone.Apps.Contacts.TaxesMessage.SetData), new Type[] { typeof(Entities.Taxes) })]
        public static class Patch_TaxesMessage_CompanyBill
        {
            // Same thread, same frame: the prefix fills this and the postfix empties it.
            private static System.Collections.Generic.List<string>? _pendingRows;
            private static System.Reflection.MethodInfo? _addPlain, _addSplitter;

            /// <summary>Can a row LABEL we hand the renderer carry markup (a colour tag) and have it render?
            /// No - and asking the TMP component was the wrong question.  The table TMP does have richText on
            /// (the game writes its own bold/position/line-height tags into it), but every label reaching
            /// AddPlainLine is now ESCAPED by the renderer itself: `AppendRow("&lt;noparse&gt;" + FitLabel(label, w) +
            /// "&lt;/noparse&gt;", value)` (decompile UI.Smartphone.Apps.Contacts/TaxesMessage.cs:366-375).  A
            /// member display name is not a localization key, so it takes that branch and a colour tag would
            /// print LITERALLY in the IRS bill.  Answer false; the helper stays (and is named for the real
            /// question) so a later game build that drops the escape needs one line changed here.</summary>
            private static bool LabelsAcceptMarkup(UI.Smartphone.Apps.Contacts.TaxesMessage msg)
            {
                return false;
            }

            static void Prefix(UI.Smartphone.Apps.Contacts.TaxesMessage __instance, ref Entities.Taxes taxes)
            {
                _pendingRows = null;
                try
                {
                    if (!MergerSync.IAmMember || taxes == null) return;
                    CompanyBooks.TaxRowTint = LabelsAcceptMarkup(__instance);   // false on this build: the renderer escapes labels
                    float own = taxes.totalToPay;
                    var company = CompanyBooks.CompanyReturn(taxes, out var pending, out int lossRows);
                    int folded = 0;
                    foreach (var kv in CompanyBooks.Partners)
                        if (MergerSync.IsMemberPid(kv.Key) && kv.Value.TaxReturn != null && kv.Value.TaxReturn!.Day == company.day) folded++;
                    _pendingRows = pending;
                    taxes = company;
                    Plugin.Logger.LogInfo($"[Tax] company bill drawn for period day {company.day}: own {own:F2} + {folded} co-member(s) {company.totalToPay - own:F2} = {company.totalToPay:F2}; {pending.Count} pending, {lossRows} loss adjustment(s).");
                }
                catch (Exception ex)
                {
                    _pendingRows = null;
                    Plugin.Logger.LogWarning($"[Tax] company bill: {ex.Message} — this member's own bill is shown instead.");
                }
            }

            static void Postfix(UI.Smartphone.Apps.Contacts.TaxesMessage __instance)
            {
                var pending = _pendingRows;
                _pendingRows = null;
                try
                {
                    if (pending == null || pending.Count == 0) return;
                    var t = typeof(UI.Smartphone.Apps.Contacts.TaxesMessage);
                    if (_addSplitter == null) _addSplitter = AccessTools.Method(t, "AddSplitter");
                    if (_addPlain == null)    _addPlain    = AccessTools.Method(t, "AddPlainLine", new Type[] { typeof(string), typeof(string) });
                    if (_addSplitter == null || _addPlain == null)
                    { Plugin.Logger.LogWarning("[Tax] pending-member rows skipped: the renderer's own line builders (AddSplitter/AddPlainLine) were not found."); return; }
                    _addSplitter!.Invoke(__instance, null);
                    foreach (var pid in pending)
                        _addPlain!.Invoke(__instance, new object[] { CompanyBooks.MemberRowLabel(pid), string.Empty });
                    CommitTable(t, __instance);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] pending-member rows: {ex.Message}"); }
            }

            private static System.Reflection.MethodInfo? _endTable;
            private static System.Reflection.FieldInfo? _tableText;
            private static bool _tableTextWarned;
            private static bool _endTableWarned;

            /// <summary>GAME-PATCH-0916: rows are no longer live objects.  SetData builds the whole message
            /// into one shared StringBuilder (TaxesMessage.TableText, private static, :72) and commits it in
            /// EndTable (:134-142) - which has already run by the time this POSTFIX appends its rows, so the
            /// postfix has to commit again or the rows are never shown.  EndTable INSERTS the line-height tag
            /// at position 0 of that StringBuilder (:136), so simply calling it twice would leave two tags.
            /// The code allows the clean fix: strip the tag EndTable put there last time, then let EndTable
            /// itself re-insert it and redo its own sizing (:137-141) off the live row metrics, which the
            /// rows just appended have already updated through MeasureCharacter (:353-364).
            /// Review r1 F6: if TableText cannot be read the tag cannot be stripped, so EndTable would quietly
            /// leave TWO `&lt;line-height=&gt;` tags in the message. In that case we do NOT commit at all (one
            /// warning per session): the appended rows are not shown, which is the same outcome as a missing
            /// EndTable above and strictly better than a visibly broken bill.</summary>
            private static void CommitTable(Type t, UI.Smartphone.Apps.Contacts.TaxesMessage msg)
            {
                if (_endTable == null)  _endTable  = AccessTools.Method(t, "EndTable");
                if (_tableText == null) _tableText = AccessTools.Field(t, "TableText");
                if (_endTable == null)
                {
                    if (!_endTableWarned)
                    {
                        _endTableWarned = true;
                        Plugin.Logger.LogWarning("[Tax] pending-member rows appended but not committed: TaxesMessage.EndTable was not found.");
                    }
                    return;
                }
                const string tagPrefix = "<line-height=";
                var sb = _tableText?.GetValue(null) as System.Text.StringBuilder;
                if (sb == null)
                {
                    if (!_tableTextWarned)
                    {
                        _tableTextWarned = true;
                        Plugin.Logger.LogWarning("[Tax] pending-member rows appended but not committed: TaxesMessage.TableText was not readable, so the line-height tag EndTable already inserted cannot be stripped and calling EndTable again would double it.");
                    }
                    return;
                }
                if (sb.Length > tagPrefix.Length)
                {
                    bool leading = true;
                    for (int i = 0; i < tagPrefix.Length; i++) if (sb[i] != tagPrefix[i]) { leading = false; break; }
                    if (leading)
                    {
                        int end = -1;
                        for (int i = tagPrefix.Length; i < sb.Length; i++) if (sb[i] == '>') { end = i; break; }
                        if (end > 0) sb.Remove(0, end + 1);
                    }
                }
                _endTable!.Invoke(msg, null);
            }
        }

        /// <summary>TAXBILL-ONE T3 (PATCH C): the IRS counter shows and charges the COMPANY total.  Native
        /// PurchaseUI.GetIrsPaymentAmount (decompile UI.Purchase/PurchaseUI.cs:486-493, private static)
        /// answers TaxHelper.GetCurrentTaxesToPay() for the current-taxes item, which PurchaseUiTax.Setup
        /// turns into both the price label and the pre-filled amount (:31-48).
        /// HOW THE MONEY MOVES: with the amount = the company total, the native pay path still charges
        /// only min(amount, this machine's own bill) here (TaxHelper.cs:389) - the payer's machine settles
        /// its OWN bill, and the existing pay-all (CompanyBooks.SendPayAll, fired by
        /// Patch_TaxHelper_PayCurrent_PayAll above) has each partner settle theirs on their machine.  Every
        /// one of those payments comes out of the SAME shared wallet (the MergerWallet mirror), so what
        /// leaves the wallet is the company total, once.  No new money path is added here.</summary>
        [HarmonyPatch(typeof(UI.Purchase.PurchaseUI), "GetIrsPaymentAmount", new Type[] { typeof(Entities.TaxPaymentType) })]
        public static class Patch_PurchaseUI_IrsAmount_CompanyTotal
        {
            static void Postfix(Entities.TaxPaymentType taxPaymentType, ref float __result)
            {
                try
                {
                    if (!MergerSync.IAmMember || taxPaymentType != Entities.TaxPaymentType.CurrentTaxes) return;
                    float partners = CompanyBooks.PartnerTaxCurrentDue();
                    if (partners <= 0f) return;                      // nothing to add: leave the counter alone
                    __result += partners;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] counter amount: {ex.Message} — the own figure stands."); }
            }
        }

        /// <summary>TAXBILL-ONE T3 (PATCH D): a member with NO bill of its own can still pay the company's.
        /// Native HasCurrentTaxesToPay (decompile Helpers/TaxHelper.cs:450-453) is only ever a UI gate -
        /// every caller either opens a screen, adds a screen row or picks a click label
        /// (IRSStationController.cs:56 and :78, ManageCtaBehavior.cs:134, PurchaseUI.cs:190, and through
        /// HasAnyTaxesToPay :460-467, InfoOverlay.cs:38).  None of them moves money and none creates a
        /// todo task (the task is created in ExecutePlayerTaxesEvent, TaxHelper.cs:141, which never asks
        /// this), so widening the answer only lets the counter open.</summary>
        [HarmonyPatch(typeof(Helpers.TaxHelper), nameof(Helpers.TaxHelper.HasCurrentTaxesToPay))]
        public static class Patch_TaxHelper_HasCurrent_CompanyBill
        {
            static void Postfix(ref bool __result)
            {
                try
                {
                    if (__result || !MergerSync.IAmMember) return;
                    if (CompanyBooks.PartnerTaxCurrentDue() > 0f) __result = true;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] company bill outstanding: {ex.Message}"); }
            }
        }

        /// <summary>TAXBILL-ONE T3 (PATCH E): the counter's amount field is not editable while it carries a
        /// COMPANY total.  Native pre-fills it with the max and lets the player type a smaller figure
        /// (PurchaseUiTax.Setup :44-45, SelectPayment :50-68 clamps to that max) - but a typed-down figure
        /// would settle only part of the company's bill while the pay-all still asks every partner to
        /// settle theirs in full.  Only the field is locked: the button and the keyboard confirm still
        /// work (KeyboardInputHelper.Configure, :48) and the pre-filled max is what gets paid.</summary>
        [HarmonyPatch(typeof(UI.Purchase.PurchaseUiTax), nameof(UI.Purchase.PurchaseUiTax.Setup))]
        public static class Patch_PurchaseUiTax_CompanyAmountFixed
        {
            static void Postfix(string key, UI.Components.InputField ___amountInput)
            {
                try
                {
                    if (!MergerSync.IAmMember || key != "taxes_pay_item") return;
                    if (CompanyBooks.PartnerTaxCurrentDue() <= 0f) return;   // own bill only: native behaviour
                    var inp = ___amountInput;
                    if (inp == null) return;
                    var f = inp.tmpInputField;
                    if (f != null) f.readOnly = true;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] counter field: {ex.Message} — the field stays editable."); }
            }
        }

        /// <summary>TAXBILL-ONE T4 (PATCH G): under a merger the game's own $150,000 filing line is tested
        /// against the COMPANY's sales.  Native PlayerShouldDoTaxes (decompile Helpers/TaxHelper.cs:150-171,
        /// private static) answers false off an anniversary and otherwise sums TotalSales over the last
        /// daysPerYear summaries BY LIST INDEX.  The day test is kept exactly as native; only the sum
        /// widens.  The overlay is already lifted for this whole pass (Patch_TaxHelper_RunDaily_BooksInert
        /// above), and OwnLastYearSales lifts it again for its own sum - the lift nests, so that costs
        /// nothing here.  When the company falls short only because a co-member has not published yet, the
        /// anniversary is marked pending and re-checked the moment those books arrive
        /// (CompanyBooks.TaxAnniversaryRecheck, called from Receive).</summary>
        [HarmonyPatch(typeof(Helpers.TaxHelper), "PlayerShouldDoTaxes")]
        public static class Patch_TaxHelper_ShouldDoTaxes_CompanyLine
        {
            static bool Prefix(ref bool __result)
            {
                try
                {
                    if (!MergerSync.IAmMember) return true;                          // vanilla
                    var gi = SaveGameManager.Current;
                    int dpy = gi?.gameVariables?.daysPerYear ?? 0;
                    if (gi == null || dpy <= 0) return true;
                    if (gi.Day % dpy != 0) { __result = false; return false; }        // native :152-155
                    float own      = CompanyBooks.OwnLastYearSales();
                    float partners = CompanyBooks.PartnerLastYearSales();
                    float company  = own + partners;
                    __result = company >= 150000f;
                    if (!__result)
                    {
                        int missing = CompanyBooks.UnpublishedMemberCount();
                        if (missing > 0)
                        {
                            CompanyBooks.AnniversaryPending = gi.Day;
                            Plugin.Logger.LogInfo($"[Tax] anniversary day {gi.Day}: company sales {company:F2} below the line with {missing} member(s) unpublished — re-checked when their books arrive.");
                        }
                    }
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Tax] company filing line: {ex.Message} — the game's own test stands."); return true; }
            }
        }

        /// <summary>B5/D18: an EconoView income-statement row whose business belongs to a PARTNER gets
        /// that member's colour on the row LABEL only (the value columns keep the game's own
        /// green/red). The cell view binds by rowName (EconoViewIncomeStatementCellView.cs:91), so the
        /// overlay keeps a label→owner map alongside its address→owner one. D19-6: the residential
        /// line and the loans/insurance/salary group line are company sums with no colour, and they
        /// never appear in that map.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.EconoView.EconoViewIncomeStatementCellView),
                      nameof(UI.Smartphone.Apps.EconoView.EconoViewIncomeStatementCellView.SetData))]
        public static class Patch_EconoViewRow_PartnerTint
        {
            /// <summary>MINOR (a, review r2): EnhancedScroller RECYCLES these cells, and nothing ever put
            /// the label's colour back - a cell that once carried a partner's colour kept it when it was
            /// re-bound to an own or residential row. The label's ORIGINAL colour is cached the first time
            /// each label is seen and restored for every row that is not a partner's.</summary>
            private static readonly Dictionary<int, UnityEngine.Color> _labelHomeColour = new();

            static void Postfix(UI.Smartphone.Apps.EconoView.EconoViewIncomeStatementModel data, TMPro.TMP_Text ___nameLabel)
            {
                try
                {
                    if (data == null || ___nameLabel == null) return;
                    int id = ___nameLabel.GetInstanceID();
                    if (!_labelHomeColour.TryGetValue(id, out var home)) { home = ___nameLabel.color; _labelHomeColour[id] = home; }

                    if (!MergerSync.IAmMember
                        || !CompanyBooks.TryOwnerOfRowLabel(data.rowName, out var pid)
                        || !PlayerColours.TryColourFor(pid, out var c))
                    { ___nameLabel.color = home; return; }   // own / residential / group row: the label's own colour
                    ___nameLabel.color = c;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] row tint: {ex.Message}"); }
            }
        }

        /// <summary>MERGER PHASE 4b / R2 (r3 restructure): the LAST-TRANSACTIONS list gets its partner
        /// rows HERE, at the screen layer - nothing is ever enqueued into gi.Transactions. LoadLatest
        /// fills the controller's own model list, `public List&lt;TModel&gt; data;`
        /// (BaTable/BaTable.cs:14), from the queue (EconoViewLastTransactionsScrollerController.cs:27-38)
        /// and orders it DAY-DESCENDING (SortByDayDescending :48-61); it keeps EVERY entry, so the merge
        /// keeps them all too. Each partner row becomes a model through the game's OWN private
        /// `CreateModel(Transaction)` (:43-46, cached MethodInfo), so its label is built exactly as a
        /// native row's - and the model holds no reference back to its transaction
        /// (EconoViewLastTransactionModel.cs:5-18 is amount/day/labelData), which is why the owner is
        /// recorded against the MODEL for the tint. RELOAD: LoadLatest calls the private ReloadData()
        /// itself at :40, so this postfix re-calls that same method - it stops and restarts the
        /// ReloadWhenReady coroutine (:77-99), which is what actually hands the scroller its data.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionsScrollerController),
                      nameof(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionsScrollerController.LoadLatest))]
        public static class Patch_EconoViewLastTx_PartnerRows
        {
            private static System.Reflection.MethodInfo? _createModel;
            private static System.Reflection.MethodInfo? _reloadData;

            static void Postfix(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionsScrollerController __instance,
                                List<UI.Smartphone.Apps.EconoView.EconoViewLastTransactionModel> ___data)
            {
                try
                {
                    if (!MergerSync.IAmMember || ___data == null) return;   // R8: one bool read, no allocation
                    CompanyFeed.ForgetModels(typeof(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionModel));
                    CompanyFeed.NoteOpenList(__instance);

                    var cm = _createModel ??= HarmonyLib.AccessTools.Method(
                        typeof(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionsScrollerController), "CreateModel");
                    if (cm == null)
                    { Plugin.Logger.LogWarning("[Feed] rows refused: EconoViewLastTransactionsScrollerController.CreateModel not found."); return; }

                    var args = new object[1];
                    int n = CompanyFeed.MergeInto(___data,
                        t => { args[0] = t; return cm.Invoke(null, args) as UI.Smartphone.Apps.EconoView.EconoViewLastTransactionModel; },
                        m => m.day,
                        "last transactions");
                    if (n <= 0) return;

                    var rd = _reloadData ??= HarmonyLib.AccessTools.Method(
                        typeof(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionsScrollerController), "ReloadData");
                    if (rd == null) { Plugin.Logger.LogWarning("[Feed] reload refused: ReloadData not found - the rows show on the next open."); return; }
                    rd.Invoke(__instance, null);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] last-transactions rows: {ex.Message}"); }
            }
        }

        /// <summary>MERGER PHASE 4b / R3 (r3 restructure): the FULL-TRANSACTIONS page, the same way.
        /// EconoViewFullTransactionsScrollerController.Load REPLACES the model list with a LINQ
        /// projection of the sequence it is handed (:12-13, `data = transactions.Select(...).ToList()`),
        /// so ___data is read AFTER that assignment and merged into in place. ORDER/SIZE: its caller
        /// hands it `orderby x.timestamp.Day descending` over the reversed queue
        /// (EconoViewFullTransactions.cs:143-147) and Load keeps every element - so the same stable
        /// day-descending merge, with no trim. The row model has a public constructor
        /// (TransactionModel.cs:21-28) fed by the same EconoViewOverview.GenerateTransactionData the
        /// game's own projection uses. RELOAD: Load ends with scroller.ReloadData() (:15), so this
        /// postfix calls it again on the same field - `public EnhancedScroller scroller;`
        /// (BaTable/BaTable.cs:18). FILTERS (m4, r4): the page's day/type/amount dropdowns filter the
        /// GAME's queue before Load is called (EconoViewFullTransactions.cs:129-142), so they narrow the
        /// OWN rows only - `filtered: true` puts the partner rows through the same three tests, from the
        /// selection the prefix below stashes.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.EconoView.EconoViewFullTransactionsScrollerController),
                      nameof(UI.Smartphone.Apps.EconoView.EconoViewFullTransactionsScrollerController.Load))]
        public static class Patch_EconoViewFullTx_PartnerRows
        {
            static void Postfix(List<UI.Smartphone.Apps.EconoView.TransactionModel> ___data,
                                EnhancedUI.EnhancedScroller.EnhancedScroller ___scroller)
            {
                try
                {
                    if (!MergerSync.IAmMember || ___data == null) return;   // R8: one bool read, no allocation
                    CompanyFeed.ForgetModels(typeof(UI.Smartphone.Apps.EconoView.TransactionModel));
                    int n = CompanyFeed.MergeInto(___data,
                        t => new UI.Smartphone.Apps.EconoView.TransactionModel(
                                 UI.Smartphone.Apps.EconoView.EconoViewOverview.GenerateTransactionData(t),
                                 t.timestamp != null ? t.timestamp.Day : 0, t.transactionType, t.amount, t.balance),
                        m => m.Day,
                        "full transactions", filtered: true);
                    if (n > 0 && ___scroller != null) ___scroller.ReloadData();
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] full-transactions rows: {ex.Message}"); }
            }
        }

        /// <summary>m4 (r4 re-check): THE PAGE'S DROPDOWNS. EconoViewFullTransactions filters the GAME's
        /// queue itself - day, then type, then amount option (EconoViewFullTransactions.cs:129-142) -
        /// and only then hands the survivors to the controller's Load (:143), so without this the
        /// partner rows would ignore the dropdowns entirely. This stashes the three selections so the
        /// Load postfix above can apply the SAME rule to them.
        /// A PREFIX, not a postfix: RefreshTransactionsList CALLS Load, so a postfix would stash one
        /// refresh too late and every filter change would show the PREVIOUS filter's partner rows.
        /// The injected fields are the page's own privates - declarations quoted verbatim:
        ///   EconoViewFullTransactions.cs:37  "private string _selectedTransactionType;"
        ///   EconoViewFullTransactions.cs:39  "private Transaction.AmountOption _selectedAmountOption;"
        ///   EconoViewFullTransactions.cs:41  "private int _selectedDay;"
        /// NO CLEAR-ON-CLOSE HOOK EXISTS (the page has no OnDisable/Hide) and none is needed:
        /// EconoViewFullTransactionsScrollerController.Load has exactly ONE caller in the game - the
        /// method patched here - so the stash is always the selection of the refresh in flight, and
        /// Show() (:43-54) resets all three and refreshes, so a reopen starts unfiltered.
        /// KNOWN LIMIT (UI, not behaviour): the TYPE dropdown's options are built from this machine's
        /// OWN queue (:56-71), so a type only a partner has is never offered - every type that IS
        /// offered filters the partner rows correctly.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.EconoView.EconoViewFullTransactions), "RefreshTransactionsList")]
        public static class Patch_EconoViewFullTx_FilterStash
        {
            static void Prefix(int ____selectedDay, string ____selectedTransactionType,
                               Transaction.AmountOption ____selectedAmountOption)
            {
                try { CompanyFeed.NoteFullPageFilter(____selectedDay, ____selectedTransactionType, (int)____selectedAmountOption); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] filter stash refused: {ex.Message}"); }
            }
        }

        /// <summary>R4, the paint (T5 in the original plan). A row whose model the screen overlay above
        /// built for a PARTNER carries that member's colour on the row LABEL only (the amount keeps the
        /// game's own green/red, EconoViewLastTransactionCellView.cs:29). ___label is the cell's own serialized field - decompile line
        /// EconoViewLastTransactionCellView.cs:12, "private TextLocalizationComponent label;" - and its
        /// TMP object is reached through the Localizor component's TextContainer member, exactly as
        /// HousingMapCues reaches it. EnhancedScroller RECYCLES these cells, so a row that is NOT a
        /// partner's must have the label's ORIGINAL colour put back or it keeps the last one's.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionCellView),
                      nameof(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionCellView.SetData))]
        public static class Patch_EconoViewLastTxCell_PartnerTint
        {
            /// <summary>m2 (r4 re-check): the entry keeps the LABEL as well as its colour. Unity REUSES
            /// instance ids after a destroy, so an id alone can hand a new label the dead one's
            /// "original" colour - the stored label is compared to the live one and the entry rebuilt
            /// when they differ. A destroyed label reads as null through Unity's ==, which is what lets
            /// SweepDeadLabels drop it; the whole map is emptied at the scene boundary by
            /// ForgetLabelColours (called from CompanyFeed.Reset).</summary>
            private struct LabelHome { public TMPro.TMP_Text Label; public UnityEngine.Color Colour; }

            private static readonly Dictionary<int, LabelHome> _labelHomeColour = new();

            /// <summary>m2: the scene boundary - every label in this map died with the scene.</summary>
            public static void ForgetLabelColours() => _labelHomeColour.Clear();

            private static void SweepDeadLabels()
            {
                List<int>? dead = null;
                foreach (var kv in _labelHomeColour) if (kv.Value.Label == null) (dead ??= new List<int>()).Add(kv.Key);
                if (dead == null) return;
                foreach (var k in dead) _labelHomeColour.Remove(k);
            }

            static void Postfix(UI.Smartphone.Apps.EconoView.EconoViewLastTransactionModel data,
                                Localizor.LanguageChangeEvent.TextLocalizationComponent ___label)
            {
                try
                {
                    if (data == null || ___label == null) return;
                    // m1 (r4 re-check): THE MEMBERSHIP READ COMES FIRST. A machine that has never been in
                    // a company has never tinted a label, so the map is empty and there is nothing to put
                    // back: it leaves here having paid two field reads per cell and no reflection at all.
                    // The map is NOT part of the test once it has entries - a machine that has just left a
                    // company must still restore what it tinted, which is what the second read below does.
                    bool member = MergerSync.IAmMember;
                    if (!member && _labelHomeColour.Count == 0) return;

                    if (!(HousingMapCues.GetMember(___label, "TextContainer") is TMPro.TMP_Text tc)) return;
                    int id = tc.GetInstanceID();
                    if (!_labelHomeColour.TryGetValue(id, out var h) || h.Label != tc)
                    {
                        if (_labelHomeColour.Count >= 128) SweepDeadLabels();
                        h = new LabelHome { Label = tc, Colour = tc.color };
                        _labelHomeColour[id] = h;
                    }

                    if (!member
                        || !CompanyFeed.TryOwnerOfModel(data, out var pid)
                        || !PlayerColours.TryColourFor(pid, out var c))
                    { tc.color = h.Colour; return; }  // this machine's own row: the label's own colour
                    tc.color = c;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Feed] row tint: {ex.Message}"); }
            }
        }

        /// <summary>D19-4: NET WORTH on the persona page becomes the COMPANY figure — shared cash
        /// (already one number: the MergerWallet mirror) + every member's investments − every member's
        /// remaining loans + every member's assets. The page lists ONE label per component, not one per
        /// member, so the components are SUMS and there is nothing per-member to colour here. LABEL:
        /// the company display name is appended to the page's existing net-worth label in parentheses —
        /// an existing on-screen string, no new wording.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.Persona.CharacterInfo), "OnEnable")]
        public static class Patch_CharacterInfo_CompanyWealth
        {
            static void Postfix(TMPro.TMP_Text ___investmentsLabel, TMPro.TMP_Text ___loansLabel,
                                TMPro.TMP_Text ___assetsLabel, TMPro.TMP_Text ___personalWealthLabel)
            {
                try
                {
                    if (!MergerSync.IAmMember) return;
                    CompanyBooks.ApplyPending("persona page");
                    CompanyBooks.CompanyWealth(out float inv, out float loans, out float assets);
                    if (inv == 0f && loans == 0f && assets == 0f)
                    { Plugin.Logger.LogInfo("[Books] persona page left personal: no partner has published its wealth components yet."); return; }

                    var w = Helpers.PlayerHelper.GetPersonalWealth();
                    float tInv = w.totalInvestments + inv, tLoans = w.totalLoans + loans, tAssets = w.totalAssets + assets;
                    float net = w.cash + tInv - tLoans + tAssets;

                    if (___investmentsLabel != null) ___investmentsLabel.SetText(tInv.ToShortCurrencyFormat(abbreviated: true));
                    if (___loansLabel != null) ___loansLabel.SetText((tLoans > 0f) ? ("-" + tLoans.ToShortCurrencyFormat(abbreviated: true)) : tLoans.ToShortCurrencyFormat(abbreviated: true));
                    if (___assetsLabel != null) ___assetsLabel.SetText(tAssets.ToShortCurrencyFormat(abbreviated: true));
                    if (___personalWealthLabel != null)
                    {
                        string company = MergerSync.MyGroupDisplayName ?? "";
                        ___personalWealthLabel.SetText(net.ToShortCurrencyFormat(abbreviated: true)
                                                     + (string.IsNullOrEmpty(company) ? "" : $" ({company})"));
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Books] persona net worth: {ex.Message}"); }
            }
        }

        // ══ MERGER PHASE 4c PART 1 — THE HEADQUARTERS PLAN UNION VIEW (D20-6/D20-8, 2026-09-11) ══
        //
        // Open a merged PARTNER's headquarters and its five plan tabs show THAT headquarters' plans.
        // Logistics' rows are already ON this machine as wave 4's TAGGED INSTALLED display copies (its
        // execution pass is guarded); only the native page filter hid them, and U4 (HQ-UNION-2) lifts that
        // in Patch_LogisticsPlanList_UnionRows below. The other FOUR families are drawn at the SCREEN LAYER
        // only, because nothing of theirs may be installed here: the postfixes below hand
        // CompanyPlans' DETACHED row objects to the list's OWN private SetUpPlanEntry, so the rows are the
        // game's own rows, in the game's own order, with no new on-screen text — and nothing is ever added
        // to this machine's game lists (an installed HR/headhunter copy would train, insure and recruit
        // twice off the replicated employee, which is outside the authority veil).
        //
        // The reorderable list is deliberately NOT re-initialised after the partner rows go in: a drag maps
        // an index straight back into GetPlansForHeadquarters (e.g. HrManagersPlanList.cs:72-78), which
        // knows only the native rows, so a registered partner row would reorder the wrong plan.

        /// <summary>The shared driver for the four postfixes. Returns how many partner rows were drawn.</summary>
        private static int OverlayPlanRows(string family, object instance, System.Type listType,
                                           UnityEngine.Transform rowParent, UnityEngine.Transform buttonEntry,
                                           System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,
                                               UnityEngine.Transform>> nativeRows = null)
        {
            try
            {
                if (MergerFlip.FlippedCount == 0) return 0;            // inert without a merger
                var m = AccessTools.Method(listType, "SetUpPlanEntry");
                if (m == null)
                {
                    if (_planRowWarned.Add(family))
                        Plugin.Logger.LogWarning($"[Plans] {family}: SetUpPlanEntry is not on {listType.Name} - the partner rows cannot be drawn (once per family).");
                    return 0;
                }
                int n = CompanyPlans.ShowPartnerRows(family, rowParent, row => m.Invoke(instance, new object[] { row }), nativeRows);
                if (n > 0 && buttonEntry != null) buttonEntry.SetAsLastSibling();   // the add button stays last
                return n;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {family} overlay: {ex.Message}"); return 0; }
        }

        /// <summary>CROSS-HR-1b K3: the native list's OWN row record, (plan id -> row), snapshotted for the
        /// union to adopt the rows the game drew itself.  HrManagersPlanList.SetUpPlanEntry :105 fills
        /// `_entriesByPlanIds` (a Dictionary&lt;string, Transform&gt;) for EVERY row it builds, a shadow
        /// included.  Taken BEFORE the overlay runs - the overlay calls that same SetUpPlanEntry, so its own
        /// rows land in the dictionary too and must not be in the snapshot.  Null when the field is not
        /// there: the adoption then simply does not happen.</summary>
        private static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,
            UnityEngine.Transform>> NativeEntryRows(object instance, string field)
        {
            try
            {
                if (instance == null) return null;
                var f = AccessTools.Field(instance.GetType(), field);
                var d = f?.GetValue(instance) as System.Collections.Generic.Dictionary<string, UnityEngine.Transform>;
                if (d == null) return null;
                return new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,
                    UnityEngine.Transform>>(d);
            }
            catch { return null; }
        }

        private static readonly HashSet<string> _planRowWarned = new HashSet<string>();

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList), "RefreshPlansList")]
        public static class Patch_PricingPlanList_PartnerRows
        {
            // ___entryTemplate / ___buttonEntry = '___' + the exact field names, decompile
            // PricingManagersPlanList.cs:21-22 `[SerializeField] private PricingManagersPlanListEntry entryTemplate;`
            // and :24-25 `[SerializeField] private Transform buttonEntry;`.
            static void Postfix(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList __instance,
                                UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanListEntry ___entryTemplate,
                                UnityEngine.Transform ___buttonEntry)
            {
                CompanyPlans.RegisterList("pricing", __instance);   // HQ-PARITY-4 P1: registries, not sweeps
                OverlayPlanRows("pricing", __instance,
                                typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList),
                                ___entryTemplate != null ? ___entryTemplate.transform.parent : null, ___buttonEntry);
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList), "RefreshManagersList")]
        public static class Patch_PurchasingPlanList_PartnerRows
        {
            // ___entryTemplate / ___noPartnershipsWarning = '___' + the exact field names, decompile
            // PurchasingAgentsPlanList.cs:21 `public Transform entryTemplate;` and
            // :25-26 `[SerializeField] private GameObject noPartnershipsWarning;`.
            static void Postfix(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList __instance,
                                UnityEngine.Transform ___entryTemplate,
                                UnityEngine.GameObject ___noPartnershipsWarning)
            {
                CompanyPlans.RegisterList("purchasing", __instance);   // HQ-PARITY-4 P1
                int n = OverlayPlanRows("purchasing", __instance,
                                        typeof(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList),
                                        ___entryTemplate != null ? ___entryTemplate.parent : null, null);
                // The native tail sets the "no partnerships" notice from the OWN row count alone
                // (PurchasingAgentsPlanList.cs:81); a partner's headquarters with rows on it is not empty.
                if (n > 0 && ___noPartnershipsWarning != null) ___noPartnershipsWarning.SetActive(false);
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList), "RefreshManagersList")]
        public static class Patch_HrPlanList_PartnerRows
        {
            // ___entryTemplate / ___buttonEntry = '___' + the exact field names, decompile
            // HrManagersPlanList.cs:23-24 `[SerializeField] private Transform entryTemplate;` and
            // :26-27 `[SerializeField] private Transform buttonEntry;`.
            static void Postfix(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList __instance,
                                UnityEngine.Transform ___entryTemplate, UnityEngine.Transform ___buttonEntry)
            {
                CompanyPlans.RegisterList("hr", __instance);   // HQ-PARITY-4 P1
                // CROSS-HR-1b K3: the snapshot is an ARGUMENT, so it is taken before the overlay draws -
                // it holds exactly the rows the NATIVE refresh built, shadows included.
                OverlayPlanRows("hr", __instance, typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList),
                                ___entryTemplate != null ? ___entryTemplate.parent : null, ___buttonEntry,
                                NativeEntryRows(__instance, "_entriesByPlanIds"));
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList), "RefreshManagersList")]
        public static class Patch_HeadhunterPlanList_PartnerRows
        {
            // ___entryTemplate / ___buttonEntry = '___' + the exact field names, decompile
            // HeadhuntersPlanList.cs:23-24 `[SerializeField] private Transform entryTemplate;` and
            // :26-27 `[SerializeField] private Transform buttonEntry;`.
            static void Postfix(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList __instance,
                                UnityEngine.Transform ___entryTemplate, UnityEngine.Transform ___buttonEntry)
            {
                CompanyPlans.RegisterList("headhunter", __instance);   // HQ-PARITY-4 P1
                OverlayPlanRows("headhunter", __instance, typeof(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList),
                                ___entryTemplate != null ? ___entryTemplate.parent : null, ___buttonEntry);
            }
        }


        /// <summary>U4 (HQ-UNION-2): LOGISTICS JOINS THE UNION.  `RefreshManagersList` is private and takes no
        /// argument (decompile LogisticsManagersPlanList.cs:87-105): it hides the plan pane, clears the entry
        /// map, draws GetFilteredPlans() - the PAGE headquarters' plans of the current tab - puts the add
        /// button last and calls reorderableList.Reinitialize().  The postfix appends the rows that filter
        /// left out (CompanyPlans.ShowLogisticsUnionRows) through the list's OWN private SetUpPlanEntry
        /// (:123-145), so they are the game's own rows with no new on-screen text.
        /// Reinitialize is deliberately NOT called again: it is RunAfterOneFrame(InitializeItems)
        /// (ReorderableList.cs:42-51), so the enrolment the NATIVE call armed runs NEXT frame, while
        /// CompanyPlans.StripDragHandle takes the ReorderableListItem off every appended row with
        /// DestroyImmediate in THIS frame - InitializeItems (:52-63) can never see one.
        /// __state is `currentTab` AS IT WAS ON ENTRY: an empty tab makes native re-enter through
        /// ChangeTab("Warehouse") (:91; ChangeTab :260-274 sets currentTab and calls RefreshManagersList
        /// again), so the INNER call draws the union rows and the OUTER postfix must not draw them twice.
        /// ___entryTemplate / ___buttonEntry = '___' + the exact field names (:26-27 `[SerializeField]
        /// private LogisticsManagersPlanListEntry entryTemplate;`, :29-30 `[SerializeField] private Transform
        /// buttonEntry;`); `currentTab` is `[HideInInspector] public string currentTab;` (:44-45) - PUBLIC, so
        /// it is read directly and not by reflection.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList), "RefreshManagersList")]
        public static class Patch_LogisticsPlanList_UnionRows
        {
            static void Prefix(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList __instance, out string __state)
            {
                __state = "";
                try { if (__instance != null) __state = __instance.currentTab ?? ""; } catch { }
            }

            static void Postfix(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList __instance,
                                UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanListEntry ___entryTemplate,
                                UnityEngine.Transform ___buttonEntry, string __state)
            {
                try
                {
                    // HQ-PARITY-4 P1: the registration is BEFORE every gate - the registry answers for the
                    // pane-and-list levers and for the purchasing stock substitution, not only for a merger.
                    CompanyPlans.RegisterList("logistics", __instance);
                    if (MergerFlip.FlippedCount == 0) return;                 // inert without a merger
                    if (string.IsNullOrEmpty(__state)) return;                // the ChangeTab re-entry already drew them
                    if (___entryTemplate == null) return;
                    var listType = typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList);
                    var m = AccessTools.Method(listType, "SetUpPlanEntry");
                    if (m == null)
                    {
                        if (_planRowWarned.Add("logistics"))
                            Plugin.Logger.LogWarning($"[Plans] logistics: SetUpPlanEntry is not on {listType.Name} - the union rows cannot be drawn (once per family).");
                        return;
                    }
                    bool factory = __state.Equals("factory", StringComparison.OrdinalIgnoreCase);
                    int n = CompanyPlans.ShowLogisticsUnionRows(factory, ___entryTemplate.transform.parent,
                                                               row => m.Invoke(__instance, new object[] { row }));
                    if (n > 0 && ___buttonEntry != null) ___buttonEntry.SetAsLastSibling();   // the add button stays last
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] logistics overlay: {ex.Message}"); }
            }
        }

        /// <summary>HQ-PARITY-4 P1 - THE PANE REGISTER for the three families that had no LoadPlan postfix.
        /// One overload each, verified in the decompile: `PricingManagerPlanUI.LoadPlan(PricingManagerPlan)`
        /// (:44), `PurchasingAgentPlanUI.LoadPlan(ImportPartnership)` (:91) and
        /// `HeadhunterPlanUI.LoadPlan(HeadhunterPlan)` (:36).  HR and logistics register from the LoadPlan
        /// postfixes they already have.  What this buys: the hierarchy search off `UIs.fullMenu.bizMan` never
        /// found the PURCHASING pane in either hands-on session - the game reaches it through a direct handle
        /// (BizManBusiness.cs:98) - so that pane never refreshed on a feed and the owner's warehouse count was
        /// never substituted into it.  The pane the GAME just loaded is the pane the player is looking at.
        /// `__instance` is declared as the common base so one patch covers all three; nothing is read off it
        /// here beyond its type.</summary>
        [HarmonyPatch]
        public static class Patch_PlanPaneLoad_Register
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var a = AccessTools.Method(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerPlanUI), "LoadPlan");
                if (a != null) yield return a;
                var b = AccessTools.Method(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI), "LoadPlan");
                if (b != null) yield return b;
                var c = AccessTools.Method(typeof(HeadhunterPlanUI), "LoadPlan");
                if (c != null) yield return c;
            }

            static void Postfix(UnityEngine.Component __instance)
            {
                try
                {
                    if (__instance == null) return;
                    string fam = __instance is UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerPlanUI ? "pricing"
                               : __instance is UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI ? "purchasing"
                               : __instance is HeadhunterPlanUI ? "headhunter" : "";
                    if (fam.Length > 0) CompanyPlans.RegisterPane(fam, __instance);
                }
                catch { }
            }
        }

        // == 4c PART 2a: THE FOUR FAMILIES BECOME EDITABLE ON A PARTNER'S HEADQUARTERS (E1/E2) ==
        //
        // Part 1 refused every commit here ("[Merger] <family> edit on partner HQ ... refused - part 2").
        // Part 2a turns each of those refusals into a ROUTE: the prefix skips the native body and sends a
        // `mergerplanedit` leg (CompanyPlans.RoutePaneEdit) to the machine that RUNS that headquarters,
        // which applies the op onto its own real plan with the game's own method. Nothing is written here:
        // the member's rows are still the DETACHED temp objects, and the next fan-out redraws them.
        //
        // A commit on one of THIS player's own plans is untouched - RoutePaneEdit returns false for
        // anything that is not an overlay row, and every prefix then returns true (run the native body).
        //
        // The two commits that are NOT routed, and why:
        //  * PurchasingAgentPlanUI.SetLockTime (decompile :165-181) only writes lockTimeLabel - it is a
        //    LABEL REFRESH, not a commit, so there is nothing to route (the brief lists it as an op).
        // (r2 MAJOR-5 corrected the second of those: the headhunter recruiting-settings writes are NOT display
        // state - they are live native writes, and they are routed as `settings` legs now. See the headhunter
        // block below.)

        /// <summary>One dropdown index -> the employee id the native handler would have taken, read out of
        /// the list's own private candidate field by reflection (the field types differ per list, so nothing
        /// is Harmony-injected here - an injected field of the wrong type fails the whole patch).
        /// -1 is the game's own "unassigned" option and comes back as "".</summary>
        private static string PlanEditEmployeeId(object instance, string field, int index)
        {
            try
            {
                if (instance == null || index < 0) return "";
                var f = AccessTools.Field(instance.GetType(), field);
                var list = f != null ? f.GetValue(instance) as System.Collections.IList : null;
                if (list == null || index >= list.Count) return "";
                var e = list[index] as Entities.EmployeeInstance;
                return e != null ? (e.id ?? "") : "";
            }
            catch { return ""; }
        }

        /// <summary>HQ-PARITY-8 G3: the chosen employee, or null.  `showError: false` because a candidate the
        /// dropdown offered is always resolvable here (the roster is built out of this machine's own
        /// EmployeeInstances), and a miss must not raise the game's error toast.</summary>
        private static Entities.EmployeeInstance? EmployeeOrNull(string id)
        { try { return string.IsNullOrEmpty(id) ? null : EmployeeHelper.GetEmployeeById(id, showError: false); } catch { return null; } }

        /// <summary>The same, for a list of plan ids (HeadhuntersAutomaticReplacementTab._hrManagerPlansIds).</summary>
        private static string PlanEditStringAt(object instance, string field, int index)
        {
            try
            {
                if (instance == null || index < 0) return "";
                var f = AccessTools.Field(instance.GetType(), field);
                var list = f != null ? f.GetValue(instance) as System.Collections.IList : null;
                if (list == null || index >= list.Count) return "";
                return list[index] as string ?? "";
            }
            catch { return ""; }
        }

        // ── CREATION: 'add plan' on a partner's headquarters creates it on the RUNNER ──
        // PricingManagersPlanList.AddPlan :63-76, HrManagersPlanList.AddPlan :292-305 and
        // HeadhuntersPlanList.AddPlan :230-243 all add to THIS machine's own gi list keyed to the PARTNER's
        // address. The prefix sends the creation instead; the plan comes back on the next fan-out.

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList), "AddPlan")]
        public static class Patch_PricingPlanCreate_MergerGate
        {
            static bool Prefix()
            { try { return MergerFlip.FlippedCount != 0 ? !CompanyPlans.RoutePlanCreate("pricing") : true; } catch { return true; } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList), "AddPlan")]
        public static class Patch_HrPlanCreate_MergerGate
        {
            static bool Prefix()
            { try { return MergerFlip.FlippedCount != 0 ? !CompanyPlans.RoutePlanCreate("hr") : true; } catch { return true; } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList), "AddPlan")]
        public static class Patch_HeadhunterPlanCreate_MergerGate
        {
            static bool Prefix()
            { try { return MergerFlip.FlippedCount != 0 ? !CompanyPlans.RoutePlanCreate("headhunter") : true; } catch { return true; } }
        }

        // ── THE MANAGER / AGENT DROPDOWNS ──
        // Each handler is (plan, index, ...) and turns the index into an employee id out of its own private
        // candidate list, which filters on `x.assignedAddress == <page address>` - on a partner's
        // headquarters that is the PARTNER's staff, replicated here by MergerEmployeeSync. The route carries
        // the id; the runner assigns it to its own real plan.

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList), "OnChangedPricingManager")]
        public static class Patch_PricingManagerAssign_MergerGate
        {
            // decompile PricingManagersPlanList.cs:205 `private void OnChangedPricingManager(PricingManagerPlan
            // plan, int pricingManagerIndex, PricingManagersPlanListEntry entry)`; `_pricingManagers` :207.
            static bool Prefix(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList __instance,
                               Buildings.Office.Headquarters.PricingManagerPlan plan, int pricingManagerIndex)
            {
                try
                {
                    string who = PlanEditEmployeeId(__instance, "_pricingManagers", pricingManagerIndex);
                    // G3 (HQ-PARITY-8): the GAME's own two conditions (PricingManagersPlanList.cs:205-230) -
                    // unassigning a manager that has prices set, or picking one whose skill is lower than the
                    // plan's current manager.  Both are answerable from the copy: originalStorePrices is
                    // carried as a COUNT (CompanyPlans.BuildPricing) and PricingManagerSkillValue reads the
                    // plan's own manager, who is replicated here.  When a confirm is due the prefix ARMS and
                    // lets the native body run, so the player sees the game's dialog: OK routes (the shared
                    // HudConfirm wrapper swaps the callback) and cancel re-selects the old option natively.
                    if (plan == null) return true;   // no row to decide about: the native body owns the call
                    // FOLD b H1: THE GAME'S OWN EQUALITY TEST COMES FIRST (PricingManagersPlanList.cs:208
                    // - the chosen manager's id against plan.assignedEmployeeId, then an early return).
                    // Re-picking the person already on the row opens NO dialog, so arming here would leave a
                    // stale route sitting in wait to hijack the next confirmation anywhere in the game.
                    if (who == (plan.assignedEmployeeId ?? "")) return true;   // who is never null - PlanEditEmployeeId returns "" for the unassigned option
                    var emp = EmployeeOrNull(who);
                    bool confirm = string.IsNullOrEmpty(who)
                        ? (plan.originalStorePrices != null && plan.originalStorePrices.Count > 0)
                        : (emp != null && emp.GetSkillValue("ba:skill_pricingmanager") < plan.PricingManagerSkillValue);
                    // FOLD b M3: ONE delegate for both legs - the confirmed route now carries the same
                    // display write the direct route does, so after OK the dropdown does not revert at the
                    // next rebuild while the owner's publish is still on its way.
                    System.Action<object> mut = row =>
                    { var pl = row as Buildings.Office.Headquarters.PricingManagerPlan; if (pl != null) pl.assignedEmployeeId = string.IsNullOrEmpty(who) ? null : who; };
                    if (confirm && CompanyPlans.ArmConfirmRoute("pricing", plan, "manager", "manager assignment", who, mut)) return true;
                    // H4: the runner's own write, mirrored on the DISPLAY copy so the dropdown settles at once.
                    return !CompanyPlans.RoutePaneEdit("pricing", "manager assignment", plan, "manager", who, 0, 0f, false, mut);
                }
                catch { return true; }
            }

            // FOLD b H1: the dialog captures its callback synchronously inside the original (the shared
            // HudConfirm wrapper takes the route at Show() time), so clearing the arm as this pane method
            // returns cannot cancel a live route - it only stops a stale one outliving the call.  Same
            // pattern as the purchasing pane's Finalizer.
            static void Finalizer() { try { CompanyPlans.DisarmConfirmRoute(); } catch { } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList), "OnChangedHrManager")]
        public static class Patch_HrManagerAssign_MergerGate
        {
            // decompile HrManagersPlanList.cs:178-180 `private void OnChangedHrManager(HrManagerPlan plan, int
            // hrManagerIndex, Transform entry, Dropdown dropdown)` / `_hrManagers[hrManagerIndex].id`.
            static bool Prefix(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList __instance,
                               Buildings.Office.Headquarters.HrManagerPlan plan, int hrManagerIndex)
            {
                try
                {
                    string who = PlanEditEmployeeId(__instance, "_hrManagers", hrManagerIndex);
                    // G3 (HQ-PARITY-8): the GAME's three confirmations (HrManagersPlanList.cs:178-235) all
                    // stand on the same two tests, and only when a manager is being ASSIGNED: can the new
                    // manager hold every assigned employee, and is their skill enough for the plan's health
                    // insurance.  DEVIATION worth knowing: the copy's assignedEmployees list drops anybody
                    // this machine cannot resolve (CompanyPlans.BuildHr), so the head-count test can read
                    // LOW and skip a prompt the owner would have seen; the insurance test is exact (the
                    // agreement travels whole).  Unassigning never prompts natively, so it routes at once.
                    if (plan == null) return true;   // no row to decide about: the native body owns the call
                    // FOLD b H1: the game's own equality test first (HrManagersPlanList.cs:180 - the chosen
                    // id against plan.assignedEmployeeId, then an early return).  See the pricing prefix.
                    if (who == (plan.assignedEmployeeId ?? "")) return true;   // who is never null - PlanEditEmployeeId returns "" for the unassigned option
                    var emp = EmployeeOrNull(who);
                    bool confirm = false;
                    if (emp != null)
                    {
                        float sk = emp.GetSkillValue("ba:skill_hrmanager");
                        bool fits = Buildings.Office.Headquarters.HrManagerHelper.CalculateMaxAssignableEmployees(sk)
                                    >= (plan.assignedEmployees != null ? plan.assignedEmployees.Count : 0);
                        bool insuranceOk = plan.healthInsurancePlan == null
                                        || sk >= Helpers.HealthInsuranceHelper.GetMinSkillForPlan(plan.healthInsurancePlan.planType);
                        confirm = !fits || !insuranceOk;
                    }
                    // FOLD b M3: one delegate for both legs (see the pricing prefix).
                    System.Action<object> mut = row =>
                    { var pl = row as Buildings.Office.Headquarters.HrManagerPlan; if (pl != null) pl.assignedEmployeeId = string.IsNullOrEmpty(who) ? null : who; };
                    if (confirm && CompanyPlans.ArmConfirmRoute("hr", plan, "manager", "manager assignment", who, mut)) return true;
                    // H4: the runner's own write, mirrored on the DISPLAY copy so the dropdown settles at once.
                    return !CompanyPlans.RoutePaneEdit("hr", "manager assignment", plan, "manager", who, 0, 0f, false, mut);
                }
                catch { return true; }
            }

            // FOLD b H1: the dialog captures its callback synchronously inside the original (the shared
            // HudConfirm wrapper takes the route at Show() time), so clearing the arm as this pane method
            // returns cannot cancel a live route - it only stops a stale one outliving the call.  Same
            // pattern as the purchasing pane's Finalizer.
            static void Finalizer() { try { CompanyPlans.DisarmConfirmRoute(); } catch { } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList), "OnChangedHeadhunter")]
        public static class Patch_HeadhunterAssign_MergerGate
        {
            // decompile HeadhuntersPlanList.cs:124-126 `_headhunters[headhunterIndex].id`.
            static bool Prefix(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList __instance,
                               Buildings.Office.Headquarters.HeadhunterPlan plan, int headhunterIndex)
            {
                try
                {
                    string who = PlanEditEmployeeId(__instance, "_headhunters", headhunterIndex);
                    // G3 (HQ-PARITY-8): the GAME's three confirmations (HeadhuntersPlanList.cs:124-179) stand
                    // on two tests, and only when a headhunter is being ASSIGNED: can the new one hold every
                    // assigned HR plan, and do its deal-breaker points cover the ones already chosen.  Both
                    // read fields the copy carries whole (assignedHrPlans, dealBreakerTypes - BuildHeadhunter)
                    // against static data, so the answer matches the owner's exactly.
                    if (plan == null) return true;   // no row to decide about: the native body owns the call
                    // FOLD b H1: the game's own equality test first (HeadhuntersPlanList.cs:127 - the chosen
                    // id against plan.assignedEmployeeId, then an early return).  See the pricing prefix.
                    if (who == (plan.assignedEmployeeId ?? "")) return true;   // who is never null - PlanEditEmployeeId returns "" for the unassigned option
                    var emp = EmployeeOrNull(who);
                    bool confirm = false;
                    if (emp != null)
                    {
                        float sk = emp.GetSkillValue("ba:skill_headhunter");
                        int held = 0;
                        if (plan.assignedHrPlans != null)
                            foreach (var s in plan.assignedHrPlans) if (!string.IsNullOrEmpty(s)) held++;
                        bool fits = Buildings.Office.Headquarters.HeadhunterHelper.CalculateMaxHrPlans(sk) >= held;
                        int cost = 0;
                        if (plan.dealBreakerTypes != null)
                            foreach (var t in plan.dealBreakerTypes)
                            { try { var d = Buildings.Office.Headquarters.HeadhunterHelper.GetData(t); if (d != null) cost += d.recruitmentPointCost; } catch { } }
                        bool pointsOk = Buildings.Office.Headquarters.HeadhunterHelper.CalculateMaxDealBreakersPoints(sk) >= cost;
                        confirm = !fits || !pointsOk;
                    }
                    // FOLD b M3: one delegate for both legs (see the pricing prefix).
                    System.Action<object> mut = row =>
                    { var pl = row as Buildings.Office.Headquarters.HeadhunterPlan; if (pl != null) pl.assignedEmployeeId = string.IsNullOrEmpty(who) ? null : who; };
                    if (confirm && CompanyPlans.ArmConfirmRoute("headhunter", plan, "manager", "manager assignment", who, mut)) return true;
                    // H4: the runner's own write, mirrored on the DISPLAY copy so the dropdown settles at once.
                    return !CompanyPlans.RoutePaneEdit("headhunter", "manager assignment", plan, "manager", who, 0, 0f, false, mut);
                }
                catch { return true; }
            }

            // FOLD b H1: the dialog captures its callback synchronously inside the original (the shared
            // HudConfirm wrapper takes the route at Show() time), so clearing the arm as this pane method
            // returns cannot cancel a live route - it only stops a stale one outliving the call.  Same
            // pattern as the purchasing pane's Finalizer.
            static void Finalizer() { try { CompanyPlans.DisarmConfirmRoute(); } catch { } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList), "OnChangedPurchasingAgent")]
        public static class Patch_PurchasingAgentAssign_MergerGate
        {
            // decompile PurchasingAgentsPlanList.cs:122-124 `_purchasingAgents[purchasingAgentIndex].id`.
            static bool Prefix(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList __instance,
                               Entities.ImportPartnership plan, int purchasingAgentIndex)
            {
                try
                {
                    string who = PlanEditEmployeeId(__instance, "_purchasingAgents", purchasingAgentIndex);
                    // G3 (HQ-PARITY-8): the GAME asks EVERY TIME an agent is assigned and never when one is
                    // unassigned (PurchasingAgentsPlanList.cs:122-148 `bizman_purchasingagent_change_confirm`),
                    // so the condition needs nothing off the plan at all.
                    // FOLD b H1: the game's own equality test first (PurchasingAgentsPlanList.cs:124-128 -
                    // the chosen id against plan.employeeInstanceId, then an early return).  Re-picking the
                    // agent already on the row opens no dialog, so arming would leave a stale route waiting
                    // to hijack the next confirmation.
                    if (plan == null) return true;   // no row to decide about: the native body owns the call
                    if (who == (plan.employeeInstanceId ?? "")) return true;   // who is never null - PlanEditEmployeeId returns "" for the unassigned option
                    // FOLD b M3: one delegate for both legs (see the pricing prefix).
                    System.Action<object> mut = row =>
                    { var pl = row as Entities.ImportPartnership; if (pl != null) pl.employeeInstanceId = string.IsNullOrEmpty(who) ? null : who; };   // fold c: null on unassign, as the owner's UnAssignEmployee writes
                    if (!string.IsNullOrEmpty(who)
                        && CompanyPlans.ArmConfirmRoute("purchasing", plan, "agent", "agent assignment", who, mut)) return true;
                    // H4: the runner's own write, mirrored on the DISPLAY copy so the dropdown settles at once.
                    return !CompanyPlans.RoutePaneEdit("purchasing", "agent assignment", plan, "agent", who, 0, 0f, false, mut);
                }
                catch { return true; }
            }

            // FOLD b H1: the dialog captures its callback synchronously inside the original (the shared
            // HudConfirm wrapper takes the route at Show() time), so clearing the arm as this pane method
            // returns cannot cancel a live route - it only stops a stale one outliving the call.  Same
            // pattern as the purchasing pane's Finalizer.
            static void Finalizer() { try { CompanyPlans.DisarmConfirmRoute(); } catch { } }
        }

        // ══ U2 (user ruling 2026-09-12, plan D37) — THE MANAGER DROPDOWN FOLLOWS THE ROW'S HEADQUARTERS ══
        //
        // Native builds each private candidate roster from the PAGE address and the change handler indexes
        // that same list: HrManagersPlanList.cs:168-177, PricingManagersPlanList.cs:174-191,
        // PurchasingAgentsPlanList.cs:109-120 and HeadhuntersPlanList.cs:114-123 all filter
        // `x.assignedAddress == InstanceBehavior<UIs>.Instance.fullMenu.bizMan.business.address`.
        // A UNION row belongs to ANOTHER headquarters, so its manager has to be picked from THAT
        // headquarters' staff: a partner's people exist here as injected copies whose assignedAddress is
        // that headquarters (the roster sync), and for my own other headquarters the records are real.
        // The postfixes below REBUILD the private list with the row's own address and the same skill test
        // native uses, then re-set the dropdown exactly as native does — so both the native change handler
        // and the mod's routing prefix (PlanEditEmployeeId) index the right person. A row that belongs to
        // the page's own headquarters is untouched: the first test returns.

        private static readonly HashSet<string> _dropdownRescoped = new HashSet<string>();

        /// <summary>Does this row belong to a DIFFERENT headquarters than the page on screen?  The call
        /// below is `page.Equals(rowHq)`, and Address compares BY VALUE there: the `Equals` OVERRIDE at
        /// BigAmbitions.Items/Address.cs:40 (`operator ==` is the separate :80).  HQ-UNION-1b, review
        /// MINOR-1: a page-HQ plan whose Address object was deserialised separately from business.address is
        /// still the same headquarters, so the test is value equality, never reference identity.  Logged
        /// once per family per session.</summary>
        private static bool RowHqOffPage(object rowHq, string family)
        {
            try
            {
                if (rowHq == null) return false;
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var biz = ui != null && ui.fullMenu != null && ui.fullMenu.bizMan != null ? ui.fullMenu.bizMan.business : null;
                object page = biz != null ? biz.address : null;
                if (page == null || ReferenceEquals(page, rowHq) || page.Equals(rowHq)) return false;
                if (_dropdownRescoped.Add(family))
                    Plugin.Logger.LogInfo($"[Plans] the {family} manager dropdown now follows the row's own headquarters (once per family).");
                return true;
            }
            catch { return false; }
        }

        /// <summary>The private candidate list the native change handler indexes.</summary>
        private static void SetPrivateList(object instance, string field, object people)
        {
            var f = AccessTools.Field(instance.GetType(), field);
            if (f != null) f.SetValue(instance, people);
        }

        /// <summary>Native's second half: the "no manager assigned" pop-up's own dropdown is filled from the
        /// SAME list (HrManagersPlanList.cs:176, PricingManagersPlanList.cs:190, HeadhuntersPlanList.cs:122,
        /// PurchasingAgentsPlanList.cs:118).  Reflection, so the four UI types need not be named here.</summary>
        private static void RescopePopUp(object instance, string uiField, object people)
        {
            try
            {
                if (instance == null) return;
                var f = AccessTools.Field(instance.GetType(), uiField);
                var ui = f != null ? f.GetValue(instance) : null;
                if (ui == null) return;
                var pf = AccessTools.Field(ui.GetType(), "noManagerAssignedPopUp");
                var pop = pf != null ? pf.GetValue(ui) : null;
                if (pop == null) return;
                var m = AccessTools.Method(pop.GetType(), "SetUpEmployeeDropdown");
                if (m != null) m.Invoke(pop, new object[] { people });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] no-manager pop-up dropdown: {ex.Message}"); }
        }

        private static UI.Elements.Dropdown ManagerDropdownOf(UnityEngine.Transform entry)
        {
            var t = entry != null ? entry.Find("ManagerDropdown") : null;
            return t != null ? t.GetComponent<UI.Elements.Dropdown>() : null;
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList), "UpdateHrManagerDropdownForPlan")]
        public static class Patch_HrDropdown_RowHq
        {
            static void Postfix(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList __instance,
                                Buildings.Office.Headquarters.HrManagerPlan plan,
                                UnityEngine.Transform selectedEntry)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || plan == null || selectedEntry == null) return;
                    var hq = plan.headquartersAddress;
                    if (!RowHqOffPage(hq, "hr")) return;
                    var gi = SaveGameManager.Current;
                    if (gi == null || gi.EmployeeInstances == null) return;
                    var people = gi.EmployeeInstances.FindAll((Entities.EmployeeInstance x) =>
                        x != null && x.assignedAddress == hq && x.HasSkill("ba:skill_hrmanager")
                        && !gi.hrManagerPlans.Exists((Buildings.Office.Headquarters.HrManagerPlan y) => y.id != plan.id && y.assignedEmployeeId == x.id)
                        && x.IsAssignedToAnyWorkShift());
                    SetPrivateList(__instance, "_hrManagers", people);
                    var opts = new System.Collections.Generic.List<string> { "common_unassigned".GetLocalization() };
                    foreach (var x in people) opts.Add(x.GetEmployeeNameWithInfo());
                    var dd = ManagerDropdownOf(selectedEntry);
                    if (dd != null) dd.SetOptions(opts, localize: false, people.FindIndex((Entities.EmployeeInstance x) => x.id == plan.assignedEmployeeId) + 1);
                    RescopePopUp(__instance, "hrManagerPlanUI", people);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] hr dropdown rescope: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList), "UpdatePricingManagerDropdownForPlan")]
        public static class Patch_PricingDropdown_RowHq
        {
            // decompile PricingManagersPlanList.cs:174 — the second parameter is the ENTRY type, not a Transform,
            // and the dropdown is reached through its own `ManagerDropdown` (:188).
            static void Postfix(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList __instance,
                                Buildings.Office.Headquarters.PricingManagerPlan plan,
                                UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanListEntry selectedEntry)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || plan == null || selectedEntry == null) return;
                    var hq = plan.headquartersAddress;
                    if (!RowHqOffPage(hq, "pricing")) return;
                    var gi = SaveGameManager.Current;
                    if (gi == null || gi.EmployeeInstances == null) return;
                    var people = gi.EmployeeInstances.FindAll((Entities.EmployeeInstance x) =>
                        x != null && x.assignedAddress == hq && x.HasSkill("ba:skill_pricingmanager")
                        && !Buildings.Office.Headquarters.PricingManagerHelper.IsEmployeeAssignedToOtherPlan(x.id, plan.id)
                        && x.IsAssignedToAnyWorkShift());
                    SetPrivateList(__instance, "_pricingManagers", people);
                    var opts = new System.Collections.Generic.List<string> { "common_unassigned".GetLocalization() };
                    foreach (var x in people) opts.Add(x.GetEmployeeNameWithInfo());
                    var dd = selectedEntry.ManagerDropdown;
                    if (dd != null) dd.SetOptions(opts, localize: false, people.FindIndex((Entities.EmployeeInstance x) => x.id == plan.assignedEmployeeId) + 1);
                    RescopePopUp(__instance, "pricingManagerPlanUI", people);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] pricing dropdown rescope: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList), "UpdateHeadhunterDropdownForPlan")]
        public static class Patch_HeadhunterDropdown_RowHq
        {
            static void Postfix(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList __instance,
                                Buildings.Office.Headquarters.HeadhunterPlan plan,
                                UnityEngine.Transform selectedEntry)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || plan == null || selectedEntry == null) return;
                    var hq = plan.headquartersAddress;
                    if (!RowHqOffPage(hq, "headhunter")) return;
                    var gi = SaveGameManager.Current;
                    if (gi == null || gi.EmployeeInstances == null) return;
                    var people = gi.EmployeeInstances.FindAll((Entities.EmployeeInstance x) =>
                        x != null && x.assignedAddress == hq && x.HasSkill("ba:skill_headhunter")
                        && !gi.headhunterPlans.Exists((Buildings.Office.Headquarters.HeadhunterPlan y) => y.id != plan.id && y.assignedEmployeeId == x.id)
                        && x.IsAssignedToAnyWorkShift());
                    SetPrivateList(__instance, "_headhunters", people);
                    var opts = new System.Collections.Generic.List<string> { "common_unassigned".GetLocalization() };
                    foreach (var x in people) opts.Add(x.GetEmployeeNameWithInfo());
                    var dd = ManagerDropdownOf(selectedEntry);
                    if (dd != null) dd.SetOptions(opts, localize: false, people.FindIndex((Entities.EmployeeInstance x) => x.id == plan.assignedEmployeeId) + 1);
                    RescopePopUp(__instance, "planUI", people);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] headhunter dropdown rescope: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList), "UpdatePurchasingAgentDropdownForPlan")]
        public static class Patch_PurchasingDropdown_RowHq
        {
            // decompile PurchasingAgentsPlanList.cs:109-120: the agent's id field is `employeeInstanceId`, and
            // the dropdown's interactability is re-applied here because native sets it AFTER SetOptions (:117).
            static void Postfix(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList __instance,
                                Entities.ImportPartnership plan,
                                UnityEngine.Transform selectedEntry)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || plan == null || selectedEntry == null) return;
                    var hq = plan.headquartersAddress;
                    if (!RowHqOffPage(hq, "purchasing")) return;
                    var gi = SaveGameManager.Current;
                    if (gi == null || gi.EmployeeInstances == null) return;
                    var people = gi.EmployeeInstances.FindAll((Entities.EmployeeInstance x) =>
                        x != null && x.assignedAddress == hq && x.HasSkill("ba:skill_purchasingagent")
                        && !gi.importPartnerships.Exists((Entities.ImportPartnership y) => y.id != plan.id && y.employeeInstanceId == x.id)
                        && x.IsAssignedToAnyWorkShift());
                    SetPrivateList(__instance, "_purchasingAgents", people);
                    var opts = new System.Collections.Generic.List<string> { "common_unassigned".GetLocalization() };
                    foreach (var x in people) opts.Add(x.GetEmployeeNameWithInfo());
                    var dd = ManagerDropdownOf(selectedEntry);
                    if (dd != null)
                    {
                        dd.SetOptions(opts, localize: false, people.FindIndex((Entities.EmployeeInstance x) => x.id == plan.employeeInstanceId) + 1);
                        dd.SetInteractable(Entities.DeliveryHelper.CanModifyContract(plan.nextDeliveryDay));
                    }
                    RescopePopUp(__instance, "purchasingAgentPlanUISettings", people);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] purchasing dropdown rescope: {ex.Message}"); }
            }
        }


        /// <summary>U4 (c): the LOGISTICS manager dropdown follows the ROW's own headquarters.
        /// `UpdateLogisticsManagerDropdownForPlan(LogisticsManagerPlan plan, LogisticsManagersPlanListEntry
        /// selectedEntry)` is private (decompile LogisticsManagersPlanList.cs:159-166): it fills
        /// `_logisticsManagers` from EmployeeInstances whose assignedAddress is the PAGE's address (:161),
        /// sets the entry's own ManagerDropdown (:164) and fills the no-manager pop-up from the SAME list
        /// (logisticsManagerPlanUI.noManagerAssignedPopUp.SetUpEmployeeDropdown, :165).  All three are redone
        /// here with the row's headquarters and native's own test (the logistics skill, no other plan already
        /// holding the manager, a work shift), so OnChangedLogisticsManager (:168-190), which indexes
        /// `_logisticsManagers`, picks the right person.  A row of the page's own headquarters is untouched.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList), "UpdateLogisticsManagerDropdownForPlan")]
        public static class Patch_LogisticsDropdown_RowHq
        {
            static void Postfix(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanList __instance,
                                Buildings.Office.Headquarters.LogisticsManagerPlan plan,
                                UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagersPlanListEntry selectedEntry)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0 || plan == null || selectedEntry == null) return;
                    var hq = plan.headquartersAddress;
                    if (!RowHqOffPage(hq, "logistics")) return;
                    var gi = SaveGameManager.Current;
                    if (gi == null || gi.EmployeeInstances == null || gi.logisticsManagerPlans == null) return;
                    var people = gi.EmployeeInstances.FindAll((Entities.EmployeeInstance x) =>
                        x != null && x.assignedAddress == hq && x.HasSkill("ba:skill_logisticsmanager")
                        && !gi.logisticsManagerPlans.Exists((Buildings.Office.Headquarters.LogisticsManagerPlan y) => y.id != plan.id && y.assignedEmployeeId == x.id)
                        && x.IsAssignedToAnyWorkShift());
                    SetPrivateList(__instance, "_logisticsManagers", people);
                    var opts = new System.Collections.Generic.List<string> { "common_unassigned".GetLocalization() };
                    foreach (var x in people) opts.Add(x.GetEmployeeNameWithInfo());
                    var dd = selectedEntry.ManagerDropdown;
                    if (dd != null) dd.SetOptions(opts, localize: false, people.FindIndex((Entities.EmployeeInstance x) => x.id == plan.assignedEmployeeId) + 1);
                    RescopePopUp(__instance, "logisticsManagerPlanUI", people);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] logistics dropdown rescope: {ex.Message}"); }
            }
        }

        /// <summary>H4 (D20-8): ONE PRICING MANAGER PER NEIGHBOURHOOD, COMPANY-WIDE.
        /// `PricingManagerHelper.IsNeighborhoodSupervised(neighborhood, exceptPlanId)` (decompile :144-154)
        /// is `foreach (PricingManagerPlan plan in Plans) if (!(plan.id == exceptPlanId) &amp;&amp;
        /// plan.supervisedNeighborhood == neighborhood) return true;` — this player's OWN plans only, so two
        /// members could each supervise the same neighbourhood and fight over its prices every update.
        /// The postfix widens the SAME predicate to the partners' published pricing plans. Its one caller is
        /// the neighbourhood dropdown's own filter (PricingManagerPlanUI.cs:81), so a taken neighbourhood is
        /// simply not offered — the game's own refusal path, no new text.</summary>
        [HarmonyPatch(typeof(Buildings.Office.Headquarters.PricingManagerHelper), "IsNeighborhoodSupervised")]
        public static class Patch_PricingNeighbourhood_CompanyWide
        {
            static void Postfix(string neighborhood, string exceptPlanId, ref bool __result)
            {
                try
                {
                    if (__result || MergerFlip.FlippedCount == 0) return;   // inert without a merger
                    if (CompanyPlans.SupervisedByPartner(neighborhood, exceptPlanId)) __result = true;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] neighbourhood rule: {ex.Message}"); }
            }
        }

        // ══ 4c PART 1 r2 - THE REVIEW FIXES (MAJOR-1/2/3, 2026-09-11) ══════════════════════════════
        //
        // MAJOR-1 THE DRAG stands unchanged in part 2a: a partner's row is still NOT DRAGGABLE. (a) is in
        // CompanyPlans.StripDragHandle: the overlay row loses its ReorderableListItem the instant it is built,
        // so ReorderableList.InitializeItems (ReorderableList.cs:52-63, deferred one frame by :42-51) never
        // enrols it and BeginDrag (:65-69) never counts it. (b) is here: the drop handler itself. It maps an
        // index straight back into the NATIVE list (PricingManagersPlanList.cs:81, HrManagersPlanList.cs:73,
        // HeadhuntersPlanList.cs:58, PurchasingAgentsPlanList.cs:56) - empty on a partner's headquarters - and
        // the throw lands inside ReorderableList.EndDrag at :173, BEFORE `_draggingItem = null` (:176) and the
        // layout restore (:177-188), leaving the tab stuck mid-drag. A prefix that returns false makes the drop
        // a no-op and lets EndDrag finish its own cleanup. Row ORDER is a per-save display preference, not a
        // plan field, so there is nothing for a route to carry.

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList), "UpdatePlanOrder")]
        public static class Patch_PricingPlanReorder_MergerGate
        {
            static bool Prefix(int fromIndex, int toIndex)
            { try { return !CompanyPlans.RefuseReorder("pricing", fromIndex, toIndex); } catch { return true; } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList), "UpdatePlanOrder")]
        public static class Patch_HrPlanReorder_MergerGate
        {
            static bool Prefix(int fromIndex, int toIndex)
            { try { return !CompanyPlans.RefuseReorder("hr", fromIndex, toIndex); } catch { return true; } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList), "UpdatePlanOrder")]
        public static class Patch_HeadhunterPlanReorder_MergerGate
        {
            static bool Prefix(int fromIndex, int toIndex)
            { try { return !CompanyPlans.RefuseReorder("headhunter", fromIndex, toIndex); } catch { return true; } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList), "OnPlanReordered")]
        public static class Patch_PurchasingPlanReorder_MergerGate
        {
            static bool Prefix(int fromIndex, int toIndex)
            { try { return !CompanyPlans.RefuseReorder("purchasing", fromIndex, toIndex); } catch { return true; } }
        }

        // ── PART 2a (E2): a partner's row is SELECTABLE again ──
        // Each row's button invokes its list's own SelectPlan (PricingManagersPlanList.cs:110 through the
        // entry's Initialize callback, HrManagersPlanList.cs:100-103, HeadhuntersPlanList.cs:91-94,
        // PurchasingAgentsPlanList.cs:92-95), which runs LoadPlan and arms the pane's commits. Those commits
        // are all routed now, so the pane may open on the DETACHED row - E2's temp plan object. The prefix
        // stays only to log it once per family and owner; the FINALIZER is the new part: LoadPlan reads
        // fields a partner's row may not be able to resolve on this machine, and a throw inside a click
        // delegate would leave the list mid-update. Swallowing it leaves the pane as it was, with the reason
        // in the log and no new on-screen text.

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanList), "SelectPlan")]
        public static class Patch_PricingPlanSelect_MergerGate
        {
            // `PricingManagerPlan plan = entry.Plan;` (PricingManagersPlanList.cs:126) - the row carries its plan
            // (PricingManagersPlanListEntry.cs:38 `public PricingManagerPlan Plan { get; private set; }`).
            static bool Prefix(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagersPlanListEntry entry)
            { try { return entry == null || !CompanyPlans.RefuseSelect("pricing", entry.Plan); } catch { return true; } }
            static Exception Finalizer(Exception __exception)
            { return CompanyPlans.SwallowPaneOpen("pricing", __exception); }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagersPlanList), "SelectPlan")]
        public static class Patch_HrPlanSelect_MergerGate
        {
            static bool Prefix(Buildings.Office.Headquarters.HrManagerPlan plan)
            { try { return !CompanyPlans.RefuseSelect("hr", plan); } catch { return true; } }
            static Exception Finalizer(Exception __exception)
            { return CompanyPlans.SwallowPaneOpen("hr", __exception); }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.Headhunters.HeadhuntersPlanList), "SelectPlan",
                      new System.Type[] { typeof(UnityEngine.Transform), typeof(Buildings.Office.Headquarters.HeadhunterPlan) })]
        public static class Patch_HeadhunterPlanSelect_MergerGate
        {
            static bool Prefix(Buildings.Office.Headquarters.HeadhunterPlan plan)
            { try { return !CompanyPlans.RefuseSelect("headhunter", plan); } catch { return true; } }
            static Exception Finalizer(Exception __exception)
            { return CompanyPlans.SwallowPaneOpen("headhunter", __exception); }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgentsPlanList), "SelectPlan",
                      new System.Type[] { typeof(UnityEngine.Transform), typeof(Entities.ImportPartnership) })]
        public static class Patch_PurchasingPlanSelect_MergerGate
        {
            static bool Prefix(Entities.ImportPartnership plan)
            { try { return !CompanyPlans.RefuseSelect("purchasing", plan); } catch { return true; } }
            static Exception Finalizer(Exception __exception)
            { return CompanyPlans.SwallowPaneOpen("purchasing", __exception); }
        }

        // ── PART 2a: the pane's own commits, ROUTED ──
        // The one that used to contaminate a save is HrManagerPlanUI.cs:261
        // `EmployeeHelper.GetEmployeeById(employeeId).assignedHrManagerPlanId = _currentPlan.id;` - a REAL
        // employee of this player (the candidate list is global: EmployeesScrollerController.cs:21-23). It is
        // now routed as the `assign` op and the RUNNER performs that exact pair on ITS own roster; CROSS-HR-3 A2
        // lets that be a CO-MEMBER's copy as well - the id joins the plan's list there and the TAG travels on to
        // the machine holding the REAL record (an injected copy of a NON-member is still refused there; moving an
        // employee between saves is still the host-held TRANSFER). Fill (:224) and ClearAssignedEmployees (:242)
        // both commit through SetEmployeeAssigned, so one route covers all three - and on MY OWN plan, where
        // native runs instead of the route, the CROSS-HR-3b B1 postfix below sends that same tag leg.

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI), "SetEmployeeAssigned")]
        public static class Patch_HrPaneAssign_MergerGate
        {
            // ____currentPlan = '___' + '_currentPlan', decompile HrManagerPlanUI.cs:72
            // `private HrManagerPlan _currentPlan;`.
            static bool Prefix(Buildings.Office.Headquarters.HrManagerPlan ____currentPlan,
                               string employeeId, bool assigned)
            {
                try
                {
                    string eid = employeeId ?? "";
                    // HO-1a H4: the native pair (decompile HrManagerPlanUI.cs:258-267) on the DISPLAY copy, so
                    // the row answers the click at once instead of at the next bundle.  A refusal redraws it back.
                    return !CompanyPlans.RoutePaneEdit("hr", "SetEmployeeAssigned", ____currentPlan, "assign",
                               eid, 0, 0f, assigned, row =>
                    {
                        // HO-1c L4.8: the display copy's LIST only.  Tagging the employee RECORD is the very
                        // save-contamination the note above names - it is this machine's own record, and a refused
                        // assign never rolled it back.  The assign list stays truthful without the tag because the
                        // H3 Load postfix drops an unassigned model that is already on the row plan's list.
                        var pl = row as Buildings.Office.Headquarters.HrManagerPlan;
                        if (pl == null || eid.Length == 0) return;
                        if (assigned) { if (!pl.assignedEmployees.Contains(eid)) pl.assignedEmployees.Add(eid); }
                        else pl.assignedEmployees.Remove(eid);
                    });
                }
                catch { return true; }
            }

            /// <summary>CROSS-HR-3b B1: MY OWN plan runs NATIVE here - the prefix routes a partner's row only
            /// (CompanyPlans.RoutePaneEdit returns false for a non-overlay plan) - and CROSS-HR-3 A1 put a
            /// CO-MEMBER's copies on my own plan's assignable list too.  Native writes the list and tags the
            /// COPY; the owner's REAL record hears nothing unless the leg goes from here.  A REFUSED route leaves
            /// __runOriginal FALSE (RoutePaneEdit answers true on a refusal, CompanyPlans.cs Refused(...), so the
            /// prefix skipped native); the overlay test is asked again for the one case where RoutePaneEdit could
            /// not send at all and native ran on a partner's row - nothing must leave from here then either.
            /// Native's Fill and ClearAssignedEmployees LOOP this method, so each co-member travels as its own
            /// leg - safe, because the employee-edit route is not rate-capped (MPServer.SharedRateOk guards
            /// schedule edits, session messages and bench publishes only).</summary>
            static void Postfix(Buildings.Office.Headquarters.HrManagerPlan ____currentPlan,
                                string employeeId, bool assigned, bool __runOriginal)
            {
                try
                {
                    if (!__runOriginal) return;
                    if (CompanyPlans.IsOverlayPlan(____currentPlan)) return;   // a partner's row: routed, or refused
                    if (!MergerSync.IAmMember) return;
                    string eid = employeeId ?? "";
                    if (eid.Length == 0 || !CompanyPlans.CoMemberCopyHere(eid)) return;
                    CompanyPlans.OwnPlanAssignedLocally(____currentPlan, eid, assigned);
                }
                catch { }
            }
        }

        // ── HO-1a H1/H3/H4 SHARED READS ON THE HR PANE ──

        /// <summary>The pane's own assignable-employee table: decompile HrManagerPlanUI.cs:231 reads
        /// `assignableEmployeesScrollerController.data`, and :238/:252 reload it after a bulk button.</summary>
        private static UI.Smartphone.Apps.BizMan.HRManagers.EmployeesScrollerController MergerHrScroller(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI ui)
        {
            try
            {
                if (ui == null) return null;
                var f = ui.GetType().GetField("assignableEmployeesScrollerController",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                          | System.Reflection.BindingFlags.Public);
                return f != null ? f.GetValue(ui) as UI.Smartphone.Apps.BizMan.HRManagers.EmployeesScrollerController : null;
            }
            catch { return null; }
        }

        /// <summary>HQ-PARITY-6 P2 — THE GATE USES THE REGISTERED PANE.  HQ-PARITY-4 P1 recorded each pane
        /// as its LoadPlan drew (CompanyPlans.RegisterPane) for the redraw path; the EDIT gates kept their
        /// own `GetComponentInChildren(true)` search, which walks inactive objects too and so can hand back
        /// a different instance from the one the player is looking at ("the pane on screen is not the one
        /// the hierarchy search finds", field log). The gate then read a null or stale `_currentPlan`,
        /// decided the edit was this machine's own and never routed it. The registered pane is preferred
        /// whenever it is alive AND on screen; the search stays as the fallback for the first open of a
        /// session, before any LoadPlan has run. The mismatch is reported ONCE per family per session, and
        /// the search only runs at all on that first comparison.
        /// FOLD b H4(a): the "they disagree" line is only meaningful when the pane we are using really came
        /// from the REGISTRY. PaneOf falls back to its own hierarchy sweep when nothing is registered yet, and
        /// comparing that sweep's result against this gate's search was comparing two searches - a line that
        /// says nothing. RegisteredPaneOf answers from the registry ONLY (null when empty), so the comparison
        /// is made exactly when there is something to compare. The gate's own lookup still uses PaneOf.
        /// FOLD b H4(b): the FALLBACK now judges what it found.  `GetComponentInChildren(true)` walks
        /// INACTIVE objects, so before any LoadPlan has run it can return a prefab/template instance that is
        /// not on screen; handing that to a gate is the original defect (a stale `_currentPlan` read off an
        /// object nobody is looking at, and the edit never routed). An off-screen find is now no find.</summary>
        private static readonly System.Collections.Generic.HashSet<string> _paneGateCompared =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static T? MergerGatePane<T>(string family, Func<T?> search) where T : UnityEngine.Component
        {
            try
            {
                var reg = CompanyPlans.PaneOf(family) as T;
                if (reg != null && reg.gameObject.activeInHierarchy)
                {
                    // H4(a): only worth saying when THIS pane is the registered one - otherwise both sides
                    // of the comparison are hierarchy sweeps.
                    if (CompanyPlans.RegisteredPaneOf(family) != null
                        && _paneGateCompared.Add(family) && !ReferenceEquals(reg, search()))
                        Plugin.Logger.LogInfo($"[Plans] {family} gate: the registered pane is used (the hierarchy search found another instance).");
                    return reg;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] {family} gate pane: {ex.Message}"); }
            // H4(b): an inactive template must never be judged.
            try { var found = search(); return found != null && found.gameObject.activeInHierarchy ? found : null; }
            catch { return null; }
        }

        private static UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI? MergerHrPaneSearch()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                return bm != null ? bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI>(true) : null;
            }
            catch { return null; }
        }

        private static UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI? MergerHrPane()
            => MergerGatePane("hr", MergerHrPaneSearch);

        /// <summary>decompile HrManagerPlanUI.cs:72 `private HrManagerPlan _currentPlan;`.</summary>
        private static Buildings.Office.Headquarters.HrManagerPlan MergerHrCurrentPlan(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI? ui)
        {
            try
            {
                if (ui == null) return null;
                var f = ui.GetType().GetField("_currentPlan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return f != null ? f.GetValue(ui) as Buildings.Office.Headquarters.HrManagerPlan : null;
            }
            catch { return null; }
        }

        private static Entities.EmployeeInstance MergerEmployee(string employeeId)
        { try { return string.IsNullOrEmpty(employeeId) ? null : EmployeeHelper.GetEmployeeById(employeeId); } catch { return null; } }

        /// <summary>The two UI calls the native bulk bodies end with (decompile HrManagerPlanUI.cs:238-239 and
        /// :252-253).  Skipping the native body means running them here, or the pane keeps showing the old
        /// list and the old counter after the player pressed the button.</summary>
        private static void MergerHrPaneRedraw(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI ui, Buildings.Office.Headquarters.HrManagerPlan pl)
        {
            try
            {
                if (ui == null || pl == null) return;
                var sc = MergerHrScroller(ui);
                if (sc != null) sc.Load(pl.EmployeeInstances, true, pl.assignedEmployeeId);
                var m = ui.GetType().GetMethod("SetUpBasicData",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                          | System.Reflection.BindingFlags.Public);
                // HO-1c L4.7: SetUpBasicData takes ONE optional parameter (decompile HrManagerPlanUI.cs:113
                // `private void SetUpBasicData(List<EmployeeInstance> employees = null)`), and a default value is
                // not filled in by reflection: invoking with no argument threw TargetParameterCountException into
                // the catch below, so the counter and the averages stayed stale after every Fill / Clear.  It is
                // the only reflective invoke the HO-1a hunks added.
                if (m != null) m.Invoke(ui, new object[] { null });
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] hr bulk redraw: {ex.Message}"); }
        }

        /// <summary>CROSS-HR-3b B2: that same pair, but only while the pane actually stands on THAT plan - an
        /// UNDONE assign (a tag the owner refused) changed the list under an open pane.  Another plan on screen,
        /// or no pane at all, redraws nothing.</summary>
        internal static void MergerHrPaneRedrawIfOn(string planId)
        {
            try
            {
                if (string.IsNullOrEmpty(planId)) return;
                var ui = MergerHrPane();
                if (ui == null || !ui.gameObject.activeInHierarchy) return;
                var pl = MergerHrCurrentPlan(ui);
                if (pl == null || pl.id != planId) return;
                MergerHrPaneRedraw(ui, pl);
            }
            catch { }
        }

        /// <summary>Whom Fill would take, in the NATIVE order and on the native test (decompile
        /// HrManagerPlanUI.cs:231-236: off the assignable table, nobody already on a plan, never the plan's own
        /// manager), capped at the free slot count.  H3 has already pruned that table to one company's people,
        /// and CROSS-HR-3 A1 widened it to a co-member's COPIES.  FILL-PREFER (user ruling 2026-09-12): those
        /// copies are no longer skipped here - CROSS-HR-3b B1's blanket skip is retired.  The plan owner's OWN
        /// people come first, in the table's order, and a co-member's copies take the slots that REMAIN, which is
        /// the one rule the routed fill applier now follows too.  An injected copy of somebody who is merely a
        /// business grantee is not a company member and is still no pick of ours.</summary>
        private static List<Entities.EmployeeInstance> MergerHrFillPicks(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI ui,
                                                                        Buildings.Office.Headquarters.HrManagerPlan pl, int free)
        {
            var picks = new List<Entities.EmployeeInstance>();
            try
            {
                var sc = MergerHrScroller(ui);
                if (sc == null || sc.data == null) return picks;
                var rows = new List<UI.Smartphone.Apps.BizMan.HrManagers.HrManagerEmployeeModel>(sc.data);
                for (int pass = 0; pass < 2 && picks.Count < free; pass++)
                foreach (var model in rows)
                {
                    if (picks.Count >= free) break;
                    if (model == null) continue;
                    var e = MergerEmployee(model.employeeId);
                    if (e == null || !string.IsNullOrEmpty(e.assignedHrManagerPlanId)) continue;
                    if (e.id == pl.assignedEmployeeId) continue;
                    if (MPRegisterSync.IsSyntheticDuty(e.id)) continue;   // HO-1c L4.5: a duty stand-in is not staff
                    bool co = CompanyPlans.CoMemberCopyHere(e.id);
                    // FILL-PREFER: pass 0 takes my own people, pass 1 a co-member's copies for the slots left over.
                    if (pass == 0 ? MPRegisterSync.IsInjectedStaff(e.id) : !co) continue;
                    picks.Add(e);
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] hr fill picks: {ex.Message}"); }
            return picks;
        }

        // ── HO-1a H1: THE BULK BUTTONS TRAVEL AS ONE LEG ──
        // Fill (decompile HrManagerPlanUI.cs:224-240) and ClearAssignedEmployees (:242-254) each LOOP
        // SetEmployeeAssigned(..., refreshData: false), so the prefix above turned ONE button into one leg per
        // employee - and the host's own per-sender cap (MPServer.SharedRateOk, ten work edits a second) dropped
        // the rest of the burst in silence.  Each button is ONE leg now, with its own PlanOp.  The display copy
        // is touched by writing the fields DIRECTLY: calling SetEmployeeAssigned here would re-enter the patched
        // method and route a second leg per person, which is the bug itself.

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI), "ClearAssignedEmployees")]
        public static class Patch_HrPaneClearAll_MergerGate
        {
            static bool Prefix(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI __instance,
                               Buildings.Office.Headquarters.HrManagerPlan ____currentPlan)
            {
                try
                {
                    if (!CompanyPlans.IsOverlayPlan(____currentPlan)) return true;   // my own plan: native runs
                    // HO-1c L4.10: native does nothing on an empty plan (decompile HrManagerPlanUI.cs:244), so
                    // neither does this - an empty Clear spends no leg of the host's per-sender budget.
                    if (____currentPlan.NumberOfAssignedEmployees == 0) return false;
                    bool routed = CompanyPlans.RoutePaneEdit("hr", "ClearAssignedEmployees", ____currentPlan,
                                     "clear", "", 0, 0f, false, row =>
                    {
                        var pl = row as Buildings.Office.Headquarters.HrManagerPlan;
                        if (pl == null) return;
                        pl.assignedEmployees.Clear();   // HO-1c L4.8: the display list only, never an employee record
                    });
                    if (routed) MergerHrPaneRedraw(__instance, ____currentPlan);
                    return !routed;
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI), "Fill")]
        public static class Patch_HrPaneFill_MergerGate
        {
            static bool Prefix(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI __instance,
                               Buildings.Office.Headquarters.HrManagerPlan ____currentPlan)
            {
                try
                {
                    if (!CompanyPlans.IsOverlayPlan(____currentPlan))
                    {
                        // CROSS-HR-3b B1: MY OWN plan.  A1 put a co-member's copies on the assignable list, so
                        // native's Fill (decompile HrManagerPlanUI.cs:224-240) would sweep them up in table order.
                        // FILL-PREFER (user ruling 2026-09-12) retires "own people only (H3)" for a fill: the picks
                        // are my OWN people first and a co-member's copies only in the slots that REMAIN, which is
                        // the rule the routed fill applier follows too, so one rule holds on both kinds of plan.
                        // SetEmployeeAssigned is native's own public method (decompile :256), so the list/tag
                        // pair is exactly native's - and the B1 postfix sends the tag leg for each co-member copy
                        // it assigns, nothing for my own people.
                        if (!MergerSync.IAmMember) return true;      // off a merger nothing is injected: native runs
                        int ownFree = ____currentPlan.MaxEmployees - ____currentPlan.NumberOfAssignedEmployees;
                        if (ownFree <= 0) return false;              // native :226-229 does nothing either
                        foreach (var pick in MergerHrFillPicks(__instance, ____currentPlan, ownFree))
                            __instance.SetEmployeeAssigned(pick.id, true, false);
                        MergerHrPaneRedraw(__instance, ____currentPlan);   // native's own tail, :238-239
                        return false;
                    }
                    int free = ____currentPlan.MaxEmployees - ____currentPlan.NumberOfAssignedEmployees;
                    if (free <= 0) return false;                      // native :226-229 does nothing either
                    var picks = MergerHrFillPicks(__instance, ____currentPlan, free);
                    bool routed = CompanyPlans.RoutePaneEdit("hr", "Fill", ____currentPlan, "fill",
                                     "", free, 0f, false, row =>
                    {
                        var pl = row as Buildings.Office.Headquarters.HrManagerPlan;
                        if (pl == null) return;
                        foreach (var e in picks)                                   // HO-1c L4.8: the display list only
                            if (!pl.assignedEmployees.Contains(e.id)) pl.assignedEmployees.Add(e.id);
                    });
                    if (routed) MergerHrPaneRedraw(__instance, ____currentPlan);
                    return !routed;
                }
                catch { return true; }
            }
        }

        // ── HO-1a H3: THE ASSIGN LIST OFFERS THAT COMPANY'S ELIGIBLE PEOPLE ONLY ──
        // EmployeesScrollerController.Load (decompile :15-27) builds `data` from the plan's own instances and
        // then, with includeUnassignedEmployees, from EVERY local record without a plan (:21-23).  On a merged
        // machine that second sweep sweeps up EVERY local record without a plan - the injected copies of a
        // partner's staff, the duty stand-ins and the hiring candidates alike.  CROSS-HR-3 A1: the eligible set
        // is now the COMPANY's working people (mine, plus a co-member's copies) on my own plan and on a
        // partner's row alike, because A2 makes an assign cross a machine.
        // The postfix prunes `data` to the one eligible set and reloads through the class's own scroller
        // (:26 `scroller.ReloadData()`).  The cell toggle stays live; there is simply nobody wrong to toggle.

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.EmployeesScrollerController), "Load")]
        public static class Patch_HrAssignList_MergerFilter
        {
            private static readonly HashSet<string> _hrListFailClosed = new();   // HO-1c L4.8: one warning per owner

            /// <summary>CROSS-HR-3 A1: a HIRING CANDIDATE is a record this save holds while employing nobody
            /// (EmployeeInstance.IsCandidate).  Native's unassigned sweep does not know the difference; an HR plan
            /// must never tag one, and a co-member never sees somebody else's hiring.</summary>
            private static bool HrListIsCandidate(string employeeId)
            {
                try
                {
                    if (string.IsNullOrEmpty(employeeId)) return false;
                    var e = Helpers.EmployeeHelper.GetEmployeeById(employeeId);
                    return e != null && e.IsCandidate;
                }
                catch { return false; }
            }

            static void Postfix(UI.Smartphone.Apps.BizMan.HRManagers.EmployeesScrollerController __instance)
            {
                try
                {
                    if (!MergerSync.IAmMember || __instance == null || __instance.data == null) return;
                    var rowPlan = MergerHrCurrentPlan(MergerHrPane());
                    string owner = CompanyPlans.OwnerOfOverlayPlan(rowPlan);
                    // HO-1c L4.8: a partner's HQ on screen with no resolvable pane plan FAILS CLOSED.  Falling to
                    // the own-plan rule there would offer this player's own people on a partner's row, which is
                    // the contamination H3 exists to stop.
                    // HQ-UNION-1b (review MAJOR-3): a partner's row can be on MY OWN card too, so the test is the union
                    // predicate plus "partner rows are drawn" - never the own-plan rule while a partner row could be showing.
                    // U7: "drawn" is the HR family's OWN last draw (PartnerRowsDrawn("hr")), not the family-agnostic row
                    // cache - a pricing-only partner, or one whose headquarters stopped resolving, no longer arms this.
                    if (rowPlan == null && CompanyPlans.CompanyHqPageOpen(out _, out var openOwner) && openOwner.Length > 0 && CompanyPlans.PartnerRowsDrawn("hr") > 0)
                    {
                        int all = __instance.data.Count;
                        __instance.data = new List<UI.Smartphone.Apps.BizMan.HrManagers.HrManagerEmployeeModel>();
                        if (__instance.scroller != null) __instance.scroller.ReloadData();
                        if (_hrListFailClosed.Add(openOwner))
                            Plugin.Logger.LogWarning($"[Plans] assign list: '{openOwner}' headquarters is open but the pane's plan is unknown here - all {all} row(s) dropped (fail closed).");
                        return;
                    }
                    var onPlan = rowPlan != null && rowPlan.assignedEmployees != null ? rowPlan.assignedEmployees : null;
                    var keep = new List<UI.Smartphone.Apps.BizMan.HrManagers.HrManagerEmployeeModel>();
                    int dropped = 0, ownKept = 0, coKept = 0;
                    foreach (var model in __instance.data)
                    {
                        if (model == null) continue;
                        // CROSS-HR-3 A1: THE WHOLE COMPANY IS ON THE LIST.  H3 used to offer that partner's own
                        // people on a partner's row and only mine on my own plan, because an assign could not cross
                        // a machine: the tag would have been written here, on a copy.  A2 makes it cross - the tag is
                        // written on the machine that holds the REAL record, and the plan id resolves there through
                        // the SHADOW CROSS-HR-1 installs for every partner feed (PaperworkSync.cs:956 installs the
                        // display lists, :973 registers the shadow HR rows), so MY plan is resolvable on a partner's
                        // machine exactly as theirs is on mine.  One eligible set on both kinds of plan now: my own
                        // real records, plus the injected copies of a CO-MEMBER's people.  Never a duty stand-in
                        // (HO-1c L4.5), never a candidate (hiring is not shared), and never an injected copy whose
                        // owner is not a company member - a plain Business grant is not a company.
                        bool injectedRow = MPRegisterSync.IsInjectedStaff(model.employeeId);
                        bool eligible = !MPRegisterSync.IsSyntheticDuty(model.employeeId)
                              && !HrListIsCandidate(model.employeeId)
                              && (!injectedRow || MPRegisterSync.IsInjectedFromMergedPartner(model.employeeId));
                        // HO-1c L4.8: the mutates no longer tag the employee RECORD, so somebody added to the
                        // display list still arrives here through native's unassigned sweep (decompile
                        // EmployeesScrollerController.cs:21-23).  The list half already shows them as assigned.
                        bool onRow = onPlan != null && onPlan.Contains(model.employeeId);
                        if (eligible && onRow && !model.assigned) { dropped++; continue; }
                        if (eligible && onRow && owner.Length > 0) model.assigned = true;      // a partner's row: the list is the truth
                        if (eligible) { keep.Add(model); if (injectedRow) coKept++; else ownKept++; } else dropped++;
                    }
                    if (dropped == 0) return;
                    __instance.data = keep;
                    if (__instance.scroller != null) __instance.scroller.ReloadData();
                    Plugin.Logger.LogInfo($"[Plans] assign list on {(owner.Length == 0 ? "my own HR plan" : $"'{owner}' HR plan")}: "
                                        + $"{dropped} dropped, {keep.Count} offered ({ownKept} of my own, {coKept} co-member copy/copies) - the company's people may join either plan.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] assign list filter: {ex.Message}"); }
            }
        }

        // -- CROSS-HR-3 A3: DELETING A PLAN CLEARS ITS TAG ON EVERY MACHINE --
        // HrManagerPlan.Delete (decompile :223-228) nulls assignedHrManagerPlanId on the plan's own
        // EmployeeInstances - LOCAL records, every one of them. Once a co-member's worker can be on the list
        // (A2) the ones that matter are somewhere else, and native cannot reach them. The prefix fans a tag
        // CLEAR out to each of those owners first, then lets native run exactly as it always has.
        // It hangs on the REAL plan, which is the one this machine may delete: a partner's SHADOW
        // (MergerAbsence.IsDisplayInstall) and a detached display row (CompanyPlans.IsOverlayPlan) are somebody
        // else's to delete, and the routed applier's own fan-out marks its plan id so the two never double up.
        // A stand-in's installs are tagged with a REAL pid, so IsDisplayInstall is false for them: standing in
        // for an absent runner, this machine fans out for that runner's plans, which is right (A4).

        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HrManagerPlan), "Delete")]
        public static class Patch_HrPlanDelete_MergerTagFanOut
        {
            static void Prefix(Buildings.Office.Headquarters.HrManagerPlan __instance)
            {
                try
                {
                    if (__instance == null || !MergerSync.IAmMember) return;
                    if (MergerAbsence.IsDisplayInstall(__instance)) return;          // a partner's shadow: not mine to fan out
                    if (CompanyPlans.IsOverlayPlan(__instance)) return;              // a detached display row: ditto
                    if (CompanyPlans.HrDeleteFanOutDone(__instance.id)) return;      // the routed applier already sent them
                    CompanyPlans.HrTagFanOutClear(__instance, "the plan was deleted here");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[CrossHR] hr delete tag fan-out: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI), "DeletePlan")]
        public static class Patch_HrPaneDelete_MergerGate
        {
            static bool Prefix(Buildings.Office.Headquarters.HrManagerPlan ____currentPlan)
            { try { return !CompanyPlans.RoutePaneEdit("hr", "DeletePlan", ____currentPlan, "delete"); } catch { return true; } }
        }

        // The two INSURANCE commits are gated on the PLAN, not on the pane: HrManagerPlanUI.CancelInsurance
        // (:179-188) and UpgradeInsurance (:190-204) each raise the game's own HudConfirm and commit inside
        // its callback, so gating the pane method would swallow the confirmation as well. Patching
        // HrManagerPlan.CancelHealthInsurancePlan (:119) / UpgradeHealthInsurancePlan (:173) leaves the
        // game's own confirm exactly where it is and routes the moment the player says yes.

        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HrManagerPlan), "CancelHealthInsurancePlan")]
        public static class Patch_HrInsuranceCancel_MergerGate
        {
            static bool Prefix(Buildings.Office.Headquarters.HrManagerPlan __instance)
            { try { return !CompanyPlans.RoutePaneEdit("hr", "CancelInsurance", __instance, "insurance-cancel"); } catch { return true; } }
        }

        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HrManagerPlan), "UpgradeHealthInsurancePlan")]
        public static class Patch_HrInsuranceUpgrade_MergerGate
        {
            static bool Prefix(Buildings.Office.Headquarters.HrManagerPlan __instance)
            { try { return !CompanyPlans.RoutePaneEdit("hr", "UpgradeInsurance", __instance, "insurance-upgrade"); } catch { return true; } }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerPlanUI), "ChangeNeighborhood")]
        public static class Patch_PricingPaneNeighbourhood_MergerGate
        {
            // ____currentPlan = '___' + '_currentPlan', decompile PricingManagerPlanUI.cs:24
            // `private PricingManagerPlan _currentPlan;`; the body is :117-123.
            static bool Prefix(Buildings.Office.Headquarters.PricingManagerPlan ____currentPlan, string newNeighborhood)
            {
                try
                {
                    string hood = newNeighborhood ?? "";
                    return !CompanyPlans.RoutePaneEdit("pricing", "ChangeNeighborhood", ____currentPlan,
                               "neighborhood", hood, 0, 0f, false, row =>
                    // H4: the native write, PricingManagerPlanUI.cs:119 -> PricingManagerPlan.cs:72.
                    { var pl = row as Buildings.Office.Headquarters.PricingManagerPlan; if (pl != null) pl.SetSupervisedNeighborhood(hood); });
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerPlanUI), "DeletePlan")]
        public static class Patch_PricingPaneDelete_MergerGate
        {
            static bool Prefix(Buildings.Office.Headquarters.PricingManagerPlan ____currentPlan)
            { try { return !CompanyPlans.RoutePaneEdit("pricing", "DeletePlan", ____currentPlan, "delete"); } catch { return true; } }
        }

        // The PRICE writes are gated on the model, which is where both screens meet: the cell view's manual
        // price (PricingManagerProductCellView.cs:131) and the mass 'apply suggested'
        // (PricingManagerProductsScrollerController.cs:91-95). Both end in ReapplyPriceWhereSold
        // (PricingManagerPlan.cs:145-156 `SetPrice(supervisedStore, itemName, price)`), which writes LIVE
        // retail prices. Routed, the RUNNER makes that write on the stores it actually simulates.

        [HarmonyPatch(typeof(Buildings.Office.Headquarters.PricingManagerPlan), "ApplyManualPrice")]
        public static class Patch_PricingApplyManual_MergerGate
        {
            // decompile PricingManagerPlan.cs:118 `public void ApplyManualPrice(string itemName, float price)`.
            static bool Prefix(Buildings.Office.Headquarters.PricingManagerPlan __instance, string itemName, float price)
            {
                try
                {
                    string item = itemName ?? "";
                    return !CompanyPlans.RoutePaneEdit("pricing", "apply manual price", __instance,
                               "manualprice", item, 0, price, false, row =>
                    // H4: only the MANUALLY-PRICED tag (PricingManagerPlan.cs:29) can be mirrored on a copy.
                    // The rest of the native body is ReapplyPriceWhereSold -> SetPrice on the supervised
                    // STORE (:145-156), a live retail price on a shop this machine may itself run - a display
                    // copy must never reach it, so the price figure waits for the runner's own bundle.
                    { var pl = row as Buildings.Office.Headquarters.PricingManagerPlan;
                      if (pl != null && item.Length > 0) pl.manuallyPricedItems.Add(item); });
                }
                catch { return true; }
            }
        }

        // HO-1a H1: the mass button.  PricingManagerProductsScrollerController.ApplySuggestedPrices
        // (decompile :91-99) loops ApplySuggestedPrice over every row on screen, so on a partner's plan it
        // used to be one leg per row and the host's per-sender cap dropped most of them.  ONE leg now; the
        // runner replays it from its own cached suggestions.  NO display mutate: the only write this button
        // makes is the live retail price on the supervised store (PricingManagerPlan.cs:145-156), which a
        // display copy must not reach - the figures arrive with the runner's urgent bundle (H4).

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerProductsScrollerController), "ApplySuggestedPrices")]
        public static class Patch_PricingApplyAll_MergerGate
        {
            static bool Prefix(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerProductsScrollerController __instance)
            {
                try
                {
                    var pl = MergerPricingPlan();
                    if (!CompanyPlans.IsOverlayPlan(pl)) return true;
                    // HO-1c L4.6: native prices its ApplyTargets only - the VISIBLE rows while
                    // PricingManagerHelper.Settings.applyOnlyToVisibleProducts is set, else every model (decompile
                    // :40-49) - so the runner replaying its whole cache priced more than the button.  The target
                    // item names travel joined by '|' in StrValue, exactly as the purchasing bulk sends its list,
                    // and the private property is read by reflection as the H6 prefix reads its own.
                    var names = new List<string>();
                    try
                    {
                        var prop = typeof(UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerProductsScrollerController)
                                   .GetProperty("ApplyTargets", System.Reflection.BindingFlags.Instance
                                              | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        if (prop != null && prop.GetValue(__instance) is System.Collections.IEnumerable rows)
                            foreach (var r in rows)
                            {
                                string nm = "";
                                try { nm = (r as UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerProductModel)?.Suggestion?.itemName ?? ""; } catch { }
                                if (nm.Length > 0 && !names.Contains(nm)) names.Add(nm);
                            }
                    }
                    catch (Exception rx) { Plugin.Logger.LogWarning($"[Merger] pricing suggestall targets: {rx.Message}"); }
                    if (names.Count == 0) return true;   // nothing on screen to price: native loops nothing either
                    return !CompanyPlans.RoutePaneEdit("pricing", "ApplySuggestedPrices", pl, "suggestall",
                               string.Join("|", names), names.Count);
                }
                catch { return true; }
            }
        }

        private static UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerPlanUI? MergerPricingPaneSearch()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                return bm != null ? bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PricingManagers.PricingManagerPlanUI>(true) : null;
            }
            catch { return null; }
        }

        /// <summary>decompile PricingManagerPlanUI.cs:24 `private PricingManagerPlan _currentPlan;` - the
        /// scroller is told its rows, never its plan, so the pane is where the plan on screen lives.
        /// HQ-PARITY-6 P2: the pane itself comes from MergerGatePane - the registered one (HQ-PARITY-4 P1
        /// records it as LoadPlan draws) while it is on screen, the hierarchy search only as the fallback,
        /// because that search can find an inactive second instance whose `_currentPlan` is stale or null
        /// and the edit then takes the own-edit path instead of being routed to the owner.</summary>
        private static Buildings.Office.Headquarters.PricingManagerPlan MergerPricingPlan()
        {
            try
            {
                var pane = MergerGatePane("pricing", MergerPricingPaneSearch);
                if (pane == null) return null;
                var f = pane.GetType().GetField("_currentPlan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return f != null ? f.GetValue(pane) as Buildings.Office.Headquarters.PricingManagerPlan : null;
            }
            catch { return null; }
        }

        [HarmonyPatch(typeof(Buildings.Office.Headquarters.PricingManagerPlan), "ApplySuggestedPrice")]
        public static class Patch_PricingApplySuggested_MergerGate
        {
            // decompile PricingManagerPlan.cs:128 `public void ApplySuggestedPrice(string itemName, float price)`.
            static bool Prefix(Buildings.Office.Headquarters.PricingManagerPlan __instance, string itemName, float price)
            {
                try
                {
                    return !CompanyPlans.RoutePaneEdit("pricing", "apply suggested price", __instance,
                               "suggestedprice", itemName ?? "", 0, price);
                }
                catch { return true; }
            }
        }

        // PURCHASING. Five commits take no argument and one pair are the toggles. SetLockTime is NOT here:
        // decompile PurchasingAgentPlanUI.cs:165-181 writes `lockTimeLabel` and nothing else - it is a label
        // refresh, so there is no commit to route.

        [HarmonyPatch]
        public static class Patch_PurchasingPane_MergerGate
        {
            // ____currentImportPartnership = '___' + '_currentImportPartnership', decompile
            // PurchasingAgentPlanUI.cs:55 `private ImportPartnership _currentImportPartnership;`.
            // r2 D2, THE CONFIRMATION FOLD. EndPartnership (:199) and MakeUrgentOrder (:214) raise a HudConfirm
            // whose callback is the commit. Routing them at the METHOD skipped the game's own confirmation -
            // the one behaviour difference this block used to have. They now ARM instead: the native body runs
            // (its OWN guards included - :201 CanModifyContract, :215 the tomorrow check), reaches
            // HudConfirm.Show, and the shared wrapper in SharedShopStaff REPLACES the confirm delegate with the
            // routed send, so the native callback never writes anything here. The arming is dropped in the
            // finaliser, so it lives exactly as long as this call - confirmed or cancelled either way.
            static readonly System.Collections.Generic.HashSet<string> Confirmed =
                new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { "EndPartnership", "MakeUrgentOrder" };
            static readonly Dictionary<string, string> Ops = new Dictionary<string, string>
            {
                { "DeletePlan", "delete" }, { "EndPartnership", "end" }, { "MakeUrgentOrder", "urgent" },
                { "StartOrder", "start" }, { "CancelOrder", "cancel" },
            };
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                var t = typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI);
                foreach (var n in Ops.Keys)
                {
                    var m = AccessTools.Method(t, n);
                    if (m != null) yield return m;
                }
            }
            static bool Prefix(Entities.ImportPartnership ____currentImportPartnership,
                               System.Reflection.MethodBase __originalMethod)
            {
                try
                {
                    string name = __originalMethod?.Name ?? "";
                    if (!Ops.TryGetValue(name, out var op)) return true;
                    if (Confirmed.Contains(name))
                    { CompanyPlans.ArmConfirmRoute("purchasing", ____currentImportPartnership, op, name); return true; }
                    return !CompanyPlans.RoutePaneEdit("purchasing", name, ____currentImportPartnership, op,
                               "", 0, 0f, false, row =>
                    {
                        // H4: the flags the native bodies set (PurchasingAgentPlanUI.cs:249-251, :267-269) on the
                        // DISPLAY copy.  `end` and `urgent` go through the confirmation fold above, which hands
                        // the route to HudConfirm and has no display leg of its own; `delete` is the row leaving
                        // a list this detached copy is not in, so neither can be mirrored here.
                        var ip = row as Entities.ImportPartnership;
                        if (ip == null) return;
                        if (op == "start")  ip.isActive = true;
                        if (op == "cancel") { ip.isActive = false; ip.isUrgentOrder = false; }
                    });
                }
                catch { return true; }
            }

            static void Finalizer() { try { CompanyPlans.DisarmConfirmRoute(); } catch { } }
        }

        // ── HO-1a H6: THE PURCHASING PRODUCT ROW ──
        // ChangeAssignedWarehouse (decompile PurchasingAgentProductCellView.cs:248-251), ChangeTarget (:234-240)
        // and the bulk MassDesignateWarehouse (PurchasingAgentProductsMassActionsUI.cs:103-118) had NO prefix at
        // all: on a partner's plan each wrote the DETACHED display copy and was lost at the next feed.  All
        // three route now.  The bulk is ONE leg - the selected item names joined by '|' in StrValue, the
        // warehouse in StationId, both fields the payload already carries, so no protocol change.
        // HO-1c L4.12: StationId is NOT free on every other mergerplanedit path - it also carries a refusal's
        // addressee (CompanyPlans.cs:1252, read at MPServer.cs:8515) and the purchasing `create` importer
        // (CompanyPlans.cs:1317).  The uses are disjoint: the host's forward branch gates on the "refused" op,
        // and `create` is its own op, so the warehouse key can never be read as either.

        private static UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI? MergerPurchasingPaneSearch()
        {
            try
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                var bm = ui != null && ui.fullMenu != null ? ui.fullMenu.bizMan : null;
                return bm != null ? bm.GetComponentInChildren<UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI>(true) : null;
            }
            catch { return null; }
        }

        /// <summary>decompile PurchasingAgentPlanUI.cs:55 `private ImportPartnership _currentImportPartnership;`
        /// - the product row and the mass-action bar both hang off that one pane.
        /// HQ-PARITY-6 P2: the pane comes from MergerGatePane now - the registered one (HQ-PARITY-4 P1) while
        /// it is on screen, the hierarchy search only as the fallback. This is the family the field log
        /// caught: the search found another instance, so the ChangeTarget edit on a partner's purchasing plan
        /// read no partnership, took the own-edit path and was never routed.</summary>
        private static Entities.ImportPartnership MergerPurchasingPlan()
        {
            try
            {
                var pane = MergerGatePane("purchasing", MergerPurchasingPaneSearch);
                if (pane == null) return null;
                var f = pane.GetType().GetField("_currentImportPartnership", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return f != null ? f.GetValue(pane) as Entities.ImportPartnership : null;
            }
            catch { return null; }
        }

        /// <summary>decompile PurchasingAgentProductCellView.cs:56 `private PurchasingAgentProductModel _data;`.</summary>
        private static UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductModel MergerCellModel(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductCellView cell)
        {
            try
            {
                if (cell == null) return null;
                var f = cell.GetType().GetField("_data", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return f != null ? f.GetValue(cell) as UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductModel : null;
            }
            catch { return null; }
        }

        private static void MergerSetWarehouse(object row, string itemList, BuildingRegistration wh)
        {
            var ip = row as Entities.ImportPartnership;
            if (ip == null || ip.products == null) return;
            foreach (var name in (itemList ?? "").Split('|'))
            {
                if (name.Length == 0) continue;
                foreach (var pr in ip.products) if (pr != null && pr.itemName == name) pr.assignedWarehouse = wh != null ? wh.Address : null;
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductCellView), "ChangeAssignedWarehouse")]
        public static class Patch_PurchasingWarehouse_MergerGate
        {
            static bool Prefix(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductCellView __instance, int index)
            {
                try
                {
                    var ip = MergerPurchasingPlan();
                    if (!CompanyPlans.IsOverlayPlan(ip)) { CompanyPlans.OwnEditCommitted("purchasing pane control"); return true; }   // HQ-PARITY-3 B4
                    var data = MergerCellModel(__instance);
                    if (data == null || data.productRef == null) return true;
                    string item = data.productRef.itemName ?? "";
                    var reg = index > 0 && data.warehouses != null && index - 1 < data.warehouses.Count
                            ? data.warehouses[index - 1] : null;                       // native :250 `index <= 0` = none
                    string key = reg != null ? GameStateReader.AddressKey(reg) : "";
                    return !CompanyPlans.RoutePaneEdit("purchasing", "ChangeAssignedWarehouse", ip, "warehouse",
                               item, 0, 0f, false, row => MergerSetWarehouse(row, item, reg), key);
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductCellView), "ChangeTarget")]
        public static class Patch_PurchasingTarget_MergerGate
        {
            static bool Prefix(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductCellView __instance, int newTarget)
            {
                try
                {
                    var ip = MergerPurchasingPlan();
                    if (!CompanyPlans.IsOverlayPlan(ip)) { CompanyPlans.OwnEditCommitted("purchasing pane control"); return true; }   // HQ-PARITY-3 B4
                    var data = MergerCellModel(__instance);
                    if (data == null || data.productRef == null) return true;
                    string item = data.productRef.itemName ?? "";
                    return !CompanyPlans.RoutePaneEdit("purchasing", "ChangeTarget", ip, "target",
                               item, newTarget, 0f, false, row =>   // H4: native :236 -> ImportProduct.amount
                    {
                        var p2 = row as Entities.ImportPartnership;
                        if (p2 == null || p2.products == null) return;
                        foreach (var pr in p2.products) if (pr != null && pr.itemName == item) pr.amount = newTarget;
                    });
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductsMassActionsUI), "MassDesignateWarehouse")]
        public static class Patch_PurchasingMassWarehouse_MergerGate
        {
            private static readonly System.Reflection.PropertyInfo _pMassContractActive =
                typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductsMassActionsUI)
                .GetProperty("IsContractActive", System.Reflection.BindingFlags.Static
                           | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

            static bool Prefix(BuildingRegistration warehouse)
            {
                try
                {
                    var ip = MergerPurchasingPlan();
                    if (!CompanyPlans.IsOverlayPlan(ip)) { CompanyPlans.OwnEditCommitted("purchasing pane control"); return true; }   // HQ-PARITY-3 B4
                    // HO-1c L4.11: native returns while the contract is ACTIVE (decompile
                    // PurchasingAgentProductsMassActionsUI.cs:105-108) - a private STATIC property, so reflection
                    // reads it without an instance.  Skipping the body and sending nothing is what native does.
                    try { if (_pMassContractActive != null && _pMassContractActive.GetValue(null) is bool act && act) return false; } catch { }
                    var names = new List<string>();
                    foreach (var pr in UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentProductsMassActionsUI.massActionSelectedProducts)
                        if (pr != null && !string.IsNullOrEmpty(pr.itemName) && !names.Contains(pr.itemName)) names.Add(pr.itemName);
                    if (names.Count == 0) return true;                                 // native :109 loops nothing
                    string list = string.Join("|", names);
                    string key = warehouse != null ? GameStateReader.AddressKey(warehouse) : "";
                    return !CompanyPlans.RoutePaneEdit("purchasing", "MassDesignateWarehouse", ip, "warehouseall",
                               list, names.Count, 0f, false, row => MergerSetWarehouse(row, list, warehouse), key);
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI), "OnRepeatingOrderToggleValueChanged")]
        public static class Patch_PurchasingRepeating_MergerGate
        {
            static bool Prefix(Entities.ImportPartnership ____currentImportPartnership, bool value)
            {
                try
                {
                    return !CompanyPlans.RoutePaneEdit("purchasing", "repeating order", ____currentImportPartnership,
                               "repeating", "", 0, 0f, value, row =>   // H4
                    { var ip = row as Entities.ImportPartnership; if (ip != null) ip.isRepeatingOrder = value; });
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.PurchasingAgent.PurchasingAgentPlanUI), "OnAutoStockToggleValueChanged")]
        public static class Patch_PurchasingAutoStock_MergerGate
        {
            static bool Prefix(Entities.ImportPartnership ____currentImportPartnership, bool value)
            {
                try
                {
                    return !CompanyPlans.RoutePaneEdit("purchasing", "auto stock", ____currentImportPartnership,
                               "autostock", "", 0, 0f, value, row =>   // H4
                    { var ip = row as Entities.ImportPartnership; if (ip != null) ip.isTarget = value; });
                }
                catch { return true; }
            }
        }

        // HEADHUNTER. r2 MAJOR-5 (D3) corrects what this block used to say: the recruiting-settings writes are
        // LIVE NATIVE WRITES on planUI.currentPlan (HeadhuntersRecruitingTab.cs:168 skillValueTarget, :312
        // skillRecruiting, :332/:336 dealBreakerTypes, :239-240 the candidate counts), and ApplyHeadhunter never
        // read the DTO - so on a partner's row they reached the DETACHED object and nothing else: `recruit` ran
        // on the RUNNER'S OWN settings, and a change made while that plan was ALREADY recruiting did nothing at
        // all. Each write is now POSTFIXED: the native write lands on the temp object (the display the player
        // just made), then one `settings` leg carries the whole DTO to the runner, which copies the recruiting
        // fields onto its real plan. `recruit` applies those same fields before StartRecruiting, and the tab's
        // StopRecruiting - a bare `isRecruiting = false` (:156) - routes `stoprecruit`.

        [HarmonyPatch(typeof(HeadhunterPlanUI), "DeletePlan")]
        public static class Patch_HeadhunterPaneDelete_MergerGate
        {
            // ___currentPlan = '___' + 'currentPlan', decompile HeadhunterPlanUI.cs:26-27
            // `[HideInInspector] public HeadhunterPlan currentPlan;`.
            static bool Prefix(Buildings.Office.Headquarters.HeadhunterPlan ___currentPlan)
            { try { return !CompanyPlans.RoutePaneEdit("headhunter", "DeletePlan", ___currentPlan, "delete"); } catch { return true; } }
        }

        [HarmonyPatch(typeof(HeadhuntersAutomaticReplacementTab), "SelectHrManagerPlan")]
        public static class Patch_HeadhunterHrPlanAssign_MergerGate
        {
            // ___planUI = '___' + 'planUI', decompile HeadhuntersAutomaticReplacementTab.cs:17-18
            // `[SerializeField] private HeadhunterPlanUI planUI;`; the body is :139-155 and picks the id out
            // of `_hrManagerPlansIds[planIndex]`, which is what the route carries (slot -> id).
            static bool Prefix(HeadhuntersAutomaticReplacementTab __instance, HeadhunterPlanUI ___planUI,
                               int slot, int planIndex)
            {
                try
                {
                    var cur = ___planUI != null ? ___planUI.currentPlan : null;
                    string picked = PlanEditStringAt(__instance, "_hrManagerPlansIds", planIndex);
                    // CROSS-HR-1b K2(b): a shadow's id must never be written into MY OWN plan's slot (:149).
                    // MY OWN only: on an OVERLAY row the ids are that OWNER's own plans and the bind ROUTES
                    // to the machine that runs them - that path is untouched, below.
                    if (!CompanyPlans.IsOverlayPlan(cur) && CompanyPlans.IsShadowHrPlanId(picked))
                    { LogShadowSkip("the headhunter replacement-slot bind", picked); return false; }
                    return !CompanyPlans.RoutePaneEdit("headhunter", "HR plan assignment",
                               cur, "hrplan", picked, slot);
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(Buildings.Office.Headquarters.HeadhunterPlan), "StartRecruiting")]
        public static class Patch_HeadhunterStartRecruiting_MergerGate
        {
            static bool Prefix(Buildings.Office.Headquarters.HeadhunterPlan __instance)
            { try { return !CompanyPlans.RoutePaneEdit("headhunter", "start recruiting", __instance, "recruit"); } catch { return true; } }
        }

        /// <summary>r2 MAJOR-5: every recruiting-settings write on an OVERLAY plan, as ONE `settings` leg
        /// carrying the whole DTO. A POSTFIX, so the native write has already updated the temp object and the
        /// DTO the leg takes is the screen the player just made. Own plans are untouched (RoutePaneEdit
        /// answers false), and the leg is idempotent - the runner copies the same fields either way.</summary>
        [HarmonyPatch]
        public static class Patch_HeadhunterRecruitSettings_MergerRoute
        {
            static readonly string[] Writes =
            {
                "SetSkillTargetValue", "ClearAllDealBreakers", "OnRecruitContinuouslyToggle",
                "OnRecruitAmountOfCandidatesToggle", "OnAmountOfCandidatesSet", "SelectSkillToRecruit",
                "ToggleDealBreaker",
            };
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var n in Writes)
                {
                    var m = AccessTools.Method(typeof(HeadhuntersRecruitingTab), n);
                    if (m != null) yield return m;
                }
            }
            static void Postfix(HeadhuntersRecruitingTab __instance, HeadhunterPlanUI ___planUI,
                                System.Reflection.MethodBase __originalMethod)
            {
                try
                {
                    var plan = ___planUI != null ? ___planUI.currentPlan : null;
                    if (plan == null || !CompanyPlans.IsOverlayPlan(plan))   // HQ-PARITY-3 B4
                    { if (plan != null) CompanyPlans.OwnEditCommitted("headhunter pane control"); return; }
                    CompanyPlans.RoutePaneEdit("headhunter", __originalMethod?.Name ?? "recruiting settings",
                                               plan, "settings");
                }
                catch { }
            }
        }

        /// <summary>HQ-PARITY-3 B5 — THE TWO HR CONTROLS THAT HAD NO PATCH AT ALL.  "Replace absent
        /// employees" and "train up to" are registered as ANONYMOUS LAMBDAS inside HrManagerPlanUI.LoadPlan
        /// (decompile :86-89 `replaceAbsentEmployeesToggle.onValueChanged.AddListener(delegate(bool value)
        /// { _currentPlan.replaceAbsentEmployees = value; })` and :92-97 the slider's twin), so there was no
        /// method to patch and a PARTNER's press wrote the detached display copy and was silently dropped at
        /// the next fan-out.  The native registration is `RemoveAllListeners()` followed by `AddListener`, so
        /// a postfix can take the pair off again and put back wrappers that make the GAME'S OWN WRITE and then
        /// take the one commitment seam: RoutePaneEdit sends the op for a partner's row and, for a plan of
        /// this machine's own, answers false and marks the bundle urgent (OwnEditCommitted).  The slider
        /// commits on RELEASE - an EventTrigger for EndDrag and PointerUp on the slider itself - so a drag
        /// across the range is one leg, not one per step; the value and its label follow the drag as they
        /// always did.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI), "LoadPlan")]
        public static class Patch_HrPlanLoad_ControlsRoute
        {
            private static System.Reflection.FieldInfo? _fToggle, _fSlider, _fValue;
            private static bool _loggedMissing;   // B7 (F9): one line, not one per LoadPlan

            static void Postfix(UI.Smartphone.Apps.BizMan.HRManagers.HrManagerPlanUI __instance,
                                Buildings.Office.Headquarters.HrManagerPlan plan)
            {
                try
                {
                    if (__instance == null) return;
                    CompanyPlans.RegisterPane("hr", __instance);   // HQ-PARITY-4 P1
                    if (plan == null) return;
                    var t = __instance.GetType();
                    const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance
                                                           | System.Reflection.BindingFlags.NonPublic;
                    if (_fToggle == null) _fToggle = t.GetField("replaceAbsentEmployeesToggle", F);
                    if (_fSlider == null) _fSlider = t.GetField("trainingTargetSlider", F);
                    if (_fValue  == null) _fValue  = t.GetField("trainingTargetValue", F);
                    // FOLD b B7 (review F9): a RENAMED FIELD used to make this whole postfix a silent no-op -
                    // the two controls simply stayed native and a partner's press went nowhere, with nothing
                    // in the log to say why.  One WARNING, once per session, naming the field that is missing.
                    if (_fToggle == null || _fSlider == null || _fValue == null)
                    {
                        if (!_loggedMissing)
                        {
                            _loggedMissing = true;
                            string missing = (_fToggle == null ? "replaceAbsentEmployeesToggle " : "")
                                           + (_fSlider == null ? "trainingTargetSlider " : "")
                                           + (_fValue  == null ? "trainingTargetValue " : "");
                            Plugin.Logger.LogWarning($"[Merger] the HR controls are not hooked: field {missing.Trim()} not found on {t.Name}.");
                        }
                        return;
                    }
                    var tg  = _fToggle.GetValue(__instance) as UnityEngine.UI.Toggle;
                    var sl  = _fSlider.GetValue(__instance) as UnityEngine.UI.Slider;
                    var lbl = _fValue.GetValue(__instance)  as TMPro.TMP_Text;
                    if (tg != null)
                    {
                        tg.onValueChanged.RemoveAllListeners();          // the native lambda, :86-89
                        tg.onValueChanged.AddListener(v =>
                        {
                            plan.replaceAbsentEmployees = v;             // the game's own write, :88
                            CompanyPlans.RoutePaneEdit("hr", "replace absent employees", plan, "replaceabsent", "", 0, 0f, v);
                        });
                        tg.SetIsOnWithoutNotify(plan.replaceAbsentEmployees);
                    }
                    if (sl != null)
                    {
                        sl.onValueChanged.RemoveAllListeners();          // the native lambda, :92-97
                        sl.onValueChanged.AddListener(v =>
                        {
                            int n = UnityEngine.Mathf.RoundToInt(v);
                            plan.trainingTarget = n;                     // the game's own write, :94
                            if (lbl != null) lbl.text = $"{n}%";          // and its own label, :95
                        });
                        sl.SetValueWithoutNotify(plan.trainingTarget);
                        ArmSliderCommit(sl, plan);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] hr pane controls: {ex.Message}"); }
            }

            private static void ArmSliderCommit(UnityEngine.UI.Slider sl, Buildings.Office.Headquarters.HrManagerPlan plan)
            {
                try
                {
                    var trig = sl.gameObject.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                    if (trig == null) trig = sl.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                    if (trig.triggers == null)
                        trig.triggers = new System.Collections.Generic.List<UnityEngine.EventSystems.EventTrigger.Entry>();
                    trig.triggers.Clear();                                // the pane is re-loaded for a new plan
                    // FOLD c C1: a re-arm IS a fresh load - the pane cannot be re-loaded in the middle of a
                    // drag - so the hold starts false, and the slider it is armed on is remembered so the
                    // gate can test the game's own activity instead of the flag alone.
                    CompanyPlans.HrSliderGo = sl.gameObject;
                    CompanyPlans.HrSliderHeld = false;
                    // FOLD d E1: and Unity's own lifecycle ends the hold when the pointer-up cannot.  The
                    // guard's OnDisable fires the moment this object or any ancestor is deactivated or
                    // destroyed - the missed-pointer-up case exactly.  One per slider: it is added once and
                    // left there, unlike the triggers above, which are rebuilt for each plan.
                    if (sl.gameObject.GetComponent<HrSliderHoldGuard>() == null)
                        sl.gameObject.AddComponent<HrSliderHoldGuard>();
                    // FOLD b B2 (d): the SAME pointer events say the slider is HELD, so a bundle arriving
                    // mid-drag defers its redraw instead of re-seating the slider from the plan
                    // (HrManagerPlanUI.LoadPlan :98) and having the release below publish that reset value.
                    foreach (var down in new[] { UnityEngine.EventSystems.EventTriggerType.PointerDown,
                                                 UnityEngine.EventSystems.EventTriggerType.BeginDrag })
                    {
                        var d = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = down };
                        d.callback.AddListener(_ => CompanyPlans.HrSliderHeld = true);
                        trig.triggers.Add(d);
                    }
                    foreach (var kind in new[] { UnityEngine.EventSystems.EventTriggerType.EndDrag,
                                                 UnityEngine.EventSystems.EventTriggerType.PointerUp })
                    {
                        var e = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = kind };
                        e.callback.AddListener(_ =>
                        {
                            CompanyPlans.HrSliderHeld = false;
                            CompanyPlans.RoutePaneEdit("hr", "training target", plan,
                                                       "trainingtarget", "", UnityEngine.Mathf.RoundToInt(sl.value));
                        });
                        trig.triggers.Add(e);
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Merger] hr training slider commit: {ex.Message}"); }
            }
        }

        /// <summary>r2 MAJOR-5: the tab's StopRecruiting is a bare `planUI.currentPlan.isRecruiting = false`
        /// (HeadhuntersRecruitingTab.cs:156), so on a partner's row it stopped nothing. Routed as
        /// `stoprecruit`; the local panel refresh is the game's own and is left to run.</summary>
        [HarmonyPatch(typeof(HeadhuntersRecruitingTab), "StopRecruiting")]
        public static class Patch_HeadhunterStopRecruiting_MergerGate
        {
            static bool Prefix(HeadhunterPlanUI ___planUI)
            {
                try
                {
                    var plan = ___planUI != null ? ___planUI.currentPlan : null;
                    return !CompanyPlans.RoutePaneEdit("headhunter", "stop recruiting", plan, "stoprecruit");
                }
                catch { return true; }
            }
        }

        /// <summary>r3 G1: the two AUTOMATIC-REPLACEMENT toggles are bare live writes on `planUI.currentPlan`
        /// (HeadhuntersAutomaticReplacementTab.cs:163-166 `planUI.currentPlan.automaticallyReplaceOnResign =
        /// toggled;` and :168-171 the retire twin) - exactly the shape of the recruiting-tab writes above, and
        /// equally unrouted: on a partner's row they reached the DETACHED temp object and nothing else.
        /// POSTFIXED the same way: the native write updates the display the player just made, then ONE
        /// `settings` leg carries the whole DTO to the runner, which copies the pair onto its real plan
        /// (ApplyRecruitSettings). Own plans are untouched (IsOverlayPlan answers false).</summary>
        [HarmonyPatch]
        public static class Patch_HeadhunterAutoReplaceToggles_MergerRoute
        {
            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (var n in new[] { "ToggleAutomaticReplacementOnResign", "ToggleAutomaticReplacementOnRetire" })
                {
                    var m = AccessTools.Method(typeof(HeadhuntersAutomaticReplacementTab), n);
                    if (m != null) yield return m;
                }
            }
            // ___planUI = '___' + 'planUI', decompile HeadhuntersAutomaticReplacementTab.cs:17-18
            // `[SerializeField] private HeadhunterPlanUI planUI;`.
            static void Postfix(HeadhunterPlanUI ___planUI, System.Reflection.MethodBase __originalMethod)
            {
                try
                {
                    var plan = ___planUI != null ? ___planUI.currentPlan : null;
                    if (plan == null || !CompanyPlans.IsOverlayPlan(plan))   // HQ-PARITY-3 B4
                    { if (plan != null) CompanyPlans.OwnEditCommitted("headhunter pane control"); return; }
                    CompanyPlans.RoutePaneEdit("headhunter", __originalMethod?.Name ?? "automatic replacement",
                                               plan, "settings");
                }
                catch { }
            }
        }

        /// <summary>r2 MAJOR-4, THE MEMBER END. SetUpHrManagerPlansList builds `_hrManagerPlansIds` from
        /// `SaveGameManager.Current.hrManagerPlans` with NO headquarters filter
        /// (HeadhuntersAutomaticReplacementTab.cs:94-97), so on a PARTNER's headhunter plan the dropdown listed
        /// THIS player's own HR plans - ids that belong to another company entirely. On an overlay plan the
        /// list is replaced with the registry's HR plans of THAT headquarters and every slot dropdown is
        /// re-optioned. SetOptions only sets state and redraws (UI.Elements/Dropdown.cs:304-323) - it does not
        /// raise onOptionSelected - so re-optioning after the native listeners are attached sends nothing.</summary>
        /// <summary>CROSS-HR-1b K2(a): the shadow filter for the OWN-plan branch of the tab below.  False
        /// when nothing had to be dropped and the native list stands.  `_hrManagerPlansIds` holds the ids in
        /// the dropdown's own order with a leading null (decompile :94-98) and the names come from the tab's
        /// own GetDropdownPlanName, so what goes back is exactly what native would have built had the
        /// shadows never been in gi.hrManagerPlans - no new text of any kind.</summary>
        private static bool StripShadowHrPlanIds(HeadhuntersAutomaticReplacementTab tab,
                                                 out System.Collections.Generic.List<string> ids,
                                                 out System.Collections.Generic.List<string> names,
                                                 out int dropped)
        {
            ids = null; names = null; dropped = 0;
            var f = AccessTools.Field(typeof(HeadhuntersAutomaticReplacementTab), "_hrManagerPlansIds");
            var cur = f?.GetValue(tab) as System.Collections.Generic.List<string>;
            if (cur == null) return false;
            var kept = new System.Collections.Generic.List<string>();
            foreach (var id in cur)
            {
                if (!string.IsNullOrEmpty(id) && CompanyPlans.IsShadowHrPlanId(id)) { dropped++; continue; }
                kept.Add(id);
            }
            if (dropped == 0) return false;
            var name = AccessTools.Method(typeof(HeadhuntersAutomaticReplacementTab), "GetDropdownPlanName");
            names = new System.Collections.Generic.List<string>();
            foreach (var id in kept)
            {
                string n = null;
                try { n = name?.Invoke(tab, new object[] { id }) as string; } catch { }
                names.Add(n ?? "");
            }
            ids = kept;
            f.SetValue(tab, ids);
            return true;
        }

        [HarmonyPatch(typeof(HeadhuntersAutomaticReplacementTab), "SetUpHrManagerPlansList")]
        public static class Patch_HeadhunterHrPlanList_MergerOverlay
        {
            static void Postfix(HeadhuntersAutomaticReplacementTab __instance, HeadhunterPlanUI ___planUI)
            {
                try
                {
                    var plan = ___planUI != null ? ___planUI.currentPlan : null;
                    if (plan == null) return;
                    System.Collections.Generic.List<string> ids, names;
                    int dropped = 0;
                    bool overlay = CompanyPlans.TryOverlayHrPlanOptions(plan, out ids, out names);
                    // CROSS-HR-1b K2(a): on MY OWN headhunter plan the native list is EVERY entry of
                    // gi.hrManagerPlans with no headquarters filter (decompile :95-97), so a partner's
                    // SHADOW was offered here and the slot write at :149 would put its id in my own plan.
                    // The shadows come out of the ids list and the slots are re-optioned off it, exactly as
                    // the overlay branch does; nothing else about my own plan changes.
                    if (!overlay && !StripShadowHrPlanIds(__instance, out ids, out names, out dropped)) return;
                    // the ids list is read back by index in SelectHrManagerPlan (PlanEditStringAt), so it is the
                    // one thing that MUST be replaced; the field type differs per list, hence reflection.
                    var f = AccessTools.Field(__instance.GetType(), "_hrManagerPlansIds");
                    if (f != null) f.SetValue(__instance, ids);
                    // r3 G4d: the slot rows are INSTANTIATED off `hrManagerSlotTemplate` (decompile :98-104), and
                    // the template itself - inactive, never a slot - carries its own "HRManagerDropdown" child.
                    // The (true) walk found that one first and shifted every preselection by one slot.
                    UnityEngine.Transform template = null;
                    try
                    {
                        var tf = AccessTools.Field(__instance.GetType(), "hrManagerSlotTemplate");
                        template = tf != null ? tf.GetValue(__instance) as UnityEngine.Transform : null;
                    }
                    catch { }
                    int slot = 0;
                    foreach (var d in __instance.GetComponentsInChildren<UI.Elements.Dropdown>(true))
                    {
                        if (d == null || d.name != "HRManagerDropdown") continue;
                        if (template != null ? d.transform.IsChildOf(template) : !d.gameObject.activeInHierarchy) continue;
                        string cur = null;
                        try { cur = plan.assignedHrPlans != null && slot < plan.assignedHrPlans.Length ? plan.assignedHrPlans[slot] : null; } catch { }
                        d.SetOptions(names, false, ids.IndexOf(cur));
                        slot++;
                    }
                    Plugin.Logger.LogInfo(overlay
                        ? $"[Plans] headhunter automatic-replacement offered {ids.Count - 1} partner HR plan(s) on this headquarters."
                        : $"[Plans] headhunter automatic-replacement withheld {dropped} partner display copy(ies) from my own plan's choices.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] headhunter hr-plan dropdown overlay: {ex.Message}"); }
            }
        }

        /// <summary>PURCHASING CREATION (part 1 MAJOR-3, now routed). `ImportManagerDialog.OnImportPartnershipSettingsSet`
        /// keys the new partnership to the chosen AGENT's headquarters (:104
        /// `headquartersAddress = inputComponent.selectedEmployeeInstance.assignedAddress`) and adds it to THIS
        /// machine's own list (:107 `SaveGameManager.Current.importPartnerships.Add(importPartnership)`). The
        /// agent list is global (ImportPartnershipSettings.cs:22-27) and merged-partner staff are exempt from
        /// the injected-staff filter, so an agent of a PARTNER's headquarters can be picked here. Part 2a
        /// sends the creation to the RUNNER of that headquarters with the importer this dialog is on (:102
        /// `importAddress = DialogController.current.contact.Address`) and the agent the player chose; the
        /// dialog's own null return is the game's existing do-nothing path, so there is no new text. Own
        /// headquarters and single player are untouched.</summary>
        [HarmonyPatch(typeof(Dialogs.ImportManagerDialog), "OnImportPartnershipSettingsSet")]
        public static class Patch_ImportPartnershipCreate_MergerGate
        {
            static bool Prefix(ref Entities.DialogEntry __result)
            {
                try
                {
                    if (MergerFlip.FlippedCount == 0) return true;      // inert without a merger
                    var dc = DialogController.current;
                    var input = dc != null ? dc.GetInputComponent<UI.Dialog.ImportPartnershipSettings>() : null;
                    var agent = input != null ? input.selectedEmployeeInstance : null;
                    if (agent == null) return true;                     // the game's own 'select an agent' path
                    var importer = dc != null && dc.contact != null ? dc.contact.Address : null;
                    if (!CompanyPlans.RoutePartnershipCreate(agent.assignedAddress, agent.id, importer)) return true;
                    __result = null;
                    return false;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] partnership dialog: {ex.Message}"); return true; }
            }
        }

        /// <summary>HQ-PARITY-5 C3, THE PARTNER'S SOURCE PRODUCTS.  For a WAREHOUSE destination the pane's
        /// product picker lists `warehouse.GetProducts()` of the plan's SOURCE building AS THIS MACHINE HOLDS
        /// IT (decompile UI.Smartphone.Apps.BizMan.LogisticsManagers/LogisticsManagerPlanUI
        /// .GetListOfAvailableProducts :471-495, the branch at :479-485), plus the imports and the targets
        /// already set.  A co-member's replica of a partner's warehouse has no pallets, so that call answers
        /// nothing and the picker draws empty - HQ-PARITY-2 P1 carried the COUNTS, never the list.  The
        /// OWNER's list travels on the plan DTO now (PwLogisticsPlan.SourceProducts) and is appended here, on
        /// a DISPLAY plan only, under the game's OWN two conditions for that branch: the destination is a
        /// warehouse AND the plan has a source address (:479-489).  FOLD b G7 (review): CanProductBeDelivered
        /// is NOT applied - the game's warehouse branch does not filter the warehouse's own products with it
        /// (only the import/export branch does, :491-495), and Warehouse.GetProducts already yields retail
        /// products and bags only (Entities/Warehouse.cs:35-46), so filtering here dropped names the owner's
        /// own pane shows.  Nothing is drawn and nothing is removed - the postfix only adds names the result does not already hold, and a throw leaves
        /// __result exactly as the game built it.</summary>
        [HarmonyPatch(typeof(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI),
                      "GetListOfAvailableProducts", new[] { typeof(Entities.LogisticsManagerPlanDestination) })]
        public static class Patch_LogisticsProducts_OwnerSourceList
        {
            /// <summary>One INFO per plan id, so a picker that redraws on every collapse does not repeat.</summary>
            static readonly System.Collections.Generic.HashSet<string> _logged =
                new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            static void Postfix(UI.Smartphone.Apps.BizMan.LogisticsManagers.LogisticsManagerPlanUI __instance,
                                Entities.LogisticsManagerPlanDestination destination,
                                ref System.Collections.Generic.List<string> __result)
            {
                try
                {
                    if (__instance == null || destination == null || __result == null) return;
                    var plan = __instance._currentPlan;
                    if (plan == null || !CompanyLists.IsDisplayPlan(plan)) return;
                    var reg = BuildingHelper.GetBuildingRegistration(destination.deliveryTargetAddress);
                    if (reg == null || reg.GetBuildingType() != "ba:buildingtype_warehouse") return;   // the game's own :479 branch
                    if (plan.targetAddress == null) return;                                            // and its :481 guard
                    string id = plan.id ?? "";
                    var dto = CompanyPlans.LogisticsDtoOf(id);
                    if (dto == null || dto.SourceProducts == null) return;
                    int added = 0;
                    foreach (var item in dto.SourceProducts)
                    {
                        if (string.IsNullOrEmpty(item) || __result.Contains(item)) continue;
                        __result.Add(item);
                        added++;
                    }
                    if (_logged.Add(id))
                        Plugin.Logger.LogInfo($"[Plans] logistics products for display plan {id}: {added} added from the owner's source building.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Plans] logistics product list: {ex.Message}"); }
            }
        }

    }
}
