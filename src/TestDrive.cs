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

        /// <summary>Round-260 "rentdeny" verb state — while true, the host denies every
        /// RentRequest with a TestDrive reason, letting an agent exercise the client's
        /// optimistic-rent rollback (the starter-item leak) without needing a genuinely
        /// occupied building.</summary>
        internal static bool ForceRentDeny;

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

                case "forrent":
                {
                    // Round-260 helper: list up to 8 for-rent addresses on this machine.
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
                    MPSaveCoordinator.HostSaveNow("autosave");
                    return "OK HostSaveNow('autosave') invoked";

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
                    // pipeline (save-store attach + peer-log pull + zip). Point the config's
                    // BugReportRelayUrl at an unreachable address first so nothing posts to
                    // the live Discord — the zip still gets built before the upload fails.
                    string why = arg.Length > 0 ? arg : "testdrive report";
                    var rep = MPBugReport.Create("testdrive: " + why, openFolder: false);
                    return $"OK report dir={rep.DirectoryPath} uploadQueued={rep.DiscordUploadQueued}";
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

                case "employees":
                {
                    // RAW roster (the no-arg EmployeeHelper.GetEmployeeInstances() - the same list
                    // MPPatches :1226 reads), so a merger's injected partner records are included.
                    // CharacterData carries ONE name string, not a first/last pair.
                    var elist = Helpers.EmployeeHelper.GetEmployeeInstances();
                    if (elist == null) return "ERR no employee roster";
                    var esb = new StringBuilder();
                    int eshown = 0, etotal = 0;
                    foreach (var e in elist)
                    {
                        if (e == null) continue;
                        string eaddr = "";
                        try { if (e.assignedAddress != null) eaddr = GameStateReader.AddressKey(e.assignedAddress); } catch { }
                        if (arg.Length > 0 && !string.Equals(eaddr, arg, StringComparison.OrdinalIgnoreCase)) continue;
                        etotal++;
                        if (arg.Length == 0 && eshown >= 30) continue;   // cap only the ALL-employees listing; an address-scoped list is bounded by that shop (run T-P0-6: a 51-staff shop hid the adopted hire, 2026-09-10)
                        string ename = ""; try { ename = e.characterData?.name?.ToString() ?? ""; } catch { }
                        bool einj = false; try { einj = MPRegisterSync.IsInjectedStaff(e.id); } catch { }
                        // Phase 4b: the bonus figures as THIS machine reads them (on a copy the cooldown is not synced, so canbonus is the satisfaction test only; the runner's own record is the real gate).
                        float ebonus = 0f; bool ecan = false; try { ebonus = e.GetBonusAmount(); ecan = e.CanGiveBonus(); } catch { }
                        // Phase 4b (people): the primary skill as this machine reads it, so a scenario can pick a trainable employee (value < 100).
                        string eskill = ""; try { var esk = e.characterData?.skills; if (esk != null && esk.Count > 0 && esk[0] != null) eskill = $"{esk[0].name}:{esk[0].value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}"; } catch { }
                        string eline = $"{e.id}|{ename}|assigned={eaddr}|injected={einj}|bonus={ebonus.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}|canbonus={ecan}|skill={eskill}";
                        Plugin.Logger.LogWarning($"[TestDrive] employee: {eline}");
                        if (eshown++ > 0) esb.Append(" ; ");
                        esb.Append(eline);
                    }
                    return $"OK {etotal} employee(s){(arg.Length > 0 ? $" @ '{arg}'" : "")}: {esb}";
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
                    if (stk.Length < 5) return "ERR usage: staffop <num> <ba:street_x> <employeeId> raise <wage> | bonus <amount>";
                    string saddr  = stk[0] + " " + stk[1];
                    string sempId = stk[2];
                    string sop    = stk[3];
                    if (sop != "raise" && sop != "bonus") return "ERR only 'raise' and 'bonus' are routed (training and to-do are refused on a routed record)";
                    if (!float.TryParse(stk[4], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var swage))
                        return sop == "bonus" ? "ERR amount must be a number" : "ERR wage must be a number";
                    var sreg = GameStatePatcher.FindRegistration(saddr);
                    if (sreg == null) return $"ERR no registration at '{saddr}'";
                    string skey = saddr; try { skey = GameStateReader.AddressKey(sreg); } catch { }
                    if (FindEmployee(sempId) == null) return $"ERR no employee '{sempId}' on this machine";
                    // Phase 4b: "bonus" carries the AMOUNT in the payload field the wage uses; the machine that
                    // runs the address re-runs the game's own GiveBonus and treats the figure only as a bound.
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
                    if (arg.Length == 0) return "ERR employee id required";
                    var rnemp = FindEmployee(arg);
                    if (rnemp == null) return $"ERR no employee '{arg}' on this machine";
                    string rnaddr = ""; try { if (rnemp.assignedAddress != null) rnaddr = GameStateReader.AddressKey(rnemp.assignedAddress); } catch { }
                    float rncost = 0f;
                    try
                    {
                        var rnsk = rnemp.characterData.skills[0];
                        rncost = Helpers.EmployeeHelper.GetTrainingCost(rnemp, rnsk.name, UnityEngine.Mathf.Min(UnityEngine.Mathf.CeilToInt(100f - rnsk.value), 10));
                    }
                    catch { }
                    bool rnrouted = SharedShopStaff.CommitStaffOp(arg, rnaddr, "train", rncost);
                    return $"OK train addr='{rnaddr}' employee='{arg}' cost={rncost.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} routed={rnrouted}";
                }

                default:
                    return "ERR unknown verb '" + verb + "' (mark|status|ledgerdump|host|hostnew|hostload|acceptjoin|join|save|autosave|blocksave|energyflag|ledgerdrop|radiobreak|fakemod|rivalrace|charconfirm|rentdeny|rent|itemcount|enterbuilding|exitbuilding|rain|screenshot|merge|mergestatus|regstate|employees|shift|shiftclear|autofill|fire|assign|money|prices|setprice|workedit|staffop|lists|plans|candidates|claim|transfer|transfers|train|messages|press|relaymsg|poachmsg)";
            }
        }

        /// <summary>Single quote for the key list above - an escaped char literal inside an
        /// interpolated string reads badly.</summary>
        private const char QT = '\'';

        /// <summary>Employee lookup by id over the RAW roster - the no-arg
        /// EmployeeHelper.GetEmployeeInstances(), which returns gi.EmployeeInstances unfiltered
        /// (MPPatches :1211), so a merger's injected partner records are visible to the driver.</summary>
        private static Entities.EmployeeInstance? FindEmployee(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var list = Helpers.EmployeeHelper.GetEmployeeInstances();
            if (list == null) return null;
            foreach (var e in list) if (e != null && e.id == id) return e;
            return null;
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
