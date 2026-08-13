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

        internal static void Tick()
        {
            if (UnityEngine.Time.unscaledTime < _nextPoll) return;
            _nextPoll = UnityEngine.Time.unscaledTime + 0.5f;
            try
            {
                _dir ??= Path.Combine(MPConfig.DataRootPath, "testdrive");
                if (!Directory.Exists(_dir)) return;   // channel not armed — fully inert
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
                    try { sb.Append($"ledger={MPServer.BuildingOwners.Count}"); } catch { }
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

                case "hostload":
                    if (!MPServer.IsRunning) return "ERR start the server first ('host')";
                    if (arg.Length == 0) return "ERR session name required";
                    MPSaveCoordinator.HostLoadSession(arg);
                    return $"OK HostLoadSession('{arg}') invoked — verify via [MPSave] log lines";

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
                    return MPServer.BuildingOwners.TryRemove(arg, out var was)
                        ? $"OK ledger entry '{arg}' → '{was}' REMOVED (synthetic zombie; expect [LedgerHeal] ADOPTED after the owner's next claim report)"
                        : $"ERR no ledger entry for '{arg}'";
                }

                default:
                    return "ERR unknown verb '" + verb + "' (mark|status|ledgerdump|host|hostload|join|save|autosave|blocksave|energyflag|ledgerdrop)";
            }
        }
    }
}
#endif
