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
        // Transport seam (Steam-connect slice 1): the client reaches the host
        // through IClientTransport — LiteNetLib UDP today, Steam relay later.
        // Events fire on the transport's poll thread, exactly as before.
        private static IClientTransport? _transport;
        private static volatile bool _connected;   // set on Connected, cleared on Disconnected/Disconnect

        /// <summary>Player list last received from the host's lobby update.
        /// CONCURRENT: replaced wholesale on the poll thread (lobby update / connect /
        /// disconnect), read on the main thread (lobby UI, roster).  Published as an
        /// immutable snapshot — single writer thread + volatile = safe without a lock.</summary>
        public static IReadOnlyList<string> LobbyPlayers => _lobbyPlayers;
        private static volatile List<string> _lobbyPlayers = new();

        /// <summary>True while in the pre-game lobby; false once the host starts the game.</summary>
        public static bool IsInLobby { get; private set; } = true;

        /// <summary>True if the host enforces one starting cash for everyone (from LobbyUpdate).</summary>
        public static bool EnforceStartingCash { get; private set; } = true;

        /// <summary>This client's chosen starting cash (used only when EnforceStartingCash is false).</summary>
        public static int ChosenStartingCash = 4200;

        /// <summary>This client's self-chosen starting age (lobby).  Reported to the
        /// host (LobbyPref); the host bakes it into this client's start settings.</summary>
        public static int ChosenStartingAge = 18;

        /// <summary>Per-player starting ages from the host (LobbyUpdate), for lobby display.
        /// CONCURRENT: cleared/repopulated on the poll thread (HandleLobbyUpdate),
        /// read per-frame on the main thread (lobby UI).</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> LobbyAges = new();

        /// <summary>True if the host is resuming a saved game (from LobbyUpdate) — the
        /// client hides the new-game settings (age) since they come from the save.</summary>
        public static bool   HostLoadMode;
        public static string HostLoadSession = "";

        public static bool IsConnected  => _connected && _transport is { IsRunning: true };
        public static bool IsConnecting => _transport is { IsRunning: true } && !_connected;

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

        /// <summary>STICKY "the local player is in an MP game world" flag — the single source of truth for
        /// native-time / skip SUPPRESSION (it replaces the live <see cref="IsConnected"/> at those gates).
        /// Latched true every frame we're in the game scene and hosting or connected (MPCanvasUI.LateUpdate),
        /// so it SURVIVES a transient drop+reconnect where IsConnected briefly reads false — that blip used to
        /// switch off ALL native-skip suppression and let the vanilla skip UI run (reconnect bug, 2026-06-19).
        /// Cleared ONLY on full exit-to-menu (scene unload) and the offline-fork dismiss, so a genuine solo
        /// fork (host gone for good) still gets native time. NOT cleared on a mere disconnect.</summary>
        public static volatile bool InMpGame;
        // Set by Disconnect() so a deliberate leave (Leave button, exit-to-menu
        // teardown, reconnect guard) never triggers the session-over lock.
        private static bool _voluntaryDisconnect;

        public static void Connect(string hostIp, int port)
        {
            // A previous session/attempt may still be live (exited to the menu
            // without disconnecting, or an autopilot retry).  Tear it down so we
            // never run two transports/poll threads at once.
            if (_transport != null) Disconnect();
            LastDisconnectReason = "";
            SessionEnded = false;
            _voluntaryDisconnect = false;
            _connected = false;

            var t = new LnlClientTransport();
            t.Connected    += OnConnected;
            t.Disconnected += OnDisconnected;
            t.Received     += OnReceive;
            _transport = t;
            if (!t.Connect(hostIp, port))
            { Plugin.Logger.LogError($"[Client] transport failed to start toward {hostIp}:{port}."); _transport = null; return; }

            Plugin.Logger.LogInfo($"[Client] Connecting to {hostIp}:{port}...");
        }

        /// <summary>Connect over Valve's relay network by the host's SteamId
        /// (slice 2) — no port forwarding, no IP.  Same session flow as the IP
        /// path from the Hello onward; the seam hides the transport.</summary>
        public static void ConnectSteam(ulong hostSteamId)
        {
            if (_transport != null) Disconnect();
            LastDisconnectReason = "";
            SessionEnded = false;
            _voluntaryDisconnect = false;
            _connected = false;

            var t = new SteamClientTransport();
            t.Connected    += OnConnected;
            t.Disconnected += OnDisconnected;
            t.Received     += OnReceive;
            _transport = t;
            if (!t.Connect(hostSteamId))
            { Plugin.Logger.LogError($"[Client] Steam relay connect toward {hostSteamId} failed to start."); _transport = null; return; }

            Plugin.Logger.LogInfo($"[Client] Connecting via Steam relay to {hostSteamId}...");
        }

        public static void Disconnect()
        {
            // Initiator forensics (see MPServer.Stop note).
            if (_transport is { IsRunning: true })
                Plugin.Logger.LogWarning($"[Client] DISCONNECT called from: {Environment.StackTrace}");
            _voluntaryDisconnect = true;   // deliberate — never the session-over lock
            _transport?.Disconnect();
            _transport = null;
            _connected = false;
            IsInLobby = true;
            _lobbyPlayers = new List<string>();
        }

        // ── Events ────────────────────────────────────────────────────────────

        private static void OnConnected()
        {
            Plugin.Logger.LogInfo("[Client] Connected to host.");
            _connected = true;

            // RECONNECT: the host's vote tally + any in-flight skip are gone on its side — clear our stale
            // copy so a leftover SkipActive or phantom vote rows don't wedge the rest dock / world-clock
            // detector after we rejoin (skip state used to reset only on scene LOAD, never on a same-scene
            // reconnect → 2026-06-19 bug). Local seating is preserved. Harmless on the first connect
            // (state already empty). Marshalled — it mutates lists the main thread reads.
            GameStatePatcher.EnqueueOnMainThread(() => { try { MPRestSync.ClearVotesOnReconnect(); } catch { } });

            IsInLobby = true;
            _lobbyPlayers = new List<string>();

            // Send Hello
            var hello = new HelloPayload
            {
                PlayerId = MPConfig.PlayerId,
                Version  = MyPluginInfo.PLUGIN_VERSION,
                StableId = MPConfig.StableId,
                Protocol = ProtocolInfo.Version,
                Game     = MPSaveManager.GameVersionNameCached()
            };
            // Phase 3: offer any pending disconnect save (un-uploaded progress from our last leave). The
            // host decides whether to request it; it never auto-overrides a host save.
            try
            {
                var dc = MPSaveCoordinator.ReadClientDisconnectMarker();
                if (dc != null && !string.IsNullOrEmpty(dc.SessionBase))
                {
                    hello.HasDisconnectSave     = true;
                    hello.DisconnectSessionBase = dc.SessionBase;
                    hello.DisconnectDay         = dc.Day;
                    hello.DisconnectSavedAtUnix = dc.SavedAtUnix;
                }
            }
            catch { }
            Send(MessageEnvelope.Create(MessageType.Hello, MPConfig.PlayerId, hello));
        }

        private static void OnDisconnected(string reason, byte[] extra)
        {
            Plugin.Logger.LogWarning($"[Client] Disconnected from host: {reason}");
            // Host can attach a HUMAN reason (kick/reject/ban) as disconnect
            // data — "RemoteConnectionClose" told the user nothing (2026-06-11).
            string why = reason;
            try
            {
                if (extra != null && extra.Length > 0)
                {
                    // RAW bytes (server sends UTF8 directly) — the transport hands
                    // them over verbatim (LiteNetLib's length-prefixed GetString
                    // threw and ate the kick/reject reason, 2026-06-11).
                    string tag = System.Text.Encoding.UTF8.GetString(extra);
                    if (tag == "BAMP:rejected") why = "Join REJECTED by host";
                    else if (tag == "BAMP:kicked") why = "KICKED by host";
                    else if (tag == "BAMP:banned") why = "Banned until host re-hosts";
                    else if (tag == "BAMP:identity") why = "Join refused — player identity invalid or already connected";
                    else if (tag.StartsWith("BAMP:version"))
                    {
                        // Tag carries the host's "mod|game" versions so we can name both sides.
                        string rest = tag.StartsWith("BAMP:version:") ? tag.Substring("BAMP:version:".Length) : "";
                        var parts = rest.Split('|');
                        string hostMod  = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "?";
                        string hostGame = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "?";
                        why = $"Join refused — version mismatch. Host: {MyPluginInfo.SHORT_NAME} {hostMod} / {hostGame}; " +
                              $"you: {MyPluginInfo.PLUGIN_VERSION} / {MPSaveManager.GameVersionNameCached()}. " +
                              "Both players need the same mod and game version.";
                    }
                    Plugin.Logger.LogInfo($"[Client] disconnect reason tag: '{tag}' → \"{why}\"");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Client] disconnect-tag read: {ex.Message}"); }
            LastDisconnectReason = why;
            _connected = false;
            // The connection is gone either way — stop the poll loop so
            // IsConnecting goes false (the UI was stuck showing "Connecting…"
            // forever after a ConnectionFailed).  StopPolling only (we're ON the
            // poll thread); the transport itself is torn down by the next
            // Connect()'s guard, exactly as the pre-seam NetManager was.
            _transport?.StopPolling();
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
                        MPLog.Dump("client: host connection lost in-game");
                        // Phase 2: snapshot our CURRENT state into the disconnect save (+marker) at the drop
                        // moment, so a rejoin can restore the minutes the host never received. (Main thread.)
                        MPSaveCoordinator.WriteClientDisconnectSave();
                    }
                }
                catch { }
            });
        }

        // ── Join quiesce: while a mid-join load is in flight, the HOST's live
        // stream must not touch our half-loaded world — a remote player
        // spawned and the clock hard-snapped MID-LOAD, leaving GameManager
        // permanently broken (NRE every frame, 2026-06-11).  The lobby flow is
        // protected by the startup hold; this is the mid-join equivalent.
        private static volatile bool _joinQuiesce;
        public static void BeginJoinQuiesce() { _joinQuiesce = true;  Plugin.Logger.LogInfo("[Client] join quiesce ON — world stream deferred until loaded."); }
        public static void EndJoinQuiesce()
        {
            if (!_joinQuiesce) return;
            _joinQuiesce = false;
            Plugin.Logger.LogInfo("[Client] join quiesce OFF — world stream live.");
            // WS1 (round-30): the world is live — publish every interior this machine owns so other
            // players never depend on this owner ENTERING each building first.
            try { InteriorSync.PublishAllOwnedInteriors("world live"); } catch { }
        }

        private static void OnReceive(byte[] bytes)
        {
            var env = MessageEnvelope.Deserialize(bytes);
            if (env == null) return;

            // Streaming world traffic is DROPPED during a join load (snapshots
            // re-arrive via the world-ready flow; streams resume on their own).
            if (_joinQuiesce)
                switch (env.Type)
                {
                    case MessageType.PlayerMove:
                    case MessageType.GameTimeSync:
                    case MessageType.VehicleSync:
                    case MessageType.PlayerAnimTrigger:
                    case MessageType.AppearanceSync:
                    case MessageType.RestSkipState:
                        return;
                    default:
                        if (env.Type.ToString().Contains("Parked") || env.Type.ToString().Contains("Traffic")
                            || env.Type.ToString().Contains("Market") || env.Type.ToString().Contains("Interior"))
                            return;
                        break;
                }

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

                case MessageType.PassengerBoardResult:
                    HandlePassengerBoardResultMsg(env);
                    break;

                case MessageType.PassengerFollowEnter:
                    HandlePassengerFollowEnterMsg(env);
                    break;

                case MessageType.PassengerFollowExit:
                    HandlePassengerFollowExitMsg(env);
                    break;

                case MessageType.PassengerExit:
                    HandlePassengerExitMsg(env);
                    break;

                case MessageType.VehicleLockSet:
                    HandleVehicleLockMsg(env);
                    break;

                case MessageType.PassengerSnapshot:
                    HandlePassengerSnapshotMsg(env);
                    break;

                case MessageType.PermissionSnapshot:
                    HandlePermissionSnapshotMsg(env);
                    break;

                case MessageType.MergerState:
                {
                    var ms = env.GetPayload<MergerStatePayload>();
                    if (ms != null) GameStatePatcher.EnqueueOnMainThread(() => MergerSync.ApplyState(ms));
                    break;
                }

                case MessageType.MergerWalletState:
                {
                    // Slice 4: the authoritative shared balance (or a targeted leave payout).
                    var ws = env.GetPayload<MergerWalletStatePayload>();
                    if (ws != null) GameStatePatcher.EnqueueOnMainThread(() => MergerWallet.ApplyState(ws));
                    break;
                }

                case MessageType.BusinessEditRequest:
                {
                    // Host-relayed, grant-gated: this machine OWNS the shop — apply natively.
                    var be = env.GetPayload<BusinessEditPayload>();
                    if (be != null) GameStatePatcher.EnqueueOnMainThread(() => BusinessSync.ApplyRoutedTemporarilyClose(be));
                    break;
                }

                case MessageType.MergerEmployeeEdit:
                {
                    // Slice 5, host-relayed: this machine OWNS the shop — apply the fire/schedule natively.
                    var ee = env.GetPayload<EmployeeEditPayload>();
                    if (ee != null) GameStatePatcher.EnqueueOnMainThread(() => MergerEmployeeSync.ApplyOnOwner(ee));
                    break;
                }

                case MessageType.MergerRequest:
                {
                    // "proposal": someone wants to merge with ME — surface Accept/Decline in the
                    // Permissions tab. "declined": my proposal was turned down.
                    var mr = env.GetPayload<MergerRequestPayload>();
                    if (mr == null) break;
                    GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        if (mr.Action == "proposal")
                        {
                            MergerSync.IncomingFromPid = mr.FromPid ?? "";
                            PassengerHud.Toast($"{mr.FromPid} proposes a company merger — see Permissions.");
                        }
                        else if (mr.Action == "withdrawn")
                        {
                            MergerSync.IncomingFromPid = "";   // silent — a withdraw must not be a notification channel
                        }
                        else if (mr.Action == "declined")
                        {
                            MergerSync.OutgoingToPid = "";
                            PassengerHud.Toast("Merger proposal declined.");
                        }
                        else if (mr.Action == "cooldown")
                        {
                            MergerSync.OutgoingToPid = "";
                            PassengerHud.Toast("Wait a minute before proposing to them again.");
                        }
                    });
                    break;
                }

                case MessageType.PermissionOwnGrants:
                    HandlePermissionOwnGrantsMsg(env);
                    break;

                case MessageType.PermissionBuildingAccess:
                    HandlePermissionBuildingAccessMsg(env);
                    break;

                case MessageType.RentDeny:
                    HandleRentDeny(env);
                    break;

                case MessageType.VacateNotify:
                    HandleVacate(env);
                    break;

                case MessageType.BuyDeny:
                    HandleBuyDeny(env);
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

                case MessageType.VehicleDrive:
                    HandleVehicleDriveMsg(env);
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

                case MessageType.ShopStockDigest:
                {
                    var sd = env.GetPayload<ShopStockDigestPayload>();
                    if (sd != null) GameStatePatcher.EnqueueOnMainThread(() => MPStockSync.Apply(sd));
                    break;
                }

                case MessageType.AuditDrill:
                {
                    // Host localized a biz divergence to bucket(s) — log our
                    // per-registration hashes so the two logs diff offline.
                    var ad = env.GetPayload<AuditDrillPayload>();
                    if (ad != null) GameStatePatcher.EnqueueOnMainThread(() => MPAudit.LogBizDrill(ad.Buckets));
                    break;
                }

                case MessageType.MarketEvents:
                {
                    var me = env.GetPayload<MarketEventsPayload>();
                    if (me != null && !string.IsNullOrEmpty(me.Json))
                        GameStatePatcher.EnqueueOnMainThread(() => GameStatePatcher.ApplyMarketEvents(me.Json));
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
                    if (ma != null) GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        MPHub.ApplyMoneyDelta(ma.Amount, ma.Reason, !ma.Silent);
                        // Client-as-shop-owner: sale credits arrive as MoneyAdjust
                        // with a "Sale: ... (sold to X)" reason — show the worker
                        // feedback (rising +$ over the buyer + own ring-up anim).
                        if (ma.Reason != null && ma.Reason.StartsWith("Sale: "))
                        {
                            string buyer = "";
                            int k = ma.Reason.LastIndexOf("(sold to ", StringComparison.Ordinal);
                            if (k >= 0) buyer = ma.Reason.Substring(k + 9).TrimEnd(')');
                            MPHub.ShowSalePopup(buyer, ma.Amount);
                        }
                    });
                    break;
                }

                case MessageType.RegisterCashier:
                {
                    MPRegisterSync.Apply(env.GetPayload<RegisterCashierPayload>());
                    break;
                }

                case MessageType.PlayerStaffRoster:
                {
                    MPRegisterSync.ApplyRoster(env.GetPayload<PlayerStaffRosterPayload>());
                    break;
                }

                case MessageType.VehicleCargoReq:   // host forwarded an accessor's request — I'm the vehicle owner
                {
                    var vq = env.GetPayload<VehicleCargoReqPayload>();
                    if (vq != null) GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        var res = VehicleStorageSync.OwnerApply(vq);
                        MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.VehicleCargoRes, MPConfig.PlayerId, res));
                    });
                    break;
                }

                case MessageType.VehicleCargoRes:   // the owner's verdict on my take/put — I'm the accessor
                {
                    var vr = env.GetPayload<VehicleCargoResPayload>();
                    if (vr != null) GameStatePatcher.EnqueueOnMainThread(() => VehicleStorageSync.OnResult(vr));
                    break;
                }

                case MessageType.BuildingCargoReq:   // host forwarded a guest's request — I'm the building owner
                {
                    var bq = env.GetPayload<BuildingCargoReqPayload>();
                    if (bq != null) GameStatePatcher.EnqueueOnMainThread(() =>
                    {
                        var res = BuildingStorageSync.OwnerApply(bq);
                        MPClient.SendEnvelope(MessageEnvelope.Create(MessageType.BuildingCargoRes, MPConfig.PlayerId, res));
                    });
                    break;
                }

                case MessageType.BuildingCargoRes:   // the owner's verdict on my take/put — I'm the guest
                {
                    var br = env.GetPayload<BuildingCargoResPayload>();
                    if (br != null) GameStatePatcher.EnqueueOnMainThread(() => BuildingStorageSync.OnResult(br));
                    break;
                }

                case MessageType.HelperOrderForward:   // host forwarded a helper-hosted sale — I'm the building owner
                {
                    var ho = env.GetPayload<HelperOrderPayload>();
                    if (ho != null) GameStatePatcher.EnqueueOnMainThread(() => CustomerEntrySync.OwnerAdoptForwardedOrder(ho));
                    break;
                }

                case MessageType.CustomerSimAuthority:   // host's per-building simulator election
                {
                    var ca = env.GetPayload<CustomerSimAuthorityPayload>();
                    if (ca != null) GameStatePatcher.EnqueueOnMainThread(() => CustomerPuppets.ApplyAuthority(ca));
                    break;
                }

                case MessageType.CustomerPuppetState:    // the simulator's live customer bodies for my room
                {
                    var cp = env.GetPayload<CustomerPuppetStatePayload>();
                    if (cp != null) GameStatePatcher.EnqueueOnMainThread(() => CustomerPuppets.ApplyState(cp));
                    break;
                }

                case MessageType.CustomerPuppetEmote:    // a simulated customer's emoji — replay on the puppet
                {
                    var ce = env.GetPayload<CustomerPuppetEmotePayload>();
                    if (ce != null) GameStatePatcher.EnqueueOnMainThread(() => CustomerPuppets.ApplyEmote(ce));
                    break;
                }

                case MessageType.CustomerPuppetLook:     // a simulated customer's look — dress the puppet
                {
                    var cl = env.GetPayload<CustomerPuppetLookPayload>();
                    if (cl != null) GameStatePatcher.EnqueueOnMainThread(() => CustomerPuppets.ApplyLook(cl));
                    break;
                }

                case MessageType.BuildingInteriorEdit:   // host forwarded a guest's interior edit — I'm the owner: adopt it
                {
                    var bie = env.GetPayload<InteriorSnapshotPayload>();
                    if (bie != null) GameStatePatcher.EnqueueOnMainThread(() => { GameStatePatcher.ApplyInteriorSnapshot(bie); InteriorSync.PushOwnedBuildingNow(bie.AddressKey); });
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
            _lobbyPlayers = payload.Players != null ? new List<string>(payload.Players) : new List<string>();
            EnforceStartingCash = payload.EnforceStartingCash;
            LobbyAges.Clear();
            if (payload.Ages != null) foreach (var kv in payload.Ages) LobbyAges[kv.Key] = kv.Value;
            HostLoadMode    = payload.LoadMode;
            HostLoadSession = payload.LoadSessionName ?? "";
            Plugin.Logger.LogInfo($"[Client] Lobby: {string.Join(", ", LobbyPlayers)} " +
                                  $"(starting cash {(EnforceStartingCash ? "enforced by host" : "per-player")})");
        }

        /// <summary>The game is starting/loading — the lobby pane must yield
        /// (the stored-save mid-join path left IsInLobby true and the MP window
        /// sat on the lobby pane over a fully loaded world, 2026-06-11).</summary>
        public static void MarkLeftLobby() => IsInLobby = false;

        /// <summary>Set for a fresh-character join: at WorldReady the player is
        /// warped to the DESIGNATED new-player start.  The native placement
        /// reads gi.LastPlayerPosition (default 215,0,0 = the designated spot),
        /// but the game continuously overwrites that field from the live
        /// transform — during our long fenced load it gets stomped with the
        /// player prefab's parking position, so placement no-ops and fresh
        /// joiners woke up in the wrong part of town (user, 2026-06-12).</summary>
        public static volatile bool PendingFreshSpawn;

        /// <summary>Mid-join fresh start: no save for this player anywhere —
       /// new character with the host''s settings (null → Normal preset).</summary>
        public static void StartFreshFromHost(GameVariablesDto? settings)
        {
            IsInLobby = false;
            PendingFreshSpawn = true;
            SendPhaseReport("Loading");   // INTENT: don't excuse me from the fence
            BeginJoinQuiesce();
            var s = settings ?? MPServer.Preset("Normal");
            Plugin.Logger.LogInfo($"[Client] Mid-join fresh start; cash={s.StartingMoney}.");
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    // The intro may not load over a RUNNING world (GameManager
                    // NRE storm) — detour via the main menu first.
                    if (Helpers.PlayerHelper.PlayerController != null)
                    {
                        Plugin.Logger.LogInfo("[Client] Fresh start while IN-GAME — detouring via main menu.");
                        MPSaveCoordinator.DeferFreshStart(settings);
                        LoadScene.LoadMainMenu(BAModAPI.ModActivationScope.City);
                        return;
                    }
                    SaveGameManager.New(MPServer.BuildGameVariables(s));
                    LoadScene.LoadIntro(false);
                }
                catch (Exception ex) { Plugin.Logger.LogError($"[Client] StartFreshFromHost: {ex}"); }
            });
        }

        private static void HandleStartGame(MessageEnvelope env, bool isNew)
        {
            IsInLobby = false;
            SendPhaseReport("Loading");   // INTENT: don't excuse me from the fence
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

            if (!string.IsNullOrEmpty(snap.SessionId) && MPLog.SessionId != snap.SessionId)
                MPLog.BeginSession(snap.SessionId, "client");
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

        private static void HandleBuyDeny(MessageEnvelope env)
        {
            var payload = env.GetPayload<BuildingOwnershipPayload>();
            if (payload == null) return;

            Plugin.Logger.LogWarning($"[Client] Buy DENIED for {payload.AddressKey} — already owned; rolling back.");
            GameStatePatcher.RollbackBuy(payload.AddressKey);
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
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                RemotePlayerManager.Remove(payload.PlayerId);
                // A departed worker must not leave a phantom "staffed" register
                // (buyers would be charged with no owner to receive the sale).
                MPRegisterSync.RemovePlayer(payload.PlayerId);
            });
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

        private static int _lastLoggedGtsDay = int.MinValue;
        private static void HandleGameTimeSync(MessageEnvelope env)
        {
            var payload = env.GetPayload<GameTimeSyncPayload>();
            if (payload == null) return;
            // Log once per in-game DAY, not every ~3s packet (was ~2,100 lines/session). The day
            // progression is what we actually diagnose with (e.g. the save-storm day range).
            if (payload.Day != _lastLoggedGtsDay)
            {
                _lastLoggedGtsDay = payload.Day;
                Plugin.Logger.LogInfo($"[Client] GameTimeSync: day={payload.Day} hour={payload.TimeOfDay:F1}");
            }

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
            Plugin.Logger.LogInfo($"[Client] Received interior snapshot for '{payload.AddressKey}': {InteriorSync.SnapshotSummary(payload)}.");
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
                    if (p.Gender >= 0) GameStatePatcher.ClientPlayerGenders[p.PlayerId] = p.Gender;
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
            // Self-stats the NATIVE self-sheet way (round-25 parity: what others see for me = what I see
            // for myself) — one shared builder with the host's own-stats block. Replaces the RentPerDay×7
            // "income" (that's rent EXPENSE, not income) and the unfiltered row list (the HOME leaked in
            // as a business row); also publishes the primary neighborhood for the first time.
            RivalSelfStats.Build(out int bldgCount, out float weeklyIncome, out string hood, out var rows);
            var p = new RivalsStatsRequestPayload
            {
                PlayerId = MPConfig.PlayerId,
                SelfOwnedBuildingsCount  = bldgCount,
                SelfOwnedBusinessesCount = rows.Count,
                SelfWeeklyIncome         = weeklyIncome,
                SelfNeighborhood         = hood,
            };
            p.Businesses.AddRange(rows);
            try
            {
                var gi = SaveGameManager.Current;
                // The game's own per-day series → real detail-view graphs on
                // other machines (last 10 points is plenty; the UI plots 7).
                if (gi?.playerWeeklyIncomeHistory != null)
                    foreach (var t in gi.playerWeeklyIncomeHistory)
                        if (t != null) p.IncomeHistory.Add(new HistoryPointF { Day = t.Item1, Value = t.Item2 });
                if (p.IncomeHistory.Count > 10) p.IncomeHistory.RemoveRange(0, p.IncomeHistory.Count - 10);
                if (gi?.playerNumberOfBusinessesHistory != null)
                    foreach (var t in gi.playerNumberOfBusinessesHistory)
                        if (t != null) p.BizCountHistory.Add(new HistoryPointI { Day = t.Item1, Value = t.Item2 });
                if (p.BizCountHistory.Count > 10) p.BizCountHistory.RemoveRange(0, p.BizCountHistory.Count - 10);
            }
            catch { }
            Send(MessageEnvelope.Create(MessageType.RivalsStatsRequest, MPConfig.PlayerId, p));
            Plugin.Logger.LogInfo($"[Client] Sent RivalsStatsRequest with self-stats: bldgs={bldgCount} biz={rows.Count} income=${weeklyIncome:F0} hood='{hood}' ({p.Businesses.Count} business rows).");
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

        /// <summary>Client owner → host: authoritative interior for a business this player runs.</summary>
        public static void SendInteriorOwnerSnapshot(InteriorSnapshotPayload payload)
        {
            if (!IsConnected || payload == null || string.IsNullOrEmpty(payload.AddressKey)) return;
            payload.OwnerPlayerId = MPConfig.PlayerId;
            payload.ItemInstancesAuthoritative = true;
            payload.Authoritative = true;   // owner's own push — authoritative for the whole interior
            Send(MessageEnvelope.Create(MessageType.InteriorOwnerSnapshot, MPConfig.PlayerId, payload));
        }

        // ── Passenger (ride shotgun) ──────────────────────────────────────────
        /// <summary>Client → host: ask to ride vehicle V (host validates owner/lock/seat).</summary>
        public static void SendBoardRequest(string vehicleId)
        {
            if (!IsConnected || string.IsNullOrEmpty(vehicleId)) return;
            Send(MessageEnvelope.Create(MessageType.PassengerBoardRequest, MPConfig.PlayerId,
                new PassengerBoardRequestPayload { PlayerId = MPConfig.PlayerId, VehicleId = vehicleId }));
        }

        /// <summary>Client → host: I'm leaving vehicle V (always allowed, even when locked).</summary>
        public static void SendPassengerExit(string vehicleId)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.PassengerExit, MPConfig.PlayerId,
                new PassengerExitPayload { PlayerId = MPConfig.PlayerId, VehicleId = vehicleId }));
        }

        /// <summary>Owner-client → host: set my vehicle's passenger lock.</summary>
        public static void SendVehicleLock(string vehicleId, bool locked)
        {
            if (!IsConnected || string.IsNullOrEmpty(vehicleId)) return;
            Send(MessageEnvelope.Create(MessageType.VehicleLockSet, MPConfig.PlayerId,
                new VehicleLockPayload { OwnerId = MPConfig.PlayerId, VehicleId = vehicleId, Locked = locked }));
        }

        /// <summary>Owner-client → host: grant/revoke a key for an ONLINE player (by PlayerId).
        /// Optimistic local apply for a snappy UI; the host's snapshot + grantee-list confirm.</summary>
        public static void SendPermissionGrant(GrantKind kind, string granteeId, bool granted)
        {
            if (!IsConnected || string.IsNullOrEmpty(granteeId)) return;
            GrantSync.SetGrant(kind, MPConfig.PlayerId, granteeId, granted);
            Send(MessageEnvelope.Create(MessageType.PermissionGrantSet, MPConfig.PlayerId,
                new PermissionGrantPayload { OwnerId = MPConfig.PlayerId, GranteeId = granteeId, Granted = granted, Kind = kind }));
        }

        /// <summary>Member-client → host: a merger action ("propose" with TargetPid, or
        /// "accept"/"decline"/"leave"). The host arbitrates and rebroadcasts MergerState.</summary>
        public static void SendMergerAction(string action, string targetPid = "")
        {
            if (!IsConnected || string.IsNullOrEmpty(action)) return;
            Send(MessageEnvelope.Create(MessageType.MergerRequest, MPConfig.PlayerId,
                new MergerRequestPayload { Action = action, TargetPid = targetPid ?? "" }));
        }

        /// <summary>Member-client → host: a routed owner-only business edit (merger slice 3).</summary>
        public static void SendBusinessEdit(BusinessEditPayload p)
        {
            if (!IsConnected || p == null) return;
            Send(MessageEnvelope.Create(MessageType.BusinessEditRequest, MPConfig.PlayerId, p));
        }

        /// <summary>Owner-client → host: revoke an OFFLINE grantee by their StableId handle (taken from
        /// the owner's grantee list). The host updates the durable store + rebroadcasts.</summary>
        /// <summary>Owner-client → host: grant/revoke a kind for an OFFLINE grantee by their StableId handle
        /// (taken from the owner's grantee list). The host updates the durable store + rebroadcasts.</summary>
        public static void SendPermissionSetOffline(GrantKind kind, string granteeStable, bool granted)
        {
            if (!IsConnected || string.IsNullOrEmpty(granteeStable)) return;
            Send(MessageEnvelope.Create(MessageType.PermissionGrantSet, MPConfig.PlayerId,
                new PermissionGrantPayload { OwnerId = MPConfig.PlayerId, GranteeStable = granteeStable, Granted = granted, Kind = kind }));
        }

        /// <summary>Client(driver) → host: I drove vehicle V into a building. The host resolves V's riders
        /// and tells each to follow (a client can't reach the other riders directly).</summary>
        public static void SendPassengerFollowRelayEnter(string vehicleId, string addressKey)
        {
            if (!IsConnected || string.IsNullOrEmpty(vehicleId)) return;
            Send(MessageEnvelope.Create(MessageType.PassengerFollowRelayEnter, MPConfig.PlayerId,
                new PassengerFollowPayload { VehicleId = vehicleId, AddressKey = addressKey }));
        }

        /// <summary>Client(driver) → host: I drove vehicle V back out of a building.</summary>
        public static void SendPassengerFollowRelayExit(string vehicleId, int exitId)
        {
            if (!IsConnected || string.IsNullOrEmpty(vehicleId)) return;
            Send(MessageEnvelope.Create(MessageType.PassengerFollowRelayExit, MPConfig.PlayerId,
                new PassengerFollowPayload { VehicleId = vehicleId, ExitId = exitId }));
        }

        /// <summary>Client → host: upload our pending disconnect save (Phase 3); the host validates its
        /// actual in-game day, then sends back the real load.</summary>
        public static void SendClientDisconnectUpload(SaveDataPayload p)
        {
            if (!IsConnected || p == null) return;
            Send(MessageEnvelope.Create(MessageType.ClientDisconnectUpload, MPConfig.PlayerId, p));
        }

        // [PassFollow] SLICE 1a — confirm the rider receives the follow-signal and can resolve the
        // building (the actual EnterBuilding lands in 1b).  Only the targeted rider acts.
        private static void HandlePassengerFollowEnterMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerFollowPayload>();
            if (p == null || p.TargetPlayerId != MPConfig.PlayerId) return;   // not for me
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                Plugin.Logger.LogInfo($"[PassFollow] FollowEnter for '{p.AddressKey}' (riding '{p.VehicleId}') → following driver inside.");
                PassengerRide.FollowDriverIntoByAddress(p.AddressKey);
            });
        }

        private static void HandlePassengerFollowExitMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerFollowPayload>();
            if (p == null || p.TargetPlayerId != MPConfig.PlayerId) return;   // not for me
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                try
                {
                    Plugin.Logger.LogInfo($"[PassFollow] FollowExit (riding '{p.VehicleId}', exit {p.ExitId}) → following driver out.");
                    PassengerRide.FollowDriverOut(p.ExitId);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"[PassFollow] FollowExit handle: {ex.Message}"); }
            });
        }

        private static void HandlePassengerBoardResultMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerBoardResultPayload>();
            if (p == null) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                if (p.Seat >= 0) PassengerSync.ApplyBoard(p.VehicleId, p.PlayerId, p.Seat);
                else if (p.PlayerId == MPConfig.PlayerId) PassengerHud.ToastReason(p.Reason);   // "Vehicle full." etc.
            });
        }

        private static void HandlePassengerExitMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerExitPayload>();
            if (p == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => PassengerSync.ApplyExit(p.PlayerId));
        }

        private static void HandleVehicleLockMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<VehicleLockPayload>();
            if (p == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => PassengerSync.SetLock(p.VehicleId, p.Locked));
        }

        private static void HandlePermissionOwnGrantsMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<PermissionOwnGrantsPayload>();
            if (p == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => GrantSync.SetMyGrantees(p.Grantees));
        }

        private static void HandlePermissionSnapshotMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<PermissionSnapshotPayload>();
            if (p == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => { GrantSync.ApplySnapshot(p); VehicleManager.OnGrantsChanged(); });
        }

        private static void HandlePermissionBuildingAccessMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<PermissionBuildingAccessPayload>();
            if (p == null) return;
            GameStatePatcher.EnqueueOnMainThread(() =>
            {
                GrantSync.SetEnterableBuildings(p.AddressKeys);
                GrantSync.SetHelperBusinesses(p.HelperAddressKeys);
                GrantSync.SetOtherOwned(p.OtherOwnedKeys);   // merger slice 3 repair source
                HousingMapCues.RefreshSharedPois();
            });
        }

        private static void HandlePassengerSnapshotMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<PassengerSnapshotPayload>();
            if (p == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => PassengerSync.ApplySnapshot(p));
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
        /// <summary>Reports a lifecycle transition to the host (load-fence
        /// visibility — lets the host excuse a menu-bailed client).</summary>
        public static void SendPhaseReport(string phase)
        {
            if (!IsConnected) return;
            Send(MessageEnvelope.Create(MessageType.PhaseReport, MPConfig.PlayerId,
                new PhaseReportPayload { PlayerId = MPConfig.PlayerId, Phase = phase }));
        }

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
            int gender = -1; try { var gi2 = SaveGameManager.Current; if (gi2?.charactersData != null && gi2.charactersData.Count > 0) gender = (int)gi2.charactersData[0].gender; } catch { }
            var p = new PlayerProfilePayload { PlayerId = MPConfig.PlayerId, CharacterName = name, PortraitPngBase64 = portrait, AgeInYears = age, Gender = gender };
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

        public static void SendStockDigest(ShopStockDigestPayload p)
        {
            if (!IsConnected || p == null) return;
            Send(MessageEnvelope.Create(MessageType.ShopStockDigest, MPConfig.PlayerId, p));
        }

        /// <summary>Periodic state-hash audit → host (silent-divergence detector).</summary>
        public static void SendAuditReport(AuditReportPayload p)
        {
            if (!IsConnected || p == null) return;
            Send(MessageEnvelope.Create(MessageType.AuditReport, MPConfig.PlayerId, p));
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

        /// <summary>Driver(borrower) → host → all: the pose of a borrowed car I'm driving (handoff).</summary>
        public static void SendVehicleDrive(VehicleDrivePayload p)
        {
            if (!IsConnected || p == null) return;
            Send(MessageEnvelope.Create(MessageType.VehicleDrive, MPConfig.PlayerId, p));
        }

        private static void HandleVehicleDriveMsg(MessageEnvelope env)
        {
            var p = env.GetPayload<VehicleDrivePayload>();
            if (p == null) return;
            GameStatePatcher.EnqueueOnMainThread(() => VehicleManager.ApplyDriveSync(p));
        }

        /// <summary>
        /// Sends the local player's position to the host so it can be relayed
        /// to all other clients.  Called from MPCanvasUI at ~10 Hz.
        /// </summary>
        public static void SendPlayerPosition(PlayerPositionPayload payload)
        {
            if (!IsConnected) return;
            var env = MessageEnvelope.Create(MessageType.PlayerMove, MPConfig.PlayerId, payload);
            _transport?.Send(env.Serialize(), reliable: false);
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

        /// <summary>Called from the unrent patch when the local client terminates a
        /// building's lease.  The native unrent already ran locally; this asks the
        /// host to release ownership so its authoritative state stops re-asserting
        /// the rental back onto us (and so the other players see the building free).</summary>
        public static bool RequestVacateBuilding(string addressKey)
        {
            if (!IsConnected) return false;

            var payload = new BuildingOwnershipPayload
            {
                AddressKey    = addressKey,
                OwnerPlayerId = MPConfig.PlayerId,
            };
            Send(MessageEnvelope.Create(MessageType.VacateRequest, MPConfig.PlayerId, payload));
            Plugin.Logger.LogInfo($"[Client] Sent VacateRequest for {addressKey}");
            return true;
        }

        /// <summary>Client bought a for-sale building locally; ask the host (the owner
        /// registrar) to ratify it.  If the host denies (someone else already owns it),
        /// HandleBuyDeny rolls back our optimistic purchase.</summary>
        public static bool RequestBuyBuilding(string addressKey)
        {
            if (!IsConnected) return false;

            var payload = new BuildingOwnershipPayload
            {
                AddressKey    = addressKey,
                OwnerPlayerId = MPConfig.PlayerId,
            };
            Send(MessageEnvelope.Create(MessageType.BuyRequest, MPConfig.PlayerId, payload));
            Plugin.Logger.LogInfo($"[Client] Sent BuyRequest for {addressKey}");
            return true;
        }

        /// <summary>Client listed an owned building for sale; ask the host to add it to
        /// the authoritative for-sale market (carrying the seller's chosen price/sqm) so
        /// the listing survives the host's market broadcast and every player sees it.</summary>
        public static bool RequestListForSale(BuildingForSaleInfo info)
        {
            if (!IsConnected || info == null) return false;
            Send(MessageEnvelope.Create(MessageType.ListForSale, MPConfig.PlayerId, info));
            Plugin.Logger.LogInfo($"[Client] Sent ListForSale for {info.AddressKey}");
            return true;
        }

        /// <summary>Client canceled its building's sale; ask the host to remove it from
        /// the authoritative market.</summary>
        public static bool RequestCancelSale(string addressKey)
        {
            if (!IsConnected) return false;
            var payload = new BuildingOwnershipPayload { AddressKey = addressKey, OwnerPlayerId = MPConfig.PlayerId };
            Send(MessageEnvelope.Create(MessageType.CancelSale, MPConfig.PlayerId, payload));
            Plugin.Logger.LogInfo($"[Client] Sent CancelSale for {addressKey}");
            return true;
        }

        /// <summary>The AI bought one of this client's listed buildings (completed in our
        /// own sim — we were paid natively).  Tell the host to drop it from the
        /// authoritative for-sale market + ownership registry.</summary>
        public static bool SendSaleCompleted(string addressKey)
        {
            if (!IsConnected) return false;
            var payload = new BuildingOwnershipPayload { AddressKey = addressKey, OwnerPlayerId = MPConfig.PlayerId };
            Send(MessageEnvelope.Create(MessageType.SaleCompleted, MPConfig.PlayerId, payload));
            Plugin.Logger.LogInfo($"[Client] Sent SaleCompleted for {addressKey}");
            return true;
        }

        private static void Send(MessageEnvelope env)
        {
            _transport?.Send(env.Serialize(), reliable: true);
        }

        /// <summary>Reliable send for modules that build their own envelope.</summary>
        public static void SendEnvelope(MessageEnvelope env)
        {
            if (IsConnected) Send(env);
        }

        // Poll loop lives in LnlClientTransport now (transport seam) — same
        // thread name, cadence, and throw-isolation as before.
    }
}
