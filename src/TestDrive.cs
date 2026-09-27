#if BAMP_DEV
using System;
using System.IO;
using System.Text;

namespace BigAmbitionsMP
{
    /// <summary>Round-239 — TEST DRIVE: a dev-build-only file-drop command channel so an
    /// agent (or a human in a terminal) can run rig tests without touching the game's UI.
    ///
    /// PROTOCOL — the channel is ARMED by creating the folder
    ///   &lt;LocalLow&gt;\Hovgaard Games\Big Ambitions\BigAmbitionsMP\testdrive\
    /// (all rig instances share that LocalLow, so commands are ROLE-ADDRESSED by filename:
    /// "h-*.cmd" runs on the HOST instance — the Steam install — while "c-*.cmd" / "d-*.cmd" run on
    /// the client installs C:\BigAmbitions2 / C:\BigAmbitions3).  A .cmd file holds one command line; the mod
    /// polls every 0.5s on the main thread, executes, writes "&lt;file&gt;.result" ("OK ..." /
    /// "ERR ..."), deletes the .cmd, and logs a [TestDrive] line.  See
    /// .modding/08-testdrive.md for the verb reference and per-test scripts.
    ///
    /// SAFETY: the entire class is compiled out of Release/Debug (BAMP_DEV only), and even
    /// on Dev builds it is inert until someone creates the folder.  Verbs are thin wrappers
    /// over existing entry points — the driver adds no new game-state write paths.</summary>
    internal static class TestDrive
    {
        private static float _nextPoll;
        private static string? _dir;
        private static bool _armedLogged;

        /// <summary>Instance role by install location: the Steam install is the HOST ("h"); an install
        /// folder named BigAmbitions&lt;N&gt; (N &gt;= 2) is a CLIENT with the letter (char)('c' + N - 2) —
        /// BigAmbitions2 = "c", BigAmbitions3 = "d", BigAmbitions4 = "e". Anything unparsable = "h".</summary>
        private static string? _role;
        private static string Role => _role ?? (_role = ComputeRole());

        private static string ComputeRole()
        {
            try
            {
                string name = Path.GetFileName(MPConfig.GameRootPath.TrimEnd('\\', '/'));
                int i = name.Length;
                while (i > 0 && name[i - 1] >= '0' && name[i - 1] <= '9') i--;
                if (i > 0 && i < name.Length &&
                    string.Equals(name.Substring(0, i), "BigAmbitions", StringComparison.OrdinalIgnoreCase))
                {
                    int n;
                    if (int.TryParse(name.Substring(i), out n) && n >= 2 && n <= 9)
                        return ((char)('c' + n - 2)).ToString();
                }
            }
            catch { }
            return "h";
        }

        /// <summary>"blocksave" verb state — MPSaveCoordinator.SaveBlockedBy honors it (dev builds)
        /// so the round-237 deferral machinery can be exercised end-to-end (defer → heartbeat →
        /// resume → upload) without a human sitting in the Interior Designer.  The NATIVE gate
        /// itself is code-verified (SaveGameManager.CanSave :490); this simulates only the state.</summary>
        internal static float SimulateSaveBlockUntil;

        /// <summary>Round-256 "rivalrace" verb state — while true, MPClient stashes an arriving
        /// RivalsSnapshot instead of applying it, so a new-game start reproduces the field's
        /// lost id race (world generates rival-less); 'release' applies the stashed payload
        /// late, exercising the repair path. Harmless on the host (handler is client-only).</summary>
        internal static bool HoldRivalsSnapshot;
        internal static RivalsSnapshotPayload? HeldRivalsSnapshot;

        /// <summary>EFFORT BATCH 28 "startfail" verb state — while true, the NEXT host start
        /// main-thread continuation throws once, exercising the lobby hand-back: a new game throws
        /// where SaveGameManager.New ran (MPServer), a lobby save load throws where the host's own
        /// load ran (MPSaveCoordinator.HostLoadSession, EFFORT BATCH 29) - both BEFORE any client is told.</summary>
        internal static bool ForceStartFailOnce;

        /// <summary>Round-260 "rentdeny" verb state — while true, the host denies every
        /// RentRequest with a TestDrive reason, letting an agent exercise the client's
        /// optimistic-rent rollback (the starter-item leak) without needing a genuinely
        /// occupied building.</summary>
        internal static bool ForceRentDeny;

        /// <summary>H-REFUSALMUTE-1 "rejectjoin" verb state - while true, the host refuses the NEXT join request
        /// parked for approval (a mid-game Hello) through the approval popup's own Reject path
        /// (MPServer.RejectPendingJoin: 'BAMP:rejected', and that player is banned until re-host), then the lever
        /// turns itself off - and it is cleared when the host stops. A lobby join is accepted at once and never
        /// parks, so it is not refused.</summary>
        internal static bool RejectNextJoin;

        private static void TickRejectJoin()
        {
            try
            {
                // Review L5: the lever dies with the server - a later re-host starts with it off.
                if (!MPServer.IsRunning) { RejectNextJoin = false; Plugin.Logger.LogInfo("[TestDrive] rejectjoin: the host stopped - lever off."); return; }
                var pending = MPServer.PendingJoinList;
                if (pending == null || pending.Count == 0) return;
                var (peerId, pid) = pending[0];
                RejectNextJoin = false;
                MPServer.RejectPendingJoin(peerId);
                Plugin.Logger.LogWarning($"[TestDrive] rejectjoin: refused the join request from '{pid}' with 'BAMP:rejected' (the approval popup's Reject path - banned until re-host); lever off.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TestDrive] rejectjoin: {ex.Message}"); }
        }

        /// <summary>"charconfirm" verb state — the autopilot that used to click past the
        /// character customizer was deleted with 4bc256a (T256 agent run hit the wall);
        /// this re-creates ONLY that piece: while armed, the tick watches for
        /// IntroCharacterCustomizer and reflect-invokes StartGame() after the deleted
        /// autopilot's proven 2s settle (invoking earlier leaves the customizer UI broken).</summary>
        internal static bool ConfirmCustomizerArmed;
        private static float _customizerFirstSeen = -1f;

        internal static void Tick()
        {
            if (UnityEngine.Time.unscaledTime < _nextPoll) return;
            _nextPoll = UnityEngine.Time.unscaledTime + 0.5f;
            try
            {
                _dir ??= Path.Combine(MPConfig.DataRootPath, "testdrive");
                if (!Directory.Exists(_dir)) return;   // channel not armed — fully inert
                if (ConfirmCustomizerArmed) TickCustomizerConfirm();
                if (RejectNextJoin) TickRejectJoin();   // H-REFUSALMUTE-1: re-checks the pending joins every poll until one is refused
                if (_workArmed) TickWork();   // H-WORKFF-1 part 2: 'work on' follow-through (DEV lever)
                if (!_armedLogged)
                {
                    _armedLogged = true;
                    Plugin.Logger.LogWarning($"[TestDrive] channel ARMED (dev build, role '{Role}') — watching {_dir} for {Role}-*.cmd");
                }
                foreach (var f in Directory.GetFiles(_dir, Role + "-*.cmd"))
                {
                    string text;
                    try { text = File.ReadAllText(f).Trim(); }
                    catch { continue; }   // mid-write by the sender — next poll gets it
                    string result;
                    try { result = Execute(text); }
                    catch (Exception ex) { result = "ERR " + ex.Message; }
                    try { File.WriteAllText(f + ".result", result); } catch { }
                    try { File.Delete(f); } catch { }
                    Plugin.Logger.LogWarning($"[TestDrive] {Role} '{text}' → {result}");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[TestDrive] tick: {ex.Message}"); }
        }

        private static string Execute(string line)
        {
            if (string.IsNullOrEmpty(line)) return "ERR empty command";
            int sp = line.IndexOf(' ');
            string verb = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
            string arg  = sp < 0 ? "" : line.Substring(sp + 1).Trim();   // may contain spaces (sessions, addresses)

            switch (verb)
            {
                // ── observation ───────────────────────────────────────────────
                case "mark":
                    // Phase bracket for the log reader — no game effect.
                    return "OK MARK " + arg;

                case "status":
                {
                    var sb = new StringBuilder("OK ");
                    sb.Append($"role={Role} server={MPServer.IsRunning} clientConn={MPClient.IsConnected} inMp={MPClient.InMpGame} ");
                    try { sb.Append($"settled={MPWorldReady.IsSettled} "); } catch { }
                    try { sb.Append($"session='{MPSaveCoordinator.ActiveSessionName}' "); } catch { }
                    try { var t = GameStateReader.GetGameTime(); sb.Append($"day={t.day} hour={t.hourOfDay:0.0} "); } catch { }
                    try { sb.Append($"ledger={MPServer.BuildingOwners.Count} "); } catch { }
                    // Weather round (2026-08-18): local isRaining / last host verdict /
                    // drops visually falling — lets the rig verify the VISUAL layer,
                    // which log lines alone cannot see (-1 = unknown).
                    try { sb.Append($"rain={MPWeatherSync.CurrentRainState()}/{MPWeatherSync.HostRainState} drops={MPWeatherSync.DropsFalling()} "); } catch { }
                    // Round-240: clientConn above is THIS instance's own outbound link, not a peer
                    // count — agents misread it. pendingJoins>0 means 'acceptjoin' is needed.
                    try { if (MPServer.IsRunning) sb.Append($"pendingJoins={MPServer.PendingJoinList.Count}"); } catch { }
                    return sb.ToString();
                }

                case "traffic":
                {
                    // TRAFFIC-SMOOTH S5 (2026-09-12): one line for the traffic stream's health.
                    // TRAFFIC-APART P9 (2026-09-12): plus WHICH traffic this machine runs (mode; "host" on the host),
                    // how many ambient cars of its own are alive, and whether a handover is still fading.
                    // TRAFFIC-CONSIST T4 (2026-09-18): plus the four numbers of the consistency work - how many
                    // leftover cars THIS client published on its last beat, how many FOREIGN cars (another
                    // player's leftovers, relayed under reserved ids) this machine holds, how many player-vehicle
                    // ghosts carry the sense proxy, and how many stand-ins a host keeps for published cars.
                    int ghosts = 0, hostcars = 0, localcars = 0;
                    int published = 0, foreign = 0, pvsensed = 0, standins = 0;
                    try { ghosts = TrafficSync.ClientTrafficGhostCount; } catch { }
                    try { if (MPServer.IsRunning) hostcars = TrafficSync.HostTrafficCount(); } catch { }
                    try { localcars = TrafficSync.LocalAmbientCount(); } catch { }
                    try { published = TrafficSync.PublishedLeftoverCount; } catch { }
                    try { foreign = TrafficSync.ForeignGhostCount; } catch { }
                    try { pvsensed = VehicleManager.SensedGhostCount(); } catch { }
                    try { standins = TrafficSync.HostStandInCount; } catch { }
                    string tmode = MPServer.IsRunning ? "host" : TrafficSync.ClientTrafficMode;
                    return $"OK traffic role={Role} ghosts={ghosts} hostcars={hostcars} " +
                           $"seq={TrafficSync.LastTrafficSeq} dropped={TrafficSync.StaleSnapshotsDropped} " +
                           $"lane={TrafficSync.TrafficLane} mode={tmode} local={localcars} " +
                           $"handover={TrafficSync.ClientHandover} " +
                           $"published={published} foreign={foreign} pvsensed={pvsensed} standins={standins}";
                }

                case "ghostjitter":
                {
                    // TRAFFIC-SMOOTH S5 (redefined fold c): the position CORRECTION each arriving packet
                    // implies at the current render time — the jerk the player sees — over a rolling 10 s
                    // window, for ghosts within 60 m. Client-side; the window exists only in a dev build.
                    if (string.Equals(arg, "reset", StringComparison.OrdinalIgnoreCase))
                    { TrafficSync.GhostJitterReset(); return "OK ghostjitter reset"; }
                    // Review MINOR-6: the BUILD test comes first — on a release build there is no window at
                    // all, so "no ghosts" would be a misleading answer even when ghosts are absent too.
                    if (!TrafficSync.GhostJitterStats(out int jn, out float jmean, out float jmax))
                        return "ERR ghostjitter needs a dev build";
                    if (TrafficSync.ClientTrafficGhostCount == 0) return "ERR no ghosts";
                    return $"OK ghostjitter window=10s samples={jn} mean={jmean:F3} max={jmax:F3}";
                }

                case "ledgerdump":
                {
                    if (!MPServer.IsRunning) return "ERR host only";
                    int n = 0;
                    foreach (var kv in MPServer.BuildingOwners)
                    { Plugin.Logger.LogWarning($"[TestDrive] ledger: '{kv.Key}' → '{kv.Value}'"); n++; }
                    return $"OK {n} ledger entr(ies) logged";
                }

                // ── session control ───────────────────────────────────────────
                case "host":
                    if (MPServer.IsRunning) return "OK server already running";
                    return MPServer.Start(int.TryParse(arg, out var port) ? port : 7777)
                        ? "OK server started" : "ERR MPServer.Start returned false";

                case "hostnew":
                    // Round-256: start a NEW game from the lobby — same entry the UI's
                    // Start button calls (StartNewNow → MPServer.StartNewGame). Needed
                    // for the rivalrace test: the lost id race only exists on a client
                    // present during a new-game start.
                    if (!MPServer.IsRunning) return "ERR start the server first ('host')";
                    MPServer.StartNewGame(new GameVariablesDto());
                    return "OK StartNewGame invoked (default settings)";

                case "startfail":
                    // EFFORT BATCH 28 (B) test lever, host-side: 'arm' -> the next StartNewGame
                    // continuation throws once; bare -> readout of the lever + the lobby latch.
                    if (arg == "arm") { ForceStartFailOnce = true;  return "OK the next start continuation (new game or host load) throws once (host-side)"; }
                    if (arg == "off") { ForceStartFailOnce = false; return "OK startfail disarmed"; }
                    // EFFORT BATCH 29: clientInLobby = this machine's CLIENT lobby latch (true until a start is acted on).
                    return $"OK startfail armed={ForceStartFailOnce} running={MPServer.IsRunning} inLobby={MPServer.IsInLobby} clientInLobby={MPClient.IsInLobby}";

                case "rivalids":
                {
                    // EFFORT BATCH 28 (A) readout: the ids native GenerateRivals minted into THIS
                    // machine's world (wholesale 7 + import 8) - on a client these come from the
                    // host's feed, so host and client must print the same list. Read-only.
                    var rgi = SaveGameManager.Current;
                    if (rgi == null) return "ERR no world loaded";
                    var rw = rgi.wholesaleRivalIds; var rim = rgi.importRivalIds;
                    int rn = (rw?.Length ?? 0) + (rim?.Length ?? 0);
                    return $"OK rivalids n={rn} w=[{(rw == null ? "" : string.Join(",", rw))}] i=[{(rim == null ? "" : string.Join(",", rim))}]";
                }

                case "rentdeny":
                    // Round-260 test switch: host-side. 'arm' → every RentRequest denied
                    // (exercises the client rollback); 'off' → normal arbitration.
                    if (arg == "arm") { ForceRentDeny = true;  return "OK every RentRequest will be DENIED (host-side)"; }
                    if (arg == "off") { ForceRentDeny = false; return "OK rent arbitration back to normal"; }
                    return "ERR rentdeny arm|off";

                case "rent":
                {
                    // Round-260 test driver: rent a building via the SAME native entry the
                    // for-rent sign flow calls (BuildingHelper.RentBuilding — the choke point
                    // Patch_RentBuilding hooks, so the MP request fires exactly like a UI
                    // rent). No money is charged (the charge lives in the UI layer) — fine
                    // for rollback tests. Arg = address key, e.g. 'rent 12 ba:street_x'.
                    if (arg.Length == 0) return "ERR address key required";
                    var rreg = GameStatePatcher.FindRegistration(arg);
                    if (rreg == null) return $"ERR no registration for '{arg}'";
                    bool free = false; try { free = rreg.AvailableForRent && !rreg.RentedByPlayer; } catch { }
                    if (!free) return $"ERR '{arg}' is not on the for-rent market on this machine";
                    var bld = Helpers.BuildingHelper.GetBuilding(rreg.Address);
                    if (bld == null) return $"ERR no Building for '{arg}'";
                    float rent = 0f; try { rent = bld.GetBuildingDailyMarketRent(); } catch { }
                    if (rent <= 0f) rent = 100f;
                    Helpers.BuildingHelper.RentBuilding(bld, rent, rent * 90f);
                    int spots = 0, spawners = 0;
                    try
                    {
                        foreach (var kv in rreg.itemInstances)
                        {
                            string n = kv.Value?.itemName?.ToString() ?? "";
                            if (n == "ba:itemname_deliveryspot") spots++;
                            else if (n == "ba:itemname_handtruckspawner") spawners++;
                        }
                    }
                    catch { }
                    return $"OK RentBuilding invoked for '{arg}' (rent {rent:F0}); instances now: deliveryspot={spots} handtruckspawner={spawners}";
                }

                case "itemcount":
                {
                    // Round-260 assertion helper: counts the two starter items on a reg.
                    if (arg.Length == 0) return "ERR address key required";
                    var creg = GameStatePatcher.FindRegistration(arg);
                    if (creg == null) return $"ERR no registration for '{arg}'";
                    int cspots = 0, cspawners = 0;
                    try
                    {
                        foreach (var kv in creg.itemInstances)
                        {
                            string n = kv.Value?.itemName?.ToString() ?? "";
                            if (n == "ba:itemname_deliveryspot") cspots++;
                            else if (n == "ba:itemname_handtruckspawner") cspawners++;
                        }
                    }
                    catch { }
                    bool availv = false, rentedv = false; string bn = "", bt = "";
                    try { availv = creg.AvailableForRent; rentedv = creg.RentedByPlayer; bn = creg.BusinessName?.ToString() ?? ""; bt = creg.businessTypeName?.ToString() ?? ""; } catch { }
                    return $"OK '{arg}': deliveryspot={cspots} handtruckspawner={cspawners} avail={availv} rented={rentedv} name='{bn}' type='{bt}'";
                }

                case "ambient":
                {
                    // D1 (2026-09-18): the ambient interior subscription. On a CLIENT, the addresses
                    // this machine holds; on the HOST, what it serves, per peer.
                    if (MPServer.IsRunning)
                    {
                        string apeers = InteriorSync.AmbientHostSummary(out int atotal);
                        return $"OK ambient peers=[{apeers}] total={atotal}";
                    }
                    return $"OK ambient local=[{HamptonsAccess.AmbientLocalSummary()}]";
                }

                case "hamptonslod":
                {
                    // 'hamptonslod <addressKey> <0|1|2>' — invoke that house's own OnLod0/OnLod1/OnLod2
                    // so the delivery trigger can be driven without walking to it. The address key may
                    // contain spaces, so the LEVEL is the last token.
                    int lsp = arg.LastIndexOf(' ');
                    if (lsp <= 0) return "ERR hamptonslod <addressKey> <0|1|2>";
                    string laddr = arg.Substring(0, lsp).Trim();
                    if (!int.TryParse(arg.Substring(lsp + 1).Trim(), out int llvl) || llvl < 0 || llvl > 2)
                        return "ERR hamptonslod <addressKey> <0|1|2>";
                    if (laddr.Length == 0) return "ERR hamptonslod <addressKey> <0|1|2>";
                    return HamptonsAccess.DevDriveLod(laddr, llvl);
                }

                case "warnicons":
                {
                    // I-lever (2026-09-12): recompute GetWarningIconType over BuildingManager.allItemControllers
                    // under the SAME scoped flip and licensing filter Patch_WarningIcons_VisitorParity applies.
                    // The manager's _activeIcons dictionary is private, so this re-derives the set rather than
                    // reading it — which also means it answers "what SHOULD be showing" for the local player.
                    var bmw = InstanceBehavior<BuildingManager>.Instance;
                    if (bmw?.allItemControllers == null) return "ERR not inside a building (no item controllers)";
                    bool wHelper = false; try { wHelper = BusinessHelperRoute.HelperHere(out _); } catch { }
                    var wsb = new StringBuilder(); int wn = 0;
                    if (wHelper) HousingFurniture.Enter(includeHelper: true);
                    try
                    {
                        foreach (var wic in bmw.allItemControllers)
                        {
                            if (wic == null) continue;
                            var wt = Player.HUD.ItemWarningIcons.WarningIconType.None;
                            try { wt = wic.GetWarningIconType(); } catch { continue; }
                            if (wt == Player.HUD.ItemWarningIcons.WarningIconType.None) continue;
                            if (wHelper && !wic.CanInteractInAnyBusiness
                                && wt == Player.HUD.ItemWarningIcons.WarningIconType.Danger)
                            {
                                bool wLic = false;
                                try
                                {
                                    var wMiss = ItemHelper.GetMissingRequirements(wic.ItemInstance);
                                    if (wMiss != null)
                                        foreach (var wr in wMiss)
                                            if (wr is Furniture.Requirements.HasActiveLicensingFee) { wLic = true; break; }
                                }
                                catch { }
                                if (wLic) continue;   // withheld by the parity prefix — the owner's fee schedule
                            }
                            wn++;
                            if (wn <= 12) wsb.Append($"{(wn > 1 ? "," : "")}{wt}:{wic.ItemInstance?.itemName ?? "?"}");
                        }
                    }
                    finally { if (wHelper) HousingFurniture.Exit(); }
                    return $"OK warnicons n={wn} [{wsb}]";
                }

                case "takeoverbuy":
                {
                    // T-lever (2026-09-12): drive the CLIENT's takeover request for an AI-run business —
                    // the same MessageType.TakeoverRequest the BizMan offer click sends (ClientOfferPrefix
                    // :72 -> MPClient.SendTakeoverRequest), so accept -> furnish -> deferred claim runs
                    // unchanged. 'itemcount <addr>' proves the resulting item count.
                    // The address key itself contains spaces, so the AMOUNT is the last token.
                    var tba = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tba.Length < 2) return "ERR usage: takeoverbuy <addr> <amount>";
                    if (!float.TryParse(tba[tba.Length - 1], out float tbAmount)) return "ERR amount must be a number";
                    string tbAddr = string.Join(" ", tba, 0, tba.Length - 1);
                    return MPTakeover.LeverRequest(tbAddr, tbAmount);
                }

                case "forrent":
                {
                    // Round-260 helper: list up to 8 for-rent addresses on this machine.
                    // H-MERGERSTOCK-2: optional building-type filter - `forrent retail` lists only ba:buildingtype_retail
                    // premises, so a run that stamps a business type on what it rents picks a shop unit, not a flat.
                    string frType = arg.Length > 0 ? "ba:buildingtype_" + arg.Trim().ToLowerInvariant() : "";
                    var found = new StringBuilder(); int nFound = 0;
                    try
                    {
                        var gi = SaveGameManager.Current;
                        if (gi?.BuildingRegistrations != null)
                            foreach (var r2 in gi.BuildingRegistrations)
                            {
                                if (r2 == null) continue;
                                bool ok2 = false; try { ok2 = r2.AvailableForRent && !r2.RentedByPlayer; } catch { }
                                if (!ok2) continue;
                                if (frType.Length > 0)
                                {
                                    string fbt = ""; try { fbt = Helpers.BuildingHelper.GetBuilding(r2.Address)?.BuildingType ?? ""; } catch { }
                                    if (fbt != frType) continue;
                                }
                                if (nFound++ > 0) found.Append(" | ");
                                found.Append(GameStateReader.AddressKey(r2));
                                if (nFound >= 8) break;
                            }
                    }
                    catch (Exception exF) { return "ERR " + exF.Message; }
                    return nFound == 0 ? "OK none for rent" : $"OK {nFound} for-rent: {found}";
                }

                case "setbiz":
                {
                    // Round-260b: stamp a business name+type onto a reg — synthesizes the
                    // host-side state BusinessSync writes when a client runs a NAMED
                    // business (the exact wedge precondition from field 20260810-232704),
                    // no naming UI needed. Makes the T260 name/type leg FIXTURE-INDEPENDENT
                    // (T260 run 1 failed its own precondition: the chosen fixture had no
                    // client-owned named business — tests must create what they assert on).
                    var tk = arg.Split(' ');
                    if (tk.Length < 3) return "ERR usage: setbiz <num> <ba:street_x> <name...>";
                    string sAddr = tk[0] + " " + tk[1];
                    string sName = string.Join(" ", tk, 2, tk.Length - 2);
                    var sreg = GameStatePatcher.FindRegistration(sAddr);
                    if (sreg == null) return $"ERR no registration for '{sAddr}'";
                    try
                    {
                        sreg.BusinessName     = sName;
                        sreg.businessTypeName = "ba:businesstype_fastfoodrestaurant";
                        sreg.AvailableForRent = false;
                    }
                    catch (Exception exS) { return "ERR " + exS.Message; }
                    return $"OK '{sAddr}' now reads name='{sName}' type=fastfoodrestaurant avail=False";
                }

                case "settype":
                {
                    // H-CLOCK60-1 rig lever (user-approved 2026-09-20). IN MEMORY ONLY: stamps a business
                    // TYPE NAME onto one of this machine's own rented registrations, so a run can plant a
                    // type that does not resolve (the field shape: a modded type gone from the world) and
                    // then put the captured old one back. Nothing is written to a save - the scenario that
                    // uses this lever never saves. Refuses an address this machine does not rent.
                    // No argument = FIXTURE DISCOVERY (the `schedcount` shape): this machine's rented
                    // registrations, '|' between entries and '=' before the type, because address keys
                    // contain both spaces and ':'.
                    if (arg.Length == 0)
                    {
                        var tregs = SaveGameManager.Current?.BuildingRegistrations;
                        if (tregs == null) return "ERR no save loaded";
                        var tsb = new StringBuilder("OK settype rented=[");
                        int tn = 0;
                        foreach (var treg in tregs)
                        {
                            if (treg == null) continue;
                            bool trented = false; try { trented = treg.RentedByPlayer; } catch { }
                            if (!trented) continue;
                            string tk2 = ""; try { tk2 = GameStateReader.AddressKey(treg); } catch { }
                            if (tk2.Length == 0) continue;
                            string tt2 = ""; try { tt2 = treg.businessTypeName ?? ""; } catch { }
                            if (tt2.Length == 0) continue;           // a premises with no type is no use to a run that plants and restores one
                            if (tn++ > 0) tsb.Append('|');
                            tsb.Append(tk2).Append('=').Append(tt2);
                            if (tn >= 12) break;
                        }
                        return tsb.Append(']').ToString();
                    }
                    var ttk = arg.Split(' ');
                    if (ttk.Length < 3) return "ERR usage: settype <num> <ba:street_x> <businessTypeName> (no argument lists this machine's rented registrations)";
                    string taddr = ttk[0] + " " + ttk[1];
                    string ttype = string.Join(" ", ttk, 2, ttk.Length - 2).Trim();
                    var treg2 = GameStatePatcher.FindRegistration(taddr);
                    if (treg2 == null) return $"ERR no registration for '{taddr}'";
                    string told;
                    try
                    {
                        if (!treg2.RentedByPlayer) return $"ERR '{taddr}' is not rented by the player on this machine";
                        told = treg2.businessTypeName ?? "";
                        treg2.businessTypeName = ttype;
                    }
                    catch (Exception exT) { return "ERR " + exT.Message; }
                    string tkey = taddr; try { tkey = GameStateReader.AddressKey(treg2); } catch { }
                    return $"OK settype {tkey} was='{told}' now='{ttype}' rented={treg2.RentedByPlayer}";
                }

                case "vacate":
                    // Round-260: client-side — sends the SAME VacateRequest the terminate
                    // patch sends, exercising the host's vacate reflect (the wedge fix).
                    if (arg.Length == 0) return "ERR address key required";
                    if (!MPClient.IsConnected) return "ERR client only (host terminates natively)";
                    MPClient.RequestVacateBuilding(arg);
                    return $"OK VacateRequest sent for '{arg}'";

                case "charconfirm":
                    ConfirmCustomizerArmed = true;
                    _customizerFirstSeen = -1f;
                    return "OK armed — confirms the character screen when it appears (2s settle)";

                case "clock":
                {
                    // L1 (user-approved rig tooling, 2026-09-18): move the game clock forward to HH:MM TODAY
                    // through the game's own time path. GameManager.RunMainGameTick is that path: it adds the
                    // minutes to the clock and, in its own `while (Minute >= 60)` loop, runs every hour it
                    // crosses (BusinessSimulatorHelper/Job/Parking/Recruitment/Happiness/Employee/Pricing
                    // RunHourly, the wholesale + factory delivery hours) and NewDay() at the 24:00 rollover,
                    // which is what produces the 23:00/midnight day-end and the DailySummary. Writing the hour
                    // field instead would skip all of it.
                    //
                    // The native TimeMachine is NOT the route in MP: Patch_TimeMachine_Start_Consensus stops
                    // every locally started machine through its own off switch and Patch_TimeMachine_Update_Freeze
                    // stops it advancing time itself, so a machine started here would move nothing.
                    // HOST ONLY: the host's clock is the world's; clients follow through the ordinary time sync.
                    if (!MPServer.IsRunning) return "ERR host only";
                    int colon = arg.IndexOf(':');
                    if (colon <= 0 || !int.TryParse(arg.Substring(0, colon).Trim(), out var wantH)
                                   || !int.TryParse(arg.Substring(colon + 1).Trim(), out var wantM))
                        return "ERR usage: clock HH:MM";
                    if (wantH < 0 || wantH > 23 || wantM < 0 || wantM > 59) return "ERR usage: clock HH:MM (00:00-23:59)";
                    var (curDay, curHour) = GameStateReader.GetGameTime();
                    double nowMin  = curHour * 60.0;
                    double wantMin = wantH * 60.0 + wantM;
                    double delta   = wantMin - nowMin;
                    if (delta <= 0.001)
                        return $"ERR the clock only moves forward — it is already day {curDay} {(int)curHour:00}:{(int)((curHour % 1f) * 60f):00}, and {wantH:00}:{wantM:00} is not later today";
                    var gm = InstanceBehavior<GameManager>.Instance;
                    if (gm == null) return "ERR no GameManager yet";
                    string was = $"{(int)curHour:00}:{(int)((curHour % 1f) * 60f):00}";
                    gm.RunMainGameTick((float)delta);                 // the game's own tick: clock + every hourly/daily pass it crosses
                    try { TimeSync.NoteAuthorizedClockWrite(); } catch { }   // sanctioned jump — the world-clock guardian re-bases instead of pinning it back
                    var (newDay, newHour) = GameStateReader.GetGameTime();
                    string now = $"{(int)newHour:00}:{(int)((newHour % 1f) * 60f):00}";
                    return $"OK clock {was} -> {now} on day {newDay} (advanced {delta:0.#} game-minutes through RunMainGameTick; day was {curDay})";
                }

                // ── Batch-21: consensus-skip rate levers (user ruling 2026-09-20) ──────
                case "skiprate":
                {
                    // The skip rate is ONE runtime value (MPRestSync.SkipMinutesPerRealSecond), read by the
                    // skip executor on every machine and by the client's BEHIND catch-up. Setting it here is
                    // LOCAL to this instance - a scenario that compares rates sets it on every instance.
                    if (arg.Length == 0)
                        return $"OK skiprate rate={MPRestSync.SkipMinutesPerRealSecond:0.##} maxPerFrame={MPRestSync.MaxSkipMinutesPerFrame:0.##}";
                    if (!float.TryParse(arg.Trim(), out var srWant) || srWant < 1f || srWant > 600f)
                        return "ERR usage: skiprate [<game-minutes-per-real-second> 1-600] (no argument prints the rate)";
                    float srWas = MPRestSync.SkipMinutesPerRealSecond;
                    MPRestSync.SkipMinutesPerRealSecond = srWant;
                    Plugin.Logger.LogInfo($"[TestDrive] skiprate {srWas:0.##} -> {srWant:0.##} game-min per real second (this machine only).");
                    return $"OK skiprate was={srWas:0.##} rate={MPRestSync.SkipMinutesPerRealSecond:0.##} maxPerFrame={MPRestSync.MaxSkipMinutesPerFrame:0.##}";
                }

                // ── D-SKIPPACE-1: the shop's VISUAL pace during a skip (user ruling 2026-09-21) ──
                case "skippace":
                {
                    // ONE number, LOCAL to this instance (like skiprate): the cap on how much faster the
                    // shop's bodies look while a consensus skip runs. 1 = the feature is off here.
                    if (arg.Length == 0)
                        return $"OK skippace max={MPRestSync.MaxSkipVisualPace:0.##} now={MPRestSync.SkipPace:0.##} "
                             + $"scaledBodies={SkipPaceBodies.ScaledBodies} animScaled={SkipPaceBodies.AnimScaled} "
                             + $"walkmax={SkipPaceBodies.MaxPacedBodySpeed:0.##} "
                             + $"localBodyPaced={MPRestSync.LocalBodyPaced}";
                    // `skippace walkmax <m/s>`: the OTHER rail - metres per second a paced body may reach.
                    // 1000 ships (un-capped); the game's own run speed is 6, so `walkmax 6` restores the
                    // pre-2026-09-21 ceiling on this machine without touching the pace itself.
                    if (arg.Trim().StartsWith("walkmax", StringComparison.OrdinalIgnoreCase))
                    {
                        string wmArg = arg.Trim().Substring(7).Trim();
                        if (wmArg.Length == 0 || !float.TryParse(wmArg, out var wmWant) || wmWant < 0.5f || wmWant > 1000f)
                            return "ERR usage: skippace walkmax <metres-per-second 0.5-1000>";
                        float wmWas = SkipPaceBodies.MaxPacedBodySpeed;
                        SkipPaceBodies.MaxPacedBodySpeed = wmWant;
                        Plugin.Logger.LogInfo($"[TestDrive] skippace walkmax {wmWas:0.##} -> {wmWant:0.##} m/s (this machine only).");
                        return $"OK skippace max={MPRestSync.MaxSkipVisualPace:0.##} now={MPRestSync.SkipPace:0.##} "
                             + $"scaledBodies={SkipPaceBodies.ScaledBodies} animScaled={SkipPaceBodies.AnimScaled} "
                             + $"walkmax={SkipPaceBodies.MaxPacedBodySpeed:0.##} was={wmWas:0.##}";
                    }
                    if (!float.TryParse(arg.Trim(), out var spWant) || spWant < 1f || spWant > 1000f)
                        return "ERR usage: skippace [<max-visual-pace> 1-1000] | skippace walkmax <m/s> (no argument prints the pace)";
                    float spWas = MPRestSync.MaxSkipVisualPace;
                    MPRestSync.MaxSkipVisualPace = spWant;
                    Plugin.Logger.LogInfo($"[TestDrive] skippace {spWas:0.##} -> {spWant:0.##} max visual pace (this machine only).");
                    return $"OK skippace max={MPRestSync.MaxSkipVisualPace:0.##} now={MPRestSync.SkipPace:0.##} "
                         + $"scaledBodies={SkipPaceBodies.ScaledBodies} animScaled={SkipPaceBodies.AnimScaled} "
                         + $"walkmax={SkipPaceBodies.MaxPacedBodySpeed:0.##} "
                         + $"localBodyPaced={MPRestSync.LocalBodyPaced} was={spWas:0.##}";
                }

                // ── BATCH-26 FOLD 2 (B5): the day's shop measurement, on one line ──
                case "shopday":
                {
                    // READ-ONLY apart from 'reset', which zeroes the counters and stamps the window's
                    // start clock. Everything here is either a plain counter bumped on an event that
                    // was already happening (ShopDayMeter) or a walk of tables the game already keeps.
                    if (arg.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase))
                    {
                        ShopDayMeter.Reset();
                        return $"OK shopday reset day={ShopDayMeter.WindowDay} hour={ShopDayMeter.WindowHour}";
                    }
                    if (arg.Length == 0) return "ERR usage: shopday <num> <ba:street_x> | shopday reset";
                    var sdReg = GameStatePatcher.FindRegistration(arg);
                    if (sdReg == null) return $"ERR no registration at '{arg}'";
                    string sdKey = arg; try { sdKey = GameStateReader.AddressKey(sdReg); } catch { }
                    int sdDay = -1, sdHour = -1;
                    try { sdDay = SaveGameManager.Current.Day; sdHour = SaveGameManager.Current.Hour; } catch { }
                    // THE DAY'S SHOPPER LIST: every CustomerEntry the game generated for this address.
                    // 'due' = its spawn hour has arrived; 'pending' = still ahead of the clock; 'window'
                    // = due since the last `shopday reset`, which is the measured stretch.
                    int sdTotal = 0, sdDue = 0, sdPending = 0, sdDone = 0, sdWindow = 0;
                    try
                    {
                        var sdList = AI.Customers.CustomerEntries.CustomerEntriesHelper.GetEntriesByAddress(sdReg.Address);
                        if (sdList != null)
                            foreach (var sdE in sdList)
                            {
                                if (sdE == null) continue;
                                sdTotal++;
                                if (sdE.completed) sdDone++;
                                if (sdE.spawnTime.Hour <= sdHour)
                                {
                                    sdDue++;
                                    if (ShopDayMeter.WindowHour >= 0 && sdE.spawnTime.Hour >= ShopDayMeter.WindowHour) sdWindow++;
                                }
                                else sdPending++;
                            }
                    }
                    catch (Exception exSd) { return "ERR " + exSd.Message; }
                    // THE TILL: the same arithmetic the `tilldupes` lever uses, so the two can never
                    // disagree - orders on the list, distinct orders, and what one reference is worth.
                    int sdOrders = 0, sdDistinct = 0;
                    double sdPaidRev = 0;
                    try
                    {
                        var sdTill = sdReg.unprocessedCompletedOrders;
                        var sdSeen = new System.Collections.Generic.HashSet<Order>(new TillDupes.RefEq<Order>());
                        if (sdTill != null)
                            for (int sdI = 0; sdI < sdTill.Count; sdI++)
                            {
                                var sdO = sdTill[sdI];
                                if (sdO == null) continue;
                                sdOrders++;
                                if (sdSeen.Add(sdO)) sdPaidRev += TillDupes.ExtraReferenceValue(sdO);
                            }
                        sdDistinct = sdSeen.Count;
                    }
                    catch { }
                    // ── BATCH-26 FOLD 3 (G): THE ACCOUNTING IDENTITY over the measured window ──
                    // Every entry whose spawn hour has fully PASSED (so its hourly pass has run) is
                    // exactly one of: BILLED (some reference to its order is on the till - a live
                    // checkout, a paper order, or a hand-back), still on the FLOOR in a live body,
                    // NOSALE (its order is completed with nothing on the till: a native walk-out, or a
                    // hand-back that found nothing to sell), a native CAPDROP (the entry marked
                    // completed with no order at all), or UNBILLED - which is the bug, and must read 0.
                    // Only hours the PAPER SIMULATOR actually covered are judged: a normal-speed hour spent
                    // standing in your own shop is exempt from native RunHourly and is billed by nobody in
                    // vanilla either, so its entries are counted as idNoPass and left out of the identity.
                    // Entries of the CURRENT hour are still OPEN (their pass has not run); later hours
                    // are PENDING. The floor is read from the LOCAL interior's registry, so the figure
                    // only means anything on a machine standing in the measured shop - which is how the
                    // t-shopday-* scenarios are built.
                    int idDue = 0, idBilled = 0, idFloor = 0, idNosale = 0, idCapDrop = 0, idUnbilled = 0, idOpen = 0, idPending = 0, idNoPass = 0;
                    try
                    {
                        var idTill = new System.Collections.Generic.HashSet<Order>(new TillDupes.RefEq<Order>());
                        var idT = sdReg.unprocessedCompletedOrders;
                        if (idT != null)
                            for (int idI = 0; idI < idT.Count; idI++)
                                if (idT[idI] != null) idTill.Add(idT[idI]);
                        var idOnFloor = new System.Collections.Generic.HashSet<AI.Customers.CustomerEntries.CustomerEntry>(
                                            new TillDupes.RefEq<AI.Customers.CustomerEntries.CustomerEntry>());
                        var idBodies = IndoorCustomerSpawner.Customers;
                        if (idBodies != null)
                            for (int idI = 0; idI < idBodies.Count; idI++)
                            {
                                var idCe = idBodies[idI]?.customerEntry;
                                if (idCe != null) idOnFloor.Add(idCe);
                            }
                        int idFrom = ShopDayMeter.WindowHour >= 0 ? ShopDayMeter.WindowHour : 0;
                        var idList = AI.Customers.CustomerEntries.CustomerEntriesHelper.GetEntriesByAddress(sdReg.Address);
                        if (idList != null)
                            foreach (var idE in idList)
                            {
                                if (idE == null) continue;
                                int idH = idE.spawnTime.Hour;
                                if (idH > sdHour) { idPending++; continue; }
                                if (idH == sdHour) { idOpen++; continue; }
                                if (idH < idFrom) continue;
                                // An hour the paper simulator was never the accountant for (a normal-speed
                                // hour inside the occupied shop, which native RunHourly exempts) is not this
                                // fold's business - vanilla bills nobody for it either. Counted, not judged.
                                if (!ShopDayMeter.PaperCoveredHour(sdDay, idH)) { idNoPass++; continue; }
                                idDue++;
                                if (idOnFloor.Contains(idE)) { idFloor++; continue; }
                                if (idE.order != null && idTill.Contains(idE.order)) { idBilled++; continue; }
                                if (idE.order != null && idE.order.completed) { idNosale++; continue; }
                                if (idE.completed) { idCapDrop++; continue; }
                                idUnbilled++;
                            }
                    }
                    catch (Exception exId) { return "ERR identity " + exId.Message; }
                    int sdCap = -1;
                    try
                    {
                        sdCap = Buildings.BuildingSizeHelper.GetData(sdReg)
                                .GetCustomerCapacity(sdReg.BuildingCached.BuildingType, sdReg.BuildingCached.BuildingVersion);
                    }
                    catch { }
                    int sdLive = -1;
                    try { sdLive = IndoorCustomerSpawner.Customers?.Count ?? -1; } catch { }
                    int sdLivePay = -1;
                    try { sdLivePay = Patch_Order_Pay_HelperForward.LivePayCount(sdKey); } catch { }
                    float sdMinMult = 1f;
                    try { sdMinMult = MPPatches.Patch_IndoorSpawner_SkipVisualPace.NormalMinutesPerRealSecond(); } catch { }
                    return $"OK shopday {sdKey} day={sdDay} hour={sdHour} windowFrom={ShopDayMeter.WindowDay}/{ShopDayMeter.WindowHour} "
                         + $"entries={sdTotal} due={sdDue} pending={sdPending} window={sdWindow} entriesCompleted={sdDone} "
                         + $"bodies={ShopDayMeter.BodiesSpawned} maxBodies={ShopDayMeter.MaxBodies} live={sdLive} cap={sdCap} "
                         + $"completeOrders={ShopDayMeter.LiveCompleteOrders} livePays={sdLivePay} unpaidExits={ShopDayMeter.UnpaidExits} "
                         + $"paperHours={ShopDayMeter.PaperHours} paperCandidates={ShopDayMeter.PaperCandidates} paperOrders={ShopDayMeter.PaperOrders} "
                         + $"capDrops={ShopDayMeter.CapDrops} paperShop='{ShopDayMeter.PaperShop}' "
                         + $"tillOrders={sdOrders} tillDistinct={sdDistinct} paidRevenue={sdPaidRev:F2} "
                         + $"handBacks={SkipHandback.HandBacks} hbPaid={SkipHandback.HandBackPaid} hbNothing={SkipHandback.HandBackNothing} "
                         + $"hbLate={SkipHandback.HandBackLate} hbEarly={SkipHandback.HandBackEarly} hbRevenue={SkipHandback.HandBackRevenue:F2} "
                         + $"idDue={idDue} idBilled={idBilled} idFloor={idFloor} idNosale={idNosale} idCapDrop={idCapDrop} "
                         + $"idUnbilled={idUnbilled} idNoPass={idNoPass} idOpen={idOpen} idPending={idPending} "
                         + $"visitAvgMin={(ShopDayMeter.VisitN > 0 ? ShopDayMeter.VisitSum / ShopDayMeter.VisitN : 0f):F1} "
                         + $"visitMaxMin={ShopDayMeter.VisitMax:F1} visitN={ShopDayMeter.VisitN} "
                         + $"lagAvg={(ShopDayMeter.LagN > 0 ? ShopDayMeter.LagSum / ShopDayMeter.LagN : 0f):F2} "
                         + $"lagMax={ShopDayMeter.LagMax:F2} lagN={ShopDayMeter.LagN} "
                         + $"leaveStale={CustomerPuppets.LeaveStaleTotal} puppetLeaves={CustomerPuppets.PuppetLeavesTotal} "
                         + $"leaveMissing={CustomerPuppets.LeaveMissingTotal} "
                         + $"bodiesOverCap={(sdCap > 0 && ShopDayMeter.MaxBodies > sdCap)} minMult={sdMinMult:0.###}";
                }

                // ── BATCH-26 FOLD 4: the POSITIVE CONTROL for the H-SKIPWALKIN-1 hand-back ──
                case "custevict":
                {
                    // Send up to n live bodies home THE WAY CLOSING TIME SENDS THEM: the native private
                    // Customer.InstantlyLeave (Customer.cs :147-155), which is exactly what
                    // Customer.OnNewHour (:118-128) calls when the shop is shut and which ends in
                    // Customer.Leave - the one funnel H-SKIPWALKIN-1 hangs off. NO new write path: this
                    // lever calls a method the game calls itself every day at closing time.
                    //
                    // Only bodies whose order is NOT completed are taken - a shopper who has already
                    // paid is owed nothing and would prove nothing - and the player's own body never is.
                    // THIS MACHINE ONLY, and only the interior it is standing in, because
                    // IndoorCustomerSpawner.Customers IS the local interior's live register.
                    int ceWant;
                    if (!int.TryParse(arg.Trim(), out ceWant) || ceWant <= 0)
                        return "ERR usage: custevict <n> (a positive count of live bodies to send home)";
                    var ceTake = new System.Collections.Generic.List<Customer>();
                    try
                    {
                        // Collected FIRST, then evicted: InstantlyLeave -> Leave can touch the register,
                        // and a list being walked is not a list to mutate.
                        var ceAll = IndoorCustomerSpawner.Customers;
                        if (ceAll == null) return "ERR custevict no indoor customer register (stand inside the shop)";
                        for (int ceI = 0; ceI < ceAll.Count && ceTake.Count < ceWant; ceI++)
                        {
                            var ceC = ceAll[ceI];
                            if (ceC == null || ceC.isPlayer) continue;
                            var ceE = ceC.customerEntry;
                            if (ceE == null || ceE.order == null) continue;      // SkipHandback reads the ENTRY's order
                            if (ceC.order == null || ceC.order.completed) continue;  // Leave and the unpaid-exit counter read the BODY's
                            ceTake.Add(ceC);
                        }
                    }
                    catch (Exception exCe) { return "ERR custevict " + exCe.Message; }
                    var ceIds = new StringBuilder();
                    int ceDone = 0;
                    foreach (var ceC in ceTake)
                    {
                        try
                        {
                            // AccessTools searches base types, so a SelfServiceCustomer / GymCustomer
                            // subclass still resolves Customer's own private InstantlyLeave.
                            var ceMi = HarmonyLib.AccessTools.Method(ceC.GetType(), "InstantlyLeave", new Type[0]);
                            if (ceMi == null) continue;
                            var ceE = ceC.customerEntry;
                            int ceH = -1;
                            try { ceH = ceE.spawnTime.Hour; } catch { }
                            int ceHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ceE);
                            ceMi.Invoke(ceC, new object[0]);
                            ceDone++;
                            if (ceIds.Length > 0) ceIds.Append(',');
                            ceIds.Append($"h{ceH}#{ceHash:X}");
                        }
                        catch (Exception exCe2)
                        {
                            try { Plugin.Logger.LogWarning($"[TestDrive] custevict: {exCe2.Message}"); } catch { }
                        }
                    }
                    return $"OK custevict asked={ceWant} evicted={ceDone} skipActive={MPRestSync.SkipActive} ids={ceIds}";
                }

                // ── H-PUPPETSTUTTER-1 (batch 27): walk-animation stops per minute, both sides ──
                case "puppetflips":
                {
                    // `puppetflips`            IsMoving true->false changes since the last reset: sim = this machine's natives at
                    //                          each stream sample (simulator), watch = its customer copies (watcher), per minute.
                    // `puppetflips reset`      zero both counters and restart the clock.
                    // `puppetflips vs <n>`     also compare this watcher's rate to the simulator's <n> per minute (within 1.5x).
                    try
                    {
                        string pfa = arg.Trim();
                        if (pfa == "reset") { CustomerPuppets.ResetFlips(); return "OK puppetflips reset"; }
                        float pfv = -1f;
                        if (pfa.StartsWith("vs ", StringComparison.Ordinal))
                        {
                            if (!float.TryParse(pfa.Substring(3).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pfv))
                                return "ERR usage: puppetflips [reset | vs <simPerMin>]";
                        }
                        else if (pfa.Length > 0) return "ERR usage: puppetflips [reset | vs <simPerMin>]";
                        return "OK puppetflips " + CustomerPuppets.FlipsLine(pfv);
                    }
                    catch (Exception ex) { return $"ERR puppetflips: {ex.GetType().Name}: {ex.Message}"; }
                }

                // ── H-PUPPETANIM-1 (batch 27): looping activities / one-shots on the watcher's copies ──
                case "puppetacts":
                {
                    // `puppetacts`              per id the looping activity names ON right now (simulator: its natives'
                    //                           animators; watcher: its copies' animators, read live), the missing-parameter
                    //                           count, event counters; the machine-readable sig=<id>:<hexmask>,... last.
                    // `puppetacts vs <sig>`     watcher: of the simulator's customers with a loop, how many copies show ALL of
                    //                           those loops (within=True at >= 80%), and the seated eaters (Sitting + ConsumingFoodSitting).
                    // `puppetacts arm <n>|eat`  simulator: log '[PuppetActs] armed ...' once >= n customers loop / a seated eater exists.
                    try
                    {
                        string paa = arg.Trim();
                        if (paa.StartsWith("arm", StringComparison.Ordinal)) return CustomerPuppets.ArmActs(paa.Substring(3).Trim());
                        if (paa.StartsWith("vs", StringComparison.Ordinal)) return "OK puppetacts " + CustomerPuppets.ActsLine(paa.Substring(2).Trim());
                        if (paa.Length > 0) return "ERR usage: puppetacts [vs <sig> | arm <n>|eat]";
                        return "OK puppetacts " + CustomerPuppets.ActsLine(null);
                    }
                    catch (Exception ex) { return $"ERR puppetacts: {ex.GetType().Name}: {ex.Message}"; }
                }

                // ── Scenario hardening (2026-09-27): bounded premise waits; a premise never reached SKIPS its assertions ──
                // `premise await <tag> <bound_s> <cond>=<n>[,...]`  arm ONE wait on this machine (conditions: live, unbooked,
                //                    fresh = unbooked and arrived in the current game hour after the arm, seated,
                //                    eating=<n>[/<game minutes left>], busy=<n>[/<game minutes left>] = in a timed activity
                //                    whose end is known); it logs "[Premise] <tag> met after ..." or, after the
                //                    bound, retries once and then logs "[Premise] <tag> NOT MET within ...".
                // `premise get <tag>`  met | unmet (a wait still pending or an unknown tag reads unmet) - the rig captures it.
                // `premise set <tag> met|unmet`  the partner's verdict, handed over by the rig (unmet logs the skip line).
                // `premise do <tag> <command>`  runs <command>; when the premise is unmet its result is prefixed
                //                    "PREMISE NOT MET (<tag>) - assertion skipped | " and "[Premise] not met (<tag>)" is logged, so a
                //                    step's regex can accept the skip explicitly. An unknown tag is an ERR (never a silent skip).
                case "premise":
                {
                    try
                    {
                        string[] pa = (arg ?? "").Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                        string psub = pa.Length > 0 ? pa[0].ToLowerInvariant() : "";
                        string ptag = pa.Length > 1 ? pa[1] : "";
                        if (ptag.Length == 0) return "ERR usage: premise await|get|set|do <tag> ...";
                        if (psub == "await")
                        {
                            string[] paw = pa.Length > 2 ? pa[2].Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries) : new string[0];
                            if (paw.Length < 2 || !float.TryParse(paw[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pbound) || pbound <= 0f)
                                return "ERR usage: premise await <tag> <bound_s> <cond>=<n>[,<cond>=<n>...]";
                            return CustomerHandoff.PremiseAwait(ptag, pbound, paw[1].Replace(" ", ""));
                        }
                        if (psub == "get")
                        {
                            string pst = CustomerHandoff.PremiseState(ptag);
                            return pst == "met" ? $"OK premise {ptag}=met" : $"OK premise {ptag}=unmet ({pst})";
                        }
                        if (psub == "set")
                        {
                            string pv = pa.Length > 2 ? pa[2].Trim().ToLowerInvariant() : "";
                            if (pv != "met" && pv != "unmet") return "ERR usage: premise set <tag> met|unmet";
                            CustomerHandoff.PremiseSet(ptag, pv == "met");
                            return $"OK premise {ptag}={pv}";
                        }
                        if (psub == "do")
                        {
                            string pinner = pa.Length > 2 ? pa[2].Trim() : "";
                            if (pinner.Length == 0) return "ERR usage: premise do <tag> <command>";
                            string pst = CustomerHandoff.PremiseState(ptag);
                            if (pst == "met") return Execute(pinner);
                            if (pst != "unmet") return $"ERR premise do: tag '{ptag}' is {pst} (await or set it first)";
                            Plugin.Logger.LogInfo($"[Premise] not met ({ptag}): '{pinner}' runs, the assertions that need the premise are skipped (premise not met).");
                            return $"PREMISE NOT MET ({ptag}) - assertion skipped | " + Execute(pinner);
                        }
                        return "ERR usage: premise await|get|set|do <tag> ...";
                    }
                    catch (Exception ex) { return "ERR premise: " + ex.Message; }
                }

                // ── H-HANDOFF-1 (batch 27): what each live customer's VISIT has reached, on THIS machine ──
                case "custstate":
                {
                    // `custstate`        this interior's natives, each id:timeState:completed:done k/n:queue spot:
                    //                    leaving:fee lines[:seat], and its copies (id:visit row known 1/0), plus
                    //                    the last take-over's tallies. sig = md5 of the sorted id:k/n pairs.
                    //                    Part B: a held spot appends :held=<kind>/<item8>/<seat idx|sub>/<endMin> and a queue
                    //                    place :q=<line item8>/<spot> (CustomerSeatPins.HeldTag).
                    // `custstate held`   part B oracle: leak counts on THIS machine (leakSeats / leakSlots / leakSpots /
                    //                    leakLine / leakDance = spots marked taken that no live customer or pending want
                    //                    holds), wantTaken k/n, the adopted rows' held spot vs the body's now
                    //                    (heldSame / heldDone / heldPending / heldFallback / heldMismatch), endDelta max.
                    // `custstate arm seated <n>` part B: log "[Handoff] armed seated count reached" ONCE, the moment n
                    //                    live natives here sit on a table seat.
                    // `custstate arm <n>` log "[Handoff] armed count reached" ONCE, the moment this interior
                    //                    holds n customers (natives when simulating, copies when following) -
                    //                    a rig step waits on that line instead of guessing a sleep.
                    string csArg = (arg ?? "").Trim();
                    // `custstate simfirst` (H-HANDOFF-1 walk-in race, 2026-09-27) HOST only: the next building change runs
                    //                    the simulator election BEFORE this machine tracks the building (the order that
                    //                    lost the partner's crowd), once - the late adopt at Final receipt must take over.
                    if (csArg.Equals("simfirst", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!MPServer.IsRunning) return "ERR custstate simfirst: host only";
                        CustomerPuppets.DevSimFirst = true;
                        return $"OK custstate simfirst armed bldg='{CustomerPuppets.MyBuilding}' lateAdopts={CustomerPuppets.LateAdopts}";
                    }
                    if (csArg.StartsWith("arm seated", StringComparison.OrdinalIgnoreCase))
                    {
                        int csS;
                        if (!int.TryParse(csArg.Substring(10).Trim(), out csS) || csS <= 0) return "ERR usage: custstate arm seated <n>";
                        CustomerHandoff.ArmSeated(csS);
                        return $"OK custstate armed seated n={csS} bldg='{CustomerPuppets.MyBuilding}'";
                    }
                    if (csArg.StartsWith("arm", StringComparison.OrdinalIgnoreCase))
                    {
                        int csN;
                        if (!int.TryParse(csArg.Substring(3).Trim(), out csN) || csN <= 0) return "ERR usage: custstate arm <n>";
                        CustomerHandoff.Arm(csN);
                        return $"OK custstate armed n={csN} bldg='{CustomerPuppets.MyBuilding}'";
                    }
                    if (csArg.Equals("held", StringComparison.OrdinalIgnoreCase))
                    {
                        string chAddr = CustomerPuppets.MyBuilding;
                        string chMode = CustomerPuppets.AwaitingFinalFrom.Length > 0 ? "waiting"
                                      : CustomerPuppets.SpawnerSuppressedHere ? "follower" : "native";
                        return $"OK custstate held bldg='{chAddr}' sim='{CustomerPuppets.SimulatorFor(chAddr)}' mode={chMode} {CustomerSeatPins.HeldReport()}";
                    }
                    string csAddr = CustomerPuppets.MyBuilding;
                    string csFee = "";
                    try { csFee = Helpers.BusinessTypeHelper.GetEntranceFeeNameForBusinessType(InstanceBehavior<BuildingManager>.Instance.businessType) ?? ""; } catch { }
                    var csNatives = new System.Collections.Generic.List<string>();
                    var csSigParts = new System.Collections.Generic.List<string>();
                    int csNat = 0, csFeeMax = 0, csFeeMin = int.MaxValue, csDone = 0, csLeaving = 0;
                    try
                    {
                        var csAll = IndoorCustomerSpawner.Customers;
                        if (csAll != null)
                            foreach (var csC in csAll)
                            {
                                if (csC == null || csC.isPlayer) continue;
                                csNat++;
                                string csId = CustomerPuppets.RowIdForCustomer(csC);
                                int csItems = 0, csK = 0, csF = 0;
                                if (csC.order != null && csC.order.entries != null)
                                    foreach (var csE in csC.order.entries)
                                        if (csE != null) { csItems++; if (csE.processed) csK++; if (csFee.Length > 0 && csE.itemName == csFee) csF++; }
                                if (csF > csFeeMax) csFeeMax = csF;
                                if (csF < csFeeMin) csFeeMin = csF;
                                bool csCompleted = csC.order != null && csC.order.completed;
                                if (csCompleted) csDone++;
                                int csSpot = -1;
                                try { csSpot = csC.assignedWaitingLine != null ? csC.currentWaitingLineSpot : -1; } catch { }
                                bool csLv = CustomerHandoff.IsLeavingBody(csC);
                                if (csLv) csLeaving++;
                                csNatives.Add($"{csId}:{(int)csC.customerTimeState}:{(csCompleted ? 1 : 0)}:{csK}/{csItems}:{csSpot}:{(csLv ? 1 : 0)}:{csF}{CustomerSeatPins.HeldTag(csC)}");
                                csSigParts.Add($"{csId}:{csK}/{csItems}");
                            }
                    }
                    catch (Exception exCs) { return "ERR custstate " + exCs.Message; }
                    if (csFeeMin == int.MaxValue) csFeeMin = 0;
                    var csCopies = new System.Collections.Generic.List<string>();
                    int csCopyRows = 0;
                    foreach (var csPid in CustomerPuppets.PuppetIds())
                    {
                        bool csHas = CustomerHandoff.HasRow(csPid);
                        if (csHas) csCopyRows++;
                        csCopies.Add($"{csPid}:{(csHas ? 1 : 0)}");
                    }
                    csSigParts.Sort(StringComparer.Ordinal);
                    string csSig = "00000000";
                    try
                    {
                        using (var md = System.Security.Cryptography.MD5.Create())
                        {
                            var hb = md.ComputeHash(Encoding.UTF8.GetBytes(string.Join(";", csSigParts)));
                            var sbh = new StringBuilder();
                            for (int i = 0; i < 4; i++) sbh.Append(hb[i].ToString("x2"));
                            csSig = sbh.ToString();
                        }
                    }
                    catch { }
                    string csMode = CustomerPuppets.AwaitingFinalFrom.Length > 0 ? "waiting"
                                  : CustomerPuppets.SpawnerSuppressedHere ? "follower" : "native";
                    return $"OK custstate bldg='{csAddr}' sim='{CustomerPuppets.SimulatorFor(csAddr)}' mode={csMode} "
                         + $"natives={csNat} done={csDone} leaving={csLeaving} feeMax={csFeeMax} feeMin={csFeeMin} copies={CustomerPuppets.PuppetCount} copyRows={csCopyRows} "
                         + $"visitRows={CustomerHandoff.KnownRows} finalsRx={CustomerHandoff.FinalsReceived} finalsTx={CustomerHandoff.FinalsSent} "
                         + $"streamRx={CustomerHandoff.StreamRowsReceived} streamTx={CustomerHandoff.StreamSends} bookOnce={BookOnce.Count} "
                         + $"lastAdopt={CustomerPuppets.LastAdopted}/{CustomerPuppets.LastWithState}/{CustomerPuppets.LastLeaving}/{CustomerPuppets.LastWalked} "
                         + $"lastMatched={CustomerPuppets.LastMatched} lastFrom='{CustomerPuppets.LastAdoptFrom}' lastVia={CustomerPuppets.LastAdoptVia} "
                         + $"feeSup={CustomerHandoff.FeeSuppressed} compSup={CustomerHandoff.ComplaintSuppressed} "
                         + $"sig={csSig} natList={string.Join(";", csNatives)} copyList={string.Join(";", csCopies)}";
                }

                // ── H-HANDOFF-1 folds: the till oracle on the machine that keeps the books ──
                case "handoffbook":
                {
                    // `handoffbook <num> <ba:street_x>` - every customer that crossed a hand-off here: its till
                    // references (entry Order or forward-booked Order) and whether the partner forwarded a sale
                    // for it. identity=ok <=> booked == paid, no id booked twice, no forwarded sale unbooked.
                    if (arg.Length == 0) return "ERR usage: handoffbook <num> <ba:street_x>";
                    var hbReg = GameStatePatcher.FindRegistration(arg);
                    if (hbReg == null) return $"ERR no registration at '{arg}'";
                    string hbKey = arg; try { hbKey = GameStateReader.AddressKey(hbReg); } catch { }
                    bool hbBooks = false; try { hbBooks = MergerFlip.BooksHere(hbReg); } catch { }
                    return $"OK handoffbook {hbKey} books={hbBooks} " + CustomerHandoff.LedgerReport(hbReg)
                         + " " + CustomerEntrySync.ForwardDoubleReport(hbReg) + " ";
                }

                case "stockdelta":
                {
                    // `stockdelta <num> <ba:street_x> [mark]` - H-HANDOFF-1 fold H2 STOCK oracle (CustomerHandoff.StockDelta):
                    // 'mark' snapshots the shop's stock / till / live baskets; a bare readout reports the per-item deltas
                    // since the last mark and marks again.
                    string sdArg = arg.Trim();
                    bool sdMark = false;
                    if (sdArg.EndsWith(" mark", StringComparison.OrdinalIgnoreCase)) { sdMark = true; sdArg = sdArg.Substring(0, sdArg.Length - 5).Trim(); }
                    if (sdArg.Length == 0) return "ERR usage: stockdelta <num> <ba:street_x> [mark]";
                    var sdReg = GameStatePatcher.FindRegistration(sdArg);
                    if (sdReg == null) return $"ERR no registration at '{sdArg}'";
                    return "OK stockdelta " + CustomerHandoff.StockDelta(sdReg, sdMark);
                }

                case "skipvote":
                {
                    // A scriptable consensus-skip vote. SetSkipRequest needs Seated || Loitering, and a rig
                    // has no seat, so the vote is raised in LOITERING mode (the standing vote, MPRestSync
                    // ~:202-218) exactly as the HUD button does. 'off' drops the vote and the loiter session.
                    // Forms: HH:MM = the NEXT occurrence of that clock time; +<n>h / +<n>m = that far ahead of
                    // THIS machine's clock, which is what a rate comparison needs (the same span on both
                    // machines whatever hour the fixture loads at). The goal is the host's to reconcile - it
                    // races to the EARLIEST goal across all votes.
                    string sv = arg.Trim();
                    if (sv.Length == 0) return "ERR usage: skipvote <HH:MM>|+<n>h|+<n>m|off";
                    if (sv.Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        MPRestSync.SetSkipRequest(false);
                        MPRestSync.SetLoitering(false);
                        return $"OK skipvote goal=- seated={MPRestSync.Seated} loitering={MPRestSync.Loitering}";
                    }
                    double svGoal;
                    if (sv[0] == '+')
                    {
                        char svUnit = char.ToLowerInvariant(sv[sv.Length - 1]);
                        string svNum = (svUnit == 'h' || svUnit == 'm') ? sv.Substring(1, sv.Length - 2) : sv.Substring(1);
                        if (!double.TryParse(svNum.Trim(), out var svAmount) || svAmount <= 0)
                            return "ERR usage: skipvote +<n>h | +<n>m (a positive amount)";
                        svGoal = MPRestSync.NowMinutes() + (svUnit == 'm' ? svAmount : svAmount * 60.0);
                    }
                    else
                    {
                        int svColon = sv.IndexOf(':');
                        if (svColon <= 0 || !int.TryParse(sv.Substring(0, svColon).Trim(), out var svH)
                                         || !int.TryParse(sv.Substring(svColon + 1).Trim(), out var svM)
                                         || svH < 0 || svH > 23 || svM < 0 || svM > 59)
                            return "ERR usage: skipvote <HH:MM>|+<n>h|+<n>m|off";
                        svGoal = MPRestSync.NextOccurrence(svH, svM);
                    }
                    MPRestSync.SetLoitering(true);
                    MPRestSync.SetSkipRequest(true, svGoal);
                    return $"OK skipvote goal={MPRestSync.Fmt(MPRestSync.LocalGoal)} seated={MPRestSync.Seated} loitering={MPRestSync.Loitering}";
                }

                case "skipstate":
                {
                    // Everything a rate comparison turns on, on one line. The *Min fields are raw total
                    // game-minutes so a run can compare two machines numerically; lastEndMin/lastGoalMin/
                    // lastRealS are FROZEN when the skip stops (a live clock moves between two rig commands,
                    // these do not), and prevRealS is the leg before - so one step can assert A > B.
                    var skb = new StringBuilder("OK skipstate ");
                    double skGoal = 0, skNow = 0;
                    try { skGoal = MPRestSync.SkipGoalMinutes; } catch { }
                    try { skNow  = MPRestSync.NowMinutes(); }    catch { }
                    skb.Append($"active={MPRestSync.SkipActive} ");
                    skb.Append($"goal={(skGoal > 0 ? MPRestSync.Fmt(skGoal) : "-")} ");
                    skb.Append($"now={MPRestSync.Fmt(skNow)} votes=[");
                    try
                    {
                        int skN = 0;
                        foreach (var skV in MPRestSync.Votes)
                        {
                            if (skV == null) continue;
                            if (skN++ > 0) skb.Append(';');
                            skb.Append(skV.PlayerId).Append('=').Append(MPRestSync.Fmt(skV.GoalMinutes));
                        }
                    }
                    catch { }
                    skb.Append($"] rate={MPRestSync.SkipMinutesPerRealSecond:0.##} required={MPRestSync.RequiredVotes} ");
                    skb.Append($"localVote={MPRestSync.LocalVoteActive} loitering={MPRestSync.Loitering} ");
                    skb.Append($"elapsedReal={MPRestSync.SkipElapsedRealSeconds:F2} lastRealS={MPRestSync.LastSkipRealSeconds:F2} ");
                    skb.Append($"prevRealS={MPRestSync.PrevSkipRealSeconds:F2} ");
                    skb.Append($"nowMin={skNow:F2} goalMin={skGoal:F2} lastStartMin={MPRestSync.LastSkipStartMinutes:F2} ");
                    skb.Append($"lastEndMin={MPRestSync.LastSkipEndMinutes:F2} lastGoalMin={MPRestSync.LastSkipGoalMinutes:F2}");
                    return skb.ToString();
                }

                case "perf":
                {
                    // The mod's own frame figures without waiting for the 10-second [Perf] line (MPPerf keeps
                    // the counters already - this only formats them). 'reset' restarts the window so a rig
                    // leg's worst frame belongs to that leg.
                    bool pfReset = arg.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase);
                    string pfRole = MPServer.IsRunning ? "MP-HOST" : (MPClient.IsConnected ? "MP-CLIENT" : "SP");
                    string pfLine;
                    try { pfLine = MPPerf.Snapshot(pfReset); }
                    catch (Exception exP) { return "ERR " + exP.Message; }
                    return $"OK perf role={pfRole} {pfLine}{(pfReset ? " (window reset)" : "")}";
                }

                case "tilldupes":
                {
                    // H-SKIPDOUBLE-1 MEASUREMENT (2026-09-20). This lever still only MEASURES; the FIX
                    // shipped the same day - Patch_RunHourly_SimulateOccupiedShopDuringSkip sets walked-in
                    // shoppers aside from the skip's hourly sim, and a tripwire prefix on ProcessDailyOrders
                    // removes any duplicate that still reaches the till. This stays the independent reader
                    // that tells a rig leg whether a dupe formed at all.
                    // During a skip Patch_RunHourly_SimulateOccupiedShopDuringSkip runs the OCCUPIED shop's
                    // full native hourly sim while live customers are still being served. That native pass
                    // builds the hour's list from every CustomerEntry whose spawnTime matches the hour with NO
                    // 'completed' filter (RetailBusinessSimulator.CacheCustomersForCurrentHour :199-210) and
                    // ends with an unconditional unprocessedCompletedOrders.Add(customerEntry.order)
                    // (:229-274) - while the live serve paths add the SAME Order object (Customer.cs:395,
                    // FullServiceEmployee.cs:156, SelfServiceEmployee.cs:150). So an order served live inside
                    // a skipped hour can sit in the day's till list TWICE.
                    // WHAT AN EXTRA REFERENCE IS WORTH: at the day roll BusinessHelper.CreateDailyOrderHistory
                    // (:232-259) walks the list per ENTRY (filter: order.completed && any entry paid), hands
                    // each to AddOrderSales (:280-296), which sums OrderEntry.price for every entry that is
                    // available && priceAccceptable && paid into ItemReport.totalPrice; the sum of those is
                    // orderHistoryEntry.totalRevenue, which ProcessDailyOrders (:221-231) pays out with
                    // ChangeMoneySafe. dupeRevenue below reproduces exactly that sum for each EXTRA
                    // reference - through TillDupes.ExtraReferenceValue, the single copy of that arithmetic,
                    // shared with the corrective tripwire so a measurement and a removal can never disagree.
                    // READ-ONLY: it counts references and never touches the list.
                    if (arg.Length == 0) return "ERR usage: tilldupes <num> <ba:street_x>";
                    var tdReg = GameStatePatcher.FindRegistration(arg);
                    if (tdReg == null) return $"ERR no registration at '{arg}'";
                    string tdKey = arg; try { tdKey = GameStateReader.AddressKey(tdReg); } catch { }
                    int tdOrders = 0, tdDistinct = -1;
                    double tdDupeRev = 0;
                    // D-SKIPPACE-1 gate measurement: what the till list is WORTH - the same single formula
                    // (TillDupes.ExtraReferenceValue, the value of ONE reference to an order at the day
                    // roll) summed over the DISTINCT orders instead of over the extra references. Paired
                    // with liveCompleted below it says how much money a skipped stretch actually produced.
                    double tdPaidRev = 0;
                    try
                    {
                        var tdList = tdReg.unprocessedCompletedOrders;
                        var tdSeen = new System.Collections.Generic.HashSet<Order>(new TillDupes.RefEq<Order>());
                        if (tdList != null)
                        {
                            for (int tdI = 0; tdI < tdList.Count; tdI++)
                            {
                                var tdO = tdList[tdI];
                                if (tdO == null) continue;
                                tdOrders++;
                                if (tdSeen.Add(tdO)) { tdPaidRev += TillDupes.ExtraReferenceValue(tdO); continue; }   // first reference - the legitimate one, and what it will pay
                                tdDupeRev += TillDupes.ExtraReferenceValue(tdO);   // THE shared formula (TillDupes, CustomerEntrySync.cs) - the tripwire bills a removed duplicate with this same code
                            }
                        }
                        tdDistinct = tdSeen.Count;
                    }
                    catch (Exception exT) { return "ERR " + exT.Message; }
                    int tdCap = -1;
                    try
                    {
                        tdCap = Buildings.BuildingSizeHelper.GetData(tdReg)
                                .GetCustomerCapacity(tdReg.BuildingCached.BuildingType, tdReg.BuildingCached.BuildingVersion);
                    }
                    catch { }
                    int tdLive = -1;
                    try
                    {
                        if (BuildingManager.IsInsideBuilding
                            && string.Equals(MPRegisterSync.CurrentShopAddress ?? "", tdKey, StringComparison.Ordinal))
                            tdLive = CustomerPuppets.LiveCustomerCount;
                    }
                    catch { }
                    int tdLiveDone = -1;
                    try { tdLiveDone = Patch_Order_Pay_HelperForward.LivePayCount(tdKey); } catch { }
                    return $"OK tilldupes {tdKey} orders={tdOrders} distinct={tdDistinct} dupes={(tdDistinct < 0 ? -1 : tdOrders - tdDistinct)} "
                         + $"dupeRevenue={tdDupeRev:F2} liveCompleted={tdLiveDone} live={tdLive} cap={tdCap} "
                         + $"paidRevenue={tdPaidRev:F2}";
                }

                case "pause":
                {
                    // T284-C (user-approved 2026-08-21): a scriptable press of the REAL pause
                    // button path — TogglePause WITHOUT the AllowNativePauseCall key, so the
                    // Patch_GSC_TogglePause postfix runs the full MP pipeline exactly as for a
                    // player press.  Absolute semantics: no toggle when already in the state.
                    if (arg != "on" && arg != "off") return "ERR usage: pause on|off";
                    bool want = arg == "on";
                    if (GameStateReader.GetGSCPaused() == want) return $"OK already {(want ? "paused" : "unpaused")}";
                    if (!GameStateReader.InvokeNativeTogglePauseAsPress()) return "ERR TogglePause unavailable (no GSC instance yet, or rate-limited — retry in 1s)";
                    bool now = GameStateReader.GetGSCPaused();
                    return now == want ? $"OK pause pressed → {(want ? "paused" : "unpaused")} (MP pipeline fired via the patch)"
                                       : $"ERR pressed but state reads {(now ? "paused" : "unpaused")} — a suppression patch may have eaten it; check the log";
                }

                case "rivalrace":
                    // Round-256 forced lost-race: run on the CLIENT (c-*.cmd).
                    if (arg == "hold")
                    {
                        HoldRivalsSnapshot = true;
                        return "OK next RivalsSnapshot will be HELD (client-side)";
                    }
                    if (arg == "release")
                    {
                        HoldRivalsSnapshot = false;
                        var held = HeldRivalsSnapshot; HeldRivalsSnapshot = null;
                        if (held == null) return "ERR nothing held";
                        GameStatePatcher.ApplyRivalsSnapshot(held);
                        return $"OK held RivalsSnapshot applied late ({held.Rivals?.Count ?? 0} rival(s))";
                    }
                    return "ERR rivalrace hold|release";

                case "hostload":
                {
                    // Round-240 (batch run 1 root cause): calling HostLoadSession directly skipped
                    // StartLoadGame's lobby-exit (IsInLobby stayed true) — every later joiner was
                    // routed to the LOBBY path and sat there forever, because the mid-game join
                    // path (approval + LoadData) only runs when IsInLobby is false.  Mirror the
                    // UI's button entry exactly; verbs must never call inner functions the UI
                    // wraps with state transitions.
                    if (!MPServer.IsRunning) return "ERR start the server first ('host')";
                    if (arg.Length == 0) return "ERR session name required";
                    // Support-rig forensics (user-approved 2026-08-18): 'hostload <session> as=<stableId>'
                    // loads the NAMED member's character as the world carrier — the only way to open a
                    // FIELD save (whose slots are steam-* ids) on a rig machine (guid-* ids).  The
                    // trailing token is stripped BEFORE the session-name checks so session names with
                    // spaces keep working.  A plain hostload always DISARMS the override.
                    string sess = arg; string? loadAs = null;
                    int ai = arg.LastIndexOf(" as=", StringComparison.OrdinalIgnoreCase);
                    if (ai > 0) { loadAs = arg.Substring(ai + 4).Trim(); sess = arg.Substring(0, ai).Trim(); }
                    MPSaveCoordinator.DevHostLoadAs = string.IsNullOrEmpty(loadAs) ? null : loadAs;
                    if (!string.IsNullOrEmpty(loadAs))
                        Plugin.Logger.LogWarning($"[MPSave] DEV hostload impersonation ARMED: world will load member '{loadAs}' (support-rig forensics).");
                    if (!MPSaveManager.ListSessions().Exists(s => s.Name == sess))
                        return $"ERR session '{sess}' not in the session list";
                    MPServer.ChosenLoadSession = sess;
                    MPServer.StartLoadGame();
                    return $"OK StartLoadGame('{sess}') invoked{(loadAs != null ? $" loading-as '{loadAs}'" : "")} (lobby clients are served their saves; later joiners need 'acceptjoin')";
                }

                case "acceptjoin":
                {
                    // Round-240: mid-game joins park for the host's approval popup — a UI click no
                    // agent can make. Same entry the popup button calls; accepts ALL pending.
                    if (!MPServer.IsRunning) return "ERR host only";
                    var pending = MPServer.PendingJoinList;
                    foreach (var (peerId, _) in pending) MPServer.AcceptPendingJoin(peerId);
                    return pending.Count == 0 ? "OK none pending" : $"OK accepted {pending.Count} pending join(s): {string.Join(", ", pending.ConvertAll(p => p.playerId))}";
                }

                case "rejectjoin":
                {
                    // H-REFUSALMUTE-1: arm/disarm the one-shot refusal of the next join request parked for approval.
                    if (!MPServer.IsRunning) return "ERR host only";
                    string rj = arg.Trim().ToLowerInvariant();
                    if (rj == "on")  { RejectNextJoin = true;  return "OK rejectjoin on - the next join request parked for approval is refused (BAMP:rejected)"; }
                    if (rj == "off") { RejectNextJoin = false; return "OK rejectjoin off"; }
                    return $"ERR usage: rejectjoin on|off (now {(RejectNextJoin ? "on" : "off")})";
                }

                case "steamnet":
                {
                    // H-STEAMNET-1 DEV lever: the effective Steam networking config and every live Steam link's
                    // status line (each also logged as '[SteamNet] link ... (on demand)'). Either role.
                    string sn = SteamNetConfig.DescribeForLever();
                    Plugin.Logger.LogInfo($"[TestDrive] steamnet: {sn}");
                    return "OK steamnet " + sn;
                }

                case "steamrate":
                {
                    // H-STEAMNET-2 DEV lever: the SteamRateControl setting and every live Steam link's rate-controller
                    // state (rate, learned ceiling, buffer, base ping, delivery, counters). Either role.
                    string sr;
                    try { sr = SteamNetConfig.DescribeRateForLever(); } catch (Exception ex) { return "ERR steamrate: " + ex.Message; }
                    Plugin.Logger.LogInfo($"[TestDrive] steamrate: {sr}");
                    return "OK steamrate " + sr;
                }

                case "join":
                {
                    if (MPClient.IsConnected) return "OK already connected";
                    string ip = "127.0.0.1"; int p = 7777;
                    if (arg.Length > 0)
                    {
                        var a = arg.Split(':');
                        ip = a[0];
                        if (a.Length > 1 && int.TryParse(a[1], out var pp)) p = pp;
                    }
                    MPClient.Connect(ip, p);
                    return $"OK Connect({ip}:{p}) invoked — verify via [Client] log lines";
                }

                // ── save-system verbs ─────────────────────────────────────────
                case "save":       // coordinated MANUAL-style save onto the lineage base
                    if (!MPServer.IsRunning) return "ERR host only";
                    MPSaveCoordinator.HostSaveSync("testdrive");
                    return "OK HostSaveSync('testdrive') invoked";

                case "autosave":   // coordinated AUTO save — rotates the -auto slots like the scheduler
                    if (!MPServer.IsRunning) return "ERR host only";
                    // WALLET-DUPE-1: name the rotation slot this save lands in (computed the way HostSaveNow
                    // computes it, before the save so the answer is the slot written), so a scenario can
                    // `hostload` exactly that slot.
                    string autoSlot = MPSaveCoordinator.NextAutoSlotName();
                    MPSaveCoordinator.HostSaveNow("autosave");
                    return $"OK HostSaveNow('autosave') invoked slot={autoSlot}";

                case "blocksave":  // round-237 machinery test: simulate a native no-save state
                {
                    float secs = float.TryParse(arg, out var s) ? s : 60f;
                    SimulateSaveBlockUntil = UnityEngine.Time.unscaledTime + secs;
                    return $"OK saves read as blocked for {secs:0}s on this machine";
                }

                // ── round-236 needs-flag test ─────────────────────────────────
                case "bugreport":
                {
                    // Bug-report v2 (task #40) harness: file a real report through the full
                    // pipeline (save-store attach + peer-log pull + zip + upload attempt 1).
                    // Point the config's BugReportRelayUrl at the local stub (tools/report_stub.py)
                    // or an unreachable address first so nothing posts to the live Discord — the
                    // zip still gets built before the upload is tried.
                    string why = arg.Length > 0 ? arg : "testdrive report";
                    var rep = MPBugReport.Create("testdrive: " + why, openFolder: false);
                    return $"OK report dir={rep.DirectoryPath} uploadQueued={rep.DiscordUploadQueued}";
                }

                case "bugbudget":
                {
                    // H-REPORTLOSS-1: override the bug-report upload budget for THIS process
                    // (KB; 0 = back to the 9 MiB default) so the planner's left-out path can be
                    // exercised with a small world. No argument = read-only: the budget, the
                    // number of pending (undelivered) reports and the bug-reports folder size.
                    if (arg.Length == 0) return "OK " + MPBugReport.DevOutboxState();
                    if (!long.TryParse(arg, out var budgetKb) || budgetKb < 0) return "ERR usage: bugbudget <KB> (0 = the default budget)";
                    return "OK " + MPBugReport.DevSetBudgetKB(budgetKb);
                }

                case "energyflag":
                {
                    var gv = SaveGameManager.Current?.gameVariables;
                    if (gv == null) return "ERR no loaded game";
                    // Round-261 test: no arg = READ ONLY (assert the birth default without touching it).
                    if (arg.Length == 0) return $"OK gv.disableEnergy={gv.disableEnergy} (read-only)";
                    gv.disableEnergy = arg == "on" || arg == "true";
                    return $"OK gv.disableEnergy={gv.disableEnergy} (heartbeat should re-align it within ~3s in MP)";
                }

                case "schedfilter":
                {
                    // Round-263 headless check: inject a duty synthetic, then run the two
                    // patched queries directly. Expect rows(list)=0, dict>=1, probeStationShifts=0.
                    if (arg.Length == 0) return "ERR address key required";
                    var freg = GameStatePatcher.FindRegistration(arg);
                    if (freg == null) return $"ERR no registration for '{arg}'";
                    string stationKey = MPRegisterSync.TestInjectSynthetic(arg);
                    UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.FetchEmployees(freg.Address);
                    int inList = 0, inDict = 0;
                    var emps = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.Employees;
                    if (emps != null)
                        foreach (var e in emps)
                            if (e?.id != null && e.id.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) inList++;
                    var dict = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.EmployeesById;
                    if (dict != null)
                        foreach (var kv in dict)
                            if (kv.Key.StartsWith(MPRegisterSync.SyntheticDutyEmployeeIdPrefix, StringComparison.Ordinal)) inDict++;
                    UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.AddShiftToCache(new WorkShift
                    {
                        startingHour = 0, endingHour = 24,
                        employeeId = MPRegisterSync.SyntheticDutyEmployeeIdPrefix + "PROBE",
                        itemInstanceId = "BAMP_PROBE_STATION", type = WorkShiftType.Default,
                    });
                    int stationShifts = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.GetWorkShiftsByWorkstationId("BAMP_PROBE_STATION").Count;
                    MPRegisterSync.TestRemoveSynthetic(stationKey);
                    return $"OK rows(list)={inList} dict={inDict} probeStationShifts={stationShifts} (expect 0/>=1/0 with round-263)";
                }

                // ── H-SCHEDWIPE-1 rig coverage (L2): the self-mutation the intent gate must catch ──
                // `schedwipe <addressKey> <dayIndex>` empties one day's work shifts the way the BUG does:
                // on the machine it runs on, directly on the list, NOT through ScheduleHelper, with no
                // schedule tab open and no auto-fill running — so SharedShopSchedule's scan sees a changed
                // day with no intent behind it and must refuse to send it. The day index is the LAST token
                // (address keys contain a space).
                case "schedwipe":
                {
                    var swTk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (swTk.Length < 2) return "ERR usage: schedwipe <addressKey> <day 1-7, Monday=1>";
                    string swDayTok = swTk[swTk.Length - 1];
                    if (!int.TryParse(swDayTok, out int swDay) || swDay < 1 || swDay > 7)   // DayOfWeekOrdered: Monday=1 .. Sunday=7
                        return $"ERR day '{swDayTok}' is not 1-7";
                    string swAddr = string.Join(" ", swTk, 0, swTk.Length - 1);
                    var swReg = GameStatePatcher.FindRegistration(swAddr);
                    if (swReg == null) return $"ERR no registration for '{swAddr}'";
                    if (swReg.scheduleDays == null) return $"ERR '{swAddr}' has no scheduleDays";
                    ScheduleDay? swSd = null;
                    foreach (var sd in swReg.scheduleDays) if (sd != null && (int)sd.day == swDay) { swSd = sd; break; }
                    if (swSd == null) return $"ERR '{swAddr}' has no schedule day {swDay}";
                    if (swSd.workShifts == null) return $"ERR '{swAddr}' day {swDay} has no workShifts list";
                    int swBefore = swSd.workShifts.Count;
                    swSd.workShifts.Clear();                       // the self-mutation, deliberately raw
                    return $"OK schedwipe {swAddr} day={swDay} shifts {swBefore}->{swSd.workShifts.Count}";
                }

                // Read-only companion: what the gate currently sees for an address.
                case "schedcount":
                {
                    // No argument = FIXTURE DISCOVERY: which shared shops on this machine actually hold
                    // shifts, so a schedwipe run has a precondition it can meet. '|' separates entries and
                    // '=' precedes the count, because address keys contain spaces and ':'.
                    if (arg.Length == 0)
                    {
                        var scShared = SharedShopSchedule.ManagedShopsWithShifts();
                        var scLsb = new System.Text.StringBuilder("OK schedcount shared=[");
                        for (int i = 0; i < scShared.Count; i++)
                        {
                            if (i > 0) scLsb.Append('|');
                            scLsb.Append(scShared[i].addr).Append('=').Append(scShared[i].shifts);
                        }
                        return scLsb.Append(']').ToString();
                    }
                    var scReg = GameStatePatcher.FindRegistration(arg);
                    if (scReg == null) return $"ERR no registration for '{arg}'";
                    var scCounts = new int[8];   // index = DayOfWeekOrdered (Monday=1 .. Sunday=7); [0] unused
                    for (int i = 0; i < 8; i++) scCounts[i] = -1;
                    if (scReg.scheduleDays != null)
                        foreach (var sd in scReg.scheduleDays)
                        {
                            if (sd == null) continue;
                            int d = (int)sd.day;
                            if (d < 1 || d > 7) continue;
                            scCounts[d] = sd.workShifts != null ? sd.workShifts.Count : -1;
                        }
                    var scSb = new System.Text.StringBuilder();
                    scSb.Append("OK schedcount ").Append(arg);
                    for (int i = 1; i <= 7; i++) scSb.Append(" d").Append(i).Append('=').Append(scCounts[i]);
                    scSb.Append(" selfChanged=").Append(SharedShopSchedule.SelfChangedDays(arg));
                    scSb.Append(" touched=").Append(SharedShopSchedule.IsTouched(arg));
                    return scSb.ToString();
                }

                // ── H-SCHEDNULL-1 census (READ-ONLY) ────────────────────────
                // `schednull <addressKey>` counts shifts with NO employee id: in the registration's own
                // schedule days, and — only when the BizMan schedule page is on THIS business — inside
                // ScheduleHelper's two private cache dictionaries (reflection; a ghost cached before it was
                // nulled sits in BOTH, so one ghost counts twice).  cacheNull=-1 means "not this business's
                // page, nothing read".  The rig never opens the page, so it always reads -1 there.
                case "schednull":
                {
                    if (arg.Length == 0) return "ERR usage: schednull <addressKey>";
                    var snReg = GameStatePatcher.FindRegistration(arg);
                    if (snReg == null) return $"ERR no registration for '{arg}'";
                    int snDay = 0;
                    if (snReg.scheduleDays != null)
                        foreach (var sd in snReg.scheduleDays)
                        {
                            if (sd?.workShifts == null) continue;
                            foreach (var w in sd.workShifts) if (w != null && w.employeeId == null) snDay++;
                        }
                    int snCache = -1;
                    bool snOpen = false;
                    try
                    {
                        var snBiz = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.Business;
                        if (snBiz != null && ReferenceEquals(snBiz.buildingRegistration, snReg))
                        {
                            try { snOpen = SharedShopSchedule.IsScheduleTabOpenFor(snReg); } catch { }
                            snCache = 0;
                            var snT = typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper);
                            foreach (var snName in new[] { "WorkShiftsByWorkstationId", "WorkShiftsByEmployeeId" })
                            {
                                var snProp = HarmonyLib.AccessTools.Property(snT, snName);
                                if (snProp?.GetValue(null) is not System.Collections.IDictionary snDict) continue;
                                foreach (System.Collections.DictionaryEntry snEntry in snDict)
                                    if (snEntry.Value is System.Collections.IEnumerable snList)
                                        foreach (var snItem in snList)
                                            if (snItem is WorkShift snWs && snWs.employeeId == null) snCache++;
                            }
                        }
                    }
                    catch { snCache = -1; }
                    return $"OK schednull {arg} dayNull={snDay} cacheNull={snCache} pageOpen={snOpen}";
                }

                // ── H-SCHEDSTATION-1 (2026-09-27) schedule-row levers ─────────
                // `schedrows <addressKey> [day 1-7]` READ-ONLY: the station rows the schedule page builds for this
                // registration (BuildingRegistration.GetAssignableItems, what ScheduleHelper.FetchWorkstations reads),
                // the employees the page can colour (the FetchEmployees query: assigned to THIS address on this
                // machine), and every shift of the day with where its employee is assigned here and whether the page's
                // colour lookup (ScheduleHelper.GetEmployeeColor = EmployeesById[id]) would throw.  With the page open
                // on this registration it also lists the drawn rows as station:sliders/cachedShifts.
                // `schedtab <addressKey> <day>` opens the BizMan Schedule tab on that shop and day (the game's own
                // BizMan.Open(address, "Schedule") + that day's button).
                // `shiftat <addressKey> <day> <stationId|auto> <employeeId> <fromHour> <toHour|auto>` adds ONE shift
                // through ScheduleHelper.AddWorkShift - the call ScheduleEmployeeSelection makes when the player picks an
                // employee - opening the tab first if needed; a native throw is caught and reported (addErr=).
                case "schedrows": return SchedRows(arg);
                case "schedtab":  return SchedTab(arg);
                case "shiftat":   return ShiftAt(arg);
                // H-SCHEDSTATION-1 fold levers (DEV). `shiftdrag <addressKey> <day 1-7> <employeeId>`: the page's bar for that
                // employee's shift is dragged and dropped on its own row the way the pointer does (WorkShiftDrag.OnBeginDrag +
                // OnEndDrag onto a ScheduleCellHour of the same station), after a direct ScheduleHelper.HasSkillForWorkstation
                // call; reports the answer, any throw and whether the bar went back into its row. `daytoggle <addressKey>
                // <day 1-7>`: that day's open toggle flipped and flipped back (Toggle.isOn -> ScheduleDayButton.OnOpenToggleChange).
                case "shiftdrag": return ShiftDrag(arg);
                case "daytoggle": return DayToggle(arg);

                // ── round-238 zombie-ledger synthesis ─────────────────────────
                case "ledgerdrop":
                {
                    if (!MPServer.IsRunning) return "ERR host only";
                    if (arg.Length == 0) return "ERR addressKey required (e.g. '31 ba:street_fourthavenue')";
                    // Deliberately does NOT name the heal's log tag in this message — the RedRoc run
                    // proved a result string containing the tag text pollutes whole-log tag greps
                    // (the inert check counted this OK line as a heal emission).
                    return MPServer.BuildingOwners.TryRemove(arg, out var was)
                        ? $"OK ledger entry '{arg}' → '{was}' REMOVED (synthetic zombie; expect adoption after the owner's next claim report)"
                        : $"ERR no ledger entry for '{arg}'";
                }

                // ── round-249 radio-shield test: corrupt the station table, then look up ──
                // Reproduces the field damage (a mod-broken install's PARTIAL station table:
                // one key missing + the fill-once latch set) and immediately performs the
                // lookup that used to throw KeyNotFoundException through vehicle entry.
                // With the shield, the lookup's prefix unlatches and the native refill
                // repairs the table inside the same call — expect "OK healed" here plus a
                // "[Radio] station table PARTIAL" warning in the log. Without it: "ERR".
                case "radiobreak":
                {
                    var rp = InstanceBehavior<GameManager>.Instance?.radioPlayer;
                    if (rp == null) return "ERR no radioPlayer (load into the city first)";
                    var dataF   = HarmonyLib.AccessTools.Field(typeof(RadioPlayer), "_radioStationsData");
                    var loadedF = HarmonyLib.AccessTools.Field(typeof(RadioPlayer), "_isDataLoaded");
                    if (dataF?.GetValue(rp) is not System.Collections.Generic.Dictionary<RadioStation, RadioStationData> dict)
                        return "ERR could not read _radioStationsData";
                    var stations = (RadioStation[])Enum.GetValues(typeof(RadioStation));
                    var victim = stations[stations.Length - 1];
                    dict.Remove(victim);
                    loadedF!.SetValue(rp, true);           // the field latch: partial table, marked complete
                    int before = dict.Count;
                    try
                    {
                        var d = rp.GetRadioStationData(victim);   // the lookup that aborted vehicle entry in the field
                        int after = ((System.Collections.Generic.Dictionary<RadioStation, RadioStationData>)dataF.GetValue(rp)!).Count;
                        return d != null
                            ? $"OK healed: '{victim}' returned after corrupt ({before}->{after} stations)"
                            : $"ERR lookup returned null for '{victim}' ({before}->{after} stations)";
                    }
                    catch (Exception ex) { return $"ERR still throwing: {ex.GetType().Name} for '{victim}' ({before} stations)"; }
                }

                // ── round-253 mod-mismatch test fixture ───────────────────────
                // Imitation mods WITHOUT touching disk: both rig instances read the
                // SAME LocalLow ModsLocal folder, so a real folder there changes both
                // lists equally and can never produce a mismatch. This appends to the
                // CACHED list the Hello handshake sends. Run BEFORE the client joins —
                // the diff happens at Hello, so changes after connect need a rejoin.
                case "fakemod":
                {
                    if (arg.StartsWith("add ", StringComparison.Ordinal))
                    {
                        MPContentFingerprint.TestAddFakeMod(arg.Substring(4).Trim());
                        return $"OK mod list now: {MPContentFingerprint.CachedMods}";
                    }
                    if (arg == "clear")
                    {
                        MPContentFingerprint.TestClearFakeMods();
                        return $"OK mod list restored: {MPContentFingerprint.CachedMods}";
                    }
                    return "ERR usage: fakemod add <name> | fakemod clear";
                }

                // ── No-takeover package (user-approved 2026-08-18): rig runs must never
                // steal foreground.  Verbs replace the SendInput F-keys; the screenshot
                // renders from INSIDE the game, so an unfocused (not minimized) window
                // captures fine while the user keeps the machine. ──────────────────────
                case "trafficmode":
                {
                    // TRAFFIC-APART P9 test seam: pin one peer's (or every peer's) traffic verdict, or hand it back to
                    // the distance rule ("auto"). Nothing flips here - the host's own 0.2 s evaluator beat carries it out.
                    if (!MPServer.IsRunning) return "ERR host only";
                    var tf = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tf.Length != 3 || !string.Equals(tf[0], "force", StringComparison.OrdinalIgnoreCase))
                        return "ERR usage: trafficmode force <pid|all> <local|ghost|auto>";
                    string who = tf[1], forced = tf[2].ToLowerInvariant();
                    if (forced != "local" && forced != "ghost" && forced != "auto")
                        return "ERR usage: trafficmode force <pid|all> <local|ghost|auto>";
                    int matched = TrafficSync.HostForceTrafficMode(who, forced);
                    if (matched == 0) return $"ERR no connected peer matches '{who}'";
                    return $"OK trafficmode {who} {forced}";
                }

                case "tp":
                {
                    // TRAFFIC-APART P9 test seam: move the LOCAL player with the GAME's own teleport
                    // (PlayerHelper.Teleport(Transform) -> navmeshAgent.Warp), so a rig step can put two players far
                    // apart - or back together - without driving there. A temporary GameObject carries the target
                    // because that is the only signature the game exposes; it is destroyed straight after.
                    var ap = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    UnityEngine.Vector3 target;
                    if (ap.Length == 3
                        && float.TryParse(ap[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tx)
                        && float.TryParse(ap[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ty)
                        && float.TryParse(ap[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tz))
                        target = new UnityEngine.Vector3(tx, ty, tz);
                    else if (ap.Length == 1)
                    {
                        UnityEngine.Vector3? known = null;
                        try
                        {
                            // The host tracks its peers' bodies; a client tracks every OTHER player's, the host's included.
                            if (MPServer.IsRunning) { if (RemotePlayerManager.TryGetRemotePosition(ap[0], out var rp)) known = rp; }
                            else known = RemotePlayerManager.GetPlayerPosition(ap[0]);
                        }
                        catch { }
                        if (known == null) return $"ERR no known position for '{ap[0]}'";
                        target = known.Value;
                    }
                    else return "ERR usage: tp <pid> | tp <x> <y> <z>";

                    try { if (BuildingManager.IsInsideBuilding) return "ERR inside a building"; } catch { }
                    try { if (Helpers.VehicleHelper.IsInsideVehicle()) return "ERR in a vehicle"; } catch { }

                    // Review r1 MINOR-6: Tick already runs ON THE MAIN THREAD (MPCanvasUI.cs:699; see the comment at
                    // the foot of this file), so the warp happens here and now. The enqueued version replied OK before
                    // anything had happened; the reply below is the player's position read BACK after the warp.
                    UnityEngine.GameObject? probe = null;
                    try
                    {
                        probe = new UnityEngine.GameObject("BAMP_TpTarget");
                        probe.transform.position = target;
                        var pc = Helpers.PlayerHelper.PlayerController;
                        probe.transform.rotation = pc != null ? pc.transform.rotation : UnityEngine.Quaternion.identity;
                        Helpers.PlayerHelper.Teleport(probe.transform);
                        Plugin.Logger.LogInfo($"[TestDrive] tp -> ({target.x:F0}, {target.y:F0}, {target.z:F0}).");
                    }
                    catch (Exception ex) { return $"ERR tp: {ex.Message}"; }
                    finally { try { if (probe != null) UnityEngine.Object.Destroy(probe); } catch { } }

                    UnityEngine.Vector3 at;
                    try { at = Helpers.PlayerHelper.GetPosition(); }
                    catch (Exception ex) { return $"ERR tp: the player's position is unreadable ({ex.Message})"; }
                    if ((at - target).sqrMagnitude > 4f) return $"ERR tp: player still at {at.x:F0} {at.y:F0} {at.z:F0}";
                    return $"OK tp {at.x:F0} {at.y:F0} {at.z:F0}";
                }

                case "tproad":
                {
                    // H-CARSTACK-1 confirming test (2026-09-24, ADDED lever): 'tp', but the target is snapped to the NEAREST
                    // Gley traffic waypoint (by x/z) to the given point - i.e. onto a road lane Gley itself drives - and the
                    // player is turned to face along that lane. The waypoint table is the one the running manager uses
                    // (densityManager.gridManager.currentSceneData, as TrafficSync.PositionInGrid reads it - never
                    // CurrentSceneData.GetSceneInstance(), which can CREATE a scene object). Same teleport as 'tp'.
                    var rq = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (rq.Length != 2
                        || !float.TryParse(rq[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rx)
                        || !float.TryParse(rq[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rz))
                        return "ERR usage: tproad <x> <z>";
                    try { if (BuildingManager.IsInsideBuilding) return "ERR inside a building"; } catch { }
                    try { if (Helpers.VehicleHelper.IsInsideVehicle()) return "ERR in a vehicle"; } catch { }

                    UnityEngine.Vector3 wpPos = default, wpDir = UnityEngine.Vector3.zero;
                    int wpIdx = -1;
                    float bestD2 = float.MaxValue;
                    try
                    {
                        var rtm = GleyTrafficSystem.TrafficManager.HasInstance ? GleyTrafficSystem.TrafficManager.Instance : null;
                        var wps = rtm?.densityManager?.gridManager?.currentSceneData?.allWaypoints;
                        if (wps == null || wps.Length == 0) return "ERR tproad: no Gley waypoint table";
                        for (int i = 0; i < wps.Length; i++)
                        {
                            var w = wps[i];
                            if (w == null || w.temporaryDisabled) continue;
                            float ddx = w.position.x - rx, ddz = w.position.z - rz;
                            float d2 = ddx * ddx + ddz * ddz;
                            if (d2 < bestD2) { bestD2 = d2; wpPos = w.position; wpIdx = i; }
                        }
                        if (wpIdx >= 0)
                        {
                            var nb = wps[wpIdx].neighbors;
                            if (nb != null && nb.Count > 0 && nb[0] >= 0 && nb[0] < wps.Length && wps[nb[0]] != null)
                                wpDir = wps[nb[0]].position - wpPos;
                        }
                    }
                    catch (Exception ex) { return $"ERR tproad: {ex.Message}"; }
                    if (wpIdx < 0) return "ERR tproad: no usable waypoint";

                    UnityEngine.GameObject? rprobe = null;
                    try
                    {
                        rprobe = new UnityEngine.GameObject("BAMP_TpRoadTarget");
                        rprobe.transform.position = wpPos;
                        wpDir.y = 0f;
                        var rpc = Helpers.PlayerHelper.PlayerController;
                        rprobe.transform.rotation = wpDir.sqrMagnitude > 0.01f ? UnityEngine.Quaternion.LookRotation(wpDir.normalized)
                                                  : (rpc != null ? rpc.transform.rotation : UnityEngine.Quaternion.identity);
                        Helpers.PlayerHelper.Teleport(rprobe.transform);
                        Plugin.Logger.LogInfo($"[TestDrive] tproad -> waypoint {wpIdx} ({wpPos.x:F0}, {wpPos.y:F0}, {wpPos.z:F0}), {UnityEngine.Mathf.Sqrt(bestD2):F1} m from ({rx:F0}, {rz:F0}).");
                    }
                    catch (Exception ex) { return $"ERR tproad: {ex.Message}"; }
                    finally { try { if (rprobe != null) UnityEngine.Object.Destroy(rprobe); } catch { } }

                    UnityEngine.Vector3 rat;
                    try { rat = Helpers.PlayerHelper.GetPosition(); }
                    catch (Exception ex) { return $"ERR tproad: the player's position is unreadable ({ex.Message})"; }
                    float miss = new UnityEngine.Vector2(rat.x - wpPos.x, rat.z - wpPos.z).magnitude;
                    if (miss > 6f) return $"ERR tproad: player at {rat.x:F1} {rat.y:F1} {rat.z:F1}, waypoint {wpIdx} at {wpPos.x:F1} {wpPos.y:F1} {wpPos.z:F1} (miss {miss:F1} m)";
                    return $"OK tproad {rat.x:F1} {rat.y:F1} {rat.z:F1} wp={wpIdx} snap={UnityEngine.Mathf.Sqrt(bestD2):F1} miss={miss:F1}";
                }

                case "enterbuilding":
                {
                    if (arg.Length == 0) return "ERR address key required (e.g. '29 ba:street_thirdstreet')";
                    string addr = arg;
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        try
                        {
                            var bm = InstanceBehavior<BuildingManager>.Instance;
                            if (bm == null) { Plugin.Logger.LogWarning("[TestDrive] enterbuilding: no BuildingManager."); return; }
                            if (bm.enteringBuilding || bm.exitingBuilding) { Plugin.Logger.LogWarning("[TestDrive] enterbuilding: a building transition is already running — re-issue the verb."); return; }
                            if (BuildingManager.IsInsideBuilding) { Plugin.Logger.LogWarning("[TestDrive] enterbuilding: already inside a building — 'exitbuilding' first."); return; }
                            var cm = InstanceBehavior<CityManager>.Instance;
                            bool found = false;
                            if (cm?.cityBuildingControllers != null)
                                foreach (var c in cm.cityBuildingControllers)
                                    if (c?.building != null && GameStateReader.AddressKey(c.building) == addr)
                                    {
                                        found = true;
                                        // Same native entry the passenger-follow uses (PassengerRide): the
                                        // full door flow — interior load, staffing, positioning. (1.0 dropped the force arg.)
                                        bool ok = bm.EnterBuilding(c.building, false, false, 0, -1);   // 1.0 dropped 'force'
                                        Plugin.Logger.LogInfo($"[TestDrive] enterbuilding '{addr}' → {(ok ? "OK" : "REFUSED by native")}.");
                                        break;
                                    }
                            if (!found) Plugin.Logger.LogWarning($"[TestDrive] enterbuilding: no building resolves to '{addr}'.");
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[TestDrive] enterbuilding: {ex.Message}"); }
                    });
                    return $"OK enterbuilding('{arg}') queued — result in log ([TestDrive] enterbuilding line)";
                }
                case "exitbuilding":
                {
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        try
                        {
                            var bm = InstanceBehavior<BuildingManager>.Instance;
                            if (bm == null || !BuildingManager.IsInsideBuilding) { Plugin.Logger.LogInfo("[TestDrive] exitbuilding: not inside a building."); return; }
                            if (bm.enteringBuilding || bm.exitingBuilding) { Plugin.Logger.LogWarning("[TestDrive] exitbuilding: a transition is already running — re-issue the verb."); return; }
                            bm.ExitFromBuilding(0);
                            Plugin.Logger.LogInfo("[TestDrive] exitbuilding invoked.");
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[TestDrive] exitbuilding: {ex.Message}"); }
                    });
                    return "OK exitbuilding queued — result in log";
                }
                case "rain":
                {
                    bool on  = arg.Trim().Equals("on",  StringComparison.OrdinalIgnoreCase);
                    bool off = arg.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
                    if (!on && !off) return "ERR usage: rain on|off";
                    GameStatePatcher.EnqueueOnMainThread(() => MPWeatherSync.TryForceRain(on));
                    return $"OK rain {(on ? "on" : "off")} queued (same apply path as the F7 key)";
                }
                case "screenshot":
                {
                    string name = arg.Length == 0 ? DateTime.Now.ToString("HHmmss") : arg;
                    string path = Path.Combine(_dir ?? Path.Combine(MPConfig.DataRootPath, "testdrive"), "shot-" + name + ".png");
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        try
                        {
                            // Reflection, not a csproj module reference (the Ctrl+F9 console
                            // precedent): ScreenCapture lives in UnityEngine.ScreenCaptureModule.
                            var sc = HarmonyLib.AccessTools.TypeByName("UnityEngine.ScreenCapture");
                            var m  = sc == null ? null : HarmonyLib.AccessTools.Method(sc, "CaptureScreenshot", new[] { typeof(string) });
                            if (m == null) { Plugin.Logger.LogWarning("[TestDrive] screenshot: ScreenCapture.CaptureScreenshot not resolvable."); return; }
                            m.Invoke(null, new object[] { path });
                            Plugin.Logger.LogInfo($"[TestDrive] screenshot → {path} (file lands at end of frame; a minimized window does not render — keep it unfocused, not minimized).");
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[TestDrive] screenshot: {ex.Message}"); }
                    });
                    return $"OK screenshot queued → {path}";
                }


                // -- merger phase 0 test levers (2026-09-10) -------------------
                case "merge":
                {
                    // Thin wrapper over the merger chips in MPCanvasUI (:2543-2645): on the HOST
                    // the chip calls MPServer.HostMergerAction directly with its OWN pid as the
                    // actor; on a client it sends MPClient.SendMergerAction. Same two calls here.
                    var mtk = arg.Split(' ');
                    string mact = mtk.Length > 0 ? mtk[0].ToLowerInvariant() : "";
                    string mpid = mtk.Length > 1 ? string.Join(" ", mtk, 1, mtk.Length - 1).Trim() : "";
                    if (mact != "propose" && mact != "accept" && mact != "decline" && mact != "leave" && mact != "unpropose")
                        return "ERR merge propose <pid>|accept|decline|leave|unpropose";
                    if (mact == "propose" && mpid.Length == 0) return "ERR 'merge propose' needs a target pid";
                    if (mact != "propose") mpid = "";        // every other chip sends an empty target
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR not in a multiplayer session";
                    // r4: a THIN wrapper again - the button writes no UI state either. Both offer fields are
                    // derived from the host's broadcast offer table, so the verb just makes the same call.
                    if (MPServer.IsRunning) MPServer.HostMergerAction(mact, mpid, MPConfig.PlayerId);
                    else                    MPClient.SendMergerAction(mact, mpid);
                    return $"OK merge {mact} sent" + (mpid.Length > 0 ? $" -> '{mpid}'" : "");
                }

                case "mergestatus":
                {
                    var gsb = new StringBuilder("OK ");
                    gsb.Append($"member={MergerSync.IAmMember} group='{MergerSync.MyGroupId}' members=[");
                    int gm = 0;
                    try { foreach (var m in MergerSync.MemberNames) { if (gm++ > 0) gsb.Append(','); gsb.Append(m); } } catch { }
                    gsb.Append($"] flipped={MergerFlip.FlippedCount} keys=[");
                    // MergerFlip keeps its key->runner map private and a test driver may not widen
                    // another module's surface: the group's building keys filtered by IsFlipped is
                    // the same set, read-only.
                    int gk = 0;
                    try
                    {
                        foreach (var k in MergerSync.MyGroupBuildingKeys)
                        {
                            if (!MergerFlip.IsFlipped(k)) continue;
                            if (gk++ > 0) gsb.Append(',');
                            gsb.Append(QT).Append(k).Append(QT);
                            if (gk >= 10) break;
                        }
                    }
                    catch { }
                    gsb.Append(']');
                    // Phase 1-A: the company's identity - display name, founder pid, join order.
                    gsb.Append($" name='{MergerSync.MyGroupDisplayName}' founder={MergerSync.MyGroupFounderPid} order=[");
                    int go = 0;
                    try { foreach (var om in MergerSync.MyMemberNamesOrdered) { if (go++ > 0) gsb.Append(','); gsb.Append(om); } } catch { }
                    gsb.Append(']');
                    return gsb.ToString();
                }

                case "walletdump":
                {
                    // WALLET-DUPE-1 (W6). HOST-ONLY: the shared-wallet LEDGER and the CONTRIBUTED-SET live on
                    // the host (MPServer._walletBalance / _walletContributed, reachable through the two
                    // snapshot accessors); a client holds only its own mirror, which the 'money' verb prints.
                    // contributed=<k>/<members> is the pooling guard against the group's roster size: k<members
                    // after a load means a member can still pool again, which is the double-money shape.
                    if (!MPServer.IsRunning) return "ERR host only";
                    var wbal = MPServer.SnapshotWalletBalances();
                    var wcon = MPServer.SnapshotWalletContributed();
                    var wkeys = new System.Collections.Generic.List<string>(wbal.Keys);
                    wkeys.Sort(StringComparer.Ordinal);
                    var wsb = new StringBuilder("OK walletdump groups=[");
                    for (int wi = 0; wi < wkeys.Count; wi++)
                    {
                        string wg = wkeys[wi];
                        if (wi > 0) wsb.Append(';');
                        int wmem = MergerSync.StoreGroups.TryGetValue(wg, out var wset) && wset != null ? wset.Count : 0;
                        int wk   = wcon.TryGetValue(wg, out var wlist) && wlist != null ? wlist.Count : 0;
                        wsb.Append(wg).Append(":balance=")
                           .Append(wbal[wg].ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                           .Append(" contributed=").Append(wk).Append('/').Append(wmem);
                    }
                    wsb.Append(']');
                    return wsb.ToString();
                }

                case "books":
                {
                    // MERGER PHASE 4a (B6). Read-only. With no argument: how much of the company
                    // books this machine currently has overlaid on its own day records. With an
                    // address key: that address's newest statement and whose books it came from.
                    return CompanyBooks.TestDriveLine(arg);
                }

                case "feed":
                {
                    // MERGER PHASE 4b (R6). Read-only. How many entries this machine's own
                    // transaction queue holds, how many partner rows the mod-side registry holds, and
                    // the newest of the two; with <n>, the newest n rows of the merged view and the
                    // pid each belongs to ('own' = this machine's). Nothing is written, and nothing
                    // the verb reports is ever in gi.Transactions except this machine's own.
                    return CompanyFeed.TestDriveLine(arg);
                }

                case "lists":
                {
                    // MERGER PHASE 2 WAVE 4 (V4). Read-only. With no argument: how many owners' DISPLAY
                    // COPIES this machine holds and how many tagged items are installed for them; with an
                    // owner pid: that owner's contract/plan counts. Nothing is written.
                    return CompanyLists.TestDriveLine(arg);
                }

                case "plans":
                {
                    // MERGER PHASE 4c part 1 (H5). Read-only. With no argument: how many headquarters plans
                    // of each family this machine owns, and how many each partner has published to the
                    // screen-layer registry. With an owner pid: that owner's five counts. With an HQ address
                    // key: that headquarters' plans by family and whose they are. With `<ownerPid> rows
                    // [family]`: that owner's HELD ROWS as <family>:<planId>:<name>, which is where the plan
                    // ids `planedit` needs come from. Nothing is written.
                    return CompanyPlans.TestDriveLine(arg);
                }

                case "hqcards":
                {
                    // HQ-PARITY-1 P8. Read-only. What the BizMan hub's headquarters list would DRAW here:
                    // how many cards survive the two filters, and which headquarters (and whose) backs the
                    // company's single card. Same CompanyPlans.BackingHq the hub's own prefix uses.
                    return CompanyPlans.HqCardsLine();
                }

                case "hqtarget":
                {
                    // HQ-PARITY-1 P8 - TEST LEVER. `hqtarget <planId> <itemName> <amount>` changes a target on
                    // one of THIS machine's OWN purchasing plans, where the pane's own ChangeTarget writes it,
                    // and marks the bundle urgent exactly as the pane commit does. Every other plan lever
                    // routes to another machine; this one is the owner's own edit, which is what P5 is about.
                    return CompanyPlans.TestDriveHqTarget(arg);
                }

                case "hqlog":
                {
                    // HQ-PARITY-2 P4 - TEST LEVER. `hqlog <planId> <op> [args]` drives one LOGISTICS control
                    // exactly where the pane's own control drives it: on a PARTNER's display copy it sends the
                    // single op leg (the same one the pane's edit diffs into), on one of this machine's OWN
                    // plans it runs the same runner body and marks the bundle urgent. Ops: manager, warehouse,
                    // destadd, destremove, destchange, target.
                    return CompanyPlans.TestDriveHqLog(arg);
                }

                case "hqtoggle":
                {
                    // HQ-PARITY-3 B6 - TEST LEVER. `hqtoggle <family> <planId> <field> [<arg>] <value>` makes
                    // one CONTROL's own write on one of THIS machine's OWN headquarters plans and takes the
                    // same commitment seam the patched pane controls take, so the rig can prove the owner-side
                    // publish per control: purchasing repeating|autostock, hr replaceabsent|trainingtarget,
                    // headhunter dealbreaker <type>.
                    return CompanyPlans.TestDriveHqToggle(arg);
                }

                case "hqpane":
                {
                    // HQ-PARITY-4 P5 - TEST LEVER. Read-only. What the mod believes about each of the five
                    // headquarters pages right now: the list object and the pane object it would use for that
                    // family (registered by the game's own draw, found by the hierarchy sweep, or missing),
                    // whether each is on screen, and the plan the pane has open. The rig draws no page, so
                    // this is the hands-on check - above all that the purchasing pane is now found at all.
                    return CompanyPlans.TestDriveHqPane(arg);
                }

                case "planedit":
                {
                    // MERGER PHASE 4c part 2a (E5). `planedit <family> <planId> <op> [args]` sends exactly the
                    // leg the pane would send for one of the four non-logistics HQ families, off the temp row
                    // the registry holds for a PARTNER's headquarters. It writes nothing on this machine:
                    // the runner of that headquarters applies the op and the next fan-out redraws the rows.
                    // Families: pricing | purchasing | hr | headhunter. Ops: see CompanyPlans.ApplyRouted.
                    return CompanyPlans.TestDriveEdit(arg);
                }

                case "contract":
                {
                    // MERGER PHASE 2 WAVE 4 (V4) - TEST LEVER. `contract <bizAddr> <wholesaleAddr>` sends the
                    // V2a route exactly as the wholesale dialog does. The OPERATOR re-runs the duplicate and
                    // shelf pre-checks; nothing is created here. Refuses off a merger, as the gate does.
                    var cargs = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (cargs.Length < 2) return "ERR usage: contract <businessAddressKey> <wholesaleAddressKey>";
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (!MergerFlip.IsFlipped(cargs[0])) return $"ERR '{cargs[0]}' is not a merger-flipped company building here";
                    SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                    { PlayerId = MPConfig.PlayerId, AddressKey = cargs[0], Op = "mergercontract", StrValue = cargs[1] });
                    return $"OK contract create routed for '{cargs[0]}' (wholesale '{cargs[1]}')";
                }

                case "mergercampaign":
                {
                    // H-MERGERHIRE-1 - TEST LEVER. `mergercampaign <shopNum> <ba:street_x> <agencyNum>
                    // <ba:street_y> <ba:skill_z> <candidates> <days>` sends exactly the leg the recruitment
                    // dialog sends for a merger-flipped partner shop. Address keys are TWO tokens each, like
                    // every other lever. Nothing is charged or added here: the OWNER clamps the numbers to its
                    // own sliders, recomputes the price and pays. Full-time is sent on, part-time off, and no
                    // quote is sent (Estimate 0), so the owner's own figure stands unremarked.
                    var kargs = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (kargs.Length < 7)
                        return "ERR usage: mergercampaign <num> <ba:street_x> <num> <ba:street_y> <ba:skill_z> <candidates> <days>";
                    string kshop = kargs[0] + " " + kargs[1], kagency = kargs[2] + " " + kargs[3];
                    if (!int.TryParse(kargs[5], out var kcand) || !int.TryParse(kargs[6], out var kdays))
                        return "ERR usage: mergercampaign <num> <ba:street_x> <num> <ba:street_y> <ba:skill_z> <candidates> <days>";
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (!MergerFlip.IsFlipped(kshop)) return $"ERR '{kshop}' is not a merger-flipped company building here";
                    SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                    {
                        PlayerId = MPConfig.PlayerId, AddressKey = kshop, Op = "mergercampaign", AgencyKey = kagency,
                        SkillName = kargs[4], IntValue = kcand, BoolValue = true, PartTime = false, Days = kdays,
                    });
                    return $"OK campaign booking routed for '{kshop}' (agency '{kagency}', {kargs[4]}, {kcand} candidates, {kdays} days)";
                }

                case "campaigns":
                {
                    // H-MERGERHIRE-1 - TEST LEVER. Read-only: this machine's own recruitment campaigns, the list
                    // the hourly tick works through (SaveGameManager.Current.RecruitmentCampaigns). The rig reads
                    // it on the OWNER to see a routed booking land.
                    // H-MERGERCAMPAIGN-1 - TWO MORE READ-ONLY SUB-VERBS.
                    //   `campaigns mirror [<agency key>]` - the MEMBER's view of a merged partner's
                    //     running campaigns (CampaignMirror's in-memory registry, never a save list).
                    //     With an agency key it also materialises that agency's rows through the real
                    //     DTO -> transient path, which is everything the screen layer does short of the
                    //     draw itself, so the rig exercises the conversion without a dialog.
                    //   `campaigns agencies` - this machine's recruitment-agency address keys.  Added
                    //     because no existing lever can name an agency, and the rig has to discover one
                    //     at runtime to book against it.
                    if (arg.StartsWith("mirror", StringComparison.OrdinalIgnoreCase))
                        return CampaignMirror.TestDriveLine(arg.Substring(6).Trim());
                    if (string.Equals(arg, "agencies", StringComparison.OrdinalIgnoreCase))
                        return CampaignMirror.AgenciesLine();
                    var kgi = SaveGameManager.Current;
                    if (kgi == null) return "ERR no save loaded";
                    var klist = kgi.RecruitmentCampaigns;
                    if (klist == null) return "campaigns=0 []";
                    var krows = new System.Collections.Generic.List<string>();
                    foreach (var c in klist)
                    {
                        if (c == null) continue;
                        string kbiz = "";
                        try { kbiz = Helpers.BuildingHelper.GetBuildingRegistration(c.businessAddress)?.BusinessName ?? ""; } catch { }
                        krows.Add($"{kbiz}|{c.skillRequirement?.skillName ?? ""}|{c.amountOfCandidates}|{c.finished}");
                    }
                    return $"campaigns={krows.Count} [{string.Join(";", krows)}]";
                }

                case "sellall":
                {
                    // MERGER PHASE 2 WAVE 4 (V4, r2 D21) - TEST LEVER. Sell-All is ACCURATE BY CONSTRUCTION:
                    // the runner sells only at a figure IT quoted. `sellall <warehouseAddr>` therefore asks
                    // for the quote (the answer opens the game's own confirmation, exactly as the click
                    // does), and `sellall <warehouseAddr> <quote>` routes that number straight through the
                    // equality gate - the way to exercise a STALE quote from the rig. Nothing is sold here.
                    // Address keys are TWO tokens ('46 ba:street_fourthstreet'), like every other lever.
                    var sargs = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (sargs.Length < 2) return "ERR usage: sellall <num> <ba:street_x> [quote]";
                    string waddr = sargs[0] + " " + sargs[1];
                    sargs = sargs.Length > 2 ? new[] { waddr, sargs[2] } : new[] { waddr };
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (!MergerFlip.IsFlipped(waddr)) return $"ERR '{waddr}' is not a merger-flipped company building here";
                    if (sargs.Length == 1)
                    {
                        SharedShopWorkTabs.AskSellQuote(waddr);
                        return $"OK sell-all quote asked for '{waddr}' (the answer raises the game's own confirmation)";
                    }
                    if (!float.TryParse(sargs[1], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var squote))
                        return "ERR usage: sellall <num> <ba:street_x> [quote]";
                    SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                    { PlayerId = MPConfig.PlayerId, AddressKey = waddr, Op = "mergersellall", Estimate = squote });
                    return $"OK sell-all routed for '{waddr}' at quote {squote} (the runner sells only if its own total still equals it)";
                }

                case "blip":
                {
                    // MERGER PHASE 5 (P11, D25) - TEST LEVER. The transport has NO re-connectable loss seam:
                    // OnDisconnected stops the poll loop and MPClient keeps no host address to rejoin with, so
                    // a real drop cannot be undone from the rig. This drives the seam the drop ARMS instead -
                    // the very MergerSync.ArmDropGrace the non-voluntary disconnect path calls - with the link
                    // read as down for N seconds. Nothing about the socket is touched.
                    //   `blip`   -> the link stays down: the view DROPS after the 3 s grace.
                    //   `blip 1` -> back inside the grace: the view is KEPT.
                    //   `blip 6` -> back after it: dropped at 3 s, rebuilt by the next MergerState.
                    if (!MergerSync.IAmMember) return "ERR not in a merged company here";
                    float bsecs = 99f;
                    if (arg.Length > 0 && !float.TryParse(arg, System.Globalization.NumberStyles.Float,
                                                          System.Globalization.CultureInfo.InvariantCulture, out bsecs))
                        return "ERR usage: blip [seconds]";
                    MergerSync.ArmDropGrace("test lever", bsecs);
                    return $"OK blip armed: the link reads as down for {bsecs:0.#} s - watch [Merger] view dropped / view kept";
                }

                case "netdrop":
                {
                    // H-STANDINTILL-2 (DEV lever, test only): an INVOLUNTARY link loss IN-PROCESS - the path a real network
                    // blip takes (MPClient.OnDisconnected with the voluntary flag false: MergerAbsence.Reset, the drop grace,
                    // the disconnect save), then the socket is closed so the host sees the departure. The rig's own drop
                    // closes the whole game, which a blip does not. Rejoin with `join`.
                    if (MPServer.IsRunning) return "ERR client only";
                    if (!MPClient.IsConnected) return "ERR not connected";
                    try
                    {
                        var ndM = typeof(MPClient).GetMethod("OnDisconnected",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        if (ndM == null) return "ERR MPClient.OnDisconnected not found";
                        ndM.Invoke(null, new object?[] { "test lever netdrop", null, null });
                        MPClient.Disconnect();
                    }
                    catch (Exception exNd) { return "ERR netdrop: " + (exNd.InnerException?.Message ?? exNd.Message); }
                    return "OK netdrop: the involuntary drop path ran and the link is closed - rejoin with 'join'";
                }

                case "terminate":
                {
                    // MERGER PHASE 5 (P11, D27) - TEST LEVER. Sends the leg the member's terminate CONFIRM
                    // sends. Address keys are TWO tokens ('46 ba:street_fourthstreet'), like every other lever.
                    // Nothing is written here: the runner sells the interior and returns the deposit there.
                    var targs = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (targs.Length < 2) return "ERR usage: terminate <num> <ba:street_x>";
                    string taddr = targs[0] + " " + targs[1];
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (!MergerSync.IAmMember) return "ERR not in a merged company here";
                    if (!MergerFlip.IsFlipped(taddr)) return $"ERR '{taddr}' is not a merger-flipped company building here";
                    SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                    { PlayerId = MPConfig.PlayerId, AddressKey = taddr, Op = "mergerterminate" });
                    return $"OK terminate-rental routed for '{taddr}' (the runner sells the interior and returns the deposit)";
                }

                case "shutdown":
                {
                    // MERGER PHASE 5 (P11, D29) - TEST LEVER. Sends the leg a LIVE member's Shutdown press
                    // sends. A stand-in is refused by the patch, not here, so the rig can drive both cases.
                    var shargs = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (shargs.Length < 2) return "ERR usage: shutdown <num> <ba:street_x>";
                    string shaddr = shargs[0] + " " + shargs[1];
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (!MergerSync.IAmMember) return "ERR not in a merged company here";
                    if (!MergerFlip.IsFlipped(shaddr)) return $"ERR '{shaddr}' is not a merger-flipped company building here";
                    SharedShopWorkTabs.SendEdit(new SharedWorkEditPayload
                    { PlayerId = MPConfig.PlayerId, AddressKey = shaddr, Op = "mergershutdown" });
                    return $"OK shutdown routed for '{shaddr}' (the runner closes the business with the game's own teardown)";
                }

                case "spend":
                {
                    // MERGER PHASE 4b (r4) - TEST LEVER. The world can go minutes without producing a
                    // transaction, so the rig needs one on demand. This makes ONE real money movement
                    // through the game's OWN path: GameManager.ChangeMoneySafe (GameManager.cs:1096)
                    // calls ChangeMoney (:1114), which builds the native Transaction and Enqueues it
                    // (:1136-1145) - so the merger wallet forward and CompanyFeed.CaptureOwn see it
                    // exactly as they see a world-produced one. No mod-only shortcut anywhere.
                    // SIGN: `spend 100` costs 100 (delta -100, type ba:transaction_licensingfee);
                    // `spend -100` earns 100 (delta +100, type ba:transaction_businessinventorysold).
                    // Both are `delta = -amount`. TransactionInfo(string type, bool isTaxDeductible=false)
                    // (TransactionInfo.cs:35) is the simplest valid constructor - ChangeMoney's only use
                    // of Categories is null-guarded (:1121-1122).
                    // MAIN THREAD: TestDrive.Tick runs from MPCanvasUI (MPCanvasUI.cs:698), so the
                    // movement happens here and the balance after it can be reported in the same line.
                    var spgi = SaveGameManager.Current;
                    if (spgi == null) return "ERR spend: no game instance";
                    if (arg.Length == 0) return "ERR spend: usage: spend <amount> (positive spends, negative earns)";
                    if (!float.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float spAmt))
                        return $"ERR spend: '{arg.Trim()}' is not a number";
                    if (spAmt == 0f) return "ERR spend: amount must be non-zero (ChangeMoney returns on 0)";

                    float spDelta = -spAmt;
                    var spInfo = new TransactionInfo(spDelta < 0f ? "ba:transaction_licensingfee"
                                                                 : "ba:transaction_businessinventorysold");
                    bool spOk;
                    try { spOk = GameManager.ChangeMoneySafe(spDelta, spInfo); }
                    catch (Exception spEx) { return $"ERR spend: {spEx.Message}"; }
                    if (!spOk) return $"ERR spend: refused, balance {spgi.Money.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} will not cover it";

                    return $"OK spend amount={spDelta.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}"
                         + $" money={spgi.Money.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}";
                }

                case "dissolvecheck":
                case "dissolverun":
                {
                    // DISSOLVE (2026-09-12): what the teardown WOULD change right now (dissolvecheck, count
                    // only - nothing written and nothing logged), or a FORCED teardown (dissolverun) that
                    // answers what it actually changed. Both run the SAME predicates. The ex-partner set is
                    // built from the live merger surfaces - my co-members plus the owners of the injected
                    // copies standing here - so the check is meaningful WHILE merged, which is the only state
                    // worth testing; an empty set would read as "anyone who is not me" and the routine's own
                    // live-membership guard would then answer zero to everything. Idempotent: a second run
                    // answers all zeros.
                    var dsvEx = MergerDissolve.LeverPartners();
                    var dsv = verb == "dissolverun" ? MergerDissolve.RunCounted("lever", dsvEx)
                                                    : MergerDissolve.Check(dsvEx);
                    return $"OK {verb} logistics={dsv.Logistics} contracts={dsv.Contracts} hrlist={dsv.HrList}"
                         + $" hrtag={dsv.HrTag} offers={dsv.Offers} copies={dsv.Copies}";
                }

                case "absence":
                {
                    // Merger phase 3-B (B6). On the HOST the marks come from the live table; on any
                    // other machine from the MergerState broadcast. simulating_here is what THIS
                    // machine actually runs for an absent owner. Read-only - no write path of its own.
                    // Phase 3-C (C6): the HOST also prints the last return it sent this session
                    // (returned=[...]) and every other machine the addresses a return actually replaced
                    // here (replaced=[...]).
                    return MergerAbsence.TestDriveLine();
                }

                case "warehouses":
                {
                    // W3-0 r1 (F9). Read-only. Every Entities.Warehouse registration ON THIS MACHINE that any
                    // merger surface can touch, grouped by WHO RUNS IT HERE: own = TrulyMine; flipped = a
                    // partner's copy the merger flipped and nobody here simulates; simulated = SimulatesHere
                    // (an absent owner's warehouse this machine stands in for). A warehouse that is none of
                    // the three (a rival's, an empty one) is not a merger surface and is not listed.
                    var wOwn = new System.Collections.Generic.List<string>();
                    var wFlip = new System.Collections.Generic.List<string>();
                    var wSim = new System.Collections.Generic.List<string>();
                    try
                    {
                        var wgi = SaveGameManager.Current;
                        if (wgi?.BuildingRegistrations == null) return "ERR no building registrations";
                        foreach (var wr in wgi.BuildingRegistrations)
                        {
                            if (!(wr is Entities.Warehouse)) continue;
                            string wk = ""; try { wk = GameStateReader.AddressKey(wr); } catch { }
                            if (wk.Length == 0) continue;
                            bool wsim = false; try { wsim = MergerAbsence.SimulatesHere(wk); } catch { }
                            if (MergerFlip.TrulyMine(wr)) wOwn.Add(wk);
                            else if (wsim) wSim.Add(wk);
                            else if (MergerFlip.IsFlipped(wk)) wFlip.Add(wk);
                        }
                    }
                    catch (Exception exW) { return "ERR " + exW.Message; }
                    wOwn.Sort(StringComparer.Ordinal); wFlip.Sort(StringComparer.Ordinal); wSim.Sort(StringComparer.Ordinal);
                    string WList(System.Collections.Generic.List<string> l)
                    {
                        var wsb = new StringBuilder("[");
                        for (int i = 0; i < l.Count; i++) { if (i > 0) wsb.Append(','); wsb.Append('\'').Append(l[i]).Append('\''); }
                        return wsb.Append(']').ToString();
                    }
                    return $"OK warehouses own={WList(wOwn)} flipped={WList(wFlip)} simulated={WList(wSim)}";
                }

                case "paperwork":
                {
                    // Merger phase 3-A. No argument on the HOST = the store census; "push" on ANY
                    // member forces one publish now. A thin wrapper over the same calls the tick
                    // makes - the verb adds no write path of its own.
                    string pwArg = arg.Trim();
                    if (string.Equals(pwArg, "push", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!MergerSync.IAmMember) return "ERR not a merger member";
                        var pushed = PaperworkSync.FlushNow("testdrive");
                        if (pushed == null) return "ERR paperwork push produced nothing (no session, or the bundle was refused)";
                        int pwBytes = 0;
                        try { pwBytes = System.Text.Encoding.UTF8.GetByteCount(Newtonsoft.Json.JsonConvert.SerializeObject(pushed)); } catch { }
                        return $"OK paperwork pushed day={pushed.Day} businesses={pushed.Businesses.Count}"
                             + $" lists={PaperworkSync.CountListItems(pushed.Lists)} employees={pushed.Employees.Count} bytes={pwBytes}";
                    }
                    if (pwArg.Length > 0 && !MPServer.IsRunning) return "ERR 'paperwork <stableid>' is a host verb";
                    if (!MPServer.IsRunning) return "ERR paperwork: not the host (use 'paperwork push' on a member)";
                    return $"OK paperwork stored=[{MPServer.PaperworkCensus(pwArg)}]";
                }

                // -- merger phase 1 test levers (2026-09-11) -------------------
                case "rivals":
                {
                    // The DATA half of RivalLeaderboard.Load (decompile :26-29) replayed headless
                    // with the phase 1-B fold gate on: GetAllRivalData() -> per-rival
                    // GetRivalLeaderboardData -> GetPlayerLeaderboardData() appended -> the same
                    // descending-weeklyIncome sort.  No UI rows, no writes of its own (the mapper
                    // may defeat a bankrupt rival exactly as the native Load does on every open).
                    // Reflection throughout: the rivals UI types are not csproj-referenced, so
                    // MPPatches resolves them by name too (:1694).
                    var lt  = HarmonyLib.AccessTools.TypeByName("UI.Smartphone.Apps.Rivals.RivalLeaderboard");
                    var lgr = lt == null ? null : HarmonyLib.AccessTools.Method(lt, "GetRivalLeaderboardData");  // public static, RivalData
                    var lgp = lt == null ? null : HarmonyLib.AccessTools.Method(lt, "GetPlayerLeaderboardData"); // PRIVATE static, no args
                    if (lgr == null || lgp == null) return "ERR RivalLeaderboard.GetRivalLeaderboardData/GetPlayerLeaderboardData not resolvable";
                    // One row = { entryName, rivalId|me, weeklyIncome, biz count, bldg count, isDefeated }.
                    object[] LRow(object o)
                    {
                        var ot = o.GetType();
                        object Fv(string n) { var f = HarmonyLib.AccessTools.Field(ot, n); return f == null ? null : f.GetValue(o); }
                        string lid = (Fv("rivalId") as string) ?? "";
                        var lbiz = Fv("ownedBusinesses") as System.Collections.ICollection;
                        var lbld = Fv("ownedBuildings")  as System.Collections.ICollection;
                        return new object[]
                        {
                            (Fv("entryName") as string) ?? "",
                            lid.Length == 0 ? "me" : lid,
                            (Fv("weeklyIncome") is float lw ? lw : 0f),
                            lbiz == null ? 0 : lbiz.Count,
                            lbld == null ? 0 : lbld.Count,
                            (Fv("isDefeated") is bool lf && lf)
                        };
                    }
                    var lrows = new System.Collections.Generic.List<object[]>();
                    try
                    {
                        GameStatePatcher.RivalsLeaderboardLoadRunning = true;
                        var lall = BigAmbitions.Rivals.RivalsHelper.GetAllRivalData();
                        if (lall != null)
                            foreach (var lr in lall)
                            {
                                if (lr == null) continue;
                                var lrow = lgr.Invoke(null, new object[] { lr });
                                if (lrow != null) lrows.Add(LRow(lrow));
                            }
                        var lme = lgp.Invoke(null, null);
                        if (lme != null) lrows.Add(LRow(lme));
                    }
                    catch (Exception lex) { return $"ERR rivals: {lex.Message}"; }
                    finally { GameStatePatcher.RivalsLeaderboardLoadRunning = false; }
                    // Load :29 verbatim: (x, y) => y.weeklyIncome.CompareTo(x.weeklyIncome).
                    lrows.Sort((x, y) => ((float)y[2]).CompareTo((float)x[2]));
                    var lsb = new StringBuilder($"OK rows={lrows.Count} mode=lb");
                    foreach (var ld in lrows)
                        lsb.Append($" | name='{ld[0]}';id='{ld[1]}';income={(int)(float)ld[2]};biz={ld[3]};bldg={ld[4]};defeated={ld[5]}");
                    return lsb.ToString();
                }

                case "notify":
                {
                    // Fires the game's own toast through its single gateway (decompile
                    // UI.Notification/Notifications.cs:26) - the phase 1-C Postfix on that method
                    // decides whether it relays.  The verb adds no relay path of its own.
                    var ntk = arg.Split(' ');
                    string nkey  = ntk.Length > 0 ? ntk[0].Trim() : "";
                    string naddr = ntk.Length > 1 ? string.Join(" ", ntk, 1, ntk.Length - 1).Trim() : "";
                    if (nkey.Length == 0 || naddr.Length == 0) return "ERR usage: notify <headerKey> <addressKey>";
                    var nreg = GameStatePatcher.FindRegistration(naddr);
                    if (nreg == null) return $"ERR no registration at '{naddr}'";
                    string nname = nreg.BusinessName ?? "";
                    var ndata = new System.Collections.Generic.Dictionary<string, string> { { "businessName", nname } };
                    UI.Notification.Notifications.Show(UI.Notification.NotificationType.Info, nkey, ndata, 5f, null, null, false, false);
                    return $"OK notify key='{nkey}' addr='{naddr}' name='{nname}'";
                }

                case "offers":
                {
                    // Read-only: the client-side offer fields plus, on the HOST only, the
                    // authoritative pending-proposal map (target key -> proposer pid).
                    var osb = new StringBuilder($"OK incoming='{MergerSync.IncomingFromPid}' outgoing='{MergerSync.OutgoingToPid}' ");
                    if (!MPServer.IsRunning) { osb.Append("pending=n/a"); return osb.ToString(); }
                    osb.Append("pending=[");
                    try
                    {
                        var okeys = new System.Collections.Generic.List<string>(MPServer._mergerPendingByTarget.Keys);
                        okeys.Sort(StringComparer.Ordinal);
                        int oi = 0;
                        foreach (var ok in okeys)
                        {
                            if (oi++ > 0) osb.Append(',');
                            osb.Append(ok).Append("<-").Append(MPServer._mergerPendingByTarget[ok]?.From ?? "");
                        }
                    }
                    catch { }
                    osb.Append(']');
                    return osb.ToString();
                }

                case "regstate":
                {
                    // Read-only: the tenancy fields the ownership flip moves, the schedule size,
                    // and the flip's own view of the address.
                    if (arg.Length == 0) return "ERR address key required";
                    var qreg = GameStatePatcher.FindRegistration(arg);
                    if (qreg == null) return $"ERR no registration for '{arg}'";
                    int qdays = 0, qshifts = 0;
                    try
                    {
                        if (qreg.scheduleDays != null)
                            foreach (var qsd in qreg.scheduleDays)
                            { if (qsd == null) continue; qdays++; if (qsd.workShifts != null) qshifts += qsd.workShifts.Count; }
                    }
                    catch { }
                    string qkey = arg; try { qkey = GameStateReader.AddressKey(qreg); } catch { }
                    string qstamp = "", qname = "", qtype = "";
                    try { qstamp = qreg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                    try { qname  = qreg.BusinessName?.ToString() ?? ""; }        catch { }
                    try { qtype  = qreg.businessTypeName?.ToString() ?? ""; }    catch { }
                    return $"OK rented={qreg.RentedByPlayer} stamp='{qstamp}' forRent={qreg.AvailableForRent} type={qtype} "
                         + $"name='{qname}' days={qdays} shifts={qshifts} flipped={MergerFlip.IsFlipped(qkey)} parked='{MergerFlip.ParkedRunner(qkey)}'";
                }

                case "ownflip":
                {
                    // H-MERGEROWNFLIP-1 (read-only): one address as the flip sees it - the native flag, the flip table,
                    // the adopt wait list, TrulyMine, and the rental-ledger tenant this machine knows (host: its live
                    // ledger; client: the company's pushed owner ids; '?' = no answer here).
                    if (arg.Length == 0) return "ERR address key required";
                    var ofreg = GameStatePatcher.FindRegistration(arg);
                    if (ofreg == null) return $"ERR no registration for '{arg}'";
                    string ofkey = arg; try { ofkey = GameStateReader.AddressKey(ofreg); } catch { }
                    bool ofKnown = MergerFlip.TryLedgerOwner(ofkey, out var ofOwner);
                    string ofname = "", oftype = "";
                    try { ofname = ofreg.BusinessName?.ToString() ?? ""; } catch { }
                    try { oftype = ofreg.businessTypeName?.ToString() ?? ""; } catch { }
                    return $"OK ownflip key='{ofkey}' rented={ofreg.RentedByPlayer} flipped={MergerFlip.IsFlipped(ofkey)} adopted={MergerFlip.IsAdopted(ofkey)} "
                         + $"truly={MergerFlip.TrulyMine(ofreg)} ledger='{(ofKnown ? ofOwner : "?")}' mine={MergerFlip.LedgerSaysMine(ofkey)} name='{ofname}' type={oftype}";
                }

                case "tillledger":
                {
                    // H-STANDINTILL-1 (DEV lever, test only): a PER-ORDER LEDGER of one shop's till
                    // (reg.unprocessedCompletedOrders) on THIS machine.
                    //   seed <tag 1-9> <n 1-20> <addr> - APPEND n SYNTHETIC, completed, PAID orders, one entry each priced
                    //        tag*100 + k + 0.25 (k = 1..n), so the ledger names each one (T<tag>#<k>) on every machine it
                    //        travels to (the paperwork carries the price, not an identity). Every seed is logged.
                    //   show <addr> - the till: tagged orders by name, the rest counted, and the day-roll value.
                    //   pay <addr>  - the game's OWN per-shop day roll (BusinessHelper.ProcessDailyOrders, private, by
                    //        reflection - the tripwire prefix and every mod patch on it run as on a real roll), one
                    //        '[TillLedger] <pid> PAID' line per order it bills, and the wallet delta.
                    var tlSp = arg.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (tlSp.Length < 2) return "ERR usage: tillledger seed <tag> <n> <addr> | show <addr> | pay <addr>";
                    string tlVerb = tlSp[0].ToLowerInvariant();
                    string tlRest = tlSp[1];
                    int tlTag = 0, tlN = 0;
                    if (tlVerb == "seed")
                    {
                        var tlS2 = tlRest.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                        if (tlS2.Length < 3 || !int.TryParse(tlS2[0], out tlTag) || !int.TryParse(tlS2[1], out tlN)
                            || tlTag < 1 || tlTag > 9 || tlN < 1 || tlN > 20)
                            return "ERR usage: tillledger seed <tag 1-9> <n 1-20> <addr>";
                        tlRest = tlS2[2];
                    }
                    else if (tlVerb != "show" && tlVerb != "pay") return "ERR usage: tillledger seed <tag> <n> <addr> | show <addr> | pay <addr>";
                    var tlReg = GameStatePatcher.FindRegistration(tlRest);
                    if (tlReg == null) return $"ERR no registration at '{tlRest}'";
                    string tlKey = tlRest; try { tlKey = GameStateReader.AddressKey(tlReg); } catch { }
                    var tlInv = System.Globalization.CultureInfo.InvariantCulture;
                    string tlPid = MPConfig.PlayerId;
                    bool tlBooks = false; try { tlBooks = MergerFlip.BooksHere(tlReg); } catch { }
                    if (tlReg.unprocessedCompletedOrders == null) tlReg.unprocessedCompletedOrders = new System.Collections.Generic.List<Order>();
                    var tlTill = tlReg.unprocessedCompletedOrders;
                    System.Func<Order, string> tlSig = tlO =>
                    {
                        if (tlO?.entries == null || tlO.entries.Count == 0) return "N[empty]";
                        if (tlO.entries.Count == 1 && tlO.entries[0] != null)
                        {
                            double tlP = tlO.entries[0].price;
                            int tlW = (int)System.Math.Floor(tlP);
                            int tlT = tlW / 100, tlK = tlW % 100;
                            if (System.Math.Abs(tlP - tlW - 0.25) < 0.01 && tlT >= 1 && tlT <= 9 && tlK >= 1) return $"T{tlT}#{tlK}";
                        }
                        var tlSb = new StringBuilder("N[");
                        foreach (var tlE in tlO.entries)
                            if (tlE != null) tlSb.Append(tlE.itemName).Append('=').Append(tlE.price.ToString("F2", tlInv)).Append(tlE.paid ? "p" : "u").Append(';');
                        return tlSb.Append(']').ToString();
                    };
                    if (tlVerb == "seed")
                    {
                        string tlItem = "";
                        try
                        {
                            foreach (var tlO in tlTill)
                            {
                                if (tlO?.entries == null) continue;
                                foreach (var tlE in tlO.entries) if (tlE != null && !string.IsNullOrEmpty(tlE.itemName)) { tlItem = tlE.itemName; break; }
                                if (tlItem.Length > 0) break;
                            }
                            if (tlItem.Length == 0 && tlReg.orderHistory != null)
                                foreach (var tlH in tlReg.orderHistory)
                                {
                                    if (tlH?.itemSales == null) continue;
                                    foreach (var tlS in tlH.itemSales) if (tlS != null && !string.IsNullOrEmpty(tlS.itemName)) { tlItem = tlS.itemName; break; }
                                    if (tlItem.Length > 0) break;
                                }
                        }
                        catch { }
                        if (tlItem.Length == 0) tlItem = "ba:tillledger_probe";
                        for (int tlK2 = 1; tlK2 <= tlN; tlK2++)
                        {
                            float tlPrice = tlTag * 100 + tlK2 + 0.25f;
                            var tlNew = new Order { completed = true };
                            tlNew.entries.Add(new global::Entities.OrderEntry
                            {
                                itemName = tlItem, price = tlPrice, available = true, priceAccceptable = true,
                                paid = true, processed = true, wholesalePrice = 0f,
                            });
                            tlTill.Add(tlNew);
                            Plugin.Logger.LogInfo($"[TillLedger] {tlPid} SEEDED T{tlTag}#{tlK2} into '{tlKey}' item='{tlItem}' "
                                                + $"price={tlPrice.ToString("F2", tlInv)} books={tlBooks} (DEV lever: a SYNTHETIC paid till entry, test only)");
                        }
                        try { PaperworkSync.MarkDirty(); } catch { }   // the till changed: the next paperwork publish carries it
                    }
                    var tlTags = new System.Collections.Generic.List<string>();
                    int tlNatural = 0; double tlValue = 0;
                    foreach (var tlO in tlTill)
                    {
                        if (tlO == null) continue;
                        string tlS3 = tlSig(tlO);
                        if (tlS3.StartsWith("T")) tlTags.Add(tlS3); else tlNatural++;
                        tlValue += TillDupes.ExtraReferenceValue(tlO);
                    }
                    tlTags.Sort(StringComparer.Ordinal);
                    string tlState = $"key='{tlKey}' books={tlBooks} orders={tlTill.Count} value={tlValue.ToString("F2", tlInv)} "
                                   + $"tagged=[{string.Join(",", tlTags)}] natural={tlNatural}";
                    if (tlVerb != "pay")
                    {
                        Plugin.Logger.LogInfo($"[TillLedger] {tlPid} {tlVerb.ToUpperInvariant()} {tlState}");
                        return $"OK tillledger {tlVerb} {tlState}";
                    }
                    var tlMi = HarmonyLib.AccessTools.Method(typeof(global::Helpers.BusinessHelper), "ProcessDailyOrders");
                    if (tlMi == null) return "ERR BusinessHelper.ProcessDailyOrders not found";
                    var tlBilled = new System.Collections.Generic.List<string>();
                    foreach (var tlO in tlTill)
                    {
                        if (tlO == null || !tlO.completed || tlO.entries == null || !tlO.entries.Exists(x => x != null && x.paid)) continue;
                        string tlS4 = tlSig(tlO);
                        tlBilled.Add(tlS4);
                        Plugin.Logger.LogInfo($"[TillLedger] {tlPid} PAID {tlS4} at '{tlKey}' value={TillDupes.ExtraReferenceValue(tlO).ToString("F2", tlInv)} books={tlBooks}");
                    }
                    double tlM0 = 0, tlM1 = 0;
                    try { tlM0 = SaveGameManager.Current.Money; } catch { }
                    try { tlMi.Invoke(null, new object[] { tlReg }); }
                    catch (Exception exTl) { return "ERR day roll threw: " + (exTl.InnerException?.Message ?? exTl.Message); }
                    try { tlM1 = SaveGameManager.Current.Money; } catch { }
                    int tlAfter = 0; try { tlAfter = tlReg.unprocessedCompletedOrders?.Count ?? 0; } catch { }
                    try { PaperworkSync.MarkDirty(); } catch { }   // the till was emptied: the next paperwork publish carries it
                    tlBilled.Sort(StringComparer.Ordinal);
                    string tlLine = $"key='{tlKey}' books={tlBooks} billed={tlBilled.Count} value={tlValue.ToString("F2", tlInv)} "
                                  + $"money={tlM0.ToString("F2", tlInv)}->{tlM1.ToString("F2", tlInv)} tillAfter={tlAfter} "
                                  + $"paid=[{string.Join(",", tlBilled.FindAll(s => s.StartsWith("T")))}] naturalPaid={tlBilled.FindAll(s => !s.StartsWith("T")).Count}";
                    Plugin.Logger.LogInfo($"[TillLedger] {tlPid} DAYROLL {tlLine}");
                    return $"OK tillledger pay {tlLine}";
                }

                case "ownerabsent":
                {
                    // H-STANDINLEASE-1 fold S1 (read-only): LeaseEndWatch.OwnerAbsent for one address, as the terminate guard reads it.
                    if (arg.Length == 0) return "ERR address key required";
                    string oakey = arg;
                    try { var oareg = GameStatePatcher.FindRegistration(arg); if (oareg != null) oakey = GameStateReader.AddressKey(oareg); } catch { }
                    bool oa = LeaseEndWatch.OwnerAbsent(oakey, out var oaOwner);
                    return $"OK ownerabsent key='{oakey}' absent={oa} owner='{oaOwner}'";
                }

                case "bizrent":
                case "bizterminate":
                {
                    // H-MERGEROWNFLIP-1 (DEV lever): runs the BizMan page's OWN handler for an address -
                    // BizManPresentation.RentBuilding (the rent button: deposit + first rent + BuildingHelper.RentBuilding)
                    // or OnTerminateContractConfirm (the terminate confirm: interior sale / deposit refund + vacate) - so
                    // every Harmony patch on it runs exactly as for a click. The page itself is not opened: a bare
                    // presentation object carries only the page's business link (building + registration), which is all
                    // either handler reads. Money is read before and after in the same frame.
                    var bargs = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (bargs.Length < 2) return $"ERR usage: {verb} <num> <ba:street_x>" + (verb == "bizterminate" ? " [postfix]" : "");
                    string baddr = bargs[0] + " " + bargs[1];
                    var bgi = SaveGameManager.Current;
                    if (bgi == null) return "ERR no game instance";
                    var breg = GameStatePatcher.FindRegistration(baddr);
                    if (breg == null) return $"ERR no registration for '{baddr}'";
                    var bbld = Helpers.BuildingHelper.GetBuilding(breg.Address);
                    if (bbld == null) return $"ERR no Building for '{baddr}'";
                    var bpres = (BizManPresentation)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(BizManPresentation));
                    var bbiz  = (BizManBusiness)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(BizManBusiness));
                    bbiz.building = bbld;
                    bbiz.buildingRegistration = breg;
                    var bfield = HarmonyLib.AccessTools.Field(typeof(BizManPresentation), "bizManBusiness");
                    if (bfield == null) return "ERR BizManPresentation.bizManBusiness not found";
                    bfield.SetValue(bpres, bbiz);
                    float bBefore = bgi.Money;
                    string bErr = "";
                    try
                    {
                        if (verb == "bizrent") bpres.RentBuilding();
                        else HarmonyLib.AccessTools.Method(typeof(BizManPresentation), "OnTerminateContractConfirm").Invoke(bpres, null);
                    }
                    catch (Exception bex) { var be = bex.InnerException ?? bex; bErr = be.GetType().Name + ": " + be.Message; }
                    // H-MERGEROWNFLIP-1 part B: `bizterminate <addr> postfix` also runs the vacate-report postfix the
                    // bare page's end-of-method throw skips (MPPatches.Patch_TerminateContract), in the SAME frame as
                    // the refund - the clean-click path, so both lease-end triggers fire and the de-dup is exercised.
                    string bPost = "";
                    if (verb == "bizterminate" && bargs.Length >= 3 && bargs[2] == "postfix")
                    {
                        try
                        {
                            var pm = HarmonyLib.AccessTools.Method(typeof(MPPatches.Patch_TerminateContract), "Postfix");
                            if (pm == null) bPost = " postfix=notfound";
                            else { pm.Invoke(null, new object[] { bpres }); bPost = " postfix=ran"; }
                        }
                        catch (Exception pex) { var pe = pex.InnerException ?? pex; bPost = $" postfix=threw({pe.GetType().Name})"; }
                    }
                    float bAfter = bgi.Money;
                    var binv = System.Globalization.CultureInfo.InvariantCulture;
                    return $"OK {verb} key='{baddr}' money={bBefore.ToString("F2", binv)}->{bAfter.ToString("F2", binv)} same={(bBefore == bAfter)} "
                         + $"rented={breg.RentedByPlayer} flipped={MergerFlip.IsFlipped(baddr)}" + (bErr.Length > 0 ? $" threw='{bErr}'" : "") + bPost;
                }

                case "employees":
                {
                    // RAW roster (the no-arg EmployeeHelper.GetEmployeeInstances() - the same list
                    // MPPatches :1226 reads), so a merger's injected partner records are included.
                    // CharacterData carries ONE name string, not a first/last pair.
                    var elist = Helpers.EmployeeHelper.GetEmployeeInstances();
                    if (elist == null) return "ERR no employee roster";
                    string eidOnly = arg.StartsWith("id=", StringComparison.Ordinal) ? arg.Substring(3).Trim() : "";
                    var esb = new StringBuilder();
                    int eshown = 0, etotal = 0;
                    foreach (var e in elist)
                    {
                        if (e == null) continue;
                        string eaddr = "";
                        try { if (e.assignedAddress != null) eaddr = GameStateReader.AddressKey(e.assignedAddress); } catch { }
                        // H-MERGERTRAIN-1: `employees id=<employeeId>` reads ONE named employee wherever they
                        // stand - the 30-row cap below used to hide a benched record a run had just picked by id,
                        // and a bench has no address to scope the listing with.
                        if (eidOnly.Length > 0) { if (!string.Equals(e.id, eidOnly, StringComparison.Ordinal)) continue; }
                        else if (arg.Length > 0 && !string.Equals(eaddr, arg, StringComparison.OrdinalIgnoreCase)) continue;
                        etotal++;
                        if (arg.Length == 0 && eshown >= 30) continue;   // cap only the ALL-employees listing; an address-scoped or id-scoped list is bounded already (run T-P0-6: a 51-staff shop hid the adopted hire, 2026-09-10)
                        string ename = ""; try { ename = e.characterData?.name?.ToString() ?? ""; } catch { }
                        bool einj = false; try { einj = MPRegisterSync.IsInjectedStaff(e.id); } catch { }
                        // Phase 4b: the bonus figures as THIS machine reads them (on a copy the cooldown is not synced, so canbonus is the satisfaction test only; the runner's own record is the real gate).
                        float ebonus = 0f; bool ecan = false; try { ebonus = e.GetBonusAmount(); ecan = e.CanGiveBonus(); } catch { }
                        // Phase 4b (people): the primary skill as this machine reads it, so a scenario can pick a trainable employee (value < 100).
                        string eskill = ""; try { var esk = e.characterData?.skills; if (esk != null && esk.Count > 0 && esk[0] != null) eskill = $"{esk[0].name}:{esk[0].value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}"; } catch { }
                        // H-MERGERTRAIN-1: the training session as THIS machine holds it - "" / -1 when there is
                        // none. On a copy it is the owner's, mirrored by the roster/bench publish; a copy that
                        // shows one it was never sent would mean this machine wrote it itself.
                        string etrain = "|-1"; try { var ets = e.trainingSession; if (ets != null) etrain = $"{ets.skill}|{ets.startDay}"; } catch { }
                        string eline = $"{e.id}|{ename}|assigned={eaddr}|injected={einj}|bonus={ebonus.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}|canbonus={ecan}|skill={eskill}|training={etrain}";
                        Plugin.Logger.LogWarning($"[TestDrive] employee: {eline}");
                        if (eshown++ > 0) esb.Append(" ; ");
                        esb.Append(eline);
                    }
                    return $"OK {etotal} employee(s){(arg.Length > 0 ? $" @ '{arg}'" : "")}: {esb}";
                }

                case "rostertip":
                {
                    // H-ROSTERTOOLTIP-1: what the native employee tooltip would print for every employee at one address, as
                    // THIS machine holds them - assignedWeeklyHours (EmployeeTooltip.cs:44) and the demand list
                    // (EmployeeTooltip.cs:36). Run on the owner (real records) and on the partner (display copies); the
                    // sig is an md5 of the sorted rows, so equal sigs = the same hours and demands on both sides.
                    // `rostertip pick` = the first (by key) shop truly mine with a real employee who has hours AND demands.
                    // Fold: each row also carries w=<assignedWeeklyDays as ints> (DaysWorkingPerWeek/FreeOnDays read them for
                    // the tooltip's demand ticks), and pick additionally requires that employee to have at least one day.
                    try
                    {
                        var rgi = SaveGameManager.Current;
                        if (rgi?.EmployeeInstances == null || rgi.BuildingRegistrations == null) return "ERR no save loaded";
                        string rarg = arg.Trim();
                        if (rarg.Length == 0) return "ERR usage: rostertip <num> <ba:street_x> | rostertip pick";
                        Func<Entities.EmployeeInstance, string> rkey = e => { try { return e.assignedAddress != null ? (GameStateReader.AddressKey(e.assignedAddress) ?? "") : ""; } catch { return ""; } };
                        if (rarg == "pick")
                        {
                            var rkeys = new System.Collections.Generic.List<string>();
                            foreach (var reg in rgi.BuildingRegistrations)
                            {
                                if (reg == null) continue;
                                bool rmine = false; try { rmine = MergerFlip.TrulyMine(reg); } catch { }
                                if (rmine) { string k = GameStateReader.AddressKey(reg); if (!string.IsNullOrEmpty(k)) rkeys.Add(k); }
                            }
                            rkeys.Sort(StringComparer.Ordinal);
                            foreach (var k in rkeys)
                            {
                                int rn = 0, rgood = 0;
                                foreach (var e in rgi.EmployeeInstances)
                                {
                                    if (e == null || string.IsNullOrEmpty(e.id)) continue;
                                    if (MPRegisterSync.IsSyntheticDuty(e.id) || MPRegisterSync.IsInjectedStaff(e.id)) continue;
                                    if (!string.Equals(rkey(e), k, StringComparison.OrdinalIgnoreCase)) continue;
                                    rn++;
                                    try { if (e.assignedWeeklyHours > 0 && e.demands != null && e.demands.Count > 0 && e.assignedWeeklyDays != null && e.assignedWeeklyDays.Count > 0) rgood++; } catch { }
                                }
                                if (rgood > 0) return $"OK rostertip pick '{k}' staff={rn} withHoursAndDemands={rgood}";
                            }
                            return $"ERR rostertip pick: none of my {rkeys.Count} shop(s) has an employee with scheduled hours and demands";
                        }
                        var rrows = new System.Collections.Generic.List<string>();
                        int rinj = 0;
                        foreach (var e in rgi.EmployeeInstances)
                        {
                            if (e == null || string.IsNullOrEmpty(e.id)) continue;
                            if (MPRegisterSync.IsSyntheticDuty(e.id)) continue;
                            if (!string.Equals(rkey(e), rarg, StringComparison.OrdinalIgnoreCase)) continue;
                            bool inj = false; try { inj = MPRegisterSync.IsInjectedStaff(e.id); } catch { }
                            if (inj) rinj++;
                            int hrs = -1; try { hrs = e.assignedWeeklyHours; } catch { }
                            string dem = ""; try { if (e.demands != null) dem = string.Join(",", e.demands); } catch { }
                            string wdy = ""; try { if (e.assignedWeeklyDays != null) { var wl = new System.Collections.Generic.List<string>(); foreach (var d in e.assignedWeeklyDays) wl.Add(((int)d).ToString()); wdy = string.Join(",", wl); } } catch { }
                            rrows.Add($"{e.id}:h={hrs}:d={dem}:w={wdy}");
                        }
                        rrows.Sort(StringComparer.Ordinal);
                        string rjoined = string.Join(" ; ", rrows);
                        string rsig;
                        using (var md = System.Security.Cryptography.MD5.Create())
                        {
                            var hb = md.ComputeHash(Encoding.UTF8.GetBytes(rjoined));
                            var hsb = new StringBuilder(); for (int i = 0; i < 4; i++) hsb.Append(hb[i].ToString("x2"));
                            rsig = hsb.ToString();
                        }
                        foreach (var row in rrows) Plugin.Logger.LogWarning($"[TestDrive] rostertip '{rarg}': {row}");
                        return $"OK rostertip '{rarg}' n={rrows.Count} injected={rinj} sig={rsig} rows={rjoined}";
                    }
                    catch (Exception ex) { return $"ERR rostertip: {ex.GetType().Name}: {ex.Message}"; }
                }

                case "shift":
                {
                    // ONE WorkShift in the exact shape SharedShopSchedule.ReplaceDay (:670) uses:
                    // sd.AddWorkShift(new WorkShift { ... }). type is set EXPLICITLY - WorkShiftType's
                    // first member is Cleaning, so a defaulted type would silently make a cleaning shift.
                    var stk = arg.Split(' ');
                    if (stk.Length < 6) return "ERR usage: shift <num> <ba:street_x> <day> <employeeId> <fromHour> <toHour>";
                    string saddr = stk[0] + " " + stk[1];
                    if (!int.TryParse(stk[2], out var sday)) return "ERR day must be a number";
                    if (!int.TryParse(stk[4], out var sfrom) || !int.TryParse(stk[5], out var sto)) return "ERR hours must be numbers";
                    if (sto <= sfrom) return "ERR toHour must be after fromHour";
                    string sempId = stk[3];
                    var sreg2 = GameStatePatcher.FindRegistration(saddr);
                    if (sreg2 == null) return $"ERR no registration for '{saddr}'";
                    var semp = FindEmployee(sempId);
                    if (semp == null) return $"ERR no employee '{sempId}' on this machine";
                    string sempAddr = "";
                    try { if (semp.assignedAddress != null) sempAddr = GameStateReader.AddressKey(semp.assignedAddress); } catch { }
                    string sregKey = saddr; try { sregKey = GameStateReader.AddressKey(sreg2); } catch { }
                    if (!string.Equals(sempAddr, sregKey, StringComparison.OrdinalIgnoreCase))
                        return $"ERR employee '{sempId}' is assigned to '{sempAddr}', not '{sregKey}'";
                    ScheduleDay? ssd = null;
                    try { foreach (var d in sreg2.scheduleDays) if (d != null && (int)d.day == sday) { ssd = d; break; } } catch { }
                    if (ssd == null)
                    {
                        // The day arg is matched against (int)ScheduleDay.day exactly as
                        // SharedShopSchedule.FindDay (:645) does; the ordinals actually present are
                        // listed rather than guessed at.
                        var sords = new StringBuilder();
                        try { foreach (var d in sreg2.scheduleDays) if (d != null) sords.Append((int)d.day).Append(' '); } catch { }
                        return $"ERR '{saddr}' has no scheduleDay with day={sday} (ordinals present: {sords})";
                    }
                    ssd.AddWorkShift(new WorkShift
                    {
                        employeeId = sempId, itemInstanceId = "",
                        startingHour = sfrom, endingHour = sto,
                        type = WorkShiftType.Default,
                    });
                    return $"OK shift {sfrom}-{sto} for '{sempId}' added to '{saddr}' day {sday}; that day now holds {ssd.workShifts.Count} shift(s)";
                }

                case "shiftclear":
                {
                    // The inverse of 'shift', with ReplaceDay's keep-synthetic rule (:660-680):
                    // this machine's own duty stand-ins survive a day rebuild.
                    var ctk = arg.Split(' ');
                    if (ctk.Length < 3) return "ERR usage: shiftclear <num> <ba:street_x> <day>";
                    string caddr = ctk[0] + " " + ctk[1];
                    if (!int.TryParse(ctk[2], out var cday)) return "ERR day must be a number";
                    var creg2 = GameStatePatcher.FindRegistration(caddr);
                    if (creg2 == null) return $"ERR no registration for '{caddr}'";
                    ScheduleDay? csd = null;
                    try { foreach (var d in creg2.scheduleDays) if (d != null && (int)d.day == cday) { csd = d; break; } } catch { }
                    if (csd == null)
                    {
                        var cords = new StringBuilder();
                        try { foreach (var d in creg2.scheduleDays) if (d != null) cords.Append((int)d.day).Append(' '); } catch { }
                        return $"ERR '{caddr}' has no scheduleDay with day={cday} (ordinals present: {cords})";
                    }
                    if (csd.workShifts == null) return $"OK '{caddr}' day {cday} had no shift list";
                    int cwas = csd.workShifts.Count;
                    int cgone = csd.workShifts.RemoveAll(w => w == null || !SharedShopSchedule.IsSynthetic(w.employeeId));
                    return $"OK cleared {cgone} shift(s) from '{caddr}' day {cday} ({cwas - cgone} synthetic kept)";
                }

                // ── H-WORKFF-1 part 2 (MEASUREMENT lever): the PLAYER as the cashier ─────────────────────
                // `work on` takes the SAME game path as a click on the register: EmployeeStationController.Interact
                // (EmployeeStationController.cs:169-175) falls through to Work() (:83; CanWork gate :69-81) ->
                // MoveTowardsEntity -> PlayerActivityUI.Show(new WorkActivity(null job = owner mode, station)) (:91).
                // In MP the mod's dock auto-presses the panel's Start (MPRestSync auto-start) -> WorkActivity.StartWorking
                // (WorkActivity.cs:93-131: AssignEmployee(player) :118) -> PlayerActivityUI.OnActivityStarted -> Running
                // (PlayerActivityUI.cs:378-386). TickWork follows it through and logs '[DEV] work on at ...' or a FAILED
                // reason; if nothing pressed Start 2 s after the panel appeared, it presses the panel's own StartWork
                // button (WorkActivity.cs:53). `work off` = the panel's StopWork button (WorkActivity.cs:55 -> Finish :158).
                // `work` / `work state` = READ-ONLY: working=True means the game's current activity is a WorkActivity
                // in state Running.
                case "work":
                {
                    string wa = arg.Trim().ToLowerInvariant();
                    if (wa.Length == 0 || wa == "state") return "OK work " + WorkStateLine();
                    if (wa == "on") return WorkOn();
                    if (wa == "off") return WorkOff();
                    return "ERR usage: work <on|off|state>";
                }

                case "autofill":
                    // Review 2026-09-10 #1: the button's helper pops a HudConfirm when staff are unassigned (nothing runs),
                    // fills on a BACKGROUND thread, and its completion touches the open BizMan screen (and UpdateHQPlans for
                    // an HQ) - a headless run never has that screen, so this lever would be racy at best and can throw on
                    // the game thread at worst. Auto-fill stays a hands-on check.
                    return "ERR autofill is not a harness lever (needs the Schedule screen open) - do it by hand";

                case "fire":
                {
                    // MyEmployees.cs:632 - the fire button is a bare RemoveEmployee() on the selected
                    // instance (no args). On an INJECTED partner record the merger prefix
                    // (MergerEmployeeSync.cs:34-50) intercepts and routes the fire to the owner.
                    if (arg.Length == 0) return "ERR employee id required";
                    var femp = FindEmployee(arg);
                    if (femp == null) return $"ERR no employee '{arg}' on this machine";
                    string fname = ""; try { fname = femp.characterData?.name?.ToString() ?? ""; } catch { }
                    bool finj = false; try { finj = MPRegisterSync.IsInjectedStaff(arg); } catch { }
                    femp.RemoveEmployee();
                    return $"OK RemoveEmployee invoked for '{arg}' ('{fname}', injected={finj})";
                }

                case "assign":
                {
                    // CandidateCellView.AssignBusiness (:192, the listener wired to the assign
                    // dropdown at :81): the whole game-state write is `assignedAddress = reg.Address`.
                    // The rest of that method re-draws the MyEmployees panel, which a headless
                    // driver has nothing to refresh.
                    var ntk = arg.Split(' ');
                    if (ntk.Length < 3) return "ERR usage: assign <employeeId> <num> <ba:street_x>";
                    string nempId = ntk[0];
                    string naddr  = ntk[1] + " " + ntk[2];
                    var nemp = FindEmployee(nempId);
                    if (nemp == null) return $"ERR no employee '{nempId}' on this machine";
                    var nreg = GameStatePatcher.FindRegistration(naddr);
                    if (nreg == null) return $"ERR no registration for '{naddr}'";
                    nemp.assignedAddress = nreg.Address;
                    return $"OK assigned '{nempId}' -> '{naddr}'";
                }

                case "licensefees":
                {
                    // 2026-09-18 (rig determinism, user-approved): mark the LOCAL player's own cinema/theater
                    // licensing fees PAID TODAY through the game's own noCharge path (LicensingFeesHelper.cs:16-38:
                    // `noCharge || IsBusinessOpen`, SetLicensingFeePaidToday, no ChangeMoney). T-WALLETDUPE asserts
                    // the company balance EXACTLY across a save/reload; a legitimate $20k daily fee landed mid-run
                    // whenever the rejoining owner's clock ran through the opening hour in-world (run
                    // T-WALLETDUPE-20260918-130339) and broke the assertion by timing alone.
                    var lgi = SaveGameManager.Current;
                    if (lgi?.BuildingRegistrations == null) return "ERR no game instance";
                    int lMarked = 0; var lNames = new StringBuilder();
                    foreach (var lr in lgi.BuildingRegistrations)
                    {
                        if (lr == null) continue;
                        string lt = lr.businessTypeName ?? "";
                        if (lt != "ba:businesstype_cinema" && lt != "ba:businesstype_theater") continue;
                        if (!MergerFlip.TrulyMine(lr)) continue;   // own shops only - never a flipped partner's
                        try
                        {
                            Buildings.Retail.Businesses.CinemaTheater.LicensingFeesHelper.PayLicensingFees(lr, noCharge: true);
                            lMarked++; lNames.Append(lNames.Length > 0 ? "," : "").Append(lr.BusinessName);
                        }
                        catch (Exception lex) { return $"ERR licensefees '{lr.BusinessName}': {lex.Message}"; }
                    }
                    return $"OK licensefees marked={lMarked} [{lNames}] (paid today, no charge)";
                }

                case "money":
                {
                    var mgi = SaveGameManager.Current;
                    if (mgi == null) return "ERR no game instance";
                    bool mmem = false; try { mmem = MergerSync.IAmMember; } catch { }
                    // MergerWallet publishes NO balance accessor: the company balance is MIRRORED
                    // into gi.Money by SetMirror (MergerWallet.cs :168-180), so while merged the
                    // money figure IS the wallet - reported as the mirror rather than inventing one.
                    return $"OK money={mgi.Money.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} merged={mmem} wallet={(mmem ? $"{mgi.Money.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} (mirror)" : "n/a")}";   // review 2026-09-10 #4: invariant culture (the readout regex expects a dot)
                }

                case "prices":
                {
                    // retailPrices is keyed by RetailPrice.itemName — the product/item NAME string the pricing
                    // tab's cell model carries (InventoryProductModel.RetailPriceReference) — printed verbatim.
                    if (arg.Length == 0) return "ERR address key required";
                    var preg = GameStatePatcher.FindRegistration(arg);
                    if (preg == null) return $"ERR no registration at '{arg}'";
                    string pkey = arg; try { pkey = GameStateReader.AddressKey(preg); } catch { }
                    var pmap = new System.Collections.Generic.SortedDictionary<string, float>(StringComparer.Ordinal);
                    if (preg.retailPrices != null)
                        foreach (var prp in preg.retailPrices)
                            if (prp != null && !string.IsNullOrEmpty(prp.itemName)) pmap[prp.itemName] = prp.price;
                    var psb = new StringBuilder($"OK prices addr='{pkey}' n={pmap.Count}");
                    foreach (var pkv in pmap)
                        psb.Append(" | ").Append(pkv.Key).Append('=')
                           .Append(pkv.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                    return psb.ToString();
                }

                case "setprice":
                {
                    // The pricing tab has NO named setter: its write is the anonymous onValueChanged listener wired
                    // in InventoryProductCellView.Start (:114-115), which assigns RetailPriceReference.price and
                    // StoredRetailPriceReference.price. SharedShopPrices.CommitPriceEdit performs exactly that pair
                    // of writes and then takes the routing decision, so this lever exercises the real seam.
                    var vtk = arg.Split(' ');
                    if (vtk.Length < 4) return "ERR usage: setprice <num> <ba:street_x> <productKey> <value>";
                    string vaddr = vtk[0] + " " + vtk[1];
                    string vprod = vtk[2];
                    if (!float.TryParse(vtk[3], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var vval))
                        return "ERR value must be a number";
                    var vreg = GameStatePatcher.FindRegistration(vaddr);
                    if (vreg == null) return $"ERR no registration at '{vaddr}'";
                    string vkey = vaddr; try { vkey = GameStateReader.AddressKey(vreg); } catch { }
                    bool vfound = false;
                    if (vreg.retailPrices != null)
                        foreach (var vrp in vreg.retailPrices)
                            if (vrp != null && vrp.itemName == vprod) { vfound = true; break; }
                    if (!vfound) return $"ERR unknown product '{vprod}'";
                    bool vrouted = SharedShopPrices.CommitPriceEdit(vreg, vkey, vprod, vval);
                    string vshown = vval.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    return $"OK setprice addr='{vkey}' product='{vprod}' value={vshown} routed={vrouted}";
                }

                case "workedit":
                {
                    // W3-6: the routed warehouse/factory work edit. The DRIVER SLOT is the field chosen here —
                    // its native write is the single assignment at SharedShopWorkTabs.cs:483, so
                    // CommitWorkEdit exercises the real payload and the real host gate.
                    var wtk = arg.Split(' ');
                    if (wtk.Length < 4) return "ERR usage: workedit <num> <ba:street_x> driver<slot> <employeeId|->";
                    string waddr  = wtk[0] + " " + wtk[1];
                    string wfield = wtk[2];
                    string wvalue = wtk[3] == "-" ? "" : wtk[3];
                    var wreg = GameStatePatcher.FindRegistration(waddr);
                    if (wreg == null) return $"ERR no registration at '{waddr}'";
                    string wkey = waddr; try { wkey = GameStateReader.AddressKey(wreg); } catch { }
                    bool wrouted = SharedShopWorkTabs.CommitWorkEdit(wkey, wfield, wvalue);
                    return $"OK workedit addr='{wkey}' field='{wfield}' value='{wtk[3]}' routed={wrouted}";
                }

                case "staffop":
                {
                    // W3-6 + phase 4b: the routed staff ops. The game has NO player-facing raise surface — hourlyWage is
                    // written only by hiring negotiation (CandidateSalaryNegotiation.cs:101) and by rival
                    // poaching (EmployeeInstance.cs:1087/:1159) — so this lever IS the seam for "raise".
                    var stk = arg.Split(' ');
                    // H-MERGERTRAIN-1: 'unassign' joins them - the routed CommitAssign leg, which is how a
                    // partner's employee reaches their own bench from here (the copy comes back empty-addressed,
                    // MergerEmployeeSync :252-259), and the only way a bench train has a subject to act on.
                    if (stk.Length < 4) return "ERR usage: staffop <num> <ba:street_x> <employeeId> raise <wage> | bonus <amount> | unassign";
                    string saddr  = stk[0] + " " + stk[1];
                    string sempId = stk[2];
                    string sop    = stk[3];
                    if (sop != "raise" && sop != "bonus" && sop != "unassign") return "ERR only 'raise', 'bonus' and 'unassign' are routed (to-do is refused on a routed record)";
                    if (sop != "unassign" && stk.Length < 5) return "ERR usage: staffop <num> <ba:street_x> <employeeId> raise <wage> | bonus <amount> | unassign";
                    float swage = 0f;
                    if (sop != "unassign" && !float.TryParse(stk[4], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out swage))
                        return sop == "bonus" ? "ERR amount must be a number" : "ERR wage must be a number";
                    var sreg = GameStatePatcher.FindRegistration(saddr);
                    if (sreg == null) return $"ERR no registration at '{saddr}'";
                    string skey = saddr; try { skey = GameStateReader.AddressKey(sreg); } catch { }
                    if (FindEmployee(sempId) == null) return $"ERR no employee '{sempId}' on this machine";
                    // Phase 4b: "bonus" carries the AMOUNT in the payload field the wage uses; the machine that
                    // runs the address re-runs the game's own GiveBonus and treats the figure only as a bound.
                    if (sop == "unassign")
                    {
                        bool surouted = SharedShopStaff.CommitAssign(sempId, skey, "");
                        return $"OK staffop addr='{skey}' employee='{sempId}' op='unassign' routed={surouted}";
                    }
                    bool srouted = SharedShopStaff.CommitStaffOp(sempId, skey, sop, swage);
                    string sval = swage.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    return sop == "bonus"
                        ? $"OK staffop addr='{skey}' employee='{sempId}' op='bonus' amount={sval} routed={srouted}"
                        : $"OK staffop addr='{skey}' employee='{sempId}' op='{sop}' wage={sval} routed={srouted}";
                }

                case "candidates":
                {
                    // Phase 4b (people) P1: the candidate list AS THIS MACHINE SEES IT - own rows and the
                    // company copies together, each naming its origin and who (if anyone) has claimed it.
                    // An argument filters to one origin pid.
                    var clines = CompanyCandidates.Readout();
                    var csb = new StringBuilder();
                    int cshown = 0, ctotal = 0;
                    foreach (var cline in clines)
                    {
                        if (arg.Length > 0 && !cline.Contains("|origin=" + arg)) continue;
                        ctotal++;
                        if (cshown >= 30) continue;
                        Plugin.Logger.LogWarning($"[TestDrive] candidate: {cline}");
                        if (cshown++ > 0) csb.Append(" ; ");
                        csb.Append(cline);
                    }
                    return $"OK {ctotal} candidate(s){(arg.Length > 0 ? $" from '{arg}'" : "")}: {csb}";
                }

                case "claim":
                {
                    // The same message MyEmployees.NegotiateWithCandidate sends before it opens a negotiation.
                    // T7(b): a second word pins or releases the claim for a harness race - `hold` marks a
                    // synthetic live negotiation so the 5 s sweep cannot take the claim back, `drop` clears
                    // that mark AND releases the claim. heldBy is read from the local table at call time.
                    if (arg.Length == 0) return "ERR usage: claim <candidateId> [hold|drop]";
                    var ctk = arg.Split(' ');
                    string cid = ctk[0];
                    string cmode = ctk.Length > 1 ? ctk[1].ToLowerInvariant() : "";
                    if (cmode.Length > 0 && cmode != "hold" && cmode != "drop") return "ERR usage: claim <candidateId> [hold|drop]";
                    string cowner = CompanyCandidates.OwnerOfCandidate(cid);
                    bool csent = false;
                    if (cmode == "drop") CompanyCandidates.DropClaim(cid);
                    else
                    {
                        if (cmode == "hold") CompanyCandidates.SetVerbHold(cid, true);
                        csent = CompanyCandidates.CommitClaim(cid);
                    }
                    return $"OK claim addr-less candidate='{cid}' origin='{(cowner.Length > 0 ? cowner : "mine")}' mode='{(cmode.Length > 0 ? cmode : "ask")}' sent={csent} heldBy='{CompanyCandidates.ClaimantOf(cid)}'";
                }

                // ── TAXBILL-ONE T5: the ONE company tax bill ───────────────────────────
                case "taxbill":
                {
                    // `taxbill` runs the game's own annual assessment now (under the books lift, exactly
                    // as the join snap does); `taxbill synth <sales> <deduct> <estate>` installs a made-up
                    // return instead, so a rig can put a known figure on every member without playing a year.
                    var txGi = SaveGameManager.Current;
                    if (txGi == null) return "ERR no world loaded";
                    var txTk = arg.Length > 0 ? arg.Split(' ') : new string[0];
                    if (txTk.Length > 0 && txTk[0].ToLowerInvariant() == "synth")
                    {
                        if (txTk.Length < 4) return "ERR usage: taxbill synth <sales> <deduct> <estate>";
                        if (!float.TryParse(txTk[1], out float sSales) || !float.TryParse(txTk[2], out float sDed) || !float.TryParse(txTk[3], out float sEst))
                            return "ERR usage: taxbill synth <sales> <deduct> <estate>";
                        int sPct = txGi.gameVariables?.taxPercentage ?? 0;
                        float sTaxable = sSales - sDed;
                        if (sTaxable < 0f) sTaxable = 0f;                     // the game's own floor (TaxHelper.cs:268-271)
                        var synth = new Entities.Taxes
                        {
                            day                = txGi.Day,
                            dueDay             = txGi.Day + 20,
                            taxPercentage      = sPct,
                            businessesIncome   = new System.Collections.Generic.List<(string, float)> { ("SYNTH-" + MPNames.Resolve(MPConfig.PlayerId), sSales) },
                            deductibleExpenses = new System.Collections.Generic.List<(string, float)> { ("SYNTH-DEDUCT", sDed) },
                            estateTaxes        = new System.Collections.Generic.List<(string, float)> { ("SYNTH-ESTATE", sEst) },
                            subtotalRegisteredBusinesses = sSales,
                            subtotalDeductibleExpenses   = sDed,
                            subtotalRealEstateTaxes      = sEst,
                            subtotalGamblingWinnings     = 0f,
                        };
                        synth.totalToPay = sTaxable * sPct / 100f + sEst;
                        if (synth.totalToPay > 0f) txGi.currentUnpaidTaxes = synth;   // as native: a ZERO bill leaves no record
                        CompanyBooks.LastFiledReturn = CompanyBooks.CloneReturn(synth);
                        var sNotice = HarmonyLib.AccessTools.Method(typeof(Helpers.TaxHelper), "SendTaxNotice", new Type[] { typeof(Entities.Taxes) });
                        if (sNotice != null) sNotice.Invoke(null, new object[] { synth });
                        CompanyBooks.Publish("tax filed");
                        return $"OK taxbill synth totalToPay={synth.totalToPay:F2} day={synth.day}";
                    }
                    var txExec = HarmonyLib.AccessTools.Method(typeof(Helpers.TaxHelper), "ExecutePlayerTaxesEvent");
                    if (txExec == null) return "ERR TaxHelper.ExecutePlayerTaxesEvent not found";
                    CompanyBooks.SuspendPush();
                    try { txExec.Invoke(null, null); } finally { CompanyBooks.SuspendPop(); }
                    var txFiled = CompanyBooks.LastFiledReturn;
                    if (txFiled == null) return "ERR taxbill fired but no return was captured (off a merger the capture is inert)";
                    int txRows = (txFiled!.businessesIncome?.Count ?? 0) + (txFiled!.deductibleExpenses?.Count ?? 0) + (txFiled!.estateTaxes?.Count ?? 0);
                    return $"OK taxbill fired totalToPay={txFiled!.totalToPay:F2} day={txFiled!.day} due={txFiled!.dueDay} rows={txRows}";
                }

                case "taxparts":
                {
                    // What this machine holds of the COMPANY's return: its own bill and every co-member's
                    // published one, so a rig can see the parts before the bill is opened.
                    var tpGi = SaveGameManager.Current;
                    if (tpGi == null) return "ERR no world loaded";
                    float tpOwn = 0f;
                    try { tpOwn = Helpers.TaxHelper.GetCurrentTaxesToPay(); } catch { }
                    int tpPeriod = tpGi.currentUnpaidTaxes?.day ?? (CompanyBooks.LastFiledReturn?.day ?? 0);
                    int tpMembers = 0, tpFiled = 0, tpPending = 0;
                    float tpSum = 0f;
                    var tpSb = new StringBuilder();
                    foreach (var tpKv in CompanyBooks.Partners)
                    {
                        if (!MergerSync.IsMemberPid(tpKv.Key)) continue;
                        tpMembers++;
                        var tpR = tpKv.Value.TaxReturn;
                        if (tpR != null && tpR.Day == tpPeriod) { tpFiled++; tpSum += tpR.TotalToPay; }
                        else tpPending++;
                        if (tpSb.Length > 0) tpSb.Append(',');
                        tpSb.Append($"{tpKv.Key}:{(tpR != null ? tpR.TotalToPay : 0f):F2}:{(tpR != null ? tpR.Day : 0)}");
                    }
                    return $"OK taxparts own={tpOwn:F2} period={tpPeriod} members={tpMembers} filed={tpFiled} pending={tpPending} sum={tpSum:F2} [{tpSb}]";
                }

                case "taxrender":
                {
                    // The renderer's OWN arithmetic (TaxesMessage.cs:194-207) on the company return, so a
                    // rig can check the lines and the grand total agree WITHOUT opening the phone.
                    var trGi = SaveGameManager.Current;
                    if (trGi == null) return "ERR no world loaded";
                    var trOwn = trGi.currentUnpaidTaxes ?? CompanyBooks.LastFiledReturn;
                    if (trOwn == null) return "ERR no return on this machine";
                    var trCo = CompanyBooks.CompanyReturn(trOwn!, out var trPending, out int trLoss);
                    float trIncome  = trCo.subtotalRegisteredBusinesses + trCo.subtotalGamblingWinnings;
                    float trTaxable = trIncome - trCo.subtotalDeductibleExpenses;
                    if (trTaxable < 0f) trTaxable = 0f;
                    float trTax   = trTaxable * trCo.taxPercentage / 100f;
                    float trTotal = trTax + trCo.subtotalRealEstateTaxes;
                    int trRows = trCo.businessesIncome.Count + trCo.deductibleExpenses.Count + trCo.estateTaxes.Count;
                    return $"OK taxrender income={trIncome:F2} deductions={trCo.subtotalDeductibleExpenses:F2} taxable={trTaxable:F2} tax={trTax:F2} estate={trCo.subtotalRealEstateTaxes:F2} total={trTotal:F2} sumbills={trCo.totalToPay:F2} match={System.Math.Abs(trTotal - trCo.totalToPay) <= 1f} pending={trPending.Count} lossrows={trLoss} rows={trRows}";
                }

                case "taxcounter":
                {
                    // What the IRS counter would show and charge: the patched GetIrsPaymentAmount itself.
                    if (SaveGameManager.Current == null) return "ERR no world loaded";
                    float tcOwn = 0f;
                    try { tcOwn = Helpers.TaxHelper.GetCurrentTaxesToPay(); } catch { }
                    var tcM = HarmonyLib.AccessTools.Method(typeof(UI.Purchase.PurchaseUI), "GetIrsPaymentAmount", new Type[] { typeof(Entities.TaxPaymentType) });
                    if (tcM == null) return "ERR PurchaseUI.GetIrsPaymentAmount not found";
                    float tcShow = (float)(tcM.Invoke(null, new object[] { Entities.TaxPaymentType.CurrentTaxes }) ?? 0f);
                    return $"OK taxcounter show={tcShow:F2} own={tcOwn:F2} partners={CompanyBooks.PartnerTaxCurrentDue():F2} hascurrent={Helpers.TaxHelper.HasCurrentTaxesToPay()}";
                }

                case "taxgate":
                {
                    // The company's half of the game's $150,000 filing line, as PATCH G computes it.
                    // `qualifies` ignores the anniversary-day test, so it can be read on any day.
                    if (SaveGameManager.Current == null) return "ERR no world loaded";
                    float tgOwn = CompanyBooks.OwnLastYearSales();
                    float tgP   = CompanyBooks.PartnerLastYearSales();
                    float tgC   = tgOwn + tgP;
                    return $"OK taxgate own={tgOwn:F2} partners={tgP:F2} company={tgC:F2} qualifies={tgC >= 150000f} pending={CompanyBooks.AnniversaryPending}";
                }

                case "taxpay":
                {
                    // The game's OWN pay action, on the main thread, exactly as IRSEmployee.cs:51 calls it -
                    // with the counter's figure when no amount is given.  No new money path.
                    if (SaveGameManager.Current == null) return "ERR no world loaded";
                    float tyAmount;
                    if (arg.Length > 0)
                    {
                        if (!float.TryParse(arg.Trim(), out tyAmount)) return "ERR usage: taxpay [amount]";
                    }
                    else
                    {
                        var tyM = HarmonyLib.AccessTools.Method(typeof(UI.Purchase.PurchaseUI), "GetIrsPaymentAmount", new Type[] { typeof(Entities.TaxPaymentType) });
                        if (tyM == null) return "ERR PurchaseUI.GetIrsPaymentAmount not found";
                        tyAmount = (float)(tyM.Invoke(null, new object[] { Entities.TaxPaymentType.CurrentTaxes }) ?? 0f);
                    }
                    bool tyOk = Helpers.TaxHelper.PayCurrentTaxes(tyAmount);
                    float tyLeft = 0f;
                    try { tyLeft = Helpers.TaxHelper.GetCurrentTaxesToPay(); } catch { }
                    return $"OK taxpay ok={tyOk} ownleft={tyLeft:F2}";
                }

                // -- RIVAL-FAIR-2 levers (2026-09-12) -------------------------
                case "rivalstate":
                {
                    if (SaveGameManager.Current == null) return "ERR no world loaded";
                    var rvStates = MPServer.BuildRivalStates();
                    int rvActive = 0, rvDef = 0;
                    var rvParts = new System.Collections.Generic.List<string>();
                    foreach (var r in rvStates)
                    {
                        if (r.IsActive) rvActive++;
                        rvDef += r.Defenses.Count;
                        rvParts.Add($"{r.RivalId}:{(r.IsActive ? "A" : "-")}:{(r.IsDefeated ? "D" : "-")}:{r.Defenses.Count}");
                    }
                    return $"OK rivalstate rivals={rvStates.Count} active={rvActive} defenses={rvDef} [{string.Join(",", rvParts)}]";
                }

                case "rivalsig":
                {
                    if (SaveGameManager.Current == null) return "ERR no world loaded";
                    // H-RIVALPARITY-1 A: `rivalsig <pid>` on the host = the signature that player's machine should show.
                    string rsPid = arg.Trim();
                    if (rsPid.Length > 0 && MPServer.IsRunning) return $"OK rivalsig {MPServer.RivalStateSignatureFor(rsPid)}";
                    return $"OK rivalsig {MPServer.RivalStateSignature()}";
                }

                // H-RIVALPARITY-1 part A levers (2026-09-27): per-key rival attention and the rival's timeline data.
                case "rivalattn":
                {
                    if (SaveGameManager.Current == null) return "ERR no world loaded";
                    return MPRivalAttention.Lever(arg);
                }

                case "rivaltimeline":
                {
                    if (SaveGameManager.Current == null) return "ERR no world loaded";
                    return MPRivalAttention.TimelineLever(arg);
                }

                // H-RIVALPARITY-1 part D (2026-09-27): the rent block per player - find / gate / try / force / host.
                case "rivalrent":
                {
                    if (SaveGameManager.Current == null) return "ERR no world loaded";
                    return MPRivalAttention.RentLever(arg);
                }

                // -- H-RIVALPARITY-1 item C levers (user-approved 2026-09-27). ALL READ-ONLY. -----------
                // Do a CLIENT's shops count as sellers in the HOST's market/demand calculation?  The game builds
                // its seller count per (item, neighbourhood) in ProductMarketHelper.FillProvidersDictionary
                // (decompile :329-356), reads it through the private GetProviders (:358-361), turns it into demand
                // in CalculateDemand (:403-410) via UpdateMarketDemand (:220-275), and a rival's weekly income is
                // the sum of its shops' dailyIncomes (RivalData.WeeklyIncome, RivalData.cs:29-42), each filled
                // daily from GetEstimatedWeeklyIncome (EstimatedWeeklyIncomeHelper.cs:26-45, via
                // CompetitionHelper.UpdateDailyValuation :225-236), which reads GetNeighborhoodDemand (:134).
                // NOTHING here writes: the live seller walk RE-READS the same fields FillProvidersDictionary
                // reads instead of calling it (calling it would rebuild the game's cached table).
                case "providers":
                {
                    try
                    {
                        if (SaveGameManager.Current == null) return "ERR no world loaded";
                        var pvTk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (pvTk.Length != 2) return "ERR usage: providers <item|*> <neighbourhood|*>";
                        string pvItem = MktItem(pvTk[0]);
                        string pvNb   = MktNb(pvTk[1]);
                        if (pvItem == "*") return MktScan(pvNb);
                        if (pvNb == "*") return "ERR providers: a neighbourhood is required with an item";
                        int pvCached = MktCachedProviders(pvItem, pvNb);
                        var pvRows = new System.Collections.Generic.List<string>();
                        int pvLive = MktLiveProviders(pvItem, pvNb, pvRows, out int pvPlayerNot);
                        string pvStored = "?", pvDemand = "?";
                        try
                        {
                            var pvNd = Helpers.ProductMarketHelper.GetNeighborhoodDemand(pvItem, pvNb);
                            if (pvNd != null) { pvStored = pvNd.providers.ToString(); pvDemand = pvNd.demand.ToString(); }
                        }
                        catch { }
                        return $"OK providers item={pvItem} nb={pvNb} cached={pvCached} live={pvLive} stored={pvStored} demand={pvDemand} "
                             + $"playerStockedNotCounted={pvPlayerNot} rows={pvRows.Count} [{string.Join(" ; ", pvRows)}]";
                    }
                    catch (Exception pvEx) { return $"ERR providers: {pvEx.GetType().Name}: {pvEx.Message}"; }
                }

                case "marketdemand":
                {
                    try
                    {
                        if (SaveGameManager.Current == null) return "ERR no world loaded";
                        var mdTk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (mdTk.Length != 2) return "ERR usage: marketdemand <item> <neighbourhood>";
                        string mdItem = MktItem(mdTk[0]);
                        string mdNb   = MktNb(mdTk[1]);
                        var mdIt = BigAmbitions.Items.ItemsGetter.GetByName(mdItem);
                        if (mdIt == null) return $"ERR marketdemand: unknown item '{mdItem}'";
                        bool mdCan = false;
                        try { mdCan = Helpers.ProductMarketHelper.CanNeighborhoodHaveItemDemand(mdNb, mdItem); } catch { }
                        // The value every consumer reads (EstimatedWeeklyIncomeHelper.cs:134): GetNeighborhoodDemand (:745).
                        var mdNd = Helpers.ProductMarketHelper.GetNeighborhoodDemand(mdItem, mdNb);
                        int mdOpt = MktOptimal(mdIt);
                        int mdCached = MktCachedProviders(mdItem, mdNb);
                        int mdLive = MktLiveProviders(mdItem, mdNb, new System.Collections.Generic.List<string>(), out int mdNot);
                        // CalculateDemand :408 - active market events on this item here (or city-wide).
                        int mdEv = 0, mdEvN = 0;
                        try
                        {
                            foreach (var me in SaveGameManager.Current.marketEvents)
                            {
                                if (me == null || !me.IsActive || me.itemName != mdItem) continue;
                                if (!string.IsNullOrEmpty(me.neighbourhood) && me.neighbourhood != mdNb) continue;
                                mdEv += me.demandImpact; mdEvN++;
                            }
                        }
                        catch { }
                        return $"OK marketdemand item={mdItem} nb={mdNb} canHaveDemand={mdCan} demand={mdNd?.demand} storedProviders={mdNd?.providers} "
                             + $"monopoly={mdNd?.hasPlayerMonopoly} lastDaySold={mdNd?.lastDaySold} optimal={mdOpt} cachedProviders={mdCached} "
                             + $"liveProviders={mdLive} playerStockedNotCounted={mdNot} events={mdEvN}:{mdEv} "
                             + $"formulaCached={MktFormula(mdCached, mdOpt)} formulaLive={MktFormula(mdLive, mdOpt)} day={SaveGameManager.Current.Day}";
                    }
                    catch (Exception mdEx) { return $"ERR marketdemand: {mdEx.GetType().Name}: {mdEx.Message}"; }
                }

                case "rivalincome":
                {
                    try
                    {
                        if (SaveGameManager.Current == null) return "ERR no world loaded";
                        string riArg = arg.Trim();
                        if (riArg.Length == 0) return "ERR usage: rivalincome <rivalId|neighbourhood>";
                        bool riNbMode = riArg.StartsWith("ba:neighborhood_", StringComparison.Ordinal);
                        var riIds = new System.Collections.Generic.List<string>();
                        string riSpecial = "";
                        if (riNbMode)
                        {
                            try { riSpecial = BigAmbitions.Rivals.RivalsHelper.GetSpecialRivalByNeighborhood(riArg)?.rivalData?.id ?? ""; } catch { }
                            if (riSpecial.Length > 0) riIds.Add(riSpecial);
                            foreach (var rr in SaveGameManager.Current.BuildingRegistrations)
                            {
                                if (rr == null) continue;
                                string rrNb = ""; try { rrNb = Helpers.BuildingHelper.GetBuilding(rr.Address)?.Neighbourhood ?? ""; } catch { }
                                if (rrNb != riArg) continue;
                                string rrOwner = ""; try { rrOwner = rr.businessOwnerRivalId?.ToString() ?? ""; } catch { }
                                if (rrOwner.Length == 0 || GameStatePatcher.IsSessionPlayerRivalId(rrOwner) || riIds.Contains(rrOwner)) continue;
                                riIds.Add(rrOwner);
                            }
                        }
                        else riIds.Add(riArg);
                        var riRows = new System.Collections.Generic.List<string>();
                        foreach (var rid in riIds)
                        {
                            if (riRows.Count >= 10) { riRows.Add("..."); break; }
                            BigAmbitions.Rivals.RivalData? rd = null;
                            try { rd = BigAmbitions.Rivals.RivalsHelper.GetRivalData(rid); } catch { }
                            if (rd == null) { riRows.Add($"{rid}|nodata"); continue; }
                            string rWi = "?"; float rWiF = 0f;
                            try { rWiF = rd.WeeklyIncome; rWi = rWiF.ToString("F0"); } catch (Exception rwx) { rWi = "ERR:" + rwx.GetType().Name; }
                            var rRo = rd.ownedRetailOfficeBusinesses;
                            int rN = rRo?.Count ?? -1, rInNb = 0, rEstN = 0;
                            float rEst = 0f;
                            if (rRo != null)
                                foreach (var rb in rRo)
                                {
                                    if (rb == null) continue;
                                    try { if (riNbMode && Helpers.BuildingHelper.GetBuilding(rb.Address)?.Neighbourhood == riArg) rInNb++; } catch { }
                                    try { rEst += Helpers.EstimatedWeeklyIncomeHelper.GetEstimatedWeeklyIncome(rb); rEstN++; } catch { }
                                }
                            // RivalTimeline.CheckSurrender :134-137 - past the income gate when no retail/office shop or income < 2000.
                            bool rUnder = !(rN > 0 && !(rWiF < 2000f));
                            riRows.Add($"{rid}|name={rd.rivalName}|weeklyIncome={rWi}|retailOffice={rN}|inNb={rInNb}|liveEstimate={rEst:F0}({rEstN})|underSurrenderLine={rUnder}");
                        }
                        return $"OK rivalincome arg={riArg} specialRival='{riSpecial}' rivals={riRows.Count} [{string.Join(" ; ", riRows)}]";
                    }
                    catch (Exception riEx) { return $"ERR rivalincome: {riEx.GetType().Name}: {riEx.Message}"; }
                }

                case "rivalfire":
                {
                    if (!MPServer.IsRunning) return "ERR host only";
                    int rfSp = arg.IndexOf(' ');
                    if (rfSp < 0) return "ERR usage: rivalfire price|lowdemand <neighborhood>|@<address>";
                    string rfKind = arg.Substring(0, rfSp).Trim().ToLowerInvariant();
                    string rfTarget = arg.Substring(rfSp + 1).Trim();
                    if (rfKind != "price" && rfKind != "lowdemand") return "ERR usage: rivalfire price|lowdemand <neighborhood>|@<address>";
                    string rfNb = rfTarget;
                    if (rfTarget.StartsWith("@"))
                    {
                        string rfKey = rfTarget.Substring(1);
                        var rfReg = GameStatePatcher.FindRegistration(rfKey);
                        if (rfReg == null) return $"ERR no registration '{rfKey}'";
                        try { rfNb = rfReg.Neighborhood ?? ""; } catch { rfNb = ""; }
                    }
                    if (rfNb.Length == 0) return "ERR no neighborhood";
                    // Rig run 4: the game's own ActivateLowDemand/ActivatePriceReduction assume a special rival
                    // lives in the neighbourhood (RivalDefenseHelper.cs:107/:115 read rival.rivalData.id) - native
                    // only ever calls them from that rival's own timeline. Refuse instead of letting native throw.
                    BigAmbitions.Rivals.SpecialRival? rfRival = null;
                    try { rfRival = BigAmbitions.Rivals.RivalsHelper.GetSpecialRivalByNeighborhood(rfNb); } catch { }
                    if (rfRival == null) return $"ERR no special rival in '{rfNb}' (the game's mechanics need one there)";
                    bool rfResult;
                    try
                    {
                        // Priority.High (Enums.Priority; the aggression band RivalDefenseHelper maps to
                        // 65%/8 days for a price war and 7 new shops for a low-demand wave, :243/:259).
                        rfResult = rfKind == "price"
                            ? BigAmbitions.Rivals.RivalDefenseHelper.ActivatePriceReduction(rfNb, Enums.Priority.High)
                            : BigAmbitions.Rivals.RivalDefenseHelper.ActivateLowDemand(rfNb, Enums.Priority.High);
                    }
                    catch (Exception rfEx) { return $"ERR rivalfire {rfKind}: {rfEx.GetType().Name}: {rfEx.Message}"; }
                    return $"OK rivalfire {rfKind} nb='{rfNb}' rival='{rfRival.rivalData?.id ?? ""}' result={rfResult}";
                }

                case "rivalnews":
                {
                    if (!MPServer.IsRunning) return "ERR host only";
                    string rnKey = arg.Trim();
                    if (rnKey.Length == 0) return "ERR usage: rivalnews <key>";
                    BigAmbitions.Rivals.SpecialRival rnRival = null!;
                    try
                    {
                        var rnAll = BigAmbitions.Rivals.RivalsHelper.GetSpecialRivals();
                        if (rnAll != null) foreach (var r in rnAll) { if (r != null) { rnRival = r; break; } }
                    }
                    catch (Exception rnEx) { return $"ERR rivalnews: {rnEx.GetType().Name}: {rnEx.Message}"; }
                    if (rnRival == null) return "ERR no special rival in this world";
                    Entities.Contact rnContact = null!;
                    try { rnContact = BigAmbitions.Rivals.RivalsHelper.GetRivalContact(rnRival); } catch { }
                    if (rnContact == null) return "ERR that rival has no contact";
                    CompanyMessages.ResetRivalNewsProbe();
                    // Through the REAL gateway, so the lever can never take a different path than the game.
                    try { rnContact.SendMessage(new Entities.TextMessage(rnKey)); }
                    catch (Exception rnSx) { return $"ERR rivalnews send: {rnSx.GetType().Name}: {rnSx.Message}"; }
                    return $"OK rivalnews key='{rnKey}' rival='{rnRival.rivalData?.id ?? ""}'"
                         + $" relayed={CompanyMessages.LastRivalNewsCount}"
                         + $" hostcopy={(CompanyMessages.LastRivalNewsHostKept ? "kept" : "suppressed")}";
                }

                // rivalmono <rivalId> (M4) - run the rival's OWN monologue path: the PUBLIC
                // RivalsHelper.SendMessageToPlayer overload for the first timeline entry that has a clip and has
                // not been sent yet, so the host-side monologue skip and the native tail (contact send +
                // bookkeeping) are both exercised through the game's own code.
                case "rivalmono":
                {
                    if (!MPServer.IsRunning) return "ERR host only";
                    string rmId = arg.Trim();
                    if (rmId.Length == 0) return "ERR usage: rivalmono <rivalId>";
                    BigAmbitions.Rivals.SpecialRival rmRival = null!;
                    try { rmRival = BigAmbitions.Rivals.RivalsHelper.GetSpecialRival(rmId); }
                    catch (Exception rmEx) { return $"ERR rivalmono: {rmEx.GetType().Name}: {rmEx.Message}"; }
                    if (rmRival == null) return $"ERR unknown rival '{rmId}'";
                    // TimelineEntry.messageLocalizationKey / .messageClip, out of RivalTimeline.allEntries (decompile
                    // RivalTimeline.cs:55; its own send at :243). HasMessageBeenSent is the game's own sentMessageKeys
                    // test (RivalsHelper.cs:529-531), so the lever and native can never disagree about what is unsent.
                    string rmKey = "";
                    UnityEngine.AudioClip rmClip = null!;
                    try
                    {
                        var rmEntries = rmRival.timeline?.allEntries;
                        if (rmEntries != null)
                            foreach (var rmE in rmEntries)
                            {
                                if (rmE == null || rmE.messageClip == null) continue;
                                // Fold c (user 2026-09-12): employee poaching is OFF in MP (MPRivalFairness.Patch_NoPoachingInMP
                                // forces ActivateHireEmployees false, so the game never completes that entry and never sends
                                // its announcement). The lever must not send it either - a rival promising to hire away
                                // your staff, followed by nothing, is exactly the confusion the mechanic's removal avoids.
                                if (rmE.defense == BigAmbitions.Rivals.DefensiveMechanic.HireBestEmployees) continue;
                                string rmK = rmE.messageLocalizationKey ?? "";
                                if (rmK.Length == 0) continue;
                                if (BigAmbitions.Rivals.RivalsHelper.HasMessageBeenSent(rmId, rmK)) continue;
                                rmKey = rmK; rmClip = rmE.messageClip; break;
                            }
                    }
                    catch (Exception rmTx) { return $"ERR rivalmono timeline: {rmTx.GetType().Name}: {rmTx.Message}"; }
                    if (rmKey.Length == 0) return $"ERR no unsent clipped timeline entry for '{rmId}'";
                    try { BigAmbitions.Rivals.RivalsHelper.SendMessageToPlayer(rmId, rmKey, rmClip, null); }
                    catch (Exception rmSx) { return $"ERR rivalmono send: {rmSx.GetType().Name}: {rmSx.Message}"; }
                    return $"OK rivalmono {rmId} key={rmKey}";
                }

                case "negotiations":
                {
                    // NEGO-ORPHAN: what StripOrphanNegotiations would take now - the salary negotiations whose embedded
                    // person is a PARTNER's copy here (injected staff or candidate).  Counts only, nothing is removed;
                    // the test is the strip's own (MPRegisterSync.IsStrippableNegotiation), so the lever and the strip
                    // cannot disagree.  Off a merged session no copy exists, so the answer is orphan=0.
                    int ngTotal = 0, ngOrphan = 0;
                    var ngIds = new StringBuilder();
                    try
                    {
                        var ngGi = SaveGameManager.Current;
                        if (ngGi == null) return "ERR no world loaded";   // r1 MINOR-2
                        var ngNegs = ngGi.candidateSalaryNegotiations;
                        if (ngNegs != null)
                            foreach (var ngNeg in ngNegs)
                            {
                                ngTotal++;
                                if (!MPRegisterSync.IsStrippableNegotiation(ngNeg)) continue;
                                string ngId = ""; try { ngId = ngNeg?.employeeInstance?.id ?? ""; } catch { }
                                ngOrphan++;
                                if (ngOrphan <= 12) { if (ngIds.Length > 0) ngIds.Append(", "); ngIds.Append(ngId.Length > 0 ? ngId : "(no id)"); }
                            }
                    }
                    catch (Exception ngEx) { return $"ERR negotiations: {ngEx.Message}"; }
                    return $"OK negotiations total={ngTotal} orphan={ngOrphan} [{ngIds}{(ngOrphan > 12 ? ", ..." : "")}]";
                }

                case "messages":
                {
                    // Phase 4b (people) P4: the relayed phone AS THIS MACHINE SEES IT - the copies of a
                    // partner's messages and the ones this machine raised and relayed, each with its id,
                    // its contact, how many buttons it still offers and whether it has been handled.
                    // An argument caps how many lines come back (default 15).
                    int mmax = 15;
                    if (arg.Length > 0 && !int.TryParse(arg, out mmax)) return "ERR usage: messages [n]";
                    if (mmax < 1) mmax = 1;
                    var mlines = CompanyMessages.Readout(mmax);
                    var msb = new StringBuilder();
                    foreach (var mline in mlines)
                    {
                        Plugin.Logger.LogWarning($"[TestDrive] message: {mline}");
                        if (msb.Length > 0) msb.Append(" ; ");
                        msb.Append(mline);
                    }
                    return $"OK {mlines.Count} relayed message(s) shown (copies here {CompanyMessages.CopyCount}): {msb}";
                }

                case "press":
                {
                    // The same message a relayed copy's button sends when it is clicked: the host hands it
                    // to the OWNER, whose machine runs the original closure once and tells the company.
                    var ptk = arg.Split(' ');
                    if (ptk.Length < 2) return "ERR usage: press <messageId> <buttonIndex>";
                    if (!int.TryParse(ptk[1], out var pidx)) return "ERR usage: press <messageId> <buttonIndex>";
                    string pwhy = CompanyMessages.CommitPress(ptk[0], pidx, out var psent, out var preason);
                    if (pwhy.Length > 0) return $"ERR press message='{ptk[0]}' button={pidx}: {pwhy}";
                    return $"OK press message='{ptk[0]}' button={pidx} sent={psent}"
                         + (preason.Length > 0 ? $" reason='{preason}'" : " (the owner runs it, once)");
                }

                case "relaymsg":
                {
                    // L7: re-send one of MY OWN messages as a fresh relay - the same DTO through the same
                    // ownership gate as the Contact.SendMessage postfix, so the rig can drive a receiver
                    // without waiting for the game to raise a second message. A message that is not mine, or
                    // whose contact has gone, is an ERR rather than a quiet no-op.
                    string rarg = arg.Trim();
                    if (rarg.Length == 0) return "ERR usage: relaymsg <messageId> | relaymsg native [<contactId>]";
                    if (rarg == "native" || rarg.StartsWith("native ", StringComparison.Ordinal))
                    {
                        // N1: the id form only reaches messages relayed SINCE this connection, so a fixture
                        // whose phone filled before the connect could not be driven at all. This picks the
                        // NEWEST NATIVE message on one of my own contacts (buttons preferred) and relays it.
                        string rfilter = rarg.Length > 6 ? rarg.Substring(7).Trim() : "";
                        string nwhy = CompanyMessages.CommitRelayNative(rfilter, out var nid, out var ncontact,
                                                                       out var nkey, out var nbuttons, out var nsent);
                        return nwhy.Length == 0
                            ? $"OK relaymsg id='{nid}' contact='{ncontact}' key='{nkey}' buttons={nbuttons} sent={nsent}"
                            : $"ERR relaymsg native: {nwhy}";
                    }
                    string rwhy = CompanyMessages.CommitRelay(rarg, out var rid, out var rcontact, out var rbuttons, out var rsent);
                    return rwhy.Length == 0
                        ? $"OK relaymsg id='{rid}' contact='{rcontact}' buttons={rbuttons} sent={rsent}"
                        : $"ERR relaymsg message='{rarg}': {rwhy}";
                }

                case "pmnotice":
                {
                    // MERGER PHASE 4d (D24). Raises the GAME'S OWN pricing-manager notice - the same key and the same
                    // two-entry data table PricingManagerPlan.NotifyMispricedItems builds (decompile
                    // Buildings.Office.Headquarters/PricingManagerPlan.cs:195, employeeName + amount) - and nothing
                    // else, so NotificationRelay's Show postfix takes it exactly as it takes the real notice and the
                    // employee->workplace resolve is driven end to end. The mod contributes no text of its own.
                    // 'name:<text>' raises it with a LITERAL name, which is how the zero-match and the ambiguous
                    // paths are driven. This handler runs on the main thread (the 0.5s .cmd poll), like 'transfer'.
                    string pname = "";
                    int pcount = 1;
                    if (arg.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    {
                        // A person's name CONTAINS SPACES, so the optional count is taken off the end only when the
                        // last token is a number; everything before it is the name.
                        string prest = arg.Substring(5).Trim();
                        int psp = prest.LastIndexOf(' ');
                        if (psp > 0 && int.TryParse(prest.Substring(psp + 1).Trim(), out var pn))
                        { pcount = pn; prest = prest.Substring(0, psp).Trim(); }
                        pname = prest;
                        if (pname.Length == 0) return "ERR usage: pmnotice name:<text> [count]";
                    }
                    else
                    {
                        var ptk = arg.Split(' ');
                        string pempId = ptk[0].Trim();
                        if (pempId.Length == 0) return "ERR usage: pmnotice <employeeId>|name:<text> [count]";
                        if (ptk.Length > 1 && ptk[1].Trim().Length > 0 && !int.TryParse(ptk[1].Trim(), out pcount))
                            return "ERR usage: pmnotice <employeeId> [count]";
                        var pemp = FindEmployee(pempId);
                        if (pemp == null) return $"ERR no employee '{pempId}' on this machine";
                        bool pinj = false; try { pinj = MPRegisterSync.IsInjectedStaff(pempId); } catch { }
                        if (pinj) return $"ERR employee '{pempId}' is an injected partner copy - the real notice about them is raised on the machine that OWNS them";
                        try { pname = pemp.characterData?.name?.ToString() ?? ""; } catch { }
                        if (pname.Length == 0) return $"ERR employee '{pempId}' has no character name";
                    }
                    if (pcount < 1) return "ERR pmnotice count must be 1 or more";
                    try
                    {
                        UI.Notification.Notifications.Show(UI.Notification.NotificationType.Info,
                            "notifications_PricingManager_mispriced_items",
                            new System.Collections.Generic.Dictionary<string, string>
                            {
                                { "employeeName", pname },
                                { "amount", pcount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            },
                            4f, null, null);
                    }
                    catch (Exception ex) { return $"ERR pmnotice show: {ex.Message}"; }
                    return $"OK pmnotice name='{pname}' amount={pcount}";
                }

                case "cues":
                {
                    // MERGER PHASE 4d (R6). Read-only. What the presentation layer is painting right now:
                    // every COMPANY building this machine shows as its own because of the merger flip, with its
                    // owner and that owner's colour, and every partner VEHICLE whose map pin is being kept alive
                    // here. Nothing is written. r2 (review MAJOR-1; run 1's post-dissolve pass was VACUOUS — the
                    // old non-member early return never looked at the pins): the pins are listed on EVERY answer —
                    // a kept pin belongs to any drivable ghost (a direct key grant keeps one too) and the
                    // post-dissolve check exists to prove they are GONE once membership ends. Only the residences
                    // list is merger-only; the suffix still names a machine that is in no company.
                    bool cmember = MergerSync.IAmMember;
                    var cres = new System.Collections.Generic.List<string>();
                    try
                    {
                        var cgi = SaveGameManager.Current;
                        if (cmember && cgi?.BuildingRegistrations != null)
                            foreach (var creg in cgi.BuildingRegistrations)
                            {
                                if (creg == null || !HousingMapCues.IsFlippedPartnerResidence(creg)) continue;
                                string ckey = GameStateReader.AddressKey(creg);
                                string cowner = PlayerColours.FlipProofOwner(ckey);
                                cres.Add($"{ckey}:{(cowner.Length == 0 ? "?" : cowner)}:{PlayerColours.ColourNameOf(cowner)}");
                            }
                    }
                    catch (Exception ex) { return $"ERR cues residences: {ex.Message}"; }
                    var cpins = new System.Collections.Generic.List<string>();
                    try { foreach (var cp in VehicleManager.KeptPins()) cpins.Add($"{cp.vid}:{cp.owner}"); }
                    catch (Exception ex) { return $"ERR cues pins: {ex.Message}"; }
                    return $"OK cues residences=[{string.Join(",", cres)}] pins=[{string.Join(",", cpins)}]" + (cmember ? "" : " (not in a company)");
                }

                case "poachmsg":
                {
                    // r4 P1: the game builds NATIVE message buttons in exactly TWO places, and both are rival
                    // poach raise requests (decompile Entities/EmployeeInstance.cs:1051-1075 PoachByRival,
                    // :1145-1165 StopPoachingRivalSurrender). This drives the first one on one of MY OWN
                    // people, so the rig can raise a genuine two-button NATIVE message instead of a
                    // relay-built one; it then travels by itself through the Contact.SendMessage postfix.
                    // This handler already runs on the main thread (the 0.5s .cmd poll), like 'transfer'.
                    var gtk = arg.Split(' ');
                    string gempId = gtk[0].Trim();
                    if (gempId.Length == 0) return "ERR usage: poachmsg <employeeId> [percent] [days]";
                    int gpct = 5, gdays = 30;      // a LONG deadline, so an aborted run never loses the person to the poach
                    if (gtk.Length > 1 && gtk[1].Length > 0 && !int.TryParse(gtk[1], out gpct)) return "ERR usage: poachmsg <employeeId> [percent] [days]";
                    if (gtk.Length > 2 && gtk[2].Length > 0 && !int.TryParse(gtk[2], out gdays)) return "ERR usage: poachmsg <employeeId> [percent] [days]";
                    // 4d MINOR: both numbers go straight into a REAL raise request on a real person, so a typo is
                    // permanent - a negative percent writes a wage CUT and 0 days arms an immediate deadline.
                    if (gpct  < 1 || gpct  > 100) return "ERR poachmsg percent must be 1..100";
                    if (gdays < 1 || gdays > 365) return "ERR poachmsg days must be 1..365";
                    var gemp = FindEmployee(gempId);
                    if (gemp == null) return $"ERR no employee '{gempId}' on this machine";
                    bool ginj = false; try { ginj = MPRegisterSync.IsInjectedStaff(gempId); } catch { }
                    if (ginj) return $"ERR employee '{gempId}' is an injected partner copy - run poachmsg on the machine that OWNS them";
                    string grival = "";
                    try
                    {
                        var ggi = SaveGameManager.Current;
                        if (ggi == null) return "ERR no save is loaded on this machine";
                        if (ggi.rivalStates != null)
                            foreach (var grs in ggi.rivalStates)
                                if (grs != null && !string.IsNullOrEmpty(grs.rivalId)) { grival = grs.rivalId; break; }
                        if (grival.Length == 0 && ggi.wholesaleRivalIds != null)
                            foreach (var gw in ggi.wholesaleRivalIds)
                                if (!string.IsNullOrEmpty(gw)) { grival = gw; break; }
                        if (grival.Length == 0 && ggi.importRivalIds != null)
                            foreach (var gim in ggi.importRivalIds)
                                if (!string.IsNullOrEmpty(gim)) { grival = gim; break; }
                    }
                    catch (Exception gx) { return $"ERR reading this save's rivals: {gx.GetType().Name}: {gx.Message}"; }
                    if (grival.Length == 0) return "ERR this save holds no rival, so there is no rival id to poach with";
                    float gold = gemp.hourlyWage;
                    float gnew = gold * (1f + (float)gpct / 100f);          // EmployeeInstance.cs:1053, the game's own sum
                    try { gemp.PoachByRival(grival, gdays, gpct); }
                    catch (Exception px) { return $"ERR poachmsg employee='{gempId}': {px.GetType().Name}: {px.Message}"; }
                    var gci = System.Globalization.CultureInfo.InvariantCulture;
                    return $"OK poachmsg employee='{gempId}' rival='{grival}' percent={gpct} days={gdays}"
                         + $" wage={gold.ToString("F2", gci)}->{gnew.ToString("F2", gci)}";
                }

                case "cargo":
                {
                    // 4c part 2: the HOST's in-transit CARGO table - goods that have left a member's
                    // warehouse and are not yet on another member's shelves. A member has nothing to show
                    // here by design (the host is the only holder). NOT `transfers`, which build A already
                    // uses for the host-held EMPLOYEE moves.
                    if (!MPServer.IsRunning) return "ERR cargo is a host verb - the host holds the in-transit cargo table";
                    var crows = MPServer.CargoReadout();
                    var cgsb = new StringBuilder();
                    foreach (var crow in crows)
                    {
                        Plugin.Logger.LogWarning($"[TestDrive] cargo: {crow}");
                        if (cgsb.Length > 0) cgsb.Append(" ; ");
                        cgsb.Append(crow);
                    }
                    return $"OK cargo pending={crows.Count}{(crows.Count > 0 ? " [" + cgsb + "]" : "")}";
                }

                case "legs":
                {
                    // 4c part 2b L1: READ-ONLY.  THIS machine's OWN logistics manager plans - the legs the
                    // cargo transfer can be raised on.  Wave 4's TAGGED DISPLAY COPIES sit in the same
                    // gi.logisticsManagerPlans list and are a partner's rows, not mine, so they are excluded
                    // through the same tag test CompanyPlans.OwnCount uses (MergerAbsence.IsDisplayInstall).
                    var lgi = SaveGameManager.Current;
                    var lgsb = new StringBuilder();
                    if (lgi?.logisticsManagerPlans != null)
                        foreach (var lp in lgi.logisticsManagerPlans)
                        {
                            if (lp == null) continue;
                            bool ldisp; try { ldisp = MergerAbsence.IsDisplayInstall(lp); } catch { ldisp = false; }
                            if (ldisp) continue;
                            string lsrc = ""; try { lsrc = GameStateReader.AddressKey(lp.targetAddress); } catch { }
                            int ldn = 0;     try { ldn  = lp.destinations != null ? lp.destinations.Count : 0; } catch { }
                            if (lgsb.Length > 0) lgsb.Append(",");
                            lgsb.Append($"{lp.id}:src={lsrc}:dests={ldn}");
                        }
                    return $"OK legs plans=[{lgsb}]";
                }

                case "stockof":
                {
                    // 4c part 2b L2: READ-ONLY.  Every item with stock at ONE registration, walked exactly
                    // where BuildingHelper.GetItemsWithStock walks it (BuildingHelper.cs:446-455 - the
                    // registration's itemInstances, each holder's cargoInstances, a cargo with nested cargo
                    // skipped).  That helper takes ONE itemName and BuildingHelper's shelf-item filter
                    // (ShelfItemNames) is private, so the walk here is the same one without those two
                    // narrowings: it reports every item name the registration holds cargo of.
                    if (arg.Length == 0) return "ERR usage: stockof <number> <ba:street_x>";
                    var soreg = GameStatePatcher.FindRegistration(arg);
                    if (soreg == null) return $"ERR no registration at '{arg}'";
                    string sokey = arg; try { sokey = GameStateReader.AddressKey(soreg); } catch { }
                    var sotot = new System.Collections.Generic.SortedDictionary<string, int>(StringComparer.Ordinal);
                    try
                    {
                        if (soreg.itemInstances != null)
                            foreach (var sokv in soreg.itemInstances)
                            {
                                var soii = sokv.Value;
                                if (soii?.cargoInstances == null) continue;
                                foreach (var soc in soii.cargoInstances)
                                {
                                    if (soc == null || soc.amount <= 0) continue;
                                    if (soc.nestedCargoInstances != null && soc.nestedCargoInstances.Count > 0) continue;
                                    string soname = soc.itemName ?? "";
                                    if (soname.Length == 0) continue;
                                    int soprev; sotot.TryGetValue(soname, out soprev);
                                    sotot[soname] = soprev + soc.amount;
                                }
                            }
                    }
                    catch (Exception sox) { return $"ERR stockof addr='{sokey}': {sox.GetType().Name}: {sox.Message}"; }
                    var sosb = new StringBuilder();
                    foreach (var sokv2 in sotot)
                    {
                        if (sosb.Length > 0) sosb.Append(",");
                        sosb.Append($"{sokv2.Key}:{sokv2.Value}");
                    }
                    return $"OK stockof addr='{sokey}' items=[{sosb}]";
                }

                case "salestats":
                {
                    // H-SALEHOLE-1 (rig oracle). READ-ONLY: every number the sale-hole question turns on,
                    // on ONE line — who runs this shop's live customers here, what this machine believes it
                    // owns, what its shopper table holds, and what actually crossed the wire. The address key
                    // is two tokens, like `stockof` / `regstate`.
                    if (arg.Length == 0) return "ERR usage: salestats <number> <ba:street_x>";
                    var ssReg = GameStatePatcher.FindRegistration(arg);
                    if (ssReg == null) return $"ERR no registration at '{arg}'";
                    string ssKey = arg; try { ssKey = GameStateReader.AddressKey(ssReg); } catch { }
                    string ssSim = "";  try { ssSim = CustomerPuppets.SimulatorFor(ssKey); } catch { }
                    bool ssInside = false;
                    try { ssInside = BuildingManager.IsInsideBuilding
                                     && string.Equals(MPRegisterSync.CurrentShopAddress ?? "", ssKey, StringComparison.Ordinal); } catch { }
                    bool ssRented = false, ssFlip = false, ssMine = false;
                    try { ssRented = ssReg.RentedByPlayer; }        catch { }
                    try { ssFlip   = MergerFlip.IsFlipped(ssKey); } catch { }
                    try { ssMine   = MergerFlip.TrulyMine(ssReg); } catch { }
                    int ssTotal = -1, ssDone = -1;
                    try { var ssSt = CustomerEntrySync.EntryStatsFor(ssReg.Address); ssTotal = ssSt.total; ssDone = ssSt.completed; } catch { }
                    int ssPending = ssTotal < 0 ? -1 : ssTotal - ssDone;
                    int ssSeeded = 0;      try { ssSeeded = CustomerEntrySync.SeededIdCountFor(ssReg.Address); } catch { }
                    int ssLive = -1;       try { if (ssInside) ssLive = CustomerPuppets.LiveCustomerCount; } catch { }
                    bool ssSpawnOff = false; try { ssSpawnOff = CustomerPuppets.SpawnerSuppressedHere; } catch { }
                    int ssSent = 0;        try { ssSent = Patch_Order_Pay_HelperForward.SentCount; } catch { }
                    int ssAdopted = 0;     try { ssAdopted = CustomerEntrySync.AdoptedCountFor(ssKey); } catch { }
                    int ssUnproc = -1;     try { ssUnproc = ssReg.unprocessedCompletedOrders?.Count ?? -1; } catch { }
                    bool ssHelper = false; try { ssHelper = GrantSync.IsHelperBusiness(ssKey); } catch { }
                    return $"OK salestats {ssKey} sim='{ssSim}' inside={ssInside} rentedRaw={ssRented} flipped={ssFlip} "
                         + $"trulyMine={ssMine} entries={ssTotal} pending={ssPending} completed={ssDone} seededIds={ssSeeded} "
                         + $"liveCustomers={ssLive} spawnDisabled={ssSpawnOff} forwardedSent={ssSent} adopted={ssAdopted} "
                         + $"unprocessed={ssUnproc} helperHere={ssHelper}";
                }

                case "cargotest":
                {
                    // 4c part 2b L3: DRIVER.  The game runs a logistics plan's delivery pass on its OWN
                    // schedule and the rig cannot wait for it, so this raises ONE real leg on demand: it
                    // APPENDS a real destination to a real own plan and calls the game's own
                    // LogisticsManagerPlan.DeliverDestination (LogisticsManagerPlan.cs:74) - nothing else.
                    // The mod's leg gate (Patch_LogisticsPlanLeg_MergerGate) sits on that method and hands a
                    // mixed-owner leg to CargoTransfer.TakeOver.  FIXTURE: `cargotest remove <plan> <addr>`
                    // takes the appended destination back out; the scenario never saves after a cargotest.
                    var ctk = arg.Split(' ');
                    bool ctrem = ctk.Length > 0 && ctk[0] == "remove";
                    if (ctrem ? ctk.Length != 4 : ctk.Length != 5)
                        return "ERR usage: cargotest <planId|first> <number> <ba:street_x> <itemName> <amount>"
                             + "  |  cargotest remove <planId|first> <number> <ba:street_x>";
                    // The address key holds a space, so the argument arity fixes its two tokens - the same
                    // shape `transfer <employeeId> <num> <ba:street_x>` uses.
                    string ctplan = ctrem ? ctk[1] : ctk[0];
                    string ctdest = ctrem ? (ctk[2] + " " + ctk[3]) : (ctk[1] + " " + ctk[2]);
                    var ctgi = SaveGameManager.Current;
                    if (ctgi?.logisticsManagerPlans == null) return "ERR this machine holds no logistics manager plans";
                    Buildings.Office.Headquarters.LogisticsManagerPlan ctp = null;
                    foreach (var cp in ctgi.logisticsManagerPlans)
                    {
                        if (cp == null) continue;
                        bool cdisp; try { cdisp = MergerAbsence.IsDisplayInstall(cp); } catch { cdisp = false; }
                        if (cdisp) continue;                    // a partner's display copy is not this machine's to run
                        if (ctplan == "first" || cp.id == ctplan) { ctp = cp; break; }
                    }
                    if (ctp == null) return $"ERR no own logistics plan '{ctplan}' on this machine";
                    if (ctp.destinations == null) return $"ERR plan '{ctp.id}' holds no destination list";
                    var ctreg = GameStatePatcher.FindRegistration(ctdest);
                    if (ctreg == null) return $"ERR no registration at '{ctdest}'";
                    string ctdkey = ctdest; try { ctdkey = GameStateReader.AddressKey(ctreg); } catch { }
                    string ctsrc  = "";     try { ctsrc  = GameStateReader.AddressKey(ctp.targetAddress); } catch { }

                    if (ctrem)
                    {
                        int ctn = 0;
                        for (int ci = ctp.destinations.Count - 1; ci >= 0; ci--)
                        {
                            var cd = ctp.destinations[ci];
                            if (cd == null) continue;
                            string ck = ""; try { ck = GameStateReader.AddressKey(cd.deliveryTargetAddress); } catch { }
                            if (ck == ctdkey) { ctp.destinations.RemoveAt(ci); ctn++; }
                        }
                        return $"OK cargotest removed={ctn}";
                    }

                    string ctitem = ctk[3];
                    int ctamt;
                    if (!int.TryParse(ctk[4], out ctamt) || ctamt < 1 || ctamt > 999)
                        return $"ERR amount '{ctk[4]}' is not 1..999";
                    bool ctknown = false;
                    try { ctknown = BigAmbitions.Items.ItemsGetter.GetByName(ctitem) != null; } catch { }
                    if (!ctknown) return $"ERR no item '{ctitem}'";

                    var ctd = new Entities.LogisticsManagerPlanDestination { deliveryTargetAddress = ctreg.Address };
                    ctd.stockTargets.Add(new BigAmbitions.Items.ItemAmountTarget(ctitem, ctamt));   // CargoTransfer.cs:253
                    ctp.destinations.Add(ctd);
                    try { ctp.DeliverDestination(ctd); }
                    catch (Exception ctx) { return $"ERR cargotest plan='{ctp.id}': {ctx.GetType().Name}: {ctx.Message}"; }
                    return $"OK cargotest plan='{ctp.id}' src='{ctsrc}' dest='{ctdkey}' item='{ctitem}' amount={ctamt}"
                         + $" dests={ctp.destinations.Count}";
                }

                case "importtest":
                {
                    // H-MERGERIMPORT-1 L1: DRIVER + FIXTURE DISCOVERY.  No argument = every import partnership
                    // on THIS machine, because the whole feature needs one that the fixture must already
                    // provide: a plan is minted natively out of an HQ purchasing agent plus an import/export
                    // contact, and fabricating one here would be inventing the very state under test.
                    // With arguments it APPENDS a real ImportProduct aimed at <address> and arms the plan for
                    // today, which is exactly what the game's own Business Purchasing page writes.  In memory
                    // only - the scenario that uses this lever never saves.
                    if (arg.Length == 0)
                    {
                        var ipgi = SaveGameManager.Current;
                        if (ipgi?.importPartnerships == null) return "OK importtest plans=[] (this machine holds no import partnership list)";
                        var ipsb = new StringBuilder("OK importtest plans=[");
                        int ipn = 0;
                        foreach (var ipp in ipgi.importPartnerships)
                        {
                            if (ipp == null) continue;
                            string ipimp = ""; try { ipimp = GameStateReader.AddressKey(ipp.importAddress); } catch { }
                            bool ipagent = false; try { ipagent = ipp.PurchasingAgentInstance != null && !ipp.PurchasingAgentInstance.isBeingReplaced; } catch { }
                            if (ipn++ > 0) ipsb.Append(" | ");
                            ipsb.Append($"id={ipp.id} importer='{ipimp}' agent='{ipp.employeeInstanceId}' agentOk={ipagent}")
                                .Append($" active={ipp.isActive} repeating={ipp.isRepeatingOrder} urgent={ipp.isUrgentOrder}")
                                .Append($" nextDay={ipp.nextDeliveryDay} today={TimeHelper.CurrentDay} isTarget={ipp.isTarget}")
                                .Append($" products={(ipp.products != null ? ipp.products.Count : 0)}");
                            if (ipn >= 8) break;
                        }
                        return ipsb.Append(']').ToString();
                    }
                    var iptk = arg.Split(' ');
                    if (iptk.Length < 4)
                        return "ERR usage: importtest <num> <ba:street_x> <itemName> <amount> [target]  (no argument lists this machine's import partnerships)";
                    string ipaddr = iptk[0] + " " + iptk[1];
                    string ipitem = iptk[2];
                    int ipamt;
                    if (!int.TryParse(iptk[3], out ipamt) || ipamt < 1 || ipamt > 99999)
                        return $"ERR amount '{iptk[3]}' is not 1..99999";
                    bool iptarget = iptk.Length > 4 && iptk[4] == "target";
                    bool ipknown = false;
                    try { ipknown = BigAmbitions.Items.ItemsGetter.GetByName(ipitem) != null; } catch { }
                    if (!ipknown) return $"ERR no item '{ipitem}'";
                    var ipgi2 = SaveGameManager.Current;
                    if (ipgi2?.importPartnerships == null || ipgi2.importPartnerships.Count == 0)
                        return "ERR this machine holds NO import partnership - the fixture must provide one (an HQ purchasing agent plus an import/export contract); nothing is faked here";
                    Entities.ImportPartnership? ipplan = null;
                    foreach (var ipp2 in ipgi2.importPartnerships) { if (ipp2 != null) { ipplan = ipp2; break; } }
                    if (ipplan == null) return "ERR this machine holds no usable import partnership";
                    var ipreg = GameStatePatcher.FindRegistration(ipaddr);
                    if (ipreg == null) return $"ERR no registration at '{ipaddr}'";
                    if (!(ipreg is Entities.Warehouse)) return $"ERR '{ipaddr}' is not a warehouse - an import product can only be assigned to one";
                    string ipkey = ipaddr; try { ipkey = GameStateReader.AddressKey(ipreg); } catch { }
                    if (ipplan.products == null) ipplan.products = new System.Collections.Generic.List<Entities.ImportProduct>();
                    Entities.ImportProduct? iprod = null;
                    foreach (var ipx in ipplan.products)
                    {
                        if (ipx == null || ipx.itemName != ipitem || ipx.assignedWarehouse == null) continue;
                        string ipk2 = ""; try { ipk2 = GameStateReader.AddressKey(ipx.assignedWarehouse); } catch { }
                        if (ipk2 == ipkey) { iprod = ipx; break; }
                    }
                    bool ipnew = iprod == null;
                    if (iprod == null) { iprod = new Entities.ImportProduct { itemName = ipitem }; ipplan.products.Add(iprod); }
                    iprod.assignedWarehouse = ipreg.Address;
                    iprod.amount = ipamt;
                    ipplan.isTarget = iptarget;
                    ipplan.isActive = true;
                    ipplan.nextDeliveryDay = TimeHelper.CurrentDay;
                    bool ipagent2 = false; try { ipagent2 = ipplan.PurchasingAgentInstance != null && !ipplan.PurchasingAgentInstance.isBeingReplaced; } catch { }
                    string ipimp2 = ""; try { ipimp2 = GameStateReader.AddressKey(ipplan.importAddress); } catch { }
                    bool ipflip = false; try { ipflip = MergerFlip.IsFlipped(ipkey); } catch { }
                    bool ipbooks = false; try { ipbooks = MergerFlip.BooksHere(ipreg); } catch { }
                    float ipprice = 0f; try { ipprice = iprod.Price; } catch { }
                    float ipdisc = 1f;  try { ipdisc = ipplan.GetDiscount; } catch { }
                    return $"OK importtest plan={ipplan.id} importer='{ipimp2}' agentOk={ipagent2} dest='{ipkey}'"
                         + $" item='{ipitem}' amount={ipamt} isTarget={iptarget} added={ipnew}"
                         + $" flipped={ipflip} booksHere={ipbooks} price={ipprice:0.##} discount={ipdisc:0.###}"
                         + $" expected={(ipprice * ipamt * ipdisc):0.##} products={ipplan.products.Count}";
                }

                case "importrun":
                {
                    // H-MERGERIMPORT-1 L2: the NATIVE entry point, so the authority veil and the detach are
                    // exercised for real - not a mod-side shortcut into the routed path.
                    try { Entities.ImportPartnership.DoAllDeliveries(); }
                    catch (Exception ipx2) { return $"ERR importrun: {ipx2.GetType().Name}: {ipx2.Message}"; }
                    return $"OK importrun pending={ImportTransfer.PendingCount} applied={ImportTransfer.AppliedCount} closed={ImportTransfer.ClosedCount}";
                }

                case "importstate":
                {
                    // H-MERGERIMPORT-1 L3: READ-ONLY.
                    return $"OK importstate pending={ImportTransfer.PendingCount} applied={ImportTransfer.AppliedCount}"
                         + $" closed={ImportTransfer.ClosedCount} last='{ImportTransfer.LastLine}'";
                }

                case "importloss":
                {
                    // H-MERGERIMPORT-1 F3 L4: HOST ONLY, DEV ONLY. Silently drop the next n inbound deliver
                    // legs AND forget their bindings - the shape a host restart (or a lost need) between the
                    // charge and the ack leaves behind, which nothing else can produce on demand.
                    if (!MPServer.IsRunning) return "ERR importloss is a host verb - the host holds the import bindings";
                    int iln;
                    if (!int.TryParse(arg.Trim(), out iln) || iln < 0 || iln > 99) return "ERR usage: importloss <n>  (0..99)";
                    MPServer.ImportLossCountdown = iln;
                    return $"OK importloss next={iln}";
                }

                case "importresend":
                {
                    // H-MERGERIMPORT-1 F3 L5: the plan owner's re-offer on demand - the very same call the
                    // session-settled edge and the hourly sweep make, so the lever proves the real path.
                    int ilp = ImportTransfer.PendingCount;
                    try { ImportTransfer.ResendUnacked("lever"); }
                    catch (Exception ipx3) { return $"ERR importresend: {ipx3.GetType().Name}: {ipx3.Message}"; }
                    return $"OK importresend pending={ilp}";
                }

                case "transfers":
                {
                    // T6: the HOST's in-transit table - the records no save holds right now. A member has
                    // nothing to show here by design (the host is the only holder).
                    if (!MPServer.IsRunning) return "ERR transfers is a host verb - the host holds the in-transit table";
                    var trows = MPServer.TransfersReadout();
                    var tfsb = new StringBuilder();
                    foreach (var trow in trows)
                    {
                        Plugin.Logger.LogWarning($"[TestDrive] transfer: {trow}");
                        if (tfsb.Length > 0) tfsb.Append(" ; ");
                        tfsb.Append(trow);
                    }
                    return $"OK transfers pending={trows.Count}{(trows.Count > 0 ? " [" + tfsb + "]" : "")}";
                }

                case "transfer":
                {
                    // Phase 4b (people) P2: the dropdown's whole game-state write is assignedAddress
                    // (MyEmployees.cs:206 / CandidateCellView.cs:81), so setting it here is exactly what a
                    // player does - the 2 s merger scan then decides between a routed assign and the
                    // two-phase release/adopt, and logs which under [Transfer].
                    var ttk = arg.Split(' ');
                    if (ttk.Length < 3) return "ERR usage: transfer <employeeId> <num> <ba:street_x>";
                    string tempId = ttk[0];
                    string taddr  = ttk[1] + " " + ttk[2];
                    var temp = FindEmployee(tempId);
                    if (temp == null) return $"ERR no employee '{tempId}' on this machine";
                    var treg = GameStatePatcher.FindRegistration(taddr);
                    if (treg == null) return $"ERR no registration for '{taddr}'";
                    bool tinj = false;  try { tinj = MPRegisterSync.IsInjectedStaff(tempId); } catch { }
                    string thome = "";  try { thome = MPRegisterSync.InjectedAddrOf(tempId); } catch { }
                    string tkey = taddr; try { tkey = GameStateReader.AddressKey(treg); } catch { }
                    temp.assignedAddress = treg.Address;
                    return $"OK transfer armed employee='{tempId}' injected={tinj} home='{thome}' -> '{tkey}'";
                }

                case "train":
                {
                    // Phase 4b (people) P3: the routed TRAIN leg on its own, without the bulk dialog. The
                    // cost is read here exactly as the mass action reads it (skills[0], +10 capped at 100)
                    // and travels as a BOUND - the machine that runs the address recomputes and pays.
                    // H-MERGERTRAIN-1: `train <employeeId> [skillName]`. A merged partner's UNASSIGNED copy takes
                    // the BENCH route (owner named, skill named, no address); everything else keeps the
                    // address-keyed leg, where an empty skill still means 'the primary one'.
                    if (arg.Length == 0) return "ERR usage: train <employeeId> [skillName]";
                    var rntk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string rnid = rntk[0];
                    string rnwant = rntk.Length > 1 ? rntk[1] : "";
                    var rnemp = FindEmployee(rnid);
                    if (rnemp == null) return $"ERR no employee '{rnid}' on this machine";
                    string rnaddr = ""; try { if (rnemp.assignedAddress != null) rnaddr = GameStateReader.AddressKey(rnemp.assignedAddress); } catch { }
                    var rnskills = rnemp.characterData.skills;
                    int rnidx = -1;
                    try
                    {
                        if (rnskills != null)
                        {
                            if (rnwant.Length == 0) rnidx = rnskills.Count > 0 ? 0 : -1;
                            else for (int i = 0; i < rnskills.Count; i++)
                                if (rnskills[i] != null && string.Equals(rnskills[i].name, rnwant, StringComparison.Ordinal)) { rnidx = i; break; }
                        }
                    }
                    catch { }
                    if (rnskills == null || rnidx < 0) return $"ERR no skill '{(rnwant.Length == 0 ? "(primary)" : rnwant)}' on '{rnid}'";
                    var rnsk = rnskills[rnidx];
                    float rncost = 0f;
                    try { rncost = Helpers.EmployeeHelper.GetTrainingCost(rnemp, rnsk.name, UnityEngine.Mathf.Min(UnityEngine.Mathf.CeilToInt(100f - rnsk.value), 10)); }
                    catch { }
                    // That price call ARMS the per-click record if this is a bench copy. No dialog follows here,
                    // so it is dropped at once rather than left for the next HudConfirm in this frame to inherit.
                    bool rnbench = SharedShopStaff.IsBenchTrainTarget(rnemp, out string rnowner);
                    SharedShopStaff.ClearBenchTrainArm();
                    bool rnrouted = rnbench
                        ? SharedShopStaff.CommitBenchTrain(rnid, rnowner, rnsk.name, rncost)
                        : SharedShopStaff.CommitStaffOp(rnid, rnaddr, "train", rncost);
                    return $"OK train addr='{rnaddr}' employee='{rnid}' skill='{rnsk.name}' cost={rncost.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} bench={rnbench} owner='{rnowner}' routed={rnrouted}";
                }

                case "hrtrain":
                {
                    // CROSS-HR-2 T4 rig lever. `hrtrain <planId>` runs THIS machine's own HR plan through the
                    // game's own TrainEmployees - the daily pass exactly as HRManager.WorkDaily runs it - so a
                    // run can assert that a leg leaves for every injected assignee and the owner applies it.
                    // Native's money is native's: the plan's single ChangeMoneySafe runs inside that method.
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (arg.Length == 0) return "ERR usage: hrtrain <planId>";
                    var htplan = Buildings.Office.Headquarters.HrManagerHelper.GetPlanFromId(arg);
                    if (htplan == null) return $"ERR no HR plan '{arg}' on this machine";
                    // r1 MAJOR-2: a partner's SHADOW sits in the same list. The game's own daily pass skips it (the WorkDaily
                    // guard); this lever must too - it would write the shadow's list, train copies and charge THIS wallet.
                    if (MergerAbsence.IsDisplayInstall(htplan)) return $"ERR HR plan '{arg}' is a partner's shadow here - it trains on the machine that owns it";
                    int htn = 0; try { htn = htplan.assignedEmployees?.Count ?? 0; } catch { }
                    bool htmgr = false; try { htmgr = !string.IsNullOrEmpty(htplan.assignedEmployeeId); } catch { }
                    MergerEmployeeSync.NoteTrainPass(arg, -1, -1, -1);   // -1 = the postfix did not run
                    htplan.TrainEmployees();
                    return $"OK hrtrain plan='{arg}' assigned={htn} manager={htmgr} injected={MergerEmployeeSync.LastTrainInjected} legs={MergerEmployeeSync.LastTrainLegs} unchanged={MergerEmployeeSync.LastTrainUnchanged} (the game's own daily training pass ran once)";
                }

                case "hrtag":
                {
                    // CROSS-HR-3 A5 rig lever. `hrtag <employeeId> <planId|->` sends exactly the tag leg A2 sends
                    // when a CO-MEMBER's person joins this machine's HR plan ("-" = the clear). It writes nothing
                    // here; the assertion belongs on the OWNER's machine, where the real record is.
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (arg.Length == 0) return "ERR usage: hrtag <employeeId> [planId|-]";
                    return CompanyPlans.TestDriveHrTag(arg);
                }

                case "hrplanof":
                {
                    // CROSS-HR-3 A5 rig lever. `hrplanof <employeeId>` reads assignedHrManagerPlanId off the
                    // record here and says whether that plan id resolves on THIS machine - real, a partner's
                    // shadow, or nothing at all. It is the read half of `hrtag`, and the `employees` output
                    // keeps its format.
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (arg.Length == 0) return "ERR usage: hrplanof <employeeId>";
                    return CompanyPlans.TestDriveHrPlanOf(arg);
                }

                case "planown":
                {
                    // CROSS-HR-3b B6 rig lever. `planown hr <planId> assign <employeeId> true|false` does, on the
                    // plan's OWN machine, exactly what the pane's native SetEmployeeAssigned does (decompile
                    // HrManagerPlanUI.cs:256-268) and then calls the same B1 helper the pane's postfix calls - so
                    // the own-plan tag leg can be exercised with no pane open. A shadow or a display install is
                    // refused there: a partner's row is not this machine's list to write.
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (arg.Length == 0) return "ERR usage: planown hr <planId> assign <employeeId> true|false";
                    return CompanyPlans.TestDrivePlanOwn(arg);
                }

                case "planbulk":
                {
                    // HO-1c L3 rig lever. `planbulk <family> <planId> clear|fill|suggestall [n]` sends exactly the
                    // ONE leg the bulk prefix sends off the temp row of a PARTNER's plan (IntValue = n for fill).
                    // It writes nothing on this machine: the runner applies the op and the feed redraws the rows.
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    return CompanyPlans.TestDriveBulk(arg);
                }

                case "planlist":
                {
                    // HO-1c L3 rig lever. `planlist <family> <planId>` prints the DISPLAY copy's assignedEmployees
                    // count and ids (hr) or its row count (the other families), so a run can assert the optimistic
                    // mutate and the urgent feed that follows it. Read-only.
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    return CompanyPlans.TestDriveList(arg);
                }

                case "helperstate":
                {
                    // H-MERGERSTOCK-2 rig lever (DEV). What THIS machine believes about business-HELPER access.
                    //   helperstate                      census over every merger-FLIPPED registration here: how many are
                    //                                    businesses / homes and how many of each read helper=True (a home
                    //                                    must never read True - HelperAt's live-type gate)
                    //   helperstate <num> <street>       one address: the host-pushed set, the full HelperAt answer, the
                    //                                    flip, BooksHere, the live type, the set's size, what is in the hands
                    //   helperstate <num> <street> drop  ALSO removes that address from the host-pushed helper set on THIS
                    //                                    machine only, in memory - a missed/late push, synthesized, so a run
                    //                                    can show the flip fallback carrying a deposit on its own. The next
                    //                                    host push restores it.
                    var hk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var hregs = SaveGameManager.Current?.BuildingRegistrations;
                    if (hregs == null) return "ERR no save loaded";
                    if (hk.Length == 0)
                    {
                        int hf = 0, hb = 0, hbH = 0, hh = 0, hhH = 0;
                        var hkeys = new StringBuilder();
                        foreach (var hr in hregs)
                        {
                            if (hr == null) continue;
                            string hkey = ""; try { hkey = GameStateReader.AddressKey(hr); } catch { }
                            if (!MergerFlip.IsFlipped(hkey)) continue;
                            hf++;
                            string ht = ""; try { ht = hr.businessTypeName ?? ""; } catch { }
                            bool hhelp = BusinessHelperRoute.HelperAt(hr, out _);
                            if (AccessSets.CountsAsBusiness(ht)) { hb++; if (hhelp) hbH++; }
                            else
                            {
                                hh++; if (hhelp) hhH++;
                                if (hh <= 3) { if (hkeys.Length > 0) hkeys.Append('|'); hkeys.Append(hkey); }
                            }
                        }
                        return $"OK helperstate flipped={hf} biz={hb} bizHelper={hbH} homes={hh} homesHelper={hhH} pushed={GrantSync.HelperBusinessCount} homeKeys=[{hkeys}]";
                    }
                    if (hk.Length < 2 || hk.Length > 3) return "ERR usage: helperstate [<num> <ba:street_x> [drop]]";
                    var hreg = GameStatePatcher.FindRegistration(hk[0] + " " + hk[1]);
                    if (hreg == null) return $"ERR no registration for '{hk[0]} {hk[1]}'";
                    string hkey2 = hk[0] + " " + hk[1]; try { hkey2 = GameStateReader.AddressKey(hreg); } catch { }
                    string hdrop = "";
                    if (hk.Length == 3)
                    {
                        if (!string.Equals(hk[2], "drop", StringComparison.OrdinalIgnoreCase)) return "ERR usage: helperstate [<num> <ba:street_x> [drop]]";
                        var hkeep = new System.Collections.Generic.List<string>();
                        foreach (var hr2 in hregs)
                        {
                            if (hr2 == null) continue;
                            string k2 = ""; try { k2 = GameStateReader.AddressKey(hr2); } catch { }
                            if (k2.Length > 0 && k2 != hkey2 && GrantSync.IsHelperBusiness(k2)) hkeep.Add(k2);
                        }
                        GrantSync.SetHelperBusinesses(hkeep);
                        hdrop = " dropped=True";
                    }
                    string htype = ""; try { htype = hreg.businessTypeName ?? ""; } catch { }
                    string hands = "empty";
                    try
                    {
                        var hi = Helpers.PlayerHelper.ItemInstanceInHands;
                        if (hi != null)
                        {
                            int hn = 0; string hname = hi.itemName ?? "";
                            if (hi.cargoInstances != null)
                                foreach (var hc in hi.cargoInstances) if (hc != null) { hn += hc.amount; hname = hc.itemName ?? hname; }
                            hands = $"{hname}x{hn}";
                        }
                    }
                    catch { }
                    return $"OK helperstate addr='{hkey2}' pushed={GrantSync.IsHelperBusiness(hkey2)} helper={BusinessHelperRoute.HelperAt(hreg, out _)} flipped={MergerFlip.IsFlipped(hkey2)} books={MergerFlip.BooksHere(hreg)} type='{htype}' count={GrantSync.HelperBusinessCount} hands={hands}{hdrop}";
                }

                case "stationroom":
                {
                    // H-MERGERSTOCK-2 rig lever (DEV), OWNER side. `stationroom <num> <ba:street_x> <n>` - takes <n> units
                    // off the first TILL (single-slot PointOfSale with a named stock holding at least <n>) at an address
                    // THIS machine books, in memory, then pushes the interior to everyone inside - so a partner's
                    // `deposit` has room (run 2 found every till and rack in the fx-hq1 store full: 1000/1000, 50/50).
                    // The units simply vanish; the scenario that uses this never saves.
                    var rk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (rk.Length != 3 || !int.TryParse(rk[2], out var rn) || rn <= 0) return "ERR usage: stationroom <num> <ba:street_x> <n>";
                    var rreg2 = GameStatePatcher.FindRegistration(rk[0] + " " + rk[1]);
                    if (rreg2 == null) return $"ERR no registration for '{rk[0]} {rk[1]}'";
                    string rkey = GameStateReader.AddressKey(rreg2);
                    if (!MergerFlip.BooksHere(rreg2)) return $"ERR '{rkey}' is not booked on this machine (owner side only)";
                    if (rreg2.itemInstances != null)
                        foreach (var rkv in rreg2.itemInstances)
                        {
                            var rii = rkv.Value;
                            if (rii?.cargoInstances == null || rii.cargoInstances.Count != 1 || rii.ItemCached == null) continue;
                            if ((rii.ItemCached.type & BigAmbitions.Items.ItemType.PointOfSale) == 0) continue;
                            var rs0 = rii.cargoInstances[0];
                            if (rs0 == null || string.IsNullOrEmpty(rs0.itemName) || rs0.amount < rn) continue;
                            int rbefore = rs0.amount;
                            rs0.amount = rbefore - rn;
                            try { InteriorSync.PushOwnedBuildingNow(rkey); } catch { }
                            return $"OK stationroom addr='{rkey}' station='{rii.itemName}' id={rii.id} item={rs0.itemName} {rbefore}->{rs0.amount}";
                        }
                    return $"ERR no till holding at least {rn} at '{rkey}'";
                }

                case "deposit":
                {
                    // H-MERGERSTOCK-2 rig lever (DEV). `deposit <num> <ba:street_x> [amount|probe]` - a hand deposit into
                    // a single-slot stock STATION (a till, or a one-slot display) driven through the game's OWN primitives:
                    // the native `getitem` console command (ItemHelper.Command_GetItem) puts <amount> (default 20, clamped
                    // to the station's room) of the station's stock item into EMPTY hands, then the native 3-argument
                    // ItemInstance.MergeCargo moves it onto the station's one stock slot - the exact primitive DepositGuard
                    // guards (bundle 145749's 'UNROUTED native MergeCargo'). The local player must be STANDING in <addr>.
                    // The station is the first single-slot item with a named stock, room and a live controller, a
                    // PointOfSale (till) preferred - run 1 found no till with room in the fixture store. `probe` only
                    // names the station and its item (so a run can read the owner's stock of that item first).
                    // localAfter == localBefore means the guard ROUTED the put to the owner and skipped the native
                    // merge; the owner's OK later empties the hands (`helperstate <addr>` hands=). The goods are
                    // conjured, so this lever is Dev-only like the rest of this file.
                    var dk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int damt = 20;
                    bool dprobe = dk.Length == 3 && string.Equals(dk[2], "probe", StringComparison.OrdinalIgnoreCase);
                    if (dk.Length < 2 || dk.Length > 3 || (dk.Length == 3 && !dprobe && (!int.TryParse(dk[2], out damt) || damt <= 0)))
                        return "ERR usage: deposit <num> <ba:street_x> [amount|probe]";
                    var dreg = GameStatePatcher.FindRegistration(dk[0] + " " + dk[1]);
                    if (dreg == null) return $"ERR no registration for '{dk[0]} {dk[1]}'";
                    string dkey = GameStateReader.AddressKey(dreg);
                    var dcur = InstanceBehavior<BuildingManager>.Instance?.buildingRegistration;
                    string dcurKey = ""; try { if (dcur != null) dcurKey = GameStateReader.AddressKey(dcur); } catch { }
                    if (dcurKey != dkey) return $"ERR standing in '{dcurKey}', not '{dkey}' (enterbuilding first)";
                    if (Helpers.PlayerHelper.IsHoldingItem) return "ERR hands are not empty";
                    if (Helpers.PlayerHelper.IsUsingVehicle) return "ERR in a vehicle";
                    BigAmbitions.Items.ItemInstance? dst = null;
                    BigAmbitions.Items.CargoInstance? dslot = null;
                    int dspace = 0;
                    bool dtill = false;
                    var dcensus = new StringBuilder();
                    int dseen = 0;
                    if (dreg.itemInstances != null)
                        foreach (var dkv in dreg.itemInstances)
                        {
                            try
                            {
                                var dii = dkv.Value;
                                if (dii?.cargoInstances == null || dii.cargoInstances.Count != 1 || dii.ItemCached == null) continue;
                                var ds0 = dii.cargoInstances[0];
                                if (ds0 == null || string.IsNullOrEmpty(ds0.itemName)) continue;
                                bool isTill = (dii.ItemCached.type & BigAmbitions.Items.ItemType.PointOfSale) != 0;
                                int dcap = 0; try { dcap = ds0.GetMaxStockCapacity(dii); } catch { }
                                int droom = dcap > 0 ? dcap - ds0.amount : 0;
                                bool dctrl = ItemHelper.GetItemControllerByID(dii.id) != null;   // the guard's route needs the live controller
                                if (dseen++ < 6) dcensus.Append($"{dii.itemName}:{(isTill ? "till" : "slot")}:{ds0.itemName}:{ds0.amount}/{dcap}:ctrl={dctrl};");
                                if (droom <= 0 || !dctrl) continue;
                                if (dst != null && (dtill || !isTill)) continue;   // keep the first; a till beats a display
                                dst = dii; dslot = ds0; dspace = droom; dtill = isTill;
                            }
                            catch { }
                        }
                    if (dst == null || dslot == null) return $"ERR no single-slot station with a named stock, room and a controller at '{dkey}' (seen {dseen}: {dcensus})";
                    int dn = Math.Min(damt, dspace);
                    string ditem = dslot.itemName;
                    if (dprobe) return $"OK deposit probe addr='{dkey}' station='{dst.itemName}' till={dtill} item={ditem} room={dspace}";
                    ItemHelper.Command_GetItem(ditem, dn);
                    BigAmbitions.Items.CargoInstance? dsrc = null;
                    var dheld = Helpers.PlayerHelper.ItemInstanceInHands;
                    if (dheld?.cargoInstances != null)
                        foreach (var dc in dheld.cargoInstances) if (dc != null && dc.itemName == ditem) { dsrc = dc; break; }
                    if (dsrc == null) return $"ERR getitem put nothing matching '{ditem}' in the hands";
                    bool dpushed = GrantSync.IsHelperBusiness(dkey);
                    bool dhelper = BusinessHelperRoute.HelperHere(out _);
                    int dbefore = dslot.amount;
                    dst.MergeCargo(fromCargoInstance: dsrc, toCargoInstance: dslot, amount: dsrc.amount);
                    int dafter = dslot.amount;
                    return $"OK deposit addr='{dkey}' station='{dst.itemName}' id={dst.id} item={ditem} amount={dn} pushed={dpushed} helper={dhelper} localBefore={dbefore} localAfter={dafter} heldAfter={dsrc.amount} routed={dafter == dbefore}";
                }

                case "drive":
                {
                    // H-OWNERRIDE-HOSTDRIVER-1 rig lever (DEV, WRITES the local player's position). `drive <ownerPid> [vehicleId]`:
                    // the LOCAL player takes the wheel of a DRIVABLE proxy of that player's car (a granted key or a merger) -
                    // teleported beside it with the game's own Teleport (as 'tp'), then the car's own DriveVehicle(), the
                    // native enter a click runs (walk to the driver door, EnterVehicle). Without a vehicle id it picks an
                    // ACTIVE car with at least one passenger seat. The drive stream then starts from VehicleManager.TickDriveSync;
                    // 'ride' reads it back (driving='<vid>').
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    var da = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (da.Length < 1 || da.Length > 2) return "ERR usage: drive <ownerPid> [vehicleId]";
                    try { if (BuildingManager.IsInsideBuilding) return "ERR inside a building"; } catch { }
                    try { if (Helpers.VehicleHelper.IsInsideVehicle()) return "ERR in a vehicle"; } catch { }
                    if (PassengerSync.IsRiding(MPConfig.PlayerId)) return "ERR riding";
                    VehicleController? dvc = null; string dvid = "", dtype = "";
                    try
                    {
                        var dlist = Helpers.VehicleHelper.AllPlayerVehicles;
                        if (dlist != null)
                            for (int i = 0; i < dlist.Count; i++)
                            {
                                var vc = dlist[i]; var inst = vc != null ? vc.vehicleInstance : null;
                                if (inst == null || string.IsNullOrEmpty(inst.id)) continue;
                                if (!inst.id.StartsWith("BAMP_", StringComparison.Ordinal) || inst.id.StartsWith("BAMP_TESTRIG", StringComparison.Ordinal)) continue;
                                if (vc == null || !vc.gameObject.activeInHierarchy) continue;
                                string real = inst.id.Substring(5);
                                if (VehicleManager.OwnerIdFor(real) != da[0]) continue;
                                if (da.Length == 2 && real != da[1]) continue;
                                string tn = VehicleManager.TypeNameFor(real);
                                if (da.Length == 1 && (VehicleManager.IsHandCartType(tn) || PassengerSync.PassengerSeatsForType(tn) < 1)) continue;
                                dvc = vc; dvid = real; dtype = tn; break;
                            }
                    }
                    catch (Exception ex) { return $"ERR drive: {ex.Message}"; }
                    if (dvc == null) return $"ERR no drivable car of '{da[0]}' here";
                    string dterr = TeleportBeside(dvc.transform);
                    if (dterr.Length > 0) return "ERR drive: " + dterr;
                    try { dvc.DriveVehicle(); }
                    catch (Exception ex) { return $"ERR drive: DriveVehicle threw {ex.Message}"; }
                    Plugin.Logger.LogInfo($"[TestDrive] drive: DriveVehicle on '{dvid}' ({dtype}, owner '{da[0]}').");
                    return $"OK drive vid={dvid} owner={da[0]} type={dtype} queued";
                }

                case "board":
                {
                    // H-OWNERRIDE-HOSTDRIVER-1 rig lever (DEV, WRITES the local player's position). `board <vehicleId>`: ask
                    // for a seat the way a click does, after a teleport beside the car. On the OWNER's own car this runs the
                    // car's own DriveVehicle(), which Patch_VehicleController_DriveVehicle_OwnerRide (PassengerLockButton.cs)
                    // turns into a passenger board while another player drives it - the field path of the bug. On anyone
                    // else's car it is PassengerRide.RequestBoard (what the ghost click ends in).
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    string bvid = arg.Trim();
                    if (bvid.Length == 0 || bvid.Contains(" ")) return "ERR usage: board <vehicleId>";
                    try { if (BuildingManager.IsInsideBuilding) return "ERR inside a building"; } catch { }
                    try { if (Helpers.VehicleHelper.IsInsideVehicle()) return "ERR in a vehicle"; } catch { }
                    VehicleController? own = null;
                    try
                    {
                        var blist = Helpers.VehicleHelper.AllPlayerVehicles;
                        if (blist != null)
                            for (int i = 0; i < blist.Count; i++)
                            {
                                var vc = blist[i];
                                if (vc != null && vc.vehicleInstance != null && vc.vehicleInstance.id == bvid) { own = vc; break; }
                            }
                    }
                    catch { }
                    UnityEngine.Transform? bt = own != null ? own.transform : VehicleManager.GhostTransform(bvid);
                    if (bt == null) return $"ERR no car '{bvid}' here";
                    string bterr = TeleportBeside(bt);
                    if (bterr.Length > 0) return "ERR board: " + bterr;
                    bool bdriven = VehicleManager.IsDrivenRemotely(bvid);
                    try
                    {
                        if (own != null) own.DriveVehicle();
                        else PassengerRide.RequestBoard(bvid);
                    }
                    catch (Exception ex) { return $"ERR board: {ex.Message}"; }
                    return $"OK board vid={bvid} own={own != null} drivenRemotely={bdriven} requested";
                }

                case "ride":
                {
                    // H-OWNERRIDE-HOSTDRIVER-1 rig readout (read-only): this machine's ride and drive state.
                    // local/seat = PassengerSync's local ride mirror; driving = the borrowed car this machine streams;
                    // riders = every seat as vid:seat:pid; driven (HOST only) = MPServer's live driven-car records vid:driver.
                    if (arg.Length > 0) return "ERR usage: ride";
                    var rl = new System.Collections.Generic.List<string>();
                    try
                    {
                        foreach (var rvid in PassengerSync.OccupiedVehicleIds())
                        {
                            var rs = PassengerSync.RidersOf(rvid);
                            if (rs != null) foreach (var kv in rs) rl.Add($"{rvid}:{kv.Key}:{kv.Value}");
                        }
                        rl.Sort(StringComparer.Ordinal);
                    }
                    catch { }
                    string rsb = $"OK ride local='{PassengerSync.LocalRidingVehicleId}' seat={PassengerSync.LocalSeat} driving='{VehicleManager.DrivingRealVid}' riders=[{string.Join(",", rl)}]";
                    if (MPServer.IsRunning) rsb += $" driven=[{MPServer.DrivenCarsDigest()}]";
                    return rsb;
                }

                case "exitride":
                {
                    // H-OWNERRIDE-HOSTDRIVER-1 rig lever (DEV). A passenger leaves through PassengerRide.RequestExit (the
                    // Exit button's call); a driver leaves through the car's own ExitVehicle() (the Park button's call,
                    // ItemPanelUI.ClickPark), which ends the drive stream with a Released.
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (PassengerSync.IsRiding(MPConfig.PlayerId))
                    {
                        string was = PassengerSync.LocalRidingVehicleId;
                        try { PassengerRide.RequestExit(); } catch (Exception ex) { return $"ERR exitride: {ex.Message}"; }
                        return $"OK exitride rider vid={was}";
                    }
                    try
                    {
                        var elist = Helpers.VehicleHelper.AllPlayerVehicles;
                        if (elist != null)
                            for (int i = 0; i < elist.Count; i++)
                            {
                                var vc = elist[i];
                                if (vc == null || !vc.controlledByPlayer) continue;
                                string evid = vc.vehicleInstance != null ? vc.vehicleInstance.id : "";
                                vc.ExitVehicle();
                                return $"OK exitride driver vid={evid}";
                            }
                    }
                    catch (Exception ex) { return $"ERR exitride: {ex.Message}"; }
                    return "ERR not in a car";
                }

                // PROBE-START: P-CARHIT
                case "carhit":
                {
                    // P-CARHIT readout (DEV, read-only): car-to-car contacts per class pair (total/detailed) and the probe's
                    // budgets. `carhit on|off` arms/disarms the PROBE ITSELF (the [Perf] cost check) - no game state.
                    if (arg == "on") CarHitProbe.Armed = true;
                    else if (arg == "off") CarHitProbe.Armed = false;
                    else if (arg.Length > 0) return "ERR usage: carhit [on|off]";
                    return "OK carhit " + CarHitProbe.Readout();
                }
                // PROBE-END: P-CARHIT

                case "cartstate":
                {
                    // H-CARTICON-1 rig readout (read-only): VehicleManager.CartStateReadout for one vehicle id.
                    if (arg.Length == 0 || arg.IndexOf(' ') >= 0) return "ERR usage: cartstate <vid>";
                    return "OK cartstate " + VehicleManager.CartStateReadout(arg);
                }

                case "cartstrand":
                {
                    // H-CARTICON-1 rig lever (DEV). Stands in for 'push my hand vehicle into this shop and let go': moves
                    // one of MY live, parked hand vehicles next to me inside the building I am in, saves its position the
                    // way native does (VehicleController.SavePosition) and stamps the building's street data exactly as
                    // native release-indoors does (VehicleController :346-350). The clearing itself is left to the game's
                    // own OnExitBuilding when the player walks out ('exitbuilding').
                    try
                    {
                        var ca = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (ca.Length < 1 || ca.Length > 2) return "ERR usage: cartstrand <vid> [sideMeters]";
                        float side = 0f;
                        if (ca.Length == 2 && !float.TryParse(ca[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out side))
                            return "ERR sideMeters not a number";
                        if (!BuildingManager.IsInsideBuilding) return "ERR not inside a building";
                        var bld = InstanceBehavior<BuildingManager>.Instance?.cityBuildingController?.building;
                        if (bld == null) return "ERR no current building";
                        VehicleController? cvc = null;
                        var clist = Helpers.VehicleHelper.AllPlayerVehicles;
                        if (clist != null)
                            for (int i = 0; i < clist.Count; i++)
                            {
                                var v = clist[i];
                                if (v != null && v.vehicleInstance != null && v.vehicleInstance.id == ca[0]
                                    && v.GetComponentInParent<ModGhostMarker>() == null) { cvc = v; break; }
                            }
                        if (cvc == null) return $"ERR '{ca[0]}' is not a live vehicle of mine";
                        if (cvc.controlledByPlayer) return "ERR vehicle in use";
                        if (!cvc.vehicleType.HasTag(BigAmbitions.Tags.TagRef.Vehicletag.ishandvehicle)) return "ERR not a hand vehicle";
                        var pt = InstanceBehavior<GameManager>.Instance.playerController.transform;
                        UnityEngine.Vector3 cpos = pt.position + pt.forward * 2.5f + pt.right * side;
                        cvc.transform.position = cpos;
                        cvc.transform.rotation = pt.rotation;
                        var rb = cvc.GetComponent<UnityEngine.Rigidbody>();
                        if (rb != null) { rb.position = cpos; rb.rotation = pt.rotation; if (!rb.isKinematic) rb.velocity = UnityEngine.Vector3.zero; }
                        UnityEngine.Physics.SyncTransforms();
                        cvc.SavePosition();
                        cvc.vehicleInstance.SetStreetData(bld.StreetName ?? string.Empty, bld.StreetNumber);
                        string ctag = $"{bld.StreetNumber} {bld.StreetName}";
                        Plugin.Logger.LogInfo($"[TestDrive] cartstrand '{ca[0]}' left inside '{ctag}' at {cpos} cargo={cvc.vehicleInstance.cargoInstances?.Count ?? 0}.");
                        return $"OK cartstrand vid={ca[0]} tag='{ctag}' cargo={cvc.vehicleInstance.cargoInstances?.Count ?? 0}";
                    }
                    catch (Exception ex) { return $"ERR cartstrand: {ex.Message}"; }
                }

                case "cartgrab":
                {
                    // H-CARTICON-1 fold rig lever (DEV, WRITES the local player's position). `cartgrab <vid>`: take a PARTNER's
                    // hand vehicle the way a click does - its drivable proxy (BAMP_<vid>) here, teleported beside it (as 'drive'),
                    // then its own DriveVehicle() (walk to the handle, EnterVehicle). Works inside a building. The drive stream
                    // then carries it (VehicleManager.TickDriveSync, pushed-cart branch).
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    string gv = arg.Trim();
                    if (gv.Length == 0 || gv.Contains(" ")) return "ERR usage: cartgrab <vehicleId>";
                    try { if (Helpers.VehicleHelper.IsInsideVehicle()) return "ERR in a vehicle"; } catch { }
                    try
                    {
                        VehicleController? gvc = null;
                        var gl = Helpers.VehicleHelper.AllPlayerVehicles;
                        if (gl != null)
                            for (int i = 0; i < gl.Count; i++)
                            {
                                var v = gl[i];
                                if (v != null && v.vehicleInstance != null && v.vehicleInstance.id == "BAMP_" + gv && v.gameObject.activeInHierarchy) { gvc = v; break; }
                            }
                        if (gvc == null) return $"ERR no active drivable proxy of '{gv}' here";
                        if (!gvc.vehicleType.HasTag(BigAmbitions.Tags.TagRef.Vehicletag.ishandvehicle)) return "ERR not a hand vehicle";
                        string gterr = TeleportBeside(gvc.transform);
                        if (gterr.Length > 0) return "ERR cartgrab: " + gterr;
                        gvc.DriveVehicle();
                        Plugin.Logger.LogInfo($"[TestDrive] cartgrab: DriveVehicle on proxy of '{gv}' (owner '{VehicleManager.OwnerIdFor(gv)}').");
                        return $"OK cartgrab vid={gv} owner={VehicleManager.OwnerIdFor(gv)} queued";
                    }
                    catch (Exception ex) { return $"ERR cartgrab: {ex.Message}"; }
                }

                case "carthand":
                {
                    // H-CARTICON-1 fold rig lever (DEV). `carthand`: the hand vehicle in the LOCAL player's hands (held=<vid>|none,
                    // with the building here). `carthand release`: let it go where it stands with the game's own HandTruck.Release()
                    // (ExitVehicle) - the drive stream then sends the release to its owner.
                    string hmode = arg.Trim();
                    if (hmode.Length > 0 && hmode != "release") return "ERR usage: carthand [release]";
                    try
                    {
                        // A PARTNER's cart (its BAMP_ proxy) first - the fx-hq1 host also carries its own 'in use'
                        // flatbed under the player (run T-CARTICON-20260927-135706), which is not the one borrowed.
                        VehicleController? hvc = null;
                        var hall = new System.Collections.Generic.List<string>();
                        var hpc = Helpers.PlayerHelper.PlayerController;
                        if (hpc != null)
                            foreach (var v in hpc.GetComponentsInChildren<VehicleController>(true))
                            {
                                if (v == null || v.vehicleInstance == null || string.IsNullOrEmpty(v.vehicleInstance.id)) continue;
                                hall.Add(v.vehicleInstance.id);
                                if (hvc == null || (!hvc.vehicleInstance.id.StartsWith("BAMP_", StringComparison.Ordinal)
                                                    && v.vehicleInstance.id.StartsWith("BAMP_", StringComparison.Ordinal))) hvc = v;
                            }
                        string hbldg = MPRegisterSync.CurrentShopAddress ?? "";
                        if (hvc == null) return $"OK carthand held=none bldg='{hbldg}' all=[]";
                        string hid = hvc.vehicleInstance.id;
                        if (hmode != "release") return $"OK carthand held={hid} bldg='{hbldg}' all=[{string.Join(",", hall)}]";
                        if (hvc is HandTruck ht) ht.Release(); else hvc.ExitVehicle();
                        Plugin.Logger.LogInfo($"[TestDrive] carthand release '{hid}' at '{hbldg}' {hvc.transform.position}.");
                        return $"OK carthand released={hid} bldg='{hbldg}'";
                    }
                    catch (Exception ex) { return $"ERR carthand: {ex.Message}"; }
                }

                case "pinstate":
                {
                    // H-GHOSTPIN-1 rig readout (read-only). Per KEPT partner pin: is its ghost active, is the pin drawn.
                    // hidden = kept-pin ghosts currently hidden; mismatched = pins whose state differs from their ghost's.
                    if (arg.Length > 0) return "ERR usage: pinstate";
                    var ps = VehicleManager.KeptPinStates();
                    int phid = 0, pmis = 0;
                    var pl = new System.Collections.Generic.List<string>();
                    foreach (var s in ps)
                    {
                        if (!s.ghostOn) phid++;
                        if (s.ghostOn != s.pinOn) pmis++;
                        pl.Add($"{s.vid}:{s.owner}:ghost={(s.ghostOn ? "on" : "off")}:pin={(s.pinOn ? "on" : "off")}");
                    }
                    pl.Sort(StringComparer.Ordinal);
                    return $"OK pinstate n={ps.Count} hidden={phid} mismatched={pmis} [{string.Join(",", pl)}]";
                }

                case "grants":
                {
                    // HO-1c L3 rig lever. The STORED grant table as this machine holds it (owner->grantee:kind),
                    // so a run can assert the removal at merge. Read-only.
                    if (arg.Length > 0) return "ERR usage: grants";
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    var glist = GrantSync.AllStoreEntries();
                    if (glist == null || glist.Count == 0) return "OK grants 0";
                    var gsb = new StringBuilder($"OK grants {glist.Count}");
                    foreach (var g in glist) gsb.Append($" {g.Owner}->{g.Grantee}:{g.Kind}");
                    return gsb.ToString();
                }

                case "bench":
                {
                    // HO-1c L3 rig lever. The injected BENCH copies here - a partner's employee with no assigned
                    // address - as <id>:<owner>, so a run can assert that a partner's bench arrived. Read-only.
                    if (arg.Length > 0) return "ERR usage: bench";
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    var blist = Helpers.EmployeeHelper.GetEmployeeInstances();
                    var bsb = new StringBuilder();
                    int bn = 0;
                    if (blist != null)
                        foreach (var be in blist)
                        {
                            if (be == null || be.assignedAddress != null) continue;
                            if (!MPRegisterSync.IsInjectedStaff(be.id)) continue;
                            bn++;
                            // H-MERGERTRAIN-1: the PRIMARY skill (name + rounded value) and the training session.
                            // The skill is what the routed bench train has to name; the value is what makes the
                            // record trainable at all (native refuses at 100); training= is the copy's read-back.
                            // skill= is LAST on purpose: a skill NAME contains a colon of its own ("ba:skill_x"),
                            // so any field after it could not be told from part of the name (run 1 lesson).
                            string bskname = ""; int bskval = -1;
                            try { var bsk = be.characterData?.skills; if (bsk != null && bsk.Count > 0 && bsk[0] != null) { bskname = bsk[0].name; bskval = UnityEngine.Mathf.RoundToInt(bsk[0].value); } } catch { }
                            string btrain = "|-1"; try { var bts = be.trainingSession; if (bts != null) btrain = $"{bts.skill}|{bts.startDay}"; } catch { }
                            bsb.Append($" {be.id}:{MPRegisterSync.OwnerOfInjected(be.id)}:training={btrain}:skillvalue={bskval}:skill={bskname}");
                        }
                    return $"OK bench {bn}" + bsb.ToString();
                }

                case "grant":
                {
                    // BUILD POPUPS-1 P5. `grant <pid> business|housing|vehicle on|off` - the HOST's own grant
                    // store, so a run can plant a permission BEFORE a merge and then assert that accepting the
                    // merge removes it.  MPServer.HostSetGrant is the only writer; it is void and refuses a
                    // co-member silently, so its own guard is mirrored here to give the rig a visible refusal.
                    if (!MPServer.IsRunning && !MPClient.IsConnected) return "ERR no session";
                    if (!MPServer.IsRunning) return "ERR host only";
                    var ga = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (ga.Length != 3) return "ERR usage: grant <pid> business|housing|vehicle on|off";
                    string gpid = ga[0], gkindName = ga[1].ToLowerInvariant(), gon = ga[2].ToLowerInvariant();
                    GrantKind gkind;
                    if (gkindName == "business") gkind = GrantKind.Business;
                    else if (gkindName == "housing") gkind = GrantKind.Housing;
                    else if (gkindName == "vehicle") gkind = GrantKind.Vehicle;
                    else return "ERR usage: grant <pid> business|housing|vehicle on|off";
                    if (gon != "on" && gon != "off") return "ERR usage: grant <pid> business|housing|vehicle on|off";
                    if (MergerSync.MergedRuntime(MPConfig.PlayerId, gpid))
                        return "ERR '" + gpid + "' is a co-member - the merger overrides the three permissions";
                    if (!MPServer.IsOnlinePid(gpid)) return "ERR '" + gpid + "' is not an online player here";   // review MINOR-5
                    MPServer.HostSetGrant(gkind, gpid, gon == "on");
                    return $"OK grant {gpid} {gkindName} {gon}";
                }

                default:
                    return "ERR unknown verb '" + verb + "' (mark|status|ledgerdump|host|hostnew|hostload|acceptjoin|join|save|autosave|blocksave|energyflag|ledgerdrop|radiobreak|fakemod|rivalrace|charconfirm|rentdeny|rent|itemcount|enterbuilding|exitbuilding|rain|screenshot|merge|mergestatus|walletdump|regstate|employees|shift|shiftclear|autofill|fire|assign|money|prices|setprice|workedit|staffop|lists|plans|planbulk|planlist|hrtrain|hrtag|hrplanof|planown|grants|grant|bench|candidates|claim|transfer|transfers|train|messages|press|relaymsg|poachmsg|negotiations|dissolvecheck|dissolverun)";
            }
        }

        /// <summary>'drive'/'board' (H-OWNERRIDE-HOSTDRIVER-1): put the LOCAL player 3 m to the side of a car with the
        /// game's own teleport, exactly as 'tp' does (a temporary GameObject carries the target). "" on success.</summary>
        private static string TeleportBeside(UnityEngine.Transform car)
        {
            UnityEngine.GameObject? probe = null;
            try
            {
                probe = new UnityEngine.GameObject("BAMP_TpTarget");
                probe.transform.position = car.position + car.right * 3f;
                var pc = Helpers.PlayerHelper.PlayerController;
                probe.transform.rotation = pc != null ? pc.transform.rotation : UnityEngine.Quaternion.identity;
                Helpers.PlayerHelper.Teleport(probe.transform);
                return "";
            }
            catch (Exception ex) { return "teleport: " + ex.Message; }
            finally { try { if (probe != null) UnityEngine.Object.Destroy(probe); } catch { } }
        }

        /// <summary>Single quote for the key list above - an escaped char literal inside an
        /// interpolated string reads badly.</summary>
        private const char QT = '\'';

        /// <summary>Employee lookup by id over the RAW roster - the no-arg
        /// EmployeeHelper.GetEmployeeInstances(), which returns gi.EmployeeInstances unfiltered
        /// (MPPatches :1211), so a merger's injected partner records are visible to the driver.</summary>
        // ── H-SCHEDSTATION-1 lever helpers (DEV, main thread) ──────────────────────────────────────
        private static readonly System.Reflection.FieldInfo? _ssDaySel =
            HarmonyLib.AccessTools.Field(typeof(UI.Smartphone.Apps.BizMan.Schedule.BizManSchedule), "daySelectionController");
        private static readonly System.Reflection.MethodInfo? _ssSelect =
            HarmonyLib.AccessTools.Method(typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleDaySelectionController), "OnDaySelected", new[] { typeof(int) });

        private static string SsAt(Entities.EmployeeInstance? e)
        {
            if (e == null) return "MISSING";
            try { return e.assignedAddress != null ? GameStateReader.AddressKey(e.assignedAddress) : "none"; } catch { return "?"; }
        }

        private static Entities.EmployeeInstance? SsEmp(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Entities.EmployeeInstance? e = null;
            try { Helpers.EmployeeHelper.EmployeeInstancesDictionary.TryGetValue(id!, out e); } catch { }
            return e;
        }

        private static string SsErr(Exception ex)
        {
            string ty = ex.GetType().Name.Replace("Exception", "");
            string at = "";
            try
            {
                foreach (var ln in (ex.StackTrace ?? "").Split('\n'))
                {
                    int i = ln.IndexOf("ScheduleHelper", StringComparison.Ordinal);
                    if (i < 0) continue;
                    string s = ln.Substring(i + 14).TrimStart('.', ':');
                    int p = s.IndexOf('(');
                    if (p > 0) s = s.Substring(0, p);
                    at = s.Trim().Replace(' ', '_');
                    break;
                }
            }
            catch { }
            return at.Length > 0 ? ty + "@" + at : ty;
        }

        /// <summary>Open the BizMan Schedule tab on this registration (and select a day when day > 0).
        /// Returns "" on success, else the reason.</summary>
        private static string SsOpenTab(global::BuildingRegistration reg, int day)
        {
            if (!SharedShopSchedule.IsScheduleTabOpenFor(reg))
            {
                var ui = InstanceBehavior<UI.UIs>.Instance;
                if (ui == null || ui.fullMenu == null || ui.fullMenu.bizMan == null) return "no BizMan app";
                ui.fullMenu.bizMan.Open(reg.Address, "Schedule");
                if (!SharedShopSchedule.IsScheduleTabOpenFor(reg)) return "the Schedule tab did not open on this shop";
            }
            if (day <= 0) return "";
            int pos = -1;
            if (reg.scheduleDays != null)
                for (int i = 0; i < reg.scheduleDays.Count; i++)
                    if (reg.scheduleDays[i] != null && (int)reg.scheduleDays[i].day == day) { pos = i; break; }
            if (pos < 0) return $"no schedule day {day}";
            var sched = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.Business?.bizManSchedule;
            var dsc = sched != null ? _ssDaySel?.GetValue(sched) : null;
            if (dsc == null || _ssSelect == null) return "day selector not found";
            _ssSelect.Invoke(dsc, new object[] { pos + 1 });
            var cur = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay;
            if (cur == null || (int)cur.day != day) return $"day {day} not selected (page on {(cur == null ? -1 : (int)cur.day)})";
            return "";
        }

        private static string SchedTab(string arg)
        {
            try
            {
                if (arg.Trim() == "close")
                {   // the phone's own close (FullMenu.CloseFullMenu) - the page's OnDisable runs as for the player
                    var cui = InstanceBehavior<UI.UIs>.Instance;
                    if (cui == null || cui.fullMenu == null) return "ERR no full menu";
                    cui.fullMenu.CloseFullMenu();
                    return "OK schedtab close";
                }
                var tk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length < 3 || !int.TryParse(tk[tk.Length - 1], out int day) || day < 1 || day > 7)
                    return "ERR usage: schedtab <addressKey> <day 1-7> | schedtab close";
                string addr = string.Join(" ", tk, 0, tk.Length - 1);
                var reg = GameStatePatcher.FindRegistration(addr);
                if (reg == null) return $"ERR no registration for '{addr}'";
                string err = SsOpenTab(reg, day);
                if (err.Length > 0) return "ERR schedtab: " + err;
                return $"OK schedtab {addr} day={day} pageOpen={SharedShopSchedule.IsScheduleTabOpenFor(reg)}";
            }
            catch (Exception ex) { return $"ERR schedtab: {SsErr(ex)}: {ex.Message}"; }
        }

        /// <summary>The station rows the open schedule page has drawn: "stationId:sliders/cachedShifts ...".</summary>
        private static string SsRows(out int cells)
        {
            cells = -1;
            try
            {
                var rsb = new StringBuilder(); cells = 0;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<UI.Smartphone.Apps.BizMan.Schedule.ScheduleCellView>())
                {
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    cells++;
                    string wid = "?"; try { wid = c.WorkstationId; } catch { }
                    int sl = 0; try { sl = c.WorkShiftParent.GetComponentsInChildren<UI.Smartphone.Apps.BizMan.Schedule.WorkShiftSlider>(false).Length; } catch { }
                    int want = -1; try { want = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.GetWorkShiftsByWorkstationId(wid).Count; } catch { }
                    if (rsb.Length > 0) rsb.Append(' ');
                    rsb.Append(wid).Append(':').Append(sl).Append('/').Append(want);
                }
                return rsb.ToString();
            }
            catch { return "?"; }
        }

        private static string SchedRows(string arg)
        {
            try
            {
                var tk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length < 2) return "ERR usage: schedrows <addressKey> [day 1-7]";
                int n = tk.Length, day = 0;
                if (n >= 3 && int.TryParse(tk[n - 1], out int dd)) { day = dd; n--; }
                string addr = string.Join(" ", tk, 0, n);
                var reg = GameStatePatcher.FindRegistration(addr);
                if (reg == null) return $"ERR no registration for '{addr}'";
                string regKey = addr; try { regKey = GameStateReader.AddressKey(reg); } catch { }
                bool open = SharedShopSchedule.IsScheduleTabOpenFor(reg);
                int pageDay = -1, pageStations = -1, pageDict = -1;
                Dictionary<string, Entities.EmployeeInstance>? pd = null;
                if (open)
                {
                    try { var cur = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay; if (cur != null) pageDay = (int)cur.day; } catch { }
                    try { pageStations = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.WorkstationsById?.Count ?? -1; } catch { }
                    try { pd = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.EmployeesById; pageDict = pd?.Count ?? -1; } catch { }
                }
                if (day == 0 && pageDay > 0) day = pageDay;
                if (day == 0 && reg.scheduleDays != null)
                    foreach (var d in reg.scheduleDays) if (d?.workShifts != null && d.workShifts.Count > 0) { day = (int)d.day; break; }
                if (day == 0) day = 1;

                // the station rows (what FetchWorkstations reads) - read into a private list, the page's statics untouched
                var items = new List<BigAmbitions.Items.ItemInstance>();
                try { reg.GetAssignableItems(items); } catch (Exception ex) { return $"ERR schedrows GetAssignableItems: {SsErr(ex)}"; }
                // the employees the page can colour: FetchEmployees' own query, into a private list
                var dict = new HashSet<string>();
                try
                {
                    var q = Helpers.EmployeeHelper.GetEmployeeInstances(new EmployeeInstancesQueryInfo { withAssignedAddress = reg.Address });
                    if (q != null) foreach (var e in q) if (e?.id != null) dict.Add(e.id);
                }
                catch (Exception ex) { return $"ERR schedrows employees: {SsErr(ex)}"; }

                ScheduleDay? sd = null;
                try { sd = SharedShopSchedule.FindDay(reg, day); } catch { }
                int shifts = 0, notIn = 0, throwing = 0;
                var list = new StringBuilder();
                if (sd?.workShifts != null)
                    foreach (var w in sd.workShifts)
                    {
                        if (w == null) continue;
                        shifts++;
                        string? id = w.employeeId;
                        var e = SsEmp(id);
                        bool inD = id != null && dict.Contains(id);
                        bool inPage = pd == null ? inD : (id != null && pd.ContainsKey(id));
                        string colour = id == null ? "ArgNull" : (inPage ? "ok" : "KNF");
                        if (!inD) notIn++;
                        if (colour != "ok") throwing++;
                        string nm = ""; try { nm = e?.characterData?.name ?? ""; } catch { }
                        if (list.Length > 0) list.Append(" ; ");
                        list.Append(w.itemInstanceId).Append('|').Append(id ?? "<null>").Append('|')
                            .Append(w.startingHour).Append('-').Append(w.endingHour).Append('|').Append(nm)
                            .Append("|at=").Append(SsAt(e)).Append("|inDict=").Append(inD).Append("|colour=").Append(colour);
                    }

                // a candidate: an employee named by a shift of THIS shop (any day) who exists here but is not on the
                // page's list - else any injected staff copy not on the list
                string cand = "-", candSrc = "-", candAt = "-", candSt = "-";
                if (reg.scheduleDays != null)
                    foreach (var d in reg.scheduleDays)
                    {
                        if (cand != "-") break;
                        if (d?.workShifts == null) continue;
                        foreach (var w in d.workShifts)
                        {
                            string id = w?.employeeId ?? "";
                            if (id.Length == 0 || SharedShopSchedule.IsSynthetic(id) || dict.Contains(id)) continue;
                            var e = SsEmp(id);
                            if (e == null) continue;
                            cand = id; candSrc = "shift"; candAt = SsAt(e); candSt = string.IsNullOrEmpty(w!.itemInstanceId) ? "-" : w.itemInstanceId;
                            break;
                        }
                    }
                if (cand == "-")
                    try
                    {
                        foreach (var kv in Helpers.EmployeeHelper.EmployeeInstancesDictionary)
                        {
                            string id = kv.Key ?? "";
                            if (id.Length == 0 || SharedShopSchedule.IsSynthetic(id) || dict.Contains(id) || !MPRegisterSync.IsInjectedStaff(id)) continue;
                            cand = id; candSrc = "injected"; candAt = SsAt(kv.Value); break;
                        }
                    }
                    catch { }

                string st0 = "-";
                foreach (var it in items)
                {
                    if (it == null || string.IsNullOrEmpty(it.id)) continue;
                    bool cleaning = false;
                    try { cleaning = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.IsCleaningStation(it); } catch { }
                    if (!cleaning) { st0 = it.id; break; }
                }
                if (candSt == "-") candSt = st0;

                // a free hour for the candidate at candSt on this day (opening hours first)
                int slot = -1;
                if (sd != null && candSt != "-")
                {
                    var order = new List<int>();
                    try { if (sd.openingHourSlots != null) foreach (var s in sd.openingHourSlots) for (int h = s.startingHour; h < s.endingHour; h++) order.Add(h); } catch { }
                    for (int h = 0; h < 24; h++) if (!order.Contains(h)) order.Add(h);
                    foreach (int h in order)
                    {
                        bool busy = false;
                        if (sd.workShifts != null)
                            foreach (var w in sd.workShifts)
                                if (w != null && !SharedShopSchedule.IsSynthetic(w.employeeId) && (w.itemInstanceId == candSt || w.employeeId == cand) && w.startingHour <= h && h < w.endingHour) { busy = true; break; }
                        if (!busy) { slot = h; break; }
                    }
                }

                // the rows actually drawn (page open on this registration only)
                string rows = "-"; int cells = -1;
                if (open) rows = SsRows(out cells);

                var ids = new StringBuilder();
                foreach (var it in items) { if (it == null) continue; if (ids.Length > 0) ids.Append(','); ids.Append(it.id); }
                return $"OK schedrows {regKey} day={day} pageOpen={open} pageDay={pageDay} stations={items.Count} pageStations={pageStations} " +
                       $"dict={dict.Count} pageDict={pageDict} shifts={shifts} notInDict={notIn} colourThrows={throwing} cells={cells} " +
                       $"st0={st0} cand={cand} candSrc={candSrc} candAt=[{candAt}] candSt={candSt} slot={slot} " +
                       $"stationIds=[{ids}] rows=[{rows}] list=[{list}]";
            }
            catch (Exception ex) { return $"ERR schedrows: {SsErr(ex)}: {ex.Message}"; }
        }

        private static string ShiftAt(string arg)
        {
            try
            {
                var tk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int n = tk.Length;
                // optional trailing 'reload': after the add, run the page's own row reload
                // (ScheduleHelper.RequestScheduleScrollerReload - the call native AddWorkShift makes right after
                // its employee update) even when that update threw, and report the rows it drew
                bool reload = n > 0 && tk[n - 1] == "reload";
                if (reload) n--;
                if (n < 7) return "ERR usage: shiftat <addressKey> <day 1-7> <stationId|auto> <employeeId> <fromHour> <toHour|auto> [reload]";
                string toTok = tk[n - 1], fromTok = tk[n - 2], emp = tk[n - 3], st = tk[n - 4], dayTok = tk[n - 5];
                string addr = string.Join(" ", tk, 0, n - 5);
                if (!int.TryParse(dayTok, out int day) || day < 1 || day > 7) return $"ERR day '{dayTok}' is not 1-7";
                if (!int.TryParse(fromTok, out int from) || from < 0 || from > 23) return $"ERR fromHour '{fromTok}' is not 0-23";
                int to = -1;
                if (toTok != "auto" && (!int.TryParse(toTok, out to) || to <= from || to > 24)) return $"ERR toHour '{toTok}' must be after fromHour and <= 24";
                var reg = GameStatePatcher.FindRegistration(addr);
                if (reg == null) return $"ERR no registration for '{addr}'";
                var e = FindEmployee(emp);
                if (e == null) return $"ERR no employee '{emp}' on this machine";
                string openErr = SsOpenTab(reg, day);
                if (openErr.Length > 0) return "ERR shiftat: " + openErr;
                var wbi = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.WorkstationsById;
                if (st == "auto") { st = ""; if (wbi != null) foreach (var kv in wbi) if (!string.IsNullOrEmpty(kv.Key)) { st = kv.Key; break; } }
                if (wbi == null || !wbi.ContainsKey(st)) return $"ERR station '{st}' is not a row on this page";
                bool inPage = false;
                try { inPage = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.EmployeesById?.ContainsKey(emp) == true; } catch { }
                var dayObj = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay;
                int before = dayObj?.workShifts?.Count ?? -1;
                string addErr = "none", editErr = "none";
                try { UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.AddWorkShift(from, st, emp); }
                catch (Exception ex) { addErr = SsErr(ex); }
                WorkShift? made = null;
                if (dayObj?.workShifts != null)
                    foreach (var w in dayObj.workShifts)
                        if (w != null && w.employeeId == emp && w.itemInstanceId == st && w.startingHour == from) { made = w; break; }
                if (made != null && to > 0 && made.endingHour != to && addErr == "none")
                {
                    try
                    {
                        UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.EditWorkShift(st, emp, from, from, to);
                        UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.RequestScheduleScrollerReload.Invoke(false);
                    }
                    catch (Exception ex) { editErr = SsErr(ex); }
                }
                string reloadErr = "-", rowsNow = "-";
                if (reload)
                {
                    reloadErr = "none";
                    try { UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.RequestScheduleScrollerReload.Invoke(false); }
                    catch (Exception ex)
                    {
                        reloadErr = SsErr(ex);
                        try
                        {   // the exact error the page's reload raised (it propagates out of the reload call to here)
                            var fr = (ex.StackTrace ?? "").Split('\n');
                            Plugin.Logger.LogWarning($"[TestDrive] shiftat reload threw {ex.GetType().FullName}: {ex.Message} | " +
                                string.Join(" <- ", fr.Take(8).Select(x => x.Trim())));
                        }
                        catch { }
                    }
                    rowsNow = SsRows(out int rc) + " cells=" + rc;
                }
                int after = dayObj?.workShifts?.Count ?? -1;
                string regKey = addr; try { regKey = GameStateReader.AddressKey(reg); } catch { }
                return $"OK shiftat {regKey} day={day} st={st} emp={emp} empAt=[{SsAt(e)}] inPageDict={inPage} " +
                       $"shift={(made == null ? "none" : made.startingHour + "-" + made.endingHour)} dayShifts={before}->{after} addErr={addErr} editErr={editErr} reloadErr={reloadErr} rows=[{rowsNow}]";
            }
            catch (Exception ex) { return $"ERR shiftat: {SsErr(ex)}: {ex.Message}"; }
        }

        private static readonly System.Reflection.FieldInfo? _ssDragWs =
            HarmonyLib.AccessTools.Field(typeof(UI.Smartphone.Apps.BizMan.Schedule.WorkShiftDrag), "_workShift");
        private static readonly System.Reflection.FieldInfo? _ssDragRt =
            HarmonyLib.AccessTools.Field(typeof(UI.Smartphone.Apps.BizMan.Schedule.WorkShiftDrag), "workShiftRectTransform");
        private static readonly System.Reflection.FieldInfo? _ssDayToggle =
            HarmonyLib.AccessTools.Field(typeof(UI.Smartphone.Apps.BizMan.Schedule.ScheduleDayButton), "openToggle");

        /// <summary>SsOpenTab only when the page is not already on this shop and day - re-selecting the day
        /// rebuilds every row (the fold levers must act on the rows as drawn).</summary>
        private static string SsOpenIfNeeded(global::BuildingRegistration reg, int day)
        {
            try
            {
                var cur = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay;
                if (SharedShopSchedule.IsScheduleTabOpenFor(reg) && cur != null && (int)cur.day == day) return "";
            }
            catch { }
            return SsOpenTab(reg, day);
        }

        /// <summary>Fold levers: with "add=<stationId>@<fromHour>", make sure the page's current day holds a shift for
        /// <paramref name="emp"/> - added through ScheduleHelper.AddWorkShift + the page's own row reload (as 'shiftat')
        /// when absent - so the lever acts in the same frame, before the shared-shop owner's answer can replace the day.
        /// Returns "present", "added", "-" (no add asked) or "ERR ...".</summary>
        private static string SsEnsureShift(string emp, string addSpec)
        {
            try
            {
                var cur = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay;
                bool has = cur?.workShifts?.Any(x => x != null && x.employeeId == emp) == true;
                if (has) return "present";
                if (addSpec.Length == 0) return "-";
                int at = addSpec.LastIndexOf('@');
                if (at <= 0 || !int.TryParse(addSpec.Substring(at + 1), out int from)) return "ERR add= must be <stationId>@<fromHour>";
                string st = addSpec.Substring(0, at);
                try { UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.AddWorkShift(from, st, emp); }
                catch (Exception ex) { return "ERR add: " + SsErr(ex); }
                UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.RequestScheduleScrollerReload.Invoke(false);
                has = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay?.workShifts?.Any(x => x != null && x.employeeId == emp) == true;
                return has ? "added" : "ERR add: no shift after AddWorkShift";
            }
            catch (Exception ex) { return "ERR ensure: " + SsErr(ex); }
        }

        /// <summary>'shiftdrag' (H-SCHEDSTATION-1 fold F1): see the verb comment.</summary>
        private static string ShiftDrag(string arg)
        {
            try
            {
                var tk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int n = tk.Length;
                string addSpec = "";
                if (n > 0 && tk[n - 1].StartsWith("add=", StringComparison.Ordinal)) { addSpec = tk[n - 1].Substring(4); n--; }
                if (n < 4) return "ERR usage: shiftdrag <addressKey> <day 1-7> <employeeId> [add=<stationId>@<fromHour>]";
                string emp = tk[n - 1];
                if (!int.TryParse(tk[n - 2], out int day) || day < 1 || day > 7) return $"ERR day '{tk[n - 2]}' is not 1-7";
                string addr = string.Join(" ", tk, 0, n - 2);
                var reg = GameStatePatcher.FindRegistration(addr);
                if (reg == null) return $"ERR no registration for '{addr}'";
                string openErr = SsOpenIfNeeded(reg, day);
                if (openErr.Length > 0) return "ERR shiftdrag: " + openErr;
                string ensured = SsEnsureShift(emp, addSpec);
                if (ensured.StartsWith("ERR")) return "ERR shiftdrag: " + ensured;
                bool inPage = false;
                try { inPage = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.EmployeesById?.ContainsKey(emp) == true; } catch { }
                UI.Smartphone.Apps.BizMan.Schedule.WorkShiftDrag? bar = null; WorkShift? ws = null;
                int nBars = 0, nActive = 0, nNoWs = 0;
                foreach (var d in UnityEngine.Resources.FindObjectsOfTypeAll<UI.Smartphone.Apps.BizMan.Schedule.WorkShiftDrag>())
                {
                    if (d == null || d.gameObject.scene.name == null) continue;   // prefabs / assets
                    nBars++;
                    if (!d.gameObject.activeInHierarchy) continue;
                    nActive++;
                    var w = _ssDragWs?.GetValue(d) as WorkShift;
                    if (w == null) { nNoWs++; continue; }
                    if (w.employeeId == emp) { bar = d; ws = w; break; }
                }
                if (bar == null || ws == null)
                {
                    string dayHas = "?";
                    try { dayHas = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.CurrentScheduleDay?.workShifts?.Any(x => x != null && x.employeeId == emp) == true ? "Y" : "N"; } catch { }
                    return $"ERR shiftdrag: no bar for employee {emp} on the page (inPageDict={inPage} bars={nBars} active={nActive} noShift={nNoWs} dayHasShift={dayHas})";
                }
                string st = ws.itemInstanceId ?? "";
                string hasSkill;
                try { hasSkill = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.HasSkillForWorkstation(st, emp).ToString(); }
                catch (Exception ex) { hasSkill = "ERR:" + SsErr(ex); }
                UI.Smartphone.Apps.BizMan.Schedule.ScheduleCellHour? cell = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<UI.Smartphone.Apps.BizMan.Schedule.ScheduleCellHour>())
                    if (c != null && c.gameObject.activeInHierarchy && c.WorkstationId == st) { cell = c; break; }
                if (cell == null) return $"ERR shiftdrag: no hour cell of station {st} on the page";
                var rt = (_ssDragRt?.GetValue(bar) as UnityEngine.RectTransform) ?? (bar.transform as UnityEngine.RectTransform);
                var parentBefore = rt != null ? rt.parent : null;
                string dragErr = "none";
                try
                {
                    var ev = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);
                    if (rt != null) ev.position = UnityEngine.RectTransformUtility.WorldToScreenPoint(null, rt.position);
                    bar.OnBeginDrag(ev);
                    ev.pointerCurrentRaycast = new UnityEngine.EventSystems.RaycastResult { gameObject = cell.gameObject };
                    bar.OnEndDrag(ev);
                }
                catch (Exception ex)
                {
                    dragErr = SsErr(ex);
                    try { Plugin.Logger.LogWarning($"[TestDrive] shiftdrag threw {ex.GetType().Name} (swallowed by the lever): {ex.Message}"); } catch { }
                }
                bool reset = false, cached = false;
                try { reset = bar != null && rt != null && rt.parent == parentBefore; } catch { }
                try { cached = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.GetWorkShiftsByWorkstationId(st).Contains(ws); } catch { }
                bool dragging = UI.Smartphone.Apps.BizMan.Schedule.WorkShiftDrag.CurrentDraggedWorkShift != null;
                string regKey = addr; try { regKey = GameStateReader.AddressKey(reg); } catch { }
                Plugin.Logger.LogInfo($"[TestDrive] shiftdrag {regKey} day={day} emp={emp} st={st} hasSkill={hasSkill} dragErr={dragErr} reset={reset} cached={cached}.");
                return $"OK shiftdrag {regKey} day={day} emp={emp} st={st} shift={ensured} inPageDict={inPage} hasSkill={hasSkill} dragErr={dragErr} reset={reset} dragging={dragging} cached={cached} ";
            }
            catch (Exception ex) { return $"ERR shiftdrag: {SsErr(ex)}: {ex.Message}"; }
        }

        /// <summary>'daytoggle' (H-SCHEDSTATION-1 fold F2): see the verb comment.</summary>
        private static string DayToggle(string arg)
        {
            try
            {
                var tk = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int n = tk.Length;
                string addSpec = "", addEmp = "";
                if (n > 0 && tk[n - 1].StartsWith("add=", StringComparison.Ordinal)) { addSpec = tk[n - 1].Substring(4); n--; }
                if (addSpec.Length > 0 && n > 0) { addEmp = tk[n - 1]; n--; }
                if (n < 3 || !int.TryParse(tk[n - 1], out int day) || day < 1 || day > 7) return "ERR usage: daytoggle <addressKey> <day 1-7> [<employeeId> add=<stationId>@<fromHour>]";
                string addr = string.Join(" ", tk, 0, n - 1);
                var reg = GameStatePatcher.FindRegistration(addr);
                if (reg == null) return $"ERR no registration for '{addr}'";
                string openErr = SsOpenIfNeeded(reg, day);
                if (openErr.Length > 0) return "ERR daytoggle: " + openErr;
                string ensured = addSpec.Length > 0 ? SsEnsureShift(addEmp, addSpec) : "-";
                if (ensured.StartsWith("ERR")) return "ERR daytoggle: " + ensured;
                UI.Smartphone.Apps.BizMan.Schedule.ScheduleDayButton? btn = null;
                foreach (var b in UnityEngine.Object.FindObjectsOfType<UI.Smartphone.Apps.BizMan.Schedule.ScheduleDayButton>())
                    if (b != null && b.gameObject.activeInHierarchy && b.dayIndex == day) { btn = b; break; }
                if (btn == null) return $"ERR daytoggle: no day button {day} on the page";
                var tg = _ssDayToggle?.GetValue(btn) as UnityEngine.UI.Toggle;
                if (tg == null) return "ERR daytoggle: open toggle not found";
                var sd = UI.Smartphone.Apps.BizMan.Schedule.ScheduleHelper.GetScheduleDay(day);
                bool before = sd.isOpen;
                tg.SetIsOnWithoutNotify(before);   // the toggle mirrors the day (UpdateDayButton) - make sure the flip is a real change
                string e1 = "none", e2 = "none";
                try { tg.isOn = !before; } catch (Exception ex) { e1 = SsErr(ex); }
                bool mid = sd.isOpen;
                try { tg.isOn = before; } catch (Exception ex) { e2 = SsErr(ex); }
                bool after = sd.isOpen;
                string regKey = addr; try { regKey = GameStateReader.AddressKey(reg); } catch { }
                Plugin.Logger.LogInfo($"[TestDrive] daytoggle {regKey} day={day} open={before}->{mid}->{after} err1={e1} err2={e2}.");
                return $"OK daytoggle {regKey} day={day} shift={ensured} open={before}->{mid}->{after} err1={e1} err2={e2} ";
            }
            catch (Exception ex) { return $"ERR daytoggle: {SsErr(ex)}: {ex.Message}"; }
        }

        private static Entities.EmployeeInstance? FindEmployee(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var list = Helpers.EmployeeHelper.GetEmployeeInstances();
            if (list == null) return null;
            foreach (var e in list) if (e != null && e.id == id) return e;
            return null;
        }

        // ── H-WORKFF-1 part 2: 'work' lever helpers (DEV, main thread) ──────────────────────────────
        private static bool _workArmed;
        private static EmployeeStationController? _workStation;
        private static string _workDesc = "";
        private static float _workDeadline, _workPanelSeen = -1f;
        private static bool _workStartPressed, _workCallPending;
        private static float _workCalledAt;
        private static int _workRetries;

        private static PlayerActivity.WorkActivity? CurrentWorkActivity(out string actName, out PlayerActivityState st)
        {
            actName = "none"; st = PlayerActivityState.NotStarted;
            var act = UI.UIs.Instance?.playerActivityUI?.GetCurrentActivity;
            if (act == null) return null;
            actName = act.GetType().Name;
            st = act.GetState();
            return act as PlayerActivity.WorkActivity;
        }

        private static System.Collections.Generic.List<EmployeeStationController> StationsHere()
        {
            var list = new System.Collections.Generic.List<EmployeeStationController>();
            var bm = InstanceBehavior<BuildingManager>.Instance;
            if (bm == null || !BuildingManager.IsInsideBuilding) return list;
            try { if (bm.IndoorItemContainer != null) list.AddRange(bm.IndoorItemContainer.GetComponentsInChildren<EmployeeStationController>(false)); } catch { }
            try
            {
                if (bm.currentLayout != null)
                    foreach (var s in bm.currentLayout.GetComponentsInChildren<EmployeeStationController>(false))
                        if (s != null && !list.Contains(s)) list.Add(s);
            }
            catch { }
            return list;
        }

        private static bool StationHasPlayer(EmployeeStationController? s)
        {
            try { return s != null && s.employee != null && s.employee.employeeTpc != null && s.employee.employeeTpc == Helpers.PlayerHelper.PlayerController?.Character; }
            catch { return false; }
        }

        private static string WorkStateLine()
        {
            var sb = new StringBuilder();
            try
            {
                var wact = CurrentWorkActivity(out var an, out var st);
                sb.Append($"working={(wact != null && st == PlayerActivityState.Running)} activity={an} state={st} panel={PlayerActivity.PlayerActivityUI.IsPanelOpen}");
            }
            catch (Exception ex) { sb.Append($"working=? err='{ex.Message}'"); }
            try
            {
                sb.Append($" inside={BuildingManager.IsInsideBuilding}");
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (BuildingManager.IsInsideBuilding && bm?.buildingRegistration != null)
                    sb.Append($" building='{GameStateReader.AddressKey(bm.buildingRegistration)}'");
                int n = 0, withPlayer = 0;
                foreach (var s in StationsHere()) { n++; if (StationHasPlayer(s)) withPlayer++; }
                sb.Append($" stations={n} playerAtStation={withPlayer}");
            }
            catch { }
            sb.Append($" armed={_workArmed}");
            // H-SHIFTEND-1 test support (2026-09-26): the current work activity's private _finishTime (the game's
            // shift end, hh:mm; '?' = none / unreadable). Inserted before shiftOn; existing regexes use '.*' here.
            try
            {
                string fin = "?";
                var wact3 = CurrentWorkActivity(out _, out _);
                var ff = wact3 != null ? HarmonyLib.AccessTools.Field(typeof(PlayerActivity.WorkActivity), "_finishTime") : null;
                if (ff != null && ff.GetValue(wact3) is BigAmbitions.DayNightCycle.Timestamp fts)
                {
                    double fm = fts.GetTotalMinutes();
                    double r = fm - Math.Floor(fm / 1440.0) * 1440.0;
                    int hh = (int)(r / 60.0), mm = (int)(r - hh * 60.0);
                    fin = $"{hh:D2}:{mm:D2}";
                }
                sb.Append($" finish={fin}");
            }
            catch { sb.Append(" finish=?"); }
            // Review F2 test support (2026-09-26), appended so older regexes still match: shiftOn = the game's
            // private WorkActivity.IsJobShiftActive() on the current work activity ('?' = none / not askable);
            // openHours/closeHour = today's opening-hour slots of the building the player is in (-1 = none).
            try
            {
                string so = "?";
                var wact2 = CurrentWorkActivity(out _, out _);
                if (wact2 != null)
                {
                    var mi = HarmonyLib.AccessTools.Method(typeof(PlayerActivity.WorkActivity), "IsJobShiftActive");
                    if (mi != null && mi.Invoke(wact2, null) is bool on) so = on.ToString();
                }
                sb.Append($" shiftOn={so}");
            }
            catch { sb.Append(" shiftOn=?"); }
            try
            {
                var hs = new StringBuilder();
                int close = -1;
                var bm2 = InstanceBehavior<BuildingManager>.Instance;
                if (BuildingManager.IsInsideBuilding && bm2?.buildingRegistration?.scheduleDays != null)
                {
                    var dow = TimeHelper.GetDayOfWeek();
                    foreach (var sd in bm2.buildingRegistration.scheduleDays)
                    {
                        if (sd == null || sd.day != dow || sd.openingHourSlots == null) continue;
                        foreach (var sl in sd.openingHourSlots)
                        {
                            if (sl == null) continue;
                            if (hs.Length > 0) hs.Append(',');
                            hs.Append(sl.startingHour).Append('-').Append(sl.endingHour);
                            if (sl.endingHour > close) close = sl.endingHour;
                        }
                    }
                }
                sb.Append($" openHours={(hs.Length > 0 ? hs.ToString() : "none")} closeHour={close}");
            }
            catch { sb.Append(" openHours=? closeHour=-1"); }
            return sb.ToString();
        }

        /// <summary>Why CanWork said no: each of its terms, read the way the game reads them (private ones by
        /// reflection). Global terms once, then per station 'type:reqCS/stationCS/assignable/cleaning'.</summary>
        private static string CanWorkTerms(System.Collections.Generic.List<EmployeeStationController> here)
        {
            var sb = new StringBuilder();
            object? R(object? o, string name)
            {
                try
                {
                    var t = typeof(EmployeeStationController);
                    var p = HarmonyLib.AccessTools.Property(t, name);
                    if (p != null) return p.GetValue(p.GetGetMethod(true)!.IsStatic ? null : o);
                    return "?";
                }
                catch (Exception ex) { return "!" + (ex.InnerException ?? ex).GetType().Name; }
            }
            try { sb.Append($"inHands={(Helpers.PlayerHelper.ItemInstanceInHands != null)} "); } catch { sb.Append("inHands=? "); }
            try { var sv = InstanceBehavior<GameManager>.Instance.selectedVehicle; sb.Append($"selectedVehicle={(sv != null ? sv.GetType().Name + ":" + sv.name : "none")} "); } catch { sb.Append("selectedVehicle=? "); }
            try { sb.Append($"ownedBiz={InstanceBehavior<BuildingManager>.Instance.IsPlayerOwnedBusiness} "); } catch { sb.Append("ownedBiz=? "); }
            try { sb.Append($"panel={PlayerActivity.PlayerActivityUI.IsPanelOpen} "); } catch { }
            sb.Append($"reqCS={R(null, "BuildingRequiresCustomerService")} |");
            foreach (var s in here)
            {
                sb.Append(' ').Append(s.GetType().Name).Append(':');
                sb.Append("stationCS=").Append(R(s, "StationSupportsCustomerService"));
                try { sb.Append(" purchaser=").Append(s.playerItemPurchaserSettings.enabled); } catch { sb.Append(" purchaser=?"); }
                try { sb.Append(" assignable=").Append(s.Item.assignable); } catch { sb.Append(" assignable=?"); }
                sb.Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Why the walk to the register did not end in the work panel: PlayerController's goal state
        /// (PlayerController.cs:233 fires the goal only when navigation is not disabled and the path ended).</summary>
        private static string NavDiag()
        {
            try
            {
                var pc = Helpers.PlayerHelper.PlayerController;
                if (pc == null) return "no PlayerController";
                object? hg = HarmonyLib.AccessTools.Field(pc.GetType(), "_hasGoal")?.GetValue(pc);
                object? hp = HarmonyLib.AccessTools.Field(pc.GetType(), "_hasPath")?.GetValue(pc);
                var bl = HarmonyLib.AccessTools.Field(pc.GetType(), "_activeNavigationBlockers")?.GetValue(pc) as System.Collections.IEnumerable;
                var names = new System.Collections.Generic.List<string>();
                if (bl != null) foreach (var b in bl) names.Add(b?.ToString() ?? "?");
                float d = -1f; try { if (_workStation != null) d = UnityEngine.Vector3.Distance(Helpers.PlayerHelper.GetPosition(), _workStation.transform.position); } catch { }
                bool sv = false; try { sv = InstanceBehavior<GameManager>.Instance.selectedVehicle != null; } catch { }
                return $"navDisabled={pc.NavigationDisabled} hasGoal={hg} hasPath={hp} blockers=[{string.Join(",", names)}] dToRegister={d:0.0}m selectedVehicle={sv} usingVehicle={Helpers.PlayerHelper.IsUsingVehicle}";
            }
            catch (Exception ex) { return "navdiag err " + ex.Message; }
        }

        private static string WorkFail(string why)
        {
            Plugin.Logger.LogWarning($"[DEV] work on FAILED: {why}");
            return "ERR work on: " + why;
        }

        private static string WorkOn()
        {
            try
            {
                if (_workArmed) return "ERR work on: already in progress";
                if (!BuildingManager.IsInsideBuilding) return WorkFail("not inside a building");
                var bm = InstanceBehavior<BuildingManager>.Instance;
                if (bm == null || bm.buildingRegistration == null) return WorkFail("no BuildingManager / registration");
                if (PlayerActivity.PlayerActivityUI.IsPanelOpen)
                {
                    CurrentWorkActivity(out var an0, out var st0);
                    return WorkFail($"an activity is already open ({an0} {st0})");
                }
                // A player pushing a hand truck / flatbed cannot work (CanWork :75 'selectedVehicle != null'); a real
                // player lets go first. Same let-go the game's own code uses: HandTruck.Release (HandTruck.cs:129 -> ExitVehicle :189).
                string released = "";
                try
                {
                    if (InstanceBehavior<GameManager>.Instance.selectedVehicle is HandTruck ht && ht != null && ht.controlledByPlayer)
                    {
                        string htName = ht.name;
                        ht.Release();
                        released = $" after letting go of the pushed {htName} (HandTruck.Release)";
                        Plugin.Logger.LogInfo($"[DEV] work: let go of the pushed {htName} first (HandTruck.Release, HandTruck.cs:129).");
                    }
                }
                catch (Exception ex) { return WorkFail("could not let go of the pushed vehicle: " + ex.Message); }
                string bname = ""; try { bname = bm.buildingRegistration.BusinessName?.ToString() ?? ""; } catch { }
                string bdesc = $"'{bname}' {GameStateReader.AddressKey(bm.buildingRegistration)}";
                var here = StationsHere();
                UnityEngine.Vector3 me = Helpers.PlayerHelper.GetPosition();
                EmployeeStationController? best = null; float bestD2 = float.MaxValue; int canWork = 0;
                foreach (var s in here)
                {
                    bool cw = false; try { cw = s.CanWork(); } catch { }
                    if (!cw) continue;
                    canWork++;
                    float d2 = (s.transform.position - me).sqrMagnitude;
                    if (d2 < bestD2) { bestD2 = d2; best = s; }
                }
                if (best == null)
                    return WorkFail($"no station in {bdesc} passes CanWork (stations={here.Count}) - CanWork terms (EmployeeStationController.cs:69-81, :240-247): {CanWorkTerms(here)}");
                string rdesc = $"{best.GetType().Name}#{MPRegisterSync.RegKeyForStation(best)} ({UnityEngine.Mathf.Sqrt(bestD2):0.0} m away)";
                _workStation = best; _workDesc = $"{rdesc} in {bdesc}{released}";
                _workPanelSeen = -1f; _workStartPressed = false; _workRetries = 0;
                _workDeadline = UnityEngine.Time.unscaledTime + 40f;
                if (released.Length > 0) { _workCallPending = true; _workArmed = true; }   // Work() on the next tick, once the let-go settled
                else
                {
                    bool ok = best.Work();   // Interact's own fall-through (:169-175) — what the click runs
                    if (!ok) return WorkFail($"EmployeeStationController.Work() refused at {rdesc} in {bdesc}");
                    _workCalledAt = UnityEngine.Time.unscaledTime; _workArmed = true;
                }
                return $"OK work on queued at {_workDesc} (registers={canWork}/{here.Count}) - result in log ([DEV] work line)";
            }
            catch (Exception ex) { _workArmed = false; return WorkFail("exception " + ex.Message); }
        }

        private static void TickWork()
        {
            try
            {
                var wact = CurrentWorkActivity(out var an, out var st);
                float now = UnityEngine.Time.unscaledTime;
                if (_workCallPending)
                {
                    _workCallPending = false;
                    if (_workStation == null || !_workStation.Work())
                    {
                        _workArmed = false;
                        WorkFail($"EmployeeStationController.Work() refused at {_workDesc} - {NavDiag()}");
                        return;
                    }
                    _workCalledAt = now;
                    return;
                }
                if (wact != null && st == PlayerActivityState.Running)
                {
                    _workArmed = false;
                    Plugin.Logger.LogInfo($"[DEV] work on at {_workDesc} (state=Running playerIsStationEmployee={StationHasPlayer(_workStation)} startPressedByLever={_workStartPressed})");
                    return;
                }
                if (wact != null && st == PlayerActivityState.NotStarted)
                {
                    if (_workPanelSeen < 0f) _workPanelSeen = now;
                    else if (!_workStartPressed && now - _workPanelSeen >= 2f)
                    {
                        UI.Elements.ButtonInfo? start = null;
                        var btns = wact.GetButtons();
                        if (btns != null) foreach (var b in btns) if (b != null && b.name == "StartWork") { start = b; break; }
                        if (start == null || start.onClick == null)
                        {
                            _workArmed = false;
                            WorkFail($"the work panel offers no Start at {_workDesc} (shop closed and not opening within 2 h?)");
                            return;
                        }
                        _workStartPressed = true;
                        start.onClick();
                        Plugin.Logger.LogInfo("[DEV] work: the panel's StartWork pressed by the lever (nothing auto-started it within 2 s).");
                    }
                }
                if (wact == null && !PlayerActivity.PlayerActivityUI.IsPanelOpen && now - _workCalledAt > 8f && _workRetries < 2 && _workStation != null)
                {
                    // A player whose click did not bring the panel up clicks the register again.
                    _workRetries++;
                    Plugin.Logger.LogWarning($"[DEV] work: no work panel {now - _workCalledAt:0} s after Work() - {NavDiag()} - clicking the register again (retry {_workRetries}/2)");
                    if (_workStation.Work()) _workCalledAt = now;
                }
                if (now > _workDeadline)
                {
                    _workArmed = false;
                    WorkFail($"not Running 40 s after 'work on' at {_workDesc} (activity={an} state={st} panel={PlayerActivity.PlayerActivityUI.IsPanelOpen}) - {NavDiag()}");
                }
            }
            catch (Exception ex) { _workArmed = false; Plugin.Logger.LogWarning($"[DEV] work on FAILED: exception {ex.Message}"); }
        }

        private static string WorkOff()
        {
            try
            {
                _workArmed = false;
                var wact = CurrentWorkActivity(out var an, out var st);
                if (wact == null)
                {
                    Plugin.Logger.LogWarning($"[DEV] work off FAILED: no work activity (current={an} {st})");
                    return $"ERR work off: not working (current={an} {st})";
                }
                UI.Elements.ButtonInfo? stop = null;
                var btns = wact.GetButtons();
                if (btns != null) foreach (var b in btns) if (b != null && b.name == "StopWork") { stop = b; break; }
                // Review F3 (2026-09-26): a WorkActivity that is NOT started (NotStarted: the panel shows only
                // Cancel, WorkActivity.GetButtons :430-431) is left by the game's own CancelWork button, not Finish().
                UI.Elements.ButtonInfo? cancel = null;
                bool started = st == PlayerActivityState.Started || st == PlayerActivityState.Running;
                if (stop?.onClick == null && !started && btns != null)
                    foreach (var b in btns) if (b != null && b.name == "CancelWork") { cancel = b; break; }
                string via;
                if (stop?.onClick != null) { stop.onClick(); via = "the StopWork button"; }
                else if (cancel?.onClick != null) { cancel.onClick(); via = "the CancelWork button"; }
                else { wact.Finish(); via = "WorkActivity.Finish()"; }
                Plugin.Logger.LogInfo($"[DEV] work off (was {st}; via {via})");
                return $"OK work off (was {st})";
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DEV] work off FAILED: exception {ex.Message}");
                return "ERR work off: " + ex.Message;
            }
        }

        // -- H-RIVALPARITY-1 item C helpers (READ-ONLY) ------------------------------------------------
        private static string MktItem(string s) => (s == "*" || s.StartsWith("ba:", StringComparison.Ordinal)) ? s : "ba:itemname_" + s;
        private static string MktNb(string s)   => (s == "*" || s.StartsWith("ba:", StringComparison.Ordinal)) ? s : "ba:neighborhood_" + s;

        private static System.Reflection.MethodInfo? _mktGetProviders;

        /// <summary>The game's CACHED seller count - ProductMarketHelper.GetProviders (private static, :358-361), the
        /// number UpdateMarketDemand (:249) feeds CalculateDemand. Rebuilt by the game only in FillProvidersDictionary
        /// (daily from CompetitionHelper.RunDaily :103, and on load).</summary>
        private static int MktCachedProviders(string item, string nb)
        {
            _mktGetProviders ??= HarmonyLib.AccessTools.Method(typeof(Helpers.ProductMarketHelper), "GetProviders");
            if (_mktGetProviders == null) return -2;
            return (int)_mktGetProviders.Invoke(null, new object[] { item, nb });
        }

        /// <summary>item.GetOptimalProviders() (BigAmbitions.dll; CalculateDemand :405). Reflection: instance method or
        /// a static extension in the item's own assembly.</summary>
        private static int MktOptimal(object item)
        {
            try
            {
                var t = item.GetType();
                var m = HarmonyLib.AccessTools.Method(t, "GetOptimalProviders", Type.EmptyTypes);
                if (m != null && !m.IsStatic) return (int)m.Invoke(item, null);
                foreach (var st in t.Assembly.GetTypes())
                {
                    if (!st.IsAbstract || !st.IsSealed) continue;
                    foreach (var sm in st.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                    {
                        if (sm.Name != "GetOptimalProviders") continue;
                        var ps = sm.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(t)) return (int)sm.Invoke(null, new object[] { item });
                    }
                }
            }
            catch { }
            return -1;
        }

        /// <summary>CalculateDemand :405-406 without its +-1 jitter and before events: 100 - providers*100/optimal,
        /// or the random 29..34 floor below 30.</summary>
        private static string MktFormula(int providers, int optimal)
        {
            if (optimal <= 0 || providers < 0) return "?";
            int n = 100 - providers * 100 / optimal;
            return n < 30 ? "29..34" : Math.Min(n, 100).ToString();
        }

        /// <summary>Who holds this registration, from THIS machine's view: own (the local player's tenancy),
        /// player:&lt;pid&gt; (another session player - GameStatePatcher.IsAnyPlayerBusiness), or ai:&lt;rivalId|-&gt;.</summary>
        private static string MktWho(BuildingRegistration reg)
        {
            string owner = ""; try { owner = reg.businessOwnerRivalId?.ToString() ?? ""; } catch { }
            bool foreign = false; try { foreign = GameStatePatcher.IsForeignPlayerBusiness(reg); } catch { }
            if (reg.RentedByPlayer && !foreign) return "own";
            bool player = false; try { player = GameStatePatcher.IsAnyPlayerBusiness(reg); } catch { }
            if (player || foreign) return "player:" + (owner.Length > 0 ? owner : "?");
            return "ai:" + (owner.Length > 0 ? owner : "-");
        }

        /// <summary>Shelf stock per item at one registration - the same walk as the `stockof` lever
        /// (BuildingHelper.GetItemsWithStock, BuildingHelper.cs:446-455).</summary>
        private static System.Collections.Generic.SortedDictionary<string, int> MktStockAll(BuildingRegistration reg)
        {
            var tot = new System.Collections.Generic.SortedDictionary<string, int>(StringComparer.Ordinal);
            if (reg.itemInstances == null) return tot;
            foreach (var kv in reg.itemInstances)
            {
                var ii = kv.Value;
                if (ii?.cargoInstances == null) continue;
                foreach (var c in ii.cargoInstances)
                {
                    if (c == null || c.amount <= 0) continue;
                    if (c.nestedCargoInstances != null && c.nestedCargoInstances.Count > 0) continue;
                    string n = c.itemName ?? "";
                    if (n.Length == 0) continue;
                    int prev; tot.TryGetValue(n, out prev);
                    tot[n] = prev + c.amount;
                }
            }
            return tot;
        }

        /// <summary>The LIVE seller count for (item, nb) by FillProvidersDictionary's own rule (:333-353): a registration
        /// not for rent, with a non-empty cachedAvailableProducts, not a special service, whose list holds the item, in
        /// that neighbourhood, counts if RentedByPlayer or PlayerItemPurchaser.GetShelfFillState(item, reg) &gt; 0.
        /// One row per such seller, plus every player-held registration here with shelf stock of the item (counted or
        /// not); playerStockedNotCounted = player-held shops holding the item that the rule does NOT count.</summary>
        private static int MktLiveProviders(string item, string nb, System.Collections.Generic.List<string> rows, out int playerStockedNotCounted)
        {
            int live = 0; playerStockedNotCounted = 0;
            foreach (var reg in SaveGameManager.Current.BuildingRegistrations)
            {
                if (reg == null) continue;
                Buildings.Building? b = null;
                try { b = Helpers.BuildingHelper.GetBuilding(reg.Address); } catch { }
                if (b == null || b.Neighbourhood != nb) continue;
                var list = reg.cachedAvailableProducts;
                int listN = list?.Count ?? 0;
                bool listHas = list != null && list.Contains(item);
                bool player = false; try { player = GameStatePatcher.IsAnyPlayerBusiness(reg); } catch { }
                bool held = player || reg.RentedByPlayer;
                int stock = 0; try { MktStockAll(reg).TryGetValue(item, out stock); } catch { }
                if (!listHas && !(held && stock > 0)) continue;
                bool special = false; try { special = b.SpecialService != null; } catch { }
                bool counted = false; string fill = "-";
                if (!reg.AvailableForRent && listN > 0 && !special && listHas)
                {
                    if (reg.RentedByPlayer) counted = true;
                    else
                    {
                        float f = -1f;
                        try { f = Controllers.PlayerItemPurchaser.GetShelfFillState(item, reg); } catch { }
                        fill = f.ToString("F2");
                        counted = f > 0f;
                    }
                }
                if (counted) live++;
                else if (held && stock > 0) playerStockedNotCounted++;
                string key = ""; try { key = GameStateReader.AddressKey(reg); } catch { }
                rows.Add($"{key}|{MktWho(reg)}|rented={reg.RentedByPlayer}|forRent={reg.AvailableForRent}|special={special}|list={listN}|listHas={listHas}|fill={fill}|stock={stock}|counted={counted}");
            }
            return live;
        }

        /// <summary>`providers * &lt;nb|*&gt;` - FIXTURE DISCOVERY: every player-held registration (own or another
        /// session player's) in nb with a product list or shelf stock, and the local player's OWN best-stocked
        /// demand-capable item (firstStocked*), preferring one its product list also names.</summary>
        private static string MktScan(string nb)
        {
            var rows = new System.Collections.Generic.List<string>();
            string first = "", firstAt = "", firstNb = ""; int firstAmt = 0; bool firstList = false;
            foreach (var reg in SaveGameManager.Current.BuildingRegistrations)
            {
                if (reg == null) continue;
                string rnb = ""; try { rnb = Helpers.BuildingHelper.GetBuilding(reg.Address)?.Neighbourhood ?? ""; } catch { }
                if (nb != "*" && rnb != nb) continue;
                bool player = false; try { player = GameStatePatcher.IsAnyPlayerBusiness(reg); } catch { }
                if (!player && !reg.RentedByPlayer) continue;
                var st = MktStockAll(reg);
                var list = reg.cachedAvailableProducts;
                if (st.Count == 0 && (list == null || list.Count == 0)) continue;
                string key = ""; try { key = GameStateReader.AddressKey(reg); } catch { }
                string who = MktWho(reg);
                var sts = new System.Collections.Generic.List<string>();
                foreach (var kv in st) sts.Add($"{kv.Key}:{kv.Value}");
                string ls = list == null ? "null" : string.Join(",", list.Count > 12 ? list.GetRange(0, 12) : list) + (list.Count > 12 ? ",..." : "");
                rows.Add($"{key}|{who}|nb={rnb}|rented={reg.RentedByPlayer}|type={reg.businessTypeName}|list=[{ls}]|stock=[{string.Join(",", sts)}]");
                if (who != "own") continue;
                foreach (var kv in st)
                {
                    bool can = false;
                    try { can = Helpers.ProductMarketHelper.CanNeighborhoodHaveItemDemand(rnb, kv.Key); } catch { }
                    if (!can) continue;
                    bool inList = list != null && list.Contains(kv.Key);
                    bool better = first.Length == 0 || (inList && !firstList) || (inList == firstList && kv.Value > firstAmt);
                    if (!better) continue;
                    first = kv.Key; firstAt = key; firstNb = rnb; firstAmt = kv.Value; firstList = inList;
                }
            }
            return $"OK providers-scan nb={nb} shops={rows.Count} firstStocked={first} firstStockedAt='{firstAt}' firstStockedNb={firstNb} "
                 + $"firstStockedAmt={firstAmt} firstStockedInList={firstList} [{string.Join(" ; ", rows)}]";
        }

        /// <summary>Armed by 'charconfirm'. Runs on the 0.5s tick cadence: waits for
        /// IntroCharacterCustomizer to exist, lets it settle 2s (the deleted autopilot's
        /// proven minimum — invoking during its Start()/coroutines leaves the UI broken),
        /// then reflect-invokes StartGame(). Disarms after one confirm or on a missing
        /// method; a customizer that disappears before confirm resets the settle clock.</summary>
        private static void TickCustomizerConfirm()
        {
            try
            {
                var found = UnityEngine.Object.FindObjectsOfType(typeof(Intro.IntroCharacterCustomizer));
                var cust = (found != null && found.Length > 0) ? found[0] : null;
                if (cust == null) { _customizerFirstSeen = -1f; return; }
                if (_customizerFirstSeen < 0f)
                {
                    _customizerFirstSeen = UnityEngine.Time.unscaledTime;
                    Plugin.Logger.LogInfo("[TestDrive] charconfirm: customizer seen — settling 2s.");
                    return;
                }
                if (UnityEngine.Time.unscaledTime - _customizerFirstSeen < 2f) return;
                var m = cust.GetType().GetMethod("StartGame",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Instance);
                if (m == null)
                {
                    ConfirmCustomizerArmed = false;
                    Plugin.Logger.LogWarning("[TestDrive] charconfirm: StartGame not found on IntroCharacterCustomizer — disarmed.");
                    return;
                }
                ConfirmCustomizerArmed = false;
                _customizerFirstSeen = -1f;
                m.Invoke(cust, null);
                Plugin.Logger.LogWarning("[TestDrive] charconfirm: IntroCharacterCustomizer.StartGame invoked.");
            }
            catch (Exception ex)
            {
                ConfirmCustomizerArmed = false;
                Plugin.Logger.LogWarning($"[TestDrive] charconfirm: {ex.Message} — disarmed.");
            }
        }
    }
}
#endif
