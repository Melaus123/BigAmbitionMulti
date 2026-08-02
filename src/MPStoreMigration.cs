using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BigAmbitionsMP
{
    /// <summary>
    /// Store v2 M2 — one-time migration of the flat (v1) MP save store to the
    /// playthrough-scoped (v2) layout. Design: .modding/03-systems/save-store-v2.md.
    ///
    /// REVISED 2026-08-01 (user review): MOVE-based, not copy-based. A same-volume
    /// rename never rewrites file contents, so content corruption is structurally
    /// impossible, the migration is near-instant regardless of store size, and no
    /// disk is duplicated (the original copy+SHA-256+full-backup design doubled the
    /// store — 319 MB on the rig — for safety a rename simply doesn't need).
    ///
    /// Safety model:
    ///  * PLAN on disk first: '_migrate_journal.json' lists every from→to rename
    ///    before anything moves. It is simultaneously the crash-resume worklist and,
    ///    once finalized as '_undo_map.json', the permanent kilobyte-sized undo
    ///    record (replay it backwards to restore the flat layout).
    ///  * ATOMIC adoption: the storeformat marker is written after the journal and
    ///    before the renames. Crash before the marker → flat store fully intact,
    ///    stale journal discarded and replanned next launch. Crash after → the store
    ///    is v2 with some sessions still at the flat root; ExecuteJournal resumes the
    ///    remaining renames next launch before anything reads the store. Each rename
    ///    is atomic (same volume), so no intermediate state can damage a session.
    ///  * Family grouping (decision C, unchanged + rehearsal-proven): a session keeps
    ///    its own manifest pid; pid-less sessions adopt their name-family's pid; a
    ///    wholly pid-less family gets ONE minted pid. Split-when-uncertain.
    ///  * Folder timestamps: renames preserve them by nature (the picker dates
    ///    manifest-less recover sessions by folder time — the copy design needed an
    ///    explicit fix for this; moves get it for free).
    /// </summary>
    internal static class MPStoreMigration
    {
        private const string JournalName = "_migrate_journal.json";
        private const string UndoMapName = "_undo_map.json";
        private static bool _ran;

        /// <summary>Called every frame from the menu canvas (main thread) — runs the
        /// actual work at most once per launch, on the first frame the version folder
        /// resolves. Cheap no-op afterwards.</summary>
        public static void RunIfNeeded()
        {
            if (_ran) return;
            string root;
            try { root = MPSaveManager.MpVersionFolder(); } catch { return; }
            if (string.IsNullOrEmpty(root)) return;   // version cache not warm yet — retry next frame
            _ran = true;
            try
            {
                if (!Directory.Exists(root)) return;  // no store at all — lazy v2 birth covers it
                string marker = Path.Combine(root, MPSaveManager.StoreFormatMarkerName);
                if (File.Exists(marker))
                {
                    // Already v2 — a crash may have interrupted the renames. Resume is
                    // idempotent and instant when nothing is pending.
                    ResumePendingJournal(root);
                    return;
                }
                if (MPSaveManager.StoreFormat() != 1) return;   // empty store: probe just birthed v2
                Migrate(root, marker);
            }
            catch (Exception ex)
            {
                // Decision F: loud, and fail SAFE — pre-marker failures leave the flat
                // store fully intact and the mod keeps running in v1 mode as before.
                Plugin.Logger.LogError($"[MPSave] STORE MIGRATION FAILED — see above; store left as-is. {ex}");
                try { MPCanvasUI.PostLobbyNotice("Save-storage upgrade failed — old layout still in use. Please send a bug report."); } catch { }
            }
        }

        private sealed class MoveEntry { public string From = "", To = ""; }

        private static void Migrate(string root, string marker)
        {
            // A journal without a marker = a previous attempt died BEFORE adoption.
            // The flat store is untouched; discard the stale plan and replan fresh.
            string journalPath = Path.Combine(root, JournalName);
            if (File.Exists(journalPath))
            {
                Plugin.Logger.LogWarning("[MPSave] Migration: discarding a stale pre-adoption journal (flat store was never touched).");
                File.Delete(journalPath);
            }

            // 1. PLAN — every non-"_" directory in the flat root is a session.
            var sessions = new List<(string Name, string Dir, string Base)>();
            foreach (var dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("_")) continue;
                sessions.Add((name, dir, MPSaveManager.StripToBase(name)));
            }
            if (sessions.Count == 0)
            {
                WriteMarker(marker, 0);
                Plugin.Logger.LogInfo("[MPSave] Migration: flat store held no sessions — marked v2.");
                return;
            }

            // Family pids: own manifest pid wins; else the family's newest stamped pid;
            // else one minted pid per name-family (split-when-uncertain).
            var famPid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var famAt  = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var ownPid = new Dictionary<string, string>();
            foreach (var s in sessions)
            {
                var m = MPSaveManager.ReadManifestAt(s.Dir);
                string pid = m?.PlaythroughId ?? "";
                long at = m?.SavedAtUnix ?? 0;
                ownPid[s.Name] = pid;
                if (pid.Length > 0 && (!famAt.TryGetValue(s.Base, out var best) || at > best))
                {
                    famPid[s.Base] = pid; famAt[s.Base] = at;
                }
            }
            var moves = new List<MoveEntry>();
            foreach (var s in sessions)
            {
                string pid = ownPid[s.Name];
                if (pid.Length == 0 && famPid.TryGetValue(s.Base, out var fp)) pid = fp;
                if (pid.Length == 0)
                {
                    string minted = Guid.NewGuid().ToString("N");
                    famPid[s.Base] = minted; pid = minted;
                    Plugin.Logger.LogInfo($"[MPSave] Migration: legacy family '{s.Base}' has no stored identity — minted {minted}.");
                }
                moves.Add(new MoveEntry { From = s.Name, To = pid + "/" + s.Name });
            }
            int playthroughs = moves.Select(m => m.To.Split('/')[0]).Distinct().Count();

            // 2. JOURNAL on disk — the complete worklist, then the ATOMIC adoption.
            WriteJournal(journalPath, moves, playthroughs, completed: false);
            WriteMarker(marker, moves.Count);

            // 3. EXECUTE — instant same-volume renames — and finalize the journal
            //    into the permanent undo map.
            ExecuteJournal(root, journalPath, moves, playthroughs);

            MPSaveManager.ResetPidCache();
            Plugin.Logger.LogInfo($"[MPSave] Store migration COMPLETE: {moves.Count} session(s) renamed into {playthroughs} playthrough folder(s); undo map kept as {UndoMapName}.");
            try { MPCanvasUI.PostLobbyNotice($"Save storage upgraded — {playthroughs} world(s) reorganized (files renamed in place, nothing copied)."); } catch { }
        }

        /// <summary>Run (or resume) the journal's renames. Idempotent: an entry whose
        /// source is gone and destination exists is already done; both-exist or
        /// neither-exist are loud defect signals that never destroy anything.</summary>
        private static void ExecuteJournal(string root, string journalPath, List<MoveEntry> moves, int playthroughs)
        {
            int done = 0, pending = 0, anomalies = 0;
            foreach (var mv in moves)
            {
                string src = Path.Combine(root, mv.From);
                string dst = Path.Combine(root, mv.To.Replace('/', Path.DirectorySeparatorChar));
                bool s = Directory.Exists(src), d = Directory.Exists(dst);
                if (!s && d) { done++; continue; }                    // completed earlier
                if (s && !d)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    Directory.Move(src, dst);                          // atomic rename
                    done++; pending++;
                    continue;
                }
                anomalies++;
                Plugin.Logger.LogWarning(s
                    ? $"[MPSave] Migration anomaly: BOTH '{mv.From}' and '{mv.To}' exist — leaving both untouched; report this."
                    : $"[MPSave] Migration anomaly: NEITHER '{mv.From}' nor '{mv.To}' exists — session missing; report this.");
            }
            if (anomalies == 0)
            {
                // Finalize: the journal becomes the permanent, kilobyte-sized undo map.
                string undo = Path.Combine(root, UndoMapName);
                try { if (File.Exists(undo)) File.Delete(undo); } catch { }
                WriteJournal(undo, moves, playthroughs, completed: true);
                try { File.Delete(journalPath); } catch { }
            }
            else
            {
                Plugin.Logger.LogError($"[MPSave] Migration: {anomalies} anomalies — journal kept for the next launch's resume; nothing was deleted.");
            }
            if (pending > 0 && done == moves.Count)
                Plugin.Logger.LogInfo($"[MPSave] Migration renames settled: {done}/{moves.Count}.");
        }

        /// <summary>Marker present but a journal remains: a crash interrupted the
        /// renames — finish them before anything reads the store.</summary>
        private static void ResumePendingJournal(string root)
        {
            string journalPath = Path.Combine(root, JournalName);
            if (!File.Exists(journalPath)) return;
            Plugin.Logger.LogWarning("[MPSave] Migration: resuming interrupted renames from the journal.");
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(journalPath));
            var moves = parsed["moves"]?.Select(t => new MoveEntry
            {
                From = t["From"]?.ToString() ?? t["from"]?.ToString() ?? "",
                To   = t["To"]?.ToString()   ?? t["to"]?.ToString()   ?? "",
            }).Where(m => m.From.Length > 0 && m.To.Length > 0).ToList() ?? new List<MoveEntry>();
            int playthroughs = moves.Select(m => m.To.Split('/')[0]).Distinct().Count();
            ExecuteJournal(root, journalPath, moves, playthroughs);
            MPSaveManager.ResetPidCache();
        }

        private static void WriteJournal(string path, List<MoveEntry> moves, int playthroughs, bool completed)
        {
            var doc = new
            {
                purpose = "BAMP save-store v2 migration " + (completed ? "undo map — replay 'To'→'From' to restore the flat layout" : "journal (in progress)"),
                createdUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                completed,
                playthroughs,
                moves,
            };
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(doc, Newtonsoft.Json.Formatting.Indented));
        }

        private static void WriteMarker(string marker, int sessions)
        {
            File.WriteAllText(marker,
                "{\"format\":2,\"born\":\"migrated\",\"migratedAtUnix\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                + ",\"sessions\":" + sessions + "}");
        }
    }
}
