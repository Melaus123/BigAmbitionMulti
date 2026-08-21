using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BigAmbitionsMP
{
    // ── Multiplayer save persistence (Phase 4) ────────────────────────────────
    //
    // Design (verified against the game's native save code):
    //  * The game serializes the ENTIRE game state to a .hsg via
    //    SaveGameManager.Save(SaveType, saveName, characterFolder) — we reuse it
    //    per-player rather than hand-serialize the (huge) world.
    //  * SP saves live under  <SaveGames>/<version>/<characterId>/<name>.hsg
    //    and the SP menu lists them via GetAllSaveGamesFromVersion(version).
    //  * MP saves go under a SIBLING root  <SaveGames>/_BAMP_MP/<version>/...
    //    which the SP menu never scans → MP progress can't be loaded in SP
    //    (anti-cheat), yet we can still list/load it by pointing the same game
    //    helper at the MP folder.
    //  * A per-MP-save manifest ties the per-player .hsg files into one session:
    //    session id, fingerprint, the cross-machine ownership map (keyed by the
    //    STABLE id, not the mutable display name), and one slot per player.

    /// <summary>One player's entry in an MP save session.</summary>
    public class MpSlot
    {
        public string StableId      { get; set; } = "";   // immutable identity (SteamID64 / guid-…)
        public string DisplayName   { get; set; } = "";   // PlayerId (persona) at save time — display only
        public string CharacterName { get; set; } = "";   // in-character name
        public string CharacterId   { get; set; } = "";   // the player's gi character id (their .hsg folder)
        public string SaveName      { get; set; } = "";   // the .hsg file name (no extension)
        public bool   IsHost        { get; set; }
        public int    Day           { get; set; }          // for display in the load list
        public float  Money         { get; set; }          // last-known cash (live-streamed) — reapplied on reconnect
    }

    /// <summary>Manifest for one MP save session — the MP-only state the per-player
    /// .hsg files don't capture.</summary>
    public class MpManifest
    {
        public int    Version        { get; set; } = 1;
        public string SessionId      { get; set; } = "";
        /// <summary>Identity of the WORLD this save belongs to — minted at the world's first save,
        /// carried across every save name, rename, and fork. Groups all of one world's named saves
        /// under one picker card, the way the native character folder does (2026-07-07). Empty on
        /// manifests from before the field existed (those group by base name).</summary>
        public string PlaythroughId  { get; set; } = "";
        public string GameVersion    { get; set; } = "";   // "EA 0.10" — guards against cross-version loads
        public long   SavedAtUnix    { get; set; }
        public int    WorldDay       { get; set; }          // fingerprint: in-game day at save
        public List<MpSlot> Slots    { get; set; } = new();
        /// <summary>addressKey → owner STABLE id (or "host"'s stable id).  The
        /// cross-machine ownership map, re-keyed from MPServer.BuildingOwners
        /// (which is keyed by the live, mutable PlayerId) to stable ids.</summary>
        public Dictionary<string, string> BuildingOwners { get; set; } = new();
        /// <summary>addressKey → owner STABLE id for BOUGHT real estate (re-keyed from
        /// MPServer.BuildingRealEstateOwners), so two players never own one building
        /// across save/reload.</summary>
        public Dictionary<string, string> BuildingRealEstateOwners { get; set; } = new();
        /// <summary>Player-to-player access GRANTS ("keys"), keyed by StableId so they survive
        /// renames + reloads (docs/PERMISSIONS-SYSTEM.md, Phase 1).</summary>
        public List<MpGrant> Grants { get; set; } = new();
        /// <summary>Merged-company membership (merger slice 1) — empty/absent = no merger.</summary>
        public List<MpMergerMember> Merger { get; set; } = new();
        /// <summary>Player-to-player loan ledger (field sweep 2026-08-18) — loans ride the
        /// manifest like grants, so each save slot carries the loans AS OF that moment and
        /// loading an older slot rolls them back with the world (timeline ruling).  NULL
        /// (absent) = manifest predates loan tracking → the legacy base-folder
        /// loans.bamp.json may be adopted once; non-null (even empty) is authoritative.</summary>
        public List<LoanEntry>? Loans { get; set; }
        /// <summary>Shared-wallet balance per merger group (slice 4) — absent on old manifests.</summary>
        public Dictionary<string, float> MergerWalletBalance { get; set; } = new();
        /// <summary>Per group: stable ids whose merge-time wallet pooling was already accepted, so a
        /// restore/join replay can never pool the same member's cash twice (slice 4).</summary>
        public Dictionary<string, List<string>> MergerWalletContributed { get; set; } = new();

        /// <summary>Round-53 — per-SAVE needs/morale authority (user design 2026-07-22): the load
        /// lobby mirrors these, the Customize panel edits them, and the edited values become the
        /// save's new authority. -1 = absent (pre-field manifest) → the lobby falls back to the
        /// host's current settings.</summary>
        public int TuneNeedsDrain  { get; set; } = -1;
        public int TuneRestSpeed   { get; set; } = -1;
        public int TuneMoraleTempo { get; set; } = -1;

        /// <summary>Handoff (slice 1, 2026-07-23): store provenance. LastHostStableId = who
        /// hosted when this manifest was written (stamped at every save-time metadata write);
        /// HostEpoch = host-start counter for the lineage (increments land in slice 2/4;
        /// 0 = pre-field manifest). Both travel inside every mirrored copy so a future host
        /// or joiner can see where the store came from.</summary>
        public int    HostEpoch        { get; set; } = 0;
        public string LastHostStableId { get; set; } = "";
    }

    /// <summary>One durable access grant: an owner gave a grantee a key (StableId space).</summary>
    public class MpGrant
    {
        public string    Owner       { get; set; } = "";                 // owner StableId
        public string    Grantee     { get; set; } = "";                 // grantee StableId
        public string    GranteeName { get; set; } = "";                 // last-known display name (for the owner's UI)
        public GrantKind Kind        { get; set; } = GrantKind.Vehicle;  // which asset kind (old manifests => Vehicle)
    }

    /// <summary>Merger slice 1 — one merged-company member (StableId-keyed like grants, so membership
    /// survives renames and offline members). Absent/empty list on old manifests = no merger.</summary>
    public class MpMergerMember
    {
        public string StableId { get; set; } = "";
        public string Name     { get; set; } = "";   // last-known display name (UI roster)
        public string Group    { get; set; } = "";   // merged-company id (several disjoint groups per session; "" on old manifests → folded into one legacy group)
    }

    public static class MPSaveManager
    {
        private const string MpRootName  = "_BAMP_MP";
        private const string ManifestName = "manifest.bamp.json";

#if BAMP_DEV
        // Dev separate-machine SIMULATION: when the client instance is launched with
        // BAMP_SIM_SEPARATE_SAVES=1, it redirects its ENTIRE MP save tree to a sibling root
        // ('_BAMP_MP_SIMCLIENT') so host+client on ONE machine no longer share save files — the host only
        // ever sees what the client UPLOADS over the network, exactly like real separate machines. Lets us
        // validate the separate-machine save/recovery paths (Phases 1-3) solo. Resolved once, cached.
        private const string MpRootNameSim = "_BAMP_MP_SIMCLIENT";
        private static int _simRoot = -1;   // -1 unresolved, 0 off, 1 on
        private static bool SimSeparateRoot()
        {
            if (_simRoot < 0)
            {
                try { _simRoot = string.Equals(Environment.GetEnvironmentVariable("BAMP_SIM_SEPARATE_SAVES"), "1", StringComparison.Ordinal) ? 1 : 0; }
                catch { _simRoot = 0; }
                if (_simRoot == 1) Plugin.Logger.LogWarning("[MPSave] SIM: separate-client save root ENABLED ('_BAMP_MP_SIMCLIENT') — dev separate-machine simulation.");
            }
            return _simRoot == 1;
        }
        // Store v2 M2 sandbox: BAMP_STORE_ROOT_OVERRIDE=<folderName> points the ENTIRE
        // MP store at a sibling root (e.g. a copy of the real store) so the migration
        // can be rehearsed and kill-tested without touching live data. Dev builds only;
        // takes precedence over the sim-client root.
        private static string? _rootOverride; private static int _rootOverrideState = -1;
        private static string? RootOverride()
        {
            if (_rootOverrideState < 0)
            {
                try { _rootOverride = Environment.GetEnvironmentVariable("BAMP_STORE_ROOT_OVERRIDE"); } catch { _rootOverride = null; }
                _rootOverrideState = string.IsNullOrEmpty(_rootOverride) ? 0 : 1;
                if (_rootOverrideState == 1)
                    Plugin.Logger.LogWarning($"[MPSave] DEV: store root OVERRIDDEN to '{_rootOverride}' (migration sandbox).");
            }
            return _rootOverrideState == 1 ? _rootOverride : null;
        }
        private static string RootName => RootOverride() ?? (SimSeparateRoot() ? MpRootNameSim : MpRootName);
#else
        private static string RootName => MpRootName;
#endif

        // ── Folder layout ─────────────────────────────────────────────────────

        // The SP version folder path, resolved ONCE on the main thread and cached.
        // CRITICAL: SaveGamePathHelper.CurrentVersionFolderPath() is an IL2CPP game
        // method — calling it off the main thread (e.g. on the network poll thread,
        // which reaches here via MpCharacterFolder when a client's uploaded save
        // arrives) faults the interop bridge → coreclr access violation.  So the
        // background path NEVER calls it; it uses this cache, populated on the main
        // thread by EnsureVersionCached().
        private static volatile string? _spVersionCache;

        /// <summary>Resolve + cache the SP version folder.  MUST be called from the
        /// Unity main thread (it touches IL2CPP).  Idempotent.</summary>
        public static void EnsureVersionCached()
        {
            if (_spVersionCache != null) return;
            try
            {
                var p = SaveGamePathHelper.CurrentVersionFolderPath()?.ToString();
                if (!string.IsNullOrEmpty(p)) _spVersionCache = p;
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] EnsureVersionCached: {ex.Message}"); }
        }

        /// <summary>The version folder the SP menu scans, e.g. ".../SaveGames/EA 0.10".
        /// Returns the cached value; only resolves via IL2CPP if not yet cached (which
        /// should only ever happen on the main thread — background callers must rely
        /// on the main thread having cached it first).</summary>
        private static string SpVersionFolder()
        {
            var cached = _spVersionCache;
            if (cached != null) return cached;
            EnsureVersionCached();
            return _spVersionCache ?? "";
        }

        /// <summary>The game version name (e.g. "EA 0.10"), from the cached path —
        /// safe on any thread once cached.</summary>
        public static string GameVersionName()
        {
            string sp = SpVersionFolder();
            return string.IsNullOrEmpty(sp) ? "" : Path.GetFileName(sp.TrimEnd('/', '\\'));
        }

        /// <summary>Game version name from the cache ONLY — never resolves via
        /// IL2CPP, so it is safe to call from the network poll thread (returns ""
        /// if the main thread hasn't cached it yet).  Used by the Hello version
        /// gate, which runs on the poll thread.</summary>
        public static string GameVersionNameCached()
        {
            var cached = _spVersionCache;
            return string.IsNullOrEmpty(cached) ? "" : Path.GetFileName(cached.TrimEnd('/', '\\'));
        }

        /// <summary>MP root for the current game version, a SIBLING of the SP
        /// version folder so the SP menu never lists it:
        /// ".../SaveGames/_BAMP_MP/EA 0.10".</summary>
        public static string MpVersionFolder()
        {
            string sp = SpVersionFolder();
            if (string.IsNullOrEmpty(sp)) return "";
            string root = Directory.GetParent(sp.TrimEnd('/', '\\'))?.FullName ?? sp;
            string version = Path.GetFileName(sp.TrimEnd('/', '\\'));
            return Path.Combine(root, RootName, version);
        }

        // ── Store format v2 (playthrough-scoped layout, 2026-08-01) ──────────────
        // v1 (flat):        <version>/<session>/...            — session NAME is the
        //                   machine-wide key; same-named worlds from different
        //                   playthroughs collide (the structural defect).
        // v2 (pid-scoped):  <version>/<pid>/<session>/...      — vanilla-shaped: one
        //                   folder per playthrough, named saves inside it.
        // A store carrying legacy flat sessions runs in v1 mode UNCHANGED until the
        // M2 migrator converts it; empty/new stores are born v2 (marker written).
        // Everything here is pure file/string IO — poll-thread safe (M0 constraint).

        internal const string StoreFormatMarkerName = "storeformat.bamp.json";
        private static int _fmtCache;   // 0 unknown, 1 flat, 2 pid-scoped
        private static readonly object _fmtLock = new();
        private static readonly Dictionary<string, string> _pidBySession =
            new(StringComparer.OrdinalIgnoreCase);
        private static volatile string _activePid = "";

        private static volatile string _activeBase = "";

        /// <summary>The live session's playthrough folder + lineage BASE name — set at
        /// host load / client join, cleared with the session. Round-218: any name in
        /// the active family resolves to the active folder UNCONDITIONALLY (no cache,
        /// no disk hunt) — the rig cross-world write happened because the auto-save
        /// rotation found a same-named sibling slot in ANOTHER world's folder.</summary>
        public static void SetActivePlaythrough(string pid, string baseName)
        {
            _activePid  = pid ?? "";
            _activeBase = baseName ?? "";
        }
        public static void ClearActivePlaythrough() { _activePid = ""; _activeBase = ""; }
        /// <summary>Round-222: the live world's folder id, for wire senders ("" between sessions).</summary>
        public static string ActivePlaythrough => _activePid;

        /// <summary>Round-218: forget every name→folder pin. Called when the world
        /// context changes (load / new world / join / session end) — a session name
        /// only means something inside one world, so pins must never outlive it.</summary>
        public static void ResetSessionPins()
        {
            lock (_pidBySession) _pidBySession.Clear();
        }

        /// <summary>Round-218: forget all pins EXCEPT the given family's — used at
        /// host load, where the picker has just pinned the clicked world and that pin
        /// must survive while everything from the previous world is dropped.</summary>
        public static void ResetSessionPinsExceptFamily(string baseName)
        {
            lock (_pidBySession)
            {
                var drop = _pidBySession.Keys
                    .Where(k => !string.Equals(StripToBase(k), baseName, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var k in drop) _pidBySession.Remove(k);
            }
        }

        /// <summary>Pin a session name to a playthrough folder — called where the pid
        /// is KNOWN (picker selection, wire payloads, mirror pieces) so name-only
        /// lookups afterwards are deterministic instead of scan-guessed.</summary>
        public static void NoteSessionPid(string sessionName, string pid)
        {
            if (string.IsNullOrEmpty(sessionName) || string.IsNullOrEmpty(pid)) return;
            lock (_pidBySession) _pidBySession[Sanitize(sessionName)] = Sanitize(pid);
        }

        /// <summary>Forget cached name→pid mappings (migration / tests).</summary>
        public static void ResetPidCache()
        {
            lock (_pidBySession) _pidBySession.Clear();
            _fmtCache = 0;
        }

        /// <summary>1 = legacy flat store (pre-migration; byte-identical behavior),
        /// 2 = playthrough-scoped. Unresolvable version folder → act v1, don't cache.</summary>
        public static int StoreFormat()
        {
            if (_fmtCache != 0) return _fmtCache;
            lock (_fmtLock)
            {
                if (_fmtCache != 0) return _fmtCache;
                string root = MpVersionFolder();
                if (string.IsNullOrEmpty(root)) return 1;
                try
                {
                    if (File.Exists(Path.Combine(root, StoreFormatMarkerName))) return _fmtCache = 2;
                    if (Directory.Exists(root))
                    {
                        foreach (var d in Directory.GetDirectories(root))
                        {
                            string n = Path.GetFileName(d);
                            if (n.StartsWith("_")) continue;
                            // A flat session: manifest directly inside, or a manifest-less
                            // legacy base holding character folders.
                            if (File.Exists(Path.Combine(d, ManifestName))) return _fmtCache = 1;
                            try
                            {
                                foreach (var c in Directory.GetDirectories(d))
                                {
                                    string cn = Path.GetFileName(c);
                                    if (cn.StartsWith("guid-") || cn.StartsWith("steam-")) return _fmtCache = 1;
                                }
                            }
                            catch { }
                        }
                    }
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, StoreFormatMarkerName),
                        "{\"format\":2,\"born\":\"v2\",\"createdUnix\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}");
                    Plugin.Logger.LogInfo("[MPSave] Store format: new store initialized as v2 (playthrough-scoped).");
                    return _fmtCache = 2;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[MPSave] StoreFormat probe: {ex.Message} — behaving as v1 this run.");
                    return _fmtCache = 1;
                }
            }
        }

        /// <summary>v2: which playthrough folder a session name lives in. Order:
        /// pinned mapping → existing folder on disk (active playthrough's copy wins;
        /// else newest, LOUD on ambiguity per decision F) → the active playthrough
        /// (write for the live family) → minted last resort (loud).</summary>
        private static string ResolvePidFolder(string sanitizedSession)
        {
            string s = sanitizedSession;
            // Round-218: the ACTIVE world's own family NEVER resolves anywhere else —
            // checked before cache and disk, so a same-named session in another
            // world's folder can never capture a sibling read or write.
            string ap0 = _activePid, ab0 = _activeBase;
            if (ap0.Length > 0 && ab0.Length > 0
                && string.Equals(StripToBase(s), ab0, StringComparison.OrdinalIgnoreCase))
                return ap0;
            lock (_pidBySession) { if (_pidBySession.TryGetValue(s, out var hit)) return hit; }
            string ap = _activePid;
            try
            {
                string root = MpVersionFolder();
                if (Directory.Exists(root))
                {
                    string found = ""; long foundAt = -1; int matches = 0;
                    foreach (var d in Directory.GetDirectories(root))
                    {
                        string pidName = Path.GetFileName(d);
                        if (pidName.StartsWith("_")) continue;
                        if (!Directory.Exists(Path.Combine(d, s))) continue;
                        matches++;
                        if (pidName == ap && ap.Length > 0) { found = pidName; foundAt = long.MaxValue; continue; }
                        long t = 0;
                        try { t = new DateTimeOffset(Directory.GetLastWriteTimeUtc(Path.Combine(d, s))).ToUnixTimeSeconds(); } catch { }
                        if (t > foundAt) { found = pidName; foundAt = t; }
                    }
                    if (matches > 1)
                        Plugin.Logger.LogWarning($"[MPSave] Session name '{s}' exists in {matches} playthroughs — using '{found}'. Name-only lookup; the caller should pin the playthrough (NoteSessionPid).");
                    if (matches > 0)
                    {
                        lock (_pidBySession) _pidBySession[s] = found;
                        return found;
                    }
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(ap)) return ap;   // new write for the live family
            // Full miss with NO active session: NEVER invent state — round-216 root
            // cause was minting+caching here on mere existence probes (auto-rotation
            // and lineage checks), which scattered one new world across 14 folders.
            // Reads of this path are harmless (File.Exists=false); a WRITE landing in
            // '_unresolved' is itself the loud defect signal (walkers skip "_").
            if (_unresolvedWarned.Add(s))
                Plugin.Logger.LogWarning($"[MPSave] No playthrough context for session '{s}' — resolving under '_unresolved' (not cached; reads harmless, a write here is a defect).");
            return "_unresolved";
        }
        private static readonly HashSet<string> _unresolvedWarned = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _pidMismatchWarned = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _noSavesSkipLogged = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>v2: find the playthrough folder already holding any member of a
        /// session FAMILY (base name + variant suffixes) — the disk-truth fallback for
        /// activating a world whose own manifest is missing. Newest wins on a tie,
        /// loudly. Pure IO, writes nothing, caches nothing. "" = family not on disk.</summary>
        public static string FindFamilyPidOnDisk(string baseName)
        {
            try
            {
                if (StoreFormat() != 2 || string.IsNullOrEmpty(baseName)) return "";
                string root = MpVersionFolder();
                if (!Directory.Exists(root)) return "";
                string found = ""; long foundAt = -1; int matches = 0;
                foreach (var pidDir in Directory.GetDirectories(root))
                {
                    string pid = Path.GetFileName(pidDir);
                    if (pid.StartsWith("_")) continue;
                    foreach (var dir in Directory.GetDirectories(pidDir))
                    {
                        if (!string.Equals(StripToBase(Path.GetFileName(dir)), baseName, StringComparison.OrdinalIgnoreCase)) continue;
                        matches++;
                        long t = 0;
                        try { t = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir)).ToUnixTimeSeconds(); } catch { }
                        if (t > foundAt) { found = pid; foundAt = t; }
                        break;   // one hit per pid dir is enough
                    }
                }
                if (matches > 1)
                    Plugin.Logger.LogWarning($"[MPSave] Family '{baseName}' found in {matches} playthrough folders — activating '{found}' (newest). If this recurs, the store needs repair.");
                return found;
            }
            catch { return ""; }
        }

        /// <summary>Folder for one named MP save session.</summary>
        public static string MpSessionFolder(string sessionName)
        {
            string s = Sanitize(sessionName);
            if (StoreFormat() == 1) return Path.Combine(MpVersionFolder(), s);
            return Path.Combine(MpVersionFolder(), ResolvePidFolder(s), s);
        }

        /// <summary>Per-player character folder inside a session (where their .hsg
        /// goes).  Keyed by the player's character id so it mirrors the game's own
        /// version/characterId/name.hsg layout (GetAllSaveGamesFromVersion expects
        /// character-id subfolders).</summary>
        public static string MpCharacterFolder(string sessionName, string characterId)
        {
            string f = Path.Combine(MpSessionFolder(sessionName), Sanitize(characterId));
            Directory.CreateDirectory(f);
            return f;
        }

        public static string ManifestPath(string sessionName)
            => Path.Combine(MpSessionFolder(sessionName), ManifestName);

        // ── Manifest IO ───────────────────────────────────────────────────────

        public static void WriteManifest(string sessionName, MpManifest m)
        {
            try
            {
                Directory.CreateDirectory(MpSessionFolder(sessionName));
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(m, Newtonsoft.Json.Formatting.Indented);
                // Round-274 (user-approved): ATOMIC write — the old in-place truncating write
                // let a concurrent reader observe a half-written manifest, which ReadManifest
                // swallows into null (reviewer-confirmed mechanism).  Write beside, then swap:
                // a reader sees the old complete file or the new complete file, never a torn one.
                string path = ManifestPath(sessionName);
                // Round-274c: unique temp name — concurrent writers of one manifest must not
                // interleave on a shared ".tmp" (verifier PLAUSIBLE-5 second-order).
                string tmp  = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try
                {
                    File.WriteAllText(tmp, json);
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                Plugin.Logger.LogInfo($"[MPSave] Wrote manifest '{sessionName}' ({m.Slots.Count} slot(s), {m.BuildingOwners.Count} owned).");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] WriteManifest '{sessionName}': {ex.Message}"); }
        }

        public static MpManifest? ReadManifest(string sessionName)
        {
            try
            {
                string p = ManifestPath(sessionName);
                if (!File.Exists(p)) return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<MpManifest>(File.ReadAllText(p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ReadManifest '{sessionName}': {ex.Message}"); return null; }
        }

        /// <summary>List all MP save sessions (folders under the MP root that
        /// contain a manifest), newest first.</summary>
        public static List<(string Name, MpManifest Manifest)> ListSessions()
        {
            var result = new List<(string, MpManifest)>();
            try
            {
                string root = MpVersionFolder();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;
                foreach (var (name, dir, pid) in WalkSessionDirs(root))
                {
                    var m = ReadManifestAt(dir);
                    if (m == null) continue;
                    // v2: the folder is the identity's ground truth — backfill a legacy
                    // manifest so every consumer can disambiguate same-named sessions.
                    if (pid.Length > 0 && string.IsNullOrEmpty(m.PlaythroughId)) m.PlaythroughId = pid;
                    result.Add((name, m));
                }
                result.Sort((a, b) => b.Item2.SavedAtUnix.CompareTo(a.Item2.SavedAtUnix));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ListSessions: {ex.Message}"); }
            return result;
        }

        /// <summary>Session directories of the store in its CURRENT format —
        /// (name, fullPath, pidFolder) with pidFolder "" on a v1 store. "_"-prefixed
        /// folders (staging, migration, backup) are skipped at every level.</summary>
        private static IEnumerable<(string Name, string Dir, string Pid)> WalkSessionDirs(string root)
        {
            if (StoreFormat() == 1)
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("_")) continue;
                    yield return (name, dir, "");
                }
                yield break;
            }
            foreach (var pidDir in Directory.GetDirectories(root))
            {
                string pid = Path.GetFileName(pidDir);
                if (pid.StartsWith("_")) continue;
                foreach (var dir in Directory.GetDirectories(pidDir))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("_")) continue;
                    yield return (name, dir, pid);
                }
            }
        }

        /// <summary>Read a manifest by exact folder path (walkers use this so a
        /// same-named session in another playthrough can never be confused in).</summary>
        internal static MpManifest? ReadManifestAt(string sessionDir)
        {
            try
            {
                string p = Path.Combine(sessionDir, ManifestName);
                if (!File.Exists(p)) return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<MpManifest>(File.ReadAllText(p));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ReadManifestAt '{sessionDir}': {ex.Message}"); return null; }
        }

        // ── Playthrough grouping (load-screen model) ───────────────────────────

        /// <summary>One playthrough = a base session + its variant saves (Main / Autosave / Disconnect /
        /// Recover), for the grouped load screen.</summary>
        public class MpPlaythrough
        {
            public string Base = "";
            public List<MpVariant> Variants = new();
            public long   NewestUnix;
            public int    NewestDay = -1;
            public string Players   = "—";
            /// <summary>Handoff slice 2: who last hosted this world (from the newest variant's
            /// manifest) — the picker labels a store whose last host is someone else as a
            /// shared/mirrored world. Empty on pre-field manifests.</summary>
            public string LastHostStableId = "";
            public string LastHostName     = "";
        }
        public class MpVariant
        {
            public string SessionName   = "";   // the actual session folder to load
            public string Kind          = "";   // Main / Autosave / Disconnect / Recover
            public int    Day           = -1;
            public long   SavedAtUnix;
            public string Players       = "";
            public string PlaythroughId = "";   // world identity from the manifest ("" = legacy/manifest-less)
            public string SessionDir    = "";   // round-225: the EXACT folder this variant was walked from (delete/portrait use this, never name resolution)
            public string LastHostStableId = "";   // handoff slice 2: manifest provenance
            public string LastHostName     = "";
        }

        /// <summary>TRUE if any session folder in the current store holds at least one actual
        /// save file — the "may Host Saved Game be clicked" probe (user-approved 2026-08-21:
        /// with nothing loadable the button greys out, vanilla-style, so the empty window is
        /// unreachable). First hit short-circuits; same file-based predicate as the picker.</summary>
        public static bool AnyLoadableSaves()
        {
            try
            {
                string root = MpVersionFolder();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
                foreach (var (_, dir, _) in WalkSessionDirs(root))
                {
                    try
                    {
                        var probe = SaveGamePathHelper.GetAllSaveGamesFromVersion(dir);
                        if (probe != null && probe.Count > 0) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>Group MP saves into PLAYTHROUGHS for the load screen: one entry per base session, each
        /// holding its variants newest-playthrough-first; variants ordered Main→Autosave→Disconnect→Recover.
        /// Includes manifest-LESS recover saves (dated by folder mtime, players borrowed from siblings).
        /// Pure file/JSON IO (no IL2CPP) — same off-thread safety as ListSessions.</summary>
        public static List<MpPlaythrough> ListPlaythroughs()
        {
            var byBase = new Dictionary<string, MpPlaythrough>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string root = MpVersionFolder();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new List<MpPlaythrough>();
                foreach (var (name, dir, pidFolder) in WalkSessionDirs(root))
                {
                    // User ruling 2026-08-21: a folder holding zero actual save files is not
                    // a save — never listed. (Vanilla derives its list from the files, so an
                    // empty folder is invisible there by construction; ours came from catalogs
                    // and could list a catalog-only shell as a loadable "Main" row.) Same
                    // source predicate as ResolveOwnSlot's "no saves" refusal, so the picker
                    // and the load validator can never disagree about "has saves".
                    List<SaveGameManager.SaveGameStruct>? probe = null;
                    try { probe = SaveGamePathHelper.GetAllSaveGamesFromVersion(dir); } catch { }
                    if (probe == null || probe.Count == 0)
                    {
                        if (_noSavesSkipLogged.Add(dir))
                            Plugin.Logger.LogWarning($"[MPSave] '{name}' holds no save files — catalog-only folder, not listed.");
                        continue;
                    }
                    // User-approved 2026-08-21: the variant's DATE comes from the save FILES
                    // (File.GetLastWriteTime via the native enumeration above), never from the
                    // catalog's SavedAtUnix claim — the file's own clock can't disagree with
                    // the file. Claim failures this sidesteps: crash between save-write and
                    // catalog-write; minted-at-load manifests (SavedAtUnix 0); folder-mtime
                    // blindness to subfolder changes. The catalog still supplies day, roster
                    // and world identity below.
                    long newestFileUnix = 0;
                    foreach (var s in probe)
                    {
                        try
                        {
                            long u = new DateTimeOffset(s.lastPlayedDate.ToUniversalTime()).ToUnixTimeSeconds();
                            if (u > newestFileUnix) newestFileUnix = u;
                        }
                        catch { }
                    }
                    var v = new MpVariant { SessionName = name, Kind = ClassifyVariant(name), SessionDir = dir };
                    var m = ReadManifestAt(dir);
                    if (m != null)
                    {
                        v.Day = m.WorldDay; v.SavedAtUnix = newestFileUnix > 0 ? newestFileUnix : m.SavedAtUnix; v.PlaythroughId = m.PlaythroughId ?? "";
                        // Round-218 watchdog: contents claiming a different world than the
                        // folder they sit in = cross-world write or legacy contamination.
                        if (pidFolder.Length > 0 && !string.IsNullOrEmpty(m.PlaythroughId) && m.PlaythroughId != pidFolder
                            && _pidMismatchWarned.Add(pidFolder + "/" + name))
                            Plugin.Logger.LogWarning($"[MPSave] STORE MISMATCH: '{name}' sits in playthrough folder {pidFolder} but its manifest claims {m.PlaythroughId} — cross-world write or legacy contamination.");
                        // v2: the containing folder IS the world identity — it wins over a
                        // stale/empty manifest field, so grouping can never straddle folders.
                        if (pidFolder.Length > 0) v.PlaythroughId = pidFolder;
                        var names = new List<string>();
                        if (m.Slots != null) foreach (var s in m.Slots) names.Add(string.IsNullOrEmpty(s.CharacterName) ? s.DisplayName : s.CharacterName);
                        v.Players = names.Count > 0 ? string.Join(", ", names) : "";
                        // Handoff slice 2: provenance — resolve the last host's display
                        // name from the manifest's own slots (StableId-keyed).
                        v.LastHostStableId = m.LastHostStableId ?? "";
                        if (v.LastHostStableId.Length > 0 && m.Slots != null)
                        {
                            var hs = m.Slots.Find(s => s.StableId == v.LastHostStableId);
                            if (hs != null) v.LastHostName = string.IsNullOrEmpty(hs.CharacterName) ? hs.DisplayName : hs.CharacterName;
                        }
                    }
                    else
                    {
                        v.SavedAtUnix = newestFileUnix;
                        if (v.SavedAtUnix <= 0)
                            try { v.SavedAtUnix = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir)).ToUnixTimeSeconds(); } catch { }
                        // v2: a manifest-less variant (recover folders) still groups with its
                        // family — the folder it sits in names the world.
                        if (pidFolder.Length > 0) v.PlaythroughId = pidFolder;
                    }

                    string baseName = StripToBase(name);
                    // Round-220: in v2 the name-family bundle must stay WITHIN its world —
                    // keying by base name alone fused same-named sessions from different
                    // worlds onto one card (rig: two 'save' rows, day 2 + day 131, on the
                    // test-playthrough card, both highlighting). The later pid-merge step
                    // still folds one world's many base-groups into its single card.
                    string groupKey = pidFolder.Length > 0 ? pidFolder + "|" + baseName : baseName;
                    if (!byBase.TryGetValue(groupKey, out var pt)) { pt = new MpPlaythrough { Base = baseName }; byBase[groupKey] = pt; }
                    pt.Variants.Add(v);
                }

                // Merge base-name groups that belong to the same WORLD (native parity 2026-07-07: the
                // character folder groups every named save of a playthrough; ours is the PlaythroughId
                // minted at the world's first save). A group's id = its newest id-bearing variant, so a
                // legacy sibling (-auto written before the field existed) can't split its base apart.
                // Groups with no id at all (pre-field saves, manifest-less folders) keep grouping by
                // base name, exactly as before.
                var merged = new Dictionary<string, MpPlaythrough>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in byBase)
                {
                    string pid = ""; long pidWhen = -1;
                    foreach (var v in kv.Value.Variants)
                        if (!string.IsNullOrEmpty(v.PlaythroughId) && v.SavedAtUnix > pidWhen) { pid = v.PlaythroughId; pidWhen = v.SavedAtUnix; }
                    string key = pid.Length > 0 ? "pid:" + pid : "base:" + kv.Key;
                    if (!merged.TryGetValue(key, out var dst)) merged[key] = kv.Value;
                    else dst.Variants.AddRange(kv.Value.Variants);
                }
                byBase = merged;

                foreach (var pt in byBase.Values)
                {
                    MpVariant newestNamed = null, newestMain = null, newestHosted = null;
                    foreach (var v in pt.Variants)
                    {
                        if (v.SavedAtUnix > pt.NewestUnix) pt.NewestUnix = v.SavedAtUnix;
                        if (v.Day > pt.NewestDay) pt.NewestDay = v.Day;
                        if (!string.IsNullOrEmpty(v.Players) && (newestNamed == null || v.SavedAtUnix > newestNamed.SavedAtUnix)) newestNamed = v;
                        if (v.Kind == "Main" && (newestMain == null || v.SavedAtUnix > newestMain.SavedAtUnix)) newestMain = v;
                        if (!string.IsNullOrEmpty(v.LastHostStableId) && (newestHosted == null || v.SavedAtUnix > newestHosted.SavedAtUnix)) newestHosted = v;
                    }
                    if (newestHosted != null) { pt.LastHostStableId = newestHosted.LastHostStableId; pt.LastHostName = newestHosted.LastHostName; }
                    // A merged card holds several save NAMES — headline it by the newest manual save,
                    // the native character-card rule (headline = newest non-recover save).
                    if (newestMain != null) pt.Base = StripToBase(newestMain.SessionName);
                    pt.Players = newestNamed != null ? newestNamed.Players : "—";
                    foreach (var v in pt.Variants) if (string.IsNullOrEmpty(v.Players)) v.Players = pt.Players;   // recover borrows the run's roster
                    pt.Variants.Sort((a, b) =>
                    {
                        int o = VariantOrder(a.Kind).CompareTo(VariantOrder(b.Kind));
                        return o != 0 ? o : b.SavedAtUnix.CompareTo(a.SavedAtUnix);   // same kind → newest first (round-37: checkpoint stacks)
                    });
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ListPlaythroughs: {ex.Message}"); }
            var result = new List<MpPlaythrough>(byBase.Values);
            result.Sort((a, b) => b.NewestUnix.CompareTo(a.NewestUnix));   // newest playthrough first
            return result;
        }

        private static string ClassifyVariant(string name)
        {
            // '-cp-'/'-cpa-' creation RETIRED 2026-07-07 (native parity — see MPSaveCoordinator);
            // classification stays so folders already on disk remain listed and loadable.
            if (name.Contains("-cpa-"))       return "Auto checkpoint";
            if (name.Contains("-cp-"))        return "Checkpoint";
            if (name.EndsWith("-recover"))    return "Recover";      // covers -recover / -auto-recover / -disconnect-recover
            if (name.EndsWith("-disconnect")) return "Disconnect";
            if (name.EndsWith("-auto"))       return "Autosave";
            if (NumberedAutoIndex(name) > 0)  return "Autosave";     // '-auto-2'.. rotation slots (2026-07-07)
            return "Main";
        }

        /// <summary>Strip every automatic-save suffix to the playthrough base name.</summary>
        public static string StripToBase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            // Legacy round-37 checkpoints: '<base>-cp-<stamp>' / '<base>-cpa-<stamp>' — cut at the marker.
            int cpa = name.IndexOf("-cpa-", StringComparison.Ordinal);
            if (cpa > 0) return name.Substring(0, cpa);
            int cp = name.IndexOf("-cp-", StringComparison.Ordinal);
            if (cp > 0) return name.Substring(0, cp);
            foreach (var suf in new[] { "-auto-recover", "-disconnect-recover", "-recover", "-disconnect", "-auto" })
                if (name.EndsWith(suf)) return name.Substring(0, name.Length - suf.Length);
            int na = NumberedAutoIndex(name);
            if (na > 0) return name.Substring(0, na);   // '-auto-2'.. rotation slots
            return name;
        }

        /// <summary>Index of a trailing '-auto-&lt;digits&gt;' rotation suffix, or -1. All-digit tail
        /// required so a base name containing '-auto-' text is never mangled (mirror of the
        /// coordinator's check — kept local so this class parses names on its own).</summary>
        private static int NumberedAutoIndex(string name)
        {
            int i = name.LastIndexOf("-auto-", StringComparison.Ordinal);
            if (i <= 0 || i + 6 >= name.Length) return -1;
            for (int k = i + 6; k < name.Length; k++)
                if (name[k] < '0' || name[k] > '9') return -1;
            return i;
        }

        private static int VariantOrder(string kind)
        {
            if (kind == "Main")            return 0;
            if (kind == "Checkpoint")      return 1;   // the user's frozen manual saves, right under Main
            if (kind == "Autosave")        return 2;
            if (kind == "Auto checkpoint") return 3;
            if (kind == "Disconnect")      return 4;
            if (kind == "Recover")         return 5;
            return 9;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Make a network- or user-supplied string safe as a single
        /// path COMPONENT: invalid filename chars replaced, and dot-only names
        /// ("." / "..") neutralized — those are directory steps, not names, and
        /// they survive the invalid-char filter.</summary>
        internal static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Trim();
            // Windows strips trailing dots/spaces when CREATING a directory but file opens
            // inside the dotted name still fail — a session named for a character like
            // "…Jr." got a folder "…Jr" it could never address again, and every manifest/
            // ledger/save write failed all session (field 20260816-112127, sweep 2026-08-18).
            s = s.TrimEnd('.', ' ');
            return (s.Length == 0 || s.Trim('.').Length == 0) ? "_" : s;
        }
    }
}
