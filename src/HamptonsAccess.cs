// ── Hamptons guest access (user decision 2026-08-29: RESIDENCE-GRANT requirement) ────────────────
//
// 1.0's Hamptons private zones have two locks, both keyed to the LOCAL machine's tenancy:
//   * HamptonsPrivateFenceDoor.IsLocked() — scans registrations for RentedByPlayer/
//     BuildingOwnedByPlayer with unlocksHamptonsPrivateFence + a matching privateFenceIndex;
//   * CityHamptonsHouseController.RefreshBlockerCollider() — a PHYSICAL barrier over the plot,
//     enabled whenever the registration is not RentedByPlayer (or is on sale / service-blocked).
// Under the mod's ownership model only the RENTER's machine reads RentedByPlayer=true, so a
// partner invited into a friend's Hamptons home met a locked gate and an invisible wall
// (sweep-3 mis#3/#4). Ruling: a RESIDENCE GRANT for a building behind the fence opens both —
// exactly the population CanEnterGranted already answers for every other shared-home surface.
// Any session player without the grant stays locked out, matching native "private" semantics.
//
// The fence lock is re-evaluated LIVE on every trigger enter, so it needs no refresh hook. The
// plot blocker is CACHED collider state — GrantSync.SetEnterableBuildings calls
// RefreshAllBlockers() on any change so an arriving/removed grant repaints within the same
// second (ruling 32: live parity of shared surfaces; the sweep is event-driven and rare).
//
// 2026-09-18 — two more locks, both from the same "tenancy term on the LOCAL machine" family
// (design read .modding/03-systems/hamptons-house-consistency-2026-09-18.md):
//   * H-MANOR-1 (ENTRY): HamptonsHouse.LateUpdate :261 and OnCityMapClosed :142 only run the plot
//     volume check while _buildingRegistration.RentedByPlayer — so a granted guest walked through an
//     open fence and an open blocker onto a plot that never noticed them. Same ruling as the blocker:
//     the native formula with the tenancy term satisfied by the grant, expressed as the project's
//     TEMPORARY FLIP (HamptonsTenancyFlip below, modelled on HousingFurniture.Enter/Exit).
//   * RENDERING (user ruling 2026-09-18, "even someone without permissions should see the house
//     correctly, just not have access to it"): CityHamptonsHouseController.ToggleLodMode :93-117
//     shows the real renderers only while the LOCAL copy is rented, so every session player's house
//     read as the closed outdoorVersion shell on everyone else's screen. Postfix re-asserts the
//     renderer decision from the OWNERSHIP question instead — and NEVER from the tenancy flip, which
//     would also open LateUpdate :261 and let an ungranted player walk in (design doc, Traps).
using HarmonyLib;
using Helpers;      // RealEstateHelper.IsOnSale extension
using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigAmbitionsMP
{
    internal static class HamptonsAccess
    {
        /// <summary>True when the local player holds a residence grant for a building that unlocks
        /// this fence index. Mirrors the native scan in HamptonsPrivateFenceDoor.IsLocked with the
        /// tenancy term replaced by the grant set.</summary>
        internal static bool GrantOpensFence(int fenceIndex)
        {
            try
            {
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return false;
                foreach (var reg in regs)
                {
                    if (reg == null) continue;
                    var b = reg.BuildingCached;
                    if (b == null || !b.unlocksHamptonsPrivateFence || b.privateFenceIndex != fenceIndex) continue;
                    if (GrantSync.CanEnterGranted(GameStateReader.AddressKey(reg))) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Grant set or ownership changed → re-evaluate every Hamptons plot barrier AND every
        /// Hamptons house's renderer decision. Both are CACHED state (collider enabled flag,
        /// _renderersEnabled), so an arriving/removed grant or a new tenant has to repaint them now.
        /// Event-driven and rare (a grant push, a rent/vacate apply), so the scene walk is acceptable.
        /// The LOD call goes through the native method so the Postfix below is what decides; while the
        /// city map is open it is passed active:true, exactly as OnRentedBuilding does.</summary>
        internal static void RefreshAllBlockers()
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var all = UnityEngine.Object.FindObjectsOfType<CityHamptonsHouseController>(true);
                if (all == null || all.Length == 0) return;
                bool mapOpen = false; try { mapOpen = CityMap.IsOpen; } catch { }
                int done = 0;
                foreach (var c in all)
                {
                    // FindObjectsOfType(true) also hands back INACTIVE controllers, whose
                    // buildingRegistration can still be null — both native bodies dereference it on
                    // their first line (CityHamptonsHouseController.cs:61 and :95), so skip them here
                    // rather than swallowing an NRE and still claiming they were re-evaluated.
                    if (c == null || c.buildingRegistration == null) continue;
                    try { ExitIfAccessLapsed(c); }
                    catch (Exception ex) { WarnOnce("RefreshAllBlockers/lapse", ex); }
                    try { c.RefreshBlockerCollider(); }
                    catch (Exception ex) { WarnOnce("RefreshAllBlockers/blocker", ex); }
                    try { c.ToggleLodMode(mapOpen); }
                    catch (Exception ex) { WarnOnce("RefreshAllBlockers/lod", ex); }
                    done++;
                }
                Plugin.Logger.LogInfo($"[Hamptons] access/ownership change → {done} of {all.Length} Hamptons controller(s) re-evaluated (blocker + renderers).");
            }
            catch (Exception ex) { WarnOnce("RefreshAllBlockers", ex); }
        }

        /// <summary>G2 (fold r1) — A GUEST WHOSE ACCESS LAPSES WHILE THEY STAND ON THE PLOT. The native
        /// LateUpdate gate (HamptonsHouse.cs:261) covers the WHOLE body, so the moment GuestMayEnter goes
        /// false the plot check stops running and OnExitPlot can never fire by itself: the player is left
        /// with BuildingManager.building set, the indoor camera and nav agent, a stuck broadcast Bldg tag —
        /// and the very refresh that revoked the grant would re-close the barrier AROUND them. So the same
        /// event that changes access performs the exit the gate can no longer reach. Event-driven, no timer.
        /// The blocker is then held open until they have physically stepped off (see the close-pending
        /// flag, re-checked by the LateUpdate prefix).</summary>
        private static void ExitIfAccessLapsed(CityHamptonsHouseController c)
        {
            var hh = c.hamptonsHouse;
            if (hh == null || !hh._isPlayerInsidePlot) return;
            var reg = c.buildingRegistration;
            if (reg == null || reg.RentedByPlayer) return;          // really the tenant here — native owns it
            if (GuestMayEnter(reg)) return;                         // still allowed in
            string key = KeyOf(reg);
            _closePending[key] = c;                                 // hold the barrier open until they are off the plot
            hh.OnExitPlot();                                        // native exit: hover colliders, occlusion, ExitFromBuilding(0)
            Plugin.Logger.LogInfo($"[Hamptons] access to '{key}' ended while on the plot — exited the house.");
        }

        /// <summary>The close-pending half of G2, driven by the LateUpdate prefix we already own (no timer):
        /// while the local player is still inside a revoked plot's volume the barrier stays open, and the
        /// frame they are outside it, the native formula is re-evaluated once and the flag cleared.</summary>
        internal static void TickClosePending(HamptonsHouse house)
        {
            if (_closePending.Count == 0 || house == null) return;
            var reg = house._buildingRegistration;
            if (reg == null) return;
            string key = KeyOf(reg);
            if (!_closePending.TryGetValue(key, out var c)) return;
            bool inside = true;
            try { inside = house.plotVolume != null && house.plotVolume.IsInside; } catch { }
            if (inside) return;
            _closePending.Remove(key);
            try { if (c != null) c.RefreshBlockerCollider(); }
            catch (Exception ex) { WarnOnce("TickClosePending/refresh", ex); }
            Plugin.Logger.LogInfo($"[Hamptons] '{key}' — the player has stepped off the revoked plot; the barrier is closed again.");
        }

        /// <summary>True while the local player is still physically inside this house's plot volume. The one
        /// extra term the blocker formula needs so a revoked guest is never walled in.</summary>
        internal static bool LocalIsInsidePlot(HamptonsHouse? house)
        {
            try { return house != null && house.plotVolume != null && house.plotVolume.IsInside; }
            catch { return false; }
        }

        /// <summary>G3 (fold r1) — THE HAMPTONS ENTRY HOOK. The mod's only entry-time interior work lives in
        /// the DelayedEnterBuildingActions prefix (MPPatches.cs:816-863), which the Hamptons path never
        /// reaches (BuildingManager.cs:610 calls it only from EnterBuildingCoroutine; EnterHamptonsBuilding
        /// does not), so a guest walked into a manor whose itemInstances were never requested — an empty
        /// house — and a CLIENT-owned manor's furniture was never published to the host either. This is that
        /// same body, for the plot path, reusing the same helpers (no second protocol).
        ///
        /// Deliberately NOT copied from the door hook: TrafficSync.OnEnteredBuilding (a Hamptons visitor is
        /// physically outdoors — flipping LocalInBuilding would drop the traffic anchor around them) and
        /// MPCanvasUI.ArmBlackOverlayScan (there is no fade transition to leave an overlay stuck).
        ///
        /// EXIT needs nothing here: ExitFromHamptonsBuilding raises GlobalEvents.onExitBuilding
        /// (BuildingManager.cs:1611), which ExitBuildingNotifier (MPPatches.cs:941-974) already answers with
        /// SetCurrentShop("", ""), InteriorSync.NotifyLocalBuildingExit and SendPlayerExitedBuilding.</summary>
        internal static void OnPlotEntered(HamptonsHouse? house)
        {
            try
            {
                var reg = house != null ? house._buildingRegistration : null;
                if (reg == null) return;
                if (!MPServer.IsRunning && !MPClient.IsConnected && !MPClient.OfflineFork) return;
                string addr = KeyOf(reg);
                if (addr.Length == 0) return;
                string runner = ""; try { runner = reg.businessOwnerRivalId ?? ""; } catch { }
                MPRegisterSync.SetCurrentShop(runner, addr);   // presence + RemoteSale context, as the door path does
                if (!MPClient.IsConnected) return;             // the host IS the source of truth for its own interiors
                bool guestHere = GrantSync.CanEnterGranted(addr);
                Plugin.Logger.LogInfo($"[Housing] enter-hook (hamptons plot) addr='{addr}' rented={reg.RentedByPlayer} guest={guestHere}.");
                if (!guestHere && InteriorSync.TrySendOwnerSnapshotOnEntry(reg, addr)) return;
                MPClient.SendInteriorRequest(addr);
            }
            catch (Exception ex) { WarnOnce("Hamptons OnEnterPlot entry-hook", ex); }
        }

        // ── shared plumbing for the per-frame gates ──────────────────────────────────────────────
        // AddressKey builds a string, so it must NOT run per frame per house: cache it per
        // registration (registrations live as long as the save does).
        private static readonly Dictionary<BuildingRegistration, string> _addrOf = new Dictionary<BuildingRegistration, string>();
        private static readonly HashSet<string> _entryLogged  = new HashSet<string>();
        private static readonly HashSet<string> _renderLogged = new HashSet<string>();
        private static readonly HashSet<string> _warned       = new HashSet<string>();
        private static readonly Dictionary<string, bool> _isHamptonsAddr = new Dictionary<string, bool>();
        private static readonly Dictionary<string, CityHamptonsHouseController> _closePending = new Dictionary<string, CityHamptonsHouseController>();
        private static int _regCountAtCache = -1;

        /// <summary>Every field above is keyed to ONE loaded world — registration objects, address keys and
        /// once-per-session log latches all die with the scene. Called from MPCanvasUI's game-load detector
        /// beside the other per-world resets.</summary>
        internal static void Reset()
        {
            try
            {
                _addrOf.Clear(); _entryLogged.Clear(); _renderLogged.Clear(); _warned.Clear();
                _isHamptonsAddr.Clear(); _closePending.Clear();
                _regCountAtCache = -1;
                HamptonsTenancyFlip.Reset();
            }
            catch { }
        }

        /// <summary>Cached address key for a registration (no per-frame allocation).</summary>
        internal static string KeyOf(BuildingRegistration reg)
        {
            if (_addrOf.TryGetValue(reg, out var cached)) return cached;
            string key = "";
            try { key = GameStateReader.AddressKey(reg); } catch { }
            if (key.Length > 0) _addrOf[reg] = key;
            return key;
        }

        /// <summary>One WARNING per patch, with its stack, then silent — these bodies run per frame.</summary>
        internal static void WarnOnce(string patch, Exception ex)
        {
            try
            {
                if (!_warned.Add(patch)) return;
                Plugin.Logger.LogWarning($"[Hamptons] {patch}: {ex.GetType().Name}: {ex.Message} (further occurrences are silent)\n{ex.StackTrace}");
            }
            catch { }
        }

        /// <summary>H-MANOR-1: may the LOCAL player be treated as this house's tenant for the ENTRY
        /// gates? Grant only — never ownership: an ungranted onlooker gets the rendering ruling below
        /// and nothing else. Logs once per address per session, the first time the gate opens.</summary>
        internal static bool GuestMayEnter(BuildingRegistration? reg)
        {
            if (reg == null) return false;
            if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return false;
            if (!GrantSync.AnyEnterable) return false;                    // no grants at all — cheapest exit
            string key = KeyOf(reg);
            if (!GrantSync.CanEnterGranted(key)) return false;
            if (_entryLogged.Add(key))
                Plugin.Logger.LogInfo($"[Hamptons] plot entry opened for granted guest at '{key}'.");   // rendering is decided separately, by the LOD postfix
            return true;
        }

        /// <summary>RENDERING predicate: which SESSION PLAYER holds this address, from the ledgers the
        /// two roles already keep — the host's BuildingOwners, and on both roles the persisted ownership
        /// stamps that GameStatePatcher reads (clients keep no building→owner map of their own,
        /// GrantSync.cs :262). "" when nobody in the session holds it (AI / unowned → native shell).</summary>
        internal static bool SessionTenantLabel(BuildingRegistration reg, string addrKey, out string label)
        {
            label = "";
            try
            {
                if (MPServer.IsRunning && addrKey.Length > 0
                    && MPServer.BuildingOwners.TryGetValue(addrKey, out var ledger)
                    && !string.IsNullOrEmpty(ledger))
                {
                    // A1 (2026-09-18, log only): the ledger files the HOST's own buildings under the literal
                    // alias "host" (GameStatePatcher.IsHostLedgerId), so the render line read
                    // "(rented by 'host')" on the host's own screen. Resolve the alias to the host's real
                    // player id the way the rest of the mod does (BusinessSync :914/:961,
                    // `o == "host" ? MPConfig.PlayerId : o`). Display only — still never an id to key on.
                    label = (ledger == "host" && MPConfig.PlayerId.Length > 0) ? MPConfig.PlayerId : ledger;
                    return true;
                }
                string runner = ""; try { runner = reg.businessOwnerRivalId ?? ""; } catch { }
                if (GameStatePatcher.IsSessionPlayerRivalId(runner)) { label = runner; return true; }
                string deed = ""; try { deed = reg.buildingOwnerRivalId ?? ""; } catch { }
                if (GameStatePatcher.IsSessionPlayerId(deed)) { label = deed; return true; }
                // A grant is proof a session player holds the address, but it does not carry WHICH one on
                // this machine — hence the out-parameter: this is a display label, never an id to key on.
                if (GrantSync.CanEnterGranted(addrKey)) { label = "(a session player — id not known here)"; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>One "real house shown" line per address per session.</summary>
        internal static bool RenderLogOnce(string addrKey) => _renderLogged.Add(addrKey);

        /// <summary>H3 presence mask: is this address key a HAMPTONS house? Resolved from the game's own
        /// registration (BuildingCached.IsHamptonsHouse) and cached per address — no scene search, no
        /// per-packet walk. An UNRESOLVABLE address is cached as a miss too: position packets arrive at
        /// ~10 Hz and FindRegistration is a linear scan, so a never-cached miss would re-scan forever. The
        /// whole table is dropped when the registration count changes (a world loaded / buildings added) and
        /// on scene reset, which is what makes caching the miss safe.</summary>
        internal static bool IsHamptonsAddress(string? addressKey)
        {
            if (string.IsNullOrEmpty(addressKey)) return false;
            string key = addressKey!;
            int n = -1; try { n = SaveGameManager.Current?.BuildingRegistrations?.Count ?? -1; } catch { }
            if (n != _regCountAtCache) { _isHamptonsAddr.Clear(); _regCountAtCache = n; }
            if (_isHamptonsAddr.TryGetValue(key, out var known)) return known;
            bool isH = false;
            try
            {
                var reg = GameStatePatcher.FindRegistration(key);
                if (reg != null)
                {
                    var b = reg.BuildingCached;
                    isH = b != null && b.IsHamptonsHouse();
                }
            }
            catch { }
            _isHamptonsAddr[key] = isH;
            return isH;
        }
    }

    /// <summary>H-MANOR-1 (user-approved 2026-09-18) — the TEMPORARY TENANCY FLIP for the Hamptons ENTRY
    /// gates, the same discipline as HousingPatches' HousingFurniture.Enter/Exit: the Prefix sets
    /// RentedByPlayer = true only when it was false, the grant holds and we are in an MP world; a
    /// FINALIZER (never a Postfix) puts it back, so a throw inside the native body cannot leave a guest
    /// permanently stamped as the tenant of someone else's house.
    ///
    /// Settled before writing (decompile reads): the flip spans OnEnterPlot → BuildingManager.EnterBuilding,
    /// and for a Hamptons house that call is EnterHamptonsBuilding (BuildingManager.cs:636-691), which is
    /// fully synchronous and does NOT take an owner interior path: LoadBuildingInternal skips
    /// ApplyInteriorDesign for Hamptons houses (:899 requires hamptonsHouse == null), instantiates no items
    /// (the untenanted LoadHamptonsItems loader owns those), and its only RentedByPlayer read
    /// (IsPlayerOwnedBusiness :925) merely short-circuits BUSINESS layout loading, which a residence with a
    /// null Layout skips anyway. The owner-snapshot trap at MPPatches ~:853 lives in
    /// DelayedEnterBuildingActions, which only EnterBuildingCoroutine runs (BuildingManager.cs:610) — the
    /// Hamptons path never reaches it. Exit is symmetric: ExitFromBuilding → ExitFromBuildingCoroutine →
    /// ExitFromHamptonsBuilding (:1477-1480), before any yield, still inside the flipped window.</summary>
    internal static class HamptonsTenancyFlip
    {
        private static int _depth;
        private static int _suspendDepth;
        private static BuildingRegistration? _forced;
        private static bool _savedValue;

        internal static void Reset() { _depth = 0; _suspendDepth = 0; _forced = null; }

        /// <summary>G1 (fold r1) — NARROW THE WINDOW TO THE GATE. OnEnterPlot fires
        /// BuildingManager.EnterBuilding, which raises GlobalEvents.onEnterBuilding
        /// (BuildingManager.cs:688) and the 'ba:gameevent_enteredbuilding' GameEvent (:689); OnExitPlot
        /// raises onExitBuilding (:1611). Goal and achievement conditions keyed on OWNING a Hamptons house
        /// are unaudited, and a guest must never satisfy one, so the flip is SUSPENDED for the duration of
        /// those two bodies. What is left inside the flipped window is only the gate's own `if` and the
        /// bookkeeping behind it: CheckIfPlayerIsInsidePlot (reads plotVolume.IsInside, the Address and
        /// IsOnSale — no writes), CheckIfPlayerIsInsideHouse (RoomWallOcclusionManager + RainHelper) and
        /// CheckHeight / OnCurrentHeightChanged, which read only the player's Y and touch floor shaders,
        /// customer visibility and a parameterless GlobalEvents.onCurrentHeightChanged —
        /// MultipleHeightsBuildingController never reads a registration at all.</summary>
        internal static void Suspend()
        {
            try
            {
                if (_suspendDepth == 0 && _forced != null) _forced.RentedByPlayer = _savedValue;
                _suspendDepth++;
            }
            catch { }
        }

        internal static void Resume()
        {
            try
            {
                if (_suspendDepth > 0) _suspendDepth--;
                if (_suspendDepth == 0 && _depth > 0 && _forced != null) _forced.RentedByPlayer = true;
            }
            catch { _suspendDepth = 0; }
        }

        internal static void Enter(HamptonsHouse house)
        {
            try
            {
                if (_depth == 0)
                {
                    _forced = null;
                    var reg = house != null ? house._buildingRegistration : null;
                    if (reg != null && !reg.RentedByPlayer && HamptonsAccess.GuestMayEnter(reg))
                    {
                        _forced = reg; _savedValue = reg.RentedByPlayer; reg.RentedByPlayer = true;
                    }
                }
                _depth++;
            }
            catch { }
        }

        internal static void Exit()
        {
            try
            {
                if (_depth > 0) _depth--;
                if (_depth == 0 && _forced != null) { _forced.RentedByPlayer = _savedValue; _forced = null; }
            }
            catch { _depth = 0; _forced = null; }
        }
    }

    /// <summary>ENTRY gate 1: HamptonsHouse.LateUpdate :259-270 — the plot-volume check that fires
    /// OnEnterPlot/OnExitPlot. Per frame per loaded house, so the flip's own gate is ordered cheapest
    /// first (MP world → any grants at all → cached address key → grant set). It doubles as G2's recurring
    /// check: the one place that is guaranteed to run every frame whatever the gate decides, so a barrier
    /// held open around a revoked guest is closed the moment they are off the plot (first test is a
    /// dictionary Count, so it costs nothing when nothing is pending).</summary>
    [HarmonyPatch(typeof(HamptonsHouse), "LateUpdate")]
    public static class Patch_HamptonsHouse_LateUpdate_GrantOpens
    {
        static void Prefix(HamptonsHouse __instance)
        {
            try { HamptonsAccess.TickClosePending(__instance); HamptonsTenancyFlip.Enter(__instance); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_LateUpdate_GrantOpens", ex); }
        }
        static void Finalizer()
        {
            try { HamptonsTenancyFlip.Exit(); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_LateUpdate_GrantOpens/fin", ex); }
        }
    }

    /// <summary>ENTRY gate 2: HamptonsHouse.OnCityMapClosed :139-154 carries the same tenancy term — it is
    /// how the game re-detects a player standing on their plot after fast travel / the city map. Without it
    /// a guest who arrived by taxi stood on the plot with nothing loaded until they stepped out and back in.</summary>
    [HarmonyPatch(typeof(HamptonsHouse), "OnCityMapClosed")]
    public static class Patch_HamptonsHouse_OnCityMapClosed_GrantOpens
    {
        static void Prefix(HamptonsHouse __instance)
        {
            try { HamptonsTenancyFlip.Enter(__instance); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnCityMapClosed_GrantOpens", ex); }
        }
        static void Finalizer()
        {
            try { HamptonsTenancyFlip.Exit(); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnCityMapClosed_GrantOpens/fin", ex); }
        }
    }

    /// <summary>G1 + G3 — the PLOT ENTRY body. The Prefix SUSPENDS the tenancy flip so nothing
    /// EnterBuilding publishes (onEnterBuilding, 'ba:gameevent_enteredbuilding') sees a guest as the owner;
    /// the Postfix then runs the interior-sync work the Hamptons path never got, with the flag back at its
    /// real value — which matters, because InteriorSync.TrySendOwnerSnapshotOnEntry decides owner-vs-guest
    /// from exactly that flag (MergerFlip.TrulyMine) plus BuildingOwnedByPlayer.
    ///
    /// The native body is RE-ENTRANT: OnEnterPlot → EnterBuilding → EnterHamptonsBuilding → LoadBuilding →
    /// LoadBuildingInternal calls hamptonsHouse.OnEnterPlot() again (BuildingManager.cs:923); the inner call
    /// stops at its own `if (!BuildingManager.IsInsideBuilding)`. The depth counter keeps the entry hook to
    /// the OUTERMOST call, so the host receives exactly one interior request.</summary>
    [HarmonyPatch(typeof(HamptonsHouse), nameof(HamptonsHouse.OnEnterPlot))]
    public static class Patch_HamptonsHouse_OnEnterPlot_NarrowFlip
    {
        private static int _depth;

        static void Prefix()
        {
            try { _depth++; HamptonsTenancyFlip.Suspend(); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnEnterPlot_NarrowFlip", ex); }
        }
        static void Postfix(HamptonsHouse __instance)
        {
            try { if (_depth == 1) HamptonsAccess.OnPlotEntered(__instance); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnEnterPlot_NarrowFlip/post", ex); }
        }
        static void Finalizer()
        {
            try { HamptonsTenancyFlip.Resume(); if (_depth > 0) _depth--; }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnEnterPlot_NarrowFlip/fin", ex); }
        }
    }

    /// <summary>G1 — the PLOT EXIT body, same suspension: ExitFromHamptonsBuilding raises
    /// GlobalEvents.onExitBuilding (BuildingManager.cs:1611) from inside here. No Postfix is needed: that
    /// event is exactly what ExitBuildingNotifier (MPPatches.cs:941-974) already listens to, so the shop
    /// context clear, InteriorSync.NotifyLocalBuildingExit and SendPlayerExitedBuilding all fire on the
    /// Hamptons path today — the teardown half was never missing, only the entry half.</summary>
    [HarmonyPatch(typeof(HamptonsHouse), "OnExitPlot")]
    public static class Patch_HamptonsHouse_OnExitPlot_NarrowFlip
    {
        static void Prefix()
        {
            try { HamptonsTenancyFlip.Suspend(); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnExitPlot_NarrowFlip", ex); }
        }
        static void Finalizer()
        {
            try { HamptonsTenancyFlip.Resume(); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnExitPlot_NarrowFlip/fin", ex); }
        }
    }

    /// <summary>RENDERING (user ruling 2026-09-18): a Hamptons house that ANY session player rents shows its
    /// REAL renderers on EVERY machine; AI and unowned houses keep the native closed outdoorVersion shell.
    /// Postfix only, and deliberately NOT the tenancy flip — the flip would also open LateUpdate's plot check
    /// and let an ungranted player walk in (design doc, Traps).
    ///
    /// `active` = the city map is open: left native, as is ToggleOutsideImageMode (screenshots), which routes
    /// through EnableOnlyOutdoorVersion and never through here. _renderersEnabled is the controller's cached
    /// decision (:22): it is set to true alongside the renderers so the next native toggle is not a silent
    /// no-op, and when the ownership reason later lapses the native recompute finds true != false and closes
    /// the house again by itself. The house's FURNITURE needs nothing here — LoadHamptonsItems is untenanted
    /// and those objects are not in the renderers array.</summary>
    [HarmonyPatch(typeof(CityHamptonsHouseController), "ToggleLodMode")]
    public static class Patch_HamptonsLod_SessionTenantShowsHouse
    {
        static void Postfix(CityHamptonsHouseController __instance, bool active)
        {
            try
            {
                if (active) return;                                           // city map open — native
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return; // single player — native
                var reg = __instance.buildingRegistration;
                if (reg == null || reg.RentedByPlayer) return;                // native already shows it
                string addr = HamptonsAccess.KeyOf(reg);
                if (!HamptonsAccess.SessionTenantLabel(reg, addr, out var tenant)) return;   // AI / unowned — keep the shell
                var lodGo = __instance.lodVersion != null ? __instance.lodVersion.gameObject : null;
                var arr = __instance.renderers;
                if (arr != null)
                    foreach (var r in arr)
                        if (r != null && r.gameObject != lodGo) r.enabled = true;
                __instance._renderersEnabled = true;
                if (__instance.outdoorVersion != null) __instance.outdoorVersion.enabled = false;
                if (HamptonsAccess.RenderLogOnce(addr))
                    Plugin.Logger.LogInfo($"[Hamptons] real house shown at '{addr}' (rented by '{tenant}').");
            }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsLod_SessionTenantShowsHouse", ex); }
        }
    }

    /// <summary>Fence gate: a residence grant behind this fence unlocks it for the local player.</summary>
    [HarmonyPatch(typeof(HamptonsPrivateFenceDoor), "IsLocked")]
    public static class Patch_HamptonsFence_GrantUnlocks
    {
        static void Postfix(HamptonsPrivateFenceDoor __instance, ref bool __result)
        {
            try
            {
                if (!__result) return;                                        // natively unlocked — done
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return; // single player — native
                if (HamptonsAccess.GrantOpensFence(__instance.privateFenceIndex)) __result = false;
            }
            catch { }
        }
    }

    /// <summary>T1 (2026-09-18, part of MANOR-1; user hands-on: "it said i needed to own the house to enter,
    /// even when i had permissions"). TaxiSystem.CanEnterPrivateFence (decompile :132-146) is a THIRD copy of
    /// the Hamptons tenancy scan — RentedByPlayer/BuildingOwnedByPlayer on a registration that unlocks this
    /// fence — and TravelTo (:48-52) refuses the ride with 'hamptons_private_zone_locked_message' when it
    /// answers false. That is the same question the fence DOOR asks, so a residence grant behind the fence
    /// answers it here too (GrantOpensFence). Postfix only: a natively true result is never touched, and
    /// single player keeps the native answer. The method is private static, hence the typed lookup.</summary>
    [HarmonyPatch]
    public static class Patch_TaxiFence_GrantAllows
    {
        private static bool _logged;

        // TargetMethods (plural), never TargetMethod: a null from the singular form makes Harmony THROW, which counts
        // as a failed patch class (ten of those disable multiplayer entry). A method that moved binds nothing instead.
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(TaxiSystem), "CanEnterPrivateFence", new[] { typeof(int) });
            Plugin.Logger.LogInfo($"[Hamptons] TaxiSystem.CanEnterPrivateFence(int): {(m != null ? "patched" : "NOT FOUND")}");
            if (m != null) yield return m;
        }

        static void Postfix(int privateFenceIndex, ref bool __result)
        {
            try
            {
                if (__result) return;                                         // natively allowed — done
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return; // single player — native
                if (!HamptonsAccess.GrantOpensFence(privateFenceIndex)) return;
                __result = true;
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Logger.LogInfo($"[Hamptons] taxi into private fence {privateFenceIndex} allowed by a housing grant.");
                }
            }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_TaxiFence_GrantAllows", ex); }
        }
    }

    /// <summary>Plot barrier: a granted guest gets the same open plot the renter has — the native
    /// formula with the tenancy term satisfied (on-sale and service-blocked still close it).</summary>
    [HarmonyPatch(typeof(CityHamptonsHouseController), nameof(CityHamptonsHouseController.RefreshBlockerCollider))]
    public static class Patch_HamptonsBlocker_GrantOpens
    {
        static void Postfix(CityHamptonsHouseController __instance)
        {
            try
            {
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return;
                var reg = __instance.buildingRegistration;
                var col = __instance.blockerCollider;
                if (reg == null || col == null || !col.enabled) return;       // open already / nothing to do
                if (reg.RentedByPlayer) return;                               // native path owns this case
                string addrKey = HamptonsAccess.KeyOf(reg);
                bool granted = GrantSync.CanEnterGranted(addrKey);
                // G2 (fold r1): a guest whose grant lapsed while they were ON the plot must not be walled
                // in. The barrier stays open while the local player is still inside the plot volume, and
                // HamptonsAccess.TickClosePending closes it the frame they have stepped off.
                bool onPlot = HamptonsAccess.LocalIsInsidePlot(__instance.hamptonsHouse);
                if (!granted && !onPlot) return;
                bool inside = false;
                try { inside = BuildingManager.IsInsideBuilding && __instance.building == InstanceBehavior<BuildingManager>.Instance.building; } catch { }
                bool stillClosed = reg.IsOnSale()
                    || (BuildingManager.IsBuildingBlockedByAnyService(__instance.building.Address) && !inside);
                if (!stillClosed)
                {
                    col.enabled = false;
                    if (granted)
                        Plugin.Logger.LogInfo($"[Hamptons] plot barrier opened for granted guest at '{addrKey}'.");
                    else
                        Plugin.Logger.LogInfo($"[Hamptons] plot barrier at '{addrKey}' held open — access ended while the player is still on the plot.");
                }
            }
            catch { }
        }
    }

    /// <summary>H3 (2026-09-12) — the HAMPTONS EXIT ZONE. OwnsAHamptonsHouseExitCondition.CanExit
    /// (decompile :10-33) walks the LOCAL save for a Hamptons registration that is RentedByPlayer or
    /// BuildingOwnedByPlayer, so an invited guest who reached a friend's house was refused the way out
    /// of the zone — the same tenancy-only lock the fence and the plot blocker had. A residence grant
    /// for a Hamptons house is the session's statement that this player belongs behind that gate, so it
    /// answers here too. Postfix-only: a natively true result is never touched.</summary>
    [HarmonyPatch(typeof(OwnsAHamptonsHouseExitCondition), nameof(OwnsAHamptonsHouseExitCondition.CanExit))]
    public static class Patch_HamptonsExit_GrantOpens
    {
        private static bool _logged;

        static void Postfix(ref bool __result)
        {
            try
            {
                if (__result) return;                                         // natively allowed — done
                if (!MPServer.IsRunning && !MPClient.IsClientInWorld) return; // single player — native
                var regs = SaveGameManager.Current?.BuildingRegistrations;
                if (regs == null) return;
                foreach (var reg in regs)
                {
                    if (reg == null) continue;
                    var b = reg.BuildingCached;
                    if (b == null || !b.IsHamptonsHouse()) continue;
                    string key = GameStateReader.AddressKey(reg);
                    if (!GrantSync.CanEnterGranted(key)) continue;
                    __result = true;
                    if (!_logged)
                    {
                        _logged = true;
                        Plugin.Logger.LogInfo($"[Hamptons] exit gate opened by a residence grant ('{key}').");
                    }
                    return;
                }
            }
            catch { }
        }
    }
}
