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
        // Identity of the WORLD being played (native parity 2026-07-07: what the character folder is
        // to vanilla). Minted at a new world's first save, adopted from the manifest on load, and kept
        // across save-name changes — a rename stamps the SAME id into the new name's manifest, so the
        // picker groups every name of one world under one card. Cleared only with the session itself.
        private static string           _activePlaythroughId = "";

        /// <summary>The MP save session currently in use.  Set when a save fires
        /// or when an existing session is loaded (Phase 4 step 4), so repeated
        /// saves overwrite the same session rather than spawning new folders.</summary>
        public static string ActiveSessionName
        {
            get => _activeSessionName;
            set
            {
                lock (_lock)
                {
                    _activeSessionName = value ?? "";
                    _activeManifest = null;
                    // Reset (new lobby) drops the world identity; a RENAME (non-empty) keeps it —
                    // saving under a new name is still the same world.
                    if (string.IsNullOrEmpty(_activeSessionName)) { _activePlaythroughId = ""; PortraitFolder = null; }
                }
            }
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

            // AUTOMATIC saves write SIBLING sessions so they never overwrite the player's
            // MANUAL save (the base).  Only genuinely user-initiated saves (pause-menu /
            // quicksave, arriving as "menu"/"menu-exit"/"client-menu"/…) land on the base.
            // (user, 2026-06-12; disconnect split out 2026-06-23.)
            // Native-parity model (user, 2026-07-07 — "otherwise match native"):
            //   autosave/join → a ROTATION of MaxAutoSavesPerGame slots ('-auto', '-auto-2', …),
            //                   oldest overwritten — mirrors vanilla's "Recover #N" cycle and
            //                   honors the player's Options setting (default 3).  A JOIN is not
            //                   the user saving (round-37: it wrote the manual base for weeks —
            //                   the "my save advanced without me saving" leak), so it rides the
            //                   same rotation.
            //   midnight      → '-recover', one fixed slot — mirrors vanilla's "Recover Midnight".
            //   disconnect    → '-disconnect', a roster checkpoint carrying the member who just
            //                   left (see carry-forward below) — MP-specific, no native analog.
            // _activeSessionName stays on the manual base; suffixes never stack. Always derive
            // from the CLEAN base (strip any drifted sibling suffix) so names never compound
            // ('-auto-disconnect') and the lineage resolves together in carry-forward + load.
            string cleanBase = StripAutoSuffix(session);
            string autoSuffix = reason == "disconnect" ? "-disconnect"
                              : reason == "midnight"   ? "-recover"
                              : (reason == "autosave" || reason == "join") ? NextAutoSlotSuffix(cleanBase)
                              : "";
            bool isAutomatic = autoSuffix.Length > 0;
            session = cleanBase + autoSuffix;

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
                    // Carry forward anyone who isn't here to save themselves (a member who just left, or
                    // was offline) so a save never silently drops them → a load that would otherwise
                    // fresh-start them as a brand-new player. (2026-06-23; field 2026-07-19: the
                    // isAutomatic gate meant MANUAL saves — including save-as under a NEW NAME —
                    // skipped this entirely, and the host's next load of that store reset the
                    // offline member. Every save carries absent members now.)
                    CarryForwardAbsentMembers(session);
                    // Loan ledger rides every session save — loans created
                    // BEFORE the session's first save (no folder yet) would
                    // otherwise never persist unless the ledger changed again.
                    MPHub.SaveLedger();
                    DiagPhase("host lambda: DONE");
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] Host save: {ex}"); }
            });
        }

        /// <summary>Round-37: the window-close self-save — writes the DISCONNECT variant, never the manual
        /// base. The old direct PerformLocalSave(activeSession) was the second "Main advanced without me
        /// saving" leak (alongside join-saves). Runs the same slot+metadata trio HostSaveNow uses so the
        /// -disconnect variant stays loadable (HostLoadSession requires a manifest). MAIN THREAD ONLY.</summary>
        public static void HostQuitCheckpoint()
        {
            string session;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_activeSessionName)) return;
                session = _activeSessionName;
            }
            string dc = MPSaveManager.StripToBase(session) + "-disconnect";
            Plugin.Logger.LogInfo($"[MPSave] HostQuitCheckpoint → '{dc}' (manual base untouched).");
            try
            {
                var slot = PerformLocalSave(dc);
                SetSessionMetadata(dc, slot.Day);
                MergeSlot(dc, slot);
                // Same carry-forward as every other save path (review 2026-07-20):
                // a '-disconnect' store missing offline members is the same
                // character-reset trap if the host later loads it.
                CarryForwardAbsentMembers(dc);
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] HostQuitCheckpoint: {ex}"); }
        }

        // ── Immutable checkpoints ('-cp-'/'-cpa-' timestamped copies) — RETIRED 2026-07-07 ───────
        // Round-37 froze a timestamped copy of the session folder after EVERY save event. That was an
        // over-delivery on "each listed save must carry what was true at save time": the requirement is
        // SLOT INTEGRITY (each slot written atomically, only ever overwritten by its own kind of event),
        // which the suffix separation + fork-on-load already guarantee. Native's answer to "keep this
        // moment" is a new save name — same name overwrites — so additive version history diverged from
        // the intended native parity (user, 2026-07-07). Autosave rollback depth is now covered by the
        // native-style '-auto' slot rotation (NextAutoSlotSuffix). Existing '-cp-'/'-cpa-' folders on
        // disk stay listed/loadable via the picker's legacy classification; no new ones are created.

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
                // Field 2026-07-19 (the unintended character reset): this MANUAL path —
                // which is exactly where a save-as under a new name lands — never carried
                // absent members, so the new store was born without the offline player's
                // .hsg and the host's next load fresh-started them.
                CarryForwardAbsentMembers(session);
                // Loan ledger rides every session save, exactly as HostSaveNow does.
                // The pause-menu save is often the session's FIRST save, so a loan
                // accepted beforehand would otherwise never reach disk (the ledger
                // path needs the session folder, which PerformLocalSave just created).
                MPHub.SaveLedger();
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
            string session = payload.SessionName;   // where THIS save goes (may be "<base>-auto")
            // Keep the client's durable session pointer on the manual BASE, mirroring
            // the host (HostSaveNow leaves _activeSessionName on the base and only
            // suffixes the per-save copy).  If an automatic sibling name ("-auto",
            // "-disconnect", "-recover") stuck here, a later client Save-and-Exit with
            // an empty name would ship its final .hsg into that sibling while the host
            // coordinates the base session, so a base-session resume would load the
            // client's stale save.  Strip EVERY auto-suffix (not just "-auto"): the
            // coordinated "-recover" checkpoint and the "-disconnect" checkpoint now
            // reach clients too, and both must leave the pointer on the base.
            string canonical = StripAutoSuffix(session);
            lock (_lock) { _activeSessionName = canonical; }

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
                // Slot.SaveName is client-supplied — sanitize it like every other
                // path component or it can step outside the session folder.
                string name = MPSaveManager.Sanitize(string.IsNullOrEmpty(data.Slot.SaveName) ? SaveFileName : data.Slot.SaveName);
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

            // Round-37 FORK SEMANTICS: load FROM the selected variant/checkpoint folder, but CONTINUE the
            // playthrough on its lineage BASE — ongoing saves go to Main/-auto/checkpoints as usual and can
            // never mutate the loaded (frozen) source. Loading is a jump to a recorded moment; the recorded
            // moment stays recorded.
            string lineage = MPSaveManager.StripToBase(session);
            // Adopt the loaded world's identity (empty on pre-field saves — the first save then
            // mints one via EnsureManifest and the lineage keeps grouping by name until stamped).
            lock (_lock) { _activeSessionName = lineage; _activeManifest = m; _activePlaythroughId = m.PlaythroughId ?? ""; }

            MPServer.RestoreOwnershipFromManifest(m);              // cross-machine ownership + cash seed
            MPServer.SendLoadDataToEachClient(session, m, lineage); // each client gets its own .hsg FROM the source, tagged with the lineage

            float hostCash = BestCashFor(m, MPConfig.StableId);
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try { LoadOwnHsg(session, MPConfig.StableId); QueueCashApply(hostCash); }
                catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] Host load: {ex}"); }
            });
            Plugin.Logger.LogInfo($"[MPSave] HostLoadSession '{session}' → continuing lineage '{lineage}' — {m.Slots.Count} slot(s).");
        }

        /// <summary>Client: received its .hsg from the host — write it locally,
        /// load it, then overlay the host's restored cash.</summary>
        public static void ClientHandleLoadData(LoadDataPayload p)
        {
            if (p == null) return;
            // Proposal 2 (2026-06-17): host says our saved character exists but its .hsg can't be read right
            // now — do NOT fresh-start (that abandons the real save). Leave cleanly so the player can reconnect
            // to retry, or the host can recover the file. Checked BEFORE the empty-hsg fresh path below.
            if (p.SaveUnavailable)
            {
                Plugin.Logger.LogError("[MPSave] Host reports our save is temporarily unavailable — aborting join WITHOUT fresh-starting (your character is not lost).");
                // Important + rare: gets a LONG toast (chat is player-only now).
                try { GameStatePatcher.EnqueueOnMainThread(() => PassengerHud.Toast("Your save couldn't be loaded right now — your character is safe. Try reconnecting, or ask the host to check the session save.", 8f)); } catch { }
                MPClient.Disconnect();
                return;
            }
            // Phase 3: the host wants our pending disconnect save before deciding what to load. Upload it
            // (the host validates its ACTUAL in-game day) and WAIT for the follow-up LoadData — do NOT load
            // anything now.
            if (p.AwaitClientDisconnectUpload)
            {
                if (!string.IsNullOrEmpty(p.SessionName)) lock (_lock) { _activeSessionName = p.SessionName; }
                Plugin.Logger.LogInfo($"[MPSave] Host requested our disconnect save for '{p.SessionName}' — uploading, awaiting load.");
                GameStatePatcher.EnqueueOnMainThread(() => UploadClientDisconnectSave(p.SessionName));
                return;
            }
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
                ClearClientDisconnectMarker();   // host resolved our join — pending disconnect offer consumed
                MPClient.StartFreshFromHost(p.FallbackSettings);
                return;
            }
            MPClient.MarkLeftLobby();   // loading now — the lobby pane yields
            MPClient.SendPhaseReport("Loading");   // INTENT: don't excuse me from the fence
            MPClient.BeginJoinQuiesce();   // live stream must not touch the load
            string session = p.SessionName;
            lock (_lock) { _activeSessionName = session; }
            try
            {
                byte[] raw    = UnGzipBase64(p.HsgGzipBase64);
                string folder = MPSaveManager.MpCharacterFolder(session, MPConfig.StableId);
                File.WriteAllBytes(Path.Combine(folder, SaveFileName + ".hsg"), raw);
                Plugin.Logger.LogInfo($"[MPSave] Received .hsg ({raw.Length}B) for session '{session}' — loading.");
                ClearClientDisconnectMarker();   // host resolved our join (incl. any validated disconnect save) — offer consumed
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
                        UI.Load.LoadScene.LoadMainMenu(BAModAPI.ModActivationScope.City);
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

        /// <summary>The MP character folder holding this player's portrait jpg —
        /// the game's own Save regenerates "&lt;SaveGameName&gt; portrait.jpg" next
        /// to the .hsg (SaveGameManager.Save :202) because PerformLocalSave passes
        /// an explicit characterFolder.  Consulted by
        /// Patch_PortraitGenerator_GetCharacterPortraitPath_MpFolder so in-game
        /// portrait READS (LoadPlayerPortrait: Rivals self row + topbar; our
        /// ReadLocalPortraitBase64 relay) resolve here instead of the native
        /// version folder, which never receives a write in MP.  Set on MP load
        /// (LoadOwnHsg) and after each successful local save (freshest rotation
        /// folder); cleared with the session.  Null → native path passthrough.</summary>
        public static volatile string? PortraitFolder;

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
            PortraitFolder = MPSaveManager.MpCharacterFolder(session, stableId);
            DiagWrite("LoadOwnHsg: redirect OFF, Load returned");
        }

        // Deferred cash overlay — applied a couple of seconds after the loaded
        // game goes live (so the load doesn't overwrite it).
        private static float _pendingCashApply;
        private static bool  _hasPendingCash;   // explicit sentinel — a legitimate $0 / negative (overdraft) is a real authoritative balance, not "nothing pending"
        private static int   _cashApplyDwell;

        public static void QueueCashApply(float money) { _pendingCashApply = money; _hasPendingCash = true; _cashApplyDwell = 0; }

        /// <summary>MAIN THREAD, per-frame while in an MP game: overlay the host's
        /// restored cash once the loaded game has settled.</summary>
        public static void TickCashApply()
        {
            if (!_hasPendingCash) return;
            if (_cashApplyDwell++ < 120) return;   // ~2s dwell past entering the game
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return;
                gi.Money = _pendingCashApply;   // apply verbatim — $0 and overdraft are legitimate authoritative balances
                Plugin.Logger.LogInfo($"[MPSave] Applied restored cash ${_pendingCashApply:F0}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] TickCashApply: {ex.Message}"); }
            _hasPendingCash   = false;
            _pendingCashApply = 0f;
        }

        /// <summary>The freshest cash we know for a player: the live-streamed value
        /// if the host still has it this session, else the manifest slot's.</summary>
        internal static float BestCashFor(MpManifest m, string stableId)
        {
            if (MPServer.CashByStableId.TryGetValue(stableId, out var live)) return live;   // a live figure (incl. a genuine $0) wins; only fall back to the slot when we have NO live cash at all
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
        // DIAG:DEVTOOL — save/exception tracing → C:\dumps (#if BAMP_DEV only). See docs/DIAGNOSTICS.md.
        private static readonly string DiagFile = $@"C:\dumps\savediag.{System.Diagnostics.Process.GetCurrentProcess().Id}.txt";
        private static bool          _diagInstalled;
        private static volatile bool _diagActive;
        private static int           _diagFramesLeft;
        private static StreamWriter? _diagWriter;

        internal static void DiagWrite(string msg)
        {
#if BAMP_DEV
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
#endif
        }

        /// <summary>Start (or extend) the diagnostic window — first-chance exception
        /// logging + per-frame heartbeat — for the post-save crash window.</summary>
        internal static void DiagArm(int frames = 360)   // ~6s @ 60fps
        {
#if BAMP_DEV
            EnsureDiag();
            _diagFramesLeft = frames;
            _diagActive = true;
#endif
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
        public static MpSlot PerformLocalSave(string sessionName, SaveGameManager.SaveType saveType = SaveGameManager.SaveType.Default)
        {
            DiagArm();
            DiagWrite($"PerformLocalSave START session='{sessionName}' host={MPServer.IsRunning}");
            // Ghost vehicles leak into gi.VehicleInstances via the ghost-spawn
            // registration and snowball one duplicate per save/load cycle
            // (run-17: extra carts/flatbeds frozen at old cargo states).  The
            // save boundary is the reliable choke point — strip them here for
            // EVERY save path (host, client, sync menu variant).
            GameStatePatcher.StripGhostVehicles("save");
            // Same choke point: the rivals UI auto-creates RivalState history
            // entries for our synthetic PLAYER rows — strip before they
            // serialize and accumulate.
            GameStatePatcher.StripSyntheticRivalStates("save");
            // Same choke point (anti-pattern Class 5): synthetic register cashiers (BAMP_DUTY_*) + their injected
            // WorkShifts are MP-only runtime objects — strip them so they can't leak into a single-player load
            // (where the world-ready cleanup never runs), then RESTORE the exact objects after serialization
            // completes (below) so live MP gameplay is undisturbed.
            var restoreSynthetics = MPRegisterSync.StripSyntheticsForSave("save");
            // Merger slice 3, same choke point: the ownership FLIP is MP-only presentation — a save
            // must never claim a partner's business as this player's tenancy (two-owners class).
            // VeilPush reverts every flipped reg to native truth for the whole serialization;
            // the finally below re-flips (VeilPop) even if the save throws.
            MergerFlip.VeilPush();
            string charName = "";
            int    day      = 0;
            // Per-player subfolder keyed by the STABLE id (not the game's characterId) so load can find it
            // deterministically by identity.  charName/day/folder/ok are declared BEFORE the try so the log +
            // the returned slot below can still read them — and so the finally that restores the synthetics
            // ALWAYS runs even if the save work throws (a failed save must never leave the session un-staffed).
            string folder   = MPSaveManager.MpCharacterFolder(sessionName, MPConfig.StableId);
            bool   ok        = false;
            try
            {
                try
                {
                    var gi = SaveGameManager.Current;
                    if (gi != null && gi.charactersData != null && gi.charactersData.Count > 0)
                        charName = gi.charactersData[0]?.name ?? "";
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] read char name: {ex.Message}"); }
                try { day = GameStateReader.GetGameTime().day; } catch { }

                DiagWrite($"about to call SaveGameManager.Save  SavingInProgress={SafeSavingInProgress()}");
                // RETRY: the game serializes through a FIXED temp file
                // (%TEMP%\Hovgaard Games\Big Ambitions\tempUncompressedSave) shared
                // by BOTH local instances — coordinated saves fire on host+client
                // simultaneously and collide ("being used by another process",
                // client slot then missing from the manifest → session load never
                // reached that client).  The launch bat now gives instance 2 its
                // own %TEMP%; this retry covers any remaining collision.
                for (int attempt = 0; attempt < 3 && !ok; attempt++)
                {
                    if (attempt > 0)
                    {
                        Plugin.Logger.LogWarning($"[MPSave] save attempt {attempt} failed — retrying in 1.2s.");
                        try { System.Threading.Thread.Sleep(1200); } catch { }
                    }
                    try { ok = SaveGameManager.Save(saveType, SaveFileName, folder); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] SaveGameManager.Save: {ex.Message}"); }
                }
                DiagWrite($"returned from Save ok={ok}");
                // Save regenerates the portrait jpg into this folder (async, lands
                // ~a frame after Save returns) — repoint portrait reads at the
                // freshest rotation folder.
                if (ok) PortraitFolder = folder;

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
            }
            finally
            {
                // ALWAYS re-add the synthetic cashiers we stripped above — even if the save threw — so a failure
                // can't leave the live session with un-staffed registers.  JoinSaveGameThreads (inside the try)
                // has returned in every normal/caught path by here, so serialization is done and gi is safe.
                try { restoreSynthetics(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] restore synthetics: {ex.Message}"); }
                try { MergerFlip.VeilPop(); } catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] restore merger flip: {ex.Message}"); }
            }

            Plugin.Logger.LogInfo($"[MPSave] Local save '{sessionName}': ok={ok} char='{charName}' day={day} → {folder}");
            // 4a diagnostic: a failed save is the upstream cause of most "lost progress" reports — make it
            // LOUD so it stands out in a submitted log (the routine line above is INFO).
            if (!ok) Plugin.Logger.LogError($"[MPSave] SAVE FAILED for '{sessionName}' (char='{charName}', day={day}) — .hsg not written; a later load may fall back to an older copy.");

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

        /// <summary>Phase 3 tamper tolerance: a disconnect save may be at most this many in-game days past
        /// the host's CURRENT world day before it's rejected as edited (a small window absorbs a legit
        /// midnight crossing in the un-synced final minutes without false-positives).</summary>
        public const int DisconnectDayWindow = 2;

        /// <summary>Strip a trailing automatic-save suffix ('-auto' / '-auto-N' / '-disconnect' /
        /// '-recover') to get the base session name shared by a session and its automatic siblings.</summary>
        public static string StripAutoSuffix(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.EndsWith("-disconnect")) return s.Substring(0, s.Length - "-disconnect".Length);
            if (s.EndsWith("-auto"))       return s.Substring(0, s.Length - "-auto".Length);
            if (s.EndsWith("-recover"))    return s.Substring(0, s.Length - "-recover".Length);   // coordinated midnight checkpoint sibling
            int i = NumberedAutoIndex(s);
            if (i > 0) return s.Substring(0, i);   // '-auto-2'.. rotation slots (native-parity, 2026-07-07)
            return s;
        }

        /// <summary>Index of a trailing '-auto-&lt;digits&gt;' rotation suffix in <paramref name="s"/>,
        /// or -1. All-digit tail required so a base name containing '-auto-' text is never mangled.</summary>
        private static int NumberedAutoIndex(string s)
        {
            int i = s.LastIndexOf("-auto-", StringComparison.Ordinal);
            if (i <= 0 || i + 6 >= s.Length) return -1;
            for (int k = i + 6; k < s.Length; k++)
                if (s[k] < '0' || s[k] > '9') return -1;
            return i;
        }

        // ── Native-parity autosave rotation (2026-07-07) ─────────────────────────
        // Vanilla cycles "Recover #0..N-1" with N = the player's MaxAutoSavesPerGame Options setting
        // (default 3). Ours rotates sibling sessions '-auto', '-auto-2', … '-auto-N': first empty slot,
        // else the OLDEST (by manifest timestamp) is overwritten. Slot 1 keeps the legacy plain '-auto'
        // name so pre-rotation folders fold into the cycle instead of orphaning.

        private static int _autosaveSlotsCached = 3;

        /// <summary>Autosave rotation depth — mirrors the native MaxAutoSavesPerGame setting. The
        /// IL2CPP prefs read only succeeds on the main thread; off-main callers (join/disconnect
        /// handlers) get the last main-thread value (the autosave tick refreshes it every cycle).</summary>
        public static int AutosaveSlotCount()
        {
            try { int m = PlayerPrefSettings.MaxAutoSavesPerGame; if (m >= 1) _autosaveSlotsCached = Math.Min(m, 10); }
            catch { }
            return _autosaveSlotsCached;
        }

        /// <summary>Pick the rotation slot the next automatic save writes to. Pure file/JSON IO —
        /// thread-safe.</summary>
        internal static string NextAutoSlotSuffix(string baseName)
        {
            int slots = AutosaveSlotCount();
            string bestSuf = "-auto"; long bestWhen = long.MaxValue;
            for (int i = 1; i <= slots; i++)
            {
                string suf = i == 1 ? "-auto" : "-auto-" + i;
                try
                {
                    string dir = MPSaveManager.MpSessionFolder(baseName + suf);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return suf;   // first empty slot
                    long when = MPSaveManager.ReadManifest(baseName + suf)?.SavedAtUnix ?? 0;
                    if (when <= 0) when = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir)).ToUnixTimeSeconds();
                    if (when < bestWhen) { bestWhen = when; bestSuf = suf; }
                }
                catch { }
            }
            return bestSuf;   // all slots taken → overwrite the oldest
        }

        /// <summary>HOST: make an automatic checkpoint (autosave / disconnect) a COMPLETE roster snapshot.
        /// For every player who has a save in this session lineage (base / -auto / -disconnect) but is NOT
        /// currently connected (so they won't upload fresh this round — e.g. the member who just left), copy
        /// their NEWEST save into <paramref name="targetSession"/> and merge their slot. Each member resumes
        /// their OWN latest within-session save — manifest-reconciled, no cross-session desync. Connected
        /// members are skipped (they save themselves fresh; skipping also avoids racing their incoming
        /// upload). Pure file/JSON IO — safe off the main thread.</summary>
        public static void CarryForwardAbsentMembers(string targetSession)
        {
            try
            {
                if (string.IsNullOrEmpty(targetSession)) return;
                var connected = MPServer.ConnectedStableIds();
                string baseName = StripAutoSuffix(targetSession);
                // Base + every automatic sibling. Rotation slots swept to a fixed 10 (the setting's cap)
                // rather than the live slot count — a lowered setting must not hide members whose newest
                // save sits in a now-out-of-range slot. Missing folders are skipped below anyway.
                var lineage = new List<string> { baseName, baseName + "-auto" };
                for (int slot = 2; slot <= 10; slot++) lineage.Add(baseName + "-auto-" + slot);
                lineage.Add(baseName + "-disconnect");
                // Cross-BASE lineage (field 2026-07-19): a save-as under a NEW NAME starts a
                // different base, so the same-base sweep above can't see the old chain — the
                // offline member's newest save lived in 'MP <date>-auto-2' while the target
                // was 'Kaido_melaus game'. PlaythroughId is the world identity carried across
                // every rename/fork: sweep every session of the SAME world too. (Manifest-less
                // self-save folders are still covered by the same-base list above.)
                try
                {
                    string pid = MPSaveManager.ReadManifest(targetSession)?.PlaythroughId
                              ?? MPSaveManager.ReadManifest(baseName)?.PlaythroughId ?? "";
                    if (!string.IsNullOrEmpty(pid))
                        foreach (var (name, m) in MPSaveManager.ListSessions())
                            if (m != null && m.PlaythroughId == pid && !lineage.Contains(name))
                                lineage.Add(name);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] carry-forward lineage scan: {ex.Message}"); }

                // Newest save per member across the lineage. Scan the character FOLDERS (not just manifest
                // slots) so a manifest-less self-save (OnApplicationQuit) is still found; remember which
                // session it came from so we can pull that session's slot (day/money) for the member.
                var newest = new Dictionary<string, (string srcSession, string srcDir, DateTime when)>();
                foreach (var s in lineage)
                {
                    string sessionFolder = MPSaveManager.MpSessionFolder(s);
                    if (string.IsNullOrEmpty(sessionFolder) || !Directory.Exists(sessionFolder)) continue;
                    foreach (var dir in Directory.GetDirectories(sessionFolder))
                    {
                        string stable = Path.GetFileName(dir);
                        if (!stable.StartsWith("guid-") && !stable.StartsWith("steam-")) continue;   // character folders only — steam-<id> is the normal id (MPConfig:379), guid- only the fallback; was excluding every Steam player
                        string? hsg = NewestHsg(dir);
                        if (hsg == null) continue;
                        DateTime when;
                        try { when = File.GetLastWriteTimeUtc(hsg); } catch { continue; }
                        if (!newest.TryGetValue(stable, out var cur) || when > cur.when)
                            newest[stable] = (s, dir, when);
                    }
                }

                foreach (var kv in newest)
                {
                    string stable = kv.Key;
                    if (connected.Contains(stable)) continue;   // saves itself fresh — don't touch (no race)
                    var (srcSession, srcDir, _) = kv.Value;
                    if (srcSession == targetSession) continue;   // already its own newest
                    // Already captured in the target this round? (check WITHOUT creating the dir)
                    string targetMemberDir = Path.Combine(MPSaveManager.MpSessionFolder(targetSession), stable);
                    if (Directory.Exists(targetMemberDir) && NewestHsg(targetMemberDir) != null) continue;
                    try
                    {
                        string dstDir = MPSaveManager.MpCharacterFolder(targetSession, stable);
                        foreach (var f in Directory.GetFiles(srcDir))
                            File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), overwrite: true);
                        var slot = MPSaveManager.ReadManifest(srcSession)?.Slots?.Find(x => x.StableId == stable);
                        if (slot != null) MergeSlot(targetSession, slot);
                        Plugin.Logger.LogInfo($"[MPSave] Carried forward absent member (stable={stable}) from '{srcSession}' → '{targetSession}'.");
                    }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] Carry-forward '{stable}': {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] CarryForwardAbsentMembers '{targetSession}': {ex.Message}"); }
        }

        // ── Phase 2: client-side disconnect save (the designated trusted-newer file) ──────────────
        // On a clean close or an in-game host-loss, the CLIENT snapshots its CURRENT game into its own
        // '<base>-disconnect' session + drops a marker. This is the ONLY client file Phase 3 will accept as
        // "newer than the host's record" on rejoin (never hard/auto saves), and only if it passes a
        // day-consistency tamper check. On separate machines it's the only way the client's final
        // pre-disconnect minutes (never uploaded) survive.
        [Serializable]
        public class ClientDisconnectMarker
        {
            public string SessionBase = "";
            public string StableId    = "";
            public int    Day;
            public long   SavedAtUnix;
        }

        private static string ClientDisconnectMarkerPath()
            => Path.Combine(MPSaveManager.MpVersionFolder(), "clientDisconnect.json");

        /// <summary>CLIENT: snapshot the current game into '&lt;base&gt;-disconnect' + write the marker.
        /// Called on a clean close and on an in-game host-loss. MAIN THREAD (PerformLocalSave touches IL2CPP).</summary>
        public static void WriteClientDisconnectSave()
        {
            if (!MPClient.IsConnected && !MPClient.SessionEnded) return;   // client-side only
            try
            {
                string baseName = StripAutoSuffix(ActiveSessionName);
                if (string.IsNullOrEmpty(baseName)) return;
                var slot = PerformLocalSave(baseName + "-disconnect");   // current game → <base>-disconnect/<ourStable>/
                long nowUnix = 0;
                try { nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } catch { }
                var marker = new ClientDisconnectMarker
                {
                    SessionBase = baseName,
                    StableId    = MPConfig.StableId,
                    Day         = slot.Day,
                    SavedAtUnix = nowUnix,
                };
                File.WriteAllText(ClientDisconnectMarkerPath(),
                    Newtonsoft.Json.JsonConvert.SerializeObject(marker, Newtonsoft.Json.Formatting.Indented));
                Plugin.Logger.LogInfo($"[MPSave] Client disconnect save written: '{baseName}-disconnect' day={slot.Day}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] WriteClientDisconnectSave: {ex.Message}"); }
        }

        /// <summary>CLIENT: the pending disconnect-save marker, if any (Phase 3 offers it on rejoin).</summary>
        public static ClientDisconnectMarker? ReadClientDisconnectMarker()
        {
            try
            {
                string p = ClientDisconnectMarkerPath();
                if (!File.Exists(p)) return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<ClientDisconnectMarker>(File.ReadAllText(p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ReadClientDisconnectMarker: {ex.Message}"); return null; }
        }

        /// <summary>CLIENT: clear the disconnect marker once consumed / superseded.</summary>
        public static void ClearClientDisconnectMarker()
        {
            try { string p = ClientDisconnectMarkerPath(); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }

        /// <summary>CLIENT: upload our pending disconnect save to the host (Phase 3) when it requests one.
        /// The host validates the save's ACTUAL in-game day before accepting it over its own copy. The
        /// Slot.Day here is just the CLAIMED day — the host re-reads the real day from the uploaded bytes.</summary>
        public static void UploadClientDisconnectSave(string hostSession)
        {
            try
            {
                string baseName = StripAutoSuffix(hostSession);
                var marker = ReadClientDisconnectMarker();
                if (marker == null || marker.SessionBase != baseName)
                { Plugin.Logger.LogWarning($"[MPSave] UploadClientDisconnectSave: no marker for '{baseName}'."); return; }
                string folder = MPSaveManager.MpCharacterFolder(baseName + "-disconnect", MPConfig.StableId);
                string? file  = NewestHsg(folder);
                if (file == null) { Plugin.Logger.LogWarning($"[MPSave] UploadClientDisconnectSave: no .hsg in '{folder}'."); return; }
                byte[] raw = File.ReadAllBytes(file);
                if (raw.Length == 0) return;
                var slot = new MpSlot
                {
                    StableId = MPConfig.StableId, DisplayName = MPConfig.PlayerId, CharacterId = MPConfig.StableId,
                    SaveName = Path.GetFileNameWithoutExtension(file), Day = marker.Day, IsHost = false,
                };
                MPClient.SendClientDisconnectUpload(new SaveDataPayload
                {
                    SessionName = baseName, Success = true, Slot = slot,
                    HsgGzipBase64 = GzipBase64(raw), RawLength = raw.Length,
                });
                Plugin.Logger.LogInfo($"[MPSave] Uploaded disconnect save for '{baseName}' (claimed day={marker.Day}, {raw.Length}B).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] UploadClientDisconnectSave: {ex.Message}"); }
        }

        /// <summary>HOST (MAIN THREAD — uses the IL2CPP save scanner): validate an uploaded client disconnect
        /// save by its ACTUAL in-game day and, if it passes, commit it as that player's save in the active
        /// session (overwriting the host's older copy). Accepted only when the real day is in
        /// [host's stored day for this player .. host's current world day + window] — newer than we hold, but
        /// not edited ahead of where the world actually is. Returns true if committed.</summary>
        /// <summary>Read the in-game day of a player's save in a session folder via the game's save scanner
        /// (the canonical day for a .hsg — distinct from a manifest slot's GameTime-based Day). -1 if none.</summary>
        private static int ReadSaveDay(string sessionFolder, string stable)
        {
            try
            {
                var saves = SaveGamePathHelper.GetAllSaveGamesFromVersion(sessionFolder);
                if (saves != null)
                    for (int i = 0; i < saves.Count; i++)
                    {
                        var s = saves[i];
                        string seg = Path.GetFileName((s?.CharacterPath ?? "").TrimEnd('\\', '/'));
                        if (string.Equals(seg, MPSaveManager.Sanitize(stable), StringComparison.OrdinalIgnoreCase)) return s.day;
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ReadSaveDay: {ex.Message}"); }
            return -1;
        }

        public static bool TryCommitClientDisconnectSave(SaveDataPayload p, string stable)
        {
            if (p == null || string.IsNullOrEmpty(stable) || string.IsNullOrEmpty(p.HsgGzipBase64)) return false;
            string session     = ActiveSessionName;
            string baseName    = StripAutoSuffix(session);
            string stageSession = "_dcstage_" + MPSaveManager.Sanitize(stable);
            try
            {
                if (!string.Equals(p.SessionName, baseName, StringComparison.Ordinal))
                { Plugin.Logger.LogWarning($"[MPSave] Disconnect upload session mismatch (got '{p.SessionName}', active base '{baseName}') — ignoring."); return false; }

                byte[] raw = UnGzipBase64(p.HsgGzipBase64);
                if (raw == null || raw.Length == 0) return false;

                // Stage into a throwaway session so we can read the save's ACTUAL day via the game scanner
                // before deciding — never disturb the real session unless we accept it.
                string stageDir = MPSaveManager.MpCharacterFolder(stageSession, stable);
                try { foreach (var f in Directory.GetFiles(stageDir)) File.Delete(f); } catch { }
                string name = MPSaveManager.Sanitize(string.IsNullOrEmpty(p.Slot?.SaveName) ? SaveFileName : p.Slot.SaveName);
                File.WriteAllBytes(Path.Combine(stageDir, name + ".hsg"), raw);

                int actualDay = ReadSaveDay(MPSaveManager.MpSessionFolder(stageSession), stable);
                int storedDay = ReadSaveDay(MPSaveManager.MpSessionFolder(session), stable);   // our current copy
                // Accept iff the uploaded save is at least as new as our copy and at most a small window past
                // it (the client only played a little before disconnecting). BOTH days come from the SAME
                // scanner so there's no numbering mismatch, and bounding against OUR stored copy (not the live
                // world clock) also avoids the save-info-vs-GameTime day-index difference that caused a false
                // reject. If we have no copy at all (storedDay<0), accept any readable save (it's all we have).
                bool accept = actualDay >= 0 && (storedDay < 0 || (actualDay >= storedDay && actualDay <= storedDay + DisconnectDayWindow));
                if (accept)
                {
                    string dstDir = MPSaveManager.MpCharacterFolder(session, stable);
                    File.WriteAllBytes(Path.Combine(dstDir, name + ".hsg"), raw);
                    MergeSlot(session, new MpSlot
                    {
                        StableId = stable, DisplayName = p.Slot?.DisplayName ?? stable, CharacterId = stable,
                        SaveName = name, Day = actualDay, IsHost = false,
                    });
                    Plugin.Logger.LogInfo($"[MPSave] ACCEPTED client disconnect save (stable={stable}, actualDay={actualDay}, storedDay={storedDay}, window={DisconnectDayWindow}) → committed to '{session}'.");
                }
                else
                {
                    Plugin.Logger.LogWarning($"[MPSave] REJECTED client disconnect save (stable={stable}, actualDay={actualDay}; allowed [{storedDay}..{storedDay + DisconnectDayWindow}]) — keeping host copy.");
                }
                return accept;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] TryCommitClientDisconnectSave: {ex.Message}"); return false; }
            finally { try { Directory.Delete(MPSaveManager.MpSessionFolder(stageSession), true); } catch { } }
        }

        /// <summary>The native midnight autosave (GameManager.RunMidNightAutoSave)
        /// fired while in an MP session.  Rather than let it drop a "Recover
        /// Midnight.hsg" into the SINGLE-PLAYER folder (unstripped + untracked), we
        /// write the SAME recover save into the MP area: a sibling
        /// '&lt;session&gt;-recover' session.  It is MANIFEST-LESS, so it doesn't
        /// collide with the normal per-player save selection (LoadOwnHsg/NewestHsg scan
        /// only the base + '-auto' sessions during a normal load).  It IS loadable: the
        /// grouped load screen (MPSaveManager.ListPlaythroughs) surfaces '-recover'
        /// folders as "Recover (crash)" points (roster borrowed from a sibling save),
        /// and selecting one reads each player's Recover Midnight.hsg via NewestHsg.
        /// Routed through PerformLocalSave like every other MP save.  MAIN THREAD.
        /// KNOWN DEFECT (2026-06-25): this is a per-machine LOCAL snapshot, NOT a
        /// host↔client coordinated save — on SEPARATE machines the host's '-recover'
        /// holds only the host's copy (the client's lives on the client's disk, never
        /// uploaded), and host/client write independently so a client can produce more
        /// of them than the host (orphans).  Fix in flight: route through the
        /// coordinated HostSaveNow path (host-triggered, clients upload, manifest +
        /// carry-forward) so every member is paired — see context log.</summary>
        // One recover save per (session, in-game day) — see _recoverSavedDays below.
        private static readonly HashSet<string> _recoverSavedDays = new();

        public static void MidnightRecoverSave()
        {
            // HOST-AUTHORITATIVE: only the host's midnight drives the recover checkpoint. A client never
            // self-saves here — it saves only when the host's coordinated SaveNow arrives — so it is now
            // structurally impossible for a client to produce more recover saves than the host (the old
            // per-machine path made orphans: 505 client vs 72 host in one capture).
            if (!MPServer.IsRunning) return;

            string baseSession;
            lock (_lock) { baseSession = _activeSessionName; }
            if (string.IsNullOrEmpty(baseSession)) return;   // no session yet — nothing to back up
            string recoverSession = StripAutoSuffix(baseSession) + "-recover";   // matches HostSaveNow("midnight")

            // Dedupe to once per (session, in-game day). The native RunMidNightAutoSave re-fires many
            // times per in-game midnight (host log showed 72 over the session; a behind client replaying
            // its catch-up hour drove 505) — without this the coordinated save would broadcast repeatedly.
            // Keyed by session so a fresh load (even at a lower day) saves again, no reset wiring needed.
            // A failed clock read (day<0) falls through and saves rather than poisoning the guard.
            int day = -1;
            try { day = GameStateReader.GetGameTime().day; } catch { }
            if (day >= 0 && !_recoverSavedDays.Add(recoverSession + "|" + day))
                return;   // already wrote this session's recover checkpoint for this in-game day

            // Coordinated, exactly like the autosave/disconnect saves: HostSaveNow("midnight") broadcasts
            // SaveNow (every client uploads its own .hsg), writes the host's slot + a manifest listing
            // everyone, and CarryForwardAbsentMembers copies forward anyone absent. So '-recover' is a
            // PAIRED, loadable checkpoint on a separate host PC — not a per-machine orphan. A lower-
            // frequency rollback point than '-auto' (once per in-game day), mirroring vanilla's daily
            // Recover save, so a bug captured by the latest autosave can still be reverted past.
            Plugin.Logger.LogInfo($"[MPSave] Midnight recover checkpoint (day {day}) → coordinated save '{recoverSession}'.");
            HostSaveNow("midnight");
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
            if (secs < 60f) secs = 60f;
            // 4b: surface the active cadence (and any change) once, so a bug report shows how much a crash
            // could cost between autosaves. Configurable via the 'AutosaveMinutes' key (0 = mirror the SP
            // setting); the floor is 60s.
            if (secs != _lastLoggedAutosaveSecs)
            {
                _lastLoggedAutosaveSecs = secs;
                Plugin.Logger.LogInfo($"[MPSave] Coordinated autosave interval: {secs / 60f:0.#} min (AutosaveMinutes={MPConfig.AutosaveMinutesLive()}; 0=mirror SP, 60s floor).");
            }
            return secs;
        }
        private static float _lastLoggedAutosaveSecs = -1f;

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
            // Stamp the world identity (native parity 2026-07-07). The LIVE world's id wins over
            // whatever is on disk: saving onto an existing name is an overwrite — the manifest must
            // describe the world being saved, not the one it used to hold. With no live id yet,
            // adopt the manifest's (resuming a stamped session), else inherit from a lineage
            // sibling, else mint — this is a new world's first save.
            if (!string.IsNullOrEmpty(_activePlaythroughId))
                _activeManifest.PlaythroughId = _activePlaythroughId;
            else if (!string.IsNullOrEmpty(_activeManifest.PlaythroughId))
                _activePlaythroughId = _activeManifest.PlaythroughId;
            else
            {
                _activePlaythroughId = InheritedPlaythroughId(sessionName) ?? Guid.NewGuid().ToString("N");
                _activeManifest.PlaythroughId = _activePlaythroughId;
            }
            return _activeManifest;
        }

        /// <summary>A lineage sibling's PlaythroughId, if any manifest in the family carries one —
        /// covers a save landing on an automatic sibling before the base was stamped. Pure JSON IO.</summary>
        private static string? InheritedPlaythroughId(string sessionName)
        {
            string baseName = StripAutoSuffix(sessionName);
            var names = new List<string> { baseName, baseName + "-auto" };
            for (int i = 2; i <= 10; i++) names.Add(baseName + "-auto-" + i);
            names.Add(baseName + "-disconnect");
            names.Add(baseName + "-recover");
            foreach (var n in names)
            {
                if (n == sessionName) continue;   // own manifest already checked by the caller
                try
                {
                    var id = MPSaveManager.ReadManifest(n)?.PlaythroughId;
                    if (!string.IsNullOrEmpty(id)) return id;
                }
                catch { }
            }
            return null;
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
                m.BuildingRealEstateOwners = RealEstateOwnersStableKeyed();
                m.Grants = new List<MpGrant>();
                foreach (var e in GrantSync.AllStoreEntries())
                    m.Grants.Add(new MpGrant { Kind = e.Kind, Owner = e.Owner, Grantee = e.Grantee, GranteeName = GrantSync.NameOf(e.Grantee) });
                m.Merger = BuildMergerManifest();
                m.MergerWalletBalance     = MPServer.SnapshotWalletBalances();      // slice 4
                m.MergerWalletContributed = MPServer.SnapshotWalletContributed();
                // Round-53: the running session's tuning dials persist with the save (mid-session
                // changes included), so the next load's lobby mirrors what this world actually ran.
                m.TuneNeedsDrain   = MPNeedsTuning.DrainPercent;
                m.TuneRestSpeed    = MPNeedsTuning.RestPercent;
                m.TuneMoraleTempo  = MPNeedsTuning.MoralePercent;
                RefreshSlotCash(m);
                MPSaveManager.WriteManifest(sessionName, m);
            }
        }

        /// <summary>Round-58 (RED ROC day-117→131 regression, 2026-07-22): loud line when the ACTIVE
        /// manifest's ownership state (deeds/rentals/grants) is meaningfully OLDER than the world we
        /// actually loaded — the signature of loading stale ledgers from an old save base while the
        /// character saves are current (the field case's lost window was bounded by mod-upgrade
        /// days). Log-only, report-visible; called at host world-ready.</summary>
        public static void CheckManifestFreshness()
        {
            if (!MPServer.IsRunning) return;
            try
            {
                MpManifest? m; string name;
                lock (_lock) { m = _activeManifest; name = _activeSessionName; }
                if (m == null || m.WorldDay <= 0) return;
                int worldDay = 0; try { worldDay = SaveGameManager.Current?.Day ?? 0; } catch { }
                if (worldDay <= 0) return;
                int drift = worldDay - m.WorldDay;
                if (drift >= 3)
                    Plugin.Logger.LogWarning(
                        $"[Integrity] MANIFEST STALE: session '{name}' ownership state (deeds/rentals/grants) is from day {m.WorldDay}, " +
                        $"but the loaded world is day {worldDay} ({drift} day(s) newer) — purchases/rentals made in between are NOT in " +
                        "the ledger and will read unowned (round-58; RED ROC class 2026-07-22).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Integrity] manifest freshness: {ex.Message}"); }
        }

        /// <summary>Host: persist the CURRENT grant set to the active session's manifest immediately. The grant
        /// store used to be written only at the "next coordinated save" — so a grant set after the last save (or
        /// before one happened) never reached the manifest and was lost on load (Grants=[], user 2026-06-30). This
        /// is called on every grant change. No-op until a session name exists (the first coordinated save covers
        /// pre-save grants). Cheap: writes only the small manifest, under the same lock as the coordinated save.</summary>
        public static void PersistGrantsNow()
        {
            try
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_activeSessionName)) return;
                    var m = EnsureManifest(_activeSessionName);
                    m.Grants = new List<MpGrant>();
                    foreach (var e in GrantSync.AllStoreEntries())
                        m.Grants.Add(new MpGrant { Kind = e.Kind, Owner = e.Owner, Grantee = e.Grantee, GranteeName = GrantSync.NameOf(e.Grantee) });
                    m.Merger = BuildMergerManifest();   // merger membership rides the same persist-on-change
                    m.MergerWalletBalance     = MPServer.SnapshotWalletBalances();      // slice 4: pooling/payout
                    m.MergerWalletContributed = MPServer.SnapshotWalletContributed();   // states persist immediately
                    m.SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    MPSaveManager.WriteManifest(_activeSessionName, m);
                    Plugin.Logger.LogInfo($"[MPSave] Persisted {m.Grants.Count} grant(s) + {m.Merger.Count} merger member(s) to '{_activeSessionName}' on change.");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] PersistGrantsNow: {ex.Message}"); }
        }

        private static List<MpMergerMember> BuildMergerManifest()
        {
            var list = new List<MpMergerMember>();
            foreach (var kv in MergerSync.StoreGroups)
                foreach (var s in kv.Value)
                    list.Add(new MpMergerMember { StableId = s, Name = GrantSync.NameOf(s), Group = kv.Key });
            return list;
        }

        /// <summary>Re-key MPServer.BuildingRealEstateOwners (live PlayerId / "host") to
        /// immutable stable ids for the manifest — mirrors BuildOwnersStableKeyed so
        /// bought-building ownership survives save/reload.</summary>
        private static Dictionary<string, string> RealEstateOwnersStableKeyed()
        {
            var result = new Dictionary<string, string>();
            try
            {
                foreach (var kv in MPServer.BuildingRealEstateOwners)
                {
                    string owner = kv.Value;
                    if (string.IsNullOrEmpty(owner)) continue;
                    string stable;
                    if (owner == "host")
                        stable = MPConfig.StableId;
                    else if (!MPServer.StableIdByPlayer.TryGetValue(owner, out stable) || string.IsNullOrEmpty(stable))
                        stable = owner;
                    result[kv.Key] = stable;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] RealEstateOwnersStableKeyed: {ex.Message}"); }
            return result;
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
