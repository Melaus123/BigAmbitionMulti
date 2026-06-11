using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace BigAmbitionsMP
{
    // ── Coordinated MP save (Phase 4, step 3) ─────────────────────────────────
    //
    // Model: PEER SIMULATION, CENTRALIZED PERSISTENCE.  Each player runs their
    // own game locally (the host does NOT simulate everyone), but every player's
    // saved game lands on the HOST so the host holds one complete, self-sufficient
    // session — nothing important lives only on a client.  This also means a
    // dropped player's latest .hsg is already safe on the host.
    //
    // Layout (on the HOST's disk):
    //   <SaveGames>/_BAMP_MP/<version>/<session>/<stableId>/save.hsg   (one per player)
    //   <SaveGames>/_BAMP_MP/<version>/<session>/manifest.bamp.json    (slots + owners)
    //
    // Trigger flow:
    //   Host HostSaveNow() → broadcasts SaveNow{session} + saves its own .hsg
    //     locally (already on the host) + writes the manifest base.
    //   Each client, on SaveNow → SaveGameManager.Save locally, waits for the
    //     write to finish, then ships gzip(.hsg) to the host via SaveData.
    //   Host, on each SaveData → writes the bytes into its own session folder +
    //     folds the slot into the manifest (rewritten idempotently).
    public static class MPSaveCoordinator
    {
        private const string SaveFileName = "save";   // <stableId>/save.hsg per player

        private static readonly object  _lock = new();
        private static MpManifest?      _activeManifest;
        private static string           _activeSessionName = "";

        /// <summary>The MP save session currently in use.  Set when a save fires
        /// or when an existing session is loaded (Phase 4 step 4), so repeated
        /// saves overwrite the same session rather than spawning new folders.</summary>
        public static string ActiveSessionName
        {
            get => _activeSessionName;
            set { lock (_lock) { _activeSessionName = value ?? ""; _activeManifest = null; } }
        }

        // ── Host entry point ──────────────────────────────────────────────────

        /// <summary>Host: trigger a coordinated save.  Safe to call from any
        /// thread.  Resolves/creates the session name, tells every client to
        /// save, and performs the host's own save + manifest write on the main
        /// thread.</summary>
        public static void HostSaveNow(string reason = "manual")
        {
            if (!MPServer.IsRunning)
            {
                Plugin.Logger.LogWarning("[MPSave] HostSaveNow ignored — not hosting.");
                return;
            }

            string session;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_activeSessionName))
                    _activeSessionName = DefaultSessionName();
                session = _activeSessionName;
            }

            Plugin.Logger.LogInfo($"[MPSave] HostSaveNow session='{session}' reason={reason} — broadcasting SaveNow.");

            try
            {
                var payload = new SaveNowPayload { SessionName = session, Reason = reason };
                MPServer.BroadcastAny(MessageEnvelope.Create(MessageType.SaveNow, "host", payload));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] SaveNow broadcast: {ex.Message}"); }

            // Host's own save + manifest base — on the main thread (IL2CPP access).
            // The host's .hsg is written straight into its own MP folder, so there
            // is nothing to transfer.
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var slot = PerformLocalSave(session);
                    DiagPhase("host lambda: PerformLocalSave done → SetSessionMetadata");
                    SetSessionMetadata(session, slot.Day);   // owners + day + timestamp
                    DiagPhase("host lambda: SetSessionMetadata done → MergeSlot");
                    MergeSlot(session, slot);                 // host's own slot
                    // Loan ledger rides every session save — loans created
                    // BEFORE the session's first save (no folder yet) would
                    // otherwise never persist unless the ledger changed again.
                    MPHub.SaveLedger();
                    DiagPhase("host lambda: DONE");
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] Host save: {ex}"); }
            });
        }

        // ── In-game pause-menu save (MiniMenu Save / Save-and-Exit) ─────────────

        /// <summary>Host: run a coordinated save SYNCHRONOUSLY on the calling
        /// (main) thread.  HostSaveNow enqueues the save for a later frame, which
        /// is wrong for the pause-menu buttons: Save-and-Exit quits the same
        /// frame, so an enqueued save would never drain.  Here we broadcast
        /// SaveNow to the clients (their save+upload runs asynchronously — fine,
        /// since the thing an immediate quit must not lose is the host's OWN
        /// copy) and write the host's own .hsg + manifest inline.  Blocks for the
        /// save's duration (the expected brief stutter).  MAIN THREAD ONLY.</summary>
        public static void HostSaveSync(string reason = "menu")
        {
            if (!MPServer.IsRunning) return;
            string session;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_activeSessionName))
                    _activeSessionName = DefaultSessionName();
                session = _activeSessionName;
            }
            Plugin.Logger.LogInfo($"[MPSave] HostSaveSync session='{session}' reason={reason} (inline).");
            try
            {
                var payload = new SaveNowPayload { SessionName = session, Reason = reason };
                MPServer.BroadcastAny(MessageEnvelope.Create(MessageType.SaveNow, "host", payload));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] HostSaveSync broadcast: {ex.Message}"); }
            try
            {
                var slot = PerformLocalSave(session);
                SetSessionMetadata(session, slot.Day);
                MergeSlot(session, slot);
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] HostSaveSync save: {ex}"); }
        }

        /// <summary>The in-game pause-menu Save / Save-and-Exit button was pressed
        /// in an MP session — persist through the coordinated MP save instead of
        /// the single-player one.  MAIN THREAD (called from the MiniMenu patch).
        ///   Host   → save inline (HostSaveSync) so an immediate quit keeps it.
        ///   Client → ask the host to coordinate (SendRequestSave); the SaveNow
        ///            round-trip captures + uploads our save over the next frames.
        ///            When <paramref name="exiting"/>, also save + ship our own
        ///            copy synchronously as a best effort, because we won't be
        ///            here for the round-trip (the clean-leave case).</summary>
        public static void MenuSave(bool exiting, string saveName = "")
        {
            // The name the player typed in the pause-menu save box becomes the MP
            // session name (so "mp 1" makes a session "mp 1", and re-saving with the
            // same name overwrites it — normal named-save behaviour).  Empty ⇒ keep
            // the active session (or the default).
            string session = SanitizeSession(saveName);
            if (!string.IsNullOrEmpty(session)) ActiveSessionName = session;

            if (MPServer.IsRunning)
            {
                HostSaveSync(exiting ? "menu-exit" : "menu");
                return;
            }
            if (!MPClient.IsConnected) return;

            // Always let the host drive the canonical session (carry our chosen name
            // so the host saves under it).
            try { MPClient.SendRequestSave(exiting ? "client-menu-exit" : "client-menu", exiting, session); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] MenuSave request: {ex.Message}"); }

            if (!exiting) return;   // round-trip completes over the next frames

            // Exiting now: best-effort synchronous self-save + inline upload so our
            // progress is captured even though we won't be around for the SaveNow
            // round-trip.  On a shared machine the host reads this folder directly;
            // on separate machines the inline SendSaveData gives the socket a chance
            // to flush before QuitToDesktop's coroutine tears us down.
            lock (_lock) { session = _activeSessionName; }   // the named one we set, or the active session
            if (string.IsNullOrEmpty(session)) return;   // no session yet — the request above is all we can do
            try
            {
                var slot = PerformLocalSave(session);
                string folder = MPSaveManager.MpCharacterFolder(session, MPConfig.StableId);
                string? file = NewestHsg(folder);
                if (file != null)
                {
                    byte[] raw = File.ReadAllBytes(file);
                    if (raw.Length > 0)
                    {
                        slot.SaveName = Path.GetFileNameWithoutExtension(file);
                        MPClient.SendSaveData(session, slot, GzipBase64(raw), raw.Length);
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] MenuSave client ship: {ex.Message}"); }
        }

        // ── Client entry point ──────────────────────────────────────────────────

        /// <summary>Client: a SaveNow arrived.  Save locally on the main thread,
        /// then queue the resulting .hsg for upload to the host.</summary>
        public static void ClientHandleSaveNow(SaveNowPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.SessionName)) return;
            string session = payload.SessionName;
            lock (_lock) { _activeSessionName = session; }

            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var slot   = PerformLocalSave(session);
                    string dir = MPSaveManager.MpCharacterFolder(session, MPConfig.StableId);
                    lock (_pending)
                        _pending.Add(new PendingUpload { Session = session, Slot = slot, Folder = dir });
                    Plugin.Logger.LogInfo($"[MPSave] Queued .hsg upload for session '{session}'.");
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] Client save: {ex}"); }
            });
        }

        // ── Client: deferred upload once the save has finished writing ───────────

        private sealed class PendingUpload
        {
            public string Session = "";
            public MpSlot Slot = new();
            public string Folder = "";
            public int    WaitedFrames;
        }

        private static readonly List<PendingUpload> _pending = new();
        private const int UploadTimeoutFrames = 3600;   // ~60s @ 60fps

        /// <summary>Call every frame on the MAIN thread.  Ships any finished local
        /// save to the host (the save writer is threaded, so we wait for it).</summary>
        public static void TickPendingUploads()
        {
            List<PendingUpload> snapshot;
            lock (_pending)
            {
                if (_pending.Count == 0) return;
                snapshot = new List<PendingUpload>(_pending);
            }

            // SavingGameInProgress is an IL2CPP read — main thread only (this is).
            bool saving;
            try { saving = SaveGameManager.SavingGameInProgress; }
            catch { saving = false; }

            foreach (var up in snapshot)
            {
                up.WaitedFrames++;
                if (up.WaitedFrames > UploadTimeoutFrames)
                {
                    Plugin.Logger.LogWarning($"[MPSave] Upload for '{up.Session}' timed out — giving up.");
                    Remove(up);
                    continue;
                }
                if (saving) continue;   // serializer still running — try again next frame

                string? file = NewestHsg(up.Folder);
                if (file == null) continue;   // not on disk yet

                try
                {
                    byte[] raw = File.ReadAllBytes(file);
                    if (raw.Length == 0) continue;   // mid-write; retry next frame
                    up.Slot.SaveName = Path.GetFileNameWithoutExtension(file);
                    if (MPClient.IsConnected)
                        MPClient.SendSaveData(up.Session, up.Slot, GzipBase64(raw), raw.Length);
                    Plugin.Logger.LogInfo($"[MPSave] Uploaded '{up.Slot.SaveName}.hsg' ({raw.Length}B) for session '{up.Session}'.");
                    Remove(up);
                }
                catch (IOException) { /* file still locked — retry next frame */ }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] Upload read: {ex.Message}"); Remove(up); }
            }
        }

        private static void Remove(PendingUpload up)
        {
            lock (_pending) _pending.Remove(up);
        }

        // ── Host: incoming save data ─────────────────────────────────────────────

        /// <summary>Host: a client sent its saved game — write it into the host's
        /// session folder and fold the slot into the manifest.  Runs on the
        /// network (background) thread; file IO + manifest are pure C#, safe here.</summary>
        public static void HostHandleSaveData(SaveDataPayload data)
        {
            if (data == null || data.Slot == null) return;
            if (!data.Success || string.IsNullOrEmpty(data.HsgGzipBase64))
            {
                Plugin.Logger.LogWarning($"[MPSave] SaveData from '{data?.Slot?.DisplayName}' had no payload.");
                return;
            }

            DiagWrite($"HostHandleSaveData entry from '{data.Slot.DisplayName}' (stable={data.Slot.StableId})");
            try
            {
                byte[] raw = UnGzipBase64(data.HsgGzipBase64);
                if (data.RawLength > 0 && raw.Length != data.RawLength)
                    Plugin.Logger.LogWarning($"[MPSave] SaveData length mismatch: got {raw.Length}, expected {data.RawLength}.");

                string dir  = MPSaveManager.MpCharacterFolder(data.SessionName, data.Slot.StableId);
                string name = string.IsNullOrEmpty(data.Slot.SaveName) ? SaveFileName : data.Slot.SaveName;
                File.WriteAllBytes(Path.Combine(dir, name + ".hsg"), raw);
                Plugin.Logger.LogInfo($"[MPSave] Stored '{data.Slot.DisplayName}' .hsg ({raw.Length}B) → {dir}");
                DiagWrite($"HostHandleSaveData wrote .hsg ({raw.Length}B), merging slot");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] HostHandleSaveData write: {ex}"); return; }

            MergeSlot(data.SessionName, data.Slot);
            DiagWrite("HostHandleSaveData done");
        }

        // ── Load / reconnect (Phase 4 step 4) ────────────────────────────────────

        /// <summary>Host: (re)load an MP session — restore the ownership map, ship
        /// each connected client its stored .hsg, and load the host's own.  Safe
        /// from any thread.</summary>
        public static void HostLoadSession(string session)
        {
            if (!MPServer.IsRunning) return;
            var m = MPSaveManager.ReadManifest(session);
            if (m == null) { Plugin.Logger.LogWarning($"[MPSave] HostLoadSession: no manifest for '{session}'."); return; }

            lock (_lock) { _activeSessionName = session; _activeManifest = m; }

            MPServer.RestoreOwnershipFromManifest(m);     // cross-machine ownership + cash seed
            MPServer.SendLoadDataToEachClient(session, m); // each client gets its own .hsg

            float hostCash = BestCashFor(m, MPConfig.StableId);
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try { LoadOwnHsg(session, MPConfig.StableId); QueueCashApply(hostCash); }
                catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] Host load: {ex}"); }
            });
            Plugin.Logger.LogInfo($"[MPSave] HostLoadSession '{session}' — {m.Slots.Count} slot(s).");
        }

        /// <summary>Client: received its .hsg from the host — write it locally,
        /// load it, then overlay the host's restored cash.</summary>
        public static void ClientHandleLoadData(LoadDataPayload p)
        {
            if (p == null) return;
            // Mid-join fallback (empty .hsg): the host has no stored save for
            // us — start a fresh character with the host's settings.  (The
            // "load your own local copy" variant was REMOVED 2026-06-10: a
            // client-supplied save is an obvious edit/exploit vector; only
            // host-stored saves are trusted.)
            if (string.IsNullOrEmpty(p.HsgGzipBase64))
            {
                if (!string.IsNullOrEmpty(p.SessionName))
                    lock (_lock) { _activeSessionName = p.SessionName; }
                Plugin.Logger.LogInfo("[MPSave] Mid-join: no host-stored save — fresh character with host settings.");
                MPClient.StartFreshFromHost(p.FallbackSettings);
                return;
            }
            MPClient.MarkLeftLobby();   // loading now — the lobby pane yields
            MPClient.BeginJoinQuiesce();   // live stream must not touch the load
            string session = p.SessionName;
            lock (_lock) { _activeSessionName = session; }
            try
            {
                byte[] raw    = UnGzipBase64(p.HsgGzipBase64);
                string folder = MPSaveManager.MpCharacterFolder(session, MPConfig.StableId);
                File.WriteAllBytes(Path.Combine(folder, SaveFileName + ".hsg"), raw);
                Plugin.Logger.LogInfo($"[MPSave] Received .hsg ({raw.Length}B) for session '{session}' — loading.");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] ClientHandleLoadData write: {ex}"); return; }

            float money = p.Money;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    // Loading a save OVER A RUNNING WORLD permanently breaks
                    // GameManager.Update (endless NRE storm, no avatar —
                    // 2026-06-11).  In-game → detour via the main menu first;
                    // TickPendingLoad finishes the load once we're there.
                    if (Helpers.PlayerHelper.PlayerController != null)
                    {
                        Plugin.Logger.LogInfo("[MPSave] Mid-join while IN-GAME — detouring via main menu before loading.");
                        _pendingLoadSession = session;
                        _pendingLoadCash    = money;
                        UI.Load.LoadScene.LoadMainMenu();
                        return;
                    }
                    LoadOwnHsg(session, MPConfig.StableId);
                    QueueCashApply(money);
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] Client load: {ex}"); }
            });
        }

        // ── Mid-join menu detour (load may not run over a live world) ─────────
        private static string? _pendingLoadSession;
        private static float   _pendingLoadCash;
        private static GameVariablesDto? _pendingFreshSettings;
        private static bool    _pendingFresh;
        private static float   _pendingCheckAt;

        /// <summary>Fresh-character start deferred until the menu (same hazard).</summary>
        public static void DeferFreshStart(GameVariablesDto? settings)
        {
            _pendingFresh = true;
            _pendingFreshSettings = settings;
        }

        /// <summary>Main thread, every frame (any scene): completes a deferred
        /// mid-join load once the world is gone and the menu has settled.</summary>
        public static void TickPendingLoad()
        {
            if (_pendingLoadSession == null && !_pendingFresh) return;
            if (UnityEngine.Time.unscaledTime < _pendingCheckAt) return;
            _pendingCheckAt = UnityEngine.Time.unscaledTime + 0.5f;
            try
            {
                if (Helpers.PlayerHelper.PlayerController != null) return;   // world still up
                if (MPCanvasUI.IsLoadingOverlayUp()) return;                 // menu still loading
                if (_pendingLoadSession != null)
                {
                    var session = _pendingLoadSession; var cash = _pendingLoadCash;
                    _pendingLoadSession = null;
                    Plugin.Logger.LogInfo($"[MPSave] Menu reached — completing deferred mid-join load ('{session}').");
                    LoadOwnHsg(session, MPConfig.StableId);
                    QueueCashApply(cash);
                }
                else if (_pendingFresh)
                {
                    _pendingFresh = false;
                    Plugin.Logger.LogInfo("[MPSave] Menu reached — completing deferred fresh start.");
                    MPClient.StartFreshFromHost(_pendingFreshSettings);
                    _pendingFreshSettings = null;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[MPSave] TickPendingLoad: {ex}");
                _pendingLoadSession = null; _pendingFresh = false;
            }
        }

        /// <summary>While non-null, the game's CurrentVersionFolderPath is redirected
        /// here (via Patch_CurrentVersionFolderPath_MpRedirect) so the game's own Load
        /// reads from the MP session folder instead of the single-player folder.  Set
        /// ONLY around a SaveGameManager.Load call, on the main thread.</summary>
        public static volatile string? LoadRedirectFolder;

        /// <summary>MAIN THREAD: load this player's .hsg out of the MP session folder.
        /// The game's Load() locates saves by re-scanning CurrentVersionFolderPath();
        /// we briefly redirect that to the MP session folder so Load finds + loads our
        /// save natively — no staging, no single-player-folder pollution.</summary>
        private static void LoadOwnHsg(string session, string stableId)
        {
            string sessionFolder = MPSaveManager.MpSessionFolder(session);
            var saves = SaveGamePathHelper.GetAllSaveGamesFromVersion(sessionFolder);
            if (saves == null || saves.Count == 0)
            { Plugin.Logger.LogWarning($"[MPSave] LoadOwnHsg: no saves under {sessionFolder}."); return; }

            string want   = Path.GetFileName(MPSaveManager.MpCharacterFolder(session, stableId).TrimEnd('\\', '/'));
            var    chosen = saves[0];
            for (int i = 0; i < saves.Count; i++)
            {
                var s   = saves[i];
                string seg = Path.GetFileName((s?.CharacterPath ?? "").TrimEnd('\\', '/'));
                if (string.Equals(seg, want, StringComparison.OrdinalIgnoreCase)) { chosen = s; break; }
            }

            Plugin.Logger.LogInfo($"[MPSave] Loading .hsg: char='{chosen.characterId}' day={chosen.day} via redirect → {sessionFolder}");
            DiagWrite($"LoadOwnHsg: redirect ON → {sessionFolder}, calling Load");
            // Redirect the game's path resolver to the MP session folder for the
            // duration of the (synchronous) Load re-scan, then restore it.  Tightly
            // gated to this one main-thread call so nothing else sees the redirect.
            LoadRedirectFolder = sessionFolder;
            try   { SaveGameManager.Load(chosen, true); }
            finally { LoadRedirectFolder = null; }
            DiagWrite("LoadOwnHsg: redirect OFF, Load returned");
        }

        // Deferred cash overlay — applied a couple of seconds after the loaded
        // game goes live (so the load doesn't overwrite it).
        private static float _pendingCashApply;
        private static int   _cashApplyDwell;

        public static void QueueCashApply(float money) { if (money > 0f) { _pendingCashApply = money; _cashApplyDwell = 0; } }

        /// <summary>MAIN THREAD, per-frame while in an MP game: overlay the host's
        /// restored cash once the loaded game has settled.</summary>
        public static void TickCashApply()
        {
            if (_pendingCashApply <= 0f) return;
            if (_cashApplyDwell++ < 120) return;   // ~2s dwell past entering the game
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                gi.Money = _pendingCashApply;
                Plugin.Logger.LogInfo($"[MPSave] Applied restored cash ${_pendingCashApply:F0}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] TickCashApply: {ex.Message}"); }
            _pendingCashApply = 0f;
        }

        /// <summary>The freshest cash we know for a player: the live-streamed value
        /// if the host still has it this session, else the manifest slot's.</summary>
        internal static float BestCashFor(MpManifest m, string stableId)
        {
            if (MPServer.CashByStableId.TryGetValue(stableId, out var live) && live != 0f) return live;
            var slot = m.Slots.Find(s => s.StableId == stableId);
            return slot?.Money ?? 0f;
        }

        /// <summary>Read a stored .hsg from the host's session folder, gzipped +
        /// base64'd, for shipping to its owner.  Null if absent.</summary>
        internal static (string b64, int raw)? ReadSaveBytesGzip(string session, string stableId)
        {
            try
            {
                string folder = MPSaveManager.MpCharacterFolder(session, stableId);
                string? file = NewestHsg(folder);
                if (file == null) return null;
                byte[] raw = File.ReadAllBytes(file);
                if (raw.Length == 0) return null;
                return (GzipBase64(raw), raw.Length);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ReadSaveBytesGzip: {ex.Message}"); return null; }
        }

        // ── Core: local save (MAIN THREAD ONLY) ──────────────────────────────────

        // ── Crash diagnostics (Phase 4) ──────────────────────────────────────────
        // Writes step markers to a file that is flushed/closed on EVERY call, so the
        // last line survives a hard native crash (coreclr failfast).  Also installs a
        // first-chance exception logger (active only during the save window) to catch
        // any managed exception — with its full stack — right before a crash.
        // Per-process file (PID in the name) so the host's and client's traces don't
        // interleave in one file when both run on the same machine.
        private static readonly string DiagFile = $@"C:\dumps\savediag.{Environment.ProcessId}.txt";
        private static bool          _diagInstalled;
        private static volatile bool _diagActive;
        private static int           _diagFramesLeft;
        private static StreamWriter? _diagWriter;

        internal static void DiagWrite(string msg)
        {
            try
            {
                if (_diagWriter == null)
                {
                    var fs = new FileStream(DiagFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _diagWriter = new StreamWriter(fs) { AutoFlush = true };
                }
                _diagWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][t{Environment.CurrentManagedThreadId}] {msg}");
            }
            catch { }
        }

        /// <summary>Start (or extend) the diagnostic window — first-chance exception
        /// logging + per-frame heartbeat — for the post-save crash window.</summary>
        internal static void DiagArm(int frames = 360)   // ~6s @ 60fps
        {
            EnsureDiag();
            _diagFramesLeft = frames;
            _diagActive = true;
        }

        /// <summary>Per-frame heartbeat (main thread).  Writes a marker BEFORE each
        /// labelled phase so the last surviving line is the operation that faulted.</summary>
        internal static void DiagPhase(string phase)
        {
            if (_diagActive) DiagWrite("phase: " + phase);
        }

        /// <summary>Call once per frame from Update — counts down the diag window.</summary>
        internal static void DiagTick()
        {
            if (!_diagActive) return;
            if (--_diagFramesLeft <= 0) { _diagActive = false; DiagWrite("=== diag window closed ==="); }
        }

        private static string SafeSavingInProgress()
        {
            try { return SaveGameManager.SavingGameInProgress.ToString(); } catch (Exception ex) { return "err:" + ex.Message; }
        }

        private static void EnsureDiag()
        {
            if (_diagInstalled) return;
            _diagInstalled = true;
            try
            {
                AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
                {
                    if (!_diagActive) return;
                    try { DiagWrite($"FIRST-CHANCE {e.Exception.GetType().FullName}: {e.Exception.Message}\n{e.Exception.StackTrace}"); } catch { }
                };
            }
            catch { }
        }

        /// <summary>Saves THIS player's full game into the MP session folder via
        /// the game's own SaveGameManager.Save, and returns the slot describing
        /// it.  Must be called on the Unity main thread.</summary>
        public static MpSlot PerformLocalSave(string sessionName)
        {
            DiagArm();
            DiagWrite($"PerformLocalSave START session='{sessionName}' host={MPServer.IsRunning}");
            string charName = "";
            int    day      = 0;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi != null && gi.charactersData != null && gi.charactersData.Count > 0)
                    charName = gi.charactersData[0]?.name ?? "";
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] read char name: {ex.Message}"); }
            try { day = GameStateReader.GetGameTime().day; } catch { }

            // Per-player subfolder keyed by the STABLE id (not the game's
            // characterId) so load can find it deterministically by identity.
            string folder = MPSaveManager.MpCharacterFolder(sessionName, MPConfig.StableId);

            bool ok = false;
            DiagWrite($"about to call SaveGameManager.Save  SavingInProgress={SafeSavingInProgress()}");
            try { ok = SaveGameManager.Save(SaveGameManager.SaveType.Default, SaveFileName, folder); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] SaveGameManager.Save: {ex.Message}"); }
            DiagWrite($"returned from Save ok={ok}");

            // CRITICAL: the game serializes the GameInstance on a BACKGROUND thread.
            // If anything mutates the gi while that thread is reading it, the managed
            // heap corrupts and coreclr failfasts (the host save crash we hit — a
            // fatal 0xc0000005 detected right after serialization).  Block here until
            // serialization finishes so the gi is stable for its whole duration.  We
            // run on the main thread, so blocking it means NOTHING else touches the
            // gi during the save — at the cost of a brief, expected save stutter.
            DiagWrite("about to JoinSaveGameThreads");
            try { SaveGameManager.JoinSaveGameThreads(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] JoinSaveGameThreads: {ex.Message}"); }
            DiagWrite("returned from JoinSaveGameThreads");

            Plugin.Logger.LogInfo($"[MPSave] Local save '{sessionName}': ok={ok} char='{charName}' day={day} → {folder}");

            return new MpSlot
            {
                StableId      = MPConfig.StableId,
                DisplayName   = MPConfig.PlayerId,
                CharacterName = charName,
                CharacterId   = MPConfig.StableId,   // folder is keyed by stable id
                SaveName      = SaveFileName,
                IsHost        = MPServer.IsRunning,
                Day           = day,
            };
        }

        // ── Native autosave suppression ─────────────────────────────────────────

        /// <summary>While in an MP session, stop the game's built-in autosave from
        /// firing into the single-player folder — the host-coordinated save
        /// replaces it.  Idempotent; call from a per-frame tick on the main
        /// thread.</summary>
        public static void SuppressNativeAutosave()
        {
            try { GameManager.preventAutoSave = true; }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] SuppressNativeAutosave: {ex.Message}"); }
        }

        /// <summary>Re-enable the game's native autosave.  The suppress flag is
        /// STICKY (nothing in the game resets it mid-world) — required when a
        /// host-loss turns the MP world into an offline single-player fork, or
        /// the fork would silently never autosave.</summary>
        public static void AllowNativeAutosave()
        {
            try { GameManager.preventAutoSave = false; }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] AllowNativeAutosave: {ex.Message}"); }
        }

        /// <summary>Coordinated-autosave interval.  Uses the host's control if set
        /// (MPConfig.AutosaveMinutes); otherwise mirrors the player's SP "minutes
        /// between autosaves" setting.  Clamped to a 60s floor.</summary>
        public static float AutosaveIntervalSeconds()
        {
            int minutes = MPConfig.AutosaveMinutesLive();   // host control; 0 = mirror SP
            if (minutes <= 0)
            {
                try { int m = PlayerPrefSettings.MinutesBetweenAutoSaves; if (m > 0) minutes = m; }
                catch { }
            }
            if (minutes <= 0) minutes = 5;
            float secs = minutes * 60f;
            return secs < 60f ? 60f : secs;
        }

        // ── Manifest assembly (thread-safe, pure C#) ─────────────────────────────

        private static MpManifest EnsureManifest(string sessionName)
        {
            // caller holds _lock
            if (_activeManifest == null || _activeSessionName != sessionName)
            {
                _activeSessionName = sessionName;
                _activeManifest = MPSaveManager.ReadManifest(sessionName) ?? new MpManifest
                {
                    SessionId   = Guid.NewGuid().ToString("N"),
                    GameVersion = SafeGameVersion(),
                };
            }
            return _activeManifest;
        }

        /// <summary>Host-only: set the session-wide metadata (ownership map, world
        /// day, timestamp) and write.</summary>
        private static void SetSessionMetadata(string sessionName, int worldDay)
        {
            lock (_lock)
            {
                var m = EnsureManifest(sessionName);
                m.GameVersion    = SafeGameVersion();
                m.WorldDay       = worldDay;
                m.SavedAtUnix    = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                m.BuildingOwners = BuildOwnersStableKeyed();
                RefreshSlotCash(m);
                MPSaveManager.WriteManifest(sessionName, m);
            }
        }

        private static void MergeSlot(string sessionName, MpSlot slot)
        {
            lock (_lock)
            {
                var m = EnsureManifest(sessionName);
                int idx = m.Slots.FindIndex(s => s.StableId == slot.StableId);
                if (idx >= 0) m.Slots[idx] = slot; else m.Slots.Add(slot);
                RefreshSlotCash(m);
                MPSaveManager.WriteManifest(sessionName, m);
            }
        }

        /// <summary>Stamp each slot with the host's most-current known cash for
        /// that player (live-streamed), so even a slot whose .hsg is stale (e.g. a
        /// player who dropped) carries near-current money to restore on reconnect.</summary>
        private static void RefreshSlotCash(MpManifest m)
        {
            try
            {
                foreach (var s in m.Slots)
                    if (MPServer.CashByStableId.TryGetValue(s.StableId, out var c))
                        s.Money = c;
            }
            catch { }
        }

        /// <summary>Re-key MPServer.BuildingOwners (keyed by the live, mutable
        /// PlayerId or the literal "host") to immutable stable ids.</summary>
        private static Dictionary<string, string> BuildOwnersStableKeyed()
        {
            var result = new Dictionary<string, string>();
            try
            {
                foreach (var kv in MPServer.BuildingOwners)
                {
                    string owner = kv.Value;
                    if (string.IsNullOrEmpty(owner)) continue;          // vacated
                    string stable;
                    if (owner == "host")
                        stable = MPConfig.StableId;                      // host runs this code
                    else if (!MPServer.StableIdByPlayer.TryGetValue(owner, out stable) || string.IsNullOrEmpty(stable))
                        stable = owner;                                  // fallback: never learned a stable id
                    result[kv.Key] = stable;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] BuildOwnersStableKeyed: {ex.Message}"); }
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string? NewestHsg(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                string? best = null; DateTime bestTime = DateTime.MinValue;
                foreach (var f in Directory.GetFiles(folder, "*.hsg"))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t >= bestTime) { bestTime = t; best = f; }
                }
                return best;
            }
            catch { return null; }
        }

        private static string GzipBase64(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                gz.Write(raw, 0, raw.Length);
            return Convert.ToBase64String(ms.ToArray());
        }

        private static byte[] UnGzipBase64(string b64)
        {
            byte[] comp = Convert.FromBase64String(b64);
            using var ins  = new MemoryStream(comp);
            using var gz   = new GZipStream(ins, CompressionMode.Decompress);
            using var outs = new MemoryStream();
            gz.CopyTo(outs);
            return outs.ToArray();
        }

        private static string SafeGameVersion()
        {
            // Use the cached version name (no IL2CPP) — this runs from MergeSlot,
            // which the host calls on the network poll thread too.
            try { return MPSaveManager.GameVersionName(); }
            catch { return ""; }
        }

        private static string DefaultSessionName()
            => "MP " + DateTime.Now.ToString("yyyy-MM-dd HHmm");

        /// <summary>Turn a user-typed save name into a safe session folder name.
        /// Empty/whitespace ⇒ "" (caller keeps the active/default session).</summary>
        internal static string SanitizeSession(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            name = name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }
    }
}
