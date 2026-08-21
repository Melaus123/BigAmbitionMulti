using System;
using System.IO;
using System.Threading.Tasks;

namespace BigAmbitionsMP
{
    /// <summary>Game-version carry-forward for the MP save store (user-approved 2026-08-21).
    /// The store lives under SaveGames/_BAMP_MP/&lt;GameVersion&gt;/ — after a game update the
    /// mod looks in the NEW version's folder, which doesn't exist yet, and every MP save
    /// silently vanishes from the picker (audit #14; vanilla's load window solves this with
    /// an upgrade prompt + copy, which our clone strips).
    ///
    /// Vanilla-parity semantics, automatic trigger:
    ///  * COPY, never move — vanilla's CopySaveGamesBetweenPreviousAndCurrentVersion is a
    ///    recursive skip-existing copy (SaveGameCompatibilityHelper.cs, read 2026-08-21).
    ///    The old version's store stays untouched: a game rollback still finds its saves,
    ///    and a failed copy loses nothing. Players may delete the old folder by hand once
    ///    the new version is proven (release notes name it).
    ///  * Runs at most once per launch, only when the CURRENT version's store holds no
    ///    session folders and a previous version's store does.
    ///  * Source discovery: newest-written sibling version folder under _BAMP_MP. The root
    ///    is exclusively ours, so a sibling scan replaces vanilla's version-number walk
    ///    and needs no native statics (poll-thread-safe file IO only).
    ///  * The copy runs on a background thread — a large store must not stall the menu.
    ///    Root files copy before session subfolders, so the storeformat marker (v2 layout)
    ///    arrives first; the crash-resumable migration journal is NOT copied (its rename
    ///    paths belong to the old store).
    ///  * Save-format conversion is NOT our job: the game modernizes old saves at load
    ///    (SaveGameCompatibilityFixes ladder, SaveGameManager.cs:312), and MP saves load
    ///    through that same native door.</summary>
    internal static class MPStoreCarryForward
    {
        private const string SkipRootFile = "_migrate_journal.json";
        private static bool _ran;
        internal static volatile bool InProgress;

        /// <summary>Called every frame from the menu canvas (main thread) — the check runs
        /// once per launch, on the first frame the version folder resolves; the copy, when
        /// needed, runs on a background thread. Cheap no-op afterwards.</summary>
        public static void RunIfNeeded()
        {
            if (_ran) return;
            string root;
            try { root = MPSaveManager.MpVersionFolder(); } catch { return; }
            if (string.IsNullOrEmpty(root)) return;   // version cache not warm yet — retry next frame
            _ran = true;
            try
            {
                if (HasSessionDirs(root)) return;   // current store populated — nothing to do
                string mpRoot = Directory.GetParent(root.TrimEnd('/', '\\'))?.FullName;
                if (string.IsNullOrEmpty(mpRoot) || !Directory.Exists(mpRoot)) return;
                string current = Path.GetFileName(root.TrimEnd('/', '\\'));
                string source = null; DateTime sourceAt = DateTime.MinValue;
                foreach (var dir in Directory.GetDirectories(mpRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("_") || string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!HasSessionDirs(dir)) continue;
                    var at = Directory.GetLastWriteTimeUtc(dir);
                    if (at > sourceAt) { sourceAt = at; source = dir; }
                }
                if (source == null) return;   // fresh install — no previous store to carry
                InProgress = true;
                string srcName = Path.GetFileName(source);
                string src = source;
                Plugin.Logger.LogWarning($"[MPSave] CARRY-FORWARD: current store '{current}' is empty — copying the '{srcName}' MP store forward (originals untouched; '{src}' can be deleted by hand once the new version is proven).");
                Task.Run(() =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var (copied, skipped, bytes) = CopyTree(src, root, atRoot: true);
                        Plugin.Logger.LogWarning($"[MPSave] CARRY-FORWARD done: {copied} file(s), {bytes / (1024.0 * 1024.0):F1} MB in {sw.ElapsedMilliseconds} ms ({skipped} already present). Old store kept at '{src}'.");
                    }
                    catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] CARRY-FORWARD FAILED (old store untouched; partial copy is skip-existing-resumable next launch): {ex}"); }
                    finally { InProgress = false; }
                });
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[MPSave] carry-forward check failed: {ex.Message}"); InProgress = false; }
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
                if (atRoot && string.Equals(fname, SkipRootFile, StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.Combine(target, fname);
                if (File.Exists(dst)) { skipped++; continue; }   // vanilla semantics: never overwrite
                File.Copy(f, dst);
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
