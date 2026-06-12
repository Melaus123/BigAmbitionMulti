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
        public string GameVersion    { get; set; } = "";   // "EA 0.10" — guards against cross-version loads
        public long   SavedAtUnix    { get; set; }
        public int    WorldDay       { get; set; }          // fingerprint: in-game day at save
        public List<MpSlot> Slots    { get; set; } = new();
        /// <summary>addressKey → owner STABLE id (or "host"'s stable id).  The
        /// cross-machine ownership map, re-keyed from MPServer.BuildingOwners
        /// (which is keyed by the live, mutable PlayerId) to stable ids.</summary>
        public Dictionary<string, string> BuildingOwners { get; set; } = new();
    }

    public static class MPSaveManager
    {
        private const string MpRootName  = "_BAMP_MP";
        private const string ManifestName = "manifest.bamp.json";

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

        /// <summary>MP root for the current game version, a SIBLING of the SP
        /// version folder so the SP menu never lists it:
        /// ".../SaveGames/_BAMP_MP/EA 0.10".</summary>
        public static string MpVersionFolder()
        {
            string sp = SpVersionFolder();
            if (string.IsNullOrEmpty(sp)) return "";
            string root = Directory.GetParent(sp.TrimEnd('/', '\\'))?.FullName ?? sp;
            string version = Path.GetFileName(sp.TrimEnd('/', '\\'));
            return Path.Combine(root, MpRootName, version);
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
