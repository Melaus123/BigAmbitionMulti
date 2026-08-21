using System;
using System.IO;
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
        private static bool _ran;
        internal static volatile bool InProgress;

        /// <summary>TRUE until this launch's carry decision is made AND any launched copy has
        /// finished — the migrator must not run concurrently with the copy's writes.</summary>
        internal static bool Busy => !_ran || InProgress;

        private class CarryRecord
        {
            public string Source  { get; set; } = "";
            public bool   Finished { get; set; }
        }

        /// <summary>Called every frame from the menu canvas (main thread) — the check runs
        /// once per launch, on the first frame the version folder resolves; the copy, when
        /// needed, runs on a background thread (v2 source) or inline (flat source). Cheap
        /// no-op afterwards.</summary>
        public static void RunIfNeeded()
        {
            if (_ran) return;
            string root;
            try { root = MPSaveManager.MpVersionFolder(); } catch { return; }
            if (string.IsNullOrEmpty(root)) return;   // version cache not warm yet — retry next frame
            _ran = true;
            try
            {
                string recPath = Path.Combine(root, RecordName);
                CarryRecord rec = null;
                try { if (File.Exists(recPath)) rec = Newtonsoft.Json.JsonConvert.DeserializeObject<CarryRecord>(File.ReadAllText(recPath)); } catch { }

                string source;
                if (rec != null)
                {
                    if (rec.Finished) return;   // carried in a prior launch — done forever
                    source = rec.Source;        // interrupted copy — RESUME (skip-existing)
                    if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
                    { Plugin.Logger.LogError($"[MPSave] CARRY-FORWARD: resume record points at a missing source ('{source}') — abandoning the resume; report this log."); return; }
                    Plugin.Logger.LogWarning($"[MPSave] CARRY-FORWARD: resuming an interrupted copy from '{Path.GetFileName(source)}' (skip-existing).");
                }
                else
                {
                    if (HasSessionDirs(root)) return;   // populated store, no record → born here; nothing to carry
                    string mpRoot = Directory.GetParent(root.TrimEnd('/', '\\'))?.FullName;
                    if (string.IsNullOrEmpty(mpRoot) || !Directory.Exists(mpRoot)) return;
                    string current = Path.GetFileName(root.TrimEnd('/', '\\'));
                    source = FindPreviousStore(mpRoot, current);
                    if (source == null) return;         // fresh install — no previous store
                    Directory.CreateDirectory(root);
                    File.WriteAllText(recPath, Newtonsoft.Json.JsonConvert.SerializeObject(new CarryRecord { Source = source, Finished = false }));
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
                string src = source;
                Action work = () =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var (copied, skipped, bytes) = CopyTree(src, root, atRoot: true);
                        try { File.WriteAllText(recPath, Newtonsoft.Json.JsonConvert.SerializeObject(new CarryRecord { Source = src, Finished = true })); }
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

        /// <summary>Vanilla's source rule (SaveGameCompatibilityHelper.cs:7-19): walk version
        /// numbers downward and take the first store that exists with content. The newest-
        /// folder-mtime scan is only the fallback when the native statics are unavailable.</summary>
        private static string FindPreviousStore(string mpRoot, string current)
        {
            try
            {
                for (int num = MainMenuController.currentVersion.version - 1; num > 0; num--)
                {
                    string cand = Path.Combine(mpRoot, GameVersion.GetEarlyAccessVersionString(num));
                    if (string.Equals(Path.GetFileName(cand), current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(cand) && HasSessionDirs(cand)) return cand;
                }
                return null;   // the walk worked and found nothing — fresh install
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[MPSave] carry-forward: native version walk unavailable ({ex.Message}) — falling back to the newest sibling folder.");
            }
            string source = null; DateTime sourceAt = DateTime.MinValue;
            try
            {
                foreach (var dir in Directory.GetDirectories(mpRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("_") || string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!HasSessionDirs(dir)) continue;
                    var at = Directory.GetLastWriteTimeUtc(dir);
                    if (at > sourceAt) { sourceAt = at; source = dir; }
                }
            }
            catch { }
            return source;
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
