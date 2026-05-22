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
}
