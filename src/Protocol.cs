using System.Text.Json;
using System.Text.Json.Serialization;

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
        InteriorRequest      = 60, // Client → Host: "I entered building X, subscribe me + send snapshot."
        InteriorSnapshot     = 61, // Host → Client: full interior state of one building.
        PlayerExitedBuilding = 62, // Client → Host: "I exited building X, unsubscribe me."

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
        RestVote             = 102, // Client → Host: this player started/ended a rest-class activity (consensus time-skip voting).
        RestSkipState        = 103, // Host → All: current votes + whether the consensus skip is running (banner + skip-detector stand-down).
        MoneyTransfer        = 104, // Client → Host: "I sent $X to player Y" (sender already debited locally; host routes the credit).
        LoanOffer            = 105, // Any → Host → target: a player offers another a loan (principal, daily interest, daily payment).
        LoanAnswer           = 106, // Target → Host: accept/decline a loan offer.
        LoanState            = 107, // Host → All: the authoritative active-loan ledger (Business Hub display).
        MoneyAdjust          = 108, // Host → one player: credit/debit your wallet by Amount (transfer delivery, loan principal, daily loan payments).
        PhaseReport          = 110, // Client → Host: my lifecycle phase changed (load-fence visibility; lets the host excuse a client who bailed to the menu instead of loading).
        RegisterCashier      = 111, // Any → Host → All: player went on/off duty at the cash register near (X,Y,Z); others can F4-buy there (Wave-2 player-staffed registers).
        RemoteSale           = 112, // Buyer → Host: I bought items in another player's shop (buyer already paid locally); host validates and credits the owner.
    }

    // ── Envelope ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every packet is a MessageEnvelope serialised as JSON.
    /// Keeping it simple for now — can switch to a binary format later.
    /// </summary>
    public class MessageEnvelope
    {
        [JsonPropertyName("t")]
        public MessageType Type { get; set; }

        [JsonPropertyName("from")]
        public string SenderId { get; set; } = "";

        /// <summary>JSON payload — type depends on MessageType.</summary>
        [JsonPropertyName("d")]
        public string Data { get; set; } = "";

        public byte[] Serialize()
        {
            return JsonSerializer.SerializeToUtf8Bytes(this);
        }

        public static MessageEnvelope? Deserialize(byte[] bytes)
        {
            return JsonSerializer.Deserialize<MessageEnvelope>(bytes);
        }

        public static MessageEnvelope Create<T>(MessageType type, string senderId, T payload)
        {
            return new MessageEnvelope
            {
                Type = type,
                SenderId = senderId,
                Data = JsonSerializer.Serialize(payload)
            };
        }

        public T? GetPayload<T>() => JsonSerializer.Deserialize<T>(Data);
    }

    // ── Payload types ─────────────────────────────────────────────────────────

    /// <summary>Sent by client on connect.</summary>
    public class HelloPayload
    {
        public string PlayerId { get; set; } = "";
        public string Version  { get; set; } = "";
        /// <summary>Immutable identity (SteamID64 / guid-…) — the key for save +
        /// ownership persistence, distinct from the mutable PlayerId display name.</summary>
        public string StableId { get; set; } = "";
    }

    /// <summary>
    /// Full world snapshot sent to a newly connecting client.
    /// Contains everything shared between players.
    /// </summary>
    public class WorldSnapshotPayload
    {
        /// <summary>Street+number → owner player ID. Empty string = available.</summary>
        public Dictionary<string, string> BuildingOwners { get; set; } = new();

        /// <summary>Serialised List&lt;ProductMarketEntry&gt; as JSON string.</summary>
        public string MarketEntriesJson { get; set; } = "[]";
    }

    /// <summary>A single building changed ownership.</summary>
    public class BuildingOwnershipPayload
    {
        /// <summary>"StreetNumber StreetName" e.g. "14 OakStreet"</summary>
        public string AddressKey  { get; set; } = "";

        /// <summary>Player ID of new owner, or empty string if vacated.</summary>
        public string OwnerPlayerId { get; set; } = "";

        /// <summary>Daily rent amount (for the client to record).</summary>
        public float DailyRent { get; set; }

        /// <summary>Last deposit paid.</summary>
        public float LastDeposit { get; set; }
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
        public bool   DisableEnergy                    { get; set; } = true;   // MP: no sleep-skip
        public bool   DisableHappiness                 { get; set; } = false;
        public bool   AllCoursesUnlocked               { get; set; } = false;
        public int    StartingMoney                    { get; set; } = 100000;
        public int    TaxPercentage                    { get; set; } = 10;
        public int    DaysPerYear                      { get; set; } = 60;
        public float  MarketPriceMultiplier            { get; set; } = 1f;
        public float  EmployeeHourlySalaryMultiplier   { get; set; } = 1f;
        public float  BankInterestMultiplier           { get; set; } = 1f;
        public bool   TutorialEnabled                  { get; set; } = false;  // MP: no story quests
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
        public float  X { get; set; }
        public float  Y { get; set; }
        public float  Z { get; set; }
        /// <summary>Full rotation quaternion (cars pitch/roll on slopes).</summary>
        public float  Qx { get; set; }
        public float  Qy { get; set; }
        public float  Qz { get; set; }
        public float  Qw { get; set; }
        /// <summary>Cargo manifest ("ItemName:amount;…") so remote ghosts show
        /// the bed/handtruck boxes (they derive from cargo) — user bug 2026-06-11.</summary>
        public string Cargo { get; set; } = "";
        /// <summary>Address key of the building this vehicle is inside ("" =
        /// outdoors).  Cross-interior mask v2: tagged sender-side (near the
        /// owner while they're inside → that building; near them outside → "";
        /// far from them → last tag kept), so a cart LEFT in a shop stays
        /// hidden from players in other same-type interiors.</summary>
        public string Bldg { get; set; } = "";
    }

    /// <summary>
    /// A player's complete vehicle fleet — every owned vehicle, parked or driven.
    /// Broadcast at ~10 Hz; it is the full truth for that owner, so a vehicle
    /// that drops out of the list has been sold/removed and its ghost is despawned.
    /// </summary>
    public class VehicleFleetPayload
    {
        public string OwnerId { get; set; } = "";
        public List<VehicleEntry> Vehicles { get; set; } = new();
        /// <summary>Sender's unscaled clock at sample time.  Receivers use the
        /// DIFFERENCE between two stamps from the same sender to measure true
        /// velocity for dead reckoning — packet arrival times are quantized to
        /// the receiver's frames and useless for velocity at low FPS.</summary>
        public float  T { get; set; }
    }

    /// <summary>One AI-traffic car in a host traffic snapshot.</summary>
    public class TrafficCarDto
    {
        /// <summary>Stable Gley pool index — identifies this car across snapshots.</summary>
        public int    Index { get; set; }
        /// <summary>Vehicle model name (e.g. "VordV150").</summary>
        public string Model { get; set; } = "";
        public float  X { get; set; }
        public float  Y { get; set; }
        public float  Z { get; set; }
        public float  Qx { get; set; }
        public float  Qy { get; set; }
        public float  Qz { get; set; }
        public float  Qw { get; set; }
        /// <summary>
        /// Body colours — flattened 6 floats (tint RGB + fresnel RGB) per
        /// SH_Vehicle renderer.  A single group means every renderer is that
        /// colour; multiple groups = per-renderer (e.g. a box truck's cab).
        /// </summary>
        public List<float> Colors { get; set; } = new();
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
        /// <summary>BusinessTypeName enum value (cast to int for transport).</summary>
        public int    BusinessTypeName   { get; set; }
        public bool   TemporarilyClosed  { get; set; }

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
        // We mirror host's schedule verbatim.
        public bool SharedSchedule { get; set; }
        public List<ScheduleDayInfo> Schedule { get; set; } = new();

        // ── Ownership (Phase 1d) ──────────────────────────────────────────────
        // The two RivalId strings drive BuildingResume.rivalBuildingOwner /
        // rivalBusinessOwner via RivalsHelper.GetRivalName.  RentedByPlayer is
        // sent for completeness.  Phase 1d Wave 3 adds OwnerPlayerId fields:
        // when a building/business is owned by a HUMAN player (local on the
        // sender's machine), the player's PlayerId is included so receivers
        // can translate it:
        //   * if receiver IS that player → set reg.RentedByPlayer = true
        //   * else → set reg.buildingOwnerRivalId = OwnerPlayerId (and we
        //     ensure a rival entry exists for that player so the popup shows
        //     the player's name).
        public string BuildingOwnerRivalId { get; set; } = "";
        public string BusinessOwnerRivalId { get; set; } = "";
        public bool   RentedByPlayer       { get; set; }
        public string OwnerPlayerId        { get; set; } = "";
        public string BusinessOwnerPlayerId{ get; set; } = "";
    }

    /// <summary>One day of the week's opening schedule for one building.</summary>
    public class ScheduleDayInfo
    {
        /// <summary>DayOfWeekOrdered enum value (Monday=1..Sunday=7).</summary>
        public int Day    { get; set; }
        public bool IsOpen { get; set; }
        public List<OpeningHourSlotInfo> OpeningHourSlots { get; set; } = new();
    }

    /// <summary>One contiguous open-hours window within a day (e.g. 09:00-17:00).</summary>
    public class OpeningHourSlotInfo
    {
        public int StartingHour { get; set; }
        public int EndingHour   { get; set; }
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

    /// <summary>A single retail-shelf price tag.</summary>
    /// <summary>Business Hub payloads (MessageTypes 104-108).</summary>
    public class MoneyTransferPayload
    {
        public string From   { get; set; } = "";
        public string To     { get; set; } = "";
        public float  Amount { get; set; }
    }

    public class LoanOfferPayload
    {
        public string Id            { get; set; } = "";
        public string From          { get; set; } = "";   // lender / gift sender
        public string To            { get; set; } = "";   // borrower / gift receiver
        public float  Principal     { get; set; }
        public float  DailyInterest { get; set; }
        public float  DailyPayment  { get; set; }
        /// <summary>"loan" or "gift" — gifts also require an accept (no silent
        /// handouts; acceptance doubles as the read receipt).</summary>
        public string Kind          { get; set; } = "loan";
        /// <summary>Offer lifecycle: "offer" (new), "revoke" (offerer
        /// cancelled), "accepted"/"declined" (host → offerer: result, clears
        /// their outgoing list).</summary>
        public string State         { get; set; } = "offer";
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

    /// <summary>One lifecycle transition on a client (MessageType.PhaseReport).</summary>
    public class PhaseReportPayload
    {
        public string PlayerId { get; set; } = "";
        public string Phase    { get; set; } = "";
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
        public int ItemName { get; set; }   // BigAmbitions.Items.ItemName enum
        public int Amount   { get; set; }
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
        public int  Required   { get; set; }
        public bool SkipActive { get; set; }
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
        /// <summary>ItemName enum value (as int for cross-version safety).</summary>
        public int   ItemName { get; set; }
        public float Price    { get; set; }
    }

    /// <summary>A single dirt spot on the floor.</summary>
    public class DirtSpotInfo
    {
        public int   X         { get; set; }
        public int   Z         { get; set; }
        public float Dirtiness { get; set; }
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
        public List<InteriorDesignInfo>   InteriorDesigns { get; set; } = new();
        public List<RetailPriceInfo>      RetailPrices    { get; set; } = new();
        public List<DirtSpotInfo>         DirtSpots       { get; set; } = new();
        public List<ItemInstanceInfo>     ItemInstances   { get; set; } = new();
    }

    // ── Item instance DTOs (Phase 2b) ────────────────────────────────────────
    // Mirror of BigAmbitions.Items.ItemInstance and its nested types.  Active
    // fields only; the 11 [Obsolete] fields on ItemInstance are skipped.
    // Enums are transmitted as int; SerializableVector3/Quaternion are inlined
    // as flat floats; SerializableColor uses the packed-int pattern from Phase 1.

    public class ItemInstanceInfo
    {
        public string Id                { get; set; } = "";
        public int    ItemName          { get; set; }   // BigAmbitions.Items.ItemName enum
        public float  Px { get; set; }  public float Py { get; set; }  public float Pz { get; set; }
        public float  Qx { get; set; }  public float Qy { get; set; }  public float Qz { get; set; }  public float Qw { get; set; }
        public float  YRotation         { get; set; }
        public string ParentId          { get; set; } = "";
        public int    StreetName        { get; set; }
        public int    StreetNumber      { get; set; }
        public int    LinkedItemName    { get; set; }
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
    }

    public class AttachableChildInfo
    {
        public string ChildId          { get; set; } = "";
        public int    ChildItemName    { get; set; }
        public int    AttachmentIndex  { get; set; }
    }

    public class CargoInstanceInfo
    {
        public int    ItemName     { get; set; }
        public int    Amount       { get; set; }
        public float  PricePerUnit { get; set; }
        public bool   Paid         { get; set; }
        public List<CustomColorInfo>          CustomColors         { get; set; } = new();
        public List<NestedCargoInstanceInfo>  NestedCargoInstances { get; set; } = new();
    }

    public class NestedCargoInstanceInfo
    {
        public int    ItemName     { get; set; }
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
        public int    ItemName     { get; set; }
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
    }

    /// <summary>
    /// Client → Host: triggers a fresh stats snapshot AND attaches the
    /// client's own self-stats so host has data to populate the client's row
    /// on host's own leaderboard.  Self-stats are computed locally from the
    /// client's gi.realEstate / RentedByPlayer state.
    /// </summary>
    public class RivalsStatsRequestPayload
    {
        public string PlayerId { get; set; } = "";
        public int    SelfOwnedBuildingsCount  { get; set; }
        public int    SelfOwnedBusinessesCount { get; set; }
        public float  SelfWeeklyIncome         { get; set; }
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
    }

    /// <summary>Host → All: trigger a coordinated MP save.  Every player saves
    /// their own .hsg into the named MP session folder, then reports back.</summary>
    public class SaveNowPayload
    {
        public string SessionName { get; set; } = "";
        /// <summary>Why the save fired — for logging only ("manual"/"autosave"/"disconnect").</summary>
        public string Reason      { get; set; } = "";
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
        public string SessionName    { get; set; } = "";
        public bool   Success        { get; set; }
        public MpSlot Slot           { get; set; } = new();
        public string HsgGzipBase64  { get; set; } = "";   // gzip(.hsg bytes) → base64
        public int    RawLength       { get; set; }          // uncompressed length, for sanity check
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

    /// <summary>Host → Client: the client's own stored .hsg for an MP session, so
    /// it can load (or reconnect into) the session.  The .hsg lives on the host
    /// (centralized persistence); this ships it back.  Money is the host's most
    /// current known cash for this player, overlaid after the load completes.</summary>
    public class LoadDataPayload
    {
        public string SessionName    { get; set; } = "";
        public string HsgGzipBase64  { get; set; } = "";
        public int    RawLength      { get; set; }
        public float  Money          { get; set; }
        /// <summary>Mid-join fallback chain: when HsgGzipBase64 is EMPTY the
        /// client loads its own LOCAL session save if present, else starts a
        /// fresh character with these host settings (null → Normal preset).</summary>
        public GameVariablesDto? FallbackSettings { get; set; }
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
        public int    BusinessType { get; set; }          // BusinessTypeName enum index
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
        public int    MostActiveNeighborhood { get; set; }   // enum index
        public bool   IsDefeated             { get; set; }
        /// <summary>Per-business breakdown (host-authoritative income per owned
        /// business).  Drives both the detail-view breakdown income override and
        /// the leaderboard business-count reconciliation on the client.</summary>
        public List<RivalBusinessInfo> Businesses { get; set; } = new();
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
