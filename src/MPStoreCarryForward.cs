using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace BigAmbitionsMP
{
    /// <summary>Game-version carry-forward for the MP save store (user-approved 2026-08-21;
    /// lean-A hardening after the Opus verification, same day). The store lives under
    /// SaveGames/_BAMP_MP/&lt;GameVersion&gt;/ — after a game update the mod looks in the NEW
    /// version's folder, which doesn't exist yet, and every MP save silently vanishes from
    /// the picker (audit #14; vanilla solves this with an upgrade prompt + copy).
    ///
    /// Vanilla-parity semantics, automatic trigger, interruption-proof:
    ///  * COPY, never move — vanilla's CopySaveGamesBetweenPreviousAndCurrentVersion is a
    ///    recursive skip-existing copy (SaveGameCompatibilityHelper.cs, read 2026-08-21).
    ///    The old version's store stays untouched: a game rollback still finds its saves,
    ///    and a failed copy loses nothing. Players may delete the old folder by hand once
    ///    the new version is proven (the finish log names it).
    ///  * A RECORD file in the target root ({Source, Finished}) is the done-flag: written
    ///    before the copy, marked finished after. An interrupted copy RESUMES next launch
    ///    (verifier defect 1 — the old HasSessionDirs trigger could never resume).
    ///  * Every file copies under a temp name and is renamed into place, so a torn file
    ///    cannot survive an interruption and skip-existing only ever sees whole files.
    ///  * Source found vanilla's way: version numbers walked downward (verifier defect 5 —
    ///    newest-folder-mtime could pick a once-touched ancient store); mtime scan only as
    ///    fallback when the native statics are unavailable.
    ///  * A PRE-v2 (flat) source copies SYNCHRONOUSLY — the store-format probe could
    ///    otherwise mis-mark the root v2 while flat sessions were still landing (verifier
    ///    defect 2); those stores predate 2026-08 and are small. v2 sources (their marker
    ///    copies first) run on a background thread so a large store can't stall the menu.
    ///  * The migrator is held off while a carry is pending (Busy — verifier defect 3).
    ///  * A source holding a pending migration journal is reported loudly and carried
    ///    as-is (rarest compound case; handled by hand if ever seen — user 2026-08-21).
    ///  * Save-format conversion is NOT our job: the game modernizes old saves at load
    ///    (SaveGameCompatibilityFixes ladder, SaveGameManager.cs:312).</summary>
    internal static class MPStoreCarryForward
    {
        private const string RecordName    = "_carry.bamp.json";
        private const string SkipJournal   = "_migrate_journal.json";
        /// <summary>Our own breadcrumb at the STORE root naming the version folder we last used. The
        /// leading underscore keeps it out of HasSessionDirs and every sibling scan. This is the only
        /// source rule that composes no name and calls no game API, so it survives any renaming a 1.0
        /// release invents — but it only exists for players who launched once on a build that writes
        /// it, which is why the version-number rank still has to carry the general case.</summary>
        private const string PointerName   = "_laststore.bamp.json";
        private static bool _ran;
        private static int  _versionFailures;
        internal static volatile bool InProgress;

        /// <summary>TRUE until this launch's carry decision is made AND any launched copy has
        /// finished — the migrator must not run concurrently with the copy's writes.</summary>
        internal static bool Busy => !_ran || InProgress;

        private class CarryRecord
        {
            public string Source  { get; set; } = "";
            public bool   Finished { get; set; }
            /// <summary>HOW the source was chosen: "pointer" | "walk" | "rank" | "mtime". Recorded
            /// because Finished short-circuits every later launch — a carry sourced by a weak rule must
            /// stay VISIBLE and re-runnable rather than silently final (review MAJOR-3).</summary>
            public string Via        { get; set; } = "";
            public int    Candidates { get; set; }
        }

        private class StorePointer
        {
            public string Version { get; set; } = "";
            public long   AtUnix  { get; set; }
        }

        /// <summary>Called every frame from the menu canvas (main thread) — the check runs
        /// once per launch, on the first frame the version folder resolves; the copy, when
        /// needed, runs on a background thread (v2 source) or inline (flat source). Cheap
        /// no-op afterwards.</summary>
        public static void RunIfNeeded()
        {
            if (_ran) return;
            string root;
            try { root = MPSaveManager.MpVersionFolder(); }
            catch (Exception vex)
            {
                // Review MINOR-5: a PERSISTENT throw here left _ran false forever, so Busy never cleared
                // and MPStoreMigration — gated on it — never ran again in ANY session. A transient
                // warm-up throw still retries; a durable one gives up loudly and releases the interlock.
                if (++_versionFailures == 1)
                    Plugin.Logger.LogWarning($"[MPSave] carry-forward: version folder unavailable ({vex.Message}) — retrying.");
                if (_versionFailures >= 120)
                {
                    _ran = true;   // releases Busy so the store migrator can still run
                    Plugin.Logger.LogError($"[MPSave] CARRY-FORWARD DISABLED this session: the version folder never resolved after {_versionFailures} attempts ({vex.Message}). Nothing was carried; every existing save is untouched.");
                }
                return;
            }
            if (string.IsNullOrEmpty(root)) return;   // version cache not warm yet — retry next frame
            _ran = true;
            try
            {
                string recPath = Path.Combine(root, RecordName);
                CarryRecord rec = null;
                try { if (File.Exists(recPath)) rec = Newtonsoft.Json.JsonConvert.DeserializeObject<CarryRecord>(File.ReadAllText(recPath)); } catch { }

                string source; string via = ""; int candidates = 0;
                if (rec != null)
                {
                    if (rec.Finished)
                    {
                        // Carried in a prior launch. A carry sourced by a WEAK rule must not pass in
                        // silence (review MAJOR-3): say so every launch and name the one-step redo, so a
                        // wrong pick stays recoverable instead of frozen forever. The redo is safe by
                        // construction — CopyTree only ever ADDS files that are not already present.
                        if (rec.Via == "rank" || rec.Via == "mtime")
                            Plugin.Logger.LogWarning($"[MPSave] carry-forward: this store was carried from '{Path.GetFileName(rec.Source)}' by the '{rec.Via}' fallback ({rec.Candidates} candidate(s)), NOT by the version walk. If those are the wrong saves, delete '{recPath}' and relaunch to re-run the search — nothing is deleted either way.");
                        return;
                    }
                    source = rec.Source;        // interrupted copy — RESUME (skip-existing)
                    via = rec.Via ?? ""; candidates = rec.Candidates;
                    if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
                    { Plugin.Logger.LogError($"[MPSave] CARRY-FORWARD: resume record points at a missing source ('{source}') — abandoning the resume; report this log."); return; }
                    Plugin.Logger.LogWarning($"[MPSave] CARRY-FORWARD: resuming an interrupted copy from '{Path.GetFileName(source)}' (skip-existing).");
                }
                else
                {
                    string mpRoot = Directory.GetParent(root.TrimEnd('/', '\\'))?.FullName;
                    string current = Path.GetFileName(root.TrimEnd('/', '\\'));
                    if (string.IsNullOrEmpty(mpRoot) || !Directory.Exists(mpRoot)) return;
                    if (HasSessionDirs(root))
                    {
                        // Populated store, no record → born here; nothing to carry. Refresh the pointer
                        // so the NEXT version finds this folder BY NAME instead of composing one.
                        WritePointer(mpRoot, current);
                        return;
                    }
                    source = FindPreviousStore(mpRoot, current, out via, out candidates);
                    if (source == null) return;         // fresh install — no previous store
                    Directory.CreateDirectory(root);
                    File.WriteAllText(recPath, Newtonsoft.Json.JsonConvert.SerializeObject(new CarryRecord { Source = source, Finished = false, Via = via, Candidates = candidates }));
                    Plugin.Logger.LogWarning($"[MPSave] CARRY-FORWARD: current store '{current}' is empty — copying the '{Path.GetFileName(source)}' MP store forward (originals untouched).");
                }

                try
                {
                    if (File.Exists(Path.Combine(source, SkipJournal)))
                        Plugin.Logger.LogError($"[MPSave] CARRY-FORWARD: source '{source}' holds a PENDING migration journal (the old version crashed mid-migration) — carrying as-is; flat leftovers may be invisible. REPORT THIS LOG.");
                }
                catch { }

                bool srcIsV2 = false;
                try { srcIsV2 = File.Exists(Path.Combine(source, MPSaveManager.StoreFormatMarkerName)); } catch { }
                InProgress = true;
                string src = source, viaCap = via; int candCap = candidates;
                Action work = () =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var (copied, skipped, bytes) = CopyTree(src, root, atRoot: true);
                        // The pointer names the folder we just populated, so the NEXT version can find
                        // it without composing a name or calling a game API.
                        try { WritePointer(Directory.GetParent(root.TrimEnd('/', '\\'))?.FullName ?? "", Path.GetFileName(root.TrimEnd('/', '\\'))); } catch { }
                        try { File.WriteAllText(recPath, Newtonsoft.Json.JsonConvert.SerializeObject(new CarryRecord { Source = src, Finished = true, Via = viaCap, Candidates = candCap })); }
                        catch (Exception mex) { Plugin.Logger.LogError($"[MPSave] CARRY-FORWARD: copy complete but the finish record failed to write ({mex.Message}) — next launch re-runs a harmless skip-existing pass."); }
                        Plugin.Logger.LogWarning($"[MPSave] CARRY-FORWARD done: {copied} file(s), {bytes / (1024.0 * 1024.0):F1} MB in {sw.ElapsedMilliseconds} ms ({skipped} already present). Old store kept at '{src}' — safe to delete by hand once the new version is proven.");
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] CARRY-FORWARD interrupted ({ex.Message}) — the record stands, so the resume runs next launch (skip-existing)."); }
                    finally { InProgress = false; }
                };
                if (srcIsV2) Task.Run(work);
                else
                {
                    Plugin.Logger.LogWarning("[MPSave] CARRY-FORWARD: source store is pre-v2 (flat) — copying synchronously so the format probe can never mis-mark a half-landed store.");
                    work();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] carry-forward check failed: {ex.Message}"); InProgress = false; }
        }

        // ── 1.0 hardening: the two native touches are ISOLATED ───────────────────────────────
        // A compile-time binding to a runtime-ABSENT member throws at JIT/prepare time in the ENCLOSING
        // frame — this project's own documented failure class (VehicleStoragePanel.cs:226-229, where a
        // removed LocalizorManager overload broke an unrelated method; remedy at :239 and :508).
        // Retiring GetEarlyAccessVersionString is close to the definition of leaving Early Access, so
        // both touches sit behind NoInlining calls: the throw then lands at the CALL SITE, inside
        // FindPreviousStore's try, and the ladder below is actually reached instead of the whole method
        // failing before its first line (review MAJOR-1).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int NativeVersionInt() => MainMenuController.currentVersion.version;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string NativeVersionFolderName(int n) => GameVersion.GetEarlyAccessVersionString(n);

        /// <summary>Name the store to carry forward, by four rules in DESCENDING trust. Every rule only
        /// ever NAMES a folder — nothing in here writes, moves or deletes anything.
        ///
        ///  1. POINTER — our own breadcrumb naming the folder we last used. Composes no name and calls
        ///     no game API, so it survives any renaming 1.0 invents. Absent on a first upgrade.
        ///  2. WALK — vanilla's rule (SaveGameCompatibilityHelper.cs:7-19): count the version int down
        ///     and compose "EA 0.{n}". Correct for as long as that naming holds.
        ///  3. RANK — parse the version NUMBER out of each sibling folder's name and take the highest.
        ///     Historical folders keep their old names whatever 1.0 renames the CURRENT one, so this is
        ///     deterministic and needs no prior state. This is the general case.
        ///  4. MTIME — last resort, and announced as a guess. A directory's timestamp records when a
        ///     DIRECT child was added or removed, not deep writes; for a store shaped
        ///     &lt;version&gt;/&lt;pid&gt;/&lt;session&gt;/&lt;files&gt; that means playing NEVER touches
        ///     it, so it does not track "most recently played" (review MAJOR-2, measured).</summary>
        private static string FindPreviousStore(string mpRoot, string current, out string via, out int candidates)
        {
            via = ""; candidates = 0;

            // 1 ── our own breadcrumb
            try
            {
                string want = ReadPointer(mpRoot);
                if (want.Length > 0 && !string.Equals(want, current, StringComparison.OrdinalIgnoreCase))
                {
                    string cand = Path.Combine(mpRoot, want);
                    if (Directory.Exists(cand) && HasSessionDirs(cand))
                    {
                        via = "pointer";
                        Plugin.Logger.LogWarning($"[MPSave] CARRY-FORWARD: source '{want}' named by our own store pointer.");
                        return cand;
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] carry-forward: store pointer unreadable ({ex.Message}) — continuing with the version rules."); }

            // 2 ── vanilla's walk
            try
            {
                for (int num = NativeVersionInt() - 1; num > 0; num--)
                {
                    string cand = Path.Combine(mpRoot, NativeVersionFolderName(num));
                    if (string.Equals(Path.GetFileName(cand), current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(cand) && HasSessionDirs(cand)) { via = "walk"; return cand; }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[MPSave] carry-forward: the native version walk is unavailable ({ex.Message}) — falling back to the folder names on disk.");
            }

            // 3 / 4 ── whatever is actually on disk
            string best = null, bestName = ""; int bMaj = -1, bMin = -1;
            string newest = null; DateTime newestAt = DateTime.MinValue;
            try
            {
                foreach (var dir in Directory.GetDirectories(mpRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!HasSessionDirs(dir)) continue;
                    candidates++;
                    var at = Directory.GetLastWriteTimeUtc(dir);
                    if (at > newestAt) { newestAt = at; newest = dir; }
                    // On a TIE, prefer the SHORTER name. Two folders can rank equal only when one is a
                    // decorated duplicate ("EA 0.11 - Copy", "EA 0.11 backup") — decoration only ever
                    // lengthens, so the shorter name is the real store. Without this the winner would be
                    // whichever the filesystem happened to enumerate first.
                    if (TryVersionRank(name, out int maj, out int min)
                        && (maj > bMaj || (maj == bMaj && min > bMin)
                            || (maj == bMaj && min == bMin && best != null && name.Length < bestName.Length)))
                    { bMaj = maj; bMin = min; best = dir; bestName = name; }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] carry-forward: sibling scan failed ({ex.Message}) — nothing carried."); }

            if (best != null)
            {
                via = "rank";
                Plugin.Logger.LogWarning($"[MPSave] CARRY-FORWARD RESCUE: the version walk named no previous store, so the highest-NUMBERED of {candidates} folder(s) on disk was chosen — '{bestName}' ({bMaj}.{bMin}). Expected when the game changes its save-folder naming (e.g. leaving Early Access). Nothing was deleted; the old folder is untouched. REPORT THIS LOG so the walk can be taught the new scheme.");
                return best;
            }
            if (newest != null)
            {
                via = "mtime";
                Plugin.Logger.LogError($"[MPSave] CARRY-FORWARD LAST RESORT: no folder under '{mpRoot}' has a parseable version number, so the most recently MODIFIED of {candidates} was chosen — '{Path.GetFileName(newest)}'. This is a GUESS: a folder's timestamp tracks when a world was added or removed, not when one was last played. Nothing was deleted. If the picker shows the wrong saves, delete the carry record in the new version folder and relaunch. REPORT THIS LOG.");
                return newest;
            }
            return null;   // nothing on disk to carry — a genuinely fresh install, silently
        }

        /// <summary>Rank a version folder by the NUMBER in its name: "EA 0.11" -&gt; (0,11), "1.0" -&gt;
        /// (1,0). Reads the first digits.digits pair and ignores the prefix entirely — which is the
        /// point: historical folders keep their old names whatever the current one ends up called.</summary>
        private static bool TryVersionRank(string name, out int major, out int minor)
        {
            major = -1; minor = -1;
            if (string.IsNullOrEmpty(name)) return false;
            for (int d = name.IndexOf('.'); d > 0 && d < name.Length - 1; d = name.IndexOf('.', d + 1))
            {
                int a = d; while (a > 0 && char.IsDigit(name[a - 1])) a--;
                if (a == d) continue;                        // no digits before the dot
                int b = d + 1; while (b < name.Length && char.IsDigit(name[b])) b++;
                if (b == d + 1) continue;                    // none after it
                if (int.TryParse(name.Substring(a, d - a), out major) &&
                    int.TryParse(name.Substring(d + 1, b - d - 1), out minor)) return true;
                major = -1; minor = -1;
            }
            return false;
        }

        private static string ReadPointer(string mpRoot)
        {
            if (string.IsNullOrEmpty(mpRoot)) return "";
            string p = Path.Combine(mpRoot, PointerName);
            if (!File.Exists(p)) return "";
            var sp = Newtonsoft.Json.JsonConvert.DeserializeObject<StorePointer>(File.ReadAllText(p));
            return sp?.Version ?? "";
        }

        /// <summary>Refresh the breadcrumb. Best-effort BY DESIGN: it is an optimisation, never a
        /// dependency — every rule below it still works when this file is missing, stale or corrupt.</summary>
        private static void WritePointer(string mpRoot, string version)
        {
            try
            {
                if (string.IsNullOrEmpty(mpRoot) || string.IsNullOrEmpty(version) || !Directory.Exists(mpRoot)) return;
                if (string.Equals(ReadPointer(mpRoot), version, StringComparison.OrdinalIgnoreCase)) return;   // unchanged — no write
                File.WriteAllText(Path.Combine(mpRoot, PointerName), Newtonsoft.Json.JsonConvert.SerializeObject(
                    new StorePointer { Version = version, AtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
                Plugin.Logger.LogInfo($"[MPSave] store pointer -> '{version}'.");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[MPSave] carry-forward: could not refresh the store pointer ({ex.Message}) — harmless, the version rules still apply."); }
        }

        /// <summary>Any non-"_" subdirectory = the store has session/playthrough content.</summary>
        private static bool HasSessionDirs(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return false;
                foreach (var d in Directory.GetDirectories(root))
                    if (!Path.GetFileName(d).StartsWith("_")) return true;
            }
            catch { }
            return false;
        }

        private static (int copied, int skipped, long bytes) CopyTree(string source, string target, bool atRoot = false)
        {
            int copied = 0, skipped = 0; long bytes = 0;
            Directory.CreateDirectory(target);
            foreach (var f in Directory.GetFiles(source))
            {
                string fname = Path.GetFileName(f);
                // Root-level exclusions: the source's own carry record (ours is already in
                // place) and the crash-resumable migration journal (its rename paths belong
                // to the OLD store).
                if (atRoot && (string.Equals(fname, SkipJournal, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(fname, RecordName, StringComparison.OrdinalIgnoreCase))) continue;
                string dst = Path.Combine(target, fname);
                if (File.Exists(dst)) { skipped++; continue; }   // vanilla semantics: never overwrite (and only whole files exist)
                string tmp = dst + ".carrytmp";
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                File.Copy(f, tmp);
                File.Move(tmp, dst);   // rename = whole-file appearance; a torn temp never shadows a final
                copied++; try { bytes += new FileInfo(dst).Length; } catch { }
            }
            foreach (var d in Directory.GetDirectories(source))
            {
                var r = CopyTree(d, Path.Combine(target, Path.GetFileName(d)));
                copied += r.copied; skipped += r.skipped; bytes += r.bytes;
            }
            return (copied, skipped, bytes);
        }
    }
}
