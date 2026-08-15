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
    /// (both rig instances share that LocalLow, so commands are ROLE-ADDRESSED by filename:
    /// "h-*.cmd" runs on the HOST instance — the one NOT installed under BigAmbitions2 —
    /// and "c-*.cmd" on the CLIENT instance).  A .cmd file holds one command line; the mod
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

        /// <summary>Instance role by install location: the rig's second instance lives under
        /// C:\BigAmbitions2 (launch_client.bat); anything else is the Steam (host) install.</summary>
        private static string Role =>
            MPConfig.GameRootPath.IndexOf("BigAmbitions2", StringComparison.OrdinalIgnoreCase) >= 0 ? "c" : "h";

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
                    if (!MPSaveManager.ListSessions().Exists(s => s.Name == arg))
                        return $"ERR session '{arg}' not in the session list";
                    MPServer.ChosenLoadSession = arg;
                    MPServer.StartLoadGame();
                    return $"OK StartLoadGame('{arg}') invoked (lobby clients are served their saves; later joiners need 'acceptjoin')";
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
                case "energyflag":
                {
                    var gv = SaveGameManager.Current?.gameVariables;
                    if (gv == null) return "ERR no loaded game";
                    gv.disableEnergy = arg == "on" || arg == "true";
                    return $"OK gv.disableEnergy={gv.disableEnergy} (heartbeat should re-align it within ~3s in MP)";
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

                default:
                    return "ERR unknown verb '" + verb + "' (mark|status|ledgerdump|host|hostnew|hostload|acceptjoin|join|save|autosave|blocksave|energyflag|ledgerdrop|radiobreak|fakemod|rivalrace|charconfirm|rentdeny|rent|itemcount)";
            }
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
