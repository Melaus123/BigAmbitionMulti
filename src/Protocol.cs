
namespace BigAmbitionsMP
{
    // ── Message type tags ──────────────────────────────────────────────────────

    public enum MessageType : byte
    {
        // Handshake
        Hello           = 1,   // Client → Host: "I'm connecting"
        Welcome         = 2,   // Host → Client: full world snapshot (late join)

        // Lobby (pre-game)
        LobbyUpdate     = 3,   // Host → All: current player list in lobby
        StartGameNew    = 4,   // Host → All: everyone start a new game
        StartGameLoad   = 5,   // Host → All: everyone load the multiplayer save
        PlayerInGame    = 6,   // Client → Host: "my game scene has finished loading"
        StartupRelease  = 7,   // Host → All: all players loaded — release the startup pause
        StartupStatus   = 8,   // Host → All: which players are still loading (waiting screen)
        ManualPause     = 9,   // Any → Host → All: the deliberate (pause-button) pause toggled
        LobbyPref       = 14,  // Client → Host: this player's lobby choices (currently: starting age).
        WorldReady      = 15,  // Client → Host: "I've applied the world sync" — frozen-until-synced startup release gate.

        // Building ownership
        RentRequest     = 10,  // Client → Host: "I want to rent this building"
        RentConfirm     = 11,  // Host → All: "This building is now rented by X"
        RentDeny        = 12,  // Host → Client: "Rent denied (already taken)"
        VacateNotify    = 13,  // Host → All: "This building is now available"
        VacateRequest   = 16,  // Client → Host: "I terminated this building's lease — release it"
        BuyRequest      = 17,  // Client → Host: "I bought this for-sale building" — host arbitrates ownership
        BuyDeny         = 18,  // Host → Client: "Buy denied (already owned)" — roll back the optimistic local purchase
        ListForSale     = 19,  // Client → Host: "I listed my building for sale" — add to the authoritative market
        CancelSale      = 21,  // Client → Host: "I canceled my building's sale" — remove from market, keep ownership
        SaleCompleted   = 22,  // Client(owner) → Host: "the AI bought my listed building" — remove from market + clear ownership

        // Market
        MarketSnapshot  = 20,  // Host → All: current product market entries

        // Player position
        PlayerMove      = 30,  // Any → All: position/rotation update
        PlayerLeft      = 31,  // Host → All: a player disconnected mid-game

        // Player animation
        PlayerAnimTrigger = 34, // Any → All: an animator trigger fired (one-off action)

        // Vehicles
        VehicleSync     = 35,  // Any → All: a player's vehicle state (drive/transform/identity)
        TrafficSnapshot = 36,  // Host → All: full AI-traffic snapshot
        TaxiHail        = 37,  // Client → Host: "I'm hailing traffic taxi N — stop it"
        TrafficLights   = 38,  // Host → All: traffic-light intersection states
        ParkedSnapshot  = 39,  // Host → All: world parked-vehicle snapshot (lots + street parking)

        // Player appearance
        PlayerAppearance = 32, // Client → Host: this player's character appearance
        AppearanceSync   = 33, // Host → All: every player's appearance

        // Time
        GameTimeSync    = 40,  // Host → All: periodic game day/time sync

        // Businesses (exterior business sync — Phase 1)
        BusinessSnapshot = 50, // Host → All: full table of business state (sent on connect).
        BusinessChange   = 51, // Host → All: single building business state changed.

        // Interiors (Phase 2: building interior sync on entry + while inside)
        InteriorRequest       = 60, // Client → Host: "I entered building X, subscribe me + send snapshot."
        InteriorSnapshot      = 61, // Host → Client: full interior state of one building.
        PlayerExitedBuilding  = 62, // Client → Host: "I exited building X, unsubscribe me."
        InteriorOwnerSnapshot = 63, // Client owner → Host: authoritative interior for a business that player runs.

        // Rivals (Phase 1d Wave 2: synthetic-rival sync so buildingOwnerRivalId
        // lookups resolve to a real name instead of "undefined" on the client).
        RivalsSnapshot       = 70, // Host → Client: full rival roster (id + name pairs).

        // Rivals stats (Phase 1d Wave 4: on-demand refresh when client opens
        // the rivals app on their phone).
        RivalsStatsRequest   = 71, // Client → Host: "the user just opened the rivals window, send me fresh stats."
        RivalsStatsSnapshot  = 72, // Host → Client: per-rival stat block (income, building counts).

        // Player profile (Phase 1d Wave 5: character name as canonical display).
        PlayerProfile        = 80, // Either direction: a player's in-character name (CharacterData.name).

        // Save persistence (Phase 4: coordinated MP save — centralized on host).
        SaveNow              = 90, // Host → All: "save your game into MP session N right now."
        SaveData             = 91, // Client → Host: "here's my saved .hsg (gzipped) + slot" — host is the keeper.
        RequestSave          = 92, // Client → Host: "the user hit Save in the pause menu — please run a coordinated save."
        CashSync             = 93, // Client → Host: periodic current money, so the host always has a near-current cash figure to restore on reconnect (loss-minimization).
        LoadData             = 94, // Host → Client: "here is YOUR stored .hsg (gzipped) for this session + the cash to restore" — load it.

        // In-game chat (Phase 6: connected-players window + chat).
        Chat                 = 100, // Any → Host → All: a chat line.  Clients send to host; host relays to everyone (incl. sender) so the log is consistent.
        RetailPrices         = 101, // Any → Host → Others: live retail prices of a business the SENDER runs — keeps per-neighbourhood price competition fed with current numbers on every machine.
        NativeClaim          = 157, // Any → Host: my save natively claims this building (+ development score) — contested-tenancy arbitration input
        ReleaseClaim         = 158, // Host → loser: release your native claim on this building (re-verified locally before executing)

        // Player-to-player business sale (round-196; rides the hub offer system — LoanOffer Kind="business")
        BizTransferFinalize  = 170, // Host → buyer: the accepted sale is paid + ledgered — claim the business locally (native takeover + staff promotion); re-sent until acked
        BizTransferRelease   = 171, // Host → seller: the sale completed — release tenancy locally + drop your staff records (they transferred)
        BizTransferAck       = 172, // Buyer → Host: local claim done — stop re-sending Finalize
        TakeoverRequest      = 173, // Client → Host (round-204b): offer on an AI-run business — host arbitrates against LIVE valuation × accept rate; NOTHING happens client-side until the result
        TakeoverResult       = 174, // Host → Client: verdict — accepted (ledger + tenant reflect already done host-side; charge + native claim now) or denied (carries the host's minimum price)
        RadioState           = 175, // Any → Host → All (round-227): a building's speaker radio state (station + signed volume; sign = on/off) — light and precise so a radio click can never clobber concurrent interior edits
        ModMismatch          = 176, // Host → joiner (round-253, user-directed 2026-08-13): your installed-mod list differs from the host's. INFORMATIONAL ONLY — never a gate; the joiner shows a lobby notice + logs the diff so players stop being blind to install deltas (divergent prices/content read as mod bugs otherwise).
        PeerLogRequest       = 177, // Reporter → connected peers (bug-report v2, user-directed 2026-08-15): a bug bundle is being filed on my machine — contribute your logs. Third-party reports ("my friend crashed") used to carry only the reporter's half of the evidence.
        PeerLogReply         = 178, // Peer → reporter: ONE log file, redacted (IPs + Windows usernames) ON THE OWNER'S MACHINE before it crosses the wire, then gzipped. TotalFiles replies per peer; TotalFiles=0 = nothing readable. The reporter's upload waits ≤12s then ships with whatever arrived.
        GuestCargoGrab       = 179, // Taker → Host → Owner (round-269, field 20260816-101747 sell-loop exploit): a Business-granted guest grabbed an item (or drained shelf stock) in another player's business — convey it so the owner's copy loses it too. Ungranted grabs are BLOCKED by the pickup gate; this closes the dup for the granted flow. Routed to the online owner (their apply + next owner-push propagates), applied host-side when the owner is offline or is the host.
        JoinProgress         = 180, // Client → Host → All (round-270, field 20260816-112127 "cannot join" = silent relay downloads cancelled): the joiner's world-download percent, throttled. Display-only. The overlay derives "loading world…" from report STALENESS (fresh percent = downloading; gone quiet but not world-ready = native load running) — no phase field to desync.
        RegisterServe        = 156, // Simulator → Host → All: a customer's serve STARTED / FINISHED at a till.  Lets the player working that till on a FOLLOWER machine perform the job — they are assigned to the station locally and see the queue, but with no real customers there the native serve loop never runs, so without this they stand motionless behind a busy counter.
        BuildingDirtEdit     = 155, // Helper → Host → Owner: floor cells a HELPER mopped in someone else's business.  Narrow on purpose (only the cells whose dirtiness changed): dirt is owner-authoritative interior state, and the only pre-existing guest→owner interior channel is the whole-snapshot forward on interior-designer close, which mopping never triggers — so without this a helper's cleaning stayed local and was overwritten by the owner's next push.
        RestVote             = 102, // Client → Host: this player started/ended a rest-class activity (consensus time-skip voting).
        RestSkipState        = 103, // Host → All: current votes + whether the consensus skip is running (banner + skip-detector stand-down).
        MoneyTransfer        = 104, // RETIRED (2026-06-12): direct transfers were replaced by accept-required gift offers (LoanOffer Kind="gift"); the host no longer handles this type.  Number stays reserved.
        LoanOffer            = 105, // Any → Host → target: a player offers another a loan (principal, daily interest, daily payment).
        LoanAnswer           = 106, // Target → Host: accept/decline a loan offer.
        LoanState            = 107, // Host → All: the authoritative active-loan ledger (Business Hub display).
        MoneyAdjust          = 108, // Host → one player: credit/debit your wallet by Amount (transfer delivery, loan principal, daily loan payments).
        LoanRepay            = 109, // Borrower → Host: repay a loan early (full or partial; Amount<=0 = full payoff).
        PhaseReport          = 110, // Client → Host: my lifecycle phase changed (load-fence visibility; lets the host excuse a client who bailed to the menu instead of loading).
        RegisterCashier      = 111, // Any → Host → All: player went on/off duty at the cash register near (X,Y,Z); others can F4-buy there (Wave-2 player-staffed registers).
        RemoteSale           = 112, // Buyer → Host: I bought items in another player's shop (buyer already paid locally); host validates and credits the owner.
        AuditReport          = 113, // Client → Host: periodic state-hash audit (clock, business table, roster, interior replicas) — host compares against its own state and logs [Audit] MISMATCH on divergence.
        AuditDrill           = 114, // Host → Client: a biz bucket diverged persistently — send back your per-registration hashes for these buckets so the host can name the diverging address(es) in its own log (round-291: no local dump — a bundle carries one log).
        MarketEvents         = 115, // Host → All: the authoritative gi.marketEvents list (shortages/hype/backorders drive shelf fills + prices; clients suppress the generating sim and would otherwise never see one).

        // Passengers (ride shotgun in another player's car — host-authoritative).
        PassengerBoardRequest = 120, // Client → Host: request to ride vehicle V.
        PassengerBoardResult  = 121, // Host → All: V's passenger = player P at seat S (S<0 = rejected; Reason set for the requester's popup).
        PassengerExit         = 122, // Any → Host → All: player P left vehicle V (rider exit OR host kick).
        VehicleLockSet        = 123, // Owner/key-holder → Host → All: vehicle V passenger-lock = Locked (2026-08-26: host authorizes the SENDER — owner or currently-granted; broadcast OwnerId = the real owner, host-normalized; same fields, no wire change).
        PassengerSnapshot     = 124, // Host → joiner: full passenger lock + seat state (join replay).

        // Shared vehicle storage (take/put from another player's UNLOCKED vehicle — host-authoritative request/grant).
        // 125/126 RETIRED (storage unification Stage B, 2026-08-25): VehicleCargoReq/Res — the
        // vehicle half of the twin storage channels; both containers ride StorageOp/StorageRes
        // (195/196) since v16. Do not reuse the numbers.

        // Passenger follows the driver through a building entrance (host-authoritative).
        PassengerFollowEnter  = 127, // Host → rider P: vehicle V drove into a building (AddressKey); rider should enter it too.
        PassengerFollowExit   = 128, // Host → rider P: vehicle V left the building; rider should follow back out.
        // Client-as-driver relay: a CLIENT driver can't reach the riders directly, so it tells the host,
        // which resolves V's riders and fans out the Follow{Enter,Exit} above (or acts locally if the host
        // itself is the rider). Driver pid = the message sender.
        PassengerFollowRelayEnter = 129, // Client(driver) → Host: I drove V into a building (AddressKey).
        PassengerFollowRelayExit  = 130, // Client(driver) → Host: I drove V out (ExitId).
        // Phase 3 save-recovery: on rejoin the host may ask the client to upload its DISCONNECT save (the
        // only client file allowed to override the host's record), which the host then validates by the
        // save's ACTUAL in-game day before accepting. Reuses SaveDataPayload.
        ClientDisconnectUpload = 131,    // Client → Host: here is my pending disconnect save for the session.

        // Stock digest (2026-06-24): a shop's owner broadcasts which of its PRICED shelves are actually
        // STOCKED, so every machine (esp. the host's economy floor) can judge an un-entered shop's real
        // stock without loading its interior. Last-known-while-inside; the set persists otherwise.
        ShopStockDigest = 132,           // Owner → Host → All: addressKey + the set of stocked goods item names.

        // Player-to-player permission GRANTS (Phase 1: vehicle "keys" — a granted player bypasses
        // the owner's vehicle lock for ride + cargo).  See docs/PERMISSIONS-SYSTEM.md.
        PermissionGrantSet  = 133,       // Owner → Host: grant/revoke a key for grantee G (by pid if online, else by StableId handle).
        PermissionSnapshot  = 134,       // Host → All: the full runtime (online) access-grant table (join replay + after every change).
        PermissionOwnGrants = 135,       // Host → one owner: your grantee list incl. OFFLINE ones (handle + name + online), for the UI.
        VehicleDrive        = 136,       // Driver(borrower) → Host → All: live pose + fuel of owner O's car V while the borrower drives it (Released on exit).
        PermissionBuildingAccess = 137,  // Host → one client: the building addressKeys it may ENTER as a granted housing guest (clients lack a building→owner map).
        // 138/139 RETIRED (storage unification Stage B, 2026-08-25): BuildingCargoReq/Res — the
        // building half of the twin storage channels; both containers ride StorageOp/StorageRes
        // (195/196) since v16. Do not reuse the numbers.
        // 140 RETIRED (interior-edit Stage 3, 2026-08-25): BuildingInteriorEdit — the guest
        // WHOLE-REPLICA edit snapshot. Every edit now travels as BuildingInteriorDelta (194);
        // a guest whole-set assertion is the exact class the 2026-08 design removed. Do not
        // reuse the number.
        PlayerStaffRoster   = 141,       // Owner → Host → All: the staff roster of one player business (round-30 WS3 — visitors inject these records so the game's own staffing engine can spawn EVERY scheduled worker, not just a synthetic cashier).
        HelperOrderForward  = 142,       // Helper → Host → building owner: an NPC customer order paid on the helper's machine (round-39f Phase 3 slice-2 step-2). Owner claims the entry, deducts stock, records the order.
        CustomerSimAuthority = 143,      // Host → All: which player SIMULATES customers in a given player building (slice 3: register-worker first, else earliest-arrived inside; "" = building empty).
        CustomerPuppetState = 144,       // Simulator → Host → the players INSIDE that building (v11/T8 — was All + sender echo; the mod's largest steady stream went to players who could not see it): live customer bodies (~4 Hz); non-simulator players inside render kinematic puppets from it.
        CustomerPuppetEmote = 145,       // Simulator → Host → inside-players (v11/T8): a customer showed an emoji expression (complaints etc.) — followers replay it on the matching puppet (round-42 parity).
        CustomerPuppetLook  = 146,       // Simulator → Host → inside-players (v11/T8), once per occupancy episode per customer — followers dress the puppet to match (round-44). The host caches the latest look per (building, customer), FIFO-capped, and REPLAYS them when a player walks in (presence edge), because followers deliberately cache looks ahead of entering (round-44c).
        MergerState         = 147,       // Host → All: merged-company membership (merger slice 1) — online member PlayerIds (what enforcement reads) + full display roster.
        MergerRequest       = 148,       // Member ↔ Host: propose/accept/decline/leave a company merger; host relays "proposal"/"declined" to the affected member.
        BusinessEditRequest = 149,       // Member → Host → business owner: an owner-only business edit made on a merger-flipped replica (slice 3: the open/close toggle) — the OWNER applies it natively and their sync republishes the truth.
        MergerWalletDelta   = 150,       // Member → Host: a native money change on a merged member's machine (delta + transaction key) — the host ledger is the shared wallet's single source of truth (slice 4).
        MergerWalletState   = 151,       // Host → All (group-tagged) or targeted (GroupId="" = personal payout on leave): the authoritative shared balance; members set their local mirror to it.
        MergerEmployeeEdit  = 152,       // Member → Host → business owner (slice 5): a routed employee/schedule op on a merger-flipped shop ("fire" an injected partner employee; "schedule" = wholesale hours+shifts write-back). Owner applies natively; roster/business heartbeats republish the truth.
        StoreMirror         = 153,       // Host → all-but-owner (handoff slice 1): one piece of the session store — a member's saved .hsg and/or the manifest — so every member holds the complete session store and can host the world later.
        AuditDrillReply     = 154,       // Client → Host (round-89): per-registration hashes+summaries for the diverged audit buckets — the host diffs against its own and NAMES the diverging address(es) in one log (field reports only ever carry one machine's log, so offline two-log diffing never happened).
        InteriorCargoSync   = 181,       // Host → a building's subscribers (round-281, field bundles 20260818-22*): the CURRENT cargo of that building's items and NOTHING else.  86% of interior traffic was cargo-only churn (shelf stock ticking down as customers buy) shipped as a FULL ~80-150KB snapshot whose 306 designs and 225 dirt spots were byte-identical every single time; round-280 slowed those sends down, this shrinks them.  ABSOLUTE STATE, never a diff-chain: it names EVERY item in the building, so re-applying it converges by itself.  Guarded by StructVersion — a receiver whose last applied FULL snapshot carried a different version knows its structure is stale, ignores the cargo and re-requests (InteriorRequest).  Sent ONLY when EVERY subscriber runs our exact build (Hello.CargoDelta + mod-version equality); one non-capable subscriber and all of them get the full snapshot, so an old client can never be handed this type.
        BillboardAds        = 182,       // Any → Host → All (round-290, field 20260820-092547): the sender's ACTIVE billboard campaigns as an ABSOLUTE set (billboard type + business name per entry). Campaign facts live only in the owner's save, so partners' paid ads never entered anyone else's AdManager pools; receivers inject these as PLAYER-weight ads. Images need nothing new — the per-size billboard logos already sync.
        // ── Shared-shop management (the Business PERMISSION feature) — NOT the merger; the merger keeps MergerEmployeeEdit ──
        SharedScheduleEdit  = 183,       // Permitted player → Host → shop OWNER (shared-shop slice 1, 2026-08-21, plan §2.5/§2.6): the CHANGED days of a shared shop's schedule, each with the owner-truth signature it was edited against. Host: direct Business grant required (merger membership does not count), per-sender rate cap, shape check. Owner applies per day (base matches → apply, else owner wins), validates references, echoes ONE targeted snapshot.
        ScheduleSession     = 184,       // Shared-shop slice 1 (plan §2.8): the editing session. open/close (+60 s keepalive): editor → Host → owner, grant-gated. snapshot: owner → Host → ONE editor (ToPid) — never a broadcast; everyone else converges on the business heartbeat. Carries all days (empty = "unchanged") + any rejected days after a routed edit.
        SharedStaffPool     = 185,       // Owner → Host → the players holding a DIRECT Business grant from that owner (shared-shop slice 3, 2026-08-22, plan §2.3): the owner's hired-but-UNASSIGNED employees (their bench), sent when membership/wages change; the host caches it and replays it to a newly granted player. Receivers inject real-id records with no business so the helper can place them in the owner's shared shop.
        SharedStaffEdit     = 186,       // Permitted player → Host → shop OWNER: "assign" an owner's employee to one of the owner's shared shops, or "unassign" them from it. Host: direct grant on the address, rate cap. Owner performs the native reassignment and republishes roster + bench.
        SharedPriceEdit     = 187,       // Permitted player → Host → shop OWNER (shared-shop slice 4, 2026-08-22, plan §2.4): ONE item's retail price at a shared shop, coalesced to 1 s of quiet on the editor (the native editor fires per keystroke AND per +/- click). Host: direct Business grant on the address, rate cap. The owner writes BOTH native lists (retailPrices + storedRetailPrices) and MPPriceSync's existing broadcast carries it to everyone — no echo of its own.
        SharedSalesHistory  = 188,       // Shared-shop slice 4 (ruling 25): the shop's recent per-item sales, WITHOUT which a helper prices blind (orderHistory is local-only — the replica's is empty). "request": editor → Host → owner when the Inventory & Pricing tab opens. "snapshot": owner → Host → exactly ONE editor (ToPid), never broadcast. Only the last 14 days and only the three fields the tab sums (item, units, revenue) — no hour reports, no wholesale, no per-price breakdown.
        SharedWorkInfo      = 189,       // Shared-shop slice 6 (rulings 26/28/29/31): a shared WAREHOUSE or FACTORY tab's figures, computed on the OWNER's machine — item data for a building only reaches other machines while someone is INSIDE it (InteriorSync), so a replica's is stale or empty. "request": helper → Host → owner when the tab opens (Tab says which). "snapshot": owner → Host → exactly ONE helper (ToPid), never broadcast. 6a carries the Inventory tab (boxes + product rows); 6b/6c add Drivers (per-slot vehicle + driver) and Factory (per-workstation config, active state, resource stock). Also the owner's ECHO after an applied SharedWorkEdit; Tab "card" carries the BizMan list card's summaries (slots + top-4 inventory rows) for one shared warehouse/factory. A helper with the tab open also re-requests every few seconds (ruling 32 live parity) — the owner replies only when the content changed (Sig match = silence).
        SharedWorkEdit      = 190,       // Shared-shop slice 6b/6c: helper → Host → building OWNER, one edit on a shared warehouse/factory — "driver" (slot assignment), "recipe", "produce" (up-to toggle + amount), "order" (workstation priority), "alias" (rename, ruled allowed 2026-08-24). Host: direct Business grant on the address, rate cap. The owner validates against ITS data (the native checks), applies natively, and echoes that tab's SharedWorkInfo snapshot to the editor — a rejected edit reverts on the helper by that same echo.
        BuildingsForSale    = 191,       // Host → All (v9, 2026-08 throughput T6): JUST the buy-marketplace list (~15 entries, a few KB). The daily RealEstateHelper refresh used to trigger a FULL ~826-building BusinessSnapshot broadcast (~1 MB/client) to move this one list; now only the list travels. Join snapshots still carry it inside BusinessSnapshot — this type is the steady-state refresh only. Rides Bulk (review M5: shares state with BusinessSnapshot).
        MirrorAck           = 192,       // Client → Host (v9 review B2): "I hold this exact mirrored .hsg" — sent after the file is WRITTEN to the client's store (or already present via the shared-store token). The host's absent-member mirror skip records delivery ONLY on this ack — never at send time, because the paced lane's documented loss recovery is "the next save re-mirrors", which a send-time record would silently delete (character loss on host handoff).
        InteriorDirtSync    = 193,       // v10 (T7, ruling 33): ONE building's dirt VALUES as an ABSOLUTE dirty-set — every lattice spot with dirtiness > 0 as (X, Z, value); every spot NOT listed reads clean. Dirt is cleaning-only data: owner → Host on change (keeps the host cache/save current, ~KBs), Host → ONLY the players physically inside (the subscriber set) — nobody else, nothing when the building is empty. The receiver UPDATES matching lattice entries and zeroes the rest; it never adds or removes entries (the lattice is one fixed entry per floor tile — replacing the list would corrupt the cleanliness average). Rides Bulk (M5: InteriorSnapshot writes the same state).
        BuildingInteriorDelta = 194,     // v13 (interior-edit Stage 1b, design 2026-08): a PERMITTED EDIT as the ops it actually made — upsert/remove naming exactly the items touched; silence about an id means "no opinion", never "delete" (THE INVARIANT). Editor → Host (grant-gated: owner, or a Housing/Business grant from the owner); Host → owner to adopt, PLUS the same delta to that building's subscribers minus the sender, PLUS an id-keyed graft onto the host's owner cache (Q1) — one delta conveys everyone, retiring the owner's post-adopt full re-push. Absolute per item, idempotent, orderless (no sequence numbers). Receivers apply ops through the SAME ApplyOneItem the full snapshot runs (Stage 1a) and NEVER discard the message (it is the edit's only carrier); the per-item drag/hands rules are the mid-edit protection. A >25-remove delta without the BulkEdit marker is refused by the receiver (a placement/removal forward is 1-3 ops; more means a corrupt diff), and the BUILDER never emits one: over-cap removes are suppressed at diff time and the address owes a re-sync instead (Stage 1b review MAJOR-O — there is no whole-replica fallback; 140 is retired).
        StorageOp             = 195,     // Accessor → Host → container owner (v16, storage unification): ONE storage operation — take/put/markpaid/setstock with a container discriminator (building addr+itemId | vehicle id) and the retired channels' ctx literals verbatim. Host resolves the owner itself (ruling 38) and grant-gates building ops; the owner's machine is the sole authority and re-verifies.
        StorageRes            = 196,     // Owner → Host → accessor: the verdict (Ok, Reason, owner-truth echoes per ctx policy, nested contents for sealed-box ops).
        TrunkDetailReq        = 197,     // Accessor → Host → vehicle owner (v17, F-2026-08-25-I proposal 2): "send me the FULL cargo detail of this trunk" — fired when the borrower OPENS the storage panel (and again when the fleet manifest moves while it is open). Event-driven, no steady-state cost; the 4-field broadcast manifest stays the fallback row source.
        TrunkDetailRes        = 198,     // Owner → Host → accessor: every cargo instance with real paid/price AND nested contents (THE codec) — the borrower's panel renders the native card shapes the owner sees (sealed contents label, bundle tooltip) and every stack past the manifest's 24-instance cap. Display-only on the receiver: rows feed the panel, never game state (Gameplay lane is correct — no Bulk-written state is touched).
        RivalStaffReq         = 199,     // Client → Host (AI-staff slice, user-approved 2026-08-29, on-demand per ruling-25 pattern): "send me this AI business's staff" — fired when the poach window opens on a client (field 20260830-042803: those lists were empty on every joiner since 0.11 — aiEmployees is generated at world-gen, which clients suppress, and no snapshot ever carried it).
        RivalStaffRes         = 200,     // Host → the ONE requester (and the claimant inside PoachResult): the business's aiEmployees as small rows (id, skill, value, hours-demand, negotiation flags). A row's PERSON is rebuilt deterministically from the id on the receiver (native GetEmployeeInstance seeds from id), so identical hireable people appear on every machine. v1 deliberately EXCLUDES poachedEmployees rows (they reference full EmployeeInstances clients don't hold). Receiver OVERWRITES its local list — clients can locally invent random staff via EstimatedWeeklyIncomeHelper's fallback generator, and those inventions must not survive.
        PoachClaim            = 201,     // Client → Host: "my rival salary negotiation for employee X at AI business Y was ACCEPTED" — the hire must be claimed host-side so two players cannot poach the same person. The client's native AcceptOffer is deferred until the verdict.
        PoachResult           = 202,     // Host → the claimant: Ok (the host removed the employee and generated the replacement natively) or not (already gone), plus the business's rows AFTER the claim — the claimant's locally-generated replacement is overwritten by host truth.
        ServiceCarStop        = 203,     // Rider → Host → service-car OWNER (2026-09-02): "stop your private driver / handed-off car so I can catch up and ride it" (host relays; the owner's machine owns that car's Gley AI). Auto-resumes after 60 s on the owner's side.
        ServiceCarResume      = 204,     // Rider → Host → OWNER: the rider boarded (native DriveAway on our GhostTaxi) or cancelled the map — resume the car's previous driving state.
        ShopValuation         = 205,     // H-BIZ-1 (2026-09-03, user option A): "request": viewer → Host → the shop's OWNER when the BizMan page of another PLAYER's shop opens; "answer": owner → Host → exactly ONE viewer (ToPid) carrying the game's own closure figure for that shop (interior items' selling prices + vehicles at the address + deposit — BizManPresentation.OnTerminateContractConfirm's formula). No grant gate: the native page shows an estimate to anyone. Small, on demand, never broadcast.
    }

