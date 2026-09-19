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
//
// 2026-09-18 — STAGE D1, AMBIENT FURNITURE DELIVERY (design
// .modding/03-systems/hamptons-furniture-delivery-design-2026-09-18.md): the house now renders for
// everyone, but its FURNITURE only exists on a machine whose registration holds the item list. The
// game's own culling group already calls HamptonsHouse.OnLod0 (~60 m) / OnLod1 / OnLod2, and its
// untenanted loader instantiates whatever `itemInstances` holds at that moment — so the only missing
// piece is DELIVERY. A connected client postfixes those three hooks and takes an AMBIENT interior
// subscription (InteriorRequest{Ambient=true}) for a house a SESSION PLAYER rents, dropping it again
// when the house leaves LOD0 range. Access is unchanged: an ambient subscriber is not "present"
// anywhere, and the plot blocker, fence and entry gates above are untouched.
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
                    // D1: a house that was ALREADY at LOD0 when the ownership answer changed never
                    // gets another OnLod0 call — the loader is one-shot. This is the event that
                    // changed the answer, so the deferred ambient subscribe retries here (IsHouseLoaded
                    // is the native "this house is at LOD0" flag).
                    try { if (c.hamptonsHouse != null && c.hamptonsHouse.IsHouseLoaded) TryAmbientSubscribe(c.hamptonsHouse, "grant/ownership refresh", mayEvict: false); }
                    catch (Exception ex) { WarnOnce("RefreshAllBlockers/ambient", ex); }
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

        // ── D1: the AMBIENT interior subscription (client side) ──────────────────────────────────
        // Addresses this machine currently holds AMBIENTLY: a Hamptons house a session player rents,
        // inside the game's own LOD0 range, that we are standing OUTSIDE. Client-only — the HOST's
        // registration already holds every client-owned interior (InteriorSync.PublishAllOwnedInteriors
        // pushes them at world-live), so the host asks for nothing.
        private static readonly HashSet<string> _ambient = new HashSet<string>();
        // FOLD r1 R2a: insertion order, so this machine releases its OWN oldest before asking for a
        // ninth house — the host's cap (InteriorSync.MaxAmbientPerPeer, the one definition) then only
        // ever fires on a client bug, and never silently in normal play.
        private static readonly List<string> _ambientOrder = new List<string>();
        // Houses that were at LOD0 before the ownership ledger named their tenant: the predicate said
        // "no session player holds this" and nothing was sent. RefreshAllBlockers — which already fires
        // on every grant/ownership change — retries them, so the subscribe rides the event that changed
        // the answer rather than a timer of ours.  This set IS that state: one DEFERRED line per
        // address, cleared by the line that reports the retry landing (FOLD r1 R5c).
        private static readonly HashSet<string> _ambientDeferLogged = new HashSet<string>();
        private static readonly HashSet<string> _ambientGotLogged   = new HashSet<string>();
        // FOLD r1 R3: the host clears its side of every subscription when the link drops
        // (InteriorSync.HandlePeerDisconnected), so ours go too — and the re-evaluation waits for the
        // event that says this client is back in a synced world (GameStatePatcher's world-sync apply).
        private static bool _ambientResubscribePending;

        /// <summary>Every field above is keyed to ONE loaded world — registration objects, address keys and
        /// once-per-session log latches all die with the scene. Called from MPCanvasUI's game-load detector
        /// beside the other per-world resets.</summary>
        internal static void Reset()
        {
            try
            {
                _addrOf.Clear(); _entryLogged.Clear(); _renderLogged.Clear(); _warned.Clear();
                _isHamptonsAddr.Clear(); _closePending.Clear();
                _ambient.Clear(); _ambientOrder.Clear(); _ambientDeferLogged.Clear(); _ambientGotLogged.Clear();
                _ambientResubscribePending = false;
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

        // ── D1: ambient subscribe / unsubscribe / measurement ────────────────────────────────────

        /// <summary>LOD0 on a Hamptons house (or the grant/ownership refresh finding one already at
        /// LOD0): ask the host for its interior when a SESSION PLAYER rents it and this machine is a
        /// connected client that does not hold the house itself. One request per address — the local
        /// set is what the LOD1/LOD2 teardown, the plot-exit re-assert and the dev lever read.
        /// The predicate is the render postfix's, minus the render-only terms.</summary>
        internal static void TryAmbientSubscribe(HamptonsHouse? house, string why, bool mayEvict = true)
        {
            try
            {
                if (!MPClient.IsConnected || MPServer.IsRunning) return;   // the host serves itself; SP does nothing
                var reg = house != null ? house!._buildingRegistration : null;
                if (reg == null) return;
                bool mine = false;
                try { mine = reg.RentedByPlayer || reg.BuildingOwnedByPlayer; } catch { }
                if (mine) return;                                          // our own house — its items are already here
                // FOLD r1 R2b: never ambient-subscribe the plot we are STANDING ON. That house belongs to
                // the entry path (OnPlotEntered's request, which the host promotes); an ambient request
                // for it is at best ignored by the host and at worst confuses the two kinds. The plot-exit
                // re-assert is the way back in.
                if (LocalIsInsidePlot(house)) return;
                string addr = KeyOf(reg);
                if (addr.Length == 0 || _ambient.Contains(addr)) return;
                if (!SessionTenantLabel(reg, addr, out var tenant))
                {
                    // Ownership has not been applied on this machine yet. Not an error and not a
                    // failure: the retry rides RefreshAllBlockers, the same event that carries the
                    // grant/ownership change which will make this predicate true.
                    if (_ambientDeferLogged.Add(addr))
                        Plugin.Logger.LogInfo($"[Hamptons] ambient subscribe DEFERRED at '{addr}' ({why}) — no session player holds it here yet; retried on the next grant/ownership refresh.");
                    return;
                }
                // FOLD r1 R2a: this machine's own cap, mirroring the host's ONE constant. Release our
                // oldest first so the host's eviction never fires in normal play.
                // FOLD r2 (re-check HIGH): only a FRESH approach may make room. A bulk sweep walks every loaded
                // house; letting it evict made it release a house it re-subscribed moments later - N requests
                // and N full snapshots per grant/ownership event with more than the cap loaded, every time.
                // A sweep fills free seats only.
                if (!mayEvict && _ambientOrder.Count >= InteriorSync.MaxAmbientPerPeer) return;
                while (_ambientOrder.Count >= InteriorSync.MaxAmbientPerPeer)
                {
                    string oldest = _ambientOrder[0];
                    ReleaseAmbient(oldest, $"local cap {InteriorSync.MaxAmbientPerPeer} — making room for '{addr}'");
                    if (_ambientOrder.Count > 0 && _ambientOrder[0] == oldest) { _ambientOrder.RemoveAt(0); break; }   // never spin
                }
                MPClient.SendInteriorRequest(addr, ambient: true);
                _ambient.Add(addr);
                _ambientOrder.Add(addr);
                bool wasDeferred = _ambientDeferLogged.Remove(addr);
                Plugin.Logger.LogInfo($"[Hamptons] ambient interior subscribed at '{addr}' ({why}; rented by '{tenant}', {_ambient.Count} ambient address(es) held)" + (wasDeferred ? " — the deferred subscribe at this address is resolved." : "."));
            }
            catch (Exception ex) { WarnOnce("TryAmbientSubscribe", ex); }
        }

        /// <summary>LOD1 / LOD2: the house left the range at which the game keeps its furniture loaded,
        /// so the ambient subscription goes back. Silent when we never held one.</summary>
        internal static void AmbientUnsubscribe(HamptonsHouse? house, string why)
        {
            try
            {
                var reg = house != null ? house!._buildingRegistration : null;
                if (reg == null) return;
                string addr = KeyOf(reg);
                if (addr.Length == 0) return;
                _ambientDeferLogged.Remove(addr);                 // the house is gone; a pending DEFERRED line is moot
                if (!_ambient.Contains(addr)) { _ambientOrder.Remove(addr); return; }
                ReleaseAmbient(addr, why);
            }
            catch (Exception ex) { WarnOnce("AmbientUnsubscribe", ex); }
        }

        /// <summary>Give one ambient subscription back: local records first, then the host.</summary>
        private static void ReleaseAmbient(string addr, string why)
        {
            _ambient.Remove(addr);
            _ambientOrder.Remove(addr);
            if (MPClient.IsConnected) MPClient.SendPlayerExitedBuilding(addr, ambient: true);
            Plugin.Logger.LogInfo($"[Hamptons] ambient interior released at '{addr}' ({why}; {_ambient.Count} ambient address(es) left).");
        }

        /// <summary>FOLD r1 R3: the link to the host dropped WITHOUT a world reload (MPClient.OnDisconnected —
        /// a reconnect into the same scene is a real path, MPClient.cs ~:249-256). The host already dropped
        /// every subscription this peer held (InteriorSync.HandlePeerDisconnected), so believing in ours would
        /// leave houses that never update and an exit we would send to nobody. Cleared here; re-taken on the
        /// world-sync event below.  Main thread (marshalled by the caller).</summary>
        internal static void OnHostLinkLost()
        {
            try
            {
                int n = _ambient.Count;
                _ambient.Clear(); _ambientOrder.Clear(); _ambientDeferLogged.Clear(); _ambientGotLogged.Clear();
                _ambientResubscribePending = true;
                if (n > 0)
                    Plugin.Logger.LogInfo($"[Hamptons] host link lost — {n} ambient subscription(s) dropped locally (the host cleared its side); they are re-taken when this client's world sync applies again.");
            }
            catch (Exception ex) { WarnOnce("OnHostLinkLost", ex); }
        }

        /// <summary>FOLD r1 R3: the client has applied the bulk world sync again (GameStatePatcher's
        /// business-snapshot apply sets MPClient.WorldSyncApplied — the same signal the world-ready gate
        /// uses, an event, not a delay). Every Hamptons house already at LOD0 is re-evaluated exactly as the
        /// RefreshAllBlockers retry does, because their one-shot LOD0 callback has long since fired.</summary>
        internal static void OnWorldSyncApplied()
        {
            try
            {
                if (!_ambientResubscribePending) return;
                _ambientResubscribePending = false;
                if (!MPClient.IsConnected || MPServer.IsRunning) return;
                var all = UnityEngine.Object.FindObjectsOfType<CityHamptonsHouseController>(true);
                if (all == null) return;
                int n = 0;
                foreach (var c in all)
                {
                    if (c == null || c.buildingRegistration == null) continue;
                    var hh = c.hamptonsHouse;
                    if (hh == null || !hh.IsHouseLoaded) continue;
                    TryAmbientSubscribe(hh, "reconnect — world sync applied", mayEvict: false);
                    n++;
                }
                Plugin.Logger.LogInfo($"[Hamptons] reconnect: {n} loaded Hamptons house(s) re-evaluated for ambient delivery ({_ambient.Count} held).");
            }
            catch (Exception ex) { WarnOnce("OnWorldSyncApplied", ex); }
        }

        /// <summary>A granted guest who walks onto the plot sends an ENTRY request (OnPlotEntered) and
        /// the host PROMOTES the ambient membership into it — one seat, now owned by the entry sub. The
        /// plot-exit teardown (ExitBuildingNotifier → SendPlayerExitedBuilding) then retires that seat
        /// while the house is still at LOD0 in front of the player, so the ambient subscription is
        /// re-asserted here, after the exit, and the furniture stays.</summary>
        internal static void ReassertAmbientAfterPlotExit(HamptonsHouse? house)
        {
            try
            {
                if (!MPClient.IsConnected || MPServer.IsRunning) return;
                if (house == null || !house!.IsHouseLoaded) return;   // below LOD0 — the LOD1/LOD2 hook owns this
                var reg = house!._buildingRegistration;
                if (reg == null) return;
                string addr = KeyOf(reg);
                if (addr.Length == 0) return;
                _ambient.Remove(addr); _ambientOrder.Remove(addr);    // the host promoted it away; ask again
                TryAmbientSubscribe(house, "plot exit — the house is still at LOD0");
            }
            catch (Exception ex) { WarnOnce("ReassertAmbientAfterPlotExit", ex); }
        }

        /// <summary>D1 measurement: a snapshot arriving for an address held AMBIENTLY is furniture for a
        /// house we are standing OUTSIDE. One line per address per session; the byte count is the frame
        /// as it arrived on the wire (deflated), the first real number against the design's 80-110 KB
        /// estimate.</summary>
        internal static void NoteAmbientSnapshot(string addressKey, int items, int wireBytes)
        {
            try
            {
                if (string.IsNullOrEmpty(addressKey) || !_ambient.Contains(addressKey)) return;
                if (!_ambientGotLogged.Add(addressKey)) return;
                Plugin.Logger.LogInfo($"[Hamptons] '{addressKey}' furniture received while outside ({items} items, {wireBytes} B on the wire).");
            }
            catch { }
        }

        /// <summary>DEV lever ('ambient'): the addresses this machine holds ambiently.</summary>
        internal static string AmbientLocalSummary()
        {
            try { return string.Join("|", _ambientOrder.ToArray()); }   // oldest first: the release order
            catch { return ""; }
        }

        /// <summary>DEV lever ('hamptonslod &lt;addressKey&gt; &lt;0|1|2&gt;'): drive one named house's own
        /// LOD callback so the rig can test the delivery trigger without walking. The house comes from the
        /// game's own controller registry (CityManager.FindCityBuildingController), never a scene sweep.</summary>
        internal static string DevDriveLod(string addressKey, int level)
        {
            try
            {
                var reg = GameStatePatcher.FindRegistration(addressKey);
                if (reg == null) return $"ERR no registration for '{addressKey}'";
                if (reg.BuildingCached == null || !reg.BuildingCached.IsHamptonsHouse()) return $"ERR '{addressKey}' is not a Hamptons house";
                var cc = InstanceBehavior<CityManager>.Instance?.FindCityBuildingController(reg.BuildingCached.Address) as CityHamptonsHouseController;
                var hh = cc?.hamptonsHouse;
                if (hh == null) return $"ERR no HamptonsHouse for '{addressKey}' in the city controller registry";
                if (level == 0) hh.OnLod0();
                else if (level == 1) hh.OnLod1();
                else hh.OnLod2();
                int regItems = 0; try { regItems = reg.itemInstances?.Count ?? 0; } catch { }
                int live = 0;     try { live = hh.allItemControllers?.Count ?? 0; } catch { }
                return $"OK hamptonslod '{addressKey}' OnLod{level}: loaded={hh.IsHouseLoaded} regItems={regItems} liveItems={live} ambient={_ambient.Contains(addressKey)}";
            }
            catch (Exception ex) { return $"ERR hamptonslod '{addressKey}': {ex.GetType().Name}: {ex.Message}"; }
        }

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
    /// GlobalEvents.onExitBuilding (BuildingManager.cs:1611) from inside here. That event is exactly what
    /// ExitBuildingNotifier (MPPatches.cs:941-974) already listens to, so the shop context clear,
    /// InteriorSync.NotifyLocalBuildingExit and SendPlayerExitedBuilding all fire on the Hamptons path
    /// today — the teardown half was never missing, only the entry half.
    ///
    /// D1 adds a Postfix, because that same teardown now retires ONE MEMBERSHIP TOO MANY: the host
    /// promoted this house's ambient subscription into the entry subscription when the guest walked in,
    /// so the exit leaves a house that is still at LOD0 in front of the player with no subscription at
    /// all. The Postfix re-asserts the ambient one, after the exit message has gone.</summary>
    [HarmonyPatch(typeof(HamptonsHouse), "OnExitPlot")]
    public static class Patch_HamptonsHouse_OnExitPlot_NarrowFlip
    {
        static void Prefix()
        {
            try { HamptonsTenancyFlip.Suspend(); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnExitPlot_NarrowFlip", ex); }
        }
        static void Postfix(HamptonsHouse __instance)
        {
            try { HamptonsAccess.ReassertAmbientAfterPlotExit(__instance); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnExitPlot_NarrowFlip/post", ex); }
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

    /// <summary>D1 (2026-09-18) — THE AMBIENT DELIVERY TRIGGER. The game's own culling group calls these
    /// three ICullable hooks (decompile HamptonsHouse.cs :334-347): OnLod0 when the house comes within
    /// ~60 m and its untenanted loader instantiates `itemInstances`, OnLod1 / OnLod2 when it leaves and
    /// they are cleared. They ARE the event — the mod polls no distances of its own. OnLod0 takes an
    /// ambient interior subscription for a house a session player rents; OnLod1 / OnLod2 give it back.
    /// One patch class per method so each reports its own binding; TargetMethods (plural), never
    /// TargetMethod, because a null from the singular form makes Harmony THROW.</summary>
    [HarmonyPatch]
    public static class Patch_HamptonsHouse_OnLod0_AmbientSubscribe
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(HamptonsHouse), "OnLod0", Type.EmptyTypes);
            Plugin.Logger.LogInfo($"[Hamptons] HamptonsHouse.OnLod0(): {(m != null ? "patched" : "NOT FOUND")}");
            if (m != null) yield return m;
        }

        static void Postfix(HamptonsHouse __instance)
        {
            try { HamptonsAccess.TryAmbientSubscribe(__instance, "LOD0"); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnLod0_AmbientSubscribe", ex); }
        }
    }

    /// <summary>D1: the house dropped to LOD1 — its items are unloaded, so the ambient subscription has
    /// nothing left to feed and goes back.</summary>
    [HarmonyPatch]
    public static class Patch_HamptonsHouse_OnLod1_AmbientRelease
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(HamptonsHouse), "OnLod1", Type.EmptyTypes);
            Plugin.Logger.LogInfo($"[Hamptons] HamptonsHouse.OnLod1(): {(m != null ? "patched" : "NOT FOUND")}");
            if (m != null) yield return m;
        }

        static void Postfix(HamptonsHouse __instance)
        {
            try { HamptonsAccess.AmbientUnsubscribe(__instance, "LOD1"); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnLod1_AmbientRelease", ex); }
        }
    }

    /// <summary>D1: same as LOD1, at the outer culling band.</summary>
    [HarmonyPatch]
    public static class Patch_HamptonsHouse_OnLod2_AmbientRelease
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var m = AccessTools.Method(typeof(HamptonsHouse), "OnLod2", Type.EmptyTypes);
            Plugin.Logger.LogInfo($"[Hamptons] HamptonsHouse.OnLod2(): {(m != null ? "patched" : "NOT FOUND")}");
            if (m != null) yield return m;
        }

        static void Postfix(HamptonsHouse __instance)
        {
            try { HamptonsAccess.AmbientUnsubscribe(__instance, "LOD2"); }
            catch (Exception ex) { HamptonsAccess.WarnOnce("Patch_HamptonsHouse_OnLod2_AmbientRelease", ex); }
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
