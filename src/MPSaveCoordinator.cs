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
        // Handoff slice 2: host-start counter for the ACTIVE lineage. 1 for a brand-new
        // world; a (re)load of a stored session sets it to the stored epoch + 1 — one
        // increment per host-start, every manifest write stamps the current value (so
        // all lineage siblings carry it uniformly). Provenance + dual-host detection
        // (slice 4) read it; it never resolves conflicts (host is sovereign).
        private static int              _activeHostEpoch = 1;

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
                    if (string.IsNullOrEmpty(_activeSessionName))
                    {
                        _activePlaythroughId = ""; _activeHostEpoch = 1; _midJoinSource = ""; PortraitFolder = null; MPSaveManager.ClearActivePlaythrough(); MPSaveManager.ResetSessionPins();
                        // Round-274/M1: the rollback fence dies with the world — bare-name keys
                        // must never leak into a different playthrough's identically-named slots.
                        lock (_abandonedAtLoad) { _abandonedAtLoad.Clear(); _abandonedCaptureUnix = 0; _loadedStampAtCapture = 0; _servedSinceLoad.Clear(); _remadeUnderFence.Clear(); }
                    }
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
            MPFrameRhythm.MarkBeat("save");   // round-207: save beats are a classic hitch source
            if (!MPServer.IsRunning)
            {
                Plugin.Logger.LogWarning("[MPSave] HostSaveNow ignored — not hosting.");
                return;
            }
            // Task-28 fix 2: the host serializes its own world in this flow — same
            // mid-placement hazard as the client leg; defer (tick-retried, 30s cap).
            if (DeferWhileSaveBlocked(reason, host: true)) return;

            string session;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_activeSessionName))
                    _activeSessionName = DefaultSessionName();
                session = _activeSessionName;
            }
            EnsureActivePid(session);   // round-216: BEFORE any rotation/lineage probe

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
            if (reason == "join") lock (_lock) { _lastJoinSession = session; _joinSaveSeq++; }   // round-274/F1: the verify pins THIS fire

            Plugin.Logger.LogInfo($"[MPSave] HostSaveNow session='{session}' reason={reason} — broadcasting SaveNow.");

            try
            {
                var payload = new SaveNowPayload { SessionName = session, Reason = reason, PlaythroughId = ActivePlaythroughId };
                MPServer.BroadcastAny(MessageEnvelope.Create(MessageType.SaveNow, "host", payload));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] SaveNow broadcast: {ex.Message}"); }
            // Round-184 fix 1 (rig-proven, TEST184-INT2): the immediate carry-forward below skips
            // CONNECTED members (their upload is expected) — but an upload can fail (crash, drop,
            // settle-gate skip), leaving the session WITHOUT their .hsg; the next load of it
            // fresh-starts them.  Backstop: after the upload window, re-run the carry INCLUDING
            // connected members — it only copies when the target still holds nothing newer.
            lock (_lock) _pendingCarryBackstops.Add((session, Environment.TickCount));

            // Host's own save + manifest base — on the main thread (IL2CPP access).
            // The host's .hsg is written straight into its own MP folder, so there
            // is nothing to transfer.
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var slot = PerformLocalSave(session, out bool saved);
                    DiagPhase("host lambda: PerformLocalSave done → SetSessionMetadata");
                    // Session metadata (owners ledger + roster) reflects the LIVE world and
                    // stays valid for the members' incoming uploads even when the host's own
                    // .hsg write failed — but the save-moment claim (day + timestamp) only
                    // advances when the host's file actually landed (user-approved 2026-08-21;
                    // the picker dates by real file times, so members' files that still land
                    // keep their own honest dates).
                    SetSessionMetadata(session, slot.Day, saved);   // owners + roster (+ day/time if saved)
                    DiagPhase("host lambda: SetSessionMetadata done → MergeSlot");
                    if (saved) MergeSlot(session, slot);      // host's own slot
                    else Plugin.Logger.LogError($"[MPSave] host save '{session}' FAILED — own slot NOT advertised (round-237); the load-time lineage rescue fills it honestly. Members' uploads still land.");
                    // Carry forward anyone who isn't here to save themselves (a member who just left, or
                    // was offline) so a save never silently drops them → a load that would otherwise
                    // fresh-start them as a brand-new player. (2026-06-23; field 2026-07-19: the
                    // isAutomatic gate meant MANUAL saves — including save-as under a NEW NAME —
                    // skipped this entirely, and the host's next load of that store reset the
                    // offline member. Every save carries absent members now.)
                    CarryForwardAbsentMembers(session);
                    // This save target is now the freshest complete roster snapshot —
                    // serve mid-session joiners from it (review-2 fix).
                    lock (_lock) { _midJoinSource = session; }
                    // Loan ledger rides every session save — loans created
                    // BEFORE the session's first save (no folder yet) would
                    // otherwise never persist unless the ledger changed again.
                    MPHub.SaveLedger();
                    // Handoff slice 1: replicate the store (manifest + host's own +
                    // absent members' .hsg) to every client. Off-thread — pure file
                    // IO + sends; the .hsg is complete (JoinSaveGameThreads returned).
                    System.Threading.Tasks.Task.Run(() => MirrorStoreSweep(session));
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
                var slot = PerformLocalSave(dc, out bool saved);
                SetSessionMetadata(dc, slot.Day, saved);
                if (saved) MergeSlot(dc, slot);
                else Plugin.Logger.LogError($"[MPSave] quit checkpoint '{dc}' save FAILED — own slot NOT advertised (round-237); members still carried forward below.");
                // Same carry-forward as every other save path (review 2026-07-20) — but
                // INCLUDING connected members (review-2 fix 2026-07-23): unlike every
                // other save, the quit checkpoint broadcasts NO SaveNow, so no uploads
                // are coming. Skipping connected members left the dc manifest without
                // their slots — the member who then hosted the checkpoint got their
                // cash overlaid with $0 (BestCashFor: no live figure, no slot).
                CarryForwardAbsentMembers(dc, includeConnected: true);
                // Handoff slice 1: best-effort mirror before the window closes —
                // inline (not Task.Run) so the sends are queued before teardown.
                MirrorStoreSweep(dc);
                // ── Round-282/282b: make the farewell mirror actually leave ──────
                // The farewell mirror is the ONE mirror a voluntary host switch depends
                // on, and round-282 put mirrors on a metered lane that releases a chunk
                // at a time — "queued before teardown" stopped meaning "gone before
                // teardown".  Two steps, in this order, both INLINE on the quit stack:
                //   1. drain — wait while bytes are still moving (progress-based, not a
                //      fixed budget: a ~6.6MB store at the measured 250-330KB/s needs
                //      ~22s and the old 8s cap failed healthy transfers).
                //   2. flushing close — hand each link a graceful close so the transport
                //      flushes what IT still holds, with a reason tag the client names.
                //
                // TEARDOWN ORDER (read from the code, 2026-08-19) — why these two must
                // sit HERE and not in MPServer.Stop:
                //   • Window close / quit-to-desktop → Unity fires MPCanvasUI's
                //     OnApplicationQuit → this method, all on one stack.
                //   • Pause-menu Save & Exit → Patch_MiniMenu_SaveAndExitToDesktop →
                //     MenuSave(exiting:true) → MiniMenuUtil.QuitToDesktop → the game's
                //     quit coroutine → Application.Quit → the SAME OnApplicationQuit.
                //   • The socket close lives further out and later: Plugin.OnUnloadAsync
                //     → MPServer.Stop → SteamHostTransport.Stop → _socket.Close() (bare,
                //     no linger — it DISCARDS whatever Steam still holds).  Whether the
                //     loader's unload hook even runs before process exit is not
                //     guaranteed; running the drain + flushing close inline here is what
                //     makes them provably precede both that close and the exit.
                MPServer.DrainOutboundForQuit();
                MPServer.CloseLinksForQuit();
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
            // Review-3 fix: the active pointer can sit on a suffixed sibling (EnsureManifest
            // relocates it during automatic saves and after a disconnect-save commit) — a
            // MANUAL save must always land on the lineage base, like HostSaveNow's strip.
            session = StripAutoSuffix(session);
            EnsureActivePid(session);   // round-216: BEFORE any path probe
            Plugin.Logger.LogInfo($"[MPSave] HostSaveSync session='{session}' reason={reason} (inline).");
            try
            {
                var payload = new SaveNowPayload { SessionName = session, Reason = reason, PlaythroughId = ActivePlaythroughId };
                MPServer.BroadcastAny(MessageEnvelope.Create(MessageType.SaveNow, "host", payload));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] HostSaveSync broadcast: {ex.Message}"); }
            // Round-184 fix 1 (rig-caught, test184-int4): this MANUAL path never queued the
            // upload backstop — HostSaveNow did — so a save-as followed by a quick quit could
            // still be born without a member whose upload failed.  Same contract as HostSaveNow.
            lock (_lock) _pendingCarryBackstops.Add((session, Environment.TickCount));
            try
            {
                var slot = PerformLocalSave(session, out bool saved);
                SetSessionMetadata(session, slot.Day, saved);
                if (saved) MergeSlot(session, slot);
                else Plugin.Logger.LogError($"[MPSave] menu save '{session}' FAILED — own slot NOT advertised (round-237); members' uploads and carry-forward still run.");
                // Field 2026-07-19 (the unintended character reset): this MANUAL path —
                // which is exactly where a save-as under a new name lands — never carried
                // absent members, so the new store was born without the offline player's
                // .hsg and the host's next load fresh-started them.
                CarryForwardAbsentMembers(session);
                // Serve mid-session joiners from this fresh snapshot (review-2 fix).
                lock (_lock) { _midJoinSource = session; }
                // Loan ledger rides every session save, exactly as HostSaveNow does.
                // The pause-menu save is often the session's FIRST save, so a loan
                // accepted beforehand would otherwise never reach disk (the ledger
                // path needs the session folder, which PerformLocalSave just created).
                MPHub.SaveLedger();
                // Handoff slice 1: replicate the store to clients. Off-thread; on a
                // Save-and-Exit this is best-effort — whatever the socket flushes
                // before teardown still lands, and the next session re-mirrors.
                System.Threading.Tasks.Task.Run(() => MirrorStoreSweep(session));
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
                var slot = PerformLocalSave(session, out bool saved);
                // Round-237 fix B: on failure NewestHsg below would find the PREVIOUS save
                // and ship it under today's slot — skip; the host round-trip requested
                // above is the remaining carrier of this exit.
                if (!saved)
                {
                    Plugin.Logger.LogError($"[MPSave] exit self-save '{session}' FAILED — inline ship SKIPPED (round-237).");
                    return;
                }
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
            // Round-217: the order names its world — pin BEFORE writing a byte, so a
            // session name this client has never seen still files under the right
            // playthrough (the rig case: host's manual save under a brand-new name
            // reached the client ahead of any mirror piece for it, and the client's
            // own save landed in '_unresolved' → the upload then found nothing).
            if (!string.IsNullOrEmpty(payload.PlaythroughId))
            {
                MPSaveManager.SetActivePlaythrough(payload.PlaythroughId, StripAutoSuffix(session));
                MPSaveManager.NoteSessionPid(session, payload.PlaythroughId);
                MPSaveManager.NoteSessionPid(StripAutoSuffix(session), payload.PlaythroughId);
            }
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

            GameStatePatcher.EnqueueOnMainThread(() => ClientSaveBody(session));
        }

        /// <summary>The client save execution — split out so a placement-deferred save can
        /// re-enter from TickDeferredSave (task-28 fix 2).  Main thread only.</summary>
        private static void ClientSaveBody(string session)
        {
                try
                {
                    // Round-157 — BLANK-UPLOAD GUARD, client side.  Field case ('save 2', day 72): a
                    // SaveNow arrived while this client's world was NOT loaded (mid crash-recovery),
                    // the save serialized a near-empty GameInstance (254KB vs the real 1.7MB) and the
                    // host stored it over the member's slot.  A save of a world we are not standing in
                    // is never valid — skip it loudly; the next coordinated save covers us.
                    // Round-179: upgraded from in-world to the full settled-gate (loading done, quiesce
                    // lifted, settle margin) — a save serialized during ANY unsettled window is invalid.
                    // Deliberate DROP, not a deferral: coordinated saves recur on their own schedule, so
                    // the next one covers this building of the retry contract.
                    if (!MPWorldReady.IsSettled)
                    {
                        Plugin.Logger.LogWarning($"[MPSave] SaveNow for '{session}' arrived while this client's world is not settled — SKIPPED (an unsettled save would clobber our slot; the next coordinated save covers us).");
                        return;
                    }
                    // Round-237 (subsumes task-28 fix 2): the game hard-refuses saves in four
                    // states (designer/casino/school/placement) — DEFER until the state
                    // clears; see DeferWhileSaveBlocked.  Retried per frame; a newer SaveNow
                    // supersedes the deferred one.
                    if (DeferWhileSaveBlocked(session, host: false)) return;
                    // DEV TOGGLE (round-184 test rig): drop this client's save+upload to fabricate an
                    // interrupted coordinated save (host ends up with a partial manifest) without
                    // having to kill the process.  Armed by creating <ModRoot>\dev-drop-upload.flag.
                    try
                    {
                        if (System.IO.File.Exists(System.IO.Path.Combine(MPConfig.ModRootPath, "dev-drop-upload.flag")))
                        {
                            Plugin.Logger.LogWarning($"[MPSave] DEV FLAG: save+upload for '{session}' DROPPED (dev-drop-upload.flag present) — simulating an interrupted coordinated save.");
                            return;
                        }
                    }
                    catch { }
                    var slot = PerformLocalSave(session, out bool saved);
                    // Round-237 fix B: a failed save must never ship.  The upload reads the
                    // folder's newest .hsg — on failure that is the PREVIOUS save's bytes —
                    // and the host would store them under this slot's fresh Day stamp.  The
                    // last genuine upload stays authoritative; the next coordinated save
                    // covers us (field 20260803-022659 shipped five of these).
                    if (!saved)
                    {
                        Plugin.Logger.LogError($"[MPSave] save '{session}' FAILED — upload SKIPPED so the older on-disk .hsg cannot travel under today's day label (round-237).");
                        return;
                    }
                    string dir = MPSaveManager.MpCharacterFolder(session, MPConfig.StableId);
                    lock (_pending)
                        _pending.Add(new PendingUpload { Session = session, Slot = slot, Folder = dir });
                    Plugin.Logger.LogInfo($"[MPSave] Queued .hsg upload for session '{session}'.");
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] Client save: {ex}"); }
        }

        // ── Round-237 (subsumes task-28 fix 2): no save while native CanSave() says no ─
        // The game refuses to save in four states (SaveGameManager.CanSave :490): casino
        // boat, Interior Designer, the school activity panel, item placement.  Vanilla
        // never collides with them at save time because it PAUSES in those UIs; the MP
        // clock keeps running, so a coordinated save can land inside any of them (field
        // 20260803-022659: five midnight saves failed inside the designer, with the
        // native "save not allowed" error on that player's screen each time).
        // Contract: DEFER and retry per frame (TickDeferredSave) until the state clears.
        // Proceeding early is a GUARANTEED failure — the gate sits inside
        // SaveGameManager.Save itself, not ours to bypass — which retires task-28's 30s
        // "proceed anyway" placement escape hatch: CanSave hard-blocks placement too, so
        // that branch could only ever produce a failed save (whose stale bytes then
        // uploaded — see the round-237 guard in ClientSaveBody).  A newer SaveNow
        // overwrites the deferred slot (the latest checkpoint supersedes); a 60s
        // heartbeat log keeps a long wait diagnosable.
        private sealed class DeferredSave { public string What = ""; public bool Host; public string Why = ""; public float NextBeat; }
        private static DeferredSave? _deferredSave;
        private static System.Reflection.MethodInfo? _canSaveMi;
        private static bool _canSaveMiSearched;

        private static bool PlacementActive()
        {
            try { return BigAmbitions.PlacementSystem.PlacementSystem.IsInPlacementMode; } catch { return false; }
        }

        /// <summary>Null when saving is allowed, else a short reason.  Asks the game's own
        /// (private) CanSave when reachable — drift-proof: a no-save state added in a future
        /// game build defers here instead of failing there — and names the state by probing
        /// the four known conditions.  The probes ARE the gate if reflection ever fails.</summary>
        private static string? SaveBlockedBy()
        {
#if BAMP_DEV
            // Round-239 test driver: lets a rig test exercise the round-237 deferral machinery
            // (defer → heartbeat → resume → ship) without a human holding the designer open.
            if (UnityEngine.Time.unscaledTime < TestDrive.SimulateSaveBlockUntil)
                return "a TestDrive-simulated no-save state";
#endif
            bool nativeSaysNo = false, nativeAsked = false;
            try
            {
                if (!_canSaveMiSearched)
                {
                    _canSaveMiSearched = true;
                    _canSaveMi = HarmonyLib.AccessTools.Method(typeof(SaveGameManager), "CanSave");
                    if (_canSaveMi == null)
                        Plugin.Logger.LogWarning("[MPSave] native CanSave not found — save gate falls back to the four known states (round-237).");
                }
                if (_canSaveMi != null && _canSaveMi.Invoke(null, null) is bool allowed)
                {
                    nativeSaysNo = !allowed;
                    nativeAsked = true;
                }
            }
            catch { }
            string? why = null;
            try { if (UI.InteriorDesigner.InteriorDesignerUI.IsOpen) why = "the Interior Designer is open"; } catch { }
            if (why == null) try { if (CasinoBoatManager.IsOnCasinoBoat) why = "the player is on the casino boat"; } catch { }
            if (why == null) try { if (PlacementActive()) why = "an item is being placed"; } catch { }
            if (why == null) try
            {
                if (PlayerActivity.PlayerActivityUI.IsPanelOpen && BuildingManager.IsInsideBuilding
                    && InstanceBehavior<BuildingManager>.Instance?.buildingRegistration?.businessTypeName == "ba:businesstype_school")
                    why = "the school activity panel is open";
            } catch { }
            if (nativeAsked) return nativeSaysNo ? (why ?? "a native no-save state (unrecognized — new game build?)") : null;
            return why;
        }

        private static bool DeferWhileSaveBlocked(string what, bool host)
        {
            string? why = SaveBlockedBy();
            if (why == null) return false;
            _deferredSave = new DeferredSave { What = what, Host = host, Why = why, NextBeat = UnityEngine.Time.unscaledTime + 60f };
            Plugin.Logger.LogInfo($"[MPSave] save '{what}' deferred — {why} on this machine and the game refuses saves in that state; retrying every frame until it clears (round-237).");
            return true;
        }

        private static void TickDeferredSave()
        {
            var d = _deferredSave;
            if (d == null) return;
            string? why = SaveBlockedBy();
            if (why != null)
            {
                if (UnityEngine.Time.unscaledTime >= d.NextBeat)
                {
                    d.NextBeat = UnityEngine.Time.unscaledTime + 60f;
                    Plugin.Logger.LogInfo($"[MPSave] save '{d.What}' still deferred — {why}.");
                }
                return;
            }
            _deferredSave = null;
            Plugin.Logger.LogInfo($"[MPSave] save '{d.What}' resuming — {d.Why} no longer holds.");
            if (d.Host) HostSaveNow(d.What);
            else ClientSaveBody(d.What);
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
        // Round-184 fix 1: coordinated saves whose upload window is still open.  Each entry
        // resolves by EARLY COMPLETION (every connected member's .hsg landed — polled at most
        // every 2s), by the 45s deadline, or by an explicit teardown FLUSH — save-and-quit is
        // common and the process is often gone long before 45s (user call, 2026-07-29).
        private static readonly List<(string session, int atMs)> _pendingCarryBackstops = new();
        private static int _nextBackstopPollMs;

        /// <summary>True when every CONNECTED member already has an .hsg in the session folder —
        /// the backstop has nothing to add.</summary>
        private static bool SessionHasAllConnected(string session)
        {
            try
            {
                foreach (var stable in MPServer.ConnectedStableIds())
                {
                    if (stable == MPConfig.StableId) continue;   // the host writes its own directly
                    string dir = Path.Combine(MPSaveManager.MpSessionFolder(session), stable);
                    if (!Directory.Exists(dir) || NewestHsg(dir) == null) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static void TickCarryBackstops()
        {
            if (_pendingCarryBackstops.Count == 0) return;                                // benign unlocked peek
            if (unchecked(Environment.TickCount - _nextBackstopPollMs) < 0) return;       // 2s IO throttle
            _nextBackstopPollMs = Environment.TickCount + 2000;
            List<(string session, int atMs)> snapshot;
            lock (_lock) snapshot = new List<(string, int)>(_pendingCarryBackstops);
            var done = new List<(string, int)>();
            List<string>? carry = null;
            foreach (var e in snapshot)
            {
                if (SessionHasAllConnected(e.session)) { done.Add(e); continue; }         // complete — uploads all landed
                if (unchecked(Environment.TickCount - e.atMs) > 45_000)
                {
                    done.Add(e);
                    (carry ??= new List<string>()).Add(e.session);
                }
            }
            if (done.Count > 0) lock (_lock) foreach (var e in done) _pendingCarryBackstops.Remove(e);
            if (carry == null) return;
            foreach (var s in carry)
            {
                Plugin.Logger.LogInfo($"[MPSave] carry-forward backstop for '{s}' — a member's .hsg never landed; carrying their newest lineage copy (round-184).");
                CarryForwardAbsentMembers(s, includeConnected: true);
            }
        }

        /// <summary>Round-184: complete every pending backstop NOW — save-and-quit, a menu
        /// return, or loading another session must never leave a just-saved session missing a
        /// member because the 45s window never elapsed.</summary>
        public static void FlushCarryBackstopsNow(string reason)
        {
            List<(string session, int atMs)> all;
            lock (_lock)
            {
                if (_pendingCarryBackstops.Count == 0) return;
                all = new List<(string, int)>(_pendingCarryBackstops);
                _pendingCarryBackstops.Clear();
            }
            foreach (var e in all)
            {
                if (SessionHasAllConnected(e.session)) continue;
                Plugin.Logger.LogInfo($"[MPSave] carry-forward backstop FLUSH ({reason}) for '{e.session}' — completing before teardown (round-184).");
                CarryForwardAbsentMembers(e.session, includeConnected: true);
            }
        }

        // ── Round-271 (Fix A): join baseline save, fired on the joiner's SETTLED report ──
        // The old trigger (client world-ready) was structurally before the joiner's own
        // settled-gate — the save it triggered could never contain the joiner's upload, so
        // every first-join slot was born without its trigger's member (field 20260816-213224).
        // The host may ALSO still be loading when a fast client settles (same field case:
        // the join save captured the host's mid-load world) — defer until the host's own
        // gate opens; the per-frame tick below is the retry, exit condition confirmed.
        // Round-274/M4: the request is a bare volatile flag set — NO Unity API on the
        // transport poll thread (the old immediate branch evaluated IsSettled there,
        // whose raw-false path resets the MAIN thread's settle window as a side effect).
        // The main-thread tick is the only evaluator; N clients settling while the host
        // is busy coalesce into ONE coordinated save, which captures every member anyway.
        private static volatile bool _pendingJoinBaseline;
        private static bool _pendingJoinLogged;

        // ── Round-274/F1: verify the joiner actually LANDED in the join baseline ────
        // The 'Running' fallback trigger can outrun the client's settled-gate (reviewer
        // measured Running-first on 2 of 5 joins, one-frame margins): the save fires,
        // the client's own gate DROPS its upload, and a one-shot latch would eat the
        // 'Settled' retry — the pre-271 fileless-slot hole reborn.  Cure, backstop-style
        // (round-184 precedent): after every baseline trigger, wait out the upload
        // window, check the slot for the joiner's file, refire ONCE if absent; a second
        // absence is left to the periodic autosave, loudly.
        // Round-274c (verifier CONFIRMED-2 + PLAUSIBLE-1/3): the first cut's presence test
        // was existence-only — a reused rotation slot's STALE file satisfied it, a vacuous
        // pass over exactly the hole this verify exists to close (rig-observed).  Now each
        // entry PINS the specific join save it watches (a sequence number pairs arm → fire,
        // so interleaved joins can't cross wires), the 12s window starts at the FIRE (not
        // the arm — a deferred save no longer burns the window), and presence means the
        // member's file was WRITTEN AT-OR-AFTER that fire (freshness, not existence).
        private sealed class BaselineVerify
        {
            public string Stable = ""; public string Session = "";
            public int ArmSeq; public int ArmedMs; public int FiredMs; public long FiredWallUnix;
            public bool Refired;
        }
        private static readonly List<BaselineVerify> _baselineVerifies = new();
        private static string _lastJoinSession = "";
        private static int _joinSaveSeq;               // bumped per join-reason save (under _lock)
        private const int BaselineVerifyMs = 12000;    // upload window after the FIRE
        private const int BaselineFireWaitMs = 90000;  // give a deferred join save this long to fire at all
        // Round-276 (field 20260818-215459): a join save costs ~1s of host main thread +
        // ~6.6MB of mirrors per fire — three fired in 15s during the storm.  Minimum
        // spacing between fires; a deferred request KEEPS its pending flag (recurrence-
        // covered) and every joiner that accumulates during the hold rides the one save
        // (SaveNow already broadcasts to everyone; verifies arm per-stable).  60s sits
        // inside BaselineFireWaitMs (90s) so no armed verify is ever dropped by the hold.
        private const int JoinBaselineMinIntervalMs = 60000;
        private static int _lastJoinFireMs;
        private static bool _joinRateLogged;
        // Round-276: refiring a join save into a congested link amplifies the very
        // congestion that made the first window unmeetable (measured RTT ramp 4→8→15→21s
        // as fires stacked).  Above this outbound backlog, extend the window instead.
        private const long VerifyCongestedBytes = 256 * 1024;
        // Round-276b (verifier finding 3): extensions need a ceiling — a peer that stays
        // congested (or wedges without disconnecting) must not extend forever.  Past this
        // age (from the ARM) the verify degrades to the documented fallback: the periodic
        // autosave covers the member.  5 min > any drain the 64MB link cap permits surviving.
        private const int BaselineExtendCapMs = 300000;

        internal static void ArmJoinBaselineVerify(string stable)
        {
            if (string.IsNullOrEmpty(stable) || stable == MPConfig.StableId) return;
            int seq; lock (_lock) seq = _joinSaveSeq;
            lock (_baselineVerifies)
            {
                foreach (var v in _baselineVerifies) if (v.Stable == stable) return;   // already watching
                _baselineVerifies.Add(new BaselineVerify { Stable = stable, ArmSeq = seq, ArmedMs = Environment.TickCount });
            }
        }

        /// <summary>Round-276b (verifier finding 2): a departed member's armed verify is
        /// dropped — their file can never land, so the entry would otherwise fall through
        /// to a full refire for a player who is gone.  Called from the disconnect path.</summary>
        internal static void DropJoinBaselineVerify(string stable)
        {
            if (string.IsNullOrEmpty(stable)) return;
            lock (_baselineVerifies)
            {
                int n = _baselineVerifies.RemoveAll(v => v.Stable == stable);
                if (n > 0) Plugin.Logger.LogInfo($"[MPSave] join baseline verify for stable={stable} dropped — member disconnected before their upload could land (round-276b).");
            }
        }

        /// <summary>Round-276b (verifier finding 4): a due entry is removed under the lock,
        /// processed outside it, and re-queued — in that gap a concurrent arm for the same
        /// member can't see it and adds a fresh entry.  On re-queue, the fresh arm wins
        /// (it is pinned to the newer sequence); adding ours back would double the watch.</summary>
        private static void ReAddVerifyUnlessSuperseded(BaselineVerify v)
        {
            lock (_baselineVerifies)
            {
                foreach (var e in _baselineVerifies) if (e.Stable == v.Stable) return;
                _baselineVerifies.Add(v);
            }
        }

        private static void TickJoinBaselineVerify()
        {
            if (!MPServer.IsRunning) { lock (_baselineVerifies) _baselineVerifies.Clear(); return; }
            int seqNow; string sessionNow;
            lock (_lock) { seqNow = _joinSaveSeq; sessionNow = _lastJoinSession; }
            List<BaselineVerify>? due = null;
            lock (_baselineVerifies)
            {
                for (int i = _baselineVerifies.Count - 1; i >= 0; i--)
                {
                    var v = _baselineVerifies[i];
                    if (v.FiredMs == 0)
                    {
                        if (seqNow > v.ArmSeq)   // the join save this entry waits on has fired — pin it
                        { v.Session = sessionNow; v.FiredMs = Environment.TickCount; v.FiredWallUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
                        else if (unchecked(Environment.TickCount - v.ArmedMs) >= BaselineFireWaitMs)
                        {
                            Plugin.Logger.LogWarning($"[MPSave] join baseline for stable={v.Stable} never fired within {BaselineFireWaitMs / 1000}s of the trigger — dropping the verify (the periodic autosave covers).");
                            _baselineVerifies.RemoveAt(i);
                        }
                        continue;
                    }
                    if (unchecked(Environment.TickCount - v.FiredMs) >= BaselineVerifyMs)
                    { (due ??= new List<BaselineVerify>()).Add(v); _baselineVerifies.RemoveAt(i); }
                }
            }
            if (due == null) return;
            foreach (var v in due)
            {
                // Presence = a file written at-or-after THIS fire (5s clock slack) — a reused
                // rotation slot's stale copy no longer reads as success.
                bool present = false;
                try
                {
                    string? hsg = NewestHsg(MPSaveManager.MpCharacterFolder(v.Session, v.Stable));
                    if (hsg != null)
                        present = new DateTimeOffset(File.GetLastWriteTimeUtc(hsg)).ToUnixTimeSeconds() >= v.FiredWallUnix - 5;
                }
                catch { }
                if (present) continue;
                // Round-276: if OUR outbound queue to this member is still congested, the
                // upload window was structurally unmeetable — their upload is queued behind
                // our own backlog (field 20260818-215459: RTT ramped 4→8→15→21s as fires
                // stacked; both "missing" uploads landed 3-4s after the verifier gave up).
                // Extend the window instead of refiring another ~6.6MB save into the very
                // congestion.  FiredWallUnix keeps the ORIGINAL fire time, so a file that
                // lands during the extension still satisfies the freshness check; the
                // extension recurs while congested and resolves when the link drains.
                long outQ = 0;
                try { outQ = MPServer.PendingSendBytesToStable(v.Stable); } catch { }
                if (outQ > VerifyCongestedBytes)
                {
                    // Round-276b: extension ceiling — past the cap, hand off to the
                    // periodic autosave loudly instead of extending forever.
                    if (unchecked(Environment.TickCount - v.ArmedMs) >= BaselineExtendCapMs)
                    {
                        Plugin.Logger.LogWarning($"[MPSave] join baseline verify for stable={v.Stable}: link still congested ({outQ / 1024}KB) after {BaselineExtendCapMs / 1000}s of extensions — giving up; the periodic autosave covers them (round-276b).");
                        continue;   // entry already removed from the list — dropped
                    }
                    v.FiredMs = Environment.TickCount;   // restart the 12s window; freshness anchor unchanged
                    ReAddVerifyUnlessSuperseded(v);      // round-276b: a concurrent fresh arm wins
                    Plugin.Logger.LogInfo($"[MPSave] join baseline verify for stable={v.Stable}: link congested ({outQ / 1024}KB queued outbound) — window extended instead of refiring into it (round-276).");
                    continue;
                }
                if (!v.Refired)
                {
                    // Round-276: wording no longer blames the settled-gate — in the field
                    // case the gate was healthy and transport latency was the cause; the
                    // old text sent the investigation to the wrong subsystem.
                    Plugin.Logger.LogWarning($"[MPSave] join baseline '{v.Session}' is missing a FRESH file for stable={v.Stable} after the upload window — their upload has not landed (slow link, refused upload, or their settled-gate); firing another (round-274/F1).");
                    int seqAtRefire; lock (_lock) seqAtRefire = _joinSaveSeq;
                    RequestJoinBaseline();
                    v.Refired = true; v.ArmSeq = seqAtRefire; v.ArmedMs = Environment.TickCount; v.FiredMs = 0; v.FiredWallUnix = 0; v.Session = "";
                    ReAddVerifyUnlessSuperseded(v);   // round-276b: a concurrent fresh arm wins
                }
                else
                    Plugin.Logger.LogError($"[MPSave] join baseline STILL missing a fresh file for stable={v.Stable} after a retry — leaving it to the periodic autosave (round-274/F1).");
            }
        }

        internal static void RequestJoinBaseline() => _pendingJoinBaseline = true;

        private static void TickPendingJoinBaseline()
        {
            if (!_pendingJoinBaseline) return;
            if (!MPServer.IsRunning) { _pendingJoinBaseline = false; _pendingJoinLogged = false; return; }
            if (!MPWorldReady.IsSettled)
            {
                if (!_pendingJoinLogged)
                {
                    _pendingJoinLogged = true;
                    Plugin.Logger.LogInfo("[MPSave] join baseline save deferred — host world not settled yet; fires when it is (round-271).");
                }
                return;
            }
            // Round-276 rate limit: the request stays pending through the hold (the
            // gate must not consume the trigger) and fires when the interval elapses.
            int sinceFire = unchecked(Environment.TickCount - _lastJoinFireMs);
            if (_lastJoinFireMs != 0 && sinceFire >= 0 && sinceFire < JoinBaselineMinIntervalMs)
            {
                if (!_joinRateLogged)
                {
                    _joinRateLogged = true;
                    Plugin.Logger.LogInfo($"[MPSave] join baseline deferred (rate limit): last join save {sinceFire / 1000}s ago — fires in ~{(JoinBaselineMinIntervalMs - sinceFire) / 1000}s; joiners accumulated meanwhile ride the same save (round-276).");
                }
                return;
            }
            _joinRateLogged = false;
            _pendingJoinBaseline = false;
            _pendingJoinLogged = false;
            _lastJoinFireMs = Environment.TickCount;
            HostSaveNow("join");
        }

        public static void TickPendingUploads()
        {
            TickDeferredSave();   // task-28 fix 2: placement-deferred saves retry here (every frame, main thread)
            TickPendingJoinBaseline();   // round-271: join baseline deferred until the HOST is settled
            TickJoinBaselineVerify();    // round-274/F1: refire once if the joiner missed their baseline
            TickCarryBackstops(); // round-184 fix 1: post-upload-window completeness sweep (host-side entries only)
            TickTornNotice();     // round-251B: deliver the torn-read warning once the world is visible
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
                    // Round-275: ship the .hsg.meta sidecar too — without it the host's copy
                    // cannot be dated by the save scanner (storedDay read -1, which made the
                    // disconnect-save day window accept anything).
                    string metaJson = "";
                    try { string mp = file + ".meta"; if (File.Exists(mp)) metaJson = File.ReadAllText(mp); } catch { }
                    if (MPClient.IsConnected)
                        MPClient.SendSaveData(up.Session, up.Slot, GzipBase64(raw), raw.Length, metaJson);
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
            // Round-222: the upload names its world — pin before any path resolution so
            // late/teardown arrivals file correctly even with no active session.
            if (data != null && !string.IsNullOrEmpty(data.PlaythroughId) && !string.IsNullOrEmpty(data.SessionName))
            {
                MPSaveManager.NoteSessionPid(data.SessionName, data.PlaythroughId);
                MPSaveManager.NoteSessionPid(StripAutoSuffix(data.SessionName), data.PlaythroughId);
            }
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
                string dest = Path.Combine(dir, name + ".hsg");
                // Round-157 — BLANK-UPLOAD GUARD, host side (defence in depth behind the client's
                // in-world gate): an upload less than HALF the size of the file it would replace is
                // the blank-save signature (field: 254KB over 1.7MB), never a legitimate shrink.
                // Keep the old file; the member's next healthy save replaces it normally.
                try
                {
                    var prev = new FileInfo(dest);
                    if (prev.Exists && prev.Length > 200_000 && raw.Length < prev.Length / 2)
                    {
                        // Round-274c: a member the fence remade legitimately shrinks — the big
                        // file being replaced IS the abandoned copy the ruling refused.
                        if (IsRemadeUnderFence(data.Slot.StableId))
                            Plugin.Logger.LogWarning($"[MPSave] small upload ({raw.Length}B) from remade member '{data.Slot.DisplayName}' ACCEPTED over a retained {prev.Length}B abandoned copy (round-274c).");
                        else
                        {
                            Plugin.Logger.LogWarning($"[MPSave] REFUSED '{data.Slot.DisplayName}' upload ({raw.Length}B) — it would replace a {prev.Length}B save with less than half its size (blank-save signature). Old file kept.");
                            return;
                        }
                    }
                }
                catch { }
                File.WriteAllBytes(dest, raw);
                // Round-275: land the sidecar with the save — a meta-less copy cannot be
                // dated by ReadSaveDay (the scanner), which blinded the day validation.
                try { if (!string.IsNullOrEmpty(data.MetaJson)) File.WriteAllText(dest + ".meta", data.MetaJson); } catch { }
                LogHsgWrite(data.SessionName, data.Slot.StableId, raw.Length, $"host stored {data.Slot.DisplayName}'s upload");
                Plugin.Logger.LogInfo($"[MPSave] Stored '{data.Slot.DisplayName}' .hsg ({raw.Length}B) → {dir}");
                DiagWrite($"HostHandleSaveData wrote .hsg ({raw.Length}B), merging slot");
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] HostHandleSaveData write: {ex}"); return; }

            MergeSlot(data.SessionName, data.Slot);
            // Handoff slice 1: this member's fresh .hsg just landed — mirror it (+
            // current manifest) to every OTHER client. Already on the network
            // thread, so the gzip + sends run inline without touching a frame.
            MirrorMemberFile(data.SessionName, data.Slot.StableId);
            DiagWrite("HostHandleSaveData done");
        }

        // ── Load / reconnect (Phase 4 step 4) ────────────────────────────────────

        /// <summary>Host: (re)load an MP session — restore the ownership map, ship
        /// each connected client its stored .hsg, and load the host's own.  Safe
        /// from any thread.</summary>
        /// <summary>Round-217: a world's identity is born WITH the world, not at its
        /// first save. Host calls this when beginning a brand-NEW world (StartNewGame)
        /// so every LoadData/SaveNow sent afterwards — including to mid-join clients
        /// while the world is still loading — names the playthrough. Always mints
        /// fresh: a new world is never the previous one, so any lingering identity
        /// from an earlier session must not leak into it. Loaded worlds adopt their
        /// identity in HostLoadSession instead.</summary>
        public static void HostBeginNewWorldIdentity()
        {
            lock (_lock)
            {
                _activePlaythroughId = Guid.NewGuid().ToString("N");
                _activeManifest      = null;   // a new world never continues an old manifest
                _activeSessionName   = "";     // named at first save (DefaultSessionName)
                _activeHostEpoch     = 1;
                _midJoinSource       = "";
                MPSaveManager.ResetSessionPins();   // round-218: nothing from a previous world survives
                MPSaveManager.SetActivePlaythrough(_activePlaythroughId, "");   // base named at first save
                // Round-274/F5: this path writes the backing field directly, bypassing the
                // property setter's fence teardown — clear here too (a new world is never
                // the rolled-back one; a stale registry could refuse its rescues).
                lock (_abandonedAtLoad) { _abandonedAtLoad.Clear(); _abandonedCaptureUnix = 0; _loadedStampAtCapture = 0; _servedSinceLoad.Clear(); _remadeUnderFence.Clear(); }
                Plugin.Logger.LogInfo($"[MPSave] New world — playthrough {_activePlaythroughId} minted at world start.");
            }
        }

        public static void HostLoadSession(string session)
        {
            if (!MPServer.IsRunning) return;
            // Round-184: leaving the current world for another load — complete any save still
            // waiting on member uploads first (its window may never elapse otherwise).
            try { FlushCarryBackstopsNow("host load"); } catch { }
            // Round-218: entering a different world — stale name pins and the previous
            // world's active identity must not survive into it (the rig cross-world
            // write: the auto-rotation reused another world's same-named slots). The
            // clicked world's own pins (set by the picker) are preserved.
            MPSaveManager.ResetSessionPinsExceptFamily(MPSaveManager.StripToBase(session));
            MPSaveManager.ClearActivePlaythrough();
            var m = MPSaveManager.ReadManifest(session);
            if (m == null) { Plugin.Logger.LogWarning($"[MPSave] HostLoadSession: no manifest for '{session}'."); MPServer.NotifyLoadRefused("no save catalog found."); return; }

            // 2026-08-18 ORDERING FIX (user-approved; found when a field save refused on the
            // rig): validate OUR OWN slot before anything below mutates shared state.
            // Previously RestoreOwnershipFromManifest persisted the manifest with the bumped
            // epoch + this machine as LastHost, and every client was served its save, all
            // BEFORE LoadOwnHsg's identity check could refuse — a refused load left a phantom
            // host-start in the group's records (FORK SUSPECT then chews on it).  Same
            // predicate as the load itself (ResolveOwnSlot) — never two copies.
            {
                int rc = ResolveOwnSlot(session, MPConfig.StableId, out _, out string effId);
                if (rc == -1)
                {
                    Plugin.Logger.LogError($"[MPSave] HostLoadSession '{session}' REFUSED before any state change: no save for this character (stable={effId}) among multiple member folders. Nothing was stamped, persisted, or served.");
                    // Round-285: hand the lobby back — the caller burned IsInLobby before we
                    // could refuse, and nothing else restores it (the wedge, live 2026-08-21).
                    MPServer.NotifyLoadRefused("no character found.");
                    return;
                }
                if (rc == -2)
                {
                    // "no save files found." RETIRED (user 2026-08-21): save-less folders are
                    // never listed, so reaching this means files vanished after listing — the
                    // player sees the one approved refusal text; this log keeps the real cause.
                    Plugin.Logger.LogWarning($"[MPSave] HostLoadSession '{session}': no member saves under the session folder — refused before any state change.");
                    MPServer.NotifyLoadRefused("no character found.");
                    return;
                }
            }

            // Round-37 FORK SEMANTICS: load FROM the selected variant/checkpoint folder, but CONTINUE the
            // playthrough on its lineage BASE — ongoing saves go to Main/-auto/checkpoints as usual and can
            // never mutate the loaded (frozen) source. Loading is a jump to a recorded moment; the recorded
            // moment stays recorded.
            string lineage = MPSaveManager.StripToBase(session);
            // Adopt the loaded world's identity (empty on pre-field saves — the first save then
            // mints one via EnsureManifest and the lineage keeps grouping by name until stamped).
            // Handoff slice 2: a (re)load is a host-start of this lineage — bump the epoch
            // (works identically on the original host and on a member hosting from their
            // MIRRORED copy of the store; every save stamps the new value + our identity).
            // Round-274/H3: _activeManifest must NOT adopt the VARIANT's manifest object while
            // _activeSessionName points at the BASE — EnsureManifest(base) reused it and wrote
            // the variant's Slots into a base folder holding no member files (rig-observed:
            // 2 phantom slots, zero folders), which under an active fence turns Fresh into
            // Unavailable and ABORTS the join.  Null → the next EnsureManifest re-reads the
            // base honestly from disk (or mints it empty — the truthful state).
            lock (_lock) { _activeSessionName = lineage; _activeManifest = null; _activePlaythroughId = m.PlaythroughId ?? ""; _activeHostEpoch = Math.Max(1, m.HostEpoch + 1); _midJoinSource = session; }
            // Store v2: the loaded world's identity becomes the active playthrough folder
            // (name-only writes for this family land there). Empty pid = legacy world —
            // the first EnsureManifest mint below will stamp + activate it.
            if (!string.IsNullOrEmpty(m.PlaythroughId)) MPSaveManager.SetActivePlaythrough(m.PlaythroughId, lineage);

            // One greppable line so any submitted log answers "did the host change hands?"
            // (user 2026-07-23) — the manifest records who hosted last; compare to this machine.
            if (!string.IsNullOrEmpty(m.LastHostStableId) && m.LastHostStableId != MPConfig.StableId)
            {
                var prevSlot = m.Slots?.Find(s => s.StableId == m.LastHostStableId);
                string prev = prevSlot == null ? m.LastHostStableId
                            : (string.IsNullOrEmpty(prevSlot.CharacterName) ? prevSlot.DisplayName : prevSlot.CharacterName);
                Plugin.Logger.LogWarning($"[MPSave] HOST HANDOFF: '{session}' was last hosted by '{prev}' — now hosting on this machine (host-start #{Math.Max(1, m.HostEpoch + 1)}).");
            }

            // Round-271 (field 20260816-213224): loading a slot while NEWER lineage siblings exist
            // is a deliberate rollback — those siblings are the ABANDONED timeline.  Captured here,
            // BEFORE any member serve below, so rescues can never hand a player a future-timeline
            // character (a rented building the loaded world has no record of: every owner-push then
            // dies as 'not the recorded owner' and their furnishing work silently evaporates).
            CaptureAbandonedTimeline(session, m);

            MPServer.RestoreOwnershipFromManifest(m);              // cross-machine ownership + cash seed
            MPServer.SendLoadDataToEachClient(session, m, lineage); // each client gets its own .hsg FROM the source, tagged with the lineage

            float hostCash = BestCashFor(m, MPConfig.StableId);
            // Review-2 fix: only overlay cash we actually KNOW (a live figure or a
            // manifest slot). A manifest without our slot — e.g. hosting a quit
            // checkpoint written before the carry-forward fix — used to overlay a
            // literal $0 over the loaded save's real money.
            bool hostCashKnown = MPServer.CashByStableId.ContainsKey(MPConfig.StableId)
                              || (m.Slots != null && m.Slots.Exists(s => s.StableId == MPConfig.StableId));
            if (!hostCashKnown)
                Plugin.Logger.LogInfo($"[MPSave] No recorded cash for this host in '{session}' — keeping the loaded save's own money.");
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try { if (LoadOwnHsg(session, MPConfig.StableId) && hostCashKnown) QueueCashApply(hostCash); }
                catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] Host load: {ex}"); }
            });
            Plugin.Logger.LogInfo($"[MPSave] HostLoadSession '{session}' → continuing lineage '{lineage}' — {m.Slots.Count} slot(s).");
        }

        /// <summary>Client: received its .hsg from the host — write it locally,
        /// load it, then overlay the host's restored cash.</summary>
        public static void ClientHandleLoadData(LoadDataPayload p)
        {
            if (p == null) return;
            // Review-3 fix: track the joined world's wire identity UNCONDITIONALLY. The
            // old conditional set left a STALE pid from a previous session in place
            // across a fresh-start into a different world — the disconnect marker then
            // stamped the wrong world and the pid gate refused a genuine recovery save.
            // An empty payload pid (fresh start / special branches / pre-field host)
            // CLEARS it; the marker then falls back to the mirrored manifest.
            _wireWorldPid = p.PlaythroughId ?? "";
            // Store v2: the join tells us exactly which world this session is — pin the
            // name and activate the playthrough so every local write (own slot, ledger,
            // disconnect save) lands in the right folder from the first byte.
            MPSaveManager.ResetSessionPins();   // round-218: a join replaces the world context wholesale
            MPSaveManager.SetActivePlaythrough(_wireWorldPid, StripAutoSuffix(p.SessionName ?? ""));
            if (!string.IsNullOrEmpty(_wireWorldPid) && !string.IsNullOrEmpty(p.SessionName))
            {
                MPSaveManager.NoteSessionPid(p.SessionName, _wireWorldPid);
                MPSaveManager.NoteSessionPid(StripAutoSuffix(p.SessionName), _wireWorldPid);
            }
            // Round-284 load ticket: adopt it up front — every branch below that leads to a
            // load (the fresh-start fallback included) must phase-report under THIS serve's
            // gen.  The no-load branches are never stamped by the host, so 0 changes nothing.
            if (p.LoadGen != 0) MPClient.ServedLoadGen = p.LoadGen;
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
            // Handoff slice 4 (REVISED per user 2026-07-23): loading an older save is STANDARD
            // multiplayer behavior — the host loaded it, joiners get it, no warning, no prompt
            // (we don't invent a new convention where one exists). The day/epoch comparison
            // survives as a LOG-ONLY diagnostic (report-visible, round-58 style) so a
            // "my progress went backwards" report can be read straight off the log.
            LogJoinRollbackFacts(p);
            ProceedWithLoadData(p);
        }

        /// <summary>LOG-ONLY (handoff slice 4): note when the session being joined is BEHIND what
        /// this machine's own store knows of the same world (host rolled back — normal), or when
        /// our store recorded a LATER host-start than the joined store knows (possible fork —
        /// two members hosting the same world independently). Pure file/JSON IO; never blocks
        /// or defers the load. Silent when either side lacks the fields.</summary>
        private static void LogJoinRollbackFacts(LoadDataPayload p)
        {
            try
            {
                if (p == null || string.IsNullOrEmpty(p.PlaythroughId) || p.WorldDay <= 0) return;
                int localDay = -1, localEpoch = -1; string newerHostName = "";
                foreach (var (_, m) in MPSaveManager.ListSessions())
                {
                    if (m == null || m.PlaythroughId != p.PlaythroughId) continue;
                    if (m.WorldDay > localDay) localDay = m.WorldDay;
                    if (m.HostEpoch > localEpoch)
                    {
                        localEpoch = m.HostEpoch;
                        var hs = m.Slots?.Find(s => s.StableId == m.LastHostStableId);
                        newerHostName = hs == null ? "" : (string.IsNullOrEmpty(hs.CharacterName) ? hs.DisplayName : hs.CharacterName);
                    }
                }
                if (localDay < 0) return;   // we know nothing of this world
                int aheadDays = localDay - p.WorldDay;
                if (aheadDays >= 1)
                    Plugin.Logger.LogInfo($"[MPSave] Joining a rolled-back session: our store knows day {localDay}, session is day {p.WorldDay} ({aheadDays} day(s) earlier) — loading it (standard behavior).");
                // Independent of the day check (review fix 2026-07-23: an 'else if' hid the
                // fork warning in its PRIMARY scenario — a separately-hosted fork is
                // usually days ahead too, so only the routine INFO line ever fired).
                if (p.HostEpoch > 0 && localEpoch > p.HostEpoch)
                    Plugin.Logger.LogWarning($"[MPSave] FORK SUSPECT: our store records a later host-start of this world (epoch {localEpoch}{(string.IsNullOrEmpty(newerHostName) ? "" : $", by {newerHostName}")}) than the joined session (epoch {p.HostEpoch}) — if that newer session is still played elsewhere, this world now has two versions.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] join rollback facts: {ex.Message}"); }
        }

        /// <summary>The "write + load our .hsg" tail of ClientHandleLoadData.</summary>
        private static void ProceedWithLoadData(LoadDataPayload p)
        {
            MPClient.MarkLeftLobby();   // loading now — the lobby pane yields
            MPClient.SendPhaseReport("Loading", "intent: load-data (served world)");   // round-276b: intents name themselves
            MPClient.BeginJoinQuiesce();   // live stream must not touch the load
            string session = p.SessionName;
            lock (_lock) { _activeSessionName = session; }
            try
            {
                byte[] raw    = UnGzipBase64(p.HsgGzipBase64);
                string folder = MPSaveManager.MpCharacterFolder(session, MPConfig.StableId);
                File.WriteAllBytes(Path.Combine(folder, SaveFileName + ".hsg"), raw);
                // Round-275b: land the served sidecar too — a meta-less local copy is
                // undatable by the save scanner (client catalog read day 0; round-262 fired).
                try { if (!string.IsNullOrEmpty(p.MetaJson)) File.WriteAllText(Path.Combine(folder, SaveFileName + ".hsg.meta"), p.MetaJson); } catch { }
                LogHsgWrite(session, MPConfig.StableId, raw.Length, "client received own copy for load");
                Plugin.Logger.LogInfo($"[MPSave] Received .hsg ({raw.Length}B) for session '{session}' — loading.");
                ClearClientDisconnectMarker();   // host resolved our join (incl. any validated disconnect save) — offer consumed
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] ClientHandleLoadData write: {ex}"); return; }

            float money = p.MoneyKnown ? p.Money : float.NaN;   // round-224: NaN = no overlay
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
                    if (LoadOwnHsg(session, MPConfig.StableId)) QueueCashApply(money);
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
                    if (LoadOwnHsg(session, MPConfig.StableId)) QueueCashApply(cash);
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

#if BAMP_DEV
        /// <summary>Support-rig forensics (user-approved 2026-08-18): when set, host-load
        /// resolution loads the NAMED member's character instead of this machine's —
        /// the only way a support machine (whose identity has no slot in a field save)
        /// can open one for reproduction.  Armed/cleared by every TestDrive 'hostload'
        /// (the 'as=<stableId>' form arms it; a plain hostload clears it).  Compiled
        /// out of Release/Debug entirely — retail identity semantics untouched.</summary>
        public static string? DevHostLoadAs;
#endif

        /// <summary>Round-285: consume the dev impersonation override.  It proved STICKY in the
        /// field (2026-08-21): armed for one forensic load, it silently re-keyed every LATER
        /// load's identity check — the user's own saves refused with a stranger's stable id
        /// until a process restart.  Consumed on: a refused pre-start (the trap case), session
        /// Stop, and StartNewGame.  A successful impersonated load deliberately keeps it armed
        /// (the load's own deferred reads still need it); the NEXT load attempt then either
        /// uses it or — if it no longer fits — refuses cleanly and consumes it here.
        /// No-op on non-dev builds (the field only exists under BAMP_DEV).</summary>
        public static void ConsumeDevHostLoadAs(string why)
        {
#if BAMP_DEV
            if (string.IsNullOrEmpty(DevHostLoadAs)) return;
            Plugin.Logger.LogWarning($"[MPSave] dev impersonation override '{DevHostLoadAs}' consumed ({why}) — later loads use this machine's own identity.");
            DevHostLoadAs = null;
#endif
        }

        /// <summary>Round-285: caller-facing pre-start validation — the SAME predicate the load
        /// itself re-runs (ResolveOwnSlot; never a second copy — the derived-counter lesson),
        /// asked BEFORE StartLoadGame burns the lobby latch, so a refusal leaves the lobby
        /// intact instead of wedging Start ("already in flight" forever, live 2026-08-21).
        /// The reason text is player-facing (it lands in the lobby notice), not log jargon.</summary>
        public static bool ValidateOwnSlotForLoad(string session, out string reason)
        {
            // Reason strings are PLAYER-FACING (lobby notice) — short and simple by user
            // ruling 2026-08-21; the detailed forensics stay on the log-only lines.
            reason = "";
            if (MPSaveManager.ReadManifest(session) == null)
            { reason = "no save catalog found."; return false; }
            int rc = ResolveOwnSlot(session, MPConfig.StableId, out var chosen, out string effId);
            if (rc == -1)
            {
                reason = effId == MPConfig.StableId
                    ? "no character found."
                    : $"no character found for '{effId}' (load-as active).";
                return false;
            }
            if (rc == -2)
            {
                // "no save files found." RETIRED (user 2026-08-21): save-less folders are
                // never listed, so this residual (files vanished between listing and click)
                // shows the one approved refusal text; the log keeps the real cause.
                Plugin.Logger.LogWarning($"[MPSave] validate '{session}': zero save files on disk — refusing with the generic notice.");
                reason = "no character found."; return false;
            }
            // C (user-approved 2026-08-21, replaces the full test-open): stream-verify the
            // chosen file's COMPRESSED CONTAINER — .hsg is gzip (SaveGameSerializationHelper
            // .cs:136-139), so decompressing into a discard buffer validates length + CRC end
            // to end at a fraction of a full world deserialize (the old test unpacked the
            // whole world twice per load, on the main thread — verifier defect 6). Catches
            // truncation/byte corruption, the dominant real class; a semantically-broken-but-
            // valid-gzip file slips to the real load's round-251 containment (disclosed
            // trade-off). Json-type saves (dev-only) skip; a nameless entry skips (F16 —
            // never guess a filename the load wouldn't use).
            if (chosen != null && !string.IsNullOrEmpty(chosen.name)
                && chosen.saveGameType != SaveGameManager.SaveGameStruct.SaveGameType.json)
            {
                string file = "";
                try
                {
                    string folder = Path.Combine(MPSaveManager.MpSessionFolder(session), chosen.characterId ?? "");
                    file = Path.Combine(folder, chosen.name + ".hsg");
                    if (File.Exists(file))
                    {
                        using var fs = File.OpenRead(file);
                        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                        var buf = new byte[81920];
                        while (gz.Read(buf, 0, buf.Length) > 0) { }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[MPSave] validate '{session}': own save '{file}' failed container verification ({ex.Message}) — refused before the lobby burns (corrupt/truncated save).");
                    reason = "no character found."; return false;
                }
            }
            return true;
        }

        /// <summary>ONE slot-resolution predicate for BOTH the HostLoadSession precheck
        /// and LoadOwnHsg (2026-08-18: the refused-load-still-mutated-the-manifest defect
        /// needs the answer BEFORE any write, and a second copy of this matching would
        /// drift — the derived-counter lesson).  Honors the Dev impersonation override.
        /// Returns 1 = own slot matched; 0 = legacy single-folder fallback (chosen set);
        /// -1 = refused (no own slot among several member folders); -2 = no saves.</summary>
        private static int ResolveOwnSlot(string session, string stableId,
            out SaveGameManager.SaveGameStruct? chosen, out string effectiveId)
        {
            effectiveId = stableId;
#if BAMP_DEV
            if (!string.IsNullOrEmpty(DevHostLoadAs)) effectiveId = DevHostLoadAs!;
#endif
            chosen = null;
            string sessionFolder = MPSaveManager.MpSessionFolder(session);
            var saves = SaveGamePathHelper.GetAllSaveGamesFromVersion(sessionFolder);
            if (saves == null || saves.Count == 0) return -2;
            string want = Path.GetFileName(MPSaveManager.MpCharacterFolder(session, effectiveId).TrimEnd('\\', '/'));
            for (int i = 0; i < saves.Count; i++)
            {
                // P5 (user-approved 2026-08-21): read the raw folder segment, NEVER the
                // CharacterPath getter — that getter rebuilds a path under the SINGLE-PLAYER
                // store and Directory.CreateDirectory's it (audit F4: junk SP-store folders).
                string seg = saves[i]?.characterId ?? "";
                if (string.Equals(seg, want, StringComparison.OrdinalIgnoreCase)) { chosen = saves[i]; return 1; }
            }
            // Review-2 semantics preserved: a single-folder store keeps the legacy
            // fallback; several folders with no own slot is a refusal.
            if (saves.Count == 1) { chosen = saves[0]; return 0; }
            return -1;
        }

        /// <summary>MAIN THREAD: load this player's .hsg out of the MP session folder.
        /// The game's Load() locates saves by re-scanning CurrentVersionFolderPath();
        /// we briefly redirect that to the MP session folder so Load finds + loads our
        /// save natively — no staging, no single-player-folder pollution. Returns false
        /// when nothing was loaded (review-3 fix: callers must not queue a cash overlay
        /// for a load that never happened — the stale pending cash applied itself to
        /// the NEXT world that went live).</summary>
        private static bool LoadOwnHsg(string session, string stableId)
        {
            string sessionFolder = MPSaveManager.MpSessionFolder(session);
            int rc = ResolveOwnSlot(session, stableId, out var chosen, out string effectiveId);
            if (rc == -2)
            { Plugin.Logger.LogWarning($"[MPSave] LoadOwnHsg: no saves under {sessionFolder}."); return false; }
            // Review-2 fix: never load ANOTHER MEMBER's character because ours is missing —
            // with several character folders in the store (mirrors!) the saves[0] fallback
            // did exactly that (e.g. own self-save failed before hosting a checkpoint).
            if (rc == -1)
            {
                Plugin.Logger.LogError($"[MPSave] LoadOwnHsg: no save for THIS character (stable={effectiveId}) under {sessionFolder} — REFUSING to load another member's character. Pick a different save of this world, or rejoin a session to restore yours.");
                return false;
            }
#if BAMP_DEV
            if (!string.Equals(effectiveId, stableId, StringComparison.Ordinal))
                Plugin.Logger.LogWarning($"[MPSave] DEV IMPERSONATION: loading member '{effectiveId}' instead of '{stableId}' (support-rig forensics; saves during this run write under this machine's own identity).");
#endif

            if (chosen == null) return false;   // rc 0/1 guarantee this; the compiler can't see it
            Plugin.Logger.LogInfo($"[MPSave] Loading .hsg: char='{chosen.characterId}' day={chosen.day} via redirect → {sessionFolder}");
            DiagWrite($"LoadOwnHsg: redirect ON → {sessionFolder}, calling Load");
            // Redirect the game's path resolver to the MP session folder for the
            // duration of the (synchronous) Load re-scan, then restore it.  Tightly
            // gated to this one main-thread call so nothing else sees the redirect.
            LoadRedirectFolder = sessionFolder;
            bool ok;
            try   { ok = GuardedNativeLoad(chosen, true, "member .hsg (LoadOwnHsg)", session); }
            finally { LoadRedirectFolder = null; }
            if (!ok) { DiagWrite("LoadOwnHsg: Load returned FALSE — round-251A containment ran"); return false; }

            // Round-262 (field 20260811-210603): the "Loading .hsg ... day=N" label above
            // comes from the folder CATALOG; the world that actually deserialized can be
            // something else entirely (that field client: label day=31, loaded state day=6
            // — a stale member copy served on every rejoin read as self-consistent in
            // every bundle). Measure at the definitive layer: the loaded world itself.
            // Detect-only; the WARN self-stamps every future bundle of this class.
            try
            {
                int loadedDay = SaveGameManager.Current?.Day ?? -1;
                if (loadedDay >= 0 && chosen.day >= 0 && loadedDay != chosen.day)
                    Plugin.Logger.LogWarning($"[MPSave] LOADED-DAY MISMATCH: catalog said day={chosen.day} but the loaded world is day={loadedDay} — a stale/mismatched member copy was loaded (round-262).");
            }
            catch { }

            PortraitFolder = MPSaveManager.MpCharacterFolder(session, effectiveId);   // = stableId outside Dev impersonation
            DiagWrite("LoadOwnHsg: redirect OFF, Load returned");
            return true;
        }

        // ── Round-251: every native Load we invoke goes through this wrapper ─────
        // Rig batch-2 (T245, 2026-08-13) proved round-245's Finalizer guards the
        // wrong layer: SaveGameManager.Load CATCHES its own deserialize/compat
        // exceptions (SaveGameManager.cs:301-318), logs them, and returns FALSE —
        // with Current already holding the PARTIALLY deserialized world.  Vanilla's
        // own caller honors that bool (TransitionToSave.cs:20-24 → error flag +
        // main menu); our direct calls ignored it and marched a 16-of-29-shops
        // half-world straight to gameplay.  Two detectors here:
        //   A (ENFORCE)      — Load returned false → run the containment (vanilla
        //                      parity: loud error, session stopped, main menu).
        //   B (DETECT-ONLY)  — the save reader printed its cut-off-data complaint
        //                      ("Reading array went wrong") but Load still returned
        //                      true: the fully-silent amputation.  User ruling
        //                      2026-08-13: do NOT block — warn the player loudly so
        //                      they are less inclined to continue, stamp the bug
        //                      report, and leave the decision to them.  Measured
        //                      base rate before shipping: the complaint appears in
        //                      6/6 known torn loads (field 20260805-123035 + both
        //                      rig tears) and 0 times across every healthy field
        //                      and rig log on hand.
        /// <summary>Sticky for bug reports (report.md TornSaveReads line).</summary>
        public static string LastTornRead = "";
        private static string? _pendingTornNotice;

        public static bool GuardedNativeLoad(SaveGameManager.SaveGameStruct save, bool loadScene, string context, string displayName = "")
        {
            // T245 validation finding (2026-08-13): SaveGameStruct.name is the FILE alias —
            // for MP member saves that is literally 'save', useless in a player-facing
            // message. Callers pass the name the player actually knows (session / alias).
            string name = ""; try { name = save?.name ?? ""; } catch { }
            string shown = string.IsNullOrEmpty(displayName) ? name : displayName;
            int torn = 0;
            UnityEngine.Application.LogCallback probe = (cond, _, _) =>
            { try { if (cond != null && cond.Contains("Reading array went wrong")) torn++; } catch { } };
            bool ok = false;
            UnityEngine.Application.logMessageReceived += probe;
            try { ok = SaveGameManager.Load(save, loadScene); }
            finally { UnityEngine.Application.logMessageReceived -= probe; }

            if (!ok)
            {
                Plugin.Logger.LogError($"[MPSave] native Load returned FALSE for '{shown}' (file '{name}'; {context}; torn-read lines during load: {torn}) — "
                    + "the file is damaged or unreadable; running containment instead of continuing on a half-initialized world (round-251A).");
                SaveLoadGuard.ContainFailedLoad(shown);
                return false;
            }
            if (torn > 0)
            {
                LastTornRead = $"torn-read×{torn} '{shown}'";
                Plugin.Logger.LogError($"[MPSave] TORN-READ detected: the save reader hit cut-off data {torn}× while loading '{shown}' ({context}) "
                    + "yet the load reported success — parts of the world may be MISSING. Detect-only by user ruling 2026-08-13 (round-251B): warning the player, not blocking.");
                _pendingTornNotice = $"WARNING: save '{shown}' loaded with damaged data — parts of the world may be missing. "
                    + "Playing on can bake the damage into new saves. Consider loading an earlier save or autosave.";
            }
            return true;
        }

        /// <summary>MAIN THREAD, per-frame (TickPendingUploads): the round-251B warning
        /// is queued at Load time but shown only once the world is actually on screen —
        /// a toast during the load screen is never seen.  Recurring check with a
        /// confirmed exit condition (world present + not loading), then one-shot.</summary>
        private static void TickTornNotice()
        {
            if (_pendingTornNotice == null) return;
            try
            {
                if (SaveGameManager.Current == null || UI.Load.LoadScene.isLoading) return;
                // 30s (user ruling 2026-08-13: hold much longer than the 2s default / 10s
                // important tier).  Toasts STACK as of round-253d (own clock per row;
                // eviction prefers short-lived rows), and the lobby notice below is the
                // durable copy either way.
                try { PassengerHud.Toast(_pendingTornNotice, 30f); } catch { }
                try { MPCanvasUI.PostLobbyNotice(_pendingTornNotice); } catch { }
                Plugin.Logger.LogWarning("[MPSave] torn-read warning shown to the player (30s toast + lobby notice).");
                _pendingTornNotice = null;
            }
            catch { }
        }

        // Deferred cash overlay — applied a couple of seconds after the loaded
        // game goes live (so the load doesn't overwrite it).
        private static float _pendingCashApply;
        private static bool  _hasPendingCash;   // explicit sentinel — a legitimate $0 / negative (overdraft) is a real authoritative balance, not "nothing pending"
        private static int   _cashApplyDwell;

        public static void QueueCashApply(float money)
        {
            // Round-224: NaN = "host has no trustworthy figure" — the .hsg's own wallet stands.
            if (float.IsNaN(money)) { Plugin.Logger.LogInfo("[MPSave] cash overlay skipped — host sent no-overlay; keeping the save's own wallet."); return; }
            _pendingCashApply = money; _hasPendingCash = true; _cashApplyDwell = 0;
        }

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

        // ── Round-184: the ONE save-serving ladder ───────────────────────────────
        internal enum ServeVerdict { Served, Rescued, Unavailable, Fresh }

        /// <summary>The single fallback ladder for serving a member their save — exact session →
        /// lineage rescue → refuse-if-slot-exists → fresh-start-only-if-truly-new.  BOTH serve
        /// paths (world start + mid-session join) resolve through here: this logic existed twice
        /// and a fix landed in only one copy (rig-caught 2026-07-29 — the lobby-start rejoin
        /// still fresh-started after only the mid-join path got the lineage rescue).  On
        /// Served/Rescued, servedFrom names the session actually read and cash is that session's
        /// manifest figure (live CashByStableId fallback).  Callers keep their own
        /// world-identity fields and their own Unavailable/Fresh messaging.</summary>
        internal static ServeVerdict ResolveMemberSave(string sourceSession, string stableId,
            out (string b64, int raw)? data, out string servedFrom, out float cash)
        {
            servedFrom = sourceSession;
            cash = 0f;
            // Round-274c (verifier CONFIRMED-1, rig-reproduced): the DIRECT read is a serve
            // like any other.  After a rollback the mid-join source (the latest save target)
            // can itself be a MARKED sibling still holding a member's gap-window file (the
            // carry-forward keeps newer-mtime files) — unfenced, it served a day-52 character
            // into a day-51 world with the founding 'not the recorded owner' symptom intact.
            // Same test, same verdict: a refused direct read falls to the rescue ladder.
            data = null;
            string? srcHsg = null;
            try { srcHsg = NewestHsg(MPSaveManager.MpCharacterFolder(sourceSession, stableId)); } catch { }
            if (srcHsg != null && IsAbandonedCopy(sourceSession, srcHsg))
                Plugin.Logger.LogWarning($"[MPSave] direct serve source '{sourceSession}' holds a gap-window copy for stable={stableId} — refused (round-274c); the rescue ladder decides.");
            else
                data = ReadSaveBytesGzip(sourceSession, stableId);
            var verdict = ServeVerdict.Served;
            int refusedAbandoned = 0;
            if (data == null)
            {
                // Round-233: fence the rescue by the LOADED session's day — a deliberately
                // rewound world must not hand a joiner their future-timeline character.
                // Round-271 sharpens the fence to the save MOMENT: abandoned-timeline slots
                // (newer than the loaded save at load time) are excluded inside the selector,
                // closing the same-day rollback hole (field 20260816-213224).
                var altPick = LineageNewestEligible(sourceSession, stableId, FenceDayFor(sourceSession), allowUnknownDay: true, out refusedAbandoned);
                string? alt = altPick?.srcSession;
                if (!string.IsNullOrEmpty(alt) && alt != sourceSession)
                {
                    data = ReadSaveBytesGzip(alt!, stableId);
                    if (data != null)
                    {
                        verdict = ServeVerdict.Rescued;
                        servedFrom = alt!;
                        Plugin.Logger.LogWarning($"[MPSave] serve rescue: no .hsg for stable={stableId} in '{sourceSession}' — adopted their newest at-or-before lineage copy from '{alt}' ({data.Value.raw}B). The session was missing this member (interrupted save / save-as without them).");
                    }
                }
            }
            if (data == null && refusedAbandoned > 0)
                Plugin.Logger.LogWarning($"[MPSave] serve for stable={stableId}: refused {refusedAbandoned} abandoned-timeline cop(ies) (rolled-back load, round-271) and no copy at-or-before the loaded save exists — verdict falls through (fresh = remake, per the timeline-coherence ruling 2026-08-17).");
            if (data == null)
            {
                bool hasSlot = false;
                try
                {
                    var mm = MPSaveManager.ReadManifest(sourceSession);
                    hasSlot = mm?.Slots != null && mm.Slots.Exists(s => s.StableId == stableId);
                }
                catch { }
                // Round-274c: a Fresh verdict under an active fence = the timeline-coherence
                // remake — record it so the blank-save guard lets the new (small) character
                // save replace the retained abandoned copies instead of resurrecting them.
                if (!hasSlot && RollbackFenceActive) RecordRemadeUnderFence(stableId);
                return hasSlot ? ServeVerdict.Unavailable : ServeVerdict.Fresh;
            }
            try
            {
                var sm = MPSaveManager.ReadManifest(servedFrom);
                cash = sm != null ? BestCashFor(sm, stableId)
                     : (MPServer.CashByStableId.TryGetValue(stableId, out var c) ? c : 0f);
            }
            catch { }
            return verdict;
        }

        /// <summary>Round-275b: the .hsg.meta sidecar next to a member's newest save in a
        /// session folder ("" if absent) — served with LoadData/mirrors so the receiving
        /// machine's copy stays datable by the save scanner.</summary>
        internal static string ReadMemberMetaJson(string session, string stableId)
        {
            try
            {
                var f = NewestHsg(MPSaveManager.MpCharacterFolder(session, stableId));
                if (f != null && File.Exists(f + ".meta")) return File.ReadAllText(f + ".meta");
            }
            catch { }
            return "";
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

        // ── Handoff slice 1 (2026-07-23): session-store mirror ───────────────────
        // The session store (manifest + EVERY member's .hsg) is operationally ONE
        // SAVE — an atomic snapshot of the world and all characters, including
        // members offline at save time (carry-forward). The host replicates it to
        // every connected member at each coordinated save, so any member can host
        // the world later with full fidelity, and a character survives as long as
        // ANY member's mirror survives. Two channels:
        //   MirrorStoreSweep — after the host's own save: manifest + the host's
        //     .hsg + carried-forward ABSENT members' .hsg. Connected clients' own
        //     files are skipped here — their fresh upload is seconds away and
        //     mirrors incrementally when it lands.
        //   MirrorMemberFile — after a member's .hsg lands on the host (coordinated
        //     upload / accepted disconnect save): that one file to everyone else.
        // A member NEVER receives its own .hsg back (its local copy is written by
        // its own save), and a receiver sharing the host's physical store folder
        // (dual-instance testing on one machine) skips applying entirely — matched
        // by HostStoreToken. All pure file/JSON IO + sends — safe off the main thread.

        /// <summary>Hash of this MACHINE + MP store root — identifies a shared physical
        /// store without disclosing the path (it contains a username). The machine name
        /// is part of the hash (review fix 2026-07-23): two DIFFERENT machines whose
        /// Windows usernames match produce identical store paths, and a path-only token
        /// silently disabled mirroring for that pair.</summary>
        internal static string StoreToken()
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA1.Create();
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    Environment.MachineName + "|" + MPSaveManager.MpVersionFolder().ToLowerInvariant()));
                return Convert.ToBase64String(bytes);
            }
            catch { return ""; }
        }

        /// <summary>The session's manifest as JSON + its world identity, preferring the
        /// in-memory active copy. Read under _lock so a concurrent MergeSlot/WriteManifest
        /// can't tear it.</summary>
        private static (string json, string pid) ManifestSnapshot(string session)
        {
            lock (_lock)
            {
                var m = (_activeManifest != null && _activeSessionName == session)
                        ? _activeManifest : MPSaveManager.ReadManifest(session);
                return m == null ? ("", "") : (Newtonsoft.Json.JsonConvert.SerializeObject(m), m.PlaythroughId ?? "");
            }
        }

        /// <summary>The live loan ledger as JSON for the mirror's legacy channel.  Since the
        /// sweep 2026-08-18 loans authoritatively ride the MANIFEST (which mirrors alongside);
        /// this channel keeps the receiver-side loans.bamp.json fallback current for stores
        /// whose manifests predate loan tracking. "" = none.</summary>
        private static string LedgerJsonSnapshot(string session)
        {
            try
            {
                var loans = MPHub.SnapshotLoans();
                if (loans.Count == 0) return "";
                return Newtonsoft.Json.JsonConvert.SerializeObject(new LoanStatePayload { Loans = loans });
            }
            catch { return ""; }
        }

        /// <summary>HOST: manifest-only mirror (ledger change between saves — KBs).</summary>
        private static void MirrorManifestOnly(string session, string manifestJson, string pid)
        {
            if (!MPServer.IsRunning || string.IsNullOrEmpty(manifestJson)) return;
            MPServer.SendStoreMirror(new StoreMirrorPayload
            {
                SessionName = session, ManifestJson = manifestJson, PlaythroughId = pid,
                LedgerJson = LedgerJsonSnapshot(session), HostStoreToken = StoreToken(),
            }, exceptStable: "");
        }

        /// <summary>HOST: mirror the manifest + the host's own and every ABSENT member's
        /// .hsg for one session to all connected clients.</summary>
        public static void MirrorStoreSweep(string session)
        {
            if (!MPServer.IsRunning || string.IsNullOrEmpty(session)) return;
            try
            {
                string token = StoreToken();
                var (manifestJson, pid) = ManifestSnapshot(session);
                if (!string.IsNullOrEmpty(manifestJson))
                    MPServer.SendStoreMirror(new StoreMirrorPayload
                    {
                        SessionName = session, ManifestJson = manifestJson, PlaythroughId = pid,
                        LedgerJson = LedgerJsonSnapshot(session), HostStoreToken = token,
                    }, exceptStable: "");

                string sessionFolder = MPSaveManager.MpSessionFolder(session);
                if (string.IsNullOrEmpty(sessionFolder) || !Directory.Exists(sessionFolder)) return;
                var connected = MPServer.ConnectedStableIds();
                int sent = 0;
                foreach (var dir in Directory.GetDirectories(sessionFolder))
                {
                    string stable = Path.GetFileName(dir);
                    if (!stable.StartsWith("guid-") && !stable.StartsWith("steam-")) continue;   // character folders only (same filter as carry-forward)
                    if (stable != MPConfig.StableId && connected.Contains(stable)) continue;     // their fresh upload mirrors itself
                    try
                    {
                        string? file = NewestHsg(dir);
                        if (file == null) continue;
                        byte[] raw = File.ReadAllBytes(file);
                        if (raw.Length == 0) continue;
                        string sweepMeta = ""; try { if (File.Exists(file + ".meta")) sweepMeta = File.ReadAllText(file + ".meta"); } catch { }
                        MPServer.SendStoreMirror(new StoreMirrorPayload
                        {
                            SessionName = session, StableId = stable,
                            SaveName = Path.GetFileNameWithoutExtension(file),
                            HsgGzipBase64 = GzipBase64(raw), RawLength = raw.Length,
                            MetaJson = sweepMeta,   // round-275b
                            PlaythroughId = pid,
                            HostStoreToken = token,   // manifest already sent above
                        }, exceptStable: stable);
                        sent++;
                    }
                    // One locked/mid-write file (e.g. a disconnect commit landing right
                    // now) must not abort the rest of the sweep — skip it; the next
                    // save re-mirrors.
                    catch (IOException ex) { Plugin.Logger.LogWarning($"[MPSave] sweep skip '{stable}': {ex.Message}"); }
                }
                Plugin.Logger.LogInfo($"[MPSave] Store mirror sweep '{session}': manifest + {sent} member file(s) → clients.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] MirrorStoreSweep '{session}': {ex.Message}"); }
        }

        /// <summary>HOST: one member's fresh .hsg just landed — mirror it (+ current
        /// manifest) to every client EXCEPT that member.</summary>
        public static void MirrorMemberFile(string session, string stable)
        {
            if (!MPServer.IsRunning || string.IsNullOrEmpty(session) || string.IsNullOrEmpty(stable)) return;
            try
            {
                string dir = MPSaveManager.MpCharacterFolder(session, stable);
                string? file = NewestHsg(dir);
                if (file == null) return;
                byte[] raw = File.ReadAllBytes(file);
                if (raw.Length == 0) return;
                var (manifestJson, pid) = ManifestSnapshot(session);
                string mmMeta = ""; try { if (File.Exists(file + ".meta")) mmMeta = File.ReadAllText(file + ".meta"); } catch { }
                MPServer.SendStoreMirror(new StoreMirrorPayload
                {
                    SessionName = session, StableId = stable,
                    SaveName = Path.GetFileNameWithoutExtension(file),
                    HsgGzipBase64 = GzipBase64(raw), RawLength = raw.Length,
                    MetaJson = mmMeta,   // round-275b
                    ManifestJson = manifestJson, PlaythroughId = pid,
                    HostStoreToken = StoreToken(),
                }, exceptStable: stable);
                Plugin.Logger.LogInfo($"[MPSave] Mirrored member file (stable={stable}, {raw.Length}B) for '{session}' → other clients.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] MirrorMemberFile '{stable}': {ex.Message}"); }
        }

        /// <summary>Session names already warned about a lineage clash — one line per run.</summary>
        private static readonly HashSet<string> _mirrorClobberWarned = new();

        /// <summary>CLIENT: the world identity of the session we joined, learned over the
        /// wire (LoadDataPayload.PlaythroughId). Sticky until the next join — the disconnect
        /// marker for the session just played stamps it (review-2 fix).</summary>
        private static volatile string _wireWorldPid = "";

        /// <summary>CLIENT: a piece of the session store arrived — write it into our
        /// LOCAL store at the same paths, so this machine holds the complete session
        /// ("single save") and can host the world later. Our OWN .hsg is never applied
        /// (our save writes it); path components are sanitized like every other
        /// network-supplied name. Network thread; pure file/JSON IO.</summary>
        public static void ClientHandleStoreMirror(StoreMirrorPayload p)
        {
            if (p == null || MPServer.IsRunning) return;   // client role only
            try
            {
                // Same-machine guard: host + client sharing ONE SaveGames folder
                // (dual-instance testing) — the files are already there, and writing
                // them here would race the host's own writes.
                string ownToken = StoreToken();
                if (!string.IsNullOrEmpty(p.HostStoreToken) && p.HostStoreToken == ownToken) return;

                string session = SanitizeSession(p.SessionName);
                if (string.IsNullOrEmpty(session)) return;

                if (MPSaveManager.StoreFormat() == 2 && !string.IsNullOrEmpty(p.PlaythroughId))
                {
                    // Store v2: the payload names its world — pin BOTH the piece's session
                    // and its family base so every write below lands in <pid>/<session>.
                    // A same-named world in another playthrough is a different folder,
                    // so the v1 collision cannot occur (decision F: host overwrites
                    // propagate; no refusal path for same-lineage updates).
                    MPSaveManager.NoteSessionPid(session, p.PlaythroughId);
                    MPSaveManager.NoteSessionPid(StripAutoSuffix(session), p.PlaythroughId);
                    // Corruption assert, LOUD per decision F: a manifest already at the
                    // target path claiming a DIFFERENT world means the store itself is
                    // damaged — refuse and say so on screen, never silently.
                    string atTarget = "";
                    try { atTarget = MPSaveManager.ReadManifest(session)?.PlaythroughId ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(atTarget) && atTarget != p.PlaythroughId)
                    {
                        Plugin.Logger.LogWarning($"[MPSave] Store mirror BLOCKED for '{session}': the folder for world {p.PlaythroughId} holds a manifest claiming world {atTarget} — save store corruption. Nothing was overwritten; report this.");
                        try { MPCanvasUI.PostLobbyNotice($"Save-store problem detected for '{session}' — shared copy NOT updated. Please send a bug report."); } catch { }
                        return;
                    }
                }
                // v1 clobber guard (review fix 2026-07-23), pre-migration stores only: a
                // SAME-NAMED local session that belongs to a DIFFERENT world must never be
                // overwritten — a mirror only applies to its own lineage.
                else if (!string.IsNullOrEmpty(p.PlaythroughId))
                {
                    string localPid = "";
                    try { localPid = MPSaveManager.ReadManifest(session)?.PlaythroughId ?? ""; } catch { }
                    // Lineage fallback (review-2 fix): a piece for a sibling we don't hold
                    // locally (e.g. '-auto-4') must still be judged against the FAMILY's
                    // identity — without this it slipped past the guard, and its ledger
                    // write below landed in the shared BASE folder of the other world.
                    if (string.IsNullOrEmpty(localPid))
                        try { localPid = InheritedPlaythroughId(session) ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(localPid) && localPid != p.PlaythroughId)
                    {
                        if (_mirrorClobberWarned.Add(session))
                        {
                            Plugin.Logger.LogWarning($"[MPSave] Store mirror REFUSED for '{session}': a local world with this name already exists (different lineage). Rename one of the worlds for this machine to receive the shared copy.");
                            // Decision F (2026-08-01): a refusal the player can't see reads as
                            // "the transfer never arrived" — say it on screen too.
                            try { MPCanvasUI.PostLobbyNotice($"Shared save NOT stored: you already have a different world named '{session}'. Rename one of them."); } catch { }
                        }
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(p.ManifestJson))
                {
                    var m = Newtonsoft.Json.JsonConvert.DeserializeObject<MpManifest>(p.ManifestJson);
                    if (m != null) MPSaveManager.WriteManifest(session, m);
                }

                // Loan ledger rides the sweep's manifest piece (review fix 2026-07-23) —
                // written at the lineage BASE folder, where MPHub loads it from.
                if (!string.IsNullOrEmpty(p.LedgerJson))
                {
                    string baseFolder = MPSaveManager.MpSessionFolder(StripAutoSuffix(session));
                    Directory.CreateDirectory(baseFolder);
                    File.WriteAllText(Path.Combine(baseFolder, MPHub.LedgerFileName), p.LedgerJson);
                }

                if (!string.IsNullOrEmpty(p.StableId) && p.StableId != MPConfig.StableId
                    && !string.IsNullOrEmpty(p.HsgGzipBase64))
                {
                    byte[] raw = UnGzipBase64(p.HsgGzipBase64);
                    if (p.RawLength > 0 && raw.Length != p.RawLength)
                        Plugin.Logger.LogWarning($"[MPSave] Store mirror length mismatch (stable={p.StableId}): got {raw.Length}, expected {p.RawLength}.");
                    if (raw.Length > 0)
                    {
                        string dir  = MPSaveManager.MpCharacterFolder(session, p.StableId);   // sanitizes the id component
                        string name = MPSaveManager.Sanitize(string.IsNullOrEmpty(p.SaveName) ? SaveFileName : p.SaveName);
                        File.WriteAllBytes(Path.Combine(dir, name + ".hsg"), raw);
                        // Round-275b: the sidecar rides the mirror — a handoff host serving from
                        // this mirrored store needs datable copies for its own day validation.
                        try { if (!string.IsNullOrEmpty(p.MetaJson)) File.WriteAllText(Path.Combine(dir, name + ".hsg.meta"), p.MetaJson); } catch { }
                        LogHsgWrite(session, p.StableId, raw.Length, "store mirror");
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ClientHandleStoreMirror: {ex.Message}"); }
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
        /// <summary>Sweep item 5: cause of the most recent failed native-save attempt — set by
        /// every failure path in the retry loop, cleared per save, appended to the SAVE FAILED
        /// line so cause and consequence land together in field bundles.</summary>
        private static string _lastSaveFailReason = "";
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
        /// <summary>Round-127 — EVERY .hsg write announces itself in one greppable form, and a write that lands
        /// on a MANUAL BASE session says so loudly.  Reason: the user reported "my save keeps drifting into the
        /// future even though I haven't saved", and it took a disk inspection to answer — the log accounted for
        /// the -auto/-recover/-disconnect writes but not for the one that touched the base.  It turned out to be
        /// a MEMBER's character file (their durable session pointer is deliberately kept on the base, so their
        /// menu-exit upload lands there) plus the base manifest being rewritten with the current world day —
        /// the owner's own character file was untouched.  That should have been one grep, not an archaeology
        /// session.  Two prior leaks of exactly this shape were fixed in round-37; this makes the next one
        /// self-evident instead of invisible.</summary>
        private static void LogHsgWrite(string session, string stableId, int bytes, string why)
        {
            try
            {
                string baseName = StripAutoSuffix(session ?? "");
                bool isBase = !string.IsNullOrEmpty(session) && session == baseName;
                string line = $"[MPSave] .hsg WRITE session='{session}' char='{stableId}' {bytes}B ({why})";
                if (isBase) Plugin.Logger.LogWarning(line + " — this is the MANUAL BASE session, not an automatic sibling.");
                else        Plugin.Logger.LogInfo(line + ".");
            }
            catch { }
        }

        public static MpSlot PerformLocalSave(string sessionName, SaveGameManager.SaveType saveType = SaveGameManager.SaveType.Default)
            => PerformLocalSave(sessionName, out _, saveType);

        /// <summary>Round-237 overload: <paramref name="saved"/> reports whether the .hsg was
        /// actually written.  Every caller that ADVERTISES or SHIPS the result (upload queue,
        /// manifest slot, disconnect marker, inline exit-send) must check it — on a failed save
        /// the folder still holds the PREVIOUS .hsg, and shipping that under today's Day stamp
        /// is stale data wearing a fresh label (it also defeats the round-233 manifest-day fence).</summary>
        public static MpSlot PerformLocalSave(string sessionName, out bool saved, SaveGameManager.SaveType saveType = SaveGameManager.SaveType.Default)
        {
            DiagArm();
            DiagWrite($"PerformLocalSave START session='{sessionName}' host={MPServer.IsRunning}");
            // Round-114: time the whole save.  We block the main thread across serialization on purpose
            // (see the JoinSaveGameThreads comment below), so however long the underlying write takes is
            // exactly how long the game is frozen — no render, no input.  Field 2026-07-26 ('Crazygamers'):
            // twenty saves that session ran 75-348ms, the twenty-first took 39,868ms, one frame lasted 51s,
            // and the session ended with no crash dump — i.e. the player almost certainly killed a game they
            // had every reason to think had hung.  That stall was environmental (the GAME's own write to a
            // temp file; >20x the worst save in ~35 collected reports) and nothing here can prevent it, but
            // it should never again have to be INFERRED from a perf line and a diagnostic timer.
            var saveClock = System.Diagnostics.Stopwatch.StartNew();
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
            // Round-68, same choke point: ActiveVehicleId must never persist a BAMP_ ghost-proxy id
            // (live save artifact 2026-07-24: the host saved "using" the client's flatbed proxy; on the
            // next load IsUsingVehicle=true + GetCurrentVehicle()=null → HasPaidForAllItems NREs →
            // ExitZoneDespawner dies on every exit attempt (player trapped in the building) AND the
            // possession check silently discards the owner's position packets for that ghost (the
            // "cart stopped syncing" freeze).  Strip for the serialization only — the player may be
            // LEGITIMATELY pushing the borrowed ghost right now, so the live value is restored in the
            // finally.  Borrowed vehicles never follow a save across sessions; "on foot" is the
            // correct persisted state.
            string ghostActiveId = "";
            try
            {
                var giAv = SaveGameManager.Current;
                string av = giAv?.ActiveVehicleId ?? "";
                if (av.StartsWith("BAMP_") && !av.StartsWith("BAMP_TESTRIG"))
                {
                    ghostActiveId = av;
                    giAv!.ActiveVehicleId = null;
                    Plugin.Logger.LogInfo($"[Vehicle] ghost ActiveVehicleId '{av}' stripped for save (restored after serialization).");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ActiveVehicleId strip: {ex.Message}"); }
            string charName = "";
            int    day      = 0;
            // Per-player subfolder keyed by the STABLE id (not the game's characterId) so load can find it
            // deterministically by identity.  charName/day/folder/ok are declared BEFORE the try so the log +
            // the returned slot below can still read them — and so the finally that restores the synthetics
            // ALWAYS runs even if the save work throws (a failed save must never leave the session un-staffed).
            string folder   = MPSaveManager.MpCharacterFolder(sessionName, MPConfig.StableId);
            bool   ok        = false;
            // Round-245 note: a post-write integrity verify (backup + gunzip check + restore)
            // was built here and REMOVED by user decision 2026-08-14 — a transient read failure
            // (AV lock on the fresh file) could have rolled a GOOD save back to the older copy,
            // and the auto-save rotation is the intended failsafe for torn files.  The load-side
            // containment (SaveLoadGuard, fix A) is the shipped answer to damaged .hsg files.
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
                _lastSaveFailReason = "";
                for (int attempt = 0; attempt < 3 && !ok; attempt++)
                {
                    if (attempt > 0)
                    {
                        Plugin.Logger.LogWarning($"[MPSave] save attempt {attempt} failed — retrying in 1.2s.");
                        try { System.Threading.Thread.Sleep(1200); } catch { }
                    }
                    try
                    {
                        ok = SaveGameManager.Save(saveType, SaveFileName, folder);
                        if (!ok)
                        {
                            // Sweep item 5 (user-approved, log-only): Save's only KNOWN silent-false
                            // path is CanSave() — re-evaluate the blockers NOW so the log separates
                            // "a blocker appeared between our pre-check and the save" (decide-then-act
                            // gap) from an unknown native false path (a new finding).
                            string blocked = "";
                            try { blocked = SaveBlockedBy() ?? ""; } catch { }
                            _lastSaveFailReason = blocked.Length > 0
                                ? $"returned false; SaveBlockedBy now reports '{blocked}' (blocker appeared mid-save)"
                                : "returned false with NO known blocker active (unknown native false path — worth a report)";
                            Plugin.Logger.LogWarning($"[MPSave] SaveGameManager.Save {_lastSaveFailReason}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Sweep item 5: message-only logging flattened distinct field failure classes
                        // ("Sequence contains no elements", native NREs, temp-file 0x2038340) into
                        // indistinguishable one-liners — the TYPE + STACK names the subsystem that died.
                        _lastSaveFailReason = $"threw {ex.GetType().Name}: {ex.Message}";
                        Plugin.Logger.LogWarning($"[MPSave] SaveGameManager.Save threw ({ex.GetType().Name}): {ex}");
                    }
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
                // Round-68: put the live borrowed-ghost state back (serialization is done by here).
                try { if (ghostActiveId.Length > 0) SaveGameManager.Current.ActiveVehicleId = ghostActiveId; }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] restore ActiveVehicleId: {ex.Message}"); }
            }

            // Measure the real on-disk size — a hardcoded 0 here read as a failed write in field
            // log analysis (bundle 20260811-225015); the file is complete once JoinSaveGameThreads returned.
            int ownBytes = 0;
            try { string? ownHsg = ok ? NewestHsg(folder) : null; if (ownHsg != null) ownBytes = (int)new FileInfo(ownHsg).Length; } catch { }
            LogHsgWrite(sessionName, MPConfig.StableId, ownBytes, $"own save via {saveType} (ok={ok}, day={day})");
            Plugin.Logger.LogInfo($"[MPSave] Local save '{sessionName}': ok={ok} char='{charName}' day={day} → {folder}");
            // Round-114: 5s is far outside anything healthy — the worst save across ~35 field reports was
            // 1.9s and a normal one is ~100-400ms — so this only fires when the game really did lock up.
            saveClock.Stop();
            if (saveClock.ElapsedMilliseconds >= 5000)
                Plugin.Logger.LogWarning($"[MPSave] SLOW SAVE: '{sessionName}' took {saveClock.ElapsedMilliseconds / 1000f:F1}s. "
                    + "The game was FROZEN for that whole time (we hold the main thread across serialization so nothing can "
                    + "corrupt the save). A healthy save here is ~0.1-0.4s, so this is the disk/antivirus/OS stalling the "
                    + "game's own write — not the save failing. If a player reports a freeze or a 'crash' around this "
                    + "timestamp, THIS is it, and they most likely force-quit a game that would have recovered.");
            // 4a diagnostic: a failed save is the upstream cause of most "lost progress" reports — make it
            // LOUD so it stands out in a submitted log (the routine line above is INFO).
            if (!ok) Plugin.Logger.LogError($"[MPSave] SAVE FAILED for '{sessionName}' (char='{charName}', day={day}) — .hsg not written; a later load may fall back to an older copy." +
                (string.IsNullOrEmpty(_lastSaveFailReason) ? "" : $" Last attempt: {_lastSaveFailReason}."));

            saved = ok;
            return new MpSlot
            {
                StableId      = MPConfig.StableId,
                DisplayName   = MPConfig.PlayerId,
                CharacterName = charName,
                CharacterId   = MPConfig.StableId,   // folder is keyed by stable id
                SaveName      = SaveFileName,
                IsHost        = MPServer.IsRunning,
                Day           = day,
                Money         = LocalWalletOr0(),   // round-224: kill the $0 placeholder at birth
            };
        }

        /// <summary>Phase 3 tamper tolerance: a disconnect save may be at most this many in-game days past
        /// the host's CURRENT world day before it's rejected as edited (a small window absorbs a legit
        /// midnight crossing in the un-synced final minutes without false-positives).</summary>
        public const int DisconnectDayWindow = 2;

        /// <summary>Round-216: make the playthrough identity exist BEFORE any save-path
        /// probe. The first save of a NEW world used to reach the rotation/lineage
        /// probes with no active pid — every probed name minted its own folder (14 on
        /// the rig, 2026-08-01) and the world scattered across them. Resolution order:
        /// live id → v2 disk family → sibling manifest inheritance → mint (genuinely
        /// new world). Cheap when already resolved.</summary>
        private static void EnsureActivePid(string session)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_activePlaythroughId))
                {
                    string baseName = StripAutoSuffix(session);
                    string pid = "";
                    try { pid = MPSaveManager.FindFamilyPidOnDisk(baseName); } catch { }
                    if (string.IsNullOrEmpty(pid))
                        try { pid = InheritedPlaythroughId(session) ?? ""; } catch { }
                    if (string.IsNullOrEmpty(pid))
                    {
                        pid = Guid.NewGuid().ToString("N");
                        Plugin.Logger.LogInfo($"[MPSave] New world '{baseName}' — playthrough {pid} minted at first save.");
                    }
                    _activePlaythroughId = pid;
                }
                MPSaveManager.SetActivePlaythrough(_activePlaythroughId, StripAutoSuffix(session));
                MPSaveManager.NoteSessionPid(StripAutoSuffix(session), _activePlaythroughId);
            }
        }

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
        /// thread-safe. User-approved 2026-08-21 (revised after verification): occupied slots
        /// are aged by their newest save file's DISK time — host-clock by construction (member
        /// uploads are written by the host at receive time) — never the catalog's SavedAtUnix
        /// claim, and never the .meta sidecar's author-clock claim (a fast peer clock made
        /// their slot dodge rotation forever; verifier defect 8). A slot folder holding zero
        /// save files counts as empty (same doctrine as the picker).</summary>
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
                    long when = -1;   // -1 = probe failed (fall back to folder mtime); 0 = probed clean, no files
                    try
                    {
                        when = 0;
                        foreach (var f in Directory.GetFiles(dir, "*.hsg", SearchOption.AllDirectories))
                        { long u = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeSeconds(); if (u > when) when = u; }
                        if (when == 0)
                            foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                            {
                                if (f.EndsWith(".bamp.json", StringComparison.OrdinalIgnoreCase)) continue;   // catalogs are not saves
                                long u = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeSeconds(); if (u > when) when = u;
                            }
                    }
                    catch { when = -1; }
                    if (when == 0) return suf;   // folder exists but holds no save files — effectively empty
                    if (when < 0) when = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir)).ToUnixTimeSeconds();
                    if (when < bestWhen) { bestWhen = when; bestSuf = suf; }
                }
                catch { }
            }
            return bestSuf;   // all slots taken → overwrite the oldest by real file age
        }

        /// <summary>HOST: make an automatic checkpoint (autosave / disconnect) a COMPLETE roster snapshot.
        /// For every player who has a save in this session lineage (base / -auto / -disconnect) but is NOT
        /// currently connected (so they won't upload fresh this round — e.g. the member who just left), copy
        /// their NEWEST save into <paramref name="targetSession"/> and merge their slot. Each member resumes
        /// their OWN latest within-session save — manifest-reconciled, no cross-session desync. Connected
        /// members are skipped (they save themselves fresh; skipping also avoids racing their incoming
        /// upload). Pure file/JSON IO — safe off the main thread.</summary>
        /// <summary>Round-184: every session name that can hold this WORLD's files — the same-base
        /// automatic siblings plus every session sharing the world's PlaythroughId (save-as
        /// renames, forks).  Extracted from CarryForwardAbsentMembers so the mid-join lineage
        /// rescue (fix 2) resolves the identical set.  Rotation slots swept to a fixed 10 (the
        /// setting's cap) rather than the live slot count — a lowered setting must not hide
        /// members whose newest save sits in a now-out-of-range slot; missing folders are
        /// skipped by every consumer.</summary>
        internal static List<string> LineageSessions(string aroundSession)
        {
            var lineage = new List<string>();
            string baseName = StripAutoSuffix(aroundSession);
            lineage.Add(baseName);
            lineage.Add(baseName + "-auto");
            for (int slot = 2; slot <= 10; slot++) lineage.Add(baseName + "-auto-" + slot);
            lineage.Add(baseName + "-disconnect");
            lineage.Add(baseName + "-recover");
            try
            {
                string pid = MPSaveManager.ReadManifest(aroundSession)?.PlaythroughId
                          ?? MPSaveManager.ReadManifest(baseName)?.PlaythroughId ?? "";
                if (!string.IsNullOrEmpty(pid))
                    foreach (var (name, m) in MPSaveManager.ListSessions())
                        if (m != null && m.PlaythroughId == pid && !lineage.Contains(name))
                            lineage.Add(name);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] lineage scan: {ex.Message}"); }
            return lineage;
        }

        /// <summary>Round-184 fix 2: the session holding the NEWEST .hsg for a member anywhere in
        /// the world lineage — the rescue source when the loaded session is missing their save
        /// (rig-proven TEST184-INT2: an interrupted save fresh-started the rejoining client, whose
        /// now-authoritative empty save then blanked their developed buildings on both machines).
        /// Round-233: fenced — pass maxDay to refuse copies from a FUTURE timeline (a deliberate
        /// rewind must not pull the old timeline's files into a rolled-back world).</summary>
        // (Round-274/L1: the FindNewestLineageSessionWithHsg wrapper was removed — zero
        //  callers remained after round-271 pointed ResolveMemberSave at the selector.)

        // ── Round-271: abandoned-timeline registry (rollback loads) ──────────────────
        // The user's save rule: a save is ONE conceptual file — a member substituted into
        // a loaded slot may come from AT OR BEFORE that slot's moment, never its future.
        // Wall-clock stamps cannot order timelines once the rolled-back world re-saves
        // (its fresh stamps postdate the abandoned copies), so the set is captured ONCE,
        // at load, against the loaded slot's stamp.  A sibling leaves the set when the
        // rotation re-saves it — its stamp changes, its bytes are current-timeline again.
        /// <summary>session → its manifest stamp at load time, for lineage sessions that
        /// were NEWER than the loaded slot (plus, round-274/M2: unknown-stamp siblings,
        /// fail-closed, once a rollback is detected).  Membership is for the life of the
        /// loaded world; ELIGIBILITY is decided per member FILE (see IsAbandonedCopy) —
        /// round-274/H2: a slot-level stamp cannot vouch for member files the rotation
        /// never rewrote, so "re-saved slot = trustworthy again" was false at member
        /// granularity (rig-proven: an offline member's stale .hsg laundered through).</summary>
        private static readonly Dictionary<string, long> _abandonedAtLoad = new();
        /// <summary>Wall-clock of the rollback load that built the registry (0 = no
        /// rollback fence active), and the LOADED slot's own save stamp at that moment.
        /// Together they bound the abandoned window: (loadedStamp .. captureUnix].</summary>
        private static long _abandonedCaptureUnix;
        private static long _loadedStampAtCapture;
        /// <summary>Round-274/C1: members this host has SERVED (LoadData or fresh-start)
        /// since the rollback load.  A disconnect save arriving from a member NOT in this
        /// set was written before the rollback — old-timeline bytes the day window cannot
        /// see (rig-reproduced: same-day rollback passed every guard and the commit
        /// destroyed the slot's coherent copy before the fenced ladder ever ran).</summary>
        private static readonly HashSet<string> _servedSinceLoad = new();

        internal static void MarkServedSinceLoad(string stable)
        {
            if (string.IsNullOrEmpty(stable)) return;
            lock (_abandonedAtLoad) _servedSinceLoad.Add(stable);
        }

        private static bool WasServedSinceLoad(string stable)
        {
            lock (_abandonedAtLoad) return _servedSinceLoad.Contains(stable);
        }

        // Round-274c (verifier CONFIRMED-3): a member the fence ruled Fresh legitimately
        // uploads a SMALL save next — but the reused slot still holds their big abandoned
        // copy, and the blank-save guard refused the remake, so the abandoned character
        // outlived the ruling (rig: 104745B refused against a retained 222965B).  While
        // the fence lives, remade members are exempt from that guard.  Cleared with the
        // fence (same lifetime, same lock).
        private static readonly HashSet<string> _remadeUnderFence = new();

        private static void RecordRemadeUnderFence(string stable)
        {
            if (string.IsNullOrEmpty(stable)) return;
            lock (_abandonedAtLoad)
                if (_remadeUnderFence.Add(stable))
                    Plugin.Logger.LogInfo($"[MPSave] stable={stable} remade under the rollback fence — their smaller uploads replace retained abandoned copies (round-274c).");
        }

        internal static bool IsRemadeUnderFence(string stable)
        {
            lock (_abandonedAtLoad) return _remadeUnderFence.Contains(stable);
        }

        private static bool RollbackFenceActive { get { lock (_abandonedAtLoad) return _abandonedCaptureUnix > 0; } }

        private static void CaptureAbandonedTimeline(string loadedSession, MpManifest loadedManifest)
        {
            lock (_abandonedAtLoad)
            {
                _abandonedAtLoad.Clear();
                _abandonedCaptureUnix = 0;
                _loadedStampAtCapture = 0;
                _servedSinceLoad.Clear();
                _remadeUnderFence.Clear();
                long loadedStamp = loadedManifest?.SavedAtUnix ?? 0;
                if (loadedStamp <= 0) return;   // unstamped (legacy) manifest — no reference moment; the day fence still applies
                try
                {
                    var unknown = new List<string>();
                    foreach (var s in LineageSessions(loadedSession))
                    {
                        if (s == loadedSession) continue;
                        // F4 (reviewer): rotation-name candidates with no folder are not siblings —
                        // marking them inflated the evidence line ("12 marked" where 5 exist).
                        bool exists = false; try { exists = Directory.Exists(MPSaveManager.MpSessionFolder(s)); } catch { }
                        if (!exists) continue;
                        long st = 0; bool failedStamp = false;
                        try { var sm = MPSaveManager.ReadManifest(s); st = sm?.SavedAtUnix ?? 0; failedStamp = sm?.LastHostSaveFailed ?? false; } catch { }
                        if (st > loadedStamp) _abandonedAtLoad[s] = st;
                        // age unknowable (minted/legacy/manifest-less) — or the stamp predates a
                        // FAILED host save whose member uploads still landed (B, 2026-08-21):
                        // both fall into the fail-closed net below.
                        else if (st <= 0 || failedStamp) unknown.Add(s);
                    }
                    // Round-274/M2 (fail-closed): once ANY sibling proves this is a rollback,
                    // unknown-age siblings cannot be trusted either — mark them; the per-file
                    // mtime test still admits anything genuinely written after the load.
                    if (_abandonedAtLoad.Count > 0)
                        foreach (var s in unknown) _abandonedAtLoad[s] = 0;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] abandoned-timeline capture: {ex.Message}"); }
                if (_abandonedAtLoad.Count > 0)
                {
                    _abandonedCaptureUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _loadedStampAtCapture = loadedStamp;
                    var names = new System.Text.StringBuilder();
                    foreach (var k in _abandonedAtLoad.Keys) { if (names.Length > 0) names.Append(", "); names.Append('\'').Append(k).Append('\''); }
                    Plugin.Logger.LogWarning($"[MPSave] rollback load: {_abandonedAtLoad.Count} sibling(s) marked abandoned-timeline (round-271/274): {names} — a marked sibling's member file is refused unless the file itself was rewritten after this load.");
                }
            }
        }

        /// <summary>Round-274/H2 + F2 fix: file-granular fence.  The abandoned timeline is
        /// exactly the WINDOW between the loaded slot's save moment and the rollback load —
        /// a marked session's file written inside it carries future world-facts and is
        /// refused.  A file OLDER than the loaded slot's moment is at-or-before by the
        /// user's own rule and serves even from a marked folder (the reviewer measured a
        /// carried-in at-or-before copy being over-refused into a join lockout); a file
        /// written after the load is the rolled-back world's own and serves.  Round-274/H4:
        /// a marked session's unreadable file fails CLOSED (refused), never open.</summary>
        private static bool IsAbandonedCopy(string session, string hsgPath)
        {
            long captureUnix, loadedStamp;
            lock (_abandonedAtLoad)
            {
                if (_abandonedCaptureUnix <= 0 || !_abandonedAtLoad.ContainsKey(session)) return false;
                captureUnix = _abandonedCaptureUnix;          // F6: read under the lock, once
                loadedStamp = _loadedStampAtCapture;
            }
            try
            {
                long fileUnix = new DateTimeOffset(File.GetLastWriteTimeUtc(hsgPath)).ToUnixTimeSeconds();
                return fileUnix > loadedStamp && fileUnix <= captureUnix;
            }
            catch { return true; }   // H4: fail closed — refusal costs a rescue attempt, trust costs the timeline
        }

        /// <summary>Round-233 (field 20260802-200058 + rewind analysis): THE shared day-fenced
        /// "newest eligible copy" selector for every place that pulls a member's newest .hsg out
        /// of the world lineage (save-time carry, quit checkpoint, load-time serve rescue).
        ///
        /// Eligibility runs BEFORE newest-selection — a fence-blocked future copy (a rewound
        /// world's old-timeline files, day 153 in a day-50 world) can never shadow an eligible
        /// older one. A candidate is eligible when its manifest slot day ≤ maxDay + 1 (one day of
        /// midnight-straddle tolerance — the same skew the disconnect-commit window accepts: a
        /// member's day can read one ahead of a save written moments later), or when its day is
        /// UNKNOWN (manifest-less '-recover' / self-saves) and the caller allows that. Unknown-day
        /// copies may FILL an empty slot but never REPLACE one (callers pass allowUnknownDay
        /// accordingly). maxDay &lt; 0 = fence unavailable → day compare skipped. Manifest slot
        /// days only (pure JSON reads — thread-safe; the IL2CPP save scanner is main-thread-only
        /// and this runs on serve paths too).</summary>
        private static (string srcSession, string srcDir, DateTime when, int day)? LineageNewestEligible(
            string aroundSession, string stableId, int maxDay, bool allowUnknownDay, out int abandonedRefused)
        {
            abandonedRefused = 0;
            try
            {
                (string srcSession, string srcDir, DateTime when, int day)? best = null;
                DateTime bestWhen = DateTime.MinValue;
                foreach (var s in LineageSessions(aroundSession))
                {
                    string dir = Path.Combine(MPSaveManager.MpSessionFolder(s), stableId);
                    if (!Directory.Exists(dir)) continue;
                    string? hsg = NewestHsg(dir);
                    if (hsg == null) continue;
                    // Round-271/274: a rolled-back load's abandoned-timeline copies are never a
                    // rescue source — they carry world-facts the loaded world predates.  Tested
                    // per FILE (H2): only a file rewritten after the rollback load is eligible.
                    if (IsAbandonedCopy(s, hsg)) { abandonedRefused++; continue; }
                    DateTime when; try { when = File.GetLastWriteTimeUtc(hsg); } catch { continue; }
                    int day = -1;
                    try { day = MPSaveManager.ReadManifest(s)?.Slots?.Find(x => x.StableId == stableId)?.Day ?? -1; } catch { }
                    if (day < 0 && !allowUnknownDay) continue;
                    if (day >= 0 && maxDay >= 0 && day > maxDay + 1) continue;   // timeline fence
                    if (when > bestWhen) { bestWhen = when; best = (s, dir, when, day); }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>Round-233: the fence's reference day — the live world clock when available
        /// (the carry runs during a save, on the main thread), else the target session's manifest
        /// (max slot day; serve paths may run off-thread where the live clock is unreadable).
        /// -1 = no reference → the fence stands down (legacy behavior).</summary>
        private static int FenceDayFor(string session)
        {
            try { int d = SaveGameManager.Current?.Day ?? -1; if (d >= 0) return d; } catch { }
            try
            {
                var m = MPSaveManager.ReadManifest(session);
                int mx = -1;
                if (m?.Slots != null) foreach (var s in m.Slots) if (s.Day > mx) mx = s.Day;
                return mx;
            }
            catch { return -1; }
        }

        public static void CarryForwardAbsentMembers(string targetSession, bool includeConnected = false)
        {
            try
            {
                if (string.IsNullOrEmpty(targetSession)) return;
                var connected = MPServer.ConnectedStableIds();
                // Round-184: lineage enumeration extracted to LineageSessions (shared with the
                // mid-join lineage rescue). Now also sweeps '-recover' — the midnight checkpoint
                // can legitimately hold a member's newest copy.
                var lineage = LineageSessions(targetSession);

                // Round-233 rework (field 20260802-200058: "friend's progress occasionally rolls
                // back to a very old state"). The old scan picked the raw mtime-newest copy and
                // then SKIPPED any member whose slot already existed in the target — so a base
                // save holding an ANCIENT copy of a departed member was never refreshed, and the
                // next load of that base served the ancient copy (present-but-stale, the case
                // round-184's absent-only fixes never covered). Now: enumerate members, then let
                // the shared day-fenced selector pick their newest ELIGIBLE copy — the fence
                // refuses future-timeline copies so a deliberately rewound world can't inherit
                // characters from its own future (rewind analysis, 2026-08-11).
                var members = new HashSet<string>();
                foreach (var s in lineage)
                {
                    string sessionFolder = MPSaveManager.MpSessionFolder(s);
                    if (string.IsNullOrEmpty(sessionFolder) || !Directory.Exists(sessionFolder)) continue;
                    foreach (var dir in Directory.GetDirectories(sessionFolder))
                    {
                        string stable = Path.GetFileName(dir);
                        if (!stable.StartsWith("guid-") && !stable.StartsWith("steam-")) continue;   // character folders only — steam-<id> is the normal id (MPConfig:379), guid- only the fallback; was excluding every Steam player
                        members.Add(stable);
                    }
                }

                int fenceDay = FenceDayFor(targetSession);
                foreach (var stable in members)
                {
                    // Normally connected members save themselves fresh this round — don't
                    // race their incoming upload. The QUIT CHECKPOINT is the exception
                    // (review-2 fix, includeConnected): it broadcasts no SaveNow, so no
                    // uploads are coming and connected members' last stored copies must
                    // be carried or the checkpoint is born without them.
                    if (!includeConnected && connected.Contains(stable)) continue;
                    // An existing target copy may only be REPLACED by a known-day candidate;
                    // an empty slot may be FILLED by anything (manifest-less recovers included).
                    string targetMemberDir = Path.Combine(MPSaveManager.MpSessionFolder(targetSession), stable);
                    string? already = Directory.Exists(targetMemberDir) ? NewestHsg(targetMemberDir) : null;
                    var best = LineageNewestEligible(targetSession, stable, fenceDay, allowUnknownDay: already == null, out int carryRefused);
                    if (best == null)
                    {
                        if (carryRefused > 0)
                            Plugin.Logger.LogInfo($"[MPSave] carry-forward for stable={stable}: only abandoned-timeline cop(ies) exist ({carryRefused} refused, round-271) — slot left without this member rather than injecting the old timeline.");
                        continue;
                    }
                    var (srcSession, srcDir, srcWhen, srcDay) = best.Value;
                    if (srcSession == targetSession) continue;   // already its own newest eligible
                    if (already != null)
                    {
                        // Replace only when the lineage holds something strictly newer — on EVERY
                        // save path now, not just the quit checkpoint (the round-233 fix).
                        DateTime tWhen; try { tWhen = File.GetLastWriteTimeUtc(already); } catch { continue; }
                        if (tWhen >= srcWhen) continue;
                    }
                    try
                    {
                        string dstDir = MPSaveManager.MpCharacterFolder(targetSession, stable);
                        foreach (var f in Directory.GetFiles(srcDir))
                            File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), overwrite: true);
                        var slot = MPSaveManager.ReadManifest(srcSession)?.Slots?.Find(x => x.StableId == stable);
                        if (slot != null) MergeSlot(targetSession, slot);
                        Plugin.Logger.LogInfo($"[MPSave] Carried forward absent member (stable={stable}, day={srcDay}) from '{srcSession}' → '{targetSession}'{(already != null ? " — REPLACED a stale copy (round-233)" : "")}.");
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
            /// <summary>Handoff slice 3: the world this save belongs to, read from our
            /// MIRRORED manifest (slice 1) at marker-write time. "" = no mirror yet
            /// (pre-mirror store) → the host falls back to name-only matching.</summary>
            public string PlaythroughId = "";
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
                var slot = PerformLocalSave(baseName + "-disconnect", out bool saved);   // current game → <base>-disconnect/<ourStable>/
                // Round-237 fix B: a marker for a failed save would offer a rejoin snapshot
                // whose bytes are the OLDER save wearing today's Day — skip both.
                if (!saved)
                {
                    Plugin.Logger.LogError("[MPSave] disconnect save FAILED — marker NOT written (round-237); rejoin will use the host's stored copy.");
                    return;
                }
                long nowUnix = 0;
                try { nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } catch { }
                // Handoff slice 3: stamp the WORLD identity so a future host — original or
                // rotated — only requests this save for the same lineage (session names
                // alone can collide across worlds). Wire truth first (review-2 fix: the
                // local base manifest can belong to a same-named DIFFERENT world when the
                // clobber guard has been refusing mirrors); mirrored manifest as fallback.
                string pid = _wireWorldPid;
                if (string.IsNullOrEmpty(pid))
                    try { pid = MPSaveManager.ReadManifest(baseName)?.PlaythroughId ?? ""; } catch { }
                var marker = new ClientDisconnectMarker
                {
                    SessionBase = baseName,
                    StableId    = MPConfig.StableId,
                    Day         = slot.Day,
                    SavedAtUnix = nowUnix,
                    PlaythroughId = pid,
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
                    Money = LocalWalletOr0(),   // round-224
                };
                // Round-275: include the sidecar — the host's day validator reads the day
                // through the save scanner, which is blind without the .meta (the restore
                // feature rejected everything past day 2 for exactly this reason).
                string dcMeta = "";
                try { string mp = file + ".meta"; if (File.Exists(mp)) dcMeta = File.ReadAllText(mp); } catch { }
                MPClient.SendClientDisconnectUpload(new SaveDataPayload
                {
                    SessionName = baseName, Success = true, Slot = slot,
                    HsgGzipBase64 = GzipBase64(raw), RawLength = raw.Length,
                    MetaJson = dcMeta,
                    PlaythroughId = _wireWorldPid,   // round-222: sticky through disconnect by design
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
                        // F10 (2026-08-21): raw folder segment, never the CharacterPath getter —
                        // it rebuilds a path under the SP store and CreateDirectory's it (audit F4).
                        string seg = s?.characterId ?? "";
                        if (string.Equals(seg, MPSaveManager.Sanitize(stable), StringComparison.OrdinalIgnoreCase)) return s.day;
                    }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ReadSaveDay: {ex.Message}"); }
            return -1;
        }

        public static bool TryCommitClientDisconnectSave(SaveDataPayload p, string stable)
        {
            if (p != null && !string.IsNullOrEmpty(p.PlaythroughId) && !string.IsNullOrEmpty(p.SessionName))
            {
                MPSaveManager.NoteSessionPid(p.SessionName, p.PlaythroughId);            // round-222
                MPSaveManager.NoteSessionPid(StripAutoSuffix(p.SessionName), p.PlaythroughId);
            }
            if (p == null || string.IsNullOrEmpty(stable) || string.IsNullOrEmpty(p.HsgGzipBase64)) return false;
            // Review-2 fix: validate against and commit into the folder that holds the
            // CURRENT state (loaded variant / latest save target) — the lineage base can
            // be stale or absent, which skewed the day window in both directions and
            // stranded the commit where the subsequent mid-join ship wouldn't read it.
            string session     = MidJoinSourceSession;
            // Review-3 fix: a frozen legacy checkpoint ('-cp-'/'-cpa-', creation retired
            // 2026-07-07) is a recorded moment (round-37) — never mutated, and a recovery
            // save from the live era does not apply to a world deliberately rolled back
            // onto one. Refuse with a clear line (the old path dropped it as a silent
            // "session mismatch"); the member joins at the checkpoint's recorded state.
            if (!string.Equals(MPSaveManager.StripToBase(session), StripAutoSuffix(session), StringComparison.Ordinal))
            {
                Plugin.Logger.LogInfo($"[MPSave] Disconnect save not applicable: host is running a frozen checkpoint ('{session}') — member joins at the checkpoint's recorded state.");
                return false;
            }
            string baseName    = StripAutoSuffix(session);
            string stageSession = "_dcstage_" + MPSaveManager.Sanitize(stable);
            try
            {
                if (!string.Equals(p.SessionName, baseName, StringComparison.Ordinal))
                { Plugin.Logger.LogWarning($"[MPSave] Disconnect upload session mismatch (got '{p.SessionName}', active base '{baseName}') — ignoring."); return false; }

                // Round-274/C1 (rig-reproduced): this commit runs BEFORE the fenced serve
                // ladder — during a rollback it wrote future-timeline bytes into the loaded
                // slot, destroyed its coherent copy, and carry-forward spread them.  The day
                // window cannot see a same-day rollback (and storedDay<0 accepted anything).
                // Discriminator, host-side only: a member disconnecting AFTER the rollback
                // load was necessarily SERVED by this world first — a member we have never
                // served since the load carries a save written before it: old timeline.
                if (RollbackFenceActive && !WasServedSinceLoad(stable))
                {
                    Plugin.Logger.LogWarning($"[MPSave] REFUSED client disconnect save (stable={stable}): written before this world's rollback load — old-timeline bytes (round-274); the fenced serve ladder decides their save instead.");
                    return false;
                }

                byte[] raw = UnGzipBase64(p.HsgGzipBase64);
                if (raw == null || raw.Length == 0) return false;

                // Stage into a throwaway session so we can read the save's ACTUAL day via the game scanner
                // before deciding — never disturb the real session unless we accept it.
                string stageDir = MPSaveManager.MpCharacterFolder(stageSession, stable);
                try { foreach (var f in Directory.GetFiles(stageDir)) File.Delete(f); } catch { }
                string name = MPSaveManager.Sanitize(string.IsNullOrEmpty(p.Slot?.SaveName) ? SaveFileName : p.Slot.SaveName);
                File.WriteAllBytes(Path.Combine(stageDir, name + ".hsg"), raw);
                // Round-275: stage the sidecar too — ReadSaveDay goes through the game's
                // save scanner, which cannot see a meta-less .hsg (actualDay always read 0,
                // so the restore feature rejected every save past day 2).
                try { if (!string.IsNullOrEmpty(p.MetaJson)) File.WriteAllText(Path.Combine(stageDir, name + ".hsg.meta"), p.MetaJson); } catch { }
                LogHsgWrite(stageSession, stable, raw.Length, "staged for day validation");

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
                    // Round-274/C1: never destroy the slot's existing copy in place — sideline
                    // it first (".bak" is invisible to NewestHsg's "*.hsg" pattern), so a wrong
                    // acceptance can still be recovered by hand instead of being unrecoverable.
                    // Round-275: the meta rides along in both directions so every copy stays datable.
                    try
                    {
                        string cur = Path.Combine(dstDir, name + ".hsg");
                        if (File.Exists(cur)) File.Copy(cur, cur + ".pre-dc.bak", overwrite: true);
                        if (File.Exists(cur + ".meta")) File.Copy(cur + ".meta", cur + ".meta.pre-dc.bak", overwrite: true);
                    }
                    catch { }
                    File.WriteAllBytes(Path.Combine(dstDir, name + ".hsg"), raw);
                    try { if (!string.IsNullOrEmpty(p.MetaJson)) File.WriteAllText(Path.Combine(dstDir, name + ".hsg.meta"), p.MetaJson); } catch { }
                    LogHsgWrite(session, stable, raw.Length, $"accepted disconnect save (day {actualDay} vs stored {storedDay})");
                    MergeSlot(session, new MpSlot
                    {
                        StableId = stable, DisplayName = p.Slot?.DisplayName ?? stable, CharacterId = stable,
                        SaveName = name, Day = actualDay, IsHost = false,
                    });
                    // Handoff slice 1: an accepted disconnect save is a fresh member
                    // file — mirror it. Off-thread (this path runs on the main thread).
                    System.Threading.Tasks.Task.Run(() => MirrorMemberFile(session, stable));
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
            // Store v2: whichever branch resolved the id, it names the active playthrough
            // folder from here on (idempotent when already set). The pin makes the name
            // resolve correctly even for writes AFTER session teardown (carry-forward
            // flush ordering) — pins outlive the active-pid clear.
            MPSaveManager.SetActivePlaythrough(_activePlaythroughId, StripAutoSuffix(sessionName));
            MPSaveManager.NoteSessionPid(sessionName, _activePlaythroughId);
            // Review fix 2026-07-23: provenance rides EVERY host-side manifest write.
            // PersistGrantsNow/MergeSlot used to rewrite + mirror manifests still
            // carrying the PREVIOUS host's identity/epoch (with fresh timestamps) in
            // the window between a handoff load and the first coordinated save —
            // false "shared — last hosted by X" labels and HOST HANDOFF lines. Also
            // ensures a freshly MINTED manifest (upload landing before the host
            // lambda) never reaches disk with epoch 0.
            if (MPServer.IsRunning)
            {
                _activeManifest.LastHostStableId = MPConfig.StableId;
                _activeManifest.HostEpoch        = _activeHostEpoch;
            }
            return _activeManifest;
        }

        /// <summary>The ACTIVE world's identity (empty when no session is live). The
        /// authoritative pid for join-time gates — unlike a re-read of the BASE
        /// session's manifest, it is correct even when the base folder has no
        /// manifest (host loaded an '-auto' variant) or holds a same-named
        /// different world (review fix 2026-07-23).</summary>
        public static string ActivePlaythroughId
        {
            get { lock (_lock) return _activePlaythroughId; }
        }

        // Review-2 fix (2026-07-23): the folder mid-session joiners are SERVED from.
        // ActiveSessionName is the lineage BASE (round-37), whose folder can be stale
        // (world loaded from a newer '-auto'/'-disconnect' variant) or absent entirely
        // (auto-only worlds) — serving from it rolled a rejoining former host back to
        // the last manual save, or fresh-started them despite their current copy
        // sitting in the loaded variant. The source is the LOADED variant until the
        // first coordinated save, then the latest save target (fresh uploads +
        // carry-forward land there; a joiner is by definition absent at save time, so
        // carry-forward has already placed their newest copy in it).
        private static string _midJoinSource = "";   // under _lock; "" → fall back to the base

        public static string MidJoinSourceSession
        {
            get { lock (_lock) return string.IsNullOrEmpty(_midJoinSource) ? _activeSessionName : _midJoinSource; }
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
        /// day, timestamp) and write. Day + timestamp advance ONLY when
        /// <paramref name="saveSucceeded"/> — a failed save must not move the catalog's
        /// save-moment claim (user-approved 2026-08-21). Everything else (owners,
        /// grants, loans, tuning, provenance) always reflects the live world, so
        /// members' incoming uploads still merge against valid metadata.</summary>
        private static void SetSessionMetadata(string sessionName, int worldDay, bool saveSucceeded)
        {
            lock (_lock)
            {
                var m = EnsureManifest(sessionName);
                m.GameVersion    = SafeGameVersion();
                if (saveSucceeded)
                {
                    m.WorldDay    = worldDay;
                    m.SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    m.LastHostSaveFailed = false;
                }
                else
                {
                    // B (user-approved 2026-08-21): the held-back claim is FLAGGED, so the
                    // rollback fence treats this sibling as age-unknown (fail-closed) instead
                    // of trusting a stamp that predates files members may still have landed.
                    m.LastHostSaveFailed = true;
                    Plugin.Logger.LogWarning($"[MPSave] '{sessionName}': save failed — catalog day/time NOT advanced (claim stays day {m.WorldDay}, flagged); members' uploads still merge and their files date themselves.");
                }
                m.BuildingOwners = BuildOwnersStableKeyed();
                m.BuildingRealEstateOwners = RealEstateOwnersStableKeyed();
                m.Grants = new List<MpGrant>();
                foreach (var e in GrantSync.AllStoreEntries())
                    m.Grants.Add(new MpGrant { Kind = e.Kind, Owner = e.Owner, Grantee = e.Grantee, GranteeName = GrantSync.NameOf(e.Grantee) });
                m.Merger = BuildMergerManifest();
                m.MergerWalletBalance     = MPServer.SnapshotWalletBalances();      // slice 4
                m.MergerWalletContributed = MPServer.SnapshotWalletContributed();
                m.Loans = MPHub.SnapshotLoans();   // sweep 2026-08-18: loans are part of the save moment
                // Round-53: the running session's tuning dials persist with the save (mid-session
                // changes included), so the next load's lobby mirrors what this world actually ran.
                m.TuneNeedsDrain   = MPNeedsTuning.DrainPercent;
                m.TuneRestSpeed    = MPNeedsTuning.RestPercent;
                m.TuneMoraleTempo  = MPNeedsTuning.MoralePercent;
                // Handoff slice 1/2: store provenance — who hosted when this was written,
                // and which host-start of the lineage this is.
                m.LastHostStableId = MPConfig.StableId;
                m.HostEpoch        = _activeHostEpoch;
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
                if (drift >= 3 && m.LastHostSaveFailed)
                    Plugin.Logger.LogInfo($"[Integrity] manifest day {m.WorldDay} vs world day {worldDay}: staleness explained by a flagged failed host save — the ledger WAS written (B, 2026-08-21).");
                else if (drift >= 3)
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
                string session; string manifestJson; string pid;
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
                    m.Loans = MPHub.SnapshotLoans();   // sweep 2026-08-18: loans ride the manifest like grants
                    // Round-274/H1: do NOT touch SavedAtUnix here — it means "when was this
                    // WORLD saved", and a grants-only persist is not a world save.  Re-stamping
                    // it fired within seconds of every load ("Persisted 0 grant(s)") and
                    // silently un-marked abandoned-timeline slots (rig-proven, twice).
                    MPSaveManager.WriteManifest(_activeSessionName, m);
                    Plugin.Logger.LogInfo($"[MPSave] Persisted {m.Grants.Count} grant(s) + {m.Merger.Count} merger member(s) + {m.Loans.Count} loan(s) to '{_activeSessionName}' on change.");
                    session = _activeSessionName;
                    manifestJson = Newtonsoft.Json.JsonConvert.SerializeObject(m);
                    pid = m.PlaythroughId ?? "";
                }
                // Handoff slice 1: ledgers changed → manifest-only mirror (KBs), so
                // every member's store copy stays ledger-current between saves.
                MirrorManifestOnly(session, manifestJson, pid);
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

        // Round-271 note: slots are added HERE ONLY — on the host's own successful save,
        // a member upload landing, or a carry that physically copied a file.  This is the
        // manifest-honesty invariant the serve ladder's Fresh-vs-Unavailable verdict rests
        // on: a listed slot means bytes landed at least once (missing/unreadable = damage,
        // refuse fresh-start); unlisted means the member did not exist in this slot, so a
        // fresh start (remake) is the timeline-accurate outcome (design ruling 2026-08-17).
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
        /// <summary>Round-224: the local player's live wallet (0 if unreadable) — slots
        /// are born with the real figure instead of a $0 placeholder. Main thread.</summary>
        private static float LocalWalletOr0()
        {
            try { var gi = SaveGameManager.Current; if (gi != null) return gi.Money; } catch { }
            return 0f;
        }

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
