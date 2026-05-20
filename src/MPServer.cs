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
                    var snapshot = BuildWorldSnapshot();
                    Send(peer, MessageEnvelope.Create(MessageType.Welcome, "host", snapshot));
                    Plugin.Logger.LogInfo($"[Server] Late join by '{hello.PlayerId}', sent world snapshot.");
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
        // CLAUDE-DIAGNOSTIC — host-side gate.  WorldSnapshot is sent ~4s after
        // host enters game; the client can't press F4 in time to gate the apply,
        // so MarkBuildingUnavailable runs before any kill-switch is active.
        // DEFAULTED TO FALSE FOR THIS DIAGNOSTIC TEST — host will not broadcast
        // any WorldSnapshot.  If this fixes the client building-exit bug, the
        // WorldSnapshot apply path was the cause and we permanently fix it.
        // Revert to true once test concludes (it carries the rent sync data).
        public static bool BroadcastWorldSnapshotEnabled { get; set; } = false;

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
