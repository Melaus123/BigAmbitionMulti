using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BigAmbitionsMP
{
    /// <summary>
    /// RIVALS-HOST-SWITCH-1 part 1 (owner-approved 2026-09-30, decision 55) - the HOST CARRY.
    ///
    /// The world-only state (AI shops' daily income lists, their identity and creation day, the
    /// neighbourhood "next new business / next forced shutdown" days, the rival income histories, the
    /// special rivals' active/defeated state, the product market, market events, buildings for sale,
    /// the wholesale/import rival ids) is computed ONLY on the machine that hosts. A former client that
    /// starts hosting used to continue from its OWN copy of that state - frozen, faked or empty (player
    /// report bamp-bug-20260930-002824: the rivals collapsed). Design option B: the new host keeps its
    /// own load exactly as before, and right after that load (before any member is served) the
    /// previous host's .hsg - which every coordinated save mirrors into this store - is read on the
    /// side (SaveGameSerializationHelper.DeserializeBinaryData never touches SaveGameManager.Current)
    /// and ONLY those world rows are copied over. Every player-run shop stays exactly as loaded
    /// (ledger, RentedByPlayer, or an owner id that is not an AI rival - D3 rules 1-3), so a miss can
    /// only leave a row the way it was before this change. Nothing is invented: a gate that fails
    /// logs one loud line and the world continues from this machine's copy, as it did before.
    /// MAIN THREAD only (called from MPSaveCoordinator.HostLoadSession's main-thread continuation).
    /// </summary>
    internal static class HostHandoffCarry
    {
        /// <summary>The last host start's carry mode on this machine: full | partial | none | off |
        /// same-host (no host switch) | "" (this machine has not hosted a load). Read by the
        /// rivalhealth lever; the bug-report stamp is MPSaveCoordinator.LastHostCarry.</summary>
        public static string Mode = "";
        /// <summary>The previous host's stable id the last carry read from ("" when none).</summary>
        public static string From = "";

#if BAMP_DEV
        /// <summary>DEV lever 'hostcarry off': skip the carry at the NEXT host start (one-shot) and log
        /// carry=off with the start health line - the rig's proof that the test sees the bug.</summary>
        public static bool DevOffNext;
        /// <summary>DEV lever 'hostcarry buildskew' (gate-F1 unit leg): the NEXT carry reads the running game's
        /// build as one higher than it is (one-shot), so gate d refuses the previous host's copy exactly as it
        /// would after a game update.</summary>
        public static bool DevBuildSkewNext;
        /// <summary>DEV 'rivalhealth base': the ident7 count recorded on this world (by object identity) - later
        /// rivalhealth lines print ident7Delta against it (part 2 test: a client writes no identical-7 lists).</summary>
        internal static int DevRhBaseId7 = -1, DevRhBaseWorld;
#endif

        // F3 (review of 8920306): the carry is staged and committed in ONE final step. 0 = nothing in the loaded
        // world changed yet; 1 = the commit is running (a throw here can leave the world partly carried);
        // 2 = committed (only the report is left). Read by Apply's catch so the log and the stamp tell the truth.
        private static int _phase;

        private const float FullWindowMinutes = 60f;          // |delta| <= 60 game-minutes = one coordinated save
        private const float PartialFloorMinutes = -7f * 1440f; // previous host's copy older by up to 7 days = partial

        // Every serialized BuildingRegistration field (BuildingRegistration.cs:38-143) except the address
        // (the match key) and poachedEmployees (EmployeeInstance references into the PREVIOUS world's
        // EmployeeInstances - copying them would plant stray duplicates; design D7). Built once.
        private static FieldInfo[]? _fullFields;
        private static FieldInfo[]? _partialFields;
        private static FieldInfo? _interiorLookupField;
        private static FieldInfo? _itemStreetName, _itemStreetNumber;   // ItemInstance's address stamp (read by reflection: the type is outside the decompile)

        // Partial carry (design D2 step 0e): only rows 1, 3, 4 per registration (income list, creation
        // day, the AI shop's internals) - and only where the business identity matches in both copies.
        private static readonly string[] PartialFieldNames =
        {
            "dailyIncomes", "creationDay", "aiEmployees", "itemInstances", "takeoverOfferAcceptRate", "takenOver",
            "customerCapacity", "factoryExports", "satisfaction", "marketingCampaigns", "lastDayOnSale", "uniformsBySkill",
        };

        private static void EnsureFields()
        {
            if (_fullFields != null) return;
            var full = new List<FieldInfo>();
            var part = new List<FieldInfo>();
            foreach (var f in typeof(BuildingRegistration).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsNotSerialized || f.IsInitOnly || f.IsLiteral) continue;
                if (f.Name == "StreetName" || f.Name == "StreetNumber" || f.Name == "poachedEmployees") continue;
                full.Add(f);
                if (Array.IndexOf(PartialFieldNames, f.Name) >= 0) part.Add(f);
            }
            _interiorLookupField = typeof(BuildingRegistration).GetField("_interiorLookup", BindingFlags.NonPublic | BindingFlags.Instance);
            _partialFields = part.ToArray();
            _fullFields = full.ToArray();
        }

        /// <summary>MAIN THREAD, right after the host's own LoadOwnHsg returned true and BEFORE the cash
        /// overlay and the member serve. ownId = this machine's effective stable id (the DEV load-as
        /// override included). Never throws.</summary>
        public static void Apply(string session, MpManifest m, string ownId)
        {
            _phase = 0;
            try { ApplyInner(session, m, ownId); }
            catch (Exception ex)
            {
                if (_phase == 0)
                {
                    Mode = "none";
                    MPSaveCoordinator.LastHostCarry = "mode=none (exception before the commit - nothing changed)";
                    Plugin.Logger.LogError($"[MPSave] HOST HANDOFF CARRY: none — the carry threw before it changed anything ({ex.GetType().Name}: {ex.Message}); world-only state continues from this machine's copy as loaded. {ex}");
                }
                else if (_phase == 1)
                {
                    Mode = "torn";
                    MPSaveCoordinator.LastHostCarry = "mode=torn (exception DURING the commit - the world may be partly carried)";
                    Plugin.Logger.LogError($"[MPSave] HOST HANDOFF CARRY: none — the carry threw DURING its commit ({ex.GetType().Name}: {ex.Message}); the world-only state may be PARTLY carried from the previous host's copy. {ex}");
                }
                else
                    Plugin.Logger.LogWarning($"[MPSave] HOST HANDOFF CARRY: the carry was committed (mode={Mode}) but its report threw ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static void ApplyInner(string session, MpManifest m, string ownId)
        {
            Mode = ""; From = "";
            var cur = SaveGameManager.Current;
            // Gate a: a host switch at all - the same condition as the HOST HANDOFF line in HostLoadSession,
            // on the EFFECTIVE id. Not a switch = nothing to carry (silent: this is every ordinary load).
            string prevId = m?.LastHostStableId ?? "";
            if (m == null || prevId.Length == 0 || string.Equals(prevId, ownId, StringComparison.Ordinal))
            {
                Mode = "same-host";
                MPSaveCoordinator.LastHostCarry = "mode=same-host";
                return;
            }
            From = prevId;
            if (cur == null) { None(prevId, "no world loaded after the host's own load", cur); return; }
#if BAMP_DEV
            CheckTickOrderAtCarry(cur);
            if (DevOffNext)
            {
                DevOffNext = false;
                Mode = "off";
                MPSaveCoordinator.LastHostCarry = $"mode=off from={prevId}";
                Plugin.Logger.LogWarning($"[MPSave] HOST HANDOFF CARRY: mode=off from={prevId} — DEV 'hostcarry off' lever: the carry was skipped for this host start.");
                LogHealth(cur);
                return;
            }
#endif
            var swTotal = Stopwatch.StartNew();
            // Gate b: the previous host's file exists (the mirror puts it under its member folder of THIS
            // session folder - the same snapshot this load came from) and its gzip container verifies.
            string folder = MPSaveManager.MpCharacterFolder(session, prevId);
            string? file = NewestHsgIn(folder);
            if (file == null) { None(prevId, $"the previous host's save is not in this store ('{folder}')", cur); return; }
            var swRead = Stopwatch.StartNew();
            try
            {
                using var fs = File.OpenRead(file);
                using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                var buf = new byte[81920];
                while (gz.Read(buf, 0, buf.Length) > 0) { }
            }
            catch (Exception ex) { None(prevId, $"the previous host's save failed container verification ({ex.Message})", cur); return; }
            long verifyMs = swRead.ElapsedMilliseconds;

            // Gate c: the side read returns a world. The native reader logs and swallows its own failures
            // (and then tries the legacy reader), so the same torn-read probe GuardedNativeLoad uses
            // listens here too - a read that complained is refused, never half-carried.
            int torn = 0, exceptions = 0;
            UnityEngine.Application.LogCallback probe = (cond, _, type) =>
            {
                try
                {
                    if (type == UnityEngine.LogType.Exception) exceptions++;
                    if (cond != null && cond.Contains("Reading array went wrong")) torn++;
                }
                catch { }
            };
            GameInstance? prev = null;
            var swDeser = Stopwatch.StartNew();
            UnityEngine.Application.logMessageReceived += probe;
            try { prev = SaveGameSerializationHelper.DeserializeBinaryData(file); }
            catch (Exception ex) { None(prevId, $"the side read threw ({ex.GetType().Name}: {ex.Message})", cur); return; }
            finally { UnityEngine.Application.logMessageReceived -= probe; }
            long deserMs = swDeser.ElapsedMilliseconds;
            Plugin.Logger.LogInfo($"[MPSave] HOST HANDOFF CARRY: side read of '{file}' ({SafeLength(file)} bytes) verify={verifyMs} ms deserialize={deserMs} ms (main thread).");
            if (prev == null) { None(prevId, "the side read returned no world", cur); return; }
            if (torn > 0 || exceptions > 0) { None(prevId, $"the side read reported damage (torn-read lines {torn}, exceptions {exceptions})", cur); return; }

            // Gate d: same build and same city. The game's compatibility fixes ran on Current only, so a copy from
            // another build is refused rather than half-migrated. NOT the world seed: every member's copy carries
            // its own (rig run T-HOSTSWITCH1-20261001-214517: host and client copies of fx-hq1 differ while the
            // 885 addresses match) - manager decision 2026-10-01: the seed is logged as information only and
            // never carried; the city identity is the registration address set (at most 1% difference).
            if (prev.buildNumberAtLastSave != cur.buildNumberAtLastSave)
            { None(prevId, $"the previous host's save is from build {prev.buildNumberAtLastSave}, this machine's from build {cur.buildNumberAtLastSave}", cur); return; }
            // F1 (review of 8920306): the two files agreeing is not enough. After a game update BOTH carry the old
            // build; the save repairs (SaveGameCompatibilityFixes, 'buildNumberAtLastSave <= item.buildNumber') ran
            // on this machine's copy only, and the build is stamped only at save (SaveGameManager.cs:175) - so the
            // previous host's copy must come from the RUNNING build. The repairs are NOT run on prev (the item
            // validator pays compensation into SaveGameManager.Current.Money).
            int running = RunningBuild();
            if (prev.buildNumberAtLastSave != running)
            { None(prevId, $"the previous host's save is from build {prev.buildNumberAtLastSave}, the running game is build {running} (the game's save repairs ran on this machine's copy only, never on the previous host's)", cur); return; }
            var prevByKey = new Dictionary<string, BuildingRegistration>(StringComparer.Ordinal);
            if (prev.BuildingRegistrations != null)
                foreach (var pr in prev.BuildingRegistrations)
                    if (pr != null) prevByKey[GameStateReader.AddressKey(pr)] = pr;
            var ownKeys = new HashSet<string>(StringComparer.Ordinal);
            if (cur.BuildingRegistrations != null)
                foreach (var orr in cur.BuildingRegistrations)
                    if (orr != null) ownKeys.Add(GameStateReader.AddressKey(orr));
            int addrBoth = 0;
            foreach (var k in ownKeys) if (prevByKey.ContainsKey(k)) addrBoth++;
            int addrUnion = ownKeys.Count + prevByKey.Count - addrBoth;
            int addrDiff = addrUnion - addrBoth;
            if (addrUnion == 0 || addrDiff * 100 > addrUnion)
            { None(prevId, $"the previous host's save is a different city ({addrBoth} of {addrUnion} addresses match)", cur); return; }
            string seedOwn = cur.seed ?? "", seedPrev = prev.seed ?? "";

            // Gate e: freshness on the DESERIALIZED clocks (never the catalog - round-262).
            float delta = (prev.Day * 1440f + prev.Hour * 60f + prev.Minute) - (cur.Day * 1440f + cur.Hour * 60f + cur.Minute);
            string mode;
            if (delta >= -FullWindowMinutes) mode = "full";            // |delta| <= 60, or the previous host's copy is newer
            else if (delta >= PartialFloorMinutes) mode = "partial";   // the previous host's copy is older (up to 7 days)
            else { None(prevId, $"the previous host's save is {(-delta / 1440f):0.#} days older than this machine's", cur); return; }
            bool full = mode == "full";

            EnsureFields();
            var swApply = Stopwatch.StartNew();

            // ── STAGE (F3): everything below up to COMMIT only READS the loaded world. Every replacement is
            // computed into the plan first; an exception before COMMIT leaves this machine's world untouched.

            // Step 1: the AI id list from prev - a positive list (player stamps never match the manifest's
            // names, design fixture note, so player shops can only be recognised as "not AI").
            var ai = AiIds(prev, false);

            // F4: this machine's employee records by id (the roster + every registration's poached copies) - a
            // carried AI shop takes prev's poachedEmployees only when every one of them resolves HERE (the objects
            // used are this world's own; no EmployeeInstance is ever created). Built lazily: most worlds have none.
            Dictionary<string, Entities.EmployeeInstance>? ownEmp = null;
            Dictionary<string, Entities.EmployeeInstance> OwnEmployees()
            {
                if (ownEmp != null) return ownEmp;
                ownEmp = new Dictionary<string, Entities.EmployeeInstance>(StringComparer.Ordinal);
                if (cur.EmployeeInstances != null)
                    foreach (var e in cur.EmployeeInstances) if (e != null && !string.IsNullOrEmpty(e.id)) ownEmp[e.id] = e;
                if (cur.BuildingRegistrations != null)
                    foreach (var r in cur.BuildingRegistrations)
                        if (r?.poachedEmployees != null)
                            foreach (var e in r.poachedEmployees) if (e != null && !string.IsNullOrEmpty(e.id)) ownEmp[e.id] = e;
                return ownEmp;
            }

            // Step 2: registrations, matched on the address key (prevByKey, built at gate d), classified by design D3.
            var ledger = m.BuildingOwners ?? new Dictionary<string, string>();
            var deeds = m.BuildingRealEstateOwners ?? new Dictionary<string, string>();
            int aiCarried = 0, keptPlayer = 0, keptLedger = 0, keptRented = 0, keptStamp = 0, keptUnknown = 0,
                conflicts = 0, deedKept = 0, unmatched = 0, partialSkipped = 0, poachPrev = 0, poachOwn = 0, poachNone = 0;
            var conflictKeys = new List<string>();
            var matched = new HashSet<string>(StringComparer.Ordinal);
            var fields = full ? _fullFields! : _partialFields!;
            bool fieldsHaveAiEmp = false;
            foreach (var f in fields) if (f.Name == "aiEmployees") { fieldsHaveAiEmp = true; break; }
            var regPlan = new List<RegCarry>();
            if (cur.BuildingRegistrations != null)
                foreach (var own in cur.BuildingRegistrations)
                {
                    if (own == null) continue;
                    string key = GameStateReader.AddressKey(own);
                    if (!prevByKey.TryGetValue(key, out var p) || p == null) { unmatched++; continue; }
                    matched.Add(key);
                    string ownBiz = own.businessOwnerRivalId ?? "", prevBiz = p.businessOwnerRivalId ?? "";
                    bool inLedger = ledger.ContainsKey(key);                                   // rule 1
                    bool rented = own.RentedByPlayer || p.RentedByPlayer;                      // rule 2
                    bool stamp = (ownBiz.Length > 0 && !ai.Contains(ownBiz))                   // rule 3
                              || (prevBiz.Length > 0 && !ai.Contains(prevBiz));
                    if (inLedger || rented || stamp)
                    {
                        keptPlayer++;
                        if (inLedger) keptLedger++; else if (rented) keptRented++; else keptStamp++;
                        // Rule 7: prev says AI-run while this copy marks a player - keep own, count it.
                        if (prevBiz.Length > 0 && ai.Contains(prevBiz) && !p.RentedByPlayer)
                        {
                            conflicts++;
                            if (conflictKeys.Count < 5) conflictKeys.Add(key);
                        }
                        continue;
                    }
                    // Rule 6: prev's business owner is an AI rival, or the building is empty / for rent in prev.
                    bool prevAi = prevBiz.Length > 0 && ai.Contains(prevBiz);
                    bool prevEmptyOrRent = IsEmptyType(p.businessTypeName) || p.AvailableForRent;
                    if (!prevAi && !prevEmptyOrRent) { keptUnknown++; continue; }
                    if (!full && (!string.Equals(own.businessTypeName ?? "", p.businessTypeName ?? "", StringComparison.Ordinal)
                                  || !string.Equals(ownBiz, prevBiz, StringComparison.Ordinal)))
                    { partialSkipped++; continue; }
                    // Rule 5: a player's deed keeps the building-owner field from own (an AI business in a
                    // player-bought building); the realEstate list is never touched by the carry.
                    string ownBldg = own.buildingOwnerRivalId ?? "", prevBldg = p.buildingOwnerRivalId ?? "";
                    bool deed = deeds.ContainsKey(key)
                             || (ownBldg.Length > 0 && !ai.Contains(ownBldg))
                             || (prevBldg.Length > 0 && !ai.Contains(prevBldg));
                    var rc = new RegCarry { Own = own, Prev = p, KeepBldg = deed && full };
                    // F4: the AI-employee list and the poached list are one pair (EmployeeInstance.cs:1150-1161 moves one
                    // AI worker into poachedEmployees per poach). Carry prev's pair when every poached id resolves in
                    // this world's records; otherwise keep own's pair (own aiEmployees + own poachedEmployees).
                    if (fieldsHaveAiEmp)
                    {
                        var pp = p.poachedEmployees;
                        if ((pp == null || pp.Count == 0) && (own.poachedEmployees == null || own.poachedEmployees.Count == 0))
                        { poachNone++; rc.PoachSet = false; }
                        else
                        {
                            List<Entities.EmployeeInstance>? np = new List<Entities.EmployeeInstance>(pp?.Count ?? 0);
                            if (pp != null)
                            {
                                var oe = OwnEmployees();
                                foreach (var e in pp)
                                {
                                    if (e == null) continue;
                                    if (string.IsNullOrEmpty(e.id) || !oe.TryGetValue(e.id, out var mine) || mine == null) { np = null; break; }
                                    np.Add(mine);
                                }
                            }
                            if (np != null) { rc.PoachSet = true; rc.Poach = np; poachPrev++; }
                            else { rc.KeepAiEmployees = true; poachOwn++; }
                        }
                    }
                    regPlan.Add(rc);
                }
            int prevOnly = 0;
            foreach (var k in prevByKey.Keys) if (!matched.Contains(k)) prevOnly++;

            // Step 3: the collections (staged).
            int ns = 0, rivalStates = 0, rivalAdded = 0, special = 0, market = 0, events = 0, forSale = 0, forSaleDropped = 0, forSaleNoAddr = 0;
            var nsPlan = new List<KeyValuePair<int, BigAmbitions.Neighborhoods.NeighbourhoodStats>>();
            if (prev.NeighbourhoodStats != null && cur.NeighbourhoodStats != null)
                foreach (var pn in prev.NeighbourhoodStats)
                {
                    if (pn == null || string.IsNullOrEmpty(pn.name)) continue;
                    nsPlan.Add(new KeyValuePair<int, BigAmbitions.Neighborhoods.NeighbourhoodStats>(cur.NeighbourhoodStats.FindIndex(x => x != null && x.name == pn.name), pn));
                    ns++;
                }
            var rsPlan = new List<KeyValuePair<int, BigAmbitions.Rivals.RivalState>>();
            if (prev.rivalStates != null)
                foreach (var pr in prev.rivalStates)
                {
                    if (pr == null || string.IsNullOrEmpty(pr.rivalId)) continue;
                    int at = cur.rivalStates == null ? -1 : cur.rivalStates.FindIndex(x => x != null && x.rivalId == pr.rivalId);
                    if (at < 0) rivalAdded++;
                    rsPlan.Add(new KeyValuePair<int, BigAmbitions.Rivals.RivalState>(at, pr));
                    rivalStates++;
                }
            // F2 (manager decision): the special rivals' isActive/isDefeated/defenseStates are carried in FULL mode only.
            // In PARTIAL mode (prev older) own's copy is the newer synced one (R3 publishes them, and part 2 stops a
            // client defeating a rival locally), the same rule the design gives every synced row in partial mode.
            int specialMissing = 0;
            var spPlan = new List<KeyValuePair<BigAmbitions.Rivals.SpecialRivalState, BigAmbitions.Rivals.SpecialRivalState>>();
            if (full && prev.specialRivalStates != null && cur.specialRivalStates != null)
                foreach (var ps in prev.specialRivalStates)
                {
                    if (ps == null || string.IsNullOrEmpty(ps.rivalId)) continue;
                    var os = cur.specialRivalStates.Find(x => x != null && x.rivalId == ps.rivalId);
                    // The per-player parts (completedTimelineEntryIds, sentMessageKeys) stay this machine's own
                    // (D1 row 11) - so a state missing here is not invented from the previous host's.
                    if (os == null) { specialMissing++; continue; }
                    spPlan.Add(new KeyValuePair<BigAmbitions.Rivals.SpecialRivalState, BigAmbitions.Rivals.SpecialRivalState>(os, ps));
                    special++;
                }
            bool idsDiffer = false;
            List<BuildingForSale>? forSaleList = null;
            if (full)
            {
                if (prev.productMarketEntries != null) market = prev.productMarketEntries.Count;
                if (prev.marketEvents != null) events = prev.marketEvents.Count;
                if (prev.buildingsForSale != null)
                {
                    forSaleList = new List<BuildingForSale>(prev.buildingsForSale.Count);
                    foreach (var b in prev.buildingsForSale)
                    {
                        if (b == null) continue;
                        // F3: null-guarded (an entry without an address cannot be a player's deed: kept as prev has it).
                        object? boxed = b.address;
                        string? bKey = null;
                        if (boxed != null) { try { bKey = GameStateReader.AddressKey(b.address); } catch { bKey = null; } }
                        if (bKey == null) forSaleNoAddr++;
                        else if (deeds.ContainsKey(bKey)) { forSaleDropped++; continue; }
                        forSaleList.Add(b);
                    }
                    forSale = forSaleList.Count;
                }
                idsDiffer = !SameIds(prev.wholesaleRivalIds, cur.wholesaleRivalIds) || !SameIds(prev.importRivalIds, cur.importRivalIds);
            }

            // ── COMMIT (F3): the one step that changes the loaded world. Plain assignments and the field copy only.
            _phase = 1;
            foreach (var rc in regPlan)
            {
                string? keepBldg = rc.Own.buildingOwnerRivalId;
                CopyFields(rc.Prev, rc.Own, fields, rc.KeepAiEmployees ? "aiEmployees" : null);
                if (rc.KeepBldg) { rc.Own.buildingOwnerRivalId = keepBldg; deedKept++; }
                if (rc.PoachSet) rc.Own.poachedEmployees = rc.Poach;
                aiCarried++;
            }
            foreach (var kv in nsPlan)
                if (kv.Key >= 0) cur.NeighbourhoodStats![kv.Key] = kv.Value; else cur.NeighbourhoodStats!.Add(kv.Value);   // nsPlan is filled only when the list exists
            if (rsPlan.Count > 0 && cur.rivalStates == null) cur.rivalStates = new List<BigAmbitions.Rivals.RivalState>();
            foreach (var kv in rsPlan)
                if (kv.Key >= 0) cur.rivalStates![kv.Key] = kv.Value; else cur.rivalStates!.Add(kv.Value);
            foreach (var kv in spPlan)
            {
                var os = kv.Key; var ps = kv.Value;
                os.isActive = ps.isActive;
                os.isDefeated = ps.isDefeated;
                os.defenseStates = ps.defenseStates;
            }
            if (full)
            {
                if (prev.productMarketEntries != null) cur.productMarketEntries = prev.productMarketEntries;
                if (prev.marketEvents != null) cur.marketEvents = prev.marketEvents;
                if (forSaleList != null) cur.buildingsForSale = forSaleList;
                if (prev.wholesaleRivalIds != null) cur.wholesaleRivalIds = prev.wholesaleRivalIds;
                if (prev.importRivalIds != null) cur.importRivalIds = prev.importRivalIds;
            }
            _phase = 2;
            long applyMs = swApply.ElapsedMilliseconds;
            prev = null;   // step 4: release the side copy

            Mode = mode;
            string summary = $"mode={mode} from={prevId} Δ={delta:0} aiCarried={aiCarried} keptPlayer={keptPlayer} keptUnknown={keptUnknown} conflicts={conflicts} "
                           + $"ns={ns} rivalStates={rivalStates} special={special} market={market} events={events} forSale={forSale} "
                           + $"ledger={ledger.Count} keptLedger={keptLedger} keptRented={keptRented} keptStamp={keptStamp} deedKept={deedKept} "
                           + $"partialSkipped={partialSkipped} unmatched={unmatched} prevOnly={prevOnly} rivalAdded={rivalAdded} specialMissing={specialMissing} "
                           + $"forSaleDropped={forSaleDropped} forSaleNoAddr={forSaleNoAddr} poachPrev={poachPrev} poachOwn={poachOwn} poachNone={poachNone} "
                           + $"idsDiffer={idsDiffer} addrMatch={addrBoth}/{addrUnion} build={running} seedOwn={seedOwn} seedPrev={seedPrev} "
                           + $"deserializeMs={deserMs} applyMs={applyMs} totalMs={swTotal.ElapsedMilliseconds}";
            MPSaveCoordinator.LastHostCarry = summary;
            Plugin.Logger.LogWarning($"[MPSave] HOST HANDOFF CARRY: {summary}");
            if (conflicts > 0)
                Plugin.Logger.LogWarning($"[MPSave] HOST HANDOFF CARRY: {conflicts} address(es) the previous host ran as AI are player shops here - kept this machine's copy: {string.Join(", ", conflictKeys)}");
            if (idsDiffer)
                Plugin.Logger.LogWarning("[MPSave] HOST HANDOFF CARRY: the wholesale/import rival ids differed between the two copies - the previous host's were taken.");
            LogHealth(cur);
        }

        /// <summary>One staged registration carry (F3/F4).</summary>
        private sealed class RegCarry
        {
            public BuildingRegistration Own = null!;
            public BuildingRegistration Prev = null!;
            public bool KeepBldg;          // rule 5: own's building-owner field stays
            public bool KeepAiEmployees;   // F4: own's aiEmployees + poachedEmployees pair stays
            public bool PoachSet;          // F4: prev's poached list, resolved to this world's own records
            public List<Entities.EmployeeInstance>? Poach;
        }

        /// <summary>The running game's build (GameVersion.GetCurrent().buildNumber - the value SaveGameManager stamps
        /// at save, SaveGameManager.cs:175). -1 when unreadable (gate d then refuses).</summary>
        private static int RunningBuild()
        {
            int b;
            try { b = GameVersion.GetCurrent()?.buildNumber ?? -1; } catch { b = -1; }
#if BAMP_DEV
            if (DevBuildSkewNext && b >= 0)
            {
                DevBuildSkewNext = false;
                Plugin.Logger.LogWarning($"[MPSave] HOST HANDOFF CARRY: DEV 'hostcarry buildskew' lever - the running build reads as {b + 1} (real {b}) for this carry.");
                b += 1;
            }
#endif
            return b;
        }

        private static void None(string prevId, string reason, GameInstance? cur)
        {
            Mode = "none";
            MPSaveCoordinator.LastHostCarry = $"mode=none from={prevId} ({reason})";
            Plugin.Logger.LogError($"[MPSave] HOST HANDOFF CARRY: none — {reason}; world-only state continues from this machine's copy");
            if (cur != null) LogHealth(cur);
        }

        private static void LogHealth(GameInstance cur)
        {
            try { Plugin.Logger.LogInfo($"[MPSave] HOST HANDOFF CARRY health: {Health(cur, false)}"); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] HOST HANDOFF CARRY health: failed ({ex.Message})"); }
        }

        private static void CopyFields(BuildingRegistration from, BuildingRegistration to, FieldInfo[] fields, string? keepField = null)
        {
            for (int i = 0; i < fields.Length; i++)
                if (keepField == null || fields[i].Name != keepField) fields[i].SetValue(to, fields[i].GetValue(from));
            // The interior-design lookup is a private cache of interiorDesigns (BuildingRegistration.cs:511-536);
            // a fresh one rebuilds itself on first use from the carried list.
            try { if (_interiorLookupField != null) _interiorLookupField.SetValue(to, Activator.CreateInstance(_interiorLookupField.FieldType)); } catch { }
            // ItemHelper.Init stamped item addresses before the carry (UpdateItemAddresses, ItemHelper.cs:1274-1292);
            // the carried items get the same stamp.
            try
            {
                if (to.itemInstances != null)
                    foreach (var it in to.itemInstances.Values)
                    {
                        if (it == null) continue;
                        if (_itemStreetName == null) { _itemStreetName = AccessTools.Field(it.GetType(), "streetName"); _itemStreetNumber = AccessTools.Field(it.GetType(), "streetNumber"); }
                        _itemStreetName?.SetValue(it, to.StreetName);
                        _itemStreetNumber?.SetValue(it, to.StreetNumber);
                    }
            }
            catch { }
        }

        private static bool IsEmptyType(string? t) => string.IsNullOrEmpty(t) || t == "ba:businesstype_empty";

        private static bool SameIds(string[]? a, string[]? b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static long SafeLength(string f) { try { return new FileInfo(f).Length; } catch { return -1; } }

        private static string? NewestHsgIn(string folder)
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

        /// <summary>The AI rival ids of a world: rivalStates + specialRivalStates + wholesale + import ids.
        /// runtime = true drops session players' ids (the mod installs runtime-only synthetic rival states
        /// for them, GameStatePatcher.InstallPlayerRivalStateHistory - stripped at every save).</summary>
        internal static HashSet<string> AiIds(GameInstance gi, bool runtime)
        {
            var ai = new HashSet<string>(StringComparer.Ordinal);
            if (gi.rivalStates != null) foreach (var r in gi.rivalStates) if (r != null && !string.IsNullOrEmpty(r.rivalId)) ai.Add(r.rivalId);
            if (gi.specialRivalStates != null) foreach (var r in gi.specialRivalStates) if (r != null && !string.IsNullOrEmpty(r.rivalId)) ai.Add(r.rivalId);
            if (gi.wholesaleRivalIds != null) foreach (var r in gi.wholesaleRivalIds) if (!string.IsNullOrEmpty(r)) ai.Add(r);
            if (gi.importRivalIds != null) foreach (var r in gi.importRivalIds) if (!string.IsNullOrEmpty(r)) ai.Add(r);
            if (runtime) ai.RemoveWhere(id => GameStatePatcher.IsSessionPlayerRivalId(id));
            return ai;
        }

        /// <summary>The rival-world health figures (design D6), one line. aiRun = registrations whose
        /// business owner is an AI rival and that are not the player's own; lens = their dailyIncomes length
        /// histogram (null counts as 0); empty = empty-type buildings; ident7 = 7 entries all equal (the
        /// client's fake series); nonZeroIncome = at least one non-zero value AND not ident7; neg7 = last-7
        /// sum below zero; nsDue = named neighbourhoods with any next-day already due; nsForce0 = next forced
        /// shutdown day 0; rivalHist = rivalStates weeklyIncomeHistory length histogram; histFlat = histories
        /// of 2+ entries all equal; specialDefeated = special rivals defeated.</summary>
        internal static string Health(GameInstance gi, bool runtime)
        {
            var ai = AiIds(gi, runtime);
            int aiRun = 0, l0 = 0, l7 = 0, l20 = 0, lOther = 0, empty = 0, ident7 = 0, nonZero = 0, neg7 = 0;
            if (gi.BuildingRegistrations != null)
                foreach (var r in gi.BuildingRegistrations)
                {
                    if (r == null) continue;
                    if (IsEmptyType(r.businessTypeName)) empty++;
                    string owner = r.businessOwnerRivalId ?? "";
                    if (r.RentedByPlayer || owner.Length == 0 || !ai.Contains(owner)) continue;
                    aiRun++;
                    var d = r.dailyIncomes;
                    int n = d?.Count ?? 0;
                    if (n == 0) l0++; else if (n == 7) l7++; else if (n == 20) l20++; else lOther++;
                    if (d == null || n == 0) continue;
                    bool allSame = true, anyNonZero = false;
                    float last7 = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        float v = d[i];
                        if (v != 0f) anyNonZero = true;
                        if (v != d[0]) allSame = false;
                        if (i >= n - 7) last7 += v;
                    }
                    bool isIdent7 = n == 7 && allSame;
                    if (isIdent7) ident7++;
                    if (anyNonZero && !isIdent7) nonZero++;
                    if (last7 < 0f) neg7++;
                }
            int nsDue = 0, nsForce0 = 0, nsNamed = 0;
            if (gi.NeighbourhoodStats != null)
                foreach (var s in gi.NeighbourhoodStats)
                {
                    if (s == null || string.IsNullOrEmpty(s.name) || s.name == "ba:neighborhood_global") continue;
                    nsNamed++;
                    if (s.nextNewBusinessDay <= gi.Day || s.nextResidentialSwapDay <= gi.Day
                        || s.nextWarehouseSwapDay <= gi.Day || s.nextForceShutdownDay <= gi.Day) nsDue++;
                    if (s.nextForceShutdownDay == 0) nsForce0++;
                }
            var hist = new SortedDictionary<int, int>();
            int histFlat = 0;
            if (gi.rivalStates != null)
                foreach (var rs in gi.rivalStates)
                {
                    if (rs == null || string.IsNullOrEmpty(rs.rivalId) || !ai.Contains(rs.rivalId)) continue;
                    var h = rs.weeklyIncomeHistory;
                    int hn = h?.Count ?? 0;
                    hist.TryGetValue(hn, out int c); hist[hn] = c + 1;
                    if (h != null && hn >= 2)
                    {
                        bool flat = true;
                        for (int i = 1; i < hn; i++) if (h[i] == null || h[0] == null || h[i].Item2 != h[0].Item2) { flat = false; break; }
                        if (flat) histFlat++;
                    }
                }
            int specialDefeated = 0;
            if (gi.specialRivalStates != null) foreach (var sr in gi.specialRivalStates) if (sr != null && sr.isDefeated) specialDefeated++;
            var sb = new StringBuilder();
            sb.Append($"day={gi.Day} {gi.Hour:00}:{(int)gi.Minute:00} aiRun={aiRun} lens={{0:{l0},7:{l7},20:{l20},other:{lOther}}} empty={empty} ident7={ident7} ");
            sb.Append($"nonZeroIncome={nonZero} nzPct={(aiRun > 0 ? 100.0 * nonZero / aiRun : 0.0):0.0} neg7={neg7} nsDue={nsDue} nsForce0={nsForce0} nsNamed={nsNamed} rivalHist={{");
            bool first = true;
            foreach (var kv in hist) { if (!first) sb.Append(','); first = false; sb.Append(kv.Key).Append(':').Append(kv.Value); }
            sb.Append($"}} histFlat={histFlat} specialDefeated={specialDefeated} carry={(Mode.Length > 0 ? Mode : "-")} carryFrom={(From.Length > 0 ? From : "-")}");
            return sb.ToString();
        }

#if BAMP_DEV
        // ── DEV assert (design D2 step 2): the carry must run BEFORE the loaded world's first hourly/daily pass.
        // The carry runs in the same main-thread action as the native Load, and the scene load that starts the
        // game clock runs over later frames - Inferred safe; this checks it. Worlds are told apart by object
        // identity (an int - no reference to an old world is kept).
        private static int _tickWorld, _ticksOnWorld, _carryWorld;
        private static bool _orderLogged = true;
        private static int _carryDay, _carryHour, _carryMinute;

        private static void CheckTickOrderAtCarry(GameInstance cur)
        {
            try
            {
                int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cur);
                if (_tickWorld == id && _ticksOnWorld > 0)
                    Plugin.Logger.LogWarning($"[MPSave] HOST HANDOFF CARRY order check: {_ticksOnWorld} hourly/daily pass(es) already ran on this world BEFORE the carry - they read the uncarried data (DEV assert).");
                _carryWorld = id; _orderLogged = false;
                _carryDay = cur.Day; _carryHour = cur.Hour; _carryMinute = (int)cur.Minute;
            }
            catch { }
        }

        private static void NoteTick(string pass)
        {
            var cur = SaveGameManager.Current;
            if (cur == null) return;
            int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cur);
            if (id != _tickWorld) { _tickWorld = id; _ticksOnWorld = 0; }
            _ticksOnWorld++;
            if (_orderLogged || id != _carryWorld) return;
            _orderLogged = true;
            // The hourly pass runs inside GameManager.RunMainGameTick's `while (Minute >= 60)` loop BEFORE Hour++ and
            // Minute -= 60 (GameManager.cs:569-575), so the raw clock reads hh:60 - shown as the hour it closes.
            int pd = cur.Day, ph = cur.Hour, pm = (int)cur.Minute;
            while (pm >= 60) { pm -= 60; ph++; }
            if (ph >= 24) { ph -= 24; pd++; }
            Plugin.Logger.LogInfo($"[MPSave] HOST HANDOFF CARRY order check: the first {pass} pass on this world ran AFTER the carry (carry at day {_carryDay} {_carryHour:00}:{_carryMinute:00}, pass at day {pd} {ph:00}:{pm:00}) - ok.");
        }

        [HarmonyPatch]
        internal static class Patch_CarryTickOrder
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var hourly = AccessTools.Method(typeof(BusinessSimulatorHelper), nameof(BusinessSimulatorHelper.RunHourly));
                if (hourly != null) yield return hourly;
                var daily = AccessTools.Method(typeof(GameManager), "NewDay");
                if (daily != null) yield return daily;
            }

            private static void Prefix(MethodBase __originalMethod)
            {
                try { NoteTick(__originalMethod?.Name == "NewDay" ? "daily" : "hourly"); } catch { }
            }
        }
#endif
    }
}
