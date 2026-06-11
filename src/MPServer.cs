using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Concurrent;
using System.Text.Json;
using UI.Load;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Runs on the host. Accepts client connections, maintains shared world state,
    /// and broadcasts updates to all peers.
    /// </summary>
    public static class MPServer
    {
        private static NetManager?   _server;
        private static EventBasedNetListener? _listener;

        /// <summary>Address key → player ID who owns it. Empty = unowned.
        /// CONCURRENT: written on the network poll thread (rent/disconnect) and
        /// read/iterated on the main thread (save manifest build, world snapshot).
        /// A plain Dictionary here corrupts under that race → coreclr access
        /// violation, so this must be a ConcurrentDictionary.</summary>
        public static readonly ConcurrentDictionary<string, string> BuildingOwners = new();

        /// <summary>Ordered list of player IDs currently in the lobby (host is always index 0).</summary>
        public static readonly List<string> LobbyPlayers = new();

        /// <summary>True while waiting in the lobby; false once the game has been started.</summary>
        public static bool IsInLobby { get; private set; } = true;

        /// <summary>The MP save session the host picked in the "Host Saved Game" list
        /// (empty = load the newest).  Consumed by StartLoadGame.</summary>
        public static string ChosenLoadSession = "";

        /// <summary>Host setting: when true, the host's starting cash applies to everyone;
        /// when false, each client sets their own starting cash.</summary>
        public static bool EnforceStartingCash = true;

        /// <summary>Host lobby: per-player starting-cash overrides, keyed by playerId
        /// (the host's own included).  Absent ⇒ the player gets the difficulty/base
        /// StartingMoney.  Lets the host designate that specific players begin with
        /// more (or less) than the base.  Edited on the main thread (lobby UI),
        /// read on the main thread (StartNewGame) — no cross-thread access.</summary>
        public static readonly Dictionary<string, int> StartingCashByPlayer = new();

        /// <summary>Effective starting cash for a player: their override if set, else
        /// the supplied base.</summary>
        public static int StartingCashFor(string playerId, int baseCash)
            => (!string.IsNullOrEmpty(playerId) && StartingCashByPlayer.TryGetValue(playerId, out var v)) ? v : baseCash;

        /// <summary>Per-player self-chosen starting age (playerId → age, host included).
        /// Unlike cash, each player picks their OWN; the host just aggregates them for
        /// display + bakes each into that player's start settings.</summary>
        public static readonly Dictionary<string, int> StartingAgeByPlayer = new();

        public static int StartingAgeFor(string playerId, int baseAge)
            => (!string.IsNullOrEmpty(playerId) && StartingAgeByPlayer.TryGetValue(playerId, out var v) && v > 0) ? v : baseAge;

        /// <summary>Records a player's chosen starting age and re-broadcasts the lobby
        /// so everyone sees it.  Called for the host's own age (from the lobby UI) and
        /// for each client's reported age (LobbyPref).</summary>
        public static void SetStartingAge(string playerId, int age)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            StartingAgeByPlayer[playerId] = age;
            if (IsInLobby) BroadcastLobbyUpdate();
        }

        private static readonly HashSet<NetPeer>        _clients   = new();
        private static readonly Dictionary<int, string> _peerNames = new(); // peer.Id → playerId
        /// <summary>playerId → immutable StableId (for save/ownership persistence).
        /// Includes the host's own.  Populated from each client's Hello.
        /// CONCURRENT: written on the poll thread (Hello) + main thread (host
        /// start), read on the main thread (save/load) — must be thread-safe.</summary>
        public static readonly ConcurrentDictionary<string, string> StableIdByPlayer = new();

        /// <summary>stableId → last-known money (live-streamed from each player +
        /// the host's own).  The most-current cash figure to restore on reconnect,
        /// so a crash costs at most a few seconds of earnings.
        /// CONCURRENT: written on poll + main threads, read on the main thread.</summary>
        public static readonly ConcurrentDictionary<string, float> CashByStableId = new();

        /// <summary>Last-synced cash for a player, or -1 when unknown (unknown
        /// must not block — the Hub treats negative as "can't validate").</summary>
        public static float GetKnownCash(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return -1f;
            string stable = StableIdByPlayer.TryGetValue(playerId, out var s) && !string.IsNullOrEmpty(s) ? s : playerId;
            return CashByStableId.TryGetValue(stable, out var m) ? m : -1f;
        }

        /// <summary>Record a player's latest cash, keyed by their stable id.</summary>
        public static void RecordCash(string playerId, float money)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            string stable = StableIdByPlayer.TryGetValue(playerId, out var s) && !string.IsNullOrEmpty(s) ? s : playerId;
            CashByStableId[stable] = money;
        }

        /// <summary>Host: rebuild the live ownership map from a session manifest
        /// (re-keying the stableId-keyed owners back to the live playerIds of
        /// connected players; absent owners stay reserved under their stableId
        /// until they reconnect) and seed last-known cash.  Phase 4 load.</summary>
        public static void RestoreOwnershipFromManifest(MpManifest m)
        {
            try
            {
                var reverse = new Dictionary<string, string>();   // stableId → playerId
                foreach (var kv in StableIdByPlayer) reverse[kv.Value] = kv.Key;

                BuildingOwners.Clear();
                foreach (var kv in m.BuildingOwners)
                {
                    string ownerStable = kv.Value;
                    if (string.IsNullOrEmpty(ownerStable)) continue;
                    if (ownerStable == MPConfig.StableId)             BuildingOwners[kv.Key] = "host";
                    else if (reverse.TryGetValue(ownerStable, out var pid)) BuildingOwners[kv.Key] = pid;
                    else                                              BuildingOwners[kv.Key] = ownerStable; // reserved (absent owner)
                }
                foreach (var slot in m.Slots)
                    if (slot.Money != 0f) CashByStableId[slot.StableId] = slot.Money;

                Plugin.Logger.LogInfo($"[Server] Restored {BuildingOwners.Count} owned building(s) from manifest.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] RestoreOwnershipFromManifest: {ex.Message}"); }
        }

        /// <summary>Host: send each connected client its own stored .hsg for the
        /// session so it can load in.  Phase 4 load.</summary>
        public static void SendLoadDataToEachClient(string session, MpManifest m)
        {
            foreach (var peer in _clients)
            {
                if (!_peerNames.TryGetValue(peer.Id, out var pid)) continue;
                if (!StableIdByPlayer.TryGetValue(pid, out var stable) || string.IsNullOrEmpty(stable)) continue;
                var data = MPSaveCoordinator.ReadSaveBytesGzip(session, stable);
                if (data == null) { Plugin.Logger.LogWarning($"[Server] No stored .hsg for '{pid}' (stable={stable})."); continue; }
                float cash = MPSaveCoordinator.BestCashFor(m, stable);
                var payload = new LoadDataPayload
                {
                    SessionName   = session,
                    HsgGzipBase64 = data.Value.b64,
                    RawLength     = data.Value.raw,
                    Money         = cash,
                };
                Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", payload));
                Plugin.Logger.LogInfo($"[Server] Sent LoadData to '{pid}' ({data.Value.raw}B, ${cash:F0}).");
            }
        }

        /// <summary>
        /// PlayerId → CharacterName (Wave 5).  Populated by PlayerProfile
        /// messages from each connected client + host's own profile.
        /// Used as the display name in RivalsSnapshot / RivalsStatsSnapshot.
        /// </summary>
        public static readonly Dictionary<string, string> _characterNamesByPlayerId = new();

        /// <summary>
        /// PlayerId → most recent self-reported stats from that client (Wave 7).
        /// Captured when client sends RivalsStatsRequest; used by host's
        /// BuildRivalsStatsSnapshot to populate non-host player rows since
        /// host has no source of truth for what other players own locally.
        /// </summary>
        private static readonly Dictionary<string, RivalsStatsRequestPayload> _clientSelfStats = new();
        private static Thread? _pollThread;
        private static volatile bool _running;

        // ── Startup pause hold ────────────────────────────────────────────────
        // Players (by ID) confirmed to have finished loading their game scene.
        // The game stays frozen at timeScale 0 until every roster player is in
        // this set, then the host releases it for everyone.  One-shot per game.
        private static readonly HashSet<string> _inGamePlayers = new();
        // Frozen-until-synced: a player is "world ready" once it has APPLIED the world
        // sync (host = as soon as its own world is loaded; clients = after they ack
        // WorldReady).  The startup hold releases only when ALL players are world-ready,
        // so nobody gets control of an un-synced world.
        private static readonly HashSet<string> _worldReadyPlayers = new();
        private static bool _hostSnapshotsReady;   // host's own world loaded → can serve snapshots
        private static readonly object _startupLock = new();
        private static bool _startupReleased;
        // True while the session is paused because a player DROPPED (set in
        // OnPeerDisconnected, cleared when they reconnect) — distinguishes the
        // disconnect pause from a deliberate manual pause so the reconnect path
        // only lifts the former.
        private static volatile bool _pausedByDisconnect;
        /// <summary>Who dropped (for the host's pause overlay).</summary>
        public static volatile string DisconnectPauseWho = "";
        public static bool PausedByDisconnect => _pausedByDisconnect;

        /// <summary>Lift a disconnect pause — from the overlay's "keep playing"
        /// click or automatically when the dropped player reconnects.</summary>
        public static void ResumeFromDisconnectPause()
        {
            if (!_pausedByDisconnect) return;
            _pausedByDisconnect = false;
            DisconnectPauseWho  = "";
            BroadcastManualPause(false);
            GameStatePatcher.EnqueueOnMainThread(() => TimeSync.SetManualPause(false));
            Plugin.Logger.LogInfo("[Server] Disconnect pause lifted.");
        }

        public static bool IsRunning      => _running;
        public static int  ConnectedCount => _clients.Count;

        public static bool Start(int port)
        {
            // A previous session may still be live (e.g. the host exited to the
            // menu without Leave — nothing stops the server on scene exit prior
            // to this guard).  Tear it down first: the old NetManager still holds
            // the port, so the new bind fails with AddressAlreadyInUse while
            // _running still reads true — a zombie host whose lobby looks alive
            // but that nobody can connect to.
            if (_running)
            {
                Plugin.Logger.LogInfo("[Server] Start: previous session still running — stopping it first.");
                Stop();
            }

            // Reset lobby state for a fresh session
            IsInLobby = true;
            LobbyPlayers.Clear();
            LobbyPlayers.Add(MPConfig.PlayerId); // host is always the first player
            EnforceStartingCash = true;
            StartingCashByPlayer.Clear();
            StartingAgeByPlayer.Clear();
            ChosenLoadSession = "";   // cleared each host session; set only when a save is picked
            MPSaveCoordinator.ActiveSessionName = "";   // stale session must not leak into a new lobby
                                                        // (HostLoadSession / first save re-set it)
            _peerNames.Clear();
            StableIdByPlayer.Clear();
            StableIdByPlayer[MPConfig.PlayerId] = MPConfig.StableId; // host's own
            _clients.Clear();         // stale peers from a torn-down session
            BuildingOwners.Clear();   // per-session state — a new game must not inherit
            CashByStableId.Clear();   // owners/cash; the load path re-seeds from the manifest
            _listener = new EventBasedNetListener();
            _server   = new NetManager(_listener)
            {
                AutoRecycle = true,
                UnconnectedMessagesEnabled = false,
            };

            _listener.ConnectionRequestEvent += request =>
            {
                // Accept all connections (key-gated); LOGGED — a silent handler
                // made transport-level join failures undiagnosable (2026-06-11).
                Plugin.Logger.LogInfo($"[Server] connection request from {request.RemoteEndPoint} (peers={_clients.Count}).");
                request.AcceptIfKey("BAMP");
            };

            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnReceive;

            if (!_server.Start(port))
            {
                Plugin.Logger.LogError($"[Server] Failed to start on port {port}");
                return false;
            }

            _running = true;
            ResetJoinControl();   // fresh hosting session — bans lift, pending requests drop
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "BAMP-Server" };
            _pollThread.Start();

            Plugin.Logger.LogInfo($"[Server] Listening on port {port}");
            return true;
        }

        public static void Stop()
        {
            _running = false;
            _server?.Stop();
            _pollThread?.Join(1000);
            IsInLobby = true;
            LobbyPlayers.Clear();
            _peerNames.Clear();
            _clients.Clear();
            lock (_startupLock) { _inGamePlayers.Clear(); _worldReadyPlayers.Clear(); _hostSnapshotsReady = false; _startupReleased = false; _pausedByDisconnect = false; }
        }

        /// <summary>Host clicked "Start New Game" in the lobby.</summary>
        /// <summary>Settings of the last new-game start — the mid-join fresh-
        /// character fallback reuses them (null when the host loaded a save).</summary>
        public static GameVariablesDto? LastStartSettings;

        public static void StartNewGame(GameVariablesDto settings)
        {
            if (!_running) return;
            LastStartSettings = settings;
            MPLoadProfiler.Mark($"HOST StartNewGame ({settings.Difficulty}) — {_clients.Count} client(s)");
            IsInLobby = false;

            // Re-arm the startup pause hold for this new game.
            lock (_startupLock) { _inGamePlayers.Clear(); _worldReadyPlayers.Clear(); _hostSnapshotsReady = false; _startupReleased = false; _pausedByDisconnect = false; }

            // Per-player starting cash: each client gets the host-designated amount
            // (their override, else the difficulty base).  The host now designates
            // everyone's cash, so EnforceStartingCash is always true here — the
            // client uses the StartingMoney we bake into its own Settings copy.
            int baseCash = settings.StartingMoney;
            int baseAge  = settings.StartingAge;
            foreach (var peer in _clients)
            {
                string pid  = _peerNames.TryGetValue(peer.Id, out var p) ? p : "";
                int    cash = StartingCashFor(pid, baseCash);
                int    age  = StartingAgeFor(pid, baseAge);
                var perPayload = new StartGamePayload
                {
                    SaveName            = "",
                    Settings            = CloneWithCash(settings, cash, age),
                    EnforceStartingCash = true,
                };
                Send(peer, MessageEnvelope.Create(MessageType.StartGameNew, "host", perPayload));
                Plugin.Logger.LogInfo($"[Server] StartNewGame → '{pid}' cash ${cash} age {age}.");
            }
            Plugin.Logger.LogInfo($"[Server] StartNewGame ({settings.Difficulty}) sent to {_clients.Count} client(s); base cash ${baseCash}.");

            // Host's own starting cash + age (the host's overrides if set, else base).
            int hostCash = StartingCashFor(MPConfig.PlayerId, baseCash);
            int hostAge  = StartingAgeFor(MPConfig.PlayerId, baseAge);
            var hostSettings = CloneWithCash(settings, hostCash, hostAge);

            // Route the host through the game's own intro/character-creation scene.
            // LoadGame() called without character data leaves the loading screen stuck;
            // LoadIntro() is the correct entry point — it sets up character data and
            // then naturally transitions to the game when the player clicks "Start Game".
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    Plugin.Logger.LogInfo("[Server] Initialising new game and loading character creation...");
                    // New() must be called first — same as MainMenuController.StartNewGame(difficulty).
                    // Without it, IntroCharacterCustomizer.StartGame() finds SaveGameManager.Current
                    // null and returns silently, making the Start button appear to do nothing.
                    // Discovery probe — logs GameVariables structure so we can
                    // later force multiplayer new games into Custom (non-story) mode.
                    GameStateReader.ProbeGameVariables();
                    SaveGameManager.New(BuildGameVariables(hostSettings));
                    LoadScene.LoadIntro(false);
                    Plugin.Logger.LogInfo("[Server] New game init + intro scene loaded.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Server] StartNewGame error: {ex}");
                }
            });
        }

        /// <summary>Host clicked "Load Multiplayer Save" in the lobby.</summary>
        public static void StartLoadGame()
        {
            if (!_running) return;
            MPLoadProfiler.Mark($"HOST StartLoadGame (session='{ChosenLoadSession}') — {_clients.Count} client(s)");
            IsInLobby = false;

            // Re-arm the startup pause hold for this new game.
            lock (_startupLock) { _inGamePlayers.Clear(); _worldReadyPlayers.Clear(); _hostSnapshotsReady = false; _startupReleased = false; _pausedByDisconnect = false; }

            // Phase 4: if a multiplayer session exists, resume it — the host holds
            // every player's .hsg, so it ships each connected client its own and
            // loads its own, rather than everyone loading a single-player save.
            var sessions = MPSaveManager.ListSessions();
            if (sessions.Count > 0)
            {
                // Use the session the host picked in the save list; else newest.
                string session = !string.IsNullOrEmpty(ChosenLoadSession)
                                 && sessions.Exists(s => s.Name == ChosenLoadSession)
                                 ? ChosenLoadSession : sessions[0].Name;
                Plugin.Logger.LogInfo($"[Server] StartLoadGame → resuming MP session '{session}'.");
                MPSaveCoordinator.HostLoadSession(session);
                return;
            }

            // No MP session yet — fall back to the legacy "everyone loads their most
            // recent single-player save" behaviour.
            var payload = new StartGamePayload { SaveName = "" };
            Broadcast(MessageEnvelope.Create(MessageType.StartGameLoad, "host", payload));
            Plugin.Logger.LogInfo("[Server] StartLoadGame (legacy SP path) sent to all clients.");

            // Host loads its most recent save on the main thread
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    Plugin.Logger.LogInfo("[Server] Loading most recent save...");
                    var versionPath = SaveGamePathHelper.CurrentVersionFolderPath();
                    var saves = SaveGamePathHelper.GetAllSaveGamesFromVersion(versionPath);

                    if (saves == null || saves.Count == 0)
                    {
                        Plugin.Logger.LogWarning("[Server] No saves found — starting new game instead.");
                        SaveGameManager.New(MakeGameVariables());
                        LoadScene.LoadIntro(false);
                        return;
                    }

                    // Load the most recent save (list is sorted newest-first by the game)
                    var save = saves[0];
                    Plugin.Logger.LogInfo($"[Server] Loading save: {save.alias}");
                    SaveGameManager.Load(save, true);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Server] StartLoadGame error: {ex}");
                }
            });
        }

        // ── Events ────────────────────────────────────────────────────────────

        private static void OnPeerConnected(NetPeer peer)
        {
            Plugin.Logger.LogInfo($"[Server] Peer connected: {peer.Id}");
            // Welcome message will be sent after we receive their Hello
        }

        private static void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Plugin.Logger.LogInfo($"[Server] Peer disconnected: {peer.Id} — {info.Reason}");
            _clients.Remove(peer);
            lock (_pendingJoins) _pendingJoins.Remove(peer.Id);   // abandoned join request

            // Clear any interior subscription the peer held so we stop polling
            // a building no client is in anymore.  Marshal — it mutates the same
            // subscriber dictionaries InteriorSync.Tick touches on the main thread.
            int gonePeerId = peer.Id;
            GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandlePeerDisconnected(gonePeerId));

            string? leftPlayer = null;

            // Remove from lobby player list
            if (_peerNames.TryGetValue(peer.Id, out leftPlayer))
            {
                LobbyPlayers.Remove(leftPlayer);
                _peerNames.Remove(peer.Id);
                BroadcastLobbyUpdate();   // keep everyone's roster (incl. the in-game F9 list) current
                if (!IsInLobby)
                    BroadcastPlayerLeft(leftPlayer); // also tell remaining clients to remove the capsule
            }

            // If a player disconnected during the startup hold, drop them from the
            // in-game set and re-check — the remaining players may now all be loaded.
            if (leftPlayer != null)
            {
                bool release = false;
                bool wasHeld = false;
                List<string> waiting = new();
                lock (_startupLock)
                {
                    if (!_startupReleased)
                    {
                        wasHeld = true;
                        _inGamePlayers.Remove(leftPlayer);
                        _worldReadyPlayers.Remove(leftPlayer);
                        // Release if the REMAINING players are all world-ready (the
                        // leaver no longer gates the hold).
                        waiting = LobbyPlayers.Where(p => !_worldReadyPlayers.Contains(p)).ToList();
                        if (LobbyPlayers.Count > 0 && waiting.Count == 0)
                        {
                            _startupReleased = true;
                            release = true;
                        }
                    }
                }
                if (release)      ReleaseStartupHold("remaining players loaded");
                else if (wasHeld) BroadcastStartupStatus(waiting);
            }

            // HOLD ownership for reconnect (Phase 4): a dropped player keeps their
            // buildings reserved to them so they reclaim everything on return,
            // rather than losing it.  We do NOT free/vacate them.  (Remaining
            // players see those buildings as owned, so they stay un-rentable.)
            int held = BuildingOwners.Count(kv => kv.Value == (leftPlayer ?? peer.Id.ToString()));
            if (held > 0)
                Plugin.Logger.LogInfo($"[Server] Holding {held} building(s) for '{leftPlayer ?? peer.Id.ToString()}' until reconnect.");

            // Remove the player's capsule from the host's own game
            if (leftPlayer != null)
                GameStatePatcher.EnqueueOnMainThread(() => RemotePlayerManager.Remove(leftPlayer));

            // In-game drop → always announce WHO left (the silent pause looked
            // like a freeze), save their state, and pause ONLY for timeout-style
            // drops (crash/network — a reconnect is plausible and supported).  A
            // clean close means the player deliberately quit: keep playing.
            if (leftPlayer != null && !IsInLobby)
            {
                bool cleanLeave = info.Reason == DisconnectReason.RemoteConnectionClose
                               || info.Reason == DisconnectReason.DisconnectPeerCalled;
                try
                {
                    string notice = cleanLeave
                        ? $"{leftPlayer} left the game."
                        : $"{leftPlayer} lost connection — game paused until they rejoin.";
                    MPChat.AddNotice(notice);          // host's own F9 chat
                    BroadcastChat("", "— " + notice);  // remaining clients' chat (no sender prefix)

                    if (!cleanLeave)
                    {
                        _pausedByDisconnect = true;    // cleared on reconnect or overlay dismiss
                        DisconnectPauseWho  = leftPlayer;
                        BroadcastManualPause(true);
                        GameStatePatcher.EnqueueOnMainThread(() => TimeSync.SetManualPause(true));
                        Plugin.Logger.LogInfo($"[Server] Paused session — '{leftPlayer}' dropped ({info.Reason}).");
                    }
                    else
                    {
                        Plugin.Logger.LogInfo($"[Server] '{leftPlayer}' left cleanly — game continues.");
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Disconnect handling: {ex.Message}"); }

                try { MPSaveCoordinator.HostSaveNow("disconnect"); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Disconnect save: {ex.Message}"); }
            }
        }

        private static void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            var bytes = reader.GetRemainingBytes();
            var env   = MessageEnvelope.Deserialize(bytes);
            if (env == null) return;

            switch (env.Type)
            {
                case MessageType.Hello:
                    HandleHello(peer, env);
                    break;

                case MessageType.PlayerInGame:
                    HandleClientPlayerInGame(env);
                    break;

                case MessageType.WorldReady:
                    HandleWorldReady(env);
                    break;

                case MessageType.ManualPause:
                    HandleClientManualPause(peer, env);
                    break;

                case MessageType.RentRequest:
                    HandleRentRequest(peer, env);
                    break;

                case MessageType.PlayerMove:
                    HandlePlayerMove(peer, env);
                    break;

                case MessageType.PlayerAnimTrigger:
                    HandleAnimTrigger(peer, env);
                    break;

                case MessageType.VehicleSync:
                    HandleVehicleSync(peer, env);
                    break;

                case MessageType.TaxiHail:
                    HandleTaxiHail(env);
                    break;

                case MessageType.PlayerAppearance:
                    HandleClientAppearance(env);
                    break;

                case MessageType.InteriorRequest:
                {
                    // HandleRequest builds the interior snapshot (reads IL2CPP:
                    // reg.interiorDesigns/itemInstances/dirtSpots) AND mutates the
                    // subscriber dictionaries that InteriorSync.Tick also touches on
                    // the main thread — so run it ON the main thread (off-thread
                    // IL2CPP read + concurrent-dictionary race otherwise).
                    var p = env.GetPayload<InteriorRequestPayload>();
                    if (p != null)
                    {
                        var pc = peer;
                        GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandleRequest(pc, p.PlayerId, p.AddressKey));
                    }
                    break;
                }

                case MessageType.PlayerExitedBuilding:
                {
                    // Mutates the same subscriber dictionaries as Tick (main thread)
                    // — marshal to avoid the concurrent-dictionary race.
                    var p = env.GetPayload<PlayerExitedBuildingPayload>();
                    if (p != null)
                    {
                        var pc = peer;
                        GameStatePatcher.EnqueueOnMainThread(() => InteriorSync.HandleExit(pc, p.PlayerId, p.AddressKey));
                    }
                    break;
                }

                case MessageType.RivalsStatsRequest:
                {
                    // CRITICAL: PollLoop runs on a BACKGROUND THREAD.  Building
                    // the stats snapshot iterates IL2CPP-Interop objects
                    // (gi.BuildingRegistrations, RivalData.WeeklyIncome,
                    // ownedBusinesses lists) which is unsafe from non-main
                    // threads — eventually native-crashes.  Marshal the work
                    // onto Unity's main thread via EnqueueOnMainThread.
                    // Self-stats capture (pure C# dict writes) is safe inline.
                    var req = env.GetPayload<RivalsStatsRequestPayload>();
                    if (req != null && !string.IsNullOrEmpty(req.PlayerId))
                    {
                        _clientSelfStats[req.PlayerId] = req;
                        string nameForClient = _characterNamesByPlayerId.TryGetValue(req.PlayerId, out var nm) && !string.IsNullOrWhiteSpace(nm) ? nm : req.PlayerId;
                        GameStatePatcher.ClientRivalStats[req.PlayerId] = new RivalStatsInfo
                        {
                            Id                   = req.PlayerId,
                            Name                 = nameForClient,
                            OwnedBuildingsCount  = req.SelfOwnedBuildingsCount,
                            OwnedBusinessesCount = req.SelfOwnedBusinessesCount,
                            WeeklyIncome         = req.SelfWeeklyIncome,
                        };
                        Plugin.Logger.LogInfo($"[Server] Stored self-stats from '{req.PlayerId}': bldgs={req.SelfOwnedBuildingsCount} biz={req.SelfOwnedBusinessesCount} income=${req.SelfWeeklyIncome:F0}.");
                    }
                    var peerCapture = peer;
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        try { SendRivalsStatsSnapshotTo(peerCapture); }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] RivalsStatsRequest main-thread dispatch: {ex.Message}"); }
                    });
                    break;
                }

                case MessageType.PlayerProfile:
                {
                    var p = env.GetPayload<PlayerProfilePayload>();
                    if (p != null && !string.IsNullOrEmpty(p.PlayerId))
                    {
                        _characterNamesByPlayerId[p.PlayerId] = p.CharacterName ?? "";
                        if (p.AgeInYears > 0) GameStatePatcher.ClientPlayerAges[p.PlayerId] = p.AgeInYears;
                        string resolvedName = string.IsNullOrWhiteSpace(p.CharacterName) ? p.PlayerId : p.CharacterName;
                        // Populate HOST's local caches so host's own UI can
                        // render this player as a rival.  Mirrors what the
                        // client does on its side via EnsureRivalCachesPopulated.
                        GameStatePatcher.ClientRivalNames[p.PlayerId] = resolvedName;
                        // Add to player roster (drives Patch_Load_AddPlayers
                        // injection of leaderboard rows for this player).
                        if (p.PlayerId != MPConfig.PlayerId)
                            GameStatePatcher.ClientPlayerRoster[p.PlayerId] = resolvedName;
                        Plugin.Logger.LogInfo($"[Server] PlayerProfile from peer={peer.Id}: PlayerId='{p.PlayerId}' CharacterName='{p.CharacterName}'.  Re-broadcasting roster.");
                        // Forward to all so every client (including the sender) sees the
                        // updated mapping.  Pure byte-forward — always safe.
                        Broadcast(MessageEnvelope.Create(MessageType.PlayerProfile, "host", p));

                        // Everything below — populating gi.rivalStates, decoding the
                        // portrait into a Texture2D/Sprite, and BuildRivalsSnapshot —
                        // touches IL2CPP game systems that DO NOT EXIST while the HOST is
                        // still in character creation / the intro scene.  Running them
                        // there NATIVE-CRASHES the host (uncatchable) when a client loads
                        // far ahead of it.  The name/roster were already stored above
                        // (pure C#), and the host re-broadcasts full rivals state on
                        // go-live, so deferring this costs nothing but a transient
                        // portrait.  Gate on the host being truly in the game world.
                        if (!HostSnapshotsReady)
                        {
                            Plugin.Logger.LogInfo($"[Server] PlayerProfile '{p.PlayerId}': host not in-world yet — deferred rivals/portrait/snapshot work (avoids intro-scene crash).");
                        }
                        else
                        {
                            GameStatePatcher.EnqueueOnMainThread(() =>
                            {
                                try
                                {
                                    // Mark IsPlayer=true so EnsureRivalCachesPopulated
                                    // skips adding this id to gi.rivalStates (which
                                    // would create a ghost leaderboard row).  Player
                                    // rows come solely from Patch_Load_AddPlayers.
                                    var injected = new RivalsSnapshotPayload();
                                    injected.Rivals.Add(new RivalInfo { Id = p.PlayerId, Name = resolvedName, IsPlayer = true });
                                    GameStatePatcher.EnsureRivalCachesPopulated(injected);
                                    // Decode this client's portrait so the HOST'S rivals
                                    // profile shows the client's real face.
                                    if (!string.IsNullOrEmpty(p.PortraitPngBase64))
                                        GameStatePatcher.ApplyPlayerPortrait(p.PlayerId, p.PortraitPngBase64);
                                }
                                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Local rival cache add for client: {ex.Message}"); }
                            });
                            try
                            {
                                var snap = BuildRivalsSnapshot();
                                Broadcast(MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", snap));
                                Plugin.Logger.LogInfo($"[Server] Re-broadcast rivals snapshot (profile update): {snap.Rivals.Count} rival(s).");
                            }
                            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Rivals re-broadcast on profile: {ex.Message}"); }
                        }
                    }
                    break;
                }

                case MessageType.SaveData:
                    // Pure C# decompress + file write + manifest merge (no IL2CPP)
                    // — safe on the poll thread.
                    MPSaveCoordinator.HostHandleSaveData(env.GetPayload<SaveDataPayload>());
                    break;

                case MessageType.BusinessChange:
                {
                    // A client built/changed a business in a building it owns.  Apply it
                    // to the host's world so the host sees it; the host's BusinessSync.Tick
                    // then relays it to the other clients.
                    var bc = env.GetPayload<BusinessChangePayload>();
                    if (bc?.Info != null) GameStatePatcher.ApplyClientBusinessChange(bc.Info);
                    break;
                }

                case MessageType.LobbyPref:
                {
                    var lp = env.GetPayload<LobbyPrefPayload>();
                    if (lp != null && !string.IsNullOrEmpty(lp.PlayerId) && lp.Age > 0)
                        SetStartingAge(lp.PlayerId, lp.Age);   // stores + re-broadcasts lobby
                    break;
                }

                case MessageType.MoneyTransfer:
                {
                    var mt = env.GetPayload<MoneyTransferPayload>();
                    if (mt != null) GameStatePatcher.EnqueueOnMainThread(() => MPHub.HostRouteTransfer(mt));
                    break;
                }

                case MessageType.LoanOffer:
                {
                    var lo = env.GetPayload<LoanOfferPayload>();
                    if (lo != null) GameStatePatcher.EnqueueOnMainThread(() => MPHub.HostRouteOffer(lo));
                    break;
                }

                case MessageType.LoanAnswer:
                {
                    var la = env.GetPayload<LoanAnswerPayload>();
                    if (la != null) GameStatePatcher.EnqueueOnMainThread(() => MPHub.HostHandleAnswer(la));
                    break;
                }

                case MessageType.RestVote:
                {
                    var rv = env.GetPayload<RestVotePayload>();
                    if (rv != null) GameStatePatcher.EnqueueOnMainThread(() => MPRestSync.HostHandleVote(rv));
                    break;
                }

                case MessageType.RetailPrices:
                {
                    // A client's business changed its retail prices: apply to the
                    // host's local registration copy (main thread) + relay to all
                    // (the sender's own echo is dropped by the OwnerId guard).
                    var rp = env.GetPayload<RetailPricesPayload>();
                    if (rp != null && !string.IsNullOrEmpty(rp.AddressKey))
                    {
                        GameStatePatcher.EnqueueOnMainThread(() => MPPriceSync.Apply(rp));
                        Broadcast(MessageEnvelope.Create(MessageType.RetailPrices, "host", rp));
                    }
                    break;
                }

                case MessageType.Chat:
                {
                    // A client chatted.  PUBLIC → append + relay to everyone (the
                    // sender included — host-ordered echo).  PRIVATE → deliver to
                    // the recipient ONLY (the sender already echoed locally).
                    // Pure C# — safe on the poll thread.
                    var cp = env.GetPayload<ChatPayload>();
                    if (cp != null && !string.IsNullOrWhiteSpace(cp.Text))
                    {
                        if (string.IsNullOrEmpty(cp.To))
                        {
                            MPChat.AddMessage(cp.PlayerId, "", cp.Text);
                            Broadcast(MessageEnvelope.Create(MessageType.Chat, "host", cp));
                        }
                        else if (cp.To == MPConfig.PlayerId)
                        {
                            MPChat.AddMessage(cp.PlayerId, cp.To, cp.Text);   // private to the host
                        }
                        else
                        {
                            SendChatPrivate(cp.PlayerId, cp.To, cp.Text);     // client → client relay
                        }
                    }
                    break;
                }

                case MessageType.RequestSave:
                {
                    // A client hit Save / Save-and-Exit in their pause menu.  Run a
                    // coordinated save — HostSaveNow is thread-safe (it enqueues the
                    // IL2CPP-touching part onto the main thread) so it's fine here on
                    // the poll thread.
                    var rq = env.GetPayload<RequestSavePayload>();
                    string reason = string.IsNullOrEmpty(rq?.Reason) ? "client-request" : rq!.Reason;
                    // Honor the requester's chosen save name as the session name.
                    string sess = MPSaveCoordinator.SanitizeSession(rq?.SaveName ?? "");
                    if (!string.IsNullOrEmpty(sess)) MPSaveCoordinator.ActiveSessionName = sess;
                    Plugin.Logger.LogInfo($"[Server] RequestSave from peer {peer.Id} (reason={reason}, exiting={rq?.Exiting}, name='{rq?.SaveName}') — coordinated save.");
                    MPSaveCoordinator.HostSaveNow(reason);
                    break;
                }

                case MessageType.CashSync:
                {
                    var c = env.GetPayload<CashSyncPayload>();
                    if (c != null && !string.IsNullOrEmpty(c.PlayerId))
                        RecordCash(c.PlayerId, c.Money);   // pure C# dict write — safe here
                    break;
                }

                default:
                    Plugin.Logger.LogWarning($"[Server] Unexpected message type {env.Type} from peer {peer.Id}");
                    break;
            }
        }

        // ── Message handlers ──────────────────────────────────────────────────

        // ── Join control: bans (kick/reject — stand until the host re-hosts)
        //    and mid-game join requests awaiting the host's approval. ────────
        private static readonly HashSet<string> _banned = new();
        private static readonly Dictionary<int, (NetPeer peer, HelloPayload hello)> _pendingJoins = new();

        /// <summary>Snapshot for the host's approval popup.</summary>
        public static List<(int peerId, string playerId)> PendingJoinList
        {
            get
            {
                var outp = new List<(int, string)>();
                lock (_pendingJoins)
                    foreach (var kv in _pendingJoins) outp.Add((kv.Key, kv.Value.hello.PlayerId));
                return outp;
            }
        }

        /// <summary>Host approved a mid-game joiner.</summary>
        public static void AcceptPendingJoin(int peerId)
        {
            (NetPeer peer, HelloPayload hello) entry;
            lock (_pendingJoins)
            {
                if (!_pendingJoins.TryGetValue(peerId, out entry)) return;
                _pendingJoins.Remove(peerId);
            }
            if (entry.peer.ConnectionState != ConnectionState.Connected)
            { Plugin.Logger.LogInfo($"[Server] join request from '{entry.hello.PlayerId}' expired (disconnected)."); return; }
            Plugin.Logger.LogInfo($"[Server] host ACCEPTED mid-game join: '{entry.hello.PlayerId}'.");
            RegisterAndProcessJoin(entry.peer, entry.hello);
        }

        /// <summary>Host rejected a mid-game joiner — banned until re-host.</summary>
        public static void RejectPendingJoin(int peerId)
        {
            (NetPeer peer, HelloPayload hello) entry;
            lock (_pendingJoins)
            {
                if (!_pendingJoins.TryGetValue(peerId, out entry)) return;
                _pendingJoins.Remove(peerId);
            }
            Ban(entry.hello);
            try { entry.peer.Disconnect(System.Text.Encoding.UTF8.GetBytes("BAMP:rejected")); } catch { }
            Plugin.Logger.LogInfo($"[Server] host REJECTED mid-game join: '{entry.hello.PlayerId}' (banned until re-host).");
        }

        /// <summary>Host kicked a lobby player — banned until re-host.</summary>
        public static void KickFromLobby(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || playerId == MPConfig.PlayerId) return;
            _banned.Add(playerId);
            if (StableIdByPlayer.TryGetValue(playerId, out var st) && !string.IsNullOrEmpty(st)) _banned.Add(st);
            foreach (var kv in _peerNames)
                if (kv.Value == playerId)
                {
                    foreach (var p in _clients)
                        if (p.Id == kv.Key) { try { p.Disconnect(System.Text.Encoding.UTF8.GetBytes("BAMP:kicked")); } catch { } break; }
                    break;
                }
            LobbyPlayers.Remove(playerId);
            BroadcastLobbyUpdate();
            Plugin.Logger.LogInfo($"[Server] KICKED '{playerId}' from the lobby (banned until re-host).");
        }

        private static void Ban(HelloPayload hello)
        {
            _banned.Add(hello.PlayerId);
            if (!string.IsNullOrEmpty(hello.StableId)) _banned.Add(hello.StableId);
        }

        /// <summary>Clear join control on a fresh hosting session ("re-form
        /// the lobby" = bans lift, pending requests drop).</summary>
        public static void ResetJoinControl()
        {
            _banned.Clear();
            lock (_pendingJoins) _pendingJoins.Clear();
        }

        private static void HandleHello(NetPeer peer, MessageEnvelope env)
        {
            var hello = env.GetPayload<HelloPayload>();
            if (hello == null) return;

            // Banned (kicked or rejected) — out until the host re-hosts.
            if (_banned.Contains(hello.PlayerId) || (!string.IsNullOrEmpty(hello.StableId) && _banned.Contains(hello.StableId)))
            {
                Plugin.Logger.LogInfo($"[Server] Hello from BANNED '{hello.PlayerId}' — disconnected.");
                try { peer.Disconnect(System.Text.Encoding.UTF8.GetBytes("BAMP:banned")); } catch { }
                return;
            }

            Plugin.Logger.LogInfo($"[Server] Hello from '{hello.PlayerId}' (v{hello.Version})");

            // Mid-game joins need the HOST'S APPROVAL — park the request; the
            // in-game popup accepts or rejects it.
            if (!IsInLobby)
            {
                lock (_pendingJoins) _pendingJoins[peer.Id] = (peer, hello);
                Plugin.Logger.LogInfo($"[Server] mid-game join request from '{hello.PlayerId}' — awaiting host approval.");
                return;
            }

            _clients.Add(peer);
            _peerNames[peer.Id] = hello.PlayerId;
            if (!string.IsNullOrEmpty(hello.StableId))
                StableIdByPlayer[hello.PlayerId] = hello.StableId;

            // Add to lobby and tell everyone
            if (!LobbyPlayers.Contains(hello.PlayerId))
                LobbyPlayers.Add(hello.PlayerId);
            BroadcastLobbyUpdate();
            Plugin.Logger.LogInfo($"[Server] '{hello.PlayerId}' joined lobby. Players: {string.Join(", ", LobbyPlayers)}");
        }

        /// <summary>Approved mid-game join: register the peer and run the
        /// load chain (host-stored save → fresh character).</summary>
        private static void RegisterAndProcessJoin(NetPeer peer, HelloPayload hello)
        {
            {
                _clients.Add(peer);
                _peerNames[peer.Id] = hello.PlayerId;
                if (!string.IsNullOrEmpty(hello.StableId))
                    StableIdByPlayer[hello.PlayerId] = hello.StableId;
                // Late join — keep the roster current for everyone (the in-game F9
                // window reads LobbyPlayers) by adding + re-broadcasting it.
                if (!LobbyPlayers.Contains(hello.PlayerId)) LobbyPlayers.Add(hello.PlayerId);
                BroadcastLobbyUpdate();

                // Mid-session JOIN/RECONNECT into an active MP save session (Phase
                // 4d core): if we hold a stored .hsg for this stableId, send it as
                // LoadData so they load in under their character.  The lobby flow
                // only covers peers present at StartLoadGame — anyone arriving
                // after (e.g. the host re-hosted a save and clicked Start before
                // the client finished re-joining) lands HERE; without this they
                // sat in the lobby forever.  World snapshots are NOT sent now:
                // they flow via MarkPlayerInGame once their scene loads (pre-
                // release: the frozen-until-synced path; post-release: the
                // reconnect branch).  Everything below is pure C# (version path
                // pre-cached) — safe on this poll thread.
                bool sentLoad = false;
                try
                {
                    string session = MPSaveCoordinator.ActiveSessionName;
                    string stable  = hello.StableId ?? "";
                    if (!string.IsNullOrEmpty(session) && !string.IsNullOrEmpty(stable))
                    {
                        var data = MPSaveCoordinator.ReadSaveBytesGzip(session, stable);
                        if (data != null)
                        {
                            // Reclaim: ownership reserved under the absent owner's
                            // stableId (manifest load) re-keys to their live playerId.
                            int rekeyed = 0;
                            foreach (var kv in BuildingOwners)
                                if (kv.Value == stable) { BuildingOwners[kv.Key] = hello.PlayerId; rekeyed++; }
                            if (rekeyed > 0)
                                Plugin.Logger.LogInfo($"[Server] Re-keyed {rekeyed} reserved building(s) to '{hello.PlayerId}'.");

                            var m = MPSaveManager.ReadManifest(session);
                            float cash = m != null ? MPSaveCoordinator.BestCashFor(m, stable)
                                                   : (CashByStableId.TryGetValue(stable, out var c) ? c : 0f);
                            Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                            {
                                SessionName   = session,
                                HsgGzipBase64 = data.Value.b64,
                                RawLength     = data.Value.raw,
                                Money         = cash,
                            }));
                            sentLoad = true;
                            Plugin.Logger.LogInfo($"[Server] Mid-session join: sent LoadData to '{hello.PlayerId}' (session='{session}', {data.Value.raw}B, ${cash:F0}); world state follows once their scene loads.");
                        }
                        else
                        {
                            // No stored .hsg — still instruct the client: load
                            // its LOCAL session copy, else fresh character.
                            float kc = GetKnownCash(hello.PlayerId);
                            Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                            {
                                SessionName = session,
                                HsgGzipBase64 = "",
                                Money = Math.Max(0f, kc),
                                FallbackSettings = LastStartSettings,
                            }));
                            sentLoad = true;
                            Plugin.Logger.LogInfo($"[Server] Mid-session join by '{hello.PlayerId}': no stored .hsg — sent local-or-fresh fallback instruction.");
                        }
                    }
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Mid-session LoadData: {ex.Message}"); }

                // Running game but NO session yet (host never saved): the joiner
                // would otherwise sit in the lobby forever — fresh-character
                // instruction (rejoiners keep nothing in this case anyway).
                if (!sentLoad && string.IsNullOrEmpty(MPSaveCoordinator.ActiveSessionName))
                {
                    try
                    {
                        Send(peer, MessageEnvelope.Create(MessageType.LoadData, "host", new LoadDataPayload
                        {
                            SessionName = "",
                            HsgGzipBase64 = "",
                            Money = Math.Max(0f, GetKnownCash(hello.PlayerId)),
                            FallbackSettings = LastStartSettings,
                        }));
                        sentLoad = true;
                        Plugin.Logger.LogInfo($"[Server] Mid-game join by '{hello.PlayerId}' (no session yet) — sent fresh-character instruction.");
                    }
                    catch (Exception ex2) { Plugin.Logger.LogWarning($"[Server] Mid-game fresh-join: {ex2.Message}"); }
                }
                if (sentLoad) return;

                // Late join — game already in progress, send world snapshot immediately
                if (!BroadcastWorldSnapshotEnabled)
                {
                    Plugin.Logger.LogWarning($"[Server] Late join by '{hello.PlayerId}' — WorldSnapshot SKIPPED (kill-switch active).");
                }
                else
                {
                    // Late join: building these snapshots iterates IL2CPP objects
                    // (gi.BuildingRegistrations, gi.rivalStates, GetRivalData …),
                    // which is unsafe from this background poll thread — marshal
                    // onto the main thread.  (Wave-13 class bug; only fires on a
                    // mid-game join so it had gone unnoticed.)
                    var joinPeer = peer;
                    var joinName = hello.PlayerId;
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        try
                        {
                            var snapshot = BuildWorldSnapshot();
                            Send(joinPeer, MessageEnvelope.Create(MessageType.Welcome, "host", snapshot));
                            Plugin.Logger.LogInfo($"[Server] Late join by '{joinName}', sent world snapshot.");
                            // Rival roster BEFORE BusinessSnapshot so building-owner
                            // ID strings have name entries to resolve to.
                            SendRivalsSnapshotTo(joinPeer);
                            // Business table — sent once, then deltas as they change.
                            SendBusinessSnapshotTo(joinPeer);
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Late-join snapshot: {ex.Message}"); }
                    });
                }
            }
        }

        // ── Startup pause hold ────────────────────────────────────────────────

        private static void HandleClientPlayerInGame(MessageEnvelope env)
        {
            var payload = env.GetPayload<PlayerInGamePayload>();
            MarkPlayerInGame(payload?.PlayerId ?? env.SenderId);
        }

        /// <summary>Send a peer the full world state (owners+market, host profile,
        /// rivals, businesses) — on the main thread (IL2CPP).  Used for both late-join
        /// and the frozen-until-synced startup hold (sent while the client is frozen on
        /// the wait screen, so it applies before anyone gets control).</summary>
        public static void SendWorldStateTo(NetPeer peer)
        {
            if (peer == null) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var snapshot = BuildWorldSnapshot();
                    Send(peer, MessageEnvelope.Create(MessageType.Welcome, "host", snapshot));
                    BroadcastHostProfile();
                    SendRivalsSnapshotTo(peer);
                    SendBusinessSnapshotTo(peer);
                    MPLoadProfiler.Mark($"HOST sent full world state to peer {peer.Id}");
                    Plugin.Logger.LogInfo($"[Server] Sent full world state to peer {peer.Id}.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendWorldStateTo: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Called when a player (host or client) confirms their game SCENE has loaded.
        /// Frozen-until-synced: we do NOT release here — instead we send that peer its
        /// world snapshots (during the freeze).  The host's own scene-load also marks
        /// the host world-ready (its world is the source) and flushes snapshots to any
        /// clients already waiting.  Release happens later, in MarkWorldReady.
        /// </summary>
        public static void MarkPlayerInGame(string playerId)
        {
            bool hostJustReady = false;
            bool released;
            List<NetPeer> sendTo = new();
            lock (_startupLock)
            {
                released = _startupReleased;
                if (released)
                {
                    // Mid-session reconnect (Phase 4d): the session is already live,
                    // so the frozen-until-synced flow won't run for this player —
                    // serve their world state directly now their scene is loaded.
                    if (playerId != MPConfig.PlayerId)
                        foreach (var p in _clients)
                            if (_peerNames.TryGetValue(p.Id, out var nm) && nm == playerId)
                                sendTo.Add(p);
                }
            }
            if (released)
            {
                foreach (var p in sendTo) SendWorldStateTo(p);
                if (sendTo.Count > 0)
                {
                    Plugin.Logger.LogInfo($"[Server] Reconnect: '{playerId}' scene loaded — sent live world state.");
                    MPChat.AddNotice($"{playerId} reconnected.");
                    BroadcastChat("", $"— {playerId} reconnected.");
                    // Lift the pause we applied when they dropped (only the
                    // disconnect pause — a deliberate manual pause stays).
                    ResumeFromDisconnectPause();
                }
                return;
            }

            lock (_startupLock)
            {
                if (_startupReleased) return;
                _inGamePlayers.Add(playerId);
                Plugin.Logger.LogInfo($"[Server] Scene loaded: '{playerId}' ({_inGamePlayers.Count}/{LobbyPlayers.Count})");
                MPLoadProfiler.Mark($"HOST scene-loaded '{playerId}' ({_inGamePlayers.Count}/{LobbyPlayers.Count})");

                bool isHost = playerId == MPConfig.PlayerId;
                if (isHost && !_hostSnapshotsReady)
                {
                    // The host's own world loaded — it's the source of truth, so it can
                    // serve snapshots now.  Flush to any clients already on the wait screen.
                    _hostSnapshotsReady = true;
                    hostJustReady = true;
                    foreach (var p in _clients)
                        if (_peerNames.TryGetValue(p.Id, out var nm) && _inGamePlayers.Contains(nm))
                            sendTo.Add(p);
                }
                else if (!isHost && _hostSnapshotsReady)
                {
                    // A client's scene is ready and the host can serve — send its world now.
                    foreach (var p in _clients)
                        if (_peerNames.TryGetValue(p.Id, out var nm) && nm == playerId)
                            sendTo.Add(p);
                }
            }

            foreach (var p in sendTo) SendWorldStateTo(p);
            // The host does NOT mark itself world-ready here.  Its game scene has
            // loaded (PlayerController spawned) so it can SERVE snapshots, but the
            // host's loading OVERLAY is usually still up and the world is still
            // streaming in.  MPCanvasUI.TickOverlayFreezeGate marks the host
            // world-ready only once that overlay has cleared — so the freeze holds
            // until the host (typically the last to finish loading) has truly
            // entered the game.
            if (hostJustReady)
                MPLoadProfiler.Mark("HOST world loaded — awaiting loading overlay before world-ready");
            BroadcastStartupStatus(WorldWaitingList());
        }

        /// <summary>A player has APPLIED the world sync.  Once everyone is world-ready
        /// the startup hold releases for all — so the game unfreezes onto a fully-synced
        /// world.  hostSelf marks the host (whose world is authoritative).</summary>
        public static void MarkWorldReady(string playerId, bool hostSelf = false)
        {
            bool release = false;
            lock (_startupLock)
            {
                if (_startupReleased)
                {
                    // Mid-session reconnect: the session-wide release already
                    // happened (one-shot), but THIS player just froze on their
                    // local startup hold and applied the world — ack them with a
                    // direct StartupRelease so they unfreeze.  Idempotent.
                    foreach (var p in _clients)
                        if (_peerNames.TryGetValue(p.Id, out var nm) && nm == playerId)
                        {
                            Send(p, MessageEnvelope.Create(
                                MessageType.StartupRelease, "host", new StartupReleasePayload()));
                            Plugin.Logger.LogInfo($"[Server] Reconnect: released startup hold for '{playerId}'.");
                        }
                    return;
                }
                if (string.IsNullOrEmpty(playerId)) return;
                if (hostSelf && !_hostSnapshotsReady) return;   // host world not loaded yet
                _worldReadyPlayers.Add(playerId);
                int total = LobbyPlayers.Count;
                int ready = LobbyPlayers.Count(p => _worldReadyPlayers.Contains(p));
                Plugin.Logger.LogInfo($"[Server] World-ready: '{playerId}' ({ready}/{total})");
                MPLoadProfiler.Mark($"HOST world-ready '{playerId}' ({ready}/{total})");
                if (total > 0 && ready >= total)
                {
                    _startupReleased = true;
                    release = true;
                }
            }
            if (release) ReleaseStartupHold("all players world-ready");
            else         BroadcastStartupStatus(WorldWaitingList());
        }

        private static void HandleWorldReady(MessageEnvelope env)
        {
            var payload = env.GetPayload<PlayerInGamePayload>();
            MarkWorldReady(payload?.PlayerId ?? env.SenderId);
            // A player whose world just loaded missed any earlier loan-state
            // broadcast (session-load ledger, joins mid-loan) — re-broadcast.
            GameStatePatcher.EnqueueOnMainThread(MPHub.BroadcastLoansIfAny);
        }

        /// <summary>True once the host's own game world has loaded (PlayerController
        /// spawned) so it can serve snapshots — even if its overlay is still up.</summary>
        public static bool HostSnapshotsReady
        {
            get { lock (_startupLock) return _hostSnapshotsReady; }
        }

        /// <summary>True once the host has been marked world-ready (its loading
        /// overlay cleared).  Used by the overlay gate to avoid re-marking.</summary>
        public static bool HostIsWorldReady
        {
            get { lock (_startupLock) return _worldReadyPlayers.Contains(MPConfig.PlayerId); }
        }

        /// <summary>Roster players who are NOT yet world-ready (for the wait screen).</summary>
        private static List<string> WorldWaitingList()
        {
            lock (_startupLock)
                return LobbyPlayers.Where(p => !_worldReadyPlayers.Contains(p)).ToList();
        }

        /// <summary>Players not yet world-ready — for the host's own startup screen.</summary>
        public static List<string> GetStartupWaitingFor()
        {
            lock (_startupLock)
            {
                if (_startupReleased) return new List<string>();
                return LobbyPlayers.Where(p => !_worldReadyPlayers.Contains(p)).ToList();
            }
        }

        private static void BroadcastStartupStatus(List<string> waiting)
        {
            Broadcast(MessageEnvelope.Create(
                MessageType.StartupStatus, "host",
                new StartupStatusPayload { WaitingFor = waiting }));
        }

        /// <summary>
        /// Host-side safety net: force-releases the startup hold even if not every
        /// player has reported in-game (e.g. one is stuck loading).  Called by the
        /// startup-timeout watchdog so the game can never freeze permanently.
        /// </summary>
        public static void ForceReleaseStartupHold()
        {
            lock (_startupLock)
            {
                if (_startupReleased) return;
                _startupReleased = true;
            }
            ReleaseStartupHold("startup timeout");
        }

        private static void ReleaseStartupHold(string reason)
        {
            // Frozen-until-synced: the world snapshots (owners/market, host profile,
            // rivals, businesses) were already sent to each peer DURING the hold (see
            // SendWorldStateTo on scene-load).  By the time we get here everyone is
            // world-ready, so release just unfreezes onto a fully-synced world.
            Plugin.Logger.LogInfo($"[Server] Releasing startup pause hold ({reason}) — world already synced.");
            MPLoadProfiler.Mark($"HOST ReleaseStartupHold ({reason}) — unfreeze (world pre-synced)");
            Broadcast(MessageEnvelope.Create(
                MessageType.StartupRelease, "host", new StartupReleasePayload()));
            GameStatePatcher.EnqueueOnMainThread(() => TimeSync.EndStartupHold());

            // Safety net: if we got here via the timeout watchdog (a client never
            // acked world-ready), make sure everyone still receives the world state.
            if (reason == "startup timeout")
            {
                GameStatePatcher.EnqueueOnMainThread(() =>
                {
                    try { BroadcastHostProfile(); Broadcast(MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", BuildRivalsSnapshot())); BroadcastBusinessSnapshot(); }
                    catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Timeout-release world resend: {ex.Message}"); }
                });
            }
        }

        // ── Manual pause ──────────────────────────────────────────────────────

        /// <summary>Broadcasts the shared manual-pause state to all clients.</summary>
        public static void BroadcastManualPause(bool paused)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(
                MessageType.ManualPause, "host", new ManualPausePayload { Paused = paused }));
            Plugin.Logger.LogInfo($"[Server] ManualPause broadcast: {paused}");
        }

        private static void HandleClientManualPause(NetPeer sender, MessageEnvelope env)
        {
            var payload = env.GetPayload<ManualPausePayload>();
            if (payload == null) return;

            // Apply on the host, then relay to every other client.
            TimeSync.SetManualPause(payload.Paused);
            foreach (var peer in _clients)
                if (peer.Id != sender.Id)
                    Send(peer, MessageEnvelope.Create(
                        MessageType.ManualPause, "host", payload));
            Plugin.Logger.LogInfo($"[Server] ManualPause from {env.SenderId}: {payload.Paused} — relayed.");
        }

        // ── Multiplayer game settings ─────────────────────────────────────────

        /// <summary>
        /// Returns a mod-defined difficulty preset.  "Normal" is the game's vanilla
        /// defaults; "Easy"/"Hard" tweak the obvious knobs.  All presets keep the
        /// multiplayer overrides (no tutorial, no energy need).  Tune values later.
        /// </summary>
        public static GameVariablesDto Preset(string difficulty)
        {
            var dto = new GameVariablesDto();   // defaults already = Normal + MP overrides
            switch (difficulty)
            {
                case "Easy":
                    dto.Difficulty                    = "Easy";
                    dto.StartingMoney                 = 8000;
                    dto.TaxPercentage                 = 5;
                    dto.RivalsDifficultyMultiplier    = 0.6f;
                    dto.EmployeeHourlySalaryMultiplier= 0.85f;
                    break;
                case "Hard":
                    dto.Difficulty                    = "Hard";
                    dto.StartingMoney                 = 2500;
                    dto.TaxPercentage                 = 18;
                    dto.RivalsDifficultyMultiplier    = 1.5f;
                    dto.EmployeeHourlySalaryMultiplier= 1.2f;
                    break;
                default:
                    dto.Difficulty                    = "Normal";
                    break;
            }
            return dto;
        }

        /// <summary>Deep-clones a settings DTO and overrides its starting cash + age,
        /// so each player can be sent the same world settings with a per-player
        /// starting balance + age without aliasing the shared lobby settings object.</summary>
        private static GameVariablesDto CloneWithCash(GameVariablesDto src, int cash, int age)
        {
            GameVariablesDto copy;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(src);
                copy = System.Text.Json.JsonSerializer.Deserialize<GameVariablesDto>(json) ?? new GameVariablesDto();
            }
            catch { copy = new GameVariablesDto(); }
            copy.StartingMoney = cash;
            if (age > 0) copy.StartingAge = age;
            return copy;
        }

        /// <summary>Converts a settings DTO into the game's GameVariables struct.</summary>
        public static GameVariables BuildGameVariables(GameVariablesDto dto)
        {
            var gv = new GameVariables();
            try
            {
                gv.startingAge                       = dto.StartingAge;
                gv.disableAging                      = dto.DisableAging;
                gv.disableEnergy                     = dto.DisableEnergy;
                gv.disableHappiness                  = dto.DisableHappiness;
                gv.allCoursesUnlocked                = dto.AllCoursesUnlocked;
                gv.startingMoney                     = dto.StartingMoney;
                gv.taxPercentage                     = dto.TaxPercentage;
                gv.daysPerYear                       = dto.DaysPerYear;
                gv.marketPriceMultiplier             = dto.MarketPriceMultiplier;
                gv.employeeHourlySalaryMultiplier    = dto.EmployeeHourlySalaryMultiplier;
                gv.bankInterestMultiplier            = dto.BankInterestMultiplier;
                gv.tutorialEnabled                   = dto.TutorialEnabled;
                gv.bankInterestRate                  = dto.BankInterestRate;
                gv.rivalsDifficultyMultiplier        = dto.RivalsDifficultyMultiplier;
                gv.disableVehicleDamage              = dto.DisableVehicleDamage;
                gv.disableVehicleFuel                = dto.DisableVehicleFuel;
                gv.allContactsUnlocked               = dto.AllContactsUnlocked;
                gv.baseCustomerPromotionMultiplier   = dto.BaseCustomerPromotionMultiplier;
                gv.wholesaleUrgentFeeMultiplier      = dto.WholesaleUrgentFeeMultiplier;
                gv.importerUrgentFeeMultiplier       = dto.ImporterUrgentFeeMultiplier;
                gv.disableWholesaleAndImportLimits   = dto.DisableWholesaleAndImportLimits;
                gv.allProductsAvailableFromImporters = dto.AllProductsAvailableFromImporters;
                gv.exportMultiplier                  = dto.ExportMultiplier;

                // difficulty is an enum — set via reflection to avoid a compile-time
                // dependency on the game's Difficulty type name.
                var diffProp = typeof(GameVariables).GetProperty("difficulty");
                if (diffProp != null && !string.IsNullOrEmpty(dto.Difficulty))
                    diffProp.SetValue(gv, Enum.Parse(diffProp.PropertyType, dto.Difficulty));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[Server] BuildGameVariables: {ex.Message}");
            }
            return gv;
        }

        /// <summary>Default multiplayer GameVariables — used for fallback (no-save) paths.</summary>
        public static GameVariables MakeGameVariables() => BuildGameVariables(Preset("Normal"));

        /// <summary>Host setter for the enforce-starting-cash toggle — re-broadcasts the lobby.</summary>
        public static void SetEnforceStartingCash(bool enforce)
        {
            EnforceStartingCash = enforce;
            if (_running && IsInLobby) BroadcastLobbyUpdate();
            Plugin.Logger.LogInfo($"[Server] Enforce starting cash = {enforce}");
        }

        private static void HandleRentRequest(NetPeer peer, MessageEnvelope env)
        {
            var req = env.GetPayload<BuildingOwnershipPayload>();
            if (req == null) return;

            Plugin.Logger.LogInfo($"[Server] RentRequest: {req.AddressKey} by {env.SenderId}");

            // Check availability
            if (BuildingOwners.TryGetValue(req.AddressKey, out var currentOwner) && currentOwner != "")
            {
                // Already taken — deny
                Send(peer, MessageEnvelope.Create(MessageType.RentDeny, "host", req));
                Plugin.Logger.LogInfo($"[Server] Rent denied — {req.AddressKey} already owned by {currentOwner}");
                return;
            }

            // Grant it
            BuildingOwners[req.AddressKey] = env.SenderId;
            req.OwnerPlayerId = env.SenderId;

            // Confirm to the OTHER clients (not the requester — it already rented
            // locally, so re-applying would double-charge it).  They mark it taken.
            var confirm = MessageEnvelope.Create(MessageType.RentConfirm, "host", req);
            foreach (var p in _clients)
                if (p != peer) Send(p, confirm);
            Plugin.Logger.LogInfo($"[Server] Rent confirmed: {req.AddressKey} → {env.SenderId} (relayed to {_clients.Count - 1} other client(s)).");

            // Reflect it in the HOST's own game (main thread — IL2CPP): take the
            // building off the for-rent pool + mark it owned by that player, so the
            // host sees it occupied as the client's business (was missing — the host
            // only updated its dict, never its game state).
            string addr = req.AddressKey, owner = env.SenderId;
            GameStatePatcher.EnqueueOnMainThread(() => GameStatePatcher.HostReflectPlayerRent(addr, owner));
            // (No event-driven save here: ownership is already tracked live on the
            //  host, and cash is live-streamed — so a crash right after a purchase
            //  loses neither.  Business internals ride the periodic autosave.)
        }

        // ── Message handlers (cont.) ──────────────────────────────────────────

        // ── Appearance sync ───────────────────────────────────────────────────

        private static void HandleClientAppearance(MessageEnvelope env)
        {
            var dto = env.GetPayload<PlayerAppearancePayload>();
            if (dto == null) return;
            // Apply + re-broadcast on the main thread (touches Unity objects).
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                RemotePlayerManager.SetAppearance(dto);
                BroadcastAppearanceSync();
            });
        }

        /// <summary>Registers the host's own appearance and syncs the full set to clients.</summary>
        public static void RegisterHostAppearance(PlayerAppearancePayload dto)
        {
            RemotePlayerManager.SetAppearance(dto);
            BroadcastAppearanceSync();
        }

        /// <summary>Broadcasts every known player appearance to all clients.</summary>
        public static void BroadcastAppearanceSync()
        {
            if (!_running) return;
            var payload = new AppearanceSyncPayload { Players = RemotePlayerManager.GetAllAppearances() };
            Broadcast(MessageEnvelope.Create(MessageType.AppearanceSync, "host", payload));
            Plugin.Logger.LogInfo($"[Server] AppearanceSync broadcast ({payload.Players.Count} players).");
        }

        private static void HandlePlayerMove(NetPeer sender, MessageEnvelope env)
        {
            var payload = env.GetPayload<PlayerPositionPayload>();
            if (payload == null) return;

            // Show this player on the host's own screen
            GameStatePatcher.EnqueueOnMainThread(() =>
                RemotePlayerManager.SpawnOrUpdate(payload));

            // Relay to every other connected client
            var bytes  = env.Serialize();
            var writer = new NetDataWriter();
            writer.Put(bytes);
            foreach (var peer in _clients)
                if (peer.Id != sender.Id)
                    peer.Send(writer, DeliveryMethod.Unreliable);
        }

        private static void HandleAnimTrigger(NetPeer sender, MessageEnvelope env)
        {
            var payload = env.GetPayload<AnimTriggerPayload>();
            if (payload == null) return;

            // Play on the host's own screen
            GameStatePatcher.EnqueueOnMainThread(() =>
                RemotePlayerManager.ApplyTrigger(payload.PlayerId, payload.ParamIndex));

            // Relay to every other connected client (reliable — triggers are one-off)
            var bytes  = env.Serialize();
            var writer = new NetDataWriter();
            writer.Put(bytes);
            foreach (var peer in _clients)
                if (peer.Id != sender.Id)
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Broadcasts the host's own animator trigger to all clients.</summary>
        public static void BroadcastAnimTrigger(int paramIndex)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.PlayerAnimTrigger, MPConfig.PlayerId,
                new AnimTriggerPayload { PlayerId = MPConfig.PlayerId, ParamIndex = paramIndex }));
        }

        private static void HandleVehicleSync(NetPeer sender, MessageEnvelope env)
        {
            var payload = env.GetPayload<VehicleFleetPayload>();
            if (payload == null) return;

            // Apply on the host's own screen
            GameStatePatcher.EnqueueOnMainThread(() => VehicleManager.ApplyVehicleFleet(payload));

            // Relay to every other connected client
            var bytes  = env.Serialize();
            var writer = new NetDataWriter();
            writer.Put(bytes);
            foreach (var peer in _clients)
                if (peer.Id != sender.Id)
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Broadcasts the host's own vehicle fleet to all clients.</summary>
        public static void BroadcastVehicleSync(VehicleFleetPayload payload)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.VehicleSync, MPConfig.PlayerId, payload));
        }

        private static void HandleTaxiHail(MessageEnvelope env)
        {
            var payload = env.GetPayload<TaxiHailPayload>();
            if (payload == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => TrafficSync.HostStopTaxi(payload.TaxiIndex));
        }

        /// <summary>Broadcasts the host's AI-traffic snapshot to all clients.</summary>
        public static void BroadcastTrafficSnapshot(TrafficSnapshotPayload payload)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.TrafficSnapshot, "host", payload));
        }

        /// <summary>Broadcasts the host's traffic-light states to all clients.</summary>
        public static void BroadcastTrafficLights(TrafficLightsPayload payload)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.TrafficLights, "host", payload));
        }

        /// <summary>Broadcasts the host's parked-vehicle snapshot to all clients.</summary>
        // ── Business sync (Phase 1: exterior business state) ─────────────────

        /// <summary>Broadcast a single business-changed delta to all clients.</summary>
        public static void BroadcastBusinessChange(BusinessInfo info)
        {
            if (!_running) return;
            var payload = new BusinessChangePayload { Info = info };
            Broadcast(MessageEnvelope.Create(MessageType.BusinessChange, "host", payload));
        }

        /// <summary>Send the full business table to a single peer (on connect).</summary>
        public static void SendBusinessSnapshotTo(LiteNetLib.NetPeer peer)
        {
            if (peer == null) return;
            try
            {
                long t0 = MPLoadProfiler.NowMs;
                var snap = BusinessSync.BuildFullSnapshot();
                MPLoadProfiler.Span($"HOST BuildFullSnapshot ({snap.Businesses.Count} buildings, {snap.BuildingsForSale.Count} for-sale)", t0);
                var env = MessageEnvelope.Create(MessageType.BusinessSnapshot, "host", snap);
                int bytes = env.Serialize().Length;
                Send(peer, env);
                MPLoadProfiler.Mark($"HOST sent BusinessSnapshot to '{peer.Id}': {bytes} bytes ({snap.Businesses.Count} buildings)");
                Plugin.Logger.LogInfo($"[Server] Sent business snapshot to '{peer.Id}': {snap.Businesses.Count} buildings, {snap.BuildingsForSale.Count} for-sale.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendBusinessSnapshotTo: {ex.Message}"); }
        }

        // ── Interior sync (Phase 2) ──────────────────────────────────────────

        /// <summary>Send a single building's interior snapshot to one peer (initial response to InteriorRequest).</summary>
        public static void SendInteriorSnapshotTo(LiteNetLib.NetPeer peer, InteriorSnapshotPayload snap)
        {
            if (!_running || peer == null || snap == null) return;
            try
            {
                Send(peer, MessageEnvelope.Create(MessageType.InteriorSnapshot, "host", snap));
                Plugin.Logger.LogInfo($"[Server] Sent interior snapshot to peer={peer.Id} addr='{snap.AddressKey}': designs={snap.InteriorDesigns.Count} prices={snap.RetailPrices.Count} dirt={snap.DirtSpots.Count}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendInteriorSnapshotTo: {ex.Message}"); }
        }

        /// <summary>Broadcast an interior snapshot to a specific set of peer ids (the building's subscribers).</summary>
        public static void BroadcastInteriorSnapshotTo(System.Collections.Generic.HashSet<int> peerIds, InteriorSnapshotPayload snap)
        {
            if (!_running || peerIds == null || peerIds.Count == 0 || snap == null) return;
            try
            {
                var env = MessageEnvelope.Create(MessageType.InteriorSnapshot, "host", snap);
                byte[] data = env.Serialize();
                int sent = 0;
                foreach (var peer in _server.ConnectedPeerList)
                {
                    if (peer == null) continue;
                    if (!peerIds.Contains(peer.Id)) continue;
                    peer.Send(data, LiteNetLib.DeliveryMethod.ReliableOrdered);
                    sent++;
                }
                if (sent > 0)
                    Plugin.Logger.LogInfo($"[Server] Interior diff broadcast to {sent} subscriber(s) for '{snap.AddressKey}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastInteriorSnapshotTo: {ex.Message}"); }
        }

        // ── Player profile (Wave 5) ──────────────────────────────────────────

        /// <summary>
        /// Resolve a player's display name (in-character).  Falls back to the
        /// PlayerId if the character name isn't known yet (e.g. before the
        /// player has finished character creation or sent their profile).
        /// </summary>
        private static string DisplayNameFor(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return "";
            if (_characterNamesByPlayerId.TryGetValue(playerId, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;
            return playerId;
        }

        /// <summary>
        /// Host-side equivalent of MPClient.SendPlayerProfile.  Reads the
        /// host's own CharacterData.name, writes it into our dict, and
        /// broadcasts to all peers so their UIs use the in-character name.
        /// Called from ReleaseStartupHold once everyone's in-game.
        /// </summary>
        public static void BroadcastHostProfile()
        {
            try
            {
                string name = MPConfig.PlayerId;
                var gi = SaveGameManager.Current;
                if (gi != null && gi.charactersData != null && gi.charactersData.Count > 0)
                {
                    var cd = gi.charactersData[0];
                    var cn = cd?.name?.ToString();
                    if (!string.IsNullOrWhiteSpace(cn)) name = cn;
                }
                _characterNamesByPlayerId[MPConfig.PlayerId] = name;
                // Also seed the host's local UI lookup dict so when host's own
                // UI calls GetRivalName(MPConfig.PlayerId), our Prefix returns
                // the character name (used by leaderboard / popups on host).
                GameStatePatcher.ClientRivalNames[MPConfig.PlayerId] = name;
                string portrait = ""; try { portrait = GameStatePatcher.ReadLocalPortraitBase64(); } catch { }
                int age = 0; try { age = GameStatePatcher.LocalAgeInYears(); } catch { }
                var p = new PlayerProfilePayload { PlayerId = MPConfig.PlayerId, CharacterName = name, PortraitPngBase64 = portrait, AgeInYears = age };
                if (!string.IsNullOrEmpty(portrait)) GameStatePatcher.LocalPortraitSent = true;   // image goes over once
                Broadcast(MessageEnvelope.Create(MessageType.PlayerProfile, "host", p));
                Plugin.Logger.LogInfo($"[Server] Broadcast host profile: PlayerId='{MPConfig.PlayerId}' CharacterName='{name}' age={age} portrait={(string.IsNullOrEmpty(portrait) ? "none" : "yes")}.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastHostProfile: {ex.Message}"); }
        }

        // ── Rivals roster sync (Phase 1d Wave 2) ─────────────────────────────

        /// <summary>
        /// Build a snapshot of host's AI rival roster (id + name pairs) by
        /// walking gi.rivalStates + resolving each id via GetRivalName.  Wave 3
        /// also injects an entry for every human player in the session (their
        /// PlayerId as both id and name for now — display name UX is later).
        /// Receivers (clients) get matching name entries so building popups
        /// can resolve "owned by [player]" without an "undefined" fallback.
        /// </summary>
        public static RivalsSnapshotPayload BuildRivalsSnapshot()
        {
            var snap = new RivalsSnapshotPayload();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi != null && gi.rivalStates != null)
                {
                    foreach (var rs in gi.rivalStates)
                    {
                        if (rs == null) continue;
                        string id = rs.rivalId?.ToString() ?? "";
                        if (string.IsNullOrEmpty(id)) continue;
                        string name = "";
                        try { name = BigAmbitions.Rivals.RivalsHelper.GetRivalName(id) ?? ""; } catch { }
                        snap.Rivals.Add(new RivalInfo { Id = id, Name = name });
                    }
                }

                // Inject every human player as a "rival" entry so receivers
                // can resolve "owned by [player X]" lookups.  Includes the host
                // itself so the host's own PlayerId resolves on every client.
                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (var r in snap.Rivals) if (!string.IsNullOrEmpty(r.Id)) seen.Add(r.Id);

                // Host itself — marked IsPlayer so client renders via Postfix-injected
                // button instead of consuming a slot in the UUID queue (which is sized
                // to the AI-rival template count exactly).
                if (!string.IsNullOrEmpty(MPConfig.PlayerId) && seen.Add(MPConfig.PlayerId))
                    snap.Rivals.Add(new RivalInfo { Id = MPConfig.PlayerId, Name = DisplayNameFor(MPConfig.PlayerId), IsPlayer = true });
                // All connected/known peers
                foreach (var playerId in LobbyPlayers)
                {
                    if (string.IsNullOrEmpty(playerId)) continue;
                    if (!seen.Add(playerId)) continue;
                    snap.Rivals.Add(new RivalInfo { Id = playerId, Name = DisplayNameFor(playerId), IsPlayer = true });
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BuildRivalsSnapshot: {ex.Message}"); }
            return snap;
        }

        public static void SendRivalsSnapshotTo(LiteNetLib.NetPeer peer)
        {
            if (peer == null) return;
            try
            {
                var snap = BuildRivalsSnapshot();
                Send(peer, MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", snap));
                Plugin.Logger.LogInfo($"[Server] Sent rivals snapshot to peer={peer.Id}: {snap.Rivals.Count} rival(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendRivalsSnapshotTo: {ex.Message}"); }
        }

        /// <summary>
        /// The game's exact per-business weekly income, as shown in the rivals
        /// detail breakdown (RivalBusinessesTable.Load, per native decompile):
        ///   RentedByPlayer ? GetAvgDailyIncome(7) * 7
        ///                  : sum of the last 7 entries of reg.dailyIncomes.
        /// AI rival businesses (RentedByPlayer=false) use the simulated
        /// dailyIncomes list (the same source RivalData.WeeklyIncome sums).
        /// </summary>
        public static float WeeklyIncomeForBusiness(BuildingRegistration reg)
        {
            if (reg == null) return 0f;
            try
            {
                if (reg.RentedByPlayer)
                {
                    try { return reg.GetAvgDailyIncome(7) * 7f; } catch { return 0f; }
                }
                var di = reg.dailyIncomes;
                if (di == null) return 0f;
                int c = di.Count;
                int start = c > 7 ? c - 7 : 0;
                float s = 0f;
                for (int i = start; i < c; i++) s += di[i];
                return s;
            }
            catch { return 0f; }
        }

        /// <summary>
        /// Build per-rival stats by iterating gi.BuildingRegistrations and
        /// counting ownership matches per rivalId.  WeeklyIncome currently
        /// approximated as sum of rent revenue from owned-as-landlord buildings;
        /// a finer model (full business revenue) is a later refinement.
        /// </summary>
        public static RivalsStatsSnapshotPayload BuildRivalsStatsSnapshot()
        {
            var snap = new RivalsStatsSnapshotPayload();
            try
            {
                var gi = SaveGameManager.Current;
                if (gi == null) return snap;

                // id → stats dict.  AI rivals come from gi.rivalStates; we read
                // their AUTHORITATIVE income/counts straight from each rival's
                // RivalData via the public RivalsHelper.GetRivalData API.  This
                // does NOT depend on the rivals leaderboard UI having been
                // opened — the old AiRivalDataRefs overlay only had data after
                // the host opened the rivals app once, which is exactly the
                // fragility that left AI income at $0 on the client.
                var byId = new System.Collections.Generic.Dictionary<string, RivalStatsInfo>();
                int aiWithData = 0, aiNonZero = 0;
                if (gi.rivalStates != null)
                {
                    foreach (var rs in gi.rivalStates)
                    {
                        if (rs == null) continue;
                        string id = rs.rivalId?.ToString() ?? "";
                        if (string.IsNullOrEmpty(id)) continue;
                        string name = "";
                        try { name = BigAmbitions.Rivals.RivalsHelper.GetRivalName(id) ?? ""; } catch { }
                        var info = new RivalStatsInfo { Id = id, Name = name };
                        try
                        {
                            var rd = BigAmbitions.Rivals.RivalsHelper.GetRivalData(id);
                            if (rd != null)
                            {
                                info.WeeklyIncome         = rd.WeeklyIncome;            // authoritative game calc
                                // Leaderboard ROW counts the Retail/Office subset
                                // (HostCountDiag: lb.ownedBusinesses.Count ==
                                // rd.ownedRetailOfficeBusinesses.Count) — this is why
                                // the host excludes factories from the count.  Send
                                // THAT so the client's row matches.
                                info.OwnedBusinessesCount = rd.ownedRetailOfficeBusinesses?.Count ?? 0;
                                info.OwnedBuildingsCount  = rd.ownedBuildings?.Count  ?? 0;
                                try { info.MostActiveNeighborhood = (int)rd.MostActiveNeighborhood; } catch { }
                                try { info.AgeInYears = BigAmbitions.Rivals.RivalsHelper.GetRivalAgeInYears(rd); } catch { }
                                try { info.IsDefeated = BigAmbitions.Rivals.RivalsHelper.IsRivalDefeated(id); } catch { }
                                aiWithData++;
                                if (info.WeeklyIncome != 0f) aiNonZero++;

                                // Per-business breakdown (host-authoritative).  The
                                // client can't compute AI business income locally
                                // (no sales simulation), so we ship name/type/income
                                // per business, keyed by AddressKey.  Iterate
                                // ownedRetailOfficeBusinesses — that is the EXACT set
                                // the leaderboard counts AND the detail breakdown
                                // shows (Retail+Office+Cinema+Theater; factory/
                                // warehouse excluded).  Confirmed via HostBizDiag
                                // (which listed a Cinema + Theater but no factory).
                                if (rd.ownedRetailOfficeBusinesses != null)
                                {
                                    foreach (var reg in rd.ownedRetailOfficeBusinesses)
                                    {
                                        if (reg == null) continue;
                                        try
                                        {
                                            string addr = GameStateReader.AddressKey(reg);
                                            // EXACT per-business income, replicating the game's
                                            // RivalBusinessesTable.Load (from native decompile):
                                            //   RentedByPlayer ? GetAvgDailyIncome(7)*7
                                            //                  : Sum(last 7 of reg.dailyIncomes)
                                            // AI rival businesses (RentedByPlayer=false) use the
                                            // simulated dailyIncomes list — the same source the
                                            // matching leaderboard total sums.  Fully computable
                                            // here for every rival; no UI capture needed.
                                            float wkInc = WeeklyIncomeForBusiness(reg);

                                            info.Businesses.Add(new RivalBusinessInfo
                                            {
                                                AddressKey   = addr,
                                                BusinessName = reg.BusinessName?.ToString() ?? "",
                                                BusinessType = (int)reg.businessTypeName,
                                                WeeklyIncome = wkInc,
                                            });
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] GetRivalData('{id}'): {ex.Message}"); }
                        byId[id] = info;
                    }
                }
                Plugin.Logger.LogInfo($"[Server] AI stats via GetRivalData: rivals={byId.Count} withData={aiWithData} nonZeroIncome={aiNonZero}.");

                // Seed host + connected players so they appear with at least
                // their identity even before they own anything.  Players are
                // NOT in RivalDataCache, so GetRivalData skipped them above.
                void SeedPlayer(string playerId)
                {
                    if (string.IsNullOrEmpty(playerId)) return;
                    if (!byId.ContainsKey(playerId))
                        byId[playerId] = new RivalStatsInfo { Id = playerId, Name = DisplayNameFor(playerId) };
                }
                SeedPlayer(MPConfig.PlayerId);
                foreach (var p in LobbyPlayers) SeedPlayer(p);

                // Other connected clients: use their self-reported stats — the
                // host's gi doesn't know what other players own (no client→host
                // ownership sync).  Pushed via RivalsStatsRequest when that
                // client opens its own rivals app.
                foreach (var kv in _clientSelfStats)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (kv.Key == MPConfig.PlayerId) continue;   // host computes own below
                    if (!byId.TryGetValue(kv.Key, out var entry)) continue;
                    var req = kv.Value;
                    entry.OwnedBuildingsCount  = req.SelfOwnedBuildingsCount;
                    entry.OwnedBusinessesCount = req.SelfOwnedBusinessesCount;
                    entry.WeeklyIncome         = req.SelfWeeklyIncome;
                }

                // Host's OWN player stats from gi.  AI rival counts now come
                // from GetRivalData above, so this only handles the host
                // player's owned buildings / operated businesses — no per-reg
                // AI counting (which previously DOUBLE-COUNTED on top of the
                // RivalData numbers and corrupted AI income).
                if (byId.TryGetValue(MPConfig.PlayerId, out var hostStat) && gi.BuildingRegistrations != null)
                {
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        try
                        {
                            if (reg.BuildingOwnedByPlayer) hostStat.OwnedBuildingsCount++;
                            // Residential rentals are HOMES, not businesses.
                            bool isResidential = false;
                            try { isResidential = reg.BuildingCached != null && reg.BuildingCached.BuildingType == Buildings.BuildingType.Residential; } catch { }
                            if (reg.RentedByPlayer && !isResidential)
                            {
                                hostStat.OwnedBusinessesCount++;
                                try { hostStat.WeeklyIncome += reg.RentPerDay * 7f; } catch { }
                            }
                        }
                        catch { }
                    }
                }

                snap.Stats.AddRange(byId.Values);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BuildRivalsStatsSnapshot: {ex.Message}"); }
            return snap;
        }

        public static void SendRivalsStatsSnapshotTo(LiteNetLib.NetPeer peer)
        {
            if (peer == null) return;
            try
            {
                var snap = BuildRivalsStatsSnapshot();
                Send(peer, MessageEnvelope.Create(MessageType.RivalsStatsSnapshot, "host", snap));
                Plugin.Logger.LogInfo($"[Server] Sent rivals stats snapshot to peer={peer.Id}: {snap.Stats.Count} stat block(s).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendRivalsStatsSnapshotTo: {ex.Message}"); }
        }

        // Collapse bursts: at go-live the release path AND the first for-sale check
        // both want to send a full table within a few hundred ms.  One is enough.
        private static int _lastFullSnapshotTick = -100000;

        /// <summary>Broadcast the full business table to all peers (on for-sale list change).</summary>
        public static void BroadcastBusinessSnapshot()
        {
            if (!_running) return;
            if (Environment.TickCount - _lastFullSnapshotTick < 3000)
            {
                Plugin.Logger.LogInfo("[Server] BusinessSnapshot broadcast skipped (duplicate within 3s).");
                MPLoadProfiler.Mark("HOST BusinessSnapshot broadcast SKIPPED (dedupe <3s)");
                return;
            }
            _lastFullSnapshotTick = Environment.TickCount;
            try
            {
                long t0 = MPLoadProfiler.NowMs;
                var snap = BusinessSync.BuildFullSnapshot();
                MPLoadProfiler.Span($"HOST BuildFullSnapshot (broadcast; {snap.Businesses.Count} buildings)", t0);
                var env = MessageEnvelope.Create(MessageType.BusinessSnapshot, "host", snap);
                int bytes = env.Serialize().Length;
                Broadcast(env);
                MPLoadProfiler.Mark($"HOST broadcast BusinessSnapshot: {bytes} bytes ({snap.Businesses.Count} buildings)");
                Plugin.Logger.LogInfo($"[Server] Broadcast business snapshot: {snap.Businesses.Count} buildings, {snap.BuildingsForSale.Count} for-sale.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] BroadcastBusinessSnapshot: {ex.Message}"); }
        }

        public static void BroadcastParkedSnapshot(ParkedSnapshotPayload payload)
        {
            if (!_running) return;
            Broadcast(MessageEnvelope.Create(MessageType.ParkedSnapshot, "host", payload));
        }

        // ── Broadcast helpers ─────────────────────────────────────────────────

        public static void BroadcastRentConfirmToClients(string addressKey, float dailyRent, float lastDeposit)
        {
            var payload = new BuildingOwnershipPayload
            {
                AddressKey    = addressKey,
                OwnerPlayerId = "host",
                DailyRent     = dailyRent,
                LastDeposit   = lastDeposit
            };
            Broadcast(MessageEnvelope.Create(MessageType.RentConfirm, "host", payload));
        }

        private static void BroadcastLobbyUpdate()
        {
            var payload = new LobbyUpdatePayload
            {
                Players = new List<string>(LobbyPlayers),
                EnforceStartingCash = EnforceStartingCash,
                Ages = new Dictionary<string, int>(StartingAgeByPlayer),
                LoadMode = !string.IsNullOrEmpty(ChosenLoadSession),   // resuming a save
                LoadSessionName = ChosenLoadSession,
            };
            Broadcast(MessageEnvelope.Create(MessageType.LobbyUpdate, "host", payload));
        }

        public static void BroadcastVacate(string addressKey)
        {
            var payload = new BuildingOwnershipPayload { AddressKey = addressKey, OwnerPlayerId = "" };
            Broadcast(MessageEnvelope.Create(MessageType.VacateNotify, "host", payload));
        }

        /// <summary>
        /// Broadcasts the host's own player position to all connected clients.
        /// Called from MPCanvasUI at ~10 Hz.
        /// </summary>
        public static void BroadcastPlayerPosition(PlayerPositionPayload payload)
        {
            if (!_running) return;
            var env    = MessageEnvelope.Create(MessageType.PlayerMove, MPConfig.PlayerId, payload);
            var bytes  = env.Serialize();
            var writer = new NetDataWriter();
            writer.Put(bytes);
            foreach (var peer in _clients)
                peer.Send(writer, DeliveryMethod.Unreliable);
        }

        /// <summary>
        /// Sends a full WorldSnapshot to every connected client.
        /// Called ~4 seconds after the host detects the game scene has loaded.
        /// </summary>
        // Toggle for host's WorldSnapshot broadcast.  Gating was added during
        // the backlog #6 entry-bug investigation; default restored to true so
        // rent sync continues to work in normal play.  Can be flipped at
        // runtime for ad-hoc diagnostic experiments.
        public static bool BroadcastWorldSnapshotEnabled { get; set; } = true;

        public static void BroadcastWorldSnapshotToAll()
        {
            if (!_running) return;
            if (!BroadcastWorldSnapshotEnabled)
            {
                Plugin.Logger.LogWarning("[Server] WorldSnapshot broadcast SKIPPED (host kill-switch active).");
                return;
            }
            var snapshot = BuildWorldSnapshot();
            Broadcast(MessageEnvelope.Create(MessageType.Welcome, "host", snapshot));
            Plugin.Logger.LogInfo("[Server] WorldSnapshot broadcast to all clients.");
        }

        private static void BroadcastPlayerLeft(string playerId)
        {
            var payload = new PlayerLeftPayload { PlayerId = playerId };
            Broadcast(MessageEnvelope.Create(MessageType.PlayerLeft, "host", payload));
            Plugin.Logger.LogInfo($"[Server] PlayerLeft broadcast for '{playerId}'");
        }

        public static void BroadcastMarketSnapshot(string marketJson)
        {
            var payload = new MarketSnapshotPayload { MarketEntriesJson = marketJson };
            Broadcast(MessageEnvelope.Create(MessageType.MarketSnapshot, "host", payload));
        }

        /// <summary>
        /// Broadcasts the host's current game day and time-of-day to all clients.
        /// Called from MPCanvasUI every few seconds as a drift-alignment heartbeat.
        /// </summary>
        public static void BroadcastGameTime(float? speedOverride = null)
        {
            if (!_running) return;
            var (day, hour) = GameStateReader.GetGameTime();
            float speed = speedOverride ?? UnityEngine.Time.timeScale;

            var payload = new GameTimeSyncPayload { Day = day, TimeOfDay = hour, Speed = speed };
            Broadcast(MessageEnvelope.Create(MessageType.GameTimeSync, "host", payload));
            Plugin.Logger.LogInfo($"[Server] GameTimeSync: day={day} hour={hour:F1} speed={speed:F2}×");
        }

        private static void Broadcast(MessageEnvelope env)
        {
            if (_server == null) return;
            var bytes  = env.Serialize();
            var writer = new NetDataWriter();
            writer.Put(bytes);
            foreach (var peer in _clients)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Public wrapper around Broadcast so external code (e.g. Harmony patches) can use it.</summary>
        public static void BroadcastAny(MessageEnvelope env) => Broadcast(env);

        /// <summary>Host: broadcast a Business Hub payload to every client.</summary>
        public static void BroadcastHub<T>(MessageType type, T payload) where T : class
        {
            if (!_running || payload == null) return;
            Broadcast(MessageEnvelope.Create(type, "host", payload));
        }

        /// <summary>Host: send a Business Hub payload to ONE player.</summary>
        public static void SendHubTo<T>(string playerId, MessageType type, T payload) where T : class
        {
            if (!_running || payload == null || string.IsNullOrEmpty(playerId)) return;
            try
            {
                foreach (var kv in _peerNames)
                {
                    if (kv.Value != playerId) continue;
                    foreach (var peer in _clients)
                        if (peer.Id == kv.Key)
                        {
                            Send(peer, MessageEnvelope.Create(type, "host", payload));
                            return;
                        }
                }
                Plugin.Logger.LogWarning($"[Server] hub message: '{playerId}' not connected.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendHubTo: {ex.Message}"); }
        }

        /// <summary>Host: broadcast the consensus rest/skip state.</summary>
        public static void BroadcastRestState(RestSkipStatePayload p)
        {
            if (!_running || p == null) return;
            Broadcast(MessageEnvelope.Create(MessageType.RestSkipState, "host", p));
        }

        /// <summary>Host: broadcast the host's own changed retail prices.</summary>
        public static void BroadcastRetailPrices(RetailPricesPayload p)
        {
            if (!_running || p == null) return;
            Broadcast(MessageEnvelope.Create(MessageType.RetailPrices, "host", p));
        }

        /// <summary>Host: relay a chat line to every connected client.</summary>
        public static void BroadcastChat(string playerId, string text)
        {
            if (!_running || string.IsNullOrWhiteSpace(text)) return;
            Broadcast(MessageEnvelope.Create(MessageType.Chat, "host",
                new ChatPayload { PlayerId = playerId, Text = text }));
        }

        /// <summary>Host: deliver a PRIVATE chat line to one player only.</summary>
        public static void SendChatPrivate(string fromId, string toId, string text)
        {
            if (!_running || string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(toId)) return;
            try
            {
                foreach (var kv in _peerNames)
                {
                    if (kv.Value != toId) continue;
                    foreach (var peer in _clients)
                        if (peer.Id == kv.Key)
                        {
                            Send(peer, MessageEnvelope.Create(MessageType.Chat, "host",
                                new ChatPayload { PlayerId = fromId, To = toId, Text = text }));
                            return;
                        }
                }
                Plugin.Logger.LogWarning($"[Server] private chat: '{toId}' not connected.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] SendChatPrivate: {ex.Message}"); }
        }

        private static void Send(NetPeer peer, MessageEnvelope env)
        {
            var bytes  = env.Serialize();
            var writer = new NetDataWriter();
            writer.Put(bytes);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private static WorldSnapshotPayload BuildWorldSnapshot()
        {
            return new WorldSnapshotPayload
            {
                BuildingOwners   = new Dictionary<string, string>(BuildingOwners),
                MarketEntriesJson = GameStateReader.GetMarketEntriesJson(),
            };
        }

        private static void PollLoop()
        {
            while (_running)
            {
                _server?.PollEvents();
                Thread.Sleep(15);
            }
        }
    }
}
