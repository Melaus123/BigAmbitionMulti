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

        /// <summary>This client's self-chosen starting age (lobby).  Reported to the
        /// host (LobbyPref); the host bakes it into this client's start settings.</summary>
        public static int ChosenStartingAge = 18;

        /// <summary>Per-player starting ages from the host (LobbyUpdate), for lobby display.</summary>
        public static readonly Dictionary<string, int> LobbyAges = new();

        /// <summary>True if the host is resuming a saved game (from LobbyUpdate) — the
        /// client hides the new-game settings (age) since they come from the save.</summary>
        public static bool   HostLoadMode;
        public static string HostLoadSession = "";

        public static bool IsConnected  => _server?.ConnectionState == ConnectionState.Connected;
        public static bool IsConnecting => _running && !IsConnected;

        /// <summary>Why the last connection ended ("ConnectionFailed", "Timeout", …).
        /// Set in OnDisconnected, cleared on Connect — the lobby UI shows it so a
        /// failed join doesn't sit silently behind an alive-looking lobby window.</summary>
        public static string LastDisconnectReason = "";

        /// <summary>True after the connection to the host dropped WHILE IN-GAME
        /// (host quit/crash/network).  Freezes the game and shows a "session
        /// ended" notice; dismissing it (click/Enter in TickStartupScreen) clears
        /// the flag and lets the player continue OFFLINE as a single-player fork
        /// — allowed by design, since the host's save copies are canonical and
        /// overwrite this world on the next rejoin.  Cleared on dismissal /
        /// exit-to-menu / next Connect.</summary>
        public static volatile bool SessionEnded;
        // Set by Disconnect() so a deliberate leave (Leave button, exit-to-menu
        // teardown, reconnect guard) never triggers the session-over lock.
        private static bool _voluntaryDisconnect;

        public static void Connect(string hostIp, int port)
        {
            // A previous session/attempt may still be live (exited to the menu
            // without disconnecting, or an autopilot retry).  Tear it down so we
            // never run two NetManagers/poll threads at once.
            if (_running || _client != null) Disconnect();
            LastDisconnectReason = "";
            SessionEnded = false;
            _voluntaryDisconnect = false;

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
            _voluntaryDisconnect = true;   // deliberate — never the session-over lock
            _running = false;
            _client?.Stop();
            _pollThread?.Join(1000);
            IsInLobby = true;
            LobbyPlayers.Clear();
            _server = null;
            _client = null;
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
                Version  = MyPluginInfo.PLUGIN_VERSION,
                StableId = MPConfig.StableId
            };
            Send(MessageEnvelope.Create(MessageType.Hello, MPConfig.PlayerId, hello));
        }

        private static void OnDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Plugin.Logger.LogWarning($"[Client] Disconnected from host: {info.Reason}");
            LastDisconnectReason = info.Reason.ToString();
            _server  = null;
            // The connection is gone either way — let the poll loop exit so
            // IsConnecting goes false (the UI was stuck showing "Connecting…"
            // forever after a ConnectionFailed).  The NetManager itself is
            // stopped by the next Connect()'s teardown guard.
            _running = false;
            bool voluntary = _voluntaryDisconnect;
            // Clean up all remote-player capsules on the main thread, and release
            // the startup hold — we must not stay frozen after losing the host.
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                RemotePlayerManager.RemoveAll();
                TimeSync.EndStartupHold();
                // Involuntary drop while IN-GAME → the MP session is over for this
                // client.  Freeze + notice; the player can dismiss it and keep
                // playing offline as an SP fork.  (IL2CPP in-game check must run
                // here on the main thread.)
                try
                {
                    if (!voluntary && SaveGameManager.Current != null
                                   && Helpers.PlayerHelper.PlayerController != null)
                    {
                        SessionEnded = true;
                        GameStateReader.SetNativePause(true);   // true pause (red border) under the notice
                        Plugin.Logger.LogWarning("[Client] Host connection lost in-game — session ended; notice shown (dismiss to continue offline as a single-player fork).");
                    }
                }
                catch { }
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
                    MPLoadProfiler.Mark("CLIENT recv StartGameNew");
                    HandleStartGame(env, isNew: true);
                    break;

                case MessageType.StartGameLoad:
                    MPLoadProfiler.Mark("CLIENT recv StartGameLoad");
                    HandleStartGame(env, isNew: false);
                    break;

                case MessageType.Welcome:
                    MPLoadProfiler.Mark("CLIENT recv Welcome (WorldSnapshot)");
                    HandleWelcome(env);
                    break;

                case MessageType.StartupRelease:
                    MPLoadProfiler.Mark("CLIENT recv StartupRelease (go-live)");
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
                    MPLoadProfiler.Mark($"CLIENT recv BusinessSnapshot ({env.Data?.Length ?? 0} bytes json)");
                    HandleBusinessSnapshot(env);
                    break;

                case MessageType.BusinessChange:
                    HandleBusinessChange(env);
                    break;

                case MessageType.InteriorSnapshot:
                    HandleInteriorSnapshot(env);
                    break;

                case MessageType.RivalsSnapshot:
                    MPLoadProfiler.Mark("CLIENT recv RivalsSnapshot");
                    HandleRivalsSnapshot(env);
                    break;

                case MessageType.RivalsStatsSnapshot:
                    HandleRivalsStatsSnapshot(env);
                    break;

                case MessageType.PlayerProfile:
                    HandlePlayerProfile(env);
                    break;

                case MessageType.SaveNow:
                    // Coordinator marshals the actual save onto the main thread.
                    MPSaveCoordinator.ClientHandleSaveNow(env.GetPayload<SaveNowPayload>());
                    break;

                case MessageType.LoadData:
                    // Host shipped us our stored .hsg — write + load it (coordinator
                    // marshals the load onto the main thread).
                    MPLoadProfiler.Mark($"CLIENT recv LoadData (own .hsg, {env.Data?.Length ?? 0} bytes json)");
                    MPSaveCoordinator.ClientHandleLoadData(env.GetPayload<LoadDataPayload>());
                    break;

                case MessageType.Chat:
                {
                    var cp = env.GetPayload<ChatPayload>();
                    if (cp != null) MPChat.AddMessage(cp.PlayerId, cp.To ?? "", cp.Text);   // pure C# — safe on poll thread
                    break;
                }

                case MessageType.RetailPrices:
                {
                    var rp = env.GetPayload<RetailPricesPayload>();
                    if (rp != null) GameStatePatcher.EnqueueOnMainThread(() => MPPriceSync.Apply(rp));
                    break;
                }

                case MessageType.RestSkipState:
                {
                    var rs = env.GetPayload<RestSkipStatePayload>();
                    if (rs != null) GameStatePatcher.EnqueueOnMainThread(() => MPRestSync.ApplyState(rs));
                    break;
                }

                case MessageType.LoanOffer:
                {
                    var lo = env.GetPayload<LoanOfferPayload>();
                    if (lo != null) GameStatePatcher.EnqueueOnMainThread(() => MPHub.ReceiveOffer(lo));
                    break;
                }

                case MessageType.LoanState:
                {
                    var ls = env.GetPayload<LoanStatePayload>();
                    if (ls != null) GameStatePatcher.EnqueueOnMainThread(() => MPHub.ApplyLoanState(ls));
                    break;
                }

                case MessageType.MoneyAdjust:
                {
                    var ma = env.GetPayload<MoneyAdjustPayload>();
                    if (ma != null) GameStatePatcher.EnqueueOnMainThread(() => MPHub.ApplyMoneyDelta(ma.Amount, ma.Reason));
                    break;
                }

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
            LobbyAges.Clear();
            if (payload.Ages != null) foreach (var kv in payload.Ages) LobbyAges[kv.Key] = kv.Value;
            HostLoadMode    = payload.LoadMode;
            HostLoadSession = payload.LoadSessionName ?? "";
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

        private static void HandleInteriorSnapshot(MessageEnvelope env)
        {
            var payload = env.GetPayload<InteriorSnapshotPayload>();
            if (payload == null) return;
            Plugin.Logger.LogInfo($"[Client] Received interior snapshot for '{payload.AddressKey}': designs={payload.InteriorDesigns.Count} prices={payload.RetailPrices.Count} dirt={payload.DirtSpots.Count}.");
            GameStatePatcher.ApplyInteriorSnapshot(payload);
        }

        private static void HandleRivalsSnapshot(MessageEnvelope env)
        {
            var payload = env.GetPayload<RivalsSnapshotPayload>();
            if (payload == null) return;
            Plugin.Logger.LogInfo($"[Client] Received rivals snapshot: {payload.Rivals.Count} rival(s).");
            GameStatePatcher.ApplyRivalsSnapshot(payload);
        }

        private static void HandleRivalsStatsSnapshot(MessageEnvelope env)
        {
            var payload = env.GetPayload<RivalsStatsSnapshotPayload>();
            if (payload == null) return;
            Plugin.Logger.LogInfo($"[Client] Received rivals stats snapshot: {payload.Stats.Count} stat block(s).");
            GameStatePatcher.ApplyRivalsStatsSnapshot(payload);
        }

        private static void HandlePlayerProfile(MessageEnvelope env)
        {
            var p = env.GetPayload<PlayerProfilePayload>();
            if (p == null || string.IsNullOrEmpty(p.PlayerId)) return;
            string name = string.IsNullOrWhiteSpace(p.CharacterName) ? p.PlayerId : p.CharacterName;
            GameStatePatcher.ClientRivalNames[p.PlayerId] = name;
            if (p.AgeInYears > 0) GameStatePatcher.ClientPlayerAges[p.PlayerId] = p.AgeInYears;
            Plugin.Logger.LogInfo($"[Client] PlayerProfile: '{p.PlayerId}' → '{name}' age={p.AgeInYears} portrait={(string.IsNullOrEmpty(p.PortraitPngBase64) ? "none" : "yes")}.");
            // Decode the relayed portrait on the main thread (Texture2D create).
            string portraitB64 = p.PortraitPngBase64;
            string pid = p.PlayerId;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                RemotePlayerManager.UpdateLabel(pid);
                if (!string.IsNullOrEmpty(portraitB64)) GameStatePatcher.ApplyPlayerPortrait(pid, portraitB64);
            });
        }

        /// <summary>
        /// Fire from the RivalLeaderboard.Load Prefix to fetch fresh stats from
        /// host.  Also includes the client's OWN self-stats so host can render
        /// this client's row on host's leaderboard.
        /// </summary>
        public static void SendRivalsStatsRequest()
        {
            if (!IsConnected) return;
            int bldgCount = 0, bizCount = 0;
            float weeklyIncome = 0f;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi != null && gi.BuildingRegistrations != null)
                {
                    foreach (var reg in gi.BuildingRegistrations)
                    {
                        if (reg == null) continue;
                        try
                        {
                            if (reg.BuildingOwnedByPlayer) bldgCount++;
                            // Don't count residential rentals as businesses —
                            // they're player HOMES, not revenue operations.
                            bool isResidential = false;
                            try { isResidential = reg.BuildingCached != null && reg.BuildingCached.BuildingType == Buildings.BuildingType.Residential; } catch { }
                            if (reg.RentedByPlayer && !isResidential)
                            {
                                bizCount++;
                                weeklyIncome += reg.RentPerDay * 7f;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            var p = new RivalsStatsRequestPayload
            {
                PlayerId = MPConfig.PlayerId,
                SelfOwnedBuildingsCount  = bldgCount,
                SelfOwnedBusinessesCount = bizCount,
                SelfWeeklyIncome         = weeklyIncome,
            };
            Send(MessageEnvelope.Create(MessageType.RivalsStatsRequest, MPConfig.PlayerId, p));
            Plugin.Logger.LogInfo($"[Client] Sent RivalsStatsRequest with self-stats: bldgs={bldgCount} biz={bizCount} income=${weeklyIncome:F0}.");
        }

        // ── Outbound ──────────────────────────────────────────────────────────

        /// <summary>Notify the host we just entered building X — host will subscribe us and reply with InteriorSnapshot.</summary>
        public static void SendInteriorRequest(string addressKey)
        {
            if (!IsConnected || string.IsNullOrEmpty(addressKey)) return;
            var p = new InteriorRequestPayload { PlayerId = MPConfig.PlayerId, AddressKey = addressKey };
            Send(MessageEnvelope.Create(MessageType.InteriorRequest, MPConfig.PlayerId, p));
            Plugin.Logger.LogInfo($"[Client] Sent InteriorRequest for '{addressKey}'.");
        }

        /// <summary>Notify the host we left building X — host will unsubscribe us.</summary>
        public static void SendPlayerExitedBuilding(string addressKey)
        {
            if (!IsConnected || string.IsNullOrEmpty(addressKey)) return;
            var p = new PlayerExitedBuildingPayload { PlayerId = MPConfig.PlayerId, AddressKey = addressKey };
            Send(MessageEnvelope.Create(MessageType.PlayerExitedBuilding, MPConfig.PlayerId, p));
            Plugin.Logger.LogInfo($"[Client] Sent PlayerExitedBuilding for '{addressKey}'.");
        }

        /// <summary>
        /// Tells the host this player's game scene has finished loading.
        /// Part of the startup pause hold — the game stays frozen until every
        /// player has sent this.
        /// </summary>
        public static void SendPlayerInGame()
        {
            if (!IsConnected) return;
            _worldReadySent = false;   // re-arm the world-ready ack for this load
            WorldSyncApplied = false;  // re-arm: world sync not yet applied for this load
            var payload = new PlayerInGamePayload { PlayerId = MPConfig.PlayerId };
            Send(MessageEnvelope.Create(MessageType.PlayerInGame, MPConfig.PlayerId, payload));
            Plugin.Logger.LogInfo("[Client] Sent PlayerInGame to host.");

            // Wave 5: send our in-character name so other players' UIs can
            // display it as our identity.  Character name lives in
            // gi.charactersData[0].name; falls back to PlayerId if not yet set.
            SendPlayerProfile();
        }

        private static bool _worldReadySent;

        /// <summary>Set true once the client has APPLIED the bulk world sync (business
        /// snapshot).  The overlay-freeze gate reads this to decide when to send
        /// WorldReady — the client must be BOTH overlay-cleared (frozen) AND
        /// world-synced before it counts as truly in the game.</summary>
        public static bool WorldSyncApplied { get; set; }

        /// <summary>Tell the host we've APPLIED the world sync, so it can release the
        /// frozen-until-synced startup hold once everyone is ready.  One-shot per load.</summary>
        public static void SendWorldReady()
        {
            if (!IsConnected || _worldReadySent) return;
            _worldReadySent = true;
            Send(MessageEnvelope.Create(MessageType.WorldReady, MPConfig.PlayerId,
                new PlayerInGamePayload { PlayerId = MPConfig.PlayerId }));
            Plugin.Logger.LogInfo("[Client] Sent WorldReady to host (world sync applied).");
        }

        /// <summary>
        /// Reads the local CharacterData.name and broadcasts it as the
        /// canonical display name for this player.  Safe to call multiple
        /// times — receivers just update their mapping.  If charactersData
        /// isn't available yet, falls back to MPConfig.PlayerId so the
        /// message still goes out with SOMETHING usable.
        /// </summary>
        public static void SendPlayerProfile()
        {
            if (!IsConnected) return;
            string name = MPConfig.PlayerId;
            try
            {
                var gi = SaveGameManager.Current;
                if (gi != null && gi.charactersData != null && gi.charactersData.Count > 0)
                {
                    var cd = gi.charactersData[0];
                    var cn = cd?.name?.ToString();
                    if (!string.IsNullOrWhiteSpace(cn)) name = cn;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Client] SendPlayerProfile read failed: {ex.Message}"); }
            string portrait = ""; try { portrait = GameStatePatcher.ReadLocalPortraitBase64(); } catch { }
            int age = 0; try { age = GameStatePatcher.LocalAgeInYears(); } catch { }
            var p = new PlayerProfilePayload { PlayerId = MPConfig.PlayerId, CharacterName = name, PortraitPngBase64 = portrait, AgeInYears = age };
            if (!string.IsNullOrEmpty(portrait)) GameStatePatcher.LocalPortraitSent = true;   // image goes over once
            Send(MessageEnvelope.Create(MessageType.PlayerProfile, MPConfig.PlayerId, p));
            Plugin.Logger.LogInfo($"[Client] Sent PlayerProfile: PlayerId='{MPConfig.PlayerId}' CharacterName='{name}' age={age} portrait={(string.IsNullOrEmpty(portrait) ? "none" : portrait.Length + "b64")}.");
        }

        /// <summary>Ships this player's saved .hsg (gzipped) up to the host so the
        /// host holds the canonical copy (Phase 4 — centralized persistence).</summary>
        public static void SendSaveData(string sessionName, MpSlot slot, string hsgGzipBase64, int rawLength)
        {
            if (!IsConnected) return;
            var p = new SaveDataPayload
            {
                SessionName   = sessionName,
                Success       = true,
                Slot          = slot,
                HsgGzipBase64 = hsgGzipBase64,
                RawLength     = rawLength,
            };
            Send(MessageEnvelope.Create(MessageType.SaveData, MPConfig.PlayerId, p));
            Plugin.Logger.LogInfo($"[Client] Sent SaveData: session='{sessionName}' raw={rawLength}B day={slot?.Day}.");
        }

        /// <summary>Sends a business this player runs (in a building it owns) up to the
        /// host so the host applies it + relays to the other players.  Change-driven
        /// (BusinessSync.TickClient only calls this when the building's info changed).</summary>
        public static void SendBusinessChange(BusinessInfo info)
        {
            if (!IsConnected || info == null) return;
            Send(MessageEnvelope.Create(MessageType.BusinessChange, MPConfig.PlayerId,
                new BusinessChangePayload { Info = info }));
        }

        /// <summary>Reports this client's self-chosen starting age to the host so it
        /// shows in everyone's lobby and is baked into this client's start settings.</summary>
        public static void SendLobbyPref(int age)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.LobbyPref, MPConfig.PlayerId,
                new LobbyPrefPayload { PlayerId = MPConfig.PlayerId, Age = age }));
        }

        /// <summary>Sends a chat line to the host, which relays it to everyone
        /// (including us) so the log stays consistent and host-ordered.</summary>
        /// <summary>Sends a Business Hub payload to the host.</summary>
        public static void SendHub<T>(MessageType type, T payload) where T : class
        {
            if (!IsConnected || payload == null) return;
            Send(MessageEnvelope.Create(type, MPConfig.PlayerId, payload));
        }

        /// <summary>Sends this player's rest-vote (started/ended a rest activity).</summary>
        public static void SendRestVote(RestVotePayload p)
        {
            if (!IsConnected || p == null) return;
            Send(MessageEnvelope.Create(MessageType.RestVote, MPConfig.PlayerId, p));
        }

        /// <summary>Sends this player's changed retail prices to the host (who
        /// applies + relays to the other clients).</summary>
        public static void SendRetailPrices(RetailPricesPayload p)
        {
            if (!IsConnected || p == null) return;
            Send(MessageEnvelope.Create(MessageType.RetailPrices, MPConfig.PlayerId, p));
        }

        public static void SendChat(string text, string to = "")
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(text)) return;
            Send(MessageEnvelope.Create(MessageType.Chat, MPConfig.PlayerId,
                new ChatPayload { PlayerId = MPConfig.PlayerId, Text = text, To = to ?? "" }));
        }

        /// <summary>Asks the host to run a coordinated MP save (the user hit Save /
        /// Save-and-Exit in the pause menu).  The host's SaveNow broadcast comes
        /// back to us, so our own save+upload happens through the normal path.</summary>
        public static void SendRequestSave(string reason = "client-menu", bool exiting = false, string saveName = "")
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.RequestSave, MPConfig.PlayerId,
                new RequestSavePayload { Reason = reason, Exiting = exiting, SaveName = saveName }));
            Plugin.Logger.LogInfo($"[Client] Sent RequestSave (reason={reason}, exiting={exiting}, name='{saveName}').");
        }

        /// <summary>Reports this player's current money to the host (Phase 4
        /// loss-minimization — host keeps a near-current cash figure).</summary>
        public static void SendCashSync(float money)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.CashSync, MPConfig.PlayerId,
                new CashSyncPayload { PlayerId = MPConfig.PlayerId, Money = money }));
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