    /// <summary>Merger slice 3 — a routed owner-only business edit (currently the temporarily-closed
    /// toggle). Grant-gated at the host like every routed op.</summary>
    public class BusinessEditPayload
    {
        public string AddressKey        { get; set; } = "";
        public bool   TemporarilyClosed { get; set; }
    }

    /// <summary>Merger slice 5 — a routed employee/schedule operation on a partner's business.
    /// Action="fire": remove EmployeeId (an injected partner-staff record on the sender's machine) —
    /// the owner runs the native RemoveEmployee. Action="schedule": replace the shop's whole
    /// scheduleDays (opening hours + work shifts) with Schedule — last-writer-wins co-op semantics.
    /// Action="adopt": the sender hired/assigned THEIR OWN employee into the owner's shop — the full
    /// record (the DTO fields below; never Newtonsoft a game type, §10) migrates into the owner's
    /// save, where the staffing engine and payroll for that shop actually live.</summary>
    public class EmployeeEditPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (validated SenderIs at the host)
        public string Action     { get; set; } = "";
        public string AddressKey { get; set; } = "";
        public string EmployeeId { get; set; } = "";
        public List<ScheduleDayInfo> Schedule { get; set; } = new();
        // "adopt" record fields
        public string Name         { get; set; } = "";
        public int    Gender       { get; set; } = -1;
        public int    AgeDays      { get; set; }
        public float  Wage         { get; set; }
        public float  Satisfaction { get; set; } = 100f;
        public List<string> Skills  { get; set; } = new();   // "name=value" (characterData.skills)
        public List<string> Demands { get; set; } = new();
    }

    // ── Shared-shop management (Business PERMISSION feature; src/SharedShopSchedule.cs) — separate from the merger ──

    /// <summary>Shared-shop slice 1: a permitted player's schedule edit for a shop they do not own — only the CHANGED
    /// days. BaseSigs[i] is the owner-truth per-day signature Days[i] was edited against (owner: equal → apply,
    /// different → the owner wins and the day comes back rejected). Seq orders one sender's edits per address;
    /// SeqEpoch is random per editor PROCESS so a restarted editor's counter is never mistaken for duplicates.
    /// Senders strip their own duty stand-ins before sending.</summary>
    public class SharedScheduleEditPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (validated SenderIs at the host)
        public string AddressKey { get; set; } = "";
        public int    Seq        { get; set; }
        public int    SeqEpoch   { get; set; }
        public List<ScheduleDayInfo> Days     { get; set; } = new();
        public List<string>          BaseSigs { get; set; } = new();
    }

    /// <summary>Shared-shop slice 1 (plan §2.8): the editing session between a permitted player (the editor) and
    /// the shop owner. "open"/"close" (and a 60 s "open" keepalive) go editor → host → owner; "snapshot" goes
    /// owner → host → exactly ONE editor (ToPid) — never broadcast. A snapshot with an empty Schedule means
    /// "your copy is current" (the editor's held Sig matched). RejectedDays lists the days the owner did not take
    /// from a routed edit (same-day collision, owner mid-drag, or a reference that no longer exists); Reason is for
    /// the log only — this feature puts nothing on screen.</summary>
    public class ScheduleSessionPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (validated SenderIs at the host)
        public string Action     { get; set; } = "";   // "open" | "close" | "snapshot"
        public string AddressKey { get; set; } = "";
        public string ToPid      { get; set; } = "";   // snapshot target — the editor
        public string Sig        { get; set; } = "";   // open: the editor's held whole-schedule sig; snapshot: the owner's
        public List<ScheduleDayInfo> Schedule { get; set; } = new();   // snapshot: every day (empty = unchanged)
        public List<int> RejectedDays { get; set; } = new();
        public string Reason     { get; set; } = "";
    }

    /// <summary>Shared-shop slice 3: an owner's hired-but-unassigned employees (their bench), as the roster's StaffInfo
    /// rows. ABSOLUTE set — receivers drop bench records no longer listed (unless the roster meanwhile assigned them).</summary>
    public class SharedStaffPoolPayload
    {
        public string PlayerId { get; set; } = "";   // the owner (validated SenderIs at the host)
        public List<StaffInfo> Staff { get; set; } = new();
    }

    /// <summary>Shared-shop slice 3: a permitted player's assignment change for one of the owner's employees.
    /// "assign" → AddressKey = the owner's shared shop to place them in; "unassign" → AddressKey = the shop they
    /// currently work at. Seq/SeqEpoch as for schedule edits (a delayed duplicate can never undo a newer edit).</summary>
    public class SharedStaffEditPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (validated SenderIs at the host)
        public string Action     { get; set; } = "";   // "assign" | "unassign"
        public string EmployeeId { get; set; } = "";
        public string AddressKey { get; set; } = "";
        public string FromAddressKey { get; set; } = "";   // where the helper believed the employee was ("" = bench); the owner rejects if that is no longer true (owner wins, as for schedule days)
        public int    Seq        { get; set; }
        public int    SeqEpoch   { get; set; }
    }

    /// <summary>Shared-shop slice 4: ONE item's retail price at a shared shop, set by a permitted player. The native
    /// editor writes per keystroke and per +/- click and raises no event, so the SEND is coalesced to 1 s of quiet
    /// (ruled 2026-08-21) — the local write stays immediate, only the message waits. Seq/SeqEpoch as elsewhere: a
    /// delayed duplicate can never undo a newer edit. Absolute value, never a delta.</summary>
    public class SharedPriceEditPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (validated SenderIs at the host)
        public string AddressKey { get; set; } = "";
        public string ItemName   { get; set; } = "";
        public float  Price      { get; set; }
        public int    Seq        { get; set; }
        public int    SeqEpoch   { get; set; }
    }

    /// <summary>Shared-shop slice 4 (ruling 25): the recent per-item sales of a shared shop, so a helper can price
    /// against real numbers instead of the empty orderHistory a replica carries. "request" → the owner answers with
    /// "snapshot" to that ONE editor. Fourteen days is exactly what the tab consumes (two 7-day windows), and three
    /// fields are exactly what it sums — everything else in an OrderHistoryEntry stays home.</summary>
    public class SharedSalesHistoryPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (validated SenderIs at the host)
        public string Action     { get; set; } = "";   // "request" | "snapshot"
        public string AddressKey { get; set; } = "";
        public string ToPid      { get; set; } = "";   // snapshot target — the editor that asked
        public int    OwnerDay   { get; set; }         // the owner's day number when taken: the receiver rebases so its own day arithmetic lands on the same window
        public List<SalesDayInfo> Days { get; set; } = new();
        public List<string> Products { get; set; } = new();   // what the shop sells (cachedAvailableProducts): derived from a shop's own shelves, so a replica has none and the tab would list nothing to price
        public List<StockInfo> Stock { get; set; } = new();    // units on hand per item: counted from the shop's INTERIOR, which a helper may never have loaded

    }

    public class SalesDayInfo
    {
        public int DayNumber { get; set; }
        public List<SalesItemInfo> Items { get; set; } = new();
    }

    public class StockInfo
    {
        public string ItemName { get; set; } = "";
        public int    Count    { get; set; }
    }

    public class SalesItemInfo
    {
        public string ItemName   { get; set; } = "";
        public int    AmountSold { get; set; }
        public float  TotalPrice { get; set; }
    }

    /// <summary>Shared-shop slice 6: a shared warehouse/factory tab's figures, owner-computed (see
    /// MessageType.SharedWorkInfo). One request per tab open; one targeted reply; never a broadcast.</summary>
    public class SharedWorkInfoPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (validated SenderIs at the host)
        public string Action     { get; set; } = "";   // "request" | "snapshot"
        public string Tab        { get; set; } = "";   // "inventory" (6a); "drivers" / "factory" later
        public string AddressKey { get; set; } = "";
        public string ToPid      { get; set; } = "";   // snapshot target — the helper that asked
        public string Sig        { get; set; } = "";   // request: the sig of the helper's held snapshot (owner stays silent on match); snapshot: the content sig
        public bool   Echo       { get; set; }         // snapshot after a routed edit: ALWAYS applied by the helper — a REJECTED edit leaves the content (and so the sig) unchanged, and the sig gate would otherwise swallow the revert (review blocker, 2026-08-24)
        public int    BoxesMax     { get; set; }       // pallet-shelf capacity, as the owner's tab computes it
        public int    BoxesCurrent { get; set; }       // boxes on those shelves right now
        public List<WorkProductInfo>  Products { get; set; } = new();
        public List<DriverSlotInfo>   Slots    { get; set; } = new();   // 6b: Drivers tab
        public List<WorkstationInfo>  Stations { get; set; } = new();   // 6c: Factory tab
        public List<StockInfo>        ResourceStock { get; set; } = new();   // 6c: ingredient units in pallets, owner-counted
        public SharedInsightInfo?     Insight  { get; set; }            // v18 7a: promotion + satisfaction scalars
        public List<InsightDayInfo>   InsightDays { get; set; } = new();   // v18 7a: customers per day, hours for yesterday
        public List<CapacityRowInfo>  Capacity { get; set; } = new();      // v18 7a: owner-computed customer-capacity rows
        public int                    OwnerDay { get; set; } = -1;         // v18 7a: the owner's Day when the snapshot was built — day rebasing (the pricing carry's shift, review MAJOR-1)
        public List<ContractInfo>     Contracts { get; set; } = new();     // v18 7b: the owner's wholesale delivery contracts for this shop
        public List<StockInfo>        Stock     { get; set; } = new();     // v18 7b-2: units on hand per deliveries-tab row, counted on the OWNER's machine (the native row builder counts the shop's INTERIOR, which a helper does not hold)
        public List<StockInfo>        SoldLastWeek { get; set; } = new();  // v18 7b-2: units sold in the last 7 days per deliveries-tab row — native sums the shop's own orderHistory, which a replica does not have
        public List<CampaignInfo>     Campaigns { get; set; } = new();     // v18 7c: the shop's marketing campaigns; both Marketing-tab readouts DERIVE from this list, so carrying it is enough
        public SettingsInfo?          Settings  { get; set; }             // v18 7d: the Settings tab's name + logo settings
    }

    /// <summary>H-BIZ-1: on-demand estimate of another PLAYER's shop, computed by its owner with the game's own closure
    /// formula. "request" (viewer → host → owner) / "answer" (owner → host → the one viewer that asked, ToPid).</summary>
    public class ShopValuationPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (SenderIs-validated at the host)
        public string Action     { get; set; } = "";   // "request" | "answer"
        public string AddressKey { get; set; } = "";
        public string ToPid      { get; set; } = "";   // answer target — the viewer that asked
        public float  Value      { get; set; }         // items' selling prices + vehicles at the address + deposit
        public int    Items      { get; set; }         // breakdown, log only
        public int    Vehicles   { get; set; }
    }

    /// <summary>v18 7b — one wholesale delivery contract, carried whole. Identity on the wire is
    /// (business address, WholesaleKey, Ordinal-among-same-pair) — the native entity has no id.</summary>
    public class ContractInfo
    {
        public string WholesaleKey { get; set; } = "";
        public int    Ordinal      { get; set; }
        public bool   Enabled      { get; set; }
        public bool   Urgent       { get; set; }
        public int    NextDeliveryDay { get; set; }   // OWNER-basis; rebased with OwnerDay on apply
        public bool   Repeating    { get; set; }
        public float  DeliveryFee  { get; set; }
        public List<ContractItemInfo> Items { get; set; } = new();
    }

    public class ContractItemInfo
    {
        public string ItemName        { get; set; } = "";
        public int    Amount          { get; set; }
        public int    OrderedThisWeek { get; set; }   // feeds the native weekly-limit label + input cap
        public int    OrderedLastWeek { get; set; }
    }

    /// <summary>v18 7d — the Settings tab's carried state. The logo is FIVE SCALARS, not an image: the game
    /// writes these onto the registration and then GENERATES the logo files from them, and the existing
    /// business sync already carries those generated files owner→everyone. Colours are the game's own packed
    /// values; the font is its enum, carried as an int.</summary>
    public class SettingsInfo
    {
        public string BusinessName { get; set; } = "";
        public string LogoShape    { get; set; } = "";
        public int    LogoFont     { get; set; }
        public int    LogoColor    { get; set; }
        public int    FontColor    { get; set; }
        public int    BackColor    { get; set; }
        // v18 7d uniforms (rulings 35/42): the shop's per-skill assignments, the OWNER's presets so the
        // helper can pick one, and whether the shop has a uniform locker — the native gate for that reads
        // the building's own items, which a helper does not hold off-site.
        public List<UniformInfo> Uniforms { get; set; } = new();
        public List<PresetInfo>  Presets  { get; set; } = new();
        public bool   HasUniformLocker { get; set; }
    }

    public class UniformInfo
    {
        public string Skill    { get; set; } = "";
        public string PresetId { get; set; } = "";
    }

    /// <summary>One uniform preset. Presets are PLAYER-level data, so a helper assigning one of their own
    /// sends the whole thing and the owner keeps a copy under a fresh id (ruling 35).</summary>
    public class PresetInfo
    {
        public string Id             { get; set; } = "";
        public string Name           { get; set; } = "";
        public bool   SkillDependent { get; set; }
        public string Skill          { get; set; } = "";
        public List<ElemInfo> Male   { get; set; } = new();
        public List<ElemInfo> Female { get; set; } = new();
    }

    /// <summary>One clothing slot of a preset: the game's element type plus the two ids it stores.</summary>
    public class ElemInfo
    {
        public int    Type      { get; set; }
        public string VariantId { get; set; } = "";
        public string ColorId   { get; set; } = "";
    }

    /// <summary>v18 7c — one marketing campaign, exactly the native entity's three fields. The tab's daily-expense
    /// total and its efficiency bar are both DERIVED from this list plus static building data (GetDailyMarketingExpenses
    /// / GetMarketingEfficiency), so nothing else has to be carried for that screen.</summary>
    public class CampaignInfo
    {
        public string AgencyKey { get; set; } = "";    // the agency building's address key
        public string TypeName  { get; set; } = "";    // MarketingTypeName, by NAME
        public bool   Enabled   { get; set; }
    }

    /// <summary>v18 7a — the Insight tab's scalar figures. All native fields are ints (Promotion /
    /// Satisfaction, global namespace); carried as-is and written onto the replica before render.</summary>
    public class SharedInsightInfo
    {
        public int PromoTotal    { get; set; }
        public int PromoTraffic  { get; set; }
        public int PromoMarketing{ get; set; }
        public int SatOverall    { get; set; }
        public int SatService    { get; set; }
        public int SatPricing    { get; set; }
        public int SatInterior   { get; set; }   // native field name: facility
        public int SatClean      { get; set; }
    }

    /// <summary>v18 7a — one chart day. Hours (24 per-hour customer counts) carried for YESTERDAY only —
    /// the native "1 day" filter reads only that day's hourReports.</summary>
    public class InsightDayInfo
    {
        public int Day       { get; set; }
        public int Customers { get; set; }
        public List<int>? Hours { get; set; }
    }

    /// <summary>v18 7a — one customer-capacity row (per item type), shelves carried raw; the native
    /// CustomersLimit/TotalCustomersPerHour recompute themselves from these on the helper.</summary>
    public class CapacityRowInfo
    {
        public string ItemName { get; set; } = "";
        public List<CapShelfInfo> Shelves { get; set; } = new();
    }

    public class CapShelfInfo
    {
        public string Name    { get; set; } = "";
        public int    Amount  { get; set; }
        public int    PerHour { get; set; }
    }

    /// <summary>6b — one warehouse/factory vehicle slot. The helper's machine does NOT hold the owner's
    /// VehicleInstances (ghosts are deregistered from the save list), so name and skill travel here.</summary>
    public class DriverSlotInfo
    {
        public int    Index         { get; set; }        // position in Warehouse.vehicleSlots
        public string VehicleId     { get; set; } = "";  // "" = no vehicle assigned to the slot
        public string VehicleType   { get; set; } = "";  // raw type name (localized on the helper)
        public float  RequiredSkill { get; set; }        // VehicleType.requiredDeliveryDriverSkillValue
        public string DriverId      { get; set; } = "";  // employee id, "" = unassigned
    }

    /// <summary>6c — one factory workstation's config + computed state. Config fields already ride the
    /// interior snapshot, but that only flows while someone is INSIDE the building — this keeps the tab
    /// truthful from across town. Active/Reasons are owner-computed (they read pallet space, ingredients
    /// and the hour, all only fresh on the owner's machine).</summary>
    public class WorkstationInfo
    {
        public string Id           { get; set; } = "";
        public string ItemName     { get; set; } = "";   // the assembly-machine item (ctor argument)
        public string WorkstationType { get; set; } = "";
        public string Alias        { get; set; } = "";
        public string RecipeId     { get; set; } = "";
        public int    Priority     { get; set; }
        public bool   ProduceUpTo  { get; set; }
        public int    UpToValue    { get; set; }
        public bool   Active       { get; set; }
        public List<string> Reasons { get; set; } = new();   // localization keys, rendered by the native tooltip
        public List<string> Stacked { get; set; } = new();   // production-machine child item names (validity check)
    }

    /// <summary>6b/6c — one routed edit on a shared warehouse/factory (see MessageType.SharedWorkEdit).</summary>
    public class SharedWorkEditPayload
    {
        public string PlayerId   { get; set; } = "";   // sender (validated SenderIs at the host)
        public string AddressKey { get; set; } = "";
        public string Op         { get; set; } = "";   // "driver" | "recipe" | "produce" | "order" | "alias" | v18 7b: "contract" (full editable state) | "endcontract"
        public int    SlotIndex  { get; set; } = -1;   // driver: which vehicle slot
        public string StationId  { get; set; } = "";   // recipe/produce/alias: which workstation
        public string StrValue   { get; set; } = "";   // driver: employee id ("" = unassign); recipe: recipe id; alias: name; order: "type|id,id,id"
        public int    IntValue   { get; set; }         // produce: up-to amount
        public bool   BoolValue  { get; set; }         // produce: up-to on/off
        public ContractInfo? Contract { get; set; }    // v18 7b: "contract"/"endcontract" — identity + (for "contract") the desired state; days OWNER-basis via OwnerDay
        public int    OwnerDay   { get; set; } = -1;   // v18 7b: the SENDER's Day for rebasing Contract.NextDeliveryDay
        public string AgencyKey  { get; set; } = "";   // v18 7c: marketing ops — the agency building's address key
        public string TypeName   { get; set; } = "";   // v18 7c: marketing ops — MarketingTypeName as its enum NAME (a reorder in a game update must not silently re-point a campaign); BoolValue carries "enabled" 
        public SettingsInfo? Settings { get; set; }    // v18 7d: "logo" — the five values LogoCustomizer would have written; "rename" uses StrValue
        public string SkillName  { get; set; } = "";   // v18 7d uniforms: which skill's uniform
        public string PresetId   { get; set; } = "";   // v18 7d uniforms: the OWNER's preset id ("" clears)
        public PresetInfo? Preset { get; set; }        // v18 7d uniforms: "uniformimport" — the helper's own preset, copied into the owner's save
    }

    /// <summary>One row of the warehouse/factory Inventory table, owner-computed: the deliveries ledger
    /// (Warehouse.deliveryTransactions) never syncs, and stock counts read item data that is only fresh
    /// on the owner's machine. Balance and days-until-empty are DERIVED by the native row from these.</summary>
    public class WorkProductInfo
    {
        public string ItemName    { get; set; } = "";
        public int    Stock       { get; set; }        // units in pallets (CountResourcesInPallets)
        public int    Deliveries  { get; set; }        // delivered, last 7 days
        public int    Consumption { get; set; }        // consumed, last 7 days
        public int    DaysLeft    { get; set; } = -1;  // Tab "card" only: the list card's days-until-empty (-1 = no drain)
    }

    /// <summary>Merger slice 4 — one native money delta from a merged member's machine. NEVER an
    /// absolute (absolutes lose updates); the host ledger sums deltas. Contribution=true is the
    /// one-time merge-time pooling of the member's whole personal wallet (host dedupes by StableId).</summary>
    public class MergerWalletDeltaPayload
    {
        public string PlayerId     { get; set; } = "";   // sender (validated SenderIs at the host)
        public float  Amount       { get; set; }
        public string Key          { get; set; } = "";   // TransactionInfo.Type — attribution/probes
        public bool   Contribution { get; set; }
    }

    /// <summary>Merger slice 4 — the authoritative shared balance. GroupId names the merged company
    /// (receivers apply only their own group's); GroupId="" is a TARGETED personal set (the leave/
    /// dissolve payout — "your personal wallet is now X").</summary>
    public class MergerWalletStatePayload
    {
        public string GroupId { get; set; } = "";
        public float  Balance { get; set; }
    }

    /// <summary>One merged company. MemberPids is ONLINE members in PlayerId space (all enforcement is
    /// PlayerId — clients never learn StableIds); MemberNames is the FULL roster (offline included).</summary>
    public class MergerGroupInfo
    {
        public string       GroupId     { get; set; } = "";
        public List<string> MemberPids  { get; set; } = new List<string>();
        public List<string> MemberNames { get; set; } = new List<string>();
        public int          MemberCount { get; set; }
        /// <summary>AddressKeys of every building OPERATED by a group member (host-resolved from its
        /// ownership map) — slice 3: each member's ownership-flip target set (minus their own).</summary>
        public List<string> BuildingKeys { get; set; } = new List<string>();
    }

    /// <summary>Merger slice 1 — ALL merged companies in the session (a session can hold several
    /// disjoint mergers: P1+P2 and P3+P4), host-broadcast on every change and join (Class 4).</summary>
    public class MergerStatePayload
    {
        public List<MergerGroupInfo> Groups { get; set; } = new List<MergerGroupInfo>();
    }

    /// <summary>Merger slice 1 — form/dissolve traffic. Client → host: Action = "propose" (TargetPid set)
    /// / "unpropose" / "accept" / "decline" / "leave". Host → member: Action = "proposal" (FromPid =
    /// proposer) / "withdrawn" (proposer cancelled) / "declined" / "cooldown" (re-propose throttled).</summary>
    public class MergerRequestPayload
    {
        public string Action    { get; set; } = "";
        public string TargetPid { get; set; } = "";
        public string FromPid   { get; set; } = "";
    }

    /// <summary>Round-44 — a simulated customer's appearance, shipped once per customer so both players
    /// see the same person. STRUCTURED fields (round-44b): raw CharacterData JSON silently failed —
    /// its itemInHands is a full ItemInstance whose cached-Item graph reaches Unity objects, and the
    /// serializer choked inside a silent catch, so the look never left the simulator.</summary>
    public class CustomerPuppetLookPayload
    {
        public string AddressKey   { get; set; } = "";
        public string SimulatorPid { get; set; } = "";
        public string CustomerId   { get; set; } = "";   // entry id
        public int    Gender       { get; set; }
        public float  Strength     { get; set; }
        public float  Fatness      { get; set; }
        public int    ColorPacked  { get; set; }
        public int    EyesPacked   { get; set; }
        public List<LookElementInfo> Elements { get; set; } = new();
        public List<LookBlendInfo>   Blends   { get; set; } = new();
    }

    public class LookElementInfo
    {
        public int    Type      { get; set; }   // AppearanceElementType
        public string VariantId { get; set; } = "";
        public string ColorId   { get; set; } = "";
    }

    public class LookBlendInfo
    {
        public string Name  { get; set; } = "";
        public float  Value { get; set; }
    }

    /// <summary>Round-42: a simulated customer's emoji expression (complaint bubbles etc.), replayed on
    /// the matching puppet so followers see the same feedback the simulator does.</summary>
    public class CustomerPuppetEmotePayload
    {
        public string AddressKey   { get; set; } = "";
        public string SimulatorPid { get; set; } = "";
        public string CustomerId   { get; set; } = "";   // entry id (round-43, matches PuppetRowInfo.Id)
        public int    Emoji        { get; set; }   // CharacterEmojiName as int
        public float  Seconds      { get; set; } = 3f;
    }

    /// <summary>Round-119: a serve beat at one till, so the player working that till on a FOLLOWER machine can
    /// perform the job in step with the real one.  Only the true START and FINISH are mirrored — the beats in
    /// between are synthesised locally, which keeps the wire quiet and means latency can shift the performance
    /// slightly but can never leave it running after the real serve ended.</summary>
    public class RegisterServePayload
    {
        public string AddressKey   { get; set; } = "";
        public string SimulatorPid { get; set; } = "";
        /// <summary>Rounded world position of the till, the same key MPRegisterSync uses for duty — verified
        /// to match across machines (interiors are host-snapshot replicas at identical coordinates).</summary>
        public string StationKey   { get; set; } = "";
        public string CustomerId   { get; set; } = "";   // matches PuppetRowInfo.Id
        public bool   Finished     { get; set; }         // false = serve started, true = serve completed
        public bool   Male         { get; set; }         // customer gender — picks the same interaction
        // Round-146: the mirror streams the stand-in's REAL serve events instead of synthesizing a
        // generic performance, so the duty player's actions match whatever THAT shop's serve actually
        // does (self-service = one ring-up at the counter; full-service = a walk to each ordered item's
        // real position, then back, then the ring-up).
        public int    Kind         { get; set; }         // 0 = serve start, 1 = fetch one item, 2 = serve end
        public bool   SelfService  { get; set; }         // start beat: this till serves without fetching
        public bool   Cancelled    { get; set; }         // end beat: serve aborted — reset, no ring-up
        public float  Dur          { get; set; }         // start beat, self-service: native ring-up anim SPEED (RunAnimation's float is a speed multiplier, not seconds)
        public float  FX           { get; set; }         // fetch beat: the grabbed item's world position
        public float  FY           { get; set; }
        public float  FZ           { get; set; }
        // Round-151: cadence.  The native rhythm is grab-last-item → walk home → THEN the customer pays;
        // waiting for the finish beat (sent when the serve is fully done) left the acting one
        // return-walk behind reality, so the customer rang up and left first.  The start beat carries
        // how many grabs to expect; the receiver returns home BY ITSELF after that many fetch acts.
        public int    Entries      { get; set; }         // start beat, full-service: order entry count
    }

    /// <summary>Slice 3 (round-41): the host's per-building customer-simulator election result.</summary>
    public class CustomerSimAuthorityPayload
    {
        public string AddressKey   { get; set; } = "";
        public string SimulatorPid { get; set; } = "";   // "" = nobody inside → every machine native/normal
    }

    public class PuppetRowInfo
    {
        // Round-43: the customer's SCHEDULE ENTRY id — stable across machines, so a body survives an
        // authority handoff (the new simulator's real customer for entry E replaces puppet E in place).
        // Customers with no entry mapping fall back to "i<instanceId>" (machine-local, churns on
        // transfer — the old v1 behavior, now the rare path).
        public string Id { get; set; } = "";
        public float X   { get; set; }
        public float Y   { get; set; }
        public float Z   { get; set; }
        public float Yaw { get; set; }
        public string Held { get; set; } = "";   // round-42: hand prop name (basket/box) — "" = empty hands
        public int Fill { get; set; }            // round-45: active direct children of the held prop (basket fill visuals)
    }

    /// <summary>Slice 3 (round-41): the simulating machine's live customer bodies for one building.
    /// Followers lerp puppets to these rows; a row disappearing = that customer left (puppet walks out).</summary>
    public class CustomerPuppetStatePayload
    {
        public string AddressKey   { get; set; } = "";
        public string SimulatorPid { get; set; } = "";
        public List<PuppetRowInfo> Rows { get; set; } = new();
    }

    /// <summary>Owner → host → all: which of a shop's PRICED shelves are actually stocked (goods item
    /// names with amount &gt; 0). Lets every machine judge an un-entered shop's real stock for the market
    /// floor without loading its interior — closes the priced-but-empty-shelf exploit on un-entered shops.</summary>
    public class ShopStockDigestPayload
    {
        public string       AddressKey   { get; set; } = "";
        public string       OwnerId      { get; set; } = "";
        public List<string> StockedItems { get; set; } = new();
    }

    // ── Passenger payloads (ride shotgun) ───────────────────────────────────────

    /// <summary>Client → host: request to ride another player's vehicle.</summary>
    public class PassengerBoardRequestPayload
    {
        public string PlayerId  { get; set; } = "";
        public string VehicleId { get; set; } = "";
    }

    /// <summary>Host → all: authoritative passenger seating (Seat &lt; 0 = rejected).</summary>
    public class PassengerBoardResultPayload
    {
        public string PlayerId  { get; set; } = "";
        public string VehicleId { get; set; } = "";
        public int    Seat      { get; set; } = -1;
        public string Reason    { get; set; } = "";
    }

    /// <summary>Any → host → all: a rider left a vehicle (exit or kick).</summary>
    public class PassengerExitPayload
    {
        public string PlayerId  { get; set; } = "";
        public string VehicleId { get; set; } = "";
    }

    /// <summary>Owner/key-holder → host → all: set a vehicle's passenger lock. Inbound, OwnerId is
    /// the spoof-checked SENDER; the host authorizes that sender (owner or currently-granted) and
    /// rebroadcasts with OwnerId normalized to the vehicle's real owner.</summary>
    public class VehicleLockPayload
    {
        public string OwnerId   { get; set; } = "";
        public string VehicleId { get; set; } = "";
        public bool   Locked    { get; set; }
    }

    /// <summary>Host → a specific rider: the vehicle they're riding entered/left a building, so the
    /// rider should follow the driver in/out (AddressKey is the building for the enter case).</summary>
    public class PassengerFollowPayload
    {
        public string TargetPlayerId { get; set; } = "";
        public string AddressKey     { get; set; } = "";
        public string VehicleId      { get; set; } = "";
        public int    ExitId         { get; set; }       // [PassFollow] exit-door id (FollowExit only; ignored on enter)
    }

    /// <summary>Host → joiner: the full passenger lock + seat state (join replay), so a
    /// connecting player sees existing locks and who's already riding which vehicle.</summary>
    public class PassengerSnapshotPayload
    {
        public System.Collections.Generic.List<PassengerLockEntry> Locks { get; set; } = new();
        public System.Collections.Generic.List<PassengerSeatEntry> Seats { get; set; } = new();
    }

    public class PassengerLockEntry
    {
        public string VehicleId { get; set; } = "";
        public bool   Locked    { get; set; }
    }

    public class PassengerSeatEntry
    {
        public string VehicleId { get; set; } = "";
        public int    Seat      { get; set; }
        public string PlayerId  { get; set; } = "";
    }

    // ── Player-to-player permission grants ("keys") ──────────────────────────────

    /// <summary>Owner → host: grant (or revoke) a player a key to the owner's vehicles. The grantee
    /// is named EITHER by GranteeId (a live PlayerId, for granting someone in the lobby) OR by
    /// GranteeStable (a StableId handle from the owner's grantee list, for revoking an OFFLINE
    /// grantee). Phase 1 of the permissions system (docs/PERMISSIONS-SYSTEM.md).</summary>
    public class PermissionGrantPayload
    {
        public string    OwnerId       { get; set; } = "";   // the owner (= the sender)
        public string    GranteeId     { get; set; } = "";   // live PlayerId of the grantee (online grant), or ""
        public string    GranteeStable { get; set; } = "";   // StableId handle of the grantee (offline revoke), or ""
        public bool      Granted       { get; set; }
        public GrantKind Kind          { get; set; } = GrantKind.Vehicle;   // which asset kind this grant covers
    }

    /// <summary>Host → All: the full runtime (online) access-grant table (join replay + after every
    /// change), so every machine has the current grants for the borrower-side access checks.</summary>
    public class PermissionSnapshotPayload
    {
        public System.Collections.Generic.List<PermissionGrantEntry> Grants { get; set; } = new();
    }

    public class PermissionGrantEntry
    {
        public string    OwnerId   { get; set; } = "";
        public string    GranteeId { get; set; } = "";
        public GrantKind Kind      { get; set; } = GrantKind.Vehicle;
    }

    /// <summary>Host → one owner: the owner's full grantee list, INCLUDING offline grantees, so the
    /// Permissions UI can show + revoke them. Handle is the grantee's StableId — sent ONLY to the
    /// granting owner, never broadcast (clients don't learn each other's StableIds).</summary>
    public class PermissionOwnGrantsPayload
    {
        public System.Collections.Generic.List<OwnGrantEntry> Grantees { get; set; } = new();
    }

    public class OwnGrantEntry
    {
        public string Handle { get; set; } = "";   // grantee StableId (used to revoke an offline grantee)
        public string Name   { get; set; } = "";   // last-known display name
        public bool   Online { get; set; }         // currently connected?
        // Which kinds this grantee currently holds from the owner (Vehicle and/or Housing) — the UI shows a
        // per-kind toggle per row, lit from this set.
        public System.Collections.Generic.List<GrantKind> Kinds { get; set; } = new System.Collections.Generic.List<GrantKind>();
    }

    /// <summary>Host → one client: the building addressKeys it may ENTER as a granted housing guest. The host
    /// computes this (clients don't keep a building→owner map); it replaces the client's set wholesale.</summary>
    public class PermissionBuildingAccessPayload
    {
        public System.Collections.Generic.List<string> AddressKeys { get; set; } = new System.Collections.Generic.List<string>();
        // Businesses this player may work in as a granted HELPER (round-32). Separate from AddressKeys
        // (residence-guest access) — the two unlock different behavior sets. JSON-tolerant default.
        public System.Collections.Generic.List<string> HelperAddressKeys { get; set; } = new System.Collections.Generic.List<string>();
        // Merger slice 3 repair: EVERY address the host's operator ledger attributes to a player
        // OTHER than this receiver (grants irrelevant). A local reg claiming RentedByPlayer on one
        // of these, outside an active flip, is contaminated (leaked tenancy) and gets self-healed.
        public System.Collections.Generic.List<string> OtherOwnedKeys { get; set; } = new System.Collections.Generic.List<string>();
        // Shared-shop MANAGEMENT (the Business PERMISSION feature, 2026-08-21): businesses this player may MANAGE —
        // DIRECT Business grants from the operator only. Deliberately separate from HelperAddressKeys, which unions
        // merger membership (merger members help in partner shops); the permission feature never keys on the merger.
        public System.Collections.Generic.List<string> SharedManageKeys { get; set; } = new System.Collections.Generic.List<string>();
    }

    /// <summary>Guest → host → building owner: take/put on a home INTERIOR item's cargo (the fridge — the same
    /// ICargoHolder / cargoInstances model as a vehicle). The host resolves the building owner from AddressKey
    /// and routes to them; the owner applies to reg.itemInstances[ItemId] (always present in their save) and
    /// pushes that building's snapshot. Mirrors VehicleCargoReq. Op: 0 = take, 1 = put.</summary>
    // STORAGE UNIFICATION Stage B (2026-08-25): BuildingCargoReqPayload / BuildingCargoResPayload
    // (138/139) and VehicleCargoReqPayload / VehicleCargoResPayload (125/126) are RETIRED — both
    // containers ride the ONE StorageOp/StorageRes family below (ruling 37; design:
    // .modding/03-systems/storage-unification-2026-08.md).

    /// <summary>ONE storage operation against a container the SENDER does not own — the unified
    /// wire (v16) for both container kinds. Container discriminates the reference bands. Op is a
    /// STRING because the retired byte spaces collided (2 = MarkPaid on vehicles, SetStock on
    /// buildings); Ctx keeps the retired channels' literals VERBATIM. The sender does NOT name the
    /// owner: the host resolves it (ruling 38 — buildings from its ledgers, vehicles from
    /// PassengerSync.OwnerOf).</summary>
    public class StorageOpPayload
    {
        public string Container    { get; set; } = "";   // "building" | "vehicle"
        public string AddressKey   { get; set; } = "";   // building band
        public string ItemId       { get; set; } = "";   // building band: the interior item instance within it
        public string VehicleId    { get; set; } = "";   // vehicle band: the REAL id (no BAMP_ ghost prefix)
        public string PlayerId     { get; set; } = "";   // the accessor (SenderIs-checked at the host)
        public string Op           { get; set; } = "";   // "take" | "put" | "markpaid" | "setstock"
        public string Ctx          { get; set; } = "";   // op modifier — "" = plain; literals unchanged from 138/139
        public string ItemName     { get; set; } = "";
        public int    Amount       { get; set; }
        public bool   Paid         { get; set; } = true;
        public float  PricePerUnit { get; set; }
        public int    Count        { get; set; } = 1;    // stack-op multiplicity (sell/discard)
        public bool   Silent       { get; set; }         // MIRROR of a native action already applied locally:
                                                         // accessor-side OnResult must NOT place/consume/toast.
        // Sealed-box ops carry nested contents (ONE codec: StorageSync.EncodeNested/DecodeNested).
        public List<CargoNestedInfo> Nested { get; set; } = new();
    }

    /// <summary>Owner → host → accessor: the verdict on a StorageOp. Ok=false → Reason ("gone" /
    /// "full" / "locked" / "denied" / "mixed" / "occupied" / "unsupported") and the accessor
    /// places/consumes nothing.</summary>
    public class StorageResPayload
    {
        public string Container    { get; set; } = "";
        public string AddressKey   { get; set; } = "";
        public string ItemId       { get; set; } = "";
        public string VehicleId    { get; set; } = "";
        public string PlayerId     { get; set; } = "";   // the accessor this verdict is for
        public string Op           { get; set; } = "";
        public string Ctx          { get; set; } = "";
        public string ItemName     { get; set; } = "";
        public int    Amount       { get; set; }
        public bool   Paid         { get; set; } = true;
        public float  PricePerUnit { get; set; }
        public int    Count        { get; set; } = 1;    // how many stack instances the owner actually removed
        public bool   Silent       { get; set; }
        public List<CargoNestedInfo> Nested { get; set; } = new();
        public bool   Ok           { get; set; }
        public string Reason       { get; set; } = "";
    }

    /// <summary>v17 (F-2026-08-25-I proposal 2): a borrower asks the vehicle owner for the FULL
    /// cargo detail of one trunk — fired on panel open and on manifest movement while open.
    /// Routed exactly like StorageOp's vehicle branch: host resolves the owner (ruling 38).</summary>
    /// <summary>ServiceCarStop / ServiceCarResume (2026-09-02): VehicleId is the service car's synthetic id
    /// (SVC_<owner>_<n>); PlayerId is the requesting rider. The host routes to the owner named by its ghost table.</summary>
    public class ServiceCarPayload
    {
        public string VehicleId { get; set; } = "";
        public string PlayerId  { get; set; } = "";
    }

    public class TrunkDetailReqPayload
    {
        public string VehicleId { get; set; } = "";   // REAL id (no BAMP_ prefix)
        public string PlayerId  { get; set; } = "";   // the requesting accessor
        public string Sig       { get; set; } = "";   // the requester's manifest signature at ask time —
                                                      // OPAQUE to the owner, echoed back verbatim so the
                                                      // answer can be matched to ITS request (review
                                                      // MINOR-B: without it, an answer to an older ask
                                                      // could be accepted as fresh under a newer one).
    }

    /// <summary>One cargo instance, display-grade: real paid/price plus nested contents through
    /// THE codec (CargoNestedInfo). Never applied to game state — feeds the borrower's panel.</summary>
    public class CargoDetailInfo
    {
        public string ItemName     { get; set; } = "";
        public int    Amount       { get; set; }
        public bool   Paid         { get; set; } = true;
        public float  PricePerUnit { get; set; }
        public List<CargoNestedInfo> Nested { get; set; } = new();
    }

    /// <summary>v17: the owner's answer — every instance in the trunk, uncapped (the broadcast
    /// manifest's 24-instance cap does not apply here). Ok=false means "could not serve"
    /// (locked to this requester / vehicle unknown) — the panel keeps its manifest fallback and
    /// must NOT render an empty trunk from a refusal.</summary>
    public class TrunkDetailResPayload
    {
        public string VehicleId { get; set; } = "";
        public string PlayerId  { get; set; } = "";
        public string Sig       { get; set; } = "";   // the request's Sig, echoed verbatim (MINOR-B)
        public bool   Ok        { get; set; }
        public List<CargoDetailInfo> Rows { get; set; } = new();
    }

    // ── AI-staff on-demand + routed poach (types 199-202, user-approved 2026-08-29) ──────────────

    /// <summary>One AI-business staff member — exactly the serialized fields of the native
    /// AiBusinessEmployeeData. The person (name, gender, age, wage, secondary skill, demands) is
    /// regenerated deterministically from Id on the receiver, so no generated data travels.</summary>
    public class RivalStaffRow
    {
        public string Id          { get; set; } = "";
        public string Skill       { get; set; } = "";   // primarySkillName
        public float  SkillValue  { get; set; }          // host truth — replacements roll a smaller range
        public string HoursDemand { get; set; } = "";   // hoursPerWeekDemandName
        public bool   NegotiationFinished { get; set; }
        public int    ReenableAtDay      { get; set; }
    }

    public class RivalStaffReqPayload
    {
        public string AddressKey { get; set; } = "";
    }

    public class RivalStaffResPayload
    {
        public string AddressKey { get; set; } = "";
        public List<RivalStaffRow> Rows { get; set; } = new();
    }

    public class PoachClaimPayload
    {
        public string AddressKey { get; set; } = "";
        public string EmployeeId { get; set; } = "";
    }

    public class PoachResultPayload
    {
        public string AddressKey { get; set; } = "";
        public string EmployeeId { get; set; } = "";
        public bool   Ok         { get; set; }
        public List<RivalStaffRow> Rows { get; set; } = new();   // host truth AFTER the claim
    }

    /// <summary>One player business's staff roster (round-30 WS3). The synced work shifts
    /// (BusinessSync ScheduleDayInfo.WorkShifts) already carry the REAL employee ids per station+hour;
    /// the only thing other machines lack is the employee RECORDS those ids point to. Receivers inject a
    /// lightweight record per entry (real id → the synced shifts match natively) so the game's own
    /// staffing engine spawns every scheduled worker. Runtime-only on receivers (save-boundary stripped).</summary>
    public class PlayerStaffRosterPayload
    {
        public string AddressKey { get; set; } = "";
        public string PlayerId   { get; set; } = "";   // the owner publishing
        public List<StaffInfo> Staff { get; set; } = new();
    }

    public class StaffInfo
    {
        public string Id        { get; set; } = "";   // REAL employee id — must match the synced shifts
        public string Name      { get; set; } = "";
        public int    Gender    { get; set; } = -1;
        public bool   Available { get; set; } = true; // IsEmployeeAvailable on the owner (sick/replaced → false)
        // Merger slice 5 — display fidelity for injected records (a member's MyEmployees/BizMan shows the
        // partner's staff with real numbers; the payroll skip is id-keyed, so a real wage never double-bills).
        public float  Wage         { get; set; }
        public float  Satisfaction { get; set; } = 100f;
        public int    AgeDays      { get; set; }
        public List<string> Skills { get; set; } = new();   // "name=value" pairs
    }

    public class CargoNestedInfo
    {
        public string ItemName     { get; set; } = "";
        public int    Amount       { get; set; }
        public float  PricePerUnit { get; set; }
        // DEFERRED (D4, 2026-08-25): nested customColors stay off the wire — SerializableColor's
        // shape is not in the decompile tree, so a serializer for it is unverifiable; the loss is
        // cosmetic and pre-existing in both directions. Extend HERE + the one codec pair
        // (StorageSync.EncodeNested/DecodeNested) when the type is read.
    }

    /// <summary>Driver (a granted borrower) → host → all: the live pose of owner O's car V while the borrower
    /// drives it (Phase 2 handoff). The OWNER's real car becomes a kinematic follower of this; the owner's
    /// own normal fleet broadcast then carries the position to everyone else. Released=true is the final
    /// message on exit, so the owner reverts to local control.</summary>
    public class VehicleDrivePayload
    {
        public string VehicleId { get; set; } = "";   // the REAL vehicle id (no BAMP_ prefix)
        public string OwnerId   { get; set; } = "";   // the car's owner
        public string DriverId  { get; set; } = "";   // the borrower driving it (= sender)
        public float  X { get; set; }
        public float  Y { get; set; }
        public float  Z { get; set; }
        public float  Qx { get; set; }
        public float  Qy { get; set; }
        public float  Qz { get; set; }
        public float  Qw { get; set; }
        public float  Fuel { get; set; }
        public float  Damage { get; set; }   // 0..1 — the borrower's accrued damage, applied to the owner's real car
        public bool   Released { get; set; }
        // Round-232: on Released, the borrower's native parking verdict for where they left the car
        // (Helpers.ParkingState as int; -1 = absent/old sender). Without it the owner's copy keeps the
        // legality of the PICKUP spot — a car returned to a legal spot kept drawing tickets, and vice versa.
        public int    ParkState { get; set; } = -1;
        // The borrower's current building ("" = outdoors). While set, the OWNER's follow HOLDS at the
        // last exterior pose instead of chasing into interior coordinates — a space that may not even
        // be loaded on the owner's machine, where the real cart gets deregistered (CartTrace 2026-07-07).
        public string Bldg { get; set; } = "";
        public float  T { get; set; }
    }

    // ── Shared vehicle storage payloads: RETIRED (unification Stage B, 2026-08-25) ──
    // VehicleCargoReqPayload / VehicleCargoResPayload rode types 125/126; both containers now use
    // StorageOpPayload / StorageResPayload above. The retired req's OwnerId field is deliberately
    // NOT carried forward: ruling 38 — the host resolves the vehicle owner itself
    // (PassengerSync.OwnerOf); a sender-supplied owner was an unverified routing input.

    // ── Envelope ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every packet is a MessageEnvelope serialised as JSON.
    /// Keeping it simple for now — can switch to a binary format later.
    /// </summary>
    public class MessageEnvelope
    {
        [Newtonsoft.Json.JsonProperty("t")]
        public MessageType Type { get; set; }

        [Newtonsoft.Json.JsonProperty("from")]
        public string SenderId { get; set; } = "";

        /// <summary>JSON payload — type depends on MessageType.</summary>
        [Newtonsoft.Json.JsonProperty("d")]
        public string Data { get; set; } = "";

        /// <summary>v9 (2026-08 throughput): raw binary rider for the save-file class
        /// (gzip .hsg bytes). Never serialized into the JSON — it ships in the 'BZA'
        /// attachment frame after the envelope, killing the +33% base64 tax. Senders
        /// set it INSTEAD of the payload's HsgGzipBase64; receivers read it off the
        /// envelope at the dispatch seam (payload.HsgRaw = env.Attachment).</summary>
        [Newtonsoft.Json.JsonIgnore]
        public byte[]? Attachment { get; set; }

        // ── T1 (2026-08 throughput audit): wire compression ─────────────────────────────
        // Everything above this threshold deflates before it ships. Measured on the real
        // payloads: -65% traffic snapshot, -91% business snapshot, -95% market table.
        // Frame: 0x02 'B' 'Z' 'P' + type(2, little-endian, for NetStats/lane routing
        // without inflating) + deflate bytes. A JSON envelope always starts '{' (0x7B),
        // and the 0x02-prefix convention is SteamFrames' own ('BFR'/'BCL') — unambiguous.
        // Mixed-version risk is zero: ValidateHelloVersion refuses a different Version.
        private const int CompressOver = 4096;

        public byte[] Serialize()
        {
            // v9 attachment frame: 0x02 'B' 'Z' 'A' + type(2 LE, same offset as 'BZP' so the
            // one six-byte peek serves NetStats and lane routing) + jsonLen(4 LE) + envelope
            // JSON + raw attachment bytes. The attachment is already gzip — deflating it
            // again buys nothing, and the ~1 KB JSON head isn't worth a second format.
            if (Attachment != null && Attachment.Length > 0)
            {
                var json = System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(this));
                var frame = new byte[10 + json.Length + Attachment.Length];
                frame[0] = 0x02; frame[1] = (byte)'B'; frame[2] = (byte)'Z'; frame[3] = (byte)'A';
                frame[4] = (byte)((int)Type & 0xFF); frame[5] = (byte)(((int)Type >> 8) & 0xFF);
                frame[6] = (byte)(json.Length & 0xFF); frame[7] = (byte)((json.Length >> 8) & 0xFF);
                frame[8] = (byte)((json.Length >> 16) & 0xFF); frame[9] = (byte)((json.Length >> 24) & 0xFF);
                System.Buffer.BlockCopy(json, 0, frame, 10, json.Length);
                System.Buffer.BlockCopy(Attachment, 0, frame, 10 + json.Length, Attachment.Length);
                if (frame.Length > 300_000) SizeTelemetry.Note(Type, frame.Length);
                return frame;
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(this));
            // Size telemetry (2026-07-20 severity review): the silent-loss class
            // was a message quietly outgrowing a transport limit — watch growth
            // centrally (fragmentation makes size SAFE; this makes it VISIBLE).
            // Throttled per type so a chatty big sender logs once per 5 minutes.
            // Kept on the RAW size — the semantic payload is what grows, not the wire form.
            if (bytes.Length > 300_000) SizeTelemetry.Note(Type, bytes.Length);
            if (bytes.Length <= CompressOver) return bytes;
            try
            {
                using var ms = new System.IO.MemoryStream(bytes.Length / 4);
                using (var dz = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    dz.Write(bytes, 0, bytes.Length);
                if (ms.Length + 6 >= bytes.Length) return bytes;   // incompressible — ship raw
                var framed = new byte[6 + ms.Length];
                framed[0] = 0x02; framed[1] = (byte)'B'; framed[2] = (byte)'Z'; framed[3] = (byte)'P';
                framed[4] = (byte)((int)Type & 0xFF); framed[5] = (byte)(((int)Type >> 8) & 0xFF);
                ms.Position = 0; ms.Read(framed, 6, (int)ms.Length);
                return framed;
            }
            catch { return bytes; }   // compression must never lose a message
        }

        internal static class SizeTelemetry
        {
            private static readonly System.Collections.Generic.Dictionary<MessageType, long> _nextMs = new();
            internal static void Note(MessageType type, int len)
            {
                try
                {
                    long now = System.DateTime.UtcNow.Ticks / System.TimeSpan.TicksPerMillisecond;
                    lock (_nextMs)
                    {
                        if (_nextMs.TryGetValue(type, out var due) && now < due) return;
                        _nextMs[type] = now + 300_000;
                    }
                    Plugin.Logger.LogWarning($"[SizeWatch] {type} serialized at {len / 1024}KB — large message (handled by fragmentation on Steam; logged for growth tracking).");
                }
                catch { }
            }
        }

        public static MessageEnvelope? Deserialize(byte[] bytes)
        {
            try
            {
                if (bytes != null && bytes.Length > 10 && bytes[0] == 0x02 && bytes[1] == (byte)'B' && bytes[2] == (byte)'Z' && bytes[3] == (byte)'A')
                {
                    // v9 attachment frame — parse the JSON head, hang the raw rider on the envelope.
                    int jsonLen = bytes[6] | (bytes[7] << 8) | (bytes[8] << 16) | (bytes[9] << 24);
                    // Review MIN-1: compare against bytes.Length - 10, never 10 + jsonLen — the
                    // addition wraps on a hostile length and would defeat this exact guard.
                    if (jsonLen <= 0 || jsonLen > bytes.Length - 10)
                        throw new System.IO.InvalidDataException($"attachment frame jsonLen {jsonLen} vs {bytes.Length}B");
                    var env = Newtonsoft.Json.JsonConvert.DeserializeObject<MessageEnvelope>(
                        System.Text.Encoding.UTF8.GetString(bytes, 10, jsonLen));
                    if (env != null && bytes.Length > 10 + jsonLen)
                    {
                        var att = new byte[bytes.Length - 10 - jsonLen];
                        System.Buffer.BlockCopy(bytes, 10 + jsonLen, att, 0, att.Length);
                        env.Attachment = att;
                    }
                    return env;
                }
                if (bytes != null && bytes.Length > 6 && bytes[0] == 0x02 && bytes[1] == (byte)'B' && bytes[2] == (byte)'Z' && bytes[3] == (byte)'P')
                {
                    // T1 compressed frame — inflate, then parse the JSON exactly as before.
                    using var src = new System.IO.MemoryStream(bytes, 6, bytes.Length - 6);
                    using var dz  = new System.IO.Compression.DeflateStream(src, System.IO.Compression.CompressionMode.Decompress);
                    using var outMs = new System.IO.MemoryStream(bytes.Length * 4);
                    dz.CopyTo(outMs);
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<MessageEnvelope>(System.Text.Encoding.UTF8.GetString(outMs.GetBuffer(), 0, (int)outMs.Length));
                }
                return Newtonsoft.Json.JsonConvert.DeserializeObject<MessageEnvelope>(System.Text.Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex)
            {
                // Review M8: the old path THREW into the pump's LogError; the null return is more robust
                // but was completely silent — and "large messages silently dropped while small ones
                // parse" is this file's own definition of the worst failure mode. Throttled, evidential.
                try
                {
                    long now = DateTime.UtcNow.Ticks;
                    if (now >= _nextDeserWarnTicks)
                    {
                        _nextDeserWarnTicks = now + TimeSpan.TicksPerSecond * 30;
                        string head = bytes != null && bytes.Length >= 4 ? $"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}" : "short";
                        Plugin.Logger.LogWarning($"[Protocol] envelope decode FAILED ({(bytes?.Length ?? 0)}B, head {head}): {ex.Message} — dropped (throttled 30s).");
                    }
                }
                catch { }
                return null;   // callers null-check; a torn frame must not throw off-thread
            }
        }

        private static long _nextDeserWarnTicks;

        public static MessageEnvelope Create<T>(MessageType type, string senderId, T payload)
        {
            return new MessageEnvelope
            {
                Type = type,
                SenderId = senderId,
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(payload)
            };
        }

        public T? GetPayload<T>() => Newtonsoft.Json.JsonConvert.DeserializeObject<T>(Data);
    }

    // ── Payload types ─────────────────────────────────────────────────────────

    /// <summary>Wire-protocol version.  Bump ONLY when a change makes this build
    /// unable to interoperate with the previous one (message layout or semantics).
    /// Mod patch releases that don't change the wire keep the same number.  The
    /// Hello handshake refuses any peer whose number differs, so an out-of-date
    /// build can't join and then misparse messages.</summary>
    public static class ProtocolInfo
    {
        // v2 (mod 0.1.10): BusinessInfo gained DeedOwnerPlayerId (rent-vs-deed split —
        // a v1 peer would fall back to the tenancy/deed conflation and re-contaminate
        // the shared world), the vehicle-cargo manifest moved to the 4-part
        // "item=amount=paid=price" form, and VehicleCargoReq/Res gained the Silent
        // flag + OpMarkPaid.  Mixed sessions would desync — refuse them cleanly.
        //
        // v3 (mod 0.1.13): CLEAN-BREAK POLICY (user 2026-07-22 — every release bumps this;
        // mixed-version sessions are never worth the risk). Concretely this release: needs/
        // morale tuning rides GameVariablesDto + the time heartbeat (an old client would
        // drain needs at native rates while everyone else runs the dials), clients no
        // longer generate their own rental market (an old client still would, recreating
        // the zombie/wrongful-rent divergence), RentDeny carries DenyReason + expects the
        // rollback handler, and BuildingStorageSync gained placereduce/vehicletake/signset.
        //
        // v4 (mod 0.1.14): clean-break bump. Concretely this release: the host-handoff
        // campaign added StoreMirror (new message type) + Hello/manifest lineage fields
        // (HostEpoch, PlaythroughId — an old peer's disconnect-commit could land on the
        // wrong world on a name collision), the vehicle fleet went data-level
        // (VehicleEntry.Dormant — an old peer treats a parked-in-unloaded-interior
        // vehicle as sold and deletes it), the push-pose stream extended
        // (PlayerPositionPayload.MlZ + hand-IK anchors grew elbow poles, IkT 8→14),
        // and helper station refills route via new BuildingStorageSync contexts
        // (producerset/producer — an old host silently drops them).
        // v5 (mod 0.1.15): clean-break bump. Concretely this release: RegisterServePayload became a
        // serve-event stream (Kind/SelfService/Cancelled/Dur/FX-FZ/Entries), BuildingOwnershipPayload
        // gained Score/BizName (contested-tenancy arbitration), and NativeClaim/ReleaseClaim message
        // types were added. A v4 peer would misread all three.
        // v6 (mod 0.1.16): clean-break bump. ItemInstanceInfo gained the factory-workstation
        // subclass fields (WorkstationType discriminator + recipe/priority/limits), and the
        // receiver now heals a type-erased workstation by respawning it from the wire copy —
        // safe only when no v5 peer (which strips those fields) can be in the session.
        // v7 (mod 0.1.17, round-196): business sale offers. LoanOffer gained Kind="business"
        // (+AddressKey/BusinessName) and the BizTransfer* messages execute the transfer. A v6
        // peer would render a business offer as a malformed LOAN row, accept it into the loan
        // ledger, and silently drop the transfer messages — mixed sessions must be impossible.
        // v8 (2026-08, throughput T1): the WIRE FORM changed — envelopes over 4 KB ship as a
        // deflate frame (0x02 'B' 'Z' 'P' + type + compressed JSON). A v7 peer would read the
        // frame as garbage and drop every large message while small ones still parse — a
        // half-working session, the worst kind. Refuse mixed sessions outright.
        //
        // v9 (2026-08, throughput T6/T9): save-file class rides a new 'BZA' attachment frame
        // (raw gzip .hsg after the JSON head — no base64), and the daily for-sale refresh is
        // its own small BuildingsForSale (191) message instead of a full business snapshot.
        // A v8 peer can parse neither; refuse mixed sessions outright.
        //
        // v10 (2026-08, throughput T7): the interior pipeline — dirt leaves the recurring
        // snapshot triggers for its own InteriorDirtSync (193, inside-players only, ruling 33);
        // the owner → Host leg goes cargo-only when only cargo changed (InteriorCargoSync gains
        // OwnerStructHash and a client → Host direction; InteriorRequest gains a Host → owner
        // direction to demand a full re-push); the shopper schedule (CustomerEntries) stops
        // riding the owner's 2 s tick pushes. A v9 peer lacks the type, the field, and both
        // new directions; refuse mixed sessions outright.
        //
        // v11 (2026-08, throughput T8): the puppet-class streams (CustomerPuppetState/Emote/Look,
        // RegisterServe) go only to the players INSIDE the building (the position stream's Bldg
        // presence map — NOT the InteriorSync subscription, which owners never join), without the
        // sender echo; looks are host-cached + replayed on entry. A v10 peer's look-cache-ahead
        // assumption (receive-everything) no longer holds; refuse mixed sessions outright.
        //
        // v12 (2026-08, interior-edit Stage 0): InteriorSnapshot gains SeedOrHeal, and receivers
        // DISCARD any full snapshot without it while the local player is mid-edit (placement mode
        // or designer open in that building), re-asking when the edit ends. A v11 peer never sets
        // the flag, so a v12 receiver would discard its entry serves and heals — and a v11
        // receiver applies mid-edit snapshots a v12 sender assumes it will refuse (the concurrent-
        // furnishing deletion class, field 20260823-110955); refuse mixed sessions outright.
        //
        // v13 (2026-08, interior-edit Stage 1b): placement and removal forwards ride
        // BuildingInteriorDelta (194) instead of the whole-replica 140, and the owner's post-adopt
        // full re-push is retired (host grafts its cache and rebroadcasts the delta minus the
        // sender; the owner's send trackers are stamped after adopting). A v12 peer lacks the type
        // AND still re-pushes full snapshots a v13 host no longer expects; refuse mixed sessions
        // outright.
        //
        // v14 (2026-08, interior-edit Stage 2): the DESIGNER CLOSE rides the delta too — item ops
        // plus UPDATE-ONLY design entries (never a clear+rebuild) and the BulkEdit marker that
        // relaxes the receiver's remove cap for the session's accumulated edits. A v13 peer sends
        // designer closes as whole-replica 140s a v14 receiver's design expects to retire, and
        // ignores the Designs band on 194; refuse mixed sessions outright.
        //
        // v15 (2026-08, interior-edit Stage 3): BuildingInteriorEdit (140) is REMOVED — a v15
        // build neither sends nor handles it. No v14 peer sends 140 either (its last sender lost
        // its callers in Stage 2), so this bump is contract hygiene per the clean-break policy
        // (every wire-contract change bumps) rather than a behavioral incompatibility: the type
        // set the two sides agree on has changed; refuse mixed sessions outright.
        //
        // v16 (2026-08, storage unification Stage B): the twin storage channels are ONE —
        // VehicleCargoReq/Res (125/126) and BuildingCargoReq/Res (138/139) are removed, senders
        // and receivers, replaced by StorageOp/StorageRes (195/196) with a container
        // discriminator, STRING ops (the retired byte spaces collided at 2), the ctx literals
        // carried verbatim, and one nested-contents codec. The host now resolves the vehicle
        // owner itself (ruling 38) — the retired sender-supplied OwnerId is gone from the wire.
        // A v15 peer sends types a v16 build no longer handles and lacks 195/196 entirely;
        // refuse mixed sessions outright.
        //
        // v17 (2026-08-25, F-2026-08-25-I proposal 2 — trunk display parity): TrunkDetailReq/Res
        // (197/198) added — on-open full cargo detail for borrowed trunks (real paid/price +
        // nested contents via THE codec), so the borrower's panel renders the same card shapes
        // the owner's native window shows (sealed contents label, bundle tooltip, stacks past
        // the manifest's 24 cap). A v16 peer lacks both types; refuse mixed sessions outright.
        //
        // v18 (2026-08-26, slice 7 — shared-shop Insight AND Deliveries tabs). Everything below is one
        // unreleased version; while v18 is unreleased, further wire changes stay in v18.
        //   7a  SharedWorkInfoPayload gains Insight / InsightDays / Capacity; Tab accepts "insight".
        //   7b  … gains Contracts (ContractInfo, ContractItemInfo) and OwnerDay; Tab accepts "deliveries";
        //       SharedWorkEditPayload gains Contract + OwnerDay and the ops "contract" / "endcontract".
        //   7b-2 … gains Stock and SoldLastWeek — owner-counted figures the native tabs otherwise derive
        //       from a shop INTERIOR and an orderHistory that a replica does not have.
        //   7c  … gains Campaigns (CampaignInfo); Tab accepts "marketing"; SharedWorkEditPayload gains
        //       AgencyKey + TypeName and the ops "campaign" / "campaignremove" / "campaigncancel".
        //   7d  … gains Settings (SettingsInfo, incl. Uniforms/Presets/HasUniformLocker); Tab accepts
        //       "settings"; SharedWorkEditPayload gains Settings, SkillName, PresetId, Preset and the ops
        //       "rename" / "logo" / "uniform" / "uniformimport".
        // A v17 peer would drop the fields silently and render zeros; the freeze rule (design doc §8) makes
        // ANY wire-visible change a version bump — refuse mixed sessions outright.
        //
        //  19  GAME 1.0 PORT (2026-08-29). GameVariablesDto gains SellingMultiplier: 1.0 added
        //      sellingMultiplier to BOTH DifficultySetting and GameVariables (GameVariables.cs:53,
        //      default 0.75f), and it drives ItemHelper.GetSellingMultiplier, i.e. every sell-back
        //      price in the game.
        //
        //      THIS ENTRY WAS WRITTEN, THEN WITHDRAWN, THEN RESTORED ON THE SAME DAY. The withdrawal
        //      is recorded because the mistake is instructive and cheap to repeat:
        //        * WRONG REASON FOR WITHDRAWING: "the game honours gv.sellingMultiplier only under
        //          Difficulty.Custom (ItemHelper.cs:85), and the mod never sends Custom." The first
        //          half is true. The second half came from reading MPServer.Preset(), which clamps
        //          to Easy/Normal/Hard because it builds PRESETS - not from reading the path that
        //          actually ships the host's settings.
        //        * WHAT IS TRUE: MPCanvasUI.MarkCustom() sets _hostSettings.Difficulty = "Custom"
        //          the moment the host hand-edits ANY row, and _hostSettings is the very DTO passed
        //          to MPServer.StartNewGame. So the mod sends Custom routinely, and under Custom the
        //          gate is satisfied for free: gv.sellingMultiplier defaults to 0.75f, which is > 0f.
        //          The field is not inert; it is LIVE on every custom-difficulty session.
        //      Lesson, and the reason this is written out rather than deleted: a helper that clamps
        //      a value tells you nothing about what the real caller sends. Read the send path.
        //
        //      A v18 peer omits the key; name-keyed Newtonsoft then leaves the C# initializer 0.75,
        //      so the peer would silently use ITS OWN sell-back rate rather than the host's. That is
        //      a real divergence, so the freeze rule applies: wire-visible change => version bump.
        // v20 (2026-08-29): storage ctx vocabulary grew "wornPhone" (the 1.0 phone accessory can be
        //      stored in item holders; the guest route + unequip-on-confirm now carry it). A v19 peer
        //      never sends the literal and would treat it as an unknown ctx on receive — mixed
        //      sessions refuse at Hello per the freeze rule.
        // v21 (2026-09-03): new message ShopValuation=205 (H-BIZ-1). A v20 peer drops it as "Unknown message type"
        //      and would show $0 on a friend's shop page; mixed sessions refuse at Hello per the freeze rule.
        public const int Version = 21;
    }

    /// <summary>Sent by client on connect.</summary>
    /// <summary>Round-253: host → joiner on a mod-list mismatch. Summary is the short
    /// player-facing notice; Detail is the capped diff for the joiner's log.</summary>
    public class ModMismatchPayload
    {
        public string Summary { get; set; } = "";
        public string Detail  { get; set; } = "";
    }

    /// <summary>Bug-report v2 (2026-08-15): ask every connected peer for its logs while a
    /// bug bundle is being assembled. RequestId pairs the replies with the waiting bundle.</summary>
    public class PeerLogRequestPayload
    {
        public string RequestId { get; set; } = "";
    }

    /// <summary>Round-270: a joining player's world-download progress (display-only).</summary>
    public class JoinProgressPayload
    {
        public string Pid     { get; set; } = "";
        public int    Percent { get; set; }
    }

    /// <summary>Round-269: a Business-granted guest's grab in another player's business.
    /// StockOnly=true mirrors the native shelf-stock take (the shelf STAYS, its stock
    /// drains — the whole-instance removal would delete the owner's shelf).</summary>
    public class GuestCargoGrabPayload
    {
        public string AddressKey     { get; set; } = "";
        public string ItemInstanceId { get; set; } = "";
        public string ItemName       { get; set; } = "";   // stock-clear matching + logs
        public bool   StockOnly      { get; set; }          // true = drain matching stock, keep the instance
        public string TakerPid       { get; set; } = "";
    }

    /// <summary>One log file for a bug bundle. Redacted (IPs + Windows usernames) on the
    /// OWNER'S machine before sending, then gzipped — raw log text never crosses the wire.</summary>
    public class PeerLogReplyPayload
    {
        public string RequestId  { get; set; } = "";
        public string FromPid    { get; set; } = "";   // sender's display PlayerId — names the file in the bundle
        public string FileName   { get; set; } = "";   // "Player.log" / "Player-prev.log"; "" when TotalFiles=0
        public string GzipBase64 { get; set; } = "";
        public int    RawLength  { get; set; }          // redacted text length before gzip
        public int    TotalFiles { get; set; }          // how many replies this peer sends for the request (0 = none readable)
        public bool   Truncated  { get; set; }          // head+tail capped (same 4MB cap as the reporter's own logs)
    }

    public class HelloPayload
    {
        public string PlayerId { get; set; } = "";
        public string Version  { get; set; } = "";   // mod version string (display)
        /// <summary>Immutable identity (SteamID64 / guid-…) — the key for save +
        /// ownership persistence, distinct from the mutable PlayerId display name.</summary>
        public string StableId { get; set; } = "";
        /// <summary>Wire-protocol version (ProtocolInfo.Version).  A missing field
        /// from an older build deserializes to 0, so it fails the host's check.</summary>
        public int    Protocol { get; set; }
        /// <summary>Game version name (e.g. "EA 0.11") — the host refuses a mismatch
        /// so two players on different game builds can't desync.</summary>
        public string Game     { get; set; } = "";
        /// <summary>Round-102: fingerprint of the loaded item/business CONTENT. `Game` above is
        /// only the version-folder name, so two installs a month apart both pass it while
        /// carrying different item data (our own rig did exactly that). Log-only evidence — the
        /// host warns on a mismatch and never refuses. Empty from older builds.</summary>
        public string Content  { get; set; } = "";
        /// <summary>Round-253: this machine's installed-mod list (same comma format as the bug
        /// report's InstalledMods line). The host diffs it against its own and INFORMS both
        /// sides on a mismatch — never refuses. Empty from older builds.</summary>
        public string Mods     { get; set; } = "";
        /// <summary>2026-09-01 (update-impact review): identity of the game BUILD — the main game assembly's
        /// module id. `Game` is only the version-folder name ("1.0") and `Content` hashes item/business NAMES,
        /// so a code-only Steam patch moved neither while the 2026-09-01 update changed the save schema
        /// (NetWorth removed, midnightBankBalances added, TodoTask.priorityOffset, TodoTaskType +2). The host
        /// refuses a mismatch. Empty from older builds (they fail the protocol check first anyway).</summary>
        public string GameBuild { get; set; } = "";
        // Phase 3 rejoin offer: set when the client holds a pending DISCONNECT save (un-uploaded progress
        // from its last leave). The host may request it (LoadDataPayload.AwaitClientDisconnectUpload) and,
        // after validating the uploaded save's ACTUAL day, restore it instead of the host's older copy.
        public bool   HasDisconnectSave     { get; set; }
        public string DisconnectSessionBase { get; set; } = "";
        public int    DisconnectDay         { get; set; }
        public long   DisconnectSavedAtUnix { get; set; }
        /// <summary>Handoff slice 3: the WORLD the disconnect save belongs to (from the
        /// client's mirrored manifest at marker-write time). The host requests the upload
        /// only when this matches its own lineage — session NAMES can collide across
        /// different worlds (esp. after handoffs/forks), and a name-only match could
        /// commit a foreign world's character file into this session. Empty (pre-field
        /// marker / no mirrored manifest yet) = legacy name-only matching.</summary>
        public string DisconnectPlaythroughId { get; set; } = "";
        /// <summary>Round-281 CAPABILITY flag: "my build understands MessageType.InteriorCargoSync."
        /// Additive — an older client omits it, Newtonsoft leaves it FALSE, and the host then never
        /// sends that peer (or anyone subscribed alongside it) the new message type.  This exists
        /// because the mod VERSION string cannot answer the question: 0.1.17-without-round-281 and
        /// 0.1.17-with-round-281 both report "0.1.17", so version equality alone would hand a new
        /// message to a build that has no handler for it.  The host gates on this flag AND version
        /// equality — the flag proves the code is there, the version keeps the pairing conservative.</summary>
        public bool   CargoDelta { get; set; }
        /// <summary>Round-283 CAPABILITY flag (client → host): "my build drops stale GameTimeSync by
        /// Seq."  It is NOT about parsing — an older client parses an express clock packet perfectly
        /// well; what it lacks is the freshness guard, so a packet that overtook an older one on the
        /// express lane could be followed by that older one and leave the client wrongly AheadHeld.
        /// The host therefore expresses clock sends ONLY to peers that raise this.  Additive: absent
        /// from every older build, Newtonsoft leaves it FALSE, and that peer keeps the ordered lane —
        /// byte-identical to today.  Gated the round-281 way (flag AND version equality).</summary>
        public bool   ExpressLane { get; set; }
    }

    /// <summary>
    /// Full world snapshot sent to a newly connecting client.
    /// Contains everything shared between players.
    /// </summary>
    public class WorldSnapshotPayload
    {
        /// <summary>Street+number → owner player ID. Empty string = available.</summary>
        public Dictionary<string, string> BuildingOwners { get; set; } = new();

        /// <summary>Street+number → owner player ID for BOUGHT real estate (distinct from
        /// BuildingOwners, which is who RENTS/operates). Empty string = unowned.</summary>
        public Dictionary<string, string> BuildingRealEstateOwners { get; set; } = new();

        /// <summary>Serialised List&lt;ProductMarketEntry&gt; as JSON string.</summary>
        public string MarketEntriesJson { get; set; } = "[]";

        /// <summary>Host's session id — clients adopt it so both machines' logs
        /// correlate to the same session (observability).</summary>
        public string SessionId { get; set; } = "";
    }

    /// <summary>A single building changed ownership.</summary>
    public class BuildingOwnershipPayload
    {
        /// <summary>Round-162: development score of the sender's copy (furniture + stocked products) —
        /// only meaningful on NativeClaim messages; the contested-tenancy arbiter's evidence.</summary>
        public int Score { get; set; }
        /// <summary>Round-163: the sender's local BusinessName for that building - forensic context so a
        /// "my business disappeared" report can be matched to the arbitration event that moved it.</summary>
        public string BizName { get; set; } = "";

        /// <summary>"StreetNumber StreetName" e.g. "14 OakStreet"</summary>
        public string AddressKey  { get; set; } = "";

        /// <summary>Player ID of new owner, or empty string if vacated.</summary>
        public string OwnerPlayerId { get; set; } = "";

        /// <summary>Daily rent amount (for the client to record).</summary>
        public float DailyRent { get; set; }

        /// <summary>Last deposit paid.</summary>
        public float LastDeposit { get; set; }

        /// <summary>Round-50: why a RentDeny was issued (host-side availability verdict) —
        /// for the client's log/toast. Optional; absent/empty on older senders.</summary>
        public string DenyReason { get; set; } = "";
    }

    /// <summary>Periodic market price broadcast.</summary>
    public class MarketSnapshotPayload
    {
        public string MarketEntriesJson { get; set; } = "[]";
    }

    /// <summary>Broadcast from host when the lobby player list changes.</summary>
    public class LobbyUpdatePayload
    {
        /// <summary>Ordered list of player IDs currently in the lobby.</summary>
        public List<string> Players { get; set; } = new();

        /// <summary>True if the host enforces one starting cash for everyone (else each client sets their own).</summary>
        public bool EnforceStartingCash { get; set; } = true;

        /// <summary>Per-player starting age (playerId → age), so every lobby shows each
        /// player's self-chosen age.  Cash is host-dictated and not synced for display.</summary>
        public Dictionary<string, int> Ages { get; set; } = new();

        /// <summary>True if the host is RESUMING a saved game (not starting a new one).
        /// Clients hide the new-game settings (age/cash) since they come from the save.</summary>
        public bool   LoadMode        { get; set; }
        /// <summary>Name of the save being resumed (for the client's "Resuming…" line).</summary>
        public string LoadSessionName { get; set; } = "";
        /// <summary>Round-283 CAPABILITY flag (host → client): "my build drops stale PhaseReport by
        /// Seq" — the mirror of HelloPayload.ExpressLane, so the client knows whether it may put its
        /// phase reports on the express lane.
        ///
        /// WHY THIS MESSAGE carried it (least blast radius of the candidates): the host had no
        /// dedicated host→client capability channel, so the flag had to ride something the host
        /// already sends.  LobbyUpdate is the only host→client message the client provably receives
        /// in BOTH join legs before StartGameNew/StartGameLoad (lobby leg) and before the LoadData
        /// that triggers the first 'Loading' report (mid-game leg).  Round-283 verifier precision:
        /// a connected client's very FIRST phase report (the Lobby/early one, gated only on
        /// IsConnected) can precede this flag's arrival — harmless, because HostExpressLane
        /// defaults FALSE and those early reports simply ride the ordered lane.  Re-sent on every
        /// roster CHANGE (join/leave/kick) — so a lost copy heals at the next roster event, not on
        /// a timer; fail-safe either way, since absent = ordered lane.  Welcome/LoadData were
        /// rejected: both arrive later on at least one leg.  Absent (older host) = FALSE = today's
        /// behaviour exactly.</summary>
        public bool   HostExpress     { get; set; }
    }

    /// <summary>Client → Host: this player's lobby preferences.  Currently just the
    /// self-chosen starting age (each player picks their own; cash stays host-set).</summary>
    public class LobbyPrefPayload
    {
        public string PlayerId { get; set; } = "";
        public int    Age      { get; set; }
    }

    /// <summary>Sent by host when starting the game (new or load).</summary>
    public class StartGamePayload
    {
        /// <summary>Save slot name for load; empty for new game.</summary>
        public string SaveName { get; set; } = "";

        /// <summary>New-game settings chosen by the host; null for a load.</summary>
        public GameVariablesDto? Settings { get; set; }

        /// <summary>True if the host enforces one starting cash; false = clients use their own.</summary>
        public bool EnforceStartingCash { get; set; } = true;

        /// <summary>Round-284 "load ticket": the host's id for THIS serve of a world to this
        /// player.  The client adopts it and echoes it in every PhaseReportPayload, so the
        /// host can key the join-baseline fire to the exact load a report describes without
        /// depending on cross-type arrival order.  0 = older host (never stamps).</summary>
        public int LoadGen { get; set; }
    }

    /// <summary>
    /// Plain serialisable mirror of the game's GameVariables struct.  The host
    /// builds this from a difficulty preset (and later the toggle UI) and sends
    /// it to every client so the whole multiplayer game uses identical settings.
    /// Defaults below are the game's vanilla "Normal" values, with the two
    /// multiplayer overrides baked in (no tutorial, no energy need).
    /// </summary>
    public class GameVariablesDto
    {
        public string Difficulty                       { get; set; } = "Normal";
        public int    StartingAge                      { get; set; } = 18;
        public bool   DisableAging                     { get; set; } = false;
        // Round-261 (field 20260811-022559): default FALSE — the old true default was a
        // stale pre-dials design ("MP: no sleep-skip") that contradicted NeedsDrainPercent's
        // default 10. The lobby only corrects this bool when the drain dial is TOUCHED, and
        // world creation ORs it in (MPServer StartNewGame apply) — so every untouched-default
        // new world was born needs-off (round-236's heartbeat then rescued it ~3s later; a
        // birth defect a later mechanism silently compensates is the rivals-saga pattern).
        // drain==0 still drives the flag through the dial + creation paths.
        public bool   DisableEnergy                    { get; set; } = false;
        public bool   DisableHappiness                 { get; set; } = false;
        public bool   AllCoursesUnlocked               { get; set; } = false;
        public int    StartingMoney                    { get; set; } = 10000;  // vanilla Normal (was a $100k dev leftover)
        public int    TaxPercentage                    { get; set; } = 10;
        public int    DaysPerYear                      { get; set; } = 60;
        public float  MarketPriceMultiplier            { get; set; } = 1f;
        public float  EmployeeHourlySalaryMultiplier   { get; set; } = 1f;
        public float  BankInterestMultiplier           { get; set; } = 1f;
        public bool   TutorialEnabled                  { get; set; } = false;  // MP: no story quests
        /// <summary>DEAD SINCE GAME 1.0. The banking overhaul removed the flat rate from BOTH
        /// DifficultySetting and GameVariables, so the host now always sends 0f and the client no
        /// longer applies it — 0f whenever the difficulty ASSET resolves (MPServer.cs:3682); on the
        /// EA-fallback branch (MPServer.cs:3703-3709) and on MPCanvasUI's empty-DTO catch it keeps this
        /// property's initializer, -0.5f. ("Always sends 0f" was too strong.) The FIELD stays on the wire so the DTO shape (and therefore the
        /// protocol version) is unchanged — but note the MEANING changed under a constant version:
        /// a released-0.2.0 peer that got past the game-name gate would apply gv.bankInterestRate = 0.
        /// The version gate normally refuses such a peer; this is recorded because "shape unchanged"
        /// is not by itself proof that an old peer is safe.</summary>
        public float  BankInterestRate                 { get; set; } = -0.5f;
        public float  RivalsDifficultyMultiplier       { get; set; } = 1f;
        public bool   DisableVehicleDamage             { get; set; } = false;
        public bool   DisableVehicleFuel               { get; set; } = false;
        public bool   AllContactsUnlocked              { get; set; } = false;
        public float  BaseCustomerPromotionMultiplier  { get; set; } = 0.5f;
        public float  WholesaleUrgentFeeMultiplier     { get; set; } = 0.2f;
        public float  ImporterUrgentFeeMultiplier      { get; set; } = 0.75f;
        public bool   DisableWholesaleAndImportLimits  { get; set; } = false;
        public bool   AllProductsAvailableFromImporters{ get; set; } = false;
        public float  ExportMultiplier                 { get; set; } = 0.65f;
        /// <summary>NEW IN GAME 1.0. Sell-back price multiplier (ItemHelper.GetSellingMultiplier),
        /// on both DifficultySetting and GameVariables, replacing a hardcoded 0.8. LIVE whenever the
        /// host has hand-edited any setting, because that flips the session to Difficulty.Custom and
        /// the game then reads gv.sellingMultiplier directly. Default 0.75 is the game's own.</summary>
        public float  SellingMultiplier                { get; set; } = 0.75f;

        // ── Needs & morale tempo (2026-07-20, additive — old peers ignore) ──
        // Single-percent controls, "% of native"; 0 = the respective system off.
        public int NeedsDrainPercent             { get; set; } = 40;    // energy+hunger drain; 0 drives native DisableEnergy (default 10 -> 40, user 2026-09-02)
        public int RestSpeedPercent              { get; set; } = 300;   // energy regen while resting
        /// <summary>Morale-buff duration dial, "% of native speed" (min 1): buff durations
        /// scale INVERSELY (10% → positives last 10×). Game 1.0 deleted the sad-period
        /// subsystem, so the second half of this dial's original job (scaling the sad-period
        /// roll) no longer exists; the wire field and its meaning for buffs are unchanged.</summary>
        public int MoraleTempoPercent            { get; set; } = 10;
    }

    /// <summary>
    /// Client → Host: this player's game scene has finished loading.
    /// Part of the startup pause hold — the game stays frozen until every
    /// player has reported in.
    /// </summary>
    public class PlayerInGamePayload
    {
        public string PlayerId { get; set; } = "";
    }

    /// <summary>
    /// Host → All: every player has finished loading — release the startup
    /// pause hold so the game resumes for everyone at once.
    /// </summary>
    public class StartupReleasePayload
    {
    }

    /// <summary>
    /// Host → All: the list of players who have NOT yet finished loading.
    /// Drives the "waiting for &lt;player&gt;" startup screen.
    /// </summary>
    public class StartupStatusPayload
    {
        public List<string> WaitingFor { get; set; } = new();
    }

    /// <summary>
    /// The shared manual (pause-button) pause state.  In the multiplayer time
    /// model this is the ONLY player-driven pause — menus/benches never pause.
    /// </summary>
    public class ManualPausePayload
    {
        public bool Paused { get; set; }
    }

    /// <summary>Broadcast at ~10 Hz so other players can see this player's position.</summary>
    public class PlayerPositionPayload
    {
        public string PlayerId { get; set; } = "";
        public float X    { get; set; }
        public float Y    { get; set; }
        public float Z    { get; set; }
        /// <summary>Y-axis rotation (yaw) in degrees.</summary>
        public float RotY { get; set; }
        /// <summary>Sender's unscaled clock at sample time (see VehicleFleetPayload.T).</summary>
        public float T    { get; set; }
        /// <summary>Address key of the building the sender is inside ("" = outdoors).
        /// Drives the cross-interior mask: same-type interiors share one detached
        /// coordinate space, so without this a player inside building A renders
        /// inside building B for anyone standing there (2026-06-11).</summary>
        public string Bldg { get; set; } = "";
        /// <summary>Name of the prop in the character's HandContent skeleton node
        /// ("" = empty hands).  CarryProbe 2026-06-12: ALL held items (boxes,
        /// baskets, …) are prefab clones parented there; receivers clone a
        /// scene template into their avatar's HandContent.</summary>
        public string Held { get; set; } = "";
        /// <summary>Field 181203: the sender is mopping. The mop's visual hangs under the
        /// character's HAND BONE (BaseHuman.AddHandObject), NOT the HandContent node the
        /// Held capture watches — so it needs its own flag; the receiver mirrors it with
        /// the game's own hand-object API + the CleaningIdle stance.</summary>
        public bool Mop { get; set; }
        /// <summary>The held prop's LOCAL transform under HandContent —
        /// [px,py,pz, ex,ey,ez] (position + euler).  Mirrored from the
        /// holder's machine so the remote prop sits exactly where theirs does
        /// (baskets hang off-axis; identity placement looked wrong).</summary>
        public List<float> HeldT { get; set; } = new();

        /// <summary>Round-74e — the sender MODEL's local Z offset under its root.
        /// The native push stance slides the model backward relative to the root
        /// (HandTruck.EnterVehicle :169-172, per-vehicle amount) — invisible to
        /// root-position sync; without it the clone's body stood a meter too far
        /// forward with hands IK'd to the handle (the non-native push posture,
        /// PushPose probe verdict 2026-07-24). 0 when not pushing.</summary>
        public float MlZ { get; set; }

        // ── Animator state (generic full-mirror) ──────────────────────────────
        // Parameter indices are positions in Animator.parameters; the controller
        // asset is identical for every player so an index means the same thing
        // everywhere.  Floats/ints are sent in full each tick; bools are the list
        // of currently-true indices (all others taken as false).  Triggers are
        // momentary and ride the separate PlayerAnimTrigger message.

        /// <summary>Float animator params: index → value (full state).</summary>
        public Dictionary<int, float> AnimF { get; set; } = new();

        /// <summary>Int animator params: index → value (full state).</summary>
        public Dictionary<int, int> AnimI { get; set; } = new();

        /// <summary>Indices of bool animator params currently set true.</summary>
        public List<int> AnimB { get; set; } = new();
        /// <summary>Animator layer weights by layer index.  Game scripts drive
        /// these on the real character (upper-body hold layer while pushing a
        /// cart etc.); the script-stripped clone needs them mirrored or a
        /// state can be entered yet render at blend weight 0.</summary>
        public List<float> LayerW { get; set; } = new();
        /// <summary>Hand-IK mirror while pushing an open vehicle (else empty):
        /// [Lx,Ly,Lz, Rx,Ry,Rz, Lweight,Rweight] — IK target positions in
        /// VEHICLE-local space + Animation Rigging hand-rig weights.</summary>
        public List<float> IkT { get; set; } = new();
    }

    /// <summary>One animator trigger fired by a player (one-off action animation).</summary>
    public class AnimTriggerPayload
    {
        public string PlayerId   { get; set; } = "";
        /// <summary>Index of the trigger parameter in Animator.parameters.</summary>
        public int    ParamIndex { get; set; }
    }

    /// <summary>One vehicle owned by a player — its identity + current transform.</summary>
    public class VehicleEntry
    {
        /// <summary>The owning VehicleInstance's stable unique id.</summary>
        public string VehicleId { get; set; } = "";
        /// <summary>VehicleTypeName enum name, e.g. "VordV150".</summary>
        public string TypeName  { get; set; } = "";
        /// <summary>The vehicle's colour name.</summary>
        public string ColorName { get; set; } = "";
        /// <summary>True if the owner is currently driving this vehicle.</summary>
        public bool   Driving   { get; set; }
        /// <summary>Owner's current fuel (liters). Synced so a granted DRIVABLE proxy isn't stuck at 0%
        /// (its local instance spawns empty) — applied to the proxy's FuelModule when not driven locally.</summary>
        public float  Fuel      { get; set; }
        public float  X { get; set; }
        public float  Y { get; set; }
        public float  Z { get; set; }
        /// <summary>Full rotation quaternion (cars pitch/roll on slopes).</summary>
        public float  Qx { get; set; }
        public float  Qy { get; set; }
        public float  Qz { get; set; }
        public float  Qw { get; set; }
        /// <summary>Cargo manifest ("itemId=amount;…" — '=' because EA 0.11 item
        /// ids contain colons) so remote ghosts show the bed/handtruck boxes
        /// (they derive from cargo) — user bug 2026-06-11.</summary>
        public string Cargo { get; set; } = "";
        /// <summary>Count of ITEM INSTANCES being transported (VehicleInstance
        /// .cargoIds — the channel hand trucks use; separate from loose-cargo
        /// amounts).  Receivers render that many generic boxes.</summary>
        public int CarriedItems { get; set; }
        /// <summary>Address key of the building this vehicle is inside ("" =
        /// outdoors).  Cross-interior mask v3 (round-74): sourced from the
        /// VEHICLE's own native street data (VehicleInstance.Address, which the
        /// game maintains on every door transition) — never from the owner's
        /// whereabouts (the old 30m owner-proximity heuristic mis-tagged carts
        /// whenever the owner stood in an adjacent interior).</summary>
        public string Bldg { get; set; } = "";
        /// <summary>Round-74 data-level fleet: TRUE for a vehicle whose live
        /// object is unloaded on the owner's machine (native interior scoping —
        /// vehicles parked in a building unload with its interior).  The entry
        /// carries the SAVE-DATA position/cargo instead of a live transform.
        /// Receivers keep the ghost parked there; absence from the payload now
        /// unambiguously means SOLD.</summary>
        public bool Dormant { get; set; }
        /// <summary>Private-driver / arrival SERVICE car (2026-09-02, .modding/03-systems/private-driver-mp.md): a Gley
        /// car this owner's machine spawned for a ride — mirrored so others see it and the host's traffic brakes for
        /// it. Receivers spawn the normal player-vehicle ghost but never depict the owner inside it, never count it as
        /// in-use, label it "<owner>'s driver", and attach the GhostTaxi ride hook. Always Driving (volatile pose).</summary>
        public bool Service { get; set; }
    }

    /// <summary>
    /// A player's complete vehicle fleet — every owned vehicle, parked or driven.
    /// Broadcast at ~10 Hz; it is the full truth for that owner, so a vehicle
    /// that drops out of the list has been sold/removed and its ghost is despawned.
    /// </summary>
    public class VehicleFleetPayload
    {
        public string OwnerId { get; set; } = "";
        /// <summary>Wave-2 (audit T4): true = ONLY the vehicles listed (the driven set, 10 Hz) —
        /// absence means NOTHING. Full-truth packets (false) keep absence = sold, and ride on a
        /// resting-set change or a 5 s heartbeat. Measured pre-fix: ~24 KB/s per client of
        /// byte-identical parked fleets.</summary>
        public bool Partial { get; set; }
        public List<VehicleEntry> Vehicles { get; set; } = new();
        /// <summary>Sender's unscaled clock at sample time.  Receivers use the
        /// DIFFERENCE between two stamps from the same sender to measure true
        /// velocity for dead reckoning — packet arrival times are quantized to
        /// the receiver's frames and useless for velocity at low FPS.</summary>
        public float  T { get; set; }
    }

    /// <summary>One AI-traffic car in a host traffic snapshot. T2 (2026-08 throughput): the pose is
    /// QUANTIZED (position in whole centimetres, quaternion ×10000 — both far below visible error at
    /// traffic distances), and the never-changing identity (model + paint) rides ONLY when this peer
    /// has not seen this pool slot's current occupant — on first sight, on recycle, and on re-entering
    /// the peer's send radius. 40% of every row was identity re-sent ten times a second (audit #1).</summary>
    public class TrafficCarDto
    {
        /// <summary>Stable Gley pool index — identifies this car across snapshots.</summary>
        public int    Index { get; set; }
        /// <summary>Vehicle model name — null when this peer already knows this slot's identity.</summary>
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public string Model { get; set; }
        /// <summary>Position in CENTIMETRES (world metres × 100, rounded).</summary>
        public int    X { get; set; }
        public int    Y { get; set; }
        public int    Z { get; set; }
        /// <summary>Rotation quaternion components × 10000.</summary>
        public int    Qx { get; set; }
        public int    Qy { get; set; }
        public int    Qz { get; set; }
        public int    Qw { get; set; }
        /// <summary>
        /// Body colours — flattened 6 floats (tint RGB + fresnel RGB) per SH_Vehicle renderer; a single
        /// group = every renderer that colour. Null when the peer already knows them (same rule as Model).
        /// </summary>
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public List<float> Colors { get; set; }
    }

    /// <summary>
    /// Host → All: the full AI-traffic snapshot.  It is the complete truth — a
    /// car index absent from the list has despawned and its ghost is removed.
    /// </summary>
    public class TrafficSnapshotPayload
    {
        public List<TrafficCarDto> Cars { get; set; } = new();
        /// <summary>Host's unscaled clock at sample time (see VehicleFleetPayload.T).</summary>
        public float T { get; set; }
    }

    /// <summary>One parked vehicle in a host parked-vehicle snapshot.
    /// Same shape as TrafficCarDto but the identity Key is the host's
    /// `GameObject.GetInstanceID()` instead of a Gley pool index, since
    /// parked cars come from a static pool keyed by model name.</summary>
    public class ParkedVehicleDto
    {
        /// <summary>Stable host-side identity (GameObject.GetInstanceID).</summary>
        public long   Key   { get; set; }
        public string Model { get; set; } = "";
        public float  X { get; set; }
        public float  Y { get; set; }
        public float  Z { get; set; }
        public float  Qx { get; set; }
        public float  Qy { get; set; }
        public float  Qz { get; set; }
        public float  Qw { get; set; }
        /// <summary>Body colours — same per-renderer encoding as TrafficCarDto.</summary>
        public List<float> Colors { get; set; } = new();
    }

    /// <summary>Host → All: parked-vehicle state.  Either a DIFF (default —
    /// IsFullSnapshot=false; `Cars` is adds, `RemovedKeys` is removes) or a
    /// FULL snapshot (IsFullSnapshot=true; `Cars` is the complete authoritative
    /// set, `RemovedKeys` is ignored).  Diffs are broadcast at most every 1s
    /// only when something changed; a full snapshot is broadcast every 30s
    /// for resync + new-joiner coverage.</summary>
    public class ParkedSnapshotPayload
    {
        public List<ParkedVehicleDto> Cars { get; set; } = new();
        public List<long> RemovedKeys { get; set; } = new();
        public bool IsFullSnapshot { get; set; } = false;
    }

    /// <summary>Client → Host: a player hailed a traffic taxi; the host stops it.</summary>
    public class TaxiHailPayload
    {
        public string PlayerId  { get; set; } = "";
        /// <summary>Gley pool index of the taxi being hailed.</summary>
        public int    TaxiIndex { get; set; }
    }

    /// <summary>One traffic-light intersection's current state.</summary>
    public class LightStateDto
    {
        /// <summary>Index into IntersectionManager.allIntersections.</summary>
        public int  Index  { get; set; }
        /// <summary>The road currently green/yellow.</summary>
        public int  Road   { get; set; }
        /// <summary>True if the current road is in its yellow phase.</summary>
        public bool Yellow { get; set; }
    }

    /// <summary>Host → All: the state of every traffic-light intersection.</summary>
    public class TrafficLightsPayload
    {
        public List<LightStateDto> Lights { get; set; } = new();
    }

    /// <summary>Broadcast by the host when a client disconnects mid-game.</summary>
    public class PlayerLeftPayload
    {
        public string PlayerId { get; set; } = "";
    }

    /// <summary>
    /// One player's character appearance: gender + the active variant name per
    /// body category (Hair, Torso, Legs, …).  The character model is a universal
    /// prefab containing every variant, so this selection reproduces any look.
    /// </summary>
    public class PlayerAppearancePayload
    {
        public string PlayerId { get; set; } = "";
        public string Gender   { get; set; } = "Male";
        public Dictionary<string, string> Variants { get; set; } = new();
        /// <summary>Every Color shader property on each active variant's materials.</summary>
        public List<ColorEntry> Colors { get; set; } = new();
        /// <summary>Every blendshape weight on each active variant's mesh (body-shape morphs).</summary>
        public List<BlendEntry> Blends { get; set; } = new();
        /// <summary>Every Float/Range shader property — the CLOTHES DYE lives
        /// here (texture-array slice index on SH_CharacterClothes*), not in a
        /// color property (probe-classified 2026-06-11).</summary>
        public List<FloatEntry> Floats { get; set; } = new();
    }

    /// <summary>One float shader property: category + material index + name + value.</summary>
    public class FloatEntry
    {
        public string Cat  { get; set; } = "";
        public int    Mat  { get; set; }
        public string Prop { get; set; } = "";
        public float  V    { get; set; }
    }

    /// <summary>One blendshape morph: category + shape name + weight (0-100).</summary>
    public class BlendEntry
    {
        public string Cat    { get; set; } = "";
        public string Shape  { get; set; } = "";
        public float  Weight { get; set; }
    }

    /// <summary>One colour value: category + material index + shader property + RGBA.</summary>
    public class ColorEntry
    {
        public string Cat  { get; set; } = "";
        public int    Mat  { get; set; }
        public string Prop { get; set; } = "";
        public float  R { get; set; }
        public float  G { get; set; }
        public float  B { get; set; }
        public float  A { get; set; }
    }

    /// <summary>Host → All: the appearance of every player in the session.</summary>
    public class AppearanceSyncPayload
    {
        public List<PlayerAppearancePayload> Players { get; set; } = new();
    }

    /// <summary>
    /// Periodic broadcast (~every 30 s) so clients stay in sync with the host's game clock.
    /// The host's time is authoritative; clients snap their clock to match.
    /// </summary>
    public class GameTimeSyncPayload
    {
        /// <summary>Current in-game day number.</summary>
        public int   Day       { get; set; }
        /// <summary>Time of day as fractional hours (0 = midnight, 12 = noon, 23.99 = just before midnight).</summary>
        public float TimeOfDay { get; set; }
        /// <summary>
        /// Current Time.timeScale equivalent (1 = normal, 2 = double, 0 = paused).
        /// -1 means "speed not included in this packet" — client should not apply it.
        /// </summary>
        public float Speed     { get; set; } = -1f;
        /// <summary>Host rain state: -1 absent/unknown (older builds omit the field —
        /// JSON-tolerant), 0 dry, 1 raining.  Clients align their local RainHelper.</summary>
        public int   RainState { get; set; } = -1;
        /// <summary>Host rain intensity 0..1 while raining, -1 absent/dry/unknown
        /// (additive like RainState — older peers ignore it).  Consumed only at the
        /// moment a client STARTS rain: the game's transition API is a TOGGLE, so a
        /// mid-rain re-apply would turn rain off instead of adjusting it.</summary>
        public float RainIntensity { get; set; } = -1f;
        /// <summary>Needs/morale tuning percents (drain, rest, morale-tempo) —
        /// -1 = absent (older hosts).  Rides the heartbeat so LOADED-session
        /// clients converge too (the RainState pattern).</summary>
        public int TuneDrain  { get; set; } = -1;
        public int TuneRest   { get; set; } = -1;
        public int TuneMorale { get; set; } = -1;
        /// <summary>Round-283 FRESHNESS STAMP (additive; monotonic per host, per session).  The
        /// express lane deliberately breaks ordering BETWEEN lanes, so a clock packet can now
        /// overtake an older one that is still stuck behind bulk — and applying the older one after
        /// the newer would set AheadHeld (TimeSync.ReceiveClockSync's ahead branch) for a cycle on a
        /// client that is in fact perfectly aligned.  The receiver drops any packet whose Seq is
        /// &lt;= the last one it applied.  0 = an older host that does not stamp → always apply,
        /// which is byte-for-byte today's behaviour.</summary>
        public long Seq { get; set; }
        /// <summary>Round-284/F2: the host's pause INTENT riding the heartbeat — clients CONVERGE
        /// to it (TimeSync.ConvergePauseFromHeartbeat; the ManualPause edge messages remain the
        /// fast path, this is the recurrence-covered floor under a lost/misordered edge).
        /// 1 = paused, 2 = unpaused, 0 = absent (older host) — the receiver must NOT converge
        /// on 0 (the Seq==0 legacy-passthrough idiom above).</summary>
        public int PauseState { get; set; }
    }

    // ── Business sync (Phase 1: exterior business state) ──────────────────────

    /// <summary>
    /// Per-building business state.  Tier A fields (BusinessName, BusinessTypeName,
    /// TemporarilyClosed) are what shows up on the map and at any distance.
    /// Tier B fields (Description, Sign, Logo) are what you see from close up.
    /// In Phase 1 both tiers ride in the same struct since we don't cull by
    /// distance yet; if bandwidth becomes an issue we can split them later.
    /// </summary>
    public class BusinessInfo
    {
        /// <summary>"StreetNumber StreetName" e.g. "14 OakStreet".  Matches BuildingOwners keys.</summary>
        public string AddressKey         { get; set; } = "";

        // ── Tier A (always sync'd, always all clients) ────────────────────────
        public string BusinessName       { get; set; } = "";
        /// <summary>Business type id string (EA 0.11, e.g. "ba:businesstype_giftshop").</summary>
        public string BusinessTypeName   { get; set; } = "";
        /// <summary>H-ENTRY-1 (bundle 20260905-170233): the game's layout-template name for an AI-run business — null for
        /// player-run shops and empty buildings (the game nulls it when a business ends). Carried so a client's copy mirrors the
        /// host's: a stale name on the client made LoadBuilding look up a template that does not exist for the new type and abort
        /// to the street (325 stale names in one save). (The handshake is exact-version, so a peer without this field never connects; protocol 21 is unreleased.)</summary>
        public string? Layout            { get; set; }
        public bool   TemporarilyClosed  { get; set; }
        /// <summary>Owner-authoritative open/closed truth: 0 = unknown (AI shop / not provided),
        /// 1 = open, 2 = closed. The machine that RUNS the business (RentedByPlayer) computes the game's
        /// IsBusinessOpen and reports it; everyone else consumes it verbatim instead of re-deriving from a
        /// possibly-incomplete schedule replica (2026-06-19 "can't enter friend's shop" bug).</summary>
        public int    OwnerOpenState     { get; set; }

        // Rental marketplace state (Phase 1b).  Without these the client's
        // local AI economy can disagree with the host about which buildings
        // are rentable and at what price.
        public bool   AvailableForRent   { get; set; }
        public float  RentPerDay         { get; set; }
        public float  LastDeposit        { get; set; }

        // ── Tier B (close-up detail; full table sent on connect) ──────────────
        public string BusinessDescription { get; set; } = "";

        // Sign appearance.  SerializableColor is a packed int in the game; we
        // pass that through unchanged so we don't have to know the bit layout.
        public int SignType           { get; set; }
        public int SignLightPacked    { get; set; }
        public int LampPacked         { get; set; }

        // Logo
        public string LogoShape       { get; set; } = "";
        public int    LogoFont        { get; set; }
        public int LogoColorPacked    { get; set; }
        public int FontColorPacked    { get; set; }
        public int BackgroundColorPacked { get; set; }

        /// <summary>
        /// Files inside the player-business logo directory on the host's disk.
        /// `GetPlayerBusinessLogoPath(name)` returns a DIRECTORY containing
        /// per-size images (Billboard.jpg / SquareSign.jpg / WideSign.jpg).
        /// We ship all files in that directory so the client can reconstruct
        /// the full set.  Empty list for AI businesses (no on-disk files).
        /// </summary>
        public List<LogoFile> LogoFiles { get; set; } = new();

        // ── Operating hours (Phase 1c) ────────────────────────────────────────
        // Without these the client sees every business as "closed" because
        // CityGenerator suppression also skips default schedule population.
        // We mirror host's schedule verbatim.  (SharedSchedule was removed by
        // EA 0.11 — BuildingRegistration no longer has the field.)
        public List<ScheduleDayInfo> Schedule { get; set; } = new();

        // ── Ownership (Phase 1d; rent-vs-deed split 2026-07-07) ──────────────
        // The two RivalId strings drive BuildingResume.rivalBuildingOwner /
        // rivalBusinessOwner via RivalsHelper.GetRivalName. Native semantics
        // (decompile-verified): buildingOwnerRivalId = the DEED holder (the AI
        // landlord from worldgen, or whoever BOUGHT the building — rival pages
        // list "owned buildings" from it); businessOwnerRivalId = who RUNS the
        // business there (the tenant). The player attributions are SPLIT the
        // same way — conflating them was the 2026-07-07 community bug ("rented
        // a building, rival page shows him as its OWNER"):
        //   * OwnerPlayerId       = the player RENTING/OPERATING here (tenancy).
        //       receiver IS that player → reg.RentedByPlayer = true
        //       receiver is someone else → reg.businessOwnerRivalId = pid
        //   * DeedOwnerPlayerId   = the player who BOUGHT the building (deed).
        //       receiver is someone else → reg.buildingOwnerRivalId = pid
        //   * BuildingOwnerRivalId rides verbatim otherwise (the AI landlord —
        //     it must SURVIVE a player renting; worldgen never re-assigns it).
        public string BuildingOwnerRivalId { get; set; } = "";
        public string BusinessOwnerRivalId { get; set; } = "";
        public bool   RentedByPlayer       { get; set; }
        public string OwnerPlayerId        { get; set; } = "";
        public string BusinessOwnerPlayerId{ get; set; } = "";
        public string DeedOwnerPlayerId    { get; set; } = "";

        /// <summary>Round-204b: the HOST's computed takeover valuation for an AI-run
        /// business (CalculateAiOwnedValuation — needs dailyIncomes, which are
        /// host-simulated and never exist on clients). Clients show THIS number on the
        /// BizMan info panel instead of a local reconstruction that bottomed out at $0.
        /// Display + local pre-check only — the host re-runs the live math at offer
        /// time. 0 for non-AI businesses; additive field, absent = 0 on old peers.</summary>
        public float  AiValuation          { get; set; }

        /// <summary>AI-business retail prices (host-authoritative).  Clients
        /// suppress the daily rival sim, so without this their AI shops keep
        /// EMPTY price tables and buy at default market prices while the host's
        /// world runs competition-adjusted ones (first audit drill-down catch,
        /// 2026-06-12).  Session-player shops are NOT carried here — the live
        /// MPPriceSync channel owns those.</summary>
        public List<RetailPriceInfo> Prices { get; set; } = new();
    }

    /// <summary>One day of the week's opening schedule for one building.</summary>
    public class ScheduleDayInfo
    {
        /// <summary>DayOfWeekOrdered enum value (Monday=1..Sunday=7).</summary>
        public int Day    { get; set; }
        public bool IsOpen { get; set; }
        public List<OpeningHourSlotInfo> OpeningHourSlots { get; set; } = new();
        public List<WorkShiftInfo> WorkShifts { get; set; } = new();
    }

    /// <summary>One contiguous open-hours window within a day (e.g. 09:00-17:00).</summary>
    public class OpeningHourSlotInfo
    {
        public int StartingHour { get; set; }
        public int EndingHour   { get; set; }
    }

    /// <summary>One employee assignment inside a business schedule day.</summary>
    public class WorkShiftInfo
    {
        public string EmployeeId     { get; set; } = "";
        public string ItemInstanceId { get; set; } = "";
        public int    StartingHour   { get; set; }
        public int    EndingHour     { get; set; }
        public int    Type           { get; set; }
    }

    /// <summary>A single file from the player-business logo directory.</summary>
    public class LogoFile
    {
        /// <summary>Filename only, no path (e.g. "WideSign.jpg").</summary>
        public string Name        { get; set; } = "";
        /// <summary>Base64-encoded bytes.</summary>
        public string Base64      { get; set; } = "";
    }

    /// <summary>
    /// One entry in the host's "buy marketplace" (gi.buildingsForSale).  The
    /// game's RealEstateHelper.UpdateBuildingsForSale picks ~3 buildings per
    /// neighborhood each day to list for sale at randomized prices.  Different
    /// RNG between host and client → different listings → map-filter divergence.
    /// We sync the host's authoritative list and suppress the client's local
    /// generator (see MPPatches.Patch_RealEstateHelper_RunDaily_SkipOnClient).
    /// </summary>
    public class BuildingForSaleInfo
    {
        /// <summary>"StreetNumber StreetName" — same key shape as BusinessInfo.AddressKey.</summary>
        public string AddressKey      { get; set; } = "";
        public float  BuildingPrice   { get; set; }
        public int    SquareMeters    { get; set; }
        public float  AcceptOfferRate { get; set; }
    }

    /// <summary>Full table of exterior business state — sent once on connect.</summary>
    public class BusinessSnapshotPayload
    {
        public List<BusinessInfo> Businesses { get; set; } = new();

        /// <summary>Host's authoritative buy marketplace list.  Replaces client's local list verbatim.</summary>
        public List<BuildingForSaleInfo> BuildingsForSale { get; set; } = new();
    }

    /// <summary>One building changed — broadcast event-driven (rare).</summary>
    public class BusinessChangePayload
    {
        public BusinessInfo Info { get; set; } = new();
    }

    // ── Interior sync (Phase 2: building interior state) ─────────────────────

    /// <summary>
    /// Client → Host on building entry.  Host adds the sender to the building's
    /// subscriber set and replies with an InteriorSnapshot.  While subscribed,
    /// the client receives further InteriorSnapshots whenever host's polling
    /// detects state changes.
    /// </summary>
    public class InteriorRequestPayload
    {
        public string PlayerId   { get; set; } = "";
        public string AddressKey { get; set; } = "";
    }

    /// <summary>Client → Host on building exit.  Removes the client from that building's subscriber set.</summary>
    public class PlayerExitedBuildingPayload
    {
        public string PlayerId   { get; set; } = "";
        public string AddressKey { get; set; } = "";
    }

    /// <summary>
    /// One interior-design entry (one wall/floor/ceiling).  UUID identifies the
    /// surface in the building's design slot; materials carry the material+color
    /// for each surface.
    /// </summary>
    public class InteriorDesignInfo
    {
        public string UUID { get; set; } = "";
        public List<InteriorMaterialInfo> Materials { get; set; } = new();
    }

    public class InteriorMaterialInfo
    {
        public string MaterialID    { get; set; } = "";
        public int    MaterialIndex { get; set; }
        public int    ColorIndex    { get; set; }
    }

    /// <summary>Business Hub payloads (MessageTypes 105-108).</summary>
    public class LoanOfferPayload
    {
        public string Id            { get; set; } = "";
        public string From          { get; set; } = "";   // lender / gift sender
        public string To            { get; set; } = "";   // borrower / gift receiver
        public float  Principal     { get; set; }
        public float  DailyInterest { get; set; }
        public float  DailyPayment  { get; set; }
        /// <summary>"loan" or "gift" — gifts also require an accept (no silent
        /// handouts; acceptance doubles as the read receipt). "business"
        /// (round-196): a purchase offer for the target's business — From is the
        /// BUYER (pays Principal on accept, like a gift sender), To is the
        /// owner; AddressKey/BusinessName identify the shop. Acceptance runs
        /// the host-orchestrated transfer (BizTransfer* messages).</summary>
        public string Kind          { get; set; } = "loan";
        /// <summary>Kind=="business" only: the shop being offered for.</summary>
        public string AddressKey    { get; set; } = "";
        public string BusinessName  { get; set; } = "";
        /// <summary>Offer lifecycle: "offer" (new), "revoke" (offerer
        /// cancelled), "accepted"/"declined" (host → offerer: result, clears
        /// their outgoing list).</summary>
        public string State         { get; set; } = "offer";
    }

    /// <summary>Round-196: the execution legs of an accepted business sale. Money already moved
    /// via the hub accept (buyer paid, seller credited); this carries the WORLD transfer — the
    /// buyer claims via the native takeover, the seller releases tenancy, and the seller's staff
    /// ride along (user ruling: workers transfer; Staff is the roster wire format).</summary>
    /// <summary>Round-204b: host-arbitrated AI-business takeover (both directions —
    /// request carries AddressKey+OfferAmount; result carries Accepted+MinPrice).</summary>
    public class TakeoverPayload
    {
        public string AddressKey  { get; set; } = "";
        public float  OfferAmount { get; set; }
        public bool   Accepted    { get; set; }
        public float  MinPrice    { get; set; }
        // Round-204c: item count of the host-furnished interior — the buyer defers its
        // native claim until that many items have landed (or a deadline), so the claim
        // runs against the furnished registration, not the bare 2-marker one.
        public int    ItemCount   { get; set; }
        // Round-204e: the AI shop's pre-rolled employee data (reg.aiEmployees,
        // host-side only — never syncs). Native GenerateEmployees CONVERTS this list
        // into real staff and the client's copy is empty, so without it a client
        // takeover produced a shop with no workers. The buyer injects it before the
        // native claim so the game mints the staff on the OWNER's machine.
        public string AiEmployeesJson { get; set; } = "";
    }

    public class BizTransferPayload
    {
        public string OfferId      { get; set; } = "";
        public string AddressKey   { get; set; } = "";
        public string BusinessName { get; set; } = "";
        public string BuyerId      { get; set; } = "";
        public string SellerId     { get; set; } = "";
        public float  Amount       { get; set; }
        /// <summary>Item count in the host's copy of the shop — the buyer defers its
        /// claim until its own interior copy has materialized (a client-buyer that
        /// never visited the shop holds NOTHING until the sale's snapshot lands;
        /// rig-proven 2026-07-30: bought shop was completely empty).</summary>
        public int    ItemCount    { get; set; }
        public List<StaffInfo> Staff { get; set; } = new();
        /// <summary>The shop's work schedule (real staff only — synthetic duty
        /// stand-ins never travel). The buyer's local mirror does not reliably
        /// carry the seller's shifts, so the transfer ships them explicitly
        /// (rig 2026-07-30: workers arrived unscheduled without this).</summary>
        public List<ShiftInfo> Shifts { get; set; } = new();
    }

    public class ShiftInfo
    {
        public int    Day            { get; set; }   // DayOfWeekOrdered as int
        public string EmployeeId     { get; set; } = "";
        public string ItemInstanceId { get; set; } = "";
        public int    StartingHour   { get; set; }
        public int    EndingHour     { get; set; }
        public int    Type           { get; set; }   // WorkShiftType as int
        /// <summary>Station identity for cross-machine re-binding: interior item ids
        /// are PER-MACHINE (rig-proven 2026-07-30: none of the seller's station ids
        /// existed in the buyer's copy — schedule rendered empty), so the receiver
        /// re-resolves the station by item name + position.</summary>
        public string StationItemName { get; set; } = "";
        public float  StationX       { get; set; }
        public float  StationY       { get; set; }
        public float  StationZ       { get; set; }
    }

    public class LoanAnswerPayload
    {
        public string Id     { get; set; } = "";
        public string From   { get; set; } = "";   // the borrower answering
        public bool   Accept { get; set; }
    }

    public class LoanEntry
    {
        public string Id            { get; set; } = "";
        public string Lender        { get; set; } = "";
        public string Borrower      { get; set; } = "";
        public float  Remaining     { get; set; }
        public float  DailyInterest { get; set; }
        public float  DailyPayment  { get; set; }
    }

    public class LoanStatePayload
    {
        public List<LoanEntry> Loans { get; set; } = new();
    }

    /// <summary>Borrower → Host (MessageType.LoanRepay): pay a loan back early.</summary>
    public class LoanRepayPayload
    {
        public string Id     { get; set; } = "";   // loan id being repaid
        public string From   { get; set; } = "";   // the borrower repaying
        public float  Amount { get; set; }         // <= 0 = pay off in full; otherwise a partial amount
    }

    /// <summary>One lifecycle transition on a client (MessageType.PhaseReport).</summary>
    public class PhaseReportPayload
    {
        public string PlayerId { get; set; } = "";
        public string Phase    { get; set; } = "";
        /// <summary>Round-276 probe (additive; absent from older builds): the sender-side
        /// reason for this transition — for a Loading demotion, the full discriminator set
        /// (clock/overlay/excuse flags + staleness age) that field 20260818-215459 could
        /// not recover because peer logs were uncollectable through the congestion.</summary>
        public string Detail   { get; set; } = "";
        /// <summary>Round-283 FRESHNESS STAMP (additive; monotonic per sending client, per session).
        /// A phase report is a LATEST-WINS state report, and the express lane can now let a newer one
        /// overtake an older one still queued behind bulk.  Re-applying an older phase after a newer
        /// one is precisely the round-276 flap class (a stale 'Loading' landing after 'Running' makes
        /// the host believe a settled client demoted).  The host drops any report whose Seq is
        /// &lt;= the last one it accepted from that player.  0 = an older client that does not stamp
        /// → always apply, exactly today's behaviour.</summary>
        public long Seq { get; set; }
        /// <summary>Round-284 (additive): echo of the load ticket the sender was last SERVED
        /// (StartGamePayload/LoadDataPayload.LoadGen).  0 = older client, or not yet served —
        /// the host's round-276 latch path applies unchanged.  Non-zero lets the host fire the
        /// join baseline GEN-KEYED — no dependency on this report's arrival order vs
        /// PlayerInGame — which is what let phase reports onto the express lane (see
        /// MPClient.SendPhaseReport).</summary>
        public int LoadGen { get; set; }
    }

    /// <summary>A purchase by one player inside another player's shop
    /// (MessageType.RemoteSale).  The buyer already paid locally; the host
    /// validates and routes the revenue to the owner.</summary>
    public class RemoteSalePayload
    {
        public string BuyerId { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public string Address { get; set; } = "";
        public float  Total   { get; set; }
        public string Desc    { get; set; } = "";   // "CheapGift x3, ..." for notices/logs
        /// <summary>Structured order lines — drives the owner-side authoritative
        /// stock decrement (slice 2).  Desc stays for notices only.</summary>
        public List<SaleItem> Items { get; set; } = new();
    }

    /// <summary>One sold line item in a RemoteSale.</summary>
    public class SaleItem
    {
        public string ItemName { get; set; } = "";   // item name string (EA 0.11 moddable-item ids)
        public int    Amount   { get; set; }
    }

    /// <summary>Periodic cross-machine state audit (MessageType.AuditReport).
    /// Hashes are FNV-1a (stable across processes).  Fields that legitimately
    /// differ per machine (Money, VehicleCount) are REPORT-ONLY; the rest are
    /// compared by the host against its own state.</summary>
    public class AuditReportPayload
    {
        public string PlayerId  { get; set; } = "";
        public int    Day       { get; set; }
        public float  Hour      { get; set; }
        public float  Money     { get; set; }            // report-only
        public int    BizHash   { get; set; }            // business table (names/types/owners/prices)
        public int    BizCount  { get; set; }
        /// <summary>16 bucket sub-hashes of the business table (bucket =
        /// StableHash(addressKey) & 15) — lets the host localize WHICH
        /// registrations diverged instead of just "the table differs".</summary>
        public List<int> BizBuckets { get; set; } = new();
        public int    RosterHash    { get; set; }
        public int    VehicleCount  { get; set; }        // report-only (fleets are per-player)
        /// <summary>Hash of gi.marketEvents (host-authoritative, synced via
        /// MessageType.MarketEvents — convergence verified here).</summary>
        public int    MarketEventsHash { get; set; }
        public List<AddressHashInfo> Interiors { get; set; } = new();
        /// <summary>Round-104: the sender's OWN businesses and how many interior items each holds.
        /// The Interiors list above only covers interiors this machine RECEIVED, so a client's own
        /// shops — the data actually at risk — were never audited ("interiors 0/0 OK" while a
        /// client's four shops were being emptied, field 2026-07-27). One int per shop keeps the
        /// payload tiny; the host compares counts and logs only what diverges.</summary>
        public List<AddressCountInfo> OwnInteriors { get; set; } = new();
    }

    public class AddressHashInfo
    {
        public string AddressKey { get; set; } = "";
        public int    Hash       { get; set; }
        /// <summary>Round-278/F5 (additive; 0 from older builds): the structural half of the
        /// hash — no cargo amounts — so a mismatch line can say "till churn" vs "structural".</summary>
        public int    StructHash { get; set; }
    }

    public class AddressCountInfo
    {
        public string AddressKey { get; set; } = "";
        public int    Items      { get; set; }
    }

    /// <summary>Host → client: log your per-registration audit hashes for
    /// these biz buckets (offline log diff finds the diverging registration).</summary>
    public class AuditDrillPayload
    {
        public List<int> Buckets { get; set; } = new();
    }

    /// <summary>Round-89: the client's answer to an AuditDrill — its per-registration audit
    /// hash + a human summary for every reg in the diverged bucket(s). The host diffs these
    /// against its own registrations and logs the exact diverging address(es), so a single
    /// machine's field report names the culprit.</summary>
    public class AuditDrillReplyPayload
    {
        public string PlayerId { get; set; } = "";
        public List<DrillRegRow> Regs { get; set; } = new();
    }

    public class DrillRegRow
    {
        public string AddressKey { get; set; } = "";
        public int Hash { get; set; }
        public string Summary { get; set; } = "";
    }

    /// <summary>Host → all: gi.marketEvents serialized wholesale (plain data
    /// class — Newtonsoft round-trips it).  Authoritative on the host; clients
    /// replace their local list.</summary>
    public class MarketEventsPayload
    {
        public string Json { get; set; } = "";
    }

    /// <summary>Player on/off duty at a cash register (MessageType.RegisterCashier).</summary>
    public class RegisterCashierPayload
    {
        public string PlayerId { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public bool On { get; set; }
        /// <summary>Address key of the shop ("12 MainStreet") — receivers use it
        /// to find the BuildingRegistration for synthetic staffing (the duty
        /// position alone can't name the building from outside the interior).</summary>
        public string Address { get; set; } = "";
        /// <summary>ItemInstance id of the register being worked — read on the
        /// WORKER's machine (interior loaded there) and used by receivers as
        /// WorkShift.itemInstanceId.  Interior replication preserves instance
        /// ids, so the id matches on every machine.</summary>
        public string StationId { get; set; } = "";
        /// <summary>True when this duty is EMPLOYEE staffing (owner's hired
        /// staff per the schedule), not the owner personally working.  The
        /// distinction matters on receivers (user ruling 2026-06-12): personal
        /// duty = the owner's avatar is the visual, no NPC; employee duty =
        /// spawn a VISIBLE synthetic staff NPC for immersion.  Commerce runs
        /// the same self-checkout + RemoteSale path either way.</summary>
        public bool Employee { get; set; }
    }

    public class MoneyAdjustPayload
    {
        public string To     { get; set; } = "";
        public float  Amount { get; set; }
        public string Reason { get; set; } = "";
        /// <summary>No chat notice on apply (accepted-offer credits, daily
        /// loan drafts — the Hub list is their home, not the chat).</summary>
        public bool   Silent { get; set; }
    }

    /// <summary>One player's rest-vote (MessageType.RestVote).</summary>
    public class RestVotePayload
    {
        public string PlayerId    { get; set; } = "";
        public bool   Active      { get; set; }
        /// <summary>Goal as total game-minutes (day*1440 + hour*60 + min).</summary>
        public double GoalMinutes { get; set; }
        /// <summary>What the player is doing ("Sleep", "Rest", "Workout"...).</summary>
        public string Activity    { get; set; } = "";
    }

    public class RestVoteEntry
    {
        public string PlayerId    { get; set; } = "";
        public double GoalMinutes { get; set; }
        public string Activity    { get; set; } = "";
    }

    /// <summary>Host → all: consensus state (MessageType.RestSkipState).</summary>
    public class RestSkipStatePayload
    {
        public List<RestVoteEntry> Votes { get; set; } = new();
        public int    Required    { get; set; }
        public bool   SkipActive  { get; set; }
        public double GoalMinutes { get; set; }   // absolute total-game-minutes target, so clients fast-run to it
    }

    /// <summary>Live retail prices for one business (MessageType.RetailPrices).
    /// Sent by the machine that RUNS the business whenever its prices change;
    /// receivers write them into their local registration copy so the game's
    /// per-neighbourhood price competition reads current numbers.</summary>
    public class RetailPricesPayload
    {
        public string AddressKey { get; set; } = "";
        public string OwnerId    { get; set; } = "";
        public List<RetailPriceInfo> Prices { get; set; } = new();
    }

    public class RetailPriceInfo
    {
        /// <summary>Item name string (EA 0.11 replaced the ItemName enum with strings).</summary>
        public string ItemName { get; set; } = "";
        public float  Price    { get; set; }
    }

    /// <summary>A single dirt spot on the floor.</summary>
    public class DirtSpotInfo
    {
        public int   X         { get; set; }
        public int   Z         { get; set; }
        public float Dirtiness { get; set; }
    }

    /// <summary>One floor cell a helper mopped (MessageType.BuildingDirtEdit).  Carries the INDEX into
    /// BuildingRegistration.dirtSpots — which is how the game itself addresses cells while mopping
    /// (MopController.FloorCellClick writes dirtSpots[DirtSpotObject.DirtSpot].dirtiness) — plus X/Z so the
    /// receiver can verify the index really is the same cell before trusting it.  The lattice is built by
    /// walking the building's Floors transforms, so the order matches across machines; the X/Z check is the
    /// cheap guard against that assumption ever breaking.</summary>
    public class DirtSpotDeltaInfo
    {
        public int   Index     { get; set; }
        public int   X         { get; set; }
        public int   Z         { get; set; }
        public float Dirtiness { get; set; }
    }

    /// <summary>Helper → Host → Owner: the floor cells this player just cleaned in a business they hold a
    /// grant for.  Deliberately NOT a whole interior snapshot: a broad forward would carry every other bit
    /// of the helper's local interior replica along with the dirt, which is exactly the class of overwrite
    /// that has cost us shop contents before.</summary>
    public class DirtEditPayload
    {
        public string AddressKey { get; set; } = "";
        public string SenderId   { get; set; } = "";
        public List<DirtSpotDeltaInfo> Spots { get; set; } = new();
    }

    /// <summary>
    /// Host → Client: full interior state for one building.  Phase 2a carries
    /// Layout/designs/prices/dirt.  Phase 2b adds ItemInstances (shelves,
    /// products, furniture).
    /// </summary>
    public class InteriorSnapshotPayload
    {
        public string                     AddressKey      { get; set; } = "";
        public string                     Layout          { get; set; } = "";
        public string                     OwnerPlayerId   { get; set; } = "";
        public bool                       ItemInstancesAuthoritative { get; set; } = true;
        // Whole-snapshot authority (2026-06-17): false ONLY when the host built this from its own replica of
        // a PLAYER-owned business (a possibly blank/stale copy). The receiver must never let a non-authoritative
        // snapshot clear a player business's interior. Default true = owner push / AI / world (apply normally).
        public bool                       Authoritative   { get; set; } = true;
        public List<InteriorDesignInfo>   InteriorDesigns { get; set; } = new();
        // Round-227: speaker radio rides the snapshot so entering players + late
        // joiners inherit it. -1 / -999 = absent (old-format peers).
        public int                        RadioStation    { get; set; } = -1;
        public float                      RadioVolume     { get; set; } = -999f;
        public List<RetailPriceInfo>      RetailPrices    { get; set; } = new();
        public List<DirtSpotInfo>         DirtSpots       { get; set; } = new();
        public List<ItemInstanceInfo>     ItemInstances   { get; set; } = new();
        // Round-39d (Phase 3 customer presence): the OWNER's authoritative shopper schedule for this
        // player business. NPC customers are machine-local — IndoorCustomerSpawner spawns them from
        // CustomerEntriesHelper's per-address entry list, which only ever gets player-business entries
        // on the owner's machine (UpdateCustomerEntriesForPlayerBusiness gates on RentedByPlayer), so
        // guests saw EMPTY shops. Owner fills this on push; guests seed their local entry table from it
        // and the game's own spawner does the rest.
        public List<CustomerEntryInfo>    CustomerEntries { get; set; } = new();
        // Round-39e (complaint parity): the shop's fulfilled-demand set (reg.cachedFulfilledCustomerDemands,
        // computed owner-side from amenities) — customers complain about demands NOT in this set, so a
        // guest without it would complain about everything.
        public List<string>               FulfilledDemands { get; set; } = new();
        // Round-281: which STRUCTURE this snapshot describes.  The host mints one number per address
        // per distinct structure hash (InteriorSync.ComputeHashes' `structure` half — layout, designs,
        // prices, the item set with poses/aliases/config) and stamps it on every full send.  The cheap
        // cargo message (InteriorCargoSync) carries the same number, so a receiver can tell in O(1)
        // whether the cargo it just got belongs to the structure it actually holds.  ADDITIVE: an older
        // host leaves this 0, the receiver records 0, and — since that host also never sends cargo
        // syncs — nothing downstream ever reads it.
        public int                        StructVersion   { get; set; }
        // v12 (interior-edit Stage 0): TRUE marks recovery traffic — entry serves, sale/takeover/
        // arbitration deliveries, round-184 heals, and answers to a re-request — which a receiver
        // must apply even while its local player is mid-edit (discarding a recovery answer can
        // loop; the per-item hands/drag protections still apply inside).  FALSE marks routine
        // traffic, which a mid-edit receiver discards and re-asks for when the edit ends ("a
        // deferral must never be stale" — the answer is rebuilt live at send time).  PER-HOP:
        // consumed at receipt, never cached or relayed onward as true.
        public bool                       SeedOrHeal      { get; set; }
    }

    /// <summary>v13 (interior-edit Stage 1b): one item operation inside a BuildingInteriorDelta.
    /// "upsert" carries the item's FULL absolute state (never a field diff — idempotent, orderless,
    /// round-281's rule scoped to one id); "remove" carries the id alone.</summary>
    public class InteriorItemOp
    {
        public string            Kind { get; set; } = "";   // "upsert" | "remove"
        public string            Id   { get; set; } = "";
        public ItemInstanceInfo? Item { get; set; }         // full absolute state on upsert; null on remove
    }

    /// <summary>v13 (interior-edit Stage 1b) — MessageType.BuildingInteriorDelta: a permitted edit as
    /// the operations it made, replacing the whole-replica 140 for placement/removal forwards.
    /// Bands are ABSENT by design (M2 fix): no RetailPrices, DirtSpots, CustomerEntries or
    /// FulfilledDemands — each has its own channel or is owner-only. Designs (update-only by UUID)
    /// and Layout/Radio ride only from Stage 2's designer-close conversion; in Stage 1b they are
    /// always empty/sentinel and a receiver logs if one arrives filled. PlaythroughId is the world
    /// identity (same rule as InteriorCargoSync): an address collision from another lineage must not
    /// mutate this world.</summary>
    public class InteriorEditDeltaPayload
    {
        public string                   AddressKey    { get; set; } = "";
        public string                   PlaythroughId { get; set; } = "";
        public string                   SenderId      { get; set; } = "";
        public List<InteriorItemOp>     Ops           { get; set; } = new();
        public List<InteriorDesignInfo> Designs       { get; set; } = new();   // UPDATE-ONLY by UUID (Stage 2)
        public string                   Layout        { get; set; } = "";      // empty = no opinion
        public int                      RadioStation  { get; set; } = -1;      // -1 = absent
        public float                    RadioVolume   { get; set; } = -999f;   // -999 = absent
        /// <summary>Review MAJOR-L: set ONLY on the Host→subscriber relay leg — the host mints the
        /// post-adopt structure version and the relay carries it, so receivers stay cargo-sync
        /// coherent without a full re-serve. 0 everywhere else (editor→Host, Host→owner forward,
        /// the no-graft fallback); a receiver records it only when > 0 — trap 2's rule ("never
        /// record a version the host's stamped stream did not state") kept intact, because this
        /// leg IS the stamped stream.</summary>
        public int                      StructVersion { get; set; }
        /// <summary>v14 (Stage 2): TRUE on a DESIGNER-CLOSE delta, where a big remove set can be
        /// legitimate (the session's accumulated edits; the sell/pack tools mostly pre-convey via
        /// the per-action removal forwards, but a session can still tear down plenty). Receivers
        /// apply the 500-remove sanity cap instead of the 25-op action cap. FALSE on the 1-3-op
        /// placement/removal forwards, where >25 removes still means a corrupt diff.</summary>
        public bool                     BulkEdit      { get; set; }
    }

    /// <summary>Round-281 — the cheap half of interior sync (MessageType.InteriorCargoSync).
    /// One building's CURRENT cargo, absolute: `Items` names EVERY item in the building with the cargo
    /// it holds right now (an emptied shelf appears with an empty list — that is what makes the message
    /// idempotent rather than a diff-chain, and what lets an emptied shelf converge without waiting for
    /// a structural change to force a full snapshot).  It deliberately carries NO structure: the
    /// receiver matches StructVersion against the last FULL snapshot it applied and, on a mismatch,
    /// throws the cargo away and re-requests the interior instead of guessing.
    /// PlaythroughId is the world identity (same field as StoreMirror/LoadData) — a cargo write is a
    /// save-state write, and a name-collided address from a different lineage must not receive one.</summary>
    public class InteriorCargoSyncPayload
    {
        public string AddressKey    { get; set; } = "";
        public string PlaythroughId { get; set; } = "";
        public int    StructVersion { get; set; }
        /// <summary>v10 (T7): set ONLY on the owner → Host direction — the sender's structure
        /// hash (ComputeHashes' `structure` band) of the interior this cargo belongs to. The
        /// host grafts the cargo onto its cached owner snapshot ONLY when the cache's structure
        /// hash matches exactly; a mismatch means the cache and the owner have diverged, and the
        /// host asks for a full push instead (InteriorRequest, host → owner). 0 = host-minted
        /// (the original Host → subscriber direction, StructVersion-guarded as before); a
        /// computed hash that lands on 0 is remapped to 1 by the sender (review m2 — 0 must
        /// stay unambiguous or a legitimate hash collision livelocks on full re-pushes).</summary>
        public int    OwnerStructHash { get; set; }
        /// <summary>v10 review M3: the sender's `structAndNonCargo` band, checked alongside
        /// OwnerStructHash. Structure alone misses non-cargo volatile state (item StateIndex):
        /// if an owner's FULL push was refused (ownership handover window, sanity gates), the
        /// owner's baselines still advanced — a later cargo graft onto the stale cache would
        /// freeze the stale item state in what the host serves. hn mismatch → full re-push
        /// heals instead. Same 0→1 remap as OwnerStructHash.</summary>
        public int    OwnerNonCargoHash { get; set; }
        public List<InteriorCargoItemInfo> Items { get; set; } = new();
    }

    /// <summary>v10 (T7, ruling 33): one building's dirt values — see MessageType.InteriorDirtSync.
    /// Review B2: entries carry the lattice INDEX (DirtSpotDeltaInfo), NOT bare X/Z — the game
    /// stacks storeys and discards Y when building the lattice, so (X, Z) is NOT unique in a
    /// multi-floor building and a coordinate-keyed write lands on every storey at once. Index is
    /// how the game itself addresses cells (MopController); X/Z ride along as the verification.</summary>
    public class InteriorDirtSyncPayload
    {
        public string AddressKey    { get; set; } = "";
        public string PlaythroughId { get; set; } = "";
        /// <summary>Every lattice spot with dirtiness > 0. Absolute: unlisted spots read clean.</summary>
        public List<DirtSpotDeltaInfo> Spots { get; set; } = new();
    }

    /// <summary>One item's cargo on the cargo-sync wire.  Reuses the snapshot's own CargoInstanceInfo
    /// verbatim (same serializer, same receiver-side builder) so the two channels can never drift into
    /// describing cargo differently.</summary>
    public class InteriorCargoItemInfo
    {
        public string Id { get; set; } = "";
        public List<CargoInstanceInfo> CargoInstances { get; set; } = new();
    }

    public class CustomerEntryInfo
    {
        // Round-39f: stable identity minted by the OWNER at first capture — the claim key for order
        // forwarding (spawn times MUTATE: TrySpawnCustomer rewrites spawnTime/timestamp on late spawns,
        // so time-based keys break across machines).
        public string EntryId    { get; set; } = "";
        public int   SpawnDay    { get; set; }
        public int   SpawnHour   { get; set; }
        public float SpawnMinute { get; set; }
        public bool  Completed   { get; set; }
        public List<OrderEntryInfo> Items { get; set; } = new();
        // Round-39e (complaint parity): the customer's demand types (Order.customerDemandTypes) — a
        // just-arrived customer complains about each demand the shop doesn't fulfill.
        public List<string> Demands { get; set; } = new();
    }

    public class OrderEntryInfo
    {
        public string ItemName       { get; set; } = "";
        public float  Price          { get; set; }
        public float  WholesalePrice { get; set; }
        // Recheck B1 (2026-09-01): the helper's machine never ran the native price-acceptability
        // check (it lives behind the owner gate), so every forwarded item was booked at any price.
        // The helper now answers it with the live customer's own tolerance; the owner refuses
        // (does not deduct or book) items marked false — native's returned-item outcome.
        public bool   Acceptable     { get; set; } = true;
    }

    /// <summary>Round-39f (Phase 3 slice-2 step-2) — a customer order PAID on a helper's machine,
    /// forwarded to the building owner to become real: the owner claims the source entry (single-writer
    /// dedup — its own entry table is the ledger), deducts the sold items + a paper bag from real stock,
    /// and records the order in reg.unprocessedCompletedOrders, where the game's own hourly calculator
    /// subtracts it from the simulated quota (native anti-double-count).</summary>
    public class HelperOrderPayload
    {
        public string AddressKey { get; set; } = "";
        public string PlayerId   { get; set; } = "";   // the helper who hosted the sale
        public string EntryId    { get; set; } = "";   // the synced schedule entry this customer came from
        public List<OrderEntryInfo> Items { get; set; } = new();   // PAID entries only
    }

    // ── Item instance DTOs (Phase 2b) ────────────────────────────────────────
    // Mirror of BigAmbitions.Items.ItemInstance and its nested types.  Active
    // fields only; the 11 [Obsolete] fields on ItemInstance are skipped.
    // Item/street names are strings (EA 0.11 replaced those enums with strings);
    // SerializableVector3/Quaternion are inlined as flat floats; SerializableColor
    // uses the packed-int pattern from Phase 1.

    public class ItemInstanceInfo
    {
        public string Id                { get; set; } = "";
        public string ItemName          { get; set; } = "";
        public float  Px { get; set; }  public float Py { get; set; }  public float Pz { get; set; }
        public float  Qx { get; set; }  public float Qy { get; set; }  public float Qz { get; set; }  public float Qw { get; set; }
        public float  YRotation         { get; set; }
        public string ParentId          { get; set; } = "";
        public string StreetName        { get; set; } = "";
        public int    StreetNumber      { get; set; }
        public string LinkedItemName    { get; set; } = "";
        public bool   IsSecured         { get; set; }
        public string WorldSpaceTextValue { get; set; } = "";
        public int    StateIndex        { get; set; }
        public string Alias             { get; set; } = "";
        public string CustomValue       { get; set; } = "";
        public float  PriceOnPurchase   { get; set; }
        public List<AttachableChildInfo>      StackedItems    { get; set; } = new();
        public List<CargoInstanceInfo>        CargoInstances  { get; set; } = new();
        public List<int>                      DirtSpotsThatAffects { get; set; } = new();
        public List<Vector3Info>              CustomPositions { get; set; } = new();
        public List<CustomColorInfo>          CustomColors    { get; set; } = new();
        public PlayerItemPurchaserSettingsInfo? PurchaserSettings { get; set; }
        // Task-28 fix 1: FactoryWorkstationInstance is an ItemInstance SUBCLASS whose five
        // config fields were silently dropped (the receiver rebuilt a base ItemInstance —
        // recipe/priority/limits lost, BizMan Factory casts fail).  WorkstationType is the
        // discriminator: non-null ⇔ the sender's instance was a FactoryWorkstationInstance.
        public string? WorkstationType   { get; set; }
        public string  SelectedRecipeId  { get; set; } = "";
        public int     WsPriority        { get; set; }
        public bool    ProduceUpTo       { get; set; }
        public int     ProduceUpToValue  { get; set; }
    }

    public class AttachableChildInfo
    {
        public string ChildId          { get; set; } = "";
        public string ChildItemName    { get; set; } = "";
        public int    AttachmentIndex  { get; set; }
    }

    public class CargoInstanceInfo
    {
        public string ItemName     { get; set; } = "";
        public int    Amount       { get; set; }
        public float  PricePerUnit { get; set; }
        public bool   Paid         { get; set; }
        public List<CustomColorInfo>          CustomColors         { get; set; } = new();
        public List<NestedCargoInstanceInfo>  NestedCargoInstances { get; set; } = new();
    }

    public class NestedCargoInstanceInfo
    {
        public string ItemName     { get; set; } = "";
        public int    Amount       { get; set; }
        public float  PricePerUnit { get; set; }
        public List<CustomColorInfo> CustomColors { get; set; } = new();
    }

    public class CustomColorInfo
    {
        public int Channel       { get; set; }   // CustomColorChannel enum
        public int ColorPacked   { get; set; }   // SerializableColor.color
    }

    public class PlayerItemPurchaserSettingsInfo
    {
        public string Name         { get; set; } = "";
        public bool   Enabled      { get; set; }
        public string ItemName     { get; set; } = "";
        public int    ItemQuantity { get; set; }
    }

    public class Vector3Info
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    // ── Rivals roster sync (Phase 1d Wave 2) ─────────────────────────────────

    /// <summary>One entry in the host's AI rival roster.</summary>
    public class RivalInfo
    {
        /// <summary>The base64 GUID the host uses as buildingOwnerRivalId / businessOwnerRivalId.</summary>
        public string Id   { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>True if this entry represents a human player (not an AI rival).</summary>
        public bool   IsPlayer { get; set; }
    }

    /// <summary>
    /// Host → Client on connect.  Replaces client's local RivalDataCache so
    /// id→name lookups resolve consistently across the session.  Client's
    /// own RivalsHelper.GenerateRivals is suppressed via Harmony patch so the
    /// host's roster is authoritative.
    /// </summary>
    public class RivalsSnapshotPayload
    {
        public List<RivalInfo> Rivals { get; set; } = new();

        /// <summary>Round-257: the host's gi.wholesaleRivalIds / importRivalIds VERBATIM,
        /// in slot order. The client's GenerateRivals mints exactly these (wholesale block
        /// first, then import) — feeding the queue from these arrays gives exact-slot
        /// alignment regardless of rivalStates list order (which top-ups can pollute).
        /// Specials never mint and must never enter the queue (the wave-6 shift bug:
        /// 4 duplicated identities + the last 4 ids never landing on the client).</summary>
        public List<string> WholesaleIds { get; set; } = new();
        public List<string> ImportIds    { get; set; } = new();
    }

    /// <summary>
    /// Client → Host: triggers a fresh stats snapshot AND attaches the
    /// client's own self-stats so host has data to populate the client's row
    /// on host's own leaderboard.  Self-stats are computed locally from the
    /// client's gi.realEstate / RentedByPlayer state.
    /// </summary>
    /// <summary>One point of a per-day history series (float).</summary>
    public class HistoryPointF { public int Day { get; set; } public float Value { get; set; } }
    /// <summary>One point of a per-day history series (int).</summary>
    public class HistoryPointI { public int Day { get; set; } public int   Value { get; set; } }

    public class RivalsStatsRequestPayload
    {
        public string PlayerId { get; set; } = "";
        public int    SelfOwnedBuildingsCount  { get; set; }
        public int    SelfOwnedBusinessesCount { get; set; }
        public float  SelfWeeklyIncome         { get; set; }
        /// <summary>Primary neighborhood, computed the native self-sheet way (round-25 parity).</summary>
        public string SelfNeighborhood         { get; set; } = "";
        /// <summary>Per-business breakdown (AddressKey + WeeklyIncome) — feeds
        /// the host's fair-rival patches: the host's replicas have no order
        /// history, so "is this player business succeeding" reads this.</summary>
        public List<RivalBusinessInfo> Businesses { get; set; } = new();
        /// <summary>The game's own per-day series for this player
        /// (gi.playerWeeklyIncomeHistory / playerNumberOfBusinessesHistory) —
        /// drives the REAL detail-view graphs on other machines.</summary>
        public List<HistoryPointF> IncomeHistory   { get; set; } = new();
        public List<HistoryPointI> BizCountHistory { get; set; } = new();
    }

    /// <summary>
    /// Player profile update — carries a player's in-character name (the one
    /// chosen in the character creator, stored in CharacterData.name).  Used
    /// as the canonical display name for the player in rival lists, building
    /// ownership popups, leaderboard, etc.  PlayerId is the internal/network
    /// key (stable, from F8 menu / Steam); CharacterName is what humans see.
    /// </summary>
    public class PlayerProfilePayload
    {
        public string PlayerId      { get; set; } = "";
        public string CharacterName { get; set; } = "";
        /// <summary>Base64 of the player's rendered portrait image (from
        /// PortraitGenerator.GetCharacterPortraitPath).  Relayed so other
        /// players see this player's ACTUAL face in the rivals profile, rather
        /// than a generated default.  May be empty if not yet on disk (it's
        /// written lazily) — the profile is re-sent once it appears.</summary>
        public string PortraitPngBase64 { get; set; } = "";
        /// <summary>Player's character age in years (charactersData[0].ageInDays
        /// / gameVariables.daysPerYear) so the rivals profile shows the real age
        /// instead of a default.</summary>
        public int AgeInYears { get; set; }
        /// <summary>Character gender (BigAmbitions.Characters.Gender as int;
        /// -1 = unknown).  Fallback-portrait fidelity: if the portrait PNG
        /// hasn't arrived yet, the game GENERATES a face — without this it
        /// always generated a default-gender one.</summary>
        public int Gender { get; set; } = -1;
    }

    /// <summary>Host → All: trigger a coordinated MP save.  Every player saves
    /// their own .hsg into the named MP session folder, then reports back.</summary>
    public class SaveNowPayload
    {
        public string SessionName { get; set; } = "";
        /// <summary>Why the save fired — for logging only ("manual"/"autosave"/"disconnect").</summary>
        public string Reason      { get; set; } = "";
        /// <summary>Round-217 (store v2): WHICH world this session name belongs to, so a
        /// client told to save under a name it has never seen files it correctly — the
        /// order and the identity travel together (decision F: no guessing).</summary>
        public string PlaythroughId { get; set; } = "";
    }

    /// <summary>One chat line.  Clients send it to the host; the host relays it to
    /// every player (including the original sender) so each player's chat log is
    /// identical and ordered by the host.</summary>
    public class ChatPayload
    {
        /// <summary>Display name of the sender (PlayerId / character name).</summary>
        public string PlayerId { get; set; } = "";
        public string Text     { get; set; } = "";
        /// <summary>Recipient player id for a PRIVATE message; "" = everyone.
        /// Private messages are delivered only to this player (host-relayed).</summary>
        public string To       { get; set; } = "";
    }

    /// <summary>Client → Host: the user pressed Save (or Save-and-Exit) in the
    /// in-game pause menu.  The host responds by running a coordinated save
    /// (HostSaveNow), which broadcasts SaveNow back so every player — including
    /// the requester — saves and uploads.  This keeps the host's session name
    /// canonical rather than letting a client guess it.</summary>
    public class RequestSavePayload
    {
        /// <summary>For logging only ("client-menu"/"client-menu-exit").</summary>
        public string Reason  { get; set; } = "";
        /// <summary>True if the requester is about to quit (clean-leave); the host
        /// logs it and the requester also best-effort ships its own save inline.</summary>
        public bool   Exiting { get; set; }
        /// <summary>Optional name the requester typed in the save box — the host
        /// uses it as the session name so the save is identifiable + overwritable.</summary>
        public string SaveName { get; set; } = "";
    }

    /// <summary>Client → Host: the client's full saved game, so the host holds
    /// the canonical copy (centralized persistence).  The .hsg is gzipped then
    /// Base64'd to ride inside the JSON envelope; a ~450 KB save compresses to a
    /// fraction of that.  The host writes it into its own MP session folder and
    /// folds the slot into the manifest.</summary>
    public class SaveDataPayload
    {
        /// <summary>Round-222 (store v2): WHICH world this upload belongs to — a save
        /// arriving after the host's session context is gone (late/teardown uploads)
        /// must still file into the right playthrough folder.</summary>
        public string PlaythroughId { get; set; } = "";
        public string SessionName    { get; set; } = "";
        public bool   Success        { get; set; }
        public MpSlot Slot           { get; set; } = new();
        public string HsgGzipBase64  { get; set; } = "";   // gzip(.hsg bytes) → base64 (legacy channel; v9 senders use the attachment rider)
        public int    RawLength       { get; set; }          // uncompressed length, for sanity check
        /// <summary>Round-275: the .hsg.meta sidecar JSON.  The game's save scanner cannot
        /// date a .hsg without it — host-stored copies lacked one, so day validation read
        /// -1/0 and the disconnect-save restore never committed on a real world.</summary>
        public string MetaJson       { get; set; } = "";
        /// <summary>v9: gzip(.hsg) as raw bytes, carried by the envelope's attachment frame
        /// (no base64). Set at the dispatch seam from env.Attachment; never in the JSON.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public byte[]? HsgRaw        { get; set; }
        public bool    HasHsgFile()  => (HsgRaw?.Length ?? 0) > 0 || !string.IsNullOrEmpty(HsgGzipBase64);
        public byte[]? GetHsgGzip()  => (HsgRaw?.Length ?? 0) > 0 ? HsgRaw
                                        : string.IsNullOrEmpty(HsgGzipBase64) ? null : System.Convert.FromBase64String(HsgGzipBase64);
    }

    /// <summary>Client → Host: this player's current money.  Sent periodically so
    /// the host always has a near-current cash figure (cash is the one private
    /// scalar worth losing the least).  On reconnect the host reapplies it over
    /// the loaded save, so a crash costs at most a few seconds of earnings.</summary>
    public class CashSyncPayload
    {
        public string PlayerId { get; set; } = "";
        public float  Money    { get; set; }
    }

    /// <summary>Round-290 (field 20260820-092547 "players can't see each other's ads"): the
    /// sender's currently-ACTIVE billboard campaigns as an absolute set — re-applying always
    /// converges, never a diff-chain. Entry format "type|businessName" where type =
    /// (int)Entities.MarketingTypeName (3/4/5 = Small/Medium/LargeBillboard). Plain strings
    /// only (§10: never Newtonsoft a game type).</summary>
    public class BillboardAdsPayload
    {
        public string PlayerId { get; set; } = "";
        public List<string> Entries { get; set; } = new();
    }

    /// <summary>Host → Client: the client's own stored .hsg for an MP session, so
    /// it can load (or reconnect into) the session.  The .hsg lives on the host
    /// (centralized persistence); this ships it back.  Money is the host's most
    /// current known cash for this player, overlaid after the load completes.</summary>
    /// <summary>Round-227: one building's speaker radio state. Volume is SIGNED —
    /// negative means muted/off (the native convention).</summary>
    public class RadioStatePayload
    {
        public string AddressKey { get; set; } = "";
        public int    Station    { get; set; } = -1;
        public float  Volume     { get; set; }
    }

    /// <summary>Host → All (v9, T6): the buy marketplace list alone — the daily for-sale
    /// refresh used to ride a full ~826-building BusinessSnapshot broadcast. Receivers
    /// apply it with the same replace-the-list routine the snapshot path uses.</summary>
    public class BuildingsForSalePayload
    {
        public List<BuildingForSaleInfo> BuildingsForSale { get; set; } = new();
    }

    /// <summary>Client → Host (v9): delivery confirmation for one mirrored .hsg —
    /// SessionName/StableId name the FILE (the mirror payload's own fields, echoed
    /// verbatim), Sig is FNV-1a over the gzip bytes as received. See MessageType.MirrorAck.</summary>
    public class MirrorAckPayload
    {
        public string SessionName { get; set; } = "";
        public string StableId    { get; set; } = "";
        public long   Sig         { get; set; }
    }

    public class LoadDataPayload
    {
        /// <summary>Round-224: false = the host has NO trustworthy cash figure for this
        /// player (placeholder slot + never streamed) — the client must keep the wallet
        /// inside its .hsg instead of applying a fake $0 overlay.</summary>
        public bool MoneyKnown { get; set; } = true;
        public string SessionName    { get; set; } = "";
        public string HsgGzipBase64  { get; set; } = "";
        public int    RawLength      { get; set; }
        /// <summary>Round-275b: the served save's .hsg.meta sidecar — without it the
        /// CLIENT-side copy is undatable (its local catalog read day 0; round-262 fired).</summary>
        public string MetaJson       { get; set; } = "";
        public float  Money          { get; set; }
        // Host → client (Proposal 2, 2026-06-17): "you HAVE a saved character in this session, but I can't read
        // its .hsg right now (missing / locked / corrupt)." The client must NOT fresh-start on this — that would
        // abandon the real save (and, once it re-saves, destroy any chance of recovery). Set only when a manifest
        // slot exists but ReadSaveBytesGzip returned null. A brand-new player (no slot) still gets a normal
        // empty-hsg fresh-start.
        public bool   SaveUnavailable { get; set; } = false;
        /// <summary>Mid-join fallback chain: when NO save file is attached (HasHsgFile()
        /// false — neither the v9 attachment rider nor the legacy base64 field) the
        /// client loads its own LOCAL session save if present, else starts a
        /// fresh character with these host settings (null → Normal preset).</summary>
        public GameVariablesDto? FallbackSettings { get; set; }
        /// <summary>Phase 3: host → client "hold — upload your pending disconnect save first
        /// (ClientDisconnectUpload); I'll validate its actual day and send your real load after." The client
        /// must NOT load on this message; it uploads and waits for the follow-up LoadData.</summary>
        public bool   AwaitClientDisconnectUpload { get; set; } = false;
        /// <summary>Handoff slice 4: the session's identity/day/epoch (from the host's
        /// manifest), sent with a real .hsg. LOG-ONLY diagnostics on the joiner (user
        /// 2026-07-23: loading an older save is standard MP behavior — no warnings, it
        /// just loads): a rolled-back join logs the day delta; a joiner whose store
        /// recorded a LATER host-start logs a fork-suspect line naming the newer host.
        /// 0/empty = pre-field host (silent).</summary>
        public int    WorldDay      { get; set; }
        public string PlaythroughId { get; set; } = "";
        public int    HostEpoch     { get; set; }
        /// <summary>Round-284 "load ticket" — see StartGamePayload.LoadGen.  Stamped only on
        /// serves that lead to a LOAD (real .hsg or fresh-character fallback); the no-load
        /// branches (SaveUnavailable / AwaitClientDisconnectUpload) stay 0.</summary>
        public int    LoadGen       { get; set; }
        /// <summary>v9: gzip(.hsg) as raw bytes via the envelope attachment frame (no base64).
        /// The "no save → fallback chain" checks MUST treat a non-empty rider as a real save.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public byte[]? HsgRaw       { get; set; }
        public bool    HasHsgFile() => (HsgRaw?.Length ?? 0) > 0 || !string.IsNullOrEmpty(HsgGzipBase64);
        public byte[]? GetHsgGzip() => (HsgRaw?.Length ?? 0) > 0 ? HsgRaw
                                       : string.IsNullOrEmpty(HsgGzipBase64) ? null : System.Convert.FromBase64String(HsgGzipBase64);
    }

    /// <summary>Host → clients (handoff slice 1, 2026-07-23): one piece of the session STORE —
    /// a member's saved .hsg (gzipped) and/or the session manifest. Sent at every coordinated
    /// save so EVERY member holds the complete "single save" (manifest + all members' .hsg)
    /// and can host the world later with full fidelity. Never sent to the member the .hsg
    /// belongs to (their local copy is written by their own save). StableId empty =
    /// manifest-only. HostStoreToken is a hash of the host's physical store folder: a
    /// receiver whose own token matches shares the SAME folder (dual-instance on one
    /// machine) and must not apply — the files are already there, and writing them would
    /// race the host's own writes.</summary>
    public class StoreMirrorPayload
    {
        public string SessionName    { get; set; } = "";
        public string StableId       { get; set; } = "";   // whose .hsg ("" = manifest-only)
        public string SaveName       { get; set; } = "";   // .hsg file name, no extension
        public string HsgGzipBase64  { get; set; } = "";
        public int    RawLength      { get; set; }
        public string MetaJson       { get; set; } = "";   // round-275b: .hsg.meta sidecar rides the mirror
        public string ManifestJson   { get; set; } = "";   // full session manifest (small)
        public string HostStoreToken { get; set; } = "";
        /// <summary>World identity of the mirrored session (review fix 2026-07-23): the
        /// receiver refuses the piece when a SAME-NAMED local session belongs to a
        /// DIFFERENT world — a mirror must never clobber an unrelated lineage.</summary>
        public string PlaythroughId  { get; set; } = "";
        /// <summary>The lineage's loan ledger (loans.bamp.json, small — review fix
        /// 2026-07-23): part of the session store, rides the sweep's manifest piece so
        /// loans survive a host handoff. "" = none.</summary>
        public string LedgerJson     { get; set; } = "";
        /// <summary>v9: gzip(.hsg) as raw bytes via the envelope attachment frame (no base64).</summary>
        [Newtonsoft.Json.JsonIgnore]
        public byte[]? HsgRaw        { get; set; }
        public bool    HasHsgFile()  => (HsgRaw?.Length ?? 0) > 0 || !string.IsNullOrEmpty(HsgGzipBase64);
        public byte[]? GetHsgGzip()  => (HsgRaw?.Length ?? 0) > 0 ? HsgRaw
                                        : string.IsNullOrEmpty(HsgGzipBase64) ? null : System.Convert.FromBase64String(HsgGzipBase64);
    }

    /// <summary>
    /// One rival-owned business, for the per-business breakdown table shown in
    /// the rival detail view (RivalBusinessesTable).  The client can't compute
    /// per-business income for AI businesses (their sales aren't simulated
    /// locally), so the host sends the authoritative figures keyed by AddressKey.
    /// </summary>
    public class RivalBusinessInfo
    {
        public string AddressKey   { get; set; } = "";   // "{streetNumber} {streetName}" — matches GameStateReader.AddressKey
        public string BusinessName { get; set; } = "";
        public string BusinessType { get; set; } = "";    // business type id string (EA 0.11)
        public float  WeeklyIncome { get; set; }
    }

    /// <summary>Per-rival stats for the leaderboard display.</summary>
    public class RivalStatsInfo
    {
        public string Id                     { get; set; } = "";
        public string Name                   { get; set; } = "";
        public int    AgeInYears             { get; set; }
        public float  WeeklyIncome           { get; set; }
        public int    OwnedBuildingsCount    { get; set; }
        public int    OwnedBusinessesCount   { get; set; }
        public string MostActiveNeighborhood { get; set; } = "";   // neighborhood id string (EA 0.11)
        public bool   IsDefeated             { get; set; }
        /// <summary>Per-business breakdown (host-authoritative income per owned
        /// business).  Drives both the detail-view breakdown income override and
        /// the leaderboard business-count reconciliation on the client.</summary>
        public List<RivalBusinessInfo> Businesses { get; set; } = new();
        /// <summary>Real per-day series (players only) — installed as the
        /// synthetic row's RivalState so the detail-view graphs plot truth
        /// instead of the flat/random backfill.</summary>
        public List<HistoryPointF> IncomeHistory   { get; set; } = new();
        public List<HistoryPointI> BizCountHistory { get; set; } = new();
    }

    /// <summary>
    /// Host → Client: stats for every rival in the host's view.  Sent in
    /// response to a RivalsStatsRequest (or when host's own rivals window
    /// rebuilds, which would be relevant once we hit multi-client).  Client
    /// caches and uses these to override RivalLeaderboard.GetRivalLeaderboardData
    /// return values.
    /// </summary>
    public class RivalsStatsSnapshotPayload
    {
        public List<RivalStatsInfo> Stats { get; set; } = new();
    }
}
