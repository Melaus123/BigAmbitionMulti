using LiteNetLib;
using LiteNetLib.Utils;
using UI.Load;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Runs on non-host players. Connects to the host, sends requests,
    /// and applies world state updates received from the host.
    /// </summary>
    public static class MPClient
    {
        private static NetManager?   _client;
        private static EventBasedNetListener? _listener;
        private static NetPeer?      _server;

        private static Thread? _pollThread;
        private static volatile bool _running;

        /// <summary>Player list last received from the host's lobby update.</summary>
        public static readonly List<string> LobbyPlayers = new();

        /// <summary>True while in the pre-game lobby; false once the host starts the game.</summary>
        public static bool IsInLobby { get; private set; } = true;

        /// <summary>True if the host enforces one starting cash for everyone (from LobbyUpdate).</summary>
        public static bool EnforceStartingCash { get; private set; } = true;

        /// <summary>This client's chosen starting cash (used only when EnforceStartingCash is false).</summary>
        public static int ChosenStartingCash = 4200;

        public static bool IsConnected  => _server?.ConnectionState == ConnectionState.Connected;
        public static bool IsConnecting => _running && !IsConnected;

        public static void Connect(string hostIp, int port)
        {
            _listener = new EventBasedNetListener();
            _client   = new NetManager(_listener) { AutoRecycle = true };

            _listener.PeerConnectedEvent += OnConnected;
            _listener.PeerDisconnectedEvent += OnDisconnected;
            _listener.NetworkReceiveEvent += OnReceive;

            _client.Start();
            _server = _client.Connect(hostIp, port, "BAMP");

            _running = true;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "BAMP-Client" };
            _pollThread.Start();

            Plugin.Logger.LogInfo($"[Client] Connecting to {hostIp}:{port}...");
        }

        public static void Disconnect()
        {
            _running = false;
            _client?.Stop();
            _pollThread?.Join(1000);
            IsInLobby = true;
            LobbyPlayers.Clear();
            _server = null;
        }

        // ── Events ────────────────────────────────────────────────────────────

        private static void OnConnected(NetPeer peer)
        {
            Plugin.Logger.LogInfo("[Client] Connected to host.");
            _server   = peer;
            IsInLobby = true;
            LobbyPlayers.Clear();

            // Send Hello
            var hello = new HelloPayload
            {
                PlayerId = MPConfig.PlayerId,
                Version  = MyPluginInfo.PLUGIN_VERSION
            };
            Send(MessageEnvelope.Create(MessageType.Hello, MPConfig.PlayerId, hello));
        }

        private static void OnDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Plugin.Logger.LogWarning($"[Client] Disconnected from host: {info.Reason}");
            _server = null;
            // Clean up all remote-player capsules on the main thread, and release
            // the startup hold — we must not stay frozen after losing the host.
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                RemotePlayerManager.RemoveAll();
                TimeSync.EndStartupHold();
            });
        }

        private static void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            var bytes = reader.GetRemainingBytes();
            var env   = MessageEnvelope.Deserialize(bytes);
            if (env == null) return;

            switch (env.Type)
            {
                case MessageType.LobbyUpdate:
                    HandleLobbyUpdate(env);
                    break;

                case MessageType.StartGameNew:
                    HandleStartGame(env, isNew: true);
                    break;

                case MessageType.StartGameLoad:
                    HandleStartGame(env, isNew: false);
                    break;

                case MessageType.Welcome:
                    HandleWelcome(env);
                    break;

                case MessageType.StartupRelease:
                    HandleStartupRelease();
                    break;

                case MessageType.StartupStatus:
                    HandleStartupStatus(env);
                    break;

                case MessageType.ManualPause:
                    HandleManualPause(env);
                    break;

                case MessageType.RentConfirm:
                    HandleRentConfirm(env);
                    break;

                case MessageType.RentDeny:
                    HandleRentDeny(env);
                    break;

                case MessageType.VacateNotify:
                    HandleVacate(env);
                    break;

                case MessageType.MarketSnapshot:
                    HandleMarketSnapshot(env);
                    break;

                case MessageType.PlayerMove:
                    HandlePlayerMove(env);
                    break;

                case MessageType.PlayerAnimTrigger:
                    HandleAnimTrigger(env);
                    break;

                case MessageType.VehicleSync:
                    HandleVehicleSync(env);
                    break;

                case MessageType.TrafficSnapshot:
                    HandleTrafficSnapshot(env);
                    break;

                case MessageType.TrafficLights:
                    HandleTrafficLights(env);
                    break;

                case MessageType.ParkedSnapshot:
                    HandleParkedSnapshot(env);
                    break;

                case MessageType.PlayerLeft:
                    HandlePlayerLeft(env);
                    break;

                case MessageType.AppearanceSync:
                    HandleAppearanceSync(env);
                    break;

                case MessageType.GameTimeSync:
                    HandleGameTimeSync(env);
                    break;

                case MessageType.BusinessSnapshot:
                    HandleBusinessSnapshot(env);
                    break;

                case MessageType.BusinessChange:
                    HandleBusinessChange(env);
                    break;

                default:
                    Plugin.Logger.LogWarning($"[Client] Unknown message type: {env.Type}");
                    break;
            }
        }

        // ── Message handlers ──────────────────────────────────────────────────

        private static void HandleLobbyUpdate(MessageEnvelope env)
        {
            var payload = env.GetPayload<LobbyUpdatePayload>();
            if (payload == null) return;
            LobbyPlayers.Clear();
            LobbyPlayers.AddRange(payload.Players);
            EnforceStartingCash = payload.EnforceStartingCash;
            Plugin.Logger.LogInfo($"[Client] Lobby: {string.Join(", ", LobbyPlayers)} " +
                                  $"(starting cash {(EnforceStartingCash ? "enforced by host" : "per-player")})");
        }

        private static void HandleStartGame(MessageEnvelope env, bool isNew)
        {
            IsInLobby = false;
            var  sp              = env.GetPayload<StartGamePayload>();
            var  newGameSettings = sp?.Settings ?? MPServer.Preset("Normal");
            bool enforceCash     = sp?.EnforceStartingCash ?? true;
            // If the host hasn't enforced a fixed amount, this client uses its
            // own starting cash; every other setting still comes from the host.
            if (!enforceCash)
                newGameSettings.StartingMoney = ChosenStartingCash;
            Plugin.Logger.LogInfo(
                $"[Client] Host started game ({(isNew ? "new" : "load")}); " +
                $"starting cash={newGameSettings.StartingMoney}.");

            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    if (isNew)
                    {
                        Plugin.Logger.LogInfo("[Client] Initialising new game and loading character creation...");
                        // New() first — mirrors MainMenuController.StartNewGame(difficulty).
                        // Without it SaveGameManager.Current is null and the intro's
                        // Start button silently does nothing.
                        SaveGameManager.New(MPServer.BuildGameVariables(newGameSettings));
                        LoadScene.LoadIntro(false);
                        Plugin.Logger.LogInfo("[Client] New game init + intro scene loaded.");
                    }
                    else
                    {
                        Plugin.Logger.LogInfo("[Client] Loading most recent save...");
                        var versionPath = SaveGamePathHelper.CurrentVersionFolderPath();
                        var saves = SaveGamePathHelper.GetAllSaveGamesFromVersion(versionPath);

                        if (saves == null || saves.Count == 0)
                        {
                            Plugin.Logger.LogWarning("[Client] No saves found — starting new game instead.");
                            SaveGameManager.New(MPServer.MakeGameVariables());
                            LoadScene.LoadIntro(false);
                            return;
                        }

                        var save = saves[0];
                        Plugin.Logger.LogInfo($"[Client] Loading save: {save.alias}");
                        SaveGameManager.Load(save, true);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[Client] HandleStartGame error: {ex}");
                }
            });
        }

        private static void HandleManualPause(MessageEnvelope env)
        {
            var payload = env.GetPayload<ManualPausePayload>();
            if (payload == null) return;
            TimeSync.SetManualPause(payload.Paused);
        }

        private static void HandleStartupRelease()
        {
            Plugin.Logger.LogInfo("[Client] StartupRelease received — all players loaded, game resuming.");
            StartupWaitingFor = new List<string>();
            GameStatePatcher.EnqueueOnMainThread(() => TimeSync.EndStartupHold());
        }

        /// <summary>
        /// Players who have not yet finished loading, per the host's last broadcast.
        /// Drives the client's "waiting for &lt;player&gt;" startup screen.
        /// </summary>
        public static List<string> StartupWaitingFor { get; private set; } = new();

        private static void HandleStartupStatus(MessageEnvelope env)
        {
            var payload = env.GetPayload<StartupStatusPayload>();
            if (payload == null) return;
            StartupWaitingFor = payload.WaitingFor ?? new List<string>();
            Plugin.Logger.LogInfo(
                $"[Client] StartupStatus: waiting for {string.Join(", ", StartupWaitingFor)}");
        }

        private static void HandleWelcome(MessageEnvelope env)
        {
            var snap = env.GetPayload<WorldSnapshotPayload>();
            if (snap == null) return;

            Plugin.Logger.LogInfo($"[Client] Received world snapshot: {snap.BuildingOwners.Count} buildings tracked.");
            GameStatePatcher.ApplyWorldSnapshot(snap);
        }

        private static void HandleRentConfirm(MessageEnvelope env)
        {
            var payload = env.GetPayload<BuildingOwnershipPayload>();
            if (payload == null) return;

            Plugin.Logger.LogInfo($"[Client] RentConfirm: {payload.AddressKey} → {payload.OwnerPlayerId}");
            GameStatePatcher.ApplyBuildingOwnership(payload);
        }

        private static void HandleRentDeny(MessageEnvelope env)
        {
            var payload = env.GetPayload<BuildingOwnershipPayload>();
            if (payload == null) return;

            Plugin.Logger.LogWarning($"[Client] Rent DENIED for {payload.AddressKey} — already taken.");
            // TODO: show in-game notification to the player
        }

        private static void HandleVacate(MessageEnvelope env)
        {
            var payload = env.GetPayload<BuildingOwnershipPayload>();
            if (payload == null) return;

            Plugin.Logger.LogInfo($"[Client] Building vacated: {payload.AddressKey}");
            GameStatePatcher.ApplyBuildingVacated(payload.AddressKey);
        }

        private static void HandlePlayerMove(MessageEnvelope env)
        {
            var payload = env.GetPayload<PlayerPositionPayload>();
            if (payload == null) return;
            if (payload.PlayerId == MPConfig.PlayerId) return; // ignore our own echoed position

            GameStatePatcher.EnqueueOnMainThread(() =>
                RemotePlayerManager.SpawnOrUpdate(payload));
        }

        private static void HandleAnimTrigger(MessageEnvelope env)
        {
            var payload = env.GetPayload<AnimTriggerPayload>();
            if (payload == null) return;
            if (payload.PlayerId == MPConfig.PlayerId) return; // our own echoed trigger
            GameStatePatcher.EnqueueOnMainThread(() =>
                RemotePlayerManager.ApplyTrigger(payload.PlayerId, payload.ParamIndex));
        }

        private static void HandleVehicleSync(MessageEnvelope env)
        {
            var payload = env.GetPayload<VehicleFleetPayload>();
            if (payload == null) return;
            if (payload.OwnerId == MPConfig.PlayerId) return; // our own echoed fleet
            GameStatePatcher.EnqueueOnMainThread(() => VehicleManager.ApplyVehicleFleet(payload));
        }

        private static void HandleTrafficSnapshot(MessageEnvelope env)
        {
            var payload = env.GetPayload<TrafficSnapshotPayload>();
            if (payload == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => TrafficSync.ApplySnapshot(payload));
        }

        private static void HandleTrafficLights(MessageEnvelope env)
        {
            var payload = env.GetPayload<TrafficLightsPayload>();
            if (payload == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => TrafficSync.ApplyTrafficLights(payload));
        }

        private static void HandleParkedSnapshot(MessageEnvelope env)
        {
            var payload = env.GetPayload<ParkedSnapshotPayload>();
            if (payload == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => ParkedVehicleSync.ApplySnapshot(payload));
        }

        private static void HandlePlayerLeft(MessageEnvelope env)
        {
            var payload = env.GetPayload<PlayerLeftPayload>();
            if (payload == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => RemotePlayerManager.Remove(payload.PlayerId));
        }

        private static void HandleAppearanceSync(MessageEnvelope env)
        {
            var payload = env.GetPayload<AppearanceSyncPayload>();
            if (payload == null) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                foreach (var dto in payload.Players)
                    RemotePlayerManager.SetAppearance(dto);
            });
        }

        /// <summary>Sends this player's character appearance to the host.</summary>
        public static void SendAppearance(PlayerAppearancePayload dto)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.PlayerAppearance, MPConfig.PlayerId, dto));
            Plugin.Logger.LogInfo($"[Client] Sent appearance: {RemotePlayerManager.Summary(dto)}");
        }

        private static void HandleGameTimeSync(MessageEnvelope env)
        {
            var payload = env.GetPayload<GameTimeSyncPayload>();
            if (payload == null) return;
            Plugin.Logger.LogInfo(
                $"[Client] GameTimeSync: day={payload.Day} hour={payload.TimeOfDay:F1}");

            // Applies clock-drift correction toward the host's authoritative clock.
            GameStatePatcher.ApplyGameTime(payload);
        }

        private static void HandleMarketSnapshot(MessageEnvelope env)
        {
            var payload = env.GetPayload<MarketSnapshotPayload>();
            if (payload == null) return;

            Plugin.Logger.LogInfo("[Client] Received market snapshot.");
            GameStatePatcher.ApplyMarketSnapshot(payload.MarketEntriesJson);
        }

        private static void HandleBusinessSnapshot(MessageEnvelope env)
        {
            var payload = env.GetPayload<BusinessSnapshotPayload>();
            if (payload == null) return;
            Plugin.Logger.LogInfo($"[Client] Received business snapshot: {payload.Businesses.Count} buildings.");
            GameStatePatcher.ApplyBusinessSnapshot(payload);
        }

        private static void HandleBusinessChange(MessageEnvelope env)
        {
            var payload = env.GetPayload<BusinessChangePayload>();
            if (payload?.Info == null) return;
            GameStatePatcher.ApplyBusinessChange(payload.Info);
        }

        // ── Outbound ──────────────────────────────────────────────────────────

        /// <summary>
        /// Tells the host this player's game scene has finished loading.
        /// Part of the startup pause hold — the game stays frozen until every
        /// player has sent this.
        /// </summary>
        public static void SendPlayerInGame()
        {
            if (!IsConnected) return;
            var payload = new PlayerInGamePayload { PlayerId = MPConfig.PlayerId };
            Send(MessageEnvelope.Create(MessageType.PlayerInGame, MPConfig.PlayerId, payload));
            Plugin.Logger.LogInfo("[Client] Sent PlayerInGame to host.");
        }

        /// <summary>Tells the host this player toggled the manual (pause-button) pause.</summary>
        public static void SendManualPause(bool paused)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(
                MessageType.ManualPause, MPConfig.PlayerId, new ManualPausePayload { Paused = paused }));
            Plugin.Logger.LogInfo($"[Client] Sent ManualPause: {paused}");
        }

        /// <summary>Sends a local animator trigger to the host for relay to all players.</summary>
        public static void SendAnimTrigger(int paramIndex)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.PlayerAnimTrigger, MPConfig.PlayerId,
                new AnimTriggerPayload { PlayerId = MPConfig.PlayerId, ParamIndex = paramIndex }));
        }

        /// <summary>Tells the host this player hailed a traffic taxi (host stops the real one).</summary>
        public static void SendTaxiHail(int taxiIndex)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.TaxiHail, MPConfig.PlayerId,
                new TaxiHailPayload { PlayerId = MPConfig.PlayerId, TaxiIndex = taxiIndex }));
        }

        /// <summary>Sends the local player's vehicle fleet to the host for relay.</summary>
        public static void SendVehicleSync(VehicleFleetPayload payload)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.VehicleSync, MPConfig.PlayerId, payload));
        }

        /// <summary>
        /// Sends the local player's position to the host so it can be relayed
        /// to all other clients.  Called from MPCanvasUI at ~10 Hz.
        /// </summary>
        public static void SendPlayerPosition(PlayerPositionPayload payload)
        {
            if (!IsConnected) return;
            var env    = MessageEnvelope.Create(MessageType.PlayerMove, MPConfig.PlayerId, payload);
            var bytes  = env.Serialize();
            var writer = new NetDataWriter();
            writer.Put(bytes);
            _server?.Send(writer, DeliveryMethod.Unreliable);
        }

        /// <summary>
        /// Called by GamePatches when the local player tries to rent a building.
        /// Instead of executing locally, sends a request to the host.
        /// Returns false if not connected (fall through to local execution).
        /// </summary>
        public static bool RequestRentBuilding(string addressKey, float dailyRent, float lastDeposit)
        {
            if (!IsConnected) return false;

            var payload = new BuildingOwnershipPayload
            {
                AddressKey   = addressKey,
                OwnerPlayerId = MPConfig.PlayerId,
                DailyRent    = dailyRent,
                LastDeposit  = lastDeposit
            };
            Send(MessageEnvelope.Create(MessageType.RentRequest, MPConfig.PlayerId, payload));
            Plugin.Logger.LogInfo($"[Client] Sent RentRequest for {addressKey}");
            return true;
        }

        private static void Send(MessageEnvelope env)
        {
            if (_server == null) return;
            var bytes  = env.Serialize();
            var writer = new NetDataWriter();
            writer.Put(bytes);
            _server.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        private static void PollLoop()
        {
            while (_running)
            {
                _client?.PollEvents();
                Thread.Sleep(15);
            }
        }
    }
}
