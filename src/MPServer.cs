using LiteNetLib;
using LiteNetLib.Utils;
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

        /// <summary>Address key → player ID who owns it. Empty = unowned.</summary>
        public static readonly Dictionary<string, string> BuildingOwners = new();

        /// <summary>Ordered list of player IDs currently in the lobby (host is always index 0).</summary>
        public static readonly List<string> LobbyPlayers = new();

        /// <summary>True while waiting in the lobby; false once the game has been started.</summary>
        public static bool IsInLobby { get; private set; } = true;

        /// <summary>Host setting: when true, the host's starting cash applies to everyone;
        /// when false, each client sets their own starting cash.</summary>
        public static bool EnforceStartingCash = true;

        private static readonly HashSet<NetPeer>        _clients   = new();
        private static readonly Dictionary<int, string> _peerNames = new(); // peer.Id → playerId

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
        private static readonly object _startupLock = new();
        private static bool _startupReleased;

        public static bool IsRunning      => _running;
        public static int  ConnectedCount => _clients.Count;

        public static void Start(int port)
        {
            // Reset lobby state for a fresh session
            IsInLobby = true;
            LobbyPlayers.Clear();
            LobbyPlayers.Add(MPConfig.PlayerId); // host is always the first player
            EnforceStartingCash = true;
            _peerNames.Clear();
            _listener = new EventBasedNetListener();
            _server   = new NetManager(_listener)
            {
                AutoRecycle = true,
                UnconnectedMessagesEnabled = false,
            };

            _listener.ConnectionRequestEvent += request =>
            {
                // Accept all connections for now; add password later if desired
                request.AcceptIfKey("BAMP");
            };

            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnReceive;

            if (!_server.Start(port))
            {
                Plugin.Logger.LogError($"[Server] Failed to start on port {port}");
                return;
            }

            _running = true;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "BAMP-Server" };
            _pollThread.Start();

            Plugin.Logger.LogInfo($"[Server] Listening on port {port}");
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
            lock (_startupLock) { _inGamePlayers.Clear(); _startupReleased = false; }
        }

        /// <summary>Host clicked "Start New Game" in the lobby.</summary>
        public static void StartNewGame(GameVariablesDto settings)
        {
            if (!_running) return;
            IsInLobby = false;

            // Re-arm the startup pause hold for this new game.
            lock (_startupLock) { _inGamePlayers.Clear(); _startupReleased = false; }

            // Tell all clients to start a new game, with the host's chosen settings.
            var payload = new StartGamePayload
            {
                SaveName = "",
                Settings = settings,
                EnforceStartingCash = EnforceStartingCash,
            };
            Broadcast(MessageEnvelope.Create(MessageType.StartGameNew, "host", payload));
            Plugin.Logger.LogInfo($"[Server] StartNewGame ({settings.Difficulty}) sent to all clients.");

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
                    SaveGameManager.New(BuildGameVariables(settings));
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
            IsInLobby = false;

            // Re-arm the startup pause hold for this new game.
            lock (_startupLock) { _inGamePlayers.Clear(); _startupReleased = false; }

            // Tell all clients to load their most recent save
            var payload = new StartGamePayload { SaveName = "" };
            Broadcast(MessageEnvelope.Create(MessageType.StartGameLoad, "host", payload));
            Plugin.Logger.LogInfo("[Server] StartLoadGame sent to all clients.");

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
                if (IsInLobby)
                    BroadcastLobbyUpdate();
                else
                    BroadcastPlayerLeft(leftPlayer); // tell remaining clients to remove the capsule
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
                        waiting = LobbyPlayers.Where(p => !_inGamePlayers.Contains(p)).ToList();
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

            // Free any buildings they owned
            var freed = BuildingOwners
                .Where(kv => kv.Value == (leftPlayer ?? peer.Id.ToString()))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in freed)
            {
                BuildingOwners[key] = "";
                BroadcastVacate(key);
            }

            // Remove the player's capsule from the host's own game
            if (leftPlayer != null)
                GameStatePatcher.EnqueueOnMainThread(() => RemotePlayerManager.Remove(leftPlayer));
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
                        Plugin.Logger.LogInfo($"[Server] PlayerProfile from peer={peer.Id}: PlayerId='{p.PlayerId}' CharacterName='{p.CharacterName}'.  Re-broadcasting roster.");
                        // Forward to all so every client (including the sender)
                        // sees the updated mapping, and rebroadcast rivals roster
                        // so the new name shows up immediately in popups.
                        Broadcast(MessageEnvelope.Create(MessageType.PlayerProfile, "host", p));
                        try
                        {
                            var snap = BuildRivalsSnapshot();
                            Broadcast(MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", snap));
                            Plugin.Logger.LogInfo($"[Server] Re-broadcast rivals snapshot (profile update): {snap.Rivals.Count} rival(s).");
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Rivals re-broadcast on profile: {ex.Message}"); }
                    }
                    break;
                }

                default:
                    Plugin.Logger.LogWarning($"[Server] Unexpected message type {env.Type} from peer {peer.Id}");
                    break;
            }
        }

        // ── Message handlers ──────────────────────────────────────────────────

        private static void HandleHello(NetPeer peer, MessageEnvelope env)
        {
            var hello = env.GetPayload<HelloPayload>();
            if (hello == null) return;

            Plugin.Logger.LogInfo($"[Server] Hello from '{hello.PlayerId}' (v{hello.Version})");
            _clients.Add(peer);
            _peerNames[peer.Id] = hello.PlayerId;

            if (IsInLobby)
            {
                // Add to lobby and tell everyone
                if (!LobbyPlayers.Contains(hello.PlayerId))
                    LobbyPlayers.Add(hello.PlayerId);
                BroadcastLobbyUpdate();
                Plugin.Logger.LogInfo($"[Server] '{hello.PlayerId}' joined lobby. Players: {string.Join(", ", LobbyPlayers)}");
            }
            else
            {
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

        /// <summary>
        /// Called when a player (host or client) confirms their game scene has
        /// finished loading.  Once every roster player is in-game, releases the
        /// startup pause hold for everyone.  One-shot — ignored after release.
        /// </summary>
        public static void MarkPlayerInGame(string playerId)
        {
            bool release = false;
            List<string> waiting;
            lock (_startupLock)
            {
                if (_startupReleased) return;
                _inGamePlayers.Add(playerId);
                int total = LobbyPlayers.Count;
                Plugin.Logger.LogInfo(
                    $"[Server] Player in-game: '{playerId}' ({_inGamePlayers.Count}/{total})");

                waiting = LobbyPlayers.Where(p => !_inGamePlayers.Contains(p)).ToList();
                if (total > 0 && waiting.Count == 0)
                {
                    _startupReleased = true;
                    release = true;
                }
            }
            if (release) ReleaseStartupHold("all players loaded");
            else         BroadcastStartupStatus(waiting);
        }

        /// <summary>Players who have not yet finished loading — for the host's own startup screen.</summary>
        public static List<string> GetStartupWaitingFor()
        {
            lock (_startupLock)
            {
                if (_startupReleased) return new List<string>();
                return LobbyPlayers.Where(p => !_inGamePlayers.Contains(p)).ToList();
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
            Plugin.Logger.LogInfo($"[Server] Releasing startup pause hold ({reason}).");
            Broadcast(MessageEnvelope.Create(
                MessageType.StartupRelease, "host", new StartupReleasePayload()));
            GameStatePatcher.EnqueueOnMainThread(() => TimeSync.EndStartupHold());

            // Host profile (Wave 5) — broadcast the host's own character name
            // BEFORE the rivals snapshot so it's available when receivers
            // populate display names for player entries.
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try { BroadcastHostProfile(); }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Host profile broadcast: {ex.Message}"); }
            });

            // Rivals roster (Phase 1d Wave 2) — must broadcast BEFORE the
            // business snapshot so client's RivalDataCache is populated when
            // the building owner IDs arrive.
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var snap = BuildRivalsSnapshot();
                    Broadcast(MessageEnvelope.Create(MessageType.RivalsSnapshot, "host", snap));
                    Plugin.Logger.LogInfo($"[Server] Broadcast rivals snapshot: {snap.Rivals.Count} rival(s).");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Initial RivalsSnapshot broadcast: {ex.Message}"); }
            });

            // Business sync — Phase 1: once all clients are in-game, send the
            // full exterior business table to everyone.  Event-driven deltas
            // (BusinessChange) follow from BusinessSync.Tick after this.
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    var snap = BusinessSync.BuildFullSnapshot();
                    Broadcast(MessageEnvelope.Create(MessageType.BusinessSnapshot, "host", snap));
                    Plugin.Logger.LogInfo($"[Server] Broadcast business snapshot: {snap.Businesses.Count} buildings.");
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[Server] Initial BusinessSnapshot broadcast: {ex.Message}"); }
            });
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

            // Confirm to all clients (including the requester)
            Broadcast(MessageEnvelope.Create(MessageType.RentConfirm, "host", req));
            Plugin.Logger.LogInfo($"[Server] Rent confirmed: {req.AddressKey} → {env.SenderId}");
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
                var snap = BusinessSync.BuildFullSnapshot();
                Send(peer, MessageEnvelope.Create(MessageType.BusinessSnapshot, "host", snap));
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

        /// <summary>Broadcast the full business table to all peers (on for-sale list change).</summary>
        public static void BroadcastBusinessSnapshot()
        {
            if (!_running) return;
            try
            {
                var snap = BusinessSync.BuildFullSnapshot();
                Broadcast(MessageEnvelope.Create(MessageType.BusinessSnapshot, "host", snap));
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
