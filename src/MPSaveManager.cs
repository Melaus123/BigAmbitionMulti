using System;
using System.Collections.Generic;
using System.IO;

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
        private static string RootName => SimSeparateRoot() ? MpRootNameSim : MpRootName;
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

        /// <summary>Folder for one named MP save session.</summary>
        public static string MpSessionFolder(string sessionName)
            => Path.Combine(MpVersionFolder(), Sanitize(sessionName));

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
                File.WriteAllText(ManifestPath(sessionName), json);
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
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    var m = ReadManifest(name);
                    if (m != null) result.Add((name, m));
                }
                result.Sort((a, b) => b.Item2.SavedAtUnix.CompareTo(a.Item2.SavedAtUnix));
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] ListSessions: {ex.Message}"); }
            return result;
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
        }
        public class MpVariant
        {
            public string SessionName   = "";   // the actual session folder to load
            public string Kind          = "";   // Main / Autosave / Disconnect / Recover
            public int    Day           = -1;
            public long   SavedAtUnix;
            public string Players       = "";
            public string PlaythroughId = "";   // world identity from the manifest ("" = legacy/manifest-less)
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
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("_")) continue;   // internal/staging folders (_dcstage_*, etc.)
                    var v = new MpVariant { SessionName = name, Kind = ClassifyVariant(name) };
                    var m = ReadManifest(name);
                    if (m != null)
                    {
                        v.Day = m.WorldDay; v.SavedAtUnix = m.SavedAtUnix; v.PlaythroughId = m.PlaythroughId ?? "";
                        var names = new List<string>();
                        if (m.Slots != null) foreach (var s in m.Slots) names.Add(string.IsNullOrEmpty(s.CharacterName) ? s.DisplayName : s.CharacterName);
                        v.Players = names.Count > 0 ? string.Join(", ", names) : "";
                    }
                    else { try { v.SavedAtUnix = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir)).ToUnixTimeSeconds(); } catch { } }

                    string baseName = StripToBase(name);
                    if (!byBase.TryGetValue(baseName, out var pt)) { pt = new MpPlaythrough { Base = baseName }; byBase[baseName] = pt; }
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
                    MpVariant newestNamed = null, newestMain = null;
                    foreach (var v in pt.Variants)
                    {
                        if (v.SavedAtUnix > pt.NewestUnix) pt.NewestUnix = v.SavedAtUnix;
                        if (v.Day > pt.NewestDay) pt.NewestDay = v.Day;
                        if (!string.IsNullOrEmpty(v.Players) && (newestNamed == null || v.SavedAtUnix > newestNamed.SavedAtUnix)) newestNamed = v;
                        if (v.Kind == "Main" && (newestMain == null || v.SavedAtUnix > newestMain.SavedAtUnix)) newestMain = v;
                    }
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
            return (s.Length == 0 || s.Trim('.').Length == 0) ? "_" : s;
        }
    }
}
